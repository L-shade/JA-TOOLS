using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using RevitDwgUnifier.Tools.Common;

namespace RevitDwgUnifier.Tools.Detail
{
    /// <summary>
    /// Hachura Única: ONE filled region over the cut of several separate elements (slab + beam +
    /// wall + column of a section, as one piece of concrete), instead of one hatch per element.
    ///
    /// How: each element's solids are intersected with a thin box just behind the cut plane of the
    /// view (sections: the view plane through its origin; plans: the cut plane of the view range);
    /// the pieces are united (Boolean union, so touching elements become one shape) and the faces of
    /// the union lying on the cut plane give the boundary loops — outer outlines and holes. One
    /// filled region with all of them (or one per separate group, if chosen).
    /// Parts closer than a gap (cm) are joined too (grow, unite, shrink back), and filled regions
    /// already drawn can be selected and merged into one the same way.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class HachuraUnicaCommand : ToolCommand
    {
        public override string Title => "Hachura Única";
        protected override bool PicksInView => true;   // may ask to click elements: no progress box

        // 1.17: Unir Hachuras merged here — hatches selected = "unir" is the first choice; in a
        // view without cut elements (drafting, detail) the tool asks to click the hatches.
        private const string Prefs = "HachuraUnica";

        private sealed class RegionFilter : ISelectionFilter
        {
            public bool AllowElement(Element e) => e is FilledRegion;
            public bool AllowReference(Reference r, XYZ p) => false;
        }

        /// <summary>Categories offered checked by default in "todos os cortados" (the structure).</summary>
        private static readonly string[] DefaultCats =
            { "OST_Floors", "OST_StructuralFraming", "OST_StructuralColumns", "OST_Columns", "OST_Walls", "OST_StructuralFoundation" };

        private sealed class ModelFilter : ISelectionFilter
        {
            public bool AllowElement(Element e) => e != null && !(e is View) && e.Category?.CategoryType == CategoryType.Model;
            public bool AllowReference(Reference r, XYZ p) => false;
        }

        protected override Result Run()
        {
            View v = UiDoc.ActiveView;
            List<FilledRegionType> types = new FilteredElementCollector(Doc).OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>()
                .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (types.Count == 0)
            {
                Show("O projeto não tem tipo de região preenchida (Anotar › Região). Crie um tipo e rode de novo.", ResultKind.Info);
                return Result.Cancelled;
            }

            var filter = new ModelFilter();
            List<Element> picked = UiDoc.Selection.GetElementIds().Select(id => Doc.GetElement(id)).Where(e => e != null).ToList();
            List<Element> selected = picked.Where(filter.AllowElement).ToList();
            // filled regions selected (all in one view): "unir" mode
            List<FilledRegion> regions = picked.OfType<FilledRegion>().ToList();
            bool cutView = (v is ViewSection || v is ViewPlan) && !v.IsTemplate;
            if (regions.Count < 2 && !cutView)
            {
                // nothing to cut here: the tool can only merge hatches
                try
                {
                    regions = UiDoc.Selection.PickObjects(ObjectType.Element, new RegionFilter(), "Clique nas hachuras a unir (na mesma vista) e em Concluir")
                        .Select(r => Doc.GetElement(r)).OfType<FilledRegion>().ToList();
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
                if (regions.Count == 0) return Result.Cancelled;
                if (regions.Select(r => r.OwnerViewId.Value).Distinct().Count() > 1)
                { Show("As hachuras precisam estar na mesma vista para serem unidas.", ResultKind.Info); return Result.Cancelled; }
            }
            if (regions.Select(r => r.OwnerViewId.Value).Distinct().Count() > 1) regions.Clear();
            View regionView = regions.Count > 0 ? Doc.GetElement(regions[0].OwnerViewId) as View : null;
            if (regionView == null) regions.Clear();

            if (regions.Count == 0 && !cutView)
            {
                Show("Abra um corte (ou uma planta) com os elementos cortados — ou selecione as hachuras a unir — e rode de novo.", ResultKind.Info);
                return Result.Cancelled;
            }

            // categories of the model elements in the view (for "todos os cortados")
            var catNames = new SortedDictionary<string, long>(StringComparer.CurrentCultureIgnoreCase);
            if (cutView)
                foreach (Element e in new FilteredElementCollector(Doc, v.Id).WhereElementIsNotElementType())
                {
                    Category c = e.Category;
                    if (c == null || c.CategoryType != CategoryType.Model || e is View) continue;
                    if (!catNames.ContainsKey(c.Name)) catNames[c.Name] = c.Id.Value;
                }
            var catList = catNames.ToList();
            var defaults = new HashSet<long>();
            foreach (string name in DefaultCats) if (Enum.TryParse(name, out BuiltInCategory b)) defaults.Add((long)b);

            var dlg = new OptionsDialog(Title, Prefs);
            var sources = new List<string>();
            int unir = -1, sel = -1, clicar = -1, todos = -1, clicarHachuras = -1;
            if (regions.Count > 0) { unir = sources.Count; sources.Add($"Unir as {regions.Count} hachura(s) selecionada(s) numa só"); }
            if (cutView)
            {
                if (selected.Count > 0) { sel = sources.Count; sources.Add($"Os {selected.Count} elemento(s) selecionado(s)"); }
                clicar = sources.Count; sources.Add("Clicar nos elementos na vista");
                todos = sources.Count; sources.Add("Todos os elementos cortados das categorias marcadas");
                clicarHachuras = sources.Count; sources.Add("Clicar nas hachuras já criadas para unir");
            }
            string sourceKey = "source" + (regions.Count > 0 ? "R" : "") + (selected.Count > 0 ? "S" : "");
            var fSource = dlg.Choice(sourceKey, "O que hachurar", sources, 0);
            OptionsDialog.MultiField fCats = null;
            if (catList.Count > 0)
            {
                bool first = ToolPrefs.Get(Prefs + ".cats") == null;
                fCats = dlg.Multi("cats", "Categorias", catList.Select(x => x.Key).ToList(), false, false);
                if (first) for (int i = 0; i < catList.Count; i++) fCats.Checked[i] = defaults.Contains(catList[i].Value);
                fCats.EnabledWhen(() => fSource.Index == todos);
            }
            dlg.Section("Hachura");
            int guess = regions.Count > 0
                ? types.FindIndex(t => t.Id.Value == regions[0].GetTypeId().Value)
                : types.FindIndex(t => t.Name.IndexOf("concreto", StringComparison.OrdinalIgnoreCase) >= 0);
            var fType = dlg.Choice("type", "Tipo de região preenchida", types.Select(t => t.Name).ToList(), Math.Max(0, guess));
            var fGap = dlg.Number("gap", "Unir partes separadas por até (cm)", 2, 0, 100,
                "Partes muito próximas que não se tocam (frestas entre laje, viga e parede, ou entre duas hachuras) viram uma peça só. 0 = só as que se encostam.");
            var fGroup = dlg.Choice("group", "Como criar", new List<string>
            {
                "Uma hachura só cobrindo todos",
                "Uma hachura por conjunto encostado"
            }, 0);
            var fBorder = dlg.Choice("border", "Contorno", new List<string> { "Sem contorno (linhas invisíveis)", "Linha do tipo de região" }, 0,
                "Sem contorno, as linhas de corte dos próprios elementos continuam marcando o desenho.");
            var fDelete = dlg.Check("delete", "Ao unir hachuras: apagar as originais", true);
            fDelete.EnabledWhen(() => fSource.Index == unir || fSource.Index == clicarHachuras);
            dlg.Note("Elementos que se encostam (ou ficam a menos da distância acima) viram uma forma só, sem as emendas; os separados ficam na mesma hachura, como ilhas.");
            if (!dlg.Show(Owner)) return Result.Cancelled;

            // "clicar nas hachuras": picked now, then merged like the selected ones
            if (fSource.Index == clicarHachuras)
            {
                try
                {
                    regions = UiDoc.Selection.PickObjects(ObjectType.Element, new RegionFilter(), "Clique nas hachuras a unir (na mesma vista) e em Concluir")
                        .Select(r => Doc.GetElement(r)).OfType<FilledRegion>().ToList();
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
                if (regions.Count == 0) return Result.Cancelled;
                if (regions.Select(r => r.OwnerViewId.Value).Distinct().Count() > 1)
                {
                    Show("As hachuras precisam estar na mesma vista para serem unidas.", ResultKind.Info);
                    return Result.Cancelled;
                }
                regionView = Doc.GetElement(regions[0].OwnerViewId) as View;
                if (regionView == null) return Result.Cancelled;
                unir = fSource.Index;
            }

            double gap = RevitUtil.M(fGap.Value / 100.0);
            var problems = new List<string>();
            List<IList<CurveLoop>> faces;
            XYZ origin, n, ax, ay;
            View target;
            int used, notCut = 0, failed = 0;

            if (fSource.Index == unir)
            {
                // ---- existing hatches: their boundaries, on their own view
                target = regionView;
                n = target.ViewDirection.Normalize(); ax = target.RightDirection; ay = target.UpDirection;
                var groups = new List<IList<CurveLoop>>();
                foreach (FilledRegion fr in regions)
                {
                    try { groups.Add(fr.GetBoundaries()); } catch { }
                }
                origin = groups.SelectMany(g => g).SelectMany(l => l).Select(c => c.GetEndPoint(0)).FirstOrDefault();
                if (origin == null) { Show("Não foi possível ler o contorno das hachuras selecionadas.", ResultKind.Warning); return Result.Cancelled; }
                ToolProgress.Setup($"Unindo {regions.Count} hachura(s)", null);
                ToolProgress.Pulse("Unindo as hachuras…");
                faces = Close(Shapes(groups, origin, ax, ay), gap, origin, n, ax, ay, problems);
                used = regions.Count;
            }
            else
            {
                target = v;
                if (!CutPlane(Doc, v, out origin, out n, out ax, out ay))
                {
                    Show("Não foi possível achar o plano de corte desta vista.", ResultKind.Warning);
                    return Result.Cancelled;
                }

                // ---- the elements
                List<Element> elements;
                if (fSource.Index == sel) elements = selected;
                else if (fSource.Index == clicar)
                {
                    try
                    {
                        elements = UiDoc.Selection.PickObjects(ObjectType.Element, filter, "Clique nos elementos que a hachura deve cobrir e em Concluir")
                            .Select(r => Doc.GetElement(r)).Where(e => e != null).ToList();
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
                }
                else
                {
                    var cats = new HashSet<long>();
                    if (fCats != null) foreach (int i in fCats.SelectedIndices) cats.Add(catList[i].Value);
                    elements = new FilteredElementCollector(Doc, v.Id).WhereElementIsNotElementType()
                        .Where(e => e.Category != null && cats.Contains(e.Category.Id.Value) && !(e is View)).ToList();
                }
                if (elements.Count == 0) { Show("Nenhum elemento escolhido.", ResultKind.Info); return Result.Cancelled; }

                // ---- the cut shapes
                ToolProgress.Setup($"Hachura única de {elements.Count} elemento(s)", null);
                ToolProgress.Pulse("Calculando o corte dos elementos…");
                Solid slab = Slab(elements, origin, n, ax, ay);
                if (slab == null) { Show("Não foi possível montar o plano de corte para esses elementos.", ResultKind.Warning); return Result.Cancelled; }
                var pieces = new List<Solid>();
                foreach (Element e in elements)
                {
                    bool any = false;
                    foreach (Solid s in Solids(e, v))
                    {
                        Solid part = null;
                        try { part = BooleanOperationsUtils.ExecuteBooleanOperation(s, slab, BooleanOperationsType.Intersect); }
                        catch { failed++; }
                        if (part != null && part.Volume > 1e-9) { pieces.Add(part); any = true; }
                    }
                    if (!any) notCut++;
                }
                if (pieces.Count == 0)
                {
                    Show("O plano de corte da vista não passa por nenhum dos elementos escolhidos (nada a hachurar)." +
                         (failed > 0 ? $"\n\n{failed} sólido(s) não puderam ser cortados pelo Revit." : ""), ResultKind.Warning);
                    return Result.Cancelled;
                }
                // united: touching pieces become one shape; faces on the cut plane = outlines and holes
                faces = FacesOnPlane(UnionAll(pieces), origin, n);
                if (gap > 1e-9 && faces.Count > 0) faces = Close(Shapes(faces, origin, ax, ay), gap, origin, n, ax, ay, problems);
                used = elements.Count - notCut;
            }
            if (faces.Count == 0) { Show("Não foi possível ler o contorno.", ResultKind.Warning); return Result.Cancelled; }

            // ---- the filled region(s)
            int made = 0;
            ElementId typeId = types[fType.Index].Id;
            ElementId invisible = fBorder.Index == 0 ? InvisibleLineStyle(Doc) : null;
            using (var tx = new Transaction(Doc, "JA Tools - " + Title))
            {
                tx.Start();
                RevitDwgUnifier.Export.FailureSwallower.Attach(tx);
                bool single = fGroup.Index == 0;
                if (single)
                {
                    var all = faces.SelectMany(x => x).ToList();
                    if (Create(typeId, target, all, invisible, problems)) made++;
                    else
                    {
                        single = false;     // shapes the union could not merge overlap: one region per shape
                        RevitUtil.AddOnce(problems, "Algumas partes se sobrepõem e não puderam ir numa hachura só; foram criadas hachuras separadas para elas.");
                    }
                }
                if (!single)
                    foreach (IList<CurveLoop> loops in faces)
                        if (Create(typeId, target, loops.ToList(), invisible, problems)) made++;
                if (fSource.Index == unir && made > 0 && fDelete.Value)
                {
                    try { Doc.Delete(regions.Select(r => r.Id).ToList()); }
                    catch (Exception ex) { RevitUtil.AddOnce(problems, "As hachuras originais não puderam ser apagadas: " + ex.Message); }
                }
                tx.Commit();
            }

            string text = made == 0 ? "Nenhuma hachura pôde ser criada."
                : fSource.Index == unir ? $"{used} hachura(s) unida(s) em {made}."
                : $"{made} hachura(s) criada(s) cobrindo {used} elemento(s) cortado(s).";
            if (notCut > 0) text += $"\n{notCut} elemento(s) escolhido(s) não são cortados pelo plano da vista e ficaram de fora.";
            if (failed > 0) text += $"\n{failed} sólido(s) não puderam ser cortados pelo Revit.";
            if (problems.Count > 0) text += "\n\n" + string.Join("\n", problems.Take(8));
            Show(text);
            return made > 0 ? Result.Succeeded : Result.Failed;
        }

        // ------------------------------------------------------------------ 2D shapes
        /// <summary>An outline and its holes (loops on the view plane).</summary>
        private sealed class Shape
        {
            public CurveLoop Outer;
            public readonly List<CurveLoop> Holes = new List<CurveLoop>();
        }

        /// <summary>Loops of each group (a face, a hatch) sorted into outlines and their holes: a loop
        /// inside an odd number of others is a hole of the smallest outline around it.</summary>
        private static List<Shape> Shapes(IEnumerable<IList<CurveLoop>> groups, XYZ origin, XYZ ax, XYZ ay)
        {
            var result = new List<Shape>();
            foreach (IList<CurveLoop> group in groups)
            {
                var loops = group.Where(l => l != null && l.Any()).ToList();
                var polys = loops.Select(l => Poly(l, origin, ax, ay)).ToList();
                var areas = polys.Select(p => Math.Abs(SignedArea(p))).ToList();
                var depth = new int[loops.Count];
                for (int i = 0; i < loops.Count; i++)
                    for (int j = 0; j < loops.Count; j++)
                        if (i != j && polys[i].Count > 0 && areas[j] > areas[i] && Inside(polys[i][0], polys[j])) depth[i]++;
                var outers = new Dictionary<int, Shape>();
                for (int i = 0; i < loops.Count; i++)
                    if (depth[i] % 2 == 0) { var sh = new Shape { Outer = loops[i] }; outers[i] = sh; result.Add(sh); }
                for (int i = 0; i < loops.Count; i++)
                {
                    if (depth[i] % 2 == 0) continue;
                    int best = -1;
                    foreach (int j in outers.Keys)
                        if (areas[j] > areas[i] && Inside(polys[i][0], polys[j]) && (best < 0 || areas[j] < areas[best])) best = j;
                    if (best >= 0) outers[best].Holes.Add(loops[i]);
                }
            }
            return result;
        }

        /// <summary>
        /// Union of the shapes; with gap &gt; 0 a "closing" also joins the ones closer than gap: each
        /// outline grows by gap/2, the grown shapes are united, the union shrinks back by gap/2 (straight
        /// sides come back exactly where they were), the original shapes are added again and their holes
        /// cut out again. Returns the loops of each resulting island.
        /// </summary>
        private static List<IList<CurveLoop>> Close(List<Shape> shapes, double gap, XYZ origin, XYZ n, XYZ ax, XYZ ay, List<string> problems)
        {
            var exact = shapes.Select(s => Prism(new[] { s.Outer }.Concat(s.Holes).ToList(), n)).Where(x => x != null).ToList();
            List<Solid> union = UnionAll(exact);
            if (gap <= 1e-9) return FacesOnPlane(union, origin, n);

            double g = gap / 2;
            var grown = new List<Solid>();
            foreach (Shape s in shapes)
            {
                CurveLoop big = Offset(s.Outer, g, true, n, origin, ax, ay);
                Solid p = Prism(new List<CurveLoop> { big ?? s.Outer }, n);
                if (p != null) grown.Add(p);
            }
            var shrunk = new List<Solid>();
            foreach (IList<CurveLoop> face in FacesOnPlane(UnionAll(grown), origin, n))
            {
                var loops = new List<CurveLoop>();
                bool ok = true;
                foreach (Shape s in Shapes(new[] { face }, origin, ax, ay))
                {
                    CurveLoop o = Offset(s.Outer, g, false, n, origin, ax, ay);
                    if (o == null) { ok = false; break; }
                    loops.Add(o);
                    foreach (CurveLoop h in s.Holes)
                    {
                        CurveLoop hh = Offset(h, g, true, n, origin, ax, ay);
                        if (hh != null) loops.Add(hh);
                    }
                }
                Solid p = ok ? Prism(loops, n) : null;
                if (p != null) shrunk.Add(p);
                else RevitUtil.AddOnce(problems, "Uma parte não pôde ser unida pela distância (contorno complexo): ficou como estava.");
            }
            List<Solid> result = UnionAll(shrunk.Concat(union).ToList());
            // the holes of the original shapes stay holes
            foreach (Shape s in shapes)
                foreach (CurveLoop h in s.Holes)
                {
                    Solid hp = Prism(new List<CurveLoop> { h }, n);
                    if (hp == null) continue;
                    for (int i = 0; i < result.Count; i++)
                    {
                        try { result[i] = BooleanOperationsUtils.ExecuteBooleanOperation(result[i], hp, BooleanOperationsType.Difference); }
                        catch { }
                    }
                }
            return FacesOnPlane(result, origin, n);
        }

        /// <summary>Thin prism behind the plane with the loops as its front face (null when Revit refuses).</summary>
        private static Solid Prism(IList<CurveLoop> loops, XYZ n)
        {
            try { return GeometryCreationUtilities.CreateExtrusionGeometry(loops, -n, RevitUtil.M(0.01)); }
            catch { return null; }
        }

        /// <summary>Sequential Boolean union; a solid the union refuses stays as its own piece.</summary>
        internal static List<Solid> UnionAll(List<Solid> solids)
        {
            var rest = new List<Solid>();
            Solid acc = null;
            foreach (Solid p in solids)
            {
                if (p == null) continue;
                if (acc == null) { acc = p; continue; }
                try { acc = BooleanOperationsUtils.ExecuteBooleanOperation(acc, p, BooleanOperationsType.Union); }
                catch { rest.Add(p); }
            }
            if (acc != null) rest.Insert(0, acc);
            return rest;
        }

        /// <summary>Loops of the faces lying on the plane and facing the viewer (one list per island).</summary>
        internal static List<IList<CurveLoop>> FacesOnPlane(IEnumerable<Solid> solids, XYZ origin, XYZ n)
        {
            var faces = new List<IList<CurveLoop>>();
            foreach (Solid s in solids)
            {
                if (s == null) continue;
                foreach (Face f in s.Faces)
                {
                    if (!(f is PlanarFace pf) || pf.FaceNormal.DotProduct(n) < 0.999) continue;
                    if (Math.Abs((pf.Origin - origin).DotProduct(n)) > RevitUtil.M(0.001)) continue;
                    IList<CurveLoop> loops;
                    try { loops = pf.GetEdgesAsCurveLoops(); } catch { continue; }
                    if (loops != null && loops.Count > 0) faces.Add(loops);
                }
            }
            return faces;
        }

        /// <summary>The loop offset by d outward (grow) or inward; the side is checked by the area. null = Revit refused.</summary>
        private static CurveLoop Offset(CurveLoop loop, double d, bool grow, XYZ n, XYZ origin, XYZ ax, XYZ ay)
        {
            double a0 = Math.Abs(SignedArea(Poly(loop, origin, ax, ay)));
            foreach (double sign in new[] { 1.0, -1.0 })
            {
                try
                {
                    CurveLoop o = CurveLoop.CreateViaOffset(loop, sign * d, n);
                    double a = Math.Abs(SignedArea(Poly(o, origin, ax, ay)));
                    if (grow ? a > a0 : a < a0) return o;
                }
                catch { }
            }
            return null;
        }

        private static List<UV> Poly(CurveLoop loop, XYZ origin, XYZ ax, XYZ ay)
        {
            var pts = new List<UV>();
            foreach (Curve c in loop)
            {
                IList<XYZ> t;
                try { t = c.Tessellate(); } catch { continue; }
                for (int i = 0; i < t.Count - 1; i++)
                {
                    XYZ q = t[i] - origin;
                    pts.Add(new UV(q.DotProduct(ax), q.DotProduct(ay)));
                }
            }
            return pts;
        }

        internal static double SignedArea(List<UV> p)
        {
            double a = 0;
            for (int i = 0; i < p.Count; i++)
            {
                UV u = p[i], w = p[(i + 1) % p.Count];
                a += u.U * w.V - w.U * u.V;
            }
            return a / 2;
        }

        /// <summary>Point in polygon (even-odd).</summary>
        internal static bool Inside(UV pt, List<UV> poly)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                if ((poly[i].V > pt.V) != (poly[j].V > pt.V) &&
                    pt.U < (poly[j].U - poly[i].U) * (pt.V - poly[i].V) / (poly[j].V - poly[i].V) + poly[i].U)
                    inside = !inside;
            }
            return inside;
        }

        private bool Create(ElementId typeId, View v, List<CurveLoop> loops, ElementId lineStyle, List<string> problems)
        {
            try
            {
                FilledRegion fr = FilledRegion.Create(Doc, typeId, v.Id, OnViewPlane(v, loops));
                if (lineStyle != null && lineStyle != ElementId.InvalidElementId) { try { fr.SetLineStyleId(lineStyle); } catch { } }
                return true;
            }
            catch (Exception ex)
            {
                RevitUtil.AddOnce(problems, "Hachura não criada: " + ex.Message);
                return false;
            }
        }

        /// <summary>Plans: the loops moved to the level of the view (its work plane), where detail items
        /// are drawn; other views: unchanged (the loops already lie on the view plane).</summary>
        internal static List<CurveLoop> OnViewPlane(View v, IList<CurveLoop> loops)
        {
            var list = loops.ToList();
            if (!(v is ViewPlan plan) || plan.GenLevel == null || list.Count == 0) return list;
            XYZ p = list.SelectMany(l => l).Select(c => c.GetEndPoint(0)).FirstOrDefault();
            if (p == null) return list;
            double dz = plan.GenLevel.ProjectElevation - p.Z;
            if (Math.Abs(dz) < 1e-9) return list;
            Transform t = Transform.CreateTranslation(new XYZ(0, 0, dz));
            return list.Select(l => CurveLoop.CreateViaTransform(l, t)).ToList();
        }

        // ------------------------------------------------------------------ geometry
        /// <summary>Cut plane of the view: point, normal towards the viewer, and the two view axes.</summary>
        internal static bool CutPlane(Document doc, View v, out XYZ origin, out XYZ n, out XYZ ax, out XYZ ay)
        {
            origin = null; n = v.ViewDirection; ax = v.RightDirection; ay = v.UpDirection;
            if (n == null || n.GetLength() < 1e-9) return false;
            n = n.Normalize();
            if (v is ViewPlan plan)
            {
                try
                {
                    PlanViewRange range = plan.GetViewRange();
                    var lvl = doc.GetElement(range.GetLevelId(PlanViewPlane.CutPlane)) as Level ?? plan.GenLevel;
                    origin = new XYZ(0, 0, (lvl?.ProjectElevation ?? 0) + range.GetOffset(PlanViewPlane.CutPlane));
                }
                catch { return false; }
            }
            else origin = v.Origin;
            return origin != null;
        }

        /// <summary>Thin box (1 cm) just behind the cut plane, covering every chosen element.</summary>
        internal static Solid Slab(List<Element> elements, XYZ origin, XYZ n, XYZ ax, XYZ ay)
        {
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            foreach (Element e in elements)
            {
                BoundingBoxXYZ bb = null;
                try { bb = e.get_BoundingBox(null); } catch { }
                if (bb == null) continue;
                Transform tr = bb.Transform ?? Transform.Identity;
                foreach (double x in new[] { bb.Min.X, bb.Max.X })
                    foreach (double y in new[] { bb.Min.Y, bb.Max.Y })
                        foreach (double z in new[] { bb.Min.Z, bb.Max.Z })
                        {
                            XYZ q = tr.OfPoint(new XYZ(x, y, z)) - origin;
                            double u = q.DotProduct(ax), w = q.DotProduct(ay);
                            x0 = Math.Min(x0, u); x1 = Math.Max(x1, u);
                            y0 = Math.Min(y0, w); y1 = Math.Max(y1, w);
                        }
            }
            if (x0 > x1 || y0 > y1) return null;
            double m = RevitUtil.M(1.0), d = RevitUtil.M(0.01);
            x0 -= m; y0 -= m; x1 += m; y1 += m;
            XYZ b = origin - n * d;
            XYZ p0 = b + ax * x0 + ay * y0, p1 = b + ax * x1 + ay * y0, p2 = b + ax * x1 + ay * y1, p3 = b + ax * x0 + ay * y1;
            try
            {
                var loop = new CurveLoop();
                loop.Append(Line.CreateBound(p0, p1));
                loop.Append(Line.CreateBound(p1, p2));
                loop.Append(Line.CreateBound(p2, p3));
                loop.Append(Line.CreateBound(p3, p0));
                return GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { loop }, n, d);
            }
            catch { return null; }
        }

        /// <summary>Solids of an element as the view shows it (families: their instance geometry).</summary>
        internal static List<Solid> Solids(Element e, View v)
        {
            var list = new List<Solid>();
            GeometryElement ge = null;
            try { ge = e.get_Geometry(new Options { View = v }); } catch { }
            Collect(ge, list, 0);
            return list;
        }

        private static void Collect(GeometryElement ge, List<Solid> list, int depth)
        {
            if (ge == null || depth > 6) return;
            foreach (GeometryObject o in ge)
            {
                if (o is Solid s)
                {
                    double vol = 0;
                    try { vol = s.Volume; } catch { }
                    if (vol > 1e-9) list.Add(s);
                }
                else if (o is GeometryInstance gi)
                {
                    GeometryElement inner = null;
                    try { inner = gi.GetInstanceGeometry(); } catch { }
                    Collect(inner, list, depth + 1);
                }
            }
        }

        internal static ElementId InvisibleLineStyle(Document doc)
        {
            try
            {
                Category lines = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
                foreach (Category sub in lines.SubCategories)
                    if (sub.Id.Value == (long)BuiltInCategory.OST_InvisibleLines)
                        return sub.GetGraphicsStyle(GraphicsStyleType.Projection)?.Id;
            }
            catch { }
            return null;
        }
    }
}
