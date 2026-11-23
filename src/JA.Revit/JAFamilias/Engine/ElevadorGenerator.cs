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
    /// <summary>Settings of the elevator landing door family.</summary>
    public sealed class ElevadorConfig
    {
        public string Codigo = "PE01";
        public double LarguraMm = 900, AlturaMm = 2100, PortalMm = 100, AfastamentoBotoeiraMm = 150, AlturaBotoeiraMm = 1000;
        public bool Botoeira = true, TiposExtras = true;
        public string MaterialPortal = "Aço inox escovado", MaterialFolhas = "Aço inox escovado", MaterialSoleira = "Alumínio";
        public string NomeTipo => $"{LarguraMm:0} x {AlturaMm:0}";
    }

    /// <summary>
    /// Porta de elevador paramétrica simplificada (porta de pavimento, abertura central): a door family made from the
    /// Revit door template, everything locked to reference planes so it follows its parameters and the host wall:
    ///  • Largura / Altura (the template's) = vão livre; the opening cut is the template's;
    ///  • portal (jambs + head) on the hall face of the wall (the exterior face of the family), "Largura do portal"
    ///    on each side and above, 20 mm proud of the face;
    ///  • two panels meeting at the centre, 40 mm thick, set 30 mm back from the face, full height;
    ///  • aluminium sill 50 mm past each side, from 10 mm in front of the face to 120 mm into the shaft;
    ///  • botoeira (call panel) 80 × 250 mm beside the right jamb: "Afastamento da botoeira", "Altura da botoeira",
    ///    on/off per instance ("Botoeira");
    ///  • materials as type parameters (portal, panels, sill, botoeira), panels and portal on the door's subcategories;
    ///  • types 800 × 2000, 900 × 2100 and 1100 × 2100 (stretcher) when asked.
    /// The plan / face planes follow the real wall: the face plane is locked to the template wall's exterior face.
    /// </summary>
    public static class ElevadorGenerator
    {
        private const double Mm = 1.0 / 304.8;

        public static GenerationResult Generate(RevitApp app, Document project, ElevadorConfig cfg, Settings settings)
        {
            var res = new GenerationResult();
            string template = TemplateLocator.Find(app, TipoEsquadria.Porta, settings, out string searched);
            if (template == null)
                throw new InvalidOperationException("Não encontrei o modelo de família de porta (.rft) do Revit.\nProcurado em: " +
                    (string.IsNullOrEmpty(searched) ? "(nenhuma pasta de modelos)" : searched) +
                    "\n\nEm JA Famílias › Configurações, indique o arquivo (ex.: \"Metric Door.rft\").");
            res.TemplatePath = template;
            res.FamilyName = "JA Porta de Elevador " + Seguro(cfg.Codigo);
            res.TypeName = cfg.NomeTipo;
            Directory.CreateDirectory(settings.PastaFamilias);
            res.RfaPath = Path.Combine(settings.PastaFamilias, res.FamilyName + ".rfa");

            Logger.Info($"[JA Famílias] Gerando {res.FamilyName} a partir de {template}");
            Document fam = app.NewFamilyDocument(template);
            if (fam == null) throw new InvalidOperationException("O Revit não criou o documento de família a partir de " + template);
            try
            {
                var resolver = new ConstraintResolver();
                using (var t = new Transaction(fam, "JA Famílias - porta de elevador"))
                {
                    t.Start();
                    var o = t.GetFailureHandlingOptions();
                    o.SetFailuresPreprocessor(resolver);
                    t.SetFailureHandlingOptions(o);
                    new Builder(fam, cfg, res).Run();
                    t.Commit();
                }
                if (resolver.Resolvidas > 0)
                    res.Warnings.Add(resolver.Resolvidas + " restrição(ões) não puderam ser mantidas e foram removidas pelo Revit (detalhes no registro).");
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

        private static string Seguro(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Trim();
        }

        private sealed class Builder
        {
            private readonly Document _fam;
            private readonly FamilyManager _fm;
            private readonly ElevadorConfig _c;
            private readonly GenerationResult _res;
            private XYZ _u, _n;
            private double _z;                 // level elevation (feet)
            private ViewPlan _plan;
            private Level _level;
            private SketchPlane _sketch;
            private int _dimRow, _falhas, _travas;

            public Builder(Document fam, ElevadorConfig c, GenerationResult res) { _fam = fam; _fm = fam.FamilyManager; _c = c; _res = res; }

            private void Warn(string s) { _res.Warnings.Add(s); Logger.Warn("[JA Famílias] Porta de elevador: " + s); }

            // positions: x along u (feet), y along n (feet), z (feet)
            private XYZ P(double x, double y, double z) => _u * x + _n * y + XYZ.BasisZ * z;
            private double X(ReferencePlane rp) => rp.GetPlane().Origin.DotProduct(_u);
            private double Y(ReferencePlane rp) => rp.GetPlane().Origin.DotProduct(_n);

            public void Run()
            {
                // ------------------------------------------------ type, width, height
                if (_fm.CurrentType == null) _fm.NewType(_c.NomeTipo);
                else { try { _fm.RenameCurrentType(_c.NomeTipo); } catch (Exception ex) { Warn("Tipo não renomeado: " + ex.Message); } }
                FamilyParameter pW = FamUtil.Find(_fm, new[] { BuiltInParameter.FAMILY_WIDTH_PARAM, BuiltInParameter.DOOR_WIDTH, BuiltInParameter.GENERIC_WIDTH }, new[] { "Width", "Largura" });
                FamilyParameter pH = FamUtil.Find(_fm, new[] { BuiltInParameter.FAMILY_HEIGHT_PARAM, BuiltInParameter.DOOR_HEIGHT, BuiltInParameter.GENERIC_HEIGHT }, new[] { "Height", "Altura" });
                if (pW == null || pH == null) throw new InvalidOperationException("O modelo de porta não tem os parâmetros Largura e Altura.");
                _fm.Set(pW, _c.LarguraMm * Mm);
                _fm.Set(pH, _c.AlturaMm * Mm);
                _fam.Regenerate();

                // ------------------------------------------------ host wall, level, plan view
                Wall wall = new FilteredElementCollector(_fam).OfClass(typeof(Wall)).Cast<Wall>().FirstOrDefault();
                if (wall == null) throw new InvalidOperationException("O modelo de porta não tem parede hospedeira.");
                XYZ o = wall.Orientation;
                _n = new XYZ(o.X, o.Y, 0).GetLength() > 1e-6 ? new XYZ(o.X, o.Y, 0).Normalize() : new XYZ(0, -1, 0);
                _u = XYZ.BasisZ.CrossProduct(_n).Normalize();
                _level = new FilteredElementCollector(_fam).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).FirstOrDefault();
                _z = _level?.Elevation ?? 0;
                _plan = new FilteredElementCollector(_fam).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().FirstOrDefault(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan);
                if (_plan == null) throw new InvalidOperationException("O modelo de porta não tem planta.");
                _sketch = _level != null ? SketchPlane.Create(_fam, _level.Id) : SketchPlane.Create(_fam, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero));

                double? ext = null, jambL = null, jambR = null;
                Reference refExt = null, refJL = null, refJR = null;
                var plusU = new List<(double p, Reference r)>();
                var minusU = new List<(double p, Reference r)>();
                foreach (GeometryObject go in wall.get_Geometry(new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine }) ?? Enumerable.Empty<GeometryObject>())
                {
                    if (!(go is Solid so)) continue;
                    foreach (Face f in so.Faces)
                    {
                        if (!(f is PlanarFace pf)) continue;
                        double dn = pf.FaceNormal.DotProduct(_n), du = pf.FaceNormal.DotProduct(_u);
                        if (dn > 0.99) { double y = pf.Origin.DotProduct(_n); if (!ext.HasValue || y > ext.Value) { ext = y; refExt = pf.Reference; } }
                        else if (du > 0.99) plusU.Add((pf.Origin.DotProduct(_u), pf.Reference));
                        else if (du < -0.99) minusU.Add((pf.Origin.DotProduct(_u), pf.Reference));
                    }
                }
                if (plusU.Count >= 2 && minusU.Count >= 2)
                {
                    var l = plusU.OrderBy(x => x.p).First(); var r = minusU.OrderByDescending(x => x.p).First();
                    jambL = l.p; refJL = l.r; jambR = r.p; refJR = r.r;
                }
                if (!ext.HasValue) throw new InvalidOperationException("Não encontrei a face da parede do modelo.");
                if (!jambL.HasValue)
                {
                    Warn("Abertura da parede do modelo não encontrada: posição pela origem.");
                    jambL = -_c.LarguraMm * Mm / 2; jambR = _c.LarguraMm * Mm / 2;
                }
                _yCotas = ext.Value;
                _xCotas = jambL.Value - (_c.PortalMm + 300) * Mm;

                // ------------------------------------------------ the template's Left / Right / Center planes (they follow Largura)
                var verticais = new FilteredElementCollector(_fam).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>()
                    .Where(rp => { try { return Math.Abs(rp.GetPlane().Normal.DotProduct(_u)) > 0.999; } catch { return false; } }).ToList();
                ReferencePlane Perto(double x) => verticais.Where(rp => Math.Abs(X(rp) - x) < 2 * Mm).OrderBy(rp => Math.Abs(X(rp) - x)).FirstOrDefault();
                double xMid = (jambL.Value + jambR.Value) / 2;
                ReferencePlane left = Perto(jambL.Value), right = Perto(jambR.Value), center = Perto(xMid);
                if (left == null) left = PlanoX("JA - jamba esquerda", jambL.Value, refJL);
                if (right == null) right = PlanoX("JA - jamba direita", jambR.Value, refJR);
                if (center == null)
                {
                    center = PlanoX("JA - centro do vão", xMid, null);
                    try { Cota(true, left, center, right).AreSegmentsEqual = true; } catch (Exception ex) { _falhas++; Logger.Warn("[JA Famílias] EQ do centro: " + ex.Message); }
                }

                // the hall face of the wall: a plane locked to the template wall's exterior face (it follows the real wall)
                ReferencePlane face = PlanoY("JA - face do hall", ext.Value);
                try { _fam.FamilyCreate.NewAlignment(_plan, refExt, face.GetReference()); _travas++; }
                catch (Exception ex) { _falhas++; Warn("A face do portal não ficou presa à face da parede: confira em paredes de outra espessura (" + ex.Message + ")."); }

                // ------------------------------------------------ parameters
                FamilyParameter pPortal = Comprimento("Largura do portal", _c.PortalMm, false, GroupTypeId.Geometry);
                FamilyParameter pAfast = Comprimento("Afastamento da botoeira", _c.AfastamentoBotoeiraMm, false, GroupTypeId.Geometry);
                FamilyParameter pAltBot = Comprimento("Altura da botoeira", _c.AlturaBotoeiraMm, false, GroupTypeId.Geometry);
                FamilyParameter pTopo = Comprimento("JA - topo do portal", _c.AlturaMm + _c.PortalMm, false, GroupTypeId.Geometry);
                FamilyParameter pTopoBot = Comprimento("JA - topo da botoeira", _c.AlturaBotoeiraMm + 250, false, GroupTypeId.Geometry);
                Formula(pTopo, pH.Definition.Name + " + Largura do portal");
                Formula(pTopoBot, "Altura da botoeira + 250 mm");
                FamilyParameter pVisBot = null;
                try
                {
                    pVisBot = _fm.get_Parameter("Botoeira") ?? _fm.AddParameter("Botoeira", GroupTypeId.Graphics, SpecTypeId.Boolean.YesNo, true);
                    _fm.Set(pVisBot, _c.Botoeira ? 1 : 0);
                }
                catch (Exception ex) { Warn("Parâmetro Botoeira: " + ex.Message); }
                FamilyParameter mPortal = FamUtil.MaterialParam(_fm, "Material do portal", Mat(_c.MaterialPortal, new Color(196, 200, 204), 90), Warn);
                FamilyParameter mFolhas = FamUtil.MaterialParam(_fm, "Material das folhas", Mat(_c.MaterialFolhas, new Color(196, 200, 204), 90), Warn);
                FamilyParameter mSoleira = FamUtil.MaterialParam(_fm, "Material da soleira", Mat(_c.MaterialSoleira, new Color(170, 174, 178), 70), Warn);
                FamilyParameter mBot = FamUtil.MaterialParam(_fm, "Material da botoeira", Mat("Aço inox escovado", new Color(196, 200, 204), 90), Warn);

                // ------------------------------------------------ reference planes
                double xL = X(left), xR = X(right), xC = X(center), yF = Y(face);
                double lm = _c.PortalMm * Mm;
                ReferencePlane portalE = PlanoX("JA - portal esquerdo", xL - lm, null);
                ReferencePlane portalD = PlanoX("JA - portal direito", xR + lm, null);
                Rotular(Cota(true, portalE, left), pPortal);
                Rotular(Cota(true, right, portalD), pPortal);
                ReferencePlane solE = PlanoX("JA - soleira esquerda", xL - 50 * Mm, null);
                ReferencePlane solD = PlanoX("JA - soleira direita", xR + 50 * Mm, null);
                Travar(Cota(true, solE, left));
                Travar(Cota(true, right, solD));
                double xb = xR + lm + _c.AfastamentoBotoeiraMm * Mm;
                ReferencePlane bot1 = PlanoX("JA - botoeira", xb, null);
                ReferencePlane bot2 = PlanoX("JA - botoeira largura", xb + 80 * Mm, null);
                Rotular(Cota(true, portalD, bot1), pAfast);
                Travar(Cota(true, bot1, bot2));

                ReferencePlane frentePortal = PlanoY("JA - frente do portal", yF + 20 * Mm);
                ReferencePlane folhaFrente = PlanoY("JA - frente das folhas", yF - 30 * Mm);
                ReferencePlane folhaFundo = PlanoY("JA - fundo das folhas", yF - 70 * Mm);
                ReferencePlane solFrente = PlanoY("JA - frente da soleira", yF + 10 * Mm);
                ReferencePlane solFundo = PlanoY("JA - fundo da soleira", yF - 120 * Mm);
                ReferencePlane botFrente = PlanoY("JA - frente da botoeira", yF + 15 * Mm);
                foreach (var rp in new[] { frentePortal, folhaFrente, folhaFundo, solFrente, solFundo, botFrente }) Travar(Cota(false, face, rp));

                // ------------------------------------------------ solids
                double h = _c.AlturaMm * Mm, top = (_c.AlturaMm + _c.PortalMm) * Mm;
                Category catPortal = Subcategoria(new[] { "Frame/Mullion", "Moldura/Montante", "Moldura/montante", "Moldura / Montante", "Marco/Montante" });
                Category catPainel = Subcategoria(new[] { "Panel", "Painel" });

                Caixa("portal esquerdo", portalE, left, face, frentePortal, 0, top, null, pTopo, mPortal, null, catPortal);
                Caixa("portal direito", right, portalD, face, frentePortal, 0, top, null, pTopo, mPortal, null, catPortal);
                Caixa("portal superior", portalE, portalD, face, frentePortal, h, top, pH, pTopo, mPortal, null, catPortal);
                Caixa("folha esquerda", left, center, folhaFundo, folhaFrente, 0, h, null, pH, mFolhas, null, catPainel);
                Caixa("folha direita", center, right, folhaFundo, folhaFrente, 0, h, null, pH, mFolhas, null, catPainel);
                Caixa("soleira", solE, solD, solFundo, solFrente, 0, 5 * Mm, null, null, mSoleira, null, null);
                Caixa("botoeira", bot1, bot2, face, botFrente, _c.AlturaBotoeiraMm * Mm, (_c.AlturaBotoeiraMm + 250) * Mm, pAltBot, pTopoBot, mBot, pVisBot, null);

                // ------------------------------------------------ identity and the other types
                Identidade();
                if (_c.TiposExtras)
                {
                    string atual = _c.NomeTipo;
                    foreach (var (w, hh) in new[] { (800.0, 2000.0), (900.0, 2100.0), (1100.0, 2100.0) })
                    {
                        string nome = $"{w:0} x {hh:0}";
                        if (nome == atual) continue;
                        try
                        {
                            _fm.NewType(nome);
                            _fm.Set(pW, w * Mm);
                            _fm.Set(pH, hh * Mm);
                            Identidade();
                        }
                        catch (Exception ex) { Warn("Tipo " + nome + " não criado: " + ex.Message); }
                    }
                    // back to the asked type
                    foreach (FamilyType ft in _fm.Types) if (ft.Name == atual) { _fm.CurrentType = ft; break; }
                }
                _fam.Regenerate();
                Logger.Info($"[JA Famílias] Porta de elevador: {_travas} travamento(s), {_falhas} falha(s).");
                if (_falhas > 0)
                    Warn(_falhas + " travamento(s) não foram feitos: alguma peça pode não acompanhar a largura, a altura ou a parede — confira a família no editor (Tipos de família, mude a largura).");
            }

            private void Identidade()
            {
                try { _fm.Set(_fm.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_MARK), _c.Codigo); } catch { }
                try { _fm.Set(_fm.get_Parameter(BuiltInParameter.ALL_MODEL_DESCRIPTION), "Porta de elevador de pavimento, abertura central, portal e botoeira"); } catch { }
                try { _fm.Set(_fm.get_Parameter(BuiltInParameter.ALL_MODEL_MODEL), "Porta de elevador"); } catch { }
            }

            // ------------------------------------------------ parameters

            private FamilyParameter Comprimento(string nome, double mm, bool instancia, ForgeTypeId grupo)
            {
                try
                {
                    FamilyParameter p = _fm.get_Parameter(nome) ?? _fm.AddParameter(nome, grupo, SpecTypeId.Length, instancia);
                    _fm.Set(p, mm * Mm);
                    return p;
                }
                catch (Exception ex) { Warn("Parâmetro '" + nome + "' não criado: " + ex.Message); return null; }
            }

            private void Formula(FamilyParameter p, string formula)
            {
                if (p == null) return;
                try { _fm.SetFormula(p, formula); }
                catch (Exception ex) { _falhas++; Warn("Fórmula de '" + p.Definition.Name + "' recusada (" + formula + "): " + ex.Message); }
            }

            private ElementId Mat(string nome, Color cor, int brilho)
            {
                try
                {
                    Material m = new FilteredElementCollector(_fam).OfClass(typeof(Material)).Cast<Material>().FirstOrDefault(x => x.Name == nome);
                    if (m == null)
                    {
                        ElementId id = Autodesk.Revit.DB.Material.Create(_fam, nome);
                        m = _fam.GetElement(id) as Material;
                        if (m != null) { m.Color = cor; m.Shininess = brilho; m.Smoothness = 60; }
                    }
                    return m?.Id ?? ElementId.InvalidElementId;
                }
                catch (Exception ex) { Logger.Warn("[JA Famílias] Material " + nome + ": " + ex.Message); return ElementId.InvalidElementId; }
            }

            private Category Subcategoria(string[] nomes)
            {
                try
                {
                    Category cat = _fam.OwnerFamily.FamilyCategory;
                    foreach (Category s in cat.SubCategories)
                        if (nomes.Any(n => string.Equals(n, s.Name, StringComparison.OrdinalIgnoreCase))) return s;
                }
                catch { }
                return null;
            }

            // ------------------------------------------------ planes and dimensions

            /// <summary>A plane across the wall (normal u) at x; locked to a face when given.</summary>
            private ReferencePlane PlanoX(string nome, double x, Reference travarEm)
            {
                ReferencePlane rp = _fam.FamilyCreate.NewReferencePlane(P(x, 0, _z), P(x, 1, _z), XYZ.BasisZ, _plan);
                try { rp.Name = nome; } catch { }
                if (travarEm != null)
                {
                    try { _fam.FamilyCreate.NewAlignment(_plan, travarEm, rp.GetReference()); _travas++; }
                    catch (Exception ex) { _falhas++; Logger.Warn("[JA Famílias] " + nome + ": " + ex.Message); }
                }
                return rp;
            }

            /// <summary>A plane along the wall (normal n) at y.</summary>
            private ReferencePlane PlanoY(string nome, double y)
            {
                ReferencePlane rp = _fam.FamilyCreate.NewReferencePlane(P(0, y, _z), P(1, y, _z), XYZ.BasisZ, _plan);
                try { rp.Name = nome; } catch { }
                return rp;
            }

            /// <summary>Dimension between planes: across the wall (porX = planes of normal u) or along it.</summary>
            private Dimension Cota(bool porX, params ReferencePlane[] planos)
            {
                try
                {
                    var ra = new ReferenceArray();
                    foreach (var p in planos) ra.Append(p.GetReference());
                    double off = (0.6 + 0.15 * (_dimRow++ % 10)) / 0.3048;   // rows 60 cm + 15 cm apart
                    Line line = porX
                        ? Line.CreateBound(P(-50, _yCotas + off, _z), P(50, _yCotas + off, _z))     // along the wall, in front of the hall face
                        : Line.CreateBound(P(_xCotas - off, -50, _z), P(_xCotas - off, 50, _z));    // across the wall, left of the door
                    return _fam.FamilyCreate.NewDimension(_plan, line, ra);
                }
                catch (Exception ex) { _falhas++; Logger.Warn("[JA Famílias] Cota: " + ex.Message); return null; }
            }

            private double _yCotas, _xCotas;        // where the dimension rows go (feet): in front of the face, left of the door

            private void Rotular(Dimension d, FamilyParameter p)
            {
                if (d == null || p == null) { _falhas++; return; }
                try { d.FamilyLabel = p; _travas++; }
                catch (Exception ex) { _falhas++; Logger.Warn("[JA Famílias] Rótulo " + p.Definition.Name + ": " + ex.Message); }
            }

            private void Travar(Dimension d)
            {
                if (d == null) { _falhas++; return; }
                try { d.IsLocked = true; _travas++; }
                catch (Exception ex) { _falhas++; Logger.Warn("[JA Famílias] Cadeado: " + ex.Message); }
            }

            // ------------------------------------------------ solids

            /// <summary>
            /// A box drawn in plan between four planes (x0..x1 along the wall, y0..y1 across it), from z0 to z1 above the
            /// level; its four side faces locked to the planes, its bottom / top driven by parameters when given.
            /// </summary>
            private Extrusion Caixa(string nome, ReferencePlane x0, ReferencePlane x1, ReferencePlane y0, ReferencePlane y1,
                                    double z0, double z1, FamilyParameter pBase, FamilyParameter pTopo, FamilyParameter mat, FamilyParameter vis, Category sub)
            {
                try
                {
                    double ax = X(x0), bx = X(x1), ay = Y(y0), by = Y(y1);
                    if (ax > bx) { double t = ax; ax = bx; bx = t; }
                    if (ay > by) { double t = ay; ay = by; by = t; }
                    var loop = new CurveArray();
                    XYZ p1 = P(ax, ay, _z), p2 = P(bx, ay, _z), p3 = P(bx, by, _z), p4 = P(ax, by, _z);
                    loop.Append(Line.CreateBound(p1, p2)); loop.Append(Line.CreateBound(p2, p3));
                    loop.Append(Line.CreateBound(p3, p4)); loop.Append(Line.CreateBound(p4, p1));
                    var perfil = new CurveArrArray();
                    perfil.Append(loop);
                    Extrusion e = _fam.FamilyCreate.NewExtrusion(true, perfil, _sketch, z1);
                    e.StartOffset = z0;
                    e.EndOffset = z1;
                    Associar(e, BuiltInParameter.EXTRUSION_START_PARAM, pBase);
                    Associar(e, BuiltInParameter.EXTRUSION_END_PARAM, pTopo);
                    Associar(e, BuiltInParameter.MATERIAL_ID_PARAM, mat);
                    Associar(e, BuiltInParameter.IS_VISIBLE_PARAM, vis);
                    if (sub != null) { try { e.Subcategory = sub; } catch { } }
                    _fam.Regenerate();
                    // the side faces onto the planes
                    var faces = new List<PlanarFace>();
                    foreach (GeometryObject go in e.get_Geometry(new Options { ComputeReferences = true, IncludeNonVisibleObjects = true }) ?? Enumerable.Empty<GeometryObject>())
                        if (go is Solid so) foreach (Face f in so.Faces) if (f is PlanarFace pf && pf.Reference != null) faces.Add(pf);
                    Alinhar(faces, x0, _u, X(x0), nome);
                    Alinhar(faces, x1, _u, X(x1), nome);
                    Alinhar(faces, y0, _n, Y(y0), nome);
                    Alinhar(faces, y1, _n, Y(y1), nome);
                    _res.Solids++;
                    return e;
                }
                catch (Exception ex) { _falhas++; Warn("Peça '" + nome + "' não criada: " + ex.Message); return null; }
            }

            private void Associar(Element e, BuiltInParameter bip, FamilyParameter fp)
            {
                if (fp == null) return;
                try
                {
                    Parameter p = e.get_Parameter(bip);
                    if (p != null) { _fm.AssociateElementParameterToFamilyParameter(p, fp); _travas++; }
                }
                catch (Exception ex) { _falhas++; Logger.Warn("[JA Famílias] Associar " + fp.Definition.Name + ": " + ex.Message); }
            }

            /// <summary>The face of normal ±dir at the plane's position, aligned (locked) to the plane: in the plan, else in an elevation.</summary>
            private void Alinhar(List<PlanarFace> faces, ReferencePlane rp, XYZ dir, double pos, string nome)
            {
                PlanarFace f = faces.FirstOrDefault(x => Math.Abs(Math.Abs(x.FaceNormal.DotProduct(dir)) - 1) < 1e-6 && Math.Abs(x.Origin.DotProduct(dir) - pos) < 0.5 * Mm);
                if (f == null) { _falhas++; Logger.Warn("[JA Famílias] " + nome + ": face não encontrada para " + rp.Name); return; }
                var vistas = new List<View> { _plan };
                vistas.AddRange(new FilteredElementCollector(_fam).OfClass(typeof(View)).Cast<View>()
                    .Where(v => !v.IsTemplate && (v.ViewType == ViewType.Elevation || v.ViewType == ViewType.ThreeD) && v.Id != _plan.Id));
                foreach (View v in vistas)
                {
                    try { _fam.FamilyCreate.NewAlignment(v, rp.GetReference(), f.Reference); _travas++; return; }
                    catch { }
                }
                _falhas++;
                Logger.Warn("[JA Famílias] " + nome + ": face não alinhada a " + rp.Name);
            }
        }
    }
}
