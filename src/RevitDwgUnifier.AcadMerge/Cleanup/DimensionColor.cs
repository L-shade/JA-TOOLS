using System;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;

namespace RevitDwgUnifier.AcadMerge.Cleanup
{
    /// <summary>
    /// Gives every dimension text the colour chosen in the exporter (an AutoCAD Color Index,
    /// "ID" 1-255, default 140): DIMCLRT on every dimension style and on every dimension entity
    /// (model space, layouts and block definitions), then regenerates the dimension block so the
    /// new colour shows immediately. 0 leaves the colours exported by Revit untouched.
    /// </summary>
    internal static class DimensionColor
    {
        public static void Apply(Database db, int aci, MergeLog log)
        {
            if (aci <= 0) return;
            if (aci > 255)
            {
                log.Warn($"Dimension text colour {aci} is not a valid colour ID (1-255); left unchanged.");
                return;
            }
            Color color = Color.FromColorIndex(ColorMethod.ByAci, (short)aci);
            int dims = 0, styles = 0, failed = 0;

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var dst = (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead);
                foreach (ObjectId id in dst)
                {
                    try
                    {
                        var ds = (DimStyleTableRecord)tr.GetObject(id, OpenMode.ForWrite);
                        ds.Dimclrt = color;
                        styles++;
                    }
                    catch (Exception) { failed++; }
                }

                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId btrId in bt)
                {
                    var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
                    if (btr.IsFromExternalReference || btr.IsDependent) continue;
                    foreach (ObjectId id in btr)
                    {
                        if (!(tr.GetObject(id, OpenMode.ForRead) is Dimension dim)) continue;
                        try
                        {
                            dim.UpgradeOpen();
                            dim.Dimclrt = color;   // per-dimension override wins over the style
                            try { dim.RecomputeDimensionBlock(true); } catch (Exception) { }
                            dims++;
                        }
                        catch (Exception) { failed++; }
                    }
                }
                tr.Commit();
            }

            log.Stat($"Dimension text colour set to ID {aci}: {dims} dimension(s), {styles} dimension style(s)" +
                     (failed > 0 ? $", {failed} failed" : "") +
                     (dims == 0 ? " (no dimension entities found: Revit may have exported them as lines and text)" : "") + ".");
        }
    }
}
