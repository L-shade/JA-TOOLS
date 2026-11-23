using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using RevitDwgUnifier.Tools.Common;
using RevitDwgUnifier.Tools.Dimensions;

namespace RevitDwgUnifier.Tools.Detail
{
    /// <summary>
    /// Annotates an interior elevation of a room (Detalhar Ambientes):
    ///   * overall dimensions: the clear width (face to face of the side walls, below the floor line)
    ///     and the height floor → ceiling (or floor → slab above when there is no ceiling), on the left;
    ///   * the height of every counter ("bancada", "tampo", casework 0.40–1.30 m high) standing
    ///     against the wall that is seen, floor → top of the counter, beside it;
    ///   * a text under the ceiling line when the elevation cuts a ceiling ("FORRO EM VISTA").
    /// The annotation crop of the view is turned off so the dimensions outside the crop show.
    /// Inside an open transaction.
    /// </summary>
    public sealed class ElevationAnnotator
    {
        private readonly Document _doc;
        private readonly List<string> _problems;

        public DimensionType DimType;
        public bool Overall = true, Counters = true, CeilingNote = true;
        public string CeilingText = "FORRO EM VISTA";
        public ElementId TextTypeId;
        public int Dims, Notes;

        private static double M(double m) => RevitUtil.M(m);
        private static XYZ Flat(XYZ p) => RevitUtil.Flat(p);

        private static readonly string[] CounterWords = { "bancada", "tampo", "balcao", "balcão", "counter", "granito", "marmore", "mármore", "silestone", "quartzo" };

        private readonly Dictionary<long, Tuple<Reference, double>> _topCache = new Dictionary<long, Tuple<Reference, double>>();
        private ElementId _cacheRoom;
        private List<Wall> _walls;
        private Tuple<Reference, double> _floor, _top;
        private bool _isCeiling;
        private double _ceilTop;

        public ElevationAnnotator(Document doc, List<string> problems)
        {
            _doc = doc;
            _problems = problems;
        }

        public void Annotate(ViewSection ev, Room room, BoundingBoxXYZ rb)
        {
            XYZ rp = RevitUtil.RoomPoint(room);
            if (rp == null || rb == null) return;
            XYZ u = Flat(ev.RightDirection);
            XYZ lookRaw = Flat(-ev.ViewDirection);
            if (u.GetLength() < 1e-6 || lookRaw.GetLength() < 1e-6) return;
            u = u.Normalize();
            XYZ look = lookRaw.Normalize();
            XYZ o = Flat(ev.Origin);
            XYZ p = Flat(rp);
            double k = Math.Max(1, ev.Scale) / 304.8;               // 1 mm of paper
            double floorZ = rb.Min.Z;
            double reach = Math.Max(rb.Max.X - rb.Min.X, rb.Max.Y - rb.Min.Y) + M(1);
            // walls, floor and ceiling of the room are read once for its four elevations
            if (ReferenceEquals(_cacheRoom, null) || _cacheRoom.Value != room.Id.Value)
            {
                _cacheRoom = room.Id;
                _walls = WallsNear(rb);
                _floor = FloorRef(room, Flat(rp), rb.Min.Z);
                _top = TopRef(Flat(rp), rb.Min.Z, rb.Max.Z, out _isCeiling, out _ceilTop);
            }
            List<Wall> walls = _walls;
            RevitUtil.SetAnnotationCrop(ev, false);

            // side walls: interior faces left and right of the room point
            XYZ a = p - u * reach, b = p + u * reach;
            List<RefAt> faces = DimEngine.WallFacesAcross(walls, a, b);
            RefAt lo = DimEngine.Below(faces, reach), hi = DimEngine.Above(faces, reach);
            double shift = (a - o).DotProduct(u);
            var span = new[] { rb.Min, rb.Max, new XYZ(rb.Min.X, rb.Max.Y, 0), new XYZ(rb.Max.X, rb.Min.Y, 0) }
                .Select(c => (Flat(c) - o).DotProduct(u)).ToList();
            double sLo = lo != null ? lo.Pos + shift : span.Min();
            double sHi = hi != null ? hi.Pos + shift : span.Max();

            Tuple<Reference, double> floor = _floor;
            Tuple<Reference, double> top = _top;
            bool isCeiling = _isCeiling;

            if (Overall)
            {
                if (lo != null && hi != null)
                {
                    try
                    {
                        if (DimEngine.Chain(_doc, ev, a, u, new XYZ(o.X, o.Y, floorZ - M(0.15) - 8 * k), new[] { lo, hi }, DimType) != null) Dims++;
                    }
                    catch (Exception ex) { RevitUtil.AddOnce(_problems, $"{ev.Name}: cota de largura ({ex.Message})."); }
                }
                else RevitUtil.AddOnce(_problems, $"{ev.Name}: paredes laterais não encontradas para a cota de largura.");

                if (floor != null && top != null)
                    Vertical(ev, u, o, sLo - M(0.15) - 8 * k, new[] { floor, top });
                else if (top == null)
                    RevitUtil.AddOnce(_problems, $"{ev.Name}: sem forro nem laje acima do ambiente para a cota de altura.");
            }

            if (Counters && floor != null)
                CounterDims(ev, room, u, look, o, p, reach, walls, floor, floorZ, sLo, sHi, k);

            if (CeilingNote && isCeiling && top != null && !string.IsNullOrWhiteSpace(CeilingText))
            {
                try
                {
                    ElementId tt = TextTypeId != null && TextTypeId != ElementId.InvalidElementId
                        ? TextTypeId : _doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
                    // on the cut ceiling, at the left wall: just above the ceiling line, in the band of the
                    // ceiling / slab seen in section (where it is written by hand)
                    var opts = new TextNoteOptions(tt)
                    {
                        HorizontalAlignment = HorizontalTextAlignment.Left,
                        VerticalAlignment = VerticalTextAlignment.Bottom
                    };
                    XYZ bp = o + u * (sLo + 3 * k);
                    double zText = top.Item2 + 1.2 * k;
                    if (TextNote.Create(_doc, ev.Id, new XYZ(bp.X, bp.Y, zText), CeilingText.Trim(), opts) != null) Notes++;
                }
                catch (Exception ex) { RevitUtil.AddOnce(_problems, $"{ev.Name}: texto do forro ({ex.Message})."); }
            }
        }

