using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Autodesk.Revit.DB;
using RevitDwgUnifier.Contracts;
using RevitDwgUnifier.Export;
using RevitDwgUnifier.Infrastructure;
using RevitDwgUnifier.Merge;

namespace RevitDwgUnifier.Core
{
    public interface IProgressSink
    {
        void Report(int current, int total, string message);
        CancellationToken Token { get; }
    }

    public sealed class JobReport
    {
        public bool Success { get; set; }
        public string OutputPath { get; set; }
        public string PartsFolder { get; set; }
        public string WorkFolder { get; set; }
        public int ItemsPlanned { get; set; }
        public int ItemsExported { get; set; }
        public List<string> Layouts { get; } = new List<string>();
        public List<string> Warnings { get; } = new List<string>();
        /// <summary>Technical warnings of the AutoCAD merge engine (English, written to the log;
        /// the summary dialog only shows how many there are).</summary>
        public List<string> EngineWarnings { get; } = new List<string>();
        public List<string> Errors { get; } = new List<string>();
        /// <summary>The AutoCAD image (OLE) pass, ready but NOT started: the command starts it once
        /// Revit is idle (started from inside the command it only ran after the result window closed).</summary>
        public OleBackgroundJob PendingOle { get; set; }
    }

    /// <summary>
    /// Orchestrates one "Unified DWG" run:
    ///
    ///   PLAN      de-duplicate the selection (ExportPlanner)                       [pitfall 6]
    ///   STAGE     TransactionGroup: enforce crops, put standalone views on
    ///             temporary sheets (auto-fit title block), commit inner transactions [pitfall 5]
    ///   EXPORT    one DWG per item with ONE shared DWGExportOptions                 [pitfalls 1-4,6]
    ///   RESTORE   TransactionGroup.RollBack() in finally -> project untouched
    ///   MERGE     manifest.json -> AutoCAD Core Console -> master DWG               [pitfalls 1-6]
    ///   DELIVER   lock-safe copy with auto-increment                                [pitfall 7]
    ///
    /// Every item is wrapped in its own try/catch: one bad view is reported, not fatal.
    /// </summary>
    public sealed partial class UnifiedExportJob
    {
        private readonly Document _doc;
        private readonly ExportSettings _s;
        private readonly IProgressSink _progress;

        public UnifiedExportJob(Document doc, ExportSettings settings, IProgressSink progress)
        {
            _doc = doc;
            _s = settings;
            _progress = progress;
        }

