using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Selection;
using RevitDwgUnifier.Tools.Common;

namespace RevitDwgUnifier.Tools.Dimensions
{
    /// <summary>A dimension reference and its position (feet) along the dimension direction.</summary>
    public sealed class RefAt
    {
        public Reference Ref;
        public double Pos;
        public string Kind;      // "wall", "opening", "edge", "point", "grid"
        public override string ToString() => $"{Kind}@{Pos:0.###}";
    }

    /// <summary>
    /// Reference finding and chain-dimension creation shared by the Cotas tools.
    ///
    /// Everything works in plan: a dimension runs along a horizontal direction u, from a base
    /// point a. Each candidate reference gets its position along u ((p - a) · u); references are
    /// sorted, de-duplicated (1 mm) and turned into one chain dimension, whose line spans from
    /// the first to the last reference.
    ///
    /// Where references come from:
    ///   walls     the faces of the STRUCTURE ("osso") of straight walls, never the finish: the side
    ///             faces (HostObjectUtils.GetSideFaces) moved in by the finish layers of the
    ///             compound structure, matched to the faces of the wall's layer solids (no face
    ///             found = no reference on that side, never the finish face). Finish walls
    ///             (revestimento modelled as its own wall — by its layers, by its name, or thin and
    ///             against a thicker wall, see ParedesCebola) give no references at all; end faces
    ///             only from the structure layers.
    ///   openings  FamilyInstance Left / Right reference planes (doors, windows)
    ///   points    FamilyInstance Center (Left/Right) or Center (Front/Back), whichever plane
    ///             is perpendicular to u (decided from HandOrientation)
    ///   floors /  edges of the top (floor) or bottom (ceiling) face, computed with
    ///   ceilings  Options.ComputeReferences
    ///   grids     new Reference(grid)
    /// </summary>
    public static class DimEngine
    {
        public const double Tol = 0.03;                 // |cos| tolerance for "perpendicular/parallel"
        private static readonly double Dedupe = RevitUtil.M(0.001);

        // ------------------------------------------------------------------ walls

        public static List<Wall> WallsInView(Document doc, View v) =>
            new FilteredElementCollector(doc, v.Id).OfClass(typeof(Wall)).Cast<Wall>()
                .Where(w => (w.Location as LocationCurve)?.Curve is Line).ToList();

        public static Line WallLine(Wall w) => (w.Location as LocationCurve)?.Curve as Line;

        public static XYZ FlatDir(Line l)
        {
            XYZ d = l.Direction;
            return new XYZ(d.X, d.Y, 0).Normalize();
        }

        /// <summary>
        /// The two faces of the CORE of a straight wall (exterior and interior side), the ones every
        /// wall dimension uses: never the finish (revestimento). A wall without finish layers gives
        /// its side faces; a finish wall gives nothing. Read once per tool run (SessionCache): a
        /// floor plan with many rooms dimensions the same walls many times. The key carries the
        /// wall's width and ends, so a wall changed during the run is read again.
        /// </summary>
        public static IEnumerable<Tuple<Reference, PlanarFace>> SideFaces(Wall w)
        {
            if (IsFinishWall(w)) return new List<Tuple<Reference, PlanarFace>>();
            string key = "wallcore|" + w.Document.GetHashCode() + "|" + w.Id.Value + "|" + WallStamp(w);
            return SessionCache.Get(key, () => ReadCoreFaces(w));
        }

        /// <summary>The finish side faces (outermost, with the revestimento) of a straight wall:
        /// for the wall's full thickness (tags in front of doors), not for dimensions.</summary>
        public static IEnumerable<Tuple<Reference, PlanarFace>> FinishFaces(Wall w)
        {
            string key = "wallfaces|" + w.Document.GetHashCode() + "|" + w.Id.Value + "|" + WallStamp(w);
            return SessionCache.Get(key, () => ReadSideFaces(w));
        }

        private static readonly string[] FinishWords =
        {
            "REVEST", "REBOCO", "EMBOCO", "ACABAMENT", "CERAMIC", "PORCELANAT", "PASTILHA", "AZULEJ", "LAMBRI", "PINTURA",
            "PAPEL DE PAREDE", "FINISH"
        };

