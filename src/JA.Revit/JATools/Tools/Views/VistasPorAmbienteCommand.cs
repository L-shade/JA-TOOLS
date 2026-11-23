using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using RevitDwgUnifier.Infrastructure;
using RevitDwgUnifier.Tools.Common;

namespace RevitDwgUnifier.Tools.Views
{
    /// <summary>
    /// Vistas por Ambiente (1.17): planta, elevações internas e isométrico de cada ambiente numa
    /// janela só (antes eram três botões com três janelas e três passadas no modelo).
    /// Fast path of 1.16 kept: one plan per level (cached), every view made first, ONE regenerate,
    /// then all crops and tags. Elevations: sides A–D, marker turned to the walls, crop from the
    /// real outline. Names with tokens; existing views: create more / only missing / replace.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class VistasPorAmbienteCommand : RoomToolCommand
    {
        public override string Title => "Vistas por Ambiente";
        protected override bool PicksInView => true;   // own window, own progress

        private static readonly XYZ[] Olho = { new XYZ(1, -1, 1), new XYZ(-1, -1, 1), new XYZ(1, 1, 1), new XYZ(-1, 1, 1) };

        private sealed class Feita
        {
            public Room Room;
            public View View;
            public string Nome;
            public LadoParede Lado;
        }

        private static readonly Autodesk.Revit.DB.ViewType[] Tipos =
            { Autodesk.Revit.DB.ViewType.FloorPlan, Autodesk.Revit.DB.ViewType.Elevation, Autodesk.Revit.DB.ViewType.ThreeD };

        protected override Result Run()
        {
            var types = new FilteredElementCollector(Doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .Where(t => t.ViewFamily == ViewFamily.Elevation).OrderBy(t => t.Name).ToList();
            var tplElev = RevitUtil.Templates(Doc, Autodesk.Revit.DB.ViewType.Elevation);
            var tplPlan = RevitUtil.Templates(Doc, Autodesk.Revit.DB.ViewType.FloorPlan);
            var tpl3D = RevitUtil.Templates(Doc, Autodesk.Revit.DB.ViewType.ThreeD);
            AllRooms = RevitUtil.Rooms(Doc);
            if (AllRooms.Count == 0) { Show("O projeto não tem ambientes colocados."); return Result.Cancelled; }

            // ---- the list: rooms, their level, how many views already carry their name
            var porNome = new Dictionary<string, List<View>>(StringComparer.OrdinalIgnoreCase);
            foreach (View v in new FilteredElementCollector(Doc).OfClass(typeof(View)).Cast<View>()
                         .Where(x => !x.IsTemplate && Tipos.Contains(x.ViewType)))
            {
                if (!porNome.TryGetValue(v.Name, out var l)) porNome[v.Name] = l = new List<View>();
                l.Add(v);
            }
            var nomes = porNome.Keys.ToList();
            var pre = new HashSet<ElementId>(UiDoc.Selection.GetElementIds());
            var itens = new List<AmbItem>();
            var porId = new Dictionary<ElementId, Room>();
            foreach (Room r in AllRooms)
            {
                var it = new AmbItem { Id = r.Id, Numero = r.Number ?? "", Nome = RevitUtil.RoomName(r), Nivel = r.Level?.Name ?? "—", Selecionado = pre.Contains(r.Id) };
                string t = RevitUtil.Sanitize(it.Titulo);
                it.Existentes = nomes.Count(n => n.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);
                itens.Add(it);
                porId[r.Id] = r;
            }

            var titleBlocks = new FilteredElementCollector(Doc).OfCategory(BuiltInCategory.OST_TitleBlocks).OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>().OrderBy(s => s.FamilyName).ThenBy(s => s.Name).ToList();
            var window = new VistasAmbienteWindow(itens, types.Select(t => t.Name).ToList(), RevitUtil.TemplateChoices(tplElev),
                                                  RevitUtil.TemplateChoices(tplPlan), RevitUtil.TemplateChoices(tpl3D),
                                                  titleBlocks.Select(s => s.FamilyName + " : " + s.Name).ToList());
            new WindowInteropHelper(window).Owner = Owner;
            if (window.ShowDialog() != true || window.Pedido == null) return Result.Cancelled;
            VistasAmbPedido p = window.Pedido;
            if (p.Elevacoes && types.Count == 0) { Show("O projeto não tem tipo de vista de elevação."); return Result.Cancelled; }

            ElementId elevTypeId = p.Elevacoes ? types[Math.Min(p.Tipo, types.Count - 1)].Id : ElementId.InvalidElementId;
            // the sides of each room: one per wall direction, parallel to the walls whatever their angle
            // (A top, B right, C bottom, D left; angled walls get their own: E, F…), or the four sides
            // square to the project when "Paralelas às paredes" is off
            var ladosDe = new Dictionary<AmbItem, List<LadoParede>>();
            foreach (AmbItem it in p.Ambientes)
            {
                var ls = new List<LadoParede>();
                if (p.Elevacoes)
                {
                    Room r = porId[(ElementId)it.Id];
                    try { ls = p.Alinhar ? ParedesAmbiente.Lados(r, RevitUtil.M(0.6)) : ParedesAmbiente.LadosOrtogonais(r); }
                    catch (Exception ex) { Logger.Info("[JA Tools] Lados de " + it.Titulo + ": " + ex.Message); }
                    if (ls.Count == 0) ls = ParedesAmbiente.LadosOrtogonais(r);
                    ls = ls.Where(l => p.Lados[l.Quadrante]).ToList();
                }
                ladosDe[it] = ls;
            }

            // the names each room will get (kind, side) -> name
            var alvo = new Dictionary<AmbItem, List<(int kind, LadoParede lado, string nome)>>();
            foreach (AmbItem it in p.Ambientes)
            {
                var l = new List<(int, LadoParede, string)>();
                if (p.Planta) l.Add((0, null, VistasAmbienteWindow.Nomear(p.PlantaNome, "PLANTA - {ambiente}", it, "")));
                foreach (LadoParede ld in ladosDe[it]) l.Add((1, ld, VistasAmbienteWindow.Nomear(p.Nome, "{ambiente} - ELEVAÇÃO {letra}", it, ld.Letra)));
                if (p.Iso) l.Add((2, null, VistasAmbienteWindow.Nomear(p.IsoNome, "ISOMÉTRICO - {ambiente}", it, "")));
                alvo[it] = l;
            }

            ToolProgress.Setup("Criando " + p.Quantidade + " vista(s)", new[] { "Preparar", "Criar vistas", "Recortar e etiquetar", "Concluir" }, cancellable: true);
            var feitas = new List<Feita>();
            var plantas = new List<Feita>();
            var problems = new List<string>();
            int puladas = 0, substituidas = 0;

            using (var t = new Transaction(Doc, "JA Tools - " + Title))
            {
                t.Start();
                RevitDwgUnifier.Export.FailureSwallower.Attach(t);

                // ---- 1. prepare
                ToolProgress.Stage(0, "Lendo o projeto…");
                var planos = new Dictionary<ElementId, ViewPlan>();
                ElementId planTypeId = p.Planta ? RevitUtil.FamilyType(Doc, ViewFamily.FloorPlan).Id : ElementId.InvalidElementId;
                ElementId isoTypeId = p.Iso ? RevitUtil.FamilyType(Doc, ViewFamily.ThreeDimensional).Id : ElementId.InvalidElementId;
                ElementId tplP = RevitUtil.TemplateFromChoice(tplPlan, p.PlantaModelo);
                ElementId tplE = RevitUtil.TemplateFromChoice(tplElev, p.Modelo);
                ElementId tpl3 = RevitUtil.TemplateFromChoice(tpl3D, p.IsoModelo);

                if (p.Existentes == 2)
                    substituidas = Substituir(alvo.Values.SelectMany(l => l.Select(x => x.nome)), porNome, problems);

                // ---- 2. every view (no regenerate in the loop)
                int n = 0, total = p.Ambientes.Count;
                foreach (AmbItem it in p.Ambientes)
                {
                    if (ToolProgress.Cancelled) { t.RollBack(); return Result.Cancelled; }
                    ToolProgress.Stage(1, it.Titulo);
                    ToolProgress.Report(n++, total, it.Titulo);
                    Room r = porId[(ElementId)it.Id];

                    var faltam = alvo[it].Where(x => p.Existentes != 1 || !porNome.ContainsKey(RevitUtil.Sanitize(x.nome))).ToList();
                    puladas += alvo[it].Count - faltam.Count;
                    if (faltam.Count == 0) continue;

                    BoundingBoxXYZ rb = RevitUtil.RoomBox(r);
                    XYZ pt = RevitUtil.RoomPoint(r);
                    if (rb == null || pt == null || r.Level == null) { RevitUtil.AddOnce(problems, $"{it.Titulo}: sem contorno."); continue; }

                    // planta
                    foreach (var x in faltam.Where(y => y.kind == 0))
                        try
                        {
                            ViewPlan v = ViewPlan.Create(Doc, planTypeId, r.LevelId);
                            RevitUtil.Configure(v, p.Escala, tplP);
                            RevitUtil.Rename(v, x.nome);
                            var f = new Feita { Room = r, View = v, Nome = it.Titulo + " planta" };
                            feitas.Add(f);
                            plantas.Add(f);
                        }
                        catch (Exception ex) { RevitUtil.AddOnce(problems, $"{it.Titulo} planta: {ex.Message}"); }

                    // elevações
                    var elev = faltam.Where(y => y.kind == 1).ToList();
                    if (elev.Count > 0)
                    {
                        if (!planos.TryGetValue(r.LevelId, out ViewPlan plan)) planos[r.LevelId] = plan = RevitUtil.PlanOf(Doc, r.Level);
                        XYZ center = new XYZ((rb.Min.X + rb.Max.X) / 2, (rb.Min.Y + rb.Max.Y) / 2, rb.Min.Z);
                        // the room point is always inside; the box centre may not be (L-shapes)
                        if (!r.IsPointInRoom(new XYZ(center.X, center.Y, rb.Min.Z + RevitUtil.M(1)))) center = new XYZ(pt.X, pt.Y, rb.Min.Z);
                        try
                        {
                            // each elevation faces its wall: markers turned to the walls' angles
                            var criadas = ParedesAmbiente.CriarElevacoes(Doc, elevTypeId, plan.Id, center, p.Escala, elev.Select(x => x.lado), problems, it.Titulo);
                            foreach (var (ld, ev) in criadas)
                            {
                                var x = elev.First(y => y.lado == ld);
                                RevitUtil.Configure(ev, p.Escala, tplE);
                                RevitUtil.Rename(ev, x.nome);
                                feitas.Add(new Feita { Room = r, View = ev, Nome = it.Titulo + " " + ld.Letra, Lado = ld });
                            }
                        }
                        catch (Exception ex) { RevitUtil.AddOnce(problems, $"{it.Titulo}: {ex.Message}"); }
                    }

                    // isométrico (no regenerate needed: section box + orientation)
                    foreach (var x in faltam.Where(y => y.kind == 2))
                        try { feitas.Add(new Feita { Room = r, View = Isometrico(r, rb, isoTypeId, tpl3, x.nome, p, p.Alinhar ? ParedesAmbiente.AnguloDominante(r) : 0, problems), Nome = it.Titulo + " 3D" }); }
                        catch (Exception ex) { RevitUtil.AddOnce(problems, $"{it.Titulo} isométrico: {ex.Message}"); }
                }

                // ---- 3. one regenerate, then every crop and tag
                ToolProgress.Stage(2, "Ajustando recortes e etiquetas…");
                Doc.Regenerate();
                double mElev = RevitUtil.M(p.Margem), mPlan = RevitUtil.M(p.PlantaMargem);
                for (int i = 0; i < feitas.Count; i++)
                {
                    if (ToolProgress.Cancelled) { t.RollBack(); return Result.Cancelled; }
                    Feita f = feitas[i];
                    if (i % 4 == 0) ToolProgress.Report(i, feitas.Count, f.Nome);
                    try
                    {
                        if (f.View is ViewSection ev && f.Lado != null)
                        {
                            BoundingBoxXYZ box = RevitUtil.RoomBox(f.Room);
                            ParedesAmbiente.Recortar(ev, f.Lado, box.Min.Z, box.Max.Z, mElev, p.MostrarRecorte);
                        }
                        else if (f.View is ViewPlan vp)
                        {
                            BoundingBoxXYZ box = RevitUtil.RoomBox(f.Room);
                            RevitUtil.CropPlan(vp, box.Min.X - mPlan, box.Min.Y - mPlan, box.Max.X + mPlan, box.Max.Y + mPlan, p.MostrarRecorte);
                            // turned with the room: walls square on the sheet, whatever their angle
                            double ang = p.Alinhar ? ParedesAmbiente.AnguloDominante(f.Room) : 0;
                            if (Math.Abs(ang) > 1e-6 && !ParedesAmbiente.GirarPlanta(Doc, vp, ang, ParedesAmbiente.Contorno(f.Room), mPlan, p.MostrarRecorte))
                                RevitUtil.AddOnce(problems, $"{f.Nome}: o Revit não deixou girar o recorte; a planta ficou na direção do projeto.");
                            if (p.PlantaEtiqueta) RevitUtil.TagRooms(Doc, vp, new[] { f.Room }, problems);
                        }
                    }
                    catch (Exception ex) { RevitUtil.AddOnce(problems, $"{f.Nome}: recorte não aplicado ({ex.Message})."); }
                }

                ToolProgress.Stage(3, "Gravando…");
                t.Commit();
            }

            ToolProgress.End();
            var views = feitas.Where(f => f.View != null).Select(f => f.View).ToList();
            var pranchas = p.Prancha && titleBlocks.Count > 0 && views.Count > 0
                ? Pranchas(feitas, p, titleBlocks[Math.Max(0, Math.Min(titleBlocks.Count - 1, p.Folha))], problems)
                : new List<ViewSheet>();
            if (puladas > 0) problems.Insert(0, $"{puladas} vista(s) já existiam e foram mantidas.");
            if (substituidas > 0) problems.Insert(0, $"{substituidas} vista(s) antiga(s) substituída(s).");
            if (pranchas.Count > 0)
                problems.Insert(0, $"{pranchas.Count} prancha(s) montada(s): {string.Join(", ", pranchas.Take(10).Select(s => s.SheetNumber))}{(pranchas.Count > 10 ? "…" : "")}. Para cotar e etiquetar, use Anotar Vista ou Detalhar Ambientes.");
            Report(views, problems);
            return Result.Succeeded;
        }

        /// <summary>
        /// One new sheet per room with its new views (plan, elevations A–D, isometric), placed by the
        /// first-fit layout of Detalhar Ambientes inside the margins. Its own transaction.
        /// </summary>
        private List<ViewSheet> Pranchas(List<Feita> feitas, VistasAmbPedido p, FamilySymbol tb, List<string> problems)
        {
            var res = new List<ViewSheet>();
            double[] mm = Detail.DetalharAmbientesCommand.ParseMargins(p.Margens);
            var usados = new HashSet<string>(new FilteredElementCollector(Doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Select(s => s.SheetNumber), StringComparer.OrdinalIgnoreCase);
            string numero = p.NumeroPrancha;
            var porItem = p.Ambientes.ToDictionary(a => (ElementId)a.Id);
            using (var t = new Transaction(Doc, "JA Tools - Pranchas por ambiente"))
            {
                t.Start();
                RevitDwgUnifier.Export.FailureSwallower.Attach(t);
                if (!tb.IsActive) tb.Activate();
                foreach (var g in feitas.Where(f => f.View != null).GroupBy(f => f.Room.Id))
                {
                    try
                    {
                        Room r = g.First().Room;
                        AmbItem it = porItem.TryGetValue(r.Id, out var a) ? a : new AmbItem { Numero = r.Number ?? "", Nome = RevitUtil.RoomName(r), Nivel = r.Level?.Name ?? "" };
                        ViewSheet sheet = ViewSheet.Create(Doc, tb.Id);
                        while (usados.Contains(numero)) numero = LegendEngine.NextNumber(numero);
                        try { sheet.SheetNumber = numero; } catch { }
                        usados.Add(numero);
                        numero = LegendEngine.NextNumber(numero);
                        try { sheet.Name = VistasAmbienteWindow.Nomear(p.NomePrancha, "{ambiente}", it, ""); } catch { }
                        // plan first, then elevations A..D, then the isometric
                        var ordem = g.OrderBy(f => f.View is ViewPlan ? 0 : f.Lado != null ? 1 : 2).ThenBy(f => f.Lado?.Letra ?? "").ToList();
                        Doc.Regenerate();
                        var vps = new List<Viewport>();
                        var keys = new List<string>();
                        foreach (Feita f in ordem)
                        {
                            try
                            {
                                if (!Viewport.CanAddViewToSheet(Doc, sheet.Id, f.View.Id)) continue;
                                vps.Add(Viewport.Create(Doc, sheet.Id, f.View.Id, XYZ.Zero));
                                keys.Add(f.View is ViewPlan ? "P" : f.Lado != null ? "E" + Math.Max(0, "ABCD".IndexOf(f.Lado.Letra ?? "A", StringComparison.Ordinal)) : "I");
                            }
                            catch (Exception ex) { RevitUtil.AddOnce(problems, $"{f.View.Name}: não colocada na prancha ({ex.Message})."); }
                        }
                        Doc.Regenerate();
                        var host = new Detail.DetalharAmbientesCommand.SheetHost(Doc, sheet, sheet.Id, vps, new List<ViewSheet> { sheet }, new List<FamilySymbol> { tb });
                        List<Detail.LayoutBox> boxes = host.FreeBoxes(keys);
                        double[] usable = Detail.DetalharAmbientesCommand.Usable(host.Paper(), mm);
                        Detail.LayoutEngine.Pack(boxes, usable[0], usable[1], usable[2], usable[3], 6);
                        int fora = 0;
                        foreach (Detail.LayoutBox b in boxes.Where(x => x.Index >= 0 && x.Index < vps.Count))
                        {
                            try { vps[b.Index].SetBoxCenter(new XYZ(b.X / 304.8, b.Y / 304.8, 0)); } catch { }
                            if (!b.Inside(usable[0], usable[1], usable[2], usable[3])) fora++;
                        }
                        if (fora > 0) RevitUtil.AddOnce(problems, $"Prancha {sheet.SheetNumber}: {fora} vista(s) fora da área útil (escala menor ou folha maior).");
                        res.Add(sheet);
                    }
                    catch (Exception ex) { RevitUtil.AddOnce(problems, "Prancha não criada: " + ex.Message); }
                }
                t.Commit();
            }
            return res;
        }

        /// <summary>3D isometric of the room, section box opened at the cut height.</summary>
        private View3D Isometrico(Room r, BoundingBoxXYZ rb, ElementId typeId, ElementId tpl, string nome, VistasAmbPedido p, double ang, List<string> problems)
        {
            double m = RevitUtil.M(p.IsoMargem);
            double top = p.IsoCorte > 0 ? rb.Min.Z + RevitUtil.M(p.IsoCorte) : rb.Max.Z;
            // section box square to the walls (turned with the room)
            Transform giro = Transform.CreateRotation(XYZ.BasisZ, ang);
            Transform inv = giro.Inverse;
            var pts = ParedesAmbiente.Contorno(r);
            if (pts.Count == 0) pts = new List<XYZ> { rb.Min, rb.Max, new XYZ(rb.Min.X, rb.Max.Y, 0), new XYZ(rb.Max.X, rb.Min.Y, 0) };
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (XYZ q in pts)
            {
                XYZ l = inv.OfPoint(new XYZ(q.X, q.Y, 0));
                minX = Math.Min(minX, l.X); maxX = Math.Max(maxX, l.X);
                minY = Math.Min(minY, l.Y); maxY = Math.Max(maxY, l.Y);
            }
            var box = new BoundingBoxXYZ
            {
                Transform = giro,
                Min = new XYZ(minX - m, minY - m, rb.Min.Z - RevitUtil.M(0.2)),
                Max = new XYZ(maxX + m, maxY + m, top)
            };
            View3D v = View3D.CreateIsometric(Doc, typeId);
            RevitUtil.Configure(v, 0, tpl);
            RevitUtil.Rename(v, nome);
            v.SetSectionBox(box);
            try { v.IsSectionBoxActive = true; } catch { }
            XYZ center = giro.OfPoint((box.Min + box.Max) / 2);
            XYZ eyeDir = ParedesAmbiente.Girar(Olho[Math.Max(0, Math.Min(3, p.IsoDirecao))], ang).Normalize();
            XYZ forward = -eyeDir;
            XYZ up = (XYZ.BasisZ - forward * XYZ.BasisZ.DotProduct(forward)).Normalize();
            try { v.SetOrientation(new ViewOrientation3D(center + eyeDir * RevitUtil.M(50), up, forward)); }
            catch (Exception ex) { RevitUtil.AddOnce(problems, "Orientação: " + ex.Message); }
            try { v.SetCategoryHidden(new ElementId(BuiltInCategory.OST_SectionBox), true); } catch { }
            return v;
        }

        // ================================================================== replace

        /// <summary>Deletes the views named as the new ones will be, and the elevation markers left empty.</summary>
        private int Substituir(IEnumerable<string> nomes, Dictionary<string, List<View>> porNome, List<string> problems)
        {
            var apagar = new HashSet<ElementId>();
            foreach (string n0 in nomes)
            {
                string nome = RevitUtil.Sanitize(n0);
                if (porNome.TryGetValue(nome, out var vs)) foreach (View v in vs) apagar.Add(v.Id);
                porNome.Remove(nome);
            }
            if (apagar.Count == 0) return 0;

            var marcadores = new FilteredElementCollector(Doc).OfClass(typeof(ElevationMarker)).Cast<ElevationMarker>()
                .Where(m => Enumerable.Range(0, 4).Any(i => { try { return apagar.Contains(m.GetViewId(i)); } catch { return false; } }))
                .Select(m => m.Id).ToList();
            int n = 0;
            foreach (ElementId id in apagar)
            {
                try { Doc.Delete(id); n++; }
                catch (Exception ex) { RevitUtil.AddOnce(problems, "Não foi possível apagar uma vista antiga: " + ex.Message); }
            }
            foreach (ElementId id in marcadores)
            {
                try { if (Doc.GetElement(id) is ElevationMarker m && m.CurrentViewCount == 0) Doc.Delete(id); }
                catch { }
            }
            SessionCache.Remove("viewnames|" + Doc.GetHashCode());   // the freed names can be used again
            return n;
        }
    }
}
