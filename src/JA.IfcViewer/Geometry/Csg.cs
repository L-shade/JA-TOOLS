using System;
using System.Collections.Generic;

namespace JA.IfcViewer.Geometry
{
    /// <summary>
    /// Solid booleans on closed triangle meshes with BSP trees (the csg.js method by Evan Wallace), written without
    /// recursion so big meshes do not overflow the stack. Used for openings (walls minus windows and doors), clipping
    /// half spaces (walls cut by roofs) and IfcBooleanResult. Inputs must be closed and face outwards (Mesh.Orient).
    /// </summary>
    public static class Csg
    {
        private const double Eps = 1e-5;   // metres
        public const int MaxPolygons = 60000;

        private sealed class Plane
        {
            public Vec3 N;
            public double W;
            public Plane(Vec3 n, double w) { N = n; W = w; }
            public Plane Clone() => new Plane(N, W);
            public void Flip() { N = -N; W = -W; }

            public static Plane From(Vec3 a, Vec3 b, Vec3 c)
            {
                Vec3 n = (b - a).Cross(c - a);
                double l = n.Length;
                if (l < 1e-14) return null;
                n = n / l;
                return new Plane(n, n.Dot(a));
            }

            private const int Coplanar = 0, Front = 1, Back = 2, Spanning = 3;

            public void Split(Polygon poly, List<Polygon> coFront, List<Polygon> coBack, List<Polygon> front, List<Polygon> back)
            {
                int type = 0;
                int n = poly.V.Count;
                var types = new int[n];
                for (int i = 0; i < n; i++)
                {
                    double t = N.Dot(poly.V[i]) - W;
                    int ty = t < -Eps ? Back : t > Eps ? Front : Coplanar;
                    type |= ty;
                    types[i] = ty;
                }
                switch (type)
                {
                    case Coplanar: (N.Dot(poly.P.N) > 0 ? coFront : coBack).Add(poly); break;
                    case Front: front.Add(poly); break;
                    case Back: back.Add(poly); break;
                    default:
                        var f = new List<Vec3>(n + 1);
                        var b = new List<Vec3>(n + 1);
                        for (int i = 0; i < n; i++)
                        {
                            int j = (i + 1) % n;
                            int ti = types[i], tj = types[j];
                            Vec3 vi = poly.V[i], vj = poly.V[j];
                            if (ti != Back) f.Add(vi);
                            if (ti != Front) b.Add(vi);
                            if ((ti | tj) == Spanning)
                            {
                                double den = N.Dot(vj - vi);
                                double t = Math.Abs(den) > 1e-300 ? (W - N.Dot(vi)) / den : 0.5;
                                Vec3 v = Vec3.Lerp(vi, vj, t);
                                f.Add(v);
                                b.Add(v);
                            }
                        }
                        if (f.Count >= 3) front.Add(new Polygon(f, poly.P));
                        if (b.Count >= 3) back.Add(new Polygon(b, poly.P));
                        break;
                }
            }
        }

        private sealed class Polygon
        {
            public List<Vec3> V;
            public Plane P;
            public Polygon(List<Vec3> v, Plane p) { V = v; P = p; }
            public Polygon Flipped()
            {
                var v = new List<Vec3>(V);
                v.Reverse();
                var p = P.Clone();
                p.Flip();
                return new Polygon(v, p);
            }
        }

        private sealed class Node
        {
            public Plane P;
            public Node Front, Back;
            public List<Polygon> Polys = new List<Polygon>();

            public Node(List<Polygon> polys) { if (polys != null) Build(polys); }

            public void Build(List<Polygon> polys)
            {
                var st = new Stack<KeyValuePair<Node, List<Polygon>>>();
                st.Push(new KeyValuePair<Node, List<Polygon>>(this, polys));
                while (st.Count > 0)
                {
                    var kv = st.Pop();
                    Node node = kv.Key;
                    List<Polygon> ps = kv.Value;
                    if (ps.Count == 0) continue;
                    if (node.P == null) node.P = ps[0].P.Clone();
                    var f = new List<Polygon>();
                    var b = new List<Polygon>();
                    foreach (Polygon p in ps) node.P.Split(p, node.Polys, node.Polys, f, b);
                    if (f.Count > 0) { if (node.Front == null) node.Front = new Node(null); st.Push(new KeyValuePair<Node, List<Polygon>>(node.Front, f)); }
                    if (b.Count > 0) { if (node.Back == null) node.Back = new Node(null); st.Push(new KeyValuePair<Node, List<Polygon>>(node.Back, b)); }
                }
            }

            private IEnumerable<Node> Nodes()
            {
                var st = new Stack<Node>();
                st.Push(this);
                while (st.Count > 0)
                {
                    Node n = st.Pop();
                    yield return n;
                    if (n.Front != null) st.Push(n.Front);
                    if (n.Back != null) st.Push(n.Back);
                }
            }