        public JobReport Run(IList<ViewInfo> selection)
        {
            var report = new JobReport();
            FileSafety.CleanupOldJobFolders(TimeSpan.FromDays(1));
            string work = FileSafety.CreateJobFolder();
            report.WorkFolder = work;
            Logger.BeginJob(Path.Combine(work, "job.log"));
            bool keepWork = _s.KeepIntermediateFiles;

            try
            {
                // ------------------------------------------------------------ PLAN
                List<PlannedItem> plan = ExportPlanner.Build(selection, _s.SkipViewsAlreadyOnSelectedSheets, _s.SheetTabNameIsNumber);
                report.ItemsPlanned = plan.Count;
                if (plan.Count == 0)
                {
                    report.Errors.Add("Nada para exportar depois de remover os duplicados.");
                    return report;
                }

                DWGExportOptions options = DwgOptionsFactory.Create(_doc, _s);
                var manifest = new MergeManifest
                {
                    SourceProject = _doc.PathName,
                    OutputDwgPath = Path.Combine(work, "merged.dwg"),
                    ResultPath = Path.Combine(work, "result.json"),
                    LogPath = Path.Combine(work, "merge.log"),
                    Options = _s.Merge ?? new MergeOptions(),
                    Coordinates = CoordinateService.Describe(_doc, _s.CoordinateBase, options.TargetUnit, report.Warnings)
                };
                manifest.Options.DwgVersion = _s.DwgVersion;
                manifest.Options.SplitSheetsByViewport = _s.SplitSheetsByViewport;
                manifest.Options.ColumnLayers = ExportLayers.Columns(_doc, options);
                manifest.Options.InstallationCategoryLayers = ExportLayers.KeepAsBlocks(_doc, options);
                if (manifest.Options.JaStandards) InstallationExport.Fill(_doc, options, manifest.Options);

                // Final name decided up front: the sheets' images are copied next to it, in
                // "<name>_imagens", so the DWG never points at the temporary job folder.
                string desired = Path.Combine(_s.OutputFolder, FileSafety.SanitizeFileName(
                    string.IsNullOrWhiteSpace(_s.OutputFileName) ? _doc.Title : _s.OutputFileName, 150) + ".dwg");
                string imageFolder = Path.ChangeExtension(desired, null) + "_imagens";
                bool imageFolderExisted = Directory.Exists(imageFolder);
                manifest.ImageFolder = imageFolder;

                // ------------------------------------------------------------ STAGE + EXPORT
                ExportItems(plan, options, work, manifest, report);
                report.ItemsExported = manifest.Items.Count;
                if (manifest.Items.Count == 0)
                {
                    report.Errors.Add("Nenhum item pôde ser exportado do Revit. Veja o registro (log).");
                    keepWork = true;
                    return report;
                }

                // ------------------------------------------------------------ MERGE
                _progress.Report(plan.Count, plan.Count + 1, "Unindo em um único DWG (AutoCAD Core Console)...");
                string manifestPath = Path.Combine(work, "manifest.json");
                manifest.ProgressPath = Path.Combine(work, "progress.log");

                IDwgMergeEngine engine = new CoreConsoleMergeEngine(_s.CoreConsolePath,
                                                                    TimeSpan.FromMinutes(_s.MergeTimeoutMinutes));
                if (!engine.IsAvailable(out string reason))
                {
                    report.Warnings.Add(reason);
                    DeliverParts(manifest, report);
                    return report;
                }

                var allItems = new List<MergeItem>(manifest.Items);
                var setAside = new List<MergeItem>();
                MergeResult merge = RunMergeWithRecovery(engine, manifest, manifestPath, setAside, report);

                report.EngineWarnings.AddRange(merge.Warnings ?? new List<string>());
                foreach (string w in report.EngineWarnings) Logger.Warn("AutoCAD: " + w);
                foreach (string e in merge.Errors ?? new List<string>()) Logger.Error("AutoCAD: " + e);
                if (merge.Errors != null && merge.Errors.Count > 0)
                    report.Errors.Add($"O AutoCAD informou {merge.Errors.Count} erro(s) na união (detalhes no registro).");
                report.Layouts.AddRange(merge.LayoutsCreated ?? new List<string>());
                foreach (string stat in merge.Statistics ?? new List<string>()) Logger.Info("AutoCAD: " + stat);
                if (merge.FailedItems != null)
                    setAside.AddRange(allItems.Where(i => merge.FailedItems.Contains(i.LayoutName) && !setAside.Contains(i)));

                if (!merge.Success || !File.Exists(manifest.OutputDwgPath))
                {
                    report.Errors.Add("A união falhou: foram entregues os DWGs individuais.");
                    keepWork = true;
                    manifest.Items = allItems;
                    DeliverParts(manifest, report);
                    return report;
                }

                // ------------------------------------------------------------ IMAGES -> OLE
                // Done in the BACKGROUND: the DWG is delivered right away with the images linked to
                // the image folder, and full AutoCAD embeds them while the user keeps working in Revit;
                // the delivered file is replaced when it finishes (a small window shows the progress).
                bool oleInBackground = false;
                CoreConsoleInstall acad = (engine as CoreConsoleMergeEngine)?.Install;
                if (merge.ImageCount > 0 && _s.EmbedImagesAsOle)
                {
                    var probe = new OleEmbedEngine(acad, TimeSpan.FromMinutes(_s.MergeTimeoutMinutes));
                    if (probe.IsAvailable(out string why)) oleInBackground = true;
                    else report.Warnings.Add(why);
                }
                else if (merge.ImageCount > 0)
                {
                    report.Warnings.Add($"{merge.ImageCount} imagem(ns) estão vinculadas (não incorporadas) a {imageFolder}. Mantenha essa pasta junto do DWG.");
                }

                // ------------------------------------------------------------ DELIVER
                report.OutputPath = FileSafety.CopyWithRetry(manifest.OutputDwgPath, desired, _s.AllowOverwrite);
                if (!string.Equals(report.OutputPath, desired, StringComparison.OrdinalIgnoreCase))
                    report.Warnings.Add($"'{Path.GetFileName(desired)}' estava em uso ou já existia; salvo como '{Path.GetFileName(report.OutputPath)}'.");

                if (setAside.Count > 0)
                {
                    DeliverItems(setAside, Path.ChangeExtension(report.OutputPath, null) + "_separados", report);
                    report.Warnings.Add($"{setAside.Count} item(ns) não puderam ser unidos e foram salvos como DWGs separados em " +
                                        report.PartsFolder);
                }

                if (oleInBackground)
                {
                    keepWork = true;     // the background pass owns the work folder and removes it at the end
                    report.PendingOle = new OleBackgroundJob
                    {
                        Install = acad,
                        Timeout = TimeSpan.FromMinutes(_s.MergeTimeoutMinutes),
                        MergedDwg = manifest.OutputDwgPath,
                        EngineDll = Path.Combine(work, "engine", "RevitDwgUnifier.AcadMerge.dll"),
                        WorkDir = work,
                        DwgVersion = _s.DwgVersion,
                        TargetDwg = report.OutputPath,
                        ImageFolder = imageFolder,
                        DeleteImageFolder = !imageFolderExisted,
                        KeepWork = _s.KeepIntermediateFiles,
                        ImageCount = merge.ImageCount
                    };
                    report.Warnings.Add($"As {merge.ImageCount} imagem(ns) estão sendo incorporadas no DWG em segundo plano (janela no canto da tela). " +
                                        "O arquivo já pode ser usado e é atualizado sozinho quando o AutoCAD terminar; pode continuar no Revit.");
                }
                else if (merge.ImageCount == 0 && !imageFolderExisted) FileSafety.TryDeleteDirectory(imageFolder);

                report.Success = true;
                Logger.Info("Unified DWG written: " + report.OutputPath);
                return report;
            }
            catch (OperationCanceledException)
            {
                report.Errors.Add("Exportação cancelada.");
                return report;
            }
            catch (Exception ex)
            {
                Logger.Error("Unified export failed.", ex);
                report.Errors.Add(ex.Message);
                keepWork = true;
                return report;
            }
            finally
            {
                foreach (var w in report.Warnings) Logger.Warn(w);
                Logger.EndJob();
                if (!keepWork) FileSafety.TryDeleteDirectory(work);
            }
        }

