using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using RevitDwgUnifier.Core;
using RevitDwgUnifier.Infrastructure;

namespace RevitDwgUnifier.Export
{
    /// <summary>Result of putting a standalone view on a temporary sheet.</summary>
    public sealed class StagedSheet
    {
        public ElementId SheetId { get; set; }
        public ElementId PlacedViewId { get; set; }
        public bool HasTitleBlock { get; set; }
        public bool UsedDuplicate { get; set; }
    }

    /// <summary>
    /// All model changes the export needs are made here, INSIDE a TransactionGroup that the
    /// job always rolls back (see UnifiedExportJob). Nothing in the user's project survives
    /// the export: no temporary sheets, no crop changes, no duplicated views.
    ///
    /// Why a temporary sheet for standalone views?  Exporting a bare view writes only model
    /// space. Placing it on a sheet first lets Revit itself produce the Paper Space layout with
    /// a viewport at the exact view scale, the real title block, and correct rotation. The
    /// merge engine then treats views and sheets with one single, well-tested code path.
    /// </summary>
    public static class StagingService
    {
        // ------------------------------------------------------------------ PITFALL #5
        /// <summary>
        /// Crop enforcement. A view whose crop region is OFF exports everything it can "see":
        /// the whole building, far-away site elements, linked models, stray detail lines left
        /// at 0,0 years ago... and they all land in model space, ruining extents and tiling.
        ///  * Crop region ON  -> model geometry clipped to the crop boundary by Revit itself.
        ///  * Annotation crop ON -> tags/dims/grids outside the annotation boundary are not exported.
        ///  * Crop boundary hidden -> the crop rectangle itself is not exported as a stray frame.
        /// The merge engine adds a second guard (CropCuller) that deletes any model-space entity
        /// lying completely outside every viewport window of its item.
        /// </summary>
        public static void EnforceCrop(View view, ExportSettings s)
        {
            if (view == null || view is ViewSheet) return;
            if (view.ViewType == ViewType.DraftingView || view.ViewType == ViewType.Legend) return;

            Parameter cropParam = view.get_Parameter(BuiltInParameter.VIEWER_CROP_REGION);
            if (cropParam == null || cropParam.IsReadOnly) return; // view type has no crop region

            // Workshared model: editing a view borrowed by someone else would make the WHOLE
            // staging transaction fail at commit. Leave such views as they are (merge-side
            // culling still removes stray geometry).
            Document doc = view.Document;
            if (doc.IsWorkshared &&
                WorksharingUtils.GetCheckoutStatus(doc, view.Id) == CheckoutStatus.OwnedByOtherUser)
            {
                Logger.Warn($"View '{view.Name}' is borrowed by another user; crop not enforced.");
                return;
            }

            try
            {
                if (s.EnforceCropRegions && !view.CropBoxActive)
                {
                    view.CropBoxActive = true;
                    Logger.Warn($"View '{view.Name}': crop region was OFF, enabled for export (temporary).");
                }

                if (s.EnforceAnnotationCrop)
                {
                    Parameter ann = view.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE);
                    if (ann != null && !ann.IsReadOnly && ann.AsInteger() == 0) ann.Set(1);
                }

                if (s.HideCropBoundary && view.CropBoxVisible) view.CropBoxVisible = false;
            }
            catch (Exception ex)
            {
                Logger.Warn($"View '{view.Name}': crop enforcement failed ({ex.Message}).");
            }
        }

        /// <summary>Creates a temporary sheet for a standalone view and places it centred on
        /// the title block (auto-fitting the title block size when requested).</summary>
        public static StagedSheet StageView(Document doc, View view, ExportSettings s)
        {
            ElementId tbTypeId = s.TitleBlockTypeId > 0 ? new ElementId(s.TitleBlockTypeId) : ElementId.InvalidElementId;
            if (tbTypeId != ElementId.InvalidElementId && !(doc.GetElement(tbTypeId) is FamilySymbol))
            {
                Logger.Warn("Configured title block type no longer exists; staging without title block.");
                tbTypeId = ElementId.InvalidElementId;
            }
            if (tbTypeId != ElementId.InvalidElementId)
            {
                var sym = (FamilySymbol)doc.GetElement(tbTypeId);
                if (!sym.IsActive) sym.Activate();
            }

            ViewSheet sheet = ViewSheet.Create(doc, tbTypeId);
            TrySetSheetIdentity(sheet, view);

            // Views can sit on only one sheet (legends excepted). If it is already placed
            // somewhere, place a temporary duplicate that carries the same crop/scale/detailing.
            View toPlace = view;
            bool duplicated = false;
            if (!Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id))
            {
                if (view.CanViewBeDuplicated(ViewDuplicateOption.WithDetailing))
                {
                    toPlace = (View)doc.GetElement(view.Duplicate(ViewDuplicateOption.WithDetailing));
                    duplicated = true;
                    EnforceCrop(toPlace, s);
                    Logger.Info($"View '{view.Name}' already on a sheet: exported through a temporary duplicate.");
                }
                else
                {
                    throw new InvalidOperationException(
                        $"View '{view.Name}' cannot be placed on a sheet and cannot be duplicated.");
                }
            }

