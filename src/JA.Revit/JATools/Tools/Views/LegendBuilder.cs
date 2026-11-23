using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using RevitDwgUnifier.Infrastructure;
using RevitDwgUnifier.Tools.Common;

namespace RevitDwgUnifier.Tools.Views
{
    /// <summary>
    /// Builds ONE legend per type, drawn as a "ficha" with the chosen template:
    ///
    ///   ┌────────┬──────────────────────────────┐
    ///   │        │   [frontal] [lateral] ...  ├ A │   elevations side by side over a floor line,
    ///   │  PA01  │   ───────────────────────  ┴ P │   height and sill chained on the right
    ///   │ ────── │          [planta]              │   plan below, width dimensioned under it
    ///   │1.60x2.2│          ├── L ──┤             │
    ///   │        ├──────────────────────────────┤
    ///   │        │   Descrição     PORTA ...      │   information table (any parameter,
    ///   │        │   Quantidade    68             │   quantity, rooms...)
    ///   └────────┴──────────────────────────────┘
    ///
    /// Sizes come from the template in printed millimetres; the legend scale is reduced when the
    /// drawings do not fit. Optionally the legends are placed on sheets, one card per cell.
    ///
    /// On sheets, the legend keeps ONLY the drawings and their dimensions (legend components, floor
    /// line, typical details): the frame, the separation lines and every text (mark, size, table
    /// labels and values) are laid out the same way but created on the sheet itself, at the place
    /// they would have in the legend (an invisible frame in the legend keeps the viewport centred on
    /// the card, so the legend → sheet mapping is exact). Without sheets everything stays in the legend.
    /// </summary>
    public static partial class LegendEngine
    {
        private static readonly int[] StandardScales = { 1, 2, 5, 10, 20, 25, 50, 75, 100, 125, 150, 200, 250, 500, 1000 };

        private sealed class Comp
        {
            public string Key;
            public Element E;
            public double W, H;       // natural size (feet, model)
        }

        private sealed class CardInfo
        {
            public View View;
            public double[] Size;      // see BuildCard
            public List<KeyValuePair<string, View>> ModelViews;
            public List<OverlayText> Texts = new List<OverlayText>();
            public List<XYZ[]> Lines = new List<XYZ[]>();
        }

        /// <summary>A text of the card, laid out in the legend, created on the sheet.</summary>
        private sealed class OverlayText
        {
            public string Text;
            public ElementId TypeId;
            public HorizontalTextAlignment Align;
            public double PaperWidth;  // 0 = unwrapped
            public XYZ Coord;          // legend coordinates (feet of model)
            public CountTag Tag;       // quantity / location text (updatable)
        }

        private sealed class Ctx
        {
            public Document Doc;
            public View View;
            public double K;           // feet of model per mm of paper
            public GraphicsStyle Invisible;
            public DimensionType DimType;
            public ElementId MarkType, SizeType, BodyType;
            /// <summary>Texts and separation lines go to the sheet (the legend keeps only drawings and dimensions).</summary>
            public bool Overlay;
            public readonly List<XYZ[]> Lines = new List<XYZ[]>();
            public readonly Dictionary<long, double> TextWidth = new Dictionary<long, double>();
            /// <summary>Quantity / location texts (by text id) and what they count, for "Atualizar Fichas".</summary>
            public readonly Dictionary<long, CountTag> Tags = new Dictionary<long, CountTag>();
        }

        // ================================================================== entry
        public static LegendResult Build(Document doc, LegendProbe p, LegendRequest r, LegendProgress prog = null)
        {
            prog = prog ?? new LegendProgress();
            _cleanBase = null;
            _cleanBaseLeft = -1;
            PhaseId = r.PhaseId ?? ElementId.InvalidElementId;
            var res = new LegendResult();
            prog.Stage(0, "Preparando…");
            LegendTemplate t = r.Template;
            var missing = new HashSet<string>();
            if (r.FillMarks) res.MarksFilled = FillMarks(doc, r.Types);
            if (t.ForceModel)
                foreach (var x in r.Types) if (x.InstanceId != ElementId.InvalidElementId && !x.IsHost) x.ModelMode = true;

            // the sheet decides the card size ("fit the cards to the sheet")
            TitleBlockInfo tb = r.Sheets.Enabled ? p.TitleBlocks.FirstOrDefault(x => x.Id == r.Sheets.TitleBlock) : null;
            if (r.Sheets.Enabled && tb == null) res.Problems.Add("Pranchas: carimbo não encontrado; as legendas foram criadas sem pranchas.");
            if (tb != null && r.Sheets.FitCards)
            {
                CardFromSheet(tb, r.Sheets, out double w, out double h);
                t = t.Clone();
                t.FixedSize = true;
                t.WidthMm = w;
                t.HeightMm = h;
            }

            var ctx0 = new Ctx
            {
                Doc = doc,
                Invisible = InvisibleLines(doc),
                DimType = Named<DimensionType>(doc, t.DimType),
                MarkType = Named<TextNoteType>(doc, t.MarkTextType)?.Id ?? LargestTextType(doc),
                SizeType = Named<TextNoteType>(doc, t.SizeTextType)?.Id ?? doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType),
                BodyType = Named<TextNoteType>(doc, t.BodyTextType)?.Id ?? doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType)
            };

            // typical details (relação layout): the type's own choice, else the template's
            List<ElementId> templateDetails = ResolveDetails(p, t.Details);
            Func<LegendCandidate, List<ElementId>> detailsOf = x =>
                r.TypeDetails != null && r.TypeDetails.TryGetValue(x.Id, out var own) && own != null ? own : templateDetails;
            bool relacao = t.Layout == 1;

            // one scale for all cards: the largest every card accepts (measured in a scratch legend)
            int shared = 0;
            if (t.SameScale && t.ScaleMode != 0 && t.FixedSize && r.Types.Count > 1)
            {
                prog.Stage(0, "Medindo os desenhos para a escala única…");
                View scratch = r.Types.Any(x => !x.ModelMode) ? NewLegend(doc, p) : null;
                for (int ti = 0; ti < r.Types.Count; ti++)
                {
                    var type = r.Types[ti];
                    if (prog.Cancelled()) break;
                    prog.Report(ti, r.Types.Count, "Medindo " + type.Title);
                    try
                    {
                        List<Comp> comps;
                        if (type.ModelMode) comps = ModelComps(doc, type, t);
                        else
                        {
                            if (!scratch.IsValidObject) scratch = NewLegend(doc, p);
                            comps = CreateComps(doc, scratch, p, t, type, null, res, false);
                        }
                        if (comps == null || comps.Count == 0) continue;
                        bool hasElev = comps.Any(x => x.Key != Plan);
                        double sill = t.FloorLine && hasElev ? Sill(doc, type) : 0;
                        int best;
                        if (relacao) best = RelBestScale(t, comps, sill, RelGroupCount(doc, type, t), detailsOf(type).Count > 0);
                        else
                        {
                            int rowCount = t.Rows.Count(row => !string.IsNullOrWhiteSpace(RowValue(doc, type, row.Source)));
                            best = BestScale(t, comps, sill, rowCount);
                        }
                        shared = Math.Max(shared, best > 0 ? best : StandardScales.Last());
                        foreach (var x in comps) if (x.E != null && x.E.IsValidObject) doc.Delete(x.E.Id);
                    }
                    catch (Exception ex) { Logger.Warn("[JA Tools] Medição de " + type.Title + ": " + ex.Message); }
                }
                if (scratch != null && scratch.IsValidObject) doc.Delete(scratch.Id);
                if (shared > 0) res.ScaleChanges.Add($"Escala única de todas as fichas: 1:{shared}");
            }

