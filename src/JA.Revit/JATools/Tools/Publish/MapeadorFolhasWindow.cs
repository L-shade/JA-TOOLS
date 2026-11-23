using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using RevitDwgUnifier.Tools.Common;
using RevitDwgUnifier.Tools.Detail;

namespace RevitDwgUnifier.Tools.Publish
{
    /// <summary>A view that is not on any sheet yet (sizes = its box on paper, mm).</summary>
    public sealed class MapVista
    {
        public long Id;
        public string Nome = "", Tipo = "", Nivel = "";
        public int Ordem, Escala;
        public double WMm, HMm;
        public bool Marcada;
        public string Rotulo => Tipo + " · " + Nome + (Escala > 0 ? $"  (1:{Escala})" : "") + (string.IsNullOrEmpty(Nivel) ? "" : "  · " + Nivel);
    }

    /// <summary>A title block and its usable drawing area (mm), as measured in its lines.</summary>
    public sealed class MapCarimbo
    {
        public string Nome = "";
        public double UtilWMm, UtilHMm;
        public override string ToString() => $"{Nome}  ({UtilWMm:0} × {UtilHMm:0} mm úteis)";
    }

    /// <summary>One sheet of the plan (the grid edits number and name).</summary>
    public sealed class MapFolha
    {
        public string Numero { get; set; }
        public string Nome { get; set; }
        public string Vistas => string.Join("; ", Itens.Select(v => v.Nome));
        public int Quantas => Itens.Count;
        public List<MapVista> Itens = new List<MapVista>();
    }

    /// <summary>
    /// Mapeador de Folhas (reference: Glyph's Sheet Mapper): views without a sheet on the left, with
    /// filters; how to group them on the right (one per view, N per sheet, or as many as fit, packed
    /// by size); the plan in an editable grid (sheet number and name) before anything is created.
    /// </summary>
    public sealed class MapeadorFolhasWindow : Window
    {
        public const string Prefs = "MapeadorFolhas";
        private readonly List<MapVista> _vistas;
        private readonly List<MapCarimbo> _carimbos;
        private readonly HashSet<string> _usados;
        private readonly Func<string, string> _proximo;

        private readonly ListBox _lista = new ListBox { SelectionMode = SelectionMode.Extended };
        private readonly ComboBox _tipo = new ComboBox(), _nivel = new ComboBox(), _carimbo = new ComboBox();
        private readonly TextBox _busca = new TextBox(), _n = new TextBox { Width = 50 }, _inicio = new TextBox(), _padrao = new TextBox(),
                                 _margem = new TextBox(), _gap = new TextBox();
        private readonly RadioButton[] _modos = new RadioButton[3];
        private readonly CheckBox _separar = new CheckBox { Content = "Não misturar tipos de vista na mesma prancha" };
        private readonly DataGrid _grade = new DataGrid();
        private readonly TextBlock _contagem = ToolChrome.Muted("", 11), _resumo = ToolChrome.Muted("", 11.5);
        private bool _ocupado;

        public List<MapFolha> Plano { get; private set; } = new List<MapFolha>();
        public int CarimboIndice => Math.Max(0, _carimbo.SelectedIndex);
        public double MargemMm => Num(_margem, 0);
        public double GapMm => Math.Max(0, Num(_gap, 10));

        public MapeadorFolhasWindow(List<MapVista> vistas, List<MapCarimbo> carimbos, HashSet<string> usados, Func<string, string> proximo)
        {
            _vistas = vistas; _carimbos = carimbos; _usados = usados; _proximo = proximo;
            Width = Math.Min(1180, SystemParameters.WorkArea.Width - 60);
            Height = Math.Min(800, SystemParameters.WorkArea.Height - 40);
            MinWidth = 900; MinHeight = 560;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(400) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            body.Children.Add(PainelVistas());
            var dir = PainelPlano();
            Grid.SetColumn(dir, 2);
            body.Children.Add(dir);

            var footer = new DockPanel();
            var ok = ToolChrome.Primary("Criar pranchas", (s, e) => Confirmar());
            ok.IsDefault = true;
            var cancel = ToolChrome.Secondary("Cancelar", (s, e) => DialogResult = false);
            cancel.IsCancel = true;
            cancel.Margin = new Thickness(0, 0, 8, 0);
            var bts = new StackPanel { Orientation = Orientation.Horizontal };
            bts.Children.Add(cancel); bts.Children.Add(ok);
            DockPanel.SetDock(bts, Dock.Right);
            footer.Children.Add(bts);
            _resumo.VerticalAlignment = VerticalAlignment.Center;
            footer.Children.Add(_resumo);
            ToolChrome.DressCompact(this, "Mapeador de Folhas", new Border { Margin = new Thickness(0, 0, 0, 12), Child = body }, footer);

            CarregarPrefs();
            Loaded += (s, e) => { PreencherLista(); Replanejar(); };
        }

