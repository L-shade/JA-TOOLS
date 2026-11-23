using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using JAEsquadrias.Core;
using JAEsquadrias.Core.Paineis;
using static JAEsquadrias.UI.CompactForm;

namespace JAEsquadrias.UI
{
    /// <summary>A JA panel design (code) already in the project, with all its sizes.</summary>
    public sealed class PainelExistente
    {
        public string Codigo;
        public PainelConfig Config;
        public int Tamanhos;
        public int Usos;
    }

    /// <summary>One panel size among the selected curtain panels.</summary>
    public sealed class MedidaPainel
    {
        public double W, H;
        public int N;
        public override string ToString() => Fmt(W) + " × " + Fmt(H) + (N > 0 ? "  (" + N + ")" : "");
        internal static string Fmt(double v) => v.ToString("0", CultureInfo.InvariantCulture);
    }

    public sealed class PainelPedido
    {
        public PainelConfig Config;
        public PainelExistente Editando;
        public bool Aplicar;
    }

    /// <summary>
    /// Painel de Cortina: configurator of curtain-wall panels that are not doors or windows.
    /// Left: front view and a vertical section (horizontal section for brises) drawn live from
    /// <see cref="PainelConfig.Build"/>; right: kind as tiles and only the options of that kind.
    /// The size comes from the selected panels (one family per size); without a selection it is typed.
    /// </summary>
    public sealed class PainelWindow : Window
    {
        public static PainelPedido Abrir(IntPtr owner, PainelConfig start, List<PainelExistente> existentes, List<string> materiais,
                                         List<MedidaPainel> tamanhos, int selecionados, PainelExistente editar = null)
        {
            var w = new PainelWindow(start, existentes, materiais, tamanhos, selecionados, editar);
            if (owner != IntPtr.Zero) new WindowInteropHelper(w).Owner = owner;
            return w.ShowDialog() == true ? w._pedido : null;
        }

        private static readonly TipoPainel[] Tipos =
            { TipoPainel.Cassete, TipoPainel.Solido, TipoPainel.Vidro, TipoPainel.Sanduiche, TipoPainel.Veneziana, TipoPainel.Brise, TipoPainel.Emoldurado };

        private PainelConfig _c;
        private readonly List<PainelExistente> _exist;
        private readonly List<MedidaPainel> _tams;
        private readonly int _sel;
        private PainelPedido _pedido;
        private bool _loading;

