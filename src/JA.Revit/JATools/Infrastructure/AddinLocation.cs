using System;
using System.IO;
using System.Reflection;

namespace RevitDwgUnifier.Infrastructure
{
    /// <summary>
    /// Where the add-in DLL is. Assembly.Location is empty when a host loads the assembly from a
    /// stream (add-in isolation / dependency contexts in newer Revit releases): then the DLL is
    /// looked up in the add-in folders the installer uses (%AppData% and %ProgramData%), for the
    /// running Revit version first.
    /// </summary>
    public static class AddinLocation
    {
        public const string DllName = "JA.Revit.dll";

        /// <summary>Folder of the DLL under Addins\&lt;year&gt;\ (JA para Revit 2.0: one DLL for both tabs).</summary>
        public const string Pasta = "JA";
        private static string _path;

        /// <summary>Revit version ("2027") set at startup, used by the fallback search.</summary>
        public static string RevitVersion { get; set; }

        public static string DllPath
        {
            get
            {
                if (!string.IsNullOrEmpty(_path)) return _path;
                string p = null;
                try { p = typeof(AddinLocation).Assembly.Location; } catch { }
                if (string.IsNullOrEmpty(p) || !File.Exists(p))
                {
                    try
                    {
                        string cb = typeof(AddinLocation).Assembly.CodeBase;
                        if (!string.IsNullOrEmpty(cb)) p = new Uri(cb).LocalPath;
                    }
                    catch { p = null; }
                }
                if (string.IsNullOrEmpty(p) || !File.Exists(p)) p = Search();
                _path = p ?? "";
                return _path;
            }
        }

        public static string Folder => string.IsNullOrEmpty(DllPath) ? "" : Path.GetDirectoryName(DllPath);

        private static string Search()
        {
            var roots = new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.CommonApplicationData };
            var versions = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrEmpty(RevitVersion)) versions.Add(RevitVersion);
            for (int y = 2035; y >= 2024; y--) if (!versions.Contains(y.ToString())) versions.Add(y.ToString());
            // Revit 2027+ reads all-users add-ins from C:\Program Files\Autodesk\Revit\Addins\<year>
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            foreach (string v in versions)
            {
                foreach (var r in roots)
                {
                    string candidate = Path.Combine(Environment.GetFolderPath(r), "Autodesk", "Revit", "Addins", v, Pasta, DllName);
                    if (File.Exists(candidate)) return candidate;
                }
                string inPf = Path.Combine(pf, "Autodesk", "Revit", "Addins", v, Pasta, DllName);
                if (File.Exists(inPf)) return inPf;
            }
            return null;
        }
    }
}
