using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using RevitDwgUnifier.Export;
using RevitDwgUnifier.Infrastructure;
using RevitDwgUnifier.Tools.Common;

namespace RevitDwgUnifier.Tools.Detail
{
    /// <summary>
    /// Windows above the cut plane of a room plan (high windows, "maxim-ar" over a counter...) do not
    /// show in the plan. For each one this puts a plan region over the opening, so it is drawn cut.
    ///
    /// The Revit API cannot create a plan region from nothing. JA Tools copies a MODEL plan region of
    /// the project (any plan region, preferably one with "JA" in Comments; its view range is the one
    /// used, e.g. cut plane at 2.10 m) into the room plan and redraws its boundary around the window
    /// (SketchEditScope). Without a model, or when Revit refuses to edit the boundary, the fallback is
    /// chosen by the user: raise the cut plane of the room plan above the highest window, or just
    /// report the windows.
    /// Runs OUTSIDE any transaction (it opens its own; it may be inside a transaction group).
    /// </summary>
    public static class PlanRegionMaker
    {
        public sealed class Outcome
        {
            public int High, Made;
            public bool CutRaised;
        }

        public static Outcome Run(Document doc, ViewPlan plan, Room room, bool raiseCutFallback, List<string> problems)
        {
            var res = new Outcome();
            if (!CutPlane(doc, plan, out double cutZ, out Level cutLevel)) return res;
            List<FamilyInstance> wins = HighWindows(doc, room, cutZ);
            res.High = wins.Count;
            if (wins.Count == 0) return res;

            // one rectangle per window (overlapping ones are merged into the first)
            var rects = new List<Obb>();
            foreach (FamilyInstance w in wins)
            {
                Obb ob = TagPlacer.OpeningBox(w);
                if (ob == null) continue;
                var r = new Obb { CX = ob.CX, CY = ob.CY, UX = ob.UX, UY = ob.UY, HU = ob.HU + RevitUtil.M(0.05), HV = ob.HV + RevitUtil.M(0.1) };
                if (rects.Any(x => x.Hits(r, RevitUtil.M(0.01)))) continue;
                rects.Add(r);
            }

            // the model plan region is looked for once per tool run (not once per room)
            Element seed = SessionCache.Get("planregionseed|" + doc.GetHashCode(), () => Tuple.Create(Seed(doc))).Item1;
            if (seed != null)
            {
                foreach (Obb r in rects)
                {
                    if (Make(doc, plan, seed, r)) res.Made++;
                    else break;               // the same reason would stop the others
                }
            }
            if (res.Made >= rects.Count) return res;

            string names = string.Join(", ", wins.Take(6).Select(w => w.Symbol?.Name ?? w.Name)) + (wins.Count > 6 ? "…" : "");
            if (raiseCutFallback)
            {
                double maxBottom = wins.Max(w => BottomZ(doc, w));
                using (var t = new Transaction(doc, "JA Tools - plano de corte"))
                {
                    t.Start();
                    FailureSwallower.Attach(t);
                    try
                    {
                        PlanViewRange vr = plan.GetViewRange();
                        double off = maxBottom - cutLevel.ProjectElevation + RevitUtil.M(0.05);
                        vr.SetOffset(PlanViewPlane.CutPlane, off);
                        if (vr.GetLevelId(PlanViewPlane.TopClipPlane) == cutLevel.Id && vr.GetOffset(PlanViewPlane.TopClipPlane) < off + RevitUtil.M(0.05))
                            vr.SetOffset(PlanViewPlane.TopClipPlane, off + RevitUtil.M(0.1));
                        plan.SetViewRange(vr);
                        t.Commit();
                        res.CutRaised = true;
                        RevitUtil.AddOnce(problems, $"{plan.Name}: plano de corte elevado para {RevitUtil.ToM(off):0.00} m para mostrar as esquadrias altas ({names}).");
                    }
                    catch (Exception ex)
                    {
                        t.RollBack();
                        RevitUtil.AddOnce(problems, $"{plan.Name}: não foi possível elevar o plano de corte ({ex.Message}). Esquadrias acima do corte: {names}.");
                    }
                }
            }
            else
            {
                RevitUtil.AddOnce(problems, seed == null
                    ? $"{plan.Name}: {wins.Count} esquadria(s) acima do plano de corte ({names}). Para o JA Tools criar a região de planta, " +
                      "desenhe uma vez uma região de planta modelo em qualquer planta (com o corte acima das janelas altas, ex. 2,10 m; escreva JA em Comentários)."
                    : $"{plan.Name}: o Revit não permitiu redesenhar a região de planta modelo; {wins.Count - res.Made} esquadria(s) alta(s) sem região ({names}).");
            }
            return res;
        }

        // ------------------------------------------------------------------ detection

        private static bool CutPlane(Document doc, ViewPlan plan, out double cutZ, out Level cutLevel)
        {
            cutZ = 0;
            cutLevel = null;
            try
            {
                PlanViewRange vr = plan.GetViewRange();
                cutLevel = doc.GetElement(vr.GetLevelId(PlanViewPlane.CutPlane)) as Level ?? plan.GenLevel;
                if (cutLevel == null) return false;
                cutZ = cutLevel.ProjectElevation + vr.GetOffset(PlanViewPlane.CutPlane);
                return true;
            }
            catch { return false; }
        }

        public static double BottomZ(Document doc, FamilyInstance w)
        {
            Level lvl = doc.GetElement(w.LevelId) as Level;
            Parameter sill = w.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM);
            if (lvl != null && sill != null && sill.HasValue) return lvl.ProjectElevation + sill.AsDouble();
            return w.get_BoundingBox(null)?.Min.Z ?? double.MinValue;
        }

