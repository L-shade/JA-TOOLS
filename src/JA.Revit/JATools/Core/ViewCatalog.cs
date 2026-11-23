using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitDwgUnifier.Core
{
    /// <summary>Lightweight, Revit-free description of an exportable view/sheet (safe to bind in WPF).</summary>
    public sealed class ViewInfo
    {
        public long Id { get; set; }
        public bool IsSheet { get; set; }
        public string Name { get; set; }
        public string SheetNumber { get; set; }
        public string ViewTypeName { get; set; }
        public int Scale { get; set; }
        /// <summary>For views: the sheet the view is placed on (if any).</summary>
        public long HostSheetId { get; set; } = -1;
        public string HostSheetLabel { get; set; }
        /// <summary>For sheets: ids of the views placed on it.</summary>
        public List<long> PlacedViewIds { get; set; } = new List<long>();

        public string DisplayName => IsSheet ? SheetNumber + " - " + Name : Name;
        public string Group => IsSheet ? "Folhas" : ViewTypeName;
    }

    /// <summary>Collects every view/sheet the tool can export and their sheet relationships.</summary>
    public static class ViewCatalog
    {
        /// <summary>View types supported as standalone items (placed on a temporary sheet).</summary>
        public static readonly HashSet<ViewType> SupportedViewTypes = new HashSet<ViewType>
        {
            ViewType.FloorPlan, ViewType.CeilingPlan, ViewType.AreaPlan, ViewType.EngineeringPlan,
            ViewType.Section, ViewType.Elevation, ViewType.Detail, ViewType.DraftingView,
            ViewType.Legend, ViewType.ThreeD
        };

        public static List<ViewInfo> Collect(Document doc)
        {
            var result = new List<ViewInfo>();

            // view id -> sheet (a model view can be on at most one sheet; legends on many,
            // we only remember the first one, it is just a UI hint).
            var viewToSheet = new Dictionary<long, ViewSheet>();
            var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Where(s => !s.IsPlaceholder && !s.IsTemplate)
                .OrderBy(s => s.SheetNumber, new NaturalComparer())
                .ToList();

            foreach (var sheet in sheets)
            {
                var info = new ViewInfo
                {
                    Id = sheet.Id.Value,
                    IsSheet = true,
                    Name = sheet.Name,
                    SheetNumber = sheet.SheetNumber,
                    ViewTypeName = "Sheet"
                };
                foreach (ElementId vpId in sheet.GetAllViewports())
                {
                    if (doc.GetElement(vpId) is Viewport vp)
                    {
                        info.PlacedViewIds.Add(vp.ViewId.Value);
                        if (!viewToSheet.ContainsKey(vp.ViewId.Value)) viewToSheet[vp.ViewId.Value] = sheet;
                    }
                }
                result.Add(info);
            }

            var views = new FilteredElementCollector(doc).OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate && v.CanBePrinted && SupportedViewTypes.Contains(v.ViewType))
                .OrderBy(v => v.ViewType.ToString())
                .ThenBy(v => v.Name, new NaturalComparer());

            foreach (var v in views)
            {
                var info = new ViewInfo
                {
                    Id = v.Id.Value,
                    IsSheet = false,
                    Name = v.Name,
                    ViewTypeName = Friendly(v.ViewType),
                    Scale = SafeScale(v)
                };
                if (viewToSheet.TryGetValue(v.Id.Value, out var host))
                {
                    info.HostSheetId = host.Id.Value;
                    info.HostSheetLabel = host.SheetNumber;
                }
                result.Add(info);
            }
            return result;
        }

        public static int SafeScale(View v)
        {
            try { return v.Scale; } catch { return 0; } // some 3D/perspective views throw
        }

        private static string Friendly(ViewType t)
        {
            switch (t)
            {
                case ViewType.FloorPlan: return "Plantas de piso";
                case ViewType.CeilingPlan: return "Plantas de forro";
                case ViewType.AreaPlan: return "Plantas de área";
                case ViewType.EngineeringPlan: return "Plantas estruturais";
                case ViewType.Section: return "Cortes";
                case ViewType.Elevation: return "Elevações";
                case ViewType.Detail: return "Vistas de detalhe";
                case ViewType.DraftingView: return "Vistas de desenho";
                case ViewType.Legend: return "Legendas";
                case ViewType.ThreeD: return "Vistas 3D";
                default: return t.ToString();
            }
        }

        /// <summary>Sorts "A-2" before "A-10" like the Revit project browser.</summary>
        private sealed class NaturalComparer : IComparer<string>
        {
            public int Compare(string a, string b)
            {
                if (a == b) return 0;
                if (a == null) return -1;
                if (b == null) return 1;
                int i = 0, j = 0;
                while (i < a.Length && j < b.Length)
                {
                    if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                    {
                        int si = i, sj = j;
                        while (i < a.Length && char.IsDigit(a[i])) i++;
                        while (j < b.Length && char.IsDigit(b[j])) j++;
                        string na = a.Substring(si, i - si).TrimStart('0');
                        string nb = b.Substring(sj, j - sj).TrimStart('0');
                        if (na.Length != nb.Length) return na.Length.CompareTo(nb.Length);
                        int c = string.CompareOrdinal(na, nb);
                        if (c != 0) return c;
                    }
                    else
                    {
                        int c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                        if (c != 0) return c;
                        i++; j++;
                    }
                }
                return a.Length.CompareTo(b.Length);
            }
        }
    }
}
