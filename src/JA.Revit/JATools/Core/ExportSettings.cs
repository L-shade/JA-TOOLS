using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using RevitDwgUnifier.Contracts;
using RevitDwgUnifier.Infrastructure;

namespace RevitDwgUnifier.Core
{
    public enum CoordinateBase { InternalOrigin, Shared }

    /// <summary>User-facing settings, persisted per Windows user between sessions.</summary>
    [DataContract]
    public sealed class ExportSettings
    {
        // ---------------- Output
        [DataMember] public string OutputFolder { get; set; }
        [DataMember] public string OutputFileName { get; set; }
        [DataMember] public bool AllowOverwrite { get; set; }
        [DataMember] public string DwgVersion { get; set; }          // "2018" | "2013" | "2010" | "2007"

        // ---------------- Export setup (category -> layer / colour / lineweight mapping)
        /// <summary>Name of a saved Revit DWG export setup; empty = "&lt;in-session&gt;" defaults.</summary>
        [DataMember] public string ExportSetupName { get; set; }
        [DataMember] public string TargetUnit { get; set; }          // "Default" | "Millimeter" | ... (ExportUnit name)
        [DataMember] public CoordinateBase CoordinateBase { get; set; }

        // ---------------- Standalone views
        /// <summary>Title block family *type* used for the temporary sheet of a standalone view.
        /// -1 = no title block (a plain border is drawn in the merge step).</summary>
        [DataMember] public long TitleBlockTypeId { get; set; }
        /// <summary>Pick the smallest type of the same title block family that fits the viewport.</summary>
        [DataMember] public bool AutoFitTitleBlock { get; set; }

        // ---------------- Pitfall switches (Revit side)
        [DataMember] public bool EnforceCropRegions { get; set; }
        [DataMember] public bool EnforceAnnotationCrop { get; set; }
        [DataMember] public bool HideCropBoundary { get; set; }
        [DataMember] public bool ExactText { get; set; }
        [DataMember] public bool MapMissingFonts { get; set; }
        [DataMember] public string FallbackFont { get; set; }
        /// <summary>User font map "Revit font=CAD font" pairs, e.g. "ISOCPEUR=Arial".</summary>
        [DataMember] public List<string> FontMap { get; set; }
        [DataMember] public string HatchPatternFile { get; set; }
        [DataMember] public bool SkipViewsAlreadyOnSelectedSheets { get; set; }
        /// <summary>Window/door book: one layout tab per sheet viewport, named by the esquadria mark.</summary>
        [DataMember] public bool SplitSheetsByViewport { get; set; }
        /// <summary>Convert raster images (logos...) to embedded OLE objects in a full-AutoCAD pass.</summary>
        [DataMember] public bool EmbedImagesAsOle { get; set; }
        /// <summary>Sheet layout tabs named by sheet number only ("A102") instead of "A102 - Name".</summary>
        [DataMember] public bool SheetTabNameIsNumber { get; set; }

        // ---------------- Merge-side options (forwarded verbatim into the manifest)
        [DataMember] public MergeOptions Merge { get; set; }

        // ---------------- Merge engine
        /// <summary>Optional explicit path to accoreconsole.exe; empty = auto-detect newest AutoCAD.</summary>
        [DataMember] public string CoreConsolePath { get; set; }
        [DataMember] public int MergeTimeoutMinutes { get; set; }
        [DataMember] public bool KeepIntermediateFiles { get; set; }

        public static ExportSettings CreateDefault() => new ExportSettings().ApplyDefaults();

