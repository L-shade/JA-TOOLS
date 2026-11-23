using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using JAEsquadrias.Core;
using JAEsquadrias.Core.Paineis;
using RevitApp = Autodesk.Revit.ApplicationServices.Application;
using Settings = JAEsquadrias.Core.Settings;

namespace JAEsquadrias.Engine
{
    public sealed class PainelResult
    {
        public Family Family;
        public FamilySymbol Symbol;
        public string FamilyName;
        public string RfaPath;
        public int Solidos;
        public List<string> Warnings = new List<string>();
    }

    /// <summary>A JA curtain panel family (one size) found in the project.</summary>
    public sealed class PainelNoProjeto
    {
        public FamilySymbol Symbol;
        public PainelConfig Config;
        public int Instancias;
        public string Codigo => Config?.Codigo ?? Symbol?.Name;
    }

    /// <summary>
    /// Curtain wall panels that are not doors or windows, from a <see cref="PainelConfig"/>: a
    /// family made from the Revit "Curtain Wall Panel" template (category Painéis de parede
    /// cortina) for one panel size; every solid of <see cref="PainelGeom"/> becomes an extrusion
    /// with its material parameter and detail level. The configuration (with the size) is kept
    /// in JA_Config, JA_Familia = "JA Painel", Marca de tipo = code.
    /// </summary>
    public static class PainelGenerator
    {
        private const double Mm = 1.0 / 304.8;
        public const string Marca = "JA Painel";

        public static PainelResult Generate(RevitApp app, Document project, PainelConfig cfg, Settings settings, string familyName = null)
        {
            cfg = cfg.Copiar();
            string template = TemplateLocator.FindCurtainPanel(app, settings, out string searched);
            if (template == null)
                throw new InvalidOperationException("Não encontrei o modelo de família de painel de parede cortina (.rft) do Revit (ex.: “Metric Curtain Wall Panel.rft”).\n" +
                    "Procurado em: " + (string.IsNullOrEmpty(searched) ? "(nenhuma pasta de modelos)" : searched) + "\n\nIndique o arquivo em Configurações › Modelo de painel de parede cortina.");
            var res = new PainelResult
            {
                FamilyName = FamilyGenerator.SafeName(string.IsNullOrWhiteSpace(familyName) ? cfg.NomeFamilia(cfg.LarguraMm, cfg.AlturaMm) : familyName)
            };
            string folder = Path.Combine(settings.PastaFamilias, "Paineis");
            Directory.CreateDirectory(folder);
            res.RfaPath = Path.Combine(folder, res.FamilyName + ".rfa");

            Logger.Info($"[JA Famílias] Gerando painel {res.FamilyName} a partir de {template}");
            Document fam = app.NewFamilyDocument(template);
            if (fam == null) throw new InvalidOperationException("O Revit não criou o documento de família a partir de " + template);
            try
            {
                SharedParams.WithDefinitions(app, defs =>
                {
                    using (var t = new Transaction(fam, "JA Famílias – painel"))
                    {
                        t.Start();
                        var opts = t.GetFailureHandlingOptions();
                        opts.SetFailuresPreprocessor(new ConstraintResolver());
                        t.SetFailureHandlingOptions(opts);
                        new Construtor(fam, cfg, settings, defs, res).Run();
                        t.Commit();
                    }
                });
                fam.SaveAs(res.RfaPath, new SaveAsOptions { OverwriteExistingFile = true, MaximumBackups = 1 });
                res.Family = fam.LoadFamily(project, new OverwriteLoadOptions());
            }
            finally
            {
                try { fam.Close(false); } catch { }
            }
            if (res.Family == null)
                res.Family = new FilteredElementCollector(project).OfClass(typeof(Family)).Cast<Family>().FirstOrDefault(f => f.Name == res.FamilyName);
            if (res.Family == null) throw new InvalidOperationException("O painel foi gerado (" + res.RfaPath + ") mas não foi carregado no projeto.");
            res.Symbol = res.Family.GetFamilySymbolIds().Select(id => project.GetElement(id)).OfType<FamilySymbol>()
                .OrderByDescending(s => s.Name == cfg.Codigo).FirstOrDefault();
            if (res.Symbol != null && !res.Symbol.IsActive)
            {
                using (var t = new Transaction(project, "JA Famílias – ativar painel"))
                {
                    t.Start();
                    res.Symbol.Activate();
                    t.Commit();
                }
            }
            return res;
        }

