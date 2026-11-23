using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using RevitDwgUnifier.Infrastructure;

namespace RevitDwgUnifier.Export
{
    /// <summary>
    /// Export Setup management: lists the DWG export setups saved in the project
    /// (Manage &gt; ... &gt; Export Setups DWG/DXF) - those carry the category -&gt; layer name,
    /// ACI colour, lineweight, linetype and hatch mapping tables - and reports the active one.
    /// </summary>
    public static class ExportSetupService
    {
        public const string InSessionSetupLabel = "<In-session setup>";

        public static IList<string> GetSetupNames(Document doc)
        {
            var names = new List<string>();
            try
            {
                // Names of all predefined DWG/DXF/DGN setups stored in the document.
                names.AddRange(BaseExportOptions.GetPredefinedSetupNames(doc));
            }
            catch (Exception ex)
            {
                Logger.Warn("GetPredefinedSetupNames failed: " + ex.Message);
            }

            // ExportDWGSettings elements are the DWG-specific subset; prefer them when present so
            // DGN-only setups are not offered for DWG export.
            try
            {
                var dwgNames = new FilteredElementCollector(doc).OfClass(typeof(ExportDWGSettings))
                    .Cast<ExportDWGSettings>().Select(s => s.Name).ToList();
                if (dwgNames.Count > 0) names = names.Intersect(dwgNames).Union(dwgNames).ToList();
            }
            catch (Exception ex)
            {
                Logger.Warn("Collecting ExportDWGSettings failed: " + ex.Message);
            }

            names = names.Distinct().OrderBy(n => n).ToList();
            names.Insert(0, InSessionSetupLabel);
            return names;
        }

        /// <summary>Name of the setup currently marked active in the project, if any.</summary>
        public static string GetActiveSetupName(Document doc)
        {
            try
            {
                var active = ExportDWGSettings.GetActivePredefinedSettings(doc);
                return active?.Name;
            }
            catch
            {
                return null;
            }
        }
    }
}