        // ---------------------------------------------------------------- left: views without sheet
        private FrameworkElement PainelVistas()
        {
            var dock = new DockPanel();
            var filtros = new StackPanel();
            _tipo.Items.Add("Todos os tipos");
            foreach (string t in _vistas.OrderBy(v => v.Ordem).Select(v => v.Tipo).Distinct()) _tipo.Items.Add(t);
            _tipo.SelectedIndex = 0;
            _nivel.Items.Add("Todos os níveis");
            foreach (string n in _vistas.Select(v => v.Nivel).Where(n => !string.IsNullOrEmpty(n)).Distinct().OrderBy(n => n)) _nivel.Items.Add(n);
            _nivel.SelectedIndex = 0;
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var l1 = ToolChrome.Labeled("Tipo", _tipo);
            var l2 = ToolChrome.Labeled("Nível", _nivel);
            Grid.SetColumn(l2, 2);
            g.Children.Add(l1); g.Children.Add(l2);
            filtros.Children.Add(g);
            var busca = ToolChrome.SearchBox(_busca, "Buscar vista…");
            busca.Margin = new Thickness(0, 0, 0, 8);
            filtros.Children.Add(busca);
            var acoes = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            var todas = ToolChrome.Ghost("Marcar visíveis", null, (s, e) => Marcar(true));
            todas.Margin = new Thickness(0, 0, 6, 0);
            acoes.Children.Add(todas);
            acoes.Children.Add(ToolChrome.Ghost("Desmarcar visíveis", null, (s, e) => Marcar(false)));
            filtros.Children.Add(acoes);
            filtros.Children.Add(_contagem);
            DockPanel.SetDock(filtros, Dock.Top);
            dock.Children.Add(filtros);
            _lista.Margin = new Thickness(0, 6, 0, 0);
            ScrollViewer.SetHorizontalScrollBarVisibility(_lista, ScrollBarVisibility.Disabled);
            VirtualizingPanel.SetIsVirtualizing(_lista, true);
            dock.Children.Add(_lista);

            _tipo.SelectionChanged += (s, e) => PreencherLista();
            _nivel.SelectionChanged += (s, e) => PreencherLista();
            var espera = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            espera.Tick += (s, e) => { espera.Stop(); PreencherLista(); };
            _busca.TextChanged += (s, e) => { espera.Stop(); espera.Start(); };
            return ToolChrome.Card(dock, "Vistas sem prancha");
        }

        private IEnumerable<MapVista> Visiveis()
        {
            string tipo = _tipo.SelectedIndex > 0 ? (string)_tipo.SelectedItem : null;
            string nivel = _nivel.SelectedIndex > 0 ? (string)_nivel.SelectedItem : null;
            string q = TextUtil.Fold(_busca.Text ?? "").Trim();
            return _vistas.Where(v => (tipo == null || v.Tipo == tipo) && (nivel == null || v.Nivel == nivel) &&
                                      (q.Length == 0 || TextUtil.Fold(v.Nome + " " + v.Nivel).Contains(q)));
        }

        private void PreencherLista()
        {
            _ocupado = true;
            _lista.Items.Clear();
            foreach (MapVista v in Visiveis())
            {
                var cb = new CheckBox { Content = v.Rotulo, IsChecked = v.Marcada, Tag = v, ToolTip = $"{v.WMm:0} × {v.HMm:0} mm no papel" };
                cb.Checked += (s, e) => { v.Marcada = true; if (!_ocupado) Replanejar(); };
                cb.Unchecked += (s, e) => { v.Marcada = false; if (!_ocupado) Replanejar(); };
                _lista.Items.Add(cb);
            }
            _ocupado = false;
            AtualizarContagem();
        }

