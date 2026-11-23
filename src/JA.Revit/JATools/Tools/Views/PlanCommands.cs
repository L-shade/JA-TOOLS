using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using RevitDwgUnifier.Tools.Common;

namespace RevitDwgUnifier.Tools.Views
{
    /// <summary>One kind of plan made by <see cref="PlanBatchCommand"/>.</summary>
    public sealed class PlanMode
    {
        public string Key = "", Label = "";
        public ViewFamily Family = ViewFamily.FloorPlan;
        public string DefaultName = "{nivel}";
        public bool TagRooms = true;
        public Tuple<BuiltInCategory, string, bool>[] Tags = new Tuple<BuiltInCategory, string, bool>[0];
    }

    /// <summary>
    /// Batch creation of plans, one per chosen level (dialog -> one transaction -> tags -> report).
    /// Several modes = one tool with a "Planta" switch at the top; each mode keeps its own view
    /// type, template, name and tags (Plantas por Nível: baixa, forro, luminotécnica).
    /// </summary>
    public abstract class PlanBatchCommand : ToolCommand
    {
        protected abstract PlanMode[] Modes { get; }
        protected abstract string PrefsKey { get; }
        protected virtual int DefaultScale => 50;
        protected virtual void AddOptions(OptionsDialog dlg) { }
        protected virtual void AfterCreate(ViewPlan view, Level level, List<string> problems, Dictionary<string, int> counters) { }

        protected override Result Run()
        {
            List<Level> levels = RevitUtil.Levels(Doc);
            if (levels.Count == 0) { Show("O projeto não tem níveis."); return Result.Cancelled; }
            PlanMode[] modes = Modes;
            bool multi = modes.Length > 1;
            string K(PlanMode m, string key) => multi ? key + "." + m.Key : key;   // single mode: old keys

            var dlg = new OptionsDialog(Title, PrefsKey);
            var fLevels = dlg.Multi("levels", "Níveis", levels.Select(l => l.Name).ToList());
            OptionsDialog.ChoiceField fMode = multi ? dlg.Choice("mode", "Planta", modes.Select(m => m.Label).ToList()) : null;

            var types = new List<ViewFamilyType>[modes.Length];
            var templates = new List<View>[modes.Length];
            var fType = new OptionsDialog.ChoiceField[modes.Length];
            var fTemplate = new OptionsDialog.ChoiceField[modes.Length];
            var fName = new OptionsDialog.TextField[modes.Length];
            for (int m = 0; m < modes.Length; m++)
            {
                types[m] = new FilteredElementCollector(Doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                    .Where(t => t.ViewFamily == modes[m].Family).OrderBy(t => t.Name).ToList();
                templates[m] = RevitUtil.Templates(Doc, modes[m].Family == ViewFamily.CeilingPlan ? Autodesk.Revit.DB.ViewType.CeilingPlan : Autodesk.Revit.DB.ViewType.FloorPlan);
                fType[m] = dlg.Choice(K(modes[m], "type"), "Tipo de vista", types[m].Select(t => t.Name).ToList());
                fTemplate[m] = dlg.Choice(K(modes[m], "template"), "Modelo de vista", RevitUtil.TemplateChoices(templates[m]));
                if (multi) { fType[m].VisibleWhen(fMode, m); fTemplate[m].VisibleWhen(fMode, m); }
            }
            var fScale = dlg.Number("scale", "Escala 1:", DefaultScale, 1, 10000);
            for (int m = 0; m < modes.Length; m++)
            {
                fName[m] = dlg.Text(K(modes[m], "name"), "Nome ({nivel} = nome do nível)", modes[m].DefaultName);
                if (multi) fName[m].VisibleWhen(fMode, m);
            }

            dlg.Section("Etiquetas");
            var fRooms = new OptionsDialog.BoolField[modes.Length];
            var tagFields = new List<Tuple<BuiltInCategory, OptionsDialog.BoolField>>[modes.Length];
            for (int m = 0; m < modes.Length; m++)
            {
                fRooms[m] = dlg.Check(K(modes[m], "tagRooms"), "Etiquetar ambientes", modes[m].TagRooms);
                if (multi) fRooms[m].VisibleWhen(fMode, m);
                tagFields[m] = new List<Tuple<BuiltInCategory, OptionsDialog.BoolField>>();
                foreach (var o in modes[m].Tags)
                {
                    var cb = dlg.Check(K(modes[m], "tag_" + o.Item1), "Etiquetar " + o.Item2, o.Item3);
                    if (multi) cb.VisibleWhen(fMode, m);
                    tagFields[m].Add(Tuple.Create(o.Item1, cb));
                }
            }
            AddOptions(dlg);
            if (!dlg.Show(Owner)) return Result.Cancelled;

            int mi = multi ? Math.Max(0, fMode.Index) : 0;
            PlanMode mode = modes[mi];
            if (types[mi].Count == 0) { Show($"O projeto não tem tipo de vista para “{(multi ? mode.Label : Title)}”."); return Result.Cancelled; }

            var problems = new List<string>();
            var counters = new Dictionary<string, int>();
            var created = new List<View>();
            bool tagRooms = fRooms[mi].Value;
            List<Room> rooms = tagRooms ? RevitUtil.Rooms(Doc) : new List<Room>();
            var cats = tagFields[mi].Where(x => x.Item2.Value).Select(x => x.Item1).ToList();

            using (var t = new Transaction(Doc, "JA Tools - " + Title))
            {
                t.Start();
                foreach (int i in fLevels.SelectedIndices)
                {
                    Level level = levels[i];
                    ViewPlan v = ViewPlan.Create(Doc, types[mi][fType[mi].Index].Id, level.Id);
                    RevitUtil.Configure(v, (int)fScale.Value, RevitUtil.TemplateFromChoice(templates[mi], fTemplate[mi].Index));
                    RevitUtil.Rename(v, fName[mi].Value.Replace("{nivel}", level.Name));
                    created.Add(v);
                }
                Doc.Regenerate();

                int pv = 0;
                foreach (ViewPlan v in created.Cast<ViewPlan>())
                {
                    ToolProgress.Report(pv++, created.Count, v.Name + ": etiquetas");
                    Level level = v.GenLevel;
                    if (tagRooms)
                        Count(counters, "etiquetas de ambiente", RevitUtil.TagRooms(Doc, v, rooms.Where(r => r.LevelId == level.Id), problems));
                    // every chosen category of the plan in one batch (faster on large plans)
                    if (cats.Count > 0)
                        foreach (var kv in RevitUtil.TagCategories(Doc, v, cats, problems))
                            Count(counters, "etiquetas de " + RevitUtil.CategoryName(Doc, kv.Key), kv.Value);
                    AfterCreate(v, level, problems, counters);
                }
                t.Commit();
            }

            string summary = $"{created.Count} vista(s) criada(s):\n" + string.Join("\n", created.Select(v => "  • " + v.Name));
            if (counters.Count > 0) summary += "\n\n" + string.Join("\n", counters.Select(kv => $"{kv.Value} {kv.Key}"));
            if (problems.Count > 0) summary += "\n\nAvisos:\n" + string.Join("\n", problems.Select(p => "  • " + p));
            Show(summary);
            if (created.Count > 0) TryOpen(created[0]);
            return Result.Succeeded;
        }

        protected static void Count(Dictionary<string, int> c, string key, int n)
        {
            if (n <= 0) return;
            c[key] = (c.TryGetValue(key, out int v) ? v : 0) + n;
        }
    }

