using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace RevitDwgUnifier.Merge
{
    public sealed class CoreConsoleInstall
    {
        public string ExePath { get; set; }
        public int Year { get; set; }
        /// <summary>Folder name under MergeEngine\ that matches this AutoCAD's .NET runtime:
        /// 2021-2024 = .NET Framework 4.8, 2025-2026 = .NET 8, 2027+ = .NET 10.</summary>
        public string EngineFlavor => Year >= 2027 ? "net10.0-windows" : Year >= 2025 ? "net8.0-windows" : "net48";
        /// <summary>AutoCAD API release: 2021-2024 = R24, 2025-2026 = R25, 2027 = R26, 2028 = R27...</summary>
        public int Release => Year <= 2024 ? 24 : Year <= 2026 ? 25 : 24 + (Year - 2025);
        public override string ToString() => $"AutoCAD {Year} ({ExePath})";
    }

    /// <summary>
    /// Finds accoreconsole.exe. AutoCAD verticals (Civil 3D, Map 3D, Plant 3D, MEP...) install
    /// into the same "AutoCAD 20xx" folder and ship the same Core Console. AutoCAD LT has no
    /// Core Console and no .NET API, so it cannot host the merge engine.
    /// </summary>
    public static class CoreConsoleLocator
    {
        public const int MinimumYear = 2021;

        public static CoreConsoleInstall Find(string explicitPath)
        {
            if (!string.IsNullOrWhiteSpace(explicitPath))
            {
                if (!File.Exists(explicitPath)) return null;
                return new CoreConsoleInstall { ExePath = explicitPath, Year = GuessYear(explicitPath) };
            }
            return FindAll().OrderByDescending(i => i.Year).FirstOrDefault();
        }

        public static IEnumerable<CoreConsoleInstall> FindAll()
        {
            var roots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetEnvironmentVariable("ProgramW6432")
            }.Where(r => !string.IsNullOrEmpty(r)).Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (string root in roots)
            {
                string autodesk = Path.Combine(root, "Autodesk");
                if (!Directory.Exists(autodesk)) continue;
                foreach (string dir in Directory.GetDirectories(autodesk, "AutoCAD 20*"))
                {
                    string exe = Path.Combine(dir, "accoreconsole.exe");
                    int year = GuessYear(dir);
                    if (File.Exists(exe) && year >= MinimumYear)
                        yield return new CoreConsoleInstall { ExePath = exe, Year = year };
                }
            }
        }

        private static int GuessYear(string path)
        {
            var m = Regex.Match(path, @"20\d\d");
            return m.Success ? int.Parse(m.Value) : 2025;
        }

        private static string EngineRoot =>
            Path.Combine(RevitDwgUnifier.Infrastructure.AddinLocation.Folder, "MergeEngine");

        /// <summary>
        /// Engines to try, best first. Folders are named "R&lt;release&gt;-&lt;tfm&gt;" (R24-net48, R25-net8.0-windows,
        /// R25-net10.0-windows, R26-net10.0-windows); older installs used just the TFM.
        ///   1. same API release, older runtime first (a .NET 8 engine also runs on a release that a
        ///      service pack moved to .NET 10, not the other way round);
        ///   2. the folder of this AutoCAD's runtime family (old layout);
        ///   3. any other engine, newest runtime first (last resort).
        /// </summary>
        public static List<string> GetEngineCandidates(CoreConsoleInstall install)
        {
            var result = new List<string>();
            string root = EngineRoot;
            if (!Directory.Exists(root)) return result;
            var all = Directory.GetDirectories(root)
                .Select(d => new { Dir = Path.GetFileName(d), Dll = Path.Combine(d, "RevitDwgUnifier.AcadMerge.dll") })
                .Where(x => File.Exists(x.Dll)).ToList();
            string prefix = "R" + install.Release + "-";
            foreach (var x in all.Where(x => x.Dir.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                                 .OrderBy(x => RuntimeRank(x.Dir.Substring(prefix.Length))))
                result.Add(x.Dll);
            foreach (var x in all.Where(x => string.Equals(x.Dir, install.EngineFlavor, StringComparison.OrdinalIgnoreCase)))
                if (!result.Contains(x.Dll)) result.Add(x.Dll);
            foreach (var x in all.OrderByDescending(x => RuntimeRank(Flavor(x.Dir))).ThenByDescending(x => x.Dir))
                if (!result.Contains(x.Dll)) result.Add(x.Dll);
            return result;
        }

        private static string Flavor(string folder)
        {
            int dash = folder.IndexOf('-');
            return folder.StartsWith("R", StringComparison.OrdinalIgnoreCase) && dash > 0 ? folder.Substring(dash + 1) : folder;
        }

        /// <summary>"net48" -> 4.8, "net8.0-windows" -> 8, "net10.0-windows" -> 10.</summary>
        private static double RuntimeRank(string flavor)
        {
            var m = Regex.Match(flavor ?? "", @"^net(\d+)(?:\.(\d+))?");
            if (!m.Success) return 0;
            string major = m.Groups[1].Value;
            if (major.Length == 2 && !m.Groups[2].Success && major[0] == '4') return 4 + (major[1] - '0') / 10.0; // net48
            return double.Parse(major, System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