        // ------------------------------------------------------------------ counters

        private void CounterDims(ViewSection ev, Room room, XYZ u, XYZ look, XYZ o, XYZ p, double reach, List<Wall> walls,
                                 Tuple<Reference, double> floor, double floorZ, double sLo, double sHi, double k)
        {
            // the wall that is seen: its face across the look direction, beyond the room point
            XYZ la = p - look * reach;
            RefAt far = DimEngine.Above(DimEngine.WallFacesAcross(walls, la, p + look * reach), reach);

            var cats = new List<BuiltInCategory>
            {
                BuiltInCategory.OST_Casework, BuiltInCategory.OST_Furniture, BuiltInCategory.OST_GenericModel,
                BuiltInCategory.OST_SpecialityEquipment, BuiltInCategory.OST_PlumbingFixtures
            };
            var done = new List<double[]>();
            foreach (FamilyInstance fi in new FilteredElementCollector(_doc, ev.Id).WherePasses(new ElementMulticategoryFilter(cats))
                         .WhereElementIsNotElementType().OfType<FamilyInstance>())
            {
                bool fixture = IsFixture(fi);
                if (!fixture && !IsCounter(fi)) continue;
                BoundingBoxXYZ bb = fi.get_BoundingBox(null);
                if (bb == null) continue;
                double h = bb.Max.Z - floorZ;
                if (h < M(fixture ? 0.5 : 0.4) || h > M(1.3)) continue;
                XYZ c = (bb.Min + bb.Max) / 2;
                bool inside;
                try { inside = room.IsPointInRoom(new XYZ(c.X, c.Y, floorZ + M(0.5))); } catch { inside = true; }
                if (!inside) continue;

                var corners = new[] { bb.Min, bb.Max, new XYZ(bb.Min.X, bb.Max.Y, 0), new XYZ(bb.Max.X, bb.Min.Y, 0) }.Select(Flat).ToList();
                double depth = corners.Max(q => (q - la).DotProduct(look));
                // counters: only the ones against the wall that is seen; basins / sinks: every one in view
                if (!fixture && far != null && far.Pos - depth > M(0.3)) continue;
                double sMin = corners.Min(q => (q - o).DotProduct(u)), sMax = corners.Max(q => (q - o).DotProduct(u));

                // the same counter / basin shows in 2 or 3 elevations: its top face is read once
                if (!_topCache.TryGetValue(fi.Id.Value, out Tuple<Reference, double> top))
                {
                    Reference r0 = TopFaceRef(_doc, fi, out double z0);
                    _topCache[fi.Id.Value] = top = Tuple.Create(r0, z0);
                }
                Reference topRef = top.Item1;
                double tz = top.Item2;
                if (topRef == null)
                {
                    RevitUtil.AddOnce(_problems, $"{(fixture ? "Louça" : "Bancada")} \"{fi.Symbol?.FamilyName}\": face superior sem referência para cotar (marque um plano de referência \"Topo\" como referência na família).");
                    continue;
                }
                double s;
                if (sMax < sHi - M(0.15)) s = sMax + 6 * k;               // free space on the right
                else if (sMin > sLo + M(0.15)) s = sMin - 6 * k;          // or on the left
                else s = sLo + (sHi - sLo) * 0.25;                        // wall to wall: over the counter
                if (done.Any(d => Math.Abs(d[1] - tz) < M(0.01) && Math.Abs(d[0] - s) < M(0.6))) continue;
                if (Vertical(ev, u, o, s, new[] { floor, Tuple.Create(topRef, tz) })) done.Add(new[] { s, tz });
            }
        }

