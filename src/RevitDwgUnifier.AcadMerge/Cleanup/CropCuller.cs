using Autodesk.AutoCAD.DatabaseServices;
using RevitDwgUnifier.AcadMerge.Geometry;

namespace RevitDwgUnifier.AcadMerge.Cleanup
{
    /// <summary>
    /// PITFALL #5 - View crop bounds &amp; stray geometry (DWG half).
    ///
    /// Even with crop regions enforced in Revit, some things escape: datum (level/grid) extents,
    /// elevation markers, imported CAD with its own extents, elements on "Show in all views"...
    /// In a single unified drawing those strays are worse than in per-sheet files: they inflate
    /// every tile, break ZOOM EXTENTS and can even appear inside another item's viewport.
    ///
    /// Rule (applied to each source DWG BEFORE it is inserted): a model-space entity whose
    /// bounding box does not touch ANY viewport window of that item (plus a small margin) is not
    /// visible on any layout, so it is deleted. Partially visible entities are kept - the
    /// viewport boundary clips them visually, exactly as in Revit.
    /// Skipped when the item has no viewports or any viewport is not a plan-like top view.
    /// </summary>
    internal static class CropCuller
    {
        public static int Cull(Database src, SourceAnalysis analysis, string itemKey, MergeLog log)
        {
            if (analysis.Windows.Count == 0)
            {
                log.Info($"{itemKey}: no model viewports, culling skipped.");
                return 0;
            }
            if (!analysis.AllTopViews)
            {
                log.Info($"{itemKey}: non-plan viewport direction found, culling skipped for safety.");
                return 0;
            }

            int erased = 0, kept = 0;
            using (var tr = src.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(src.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead) is Entity e)) continue;
                    if (!ViewportMath.TryGetExtents(e, out var ext)) { kept++; continue; }

                    bool visible = false;
                    foreach (var w in analysis.Windows)
                        if (ViewportMath.IntersectsXY(ext, w)) { visible = true; break; }

                    if (visible) { kept++; continue; }
                    e.UpgradeOpen();
                    e.Erase();
                    erased++;
                }
                tr.Commit();
            }
            if (erased > 0) log.Stat($"{itemKey}: removed {erased} stray model entities outside all viewports ({kept} kept).");
            return erased;
        }
    }
}
