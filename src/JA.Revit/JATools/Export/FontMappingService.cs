using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using RevitDwgUnifier.Core;
using RevitDwgUnifier.Infrastructure;

namespace RevitDwgUnifier.Export
{
    /// <summary>
    /// PITFALL #3 - Text formatting &amp; custom font distortion (Revit half).
    ///
    /// Revit text types reference Windows TrueType fonts by family name. When the DWG is opened
    /// on a machine (or in a CAD platform) that lacks that font, AutoCAD substitutes a font with
    /// different metrics: text overflows its box, wraps differently, or shows "?" for characters
    /// (accents, Ø, ², ±) that the substitute (often an SHX) does not contain.
    ///
    /// Fix applied here, BEFORE export, through Revit's own export font table
    /// (DWGExportOptions.GetExportFontTable / SetExportFontTable):
    ///   1. explicit user map "Revit font=CAD font" (e.g. "Revit Custom Sans=Arial");
    ///   2. every font used by a text/dimension/tag type that is NOT installed on this PC
    ///      (therefore also unlikely on the CAD side) is mapped to the fallback TTF (Arial by default).
    /// Text treatment is set to Exact (see DwgOptionsFactory) so line breaks are kept as authored
    /// in Revit instead of being re-flowed by AutoCAD's MText word wrap.
    /// The merge engine adds a second safety net at the DWG level (TextStyleRepair).
    /// </summary>
    public static class FontMappingService
    {
        public static void Apply(Document doc, DWGExportOptions options, ExportSettings settings)
        {
            try
            {
                ExportFontTable table = options.GetExportFontTable();
                int mapped = 0;

                // 1) Explicit user mapping wins.
                foreach (var pair in ParseUserMap(settings.FontMap))
                    if (SetMapping(table, pair.Key, pair.Value)) mapped++;

                // 2) Auto-map fonts that are missing on this machine.
                if (settings.MapMissingFonts)
                {
                    var installed = GetInstalledFontFamilies();
                    foreach (string font in CollectUsedFonts(doc))
                    {
                        if (IsInstalled(font, installed)) continue;
                        if (ParseUserMap(settings.FontMap).ContainsKey(font)) continue;
                        if (SetMapping(table, font, settings.FallbackFont))
                        {
                            mapped++;
                            Logger.Warn($"Font '{font}' is not installed; mapped to '{settings.FallbackFont}' for DWG export.");
                        }
                    }
                }

                options.SetExportFontTable(table);
                Logger.Info($"Export font table: {mapped} mapping(s) applied.");
            }
            catch (Exception ex)
            {
                // A font-table failure must not stop the export; the merge engine repairs styles too.
                Logger.Warn("Font mapping skipped: " + ex.Message);
            }
        }

        private static bool SetMapping(ExportFontTable table, string revitFont, string cadFont)
        {
            if (string.IsNullOrWhiteSpace(revitFont) || string.IsNullOrWhiteSpace(cadFont)) return false;
            var key = new ExportFontKey(revitFont.Trim());
            var info = new ExportFontInfo(cadFont.Trim());
            if (!table.ContainsKey(key))
            {
                table.Add(key, info);
                return true;
            }

            // Key already mapped by the export setup: replace it. The member name differs between
            // Revit releases, so it is resolved by reflection (compiles against every version).
            var type = table.GetType();
            var setter = type.GetMethod("SetExportFontInfo", new[] { typeof(ExportFontKey), typeof(ExportFontInfo) })
                      ?? type.GetMethod("SetFontInfo", new[] { typeof(ExportFontKey), typeof(ExportFontInfo) });
            if (setter != null)
            {
                setter.Invoke(table, new object[] { key, info });
                return true;
            }
            var remove = type.GetMethod("Remove", new[] { typeof(ExportFontKey) });
            if (remove != null)
            {
                remove.Invoke(table, new object[] { key });
                table.Add(key, info);
                return true;
            }
            Logger.Warn($"Font '{revitFont}' is already mapped by the export setup; mapping kept.");
            return false;
        }

