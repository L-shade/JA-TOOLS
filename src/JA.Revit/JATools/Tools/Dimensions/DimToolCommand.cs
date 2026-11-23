using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using RevitDwgUnifier.Tools.Common;

namespace RevitDwgUnifier.Tools.Dimensions
{
    /// <summary>Common parts of the Cotas tools: plan check, dimension type choice, report.</summary>
    public abstract class DimToolCommand : ToolCommand
    {
        protected List<DimensionType> DimTypes;
        protected readonly List<string> Problems = new List<string>();
        protected int Created, Failed;

        /// <summary>The active view if it is a plan (floor, ceiling, area, structural); else null
        /// and a message.</summary>
        protected ViewPlan RequirePlan()
        {
            if (Doc.ActiveView is ViewPlan vp && !vp.IsTemplate) return vp;
            Show("Abra uma planta (de piso ou de forro) e execute novamente: as cotas são criadas na vista ativa.");
            return null;
        }

        protected OptionsDialog.ChoiceField AddTypeChoice(OptionsDialog dlg)
        {
            DimTypes = RevitUtil.LinearDimensionTypes(Doc);
            return dlg.Choice("dimType", "Tipo de cota", DimEngine.TypeChoices(DimTypes));
        }

        protected DimensionType SelectedType(OptionsDialog.ChoiceField f) => DimEngine.TypeFromChoice(DimTypes, f.Index);

        /// <summary>Creates one chain, counting successes and failures.</summary>
        protected void Make(View view, XYZ origin, XYZ u, XYZ through, IEnumerable<RefAt> refs, DimensionType type, string what)
        {
            try
            {
                if (DimEngine.Chain(Doc, view, origin, u, through, refs, type) != null) Created++;
            }
            catch (System.Exception ex)
            {
                Failed++;
                RevitUtil.AddOnce(Problems, $"{what}: {ex.Message}");
            }
        }

        private sealed class Pt
        {
            public FamilyInstance Fi;
            public XYZ P;            // flat location
            public XYZ Along;        // row direction
            public XYZ Front;        // side where the dimension goes (unit, across the row)
        }

        /// <summary>
        /// Point-like instances in ROWS (same line within tol), one chain per row: nearest wall face
        /// before, the centre plane of every instance, nearest wall face after. Instances on a wall
        /// get one row along the wall in front of it; free ones rows along X and Y. Inside an open
        /// transaction. Returns the number of rows.
        /// </summary>
        protected int DimPoints(ViewPlan view, List<FamilyInstance> instances, List<Wall> walls, bool wallRows,
                                double tol, double off, DimensionType type)
        {
            double z = DimEngine.ViewZ(view);

            // Build the points with their row direction(s).
            var points = new List<Pt>();
            foreach (FamilyInstance fi in instances)
            {
                XYZ c = RevitUtil.Center(fi);
                if (c == null) continue;
                XYZ p = RevitUtil.Flat(c);
                XYZ facing = new XYZ(fi.FacingOrientation.X, fi.FacingOrientation.Y, 0);
                bool onWall = wallRows && (fi.Host is Wall || (fi.HostFace != null && Doc.GetElement(fi.HostFace) is Wall)) && !facing.IsZeroLength();
                if (onWall)
                {
                    facing = facing.Normalize();
                    points.Add(new Pt { Fi = fi, P = p, Along = new XYZ(-facing.Y, facing.X, 0), Front = facing });
                }
                else
                {
                    // free points (floor drains, ceiling lights): rows along the nearest wall and across it,
                    // so a room with walls at any angle is measured square to its walls
                    XYZ d = DimEngine.NearestWallDir(walls, p, RevitUtil.M(4)) ?? XYZ.BasisX;
                    XYZ nrm = new XYZ(-d.Y, d.X, 0);
                    points.Add(new Pt { Fi = fi, P = p, Along = d, Front = nrm });
                    points.Add(new Pt { Fi = fi, P = p, Along = nrm, Front = d });
                }
            }

            // Group into rows: same direction (either sense) and same line (offset across).
            var rows = new List<List<Pt>>();
            foreach (Pt pt in points)
            {
                XYZ n = new XYZ(-pt.Along.Y, pt.Along.X, 0);
                List<Pt> row = rows.FirstOrDefault(r =>
                    Math.Abs(r[0].Along.DotProduct(pt.Along)) >= 1 - DimEngine.Tol &&
                    Math.Abs((pt.P - r[0].P).DotProduct(n)) <= tol &&
                    r[0].Front.DotProduct(pt.Front) > 0);
                if (row == null) rows.Add(new List<Pt> { pt }); else row.Add(pt);
            }

            int noRef = 0;
            {
                foreach (List<Pt> row in rows)
                {
                    XYZ u = row[0].Along;
                    XYZ o = row[0].P;
                    var refs = new List<RefAt>();
                    foreach (Pt pt in row)
                    {
                        RefAt r = DimEngine.CenterAcross(pt.Fi, o, u);
                        if (r == null) { noRef++; continue; }
                        refs.Add(r);
                    }
                    if (refs.Count == 0) continue;

                    // Walls before / after the row, found on the dimension line itself.
                    double lo = refs.Min(r => r.Pos), hi = refs.Max(r => r.Pos);
                    XYZ lineAt = o + row[0].Front * off;
                    XYZ a = lineAt - u * RevitUtil.M(200), b = lineAt + u * RevitUtil.M(200);
                    var faces = DimEngine.WallFacesAcross(walls, a, b);
                    double shift = (RevitUtil.Flat(a) - o).DotProduct(u);
                    foreach (RefAt f in faces) f.Pos += shift;
                    RefAt before = DimEngine.Below(faces, lo), after = DimEngine.Above(faces, hi);
                    if (before != null) refs.Add(before);
                    if (after != null) refs.Add(after);

                    Make(view, o, u, new XYZ(lineAt.X, lineAt.Y, z), refs, type, "Fila");
                }
            }
            if (noRef > 0) RevitUtil.AddOnce(Problems, $"{noRef} elemento(s) sem plano de referência central (Center Left/Right, Front/Back) definido como referência na família.");
            return rows.Count;
        }

