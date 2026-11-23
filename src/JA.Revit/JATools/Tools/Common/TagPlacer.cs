using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using RevitDwgUnifier.Infrastructure;
using RevitDwgUnifier.Tools.Dimensions;

namespace RevitDwgUnifier.Tools.Common
{
    /// <summary>Oriented rectangle in plan (model X/Y, feet): centre, unit axis u (v = u turned 90°), half sizes.</summary>
    public sealed class Obb
    {
        public double CX, CY, UX = 1, UY, HU, HV;
        public ElementId Id;          // element it comes from (null for annotations)
        public double Weight = 1;     // cost of covering it
        internal int Mark;            // ObbGrid query stamp

        public double VX => -UY;
        public double VY => UX;

        public static Obb Axis(double x0, double y0, double x1, double y1, double weight = 1, ElementId id = null) =>
            new Obb { CX = (x0 + x1) / 2, CY = (y0 + y1) / 2, HU = Math.Abs(x1 - x0) / 2, HV = Math.Abs(y1 - y0) / 2, Weight = weight, Id = id };

        public Obb At(double cx, double cy) => new Obb { CX = cx, CY = cy, UX = UX, UY = UY, HU = HU, HV = HV, Id = Id, Weight = Weight };

        public bool Contains(double x, double y, double tol = 0)
        {
            double dx = x - CX, dy = y - CY;
            return Math.Abs(dx * UX + dy * UY) <= HU + tol && Math.Abs(dx * VX + dy * VY) <= HV + tol;
        }

        /// <summary>Half extent of the rectangle measured along a unit direction (nx, ny).</summary>
        public double Extent(double nx, double ny) => HU * Math.Abs(UX * nx + UY * ny) + HV * Math.Abs(VX * nx + VY * ny);

        /// <summary>Separating-axis test (gap = extra clearance).</summary>
        public bool Hits(Obb o, double gap)
        {
            double dx = o.CX - CX, dy = o.CY - CY;
            foreach (var ax in new[] { new[] { UX, UY }, new[] { VX, VY }, new[] { o.UX, o.UY }, new[] { o.VX, o.VY } })
            {
                double d = Math.Abs(dx * ax[0] + dy * ax[1]);
                if (d > Extent(ax[0], ax[1]) + o.Extent(ax[0], ax[1]) + gap) return false;
            }
            return true;
        }

        public IEnumerable<XYZ> Corners(double z = 0)
        {
            foreach (int su in new[] { -1, 1 })
                foreach (int sv in new[] { -1, 1 })
                    yield return new XYZ(CX + su * HU * UX + sv * HV * VX, CY + su * HU * UY + sv * HV * VY, z);
        }
    }

    /// <summary>
    /// Rectangles bucketed in square cells: "what is near this rectangle" without walking every
    /// obstacle of the view (a whole floor plan has thousands). Very large rectangles (a long wall
    /// across the plan) go to a short list that is always returned.
    /// </summary>
    public sealed class ObbGrid
    {
        private readonly double _cell;
        private readonly Dictionary<long, List<Obb>> _cells = new Dictionary<long, List<Obb>>();
        private readonly List<Obb> _big = new List<Obb>();
        private int _stamp;

        public ObbGrid(double cell) { _cell = Math.Max(1e-3, cell); }

        private static long Key(int i, int j) => ((long)i << 32) ^ (uint)j;

        private void Range(Obb o, double margin, out int i0, out int j0, out int i1, out int j1)
        {
            double ex = o.Extent(1, 0) + margin, ey = o.Extent(0, 1) + margin;
            i0 = (int)Math.Floor((o.CX - ex) / _cell); i1 = (int)Math.Floor((o.CX + ex) / _cell);
            j0 = (int)Math.Floor((o.CY - ey) / _cell); j1 = (int)Math.Floor((o.CY + ey) / _cell);
        }

        public void Add(Obb o)
        {
            Range(o, 0, out int i0, out int j0, out int i1, out int j1);
            if ((long)(i1 - i0 + 1) * (j1 - j0 + 1) > 400) { _big.Add(o); return; }
            for (int i = i0; i <= i1; i++)
                for (int j = j0; j <= j1; j++)
                {
                    long k = Key(i, j);
                    if (!_cells.TryGetValue(k, out List<Obb> l)) _cells[k] = l = new List<Obb>();
                    l.Add(o);
                }
        }

