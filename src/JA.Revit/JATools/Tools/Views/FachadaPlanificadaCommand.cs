using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDwgUnifier.Infrastructure;
using RevitDwgUnifier.Tools.Common;

namespace RevitDwgUnifier.Tools.Views
{
    /// <summary>
    /// Fachada Planificada: an elevation of a facade with several angles, unfolded. Every straight
    /// stretch becomes a section seen square-on (true size), and the sections are glued side by
    /// side on a sheet, bottoms aligned — as if the facade were flattened.
    ///
    /// The facade comes from the selected walls (chained in order; curved walls are split into
    /// chords) or, with nothing selected, from points clicked on the plan (Esc ends) plus a click
    /// on the side it is seen from.
    ///
    /// Each section: origin on the stretch moved "afastamento" in front of the facade (walls: in
    /// front of the exterior face), X along the stretch, crop exactly the stretch length, height
    /// from the lowest to the highest level (+ margins), far clip "profundidade" behind the line.
    /// The views are live (they follow the model). On the sheet each viewport is moved so the
    /// start of its stretch at the bottom of the crop lands where the previous one ended.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class FachadaPlanificadaCommand : ToolCommand
    {
        public override string Title => "Fachada Planificada";
        protected override bool PicksInView => true;

        private const double ChordDeg = 15;          // curved walls: one stretch per 15°

        /// <summary>A straight stretch of the facade: a → b (plan), seen from the right of a→b after orienting.</summary>
        private sealed class Trecho
        {
            public XYZ A, B;
            public double Offset;                    // from the line to the facade face (walls: half thickness)
            public double Length => A.DistanceTo(B);
            public XYZ Dir => (B - A).Normalize();
        }

        protected override Result Run()
        {
            // ---------------------------------------------------------------- source
            List<Wall> walls = UiDoc.Selection.GetElementIds().Select(id => Doc.GetElement(id)).OfType<Wall>()
                .Where(w => w.Location is LocationCurve).ToList();
            bool fromWalls = walls.Count > 0;
            if (!fromWalls && !(Doc.ActiveView is ViewPlan))
            {
                Show("Selecione as paredes da fachada, ou abra uma planta para desenhar a fachada com cliques.");
                return Result.Cancelled;
            }

            var types = new FilteredElementCollector(Doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .Where(t => t.ViewFamily == ViewFamily.Section).OrderBy(t => t.Name).ToList();
            if (types.Count == 0) { Show("O projeto não tem tipo de vista de corte."); return Result.Cancelled; }
            var templates = RevitUtil.Templates(Doc, Autodesk.Revit.DB.ViewType.Section, Autodesk.Revit.DB.ViewType.Elevation);
            var tbs = new FilteredElementCollector(Doc).OfCategory(BuiltInCategory.OST_TitleBlocks).OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>().OrderBy(s => s.FamilyName).ThenBy(s => s.Name).ToList();

            // ---------------------------------------------------------------- options
            var dlg = new OptionsDialog(Title, "FachadaPlanificada");
            var fName = dlg.Text("name", "Nome da fachada", "FACHADA A");
            var fType = dlg.Choice("type", "Tipo de vista (corte)", types.Select(t => t.Name).ToList());
            var fTemplate = dlg.Choice("template", "Modelo de vista", RevitUtil.TemplateChoices(templates));
            var fScale = dlg.Number("scale", "Escala 1:", 100, 1, 10000);
            dlg.Section("Recorte");
            var fSide = fromWalls
                ? dlg.Choice("side", "Ver as paredes de", new List<string> { "Fora (lado exterior)", "Dentro (lado interior)" })
                : null;
            var fFront = dlg.Number("front", "Afastamento à frente da fachada (m)", 1.0, 0, 100,
                "Plano de corte à frente da face. Aumente para mostrar varandas e marquises; diminua se outra ala aparecer na frente.");
            var fDepth = dlg.Number("depth", "Profundidade atrás da fachada (m)", 3.0, 0.1, 1000);
            var fBelow = dlg.Number("below", "Abaixo do nível mais baixo (m)", 0.5, 0, 100);
            var fAbove = dlg.Number("above", "Acima do nível mais alto (m)", 2.0, 0, 100);
            dlg.Section("Prancha");
            var fSheet = dlg.Check("sheet", "Montar a fachada numa prancha nova", true);
            var tbChoices = new List<string> { "(sem carimbo)" };
            tbChoices.AddRange(tbs.Select(s => s.FamilyName + " : " + s.Name));
            var fTb = dlg.Choice("tb", "Carimbo", tbChoices, tbChoices.Count > 1 ? 1 : 0);
            fTb.EnabledWhen(fSheet);
            var fFolds = dlg.Check("folds", "Linha tracejada em cada dobra", true);
            fFolds.EnabledWhen(fSheet);
            var fNoTitles = dlg.Check("notitles", "Sem título embaixo de cada trecho", true);
            fNoTitles.EnabledWhen(fSheet);
            var fLevels = dlg.Check("levels", "Níveis só no primeiro trecho", true,
                "Esconde as linhas de nível nos outros trechos (não funciona se o modelo de vista controlar a visibilidade).");
            dlg.Note(fromWalls
                ? $"{walls.Count} parede(s) selecionada(s): a fachada segue a ordem em que elas se encostam. Paredes curvas viram trechos de até {ChordDeg:0}°."
                : "Depois do OK: clique os cantos da fachada na planta, em ordem (Esc termina), e depois o lado de onde ela é vista.");
            if (!dlg.Show(Owner)) return Result.Cancelled;

            var problems = new List<string>();

            // ---------------------------------------------------------------- the facade line
            List<Trecho> trechos;
            if (fromWalls)
            {
                trechos = FromWalls(walls, fSide.Index == 1, problems);
            }
            else
            {
                trechos = FromClicks(problems);
                if (trechos == null) return Result.Cancelled;
            }
            trechos = Clean(trechos);
            if (trechos.Count == 0) { Show("Nenhum trecho com comprimento suficiente."); return Result.Cancelled; }

            // ---------------------------------------------------------------- views
            List<Level> levels = RevitUtil.Levels(Doc);
            double zMin = (levels.Count > 0 ? levels.First().ProjectElevation : 0) - RevitUtil.M(fBelow.Value);
            double zMax = (levels.Count > 0 ? levels.Last().ProjectElevation : RevitUtil.M(3)) + RevitUtil.M(fAbove.Value);
            if (zMax - zMin < RevitUtil.M(1)) zMax = zMin + RevitUtil.M(3);
            int scale = Math.Max(1, (int)fScale.Value);
            string baseName = string.IsNullOrWhiteSpace(fName.Value) ? "FACHADA" : fName.Value.Trim();

            var views = new List<ViewSection>();
            ViewSheet sheet = null;
            using (var tg = new TransactionGroup(Doc, "JA Tools - " + Title))
            {
                tg.Start();
                using (var t = new Transaction(Doc, "JA Tools - " + Title + " · vistas"))
                {
                    t.Start();
                    ElementId template = RevitUtil.TemplateFromChoice(templates, fTemplate.Index);
                    for (int i = 0; i < trechos.Count; i++)
                    {
                        var tr = trechos[i];
                        try
                        {
                            ViewSection v = CreateSection(types[fType.Index].Id, tr, zMin, zMax,
                                                          tr.Offset + RevitUtil.M(fFront.Value), RevitUtil.M(fDepth.Value));
                            RevitUtil.Configure(v, scale, template);
                            v.Name = RevitUtil.UniqueViewName(Doc, $"{baseName} - {i + 1:00}");
                            try { v.CropBoxActive = true; v.CropBoxVisible = false; } catch { }
                            if (fLevels.Value && i > 0)
                            {
                                try { v.SetCategoryHidden(new ElementId(BuiltInCategory.OST_Levels), true); }
                                catch { RevitUtil.AddOnce(problems, "Níveis: o modelo de vista controla a visibilidade, eles aparecem em todos os trechos."); }
                            }
                            views.Add(v);
                        }
                        catch (Exception ex)
                        {
                            problems.Add($"Trecho {i + 1}: vista não criada ({ex.Message}).");
                            views.Add(null);
                        }
                    }
                    t.Commit();
                }

                // ------------------------------------------------------------ sheet
                if (fSheet.Value && views.Any(v => v != null))
                {
                    using (var t = new Transaction(Doc, "JA Tools - " + Title + " · prancha"))
                    {
                        t.Start();
                        try
                        {
                            FamilySymbol tb = fTb.Index > 0 ? tbs[fTb.Index - 1] : null;
                            sheet = Assemble(trechos, views, tb, scale, zMin, zMax, baseName, fFolds.Value, fNoTitles.Value, problems);
                            t.Commit();
                        }
                        catch (Exception ex)
                        {
                            t.RollBack();
                            problems.Add("Prancha não montada: " + ex.Message);
                        }
                    }
                }
                tg.Assimilate();
            }

            int ok = views.Count(v => v != null);
            double total = trechos.Sum(x => x.Length);
            string text = $"{ok} trecho(s) criado(s), fachada planificada com {RevitUtil.ToM(total):0.00} m.";
            if (sheet != null) text += $"\nPrancha: {sheet.SheetNumber} - {sheet.Name}";
            text += "\n" + string.Join("\n", views.Where(v => v != null).Select((v, i) =>
                        $"  • {v.Name}  ({RevitUtil.ToM(trechos[i].Length):0.00} m)"));
            if (problems.Count > 0) text += "\n\nAvisos:\n" + string.Join("\n", problems.Distinct().Select(p => "  • " + p));
            Show(text);
            if (sheet != null) TryOpen(sheet);
            else TryOpen(views.FirstOrDefault(v => v != null));
            return ok > 0 ? Result.Succeeded : Result.Failed;
        }

        // ==================================================================== input

        /// <summary>Walls chained end to end; the stretches run so the facade is seen from their right.</summary>
        private List<Trecho> FromWalls(List<Wall> walls, bool inside, List<string> problems)
        {
            // each wall as a polyline (curves split in chords) with its half thickness
            var parts = new List<(List<XYZ> pts, double half, Wall wall)>();
            foreach (Wall w in walls)
            {
                Curve c = ((LocationCurve)w.Location).Curve;
                var pts = new List<XYZ>();
                if (c is Line) { pts.Add(RevitUtil.Flat(c.GetEndPoint(0))); pts.Add(RevitUtil.Flat(c.GetEndPoint(1))); }
                else if (c is Arc arc)
                {
                    double ang = Math.Abs(arc.GetEndParameter(1) - arc.GetEndParameter(0)) * 180 / Math.PI;
                    int n = Math.Max(1, (int)Math.Ceiling(ang / ChordDeg));
                    for (int i = 0; i <= n; i++) pts.Add(RevitUtil.Flat(arc.Evaluate((double)i / n, true)));
                }
                else pts.AddRange(c.Tessellate().Select(RevitUtil.Flat));
                if (pts.Count >= 2) parts.Add((pts, w.Width / 2, w));
            }
            if (parts.Count == 0) return new List<Trecho>();

            // chain: start at an open end, then always the nearest free end
            double tol = RevitUtil.M(1.5);
            int start = 0, startEnd = 0;
            double best = -1;
            for (int i = 0; i < parts.Count; i++)
                for (int e = 0; e < 2; e++)
                {
                    XYZ p = e == 0 ? parts[i].pts[0] : parts[i].pts.Last();
                    double near = double.MaxValue;
                    for (int j = 0; j < parts.Count; j++)
                        if (j != i) near = Math.Min(near, Math.Min(p.DistanceTo(parts[j].pts[0]), p.DistanceTo(parts[j].pts.Last())));
                    if (near > best) { best = near; start = i; startEnd = e; }
                }
            if (startEnd == 1) parts[start].pts.Reverse();   // the chain starts at the free end

            var chain = new List<(XYZ p, double half)>();
            var used = new HashSet<int> { start };
            foreach (XYZ p in parts[start].pts) chain.Add((p, parts[start].half));
            while (used.Count < parts.Count)
            {
                XYZ end = chain.Last().p;
                int next = -1; bool rev = false; double d = double.MaxValue;
                for (int j = 0; j < parts.Count; j++)
                {
                    if (used.Contains(j)) continue;
                    double d0 = end.DistanceTo(parts[j].pts[0]), d1 = end.DistanceTo(parts[j].pts.Last());
                    if (d0 < d) { d = d0; next = j; rev = false; }
                    if (d1 < d) { d = d1; next = j; rev = true; }
                }
                used.Add(next);
                var pts = new List<XYZ>(parts[next].pts);
                if (rev) pts.Reverse();
                if (d > tol) problems.Add($"Parede {parts[next].wall.Id}: não encosta na anterior ({RevitUtil.ToM(d):0.00} m); a fachada foi ligada mesmo assim.");
                // corner where the two lines meet (walls joined by their faces stop short of it)
                XYZ corner = Meet(chain[chain.Count - 2].p, end, pts[0], pts[1]);
                if (corner != null && corner.DistanceTo(end) < tol && corner.DistanceTo(pts[0]) < tol)
                {
                    chain[chain.Count - 1] = (corner, chain.Last().half);
                    pts[0] = corner;
                }
                // the point that starts this wall carries this wall's thickness for the next stretch
                if (pts[0].DistanceTo(chain.Last().p) < 1e-6) chain[chain.Count - 1] = (chain.Last().p, parts[next].half);
                else chain.Add((pts[0], parts[next].half));
                for (int k = 1; k < pts.Count; k++) chain.Add((pts[k], parts[next].half));
            }

            // closed outline (the whole building): close the ring at the corner
            if (parts.Count >= 3 && chain.Last().p.DistanceTo(chain[0].p) < tol)
            {
                XYZ corner = Meet(chain[chain.Count - 2].p, chain.Last().p, chain[0].p, chain[1].p);
                if (corner != null) { chain[0] = (corner, chain[0].half); chain[chain.Count - 1] = (corner, chain.Last().half); }
            }

            var trechos = new List<Trecho>();
            for (int i = 0; i + 1 < chain.Count; i++)
                trechos.Add(new Trecho { A = chain[i].p, B = chain[i + 1].p, Offset = chain[i].half });

            // which side is outside: the walls' exterior (Orientation) by vote of length
            double vote = 0;
            foreach (var part in parts)
            {
                if (!(((LocationCurve)part.wall.Location).Curve is Line)) continue;
                XYZ o = part.wall.Orientation;
                foreach (var tr in trechos)
                {
                    XYZ mid = (tr.A + tr.B) / 2;
                    if (((LocationCurve)part.wall.Location).Curve.Distance(new XYZ(mid.X, mid.Y, ((LocationCurve)part.wall.Location).Curve.GetEndPoint(0).Z)) > RevitUtil.M(0.05)) continue;
                    vote += o.DotProduct(Left(tr.Dir)) * tr.Length;
                }
            }
            if (vote == 0)
            {
                // only curved walls: outside = away from the centre of the outline
                XYZ c = trechos.Aggregate(XYZ.Zero, (s, x) => s + x.A) / trechos.Count;
                foreach (var tr in trechos) vote += ((tr.A + tr.B) / 2 - c).DotProduct(Left(tr.Dir)) * tr.Length;
            }
            bool outsideOnLeft = vote > 0;
            if (inside) outsideOnLeft = !outsideOnLeft;
            return outsideOnLeft ? Reverse(trechos) : trechos;
        }

        /// <summary>Corners clicked on the plan (temporary lines show the path), then the side seen from.</summary>
        private List<Trecho> FromClicks(List<string> problems)
        {
            var pts = new List<XYZ>();
            var temp = new List<ElementId>();
            View plan = Doc.ActiveView;
            try
            {
                while (true)
                {
                    XYZ p;
                    try
                    {
                        p = RevitUtil.PickPoint(UiDoc, pts.Count == 0 ? "Fachada: clique o primeiro canto"
                                                                       : $"Canto {pts.Count + 1} (Esc termina)");
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException) { break; }
                    p = RevitUtil.Flat(p);
                    if (pts.Count > 0 && p.DistanceTo(pts.Last()) < RevitUtil.M(0.05)) continue;
                    pts.Add(p);
                    if (pts.Count >= 2) temp.Add(TempLine(plan, pts[pts.Count - 2], p));
                }
                if (pts.Count < 2) return null;

                XYZ side;
                try { side = RevitUtil.Flat(RevitUtil.PickPoint(UiDoc, "Clique o lado de onde a fachada é vista")); }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return null; }

                var trechos = new List<Trecho>();
                for (int i = 0; i + 1 < pts.Count; i++) trechos.Add(new Trecho { A = pts[i], B = pts[i + 1], Offset = 0 });
                // the stretch nearest to the click says which side is outside
                Trecho near = trechos.OrderBy(tr => DistToSegment(side, tr.A, tr.B)).First();
                bool outsideOnLeft = (side - near.A).DotProduct(Left(near.Dir)) > 0;
                return outsideOnLeft ? Reverse(trechos) : trechos;
            }
            finally
            {
                if (temp.Count > 0)
                {
                    using (var t = new Transaction(Doc, "JA Tools - linhas temporárias"))
                    {
                        t.Start();
                        try { Doc.Delete(temp); } catch { }
                        t.Commit();
                    }
                }
            }
        }

        private ElementId TempLine(View plan, XYZ a, XYZ b)
        {
            using (var t = new Transaction(Doc, "JA Tools - linha temporária"))
            {
                t.Start();
                try
                {
                    double z = plan.GenLevel != null ? plan.GenLevel.ProjectElevation : 0;
                    DetailCurve dc = Doc.Create.NewDetailCurve(plan, Line.CreateBound(new XYZ(a.X, a.Y, z), new XYZ(b.X, b.Y, z)));
                    t.Commit();
                    UiDoc.RefreshActiveView();
                    return dc.Id;
                }
                catch { t.RollBack(); return ElementId.InvalidElementId; }
            }
        }

        // ==================================================================== geometry

        private static XYZ Left(XYZ d) => XYZ.BasisZ.CrossProduct(d);

        private static List<Trecho> Reverse(List<Trecho> list)
        {
            var r = new List<Trecho>();
            for (int i = list.Count - 1; i >= 0; i--) r.Add(new Trecho { A = list[i].B, B = list[i].A, Offset = list[i].Offset });
            return r;
        }

        /// <summary>Joins collinear neighbours and drops stretches shorter than 5 cm.</summary>
        private static List<Trecho> Clean(List<Trecho> list)
        {
            var r = new List<Trecho>();
            foreach (var tr in list.Where(x => x.Length > RevitUtil.M(0.05)))
            {
                if (r.Count > 0)
                {
                    var last = r[r.Count - 1];
                    if (last.Dir.DotProduct(tr.Dir) > Math.Cos(0.5 * Math.PI / 180) && last.B.DistanceTo(tr.A) < RevitUtil.M(0.01))
                    {
                        last.B = tr.B;
                        last.Offset = Math.Max(last.Offset, tr.Offset);
                        continue;
                    }
                }
                r.Add(new Trecho { A = tr.A, B = tr.B, Offset = tr.Offset });
            }
            return r;
        }

        /// <summary>Intersection of the lines a0-a1 and b0-b1 (plan), null if parallel.</summary>
        private static XYZ Meet(XYZ a0, XYZ a1, XYZ b0, XYZ b1)
        {
            XYZ d0 = a1 - a0, d1 = b1 - b0;
            double den = d0.X * d1.Y - d0.Y * d1.X;
            if (Math.Abs(den) < 1e-9 * d0.GetLength() * d1.GetLength()) return null;
            double t = ((b0.X - a0.X) * d1.Y - (b0.Y - a0.Y) * d1.X) / den;
            return new XYZ(a0.X + d0.X * t, a0.Y + d0.Y * t, 0);
        }

        private static double DistToSegment(XYZ p, XYZ a, XYZ b)
        {
            XYZ d = b - a;
            double l2 = d.DotProduct(d);
            double t = l2 < 1e-12 ? 0 : Math.Max(0, Math.Min(1, (p - a).DotProduct(d) / l2));
            return p.DistanceTo(a + d * t);
        }

        /// <summary>
        /// Section seen square-on from the right of a→b. Box: X along a→b (0..L), Y up (zMin..zMax),
        /// Z toward the viewer; cut plane (Max.Z = 0) <paramref name="front"/> in front of the line,
        /// far clip <paramref name="depth"/> behind it.
        /// </summary>
        private ViewSection CreateSection(ElementId typeId, Trecho tr, double zMin, double zMax, double front, double depth)
        {
            XYZ dir = tr.Dir;
            XYZ toViewer = dir.CrossProduct(XYZ.BasisZ);                  // right of a→b
            XYZ basisY = XYZ.BasisZ;
            XYZ basisX = basisY.CrossProduct(toViewer);                   // = dir
            XYZ o = tr.A + toViewer * front;
            var tf = Transform.Identity;
            tf.Origin = new XYZ(o.X, o.Y, 0);
            tf.BasisX = basisX;
            tf.BasisY = basisY;
            tf.BasisZ = toViewer;
            var box = new BoundingBoxXYZ
            {
                Transform = tf,
                Min = new XYZ(0, zMin, -(front + depth)),
                Max = new XYZ(tr.Length, zMax, 0)
            };
            return ViewSection.CreateSection(Doc, typeId, box);
        }

        // ==================================================================== sheet

        private ViewSheet Assemble(List<Trecho> trechos, List<ViewSection> views, FamilySymbol tb, int scale,
                                   double zMin, double zMax, string baseName, bool folds, bool noTitles, List<string> problems)
        {
            if (tb != null && !tb.IsActive) tb.Activate();
            ViewSheet sheet = ViewSheet.Create(Doc, tb != null ? tb.Id : ElementId.InvalidElementId);
            try { sheet.SheetNumber = NextSheetNumber(); } catch { }
            try { sheet.Name = baseName + " (PLANIFICADA)"; } catch { }
            Doc.Regenerate();

            double width = trechos.Sum(t => t.Length) / scale, height = (zMax - zMin) / scale;
            XYZ c = new XYZ(width / 2, height / 2, 0);
            Element tbInst = new FilteredElementCollector(Doc, sheet.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).FirstElement();
            BoundingBoxXYZ bb = tbInst?.get_BoundingBox(sheet);
            if (bb != null)
            {
                c = (bb.Min + bb.Max) / 2;
                if (width > bb.Max.X - bb.Min.X || height > bb.Max.Y - bb.Min.Y)
                    problems.Add("A fachada não cabe no carimbo escolhido: use uma escala menor ou um carimbo maior.");
            }
            double x0 = c.X - width / 2, y0 = c.Y - height / 2;

            ElementId noTitleType = null;
            double cum = 0;
            for (int i = 0; i < trechos.Count; i++)
            {
                ViewSection v = views[i];
                double w = trechos[i].Length / scale;
                if (v != null)
                {
                    if (!Viewport.CanAddViewToSheet(Doc, sheet.Id, v.Id)) problems.Add($"{v.Name}: o Revit não permitiu colocar na prancha.");
                    else
                    {
                        Viewport vp = Viewport.Create(Doc, sheet.Id, v.Id, new XYZ(x0 + cum + w / 2, y0 + height / 2, 0));
                        if (noTitles)
                        {
                            if (noTitleType == null) noTitleType = NoTitleType(vp);
                            if (noTitleType != ElementId.InvalidElementId) { try { vp.ChangeTypeId(noTitleType); } catch { } }
                        }
                        Doc.Regenerate();
                        // exact: the stretch's start at the bottom of the crop goes to (x0 + cum, y0)
                        try
                        {
                            Transform toProj = v.GetModelToProjectionTransforms()[0].GetModelToProjectionTransform();
                            Transform toSheet = vp.GetProjectionToSheetTransform();
                            XYZ at = toSheet.OfPoint(toProj.OfPoint(new XYZ(trechos[i].A.X, trechos[i].A.Y, zMin)));
                            XYZ move = new XYZ(x0 + cum - at.X, y0 - at.Y, 0);
                            if (move.GetLength() > 1e-6) ElementTransformUtils.MoveElement(Doc, vp.Id, move);
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn("[JA Tools] Fachada Planificada: alinhamento da viewport: " + ex.Message);
                            RevitUtil.AddOnce(problems, "Alinhamento exato das vistas falhou; confira as emendas na prancha.");
                        }
                    }
                }
                cum += w;
                if (folds && i < trechos.Count - 1)
                {
                    try
                    {
                        var line = Line.CreateBound(new XYZ(x0 + cum, y0, 0), new XYZ(x0 + cum, y0 + height, 0));
                        DetailCurve dc = Doc.Create.NewDetailCurve(sheet, line);
                        GraphicsStyle dash = DashedStyle();
                        if (dash != null) dc.LineStyle = dash;
                    }
                    catch (Exception ex) { RevitUtil.AddOnce(problems, "Linhas de dobra: " + ex.Message); }
                }
            }
            return sheet;
        }

        private string NextSheetNumber()
        {
            var used = new HashSet<string>(new FilteredElementCollector(Doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                                               .Select(s => s.SheetNumber), StringComparer.OrdinalIgnoreCase);
            for (int i = 1; ; i++) { string n = "FP" + i.ToString("00"); if (!used.Contains(n)) return n; }
        }

        /// <summary>A viewport type with "Show title" off (duplicated from the current one if needed).</summary>
        private ElementId NoTitleType(Viewport vp)
        {
            try
            {
                var current = Doc.GetElement(vp.GetTypeId()) as ElementType;
                if (current == null) return ElementId.InvalidElementId;
                foreach (ElementId id in current.GetValidTypes())
                {
                    Parameter p = Doc.GetElement(id)?.get_Parameter(BuiltInParameter.VIEWPORT_ATTR_SHOW_LABEL);
                    if (p != null && p.AsInteger() == 0) return id;
                }
                string name = current.Name + " (sem título)";
                ElementType dup = current.Duplicate(name);
                dup.get_Parameter(BuiltInParameter.VIEWPORT_ATTR_SHOW_LABEL)?.Set(0);
                return dup.Id;
            }
            catch { return ElementId.InvalidElementId; }
        }

        /// <summary>A dashed/hidden line style for the folds, if the project has one.</summary>
        private GraphicsStyle DashedStyle()
        {
            Category lines = Doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
            if (lines == null) return null;
            string[] words = { "ocult", "hidden", "tracej", "dash", "pontilh" };
            foreach (Category sub in lines.SubCategories)
            {
                string n = sub.Name.ToLowerInvariant();
                if (words.Any(n.Contains)) return sub.GetGraphicsStyle(GraphicsStyleType.Projection);
            }
            return null;
        }
    }
}
