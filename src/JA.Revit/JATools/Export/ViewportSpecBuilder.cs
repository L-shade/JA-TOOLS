using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using RevitDwgUnifier.Core;
using RevitDwgUnifier.Contracts;
using RevitDwgUnifier.Infrastructure;

namespace RevitDwgUnifier.Export
{
    /// <summary>
    /// Window/door book support: describes every viewport of a sheet (box on the sheet, view,
    /// scale) and finds the window/door ("esquadria") each view shows, so the merge engine can
    /// create one layout tab per viewport named after the esquadria's mark.
    ///
    /// Mark resolution, per view:
    ///   * Legend views (typical "caderno de esquadrias"): the family type shown by each legend
    ///     component (parameter "Component Type").
    ///   * Model views (elevations, sections, callouts, plans): the window/door instances visible
    ///     in the view; the one closest to the centre of the crop region wins, so neighbours that
    ///     leak into the crop do not steal the name.
    ///   * Name = Type Mark of that type; fallback instance Mark; fallback the view name.
    /// </summary>
    public static class ViewportSpecBuilder
    {
        private static readonly BuiltInCategory[] EsquadriaCategories =
        {
            BuiltInCategory.OST_Windows, BuiltInCategory.OST_Doors
        };

        public static List<ViewportSpec> Build(Document doc, ViewSheet sheet)
        {
            var specs = new List<ViewportSpec>();
            foreach (ElementId vpId in sheet.GetAllViewports())
            {
                try
                {
                    if (!(doc.GetElement(vpId) is Viewport vp)) continue;
                    if (!(doc.GetElement(vp.ViewId) is View view)) continue;

                    Outline box = vp.GetBoxOutline();
                    string mark = ResolveMark(doc, view);
                    specs.Add(new ViewportSpec
                    {
                        ViewId = view.Id.Value.ToString(),
                        ViewName = view.Name,
                        Mark = mark,
                        LayoutName = string.IsNullOrWhiteSpace(mark) ? view.Name : mark,
                        Scale = ViewCatalog.SafeScale(view),
                        MinX = box.MinimumPoint.X,
                        MinY = box.MinimumPoint.Y,
                        MaxX = box.MaximumPoint.X,
                        MaxY = box.MaximumPoint.Y
                    });
                    if (string.IsNullOrWhiteSpace(mark))
                        Logger.Info($"Sheet {sheet.SheetNumber}: no window/door mark found in view '{view.Name}', tab uses the view name.");
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Sheet {sheet.SheetNumber}: viewport {vpId.Value} skipped for tab naming ({ex.Message}).");
                }
            }
            return specs;
        }

        public static string ResolveMark(Document doc, View view)
        {
            try
            {
                string instanceMark = null;
                ElementId typeId = view.ViewType == ViewType.Legend
                    ? FromLegend(doc, view)
                    : FromModelView(doc, view, out instanceMark);
                if (typeId == ElementId.InvalidElementId) return null;

                string typeMark = doc.GetElement(typeId)?.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_MARK)?.AsString();
                if (!string.IsNullOrWhiteSpace(typeMark)) return typeMark.Trim();
                if (!string.IsNullOrWhiteSpace(instanceMark)) return instanceMark.Trim();
            }
            catch (Exception ex)
            {
                Logger.Debug($"Mark resolution failed for view '{view.Name}': {ex.Message}");
            }
            return null;
        }

        /// <summary>Most frequent window/door type among the legend components of the view.</summary>
        private static ElementId FromLegend(Document doc, View legend)
        {
            var types = new FilteredElementCollector(doc, legend.Id)
                .OfCategory(BuiltInCategory.OST_LegendComponents)
                .WhereElementIsNotElementType()
                .Select(e => e.get_Parameter(BuiltInParameter.LEGEND_COMPONENT)?.AsElementId())
                .Where(id => id != null && id != ElementId.InvalidElementId)
                .Where(id => IsEsquadriaType(doc.GetElement(id)))
                .GroupBy(id => id.Value)
                .OrderByDescending(g => g.Count())
                .FirstOrDefault();
            return types?.First() ?? ElementId.InvalidElementId;
        }

        private static bool IsEsquadriaType(Element type)
        {
            var cat = type?.Category;
            if (cat == null) return false;
            long c = cat.Id.Value;
            return EsquadriaCategories.Any(b => (long)b == c);
        }

        /// <summary>Window/door instance closest to the crop centre (in the view plane).</summary>
        private static ElementId FromModelView(Document doc, View view, out string instanceMark)
        {
            instanceMark = null;
            var instances = new FilteredElementCollector(doc, view.Id)
                .WherePasses(new ElementMulticategoryFilter(EsquadriaCategories.ToList()))
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>()
                .ToList();
            if (instances.Count == 0) return ElementId.InvalidElementId;

            FamilyInstance best = instances[0];
            BoundingBoxXYZ crop = view.CropBoxActive ? view.CropBox : null;
            if (crop != null && instances.Count > 1)
            {
                Transform toView = crop.Transform.Inverse;
                XYZ c = (crop.Min + crop.Max) * 0.5;
                double bestD = double.MaxValue;
                foreach (var fi in instances)
                {
                    BoundingBoxXYZ bb = fi.get_BoundingBox(null);
                    if (bb == null) continue;
                    XYZ p = toView.OfPoint((bb.Min + bb.Max) * 0.5);
                    double d = (p.X - c.X) * (p.X - c.X) + (p.Y - c.Y) * (p.Y - c.Y);
                    if (d < bestD) { bestD = d; best = fi; }
                }
            }
            instanceMark = best.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();
            return best.GetTypeId();
        }
    }
}