        /// <summary>Clear width (u = X) or length (u = Y) of a room, face to face of its walls, through
        /// its point shifted by offset (kept inside the room). Inside an open transaction.</summary>
        protected bool DimRoom(View view, List<Wall> walls, Autodesk.Revit.DB.Architecture.Room r, XYZ p, XYZ u, XYZ across,
                               double offset, double reach, double z, DimensionType type, bool quiet = false)
        {
            XYZ through = new XYZ(p.X, p.Y, z) + across * offset;
            if (!r.IsPointInRoom(through + new XYZ(0, 0, RevitUtil.M(0.5)))) through = new XYZ(p.X, p.Y, z);
            XYZ a = through - u * reach, b = through + u * reach;
            var faces = DimEngine.WallFacesAcross(walls, a, b);
            double here = (through - a).DotProduct(u);
            RefAt lo = DimEngine.Below(faces, here), hi = DimEngine.Above(faces, here);
            if (lo == null || hi == null)
            {
                if (!quiet) RevitUtil.AddOnce(Problems, $"Ambiente {r.Number}: paredes não encontradas em uma direção.");
                return false;
            }
            int before = Created;
            Make(view, a, u, through, new[] { lo, hi }, type, "Ambiente " + r.Number);
            return Created > before;
        }

        /// <summary>
        /// Clear dimensions of a room across EVERY direction of its walls (walls at 45°, 30°, 27°, 21°,
        /// 11°... included): for each direction, the distance between the parallel walls facing each
        /// other, through the room point. onlyXY = the old behaviour (horizontal and vertical only).
        /// Returns the dimensions created.
        /// </summary>
        protected int DimRoomAll(View view, List<Wall> walls, Autodesk.Revit.DB.Architecture.Room r, double offset, double z,
                                 DimensionType type, bool onlyXY, bool x = true, bool y = true)
        {
            XYZ p = RevitUtil.RoomPoint(r);
            BoundingBoxXYZ box = RevitUtil.RoomBox(r);
            if (p == null || box == null) return 0;
            double reach = Math.Max(box.Max.X - box.Min.X, box.Max.Y - box.Min.Y) + RevitUtil.M(1);
            double size = Math.Min(box.Max.X - box.Min.X, box.Max.Y - box.Min.Y);
            int made = 0;
            if (onlyXY)
            {
                if (x && DimRoom(view, walls, r, p, XYZ.BasisX, XYZ.BasisY, Math.Min(offset, (box.Max.Y - box.Min.Y) * 0.3), reach, z, type)) made++;
                if (y && DimRoom(view, walls, r, p, XYZ.BasisY, XYZ.BasisX, Math.Min(offset, (box.Max.X - box.Min.X) * 0.3), reach, z, type)) made++;
                return made;
            }
            // wall directions (at most 6, each with at least 30 cm of wall)
            List<XYZ> dirs = DimEngine.RoomDirections(r, RevitUtil.M(0.3)).Take(6).ToList();
            if (dirs.Count == 0) dirs = new List<XYZ> { XYZ.BasisX, XYZ.BasisY };
            var cotadas = new List<XYZ>();
            foreach (XYZ d in dirs)
            {
                XYZ u = new XYZ(-d.Y, d.X, 0);      // across the walls of direction d
                if (DimRoom(view, walls, r, p, u, d, Math.Min(offset, size * 0.3), reach, z, type, quiet: true)) { made++; cotadas.Add(d); }
            }
            // walls with no parallel wall in front (irregular rooms): their clear length, corner to corner
            made += DimLados(view, r, cotadas, z, type);
            if (made == 0) RevitUtil.AddOnce(Problems, $"Ambiente {r.Number}: nenhuma parede para cotar (contorno sem paredes ou cantos sem referência).");
            return made;
        }

