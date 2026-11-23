using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using JAEsquadrias.Core.Perfis;
using static JAEsquadrias.UI.CompactForm;

namespace JAEsquadrias.UI
{
    /// <summary>A JA mullion type already in the project (to edit it).</summary>
    public sealed class PerfilExistente
    {
        public string Nome;
        public PerfilConfig Config;
        public int Usos;
        public object Tipo;           // MullionType (kept as object: no Revit API here)
    }

    public sealed class PerfilPedido
    {
        public PerfilConfig Config;
        public PerfilExistente Editando;
        public bool Aplicar;
    }

    /// <summary>
    /// Novo Perfil: curtain-wall mullion section configurator. Live section drawing at the left
    /// (exterior on top, panel plane and grid line marked, area and weight), the shape as tiles
    /// and only the options of that shape at the right. Compact chrome, like the JA Tools windows.
    /// </summary>
    public sealed class PerfilWindow : Window
    {
        public static PerfilPedido Abrir(IntPtr owner, PerfilConfig start, List<PerfilExistente> existentes, List<string> materiais, int selecionados)
        {
            var w = new PerfilWindow(start, existentes, materiais, selecionados);
            if (owner != IntPtr.Zero) new WindowInteropHelper(w).Owner = owner;
            return w.ShowDialog() == true ? w._pedido : null;
        }

        private static readonly FormaPerfil[] Formas =
            { FormaPerfil.Retangular, FormaPerfil.TuboRetangular, FormaPerfil.TuboRedondo, FormaPerfil.I, FormaPerfil.T, FormaPerfil.U, FormaPerfil.L, FormaPerfil.ComTampa, FormaPerfil.Canto };

        private PerfilConfig _c;
        private readonly List<PerfilExistente> _exist;
        private readonly int _sel;
        private PerfilPedido _pedido;
        private bool _loading;

