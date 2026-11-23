using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using JAEsquadrias.Core;

namespace JAEsquadrias.UI
{
    public sealed class RenderOptions
    {
        public bool Dimensions = true;
        public bool Wall = true;
        public bool Caption = true;
        public int SelRow = -1, SelCol = -1;
        public Color Accent = Color.FromRgb(0x1F, 0x6E, 0x8C);
    }

    /// <summary>Screen ↔ model (mm) mapping of the last drawing, for hit testing.</summary>
    public sealed class RenderFrame
    {
        public double Scale, OffX, OffY;
        public Point ToScreen(double x, double y) => new Point(OffX + x * Scale, OffY - y * Scale);
        public Point ToModel(Point p) => new Point((p.X - OffX) / Scale, (OffY - p.Y) / Scale);
    }

    /// <summary>
    /// Draws the elevation of an esquadria (seen from the exterior) into a WPF Canvas from the
    /// very same <see cref="Layout"/> that becomes the Revit family: wall context, frame, sashes,
    /// glass, louvres, opening symbols, the selected module and the dimensions.
    /// </summary>
    public static class ElevationRenderer
    {
        private static readonly Brush DimBrush = Frozen(Color.FromRgb(0x6B, 0x76, 0x85));
        private static readonly Brush SymbolBrush = Frozen(Color.FromRgb(0x1E, 0x2A, 0x38));
        private static readonly Brush WallBrush = Frozen(Color.FromRgb(0xEC, 0xE8, 0xE2));
        private static readonly Brush WallLine = Frozen(Color.FromRgb(0xC9, 0xC2, 0xB8));
        private static readonly Brush RevBrush = Frozen(Color.FromRgb(0xF6, 0xF3, 0xEE));

        public static RenderFrame Draw(Canvas canvas, EsquadriaConfig cfg, Layout lay, RenderOptions o, double width, double height)
        {
            canvas.Children.Clear();
            var frame = new RenderFrame();
            if (width < 20 || height < 20 || lay == null) return frame;

            double W = lay.W, H = lay.H;
            double wm = o.Wall ? Math.Max(150, Math.Min(400, Math.Max(W, H) * 0.12)) : 0;
            double below = 0;
            if (o.Wall && !cfg.IsPorta) below = Math.Min(Math.Max(cfg.PeitorilMm, 0), 350);
            if (!o.Wall && !cfg.IsPorta && cfg.Peitoril) below = cfg.PeitorilEspessuraMm;
            double ml = o.Dimensions ? 62 : 4, mr = o.Dimensions ? 62 : 4, mt = o.Dimensions ? 44 : 4, mb = o.Dimensions ? 58 : 4;
            if (o.Caption && o.Dimensions) mt += 6;

            double spanX = W + 2 * wm, spanY = H + wm + below;
            double scale = Math.Min((width - ml - mr) / spanX, (height - mt - mb) / spanY);
            if (scale <= 0) return frame;
            frame.Scale = scale;
            frame.OffX = ml + ((width - ml - mr) - spanX * scale) / 2 + wm * scale;
            frame.OffY = mt + ((height - mt - mb) - spanY * scale) / 2 + (H + wm) * scale;

            // ------------------------------------------------------------ wall context
            if (o.Wall)
            {
                var wall = new PathGeometry { FillRule = FillRule.EvenOdd };
                wall.Figures.Add(Figure(frame, new Rc(-wm, -below, W + wm, H + wm).Loop()));
                wall.Figures.Add(Figure(frame, new Rc(0, 0, W, H).Loop()));
                canvas.Children.Add(new Path { Data = wall, Fill = WallBrush, Stroke = WallLine, StrokeThickness = 1 });
                if (cfg.IsPorta)
                {
                    var a = frame.ToScreen(-wm - 60, 0);
                    var b = frame.ToScreen(W + wm + 60, 0);
                    canvas.Children.Add(new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = SymbolBrush, StrokeThickness = 1.6 });
                }
            }

