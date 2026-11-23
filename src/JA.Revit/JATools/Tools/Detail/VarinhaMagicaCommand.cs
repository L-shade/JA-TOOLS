using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using RevitDwgUnifier.Tools.Common;

namespace RevitDwgUnifier.Tools.Detail
{
    /// <summary>
    /// Varinha Mágica (like ArchiCAD's magic wand): a click inside a closed area creates a hatch,
    /// detail lines, a floor or a ceiling following its outline. One click, one element; Esc ends.
    /// Starts from the ribbon or with its keyboard shortcut (default Space); its options sit in a
    /// small panel in the corner of Revit (<see cref="VarinhaPainel"/>), not in a dialog.
    ///
    /// Where the outline comes from:
    ///  * "pelo ambiente" (plans): a temporary room at the click (rolled back at once) gives the
    ///    outline Revit sees from the walls, columns and room separators — closed at the doors, with
    ///    the holes of the columns inside;
    ///  * "pelas linhas e cortes" (any view): the lines drawn in the view, the outlines of the
    ///    hatches and the cut outline of the elements are put in a planar arrangement (split at every
    ///    crossing, loose ends removed) and the smallest closed area around the click is walked —
    ///    searched in a square that grows (3 m, 12 m, 48 m) until the area is closed inside it.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class VarinhaMagicaCommand : ToolCommand
    {
        public override string Title => "Varinha Mágica";
        protected override bool PicksInView => true;

        private enum Criar { Hachura, Linhas, Piso, Forro }

        protected override Result Run() => Sessao(false);

        /// <summary>
        /// One wand session: the small panel opens in the corner of Revit (no dialog first), every
        /// click inside a closed area creates one element with the panel's current choices, Esc
        /// (or the shortcut, or ✕) ends. Also started by the keyboard shortcut (<see cref="VarinhaAtalho"/>).
        /// </summary>
        internal Result Sessao(bool atalho)
        {
            View v = UiDoc?.ActiveView;
            if (v == null) return Result.Cancelled;
            bool plan = v is ViewPlan;
            bool ok = !v.IsTemplate && (plan || v is ViewSection || v.ViewType == ViewType.DraftingView || v.ViewType == ViewType.Detail);
            if (!ok)
            {
                if (!atalho) Show("Abra uma planta, um corte, uma elevação ou uma vista de desenho e rode de novo.", ResultKind.Info);
                return Result.Cancelled;
            }

            var regionTypes = new FilteredElementCollector(Doc).OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>()
                .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            var lineStyles = LineStyles(Doc);
            var floorTypes = plan ? new FilteredElementCollector(Doc).OfClass(typeof(FloorType)).Cast<FloorType>()
                .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList() : new List<FloorType>();
            var ceilingTypes = plan ? new FilteredElementCollector(Doc).OfClass(typeof(CeilingType)).Cast<CeilingType>()
                .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList() : new List<CeilingType>();

            // what the panel offers (floors and ceilings only in plans that have their types)
            var kinds = new List<Criar> { Criar.Hachura, Criar.Linhas };
            var grupos = new List<VarinhaPainel.Grupo>
            {
                new VarinhaPainel.Grupo { Rotulo = "Hachura", Chave = "region", Tipos = regionTypes.Select(x => x.Name).ToList(), SemTipos = "O projeto não tem tipo de região preenchida." },
                new VarinhaPainel.Grupo { Rotulo = "Linhas", Chave = "line", Tipos = lineStyles.Select(x => x.Value).ToList(), SemTipos = "Nenhum estilo de linha encontrado: usa o padrão." },
            };
            if (plan && floorTypes.Count > 0)
            {
                kinds.Add(Criar.Piso);
                grupos.Add(new VarinhaPainel.Grupo { Rotulo = "Piso", Chave = "floor", Tipos = floorTypes.Select(x => x.Name).ToList() });
            }
            if (plan && ceilingTypes.Count > 0)
            {
                kinds.Add(Criar.Forro);
                grupos.Add(new VarinhaPainel.Grupo { Rotulo = "Forro", Chave = "ceiling", Tipos = ceilingTypes.Select(x => x.Name).ToList() });
            }
            var borders = new List<string> { "Sem contorno (linhas invisíveis)", "Linha do tipo de região" };
            var modes = new List<string> { "Pelas linhas e cortes visíveis (como no ArchiCAD)" };
            if (plan) { modes.Insert(0, "Automático: pelo ambiente; se não fechar, pelas linhas"); modes.Add("Só pelo ambiente (paredes, pilares e separadores)"); }

            var painel = new VarinhaPainel(Owner, "VarinhaMagica" + (plan ? "P" : ""), grupos, borders, modes);
            int made = 0, missed = 0;
            var problems = new List<string>();
            var cache = new Dictionary<long, List<Seg>>();
            VarinhaAtalho.SessaoAtiva = true;
            try
            {
                painel.Mostrar();
                RevitUtil.EnsureWorkPlane(UiDoc);
                while (true)
                {
                    XYZ p;
                    // no snaps: the click is a point inside the area, not on a line
                    try { p = UiDoc.Selection.PickPoint(Autodesk.Revit.UI.Selection.ObjectSnapTypes.None, "Varinha mágica: clique dentro de uma área fechada (Esc termina)"); }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException) { break; }
                    catch (Exception ex) { RevitUtil.AddOnce(problems, "Clique: " + ex.Message); break; }

                    Criar kind = kinds[Math.Min(painel.Criar, kinds.Count - 1)];
                    int type = painel.Tipo;
                    if (kind == Criar.Hachura && regionTypes.Count == 0) { painel.Aviso("O projeto não tem tipo de região preenchida."); continue; }

                    bool byRoomFirst = plan && painel.Modo == 0;
                    bool roomOnly = plan && painel.Modo == 2;
                    List<CurveLoop> loops = null;
                    if (byRoomFirst || roomOnly) loops = RoomLoops((ViewPlan)v, p);
                    if (loops == null && !roomOnly) loops = GeometricLoops(v, p, cache);
                    if (loops == null || loops.Count == 0)
                    {
                        missed++;
                        painel.Contar(made, missed);
                        continue;
                    }

                    using (var t = new Transaction(Doc, "JA Tools - " + Title))
                    {
                        t.Start();
                        RevitDwgUnifier.Export.FailureSwallower.Attach(t);
                        try
                        {
                            List<CurveLoop> onPlane = HachuraUnicaCommand.OnViewPlane(v, loops);
                            switch (kind)
                            {
                                case Criar.Hachura:
                                    FilledRegion fr = FilledRegion.Create(Doc, regionTypes[Math.Min(type, regionTypes.Count - 1)].Id, v.Id, onPlane);
                                    ElementId inv = painel.Borda == 0 ? HachuraUnicaCommand.InvisibleLineStyle(Doc) : null;
                                    if (inv != null) { try { fr.SetLineStyleId(inv); } catch { } }
                                    break;
                                case Criar.Linhas:
                                    var style = lineStyles.Count > 0 ? Doc.GetElement(lineStyles[Math.Min(type, lineStyles.Count - 1)].Key) as GraphicsStyle : null;
                                    foreach (CurveLoop l in onPlane)
                                        foreach (Curve c in l)
                                        {
                                            DetailCurve dc = Doc.Create.NewDetailCurve(v, c);
                                            if (style != null) { try { dc.LineStyle = style; } catch { } }
                                        }
                                    break;
                                case Criar.Piso:
                                    Floor.Create(Doc, onPlane, floorTypes[Math.Min(type, floorTypes.Count - 1)].Id, ((ViewPlan)v).GenLevel.Id);
                                    break;
                                case Criar.Forro:
                                    Ceiling.Create(Doc, onPlane, ceilingTypes[Math.Min(type, ceilingTypes.Count - 1)].Id, ((ViewPlan)v).GenLevel.Id);
                                    break;
                            }
                            t.Commit();
                            made++;
                            painel.Aviso(null);
                        }
                        catch (Exception ex)
                        {
                            t.RollBack();
                            missed++;
                            RevitUtil.AddOnce(problems, "Não criado: " + ex.Message);
                            painel.Aviso("Não criado: " + ex.Message);
                        }
                    }
                    painel.Contar(made, missed);
                }
            }
            finally
            {
                VarinhaAtalho.SessaoAtiva = false;
                painel.Fechar();
            }

            // the panel already showed the count: a message only when something went wrong
            if (problems.Count > 0 || (made == 0 && missed > 0))
            {
                string text = $"{made} elemento(s) criado(s) pela varinha.";
                if (missed > 0) text += $"\n{missed} clique(s) fora de uma área fechada (ou contorno que o Revit recusou): nada criado nesses.";
                if (problems.Count > 0) text += "\n\n" + string.Join("\n", problems.Take(8));
                Show(text);
            }
            return made > 0 ? Result.Succeeded : Result.Cancelled;
        }

