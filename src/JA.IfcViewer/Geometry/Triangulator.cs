using System;
using System.Collections.Generic;

namespace JA.IfcViewer.Geometry
{
    /// <summary>
    /// Polygon with holes → triangles. Holes are joined to the outer loop by bridges (the hole's rightmost vertex to
    /// the nearest outer vertex it can see), then the single loop is cut by ear clipping. Results are CCW triangles
    /// (positive area) as indices into the list "outer, then each hole in order".
    /// </summary>
    public static class Triangulator
    {
        private const double Eps = 1e-12;

        public static List<int> Triangulate(IList<Vec2> outer, IList<IList<Vec2>> holes = null)
        {
            var tris = new List<int>();
            var pts = new List<Vec2>();
            var outerIdx = Clean(outer, pts, 0);
            if (outerIdx.Count < 3) return tris;
            if (Area(pts, outerIdx) < 0) outerIdx.Reverse();
            double scale = Extent(pts, outerIdx);
            double tol = Math.Max(1e-12, scale * 1e-9);

            // holes: cleaned, clockwise, sorted by their rightmost x (largest first)
            var holeLoops = new List<List<int>>();
            int offset = outer.Count;
            if (holes != null)
            {
                foreach (IList<Vec2> h in holes)
                {
                    var idx = Clean(h, pts, offset);
                    offset += h.Count;
                    if (idx.Count < 3) continue;
                    if (Math.Abs(Area(pts, idx)) < tol * tol) continue;
                    if (Area(pts, idx) > 0) idx.Reverse();
                    holeLoops.Add(idx);
                }
            }
            List<int> poly = outerIdx;
            if (holeLoops.Count > 0)
            {
                holeLoops.Sort((a, b) => MaxX(pts, b).CompareTo(MaxX(pts, a)));
                for (int h = 0; h < holeLoops.Count; h++)
                    poly = Bridge(pts, poly, holeLoops[h], holeLoops, h + 1);
            }
            EarClip(pts, poly, tris, tol);
            return tris;
        }

        /// <summary>Indices of the loop without repeated / closing points (pts receives the positions at index offset+i).</summary>
        private static List<int> Clean(IList<Vec2> loop, List<Vec2> pts, int offset)
        {
            while (pts.Count < offset) pts.Add(default(Vec2));
            for (int i = 0; i < loop.Count; i++) pts.Add(loop[i]);
            var idx = new List<int>(loop.Count);
            double ext = 0;
            for (int i = 0; i < loop.Count; i++) ext = Math.Max(ext, Math.Max(Math.Abs(loop[i].X), Math.Abs(loop[i].Y)));
            double tol2 = Math.Pow(Math.Max(1e-12, ext * 1e-10), 2);
            for (int i = 0; i < loop.Count; i++)
            {
                int k = offset + i;
                if (idx.Count > 0 && Dist2(pts[idx[idx.Count - 1]], pts[k]) <= tol2) continue;
                idx.Add(k);
            }
            while (idx.Count > 1 && Dist2(pts[idx[0]], pts[idx[idx.Count - 1]]) <= tol2) idx.RemoveAt(idx.Count - 1);
            return idx;
        }

        private static double Dist2(Vec2 a, Vec2 b) { double dx = a.X - b.X, dy = a.Y - b.Y; return dx * dx + dy * dy; }

        public static double Area(IList<Vec2> pts, IList<int> idx)
        {
            double a = 0;
            for (int i = 0; i < idx.Count; i++)
            {
                Vec2 p = pts[idx[i]], q = pts[idx[(i + 1) % idx.Count]];
                a += p.X * q.Y - q.X * p.Y;
            }
            return a * 0.5;
        }

        public static double Area(IList<Vec2> loop)
        {
            double a = 0;
            for (int i = 0; i < loop.Count; i++)
            {
                Vec2 p = loop[i], q = loop[(i + 1) % loop.Count];
                a += p.X * q.Y - q.X * p.Y;
            }
            return a * 0.5;
        }

