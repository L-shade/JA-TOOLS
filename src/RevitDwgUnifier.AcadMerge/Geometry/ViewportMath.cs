using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace RevitDwgUnifier.AcadMerge.Geometry
{
    /// <summary>
    /// Paper-space &lt;-&gt; model-space transforms of a viewport.
    ///
    ///   PSDCS (paper)  --DCS2PSDCS^-1-->  DCS (view)  --DCS2WCS-->  WCS (model)
    ///
    /// DCS is the viewport's display coordinate system: origin at ViewTarget, Z along
    /// ViewDirection, rotated by TwistAngle. ViewCenter is expressed in DCS.
    ///
    /// Consequence used by the merge: if model content is translated by vector T, adding T to
    /// ViewTarget (and leaving ViewCenter / scale / twist untouched) shows exactly the same
    /// picture - for any view direction and any twist. That is how viewports are re-targeted
    /// after tiling without any Revit-side geometry math.
    /// </summary>
    public static class ViewportMath
    {
        public static Matrix3d DcsToWcs(Viewport vp) =>
            Matrix3d.Rotation(-vp.TwistAngle, vp.ViewDirection, vp.ViewTarget) *
            Matrix3d.Displacement(vp.ViewTarget.GetAsVector()) *
            Matrix3d.PlaneToWorld(vp.ViewDirection);

        public static Matrix3d DcsToPsdcs(Viewport vp) =>
            Matrix3d.Scaling(vp.CustomScale, vp.CenterPoint) *
            Matrix3d.Displacement(new Point3d(vp.ViewCenter.X, vp.ViewCenter.Y, 0.0).GetVectorTo(vp.CenterPoint));

        public static Matrix3d PsdcsToWcs(Viewport vp) => DcsToWcs(vp) * DcsToPsdcs(vp).Inverse();

        /// <summary>True for plan-like viewports (view direction parallel to WCS Z), which is
        /// what Revit writes for every 2D view on a sheet. Culling is only done for those.</summary>
        public static bool IsTopView(Viewport vp) =>
            vp.ViewDirection.GetNormal().IsParallelTo(Vector3d.ZAxis, new Tolerance(1e-6, 1e-6));

        /// <summary>Axis-aligned model-space window seen through the viewport (XY only), grown by
        /// <paramref name="margin"/> (fraction of the window size).</summary>
        public static Extents3d? ModelWindow(Viewport vp, double margin)
        {
            if (vp.CustomScale <= 1e-12 || vp.Width <= 0 || vp.Height <= 0) return null;
            Matrix3d m = PsdcsToWcs(vp);
            double hw = vp.Width / 2.0, hh = vp.Height / 2.0;
            Point3d c = vp.CenterPoint;
            var corners = new List<Point3d>
            {
                new Point3d(c.X - hw, c.Y - hh, 0), new Point3d(c.X + hw, c.Y - hh, 0),
                new Point3d(c.X + hw, c.Y + hh, 0), new Point3d(c.X - hw, c.Y + hh, 0)
            };
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (Point3d p in corners)
            {
                Point3d w = p.TransformBy(m);
                minX = Math.Min(minX, w.X); minY = Math.Min(minY, w.Y);
                maxX = Math.Max(maxX, w.X); maxY = Math.Max(maxY, w.Y);
            }
            double gx = (maxX - minX) * margin, gy = (maxY - minY) * margin;
            return new Extents3d(new Point3d(minX - gx, minY - gy, -1e9), new Point3d(maxX + gx, maxY + gy, 1e9));
        }

        public static bool IntersectsXY(Extents3d a, Extents3d b) =>
            a.MinPoint.X <= b.MaxPoint.X && a.MaxPoint.X >= b.MinPoint.X &&
            a.MinPoint.Y <= b.MaxPoint.Y && a.MaxPoint.Y >= b.MinPoint.Y;

        public static bool TryGetExtents(Entity e, out Extents3d ext)
        {
            try
            {
                ext = e.GeometricExtents;
                return true;
            }
            catch
            {
                // Empty text, zero-length lines, unevaluated hatches, proxy entities...
                ext = default(Extents3d);
                return false;
            }
        }
    }
}
