using System;
using System.Collections.Generic;
using System.Linq;

namespace JAEsquadrias.Core
{
    /// <summary>
    /// Paginação: a grid of tiles (piece + joint) laid over a region (outer loops and holes, even-odd)
    /// in a 2D frame; gives the joint lines clipped to the region and counts the whole and cut
    /// pieces. All lengths in mm. The grid has its axes rotated by Angulo (radians) about the origin.
    /// </summary>
    public sealed class Paginacao
    {
        public double PecaX = 600, PecaY = 600, Junta = 2, Angulo;
        public P2 Origem;                   // a joint corner of the grid
        public readonly List<P2[]> Linhas = new List<P2[]>();
        public int Inteiras, Recortes;
        public double AreaM2;

        private static P2 Gira(P2 p, double c, double s) => new P2(p.X * c - p.Y * s, p.X * s + p.Y * c);

        public static double Area(IList<P2> l)
        {
            double a = 0;
            for (int i = 0; i < l.Count; i++) { P2 p = l[i], q = l[(i + 1) % l.Count]; a += p.X * q.Y - q.X * p.Y; }
            return a / 2;
        }

        /// <summary>Region area (mm²), even-odd: the loops with the largest |area| first is outer, nested ones subtract.</summary>
        private static double AreaRegiao(List<List<P2>> loops)
        {
            // even-odd: a loop inside an odd number of other loops is a hole
            double total = 0;
            for (int i = 0; i < loops.Count; i++)
            {
                int dentro = 0;
                P2 p = loops[i][0];
                for (int j = 0; j < loops.Count; j++) if (j != i && Dentro(loops[j], p)) dentro++;
                total += (dentro % 2 == 0 ? 1 : -1) * Math.Abs(Area(loops[i]));
            }
            return total;
        }

        public static bool Dentro(IList<P2> l, P2 p)
        {
            bool c = false;
            for (int i = 0, j = l.Count - 1; i < l.Count; j = i++)
            {
                P2 a = l[i], b = l[j];
                if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) c = !c;
            }
            return c;
        }

