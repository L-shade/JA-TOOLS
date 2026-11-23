using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using RevitDwgUnifier.Infrastructure;
using RevitDwgUnifier.Tools.Common;

namespace RevitDwgUnifier.Tools.Views
{
    /// <summary>One family / system type that can get a detail ("ficha").</summary>
    public sealed class LegendCandidate : INotifyPropertyChanged
    {
        public ElementId Id;
        public string UniqueId = "";
        public string Category;
        public long CategoryId;
        public string Family;
        public string TypeName;
        public string Mark;
        public int Count;
        public double Width;      // feet (sorting / "L x A")
        public double Height;     // feet
        public double Depth;      // feet (cabinets, furniture)
        public string Description = "";
        public bool IsHost;       // wall / floor / ceiling / roof type (uses the legend "host length")
        /// <summary>Drawn from the model (sections around an instance) instead of a legend component:
        /// the category is not accepted in legends, or the project has no legend component to copy.</summary>
        public bool ModelMode;
        public ElementId InstanceId = ElementId.InvalidElementId;
        public Action Changed;

        /// <summary>Model-mode types need an instance placed in the model.</summary>
        public bool Available => !ModelMode || InstanceId != ElementId.InvalidElementId;

        private bool _selected;
        public bool IsSelected
        {
            get => _selected;
            set
            {
                if (_selected == value) return;
                _selected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                Changed?.Invoke();
            }
        }

        public string Title => (string.IsNullOrEmpty(Mark) ? "" : Mark + "   ") + Family + " : " + TypeName;
        public string Detail =>
            (Count == 0 ? "não usado" : Count + " no projeto") +
            (ModelMode ? (Available ? "  ·  vista do modelo" : "  ·  sem instância no modelo") : "");
        public string GroupName => Category;
        public event PropertyChangedEventHandler PropertyChanged;
    }

    /// <summary>A model category the user can load (types are read only when it is chosen).</summary>
    public sealed class CategoryInfo
    {
        public string Name;
        public long Id;
        public int TypeCount;
        public bool Host;
        public bool Loaded;
        /// <summary>Legend components accept this category (known after loading).</summary>
        public bool LegendOk;
        public override string ToString() => $"{Name} ({TypeCount})";
    }

    /// <summary>
    /// What the project offers. Opening is cheap: only the category list, styles and the legend
    /// to copy from. Types, instance counts, legend support and view directions of a category are
    /// read when the user picks it (LoadCategory); title block sizes when sheets are used.
    /// </summary>
    public sealed class LegendProbe
    {
        public View BaseLegend;
        public ElementId SourceComponentId = ElementId.InvalidElementId;
        public View SourceView;
        public bool HasLegendSource => SourceView != null && SourceComponentId != ElementId.InvalidElementId;
        public ElementId SectionTypeId = ElementId.InvalidElementId;
        public ElementId DraftingTypeId = ElementId.InvalidElementId;
        public string Problem;
        public readonly List<CategoryInfo> Categories = new List<CategoryInfo>();
        public readonly List<LegendCandidate> Candidates = new List<LegendCandidate>();
        /// <summary>category → direction key → LEGEND_COMPONENT_VIEW value (model mode: all directions, value 0).</summary>
        public readonly Dictionary<string, Dictionary<string, int>> Directions = new Dictionary<string, Dictionary<string, int>>();
        public readonly List<string> Unsupported = new List<string>();
        public readonly List<string> ParameterNames = new List<string>();
        public readonly List<KeyValuePair<ElementId, string>> TextTypes = new List<KeyValuePair<ElementId, string>>();
        public readonly List<KeyValuePair<ElementId, string>> DimensionTypes = new List<KeyValuePair<ElementId, string>>();
        public readonly List<TitleBlockInfo> TitleBlocks = new List<TitleBlockInfo>();
        public bool TitleBlocksMeasured;
        /// <summary>Phases of the project, in order (the elements can be taken from one of them).</summary>
        public readonly List<KeyValuePair<ElementId, string>> Phases = new List<KeyValuePair<ElementId, string>>();
        /// <summary>Generic annotation types that can be placed as typical details ("Família : Tipo").</summary>
        public readonly List<KeyValuePair<ElementId, string>> DetailSymbols = new List<KeyValuePair<ElementId, string>>();
    }

    /// <summary>Everything the user chose in the window: one detail per type, drawn with a template.</summary>
    public sealed class LegendRequest
    {
        public List<LegendCandidate> Types = new List<LegendCandidate>();
        public LegendTemplate Template = new LegendTemplate();
        public string NamePattern = "DETALHE - {marca}";
        public bool FillMarks;
        public SheetPlacement Sheets = new SheetPlacement();
        /// <summary>Typical details (generic annotation types) chosen per type; types not listed use the template's.</summary>
        public Dictionary<ElementId, List<ElementId>> TypeDetails = new Dictionary<ElementId, List<ElementId>>();
        /// <summary>Only the elements that exist in this phase (new or existing) count: quantities,
        /// rooms, the instance drawn from the model. Invalid = every phase (as before).</summary>
        public ElementId PhaseId = ElementId.InvalidElementId;
    }

