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
    //  Etiquetar Elementos
    // =====================================================================================

    /// <summary>
    /// Etiquetar Elementos: tags the elements of the chosen categories in the active view with the
    /// office identifiers (loaded on demand), skipping elements already tagged in the view and
    /// elements without Mark / Type Mark; then moves overlapping annotations apart.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class EtiquetarElementosCommand : ToolCommand
    {
        public override string Title => "Etiquetar Elementos";

        protected override Result Run()
        {
            View view = Doc.ActiveView;
            if (view == null || view.IsTemplate || view is ViewSheet || view is ViewSchedule || (view is View3D v3 && !v3.IsLocked))
            {
                Show("Abra uma planta, corte, elevação ou vista 3D travada e execute novamente.");
                return Result.Cancelled;
            }

            var selected = new HashSet<ElementId>(UiDoc.Selection.GetElementIds());
            // categories with taggable elements in the view
            var byCat = new FilteredElementCollector(Doc, view.Id).WhereElementIsNotElementType()
                .Where(e => e.Category != null && e.Category.CategoryType == CategoryType.Model && !(e is Room) &&
                            e.Category.Id.Value != (long)BuiltInCategory.OST_RoomSeparationLines &&
                            e.Category.Id.Value != (long)BuiltInCategory.OST_Lines)
                .GroupBy(e => e.Category.Id.Value)
                .Select(g => new { Cat = g.First().Category, Items = g.ToList() })
                .OrderBy(x => x.Cat.Name).ToList();
            if (byCat.Count == 0) { Show("Nenhum elemento do modelo visível nesta vista."); return Result.Cancelled; }

            var dlg = new OptionsDialog(Title, "EtiquetarElementos", "Etiquetar");
            var labels = byCat.Select(x => $"{x.Cat.Name}  ({x.Items.Count})" + (TagLibrary.TagCategoryOf(x.Cat).HasValue ? "  · identificador JA" : "")).ToList();
            var fCats = dlg.Multi("cats", "Categorias", labels, false);
            for (int i = 0; i < byCat.Count; i++)
                if (ToolPrefs.Get("EtiquetarElementos.cats") == null) fCats.Checked[i] = TagLibrary.TagCategoryOf(byCat[i].Cat).HasValue;
            var fSel = dlg.Check("onlySel", $"Somente os elementos selecionados ({selected.Count})", selected.Count > 0);
            fSel.EnabledWhen(() => selected.Count > 0);
            var fLeader = dlg.Check("leader", "Com linha de chamada", false);
            var fWhere = dlg.Choice("where", "Posição", new[] { "Acima", "À direita", "Abaixo", "No centro", "Automática (como no desenho à mão)" }, 4,
                "Automática (plantas): portas e janelas com a etiqueta em frente, no sentido da parede; peças compridas (tento, soleira) " +
                "com a etiqueta sobre a peça, no sentido dela; as demais ao lado, no primeiro espaço livre (sem cobrir paredes, peças e outras anotações).");
            var fOff = dlg.Number("off", "Distância do elemento (mm no papel)", 5, 0, 50);
            var fAdjust = dlg.Check("adjust", "Ajustar sobreposições depois (só as etiquetas novas se movem)", true);
            dlg.Note("Usa os identificadores do escritório (pasta Identificadores do JA Tools) quando a categoria tem um. " +
                     "Elementos sem Marca nem Marca de tipo e elementos já etiquetados nesta vista são pulados.");
            if (!dlg.Show(Owner)) return Result.Cancelled;

            var problems = new List<string>();
            int created = 0, unmarked = 0, already = 0;
            var newIds = new List<ElementId>();
            AnnotationAdjuster.Result adj = null;
            double mm = Math.Max(1, view.Scale) / 304.8;
            XYZ right = view.RightDirection, up = view.UpDirection;
            var catOrder = fCats.SelectedIndices.ToList();
            ToolProgress.Setup($"Etiquetando {catOrder.Count} categoria(s)", new[] { "Etiquetar", "Ajustar sobreposições", "Concluir" });
            int catDone = 0;

            using (var t = new Transaction(Doc, "JA Tools - " + Title))
            {
                t.Start();
                RevitDwgUnifier.Export.FailureSwallower.Attach(t);
                var tagged = TaggedIds(view);
                foreach (int ci in catOrder)
                {
                    var group = byCat[ci];
                    ToolProgress.Report(catDone++, catOrder.Count, $"Etiquetando {group.Cat.Name} ({group.Items.Count})…");
                    ElementId jaTag = TagLibrary.Ensure(Doc, group.Cat, problems);
                    if (fWhere.Index == 4 && view is ViewPlan)
                    {
                        var todo = new List<Element>();
                        foreach (Element e in group.Items)
                        {
                            if (fSel.Value && selected.Count > 0 && !selected.Contains(e.Id)) continue;
                            if (tagged.Contains(e.Id)) { already++; continue; }
                            if (!RevitUtil.HasMark(Doc, e)) { unmarked++; continue; }
                            todo.Add(e);
                        }
                        foreach (IndependentTag tg in TagPlacer.Tag(Doc, view, todo, jaTag, null, fLeader.Value, problems, group.Cat.Name))
                        {
                            newIds.Add(tg.Id);
                            created++;
                        }
                        foreach (Element e in todo) tagged.Add(e.Id);
                        continue;
                    }
                    foreach (Element e in group.Items)
                    {
                        if (fSel.Value && selected.Count > 0 && !selected.Contains(e.Id)) continue;
                        if (tagged.Contains(e.Id)) { already++; continue; }
                        if (!RevitUtil.HasMark(Doc, e)) { unmarked++; continue; }
                        XYZ c = RevitUtil.Center(e, view);
                        BoundingBoxXYZ bb = e.get_BoundingBox(view);
                        if (c == null) continue;
                        double halfW = 0, halfH = 0;
                        if (bb != null)
                        {
                            halfW = Math.Abs((bb.Max - bb.Min).DotProduct(right)) / 2;
                            halfH = Math.Abs((bb.Max - bb.Min).DotProduct(up)) / 2;
                        }
                        double off = fOff.Value * mm;
                        XYZ head;
                        switch (fWhere.Index)
                        {
                            case 1: head = c + right * (halfW + off); break;
                            case 2: head = c - up * (halfH + off); break;
                            case 3: head = c; break;
                            default: head = c + up * (halfH + off); break;
                        }
                        try
                        {
                            var tag = IndependentTag.Create(Doc, view.Id, new Reference(e), fLeader.Value, TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal, head);
                            if (tag == null) continue;
                            if (jaTag != null) { try { tag.ChangeTypeId(jaTag); } catch { } }
                            newIds.Add(tag.Id);
                            tagged.Add(e.Id);
                            created++;
                        }
                        catch (Exception ex)
                        {
                            RevitUtil.AddOnce(problems, $"{group.Cat.Name}: {ex.Message} (carregue uma etiqueta dessa categoria).");
                            break;
                        }
                    }
                }
                Doc.Regenerate();
                if (fAdjust.Value && newIds.Count > 0)
                {
                    ToolProgress.Stage(1, "Verificando sobreposições…");
                    adj = AnnotationAdjuster.Run(Doc, view, newIds);
                }
                ToolProgress.Stage(2, "Gravando no modelo…");
                t.Commit();
            }

            string text = $"{created} etiqueta(s) criada(s)." +
                          (already > 0 ? $"\n{already} elemento(s) já estavam etiquetados." : "") +
                          (unmarked > 0 ? $"\n{unmarked} elemento(s) sem Marca nem Marca de tipo não foram etiquetados." : "") +
                          (adj != null && adj.Overlapping > 0 ? $"\nSobreposições: {adj.Moved} etiqueta(s) ajustadas" + (adj.Unresolved > 0 ? $", {adj.Unresolved} sem lugar livre." : ".") : "");
            if (problems.Count > 0) text += "\n\nAvisos:\n" + string.Join("\n", problems.Take(12).Select(p => "  • " + p));
            Show(text);
            return Result.Succeeded;
        }

        internal static HashSet<ElementId> TaggedIds(View view)
        {
            var set = new HashSet<ElementId>();
            foreach (IndependentTag tag in new FilteredElementCollector(view.Document, view.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>())
            {
                try { foreach (ElementId id in tag.GetTaggedLocalElementIds()) set.Add(id); } catch { }
            }
            return set;
        }
    }
}
