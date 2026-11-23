using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using RevitDwgUnifier.Infrastructure;
using RevitDwgUnifier.Tools.Common;

namespace RevitDwgUnifier.Tools.Detail
{
    /// <summary>
    /// The office plan standard, learned from the reference view "FORNECER CLAUDE" (CVIVA, 2026):
    /// view type "Planta. Nivel Acabado", template "PE - Plantas. Civil", room tag "IND NOME
    /// AMBIENTE : NOME E AREA", linear dims "EXECUTIVO - 1,5mm_RomansD", angular dims at the
    /// corners that are not square ("1,5mm_vermelho"), one spot elevation "JA - Nível (Relativo)"
    /// on the floor of every room (relative to the "Osso" level, P.A./P.O.) just below-left of
    /// the room tag, "Soleira/Filete - Relativo a Nível" on the door thresholds, door tags "Tag
    /// Porta EX : Com Soleira", window tags "Tag Janela EX", railing tags. Names are compared
    /// without accents and without the "LB" prefix/suffix of the template the project came from.
    /// </summary>
    public static class PadraoPlanta
    {
        public static readonly string[] TipoVista = { "JA - Planta - Nível Acabado", "Planta. Nivel Acabado", "Planta Nivel Acabado" };
        public static readonly string[] Modelo = { "JA - PE - Planta Baixa Civil", "PE - Plantas. Civil", "PE - Plantas Civil" };
        public static readonly string[] EtiquetaAmbiente = { "JA - Etiqueta - Ambiente : Nome + Área", "IND NOME AMBIENTE : NOME E AREA", "NOME E AREA" };
        public static readonly string[] CotaLinear = { "JA - Cota Linear - Executivo 1,5 mm", "EXECUTIVO - 1,5mm_RomansD", "EXECUTIVO - 1,5mm" };
        public static readonly string[] CotaAngular = { "JA - Cota Angular - 1,5 mm Vermelha", "1,5mm_vermelho" };
        public static readonly string[] NivelPiso = { "JA - Nível - Piso Acabado (P.A./P.O.)", "JA - Nível (Relativo)", "Nível (Relativo)" };
        public static readonly string[] NivelSoleira = { "JA - Nível - Soleira", "Soleira/Filete - Relativo a Nível", "Soleira/Filete" };
        public static readonly Dictionary<string, string[]> EtiquetaCategoria = new Dictionary<string, string[]>
        {
            ["OST_Doors"] = new[] { "JA - Etiqueta - Porta : Código + Soleira", "Tag Porta EX : Com Soleira", "JA - Etiqueta - Porta", "Tag Porta EX" },
            ["OST_Windows"] = new[] { "JA - Etiqueta - Janela : Código", "Tag Janela EX : Sem Peitoril", "JA - Etiqueta - Janela", "Tag Janela EX" },
            ["OST_StairsRailing"] = new[] { "JA - Etiqueta - Guarda-corpo : Código", "TAG - Identificador de Guarda corpo : Sem Soleira", "JA - Etiqueta - Guarda-corpo", "Identificador de Guarda corpo" },
        };

        // ================================================================== names

        /// <summary>Name for comparing: no accents, lower case, no "LB" prefix/suffix, single spaces.</summary>
        public static string Norm(string s) => PadraoNomes.Norm(s);

        /// <summary>Index of the item that best matches one of the wanted names (exact, then
        /// starts with, then contains); -1 if none. Items may carry an offset (e.g. "(nenhum)" first).</summary>
        public static int Melhor(IList<string> itens, IEnumerable<string> nomes)
        {
            var n = itens.Select(Norm).ToList();
            foreach (string q0 in nomes)
            {
                string q = Norm(q0);
                if (q.Length == 0) continue;
                int i = n.IndexOf(q);
                if (i >= 0) return i;
            }
            foreach (string q0 in nomes)
            {
                string q = Norm(q0);
                for (int i = 0; i < n.Count; i++) if (q.Length > 0 && (n[i].StartsWith(q) || n[i].EndsWith(q))) return i;
            }
            foreach (string q0 in nomes)
            {
                string q = Norm(q0);
                for (int i = 0; i < n.Count; i++) if (q.Length > 3 && n[i].Contains(q)) return i;
            }
            return -1;
        }

