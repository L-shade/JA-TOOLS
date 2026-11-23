using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using JAEsquadrias.Core;
using Settings = JAEsquadrias.Core.Settings;
using RevitApp = Autodesk.Revit.ApplicationServices.Application;

namespace JAEsquadrias.Engine
{
    public sealed class GenerationResult
    {
        public Family Family;
        public FamilySymbol Symbol;
        public string FamilyName;
        public string TypeName;
        public string RfaPath;
        public string TemplatePath;
        public Layout Layout;
        public int Solids;
        /// <summary>True when the family follows any wall thickness and the finishes are instance parameters.</summary>
        public bool Adaptavel;
        /// <summary>The plan lines were measured in the family and are all in place.</summary>
        public bool PlantaConferida;
        /// <summary>Curved chapim: the revolution angle follows the instance parameter "Ângulo do arco" (false = fixed angle).</summary>
        public bool AnguloParametrico;
        public List<string> Warnings = new List<string>();
    }

    /// <summary>
    /// Builds a real Revit window/door family from an <see cref="EsquadriaConfig"/>:
    ///  1. a new family document from the window/door template (it already carries the host
    ///     wall and the opening cut that follows Width/Height);
    ///  2. Width, Height and Default Sill Height set; one type named after the code (J01...);
    ///  3. every solid of the <see cref="Layout"/> becomes an extrusion (frame, sashes, glass,
    ///     louvres, handles, sill), with material parameters and detail-level visibility;
    ///  4. opening symbols as symbolic lines (elevation; plan swing for doors);
    ///  5. shared parameters (JA_*) + Type Mark, Description, Manufacturer, Model;
    ///  6. saved as .rfa in the families folder and loaded into the project (overwriting the
    ///     previous version of the same code, so existing instances update).
    /// </summary>
    public static class FamilyGenerator
    {
        internal const double Mm = 1.0 / 304.8;

        /// <param name="familyName">Edit mode: the existing family's name (so it is reloaded in place).</param>
        /// <param name="typeName">Edit mode: the existing type's name.</param>
        public static GenerationResult Generate(RevitApp app, Document project, EsquadriaConfig cfg, Settings settings,
                                                string familyName = null, string typeName = null)
        {
            cfg = cfg.Clone();
            var res = new GenerationResult();
            string searched;
            string template = cfg.ParedeCortina
                ? TemplateLocator.FindCurtain(app, cfg.Tipo, settings, out searched)
                : TemplateLocator.Find(app, cfg.Tipo, settings, out searched);
            if (template == null && cfg.ParedeCortina)
                throw new InvalidOperationException(
                    "Não encontrei o modelo de família de " + (cfg.IsPorta ? "porta" : "janela") + " para parede cortina (.rft) do Revit.\n" +
                    "Procurado em: " + (string.IsNullOrEmpty(searched) ? "(nenhuma pasta de modelos)" : searched) + "\n\n" +
                    "Ele vem com o Revit (ex.: \"Metric " + (cfg.IsPorta ? "Door" : "Window") + " - Curtain Wall.rft\"); instale os modelos de família do Revit.");
            if (template == null)
                throw new InvalidOperationException(
                    "Não encontrei o modelo de família de " + (cfg.IsPorta ? "porta" : "janela") + " (.rft) do Revit.\n" +
                    "Procurado em: " + (string.IsNullOrEmpty(searched) ? "(nenhuma pasta de modelos)" : searched) + "\n\n" +
                    "Em Configurações, indique o arquivo (ex.: \"Metric " + (cfg.IsPorta ? "Door" : "Window") + ".rft\").");
            res.TemplatePath = template;
            res.FamilyName = SafeName(string.IsNullOrWhiteSpace(familyName) ? cfg.FamilyName(settings.PrefixoFamilia) : familyName);
            res.TypeName = string.IsNullOrWhiteSpace(typeName) ? cfg.Codigo : typeName;
            Directory.CreateDirectory(settings.PastaFamilias);
            res.RfaPath = Path.Combine(settings.PastaFamilias, res.FamilyName + ".rfa");

            Logger.Info($"[JA Famílias] Gerando {res.FamilyName} a partir de {template}");
            Document fam = app.NewFamilyDocument(template);
            if (fam == null) throw new InvalidOperationException("O Revit não criou o documento de família a partir de " + template);
            try
            {
                var resolver = new ConstraintResolver();
                Builder builder = null;
                SharedParams.WithDefinitions(app, defs =>
                {
                    using (var t = new Transaction(fam, "JA Famílias"))
                    {
                        t.Start();
                        var opts = t.GetFailureHandlingOptions();
                        opts.SetFailuresPreprocessor(resolver);
                        t.SetFailureHandlingOptions(opts);
                        builder = new Builder(fam, cfg, settings, defs, res);
                        builder.Run();
                        t.Commit();
                    }
                });
                // plan groups: detail-item families built and loaded outside the transaction
                if (builder != null)
                {
                    try { builder.Planta(app, resolver); }
                    catch (Exception ex) { res.Warnings.Add("Planta 2D incompleta: " + ex.Message); Logger.Warn("[JA Famílias] Planta aninhada: " + ex); }
                    // every line of the plan where the layout says, also with another wall (rolled back)
                    try { builder.Conferir(); }
                    catch (Exception ex) { Logger.Warn("[JA Famílias] Conferência da planta: " + ex.Message); }
                }
                if (resolver.Resolvidas > 0)
                    res.Warnings.Add(resolver.Resolvidas + " restrição(ões) da família não puderam ser mantidas e foram removidas pelo Revit (detalhes no registro).");

                fam.SaveAs(res.RfaPath, new SaveAsOptions { OverwriteExistingFile = true, MaximumBackups = 1 });
                res.Family = fam.LoadFamily(project, new OverwriteLoadOptions());
            }
            finally
            {
                try { fam.Close(false); } catch { }
            }

            if (res.Family == null)
                res.Family = new FilteredElementCollector(project).OfClass(typeof(Family)).Cast<Family>().FirstOrDefault(f => f.Name == res.FamilyName);
            if (res.Family == null) throw new InvalidOperationException("A família foi gerada (" + res.RfaPath + ") mas não foi carregada no projeto.");

            foreach (ElementId id in res.Family.GetFamilySymbolIds())
            {
                if (!(project.GetElement(id) is FamilySymbol s)) continue;
                if (res.Symbol == null || s.Name == res.TypeName) res.Symbol = s;
            }
            if (res.Symbol != null && !res.Symbol.IsActive)
            {
                using (var t = new Transaction(project, "JA Famílias – ativar tipo"))
                {
                    t.Start();
                    res.Symbol.Activate();
                    t.Commit();
                }
            }
            return res;
        }

        /// <summary>Name of the Yes/No instance parameter that shows a plan group ("Planta - folhas"...).</summary>
        public static string NomeGrupoPlanta(EsquadriaConfig c, GrupoPlanta g)
        {
            switch (g)
            {
                case GrupoPlanta.Marco: return "Planta - marco e batente";
                case GrupoPlanta.Folha: return c.IsPorta ? "Planta - folha aberta" : "Planta - folhas";
                case GrupoPlanta.Arco: return "Planta - arco de abertura";
                case GrupoPlanta.FolhaFechada: return "Planta - folha fechada";
                case GrupoPlanta.Guarnicao: return "Planta - guarnições";
                case GrupoPlanta.Pedra: return c.IsPorta ? "Planta - " + c.NomeSoleira.ToLowerInvariant() : "Planta - peitoril";
                case GrupoPlanta.Verga: return "Planta - projeção da verga";
                case GrupoPlanta.Setas: return "Planta - setas de correr";
                case GrupoPlanta.Vao: return "Planta - abertura na parede";
                default: return "Planta - " + g;
            }
        }

        public static string SafeName(string name)
        {
            var bad = Path.GetInvalidFileNameChars().Concat(new[] { '\\', ':', '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~', '"', '/' }).ToArray();
            string s = new string((name ?? "").Select(ch => bad.Contains(ch) ? '-' : ch).ToArray()).Trim();
            return s.Length == 0 ? "JA Esquadria" : s;
        }

