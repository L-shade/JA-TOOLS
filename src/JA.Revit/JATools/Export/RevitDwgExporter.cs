using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using RevitDwgUnifier.Infrastructure;

namespace RevitDwgUnifier.Export
{
    /// <summary>
    /// Thin, defensive wrapper around Document.Export(folder, name, views, DWGExportOptions).
    ///
    /// Each item is exported ALONE into its own empty sub-folder. Reasons:
    ///  * Revit decorates file names differently across versions / when several views are
    ///    exported in one call (project name prefixes, "-Sheet - " infixes). Scanning an empty
    ///    folder for the produced *.dwg is version-proof.
    ///  * One failing view (corrupt family, out-of-memory on a huge 3D view) does not take the
    ///    rest of the batch with it.
    ///  * Side files Revit may write (*.pcp layer reports, xref DWGs if someone changes
    ///    MergedViews back) cannot collide between items.
    /// </summary>
    public static class RevitDwgExporter
    {
        public static string ExportSingle(Document doc, ElementId viewOrSheetId, string itemFolder,
                                          string baseName, DWGExportOptions options)
        {
            Directory.CreateDirectory(itemFolder);
            string safeName = FileSafety.SanitizeFileName(baseName, 60);

            bool ok = doc.Export(itemFolder, safeName, new List<ElementId> { viewOrSheetId }, options);
            if (!ok)
                throw new InvalidOperationException("Revit reported a failed DWG export for element " + viewOrSheetId.Value);

            var dwgs = Directory.GetFiles(itemFolder, "*.dwg", SearchOption.TopDirectoryOnly);
            if (dwgs.Length == 0)
                throw new FileNotFoundException("Export finished but no DWG was produced in " + itemFolder);

            // With MergedViews=true there is exactly one; if xrefs were produced anyway the
            // host file is the one whose name starts with our requested name.
            string main = dwgs.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f)
                                                       .Equals(safeName, StringComparison.OrdinalIgnoreCase))
                          ?? dwgs.OrderBy(f => Path.GetFileName(f).Length).First();
            if (dwgs.Length > 1)
                Logger.Warn($"Export of {viewOrSheetId.Value} produced {dwgs.Length} DWGs (xrefs?). Using {Path.GetFileName(main)}; " +
                            "remaining xrefs will be bound by the merge engine.");

            var fi = new FileInfo(main);
            if (fi.Length < 1024) Logger.Warn($"{fi.Name} is suspiciously small ({fi.Length} bytes) - view may be empty.");
            return main;
        }
    }
}
