using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using RevitDwgUnifier.Contracts;
using RevitDwgUnifier.Export;
using RevitDwgUnifier.Infrastructure;
using RevitDwgUnifier.Merge;
using RevitDwgUnifier.Tools.Exporting;

namespace RevitDwgUnifier.Core
{
    /// <summary>Result of "DWG por prancha": the files written and the image passes left to run.</summary>
    public sealed class PerSheetReport
    {
        public readonly List<string> Files = new List<string>();
        public readonly List<string> Problems = new List<string>();
        /// <summary>First of a chain of image (OLE) passes, one file after the other (<see cref="OleBackgroundJob.Next"/>).</summary>
        public OleBackgroundJob PendingOle;
    }

    public sealed partial class UnifiedExportJob
    {
        /// <summary>
        /// "DWG por prancha" through the SAME pipeline as the DWG único: the same Revit export options
        /// and staging (crops, views off sheets on a title block), then every item goes through the
        /// AutoCAD engine alone (a one-sheet "DWG único"): same clean-up, Padrão JA, ByLayer colours,
        /// non-plotting viewports, paper units, model laid out like the sheet, layout names, images.
        /// Several Core Consoles run side by side. Without AutoCAD, the raw Revit DWGs are delivered.
        /// </summary>
        public PerSheetReport RunPerSheet(IList<ViewInfo> selection, string folder, string pattern)
        {
            var result = new PerSheetReport();
            FileSafety.CleanupOldJobFolders(TimeSpan.FromDays(1));
            string work = FileSafety.CreateJobFolder();
            Logger.BeginJob(Path.Combine(work, "job.log"));
            bool keepWork = _s.KeepIntermediateFiles;
            var report = new JobReport { WorkFolder = work };
            try
            {
                List<PlannedItem> plan = ExportPlanner.Build(selection, _s.SkipViewsAlreadyOnSelectedSheets, _s.SheetTabNameIsNumber);
                if (plan.Count == 0) { result.Problems.Add("DWG por prancha: nada para exportar."); return result; }

                // one file name per item, from the naming pattern of the exporter
                var nomes = new Dictionary<string, string>();
                var usados = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (PlannedItem p in plan)
                {
                    View v = _doc.GetElement(new ElementId(p.Info.Id)) as View;
                    string n = FileSafety.SanitizeFileName(v != null ? ExportNaming.Name(_doc, v, pattern) : p.LayoutName, 150);
                    if (n.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 4);
                    string unico = n;
                    for (int k = 2; !usados.Add(unico); k++) unico = n + " (" + k + ")";
                    nomes[p.Key] = unico;
                }

                DWGExportOptions options = DwgOptionsFactory.Create(_doc, _s);
                var manifest = new MergeManifest
                {
                    SourceProject = _doc.PathName,
                    Options = _s.Merge ?? new MergeOptions(),
                    Coordinates = CoordinateService.Describe(_doc, _s.CoordinateBase, options.TargetUnit, report.Warnings)
                };
                manifest.Options.DwgVersion = _s.DwgVersion;
                manifest.Options.SplitSheetsByViewport = _s.SplitSheetsByViewport;
                manifest.Options.ColumnLayers = ExportLayers.Columns(_doc, options);
                manifest.Options.InstallationCategoryLayers = ExportLayers.KeepAsBlocks(_doc, options);
                if (manifest.Options.JaStandards) InstallationExport.Fill(_doc, options, manifest.Options);

                // ------------------------------------------------------------ STAGE + EXPORT (same as the DWG único)
                ExportItems(plan, options, work, manifest, report);
                result.Problems.AddRange(report.Warnings);
                report.Warnings.Clear();
                if (manifest.Items.Count == 0) { result.Problems.Add("DWG por prancha: nenhum item pôde ser exportado do Revit."); keepWork = true; return result; }
                Directory.CreateDirectory(folder);

                var probe = new CoreConsoleMergeEngine(_s.CoreConsolePath, TimeSpan.FromMinutes(_s.MergeTimeoutMinutes));
                if (!probe.IsAvailable(out string reason))
                {
                    result.Problems.Add("DWG por prancha sem os ajustes do AutoCAD: " + reason);
                    foreach (MergeItem it in manifest.Items) Entregar(it.SourceDwgPath, folder, nomes[it.Key], _s.AllowOverwrite, result);
                    return result;
                }

                // ------------------------------------------------------------ one engine run per item, side by side
                var tarefas = new List<ItemTarefa>();
                int idx = 0;
                foreach (MergeItem it in manifest.Items)
                {
                    idx++;
                    string sub = Path.Combine(work, "p" + idx.ToString("000"));
                    Directory.CreateDirectory(sub);
                    string tmpl = Path.Combine(sub, "base.json");
                    ContractJson.Write(tmpl, manifest);              // deep copy: each run owns its options
                    MergeManifest m = ContractJson.Read<MergeManifest>(tmpl);
                    m.Items = new List<MergeItem> { it };
                    m.OutputDwgPath = Path.Combine(sub, "out.dwg");
                    m.ResultPath = Path.Combine(sub, "result.json");
                    m.LogPath = Path.Combine(sub, "merge.log");
                    m.ProgressPath = Path.Combine(sub, "progress.log");
                    string destino = Path.Combine(folder, nomes[it.Key] + ".dwg");
                    m.ImageFolder = Path.ChangeExtension(destino, null) + "_imagens";
                    tarefas.Add(new ItemTarefa { Item = it, Manifesto = m, Pasta = sub, Destino = destino, PastaImagensExistia = Directory.Exists(m.ImageFolder) });
                }

                int paralelo = Math.Max(1, Math.Min(3, Environment.ProcessorCount / 2));
                int feitos = 0;
                CancellationToken token = _progress.Token;
                var trabalho = Task.Run(() => Parallel.ForEach(tarefas, new ParallelOptions { MaxDegreeOfParallelism = paralelo }, t =>
                {
                    if (token.IsCancellationRequested) return;
                    try { Unir(t, token); }
                    catch (Exception ex) { t.Erro = ex.Message; Logger.Error($"DWG por prancha: '{t.Item.LayoutName}' falhou no AutoCAD.", ex); }
                    Interlocked.Increment(ref feitos);
                }));
                // the progress window lives on this thread: report from here while the consoles work
                while (!trabalho.Wait(300))
                    _progress.Report(feitos, tarefas.Count, $"Ajustando no AutoCAD ({paralelo} por vez): {feitos} de {tarefas.Count}…");
                _progress.Report(tarefas.Count, tarefas.Count, "Gravando os DWGs…");

                // steps that crashed AutoCAD: switched off for the next exports (as in the DWG único)
                var desligados = tarefas.SelectMany(t => t.EtapasDesligadas).Distinct().ToList();
                if (desligados.Count > 0)
                {
                    foreach (string step in desligados) DisableStep(_s.Merge, step);
                    if (desligados.Any(s => s != "activate")) SettingsStore.Save(_s);
                    result.Problems.Add("Etapa(s) de limpeza que travaram o AutoCAD e foram desligadas: " + string.Join(", ", desligados) + ". Você pode religá-las na janela.");
                }

                // ------------------------------------------------------------ deliver + images
                CoreConsoleInstall acad = probe.Install;
                OleBackgroundJob ultimo = null;
                bool oleOk = false, oleTestado = false;
                foreach (ItemTarefa t in tarefas)
                {
                    MergeResult r = t.Resultado;
                    foreach (string stat in r?.Statistics ?? new List<string>()) Logger.Info($"AutoCAD ({t.Item.LayoutName}): {stat}");
                    foreach (string w in r?.Warnings ?? new List<string>()) Logger.Warn($"AutoCAD ({t.Item.LayoutName}): {w}");
                    bool ok = r != null && r.Success && File.Exists(t.Manifesto.OutputDwgPath);
                    if (!ok)
                    {
                        string porque = t.Erro ?? r?.Errors?.FirstOrDefault() ?? (token.IsCancellationRequested ? "cancelado" : "o AutoCAD não concluiu");
                        result.Problems.Add($"{Path.GetFileName(t.Destino)}: entregue sem os ajustes do AutoCAD ({porque}).");
                        Entregar(t.Item.SourceDwgPath, folder, Path.GetFileNameWithoutExtension(t.Destino), _s.AllowOverwrite, result);
                        keepWork = true;
                        continue;
                    }
                    string final = FileSafety.CopyWithRetry(t.Manifesto.OutputDwgPath, t.Destino, _s.AllowOverwrite);
                    result.Files.Add(final);
                    if (!string.Equals(final, t.Destino, StringComparison.OrdinalIgnoreCase))
                        result.Problems.Add($"'{Path.GetFileName(t.Destino)}' estava em uso ou já existia; salvo como '{Path.GetFileName(final)}'.");

                    if (r.ImageCount > 0 && _s.EmbedImagesAsOle)
                    {
                        if (!oleTestado)
                        {
                            oleTestado = true;
                            oleOk = new OleEmbedEngine(acad, TimeSpan.FromMinutes(_s.MergeTimeoutMinutes)).IsAvailable(out string why);
                            if (!oleOk) result.Problems.Add(why);
                        }
                        if (oleOk)
                        {
                            keepWork = true;
                            var job = new OleBackgroundJob
                            {
                                Install = acad, Timeout = TimeSpan.FromMinutes(_s.MergeTimeoutMinutes),
                                MergedDwg = t.Manifesto.OutputDwgPath, EngineDll = Path.Combine(t.Pasta, "engine", "RevitDwgUnifier.AcadMerge.dll"),
                                WorkDir = t.Pasta, DwgVersion = _s.DwgVersion, TargetDwg = final, ImageFolder = t.Manifesto.ImageFolder,
                                DeleteImageFolder = !t.PastaImagensExistia, KeepWork = _s.KeepIntermediateFiles, ImageCount = r.ImageCount
                            };
                            if (ultimo == null) result.PendingOle = job; else ultimo.Next = job;
                            ultimo = job;
                            continue;
                        }
                    }
                    if (r.ImageCount > 0) result.Problems.Add($"{Path.GetFileName(final)}: {r.ImageCount} imagem(ns) vinculadas à pasta {Path.GetFileName(t.Manifesto.ImageFolder)}; mantenha-a junto do DWG.");
                    else if (!t.PastaImagensExistia) FileSafety.TryDeleteDirectory(t.Manifesto.ImageFolder);
                }
                if (result.PendingOle != null)
                    result.Problems.Add("As imagens dos DWGs por prancha estão sendo incorporadas em segundo plano, um arquivo por vez (janela no canto da tela).");
                result.Problems.AddRange(report.Warnings);
                return result;
            }
            catch (OperationCanceledException) { result.Problems.Add("DWG por prancha cancelado."); return result; }
            catch (Exception ex)
            {
                Logger.Error("DWG por prancha falhou.", ex);
                result.Problems.Add("DWG por prancha: " + ex.Message);
                keepWork = true;
                return result;
            }
            finally
            {
                Logger.EndJob();
                if (!keepWork) FileSafety.TryDeleteDirectory(work);
            }
        }