        /// <summary>
        /// A wall that is only revestimento — never dimensioned: every layer of its structure is a finish
        /// (or membrane) layer, as "Converter em Camadas" makes them; or a thin type (≤ 10 cm) whose name says
        /// so ("Revestimento cerâmico 1 cm", "Reboco"…); or a thin wall against a thicker one (a parede cebola
        /// with a neutral name and a structural layer — same settings as Ajustar Paredes Cebola).
        /// </summary>
        public static bool IsFinishWall(Wall w)
        {
            WallType t = w?.WallType;
            if (t == null || t.Kind != WallKind.Basic) return false;
            string key = "finishwall|" + w.Document.GetHashCode() + "|" + t.Id.Value;
            bool porTipo = SessionCache.Get(key, () =>
            {
                try
                {
                    CompoundStructure cs = t.GetCompoundStructure();
                    IList<CompoundStructureLayer> layers = cs?.GetLayers();
                    if (layers != null && layers.Count > 0 && layers.All(l =>
                            l.Function == MaterialFunctionAssignment.Finish1 || l.Function == MaterialFunctionAssignment.Finish2 ||
                            l.Function == MaterialFunctionAssignment.Membrane))
                        return true;
                }
                catch { }
                double largura = 0;
                try { largura = t.Width; } catch { }
                if (largura > RevitUtil.M(0.10)) return false;     // "Alvenaria revestida 15 cm" is a wall
                string n = " " + RevitDwgUnifier.Contracts.InstallationRules.Normalize(t.Name) + " ";
                return FinishWords.Any(k => n.Contains(" " + k));
            });
            if (porTipo) return true;
            if (w.Width > ParedesCebola.EspessuraMax + 1e-9 && w.Width > RevitUtil.M(0.10)) return false;
            string kw = "cebola|" + w.Document.GetHashCode() + "|" + w.Id.Value + "|" + WallStamp(w);
            return SessionCache.Get(kw, () => { try { return ParedesCebola.EhRevestimento(w); } catch { return false; } });
        }

        /// <summary>The structure wall behind a finish wall (tile → plaster → wall), or null.</summary>
        public static Wall Structure(Wall finish) => ParedesCebola.Estrutura(finish, IsFinishWall);

        /// <summary>The walls to dimension for a picked set: finish walls replaced by their structure wall.</summary>
        public static List<Wall> StructureWalls(IEnumerable<Wall> picked, out int replaced)
        {
            var res = new List<Wall>();
            int n = 0;
            foreach (Wall w in picked)
            {
                Wall s = w;
                if (IsFinishWall(w)) { s = Structure(w); n++; }
                if (s != null && WallLine(s) != null && !res.Any(x => x.Id == s.Id)) res.Add(s);
            }
            replaced = n;
            return res;
        }

        /// <summary>Warnings of this run about walls whose structure face was not found (shown in the summary).</summary>
        public static List<string> CoreWarnings => SessionCache.Get("dim|corewarnings", () => new List<string>());

