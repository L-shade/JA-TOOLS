using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDwgUnifier.Tools.Common;

namespace RevitDwgUnifier.Tools.Dimensions
{
    /// <summary>
    /// Cotas de Piso e Forro. Dimensions of horizontal elements by their own edges: for every floor / ceiling, a chain of
    /// all its edges across X and across Y (through its middle) plus, optionally, an overall
    /// dimension, and the wall faces around it (ceilings: the gap between ceiling and wall =
    /// tabica / trim appears in the same chain).
    /// </summary>
    /// <remarks>1.17: Cotas por Piso + Cotas por Forro = Cotas de Piso e Forro (escolha "Cotar").
    /// Forros: a tabica (folga até a parede) entra na mesma corrente.</remarks>
    [Transaction(TransactionMode.Manual)]
    public sealed class CotasPisoForroCommand : DimToolCommand
    {
        public override string Title => "Cotas de Piso e Forro";

        protected override Result Run()
        {
            ViewPlan view = RequirePlan();
            if (view == null) return Result.Cancelled;

            var sel = UiDoc.Selection.GetElementIds().Select(id => Doc.GetElement(id)).Where(e => e?.Category != null).ToList();
            int nPisos = sel.Count(e => e.Category.Id.Value == (long)BuiltInCategory.OST_Floors);
            int nForros = sel.Count(e => e.Category.Id.Value == (long)BuiltInCategory.OST_Ceilings);
            int modo0 = nForros > nPisos || (nPisos == 0 && view.ViewType == Autodesk.Revit.DB.ViewType.CeilingPlan) ? 1 : 0;

            var dlg = new OptionsDialog(Title, "CotasPisoForro");
            var fMode = dlg.Choice("mode", "Cotar", new[] { "Pisos", "Forros" }, modo0);
            if (ToolPrefs.Get("CotasPisoForro.mode") != null && (nPisos > 0 || nForros > 0)) fMode.Index = modo0;   // the selection decides
            var fType = AddTypeChoice(dlg);
            var fWhich = dlg.Choice("which", "Elementos", new[]
            {
                "Todos os visíveis na vista",
                nPisos + nForros > 0 ? "Selecionados" : "Selecionados (nenhum selecionado)"
            }, nPisos + nForros > 0 ? 1 : 0);
            var fWallsP = dlg.Check("walls.piso", "Incluir as faces das paredes em volta na mesma corrente", false);
            var fWallsF = dlg.Check("walls.forro", "Incluir as faces das paredes em volta (tabica) na mesma corrente", true);
            fWallsP.VisibleWhen(fMode, 0);
            fWallsF.VisibleWhen(fMode, 1);
            var fTotal = dlg.Check("total", "Cota total por fora da corrente", true);
            var fGap = dlg.Number("gap", "Distância entre corrente e total (m)", 0.35, 0.05, 10);
            var fDirs = dlg.Choice("dirs", "Direções", new[] { "Todas as direções das arestas (anguladas também)", "Somente horizontal e vertical" }, 0);
            if (!dlg.Show(Owner)) return Result.Cancelled;
            DimensionType type = SelectedType(fType);
            bool forro = fMode.Index == 1;
            BuiltInCategory cat = forro ? BuiltInCategory.OST_Ceilings : BuiltInCategory.OST_Floors;
            bool topFace = !forro;
            var fWalls = forro ? fWallsF : fWallsP;
            var selected = sel.Where(e => e.Category.Id.Value == (long)cat).ToList();

            List<Element> elements = fWhich.Index == 1 && selected.Count > 0
                ? selected
                : new FilteredElementCollector(Doc, view.Id).OfCategory(cat).WhereElementIsNotElementType().ToList();
            List<Wall> walls = fWalls.Value ? DimEngine.WallsInView(Doc, view) : new List<Wall>();
            double z = DimEngine.ViewZ(view);

            using (var t = new Transaction(Doc, "JA Tools - " + Title))
            {
                t.Start();
                foreach (Element e in elements)
                {
                    PlanarFace face = DimEngine.HorizontalFace(e, topFace);
                    if (face == null) { RevitUtil.AddOnce(Problems, "Elemento sem face horizontal: ignorado."); continue; }
                    List<Line> edges = DimEngine.FaceLines(face);
                    var pts = edges.SelectMany(l => new[] { RevitUtil.Flat(l.GetEndPoint(0)), RevitUtil.Flat(l.GetEndPoint(1)) }).ToList();
                    // one chain across every direction of the edges (any angle: 45°, 30°, 27°...)
                    List<XYZ> dirs = fDirs.Index == 1 ? new List<XYZ> { XYZ.BasisY, XYZ.BasisX }
                                                      : DimEngine.Directions(edges, RevitUtil.M(0.1)).Take(6).ToList();
                    foreach (XYZ d in dirs)
                    {
                        XYZ u = new XYZ(-d.Y, d.X, 0);      // across the edges of direction d
                        XYZ across = d;

                        // Line through the middle of the face, across the whole element.
                        double lo = pts.Min(q => q.DotProduct(u)), hi = pts.Max(q => q.DotProduct(u));
                        double c = (pts.Min(q => q.DotProduct(across)) + pts.Max(q => q.DotProduct(across))) / 2;
                        double tol = RevitUtil.M(0.001);
                        // Only the edges that the middle line actually crosses.
                        List<RefAt> crossing = DimEngine.HorizontalFaceEdges(e, topFace, XYZ.Zero, u, out _, (p0, p1) =>
                        {
                            double a0 = p0.DotProduct(across), a1 = p1.DotProduct(across);
                            return Math.Min(a0, a1) - tol <= c && c <= Math.Max(a0, a1) + tol;
                        });

                        var chain = new List<RefAt>(crossing);
                        if (fWalls.Value)
                        {
                            XYZ a = across * c + u * (lo - RevitUtil.M(0.4));
                            XYZ b = across * c + u * (hi + RevitUtil.M(0.4));
                            var faces = DimEngine.WallFacesAcross(walls, a, b);
                            // positions relative to XYZ.Zero along u: shift from a
                            double shift = a.DotProduct(u);
                            foreach (RefAt f in faces) f.Pos += shift;
                            // keep the wall faces closest to the element on each side
                            RefAt wl = DimEngine.Below(faces, lo + RevitUtil.M(0.001)), wh = DimEngine.Above(faces, hi - RevitUtil.M(0.001));
                            if (wl != null && lo - wl.Pos < RevitUtil.M(0.4)) chain.Add(wl);
                            if (wh != null && wh.Pos - hi < RevitUtil.M(0.4)) chain.Add(wh);
                        }

                        XYZ through = across * c + new XYZ(0, 0, z);
                        Make(view, XYZ.Zero, u, through, chain, type, "Corrente");
                        if (fTotal.Value && DimEngine.Clean(crossing).Count > 2)
                        {
                            var clean = DimEngine.Clean(crossing);
                            Make(view, XYZ.Zero, u, through + across * RevitUtil.M(fGap.Value), new[] { clean.First(), clean.Last() }, type, "Total");
                        }
                    }
                }
                t.Commit();
            }
            Report($"{elements.Count} elemento(s) analisado(s).");
            return Result.Succeeded;
        }
    }
}
