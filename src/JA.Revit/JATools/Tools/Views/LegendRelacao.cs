using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using RevitDwgUnifier.Infrastructure;
using RevitDwgUnifier.Tools.Common;

namespace RevitDwgUnifier.Tools.Views
{
    /// <summary>
    /// "Relação" layout (relação de bancadas / relação de granito): one card per type, stacked on
    /// the sheet like the office's granite lists:
    ///
    ///   ┌──────┬───────────────────┬──────────────────┬────────────┐
    ///   │      │   ESPECIFICAÇÃO   │  COMPARTIMENTO   │ QUANTIDADE │
    ///   │ TE01 ├───────────────────┼──────────────────┼────────────┤
    ///   │      │ GRANITO A SER ... │ BANHEIRO SUÍTE   │ 1 x 6 PAV. │  one row per compartment (optional)
    ///   ├──────┴───────────────────┴──────────────────┴────────────┤
    ///   │     [planta com cotas]              (det. 1)  (det. 2)   │  typical details = generic annotations
    ///   ├──────────────────────────────────────────────────────────┤
    ///   │ TOTAL: 6 BANCAS                                          │
    ///   └──────────────────────────────────────────────────────────┘
    /// </summary>
    public static partial class LegendEngine
    {
        private sealed class RelGroup
        {
            public string Comp = "";
            public string Qty = "";
        }

        private static string UnitFor(LegendTemplate t, int n) =>
            n == 1 ? (string.IsNullOrWhiteSpace(t.UnitName) ? "UNIDADE" : t.UnitName.Trim())
                   : (string.IsNullOrWhiteSpace(t.UnitPlural) ? "UNIDADES" : t.UnitPlural.Trim());

        /// <summary>Instances of the type (family instances and system elements alike) in the chosen
        /// phase. The category is read once per run and grouped by type (was one model scan per type).</summary>
        private static List<Element> Instances(Document doc, LegendCandidate type)
        {
            try
            {
                var map = SessionCache.Get("legendInst|" + doc.GetHashCode() + "|" + type.CategoryId + "|" + PhaseKey, () =>
                {
                    var m = new Dictionary<long, List<Element>>();
                    foreach (Element e in new FilteredElementCollector(doc).WhereElementIsNotElementType().OfCategoryId(new ElementId(type.CategoryId)))
                    {
                        if (!InPhase(e)) continue;
                        long t;
                        try { t = e.GetTypeId().Value; } catch { continue; }
                        if (!m.TryGetValue(t, out List<Element> l)) m[t] = l = new List<Element>();
                        l.Add(e);
                    }
                    return m;
                });
                return map.TryGetValue(type.Id.Value, out List<Element> list) ? list : new List<Element>();
            }
            catch { return new List<Element>(); }
        }

        /// <summary>Compartment of one instance: its rooms, or the chosen parameter (instance first, then type).</summary>
        private static string CompOf(Document doc, Element e, LegendCandidate type, string source)
        {
            if (string.IsNullOrEmpty(source) || source == LegendSources.Location)
            {
                var names = new List<string>();
                var rooms = new List<Element>();
                if (e is FamilyInstance fi) rooms.AddRange(RoomsOf(doc, fi));
                if (rooms.All(r => r == null))
                {
                    BoundingBoxXYZ bb = e.get_BoundingBox(null);
                    if (bb != null)
                    {
                        XYZ at = new XYZ((bb.Min.X + bb.Max.X) / 2, (bb.Min.Y + bb.Max.Y) / 2, bb.Min.Z + 0.3);
                        Phase ph = CurrentPhase(doc);
                        rooms.Add(SafeRoom(() => ph != null ? doc.GetRoomAtPoint(at, ph) : doc.GetRoomAtPoint(at)));
                    }
                }
                foreach (Element room in rooms)
                {
                    string n = room?.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString();
                    if (!string.IsNullOrWhiteSpace(n) && !names.Contains(n.Trim(), StringComparer.OrdinalIgnoreCase)) names.Add(n.Trim());
                }
                return string.Join(", ", names);
            }
            if (LegendSources.All.Contains(source)) return RowValue(doc, type, source) ?? "";
            return ParamText(e, source) ?? ParamText(doc.GetElement(type.Id), source) ?? "";
        }