        private readonly Canvas _canvas = new Canvas { ClipToBounds = true };
        private readonly TextBlock _desc = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 8, 0, 0) };
        private readonly TextBlock _avisos = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Margin = new Thickness(0, 6, 0, 0) };
        private readonly ComboBox _editar = new ComboBox();
        private readonly Dictionary<FormaPerfil, RadioButton> _forma = new Dictionary<FormaPerfil, RadioButton>();

        // fields
        private readonly TextBox _w = Num(), _d = Num(), _off = Num(), _t = Num(), _aba = Num(), _alma = Num(), _canto = Num();
        private readonly TextBox _tw = Num(), _tc = Num(), _gap = Num(), _haste = Num(), _p1 = Num(), _p2 = Num(), _e = Num();
        private readonly CheckBox _oco = Toggle("Oco (tubo)"), _abaFora = Toggle("Aba na face externa"), _espelhar = Toggle("Espelhar"),
                                  _soExt = Toggle("Só na face externa"), _inverter = Toggle("Inverter interior / exterior"), _aplicar = Toggle("");
        private readonly RadioButton[] _cantos = new RadioButton[3];
        private readonly RadioButton[] _tipoCanto = new RadioButton[2];
        private readonly TextBox _codigo = new TextBox { Width = 90 };
        private readonly ComboBox _material = new ComboBox { IsEditable = true, MinWidth = 220 };
        private readonly TextBlock _tipoNome = new TextBlock { FontSize = 11.5, Margin = new Thickness(0, 4, 0, 0) };
        private FrameworkElement _lw, _ld, _loff, _lt, _laba, _lalma, _rowOco, _rowAba, _rowEsp;
        private TextBlock _lwText, _ldText;
        private Border _cardCantos, _cardTampa, _cardCanto, _cardMedidas;
        private Button _ok;

        private PerfilWindow(PerfilConfig start, List<PerfilExistente> existentes, List<string> materiais, int selecionados)
        {
            _c = (start ?? new PerfilConfig()).Copiar();
            _exist = existentes ?? new List<PerfilExistente>();
            _sel = selecionados;
            Width = 1020; Height = 720; MinWidth = 880; MinHeight = 580;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 360 });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.05, GridUnitType.Star), MinWidth = 420 });
            body.Children.Add(Preview());
            var right = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = Options(materiais) };
            Grid.SetColumn(right, 2);
            body.Children.Add(right);

            ToolChrome.DressCompact(this, "Novo Perfil", body, Footer(),
                "Montantes e travessas de parede cortina: reto, chanfrado, tubos, I, T, U, L, com tampa ou de canto.");
            Load(_c);
            Loaded += (s, e) => Redraw();
            _canvas.SizeChanged += (s, e) => Redraw();
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { Confirm(); e.Handled = true; }
            };
        }

        // ================================================================== left: drawing

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
            var frame = new Border { CornerRadius = new CornerRadius(6), Child = _canvas, BorderThickness = new Thickness(1) };
            frame.SetResourceReference(Border.BackgroundProperty, "Ja.Field");
            frame.SetResourceReference(Border.BorderBrushProperty, "Ja.Line");
            dock.Children.Add(frame);
            return ToolChrome.Card(dock, "Seção");
        }

        private void Redraw()
        {
            _canvas.Children.Clear();
            double cw = _canvas.ActualWidth, ch = _canvas.ActualHeight;
            if (cw < 20 || ch < 20) return;
            PerfilGeom g = _c.Build();
            var pts = g.Loops.SelectMany(l => l.Tessellate()).ToList();
            if (pts.Count == 0) return;
            double minX = Math.Min(pts.Min(p => p.X), -10), maxX = Math.Max(pts.Max(p => p.X), 10);
            double minY = Math.Min(pts.Min(p => p.Y), -10), maxY = Math.Max(pts.Max(p => p.Y), 10);
            double pad = 46;
            double k = Math.Min((cw - 2 * pad) / (maxX - minX), (ch - 2 * pad) / (maxY - minY));
            double cx = cw / 2 - (minX + maxX) / 2 * k, cy = ch / 2 + (minY + maxY) / 2 * k;
            Point S(P p) => new Point(cx + p.X * k, cy - p.Y * k);

            var accent = (Brush)FindResource("Ja.Accent");
            var muted = (Brush)FindResource("Ja.Muted");
            var soft = (Brush)FindResource("Ja.AccentSoft");

            // panel plane (glass) and grid line
            if (!g.Canto)
            {
                double y = cy;
                AddLine(0, y, cw, y, muted, 1, new DoubleCollection { 6, 4 });
                // glass panels on both sides, out to the edge of the drawing
                double gx0 = S(new P(pts.Where(p => Math.Abs(p.Y) < 1).Select(p => p.X).DefaultIfEmpty(minX).Min(), 0)).X;
                double gx1 = S(new P(pts.Where(p => Math.Abs(p.Y) < 1).Select(p => p.X).DefaultIfEmpty(maxX).Max(), 0)).X;
                var glass = new SolidColorBrush(Color.FromArgb(70, 120, 170, 200));
                AddRect(0, y - 5, Math.Max(0, gx0), 10, glass);
                AddRect(gx1, y - 5, Math.Max(0, cw - gx1), 10, glass);
                AddLine(cx, 0, cx, ch, muted, 1, new DoubleCollection { 2, 3 });
                AddText("EXTERIOR", 8, 6, muted, 10.5, true);
                AddText("INTERIOR", 8, ch - 20, muted, 10.5, true);
                AddText("vidro", 8, y - 20, muted, 10.5, false);
            }

            var geo = new StreamGeometry { FillRule = FillRule.EvenOdd };
            using (var ctx = geo.Open())
                foreach (Loop l in g.Loops)
                {
                    var tp = l.Tessellate();
                    ctx.BeginFigure(S(tp[0]), true, true);
                    ctx.PolyLineTo(tp.Skip(1).Select(S).ToList(), true, true);
                }
            _canvas.Children.Add(new Path { Data = geo, Fill = soft, Stroke = accent, StrokeThickness = 1.6 });

            // overall dimensions
            Point a = S(new P(pts.Min(p => p.X), pts.Max(p => p.Y))), b = S(new P(pts.Max(p => p.X), pts.Min(p => p.Y)));
            AddText(N(pts.Max(p => p.X) - pts.Min(p => p.X)), (a.X + b.X) / 2 - 14, b.Y + 6, accent, 11, true);
            AddText(N(pts.Max(p => p.Y) - pts.Min(p => p.Y)), b.X + 8, (a.Y + b.Y) / 2 - 8, accent, 11, true);

            _desc.Text = _c.Descricao();
            var av = new List<string>(g.Avisos);
            if (_c.EhCanto) av.Insert(0, "Montante de canto do Revit (sistema): pernas e espessura ajustáveis; o Revit não aceita perfil próprio em montantes de canto.");
            _avisos.Text = string.Join("\n", av.Select(x => "• " + x));
            _tipoNome.Text = "Tipo no projeto: " + _c.NomeTipo;
        }

        private void AddLine(double x0, double y0, double x1, double y1, Brush b, double w, DoubleCollection dash) =>
            _canvas.Children.Add(new Line { X1 = x0, Y1 = y0, X2 = x1, Y2 = y1, Stroke = b, StrokeThickness = w, StrokeDashArray = dash });

        private void AddRect(double x, double y, double w, double h, Brush b)
        {
            var r = new Rectangle { Width = w, Height = h, Fill = b };
            Canvas.SetLeft(r, x); Canvas.SetTop(r, y);
            _canvas.Children.Add(r);
        }

        private void AddText(string t, double x, double y, Brush b, double size, bool bold)
        {
            var tb = new TextBlock { Text = t, Foreground = b, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
            Canvas.SetLeft(tb, x); Canvas.SetTop(tb, y);
            _canvas.Children.Add(tb);
        }

        // ================================================================== right: options

        private UIElement Options(List<string> materiais)
        {
            var sp = new StackPanel();

            if (_exist.Count > 0)
            {
                _editar.Items.Add("Novo perfil");
                foreach (var e in _exist) _editar.Items.Add(e.Nome + (e.Usos > 0 ? $"  ·  {e.Usos} no modelo" : ""));
                _editar.SelectedIndex = 0;
                _editar.SelectionChanged += (s, e) =>
                {
                    int i = _editar.SelectedIndex - 1;
                    if (i >= 0 && i < _exist.Count && _exist[i].Config != null) Load(_exist[i].Config.Copiar());
                    UpdateOk();
                };
                sp.Children.Add(ToolChrome.Card(ToolChrome.Labeled("Criar um novo ou editar um perfil JA do projeto (as cópias no modelo acompanham)", _editar), "Perfil"));
            }

            // shape tiles
            var tiles = new WrapPanel();
            foreach (var f in Formas)
            {
                var rb = new RadioButton { Content = PerfilConfig.NomeForma(f).Replace(" (meia esquadria)", ""), GroupName = "perfil-forma", Margin = new Thickness(0, 0, 4, 4) };
                rb.SetResourceReference(StyleProperty, "Ja.Segment");
                rb.ToolTip = f == FormaPerfil.Canto ? "Meia esquadria: montante de canto em V (as duas pernas se encontram a 45°) ou em L." : null;
                var forma = f;
                rb.Checked += (s, e) => { if (_loading) return; _c.Forma = forma; Mostrar(); Changed(); };
                tiles.Children.Add(rb);
                _forma[f] = rb;
            }
            var track = ToolChrome.Track(tiles);
            track.HorizontalAlignment = HorizontalAlignment.Stretch;
            foreach (FrameworkElement fe in tiles.Children) fe.Margin = new Thickness(0, 0, 3, 3);
            sp.Children.Add(ToolChrome.Card(track, "Forma"));

            // measures
            var med = new WrapPanel();
            _lw = Field("Largura", _w, "mm", "Medida ao longo da parede cortina (de um lado ao outro da linha da grade).", out _lwText);
            _ld = Field("Profundidade", _d, "mm", "Medida através da parede (do interior ao exterior).", out _ldText);
            _loff = Field("Deslocamento", _off, "mm", "Centro do perfil em relação ao plano do vidro (+ para fora, − para dentro).", out _);
            _lt = Field("Chapa", _t, "mm", "Espessura da parede do tubo, do U ou do L.", out _);
            _laba = Field("Aba", _aba, "mm", "Espessura das abas (I, T).", out _);
            _lalma = Field("Alma", _alma, "mm", "Espessura da alma (I, T).", out _);
            foreach (var f in new[] { _lw, _ld, _loff, _lt, _laba, _lalma }) med.Children.Add(f);
            var medStack = new StackPanel();
            medStack.Children.Add(med);
            var tg = new WrapPanel();
            _rowOco = _oco; _rowAba = _abaFora; _rowEsp = _espelhar;
            tg.Children.Add(_oco); tg.Children.Add(_abaFora); tg.Children.Add(_espelhar);
            _oco.ToolTip = "Desmarcado: barra maciça. O perfil oco usa dois contornos.";
            _abaFora.ToolTip = "T: a aba fica na face externa. U: o lado fechado fica para fora. L: a perna horizontal fica para fora.";
            medStack.Children.Add(tg);
            _cardMedidas = ToolChrome.Card(medStack, "Medidas");
            sp.Children.Add(_cardMedidas);

            // corners
            var cs = new StackPanel();
            var ctr = new WrapPanel();
            string[] cn = { "Retos", "Chanfrados", "Boleados" };
            for (int i = 0; i < 3; i++)
            {
                int idx = i;
                var rb = new RadioButton { Content = cn[i], GroupName = "perfil-cantos" };
                rb.SetResourceReference(StyleProperty, "Ja.Segment");
                rb.Checked += (s, e) => { if (_loading) return; _c.Cantos = (CantoPerfil)idx; Mostrar(); Changed(); };
                ctr.Children.Add(rb);
                _cantos[i] = rb;
            }
            cs.Children.Add(ToolChrome.Track(ctr));
            var crow = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            crow.Children.Add(Field("Tamanho", _canto, "mm", "Chanfrado: a perna do chanfro. Boleado: o raio.", out _));
            _soExt.Margin = new Thickness(12, 16, 0, 0);
            crow.Children.Add(_soExt);
            cs.Children.Add(crow);
            _cardCantos = ToolChrome.Card(cs, "Cantos");
            sp.Children.Add(_cardCantos);

            // tampa
            var tp = new WrapPanel();
            tp.Children.Add(Field("Largura da tampa", _tw, "mm", null, out _));
            tp.Children.Add(Field("Profundidade da tampa", _tc, "mm", null, out _));
            tp.Children.Add(Field("Vão do vidro", _gap, "mm", "Vidro + gaxetas: espaço entre o corpo e a tampa, centrado no plano do vidro.", out _));
            tp.Children.Add(Field("Haste", _haste, "mm", "Largura da haste (parafuso) que atravessa o vão do vidro. O Revit corta o vidro nela.", out _));
            _cardTampa = ToolChrome.Card(tp, "Tampa de pressão");
            sp.Children.Add(_cardTampa);

            // corner mullion
            var cc = new StackPanel();
            var vt = new WrapPanel();
            string[] tn = { "Em V — meia esquadria", "Em L" };
            for (int i = 0; i < 2; i++)
            {
                int idx = i;
                var rb = new RadioButton { Content = tn[i], GroupName = "perfil-canto" };
                rb.SetResourceReference(StyleProperty, "Ja.Segment");
                rb.Checked += (s, e) => { if (_loading) return; _c.TipoCanto = (TipoCanto)idx; Changed(); };
                vt.Children.Add(rb);
                _tipoCanto[i] = rb;
            }
            cc.Children.Add(ToolChrome.Track(vt));
            var cr = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            cr.Children.Add(Field("Perna 1", _p1, "mm", null, out _));
            cr.Children.Add(Field("Perna 2", _p2, "mm", null, out _));
            cr.Children.Add(Field("Espessura", _e, "mm", null, out _));
            cc.Children.Add(cr);
            cc.Children.Add(ToolChrome.Muted("Use nos cantos entre duas paredes cortina (Trocar Montantes ou propriedades da parede). Em V as pernas se encontram na bissetriz: meia esquadria.", 11));
            _cardCanto = ToolChrome.Card(cc, "Canto");
            sp.Children.Add(_cardCanto);

            // type in the project
            var ty = new StackPanel();
            var trow = new WrapPanel();
            trow.Children.Add(ToolChrome.Labeled("Código", _codigo));
            foreach (string m in (materiais ?? new List<string>()).OrderBy(x => x)) _material.Items.Add(m);
            var mat = ToolChrome.Labeled("Material", _material, "Material do tipo de montante; se não existir no projeto, é criado.");
            ((FrameworkElement)mat).Margin = new Thickness(12, 0, 0, 8);
            trow.Children.Add(mat);
            ty.Children.Add(trow);
            _inverter.ToolTip = "Se o perfil sair virado na parede (abas para dentro), gere de novo com esta opção.";
            ty.Children.Add(_inverter);
            _tipoNome.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Accent");
            ty.Children.Add(_tipoNome);
            sp.Children.Add(ToolChrome.Card(ty, "Tipo de montante"));

            foreach (var tb in new[] { _w, _d, _off, _t, _aba, _alma, _canto, _tw, _tc, _gap, _haste, _p1, _p2, _e, _codigo })
                tb.TextChanged += (s, e) => { if (!_loading) { Read(); Changed(); } };
            foreach (var cb in new[] { _oco, _abaFora, _espelhar, _soExt, _inverter })
            {
                cb.Checked += (s, e) => { if (!_loading) { Read(); Changed(); } };
                cb.Unchecked += (s, e) => { if (!_loading) { Read(); Changed(); } };
            }
            _material.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((s, e) => { if (!_loading) { Read(); Changed(); } }));
            return sp;
        }

        private UIElement Footer()
        {
            var bar = new DockPanel();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            if (_sel > 0)
            {
                _aplicar.Content = $"Aplicar nos {_sel} montante(s) selecionado(s)";
                _aplicar.IsChecked = true;
                _aplicar.Margin = new Thickness(0, 0, 12, 0);
                _aplicar.VerticalAlignment = VerticalAlignment.Center;
                buttons.Children.Add(_aplicar);
            }
            var cancel = ToolChrome.Ghost("Cancelar");
            cancel.IsCancel = true; cancel.MinWidth = 88; cancel.Margin = new Thickness(0, 0, 8, 0);
            buttons.Children.Add(cancel);
            _ok = ToolChrome.Primary("Criar perfil", (s, e) => Confirm());
            _ok.MinWidth = 170; _ok.IsDefault = true;
            buttons.Children.Add(_ok);
            DockPanel.SetDock(buttons, Dock.Right);
            bar.Children.Add(buttons);
            bar.Children.Add(ToolChrome.Muted("Exterior para cima no desenho. Ctrl+Enter cria.", 11.5));
            return bar;
        }

        // ================================================================== state

        private void Load(PerfilConfig c)
        {
            _loading = true;
            try
            {
                _c = c.Copiar();
                if (_forma.TryGetValue(_c.Forma, out var rb)) rb.IsChecked = true;
                _w.Text = N(_c.LarguraMm); _d.Text = N(_c.ProfundidadeMm); _off.Text = N(_c.DeslocamentoMm);
                _t.Text = N(_c.ChapaMm); _aba.Text = N(_c.AbaMm); _alma.Text = N(_c.AlmaMm); _canto.Text = N(_c.CantoMm);
                _tw.Text = N(_c.TampaLarguraMm); _tc.Text = N(_c.TampaProfundidadeMm); _gap.Text = N(_c.VaoVidroMm); _haste.Text = N(_c.HasteMm);
                _p1.Text = N(_c.Perna1Mm); _p2.Text = N(_c.Perna2Mm); _e.Text = N(_c.EspessuraCantoMm);
                _oco.IsChecked = _c.Oco; _abaFora.IsChecked = _c.AbaParaFora; _espelhar.IsChecked = _c.Espelhar;
                _soExt.IsChecked = _c.SoFaceExterna; _inverter.IsChecked = _c.InverterLado;
                _cantos[(int)_c.Cantos].IsChecked = true;
                _tipoCanto[(int)_c.TipoCanto].IsChecked = true;
                _codigo.Text = _c.Codigo;
                _material.Text = _c.Material;
            }
            finally { _loading = false; }
            Mostrar();
            Changed();
        }

        private void Read()
        {
            _c.LarguraMm = D(_w, _c.LarguraMm); _c.ProfundidadeMm = D(_d, _c.ProfundidadeMm); _c.DeslocamentoMm = D(_off, _c.DeslocamentoMm, true);
            _c.ChapaMm = D(_t, _c.ChapaMm); _c.AbaMm = D(_aba, _c.AbaMm); _c.AlmaMm = D(_alma, _c.AlmaMm); _c.CantoMm = D(_canto, _c.CantoMm, true);
            _c.TampaLarguraMm = D(_tw, _c.TampaLarguraMm); _c.TampaProfundidadeMm = D(_tc, _c.TampaProfundidadeMm);
            _c.VaoVidroMm = D(_gap, _c.VaoVidroMm, true); _c.HasteMm = D(_haste, _c.HasteMm);
            _c.Perna1Mm = D(_p1, _c.Perna1Mm); _c.Perna2Mm = D(_p2, _c.Perna2Mm); _c.EspessuraCantoMm = D(_e, _c.EspessuraCantoMm);
            _c.Oco = _oco.IsChecked == true; _c.AbaParaFora = _abaFora.IsChecked == true; _c.Espelhar = _espelhar.IsChecked == true;
            _c.SoFaceExterna = _soExt.IsChecked == true; _c.InverterLado = _inverter.IsChecked == true;
            _c.Codigo = string.IsNullOrWhiteSpace(_codigo.Text) ? "MT01" : _codigo.Text.Trim();
            _c.Material = string.IsNullOrWhiteSpace(_material.Text) ? "Alumínio anodizado natural" : _material.Text.Trim();
        }

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
            _ok.Content = edit ? "Atualizar perfil" : _c.EhCanto ? "Criar canto" : "Criar perfil";
        }

        /// <summary>Only the options of the chosen shape.</summary>
        private void Mostrar()
        {
            FormaPerfil f = _c.Forma;
            bool canto = f == FormaPerfil.Canto, round = f == FormaPerfil.TuboRedondo;
            Vis(_cardMedidas, !canto);
            Vis(_cardCanto, canto);
            Vis(_cardTampa, f == FormaPerfil.ComTampa);
            Vis(_cardCantos, !canto && !round);
            Vis(_ld, !round);
            _lwText.Text = round ? "Diâmetro" : "Largura";
            _ldText.Text = f == FormaPerfil.ComTampa ? "Profundidade do corpo" : "Profundidade";
            Vis(_lt, f == FormaPerfil.TuboRetangular || round || f == FormaPerfil.U || f == FormaPerfil.L || f == FormaPerfil.ComTampa);
            Vis(_laba, f == FormaPerfil.I || f == FormaPerfil.T);
            Vis(_lalma, f == FormaPerfil.I || f == FormaPerfil.T);
            Vis(_rowOco, f == FormaPerfil.TuboRetangular || round || f == FormaPerfil.ComTampa);
            Vis(_rowAba, f == FormaPerfil.T || f == FormaPerfil.U || f == FormaPerfil.L);
            Vis(_rowEsp, f == FormaPerfil.L);
            _canto.IsEnabled = _c.Cantos != CantoPerfil.Reto;
            _soExt.IsEnabled = _c.Cantos != CantoPerfil.Reto;
        }

        private void Confirm()
        {
            Read();
            _c.Normalized();
            int i = _editar.SelectedIndex - 1;
            _pedido = new PerfilPedido
            {
                Config = _c.Copiar(),
                Editando = i >= 0 && i < _exist.Count ? _exist[i] : null,
                Aplicar = _sel > 0 && _aplicar.IsChecked == true
            };
            DialogResult = true;
        }
    }
}
