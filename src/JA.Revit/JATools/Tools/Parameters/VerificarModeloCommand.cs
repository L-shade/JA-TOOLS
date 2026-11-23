using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using RevitDwgUnifier.Tools.Common;

namespace RevitDwgUnifier.Tools.Parameters
{
    /// <summary>
    /// Verificar Modelo: a check of the office standards — Revit warnings, views that are on no
    /// sheet, empty sheets, names left as "Cópia", imported (not linked) CAD, loaded families with
    /// no instance and rooms that are not placed or not enclosed. After the report, an optional
    /// "Corrigir" step deletes what the user ticks (unused families, empty sheets, "Cópia" views
    /// on no sheet, unplaced / unenclosed rooms), with the list as a preview; Ctrl+Z undoes it.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class VerificarModeloCommand : ToolCommand
    {
        public override string Title => "Verificar Modelo";

        protected override Result Run()
        {
            var sb = new StringBuilder();
            int problemas = 0;
            // what the correction step can delete
            var famSemUso = new List<Family>();
            var pranchasVazias = new List<ViewSheet>();
            var vistasCopia = new List<View>();
            var ambientesSoltos = new List<Room>();
            var gruposSemUso = new List<GroupType>();
            var modelosSemUso = new List<View>();
            var linhasModelo = new List<ModelCurve>();
            void Bloco(string titulo, IList<string> itens, string dica)
            {
                if (itens.Count == 0) { sb.AppendLine("✔ " + titulo + ": nenhum."); return; }
                problemas += itens.Count;
                sb.AppendLine();
                sb.AppendLine("⚠ " + titulo + ": " + itens.Count);
                foreach (string s in itens.Take(12)) sb.AppendLine("   • " + s);
                if (itens.Count > 12) sb.AppendLine("   … e mais " + (itens.Count - 12) + ".");
                if (dica != null) sb.AppendLine("   " + dica);
            }
            List<string> Tentar(Func<List<string>> f, string nome)
            {
                try { return f(); }
                catch (Exception ex) { return new List<string> { "(não foi possível verificar " + nome + ": " + ex.Message + ")" }; }
            }

            // Revit warnings, grouped by text
            Bloco("Avisos do Revit", Tentar(() => Doc.GetWarnings().GroupBy(w => w.GetDescriptionText())
                .OrderByDescending(g => g.Count()).Select(g => g.Count() + " × " + g.Key).ToList(), "os avisos"),
                "Gerenciar › Avisos mostra os elementos de cada um.");

            // views on no sheet
            var tipos = new HashSet<ViewType> { ViewType.FloorPlan, ViewType.CeilingPlan, ViewType.Elevation, ViewType.Section, ViewType.ThreeD,
                                                ViewType.Detail, ViewType.DraftingView, ViewType.AreaPlan, ViewType.EngineeringPlan };
            var vistas = new FilteredElementCollector(Doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate && tipos.Contains(v.ViewType)).ToList();
            Bloco("Vistas fora de pranchas", Tentar(() =>
            {
                var naPrancha = new HashSet<ElementId>(new FilteredElementCollector(Doc).OfClass(typeof(Viewport)).Cast<Viewport>().Select(vp => vp.ViewId));
                bool Copia0(string n) => !string.IsNullOrEmpty(n) && (n.IndexOf("Cópia", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Copia", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                                      n.IndexOf("Copy", StringComparison.OrdinalIgnoreCase) >= 0);
                vistasCopia.AddRange(vistas.Where(v => !naPrancha.Contains(v.Id) && Copia0(v.Name) && v.Id != Doc.ActiveView.Id));
                return vistas.Where(v => !naPrancha.Contains(v.Id)).OrderBy(v => v.ViewType.ToString()).ThenBy(v => v.Name).Select(v => v.Name + " (" + v.ViewType + ")").ToList();
            }, "as vistas"), "Vistas de trabalho são normais; apague as que sobraram para o arquivo ficar leve.");

            // empty sheets
            var pranchas = new FilteredElementCollector(Doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => !s.IsPlaceholder).ToList();
            Bloco("Pranchas sem nenhuma vista", Tentar(() =>
            {
                var comTabela = new HashSet<ElementId>(new FilteredElementCollector(Doc).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>()
                    .Where(i => !i.IsTitleblockRevisionSchedule).Select(i => i.OwnerViewId));
                var vazias = pranchas.Where(s => s.GetAllPlacedViews().Count == 0 && !comTabela.Contains(s.Id)).OrderBy(s => s.SheetNumber).ToList();
                pranchasVazias.AddRange(vazias.Where(s => s.Id != Doc.ActiveView.Id));
                return vazias.Select(s => s.SheetNumber + " - " + s.Name).ToList();
            }, "as pranchas"), null);

            // names left by a copy
            bool Copia(string n) => !string.IsNullOrEmpty(n) && (n.IndexOf("Cópia", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Copia", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                                 n.IndexOf("Copy", StringComparison.OrdinalIgnoreCase) >= 0);
            Bloco("Vistas e pranchas com “Cópia” no nome", vistas.Where(v => Copia(v.Name)).Select(v => v.Name)
                .Concat(pranchas.Where(s => Copia(s.Name)).Select(s => s.SheetNumber + " - " + s.Name)).OrderBy(n => n).ToList(), "Renomeie conforme o padrão do escritório.");
            Bloco("Tipos com “Cópia” no nome", Tentar(() => new FilteredElementCollector(Doc).WhereElementIsElementType().Cast<ElementType>()
                .Where(t => Copia(t.Name)).Select(t => (t.FamilyName ?? "") + " : " + t.Name).Distinct().OrderBy(n => n).ToList(), "os tipos"), null);

            // imported CAD
            Bloco("CAD importado (não vinculado)", Tentar(() => new FilteredElementCollector(Doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>()
                .Where(i => !i.IsLinked).Select(i => (i.Category?.Name ?? "CAD") + (i.ViewSpecific ? " — só numa vista" : " — em todo o modelo")).OrderBy(n => n).ToList(), "os CAD"),
                "Prefira vincular o DWG: o importado pesa no arquivo e traz estilos de linha.");

            // loaded families with no instance
            Bloco("Famílias carregadas sem uso", Tentar(() =>
            {
                var usados = new HashSet<ElementId>(new FilteredElementCollector(Doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                    .Select(i => i.Symbol?.Family?.Id).Where(id => id != null));
                var sem = new FilteredElementCollector(Doc).OfClass(typeof(Family)).Cast<Family>()
                    .Where(f => !f.IsInPlace && f.IsEditable && !usados.Contains(f.Id) && f.FamilyCategory != null && f.FamilyCategory.CategoryType == CategoryType.Model)
                    .OrderBy(f => f.FamilyCategory.Name + " : " + f.Name).ToList();
                famSemUso.AddRange(sem);
                return sem.Select(f => f.FamilyCategory.Name + " : " + f.Name).ToList();
            }, "as famílias"), "Gerenciar › Limpar não utilizados remove de uma vez (famílias aninhadas e de anotação ficaram de fora desta lista).");

            // rooms
            Bloco("Ambientes não colocados ou não delimitados", Tentar(() =>
            {
                var soltos = new FilteredElementCollector(Doc).OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType().OfType<Room>().Where(r => r.Area <= 1e-6).OrderBy(r => r.Number ?? "").ToList();
                ambientesSoltos.AddRange(soltos);
                return soltos.Select(r => Nome(r)).ToList();
            }, "os ambientes"), null);

            // levels with no floor plan
            Bloco("Níveis sem planta baixa", Tentar(() =>
            {
                var comPlanta = new HashSet<ElementId>(new FilteredElementCollector(Doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                    .Where(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan && v.GenLevel != null).Select(v => v.GenLevel.Id));
                return RevitUtil.Levels(Doc).Where(l => !comPlanta.Contains(l.Id)).Select(l => l.Name).ToList();
            }, "os níveis"), "Nível sem planta costuma ser sobra (ou falta a planta): Editar Níveis cria as plantas.");

            // groups never placed
            Bloco("Grupos sem uso", Tentar(() =>
            {
                var sem = new FilteredElementCollector(Doc).OfClass(typeof(GroupType)).Cast<GroupType>().Where(g => g.Groups.Size == 0).OrderBy(g => g.Name).ToList();
                gruposSemUso.AddRange(sem);
                return sem.Select(g => g.Name).ToList();
            }, "os grupos"), null);

            // view templates not applied to any view
            Bloco("Modelos de vista sem uso", Tentar(() =>
            {
                var todas = new FilteredElementCollector(Doc).OfClass(typeof(View)).Cast<View>().ToList();
                var usados = new HashSet<ElementId>(todas.Where(v => !v.IsTemplate).Select(v => v.ViewTemplateId).Where(id => id != ElementId.InvalidElementId));
                var sem = todas.Where(v => v.IsTemplate && !usados.Contains(v.Id)).OrderBy(v => v.Name).ToList();
                modelosSemUso.AddRange(sem);
                return sem.Select(v => v.Name).ToList();
            }, "os modelos de vista"), "Modelos do padrão do escritório podem ficar mesmo sem uso; apague só os que vieram de outros arquivos.");

            // model lines lying flat (drawn in a plan as model lines instead of detail lines)
            Bloco("Linhas de modelo soltas em plantas", Tentar(() =>
            {
                var l = new FilteredElementCollector(Doc).OfClass(typeof(CurveElement)).OfType<ModelCurve>()
                    .Where(c => c.Category != null && c.Category.Id.Value == (long)BuiltInCategory.OST_Lines && c.GroupId == ElementId.InvalidElementId &&
                                c.SketchPlane != null && Math.Abs(c.SketchPlane.GetPlane().Normal.Z) > 0.99).ToList();
                linhasModelo.AddRange(l);
                return l.GroupBy(c => c.LineStyle?.Name ?? "Linhas").Select(g => g.Count() + " × " + g.Key).ToList();
            }, "as linhas de modelo"), "Linhas de modelo aparecem em todas as vistas; em planta, use linhas de detalhe.");

            string cabeca = problemas == 0 ? "Modelo dentro do padrão: nenhum item para corrigir." : problemas + " item(ns) para conferir. Nada foi alterado no modelo.";
            int corrigiveis = famSemUso.Count + pranchasVazias.Count + vistasCopia.Count + ambientesSoltos.Count + gruposSemUso.Count + modelosSemUso.Count + linhasModelo.Count;
            if (corrigiveis > 0) cabeca += "\nA seguir: a janela Corrigir, para apagar o que você marcar (feche-a para só conferir).";
            Show(cabeca + "\n" + sb.ToString().TrimEnd(), problemas == 0 ? ResultKind.Success : ResultKind.Warning);
            if (corrigiveis == 0) return Result.Succeeded;
            return Corrigir(famSemUso, pranchasVazias, vistasCopia, ambientesSoltos, gruposSemUso, modelosSemUso, linhasModelo);
        }

        private static string Nome(Room r) => ((r.Number ?? "") + " " + (r.Name ?? "")).Trim() + (r.Location == null ? " (não colocado)" : " (não delimitado)");

        /// <summary>The correction step: each kind with its own switch and the list as preview (all ticked).</summary>
        private Result Corrigir(List<Family> fams, List<ViewSheet> sheets, List<View> views, List<Room> rooms,
                                List<GroupType> grupos0, List<View> modelos, List<ModelCurve> linhas)
        {
            var dlg = new OptionsDialog("Verificar Modelo", "VerificarModeloCorrigir", "Apagar marcados");
            var grupos = new List<(OptionsDialog.BoolField chk, OptionsDialog.MultiField lista, List<ElementId> ids)>();
            void Grupo(string chave, string titulo, List<ElementId> ids, List<string> nomes, string ajuda)
            {
                if (ids.Count == 0) return;
                dlg.Section(titulo + " (" + ids.Count + ")");
                var chk = dlg.Check(chave, "Apagar", false, ajuda);
                chk.Value = false;
                var lista = dlg.Multi(chave + "Lista", "Itens", nomes, true, false);
                lista.EnabledWhen(chk);
                grupos.Add((chk, lista, ids));
            }
            Grupo("familias", "Famílias carregadas sem uso", fams.Select(f => f.Id).ToList(), fams.Select(f => f.FamilyCategory.Name + " : " + f.Name).ToList(),
                  "Remove a família e seus tipos do projeto (dá para carregar de novo da biblioteca).");
            Grupo("pranchas", "Pranchas sem nenhuma vista", sheets.Select(s => s.Id).ToList(), sheets.Select(s => s.SheetNumber + " - " + s.Name).ToList(), null);
            Grupo("vistas", "Vistas com “Cópia” no nome e fora de pranchas", views.Select(v => v.Id).ToList(), views.Select(v => v.Name + " (" + v.ViewType + ")").ToList(),
                  "Apagar uma vista apaga também as cotas, textos e linhas de detalhe dela.");
            Grupo("ambientes", "Ambientes não colocados ou não delimitados", rooms.Select(r => r.Id).ToList(), rooms.Select(Nome).ToList(),
                  "Os ambientes não colocados aparecem como “Não colocado” nas tabelas de ambientes.");
            Grupo("grupos", "Grupos sem uso", grupos0.Select(g => g.Id).ToList(), grupos0.Select(g => g.Name).ToList(), null);
            Grupo("modelos", "Modelos de vista sem uso", modelos.Select(v => v.Id).ToList(), modelos.Select(v => v.Name).ToList(),
                  "Confira antes: modelos do padrão do escritório podem ficar mesmo sem uso.");
            Grupo("linhas", "Linhas de modelo soltas em plantas", linhas.Select(l => l.Id).ToList(),
                  linhas.Select(l => (l.LineStyle?.Name ?? "Linhas") + " · " + l.Id.Value).ToList(), "Elas somem de todas as vistas (não viram linhas de detalhe).");
            dlg.Note("Nada é apagado sem marcar “Apagar” no grupo. Ctrl+Z desfaz tudo de uma vez.");
            if (!dlg.Show(Owner)) return Result.Succeeded;

            var alvo = grupos.Where(g => g.chk.Value).SelectMany(g => g.lista.SelectedIndices.Select(i => g.ids[i])).ToList();
            if (alvo.Count == 0) return Result.Succeeded;
            int apagados = 0;
            var falhas = new List<string>();
            using (var t = new Transaction(Doc, "JA Tools - Verificar Modelo (corrigir)"))
            {
                t.Start();
                RevitDwgUnifier.Export.FailureSwallower.Attach(t);
                foreach (ElementId id in alvo)
                {
                    Element e = Doc.GetElement(id);
                    if (e == null) continue;      // already gone with something deleted before (a view of a deleted sheet...)
                    string nome = e.Name;
                    try { Doc.Delete(id); apagados++; }
                    catch (Exception ex) { RevitUtil.AddOnce(falhas, nome + ": " + ex.Message); }
                }
                t.Commit();
            }
            Show($"{apagados} item(ns) apagado(s)." + (falhas.Count > 0 ? "\n\nNão apagados:\n• " + string.Join("\n• ", falhas.Take(15)) : "") + "\n\nCtrl+Z desfaz.",
                 falhas.Count > 0 ? ResultKind.Warning : ResultKind.Success);
            return Result.Succeeded;
        }
    }
}
