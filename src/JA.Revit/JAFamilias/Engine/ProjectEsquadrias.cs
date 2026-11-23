using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using JAEsquadrias.Core;

namespace JAEsquadrias.Engine
{
    /// <summary>A JA esquadria type found in the project.</summary>
    public sealed class EsquadriaNoProjeto
    {
        public FamilySymbol Symbol;
        public EsquadriaConfig Config;
        public int Instancias;
        public string Codigo => Config?.Codigo ?? Symbol?.Name;
    }

    /// <summary>Reads the JA esquadrias (types carrying JA_Config) of a project.</summary>
    public static class ProjectEsquadrias
    {
        public static List<EsquadriaNoProjeto> All(Document doc)
        {
            var result = new List<EsquadriaNoProjeto>();
            var filter = new LogicalOrFilter(new ElementCategoryFilter(BuiltInCategory.OST_Windows), new ElementCategoryFilter(BuiltInCategory.OST_Doors));
            var symbols = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).WherePasses(filter).Cast<FamilySymbol>();
            foreach (var s in symbols)
            {
                string json = SharedParams.GetString(s, SharedParams.Config);
                if (string.IsNullOrWhiteSpace(json)) continue;
                var cfg = EsquadriaConfig.FromJson(json);
                if (cfg == null) continue;
                result.Add(new EsquadriaNoProjeto { Symbol = s, Config = cfg });
            }
            if (result.Count > 0)
            {
                var counts = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).WherePasses(filter).Cast<FamilyInstance>()
                    .GroupBy(i => i.Symbol?.Id?.Value ?? -1).ToDictionary(g => g.Key, g => g.Count());
                foreach (var e in result)
                    e.Instancias = counts.TryGetValue(e.Symbol.Id.Value, out int n) ? n : 0;
            }
            return result.OrderBy(e => e.Config.Tipo).ThenBy(e => e.Codigo, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Next free code: J01, J02... / P01, P02...</summary>
        public static string NextCode(IEnumerable<string> used, TipoEsquadria tipo)
        {
            string prefix = tipo == TipoEsquadria.Porta ? "P" : "J";
            int max = 0;
            foreach (string c in used ?? Enumerable.Empty<string>())
            {
                var m = Regex.Match(c ?? "", "^" + prefix + @"(\d+)$", RegexOptions.IgnoreCase);
                if (m.Success && int.TryParse(m.Groups[1].Value, out int n)) max = Math.Max(max, n);
            }
            return prefix + (max + 1).ToString("00");
        }

        /// <summary>The JA configuration of a family instance (or of its type), or null.</summary>
        public static EsquadriaConfig ConfigOf(Element e, out FamilySymbol symbol)
        {
            symbol = (e as FamilyInstance)?.Symbol ?? e as FamilySymbol;
            // only windows and doors (bancadas, nichos and chapins carry JA_Config too)
            long cat = symbol?.Category?.Id?.Value ?? 0;
            if (cat != (long)BuiltInCategory.OST_Windows && cat != (long)BuiltInCategory.OST_Doors) { symbol = null; return null; }
            string json = SharedParams.GetString(symbol, SharedParams.Config);
            return string.IsNullOrWhiteSpace(json) ? null : EsquadriaConfig.FromJson(json);
        }

        /// <summary>Thickness (mm) of the wall hosting an instance, or 0.</summary>
        public static double HostThicknessMm(Element e)
        {
            try
            {
                if (e is FamilyInstance fi && fi.Host is Wall w) return w.Width * 304.8;
            }
            catch { }
            return 0;
        }
    }
}
