using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitDwgUnifier.Tools.Exporting
{
    /// <summary>File naming with the office standard: tokens replaced from the sheet / view and
    /// the project information.</summary>
    public static class ExportNaming
    {
        public const string Help = "{numero} {nome} {revisao} {projeto} {codigo} {data}";

        public static string Name(Document doc, View v, string pattern)
        {
            ProjectInfo pi = doc.ProjectInformation;
            var sheet = v as ViewSheet;
            string rev = sheet?.get_Parameter(BuiltInParameter.SHEET_CURRENT_REVISION)?.AsString() ?? "";
            string s = (pattern ?? "{numero} - {nome}")
                .Replace("{numero}", sheet?.SheetNumber ?? "")
                .Replace("{nome}", v.Name)
                .Replace("{revisao}", rev)
                .Replace("{projeto}", pi?.Name ?? "")
                .Replace("{codigo}", pi?.Number ?? "")
                .Replace("{data}", DateTime.Now.ToString("yyyy-MM-dd"));
            return Infrastructure.FileSafety.SanitizeFileName(s.Trim(' ', '-', '_'), 150);
        }

        public static string DefaultFolder(Document doc)
        {
            string baseDir = !string.IsNullOrEmpty(doc.PathName) ? Path.GetDirectoryName(doc.PathName)
                                                                   : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return Path.Combine(baseDir ?? "", "Exportação JA");
        }

        public static PDFExportOptions Pdf(int colorIndex, bool hideCrop)
        {
            return new PDFExportOptions
            {
                Combine = true,
                PaperFormat = ExportPaperFormat.Default,
                ZoomType = ZoomType.Zoom,
                ZoomPercentage = 100,
                ColorDepth = colorIndex == 1 ? ColorDepthType.GrayScale : colorIndex == 2 ? ColorDepthType.BlackLine : ColorDepthType.Color,
                RasterQuality = RasterQualityType.High,
                HideCropBoundaries = hideCrop,
                HideScopeBoxes = true,
                HideReferencePlane = true,
                HideUnreferencedViewTags = true
            };
        }

        public static readonly string[] ColorChoices = { "Colorido", "Tons de cinza", "Preto e branco" };
    }

    /// <summary>
    /// PNG of sheets and views (1.17: was the Exportar Vistas button, now the "PNG" format of
    /// Exportar Pranchas): Revit image export renamed to the office pattern, optionally with the
    /// white made transparent.
    /// </summary>
    public static class PngExport
    {
        /// <summary>Revit appends " - &lt;type&gt; - &lt;view&gt;" to the file name: export under a
        /// temporary stem, then rename the produced file to the wanted name.</summary>
        public static string ExportPng(Document Doc, View v, string folder, string name, int pixels)
        {
            string stem = "~ja" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var o = new ImageExportOptions
            {
                ExportRange = ExportRange.SetOfViews,
                FilePath = Path.Combine(folder, stem),
                HLRandWFViewsFileType = ImageFileType.PNG,
                ShadowViewsFileType = ImageFileType.PNG,
                ImageResolution = ImageResolution.DPI_300,
                ZoomType = ZoomFitType.FitToPage,
                FitDirection = FitDirectionType.Horizontal,
                PixelSize = pixels
            };
            o.SetViewsAndSheets(new List<ElementId> { v.Id });
            Doc.ExportImage(o);

            string produced = Directory.GetFiles(folder, stem + "*.png").OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
            if (produced == null) return null;
            string target = Path.Combine(folder, name + ".png");
            if (File.Exists(target)) File.Delete(target);
            File.Move(produced, target);
            return target;
        }

        /// <summary>Near-white pixels become fully transparent; anti-aliased edges keep a
        /// proportional alpha so lines stay smooth on any background.</summary>
        public static void MakeWhiteTransparent(string file)
        {
            BitmapSource src;
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read))
            {
                var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                src = new FormatConvertedBitmap(dec.Frames[0], PixelFormats.Bgra32, null, 0);
            }
            int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
            var px = new byte[h * stride];
            src.CopyPixels(px, stride, 0);
            for (int i = 0; i < px.Length; i += 4)
            {
                int min = Math.Min(px[i], Math.Min(px[i + 1], px[i + 2]));      // whiteness = darkest channel
                if (min >= 250) { px[i + 3] = 0; continue; }
                if (min > 200)
                {
                    // Partially white: alpha from whiteness, colour "un-blended" from white.
                    double a = (255 - min) / 55.0;
                    px[i + 3] = (byte)(255 * a);
                    for (int c = 0; c < 3; c++) px[i + c] = (byte)Math.Max(0, Math.Min(255, 255 - (255 - px[i + c]) / Math.Max(a, 0.01)));
                }
            }
            var outBmp = BitmapSource.Create(w, h, src.DpiX, src.DpiY, PixelFormats.Bgra32, null, px, stride);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(outBmp));
            using (var fs = new FileStream(file, FileMode.Create, FileAccess.Write)) enc.Save(fs);
        }
    }
}
