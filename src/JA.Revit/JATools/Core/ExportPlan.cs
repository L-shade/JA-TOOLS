using System.Collections.Generic;
using System.Linq;
using RevitDwgUnifier.Infrastructure;

namespace RevitDwgUnifier.Core
{
    public enum ExportItemKind { Sheet, View }

    /// <summary>One future layout tab in the unified DWG.</summary>
    public sealed class PlannedItem
    {
        public ExportItemKind Kind { get; set; }
        public ViewInfo Info { get; set; }
        public string Key => (Kind == ExportItemKind.Sheet ? "S-" : "V-") + Info.Id;
        /// <summary>Sheet tabs: "A102" (default) or "A102 - CADERNO DE ESQUADRIA".</summary>
        public bool SheetNumberOnly { get; set; } = true;
        public string LayoutName => Kind == ExportItemKind.Sheet
            ? (SheetNumberOnly || string.IsNullOrWhiteSpace(Info.Name) ? Info.SheetNumber : Info.SheetNumber + " - " + Info.Name)
            : Info.Name;
    }

    /// <summary>
    /// PITFALL #6 - Sheet / model space duplication (planning half).
    ///
    /// The cheapest duplicate is the one never exported. Before anything touches Revit:
    ///  * every id is unique (double-selection through filters / pre-selection is collapsed);
    ///  * a view that is ALSO placed on one of the selected sheets is dropped as a standalone
    ///    item (it already arrives through that sheet's viewport), unless the user disables
    ///    "skip views already on selected sheets" because they want a dedicated layout for it;
    ///  * each sheet is exported exactly once, so the model content behind its viewports lands
    ///    in model space once. Different layouts that need the same content therefore share it
    ///    (per item tile) rather than receiving copies.
    /// The second half (geometric de-duplication of coincident entities) runs in the merge
    /// engine, see DuplicateRemover in the AutoCAD project.
    /// </summary>
    public static class ExportPlanner
    {
        public static List<PlannedItem> Build(IEnumerable<ViewInfo> selection, bool skipViewsOnSelectedSheets,
                                              bool sheetNumberOnly = true)
        {
            var unique = selection.GroupBy(v => v.Id).Select(g => g.First()).ToList();
            var sheets = unique.Where(v => v.IsSheet).ToList();
            var viewsCoveredBySheets = new HashSet<long>(sheets.SelectMany(s => s.PlacedViewIds));

            var plan = new List<PlannedItem>();
            plan.AddRange(sheets.Select(s => new PlannedItem { Kind = ExportItemKind.Sheet, Info = s, SheetNumberOnly = sheetNumberOnly }));

            foreach (var v in unique.Where(v => !v.IsSheet))
            {
                if (skipViewsOnSelectedSheets && viewsCoveredBySheets.Contains(v.Id))
                {
                    Logger.Info("Skipping standalone export of view '" + v.Name +
                                "': it is already placed on a selected sheet (no duplicate model content).");
                    continue;
                }
                plan.Add(new PlannedItem { Kind = ExportItemKind.View, Info = v });
            }
            return plan;
        }
    }
}