        /// <summary>Windows in the walls of the room whose bottom is above the cut plane.</summary>
        private static List<FamilyInstance> HighWindows(Document doc, Room room, double cutZ)
        {
            var list = new List<FamilyInstance>();
            BoundingBoxXYZ rb = RevitUtil.RoomBox(room);
            if (rb == null) return list;
            double z = rb.Min.Z + RevitUtil.M(1.0);
            var near = new Outline(new XYZ(rb.Min.X - RevitUtil.M(1), rb.Min.Y - RevitUtil.M(1), rb.Min.Z - RevitUtil.M(1)),
                                   new XYZ(rb.Max.X + RevitUtil.M(1), rb.Max.Y + RevitUtil.M(1), rb.Max.Z + RevitUtil.M(1)));
            foreach (FamilyInstance w in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Windows).OfClass(typeof(FamilyInstance))
                         .WherePasses(new BoundingBoxIntersectsFilter(near)).Cast<FamilyInstance>())
            {
                if (!(w.Host is Wall)) continue;
                if (BottomZ(doc, w) <= cutZ + RevitUtil.M(0.01)) continue;
                Obb ob = TagPlacer.OpeningBox(w);
                if (ob == null) continue;
                bool inRoom = false;
                foreach (int side in new[] { 1, -1 })
                {
                    double d = ob.HV + RevitUtil.M(0.15);
                    var q = new XYZ(ob.CX - ob.VX * d * side, ob.CY - ob.VY * d * side, z);
                    try { if (room.IsPointInRoom(q)) inRoom = true; } catch { }
                }
                if (inRoom) list.Add(w);
            }
            return list;
        }

        private static Element Seed(Document doc)
        {
            var regions = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_PlanRegion).WhereElementIsNotElementType()
                .Where(e => e.OwnerViewId != null && e.OwnerViewId != ElementId.InvalidElementId).ToList();
            return regions.OrderByDescending(e =>
            {
                string c = e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "";
                return c.IndexOf("JA", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0;
            }).FirstOrDefault();
        }

        // ------------------------------------------------------------------ creation

        private static bool Make(Document doc, ViewPlan plan, Element seed, Obb rect)
        {
            ElementId copyId = null;
            try
            {
                using (var t = new Transaction(doc, "JA Tools - região de planta"))
                {
                    t.Start();
                    FailureSwallower.Attach(t);
                    var source = doc.GetElement(seed.OwnerViewId) as View;
                    ICollection<ElementId> ids = ElementTransformUtils.CopyElements(source, new List<ElementId> { seed.Id }, plan, Transform.Identity, new CopyPasteOptions());
                    copyId = ids?.FirstOrDefault(id => doc.GetElement(id)?.Category?.Id.Value == (long)BuiltInCategory.OST_PlanRegion);
                    t.Commit();
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("[JA Tools] Cópia da região de planta: " + ex.Message);
                return false;
            }
            if (copyId == null) return false;
            if (Reshape(doc, copyId, rect)) return true;
            try
            {
                using (var t = new Transaction(doc, "JA Tools - região de planta (desfazer)"))
                {
                    t.Start();
                    doc.Delete(copyId);
                    t.Commit();
                }
            }
            catch { }
            return false;
        }

        /// <summary>Replaces the boundary of the plan region by the rectangle.</summary>
        private static bool Reshape(Document doc, ElementId regionId, Obb rect)
        {
            Element region = doc.GetElement(regionId);
            Sketch sketch = region?.GetDependentElements(new ElementClassFilter(typeof(Sketch)))
                .Select(id => doc.GetElement(id) as Sketch).FirstOrDefault(s => s != null);
            if (sketch == null) return false;
            try
            {
                using (var scope = new SketchEditScope(doc, "JA Tools - contorno da região de planta"))
                {
                    scope.Start(sketch.Id);
                    using (var t = new Transaction(doc, "JA Tools - contorno"))
                    {
                        t.Start();
                        var old = OldCurves(doc, sketch);
                        SketchPlane sp = sketch.SketchPlane;
                        double z = sp.GetPlane().Origin.Z;
                        List<XYZ> c = rect.Corners(z).ToList();
                        var ring = new[] { c[0], c[1], c[3], c[2] };
                        for (int i = 0; i < 4; i++)
                            doc.Create.NewModelCurve(Line.CreateBound(ring[i], ring[(i + 1) % 4]), sp);
                        if (old.Count > 0) doc.Delete(old);
                        t.Commit();
                    }
                    scope.Commit(new FailureSwallower());
                }
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn("[JA Tools] Contorno da região de planta: " + ex.Message);
                return false;
            }
        }

        private static List<ElementId> OldCurves(Document doc, Sketch sketch)
        {
            var ids = new List<ElementId>();
            try
            {
                foreach (CurveArray ca in sketch.Profile)
                    foreach (Curve c in ca)
                    {
                        ElementId id = c.Reference?.ElementId;
                        if (id != null && id != ElementId.InvalidElementId && !ids.Contains(id)) ids.Add(id);
                    }
            }
            catch { }
            if (ids.Count > 0) return ids;
            // Revit 2022+: Sketch.GetAllElements (read by reflection: not in every API version)
            try
            {
                var m = typeof(Sketch).GetMethod("GetAllElements", Type.EmptyTypes);
                if (m?.Invoke(sketch, null) is IEnumerable<ElementId> all)
                    ids.AddRange(all.Where(id => doc.GetElement(id) is CurveElement));
            }
            catch { }
            return ids;
        }
    }
}
