using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDwgUnifier.Tools.Common;

namespace RevitDwgUnifier.Tools.Views
{
    /// <summary>
    /// Eixos de Paredes: creates grids on the location lines of straight walls (the selected ones,
    /// or all walls visible in the active plan). Collinear walls share one grid spanning all of
    /// them; walls on an existing grid are skipped. Grids are then numbered / lettered and
    /// dimensioned in the active plan.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class EixosDeParedesCommand : ToolCommand
    {
        public override string Title => "Eixos de Paredes";

        private sealed class Run1
        {
            public XYZ Dir, Normal;
            public double Offset;          // position across (Normal · point)
            public double From, To;        // extent along Dir
        }

        protected override Result Run()
        {
            if (!(Doc.ActiveView is ViewPlan plan))
            {
                Show("Abra uma planta e execute novamente.");
                return Result.Cancelled;
            }

            var selected = UiDoc.Selection.GetElementIds().Select(id => Doc.GetElement(id)).OfType<Wall>().ToList();
            var dlg = new OptionsDialog(Title, "EixosDeParedes");
            var fSource = dlg.Choice("source", "Paredes", new[]
            {
                selected.Count > 0 ? $"Selecionadas ({selected.Count})" : "Selecionadas (nenhuma selecionada)",
                "Todas as visíveis na vista ativa"
            }, selected.Count > 0 ? 0 : 1);
            var fMin = dlg.Number("min", "Ignorar paredes menores que (m)", 0.6, 0, 1000);
            var fExt = dlg.Number("ext", "Prolongar os eixos além das paredes (m)", 1.5, 0, 100);
            var fTol = dlg.Number("tol", "Tolerância de alinhamento (cm)", 2, 0.1, 100);
            dlg.Section("Numeração e cotas");
            var fRule = dlg.Choice("rule", "Numeração", GridTools.Rules);
            var fRenameAll = dlg.Check("renameAll", "Renumerar também os eixos já existentes", true);
            var fDim = dlg.Check("dim", "Cotar os eixos na vista ativa", true);
            if (!dlg.Show(Owner)) return Result.Cancelled;

            List<Wall> walls = fSource.Index == 0 && selected.Count > 0
                ? selected
                : new FilteredElementCollector(Doc, plan.Id).OfClass(typeof(Wall)).Cast<Wall>().ToList();
            double tol = fFeet(fTol.Value / 100.0);
            double minLen = RevitUtil.M(fMin.Value);

            // 1. Straight wall axes -> merged collinear runs.
            var runs = new List<Run1>();
            foreach (Wall w in walls)
            {
                if (!((w.Location as LocationCurve)?.Curve is Line l) || l.Length < minLen) continue;
                XYZ a = RevitUtil.Flat(l.GetEndPoint(0)), b = RevitUtil.Flat(l.GetEndPoint(1));
                XYZ d = (b - a).Normalize();
                if (d.X < -1e-9 || (Math.Abs(d.X) < 1e-9 && d.Y < 0)) d = -d;     // canonical direction
                XYZ n = new XYZ(-d.Y, d.X, 0);
                double off = n.DotProduct(a);
                double s0 = Math.Min(d.DotProduct(a), d.DotProduct(b)), s1 = Math.Max(d.DotProduct(a), d.DotProduct(b));

                Run1 run = runs.FirstOrDefault(r => r.Dir.CrossProduct(d).GetLength() < 1e-3 && Math.Abs(r.Offset - off) < tol);
                if (run == null) runs.Add(new Run1 { Dir = d, Normal = n, Offset = off, From = s0, To = s1 });
                else { run.From = Math.Min(run.From, s0); run.To = Math.Max(run.To, s1); }
            }
            if (runs.Count == 0) { Show("Nenhuma parede reta encontrada."); return Result.Cancelled; }

            // 2. Skip runs already covered by an existing grid.
            var existing = GridTools.LinearGrids(Doc);
            runs = runs.Where(r => !existing.Any(g =>
            {
                var gl = (Line)g.Curve;
                return gl.Direction.CrossProduct(r.Dir).GetLength() < 1e-3 && Math.Abs(r.Normal.DotProduct(RevitUtil.Flat(gl.GetEndPoint(0))) - r.Offset) < tol;
            })).ToList();

            var problems = new List<string>();
            int createdCount = 0, dims = 0;
            double ext = RevitUtil.M(fExt.Value);
            double z = plan.GenLevel?.ProjectElevation ?? 0;

            using (var t = new Transaction(Doc, "JA Tools - " + Title))
            {
                t.Start();
                var created = new List<Grid>();
                foreach (Run1 r in runs)
                {
                    XYZ p0 = r.Dir * (r.From - ext) + r.Normal * r.Offset + new XYZ(0, 0, z);
                    XYZ p1 = r.Dir * (r.To + ext) + r.Normal * r.Offset + new XYZ(0, 0, z);
                    try { created.Add(Grid.Create(Doc, Line.CreateBound(p0, p1))); createdCount++; }
                    catch (Exception ex) { RevitUtil.AddOnce(problems, "Eixo não criado: " + ex.Message); }
                }
                Doc.Regenerate();

                var toName = fRenameAll.Value ? GridTools.LinearGrids(Doc) : created;
                if (toName.Count > 0) GridTools.Rename(Doc, toName, fRule.Index);

                if (fDim.Value)
                {
                    Doc.Regenerate();
                    foreach (ElementId id in GridTools.GridOnlyDimensions(Doc, plan)) Doc.Delete(id);
                    dims = GridTools.DimensionGrids(Doc, plan, GridTools.LinearGrids(Doc, plan), problems);
                }
                t.Commit();
            }

            string text = $"{createdCount} eixo(s) criado(s)" + (fDim.Value ? $", {dims} cota(s)." : ".");
            if (problems.Count > 0) text += "\n\nAvisos:\n" + string.Join("\n", problems.Select(p => "  • " + p));
            Show(text);
            return Result.Succeeded;
        }

        private static double fFeet(double meters) => RevitUtil.M(meters);
    }