        private static string LevelKey(Document doc, Element e)
        {
            try
            {
                BoundingBoxXYZ bb = e.get_BoundingBox(null);
                Level l = LevelOf(doc, e, bb?.Min.Z ?? 0);
                return l?.Name ?? "";
            }
            catch { return ""; }
        }

        /// <summary>"1 x 6 PAV. = 6 BANCAS" when every level has the same count; otherwise "6 BANCAS".</summary>
        private static string QtyText(Document doc, LegendTemplate t, List<Element> items)
        {
            int total = items.Count;
            if (t.QtyByFloor && total > 1)
            {
                var perLevel = items.GroupBy(e => LevelKey(doc, e)).Select(g => g.Count()).ToList();
                if (perLevel.Count > 1 && perLevel.Distinct().Count() == 1)
                    return $"{perLevel[0]} x {perLevel.Count} {(t.FloorWord ?? "PAV.").Trim()} = {total} {UnitFor(t, total)}";
            }
            return $"{total} {UnitFor(t, total)}";
        }

        /// <summary>Compartment rows of the card and the total count.</summary>
        private static List<RelGroup> RelGroups(Document doc, LegendCandidate type, LegendTemplate t, out int total)
        {
            var items = Instances(doc, type);
            total = items.Count;
            var groups = new List<RelGroup>();
            if (items.Count == 0)
            {
                groups.Add(new RelGroup { Comp = "—", Qty = "0 " + UnitFor(t, 0) });
                return groups;
            }
            var withComp = items.Select(e => new { E = e, C = CompOf(doc, e, type, t.CompSource) }).ToList();
            if (t.CompPerRoom)
            {
                var nat = new NaturalStringComparer();
                foreach (var g in withComp.GroupBy(x => x.C ?? "", StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, nat))
                    groups.Add(new RelGroup { Comp = g.Key.Length > 0 ? g.Key : "—", Qty = QtyText(doc, t, g.Select(x => x.E).ToList()) });
            }
            else
            {
                var comps = new List<string>();
                foreach (var x in withComp)
                    foreach (string part in (x.C ?? "").Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries))
                        if (!comps.Contains(part, StringComparer.OrdinalIgnoreCase)) comps.Add(part);
                groups.Add(new RelGroup { Comp = comps.Count > 0 ? string.Join(", ", comps) : "—", Qty = QtyText(doc, t, items) });
            }
            return groups;
        }

        private static int RelGroupCount(Document doc, LegendCandidate type, LegendTemplate t)
        {
            try { return RelGroups(doc, type, t, out _).Count; } catch { return 1; }
        }

        // ================================================================== scale
        private static bool RelFits(LegendTemplate t, List<Comp> comps, double sill, int groups, bool details, int scale)
        {
            if (!t.FixedSize) return true;
            var elevs = comps.Where(x => x.Key != Plan).ToList();
            Comp plan = comps.FirstOrDefault(x => x.Key == Plan);
            double gapMm = Math.Max(2, t.ViewGapMm), dimMm = 9;
            double toMm = 304.8 / scale;
            double elevW = elevs.Sum(x => x.W) * toMm + Math.Max(0, elevs.Count - 1) * gapMm + (t.DimHeight || t.DimSill ? dimMm * 1.6 : 0);
            double elevH = elevs.Count == 0 ? 0 : (elevs.Max(x => x.H) + sill) * toMm;
            double planH = plan == null ? 0 : plan.H * toMm + (t.DimWidth ? dimMm : 0) + (elevs.Count > 0 ? gapMm : 0);
            double planW = plan == null ? 0 : plan.W * toMm + (t.DimWidth || t.DimDepth ? dimMm * 1.6 : 0);
            double needW = Math.Max(elevW, planW), needH = elevH + planH;
            double areaW = (t.WidthMm - 2 * t.PaddingMm) * (details ? 1 - Clamp(t.DetailsPct, 0, 80) / 100 : 1);
            double headMm = Math.Max(4, t.HeadRowMm) + Math.Max(1, groups) * 11;
            double areaH = t.HeightMm - headMm - (t.ShowTotal ? Math.Max(5, t.HeadRowMm) : 0) - 2 * t.PaddingMm;
            return needW <= areaW && needH <= areaH;
        }

