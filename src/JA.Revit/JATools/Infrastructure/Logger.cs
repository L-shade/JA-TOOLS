using System;
using System.IO;
using System.Text;
using System.Threading;

namespace RevitDwgUnifier.Infrastructure
{
    public enum LogLevel { Debug, Info, Warning, Error }

    /// <summary>
    /// Minimal, dependency-free, thread-safe file logger.
    ///
    /// Why not just throw? An unhandled exception inside IExternalCommand.Execute is caught by
    /// Revit, but it aborts the whole command (and with it every remaining sheet). Everything in
    /// this add-in therefore logs and continues per item, and only the final summary tells the
    /// user what failed. Logs go to:
    ///   %LOCALAPPDATA%\RevitDwgUnifier\Logs\RevitDwgUnifier_yyyyMMdd.log   (rolling, per day)
    ///   plus an optional per-job copy inside the job work folder (attached to the manifest).
    /// Lines are buffered and written together (every 1.5 s, every 64 lines, at once on an error,
    /// at the end of each tool and when Revit closes) instead of opening the file for every line:
    /// the content of the log is the same.
    /// </summary>
    public static class Logger
    {
        private static readonly object Sync = new object();
        private static string _dailyLogPath;
        private static string _jobLogPath;
        private static readonly StringBuilder PendingDaily = new StringBuilder();
        private static readonly StringBuilder PendingJob = new StringBuilder();
        private static int _pendingLines;
        private static Timer _timer;

        public static string DailyLogPath => _dailyLogPath;

        public static void Initialize()
        {
            if (!string.IsNullOrEmpty(_dailyLogPath)) return;   // both tabs start it: once
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "RevitDwgUnifier", "Logs");
                Directory.CreateDirectory(dir);
                _dailyLogPath = Path.Combine(dir, "RevitDwgUnifier_" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                PruneOldLogs(dir, 30);
                AppDomain.CurrentDomain.ProcessExit += (s, e) => Flush();
                AppDomain.CurrentDomain.DomainUnload += (s, e) => Flush();
            }
            catch
            {
                // Logging must never be the reason the add-in fails to load.
                _dailyLogPath = null;
            }
        }

        public static void BeginJob(string jobLogPath)
        {
            lock (Sync) { FlushLocked(); _jobLogPath = jobLogPath; }
            Info("==== Job started: " + jobLogPath);
        }

        public static void EndJob()
        {
            Info("==== Job finished");
            lock (Sync) { FlushLocked(); _jobLogPath = null; }
        }

        public static void Debug(string msg) => Write(LogLevel.Debug, msg, null);
        public static void Info(string msg) => Write(LogLevel.Info, msg, null);
        public static void Warn(string msg) => Write(LogLevel.Warning, msg, null);
        public static void Error(string msg, Exception ex = null) => Write(LogLevel.Error, msg, ex);

        private static void Write(LogLevel level, string msg, Exception ex)
        {
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
              .Append(" [").Append(level.ToString().ToUpperInvariant()).Append("] ")
              .Append(msg);
            if (ex != null) sb.AppendLine().Append(ex);
            sb.AppendLine();
            string line = sb.ToString();

            lock (Sync)
            {
                if (!string.IsNullOrEmpty(_dailyLogPath)) PendingDaily.Append(line);
                if (!string.IsNullOrEmpty(_jobLogPath)) PendingJob.Append(line);
                _pendingLines++;
                if (level == LogLevel.Error || _pendingLines >= 64) FlushLocked();
                else if (_timer == null)
                {
                    try { _timer = new Timer(_ => Flush(), null, 1500, 1500); } catch { FlushLocked(); }
                }
            }
            System.Diagnostics.Debug.Write(line);
        }

        /// <summary>Writes the buffered lines now (end of a tool, before reading the log...).</summary>
        public static void Flush()
        {
            lock (Sync) FlushLocked();
        }

        private static void FlushLocked()
        {
            if (PendingDaily.Length > 0) { TryAppend(_dailyLogPath, PendingDaily.ToString()); PendingDaily.Clear(); }
            if (PendingJob.Length > 0) { TryAppend(_jobLogPath, PendingJob.ToString()); PendingJob.Clear(); }
            _pendingLines = 0;
        }

        private static void TryAppend(string path, string text)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { File.AppendAllText(path, text, Encoding.UTF8); }
            catch { /* disk full / locked log: swallow, never crash Revit because of a log line */ }
        }

        private static void PruneOldLogs(string dir, int keepDays)
        {
            try
            {
                foreach (var f in Directory.GetFiles(dir, "RevitDwgUnifier_*.log"))
                    if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-keepDays)) File.Delete(f);
            }
            catch { }
        }
    }
}