        /// <summary>Every rectangle that may touch q (+margin); each one once.</summary>
        public List<Obb> Near(Obb q, double margin)
        {
            _stamp++;
            var result = new List<Obb>(_big);
            Range(q, margin, out int i0, out int j0, out int i1, out int j1);
            for (int i = i0; i <= i1; i++)
                for (int j = j0; j <= j1; j++)
                {
                    if (!_cells.TryGetValue(Key(i, j), out List<Obb> l)) continue;
                    foreach (Obb o in l)
                    {
                        if (o.Mark == _stamp) continue;
                        o.Mark = _stamp;
                        result.Add(o);
                    }
                }
            return result;
        }
    }

    /// <summary>
    /// Places element tags in plan like a draftsman would:
    ///   * doors and windows: in front of the opening (the room side when a room is given), centred
    ///     on it and turned to read along the wall (vertical wall = vertical tag, any angle works);
    ///   * long, narrow pieces (a "tento", a threshold, a shelf): on the piece itself, along it;
    ///   * everything else (a basin, a toilet, a column): beside the piece, on the first free side
    ///     (right, left, below, above), never over walls, other pieces, tags or dimension texts —
    ///     a counter that holds the basin does not count as an obstacle, so the tag sits on the
    ///     free part of the counter.
    /// Tags are created at the element, the view is regenerated once to measure them, then each is
    /// moved to its place. Inside an open transaction. Plans only (other views: centre).
    /// </summary>
    public static class TagPlacer
    {
        private enum Kind { Opening, Linear, Point }

        private sealed class Item
        {
            public Element E;
            public IndependentTag Tag;
            public Kind K;
            public Obb Foot;
            public double NX, NY;         // opening: wall normal
            public double WallHalf;       // opening: half thickness of the wall
            public double Angle;          // reading direction of the tag (radians, view = plan X/Y)
            public ElementId HostId;
            public Room Room;             // the room the tag must stay in (null = any)
        }

        /// <summary>One category (or group) of elements to tag with one tag type.</summary>
        public sealed class TagJob
        {
            public IList<Element> Elements;
            public ElementId TypeId;      // null = category default
            public bool Multi;            // TypeId is a multi-category tag
            public string What;           // name for messages
            public int Created;           // tags made for this job (filled by TagMany)
        }

        private static readonly PropertyInfo RotationProp = typeof(IndependentTag).GetProperty("RotationAngle");

        /// <summary>element category -> its tag category (names parsed at run time).</summary>
        private static readonly string[][] TagCats =
        {
            new[] { "OST_Doors", "OST_DoorTags" }, new[] { "OST_Windows", "OST_WindowTags" },
            new[] { "OST_PlumbingFixtures", "OST_PlumbingFixtureTags" }, new[] { "OST_Casework", "OST_CaseworkTags" },
            new[] { "OST_Furniture", "OST_FurnitureTags" }, new[] { "OST_SpecialityEquipment", "OST_SpecialityEquipmentTags" },
            new[] { "OST_LightingFixtures", "OST_LightingFixtureTags" }, new[] { "OST_ElectricalFixtures", "OST_ElectricalFixtureTags" },
            new[] { "OST_LightingDevices", "OST_LightingDeviceTags" }, new[] { "OST_PipeAccessory", "OST_PipeAccessoryTags" },
            new[] { "OST_StructuralColumns", "OST_StructuralColumnTags" }, new[] { "OST_Columns", "OST_ColumnTags" },
            new[] { "OST_GenericModel", "OST_GenericModelTags" }, new[] { "OST_PlumbingEquipment", "OST_PlumbingEquipmentTags" },
            new[] { "OST_ElectricalEquipment", "OST_ElectricalEquipmentTags" }, new[] { "OST_MechanicalEquipment", "OST_MechanicalEquipmentTags" },
            new[] { "OST_PipeFitting", "OST_PipeFittingTags" }, new[] { "OST_CommunicationDevices", "OST_CommunicationDeviceTags" },
            new[] { "OST_DataDevices", "OST_DataDeviceTags" }, new[] { "OST_Parking", "OST_ParkingTags" },
            new[] { "OST_StairsRailing", "OST_StairsRailingTags" }
        };