        private static int RelBestScale(LegendTemplate t, List<Comp> comps, double sill, int groups, bool details)
        {
            int fixedScale = Math.Max(1, t.Scale);
            if (!t.FixedSize) return fixedScale;
            switch (t.ScaleMode)
            {
                case 0: return fixedScale;
                case 1:
                    if (RelFits(t, comps, sill, groups, details, fixedScale)) return fixedScale;
                    return StandardScales.Where(s => s > fixedScale).FirstOrDefault(s => RelFits(t, comps, sill, groups, details, s));
                default:
                    return StandardScales.Where(s => s >= 5).FirstOrDefault(s => RelFits(t, comps, sill, groups, details, s));
            }
        }

        private static double Clamp(double v, double min, double max) => Math.Max(min, Math.Min(max, v));

        // ================================================================== one card
        /// <returns>Same as BuildCard: card width, height, drawing area left, top, width, height (mm) and scale.</returns>
        private static double[] BuildRelacaoCard(Ctx c, LegendProbe p, LegendTemplate t, LegendCandidate type, HashSet<string> missing,
                                                 LegendResult res, int forcedScale, List<KeyValuePair<string, View>> modelViews,
                                                 List<ElementId> details)
        {
            Document doc = c.Doc;
            View view = c.View;

            // ---- 1. drawings
            bool model = type.ModelMode;
            List<Comp> comps = model ? ModelComps(doc, type, t) : CreateComps(doc, view, p, t, type, missing, res, true);
            if (comps == null || comps.Count == 0)
            {
                if (model) res.Problems.Add($"{type.Title}: nenhuma instância no modelo para desenhar.");
                return null;
            }
            var elevs = comps.Where(x => x.Key != Plan).ToList();
            Comp plan = comps.FirstOrDefault(x => x.Key == Plan);
            double sill = !model && t.FloorLine && elevs.Count > 0 ? Sill(doc, type) : 0;
            double nomW = type.Width > 0 ? type.Width : (elevs.Count > 0 ? elevs[0].W : plan?.W ?? 0);
            double nomH = type.Height > 0 ? type.Height : (elevs.Count > 0 ? elevs[0].H : 0);

            // ---- 2. data
            var groups = RelGroups(doc, type, t, out int total);
            details = (details ?? new List<ElementId>()).Where(id => doc.GetElement(id) is FamilySymbol).ToList();
            bool hasDetails = details.Count > 0;

            // ---- 3. scale
            int scale = forcedScale > 0 ? forcedScale : RelBestScale(t, comps, sill, groups.Count, hasDetails);
            if (scale <= 0)
            {
                scale = StandardScales.Last();
                res.Problems.Add($"{type.Title}: o desenho não cabe na ficha nem em 1:{scale}.");
            }
            else if (!RelFits(t, comps, sill, groups.Count, hasDetails, scale))
                res.Problems.Add($"{type.Title}: o desenho não cabe bem na ficha em 1:{scale}.");
            if (scale != t.Scale && t.ScaleMode == 1) res.ScaleChanges.Add($"{type.Title}: 1:{t.Scale} → 1:{scale}");
            try { view.Scale = scale; } catch { }
            c.K = scale / 304.8;
            double k = c.K;

            // ---- 4. columns
            double W = t.WidthMm * k, Hc = t.HeightMm * k, P = t.PaddingMm * k;
            double markW = t.ShowMark ? Clamp(t.MarkColMm, 8, t.WidthMm * 0.3) * k : 0;
            double rest = W - markW;
            double specW = rest * Clamp(t.SpecColPct, 10, 80) / 100;
            double compW = rest * Clamp(t.CompColPct, 10, 80) / 100;
            double qtyW = Math.Max(rest * 0.1, rest - specW - compW);
            if (specW + compW + qtyW > rest) compW = Math.Max(rest * 0.1, rest - specW - qtyW);
            double x1 = markW, x2 = markW + specW, x3 = x2 + compW;
            double pad = 1.5 * k;
            Func<double, double> paper = w => Math.Max(5 * k, w - 2 * pad) / scale;   // text width in paper feet

            // ---- 5. texts
            var center = HorizontalTextAlignment.Center;
            TextNote mark = t.ShowMark ? Text(c, string.IsNullOrEmpty(type.Mark) ? type.TypeName : type.Mark, c.MarkType, center) : null;
            TextNote hs = Text(c, Nz(t.HeadSpec, "ESPECIFICAÇÃO"), c.BodyType, center, paper(specW));
            TextNote hc = Text(c, Nz(t.HeadComp, "COMPARTIMENTO"), c.BodyType, center, paper(compW));
            TextNote hq = Text(c, Nz(t.HeadQty, "QUANTIDADE"), c.BodyType, center, paper(qtyW));
            string spec = RowValue(doc, type, t.SpecSource);
            TextNote vs = string.IsNullOrWhiteSpace(spec) ? null : Text(c, spec.Trim(), c.BodyType, center, paper(specW));
            var rowTexts = groups.Select(g => new[]
            {
                Text(c, g.Comp, c.BodyType, center, paper(compW)),
                Text(c, g.Qty, c.BodyType, center, paper(qtyW))
            }).ToList();
            TextNote tot = t.ShowTotal ? Text(c, $"TOTAL: {total} {UnitFor(t, total)}", c.BodyType, HorizontalTextAlignment.Left) : null;
            // quantity texts that "Atualizar Fichas" can recount later
            for (int gi = 0; gi < groups.Count; gi++)
            {
                c.Tags[rowTexts[gi][1].Id.Value] = Tag(type, "relqty", RelExtra(t, groups[gi].Comp));
                if (!t.CompPerRoom) c.Tags[rowTexts[gi][0].Id.Value] = Tag(type, "relcomp", RelExtra(t, groups[gi].Comp));
            }
            if (tot != null) c.Tags[tot.Id.Value] = Tag(type, "reltotal", RelExtra(t, ""));
            doc.Regenerate();

            // ---- 6. row heights
            double headH = Math.Max(Math.Max(3, t.HeadRowMm) * k, new[] { hs, hc, hq }.Max(x => H(c, x)) + 2 * pad);
            var rowH = rowTexts.Select(r => Math.Max(Math.Max(8, t.HeadRowMm * 1.6) * k, Math.Max(H(c, r[0]), H(c, r[1])) + 3 * pad)).ToList();
            double valuesH = rowH.Sum();
            double specNeed = (vs != null ? H(c, vs) : 0) + 3 * pad;
            if (valuesH < specNeed) { rowH[rowH.Count - 1] += specNeed - valuesH; valuesH = specNeed; }
            double markNeed = (mark != null ? H(c, mark) : 0) + 3 * pad;
            if (headH + valuesH < markNeed) { rowH[rowH.Count - 1] += markNeed - headH - valuesH; valuesH = markNeed - headH; }
            double tableH = headH + valuesH;
            double totalH = tot != null ? Math.Max(Math.Max(4, t.HeadRowMm) * k, H(c, tot) + 2 * pad) : 0;

            // ---- 7. drawing area (left) and typical details (right)
            double gap = Math.Max(2, t.ViewGapMm) * k, dim = 9 * k;
            double rowW = elevs.Sum(x => x.W) + Math.Max(0, elevs.Count - 1) * gap;
            double elevRowH = elevs.Count == 0 ? 0 : elevs.Max(x => x.H) + sill;
            double planBlockH = plan == null ? 0 : plan.H + (t.DimWidth ? dim : 0) + (elevs.Count > 0 ? gap : 0);
            double blockW = Math.Max(rowW, plan?.W ?? 0);
            double blockH = elevRowH + planBlockH;
            if (!t.FixedSize) Hc = tableH + 2 * P + blockH + dim + totalH;

            double areaLeft = P, areaTop = -tableH - P;
            double areaFullW = W - 2 * P, areaH = Hc - tableH - totalH - 2 * P;
            double drawW = hasDetails ? areaFullW * (1 - Clamp(t.DetailsPct, 0, 80) / 100) : areaFullW;
            double rightDims = (t.DimDepth || t.DimHeight || WantsDepth(t, type)) ? dim * 1.2 : 0;
            double blockLeft = areaLeft + Math.Max(0, (drawW - blockW - rightDims) / 2);
            double blockTop = areaTop - Math.Max(0, (areaH - blockH - (t.DimWidth ? dim : 0)) / 2);
            if (!model) DrawBlock(c, t, type, elevs, plan, sill, nomW, nomH, blockLeft, blockTop, blockW, rowW, elevRowH, gap);

            if (hasDetails) PlaceDetails(c, details, areaLeft + drawW, areaTop, areaFullW - drawW, areaH, res, type);

            // ---- 8. table lines and texts
            Frame(c, t.Frame, true, W, Hc);
            double y1 = -headH, y2 = -tableH;
            Sep(c, new XYZ(x1, y1, 0), new XYZ(W, y1, 0));
            Sep(c, new XYZ(0, y2, 0), new XYZ(W, y2, 0));
            if (markW > 0) Sep(c, new XYZ(x1, 0, 0), new XYZ(x1, y2, 0));
            Sep(c, new XYZ(x2, 0, 0), new XYZ(x2, y2, 0));
            Sep(c, new XYZ(x3, 0, 0), new XYZ(x3, y2, 0));
            if (tot != null) Sep(c, new XYZ(0, -(Hc - totalH), 0), new XYZ(W, -(Hc - totalH), 0));

            if (mark != null) Place(c, mark, x1 / 2, y2 / 2 + H(c, mark) / 2, true);
            Place(c, hs, x1 + specW / 2, -(headH - H(c, hs)) / 2, true);
            Place(c, hc, x2 + compW / 2, -(headH - H(c, hc)) / 2, true);
            Place(c, hq, x3 + qtyW / 2, -(headH - H(c, hq)) / 2, true);
            if (vs != null) Place(c, vs, x1 + specW / 2, y1 - (valuesH - H(c, vs)) / 2, true);
            double yy = y1;
            for (int i = 0; i < rowTexts.Count; i++)
            {
                double rh = rowH[i];
                Place(c, rowTexts[i][0], x2 + compW / 2, yy - (rh - H(c, rowTexts[i][0])) / 2, true);
                Place(c, rowTexts[i][1], x3 + qtyW / 2, yy - (rh - H(c, rowTexts[i][1])) / 2, true);
                yy -= rh;
                if (i < rowTexts.Count - 1) Sep(c, new XYZ(x2, yy, 0), new XYZ(W, yy, 0));
            }
            if (tot != null) Place(c, tot, pad * 1.5, -(Hc - totalH) - (totalH - H(c, tot)) / 2, false);
            doc.Regenerate();

            // ---- 9. model mode: the views go in the drawing area on the sheet
            if (model) CreateModelViews(c, p, t, type, scale, modelViews, res);
            return new[] { W / k, Hc / k, areaLeft / k, -areaTop / k, drawW / k, areaH / k, scale };
        }

