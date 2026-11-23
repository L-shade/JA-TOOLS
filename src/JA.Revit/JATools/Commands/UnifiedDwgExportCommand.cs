using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDwgUnifier.Core;
using RevitDwgUnifier.Export;
using RevitDwgUnifier.Infrastructure;
using RevitDwgUnifier.Tools.Common;
using RevitDwgUnifier.Tools.Exporting;
using RevitDwgUnifier.UI;

namespace RevitDwgUnifier.Commands
{
    /// <summary>
    /// "Exportar Pranchas": the export hub. One window (ExportWindow) picks the items and the
    /// formats; then, in this order (fast first, AutoCAD last):
    ///   1. PDF            – Revit's PDF export, one per item or one combined file;
    ///   2. DWG por prancha – Revit's DWG export with the chosen setup, one file per item;
    ///   3. DWG único       – UnifiedExportJob (one DWG, one layout per sheet, AutoCAD merge).
    /// One progress window with all the stages and one result window at the end.
    /// Manual transaction mode: the DWG job opens (and always rolls back) its own TransactionGroup.
    /// Nothing escapes as an exception - Revit gets Succeeded/Cancelled/Failed and the details are in the log.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class UnifiedDwgExportCommand : IExternalCommand
    {
        private const string ToolTitle = "Exportar Pranchas";

        /// <summary>What the native (PDF / DWG por prancha) stages produced.</summary>
        private sealed class NativeReport
        {
            public int Pdfs, Dwgs, Pngs;
            public bool Cancelled;
            public readonly List<string> Problems = new List<string>();
        }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // telemetria: this button has its own flow (not a ToolCommand)
            var rodada = RevitDwgUnifier.Infrastructure.Telemetria.IniciarComando("Exportar Pranchas", commandData.Application.ActiveUIDocument, false);
            Result r = Result.Failed;
            Exception erro = null;
            try { r = ExecutarExportacao(commandData, ref message, elements); return r; }
            catch (Exception ex) { erro = ex; throw; }
            finally { RevitDwgUnifier.Infrastructure.Telemetria.FimComando(rodada, r, erro); }
        }