        /// <summary>
        /// Structure faces: each side face moved in by the width of the finish layers on that side
        /// (outside the core, plus outer finish / membrane layers inside it — ParedesCebola.Acabamentos),
        /// matched to a face of the wall's layer solids at that plane (the core solid's face preferred).
        /// Where no such face exists that side gives nothing (and a warning): never the finish face.
        /// </summary>
        private static List<Tuple<Reference, PlanarFace>> ReadCoreFaces(Wall w)
        {
            List<Tuple<Reference, PlanarFace>> finish = FinishFaces(w).ToList();
            ParedesCebola.Acabamentos(w.WallType, out double ext, out double inn);
            if (ext < 1e-6 && inn < 1e-6) return finish;                 // no finish layers: the side faces are the structure

            List<PlanarFace> candidates;
            try { candidates = PlanarFaces(w).Where(pf => pf.Reference != null && Math.Abs(pf.FaceNormal.Z) < 0.01).ToList(); }
            catch { candidates = new List<PlanarFace>(); }

            XYZ ori = new XYZ(w.Orientation.X, w.Orientation.Y, 0);
            var result = new List<Tuple<Reference, PlanarFace>>();
            foreach (var f in finish)
            {
                XYZ n = new XYZ(f.Item2.FaceNormal.X, f.Item2.FaceNormal.Y, 0);
                if (n.GetLength() < 1e-9) { result.Add(f); continue; }
                n = n.Normalize();
                bool exterior = ori.GetLength() > 1e-9 ? n.DotProduct(ori) > 0 : true;
                double off = exterior ? ext : inn;
                if (off < 1e-6) { result.Add(f); continue; }
                XYZ target = f.Item2.Origin - n * off;                     // inward, through the finish layers
                PlanarFace best = candidates
                    .Where(pf => Math.Abs(pf.FaceNormal.DotProduct(n)) >= 1 - Tol &&
                                 Math.Abs((pf.Origin - target).DotProduct(n)) < RevitUtil.M(0.001))
                    .OrderByDescending(pf => pf.FaceNormal.DotProduct(n) > 0)   // the core's own face (pointing outward)
                    .ThenByDescending(pf => pf.Area)
                    .FirstOrDefault();
                if (best != null) result.Add(Tuple.Create(best.Reference, best));
                else RevitUtil.AddOnce(CoreWarnings, $"Parede {w.Id.Value} ({w.WallType?.Name}): a face da estrutura não foi encontrada no lado {(exterior ? "externo" : "interno")} — esse lado ficou sem cota (o revestimento nunca é cotado). Confira se as camadas de revestimento têm material próprio.");
            }
            return result;
        }

        private static string WallStamp(Wall w)
        {
            try
            {
                Line l = WallLine(w);
                if (l == null) return w.Width.ToString("R");
                XYZ a = l.GetEndPoint(0), b = l.GetEndPoint(1);
                return string.Join(",", w.Width.ToString("0.######"), a.X.ToString("0.######"), a.Y.ToString("0.######"),
                    b.X.ToString("0.######"), b.Y.ToString("0.######"), a.Z.ToString("0.###"));
            }
            catch { return "?"; }
        }

        private static List<Tuple<Reference, PlanarFace>> ReadSideFaces(Wall w)
        {
            var list = new List<Tuple<Reference, PlanarFace>>();
            foreach (ShellLayerType side in new[] { ShellLayerType.Exterior, ShellLayerType.Interior })
            {
                IList<Reference> refs;
                try { refs = HostObjectUtils.GetSideFaces(w, side); } catch { continue; }
                foreach (Reference r in refs)
                {
                    PlanarFace pf = null;
                    try { pf = w.GetGeometryObjectFromReference(r) as PlanarFace; } catch { }
                    if (pf != null) list.Add(Tuple.Create(r, pf));
                }
            }
            return list;
        }

        /// <summary>
        /// Faces of the walls crossed by the segment a-b (extended by <paramref name="extend"/>)
        /// that are perpendicular to it. Positions are measured from a along u = (b - a).
        /// </summary>
        public static List<RefAt> WallFacesAcross(IEnumerable<Wall> walls, XYZ a, XYZ b, double extend = 0)
        {
            var result = new List<RefAt>();
            XYZ fa = RevitUtil.Flat(a), fb = RevitUtil.Flat(b);
            double len = fa.DistanceTo(fb);
            if (len < 1e-9) return result;
            XYZ u = (fb - fa) / len;

            foreach (Wall w in walls)
            {
                Line wl = WallLine(w);
                if (wl == null) continue;
                XYZ d = FlatDir(wl);
                if (Math.Abs(d.DotProduct(u)) > Tol) continue;        // only walls across the line

                XYZ p0 = RevitUtil.Flat(wl.GetEndPoint(0));
                double denom = u.X * d.Y - u.Y * d.X;
                if (Math.Abs(denom) < 1e-9) continue;
                double t = ((p0.X - fa.X) * d.Y - (p0.Y - fa.Y) * d.X) / denom;   // along the dimension line
                double s = ((p0.X - fa.X) * u.Y - (p0.Y - fa.Y) * u.X) / denom;   // along the wall
                double half = w.Width / 2 + RevitUtil.M(0.01);
                if (t < -extend - half || t > len + extend + half) continue;
                if (s < -half || s > wl.Length + half) continue;

                foreach (var f in SideFaces(w))
                    result.Add(new RefAt { Ref = f.Item1, Pos = (RevitUtil.Flat(f.Item2.Origin) - fa).DotProduct(u), Kind = "wall" });
            }
            return result;
        }

