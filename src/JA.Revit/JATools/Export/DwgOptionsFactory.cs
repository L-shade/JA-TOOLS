using System;
using System.IO;
using Autodesk.Revit.DB;
using RevitDwgUnifier.Core;
using RevitDwgUnifier.Infrastructure;

namespace RevitDwgUnifier.Export
{
    /// <summary>
    /// Builds the single DWGExportOptions instance used for EVERY item of a job.
    ///
    /// Start point = the user's saved Export Setup (keeps their category -&gt; layer/colour/
    /// lineweight mapping, linetype and hatch tables), then a small set of properties is
    /// forced because the merge step depends on them. Each forced value documents which
    /// pitfall it neutralises.
    /// </summary>
    public static class DwgOptionsFactory
    {
        public static DWGExportOptions Create(Document doc, ExportSettings s)
        {
            DWGExportOptions opt = null;

            if (!string.IsNullOrEmpty(s.ExportSetupName) && s.ExportSetupName != ExportSetupService.InSessionSetupLabel)
            {
                try
                {
                    // Pulls layer table, colours, lineweights, linetype/pattern/font tables of that setup.
                    opt = DWGExportOptions.GetPredefinedOptions(doc, s.ExportSetupName);
                    Logger.Info("Using DWG export setup: " + s.ExportSetupName);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Export setup '{s.ExportSetupName}' could not be loaded ({ex.Message}); using defaults.");
                }
            }
            if (opt == null)
            {
                opt = new DWGExportOptions();
                Logger.Info("Using in-session DWG export defaults.");
            }

            // ---------------------------------------------------------------- PITFALL #2
            // MergedViews = true  ==  UNTICK "Export views on sheets and links as external references".
            // With xrefs, every viewport becomes its own DWG attached to the sheet DWG; merging
            // those means re-pathing/binding dozens of xrefs and produces "$0$" layer names.
            // Merged views put all content into the sheet DWG itself, which the merge engine
            // can then insert cleanly (layers keep their export-setup names).
            opt.MergedViews = true;

            // Export AutoCAD Architecture/MEP objects as plain geometry, not AEC objects. AEC
            // objects open as proxy objects in vanilla AutoCAD (cannot be edited / exploded and
            // generate anonymous blocks when bound).
            opt.ACAPreference = ACAObjectPreference.Geometry;

            // 2D drawings: polymesh, never ACIS solids (ACIS in 2D views produce 3DSOLID blobs).
            opt.ExportOfSolids = SolidGeometry.Polymesh;

            // ---------------------------------------------------------------- PITFALL #1
            // One coordinate base for the whole job (see CoordinateService for the reasoning).
            opt.SharedCoords = s.CoordinateBase == CoordinateBase.Shared;
            if (Enum.TryParse(s.TargetUnit, out ExportUnit unit)) opt.TargetUnit = unit;

            // ---------------------------------------------------------------- PITFALL #3
            // Exact: the DWG reproduces Revit's line breaks and width factor instead of
            // letting AutoCAD re-wrap MText with its own metrics (the #1 cause of overflow).
            opt.TextTreatment = s.ExactText ? TextTreatment.Exact : TextTreatment.Approximate;
            FontMappingService.Apply(doc, opt, s);

            // ---------------------------------------------------------------- PITFALL #4
            // Several layouts at different scales live in ONE drawing, so linetype (and dash-
            // pattern) scaling must be per viewport: PaperSpace => PSLTSCALE=1, LTSCALE=1.
            // "ModelSpace" or "ViewScale" bake one view's scale into the whole drawing, which is
            // only right for single-view files.
            opt.LineScaling = LineScaling.PaperSpace;

            // Optional hatch pattern mapping file (maps Revit fill patterns to standard AutoCAD
            // pattern names such as ANSI31/AR-CONC). Standard names matter: the merge engine can
            // only re-scale a hatch whose pattern AutoCAD can look up in acad(iso).pat.
            if (!string.IsNullOrWhiteSpace(s.HatchPatternFile))
            {
                if (File.Exists(s.HatchPatternFile)) opt.HatchPatternsFileName = s.HatchPatternFile;
                else Logger.Warn("Hatch pattern mapping file not found: " + s.HatchPatternFile);
            }

            // ---------------------------------------------------------------- PITFALL #5
            // Elements that are notorious for living far outside the crop region and inflating
            // model-space extents: scope boxes, reference planes and unreferenced view tags
            // (section/elevation markers of views that are on no sheet).
            opt.HideScopeBox = true;
            opt.HideReferencePlane = true;
            opt.HideUnreferenceViewTags = true;

            // ---------------------------------------------------------------- PITFALL #6
            // Do not keep overlapping coincident lines (Revit otherwise exports, e.g., a wall
            // cut line and a floor edge on top of each other).
            opt.PreserveCoincidentLines = false;

            // ---------------------------------------------------------------- Output version
            opt.FileVersion = MapVersion(s.DwgVersion);

            Logger.Info($"DWG options: MergedViews={opt.MergedViews}, SharedCoords={opt.SharedCoords}, " +
                        $"Unit={opt.TargetUnit}, Text={opt.TextTreatment}, LineScaling={opt.LineScaling}, " +
                        $"Version={opt.FileVersion}");
            return opt;
        }

        public static ACADVersion MapVersion(string v)
        {
            switch (v)
            {
                case "2007": return ACADVersion.R2007;
                case "2010": return ACADVersion.R2010;
                case "2013": return ACADVersion.R2013;
                default: return ACADVersion.R2018;
            }
        }
    }
}