        private Result ExecutarExportacao(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiApp = commandData.Application;
            UIDocument uiDoc = uiApp.ActiveUIDocument;
            Document doc = uiDoc.Document;

            try
            {
                if (doc.IsModifiable)
                {
                    message = "Outro comando ainda está editando o modelo. Termine-o e tente novamente.";
                    return Result.Failed;
                }

                ExportSettings settings = SettingsStore.Load();
                SheetExportOptions formats = SheetExportOptions.Load();
                List<ViewInfo> catalog = ViewCatalog.Collect(doc);

                // Pre-select whatever is selected in the Project Browser (views/sheets).
                var preselected = uiDoc.Selection.GetElementIds()
                    .Select(id => doc.GetElement(id)).OfType<View>().Select(v => v.Id.Value).ToList();

                IList<string> setups = ExportSetupService.GetSetupNames(doc);
                var vm = new ExportViewModel(catalog, preselected,
                    setups, ExportSetupService.GetActiveSetupName(doc),
                    CollectTitleBlocks(doc), settings, formats, ExportNaming.ColorChoices);

                var window = new ExportWindow(vm);
                new WindowInteropHelper(window).Owner = uiApp.MainWindowHandle;
                if (window.ShowDialog() != true) return Result.Cancelled;

                settings = vm.Commit();
                SettingsStore.Save(settings);
                formats.Save();
                IList<ViewInfo> selection = vm.GetSelection();
                string folder = settings.OutputFolder;
                Directory.CreateDirectory(folder);

                // ---- stages shown in the progress tracker
                var stages = new List<string>();
                int pdfStage = -1, dwgStage = -1, unifiedStage = -1, pngStage = -1;
                if (formats.Pdf) { pdfStage = stages.Count; stages.Add("PDF"); }
                if (formats.Png) { pngStage = stages.Count; stages.Add("PNG"); }
                if (formats.DwgPerSheet) { dwgStage = stages.Count; stages.Add("DWG por prancha"); }
                if (formats.DwgUnified) { unifiedStage = stages.Count; stages.AddRange(ProgressWindow.DwgStages); }
                stages.Add("Concluir");

                var native = new NativeReport();
                JobReport unified = null;
                PerSheetReport perSheet = null;
                var progress = new ProgressWindow("Exportando " + selection.Count + (selection.Count == 1 ? " item" : " itens"), stages.ToArray());
                new WindowInteropHelper(progress).Owner = uiApp.MainWindowHandle;
                progress.Show();
                try
                {
                    var views = selection.Select(i => doc.GetElement(new ElementId(i.Id)) as View).Where(v => v != null).ToList();

                    if (pdfStage >= 0 && !progress.Token.IsCancellationRequested)
                    {
                        progress.BeginStage(pdfStage, "Gerando PDF…");
                        ExportPdfs(doc, views, folder, formats, progress, native);
                    }
                    if (pngStage >= 0 && !progress.Token.IsCancellationRequested)
                    {
                        progress.BeginStage(pngStage, "Gerando PNG…");
                        ExportPngs(doc, views, folder, formats, progress, native);
                    }
                    if (dwgStage >= 0 && !progress.Token.IsCancellationRequested)
                    {
                        progress.BeginStage(dwgStage, "Gerando DWG por prancha…");
                        // same pipeline and options as the DWG único, one file per item
                        try
                        {
                            perSheet = new UnifiedExportJob(doc, settings, progress).RunPerSheet(selection, folder, formats.FilePattern);
                            native.Dwgs += perSheet.Files.Count;
                            native.Problems.AddRange(perSheet.Problems);
                        }
                        catch (OperationCanceledException) { native.Cancelled = true; }
                    }
                    if (unifiedStage >= 0 && !progress.Token.IsCancellationRequested)
                    {
                        progress.BeginDwg(unifiedStage);
                        try { unified = new UnifiedExportJob(doc, settings, progress).Run(selection); }
                        catch (OperationCanceledException) { native.Cancelled = true; }
                    }
                    native.Cancelled |= progress.Token.IsCancellationRequested;
                    progress.Finish("Concluído.");
                }
                finally
                {
                    progress.Close();
                }

                // The result window no longer holds Revit (modeless), and the AutoCAD image pass starts
                // only when Revit is idle: before, the images were embedded only after the window was closed.
                ShowSummary(native, unified, formats, folder, uiApp.MainWindowHandle);
                // image passes one after the other: the DWG único first, then the per-sheet files
                RevitDwgUnifier.Merge.OleBackgroundJob ole = unified?.PendingOle;
                if (ole != null && perSheet?.PendingOle != null)
                {
                    var last = ole;
                    while (last.Next != null) last = last.Next;
                    last.Next = perSheet.PendingOle;
                }
                ole = ole ?? perSheet?.PendingOle;
                if (ole != null) StartWhenIdle(uiApp, ole);
                bool any = native.Pdfs + native.Dwgs + native.Pngs > 0 || (unified != null && (unified.Success || unified.PartsFolder != null));
                return any ? Result.Succeeded : native.Cancelled ? Result.Cancelled : Result.Failed;
            }
            catch (OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                Logger.Error("Exportar Pranchas crashed.", ex);
                message = ex.Message;
                try
                {
                    ResultDialog.Show(uiApp.MainWindowHandle, ToolTitle,
                        "Erro: " + ex.Message + "\n\nOs detalhes técnicos foram gravados no registro do JA Tools.", ResultKind.Error);
                }
                catch
                {
                    TaskDialog.Show("JA Tools - " + ToolTitle, "A exportação parou por um erro inesperado:\n" + ex.Message +
                                    "\n\nDetalhes gravados em:\n" + Logger.DailyLogPath);
                }
                return Result.Failed;
            }
        }