        private static double Extent(List<Vec2> pts, List<int> idx)
        {
            double minx = double.MaxValue, miny = double.MaxValue, maxx = double.MinValue, maxy = double.MinValue;
            foreach (int i in idx)
            {
                Vec2 p = pts[i];
                if (p.X < minx) minx = p.X; if (p.X > maxx) maxx = p.X;
                if (p.Y < miny) miny = p.Y; if (p.Y > maxy) maxy = p.Y;
            }
            return Math.Max(maxx - minx, maxy - miny);
        }

        private static double MaxX(List<Vec2> pts, List<int> loop)
        {
            double m = double.MinValue;
            foreach (int i in loop) if (pts[i].X > m) m = pts[i].X;
            return m;
        }

        /// <summary>Joins a hole into the polygon through the shortest bridge that crosses no edge.</summary>
        private static List<int> Bridge(List<Vec2> pts, List<int> poly, List<int> hole, List<List<int>> holes, int nextHole)
        {
            int hm = 0;
            for (int i = 1; i < hole.Count; i++) if (pts[hole[i]].X > pts[hole[hm]].X) hm = i;
            Vec2 m = pts[hole[hm]];
            var order = new List<int>(poly.Count);
            for (int i = 0; i < poly.Count; i++) order.Add(i);
            order.Sort((a, b) => Dist2(pts[poly[a]], m).CompareTo(Dist2(pts[poly[b]], m)));
            int best = -1;
            int tries = 0;
            foreach (int pi in order)
            {
                if (++tries > 400) break;
                Vec2 p = pts[poly[pi]];
                if (Dist2(p, m) < 1e-30) { best = pi; break; }
                if (!InCone(pts, poly, pi, m)) continue;
                if (CrossesAny(pts, poly, p, m, poly[pi], -1)) continue;
                if (CrossesAny(pts, hole, p, m, -1, hole[hm])) continue;
                bool blocked = false;
                for (int h = nextHole; h < holes.Count && !blocked; h++) blocked = CrossesAny(pts, holes[h], p, m, -1, -1);
                if (blocked) continue;
                best = pi;
                break;
            }
            if (best < 0) best = order[0];
            var r = new List<int>(poly.Count + hole.Count + 2);
            for (int i = 0; i <= best; i++) r.Add(poly[i]);
            for (int k = 0; k <= hole.Count; k++) r.Add(hole[(hm + k) % hole.Count]);
            r.Add(poly[best]);
            for (int i = best + 1; i < poly.Count; i++) r.Add(poly[i]);
            return r;
        }

        /// <summary>Whether point m lies inside the interior angle at vertex i of the polygon (so the bridge goes inwards).</summary>
        private static bool InCone(List<Vec2> pts, List<int> poly, int i, Vec2 m)
        {
            int n = poly.Count;
            Vec2 a = pts[poly[(i - 1 + n) % n]], b = pts[poly[i]], c = pts[poly[(i + 1) % n]];
            double turn = (b - a).Cross(c - b);
            double s1 = (b - a).Cross(m - a) , s2 = (c - b).Cross(m - b);
            if (turn >= 0) return s1 >= -Eps && s2 >= -Eps;   // convex vertex: m left of both edges
            return s1 >= -Eps || s2 >= -Eps;                   // reflex vertex
        }

        private static bool CrossesAny(List<Vec2> pts, List<int> loop, Vec2 p, Vec2 q, int skipA, int skipB)
        {
            int n = loop.Count;
            for (int i = 0; i < n; i++)
            {
                int ia = loop[i], ib = loop[(i + 1) % n];
                if (ia == skipA || ib == skipA || ia == skipB || ib == skipB) continue;
                Vec2 a = pts[ia], b = pts[ib];
                if (Dist2(a, p) < 1e-30 || Dist2(b, p) < 1e-30 || Dist2(a, q) < 1e-30 || Dist2(b, q) < 1e-30) continue;
                if (SegmentsCross(p, q, a, b)) return true;
            }
            return false;
        }