        private static List<KeyValuePair<ElementId, string>> LineStyles(Document doc)
        {
            var list = new List<KeyValuePair<ElementId, string>>();
            try
            {
                Category lines = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
                foreach (Category sub in lines.SubCategories)
                {
                    GraphicsStyle gs = sub.GetGraphicsStyle(GraphicsStyleType.Projection);
                    if (gs != null) list.Add(new KeyValuePair<ElementId, string>(gs.Id, sub.Name));
                }
            }
            catch { }
            return list.OrderBy(x => x.Value, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        // ================================================================== by the room
        /// <summary>Outline of a temporary room at the click (walls, columns, separators; rolled back).</summary>
        private List<CurveLoop> RoomLoops(ViewPlan plan, XYZ p)
        {
            Level lvl = plan.GenLevel;
            if (lvl == null) return null;
            List<CurveLoop> result = null;
            using (var t = new Transaction(Doc, "JA Tools - varinha (ambiente temporário)"))
            {
                t.Start();
                RevitDwgUnifier.Export.FailureSwallower.Attach(t);
                try
                {
                    Room room = Doc.Create.NewRoom(lvl, new UV(p.X, p.Y));
                    Doc.Regenerate();
                    if (room != null && room.Area > 1e-6)
                    {
                        var opts = new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish };
                        IList<IList<BoundarySegment>> segs = room.GetBoundarySegments(opts);
                        if (segs != null && segs.Count > 0)
                        {
                            result = new List<CurveLoop>();
                            foreach (IList<BoundarySegment> loop in segs)
                            {
                                var curves = loop.Select(s => s.GetCurve()).Where(c => c != null).ToList();
                                if (curves.Count < 2) continue;
                                try { result.Add(CurveLoop.Create(curves)); } catch { }
                            }
                            if (result.Count == 0) result = null;
                        }
                    }
                }
                catch { result = null; }
                t.RollBack();
            }
            return result;
        }

        // ================================================================== by the lines and cuts
        private sealed class Seg
        {
            public UV A, B;
        }

        private List<CurveLoop> GeometricLoops(View v, XYZ click, Dictionary<long, List<Seg>> cache)
        {
            if (!HachuraUnicaCommand.CutPlane(Doc, v, out XYZ origin, out XYZ n, out XYZ ax, out XYZ ay)) return null;
            if (v.ViewType == ViewType.DraftingView) origin = v.Origin ?? origin;
            UV pt = To2D(click, origin, ax, ay);
            double eps = RevitUtil.M(0.001);

            // lines of the view and outlines of hatches (read once per run)
            if (!cache.TryGetValue(-1, out List<Seg> drawn))
            {
                drawn = new List<Seg>();
                foreach (CurveElement ce in new FilteredElementCollector(Doc, v.Id).OfClass(typeof(CurveElement)).Cast<CurveElement>())
                {
                    try { AddCurve(drawn, ce.GeometryCurve, origin, ax, ay); } catch { }
                }
                foreach (FilledRegion fr in new FilteredElementCollector(Doc, v.Id).OfClass(typeof(FilledRegion)).Cast<FilledRegion>())
                {
                    try { foreach (CurveLoop l in fr.GetBoundaries()) foreach (Curve c in l) AddCurve(drawn, c, origin, ax, ay); } catch { }
                }
                cache[-1] = drawn;
            }

            bool cuts = v.ViewType != ViewType.DraftingView;
            double[] radii = { RevitUtil.M(3), RevitUtil.M(12), RevitUtil.M(48) };
            for (int ri = 0; ri < radii.Length; ri++)
            {
                double r = radii[ri];
                var segs = drawn.Where(s => Near(s, pt, r)).ToList();
                if (cuts)
                {
                    foreach (Element e in ElementsNear(v, origin, n, ax, ay, pt, r))
                    {
                        if (!cache.TryGetValue(e.Id.Value, out List<Seg> own))
                        {
                            own = CutSegments(e, v, origin, n, ax, ay);
                            cache[e.Id.Value] = own;
                        }
                        segs.AddRange(own.Where(s => Near(s, pt, r)));
                    }
                }
                List<UV> poly = FindFace(segs, pt, eps);
                if (poly == null) continue;
                double minU = poly.Min(q => q.U), maxU = poly.Max(q => q.U), minV = poly.Min(q => q.V), maxV = poly.Max(q => q.V);
                bool closedInside = minU > pt.U - r + eps && maxU < pt.U + r - eps && minV > pt.V - r + eps && maxV < pt.V + r - eps;
                if (closedInside || ri == radii.Length - 1) return ToLoops(poly, origin, ax, ay);
            }
            return null;
        }

        private static UV To2D(XYZ p, XYZ origin, XYZ ax, XYZ ay)
        {
            XYZ q = p - origin;
            return new UV(q.DotProduct(ax), q.DotProduct(ay));
        }

        private static void AddCurve(List<Seg> segs, Curve c, XYZ origin, XYZ ax, XYZ ay)
        {
            if (c == null) return;
            IList<XYZ> pts = c.Tessellate();
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                UV a = To2D(pts[i], origin, ax, ay), b = To2D(pts[i + 1], origin, ax, ay);
                if (a.DistanceTo(b) > 1e-6) segs.Add(new Seg { A = a, B = b });
            }
        }