        // ==========================================================================================
        private sealed class Builder
        {
            private readonly Document _fam;
            private readonly FamilyManager _fm;
            private readonly EsquadriaConfig _c;
            private readonly Settings _s;
            private readonly Dictionary<Guid, ExternalDefinition> _defs;
            private readonly GenerationResult _res;

            // frame of reference (feet): u = right as seen from outside, n = towards the exterior
            private XYZ _u, _n;
            private double _xLeft, _zBot, _nCenter;
            /// <summary>Offset (mm) of the frame's exterior face from the wall centre, towards the exterior.</summary>
            private double _dExt;
            private readonly Dictionary<long, SketchPlane> _planes = new Dictionary<long, SketchPlane>();
            private readonly List<Extrusion> _solids = new List<Extrusion>();

            // ----- following the real wall (see AncorasParede)
            private Reference _refExt, _refInt;
            private double _extPos, _intPos;          // feet along n, faces of the template wall
            private ViewPlan _planView;
            private AnaliseProfundidade _an;
            private AncorasParede _anc;
            private readonly List<GrupoPlanta2D> _pendentes = new List<GrupoPlanta2D>();
            private readonly Dictionary<string, FamilyParameter> _visByName = new Dictionary<string, FamilyParameter>();
            private FamilyParameter _vaoParam;
            private SketchPlane _planSketch;
            private GraphicsStyle _stOutline, _stThin, _stDash;
            private Layout _layout;

            public Builder(Document fam, EsquadriaConfig c, Settings s, Dictionary<Guid, ExternalDefinition> defs, GenerationResult res)
            {
                _fam = fam;
                _fm = fam.FamilyManager;
                _c = c;
                _s = s;
                _defs = defs;
                _res = res;
            }

            public void Run()
            {
                // ---------------------------------------------------- type + main dimensions
                if (_fm.CurrentType == null) _fm.NewType(_res.TypeName);
                else { try { _fm.RenameCurrentType(_res.TypeName); } catch (Exception ex) { Warn("Tipo não renomeado: " + ex.Message); } }

                SetLength(Find(new[] { BuiltInParameter.FAMILY_WIDTH_PARAM, BuiltInParameter.WINDOW_WIDTH, BuiltInParameter.DOOR_WIDTH, BuiltInParameter.GENERIC_WIDTH },
                               new[] { "Width", "Largura" }), _c.LarguraMm, "Largura");
                SetLength(Find(new[] { BuiltInParameter.FAMILY_HEIGHT_PARAM, BuiltInParameter.WINDOW_HEIGHT, BuiltInParameter.DOOR_HEIGHT, BuiltInParameter.GENERIC_HEIGHT },
                               new[] { "Height", "Altura" }), _c.AlturaMm, "Altura");
                if (!_c.IsPorta && !_c.ParedeCortina)
                {
                    var sill = Find(new[] { BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM },
                                    new[] { "Default Sill Height", "Altura padrão do peitoril", "Altura do peitoril padrão", "Altura padrão de peitoril", "Altura de peitoril padrão" });
                    if (sill == null) sill = _fm.Parameters.Cast<FamilyParameter>().FirstOrDefault(p => Contains(p, "sill") || Contains(p, "peitoril"));
                    SetLength(sill, _c.PeitorilMm, "Peitoril", warnIfMissing: false);
                }
                _fam.Regenerate();

                // ---------------------------------------------------- where the opening is
                ReadHost();
                // the geometry is laid out for the template wall; the anchors make it follow the real one
                double tTpl = (_extPos - _intPos) * 304.8;
                if (tTpl > 10 && tTpl < 3000) _c.ParedeMm = Math.Round(tTpl, 2);
                _dExt = Layout.FrameOffsetFromCenter(_c);
                _an = AnaliseProfundidade.Executar(_c, _c.ParedeMm);
                var layout = _an.Ok ? _an.Layout : Layout.Build(_c);
                _layout = layout;
                _res.Layout = layout;
                foreach (string a in layout.Avisos) Warn(a);
                if (_an.Ok && _refExt != null && _refInt != null && _planView != null)
                {
                    try
                    {
                        _anc = new AncorasParede(_fam, _planView, _u, _n, _extPos, _intPos, _xLeft - 2.0, _c.RevestimentoExtMm, _c.RevestimentoIntMm, Warn);
                        if (!_anc.Iniciar(_refExt, _refInt)) _anc = null;
                    }
                    catch (Exception ex) { _anc = null; Logger.Warn("[JA Famílias] Âncoras na parede: " + ex); }
                }
                if (_anc == null && _c.ParedeCortina) _an = null;   // curtain wall: no host wall to follow
                else if (_anc == null)
                {
                    if (_an != null && !_an.Ok && !string.IsNullOrEmpty(_an.Motivo)) Logger.Warn("[JA Famílias] Não paramétrica: " + _an.Motivo);
                    _an = null;
                    Warn("A família não ficou paramétrica: ela foi desenhada para a parede do modelo de família (" + Layout.Mm(_c.ParedeMm) +
                         " mm) e os revestimentos ficaram fixos. Detalhes no registro.");
                }
                _res.Adaptavel = _anc != null || _c.ParedeCortina;

                // ---------------------------------------------------- materials
                var perfil = Catalog.Perfil(_c.PerfilId);
                var vidro = Catalog.Vidro(_c.VidroId);
                var cor = Catalog.Cor(_c.CorId);
                string corNome = cor != null && string.Equals(cor.Hex, _c.CorHex, StringComparison.OrdinalIgnoreCase) ? cor.Nome : "#" + _c.CorHex.ToUpperInvariant();
                var matParams = new Dictionary<MaterialSlot, FamilyParameter>
                {
                    [MaterialSlot.Perfil] = MaterialParam("Material do perfil", MakeMaterial("JA Perfil - " + perfil.Material + " " + corNome, _c.CorHex, 0)),
                    [MaterialSlot.Vidro] = MaterialParam("Material do vidro", MakeMaterial("JA Vidro - " + vidro.Nome, vidro.CorHex, vidro.Transparencia)),
                    [MaterialSlot.Painel] = MaterialParam("Material do painel", MakeMaterial("JA Painel - " + corNome, _c.CorHex, 0)),
                    // stone: the material name typed by the user, so a project material with the same
                    // name (e.g. "Granito São Gabriel") is used when the family is loaded
                    [MaterialSlot.Peitoril] = MaterialParam(_c.IsPorta ? "Material da soleira" : "Material do peitoril",
                        MakeMaterial(_c.IsPorta ? _c.SoleiraMaterial.Trim() : _c.PeitorilMaterial.Trim(), _c.IsPorta ? _c.SoleiraCorHex : _c.PeitorilCorHex, 0)),
                };

                // ---------------------------------------------------- solids
                int failed = 0;
                for (int pi = 0; pi < layout.Prismas.Count; pi++)
                {
                    var p = layout.Prismas[pi];
                    try
                    {
                        Extrusion ex = _an != null ? MakeExtrusion(p, true, _an.Prisma0[pi], _an.Prisma1[pi]) : MakeExtrusion(p);
                        if (ex == null) continue;
                        _res.Solids++;
                        _solids.Add(ex);
                        if (matParams.TryGetValue(p.Material, out FamilyParameter fp) && fp != null)
                        {
                            Parameter mp = ex.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
                            if (mp != null) { try { _fm.AssociateElementParameterToFamilyParameter(mp, fp); } catch { } }
                        }
                        // plan views use the 2D representation (symbolic lines): the solids are
                        // hidden there, like in office CAD blocks; 3D, elevations and sections keep them
                        // plans use the 2D drawing (doors: CAD block; windows: section at 1.20 m), so it is
                        // the same whatever the cut plane of the view; the window sill never shows in plan
                        bool planSymbolic = _c.SimbolosPlanta || p.Papel == Papel.Peitoril;
                        if (planSymbolic || !(p.Coarse && p.Medium && p.Fine))
                        {
                            var vis = new FamilyElementVisibility(FamilyElementVisibilityType.Model)
                            {
                                IsShownInCoarse = p.Coarse,
                                IsShownInMedium = p.Medium,
                                IsShownInFine = p.Fine
                            };
                            if (planSymbolic)
                            {
                                vis.IsShownInPlanRCPCut = false;
                                vis.IsShownInTopBottom = false;
                            }
                            ex.SetVisibility(vis);
                        }
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        Logger.Warn($"[JA Famílias] Sólido {p.Papel} não criado: {ex.Message}");
                    }
                }
                if (failed > 0) Warn($"{failed} peça(s) não puderam ser modeladas (detalhes no registro).");

                // ---------------------------------------------------- voids (sill recess in the masonry)
                if (layout.Vazios.Count > 0) CutVoids(layout.Vazios);
                if (_anc != null && _anc.Falhas > 0)
                    Logger.Warn($"[JA Famílias] {_anc.Falhas} restrição(ões) de parede não criadas.");

                // ---------------------------------------------------- symbols
                try { Symbols(layout); }
                catch (Exception ex) { Warn("Linhas de abertura não criadas: " + ex.Message); }

                // ---------------------------------------------------- data
                try { Data(layout, perfil, vidro, corNome); }
                catch (Exception ex) { Warn("Parâmetros de dados incompletos: " + ex.Message); }
            }