            // ------------------------------------------------------------ solids (far to near)
            Color profile = Hex(cfg.CorHex, Color.FromRgb(0xF2, 0xF2, 0xEF));
            var vidro = Catalog.Vidro(cfg.VidroId);
            Color glass = Hex(vidro.CorHex, Color.FromRgb(0xCF, 0xE6, 0xEE));
            foreach (var p in lay.Prismas.Where(p => p.Papel != Papel.Soleira && (p.Papel == Papel.Puxador ? cfg.IsPorta : p.Medium))
                                         .OrderByDescending(p => p.D0).ThenBy(p => p.Papel == Papel.Vidro ? 0 : 1))
            {
                Color fill;
                double alpha = 1;
                switch (p.Material)
                {
                    case MaterialSlot.Vidro: fill = glass; alpha = 0.78; break;
                    case MaterialSlot.Painel: fill = Mix(profile, Colors.White, 0.12); break;
                    case MaterialSlot.Peitoril: fill = Color.FromRgb(0xD8, 0xD3, 0xCA); break;
                    default: fill = profile; break;
                }
                var geo = new PathGeometry { FillRule = FillRule.EvenOdd };
                foreach (var loop in p.Loops) geo.Figures.Add(Figure(frame, loop));
                var brush = new SolidColorBrush(Color.FromArgb((byte)(alpha * 255), fill.R, fill.G, fill.B));
                var stroke = new SolidColorBrush(Edge(fill));
                canvas.Children.Add(new Path { Data = geo, Fill = brush, Stroke = stroke, StrokeThickness = scale > 0.15 ? 1 : 0.7, SnapsToDevicePixels = true });

                if (p.Papel == Papel.Vidro && p.Loops.Count > 0) Reflection(canvas, frame, p.Loops[0]);
            }

            // ------------------------------------------------------------ opening symbols
            foreach (var t in lay.Simbolos)
            {
                var a = frame.ToScreen(t.A.X, t.A.Y);
                var b = frame.ToScreen(t.B.X, t.B.Y);
                var line = new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = SymbolBrush, StrokeThickness = o.Dimensions ? 1.1 : 0.8 };
                if (t.Tracejado) line.StrokeDashArray = new DoubleCollection { 5, 3 };
                canvas.Children.Add(line);
            }

            // ------------------------------------------------------------ selection
            var sel = lay.Modulos.FirstOrDefault(m => m.Row == o.SelRow && m.Col == o.SelCol);
            if (sel != null && lay.Modulos.Count > 0)
            {
                var a = frame.ToScreen(sel.Mod.X0, sel.Mod.Y1);
                var b = frame.ToScreen(sel.Mod.X1, sel.Mod.Y0);
                var rect = new Rectangle
                {
                    Width = Math.Max(1, b.X - a.X), Height = Math.Max(1, b.Y - a.Y),
                    Stroke = new SolidColorBrush(o.Accent), StrokeThickness = 2.5,
                    Fill = new SolidColorBrush(Color.FromArgb(0x26, o.Accent.R, o.Accent.G, o.Accent.B)),
                    RadiusX = 2, RadiusY = 2, IsHitTestVisible = false
                };
                Canvas.SetLeft(rect, a.X);
                Canvas.SetTop(rect, a.Y);
                canvas.Children.Add(rect);
            }

