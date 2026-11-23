using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace RevitDwgUnifier.Infrastructure
{
    /// <summary>
    /// PITFALL #7 - File naming &amp; path overwrite errors.
    ///
    /// Typical failures this class prevents:
    ///  * Revit's Export() returns false / throws when the target DWG is open in AutoCAD
    ///    (AutoCAD holds an exclusive handle on the .dwg and writes a sibling .dwl/.dwl2 lock file).
    ///  * Same DWG open by a colleague over a network share: the .dwg itself may not be locked
    ///    from our machine's point of view, but the .dwl file says "in use by USER on PC".
    ///  * View/sheet names containing characters that are illegal in file names (":", "/", "?", ...)
    ///    or Windows reserved device names (CON, PRN, AUX, NUL, COM1...).
    ///  * Paths longer than MAX_PATH (260) when deep project folders meet long sheet names.
    ///
    /// Strategy: never write the final file in place during the export. Everything is produced
    /// inside a private work folder, and only the finished master DWG is copied to the final
    /// destination through <see cref="ResolveWritableTarget"/> + <see cref="CopyWithRetry"/>, which
    /// auto-increments the name ("Name_01.dwg") instead of failing when the target is locked.
    /// </summary>
    public static class FileSafety
    {
        private static readonly string[] ReservedNames =
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        public static string SanitizeFileName(string name, int maxLength = 100)
        {
            if (string.IsNullOrWhiteSpace(name)) name = "Export";
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
                sb.Append(invalid.Contains(c) || c == ';' || c == ',' || c == '=' ? '_' : c);

            string result = sb.ToString().Trim().TrimEnd('.', ' ');
            if (result.Length == 0) result = "Export";
            if (ReservedNames.Contains(result.ToUpperInvariant())) result = "_" + result;
            if (result.Length > maxLength) result = result.Substring(0, maxLength).TrimEnd('.', ' ');
            return result;
        }

        /// <summary>True when the file exists and cannot be opened exclusively (open in AutoCAD,
        /// being synced by a cloud client, read-only, ...).</summary>
        public static bool IsFileLocked(string path)
        {
            if (!File.Exists(path)) return false;
            try
            {
                using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                return false;
            }
            catch (IOException) { return true; }               // sharing violation
            catch (UnauthorizedAccessException) { return true; } // read-only file / no ACL
        }

        /// <summary>AutoCAD writes NAME.dwl + NAME.dwl2 next to an open drawing. They survive
        /// a crash, so a .dwl without a locked .dwg is reported as "stale" but still respected:
        /// overwriting a drawing someone may have open on another PC is worse than a new name.</summary>
        public static bool HasAutoCadLockFile(string dwgPath, out string detail)
        {
            detail = null;
            string dwl = Path.ChangeExtension(dwgPath, ".dwl");
            string dwl2 = Path.ChangeExtension(dwgPath, ".dwl2");
            string found = File.Exists(dwl) ? dwl : File.Exists(dwl2) ? dwl2 : null;
            if (found == null) return false;

            try
            {
                // .dwl content: line1 = user name, line2 = machine, line3 = time (ANSI).
                var lines = File.ReadAllLines(found, Encoding.Default);
                string who = lines.Length > 0 ? lines[0] : "?";
                string pc = lines.Length > 1 ? lines[1] : "?";
                detail = string.Format(CultureInfo.InvariantCulture,
                    "AutoCAD lock file {0} (user '{1}' on '{2}', written {3:g})",
                    Path.GetFileName(found), who, pc, File.GetLastWriteTime(found));
            }
            catch (IOException)
            {
                detail = "AutoCAD lock file " + Path.GetFileName(found) + " is held open (drawing is in use)";
            }
            return true;
        }

        /// <summary>
        /// Returns a path that can be written right now. If <paramref name="desiredPath"/> exists
        /// and (overwrite is not allowed OR it is locked OR AutoCAD's .dwl says it is in use),
        /// the name is auto-incremented: Name_01.dwg, Name_02.dwg, ...
        /// </summary>
        public static string ResolveWritableTarget(string desiredPath, bool allowOverwrite)
        {
            string dir = Path.GetDirectoryName(desiredPath);
            if (string.IsNullOrEmpty(dir)) throw new ArgumentException("Output path has no folder: " + desiredPath);
            Directory.CreateDirectory(dir);

            string baseName = SanitizeFileName(Path.GetFileNameWithoutExtension(desiredPath), 150);
            string ext = Path.GetExtension(desiredPath);
            if (string.IsNullOrEmpty(ext)) ext = ".dwg";

            string candidate = Path.Combine(dir, baseName + ext);
            for (int i = 1; i <= 999; i++)
            {
                if (IsUsable(candidate, allowOverwrite)) return EnsurePathLength(candidate);
                candidate = Path.Combine(dir, baseName + "_" + i.ToString("00", CultureInfo.InvariantCulture) + ext);
            }
            throw new IOException("Could not find a free file name for " + desiredPath + " (tried 999 variants).");
        }

        private static bool IsUsable(string path, bool allowOverwrite)
        {
            if (HasAutoCadLockFile(path, out string detail))
            {
                Logger.Warn("Target in use, choosing another name: " + detail);
                return false;
            }
            if (!File.Exists(path)) return true;
            if (!allowOverwrite) return false;
            if (IsFileLocked(path))
            {
                Logger.Warn("Target file is locked (open in AutoCAD / read-only): " + path);
                return false;
            }
            return true;
        }

        private static string EnsurePathLength(string path)
        {
            // .NET Framework 4.8 inside Revit 2024 still trips over MAX_PATH unless long paths are
            // enabled machine-wide; fail early with a readable message instead of a cryptic one.
            if (path.Length >= 248)
                throw new PathTooLongException("The output path is too long (" + path.Length +
                                               " chars). Choose a shorter folder or file name: " + path);
            return path;
        }

        /// <summary>
        /// Copies with retries (cloud-sync clients and antivirus often hold new files for a few
        /// hundred ms). If the target turns out locked between the check and the copy, a new
        /// incremented name is resolved instead of failing. Returns the path actually written.
        /// </summary>
        public static string CopyWithRetry(string source, string desiredTarget, bool allowOverwrite, int attempts = 4)
        {
            string target = ResolveWritableTarget(desiredTarget, allowOverwrite);
            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                try
                {
                    File.Copy(source, target, overwrite: allowOverwrite);
                    return target;
                }
                catch (IOException ex) when (attempt < attempts)
                {
                    Logger.Warn("Copy attempt " + attempt + " failed (" + ex.Message + "), retrying...");
                    Thread.Sleep(400 * attempt);
                    target = ResolveWritableTarget(desiredTarget, allowOverwrite);
                }
                catch (UnauthorizedAccessException) when (attempt < attempts)
                {
                    target = ResolveWritableTarget(desiredTarget, allowOverwrite: false);
                }
            }
            throw new IOException("Unable to write " + desiredTarget);
        }

        /// <summary>
        /// A short, ASCII-only work folder. AutoCAD Core Console scripts are parsed with the
        /// system code page: a user profile like C:\Users\João\AppData\Local\Temp breaks NETLOAD
        /// paths, so non-ASCII temp folders fall back to %ProgramData%.
        /// </summary>
        public static string CreateJobFolder()
        {
            string root = Path.Combine(Path.GetTempPath(), "RvtDwgU");
            if (!IsAscii(root) || root.Length > 80)
                root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RvtDwgU");

            string job = Path.Combine(root, DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" +
                                            Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(job);
            return job;
        }

        public static bool IsAscii(string s) => s.All(c => c < 128);

        public static void TryDeleteDirectory(string dir)
        {
            // AutoCAD helper processes can keep a temp file open for a few seconds after
            // Core Console exits: retry briefly, then leave it for CleanupOldJobFolders.
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                    return;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    if (attempt == 3)
                    {
                        Logger.Info("Work folder still in use, it will be removed on a later export: " + dir);
                        return;
                    }
                    Thread.Sleep(1000 * attempt);
                }
            }
        }

        /// <summary>Removes job folders left behind by earlier runs (locked files, crashes).</summary>
        public static void CleanupOldJobFolders(TimeSpan olderThan)
        {
            foreach (string root in new[]
                     {
                         Path.Combine(Path.GetTempPath(), "RvtDwgU"),
                         Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RvtDwgU")
                     })
            {
                try
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (string dir in Directory.GetDirectories(root))
                    {
                        if (Directory.GetCreationTime(dir) > DateTime.Now - olderThan) continue;
                        try { Directory.Delete(dir, recursive: true); } catch { /* still locked: next time */ }
                    }
                }
                catch { }
            }
        }
    }
}