        private void ExportItems(List<PlannedItem> plan, DWGExportOptions options, string work,
                                 MergeManifest manifest, JobReport report)
        {
            // The group is ALWAYS rolled back: temporary sheets, duplicates and crop changes vanish.
            using (var group = new TransactionGroup(_doc, "Unified DWG export (temporary)"))
            {
                group.Start();
                try
                {
                    // Stage everything first (one regeneration pass), then export.
                    var staged = new Dictionary<string, StagedSheet>();
                    using (var t = new Transaction(_doc, "Preparar vistas para exportação DWG"))
                    {
                        FailureSwallower.Attach(t);
                        t.Start();

                        foreach (var item in plan)
                        {
                            _progress.Token.ThrowIfCancellationRequested();
                            try
                            {
                                if (item.Kind == ExportItemKind.Sheet)
                                {
                                    foreach (long vid in item.Info.PlacedViewIds)
                                        StagingService.EnforceCrop(_doc.GetElement(new ElementId(vid)) as View, _s);
                                }
                                else
                                {
                                    var view = (View)_doc.GetElement(new ElementId(item.Info.Id));
                                    StagingService.EnforceCrop(view, _s);
                                    staged[item.Key] = StagingService.StageView(_doc, view, _s);
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Error($"Staging failed for '{item.LayoutName}'.", ex);
                                report.Warnings.Add($"'{item.LayoutName}' ignorado: {ex.Message}");
                            }
                        }

                        if (t.Commit() != TransactionStatus.Committed)
                            throw new InvalidOperationException("Não foi possível preparar as vistas para exportação (transação desfeita).");
                    }

                    int i = 0;
                    foreach (var item in plan)
                    {
                        _progress.Token.ThrowIfCancellationRequested();
                        i++;
                        _progress.Report(i, plan.Count + 1, "Exportando " + item.LayoutName);

                        ElementId exportId;
                        bool hasTb = true;
                        if (item.Kind == ExportItemKind.Sheet) exportId = new ElementId(item.Info.Id);
                        else if (staged.TryGetValue(item.Key, out var st)) { exportId = st.SheetId; hasTb = st.HasTitleBlock; }
                        else continue; // staging failed, already reported

                        try
                        {
                            string folder = Path.Combine(work, "items", item.Key);
                            string dwg = RevitDwgExporter.ExportSingle(_doc, exportId, folder, item.Key, options);
                            manifest.Items.Add(new MergeItem
                            {
                                Key = item.Key,
                                Kind = item.Kind.ToString(),
                                RevitElementId = item.Info.Id.ToString(),
                                SourceDwgPath = dwg,
                                LayoutName = item.LayoutName,
                                ViewScale = item.Info.Scale,
                                HasTitleBlock = hasTb,
                                Title = item.Info.DisplayName,
                                HostedViewIds = item.Kind == ExportItemKind.Sheet
                                    ? item.Info.PlacedViewIds.Select(v => v.ToString()).ToList()
                                    : new List<string> { item.Info.Id.ToString() },
                                Viewports = item.Kind == ExportItemKind.Sheet && _s.SplitSheetsByViewport
                                    ? ViewportSpecBuilder.Build(_doc, (ViewSheet)_doc.GetElement(exportId))
                                    : new List<ViewportSpec>()
                            });
                            Logger.Info($"Exported '{item.LayoutName}' -> {dwg}");
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"DWG export failed for '{item.LayoutName}'.", ex);
                            report.Warnings.Add($"'{item.LayoutName}' não foi exportado: {ex.Message}");
                        }
                    }
                }
                finally
                {
                    // Undo everything we touched, whatever happened above.
                    if (group.GetStatus() == TransactionStatus.Started) group.RollBack();
                }
            }
        }

