using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using RevitDwgUnifier.Contracts;
using RevitDwgUnifier.Infrastructure;

namespace RevitDwgUnifier.Merge
{
    /// <summary>
    /// Optional second pass: opens the merged DWG in FULL AutoCAD (acad.exe, same version as the
    /// Core Console used for the merge) and runs RVTOLE, which replaces every raster image
    /// reference with an embedded OLE object at the same position.
    ///
    /// Why full AutoCAD: an OLE object can only be created by pasting from the Windows clipboard,
    /// and Core Console has neither a clipboard nor an OLE container.
    ///
    /// The AutoCAD window is shown while it works (OLE needs a real view). The script restores FILEDIA / SECURELOAD to the
    /// user's values right after use, saves (so QUIT never asks anything) and quits. The merged
    /// DWG is only replaced when result.json reports success, so a failure here never costs the
    /// user the drawing: images then stay as references to the image folder next to the DWG.
    /// </summary>
    public sealed class OleEmbedEngine
    {
        private readonly CoreConsoleInstall _install;
        private readonly TimeSpan _timeout;

        public OleEmbedEngine(CoreConsoleInstall install, TimeSpan timeout)
        {
            _install = install;
            _timeout = timeout;
        }

        public string AcadExe => _install == null ? null : Path.Combine(Path.GetDirectoryName(_install.ExePath), "acad.exe");

        public bool IsAvailable(out string reason)
        {
            if (_install == null || !File.Exists(AcadExe))
            {
                reason = "As imagens não foram incorporadas como OLE: o AutoCAD completo (acad.exe) não foi encontrado junto de " +
                         (_install?.ExePath ?? "accoreconsole.exe") + ".";
                return false;
            }
            reason = null;
            return true;
        }

        /// <param name="engineDll">The merge engine already copied into the job folder.</param>
        public MergeResult Run(string inputDwg, string engineDll, string workDir, string dwgVersion, CancellationToken token)
        {
            string oleDir = Path.Combine(workDir, "ole");
            Directory.CreateDirectory(oleDir);

            // AutoCAD saves its own copy (QSAVE) of the drawing it opened: work on a copy.
            string opened = Path.Combine(oleDir, "ole_input.dwg");
            File.Copy(inputDwg, opened, true);

            var manifest = new OleManifest
            {
                InputDwgPath = opened,
                OutputDwgPath = Path.Combine(oleDir, "merged_ole.dwg"),
                ResultPath = Path.Combine(oleDir, "ole_result.json"),
                LogPath = Path.Combine(workDir, "merge.log"),
                DwgVersion = dwgVersion
            };
            string manifestPath = Path.Combine(oleDir, "ole_manifest.json");
            ContractJson.Write(manifestPath, manifest);

            string scriptPath = Path.Combine(oleDir, "ole.scr");
            File.WriteAllText(scriptPath, BuildScript(engineDll, manifestPath), Encoding.ASCII);

            var psi = new ProcessStartInfo
            {
                FileName = AcadExe,
                Arguments = $"\"{opened}\" /nologo /b \"{scriptPath}\"",
                WorkingDirectory = Path.GetDirectoryName(AcadExe),
                UseShellExecute = true,
                // NOT minimized: OLE paste needs a drawing window with a real size (a minimized
                // window has a zero-size view and PASTECLIP silently creates nothing).
                WindowStyle = ProcessWindowStyle.Normal
            };

            if (Process.GetProcessesByName("acad").Length > 0)
                Logger.Info("AutoCAD já está aberto: a etapa OLE abre outra instância (mais lenta).");
            Logger.Info($"Starting AutoCAD {_install.Year} for the OLE pass: {psi.FileName} {psi.Arguments}");
            var sw = Stopwatch.StartNew();
            string stuck = null;
            using (var p = Process.Start(psi))
            {
                // Watchdog. A full AutoCAD that never reaches our command is almost always waiting on a
                // dialog (sign-in / licence, drawing recovery, a startup message) that nobody will answer:
                // give up after a few minutes instead of the whole merge timeout.
                long logStart = LogLength(manifest.LogPath), lastLen = logStart;
                var idle = Stopwatch.StartNew();
                bool started = false;
                Stopwatch grace = null;
                while (p != null && !p.WaitForExit(500))
                {
                    Report?.Invoke(started ? "Incorporando as imagens no AutoCAD…" : $"Abrindo o AutoCAD {_install.Year}… ({sw.Elapsed.TotalSeconds:0} s)");
                    long len = LogLength(manifest.LogPath);
                    if (len != lastLen) { lastLen = len; idle.Restart(); if (len > logStart) started = true; }

                    // RVTOLE writes its result as the last step; the script then saves and quits.
                    if (grace == null && File.Exists(manifest.ResultPath)) grace = Stopwatch.StartNew();
                    if (grace != null && grace.Elapsed > TimeSpan.FromSeconds(60))
                    {
                        Logger.Warn("AutoCAD did not close after the OLE pass; terminating it.");
                        TryKill(p);
                        break;
                    }
                    if (grace == null && !started && sw.Elapsed > StartupLimit)
                    {
                        stuck = $"o AutoCAD não chegou a executar o comando em {StartupLimit.TotalMinutes:0} min (provavelmente parado numa janela de login, licença ou recuperação de desenho)";
                        TryKill(p);
                        break;
                    }
                    if (grace == null && started && idle.Elapsed > IdleLimit)
                    {
                        stuck = $"o AutoCAD ficou {IdleLimit.TotalMinutes:0} min sem avançar";
                        TryKill(p);
                        break;
                    }
                    if (token.IsCancellationRequested || sw.Elapsed > _timeout)
                    {
                        stuck = token.IsCancellationRequested ? "cancelado" : $"tempo limite de {_timeout.TotalMinutes:0} min";
                        TryKill(p);
                        break;
                    }
                }
                Logger.Info($"AutoCAD (OLE pass) finished after {sw.Elapsed.TotalSeconds:0.0} s" + (stuck != null ? " – " + stuck : "") + ".");
            }

            if (File.Exists(manifest.ResultPath))
            {
                try
                {
                    var r = ContractJson.Read<MergeResult>(manifest.ResultPath);
                    if (r.Success && !File.Exists(manifest.OutputDwgPath))
                    {
                        r.Success = false;
                        r.Errors.Add("O AutoCAD informou sucesso, mas não gravou o desenho.");
                    }
                    return r;
                }
                catch (Exception ex) { Logger.Error("Unreadable OLE result file.", ex); }
            }

            var failed = new MergeResult { Success = false };
            failed.Errors.Add(stuck != null ? "Etapa OLE interrompida: " + stuck + "."
                                            : "O AutoCAD não concluiu a etapa OLE (pode ter mostrado uma janela ou não ter iniciado).");
            return failed;
        }