        private static string Nz(string s, string fallback) => string.IsNullOrWhiteSpace(s) ? fallback : s.Trim();

        /// <summary>Typical details (generic annotations) side by side in the right part of the drawing
        /// area, wrapping to a new row when they do not fit; the group is centred.</summary>
        private static void PlaceDetails(Ctx c, List<ElementId> details, double left, double top, double width, double height,
                                         LegendResult res, LegendCandidate type)
        {
            Document doc = c.Doc;
            var placed = new List<FamilyInstance>();
            foreach (ElementId id in details)
            {
                try
                {
                    var sym = (FamilySymbol)doc.GetElement(id);
                    if (!sym.IsActive) { sym.Activate(); doc.Regenerate(); }
                    placed.Add(doc.Create.NewFamilyInstance(XYZ.Zero, sym, c.View));
                }
                catch (Exception ex)
                {
                    res.Problems.Add($"{type.Title}: detalhe típico não colocado ({ex.Message}).");
                }
            }
            if (placed.Count == 0) return;
            doc.Regenerate();

            double gap = 4 * c.K;
            var sizes = placed.Select(e => e.get_BoundingBox(c.View)).ToList();
            // rows that fit the width
            var rows = new List<List<int>>();
            var current = new List<int>();
            double used = 0;
            for (int i = 0; i < placed.Count; i++)
            {
                if (sizes[i] == null) continue;
                double w = sizes[i].Max.X - sizes[i].Min.X;
                if (current.Count > 0 && used + gap + w > width) { rows.Add(current); current = new List<int>(); used = 0; }
                used += (current.Count > 0 ? gap : 0) + w;
                current.Add(i);
            }
            if (current.Count > 0) rows.Add(current);

            Func<List<int>, double> rowWidth = r => r.Sum(i => sizes[i].Max.X - sizes[i].Min.X) + Math.Max(0, r.Count - 1) * gap;
            Func<List<int>, double> rowHeight = r => r.Max(i => sizes[i].Max.Y - sizes[i].Min.Y);
            double totalH = rows.Sum(rowHeight) + Math.Max(0, rows.Count - 1) * gap;
            if (rows.Any(r => rowWidth(r) > width + 1e-6) || totalH > height + 1e-6)
                res.Problems.Add($"{type.Title}: os detalhes típicos não cabem bem no espaço reservado (aumente a parte dos detalhes na aba Layout).");

            double y = top - Math.Max(0, (height - totalH) / 2);
            foreach (var r in rows)
            {
                double x = left + Math.Max(0, (width - rowWidth(r)) / 2);
                double rh = rowHeight(r);
                foreach (int i in r)
                {
                    BoundingBoxXYZ b = sizes[i];
                    double w = b.Max.X - b.Min.X, h = b.Max.Y - b.Min.Y;
                    // centred vertically in its row
                    ElementTransformUtils.MoveElement(doc, placed[i].Id, new XYZ(x - b.Min.X, (y - (rh - h) / 2) - b.Max.Y, 0));
                    x += w + gap;
                }
                y -= rh + gap;
            }
        }

