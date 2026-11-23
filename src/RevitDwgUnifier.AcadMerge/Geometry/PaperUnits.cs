using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace RevitDwgUnifier.AcadMerge.Geometry
{
    /// <summary>
    /// Paper space in its own unit, independent of model space, e.g. model in METERS (1 unit =
    /// 1 m, as exported by Revit) and paper in MILLIMETERS (1 unit = 1 mm, the A4 sheet is
    /// 210 x 297), the usual CAD convention.
    ///
    /// Revit writes paper space in the same unit as model space, so an A4 sheet exported in
    /// meters is 0.21 x 0.297 and a 1:50 viewport has CustomScale 0.02. With f = (model unit) /
    /// (paper unit) (= 1000 for m -> mm) every paper-space object is scaled by f about 0,0, and
    /// each model viewport is re-framed so that it keeps showing exactly the same model area:
    ///
    ///     CenterPoint, Width, Height  x f      (frame on the bigger paper)
    ///     CustomScale                 x f      (0.02 -> 20 : still 1:50, now in mm per m)
    ///     ViewCenter, ViewTarget      unchanged (same model area, same position in the frame)
    ///
    /// so the drawing on paper is exactly as in Revit, at Revit's scale. Paper-space dimensions
    /// keep their displayed values (DIMLFAC / f) and their look (DIMSCALE x f); the plot scale
    /// of the layout is divided by f (e.g. "1000 mm = 1 unit" becomes "1 mm = 1 unit").
    /// LTSCALE stays 1: the linetype definitions are scaled instead (ScaleLinetypes).
    /// </summary>
    internal static class PaperUnits
    {
        public const string SameAsModel = "Model";

        /// <summary>Millimeters per unit for the names used in the settings / by Revit's ExportUnit.</summary>
        public static double? MmPerUnit(string name)
        {
            switch ((name ?? "").Trim().ToLowerInvariant())
            {
                case "millimeter": case "millimeters": case "mm": return 1.0;
                case "centimeter": case "centimeters": case "cm": return 10.0;
                case "decimeter": case "decimeters": case "dm": return 100.0;
                case "meter": case "meters": case "m": return 1000.0;
                case "inch": case "inches": case "in": return 25.4;
                case "foot": case "feet": case "ft": return 304.8;
                default: return null;
            }
        }

        public static double? MmPerUnit(UnitsValue u)
        {
            switch (u)
            {
                case UnitsValue.Millimeters: return 1.0;
                case UnitsValue.Centimeters: return 10.0;
                case UnitsValue.Decimeters: return 100.0;
                case UnitsValue.Meters: return 1000.0;
                case UnitsValue.Inches: return 25.4;
                case UnitsValue.Feet: return 304.8;
                default: return null;
            }
        }

        /// <summary>Paper scale factor f; 1 = paper stays in model units.</summary>
        public static double Factor(Database db, string paperUnits, string exportUnit, MergeLog log)
        {
            if (string.IsNullOrWhiteSpace(paperUnits) || paperUnits.Equals(SameAsModel, StringComparison.OrdinalIgnoreCase)) return 1.0;
            double? paper = MmPerUnit(paperUnits);
            if (!paper.HasValue)
            {
                log.Warn($"Paper unit '{paperUnits}' is unknown; paper space kept in model units.");
                return 1.0;
            }
            double? model = MmPerUnit(db.Insunits) ?? MmPerUnit(exportUnit);
            if (!model.HasValue)
            {
                log.Warn($"Model units of the DWG are unknown (INSUNITS={db.Insunits}, export unit '{exportUnit}'); " +
                         "paper space kept in model units. Choose a DWG unit in the export dialog.");
                return 1.0;
            }
            double f = model.Value / paper.Value;
            log.Info($"Paper units: model 1 unit = {model.Value} mm, paper 1 unit = {paper.Value} mm -> paper scaled x{f:0.######}.");
            return f;
        }

        public static void ScaleLayouts(Database db, IEnumerable<string> layoutNames, double f, MergeLog log)
        {
            if (Math.Abs(f - 1.0) < 1e-12) return;
            Matrix3d s = Matrix3d.Scaling(f, Point3d.Origin);
            int layouts = 0, entities = 0, viewports = 0, failed = 0;

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                foreach (string name in layoutNames)
                {
                    if (!dict.Contains(name)) continue;
                    var lay = (Layout)tr.GetObject(dict.GetAt(name), OpenMode.ForWrite);
                    var btr = (BlockTableRecord)tr.GetObject(lay.BlockTableRecordId, OpenMode.ForRead);
                    ObjectIdCollection lvps = lay.GetViewports();
                    ObjectId overall = lvps != null && lvps.Count > 0 ? lvps[0] : ObjectId.Null;

                    var ids = new List<ObjectId>();
                    foreach (ObjectId id in btr) if (!id.IsErased) ids.Add(id);

                    foreach (ObjectId id in ids)
                    {
                        try
                        {
                            var ent = (Entity)tr.GetObject(id, OpenMode.ForWrite, false);
                            if (ent is Viewport vp)
                            {
                                bool paperVp = id == overall || vp.Number == 1 ||
                                    (overall.IsNull && Math.Abs(vp.CustomScale - 1.0) < 1e-9 &&
                                     new Point2d(vp.CenterPoint.X, vp.CenterPoint.Y).GetDistanceTo(vp.ViewCenter) < 1e-6);
                                ScaleViewport(vp, f, paperVp);
                                if (!paperVp) viewports++;
                            }
                            else if (ent is Dimension dim)
                            {
                                double oldScale = dim.Dimscale;
                                dim.TransformBy(s);
                                if (oldScale > 0 && Math.Abs(dim.Dimscale - oldScale) < 1e-12) dim.Dimscale = oldScale * f;
                                dim.Dimlfac = dim.Dimlfac / f;   // same measured value as in Revit
                                try { dim.RecomputeDimensionBlock(true); } catch { }
                            }
                            else
                            {
                                ent.TransformBy(s);
                            }
                            entities++;
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            log.Info($"Layout '{name}': object not scaled to paper units ({ex.Message}).");
                        }
                    }

                    ScaleLayoutSettings(lay, f, log);
                    layouts++;
                }
                tr.Commit();
            }
            log.Stat($"Paper space scaled x{f:0.######}: {layouts} layout(s), {entities} object(s), {viewports} model viewport(s) " +
                     $"kept at Revit's scale" + (failed > 0 ? $", {failed} object(s) failed" : "") + ".");
        }

        /// <summary>
        /// Keeps LTSCALE = 1 with paper in another unit: Revit writes dash lengths as paper sizes
        /// in the export unit (a 3 mm dash = 0.003 when exporting in meters). With PSLTSCALE = 1
        /// AutoCAD draws dashes in paper units, so every definition is multiplied by f (0.003 ->
        /// 3 mm), dash lengths, pattern length and the scale / offset of text and shape elements.
        /// </summary>
        public static void ScaleLinetypes(Database db, double f, MergeLog log)
        {
            if (Math.Abs(f - 1.0) < 1e-12) return;
            int scaled = 0, failed = 0;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var lt = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
                foreach (ObjectId id in lt)
                {
                    var ltr = (LinetypeTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    string n = ltr.Name.ToUpperInvariant();
                    if (n == "BYLAYER" || n == "BYBLOCK" || n == "CONTINUOUS" || ltr.IsDependent || ltr.NumDashes == 0) continue;
                    try
                    {
                        ltr.UpgradeOpen();
                        for (int i = 0; i < ltr.NumDashes; i++)
                        {
                            ltr.SetDashLengthAt(i, ltr.DashLengthAt(i) * f);
                            if (!ltr.ShapeStyleAt(i).IsNull)
                            {
                                ltr.SetShapeScaleAt(i, ltr.ShapeScaleAt(i) * f);
                                Vector2d off = ltr.ShapeOffsetAt(i);
                                ltr.SetShapeOffsetAt(i, new Vector2d(off.X * f, off.Y * f));
                            }
                        }
                        ltr.PatternLength = ltr.PatternLength * f;
                        scaled++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        log.Info($"Linetype '{ltr.Name}' not scaled: {ex.Message}");
                    }
                }
                tr.Commit();
            }
            log.Stat($"Linetype definitions scaled x{f:0.######} for paper units (LTSCALE stays 1): {scaled}" +
                     (failed > 0 ? $", {failed} failed" : ""));
        }

        /// <summary>
        /// Model tab with the dashes of the sheets, LTSCALE untouched. The linetype definitions are paper sizes (in paper
        /// units, after ScaleLinetypes), right in the layouts (PSLTSCALE = 1) but meaningless in model space, where a 3 mm
        /// dash would be 3 m with paper in mm and model in m, or 3 mm of model at any scale. With MSLTSCALE = 1 AutoCAD
        /// multiplies the model-tab dashes by the current annotation scale (CANNOSCALE, drawing / paper units), and only
        /// on the Model tab: so one annotation scale per sheet scale is created ("1:50 (JA)" = 1 paper unit : 1 / CustomScale
        /// model units — 0,5 m per cm at 1:50 with model in m and paper in cm), the one of most sheets (the scale of
        /// each sheet's main viewport) is made current, and MSLTSCALE is set to 1. In the model tab the dashes then look
        /// as on paper; for a sheet at another scale, pick its "1:n (JA)" in the annotation scale list of the status bar.
        /// Layouts, entities and LTSCALE do not change.
        /// </summary>
        public static void ModelComoPapel(Database db, double f, IEnumerable<string> layoutNames, MergeLog log)
        {
            // the scale of each sheet = its largest model viewport; how many sheets use each
            var porEscala = new Dictionary<double, (int folhas, double area)>();
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                foreach (string name in layoutNames)
                {
                    if (!dict.Contains(name)) continue;
                    var lay = (Layout)tr.GetObject(dict.GetAt(name), OpenMode.ForRead);
                    var btr = (BlockTableRecord)tr.GetObject(lay.BlockTableRecordId, OpenMode.ForRead);
                    Viewport maior = null;
                    ObjectIdCollection lvps = lay.GetViewports();
                    ObjectId overall = lvps != null && lvps.Count > 0 ? lvps[0] : ObjectId.Null;
                    foreach (ObjectId id in btr)
                    {
                        if (id.IsErased || id == overall || !(tr.GetObject(id, OpenMode.ForRead) is Viewport vp)) continue;
                        if (vp.Number == 1 || vp.CustomScale <= 1e-12) continue;
                        if (Math.Abs(vp.CustomScale - 1.0) < 1e-9 && new Point2d(vp.CenterPoint.X, vp.CenterPoint.Y).GetDistanceTo(vp.ViewCenter) < 1e-6) continue;   // the sheet's own
                        if (maior == null || vp.Width * vp.Height > maior.Width * maior.Height) maior = vp;
                    }
                    if (maior == null) continue;
                    double fator = Arredondar(1.0 / maior.CustomScale);
                    porEscala.TryGetValue(fator, out var v);
                    porEscala[fator] = (v.folhas + 1, v.area + maior.Width * maior.Height);
                }
                tr.Commit();
            }
            if (porEscala.Count == 0) { log.Info("Model linetypes: no model viewport, annotation scale left as it is."); return; }

            double principal = porEscala.OrderByDescending(x => x.Value.folhas).ThenByDescending(x => x.Value.area).First().Key;
            try
            {
                ObjectContextCollection occ = db.ObjectContextManager.GetContextCollection("ACDB_ANNOTATIONSCALES");
                AnnotationScale atual = null;
                foreach (double fator in porEscala.Keys.OrderBy(x => x))
                {
                    double den = fator * f;                     // 1:den in real lengths
                    string nome = "1:" + Arredondar(den).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " (JA)";
                    AnnotationScale sc = null;
                    if (occ.HasContext(nome)) sc = occ.GetContext(nome) as AnnotationScale;
                    if (sc == null)
                    {
                        sc = new AnnotationScale { Name = nome, PaperUnits = 1.0, DrawingUnits = fator };
                        occ.AddContext(sc);
                        sc = occ.GetContext(nome) as AnnotationScale ?? sc;
                    }
                    if (Math.Abs(fator - principal) < 1e-12) atual = sc;
                }
                if (atual != null) db.Cannoscale = atual;
                log.Stat($"Model linetypes as on paper (LTSCALE stays 1): MSLTSCALE = 1, annotation scale {atual?.Name}" +
                         (porEscala.Count > 1 ? $"; other sheet scales in the annotation scale list: {string.Join(", ", porEscala.Keys.Where(k => Math.Abs(k - principal) > 1e-12).Select(k => "1:" + Arredondar(k * f).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)))}" : ""));
            }
            catch (Exception ex) { log.Warn("Model linetypes: annotation scale not set (" + ex.Message + ")."); }
            Msltscale(db, log);
        }

        /// <summary>MSLTSCALE = 1 in the drawing's header (no managed property: through the system variable of the working database).</summary>
        private static void Msltscale(Database db, MergeLog log)
        {
            PropertyInfo p = typeof(Database).GetProperty("Msltscale");
            if (p != null && p.CanWrite)
            {
                try { p.SetValue(db, p.PropertyType == typeof(bool) ? (object)true : 1, null); return; } catch { }
            }
            Database antes = HostApplicationServices.WorkingDatabase;
            try
            {
                HostApplicationServices.WorkingDatabase = db;
                Autodesk.AutoCAD.ApplicationServices.Core.Application.SetSystemVariable("MSLTSCALE", 1);
            }
            catch (Exception ex) { log.Info("MSLTSCALE not set (" + ex.Message + "); AutoCAD's default is 1."); }
            finally
            {
                try { if (antes != null) HostApplicationServices.WorkingDatabase = antes; } catch { }
            }
        }

        /// <summary>Scales read from the viewports carry float noise (49.99999): 4 significant digits.</summary>
        private static double Arredondar(double v)
        {
            if (v <= 0 || double.IsNaN(v) || double.IsInfinity(v)) return v;
            double mag = Math.Pow(10, Math.Floor(Math.Log10(v)) - 3);
            return Math.Round(v / mag) * mag;
        }

        /// <summary>Frame x f, scale x f, view unchanged: same model area, same drawing, bigger paper.</summary>
        private static void ScaleViewport(Viewport vp, double f, bool paperViewport)
        {
            Point3d c = vp.CenterPoint;
            double w = vp.Width, h = vp.Height;
            if (paperViewport)
            {
                // The sheet's own viewport only decides how paper space is zoomed on screen.
                Point2d vc = vp.ViewCenter;
                double vh = vp.ViewHeight;
                vp.CenterPoint = new Point3d(c.X * f, c.Y * f, c.Z * f);
                vp.Width = w * f;
                vp.Height = h * f;
                vp.ViewCenter = new Point2d(vc.X * f, vc.Y * f);
                vp.ViewHeight = vh * f;
                return;
            }

            double scale = vp.CustomScale;
            Point2d viewCenter = vp.ViewCenter;
            Point3d target = vp.ViewTarget;
            double twist = vp.TwistAngle;
            bool locked = vp.Locked;
            vp.Locked = false;

            vp.CenterPoint = new Point3d(c.X * f, c.Y * f, c.Z * f);
            vp.Width = w * f;
            vp.Height = h * f;
            vp.ViewTarget = target;
            vp.TwistAngle = twist;
            vp.ViewCenter = viewCenter;
            vp.CustomScale = scale * f;    // e.g. 0.02 (m paper / m model) -> 20 (mm paper / m model) = 1:50
            vp.ViewCenter = viewCenter;    // CustomScale keeps the centre, set again to be explicit
            vp.Locked = locked;
        }

        /// <summary>Plot scale / plot window / limits follow the new paper unit.</summary>
        private static void ScaleLayoutSettings(Layout lay, double f, MergeLog log)
        {
            try
            {
                var psv = PlotSettingsValidator.Current;
                try { psv.RefreshLists(lay); } catch { }

                if (lay.PlotType == Autodesk.AutoCAD.DatabaseServices.PlotType.Window)
                {
                    Extents2d win = lay.PlotWindowArea;
                    psv.SetPlotWindowArea(lay, new Extents2d(win.MinPoint.X * f, win.MinPoint.Y * f, win.MaxPoint.X * f, win.MaxPoint.Y * f));
                }

                bool fit = lay.UseStandardScale && lay.StdScaleType == StdScaleType.ScaleToFit;
                if (!fit)
                {
                    double ratio;   // plot paper units per drawing unit
                    if (lay.UseStandardScale) ratio = lay.StdScale;
                    else
                    {
                        CustomScale cs = lay.CustomPrintScale;
                        ratio = cs.Denominator != 0 ? cs.Numerator / cs.Denominator : 0;
                    }
                    if (ratio > 0)
                    {
                        double nr = ratio / f;
                        psv.SetUseStandardScale(lay, false);
                        psv.SetCustomPrintScale(lay, nr >= 1 ? new CustomScale(nr, 1) : new CustomScale(1, 1 / nr));
                    }
                }
            }
            catch (Exception ex)
            {
                log.Info($"Layout '{lay.LayoutName}': plot scale not adapted to paper units ({ex.Message}).");
            }

            // Layout limits (grid / LIMITS): property names differ between releases -> reflection.
            try
            {
                PropertyInfo p = typeof(Layout).GetProperty("Limits");
                if (p != null && p.CanWrite && p.GetValue(lay, null) is Extents2d lim)
                    p.SetValue(lay, new Extents2d(lim.MinPoint.X * f, lim.MinPoint.Y * f, lim.MaxPoint.X * f, lim.MaxPoint.Y * f), null);
            }
            catch { }
        }
    }
}