        private static bool Near(Seg s, UV pt, double r) =>
            Math.Max(s.A.U, s.B.U) >= pt.U - r && Math.Min(s.A.U, s.B.U) <= pt.U + r &&
            Math.Max(s.A.V, s.B.V) >= pt.V - r && Math.Min(s.A.V, s.B.V) <= pt.V + r;

        /// <summary>Model elements of the view whose box reaches the search square.</summary>
        private IEnumerable<Element> ElementsNear(View v, XYZ origin, XYZ n, XYZ ax, XYZ ay, UV pt, double r)
        {
            double depth = RevitUtil.M(200);
            var corners = new List<XYZ>();
            foreach (double du in new[] { -r, r })
                foreach (double dv in new[] { -r, r })
                    foreach (double dn in new[] { -depth, depth })
                        corners.Add(origin + ax * (pt.U + du) + ay * (pt.V + dv) + n * dn);
            var min = new XYZ(corners.Min(c => c.X), corners.Min(c => c.Y), corners.Min(c => c.Z));
            var max = new XYZ(corners.Max(c => c.X), corners.Max(c => c.Y), corners.Max(c => c.Z));
            IList<Element> found;
            try
            {
                found = new FilteredElementCollector(Doc, v.Id).WhereElementIsNotElementType()
                    .WherePasses(new BoundingBoxIntersectsFilter(new Outline(min, max))).ToElements();
            }
            catch { yield break; }
            foreach (Element e in found)
                if (e.Category != null && e.Category.CategoryType == CategoryType.Model && !(e is View)) yield return e;
        }