            public void Invert()
            {
                foreach (Node n in Nodes())
                {
                    for (int i = 0; i < n.Polys.Count; i++) n.Polys[i] = n.Polys[i].Flipped();
                    n.P?.Flip();
                    Node t = n.Front; n.Front = n.Back; n.Back = t;
                }
            }

            /// <summary>The parts of the polygons outside the solid of this tree.</summary>
            public List<Polygon> ClipPolygons(List<Polygon> polys)
            {
                var result = new List<Polygon>();
                var st = new Stack<KeyValuePair<Node, List<Polygon>>>();
                st.Push(new KeyValuePair<Node, List<Polygon>>(this, polys));
                while (st.Count > 0)
                {
                    var kv = st.Pop();
                    Node node = kv.Key;
                    if (node.P == null) { result.AddRange(kv.Value); continue; }
                    var f = new List<Polygon>();
                    var b = new List<Polygon>();
                    foreach (Polygon p in kv.Value) node.P.Split(p, f, b, f, b);
                    if (node.Front != null) st.Push(new KeyValuePair<Node, List<Polygon>>(node.Front, f)); else result.AddRange(f);
                    if (node.Back != null) st.Push(new KeyValuePair<Node, List<Polygon>>(node.Back, b));
                }
                return result;
            }

            public void ClipTo(Node bsp)
            {
                foreach (Node n in Nodes()) n.Polys = bsp.ClipPolygons(n.Polys);
            }

            public List<Polygon> AllPolygons()
            {
                var r = new List<Polygon>();
                foreach (Node n in Nodes()) r.AddRange(n.Polys);
                return r;
            }
        }

        private static List<Polygon> ToPolygons(Mesh m)
        {
            var r = new List<Polygon>(m.TriangleCount);
            for (int i = 0; i + 2 < m.P.Count; i += 3)
            {
                Plane p = Plane.From(m.P[i], m.P[i + 1], m.P[i + 2]);
                if (p == null) continue;
                r.Add(new Polygon(new List<Vec3>(3) { m.P[i], m.P[i + 1], m.P[i + 2] }, p));
            }
            return r;
        }

        private static Mesh ToMesh(List<Polygon> polys)
        {
            var m = new Mesh(polys.Count * 3);
            foreach (Polygon p in polys)
                for (int i = 1; i + 1 < p.V.Count; i++) m.Add(p.V[0], p.V[i], p.V[i + 1]);
            return m;
        }

        /// <summary>a − b (null when the inputs are too big: keep a).</summary>
        public static Mesh Subtract(Mesh a, Mesh b)
        {
            var pa = ToPolygons(a); var pb = ToPolygons(b);
            if (pa.Count + pb.Count > MaxPolygons) return null;
            var A = new Node(pa); var B = new Node(pb);
            A.Invert(); A.ClipTo(B); B.ClipTo(A); B.Invert(); B.ClipTo(A); B.Invert(); A.Build(B.AllPolygons()); A.Invert();
            return ToMesh(A.AllPolygons());
        }

        public static Mesh Union(Mesh a, Mesh b)
        {
            var pa = ToPolygons(a); var pb = ToPolygons(b);
            if (pa.Count + pb.Count > MaxPolygons) return null;
            var A = new Node(pa); var B = new Node(pb);
            A.ClipTo(B); B.ClipTo(A); B.Invert(); B.ClipTo(A); B.Invert(); A.Build(B.AllPolygons());
            return ToMesh(A.AllPolygons());
        }

        public static Mesh Intersect(Mesh a, Mesh b)
        {
            var pa = ToPolygons(a); var pb = ToPolygons(b);
            if (pa.Count + pb.Count > MaxPolygons) return null;
            var A = new Node(pa); var B = new Node(pb);
            A.Invert(); B.ClipTo(A); B.Invert(); A.ClipTo(B); B.ClipTo(A); A.Build(B.AllPolygons()); A.Invert();
            return ToMesh(A.AllPolygons());
        }

        /// <summary>Axis-free box: the 8 corners of a frame cell (origin + u*[0..lu] + v*[0..lv] + w*[0..lw]), closed, outwards.</summary>
        public static Mesh Box(Vec3 o, Vec3 u, Vec3 v, Vec3 w)
        {
            Vec3[] c =
            {
                o, o + u, o + u + v, o + v,
                o + w, o + u + w, o + u + v + w, o + v + w
            };
            var m = new Mesh(36);
            void Q(int a, int b, int cc, int d) { m.Add(c[a], c[b], c[cc]); m.Add(c[a], c[cc], c[d]); }
            Q(0, 3, 2, 1); Q(4, 5, 6, 7); Q(0, 1, 5, 4); Q(1, 2, 6, 5); Q(2, 3, 7, 6); Q(3, 0, 4, 7);
            m.Orient();
            return m;
        }
    }
}
