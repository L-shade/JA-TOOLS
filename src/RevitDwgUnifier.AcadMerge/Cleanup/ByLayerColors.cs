using System;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;

namespace RevitDwgUnifier.AcadMerge.Cleanup
{
    /// <summary>
    /// Object colour ByLayer on every entity of the drawing: model space, layouts and block
    /// definitions (Revit families included), attributes of block references, dimension line and
    /// extension colours (styles and entities) and colour codes written inside MText. What Revit
    /// exported as overrides (view filters, "by element" colours, ByBlock) then shows in the
    /// colour of the entity's own layer. The dimension text keeps the colour chosen in the
    /// exporter (applied afterwards); with that option at 0 it becomes ByLayer too.
    /// </summary>
    internal static class ByLayerColors
    {
        private static readonly Regex CorMText = new Regex(@"(?<!\\)\\[Cc]\d+;", RegexOptions.Compiled);

        public static void Apply(Database db, bool dimTextToo, MergeLog log)
        {
            Color byLayer = Color.FromColorIndex(ColorMethod.ByLayer, 256);
            int changed = 0, attrs = 0, dims = 0, mtext = 0, failed = 0;

            using (var tr = db.TransactionManager.StartTransaction())
            {
                // dimension styles: dimension and extension lines ByLayer
                var dst = (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead);
                foreach (ObjectId id in dst)
                {
                    try
                    {
                        var ds = (DimStyleTableRecord)tr.GetObject(id, OpenMode.ForWrite);
                        ds.Dimclrd = byLayer;
                        ds.Dimclre = byLayer;
                        if (dimTextToo) ds.Dimclrt = byLayer;
                    }
                    catch (Exception) { failed++; }
                }

                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId btrId in bt)
                {
                    var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
                    if (btr.IsFromExternalReference || btr.IsDependent) continue;
                    // the anonymous blocks of dimensions are rebuilt from the dimension itself
                    if (btr.IsAnonymous && btr.Name.StartsWith("*D", StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (ObjectId id in btr)
                    {
                        Entity e;
                        try { e = tr.GetObject(id, OpenMode.ForRead) as Entity; }
                        catch (Exception) { failed++; continue; }
                        if (e == null) continue;
                        try
                        {
                            bool mudar = !e.Color.IsByLayer;
                            Dimension dim = e as Dimension;
                            MText mt = e as MText;
                            bool corNoTexto = mt != null && CorMText.IsMatch(mt.Contents ?? "");
                            bool dimMuda = dim != null && (!dim.Dimclrd.IsByLayer || !dim.Dimclre.IsByLayer || (dimTextToo && !dim.Dimclrt.IsByLayer));
                            if (mudar || corNoTexto || dimMuda)
                            {
                                // force open: entities on locked layers too
                                e = (Entity)tr.GetObject(id, OpenMode.ForWrite, false, true);
                                if (mudar) { e.ColorIndex = 256; changed++; }
                                if (corNoTexto) { ((MText)e).Contents = CorMText.Replace(((MText)e).Contents, ""); mtext++; }
                                if (dimMuda)
                                {
                                    var d = (Dimension)e;
                                    d.Dimclrd = byLayer;
                                    d.Dimclre = byLayer;
                                    if (dimTextToo) d.Dimclrt = byLayer;
                                    try { d.RecomputeDimensionBlock(true); } catch (Exception) { }
                                    dims++;
                                }
                            }
                            if (e is BlockReference br && br.AttributeCollection.Count > 0)
                            {
                                foreach (ObjectId aid in br.AttributeCollection)
                                {
                                    try
                                    {
                                        var ar = tr.GetObject(aid, OpenMode.ForRead) as AttributeReference;
                                        if (ar == null || ar.Color.IsByLayer) continue;
                                        ar = (AttributeReference)tr.GetObject(aid, OpenMode.ForWrite, false, true);
                                        ar.ColorIndex = 256;
                                        attrs++;
                                    }
                                    catch (Exception) { failed++; }
                                }
                            }
                        }
                        catch (Exception) { failed++; }
                    }
                }
                tr.Commit();
            }

            log.Stat($"Object colour ByLayer: {changed} entit(ies)" +
                     (attrs > 0 ? $", {attrs} attribute(s)" : "") + (dims > 0 ? $", {dims} dimension(s)" : "") +
                     (mtext > 0 ? $", colour codes removed from {mtext} MText" : "") +
                     (failed > 0 ? $", {failed} failed" : "") + ".");
        }
    }
}
