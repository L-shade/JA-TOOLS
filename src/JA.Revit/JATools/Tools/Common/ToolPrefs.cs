using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using RevitDwgUnifier.Contracts;
using RevitDwgUnifier.Infrastructure;

namespace RevitDwgUnifier.Tools.Common
{
    /// <summary>
    /// Remembers the last values chosen in every dialog of both tabs (per Windows user), so a tool
    /// opens with the options used last time. Stored as "Tool.Field" = text, in one file
    /// (%AppData%\RevitDwgUnifier\jatools_prefs.json). The JA Famílias tab had its own file
    /// (%AppData%\JAEsquadrias\prefs.json): its values are brought in once (2.0).
    /// </summary>
    public static class ToolPrefs
    {
        [DataContract]
        private sealed class Store
        {
            [DataMember] public Dictionary<string, string> Values { get; set; } = new Dictionary<string, string>();
        }

        private static Store _store;

        private static string PathName => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RevitDwgUnifier", "jatools_prefs.json");

        private static Store Data
        {
            get
            {
                if (_store != null) return _store;
                try
                {
                    if (File.Exists(PathName)) _store = ContractJson.Read<Store>(PathName);
                }
                catch (Exception ex) { Logger.Warn("JA preferences unreadable: " + ex.Message); }
                if (_store == null) _store = new Store();
                if (_store.Values == null) _store.Values = new Dictionary<string, string>();
                TrazerJaFamilias(_store);
                return _store;
            }
        }

        private const string MarcaMigracao = "__migrado.JAFamilias";

        private static string PathFamilias => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JAEsquadrias", "prefs.json");

        /// <summary>The JA Famílias preferences of before 2.0, once: keys not set here yet are copied.</summary>
        private static void TrazerJaFamilias(Store st)
        {
            if (st.Values.ContainsKey(MarcaMigracao)) return;
            try
            {
                if (File.Exists(PathFamilias))
                {
                    Store velho = ContractJson.Read<Store>(PathFamilias);
                    int n = 0;
                    if (velho?.Values != null)
                        foreach (var kv in velho.Values)
                            if (!st.Values.ContainsKey(kv.Key)) { st.Values[kv.Key] = kv.Value; n++; }
                    Logger.Info($"JA preferences: {n} value(s) brought from the JA Famílias file.");
                }
            }
            catch (Exception ex) { Logger.Warn("JA Famílias preferences not brought in: " + ex.Message); }
            st.Values[MarcaMigracao] = DateTime.Now.ToString("yyyy-MM-dd");
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathName));
                ContractJson.Write(PathName, st);
            }
            catch { }
        }

        public static string Get(string key) => Data.Values.TryGetValue(key, out string v) ? v : null;

        public static void Set(string key, string value)
        {
            Data.Values[key] = value ?? "";
            Gravando?.Add(key);
        }

        /// <summary>While not null, every key written is recorded (Central JA: "gravar tarefa" of tools with their own window).</summary>
        public static HashSet<string> Gravando;

        /// <summary>Re-reads the preferences file (after "Padrões do Escritório" imported one).</summary>
        public static void Reload() => _store = null;

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathName));
                ContractJson.Write(PathName, Data);
            }
            catch (Exception ex) { Logger.Warn("JA preferences not saved: " + ex.Message); }
        }
    }
}