        public static int Padrao(IList<string> itens, IEnumerable<string> nomes, int seNao) { int i = Melhor(itens, nomes); return i >= 0 ? i : seNao; }

        // ================================================================== types

        public static List<SpotDimensionType> TiposCotaNivel(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(SpotDimensionType)).Cast<SpotDimensionType>()
                .Where(t => { try { return t.StyleType == DimensionStyleType.SpotElevation; } catch { return false; } })
                .OrderBy(t => t.Name).ToList();

        public static List<DimensionType> TiposCotaAngular(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(DimensionType)).Cast<DimensionType>()
                .Where(t => { try { return t.StyleType == DimensionStyleType.Angular; } catch { return false; } })
                .OrderBy(t => t.Name).ToList();

        public static List<ViewFamilyType> TiposPlanta(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .Where(t => t.ViewFamily == ViewFamily.FloorPlan).OrderBy(t => t.Name).ToList();

        // ================================================================== spot elevations

        /// <summary>
        /// One spot elevation on the floor of the room, just below-left of the room point (where the
        /// office puts it, under the room tag), relative to the structural level ("Osso") of the floor
        /// when there is one. Inside an open transaction.
        /// </summary>
        public static bool NivelDoAmbiente(Document doc, View plan, Room r, SpotDimensionType tipo, List<string> problemas)
        {
            XYZ p = RevitUtil.RoomPoint(r);
            if (p == null) return false;
            double z0 = (r.Level?.ProjectElevation ?? 0) + r.BaseOffset;
            // below-left of the tag, kept inside the room
            XYZ alvo = null;
            foreach (double[] o in new[] { new[] { -0.35, -0.55 }, new[] { 0.35, -0.55 }, new[] { -0.35, 0.55 }, new[] { 0, -0.4 }, new[] { 0.0, 0.0 } })
            {
                var q = new XYZ(p.X + RevitUtil.M(o[0]), p.Y + RevitUtil.M(o[1]), z0 + RevitUtil.M(0.3));
                bool dentro = false;
                try { dentro = r.IsPointInRoom(q); } catch { }
                if (dentro) { alvo = q; break; }
            }
            if (alvo == null) return false;
            var (refFace, ponto) = FaceSuperior(doc, plan, alvo, z0, new[] { BuiltInCategory.OST_Floors });
            if (refFace == null)
            {
                RevitUtil.AddOnce(problemas, $"Ambiente {r.Number}: sem piso embaixo para a cota de nível.");
                return false;
            }
            return Criar(doc, plan, refFace, ponto, tipo, r.Level, false, problemas, "Ambiente " + r.Number);
        }

        /// <summary>Spot elevation on the threshold of every door of the room (top of the soleira piece,
        /// or the floor under the door). Returns how many. Inside an open transaction.</summary>
        public static int NiveisDasSoleiras(Document doc, View plan, Room r, SpotDimensionType tipo, HashSet<ElementId> feitas, List<string> problemas)
        {
            int n = 0;
            double z0 = (r.Level?.ProjectElevation ?? 0) + r.BaseOffset;
            foreach (FamilyInstance d in new FilteredElementCollector(doc, plan.Id).OfCategory(BuiltInCategory.OST_Doors).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>())
            {
                if (feitas.Contains(d.Id) || !PlanAnnotation.BelongsTo(d, r)) continue;
                if (!(d.Location is LocationPoint lp)) continue;
                var (refFace, ponto) = FaceSuperior(doc, plan, new XYZ(lp.Point.X, lp.Point.Y, z0), z0,
                    new[] { BuiltInCategory.OST_Casework, BuiltInCategory.OST_GenericModel, BuiltInCategory.OST_Floors });
                if (refFace == null) continue;
                feitas.Add(d.Id);
                if (Criar(doc, plan, refFace, ponto, tipo, r.Level, true, problemas, "Soleira " + (d.Symbol?.Name ?? ""))) n++;
            }
            return n;
        }

        /// <summary>The top planar face (reference + point on it) of an element of the categories right
        /// under the point, within 40 cm of the floor level.</summary>
        private static (Reference, XYZ) FaceSuperior(Document doc, View plan, XYZ p, double z0, BuiltInCategory[] cats)
        {
            var caixa = new Outline(new XYZ(p.X - 0.05, p.Y - 0.05, z0 - RevitUtil.M(0.6)), new XYZ(p.X + 0.05, p.Y + 0.05, z0 + RevitUtil.M(0.4)));
            var opt = new Options { ComputeReferences = true, View = plan };
            foreach (BuiltInCategory c in cats)
            {
                IList<Element> els;
                try
                {
                    els = new FilteredElementCollector(doc, plan.Id).OfCategory(c).WhereElementIsNotElementType()
                        .WherePasses(new BoundingBoxIntersectsFilter(caixa)).ToElements();
                }
                catch { continue; }
                // highest top first (finish floor over the structural slab, soleira over the floor)
                var cand = new List<(Reference r, XYZ q, double z)>();
                foreach (Element e in els)
                {
                    GeometryElement g;
                    try { g = e.get_Geometry(opt); } catch { continue; }
                    if (g == null) continue;
                    foreach (Solid s in Solidos(g))
                        foreach (Face f in s.Faces)
                        {
                            if (!(f is PlanarFace pf) || pf.FaceNormal.Z < 0.98 || pf.Reference == null) continue;
                            IntersectionResult ir = null;
                            try { ir = pf.Project(new XYZ(p.X, p.Y, pf.Origin.Z)); } catch { }
                            if (ir == null || ir.Distance > 1e-3) continue;
                            double z = ir.XYZPoint.Z;
                            if (z < z0 - RevitUtil.M(0.6) || z > z0 + RevitUtil.M(0.4)) continue;
                            cand.Add((pf.Reference, ir.XYZPoint, z));
                        }
                }
                if (cand.Count > 0)
                {
                    var b = cand.OrderByDescending(x => x.z).First();
                    return (b.r, b.q);
                }
            }
            return (null, null);
        }

        private static IEnumerable<Solid> Solidos(GeometryElement g)
        {
            foreach (GeometryObject o in g)
            {
                if (o is Solid s && s.Volume > 1e-9) yield return s;
                else if (o is GeometryInstance gi)
                    foreach (GeometryObject o2 in gi.GetInstanceGeometry())
                        if (o2 is Solid s2 && s2.Volume > 1e-9) yield return s2;
            }
        }

        private static bool Criar(Document doc, View plan, Reference refFace, XYZ ponto, SpotDimensionType tipo, Level nivel, bool chamada,
                                  List<string> problemas, string oQue)
        {
            try
            {
                XYZ dobra = ponto + new XYZ(RevitUtil.M(0.25), RevitUtil.M(0.25), 0);
                XYZ fim = dobra + new XYZ(RevitUtil.M(0.3), 0, 0);
                SpotDimension sd = doc.Create.NewSpotElevation(plan, refFace, ponto, chamada ? dobra : ponto, chamada ? fim : ponto, ponto, chamada);
                if (sd == null) return false;
                if (tipo != null) { try { sd.ChangeTypeId(tipo.Id); } catch { } }
                BaseOsso(doc, sd, nivel);
                return true;
            }
            catch (Exception ex)
            {
                RevitUtil.AddOnce(problemas, $"{oQue}: cota de nível ({ex.Message}).");
                return false;
            }
        }

        /// <summary>"Base relativa" = the structural ("Osso") level of the floor, or the room level.</summary>
        private static void BaseOsso(Document doc, SpotDimension sd, Level nivel)
        {
            if (nivel == null) return;
            try
            {
                Parameter p = null;
                if (Enum.TryParse("SPOT_ELEV_RELATIVE_BASE", out BuiltInParameter bip)) p = sd.get_Parameter(bip);
                if (p == null) p = sd.LookupParameter("Base relativa") ?? sd.LookupParameter("Relative Base");
                if (p == null || p.IsReadOnly || p.StorageType != StorageType.ElementId) return;
                Level osso = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                    .Where(l => TextUtil.Fold(l.Name).Contains("osso") && Math.Abs(l.ProjectElevation - nivel.ProjectElevation) <= RevitUtil.M(0.3))
                    .OrderBy(l => Math.Abs(l.ProjectElevation - nivel.ProjectElevation)).FirstOrDefault();
                p.Set((osso ?? nivel).Id);
            }
            catch (Exception ex) { Logger.Info("[Padrão planta] base relativa: " + ex.Message); }
        }

        // ================================================================== angular dimensions

        /// <summary>Angle dimension at every corner of the room that is not square (nor straight),
        /// between the faces of the two walls. Returns how many. Inside an open transaction.</summary>
        public static int AngulosDoAmbiente(Document doc, View plan, Room r, DimensionType tipo, List<string> problemas)
        {
            IList<IList<BoundarySegment>> loops = null;
            try { loops = RevitUtil.Boundary(r); } catch { }
            if (loops == null || loops.Count == 0) return 0;
            IList<BoundarySegment> ext = loops.OrderByDescending(l => l.Sum(s => s.GetCurve().Length)).First();
            double z = (plan as ViewPlan)?.GenLevel?.ProjectElevation ?? 0;
            var opt = new Options { ComputeReferences = true, View = plan };
            int n = 0;
            for (int i = 0; i < ext.Count; i++)
            {
                BoundarySegment a = ext[i], b = ext[(i + 1) % ext.Count];
                if (!(a.GetCurve() is Line la) || !(b.GetCurve() is Line lb)) continue;
                if (la.Length < RevitUtil.M(0.2) || lb.Length < RevitUtil.M(0.2)) continue;
                XYZ da = Plano(la.Direction), db = Plano(lb.Direction);
                double ang = Math.Acos(Math.Max(-1, Math.Min(1, da.DotProduct(db)))) * 180 / Math.PI;   // turn at the corner
                if (ang < 1 || Math.Abs(ang - 90) < 0.5) continue;                                         // straight or square
                XYZ c = la.GetEndPoint(1);
                Reference ra = FaceNoCanto(doc, a.ElementId, c, da, opt), rb = FaceNoCanto(doc, b.ElementId, c, db, opt);
                if (ra == null || rb == null) continue;
                try
                {
                    double raio = RevitUtil.M(0.6);
                    XYZ cc = new XYZ(c.X, c.Y, z);
                    XYZ u = -da, v = db;                                   // both legs of the corner, pointing away from it
                    XYZ meio = (u + v).GetLength() > 1e-6 ? (u + v).Normalize() : new XYZ(-u.Y, u.X, 0);
                    Arc arco = Arc.Create(cc + u * raio, cc + v * raio, cc + meio * raio);
                    AngularDimension.Create(doc, plan, arco, new List<Reference> { ra, rb }, tipo);
                    n++;
                }
                catch (Exception ex) { RevitUtil.AddOnce(problemas, $"Ambiente {r.Number}: cota angular ({ex.Message})."); }
            }
            return n;
        }

        private static XYZ Plano(XYZ v) { var p = new XYZ(v.X, v.Y, 0); return p.GetLength() < 1e-9 ? XYZ.BasisX : p.Normalize(); }

        /// <summary>The vertical face of the wall that runs along <paramref name="dir"/> through the corner.</summary>
        private static Reference FaceNoCanto(Document doc, ElementId id, XYZ canto, XYZ dir, Options opt)
        {
            if (!(doc.GetElement(id) is Wall w)) return null;
            GeometryElement g;
            try { g = w.get_Geometry(opt); } catch { return null; }
            if (g == null) return null;
            XYZ n = new XYZ(-dir.Y, dir.X, 0);
            foreach (Solid s in Solidos(g))
                foreach (Face f in s.Faces)
                {
                    if (!(f is PlanarFace pf) || pf.Reference == null || Math.Abs(pf.FaceNormal.Z) > 0.02) continue;
                    if (Math.Abs(Math.Abs(pf.FaceNormal.DotProduct(n)) - 1) > 0.002) continue;
                    double d = Math.Abs((new XYZ(canto.X, canto.Y, pf.Origin.Z) - pf.Origin).DotProduct(pf.FaceNormal));
                    if (d < RevitUtil.M(0.02)) return pf.Reference;
                }
            return null;
        }
    }
}
