using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using JAEsquadrias.Core;

namespace JAEsquadrias.UI
{
    public enum VistaPeca { Iso = 0, Planta = 1, Frente = 2 }

    public sealed class PecaRenderOptions
    {
        public bool Dimensions = true;
        /// <summary>Wall / floor around the piece (never part of the family).</summary>
        public bool Context = true;
        public Color Accent = Color.FromRgb(0x1F, 0x6E, 0x8C);
    }

    /// <summary>
    /// Preview of bancadas, nichos and chapins from the very same pieces the family is built
    /// from (PecaModelo). A tiny software renderer: every face is planar, so it is scan-filled
    /// with a depth buffer (correct hiding for sinks under the top, shelves inside niches...),
    /// flat-shaded, edges drawn with a depth test, 2× supersampled; dimensions and the plan
    /// symbolic lines go on top as WPF vectors.
    /// </summary>
    public static class PecaRenderer
    {
        private const int SS = 2;
        private const string Contexto = "contexto";
        private const string Fundo = "fundo";

        private sealed class Face
        {
            public List<List<V3>> Loops = new List<List<V3>>();
            public V3 Normal;
            public Color Color;
            public byte Alpha = 255;
            public bool Context;
        }

        private struct Cam
        {
            public V3 C, R, U;
            public double X(V3 p) => p.Dot(R);
            public double Y(V3 p) => -p.Dot(U);
            public double D(V3 p) => p.Dot(C);
        }

        public static string NomeVista(TipoPeca t, VistaPeca v) =>
            v == VistaPeca.Iso ? "3D" : v == VistaPeca.Planta ? "Planta" : t == TipoPeca.Chapim ? "Corte" : t == TipoPeca.Nicho ? "Elevação" : "Frente";