        private void Marcar(bool sim)
        {
            _ocupado = true;
            foreach (CheckBox cb in _lista.Items.OfType<CheckBox>()) cb.IsChecked = sim;
            _ocupado = false;
            Replanejar();
        }

        private void AtualizarContagem() =>
            _contagem.Text = $"{_vistas.Count(v => v.Marcada)} de {_vistas.Count} vista(s) marcada(s) · {_lista.Items.Count} na lista";

        // ---------------------------------------------------------------- right: how + plan
        private FrameworkElement PainelPlano()
        {
            var dock = new DockPanel();
            var config = new StackPanel();
            string[] nomes = { "Uma por vista", "N por prancha", "Encaixar (o que couber)" };
            var seg = new StackPanel { Orientation = Orientation.Horizontal };
            for (int i = 0; i < 3; i++)
            {
                _modos[i] = new RadioButton { Content = nomes[i], GroupName = "map-modo" };
                _modos[i].SetResourceReference(StyleProperty, "Ja.Segment");
                _modos[i].Checked += (s, e) => { _n.IsEnabled = _modos[1].IsChecked == true; if (!_ocupado) Replanejar(); };
                seg.Children.Add(_modos[i]);
            }
            var linhaModo = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var nBox = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            nBox.Children.Add(new TextBlock { Text = "N =", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            nBox.Children.Add(_n);
            DockPanel.SetDock(nBox, Dock.Right);
            linhaModo.Children.Add(nBox);
            linhaModo.Children.Add(ToolChrome.Track(seg));
            config.Children.Add(linhaModo);

            foreach (MapCarimbo c in _carimbos) _carimbo.Items.Add(c);
            var g = new Grid();
            for (int i = 0; i < 4; i++)
            {
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 0 ? new GridLength(2, GridUnitType.Star) : new GridLength(1, GridUnitType.Star) });
            }
            g.RowDefinitions.Add(new RowDefinition());
            g.RowDefinitions.Add(new RowDefinition());
            Pos(g, ToolChrome.Labeled("Carimbo", _carimbo), 0, 0, 1);
            Pos(g, ToolChrome.Labeled("Nº inicial", _inicio, "Ex.: A101. Números já usados no projeto são pulados."), 0, 1, 1);
            Pos(g, ToolChrome.Labeled("Margem extra (mm)", _margem, "Além da área livre detectada no carimbo."), 0, 2, 1);
            Pos(g, ToolChrome.Labeled("Espaço entre vistas (mm)", _gap, "Deixe espaço para o título da vista."), 0, 3, 1);
            Pos(g, ToolChrome.Labeled("Nome da prancha", _padrao, "Campos: {vista} (1ª vista), {vistas} (todas), {nivel}, {tipo}, {n} (nº de ordem)."), 1, 0, 2);
            Pos(g, _separar, 1, 2, 2);
            _separar.VerticalAlignment = VerticalAlignment.Center;
            _separar.Margin = new Thickness(8, 8, 0, 0);
            config.Children.Add(g);
            var cfgCard = ToolChrome.Card(config, "Como montar");
            cfgCard.Margin = new Thickness(0, 0, 0, 10);
            DockPanel.SetDock(cfgCard, Dock.Top);
            dock.Children.Add(cfgCard);

            // plan grid
            _grade.AutoGenerateColumns = false;
            _grade.CanUserAddRows = false; _grade.CanUserDeleteRows = false; _grade.CanUserSortColumns = false;
            _grade.HeadersVisibility = DataGridHeadersVisibility.Column;
            _grade.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
            _grade.RowHeight = 26;
            _grade.Columns.Add(new DataGridTextColumn { Header = "Nº", Binding = new Binding("Numero") { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus }, Width = 90 });
            _grade.Columns.Add(new DataGridTextColumn { Header = "Nome da prancha", Binding = new Binding("Nome") { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus }, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            _grade.Columns.Add(new DataGridTextColumn { Header = "Vistas", Binding = new Binding("Quantas"), IsReadOnly = true, Width = 60 });
            _grade.Columns.Add(new DataGridTextColumn { Header = "Quais", Binding = new Binding("Vistas"), IsReadOnly = true, Width = new DataGridLength(1.4, DataGridLengthUnitType.Star) });
            var refazer = ToolChrome.Ghost("↻ Refazer plano", "Recalcula números e nomes (perde as edições da grade).", (s, e) => Replanejar());
            dock.Children.Add(ToolChrome.Card(_grade, "Plano (edite número e nome antes de criar)", refazer));

            foreach (var tb in new[] { _n, _inicio, _padrao, _margem, _gap })
                tb.LostFocus += (s, e) => { if (!_ocupado) Replanejar(); };
            _carimbo.SelectionChanged += (s, e) => { if (!_ocupado) Replanejar(); };
            _separar.Checked += (s, e) => { if (!_ocupado) Replanejar(); };
            _separar.Unchecked += (s, e) => { if (!_ocupado) Replanejar(); };
            return dock;
        }

        private static void Pos(Grid g, FrameworkElement el, int row, int col, int span)
        {
            Grid.SetRow(el, row); Grid.SetColumn(el, col); Grid.SetColumnSpan(el, span);
            if (col > 0 && !(el is CheckBox)) el.Margin = new Thickness(8, 0, 0, 8);
            g.Children.Add(el);
        }

        private static double Num(TextBox t, double padrao) =>
            double.TryParse((t.Text ?? "").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : padrao;

        private int Modo => _modos[1].IsChecked == true ? 1 : _modos[2].IsChecked == true ? 2 : 0;

        private void CarregarPrefs()
        {
            _ocupado = true;
            int modo = int.TryParse(ToolPrefs.Get(Prefs + ".modo"), out int m) ? Math.Max(0, Math.Min(2, m)) : 2;
            _modos[modo].IsChecked = true;
            _n.Text = ToolPrefs.Get(Prefs + ".n") ?? "4";
            _n.IsEnabled = modo == 1;
            _inicio.Text = ToolPrefs.Get(Prefs + ".inicio") ?? "A101";
            _padrao.Text = ToolPrefs.Get(Prefs + ".padrao") ?? "{vista}";
            _margem.Text = ToolPrefs.Get(Prefs + ".margem") ?? "0";
            _gap.Text = ToolPrefs.Get(Prefs + ".gap") ?? "12";
            _separar.IsChecked = (ToolPrefs.Get(Prefs + ".separar") ?? "1") == "1";
            string cb = ToolPrefs.Get(Prefs + ".carimbo");
            int ci = _carimbos.FindIndex(c => c.Nome == cb);
            _carimbo.SelectedIndex = ci >= 0 ? ci : (_carimbos.Count > 0 ? 0 : -1);
            _ocupado = false;
        }

        private void GravarPrefs()
        {
            ToolPrefs.Set(Prefs + ".modo", Modo.ToString(CultureInfo.InvariantCulture));
            ToolPrefs.Set(Prefs + ".n", _n.Text);
            ToolPrefs.Set(Prefs + ".inicio", _inicio.Text);
            ToolPrefs.Set(Prefs + ".padrao", _padrao.Text);
            ToolPrefs.Set(Prefs + ".margem", _margem.Text);
            ToolPrefs.Set(Prefs + ".gap", _gap.Text);
            ToolPrefs.Set(Prefs + ".separar", _separar.IsChecked == true ? "1" : "0");
            if (_carimbo.SelectedItem is MapCarimbo c) ToolPrefs.Set(Prefs + ".carimbo", c.Nome);
            ToolPrefs.Save();
        }

        // ---------------------------------------------------------------- planning (pure)
        private void Replanejar()
        {
            AtualizarContagem();
            Plano = Planejar(out int grandes);
            _grade.ItemsSource = null;
            _grade.ItemsSource = Plano;
            int nv = Plano.Sum(f => f.Itens.Count);
            _resumo.Text = nv == 0 ? "Marque as vistas à esquerda."
                : $"{Plano.Count} prancha(s) para {nv} vista(s)." + (grandes > 0 ? $"  {grandes} vista(s) maiores que a área útil: ficam sozinhas e passam da margem (troque a escala ou o carimbo)." : "");
            _resumo.SetResourceReference(TextBlock.ForegroundProperty, grandes > 0 ? "Ja.Warn" : "Ja.Muted");
        }

        private List<MapFolha> Planejar(out int grandes)
        {
            grandes = 0;
            var plano = new List<MapFolha>();
            var marcadas = _vistas.Where(v => v.Marcada).OrderBy(v => v.Ordem).ThenBy(v => v.Nivel).ThenBy(v => v.Nome, new NaturalStringComparer()).ToList();
            if (marcadas.Count == 0) return plano;
            MapCarimbo c = _carimbo.SelectedItem as MapCarimbo ?? _carimbos.FirstOrDefault();
            double m = MargemMm, gap = GapMm;
            double aw = Math.Max(20, (c?.UtilWMm ?? 400) - 2 * m), ah = Math.Max(20, (c?.UtilHMm ?? 280) - 2 * m);
            int n = Modo == 0 ? 1 : Modo == 1 ? Math.Max(1, (int)Num(_n, 4)) : int.MaxValue;
            bool separar = _separar.IsChecked == true;

            MapFolha atual = null;
            foreach (MapVista v in marcadas)
            {
                bool grande = v.WMm > aw + 0.5 || v.HMm > ah + 0.5;
                if (grande) grandes++;
                bool nova = atual == null || atual.Itens.Count >= n || grande || (atual.Itens.Count == 1 && Excede(atual.Itens[0], aw, ah)) ||
                            (separar && atual.Itens[0].Tipo != v.Tipo);
                if (!nova && Modo == 2) nova = !Cabe(atual.Itens.Concat(new[] { v }).ToList(), aw, ah, gap);
                if (nova) { atual = new MapFolha(); plano.Add(atual); }
                atual.Itens.Add(v);
            }

            // numbers and names
            string num = string.IsNullOrWhiteSpace(_inicio.Text) ? "A101" : _inicio.Text.Trim();
            var usados = new HashSet<string>(_usados, StringComparer.OrdinalIgnoreCase);
            string padrao = string.IsNullOrWhiteSpace(_padrao.Text) ? "{vista}" : _padrao.Text;
            for (int i = 0; i < plano.Count; i++)
            {
                while (usados.Contains(num)) num = _proximo(num);
                MapFolha f = plano[i];
                f.Numero = num;
                usados.Add(num);
                MapVista p = f.Itens[0];
                f.Nome = padrao.Replace("{vista}", p.Nome).Replace("{vistas}", string.Join(" / ", f.Itens.Select(x => x.Nome)))
                               .Replace("{nivel}", p.Nivel).Replace("{tipo}", p.Tipo).Replace("{n}", (i + 1).ToString(CultureInfo.InvariantCulture)).Trim();
                if (f.Nome.Length == 0) f.Nome = p.Nome;
                num = _proximo(num);
            }
            return plano;
        }

        private static bool Excede(MapVista v, double aw, double ah) => v.WMm > aw + 0.5 || v.HMm > ah + 0.5;

        /// <summary>Same packing the command uses on the sheet (rows from the top left, small ones in the gaps).</summary>
        internal static bool Cabe(List<MapVista> vistas, double aw, double ah, double gap)
        {
            var boxes = vistas.OrderByDescending(v => v.WMm * v.HMm).Select(v => new LayoutBox { W = v.WMm, H = v.HMm }).ToList();
            return LayoutEngine.Pack(boxes, 0, ah, aw, 0, gap);
        }

        private void Confirmar()
        {
            _grade.CommitEdit(DataGridEditingUnit.Row, true);
            if (Plano.Count == 0) { MessageBox.Show(this, "Marque ao menos uma vista.", Title, MessageBoxButton.OK, MessageBoxImage.Information); return; }
            if (_carimbo.SelectedItem == null) { MessageBox.Show(this, "Escolha o carimbo.", Title, MessageBoxButton.OK, MessageBoxImage.Information); return; }
            var nums = Plano.Select(f => (f.Numero ?? "").Trim()).ToList();
            string dup = nums.GroupBy(x => x, StringComparer.OrdinalIgnoreCase).FirstOrDefault(gr => gr.Count() > 1 || gr.Key.Length == 0)?.Key;
            if (dup != null)
            {
                MessageBox.Show(this, dup.Length == 0 ? "Há prancha sem número." : $"O número {dup} aparece duas vezes no plano.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            GravarPrefs();
            DialogResult = true;
        }
    }
}