    /// <summary>Plantas por Nível (1.17): Planta Baixa, Plantas de Forro e Plantas Luminotécnicas
    /// numa ferramenta só — a escolha "Planta" no topo troca tipo, modelo, nome e etiquetas.</summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class PlantasPorNivelCommand : PlanBatchCommand
    {
        public override string Title => "Plantas por Nível";
        protected override string PrefsKey => "PlantasPorNivel";
        protected override PlanMode[] Modes => new[]
        {
            new PlanMode { Key = "baixa", Label = "Baixa", Family = ViewFamily.FloorPlan, DefaultName = "PLANTA BAIXA - {nivel}",
                Tags = new[] { Tuple.Create(BuiltInCategory.OST_Doors, "portas", true), Tuple.Create(BuiltInCategory.OST_Windows, "janelas", true) } },
            new PlanMode { Key = "forro", Label = "Forro", Family = ViewFamily.CeilingPlan, DefaultName = "PLANTA DE FORRO - {nivel}",
                Tags = new[] { Tuple.Create(BuiltInCategory.OST_Ceilings, "forros", true) } },
            new PlanMode { Key = "lumi", Label = "Luminotécnica", Family = ViewFamily.CeilingPlan, DefaultName = "LUMINOTÉCNICO - {nivel}", TagRooms = false,
                Tags = new[] { Tuple.Create(BuiltInCategory.OST_LightingFixtures, "luminárias", true), Tuple.Create(BuiltInCategory.OST_LightingDevices, "interruptores", false) } }
        };
    }

    /// <summary>Vistas de Eixos: grid plans per level, grids renamed and dimensioned.</summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class VistasEixosCommand : PlanBatchCommand
    {
        public override string Title => "Vistas de Eixos";
        protected override string PrefsKey => "VistasEixos";
        protected override int DefaultScale => 100;
        protected override PlanMode[] Modes => new[] { new PlanMode { Key = "eixos", Family = ViewFamily.FloorPlan, DefaultName = "EIXOS - {nivel}", TagRooms = false } };

        private OptionsDialog.BoolField _dim;
        private OptionsDialog.NumberField _offset;

        protected override void AddOptions(OptionsDialog dlg)
        {
            dlg.Section("Cotas");
            _dim = dlg.Check("dim", "Cotar eixos (corrente + total)", true);
            _offset = dlg.Number("offset", "Distância da cota à ponta dos eixos (m)", 1.0, 0, 100);
        }

        protected override void AfterCreate(ViewPlan view, Level level, List<string> problems, Dictionary<string, int> counters)
        {
            if (!_dim.Value) return;
            var grids = GridTools.LinearGrids(Doc, view);
            if (grids.Count < 2) { RevitUtil.AddOnce(problems, "Menos de dois eixos visíveis: nada para cotar."); return; }
            Count(counters, "cotas de eixos", GridTools.DimensionGrids(Doc, view, grids, problems, _offset.Value));
        }
    }
}
