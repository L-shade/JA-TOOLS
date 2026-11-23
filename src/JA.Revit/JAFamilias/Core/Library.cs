using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JAEsquadrias.Infrastructure;

namespace JAEsquadrias.Core
{
    public sealed class LibraryItem
    {
        public string Name;
        public string Path;
        public EsquadriaConfig Config;
    }

    /// <summary>
    /// "Biblioteca": the user's own compositions, one JSON file each, in a folder that can be on
    /// the network so the whole office shares it.
    /// </summary>
    public static class Library
    {
        public static List<LibraryItem> List(Settings s)
        {
            var list = new List<LibraryItem>();
            try
            {
                if (!Directory.Exists(s.PastaBiblioteca)) return list;
                foreach (string f in Directory.GetFiles(s.PastaBiblioteca, "*.json").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var cfg = EsquadriaConfig.FromJson(File.ReadAllText(f));
                        if (cfg != null) list.Add(new LibraryItem { Name = System.IO.Path.GetFileNameWithoutExtension(f), Path = f, Config = cfg });
                    }
                    catch (Exception ex) { Logger.Warn("[JA Famílias] Item da biblioteca ilegível " + f + ": " + ex.Message); }
                }
            }
            catch (Exception ex) { Logger.Warn("[JA Famílias] Biblioteca ilegível: " + ex.Message); }
            return list;
        }

        public static string Save(Settings s, string name, EsquadriaConfig cfg)
        {
            string safe = new string((name ?? "").Trim().Select(ch => System.IO.Path.GetInvalidFileNameChars().Contains(ch) ? '-' : ch).ToArray());
            if (safe.Length == 0) safe = "Esquadria";
            string path = System.IO.Path.Combine(s.PastaBiblioteca, safe + ".json");
            Json.Write(path, cfg);
            return path;
        }

        public static void Delete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) { Logger.Warn("[JA Famílias] Não foi possível excluir " + path + ": " + ex.Message); }
        }
    }
}

namespace JAEsquadrias.Core
{
    public sealed class PecaLibraryItem
    {
        public string Name;
        public string Path;
        public IPecaConfig Config;
    }

    /// <summary>Library of bancadas, nichos and chapins: one subfolder per kind.</summary>
    public static class PecaLibrary
    {
        public static string Folder(Settings s, TipoPeca t) =>
            System.IO.Path.Combine(s.PastaBiblioteca, PecaUtil.Plural(t));

        public static List<PecaLibraryItem> List(Settings s, TipoPeca t)
        {
            var list = new List<PecaLibraryItem>();
            try
            {
                string dir = Folder(s, t);
                if (!Directory.Exists(dir)) return list;
                foreach (string f in Directory.GetFiles(dir, "*.json").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    var cfg = PecaUtil.FromJson(t, File.ReadAllText(f));
                    if (cfg != null) list.Add(new PecaLibraryItem { Name = System.IO.Path.GetFileNameWithoutExtension(f), Path = f, Config = cfg });
                }
            }
            catch (Exception ex) { Logger.Warn("[JA Famílias] Biblioteca ilegível: " + ex.Message); }
            return list;
        }

        public static string Save(Settings s, string name, IPecaConfig cfg)
        {
            string safe = new string((name ?? "").Trim().Select(ch => System.IO.Path.GetInvalidFileNameChars().Contains(ch) ? '-' : ch).ToArray());
            if (safe.Length == 0) safe = PecaUtil.NomeTipo(cfg.Tipo);
            string dir = Folder(s, cfg.Tipo);
            Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, safe + ".json");
            File.WriteAllText(path, cfg.ToJson(), new System.Text.UTF8Encoding(false));
            return path;
        }
    }
}
