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
    /// Runs the merge in AutoCAD Core Console (accoreconsole.exe): a headless AutoCAD that
    /// starts in ~2-4 s, has the full DWG database engine, and can NETLOAD a .NET assembly.
    ///
    /// Process isolation is deliberate:
    ///  * Revit and AutoCAD cannot share a process (two Autodesk runtimes, conflicting
    ///    native DLLs), so the DWG work cannot happen inside Revit anyway.
    ///  * A crash or hang in the DWG step cannot take Revit (and unsaved work) down with it;
    ///    a hard timeout kills the console and the job reports a clean failure.
    ///  * /isolate gives the console a throw-away user profile in the job folder, so the
    ///    SECURELOAD/TRUSTEDPATHS changes made by the script never leak into the user's AutoCAD.
    /// </summary>
    public sealed class CoreConsoleMergeEngine : IDwgMergeEngine
    {
        private readonly string _explicitExe;
        private readonly TimeSpan _timeout;
        private CoreConsoleInstall _install;

        public CoreConsoleMergeEngine(string explicitExe, TimeSpan timeout)
        {
            _explicitExe = explicitExe;
            _timeout = timeout;
        }

        /// <summary>The AutoCAD found by <see cref="IsAvailable"/> (also used for the OLE pass).</summary>
        public CoreConsoleInstall Install => _install;

        /// <summary>True once a run reported "Unable to load the Modeler DLLs".</summary>
        public bool ModelerUnavailable { get; private set; }

        public string Name => _install != null ? "AutoCAD Core Console " + _install.Year : "AutoCAD Core Console";

        public bool IsAvailable(out string reason)
        {
            _install = CoreConsoleLocator.Find(_explicitExe);
            if (_install == null)
            {
                reason = "O AutoCAD (completo ou vertical, 2021 ou mais novo) não foi encontrado; o AutoCAD LT não serve. " +
                         "Informe o caminho do accoreconsole.exe nas configurações ou use os DWGs individuais deixados na pasta de saída.";
                return false;
            }
            if (CoreConsoleLocator.GetEngineCandidates(_install).Count == 0)
            {
                reason = $"Falta o motor de união para {_install} (pasta MergeEngine vazia). Reinstale o JA Tools.";
                return false;
            }
            reason = null;
            return true;
        }

        public MergeResult Merge(MergeManifest manifest, string manifestPath, CancellationToken token)
            => Run("RVTUNIFY", manifestPath, manifest.ResultPath, token);

        /// <summary>Padrão JA on DWGs exported straight by Revit (DWG por prancha), saved in place.</summary>
        public MergeResult ApplyStandards(StandardsManifest manifest, string manifestPath, CancellationToken token)
            => Run("RVTSTANDARDS", manifestPath, manifest.ResultPath, token);

        /// <summary>
        /// Runs the command with the engine built for this AutoCAD's release; when AutoCAD cannot load
        /// it (e.g. a service pack moved the release to a newer .NET), the next compatible engine is tried.
        /// </summary>
        private MergeResult Run(string command, string manifestPath, string resultPath, CancellationToken token)
        {
            if (_install == null && !IsAvailable(out string why)) throw new InvalidOperationException(why);
            var candidates = CoreConsoleLocator.GetEngineCandidates(_install);
            MergeResult last = null;
            for (int i = 0; i < candidates.Count; i++)
            {
                try { if (File.Exists(resultPath)) File.Delete(resultPath); } catch { }
                Logger.Info($"Motor de união: {candidates[i]}");
                last = RunOnce(command, manifestPath, resultPath, token, candidates[i], out bool loaded);
                if (loaded || token.IsCancellationRequested) return last;
                Logger.Warn($"O AutoCAD {_install.Year} não carregou o motor {candidates[i]}; tentando o próximo.");
            }
            return last ?? new MergeResult { Success = false };
        }

        private MergeResult RunOnce(string command, string manifestPath, string resultPath, CancellationToken token,
                                    string engineSource, out bool engineLoaded)
        {
            engineLoaded = false;

            string workDir = Path.GetDirectoryName(manifestPath);

            // Copy the engine into the (ASCII-only) job folder: avoids non-ASCII paths in the
            // script and never locks the deployed DLL (so the add-in can be updated while a
            // console is still shutting down).
            string engineDir = Path.Combine(workDir, "engine");
            Directory.CreateDirectory(engineDir);
            string engineDll = Path.Combine(engineDir, "RevitDwgUnifier.AcadMerge.dll");
            File.Copy(engineSource, engineDll, true);

            string scriptPath = Path.Combine(workDir, "merge.scr");
            File.WriteAllText(scriptPath, BuildScript(engineDir, engineDll, command, manifestPath), Encoding.ASCII);

            string isolateDir = Path.Combine(workDir, "acad_profile");
            Directory.CreateDirectory(isolateDir);

            var psi = new ProcessStartInfo
            {
                FileName = _install.ExePath,
                Arguments = $"/s \"{scriptPath}\" /isolate RvtDwgUnifier \"{isolateDir}\"",
                // MUST be the AutoCAD install folder: the geometry modeler (ASM, needed for
                // 3DSOLID/REGION/BODY entities) is resolved from the current directory. From any
                // other folder Core Console aborts with "FATAL ERROR: Unable to load the Modeler DLLs".
                WorkingDirectory = Path.GetDirectoryName(_install.ExePath),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // Core Console writes UTF-16LE to stdout.
                StandardOutputEncoding = Encoding.Unicode,
                StandardErrorEncoding = Encoding.Unicode
            };

            var console = new StringBuilder();
            Logger.Info($"Starting {_install}: {psi.Arguments}");
            var sw = Stopwatch.StartNew();

            using (var p = new Process { StartInfo = psi })
            {
                p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (console) console.AppendLine(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (console) console.AppendLine("ERR: " + e.Data); };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                Stopwatch grace = null;
                while (!p.WaitForExit(500))
                {
                    // The engine writes result.json as its very last action. If the console then
                    // lingers (e.g. waiting at a prompt), give it a short grace period and stop it.
                    if (grace == null && File.Exists(resultPath)) grace = Stopwatch.StartNew();
                    if (grace != null && grace.Elapsed > TimeSpan.FromSeconds(20))
                    {
                        Logger.Warn("Core Console did not exit after writing its result; terminating it.");
                        OleEmbedEngine.TryKill(p);
                        break;
                    }
                    if (token.IsCancellationRequested || sw.Elapsed > _timeout)
                    {
                        Logger.Error(token.IsCancellationRequested ? "Merge cancelled by user." :
                                     $"Merge timed out after {_timeout.TotalMinutes:0} min; killing Core Console.");
                        OleEmbedEngine.TryKill(p);
                        break;
                    }
                }
                p.WaitForExit(5000);
                Logger.Info($"Core Console exited (code {SafeExitCode(p)}) after {sw.Elapsed.TotalSeconds:0.0} s.");
            }

            string consoleLog = Path.Combine(workDir, "accoreconsole_output.log");
            string consoleText;
            lock (console) { consoleText = console.ToString(); }
            File.AppendAllText(consoleLog, $"===== run at {DateTime.Now:HH:mm:ss} =====\r\n" + consoleText, Encoding.UTF8);

            // Native "FATAL ERROR" dialogs are auto-answered by Core Console and never reach .NET;
            // recognise the known ones so the user gets a meaningful message.
            if (consoleText.IndexOf("Unable to load the Modeler DLLs", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                ModelerUnavailable = true;
                Logger.Warn("Core Console could not load the 3D modeler (ASM). Steps that evaluate hatches, " +
                            "regions or solids crash in this AutoCAD installation.");
            }

            // the engine logs "[RVTUNIFY] ..." as soon as its command runs; without it NETLOAD failed
            engineLoaded = File.Exists(resultPath) || consoleText.IndexOf("[RVTUNIFY]", StringComparison.Ordinal) >= 0
                           || consoleText.IndexOf("[RVTSTANDARDS]", StringComparison.Ordinal) >= 0;

            if (File.Exists(resultPath))
            {
                try { return ContractJson.Read<MergeResult>(resultPath); }
                catch (Exception ex) { Logger.Error("Unreadable merge result file.", ex); }
            }

            var failed = new MergeResult { Success = false };
            failed.Errors.Add("O motor de união não gerou resultado. Veja " + consoleLog);
            return failed;
        }

        /// <summary>
        /// Script fed to Core Console. Every prompt answer is on its own line (a script line
        /// == pressing Enter). NETLOAD of an untrusted folder would otherwise pop a SECURELOAD
        /// question that silently eats the following script lines.
        /// </summary>
        private static string BuildScript(string engineDir, string engineDll, string command, string manifestPath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("FILEDIA");
            sb.AppendLine("0");
            sb.AppendLine("SECURELOAD");
            sb.AppendLine("0");
            sb.AppendLine("(setvar \"TRUSTEDPATHS\" (strcat (getvar \"TRUSTEDPATHS\") \";" +
                          engineDir.Replace("\\", "\\\\") + "\"))");
            sb.AppendLine("NETLOAD");
            sb.AppendLine("\"" + engineDll + "\"");
            sb.AppendLine(command);
            sb.AppendLine(manifestPath);      // read with AllowSpaces=true
            sb.AppendLine("_.QUIT");          // the startup drawing is untouched; "_Y" answers a
            sb.AppendLine("_Y");              // discard-changes prompt if one ever appears
            return sb.ToString();
        }

        private static string SafeExitCode(Process p)
        {
            try { return p.HasExited ? p.ExitCode.ToString() : "n/a"; } catch { return "n/a"; }
        }
    }
}
