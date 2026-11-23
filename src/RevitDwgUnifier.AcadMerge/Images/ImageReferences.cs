using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;

namespace RevitDwgUnifier.AcadMerge.Images
{
    /// <summary>
    /// Raster image references exported by Revit (title block logos, pictures placed on sheets).
    ///
    /// Revit writes each image as a separate file next to the sheet DWG and references it with a
    /// path relative to that DWG. Once the sheet is merged into the master drawing (which lives in
    /// another folder, and whose work folder is deleted afterwards) those references break: the
    /// logo frames come out empty. Before a source is merged, every image definition is therefore
    /// re-pointed to an absolute path, and the file is copied to a persistent folder next to the
    /// final DWG. The OLE pass (<see cref="OleEmbedder"/>) then embeds them into the drawing.
    /// </summary>
    internal static class ImageReferences
    {
        public static int Relocate(Database src, string sourceDwgPath, string imageFolder, string prefix, MergeLog log)
        {
            int moved = 0;
            string srcDir = Path.GetDirectoryName(sourceDwgPath) ?? "";
            try
            {
                using (var tr = src.TransactionManager.StartTransaction())
                {
                    ObjectId dictId = RasterImageDef.GetImageDictionary(src);
                    if (dictId.IsNull) { tr.Commit(); return 0; }
                    var dict = (DBDictionary)tr.GetObject(dictId, OpenMode.ForRead);
                    foreach (DBDictionaryEntry e in dict)
                    {
                        if (!(tr.GetObject(e.Value, OpenMode.ForRead) is RasterImageDef def)) continue;
                        string file = Resolve(def, srcDir);
                        if (file == null)
                        {
                            log.Warn($"Image '{def.SourceFileName}' referenced by {Path.GetFileName(sourceDwgPath)} was not found.");
                            continue;
                        }

                        string target = file;
                        if (!string.IsNullOrEmpty(imageFolder))
                        {
                            Directory.CreateDirectory(imageFolder);
                            target = Path.Combine(imageFolder, prefix + "_" + Path.GetFileName(file));
                            if (!string.Equals(Path.GetFullPath(file), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                                File.Copy(file, target, true);
                        }
                        def.UpgradeOpen();
                        def.SourceFileName = target;
                        moved++;
                    }
                    tr.Commit();
                }
            }
            catch (Exception ex)
            {
                log.Warn($"Image references of {Path.GetFileName(sourceDwgPath)} could not be relocated: {ex.Message}");
            }
            if (moved > 0) log.Info($"{Path.GetFileName(sourceDwgPath)}: {moved} image file(s) re-pathed" +
                                    (string.IsNullOrEmpty(imageFolder) ? "." : " to " + imageFolder + "."));
            return moved;
        }

        /// <summary>Finds the image file of a definition: as found by AutoCAD, as stored (absolute or
        /// relative to the source DWG), or by file name next to the source DWG.</summary>
        public static string Resolve(RasterImageDef def, string baseDir)
        {
            var candidates = new List<string>();
            try { if (!string.IsNullOrEmpty(def.ActiveFileName)) candidates.Add(def.ActiveFileName); } catch { }
            string stored = def.SourceFileName ?? "";
            if (stored.Length > 0)
            {
                candidates.Add(stored);
                if (!string.IsNullOrEmpty(baseDir))
                {
                    try { candidates.Add(Path.GetFullPath(Path.Combine(baseDir, stored.TrimStart('.', '\\', '/')))); } catch { }
                    try { candidates.Add(Path.GetFullPath(Path.Combine(baseDir, stored))); } catch { }
                    try { candidates.Add(Path.Combine(baseDir, Path.GetFileName(stored))); } catch { }
                }
            }
            foreach (string c in candidates)
            {
                try { if (Path.IsPathRooted(c) && File.Exists(c)) return Path.GetFullPath(c); } catch { }
            }
            return null;
        }

        /// <summary>
        /// Loads every image definition whose file exists and makes every image entity show its
        /// picture. The "loaded" state is SAVED in the DWG (IMAGEDEF flag): definitions that came
        /// from Revit's sheet DWG were never loaded (their relative path was not found from the
        /// merge folder), so without this the drawing opens with the logos unloaded = invisible.
        /// </summary>
        public static void LoadAll(Database db, MergeLog log)
        {
            int loaded = 0, missing = 0, failed = 0, shown = 0;
            try
            {
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    ObjectId dictId = RasterImageDef.GetImageDictionary(db);
                    if (!dictId.IsNull)
                    {
                        var dict = (DBDictionary)tr.GetObject(dictId, OpenMode.ForRead);
                        foreach (DBDictionaryEntry e in dict)
                        {
                            if (!(tr.GetObject(e.Value, OpenMode.ForWrite) is RasterImageDef def)) continue;
                            string file = Resolve(def, null);
                            if (file == null) { missing++; log.Warn($"Image file not found: {def.SourceFileName}"); continue; }
                            if (!string.Equals(def.SourceFileName, file, StringComparison.OrdinalIgnoreCase)) def.SourceFileName = file;
                            try
                            {
                                if (!def.IsLoaded) def.Load();
                                if (def.IsLoaded) loaded++; else failed++;
                            }
                            catch (Exception ex) { failed++; log.Info($"Image '{Path.GetFileName(file)}' not loaded: {ex.Message}"); }
                        }
                    }

                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    foreach (ObjectId btrId in bt)
                    {
                        var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
                        if (btr.IsFromExternalReference) continue;
                        var images = new List<ObjectId>();
                        foreach (ObjectId id in btr)
                        {
                            if (!(tr.GetObject(id, OpenMode.ForRead) is RasterImage img)) continue;
                            images.Add(id);
                            if (!img.ShowImage || !img.Visible)
                            {
                                img.UpgradeOpen();
                                img.ShowImage = true;
                                img.Visible = true;
                                shown++;
                            }
                        }
                        if (images.Count > 0) SendToBack(tr, btr, images);
                    }
                    tr.Commit();
                }
            }
            catch (Exception ex)
            {
                log.Warn("Image loading failed: " + ex.Message);
            }
            log.Stat($"Image definitions loaded: {loaded}" + (failed > 0 ? $", {failed} could not be loaded" : "") +
                     (missing > 0 ? $", {missing} file(s) missing" : "") + (shown > 0 ? $"; {shown} hidden image(s) switched on" : ""));
        }

        /// <summary>DRAWORDER > Send to Back for the given entities of one space / block definition,
        /// so pictures never hide linework or text drawn over them.</summary>
        public static void SendToBack(Transaction tr, BlockTableRecord owner, IList<ObjectId> ids)
        {
            try
            {
                // The draw order table may not exist yet; creating it needs the owner open for write.
                if (!owner.IsWriteEnabled) owner.UpgradeOpen();
                var dot = (DrawOrderTable)tr.GetObject(owner.DrawOrderTableId, OpenMode.ForWrite);
                dot.MoveToBottom(new ObjectIdCollection(ids.ToArray()));
            }
            catch (Exception) { /* draw order is cosmetic: never fail the export for it */ }
        }

        /// <summary>Raster image entities anywhere in the drawing (spaces and block definitions).</summary>
        public static int Count(Database db)
        {
            int n = 0;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId btrId in bt)
                {
                    var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
                    if (btr.IsFromExternalReference) continue;
                    foreach (ObjectId id in btr)
                        if (id.ObjectClass.IsDerivedFrom(Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(RasterImage)))) n++;
                }
                tr.Commit();
            }
            return n;
        }
    }
}