        /// <summary>Draws the piece; returns the model built for the preview.</summary>
        public static PecaModelo Draw(Canvas canvas, IPecaConfig cfg, VistaPeca vista, PecaRenderOptions o, double width, double height)
        {
            canvas.Children.Clear();
            if (cfg == null || width < 24 || height < 24) return null;
            o = o ?? new PecaRenderOptions();

            // chapins are drawn with a short length (the real one follows the wall)
            IPecaConfig shown = cfg;
            if (cfg is ChapimConfig ch)
            {
                var c2 = (ChapimConfig)ch.Copiar();
                c2.ComprimentoMm = Math.Min(c2.ComprimentoMm, Math.Max(500, c2.LarguraTotalMm * (vista == VistaPeca.Iso ? 2.6 : 3.2)));
                c2.PecaComprimentoMm = c2.PecaComprimentoMm > 0 && c2.PecaComprimentoMm < c2.ComprimentoMm ? c2.PecaComprimentoMm : 0;
                shown = c2;
            }
            PecaModelo m = shown.Build();
            var colors = new Dictionary<string, MaterialDef>();
            foreach (var md in shown.Materiais()) colors[md.Key] = md;

            var pieces = new List<Peca>(m.Pecas.Where(p => !p.Void && p.Fine));
            // niche plan = horizontal cut at mid height: what is fully above the cut is left out
            bool cutNiche = vista == VistaPeca.Planta && shown is NichoConfig;
            if (cutNiche) pieces = pieces.Where(p => MinZ(p) < ((NichoConfig)shown).AlturaMm / 2).ToList();
            if (o.Context) pieces.AddRange(ContextPieces(shown, m, cutNiche));

            Cam cam = Camera(cfg.Tipo, vista);
            var faces = new List<Face>();
            foreach (var p in pieces) faces.AddRange(Faces(p, colors));

            // fit
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            double bMinX = double.MaxValue, bMinY = double.MaxValue, bMaxX = double.MinValue, bMaxY = double.MinValue;
            foreach (var f in faces)
                foreach (var l in f.Loops)
                    foreach (var v in l)
                    {
                        double x = cam.X(v), y = cam.Y(v);
                        minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                        if (f.Context) continue;
                        bMinX = Math.Min(bMinX, x); bMaxX = Math.Max(bMaxX, x); bMinY = Math.Min(bMinY, y); bMaxY = Math.Max(bMaxY, y);
                    }
            if (minX > maxX) return m;
            bool dims = o.Dimensions && vista != VistaPeca.Iso;
            double margin = dims ? 44 : 6;
            double scale = Math.Min((width - 2 * margin) / Math.Max(1, maxX - minX), (height - 2 * margin) / Math.Max(1, maxY - minY));
            if (scale <= 0) return m;
            double offX = (width - (maxX - minX) * scale) / 2 - minX * scale;
            double offY = (height - (maxY - minY) * scale) / 2 - minY * scale;

            // raster
            int W = Math.Max(1, (int)Math.Ceiling(width)) * SS, H = Math.Max(1, (int)Math.Ceiling(height)) * SS;
            var r = new Raster(W, H);
            double s2 = scale * SS, ox2 = offX * SS, oy2 = offY * SS;
            double eps = 1.0 + 3.0 / scale;   // mm, depth tolerance of the edges
            var light = new V3(-0.45, -0.75, 1.0).Unit();

            var visible = faces.Where(f => f.Normal.Dot(cam.C) > 1e-6).ToList();
            foreach (var f in visible.Where(f => f.Alpha == 255))
                r.Fill(Project(f, cam, s2, ox2, oy2), Shade(f, light), false);
            foreach (var f in visible.Where(f => f.Alpha < 255).OrderBy(f => f.Loops[0].Average(v => cam.D(v))))
                r.Fill(Project(f, cam, s2, ox2, oy2), Shade(f, light), true);

            var edge = Color.FromRgb(0x2A, 0x33, 0x3D);
            var edgeCtx = Color.FromRgb(0xB5, 0xB0, 0xA8);
            foreach (var f in visible)
                foreach (var loop in Project(f, cam, s2, ox2, oy2))
                    for (int i = 0; i < loop.Count; i++)
                        r.Line(loop[i], loop[(i + 1) % loop.Count], f.Context ? edgeCtx : edge, eps);

            var img = new Image { Source = r.ToBitmap(), Width = W / (double)SS, Height = H / (double)SS, IsHitTestVisible = false };
            System.Windows.Media.RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            canvas.Children.Add(img);

            // plan symbolic lines (burners, joints, niche outline) over the plan
            if (vista == VistaPeca.Planta)
            {
                var stroke = new SolidColorBrush(Color.FromRgb(0x2A, 0x33, 0x3D));
                foreach (var l in m.Planta)
                {
                    double z = cfg.Tipo == TipoPeca.Bancada && cfg is BancadaConfig bc ? bc.AlturaMm + 1 : 0;
                    var a = new V3(l.A.X, l.A.Y, z);
                    var b = new V3(l.B.X, l.B.Y, z);
                    var ln = new Line
                    {
                        X1 = cam.X(a) * scale + offX, Y1 = cam.Y(a) * scale + offY,
                        X2 = cam.X(b) * scale + offX, Y2 = cam.Y(b) * scale + offY,
                        Stroke = stroke, StrokeThickness = 0.8
                    };
                    if (l.Tracejada) ln.StrokeDashArray = new DoubleCollection { 4, 3 };
                    canvas.Children.Add(ln);
                }
            }

            if (dims && bMinX <= bMaxX)
            {
                var ink = new SolidColorBrush(Color.FromRgb(0x5A, 0x64, 0x70));
                double x0 = bMinX * scale + offX, x1 = bMaxX * scale + offX, y0 = bMinY * scale + offY, y1 = bMaxY * scale + offY;
                double SX(V3 v) => cam.X(v) * scale + offX;
                double SY(V3 v) => cam.Y(v) * scale + offY;
                if (vista == VistaPeca.Frente && shown is BancadaConfig bf)
                {
                    // width of the piece and height of the top from the floor
                    DimH(canvas, x0, x1, Math.Max(y1, SY(V3.Zero)) + 18, bMaxX - bMinX, ink);
                    DimV(canvas, SY(new V3(0, 0, bf.AlturaMm)), SY(V3.Zero), x1 + 18, bf.AlturaMm, ink);
                }
                else if (vista == VistaPeca.Frente && shown is NichoConfig nf)
                {
                    // finished inside size
                    DimH(canvas, SX(new V3(-nf.LarguraMm / 2, 0, 0)), SX(new V3(nf.LarguraMm / 2, 0, 0)), y1 + 18, nf.LarguraMm, ink);
                    DimV(canvas, SY(new V3(0, 0, nf.AlturaMm)), SY(V3.Zero), x1 + 18, nf.AlturaMm, ink);
                }
                else
                {
                    DimH(canvas, x0, x1, y1 + 18, bMaxX - bMinX, ink);
                    DimV(canvas, y0, y1, x1 + 18, bMaxY - bMinY, ink);
                }
            }
            return m;
        }