        public static BuiltInCategory? TagCategoryFor(BuiltInCategory elementCat)
        {
            string name = elementCat.ToString();
            foreach (var m in TagCats)
                if (m[0] == name && Enum.TryParse(m[1], out BuiltInCategory t)) return t;
            return null;
        }

        /// <summary>Categories whose pieces are obstacles for tags (besides walls).</summary>
        private static readonly string[] ObstacleCats =
        {
            "OST_Doors", "OST_Windows", "OST_PlumbingFixtures", "OST_Casework", "OST_Furniture", "OST_SpecialityEquipment",
            "OST_GenericModel", "OST_Columns", "OST_StructuralColumns", "OST_PlumbingEquipment", "OST_ElectricalEquipment"
        };

        /// <summary>
        /// Tags the elements (already filtered: marked, not yet tagged) and places each tag.
        /// typeId: tag type to use (null = the category default); multi = typeId is a multi-category tag.
        /// room: when given, tags stay inside it (doors: on its side).
        /// </summary>
        public static List<IndependentTag> Tag(Document doc, View view, IList<Element> elements, ElementId typeId, Room room,
                                               bool leader, List<string> problems, string what, bool multi = false) =>
            TagMany(doc, view, new[] { new TagJob { Elements = elements, TypeId = typeId, Multi = multi, What = what } }, room, leader, problems);

        /// <summary>
        /// Several categories at once (faster: the view is regenerated once and the walls / pieces
        /// are read once for all the tags). room = null: each piece stays in the room it is in.
        /// </summary>
        public static List<IndependentTag> TagMany(Document doc, View view, IEnumerable<TagJob> jobs, Room room,
                                                   bool leader, List<string> problems)
        {
            var result = new List<IndependentTag>();
            var items = new List<Item>();
            bool plan = view is ViewPlan;
            foreach (TagJob job in jobs)
            {
                if (job?.Elements == null) continue;
                foreach (Element e in job.Elements)
                {
                    Item it;
                    try { it = Describe(doc, view, e); } catch { it = null; }
                    if (it == null)
                    {
                        XYZ c = RevitUtil.Center(e, view);
                        if (c == null) continue;
                        it = new Item { E = e, K = Kind.Point, Foot = new Obb { CX = c.X, CY = c.Y, HU = RevitUtil.M(0.1), HV = RevitUtil.M(0.1), Id = e.Id } };
                    }
                    it.Room = room ?? (plan && it.K != Kind.Opening ? RoomAt(doc, view, it.Foot) : null);
                    XYZ head = new XYZ(it.Foot.CX, it.Foot.CY, RevitUtil.Center(e, view)?.Z ?? 0);
                    TagOrientation orient = OrientationFor(it.Angle, out bool setAngle, out double angle);
                    it.Angle = angle;
                    try
                    {
                        var tag = IndependentTag.Create(doc, view.Id, new Reference(e), leader,
                            job.Multi ? TagMode.TM_ADDBY_MULTICATEGORY : TagMode.TM_ADDBY_CATEGORY, orient, head);
                        if (tag == null) continue;
                        if (job.TypeId != null && job.TypeId != ElementId.InvalidElementId) { try { tag.ChangeTypeId(job.TypeId); } catch { } }
                        if (setAngle) { try { RotationProp?.SetValue(tag, angle); } catch { } }
                        it.Tag = tag;
                        items.Add(it);
                        result.Add(tag);
                        job.Created++;
                    }
                    catch (Exception ex)
                    {
                        RevitUtil.AddOnce(problems, $"Etiqueta de {job.What}: {ex.Message} (carregue uma família de etiqueta dessa categoria).");
                        break; // same failure for every element of the category
                    }
                }
            }
            if (items.Count == 0 || !plan) return result;

            try
            {
                doc.Regenerate();
                Place(doc, view, items);
            }
            catch (Exception ex)
            {
                Logger.Warn("[JA Tools] Posicionamento das etiquetas: " + ex.Message);
            }
            return result;
        }