        /// <summary>DataContract deserialisation skips constructors/initialisers, so defaults
        /// are (re)applied explicitly after loading for every member that came back empty.</summary>
        public ExportSettings ApplyDefaults()
        {
            if (string.IsNullOrEmpty(OutputFolder))
                OutputFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(DwgVersion)) DwgVersion = "2018";
            if (string.IsNullOrEmpty(TargetUnit)) TargetUnit = "Default";
            if (string.IsNullOrEmpty(FallbackFont)) FallbackFont = "Arial";
            if (FontMap == null) FontMap = new List<string>();
            if (Merge == null) Merge = new MergeOptions();
            if (MergeTimeoutMinutes <= 0) MergeTimeoutMinutes = 20;
            if (TitleBlockTypeId == 0) TitleBlockTypeId = -1;
            if (!_initialised)
            {
                // First run only (bools default to false in DataContract land).
                EnforceCropRegions = true;
                EnforceAnnotationCrop = true;
                HideCropBoundary = true;
                ExactText = true;
                MapMissingFonts = true;
                SkipViewsAlreadyOnSelectedSheets = true;
                AutoFitTitleBlock = true;
                _initialised = true;
            }
            // Settings saved by older versions lack newer members, and DataContract leaves them
            // at false/0 instead of their defaults: upgrade once per schema version.
            if (_schema < 3)
            {
                // v3: viewports are rebuilt in the "activate" step; re-enable it even if an
                // earlier crash recovery switched it off.
                Merge.ActivateViewports = true;
                _schema = 3;
            }
            if (_schema < 4)
            {
                // v4: each sheet becomes one layout identical to the Revit sheet (same viewports,
                // same positions), and the tab is named by the sheet number.
                SplitSheetsByViewport = false;
                SheetTabNameIsNumber = true;
                _schema = 4;
            }
            if (_schema < 5)
            {
                EmbedImagesAsOle = true;
                _schema = 5;
            }
            if (_schema < 6)
            {
                Merge.DimTextColorIndex = 140;
                Merge.UniformViewportSize = true;
                _schema = 6;
            }
            if (_schema < 7)
            {
                Merge.PaperUnits = "Millimeter";
                _schema = 7;
            }
            if (_schema < 8)
            {
                // v8: Padrão JA (pilares exploded, hatch / PD-area layers) on by default
                Merge.JaStandards = true;
                _schema = 8;
            }
            if (_schema < 9)
            {
                // v9: Model Space assembled like the sheets (title block + views), not scattered
                Merge.ModelSheets = true;
                Merge.ModelSheetsPerRow = 20;
                _schema = 9;
            }
            if (_schema < 10)
            {
                // v10: notes and tags of the building services on the "JA - INST_..." layers
                Merge.InstallationLayers = true;
                _schema = 10;
            }
            if (Merge.InstallationLayerTable == null || Merge.InstallationLayerTable.Count == 0)
                Merge.InstallationLayerTable = RevitDwgUnifier.Contracts.InstallationRules.Defaults();
            if (_schema < 11)
            {
                // v11: luminaires on their own layer (JA - INST_ILUM), checked before JA - INST_ELET
                var ilum = RevitDwgUnifier.Contracts.InstallationRules.Defaults()
                    .First(l => l.Name == RevitDwgUnifier.Contracts.InstallationRules.IlumLayer);
                var table = Merge.InstallationLayerTable;
                if (!table.Any(l => string.Equals(l?.Name?.Trim(), ilum.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    int at = table.FindIndex(l => (l?.Name ?? "").ToUpperInvariant().Contains("INST_ELET"));
                    table.Insert(at < 0 ? table.Count : at, ilum);
                }
                foreach (var l in table.Where(l => (l?.Name ?? "").ToUpperInvariant().Contains("INST_ELET") && l.Keywords != null))
                    l.Keywords.RemoveAll(k => string.Equals(k, "ILUMIN", StringComparison.OrdinalIgnoreCase));
                _schema = 11;
            }
            if (_schema < 12)
            {
                // v12: object colours ByLayer and viewports on a non-plottable layer, always on
                Merge.ColorsByLayer = true;
                Merge.ViewportsNoPlot = true;
                _schema = 12;
            }
            if (string.IsNullOrWhiteSpace(Merge.ViewportLayerName)) Merge.ViewportLayerName = "JA - VIEWPORT";
            // filled per export from the open project: never carried over from a saved file
            Merge.InstallationNotes = new List<RevitDwgUnifier.Contracts.InstallationText>();
            Merge.InstallationTags = new List<RevitDwgUnifier.Contracts.InstallationText>();
            Merge.TagLayers = new List<string>();
            Merge.InstallationLayerMoves = new List<RevitDwgUnifier.Contracts.InstallationLayerMove>();
            Merge.InstallationCategoryLayers = new List<string>();
            if (Merge.ModelSheetsPerRow <= 0) Merge.ModelSheetsPerRow = 20;
            if (string.IsNullOrWhiteSpace(Merge.ColumnHatchLayer)) Merge.ColumnHatchLayer = "JA - EST_PILAR_HATCH";
            if (Merge.ColumnHatchColor < 1 || Merge.ColumnHatchColor > 255) Merge.ColumnHatchColor = 6;
            if (string.IsNullOrWhiteSpace(Merge.AreaTextLayer)) Merge.AreaTextLayer = "JA - TXT_AREA_CONSTR";
            if (Merge.AreaTextColor < 1 || Merge.AreaTextColor > 255) Merge.AreaTextColor = 22;
            if (Merge.ColumnLayers == null) Merge.ColumnLayers = new List<string>();
            if (string.IsNullOrEmpty(Merge.PaperUnits)) Merge.PaperUnits = "Model";
            return this;
        }

        [DataMember] private bool _initialised;
        [DataMember] private int _schema;
    }

    public static class SettingsStore
    {
        private static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RevitDwgUnifier", "settings.json");

        public static ExportSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                    return ContractJson.Read<ExportSettings>(SettingsPath).ApplyDefaults();
            }
            catch (Exception ex)
            {
                Logger.Warn("Settings file unreadable, using defaults: " + ex.Message);
            }
            return ExportSettings.CreateDefault();
        }

        public static void Save(ExportSettings settings)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                ContractJson.Write(SettingsPath, settings);
            }
            catch (Exception ex)
            {
                Logger.Warn("Could not save settings: " + ex.Message);
            }
        }
    }
}
