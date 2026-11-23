using System;
using System.IO;
using System.Text;
using Autodesk.AutoCAD.EditorInput;
using RevitDwgUnifier.Contracts;

namespace RevitDwgUnifier.AcadMerge
{
    /// <summary>Log to file + Core Console stdout, and collect warnings into the MergeResult.</summary>
    public sealed class MergeLog
    {
        private readonly string _path;
        private readonly Editor _ed;
        private readonly MergeResult _result;

        public MergeLog(string path, Editor ed, MergeResult result)
        {
            _path = path;
            _ed = ed;
            _result = result;
        }

        public void Info(string msg) => Write("INFO", msg);

        public void Warn(string msg)
        {
            Write("WARN", msg);
            _result.Warnings.Add(msg);
        }

        public void Error(string msg, Exception ex = null)
        {
            Write("ERROR", msg + (ex != null ? Environment.NewLine + ex : ""));
            _result.Errors.Add(msg + (ex != null ? ": " + ex.Message : ""));
        }

        public void Stat(string msg)
        {
            Write("STAT", msg);
            _result.Statistics.Add(msg);
        }

        private void Write(string level, string msg)
        {
            string line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {msg}";
            try { if (!string.IsNullOrEmpty(_path)) File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8); }
            catch { }
            try { _ed?.WriteMessage("\n[RVTUNIFY] " + line); } catch { }
        }
    }
}
