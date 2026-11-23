using System;
using System.Collections.Generic;
using System.Linq;

namespace JAEsquadrias.Core
{
    /// <summary>
    /// Plan geometry of free-form countertops (mm): point in polygon, distance to the boundary,
    /// ray casting and the fitting of a dividing path (dry / wet areas) to the outline.
    /// Polygons are simple (no self-crossing), in either direction.
    /// </summary>
    public static class Poligono
    {
        /// <summary>Two points closer than this (mm) are the same point.</summary>
        public const double Tol = 1.0;

        public static double Dist(P2 a, P2 b) => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));

        public static P2 Meio(P2 a, P2 b) => new P2((a.X + b.X) / 2, (a.Y + b.Y) / 2);

        /// <summary>Even-odd test; points on the boundary may fall on either side.</summary>
        public static bool Dentro(IList<P2> poly, P2 p)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                P2 a = poly[i], b = poly[j];
                if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
            }
            return inside;
        }

        /// <summary>Distance from p to the segment a–b; q = the closest point of the segment.</summary>
        public static double DistSeg(P2 p, P2 a, P2 b, out P2 q)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y, l2 = dx * dx + dy * dy;
            double t = l2 < 1e-12 ? 0 : ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l2;
            t = Math.Max(0, Math.Min(1, t));
            q = new P2(a.X + dx * t, a.Y + dy * t);
            return Dist(p, q);
        }

        /// <summary>Distance from p to the boundary; aresta = index of the closest edge (poly[i] → poly[i+1]), q = closest point.</summary>
        public static double DistBorda(IList<P2> poly, P2 p, out int aresta, out P2 q)
        {
            double best = double.MaxValue;
            aresta = -1; q = p;
            for (int i = 0; i < poly.Count; i++)
            {
                double d = DistSeg(p, poly[i], poly[(i + 1) % poly.Count], out P2 c);
                if (d < best) { best = d; aresta = i; q = c; }
            }
            return best;
        }

        /// <summary>Distances (mm, along the unit direction d from o; may be negative) where the line o + t·d crosses the boundary, sorted, without repeats.</summary>
        public static List<double> Cortes(IList<P2> poly, P2 o, P2 d)
        {
            var ts = new List<double>();
            for (int i = 0; i < poly.Count; i++)
            {
                P2 a = poly[i], b = poly[(i + 1) % poly.Count];
                double ex = b.X - a.X, ey = b.Y - a.Y, len = Math.Sqrt(ex * ex + ey * ey);
                if (len < 1e-9) continue;
                double den = d.X * ey - d.Y * ex;
                if (Math.Abs(den) < 1e-9 * len) continue;                    // parallel
                double t = ((a.X - o.X) * ey - (a.Y - o.Y) * ex) / den;
                double s = ((a.X - o.X) * d.Y - (a.Y - o.Y) * d.X) / den;    // 0..1 along the edge
                if (s * len < -0.01 || s * len > len + 0.01) continue;
                ts.Add(t);
            }
            ts.Sort();
            var res = new List<double>();
            foreach (double t in ts) if (res.Count == 0 || t - res[res.Count - 1] > 0.05) res.Add(t);
            return res;
        }

        /// <summary>A point strictly inside the polygon (the centroid may be outside in L or U shapes).</summary>
        public static P2 Interior(IList<P2> poly)
        {
            if (poly == null || poly.Count == 0) return new P2(0, 0);
            double cx = poly.Average(p => p.X), cy = poly.Average(p => p.Y);
            var c = new P2(cx, cy);
            if (poly.Count >= 3 && Dentro(poly, c) && DistBorda(poly, c, out _, out _) > 5) return c;
            // widest inside stretch of a few horizontal lines
            double y0 = poly.Min(p => p.Y), y1 = poly.Max(p => p.Y);
            P2 best = c;
            double bestW = -1;
            for (int k = 1; k <= 9; k++)
            {
                double y = y0 + (y1 - y0) * k / 10.0 + 0.137;
                var ts = Cortes(poly, new P2(0, y), new P2(1, 0));
                for (int i = 0; i + 1 < ts.Count; i++)
                {
                    double xm = (ts[i] + ts[i + 1]) / 2, w = ts[i + 1] - ts[i];
                    if (w > bestW && Dentro(poly, new P2(xm, y))) { bestW = w; best = new P2(xm, y); }
                }
            }
            return best;
        }

        /// <summary>Consecutive points closer than <see cref="Tol"/> merged.</summary>
        public static List<P2> Limpar(IEnumerable<P2> pts, bool fechado)
        {
            var r = new List<P2>();
            foreach (P2 p in pts) if (r.Count == 0 || Dist(r[r.Count - 1], p) > Tol) r.Add(p);
            if (fechado) while (r.Count > 1 && Dist(r[0], r[r.Count - 1]) <= Tol) r.RemoveAt(r.Count - 1);
            return r;
        }

        /// <summary>
        /// Fits a dividing path to a polygon. Two points: the line through them, cut where it
        /// enters and leaves the polygon (the stretch that holds the middle of the two clicks,
        /// so the clicks need not be exactly on the edges). More points: the first and the last
        /// are moved onto the edge they are near (up to 60 mm) and the others must be inside.
        /// </summary>
        public static bool Ajustar(IList<P2> poly, IList<P2> caminho, out List<P2> res, out string erro)
        {
            res = null; erro = null;
            var pts = Limpar(caminho, false);
            if (pts.Count < 2) { erro = "a divisão precisa de dois pontos."; return false; }
            if (pts.Count == 2)
            {
                double len = Dist(pts[0], pts[1]);
                var d = new P2((pts[1].X - pts[0].X) / len, (pts[1].Y - pts[0].Y) / len);
                var ts = Cortes(poly, pts[0], d);
                double alvo = len / 2, bestDist = double.MaxValue;
                int best = -1;
                for (int i = 0; i + 1 < ts.Count; i++)
                {
                    if (ts[i + 1] - ts[i] < 2 * Tol) continue;
                    double tm = (ts[i] + ts[i + 1]) / 2;
                    if (!Dentro(poly, new P2(pts[0].X + d.X * tm, pts[0].Y + d.Y * tm))) continue;
                    // the clicked stretch (0..len) must reach this inside stretch (or stop less than 100 mm short of it)
                    if (Math.Max(ts[i] - len, -ts[i + 1]) > 100) continue;
                    double dist = alvo < ts[i] ? ts[i] - alvo : alvo > ts[i + 1] ? alvo - ts[i + 1] : 0;
                    if (dist < bestDist) { bestDist = dist; best = i; }
                }
                if (best < 0) { erro = "a linha não atravessa o tampo (clique dois pontos sobre ele ou nas bordas)."; return false; }
                res = new List<P2>
                {
                    new P2(pts[0].X + d.X * ts[best], pts[0].Y + d.Y * ts[best]),
                    new P2(pts[0].X + d.X * ts[best + 1], pts[0].Y + d.Y * ts[best + 1])
                };
                return true;
            }
            if (DistBorda(poly, pts[0], out _, out P2 q0) > 60 || DistBorda(poly, pts[pts.Count - 1], out _, out P2 q1) > 60)
            {
                erro = "a linha precisa começar e terminar numa borda do tampo.";
                return false;
            }
            for (int i = 1; i < pts.Count - 1; i++)
                if (!Dentro(poly, pts[i])) { erro = "os pontos do meio da linha precisam ficar dentro do tampo."; return false; }
            pts[0] = q0;
            pts[pts.Count - 1] = q1;
            res = Limpar(pts, false);
            if (res.Count < 2) { erro = "a linha é curta demais."; return false; }
            return true;
        }

        /// <summary>Index of the edge before (dir = −1) or after (dir = +1) edge i, skipping edges shorter than 0.5 mm.</summary>
        public static int Vizinha(IList<P2> poly, int i, int dir)
        {
            int n = poly.Count;
            for (int k = 1; k < n; k++)
            {
                int j = ((i + dir * k) % n + n) % n;
                if (Dist(poly[j], poly[(j + 1) % n]) >= 0.5) return j;
            }
            return i;
        }

        /// <summary>
        /// Plan outline of a strip (frontão, saia, tento…) along edge i of a polygon, from
        /// <paramref name="o"/> to o + w inside it, with the ends cut so that the strips of
        /// neighbouring edges meet without gaps or overlaps. For each end, {c0, c1}: the line of
        /// the neighbour edge moved c0 inside cuts the o side of the strip and moved c1 inside
        /// cuts the o + w side —
        /// {on, on + wn} = mitre with the neighbour's strip (from on to on + wn); {0, 0} = flush
        /// with the neighbour edge; {wn, wn} = stops at the face of a neighbour piece wn thick.
        /// Null = square end. Flush and stop only matter at convex corners (at a reflex corner
        /// the end is square). <paramref name="sinal"/>: +1 counter-clockwise polygon, −1 clockwise.
        /// Null when the edge is too short for the cuts.
        /// </summary>
        public static List<P2> Faixa(IList<P2> poly, double sinal, int i, double o, double w, double[] ini, double[] fim)
        {
            int n = poly.Count;
            P2 a = poly[i], b = poly[(i + 1) % n];
            double len = Dist(a, b);
            if (len < 0.5 || w <= 0) return null;
            var d = new P2((b.X - a.X) / len, (b.Y - a.Y) / len);
            var nrm = new P2(-d.Y * sinal, d.X * sinal);                 // towards the inside

            P2[] Ponta(P2 v, int viz, double[] corte, bool final)
            {
                var po = new P2(v.X + nrm.X * o, v.Y + nrm.Y * o);
                var pw = new P2(v.X + nrm.X * (o + w), v.Y + nrm.Y * (o + w));
                if (corte == null || viz == i) return new[] { po, pw };
                P2 va = poly[viz], vb = poly[(viz + 1) % n];
                double lv = Dist(va, vb);
                var dn = new P2((vb.X - va.X) / lv, (vb.Y - va.Y) / lv);
                // turning towards the inside at this corner?
                double giro = final ? d.X * dn.Y - d.Y * dn.X : dn.X * d.Y - dn.Y * d.X;
                bool convexo = giro * sinal > 0;
                if (!convexo && Math.Abs(corte[0] - corte[1]) < 1e-9) return new[] { po, pw };
                var nn = new P2(-dn.Y * sinal, dn.X * sinal);
                double lim = 6 * Math.Max(Math.Max(o + w, corte[1]), 1);
                return new[]
                {
                    Cruzar(po, d, new P2(v.X + nn.X * corte[0], v.Y + nn.Y * corte[0]), dn, lim),
                    Cruzar(pw, d, new P2(v.X + nn.X * corte[1], v.Y + nn.Y * corte[1]), dn, lim)
                };
            }

            P2[] s = Ponta(a, Vizinha(poly, i, -1), ini, false);
            P2[] e = Ponta(b, Vizinha(poly, i, 1), fim, true);
            double lo = (e[0].X - s[0].X) * d.X + (e[0].Y - s[0].Y) * d.Y;       // length left on the o side
            double lw = (e[1].X - s[1].X) * d.X + (e[1].Y - s[1].Y) * d.Y;       // and on the o + w side
            List<P2> res;
            if (lo < 0.5 && lw < 0.5) return null;
            if (lw < 0.5) res = new List<P2> { s[0], e[0], Meio(s[1], e[1]) };
            else if (lo < 0.5) res = new List<P2> { Meio(s[0], e[0]), e[1], s[1] };
            else res = new List<P2> { s[0], e[0], e[1], s[1] };
            double area = 0;
            for (int k = 0; k < res.Count; k++) area += res[k].X * res[(k + 1) % res.Count].Y - res[(k + 1) % res.Count].X * res[k].Y;
            return Math.Abs(area) / 2 < 1 ? null : res;
        }

        /// <summary>Where the line p + t·d meets the line q + u·dn; p itself when they are (almost) parallel or meet farther than lim from p.</summary>
        private static P2 Cruzar(P2 p, P2 d, P2 q, P2 dn, double lim)
        {
            double den = d.X * dn.Y - d.Y * dn.X;
            if (Math.Abs(den) < 0.02) return p;
            double t = ((q.X - p.X) * dn.Y - (q.Y - p.Y) * dn.X) / den;
            return Math.Abs(t) > lim ? p : new P2(p.X + d.X * t, p.Y + d.Y * t);
        }

        /// <summary>Points turned by <paramref name="graus"/> (counter-clockwise) about (cx, cy).</summary>
        public static List<P2> Girar(List<P2> pts, double cx, double cy, double graus)
        {
            if (Math.Abs(graus) < 1e-6) return pts;
            double a = graus * Math.PI / 180, cs = Math.Cos(a), sn = Math.Sin(a);
            return pts.Select(p => new P2(cx + (p.X - cx) * cs - (p.Y - cy) * sn, cy + (p.X - cx) * sn + (p.Y - cy) * cs)).ToList();
        }

        public static P2 Girar(P2 p, double cx, double cy, double graus) => Girar(new List<P2> { p }, cx, cy, graus)[0];

        /// <summary>Size of the box around the points, aligned with their longest edge: length (along it) and width.</summary>
        public static void Envolvente(IList<P2> poly, out double comprimento, out double largura)
        {
            comprimento = largura = 0;
            if (poly == null || poly.Count < 2) return;
            double best = -1, ux = 1, uy = 0;
            for (int i = 0; i < poly.Count; i++)
            {
                P2 a = poly[i], b = poly[(i + 1) % poly.Count];
                double l = Dist(a, b);
                if (l > best) { best = l; ux = (b.X - a.X) / l; uy = (b.Y - a.Y) / l; }
            }
            double u0 = double.MaxValue, u1 = double.MinValue, v0 = double.MaxValue, v1 = double.MinValue;
            foreach (P2 p in poly)
            {
                double u = p.X * ux + p.Y * uy, v = -p.X * uy + p.Y * ux;
                u0 = Math.Min(u0, u); u1 = Math.Max(u1, u); v0 = Math.Min(v0, v); v1 = Math.Max(v1, v);
            }
            comprimento = u1 - u0;
            largura = v1 - v0;
        }
    }
}
