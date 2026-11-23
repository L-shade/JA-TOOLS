// ---------------------------------------------------------------------------------
//  InstallationLayers.cs  (linked into BOTH the Revit add-in and the AutoCAD engine)
//
//  Padrão JA – instalações: text notes and tags of the building services (electrical,
//  sewage, gas, water, fire...) go to the office layers "JA - INST_...". A note belongs to
//  a discipline by the NAME OF ITS TEXT TYPE (e.g. "INST_GAS 2,5mm", "Texto Elétrica"); a
//  tag by the name of its family / type or, failing that, by the element it tags (system
//  of the pipe, electrical category...). The same keyword rule runs on both sides.
// ---------------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;

namespace RevitDwgUnifier.Contracts
{
    /// <summary>One office layer of a discipline: name, AutoCAD colour index, linetype and the
    /// words that identify the discipline in a type / family / system name.</summary>
    [DataContract]
    public sealed class InstallationLayer
    {
        [DataMember(Order = 0)] public string Name { get; set; }
        [DataMember(Order = 1)] public int Color { get; set; }
        [DataMember(Order = 2)] public string Linetype { get; set; } = "Continuous";
        /// <summary>Words (accents and case ignored). Up to 3 letters: whole word ("GAS", "HID");
        /// longer: start of a word ("ELET" = elétrica, eletroduto); with a space: the phrase.</summary>
        [DataMember(Order = 3)] public List<string> Keywords { get; set; } = new List<string>();
    }

    /// <summary>A text (note or tag) found in Revit and the layer it goes to.</summary>
    [DataContract]
    public sealed class InstallationText
    {
        [DataMember(Order = 0)] public string Text { get; set; }
        [DataMember(Order = 1)] public string Layer { get; set; }
    }

    /// <summary>A whole DWG layer (the layer of a tag category whose tags are all of one discipline).</summary>
    [DataContract]
    public sealed class InstallationLayerMove
    {
        [DataMember(Order = 0)] public string From { get; set; }
        [DataMember(Order = 1)] public string To { get; set; }
    }

    public static class InstallationRules
    {
        /// <summary>The office table (name, colour ID, linetype). Order matters: the first discipline
        /// whose word is found wins (fire before water: "HIDRANTE" is fire protection).</summary>
        /// <summary>Luminaires (the families and their tags) and lighting notes.</summary>
        public const string IlumLayer = "JA - INST_ILUM";

        public static List<InstallationLayer> Defaults() => new List<InstallationLayer>
        {
            L("JA - INST_SPDA", 21, "Continuous", "SPDA", "PARA RAIO", "PARARAIO", "CAPTOR"),
            L("JA - INST_INC", 230, "DIVIDE2", "INC", "INCEND", "HIDRANTE", "SPRINK", "EXTINT", "PCI", "ALARME DE INC", "FIRE"),
            L("JA - INST_GAS", 200, "Continuous", "GAS", "GLP", "GN"),
            L("JA - INST_ESG", 142, "Continuous", "ESG", "ESGOT", "RALO", "SANIT", "PLUV"),
            L("JA - INST_EXAUSTÃO_AR COND.", 21, "Continuous", "EXAUST", "AR COND", "ARCOND", "HVAC", "CLIMATIZ", "VENTILAC", "DUTO", "SPLIT"),
            L("JA - INST_ESP", 21, "Continuous", "ESP", "DADOS", "LOGICA", "TELEFON", "CFTV", "COMUNICA", "INTERFON", "SEGURANC", "ANTENA"),
            L(IlumLayer, 10, "Continuous", "LUMINAR", "ILUMIN", "LAMPAD", "ARANDELA", "PLAFON", "PENDENTE", "SPOT", "LIGHTING FIXTURE"),
            L("JA - INST_ELET", 10, "Continuous", "ELET", "ELE", "LUMIN", "TOMADA", "INTERRUP", "FORCA", "CIRCUITO"),
            L("JA - INST_HID", 120, "Continuous", "HID", "HIDR", "AGUA", "AF", "AQ", "DOMESTIC", "WATER")
        };

        private static InstallationLayer L(string name, int color, string lt, params string[] words) =>
            new InstallationLayer { Name = name, Color = color, Linetype = lt, Keywords = words.ToList() };

        /// <summary>Upper case, no accents, every non letter/digit becomes a space.</summary>
        public static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string d = s.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(d.Length);
            bool space = true;
            foreach (char c in d)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(c)) { sb.Append(char.ToUpperInvariant(c)); space = false; }
                else if (!space) { sb.Append(' '); space = true; }
            }
            return sb.ToString().Trim();
        }

        /// <summary>The layer of the first discipline whose word appears in one of the names, or null.</summary>
        public static string Classify(IList<InstallationLayer> layers, params string[] names)
        {
            if (layers == null) return null;
            var norms = names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(Normalize).Where(n => n.Length > 0).ToList();
            if (norms.Count == 0) return null;
            foreach (InstallationLayer l in layers)
            {
                if (l?.Keywords == null || string.IsNullOrWhiteSpace(l.Name)) continue;
                foreach (string raw in l.Keywords)
                {
                    string k = Normalize(raw);
                    if (k.Length == 0) continue;
                    foreach (string n in norms)
                        if (Has(n, k)) return l.Name.Trim();
                }
            }
            return null;
        }

        private static bool Has(string norm, string key)
        {
            if (key.IndexOf(' ') >= 0)
                return (" " + norm + " ").IndexOf(" " + key + " ", StringComparison.Ordinal) >= 0 ||
                       (" " + norm).IndexOf(" " + key, StringComparison.Ordinal) >= 0;
            foreach (string w in norm.Split(' '))
            {
                if (key.Length <= 3 ? w == key : w.StartsWith(key, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>Text compared without spaces / line breaks / case (the DWG re-wraps lines).</summary>
        public static string TextKey(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) if (!char.IsWhiteSpace(c)) sb.Append(char.ToUpperInvariant(c));
            return sb.ToString();
        }
    }
}
