using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using JAEsquadrias.Core;

namespace JAEsquadrias.Engine
{
    /// <summary>Plan footprint of a vertical face of a wall or column: from A to B (z = 0), N = the side it looks at.</summary>
    internal sealed class FaceEntorno
    {
        public XYZ A, B, N;
        public double Length => A.DistanceTo(B);
    }

    /// <summary>
    /// What is around a countertop modelled in place: the vertical faces of walls and columns
    /// (structural and architectural) next to its outline, at the height of the top. Used to
    /// tell which edges of the outline touch a wall (frontão) and which are free (saia), and to
    /// draw the surroundings in the editor.
    /// </summary>
    internal static class EntornoBancada
    {
        private const double Mm = 1.0 / 304.8;

        /// <summary>Faces near the box min–max (feet, plan), that exist at the height <paramref name="z"/>.</summary>
        public static List<FaceEntorno> Faces(Document doc, IList<XYZ> pts, double z)
        {
            var res = new List<FaceEntorno>();
            if (pts == null || pts.Count == 0) return res;
            double x0 = pts.Min(p => p.X) - 2.5, x1 = pts.Max(p => p.X) + 2.5, y0 = pts.Min(p => p.Y) - 2.5, y1 = pts.Max(p => p.Y) + 2.5;
            try
            {
                var cats = new List<BuiltInCategory> { BuiltInCategory.OST_Walls, BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_Columns };
                var box = new Outline(new XYZ(x0, y0, z - 0.3), new XYZ(x1, y1, z + 0.3));
                var opt = new Options { DetailLevel = ViewDetailLevel.Fine };
                foreach (Element e in new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(cats))
                                          .WhereElementIsNotElementType().WherePasses(new BoundingBoxIntersectsFilter(box)))
                {
                    try { Coletar(e.get_Geometry(opt), z, x0, y0, x1, y1, res, 0); }
                    catch (Exception ex) { Logger.Warn("[JA Famílias] Entorno da bancada, elemento " + e.Id.Value + ": " + ex.Message); }
                    if (res.Count > 4000) break;
                }
            }
            catch (Exception ex) { Logger.Warn("[JA Famílias] Entorno da bancada: " + ex.Message); }
            return res;
        }

        private static void Coletar(GeometryElement ge, double z, double x0, double y0, double x1, double y1, List<FaceEntorno> res, int nivel)
        {
            if (ge == null || nivel > 4) return;
            foreach (GeometryObject go in ge)
            {
                if (go is GeometryInstance gi)
                {
                    GeometryElement inner = null;
                    try { inner = gi.GetInstanceGeometry(); } catch { }
                    Coletar(inner, z, x0, y0, x1, y1, res, nivel + 1);
                    continue;
                }
                if (!(go is Solid solid) || solid.Faces.Size == 0) continue;
                foreach (Face f in solid.Faces)
                {
                    if (!(f is PlanarFace pf) || Math.Abs(pf.FaceNormal.Z) > 0.02) continue;
                    XYZ n = new XYZ(pf.FaceNormal.X, pf.FaceNormal.Y, 0).Normalize();
                    XYZ d = XYZ.BasisZ.CrossProduct(n);
                    double t0 = double.MaxValue, t1 = double.MinValue, zMin = double.MaxValue, zMax = double.MinValue;
                    foreach (EdgeArray loop in pf.EdgeLoops)
                        foreach (Edge ed in loop)
                            foreach (XYZ p in ed.Tessellate())
                            {
                                double t = p.X * d.X + p.Y * d.Y;
                                t0 = Math.Min(t0, t); t1 = Math.Max(t1, t);
                                zMin = Math.Min(zMin, p.Z); zMax = Math.Max(zMax, p.Z);
                            }
                    if (t1 - t0 < 20 * Mm || z < zMin - 0.02 || z > zMax + 0.02) continue;
                    // the line of the face in plan: every point p with p·n = k
                    double k = pf.Origin.X * n.X + pf.Origin.Y * n.Y;
                    var a = new XYZ(n.X * k + d.X * t0, n.Y * k + d.Y * t0, 0);
                    var b = new XYZ(n.X * k + d.X * t1, n.Y * k + d.Y * t1, 0);
                    if (Math.Max(a.X, b.X) < x0 || Math.Min(a.X, b.X) > x1 || Math.Max(a.Y, b.Y) < y0 || Math.Min(a.Y, b.Y) > y1) continue;
                    res.Add(new FaceEntorno { A = a, B = b, N = n });
                }
            }
        }

        /// <summary>
        /// For each edge of the outline (pts[i] → pts[i+1], feet): Parede when a wall or column
        /// face runs along it (parallel, up to 15 mm away, looking at the countertop, along at
        /// least 60 % of the edge), else Livre.
        /// </summary>
        public static List<BordaLocal> Detectar(IList<XYZ> pts, List<FaceEntorno> faces)
        {
            var res = new List<BordaLocal>();
            int n = pts.Count;
            double area = 0;
            for (int i = 0; i < n; i++) area += pts[i].X * pts[(i + 1) % n].Y - pts[(i + 1) % n].X * pts[i].Y;
            double sinal = area >= 0 ? 1 : -1;
            for (int i = 0; i < n; i++)
            {
                XYZ a = new XYZ(pts[i].X, pts[i].Y, 0), b = new XYZ(pts[(i + 1) % n].X, pts[(i + 1) % n].Y, 0);
                double len = a.DistanceTo(b);
                if (len < 1 * Mm) { res.Add(BordaLocal.Livre); continue; }
                XYZ dir = (b - a) / len;
                var dentro = new XYZ(-dir.Y * sinal, dir.X * sinal, 0);      // towards the inside of the countertop
                XYZ mid = (a + b) / 2;
                double encostado = 0;
                foreach (FaceEntorno f in faces)
                {
                    double fl = f.Length;
                    if (fl < 1 * Mm) continue;
                    XYZ fd = (f.B - f.A) / fl;
                    if (Math.Abs(dir.X * fd.Y - dir.Y * fd.X) > 0.02) continue;              // not parallel
                    if (Math.Abs((mid - f.A).DotProduct(f.N)) > 15 * Mm) continue;           // not on the face
                    if (f.N.DotProduct(dentro) < 0.5) continue;                              // the face looks away
                    double ta = (a - f.A).DotProduct(fd), tb = (b - f.A).DotProduct(fd);
                    double lo = Math.Max(Math.Min(ta, tb), 0), hi = Math.Min(Math.Max(ta, tb), fl);
                    if (hi > lo) encostado += hi - lo;
                }
                res.Add(encostado >= 0.6 * len ? BordaLocal.Parede : BordaLocal.Livre);
            }
            return res;
        }
    }
}