        // ================================================================== sheet note
        /// <summary>Framed note row ("OBS.: CONFERIR MEDIDAS NO LOCAL; COTAS EM METRO.") under the cards.</summary>
        private static void DrawSheetNote(Document doc, ViewSheet sheet, SheetPlacement s, double left, double top,
                                          double usableW, double usableH, ElementId textType)
        {
            if (string.IsNullOrWhiteSpace(s.Note)) return;
            try
            {
                double x0 = left + s.MarginLeftMm / 304.8, x1 = x0 + usableW / 304.8;
                double yTop = top - (s.MarginTopMm + usableH) / 304.8, yBot = yTop - s.NoteRowMm / 304.8;
                foreach (var pair in new[]
                {
                    new[] { new XYZ(x0, yTop, 0), new XYZ(x1, yTop, 0) }, new[] { new XYZ(x1, yTop, 0), new XYZ(x1, yBot, 0) },
                    new[] { new XYZ(x1, yBot, 0), new XYZ(x0, yBot, 0) }, new[] { new XYZ(x0, yBot, 0), new XYZ(x0, yTop, 0) }
                })
                    doc.Create.NewDetailCurve(sheet, Line.CreateBound(pair[0], pair[1]));
                var o = new TextNoteOptions(textType) { HorizontalAlignment = HorizontalTextAlignment.Left, VerticalAlignment = VerticalTextAlignment.Middle };
                TextNote.Create(doc, sheet.Id, new XYZ(x0 + 2 / 304.8, (yTop + yBot) / 2, 0), s.Note.Trim(), o);
            }
            catch (Exception ex) { Logger.Warn("[JA Tools] Observação da prancha: " + ex.Message); }
        }
    }
}
