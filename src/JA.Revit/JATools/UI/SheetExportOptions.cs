using System.Globalization;
using RevitDwgUnifier.Tools.Common;

namespace RevitDwgUnifier.UI
{
    /// <summary>
    /// Exportar Pranchas: which formats to produce and the PDF / file-name options.
    /// (The DWG único options stay in ExportSettings, shared with the merge job.)
    /// Remembered in the JA Tools preferences.
    /// </summary>
    public sealed class SheetExportOptions
    {
        public bool Pdf = true;
        public bool DwgUnified = true;
        public bool DwgPerSheet;
        public bool Png;
        public int PngPixels = 4000;
        public bool PngTransparent = true;
        public bool PdfCombine;
        public string PdfCombinedName = "{codigo} {projeto} - Pranchas";
        public int PdfColorIndex;
        public string FilePattern = "{numero} - {nome}";

        private const string P = "ExportarPranchas.";

        public static SheetExportOptions Load()
        {
            var o = new SheetExportOptions();
            o.Pdf = Bool("pdf", o.Pdf);
            o.DwgUnified = Bool("dwgUnified", o.DwgUnified);
            o.DwgPerSheet = Bool("dwg", o.DwgPerSheet);
            o.Png = Bool("png", o.Png);
            o.PngTransparent = Bool("pngTransparent", o.PngTransparent);
            if (int.TryParse(ToolPrefs.Get(P + "pngPixels"), out int px) && px >= 200 && px <= 15000) o.PngPixels = px;
            o.PdfCombine = Bool("combine", o.PdfCombine);
            o.PdfCombinedName = ToolPrefs.Get(P + "combinedName") ?? o.PdfCombinedName;
            o.FilePattern = ToolPrefs.Get(P + "pattern") ?? o.FilePattern;
            if (int.TryParse(ToolPrefs.Get(P + "colorIndex"), out int c) && c >= 0 && c <= 2) o.PdfColorIndex = c;
            return o;
        }

        public void Save()
        {
            ToolPrefs.Set(P + "pdf", Pdf ? "1" : "0");
            ToolPrefs.Set(P + "dwgUnified", DwgUnified ? "1" : "0");
            ToolPrefs.Set(P + "dwg", DwgPerSheet ? "1" : "0");
            ToolPrefs.Set(P + "png", Png ? "1" : "0");
            ToolPrefs.Set(P + "pngTransparent", PngTransparent ? "1" : "0");
            ToolPrefs.Set(P + "pngPixels", PngPixels.ToString(CultureInfo.InvariantCulture));
            ToolPrefs.Set(P + "combine", PdfCombine ? "1" : "0");
            ToolPrefs.Set(P + "combinedName", PdfCombinedName ?? "");
            ToolPrefs.Set(P + "pattern", FilePattern ?? "");
            ToolPrefs.Set(P + "colorIndex", PdfColorIndex.ToString(CultureInfo.InvariantCulture));
            ToolPrefs.Save();
        }

        private static bool Bool(string key, bool def)
        {
            string v = ToolPrefs.Get(P + key);
            return v == "1" ? true : v == "0" ? false : def;
        }
    }
}