        /// <summary>Cut outline of one element on the plane of the view (empty when the plane misses it).</summary>
        private static List<Seg> CutSegments(Element e, View v, XYZ origin, XYZ n, XYZ ax, XYZ ay)
        {
            var segs = new List<Seg>();
            try
            {
                Solid slab = HachuraUnicaCommand.Slab(new List<Element> { e }, origin, n, ax, ay);
                if (slab == null) return segs;
                var parts = new List<Solid>();
                foreach (Solid s in HachuraUnicaCommand.Solids(e, v))
                {
                    try
                    {
                        Solid part = BooleanOperationsUtils.ExecuteBooleanOperation(s, slab, BooleanOperationsType.Intersect);
                        if (part != null && part.Volume > 1e-9) parts.Add(part);
                    }
                    catch { }
                }
                if (parts.Count == 0) return segs;
                foreach (IList<CurveLoop> face in HachuraUnicaCommand.FacesOnPlane(HachuraUnicaCommand.UnionAll(parts), origin, n))
                    foreach (CurveLoop l in face)
                        foreach (Curve c in l) AddCurve(segs, c, origin, ax, ay);
            }
            catch { }
            return segs;
        }

        // ------------------------------------------------------------------ planar arrangement
        /// <summary>
        /// The closed area around pt: segments split at every crossing and touch, vertices merged
        /// (eps), loose ends removed; a ray to the right finds the nearest edge and the face on its
        /// left is walked (at each vertex, the next edge clockwise from the one we came by).
        /// Returns the outline counter-clockwise, or null when pt is not in a closed area.
        /// </summary>
        private static List<UV> FindFace(List<Seg> segs, UV pt, double eps)
        {
            if (segs.Count < 3) return null;
            // ---- split points
            var cuts = segs.Select(s => new List<double> { 0, 1 }).ToList();
            for (int i = 0; i < segs.Count; i++)
            {
                Seg a = segs[i];
                double aMinU = Math.Min(a.A.U, a.B.U) - eps, aMaxU = Math.Max(a.A.U, a.B.U) + eps;
                double aMinV = Math.Min(a.A.V, a.B.V) - eps, aMaxV = Math.Max(a.A.V, a.B.V) + eps;
                for (int j = i + 1; j < segs.Count; j++)
                {
                    Seg b = segs[j];
                    if (Math.Max(b.A.U, b.B.U) < aMinU || Math.Min(b.A.U, b.B.U) > aMaxU ||
                        Math.Max(b.A.V, b.B.V) < aMinV || Math.Min(b.A.V, b.B.V) > aMaxV) continue;
                    if (Cross(a, b, eps, out double ta, out double tb)) { cuts[i].Add(ta); cuts[j].Add(tb); }
                    // touches and overlaps: an end of one on the other
                    AddIfOn(a, b.A, eps, cuts[i]); AddIfOn(a, b.B, eps, cuts[i]);
                    AddIfOn(b, a.A, eps, cuts[j]); AddIfOn(b, a.B, eps, cuts[j]);
                }
            }

            // ---- vertices and edges
            // vertices merged within eps: a grid of eps cells, the 9 cells around each point are checked
            // (two points a hair apart on each side of a cell border are still the same vertex)
            var grid = new Dictionary<(long, long), List<int>>();
            var pos = new List<UV>();
            Func<UV, int> vertex = q =>
            {
                long ku = (long)Math.Floor(q.U / eps), kv = (long)Math.Floor(q.V / eps);
                for (long du = -1; du <= 1; du++)
                    for (long dv = -1; dv <= 1; dv++)
                        if (grid.TryGetValue((ku + du, kv + dv), out List<int> cell))
                            foreach (int found in cell)
                                if (pos[found].DistanceTo(q) <= eps) return found;
                int id = pos.Count;
                pos.Add(q);
                if (!grid.TryGetValue((ku, kv), out List<int> mine)) grid[(ku, kv)] = mine = new List<int>();
                mine.Add(id);
                return id;
            };
            var edges = new HashSet<long>();
            var adj = new Dictionary<int, HashSet<int>>();
            void Link(int x, int y)
            {
                if (x == y) return;
                long k = x < y ? ((long)x << 32) | (uint)y : ((long)y << 32) | (uint)x;
                if (!edges.Add(k)) return;
                if (!adj.TryGetValue(x, out var sx)) adj[x] = sx = new HashSet<int>();
                if (!adj.TryGetValue(y, out var sy)) adj[y] = sy = new HashSet<int>();
                sx.Add(y); sy.Add(x);
            }
            for (int i = 0; i < segs.Count; i++)
            {
                Seg s = segs[i];
                var ts = cuts[i].Where(t => t >= -1e-9 && t <= 1 + 1e-9).Distinct().OrderBy(t => t).ToList();
                int prev = -1;
                foreach (double t in ts)
                {
                    int id = vertex(new UV(s.A.U + (s.B.U - s.A.U) * t, s.A.V + (s.B.V - s.A.V) * t));
                    if (prev >= 0) Link(prev, id);
                    prev = id;
                }
            }

            // ---- loose ends out (repeat until none)
            var dead = new Queue<int>(adj.Where(kv => kv.Value.Count <= 1).Select(kv => kv.Key));
            while (dead.Count > 0)
            {
                int d = dead.Dequeue();
                if (!adj.TryGetValue(d, out var nb)) continue;
                foreach (int o in nb.ToList())
                {
                    adj[o].Remove(d);
                    if (adj[o].Count == 1) dead.Enqueue(o);
                    if (adj[o].Count == 0) adj.Remove(o);
                }
                adj.Remove(d);
            }
            if (adj.Count < 3) return null;

            // ---- neighbours sorted counter-clockwise
            var order = new Dictionary<int, List<int>>();
            foreach (var kv in adj)
            {
                UV c = pos[kv.Key];
                order[kv.Key] = kv.Value.OrderBy(o => Math.Atan2(pos[o].V - c.V, pos[o].U - c.U)).ToList();
            }

            // ---- nearest edge crossed by the ray to the right; start upwards (the area is on its left)
            int sa = -1, sb = -1;
            double best = double.MaxValue;
            foreach (var kv in adj)
                foreach (int o in kv.Value)
                {
                    if (o <= kv.Key) continue;
                    UV a = pos[kv.Key], b = pos[o];
                    if ((a.V > pt.V) == (b.V > pt.V)) continue;
                    double x = a.U + (pt.V - a.V) * (b.U - a.U) / (b.V - a.V);
                    if (x <= pt.U || x >= best) continue;
                    best = x;
                    if (a.V < b.V) { sa = kv.Key; sb = o; } else { sa = o; sb = kv.Key; }
                }
            if (sa < 0) return null;

            // ---- walk the face
            var poly = new List<UV> { pos[sa] };
            int from = sa, to = sb, guard = edges.Count * 2 + 10;
            while (guard-- > 0)
            {
                poly.Add(pos[to]);
                List<int> nbs = order[to];
                int k = nbs.IndexOf(from);
                if (k < 0) return null;
                int next = nbs[(k - 1 + nbs.Count) % nbs.Count];
                from = to;
                to = next;
                if (from == sa && to == sb) break;
            }
            if (guard <= 0) return null;
            poly.RemoveAt(poly.Count - 1);                 // the start again

            poly = Simplify(poly, eps);
            if (poly.Count < 3 || HachuraUnicaCommand.SignedArea(poly) <= eps * eps || !HachuraUnicaCommand.Inside(pt, poly)) return null;
            return poly;
        }