        private static readonly string[] NotFixtureWords = { "bacia", "vaso", "chuveiro", "ducha", "ralo", "registro", "mictor", "banheira", "toilet", "shower" };

        /// <summary>Basins, sinks, tanks (plumbing fixtures): their height is dimensioned in every
        /// elevation that shows them (front or side).</summary>
        private static bool IsFixture(FamilyInstance fi)
        {
            if (fi.Category?.Id.Value != (long)BuiltInCategory.OST_PlumbingFixtures) return false;
            string name = ((fi.Symbol?.FamilyName ?? "") + " " + (fi.Symbol?.Name ?? "") + " " + fi.Name).ToLowerInvariant();
            if (NotFixtureWords.Any(w => name.Contains(w))) return false;
            return true;
        }

        private static bool IsCounter(FamilyInstance fi)
        {
            string name = ((fi.Symbol?.FamilyName ?? "") + " " + (fi.Symbol?.Name ?? "") + " " + fi.Name).ToLowerInvariant();
            if (CounterWords.Any(w => name.Contains(w))) return true;
            return fi.Category?.Id.Value == (long)BuiltInCategory.OST_Casework && !name.Contains("aereo") && !name.Contains("aéreo");
        }

        /// <summary>
        /// Reference of the highest horizontal face of an instance, usable in a dimension: a face of the
        /// instance's own solids, else a face of the family geometry turned into an instance reference
        /// (stable representation "instance:0:INSTANCE:symbol-face"), else its "Top" reference plane.
        /// </summary>
        public static Reference TopFaceRef(Document doc, FamilyInstance fi, out double z)
        {
            z = double.MinValue;
            Reference best = null;
            try
            {
                GeometryElement geo = fi.get_Geometry(new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine });
                if (geo != null)
                    foreach (GeometryObject obj in geo)
                    {
                        if (obj is Solid s && s.Faces.Size > 0)
                        {
                            foreach (Face f in s.Faces)
                                if (f is PlanarFace pf && pf.FaceNormal.Z > 0.99 && pf.Reference != null && pf.Origin.Z > z)
                                {
                                    z = pf.Origin.Z;
                                    best = pf.Reference;
                                }
                        }
                        else if (obj is GeometryInstance gi)
                        {
                            Transform t = gi.Transform;
                            foreach (GeometryObject so in gi.GetSymbolGeometry())
                            {
                                if (!(so is Solid ss) || ss.Faces.Size == 0) continue;
                                foreach (Face f in ss.Faces)
                                {
                                    if (!(f is PlanarFace pf) || pf.Reference == null) continue;
                                    if (t.OfVector(pf.FaceNormal).Z < 0.99) continue;
                                    double fz = t.OfPoint(pf.Origin).Z;
                                    if (fz <= z) continue;
                                    try
                                    {
                                        string rep = pf.Reference.ConvertToStableRepresentation(doc);
                                        Reference r = Reference.ParseFromStableRepresentation(doc, $"{fi.UniqueId}:0:INSTANCE:{rep}");
                                        if (r != null) { best = r; z = fz; }
                                    }
                                    catch { }
                                }
                            }
                        }
                    }
            }
            catch { }
            if (best != null) return best;
            try
            {
                Reference top = fi.GetReferences(FamilyInstanceReferenceType.Top).FirstOrDefault();
                if (top != null)
                {
                    z = fi.get_BoundingBox(null)?.Max.Z ?? 0;
                    return top;
                }
            }
            catch { }
            return null;
        }

        // ------------------------------------------------------------------ floor / top