        // ================================================================== project
        public static PainelConfig ConfigOf(Element e, out FamilySymbol symbol)
        {
            symbol = null;
            try { symbol = (e as FamilyInstance)?.Symbol ?? e as FamilySymbol; } catch { }
            if (symbol == null) return null;
            if ((symbol.Category?.Id.Value ?? 0) != (long)BuiltInCategory.OST_CurtainWallPanels) { symbol = null; return null; }
            if (SharedParams.GetString(symbol, SharedParams.Familia) != Marca) { symbol = null; return null; }
            var c = PainelConfig.FromJson(SharedParams.GetString(symbol, SharedParams.Config));
            if (c == null) symbol = null;
            return c;
        }

        public static List<PainelNoProjeto> All(Document doc)
        {
            var list = new List<PainelNoProjeto>();
            foreach (FamilySymbol s in new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_CurtainWallPanels).Cast<FamilySymbol>())
            {
                PainelConfig c = ConfigOf(s, out _);
                if (c != null) list.Add(new PainelNoProjeto { Symbol = s, Config = c });
            }
            if (list.Count > 0)
            {
                var counts = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_CurtainWallPanels).WhereElementIsNotElementType()
                    .GroupBy(i => i.GetTypeId().Value).ToDictionary(g => g.Key, g => g.Count());
                foreach (var p in list) p.Instancias = counts.TryGetValue(p.Symbol.Id.Value, out int n) ? n : 0;
            }
            return list.OrderBy(p => p.Codigo, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Config.LarguraMm).ThenBy(p => p.Config.AlturaMm).ToList();
        }

        /// <summary>Next free code: PN01, PN02...</summary>
        public static string NextCode(IEnumerable<string> used)
        {
            var set = new HashSet<string>(used ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < 1000; i++)
            {
                string c = "PN" + i.ToString("00", CultureInfo.InvariantCulture);
                if (!set.Contains(c)) return c;
            }
            return "PN" + Guid.NewGuid().ToString("N").Substring(0, 4);
        }

        // ================================================================== family
        private sealed class Construtor
        {
            private readonly Document _fam;
            private readonly FamilyManager _fm;
            private readonly PainelConfig _c;
            private readonly Settings _s;
            private readonly Dictionary<Guid, ExternalDefinition> _defs;
            private readonly PainelResult _res;
            private XYZ _u, _n;
            private double _xLeft, _zBot;
            private readonly Dictionary<SlotPainel, FamilyParameter> _mat = new Dictionary<SlotPainel, FamilyParameter>();

            public Construtor(Document fam, PainelConfig c, Settings s, Dictionary<Guid, ExternalDefinition> defs, PainelResult res)
            {
                _fam = fam; _fm = fam.FamilyManager; _c = c; _s = s; _defs = defs; _res = res;
            }

            public void Run()
            {
                if (_fm.CurrentType == null) _fm.NewType(_c.Codigo);
                else { try { _fm.RenameCurrentType(_c.Codigo); } catch (Exception ex) { Warn("Tipo não renomeado: " + ex.Message); } }

                // size: only a hint (the curtain grid drives the panel in the project), but it puts
                // the template's reference planes where the panel will be
                SetLength(Find(new[] { BuiltInParameter.FAMILY_WIDTH_PARAM, BuiltInParameter.CURTAIN_WALL_PANELS_WIDTH, BuiltInParameter.GENERIC_WIDTH }, new[] { "Width", "Largura" }), _c.LarguraMm, "Largura");
                SetLength(Find(new[] { BuiltInParameter.FAMILY_HEIGHT_PARAM, BuiltInParameter.CURTAIN_WALL_PANELS_HEIGHT, BuiltInParameter.GENERIC_HEIGHT }, new[] { "Height", "Altura" }), _c.AlturaMm, "Altura");
                try { _fam.Regenerate(); } catch { }
                Quadro();

                PainelGeom g = _c.Build();
                foreach (string a in g.Avisos) Warn(a);
                int falhas = 0;
                foreach (SolidoPainel s in g.Solidos)
                {
                    try
                    {
                        Extrusion ex = Extrusao(s);
                        if (ex == null) continue;
                        _res.Solidos++;
                        FamilyParameter fp = Material(s.Slot);
                        if (fp != null)
                        {
                            Parameter mp = ex.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
                            if (mp != null) { try { _fm.AssociateElementParameterToFamilyParameter(mp, fp); } catch { } }
                        }
                        if (!(s.Coarse && s.Medium && s.Fine))
                            ex.SetVisibility(new FamilyElementVisibility(FamilyElementVisibilityType.Model) { IsShownInCoarse = s.Coarse, IsShownInMedium = s.Medium, IsShownInFine = s.Fine });
                    }
                    catch (Exception e)
                    {
                        falhas++;
                        Logger.Warn($"[JA Famílias] Painel: sólido {s.Papel} não criado: {e.Message}");
                    }
                }
                if (_res.Solidos == 0) throw new InvalidOperationException("O Revit não aceitou nenhuma peça do painel (detalhes no registro).");
                if (falhas > 0) Warn($"{falhas} peça(s) do painel não puderam ser modeladas (detalhes no registro).");
                Dados(g);
            }

            /// <summary>
            /// Frame of the template: X along the panel (right seen from outside), Y up, the grid
            /// plane at the family's Y = 0 with the exterior towards −Y (Revit wall-hosted
            /// convention; "Inverter interior / exterior" flips it). Left edge and bottom from the
            /// reference planes that are W / H apart, else centred / on the level.
            /// </summary>
            private void Quadro()
            {
                _n = _c.InverterLado ? new XYZ(0, 1, 0) : new XYZ(0, -1, 0);
                _u = XYZ.BasisZ.CrossProduct(_n).Normalize();
                double w = _c.LarguraMm * Mm, h = _c.AlturaMm * Mm;
                var xs = new List<double>();
                var zs = new List<double> { 0 };
                foreach (ReferencePlane rp in new FilteredElementCollector(_fam).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>())
                {
                    try
                    {
                        XYZ nn = rp.Normal;
                        if (Math.Abs(nn.Z) > 0.99) zs.Add(rp.BubbleEnd.Z);
                        else if (Math.Abs(nn.DotProduct(_u)) > 0.99) xs.Add(rp.BubbleEnd.DotProduct(_u));
                    }
                    catch { }
                }
                double? left = null, bottom = null;
                foreach (double a in xs) foreach (double b in xs) if (Math.Abs(b - a - w) < 2 * Mm) left = a;
                foreach (double a in zs) foreach (double b in zs)
                        if (Math.Abs(b - a - h) < 2 * Mm && (!bottom.HasValue || Math.Abs(a) < Math.Abs(bottom.Value))) bottom = a;
                _xLeft = left ?? -w / 2;
                _zBot = bottom ?? 0;
                Logger.Info($"[JA Famílias] Painel de parede cortina: esquerda {_xLeft / Mm:0} mm, base {_zBot / Mm:0} mm (planos {(left.HasValue ? "achados" : "não achados")}).");
            }

            private XYZ Q(double x, double y, double p) => _u * (_xLeft + x * Mm) + XYZ.BasisZ * (_zBot + y * Mm) + _n * (p * Mm);

            private Extrusion Extrusao(SolidoPainel s)
            {
                double len = (s.E1 - s.E0) * Mm;
                if (len < 0.5 * Mm) return null;
                Func<P2, XYZ> pt;
                XYZ normal;
                switch (s.Eixo)
                {
                    case EixoPainel.Largura: pt = q => Q(s.E0, q.Y, q.X); normal = _u; break;      // profile (p, y)
                    case EixoPainel.Altura: pt = q => Q(q.X, s.E0, q.Y); normal = XYZ.BasisZ; break; // profile (x, p)
                    default: pt = q => Q(q.X, q.Y, s.E0); normal = _n; break;                        // profile (x, y)
                }
                var arr = new CurveArrArray();
                foreach (var raw in s.Loops)
                {
                    var loop = Limpar(raw);
                    if (loop.Count < 3) continue;
                    var ca = new CurveArray();
                    for (int i = 0; i < loop.Count; i++) ca.Append(Line.CreateBound(pt(loop[i]), pt(loop[(i + 1) % loop.Count])));
                    arr.Append(ca);
                }
                if (arr.Size == 0) return null;
                SketchPlane sp = SketchPlane.Create(_fam, Plane.CreateByNormalAndOrigin(normal, pt(s.Loops[0][0])));
                return _fam.FamilyCreate.NewExtrusion(true, arr, sp, len);
            }

            /// <summary>Points closer than 1 mm to the previous one are dropped (Revit rejects tiny edges).</summary>
            private static List<P2> Limpar(List<P2> loop)
            {
                var r = new List<P2>();
                foreach (var p in loop)
                    if (r.Count == 0 || Math.Abs(p.X - r[r.Count - 1].X) + Math.Abs(p.Y - r[r.Count - 1].Y) > 1.0) r.Add(p);
                while (r.Count > 2 && Math.Abs(r[0].X - r[r.Count - 1].X) + Math.Abs(r[0].Y - r[r.Count - 1].Y) <= 1.0) r.RemoveAt(r.Count - 1);
                return r;
            }

            // ------------------------------------------------------------------ materials
            private FamilyParameter Material(SlotPainel slot)
            {
                if (_mat.TryGetValue(slot, out FamilyParameter fp)) return fp;
                string nome, cor; int transp = 0;
                switch (slot)
                {
                    case SlotPainel.Vidro: nome = "Material do vidro"; cor = "BFD8E0"; transp = 70; break;
                    case SlotPainel.Nucleo: nome = "Material do núcleo"; cor = "D9C99A"; break;
                    case SlotPainel.Moldura: nome = "Material da moldura"; cor = _c.CorMolduraHex; break;
                    default: nome = "Material do painel"; cor = _c.CorHex; break;
                }
                string mat = slot == SlotPainel.Vidro ? _c.MaterialVidro : slot == SlotPainel.Nucleo ? _c.MaterialNucleo : slot == SlotPainel.Moldura ? _c.MaterialMoldura : _c.Material;
                try
                {
                    fp = _fm.get_Parameter(nome) ?? _fm.AddParameter(nome, GroupTypeId.Materials, SpecTypeId.Reference.Material, false);
                    ElementId id = CriarMaterial(mat.Trim(), cor, transp);
                    if (id != ElementId.InvalidElementId) _fm.Set(fp, id);
                }
                catch (Exception ex) { Warn($"Parâmetro '{nome}' não criado: {ex.Message}"); fp = null; }
                _mat[slot] = fp;
                return fp;
            }

            private ElementId CriarMaterial(string name, string hex, int transparency)
            {
                try
                {
                    var m = new FilteredElementCollector(_fam).OfClass(typeof(Autodesk.Revit.DB.Material)).Cast<Autodesk.Revit.DB.Material>().FirstOrDefault(x => x.Name == name)
                            ?? _fam.GetElement(Autodesk.Revit.DB.Material.Create(_fam, name)) as Autodesk.Revit.DB.Material;
                    if (m == null) return ElementId.InvalidElementId;
                    m.Color = Cor(hex);
                    m.Transparency = transparency;
                    m.Shininess = transparency > 0 ? 90 : 50;
                    m.Smoothness = transparency > 0 ? 90 : 50;
                    return m.Id;
                }
                catch (Exception ex) { Warn($"Material '{name}' não criado: {ex.Message}"); return ElementId.InvalidElementId; }
            }

            private static Color Cor(string hex)
            {
                hex = (hex ?? "").Trim().TrimStart('#');
                if (hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v))
                    return new Color((byte)(v >> 16), (byte)((v >> 8) & 255), (byte)(v & 255));
                return new Color(180, 180, 180);
            }

            // ------------------------------------------------------------------ data
            private void Dados(PainelGeom g)
            {
                Texto(SharedParams.Config, _c.ToJson());
                Texto(SharedParams.Familia, Marca);
                Texto(SharedParams.Material, _c.Tipo == TipoPainel.Vidro || (_c.Tipo == TipoPainel.Emoldurado && _c.PreenchimentoVidro) ? _c.MaterialVidro : _c.Material);
                Texto(SharedParams.Dimensoes, _c.Dimensoes(_c.LarguraMm, _c.AlturaMm));
                BuiltIn(BuiltInParameter.ALL_MODEL_TYPE_MARK, _c.Codigo);
                BuiltIn(BuiltInParameter.ALL_MODEL_DESCRIPTION, _c.Descricao());
                BuiltIn(BuiltInParameter.ALL_MODEL_MANUFACTURER, _s.Fabricante);
                BuiltIn(BuiltInParameter.ALL_MODEL_MODEL, PainelConfig.NomeTipo(_c.Tipo));
                if (g.Aletas > 0) BuiltIn(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS, g.Aletas + " aletas");
            }

            private void Texto(SharedParams.Def d, string value)
            {
                try
                {
                    FamilyParameter p = null;
                    try { p = _fm.get_Parameter(d.Guid); } catch { }
                    if (p == null && _defs.TryGetValue(d.Guid, out ExternalDefinition def)) p = _fm.AddParameter(def, GroupTypeId.Data, false);
                    if (p != null) _fm.Set(p, value ?? "");
                }
                catch (Exception ex) { Logger.Warn("[JA Famílias] Parâmetro " + d.Name + " no painel: " + ex.Message); }
            }

            private void BuiltIn(BuiltInParameter bip, string value)
            {
                try
                {
                    FamilyParameter p = _fm.get_Parameter(bip);
                    if (p != null) _fm.Set(p, value ?? "");
                }
                catch (Exception ex) { Logger.Warn("[JA Famílias] " + bip + " no painel: " + ex.Message); }
            }

            private FamilyParameter Find(BuiltInParameter[] bips, string[] names) => FamUtil.Find(_fm, bips, names);

            private void SetLength(FamilyParameter p, double mm, string what)
            {
                if (p == null) return;
                try { if (!string.IsNullOrEmpty(p.Formula)) _fm.SetFormula(p, null); _fm.Set(p, mm * Mm); }
                catch (Exception ex) { Logger.Info($"[JA Famílias] {what} do painel controlada pela parede cortina ({ex.Message})."); }
            }

            private void Warn(string w)
            {
                _res.Warnings.Add(w);
                Logger.Warn("[JA Famílias] " + w);
            }
        }
    }
}
