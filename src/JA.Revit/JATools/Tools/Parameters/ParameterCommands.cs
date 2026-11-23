using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using RevitDwgUnifier.Tools.Common;
using RevitDwgUnifier.Tools.Dimensions;

namespace RevitDwgUnifier.Tools.Parameters
{
    /// <summary>
    /// Renumerar Elementos: writes Mark = prefix + sequential number to doors, windows or walls,
    /// in reading order (level, then top→bottom, left→right) or in the order they are clicked.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class RenumerarElementosCommand : ToolCommand
    {
        public override string Title => "Renumerar Elementos";
        protected override bool PicksInView => true;   // clicks in the view: no progress box

        private static readonly BuiltInCategory[] Cats = { BuiltInCategory.OST_Doors, BuiltInCategory.OST_Windows, BuiltInCategory.OST_Walls };
        private static readonly string[] CatNames = { "Portas", "Janelas", "Paredes" };
        private static readonly string[] Prefixes = { "P", "J", "PAR" };

        protected override Result Run()
        {
            var dlg = new OptionsDialog(Title, "Renumerar");
            var fCat = dlg.Choice("cat", "Categoria", CatNames);
            var fMode = dlg.Choice("mode", "Ordem", new[]
            {
                "Por posição: nível, depois de cima para baixo e da esquerda para a direita",
                "Clicando nos elementos em sequência (Esc termina)",
                "Somente os selecionados, por posição"
            });
            var fPrefix = dlg.Text("prefix", "Prefixo (vazio = P / J / PAR)", "");
            var fStart = dlg.Number("start", "Primeiro número", 1, 0, 100000);
            var fDigits = dlg.Number("digits", "Dígitos (ex.: 2 → 01)", 2, 1, 6);
            var fRow = dlg.Number("row", "Tolerância de linha (m)", 0.5, 0, 100);
            if (!dlg.Show(Owner)) return Result.Cancelled;

            BuiltInCategory cat = Cats[fCat.Index];
            string prefix = string.IsNullOrEmpty(fPrefix.Value) ? Prefixes[fCat.Index] : fPrefix.Value;
            List<Element> ordered;

            if (fMode.Index == 1)
            {
                ordered = new List<Element>();
                var seen = new HashSet<ElementId>();
                while (true)
                {
                    try
                    {
                        Reference r = UiDoc.Selection.PickObject(ObjectType.Element, new CategorySelectionFilter(cat),
                            $"Clique no elemento nº {ordered.Count + (int)fStart.Value} (Esc termina)");
                        if (seen.Add(r.ElementId)) ordered.Add(Doc.GetElement(r));
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException) { break; }
                }
            }
            else
            {
                IEnumerable<Element> source = fMode.Index == 2
                    ? UiDoc.Selection.GetElementIds().Select(id => Doc.GetElement(id)).Where(e => e?.Category != null && e.Category.Id.Value == (long)cat)
                    : new FilteredElementCollector(Doc).OfCategory(cat).WhereElementIsNotElementType().ToElements();
                double row = RevitUtil.M(fRow.Value);
                ordered = source.Select(e => new { E = e, P = RevitUtil.Center(e), Z = (Doc.GetElement(e.LevelId) as Level)?.Elevation ?? 0 })
                    .Where(x => x.P != null)
                    .OrderBy(x => x.Z)
                    .ThenByDescending(x => row > 0 ? Math.Round(x.P.Y / row) : x.P.Y)
                    .ThenBy(x => x.P.X)
                    .Select(x => x.E).ToList();
            }
            if (ordered.Count == 0) return Result.Cancelled;

            int n = 0;
            using (var t = new Transaction(Doc, "JA Tools - " + Title))
            {
                t.Start();
                RevitDwgUnifier.Export.FailureSwallower.Attach(t);   // duplicate-mark warnings while renumbering
                int number = (int)fStart.Value;
                foreach (Element e in ordered)
                {
                    Parameter p = e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
                    if (p == null || p.IsReadOnly) continue;
                    p.Set(prefix + number.ToString(new string('0', (int)fDigits.Value)));
                    number++;
                    n++;
                }
                t.Commit();
            }
            Show($"{n} elemento(s) renumerado(s) ({prefix}{((int)fStart.Value).ToString(new string('0', (int)fDigits.Value))} …).");
            return Result.Succeeded;
        }
    }