        /// <summary>End faces of a wall (only exist where the wall is not joined), from its solid.</summary>
        public static List<RefAt> WallEndFaces(Wall w, XYZ origin, XYZ u)
        {
            var result = new List<RefAt>();
            if (IsFinishWall(w)) return result;            // never the revestimento
            Line wl = WallLine(w);
            if (wl == null) return result;
            XYZ d = FlatDir(wl);
            // the structure band across the wall (from its centre line, along Orientation): end faces of the
            // finish layers (also when they wrap around the end) are left out
            bool faixa = ParedesCebola.Eixo(w, out XYZ ca, out _, out _, out XYZ cn, out _);
            ParedesCebola.Acabamentos(w.WallType, out double ext, out double inn);
            double lo = -w.Width / 2 + inn - RevitUtil.M(0.001), hi = w.Width / 2 - ext + RevitUtil.M(0.001);
            foreach (PlanarFace pf in PlanarFaces(w))
            {
                if (Math.Abs(pf.FaceNormal.DotProduct(d)) < 1 - Tol || pf.Reference == null) continue;
                if (faixa && (ext > 1e-6 || inn > 1e-6))
                {
                    double lat;
                    try
                    {
                        BoundingBoxUV bx = pf.GetBoundingBox();
                        XYZ c = pf.Evaluate(new UV((bx.Min.U + bx.Max.U) / 2, (bx.Min.V + bx.Max.V) / 2));
                        lat = (RevitUtil.Flat(c) - ca).DotProduct(cn);
                    }
                    catch { continue; }
                    if (lat < lo || lat > hi) continue;
                }
                result.Add(new RefAt { Ref = pf.Reference, Pos = (RevitUtil.Flat(pf.Origin) - RevitUtil.Flat(origin)).DotProduct(u), Kind = "wall" });
            }
            return result;
        }

        // ------------------------------------------------------------------ directions (any angle)

        /// <summary>
        /// Distinct directions of straight segments (0..180°, clustered within half a degree), the
        /// longest total first: the directions of the walls of a room, the edges of a floor...
        /// Any angle (45°, 30°, 27°, 21°, 11°...) works: dimensions are measured across each one.
        /// </summary>
        public static List<XYZ> Directions(IEnumerable<Line> lines, double minTotal)
        {
            var groups = new List<Tuple<double, double>>();   // angle (rad, 0..pi), total length
            foreach (Line l in lines)
            {
                if (l == null) continue;
                XYZ d = l.Direction;
                if (Math.Abs(d.Z) > 0.99) continue;
                double ang = Math.Atan2(d.Y, d.X);
                if (ang < 0) ang += Math.PI;
                if (ang >= Math.PI - 1e-9) ang -= Math.PI;
                double len = Math.Sqrt(Math.Pow(l.GetEndPoint(1).X - l.GetEndPoint(0).X, 2) + Math.Pow(l.GetEndPoint(1).Y - l.GetEndPoint(0).Y, 2));
                int i = groups.FindIndex(g => AngleDiff(g.Item1, ang) < Math.PI / 360);
                if (i < 0) groups.Add(Tuple.Create(ang, len));
                else groups[i] = Tuple.Create(groups[i].Item1, groups[i].Item2 + len);
            }
            return groups.Where(g => g.Item2 >= minTotal).OrderByDescending(g => g.Item2)
                         .Select(g => new XYZ(Math.Cos(g.Item1), Math.Sin(g.Item1), 0)).ToList();
        }

        private static double AngleDiff(double a, double b)
        {
            double d = Math.Abs(a - b) % Math.PI;
            return Math.Min(d, Math.PI - d);
        }

        /// <summary>Directions of the straight boundary segments of a room.</summary>
        public static List<XYZ> RoomDirections(Autodesk.Revit.DB.Architecture.Room r, double minTotal)
        {
            var lines = new List<Line>();
            try
            {
                foreach (IList<BoundarySegment> loop in RevitUtil.Boundary(r))
                    foreach (BoundarySegment seg in loop)
                        if (seg.GetCurve() is Line l) lines.Add(l);
            }
            catch { }
            return Directions(lines, minTotal);
        }