        // ---------------------------------------------------------------------------- camera
        private static Cam Camera(TipoPeca t, VistaPeca v)
        {
            V3 c;
            switch (v)
            {
                case VistaPeca.Planta: c = V3.Ez; break;
                case VistaPeca.Frente: c = t == TipoPeca.Chapim ? V3.Ex : new V3(0, -1, 0); break;
                default:
                    c = t == TipoPeca.Chapim ? new V3(1.0, -0.85, 0.75) : t == TipoPeca.Nicho ? new V3(-0.55, -1, 0.45) : new V3(-0.55, -1, 0.8);
                    break;
            }
            c = c.Unit();
            V3 rr = Math.Abs(c.Z) > 0.99 ? V3.Ex : V3.Ez.Cross(c).Unit();
            V3 uu = c.Cross(rr).Unit();
            return new Cam { C = c, R = rr, U = uu };
        }

        // ---------------------------------------------------------------------------- faces
        private static IEnumerable<Face> Faces(Peca p, Dictionary<string, MaterialDef> mats)
        {
            Color col;
            byte alpha = 255;
            bool ctx = p.Slot == Contexto || p.Slot == Fundo;
            if (p.Slot == Contexto) col = Color.FromRgb(0xEE, 0xEB, 0xE6);
            else if (p.Slot == Fundo) col = Color.FromRgb(0xD9, 0xD4, 0xCC);
            else if (mats.TryGetValue(p.Slot ?? "", out MaterialDef md))
            {
                col = ElevationRenderer.Hex(md.Hex, Color.FromRgb(0xC8, 0xC8, 0xC8));
                if (md.Transparencia > 0) alpha = (byte)Math.Max(70, 255 - md.Transparencia * 255 / 100);
            }
            else col = Color.FromRgb(0xC8, 0xC8, 0xC8);

            V3 n = p.N.Unit();
            V3 top = p.N * (p.Depth / Math.Max(1e-9, p.N.Length));
            var loops = p.Loops.Where(l => l != null && l.Count >= 3).ToList();
            if (loops.Count == 0) yield break;

            var bottom = new Face { Normal = -n, Color = col, Alpha = alpha, Context = ctx };
            var cap = new Face { Normal = n, Color = col, Alpha = alpha, Context = ctx };
            if (p.Topo != null && p.Topo.Count == loops[0].Count)
            {
                // blend (mitred chamfer): base and top joined vertex to vertex
                var b0 = loops[0];
                bottom.Loops.Add(b0.Select(q => p.P(q)).ToList());
                cap.Loops.Add(p.Topo.Select(q => p.P(q) + top).ToList());
                yield return bottom;
                yield return cap;
                double sg0 = Math.Sign(Peca.Area(b0));
                for (int i = 0; i < b0.Count; i++)
                {
                    int j = (i + 1) % b0.Count;
                    V3 a = p.P(b0[i]), b = p.P(b0[j]), c = p.P(p.Topo[j]) + top, d = p.P(p.Topo[i]) + top;
                    V3 nn = (b - a).Cross(d - a).Unit() * -sg0;
                    if ((b - a).Length < 0.01 && (c - d).Length < 0.01) continue;
                    yield return new Face { Normal = nn, Color = col, Alpha = alpha, Context = ctx, Loops = { new List<V3> { a, b, c, d } } };
                }
                yield break;
            }
            foreach (var l in loops)
            {
                bottom.Loops.Add(l.Select(q => p.P(q)).ToList());
                cap.Loops.Add(l.Select(q => p.P(q) + top).ToList());
            }
            yield return bottom;
            yield return cap;

            for (int li = 0; li < loops.Count; li++)
            {
                var l = loops[li];
                double sgn = Math.Sign(Peca.Area(l)) * (li == 0 ? 1 : -1);
                if (sgn == 0) continue;
                for (int i = 0; i < l.Count; i++)
                {
                    V3 a = p.P(l[i]), b = p.P(l[(i + 1) % l.Count]);
                    V3 e = b - a;
                    if (e.Length < 0.01) continue;
                    V3 nn = e.Cross(n).Unit() * sgn;
                    yield return new Face
                    {
                        Normal = nn, Color = col, Alpha = alpha, Context = ctx,
                        Loops = { new List<V3> { a, b, b + top, a + top } }
                    };
                }
            }
        }

