using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security;
using System.Text;

namespace JAEsquadrias.Infrastructure
{
    /// <summary>
    /// A minimal Word (.docx) writer: title, headings, paragraphs, bullet lines and simple tables,
    /// in Calibri / the office green. Enough for the Memorial Descritivo; opens in Word and LibreOffice.
    /// </summary>
    public sealed class DocxWriter
    {
        private readonly StringBuilder _body = new StringBuilder();
        public string Cor = "1F7A5A";

        private static string X(string s) => SecurityElement.Escape(s ?? "") ?? "";

        private void P(string estilo, string texto, bool negrito = false, string extraPPr = "")
        {
            _body.Append("<w:p><w:pPr>" + (estilo != null ? "<w:pStyle w:val=\"" + estilo + "\"/>" : "") + extraPPr + "</w:pPr>");
            // line breaks inside the text become <w:br/>
            var partes = (texto ?? "").Replace("\r", "").Split('\n');
            _body.Append("<w:r>" + (negrito ? "<w:rPr><w:b/></w:rPr>" : ""));
            for (int i = 0; i < partes.Length; i++)
            {
                if (i > 0) _body.Append("<w:br/>");
                _body.Append("<w:t xml:space=\"preserve\">" + X(partes[i]) + "</w:t>");
            }
            _body.Append("</w:r></w:p>");
        }

        public void Titulo(string t) => P("Title", t);
        public void Secao(string t) => P("Heading1", t);
        public void Subsecao(string t) => P("Heading2", t);
        public void Paragrafo(string t) => P(null, t);
        public void Negrito(string t) => P(null, t, true);
        public void Item(string t) => P("ListBullet", "•\t" + t);

        /// <summary>Label in bold, then the text on the same line.</summary>
        public void Campo(string rotulo, string texto)
        {
            _body.Append("<w:p><w:r><w:rPr><w:b/></w:rPr><w:t xml:space=\"preserve\">" + X(rotulo) + ": </w:t></w:r><w:r><w:t xml:space=\"preserve\">" + X(texto) + "</w:t></w:r></w:p>");
        }

        /// <summary>Table with the first row as the header (green, white bold text).</summary>
        public void Tabela(IList<string[]> linhas)
        {
            if (linhas == null || linhas.Count == 0) return;
            int n = linhas.Max(l => l.Length);
            _body.Append("<w:tbl><w:tblPr><w:tblStyle w:val=\"TableGrid\"/><w:tblW w:w=\"5000\" w:type=\"pct\"/>" +
                         "<w:tblBorders><w:top w:val=\"single\" w:sz=\"4\" w:color=\"BFBFBF\"/><w:left w:val=\"single\" w:sz=\"4\" w:color=\"BFBFBF\"/><w:bottom w:val=\"single\" w:sz=\"4\" w:color=\"BFBFBF\"/>" +
                         "<w:right w:val=\"single\" w:sz=\"4\" w:color=\"BFBFBF\"/><w:insideH w:val=\"single\" w:sz=\"4\" w:color=\"BFBFBF\"/><w:insideV w:val=\"single\" w:sz=\"4\" w:color=\"BFBFBF\"/></w:tblBorders></w:tblPr><w:tblGrid>");
            for (int i = 0; i < n; i++) _body.Append("<w:gridCol/>");
            _body.Append("</w:tblGrid>");
            for (int r = 0; r < linhas.Count; r++)
            {
                bool cab = r == 0;
                _body.Append("<w:tr>" + (cab ? "<w:trPr><w:tblHeader/></w:trPr>" : ""));
                for (int c = 0; c < n; c++)
                {
                    string v = c < linhas[r].Length ? linhas[r][c] : "";
                    _body.Append("<w:tc><w:tcPr>" + (cab ? "<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"" + Cor + "\"/>" : "") + "</w:tcPr><w:p><w:pPr><w:spacing w:before=\"20\" w:after=\"20\"/></w:pPr><w:r>" +
                                 (cab ? "<w:rPr><w:b/><w:color w:val=\"FFFFFF\"/></w:rPr>" : "") + "<w:t xml:space=\"preserve\">" + X(v) + "</w:t></w:r></w:p></w:tc>");
                }
                _body.Append("</w:tr>");
            }
            _body.Append("</w:tbl>");
            P(null, "");
        }

        public void Save(string path)
        {
            if (File.Exists(path)) File.Delete(path);
            using (var fs = new FileStream(path, FileMode.CreateNew))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                W(zip, "[Content_Types].xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                    "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                    "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
                    "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/></Types>");
                W(zip, "_rels/.rels",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
                W(zip, "word/_rels/document.xml.rels",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
                W(zip, "word/styles.xml", Estilos());
                W(zip, "word/document.xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" +
                    _body + "<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1418\" w:right=\"1134\" w:bottom=\"1134\" w:left=\"1418\" w:header=\"709\" w:footer=\"709\" w:gutter=\"0\"/></w:sectPr></w:body></w:document>");
            }
        }

        private string Estilos() =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
            "<w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii=\"Calibri\" w:hAnsi=\"Calibri\" w:cs=\"Calibri\"/><w:sz w:val=\"21\"/><w:lang w:val=\"pt-BR\"/></w:rPr></w:rPrDefault>" +
            "<w:pPrDefault><w:pPr><w:spacing w:after=\"80\" w:line=\"264\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault></w:docDefaults>" +
            "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/></w:style>" +
            "<w:style w:type=\"paragraph\" w:styleId=\"Title\"><w:name w:val=\"Title\"/><w:basedOn w:val=\"Normal\"/><w:pPr><w:spacing w:after=\"240\"/></w:pPr><w:rPr><w:b/><w:color w:val=\"" + Cor + "\"/><w:sz w:val=\"40\"/></w:rPr></w:style>" +
            "<w:style w:type=\"paragraph\" w:styleId=\"Heading1\"><w:name w:val=\"heading 1\"/><w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/><w:pPr><w:keepNext/><w:spacing w:before=\"320\" w:after=\"120\"/><w:outlineLvl w:val=\"0\"/></w:pPr><w:rPr><w:b/><w:color w:val=\"" + Cor + "\"/><w:sz w:val=\"30\"/></w:rPr></w:style>" +
            "<w:style w:type=\"paragraph\" w:styleId=\"Heading2\"><w:name w:val=\"heading 2\"/><w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/><w:pPr><w:keepNext/><w:spacing w:before=\"200\" w:after=\"80\"/><w:outlineLvl w:val=\"1\"/></w:pPr><w:rPr><w:b/><w:sz w:val=\"24\"/></w:rPr></w:style>" +
            "<w:style w:type=\"paragraph\" w:styleId=\"ListBullet\"><w:name w:val=\"List Bullet\"/><w:basedOn w:val=\"Normal\"/><w:pPr><w:tabs><w:tab w:val=\"left\" w:pos=\"284\"/></w:tabs><w:spacing w:after=\"40\"/><w:ind w:left=\"284\" w:hanging=\"284\"/></w:pPr></w:style>" +
            "<w:style w:type=\"table\" w:styleId=\"TableGrid\"><w:name w:val=\"Table Grid\"/><w:tblPr><w:tblCellMar><w:left w:w=\"80\" w:type=\"dxa\"/><w:right w:w=\"80\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr></w:style>" +
            "</w:styles>";

        private static void W(ZipArchive zip, string name, string content)
        {
            var e = zip.CreateEntry(name, CompressionLevel.Optimal);
            using (var w = new StreamWriter(e.Open(), new UTF8Encoding(false))) w.Write(content);
        }
    }
}
