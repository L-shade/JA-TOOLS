using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using RevitDwgUnifier.Tools.Common;
using RevitDwgUnifier.Tools.Dimensions;

namespace RevitDwgUnifier.Tools.Detail
{
    /// <summary>
    /// Anotar Vista (1.17 = Anotar Planta + Anotação Completa): the annotation of Detalhar Ambientes
    /// on views that already exist, without creating views or sheets, plus the water / sewer points
    /// of the old Anotar Planta (dimensions from the walls and água / esgoto tags). Which views: the ones selected in the project browser (or their
    /// viewports selected on a sheet); else every plan / elevation of the active sheet; else the
    /// active view.
    ///   * floor plans: room dimensions (every wall direction), room tags on untagged rooms, element
    ///     tags per category (placed by TagPlacer), overlap check, plan regions over high windows;
    ///   * elevations / sections looking into a room: overall dimensions, counter heights, the text
    ///     of a cut ceiling.
    /// One transaction per view inside one group (one undo); progress per view with Cancel.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class AnotarVistaCommand : DimToolCommand
    {
        public override string Title => "Anotar Vista";

        private static readonly BuiltInCategory[] PointCategories = BuildPointCategories();

        private static BuiltInCategory[] BuildPointCategories()
        {
            var list = new List<BuiltInCategory>
            {
                BuiltInCategory.OST_PlumbingFixtures, BuiltInCategory.OST_PipeAccessory, BuiltInCategory.OST_GenericModel,
                BuiltInCategory.OST_MechanicalEquipment, BuiltInCategory.OST_PipeFitting
            };
            // "Equipamentos hidráulicos" (Plumbing Equipment) exists from Revit 2025 on
            if (Enum.TryParse("OST_PlumbingEquipment", out BuiltInCategory pe)) list.Add(pe);
            return list.ToArray();
        }

        /// <summary>Tag category of a point category: the one map of the add-in (TagPlacer).</summary>
        private static BuiltInCategory? TagCategoryOf(BuiltInCategory c) => TagPlacer.TagCategoryFor(c);

        private const string AguaWords = "agua, af, aq, registro, torneira, misturador, chuveiro, ducha, filtro, engate, hidr";
        private const string EsgotoWords = "esgoto, es, ralo, sifao, sifonada, cs, rs, tq, tubo de queda, gordura, inspecao, ventilacao";


        private static bool Annotatable(View v) =>
            v != null && !v.IsTemplate &&
            ((v is ViewPlan && v.ViewType == Autodesk.Revit.DB.ViewType.FloorPlan) ||
             (v is ViewSection && (v.ViewType == Autodesk.Revit.DB.ViewType.Elevation || v.ViewType == Autodesk.Revit.DB.ViewType.Section)));

        private List<View> Targets(out string where)
        {
            var sel = UiDoc.Selection.GetElementIds().Select(id => Doc.GetElement(id)).Where(e => e != null).ToList();
            var views = sel.OfType<View>().Where(Annotatable).ToList();
            if (views.Count > 0) { where = "Vistas selecionadas"; return views; }
            views = sel.OfType<Viewport>().Select(vp => Doc.GetElement(vp.ViewId) as View).Where(Annotatable).ToList();
            if (views.Count > 0) { where = "Vistas selecionadas na prancha"; return views; }
            View av = Doc.ActiveView;
            if (av is ViewSheet sh)
            {
                where = "Prancha " + sh.SheetNumber + " - " + sh.Name;
                return sh.GetAllPlacedViews().Select(id => Doc.GetElement(id) as View).Where(Annotatable)
                         .OrderBy(v => v is ViewPlan ? 0 : 1).ThenBy(v => v.Name).ToList();
            }
            where = "Vista ativa";
            return Annotatable(av) ? new List<View> { av } : new List<View>();
        }

        protected override Result Run()
        {
            List<View> targets = Targets(out string where);
            if (targets.Count == 0)
            {
                Show("Abra uma planta de piso ou uma elevação — ou uma prancha (anota todas as vistas dela), ou selecione vistas no navegador de projeto — e execute novamente.");
                return Result.Cancelled;
            }
            int planCount = targets.Count(v => v is ViewPlan), elevCount = targets.Count - planCount;
            List<Room> rooms = RevitUtil.Rooms(Doc);
            List<TagCatOption> tagCats = PlanAnnotation.Categories(Doc);
            var roomTagTypes = new FilteredElementCollector(Doc).OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_RoomTags)
                .Cast<FamilySymbol>().OrderBy(s => s.FamilyName).ThenBy(s => s.Name).ToList();
            var textTypes = new FilteredElementCollector(Doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>().OrderBy(t => t.Name).ToList();

            var selModel = UiDoc.Selection.GetElementIds().Select(id => Doc.GetElement(id))
                .Where(e => e != null && !(e is View) && !(e is Viewport)).ToList();
            var pointTagCats = PointCategories.Select(TagCategoryOf).Where(c => c.HasValue).Select(c => c.Value)
                .Concat(new[] { BuiltInCategory.OST_MultiCategoryTags }).Distinct().ToList();
            var pointTagTypes = new FilteredElementCollector(Doc).OfClass(typeof(FamilySymbol)).WherePasses(new ElementMulticategoryFilter(pointTagCats))
                .Cast<FamilySymbol>().OrderBy(s => s.Category?.Name).ThenBy(s => s.FamilyName).ThenBy(s => s.Name).ToList();
            var pointTagChoices = new List<string> { "(identificador JA de cada categoria)" }
                .Concat(pointTagTypes.Select(s => $"{s.FamilyName} : {s.Name}  [{s.Category?.Name}]")).ToList();

            var dlg = new OptionsDialog(Title, "AnotarVista", "Anotar");
            dlg.Note($"{where}: {planCount} planta(s) e {elevCount} elevação(ões)/corte(s)" +
                     (targets.Count <= 6 ? ": " + string.Join(", ", targets.Select(v => v.Name)) : "") + ".");

            dlg.Section("Plantas");
            var fDims = dlg.Check("dims", "Cotas internas dos ambientes (todas as direções das paredes)", true);
            var fType = AddTypeChoice(dlg);
            var fRoomOff = dlg.Number("roomOff", "Afastar as cotas do centro do ambiente (m)", 0.4, 0, 20);
            var fRoomTag = dlg.Check("roomTag", "Etiqueta de ambiente (nos ambientes sem etiqueta)", true);
            var fRoomTagType = dlg.Choice("roomTagType", "Tipo da etiqueta de ambiente",
                new List<string> { "(padrão do projeto)" }.Concat(roomTagTypes.Select(s => s.FamilyName + " : " + s.Name)).ToList());
            var fTags = dlg.Check("tags", "Etiquetas dos elementos (tipo de cada categoria abaixo)", true);
            var fAdjust = dlg.Check("adjust", "Ajustar sobreposições", true);
            var fAdjustAll = dlg.Choice("adjustScope", "Ajustar", new[] { "Todas as anotações da vista", "Só as criadas agora" });
            var fOnlySel = dlg.Check("onlySel", $"Somente os ambientes e pontos selecionados ({selModel.Count})", false);
            var fRegion = dlg.Check("planRegion", "Região de planta nas esquadrias acima do plano de corte", true);
            var fRegionFb = dlg.Choice("planRegionFb", "Sem região de planta modelo no projeto", new[] { "Só avisar", "Elevar o corte da planta" });
            bool hasPlans = planCount > 0, hasElevs = elevCount > 0;
            foreach (var f in new OptionsDialog.Field[] { fDims, fRoomTag, fTags, fAdjust, fRegion }) f.EnabledWhen(() => hasPlans);
            fType.EnabledWhen(() => hasPlans && fDims.Live);
            fRoomTagType.EnabledWhen(() => hasPlans && fRoomTag.Live);
            fRegionFb.EnabledWhen(() => hasPlans && fRegion.Live);
            fAdjustAll.EnabledWhen(() => hasPlans && fAdjust.Live);
            fRoomOff.EnabledWhen(() => hasPlans && fDims.Live);
            fOnlySel.EnabledWhen(() => hasPlans && selModel.Count > 0);
            PlanAnnotation.AddFields(Doc, dlg, tagCats, () => hasPlans && fTags.Live);

            dlg.Section("Pontos hidrossanitários (plantas)");
            var fPointDims = dlg.Check("pointDims", "Cotas dos pontos hidráulicos e de esgoto a partir das paredes", true);
            var fWaterTags = dlg.Check("waterTags", "Etiquetas dos pontos hidráulicos (água fria / quente)", true);
            var fSewerTags = dlg.Check("sewerTags", "Etiquetas dos pontos de esgoto", true);
            var fWaterTagType = dlg.Choice("waterTagType", "Etiqueta dos pontos hidráulicos", pointTagChoices);
            var fSewerTagType = dlg.Choice("sewerTagType", "Etiqueta dos pontos de esgoto", pointTagChoices);
            var fPointOff = dlg.Number("pointOff", "Afastamento das cotas dos pontos (m)", 0.3, 0, 10);
            var fTol = dlg.Number("tol", "Tolerância para alinhar pontos em fila (cm)", 5, 0.1, 100);
            var fTagOff = dlg.Number("tagOff", "Distância da etiqueta ao ponto (mm no papel)", 6, 0, 50);
            var fWater = dlg.Text("aguaWords", "Palavras de ponto hidráulico", AguaWords,
                "Usadas quando a família não tem conectores de tubulação: nome da família ou do tipo. Siglas de até 3 letras valem como palavra inteira.");
            var fSewer = dlg.Text("esgotoWords", "Palavras de ponto de esgoto", EsgotoWords,
                "Pela tubulação (conectores) ou pelo nome: água fria/quente = hidráulico; esgoto = esgoto; peças com os dois (pias, lavatórios) recebem as duas etiquetas.");
            foreach (var f in new OptionsDialog.Field[] { fPointDims, fWaterTags, fSewerTags }) f.EnabledWhen(() => hasPlans);
            bool Hidro() => hasPlans && (fPointDims.Live || fWaterTags.Live || fSewerTags.Live);
            fWaterTagType.EnabledWhen(() => hasPlans && fWaterTags.Live);
            fSewerTagType.EnabledWhen(() => hasPlans && fSewerTags.Live);
            fPointOff.EnabledWhen(() => hasPlans && fPointDims.Live);
            fTol.EnabledWhen(() => hasPlans && fPointDims.Live);
            fTagOff.EnabledWhen(() => hasPlans && (fWaterTags.Live || fSewerTags.Live));
            fWater.EnabledWhen(Hidro);
            fSewer.EnabledWhen(Hidro);

            dlg.Section("Elevações e cortes");
            var fElevDims = dlg.Check("elevDims", "Cotas gerais (largura e pé-direito)", true);
            var fCounter = dlg.Check("counterDims", "Cota da altura das bancadas", true);
            var fElevDimType = dlg.Choice("elevDimType", "Tipo de cota das elevações", DimEngine.TypeChoices(DimTypes));
            var fCeil = dlg.Check("ceilNote", "Texto quando a vista corta o forro", true);
            var fCeilText = dlg.Text("ceilText", "Texto do forro", "FORRO EM VISTA");
            var fTextType = dlg.Choice("textType", "Tipo de texto",
                new List<string> { "(padrão do projeto)" }.Concat(textTypes.Select(t => t.Name)).ToList());
            foreach (var f in new OptionsDialog.Field[] { fElevDims, fCounter, fElevDimType, fCeil }) f.EnabledWhen(() => hasElevs);
            fCeilText.EnabledWhen(() => hasElevs && fCeil.Live);
            fTextType.EnabledWhen(() => hasElevs && fCeil.Live);
            dlg.Note("Rodar de novo na mesma vista não duplica etiquetas, mas duplica as cotas.");
            if (!dlg.Show(Owner)) return Result.Cancelled;

            DimensionType dimType = SelectedType(fType);
            FamilySymbol roomTagType = fRoomTagType.Index > 0 ? roomTagTypes[fRoomTagType.Index - 1] : null;
            bool onlySel = fOnlySel.Value && selModel.Count > 0;
            var selIds = new HashSet<ElementId>(selModel.Select(e => e.Id));
            bool hidro = fPointDims.Value || fWaterTags.Value || fSewerTags.Value;
            string[] aguaWords = Words(fWater.Value), esgotoWords = Words(fSewer.Value);
            int pointRows = 0, waterTags = 0, sewerTags = 0, points = 0, unclassified = 0;
            var annot = new ElevationAnnotator(Doc, Problems)
            {
                DimType = DimEngine.TypeFromChoice(DimTypes, fElevDimType.Index),
                Overall = fElevDims.Value, Counters = fCounter.Value, CeilingNote = fCeil.Value,
                CeilingText = fCeilText.Value, TextTypeId = fTextType.Index > 0 ? textTypes[fTextType.Index - 1].Id : null
            };

            ToolProgress.Setup($"Anotando {targets.Count} vista(s)", new[] { "Plantas", "Elevações e cortes", "Concluir" }, cancellable: true);
            int tags = 0, roomTags = 0, moved = 0, regions = 0, highWin = 0, done = 0;
            using (var tg = new TransactionGroup(Doc, "JA Tools - " + Title))
            {
                tg.Start();
                for (int i = 0; i < targets.Count; i++)
                {
                    View v = targets[i];
                    if (ToolProgress.Cancelled)
                    {
                        tg.RollBack();
                        Show("Anotação cancelada. Nada foi alterado no modelo.", ResultKind.Info);
                        return Result.Cancelled;
                    }
                    ToolProgress.Stage(v is ViewPlan ? 0 : 1, $"Vista {i + 1} de {targets.Count} — {v.Name}");
                    ToolProgress.Report(i, targets.Count, $"Vista {i + 1} de {targets.Count} — {v.Name}");
                    try
                    {
                        if (v is ViewPlan plan)
                        {
                            List<Room> inView = RoomsInView(plan);
                            if (onlySel) inView = inView.Where(r => selIds.Contains(r.Id)).ToList();
                            using (var t = new Transaction(Doc, "JA Tools - Anotar " + plan.Name))
                            {
                                t.Start();
                                RevitDwgUnifier.Export.FailureSwallower.Attach(t);
                                var before = new HashSet<ElementId>(new FilteredElementCollector(Doc, plan.Id).ToElementIds());
                                List<Wall> walls = null;
                                if (fRoomTag.Value)
                                {
                                    HashSet<ElementId> tagged = PlanAnnotation.TaggedRooms(Doc, plan);
                                    foreach (Room r in inView.Where(x => !tagged.Contains(x.Id)))
                                        if (PlanAnnotation.TagRoom(Doc, plan, r, roomTagType, Problems)) roomTags++;
                                }
                                if (fDims.Value && inView.Count > 0)
                                {
                                    ToolProgress.Pulse($"{plan.Name}: cotas de {inView.Count} ambiente(s)");
                                    walls = DimEngine.WallsInView(Doc, plan);
                                    double z = DimEngine.ViewZ(plan);
                                    foreach (Room r in inView) DimRoomAll(plan, walls, r, RevitUtil.M(fRoomOff.Value), z, dimType, false);
                                }
                                // water / sewer points: before the generic tags, with their own tag types
                                if (hidro)
                                {
                                    ToolProgress.Pulse($"{plan.Name}: pontos hidrossanitários");
                                    var pts = new FilteredElementCollector(Doc, plan.Id).WherePasses(new ElementMulticategoryFilter(PointCategories))
                                        .WhereElementIsNotElementType().OfType<FamilyInstance>()
                                        .Where(fi => !onlySel || selIds.Contains(fi.Id)).ToList();
                                    var water = new List<FamilyInstance>();
                                    var sewer = new List<FamilyInstance>();
                                    foreach (FamilyInstance fi in pts)
                                    {
                                        Classify(fi, aguaWords, esgotoWords, out bool w, out bool sw);
                                        if (w) water.Add(fi);
                                        if (sw) sewer.Add(fi);
                                        if (!w && !sw) unclassified++;
                                    }
                                    points += pts.Count;
                                    if (fPointDims.Value)
                                    {
                                        var both = water.Concat(sewer).Distinct().ToList();
                                        if (both.Count > 0)
                                            pointRows += DimPoints(plan, both, walls ?? (walls = DimEngine.WallsInView(Doc, plan)), true,
                                                                   RevitUtil.M(fTol.Value / 100.0), RevitUtil.M(fPointOff.Value), dimType);
                                    }
                                    // a point with water AND sewer takes two tags: done only with two (never duplicated)
                                    Dictionary<ElementId, int> tagCount = TagCountInView(plan);
                                    var waterIds = new HashSet<ElementId>(water.Select(w => w.Id));
                                    var sewerIds = new HashSet<ElementId>(sewer.Select(w => w.Id));
                                    var skipWater = new HashSet<ElementId>(tagCount.Keys);
                                    var skipSewer = new HashSet<ElementId>(tagCount.Where(kv => kv.Value >= (waterIds.Contains(kv.Key) && sewerIds.Contains(kv.Key) ? 2 : 1)).Select(kv => kv.Key));
                                    double tagOff = fTagOff.Value * plan.Scale / 304.8;
                                    if (fWaterTags.Value)
                                        waterTags += TagPoints(plan, water, skipWater, PointTagType(pointTagTypes, fWaterTagType.Index), tagOff, "hidráulico");
                                    if (fSewerTags.Value)
                                        sewerTags += TagPoints(plan, sewer, skipSewer, PointTagType(pointTagTypes, fSewerTagType.Index), tagOff * 1.8, "de esgoto");
                                }
                                if (fTags.Value)
                                {
                                    ToolProgress.Pulse($"{plan.Name}: etiquetas");
                                    tags += PlanAnnotation.TagElements(Doc, plan, inView.Count == 1 ? inView : null, tagCats, Problems);
                                }
                                if (fAdjust.Value)
                                {
                                    ToolProgress.Pulse($"{plan.Name}: sobreposições");
                                    Doc.Regenerate();
                                    List<ElementId> created = null;
                                    if (fAdjustAll.Index == 1)
                                        created = new FilteredElementCollector(Doc, plan.Id).ToElementIds().Where(id => !before.Contains(id)).ToList();
                                    moved += AnnotationAdjuster.Run(Doc, plan, created).Moved;
                                }
                                t.Commit();
                            }
                            if (fRegion.Value)
                                foreach (Room r in inView)
                                {
                                    var o = PlanRegionMaker.Run(Doc, plan, r, fRegionFb.Index == 1, Problems);
                                    regions += o.Made;
                                    highWin += o.High;
                                }
                            done++;
                        }
                        else if (v is ViewSection ev)
                        {
                            Room r = PlanAnnotation.RoomOfElevation(Doc, ev, rooms);
                            if (r == null) { RevitUtil.AddOnce(Problems, $"{ev.Name}: o marcador não está dentro de um ambiente (vista pulada)."); continue; }
                            using (var t = new Transaction(Doc, "JA Tools - Anotar " + ev.Name))
                            {
                                t.Start();
                                RevitDwgUnifier.Export.FailureSwallower.Attach(t);
                                annot.Annotate(ev, r, RevitUtil.RoomBox(r));
                                t.Commit();
                            }
                            done++;
                        }
                    }
                    catch (Exception ex) { RevitUtil.AddOnce(Problems, $"{v.Name}: {ex.Message}"); }
                }
                ToolProgress.Stage(2, "Gravando no modelo…");
                tg.Assimilate();
            }

            string text = $"{done} de {targets.Count} vista(s) anotada(s)." +
                          (planCount > 0 ? $"\nPlantas: {Created} cota(s), {roomTags} etiqueta(s) de ambiente, {tags} etiqueta(s) de elementos" +
                                           (moved > 0 ? $", {moved} anotação(ões) ajustadas." : ".") +
                                           (highWin > 0 ? $"\nEsquadrias acima do corte: {highWin}, {regions} região(ões) de planta." : "") : "") +
                          (hidro && points > 0 ? $"\nPontos hidrossanitários: {points}, {pointRows} fila(s) cotadas, {waterTags} etiqueta(s) hidráulica(s), {sewerTags} de esgoto." : "") +
                          (elevCount > 0 ? $"\nElevações: {annot.Dims} cota(s)" + (annot.Notes > 0 ? $", {annot.Notes} texto(s) de forro." : ".") : "");
            if (_unmarked.Count > 0) RevitUtil.AddOnce(Problems, $"{_unmarked.Count} ponto(s) sem Marca nem Marca de tipo não foram etiquetados (preencha a marca para etiquetá-los).");
            if (unclassified > 0) RevitUtil.AddOnce(Problems, $"{unclassified} elemento(s) não reconhecidos como ponto hidráulico nem de esgoto (sem conectores e sem as palavras da lista).");
            if (Problems.Count > 0) text += "\n\nAvisos:\n" + string.Join("\n", Problems.Take(18).Select(p => "  • " + p));
            Show(text);
            return Result.Succeeded;
        }

        /// <summary>Rooms of the plan's level whose point is inside its crop region.</summary>
        private List<Room> RoomsInView(ViewPlan plan)
        {
            var list = new FilteredElementCollector(Doc, plan.Id).OfCategory(BuiltInCategory.OST_Rooms).OfType<Room>()
                .Where(r => r.Area > 1e-6 && r.Location != null).ToList();
            if (!plan.CropBoxActive) return list;
            BoundingBoxXYZ cb = plan.CropBox;
            Transform inv = cb.Transform.Inverse;
            return list.Where(r =>
            {
                XYZ p = RevitUtil.RoomPoint(r);
                if (p == null) return false;
                XYZ q = inv.OfPoint(new XYZ(p.X, p.Y, cb.Transform.Origin.Z));
                return q.X >= cb.Min.X && q.X <= cb.Max.X && q.Y >= cb.Min.Y && q.Y <= cb.Max.Y;
            }).ToList();
        }
    
        // ------------------------------------------------------------------ classification
        private static string[] Words(string text) =>
            (text ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(w => Fold(w.Trim())).Where(w => w.Length > 0).ToArray();

        private static string Fold(string s) => TextUtil.Fold(s);

        private static bool Matches(string folded, HashSet<string> tokens, string[] words) =>
            words.Any(w => w.Length <= 3 ? tokens.Contains(w) : folded.Contains(w));

        private static void Classify(FamilyInstance fi, string[] agua, string[] esgoto, out bool water, out bool sewer)
        {
            water = false; sewer = false;
            // 1. pipe connectors
            try
            {
                ConnectorSet cs = fi.MEPModel?.ConnectorManager?.Connectors;
                if (cs != null)
                    foreach (Connector c in cs)
                    {
                        if (c.Domain != Domain.DomainPiping) continue;
                        PipeSystemType st;
                        try { st = c.PipeSystemType; } catch { continue; }
                        if (st == PipeSystemType.DomesticColdWater || st == PipeSystemType.DomesticHotWater) water = true;
                        else if (st == PipeSystemType.Sanitary || st == PipeSystemType.Vent) sewer = true;
                    }
            }
            catch { }
            if (water || sewer) return;

            // 2. words of the family / type name
            string name = Fold((fi.Symbol?.FamilyName ?? "") + " " + (fi.Symbol?.Name ?? "") + " " + fi.Name);
            var tokens = new HashSet<string>(name.Split(new[] { ' ', '-', '_', '.', ',', '(', ')', '/', ':', '+' }, StringSplitOptions.RemoveEmptyEntries));
            sewer = Matches(name, tokens, esgoto);
            water = !sewer && Matches(name, tokens, agua);
            // plumbing fixtures with nothing else known are water points
            if (!water && !sewer && fi.Category?.Id.Value == (long)BuiltInCategory.OST_PlumbingFixtures) water = true;
        }

        // ------------------------------------------------------------------ tags

        private Dictionary<ElementId, int> TagCountInView(View view)
        {
            var count = new Dictionary<ElementId, int>();
            foreach (IndependentTag tag in new FilteredElementCollector(Doc, view.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>())
            {
                try
                {
                    foreach (ElementId id in tag.GetTaggedLocalElementIds())
                        count[id] = count.TryGetValue(id, out int n) ? n + 1 : 1;
                }
                catch { }
            }
            return count;
        }

        private readonly HashSet<ElementId> _unmarked = new HashSet<ElementId>();

        private static FamilySymbol PointTagType(List<FamilySymbol> types, int choice) => choice > 0 && choice <= types.Count ? types[choice - 1] : null;

        private int TagPoints(View view, List<FamilyInstance> items, HashSet<ElementId> skip, FamilySymbol type, double offset, string what)
        {
            int n = 0;
            bool multi = type?.Category?.Id.Value == (long)BuiltInCategory.OST_MultiCategoryTags;
            var failedCats = new HashSet<long>();
            foreach (FamilyInstance fi in items)
            {
                if (skip.Contains(fi.Id) || fi.Category == null || failedCats.Contains(fi.Category.Id.Value)) continue;
                if (!RevitUtil.HasMark(Doc, fi)) { _unmarked.Add(fi.Id); continue; }   // no Mark / Type Mark: not tagged
                XYZ c = RevitUtil.Center(fi, view);
                if (c == null) continue;
                XYZ facing = XYZ.BasisY;
                try
                {
                    var f = new XYZ(fi.FacingOrientation.X, fi.FacingOrientation.Y, 0);
                    if (f.GetLength() > 1e-6) facing = f.Normalize();
                }
                catch { }
                XYZ head = new XYZ(c.X, c.Y, 0) + facing * offset;
                // default: the office's identifier for the category (JA Tools loads it if missing)
                ElementId jaTag = type == null ? TagLibrary.Ensure(Doc, fi.Category, Problems) : null;
                try
                {
                    var tag = IndependentTag.Create(Doc, view.Id, new Reference(fi), true,
                        multi ? TagMode.TM_ADDBY_MULTICATEGORY : TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal, head);
                    if (tag == null) continue;
                    if (jaTag != null) { try { tag.ChangeTypeId(jaTag); } catch { } }
                    if (type != null)
                    {
                        BuiltInCategory? tc = TagCategoryOf((BuiltInCategory)(int)fi.Category.Id.Value);
                        bool same = multi || (tc.HasValue && type.Category?.Id.Value == (long)tc.Value);
                        if (same) { try { tag.ChangeTypeId(type.Id); } catch { } }
                        else RevitUtil.AddOnce(Problems, $"Etiqueta {what}: o tipo escolhido não serve para {fi.Category.Name}; usada a etiqueta padrão dessa categoria.");
                    }
                    skip.Add(fi.Id);
                    n++;
                }
                catch (Exception ex)
                {
                    failedCats.Add(fi.Category.Id.Value);
                    RevitUtil.AddOnce(Problems, $"Etiqueta {what} em {fi.Category.Name}: {ex.Message} (carregue uma família de etiqueta dessa categoria ou use uma etiqueta multicategoria).");
                }
            }
            return n;
        }
    }
}