        /// <summary>Unit direction of the wall closest to a point (within maxDist), or null.</summary>
        public static XYZ NearestWallDir(IEnumerable<Wall> walls, XYZ p, double maxDist)
        {
            XYZ fp = RevitUtil.Flat(p), best = null;
            double bestD = maxDist;
            foreach (Wall w in walls)
            {
                Line wl = WallLine(w);
                if (wl == null) continue;
                XYZ a = RevitUtil.Flat(wl.GetEndPoint(0)), b = RevitUtil.Flat(wl.GetEndPoint(1));
                XYZ ab = b - a;
                double t = ab.GetLength() < 1e-9 ? 0 : Math.Max(0, Math.Min(1, (fp - a).DotProduct(ab) / ab.DotProduct(ab)));
                double dist = (a + ab * t).DistanceTo(fp);
                if (dist < bestD) { bestD = dist; best = FlatDir(wl); }
            }
            return best;
        }

        // ------------------------------------------------------------------ family instances

        /// <summary>Left / Right planes of a door or window, with positions from its box.</summary>
        public static List<RefAt> OpeningSides(FamilyInstance fi, XYZ origin, XYZ u)
        {
            var result = new List<RefAt>();
            Reference left = fi.GetReferences(FamilyInstanceReferenceType.Left).FirstOrDefault();
            Reference right = fi.GetReferences(FamilyInstanceReferenceType.Right).FirstOrDefault();
            if (left == null || right == null) return result;
            double lo = double.MaxValue, hi = double.MinValue;
            // From the insertion point and the width along the wall: exact at any wall angle
            // (the bounding box of a door in a 30° wall is much wider than the door).
            XYZ at = (fi.Location as LocationPoint)?.Point;
            double width = 0;
            foreach (BuiltInParameter bip in new[] { BuiltInParameter.DOOR_WIDTH, BuiltInParameter.WINDOW_WIDTH, BuiltInParameter.FAMILY_WIDTH_PARAM })
            {
                double w = fi.Symbol?.get_Parameter(bip)?.AsDouble() ?? fi.get_Parameter(bip)?.AsDouble() ?? 0;
                if (w > 1e-6) { width = w; break; }
            }
            XYZ hand = new XYZ(fi.HandOrientation.X, fi.HandOrientation.Y, 0);
            if (at != null && width > 1e-6 && hand.GetLength() > 1e-6)
            {
                double c = (RevitUtil.Flat(at) - RevitUtil.Flat(origin)).DotProduct(u);
                double half = Math.Abs(hand.Normalize().DotProduct(u)) * width / 2;
                lo = c - half; hi = c + half;
            }
            else
            {
                BoundingBoxXYZ bb = fi.get_BoundingBox(null);
                if (bb == null) return result;
                foreach (XYZ c in new[] { bb.Min, bb.Max, new XYZ(bb.Min.X, bb.Max.Y, 0), new XYZ(bb.Max.X, bb.Min.Y, 0) })
                {
                    double p = (RevitUtil.Flat(c) - RevitUtil.Flat(origin)).DotProduct(u);
                    lo = Math.Min(lo, p); hi = Math.Max(hi, p);
                }
            }
            // Which plane is at the low end: the hand direction tells where "right" is.
            bool rightIsHigh = fi.HandOrientation.DotProduct(u) >= 0;
            result.Add(new RefAt { Ref = rightIsHigh ? left : right, Pos = lo, Kind = "opening" });
            result.Add(new RefAt { Ref = rightIsHigh ? right : left, Pos = hi, Kind = "opening" });
            return result;
        }

