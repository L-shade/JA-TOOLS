using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using RevitDwgUnifier.AcadMerge.Geometry;

namespace RevitDwgUnifier.AcadMerge
{
    /// <summary>One paper-space layout found in a Revit-exported DWG.</summary>
    internal sealed class SourceLayout
    {
        public ObjectId LayoutId;
        public string Name;
        public int TabOrder;
        public ObjectId BlockTableRecordId;
        /// <summary>Entities to copy (everything in the layout except its overall paper viewport).</summary>
        public List<ObjectId> EntityIds = new List<ObjectId>();
        public int ViewportCount;
    }

    internal sealed class SourceAnalysis
    {
        public List<SourceLayout> Layouts = new List<SourceLayout>();
        /// <summary>Model-space windows visible through the layout viewports.</summary>
        public List<Extents3d> Windows = new List<Extents3d>();
        /// <summary>False if any viewport is not a top (plan-like) view: culling is then unsafe.</summary>
        public bool AllTopViews = true;
        public Extents3d? ModelExtents;

        public Extents3d? WindowUnion
        {
            get
            {
                if (Windows.Count == 0) return null;
                var u = Windows[0];
                foreach (var w in Windows.Skip(1)) u.AddExtents(w);
                // Limit the infinite Z used for culling to a sane value for tiling.
                return new Extents3d(new Point3d(u.MinPoint.X, u.MinPoint.Y, 0), new Point3d(u.MaxPoint.X, u.MaxPoint.Y, 0));
            }
        }
    }

    internal static class SourceAnalyzer
    {
        public static SourceAnalysis Analyze(Database src, double cullMargin)
        {
            var a = new SourceAnalysis();
            using (var tr = src.TransactionManager.StartTransaction())
            {
                var layoutDict = (DBDictionary)tr.GetObject(src.LayoutDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry entry in layoutDict)
                {
                    var layout = (Layout)tr.GetObject(entry.Value, OpenMode.ForRead);
                    if (layout.ModelType) continue;

                    var sl = new SourceLayout
                    {
                        LayoutId = layout.ObjectId,
                        Name = layout.LayoutName,
                        TabOrder = layout.TabOrder,
                        BlockTableRecordId = layout.BlockTableRecordId
                    };

                    // The first viewport of a layout is the "overall" paper-space viewport (the
                    // sheet window itself). Copying it would create a bogus second sheet window,
                    // so it is excluded. GetViewports() is empty for never-activated layouts; then
                    // the first Viewport in drawing order is the overall one by AutoCAD convention.
                    ObjectIdCollection vps = layout.GetViewports();
                    ObjectId overall = vps != null && vps.Count > 0 ? vps[0] : ObjectId.Null;

                    var btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                    bool firstViewport = true;
                    foreach (ObjectId id in btr)
                    {
                        var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (ent == null) continue;

                        if (ent is Viewport vp)
                        {
                            if (overall.IsNull && firstViewport && LooksLikeOverall(vp)) overall = id;
                            firstViewport = false;
                            if (id == overall) continue;

                            sl.ViewportCount++;
                            if (!ViewportMath.IsTopView(vp)) a.AllTopViews = false;
                            else
                            {
                                Extents3d? w = ViewportMath.ModelWindow(vp, cullMargin);
                                if (w.HasValue) a.Windows.Add(w.Value);
                            }
                        }
                        sl.EntityIds.Add(id);
                    }

                    if (sl.EntityIds.Count > 0) a.Layouts.Add(sl);
                }

                a.ModelExtents = ModelSpaceExtents(src, tr);
                tr.Commit();
            }
            a.Layouts = a.Layouts.OrderBy(l => l.TabOrder).ToList();
            return a;
        }

        private static bool LooksLikeOverall(Viewport vp)
        {
            // The paper-space viewport views paper space 1:1 around its own centre.
            return System.Math.Abs(vp.CustomScale - 1.0) < 1e-9 &&
                   new Point2d(vp.CenterPoint.X, vp.CenterPoint.Y).GetDistanceTo(vp.ViewCenter) < 1e-6;
        }

        public static Extents3d? ModelSpaceExtents(Database db, Transaction tr)
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            Extents3d? ext = null;
            foreach (ObjectId id in ms)
            {
                if (!(tr.GetObject(id, OpenMode.ForRead) is Entity e)) continue;
                if (!ViewportMath.TryGetExtents(e, out var x)) continue;
                if (ext.HasValue) { var u = ext.Value; u.AddExtents(x); ext = u; }
                else ext = x;
            }
            return ext;
        }
    }
}