            var cards = new List<CardInfo>();
            prog.Stage(1, "Desenhando as fichas…");
            for (int ti = 0; ti < r.Types.Count; ti++)
            {
                var type = r.Types[ti];
                if (prog.Cancelled()) { res.Cancelled = true; break; }
                prog.Report(ti, r.Types.Count, "Ficha " + (ti + 1) + " de " + r.Types.Count + ": " + type.Title + (type.ModelMode ? " (vistas do modelo)" : ""));
                try
                {
                    string name = (r.NamePattern ?? "{marca}")
                        .Replace("{marca}", string.IsNullOrEmpty(type.Mark) ? type.TypeName : type.Mark)
                        .Replace("{tipo}", type.TypeName).Replace("{familia}", type.Family).Replace("{categoria}", type.Category);
                    View v = NewLegend(doc, p);
                    RevitUtil.Rename(v, name);
                    var ctx = new Ctx
                    {
                        Doc = doc, View = v, Invisible = ctx0.Invisible, DimType = ctx0.DimType, MarkType = ctx0.MarkType, SizeType = ctx0.SizeType, BodyType = ctx0.BodyType,
                        Overlay = tb != null       // on sheets: texts and lines are created on the sheet
                    };
                    var modelViews = new List<KeyValuePair<string, View>>();
                    double[] size = relacao
                        ? BuildRelacaoCard(ctx, p, t, type, missing, res, shared, modelViews, detailsOf(type))
                        : BuildCard(ctx, p, t, type, missing, res, shared, modelViews);
                    if (size == null) { if (v.IsValidObject) doc.Delete(v.Id); continue; }
                    res.Legends.Add(v);
                    var card = new CardInfo { View = v, Size = size, ModelViews = modelViews };
                    if (ctx.Overlay) TakeOverlay(ctx, card);
                    else
                        foreach (var kv in ctx.Tags) ApplyCountTag(doc.GetElement(new ElementId(kv.Key)) as TextNote, kv.Value);
                    cards.Add(card);
                }
                catch (Exception ex)
                {
                    Logger.Error("[JA Tools] Ficha de " + type.Title + " falhou.", ex);
                    res.Problems.Add(type.Title + ": " + ex.Message);
                }
            }
            prog.Report(r.Types.Count, r.Types.Count, "Fichas prontas.");

