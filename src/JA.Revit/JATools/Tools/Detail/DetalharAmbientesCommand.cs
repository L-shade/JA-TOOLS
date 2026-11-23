using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using RevitDwgUnifier.Tools.Common;
using RevitDwgUnifier.Tools.Dimensions;
using RevitDwgUnifier.Tools.Views;

namespace RevitDwgUnifier.Tools.Detail
{
    // =====================================================================================
    //  Detalhar Ambientes
    // =====================================================================================

    /// <summary>
    /// Detalhar Ambientes (one click per room, in the spirit of Ascent.Detail): for every chosen room
    ///   * a cropped plan with the room dimensions (every wall direction), the room tag, element tags
    ///     (type chosen per category) placed like a draftsman would, the overlap check, and plan
    ///     regions over the windows above the cut plane;
    ///   * the four interior elevations, cropped to the room, with the overall dimensions, the
    ///     height of the counters and a text where a ceiling is cut;
    ///   * an isometric with a section box, its crop fitted to the box;
    ///   * the views on a new sheet (or an existing one), with a sheet editor (preview) before
    ///     anything is placed; the layout of the first sheet can be repeated on the next ones.
    /// "Rápido" mode creates only the views and the sheets (the annotation can be done later, view
    /// by view or a whole sheet at once, with Anotar Vista).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class DetalharAmbientesCommand : DimToolCommand
    {
        public override string Title => "Detalhar Ambientes";

        protected override Result Run()
        {
            List<Room> rooms = RevitUtil.Rooms(Doc);
            if (rooms.Count == 0) { Show("O projeto não tem ambientes colocados."); return Result.Cancelled; }
            var pre = new HashSet<ElementId>(UiDoc.Selection.GetElementIds());
            var planTemplates = RevitUtil.Templates(Doc, Autodesk.Revit.DB.ViewType.FloorPlan);
            var elevTemplates = RevitUtil.Templates(Doc, Autodesk.Revit.DB.ViewType.Elevation);
            var isoTemplates = RevitUtil.Templates(Doc, Autodesk.Revit.DB.ViewType.ThreeD);
            var elevTypes = new FilteredElementCollector(Doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .Where(x => x.ViewFamily == ViewFamily.Elevation).OrderBy(x => x.Name).ToList();
            var titleBlocks = new FilteredElementCollector(Doc).OfCategory(BuiltInCategory.OST_TitleBlocks).OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>().OrderBy(s => s.FamilyName).ThenBy(s => s.Name).ToList();
            var sheetsInProject = new FilteredElementCollector(Doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .Where(s => !s.IsPlaceholder && !s.IsTemplate).OrderBy(s => s.SheetNumber, new NaturalStringComparer()).ToList();
            var roomTagTypes = new FilteredElementCollector(Doc).OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_RoomTags)
                .Cast<FamilySymbol>().OrderBy(s => s.FamilyName).ThenBy(s => s.Name).ToList();
            var textTypes = new FilteredElementCollector(Doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>().OrderBy(t => t.Name).ToList();
            List<TagCatOption> tagCats = PlanAnnotation.Categories(Doc);
            var planTypes = PadraoPlanta.TiposPlanta(Doc);
            var spotTypes = PadraoPlanta.TiposCotaNivel(Doc);
            var angTypes = PadraoPlanta.TiposCotaAngular(Doc);

            var dlg = new OptionsDialog(Title, "DetalharAmbientes", "Detalhar");
            dlg.Section("Ambientes");
            var fRooms = dlg.Multi("rooms", "Ambientes", rooms.Select(RevitUtil.RoomLabel).ToList(), false);
            if (pre.Count > 0) for (int i = 0; i < rooms.Count; i++) fRooms.Checked[i] = pre.Contains(rooms[i].Id);

            dlg.Section("Execução");
            var fMode = dlg.Choice("mode", "O que fazer", new[] { "Completo (vistas, anotação e prancha)", "Rápido (vistas e prancha; anotar depois)" }, 0,
                "Rápido: cria as vistas e as pranchas em poucos segundos; depois use Anotar Vista numa vista, em várias vistas " +
                "selecionadas no navegador ou na prancha inteira para colocar cotas, etiquetas e textos.");

            dlg.Section("Vistas");
            var fPlan = dlg.Check("plan", "Planta do ambiente (recortada)", true);
            var fElev = dlg.Check("elev", "Quatro elevações internas", true);
            var fIso = dlg.Check("iso", "Isométrico com caixa de corte", true);
            var fScale = dlg.Number("scale", "Escala 1:", 25, 1, 1000);
            var fIsoScale = dlg.Number("isoScale", "Escala do isométrico 1:", 50, 1, 1000);
            var fPlanT = dlg.Choice("planT", "Modelo da planta", RevitUtil.TemplateChoices(planTemplates),
                PadraoPlanta.Padrao(RevitUtil.TemplateChoices(planTemplates), PadraoPlanta.Modelo, 0));
            var planTypeNames = planTypes.Select(x => x.Name).ToList();
            var fPlanType = dlg.Choice("planType", "Tipo de vista da planta", planTypeNames, PadraoPlanta.Padrao(planTypeNames, PadraoPlanta.TipoVista, 0),
                "O tipo da vista modelo do escritório é “Planta. Nivel Acabado” (o prefixo LB é ignorado).");
            var fElevT = dlg.Choice("elevT", "Modelo das elevações", RevitUtil.TemplateChoices(elevTemplates));
            var fElevType = dlg.Choice("elevType", "Tipo de elevação", elevTypes.Select(x => x.Name).ToList());
            var fIsoT = dlg.Choice("isoT", "Modelo do isométrico", RevitUtil.TemplateChoices(isoTemplates));
            var fCropMode = dlg.Choice("cropMode", "Recorte da planta", new[] { "Contorno do ambiente (com as paredes)", "Retângulo com margem" }, 0,
                "Contorno: a planta mostra só o ambiente e as suas paredes, recortada no formato dele (L, ângulos...). Retângulo: o retângulo do ambiente mais a margem.");
            var fMargin = dlg.Number("margin", "Margem do recorte retangular (m)", 0.5, 0, 20);
            fMargin.EnabledWhen(fCropMode, 1);
            fPlanT.EnabledWhen(fPlan); fElevT.EnabledWhen(fElev); fElevType.EnabledWhen(fElev); fIsoT.EnabledWhen(fIso); fIsoScale.EnabledWhen(fIso);

            Func<bool> full = () => fMode.LiveIndex == 0;
            dlg.Section("Planta");
            var fDims = dlg.Check("dims", "Cotas internas (todas as direções das paredes)", true);
            var fType = AddTypeChoice(dlg);
            fType.Index = PadraoPlanta.Padrao(fType.Items, PadraoPlanta.CotaLinear, fType.Index);
            var fRoomTag = dlg.Check("roomTag", "Etiqueta do ambiente", true);
            var fRoomTagType = dlg.Choice("roomTagType", "Tipo da etiqueta de ambiente",
                new List<string> { "(padrão do projeto)" }.Concat(roomTagTypes.Select(s => s.FamilyName + " : " + s.Name)).ToList());
            fRoomTagType.Index = PadraoPlanta.Padrao(fRoomTagType.Items, PadraoPlanta.EtiquetaAmbiente, 0);
            var spotNames = new List<string> { "(padrão do projeto)" }.Concat(spotTypes.Select(t => t.Name)).ToList();
            var fSpot = dlg.Check("spotRoom", "Cota de nível do piso em cada ambiente", true,
                "Uma cota de nível no piso acabado, logo abaixo da etiqueta do ambiente, relativa ao nível “Osso” do pavimento quando houver (P.A. / P.O.).");
            var fSpotType = dlg.Choice("spotRoomType", "Tipo da cota de nível do piso", spotNames, PadraoPlanta.Padrao(spotNames, PadraoPlanta.NivelPiso, 0));
            var fSill = dlg.Check("spotSill", "Cota de nível nas soleiras das portas", true, "No topo da soleira (ou do piso) embaixo de cada porta do ambiente.");
            var fSillType = dlg.Choice("spotSillType", "Tipo da cota de nível da soleira", spotNames, PadraoPlanta.Padrao(spotNames, PadraoPlanta.NivelSoleira, 0));
            var angNames = new List<string> { "(padrão do projeto)" }.Concat(angTypes.Select(t => t.Name)).ToList();
            var fAng = dlg.Check("angDims", "Cota angular nos cantos fora de esquadro", true, "Entre as faces das duas paredes, nos cantos que não têm 90°.");
            var fAngType = dlg.Choice("angDimType", "Tipo da cota angular", angNames, PadraoPlanta.Padrao(angNames, PadraoPlanta.CotaAngular, 0));
            var fTags = dlg.Check("tags", "Etiquetas dos elementos (tipo de cada categoria abaixo)", true);
            var fAdjust = dlg.Check("adjust", "Ajustar sobreposições", true);
            var fRegion = dlg.Check("planRegion", "Região de planta nas esquadrias acima do plano de corte", true);
            var fRegionFb = dlg.Choice("planRegionFb", "Sem região de planta modelo no projeto", new[] { "Só avisar", "Elevar o corte da planta" }, 0,
                "O Revit não deixa criar região de planta do zero: o JA Tools copia uma região de planta do projeto (de preferência com JA em Comentários; " +
                "o corte dela é o usado) e redesenha o contorno em volta da janela. Sem nenhuma, avisa ou eleva o plano de corte da planta do ambiente.");
            foreach (var f in new OptionsDialog.Field[] { fDims, fRoomTag, fTags, fAdjust, fRegion, fSpot, fSill, fAng }) f.EnabledWhen(() => fPlan.Live && full());
            fSpotType.EnabledWhen(() => fSpot.Live && fPlan.Live && full());
            fSillType.EnabledWhen(() => fSill.Live && fPlan.Live && full());
            fAngType.EnabledWhen(() => fAng.Live && fPlan.Live && full());
            fPlanType.EnabledWhen(fPlan);
            fType.EnabledWhen(() => fDims.Live && fPlan.Live && full());
            fRoomTagType.EnabledWhen(() => fRoomTag.Live && fPlan.Live && full());
            fRegionFb.EnabledWhen(() => fRegion.Live && fPlan.Live && full());
            PlanAnnotation.AddFields(Doc, dlg, tagCats, () => fTags.Live && fPlan.Live && full());

            dlg.Section("Elevações");
            var fElevDims = dlg.Check("elevDims", "Cotas gerais (largura e pé-direito)", true);
            var fCounter = dlg.Check("counterDims", "Cota da altura das bancadas", true);
            var fElevDimType = dlg.Choice("elevDimType", "Tipo de cota das elevações", DimEngine.TypeChoices(DimTypes));
            var fCeil = dlg.Check("ceilNote", "Texto quando a elevação corta o forro", true);
            var fCeilText = dlg.Text("ceilText", "Texto do forro", "FORRO EM VISTA");
            var fTextType = dlg.Choice("textType", "Tipo de texto",
                new List<string> { "(padrão do projeto)" }.Concat(textTypes.Select(t => t.Name)).ToList());
            fCeilText.EnabledWhen(() => fCeil.Live && fElev.Live && full());
            fTextType.EnabledWhen(() => fCeil.Live && fElev.Live && full());
            foreach (var f in new OptionsDialog.Field[] { fElevDims, fCounter, fElevDimType, fCeil }) f.EnabledWhen(() => fElev.Live && full());

            dlg.Section("Prancha");
            var fDest = dlg.Choice("dest", "Destino", new[] { "Não criar prancha", "Nova por ambiente", "Prancha existente" }, 1);
            var fTb = dlg.Choice("tb", "Carimbo", titleBlocks.Select(s => s.FamilyName + " : " + s.Name).ToList());
            var fNumber = dlg.Text("number", "Primeira prancha", "D101");
            var fSheetName = dlg.Text("sheetName", "Nome da prancha ({ambiente})", "DETALHAMENTO - {ambiente}");
            var fSheet = dlg.Choice("sheet", "Prancha existente", sheetsInProject.Select(s => s.SheetNumber + " - " + s.Name).ToList());
            var fMargins = dlg.Text("margins", "Margens esq., sup., dir., inf. (mm)", "25, 10, 10, 45",
                "Área útil da prancha: distância das bordas do carimbo. A inferior deixa espaço para a faixa de informações.");
            var fPreview = dlg.Check("preview", "Pré-visualizar e montar o layout antes de colocar as vistas", true);
            var fShortTitle = dlg.Check("shortTitle", "Título curto na prancha (PLANTA, ISOMÉTRICO, ELEVAÇÃO A...)", true,
                "Preenche o \"Título na folha\" de cada vista: a prancha já é do ambiente, então o título não repete o nome dele e não invade as vistas vizinhas. O nome da vista no navegador continua completo.");
            var fReuse = dlg.Check("reuse", "Repetir o layout da primeira prancha nas seguintes (sem perguntar)", true);
            fTb.EnabledWhen(fDest, 1); fNumber.EnabledWhen(fDest, 1); fSheetName.EnabledWhen(fDest, 1);
            fSheet.EnabledWhen(fDest, 2); fMargins.EnabledWhen(fDest, 1, 2); fPreview.EnabledWhen(fDest, 1, 2);
            fReuse.EnabledWhen(() => fDest.LiveIndex != 0 && fPreview.Live);
            if (!dlg.Show(Owner)) return Result.Cancelled;

            var chosen = fRooms.SelectedIndices.Select(i => rooms[i]).ToList();
            if (chosen.Count == 0) { Show("Marque ao menos um ambiente."); return Result.Cancelled; }
            bool annotate = fMode.Index == 0;
            int scale = (int)fScale.Value, isoScale = (int)fIsoScale.Value;
            double margin = RevitUtil.M(fMargin.Value);
            DimensionType dimType = SelectedType(fType);
            double[] mm = ParseMargins(fMargins.Value);
            int dest = fDest.Index;
            FamilySymbol tb = dest == 1 && fTb.Index >= 0 && fTb.Index < titleBlocks.Count ? titleBlocks[fTb.Index] : null;
            ViewSheet existingSheet = dest == 2 && fSheet.Index >= 0 && fSheet.Index < sheetsInProject.Count ? sheetsInProject[fSheet.Index] : null;
            if (dest == 1 && tb == null) RevitUtil.AddOnce(Problems, "O projeto não tem carimbo: as pranchas não foram criadas.");
            if (dest == 2 && existingSheet == null) RevitUtil.AddOnce(Problems, "Nenhuma prancha existente escolhida: as vistas não foram colocadas.");
            ViewFamilyType elevType = elevTypes.Count > 0 ? elevTypes[Math.Max(0, Math.Min(fElevType.Index, elevTypes.Count - 1))] : null;
            FamilySymbol roomTagType = fRoomTagType.Index > 0 ? roomTagTypes[fRoomTagType.Index - 1] : null;
            var annot = new ElevationAnnotator(Doc, Problems)
            {
                DimType = DimEngine.TypeFromChoice(DimTypes, fElevDimType.Index),
                Overall = fElevDims.Value, Counters = fCounter.Value, CeilingNote = fCeil.Value,
                CeilingText = fCeilText.Value, TextTypeId = fTextType.Index > 0 ? textTypes[fTextType.Index - 1].Id : null
            };
            bool annotateElev = annotate && (fElevDims.Value || fCounter.Value || fCeil.Value);

            // progress box: one step per kind of work, the bar counts the rooms, with time left
            var stages = new List<string>();
            if (fPlan.Value) stages.Add(annotate ? "Planta e anotação" : "Planta");
            if (fElev.Value) stages.Add("Elevações");
            if (fIso.Value) stages.Add("Isométrico");
            if (tb != null || existingSheet != null) stages.Add("Prancha");
            stages.Add("Concluir");
            ToolProgress.Setup($"Detalhando {chosen.Count} ambiente(s)" + (annotate ? "" : " (rápido)"), stages.ToArray(), cancellable: true);
            Action<int, int, string> step = (stageIndex, roomIndex, msg) =>
            {
                ToolProgress.Stage(stageIndex, msg);
                ToolProgress.Report(roomIndex, chosen.Count, msg);
            };
            int sPlan = stages.IndexOf(annotate ? "Planta e anotação" : "Planta"), sElev = stages.IndexOf("Elevações"),
                sIso = stages.IndexOf("Isométrico"), sSheet = stages.IndexOf("Prancha");

            var createdViews = new List<View>();
            var sheets = new List<ViewSheet>();
            int tagsMade = 0, moved = 0, regions = 0, highWindows = 0;
            bool skipPreview = false;
            Dictionary<string, double[]> layoutTemplate = null;     // key -> dx, dy (from usable top-left), scale, excluded
            using (var tg = new TransactionGroup(Doc, "JA Tools - " + Title))
            {
                tg.Start();
                var usedNumbers = new HashSet<string>(sheetsInProject.Select(s => s.SheetNumber), StringComparer.OrdinalIgnoreCase);
                string number = string.IsNullOrWhiteSpace(fNumber.Value) ? "D101" : fNumber.Value.Trim();

                int ri = -1;
                foreach (Room r in chosen)
                {
                    ri++;
                    if (ToolProgress.Cancelled)
                    {
                        tg.RollBack();
                        Show("Detalhar Ambientes cancelado. Nada foi alterado no modelo.", ResultKind.Info);
                        return Result.Cancelled;
                    }
                    string title = (string.IsNullOrWhiteSpace(r.Number) ? "" : r.Number + " - ") + RevitUtil.RoomName(r);
                    string head = $"Ambiente {ri + 1} de {chosen.Count} — {title}";
                    BoundingBoxXYZ rb = RevitUtil.RoomBox(r);
                    XYZ rp = RevitUtil.RoomPoint(r);
                    if (rb == null || rp == null || r.Level == null) { RevitUtil.AddOnce(Problems, $"{title}: ambiente sem contorno."); continue; }
                    ViewPlan plan = null;
                    View iso = null;
                    var elevs = new List<ViewSection>();

                    using (var t = new Transaction(Doc, "JA Tools - Detalhar " + title))
                    {
                        t.Start();
                        RevitDwgUnifier.Export.FailureSwallower.Attach(t);

                        // ---- plan
                        if (fPlan.Value)
                        {
                            step(sPlan, ri, head + ": planta");
                            ElementId planTypeId = fPlanType.Index >= 0 && fPlanType.Index < planTypes.Count ? planTypes[fPlanType.Index].Id : RevitUtil.FamilyType(Doc, ViewFamily.FloorPlan).Id;
                            plan = ViewPlan.Create(Doc, planTypeId, r.LevelId);
                            RevitUtil.Configure(plan, scale, RevitUtil.TemplateFromChoice(planTemplates, fPlanT.Index));
                            RevitUtil.Rename(plan, "PLANTA - " + title);
                            if (fShortTitle.Value) SetSheetTitle(plan, "PLANTA");
                            try { RevitUtil.CropPlan(plan, rb.Min.X - margin, rb.Min.Y - margin, rb.Max.X + margin, rb.Max.Y + margin, false); }
                            catch (Exception ex) { RevitUtil.AddOnce(Problems, $"{title}: recorte ({ex.Message})."); }
                            if (fCropMode.Index == 0 && !CropToRoom(Doc, plan, r, RevitUtil.M(0.15)))
                                RevitUtil.AddOnce(Problems, $"{title}: o contorno do ambiente não serviu de recorte; usado o retângulo.");
                            if (annotate)
                            {
                                Doc.Regenerate();
                                if (fRoomTag.Value) PlanAnnotation.TagRoom(Doc, plan, r, roomTagType, Problems);
                                if (fDims.Value)
                                {
                                    ToolProgress.Pulse(head + ": cotas");
                                    List<Wall> walls = DimEngine.WallsInView(Doc, plan);
                                    DimRoomAll(plan, walls, r, RevitUtil.M(0.4), DimEngine.ViewZ(plan), dimType, false);
                                }
                                if (fAng.Value)
                                {
                                    DimensionType at = fAngType.Index > 0 ? angTypes[fAngType.Index - 1] : null;
                                    if (at == null) at = angTypes.FirstOrDefault();
                                    if (at != null) PadraoPlanta.AngulosDoAmbiente(Doc, plan, r, at, Problems);
                                }
                                if (fSpot.Value || fSill.Value)
                                {
                                    ToolProgress.Pulse(head + ": cotas de nível");
                                    Doc.Regenerate();
                                    if (fSpot.Value) PadraoPlanta.NivelDoAmbiente(Doc, plan, r, fSpotType.Index > 0 ? spotTypes[fSpotType.Index - 1] : null, Problems);
                                    if (fSill.Value) PadraoPlanta.NiveisDasSoleiras(Doc, plan, r, fSillType.Index > 0 ? spotTypes[fSillType.Index - 1] : null, new HashSet<ElementId>(), Problems);
                                }
                                if (fTags.Value)
                                {
                                    ToolProgress.Pulse(head + ": etiquetas");
                                    tagsMade += PlanAnnotation.TagElements(Doc, plan, new[] { r }, tagCats, Problems);
                                }
                                if (fAdjust.Value)
                                {
                                    ToolProgress.Pulse(head + ": sobreposições");
                                    Doc.Regenerate();
                                    moved += AnnotationAdjuster.Run(Doc, plan, null).Moved;
                                }
                            }
                        }

                        // ---- elevations: created together, regenerated once, then annotated
                        if (fElev.Value && elevType != null)
                        {
                            step(sElev, ri, head + ": elevações");
                            ViewPlan host = plan ?? RevitUtil.PlanOf(Doc, r.Level);
                            XYZ center = new XYZ((rb.Min.X + rb.Max.X) / 2, (rb.Min.Y + rb.Max.Y) / 2, rb.Min.Z);
                            if (!r.IsPointInRoom(new XYZ(center.X, center.Y, rb.Min.Z + RevitUtil.M(1)))) center = new XYZ(rp.X, rp.Y, rb.Min.Z);
                            try
                            {
                                // one elevation per wall direction, each looking straight at its wall (any angle)
                                List<RevitDwgUnifier.Tools.Views.LadoParede> lados = null;
                                try { lados = RevitDwgUnifier.Tools.Views.ParedesAmbiente.Lados(r, RevitUtil.M(0.6)); } catch { }
                                if (lados == null || lados.Count == 0) lados = RevitDwgUnifier.Tools.Views.ParedesAmbiente.LadosOrtogonais(r);
                                var criadas = RevitDwgUnifier.Tools.Views.ParedesAmbiente.CriarElevacoes(Doc, elevType.Id, host.Id, center, scale, lados, Problems, title);
                                foreach (var (ld, ev) in criadas)
                                {
                                    RevitUtil.Configure(ev, scale, RevitUtil.TemplateFromChoice(elevTemplates, fElevT.Index));
                                    RevitUtil.Rename(ev, $"{title} - ELEVAÇÃO {ld.Letra}");
                                    if (fShortTitle.Value) SetSheetTitle(ev, "ELEVAÇÃO " + ld.Letra);
                                    elevs.Add(ev);
                                }
                                Doc.Regenerate();
                                double wallT = Math.Max(RevitUtil.M(0.08), Math.Min(RevitUtil.M(0.5), BoundingWallThickness(Doc, r))) + RevitUtil.M(0.02);
                                foreach (var (ld, ev) in criadas)
                                {
                                    bool visivel = true;
                                    try { visivel = ev.CropBoxVisible; } catch { }
                                    RevitDwgUnifier.Tools.Views.ParedesAmbiente.Recortar(ev, ld, rb.Min.Z, rb.Max.Z, wallT, visivel);
                                }
                                if (annotateElev)
                                {
                                    Doc.Regenerate();
                                    foreach (ViewSection ev in elevs)
                                    {
                                        ToolProgress.Pulse($"{head}: cotas de {ev.Name}");
                                        try { annot.Annotate(ev, r, rb); }
                                        catch (Exception ex) { RevitUtil.AddOnce(Problems, $"{ev.Name}: anotação ({ex.Message})."); }
                                    }
                                }
                            }
                            catch (Exception ex) { RevitUtil.AddOnce(Problems, $"{title}: elevações ({ex.Message})."); }
                        }

                        // ---- isometric, its crop fitted to the section box (the viewport is the size of the drawing)
                        if (fIso.Value)
                        {
                            step(sIso, ri, head + ": isométrico");
                            try
                            {
                                View3D v = View3D.CreateIsometric(Doc, RevitUtil.FamilyType(Doc, ViewFamily.ThreeDimensional).Id);
                                RevitUtil.Configure(v, isoScale, RevitUtil.TemplateFromChoice(isoTemplates, fIsoT.Index));
                                RevitUtil.Rename(v, "ISOMÉTRICO - " + title);
                                if (fShortTitle.Value) SetSheetTitle(v, "ISOMÉTRICO");
                                double top = Math.Min(rb.Max.Z, rb.Min.Z + RevitUtil.M(2.4));
                                var box = new BoundingBoxXYZ
                                {
                                    Min = new XYZ(rb.Min.X - RevitUtil.M(0.3), rb.Min.Y - RevitUtil.M(0.3), rb.Min.Z - RevitUtil.M(0.2)),
                                    Max = new XYZ(rb.Max.X + RevitUtil.M(0.3), rb.Max.Y + RevitUtil.M(0.3), top)
                                };
                                v.SetSectionBox(box);
                                XYZ c = (box.Min + box.Max) / 2, eyeDir = new XYZ(1, -1, 1).Normalize(), fwd = -eyeDir;
                                XYZ upv = (XYZ.BasisZ - fwd * XYZ.BasisZ.DotProduct(fwd)).Normalize();
                                v.SetOrientation(new ViewOrientation3D(c + eyeDir * RevitUtil.M(50), upv, fwd));
                                try { v.SetCategoryHidden(new ElementId(BuiltInCategory.OST_SectionBox), true); } catch { }
                                Doc.Regenerate();
                                CropIsometric(v, box, RevitUtil.M(0.25));
                                iso = v;
                            }
                            catch (Exception ex) { RevitUtil.AddOnce(Problems, $"{title}: isométrico ({ex.Message})."); }
                        }
                        t.Commit();
                    }

                    // ---- plan regions over the windows above the cut plane (own transactions)
                    if (annotate && plan != null && fRegion.Value)
                    {
                        ToolProgress.Pulse(head + ": esquadrias acima do corte");
                        try
                        {
                            var o = PlanRegionMaker.Run(Doc, plan, r, fRegionFb.Index == 1, Problems);
                            regions += o.Made;
                            highWindows += o.High;
                        }
                        catch (Exception ex) { RevitUtil.AddOnce(Problems, $"{title}: região de planta ({ex.Message})."); }
                    }

                    var views = new List<View>();
                    var keys = new List<string>();
                    if (plan != null) { views.Add(plan); keys.Add("P"); }
                    if (iso != null) { views.Add(iso); keys.Add("I"); }
                    for (int k = 0; k < elevs.Count; k++) { views.Add(elevs[k]); keys.Add("E" + k); }
                    createdViews.AddRange(views);

                    // ---- sheet
                    if (views.Count == 0 || (tb == null && existingSheet == null)) continue;
                    if (ToolProgress.Cancelled)
                    {
                        tg.RollBack();
                        Show("Detalhar Ambientes cancelado. Nada foi alterado no modelo.", ResultKind.Info);
                        return Result.Cancelled;
                    }
                    step(sSheet, ri, head + ": montando a prancha");
                    using (var t = new Transaction(Doc, "JA Tools - Prancha " + title))
                    {
                        t.Start();
                        RevitDwgUnifier.Export.FailureSwallower.Attach(t);
                        ViewSheet newSheet = null;
                        ViewSheet sheet;
                        if (existingSheet != null) sheet = existingSheet;
                        else
                        {
                            if (!tb.IsActive) tb.Activate();
                            sheet = newSheet = ViewSheet.Create(Doc, tb.Id);
                            while (usedNumbers.Contains(number)) number = LegendEngine.NextNumber(number);
                            try { sheet.SheetNumber = number; } catch { }
                            usedNumbers.Add(number);
                            number = LegendEngine.NextNumber(number);
                            try { sheet.Name = (fSheetName.Value ?? "{ambiente}").Replace("{ambiente}", title); } catch { }
                        }
                        Doc.Regenerate();
                        var vps = new List<Viewport>();
                        var vpKeys = new List<string>();
                        for (int i = 0; i < views.Count; i++)
                        {
                            try
                            {
                                if (!Viewport.CanAddViewToSheet(Doc, sheet.Id, views[i].Id)) continue;
                                vps.Add(Viewport.Create(Doc, sheet.Id, views[i].Id, XYZ.Zero));
                                vpKeys.Add(keys[i]);
                            }
                            catch (Exception ex) { RevitUtil.AddOnce(Problems, $"{views[i].Name}: não colocada na prancha ({ex.Message})."); }
                        }
                        Doc.Regenerate();

                        var choices = new List<ViewSheet>();
                        choices.Add(sheet);
                        choices.AddRange(sheets.Where(s => s.Id != sheet.Id));
                        choices.AddRange(sheetsInProject.Where(s => s.Id != sheet.Id));
                        var hostSheet = new SheetHost(Doc, sheet, newSheet?.Id, vps, choices, titleBlocks);
                        List<LayoutBox> boxes = hostSheet.FixedBoxes();
                        boxes.AddRange(hostSheet.FreeBoxes(vpKeys));
                        foreach (LayoutBox b in boxes.Where(x => !x.Fixed && x.Tag is string))
                        {
                            string key = (string)b.Tag;
                            View bv = b.Index >= 0 && b.Index < vps.Count ? Doc.GetElement(vps[b.Index].ViewId) as View : null;
                            b.Short = key == "P" ? "Planta" : key == "I" ? "Isométrico" : key.StartsWith("E") ? "Elevação " + "ABCD"[Math.Min(3, int.Parse(key.Substring(1)))] : b.Name;
                            try { b.Sketch = Sketch(bv, r, rb); } catch { }
                        }
                        double[] paper = hostSheet.Paper();
                        double[] usable = Usable(paper, mm);

                        // layout: the one repeated from the first sheet when it fits, else first-fit
                        bool templated = layoutTemplate != null && ApplyTemplate(boxes, layoutTemplate, usable, 6);
                        if (!templated) LayoutEngine.Pack(boxes, usable[0], usable[1], usable[2], usable[3], 6);
                        if (fPreview.Value && !skipPreview && !templated && vps.Count > 0)
                        {
                            LayoutAnswer answer = SheetLayoutDialog.Show(Owner, hostSheet, $"{title}: {vps.Count} vista(s).", paper, mm,
                                boxes, 6, fReuse.Value, out bool dontAsk, out bool reuse);
                            if (dontAsk) skipPreview = true;
                            if (answer == LayoutAnswer.CancelAll)
                            {
                                t.RollBack();
                                tg.RollBack();
                                return Result.Cancelled;
                            }
                            paper = hostSheet.Paper();
                            usable = Usable(paper, mm);
                            if (reuse) layoutTemplate = MakeTemplate(boxes, usable);
                        }
                        sheet = hostSheet.Sheet;
                        vps = hostSheet.Vps;
                        ToolProgress.Pulse(head + ": colocando as vistas");

                        // scales and views taken off the sheet, then the positions
                        foreach (LayoutBox b in boxes.Where(x => !x.Fixed && x.Index >= 0 && x.Index < vps.Count))
                        {
                            Viewport vp = vps[b.Index];
                            if (b.Excluded) { try { Doc.Delete(vp.Id); } catch { } continue; }
                            if (b.Scale > 0 && b.Scale != b.OrigScale)
                            {
                                var v = Doc.GetElement(vp.ViewId) as View;
                                try { v.Scale = b.Scale; }
                                catch { RevitUtil.AddOnce(Problems, $"{v?.Name}: a escala não mudou (o modelo de vista controla a escala)."); }
                            }
                        }
                        Doc.Regenerate();
                        int outside = 0;
                        foreach (LayoutBox b in boxes.Where(x => !x.Fixed && !x.Excluded && x.Index >= 0 && x.Index < vps.Count))
                        {
                            try { vps[b.Index].SetBoxCenter(new XYZ(b.X / 304.8, b.Y / 304.8, 0)); } catch { }
                            if (!b.Inside(usable[0], usable[1], usable[2], usable[3])) outside++;
                        }
                        if (outside > 0)
                            RevitUtil.AddOnce(Problems, $"Prancha {sheet.SheetNumber}: {outside} vista(s) fora da área útil (reduza a escala, use um carimbo maior ou monte o layout na pré-visualização).");

                        // the new sheet left empty (views moved to another sheet) is removed
                        if (newSheet != null && newSheet.Id != sheet.Id && newSheet.GetAllViewports().Count == 0)
                        {
                            string n = newSheet.SheetNumber;
                            try { Doc.Delete(newSheet.Id); usedNumbers.Remove(n); number = n; } catch { }
                        }
                        if (!sheets.Any(s => s.Id == sheet.Id)) sheets.Add(sheet);
                        t.Commit();
                    }
                }
                ToolProgress.Stage(stages.Count - 1, "Gravando no modelo…");
                tg.Assimilate();
            }

            string text = $"{chosen.Count} ambiente(s) detalhado(s): {createdViews.Count} vista(s)" +
                          (sheets.Count > 0 ? $", {sheets.Count} prancha(s) ({string.Join(", ", sheets.Take(8).Select(s => s.SheetNumber))}{(sheets.Count > 8 ? "…" : "")})" : "") + "." +
                          (annotate
                              ? $"\nPlanta: {Created} cota(s), {tagsMade} etiqueta(s) de elementos" + (moved > 0 ? $", {moved} anotação(ões) ajustadas." : ".") +
                                (annotateElev && fElev.Value ? $"\nElevações: {annot.Dims} cota(s)" + (annot.Notes > 0 ? $", {annot.Notes} texto(s) de forro." : ".") : "") +
                                (highWindows > 0 ? $"\nEsquadrias acima do corte: {highWindows}, {regions} região(ões) de planta criada(s)." : "")
                              : "\nModo rápido: para anotar, abra a prancha (ou selecione as vistas no navegador) e use Anotar Vista.");
            if (Problems.Count > 0) text += "\n\nAvisos:\n" + string.Join("\n", Problems.Take(18).Select(p => "  • " + p));
            Show(text);
            if (sheets.Count > 0) TryOpen(sheets[0]); else if (createdViews.Count > 0) TryOpen(createdViews[0]);
            return Result.Succeeded;
        }

        // ------------------------------------------------------------------ sheet layout

        internal static double[] Usable(double[] paper, double[] mm) =>
            new[] { paper[0] + mm[0], paper[3] - mm[1], paper[2] - mm[2], paper[1] + mm[3] };

        /// <summary>The layout of a sheet as offsets of each kind of view (plan, isometric, elevation
        /// A..D) from the top-left corner of the usable area, with its scale and exclusion.</summary>
        private static Dictionary<string, double[]> MakeTemplate(List<LayoutBox> boxes, double[] usable)
        {
            var t = new Dictionary<string, double[]>();
            foreach (LayoutBox b in boxes.Where(x => !x.Fixed && x.Tag is string))
                t[(string)b.Tag] = new[] { b.L - usable[0], b.T - usable[1], b.Scale, b.Excluded ? 1 : 0 };
            return t;
        }

        /// <summary>Repeats the template (same corners, scales, exclusions); false when the result
        /// does not fit or overlaps (then the sheet is arranged and shown as usual).</summary>
        private static bool ApplyTemplate(List<LayoutBox> boxes, Dictionary<string, double[]> template, double[] usable, double gap)
        {
            var free = boxes.Where(x => !x.Fixed).ToList();
            if (free.Any(b => !(b.Tag is string key) || !template.ContainsKey(key))) return false;
            foreach (LayoutBox b in free)
            {
                double[] v = template[(string)b.Tag];
                if (v[2] > 0) b.SetScale((int)v[2]);
                b.Excluded = v[3] > 0.5;
                b.X = usable[0] + v[0] + b.W / 2;
                b.Y = usable[1] + v[1] - b.H / 2;
            }
            var live = boxes.Where(b => !b.Excluded).ToList();
            foreach (LayoutBox b in live.Where(x => !x.Fixed))
            {
                if (!b.Inside(usable[0], usable[1], usable[2], usable[3])) return false;
                if (live.Any(o => o != b && b.Overlaps(o, gap * 0.5))) return false;
            }
            return true;
        }

        /// <summary>The sheet seen by the preview: switches the new views to another sheet, changes
        /// the title block, and measures paper and viewports (mm). Inside the open transaction.</summary>
        internal sealed class SheetHost : ISheetLayoutHost
        {
            private readonly Document _doc;
            private readonly ElementId _newSheetId;
            private readonly List<ViewSheet> _choices;
            private readonly List<FamilySymbol> _tbs;
            public ViewSheet Sheet;
            public List<Viewport> Vps;

            public SheetHost(Document doc, ViewSheet sheet, ElementId newSheetId, List<Viewport> vps, List<ViewSheet> choices, List<FamilySymbol> tbs)
            {
                _doc = doc; Sheet = sheet; _newSheetId = newSheetId; Vps = vps; _choices = choices; _tbs = tbs;
            }

            public string SheetTitle => Sheet.SheetNumber + " - " + Sheet.Name;

            public IList<string> Sheets => _choices.Select(s => s.SheetNumber + " - " + s.Name +
                (!ReferenceEquals(_newSheetId, null) && s.Id == _newSheetId ? "  (nova)" : "")).ToList();

            public int SheetIndex => _choices.FindIndex(s => s.Id == Sheet.Id);

            public IList<string> TitleBlocks => _tbs.Select(s => s.FamilyName + " : " + s.Name).ToList();

            public int TitleBlockIndex
            {
                get
                {
                    Element tbi = TitleBlockOf(Sheet);
                    if (tbi == null) return -1;
                    ElementId type = tbi.GetTypeId();
                    return _tbs.FindIndex(s => s.Id == type);
                }
            }

            private Element TitleBlockOf(ViewSheet s) =>
                new FilteredElementCollector(_doc, s.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType().FirstElement();

            public double[] Paper()
            {
                const double k = 304.8;
                BoundingBoxXYZ area = TitleBlockOf(Sheet)?.get_BoundingBox(Sheet);
                return area != null
                    ? new[] { area.Min.X * k, area.Min.Y * k, area.Max.X * k, area.Max.Y * k }
                    : new[] { 0.0, 0.0, 841.0, 594.0 };
            }

            /// <summary>Viewports and schedules already on the sheet (not ours).</summary>
            public List<LayoutBox> FixedBoxes()
            {
                const double k = 304.8;
                var ours = new HashSet<long>(Vps.Select(v => v.Id.Value));
                var boxes = new List<LayoutBox>();
                foreach (ElementId id in Sheet.GetAllViewports())
                {
                    if (ours.Contains(id.Value) || !(_doc.GetElement(id) is Viewport vp)) continue;
                    try
                    {
                        Outline o = vp.GetBoxOutline();
                        var v = _doc.GetElement(vp.ViewId) as View;
                        var fb = new LayoutBox
                        {
                            Name = v?.Name ?? "Vista", Fixed = true,
                            W = (o.MaximumPoint.X - o.MinimumPoint.X) * k, H = (o.MaximumPoint.Y - o.MinimumPoint.Y) * k,
                            X = (o.MaximumPoint.X + o.MinimumPoint.X) / 2 * k, Y = (o.MaximumPoint.Y + o.MinimumPoint.Y) / 2 * k
                        };
                        SetLabel(fb, vp, o, v);
                        boxes.Add(fb);
                    }
                    catch { }
                }
                foreach (ScheduleSheetInstance ss in new FilteredElementCollector(_doc, Sheet.Id).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>())
                {
                    if (ss.IsTitleblockRevisionSchedule) continue;
                    BoundingBoxXYZ bb = ss.get_BoundingBox(Sheet);
                    if (bb == null) continue;
                    boxes.Add(new LayoutBox
                    {
                        Name = ss.Name, Fixed = true,
                        W = (bb.Max.X - bb.Min.X) * k, H = (bb.Max.Y - bb.Min.Y) * k,
                        X = (bb.Max.X + bb.Min.X) / 2 * k, Y = (bb.Max.Y + bb.Min.Y) / 2 * k
                    });
                }
                return boxes;
            }

            /// <summary>Our viewports, in order (Index = position in Vps; Tag = kind key).</summary>
            public List<LayoutBox> FreeBoxes(List<string> keys)
            {
                const double k = 304.8;
                var boxes = new List<LayoutBox>();
                for (int i = 0; i < Vps.Count; i++)
                {
                    Outline o = Vps[i].GetBoxOutline();
                    var v = _doc.GetElement(Vps[i].ViewId) as View;
                    int sc = 0;
                    try { sc = v?.Scale ?? 0; } catch { }
                    var nb = new LayoutBox
                    {
                        Name = v?.Name ?? "Vista", Index = i, Tag = i < keys.Count ? keys[i] : null, Scale = sc, OrigScale = sc,
                        W = (o.MaximumPoint.X - o.MinimumPoint.X) * k, H = (o.MaximumPoint.Y - o.MinimumPoint.Y) * k,
                        X = (o.MaximumPoint.X + o.MinimumPoint.X) / 2 * k, Y = (o.MaximumPoint.Y + o.MinimumPoint.Y) / 2 * k
                    };
                    SetLabel(nb, Vps[i], o, v);
                    boxes.Add(nb);
                }
                return boxes;
            }

            private static readonly System.Reflection.MethodInfo LabelOutlineMethod = typeof(Viewport).GetMethod("GetLabelOutline", Type.EmptyTypes);

            /// <summary>The viewport title (Revit 2022+: Viewport.GetLabelOutline), relative to the box:
            /// the layout keeps it clear of the other views.</summary>
            private static void SetLabel(LayoutBox b, Viewport vp, Outline box, View v)
            {
                try
                {
                    if (!(LabelOutlineMethod?.Invoke(vp, null) is Outline lo)) return;
                    double w = lo.MaximumPoint.X - lo.MinimumPoint.X, h = lo.MaximumPoint.Y - lo.MinimumPoint.Y;
                    if (w <= 1e-6 || h <= 1e-6) return;
                    const double k = 304.8;
                    b.HasLabel = true;
                    b.LabX0 = (lo.MinimumPoint.X - box.MinimumPoint.X) * k;
                    b.LabX1 = (lo.MaximumPoint.X - box.MinimumPoint.X) * k;
                    b.LabY0 = (lo.MinimumPoint.Y - box.MinimumPoint.Y) * k;
                    b.LabY1 = (lo.MaximumPoint.Y - box.MinimumPoint.Y) * k;
                    string title = v?.get_Parameter(BuiltInParameter.VIEW_DESCRIPTION)?.AsString();
                    b.LabelText = string.IsNullOrWhiteSpace(title) ? v?.Name : title;
                }
                catch { }
            }

            public bool SwitchSheet(int index, out double[] paper, out List<LayoutBox> fixedBoxes, out string error)
            {
                paper = null; fixedBoxes = null; error = null;
                if (index < 0 || index >= _choices.Count) { error = "Prancha inválida."; return false; }
                ViewSheet target = _choices[index];
                if (target.Id == Sheet.Id) { paper = Paper(); fixedBoxes = FixedBoxes(); return true; }
                ViewSheet from = Sheet;
                var viewIds = Vps.Select(v => v.ViewId).ToList();
                try
                {
                    foreach (Viewport vp in Vps) _doc.Delete(vp.Id);
                    _doc.Regenerate();
                    var bad = viewIds.Where(id => !Viewport.CanAddViewToSheet(_doc, target.Id, id)).ToList();
                    ViewSheet dest = bad.Count == 0 ? target : from;
                    var created = viewIds.Select(id => Viewport.Create(_doc, dest.Id, id, XYZ.Zero)).ToList();
                    _doc.Regenerate();
                    Vps = created;
                    Sheet = dest;
                    if (bad.Count > 0) error = "Algumas vistas não podem ir para essa prancha; elas continuam na prancha atual.";
                    paper = Paper();
                    fixedBoxes = FixedBoxes();
                    return bad.Count == 0;
                }
                catch (Exception ex)
                {
                    error = "Não foi possível mudar de prancha: " + ex.Message;
                    return false;
                }
            }

            public IList<double[]> TitleBlockLines()
            {
                var list = new List<double[]>();
                Element tb = TitleBlockOf(Sheet);
                if (tb == null) return list;
                try
                {
                    GeometryElement geo = tb.get_Geometry(new Options { View = Sheet });
                    if (geo != null) Collect(geo, list);
                }
                catch { }
                return list;
            }

            private static void Collect(GeometryElement geo, List<double[]> list)
            {
                foreach (GeometryObject o in geo)
                {
                    if (list.Count > 6000) return;
                    if (o is GeometryInstance gi) { Collect(gi.GetInstanceGeometry(), list); continue; }
                    IList<XYZ> pts = null;
                    if (o is Curve c) { try { pts = c.Tessellate(); } catch { } }
                    else if (o is PolyLine pl) pts = pl.GetCoordinates();
                    if (pts == null || pts.Count < 2) continue;
                    var arr = new double[pts.Count * 2];
                    for (int i = 0; i < pts.Count; i++) { arr[2 * i] = pts[i].X * 304.8; arr[2 * i + 1] = pts[i].Y * 304.8; }
                    list.Add(arr);
                }
            }

            public bool ChangeTitleBlock(int index, out double[] paper, out string error)
            {
                paper = null; error = null;
                if (index < 0 || index >= _tbs.Count) { error = "Carimbo inválido."; return false; }
                try
                {
                    FamilySymbol sym = _tbs[index];
                    if (!sym.IsActive) sym.Activate();
                    if (TitleBlockOf(Sheet) is FamilyInstance tbi) tbi.ChangeTypeId(sym.Id);
                    else _doc.Create.NewFamilyInstance(XYZ.Zero, sym, Sheet);
                    _doc.Regenerate();
                    paper = Paper();
                    return true;
                }
                catch (Exception ex)
                {
                    error = "Não foi possível trocar o carimbo: " + ex.Message;
                    return false;
                }
            }
        }

        /// <summary>
        /// Crop of the plan in the shape of the room: its boundary (finish faces) pushed out by the
        /// thickness of each wall that bounds it (+extra), so the plan shows the room and its walls and
        /// nothing else, whatever its shape. false = not possible (the rectangle stays).
        /// </summary>
        private static bool CropToRoom(Document doc, ViewPlan plan, Room r, double extra)
        {
            try
            {
                IList<IList<BoundarySegment>> loops = RevitUtil.Boundary(r, SpatialElementBoundaryLocation.Finish);
                if (loops == null || loops.Count == 0) return false;
                IList<BoundarySegment> outer = loops.OrderByDescending(l => LoopArea(l.Select(x => x.GetCurve()))).First();
                var curves = new List<Curve>();
                var offsets = new List<double>();
                foreach (BoundarySegment seg in outer)
                {
                    Curve c = seg.GetCurve();
                    if (c == null || c.Length < 1e-4) continue;
                    curves.Add(c);
                    double w = doc.GetElement(seg.ElementId) is Wall wall ? wall.Width : RevitUtil.M(0.05);
                    offsets.Add(w + extra);
                }
                if (curves.Count < 3) return false;
                CurveLoop loop = CurveLoop.Create(curves);
                double a0 = LoopArea(loop);
                CurveLoop grown = CurveLoop.CreateViaOffset(loop, offsets, XYZ.BasisZ);
                if (LoopArea(grown) < a0) grown = CurveLoop.CreateViaOffset(loop, offsets.Select(x => -x).ToList(), XYZ.BasisZ);
                if (LoopArea(grown) < a0) return false;
                plan.CropBoxActive = true;
                plan.GetCropRegionShapeManager().SetCropShape(grown);
                plan.CropBoxVisible = false;
                RevitUtil.SetAnnotationCrop(plan, true);
                return true;
            }
            catch { return false; }
        }

        private static void SetSheetTitle(View v, string text)
        {
            try { v.get_Parameter(BuiltInParameter.VIEW_DESCRIPTION)?.Set(text); } catch { }
        }

        /// <summary>Area of the bounding rectangle of some curves (enough to compare a loop with its offset).</summary>
        private static double LoopArea(IEnumerable<Curve> curves)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (Curve c in curves)
                foreach (XYZ p in c.Tessellate())
                {
                    minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                    minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
                }
            return minX > maxX ? 0 : (maxX - minX) * (maxY - minY);
        }

        /// <summary>The thickest wall around the room (the elevations show the side walls whole).</summary>
        private static double BoundingWallThickness(Document doc, Room r)
        {
            double t = 0;
            try
            {
                foreach (var loop in RevitUtil.Boundary(r))
                    foreach (BoundarySegment seg in loop)
                        if (doc.GetElement(seg.ElementId) is Wall w) t = Math.Max(t, w.Width);
            }
            catch { }
            return t > 0 ? t : RevitUtil.M(0.15);
        }

        /// <summary>
        /// Silhouette of a view for the sheet preview: the room outline (plan), the walls / floor /
        /// ceiling of the room seen from the side (elevation), the section box in isometric.
        /// Coordinates in the plane of the view (feet), Y up.
        /// </summary>
        private static List<double[]> Sketch(View v, Room r, BoundingBoxXYZ rb)
        {
            var list = new List<double[]>();
            if (v == null) return list;
            if (v is ViewPlan)
            {
                foreach (var loop in RevitUtil.Boundary(r))
                {
                    var pts = new List<double>();
                    foreach (BoundarySegment seg in loop)
                        foreach (XYZ p in seg.GetCurve().Tessellate()) { pts.Add(p.X); pts.Add(p.Y); }
                    if (pts.Count >= 4) { pts.Add(pts[0]); pts.Add(pts[1]); list.Add(pts.ToArray()); }
                }
                return list;
            }
            Transform inv = v.CropBox.Transform.Inverse;
            Func<XYZ, double[]> uv = p => { XYZ q = inv.OfPoint(p); return new[] { q.X, q.Y }; };
            var corners = new List<XYZ>();
            foreach (double x in new[] { rb.Min.X, rb.Max.X })
                foreach (double y in new[] { rb.Min.Y, rb.Max.Y })
                    foreach (double z in new[] { rb.Min.Z, rb.Max.Z })
                        corners.Add(new XYZ(x, y, z));
            if (v is View3D)
            {
                // the 12 edges of the room box
                for (int i = 0; i < 8; i++)
                    for (int j = i + 1; j < 8; j++)
                    {
                        XYZ a = corners[i], b = corners[j];
                        int diff = (Math.Abs(a.X - b.X) > 1e-9 ? 1 : 0) + (Math.Abs(a.Y - b.Y) > 1e-9 ? 1 : 0) + (Math.Abs(a.Z - b.Z) > 1e-9 ? 1 : 0);
                        if (diff != 1) continue;
                        double[] pa = uv(a), pb = uv(b);
                        list.Add(new[] { pa[0], pa[1], pb[0], pb[1] });
                    }
                return list;
            }
            // elevation / section: the room seen from the side (inner outline) and the floor line
            var proj = corners.Select(uv).ToList();
            double x0 = proj.Min(p => p[0]), x1 = proj.Max(p => p[0]), y0 = proj.Min(p => p[1]), y1 = proj.Max(p => p[1]);
            double t = RevitUtil.M(0.15);
            list.Add(new[] { x0, y0, x1, y0, x1, y1, x0, y1, x0, y0 });
            list.Add(new[] { x0 - t, y0, x0 - t, y1 + t, x1 + t, y1 + t, x1 + t, y0 });
            list.Add(new[] { x0 - t, y0 - t, x1 + t, y0 - t });
            return list;
        }

        /// <summary>Crop of a 3D view around the projection of its section box (+pad): without it the
        /// viewport takes the size of the whole model view.</summary>
        private static void CropIsometric(View3D v, BoundingBoxXYZ box, double pad)
        {
            try
            {
                BoundingBoxXYZ cb = v.CropBox;
                Transform tr = cb.Transform;
                Transform inv = tr.Inverse;
                double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                foreach (double x in new[] { box.Min.X, box.Max.X })
                    foreach (double y in new[] { box.Min.Y, box.Max.Y })
                        foreach (double z in new[] { box.Min.Z, box.Max.Z })
                        {
                            XYZ p = inv.OfPoint(new XYZ(x, y, z));
                            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
                        }
                v.CropBox = new BoundingBoxXYZ
                {
                    Transform = tr,
                    Min = new XYZ(minX - pad, minY - pad, cb.Min.Z),
                    Max = new XYZ(maxX + pad, maxY + pad, cb.Max.Z)
                };
                v.CropBoxActive = true;
                v.CropBoxVisible = false;
            }
            catch { }
        }

        internal static double[] ParseMargins(string text)
        {
            var parts = (text ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => double.TryParse(p.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : -1)
                .Where(v => v >= 0).ToList();
            while (parts.Count < 4) parts.Add(parts.Count == 0 ? 25 : parts.Count == 3 ? 45 : 10);
            return parts.Take(4).ToArray();
        }

    }
}
