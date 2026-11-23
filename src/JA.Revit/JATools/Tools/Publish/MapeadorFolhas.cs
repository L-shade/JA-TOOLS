using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDwgUnifier.Tools.Common;
using RevitDwgUnifier.Tools.Detail;
using RevitDwgUnifier.Tools.Views;

namespace RevitDwgUnifier.Tools.Publish
{
    /// <summary>
    /// Mapeador de Folhas (reference: Glyph's Sheet Mapper): takes the views that are not on any
    /// sheet, groups them (one per view, N per sheet, or as many as fit) and creates the sheets with
    /// the chosen title block, numbering and names; the views are packed inside the free area of the
    /// title block (rows from the top left), each one keeping its scale.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class MapeadorFolhasCommand : SheetToolCommand
    {
        public override string Title => "Mapeador de Folhas";
        protected override bool PicksInView => true;

        private static readonly Dictionary<ViewType, (int ordem, string nome)> Tipos = new Dictionary<ViewType, (int, string)>
        {
            [ViewType.FloorPlan] = (0, "Planta"),
            [ViewType.CeilingPlan] = (1, "Forro"),
            [ViewType.AreaPlan] = (2, "Planta de área"),
            [ViewType.EngineeringPlan] = (3, "Planta estrutural"),
            [ViewType.Section] = (4, "Corte"),
            [ViewType.Elevation] = (5, "Elevação"),
            [ViewType.Detail] = (6, "Detalhe"),
            [ViewType.ThreeD] = (7, "3D"),
            [ViewType.DraftingView] = (8, "Desenho"),
            [ViewType.Legend] = (9, "Legenda"),
        };

        protected override Result Run()
        {
            ToolProgress.End();
            var naFolha = new HashSet<ElementId>(new FilteredElementCollector(Doc).OfClass(typeof(Viewport)).Cast<Viewport>().Select(vp => vp.ViewId));
            var selecao = new HashSet<ElementId>(UiDoc.Selection.GetElementIds());
            var vistas = new List<MapVista>();
            foreach (View v in new FilteredElementCollector(Doc).OfClass(typeof(View)).Cast<View>())
            {
                if (v.IsTemplate || !Tipos.TryGetValue(v.ViewType, out var tipo) || naFolha.Contains(v.Id)) continue;
                try { if (!v.CanBePrinted) continue; } catch { continue; }
                double w = 0, h = 0;
                try
                {
                    BoundingBoxUV o = v.Outline;
                    w = (o.Max.U - o.Min.U) * 304.8; h = (o.Max.V - o.Min.V) * 304.8;
                }
                catch { }
                if (w <= 1 || h <= 1 || w > 5000 || h > 5000) { w = 120; h = 90; }
                int escala = 0;
                try { escala = v.Scale; } catch { }
                string nivel = "";
                try { nivel = v.GenLevel?.Name ?? ""; } catch { }
                vistas.Add(new MapVista
                {
                    Id = v.Id.Value, Nome = v.Name, Tipo = tipo.nome, Ordem = tipo.ordem, Nivel = nivel, Escala = escala, WMm = w, HMm = h,
                    Marcada = selecao.Contains(v.Id) || (Automacao.NomesEscopo != null && Automacao.CorrespondeEscopo(v.Name))
                });
            }
            if (vistas.Count == 0) { Show("Todas as vistas imprimíveis do projeto já estão em pranchas.", ResultKind.Info); return Result.Cancelled; }

            var probe = new LegendProbe();
            LegendEngine.MeasureTitleBlocks(Doc, probe, null);
            if (probe.TitleBlocks.Count == 0) { Show("O projeto não tem carimbos (famílias de folha). Carregue um carimbo e rode de novo.", ResultKind.Warning); return Result.Cancelled; }
            var carimbos = probe.TitleBlocks.Select(Carimbo).ToList();

            var usados = new HashSet<string>(new FilteredElementCollector(Doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Select(s => s.SheetNumber), StringComparer.OrdinalIgnoreCase);
            var w2 = new MapeadorFolhasWindow(vistas, carimbos, usados, LegendEngine.NextNumber);
            if (Owner != IntPtr.Zero) new System.Windows.Interop.WindowInteropHelper(w2).Owner = Owner;
            if (w2.ShowDialog() != true) return Result.Cancelled;

            TitleBlockInfo tb = probe.TitleBlocks[w2.CarimboIndice];
            var criadas = new List<ViewSheet>();
            var problemas = new List<string>();
            int colocadas = 0;
            ToolProgress.Begin(Owner, Title, false);
            using (var t = new Transaction(Doc, "JA Tools - Mapeador de Folhas"))
            {
                t.Start();
                RevitDwgUnifier.Export.FailureSwallower.Attach(t);
                for (int i = 0; i < w2.Plano.Count; i++)
                {
                    MapFolha f = w2.Plano[i];
                    ToolProgress.Report(i, w2.Plano.Count, "Prancha " + f.Numero + "…");
                    ViewSheet sh = ViewSheet.Create(Doc, tb.Id);
                    string num = NextFree(f.Numero, usados);
                    usados.Add(num);
                    try { sh.SheetNumber = num; } catch (Exception ex) { problemas.Add($"{f.Numero}: número não aceito ({ex.Message})."); }
                    try { sh.Name = string.IsNullOrWhiteSpace(f.Nome) ? f.Itens[0].Nome : f.Nome.Trim(); } catch { }
                    criadas.Add(sh);
                    Doc.Regenerate();

                    // usable area (mm, sheet coordinates)
                    Element tbi = new FilteredElementCollector(Doc, sh.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).FirstElement();
                    BoundingBoxXYZ bb = tbi?.get_BoundingBox(sh);
                    double x0 = (bb?.Min.X ?? 0) * 304.8, y0 = (bb?.Min.Y ?? 0) * 304.8;
                    double x1 = bb != null ? bb.Max.X * 304.8 : x0 + tb.Width * 304.8, y1 = bb != null ? bb.Max.Y * 304.8 : y0 + tb.Height * 304.8;
                    Livre(tb, out double fl, out double ft, out double fr, out double fb);
                    double m = w2.MargemMm;
                    double L = x0 + fl + m, T = y1 - ft - m, R = x1 - fr - m, B = y0 + fb + m;
                    var centro = new XYZ((L + R) / 2 / 304.8, (T + B) / 2 / 304.8, 0);

                    var vps = new List<Viewport>();
                    foreach (MapVista mv in f.Itens)
                    {
                        var id = new ElementId(mv.Id);
                        if (!Viewport.CanAddViewToSheet(Doc, sh.Id, id)) { problemas.Add($"{mv.Nome}: o Revit não deixa pôr esta vista numa prancha."); continue; }
                        try { vps.Add(Viewport.Create(Doc, sh.Id, id, centro)); colocadas++; }
                        catch (Exception ex) { problemas.Add($"{mv.Nome}: {ex.Message}"); }
                    }
                    if (vps.Count == 0) continue;
                    Doc.Regenerate();
                    var boxes = new List<LayoutBox>();
                    foreach (Viewport vp in vps)
                    {
                        Outline o = vp.GetBoxOutline();
                        boxes.Add(new LayoutBox { W = (o.MaximumPoint.X - o.MinimumPoint.X) * 304.8, H = (o.MaximumPoint.Y - o.MinimumPoint.Y) * 304.8, Tag = vp });
                    }
                    var ordem = boxes.OrderByDescending(b => b.W * b.H).ToList();
                    bool coube = LayoutEngine.Pack(ordem, L, T, R, B, w2.GapMm);
                    foreach (LayoutBox b in ordem)
                        try { ((Viewport)b.Tag).SetBoxCenter(new XYZ(b.X / 304.8, b.Y / 304.8, 0)); } catch { }
                    if (!coube) problemas.Add($"{num}: as vistas não couberam todas na área útil — confira a prancha (escala, carimbo ou mais pranchas).");
                }
                t.Commit();
            }
            ToolProgress.End();

            if (criadas.Count > 0) TryOpen(criadas[0]);
            string txt = $"{criadas.Count} prancha(s) criada(s) ({criadas.First().SheetNumber} a {criadas.Last().SheetNumber}) com {colocadas} vista(s).";
            if (problemas.Count > 0) txt += "\n\nAtenção:\n• " + string.Join("\n• ", problemas.Take(20)) + (problemas.Count > 20 ? $"\n• … e mais {problemas.Count - 20}" : "");
            Show(txt, problemas.Count > 0 ? ResultKind.Warning : ResultKind.Success);
            return Result.Succeeded;
        }

        private static void Livre(TitleBlockInfo tb, out double l, out double t, out double r, out double b)
        {
            if (tb.FreeDetected) { l = tb.FreeLeftMm; t = tb.FreeTopMm; r = tb.FreeRightMm; b = tb.FreeBottomMm; }
            else { l = 20; t = 10; r = 10; b = 10; }
        }

        private static MapCarimbo Carimbo(TitleBlockInfo tb)
        {
            Livre(tb, out double l, out double t, out double r, out double b);
            return new MapCarimbo { Nome = tb.Name, UtilWMm = tb.Width * 304.8 - l - r, UtilHMm = tb.Height * 304.8 - t - b };
        }
    }
}