        private static Color Shade(Face f, V3 light)
        {
            double k = 0.64 + 0.36 * Math.Max(0, f.Normal.Dot(light));
            if (f.Normal.Z > 0.9) k = Math.Max(k, 0.97);
            byte S(byte v) => (byte)Math.Max(0, Math.Min(255, v * k + 255 * (1 - k) * 0.08));
            return Color.FromArgb(f.Alpha, S(f.Color.R), S(f.Color.G), S(f.Color.B));
        }

        private static List<List<Point3>> Project(Face f, Cam cam, double s, double ox, double oy) =>
            f.Loops.Select(l => l.Select(v => new Point3(cam.X(v) * s + ox, cam.Y(v) * s + oy, cam.D(v))).ToList()).ToList();

        // ---------------------------------------------------------------------------- context
        private static double MinZ(Peca p)
        {
            double z = double.MaxValue;
            V3 top = p.N * (p.Depth / Math.Max(1e-9, p.N.Length));
            foreach (var l in p.Loops)
                foreach (var q in l)
                {
                    V3 v = p.P(q);
                    z = Math.Min(z, Math.Min(v.Z, (v + top).Z));
                }
            return z;
        }

        private static IEnumerable<Peca> ContextPieces(IPecaConfig cfg, PecaModelo m, bool cutNiche)
        {
            switch (cfg)
            {
                case BancadaConfig b:
                {
                    double x0 = double.MaxValue, x1 = double.MinValue, y0 = double.MaxValue;
                    foreach (var p in m.Pecas)
                        foreach (var l in p.Loops)
                            foreach (var q in l)
                            {
                                V3 v = p.P(q);
                                x0 = Math.Min(x0, v.X); x1 = Math.Max(x1, v.X); y0 = Math.Min(y0, v.Y);
                            }
                    if (x0 > x1 || b.Livre) yield break;      // free form: the walls are wherever the project has them
                    double hWall = b.AlturaMm + (b.Frontao ? b.FrontaoAlturaMm : 0) + 450;
                    if (!b.FundoLivre) yield return Peca.Box("Parede", x0 - 150, 0, 0, x1 + 150, 90, hWall, Contexto);
                    yield return Peca.Box("Piso", x0 - 150, y0 - 350, -25, x1 + 150, 90, 0, Contexto);
                    break;
                }
                case NichoConfig n:
                {
                    double t = n.Revestimento ? n.RevestimentoEspessuraMm : 0;
                    double bb = n.BasePedra ? n.BaseEspessuraMm : t;
                    double dv = n.ProfundidadeMm + t + 2;
                    double wT = Math.Max(140, dv + 40);
                    double hx0 = -n.LarguraMm / 2 - t, hx1 = n.LarguraMm / 2 + t, hz0 = -bb, hz1 = n.AlturaMm + t;
                    double pad = Math.Max(250, n.LarguraMm * 0.45);
                    double ext = n.Moldura ? n.MolduraLarguraMm : 0;
                    if (cutNiche)
                    {
                        // wall cut at mid height of the niche: two jambs and what is left behind
                        double zc = n.AlturaMm / 2;
                        yield return Peca.Box("Parede", hx0 - pad - ext, 0, hz0 - 50, hx0, wT, zc, Contexto);
                        yield return Peca.Box("Parede", hx1, 0, hz0 - 50, hx1 + pad + ext, wT, zc, Contexto);
                        yield return Peca.Box("Fundo", hx0, dv, hz0 - 50, hx1, wT, zc, Fundo);
                        break;
                    }
                    // wall face with the opening: plane XZ at Y = wT, extruded towards −Y (out of the wall)
                    var wall = new Peca { Nome = "Parede", O = new V3(0, wT, 0), U = V3.Ex, V = V3.Ez, Depth = wT, Slot = Contexto };
                    wall.Loops.Add(Peca.Rect(hx0 - pad - ext, hz0 - pad - ext, hx1 + pad + ext, hz1 + pad + ext));
                    wall.Loops.Add(Peca.Rect(hx0, hz0, hx1, hz1));
                    yield return wall;
                    // what is left of the wall behind the niche
                    yield return Peca.Box("Fundo", hx0, dv, hz0, hx1, wT, hz1, Fundo);
                    break;
                }
                case ChapimConfig c:
                {
                    double y = c.EspessuraMuroMm / 2;
                    yield return Peca.Box("Muro", 0, -y, -Math.Max(250, c.LarguraTotalMm * 1.2), c.ComprimentoMm, y, 0, Contexto);
                    break;
                }
            }
        }