        // ------------------------------------------------------------------ PDF
        private static void ExportPdfs(Document doc, List<View> views, string folder, SheetExportOptions f,
                                       ProgressWindow progress, NativeReport rep)
        {
            if (views.Count == 0) return;
            if (f.PdfCombine)
            {
                progress.Report(0, 1, "PDF único com " + views.Count + " item(ns)…");
                var o = ExportNaming.Pdf(f.PdfColorIndex, true);
                o.FileName = ExportNaming.Name(doc, views[0], f.PdfCombinedName);
                try { if (doc.Export(folder, views.Select(v => v.Id).ToList(), o)) rep.Pdfs++; }
                catch (Exception ex) { rep.Problems.Add("PDF único: " + ex.Message); }
                progress.Report(1, 1, "PDF único gravado.");
                return;
            }
            for (int i = 0; i < views.Count; i++)
            {
                if (progress.Token.IsCancellationRequested) return;
                View v = views[i];
                progress.Report(i, views.Count, "PDF: " + Label(v));
                var o = ExportNaming.Pdf(f.PdfColorIndex, true);
                o.FileName = ExportNaming.Name(doc, v, f.FilePattern);
                try { if (doc.Export(folder, new List<ElementId> { v.Id }, o)) rep.Pdfs++; else rep.Problems.Add("PDF " + Label(v) + ": o Revit não gerou o arquivo."); }
                catch (Exception ex) { rep.Problems.Add("PDF " + Label(v) + ": " + ex.Message); }
            }
            progress.Report(views.Count, views.Count, "PDFs gravados.");
        }

        // ------------------------------------------------------------------ PNG (1.17: was Exportar Vistas)
        private static void ExportPngs(Document doc, List<View> views, string folder, SheetExportOptions f,
                                       ProgressWindow progress, NativeReport rep)
        {
            for (int i = 0; i < views.Count; i++)
            {
                if (progress.Token.IsCancellationRequested) return;
                View v = views[i];
                progress.Report(i, views.Count, "PNG: " + Label(v));
                try
                {
                    string file = PngExport.ExportPng(doc, v, folder, ExportNaming.Name(doc, v, f.FilePattern), f.PngPixels);
                    if (file == null) { rep.Problems.Add("PNG " + Label(v) + ": o Revit não gerou a imagem."); continue; }
                    if (f.PngTransparent) PngExport.MakeWhiteTransparent(file);
                    rep.Pngs++;
                }
                catch (Exception ex) { rep.Problems.Add("PNG " + Label(v) + ": " + ex.Message); }
            }
            progress.Report(views.Count, views.Count, "PNGs gravados.");
        }

        // ------------------------------------------------------------------ DWG por prancha
        // Since 1.23 it runs through UnifiedExportJob.RunPerSheet: same options as the DWG único.

        private static string Label(View v) => v is ViewSheet s ? s.SheetNumber + " - " + s.Name : v.Name;

        private static IList<NamedId> CollectTitleBlocks(Document doc)
        {
            var list = new List<NamedId> { new NamedId { Id = -1, Label = "<Nenhum - apenas moldura>" } };
            list.AddRange(new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .Cast<FamilySymbol>()
                .OrderBy(s => s.FamilyName).ThenBy(s => s.Name)
                .Select(s => new NamedId { Id = s.Id.Value, Label = s.FamilyName + " : " + s.Name }));
            return list;
        }

        // ------------------------------------------------------------------ OLE pass
        /// <summary>Starts the background image pass on the first Idling after the command has
        /// returned (nothing of Revit is busy then); a 5 s timer starts it anyway if Idling never comes.</summary>
        private static void StartWhenIdle(UIApplication uiApp, RevitDwgUnifier.Merge.OleBackgroundJob job)
        {
            int started = 0;
            void Go()
            {
                if (Interlocked.Exchange(ref started, 1) == 1) return;
                try { RevitDwgUnifier.Merge.OleBackgroundJob.Start(job); }
                catch (Exception ex) { Logger.Error("OLE em segundo plano não iniciou.", ex); }
            }
            EventHandler<Autodesk.Revit.UI.Events.IdlingEventArgs> onIdle = null;
            onIdle = (s, e) =>
            {
                try { uiApp.Idling -= onIdle; } catch { }
                Go();
            };
            try { uiApp.Idling += onIdle; }
            catch (Exception ex) { Logger.Warn("Idling indisponível: " + ex.Message); Go(); return; }
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            timer.Tick += (s, e) => { timer.Stop(); Go(); };
            timer.Start();
        }