        /// <summary>Sutherland–Hodgman: polygon clipped by an axis-aligned rectangle.</summary>
        private static List<P2> Recorta(List<P2> poly, double x0, double y0, double x1, double y1)
        {
            List<P2> r = poly;
            for (int lado = 0; lado < 4 && r.Count > 0; lado++)
            {
                bool In(P2 p) => lado == 0 ? p.X >= x0 : lado == 1 ? p.X <= x1 : lado == 2 ? p.Y >= y0 : p.Y <= y1;
                P2 Corte(P2 a, P2 b)
                {
                    double t = lado == 0 ? (x0 - a.X) / (b.X - a.X) : lado == 1 ? (x1 - a.X) / (b.X - a.X) : lado == 2 ? (y0 - a.Y) / (b.Y - a.Y) : (y1 - a.Y) / (b.Y - a.Y);
                    return new P2(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
                }
                var o = new List<P2>();
                for (int i = 0; i < r.Count; i++)
                {
                    P2 cur = r[i], prev = r[(i + r.Count - 1) % r.Count];
                    bool ci = In(cur), pi = In(prev);
                    if (ci) { if (!pi) o.Add(Corte(prev, cur)); o.Add(cur); }
                    else if (pi) o.Add(Corte(prev, cur));
                }
                r = o;
            }
            return r;
        }

        /// <summary>Lays the grid over the region. loops: in the 2D frame (mm).</summary>
        public void Calcular(List<List<P2>> loops)
        {
            Linhas.Clear(); Inteiras = Recortes = 0;
            loops = loops.Where(l => l.Count >= 3).ToList();
            if (loops.Count == 0) return;
            AreaM2 = AreaRegiao(loops) / 1e6;

            // into the grid frame: origin at Origem, axes rotated by -Angulo
            double c = Math.Cos(-Angulo), s = Math.Sin(-Angulo);
            var g = loops.Select(l => l.Select(p => Gira(new P2(p.X - Origem.X, p.Y - Origem.Y), c, s)).ToList()).ToList();
            double minX = g.SelectMany(l => l).Min(p => p.X), maxX = g.SelectMany(l => l).Max(p => p.X);
            double minY = g.SelectMany(l => l).Min(p => p.Y), maxY = g.SelectMany(l => l).Max(p => p.Y);
            double mx = PecaX + Junta, my = PecaY + Junta;
            int i0 = (int)Math.Floor(minX / mx) - 1, i1 = (int)Math.Ceiling(maxX / mx) + 1;
            int j0 = (int)Math.Floor(minY / my) - 1, j1 = (int)Math.Ceiling(maxY / my) + 1;
            if ((long)(i1 - i0) * (j1 - j0) > 400000) throw new InvalidOperationException("Peças pequenas demais para esta área (mais de 400 mil peças).");

            double cb = Math.Cos(Angulo), sb = Math.Sin(Angulo);
            P2 Volta(P2 p) { P2 q = Gira(p, cb, sb); return new P2(q.X + Origem.X, q.Y + Origem.Y); }

            // joint lines (one line per joint, at its centre), clipped to the region
            void Clipa(P2 a, P2 b)
            {
                var ts = new List<double>();
                foreach (var l in g)
                    for (int k = 0; k < l.Count; k++)
                    {
                        P2 p = l[k], q = l[(k + 1) % l.Count];
                        double d = (b.X - a.X) * (q.Y - p.Y) - (b.Y - a.Y) * (q.X - p.X);
                        if (Math.Abs(d) < 1e-12) continue;
                        double t = ((p.X - a.X) * (q.Y - p.Y) - (p.Y - a.Y) * (q.X - p.X)) / d;
                        double u = ((p.X - a.X) * (b.Y - a.Y) - (p.Y - a.Y) * (b.X - a.X)) / d;
                        if (u >= 0 && u < 1 && t >= 0 && t <= 1) ts.Add(t);
                    }
                ts.Sort();
                for (int k = 0; k + 1 < ts.Count; k++)
                {
                    double tm = (ts[k] + ts[k + 1]) / 2;
                    var pm = new P2(a.X + (b.X - a.X) * tm, a.Y + (b.Y - a.Y) * tm);
                    int dentro = g.Count(l => Dentro(l, pm));
                    if (dentro % 2 == 1 && (ts[k + 1] - ts[k]) * Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y)) > 1)
                        Linhas.Add(new[] { Volta(new P2(a.X + (b.X - a.X) * ts[k], a.Y + (b.Y - a.Y) * ts[k])), Volta(new P2(a.X + (b.X - a.X) * ts[k + 1], a.Y + (b.Y - a.Y) * ts[k + 1])) });
                }
            }
            double ya = minY - my, yb = maxY + my, xa = minX - mx, xb = maxX + mx;
            for (int i = i0; i <= i1; i++) { double x = i * mx - Junta / 2; if (x > minX && x < maxX) Clipa(new P2(x, ya), new P2(x, yb)); }
            for (int j = j0; j <= j1; j++) { double y = j * my - Junta / 2; if (y > minY && y < maxY) Clipa(new P2(xa, y), new P2(xb, y)); }

            // pieces: area of the region inside each tile
            double aPeca = PecaX * PecaY;
            for (int i = i0; i <= i1; i++)
                for (int j = j0; j <= j1; j++)
                {
                    double x0 = i * mx, y0 = j * my, x1 = x0 + PecaX, y1 = y0 + PecaY;
                    if (x1 < minX || x0 > maxX || y1 < minY || y0 > maxY) continue;
                    double a = 0;
                    var partes = new List<List<P2>>();
                    foreach (var l in g)
                    {
                        var r = Recorta(l, x0, y0, x1, y1);
                        partes.Add(r);
                    }
                    for (int k = 0; k < g.Count; k++)
                    {
                        if (partes[k].Count < 3) continue;
                        int dentro = 0;
                        for (int m = 0; m < g.Count; m++) if (m != k && Dentro(g[m], g[k][0])) dentro++;
                        a += (dentro % 2 == 0 ? 1 : -1) * Math.Abs(Area(partes[k]));
                    }
                    double f = a / aPeca;
                    if (f > 0.998) Inteiras++;
                    else if (f > 0.002) Recortes++;
                }
        }
    }
}