        private sealed class ItemTarefa
        {
            public MergeItem Item;
            public MergeManifest Manifesto;
            public string Pasta, Destino, Erro;
            public bool PastaImagensExistia;
            public MergeResult Resultado;
            public readonly List<string> EtapasDesligadas = new List<string>();
        }

        /// <summary>
        /// One item through the engine (worker thread: no Revit API, no UI). A native crash in a
        /// clean-up step switches that step off for this file and retries, as the DWG único does.
        /// </summary>
        private void Unir(ItemTarefa t, CancellationToken token)
        {
            var engine = new CoreConsoleMergeEngine(_s.CoreConsolePath, TimeSpan.FromMinutes(_s.MergeTimeoutMinutes));
            if (!engine.IsAvailable(out string reason)) { t.Erro = reason; return; }
            string manifestPath = Path.Combine(t.Pasta, "manifest.json");
            for (int tentativa = 1; tentativa <= 4; tentativa++)
            {
                TryDelete(t.Manifesto.ResultPath);
                TryDelete(t.Manifesto.ProgressPath);
                ContractJson.Write(manifestPath, t.Manifesto);
                t.Resultado = engine.Merge(t.Manifesto, manifestPath, token);
                if (t.Resultado.Success || File.Exists(t.Manifesto.ResultPath) || token.IsCancellationRequested) return;
                string unfinished = MergeProgress.FindUnfinished(t.Manifesto.ProgressPath);
                if (unfinished == null || !unfinished.StartsWith(MergeProgress.Step)) return; // the sheet itself crashed
                string step = unfinished.Substring(MergeProgress.Step.Length);
                if (!DisableStep(t.Manifesto.Options, step)) return;
                t.EtapasDesligadas.Add(step);
                Logger.Warn($"DWG por prancha '{t.Item.LayoutName}': a etapa '{step}' travou o AutoCAD; repetindo sem ela.");
            }
        }

        private static void Entregar(string origem, string pasta, string nome, bool sobrescrever, PerSheetReport result)
        {
            if (string.IsNullOrEmpty(origem) || !File.Exists(origem)) return;
            string final = FileSafety.CopyWithRetry(origem, Path.Combine(pasta, nome + ".dwg"), sobrescrever);
            result.Files.Add(final);
        }
    }
}