            // ------------------------------------------------------------ dimensions
            if (o.Dimensions)
            {
                double gap = 18;
                // overall width (bottom)
                double yb = frame.ToScreen(0, -below).Y + gap + (cfg.IsPorta ? 6 : 0);
                HDim(canvas, frame.ToScreen(0, 0).X, frame.ToScreen(W, 0).X, yb, Layout.Mm(W), true);
                // overall height (right)
                double xr = frame.ToScreen(W + wm, 0).X + gap;
                VDim(canvas, frame.ToScreen(0, 0).Y, frame.ToScreen(0, H).Y, xr, Layout.Mm(H), true);

                // module widths of the selected (or first) row, on top
                int row = sel != null ? sel.Row : 0;
                var cells = lay.Modulos.Where(m => m.Row == row).OrderBy(m => m.Col).ToList();
                if (cells.Count > 1)
                {
                    double yt = frame.ToScreen(0, H + wm).Y - gap + 4;
                    foreach (var m in cells) HDim(canvas, frame.ToScreen(m.Mod.X0, 0).X, frame.ToScreen(m.Mod.X1, 0).X, yt, Layout.Mm(m.Mod.W), false);
                }
                // row heights, on the left
                var rows = lay.Modulos.GroupBy(m => m.Row).Select(g => g.First()).OrderBy(m => m.Row).ToList();
                if (rows.Count > 1)
                {
                    double xl = frame.ToScreen(-wm, 0).X - gap;
                    foreach (var m in rows) VDim(canvas, frame.ToScreen(0, m.Mod.Y0).Y, frame.ToScreen(0, m.Mod.Y1).Y, xl, Layout.Mm(m.Mod.H), false);
                }
                if (!cfg.IsPorta && o.Wall)
                {
                    var p0 = frame.ToScreen(-wm, -below);
                    Text(canvas, "peitoril " + Layout.Mm(cfg.PeitorilMm), p0.X + 4, p0.Y - 16, 10.5, DimBrush, false);
                }
                if (o.Caption)
                    Text(canvas, "VISTA EXTERNA", 6, 4, 10, DimBrush, true);
            }
            return frame;
        }

        /// <summary>
        /// Plan view (as it will look in Revit plans): the wall on both sides of the opening, cut,
        /// and the 2D representation of the esquadria — marco, folhas, open door leaf with its
        /// arc, guarnições, peitoril/soleira. Interior up, exterior down.
        /// </summary>
        public static RenderFrame DrawPlan(Canvas canvas, EsquadriaConfig cfg, Layout lay, RenderOptions o, double width, double height)
        {
            canvas.Children.Clear();
            var frame = new RenderFrame();
            if (width < 20 || height < 20 || lay == null) return frame;

            double W = lay.W;
            double wallStub = Math.Max(200, Math.Min(500, W * 0.25));
            double dMin = Math.Min(lay.FaceExt, 0), dMax = Math.Max(lay.FaceInt, lay.MarcoProf);
            foreach (var t in lay.Planta.Where(x => x.Visivel))
            {
                dMin = Math.Min(dMin, Math.Min(t.A.Y, t.B.Y));
                dMax = Math.Max(dMax, Math.Max(t.A.Y, t.B.Y));
                if (t.IsArc) { dMin = Math.Min(dMin, t.Mid.Y); dMax = Math.Max(dMax, t.Mid.Y); }
            }
            dMin -= 60; dMax += 60;
            double ml = 20, mr = 20, mt = o.Caption ? 28 : 12, mb = o.Dimensions ? 44 : 12;
            double spanX = W + 2 * wallStub, spanY = dMax - dMin;
            double scale = Math.Min((width - ml - mr) / spanX, (height - mt - mb) / spanY);
            if (scale <= 0) return frame;
            // RenderFrame maps (x, y) with y up on screen: here y = depth, interior (larger d) up
            frame.Scale = scale;
            frame.OffX = ml + ((width - ml - mr) - spanX * scale) / 2 + wallStub * scale;
            frame.OffY = mt + ((height - mt - mb) - spanY * scale) / 2 + dMax * scale;

            // wall stubs, cut (hatched look: light fill + outline)
            foreach (bool left in new[] { true, false })
            {
                double x0 = left ? -wallStub : W, x1 = left ? 0 : W + wallStub;
                var a = frame.ToScreen(x0, lay.ParedeInt);
                var b = frame.ToScreen(x1, lay.ParedeExt);
                var r = new Rectangle { Width = Math.Max(1, b.X - a.X), Height = Math.Max(1, b.Y - a.Y), Fill = WallBrush, Stroke = WallLine, StrokeThickness = 1 };
                Canvas.SetLeft(r, a.X);
                Canvas.SetTop(r, a.Y);
                canvas.Children.Add(r);
                // revestimento (finish) on each face: thin band up to the finished face
                foreach (bool ext in new[] { true, false })
                {
                    double f0 = ext ? lay.FaceExt : lay.ParedeInt, f1 = ext ? lay.ParedeExt : lay.FaceInt;
                    if (f1 - f0 < 0.5) continue;
                    var c0 = frame.ToScreen(x0, f1);
                    var c1 = frame.ToScreen(x1, f0);
                    var band = new Rectangle { Width = Math.Max(1, c1.X - c0.X), Height = Math.Max(1, c1.Y - c0.Y), Fill = RevBrush, Stroke = WallLine, StrokeThickness = 0.6, StrokeDashArray = new DoubleCollection { 3, 2 } };
                    Canvas.SetLeft(band, c0.X);
                    Canvas.SetTop(band, c0.Y);
                    canvas.Children.Add(band);
                }
            }

            var ink = SymbolBrush;
            foreach (var t in lay.Planta.Where(x => x.Visivel))
            {
                var a = frame.ToScreen(t.A.X, t.A.Y);
                var b = frame.ToScreen(t.B.X, t.B.Y);
                double th = t.Fino ? 0.8 : 1.3;
                if (t.IsArc)
                {
                    var m = frame.ToScreen(t.Mid.X, t.Mid.Y);
                    var fig = new PathFigure { StartPoint = a, IsClosed = false, IsFilled = false };
                    fig.Segments.Add(ArcThrough(a, m, b));
                    var g = new PathGeometry();
                    g.Figures.Add(fig);
                    var arc = new Path { Data = g, Stroke = ink, StrokeThickness = th };
                    if (t.Tracejado) arc.StrokeDashArray = new DoubleCollection { 5, 3 };
                    canvas.Children.Add(arc);
                }
                else
                {
                    var ln = new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = ink, StrokeThickness = th, SnapsToDevicePixels = true };
                    if (t.Tracejado) ln.StrokeDashArray = new DoubleCollection { 5, 3 };
                    canvas.Children.Add(ln);
                }
            }

            if (o.Dimensions)
            {
                double yb = frame.ToScreen(0, dMin).Y + 14;
                HDim(canvas, frame.ToScreen(0, 0).X, frame.ToScreen(W, 0).X, yb, Layout.Mm(W), true);
                Text(canvas, "EXTERIOR", frame.ToScreen(W + wallStub, 0).X - 64, frame.ToScreen(0, lay.FaceExt).Y + 4, 9.5, DimBrush, true);
                Text(canvas, "INTERIOR", frame.ToScreen(W + wallStub, 0).X - 64, frame.ToScreen(0, lay.FaceInt).Y - 16, 9.5, DimBrush, true);
            }
            if (o.Caption)
                Text(canvas, cfg.IsPorta ? "PLANTA" : "PLANTA (corte a 1,20 m)", 6, 4, 10, DimBrush, true);
            return frame;
        }

        /// <summary>WPF arc segment from a through m to b (circle through three points).</summary>
        private static ArcSegment ArcThrough(Point a, Point m, Point b)
        {
            double ax = a.X, ay = a.Y, bx = m.X, by = m.Y, cx = b.X, cy = b.Y;
            double d = 2 * (ax * (by - cy) + bx * (cy - ay) + cx * (ay - by));
            double r = 1;
            bool clockwise = true;
            if (Math.Abs(d) > 1e-9)
            {
                double ux = ((ax * ax + ay * ay) * (by - cy) + (bx * bx + by * by) * (cy - ay) + (cx * cx + cy * cy) * (ay - by)) / d;
                double uy = ((ax * ax + ay * ay) * (cx - bx) + (bx * bx + by * by) * (ax - cx) + (cx * cx + cy * cy) * (bx - ax)) / d;
                r = Math.Sqrt((ax - ux) * (ax - ux) + (ay - uy) * (ay - uy));
                // orientation of a -> m -> b (screen coordinates, y down)
                double cross = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
                clockwise = cross > 0;
            }
            return new ArcSegment(b, new Size(r, r), 0, false, clockwise ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true);
        }

        // ---------------------------------------------------------------- pieces
        private static PathFigure Figure(RenderFrame f, List<P2> loop)
        {
            var fig = new PathFigure { StartPoint = f.ToScreen(loop[0].X, loop[0].Y), IsClosed = true, IsFilled = true };
            for (int i = 1; i < loop.Count; i++) fig.Segments.Add(new LineSegment(f.ToScreen(loop[i].X, loop[i].Y), true));
            return fig;
        }

        private static void Reflection(Canvas c, RenderFrame f, List<P2> loop)
        {
            double x0 = loop.Min(p => p.X), x1 = loop.Max(p => p.X), y0 = loop.Min(p => p.Y), y1 = loop.Max(p => p.Y);
            double w = x1 - x0, h = y1 - y0;
            if (w * f.Scale < 30 || h * f.Scale < 30) return;
            double s = Math.Min(w, h);
            var brush = new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF));
            for (int i = 0; i < 2; i++)
            {
                double k = 0.18 + i * 0.1;
                var a = f.ToScreen(x1 - s * k - s * 0.18, y1 - s * 0.08);
                var b = f.ToScreen(x1 - s * 0.08, y1 - s * k - s * 0.18);
                c.Children.Add(new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = brush, StrokeThickness = 1.6, IsHitTestVisible = false });
            }
        }

        private static void HDim(Canvas c, double x0, double x1, double y, string text, bool strong)
        {
            if (x1 - x0 < 6) return;
            Seg(c, x0, y, x1, y);
            Tick(c, x0, y);
            Tick(c, x1, y);
            Seg(c, x0, y - 7, x0, y + 3);
            Seg(c, x1, y - 7, x1, y + 3);
            var tb = Text(c, text, 0, 0, strong ? 12 : 10.5, strong ? SymbolBrush : DimBrush, strong);
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(tb, (x0 + x1) / 2 - tb.DesiredSize.Width / 2);
            Canvas.SetTop(tb, y - tb.DesiredSize.Height - 1);
        }

        private static void VDim(Canvas c, double y0, double y1, double x, string text, bool strong)
        {
            double a = Math.Min(y0, y1), b = Math.Max(y0, y1);
            if (b - a < 6) return;
            Seg(c, x, a, x, b);
            Tick(c, x, a);
            Tick(c, x, b);
            Seg(c, x - 3, a, x + 7, a);
            Seg(c, x - 3, b, x + 7, b);
            var tb = Text(c, text, 0, 0, strong ? 12 : 10.5, strong ? SymbolBrush : DimBrush, strong);
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            tb.RenderTransform = new RotateTransform(-90);
            Canvas.SetLeft(tb, x - tb.DesiredSize.Height - 1);
            Canvas.SetTop(tb, (a + b) / 2 + tb.DesiredSize.Width / 2);
        }

        private static void Seg(Canvas c, double x0, double y0, double x1, double y1) =>
            c.Children.Add(new Line { X1 = x0, Y1 = y0, X2 = x1, Y2 = y1, Stroke = DimBrush, StrokeThickness = 0.9, IsHitTestVisible = false });

        /// <summary>Dimension terminator: filled dot (office standard).</summary>
        private static void Tick(Canvas c, double x, double y)
        {
            var e = new Ellipse { Width = 6, Height = 6, Fill = SymbolBrush, IsHitTestVisible = false };
            Canvas.SetLeft(e, x - 3);
            Canvas.SetTop(e, y - 3);
            c.Children.Add(e);
        }

        private static TextBlock Text(Canvas c, string s, double x, double y, double size, Brush b, bool bold)
        {
            var tb = new TextBlock { Text = s, FontSize = size, Foreground = b, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, IsHitTestVisible = false };
            Canvas.SetLeft(tb, x);
            Canvas.SetTop(tb, y);
            c.Children.Add(tb);
            return tb;
        }

        // ---------------------------------------------------------------- colours
        public static Color Hex(string hex, Color fallback)
        {
            hex = (hex ?? "").Trim().TrimStart('#');
            if (hex.Length != 6) return fallback;
            try
            {
                return Color.FromRgb(byte.Parse(hex.Substring(0, 2), NumberStyles.HexNumber),
                                     byte.Parse(hex.Substring(2, 2), NumberStyles.HexNumber),
                                     byte.Parse(hex.Substring(4, 2), NumberStyles.HexNumber));
            }
            catch { return fallback; }
        }

        public static Color Mix(Color a, Color b, double t) =>
            Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

        /// <summary>Outline colour: darker for light fills, lighter for dark ones.</summary>
        private static Color Edge(Color c)
        {
            double lum = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
            return lum > 90 ? Mix(c, Color.FromRgb(0x20, 0x26, 0x2E), 0.55) : Mix(c, Colors.White, 0.35);
        }

        private static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}