        /// <summary>The centre plane of an instance that is perpendicular to u.</summary>
        public static RefAt CenterAcross(FamilyInstance fi, XYZ origin, XYZ u)
        {
            bool handAlongU = Math.Abs(fi.HandOrientation.DotProduct(u)) >= 0.7;
            var type = handAlongU ? FamilyInstanceReferenceType.CenterLeftRight : FamilyInstanceReferenceType.CenterFrontBack;
            // Only the plane across u: the other centre plane would give an invalid dimension.
            Reference r = fi.GetReferences(type).FirstOrDefault();
            if (r == null) return null;
            XYZ p = RevitUtil.Center(fi);
            if (p == null) return null;
            return new RefAt { Ref = r, Pos = (RevitUtil.Flat(p) - RevitUtil.Flat(origin)).DotProduct(u), Kind = "point" };
        }

        // ------------------------------------------------------------------ floors / ceilings

        public static IEnumerable<PlanarFace> PlanarFaces(Element e)
        {
            var opt = new Options { ComputeReferences = true, IncludeNonVisibleObjects = false, DetailLevel = ViewDetailLevel.Fine };
            GeometryElement geo = e.get_Geometry(opt);
            if (geo == null) yield break;
            foreach (Solid s in Solids(geo))
                foreach (Face f in s.Faces)
                    if (f is PlanarFace pf) yield return pf;
        }

        private static IEnumerable<Solid> Solids(GeometryElement geo)
        {
            foreach (GeometryObject o in geo)
            {
                if (o is Solid s && s.Faces.Size > 0) yield return s;
                else if (o is GeometryInstance gi)
                    foreach (Solid si in Solids(gi.GetInstanceGeometry())) yield return si;
            }
        }

        /// <summary>The top (up = true) or bottom horizontal face of an element.</summary>
        public static PlanarFace HorizontalFace(Element e, bool up)
        {
            double best = up ? double.MinValue : double.MaxValue;
            PlanarFace face = null;
            foreach (PlanarFace pf in PlanarFaces(e))
            {
                if (up ? pf.FaceNormal.Z < 0.99 : pf.FaceNormal.Z > -0.99) continue;
                double z = pf.Origin.Z;
                if (up ? z > best : z < best) { best = z; face = pf; }
            }
            return face;
        }

        public static List<Line> FaceLines(PlanarFace face)
        {
            var lines = new List<Line>();
            if (face == null) return lines;
            foreach (EdgeArray loop in face.EdgeLoops)
                foreach (Edge edge in loop)
                    if (edge.AsCurve() is Line l) lines.Add(l);
            return lines;
        }

        /// <summary>Edges of the top (up = true) or bottom horizontal face that are perpendicular
        /// to u, i.e. that a dimension along u can measure.</summary>
        public static List<RefAt> HorizontalFaceEdges(Element e, bool up, XYZ origin, XYZ u, out BoundingBoxXYZ faceBox,
                                                      Func<XYZ, XYZ, bool> keep = null)
        {
            var result = new List<RefAt>();
            faceBox = null;
            double best = up ? double.MinValue : double.MaxValue;
            PlanarFace face = null;
            foreach (PlanarFace pf in PlanarFaces(e))
            {
                if (up ? pf.FaceNormal.Z < 0.99 : pf.FaceNormal.Z > -0.99) continue;
                double z = pf.Origin.Z;
                if (up ? z > best : z < best) { best = z; face = pf; }
            }
            if (face == null) return result;

            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (EdgeArray loop in face.EdgeLoops)
                foreach (Edge edge in loop)
                {
                    if (!(edge.AsCurve() is Line l)) continue;
                    XYZ p0 = l.GetEndPoint(0), p1 = l.GetEndPoint(1);
                    minX = Math.Min(minX, Math.Min(p0.X, p1.X)); maxX = Math.Max(maxX, Math.Max(p0.X, p1.X));
                    minY = Math.Min(minY, Math.Min(p0.Y, p1.Y)); maxY = Math.Max(maxY, Math.Max(p0.Y, p1.Y));
                    if (edge.Reference == null) continue;
                    XYZ d = FlatDir(l);
                    if (Math.Abs(d.DotProduct(u)) > Tol) continue;
                    if (keep != null && !keep(p0, p1)) continue;
                    result.Add(new RefAt { Ref = edge.Reference, Pos = (RevitUtil.Flat((p0 + p1) / 2) - RevitUtil.Flat(origin)).DotProduct(u), Kind = "edge" });
                }
            if (minX <= maxX) faceBox = new BoundingBoxXYZ { Min = new XYZ(minX, minY, best), Max = new XYZ(maxX, maxY, best) };
            return result;
        }