            Viewport vp = Viewport.Create(doc, sheet.Id, toPlace.Id, XYZ.Zero);
            doc.Regenerate();

            bool hasTb = false;
            FamilyInstance tb = GetTitleBlock(doc, sheet);
            if (tb != null)
            {
                hasTb = true;
                if (s.AutoFitTitleBlock) AutoFitTitleBlock(doc, tb, sheet, vp);
                BoundingBoxXYZ bb = tb.get_BoundingBox(sheet);
                if (bb != null)
                {
                    // Titleblock family origins are not always bottom-left; centring on the
                    // actual bounding box keeps the viewport inside the frame regardless.
                    vp.SetBoxCenter((bb.Min + bb.Max) * 0.5);
                    doc.Regenerate();
                }
            }

            return new StagedSheet
            {
                SheetId = sheet.Id,
                PlacedViewId = toPlace.Id,
                HasTitleBlock = hasTb,
                UsedDuplicate = duplicated
            };
        }

        private static void TrySetSheetIdentity(ViewSheet sheet, View view)
        {
            try { sheet.Name = view.Name; } catch { /* name validation edge cases */ }
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    sheet.SheetNumber = "UDWG-" + view.Id.Value + (attempt == 0 ? "" : "-" + attempt);
                    return;
                }
                catch (Autodesk.Revit.Exceptions.ArgumentException) { /* number in use, try next */ }
            }
        }

        public static FamilyInstance GetTitleBlock(Document doc, ViewSheet sheet)
        {
            return new FilteredElementCollector(doc, sheet.Id)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType()
                .Cast<FamilyInstance>()
                .FirstOrDefault();
        }

        /// <summary>
        /// Chooses the smallest type of the title block family (A3/A2/A1/A0, or Letter..E) whose
        /// frame contains the viewport with a 5% margin. Avoids the classic "view hangs off the
        /// sheet" layout when one fixed title block is used for every view.
        /// </summary>
        private static void AutoFitTitleBlock(Document doc, FamilyInstance tb, ViewSheet sheet, Viewport vp)
        {
            try
            {
                Outline vpOutline = vp.GetBoxOutline();
                double needW = (vpOutline.MaximumPoint.X - vpOutline.MinimumPoint.X) * 1.05;
                double needH = (vpOutline.MaximumPoint.Y - vpOutline.MinimumPoint.Y) * 1.05;

                Family family = tb.Symbol.Family;
                var candidates = new List<Tuple<ElementId, double, double>>();
                ElementId original = tb.GetTypeId();

                foreach (ElementId symId in family.GetFamilySymbolIds())
                {
                    var sym = (FamilySymbol)doc.GetElement(symId);
                    if (!sym.IsActive) sym.Activate();
                    tb.ChangeTypeId(symId);
                    doc.Regenerate();
                    BoundingBoxXYZ bb = tb.get_BoundingBox(sheet);
                    if (bb == null) continue;
                    candidates.Add(Tuple.Create(symId, bb.Max.X - bb.Min.X, bb.Max.Y - bb.Min.Y));
                }

                var fitting = candidates.Where(c => c.Item2 >= needW && c.Item3 >= needH)
                                        .OrderBy(c => c.Item2 * c.Item3).FirstOrDefault();
                var chosen = fitting ?? candidates.OrderByDescending(c => c.Item2 * c.Item3).FirstOrDefault();

                tb.ChangeTypeId(chosen?.Item1 ?? original);
                doc.Regenerate();

                if (fitting == null)
                    Logger.Warn($"No title block size fits view on sheet '{sheet.Name}'; the largest type was used.");
            }
            catch (Exception ex)
            {
                Logger.Warn("Title block auto-fit skipped: " + ex.Message);
            }
        }
    }
}
