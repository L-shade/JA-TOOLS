using System;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using RevitDwgUnifier.Contracts;

[assembly: CommandClass(typeof(RevitDwgUnifier.AcadMerge.MergeCommands))]

namespace RevitDwgUnifier.AcadMerge
{
    /// <summary>
    /// Entry point executed inside AutoCAD Core Console by the script that the Revit add-in
    /// generates:  NETLOAD &lt;this dll&gt;  ->  RVTUNIFY  ->  &lt;manifest path&gt;
    /// The command never throws to AutoCAD: every outcome ends up in result.json, which is what
    /// the Revit side reads back (exit codes of accoreconsole are not reliable).
    /// </summary>
    public sealed class MergeCommands
    {
        /// <summary>
        /// Second pass, run in FULL AutoCAD (acad.exe) on the merged drawing:
        /// NETLOAD &lt;this dll&gt; -> RVTOLE -> &lt;ole manifest path&gt;. Converts raster image
        /// references into embedded OLE objects (needs the clipboard, so not Core Console).
        /// </summary>
        [CommandMethod("RVTOLE", CommandFlags.Modal | CommandFlags.NoUndoMarker)]
        public void EmbedImages()
        {
            Editor ed = Application.DocumentManager.MdiActiveDocument?.Editor;
            var result = new MergeResult();
            OleManifest manifest = null;
            string manifestPath = null;
            try
            {
                var opts = new PromptStringOptions("\nRVTOLE manifest path: ") { AllowSpaces = true };
                PromptResult pr = ed.GetString(opts);
                if (pr.Status != PromptStatus.OK) return;
                manifestPath = pr.StringResult.Trim().Trim('"');
                manifest = ContractJson.Read<OleManifest>(manifestPath);

                var log = new MergeLog(manifest.LogPath, ed, result);
                log.Info($"OLE pass on {manifest.InputDwgPath}");
                new Images.OleEmbedder(manifest, log, result).Run();
            }
            catch (System.Exception ex)
            {
                result.Success = false;
                result.Errors.Add("Fatal: " + ex);
                try { ed?.WriteMessage("\n[RVTOLE] FATAL " + ex); } catch { }
            }
            finally
            {
                string resultPath = manifest?.ResultPath
                                    ?? (manifestPath != null ? Path.ChangeExtension(manifestPath, ".result.json") : null);
                if (resultPath != null)
                {
                    try { ContractJson.Write(resultPath, result); }
                    catch (System.Exception ex) { ed?.WriteMessage("\n[RVTOLE] cannot write result: " + ex.Message); }
                }
            }
        }

        /// <summary>
        /// "DWG por prancha": NETLOAD &lt;this dll&gt; -> RVTSTANDARDS -> &lt;standards manifest path&gt;.
        /// Opens every DWG exported by Revit, applies the office standard (Padrão JA: pilares
        /// exploded, hatch and PD/area text layers), object colours ByLayer and non-plottable viewport
        /// layer (each when enabled) and saves it in place, in its own DWG version.
        /// </summary>
        [CommandMethod("RVTSTANDARDS", CommandFlags.Modal)]
        public void Standards()
        {
            Editor ed = Application.DocumentManager.MdiActiveDocument?.Editor;
            var result = new MergeResult { Success = true };
            StandardsManifest manifest = null;
            string manifestPath = null;
            try
            {
                var opts = new PromptStringOptions("\nRVTSTANDARDS manifest path: ") { AllowSpaces = true };
                PromptResult pr = ed.GetString(opts);
                if (pr.Status != PromptStatus.OK) return;
                manifestPath = pr.StringResult.Trim().Trim('"');
                manifest = ContractJson.Read<StandardsManifest>(manifestPath);
                if (manifest.Options == null) manifest.Options = new MergeOptions();
                var log = new MergeLog(manifest.LogPath, ed, result);
                log.Info($"Padrão JA em {manifest.Files?.Count ?? 0} DWG(s)");

                foreach (string file in manifest.Files ?? new System.Collections.Generic.List<string>())
                {
                    try
                    {
                        ApplyStandards(file, manifest.Options, log);
                        result.LayoutsCreated.Add(file);
                    }
                    catch (System.Exception ex)
                    {
                        result.FailedItems.Add(file);
                        log.Warn($"{Path.GetFileName(file)}: padrão JA não aplicado ({ex.Message}).");
                    }
                }
            }
            catch (System.Exception ex)
            {
                result.Success = false;
                result.Errors.Add("Fatal: " + ex);
                try { ed?.WriteMessage("\n[RVTSTANDARDS] FATAL " + ex); } catch { }
            }
            finally
            {
                string resultPath = manifest?.ResultPath
                                    ?? (manifestPath != null ? Path.ChangeExtension(manifestPath, ".result.json") : null);
                if (resultPath != null)
                {
                    try { ContractJson.Write(resultPath, result); }
                    catch (System.Exception ex) { ed?.WriteMessage("\n[RVTSTANDARDS] cannot write result: " + ex.Message); }
                }
            }
        }