            // ------------------------------------------------------------------ parameters
            private static bool Contains(FamilyParameter p, string word) =>
                (p?.Definition?.Name ?? "").IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0;

            private FamilyParameter Find(BuiltInParameter[] bips, string[] names) => FamUtil.Find(_fm, bips, names);
            private FamilyParameter MaterialParam(string name, ElementId material) => FamUtil.MaterialParam(_fm, name, material, Warn);
            private FamilyParameter Shared(SharedParams.Def d) => FamUtil.Shared(_fm, _defs, d);

            private void SetLength(FamilyParameter p, double mm, string what, bool warnIfMissing = true)
            {
                if (_c.ParedeCortina)
                {
                    // curtain panels: the curtain grid drives width/height; setting them is only a hint
                    if (p == null) return;
                    try { if (!string.IsNullOrEmpty(p.Formula)) _fm.SetFormula(p, null); _fm.Set(p, mm * Mm); }
                    catch (Exception ex) { Logger.Info($"[JA Famílias] {what} do painel controlada pela parede cortina ({ex.Message})."); }
                    return;
                }
                if (p == null)
                {
                    if (warnIfMissing) Warn($"O modelo de família não tem o parâmetro de {what.ToLowerInvariant()}: a abertura na parede pode não acompanhar a esquadria.");
                    return;
                }
                try
                {
                    if (!string.IsNullOrEmpty(p.Formula)) _fm.SetFormula(p, null);
                    _fm.Set(p, mm * Mm);
                }
                catch (Exception ex) { Warn($"{what} não aplicada: {ex.Message}"); }
            }

            private ElementId MakeMaterial(string name, string hex, int transparency)
            {
                try
                {
                    var mat = new FilteredElementCollector(_fam).OfClass(typeof(Autodesk.Revit.DB.Material)).Cast<Autodesk.Revit.DB.Material>().FirstOrDefault(m => m.Name == name);
                    if (mat == null) mat = _fam.GetElement(Autodesk.Revit.DB.Material.Create(_fam, name)) as Autodesk.Revit.DB.Material;
                    if (mat == null) return ElementId.InvalidElementId;
                    mat.Color = FamUtil.ToColor(hex);
                    mat.Transparency = Math.Max(0, Math.Min(100, transparency));
                    mat.Shininess = transparency > 0 ? 90 : 40;
                    mat.Smoothness = transparency > 0 ? 90 : 50;
                    return mat.Id;
                }
                catch (Exception ex)
                {
                    Warn($"Material '{name}' não criado: {ex.Message}");
                    return ElementId.InvalidElementId;
                }
            }

            private void SetText(FamilyParameter p, string value)
            {
                if (p == null) return;
                try { _fm.Set(p, value ?? ""); } catch (Exception ex) { Logger.Warn($"[JA Famílias] {p.Definition?.Name}: {ex.Message}"); }
            }

            private void SetNumber(FamilyParameter p, double value)
            {
                if (p == null) return;
                try { _fm.Set(p, value); } catch (Exception ex) { Logger.Warn($"[JA Famílias] {p.Definition?.Name}: {ex.Message}"); }
            }

            private void SetBuiltIn(BuiltInParameter bip, string value)
            {
                FamilyParameter p = null;
                try { p = _fm.get_Parameter(bip); } catch { }
                SetText(p, value);
            }

            private void Data(Layout layout, PerfilInfo perfil, VidroInfo vidro, string corNome)
            {
                SetText(Shared(SharedParams.Config), _c.ToJson());
                SetText(Shared(SharedParams.Linha), perfil.Nome);
                SetText(Shared(SharedParams.Cor), corNome);
                SetText(Shared(SharedParams.Vidro), Layout.UsesGlass(_c) ? vidro.Nome : "—");
                SetText(Shared(SharedParams.Abertura), Layout.ResumoAberturas(_c));
                SetText(Shared(SharedParams.Ferragens), string.IsNullOrWhiteSpace(_c.Ferragens) ? "—" : _c.Ferragens.Trim());
                SetNumber(Shared(SharedParams.Uw), Math.Round(layout.Uw, 2));
                SetNumber(Shared(SharedParams.AreaIluminacao), UnitUtils.ConvertToInternalUnits(layout.AreaIluminacao, UnitTypeId.SquareMeters));
                SetNumber(Shared(SharedParams.AreaVentilacao), UnitUtils.ConvertToInternalUnits(layout.AreaVentilacao, UnitTypeId.SquareMeters));

                // soleira (doors) / peitoril (windows): code, material and size for tags and schedules
                bool stone = _c.IsPorta ? _c.Soleira : _c.Peitoril;
                if (stone && layout.Pedra.HasValue)
                {
                    var pd = layout.Pedra.Value;
                    double esp = _c.IsPorta ? _c.SoleiraEspessuraMm : _c.PeitorilEspessuraMm;
                    SetText(Shared(SharedParams.SoleiraCodigo), _c.IsPorta ? _c.SoleiraCodigo.Trim() : _c.PeitorilCodigo.Trim());
                    SetText(Shared(SharedParams.SoleiraMaterial), _c.IsPorta ? _c.SoleiraMaterial.Trim() : _c.PeitorilMaterial.Trim());
                    SetText(Shared(SharedParams.SoleiraDimensoes), (_c.IsPorta ? _c.NomeSoleira : "Peitoril") + " " +
                        Layout.Mm(pd.W) + " x " + Layout.Mm(pd.H) + " x " + Layout.Mm(esp) + " mm");
                }
                else
                {
                    SetText(Shared(SharedParams.SoleiraCodigo), "");
                    SetText(Shared(SharedParams.SoleiraMaterial), "");
                    SetText(Shared(SharedParams.SoleiraDimensoes), "");
                }

                SetBuiltIn(BuiltInParameter.ALL_MODEL_TYPE_MARK, _c.Codigo);
                SetBuiltIn(BuiltInParameter.ALL_MODEL_DESCRIPTION, Layout.Descricao(_c));
                SetBuiltIn(BuiltInParameter.ALL_MODEL_MANUFACTURER, _s.Fabricante);
                SetBuiltIn(BuiltInParameter.ALL_MODEL_MODEL, perfil.Nome);
                SetBuiltIn(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS, Layout.UsesGlass(_c) ? "Vidro " + vidro.Nome.ToLowerInvariant() : "");
            }

