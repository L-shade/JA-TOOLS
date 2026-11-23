using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using RevitDwgUnifier.Contracts;
using RevitDwgUnifier.Core;
using RevitDwgUnifier.Infrastructure;

namespace RevitDwgUnifier.Export
{
    /// <summary>
    /// PITFALL #1 - Title block &amp; model mismatch / displacement (Revit half).
    ///
    /// Revit has three "origins": the Internal Origin (fixed, what the API calls 0,0,0),
    /// the Project Base Point (PBP) and the Survey Point (SP, carries shared coordinates).
    /// DWG export writes either internal coordinates (SharedCoords = false) or shared
    /// coordinates (SharedCoords = true). Classic failures:
    ///   * Shared coordinates of a georeferenced site are kilometres away from 0,0. The
    ///     geometry lands "miles away" in AutoCAD, ZOOM EXTENTS shows a dot, and double
    ///     precision jitter appears on linework (AutoCAD is comfortable up to ~1e7 units).
    ///   * Views/sheets exported with different bases do not line up with each other.
    ///
    /// What this class does:
    ///   1. Every item of one job is exported with the SAME base (one DWGExportOptions instance).
    ///   2. The PBP/SP/shared offsets are measured and written into the manifest; the merge
    ///      engine stores them in the DWG (named-object dictionary "RVT_UNIFY") so the true
    ///      position can always be recovered, even after tiling.
    ///   3. Large offsets produce a warning recommending Internal Origin + Tiled arrangement
    ///      (the merge engine then also re-bases each item's model content near 0,0, and the
    ///      viewports are re-targeted by the same vector, so titleblock and views stay aligned).
    /// </summary>
    public static class CoordinateService
    {
        /// <summary>Beyond this distance (metres) from 0,0 we warn about precision/visibility.</summary>
        private const double FarFromOriginMeters = 10000.0;

        public static CoordinateInfo Describe(Document doc, CoordinateBase coordinateBase, ExportUnit unit,
                                              List<string> warnings)
        {
            var info = new CoordinateInfo
            {
                Base = coordinateBase == CoordinateBase.Shared ? "Shared" : "Internal",
                DwgUnits = unit.ToString()
            };

            try
            {
                BasePoint pbp = BasePoint.GetProjectBasePoint(doc);
                BasePoint sp = BasePoint.GetSurveyPoint(doc);
                double f = FeetToDwgUnits(unit);

                info.ProjectBasePointX = pbp.Position.X * f;
                info.ProjectBasePointY = pbp.Position.Y * f;
                info.SurveyPointX = sp.Position.X * f;
                info.SurveyPointY = sp.Position.Y * f;

                ProjectPosition pos = doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero);
                info.SharedEastWest = pos.EastWest * f;
                info.SharedNorthSouth = pos.NorthSouth * f;
                info.SharedElevation = pos.Elevation * f;
                info.SharedAngleRadians = pos.Angle;

                double sharedOffsetM = UnitUtils.ConvertFromInternalUnits(
                    Math.Sqrt(pos.EastWest * pos.EastWest + pos.NorthSouth * pos.NorthSouth), UnitTypeId.Meters);
                double pbpOffsetM = UnitUtils.ConvertFromInternalUnits(
                    Math.Sqrt(pbp.Position.X * pbp.Position.X + pbp.Position.Y * pbp.Position.Y), UnitTypeId.Meters);

                Logger.Info($"Coordinates: base={info.Base}, PBP offset from internal origin={pbpOffsetM:0.###} m, " +
                            $"shared offset={sharedOffsetM:0.###} m, true-north angle={pos.Angle * 180 / Math.PI:0.####} deg");

                if (coordinateBase == CoordinateBase.Shared && sharedOffsetM > FarFromOriginMeters)
                    warnings.Add($"As coordenadas compartilhadas colocam o modelo a {sharedOffsetM / 1000:0.0} km da origem do DWG. " +
                                 "Use o arranjo lado a lado (padrão) ou a origem interna para evitar imprecisão; " +
                                 "as coordenadas reais ficam no dicionário RVT_UNIFY do DWG.");
                if (coordinateBase == CoordinateBase.InternalOrigin && pbpOffsetM > FarFromOriginMeters)
                    warnings.Add($"O modelo está a {pbpOffsetM / 1000:0.0} km da origem interna do Revit. " +
                                 "Considere reposicioná-lo no Revit; o arranjo lado a lado compensa no DWG.");
            }
            catch (Exception ex)
            {
                Logger.Warn("Could not read base/survey points: " + ex.Message);
            }
            return info;
        }

        /// <summary>Revit internal length unit is decimal feet.</summary>
        public static double FeetToDwgUnits(ExportUnit unit)
        {
            switch (unit)
            {
                case ExportUnit.Inch: return 12.0;
                case ExportUnit.Millimeter: return 304.8;
                case ExportUnit.Centimeter: return 30.48;
                case ExportUnit.Meter: return 0.3048;
                case ExportUnit.Foot: return 1.0;
                default: return 1.0; // "Default" follows project units; offsets are informational only
            }
        }
    }
}