        private readonly Canvas _frente = new Canvas { ClipToBounds = true };
        private readonly Canvas _corte = new Canvas { ClipToBounds = true };
        private TextBlock _corteTitulo;
        private readonly TextBlock _desc = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 8, 0, 0) };
        private readonly TextBlock _avisos = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Margin = new Thickness(0, 6, 0, 0) };
        private readonly ComboBox _editar = new ComboBox();
        private readonly ComboBox _tamanho = new ComboBox { MinWidth = 200 };
        private readonly Dictionary<TipoPainel, RadioButton> _tipo = new Dictionary<TipoPainel, RadioButton>();

        private readonly TextBox _w = Num(), _h = Num(), _e = Num(), _off = Num(), _junta = Num(), _chapa = Num(), _ret = Num();
        private readonly TextBox _al = Num(), _ae = Num(), _passo = Num(), _ang = Num(), _mf = Num(), _mp = Num(), _trav = Num();
        private readonly TextBox _codigo = new TextBox { Width = 90 };
        private readonly TextBox _cor = new TextBox { Width = 72 };
        private readonly ComboBox _mat = Combo(), _matVidro = Combo(), _matNucleo = Combo(), _matMoldura = Combo();
        private readonly CheckBox _inverter = Toggle("Inverter interior / exterior"), _aplicar = Toggle("");
        private readonly RadioButton _vidroSim = new RadioButton { Content = "Vidro", GroupName = "painel-preench" }, _vidroNao = new RadioButton { Content = "Painel cego", GroupName = "painel-preench" };
        private readonly TextBlock _familia = new TextBlock { FontSize = 11.5, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
        private FrameworkElement _lE, _lChapa, _lRet, _rowMatVidro, _rowMatNucleo, _rowMatMoldura, _rowPreench, _lTrav, _cardTamanhoFixo, _cardTamanhoLivre;
        private TextBlock _lEText;
        private Border _cardChapas, _cardAletas, _cardMoldura;
        private Button _ok;

        private PainelWindow(PainelConfig start, List<PainelExistente> existentes, List<string> materiais, List<MedidaPainel> tamanhos, int selecionados, PainelExistente editar)
        {
            _c = (start ?? new PainelConfig()).Copiar();
            _exist = existentes ?? new List<PainelExistente>();
            _tams = tamanhos ?? new List<MedidaPainel>();
            _sel = selecionados;
            Width = 1080; Height = 760; MinWidth = 900; MinHeight = 600;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 380 });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.05, GridUnitType.Star), MinWidth = 430 });
            body.Children.Add(Preview());
            var right = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = Options(materiais) };
            Grid.SetColumn(right, 2);
            body.Children.Add(right);

            ToolChrome.DressCompact(this, "Painel de Cortina", body, Footer(),
                "Painéis de parede cortina que não são porta nem janela: cassete ACM, sólido, vidro, sanduíche, veneziana, brise e emoldurado.");
            Load(_c);
            if (editar != null)
            {
                int i = _exist.IndexOf(editar);
                if (i >= 0) _editar.SelectedIndex = i + 1;
            }
            Loaded += (s, e) => Redraw();
            _frente.SizeChanged += (s, e) => Redraw();
            _corte.SizeChanged += (s, e) => Redraw();
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { Confirm(); e.Handled = true; }
            };
        }

        // ================================================================== left: drawings

        private UIElement Preview()
        {
            var dock = new DockPanel();
            var info = new StackPanel();
            _desc.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Text");
            _avisos.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Warn");
            info.Children.Add(_desc);
            info.Children.Add(_avisos);
            DockPanel.SetDock(info, Dock.Bottom);
            dock.Children.Add(info);

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.7, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(Quadro(_frente, "Frente (vista de fora)", out _));
            var c2 = Quadro(_corte, "Corte", out _corteTitulo);
            Grid.SetRow(c2, 2);
            grid.Children.Add(c2);
            dock.Children.Add(grid);
            return ToolChrome.Card(dock, "Painel");
        }

        private static FrameworkElement Quadro(Canvas c, string titulo, out TextBlock title)
        {
            var g = new Grid();
            var frame = new Border { CornerRadius = new CornerRadius(6), Child = c, BorderThickness = new Thickness(1) };
            frame.SetResourceReference(Border.BackgroundProperty, "Ja.Field");
            frame.SetResourceReference(Border.BorderBrushProperty, "Ja.Line");
            g.Children.Add(frame);
            title = new TextBlock { Text = titulo, FontSize = 10.5, Margin = new Thickness(8, 5, 0, 0), IsHitTestVisible = false };
            title.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Muted");
            g.Children.Add(title);
            return g;
        }

        private void Redraw()
        {
            PainelGeom g = _c.Build();
            DrawFrente(g);
            if (_c.Tipo == TipoPainel.Brise) DrawPlanta(g); else DrawCorte(g);

            var av = new List<string>(g.Avisos);
            _desc.Text = _c.Descricao() + " · " + MedidaPainel.Fmt(g.W) + " × " + MedidaPainel.Fmt(g.H) + " mm · " +
                         (g.AreaM2).ToString("0.00", Br) + " m²" + (g.Aletas > 0 ? " · " + g.Aletas + " aletas" : "") +
                         " · profundidade " + (g.PMax - g.PMin).ToString("0.#", Br) + " mm";
            if (_tams.Count > 1) av.Add($"{_tams.Count} tamanhos de painel na seleção: uma família por tamanho, todas com o código {_c.Codigo}.");
            _avisos.Text = string.Join("\n", av.Select(x => "• " + x));
            _familia.Text = "Família: " + _c.NomeFamilia(g.W, g.H) + (_tams.Count > 1 ? " (e os outros tamanhos)" : "");
        }

        private Brush Fill(SlotPainel s)
        {
            switch (s)
            {
                case SlotPainel.Vidro: return new SolidColorBrush(Color.FromArgb(110, 140, 190, 215));
                case SlotPainel.Nucleo: return new SolidColorBrush(Color.FromArgb(200, 217, 201, 154));
                case SlotPainel.Moldura: return new SolidColorBrush(Hex(_c.CorMolduraHex, 230));
                default: return new SolidColorBrush(Hex(_c.CorHex, 215));
            }
        }

        private static Color Hex(string hex, byte a)
        {
            hex = (hex ?? "").Trim().TrimStart('#');
            if (hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v))
                return Color.FromArgb(a, (byte)(v >> 16), (byte)((v >> 8) & 255), (byte)(v & 255));
            return Color.FromArgb(a, 160, 160, 160);
        }

        private void DrawFrente(PainelGeom g)
        {
            var cv = _frente;
            cv.Children.Clear();
            double cw = cv.ActualWidth, ch = cv.ActualHeight;
            if (cw < 20 || ch < 20 || g.W <= 0 || g.H <= 0) return;
            double pad = 30;
            double k = Math.Min((cw - 2 * pad) / g.W, (ch - 2 * pad - 10) / g.H);
            double ox = (cw - g.W * k) / 2, oy = (ch + g.H * k) / 2 + 6;
            Point S(double x, double y) => new Point(ox + x * k, oy - y * k);
            var muted = (Brush)FindResource("Ja.Muted");
            var ink = (Brush)FindResource("Ja.Text");

            // the grid cell (curtain wall grid lines)
            Rect(cv, S(0, g.H), g.W * k, g.H * k, null, muted, new DoubleCollection { 5, 4 });
            // back to front, so what is outside covers what is inside
            foreach (var s in g.Solidos.Where(x => x.Medium).OrderBy(x => PainelGeom.Faixa(x).Value))
            {
                Brush fill = Fill(s.Slot);
                switch (s.Eixo)
                {
                    case EixoPainel.Profundidade: Poly(cv, s.Loops.Select(l => l.Select(q => S(q.X, q.Y)).ToList()), fill, ink); break;
                    case EixoPainel.Largura:
                    {
                        double y0 = s.Loops[0].Min(q => q.Y), y1 = s.Loops[0].Max(q => q.Y);
                        Rect(cv, S(s.E0, y1), (s.E1 - s.E0) * k, (y1 - y0) * k, fill, ink, null);
                        break;
                    }
                    default:
                    {
                        double x0 = s.Loops[0].Min(q => q.X), x1 = s.Loops[0].Max(q => q.X);
                        Rect(cv, S(x0, s.E1), (x1 - x0) * k, (s.E1 - s.E0) * k, fill, ink, null);
                        break;
                    }
                }
            }
            Text(cv, MedidaPainel.Fmt(g.W), (S(0, 0).X + S(g.W, 0).X) / 2 - 14, S(0, 0).Y + 4, muted);
            Text(cv, MedidaPainel.Fmt(g.H), S(g.W, 0).X + 6, (S(0, 0).Y + S(0, g.H).Y) / 2 - 8, muted);
        }

        /// <summary>Vertical section (bottom stretch) through the middle of the width: exterior on the left.</summary>
        private void DrawCorte(PainelGeom g)
        {
            var cv = _corte;
            cv.Children.Clear();
            _corteTitulo.Text = "Corte vertical (trecho inferior) — exterior à esquerda";
            double cw = cv.ActualWidth, chh = cv.ActualHeight;
            if (cw < 20 || chh < 20 || g.Solidos.Count == 0) return;
            double xc = g.W / 2 + 0.37;
            double yMax = Math.Min(g.H, 700);
            double pMin = Math.Min(g.PMin, -20), pMax = Math.Max(g.PMax, 20);
            double pad = 24;
            double k = Math.Min((cw - 2 * pad) / (pMax - pMin + 60), (chh - 2 * pad - 10) / yMax);
            double cx = cw / 2 + (pMax + pMin) / 2 * k, oy = chh - pad;
            Point S(double p, double y) => new Point(cx - p * k, oy - y * k);
            var muted = (Brush)FindResource("Ja.Muted");
            var ink = (Brush)FindResource("Ja.Text");
            Line(cv, S(0, 0), S(0, yMax), muted, new DoubleCollection { 5, 4 });
            Text(cv, "EXTERIOR", 8, 20, muted);
            Text(cv, "INTERIOR", cw - 64, 20, muted);
            foreach (var s in g.Solidos.Where(x => x.Medium))
            {
                Brush fill = Fill(s.Slot);
                switch (s.Eixo)
                {
                    case EixoPainel.Profundidade:
                        foreach (var iv in Cortar(s.Loops, xc, true))
                        {
                            double y0 = iv.Key, y1 = Math.Min(iv.Value, yMax);
                            if (y0 >= yMax) continue;
                            Rect(cv, S(s.E1, y1), (s.E1 - s.E0) * k, (y1 - y0) * k, fill, ink, null);
                        }
                        break;
                    case EixoPainel.Largura:
                        if (xc < s.E0 || xc > s.E1 || s.Loops[0].Min(q => q.Y) > yMax) break;
                        Poly(cv, new[] { s.Loops[0].Select(q => S(q.X, q.Y)).ToList() }, fill, ink);
                        break;
                    default:
                        foreach (var iv in Cortar(s.Loops, xc, true))
                        {
                            double y1 = Math.Min(s.E1, yMax);
                            if (s.E0 >= yMax) continue;
                            Rect(cv, S(iv.Value, y1), (iv.Value - iv.Key) * k, (y1 - s.E0) * k, fill, ink, null);
                        }
                        break;
                }
            }
        }

        /// <summary>Horizontal section (left stretch) at mid height: exterior on top.</summary>
        private void DrawPlanta(PainelGeom g)
        {
            var cv = _corte;
            cv.Children.Clear();
            _corteTitulo.Text = "Planta (trecho à esquerda) — exterior para cima";
            double cw = cv.ActualWidth, chh = cv.ActualHeight;
            if (cw < 20 || chh < 20 || g.Solidos.Count == 0) return;
            double yc = g.H / 2 + 0.37;
            double xMax = Math.Min(g.W, 900);
            double pMin = Math.Min(g.PMin, -20), pMax = Math.Max(g.PMax, 20);
            double pad = 24;
            double k = Math.Min((cw - 2 * pad) / xMax, (chh - 2 * pad - 14) / (pMax - pMin + 40));
            double ox = pad, cy = chh / 2 + 8 + (pMax + pMin) / 2 * k;
            Point S(double x, double p) => new Point(ox + x * k, cy - p * k);
            var muted = (Brush)FindResource("Ja.Muted");
            var ink = (Brush)FindResource("Ja.Text");
            Line(cv, S(0, 0), S(xMax, 0), muted, new DoubleCollection { 5, 4 });
            Text(cv, "EXTERIOR", cw - 70, 20, muted);
            foreach (var s in g.Solidos.Where(x => x.Medium))
            {
                Brush fill = Fill(s.Slot);
                switch (s.Eixo)
                {
                    case EixoPainel.Profundidade:
                        foreach (var iv in Cortar(s.Loops, yc, false))
                        {
                            double x0 = iv.Key, x1 = Math.Min(iv.Value, xMax);
                            if (x0 >= xMax) continue;
                            Rect(cv, S(x0, s.E1), (x1 - x0) * k, (s.E1 - s.E0) * k, fill, ink, null);
                        }
                        break;
                    case EixoPainel.Altura:
                        if (yc < s.E0 || yc > s.E1 || s.Loops[0].Min(q => q.X) > xMax) break;
                        Poly(cv, new[] { s.Loops[0].Select(q => S(q.X, q.Y)).ToList() }, fill, ink);
                        break;
                    default:
                        foreach (var iv in Cortar(s.Loops, yc, false))
                        {
                            double x1 = Math.Min(s.E1, xMax);
                            if (s.E0 >= xMax) continue;
                            Rect(cv, S(s.E0, iv.Value), (x1 - s.E0) * k, (iv.Value - iv.Key) * k, fill, ink, null);
                        }
                        break;
                }
            }
        }

        /// <summary>Intervals of a profile on a line: porX → the line X = v, returns Y intervals; else Y = v, X intervals.</summary>
        private static List<KeyValuePair<double, double>> Cortar(List<List<P2>> loops, double v, bool porX)
        {
            var xs = new List<double>();
            foreach (var l in loops)
                for (int i = 0; i < l.Count; i++)
                {
                    P2 a = l[i], b = l[(i + 1) % l.Count];
                    double ca = porX ? a.X : a.Y, cb = porX ? b.X : b.Y, oa = porX ? a.Y : a.X, ob = porX ? b.Y : b.X;
                    if ((ca <= v && cb > v) || (cb <= v && ca > v)) xs.Add(oa + (v - ca) * (ob - oa) / (cb - ca));
                }
            xs.Sort();
            var r = new List<KeyValuePair<double, double>>();
            for (int i = 0; i + 1 < xs.Count; i += 2)
                if (xs[i + 1] - xs[i] > 0.01) r.Add(new KeyValuePair<double, double>(xs[i], xs[i + 1]));
            return r;
        }

        private static void Poly(Canvas cv, IEnumerable<List<Point>> loops, Brush fill, Brush stroke)
        {
            var geo = new StreamGeometry { FillRule = FillRule.EvenOdd };
            using (var ctx = geo.Open())
                foreach (var l in loops)
                {
                    if (l.Count < 3) continue;
                    ctx.BeginFigure(l[0], true, true);
                    ctx.PolyLineTo(l.Skip(1).ToList(), true, true);
                }
            cv.Children.Add(new Path { Data = geo, Fill = fill, Stroke = stroke, StrokeThickness = 0.8 });
        }

        private static void Rect(Canvas cv, Point topLeft, double w, double h, Brush fill, Brush stroke, DoubleCollection dash)
        {
            var r = new Rectangle { Width = Math.Max(0.5, w), Height = Math.Max(0.5, h), Fill = fill, Stroke = stroke, StrokeThickness = 0.8, StrokeDashArray = dash };
            Canvas.SetLeft(r, topLeft.X); Canvas.SetTop(r, topLeft.Y);
            cv.Children.Add(r);
        }

        private static void Line(Canvas cv, Point a, Point b, Brush stroke, DoubleCollection dash) =>
            cv.Children.Add(new System.Windows.Shapes.Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = stroke, StrokeThickness = 1, StrokeDashArray = dash });

        private static void Text(Canvas cv, string t, double x, double y, Brush b)
        {
            var tb = new TextBlock { Text = t, Foreground = b, FontSize = 10.5, FontWeight = FontWeights.SemiBold };
            Canvas.SetLeft(tb, x); Canvas.SetTop(tb, y);
            cv.Children.Add(tb);
        }

        // ================================================================== right: options

        private UIElement Options(List<string> materiais)
        {
            var sp = new StackPanel();

            if (_exist.Count > 0)
            {
                _editar.Items.Add("Novo painel");
                foreach (var e in _exist)
                    _editar.Items.Add(e.Codigo + " · " + PainelConfig.NomeTipo(e.Config.Tipo) + " · " + e.Tamanhos + " tamanho(s)" + (e.Usos > 0 ? $" · {e.Usos} no modelo" : ""));
                _editar.SelectedIndex = 0;
                _editar.SelectionChanged += (s, e) =>
                {
                    int i = _editar.SelectedIndex - 1;
                    if (i >= 0 && i < _exist.Count && _exist[i].Config != null)
                    {
                        var c = _exist[i].Config.Copiar();
                        c.LarguraMm = _c.LarguraMm; c.AlturaMm = _c.AlturaMm;    // keep the size being shown
                        Load(c);
                    }
                    UpdateOk();
                };
                sp.Children.Add(ToolChrome.Card(ToolChrome.Labeled("Criar um novo ou editar um painel JA do projeto (todos os tamanhos e cópias acompanham)", _editar), "Painel"));
            }

            // kind tiles
            var tiles = new WrapPanel();
            foreach (var t in Tipos)
            {
                var rb = new RadioButton { Content = PainelConfig.NomeTipo(t), GroupName = "painel-tipo", Margin = new Thickness(0, 0, 3, 3) };
                rb.SetResourceReference(StyleProperty, "Ja.Segment");
                rb.ToolTip = Dica(t);
                var tipo = t;
                rb.Checked += (s, e) => { if (_loading) return; Read(); _c.PadroesDe(tipo); Load(_c); };
                tiles.Children.Add(rb);
                _tipo[t] = rb;
            }
            var track = ToolChrome.Track(tiles);
            track.HorizontalAlignment = HorizontalAlignment.Stretch;
            sp.Children.Add(ToolChrome.Card(track, "Tipo"));

            // size
            if (_tams.Count > 0)
            {
                foreach (var t in _tams) _tamanho.Items.Add(t);
                _tamanho.SelectedIndex = 0;
                _tamanho.SelectionChanged += (s, e) =>
                {
                    if (_loading || !(_tamanho.SelectedItem is MedidaPainel t)) return;
                    _c.LarguraMm = t.W; _c.AlturaMm = t.H;
                    Changed();
                };
                var st = new StackPanel();
                st.Children.Add(ToolChrome.Labeled(_tams.Count == 1 ? "Tamanho do painel (vem da parede cortina)" : "Prévia no tamanho (cada tamanho vira uma família)", _tamanho));
                _cardTamanhoFixo = ToolChrome.Card(st, "Tamanho");
                sp.Children.Add(_cardTamanhoFixo);
            }
            else
            {
                var wp = new WrapPanel();
                wp.Children.Add(Field("Largura", _w, "mm", "Largura do painel (célula da grade).", out _));
                wp.Children.Add(Field("Altura", _h, "mm", "Altura do painel.", out _));
                var st = new StackPanel();
                st.Children.Add(wp);
                st.Children.Add(ToolChrome.Muted("Nenhum painel selecionado: a família é gerada neste tamanho. Para trocar painéis, selecione-os antes de abrir.", 11));
                _cardTamanhoLivre = ToolChrome.Card(st, "Tamanho");
                sp.Children.Add(_cardTamanhoLivre);
            }

            // measures
            var med = new WrapPanel();
            _lE = Field("Espessura", _e, "mm", null, out _lEText);
            med.Children.Add(_lE);
            med.Children.Add(Field("Deslocamento", _off, "mm", "Centro do painel em relação à linha da grade (+ para fora, − para dentro).", out _));
            med.Children.Add(Field("Junta", _junta, "mm", "Junta aberta entre painéis vizinhos: cada borda recua a metade.", out _));
            sp.Children.Add(ToolChrome.Card(med, "Medidas"));

            // sheets (cassete, sanduíche)
            var chp = new WrapPanel();
            _lChapa = Field("Chapa", _chapa, "mm", "Cassete: espessura da chapa (ACM 4 mm). Sanduíche: cada chapa de face.", out _);
            _lRet = Field("Retorno", _ret, "mm", "Profundidade da dobra no perímetro do cassete (para dentro).", out _);
            chp.Children.Add(_lChapa);
            chp.Children.Add(_lRet);
            _cardChapas = ToolChrome.Card(chp, "Chapas");
            sp.Children.Add(_cardChapas);

            // blades
            var al = new WrapPanel();
            al.Children.Add(Field("Largura da aleta", _al, "mm", "Profundidade da aleta (perpendicular à fachada no ângulo 0).", out _));
            al.Children.Add(Field("Espessura", _ae, "mm", null, out _));
            al.Children.Add(Field("Passo", _passo, "mm", "Distância entre aletas; é ajustada para preencher o painel por igual.", out _));
            al.Children.Add(Field("Ângulo", _ang, "°", "0 = perpendicular à fachada. Veneziana: + baixa a borda externa (escoa a chuva). Brise: + gira a borda externa para a direita.", out _));
            _cardAletas = ToolChrome.Card(al, "Aletas");
            sp.Children.Add(_cardAletas);

            // frame
            var mo = new StackPanel();
            var mw = new WrapPanel();
            mw.Children.Add(Field("Face", _mf, "mm", "Largura da moldura vista de frente (0 = sem moldura; emoldurado sempre tem).", out _));
            mw.Children.Add(Field("Profundidade", _mp, "mm", null, out _));
            _lTrav = Field("Travessas", _trav, "", "Travessas horizontais intermediárias (0 a 10).", out _);
            mw.Children.Add(_lTrav);
            mo.Children.Add(mw);
            var pr = new WrapPanel();
            _vidroSim.SetResourceReference(StyleProperty, "Ja.Segment");
            _vidroNao.SetResourceReference(StyleProperty, "Ja.Segment");
            pr.Children.Add(_vidroSim); pr.Children.Add(_vidroNao);
            _vidroSim.Checked += (s, e) => { if (!_loading) { _c.PreenchimentoVidro = true; Mostrar(); Changed(); } };
            _vidroNao.Checked += (s, e) => { if (!_loading) { _c.PreenchimentoVidro = false; Mostrar(); Changed(); } };
            _rowPreench = ToolChrome.Labeled("Preenchimento", ToolChrome.Track(pr));
            mo.Children.Add(_rowPreench);
            _cardMoldura = ToolChrome.Card(mo, "Moldura");
            sp.Children.Add(_cardMoldura);

            // materials
            var mats = (materiais ?? new List<string>()).OrderBy(x => x).ToList();
            foreach (var cb in new[] { _mat, _matVidro, _matNucleo, _matMoldura }) foreach (string m in mats) cb.Items.Add(m);
            var ms = new StackPanel();
            var mr = new WrapPanel();
            mr.Children.Add(ToolChrome.Labeled("Material do painel", _mat, "Se não existir no projeto, é criado com a cor ao lado."));
            var corBox = ToolChrome.Labeled("Cor (hex)", _cor);
            ((FrameworkElement)corBox).Margin = new Thickness(12, 0, 0, 8);
            mr.Children.Add(corBox);
            ms.Children.Add(mr);
            _rowMatVidro = ToolChrome.Labeled("Vidro", _matVidro);
            _rowMatNucleo = ToolChrome.Labeled("Núcleo", _matNucleo);
            _rowMatMoldura = ToolChrome.Labeled("Moldura", _matMoldura);
            ms.Children.Add(_rowMatVidro); ms.Children.Add(_rowMatNucleo); ms.Children.Add(_rowMatMoldura);
            sp.Children.Add(ToolChrome.Card(ms, "Materiais"));

            // type in the project
            var ty = new StackPanel();
            ty.Children.Add(ToolChrome.Labeled("Código (marca de tipo)", _codigo));
            _inverter.ToolTip = "Se o painel sair com o exterior para dentro da parede cortina, gere de novo com esta opção.";
            ty.Children.Add(_inverter);
            _familia.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Accent");
            ty.Children.Add(_familia);
            sp.Children.Add(ToolChrome.Card(ty, "Tipo no projeto"));

            foreach (var tb in new[] { _w, _h, _e, _off, _junta, _chapa, _ret, _al, _ae, _passo, _ang, _mf, _mp, _trav, _codigo, _cor })
                tb.TextChanged += (s, e) => { if (!_loading) { Read(); Changed(); } };
            _inverter.Checked += (s, e) => { if (!_loading) { Read(); Changed(); } };
            _inverter.Unchecked += (s, e) => { if (!_loading) { Read(); Changed(); } };
            foreach (var cb in new[] { _mat, _matVidro, _matNucleo, _matMoldura })
                cb.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((s, e) => { if (!_loading) { Read(); Changed(); } }));
            return sp;
        }

        private static string Dica(TipoPainel t)
        {
            switch (t)
            {
                case TipoPainel.Cassete: return "Chapa dobrada (ACM, alumínio): face + retornos no perímetro, com junta aberta.";
                case TipoPainel.Solido: return "Painel opaco liso de uma espessura.";
                case TipoPainel.Vidro: return "Vidro sem caixilho (spandrel, fixo).";
                case TipoPainel.Sanduiche: return "Duas chapas com núcleo isolante (lã de rocha, PIR...).";
                case TipoPainel.Veneziana: return "Aletas horizontais inclinadas, com ou sem moldura.";
                case TipoPainel.Brise: return "Aletas verticais (brise), com ou sem moldura.";
                default: return "Moldura no perímetro com vidro ou painel cego e travessas opcionais.";
            }
        }

        private UIElement Footer()
        {
            var bar = new DockPanel();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            if (_sel > 0)
            {
                _aplicar.Content = $"Trocar os {_sel} painel(éis) selecionado(s)";
                _aplicar.IsChecked = true;
                _aplicar.Margin = new Thickness(0, 0, 12, 0);
                _aplicar.VerticalAlignment = VerticalAlignment.Center;
                _aplicar.Checked += (s, e) => UpdateOk();
                _aplicar.Unchecked += (s, e) => UpdateOk();
                buttons.Children.Add(_aplicar);
            }
            var cancel = ToolChrome.Ghost("Cancelar");
            cancel.IsCancel = true; cancel.MinWidth = 88; cancel.Margin = new Thickness(0, 0, 8, 0);
            buttons.Children.Add(cancel);
            _ok = ToolChrome.Primary("Gerar painel", (s, e) => Confirm());
            _ok.MinWidth = 170; _ok.IsDefault = true;
            buttons.Children.Add(_ok);
            DockPanel.SetDock(buttons, Dock.Right);
            bar.Children.Add(buttons);
            bar.Children.Add(ToolChrome.Muted("Vista de fora. Ctrl+Enter gera.", 11.5));
            return bar;
        }

        // ================================================================== state

        private void Load(PainelConfig c)
        {
            _loading = true;
            try
            {
                _c = c.Copiar();
                if (_tipo.TryGetValue(_c.Tipo, out var rb)) rb.IsChecked = true;
                _w.Text = N(_c.LarguraMm); _h.Text = N(_c.AlturaMm);
                _e.Text = N(_c.EspessuraMm); _off.Text = N(_c.DeslocamentoMm); _junta.Text = N(_c.JuntaMm);
                _chapa.Text = N(_c.ChapaMm); _ret.Text = N(_c.RetornoMm);
                _al.Text = N(_c.AletaLarguraMm); _ae.Text = N(_c.AletaEspessuraMm); _passo.Text = N(_c.PassoMm); _ang.Text = N(_c.AnguloGraus);
                _mf.Text = N(_c.MolduraMm); _mp.Text = N(_c.MolduraProfMm); _trav.Text = _c.Travessas.ToString(CultureInfo.InvariantCulture);
                (_c.PreenchimentoVidro ? _vidroSim : _vidroNao).IsChecked = true;
                _codigo.Text = _c.Codigo;
                _cor.Text = _c.CorHex;
                _mat.Text = _c.Material; _matVidro.Text = _c.MaterialVidro; _matNucleo.Text = _c.MaterialNucleo; _matMoldura.Text = _c.MaterialMoldura;
                _inverter.IsChecked = _c.InverterLado;
            }
            finally { _loading = false; }
            Mostrar();
            Changed();
        }

        private void Read()
        {
            if (_tams.Count == 0) { _c.LarguraMm = D(_w, _c.LarguraMm); _c.AlturaMm = D(_h, _c.AlturaMm); }
            _c.EspessuraMm = D(_e, _c.EspessuraMm); _c.DeslocamentoMm = D(_off, _c.DeslocamentoMm, true); _c.JuntaMm = D(_junta, _c.JuntaMm, true);
            _c.ChapaMm = D(_chapa, _c.ChapaMm); _c.RetornoMm = D(_ret, _c.RetornoMm);
            _c.AletaLarguraMm = D(_al, _c.AletaLarguraMm); _c.AletaEspessuraMm = D(_ae, _c.AletaEspessuraMm);
            _c.PassoMm = D(_passo, _c.PassoMm); _c.AnguloGraus = D(_ang, _c.AnguloGraus, true);
            _c.MolduraMm = D(_mf, _c.MolduraMm, true); _c.MolduraProfMm = D(_mp, _c.MolduraProfMm);
            _c.Travessas = (int)Math.Round(D(_trav, _c.Travessas, true));
            _c.Codigo = string.IsNullOrWhiteSpace(_codigo.Text) ? "PN01" : _codigo.Text.Trim();
            string hex = (_cor.Text ?? "").Trim().TrimStart('#');
            if (hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)) _c.CorHex = hex.ToUpperInvariant();
            _c.Material = T(_mat, _c.Material); _c.MaterialVidro = T(_matVidro, _c.MaterialVidro);
            _c.MaterialNucleo = T(_matNucleo, _c.MaterialNucleo); _c.MaterialMoldura = T(_matMoldura, _c.MaterialMoldura);
            _c.InverterLado = _inverter.IsChecked == true;
        }

        private static string T(ComboBox cb, string fallback) => string.IsNullOrWhiteSpace(cb.Text) ? fallback : cb.Text.Trim();

        private void Changed()
        {
            _c.Normalized();
            if (IsLoaded) Redraw();
            UpdateOk();
        }

        private void UpdateOk()
        {
            if (_ok == null) return;
            bool edit = _editar.SelectedIndex > 0;
            int n = Math.Max(1, _tams.Count);
            _ok.Content = edit ? "Atualizar painel" : n > 1 ? $"Gerar {n} famílias" : _sel > 0 && _aplicar.IsChecked == true ? "Gerar e trocar" : "Gerar painel";
        }

        /// <summary>Only the options of the chosen kind.</summary>
        private void Mostrar()
        {
            TipoPainel t = _c.Tipo;
            bool aletas = t == TipoPainel.Veneziana || t == TipoPainel.Brise;
            Vis(_lE, t == TipoPainel.Solido || t == TipoPainel.Vidro || t == TipoPainel.Sanduiche || t == TipoPainel.Emoldurado);
            _lEText.Text = t == TipoPainel.Vidro ? "Espessura do vidro" : t == TipoPainel.Emoldurado ? "Espessura do preenchimento" : "Espessura total";
            Vis(_cardChapas, t == TipoPainel.Cassete || t == TipoPainel.Sanduiche);
            Vis(_lRet, t == TipoPainel.Cassete);
            Vis(_cardAletas, aletas);
            Vis(_cardMoldura, aletas || t == TipoPainel.Emoldurado);
            Vis(_lTrav, t == TipoPainel.Emoldurado);
            Vis(_rowPreench, t == TipoPainel.Emoldurado);
            bool vidro = t == TipoPainel.Vidro || (t == TipoPainel.Emoldurado && _c.PreenchimentoVidro);
            Vis(_rowMatVidro, vidro);
            Vis(_rowMatNucleo, t == TipoPainel.Sanduiche);
            Vis(_rowMatMoldura, t == TipoPainel.Emoldurado || (aletas && _c.MolduraMm > 0));
        }

        private void Confirm()
        {
            Read();
            _c.Normalized();
            int i = _editar.SelectedIndex - 1;
            _pedido = new PainelPedido
            {
                Config = _c.Copiar(),
                Editando = i >= 0 && i < _exist.Count ? _exist[i] : null,
                Aplicar = _sel > 0 && _aplicar.IsChecked == true
            };
            DialogResult = true;
        }

        // ================================================================== helpers

        private static ComboBox Combo() => new ComboBox { IsEditable = true, MinWidth = 220 };
    }
}
