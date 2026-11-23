using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace RevitDwgUnifier.Tools.Publish
{
    /// <summary>One cell as it should look in Revit (already formatted text).</summary>
    public sealed class PlCelula
    {
        public string Texto = "";
        public bool Negrito, Italico, Sublinhado;
        public string Fonte = "Arial";
        public double FontePt = 10;
        /// <summary>RGB (0xRRGGBB) or -1 = automatic.</summary>
        public int Cor = -1, Fundo = -1;
        /// <summary>0 none, 1 thin, 2 medium, 3 thick.</summary>
        public int BordaE, BordaD, BordaC, BordaB;
        /// <summary>0 left, 1 center, 2 right.</summary>
        public int AlinhH;
        /// <summary>0 top, 1 middle, 2 bottom.</summary>
        public int AlinhV = 2;
        /// <summary>Degrees (90 = vertical text).</summary>
        public int Rotacao;
    }

    public sealed class PlMescla
    {
        public int Linha1, Coluna1, Linha2, Coluna2;
    }

    /// <summary>A rectangular piece of a worksheet ready to become a Revit table.</summary>
    public sealed class PlTabela
    {
        public List<double> LargurasMm = new List<double>();
        public List<double> AlturasMm = new List<double>();
        public PlCelula[,] Celulas;
        public List<PlMescla> Mesclas = new List<PlMescla>();
        public int Linhas => AlturasMm.Count;
        public int Colunas => LargurasMm.Count;
        public string Intervalo = "";
    }

    /// <summary>
    /// Reads .xlsx/.xlsm (Office Open XML, no Excel needed) and .csv into a <see cref="PlTabela"/>:
    /// texts formatted as Excel shows them (number formats, dates, percentages in pt-BR), fonts,
    /// colors, fills, borders, alignment, merged cells, column widths and row heights.
    /// </summary>
    public static class PlanilhaLeitor
    {
        private static readonly XNamespace M = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private static readonly XNamespace PR = "http://schemas.openxmlformats.org/package/2006/relationships";
        private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
        internal static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

        // ------------------------------------------------------------------ public API

        /// <summary>Visible worksheet names (xlsx) or a single "CSV".</summary>
        public static List<string> Abas(string caminho)
        {
            if (EhCsv(caminho)) return new List<string> { "CSV" };
            using (var z = Abrir(caminho))
                return LerAbas(z).Where(a => !a.Oculta).Select(a => a.Nome).ToList();
        }

        public static bool EhCsv(string caminho) =>
            string.Equals(Path.GetExtension(caminho), ".csv", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetExtension(caminho), ".txt", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Reads <paramref name="aba"/> (null = first visible). <paramref name="intervalo"/> like "A1:H30";
        /// empty = print area, or the used area (cells with text, fill or border).
        /// </summary>
        public static PlTabela Ler(string caminho, string aba, string intervalo)
        {
            if (EhCsv(caminho)) return LerCsv(caminho, intervalo);
            using (var z = Abrir(caminho))
            {
                var abas = LerAbas(z);
                var a = abas.FirstOrDefault(x => string.Equals(x.Nome, aba, StringComparison.OrdinalIgnoreCase))
                        ?? abas.FirstOrDefault(x => !x.Oculta) ?? abas.FirstOrDefault();
                if (a == null) throw new InvalidDataException("A planilha não tem abas.");
                var estilos = new Estilos(z);
                var textos = LerTextos(z);
                return LerAba(z, a, estilos, textos, intervalo, abas.IndexOf(a));
            }
        }

        // ------------------------------------------------------------------ package

        private static ZipArchive Abrir(string caminho)
        {
            // FileShare.ReadWrite: the file may be open in Excel
            var fs = new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            try { return new ZipArchive(fs, ZipArchiveMode.Read); }
            catch (InvalidDataException)
            {
                fs.Dispose();
                throw new InvalidDataException("O arquivo não é um .xlsx válido (arquivos .xls antigos: salve como .xlsx ou use “Excel como imagem”).");
            }
        }

        private static XDocument Xml(ZipArchive z, string parte)
        {
            parte = parte.TrimStart('/');
            ZipArchiveEntry e = z.Entries.FirstOrDefault(x => string.Equals(x.FullName, parte, StringComparison.OrdinalIgnoreCase));
            if (e == null) return null;
            using (var s = e.Open()) return XDocument.Load(s);
        }

        private sealed class AbaInfo
        {
            public string Nome, Parte, AreaImpressao;
            public bool Oculta;
        }

        private static List<AbaInfo> LerAbas(ZipArchive z)
        {
            XDocument wb = Xml(z, "xl/workbook.xml") ?? throw new InvalidDataException("workbook.xml ausente.");
            XDocument rels = Xml(z, "xl/_rels/workbook.xml.rels");
            var alvo = rels?.Root?.Elements(PR + "Relationship").ToDictionary(r => (string)r.Attribute("Id"), r => (string)r.Attribute("Target"))
                       ?? new Dictionary<string, string>();
            var list = new List<AbaInfo>();
            foreach (XElement s in wb.Root.Element(M + "sheets")?.Elements(M + "sheet") ?? Enumerable.Empty<XElement>())
            {
                string rid = (string)s.Attribute(R + "id");
                if (rid == null || !alvo.TryGetValue(rid, out string t)) continue;
                string parte = t.StartsWith("/") ? t.TrimStart('/') : "xl/" + t;
                list.Add(new AbaInfo { Nome = (string)s.Attribute("name"), Parte = parte, Oculta = ((string)s.Attribute("state") ?? "visible") != "visible" });
            }
            foreach (XElement d in wb.Root.Element(M + "definedNames")?.Elements(M + "definedName") ?? Enumerable.Empty<XElement>())
            {
                if ((string)d.Attribute("name") != "_xlnm.Print_Area") continue;
                if (!int.TryParse((string)d.Attribute("localSheetId"), out int i) || i < 0 || i >= list.Count) continue;
                // 'Folha 1'!$A$1:$H$30  (several areas separated by ',' : first one)
                string v = d.Value.Split(',')[0];
                int ex = v.LastIndexOf('!');
                list[i].AreaImpressao = (ex >= 0 ? v.Substring(ex + 1) : v).Replace("$", "");
            }
            return list;
        }

        private static List<string> LerTextos(ZipArchive z)
        {
            var list = new List<string>();
            XDocument ss = Xml(z, "xl/sharedStrings.xml");
            if (ss == null) return list;
            foreach (XElement si in ss.Root.Elements(M + "si")) list.Add(TextoDe(si));
            return list;
        }

        /// <summary>Text of an &lt;si&gt; or &lt;is&gt;: plain t, or the runs (phonetic runs left out).</summary>
        private static string TextoDe(XElement si)
        {
            XElement t = si.Element(M + "t");
            if (t != null) return Limpar(t.Value);
            return Limpar(string.Concat(si.Elements(M + "r").Select(r => r.Element(M + "t")?.Value ?? "")));
        }

        /// <summary>_x000D_ escapes and CR/LF.</summary>
        private static string Limpar(string s) =>
            Regex.Replace(s ?? "", "_x([0-9A-Fa-f]{4})_", m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString())
                 .Replace("\r\n", "\n").Replace("\r", "\n");

        // ------------------------------------------------------------------ styles

        private sealed class Fonte { public string Nome = "Calibri"; public double Pt = 11; public bool B, I, U; public int Cor = -1; }
        private sealed class Borda { public int E, D, C, B; }
        private sealed class Xf { public int NumFmt, Fonte, Fundo, Borda; public string H, V; public int Rot; public bool Quebra; }

        private sealed class Estilos
        {
            public readonly List<Fonte> Fontes = new List<Fonte>();
            public readonly List<int> Fundos = new List<int>();
            public readonly List<Borda> Bordas = new List<Borda>();
            public readonly List<Xf> Xfs = new List<Xf>();
            public readonly Dictionary<int, string> Formatos = new Dictionary<int, string>();
            private readonly List<int> _tema = new List<int>();

            public Estilos(ZipArchive z)
            {
                LerTema(z);
                XDocument st = Xml(z, "xl/styles.xml");
                if (st == null) { Padrao(); return; }
                XElement root = st.Root;
                foreach (XElement n in root.Element(M + "numFmts")?.Elements(M + "numFmt") ?? Enumerable.Empty<XElement>())
                    Formatos[(int?)n.Attribute("numFmtId") ?? 0] = (string)n.Attribute("formatCode") ?? "";
                foreach (XElement f in root.Element(M + "fonts")?.Elements(M + "font") ?? Enumerable.Empty<XElement>())
                {
                    var fo = new Fonte
                    {
                        Nome = (string)f.Element(M + "name")?.Attribute("val") ?? "Calibri",
                        Pt = Dbl((string)f.Element(M + "sz")?.Attribute("val"), 11),
                        B = Liga(f.Element(M + "b")), I = Liga(f.Element(M + "i")),
                        U = f.Element(M + "u") != null && (string)f.Element(M + "u").Attribute("val") != "none",
                        Cor = Cor(f.Element(M + "color"))
                    };
                    Fontes.Add(fo);
                }
                foreach (XElement f in root.Element(M + "fills")?.Elements(M + "fill") ?? Enumerable.Empty<XElement>())
                {
                    XElement p = f.Element(M + "patternFill");
                    string tipo = (string)p?.Attribute("patternType") ?? "none";
                    if (p == null || tipo == "none") { Fundos.Add(-1); continue; }
                    int c = Cor(p.Element(M + "fgColor"));
                    if (c < 0 && tipo != "solid") c = Cor(p.Element(M + "bgColor"));
                    Fundos.Add(tipo == "gray125" || tipo == "gray0625" ? -1 : c);
                }
                foreach (XElement b in root.Element(M + "borders")?.Elements(M + "border") ?? Enumerable.Empty<XElement>())
                    Bordas.Add(new Borda { E = Peso(b.Element(M + "left") ?? b.Element(M + "start")), D = Peso(b.Element(M + "right") ?? b.Element(M + "end")), C = Peso(b.Element(M + "top")), B = Peso(b.Element(M + "bottom")) });
                foreach (XElement x in root.Element(M + "cellXfs")?.Elements(M + "xf") ?? Enumerable.Empty<XElement>())
                {
                    XElement al = x.Element(M + "alignment");
                    Xfs.Add(new Xf
                    {
                        NumFmt = (int?)x.Attribute("numFmtId") ?? 0, Fonte = (int?)x.Attribute("fontId") ?? 0,
                        Fundo = (int?)x.Attribute("fillId") ?? 0, Borda = (int?)x.Attribute("borderId") ?? 0,
                        H = (string)al?.Attribute("horizontal"), V = (string)al?.Attribute("vertical"),
                        Rot = (int?)al?.Attribute("textRotation") ?? 0, Quebra = (string)al?.Attribute("wrapText") == "1"
                    });
                }
                Padrao();
            }

            private void Padrao()
            {
                if (Fontes.Count == 0) Fontes.Add(new Fonte());
                if (Fundos.Count == 0) Fundos.Add(-1);
                if (Bordas.Count == 0) Bordas.Add(new Borda());
                if (Xfs.Count == 0) Xfs.Add(new Xf());
            }

            private static bool Liga(XElement e) => e != null && (string)e.Attribute("val") != "0" && (string)e.Attribute("val") != "false";

            private static int Peso(XElement e)
            {
                switch ((string)e?.Attribute("style"))
                {
                    case null: case "none": return 0;
                    case "medium": case "mediumDashed": case "mediumDashDot": case "mediumDashDotDot": case "double": return 2;
                    case "thick": return 3;
                    default: return 1;
                }
            }

            private void LerTema(ZipArchive z)
            {
                XDocument th = Xml(z, "xl/theme/theme1.xml");
                XElement cs = th?.Descendants(A + "clrScheme").FirstOrDefault();
                string[] ordem = { "lt1", "dk1", "lt2", "dk2", "accent1", "accent2", "accent3", "accent4", "accent5", "accent6", "hlink", "folHlink" };
                foreach (string n in ordem)
                {
                    XElement e = cs?.Element(A + n);
                    string hex = (string)e?.Element(A + "srgbClr")?.Attribute("val") ?? (string)e?.Element(A + "sysClr")?.Attribute("lastClr");
                    _tema.Add(hex != null && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v) ? v
                        : n == "lt1" ? 0xFFFFFF : n == "dk1" ? 0x000000 : n == "lt2" ? 0xE7E6E6 : n == "dk2" ? 0x44546A : 0x4472C4);
                }
            }

            public int Cor(XElement c)
            {
                if (c == null || (string)c.Attribute("auto") == "1") return -1;
                int rgb = -1;
                string hex = (string)c.Attribute("rgb");
                if (hex != null && hex.Length >= 6 && int.TryParse(hex.Substring(hex.Length - 6), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v)) rgb = v;
                else if (int.TryParse((string)c.Attribute("theme"), out int t) && t >= 0 && t < _tema.Count) rgb = _tema[t];
                else if (int.TryParse((string)c.Attribute("indexed"), out int ix)) rgb = Indexada(ix);
                if (rgb < 0) return -1;
                double tint = Dbl((string)c.Attribute("tint"), 0);
                return tint == 0 ? rgb : Matiz(rgb, tint);
            }
        }

        private static readonly int[] Paleta =
        {
            0x000000, 0xFFFFFF, 0xFF0000, 0x00FF00, 0x0000FF, 0xFFFF00, 0xFF00FF, 0x00FFFF,
            0x000000, 0xFFFFFF, 0xFF0000, 0x00FF00, 0x0000FF, 0xFFFF00, 0xFF00FF, 0x00FFFF,
            0x800000, 0x008000, 0x000080, 0x808000, 0x800080, 0x008080, 0xC0C0C0, 0x808080,
            0x9999FF, 0x993366, 0xFFFFCC, 0xCCFFFF, 0x660066, 0xFF8080, 0x0066CC, 0xCCCCFF,
            0x000080, 0xFF00FF, 0xFFFF00, 0x00FFFF, 0x800080, 0x800000, 0x008080, 0x0000FF,
            0x00CCFF, 0xCCFFFF, 0xCCFFCC, 0xFFFF99, 0x99CCFF, 0xFF99CC, 0xCC99FF, 0xFFCC99,
            0x3366FF, 0x33CCCC, 0x99CC00, 0xFFCC00, 0xFF9900, 0xFF6600, 0x666699, 0x969696,
            0x003366, 0x339966, 0x003300, 0x333300, 0x993300, 0x993366, 0x333399, 0x333333
        };

        private static int Indexada(int i) => i >= 0 && i < Paleta.Length ? Paleta[i] : i == 64 ? -1 : i == 65 ? -1 : -1;

        /// <summary>Excel tint: lighten (t &gt; 0) or darken (t &lt; 0) the luminance in HLS.</summary>
        internal static int Matiz(int rgb, double tint)
        {
            double r = ((rgb >> 16) & 255) / 255.0, g = ((rgb >> 8) & 255) / 255.0, b = (rgb & 255) / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double h = 0, s, l = (max + min) / 2;
            if (max == min) s = 0;
            else
            {
                double d = max - min;
                s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
                if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
                else if (max == g) h = (b - r) / d + 2;
                else h = (r - g) / d + 4;
                h /= 6;
            }
            l = tint < 0 ? l * (1 + tint) : l * (1 - tint) + tint;
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
            double Hue(double t)
            {
                if (t < 0) t += 1; if (t > 1) t -= 1;
                if (t < 1 / 6.0) return p + (q - p) * 6 * t;
                if (t < 0.5) return q;
                if (t < 2 / 3.0) return p + (q - p) * (2 / 3.0 - t) * 6;
                return p;
            }
            if (s == 0) r = g = b = l; else { r = Hue(h + 1 / 3.0); g = Hue(h); b = Hue(h - 1 / 3.0); }
            int C(double v) => Math.Max(0, Math.Min(255, (int)Math.Round(v * 255)));
            return (C(r) << 16) | (C(g) << 8) | C(b);
        }

        private static double Dbl(string s, double padrao) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : padrao;

        // ------------------------------------------------------------------ worksheet

        private sealed class CelulaBruta { public string Tipo, Valor; public int Estilo; }

        private static PlTabela LerAba(ZipArchive z, AbaInfo aba, Estilos es, List<string> textos, string intervalo, int indice)
        {
            XDocument doc = Xml(z, aba.Parte) ?? throw new InvalidDataException("Aba ilegível: " + aba.Nome);
            XElement ws = doc.Root;

            XElement fmt = ws.Element(M + "sheetFormatPr");
            double larguraPadrao = Dbl((string)fmt?.Attribute("defaultColWidth"), double.NaN);
            if (double.IsNaN(larguraPadrao)) larguraPadrao = Dbl((string)fmt?.Attribute("baseColWidth"), 8) + 1.140625;
            double alturaPadrao = Dbl((string)fmt?.Attribute("defaultRowHeight"), 15);

            var colLarg = new Dictionary<int, double>();
            var colOculta = new HashSet<int>();
            var colEstilo = new Dictionary<int, int>();
            foreach (XElement c in ws.Element(M + "cols")?.Elements(M + "col") ?? Enumerable.Empty<XElement>())
            {
                int min = (int?)c.Attribute("min") ?? 1, max = Math.Min((int?)c.Attribute("max") ?? min, 16384);
                double w = Dbl((string)c.Attribute("width"), larguraPadrao);
                bool hid = (string)c.Attribute("hidden") == "1" || w <= 0;
                int? s = (int?)c.Attribute("style");
                for (int i = min; i <= max && i <= min + 2000; i++)
                {
                    colLarg[i] = w;
                    if (hid) colOculta.Add(i);
                    if (s.HasValue) colEstilo[i] = s.Value;
                }
            }

            var linAlt = new Dictionary<int, double>();
            var linOculta = new HashSet<int>();
            var linEstilo = new Dictionary<int, int>();
            var celulas = new Dictionary<(int r, int c), CelulaBruta>();
            int maxR = 0, maxC = 0;
            XElement data = ws.Element(M + "sheetData");
            int linhaAtual = 0;
            foreach (XElement row in data?.Elements(M + "row") ?? Enumerable.Empty<XElement>())
            {
                int r = (int?)row.Attribute("r") ?? linhaAtual + 1;
                linhaAtual = r;
                if (row.Attribute("ht") != null) linAlt[r] = Dbl((string)row.Attribute("ht"), alturaPadrao);
                if ((string)row.Attribute("hidden") == "1") linOculta.Add(r);
                if ((string)row.Attribute("customFormat") == "1" && row.Attribute("s") != null) linEstilo[r] = (int)row.Attribute("s");
                int colAtual = 0;
                foreach (XElement c in row.Elements(M + "c"))
                {
                    int col;
                    string refc = (string)c.Attribute("r");
                    if (refc != null) { Ref(refc, out int rr, out col); } else col = colAtual + 1;
                    colAtual = col;
                    string t = (string)c.Attribute("t") ?? "n";
                    string v = t == "inlineStr" ? (c.Element(M + "is") != null ? TextoDe(c.Element(M + "is")) : "") : c.Element(M + "v")?.Value;
                    var cb = new CelulaBruta { Tipo = t, Valor = v, Estilo = (int?)c.Attribute("s") ?? -1 };
                    celulas[(r, col)] = cb;
                    bool util = !string.IsNullOrEmpty(v) || Visivel(es, cb.Estilo);
                    if (util) { maxR = Math.Max(maxR, r); maxC = Math.Max(maxC, col); }
                }
            }

            var mesclas = new List<PlMescla>();
            foreach (XElement m in ws.Element(M + "mergeCells")?.Elements(M + "mergeCell") ?? Enumerable.Empty<XElement>())
            {
                string[] p = ((string)m.Attribute("ref") ?? "").Split(':');
                if (p.Length != 2) continue;
                Ref(p[0], out int r1, out int c1);
                Ref(p[1], out int r2, out int c2);
                mesclas.Add(new PlMescla { Linha1 = r1, Coluna1 = c1, Linha2 = r2, Coluna2 = c2 });
            }

            // range
            int R1 = 1, C1 = 1, R2 = maxR, C2 = maxC;
            string area = string.IsNullOrWhiteSpace(intervalo) ? aba.AreaImpressao : intervalo.Replace("$", "").Trim();
            if (!string.IsNullOrWhiteSpace(area))
            {
                string[] p = area.Split(':');
                Ref(p[0], out R1, out C1);
                if (p.Length > 1) Ref(p[1], out R2, out C2); else { R2 = R1; C2 = C1; }
                if (R1 > R2) { int x = R1; R1 = R2; R2 = x; }
                if (C1 > C2) { int x = C1; C1 = C2; C2 = x; }
            }
            else
            {
                // used area starts at the first used row/column
                var usadas = celulas.Where(kv => !string.IsNullOrEmpty(kv.Value.Valor) || Visivel(es, kv.Value.Estilo)).Select(kv => kv.Key).ToList();
                if (usadas.Count == 0) throw new InvalidDataException($"A aba “{aba.Nome}” está vazia.");
                R1 = usadas.Min(k => k.r); C1 = usadas.Min(k => k.c);
                foreach (var m in mesclas.Where(m => m.Linha1 <= R2 && m.Coluna1 <= C2)) { R2 = Math.Max(R2, m.Linha2); C2 = Math.Max(C2, m.Coluna2); }
            }
            if ((long)(R2 - R1 + 1) * (C2 - C1 + 1) > 60000)
                throw new InvalidDataException($"Intervalo grande demais ({R2 - R1 + 1} linhas × {C2 - C1 + 1} colunas). Defina um intervalo menor ou uma área de impressão.");

            // visible rows/cols → table indices
            var linhas = Enumerable.Range(R1, R2 - R1 + 1).Where(r => !linOculta.Contains(r)).ToList();
            var colunas = Enumerable.Range(C1, C2 - C1 + 1).Where(c => !colOculta.Contains(c)).ToList();
            if (linhas.Count == 0 || colunas.Count == 0) throw new InvalidDataException("O intervalo só tem linhas ou colunas ocultas.");
            var idxL = new Dictionary<int, int>(); for (int i = 0; i < linhas.Count; i++) idxL[linhas[i]] = i;
            var idxC = new Dictionary<int, int>(); for (int i = 0; i < colunas.Count; i++) idxC[colunas[i]] = i;

            var tab = new PlTabela { Intervalo = Nome(R1, C1) + ":" + Nome(R2, C2), Celulas = new PlCelula[linhas.Count, colunas.Count] };
            foreach (int c in colunas) tab.LargurasMm.Add(LarguraMm(colLarg.TryGetValue(c, out double w) ? w : larguraPadrao));
            foreach (int r in linhas) tab.AlturasMm.Add((linAlt.TryGetValue(r, out double h) ? h : alturaPadrao) * 25.4 / 72);

            for (int i = 0; i < linhas.Count; i++)
                for (int j = 0; j < colunas.Count; j++)
                {
                    int r = linhas[i], c = colunas[j];
                    celulas.TryGetValue((r, c), out CelulaBruta cb);
                    int s = cb != null && cb.Estilo >= 0 ? cb.Estilo : linEstilo.TryGetValue(r, out int ls) ? ls : colEstilo.TryGetValue(c, out int cs) ? cs : 0;
                    tab.Celulas[i, j] = Montar(es, textos, cb, s);
                }

            // merges clipped to the range and to visible rows/cols
            foreach (PlMescla m in mesclas)
            {
                var ls = linhas.Where(r => r >= m.Linha1 && r <= m.Linha2).ToList();
                var cs = colunas.Where(c => c >= m.Coluna1 && c <= m.Coluna2).ToList();
                if (ls.Count == 0 || cs.Count == 0 || (ls.Count == 1 && cs.Count == 1)) continue;
                var nm = new PlMescla { Linha1 = idxL[ls.First()], Linha2 = idxL[ls.Last()], Coluna1 = idxC[cs.First()], Coluna2 = idxC[cs.Last()] };
                // the merged block shows the top-left cell of the Excel merge (even if it is outside the range)
                if (m.Linha1 != ls.First() || m.Coluna1 != cs.First())
                {
                    celulas.TryGetValue((m.Linha1, m.Coluna1), out CelulaBruta cb0);
                    if (cb0 != null) tab.Celulas[nm.Linha1, nm.Coluna1] = Montar(es, textos, cb0, cb0.Estilo >= 0 ? cb0.Estilo : 0);
                }
                // outer borders of the block come from its edge cells
                PlCelula tl = tab.Celulas[nm.Linha1, nm.Coluna1];
                for (int i = nm.Linha1; i <= nm.Linha2; i++)
                {
                    tl.BordaE = Math.Max(tl.BordaE, tab.Celulas[i, nm.Coluna1].BordaE);
                    tl.BordaD = Math.Max(tl.BordaD, tab.Celulas[i, nm.Coluna2].BordaD);
                }
                for (int j = nm.Coluna1; j <= nm.Coluna2; j++)
                {
                    tl.BordaC = Math.Max(tl.BordaC, tab.Celulas[nm.Linha1, j].BordaC);
                    tl.BordaB = Math.Max(tl.BordaB, tab.Celulas[nm.Linha2, j].BordaB);
                }
                tab.Mesclas.Add(nm);
            }

            // shared edges: the heavier of the two sides on both cells
            for (int i = 0; i < tab.Linhas; i++)
                for (int j = 0; j < tab.Colunas; j++)
                {
                    PlCelula a = tab.Celulas[i, j];
                    if (j + 1 < tab.Colunas) { PlCelula b = tab.Celulas[i, j + 1]; int x = Math.Max(a.BordaD, b.BordaE); a.BordaD = b.BordaE = x; }
                    if (i + 1 < tab.Linhas) { PlCelula b = tab.Celulas[i + 1, j]; int x = Math.Max(a.BordaB, b.BordaC); a.BordaB = b.BordaC = x; }
                }
            return tab;
        }

        private static bool Visivel(Estilos es, int s)
        {
            if (s < 0 || s >= es.Xfs.Count) return false;
            Xf x = es.Xfs[s];
            Borda b = x.Borda < es.Bordas.Count ? es.Bordas[x.Borda] : null;
            int fundo = x.Fundo < es.Fundos.Count ? es.Fundos[x.Fundo] : -1;
            return fundo >= 0 || (b != null && (b.E | b.D | b.C | b.B) != 0);
        }

        private static PlCelula Montar(Estilos es, List<string> textos, CelulaBruta cb, int s)
        {
            Xf x = s >= 0 && s < es.Xfs.Count ? es.Xfs[s] : es.Xfs[0];
            Fonte f = x.Fonte < es.Fontes.Count ? es.Fontes[x.Fonte] : es.Fontes[0];
            Borda b = x.Borda < es.Bordas.Count ? es.Bordas[x.Borda] : new Borda();
            var cel = new PlCelula
            {
                Negrito = f.B, Italico = f.I, Sublinhado = f.U, Fonte = f.Nome, FontePt = f.Pt, Cor = f.Cor,
                Fundo = x.Fundo < es.Fundos.Count ? es.Fundos[x.Fundo] : -1,
                BordaE = b.E, BordaD = b.D, BordaC = b.C, BordaB = b.B,
                Rotacao = x.Rot == 255 ? 90 : x.Rot > 90 ? -(x.Rot - 90) : x.Rot
            };
            bool numero = false;
            if (cb != null && cb.Valor != null)
            {
                switch (cb.Tipo)
                {
                    case "s":
                        cel.Texto = int.TryParse(cb.Valor, out int i) && i >= 0 && i < textos.Count ? textos[i] : "";
                        break;
                    case "inlineStr": case "str":
                        cel.Texto = Limpar(cb.Valor);
                        break;
                    case "b":
                        cel.Texto = cb.Valor == "1" ? "VERDADEIRO" : "FALSO";
                        cel.AlinhH = 1;
                        break;
                    case "e":
                        cel.Texto = cb.Valor;
                        cel.AlinhH = 1;
                        break;
                    case "d":
                        if (DateTime.TryParse(cb.Valor, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dt))
                        { cel.Texto = NumeroFormato.Formatar(dt.ToOADate(), Codigo(es, x.NumFmt, true), PtBr); numero = true; }
                        else cel.Texto = cb.Valor;
                        break;
                    default:
                        if (double.TryParse(cb.Valor, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                        { cel.Texto = NumeroFormato.Formatar(v, Codigo(es, x.NumFmt, false), PtBr); numero = true; }
                        else cel.Texto = cb.Valor;
                        break;
                }
            }
            switch (x.H)
            {
                case "center": case "centerContinuous": case "distributed": cel.AlinhH = 1; break;
                case "right": cel.AlinhH = 2; break;
                case "left": case "fill": case "justify": cel.AlinhH = 0; break;
                default: if (numero) cel.AlinhH = 2; break;
            }
            cel.AlinhV = x.V == "top" ? 0 : x.V == "center" || x.V == "distributed" || x.V == "justify" ? 1 : 2;
            return cel;
        }

        private static string Codigo(Estilos es, int id, bool data)
        {
            if (es.Formatos.TryGetValue(id, out string c)) return c;
            switch (id)
            {
                case 0: return data ? "dd/mm/yyyy" : "General";
                case 1: return "0";
                case 2: return "0.00";
                case 3: return "#,##0";
                case 4: return "#,##0.00";
                case 5: case 6: return "\"R$\" #,##0;-\"R$\" #,##0";
                case 7: case 8: return "\"R$\" #,##0.00;-\"R$\" #,##0.00";
                case 9: return "0%";
                case 10: return "0.00%";
                case 11: return "0.00E+00";
                case 12: return "# ?/?";
                case 13: return "# ??/??";
                case 14: return "dd/mm/yyyy";
                case 15: return "d-mmm-yy";
                case 16: return "d-mmm";
                case 17: return "mmm-yy";
                case 18: return "h:mm AM/PM";
                case 19: return "h:mm:ss AM/PM";
                case 20: return "hh:mm";
                case 21: return "hh:mm:ss";
                case 22: return "dd/mm/yyyy hh:mm";
                case 37: return "#,##0 ;(#,##0)";
                case 38: return "#,##0 ;[Red](#,##0)";
                case 39: return "#,##0.00;(#,##0.00)";
                case 40: return "#,##0.00;[Red](#,##0.00)";
                case 44: return "_-\"R$\" * #,##0.00_-;-\"R$\" * #,##0.00_-;_-\"R$\" * \"-\"??_-;_-@_-";
                case 45: return "mm:ss";
                case 46: return "[h]:mm:ss";
                case 47: return "mm:ss.0";
                case 48: return "##0.0E+0";
                case 49: return "@";
                default: return data ? "dd/mm/yyyy" : "General";
            }
        }

        /// <summary>Excel column width (characters of the default font) → mm on paper (Calibri 11: 7 px per digit, 96 dpi).</summary>
        internal static double LarguraMm(double chars)
        {
            if (chars <= 0) return 0;
            double px = Math.Truncate((256 * chars + Math.Truncate(128 / 7.0)) / 256 * 7);
            return px * 25.4 / 96;
        }

        /// <summary>"B12" → row 12, column 2.</summary>
        internal static void Ref(string a1, out int linha, out int coluna)
        {
            linha = 0; coluna = 0;
            int i = 0;
            a1 = (a1 ?? "").Trim().ToUpperInvariant();
            while (i < a1.Length && a1[i] >= 'A' && a1[i] <= 'Z') { coluna = coluna * 26 + (a1[i] - 'A' + 1); i++; }
            int.TryParse(a1.Substring(i), out linha);
            if (linha <= 0) linha = 1;
            if (coluna <= 0) coluna = 1;
        }

        internal static string Nome(int linha, int coluna)
        {
            string s = "";
            for (int c = coluna; c > 0; c = (c - 1) / 26) s = (char)('A' + (c - 1) % 26) + s;
            return s + linha.ToString(CultureInfo.InvariantCulture);
        }

        // ------------------------------------------------------------------ CSV

        private static PlTabela LerCsv(string caminho, string intervalo)
        {
            byte[] bytes;
            using (var fs = new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var ms = new MemoryStream()) { fs.CopyTo(ms); bytes = ms.ToArray(); }
            string texto;
            try { texto = new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { texto = Encoding.GetEncoding(1252).GetString(bytes); }
            texto = texto.TrimStart('﻿');
            string primeira = texto.Split('\n')[0];
            char sep = new[] { ';', '\t', ',' }.OrderByDescending(ch => primeira.Count(x => x == ch)).First();

            var linhas = new List<List<string>>();
            var atual = new List<string>();
            var campo = new StringBuilder();
            bool aspas = false;
            for (int i = 0; i < texto.Length; i++)
            {
                char ch = texto[i];
                if (aspas)
                {
                    if (ch == '"') { if (i + 1 < texto.Length && texto[i + 1] == '"') { campo.Append('"'); i++; } else aspas = false; }
                    else campo.Append(ch);
                }
                else if (ch == '"') aspas = true;
                else if (ch == sep) { atual.Add(campo.ToString()); campo.Clear(); }
                else if (ch == '\n' || ch == '\r')
                {
                    if (ch == '\r' && i + 1 < texto.Length && texto[i + 1] == '\n') i++;
                    atual.Add(campo.ToString()); campo.Clear();
                    linhas.Add(atual); atual = new List<string>();
                }
                else campo.Append(ch);
            }
            if (campo.Length > 0 || atual.Count > 0) { atual.Add(campo.ToString()); linhas.Add(atual); }
            while (linhas.Count > 0 && linhas[linhas.Count - 1].All(string.IsNullOrWhiteSpace)) linhas.RemoveAt(linhas.Count - 1);
            if (linhas.Count == 0) throw new InvalidDataException("O CSV está vazio.");

            int nc = linhas.Max(l => l.Count);
            int R1 = 1, C1 = 1, R2 = linhas.Count, C2 = nc;
            if (!string.IsNullOrWhiteSpace(intervalo))
            {
                string[] p = intervalo.Replace("$", "").Split(':');
                Ref(p[0], out R1, out C1);
                if (p.Length > 1) Ref(p[1], out R2, out C2); else { R2 = R1; C2 = C1; }
                R2 = Math.Min(R2, linhas.Count); C2 = Math.Min(C2, nc);
            }
            if ((long)(R2 - R1 + 1) * (C2 - C1 + 1) > 60000) throw new InvalidDataException("CSV grande demais; defina um intervalo.");
            int L = Math.Max(1, R2 - R1 + 1), C = Math.Max(1, C2 - C1 + 1);
            var tab = new PlTabela { Intervalo = Nome(R1, C1) + ":" + Nome(R2, C2), Celulas = new PlCelula[L, C] };
            for (int j = 0; j < C; j++)
            {
                int max = 0;
                for (int i = 0; i < L; i++)
                {
                    var l = R1 - 1 + i < linhas.Count ? linhas[R1 - 1 + i] : null;
                    string t = l != null && C1 - 1 + j < l.Count ? l[C1 - 1 + j].Trim() : "";
                    bool num = double.TryParse(t, NumberStyles.Number, PtBr, out _);
                    tab.Celulas[i, j] = new PlCelula { Texto = t, Fonte = "Arial", FontePt = 10, AlinhH = num ? 2 : 0, Negrito = i == 0 && R1 == 1, BordaE = 1, BordaD = 1, BordaC = 1, BordaB = 1 };
                    max = Math.Max(max, t.Length);
                }
                tab.LargurasMm.Add(Math.Max(12, Math.Min(90, max * 1.9 + 4)));
            }
            for (int i = 0; i < L; i++) tab.AlturasMm.Add(6);
            return tab;
        }
    }

    /// <summary>Excel number formats → text as Excel shows it (pt-BR separators).</summary>
    public static class NumeroFormato
    {
        private static readonly string[] Meses = { "janeiro", "fevereiro", "março", "abril", "maio", "junho", "julho", "agosto", "setembro", "outubro", "novembro", "dezembro" };
        private static readonly string[] Dias = { "domingo", "segunda-feira", "terça-feira", "quarta-feira", "quinta-feira", "sexta-feira", "sábado" };

        public static string Formatar(double v, string codigo, CultureInfo cult)
        {
            if (string.IsNullOrEmpty(codigo) || codigo.Equals("General", StringComparison.OrdinalIgnoreCase) || codigo.Equals("Geral", StringComparison.OrdinalIgnoreCase))
                return Geral(v, cult);
            string[] secoes = Dividir(codigo);
            string sec;
            bool usarAbs = false;
            if (v < 0 && secoes.Length >= 2) { sec = secoes[1]; usarAbs = true; }
            else if (v == 0 && secoes.Length >= 3) { sec = secoes[2]; usarAbs = true; }
            else sec = secoes[0];
            // conditions like [<1000] are rare: treat the first section as the main one
            sec = Regex.Replace(sec, @"\[(Red|Black|Blue|Green|White|Yellow|Magenta|Cyan|Vermelho|Preto|Azul|Verde|Branco|Amarelo|Color\s*\d+|Cor\s*\d+|[<>=]+[-0-9.]+)\]", "", RegexOptions.IgnoreCase);
            // [$R$-416] → R$ ; [$-416] → nothing
            sec = Regex.Replace(sec, @"\[\$([^\]-]*)(-[0-9A-Fa-f]+)?\]", m => m.Groups[1].Value.Length > 0 ? "\"" + m.Groups[1].Value + "\"" : "");
            if (sec == "@") return Geral(v, cult);
            if (EhData(sec)) return Data(v, sec);
            double x = usarAbs ? Math.Abs(v) : v;
            string r = Numero(x, sec, cult, out bool temNumero);
            if (!temNumero) return r; // text-only section (e.g. "-")
            if (!usarAbs && v < 0 && secoes.Length < 2 && !r.StartsWith("-")) r = "-" + r;
            return r;
        }

        private static string[] Dividir(string codigo)
        {
            var list = new List<string>();
            var sb = new StringBuilder();
            bool q = false;
            for (int i = 0; i < codigo.Length; i++)
            {
                char c = codigo[i];
                if (c == '"') q = !q;
                if (c == '\\' && i + 1 < codigo.Length) { sb.Append(c).Append(codigo[++i]); continue; }
                if (c == ';' && !q) { list.Add(sb.ToString()); sb.Clear(); continue; }
                sb.Append(c);
            }
            list.Add(sb.ToString());
            return list.ToArray();
        }

        public static string Geral(double v, CultureInfo cult)
        {
            if (v == Math.Floor(v) && Math.Abs(v) < 1e11) return v.ToString("0", cult);
            double a = Math.Abs(v);
            if (a >= 1e11 || a < 1e-9) return v.ToString("0.#####E+00", cult);
            int inteiros = a < 1 ? 1 : (int)Math.Floor(Math.Log10(a)) + 1;
            int dec = Math.Max(0, 10 - inteiros);
            return Math.Round(v, dec).ToString("0." + new string('#', Math.Max(1, dec)), cult);
        }

        /// <summary>True if the section has date/time codes outside quotes.</summary>
        private static bool EhData(string sec)
        {
            string s = Regex.Replace(sec, "\"[^\"]*\"|\\\\.|_.|\\*.", "");
            s = Regex.Replace(s, @"\[(h|hh|m|mm|s|ss)\]", "h", RegexOptions.IgnoreCase);
            s = s.Replace("E+", "").Replace("e+", "").Replace("E-", "").Replace("e-", "");
            if (Regex.IsMatch(s, "[sS]")) s = Regex.Replace(s, @"\.0+", "");
            return Regex.IsMatch(s, "[dDmMyYhHsS]") && !Regex.IsMatch(s, "[#0?]");
        }

        private static string Data(double v, string sec)
        {
            DateTime dt;
            try { dt = DateTime.FromOADate(v); } catch { return v.ToString(CultureInfo.InvariantCulture); }
            bool ampm = Regex.IsMatch(sec, "AM/PM|A/P", RegexOptions.IgnoreCase);
            var tokens = new List<string>();
            int i = 0;
            while (i < sec.Length)
            {
                char c = sec[i];
                if (c == '"') { int j = sec.IndexOf('"', i + 1); if (j < 0) j = sec.Length - 1; tokens.Add("L" + sec.Substring(i + 1, j - i - 1)); i = j + 1; continue; }
                if (c == '\\' && i + 1 < sec.Length) { tokens.Add("L" + sec[i + 1]); i += 2; continue; }
                if (c == '_' && i + 1 < sec.Length) { tokens.Add("L "); i += 2; continue; }
                if (c == '*' && i + 1 < sec.Length) { i += 2; continue; }
                if (sec.Substring(i).StartsWith("AM/PM", StringComparison.OrdinalIgnoreCase)) { tokens.Add("AMPM"); i += 5; continue; }
                if (sec.Substring(i).StartsWith("A/P", StringComparison.OrdinalIgnoreCase)) { tokens.Add("AP"); i += 3; continue; }
                if (c == '[') { int j = sec.IndexOf(']', i); if (j < 0) j = sec.Length - 1; tokens.Add("E" + sec.Substring(i + 1, j - i - 1).ToLowerInvariant()); i = j + 1; continue; }
                char lc = char.ToLowerInvariant(c);
                if ("dmyhs".IndexOf(lc) >= 0) { int j = i; while (j < sec.Length && char.ToLowerInvariant(sec[j]) == lc) j++; tokens.Add(new string(lc, j - i)); i = j; continue; }
                if (c == '.' && i + 1 < sec.Length && sec[i + 1] == '0') { int j = i + 1; while (j < sec.Length && sec[j] == '0') j++; tokens.Add("F" + (j - i - 1)); i = j; continue; }
                tokens.Add("L" + c); i++;
            }
            // m after h or before s = minutes
            for (int k = 0; k < tokens.Count; k++)
            {
                if (tokens[k] != "m" && tokens[k] != "mm") continue;
                bool min = false;
                for (int b = k - 1; b >= 0; b--) { if (tokens[b].StartsWith("L")) continue; min = tokens[b].StartsWith("h") || tokens[b] == "Eh" || tokens[b] == "Ehh"; break; }
                for (int a = k + 1; a < tokens.Count && !min; a++) { if (tokens[a].StartsWith("L")) continue; min = tokens[a].StartsWith("s"); break; }
                if (min) tokens[k] = "N" + tokens[k].Length;
            }
            var sb = new StringBuilder();
            foreach (string t in tokens)
            {
                switch (t)
                {
                    case "d": sb.Append(dt.Day); break;
                    case "dd": sb.Append(dt.Day.ToString("00")); break;
                    case "ddd": sb.Append(Dias[(int)dt.DayOfWeek].Substring(0, 3)); break;
                    case "m": sb.Append(dt.Month); break;
                    case "mm": sb.Append(dt.Month.ToString("00")); break;
                    case "mmm": sb.Append(Meses[dt.Month - 1].Substring(0, 3)); break;
                    case "mmmmm": sb.Append(Meses[dt.Month - 1].Substring(0, 1)); break;
                    case "yy": case "y": sb.Append((dt.Year % 100).ToString("00")); break;
                    case "h": sb.Append(ampm ? (dt.Hour % 12 == 0 ? 12 : dt.Hour % 12) : dt.Hour); break;
                    case "hh": sb.Append((ampm ? (dt.Hour % 12 == 0 ? 12 : dt.Hour % 12) : dt.Hour).ToString("00")); break;
                    case "N1": sb.Append(dt.Minute); break;
                    case "N2": sb.Append(dt.Minute.ToString("00")); break;
                    case "s": sb.Append(dt.Second); break;
                    case "ss": sb.Append(dt.Second.ToString("00")); break;
                    case "AMPM": sb.Append(dt.Hour < 12 ? "AM" : "PM"); break;
                    case "AP": sb.Append(dt.Hour < 12 ? "A" : "P"); break;
                    case "Eh": sb.Append((int)Math.Floor(v * 24)); break;
                    case "Ehh": sb.Append(((int)Math.Floor(v * 24)).ToString("00")); break;
                    case "Em": sb.Append((int)Math.Floor(v * 1440)); break;
                    case "Es": sb.Append((int)Math.Floor(v * 86400)); break;
                    default:
                        if (t.StartsWith("L")) sb.Append(t.Substring(1));
                        else if (t.StartsWith("F")) { int n = int.Parse(t.Substring(1)); sb.Append(',').Append(dt.Millisecond.ToString("000").Substring(0, Math.Min(3, n))); }
                        else if (t.StartsWith("d")) sb.Append(Dias[(int)dt.DayOfWeek]);
                        else if (t.StartsWith("m")) sb.Append(Meses[dt.Month - 1]);
                        else if (t.StartsWith("y")) sb.Append(dt.Year);
                        else if (t.StartsWith("h")) sb.Append(dt.Hour.ToString("00"));
                        else if (t.StartsWith("s")) sb.Append(dt.Second.ToString("00"));
                        break;
                }
            }
            return sb.ToString();
        }

        /// <summary>Numeric section: literals around one number pattern (0 # ? , . % E).</summary>
        private static string Numero(double v, string sec, CultureInfo cult, out bool temNumero)
        {
            var pre = new StringBuilder();
            var suf = new StringBuilder();
            var padrao = new StringBuilder();
            int estado = 0; // 0 before, 1 in pattern, 2 after
            bool pct = false;
            for (int i = 0; i < sec.Length; i++)
            {
                char c = sec[i];
                if (c == '"')
                {
                    int j = sec.IndexOf('"', i + 1);
                    if (j < 0) j = sec.Length;
                    if (estado == 1) estado = 2;
                    (estado == 0 ? pre : suf).Append(sec.Substring(i + 1, Math.Max(0, j - i - 1)));
                    i = j;
                    continue;
                }
                if (c == '\\' && i + 1 < sec.Length) { if (estado == 1) estado = 2; (estado == 0 ? pre : suf).Append(sec[++i]); continue; }
                if (c == '_' && i + 1 < sec.Length) { i++; if (estado == 1) estado = 2; (estado == 0 ? pre : suf).Append(' '); continue; }
                if (c == '*' && i + 1 < sec.Length) { i++; continue; }
                if (c == '%') { pct = true; if (estado == 1) estado = 2; (estado == 0 ? pre : suf).Append('%'); continue; }
                bool pat = "0#?".IndexOf(c) >= 0 || ((c == '.' || c == ',') && estado == 1) || ((c == 'E' || c == 'e') && estado == 1 && i + 1 < sec.Length && (sec[i + 1] == '+' || sec[i + 1] == '-'));
                if (c == '.' && estado == 0 && i + 1 < sec.Length && "0#?".IndexOf(sec[i + 1]) >= 0) pat = true;
                if (pat && estado != 2)
                {
                    estado = 1;
                    padrao.Append(c);
                    if (c == 'E' || c == 'e') { padrao.Append(sec[++i]); }
                    continue;
                }
                if (estado == 1) estado = 2;
                (estado == 0 ? pre : suf).Append(c);
            }
            temNumero = padrao.Length > 0;
            if (!temNumero) return (pre.ToString() + suf).Trim() == "" ? "" : pre.ToString() + suf;

            string p = padrao.ToString();
            if (pct) v *= 100;
            // trailing commas scale by 1000
            while (p.EndsWith(",")) { v /= 1000; p = p.Substring(0, p.Length - 1); }
            string r;
            int e = p.IndexOfAny(new[] { 'E', 'e' });
            if (e >= 0)
            {
                string mant = p.Substring(0, e);
                int decE = mant.Contains(".") ? mant.Length - mant.IndexOf('.') - 1 : 0;
                r = v.ToString((decE > 0 ? "0." + new string('0', decE) : "0") + "E+00", cult);
            }
            else
            {
                int ponto = p.IndexOf('.');
                string ip = ponto >= 0 ? p.Substring(0, ponto) : p, dp = ponto >= 0 ? p.Substring(ponto + 1) : "";
                bool grupo = ip.Contains(",");
                int minInt = ip.Count(ch => ch == '0');
                int decObrig = dp.Count(ch => ch == '0'), decMax = dp.Count(ch => "0#?".IndexOf(ch) >= 0);
                double a = Math.Round(Math.Abs(v), decMax, MidpointRounding.AwayFromZero);
                var nf = (NumberFormatInfo)cult.NumberFormat.Clone();
                string fmt = grupo ? "#,##" + (minInt > 0 ? new string('0', minInt) : "#") : (minInt > 0 ? new string('0', minInt) : "#");
                if (decMax > 0) fmt += "." + new string('0', decObrig) + new string('#', decMax - decObrig);
                r = a.ToString(fmt, nf);
                if (r == "") r = minInt > 0 ? "0" : "";
                if (v < 0 && a != 0) r = "-" + r;
            }
            return pre.ToString() + r + suf;
        }
    }
}
