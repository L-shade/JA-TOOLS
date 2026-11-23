using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using RevitDwgUnifier.Tools.Common;

namespace RevitDwgUnifier.Tools.Views
{
    /// <summary>
    /// Ocultar em Vista: in sections, elevations and plans, treats the elements of the chosen
    /// categories (or the selected elements) that are SEEN beyond the cut plane ("em vista"):
    ///  * whole elements not cut: hidden, thin lines, thin grey lines or halftone;
    ///  * elements the cut plane goes through: their nested shared parts that are not cut are
    ///    hidden, and their projection lines (the part seen beyond the cut) get a thin / grey
    ///    override — the cut lines stay as they are. E.g. the railing cut in front of the slab:
    ///    the cut profile stays strong, the panels and posts seen behind become thin.
    ///
    /// Cut test: a thin slab on the cut plane (sections/elevations: the plane of the view through
    /// its origin; plans: the cut plane of the view range), as wide as the elements of the view;
    /// an element whose geometry touches it (ElementIntersectsSolidFilter) is cut.
    /// Everything is per view (Hide in View › Elements, Override Graphics in View › By Element).
    /// What the tool did is kept in the view (extensible storage), so "Desfazer" undoes exactly
    /// that — never what the user hid or overrode by hand.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class OcultarEmVistaCommand : ToolCommand
    {
        public override string Title => "Ocultar em Vista";

        private const string Prefs = "OcultarEmVista";
        private static readonly Guid SchemaV1 = new Guid("7b1f3c52-9a4e-4d2b-8f61-3c0e5a9d2b47");   // Build 48: hidden ids only
        private static readonly Guid SchemaV2 = new Guid("c4e2a7d9-51b3-4f86-9a0c-2d7e8b13f5a6");   // Build 49
        private static readonly Guid SchemaId = new Guid("e91d6b40-3f2a-4c7e-b5d8-6a0f1c29e7b3");
        private const string FHidden = "HiddenIds", FWeight = "WeightIds", FColor = "ColorIds", FHalftone = "HalftoneIds";
        private const string FCutWeight = "CutWeightIds", FCatWeight = "CatWeightIds", FCatCut = "CatCutIds";

        /// <summary>The parts of a railing: one choice, "Guarda-corpos".</summary>
        private static readonly string[] RailingCats =
        {
            "OST_StairsRailing", "OST_RailingTopRail", "OST_RailingHandRail", "OST_RailingSupport", "OST_RailingTermination",
            "OST_StairsRailingBaluster", "OST_RailingSystem", "OST_RailingSystemHandRail", "OST_RailingSystemTopRail"
        };

        /// <summary>Categories never offered (not "seen" model pieces).</summary>
        private static readonly string[] Skip =
        {
            "OST_Rooms", "OST_Areas", "OST_MEPSpaces", "OST_RvtLinks", "OST_Lines", "OST_Cameras", "OST_SectionBox",
            "OST_Levels", "OST_Grids", "OST_CLines", "OST_IOSModelGroups", "OST_Viewers", "OST_Matchline", "OST_VolumeOfInterest"
        };

        private sealed class CatGroup
        {
            public string Name;
            public readonly HashSet<long> Cats = new HashSet<long>();
            public int Count;
        }

        /// <summary>What to do with the lines of an element.</summary>
        private enum Look { None, Thin, ThinGrey, Halftone }

        private sealed class Settings
        {
            public bool HideWhole;              // whole elements not cut: hide (else Whole look)
            public Look Whole = Look.None;
            public bool HideParts;              // nested parts not cut of cut elements
            public Look CutLines = Look.None;   // projection lines of cut elements
            public int Pen = 1;
            public double Tol;
        }

        /// <summary>What the tool changed in a view (kept in the view).</summary>
        private sealed class Record
        {
            public readonly List<ElementId> Hidden = new List<ElementId>(), Weight = new List<ElementId>(),
                                            Color = new List<ElementId>(), Halftone = new List<ElementId>(),
                                            CutWeight = new List<ElementId>(),                     // elements: cut line weight
                                            CatWeight = new List<ElementId>(), CatCut = new List<ElementId>();   // categories of the view
            public int HiddenWhole, HiddenParts, Thinned;
        }

        protected override Result Run()
        {
            // selected model elements (in the active view): only them, whatever their category
            List<Element> picked = UiDoc.Selection.GetElementIds().Select(id => Doc.GetElement(id))
                .Where(e => e != null && !(e is View) && !(e is Viewport) && e.Category?.CategoryType == CategoryType.Model).ToList();
            List<View> views = picked.Count > 0 && Eligible(UiDoc.ActiveView) ? new List<View> { UiDoc.ActiveView } : TargetViews();
            if (views.Count == 0)
            {
                Show("Abra um corte, uma elevação ou uma planta (ou selecione vistas / pranchas no Navegador de Projeto) e rode de novo.");
                return Result.Cancelled;
            }
            if (picked.Count > 0 && !Eligible(UiDoc.ActiveView)) picked.Clear();

            // categories present in the views (railing parts together)
            var railing = new HashSet<long>();
            foreach (string n in RailingCats) if (Enum.TryParse(n, out BuiltInCategory b)) railing.Add((long)b);
            var skip = new HashSet<long>();
            foreach (string n in Skip) if (Enum.TryParse(n, out BuiltInCategory b)) skip.Add((long)b);

            var groups = new Dictionary<string, CatGroup>(StringComparer.OrdinalIgnoreCase);
            if (picked.Count == 0)
                foreach (View v in views)
                {
                    ToolProgress.Pulse($"{v.Name}: lendo os elementos…");
                    foreach (Element e in new FilteredElementCollector(Doc, v.Id).WhereElementIsNotElementType())
                    {
                        Category c = e.Category;
                        if (c == null || c.CategoryType != CategoryType.Model || e is View) continue;
                        long id = c.Id.Value;
                        if (skip.Contains(id)) continue;
                        string name = railing.Contains(id) ? "Guarda-corpos" : c.Name;
                        if (!groups.TryGetValue(name, out CatGroup g)) groups[name] = g = new CatGroup { Name = name };
                        g.Cats.Add(id);
                        g.Count++;
                    }
                }
            List<CatGroup> list = groups.Values.OrderBy(g => g.Name).ToList();

            var dlg = new OptionsDialog(Title, Prefs);
            var fMode = dlg.Choice("mode", "O que fazer", new List<string>
            {
                picked.Count > 0 ? $"Tratar os {picked.Count} elemento(s) selecionado(s)" : "Tratar os elementos em vista das categorias marcadas",
                "Desfazer o que esta ferramenta fez (reexibir e tirar as linhas finas)",
                picked.Count > 0 ? $"Só mudar a espessura de linha dos {picked.Count} elemento(s) selecionado(s)"
                                 : "Só mudar a espessura de linha das categorias marcadas (todos os elementos de uma vez)"
            });
            Func<bool> applying = () => fMode.Index == 0;
            Func<bool> weighting = () => fMode.Index == 2;
            OptionsDialog.MultiField fCats = null;
            if (picked.Count == 0 && list.Count > 0)
            {
                bool first = ToolPrefs.Get(Prefs + ".cats") == null;
                fCats = dlg.Multi("cats", "Categorias", list.Select(g => g.Name).ToList(), false, false);
                if (first)
                    for (int i = 0; i < list.Count; i++) fCats.Checked[i] = list[i].Name == "Guarda-corpos";
                fCats.EnabledWhen(() => fMode.Index != 1);
            }

            dlg.Section("Elementos inteiros em vista (o plano de corte não passa por eles)");
            var fWhole = dlg.Choice("whole", "Fazer", new List<string> { "Ocultar", "Linha fina", "Linha fina e cinza", "Meio-tom", "Não mexer" }, 0);
            fWhole.EnabledWhen(applying);

            dlg.Section("Elementos cortados: a parte em vista (além do corte)");
            var fParts = dlg.Check("parts", "Ocultar as peças aninhadas que não são cortadas", true,
                "Peças de famílias aninhadas compartilhadas (montantes, painéis, ferragens...) que ficam além do corte. Peças que não são compartilhadas não podem ser ocultadas sozinhas: use a linha fina abaixo.");
            fParts.EnabledWhen(applying);
            var fCutLines = dlg.Choice("cutlines", "Linhas em vista", new List<string> { "Não mexer", "Linha fina", "Linha fina e cinza" }, 1,
                "Só as linhas de projeção (o que se vê além do corte) mudam; as linhas cortadas continuam com a espessura de corte.");
            fCutLines.EnabledWhen(applying);
            var fPen = dlg.Number("pen", "Espessura da linha fina (1 a 16)", 1, 1, 16);
            fPen.EnabledWhen(applying);
            dlg.Section("Só espessura de linha (toda a categoria)");
            var fProjPen = dlg.Number("projpen", "Espessura das linhas em vista / projeção (0 = não mexer)", 1, 0, 16,
                "Vale para todos os elementos das categorias marcadas nessas vistas, cortados ou não, e também para os que forem modelados depois (Visibilidade/Gráficos da vista).");
            fProjPen.EnabledWhen(weighting);
            var fCutPen = dlg.Number("cutpen", "Espessura das linhas cortadas (0 = não mexer)", 0, 0, 16);
            fCutPen.EnabledWhen(weighting);

            var fTol = dlg.Number("tol", "Conta como cortado até (cm) do plano de corte", 1, 0, 100,
                "Elementos ou peças a essa distância do plano de corte (ou cortados por ele) são tratados como cortados.");
            fTol.EnabledWhen(applying);
            dlg.Note($"{views.Count} vista(s): " + string.Join(", ", views.Take(4).Select(v => v.Name)) + (views.Count > 4 ? "…" : "") + ".");
            dlg.Note("Depois do OK: as mudanças valem só nessas vistas (Ocultar na vista, Sobrepor gráficos por elemento ou, em \"Só mudar a espessura\", Visibilidade/Gráficos da categoria). Rode de novo com \"Desfazer\" para voltar como estava.");
            if (!dlg.Show(Owner)) return Result.Cancelled;

            var st = new Settings
            {
                HideWhole = fWhole.Index == 0,
                Whole = fWhole.Index == 1 ? Look.Thin : fWhole.Index == 2 ? Look.ThinGrey : fWhole.Index == 3 ? Look.Halftone : Look.None,
                HideParts = fParts.Value,
                CutLines = fCutLines.Index == 1 ? Look.Thin : fCutLines.Index == 2 ? Look.ThinGrey : Look.None,
                Pen = Math.Max(1, Math.Min(16, (int)Math.Round(fPen.Value))),
                Tol = RevitUtil.M(fTol.Value / 100.0)
            };

            var problems = new List<string>();
            int whole = 0, parts = 0, thinned = 0, undone = 0, weighted = 0, viewsDone = 0;
            int projPen = (int)Math.Round(fProjPen.Value), cutPen = (int)Math.Round(fCutPen.Value);
            using (var t = new Transaction(Doc, "JA Tools - " + Title))
            {
                t.Start();
                Schema schema = GetSchema();
                int k = 0;
                foreach (View v in views)
                {
                    ToolProgress.Report(k++, views.Count, v.Name);
                    try
                    {
                        if (fMode.Index == 0)
                        {
                            List<Element> candidates = picked.Count > 0 ? picked : Candidates(v, list, fCats);
                            Record r = ApplyInView(v, candidates, st, schema, problems);
                            if (r != null && r.HiddenWhole + r.HiddenParts + r.Thinned > 0)
                            {
                                whole += r.HiddenWhole; parts += r.HiddenParts; thinned += r.Thinned; viewsDone++;
                            }
                        }
                        else if (fMode.Index == 2)
                        {
                            int n = WeightInView(v, list, fCats, picked, projPen, cutPen, schema, problems);
                            if (n > 0) { weighted += n; viewsDone++; }
                        }
                        else
                        {
                            int n = UndoInView(v, schema);
                            if (n > 0) { undone += n; viewsDone++; }
                        }
                    }
                    catch (Exception ex) { RevitUtil.AddOnce(problems, v.Name + ": " + ex.Message); }
                }
                t.Commit();
            }

            string text;
            if (fMode.Index == 0)
            {
                var bits = new List<string>();
                if (whole > 0) bits.Add($"{whole} elemento(s) em vista " + (st.HideWhole ? "ocultado(s)" : "com linha fina / meio-tom"));
                if (parts > 0) bits.Add($"{parts} peça(s) em vista de elementos cortados ocultada(s)");
                if (thinned > 0) bits.Add($"{thinned} elemento(s) cortado(s) com as linhas em vista finas");
                text = bits.Count > 0 ? string.Join("; ", bits) + $" — em {viewsDone} vista(s)." : "Nada a tratar: nenhum elemento em vista nas categorias / seleção.";
            }
            else if (fMode.Index == 2)
                text = projPen <= 0 && cutPen <= 0 ? "Nada mudou: as duas espessuras estão em 0 (não mexer)."
                     : weighted > 0 ? $"Espessura de linha alterada em {weighted} " + (picked.Count > 0 ? "elemento(s)" : "categoria(s)") + $" em {viewsDone} vista(s)" +
                                      (projPen > 0 ? $" — em vista: {projPen}" : "") + (cutPen > 0 ? $" — cortadas: {cutPen}" : "") + "."
                     : "Nada mudou: nenhuma categoria marcada pôde ser alterada nessas vistas.";
            else text = undone > 0 ? $"{undone} elemento(s) voltaram ao normal em {viewsDone} vista(s)." : "Nada a desfazer: esta ferramenta não mudou elementos nessas vistas.";
            if (problems.Count > 0) text += "\n\n" + string.Join("\n", problems.Take(12));
            Show(text);
            return Result.Succeeded;
        }

        // ------------------------------------------------------------------ views
        private List<View> TargetViews()
        {
            var result = new List<View>();
            var seen = new HashSet<long>();
            void Add(View v)
            {
                if (v == null || v.IsTemplate || !Eligible(v) || !seen.Add(v.Id.Value)) return;
                result.Add(v);
            }
            void AddSheet(ViewSheet s)
            {
                foreach (ElementId id in s.GetAllPlacedViews()) Add(Doc.GetElement(id) as View);
            }
            foreach (ElementId id in UiDoc.Selection.GetElementIds())
            {
                Element e = Doc.GetElement(id);
                if (e is ViewSheet s) AddSheet(s);
                else if (e is View v) Add(v);
                else if (e is Viewport vp) Add(Doc.GetElement(vp.ViewId) as View);
            }
            if (result.Count == 0)
            {
                View active = UiDoc.ActiveView;
                if (active is ViewSheet s) AddSheet(s); else Add(active);
            }
            return result;
        }

        private static bool Eligible(View v)
        {
            if (v == null) return false;
            switch (v.ViewType)
            {
                case ViewType.Section:
                case ViewType.Elevation:
                case ViewType.Detail:
                case ViewType.FloorPlan:
                case ViewType.CeilingPlan:
                case ViewType.EngineeringPlan:
                case ViewType.AreaPlan:
                    return true;
                default:
                    return false;
            }
        }

        // ------------------------------------------------------------------ apply / undo
        private List<Element> Candidates(View v, List<CatGroup> groups, OptionsDialog.MultiField fCats)
        {
            if (fCats == null) return new List<Element>();
            var cats = new HashSet<long>();
            foreach (int i in fCats.SelectedIndices) foreach (long c in groups[i].Cats) cats.Add(c);
            if (cats.Count == 0) return new List<Element>();
            return new FilteredElementCollector(Doc, v.Id).WhereElementIsNotElementType()
                .Where(e => e.Category != null && cats.Contains(e.Category.Id.Value)).ToList();
        }

        private Record ApplyInView(View v, List<Element> candidates, Settings st, Schema schema, List<string> problems)
        {
            if (candidates.Count == 0) return null;
            Solid slab = CutSlab(v, candidates, st.Tol);
            if (slab == null)
            {
                RevitUtil.AddOnce(problems, v.Name + ": não foi possível achar o plano de corte; nada mudou.");
                return null;
            }
            HashSet<long> cut = CutIds(candidates.Select(e => e.Id).ToList(), slab, v, problems);
            if (cut == null) return null;

            Record rec = LoadRecord(v, schema);
            var hide = new List<ElementId>();
            var hideSet = new HashSet<long>();
            var pen = st.Pen;
            void Hide(ElementId id) { if (hideSet.Add(id.Value)) hide.Add(id); }

            // nested parts of every cut element, tested against the cut plane in ONE filter (was one per element)
            var partsOf = new Dictionary<long, List<ElementId>>();
            var allParts = new List<ElementId>();
            var seenParts = new HashSet<long>();
            foreach (Element e in candidates)
            {
                if (!cut.Contains(e.Id.Value)) continue;
                List<ElementId> sub = SubParts(e);
                if (sub.Count == 0) continue;
                partsOf[e.Id.Value] = sub;
                foreach (ElementId id in sub) if (seenParts.Add(id.Value)) allParts.Add(id);
            }
            HashSet<long> partCut = allParts.Count > 0
                ? CutIds(allParts, slab, v, problems) ?? new HashSet<long>(allParts.Select(i => i.Value))
                : new HashSet<long>();

            foreach (Element e in candidates)
            {
                if (!cut.Contains(e.Id.Value))
                {
                    // whole element beyond the cut
                    if (st.HideWhole) { if (CanHide(e, v) && !hideSet.Contains(e.Id.Value)) { Hide(e.Id); rec.HiddenWhole++; } }
                    else if (st.Whole != Look.None && Override(v, e.Id, st.Whole, pen, rec)) rec.HiddenWhole++;
                    continue;
                }

                // cut element: its nested parts beyond the cut, and the lines seen beyond the cut
                if (partsOf.TryGetValue(e.Id.Value, out List<ElementId> sub))
                {
                    foreach (ElementId id in sub)
                    {
                        Element p = Doc.GetElement(id);
                        if (p == null) continue;
                        if (!partCut.Contains(id.Value))
                        {
                            if (st.HideParts && CanHide(p, v)) { if (!hideSet.Contains(id.Value)) { Hide(id); rec.HiddenParts++; } }
                            else if (st.CutLines != Look.None) Override(v, id, st.CutLines, pen, rec);
                        }
                        else if (st.CutLines != Look.None) Override(v, id, st.CutLines, pen, rec);
                    }
                }
                if (st.CutLines != Look.None && Override(v, e.Id, st.CutLines, pen, rec)) rec.Thinned++;
            }

            if (hide.Count > 0)
            {
                v.HideElements(hide);
                var have = new HashSet<long>(rec.Hidden.Select(i => i.Value));
                rec.Hidden.AddRange(hide.Where(i => have.Add(i.Value)));
            }
            SaveRecord(v, schema, rec);
            return rec;
        }

        private HashSet<long> CutIds(List<ElementId> ids, Solid slab, View v, List<string> problems)
        {
            var cut = new HashSet<long>();
            if (ids.Count == 0) return cut;
            try
            {
                foreach (ElementId id in new FilteredElementCollector(Doc, ids).WherePasses(new ElementIntersectsSolidFilter(slab)).ToElementIds())
                    cut.Add(id.Value);
                return cut;
            }
            catch (Exception ex)
            {
                RevitUtil.AddOnce(problems, v.Name + ": teste de corte falhou (" + ex.Message + "); nada mudou.");
                return null;
            }
        }

        private static bool CanHide(Element e, View v)
        {
            try { return !e.IsHidden(v) && e.CanBeHidden(v); } catch { return false; }
        }

        /// <summary>Nested shared parts of a family instance (all levels).</summary>
        private List<ElementId> SubParts(Element e)
        {
            var result = new List<ElementId>();
            var seen = new HashSet<long>();
            var stack = new Stack<Element>();
            stack.Push(e);
            while (stack.Count > 0)
            {
                if (!(stack.Pop() is FamilyInstance fi)) continue;
                ICollection<ElementId> ids;
                try { ids = fi.GetSubComponentIds(); } catch { continue; }
                foreach (ElementId id in ids)
                {
                    if (!seen.Add(id.Value)) continue;
                    result.Add(id);
                    Element p = Doc.GetElement(id);
                    if (p != null) stack.Push(p);
                }
            }
            return result;
        }

        /// <summary>Thin / grey projection lines, or halftone, added to the element's overrides in the view.</summary>
        private static bool Override(View v, ElementId id, Look look, int pen, Record rec)
        {
            try
            {
                OverrideGraphicSettings o = v.GetElementOverrides(id);
                if (look == Look.Halftone)
                {
                    if (o.Halftone) return false;
                    o.SetHalftone(true);
                    rec.Halftone.Add(id);
                }
                else
                {
                    o.SetProjectionLineWeight(pen);
                    rec.Weight.Add(id);
                    if (look == Look.ThinGrey)
                    {
                        o.SetProjectionLineColor(new Color(128, 128, 128));
                        rec.Color.Add(id);
                    }
                }
                v.SetElementOverrides(id, o);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// "Só mudar a espessura": the line weights of every element of the chosen categories in the
        /// view, at once (category override of Visibility/Graphics: elements modelled later too), or
        /// of the selected elements (element override). Returns categories / elements changed.
        /// </summary>
        private int WeightInView(View v, List<CatGroup> groups, OptionsDialog.MultiField fCats, List<Element> picked,
                                 int projPen, int cutPen, Schema schema, List<string> problems)
        {
            if (projPen <= 0 && cutPen <= 0) return 0;
            Record rec = LoadRecord(v, schema);
            int n = 0;
            if (picked.Count > 0)
            {
                foreach (Element e in picked)
                {
                    try
                    {
                        OverrideGraphicSettings o = v.GetElementOverrides(e.Id);
                        if (projPen > 0) { o.SetProjectionLineWeight(projPen); rec.Weight.Add(e.Id); }
                        if (cutPen > 0) { o.SetCutLineWeight(cutPen); rec.CutWeight.Add(e.Id); }
                        v.SetElementOverrides(e.Id, o);
                        n++;
                    }
                    catch (Exception ex) { RevitUtil.AddOnce(problems, $"{v.Name}: {e.Category?.Name} {e.Id.Value}: {ex.Message}"); }
                }
            }
            else if (fCats != null)
            {
                var cats = new HashSet<long>();
                foreach (int i in fCats.SelectedIndices) foreach (long c in groups[i].Cats) cats.Add(c);
                foreach (long c in cats)
                {
                    var id = new ElementId(c);
                    try
                    {
                        if (!v.IsCategoryOverridable(id)) continue;
                        OverrideGraphicSettings o = v.GetCategoryOverrides(id);
                        if (projPen > 0) { o.SetProjectionLineWeight(projPen); rec.CatWeight.Add(id); }
                        if (cutPen > 0) { o.SetCutLineWeight(cutPen); rec.CatCut.Add(id); }
                        v.SetCategoryOverrides(id, o);
                        n++;
                    }
                    catch (Exception ex)
                    {
                        RevitUtil.AddOnce(problems, v.Name + (v.ViewTemplateId != ElementId.InvalidElementId
                            ? ": o modelo de vista controla Visibilidade/Gráficos — tire o modelo ou altere a espessura nele."
                            : ": " + ex.Message));
                    }
                }
            }
            if (n > 0) SaveRecord(v, schema, rec);
            return n;
        }

        private int UndoInView(View v, Schema schema)
        {
            Record rec = LoadRecord(v, schema);
            var touched = new HashSet<long>();
            var show = new List<ElementId>();
            foreach (ElementId id in rec.Hidden)
            {
                Element e = Doc.GetElement(id);
                try { if (e != null && e.IsHidden(v)) { show.Add(id); touched.Add(id.Value); } } catch { }
            }
            if (show.Count > 0) v.UnhideElements(show);

            void Reset(IEnumerable<ElementId> ids, Action<OverrideGraphicSettings> undo)
            {
                foreach (ElementId id in ids.GroupBy(i => i.Value).Select(g => g.First()))
                {
                    if (Doc.GetElement(id) == null) continue;
                    try
                    {
                        OverrideGraphicSettings o = v.GetElementOverrides(id);
                        undo(o);
                        v.SetElementOverrides(id, o);
                        touched.Add(id.Value);
                    }
                    catch { }
                }
            }
            Reset(rec.Weight, o => o.SetProjectionLineWeight(OverrideGraphicSettings.InvalidPenNumber));
            Reset(rec.Color, o => o.SetProjectionLineColor(Color.InvalidColorValue));
            Reset(rec.Halftone, o => o.SetHalftone(false));
            Reset(rec.CutWeight, o => o.SetCutLineWeight(OverrideGraphicSettings.InvalidPenNumber));

            // category overrides of the view (Visibilidade/Gráficos)
            foreach (var g in rec.CatWeight.Select(i => Tuple.Create(i, true)).Concat(rec.CatCut.Select(i => Tuple.Create(i, false)))
                         .GroupBy(x => x.Item1.Value))
            {
                ElementId cat = g.First().Item1;
                try
                {
                    OverrideGraphicSettings o = v.GetCategoryOverrides(cat);
                    if (g.Any(x => x.Item2)) o.SetProjectionLineWeight(OverrideGraphicSettings.InvalidPenNumber);
                    if (g.Any(x => !x.Item2)) o.SetCutLineWeight(OverrideGraphicSettings.InvalidPenNumber);
                    v.SetCategoryOverrides(cat, o);
                    touched.Add(cat.Value);
                }
                catch { }
            }

            try { v.DeleteEntity(schema); } catch { }
            try { Schema old = Schema.Lookup(SchemaV2); if (old != null) v.DeleteEntity(old); } catch { }
            try { Schema old = Schema.Lookup(SchemaV1); if (old != null) v.DeleteEntity(old); } catch { }
            return touched.Count;
        }

        /// <summary>Thin box (2 × tol, at least 2 mm) on the cut plane, covering every candidate.</summary>
        private Solid CutSlab(View v, List<Element> elements, double tol)
        {
            XYZ origin, n, ax, ay;
            if (v is ViewPlan plan)
            {
                double z;
                try
                {
                    PlanViewRange range = plan.GetViewRange();
                    var lvl = Doc.GetElement(range.GetLevelId(PlanViewPlane.CutPlane)) as Level ?? plan.GenLevel;
                    z = (lvl?.ProjectElevation ?? 0) + range.GetOffset(PlanViewPlane.CutPlane);
                }
                catch { return null; }
                origin = new XYZ(0, 0, z);
                n = XYZ.BasisZ; ax = XYZ.BasisX; ay = XYZ.BasisY;
            }
            else
            {
                origin = v.Origin;
                n = v.ViewDirection;
                ax = v.RightDirection;
                ay = v.UpDirection;
                if (origin == null || n == null || n.GetLength() < 1e-9) return null;
            }

            // extent of the elements on the plane, plus a margin
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            foreach (Element e in elements)
            {
                BoundingBoxXYZ bb = null;
                try { bb = e.get_BoundingBox(null); } catch { }
                if (bb == null) continue;
                foreach (XYZ p in Corners(bb))
                {
                    XYZ q = p - origin;
                    double x = q.DotProduct(ax), y = q.DotProduct(ay);
                    x0 = Math.Min(x0, x); x1 = Math.Max(x1, x);
                    y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
                }
            }
            if (x0 > x1 || y0 > y1) return null;
            double m = RevitUtil.M(1.0);
            x0 -= m; y0 -= m; x1 += m; y1 += m;
            double half = Math.Max(tol, RevitUtil.M(0.001));

            XYZ b = origin - n * half;
            XYZ p0 = b + ax * x0 + ay * y0, p1 = b + ax * x1 + ay * y0, p2 = b + ax * x1 + ay * y1, p3 = b + ax * x0 + ay * y1;
            try
            {
                var loop = new CurveLoop();
                loop.Append(Line.CreateBound(p0, p1));
                loop.Append(Line.CreateBound(p1, p2));
                loop.Append(Line.CreateBound(p2, p3));
                loop.Append(Line.CreateBound(p3, p0));
                return GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { loop }, n, 2 * half);
            }
            catch { return null; }
        }

        private static IEnumerable<XYZ> Corners(BoundingBoxXYZ bb)
        {
            Transform t = bb.Transform ?? Transform.Identity;
            XYZ a = bb.Min, c = bb.Max;
            foreach (double x in new[] { a.X, c.X })
                foreach (double y in new[] { a.Y, c.Y })
                    foreach (double z in new[] { a.Z, c.Z })
                        yield return t.OfPoint(new XYZ(x, y, z));
        }

        // ------------------------------------------------------------------ storage (in the view)
        private static Schema GetSchema()
        {
            Schema s = Schema.Lookup(SchemaId);
            if (s != null) return s;
            var b = new SchemaBuilder(SchemaId);
            b.SetSchemaName("JAToolsOcultarEmVista3");
            b.SetReadAccessLevel(AccessLevel.Public);
            b.SetWriteAccessLevel(AccessLevel.Public);
            b.SetDocumentation("O que a ferramenta Ocultar em Vista do JA Tools mudou nesta vista.");
            b.AddArrayField(FHidden, typeof(ElementId));
            b.AddArrayField(FWeight, typeof(ElementId));
            b.AddArrayField(FColor, typeof(ElementId));
            b.AddArrayField(FHalftone, typeof(ElementId));
            b.AddArrayField(FCutWeight, typeof(ElementId));
            b.AddArrayField(FCatWeight, typeof(ElementId));
            b.AddArrayField(FCatCut, typeof(ElementId));
            return b.Finish();
        }

        private static Record LoadRecord(View v, Schema schema)
        {
            var r = new Record();
            try
            {
                Entity e = v.GetEntity(schema);
                if (e != null && e.IsValid())
                {
                    r.Hidden.AddRange(e.Get<IList<ElementId>>(FHidden) ?? new List<ElementId>());
                    r.Weight.AddRange(e.Get<IList<ElementId>>(FWeight) ?? new List<ElementId>());
                    r.Color.AddRange(e.Get<IList<ElementId>>(FColor) ?? new List<ElementId>());
                    r.Halftone.AddRange(e.Get<IList<ElementId>>(FHalftone) ?? new List<ElementId>());
                    r.CutWeight.AddRange(e.Get<IList<ElementId>>(FCutWeight) ?? new List<ElementId>());
                    r.CatWeight.AddRange(e.Get<IList<ElementId>>(FCatWeight) ?? new List<ElementId>());
                    r.CatCut.AddRange(e.Get<IList<ElementId>>(FCatCut) ?? new List<ElementId>());
                }
            }
            catch { }
            // what Build 49 did (second version of the tool)
            try
            {
                Schema old = Schema.Lookup(SchemaV2);
                Entity e = old != null ? v.GetEntity(old) : null;
                if (e != null && e.IsValid())
                {
                    r.Hidden.AddRange(e.Get<IList<ElementId>>(FHidden) ?? new List<ElementId>());
                    r.Weight.AddRange(e.Get<IList<ElementId>>(FWeight) ?? new List<ElementId>());
                    r.Color.AddRange(e.Get<IList<ElementId>>(FColor) ?? new List<ElementId>());
                    r.Halftone.AddRange(e.Get<IList<ElementId>>(FHalftone) ?? new List<ElementId>());
                }
            }
            catch { }
            // what Build 48 hid (first version of the tool)
            try
            {
                Schema old = Schema.Lookup(SchemaV1);
                Entity e = old != null ? v.GetEntity(old) : null;
                if (e != null && e.IsValid()) r.Hidden.AddRange(e.Get<IList<ElementId>>(FHidden) ?? new List<ElementId>());
            }
            catch { }
            return r;
        }

        private static void SaveRecord(View v, Schema schema, Record r)
        {
            IList<ElementId> Distinct(List<ElementId> ids) => ids.GroupBy(i => i.Value).Select(g => g.First()).ToList();
            var e = new Entity(schema);
            e.Set<IList<ElementId>>(FHidden, Distinct(r.Hidden));
            e.Set<IList<ElementId>>(FWeight, Distinct(r.Weight));
            e.Set<IList<ElementId>>(FColor, Distinct(r.Color));
            e.Set<IList<ElementId>>(FHalftone, Distinct(r.Halftone));
            e.Set<IList<ElementId>>(FCutWeight, Distinct(r.CutWeight));
            e.Set<IList<ElementId>>(FCatWeight, Distinct(r.CatWeight));
            e.Set<IList<ElementId>>(FCatCut, Distinct(r.CatCut));
            v.SetEntity(e);
            // older records were merged into this one
            try { Schema old = Schema.Lookup(SchemaV2); if (old != null) v.DeleteEntity(old); } catch { }
            try { Schema old = Schema.Lookup(SchemaV1); if (old != null) v.DeleteEntity(old); } catch { }
        }
    }
}