        /// <summary>
        /// Length of every straight side of the room that no width dimension already gives (a side
        /// is measured when both its neighbours are square to it and dimensioned across each other):
        /// along the inner face, between the two vertical corner edges
        /// of the walls (edges are parallel to each other, so any wall angle works), a little inside
        /// the room. Inside an open transaction. Returns the dimensions created.
        /// </summary>
        protected int DimLados(View view, Autodesk.Revit.DB.Architecture.Room r, List<XYZ> cotadas, double z, DimensionType type)
        {
            // the structure of the walls, never the finish: the core boundary of compound walls, and for
            // finish walls (paredes cebola) the face of the wall behind them
            IList<IList<BoundarySegment>> loops = null;
            try { loops = RevitUtil.Boundary(r, SpatialElementBoundaryLocation.CoreBoundary); } catch { }
            if (loops == null || loops.Count == 0) try { loops = RevitUtil.Boundary(r); } catch { }
            if (loops == null || loops.Count == 0) return 0;
            IList<BoundarySegment> externo = loops.OrderByDescending(l => l.Sum(sg => sg.GetCurve().Length)).First();
            double area = 0;
            foreach (BoundarySegment sg in externo)
            {
                XYZ a0 = sg.GetCurve().GetEndPoint(0), b0 = sg.GetCurve().GetEndPoint(1);
                area += a0.X * b0.Y - b0.X * a0.Y;
            }
            bool antiHorario = area > 0;
            var opt = new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine };   // the layers, also in coarse plans
            int made = 0, semCanto = 0;
            int nSeg = externo.Count;
            var linhas = new Line[nSeg];
            var donos = new ElementId[nSeg];
            for (int i = 0; i < nSeg; i++) linhas[i] = LinhaDaEstrutura(externo[i], antiHorario, out donos[i]);
            for (int i = 0; i < externo.Count; i++)
            {
                if (!(externo[i].GetCurve() is Line ln0) || ln0.Length < RevitUtil.M(0.3)) continue;
                Line ln = linhas[i] ?? ln0;
                XYZ d = new XYZ(ln.Direction.X, ln.Direction.Y, 0).Normalize();
                // already measured: both neighbours square to this side and dimensioned across each other
                // (rectangles, and the square side of a trapezoid); a side next to an angled wall is not
                XYZ dp = Dir(externo[(i + externo.Count - 1) % externo.Count]), dn = Dir(externo[(i + 1) % externo.Count]);
                bool medido = dp != null && dn != null
                              && Math.Abs(dp.DotProduct(d)) < DimEngine.Tol && Math.Abs(dn.DotProduct(d)) < DimEngine.Tol
                              && cotadas.Any(c => Math.Abs(c.X * dp.Y - c.Y * dp.X) < DimEngine.Tol);
                if (medido) continue;
                int ip = (i + nSeg - 1) % nSeg, inx = (i + 1) % nSeg;
                var ids = new List<ElementId> { donos[i], donos[ip], donos[inx] };
                // the corners where the structure faces meet (the finish walls are skipped)
                XYZ a = Encontro(linhas[ip], ln) ?? RevitUtil.Flat(ln.GetEndPoint(0)), b = Encontro(ln, linhas[inx]) ?? RevitUtil.Flat(ln.GetEndPoint(1));
                if (a.DistanceTo(b) < RevitUtil.M(0.3)) continue;
                Reference ra = CantoVertical(view, ids, a, opt), rb = CantoVertical(view, ids, b, opt);
                if (ra == null || rb == null) { semCanto++; continue; }
                XYZ dentro = antiHorario ? new XYZ(-d.Y, d.X, 0) : new XYZ(d.Y, -d.X, 0);
                double off = Math.Min(RevitUtil.M(0.4), ln.Length * 0.25);
                XYZ through = (a + b) / 2 + dentro * off + new XYZ(0, 0, z);
                int before = Created;
                Make(view, a, d, through, new[]
                {
                    new RefAt { Ref = ra, Pos = 0, Kind = "edge" },
                    new RefAt { Ref = rb, Pos = (b - a).DotProduct(d), Kind = "edge" }
                }, type, "Ambiente " + r.Number);
                if (Created > before) made++;
            }
            if (semCanto > 0) RevitUtil.AddOnce(Problems, $"Ambiente {r.Number}: {semCanto} lado(s) sem parede nos cantos (linha separadora ou parede sem geometria) não foram cotados.");
            return made;
        }