        private static bool Cross(Seg a, Seg b, double eps, out double ta, out double tb)
        {
            ta = tb = 0;
            double rx = a.B.U - a.A.U, ry = a.B.V - a.A.V, sx = b.B.U - b.A.U, sy = b.B.V - b.A.V;
            double den = rx * sy - ry * sx;
            if (Math.Abs(den) < 1e-12) return false;            // parallel (touches handled apart)
            double qx = b.A.U - a.A.U, qy = b.A.V - a.A.V;
            ta = (qx * sy - qy * sx) / den;
            tb = (qx * ry - qy * rx) / den;
            double la = Math.Sqrt(rx * rx + ry * ry), lb = Math.Sqrt(sx * sx + sy * sy);
            double ea = la > 0 ? eps / la : 0, eb = lb > 0 ? eps / lb : 0;
            return ta >= -ea && ta <= 1 + ea && tb >= -eb && tb <= 1 + eb;
        }

        private static void AddIfOn(Seg s, UV q, double eps, List<double> cuts)
        {
            double rx = s.B.U - s.A.U, ry = s.B.V - s.A.V, l2 = rx * rx + ry * ry;
            if (l2 < 1e-18) return;
            double t = ((q.U - s.A.U) * rx + (q.V - s.A.V) * ry) / l2;
            if (t <= 0 || t >= 1) return;
            double px = s.A.U + rx * t - q.U, py = s.A.V + ry * t - q.V;
            if (px * px + py * py <= eps * eps) cuts.Add(t);
        }