        private static bool SegmentsCross(Vec2 p1, Vec2 p2, Vec2 q1, Vec2 q2)
        {
            double d1 = (p2 - p1).Cross(q1 - p1), d2 = (p2 - p1).Cross(q2 - p1);
            double d3 = (q2 - q1).Cross(p1 - q1), d4 = (q2 - q1).Cross(p2 - q1);
            return ((d1 > Eps && d2 < -Eps) || (d1 < -Eps && d2 > Eps)) && ((d3 > Eps && d4 < -Eps) || (d3 < -Eps && d4 > Eps));
        }

        // ------------------------------------------------------------------ ear clipping

        private static void EarClip(List<Vec2> pts, List<int> poly, List<int> tris, double tol)
        {
            int n = poly.Count;
            if (n < 3) return;
            if (n == 3) { tris.Add(poly[0]); tris.Add(poly[1]); tris.Add(poly[2]); return; }
            var prev = new int[n];
            var next = new int[n];
            for (int i = 0; i < n; i++) { prev[i] = (i - 1 + n) % n; next[i] = (i + 1) % n; }
            int remaining = n, cur = 0, guard = 0;
            double areaTol = tol * tol;
            while (remaining > 3)
            {
                int a = prev[cur], b = cur, c = next[cur];
                if (IsEar(pts, poly, prev, next, a, b, c, remaining, areaTol))
                {
                    tris.Add(poly[a]); tris.Add(poly[b]); tris.Add(poly[c]);
                    next[a] = c; prev[c] = a;
                    remaining--;
                    cur = c;
                    guard = 0;
                    continue;
                }
                // zero-area (collinear or doubled) vertex: drop without a triangle
                double cr = (pts[poly[b]] - pts[poly[a]]).Cross(pts[poly[c]] - pts[poly[b]]);
                if (Math.Abs(cr) <= areaTol && Dist2(pts[poly[a]], pts[poly[c]]) > 0 && Collinearish(pts[poly[a]], pts[poly[b]], pts[poly[c]]))
                {
                    next[a] = c; prev[c] = a; remaining--; cur = c; guard = 0; continue;
                }
                cur = next[cur];
                if (++guard > remaining)
                {
                    // no ear found (self-intersecting or numerically bad input): clip the most convex vertex
                    int bestV = -1; double bestCr = double.MinValue;
                    int v = cur;
                    for (int k = 0; k < remaining; k++)
                    {
                        double x = (pts[poly[v]] - pts[poly[prev[v]]]).Cross(pts[poly[next[v]]] - pts[poly[v]]);
                        if (x > bestCr) { bestCr = x; bestV = v; }
                        v = next[v];
                    }
                    if (bestV < 0) return;
                    a = prev[bestV]; c = next[bestV];
                    if (bestCr > 0) { tris.Add(poly[a]); tris.Add(poly[bestV]); tris.Add(poly[c]); }
                    next[a] = c; prev[c] = a; remaining--; cur = c; guard = 0;
                }
            }
            {
                int a = prev[cur], b = cur, c = next[cur];
                double cr = (pts[poly[b]] - pts[poly[a]]).Cross(pts[poly[c]] - pts[poly[b]]);
                if (cr > areaTol) { tris.Add(poly[a]); tris.Add(poly[b]); tris.Add(poly[c]); }
            }
        }

        private static bool Collinearish(Vec2 a, Vec2 b, Vec2 c)
        {
            // b lies on the line a-c (either between them or a spike back)
            double len = Math.Sqrt(Dist2(a, c)) + Math.Sqrt(Dist2(a, b));
            return Math.Abs((c - a).Cross(b - a)) <= 1e-9 * len * len + 1e-24;
        }