        private static Dictionary<string, string> ParseUserMap(IEnumerable<string> lines)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (lines == null) return map;
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0 || eq == line.Length - 1) continue;
                map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return map;
        }

        /// <summary>Font family names referenced by text-bearing element types in the project.</summary>
        public static HashSet<string> CollectUsedFonts(Document doc)
        {
            var fonts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Harvest(IEnumerable<Element> types)
            {
                foreach (var t in types)
                {
                    string f = t.get_Parameter(BuiltInParameter.TEXT_FONT)?.AsString();
                    if (!string.IsNullOrWhiteSpace(f)) fonts.Add(f.Trim());
                }
            }

            Harvest(new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)));
            Harvest(new FilteredElementCollector(doc).OfClass(typeof(DimensionType)));
            // Tags / labels in annotation families expose TEXT_FONT on their family type
            // only inside the family document, so they are covered by the merge-side repair.
            return fonts;
        }

        private static readonly string[] StyleWords =
        {
            "Thin", "Hairline", "ExtraLight", "UltraLight", "SemiLight", "Light", "Regular", "Book", "Normal",
            "Medium", "SemiBold", "DemiBold", "Bold", "ExtraBold", "UltraBold", "Black", "Heavy",
            "Narrow", "Condensed", "SemiCondensed", "Compressed", "Expanded", "Italic", "Oblique"
        };

        /// <summary>"Bahnschrift SemiLight" is installed when "Bahnschrift" is (variable fonts expose
        /// weights/widths as styles, Revit stores them as separate family names).</summary>
        private static bool IsInstalled(string font, HashSet<string> installed)
        {
            string name = font.Trim();
            while (true)
            {
                if (installed.Contains(name)) return true;
                int space = name.LastIndexOf(' ');
                if (space <= 0) return false;
                string last = name.Substring(space + 1);
                if (!StyleWords.Any(w => w.Equals(last, StringComparison.OrdinalIgnoreCase))) return false;
                name = name.Substring(0, space).TrimEnd();
            }
        }

        private static HashSet<string> GetInstalledFontFamilies()
        {
            // WPF groups fonts by typographic family, so "Arial Narrow" is a typeface of "Arial" and
            // "Bahnschrift SemiLight" a typeface of "Bahnschrift". Revit stores the GDI name, so both
            // "family" and "family + face" are registered, plus the names in the Windows font registry.
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var family in System.Windows.Media.Fonts.SystemFontFamilies)
                {
                    var familyNames = new List<string> { family.Source };
                    familyNames.AddRange(family.FamilyNames.Values);
                    foreach (string fam in familyNames)
                    {
                        set.Add(fam);
                        foreach (var typeface in family.FamilyTypefaces)
                            foreach (string face in typeface.AdjustedFaceNames.Values)
                                if (!string.IsNullOrWhiteSpace(face) && face != "Regular") set.Add(fam + " " + face);
                    }
                }
            }
            catch (Exception ex) { Logger.Debug("WPF font enumeration failed: " + ex.Message); }

            foreach (var hive in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
            {
                try
                {
                    using (var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts"))
                    {
                        if (key == null) continue;
                        foreach (string valueName in key.GetValueNames())
                        {
                            // "Arial Narrow Bold (TrueType)", "Cambria & Cambria Math (TrueType)"
                            string n = valueName;
                            int paren = n.LastIndexOf('(');
                            if (paren > 0) n = n.Substring(0, paren);
                            foreach (string part in n.Split('&'))
                            {
                                string name = part.Trim();
                                if (name.Length == 0) continue;
                                set.Add(name);
                                foreach (string style in new[] { " Bold Italic", " Bold", " Italic" })
                                    if (name.EndsWith(style, StringComparison.OrdinalIgnoreCase))
                                        set.Add(name.Substring(0, name.Length - style.Length));
                            }
                        }
                    }
                }
                catch (Exception ex) { Logger.Debug("Font registry read failed: " + ex.Message); }
            }
            return set;
        }
    }
}