            // ------------------------------------------------------------------ host / opening
            private void ReadHost()
            {
                _n = new XYZ(0, -1, 0);
                if (_c.ParedeCortina) { ReadCurtainPanel(); return; }
                Wall wall = new FilteredElementCollector(_fam).OfClass(typeof(Wall)).Cast<Wall>().FirstOrDefault();
                if (wall != null)
                {
                    try
                    {
                        XYZ o = wall.Orientation;
                        var flat = new XYZ(o.X, o.Y, 0);
                        if (flat.GetLength() > 1e-6) _n = flat.Normalize();
                    }
                    catch { }
                }
                else Warn("O modelo de família não tem parede hospedeira: posição na parede calculada a partir da origem.");
                _u = XYZ.BasisZ.CrossProduct(_n).Normalize();

                // The template wall (with the opening already cut) tells where everything is:
                //  - faces facing ±n            -> wall faces -> centre plane;
                //  - faces facing up (+Z)        -> wall top and the opening sill  (sill = lowest);
                //  - faces facing down (-Z)      -> wall base and the opening head (base = lowest);
                //  - faces facing +u / -u        -> wall ends and the opening jambs.
                double? ext = null, inn = null;
                Reference refExt = null, refInn = null;
                var up = new List<double>();
                var down = new List<double>();
                var plusU = new List<double>();
                var minusU = new List<double>();
                if (wall != null)
                {
                    try
                    {
                        GeometryElement geo = wall.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine, ComputeReferences = true });
                        if (geo != null)
                            foreach (GeometryObject go in geo)
                            {
                                if (!(go is Solid solid) || solid.Faces.Size == 0) continue;
                                foreach (Face f in solid.Faces)
                                {
                                    if (!(f is PlanarFace pf)) continue;
                                    XYZ nn = pf.FaceNormal;
                                    double dn = nn.DotProduct(_n), du = nn.DotProduct(_u), dz = nn.Z;
                                    if (dn > 0.99) { double off = pf.Origin.DotProduct(_n); if (!ext.HasValue || off > ext.Value) { ext = off; refExt = pf.Reference; } }
                                    else if (dn < -0.99) { double off = pf.Origin.DotProduct(_n); if (!inn.HasValue || off < inn.Value) { inn = off; refInn = pf.Reference; } }
                                    else if (dz > 0.99) up.Add(pf.Origin.Z);
                                    else if (dz < -0.99) down.Add(pf.Origin.Z);
                                    else if (du > 0.99) plusU.Add(pf.Origin.DotProduct(_u));
                                    else if (du < -0.99) minusU.Add(pf.Origin.DotProduct(_u));
                                }
                            }
                    }
                    catch (Exception ex) { Logger.Warn("[JA Famílias] Geometria da parede do modelo ilegível: " + ex.Message); }
                }
                if (ext.HasValue && inn.HasValue)
                {
                    _nCenter = (ext.Value + inn.Value) / 2;
                    _extPos = ext.Value;
                    _intPos = inn.Value;
                    _refExt = refExt;
                    _refInt = refInn;
                }
                else
                {
                    _nCenter = 0;
                    try { if (wall?.Location is LocationCurve lc) _nCenter = lc.Curve.GetEndPoint(0).DotProduct(_n); } catch { }
                }

                double w = _c.LarguraMm * Mm;
                bool foundX = false, foundZ = false;
                double sMid = 0;
                _zBot = 0;

                // jambs: the opening's left jamb faces +u (the wall's right end too, further right)
                if (plusU.Count >= 2 && minusU.Count >= 2)
                {
                    double left = plusU.Min(), right = minusU.Max();
                    if (right > left)
                    {
                        sMid = (left + right) / 2;
                        foundX = true;
                        if (Math.Abs((right - left) - w) > 3 * Mm)
                            Warn("A abertura do modelo de família não acompanhou a largura: confira a família no editor.");
                    }
                }
                // bottom: windows have a sill face (lowest face looking up); doors start at the wall base
                if (up.Count >= 2) { _zBot = up.Min(); foundZ = true; }
                else if (_c.IsPorta && down.Count >= 1) { _zBot = down.Min(); foundZ = true; }

                if (!foundX || !foundZ)
                {
                    // fallback: the opening element's rectangle
                    try
                    {
                        Opening op = new FilteredElementCollector(_fam).OfClass(typeof(Opening)).Cast<Opening>()
                            .FirstOrDefault(o => { try { return o.IsRectBoundary; } catch { return false; } });
                        IList<XYZ> pts = op?.BoundaryRect;
                        if (pts != null && pts.Count >= 2)
                        {
                            if (!foundX) { sMid = (pts[0].DotProduct(_u) + pts[1].DotProduct(_u)) / 2; foundX = true; }
                            if (!foundZ) { _zBot = Math.Min(pts[0].Z, pts[1].Z); foundZ = true; }
                        }
                    }
                    catch (Exception ex) { Logger.Warn("[JA Famílias] Abertura do modelo ilegível: " + ex.Message); }
                }
                if (!foundZ && !_c.IsPorta)
                {
                    _zBot = _c.PeitorilMm * Mm;
                    Warn("Não encontrei a base do vão no modelo de família: a janela foi posicionada pela altura do peitoril.");
                }
                Logger.Info($"[JA Famílias] Vão no modelo: centro {sMid * 304.8:0} mm, base {_zBot * 304.8:0} mm (x {(foundX ? "geometria" : "origem")}).");
                _xLeft = sMid - w / 2;

                // depth position of the frame (same rule as the preview/plan)
                _dExt = Layout.FrameOffsetFromCenter(_c);
                _planView = new FilteredElementCollector(_fam).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                    .FirstOrDefault(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan);
            }

            /// <summary>
            /// Curtain panel template: no wall. The panel is centred on the curtain wall's plane;
            /// its sides and bottom come from the reference planes that are W apart (vertical) and H
            /// apart (horizontal, the level counts), else centre / level.
            /// </summary>
            private void ReadCurtainPanel()
            {
                _u = XYZ.BasisZ.CrossProduct(_n).Normalize();
                _nCenter = 0;
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
                foreach (double a in zs) foreach (double b in zs) if (Math.Abs(b - a - h) < 2 * Mm) { if (!bottom.HasValue || Math.Abs(a) < Math.Abs(bottom.Value)) bottom = a; }
                _xLeft = left ?? -w / 2;
                _zBot = bottom ?? 0;
                Logger.Info($"[JA Famílias] Painel de parede cortina: esquerda {_xLeft * 304.8:0} mm, base {_zBot * 304.8:0} mm.");
                _dExt = Layout.FrameOffsetFromCenter(_c);
                _planView = new FilteredElementCollector(_fam).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                    .FirstOrDefault(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan);
            }

            /// <summary>Elevation point (mm) at a position across the wall (mm from the wall centre, + = exterior).</summary>
            private XYZ Q(double x, double y, double pos) =>
                _u * (_xLeft + x * Mm) + XYZ.BasisZ * (_zBot + y * Mm) + _n * (_nCenter + pos * Mm);

            /// <summary>Elevation point (mm) at depth d (mm, from the frame's exterior face inwards) → model (feet).</summary>
            private XYZ P(double x, double y, double d) =>
                _u * (_xLeft + x * Mm) + XYZ.BasisZ * (_zBot + y * Mm) + _n * (_nCenter + (_dExt - d) * Mm);

            private SketchPlane PlaneAt(double d)
            {
                long key = (long)Math.Round(d * 100);
                if (_planes.TryGetValue(key, out SketchPlane sp) && sp.IsValidObject) return sp;
                sp = SketchPlane.Create(_fam, Plane.CreateByNormalAndOrigin(_n.Negate(), P(0, 0, d)));
                _planes[key] = sp;
                return sp;
            }