        /// <summary>Repeated and straight-through vertices removed.</summary>
        private static List<UV> Simplify(List<UV> poly, double eps)
        {
            var p = new List<UV>();
            foreach (UV q in poly) if (p.Count == 0 || p[p.Count - 1].DistanceTo(q) > eps) p.Add(q);
            if (p.Count > 1 && p[0].DistanceTo(p[p.Count - 1]) <= eps) p.RemoveAt(p.Count - 1);
            bool changed = true;
            while (changed && p.Count > 3)
            {
                changed = false;
                for (int i = 0; i < p.Count && p.Count > 3; i++)
                {
                    UV a = p[(i - 1 + p.Count) % p.Count], b = p[i], c = p[(i + 1) % p.Count];
                    double cross = (b.U - a.U) * (c.V - b.V) - (b.V - a.V) * (c.U - b.U);
                    double len = a.DistanceTo(b) * b.DistanceTo(c);
                    if (len < 1e-18 || Math.Abs(cross) <= 1e-9 * Math.Max(1, len)) { p.RemoveAt(i); changed = true; i--; }
                }
            }
            return p;
        }

        private List<CurveLoop> ToLoops(List<UV> poly, XYZ origin, XYZ ax, XYZ ay)
        {
            double tol = Doc.Application.ShortCurveTolerance;
            var pts = poly.Select(q => origin + ax * q.U + ay * q.V).ToList();
            var loop = new CurveLoop();
            for (int i = 0; i < pts.Count; i++)
            {
                XYZ a = pts[i], b = pts[(i + 1) % pts.Count];
                if (a.DistanceTo(b) <= tol) return null;           // Revit refuses such a short side
                loop.Append(Line.CreateBound(a, b));
            }
            return new List<CurveLoop> { loop };
        }
    }
}
