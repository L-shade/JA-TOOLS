using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using RevitDwgUnifier.Tools.Common;

namespace RevitDwgUnifier.Tools.Modeling
{
    /// <summary>Geometry and type helpers for the Modelagem tools.</summary>
    public static class ModelUtil
    {
        // ------------------------------------------------------------------ room outlines

        public static IList<IList<BoundarySegment>> Segments(Room r, SpatialElementBoundaryLocation where = SpatialElementBoundaryLocation.Finish) =>
            RevitUtil.Boundary(r, where);

        /// <summary>Closed loops of the room (first = outer, then holes: columns, shafts), flattened
        /// to Z = <paramref name="z"/>. Tiny segments (&lt; 1 mm) are merged into their neighbours.</summary>
        public static List<CurveLoop> RoomLoops(Room r, double z, List<string> problems)
        {
            var loops = new List<CurveLoop>();
            foreach (var segs in Segments(r))
            {
                var curves = segs.Select(s => Flatten(s.GetCurve(), z)).Where(c => c != null && c.Length > RevitUtil.M(0.001)).ToList();
                if (curves.Count < 3) continue;
                try
                {
                    var loop = new CurveLoop();
                    for (int i = 0; i < curves.Count; i++)
                    {
                        Curve c = curves[i];
                        // Close tiny gaps left by the removed micro-segments.
                        XYZ start = i == 0 ? c.GetEndPoint(0) : loop.Last().GetEndPoint(1);
                        XYZ end = i == curves.Count - 1 ? curves[0].GetEndPoint(0) : c.GetEndPoint(1);
                        if (c is Line) c = Line.CreateBound(start, end);
                        loop.Append(c);
                    }
                    loops.Add(loop);
                }
                catch (Exception ex)
                {
                    RevitUtil.AddOnce(problems, $"Ambiente {r.Number}: contorno com falhas ({ex.Message}).");
                }
            }
            return loops;
        }

        public static Curve Flatten(Curve c, double z)
        {
            try
            {
                Transform t = Transform.CreateTranslation(new XYZ(0, 0, z - c.GetEndPoint(0).Z));
                return c.CreateTransformed(t);
            }
            catch { return null; }
        }

        // ------------------------------------------------------------------ types

        public static List<FamilySymbol> Symbols(Document doc, BuiltInCategory cat) =>
            new FilteredElementCollector(doc).OfCategory(cat).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .OrderBy(s => s.FamilyName).ThenBy(s => s.Name).ToList();

        public static string SymbolLabel(FamilySymbol s) => s.FamilyName + " : " + s.Name;

        /// <summary>A Basic wall type with one single layer (material, function, width); reused
        /// by name, created by duplicating <paramref name="baseType"/> otherwise.</summary>
        public static WallType SingleLayerWallType(Document doc, WallType baseType, string name, MaterialFunctionAssignment function, ElementId material, double width)
        {
            var existing = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>().FirstOrDefault(t => t.Name == name);
            if (existing != null) return existing;
            var wt = (WallType)baseType.Duplicate(name);
            wt.SetCompoundStructure(CompoundStructure.CreateSingleLayerCompoundStructure(function, width, material));
            return wt;
        }

        public static FloorType SingleLayerFloorType(Document doc, FloorType baseType, string name, MaterialFunctionAssignment function, ElementId material, double width)
        {
            var existing = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().FirstOrDefault(t => t.Name == name);
            if (existing != null) return existing;
            var ft = (FloorType)baseType.Duplicate(name);
            ft.SetCompoundStructure(CompoundStructure.CreateSingleLayerCompoundStructure(function, width, material));
            return ft;
        }

        public static string MaterialName(Document doc, ElementId id) =>
            id != null && id != ElementId.InvalidElementId ? (doc.GetElement(id)?.Name ?? "Material") : "Sem material";

        public static string Cm(double feet) => (RevitUtil.ToM(feet) * 100).ToString("0.#");

        // ------------------------------------------------------------------ 2D line math

        /// <summary>Intersection of two unbounded lines in plan (null when parallel).</summary>
        public static XYZ Intersect(XYZ p1, XYZ d1, XYZ p2, XYZ d2)
        {
            double den = d1.X * d2.Y - d1.Y * d2.X;
            if (Math.Abs(den) < 1e-9) return null;
            double t = ((p2.X - p1.X) * d2.Y - (p2.Y - p1.Y) * d2.X) / den;
            return new XYZ(p1.X + d1.X * t, p1.Y + d1.Y * t, p1.Z);
        }

        /// <summary>
        /// Offsets a closed room loop toward the inside of the room by <paramref name="dist"/>,
        /// keeping clean corners for straight segments (consecutive offset lines are intersected).
        /// Arcs are offset with Curve.CreateOffset. Returns one curve per original segment, plus
        /// the id of the element (wall) that produced it.
        /// </summary>
        public static List<Tuple<Curve, ElementId>> InsetLoop(Room r, IList<BoundarySegment> segs, double dist, double z)
        {
            var src = segs.Select(s => Tuple.Create(Flatten(s.GetCurve(), z), s.ElementId)).Where(t => t.Item1 != null && t.Item1.Length > RevitUtil.M(0.001)).ToList();
            int n = src.Count;
            var result = new List<Tuple<Curve, ElementId>>();
            if (n == 0) return result;

            // Side of the room: left of the segment direction for Revit's loops; checked once.
            int side = 1;
            Curve c0 = src[0].Item1;
            XYZ m0 = c0.Evaluate(0.5, true), t0 = c0.ComputeDerivatives(0.5, true).BasisX.Normalize();
            XYZ left0 = XYZ.BasisZ.CrossProduct(t0);
            if (!r.IsPointInRoom(m0 + left0 * RevitUtil.M(0.05) + new XYZ(0, 0, RevitUtil.M(0.5)))) side = -1;

            var offs = new List<Curve>();
            foreach (var s in src)
            {
                Curve c = s.Item1;
                if (c is Line l)
                {
                    XYZ d = l.Direction;
                    XYZ left = XYZ.BasisZ.CrossProduct(d) * side;
                    offs.Add(Line.CreateBound(l.GetEndPoint(0) + left * dist, l.GetEndPoint(1) + left * dist));
                }
                else
                {
                    try { offs.Add(c.CreateOffset(-side * dist, XYZ.BasisZ)); }
                    catch { offs.Add(c); }
                }
            }

            // Re-trim consecutive straight offsets at their intersection (miter corners).
            var starts = offs.Select(c => c.GetEndPoint(0)).ToArray();
            var ends = offs.Select(c => c.GetEndPoint(1)).ToArray();
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                if (offs[i] is Line li && offs[j] is Line lj)
                {
                    XYZ x = Intersect(li.GetEndPoint(0), li.Direction, lj.GetEndPoint(0), lj.Direction);
                    if (x != null && x.DistanceTo(ends[i]) < dist * 20 + RevitUtil.M(0.5)) { ends[i] = x; starts[j] = x; }
                }
            }
            for (int i = 0; i < n; i++)
            {
                Curve c = offs[i];
                if (c is Line && starts[i].DistanceTo(ends[i]) > RevitUtil.M(0.002)) c = Line.CreateBound(starts[i], ends[i]);
                result.Add(Tuple.Create(c, src[i].Item2));
            }
            return result;
        }
    }
}