        // ---------------------------------------------------------------------------- dimensions
        private static void DimH(Canvas c, double x0, double x1, double y, double mm, Brush ink)
        {
            if (x1 - x0 < 12) return;
            Seg(c, x0, y, x1, y, ink);
            Seg(c, x0, y - 5, x0, y + 5, ink);
            Seg(c, x1, y - 5, x1, y + 5, ink);
            Tick(c, x0, y, ink); Tick(c, x1, y, ink);
            var t = Label(mm, ink);
            t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(t, (x0 + x1) / 2 - t.DesiredSize.Width / 2);
            Canvas.SetTop(t, y + 3);
            c.Children.Add(t);
        }

        private static void DimV(Canvas c, double y0, double y1, double x, double mm, Brush ink)
        {
            if (y1 - y0 < 12) return;
            Seg(c, x, y0, x, y1, ink);
            Seg(c, x - 5, y0, x + 5, y0, ink);
            Seg(c, x - 5, y1, x + 5, y1, ink);
            Tick(c, x, y0, ink); Tick(c, x, y1, ink);
            var t = Label(mm, ink);
            t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            t.LayoutTransform = new RotateTransform(-90);
            Canvas.SetLeft(t, x + 3);
            Canvas.SetTop(t, (y0 + y1) / 2 - t.DesiredSize.Width / 2);
            c.Children.Add(t);
        }

        private static void Seg(Canvas c, double x0, double y0, double x1, double y1, Brush b) =>
            c.Children.Add(new Line { X1 = x0, Y1 = y0, X2 = x1, Y2 = y1, Stroke = b, StrokeThickness = 0.8 });

        private static void Tick(Canvas c, double x, double y, Brush b)
        {
            var e = new Ellipse { Width = 3.6, Height = 3.6, Fill = b };
            Canvas.SetLeft(e, x - 1.8);
            Canvas.SetTop(e, y - 1.8);
            c.Children.Add(e);
        }

        private static TextBlock Label(double mm, Brush ink) =>
            new TextBlock { Text = Math.Round(mm).ToString("0", CultureInfo.InvariantCulture), FontSize = 11, Foreground = ink };

        // ---------------------------------------------------------------------------- raster
        private struct Point3
        {
            public double X, Y, D;
            public Point3(double x, double y, double d) { X = x; Y = y; D = d; }
        }

        private sealed class Raster
        {
            private readonly int _w, _h;
            private readonly uint[] _px;
            private readonly float[] _z;

            public Raster(int w, int h)
            {
                _w = w; _h = h;
                _px = new uint[w * h];
                _z = new float[w * h];
                for (int i = 0; i < _px.Length; i++) { _px[i] = 0xFFFFFFFF; _z[i] = float.NegativeInfinity; }
            }

