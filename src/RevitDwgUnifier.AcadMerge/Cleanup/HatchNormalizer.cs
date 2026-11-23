using System;
using Autodesk.AutoCAD.DatabaseServices;

namespace RevitDwgUnifier.AcadMerge.Cleanup
{
    /// <summary>
    /// PITFALL #4 - Hatch / pattern scaling (DWG half).
    ///
    /// Revit drafting patterns are defined in paper units and model patterns in real units.
    /// After export, a pattern intended for 1:20 that ends up in a 1:500 viewport (or a model
    /// pattern exported in the wrong unit) has thousands of lines per hatch. AutoCAD then either
    /// refuses to generate it (HPMAXLINES -> the hatch is invisible), or it plots as a solid grey
    /// blob. Both are fixed here by measuring the real generated line count of every hatch and
    /// scaling the pattern up until it is below <c>MaxHatchLines</c>:
    ///   * over-dense : scale *= lines / MaxHatchLines            (iterated, max 3 times)
    ///   * invisible  : 0 generated lines on a non-solid hatch    -> scale *= 10 (iterated)
    /// Predefined/custom patterns are re-looked-up by name (needs a standard name, see the Revit
    /// side hatch mapping); user-defined patterns change PatternSpace instead. Solid fills and
    /// gradients are never touched. MEASUREMENT is aligned with INSUNITS so AutoCAD reads
    /// acadiso.pat (metric) vs acad.pat (imperial) when hatches are edited later.
    /// </summary>
    internal static class HatchNormalizer
    {
        public static void Normalize(Database db, int maxLines, MergeLog log)
        {
            if (maxLines <= 0) return;
            int fixedDense = 0, fixedInvisible = 0, unresolved = 0, scanned = 0;

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId btrId in bt)
                {
                    var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
                    if (btr.IsFromExternalReference || btr.IsDependent) continue;

                    foreach (ObjectId id in btr)
                    {
                        if (!(tr.GetObject(id, OpenMode.ForRead) is Hatch h)) continue;
                        if (h.IsSolidFill || h.IsGradient) continue;
                        scanned++;

                        int lines = LineCount(h);
                        bool dense = lines > maxLines;
                        bool invisible = lines == 0 && HasArea(h);
                        if (!dense && !invisible) continue;

                        h.UpgradeOpen();
                        bool ok = false;
                        for (int attempt = 0; attempt < 3; attempt++)
                        {
                            double factor = dense ? Math.Max(1.5, (double)lines / maxLines) : 10.0;
                            if (!Rescale(h, factor)) break;
                            lines = LineCount(h);
                            dense = lines > maxLines;
                            invisible = lines == 0;
                            if (!dense && !invisible) { ok = true; break; }
                        }

                        if (ok)
                        {
                            if (invisible) fixedInvisible++; else fixedDense++;
                        }
                        else unresolved++;
                    }
                }
                tr.Commit();
            }

            log.Stat($"Hatches scanned: {scanned}, re-scaled: {fixedDense + fixedInvisible}, " +
                     $"could not be re-scaled: {unresolved}");
            if (unresolved > 0)
                log.Warn($"{unresolved} hatch(es) are still very dense. Map their Revit fill patterns to standard " +
                         "AutoCAD patterns in the export setup so they can be re-scaled.");
        }

        private static bool Rescale(Hatch h, double factor)
        {
            try
            {
                if (h.PatternType == HatchPatternType.UserDefined)
                {
                    h.PatternSpace *= factor;
                    h.SetHatchPattern(HatchPatternType.UserDefined, h.PatternName);
                }
                else
                {
                    double old = h.PatternScale;
                    h.PatternScale = old * factor;
                    try
                    {
                        // Re-reads the definition from acad(iso).pat / <name>.pat with the new scale.
                        h.SetHatchPattern(h.PatternType, h.PatternName);
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        h.PatternScale = old; // pattern not found on this machine: keep original
                        return false;
                    }
                }
                h.EvaluateHatch(true);
                return true;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                return false;
            }
        }

        private static int LineCount(Hatch h)
        {
            try { return h.NumberOfHatchLines; }
            catch { return -1; }
        }

        private static bool HasArea(Hatch h)
        {
            try { return h.Area > 1e-9; }
            catch { return false; }
        }

        public static void AlignMeasurement(Database db)
        {
            switch (db.Insunits)
            {
                case UnitsValue.Millimeters:
                case UnitsValue.Centimeters:
                case UnitsValue.Meters:
                    db.Measurement = MeasurementValue.Metric;
                    break;
                case UnitsValue.Inches:
                case UnitsValue.Feet:
                    db.Measurement = MeasurementValue.English;
                    break;
            }
        }
    }
}