        private static bool IsEar(List<Vec2> pts, List<int> poly, int[] prev, int[] next, int a, int b, int c, int remaining, double areaTol)
        {
            Vec2 pa = pts[poly[a]], pb = pts[poly[b]], pc = pts[poly[c]];
            double cr = (pb - pa).Cross(pc - pb);
            if (cr <= areaTol) return false;   // reflex or flat
            int v = next[c];
            for (int k = 0; k < remaining - 3; k++, v = next[v])
            {
                int iv = poly[v];
                if (iv == poly[a] || iv == poly[b] || iv == poly[c]) continue;
                Vec2 p = pts[iv];
                if (Dist2(p, pa) < 1e-30 || Dist2(p, pb) < 1e-30 || Dist2(p, pc) < 1e-30) continue;
                if (InTriangle(pa, pb, pc, p)) return false;
            }
            return true;
        }

        private static bool InTriangle(Vec2 a, Vec2 b, Vec2 c, Vec2 p)
        {
            double d1 = (b - a).Cross(p - a), d2 = (c - b).Cross(p - b), d3 = (a - c).Cross(p - c);
            return d1 >= -Eps && d2 >= -Eps && d3 >= -Eps;
        }

        // ================================================================== 3D faces

        /// <summary>Newell normal of a 3D loop (length = twice the area).</summary>
        public static Vec3 Newell(IList<Vec3> loop)
        {
            double x = 0, y = 0, z = 0;
            for (int i = 0; i < loop.Count; i++)
            {
                Vec3 p = loop[i], q = loop[(i + 1) % loop.Count];
                x += (p.Y - q.Y) * (p.Z + q.Z);
                y += (p.Z - q.Z) * (p.X + q.X);
                z += (p.X - q.X) * (p.Y + q.Y);
            }
            return new Vec3(x, y, z);
        }

        /// <summary>A planar (or nearly planar) 3D face with holes, triangulated and appended to the mesh with the outer
        /// loop's orientation (triangles face the side the outer loop turns counter-clockwise about).</summary>
        public static void AddFace(Mesh mesh, IList<Vec3> outer, IList<IList<Vec3>> holes = null)
        {
            int n = outer.Count;
            if (n < 3) return;
            if (n == 3 && (holes == null || holes.Count == 0)) { mesh.Add(outer[0], outer[1], outer[2]); return; }
            Vec3 nrm = Newell(outer);
            if (nrm.LengthSq < 1e-30) return;
            nrm = nrm.Normalized();
            if (n == 4 && (holes == null || holes.Count == 0) && IsConvexQuad(outer, nrm))
            {
                mesh.Add(outer[0], outer[1], outer[2]);
                mesh.Add(outer[0], outer[2], outer[3]);
                return;
            }
            Vec3 u = nrm.AnyPerpendicular(), v = nrm.Cross(u);
            Vec3 o = outer[0];
            var all = new List<Vec3>(outer);
            var o2 = new List<Vec2>(n);
            foreach (Vec3 p in outer) o2.Add(new Vec2((p - o).Dot(u), (p - o).Dot(v)));
            List<IList<Vec2>> h2 = null;
            if (holes != null && holes.Count > 0)
            {
                h2 = new List<IList<Vec2>>();
                foreach (IList<Vec3> h in holes)
                {
                    var l = new List<Vec2>(h.Count);
                    foreach (Vec3 p in h) { l.Add(new Vec2((p - o).Dot(u), (p - o).Dot(v))); all.Add(p); }
                    h2.Add(l);
                }
            }
            bool reversed = Area(o2) < 0;   // should not happen with the Newell frame; keep the outer orientation
            List<int> t = Triangulate(o2, h2);
            for (int i = 0; i + 2 < t.Count; i += 3)
            {
                if (!reversed) mesh.Add(all[t[i]], all[t[i + 1]], all[t[i + 2]]);
                else mesh.Add(all[t[i]], all[t[i + 2]], all[t[i + 1]]);
            }
        }

        private static bool IsConvexQuad(IList<Vec3> q, Vec3 n)
        {
            for (int i = 0; i < 4; i++)
            {
                Vec3 a = q[i], b = q[(i + 1) % 4], c = q[(i + 2) % 4];
                if ((b - a).Cross(c - b).Dot(n) <= 0) return false;
            }
            return true;
        }
    }
}
