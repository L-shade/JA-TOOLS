using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using RevitDwgUnifier.Tools.Common;

namespace RevitDwgUnifier.Tools.Dimensions
{
    /// <summary>
    /// Cotas por Ambiente: the clear interior width and length of every room of the active plan
    /// (or of the selected rooms), between the faces of the walls that bound it.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class CotasPorAmbienteCommand : DimToolCommand
    {
        public override string Title => "Cotas por Ambiente";

        protected override Result Run()
        {
            ViewPlan view = RequirePlan();
            if (view == null) return Result.Cancelled;

            var selectedRooms = UiDoc.Selection.GetElementIds().Select(id => Doc.GetElement(id)).OfType<Room>().ToList();
            var dlg = new OptionsDialog(Title, "CotasPorAmbiente");
            var fType = AddTypeChoice(dlg);
            var fWhich = dlg.Choice("which", "Ambientes", new[]
            {
                "Todos os ambientes visíveis na vista",
                selectedRooms.Count > 0 ? $"Selecionados ({selectedRooms.Count})" : "Selecionados (nenhum selecionado)"
            }, selectedRooms.Count > 0 ? 1 : 0);
            var fDirs = dlg.Choice("dirs", "Direções", new[] { "Todas as direções das paredes (anguladas também)", "Somente horizontal e vertical" }, 0,
                "Paredes a 45°, 30°, 27°, 21°, 11°... recebem a cota entre elas e a parede paralela à frente.");
            var fX = dlg.Check("x", "Cotar na horizontal (largura)", true);
            var fY = dlg.Check("y", "Cotar na vertical (comprimento)", true);
            fX.EnabledWhen(fDirs, 1);
            fY.EnabledWhen(fDirs, 1);
            var fOff = dlg.Number("offset", "Afastar as cotas do centro do ambiente (m)", 0.6, 0, 20);
            dlg.Note("A cota vai de face a face das paredes que limitam o ambiente, passando pelo seu ponto de etiqueta.");
            if (!dlg.Show(Owner)) return Result.Cancelled;

            List<Room> rooms = fWhich.Index == 1 && selectedRooms.Count > 0
                ? selectedRooms
                : new FilteredElementCollector(Doc, view.Id).OfCategory(BuiltInCategory.OST_Rooms).OfType<Room>().Where(r => r.Area > 1e-6).ToList();
            List<Wall> walls = DimEngine.WallsInView(Doc, view);
            DimensionType type = SelectedType(fType);

            using (var t = new Transaction(Doc, "JA Tools - " + Title))
            {
                t.Start();
                double z = DimEngine.ViewZ(view);
                int done = 0;
                foreach (Room r in rooms)
                {
                    ToolProgress.Report(done++, rooms.Count, $"Cotando {r.Number} - {RevitUtil.RoomName(r)}");
                    DimRoomAll(view, walls, r, RevitUtil.M(fOff.Value), z, type, fDirs.Index == 1, fX.Value, fY.Value);
                }
                t.Commit();
            }
            Report($"{rooms.Count} ambiente(s) analisado(s).");
            return Result.Succeeded;
        }
    }