            /// <summary>
            /// Voids that must cut the host wall (the sill recess): created as void extrusions and
            /// joined to the template's wall with "Cortar geometria", so the real wall is cut too.
            /// </summary>
            private void CutVoids(IList<Prisma> vazios)
            {
                Wall wall = new FilteredElementCollector(_fam).OfClass(typeof(Wall)).Cast<Wall>().FirstOrDefault();
                var made = new List<Extrusion>();
                for (int vi = 0; vi < vazios.Count; vi++)
                {
                    var v = vazios[vi];
                    try { var ex = _an != null ? MakeExtrusion(v, false, _an.Vazio0[vi], _an.Vazio1[vi]) : MakeExtrusion(v, false); if (ex != null) made.Add(ex); }
                    catch (Exception ex) { Logger.Warn("[JA Famílias] Vazio do peitoril: " + ex.Message); }
                }
                if (made.Count == 0) return;
                if (wall == null) { Warn("O modelo de família não tem parede: o recorte do peitoril na alvenaria não foi feito."); return; }
                try { _fam.Regenerate(); } catch { }
                int fail = 0;
                foreach (var ex in made)
                {
                    try
                    {
                        bool exists = false;
                        try { exists = SolidSolidCutUtils.CutExistsBetweenElements(ex, wall, out _); } catch { }
                        if (!exists) SolidSolidCutUtils.AddCutBetweenSolids(_fam, wall, ex);
                    }
                    catch (Exception e) { fail++; Logger.Warn("[JA Famílias] Recorte do peitoril na parede: " + e.Message); }
                }
                if (fail > 0) Warn("O recorte do peitoril na alvenaria não foi feito (detalhes no registro).");
                VoidGuard.Uncut(_fam, made, _solids);
            }

            /// <summary>Solid sketched on the anchor of its depths (follows the wall); old way when it can't.</summary>
            private Extrusion MakeExtrusion(Prisma p, bool solid, Forma f0, Forma f1)
            {
                if (_anc != null && !(f0.Fixa && f1.Fixa))
                {
                    try
                    {
                        var loops = p.Loops.Select(FamUtil.Clean).Where(l => l.Count >= 3).ToList();
                        Extrusion ex = _anc.Extrusao(solid, loops, Q, f0, f1);
                        if (ex != null) return ex;
                    }
                    catch (Exception e) { Logger.Warn($"[JA Famílias] Sólido {p.Papel} ancorado: {e.Message}"); }
                }
                return MakeExtrusion(p, solid);
            }

            private Extrusion MakeExtrusion(Prisma p, bool solid = true)
            {
                double len = (p.D1 - p.D0) * Mm;
                if (len < 0.5 * Mm) return null;
                var arr = new CurveArrArray();
                foreach (var raw in p.Loops)
                {
                    var loop = FamUtil.Clean(raw);
                    if (loop.Count < 3) continue;
                    var ca = new CurveArray();
                    for (int i = 0; i < loop.Count; i++)
                    {
                        P2 a = loop[i], b = loop[(i + 1) % loop.Count];
                        ca.Append(Line.CreateBound(P(a.X, a.Y, p.D0), P(b.X, b.Y, p.D0)));
                    }
                    arr.Append(ca);
                }
                if (arr.Size == 0) return null;
                return _fam.FamilyCreate.NewExtrusion(solid, arr, PlaneAt(p.D0), len);
            }