        /// <summary>The room at the centre of a piece (plan level + 0.5 m), or null.</summary>
        private static Room RoomAt(Document doc, View view, Obb foot)
        {
            try
            {
                double z = ((view as ViewPlan)?.GenLevel?.ProjectElevation ?? 0) + RevitUtil.M(0.5);
                return doc.GetRoomAtPoint(new XYZ(foot.CX, foot.CY, z));
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------ description

        private static Item Describe(Document doc, View view, Element e)
        {
            long cid = e.Category?.Id.Value ?? 0;
            bool isOpening = (cid == (long)BuiltInCategory.OST_Doors || cid == (long)BuiltInCategory.OST_Windows)
                             && e is FamilyInstance ofi && ofi.Host is Wall;
            if (isOpening)
            {
                Obb ob = OpeningBox((FamilyInstance)e);
                if (ob == null) return null;
                return new Item
                {
                    E = e, K = Kind.Opening, NX = -ob.VX, NY = -ob.VY, WallHalf = ob.HV, HostId = ((FamilyInstance)e).Host.Id,
                    Angle = Math.Atan2(ob.UY, ob.UX), Foot = ob
                };
            }

            Obb foot = Footprint(e, view);
            if (foot == null) return null;
            double longHalf = Math.Max(foot.HU, foot.HV), shortHalf = Math.Min(foot.HU, foot.HV);
            if (longHalf >= 3 * shortHalf && longHalf * 2 >= RevitUtil.M(0.25))
            {
                double ang = foot.HU >= foot.HV ? Math.Atan2(foot.UY, foot.UX) : Math.Atan2(foot.VY, foot.VX);
                return new Item { E = e, K = Kind.Linear, Foot = foot, Angle = ang };
            }
            return new Item { E = e, K = Kind.Point, Foot = foot, Angle = 0 };
        }

        /// <summary>
        /// A door or window in plan: centred on the wall's centre line (exact for any location line),
        /// u along the wall (width/2), v = minus the facing direction (half the wall thickness).
        /// </summary>
        public static Obb OpeningBox(FamilyInstance fi)
        {
            if (!(fi?.Host is Wall wall)) return null;
            XYZ n = new XYZ(fi.FacingOrientation.X, fi.FacingOrientation.Y, 0);
            Line wl = DimEngine.WallLine(wall);
            if (n.GetLength() < 1e-6 && wl != null) { XYZ d = DimEngine.FlatDir(wl); n = new XYZ(-d.Y, d.X, 0); }
            if (n.GetLength() < 1e-6) return null;
            n = n.Normalize();
            XYZ p = RevitUtil.Flat((fi.Location as LocationPoint)?.Point ?? RevitUtil.Center(fi) ?? XYZ.Zero);

            // wall centre and thickness across n, from its side faces
            double lo = double.MaxValue, hi = double.MinValue;
            foreach (var f in DimEngine.FinishFaces(wall))     // full thickness, with the revestimento
            {
                double s = RevitUtil.Flat(f.Item2.Origin).DotProduct(n);
                lo = Math.Min(lo, s); hi = Math.Max(hi, s);
            }
            double centre = lo <= hi ? (lo + hi) / 2 : p.DotProduct(n);
            double half = lo <= hi && hi - lo > 1e-6 ? (hi - lo) / 2 : wall.Width / 2;
            XYZ c = p + n * (centre - p.DotProduct(n));

            double width = 0;
            foreach (BuiltInParameter bip in new[] { BuiltInParameter.DOOR_WIDTH, BuiltInParameter.WINDOW_WIDTH, BuiltInParameter.FAMILY_WIDTH_PARAM })
            {
                double w = fi.get_Parameter(bip)?.AsDouble() ?? 0;
                if (w < 1e-6) w = fi.Symbol?.get_Parameter(bip)?.AsDouble() ?? 0;
                if (w > 1e-6) { width = w; break; }
            }
            if (width < 1e-6) width = RevitUtil.M(0.8);
            // u = (-n.Y, n.X) so that v = (-u.Y, u.X) = -n
            return new Obb { CX = c.X, CY = c.Y, UX = -n.Y, UY = n.X, HU = width / 2, HV = half, Id = fi.Id };
        }

        /// <summary>Plan footprint: the family's own box turned with the instance; else the location
        /// line; else the view box.</summary>
        public static Obb Footprint(Element e, View view)
        {
            if (e is FamilyInstance fi)
            {
                try
                {
                    GeometryElement g = fi.GetOriginalGeometry(new Options { DetailLevel = ViewDetailLevel.Fine });
                    BoundingBoxXYZ lb = g?.GetBoundingBox();
                    Transform t = fi.GetTotalTransform();
                    if (lb != null && t != null && lb.Max.X - lb.Min.X > 1e-6 && lb.Max.Y - lb.Min.Y > 1e-6)
                    {
                        XYZ c = t.OfPoint((lb.Min + lb.Max) / 2);
                        XYZ ux = t.OfVector(XYZ.BasisX), uy = t.OfVector(XYZ.BasisY);
                        var fx = new XYZ(ux.X, ux.Y, 0);
                        if (fx.GetLength() > 0.5 && Math.Abs(uy.Z) < 0.5)
                        {
                            fx = fx.Normalize();
                            return new Obb
                            {
                                CX = c.X, CY = c.Y, UX = fx.X, UY = fx.Y, Id = e.Id,
                                HU = (lb.Max.X - lb.Min.X) / 2 * ux.GetLength(),
                                HV = (lb.Max.Y - lb.Min.Y) / 2 * uy.GetLength()
                            };
                        }
                    }
                }
                catch { }
            }
            BoundingBoxXYZ bb = e.get_BoundingBox(view) ?? e.get_BoundingBox(null);
            if (e.Location is LocationCurve lc && lc.Curve is Line l)
            {
                XYZ d = DimEngine.FlatDir(l);
                XYZ m = RevitUtil.Flat(l.Evaluate(0.5, true));
                double hv = e is Wall w ? w.Width / 2 : RevitUtil.M(0.05);
                if (!(e is Wall) && bb != null)
                {
                    var n = new XYZ(-d.Y, d.X, 0);
                    var ps = new[] { bb.Min, bb.Max, new XYZ(bb.Min.X, bb.Max.Y, 0), new XYZ(bb.Max.X, bb.Min.Y, 0) }.Select(q => (RevitUtil.Flat(q) - m).DotProduct(n)).ToList();
                    hv = Math.Max(RevitUtil.M(0.01), Math.Min(Math.Abs(ps.Min()), Math.Abs(ps.Max())));
                }
                return new Obb { CX = m.X, CY = m.Y, UX = d.X, UY = d.Y, HU = l.Length / 2, HV = hv, Id = e.Id };
            }
            if (bb == null) return null;
            return Obb.Axis(bb.Min.X, bb.Min.Y, bb.Max.X, bb.Max.Y, 1, e.Id);
        }

        /// <summary>Horizontal / vertical when the reading direction is (nearly) one of them; else
        /// "model direction" with the angle (Revit 2023+), else the closest of the two.</summary>
        private static TagOrientation OrientationFor(double a, out bool setAngle, out double angle)
        {
            setAngle = false;
            while (a <= -Math.PI / 2 + 1e-9) a += Math.PI;
            while (a > Math.PI / 2 + 1e-9) a -= Math.PI;
            double tol = Math.PI / 180;
            if (Math.Abs(a) < tol) { angle = 0; return TagOrientation.Horizontal; }
            if (Math.Abs(Math.Abs(a) - Math.PI / 2) < tol) { angle = Math.PI / 2; return TagOrientation.Vertical; }
            if (RotationProp != null && RotationProp.CanWrite)
            {
                angle = a;
                setAngle = true;
                return TagOrientation.AnyModelDirection;
            }
            if (Math.Abs(a) < Math.PI / 4) { angle = 0; return TagOrientation.Horizontal; }
            angle = Math.PI / 2;
            return TagOrientation.Vertical;
        }

        // ------------------------------------------------------------------ placement

        private static void Place(Document doc, View view, List<Item> items)
        {
            double mm = Math.Max(1, view.Scale) / 304.8;
            double gap = 1.5 * mm;
            var ours = new HashSet<ElementId>(items.Select(i => i.Tag.Id));
            // obstacles in a grid: each candidate is compared only with what is near it
            var obstacles = new ObbGrid(Math.Max(RevitUtil.M(0.5), 8 * mm));
            foreach (Obb o in Obstacles(doc, view, ours)) obstacles.Add(o);
            BoundingBoxXYZ crop = view.CropBoxActive ? view.CropBox : null;
            Transform cropInv = crop?.Transform?.Inverse;
            foreach (Item it in items.OrderBy(i => (int)i.K))
            {
                Room room = it.Room;
                double roomZ = room != null ? (room.Level?.ProjectElevation ?? 0) + room.BaseOffset + RevitUtil.M(1.0) : 0;
                BoundingBoxXYZ bb = it.Tag.get_BoundingBox(view);
                if (bb == null) continue;
                XYZ head = it.Tag.TagHeadPosition;
                double bcx = (bb.Min.X + bb.Max.X) / 2, bcy = (bb.Min.Y + bb.Max.Y) / 2;
                double dx = bcx - head.X, dy = bcy - head.Y;
                Obb box = TagBox(bb, it.Angle);

                Obb best = null;
                double bestCost = double.MaxValue;
                foreach (Obb cand in Candidates(it, box, gap, mm))
                {
                    double cost = Cost(cand, it, obstacles, gap * 0.5, room, roomZ, crop, cropInv, bestCost);
                    if (cost < bestCost - 1e-9) { bestCost = cost; best = cand; }
                    if (cost <= 0) break;
                }
                if (best == null) continue;
                try
                {
                    it.Tag.TagHeadPosition = new XYZ(best.CX - dx, best.CY - dy, head.Z);
                }
                catch { best = box; }
                var placed = best.At(best.CX, best.CY);
                placed.Weight = 3;
                placed.Id = null;
                obstacles.Add(placed);
            }
        }

        /// <summary>The tag as a rectangle along its reading direction, from its view box.</summary>
        private static Obb TagBox(BoundingBoxXYZ bb, double angle)
        {
            double bx = (bb.Max.X - bb.Min.X) / 2, by = (bb.Max.Y - bb.Min.Y) / 2;
            double c = Math.Abs(Math.Cos(angle)), s = Math.Abs(Math.Sin(angle));
            double hu, hv;
            if (s < 1e-3) { hu = bx; hv = by; }
            else if (c < 1e-3) { hu = by; hv = bx; }
            else
            {
                double det = c * c - s * s;
                if (Math.Abs(det) > 0.3)
                {
                    hu = (bx * c - by * s) / det;
                    hv = (by * c - bx * s) / det;
                }
                else { hv = Math.Min(bx, by) * 0.3; hu = (bx - hv * s) / c; }
                hu = Math.Max(hu, 1e-3); hv = Math.Max(hv, 1e-3);
            }
            return new Obb { CX = (bb.Min.X + bb.Max.X) / 2, CY = (bb.Min.Y + bb.Max.Y) / 2, UX = Math.Cos(angle), UY = Math.Sin(angle), HU = hu, HV = hv };
        }

        private static IEnumerable<Obb> Candidates(Item it, Obb box, double gap, double mm)
        {
            Obb f = it.Foot;
            switch (it.K)
            {
                case Kind.Opening:
                {
                    // in front of the opening, centred, both sides (the cost decides), a little further each ring
                    double across = box.Extent(it.NX, it.NY);
                    for (int k = 0; k < 4; k++)
                        foreach (int side in new[] { 1, -1 })
                        {
                            double d = it.WallHalf + across + gap * (1 + 1.5 * k);
                            yield return box.At(f.CX + it.NX * d * side, f.CY + it.NY * d * side);
                        }
                    // last resort: slid along the wall
                    foreach (int side in new[] { 1, -1 })
                        foreach (int slide in new[] { 1, -1 })
                        {
                            double d = it.WallHalf + across + gap;
                            double sl = f.HU * 0.6 * slide;
                            yield return box.At(f.CX + it.NX * d * side + f.UX * sl, f.CY + it.NY * d * side + f.UY * sl);
                        }
                    break;
                }
                case Kind.Linear:
                {
                    // on the piece itself, along it
                    bool alongU = f.HU >= f.HV;
                    double ax = alongU ? f.UX : f.VX, ay = alongU ? f.UY : f.VY;
                    double len = alongU ? f.HU : f.HV;
                    double step = Math.Max(box.HU, len / 4);
                    yield return box.At(f.CX, f.CY);
                    for (int j = 1; j <= 3; j++)
                        foreach (int s in new[] { 1, -1 })
                        {
                            double t = j * step * s;
                            if (Math.Abs(t) + box.HU > len + box.HU * 0.5) continue;
                            yield return box.At(f.CX + ax * t, f.CY + ay * t);
                        }
                    // beside it, if the piece is covered by something else
                    double nx = -ay, ny = ax;
                    double side = f.Extent(nx, ny) + box.Extent(nx, ny) + gap;
                    yield return box.At(f.CX + nx * side, f.CY + ny * side);
                    yield return box.At(f.CX - nx * side, f.CY - ny * side);
                    break;
                }
                default:
                {
                    // beside the piece: right, left, below, above, then the corners, then further out
                    double ex = f.Extent(1, 0), ey = f.Extent(0, 1);
                    double tx = box.Extent(1, 0), ty = box.Extent(0, 1);
                    for (int k = 0; k < 3; k++)
                    {
                        double g = gap * (1 + 2.5 * k);
                        double rx = ex + tx + g, ry = ey + ty + g;
                        yield return box.At(f.CX + rx, f.CY);
                        yield return box.At(f.CX - rx, f.CY);
                        yield return box.At(f.CX, f.CY - ry);
                        yield return box.At(f.CX, f.CY + ry);
                        yield return box.At(f.CX + rx, f.CY - ry);
                        yield return box.At(f.CX - rx, f.CY - ry);
                        yield return box.At(f.CX + rx, f.CY + ry);
                        yield return box.At(f.CX - rx, f.CY + ry);
                    }
                    break;
                }
            }
        }

        private static double Cost(Obb cand, Item it, ObbGrid obstacles, double gap, Room room, double roomZ,
                                   BoundingBoxXYZ crop, Transform cropInv, double bound)
        {
            double cost = 0;
            foreach (Obb o in obstacles.Near(cand, gap))
            {
                if (o.Id != null)
                {
                    if (o.Id == it.E.Id) continue;                       // the piece itself
                    if (it.K != Kind.Opening && o.Weight <= 1 && o.Contains(it.Foot.CX, it.Foot.CY)) continue;  // its counter / container
                }
                if (cand.Hits(o, gap)) cost += o.Weight;
            }
            if (cost >= bound) return cost;                          // already worse than the best: skip the room test
            if (room != null)
            {
                bool inside;
                try { inside = room.IsPointInRoom(new XYZ(cand.CX, cand.CY, roomZ)); } catch { inside = true; }
                if (!inside) cost += 6;
            }
            if (crop != null && cropInv != null)
            {
                foreach (XYZ p in cand.Corners(crop.Transform.Origin.Z))
                {
                    XYZ q = cropInv.OfPoint(p);
                    if (q.X < crop.Min.X || q.X > crop.Max.X || q.Y < crop.Min.Y || q.Y > crop.Max.Y) { cost += 10; break; }
                }
            }
            return cost;
        }

        /// <summary>Walls (weight 4), annotations and dimension texts (3), pieces (1).</summary>
        private static List<Obb> Obstacles(Document doc, View view, HashSet<ElementId> ours)
        {
            var list = new List<Obb>();
            foreach (Element e in new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType())
            {
                if (!(e is IndependentTag || e is SpatialElementTag || e is TextNote) || ours.Contains(e.Id)) continue;
                BoundingBoxXYZ b = e.get_BoundingBox(view);
                if (b != null) list.Add(Obb.Axis(b.Min.X, b.Min.Y, b.Max.X, b.Max.Y, 3));
            }
            foreach (double[] r in AnnotationAdjuster.DimensionTextRects(doc, view))
                list.Add(Obb.Axis(r[0], r[1], r[2], r[3], 3));
            foreach (Wall w in new FilteredElementCollector(doc, view.Id).OfClass(typeof(Wall)).Cast<Wall>())
            {
                Line l = DimEngine.WallLine(w);
                if (l == null) continue;
                XYZ d = DimEngine.FlatDir(l), m = RevitUtil.Flat(l.Evaluate(0.5, true));
                list.Add(new Obb { CX = m.X, CY = m.Y, UX = d.X, UY = d.Y, HU = l.Length / 2, HV = w.Width / 2, Weight = 4, Id = w.Id });
            }
            var cats = new List<BuiltInCategory>();
            foreach (string n in ObstacleCats) if (Enum.TryParse(n, out BuiltInCategory c)) cats.Add(c);
            foreach (Element e in new FilteredElementCollector(doc, view.Id).WherePasses(new ElementMulticategoryFilter(cats)).WhereElementIsNotElementType())
            {
                Obb f;
                try { f = Footprint(e, view); } catch { f = null; }
                if (f == null) continue;
                f.Weight = 1;
                f.Id = e.Id;
                list.Add(f);
            }
            return list;
        }
    }
}
