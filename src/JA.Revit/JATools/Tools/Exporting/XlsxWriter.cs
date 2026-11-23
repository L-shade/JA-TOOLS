using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace RevitDwgUnifier.Tools.Exporting
{
    /// <summary>
    /// Minimal .xlsx (Office Open XML) writer, no external library: one worksheet per table,
    /// title row, coloured header row, optional zebra rows, thin borders, frozen header, column
    /// widths from the content. Values are written as DATA: a number formatted by Revit in the
    /// project's style ("1.250,50 m²", "45%", "R$ 1.200,00") becomes a real number with the unit
    /// kept in the cell's number format (Excel shows the same, and can sum, sort and filter).
    /// Colours are "RRGGBB" (the office brand colours, remembered in the export dialog).
    /// </summary>
    public sealed class XlsxWriter
    {
        public sealed class Table
        {
            public string Name;
            public string Title;
            public List<string[]> Rows = new List<string[]>();
            public int HeaderRows = 1;
        }

        public string HeaderFill = "1F7A5A";
        public string HeaderFont = "FFFFFF";
        public string ZebraFill = "EEF5F1";
        public string TitleFont = "1F7A5A";
        public bool Zebra = true;
        /// <summary>Numbers written as numbers (false = everything as text, as Revit shows it).</summary>
        public bool Numeros = true;
        /// <summary>How the project writes numbers (Revit: Gerenciar › Unidades do projeto).</summary>
        public LeitorNumero Leitor = new LeitorNumero();

        private readonly List<Table> _tables = new List<Table>();
        // number formats used by the cells: code → numFmtId (164+), and (base style, numFmtId) → cellXfs index
        private readonly Dictionary<string, int> _fmts = new Dictionary<string, int>();
        private readonly Dictionary<long, int> _xfs = new Dictionary<long, int>();
        private readonly List<KeyValuePair<int, int>> _xfList = new List<KeyValuePair<int, int>>();

        /// <summary>A last line "Total" with a SUM formula under every column whose cells are all numbers.</summary>
        public bool Totais;

        public void Add(Table t) => _tables.Add(t);

        /// <summary>The table as CSV (";" between columns, UTF-8 with BOM: opens straight in Excel), text as Revit shows it.</summary>
        public static void SaveCsv(Table t, string path)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(t.Title)) sb.AppendLine(Csv(t.Title));
            foreach (string[] r in t.Rows) sb.AppendLine(string.Join(";", r.Select(Csv)));
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        private static string Csv(string v)
        {
            v = v ?? "";
            return v.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        }

        public void Save(string path)
        {
            if (File.Exists(path)) File.Delete(path);
            using (var fs = new FileStream(path, FileMode.CreateNew))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var names = UniqueSheetNames(_tables.Select(t => t.Name));
                // sheets first: they register the number formats the styles part must declare
                var sheets = _tables.Select(Sheet).ToList();
                Write(zip, "[Content_Types].xml", ContentTypes());
                Write(zip, "_rels/.rels",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                    "</Relationships>");
                Write(zip, "xl/workbook.xml", Workbook(names));
                Write(zip, "xl/_rels/workbook.xml.rels", WorkbookRels());
                Write(zip, "xl/styles.xml", Styles());
                for (int i = 0; i < sheets.Count; i++) Write(zip, $"xl/worksheets/sheet{i + 1}.xml", sheets[i]);
            }
        }

        private static void Write(ZipArchive zip, string name, string content)
        {
            var e = zip.CreateEntry(name, CompressionLevel.Optimal);
            using (var w = new StreamWriter(e.Open(), new UTF8Encoding(false))) w.Write(content);
        }

        private string ContentTypes()
        {
            var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
            for (int i = 0; i < _tables.Count; i++)
                sb.Append($"<Override PartName=\"/xl/worksheets/sheet{i + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
            return sb.Append("</Types>").ToString();
        }

        private string Workbook(IList<string> names)
        {
            var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
                "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
            for (int i = 0; i < names.Count; i++) sb.Append($"<sheet name=\"{X(names[i])}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>");
            return sb.Append("</sheets></workbook>").ToString();
        }

        private string WorkbookRels()
        {
            var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            for (int i = 0; i < _tables.Count; i++)
                sb.Append($"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>");
            sb.Append($"<Relationship Id=\"rId{_tables.Count + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
            return sb.Append("</Relationships>").ToString();
        }

        // Style indices: 0 default, 1 title, 2 header, 3 body, 4 zebra body, 5+ body / zebra with a number format
        private string Styles() =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            NumFmts() +
            "<fonts count=\"3\">" +
            "<font><sz val=\"10\"/><name val=\"Arial\"/></font>" +
            $"<font><b/><sz val=\"13\"/><color rgb=\"FF{TitleFont}\"/><name val=\"Arial\"/></font>" +
            $"<font><b/><sz val=\"10\"/><color rgb=\"FF{HeaderFont}\"/><name val=\"Arial\"/></font>" +
            "</fonts>" +
            "<fills count=\"4\">" +
            "<fill><patternFill patternType=\"none\"/></fill>" +
            "<fill><patternFill patternType=\"gray125\"/></fill>" +
            $"<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF{HeaderFill}\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
            $"<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF{ZebraFill}\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
            "</fills>" +
            "<borders count=\"2\">" +
            "<border><left/><right/><top/><bottom/><diagonal/></border>" +
            "<border><left style=\"thin\"><color rgb=\"FFBFBFBF\"/></left><right style=\"thin\"><color rgb=\"FFBFBFBF\"/></right>" +
            "<top style=\"thin\"><color rgb=\"FFBFBFBF\"/></top><bottom style=\"thin\"><color rgb=\"FFBFBFBF\"/></bottom><diagonal/></border>" +
            "</borders>" +
            "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
            $"<cellXfs count=\"{5 + _xfList.Count}\">" +
            "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
            "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>" +
            "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\" wrapText=\"1\"/></xf>" +
            "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyBorder=\"1\"/>" +
            "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"3\" borderId=\"1\" xfId=\"0\" applyFill=\"1\" applyBorder=\"1\"/>" +
            string.Concat(_xfList.Select(x =>
                $"<xf numFmtId=\"{x.Value}\" fontId=\"0\" fillId=\"{(x.Key == 4 ? 3 : 0)}\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyFill=\"1\" applyBorder=\"1\"/>")) +
            "</cellXfs>" +
            "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
            "</styleSheet>";

        private string Sheet(Table t)
        {
            int cols = t.Rows.Count > 0 ? t.Rows.Max(r => r.Length) : 1;
            var widths = new double[cols];
            foreach (var r in t.Rows)
                for (int c = 0; c < r.Length; c++) widths[c] = Math.Max(widths[c], Math.Min(60, (r[c] ?? "").Length * 1.1 + 2));

            int titleRows = string.IsNullOrEmpty(t.Title) ? 0 : 2;
            int freeze = titleRows + t.HeaderRows;
            var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            if (freeze > 0)
                sb.Append($"<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"{freeze}\" topLeftCell=\"A{freeze + 1}\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
            sb.Append("<cols>");
            for (int c = 0; c < cols; c++) sb.Append($"<col min=\"{c + 1}\" max=\"{c + 1}\" width=\"{Math.Max(8, widths[c]).ToString("0.#", CultureInfo.InvariantCulture)}\" customWidth=\"1\"/>");
            sb.Append("</cols><sheetData>");

            int rowNo = 1;
            if (titleRows > 0)
            {
                sb.Append($"<row r=\"{rowNo}\" ht=\"20\" customHeight=\"1\">{Cell(0, rowNo, t.Title, 1)}</row>");
                rowNo += 2;
            }
            for (int i = 0; i < t.Rows.Count; i++, rowNo++)
            {
                int style = i < t.HeaderRows ? 2 : (Zebra && (i - t.HeaderRows) % 2 == 1 ? 4 : 3);
                sb.Append($"<row r=\"{rowNo}\">");
                for (int c = 0; c < cols; c++) sb.Append(Cell(c, rowNo, c < t.Rows[i].Length ? t.Rows[i][c] : "", style, i >= t.HeaderRows));
                sb.Append("</row>");
            }
            // totals: SUM under the columns where every filled cell is a number (same format as the column)
            int corpo = t.Rows.Count - t.HeaderRows;
            if (Totais && Numeros && corpo > 0)
            {
                int first = titleRows + t.HeaderRows + 1, last = first + corpo - 1;
                var cells = new StringBuilder();
                bool algum = false;
                for (int c = 0; c < cols; c++)
                {
                    int nums = 0, textos = 0;
                    string fmt = null;
                    for (int i = t.HeaderRows; i < t.Rows.Count; i++)
                    {
                        string v = c < t.Rows[i].Length ? t.Rows[i][c] : "";
                        if (string.IsNullOrWhiteSpace(v)) continue;
                        if (Leitor.TryParse(v, out _, out string f)) { nums++; fmt = fmt ?? f; }
                        else textos++;
                    }
                    if (nums > 0 && textos == 0)
                    {
                        // percentages are not added up
                        if (fmt != null && fmt.EndsWith("%")) { cells.Append(Cell(c, rowNo, "", 2)); continue; }
                        cells.Append($"<c r=\"{Col(c)}{rowNo}\" s=\"{Estilo(3, fmt)}\"><f>SUM({Col(c)}{first}:{Col(c)}{last})</f></c>");
                        algum = true;
                    }
                    else cells.Append(Cell(c, rowNo, c == 0 ? "Total" : "", 2));
                }
                if (algum) sb.Append($"<row r=\"{rowNo}\">{cells}</row>");
            }
            sb.Append("</sheetData>");
            if (titleRows > 0 && cols > 1) sb.Append($"<mergeCells count=\"1\"><mergeCell ref=\"A1:{Col(cols - 1)}1\"/></mergeCells>");
            return sb.Append("</worksheet>").ToString();
        }

        private string NumFmts()
        {
            if (_fmts.Count == 0) return "";
            var sb = new StringBuilder($"<numFmts count=\"{_fmts.Count}\">");
            foreach (var kv in _fmts.OrderBy(k => k.Value)) sb.Append($"<numFmt numFmtId=\"{kv.Value}\" formatCode=\"{X(kv.Key)}\"/>");
            return sb.Append("</numFmts>").ToString();
        }

        /// <summary>cellXfs index of a body style (3 or 4) with a number format.</summary>
        private int Estilo(int baseStyle, string formato)
        {
            if (string.IsNullOrEmpty(formato) || formato == "General") return baseStyle;
            if (!_fmts.TryGetValue(formato, out int id)) { id = 164 + _fmts.Count; _fmts[formato] = id; }
            long key = (long)baseStyle * 100000 + id;
            if (!_xfs.TryGetValue(key, out int xf))
            {
                _xfList.Add(new KeyValuePair<int, int>(baseStyle, id));
                xf = 4 + _xfList.Count;
                _xfs[key] = xf;
            }
            return xf;
        }

        private string Cell(int col, int row, string text, int style, bool allowNumber = false)
        {
            string r = Col(col) + row;
            text = text ?? "";
            if (allowNumber && Numeros && Leitor.TryParse(text, out double v, out string formato))
                return $"<c r=\"{r}\" s=\"{Estilo(style, formato)}\"><v>{v.ToString("R", CultureInfo.InvariantCulture)}</v></c>";
            if (text.Length == 0) return $"<c r=\"{r}\" s=\"{style}\"/>";
            return $"<c r=\"{r}\" s=\"{style}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{X(text)}</t></is></c>";
        }

        public static string Col(int index)
        {
            string s = "";
            index++;
            while (index > 0) { int m = (index - 1) % 26; s = (char)('A' + m) + s; index = (index - 1) / 26; }
            return s;
        }

        private static string X(string s)
        {
            var clean = new string((s ?? "").Where(ch => ch == '\t' || ch == '\n' || ch == '\r' || ch >= 0x20).ToArray());
            return SecurityElement.Escape(clean);
        }

        private static List<string> UniqueSheetNames(IEnumerable<string> names)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            foreach (string n in names)
            {
                string b = new string((n ?? "Tabela").Select(c => "[]:*?/\\".IndexOf(c) >= 0 ? '-' : c).ToArray()).Trim();
                if (b.Length == 0) b = "Tabela";
                if (b.Length > 31) b = b.Substring(0, 31);
                string name = b;
                for (int i = 2; used.Contains(name); i++)
                {
                    string suffix = " (" + i + ")";
                    name = (b.Length + suffix.Length > 31 ? b.Substring(0, 31 - suffix.Length) : b) + suffix;
                }
                used.Add(name);
                result.Add(name);
            }
            return result;
        }
    }

    /// <summary>
    /// Reads a number as Revit formats it in the project ("1.250,5", "12,50 m²", "−3", "45%",
    /// "R$ 1.200,00"), with the project's decimal and grouping symbols, and gives the value plus
    /// an Excel number format that keeps the look (decimals, grouping, unit). Text that is not a
    /// plain number stays text: codes with letters ("J01", "P2"), leading zeros ("007"), feet and
    /// inches, ranges, dates.
    /// </summary>
    public sealed class LeitorNumero
    {
        /// <summary>SeparadorDecimal symbol of the project (',' in Brazil).</summary>
        public char SeparadorDecimal = ',';
        /// <summary>Digit grouping symbol of the project ("." in Brazil; "" = none).</summary>
        public string Agrupamento = ".";

        private static readonly string[] Moedas = { "R$", "US$", "U$", "$", "€", "£" };
        /// <summary>Units that may follow a number (anything else, e.g. "101A", keeps the cell as text).</summary>
        private static readonly HashSet<string> Unidades = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "mm", "cm", "dm", "m", "km", "m²", "m2", "m³", "m3", "cm²", "cm2", "mm²", "ha", "%", "°", "º",
            "kg", "kgf", "kN", "MPa", "kPa", "Pa", "bar", "mca", "L", "ml", "un", "un.", "unid", "unid.", "pç", "pç.", "pçs",
            "W", "kW", "kWh", "kVA", "VA", "W/m²K", "kg/m²", "kg/m³", "kg/m", "m/s", "l/s", "m³/h", "lux", "lx", "dB", "pav", "vagas", "ft", "ft²", "ft2", "ft³", "in", "SF"
        };
        private Regex _re;
        private char _dec;
        private string _grp;

        private Regex Re()
        {
            if (_re != null && _dec == SeparadorDecimal && _grp == Agrupamento) return _re;
            _dec = SeparadorDecimal; _grp = Agrupamento ?? "";
            string d = Regex.Escape(SeparadorDecimal.ToString());
            string g = _grp.Length == 0 ? null : (_grp == " " ? "[ \\u00A0\\u202F]" : Regex.Escape(_grp));
            string inteiro = g == null ? @"\d+" : @"(?:\d{1,3}(?:" + g + @"\d{3})+|\d+)";
            // sign, integer (grouped or not), decimals, optional unit (letters, ², ³, °, %, /, ., trailing 2/3)
            _re = new Regex(@"^(?<s>[-+\u2212]?)\s*(?<i>" + inteiro + @")(?:" + d + @"(?<f>\d+))?(?<sp>\s*)(?<u>[A-Za-zµÀ-ÿ°%²³/\.]{1,8}[23]?)?$",
                            RegexOptions.CultureInvariant);
            return _re;
        }

        public bool TryParse(string text, out double value, out string formato)
        {
            value = 0; formato = null;
            string t = (text ?? "").Trim();
            if (t.Length == 0 || t.Length > 40) return false;
            string moeda = null;
            foreach (string m in Moedas)
                if (t.StartsWith(m, StringComparison.Ordinal)) { moeda = m; t = t.Substring(m.Length).TrimStart(); break; }
            Match mt = Re().Match(t);
            if (!mt.Success) return false;
            string ip = mt.Groups["i"].Value, fp = mt.Groups["f"].Value, unit = mt.Groups["u"].Value;
            bool agrupado = _grp.Length > 0 && !Regex.IsMatch(ip, @"^\d+$");
            string digits = Regex.Replace(ip, @"\D", "");
            // codes with leading zeros stay text ("007", "01"); "0", "0,5" are numbers
            if (digits.Length > 1 && digits[0] == '0' && !agrupado && fp.Length == 0) return false;
            if (unit.Length > 0 && moeda != null) return false;
            if (unit.Length > 0 && !Unidades.Contains(unit)) return false;
            if (!double.TryParse(digits + (fp.Length > 0 ? "." + fp : ""), NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return false;
            if (mt.Groups["s"].Value.Length > 0 && mt.Groups["s"].Value != "+") value = -value;

            string num = (agrupado ? "#,##0" : "0") + (fp.Length > 0 ? "." + new string('0', Math.Min(fp.Length, 10)) : "");
            if (unit == "%")
            {
                value /= 100;
                formato = num + "%";
            }
            else if (moeda != null) formato = "\"" + moeda + " \"" + num;
            else if (unit.Length > 0) formato = num + "\"" + mt.Groups["sp"].Value + unit + "\"";
            else formato = num;
            if (formato == "0") formato = null;     // plain integers: the General format already shows them
            return true;
        }
    }
}