        private Tuple<Reference, double> FloorRef(Room room, XYZ p, double floorZ)
        {
            Tuple<Reference, double> best = null;
            try
            {
                var box = new Outline(new XYZ(p.X - M(0.01), p.Y - M(0.01), floorZ - M(0.35)), new XYZ(p.X + M(0.01), p.Y + M(0.01), floorZ + M(0.35)));
                foreach (Floor f in new FilteredElementCollector(_doc).OfClass(typeof(Floor)).WherePasses(new BoundingBoxIntersectsFilter(box)).Cast<Floor>())
                {
                    PlanarFace pf = DimEngine.HorizontalFace(f, true);
                    if (pf?.Reference == null) continue;
                    double z = pf.Origin.Z;
                    if (Math.Abs(z - floorZ) > M(0.35)) continue;
                    if (best == null || z > best.Item2) best = Tuple.Create(pf.Reference, z);
                }
            }
            catch { }
            if (best != null) return best;
            try
            {
                Level lvl = room.Level;
                if (lvl != null) return Tuple.Create(lvl.GetPlaneReference(), lvl.ProjectElevation);
            }
            catch { }
            return null;
        }

        /// <summary>Bottom of the ceiling over the room point; else bottom of the slab above.</summary>
        private Tuple<Reference, double> TopRef(XYZ p, double floorZ, double roomTop, out bool isCeiling, out double ceilingTop)
        {
            isCeiling = false;
            ceilingTop = double.NaN;
            Tuple<Reference, double> best = null;
            try
            {
                var box = new Outline(new XYZ(p.X - M(0.01), p.Y - M(0.01), floorZ + M(1.5)), new XYZ(p.X + M(0.01), p.Y + M(0.01), Math.Max(roomTop, floorZ + M(2)) + M(1)));
                foreach (Ceiling c in new FilteredElementCollector(_doc).OfClass(typeof(Ceiling)).WherePasses(new BoundingBoxIntersectsFilter(box)).Cast<Ceiling>())
                {
                    PlanarFace pf = DimEngine.HorizontalFace(c, false);
                    if (pf?.Reference == null) continue;
                    double z = pf.Origin.Z;
                    if (z < floorZ + M(1.5)) continue;
                    if (best == null || z < best.Item2)
                    {
                        best = Tuple.Create(pf.Reference, z);
                        ceilingTop = DimEngine.HorizontalFace(c, true)?.Origin.Z ?? double.NaN;
                    }
                }
                if (best != null) { isCeiling = true; return best; }

                var slab = new Outline(new XYZ(p.X - M(0.01), p.Y - M(0.01), floorZ + M(1.8)), new XYZ(p.X + M(0.01), p.Y + M(0.01), floorZ + M(8)));
                foreach (Floor f in new FilteredElementCollector(_doc).OfClass(typeof(Floor)).WherePasses(new BoundingBoxIntersectsFilter(slab)).Cast<Floor>())
                {
                    PlanarFace pf = DimEngine.HorizontalFace(f, false);
                    if (pf?.Reference == null) continue;
                    double z = pf.Origin.Z;
                    if (z < floorZ + M(1.8)) continue;
                    if (best == null || z < best.Item2) best = Tuple.Create(pf.Reference, z);
                }
            }
            catch { }
            return best;
        }

        // ------------------------------------------------------------------ helpers

        private List<Wall> WallsNear(BoundingBoxXYZ rb)
        {
            var box = new Outline(new XYZ(rb.Min.X - M(1), rb.Min.Y - M(1), rb.Min.Z - M(1)), new XYZ(rb.Max.X + M(1), rb.Max.Y + M(1), rb.Max.Z + M(1)));
            return new FilteredElementCollector(_doc).OfClass(typeof(Wall)).WherePasses(new BoundingBoxIntersectsFilter(box))
                .Cast<Wall>().Where(w => DimEngine.WallLine(w) != null).ToList();
        }

        /// <summary>Vertical dimension at position s (along u, from o) through references at heights.</summary>
        private bool Vertical(ViewSection ev, XYZ u, XYZ o, double s, IEnumerable<Tuple<Reference, double>> refs)
        {
            var list = refs.Where(r => r != null && r.Item1 != null).OrderBy(r => r.Item2).ToList();
            var clean = new List<Tuple<Reference, double>>();
            foreach (var r in list)
                if (clean.Count == 0 || r.Item2 - clean[clean.Count - 1].Item2 > M(0.001)) clean.Add(r);
            if (clean.Count < 2) return false;
            XYZ bp = o + u * s;
            double ext = M(0.1);
            try
            {
                Line l = Line.CreateBound(new XYZ(bp.X, bp.Y, clean[0].Item2 - ext), new XYZ(bp.X, bp.Y, clean[clean.Count - 1].Item2 + ext));
                if (RevitUtil.CreateDimension(_doc, ev, l, clean.Select(x => x.Item1), DimType) != null) { Dims++; return true; }
            }
            catch (Exception ex) { RevitUtil.AddOnce(_problems, $"{ev.Name}: cota de altura ({ex.Message})."); }
            return false;
        }
    }
}