        /// <summary>Until the engine writes its first log line (AutoCAD started and ran the script).</summary>
        public TimeSpan StartupLimit { get; set; } = TimeSpan.FromMinutes(4);
        /// <summary>No new log line for this long while embedding = stuck.</summary>
        public TimeSpan IdleLimit { get; set; } = TimeSpan.FromMinutes(3);
        /// <summary>Status text for a progress display (called from the waiting thread).</summary>
        public Action<string> Report { get; set; }

        private static long LogLength(string path)
        {
            try { return File.Exists(path) ? new FileInfo(path).Length : 0; } catch { return 0; }
        }

        /// <summary>
        /// The user's own AutoCAD profile is used, so every system variable changed here is put
        /// back: FILEDIA (file prompts on the command line while the script runs) and SECURELOAD
        /// (so the engine copied to the job folder loads without the security question).
        /// </summary>
        private static string BuildScript(string engineDll, string manifestPath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("(setq rvt:fd (getvar \"FILEDIA\") rvt:sl (getvar \"SECURELOAD\"))");
            sb.AppendLine("(setvar \"FILEDIA\" 0)");
            sb.AppendLine("(setvar \"SECURELOAD\" 0)");
            sb.AppendLine("NETLOAD");
            sb.AppendLine("\"" + engineDll + "\"");
            sb.AppendLine("(setvar \"SECURELOAD\" rvt:sl)");
            sb.AppendLine("RVTOLE");
            sb.AppendLine(manifestPath);
            sb.AppendLine("_.QSAVE");            // the copy that was opened: clears "modified", so
            sb.AppendLine("(setvar \"FILEDIA\" rvt:fd)");
            sb.AppendLine("_.QUIT");             // QUIT closes AutoCAD without any question
            return sb.ToString();
        }

        internal static void TryKill(Process p)
        {
            try { if (!p.HasExited) p.Kill(); } catch { }
        }
    }
}