        /// <summary>
        /// Runs the merge; if AutoCAD dies with a native fatal error (no result.json), the progress
        /// markers tell which item or clean-up step was running. That item is set aside (delivered
        /// as its own DWG) or that step is disabled, and the merge is retried - so one bad sheet
        /// never costs the whole unified drawing.
        /// </summary>
        private MergeResult RunMergeWithRecovery(IDwgMergeEngine engine, MergeManifest manifest, string manifestPath,
                                                 List<MergeItem> setAside, JobReport report)
        {
            const int maxAttempts = 4;
            MergeResult merge = null;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                TryDelete(manifest.ResultPath);
                TryDelete(manifest.ProgressPath);
                ContractJson.Write(manifestPath, manifest);

                merge = engine.Merge(manifest, manifestPath, _progress.Token);
                bool engineAnswered = File.Exists(manifest.ResultPath);
                if (merge.Success || engineAnswered || attempt == maxAttempts) return merge;

                string unfinished = MergeProgress.FindUnfinished(manifest.ProgressPath);
                Logger.Warn($"Core Console crashed (attempt {attempt}); last unfinished marker: {unfinished ?? "<none>"}");
                if (unfinished == null) return merge; // crashed before/after the merge loop: nothing to isolate

                if (unfinished.StartsWith(MergeProgress.Start))
                {
                    string key = unfinished.Substring(MergeProgress.Start.Length);
                    MergeItem bad = manifest.Items.FirstOrDefault(i => i.Key == key);
                    if (bad == null) return merge;
                    manifest.Items.Remove(bad);
                    setAside.Add(bad);
                    report.Warnings.Add($"'{bad.LayoutName}' travou o AutoCAD durante a união; foi entregue como DWG separado.");
                    if (manifest.Items.Count == 0) return merge;
                }
                else
                {
                    string step = unfinished.Substring(MergeProgress.Step.Length);
                    if (!DisableStep(manifest.Options, step)) return merge;
                    // Remember it: the same AutoCAD installation would crash on every export.
                    // The option can be switched back on in the dialog (e.g. after an AutoCAD update).
                    // "activate" (viewport rebuild) is essential for the layouts: never persisted off.
                    if (step != "activate") SettingsStore.Save(_s);
                    string why = (engine as CoreConsoleMergeEngine)?.ModelerUnavailable == true
                        ? " (o AutoCAD Core Console não carregou o modelador 3D)" : "";
                    report.Warnings.Add($"A etapa de limpeza '{step}' travou o AutoCAD{why}; foi pulada e desligada " +
                                        "para as próximas exportações. Você pode religá-la na janela.");
                }
                _progress.Report(1, 1, $"O AutoCAD travou - tentando a união de novo (tentativa {attempt + 1})...");
            }
            return merge;
        }

