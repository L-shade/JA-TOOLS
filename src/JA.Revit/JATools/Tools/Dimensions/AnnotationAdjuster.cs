using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using RevitDwgUnifier.Tools.Common;

namespace RevitDwgUnifier.Tools.Dimensions
{
    /// <summary>
    /// Finds overlapping annotations in a view (tags, room tags, text notes, dimension texts) and
    /// moves the movable ones to the nearest free place.
    ///
    ///   * Dimension texts are fixed obstacles (a dimension is never moved).
    ///   * Order of placement: room tags, then text notes, then element tags. Whatever already
    ///     fits stays where it is; only an annotation that overlaps something placed before it
    ///     is moved, to the closest free position on rings around it (16 directions).
    ///   * A room tag only moves inside its room; nothing moves outside the crop region; an element
    ///     tag (or room tag) moved far from where it was gets a leader so it still points at its element.
    /// The boxes are the annotations' extents in the view (model units); moves are computed on the
    /// boxes, so the view is regenerated only once, at the end. Inside an open transaction.
    /// </summary>
    public static class AnnotationAdjuster
    {
        public sealed class Result
        {
            public int Checked, Overlapping, Moved, Unresolved;
        }

        private sealed class Box
        {
            public Element E;
            public double X0, Y0, X1, Y1;
            public int Priority;
            public double W => X1 - X0;
            public double H => Y1 - Y0;
            public Box Shift(double dx, double dy) => new Box { E = E, X0 = X0 + dx, Y0 = Y0 + dy, X1 = X1 + dx, Y1 = Y1 + dy, Priority = Priority };
            public bool Hits(Box o, double gap) => X0 < o.X1 + gap && X1 > o.X0 - gap && Y0 < o.Y1 + gap && Y1 > o.Y0 - gap;
        }

        /// <param name="movable">null = every annotation of the view may move; otherwise only these.</param>
        public static Result Run(Document doc, View view, ICollection<ElementId> movable)
        {
            var res = new Result();
            double scale = Math.Max(1, view.Scale);
            double mm = scale / 304.8;                 // 1 mm of paper in model feet
            double gap = 0.6 * mm;

            var fixedBoxes = DimensionTextBoxes(doc, view);
            var anns = new List<Box>();
            foreach (Element e in new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType())
            {
                int pr;
                if (e is SpatialElementTag) pr = 0;
                else if (e is TextNote) pr = 1;
                else if (e is IndependentTag) pr = 2;
                else continue;
                if (e.ViewSpecific && e.OwnerViewId != view.Id) continue;
                BoundingBoxXYZ bb = e.get_BoundingBox(view);
                if (bb == null) continue;
                anns.Add(new Box { E = e, X0 = bb.Min.X, Y0 = bb.Min.Y, X1 = bb.Max.X, Y1 = bb.Max.Y, Priority = pr });
            }
            res.Checked = anns.Count;

            BoundingBoxXYZ crop = view.CropBoxActive ? view.CropBox : null;
            Transform cropInv = crop?.Transform?.Inverse;

            // placed boxes in a grid: each test looks only at the neighbourhood (views with
            // hundreds of annotations no longer compare everything with everything)
            var placed = new BoxGrid(Math.Max(RevitUtil.M(0.5), 8 * mm));
            foreach (Box f in fixedBoxes) placed.Add(f);
            foreach (Box a in anns.OrderBy(b => b.Priority).ThenByDescending(b => b.Y1).ThenBy(b => b.X0))
            {
                if (!placed.AnyHit(a, gap)) { placed.Add(a); continue; }
                res.Overlapping++;
                bool canMove = movable == null || movable.Contains(a.E.Id);
                if (!canMove) { placed.Add(a); continue; }

                Room room = (a.E as RoomTag)?.Room;
                double roomZ = room != null ? ((room.Location as LocationPoint)?.Point.Z ?? 0) + RevitUtil.M(0.5) : 0;
                double step = Math.Max(Math.Min(a.W, a.H) * 0.6, 1.5 * mm);
                Box best = null;
                double bestDx = 0, bestDy = 0;
                for (int ring = 1; ring <= 16 && best == null; ring++)
                {
                    for (int k = 0; k < 16; k++)
                    {
                        // nearest directions first: right/left/up/down, then the diagonals
                        double ang = Order[k] * Math.PI / 8;
                        double dx = Math.Cos(ang) * ring * step, dy = Math.Sin(ang) * ring * step;
                        Box c = a.Shift(dx, dy);
                        if (placed.AnyHit(c, gap)) continue;
                        if (!InsideCrop(c, crop, cropInv)) continue;
                        if (room != null)
                        {
                            var center = new XYZ((c.X0 + c.X1) / 2, (c.Y0 + c.Y1) / 2, roomZ);
                            bool inside;
                            try { inside = room.IsPointInRoom(center); } catch { inside = true; }
                            if (!inside) continue;
                        }
                        best = c; bestDx = dx; bestDy = dy;
                        break;
                    }
                }
                if (best == null) { res.Unresolved++; placed.Add(a); continue; }

                try
                {
                    bool far = Math.Sqrt(bestDx * bestDx + bestDy * bestDy) > Math.Max(a.W, a.H) * 1.2;
                    Move(doc, a.E, new XYZ(bestDx, bestDy, 0), far);
                    placed.Add(best);
                    res.Moved++;
                }
                catch
                {
                    res.Unresolved++;
                    placed.Add(a);
                }
            }
            if (res.Moved > 0) doc.Regenerate();
            return res;
        }