            /// <summary>Scan-fills a planar face (outline + holes, even-odd) with a depth test.</summary>
            public void Fill(List<List<Point3>> loops, Color c, bool blend)
            {
                if (loops.Count == 0 || loops[0].Count < 3) return;
                // depth plane D = A·x + B·y + C from the three most spread vertices
                var l0 = loops[0];
                Point3 p0 = l0[0], p1 = l0[0], p2 = l0[0];
                double best = 0;
                foreach (var a in l0) if (Dist2(a, p0) > Dist2(p1, p0)) p1 = a;
                foreach (var a in l0)
                {
                    double cr = Math.Abs((p1.X - p0.X) * (a.Y - p0.Y) - (p1.Y - p0.Y) * (a.X - p0.X));
                    if (cr > best) { best = cr; p2 = a; }
                }
                double det = (p1.X - p0.X) * (p2.Y - p0.Y) - (p1.Y - p0.Y) * (p2.X - p0.X);
                if (Math.Abs(det) < 1e-6) return;   // seen edge-on
                double A = ((p1.D - p0.D) * (p2.Y - p0.Y) - (p1.Y - p0.Y) * (p2.D - p0.D)) / det;
                double B = ((p1.X - p0.X) * (p2.D - p0.D) - (p1.D - p0.D) * (p2.X - p0.X)) / det;
                double C = p0.D - A * p0.X - B * p0.Y;

                double minY = double.MaxValue, maxY = double.MinValue;
                foreach (var l in loops) foreach (var p in l) { minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y); }
                int y0 = Math.Max(0, (int)Math.Floor(minY)), y1 = Math.Min(_h - 1, (int)Math.Ceiling(maxY));
                uint argb = ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
                double alpha = c.A / 255.0;
                var xs = new List<double>();
                for (int y = y0; y <= y1; y++)
                {
                    double sy = y + 0.5;
                    xs.Clear();
                    foreach (var l in loops)
                        for (int i = 0; i < l.Count; i++)
                        {
                            Point3 a = l[i], b = l[(i + 1) % l.Count];
                            if ((a.Y <= sy && b.Y > sy) || (b.Y <= sy && a.Y > sy))
                                xs.Add(a.X + (sy - a.Y) * (b.X - a.X) / (b.Y - a.Y));
                        }
                    if (xs.Count < 2) continue;
                    xs.Sort();
                    for (int k = 0; k + 1 < xs.Count; k += 2)
                    {
                        int xa = Math.Max(0, (int)Math.Ceiling(xs[k] - 0.5)), xb = Math.Min(_w - 1, (int)Math.Floor(xs[k + 1] - 0.5));
                        for (int x = xa; x <= xb; x++)
                        {
                            int i = y * _w + x;
                            float d = (float)(A * (x + 0.5) + B * sy + C);
                            if (d < _z[i]) continue;
                            if (blend)
                            {
                                uint o = _px[i];
                                byte R = (byte)(c.R * alpha + ((o >> 16) & 0xFF) * (1 - alpha));
                                byte G = (byte)(c.G * alpha + ((o >> 8) & 0xFF) * (1 - alpha));
                                byte Bb = (byte)(c.B * alpha + (o & 0xFF) * (1 - alpha));
                                _px[i] = 0xFF000000 | ((uint)R << 16) | ((uint)G << 8) | Bb;
                            }
                            else
                            {
                                _px[i] = argb | 0xFF000000;
                                _z[i] = d;
                            }
                        }
                    }
                }
            }

            /// <summary>Edge line (2 px in the supersampled image = 1 px on screen), hidden behind nearer faces.</summary>
            public void Line(Point3 a, Point3 b, Color c, double eps)
            {
                double dx = b.X - a.X, dy = b.Y - a.Y;
                int n = (int)Math.Ceiling(Math.Max(Math.Abs(dx), Math.Abs(dy)));
                if (n == 0) n = 1;
                uint argb = 0xFF000000 | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
                for (int k = 0; k <= n; k++)
                {
                    double t = k / (double)n;
                    double x = a.X + dx * t - 0.5, y = a.Y + dy * t - 0.5, d = a.D + (b.D - a.D) * t;
                    int xi = (int)Math.Floor(x), yi = (int)Math.Floor(y);
                    for (int oy = 0; oy <= 1; oy++)
                        for (int ox = 0; ox <= 1; ox++)
                        {
                            int px = xi + ox, py = yi + oy;
                            if (px < 0 || py < 0 || px >= _w || py >= _h) continue;
                            int i = py * _w + px;
                            if (d + eps < _z[i]) continue;
                            _px[i] = argb;
                        }
                }
            }

            public BitmapSource ToBitmap()
            {
                var bmp = BitmapSource.Create(_w, _h, 96 * SS, 96 * SS, PixelFormats.Bgra32, null, _px, _w * 4);
                bmp.Freeze();
                return bmp;
            }

            private static double Dist2(Point3 a, Point3 b) => (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y);
        }
    }
}