    /// <summary>A title block type and the size of its sheet (feet), measured once.</summary>
    public sealed class TitleBlockInfo
    {
        public ElementId Id;
        public string Name;
        public double Width, Height;
        /// <summary>Free drawing area of the sheet, found in the title block's lines: distances (mm) from the
        /// edges of the title block to its inner frame, and from the bottom to the top of the info band.</summary>
        public bool FreeDetected;
        public double FreeLeftMm, FreeTopMm, FreeRightMm, FreeBottomMm;
        public override string ToString() => Name;
    }

    public sealed class LegendResult
    {
        public readonly List<View> Legends = new List<View>();
        public readonly List<View> ModelViews = new List<View>();
        public readonly List<ViewSheet> Sheets = new List<ViewSheet>();
        public readonly List<string> ScaleChanges = new List<string>();
        public readonly List<string> Problems = new List<string>();
        public int Components;
        public int MarksFilled;
        public bool Cancelled;
    }

    /// <summary>Progress callbacks (the command shows them in a progress window).</summary>
    public sealed class LegendProgress
    {
        public Action<int, string> Stage = (i, m) => { };
        public Action<int, int, string> Report = (c, t, m) => { };
        public Func<bool> Cancelled = () => false;
    }

    public static partial class LegendEngine
    {
        // direction keys (order = order of the elevations in a cell; the plan goes below them)
        public const string Plan = "plan", Front = "front", Left = "left", Right = "right", Back = "back", Section = "section";
        public static readonly string[] DirectionOrder = { Front, Left, Right, Back, Section, Plan };

        public static string DirectionLabel(string key)
        {
            switch (key)
            {
                case Plan: return "Planta";
                case Front: return "Frontal";
                case Back: return "Posterior";
                case Left: return "Lateral esquerda";
                case Right: return "Lateral direita";
                case Section: return "Corte";
                default: return key;
            }
        }

        private static readonly HashSet<BuiltInCategory> Excluded = new HashSet<BuiltInCategory>
        {
            BuiltInCategory.OST_TitleBlocks, BuiltInCategory.OST_DetailComponents, BuiltInCategory.OST_ProfileFamilies,
            BuiltInCategory.OST_GenericAnnotation, BuiltInCategory.OST_Mass, BuiltInCategory.OST_LegendComponents,
            BuiltInCategory.OST_RvtLinks, BuiltInCategory.OST_Entourage, BuiltInCategory.OST_Planting,
            BuiltInCategory.OST_Site, BuiltInCategory.OST_Topography, BuiltInCategory.OST_CurtainWallPanels
        };

        private static readonly Type[] HostClasses = { typeof(WallType), typeof(FloorType), typeof(CeilingType), typeof(RoofType) };

        // What Revit offers per category and the title block sizes do not change while Revit is
        // open: remembered per document, so a category is analysed once per session.
        private static readonly Dictionary<string, Dictionary<string, int>> DirCache = new Dictionary<string, Dictionary<string, int>>();
        private static readonly Dictionary<string, TitleBlockInfo> TitleBlockCache = new Dictionary<string, TitleBlockInfo>();
        private static string CacheKey(Document doc, string what) => (doc.PathName.Length > 0 ? doc.PathName : doc.Title) + "|" + what;

        // ================================================================== probe (cheap)
        public static LegendProbe Probe(Document doc)
        {
            var p = new LegendProbe();

            // legend + legend component to copy (optional: without them every type is drawn from the model)
            var legends = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => v.ViewType == ViewType.Legend && !v.IsTemplate).ToList();
            if (legends.Count > 0)
            {
                // base to duplicate: the legend with the least content (ideally an empty one)
                p.BaseLegend = legends.OrderBy(v => new FilteredElementCollector(doc).OwnedByView(v.Id).GetElementCount()).First();
                var legendIds = new HashSet<ElementId>(legends.Select(v => v.Id));
                Element comp = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_LegendComponents)
                    .WhereElementIsNotElementType().FirstOrDefault(e => legendIds.Contains(e.OwnerViewId));
                if (comp != null)
                {
                    p.SourceComponentId = comp.Id;
                    p.SourceView = doc.GetElement(comp.OwnerViewId) as View;
                }
            }
            p.SectionTypeId = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(t => t.ViewFamily == ViewFamily.Section)?.Id ?? ElementId.InvalidElementId;
            p.DraftingTypeId = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(t => t.ViewFamily == ViewFamily.Drafting)?.Id ?? ElementId.InvalidElementId;
            if (p.BaseLegend == null && p.DraftingTypeId == ElementId.InvalidElementId)
            {
                p.Problem = "O projeto não tem legenda nem tipo de vista de desenho para montar as fichas.";
                return p;
            }