        /// <summary>Axis-aligned boxes bucketed in square cells (see TagPlacer's ObbGrid).</summary>
        private sealed class BoxGrid
        {
            private readonly double _cell;
            private readonly Dictionary<long, List<Box>> _cells = new Dictionary<long, List<Box>>();
            private readonly List<Box> _big = new List<Box>();

            public BoxGrid(double cell) { _cell = Math.Max(1e-3, cell); }

            private static long Key(int i, int j) => ((long)i << 32) ^ (uint)j;

            public void Add(Box b)
            {
                int i0 = (int)Math.Floor(b.X0 / _cell), i1 = (int)Math.Floor(b.X1 / _cell);
                int j0 = (int)Math.Floor(b.Y0 / _cell), j1 = (int)Math.Floor(b.Y1 / _cell);
                if ((long)(i1 - i0 + 1) * (j1 - j0 + 1) > 400) { _big.Add(b); return; }
                for (int i = i0; i <= i1; i++)
                    for (int j = j0; j <= j1; j++)
                    {
                        long k = Key(i, j);
                        if (!_cells.TryGetValue(k, out List<Box> l)) _cells[k] = l = new List<Box>();
                        l.Add(b);
                    }
            }

            public bool AnyHit(Box b, double gap)
            {
                foreach (Box o in _big) if (o.Hits(b, gap)) return true;
                int i0 = (int)Math.Floor((b.X0 - gap) / _cell), i1 = (int)Math.Floor((b.X1 + gap) / _cell);
                int j0 = (int)Math.Floor((b.Y0 - gap) / _cell), j1 = (int)Math.Floor((b.Y1 + gap) / _cell);
                for (int i = i0; i <= i1; i++)
                    for (int j = j0; j <= j1; j++)
                        if (_cells.TryGetValue(Key(i, j), out List<Box> l))
                            foreach (Box o in l) if (o.Hits(b, gap)) return true;
                return false;
            }
        }

        // right, left, up, down, then the diagonals and the in-betweens
        private static readonly int[] Order = { 0, 8, 4, 12, 2, 6, 10, 14, 1, 7, 9, 15, 3, 5, 11, 13 };

        private static bool InsideCrop(Box b, BoundingBoxXYZ crop, Transform inv)
        {
            if (crop == null || inv == null) return true;
            foreach (var p in new[] { new XYZ(b.X0, b.Y0, 0), new XYZ(b.X1, b.Y1, 0) })
            {
                XYZ q = inv.OfPoint(p);
                if (q.X < crop.Min.X || q.X > crop.Max.X || q.Y < crop.Min.Y || q.Y > crop.Max.Y) return false;
            }
            return true;
        }

        /// <summary>Moves a tag (its head), a room tag or a text by d; adds a leader to a tag moved far.</summary>
        public static void MoveAnnotation(Document doc, Element e, XYZ d, bool far) => Move(doc, e, d, far);

        private static void Move(Document doc, Element e, XYZ d, bool far)
        {
            if (e is IndependentTag tag)
            {
                tag.TagHeadPosition = tag.TagHeadPosition + d;
                if (far && !tag.HasLeader) { try { tag.HasLeader = true; } catch { } }
                return;
            }
            ElementTransformUtils.MoveElement(doc, e.Id, d);
            if (far && e is SpatialElementTag st && !st.HasLeader) { try { st.HasLeader = true; } catch { } }
        }

        /// <summary>Boxes of the dimension value texts of a view as {x0, y0, x1, y1} (model feet).</summary>
        public static List<double[]> DimensionTextRects(Document doc, View view) =>
            DimensionTextBoxes(doc, view).Select(b => new[] { b.X0, b.Y0, b.X1, b.Y1 }).ToList();

        /// <summary>Approximate box of every dimension value text (fixed obstacles).</summary>
        private static List<Box> DimensionTextBoxes(Document doc, View view)
        {
            var list = new List<Box>();
            double scale = Math.Max(1, view.Scale);
            foreach (Dimension d in new FilteredElementCollector(doc, view.Id).OfClass(typeof(Dimension)).Cast<Dimension>())
            {
                try
                {
                    double size = (doc.GetElement(d.GetTypeId()) as DimensionType)?.get_Parameter(BuiltInParameter.TEXT_SIZE)?.AsDouble() ?? (2.5 / 304.8);
                    double th = size * scale;
                    bool vertical = false;
                    if (d.Curve is Line l) vertical = Math.Abs(l.Direction.Y) > Math.Abs(l.Direction.X);
                    var texts = new List<Tuple<XYZ, string>>();
                    if (d.NumberOfSegments > 1)
                        foreach (DimensionSegment s in d.Segments) texts.Add(Tuple.Create(s.TextPosition, s.ValueString));
                    else texts.Add(Tuple.Create(d.TextPosition, d.ValueString));
                    foreach (var t in texts)
                    {
                        if (t.Item1 == null) continue;
                        double w = Math.Max(3, (t.Item2 ?? "0.00").Length) * th * 0.75, h = th * 1.3;
                        if (vertical) { double tmp = w; w = h; h = tmp; }
                        list.Add(new Box { E = d, X0 = t.Item1.X - w / 2, X1 = t.Item1.X + w / 2, Y0 = t.Item1.Y - h / 2, Y1 = t.Item1.Y + h / 2, Priority = -1 });
                    }
                }
                catch { }
            }
            return list;
        }
    }
}
