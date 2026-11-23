using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace RevitDwgUnifier.AcadMerge.Cleanup
{
    /// <summary>
    /// The viewport frames never print: the layer that holds the viewports of the layouts is made
    /// non-plottable. A layer used only by viewports (as Revit exports them) keeps its name; viewports
    /// sharing a layer with drawing entities ("0", title block...) move to <c>fallbackLayer</c>,
    /// created non-plottable. The layouts' own paper viewports are not touched.
    /// </summary>
    internal static class ViewportLayer
    {
        public static void Apply(Database db, string fallbackLayer, MergeLog log)
        {
            if (string.IsNullOrWhiteSpace(fallbackLayer)) fallbackLayer = "JA - VIEWPORT";
            var viewports = new List<ObjectId>();
            var layersWithVp = new HashSet<ObjectId>();
            var layersWithOthers = new HashSet<ObjectId>();
            int moved = 0, layers = 0;

            using (var tr = db.TransactionManager.StartTransaction())
            {
                // viewports of the layouts (not the overall paper viewport)
                var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                var paper = new HashSet<ObjectId>();
                foreach (DBDictionaryEntry entry in dict)
                {
                    var layout = (Layout)tr.GetObject(entry.Value, OpenMode.ForRead);
                    if (layout.ModelType) continue;
                    var btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                    ObjectIdCollection lvps = null;
                    try { lvps = layout.GetViewports(); } catch (Exception) { }
                    ObjectId overall = lvps != null && lvps.Count > 0 ? lvps[0] : ObjectId.Null;
                    paper.Add(btr.ObjectId);
                    foreach (ObjectId id in btr)
                    {
                        if (id.IsErased || !(tr.GetObject(id, OpenMode.ForRead) is Viewport vp)) continue;
                        if (id == overall || (overall.IsNull && LooksLikePaper(vp))) continue;
                        viewports.Add(id);
                        layersWithVp.Add(vp.LayerId);
                    }
                }
                if (viewports.Count == 0) { tr.Commit(); return; }

                // which of those layers also hold drawing entities (anywhere in the drawing)
                var vpSet = new HashSet<ObjectId>(viewports);
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId btrId in bt)
                {
                    var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
                    if (btr.IsFromExternalReference || btr.IsDependent) continue;
                    foreach (ObjectId id in btr)
                    {
                        if (vpSet.Contains(id)) continue;
                        if (!(tr.GetObject(id, OpenMode.ForRead) is Entity e)) continue;
                        if (e is Viewport && paper.Contains(btrId)) continue; // overall paper viewports
                        if (layersWithVp.Contains(e.LayerId)) layersWithOthers.Add(e.LayerId);
                    }
                }

                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                ObjectId zero = lt["0"];
                ObjectId fallback = ObjectId.Null;
                ObjectId Fallback()
                {
                    if (!fallback.IsNull) return fallback;
                    if (lt.Has(fallbackLayer)) fallback = lt[fallbackLayer];
                    else
                    {
                        lt.UpgradeOpen();
                        var l = new LayerTableRecord { Name = fallbackLayer };
                        l.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, 8);
                        fallback = lt.Add(l);
                        tr.AddNewlyCreatedDBObject(l, true);
                    }
                    MakeNoPlot(tr, fallback);
                    layers++;
                    return fallback;
                }

                foreach (ObjectId lid in layersWithVp)
                {
                    if (lid == zero || layersWithOthers.Contains(lid)) continue;
                    if (MakeNoPlot(tr, lid)) layers++;
                }
                foreach (ObjectId id in viewports)
                {
                    var vp = (Viewport)tr.GetObject(id, OpenMode.ForRead);
                    if (vp.LayerId != zero && !layersWithOthers.Contains(vp.LayerId)) continue;
                    try
                    {
                        vp = (Viewport)tr.GetObject(id, OpenMode.ForWrite, false, true);
                        vp.LayerId = Fallback();
                        moved++;
                    }
                    catch (Exception) { }
                }
                tr.Commit();
            }
            log.Stat($"Viewports: {viewports.Count} on non-plottable layer(s)" + (moved > 0 ? $", {moved} moved to '{fallbackLayer}'" : "") + ".");
        }

        private static bool MakeNoPlot(Transaction tr, ObjectId layerId)
        {
            var l = (LayerTableRecord)tr.GetObject(layerId, OpenMode.ForRead);
            if (!l.IsPlottable) return false;
            l.UpgradeOpen();
            l.IsPlottable = false;
            return true;
        }

        private static bool LooksLikePaper(Viewport vp) =>
            vp.Number == 1 ||
            (Math.Abs(vp.CustomScale - 1.0) < 1e-9 && new Point2d(vp.CenterPoint.X, vp.CenterPoint.Y).GetDistanceTo(vp.ViewCenter) < 1e-6);
    }
}