        // ------------------------------------------------------------------ result
        private static void ShowSummary(NativeReport n, JobReport r, SheetExportOptions f, string folder, IntPtr owner)
        {
            var sb = new StringBuilder();
            var parts = new List<string>();
            var actions = new List<ResultAction>();
            bool unifiedOk = r != null && r.Success;

            if (f.Pdf) parts.Add(n.Pdfs + (n.Pdfs == 1 ? " PDF" : " PDFs"));
            if (f.DwgPerSheet) parts.Add(n.Dwgs + (n.Dwgs == 1 ? " DWG por prancha" : " DWGs por prancha"));
            if (f.Png) parts.Add(n.Pngs + (n.Pngs == 1 ? " PNG" : " PNGs"));
            if (f.DwgUnified) parts.Add(unifiedOk ? "1 DWG único" : "DWG único com problema");
            string headline = n.Cancelled ? "Exportação cancelada — " + string.Join(", ", parts)
                                          : "Exportado: " + string.Join(", ", parts);

            if (unifiedOk)
                sb.AppendLine($"DWG único: {Path.GetFileName(r.OutputPath)} com {r.Layouts.Count} layout(s), " +
                              $"{r.ItemsExported} de {r.ItemsPlanned} item(ns).");
            else if (r != null && r.PartsFolder != null)
            {
                sb.AppendLine("O DWG único não pôde ser unido. Os DWGs individuais ficaram em:");
                sb.AppendLine(r.PartsFolder);
            }
            else if (r != null)
                sb.AppendLine("O DWG único não foi concluído (veja os erros abaixo).");
            sb.AppendLine("Pasta:");
            sb.AppendLine(folder);

            var errors = new List<string>();
            var warnings = new List<string>(n.Problems);
            if (r != null) { errors.AddRange(r.Errors); warnings.AddRange(r.Warnings); }
            if (errors.Count > 0)
            {
                sb.AppendLine().AppendLine("Erros:");
                foreach (var e in errors.Take(8)) sb.AppendLine(" - " + e);
            }
            if (warnings.Count > 0)
            {
                sb.AppendLine().AppendLine($"Avisos ({warnings.Count}):");
                foreach (var w in warnings.Take(10)) sb.AppendLine(" - " + w);
                if (warnings.Count > 10) sb.AppendLine(" - ...");
            }
            if (r != null && r.EngineWarnings.Count > 0)
                sb.AppendLine().AppendLine($"Avisos técnicos do AutoCAD: {r.EngineWarnings.Count} (detalhes no registro).");

            string dir = folder;
            actions.Add(new ResultAction("Abrir pasta", () => ResultDialog.OpenPath(dir), !unifiedOk));
            if (unifiedOk)
            {
                string file = r.OutputPath;
                actions.Add(new ResultAction("Abrir DWG", () => ResultDialog.OpenFile(file), true));
            }

            bool nothing = n.Pdfs + n.Dwgs == 0 && !unifiedOk && (r == null || r.PartsFolder == null);
            ResultKind kind = nothing ? (n.Cancelled ? ResultKind.Info : ResultKind.Error)
                            : errors.Count > 0 || warnings.Count > 0 || n.Cancelled || (r != null && !r.Success) ? ResultKind.Warning
                            : ResultKind.Success;
            try
            {
                ResultDialog.ShowModeless(owner, ToolTitle, sb.ToString(), kind, headline, actions.ToArray());
            }
            catch (Exception ex)
            {
                Logger.Warn("Result window failed: " + ex.Message);
                TaskDialog.Show("JA Tools - " + ToolTitle, headline + "\n\n" + sb + "\nRegistro: " + Logger.DailyLogPath);
            }
        }
    }
}