        private static void ApplyStandards(string file, MergeOptions options, MergeLog log)
        {
            string tmp = Path.Combine(Path.GetDirectoryName(file), Path.GetFileNameWithoutExtension(file) + ".ja_tmp.dwg");
            using (var db = new Autodesk.AutoCAD.DatabaseServices.Database(false, true))
            {
                db.ReadDwgFile(file, Autodesk.AutoCAD.DatabaseServices.FileOpenMode.OpenForReadAndAllShare, true, "");
                db.CloseInput(true);
                var previous = Autodesk.AutoCAD.DatabaseServices.HostApplicationServices.WorkingDatabase;
                Autodesk.AutoCAD.DatabaseServices.HostApplicationServices.WorkingDatabase = db;
                try
                {
                    log.Info("Arquivo: " + Path.GetFileName(file));
                    if (options.JaStandards) Cleanup.JaStandards.Apply(db, options, log);
                    if (options.ViewportsNoPlot) Cleanup.ViewportLayer.Apply(db, options.ViewportLayerName, log);
                    if (options.ColorsByLayer) Cleanup.ByLayerColors.Apply(db, options.DimTextColorIndex <= 0, log);
                }
                finally
                {
                    Autodesk.AutoCAD.DatabaseServices.HostApplicationServices.WorkingDatabase = previous;
                }
                db.SaveAs(tmp, db.OriginalFileVersion);
            }
            File.Copy(tmp, file, true);
            try { File.Delete(tmp); } catch { }
        }

        [CommandMethod("RVTUNIFY", CommandFlags.Modal)]
        public void Unify()
        {
            Editor ed = Application.DocumentManager.MdiActiveDocument?.Editor;
            var result = new MergeResult();
            MergeManifest manifest = null;
            string manifestPath = null;

            try
            {
                var opts = new PromptStringOptions("\nRVTUNIFY manifest path: ") { AllowSpaces = true };
                PromptResult pr = ed.GetString(opts);
                if (pr.Status != PromptStatus.OK) return;
                manifestPath = pr.StringResult.Trim().Trim('"');

                manifest = ContractJson.Read<MergeManifest>(manifestPath);
                if (manifest.Options == null) manifest.Options = new MergeOptions();

                var log = new MergeLog(manifest.LogPath, ed, result);
                log.Info($"Manifest: {manifestPath} ({manifest.Items?.Count ?? 0} items)");

                new MasterDrawingBuilder(manifest, log, result).Build();
            }
            catch (System.Exception ex)
            {
                result.Success = false;
                result.Errors.Add("Fatal: " + ex);
                try { ed?.WriteMessage("\n[RVTUNIFY] FATAL " + ex); } catch { }
            }
            finally
            {
                string resultPath = manifest?.ResultPath
                                    ?? (manifestPath != null ? Path.ChangeExtension(manifestPath, ".result.json") : null);
                if (resultPath != null)
                {
                    try { ContractJson.Write(resultPath, result); }
                    catch (System.Exception ex) { ed?.WriteMessage("\n[RVTUNIFY] cannot write result: " + ex.Message); }
                }
            }
        }
    }
}
