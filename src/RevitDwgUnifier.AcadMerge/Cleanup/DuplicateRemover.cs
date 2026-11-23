using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace RevitDwgUnifier.AcadMerge.Cleanup
{
    /// <summary>
    /// PITFALL #6 - Sheet / model space duplication (DWG half).
    ///
    /// The planner already avoids exporting the same view twice. What remains are coincident
    /// copies created by overlapping views of the same model area (a plan and its callout or a
    /// dependent view on the same sheet, or several sheets in TrueCoordinates mode): identical
    /// lines/arcs/text on the same layer, stacked on top of each other. They double plot
    /// density, break OVERKILL-sensitive workflows and make selection ambiguous.
    ///
    /// This is a conservative OVERKILL: only EXACT duplicates are removed - same entity type,
    /// layer, colour, linetype, lineweight and geometry (rounded to 1e-6 drawing units; lines
    /// and polylines are direction-independent). Overlapping-but-different geometry (e.g. the
    /// same wall at coarse vs. fine detail level) is intentionally left alone.
    /// </summary>
    internal static class DuplicateRemover
    {
        public static int Remove(Database db, MergeLog log)
        {
            int erased = 0;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                var seen = new HashSet<string>(StringComparer.Ordinal);

                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead) is Entity e)) continue;
                    string sig = Signature(e);
                    if (sig == null) continue;
                    if (seen.Add(sig)) continue;

                    e.UpgradeOpen();
                    e.Erase();
                    erased++;
                }
                tr.Commit();
            }
            log.Stat($"Duplicate coincident entities removed: {erased}");
            return erased;
        }

        private static string Signature(Entity e)
        {
            string common = e.GetType().Name + "|" + e.Layer + "|" + e.Color + "|" + e.Linetype + "|" + (int)e.LineWeight + "|";
            switch (e)
            {
                case Line l:
                    return common + Seg(l.StartPoint, l.EndPoint);
                case Arc a:
                    return common + P(a.Center) + R(a.Radius) + R(a.StartAngle) + R(a.EndAngle) + V(a.Normal);
                case Circle c:
                    return common + P(c.Center) + R(c.Radius) + V(c.Normal);
                case Polyline pl:
                    return common + PolylineKey(pl);
                case DBText t when !(e is AttributeDefinition):
                    return common + P(t.Position) + R(t.Height) + R(t.Rotation) + t.TextStyleId.Handle + "|" + t.TextString;
                case MText m:
                    return common + P(m.Location) + R(m.TextHeight) + R(m.Rotation) + R(m.Width) + "|" + m.Contents;
                case BlockReference br when !(e is Table):
                    if (br.AttributeCollection.Count > 0) return null; // attribute values may differ
                    return common + br.BlockTableRecord.Handle + "|" + P(br.Position) + R(br.Rotation) +
                           R(br.ScaleFactors.X) + R(br.ScaleFactors.Y) + R(br.ScaleFactors.Z);
                default:
                    return null; // hatches, dimensions, splines, solids: never touched
            }
        }

        private static string PolylineKey(Polyline pl)
        {
            var fwd = new StringBuilder();
            var rev = new StringBuilder();
            int n = pl.NumberOfVertices;
            for (int i = 0; i < n; i++)
            {
                fwd.Append(P2(pl.GetPoint2dAt(i))).Append(R(pl.GetBulgeAt(i)));
                // Reversed traversal: bulges change sign and shift by one vertex.
                int j = n - 1 - i;
                double bulge = j - 1 >= 0 ? -pl.GetBulgeAt(j - 1) : (pl.Closed ? -pl.GetBulgeAt(n - 1) : 0.0);
                rev.Append(P2(pl.GetPoint2dAt(j))).Append(R(bulge));
            }
            string a = fwd.ToString(), b = rev.ToString();
            return (pl.Closed ? "C" : "O") + R(pl.ConstantWidth) + (string.CompareOrdinal(a, b) <= 0 ? a : b);
        }

        private static string Seg(Point3d a, Point3d b)
        {
            string sa = P(a), sb = P(b);
            return string.CompareOrdinal(sa, sb) <= 0 ? sa + sb : sb + sa;
        }

        private static string P(Point3d p) => R(p.X) + R(p.Y) + R(p.Z);
        private static string P2(Point2d p) => R(p.X) + R(p.Y);
        private static string V(Vector3d v) => R(v.X) + R(v.Y) + R(v.Z);

        private static string R(double v)
        {
            double r = Math.Round(v, 6);
            if (r == 0) r = 0; // normalise -0
            return r.ToString("R", CultureInfo.InvariantCulture) + ";";
        }
    }
}