        private static bool DisableStep(MergeOptions o, string step)
        {
            switch (step)
            {
                case "explode": o.ExplodeAnonymousBlocks = false; return true;
                case "hatch": o.NormalizeHatches = false; return true;
                case "text": o.RepairTextStyles = false; return true;
                case "dedupe": o.RemoveDuplicateEntities = false; return true;
                case "purge": o.PurgeUnused = false; return true;
                case "activate": o.ActivateViewports = false; return true;
                case "dimcolor": o.DimTextColorIndex = 0; return true;
                case "jastd": o.JaStandards = false; return true;
                case "bylayer": o.ColorsByLayer = false; return true;
                case "vpnoplot": o.ViewportsNoPlot = false; return true;
                case "modelsheets": o.ModelSheets = false; return true;
                default: return false; // "save": nothing left to switch off
            }
        }

        private static void TryDelete(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); } catch { }
        }

        private void DeliverItems(IEnumerable<MergeItem> items, string folder, JobReport report)
        {
            string baseFolder = folder;
            for (int n = 1; Directory.Exists(folder) && n < 100; n++) folder = baseFolder + "_" + n.ToString("00");
            Directory.CreateDirectory(folder);
            foreach (var item in items)
            {
                if (!File.Exists(item.SourceDwgPath)) continue;
                string target = Path.Combine(folder, FileSafety.SanitizeFileName(item.LayoutName) + ".dwg");
                FileSafety.CopyWithRetry(item.SourceDwgPath, target, allowOverwrite: false);
            }
            report.PartsFolder = folder;
        }

        /// <summary>Fallback when no merge engine is available: hand over the per-item DWGs
        /// plus the manifest, so the merge can be run later on a machine with AutoCAD.</summary>
        private void DeliverParts(MergeManifest manifest, JobReport report)
        {
            string name = FileSafety.SanitizeFileName(
                string.IsNullOrWhiteSpace(_s.OutputFileName) ? _doc.Title : _s.OutputFileName, 120);
            string parts = Path.Combine(_s.OutputFolder, name + "_parts");
            for (int n = 1; Directory.Exists(parts) && n < 100; n++)
                parts = Path.Combine(_s.OutputFolder, name + "_parts_" + n.ToString("00"));
            Directory.CreateDirectory(parts);

            foreach (var item in manifest.Items)
            {
                string target = Path.Combine(parts, FileSafety.SanitizeFileName(item.LayoutName) + ".dwg");
                FileSafety.CopyWithRetry(item.SourceDwgPath, target, allowOverwrite: false);
            }
            ContractJson.Write(Path.Combine(parts, "manifest.json"), manifest);
            report.PartsFolder = parts;
            Logger.Info("Per-item DWGs delivered to " + parts);
        }
    }
}