    /// <summary>
    /// Cotas por Seleção: select parallel walls, then click where the dimension line goes:
    /// one continuous chain across all of them (both faces of each wall = thicknesses included).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class CotasPorSelecaoCommand : DimToolCommand
    {
        public override string Title => "Cotas por Seleção";   // 1.17: + Cotas totais (modo Fachada)
        protected override bool PicksInView => true;   // clicks in the view: no progress box

        protected override Result Run()
        {
            ViewPlan view = RequirePlan();
            if (view == null) return Result.Cancelled;

            var dlg = new OptionsDialog(Title, "CotasPorSelecao");
            var fMode = dlg.Choice("mode", "Cotar", new[] { "Corrente", "Fachada (totais)" }, 0,
                "Corrente: paredes paralelas numa corrente contínua, na posição clicada. Fachada: aberturas, paredes que chegam e total, do lado de fora.");
            var fType = AddTypeChoice(dlg);
            var fFaces = dlg.Choice("faces", "Faces das paredes", new[] { "As duas faces (com espessura)", "Somente a face voltada para a cota" });
            fFaces.VisibleWhen(fMode, 0);
            var fOpen = dlg.Check("open", "Linha 1: aberturas (portas e janelas)", true);
            var fWalls = dlg.Check("walls", "Linha 2: paredes que encontram a fachada", true);
            var fTotal = dlg.Check("total", "Linha 3: total", true);
            var fFirst = dlg.Number("first", "Distância da 1ª linha à face externa (m)", 0.6, 0, 20);
            var fStep = dlg.Number("step", "Espaçamento entre linhas (m)", 0.5, 0.05, 20);
            foreach (var f in new OptionsDialog.Field[] { fOpen, fWalls, fTotal, fFirst, fStep }) f.VisibleWhen(fMode, 1);
            dlg.Note("Depois do OK: corrente — selecione as paredes paralelas (Concluir) e clique onde a linha de cota passa; " +
                     "fachada — selecione as paredes alinhadas da fachada (Concluir); o lado externo é o da orientação da parede. Repete até Esc.");
            if (!dlg.Show(Owner)) return Result.Cancelled;
            DimensionType type = SelectedType(fType);
            if (fMode.Index == 1)
            {
                RunFacade(view, type, fOpen.Value, fWalls.Value, fTotal.Value, RevitUtil.M(fFirst.Value), RevitUtil.M(fStep.Value));
                Report();
                return Result.Succeeded;
            }

            while (true)
            {
                List<Wall> walls;
                XYZ at;
                try
                {
                    walls = UiDoc.Selection.PickObjects(ObjectType.Element, new CategorySelectionFilter(BuiltInCategory.OST_Walls),
                                "Selecione as paredes paralelas e clique em Concluir (Esc termina)")
                        .Select(rf => Doc.GetElement(rf)).OfType<Wall>().Where(w => DimEngine.WallLine(w) != null).ToList();
                    if (walls.Count == 0) break;
                    walls = DimEngine.StructureWalls(walls, out int trocados);   // a finish picked = its wall
                    if (trocados > 0) RevitUtil.AddOnce(Problems, "Revestimentos selecionados: foi cotada a estrutura da parede deles (o revestimento nunca é cotado).");
                    if (walls.Count == 0) continue;
                    at = RevitUtil.PickPoint(UiDoc, "Clique a posição da linha de cota");
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { break; }

                XYZ d = DimEngine.FlatDir(DimEngine.WallLine(walls[0]));
                XYZ u = new XYZ(-d.Y, d.X, 0);
                var parallel = walls.Where(w => Math.Abs(DimEngine.FlatDir(DimEngine.WallLine(w)).DotProduct(u)) <= DimEngine.Tol).ToList();
                if (parallel.Count < walls.Count) RevitUtil.AddOnce(Problems, "Paredes não paralelas à primeira foram ignoradas.");

                double big = RevitUtil.M(1000);
                XYZ a = RevitUtil.Flat(at) - u * big, b = RevitUtil.Flat(at) + u * big;
                var refs = new List<RefAt>();
                foreach (Wall w in parallel)
                {
                    var faces = new List<RefAt>();
                    foreach (var f in DimEngine.SideFaces(w))
                        faces.Add(new RefAt { Ref = f.Item1, Pos = (RevitUtil.Flat(f.Item2.Origin) - a).DotProduct(u), Kind = "wall" });
                    if (fFaces.Index == 1 && faces.Count > 0)
                    {
                        double here = (RevitUtil.Flat(at) - a).DotProduct(u);
                        faces = new List<RefAt> { faces.OrderBy(f => Math.Abs(f.Pos - here)).First() };
                    }
                    refs.AddRange(faces);
                }

                using (var t = new Transaction(Doc, "JA Tools - " + Title))
                {
                    t.Start();
                    Make(view, a, u, new XYZ(at.X, at.Y, DimEngine.ViewZ(view)), refs, type, "Cota");
                    t.Commit();
                }
            }
            Report();
            return Result.Succeeded;
        }

        /// <summary>Fachada (antes "Cotas totais por Seleção"): three rows outside the selected
        /// facade walls — openings, walls that meet it, overall length. Repeats until Esc.</summary>
        private void RunFacade(ViewPlan view, DimensionType type, bool rowOpen, bool rowWalls, bool rowTotal, double first, double step)
        {
            List<Wall> allWalls = DimEngine.WallsInView(Doc, view);
            while (true)
            {
                List<Wall> sel;
                try
                {
                    sel = UiDoc.Selection.PickObjects(ObjectType.Element, new CategorySelectionFilter(BuiltInCategory.OST_Walls),
                              "Selecione as paredes da fachada e clique em Concluir (Esc termina)")
                        .Select(rf => Doc.GetElement(rf)).OfType<Wall>().Where(w => DimEngine.WallLine(w) != null).ToList();
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { break; }
                if (sel.Count == 0) break;
                sel = DimEngine.StructureWalls(sel, out int trocados);       // a finish picked = its wall
                if (trocados > 0) RevitUtil.AddOnce(Problems, "Revestimentos selecionados: foi cotada a estrutura da parede deles (o revestimento nunca é cotado).");
                if (sel.Count == 0) continue;

                using (var t = new Transaction(Doc, "JA Tools - " + Title))
                {
                    t.Start();
                    Facade(view, sel, allWalls, type, rowOpen, rowWalls, rowTotal, first, step);
                    t.Commit();
                }
            }
        }

        private void Facade(View view, List<Wall> sel, List<Wall> allWalls, DimensionType type, bool rowOpen, bool rowWalls, bool rowTotal, double first, double step)
        {
            Wall w0 = sel[0];
            Line l0 = DimEngine.WallLine(w0);
            XYZ d = DimEngine.FlatDir(l0);
            XYZ n = new XYZ(w0.Orientation.X, w0.Orientation.Y, 0).Normalize();      // exterior side
            XYZ o = RevitUtil.Flat(l0.GetEndPoint(0));
            sel = sel.Where(w => Math.Abs(DimEngine.FlatDir(DimEngine.WallLine(w)).DotProduct(d)) >= 1 - DimEngine.Tol).ToList();

            // Facade extent along d and exterior face offset along n.
            double s0 = double.MaxValue, s1 = double.MinValue, ext = double.MinValue;
            foreach (Wall w in sel)
            {
                Line l = DimEngine.WallLine(w);
                foreach (XYZ p in new[] { l.GetEndPoint(0), l.GetEndPoint(1) })
                {
                    double s = (RevitUtil.Flat(p) - o).DotProduct(d);
                    s0 = Math.Min(s0, s); s1 = Math.Max(s1, s);
                }
                ext = Math.Max(ext, (RevitUtil.Flat(l.GetEndPoint(0)) - o).DotProduct(n) + w.Width / 2);
            }

            // Walls meeting the facade (perpendicular, with an end close to the facade line).
            var meeting = new List<RefAt>();
            foreach (Wall w in allWalls)
            {
                if (sel.Contains(w) || DimEngine.IsFinishWall(w)) continue;
                Line l = DimEngine.WallLine(w);
                if (Math.Abs(DimEngine.FlatDir(l).DotProduct(d)) > DimEngine.Tol) continue;
                bool touches = new[] { l.GetEndPoint(0), l.GetEndPoint(1) }.Any(p =>
                {
                    XYZ q = RevitUtil.Flat(p) - o;
                    double across = q.DotProduct(n), along = q.DotProduct(d);
                    return Math.Abs(across) <= w0.Width / 2 + w.Width / 2 + RevitUtil.M(0.3) &&
                           along >= s0 - RevitUtil.M(0.5) && along <= s1 + RevitUtil.M(0.5);
                });
                if (!touches) continue;
                foreach (var f in DimEngine.SideFaces(w))
                    meeting.Add(new RefAt { Ref = f.Item1, Pos = (RevitUtil.Flat(f.Item2.Origin) - o).DotProduct(d), Kind = "wall" });
            }

            var ends = new List<RefAt>(meeting);
            foreach (Wall w in sel) ends.AddRange(DimEngine.WallEndFaces(w, o, d));
            ends = DimEngine.Clean(ends);
            if (ends.Count < 2) { RevitUtil.AddOnce(Problems, "Extremidades da fachada não encontradas (paredes sem encontro e sem faces de topo)."); return; }
            RefAt first0 = ends.First(), last0 = ends.Last();

            var openings = new List<RefAt>();
            foreach (Wall w in sel)
                foreach (ElementId id in w.FindInserts(true, false, false, false))
                    if (Doc.GetElement(id) is FamilyInstance fi)
                        openings.AddRange(DimEngine.OpeningSides(fi, o, d));

            double z = DimEngine.ViewZ(view);
            XYZ Row(int k) => o + n * (ext + first + step * k) + new XYZ(0, 0, z);
            int row = 0;
            if (rowOpen && openings.Count > 0) Make(view, o, d, Row(row++), openings.Concat(new[] { first0, last0 }), type, "Aberturas");
            if (rowWalls && meeting.Count > 0) Make(view, o, d, Row(row++), meeting.Where(m => m.Pos >= first0.Pos - 1e-6 && m.Pos <= last0.Pos + 1e-6).Concat(new[] { first0, last0 }), type, "Paredes");
            if (rowTotal) Make(view, o, d, Row(row), new[] { first0, last0 }, type, "Total");
        }
    }

    /// <summary>Cotas por Pontos: click two points; everything the line crosses is dimensioned
    /// (wall faces and, optionally, grids). Repeat until Esc.</summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class CotasPorPontosCommand : DimToolCommand
    {
        public override string Title => "Cotas por Pontos";
        protected override bool PicksInView => true;   // clicks in the view: no progress box

        protected override Result Run()
        {
            ViewPlan view = RequirePlan();
            if (view == null) return Result.Cancelled;

            var dlg = new OptionsDialog(Title, "CotasPorPontos");
            var fType = AddTypeChoice(dlg);
            var fGrids = dlg.Check("grids", "Incluir eixos cruzados", true);
            dlg.Note("Depois do OK: clique o início e o fim da linha. Tudo o que ela cruzar é cotado. Repete até Esc.");
            if (!dlg.Show(Owner)) return Result.Cancelled;
            DimensionType type = SelectedType(fType);
            List<Wall> walls = DimEngine.WallsInView(Doc, view);
            var grids = fGrids.Value ? new FilteredElementCollector(Doc, view.Id).OfClass(typeof(Grid)).Cast<Grid>().ToList() : new List<Grid>();

            while (true)
            {
                XYZ a, b;
                try
                {
                    a = RevitUtil.PickPoint(UiDoc, "Início da linha de cota (Esc termina)");
                    b = RevitUtil.PickPoint(UiDoc, "Fim da linha de cota");
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { break; }
                XYZ fa = RevitUtil.Flat(a), fb = RevitUtil.Flat(b);
                if (fa.DistanceTo(fb) < RevitUtil.M(0.05)) continue;
                XYZ u = (fb - fa).Normalize();

                var refs = DimEngine.WallFacesAcross(walls, fa, fb);
                refs.AddRange(DimEngine.GridsAcross(grids, fa, fb));
                using (var t = new Transaction(Doc, "JA Tools - " + Title))
                {
                    t.Start();
                    if (DimEngine.Clean(refs).Count < 2) RevitUtil.AddOnce(Problems, "Uma linha não cruzou pelo menos duas faces/eixos perpendiculares a ela.");
                    else Make(view, fa, u, new XYZ(fa.X, fa.Y, DimEngine.ViewZ(view)), refs, type, "Cota");
                    t.Commit();
                }
            }
            Report();
            return Result.Succeeded;
        }
    }

    /// <summary>Cotas por Parede: click a wall; a chain is created through the clicked point,
    /// across every parallel wall found in that direction. Repeat until Esc.</summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class CotasPorParedeCommand : DimToolCommand
    {
        public override string Title => "Cotas por Parede";
        protected override bool PicksInView => true;   // clicks in the view: no progress box

        protected override Result Run()
        {
            ViewPlan view = RequirePlan();
            if (view == null) return Result.Cancelled;

            var dlg = new OptionsDialog(Title, "CotasPorParede");
            var fType = AddTypeChoice(dlg);
            var fReach = dlg.Number("reach", "Procurar paredes até (m) de cada lado", 100, 1, 10000);
            var fGrids = dlg.Check("grids", "Incluir eixos cruzados", false);
            dlg.Note("Depois do OK: clique numa parede, no ponto por onde a cota deve passar. Repete até Esc.");
            if (!dlg.Show(Owner)) return Result.Cancelled;
            DimensionType type = SelectedType(fType);
            List<Wall> walls = DimEngine.WallsInView(Doc, view);
            var grids = fGrids.Value ? new FilteredElementCollector(Doc, view.Id).OfClass(typeof(Grid)).Cast<Grid>().ToList() : new List<Grid>();

            while (true)
            {
                Reference picked;
                try
                {
                    picked = UiDoc.Selection.PickObject(ObjectType.Element, new CategorySelectionFilter(BuiltInCategory.OST_Walls),
                                 "Clique numa parede (Esc termina)");
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { break; }

                if (!(Doc.GetElement(picked) is Wall w) || DimEngine.WallLine(w) == null) continue;
                XYZ d = DimEngine.FlatDir(DimEngine.WallLine(w));
                XYZ u = new XYZ(-d.Y, d.X, 0);
                XYZ p = RevitUtil.Flat(picked.GlobalPoint ?? RevitUtil.Center(w));
                double reach = RevitUtil.M(fReach.Value);
                XYZ a = p - u * reach, b = p + u * reach;

                var refs = DimEngine.WallFacesAcross(walls, a, b);
                refs.AddRange(DimEngine.GridsAcross(grids, a, b));
                using (var t = new Transaction(Doc, "JA Tools - " + Title))
                {
                    t.Start();
                    Make(view, a, u, new XYZ(p.X, p.Y, DimEngine.ViewZ(view)), refs, type, "Cota");
                    t.Commit();
                }
            }
            Report();
            return Result.Succeeded;
        }
    }
}