        // ------------------------------------------------------------------ grids

        public static List<RefAt> GridsAcross(IEnumerable<Grid> grids, XYZ a, XYZ b)
        {
            var result = new List<RefAt>();
            XYZ fa = RevitUtil.Flat(a), fb = RevitUtil.Flat(b);
            double len = fa.DistanceTo(fb);
            if (len < 1e-9) return result;
            XYZ u = (fb - fa) / len;
            foreach (Grid g in grids)
            {
                if (!(g.Curve is Line gl)) continue;
                XYZ d = FlatDir(gl);
                if (Math.Abs(d.DotProduct(u)) > Tol) continue;
                double t = (RevitUtil.Flat(gl.GetEndPoint(0)) - fa).DotProduct(u);
                if (t < 0 || t > len) continue;
                result.Add(new RefAt { Ref = new Reference(g), Pos = t, Kind = "grid" });
            }
            return result;
        }

        // ------------------------------------------------------------------ chains

        /// <summary>Sorted by position, one reference per position (1 mm).</summary>
        public static List<RefAt> Clean(IEnumerable<RefAt> refs)
        {
            var list = refs.Where(r => r?.Ref != null).OrderBy(r => r.Pos).ToList();
            var result = new List<RefAt>();
            foreach (RefAt r in list)
                if (result.Count == 0 || r.Pos - result[result.Count - 1].Pos > Dedupe) result.Add(r);
            return result;
        }

        /// <summary>
        /// Chain dimension along u through <paramref name="through"/> (a point of the dimension
        /// line). Returns null when fewer than two distinct references remain.
        /// </summary>
        public static Dimension Chain(Document doc, View view, XYZ origin, XYZ u, XYZ through, IEnumerable<RefAt> refs, DimensionType type)
        {
            List<RefAt> list = Clean(refs);
            if (list.Count < 2) return null;
            XYZ o = RevitUtil.Flat(origin);
            XYZ t = RevitUtil.Flat(through);
            XYZ across = t - o - u * (t - o).DotProduct(u);          // offset of the line from the origin
            double z = through.Z;
            double ext = RevitUtil.M(0.2);
            XYZ p0 = o + across + u * (list.First().Pos - ext) + new XYZ(0, 0, z);
            XYZ p1 = o + across + u * (list.Last().Pos + ext) + new XYZ(0, 0, z);
            return RevitUtil.CreateDimension(doc, view, Line.CreateBound(p0, p1), list.Select(r => r.Ref), type);
        }

        /// <summary>The nearest reference strictly below / above a position.</summary>
        public static RefAt Below(IEnumerable<RefAt> refs, double pos) =>
            refs.Where(r => r.Pos < pos - Dedupe).OrderByDescending(r => r.Pos).FirstOrDefault();

        public static RefAt Above(IEnumerable<RefAt> refs, double pos) =>
            refs.Where(r => r.Pos > pos + Dedupe).OrderBy(r => r.Pos).FirstOrDefault();

        /// <summary>A plan level height for dimension lines in the view.</summary>
        public static double ViewZ(View v) => (v as ViewPlan)?.GenLevel?.ProjectElevation ?? 0;

        /// <summary>"(padrão)" + linear dimension type names.</summary>
        public static List<string> TypeChoices(IList<DimensionType> types) =>
            new[] { "(padrão do projeto)" }.Concat(types.Select(t => t.Name)).ToList();

        public static DimensionType TypeFromChoice(IList<DimensionType> types, int index) =>
            index >= 1 && index <= types.Count ? types[index - 1] : null;
    }

    public sealed class CategorySelectionFilter : ISelectionFilter
    {
        private readonly HashSet<long> _cats;
        public CategorySelectionFilter(params BuiltInCategory[] cats) { _cats = new HashSet<long>(cats.Select(c => (long)c)); }
        public bool AllowElement(Element e) => e?.Category != null && _cats.Contains(e.Category.Id.Value);
        public bool AllowReference(Reference r, XYZ p) => true;
    }
}