            if (tb != null && cards.Count > 0 && !res.Cancelled)
            {
                prog.Stage(2, "Montando as pranchas…");
                try { PlaceOnSheets(doc, tb, r.Sheets, cards, res, prog, ctx0.BodyType); }
                catch (Exception ex)
                {
                    Logger.Error("[JA Tools] Pranchas das fichas falharam.", ex);
                    res.Problems.Add("Pranchas: " + ex.Message);
                }
            }
            else if (cards.Any(x => x.ModelViews.Count > 0))
                res.Problems.Add("As vistas do modelo foram criadas, mas só são montadas dentro da ficha quando as fichas vão para pranchas (aba Pranchas).");
            if (_cleanBase != null && _cleanBase.IsValidObject) { try { doc.Delete(_cleanBase.Id); } catch { } }
            _cleanBase = null;
            foreach (string m in missing) res.Problems.Add(m);
            return res;
        }

        /// <summary>
        /// Empty legend: a duplicate of the base legend with everything that came along removed
        /// (otherwise stray content widens the legend and its viewport is placed off-centre).
        /// </summary>
        private static View NewLegend(Document doc, LegendProbe p)
        {
            if (p.BaseLegend == null)
                return ViewDrafting.Create(doc, p.DraftingTypeId);     // no legend in the project: the card is a drafting view
            if (_cleanBase == null || !_cleanBase.IsValidObject) _cleanBase = MakeCleanBase(doc, p.BaseLegend);
            View v = doc.GetElement(_cleanBase.Duplicate(ViewDuplicateOption.Duplicate)) as View;
            // a duplicate of a base that is already empty brings nothing: no second clean (2 regenerations less per card)
            if (_cleanBaseLeft == 0) return v;
            int left = Clean(doc, v);
            if (left > 0) Logger.Warn($"[JA Tools] Legenda nova ainda com {left} elemento(s) copiado(s).");
            return v;
        }

        /// <summary>Empty legend used as the base of every card in this run (deleted at the end).
        /// Duplicating a legend can bring its content along (e.g. a card made in an earlier run),
        /// which would overlap the new card and push its viewport off the sheet.</summary>
        [ThreadStatic] private static View _cleanBase;
        /// <summary>Elements left in the clean base (-1 = not measured).</summary>
        [ThreadStatic] private static int _cleanBaseLeft;

        private static View MakeCleanBase(Document doc, View source)
        {
            View v = doc.GetElement(source.Duplicate(ViewDuplicateOption.Duplicate)) as View;
            _cleanBaseLeft = Clean(doc, v);
            RevitUtil.Rename(v, "JA - base temporária");
            return v;
        }

        private static readonly HashSet<long> DrawingCategories = BuildDrawingCategories();

        private static HashSet<long> BuildDrawingCategories()
        {
            var set = new HashSet<long>
            {
                (long)BuiltInCategory.OST_LegendComponents, (long)BuiltInCategory.OST_TextNotes, (long)BuiltInCategory.OST_Dimensions,
                (long)BuiltInCategory.OST_Lines, (long)BuiltInCategory.OST_DetailComponents, (long)BuiltInCategory.OST_GenericAnnotation
            };
            // names parsed at run time (not every Revit version has all of them)
            foreach (string n in new[] { "OST_RasterImages", "OST_FilledRegion", "OST_RevisionClouds", "OST_InsulationLines", "OST_IOSDetailGroups", "OST_DetailComponentTags", "OST_SpotElevations" })
                if (Enum.TryParse(n, out BuiltInCategory bic)) set.Add((long)bic);
            return set;
        }

        /// <summary>Deletes the drawing content of a view (never its internal elements). Returns how many remain.</summary>
        private static int Clean(Document doc, View v)
        {
            doc.Regenerate();     // elements created by Duplicate are only visible to a view collector after this
            // every element OWNED by the view (does not depend on what the view shows)
            Func<List<ElementId>> find = () => new FilteredElementCollector(doc).OwnedByView(v.Id).WhereElementIsNotElementType().ToElements()
                .Where(e => e.Category != null && DrawingCategories.Contains(e.Category.Id.Value))
                .Select(e => e.Id).ToList();
            var junk = find();
            foreach (ElementId id in junk)
            {
                if (doc.GetElement(id) == null) continue;     // already gone with another element (e.g. dimension of a line)
                try { doc.Delete(id); } catch { }
                if (!v.IsValidObject) return 0;
            }
            doc.Regenerate();
            return v.IsValidObject ? find().Count : 0;
        }

        /// <summary>Text type with the biggest letters (for the type mark).</summary>
        private static ElementId LargestTextType(Document doc)
        {
            TextNoteType best = null;
            double size = 0;
            foreach (TextNoteType t in new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>())
            {
                double h = t.get_Parameter(BuiltInParameter.TEXT_SIZE)?.AsDouble() ?? 0;
                if (h > size) { size = h; best = t; }
            }
            return best?.Id ?? doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
        }

        /// <summary>Card size (mm) when rows × columns fill the usable area of the sheet.</summary>
        public static void CardFromSheet(TitleBlockInfo tb, SheetPlacement s, out double w, out double h)
        {
            double usableW = tb.Width * 304.8 - s.MarginLeftMm - s.MarginRightMm;
            double usableH = tb.Height * 304.8 - s.MarginTopMm - s.MarginBottomMm - s.NoteHeightMm;
            w = Math.Max(40, usableW / Math.Max(1, s.Columns));
            h = Math.Max(40, usableH / Math.Max(1, s.Rows));
        }

        // ================================================================== components
        /// <summary>One legend component per chosen view, with type, view direction, detail level
        /// and host length set. null = Revit refused the type.</summary>
        private static List<Comp> CreateComps(Document doc, View view, LegendProbe p, LegendTemplate t, LegendCandidate type,
                                              HashSet<string> missing, LegendResult res, bool count)
        {
            p.Directions.TryGetValue(type.Category, out var dirs);
            dirs = dirs ?? new Dictionary<string, int>();
            var comps = new List<Comp>();
            foreach (string key in DirectionOrder.Where(k => t.Views.Contains(k)))
            {
                if (!dirs.TryGetValue(key, out int value))
                {
                    missing?.Add($"{DirectionLabel(key)}: o Revit não oferece essa vista para {type.Category}.");
                    continue;
                }
                Element comp = CopyComponent(doc, p, view);
                if (comp == null) continue;
                if (!SetType(doc, comp, type.Id))
                {
                    doc.Delete(comp.Id);
                    foreach (var x in comps) doc.Delete(x.E.Id);
                    if (count) res.Problems.Add($"{type.Title}: o Revit não aceitou este tipo em legenda.");
                    return null;
                }
                TrySet(comp, BuiltInParameter.LEGEND_COMPONENT_VIEW, value);
                SetDetailLevel(comp, t.DetailLevel);
                if (type.IsHost) TrySetDouble(comp, BuiltInParameter.LEGEND_COMPONENT_LENGTH, RevitUtil.M(Math.Max(0.1, t.HostLengthM)));
                comps.Add(new Comp { Key = key, E = comp });
            }
            if (comps.Count == 0) return comps;
            // one regeneration for every component of the card (was one per component)
            doc.Regenerate();
            var measured = new List<Comp>();
            foreach (Comp x in comps)
            {
                BoundingBoxXYZ bb = x.E.get_BoundingBox(view);
                if (bb == null) { doc.Delete(x.E.Id); continue; }
                x.W = bb.Max.X - bb.Min.X;
                x.H = bb.Max.Y - bb.Min.Y;
                measured.Add(x);
                if (count) res.Components++;
            }
            return measured;
        }

        /// <summary>Do the drawings fit the card at 1:scale? (estimate in printed mm)</summary>
        private static bool Fits(LegendTemplate t, List<Comp> comps, double sill, int rowCount, int scale)
        {
            if (!t.FixedSize) return true;
            var elevs = comps.Where(x => x.Key != Plan).ToList();
            Comp plan = comps.FirstOrDefault(x => x.Key == Plan);
            double gapMm = Math.Max(2, t.ViewGapMm), dimMm = 9;
            double toMm = 304.8 / scale;
            double elevW = elevs.Sum(x => x.W) * toMm + Math.Max(0, elevs.Count - 1) * gapMm + (t.DimHeight || t.DimSill ? dimMm * 1.6 : 0) + (t.FloorLine ? 20 : 0);
            double elevH = elevs.Count == 0 ? 0 : (elevs.Max(x => x.H) + sill) * toMm + (t.DimDepth ? dimMm : 0);
            double planH = plan == null ? 0 : plan.H * toMm + (t.DimWidth ? dimMm : 0) + (elevs.Count > 0 ? gapMm : 0);
            double planW = plan == null ? 0 : plan.W * toMm + (t.DimDepth || t.DimWidth ? dimMm * 1.6 : 0);
            double needW = Math.Max(elevW, planW);
            double needH = elevH + planH;
            double areaW = t.WidthMm - (t.MarkLeft ? t.LeftColumnMm : 0) - 2 * t.PaddingMm;
            double areaH = t.HeightMm - 2 * t.PaddingMm - (t.MarkLeft ? 0 : 14) - (rowCount * Math.Max(4, t.RowHeightMm) + (rowCount > 0 ? 3 : 0));
            return needW <= areaW && needH <= areaH;
        }

        /// <summary>Scale for one card according to the template's mode. 0 = nothing fits.</summary>
        private static int BestScale(LegendTemplate t, List<Comp> comps, double sill, int rowCount)
        {
            int fixedScale = Math.Max(1, t.Scale);
            if (!t.FixedSize) return fixedScale;      // the card grows with the drawing
            switch (t.ScaleMode)
            {
                case 0: return fixedScale;
                case 1:
                    if (Fits(t, comps, sill, rowCount, fixedScale)) return fixedScale;
                    return StandardScales.Where(s => s > fixedScale).FirstOrDefault(s => Fits(t, comps, sill, rowCount, s));
                default:
                    return StandardScales.Where(s => s >= 5).FirstOrDefault(s => Fits(t, comps, sill, rowCount, s));
            }
        }

        // ================================================================== one card
        /// <returns>Card width, height, drawing area left, top, width, height (mm, from the card's
        /// top-left corner, top measured downwards) and scale; null when nothing could be drawn.</returns>
        private static double[] BuildCard(Ctx c, LegendProbe p, LegendTemplate t, LegendCandidate type, HashSet<string> missing, LegendResult res,
                                          int forcedScale, List<KeyValuePair<string, View>> modelViews)
        {
            Document doc = c.Doc;
            View view = c.View;

            // ---- 1. legend components (model size does not depend on the scale)
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

            // ---- 2. texts of the table
            var rowDefs = t.Rows.Select(row => new { Label = row.Label ?? "", Value = RowValue(doc, type, row.Source), row.Source })
                                .Where(x => !string.IsNullOrWhiteSpace(x.Value)).ToList();
            var rows = rowDefs.Select(x => new KeyValuePair<string, string>(x.Label, x.Value)).ToList();

            // ---- 3. scale (template mode; or the one shared by every card)
            double gapMm = Math.Max(2, t.ViewGapMm);
            double dimMm = 9;
            int scale = forcedScale > 0 ? forcedScale : BestScale(t, comps, sill, rows.Count);
            if (scale <= 0)
            {
                scale = StandardScales.Last();
                res.Problems.Add($"{type.Title}: os desenhos não cabem na ficha nem em 1:{scale}.");
            }
            else if (!Fits(t, comps, sill, rows.Count, scale))
                res.Problems.Add($"{type.Title}: os desenhos não cabem bem na ficha em 1:{scale}.");
            if (scale != t.Scale && t.ScaleMode == 1) res.ScaleChanges.Add($"{type.Title}: 1:{t.Scale} → 1:{scale}");
            try { view.Scale = scale; } catch { }
            c.K = scale / 304.8;
            double k = c.K;

            // ---- 4. header texts (mark + size) — every text of the card is created first and
            // measured after ONE regeneration
            TextNote mark = null, size = null;
            if (t.ShowMark)
                mark = Text(c, string.IsNullOrEmpty(type.Mark) ? type.TypeName : type.Mark, c.MarkType, t.MarkLeft ? HorizontalTextAlignment.Center : HorizontalTextAlignment.Left);
            string sizeText = SizeText(t.SizeFormat, type);
            if (t.ShowSize && !string.IsNullOrEmpty(sizeText))
                size = Text(c, sizeText, c.SizeType, t.MarkLeft ? HorizontalTextAlignment.Center : HorizontalTextAlignment.Left);

            // ---- 5. table texts (measured to know the row heights)
            double W = t.WidthMm * k, Hc = t.HeightMm * k, P = t.PaddingMm * k, Lc = t.MarkLeft ? t.LeftColumnMm * k : 0;
            var rowTexts = new List<TextNote[]>();
            var rowHeights = new List<double>();
            double tableLeft = Lc, tableW = W - Lc;
            bool table = t.TableLines;
            if (!t.FixedSize) { tableW = Math.Max(90 * k, tableW); }
            double labelX = tableLeft + tableW * t.LabelColumnPct / 100.0;
            double valueX = tableLeft + tableW * t.ValueColumnPct / 100.0;
            if (table)
            {
                for (int ri = 0; ri < rows.Count; ri++)
                {
                    var kv = rows[ri];
                    TextNote lab = t.ShowLabels && kv.Key.Length > 0 ? Text(c, kv.Key, c.BodyType, HorizontalTextAlignment.Left) : null;
                    double valueWidth = Math.Max(20 * k, (tableLeft + tableW) - P - valueX);
                    TextNote val = Text(c, kv.Value, c.BodyType, HorizontalTextAlignment.Left, valueWidth / scale);
                    rowTexts.Add(new[] { lab, val });
                    string src = rowDefs[ri].Source;
                    if (src == LegendSources.Quantity || src == LegendSources.Location)
                        c.Tags[val.Id.Value] = Tag(type, "row", Kv("source", src));
                }
            }
            // inline information (no table): "Rótulo: valor" lines
            TextNote info = null;
            if (!table && rows.Count > 0)
            {
                info = Text(c, string.Join("\r", rows.Select(kv => t.ShowLabels && kv.Key.Length > 0 ? kv.Key + ": " + kv.Value : kv.Value)), c.BodyType, HorizontalTextAlignment.Left);
                if (rowDefs.Any(x => x.Source == LegendSources.Quantity || x.Source == LegendSources.Location))
                    c.Tags[info.Id.Value] = Tag(type, "info", Kv("labels", t.ShowLabels ? "1" : "0") +
                                                              string.Concat(rowDefs.Select(x => Kv("row", x.Label.Replace("\t", " ") + "\t" + x.Source))));
            }
            doc.Regenerate();
            double markH = mark != null ? H(c, mark) : 0, sizeH = size != null ? H(c, size) : 0;
            if (table)
            {
                foreach (var pair in rowTexts)
                {
                    double th = Math.Max(pair[0] != null ? H(c, pair[0]) : 0, H(c, pair[1]));
                    rowHeights.Add(Math.Max(t.RowHeightMm * k, th + 2.4 * k));
                }
            }
            double tableH = rowHeights.Sum();
            double infoH = info != null ? H(c, info) + gapMm * k * 0.6 : 0;

            // ---- 6. drawing block (elevations over the floor line, plan below)
            double gap = gapMm * k, dim = dimMm * k;
            double rowW = elevs.Sum(x => x.W) + Math.Max(0, elevs.Count - 1) * gap;
            double rowH = elevs.Count == 0 ? 0 : elevs.Max(x => x.H) + sill;
            double planBlockH = plan == null ? 0 : plan.H + (t.DimWidth ? dim : 0) + (elevs.Count > 0 ? gap : 0);
            double blockW = Math.Max(rowW, plan?.W ?? 0);
            double blockH = rowH + planBlockH;

            double headerTop = 0;
            if (!t.MarkLeft && (mark != null || size != null)) headerTop = markH + (size != null ? sizeH + 1.5 * k : 0) + gap * 0.6;
            double areaLeft = Lc + P;
            double areaTop = -(P + headerTop);
            double areaW, areaH;
            if (t.FixedSize)
            {
                areaW = W - Lc - 2 * P;
                areaH = Hc - 2 * P - headerTop - tableH - infoH - (tableH > 0 ? 2 * k : 0);
            }
            else
            {
                areaW = blockW + (t.DimHeight || t.DimSill ? dim * 1.6 : 0) + (t.FloorLine ? 20 * k : 0);
                areaH = blockH;
                W = Lc + areaW + 2 * P;
                tableW = W - Lc;
                labelX = tableLeft + tableW * t.LabelColumnPct / 100.0;
                valueX = tableLeft + tableW * t.ValueColumnPct / 100.0;
                Hc = P + headerTop + areaH + gap + infoH + tableH + P;
            }
            double blockLeft = areaLeft + (areaW - blockW) / 2;
            double blockTop = areaTop - Math.Max(0, (areaH - blockH) / 2);

            if (!model) DrawBlock(c, t, type, elevs, plan, sill, nomW, nomH, blockLeft, blockTop, blockW, rowW, rowH, gap);

            // ---- 7. header placement
            if (t.MarkLeft)
            {
                double colCx = Lc / 2;
                double groupH = markH + (t.MarkUnderline && mark != null ? 3 * k : 0) + (size != null ? sizeH + 1.5 * k : 0);
                double y = -Hc / 2 + groupH / 2;
                if (mark != null) { Place(c, mark, colCx, y, true); y -= markH + 1.5 * k; }
                if (t.MarkUnderline && mark != null)
                {
                    double half = Math.Max(Width(c, mark), size != null ? Width(c, size) : 0) / 2 + 3 * k;
                    half = Math.Min(half, Lc / 2 - 1.5 * k);
                    Sep(c, new XYZ(colCx - half, y, 0), new XYZ(colCx + half, y, 0));
                    y -= 1.5 * k;
                }
                if (size != null) Place(c, size, colCx, y, true);
            }
            else
            {
                double y = -P;
                if (mark != null) { Place(c, mark, P, y, false); y -= markH + 1.5 * k; }
                if (size != null) Place(c, size, P, y, false);
            }

            // ---- 8. table / info
            if (table && rowTexts.Count > 0)
            {
                double y = -Hc + tableH;
                if (!t.FixedSize) y = -(Hc - P) + tableH;
                for (int i = 0; i < rowTexts.Count; i++)
                {
                    double rh = rowHeights[i];
                    Sep(c, new XYZ(tableLeft, y, 0), new XYZ(tableLeft + tableW, y, 0));
                    var pair = rowTexts[i];
                    double th = H(c, pair[1]);
                    double ty = y - Math.Max(1.2 * k, (rh - th) / 2);
                    if (pair[0] != null) Place(c, pair[0], labelX, ty, false);
                    Place(c, pair[1], valueX, ty, false);
                    y -= rh;
                }
            }
            if (info != null)
            {
                double y = t.FixedSize ? -Hc + P + H(c, info) : -(Hc - P) + H(c, info);
                Place(c, info, areaLeft, y, false);
            }

            // ---- 9. frame (invisible when "no frame" so every card has the same extents)
            Frame(c, t.Frame, t.Frame || t.FixedSize, W, Hc);
            // the divider stays a visible line even without the outer frame
            if (t.MarkLeft && t.Divider && Lc > 0) Sep(c, new XYZ(Lc, 0, 0), new XYZ(Lc, -Hc, 0));
            doc.Regenerate();

            // ---- 10. model mode: the views themselves (placed in the drawing area on the sheet)
            if (model) CreateModelViews(c, p, t, type, scale, modelViews, res);
            return new[] { W / k, Hc / k, areaLeft / k, -areaTop / k, areaW / k, areaH / k, scale };
        }

        /// <summary>Legend components of one card placed in their block: elevations over the floor line, plan
        /// below, dimensions taken from the real geometry of the first instance.</summary>
        private static void DrawBlock(Ctx c, LegendTemplate t, LegendCandidate type, List<Comp> elevs, Comp plan, double sill,
                                      double nomW, double nomH, double blockLeft, double blockTop, double blockW,
                                      double rowW, double rowH, double gap)
        {
            Document doc = c.Doc;
            View view = c.View;
            double k = c.K;
            // real geometry of the first instance (bancadas, cabinets...): null for doors / windows
            Facts f = ModelFacts(doc, type);
            double dimOff = 5 * k;
            if (elevs.Count > 0)
            {
                double floorY = blockTop - rowH;
                double x = blockLeft + (blockW - rowW) / 2;
                foreach (var e in elevs)
                {
                    BoundingBoxXYZ bb = e.E.get_BoundingBox(view);
                    // the legend box may carry an even margin around the drawing: align centres, not edges
                    double dy = f != null
                        ? floorY + sill + (f.Top - f.Bottom) / 2 - (bb.Min.Y + bb.Max.Y) / 2
                        : floorY + sill - bb.Min.Y;
                    ElementTransformUtils.MoveElement(doc, e.E.Id, new XYZ(x - bb.Min.X, dy, 0));
                    x += e.W + gap;
                }
                doc.Regenerate();
                DetailCurve floor = null;
                if (t.FloorLine)
                {
                    double l = blockLeft + (blockW - rowW) / 2 - 12 * k, rr = blockLeft + (blockW + rowW) / 2 + 12 * k;
                    floor = Seg(c, new XYZ(l, floorY, 0), new XYZ(rr, floorY, 0), null);
                }

                // one chained dimension on the right of the first elevation:
                // finished floor → base of the element → top of the element (bancada: its slab)
                var e0 = elevs[0];
                BoundingBoxXYZ eb = e0.E.get_BoundingBox(view);
                double ecx = (eb.Min.X + eb.Max.X) / 2;
                bool sideView = e0.Key == Left || e0.Key == Right;
                double halfVis = f != null ? (sideView ? f.Depth : f.Width) / 2 : (eb.Max.X - eb.Min.X) / 2;
                double right = ecx + halfVis;
                double bottom, top;
                if (f != null)
                {
                    double baseY = floorY + sill - f.Bottom;          // level of the instance
                    bottom = baseY + f.BodyBottom;
                    top = baseY + f.BodyTop;
                }
                else
                {
                    bottom = eb.Min.Y;
                    top = nomH > 0 ? Math.Min(eb.Max.Y, bottom + nomH) : eb.Max.Y;
                    if (top - bottom < 1e-3) top = eb.Max.Y;
                }
                bool withSill = t.DimSill && floor != null && bottom - floorY > 1e-3;
                var ys = new List<double> { bottom };
                if (t.DimHeight) ys.Add(top);
                if (t.DimHeight || withSill)
                    DimY(c, ys, right, right + dimOff, withSill ? floor.GeometryCurve.Reference : null, floorY);

                // no plan: width under the first elevation (below the floor line)
                if (plan == null && t.DimWidth && !sideView)
                {
                    double w = nomW > 1e-3 ? nomW : f != null ? f.BodyWidth : eb.Max.X - eb.Min.X;
                    double edge = Math.Min(floorY, f != null ? floorY + sill : eb.Min.Y);
                    DimX(c, ecx - w / 2, ecx + w / 2, edge, edge - dimOff);
                }
                // no plan: depth under a side elevation
                Comp side = plan == null && WantsDepth(t, type) ? elevs.FirstOrDefault(v => v.Key == Left || v.Key == Right) : null;
                if (side != null)
                {
                    BoundingBoxXYZ sb = side.E.get_BoundingBox(view);
                    double scx = (sb.Min.X + sb.Max.X) / 2;
                    double d = type.Depth > 1e-3 ? type.Depth : f != null ? f.BodyDepth : sb.Max.X - sb.Min.X;
                    double edge = Math.Min(floorY, f != null ? floorY + sill : sb.Min.Y);
                    DimX(c, scx - d / 2, scx + d / 2, edge, edge - dimOff);
                }
            }
            if (plan != null)
            {
                BoundingBoxXYZ bb = plan.E.get_BoundingBox(view);
                double centerX = elevs.Count > 0 ? Center(elevs[0].E, view).X : blockLeft + blockW / 2;
                double top = elevs.Count > 0 ? blockTop - rowH - gap : blockTop;
                ElementTransformUtils.MoveElement(doc, plan.E.Id, new XYZ(centerX - (bb.Min.X + bb.Max.X) / 2, top - bb.Max.Y, 0));
                doc.Regenerate();
                bb = plan.E.get_BoundingBox(view);
                double cx = (bb.Min.X + bb.Max.X) / 2, cy = (bb.Min.Y + bb.Max.Y) / 2;
                // what is really drawn (the legend box can be larger than the drawing)
                double visW = f != null ? Math.Min(f.Width, bb.Max.X - bb.Min.X) : bb.Max.X - bb.Min.X;
                double visD = f != null ? Math.Min(f.Depth, bb.Max.Y - bb.Min.Y) : bb.Max.Y - bb.Min.Y;
                if (t.DimWidth)
                {
                    double w = nomW > 1e-3 && nomW <= visW + 1e-3 ? nomW : f != null ? f.BodyWidth : visW;
                    DimX(c, cx - w / 2, cx + w / 2, cy - visD / 2, cy - visD / 2 - dimOff);
                }
                if (WantsDepth(t, type))
                {
                    double d = type.Depth > 1e-3 && type.Depth <= visD + 1e-3 ? type.Depth : f != null ? f.BodyDepth : visD;
                    DimY(c, new List<double> { cy - d / 2, cy + d / 2 }, cx + visW / 2, cx + visW / 2 + dimOff, null, 0);
                }
            }
        }

        // ================================================================== sheets
        private static void PlaceOnSheets(Document doc, TitleBlockInfo tb, SheetPlacement s, List<CardInfo> cards, LegendResult res, LegendProgress prog,
                                          ElementId noteTextType)
        {
            double usableW = tb.Width * 304.8 - s.MarginLeftMm - s.MarginRightMm;
            double usableH = tb.Height * 304.8 - s.MarginTopMm - s.MarginBottomMm - s.NoteHeightMm;
            double cw = cards.Max(x => x.Size[0]), ch = cards.Max(x => x.Size[1]);
            int cols = s.FitCards ? Math.Max(1, s.Columns) : Math.Max(1, (int)Math.Floor(usableW / cw + 1e-6));
            int rows = s.FitCards ? Math.Max(1, s.Rows) : Math.Max(1, (int)Math.Floor(usableH / ch + 1e-6));
            if (!s.FitCards && (cw > usableW + 0.5 || ch > usableH + 0.5))
                res.Problems.Add($"Pranchas: a ficha ({cw:0} × {ch:0} mm) é maior que a área útil ({usableW:0} × {usableH:0} mm).");
            int perSheet = cols * rows;

            var usedNumbers = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .Select(v => v.SheetNumber), StringComparer.OrdinalIgnoreCase);
            string number = string.IsNullOrWhiteSpace(s.FirstNumber) ? "A101" : s.FirstNumber.Trim();
            ElementId vpType = null;

            for (int start = 0; start < cards.Count; start += perSheet)
            {
                if (prog.Cancelled()) { res.Cancelled = true; return; }
                ViewSheet sheet = ViewSheet.Create(doc, tb.Id);
                while (usedNumbers.Contains(number)) number = NextNumber(number);
                try { sheet.SheetNumber = number; } catch { }
                usedNumbers.Add(number);
                number = NextNumber(number);
                try { sheet.Name = string.IsNullOrWhiteSpace(s.Name) ? "DETALHES" : s.Name; } catch { }
                doc.Regenerate();
                Element inst = new FilteredElementCollector(doc, sheet.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).FirstElement();
                BoundingBoxXYZ bb = inst?.get_BoundingBox(sheet);
                double left, top;
                if (bb != null && Math.Abs((bb.Max.X - bb.Min.X) - tb.Width) < 0.05 && Math.Abs((bb.Max.Y - bb.Min.Y) - tb.Height) < 0.05)
                {
                    left = bb.Min.X; top = bb.Max.Y;
                }
                else
                {
                    // fall back to the sheet outline (paper extents)
                    BoundingBoxUV so = sheet.Outline;
                    left = so.Min.U; top = so.Max.V;
                }
                Logger.Info($"[JA Tools] Prancha {sheet.SheetNumber}: origem ({left * 304.8:0}, {top * 304.8:0}) mm, carimbo {tb.Width * 304.8:0} x {tb.Height * 304.8:0} mm.");
                res.Sheets.Add(sheet);
                // the note sits right under the last row of cards
                double cardsH = s.FitCards ? usableH : Math.Min(usableH, rows * ch);
                DrawSheetNote(doc, sheet, s, left, top, s.FitCards ? usableW : Math.Min(usableW, cols * cw), cardsH, noteTextType);

                for (int i = start; i < Math.Min(cards.Count, start + perSheet); i++)
                {
                    prog.Report(i, cards.Count, $"Prancha {sheet.SheetNumber}: {cards[i].View.Name}");
                    int idx = i - start, col = idx % cols, row = idx / cols;
                    double cx = left + (s.MarginLeftMm + cw * (col + 0.5)) / 304.8;
                    double cy = top - (s.MarginTopMm + ch * (row + 0.5)) / 304.8;
                    var center = new XYZ(cx, cy, 0);
                    Viewport port = PlaceViewport(doc, sheet, cards[i].View, center, s, ref vpType, res);
                    if (port == null) continue;
                    // texts and lines around the real centre of the viewport (= centre of the card)
                    XYZ real = center;
                    try { XYZ bc = port.GetBoxCenter(); real = new XYZ(bc.X, bc.Y, 0); } catch { }
                    DrawOverlay(doc, sheet, cards[i], real, res);
                    if (cards[i].ModelViews.Count > 0) PlaceModelViews(doc, sheet, cards[i], center, s, ref vpType, res);
                }
            }
            prog.Report(cards.Count, cards.Count, "Pranchas prontas.");
        }

        /// <summary>Viewport centred exactly on a point (its real outline is measured and corrected).</summary>
        private static Viewport PlaceViewport(Document doc, ViewSheet sheet, View view, XYZ center, SheetPlacement s, ref ElementId vpType, LegendResult res)
        {
            if (!Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id))
            {
                res.Problems.Add($"{view.Name}: o Revit não permitiu colocar na prancha.");
                return null;
            }
            Viewport vp = Viewport.Create(doc, sheet.Id, view.Id, center);
            if (vp == null) return null;
            if (s.HideViewportTitle)
            {
                if (vpType == null) vpType = NoTitleViewportType(doc, vp);
                if (vpType != null && vpType != ElementId.InvalidElementId) { try { vp.ChangeTypeId(vpType); } catch { } }
            }
            doc.Regenerate();
            MoveViewportTo(doc, vp, center);
            try
            {
                Outline o = vp.GetBoxOutline();
                Logger.Info($"[JA Tools] Viewport '{view.Name}' em {sheet.SheetNumber}: alvo ({center.X * 304.8:0},{center.Y * 304.8:0}) mm, " +
                            $"contorno ({o.MinimumPoint.X * 304.8:0},{o.MinimumPoint.Y * 304.8:0})-({o.MaximumPoint.X * 304.8:0},{o.MaximumPoint.Y * 304.8:0}) mm.");
            }
            catch { }
            return vp;
        }

        private static void MoveViewportTo(Document doc, Viewport vp, XYZ center)
        {
            try
            {
                vp.SetBoxCenter(new XYZ(center.X, center.Y, 0));
                doc.Regenerate();
                XYZ actual = vp.GetBoxCenter();
                if (actual.DistanceTo(new XYZ(center.X, center.Y, actual.Z)) > 0.5 / 304.8)
                {
                    // SetBoxCenter did not take: move the element by the remaining difference
                    ElementTransformUtils.MoveElement(doc, vp.Id, new XYZ(center.X - actual.X, center.Y - actual.Y, 0));
                    doc.Regenerate();
                    actual = vp.GetBoxCenter();
                    Logger.Warn($"[JA Tools] Viewport ajustada por movimento: centro final ({actual.X * 304.8:0},{actual.Y * 304.8:0}) mm, alvo ({center.X * 304.8:0},{center.Y * 304.8:0}) mm.");
                }
            }
            catch (Exception ex) { Logger.Warn("[JA Tools] Ajuste da viewport: " + ex.Message); }
        }

        /// <summary>Model-mode views inside the drawing area of their ficha: elevations side by side
        /// (bottoms aligned), plan below the first one, the group centred in the area.</summary>
        private static void PlaceModelViews(Document doc, ViewSheet sheet, CardInfo card, XYZ cardCenter, SheetPlacement s, ref ElementId vpType, LegendResult res)
        {
            double mm = 1 / 304.8;
            double cardLeft = cardCenter.X - card.Size[0] * mm / 2, cardTop = cardCenter.Y + card.Size[1] * mm / 2;
            double aL = cardLeft + card.Size[2] * mm, aT = cardTop - card.Size[3] * mm, aW = card.Size[4] * mm, aH = card.Size[5] * mm;
            XYZ areaCenter = new XYZ(aL + aW / 2, aT - aH / 2, 0);
            double gap = 8 * mm;

            var placed = new List<KeyValuePair<string, Viewport>>();
            foreach (var kv in card.ModelViews)
            {
                Viewport vp = PlaceViewport(doc, sheet, kv.Value, areaCenter, s, ref vpType, res);
                if (vp != null) placed.Add(new KeyValuePair<string, Viewport>(kv.Key, vp));
            }
            if (placed.Count == 0) return;
            doc.Regenerate();
            Func<Viewport, double[]> size = port =>
            {
                Outline o = port.GetBoxOutline();
                return new[] { o.MaximumPoint.X - o.MinimumPoint.X, o.MaximumPoint.Y - o.MinimumPoint.Y };
            };
            var elevs = placed.Where(x => x.Key != Plan).ToList();
            var plan = placed.FirstOrDefault(x => x.Key == Plan).Value;
            double rowW = elevs.Sum(x => size(x.Value)[0]) + Math.Max(0, elevs.Count - 1) * gap;
            double rowH = elevs.Count == 0 ? 0 : elevs.Max(x => size(x.Value)[1]);
            double planH = plan != null ? size(plan)[1] : 0;
            double blockH = rowH + (plan != null && elevs.Count > 0 ? gap : 0) + planH;
            double blockTop = aT - Math.Max(0, (aH - blockH) / 2);
            if (blockH > aH + 1e-6 || Math.Max(rowW, plan != null ? size(plan)[0] : 0) > aW + 1e-6)
                res.Problems.Add($"{card.View.Name}: as vistas do modelo passam do espaço da ficha (reduza a escala).");

            double xPos = aL + (aW - rowW) / 2, firstCx = aL + aW / 2;
            for (int i = 0; i < elevs.Count; i++)
            {
                double[] sz = size(elevs[i].Value);
                var target = new XYZ(xPos + sz[0] / 2, blockTop - rowH + sz[1] / 2, 0);
                if (i == 0) firstCx = target.X;
                MoveViewportTo(doc, elevs[i].Value, target);
                xPos += sz[0] + gap;
            }
            if (plan != null)
            {
                double[] sz = size(plan);
                double py = (elevs.Count > 0 ? blockTop - rowH - gap : blockTop) - sz[1] / 2;
                MoveViewportTo(doc, plan, new XYZ(firstCx, py, 0));
            }
        }

        /// <summary>"A106" → "A107", "A-09" → "A-10", "10" → "11", "X" → "X1".</summary>
        public static string NextNumber(string s)
        {
            int end = s.Length;
            int i = end;
            while (i > 0 && char.IsDigit(s[i - 1])) i--;
            if (i == end) return s + "1";
            string digits = s.Substring(i);
            long n = long.Parse(digits, CultureInfo.InvariantCulture) + 1;
            return s.Substring(0, i) + n.ToString(new string('0', digits.Length), CultureInfo.InvariantCulture);
        }

        /// <summary>A viewport type with "Mostrar título" = Não (created from the current one if needed).</summary>
        private static ElementId NoTitleViewportType(Document doc, Viewport vp)
        {
            BuiltInParameter showLabel;
            if (!Enum.TryParse("VIEWPORT_ATTR_SHOW_LABEL", out showLabel)) return null;
            try
            {
                foreach (ElementId id in vp.GetValidTypes())
                {
                    Parameter prm = doc.GetElement(id)?.get_Parameter(showLabel);
                    if (prm != null && prm.StorageType == StorageType.Integer && prm.AsInteger() == 0) return id;
                }
                var current = doc.GetElement(vp.GetTypeId()) as ElementType;
                if (current == null) return null;
                string name = "JA - Sem título";
                ElementType dup = null;
                foreach (ElementId id in vp.GetValidTypes())
                    if ((doc.GetElement(id) as ElementType)?.Name == name) dup = doc.GetElement(id) as ElementType;
                if (dup == null) dup = current.Duplicate(name);
                dup.get_Parameter(showLabel)?.Set(0);
                return dup.Id;
            }
            catch (Exception ex)
            {
                Logger.Warn("[JA Tools] Tipo de viewport sem título: " + ex.Message);
                return null;
            }
        }

        // ================================================================== values
        private static string RowValue(Document doc, LegendCandidate type, string source)
        {
            switch (source)
            {
                case LegendSources.Quantity: return type.Count.ToString(CultureInfo.CurrentCulture);
                case LegendSources.Location: return Locations(doc, type.Id);
                case LegendSources.Size: return SizeText(type.Depth > 1e-6 ? "{L} x {P} x {A} m" : "{L} m x {A} m", type);
                case LegendSources.Family: return type.Family;
                case LegendSources.TypeName: return type.TypeName;
                case LegendSources.Mark: return type.Mark;
                case LegendSources.Description: return type.Description;
            }
            if (string.IsNullOrEmpty(source)) return null;
            Element t = doc.GetElement(type.Id);
            string v = ParamText(t, source);
            if (string.IsNullOrWhiteSpace(v)) v = ParamText(FirstInstance(doc, type.Id), source);
            return v;
        }

        public static string SizeText(string format, LegendCandidate type)
        {
            if (type.IsHost)
                return type.Width > 0 ? "e = " + Math.Round(type.Width * 304.8).ToString(CultureInfo.InvariantCulture) + " mm" : "";
            if (type.Width <= 0 && type.Height <= 0) return "";
            return (format ?? "{L} m x {A} m")
                .Replace("{L}", (type.Width * 0.3048).ToString("0.00", CultureInfo.InvariantCulture))
                .Replace("{A}", (type.Height * 0.3048).ToString("0.00", CultureInfo.InvariantCulture))
                .Replace("{P}", (type.Depth * 0.3048).ToString("0.00", CultureInfo.InvariantCulture));
        }

        /// <summary>Rooms where the instances of the type are (doors: both sides), without repeats — in
        /// the chosen phase. Read once per type and run (the scale test and the card both ask).</summary>
        private static string Locations(Document doc, ElementId typeId) =>
            SessionCache.Get("legendLoc|" + doc.GetHashCode() + "|" + typeId.Value + "|" + PhaseKey, () => ReadLocations(doc, typeId));

        private static string ReadLocations(Document doc, ElementId typeId)
        {
            var names = new List<string>();
            try
            {
                foreach (FamilyInstance fi in InstancesOfType(doc, typeId).Select(id => doc.GetElement(id)).OfType<FamilyInstance>())
                {
                    foreach (Element room in RoomsOf(doc, fi))
                    {
                        string n = room?.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString();
                        if (!string.IsNullOrWhiteSpace(n) && !names.Contains(n.Trim(), StringComparer.OrdinalIgnoreCase)) names.Add(n.Trim());
                    }
                }
            }
            catch { }
            return string.Join(", ", names);
        }

        private static Element SafeRoom(Func<Element> get)
        {
            try { return get(); } catch { return null; }
        }

        /// <summary>Depth dimension: when the template asks, and always for everything that is not a door or window
        /// (bancadas, cabinets, furniture are dimensioned width × depth in the plan).</summary>
        private static bool WantsDepth(LegendTemplate t, LegendCandidate type) =>
            t.DimDepth || (t.DimWidth && type.CategoryId != (long)BuiltInCategory.OST_Doors && type.CategoryId != (long)BuiltInCategory.OST_Windows);

        /// <summary>
        /// Height of the base of the element above the finished floor (feet; 0 when it stands on it):
        /// doors 0; windows their sill height; anything else (bancadas, wall cabinets, shelves...) measured
        /// from the geometry of the first instance — lowest point of its solids minus the elevation of its level.
        /// </summary>
        private static double Sill(Document doc, LegendCandidate type)
        {
            try
            {
                if (type.CategoryId == (long)BuiltInCategory.OST_Doors) return 0;
                Element inst = FirstInstance(doc, type.Id);
                Parameter p = inst?.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM);
                if (type.CategoryId == (long)BuiltInCategory.OST_Windows)
                {
                    if (p != null && p.HasValue && p.AsDouble() > 1e-6) return p.AsDouble();
                    Element te = doc.GetElement(type.Id);
                    foreach (string n in new[] { "Altura do peitoril", "Altura de peitoril", "Peitoril", "Sill Height", "Default Sill Height" })
                    {
                        Parameter q = inst?.LookupParameter(n) ?? te?.LookupParameter(n);
                        if (q != null && q.StorageType == StorageType.Double && q.HasValue && q.AsDouble() > 1e-6) return q.AsDouble();
                    }
                    return 0;
                }
                if (inst == null) return 0;

                Facts f = ModelFacts(doc, type);
                if (f != null && f.Bottom < 30) return Math.Max(0, f.Bottom);

                // fallbacks: sill / offset parameters
                if (p != null && p.HasValue && p.AsDouble() > 1e-6) return p.AsDouble();
                double off = ByName(inst, new[] { "INSTANCE_ELEVATION_PARAM", "INSTANCE_FREE_HOST_OFFSET_PARAM" },
                                    new[] { "Elevação a partir do nível", "Deslocamento a partir do hospedeiro", "Elevation from Level", "Offset from Host" });
                if (off > 1e-6 && off < 30) return off;
            }
            catch (Exception ex) { Logger.Debug("[JA Tools] Altura do piso de " + type.Title + ": " + ex.Message); }
            return 0;
        }

        /// <summary>
        /// What the first instance really looks like (feet): extents of all its solids and of its main body
        /// (the big solids only — a bancada's slab without the sink bowl and the faucet), heights above its level,
        /// width along the hand direction and depth along the facing direction.
        /// </summary>
        private sealed class Facts
        {
            public double Bottom, Top, BodyBottom, BodyTop;
            public double Width, Depth, BodyWidth, BodyDepth;
        }

        private sealed class FactsBox { public Facts F; }

        /// <summary>Read once per type and run (the sill, the drawing and its dimensions all ask).</summary>
        private static Facts ModelFacts(Document doc, LegendCandidate type) =>
            SessionCache.Get("legendFacts|" + doc.GetHashCode() + "|" + type.Id.Value + "|" + PhaseKey,
                             () => new FactsBox { F = ReadModelFacts(doc, type) }).F;

        private static Facts ReadModelFacts(Document doc, LegendCandidate type)
        {
            if (type.CategoryId == (long)BuiltInCategory.OST_Doors || type.CategoryId == (long)BuiltInCategory.OST_Windows) return null;
            try
            {
                Element inst = FirstInstance(doc, type.Id);
                if (inst == null) return null;
                XYZ hand = XYZ.BasisX, facing = XYZ.BasisY;
                if (inst is FamilyInstance fi)
                {
                    XYZ fo = new XYZ(fi.FacingOrientation.X, fi.FacingOrientation.Y, 0);
                    XYZ ho = new XYZ(fi.HandOrientation.X, fi.HandOrientation.Y, 0);
                    if (fo.GetLength() > 1e-6) facing = fo.Normalize();
                    hand = ho.GetLength() > 1e-6 ? ho.Normalize() : facing.CrossProduct(XYZ.BasisZ).Normalize();
                }
                var solids = new List<Solid>();
                CollectSolids(inst.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine }), solids, 0);
                if (solids.Count == 0) return null;
                double maxVol = solids.Max(x => SafeVolume(x));
                double[] all = Extents(solids, hand, facing);
                double[] body = Extents(solids.Where(x => SafeVolume(x) >= maxVol * 0.1), hand, facing);
                if (all == null) return null;
                if (body == null) body = all;
                Level level = LevelOf(doc, inst, all[4]);
                if (level == null) return null;
                double z0 = level.ProjectElevation;
                return new Facts
                {
                    Bottom = all[4] - z0, Top = all[5] - z0,
                    BodyBottom = body[4] - z0, BodyTop = body[5] - z0,
                    Width = all[1] - all[0], Depth = all[3] - all[2],
                    BodyWidth = body[1] - body[0], BodyDepth = body[3] - body[2]
                };
            }
            catch (Exception ex) { Logger.Debug("[JA Tools] Geometria de " + type.Title + ": " + ex.Message); }
            return null;
        }

        private static double SafeVolume(Solid s)
        {
            try { return s.Volume; } catch { return 0; }
        }

        private static void CollectSolids(GeometryElement ge, List<Solid> list, int depth)
        {
            if (ge == null || depth > 8) return;
            foreach (GeometryObject o in ge)
            {
                if (o is Solid s) { if (SafeVolume(s) > 1e-9 && s.Edges.Size > 0) list.Add(s); }
                else if (o is GeometryInstance gi) CollectSolids(gi.GetInstanceGeometry(), list, depth + 1);
            }
        }

        /// <summary>{min, max} along hand, facing and Z of the solids' edges; null when empty.</summary>
        private static double[] Extents(IEnumerable<Solid> solids, XYZ hand, XYZ facing)
        {
            var r = new[] { double.MaxValue, double.MinValue, double.MaxValue, double.MinValue, double.MaxValue, double.MinValue };
            bool any = false;
            foreach (Solid s in solids)
                foreach (Edge e in s.Edges)
                {
                    IList<XYZ> pts;
                    try { pts = e.Tessellate(); } catch { continue; }
                    foreach (XYZ q in pts)
                    {
                        double u = q.DotProduct(hand), v = q.DotProduct(facing);
                        r[0] = Math.Min(r[0], u); r[1] = Math.Max(r[1], u);
                        r[2] = Math.Min(r[2], v); r[3] = Math.Max(r[3], v);
                        r[4] = Math.Min(r[4], q.Z); r[5] = Math.Max(r[5], q.Z);
                        any = true;
                    }
                }
            return any ? r : null;
        }

        /// <summary>The level the instance belongs to (its own, its host's, or the nearest one below it).</summary>
        private static Level LevelOf(Document doc, Element inst, double bottomZ)
        {
            Level level = doc.GetElement(inst.LevelId) as Level;
            if (level == null && inst is FamilyInstance fi)
            {
                if (fi.Host != null) level = doc.GetElement(fi.Host.LevelId) as Level;
                if (level == null)
                {
                    foreach (BuiltInParameter bip in new[] { BuiltInParameter.FAMILY_LEVEL_PARAM, BuiltInParameter.SCHEDULE_LEVEL_PARAM, BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM })
                    {
                        try
                        {
                            Parameter lp = inst.get_Parameter(bip);
                            if (lp != null && lp.StorageType == StorageType.ElementId) level = doc.GetElement(lp.AsElementId()) as Level;
                        }
                        catch { }
                        if (level != null) break;
                    }
                }
            }
            return level ?? new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .Where(l => l.ProjectElevation <= bottomZ + 1e-3)
                .OrderByDescending(l => l.ProjectElevation).FirstOrDefault();
        }

        // ---- dimensions with short invisible reference ticks right at the edge of the drawing
        /// <summary>Horizontal dimension x1–x2: ticks start at the drawing edge (edgeY), the line sits at lineY.</summary>
        private static void DimX(Ctx c, double x1, double x2, double edgeY, double lineY)
        {
            double s = Math.Sign(lineY - edgeY), k = c.K;
            var a = Seg(c, new XYZ(x1, edgeY + s * 0.5 * k, 0), new XYZ(x1, edgeY + s * 1.5 * k, 0), c.Invisible);
            var b = Seg(c, new XYZ(x2, edgeY + s * 0.5 * k, 0), new XYZ(x2, edgeY + s * 1.5 * k, 0), c.Invisible);
            TryDim(c, Line.CreateBound(new XYZ(x1, lineY, 0), new XYZ(x2, lineY, 0)),
                   new List<Reference> { a.GeometryCurve.Reference, b.GeometryCurve.Reference });
        }

        /// <summary>Vertical (chained) dimension through the heights ys; optional extra first reference (the floor line).</summary>
        private static void DimY(Ctx c, IList<double> ys, double edgeX, double lineX, Reference first, double firstY)
        {
            double s = Math.Sign(lineX - edgeX), k = c.K;
            var refs = new List<Reference>();
            var at = new List<double>();
            if (first != null) { refs.Add(first); at.Add(firstY); }
            foreach (double y in ys)
            {
                if (at.Any(v => Math.Abs(v - y) < 1e-3)) continue;      // a zero-length segment is not drawn
                refs.Add(Seg(c, new XYZ(edgeX + s * 0.5 * k, y, 0), new XYZ(edgeX + s * 1.5 * k, y, 0), c.Invisible).GeometryCurve.Reference);
                at.Add(y);
            }
            if (refs.Count < 2) return;
            TryDim(c, Line.CreateBound(new XYZ(lineX, at.Min(), 0), new XYZ(lineX, at.Max(), 0)), refs);
        }

        // ================================================================== drawing helpers
        private static TextNote Text(Ctx c, string text, ElementId typeId, HorizontalTextAlignment align, double paperWidth = 0)
        {
            var o = new TextNoteOptions(typeId) { HorizontalAlignment = align };
            if (paperWidth > 0)
            {
                // clamp to what Revit accepts, so the call never fails half-way
                try
                {
                    double min = TextElement.GetMinimumAllowedWidth(c.Doc, typeId);
                    double max = TextElement.GetMaximumAllowedWidth(c.Doc, typeId);
                    paperWidth = Math.Max(min * 1.01, Math.Min(max * 0.99, paperWidth));
                }
                catch { }
                TextNote wrapped = TextNote.Create(c.Doc, c.View.Id, XYZ.Zero, paperWidth, text, o);
                c.TextWidth[wrapped.Id.Value] = paperWidth;
                return wrapped;
            }
            return TextNote.Create(c.Doc, c.View.Id, XYZ.Zero, text, o);
        }

        /// <summary>A separation line of the card: on the sheet (overlay) or in the legend.</summary>
        private static void Sep(Ctx c, XYZ a, XYZ b)
        {
            if (c.Overlay) c.Lines.Add(new[] { a, b });
            else Seg(c, a, b, null);
        }

        /// <summary>Card frame. On sheets the legend always gets an invisible frame (the viewport is then
        /// exactly the card) and the visible one is drawn on the sheet.</summary>
        private static void Frame(Ctx c, bool visible, bool needed, double W, double Hc)
        {
            var pts = new[] { new XYZ(0, 0, 0), new XYZ(W, 0, 0), new XYZ(W, -Hc, 0), new XYZ(0, -Hc, 0) };
            if (c.Overlay)
            {
                for (int i = 0; i < 4; i++) Seg(c, pts[i], pts[(i + 1) % 4], c.Invisible);
                if (visible) for (int i = 0; i < 4; i++) c.Lines.Add(new[] { pts[i], pts[(i + 1) % 4] });
                return;
            }
            if (!needed) return;
            for (int i = 0; i < 4; i++) Seg(c, pts[i], pts[(i + 1) % 4], visible ? null : c.Invisible);
        }

        /// <summary>Overlay mode: the card's texts leave the legend (kept as data for the sheet).</summary>
        private static void TakeOverlay(Ctx c, CardInfo card)
        {
            card.Lines.AddRange(c.Lines);
            var texts = new FilteredElementCollector(c.Doc).OwnedByView(c.View.Id).OfClass(typeof(TextNote)).Cast<TextNote>().ToList();
            foreach (TextNote tn in texts)
            {
                try
                {
                    card.Texts.Add(new OverlayText
                    {
                        Text = tn.Text,
                        TypeId = tn.GetTypeId(),
                        Align = tn.HorizontalAlignment,
                        PaperWidth = c.TextWidth.TryGetValue(tn.Id.Value, out double w) ? w : 0,
                        Coord = tn.Coord,
                        Tag = c.Tags.TryGetValue(tn.Id.Value, out CountTag tag) ? tag : null
                    });
                    c.Doc.Delete(tn.Id);
                }
                catch (Exception ex) { Logger.Warn("[JA Tools] Texto da ficha não levado para a prancha: " + ex.Message); }
            }
        }

        /// <summary>The card's texts and lines on the sheet, where they were in the legend: the viewport
        /// is centred on the card, so sheet = centre + (legend − card centre) / scale.</summary>
        private static void DrawOverlay(Document doc, ViewSheet sheet, CardInfo card, XYZ center, LegendResult res)
        {
            if (card.Texts.Count == 0 && card.Lines.Count == 0) return;
            double scale = card.Size[6] > 0 ? card.Size[6] : 1;
            double k = scale / 304.8;
            var legendCenter = new XYZ(card.Size[0] * k / 2, -card.Size[1] * k / 2, 0);
            Func<XYZ, XYZ> map = q => new XYZ(center.X + (q.X - legendCenter.X) / scale, center.Y + (q.Y - legendCenter.Y) / scale, 0);
            foreach (XYZ[] l in card.Lines)
            {
                try
                {
                    XYZ a = map(l[0]), b = map(l[1]);
                    if (a.DistanceTo(b) < 0.0005) continue;
                    doc.Create.NewDetailCurve(sheet, Line.CreateBound(a, b));
                }
                catch (Exception ex) { Logger.Warn("[JA Tools] Linha da ficha na prancha: " + ex.Message); }
            }
            foreach (OverlayText x in card.Texts)
            {
                try
                {
                    var o = new TextNoteOptions(x.TypeId) { HorizontalAlignment = x.Align };
                    string text = (x.Text ?? "").TrimEnd('\r', '\n');
                    TextNote made = x.PaperWidth > 0
                        ? TextNote.Create(doc, sheet.Id, map(x.Coord), x.PaperWidth, text, o)
                        : TextNote.Create(doc, sheet.Id, map(x.Coord), text, o);
                    if (x.Tag != null) ApplyCountTag(made, x.Tag);
                }
                catch (Exception ex) { RevitUtil.AddOnce(res.Problems, "Texto da ficha na prancha: " + ex.Message); }
            }
        }

        private static double H(Ctx c, Element e)
        {
            BoundingBoxXYZ b = e.get_BoundingBox(c.View);
            return b == null ? 0 : b.Max.Y - b.Min.Y;
        }

        private static double Width(Ctx c, Element e)
        {
            BoundingBoxXYZ b = e.get_BoundingBox(c.View);
            return b == null ? 0 : b.Max.X - b.Min.X;
        }

        private static XYZ Center(Element e, View v)
        {
            BoundingBoxXYZ b = e.get_BoundingBox(v);
            return b == null ? XYZ.Zero : (b.Min + b.Max) / 2;
        }

        /// <summary>Moves a text so its box starts at (x, top) — or is centred on x.</summary>
        private static void Place(Ctx c, Element e, double x, double top, bool centered)
        {
            // no regeneration: every text is measured after the one regeneration that follows its
            // creation, and each is placed once (moving it does not change its size)
            BoundingBoxXYZ b = e.get_BoundingBox(c.View);
            if (b == null) return;
            double dx = centered ? x - (b.Min.X + b.Max.X) / 2 : x - b.Min.X;
            ElementTransformUtils.MoveElement(c.Doc, e.Id, new XYZ(dx, top - b.Max.Y, 0));
        }

        private static DetailCurve Seg(Ctx c, XYZ a, XYZ b, GraphicsStyle style)
        {
            if (a.DistanceTo(b) < 0.003) b = a + ((b - a).GetLength() > 1e-9 ? (b - a).Normalize() : XYZ.BasisX).Multiply(0.003);
            DetailCurve dc = c.Doc.Create.NewDetailCurve(c.View, Line.CreateBound(a, b));
            if (style != null) SetInvisible(c, dc, style);
            return dc;
        }

        /// <summary>Applies the &lt;Invisible lines&gt; style and checks it took; tries the styles the curve accepts otherwise.</summary>
        private static void SetInvisible(Ctx c, DetailCurve dc, GraphicsStyle style)
        {
            try { dc.LineStyle = style; } catch { }
            try
            {
                if (dc.LineStyle != null && dc.LineStyle.Id == style.Id) return;
                foreach (ElementId id in dc.GetLineStyleIds())
                {
                    if (c.Doc.GetElement(id) is GraphicsStyle gs && IsInvisibleStyle(gs))
                    {
                        dc.LineStyle = gs;
                        return;
                    }
                }
                Logger.Warn("[JA Tools] Estilo <Linhas invisíveis> não aplicado numa linha de referência da legenda.");
            }
            catch (Exception ex) { Logger.Debug("[JA Tools] Linha invisível: " + ex.Message); }
        }

        private static bool IsInvisibleStyle(GraphicsStyle gs)
        {
            Category cat = gs?.GraphicsStyleCategory;
            if (cat == null) return false;
            if (cat.Id.Value == (long)BuiltInCategory.OST_InvisibleLines) return true;
            string n = (cat.Name ?? gs.Name ?? "").ToLowerInvariant();
            return n.Contains("invis");
        }

        private static void TryDim(Ctx c, Line line, List<Reference> refs)
        {
            try { RevitUtil.CreateDimension(c.Doc, c.View, line, refs, c.DimType); }
            catch (Exception ex) { Logger.Warn("[JA Tools] Cota da legenda: " + ex.Message); }
        }

        private static T Named<T>(Document doc, string name) where T : Element =>
            string.IsNullOrEmpty(name) ? null :
            new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>().FirstOrDefault(x => x.Name == name);

        private static GraphicsStyle InvisibleLines(Document doc)
        {
            try
            {
                Category lines = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
                foreach (Category sub in lines.SubCategories)
                    if (sub.Id.Value == (long)BuiltInCategory.OST_InvisibleLines)
                    {
                        GraphicsStyle gs = sub.GetGraphicsStyle(GraphicsStyleType.Projection);
                        if (gs != null) return gs;
                    }
            }
            catch { }
            try
            {
                // any graphics style of the invisible-lines subcategory (or named so in the project's language)
                var styles = new FilteredElementCollector(doc).OfClass(typeof(GraphicsStyle)).Cast<GraphicsStyle>()
                    .Where(g => g.GraphicsStyleType == GraphicsStyleType.Projection).ToList();
                return styles.FirstOrDefault(g => g.GraphicsStyleCategory != null && g.GraphicsStyleCategory.Id.Value == (long)BuiltInCategory.OST_InvisibleLines)
                       ?? styles.FirstOrDefault(IsInvisibleStyle);
            }
            catch (Exception ex) { Logger.Warn("[JA Tools] Estilo de linhas invisíveis não encontrado: " + ex.Message); }
            return null;
        }
    }
}