    /// <summary>Editar Níveis: every level in one table (name, elevation in meters); add, delete,
    /// and optionally rename the plan views named after a renamed level.</summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class EditarNiveisCommand : ToolCommand
    {
        public override string Title => "Editar Níveis";

        protected override Result Run()
        {
            List<Level> levels = RevitUtil.Levels(Doc);
            var rows = levels.Select(l => new LevelRow { Id = l.Id, Name = l.Name, OriginalName = l.Name, ElevationM = Math.Round(RevitUtil.ToM(l.Elevation), 4) }).ToList();
            var window = new LevelEditorWindow(rows);
            if (Owner != IntPtr.Zero) new System.Windows.Interop.WindowInteropHelper(window).Owner = Owner;
            if (window.ShowDialog() != true) return Result.Cancelled;
            ToolProgress.Arm();

            int created = 0, changed = 0, deleted = 0, views = 0, rehosted = 0, rehostFailed = 0;
            var problems = new List<string>();
            var made = new Dictionary<LevelRow, Level>();
            using (var tg = new TransactionGroup(Doc, "JA Tools - " + Title))
            {
                tg.Start();

                // 1. new levels, elevations and names (deletions wait for the end)
                using (var t = new Transaction(Doc, "JA Tools - " + Title))
                {
                    t.Start();
                    RevitDwgUnifier.Export.FailureSwallower.Attach(t);
                    var allViews = new FilteredElementCollector(Doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().Where(v => !v.IsTemplate).ToList();

                    foreach (LevelRow r in window.Rows)
                    {
                        try
                        {
                            if (r.Id == null || r.Id == ElementId.InvalidElementId)
                            {
                                if (r.Delete) continue;
                                Level nl = Level.Create(Doc, RevitUtil.M(r.ElevationM));
                                if (!string.IsNullOrWhiteSpace(r.Name)) nl.Name = r.Name.Trim();
                                made[r] = nl;
                                if (window.CreatePlans)
                                {
                                    ViewPlan vp = ViewPlan.Create(Doc, RevitUtil.FamilyType(Doc, ViewFamily.FloorPlan).Id, nl.Id);
                                    RevitUtil.Rename(vp, nl.Name);
                                }
                                created++;
                                continue;
                            }
                            if (r.Delete) continue;

                            var level = (Level)Doc.GetElement(r.Id);
                            bool any = false;
                            double elev = RevitUtil.M(r.ElevationM);
                            if (Math.Abs(level.Elevation - elev) > RevitUtil.M(0.0001)) { level.Elevation = elev; any = true; }
                            string name = (r.Name ?? "").Trim();
                            if (name.Length > 0 && name != level.Name)
                            {
                                string old = level.Name;
                                level.Name = name;
                                any = true;
                                if (window.RenameViews)
                                    foreach (ViewPlan v in allViews.Where(v => v.GenLevel?.Id == level.Id && v.Name == old))
                                    {
                                        RevitUtil.Rename(v, name);
                                        views++;
                                    }
                            }
                            if (any) changed++;
                        }
                        catch (Exception ex) { problems.Add($"{r.Name}: {ex.Message}"); }
                    }
                    t.Commit();
                }

                // 2. revincular: one transaction per pair (a refusal by Revit does not undo the rest)
                int mi = 0;
                foreach (LevelMove m in window.Moves)
                {
                    ToolProgress.Report(mi++, window.Moves.Count, $"Revinculando {m.From.Name} → {m.To.Name}");
                    Level from = m.From.Id != null && m.From.Id != ElementId.InvalidElementId ? Doc.GetElement(m.From.Id) as Level : null;
                    Level to = m.To.Id != null && m.To.Id != ElementId.InvalidElementId ? Doc.GetElement(m.To.Id) as Level
                             : made.TryGetValue(m.To, out Level nl2) ? nl2 : null;
                    if (from == null || to == null) { problems.Add($"{m}: nível não encontrado (o destino foi excluído?)."); continue; }
                    if (m.To.Delete) { problems.Add($"{m}: o nível de destino está marcado para excluir — revínculo pulado."); continue; }
                    using (var t = new Transaction(Doc, $"JA Tools - Revincular {from.Name} → {to.Name}"))
                    {
                        t.Start();
                        RevitDwgUnifier.Export.FailureSwallower.Attach(t);
                        LevelRehost.Outcome o = LevelRehost.Move(Doc, from, to);
                        if (t.Commit() == TransactionStatus.Committed)
                        {
                            rehosted += o.Moved;
                            rehostFailed += o.Failed;
                            foreach (string p in o.Problems.Take(5)) RevitUtil.AddOnce(problems, p);
                            if (o.Rooms > 0)
                                problems.Add($"{from.Name}: {o.Rooms} ambiente(s) continuam nesse nível (o Revit não deixa mudar o nível de um ambiente; recrie-os no destino se for excluir a origem).");
                        }
                        else problems.Add($"{from.Name} → {to.Name}: o Revit recusou o revínculo (nada foi mudado nesse par).");
                    }
                }

                // 3. deletions, after the elements were moved away
                using (var t = new Transaction(Doc, "JA Tools - " + Title + " (excluir)"))
                {
                    t.Start();
                    RevitDwgUnifier.Export.FailureSwallower.Attach(t);
                    foreach (LevelRow r in window.Rows.Where(x => x.Delete && x.Id != null && x.Id != ElementId.InvalidElementId))
                    {
                        try { Doc.Delete(r.Id); deleted++; }
                        catch (Exception ex) { problems.Add($"{r.Name}: {ex.Message}"); }
                    }
                    t.Commit();
                }
                tg.Assimilate();
            }

            string text = $"{changed} nível(is) alterado(s), {created} criado(s), {deleted} excluído(s), {views} vista(s) renomeada(s)." +
                          (window.Moves.Count > 0 ? $"\n{rehosted} elemento(s) revinculado(s) sem sair do lugar" + (rehostFailed > 0 ? $", {rehostFailed} não puderam mudar de nível." : ".") : "");
            if (problems.Count > 0) text += "\n\nAvisos:\n" + string.Join("\n", problems.Take(20).Select(p => "  • " + p));
            Show(text);
            return Result.Succeeded;
        }
    }

    /// <summary>Remover Ambientes: deletes rooms that are not placed, not enclosed or redundant
    /// (the "Not Placed / Not Enclosed" lines that clutter room schedules).</summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class RemoverAmbientesCommand : ToolCommand
    {
        public override string Title => "Remover Ambientes";

        protected override Result Run()
        {
            var rooms = new FilteredElementCollector(Doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().OfType<Room>().ToList();
            var unplaced = rooms.Where(r => r.Location == null).ToList();
            var zero = rooms.Where(r => r.Location != null && r.Area <= 1e-9).ToList();   // not enclosed or redundant

            var dlg = new OptionsDialog(Title, "RemoverAmbientes");
            var fUnplaced = dlg.Check("unplaced", $"Não colocados ({unplaced.Count})", true);
            var fZero = dlg.Check("zero", $"Não delimitados ou redundantes — área zero ({zero.Count})", true);
            dlg.Note("Ambientes colocados e delimitados nunca são removidos.");
            if (!dlg.Show(Owner)) return Result.Cancelled;

            var ids = new List<ElementId>();
            if (fUnplaced.Value) ids.AddRange(unplaced.Select(r => r.Id));
            if (fZero.Value) ids.AddRange(zero.Select(r => r.Id));
            if (ids.Count == 0) { Show("Nenhum ambiente a remover."); return Result.Succeeded; }

            using (var t = new Transaction(Doc, "JA Tools - " + Title))
            {
                t.Start();
                RevitDwgUnifier.Export.FailureSwallower.Attach(t);
                Doc.Delete(ids);
                t.Commit();
            }
            Show($"{ids.Count} ambiente(s) removido(s).");
            return Result.Succeeded;
        }
    }
}
