using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using RevitDwgUnifier.AcadMerge.Geometry;
using RevitDwgUnifier.Contracts;

namespace RevitDwgUnifier.AcadMerge
{
    internal struct Rect2
    {
        public double MinX, MinY, MaxX, MaxY;
        public Rect2(double minX, double minY, double maxX, double maxY) { MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY; }
        public double Width => MaxX - MinX;
        public double Height => MaxY - MinY;
        public double CenterX => (MinX + MaxX) / 2;
        public double CenterY => (MinY + MaxY) / 2;
        public Rect2 Grow(double d) => new Rect2(MinX - d, MinY - d, MaxX + d, MaxY + d);
        public bool Contains(Extents3d e) =>
            e.MinPoint.X >= MinX && e.MaxPoint.X <= MaxX && e.MinPoint.Y >= MinY && e.MaxPoint.Y <= MaxY;
        public static Rect2 Union(IEnumerable<Rect2> rects)
        {
            var list = rects.ToList();
            return new Rect2(list.Min(r => r.MinX), list.Min(r => r.MinY), list.Max(r => r.MaxX), list.Max(r => r.MaxY));
        }
    }

    /// <summary>A model viewport found in a source layout, plus what belongs to it.</summary>
    internal sealed class PaperViewport
    {
        public ObjectId Id;
        public ObjectId ClipId;
        public Rect2 Rect;
        public ViewportSpec Spec;
        /// <summary>Model entities visible through this viewport in the source DWG (-1 = unknown).</summary>
        public int SourceHits = -1;
        /// <summary>Paper entities that sit entirely around this viewport (view title, scale
        /// label, callout tags...). They travel with the viewport.</summary>
        public HashSet<ObjectId> Satellites = new HashSet<ObjectId>();
    }

    /// <summary>
    /// "One tab per window/door": splits a sheet layout with N viewports into N layouts.
    ///
    /// Each new layout keeps everything that belongs to the sheet (title block, notes, legends)
    /// and only ONE viewport with its own label. The kept viewport is moved, together with its
    /// label, so that it is centred in the sheet's drawing area: the union of the boxes of all
    /// viewports of the original sheet, i.e. the region the designer reserved for drawings. The
    /// viewport keeps its Revit scale, so dimensions and text sizes stay as drawn.
    ///
    /// DWG viewports are matched to the Revit viewports (which carry the view name and the
    /// window/door mark) by their relative position on the sheet: both sets are normalised to
    /// their own union box, which makes the match independent of units and title block origin.
    /// </summary>
    internal static class ViewportSplitter
    {
        /// <summary>Label search margin around a viewport, as a fraction of its larger side.</summary>
        private const double SatelliteMargin = 0.12;

        public static List<PaperViewport> Collect(Transaction str, SourceLayout sl, out Dictionary<ObjectId, Extents3d> extents)
        {
            var vps = new List<PaperViewport>();
            extents = new Dictionary<ObjectId, Extents3d>();
            foreach (ObjectId id in sl.EntityIds)
            {
                if (!(str.GetObject(id, OpenMode.ForRead) is Entity e)) continue;
                if (e is Viewport vp)
                {
                    double hw = vp.Width / 2, hh = vp.Height / 2;
                    vps.Add(new PaperViewport
                    {
                        Id = id,
                        ClipId = vp.NonRectClipOn ? vp.NonRectClipEntityId : ObjectId.Null,
                        Rect = new Rect2(vp.CenterPoint.X - hw, vp.CenterPoint.Y - hh, vp.CenterPoint.X + hw, vp.CenterPoint.Y + hh)
                    });
                }
                else if (ViewportMath.TryGetExtents(e, out var ext))
                {
                    extents[id] = ext;
                }
            }

            // Satellites: entities fully inside the grown box of exactly one viewport family.
            foreach (var pv in vps)
            {
                Rect2 zone = pv.Rect.Grow(Math.Max(pv.Rect.Width, pv.Rect.Height) * SatelliteMargin);
                double vpArea = Math.Max(pv.Rect.Width * pv.Rect.Height, 1e-9);
                foreach (var kv in extents)
                {
                    double w = kv.Value.MaxPoint.X - kv.Value.MinPoint.X, h = kv.Value.MaxPoint.Y - kv.Value.MinPoint.Y;
                    // Never adopt sheet-sized things (title block, border) even if a big viewport
                    // almost covers the sheet.
                    if (w * h > vpArea * 0.5) continue;
                    if (zone.Contains(kv.Value)) pv.Satellites.Add(kv.Key);
                }
                if (!pv.ClipId.IsNull) pv.Satellites.Add(pv.ClipId);
            }
            return vps;
        }

        /// <summary>Greedy nearest match on normalised centres.</summary>
        public static void Match(List<PaperViewport> dwg, List<ViewportSpec> specs)
        {
            if (dwg.Count == 0 || specs == null || specs.Count == 0) return;
            if (dwg.Count == 1 && specs.Count == 1) { dwg[0].Spec = specs[0]; return; }

            Rect2 ud = Rect2.Union(dwg.Select(d => d.Rect));
            Rect2 ur = Rect2.Union(specs.Select(s => new Rect2(s.MinX, s.MinY, s.MaxX, s.MaxY)));

            var pairs = new List<Tuple<double, PaperViewport, ViewportSpec>>();
            foreach (var d in dwg)
            {
                double dx = Norm(d.Rect.CenterX, ud.MinX, ud.Width), dy = Norm(d.Rect.CenterY, ud.MinY, ud.Height);
                double dw = d.Rect.Width / Math.Max(ud.Width, 1e-9), dh = d.Rect.Height / Math.Max(ud.Height, 1e-9);
                foreach (var s in specs)
                {
                    double sx = Norm((s.MinX + s.MaxX) / 2, ur.MinX, ur.Width), sy = Norm((s.MinY + s.MaxY) / 2, ur.MinY, ur.Height);
                    double sw = (s.MaxX - s.MinX) / Math.Max(ur.Width, 1e-9), sh = (s.MaxY - s.MinY) / Math.Max(ur.Height, 1e-9);
                    double cost = Math.Sqrt((dx - sx) * (dx - sx) + (dy - sy) * (dy - sy)) +
                                  0.5 * (Math.Abs(dw - sw) + Math.Abs(dh - sh)); // size breaks ties
                    pairs.Add(Tuple.Create(cost, d, s));
                }
            }

            var usedD = new HashSet<PaperViewport>();
            var usedS = new HashSet<ViewportSpec>();
            foreach (var p in pairs.OrderBy(p => p.Item1))
            {
                if (usedD.Contains(p.Item2) || usedS.Contains(p.Item3)) continue;
                p.Item2.Spec = p.Item3;
                usedD.Add(p.Item2);
                usedS.Add(p.Item3);
            }
        }

        private static double Norm(double v, double min, double size) => size > 1e-9 ? (v - min) / size : 0.5;
    }
}
