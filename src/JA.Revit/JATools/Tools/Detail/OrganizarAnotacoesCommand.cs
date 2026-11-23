using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using RevitDwgUnifier.Tools.Common;
using RevitDwgUnifier.Tools.Dimensions;

namespace RevitDwgUnifier.Tools.Detail
{
    // =====================================================================================
    //  Organizar Anotações (1.17 = Ajustar Anotações + Alinhar Anotações)
    // =====================================================================================

    /// <summary>
    /// Organizar Anotações: "Sobreposições" moves the overlapping tags, texts and dimension values
    /// of the active view to the nearest free place (all of them or only the selected ones);
    /// "Alinhar" / "Distribuir" line up or space evenly the selected tags, room tags and texts.
    /// With two or more annotations selected it opens on "Alinhar"; otherwise on "Sobreposições".
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class OrganizarAnotacoesCommand : ToolCommand
    {
        public override string Title => "Organizar Anotações";

        private sealed class Box
        {
            public Element E;
            public double L, R, B, T;
        }

        private static readonly string[] Alinhamentos = { "Esquerda", "Centro (vertical)", "Direita", "Topo", "Meio (horizontal)", "Base" };

        protected override Result Run()
        {
            View view = Doc.ActiveView;
            if (view == null || view.IsTemplate || view is ViewSheet || view is View3D)
            {
                Show("Abra uma planta, corte, elevação ou vista de desenho e execute novamente.");
                return Result.Cancelled;
            }
            var items = UiDoc.Selection.GetElementIds().Select(id => Doc.GetElement(id))
                .Where(e => e is IndependentTag || e is SpatialElementTag || e is TextNote).ToList();

            var dlg = new OptionsDialog(Title, "OrganizarAnotacoes", "Organizar");
            var fWhat = dlg.Choice("what", "Organizar", new[] { "Sobreposições", "Alinhar", "Distribuir" }, items.Count >= 2 ? 1 : 0);
            if (items.Count >= 2 && fWhat.Index == 0) fWhat.Index = 1;   // a selection means "line these up"
            var fScope = dlg.Choice("scope", "Ajustar", new[] { "Todas da vista", $"Só as selecionadas ({items.Count})" }, items.Count > 0 ? 1 : 0);
            fScope.VisibleWhen(fWhat, 0);
            fScope.EnabledWhen(() => items.Count > 0);
            var fAlign = dlg.Choice("align", "Alinhar por", Alinhamentos);
            fAlign.VisibleWhen(fWhat, 1);
            var fRef = dlg.Choice("ref", "Referência", new[] { "A mais externa", "A primeira selecionada" });
            fRef.VisibleWhen(fWhat, 1);
            var fDist = dlg.Choice("dist", "Distribuir", new[] { "Na horizontal", "Na vertical" });
            fDist.VisibleWhen(fWhat, 2);
            dlg.Note(items.Count > 0
                ? $"{items.Count} anotação(ões) selecionada(s). As linhas de chamada continuam presas aos elementos."
                : "Alinhar e Distribuir usam as etiquetas e textos selecionados (dois ou mais). Sobreposições vale para a vista toda.");
            if (!dlg.Show(Owner)) return Result.Cancelled;

            if (fWhat.Index == 0) return Sobreposicoes(view, fScope.Index == 1 && items.Count > 0 ? items.Select(e => e.Id).ToList() : null);
            if (items.Count < 2)
            {
                Show("Selecione duas ou mais etiquetas ou textos na vista e execute novamente.");
                return Result.Cancelled;
            }
            // 0..5 = align (left, centre, right, top, middle, bottom); 6/7 = distribute (horizontal / vertical)
            int mode = fWhat.Index == 1 ? Math.Max(0, fAlign.Index) : 6 + Math.Max(0, fDist.Index);
            return Alinhar(view, items, mode, fRef.Index == 1);
        }

        private Result Sobreposicoes(View view, ICollection<ElementId> only)
        {
            AnnotationAdjuster.Result r;
            using (var t = new Transaction(Doc, "JA Tools - " + Title))
            {
                t.Start();
                r = AnnotationAdjuster.Run(Doc, view, only);
                t.Commit();
            }
            Show(r.Overlapping == 0
                ? $"Nenhuma sobreposição entre as {r.Checked} anotação(ões) da vista."
                : $"{r.Overlapping} sobreposição(ões) encontradas; {r.Moved} anotação(ões) ajustadas." +
                  (r.Unresolved > 0 ? $"\n{r.Unresolved} sem lugar livre por perto: ajuste à mão." : ""));
            return Result.Succeeded;
        }

        private Result Alinhar(View view, List<Element> items, int m, bool useFirst)
        {
            XYZ right = view.RightDirection, up = view.UpDirection;
            var boxes = new List<Box>();
            foreach (Element e in items)
            {
                BoundingBoxXYZ b = e.get_BoundingBox(view);
                if (b == null) continue;
                var corners = new[] { b.Min, b.Max, new XYZ(b.Min.X, b.Max.Y, b.Min.Z), new XYZ(b.Max.X, b.Min.Y, b.Min.Z) };
                boxes.Add(new Box
                {
                    E = e,
                    L = corners.Min(p => p.DotProduct(right)), R = corners.Max(p => p.DotProduct(right)),
                    B = corners.Min(p => p.DotProduct(up)), T = corners.Max(p => p.DotProduct(up))
                });
            }
            if (boxes.Count < 2) { Show("As anotações selecionadas não têm extensão nesta vista."); return Result.Cancelled; }

            int moved = 0;
            using (var t = new Transaction(Doc, "JA Tools - " + Title))
            {
                t.Start();
                var first = boxes[0];
                if (m <= 5)
                {
                    double target;
                    switch (m)
                    {
                        case 0: target = useFirst ? first.L : boxes.Min(b => b.L); break;
                        case 1: target = useFirst ? (first.L + first.R) / 2 : boxes.Average(b => (b.L + b.R) / 2); break;
                        case 2: target = useFirst ? first.R : boxes.Max(b => b.R); break;
                        case 3: target = useFirst ? first.T : boxes.Max(b => b.T); break;
                        case 4: target = useFirst ? (first.B + first.T) / 2 : boxes.Average(b => (b.B + b.T) / 2); break;
                        default: target = useFirst ? first.B : boxes.Min(b => b.B); break;
                    }
                    foreach (var b in boxes)
                    {
                        double cur = m == 0 ? b.L : m == 1 ? (b.L + b.R) / 2 : m == 2 ? b.R : m == 3 ? b.T : m == 4 ? (b.B + b.T) / 2 : b.B;
                        double d = target - cur;
                        if (Math.Abs(d) < 1e-6) continue;
                        AnnotationAdjuster.MoveAnnotation(Doc, b.E, (m <= 2 ? right : up) * d, false);
                        moved++;
                    }
                }
                else
                {
                    bool horiz = m == 6;
                    var ordered = horiz ? boxes.OrderBy(b => b.L).ToList() : boxes.OrderByDescending(b => b.T).ToList();
                    double span = horiz ? ordered.Last().R - ordered.First().L : ordered.First().T - ordered.Last().B;
                    double sizes = ordered.Sum(b => horiz ? b.R - b.L : b.T - b.B);
                    double gap = (span - sizes) / Math.Max(1, ordered.Count - 1);
                    double pos = horiz ? ordered.First().L : ordered.First().T;
                    foreach (var b in ordered)
                    {
                        double cur = horiz ? b.L : b.T;
                        double d = pos - cur;
                        if (Math.Abs(d) > 1e-6) { AnnotationAdjuster.MoveAnnotation(Doc, b.E, (horiz ? right : up) * d, false); moved++; }
                        pos += horiz ? (b.R - b.L) + gap : -((b.T - b.B) + gap);
                    }
                }
                t.Commit();
            }
            Show($"{moved} anotação(ões) movida(s).");
            return Result.Succeeded;
        }
    }
}
