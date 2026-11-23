using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using JAEsquadrias.Core;
using RevitApp = Autodesk.Revit.ApplicationServices.Application;
using Settings = JAEsquadrias.Core.Settings;

namespace JAEsquadrias.Engine
{
    /// <summary>
    /// Builds the Generic Model family of a bancada (free-standing), nicho (wall-based, its void
    /// cuts the wall) or chapim (line-based, follows the wall length) from its configuration.
    /// Same pipeline as the esquadrias: template → type → solids/voids with material parameters →
    /// symbolic plan lines → JA parameters (JA_Config keeps the full configuration) → .rfa → load.
    /// </summary>
    public static class PecaGenerator
    {
        private const double Mm = 1.0 / 304.8;
        /// <summary>Curved chapim: instance angle of the arc and type radius of the wall centre line.</summary>
        public const string ParamAngulo = "Ângulo do arco";
        public const string ParamRaio = "Raio do eixo";

        public static TemplateLocator.Hospedeiro HostOf(TipoPeca t) =>
            t == TipoPeca.Nicho ? TemplateLocator.Hospedeiro.Parede : t == TipoPeca.Chapim ? TemplateLocator.Hospedeiro.Linha : TemplateLocator.Hospedeiro.Livre;

        /// <summary>
        /// The family was already in the project as another category (made by an older version, e.g. a bancada
        /// as Generic Model): the old family is renamed, the new one (Casework) is loaded under the name, each
        /// free-standing copy is swapped for the new family in the same place and turn, and the old family goes.
        /// False when it cannot (hosted or line-based copies): then the caller warns.
        /// </summary>
        private static bool ConverterParaGabinete(Document project, GenerationResult res)
        {
            Family velha = res.Family;
            try
            {
                var copias = new FilteredElementCollector(project).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                    .Where(i => i.Symbol?.Family?.Id == velha.Id).ToList();
                if (copias.Any(i => i.Host != null || i.Location is LocationCurve)) return false;
                using (var t = new Transaction(project, "JA Famílias – converter para Componente de gabinete"))
                {
                    t.Start();
                    var op = t.GetFailureHandlingOptions(); op.SetFailuresPreprocessor(new WarningSwallower()); t.SetFailureHandlingOptions(op);
                    string nome = velha.Name;
                    velha.Name = nome + " (antiga)";
                    if (!project.LoadFamily(res.RfaPath, new OverwriteLoadOptions(), out Family nova) || nova == null ||
                        nova.FamilyCategory?.Id != new ElementId(BuiltInCategory.OST_Casework))
                    {
                        t.RollBack();
                        return false;
                    }
                    var simbolos = nova.GetFamilySymbolIds().Select(id => project.GetElement(id) as FamilySymbol).Where(x => x != null).ToList();
                    int espelhadas = 0;
                    foreach (FamilyInstance fi in copias)
                    {
                        FamilySymbol s = simbolos.FirstOrDefault(x => x.Name == fi.Symbol.Name) ?? simbolos.FirstOrDefault();
                        if (s == null) continue;
                        if (!s.IsActive) s.Activate();
                        Transform T = fi.GetTransform();
                        Level lv = project.GetElement(fi.LevelId) as Level;
                        FamilyInstance novo = lv != null
                            ? project.Create.NewFamilyInstance(T.Origin, s, lv, Autodesk.Revit.DB.Structure.StructuralType.NonStructural)
                            : project.Create.NewFamilyInstance(T.Origin, s, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                        project.Regenerate();
                        double ang = Math.Atan2(T.BasisX.Y, T.BasisX.X);
                        if (Math.Abs(ang) > 1e-9) ElementTransformUtils.RotateElement(project, novo.Id, Line.CreateBound(T.Origin, T.Origin + XYZ.BasisZ), ang);
                        project.Regenerate();
                        XYZ delta = T.Origin - novo.GetTransform().Origin;
                        if (delta.GetLength() > 1e-6) ElementTransformUtils.MoveElement(project, novo.Id, delta);
                        foreach (BuiltInParameter b in new[] { BuiltInParameter.ALL_MODEL_MARK, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS })
                        {
                            try { string v = fi.get_Parameter(b)?.AsString(); if (!string.IsNullOrEmpty(v)) novo.get_Parameter(b)?.Set(v); } catch { }
                        }
                        if (fi.Mirrored) espelhadas++;
                        project.Delete(fi.Id);
                    }
                    project.Delete(velha.Id);
                    t.Commit();
                    res.Family = nova;
                    res.Warnings.Add("A família " + nome + " era de outra categoria (versão antiga): virou Componente de gabinete e as " + copias.Count + " cópia(s) foram trocadas no mesmo lugar." +
                                     (espelhadas > 0 ? " " + espelhadas + " estava(m) espelhada(s): confira." : ""));
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("[JA Famílias] Conversão para Componente de gabinete: " + ex.Message);
                return false;
            }
        }

        public static GenerationResult Generate(RevitApp app, Document project, IPecaConfig cfg, Settings settings,
                                                string familyName = null, string typeName = null)
        {
            cfg = cfg.Copiar();
            var res = new GenerationResult();
            // curved chapim: a free family (revolution about the arc centre), not line-based
            var host = cfg is ChapimConfig curvo && curvo.Curvo ? TemplateLocator.Hospedeiro.Livre
                     : PecaUtil.Linear(cfg) ? TemplateLocator.Hospedeiro.Linha : HostOf(cfg.Tipo);
            string template = null, searched = "";
            // the glass plate of a Revit railing: the baluster panel template, else a generic one switched to Balaústres
            if (PecaUtil.Balaustre(cfg)) template = TemplateLocator.FindBalusterPanel(app, out searched);
            if (template == null) template = TemplateLocator.FindGeneric(app, host, settings, out searched);
            if (template == null)
            {
                string which = host == TemplateLocator.Hospedeiro.Parede ? "Metric Generic Model wall based.rft"
                             : host == TemplateLocator.Hospedeiro.Linha ? "Metric Generic Model line based.rft" : "Metric Generic Model.rft";
                throw new InvalidOperationException("Não encontrei o modelo de família “" + which + "” (ou equivalente em português).\n" +
                    "Procurado em: " + (string.IsNullOrEmpty(searched) ? "(nenhuma pasta de modelos)" : searched) + "\n\nIndique o arquivo em Configurações.");
            }
            res.TemplatePath = template;
            res.FamilyName = FamilyGenerator.SafeName(string.IsNullOrWhiteSpace(familyName) ? PecaUtil.FamilyName(cfg) : familyName);
            res.TypeName = string.IsNullOrWhiteSpace(typeName) ? cfg.Codigo : typeName;
            Directory.CreateDirectory(settings.PastaFamilias);
            res.RfaPath = Path.Combine(settings.PastaFamilias, res.FamilyName + ".rfa");

            Logger.Info($"[JA Famílias] Gerando {res.FamilyName} a partir de {template}");
            Document fam = app.NewFamilyDocument(template);
            if (fam == null) throw new InvalidOperationException("O Revit não criou o documento de família a partir de " + template);
            PecaModelo model = cfg.Build();
            try
            {
                SharedParams.WithDefinitions(app, defs =>
                {
                    using (var t = new Transaction(fam, "JA Famílias"))
                    {
                        t.Start();
                        var opts = t.GetFailureHandlingOptions();
                        opts.SetFailuresPreprocessor(new WarningSwallower());
                        t.SetFailureHandlingOptions(opts);
                        new Builder(fam, cfg, model, settings, defs, res).Run();
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
            if (res.Family == null) throw new InvalidOperationException("A família foi gerada (" + res.RfaPath + ") mas não foi carregada no projeto.");
            if (PecaUtil.Tubo(cfg.Tipo) && res.Family.FamilyCategory?.Id != new ElementId(BuiltInCategory.OST_PipeFitting))
                res.Warnings.Add("A família " + res.FamilyName + " continua como " + (res.Family.FamilyCategory?.Name ?? "outra categoria") +
                                 " no projeto (ela já existia assim). Exclua-a do projeto e gere de novo para ela virar Conexão de tubo.");
            if (PecaUtil.Balaustre(cfg) && res.Family.FamilyCategory?.Id != new ElementId(BuiltInCategory.OST_StairsRailingBaluster))
                res.Warnings.Add("A placa " + res.FamilyName + " não ficou na categoria Balaústres (" + (res.Family.FamilyCategory?.Name ?? "?") + ").");
            if (PecaUtil.Gabinete(cfg.Tipo) && res.Family.FamilyCategory?.Id != new ElementId(BuiltInCategory.OST_Casework)
                && !ConverterParaGabinete(project, res))
                res.Warnings.Add("A família " + res.FamilyName + " continua como " + (res.Family.FamilyCategory?.Name ?? "outra categoria") +
                                 " no projeto (ela já existia assim). Exclua-a do projeto e gere de novo para ela virar Componente de gabinete.");
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

        // ==========================================================================================
        private sealed class Builder
        {
            private readonly Document _fam;
            private readonly FamilyManager _fm;
            private readonly IPecaConfig _c;
            private readonly PecaModelo _m;
            private readonly Settings _s;
            private readonly Dictionary<Guid, ExternalDefinition> _defs;
            private readonly GenerationResult _res;

            // local (mm) → model (feet): origin + X·ax + Y·ay + Z·az
            private XYZ _o = XYZ.Zero, _ax = XYZ.BasisX, _ay = XYZ.BasisY, _az = XYZ.BasisZ;
            private FamilyParameter _length;
            private Wall _hostWall;
            // curved chapim
            public const string PAngulo = ParamAngulo;
            public const string PRaio = ParamRaio;
            private FamilyParameter _angulo, _raio;
            private bool Curvo => _c is ChapimConfig ch && ch.Curvo;

            public Builder(Document fam, IPecaConfig c, PecaModelo m, Settings s, Dictionary<Guid, ExternalDefinition> defs, GenerationResult res)
            {
                _fam = fam; _fm = fam.FamilyManager; _c = c; _m = m; _s = s; _defs = defs; _res = res;
            }

            private XYZ M(V3 p) => _o + _ax * (p.X * Mm) + _ay * (p.Y * Mm) + _az * (p.Z * Mm);
            private XYZ Dir(V3 v) => (_ax * v.X + _ay * v.Y + _az * v.Z).Normalize();

            public void Run()
            {
                SetCategory();
                if (_fm.CurrentType == null) _fm.NewType(_res.TypeName);
                else { try { _fm.RenameCurrentType(_res.TypeName); } catch (Exception ex) { Warn("Tipo não renomeado: " + ex.Message); } }

                if (_c.Tipo == TipoPeca.Nicho) SetupWall();
                FamilyParameter length = PecaUtil.Linear(_c) ? SetupLine() : null;
                if (Curvo) SetupArco();
                _length = length;
                foreach (string a in _m.Avisos) Warn(a);

                // materials (by name: a project material with the same name is used on load)
                var slots = new Dictionary<string, FamilyParameter>();
                foreach (var md in _c.Materiais())
                {
                    if (!_m.Pecas.Any(p => p.Slot == md.Key && !p.Void)) continue;
                    slots[md.Key] = MaterialParam(md.Parametro, MakeMaterial(md.Nome, md.Hex, md.Transparencia));
                }

                int failed = 0;
                var voids = new List<Extrusion>();
                var solids = new List<Extrusion>();
                var tubos = new List<(Peca p, Extrusion ex)>();
                foreach (var p0 in _m.Pecas)
                {
                    Peca p = p0;
                    if (p.Revolucao)
                    {
                        try
                        {
                            Revolution rv = MakeRevolution(p);
                            if (rv == null) { failed++; continue; }
                            _res.Solids++;
                            if (slots.TryGetValue(p.Slot, out FamilyParameter rfp) && rfp != null)
                            {
                                Parameter mp = rv.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
                                if (mp != null) { try { _fm.AssociateElementParameterToFamilyParameter(mp, rfp); } catch { } }
                            }
                            _res.AnguloParametrico = LigarAngulo(rv);
                            if (!_res.AnguloParametrico) Logger.Warn("[JA Famílias] Chapim curvo: ângulo da revolução fixo em " + p.AnguloGraus + "°.");
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            Logger.Warn($"[JA Famílias] Chapim curvo não criado: {ex.Message}");
                        }
                        continue;
                    }
                    if (p.Topo != null)
                    {
                        Blend bl = null;
                        try { bl = MakeBlend(p); }
                        catch (Exception ex) { Logger.Warn($"[JA Famílias] Mescla '{p.Nome}' não criada ({ex.Message}); usando a peça reta."); }
                        if (bl != null)
                        {
                            _res.Solids++;
                            if (slots.TryGetValue(p.Slot, out FamilyParameter bfp) && bfp != null)
                            {
                                Parameter mp = bl.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
                                if (mp != null) { try { _fm.AssociateElementParameterToFamilyParameter(mp, bfp); } catch { } }
                            }
                            continue;
                        }
                        if (p.Alternativa == null) { failed++; continue; }
                        p = p.Alternativa;      // the straight wedge
                    }
                    try
                    {
                        Extrusion ex = MakeExtrusion(p);
                        if (ex == null) continue;
                        _res.Solids++;
                        if (p.Void) voids.Add(ex); else solids.Add(ex);
                        if (p.Conector != null && !p.Void) tubos.Add((p, ex));
                        if (!p.Void && slots.TryGetValue(p.Slot, out FamilyParameter fp) && fp != null)
                        {
                            Parameter mp = ex.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
                            if (mp != null) { try { _fm.AssociateElementParameterToFamilyParameter(mp, fp); } catch { } }
                        }
                        if (p.AcompanhaComprimento && length != null)
                        {
                            Parameter end = ex.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM);
                            try { if (end != null) _fm.AssociateElementParameterToFamilyParameter(end, length); }
                            catch (Exception e) { Warn("O chapim não ficou ligado ao comprimento da linha: " + e.Message); }
                        }
                        if (!p.Void && (!(p.Coarse && p.Medium && p.Fine) || p.OcultarEmPlanta))
                        {
                            var vis = new FamilyElementVisibility(FamilyElementVisibilityType.Model)
                            {
                                IsShownInCoarse = p.Coarse, IsShownInMedium = p.Medium, IsShownInFine = p.Fine
                            };
                            if (p.OcultarEmPlanta) { vis.IsShownInPlanRCPCut = false; vis.IsShownInTopBottom = false; }
                            ex.SetVisibility(vis);
                        }
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        Logger.Warn($"[JA Famílias] Peça '{p.Nome}' não criada: {ex.Message}");
                    }
                }
                if (failed > 0) Warn($"{failed} peça(s) não puderam ser modeladas (detalhes no registro).");

                // the niche void must CUT the template's wall (Revit's "Cortar geometria"): only then
                // it cuts the real wall in the project. Needs the new geometry regenerated first.
                if (voids.Count > 0)
                {
                    if (_hostWall == null) Warn("O modelo de família não tem parede hospedeira: o vazio do nicho não vai cortar a parede.");
                    else
                    {
                        try { _fam.Regenerate(); } catch (Exception ex) { Logger.Warn("[JA Famílias] Regenerar antes do corte: " + ex.Message); }
                        int cut = voids.Count(v => CutHost(v));
                        if (cut < voids.Count) Warn("O vazio do nicho não cortou a parede do modelo de família (detalhes no registro); o JA Famílias tenta cortar as paredes no projeto.");
                    }
                    // the void cuts the wall, not the niche's own linings, base and shelves
                    try { _fam.Regenerate(); } catch { }
                    VoidGuard.Uncut(_fam, voids, solids);
                }

                try { Conectores(tubos); } catch (Exception ex) { Warn("Conectores de tubo não criados: " + ex.Message); }
                try { PlanLines(); } catch (Exception ex) { Warn("Linhas de planta não criadas: " + ex.Message); }
                try { Data(); } catch (Exception ex) { Warn("Parâmetros de dados incompletos: " + ex.Message); }
            }

            // ------------------------------------------------------------------ hosts
            /// <summary>Wall-based: the face the family sits on, the direction into the wall, along the wall.</summary>
            private void SetupWall()
            {
                Wall wall = new FilteredElementCollector(_fam).OfClass(typeof(Wall)).Cast<Wall>().FirstOrDefault();
                _hostWall = wall;
                XYZ outward = XYZ.BasisY;
                double faceOffset = 0;
                bool found = false;
                if (wall != null)
                {
                    try
                    {
                        GeometryElement geo = wall.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine });
                        double best = double.MaxValue;
                        if (geo != null)
                            foreach (GeometryObject go in geo)
                            {
                                if (!(go is Solid solid) || solid.Faces.Size == 0) continue;
                                foreach (Face f in solid.Faces)
                                {
                                    if (!(f is PlanarFace pf) || Math.Abs(pf.FaceNormal.Z) > 0.01) continue;
                                    XYZ nrm = pf.FaceNormal;
                                    double off = pf.Origin.DotProduct(nrm);
                                    // the face the family is placed on goes through (or next to) the origin
                                    if (Math.Abs(off) < best && (Math.Abs(nrm.X) > 0.99 || Math.Abs(nrm.Y) > 0.99))
                                    {
                                        // big faces only (skip the wall ends)
                                        if (pf.Area < 1.0) continue;
                                        best = Math.Abs(off);
                                        outward = nrm;
                                        faceOffset = off;
                                        found = true;
                                    }
                                }
                            }
                    }
                    catch (Exception ex) { Logger.Warn("[JA Famílias] Parede do modelo ilegível: " + ex.Message); }
                }
                if (!found) Warn("Não encontrei a face da parede no modelo de família: o nicho foi posicionado pela origem.");
                _ax = XYZ.BasisZ.CrossProduct(outward).Normalize();   // along the wall, to the right seen from the face
                _ay = outward.Negate();                               // into the wall
                _az = XYZ.BasisZ;
                _o = outward * faceOffset;

                if (_c is NichoConfig n)
                {
                    FamilyParameter elev = Find(new[] { "Default Elevation", "Elevação padrão", "Elevação Padrão", "Elevação predefinida" });
                    if (elev == null) elev = _fm.Parameters.Cast<FamilyParameter>().FirstOrDefault(p => (p.Definition?.Name ?? "").IndexOf("elev", StringComparison.OrdinalIgnoreCase) >= 0 && p.StorageType == StorageType.Double);
                    if (elev != null) { try { _fm.Set(elev, n.AlturaBaseMm * Mm); } catch (Exception ex) { Warn("Altura da base não aplicada: " + ex.Message); } }
                    else Warn("O modelo de família não tem “Elevação padrão”: ajuste a Elevação de cada nicho no Revit.");
                }
            }

            /// <summary>Curved chapim: instance angle of the arc and the (informative) radius of the wall centre line.</summary>
            private void SetupArco()
            {
                var ch = (ChapimConfig)_c;
                try
                {
                    _angulo = _fm.get_Parameter(PAngulo) ?? _fm.AddParameter(PAngulo, GroupTypeId.Geometry, SpecTypeId.Angle, true);
                    _fm.Set(_angulo, ch.AnguloGraus * Math.PI / 180);
                }
                catch (Exception ex) { _angulo = null; Warn("Parâmetro “" + PAngulo + "” não criado: " + ex.Message); }
                try
                {
                    _raio = _fm.get_Parameter(PRaio) ?? _fm.AddParameter(PRaio, GroupTypeId.Geometry, SpecTypeId.Length, false);
                    _fm.Set(_raio, ch.RaioMm * Mm);
                }
                catch (Exception ex) { _raio = null; Logger.Warn("[JA Famílias] " + PRaio + ": " + ex.Message); }
            }

            /// <summary>Profile in the vertical radial plane (local X = radius, Z up) turned about the local Z axis.</summary>
            private Revolution MakeRevolution(Peca p)
            {
                var arr = new CurveArrArray();
                foreach (var raw in p.Loops)
                {
                    var loop = FamUtil.Clean(raw);
                    if (loop.Count < 3) continue;
                    var ca = new CurveArray();
                    for (int i = 0; i < loop.Count; i++)
                    {
                        P2 a = loop[i], b = loop[(i + 1) % loop.Count];
                        ca.Append(Line.CreateBound(M(p.P(a)), M(p.P(b))));
                    }
                    arr.Append(ca);
                }
                if (arr.Size == 0) return null;
                XYZ u = Dir(p.U), v = Dir(p.V);
                SketchPlane sp = SketchPlane.Create(_fam, Plane.CreateByNormalAndOrigin(u.CrossProduct(v).Normalize(), M(p.O)));
                Line axis = Line.CreateBound(M(p.O), M(p.O) + v);
                return _fam.FamilyCreate.NewRevolution(true, arr, sp, axis, 0, p.AnguloGraus * Math.PI / 180);
            }

            /// <summary>Revolution end angle = "Ângulo do arco" (instance): one family serves every arc of that radius.</summary>
            private bool LigarAngulo(Revolution rv)
            {
                if (_angulo == null) return false;
                Parameter end = null;
                if (Enum.TryParse("REVOLUTION_END_ANGLE", out BuiltInParameter bip)) { try { end = rv.get_Parameter(bip); } catch { } }
                if (end == null) end = rv.LookupParameter("End Angle") ?? rv.LookupParameter("Ângulo final") ?? rv.LookupParameter("Ângulo de fim");
                if (end == null) return false;
                try { _fm.AssociateElementParameterToFamilyParameter(end, _angulo); return true; }
                catch (Exception ex) { Logger.Warn("[JA Famílias] Ângulo do chapim curvo não ligado: " + ex.Message); return false; }
            }

            /// <summary>Line-based: the length parameter the chapim follows.</summary>
            private FamilyParameter SetupLine()
            {
                FamilyParameter len = Find(new[] { "Length", "Comprimento", "Longitud" });
                if (len == null) Warn("O modelo baseado em linha não tem o parâmetro Comprimento: o chapim terá comprimento fixo.");
                else
                {
                    double def = _c is ChapimConfig ch ? ch.ComprimentoMm : _c is PedraConfig pr ? Math.Max(1000, pr.ComprimentoMm) : _c is VidroConfig vc ? vc.ComprimentoMm : 1000;
                    try { _fm.Set(len, def * Mm); } catch { }
                }
                return len;
            }

            private FamilyParameter Find(string[] names)
            {
                foreach (FamilyParameter p in _fm.Parameters)
                    if (names.Any(n => string.Equals(p.Definition?.Name, n, StringComparison.OrdinalIgnoreCase))) return p;
                return null;
            }

            // ------------------------------------------------------------------ solids
            private Extrusion MakeExtrusion(Peca p)
            {
                double len = p.Depth * Mm;
                if (len < 0.5 * Mm) return null;
                XYZ u = Dir(p.U), v = Dir(p.V);
                XYZ n = u.CrossProduct(v).Normalize();
                var arr = new CurveArrArray();
                if (p.Conector != null)
                {
                    // pipe stub: a true circle (two arcs), so the connector sits on a round face
                    XYZ c0 = M(p.O);
                    double r = p.Conector.DiametroMm / 2 * Mm;
                    var circ = new CurveArray();
                    circ.Append(Arc.Create(c0, r, 0, Math.PI, u, v));
                    circ.Append(Arc.Create(c0, r, Math.PI, 2 * Math.PI, u, v));
                    arr.Append(circ);
                    SketchPlane spc = SketchPlane.Create(_fam, Plane.CreateByNormalAndOrigin(n, c0));
                    return _fam.FamilyCreate.NewExtrusion(true, arr, spc, len);
                }
                foreach (var raw in p.Loops)
                {
                    var loop = FamUtil.Clean(raw);
                    if (loop.Count < 3) continue;
                    var ca = new CurveArray();
                    for (int i = 0; i < loop.Count; i++)
                    {
                        P2 a = loop[i], b = loop[(i + 1) % loop.Count];
                        ca.Append(Line.CreateBound(M(p.P(a)), M(p.P(b))));
                    }
                    arr.Append(ca);
                }
                if (arr.Size == 0) return null;
                SketchPlane sp = SketchPlane.Create(_fam, Plane.CreateByNormalAndOrigin(n, M(p.O)));
                return _fam.FamilyCreate.NewExtrusion(!p.Void, arr, sp, len);
            }

            /// <summary>Blend from the base profile (Loops[0]) to the top profile (Topo), Depth apart along N.</summary>
            private Blend MakeBlend(Peca p)
            {
                var b0 = FamUtil.Clean(p.Loops[0]);
                if (b0.Count != p.Loops[0].Count || b0.Count != p.Topo.Count || b0.Count < 3) return null;
                XYZ u = Dir(p.U), v = Dir(p.V);
                XYZ n = u.CrossProduct(v).Normalize();
                XYZ up = n * (p.Depth * Mm);
                CurveArray Perfil(List<P2> l, XYZ d)
                {
                    var ca = new CurveArray();
                    for (int i = 0; i < l.Count; i++)
                    {
                        XYZ a = M(p.P(l[i])) + d, b = M(p.P(l[(i + 1) % l.Count])) + d;
                        if (a.DistanceTo(b) < _fam.Application.ShortCurveTolerance) return null;
                        ca.Append(Line.CreateBound(a, b));
                    }
                    return ca;
                }
                CurveArray baixo = Perfil(b0, XYZ.Zero), alto = Perfil(p.Topo, up);
                if (baixo == null || alto == null) return null;
                SketchPlane sp = SketchPlane.Create(_fam, Plane.CreateByNormalAndOrigin(n, M(p.O)));
                return _fam.FamilyCreate.NewBlend(!p.Void, alto, baixo, sp);
            }

            /// <summary>Makes the void cut the template's wall (so it cuts the real wall when hosted).</summary>
            private bool CutHost(Extrusion v)
            {
                try
                {
                    try { if (SolidSolidCutUtils.CutExistsBetweenElements(v, _hostWall, out _)) return true; } catch { }
                    if (!SolidSolidCutUtils.CanElementCutElement(v, _hostWall, out CutFailureReason why))
                        Logger.Warn("[JA Famílias] O vazio não pode cortar a parede do modelo: " + why);
                    SolidSolidCutUtils.AddCutBetweenSolids(_fam, _hostWall, v);
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Warn("[JA Famílias] Vazio do nicho x parede: " + ex.Message);
                    return false;
                }
            }

            /// <summary>Nichos and chapins: category Casework (Componentes de gabinete), whatever the template.
            /// Ralos: Pipe Fittings (Conexões de tubo), part type Multi Port (any number of connectors).</summary>
            private void SetCategory()
            {
                if (PecaUtil.Tubo(_c.Tipo))
                {
                    try
                    {
                        Category pf = _fam.Settings.Categories.get_Item(BuiltInCategory.OST_PipeFitting);
                        if (pf != null && _fam.OwnerFamily.FamilyCategory?.Id != pf.Id) _fam.OwnerFamily.FamilyCategory = pf;
                    }
                    catch (Exception ex) { Warn("Não deu para mudar a categoria para Conexões de tubo: " + ex.Message); }
                    try
                    {
                        Parameter pt = _fam.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_CONTENT_PART_TYPE);
                        if (pt != null && !pt.IsReadOnly && Enum.TryParse("MultiPort", out PartType multi)) pt.Set((int)multi);
                    }
                    catch (Exception ex) { Logger.Warn("[JA Famílias] Tipo de peça (Multi Port): " + ex.Message); }
                    return;
                }
                if (PecaUtil.Balaustre(_c))
                {
                    try
                    {
                        Category bl = _fam.Settings.Categories.get_Item(BuiltInCategory.OST_StairsRailingBaluster);
                        if (bl != null && _fam.OwnerFamily.FamilyCategory?.Id != bl.Id) _fam.OwnerFamily.FamilyCategory = bl;
                    }
                    catch (Exception ex) { Warn("Não deu para pôr a placa na categoria Balaústres: " + ex.Message); }
                    return;
                }
                if (!PecaUtil.Gabinete(_c.Tipo)) return;
                try
                {
                    Category cw = _fam.Settings.Categories.get_Item(BuiltInCategory.OST_Casework);
                    if (cw != null && _fam.OwnerFamily.FamilyCategory?.Id != cw.Id) _fam.OwnerFamily.FamilyCategory = cw;
                }
                catch (Exception ex) { Warn("Não deu para mudar a categoria para Componentes de gabinete: " + ex.Message); }
                if (_c.Tipo == TipoPeca.Nicho)
                {
                    // lets the project fall back to "cut with voids" if the family-level cut fails
                    try { _fam.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_ALLOW_CUT_WITH_VOIDS)?.Set(1); } catch { }
                }
            }

            // ------------------------------------------------------------------ pipe connectors
            /// <summary>One pipe connector on the far face of each pipe stub (outlet: flow out; inlet: in),
            /// with its diameter and the system (esgoto = Sanitary; pluvial = Storm if the API has it).</summary>
            private void Conectores(List<(Peca p, Extrusion ex)> tubos)
            {
                if (tubos.Count == 0) return;
                try { _fam.Regenerate(); } catch (Exception ex) { Logger.Warn("[JA Famílias] Regenerar antes dos conectores: " + ex.Message); }
                PipeSystemType sistema = Sistema();
                int feitos = 0;
                foreach (var (p, ex) in tubos)
                {
                    XYZ n = Dir(p.N);
                    XYZ fim = M(p.O + p.N.Unit() * p.Depth);
                    Reference face = FaceNaPonta(ex, n, fim);
                    if (face == null) { Logger.Warn("[JA Famílias] Face da ponta não achada: " + p.Nome); continue; }
                    try
                    {
                        ConnectorElement ce = ConnectorElement.CreatePipeConnector(_fam, sistema, face);
                        try { ce.get_Parameter(BuiltInParameter.CONNECTOR_DIAMETER)?.Set(p.Conector.DiametroMm * Mm); } catch { }
                        try
                        {
                            if (Enum.TryParse("RBS_PIPE_FLOW_DIRECTION_PARAM", out BuiltInParameter fdp))
                            {
                                Parameter fd = ce.get_Parameter(fdp);
                                if (fd != null && !fd.IsReadOnly) fd.Set((int)(p.Conector.Saida ? FlowDirectionType.Out : FlowDirectionType.In));
                            }
                        }
                        catch { }
                        feitos++;
                    }
                    catch (Exception e) { Logger.Warn("[JA Famílias] Conector de " + p.Nome + ": " + e.Message); }
                }
                if (feitos < tubos.Count)
                    Warn($"{tubos.Count - feitos} de {tubos.Count} conector(es) de tubo não foram criados (detalhes no registro); ligue a tubulação manualmente ou gere de novo.");
            }

            private PipeSystemType Sistema()
            {
                var r = _c as RaloConfig;
                string[] nomes = r != null && r.Sistema == SistemaRalo.Pluvial ? new[] { "Storm", "OtherPipe", "Sanitary" } : new[] { "Sanitary" };
                foreach (string nm in nomes)
                    if (Enum.TryParse(nm, out PipeSystemType t)) return t;
                return PipeSystemType.Sanitary;
            }

            private static Reference FaceNaPonta(Extrusion ex, XYZ n, XYZ fim)
            {
                var opt = new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine };
                GeometryElement geo = ex.get_Geometry(opt);
                if (geo == null) return null;
                foreach (GeometryObject go in geo)
                {
                    if (!(go is Solid s) || s.Faces.Size == 0) continue;
                    foreach (Face f in s.Faces)
                        if (f is PlanarFace pf && pf.FaceNormal.IsAlmostEqualTo(n, 1e-3) && Math.Abs((pf.Origin - fim).DotProduct(n)) < 1e-3 && pf.Reference != null)
                            return pf.Reference;
                }
                return null;
            }

            private void PlanLines()
            {
                if (_m.Planta.Count == 0) return;
                GraphicsStyle solid = Style("JA Planta", false, 1);
                GraphicsStyle dashed = Style("JA Planta (tracejada)", true, 1) ?? solid;
                SketchPlane plan = SketchPlane.Create(_fam, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, M(V3.Zero)));
                foreach (var l in _m.Planta)
                {
                    XYZ a = M(new V3(l.A.X, l.A.Y, 0)), b = M(new V3(l.B.X, l.B.Y, 0));
                    a = new XYZ(a.X, a.Y, _o.Z);
                    b = new XYZ(b.X, b.Y, _o.Z);
                    if (a.DistanceTo(b) < 1.5 * Mm) continue;
                    try
                    {
                        SymbolicCurve sc = _fam.FamilyCreate.NewSymbolicCurve(Line.CreateBound(a, b), plan);
                        GraphicsStyle gs = l.Tracejada ? dashed : solid;
                        if (sc != null && gs != null) sc.Subcategory = gs;
                    }
                    catch (Exception ex) { Logger.Warn("[JA Famílias] Linha de planta: " + ex.Message); }
                }
            }

            // ------------------------------------------------------------------ materials / styles

            private FamilyParameter MaterialParam(string name, ElementId material) => FamUtil.MaterialParam(_fm, name, material, Warn);
            private FamilyParameter Shared(SharedParams.Def d) => FamUtil.Shared(_fm, _defs, d);

            private ElementId MakeMaterial(string name, string hex, int transparency)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(name)) return ElementId.InvalidElementId;
                    var mat = new FilteredElementCollector(_fam).OfClass(typeof(Autodesk.Revit.DB.Material)).Cast<Autodesk.Revit.DB.Material>().FirstOrDefault(m => m.Name == name);
                    if (mat == null) mat = _fam.GetElement(Autodesk.Revit.DB.Material.Create(_fam, name)) as Autodesk.Revit.DB.Material;
                    if (mat == null) return ElementId.InvalidElementId;
                    mat.Color = FamUtil.ToColor(hex);
                    mat.Transparency = Math.Max(0, Math.Min(100, transparency));
                    return mat.Id;
                }
                catch (Exception ex) { Warn($"Material '{name}' não criado: {ex.Message}"); return ElementId.InvalidElementId; }
            }

            private GraphicsStyle Style(string name, bool dashed, int weight)
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
                        var pat = new FilteredElementCollector(_fam).OfClass(typeof(LinePatternElement)).Cast<LinePatternElement>()
                            .FirstOrDefault(p => { string nm = p.Name.ToLowerInvariant(); return nm.StartsWith("dash") || nm.StartsWith("tracej") || nm.StartsWith("hidden") || nm.StartsWith("oculta"); });
                        ElementId pid = pat?.Id ?? ElementId.InvalidElementId;
                        if (pid == ElementId.InvalidElementId)
                        {
                            var lp = new LinePattern("JA Tracejado");
                            lp.SetSegments(new List<LinePatternSegment> { new LinePatternSegment(LinePatternSegmentType.Dash, 2.5 * Mm), new LinePatternSegment(LinePatternSegmentType.Space, 1.5 * Mm) });
                            pid = LinePatternElement.Create(_fam, lp).Id;
                        }
                        sub.SetLinePatternId(pid, GraphicsStyleType.Projection);
                    }
                    return sub.GetGraphicsStyle(GraphicsStyleType.Projection);
                }
                catch (Exception ex) { Logger.Warn("[JA Famílias] Subcategoria " + name + ": " + ex.Message); return null; }
            }

            // ------------------------------------------------------------------ data

            private void SetText(FamilyParameter p, string v)
            {
                if (p == null) return;
                try { _fm.Set(p, v ?? ""); } catch (Exception ex) { Logger.Warn($"[JA Famílias] {p.Definition?.Name}: {ex.Message}"); }
            }

            private void SetBuiltIn(BuiltInParameter bip, string v)
            {
                FamilyParameter p = null;
                try { p = _fm.get_Parameter(bip); } catch { }
                SetText(p, v);
            }

            private void Data()
            {
                SetText(Shared(SharedParams.Config), _c.ToJson());
                SetText(Shared(SharedParams.Familia), PecaUtil.NomeTipo(_c.Tipo));
                SetText(Shared(SharedParams.Material), _c.MaterialPrincipal());
                SetText(Shared(SharedParams.Dimensoes), _c.Dimensoes());
                var area = PecaUtil.Tubo(_c.Tipo) ? null : Shared(SharedParams.AreaPedra);
                if (area != null) { try { _fm.Set(area, UnitUtils.ConvertToInternalUnits(_m.AreaPedra, UnitTypeId.SquareMeters)); } catch { } }
                // bancadas with a wet area: its top area, for the schedule
                if (_m.AreaMolhada > 0)
                {
                    try
                    {
                        var am = Shared(SharedParams.AreaMolhada);
                        if (am != null) _fm.Set(am, UnitUtils.ConvertToInternalUnits(_m.AreaMolhada, UnitTypeId.SquareMeters));
                    }
                    catch (Exception ex) { Logger.Warn("[JA Famílias] JA_AreaMolhada: " + ex.Message); }
                }

                if (Curvo)
                {
                    // instance length along the wall centre line = radius × angle (schedules: metres of chapim)
                    try
                    {
                        FamilyParameter comp = null;
                        try { comp = _fm.get_Parameter(SharedParams.Comprimento.Guid); } catch { }
                        if (comp == null && _defs.TryGetValue(SharedParams.Comprimento.Guid, out ExternalDefinition cd))
                            comp = _fm.AddParameter(cd, GroupTypeId.Data, true);
                        if (comp != null && _raio != null && _angulo != null && _res.AnguloParametrico)
                        {
                            try { _fm.SetFormula(comp, PRaio + " * " + PAngulo + " / 180° * pi()"); }
                            catch (Exception ex) { Logger.Warn("[JA Famílias] JA_Comprimento do chapim curvo (fórmula): " + ex.Message); }
                        }
                    }
                    catch (Exception ex) { Logger.Warn("[JA Famílias] JA_Comprimento: " + ex.Message); }
                }
                if (_length != null)
                {
                    // instance length that follows the line, for schedules (total metres of chapim)
                    try
                    {
                        FamilyParameter comp = null;
                        try { comp = _fm.get_Parameter(SharedParams.Comprimento.Guid); } catch { }
                        if (comp == null && _defs.TryGetValue(SharedParams.Comprimento.Guid, out ExternalDefinition cd))
                            comp = _fm.AddParameter(cd, GroupTypeId.Data, true);
                        if (comp != null) _fm.SetFormula(comp, _length.Definition.Name);
                    }
                    catch (Exception ex) { Logger.Warn("[JA Famílias] JA_Comprimento: " + ex.Message); }
                }

                SetBuiltIn(BuiltInParameter.ALL_MODEL_TYPE_MARK, _c.Codigo);
                SetBuiltIn(BuiltInParameter.ALL_MODEL_DESCRIPTION, _c.Descricao());
                SetBuiltIn(BuiltInParameter.ALL_MODEL_MANUFACTURER, _s.Fabricante);
                SetBuiltIn(BuiltInParameter.ALL_MODEL_MODEL, PecaUtil.NomeTipo(_c.Tipo));
                SetBuiltIn(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS, _c.MaterialPrincipal());
            }

            private void Warn(string w)
            {
                _res.Warnings.Add(w);
                Logger.Warn("[JA Famílias] " + w);
            }
        }
    }
}
