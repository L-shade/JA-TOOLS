using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using RevitDwgUnifier.Tools.Common;

namespace RevitDwgUnifier.Tools.Views
{
    /// <summary>
    /// Grid naming and dimensioning shared by "Eixos de Paredes", "Recalcular Eixos" and
    /// "Vistas de Eixos".
    ///
    /// Convention (Brazilian practice, configurable): grids running up/down the plan (vertical)
    /// are NUMBERED 1, 2, 3... from left to right; grids running left/right (horizontal) get
    /// LETTERS A, B, C... from top to bottom. Oblique grids are classified by their closest axis.
    /// </summary>
    public static class GridTools
    {
        public static readonly IList<string> Rules = new[]
        {
            "Verticais: 1, 2, 3 (esq.→dir.)  |  Horizontais: A, B, C (cima→baixo)",
            "Verticais: A, B, C (esq.→dir.)  |  Horizontais: 1, 2, 3 (baixo→cima)"
        };

        public static bool IsVertical(Line l)
        {
            XYZ d = l.Direction;
            return Math.Abs(d.Y) >= Math.Abs(d.X);
        }

        public static List<Grid> LinearGrids(Document doc, View onlyVisibleIn = null)
        {
            var col = onlyVisibleIn != null ? new FilteredElementCollector(doc, onlyVisibleIn.Id) : new FilteredElementCollector(doc);
            return col.OfClass(typeof(Grid)).Cast<Grid>().Where(g => g.Curve is Line).ToList();
        }

        /// <summary>Signed distance of the grid line from the origin, measured along the axis the
        /// grids are ordered by (X for vertical grids, Y for horizontal ones).</summary>
        private static double Position(Grid g, bool vertical)
        {
            var l = (Line)g.Curve;
            XYZ mid = (l.GetEndPoint(0) + l.GetEndPoint(1)) / 2;
            return vertical ? mid.X : mid.Y;
        }

        public static string Letters(int index)
        {
            // 0 -> A ... 25 -> Z, 26 -> AA ...
            string s = "";
            index++;
            while (index > 0)
            {
                int r = (index - 1) % 26;
                s = (char)('A' + r) + s;
                index = (index - 1) / 26;
            }
            return s;
        }

        /// <summary>Renames the grids by the rule (two passes, so names never collide).</summary>
        public static int Rename(Document doc, IList<Grid> grids, int rule)
        {
            var vertical = grids.Where(g => IsVertical((Line)g.Curve)).OrderBy(g => Position(g, true)).ToList();
            var horizontal = grids.Where(g => !IsVertical((Line)g.Curve)).ToList();
            horizontal = rule == 0
                ? horizontal.OrderByDescending(g => Position(g, false)).ToList()   // top -> bottom
                : horizontal.OrderBy(g => Position(g, false)).ToList();            // bottom -> top

            var target = new Dictionary<ElementId, string>();
            for (int i = 0; i < vertical.Count; i++) target[vertical[i].Id] = rule == 0 ? (i + 1).ToString() : Letters(i);
            for (int i = 0; i < horizontal.Count; i++) target[horizontal[i].Id] = rule == 0 ? Letters(i) : (i + 1).ToString();

            // Pass 1: temporary unique names (also frees names used by grids outside the set).
            string stamp = DateTime.Now.Ticks.ToString("x");
            int k = 0;
            foreach (Grid g in grids) g.Name = $"~ja{stamp}_{k++}";
            var others = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>()
                .Where(g => !target.ContainsKey(g.Id)).Select(g => g.Name), StringComparer.OrdinalIgnoreCase);

            int renamed = 0;
            foreach (Grid g in grids)
            {
                string name = target[g.Id];
                string unique = name;
                for (int n = 2; others.Contains(unique); n++) unique = name + "'" + (n > 2 ? n.ToString() : "");
                g.Name = unique;
                renamed++;
            }
            return renamed;
        }

        /// <summary>
        /// Chain dimension + overall dimension for every family of parallel grids, placed near
        /// the end of the grids (inside the bubbles). Returns the number of dimensions created.
        /// </summary>
        public static int DimensionGrids(Document doc, View view, IList<Grid> grids, List<string> problems,
                                    double offsetMeters = 1.0, bool overall = true)
        {
            int created = 0;
            // Group by direction (angle mod 180°, 0.5° tolerance).
            var groups = new List<List<Grid>>();
            foreach (Grid g in grids)
            {
                XYZ d = ((Line)g.Curve).Direction;
                var group = groups.FirstOrDefault(gr => Math.Abs(((Line)gr[0].Curve).Direction.CrossProduct(d).Z) < Math.Sin(Math.PI / 360));
                if (group == null) groups.Add(new List<Grid> { g }); else group.Add(g);
            }

            foreach (var group in groups.Where(gr => gr.Count >= 2))
            {
                XYZ dir = ((Line)group[0].Curve).Direction;
                if (dir.Y < -1e-9 || (Math.Abs(dir.Y) < 1e-9 && dir.X < 0)) dir = -dir;   // consistent "up"/"right"
                XYZ normal = new XYZ(-dir.Y, dir.X, 0);                                    // across the grids

                var ordered = group.OrderBy(g => ((Line)g.Curve).GetEndPoint(0).DotProduct(normal)).ToList();
                // Common end of the grids along their direction: the furthest "start" end.
                double maxAlong = ordered.Max(g => Math.Max(((Line)g.Curve).GetEndPoint(0).DotProduct(dir), ((Line)g.Curve).GetEndPoint(1).DotProduct(dir)));
                double minAcross = ordered.Min(g => ((Line)g.Curve).GetEndPoint(0).DotProduct(normal));
                double maxAcross = ordered.Max(g => ((Line)g.Curve).GetEndPoint(0).DotProduct(normal));
                double z = ((Line)ordered[0].Curve).GetEndPoint(0).Z;

                Line At(double along) =>
                    Line.CreateBound(dir * along + normal * (minAcross - RevitUtil.M(0.5)) + new XYZ(0, 0, z),
                                     dir * along + normal * (maxAcross + RevitUtil.M(0.5)) + new XYZ(0, 0, z));
                try
                {
                    double a1 = maxAlong - RevitUtil.M(offsetMeters);
                    if (RevitUtil.CreateDimension(doc, view, At(a1), ordered.Select(g => new Reference(g))) != null) created++;
                    if (overall && ordered.Count > 2)
                    {
                        double a2 = maxAlong - RevitUtil.M(offsetMeters + 0.8);
                        if (RevitUtil.CreateDimension(doc, view, At(a2), new[] { new Reference(ordered.First()), new Reference(ordered.Last()) }) != null) created++;
                    }
                }
                catch (Exception ex)
                {
                    RevitUtil.AddOnce(problems, "Cota dos eixos: " + ex.Message);
                }
            }
            return created;
        }

        /// <summary>Dimensions of the view whose references are all grids (made by this tool or by hand).</summary>
        public static List<ElementId> GridOnlyDimensions(Document doc, View view)
        {
            var result = new List<ElementId>();
            foreach (Dimension d in new FilteredElementCollector(doc, view.Id).OfClass(typeof(Dimension)).Cast<Dimension>())
            {
                try
                {
                    ReferenceArray refs = d.References;
                    if (refs == null || refs.Size < 2) continue;
                    bool allGrids = true;
                    foreach (Reference r in refs)
                        if (!(doc.GetElement(r) is Grid)) { allGrids = false; break; }
                    if (allGrids) result.Add(d.Id);
                }
                catch { }
            }
            return result;
        }
    }
}
