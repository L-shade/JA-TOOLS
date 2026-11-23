// ---------------------------------------------------------------------------------
//  MergeManifest.cs  (linked into BOTH the Revit add-in and the AutoCAD merge engine)
//
//  This is the contract between the two halves of the tool:
//    Revit side  -> writes manifest.json (what was exported, where, and how to merge)
//    AutoCAD side-> reads manifest.json, builds the master DWG, writes result.json
//
//  DataContractJsonSerializer is used on purpose: it ships with both .NET Framework 4.8
//  and .NET 8, so neither host needs an extra JSON dependency (no binding-redirect pain
//  inside Revit or AutoCAD, which both load their own Newtonsoft versions).
// ---------------------------------------------------------------------------------
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace RevitDwgUnifier.Contracts
{
    public static class ModelSpaceArrangement
    {
        /// <summary>Each item's model content is moved into its own non-overlapping tile
        /// near the DWG origin. Default: safest, no stacking of plans from different levels.</summary>
        public const string Tiled = "Tiled";

        /// <summary>Model content keeps the coordinates Revit exported (internal or shared).
        /// Only sensible when items do not spatially overlap (or for coordination use).</summary>
        public const string TrueCoordinates = "TrueCoordinates";
    }

    [DataContract]
    public sealed class MergeManifest
    {
        [DataMember(Order = 0)] public string SchemaVersion { get; set; } = "1.0";
        [DataMember(Order = 1)] public string SourceProject { get; set; }
        [DataMember(Order = 2)] public string OutputDwgPath { get; set; }
        [DataMember(Order = 3)] public string ResultPath { get; set; }
        [DataMember(Order = 4)] public string LogPath { get; set; }
        [DataMember(Order = 5)] public List<MergeItem> Items { get; set; } = new List<MergeItem>();
        [DataMember(Order = 6)] public MergeOptions Options { get; set; } = new MergeOptions();
        [DataMember(Order = 7)] public CoordinateInfo Coordinates { get; set; } = new CoordinateInfo();
        /// <summary>Append-only "START key" / "DONE key" / "STEP id" markers written by the engine.
        /// If AutoCAD dies with a native fatal error (no .NET exception, no result.json), the Revit
        /// side reads this file to find the culprit, removes it and retries the merge.</summary>
        [DataMember(Order = 8)] public string ProgressPath { get; set; }
        /// <summary>Persistent folder (next to the final DWG) that receives a copy of every raster
        /// image the sheets reference. Image references are re-pathed to it, so the DWG keeps its
        /// images even when they are not embedded as OLE objects. Empty = paths only made absolute.</summary>
        [DataMember(Order = 9)] public string ImageFolder { get; set; }
    }

    /// <summary>
    /// Second, optional pass run in FULL AutoCAD (acad.exe): converts every raster image
    /// reference into an embedded OLE object at the same place. Core Console cannot do it: OLE
    /// needs the Windows clipboard and an OLE container, which only the UI application has.
    /// The result is written as a <see cref="MergeResult"/>.
    /// </summary>
    [DataContract]
    public sealed class OleManifest
    {
        [DataMember(Order = 0)] public string InputDwgPath { get; set; }
        [DataMember(Order = 1)] public string OutputDwgPath { get; set; }
        [DataMember(Order = 2)] public string ResultPath { get; set; }
        [DataMember(Order = 3)] public string LogPath { get; set; }
        [DataMember(Order = 4)] public string DwgVersion { get; set; }
        /// <summary>Longest side, in pixels, of the bitmap embedded per image (keeps the DWG small).</summary>
        [DataMember(Order = 5)] public int MaxPixels { get; set; } = 3000;
    }

    /// <summary>Writes/reads the crash-recovery markers (one line per event, flushed immediately).</summary>
    public static class MergeProgress
    {
        public const string Start = "START ";
        public const string Done = "DONE ";
        public const string Step = "STEP ";

        public static void Mark(string path, string line)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { File.AppendAllText(path, line + "\n", Encoding.UTF8); } catch { }
        }

        /// <summary>Returns the last marker that was started but never completed
        /// ("START key" or "STEP id"), or null when nothing is pending.</summary>
        public static string FindUnfinished(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            string pending = null;
            foreach (var raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.StartsWith(Start) || line.StartsWith(Step)) pending = line;
                else if (line.StartsWith(Done) && pending != null && line.Substring(Done.Length) == pending) pending = null;
            }
            return pending;
        }
    }

    [DataContract]
    public sealed class MergeItem
    {
        /// <summary>Stable unique key, e.g. "S-123456" (sheet) or "V-98765" (view on temp sheet).</summary>
        [DataMember(Order = 0)] public string Key { get; set; }
        [DataMember(Order = 1)] public string Kind { get; set; }          // "Sheet" | "View"
        [DataMember(Order = 2)] public string RevitElementId { get; set; }
        [DataMember(Order = 3)] public string SourceDwgPath { get; set; }
        [DataMember(Order = 4)] public string LayoutName { get; set; }
        [DataMember(Order = 5)] public int ViewScale { get; set; }        // 0 when not meaningful (sheets)
        [DataMember(Order = 6)] public bool HasTitleBlock { get; set; }
        [DataMember(Order = 7)] public string Title { get; set; }
        [DataMember(Order = 8)] public List<string> HostedViewIds { get; set; } = new List<string>();
        /// <summary>Viewports of a sheet as Revit sees them (used to split the sheet into one
        /// layout per viewport and to name each tab, e.g. by the window/door type mark).</summary>
        [DataMember(Order = 9)] public List<ViewportSpec> Viewports { get; set; } = new List<ViewportSpec>();
    }

    [DataContract]
    public sealed class ViewportSpec
    {
        [DataMember(Order = 0)] public string ViewId { get; set; }
        [DataMember(Order = 1)] public string ViewName { get; set; }
        /// <summary>Window/door mark found in the view (type mark first, then instance mark).</summary>
        [DataMember(Order = 2)] public string Mark { get; set; }
        /// <summary>Tab name to use for this viewport's layout.</summary>
        [DataMember(Order = 3)] public string LayoutName { get; set; }
        [DataMember(Order = 4)] public int Scale { get; set; }
        // Viewport box on the Revit sheet (Revit sheet units). Only relative positions are used:
        // they are matched to the DWG viewports after normalising both sets to their union box.
        [DataMember(Order = 5)] public double MinX { get; set; }
        [DataMember(Order = 6)] public double MinY { get; set; }
        [DataMember(Order = 7)] public double MaxX { get; set; }
        [DataMember(Order = 8)] public double MaxY { get; set; }
    }

    [DataContract]
    public sealed class MergeOptions
    {
        [DataMember(Order = 0)] public string Arrangement { get; set; } = ModelSpaceArrangement.Tiled;
        /// <summary>Gap between tiles in DWG units. 0 = automatic (25% of the larger tile side).</summary>
        [DataMember(Order = 1)] public double TileGap { get; set; } = 0;
        [DataMember(Order = 2)] public int TilesPerRow { get; set; } = 4;

        // --- Pitfall #1: titleblock / viewport displacement
        [DataMember(Order = 3)] public bool NormalizePaperOrigin { get; set; } = true;
        [DataMember(Order = 4)] public bool AddOriginMarkers { get; set; } = false;

        // --- Pitfall #2: blocks / xrefs
        [DataMember(Order = 5)] public bool BindXrefs { get; set; } = true;
        [DataMember(Order = 6)] public bool ExplodeAnonymousBlocks { get; set; } = true;
        [DataMember(Order = 7)] public bool RenameConflictingBlocks { get; set; } = true;
        [DataMember(Order = 8)] public bool PurgeUnused { get; set; } = true;

        // --- Pitfall #3: text
        [DataMember(Order = 9)] public bool RepairTextStyles { get; set; } = true;
        [DataMember(Order = 10)] public string FallbackTrueTypeFace { get; set; } = "Arial";
        [DataMember(Order = 11)] public string FallbackTrueTypeFile { get; set; } = "arial.ttf";
        [DataMember(Order = 12)] public bool ForceTrueTypeForUnicode { get; set; } = true;
        /// <summary>Extra MText width (fraction, e.g. 0.05 = +5%) to absorb font-metric differences. 0 = off.</summary>
        [DataMember(Order = 13)] public double MTextWidthSlack { get; set; } = 0;

        // --- Pitfall #4: hatches
        [DataMember(Order = 14)] public bool NormalizeHatches { get; set; } = true;
        [DataMember(Order = 15)] public int MaxHatchLines { get; set; } = 20000;

        // --- Pitfall #5: stray geometry
        [DataMember(Order = 16)] public bool CullOutsideViewports { get; set; } = true;
        /// <summary>Extra margin around each viewport window, as a fraction of the window size.</summary>
        [DataMember(Order = 17)] public double CullMargin { get; set; } = 0.05;

        // --- Pitfall #6: duplication
        [DataMember(Order = 18)] public bool RemoveDuplicateEntities { get; set; } = true;

        // --- Output
        [DataMember(Order = 19)] public string DwgVersion { get; set; } = "2018"; // Current|2018|2013|2010|2007
        [DataMember(Order = 20)] public bool DeleteDefaultLayouts { get; set; } = true;

        // --- Window/door book: one layout tab per sheet viewport
        [DataMember(Order = 21)] public bool SplitSheetsByViewport { get; set; } = false;
        /// <summary>Make each layout current once and switch its viewports ON before saving.</summary>
        [DataMember(Order = 22)] public bool ActivateViewports { get; set; } = true;

        /// <summary>ACI colour (1-255) given to every dimension text (DIMCLRT); 0 = keep Revit's.</summary>
        [DataMember(Order = 23)] public int DimTextColorIndex { get; set; } = 140;
        /// <summary>On a sheet with several viewports, shrink the oversized ones (Revit crop much
        /// larger than the drawing) to the size of the smallest one, around their content.</summary>
        [DataMember(Order = 24)] public bool UniformViewportSize { get; set; } = true;

        /// <summary>Unit of paper space: "Model" (same as model space, Revit's default) or
        /// Millimeter | Centimeter | Meter | Inch | Foot. Viewports keep Revit's scale either way.</summary>
        [DataMember(Order = 25)] public string PaperUnits { get; set; } = "Millimeter";

        // --- Padrão JA (office CAD standard)
        /// <summary>Explode column / structural column blocks (pilares) and move their hatches to
        /// <see cref="ColumnHatchLayer"/>; put the ceiling height (PD) and area (m²) texts of the
        /// rooms on <see cref="AreaTextLayer"/>.</summary>
        [DataMember(Order = 26)] public bool JaStandards { get; set; } = true;
        /// <summary>Layers the Revit export setup gives to Columns / Structural Columns (projection and cut).</summary>
        [DataMember(Order = 27)] public List<string> ColumnLayers { get; set; } = new List<string>();
        [DataMember(Order = 28)] public string ColumnHatchLayer { get; set; } = "JA - EST_PILAR_HATCH";
        [DataMember(Order = 29)] public int ColumnHatchColor { get; set; } = 6;
        [DataMember(Order = 30)] public string AreaTextLayer { get; set; } = "JA - TXT_AREA_CONSTR";
        [DataMember(Order = 31)] public int AreaTextColor { get; set; } = 22;

        // --- Model Space laid out like the sheets
        /// <summary>Model Space shows every sheet assembled (title block + views at the real size of its main
        /// view), side by side in rows, instead of the views scattered; the layouts look at it.</summary>
        [DataMember(Order = 32)] public bool ModelSheets { get; set; } = true;
        [DataMember(Order = 33)] public int ModelSheetsPerRow { get; set; } = 20;

        // --- Padrão JA: instalações (see InstallationLayers.cs)
        /// <summary>Text notes and tags of the building services go to the "JA - INST_..." layers.</summary>
        [DataMember(Order = 34)] public bool InstallationLayers { get; set; } = true;
        /// <summary>The office layers (name, colour ID, linetype, words of the discipline).</summary>
        [DataMember(Order = 35)] public List<InstallationLayer> InstallationLayerTable { get; set; } = InstallationRules.Defaults();
        /// <summary>Filled by Revit at export: the texts of the installation notes and their layer.</summary>
        [DataMember(Order = 36)] public List<InstallationText> InstallationNotes { get; set; } = new List<InstallationText>();
        /// <summary>Filled by Revit at export: the texts of the installation tags and their layer.</summary>
        [DataMember(Order = 37)] public List<InstallationText> InstallationTags { get; set; } = new List<InstallationText>();
        /// <summary>Filled by Revit at export: the DWG layers of every tag category.</summary>
        [DataMember(Order = 38)] public List<string> TagLayers { get; set; } = new List<string>();
        /// <summary>Filled by Revit at export: tag category layers whose tags are all of one discipline.</summary>
        [DataMember(Order = 39)] public List<InstallationLayerMove> InstallationLayerMoves { get; set; } = new List<InstallationLayerMove>();
        /// <summary>Filled by Revit at export: the DWG layers of the families that stay blocks —
        /// building services (fixtures, pipes, luminaires, devices, ducts...) and furniture
        /// (mobiliário, marcenaria, equipamentos). Their blocks are never exploded.</summary>
        [DataMember(Order = 40)] public List<string> InstallationCategoryLayers { get; set; } = new List<string>();

        // --- colours and plotting
        /// <summary>Object colour ByLayer on every entity (blocks, attributes, dimension lines, MText colour codes).</summary>
        [DataMember(Order = 41)] public bool ColorsByLayer { get; set; } = true;
        /// <summary>The layer of the layout viewports is non-plottable (viewports on shared layers move to <see cref="ViewportLayerName"/>).</summary>
        [DataMember(Order = 42)] public bool ViewportsNoPlot { get; set; } = true;
        [DataMember(Order = 43)] public string ViewportLayerName { get; set; } = "JA - VIEWPORT";
    }

    /// <summary>
    /// "DWG por prancha" files exported straight by Revit, post-processed in place by AutoCAD Core
    /// Console (command RVTSTANDARDS) with the office standard (<see cref="MergeOptions.JaStandards"/>).
    /// The result is written as a <see cref="MergeResult"/>.
    /// </summary>
    [DataContract]
    public sealed class StandardsManifest
    {
        [DataMember(Order = 0)] public List<string> Files { get; set; } = new List<string>();
        [DataMember(Order = 1)] public string ResultPath { get; set; }
        [DataMember(Order = 2)] public string LogPath { get; set; }
        [DataMember(Order = 3)] public MergeOptions Options { get; set; } = new MergeOptions();
    }

    [DataContract]
    public sealed class CoordinateInfo
    {
        [DataMember(Order = 0)] public string Base { get; set; }                 // "Internal" | "Shared"
        [DataMember(Order = 1)] public string DwgUnits { get; set; }
        [DataMember(Order = 2)] public double ProjectBasePointX { get; set; }    // in DWG units, internal coords
        [DataMember(Order = 3)] public double ProjectBasePointY { get; set; }
        [DataMember(Order = 4)] public double SurveyPointX { get; set; }
        [DataMember(Order = 5)] public double SurveyPointY { get; set; }
        [DataMember(Order = 6)] public double SharedEastWest { get; set; }       // shared offset of internal origin
        [DataMember(Order = 7)] public double SharedNorthSouth { get; set; }
        [DataMember(Order = 8)] public double SharedElevation { get; set; }
        [DataMember(Order = 9)] public double SharedAngleRadians { get; set; }
    }

    [DataContract]
    public sealed class MergeResult
    {
        [DataMember(Order = 0)] public bool Success { get; set; }
        [DataMember(Order = 1)] public string OutputDwgPath { get; set; }
        [DataMember(Order = 2)] public List<string> LayoutsCreated { get; set; } = new List<string>();
        [DataMember(Order = 3)] public List<string> FailedItems { get; set; } = new List<string>();
        [DataMember(Order = 4)] public List<string> Warnings { get; set; } = new List<string>();
        [DataMember(Order = 5)] public List<string> Errors { get; set; } = new List<string>();
        [DataMember(Order = 6)] public List<string> Statistics { get; set; } = new List<string>();
        /// <summary>Raster image references in the saved DWG (merge) / images converted (OLE pass).</summary>
        [DataMember(Order = 7)] public int ImageCount { get; set; }
        /// <summary>OLE pass only: image instances that could not be converted (kept as references).</summary>
        [DataMember(Order = 8)] public int ImagesFailed { get; set; }
    }

    /// <summary>JSON (de)serialisation helpers shared by both sides.</summary>
    public static class ContractJson
    {
        public static void Write<T>(string path, T value)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var ms = new MemoryStream())
            {
                serializer.WriteObject(ms, value);
                // Write via temp + replace so a crash never leaves a half-written manifest.
                string tmp = path + ".tmp";
                File.WriteAllBytes(tmp, ms.ToArray());
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
        }

        public static T Read<T>(string path)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                return (T)serializer.ReadObject(fs);
            }
        }

        public static string ToJson<T>(T value)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var ms = new MemoryStream())
            {
                serializer.WriteObject(ms, value);
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }
    }
}
