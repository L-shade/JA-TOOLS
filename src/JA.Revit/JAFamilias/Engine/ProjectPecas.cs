using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using JAEsquadrias.Core;

namespace JAEsquadrias.Engine
{
    /// <summary>A JA bancada / nicho / chapim type found in the project.</summary>
    public sealed class PecaNoProjeto
    {
        public FamilySymbol Symbol;
        public IPecaConfig Config;
        public int Instancias;
        public string Codigo => Config?.Codigo ?? Symbol?.Name;
    }

    /// <summary>Reads the JA pieces of a project: Generic Model types carrying JA_Familia + JA_Config.</summary>
    public static class ProjectPecas
    {
        /// <summary>Length (mm) of every placed copy of a line-based JA piece (JA_Comprimento).</summary>
        public static List<double> ComprimentosMm(Document doc, FamilySymbol sym) =>
            sym == null ? new List<double>() :
            new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().Where(i => i.Symbol != null && i.Symbol.Id == sym.Id)
                .Select(fi => fi.get_Parameter(SharedParams.Comprimento.Guid)).Where(p => p != null && p.StorageType == StorageType.Double && p.AsDouble() > 0)
                .Select(p => Math.Round(p.AsDouble() * 304.8)).ToList();

        /// <summary>Total length (m) of the copies of a line-based JA piece.</summary>
        public static double Metros(Document doc, FamilySymbol sym) => ComprimentosMm(doc, sym).Sum() / 1000.0;

        public static List<PecaNoProjeto> All(Document doc, TipoPeca? tipo = null)
        {
            var result = new List<PecaNoProjeto>();
            var symbols = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).WherePasses(Categories()).Cast<FamilySymbol>();
            foreach (var s in symbols)
            {
                IPecaConfig cfg = ConfigOfSymbol(s);
                if (cfg == null || (tipo.HasValue && cfg.Tipo != tipo.Value)) continue;
                result.Add(new PecaNoProjeto { Symbol = s, Config = cfg });
            }
            if (result.Count > 0)
            {
                var counts = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).WherePasses(Categories()).Cast<FamilyInstance>()
                    .GroupBy(i => i.Symbol?.Id?.Value ?? -1).ToDictionary(g => g.Key, g => g.Count());
                foreach (var e in result)
                    e.Instancias = counts.TryGetValue(e.Symbol.Id.Value, out int n) ? n : 0;
            }
            return result.OrderBy(e => e.Config.Tipo).ThenBy(e => e.Codigo, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Generic Models (bancadas) and Casework (nichos, chapins; also older pieces generated as Generic Models).</summary>
        private static ElementFilter Categories() =>
            new ElementMulticategoryFilter(new List<BuiltInCategory> { BuiltInCategory.OST_GenericModel, BuiltInCategory.OST_Casework, BuiltInCategory.OST_PipeFitting, BuiltInCategory.OST_StairsRailingBaluster });

        /// <summary>Codes already used by JA pieces of one kind.</summary>
        public static HashSet<string> UsedCodes(Document doc, TipoPeca tipo) =>
            new HashSet<string>(All(doc, tipo).Select(p => p.Codigo), StringComparer.OrdinalIgnoreCase);

        /// <summary>Next free code: BC01, NI01, CH01...</summary>
        public static string NextCode(IEnumerable<string> used, TipoPeca tipo) => NextCode(used, PecaUtil.Prefixo(tipo));

        /// <summary>Next free code with a given prefix (RD01, DG01, BX01...).</summary>
        public static string NextCode(IEnumerable<string> used, string prefix)
        {
            int max = 0;
            foreach (string c in used ?? Enumerable.Empty<string>())
            {
                var m = Regex.Match(c ?? "", "^" + prefix + @"(\d+)", RegexOptions.IgnoreCase);
                if (m.Success && int.TryParse(m.Groups[1].Value, out int n)) max = Math.Max(max, n);
            }
            return prefix + (max + 1).ToString("00");
        }

        /// <summary>The JA piece configuration of an instance (or type), or null.</summary>
        public static IPecaConfig ConfigOf(Element e, out FamilySymbol symbol)
        {
            symbol = (e as FamilyInstance)?.Symbol ?? e as FamilySymbol;
            return ConfigOfSymbol(symbol);
        }

        private static IPecaConfig ConfigOfSymbol(FamilySymbol s)
        {
            if (s == null) return null;
            string kind = SharedParams.GetString(s, SharedParams.Familia);
            if (string.IsNullOrWhiteSpace(kind) || !PecaUtil.TryParseTipo(kind.Trim(), out TipoPeca t)) return null;
            string json = SharedParams.GetString(s, SharedParams.Config);
            return string.IsNullOrWhiteSpace(json) ? null : PecaUtil.FromJson(t, json);
        }

    }
}