            // ------------------------------------------------------------------ symbols
            private void Symbols(Layout layout)
            {
                if (layout.Simbolos.Count == 0 && layout.Planta.Count == 0) return;
                GraphicsStyle solid = Style("JA Abertura", false);
                GraphicsStyle dashed = Style("JA Abertura (para dentro)", true) ?? solid;

                if (layout.Simbolos.Count > 0)
                {
                    FamilyParameter elevParam = YesNo("Elevação - linhas de abertura", true);
                    SketchPlane elev = null;
                    if (_anc != null && _an != null && !_an.Marco.Fixa)
                    {
                        // 2 mm in front of the frame, on a plane that moves with it
                        var f = _an.Marco;
                        f.C += 2;
                        try { ReferencePlane rp = _anc.PlanoEm(f); if (rp != null) elev = _anc.EsbocoEm(rp); }
                        catch (Exception ex) { Logger.Warn("[JA Famílias] Plano das linhas de abertura: " + ex.Message); }
                    }
                    if (elev == null) elev = SketchPlane.Create(_fam, Plane.CreateByNormalAndOrigin(_n, P(0, 0, -2)));
                    foreach (var tr in layout.Simbolos)
                    {
                        XYZ a = P(tr.A.X, tr.A.Y, -2), b = P(tr.B.X, tr.B.Y, -2);
                        if (a.DistanceTo(b) < 2 * Mm) continue;
                        SymbolicCurve sc = _fam.FamilyCreate.NewSymbolicCurve(Line.CreateBound(a, b), elev);
                        GraphicsStyle gs = tr.Tracejado ? dashed : solid;
                        if (sc != null && gs != null) sc.Subcategory = gs;
                        BindVisibility(sc, elevParam);
                    }
                }

                var planta = layout.Planta.ToList();
                if (planta.Count > 0)
                {
                    GraphicsStyle outline = Style("JA Planta", false, 2) ?? solid;
                    GraphicsStyle thin = Style("JA Planta (linha fina)", false, 1) ?? solid;
                    GraphicsStyle dash = Style("JA Planta (tracejada)", true, 1) ?? thin;

                    // one Yes/No INSTANCE parameter per group: the 2D view can be changed door by
                    // door in Revit (Propriedades › Gráficos), without regenerating the family
                    var groupParams = new Dictionary<GrupoPlanta, FamilyParameter>();
                    foreach (var g in planta.Select(t => t.Grupo).Distinct())
                    {
                        bool on = planta.Any(t => t.Grupo == g && t.Visivel);
                        groupParams[g] = YesNo(GroupName(g), on);
                    }

                    SketchPlane plan = SketchPlane.Create(_fam, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, P(0, 0, 0)));
                    _planSketch = plan;
                    _stOutline = outline;
                    _stThin = thin;
                    _stDash = dash;
                    foreach (var kv in groupParams) if (kv.Value != null) _visByName[GroupName(kv.Key)] = kv.Value;
                    groupParams.TryGetValue(GrupoPlanta.Vao, out _vaoParam);

                    var done = new HashSet<string>();
                    var grupos = new Dictionary<string, GrupoPlanta2D>();
                    GrupoPlanta2D abertura = null;
                    for (int i = 0; i < planta.Count; i++)
                    {
                        var tp = planta[i];
                        // the same edge is often produced twice (adjacent rectangles): draw it once per group
                        string key = tp.Grupo + ":" + Key(tp);
                        if (!done.Add(key)) continue;
                        if (P(tp.A.X, 0, tp.A.Y).DistanceTo(P(tp.B.X, 0, tp.B.Y)) < 1.5 * Mm) continue;

                        if (_anc != null && _an != null)
                        {
                            Forma fa = _an.PlantaA[i], fb = _an.PlantaB[i], fm = _an.PlantaM[i];
                            if (tp.Grupo == GrupoPlanta.Vao && !_c.IsPorta)
                            {
                                // the opening (jambs across the wall + mask): one nested family with a set per wall thickness
                                if (abertura == null && _anc.PEsp != null)
                                {
                                    var anc = _anc.AncoraDe(new Forma { At = 0.5 });
                                    if (anc != null)
                                    {
                                        abertura = new GrupoPlanta2D
                                        {
                                            Nome = SafeName(_res.FamilyName + " - abertura"), Ancora = anc, Origem = _anc.T / 2,
                                            Faixas = true, Largura = layout.W, Espessura = _anc.T
                                        };
                                        _pendentes.Add(abertura);
                                    }
                                }
                                if (abertura != null) continue;
                            }
                            bool rigid = !fa.Fixa && !fb.Fixa && !fm.Fixa && fa.MesmaAncora(fb) && (!tp.IsArc || fa.MesmaAncora(fm));
                            if (rigid)
                            {
                                if (!grupos.TryGetValue(fa.Chave, out GrupoPlanta2D g))
                                {
                                    var anc = _anc.AncoraDe(fa);
                                    if (anc != null)
                                    {
                                        g = new GrupoPlanta2D
                                        {
                                            Nome = SafeName(_res.FamilyName + " - planta " + (grupos.Count + 1)), Ancora = anc,
                                            Origem = anc.Forma.Valor(_anc.T, _anc.Rx, _anc.Ri) - anc.Sinal * AncorasParede.K
                                        };
                                        grupos[fa.Chave] = g;
                                        _pendentes.Add(g);
                                    }
                                }
                                if (g != null)
                                {
                                    g.Tracos.Add(new Traco2D
                                    {
                                        XA = tp.A.X, PA = _dExt - tp.A.Y, XB = tp.B.X, PB = _dExt - tp.B.Y, XM = tp.Mid.X, PM = _dExt - tp.Mid.Y,
                                        Arco = tp.IsArc, Fino = tp.Fino, Tracejado = tp.Tracejado, Grupo = GroupName(tp.Grupo)
                                    });
                                    continue;
                                }
                            }
                            else if (!tp.IsArc && Math.Abs(tp.A.X - tp.B.X) < 0.01 && !fa.Fixa && !fb.Fixa)
                            {
                                // a line across the wall: drawn here, both ends held on their planes
                                SymbolicCurve across = HostLine(tp, groupParams);
                                if (across != null) _anc.PrenderPontas(across, fa, fb, plan, _xLeft + tp.A.X * Mm);
                                continue;
                            }
                        }
                        HostLine(tp, groupParams);
                    }

                    // windows: hide the wall between the jambs in plan (masking region), so the opening
                    // shows even when the cut plane of the view passes below the sill or above the head
                    if (!_c.IsPorta && _vaoParam != null && abertura == null)
                        MaskOpening(layout, _vaoParam);
                }
            }

            /// <summary>A plan line drawn directly in the family (fixed where the layout puts it).</summary>
            private SymbolicCurve HostLine(TracoPlanta tp, Dictionary<GrupoPlanta, FamilyParameter> groupParams)
            {
                try
                {
                    XYZ a = P(tp.A.X, 0, tp.A.Y), b = P(tp.B.X, 0, tp.B.Y);
                    Curve c = tp.IsArc ? (Curve)Arc.Create(a, b, P(tp.Mid.X, 0, tp.Mid.Y)) : Line.CreateBound(a, b);
                    SymbolicCurve sc = _fam.FamilyCreate.NewSymbolicCurve(c, _planSketch);
                    GraphicsStyle gs = tp.Tracejado ? _stDash : tp.Fino ? _stThin : _stOutline;
                    if (sc != null && gs != null) sc.Subcategory = gs;
                    if (groupParams.TryGetValue(tp.Grupo, out FamilyParameter fp)) BindVisibility(sc, fp);
                    return sc;
                }
                catch (Exception ex)
                {
                    Logger.Warn("[JA Famílias] Linha de planta: " + ex.Message);
                    return null;
                }
            }

            /// <summary>
            /// Second step (outside the first transaction): the plan groups become detail-item
            /// families loaded into this family, then placed on their anchors. Without the detail
            /// template (or if it fails) the lines are drawn directly, fixed.
            /// </summary>
            public void Planta(RevitApp app, ConstraintResolver resolver)
            {
                if (_pendentes.Count == 0) return;
                string template = TemplateLocator.FindDetail(app, _s, out string searched);
                if (template == null)
                    Warn("Não encontrei o modelo de item de detalhe (.rft) do Revit (procurado em: " + searched + "): as linhas da planta foram desenhadas fixas e não acompanham a espessura da parede.");
                else
                    foreach (var g in _pendentes)
                    {
                        try { g.Familia = PlantaAninhada.Construir(app, template, g, _u, _n, _fam); }
                        catch (Exception ex) { Logger.Warn("[JA Famílias] Grupo da planta " + g.Nome + ": " + ex); }
                    }

                using (var t = new Transaction(_fam, "JA Famílias – planta"))
                {
                    t.Start();
                    var opts = t.GetFailureHandlingOptions();
                    opts.SetFailuresPreprocessor(resolver);
                    t.SetFailureHandlingOptions(opts);
                    int fixos = 0;
                    foreach (var g in _pendentes)
                    {
                        if (g.Familia != null && Colocar(g)) continue;
                        DesenharFixo(g);
                        fixos++;
                    }
                    if (fixos > 0 && template != null)
                        Warn(fixos + " grupo(s) de linhas da planta ficaram fixos (não acompanham a espessura da parede). Detalhes no registro.");
                    t.Commit();
                }
            }

            private bool Colocar(GrupoPlanta2D g)
            {
                try
                {
                    FamilySymbol sym = g.Familia.GetFamilySymbolIds().Select(id => _fam.GetElement(id) as FamilySymbol).FirstOrDefault(x => x != null);
                    if (sym == null) return false;
                    if (!sym.IsActive) sym.Activate();
                    FamilyInstance inst = _fam.FamilyCreate.NewFamilyInstance(_anc.Pt(_xLeft, g.Origem), sym, _planView);
                    if (inst == null) return false;
                    _anc.PrenderGrupo(inst, g.Ancora);
                    if (g.Faixas)
                    {
                        Associar(inst, PlantaAninhada.PAbertura, _vaoParam);
                        Parameter pe = inst.LookupParameter(PlantaAninhada.PEspessura);
                        bool ok = false;
                        if (pe != null && _anc.PEsp != null)
                        {
                            try { _fam.FamilyManager.AssociateElementParameterToFamilyParameter(pe, _anc.PEsp); ok = true; }
                            catch { ok = false; }
                            if (!ok)
                            {
                                FamilyParameter aux = _anc.Aux("0 mm + " + AncorasParede.NomeEspessura);
                                if (aux != null) { try { _fam.FamilyManager.AssociateElementParameterToFamilyParameter(pe, aux); ok = true; } catch { } }
                            }
                        }
                        if (!ok) Logger.Warn("[JA Famílias] A abertura da planta não recebeu a espessura da parede.");
                    }
                    else
                        foreach (string name in g.Tracos.Select(x => x.Grupo).Where(x => !string.IsNullOrEmpty(x)).Distinct())
                            if (_visByName.TryGetValue(name, out FamilyParameter fp)) Associar(inst, name, fp);
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Warn("[JA Famílias] Grupo da planta não colocado (" + g.Nome + "): " + ex.Message);
                    return false;
                }
            }

            private void Associar(FamilyInstance inst, string name, FamilyParameter host)
            {
                if (host == null) return;
                try
                {
                    Parameter p = inst.LookupParameter(name);
                    if (p != null) _fam.FamilyManager.AssociateElementParameterToFamilyParameter(p, host);
                }
                catch (Exception ex) { Logger.Warn("[JA Famílias] Visibilidade do grupo da planta '" + name + "': " + ex.Message); }
            }

            /// <summary>Fallback: the group's lines drawn directly in the family, where they are now.</summary>
            private void DesenharFixo(GrupoPlanta2D g)
            {
                if (g.Faixas)
                {
                    foreach (double x in new[] { 0.0, g.Largura })
                    {
                        try
                        {
                            var sc = _fam.FamilyCreate.NewSymbolicCurve(Line.CreateBound(Q(x, 0, g.Origem), Q(x, 0, g.Origem - g.Espessura)), _planSketch);
                            if (sc != null && _stOutline != null) sc.Subcategory = _stOutline;
                            BindVisibility(sc, _vaoParam);
                        }
                        catch (Exception ex) { Logger.Warn("[JA Famílias] Linha do vão: " + ex.Message); }
                    }
                    if (_vaoParam != null && _layout != null) MaskOpening(_layout, _vaoParam);
                    return;
                }
                foreach (var t in g.Tracos)
                {
                    try
                    {
                        XYZ a = Q(t.XA, 0, t.PA), b = Q(t.XB, 0, t.PB);
                        if (a.DistanceTo(b) < 1.5 * Mm) continue;
                        Curve c = t.Arco ? (Curve)Arc.Create(a, b, Q(t.XM, 0, t.PM)) : Line.CreateBound(a, b);
                        SymbolicCurve sc = _fam.FamilyCreate.NewSymbolicCurve(c, _planSketch);
                        GraphicsStyle gs = t.Tracejado ? _stDash : t.Fino ? _stThin : _stOutline;
                        if (sc != null && gs != null) sc.Subcategory = gs;
                        if (_visByName.TryGetValue(t.Grupo ?? "", out FamilyParameter fp)) BindVisibility(sc, fp);
                    }
                    catch (Exception ex) { Logger.Warn("[JA Famílias] Linha de planta: " + ex.Message); }
                }
            }

            private void MaskOpening(Layout layout, FamilyParameter vis)
            {
                try
                {
                    // FilledRegion.CreateMaskingRegion(Document, ElementId viewId, IList<CurveLoop>), looked up
                    // by reflection so the add-in still loads if a Revit version lacks it
                    var mi = typeof(FilledRegion).GetMethod("CreateMaskingRegion", new[] { typeof(Document), typeof(ElementId), typeof(IList<CurveLoop>) });
                    if (mi == null) { Logger.Warn("[JA Famílias] Região de máscara indisponível nesta versão do Revit."); return; }
                    ViewPlan plan = new FilteredElementCollector(_fam).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                        .FirstOrDefault(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan);
                    if (plan == null) { Logger.Warn("[JA Famílias] Vista de planta do modelo de família não encontrada."); return; }
                    double z = plan.GenLevel?.ProjectElevation ?? 0;
                    XYZ F(double x, double d) { XYZ q = P(x, 0, d); return new XYZ(q.X, q.Y, z); }
                    double d0 = layout.ParedeExt - 1, d1 = layout.ParedeInt + 1;
                    var pts = new[] { F(0, d0), F(layout.W, d0), F(layout.W, d1), F(0, d1) };
                    var loop = new CurveLoop();
                    for (int i = 0; i < 4; i++) loop.Append(Line.CreateBound(pts[i], pts[(i + 1) % 4]));
                    var region = mi.Invoke(null, new object[] { _fam, plan.Id, new List<CurveLoop> { loop } }) as Element;
                    if (region == null) return;
                    try
                    {
                        GraphicsStyle inv = Category.GetCategory(_fam, BuiltInCategory.OST_InvisibleLines)?.GetGraphicsStyle(GraphicsStyleType.Projection);
                        var set = region.GetType().GetMethod("SetLineStyleId", new[] { typeof(ElementId) });
                        if (inv != null && set != null) set.Invoke(region, new object[] { inv.Id });
                    }
                    catch (Exception ex) { Logger.Warn("[JA Famílias] Contorno da máscara: " + ex.Message); }
                    BindVisibility(region, vis);
                }
                catch (Exception ex)
                {
                    Logger.Warn("[JA Famílias] Máscara da abertura: " + (ex.InnerException?.Message ?? ex.Message));
                    Warn("A parede não foi mascarada na abertura (planta): em vistas cujo corte não passa pela janela, a parede pode aparecer contínua.");
                }
            }

            private string GroupName(GrupoPlanta g) => NomeGrupoPlanta(_c, g);

            // ------------------------------------------------------------------ self-check of the plan
            /// <summary>
            /// Measures the plan lines of the finished family (host symbolic lines + nested plan
            /// groups) at the template wall and, when the family follows the wall, at two other
            /// thicknesses and finishes (changes rolled back), and compares them with the layout.
            /// A line out of place becomes a warning (details in the log); it never stops the generation.
            /// </summary>
            public void Conferir()
            {
                if (!_c.SimbolosPlanta || _planView == null || _layout == null || _layout.Planta.Count == 0) return;
                double rx = _c.RevestimentoExtMm, ri = _c.RevestimentoIntMm;
                var estados = new List<double[]> { new[] { 0.0, rx, ri } };
                if (_anc != null && _anc.PRx != null && _anc.PRi != null && !_c.ParedeCortina)
                {
                    estados.Add(new[] { 60.0, rx + 15, ri + 10 });
                    estados.Add(new[] { -40.0, Math.Max(0, rx - 10), ri + 25 });
                }
                var problemas = new List<string>();
                int conferidos = 0;
                foreach (double[] st in estados)
                {
                    using (var t = new Transaction(_fam, "JA Famílias – conferência da planta"))
                    {
                        try
                        {
                            t.Start();
                            var opts = t.GetFailureHandlingOptions();
                            opts.SetFailuresPreprocessor(new WarningSwallower());
                            t.SetFailureHandlingOptions(opts);
                            if (Math.Abs(st[0]) > 0.01 && !MudarEspessura(st[0])) { Logger.Info("[JA Famílias] Conferência da planta: espessura da parede do modelo não pôde ser variada."); continue; }
                            if (_anc != null && _anc.PRx != null && _anc.PRi != null)
                            {
                                _fm.Set(_anc.PRx, st[1] * Mm);
                                _fm.Set(_anc.PRi, st[2] * Mm);
                            }
                            _fam.Regenerate();

                            double centro = _nCenter, tMm = _c.ParedeMm;
                            Wall wall = new FilteredElementCollector(_fam).OfClass(typeof(Wall)).Cast<Wall>().FirstOrDefault();
                            if (!_c.ParedeCortina && wall != null && Faces(wall, _n, out double ext, out double inn))
                            {
                                centro = (ext + inn) / 2;
                                tMm = (ext - inn) / Mm;
                            }
                            EsquadriaConfig c = _c.Clone();
                            c.ParedeMm = Math.Round(tMm, 2);
                            c.RevestimentoExtMm = st[1];
                            c.RevestimentoIntMm = st[2];
                            var esperadas = ConferenciaPlanta.Esperadas(c, Ligado);
                            var medidas = MedirPlanta(centro);
                            var r = ConferenciaPlanta.Comparar(esperadas, medidas, _c.LarguraMm, false);
                            conferidos++;
                            string estado = Math.Abs(st[0]) < 0.01 ? "na parede do modelo (" + Layout.Mm(tMm) + " mm)"
                                          : "em parede de " + Layout.Mm(tMm) + " mm, revestimentos " + Layout.Mm(st[1]) + "/" + Layout.Mm(st[2]) + " mm";
                            Logger.Info($"[JA Famílias] Conferência da planta {estado}: {r.Esperadas - r.Desvios.Count}/{r.Esperadas} linhas no lugar ({r.Medidas} medidas).");
                            if (!r.Ok)
                            {
                                Logger.Warn("[JA Famílias] Planta " + estado + ":\n" + ConferenciaPlanta.Texto(c, r, 40));
                                problemas.Add(estado + ": " + r.Desvios.Count + " linha(s) fora do lugar" + (r.MaiorDesvio > 0 ? " (até " + Layout.Mm(r.MaiorDesvio) + " mm)" : ""));
                            }
                        }
                        catch (Exception ex) { Logger.Warn("[JA Famílias] Conferência da planta: " + ex.Message); }
                        finally
                        {
                            try { if (t.HasStarted() && !t.HasEnded()) t.RollBack(); } catch { }
                        }
                    }
                }
                _res.PlantaConferida = conferidos > 0 && problemas.Count == 0;
                if (problemas.Count > 0)
                    Warn("Conferência da planta 2D: " + string.Join("; ", problemas) + ". A família foi gerada assim mesmo; use Conferir Planta no projeto e mande o registro para análise.");
            }

            /// <summary>Default of the Yes/No parameter of a plan group (lines of hidden groups are not drawn by Revit).</summary>
            private bool Ligado(GrupoPlanta g)
            {
                if (!_visByName.TryGetValue(GroupName(g), out FamilyParameter fp) || fp == null) return true;
                try { return _fm.CurrentType?.AsInteger(fp) != 0; } catch { return true; }
            }

            private List<SegMedido> MedirPlanta(double centro)
            {
                var list = new List<SegMedido>();
                Func<XYZ, double> pos = p => (p.DotProduct(_n) - centro) / Mm;
                Func<XYZ, double> x = p => (p.DotProduct(_u) - _xLeft) / Mm;
                foreach (SymbolicCurve sc in new FilteredElementCollector(_fam).OfClass(typeof(CurveElement)).OfType<SymbolicCurve>())
                {
                    try
                    {
                        string st = sc.Subcategory?.GraphicsStyleCategory?.Name ?? "";
                        if (!st.StartsWith("JA Planta", StringComparison.OrdinalIgnoreCase)) continue;
                        Parameter vis = sc.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM);
                        if (vis != null && vis.AsInteger() == 0) continue;
                        if (sc.GeometryCurve is Line ln) ConferenciaPlanta.Adicionar(ln.GetEndPoint(0), ln.GetEndPoint(1), pos, x, list);
                    }
                    catch { }
                }
                var opt = new Options { View = _planView };
                foreach (FamilyInstance fi in new FilteredElementCollector(_fam).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>())
                {
                    try { ConferenciaPlanta.Coletar(fi.get_Geometry(opt), _fam, pos, x, list); }
                    catch { }
                }
                return list;
            }

            /// <summary>Changes the template wall thickness by <paramref name="deltaMm"/> (thickest layer), in a new wall type.</summary>
            private bool MudarEspessura(double deltaMm)
            {
                Wall wall = new FilteredElementCollector(_fam).OfClass(typeof(Wall)).Cast<Wall>().FirstOrDefault();
                WallType wt = wall?.WallType;
                CompoundStructure cs = wt?.GetCompoundStructure();
                if (cs == null || cs.LayerCount == 0) return false;
                int idx = 0;
                for (int i = 1; i < cs.LayerCount; i++) if (cs.GetLayerWidth(i) > cs.GetLayerWidth(idx)) idx = i;
                double nova = cs.GetLayerWidth(idx) + deltaMm * Mm;
                if (nova < 10 * Mm) return false;
                cs.SetLayerWidth(idx, nova);
                if (!(wt.Duplicate("JA conferência " + DateTime.Now.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)) is WallType nt)) return false;
                nt.SetCompoundStructure(cs);
                wall.WallType = nt;
                return true;
            }

            /// <summary>Exterior and interior faces (feet along n) of a wall.</summary>
            private static bool Faces(Wall wall, XYZ n, out double ext, out double inn)
            {
                ext = double.MinValue; inn = double.MaxValue;
                GeometryElement geo = wall.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine });
                if (geo == null) return false;
                foreach (GeometryObject go in geo)
                {
                    if (!(go is Solid solid) || solid.Faces.Size == 0) continue;
                    foreach (Face f in solid.Faces)
                    {
                        if (!(f is PlanarFace pf)) continue;
                        double dn = pf.FaceNormal.DotProduct(n), off = pf.Origin.DotProduct(n);
                        if (dn > 0.99 && off > ext) ext = off;
                        else if (dn < -0.99 && off < inn) inn = off;
                    }
                }
                return ext > double.MinValue && inn < double.MaxValue && ext > inn;
            }

            /// <summary>Yes/No instance parameter (group Graphics) with a default value.</summary>
            private FamilyParameter YesNo(string name, bool value)
            {
                try
                {
                    FamilyParameter fp = _fm.get_Parameter(name) ?? _fm.AddParameter(name, GroupTypeId.Graphics, SpecTypeId.Boolean.YesNo, true);
                    _fm.Set(fp, value ? 1 : 0);
                    return fp;
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[JA Famílias] Parâmetro '{name}' não criado: {ex.Message}");
                    return null;
                }
            }

            private void BindVisibility(Element e, FamilyParameter fp)
            {
                if (e == null || fp == null) return;
                try
                {
                    Parameter vis = e.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM);
                    if (vis != null) _fm.AssociateElementParameterToFamilyParameter(vis, fp);
                }
                catch (Exception ex) { Logger.Warn("[JA Famílias] Visibilidade não associada: " + ex.Message); }
            }

            private static string Key(TracoPlanta t)
            {
                string P2s(P2 p) => p.X.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + ";" +
                                    p.Y.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
                string a = P2s(t.A), b = P2s(t.B);
                if (t.IsArc) return "arc:" + a + "|" + b + "|" + P2s(t.Mid);
                return string.CompareOrdinal(a, b) < 0 ? a + "|" + b : b + "|" + a;
            }

            private GraphicsStyle Style(string name, bool dashed, int weight = 1)
            {
                try
                {
                    Category parent = _fam.OwnerFamily.FamilyCategory;
                    if (parent == null) return null;
                    Category sub = null;
                    foreach (Category c in parent.SubCategories) if (c.Name == name) sub = c;
                    if (sub == null) sub = _fam.Settings.Categories.NewSubcategory(parent, name);
                    sub.SetLineWeight(weight, GraphicsStyleType.Projection);
                    if (dashed)
                    {
                        ElementId pat = DashPattern();
                        if (pat != ElementId.InvalidElementId) sub.SetLinePatternId(pat, GraphicsStyleType.Projection);
                    }
                    return sub.GetGraphicsStyle(GraphicsStyleType.Projection);
                }
                catch (Exception ex)
                {
                    Logger.Warn("[JA Famílias] Subcategoria " + name + ": " + ex.Message);
                    return null;
                }
            }

            private ElementId DashPattern()
            {
                var all = new FilteredElementCollector(_fam).OfClass(typeof(LinePatternElement)).Cast<LinePatternElement>().ToList();
                var found = all.FirstOrDefault(p => p.Name == "JA Tracejado")
                            ?? all.FirstOrDefault(p => { string n = p.Name.ToLowerInvariant(); return n.StartsWith("dash") || n.StartsWith("tracej") || n.StartsWith("hidden") || n.StartsWith("oculta"); });
                if (found != null) return found.Id;
                var lp = new LinePattern("JA Tracejado");
                lp.SetSegments(new List<LinePatternSegment>
                {
                    new LinePatternSegment(LinePatternSegmentType.Dash, 2.5 * Mm),
                    new LinePatternSegment(LinePatternSegmentType.Space, 1.5 * Mm)
                });
                return LinePatternElement.Create(_fam, lp).Id;
            }

            private void Warn(string w)
            {
                _res.Warnings.Add(w);
                Logger.Warn("[JA Famílias] " + w);
            }
        }
    }

    /// <summary>Reloading a generated family replaces the previous version and its values.</summary>
    /// <summary>A void must cut only the host wall, never the family's own pieces (stone, linings).</summary>
    internal static class VoidGuard
    {
        public static void Uncut(Document fam, IEnumerable<Extrusion> voids, IEnumerable<Extrusion> solids)
        {
            foreach (var v in voids)
                foreach (var s in solids)
                {
                    try
                    {
                        if (SolidSolidCutUtils.CutExistsBetweenElements(v, s, out _))
                            SolidSolidCutUtils.RemoveCutBetweenSolids(fam, s, v);
                    }
                    catch (Exception ex) { Logger.Warn("[JA Famílias] Vazio x peça: " + ex.Message); }
                }
        }
    }

    internal sealed class OverwriteLoadOptions : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            overwriteParameterValues = true;
            return true;
        }

        public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
        {
            source = FamilySource.Family;
            overwriteParameterValues = true;
            return true;
        }
    }

    /// <summary>
    /// Warnings are dropped; errors that Revit can resolve itself (a constraint that can't be kept)
    /// are resolved with Revit's default resolution, so the family is still saved and loaded.
    /// </summary>
    internal sealed class ConstraintResolver : IFailuresPreprocessor
    {
        public int Resolvidas;

        public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
        {
            bool resolved = false;
            foreach (FailureMessageAccessor f in a.GetFailureMessages())
            {
                if (f.GetSeverity() == FailureSeverity.Warning) { a.DeleteWarning(f); continue; }
                if (f.GetSeverity() == FailureSeverity.Error && f.HasResolutions())
                {
                    try
                    {
                        Logger.Warn("[JA Famílias] Restrição resolvida pelo Revit: " + f.GetDescriptionText());
                        a.ResolveFailure(f);
                        Resolvidas++;
                        resolved = true;
                    }
                    catch { }
                }
            }
            return resolved ? FailureProcessingResult.ProceedWithCommit : FailureProcessingResult.Continue;
        }
    }

    /// <summary>Warnings raised while building the family are not shown to the user.</summary>
    internal sealed class WarningSwallower : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
        {
            foreach (FailureMessageAccessor f in a.GetFailureMessages())
                if (f.GetSeverity() == FailureSeverity.Warning) a.DeleteWarning(f);
            return FailureProcessingResult.Continue;
        }
    }
}