        /// <summary>
        /// The line of the structure face along a boundary segment: the segment itself, or — when it runs on a
        /// finish wall — the room-side structure face of the wall behind it (moved out by the finish).
        /// </summary>
        private Line LinhaDaEstrutura(BoundarySegment sg, bool antiHorario, out ElementId dono)
        {
            dono = sg.ElementId;
            if (!(sg.GetCurve() is Line l) || l.Length < 1e-6) return null;
            XYZ a = RevitUtil.Flat(l.GetEndPoint(0)), b = RevitUtil.Flat(l.GetEndPoint(1));
            if (!(Doc.GetElement(sg.ElementId) is Wall w) || !DimEngine.IsFinishWall(w)) return Line.CreateBound(a, b);
            Wall s = DimEngine.Structure(w);
            if (s == null) return Line.CreateBound(a, b);
            XYZ d = (b - a).Normalize();
            XYZ dentro = antiHorario ? new XYZ(-d.Y, d.X, 0) : new XYZ(d.Y, -d.X, 0);
            double melhor = double.MaxValue;
            foreach (var f in DimEngine.SideFaces(s))
            {
                XYZ n = RevitUtil.Flat(f.Item2.FaceNormal);
                if (n.GetLength() < 1e-9 || n.Normalize().DotProduct(dentro) < 1 - DimEngine.Tol) continue;   // the face looking into the room
                double atras = (a - RevitUtil.Flat(f.Item2.Origin)).DotProduct(dentro);                        // how far the segment is in front of it
                if (atras < -RevitUtil.M(0.002) || atras > RevitUtil.M(0.5) || atras >= melhor) continue;
                melhor = atras;
            }
            if (melhor == double.MaxValue) return Line.CreateBound(a, b);
            dono = s.Id;
            return Line.CreateBound(a - dentro * melhor, b - dentro * melhor);
        }

        /// <summary>Where two plan lines (unbounded) cross; null when parallel.</summary>
        private static XYZ Encontro(Line l1, Line l2)
        {
            if (l1 == null || l2 == null) return null;
            XYZ p = RevitUtil.Flat(l1.GetEndPoint(0)), d = RevitUtil.Flat(l1.Direction).Normalize();
            XYZ q = RevitUtil.Flat(l2.GetEndPoint(0)), e = RevitUtil.Flat(l2.Direction).Normalize();
            double c = d.X * e.Y - d.Y * e.X;
            if (Math.Abs(c) < 1e-6) return null;
            XYZ w = q - p;
            double t = (w.X * e.Y - w.Y * e.X) / c;
            return p + d * t;
        }

        private static XYZ Dir(BoundarySegment sg)
        {
            if (!(sg.GetCurve() is Line l) || l.Length < 1e-6) return null;
            var f = new XYZ(l.Direction.X, l.Direction.Y, 0);
            return f.GetLength() < 1e-9 ? null : f.Normalize();
        }

        /// <summary>A vertical edge of one of the walls at a plan point (a room corner).</summary>
        private Reference CantoVertical(View view, IEnumerable<ElementId> ids, XYZ p, Options opt)
        {
            double tol = RevitUtil.M(0.02);
            foreach (ElementId id in ids.Distinct())
            {
                if (!(Doc.GetElement(id) is Wall w)) continue;
                GeometryElement geo;
                try { geo = w.get_Geometry(opt); } catch { continue; }
                if (geo == null) continue;
                foreach (GeometryObject go in geo)
                {
                    if (!(go is Solid s) || s.Volume < 1e-9) continue;
                    foreach (Edge e in s.Edges)
                    {
                        if (e.Reference == null || !(e.AsCurve() is Line l) || Math.Abs(l.Direction.Z) < 0.999) continue;
                        XYZ q = l.GetEndPoint(0);
                        if (new XYZ(q.X - p.X, q.Y - p.Y, 0).GetLength() < tol) return e.Reference;
                    }
                }
            }
            return null;
        }

        protected void Report(string extra = null)
        {
            foreach (string a in DimEngine.CoreWarnings) RevitUtil.AddOnce(Problems, a);
            string text = $"{Created} cota(s) criada(s)." + (Failed > 0 ? $"\n{Failed} não puderam ser criadas." : "") +
                          (string.IsNullOrEmpty(extra) ? "" : "\n" + extra);
            if (Problems.Count > 0) text += "\n\nAvisos:\n" + string.Join("\n", Problems.Take(15).Select(p => "  • " + p));
            Show(text);
        }
    }
}