    /// <summary>
    /// Recalcular Eixos: after grids were added, moved or deleted, renames all of them in order
    /// and rebuilds their dimension chains in the active plan.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class RecalcularEixosCommand : ToolCommand
    {
        public override string Title => "Recalcular Eixos";

        protected override Result Run()
        {
            var grids = GridTools.LinearGrids(Doc);
            if (grids.Count == 0) { Show("O projeto não tem eixos."); return Result.Cancelled; }

            var dlg = new OptionsDialog(Title, "RecalcularEixos");
            var fRule = dlg.Choice("rule", "Numeração", GridTools.Rules);
            var fDim = dlg.Check("dim", "Refazer as cotas dos eixos na vista ativa", Doc.ActiveView is ViewPlan);
            var fOffset = dlg.Number("offset", "Distância da cota à ponta dos eixos (m)", 1.0, 0, 100);
            if (!dlg.Show(Owner)) return Result.Cancelled;

            var problems = new List<string>();
            int deleted = 0, dims = 0, renamed;
            using (var t = new Transaction(Doc, "JA Tools - " + Title))
            {
                t.Start();
                renamed = GridTools.Rename(Doc, grids, fRule.Index);
                if (fDim.Value && Doc.ActiveView is ViewPlan plan)
                {
                    Doc.Regenerate();
                    foreach (ElementId id in GridTools.GridOnlyDimensions(Doc, plan)) { Doc.Delete(id); deleted++; }
                    dims = GridTools.DimensionGrids(Doc, plan, GridTools.LinearGrids(Doc, plan), problems, fOffset.Value);
                }
                t.Commit();
            }

            string text = $"{renamed} eixo(s) renomeado(s)." + (fDim.Value ? $"\n{deleted} cota(s) antiga(s) removida(s), {dims} cota(s) criada(s)." : "");
            if (problems.Count > 0) text += "\n\nAvisos:\n" + string.Join("\n", problems.Select(p => "  • " + p));
            Show(text);
            return Result.Succeeded;
        }
    }
}