            // categories (type counts only; nothing per type is read yet)
            var cats = new Dictionary<long, CategoryInfo>();
            foreach (FamilySymbol s in new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>())
            {
                Category c = s.Category;
                if (c == null || c.CategoryType != CategoryType.Model) continue;
                if (Excluded.Contains((BuiltInCategory)(int)c.Id.Value)) continue;
                if (!cats.TryGetValue(c.Id.Value, out var info)) cats[c.Id.Value] = info = new CategoryInfo { Name = c.Name, Id = c.Id.Value };
                info.TypeCount++;
            }
            foreach (Type cls in HostClasses)
            {
                foreach (ElementType t in new FilteredElementCollector(doc).OfClass(cls).Cast<ElementType>())
                {
                    if (t is WallType wt && wt.Kind == WallKind.Curtain) continue;
                    Category c = t.Category;
                    if (c == null) continue;
                    if (!cats.TryGetValue(c.Id.Value, out var info)) cats[c.Id.Value] = info = new CategoryInfo { Name = c.Name, Id = c.Id.Value, Host = true };
                    info.TypeCount++;
                }
            }
            p.Categories.AddRange(cats.Values.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase));
            CollectStyles(doc, p);
            try
            {
                foreach (Phase ph in doc.Phases) p.Phases.Add(new KeyValuePair<ElementId, string>(ph.Id, ph.Name));
            }
            catch { }
            return p;
        }

        // ================================================================== phase
        /// <summary>Phase the elements are taken from (Invalid = all phases). Set by the window / the request.</summary>
        public static ElementId PhaseId = ElementId.InvalidElementId;

        private static bool HasPhase => !ReferenceEquals(PhaseId, null) && PhaseId.Value != ElementId.InvalidElementId.Value;
        private static string PhaseKey => HasPhase ? PhaseId.Value.ToString(CultureInfo.InvariantCulture) : "all";

        /// <summary>The element exists in the chosen phase: created in it or before and not demolished
        /// up to it (temporary and future elements are left out). Elements without phase always count.</summary>
        internal static bool InPhase(Element e)
        {
            if (!HasPhase || e == null) return true;
            try
            {
                ElementOnPhaseStatus st = e.GetPhaseStatus(PhaseId);
                return st == ElementOnPhaseStatus.New || st == ElementOnPhaseStatus.Existing || st == ElementOnPhaseStatus.None;
            }
            catch { return true; }
        }

        private static Phase CurrentPhase(Document doc) => HasPhase ? doc.GetElement(PhaseId) as Phase : null;

        /// <summary>Rooms of an instance in the chosen phase (doors: both sides).</summary>
        internal static IEnumerable<Element> RoomsOf(Document doc, FamilyInstance fi)
        {
            Phase ph = CurrentPhase(doc);
            if (ph != null)
            {
                yield return SafeRoom(() => fi.get_Room(ph));
                yield return SafeRoom(() => fi.get_FromRoom(ph));
                yield return SafeRoom(() => fi.get_ToRoom(ph));
            }
            else
            {
                yield return SafeRoom(() => fi.Room);
                yield return SafeRoom(() => fi.FromRoom);
                yield return SafeRoom(() => fi.ToRoom);
            }
        }

        /// <summary>Changes the phase and recounts the types already loaded (quantity and the instance
        /// drawn from the model) — the list shows the counts of the chosen phase.</summary>
        public static void SetPhase(Document doc, LegendProbe p, ElementId phaseId)
        {
            PhaseId = phaseId ?? ElementId.InvalidElementId;
            foreach (var group in p.Candidates.GroupBy(c => c.CategoryId))
            {
                var counts = new Dictionary<long, int>();
                var first = new Dictionary<long, ElementId>();
                try
                {
                    foreach (Element e in new FilteredElementCollector(doc).OfCategoryId(new ElementId(group.Key)).WhereElementIsNotElementType())
                    {
                        if (!InPhase(e)) continue;
                        ElementId t;
                        try { t = e.GetTypeId(); } catch { continue; }
                        if (t == null || t == ElementId.InvalidElementId) continue;
                        counts.TryGetValue(t.Value, out int n);
                        counts[t.Value] = n + 1;
                        if (!first.ContainsKey(t.Value)) first[t.Value] = e.Id;
                    }
                }
                catch { continue; }
                foreach (LegendCandidate c in group)
                {
                    counts.TryGetValue(c.Id.Value, out int n);
                    c.Count = n;
                    if (c.IsHost && c.ModelMode) continue;              // system types are not drawn from the model
                    c.InstanceId = first.TryGetValue(c.Id.Value, out ElementId inst) ? inst : ElementId.InvalidElementId;
                }
            }
        }

        private static void CollectStyles(Document doc, LegendProbe p)
        {
            foreach (TextNoteType t in new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>().OrderBy(t => t.Name))
                p.TextTypes.Add(new KeyValuePair<ElementId, string>(t.Id, t.Name));
            foreach (DimensionType t in RevitUtil.LinearDimensionTypes(doc))
                p.DimensionTypes.Add(new KeyValuePair<ElementId, string>(t.Id, t.Name));
            CollectDetailSymbols(doc, p);
        }

        /// <summary>Generic annotations ("Det.Frontispício : 7 cm"...), the "Det" families first.</summary>
        public static void CollectDetailSymbols(Document doc, LegendProbe p)
        {
            p.DetailSymbols.Clear();
            var all = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_GenericAnnotation)
                .Cast<FamilySymbol>()
                .Select(s => new KeyValuePair<ElementId, string>(s.Id, s.FamilyName + " : " + s.Name))
                .OrderBy(kv => kv.Value.StartsWith("Det", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(kv => kv.Value, StringComparer.CurrentCultureIgnoreCase);
            p.DetailSymbols.AddRange(all);
        }

        /// <summary>Loads detail families (.rfa) into the project (inside the caller's transaction).</summary>
        public static int LoadDetailFamilies(Document doc, IEnumerable<string> paths, List<string> problems)
        {
            int n = 0;
            foreach (string path in paths)
            {
                try
                {
                    if (doc.LoadFamily(path, new TagLibrary.KeepValues(), out Family f) || f != null) n++;
                    else problems.Add(System.IO.Path.GetFileName(path) + ": já estava carregada.");
                }
                catch (Exception ex) { problems.Add(System.IO.Path.GetFileName(path) + ": " + ex.Message); }
            }
            return n;
        }

        /// <summary>Resolves the template's typical details ("Família : Tipo" or just "Família") to types.</summary>
        public static List<ElementId> ResolveDetails(LegendProbe p, IEnumerable<string> names)
        {
            var ids = new List<ElementId>();
            foreach (string raw in names ?? Enumerable.Empty<string>())
            {
                string n = (raw ?? "").Trim();
                if (n.Length == 0) continue;
                var hit = p.DetailSymbols.FirstOrDefault(kv => string.Equals(kv.Value, n, StringComparison.CurrentCultureIgnoreCase));
                if (hit.Key == null)
                    hit = p.DetailSymbols.FirstOrDefault(kv => Fold(kv.Value).StartsWith(Fold(n) + " :", StringComparison.Ordinal));
                if (hit.Key != null && !ids.Contains(hit.Key)) ids.Add(hit.Key);
            }
            return ids;
        }

        // ================================================================== one category (on demand)
        /// <summary>
        /// Reads the types of one category, counts their instances, finds whether legend components
        /// accept it (several families are tried) and which views Revit offers. Categories legends
        /// refuse are drawn from the model (sections around a placed instance).
        /// </summary>
        public static string LoadCategory(Document doc, LegendProbe p, CategoryInfo cat, Action<int, int, string> report)
        {
            if (cat.Loaded) return null;
            report = report ?? ((a, b, m) => { });
            try
            {
                report(0, 4, $"{cat.Name}: lendo os tipos…");
                var types = new List<ElementType>();
                if (cat.Host)
                {
                    foreach (Type cls in HostClasses)
                        types.AddRange(new FilteredElementCollector(doc).OfClass(cls).Cast<ElementType>()
                            .Where(t => t.Category != null && t.Category.Id.Value == cat.Id && !(t is WallType wt && wt.Kind == WallKind.Curtain)));
                }
                else
                {
                    types.AddRange(new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategoryId(new ElementId((BuiltInCategory)(int)cat.Id))
                        .Cast<ElementType>());
                }

                report(1, 4, $"{cat.Name}: contando as instâncias no modelo…");
                var counts = new Dictionary<ElementId, int>();
                var firstInstance = new Dictionary<ElementId, ElementId>();
                foreach (Element e in new FilteredElementCollector(doc).OfCategoryId(new ElementId((BuiltInCategory)(int)cat.Id)).WhereElementIsNotElementType())
                {
                    if (!InPhase(e)) continue;                                // only the chosen phase
                    ElementId t;
                    try { t = e.GetTypeId(); } catch { continue; }
                    if (t == null || t == ElementId.InvalidElementId) continue;
                    counts.TryGetValue(t, out int n);
                    counts[t] = n + 1;
                    if (!firstInstance.ContainsKey(t)) firstInstance[t] = e.Id;
                }
                var list = types.Select(t =>
                {
                    var c = Candidate(t, t.Category, t is FamilySymbol fs ? fs.FamilyName : t.FamilyName, counts, cat.Host);
                    if (firstInstance.TryGetValue(t.Id, out var inst)) c.InstanceId = inst;
                    return c;
                }).ToList();

                report(2, 4, $"{cat.Name}: verificando as legendas do Revit…");
                Dictionary<string, int> dirs = null;
                string key = CacheKey(doc, "cat:" + cat.Name);
                if (!p.HasLegendSource) dirs = null;
                else if (DirCache.TryGetValue(key, out var cached)) dirs = cached;
                else
                {
                    dirs = ProbeLegendDirections(doc, p, list);
                    DirCache[key] = dirs;
                }
                cat.LegendOk = dirs != null && dirs.Count > 0;
                if (cat.LegendOk) p.Directions[cat.Name] = dirs;
                else
                {
                    if (p.HasLegendSource) p.Unsupported.Add(cat.Name);
                    // model mode: every direction can be drawn as a section around an instance
                    p.Directions[cat.Name] = DirectionOrder.ToDictionary(k => k, k => 0);
                    foreach (var c in list) c.ModelMode = true;
                    if (cat.Host) foreach (var c in list) c.InstanceId = ElementId.InvalidElementId;   // not supported for system types
                }

                report(3, 4, $"{cat.Name}: lendo os parâmetros…");
                var names = new HashSet<string>(p.ParameterNames, StringComparer.OrdinalIgnoreCase);
                foreach (var c in list)
                {
                    AddParameterNames(doc.GetElement(c.Id), names);
                    if (c.InstanceId != ElementId.InvalidElementId) AddParameterNames(doc.GetElement(c.InstanceId), names);
                }
                p.ParameterNames.Clear();
                p.ParameterNames.AddRange(names.OrderBy(n => n, new NaturalStringComparer()));

                p.Candidates.AddRange(list);
                cat.Loaded = true;
                report(4, 4, $"{cat.Name}: {list.Count} tipo(s).");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error("[JA Tools] Categoria " + cat.Name + " não carregada.", ex);
                return ex.Message;
            }
        }

        private static void AddParameterNames(Element e, HashSet<string> names)
        {
            if (e == null) return;
            foreach (Parameter prm in e.Parameters)
            {
                try
                {
                    if (prm.Definition == null || !prm.HasValue) continue;
                    if (prm.StorageType == StorageType.ElementId || prm.StorageType == StorageType.None) continue;
                    names.Add(prm.Definition.Name);
                }
                catch { }
            }
        }

        /// <summary>
        /// Tries up to 4 types of different families in a throw-away legend (rolled back). null or
        /// empty = legend components refuse the category. The view directions come from the
        /// parameter display text (works in any Revit language).
        /// </summary>
        private static Dictionary<string, int> ProbeLegendDirections(Document doc, LegendProbe p, List<LegendCandidate> list)
        {
            var tries = list.OrderByDescending(c => c.Count).GroupBy(c => c.Family).Select(g => g.First()).Take(4).ToList();
            if (tries.Count == 0) return null;
            using (var t = new Transaction(doc, "JA Tools - analisar legendas"))
            {
                t.Start();
                RevitDwgUnifier.Export.FailureSwallower.Attach(t);
                try
                {
                    View legend = doc.GetElement(p.BaseLegend.Duplicate(ViewDuplicateOption.Duplicate)) as View;
                    Element comp = CopyComponent(doc, p, legend);
                    if (comp == null) return null;
                    foreach (var first in tries)
                    {
                        if (!SetType(doc, comp, first.Id)) continue;
                        var dirs = new Dictionary<string, int>();
                        var raw = new List<KeyValuePair<int, string>>();
                        Parameter view = comp.get_Parameter(BuiltInParameter.LEGEND_COMPONENT_VIEW);
                        if (view != null && !view.IsReadOnly)
                        {
                            for (int v = -12; v <= -1; v++)
                            {
                                try
                                {
                                    if (!view.Set(v)) continue;     // the display text needs no regeneration
                                    if (view.AsInteger() != v) continue;
                                    string name = view.AsValueString();
                                    if (!string.IsNullOrWhiteSpace(name)) raw.Add(new KeyValuePair<int, string>(v, name));
                                }
                                catch { }
                            }
                        }
                        foreach (var kv in raw)
                        {
                            string key = Match(kv.Value);
                            if (key != null && !dirs.ContainsKey(key)) dirs[key] = kv.Key;
                        }
                        if (dirs.Count == 0 && raw.Count == 0)
                        {
                            dirs[Plan] = -8; dirs[Front] = -7; dirs[Back] = -6; dirs[Left] = -5; dirs[Right] = -4;
                        }
                        return dirs;
                    }
                    return null;
                }
                catch (Exception ex)
                {
                    Logger.Warn("[JA Tools] Análise de legenda: " + ex.Message);
                    return null;
                }
                finally
                {
                    if (t.GetStatus() == TransactionStatus.Started) t.RollBack();
                }
            }
        }

        // ================================================================== title blocks (on demand)
        public static void MeasureTitleBlocks(Document doc, LegendProbe p, Action<int, int, string> report)
        {
            if (p.TitleBlocksMeasured) return;
            report = report ?? ((a, b, m) => { });
            var all = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks)
                .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().OrderBy(x => x.FamilyName).ThenBy(x => x.Name).ToList();
            var toMeasure = new List<FamilySymbol>();
            foreach (var tb in all)
            {
                if (TitleBlockCache.TryGetValue(CacheKey(doc, "tb:" + tb.Id.Value), out var info)) { if (info != null) p.TitleBlocks.Add(info); }
                else toMeasure.Add(tb);
            }
            if (toMeasure.Count > 0)
            {
                using (var t = new Transaction(doc, "JA Tools - medir carimbos"))
                {
                    t.Start();
                    RevitDwgUnifier.Export.FailureSwallower.Attach(t);
                    try
                    {
                        for (int i = 0; i < toMeasure.Count; i++)
                        {
                            var tb = toMeasure[i];
                            report(i, toMeasure.Count, "Medindo o carimbo " + tb.FamilyName + " : " + tb.Name + "…");
                            try
                            {
                                ViewSheet sh = ViewSheet.Create(doc, tb.Id);
                                doc.Regenerate();
                                Element inst = new FilteredElementCollector(doc, sh.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).FirstElement();
                                BoundingBoxXYZ bb = inst?.get_BoundingBox(sh);
                                TitleBlockInfo info = bb == null ? null
                                    : new TitleBlockInfo { Id = tb.Id, Name = tb.FamilyName + " : " + tb.Name, Width = bb.Max.X - bb.Min.X, Height = bb.Max.Y - bb.Min.Y };
                                if (info != null)
                                {
                                    DetectFreeArea(inst, sh, bb, info);
                                    p.TitleBlocks.Add(info);
                                }
                                TitleBlockCache[CacheKey(doc, "tb:" + tb.Id.Value)] = info;
                            }
                            catch (Exception ex) { Logger.Warn("[JA Tools] Carimbo " + tb.Name + " não medido: " + ex.Message); }
                        }
                    }
                    finally
                    {
                        if (t.GetStatus() == TransactionStatus.Started) t.RollBack();
                    }
                }
            }
            p.TitleBlocks.Sort((x, y) => string.Compare(x.Name, y.Name, StringComparison.CurrentCultureIgnoreCase));
            p.TitleBlocksMeasured = true;
            report(1, 1, $"{p.TitleBlocks.Count} carimbo(s).");
        }

        // ================================================================== candidates
        private sealed class Seg2 { public double X1, Y1, X2, Y2; }

        /// <summary>
        /// Free area of a title block, from its lines (sheet coordinates relative to the title block box):
        ///  * left / right: the innermost long vertical lines (≥ half the height) near each side — the frame;
        ///  * top: the innermost long horizontal line near the top;
        ///  * bottom: the highest long horizontal line (≥ half the frame width) in the lower half — the top of
        ///    the info band, or the bottom of the frame when the band does not span the sheet.
        /// </summary>
        private static void DetectFreeArea(Element inst, View sheet, BoundingBoxXYZ bb, TitleBlockInfo info)
        {
            try
            {
                var segs = new List<Seg2>();
                CollectSegments(inst.get_Geometry(new Options { View = sheet }), segs, 0, bb.Min);
                if (segs.Count == 0) return;
                double W = info.Width, H = info.Height, tol = 0.4 / 304.8;
                var vert = segs.Where(q => Math.Abs(q.X1 - q.X2) < tol && Math.Abs(q.Y1 - q.Y2) >= 0.5 * H).Select(q => (q.X1 + q.X2) / 2).ToList();
                double left = vert.Where(x => x < 0.35 * W).DefaultIfEmpty(0).Max();
                double right = vert.Where(x => x > 0.65 * W).DefaultIfEmpty(W).Min();
                double frameW = right - left;
                var hor = segs.Where(q => Math.Abs(q.Y1 - q.Y2) < tol && Math.Abs(q.X1 - q.X2) >= 0.5 * frameW).Select(q => (q.Y1 + q.Y2) / 2).ToList();
                double top = hor.Where(y => y > 0.65 * H).DefaultIfEmpty(H).Min();
                double bottom = hor.Where(y => y < 0.5 * H).DefaultIfEmpty(0).Max();
                info.FreeLeftMm = Math.Max(0, left * 304.8);
                info.FreeRightMm = Math.Max(0, (W - right) * 304.8);
                info.FreeTopMm = Math.Max(0, (H - top) * 304.8);
                info.FreeBottomMm = Math.Max(0, bottom * 304.8);
                info.FreeDetected = vert.Count > 0 || hor.Count > 0;
                Logger.Info($"[JA Tools] Carimbo {info.Name}: área livre a {info.FreeLeftMm:0} / {info.FreeTopMm:0} / {info.FreeRightMm:0} / {info.FreeBottomMm:0} mm (esq./sup./dir./inf.).");
            }
            catch (Exception ex) { Logger.Debug("[JA Tools] Área livre do carimbo: " + ex.Message); }
        }

        private static void CollectSegments(GeometryElement ge, List<Seg2> segs, int depth, XYZ origin)
        {
            if (ge == null || depth > 6) return;
            foreach (GeometryObject o in ge)
            {
                if (o is Line l)
                    segs.Add(new Seg2 { X1 = l.GetEndPoint(0).X - origin.X, Y1 = l.GetEndPoint(0).Y - origin.Y, X2 = l.GetEndPoint(1).X - origin.X, Y2 = l.GetEndPoint(1).Y - origin.Y });
                else if (o is PolyLine pl)
                {
                    IList<XYZ> pts = pl.GetCoordinates();
                    for (int i = 1; i < pts.Count; i++)
                        segs.Add(new Seg2 { X1 = pts[i - 1].X - origin.X, Y1 = pts[i - 1].Y - origin.Y, X2 = pts[i].X - origin.X, Y2 = pts[i].Y - origin.Y });
                }
                else if (o is GeometryInstance gi) CollectSegments(gi.GetInstanceGeometry(), segs, depth + 1, origin);
            }
        }

        private static LegendCandidate Candidate(ElementType t, Category category, string family, Dictionary<ElementId, int> counts, bool host)
        {
            counts.TryGetValue(t.Id, out int n);
            return new LegendCandidate
            {
                Id = t.Id,
                UniqueId = t.UniqueId,
                Category = category.Name,
                CategoryId = category.Id.Value,
                Family = family ?? "",
                TypeName = t.Name,
                Mark = t.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_MARK)?.AsString() ?? "",
                Description = t.get_Parameter(BuiltInParameter.ALL_MODEL_DESCRIPTION)?.AsString() ?? "",
                Count = n,
                IsHost = host,
                Width = host && t is HostObjAttributes h ? SafeWidth(h) : First(t, BuiltInParameter.DOOR_WIDTH, BuiltInParameter.WINDOW_WIDTH, BuiltInParameter.FAMILY_WIDTH_PARAM, BuiltInParameter.CASEWORK_WIDTH),
                Height = host ? 0 : First(t, BuiltInParameter.DOOR_HEIGHT, BuiltInParameter.WINDOW_HEIGHT, BuiltInParameter.FAMILY_HEIGHT_PARAM, BuiltInParameter.CASEWORK_HEIGHT, BuiltInParameter.GENERIC_HEIGHT),
                Depth = host ? 0 : ByName(t, new[] { "CASEWORK_DEPTH", "FURNITURE_DEPTH", "GENERIC_DEPTH", "FAMILY_DEPTH_PARAM" },
                                          new[] { "Profundidade", "Depth", "Profundidad" })
            };
        }

        private static double SafeWidth(HostObjAttributes h)
        {
            try { return h is WallType w ? w.Width : h.GetCompoundStructure()?.GetWidth() ?? 0; } catch { return 0; }
        }

        private static double First(Element e, params BuiltInParameter[] ids)
        {
            var list = new List<Parameter>();
            foreach (var id in ids)
            {
                try { list.Add(e.get_Parameter(id)); } catch { }
            }
            // families with their own "Largura"/"Width" or "Altura"/"Height" parameters
            bool width = ids.Contains(BuiltInParameter.FAMILY_WIDTH_PARAM);
            foreach (string name in width ? new[] { "Largura", "Width", "Ancho" } : new[] { "Altura", "Height", "Alto" })
                list.Add(e.LookupParameter(name));
            foreach (Parameter p in list)
            {
                try { if (p != null && p.StorageType == StorageType.Double && p.HasValue && p.AsDouble() > 1e-6) return p.AsDouble(); }
                catch { }
            }
            return 0;
        }

        /// <summary>First positive length among built-in parameters given by NAME (parsed at run time,
        /// so a name missing in some Revit version never breaks the build) and named parameters.</summary>
        internal static double ByName(Element e, string[] builtIns, string[] names)
        {
            if (e == null) return 0;
            foreach (string n in builtIns)
            {
                if (!Enum.TryParse(n, out BuiltInParameter bip)) continue;
                try
                {
                    Parameter p = e.get_Parameter(bip);
                    if (p != null && p.StorageType == StorageType.Double && p.HasValue && p.AsDouble() > 1e-6) return p.AsDouble();
                }
                catch { }
            }
            foreach (string n in names)
            {
                Parameter p = e.LookupParameter(n);
                try { if (p != null && p.StorageType == StorageType.Double && p.HasValue && p.AsDouble() > 1e-6) return p.AsDouble(); } catch { }
            }
            return 0;
        }

        /// <summary>Direction key from the Revit display text ("Elevação: Frontal", "Floor Plan", "Corte"...).</summary>
        public static string Match(string display)
        {
            string s = Fold(display);
            if (s.Contains("section") || s.Contains("corte") || s.Contains("secao") || s.Contains("seccion")) return Section;
            if ((s.Contains("plan")) && !s.Contains("forro") && !s.Contains("ceiling") && !s.Contains("techo")) return Plan;
            if (s.Contains("front") || s.Contains("frente")) return Front;
            if (s.Contains("back") || s.Contains("rear") || s.Contains("post") || s.Contains("tras") || s.Contains("fundo")) return Back;
            if (s.Contains("left") || s.Contains("esq") || s.Contains("izq") || s.Contains("gauche") || s.Contains("links")) return Left;
            if (s.Contains("right") || s.Contains("direit") || s.Contains("derech") || s.Contains("droit") || s.Contains("rechts")) return Right;
            return null;
        }

        private static string Fold(string s) => TextUtil.Fold(s);

        // ================================================================== shared helpers (build: LegendBuilder.cs)
        private static string ParamText(Element e, string name)
        {
            if (e == null) return null;
            Parameter p = e.LookupParameter(name);
            if (p == null || !p.HasValue) return null;
            try
            {
                switch (p.StorageType)
                {
                    case StorageType.String: return p.AsString();
                    case StorageType.Integer: return p.AsValueString() ?? p.AsInteger().ToString(CultureInfo.CurrentCulture);
                    case StorageType.Double: return p.AsValueString();
                    default: return p.AsValueString();
                }
            }
            catch { return null; }
        }

        private static Element FirstInstance(Document doc, ElementId typeId)
        {
            List<ElementId> ids = InstancesOfType(doc, typeId);
            return ids.Count > 0 ? doc.GetElement(ids[0]) : null;
        }

        /// <summary>
        /// Instances of a family type, in model order. The instances of the whole category are read
        /// ONCE per tool run (SessionCache) and grouped by type: before, every type walked the whole
        /// model with a slow filter (types × elements). Same answer as FamilyInstanceFilter: only
        /// family types have instances here.
        /// </summary>
        internal static List<ElementId> InstancesOfType(Document doc, ElementId typeId)
        {
            var none = new List<ElementId>();
            try
            {
                if (!(doc.GetElement(typeId) is FamilySymbol sym) || sym.Category == null) return none;
                long cat = sym.Category.Id.Value;
                var map = SessionCache.Get("instancesByType|" + doc.GetHashCode() + "|" + cat + "|" + PhaseKey, () =>
                {
                    var m = new Dictionary<long, List<ElementId>>();
                    foreach (FamilyInstance fi in new FilteredElementCollector(doc).OfCategoryId(sym.Category.Id)
                                 .OfClass(typeof(FamilyInstance)).WhereElementIsNotElementType().Cast<FamilyInstance>())
                    {
                        if (!InPhase(fi)) continue;
                        long t = fi.GetTypeId().Value;
                        if (!m.TryGetValue(t, out List<ElementId> l)) m[t] = l = new List<ElementId>();
                        l.Add(fi.Id);
                    }
                    return m;
                });
                return map.TryGetValue(typeId.Value, out List<ElementId> list) ? list : none;
            }
            catch { return none; }
        }

        private static Element CopyComponent(Document doc, LegendProbe p, View target)
        {
            ICollection<ElementId> ids = ElementTransformUtils.CopyElements(p.SourceView, new List<ElementId> { p.SourceComponentId },
                target, Transform.Identity, new CopyPasteOptions());
            return ids.Count > 0 ? doc.GetElement(ids.First()) : null;
        }

        private static bool SetType(Document doc, Element comp, ElementId typeId)
        {
            try
            {
                Parameter p = comp.get_Parameter(BuiltInParameter.LEGEND_COMPONENT);
                if (p == null || !p.Set(typeId)) return false;
                doc.Regenerate();
                return comp.get_Parameter(BuiltInParameter.LEGEND_COMPONENT).AsElementId() == typeId;
            }
            catch { return false; }
        }

        /// <summary>
        /// "Nível de detalhe" of a legend component. Its BuiltInParameter name differs between
        /// Revit versions, so it is found at run time: by the enum name (contains LEGEND and
        /// DETAIL), else by the parameter name in the Revit language.
        /// </summary>
        private static void SetDetailLevel(Element comp, int level)
        {
            Parameter found = null;
            foreach (Parameter p in comp.Parameters)
            {
                try
                {
                    if (p.StorageType != StorageType.Integer || p.IsReadOnly) continue;
                    if (p.Definition is InternalDefinition d)
                    {
                        string n = d.BuiltInParameter.ToString();
                        if (n.IndexOf("LEGEND", StringComparison.Ordinal) >= 0 && n.IndexOf("DETAIL", StringComparison.Ordinal) >= 0) { found = p; break; }
                    }
                    string name = Fold(p.Definition?.Name);
                    if (found == null && (name.Contains("detail level") || name.Contains("nivel de detalhe") || name.Contains("nivel de detalle"))) found = p;
                }
                catch { }
            }
            try { found?.Set(level); } catch { }
        }

        private static void TrySet(Element e, BuiltInParameter id, int value)
        {
            try { Parameter p = e.get_Parameter(id); if (p != null && !p.IsReadOnly) p.Set(value); } catch { }
        }

        private static void TrySetDouble(Element e, BuiltInParameter id, double value)
        {
            try { Parameter p = e.get_Parameter(id); if (p != null && !p.IsReadOnly) p.Set(value); } catch { }
        }

        /// <summary>Gives the next free code to types without a type mark: P01 (portas), J01 (janelas), XX01 (others).</summary>
        private static int FillMarks(Document doc, List<LegendCandidate> types)
        {
            var used = new HashSet<string>(new FilteredElementCollector(doc).WhereElementIsElementType()
                .Select(e => e.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_MARK)?.AsString())
                .Where(s => !string.IsNullOrEmpty(s)), StringComparer.OrdinalIgnoreCase);
            int n = 0;
            foreach (var t in types.Where(t => string.IsNullOrEmpty(t.Mark)))
            {
                Element e = doc.GetElement(t.Id);
                Parameter p = e?.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_MARK);
                if (p == null || p.IsReadOnly) continue;
                string prefix = Prefix(e.Category);
                string code = null;
                for (int i = 1; i < 1000; i++)
                {
                    code = prefix + i.ToString("00");
                    if (!used.Contains(code)) break;
                }
                if (code == null) continue;
                try { if (p.Set(code)) { used.Add(code); t.Mark = code; n++; } } catch { }
            }
            return n;
        }

        private static string Prefix(Category c)
        {
            if (c == null) return "X";
            long id = c.Id.Value;
            if (id == (long)BuiltInCategory.OST_Doors) return "P";
            if (id == (long)BuiltInCategory.OST_Windows) return "J";
            string letters = Regex.Replace(Fold(c.Name).ToUpperInvariant(), "[^A-Z]", "");
            return letters.Length >= 2 ? letters.Substring(0, 2) : letters.Length == 1 ? letters : "X";
        }
    }
}
