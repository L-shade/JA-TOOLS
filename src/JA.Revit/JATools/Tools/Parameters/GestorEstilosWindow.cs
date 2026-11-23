using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Autodesk.Revit.DB;
using RevitDwgUnifier.Infrastructure;
using RevitDwgUnifier.Tools.Common;
using WC = System.Windows.Controls;
using Grid = System.Windows.Controls.Grid;
using Binding = System.Windows.Data.Binding;
using WM = System.Windows.Media;

namespace RevitDwgUnifier.Tools.Parameters
{
    /// <summary>One row of the style table (wraps an EstiloItem for binding).</summary>
    public sealed class EstiloLinha : INotifyPropertyChanged
    {
        public EstiloItem Item { get; }
        public EstiloLinha(EstiloItem item) { Item = item; }

        private bool _marcado;
        private string _novo;
        public bool Marcado { get => _marcado; set { if (_marcado == value) return; _marcado = value; On(nameof(Marcado)); MarcadoMudou?.Invoke(); } }
        public string NovoNome { get => _novo; set { if (_novo == value) return; _novo = value; On(nameof(NovoNome)); } }
        internal Action MarcadoMudou;

        public string Nome => Item.Nome;
        public string Detalhe => Item.Detalhe;
        public int Usos => Item.Total;
        public string UsosTxt => Item.Refs == 0 ? Item.Usos.ToString(CultureInfo.CurrentCulture) : Item.Usos == 0 ? $"{Item.Refs} ref." : $"{Item.Usos} + {Item.Refs} ref.";
        public string VistasTxt => Item.Vistas.Count == 0 ? "" : Item.Vistas.Count.ToString(CultureInfo.CurrentCulture);
        private int _dup;
        public int Duplicados { get => _dup; set { if (_dup == value) return; _dup = value; On(nameof(DupTxt)); } }
        /// <summary>Name + detail without accents, computed once (search on every key press).</summary>
        public string Busca => _busca ?? (_busca = TextUtil.Fold(Item.Nome + " " + Item.Detalhe));
        private string _busca;
        public string DupTxt => Duplicados > 1 ? "≡ " + Duplicados : "";
        public string Cadeado => Item.Protegido || Item.NaoApaga != null ? "🔒" : "";
        public string CadeadoDica => Item.Protegido ? "Estilo do Revit: não pode ser apagado nem renomeado." : Item.NaoApaga != null ? "Não pode ser apagado: " + Item.NaoApaga + "." : null;
        public WM.Brush Tinta => Item.Total == 0 ? WM.Brushes.Gray : WM.Brushes.Black;

        public event PropertyChangedEventHandler PropertyChanged;
        private void On(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        internal void AvisarCadeado() { On(nameof(Cadeado)); On(nameof(CadeadoDica)); }
    }

    /// <summary>What the command does after the window closes ("Abrir e selecionar").</summary>
    public sealed class EstiloIrPara
    {
        public ElementId Vista = ElementId.InvalidElementId;
        public List<ElementId> Elementos = new List<ElementId>();
    }

    /// <summary>
    /// Gestor de Estilos (like Ideate StyleManager): pick a kind at the left, see every style with its
    /// uses, then find where it is used, merge/replace, purge the unused or rename in batch. Changes
    /// are applied at once (one undo per action) and the table is read again.
    /// </summary>
    public sealed class GestorEstilosWindow : Window
    {
        private const string Titulo = "Gestor de Estilos";
        private readonly Document _doc;
        private readonly UsoCache _cache;
        private readonly List<EstiloTipo> _tipos = GestorEstilos.Tipos();
        private EstiloTipo _tipo;
        private List<EstiloLinha> _todas = new List<EstiloLinha>();
        private ObservableCollection<EstiloLinha> _linhas = new ObservableCollection<EstiloLinha>();

        public EstiloIrPara IrPara { get; private set; }

        private readonly WC.ListBox _lista = new WC.ListBox { BorderThickness = new Thickness(0), Background = WM.Brushes.Transparent };
        private readonly DataGrid _grid = new DataGrid();
        private readonly TextBox _busca = new TextBox();
        private readonly CheckBox _soNaoUsados = new CheckBox { Content = "Só não usados", VerticalAlignment = VerticalAlignment.Center };
        private readonly CheckBox _soDuplicados = new CheckBox { Content = "Só duplicados", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        private readonly TextBlock _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _titulo = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        private readonly ContentControl _painel = new ContentControl();
        private readonly RadioButton[] _modos = new RadioButton[4];
        private DataGridTextColumn _colNovo;

        // Onde
        private readonly TextBlock _ondeNome = new TextBlock { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _ondeResumo = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 8) };
        private readonly WC.ListBox _ondeVistas = new WC.ListBox { Height = 260 };
        private readonly TextBlock _ondeNota = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 8, 0, 0) };
        private Button _ondeIr;

        // Unir
        private readonly TextBlock _unirResumo = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        private readonly ComboBox _unirAlvo = new ComboBox { DisplayMemberPath = "Nome" };
        private Button _unirBotao, _unirDup;
        private readonly TextBlock _unirDupResumo = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };

        // Limpar
        private readonly TextBlock _limparResumo = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        private Button _limparBotao;

        // Renomear
        private readonly TextBox _prefixo = new TextBox(), _sufixo = new TextBox(), _procurar = new TextBox(), _substituir = new TextBox();
        private readonly RadioButton[] _caixa = new RadioButton[4];
        private readonly TextBlock _renResumo = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
        private Button _renBotao;

        private bool _carregando;

        public GestorEstilosWindow(Document doc)
        {
            _doc = doc;
            _cache = new UsoCache(doc);
            double workH = SystemParameters.WorkArea.Height, workW = SystemParameters.WorkArea.Width;
            Width = Math.Min(1280, Math.Max(980, workW - 80));
            Height = Math.Min(860, Math.Max(620, workH - 40));
            MinWidth = 960; MinHeight = 560;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });

            var left = ToolChrome.Card(MontarLista());
            left.Margin = new Thickness(0, 0, 10, 12);
            body.Children.Add(left);

            var centro = ToolChrome.Card(MontarTabela());
            centro.Margin = new Thickness(0, 0, 10, 12);
            Grid.SetColumn(centro, 1);
            body.Children.Add(centro);

            var direita = MontarPainel();
            direita.Margin = new Thickness(0, 0, 0, 12);
            Grid.SetColumn(direita, 2);
            body.Children.Add(direita);

            var footer = new DockPanel();
            var fechar = ToolChrome.Secondary("Fechar", (s, e) => Close());
            fechar.IsCancel = true;
            DockPanel.SetDock(fechar, Dock.Right);
            footer.Children.Add(fechar);
            var csv = ToolChrome.Ghost("Exportar lista (CSV)", "Grava a lista desta categoria com os usos, para conferir no Excel.", (s, e) => ExportarCsv());
            csv.Margin = new Thickness(0, 0, 8, 0);
            DockPanel.SetDock(csv, Dock.Right);
            footer.Children.Add(csv);
            _status.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Muted");
            footer.Children.Add(_status);

            ToolChrome.DressCompact(this, Titulo, body, footer);

            string salvo = ToolPrefs.Get("gestorEstilos.tipo");
            EstiloTipo inicial = _tipos.FirstOrDefault(t => t.Nome == salvo) ?? _tipos[0];
            Loaded += (s, e) => SelecionarTipo(inicial);
        }

        // ==================================================================== layout

        private UIElement MontarLista()
        {
            _lista.SelectionChanged += (s, e) =>
            {
                if ((_lista.SelectedItem as ListBoxItem)?.Tag is EstiloTipo t && t != _tipo) Carregar(t);
            };
            foreach (var g in _tipos.GroupBy(t => t.Grupo))
            {
                var head = new ListBoxItem
                {
                    Content = g.Key.ToUpperInvariant(), IsEnabled = false, Focusable = false, FontSize = 10.5, FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, _lista.Items.Count == 0 ? 0 : 10, 0, 2), Padding = new Thickness(4, 0, 4, 0)
                };
                _lista.Items.Add(head);
                foreach (EstiloTipo t in g)
                    _lista.Items.Add(new ListBoxItem { Content = t.Nome, Tag = t, Padding = new Thickness(8, 5, 8, 5) });
            }
            var sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _lista };
            ScrollViewer.SetHorizontalScrollBarVisibility(_lista, ScrollBarVisibility.Disabled);
            return sv;
        }

        private UIElement MontarTabela()
        {
            var dock = new DockPanel();

            var topo = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            _titulo.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Ink");
            var filtros = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _soNaoUsados.SetResourceReference(StyleProperty, "Ja.Toggle");
            _soDuplicados.SetResourceReference(StyleProperty, "Ja.Toggle");
            _soNaoUsados.ToolTip = "Mostra só os estilos sem nenhum uso (candidatos a limpar).";
            _soDuplicados.ToolTip = "Mostra só os estilos que têm um gêmeo idêntico com outro nome (≡).";
            foreach (CheckBox c in new[] { _soNaoUsados, _soDuplicados }) { c.Checked += (s, e) => Filtrar(); c.Unchecked += (s, e) => Filtrar(); }
            _soDuplicados.Checked += (s, e) => { if (GarantirDuplicados()) Filtrar(); };
            filtros.Children.Add(_soNaoUsados);
            filtros.Children.Add(_soDuplicados);
            DockPanel.SetDock(filtros, Dock.Right);
            topo.Children.Add(filtros);
            topo.Children.Add(_titulo);
            DockPanel.SetDock(topo, Dock.Top);
            dock.Children.Add(topo);

            var busca = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var marcar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
            marcar.Children.Add(ToolChrome.Ghost("Marcar todos", "Marca todos os estilos visíveis.", (s, e) => Marcar(l => true)));
            marcar.Children.Add(ToolChrome.Ghost("Não usados", "Marca os visíveis sem nenhum uso.", (s, e) => Marcar(l => l.Usos == 0 && l.Item.Apagavel)));
            marcar.Children.Add(ToolChrome.Ghost("Nenhum", null, (s, e) => Marcar(l => false)));
            DockPanel.SetDock(marcar, Dock.Right);
            busca.Children.Add(marcar);
            // search waits for a short pause in typing instead of filtering on every key
            var espera = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            espera.Tick += (s, e) => { espera.Stop(); Filtrar(); };
            _busca.TextChanged += (s, e) => { espera.Stop(); espera.Start(); };
            busca.Children.Add(ToolChrome.SearchBox(_busca, "Buscar por nome ou detalhe…"));
            DockPanel.SetDock(busca, Dock.Top);
            dock.Children.Add(busca);

            _grid.ItemsSource = _linhas;
            _grid.AutoGenerateColumns = false;
            _grid.CanUserAddRows = false;
            _grid.CanUserDeleteRows = false;
            _grid.HeadersVisibility = DataGridHeadersVisibility.Column;
            _grid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
            _grid.RowHeight = 26;
            _grid.BorderThickness = new Thickness(0);
            _grid.Background = WM.Brushes.White;
            _grid.SelectionMode = DataGridSelectionMode.Extended;
            _grid.SelectionUnit = DataGridSelectionUnit.FullRow;
            _grid.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _grid.EnableRowVirtualization = true;
            _grid.EnableColumnVirtualization = true;
            VirtualizingPanel.SetIsVirtualizing(_grid, true);
            VirtualizingPanel.SetVirtualizationMode(_grid, VirtualizationMode.Recycling);
            _grid.SetResourceReference(DataGrid.HorizontalGridLinesBrushProperty, "Ja.Line");

            var check = new FrameworkElementFactory(typeof(CheckBox));
            check.SetBinding(ToggleButton_IsChecked, new Binding(nameof(EstiloLinha.Marcado)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            check.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            check.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            _grid.Columns.Add(new DataGridTemplateColumn { Header = "", Width = new DataGridLength(32), CellTemplate = new DataTemplate { VisualTree = check }, CanUserResize = false });

            _grid.Columns.Add(Coluna("Nome", nameof(EstiloLinha.Nome), new DataGridLength(2, DataGridLengthUnitType.Star), true));
            var cadeado = Coluna("", nameof(EstiloLinha.Cadeado), new DataGridLength(24));
            var stCad = new Style(typeof(TextBlock));
            stCad.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(nameof(EstiloLinha.CadeadoDica))));
            stCad.Setters.Add(new Setter(VerticalAlignmentProperty, VerticalAlignment.Center));
            cadeado.ElementStyle = stCad;
            _grid.Columns.Add(cadeado);
            _grid.Columns.Add(Coluna("Detalhe", nameof(EstiloLinha.Detalhe), new DataGridLength(1.4, DataGridLengthUnitType.Star)));
            var usos = Coluna("Usos", nameof(EstiloLinha.UsosTxt), new DataGridLength(84));
            usos.SortMemberPath = nameof(EstiloLinha.Usos);
            _grid.Columns.Add(usos);
            _grid.Columns.Add(Coluna("Vistas", nameof(EstiloLinha.VistasTxt), new DataGridLength(56)));
            var dup = Coluna("Dup.", nameof(EstiloLinha.DupTxt), new DataGridLength(48));
            dup.SortMemberPath = nameof(EstiloLinha.Duplicados);
            _grid.Columns.Add(dup);
            _colNovo = new DataGridTextColumn
            {
                Header = "Novo nome", Binding = new Binding(nameof(EstiloLinha.NovoNome)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
                Width = new DataGridLength(1.6, DataGridLengthUnitType.Star), Visibility = System.Windows.Visibility.Collapsed
            };
            _grid.Columns.Add(_colNovo);
            _grid.SelectionChanged += (s, e) => AtualizarPainel();
            _grid.PreviewKeyDown += (s, e) =>
            {
                // Space marks / unmarks the selected rows
                if (e.Key == Key.Space && !(_grid.CurrentColumn == _colNovo))
                {
                    var sel = _grid.SelectedItems.OfType<EstiloLinha>().ToList();
                    bool v = sel.Any(l => !l.Marcado);
                    foreach (var l in sel) l.Marcado = v;
                    e.Handled = true;
                }
            };
            dock.Children.Add(_grid);
            return dock;
        }

        private static readonly DependencyProperty ToggleButton_IsChecked = System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty;

        private static DataGridTextColumn Coluna(string header, string path, DataGridLength w, bool tinta = false)
        {
            var c = new DataGridTextColumn { Header = header, Binding = new Binding(path), Width = w, IsReadOnly = true };
            if (tinta)
            {
                var st = new Style(typeof(TextBlock));
                st.Setters.Add(new Setter(TextBlock.ForegroundProperty, new Binding(nameof(EstiloLinha.Tinta))));
                st.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
                st.Setters.Add(new Setter(VerticalAlignmentProperty, VerticalAlignment.Center));
                c.ElementStyle = st;
            }
            return c;
        }

        private FrameworkElement MontarPainel()
        {
            var dock = new DockPanel();
            var modos = new StackPanel { Orientation = Orientation.Horizontal };
            string[] nomes = { "Onde", "Unir", "Limpar", "Renomear" };
            string[] dicas =
            {
                "Em que vistas o estilo aparece; abre a vista e seleciona os elementos.",
                "Passa todos os usos dos marcados para um estilo e apaga os marcados.",
                "Apaga os marcados que não têm nenhum uso.",
                "Prefixo, sufixo, procurar/substituir e maiúsculas nos marcados, com prévia."
            };
            for (int i = 0; i < 4; i++)
            {
                var rb = new RadioButton { Content = nomes[i], GroupName = "gest-modo", ToolTip = dicas[i] };
                rb.SetResourceReference(StyleProperty, "Ja.Segment");
                int k = i;
                rb.Checked += (s, e) => TrocarModo(k);
                modos.Children.Add(rb);
                _modos[i] = rb;
            }
            var track = ToolChrome.Track(modos);
            track.Margin = new Thickness(0, 0, 0, 10);
            DockPanel.SetDock(track, Dock.Top);
            dock.Children.Add(track);
            dock.Children.Add(ToolChrome.Card(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _painel }));
            int m;
            int.TryParse(ToolPrefs.Get("gestorEstilos.modo") ?? "0", out m);
            _modos[Math.Max(0, Math.Min(3, m))].IsChecked = true;
            return dock;
        }

        private UIElement PainelOnde()
        {
            var sp = new StackPanel();
            _ondeNome.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Ink");
            _ondeResumo.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Muted");
            _ondeNota.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Muted");
            sp.Children.Add(_ondeNome);
            sp.Children.Add(_ondeResumo);
            _ondeVistas.MouseDoubleClick += (s, e) => Ir();
            _ondeVistas.SelectionChanged += (s, e) => _ondeIr.IsEnabled = (_ondeVistas.SelectedItem as VistaUso)?.PodeAbrir ?? false;
            sp.Children.Add(_ondeVistas);
            _ondeIr = ToolChrome.Primary("Abrir e selecionar", (s, e) => Ir());
            _ondeIr.Margin = new Thickness(0, 8, 0, 0);
            _ondeIr.HorizontalAlignment = HorizontalAlignment.Left;
            _ondeIr.ToolTip = "Fecha o gestor, abre a vista escolhida e seleciona os elementos com este estilo.";
            sp.Children.Add(_ondeIr);
            sp.Children.Add(_ondeNota);
            return sp;
        }

        private UIElement PainelUnir()
        {
            var sp = new StackPanel();
            sp.Children.Add(Subtitulo("Unir os marcados"));
            _unirResumo.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Muted");
            sp.Children.Add(_unirResumo);
            sp.Children.Add(ToolChrome.Labeled("Em", _unirAlvo, "O estilo que fica. Todos os usos dos marcados passam para ele."));
            _unirBotao = ToolChrome.Primary("Unir", (s, e) => Unir());
            _unirBotao.HorizontalAlignment = HorizontalAlignment.Left;
            sp.Children.Add(_unirBotao);

            var sep = new Border { Height = 1, Margin = new Thickness(0, 16, 0, 12) };
            sep.SetResourceReference(Border.BackgroundProperty, "Ja.Line");
            sp.Children.Add(sep);
            sp.Children.Add(Subtitulo("Duplicados"));
            _unirDupResumo.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Muted");
            sp.Children.Add(_unirDupResumo);
            _unirDup = ToolChrome.Secondary("Unir duplicados", (s, e) => UnirDuplicados(),
                "Em cada grupo de estilos idênticos (≡), fica o mais usado e os outros são unidos nele.");
            _unirDup.HorizontalAlignment = HorizontalAlignment.Left;
            sp.Children.Add(_unirDup);
            return sp;
        }

        private UIElement PainelLimpar()
        {
            var sp = new StackPanel();
            sp.Children.Add(Subtitulo("Limpar não usados"));
            _limparResumo.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Muted");
            sp.Children.Add(_limparResumo);
            var marcar = ToolChrome.Secondary("Marcar não usados", (s, e) => Marcar(l => l.Usos == 0 && l.Item.Apagavel));
            marcar.HorizontalAlignment = HorizontalAlignment.Left;
            marcar.Margin = new Thickness(0, 0, 0, 8);
            sp.Children.Add(marcar);
            _limparBotao = ToolChrome.Primary("Apagar", (s, e) => Limpar());
            _limparBotao.HorizontalAlignment = HorizontalAlignment.Left;
            sp.Children.Add(_limparBotao);
            sp.Children.Add(ToolChrome.Muted("Só saem os marcados com 0 usos; os usados e os que o Revit não deixa apagar (🔒: do Revit, último tipo da família ou do sistema) nem são tentados. Desfaz com Ctrl+Z.", 11));
            ((TextBlock)sp.Children[sp.Children.Count - 1]).Margin = new Thickness(0, 10, 0, 0);
            return sp;
        }

        private UIElement PainelRenomear()
        {
            var sp = new StackPanel();
            sp.Children.Add(Subtitulo("Renomear os marcados"));
            foreach (TextBox t in new[] { _prefixo, _sufixo, _procurar, _substituir })
            {
                t.TextChanged += (s, e) => Previa();
            }
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            void Por(UIElement e, int row, int col) { Grid.SetRow(e, row); Grid.SetColumn(e, col); g.Children.Add(e); }
            g.RowDefinitions.Add(new RowDefinition());
            g.RowDefinitions.Add(new RowDefinition());
            Por(ToolChrome.Labeled("Prefixo", _prefixo), 0, 0);
            Por(ToolChrome.Labeled("Sufixo", _sufixo), 0, 2);
            Por(ToolChrome.Labeled("Procurar", _procurar, "Sem diferença de maiúsculas."), 1, 0);
            Por(ToolChrome.Labeled("Substituir por", _substituir), 1, 2);
            sp.Children.Add(g);
            var caixa = new StackPanel { Orientation = Orientation.Horizontal };
            string[] ops = { "Manter", "MAIÚSC.", "minúsc.", "Título" };
            for (int i = 0; i < 4; i++)
            {
                var rb = new RadioButton { Content = ops[i], GroupName = "gest-caixa", IsChecked = i == 0 };
                rb.SetResourceReference(StyleProperty, "Ja.Segment");
                rb.Checked += (s, e) => Previa();
                caixa.Children.Add(rb);
                _caixa[i] = rb;
            }
            sp.Children.Add(ToolChrome.Labeled("Maiúsculas", ToolChrome.Track(caixa)));
            _renResumo.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Muted");
            sp.Children.Add(_renResumo);
            _renBotao = ToolChrome.Primary("Renomear", (s, e) => Renomear());
            _renBotao.HorizontalAlignment = HorizontalAlignment.Left;
            sp.Children.Add(_renBotao);
            sp.Children.Add(ToolChrome.Muted("A prévia aparece na coluna “Novo nome”, que também pode ser editada à mão.", 11));
            ((TextBlock)sp.Children[sp.Children.Count - 1]).Margin = new Thickness(0, 10, 0, 0);
            return sp;
        }

        private static TextBlock Subtitulo(string t)
        {
            var tb = new TextBlock { Text = t, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Ink");
            return tb;
        }

        private UIElement[] _paineis;
        private int _modo;

        private void TrocarModo(int k)
        {
            if (_paineis == null) _paineis = new[] { PainelOnde(), PainelUnir(), PainelLimpar(), PainelRenomear() };
            _modo = k;
            if (k == 1) GarantirDuplicados();
            _painel.Content = _paineis[k];
            _colNovo.Visibility = k == 3 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            ToolPrefs.Set("gestorEstilos.modo", k.ToString(CultureInfo.InvariantCulture));
            if (k != 3) foreach (var l in _todas) l.NovoNome = null;
            AtualizarPainel();
        }

        // ==================================================================== data

        private void SelecionarTipo(EstiloTipo t)
        {
            foreach (var o in _lista.Items.OfType<ListBoxItem>())
                if (o.Tag == t) { _lista.SelectedItem = o; o.BringIntoView(); return; }
        }

        /// <summary>Something was deleted without re-reading: the next kind reads the uses again.</summary>
        private bool _cacheSujo;

        private void Carregar(EstiloTipo t)
        {
            if (_cacheSujo) { _cache.Limpar(); _cacheSujo = false; }
            _tipo = t;
            ToolPrefs.Set("gestorEstilos.tipo", t.Nome);
            Recarregar();
        }

        private void Recarregar()
        {
            if (_tipo == null) return;
            Cursor = Cursors.Wait;
            _status.Text = "Lendo " + _tipo.Nome.ToLowerInvariant() + " e seus usos…";
            try { Dispatcher.Invoke(new Action(() => { }), System.Windows.Threading.DispatcherPriority.Render); } catch { }
            _carregando = true;
            _dupOk = false;
            try
            {
                var marcados = new HashSet<ElementId>(_todas.Where(l => l.Marcado).Select(l => l.Item.Id));
                List<EstiloItem> itens;
                try
                {
                    itens = _tipo.Listar(_doc);
                    GestorEstilos.MarcarNaoApagaveis(_doc, itens);
                    _tipo.Usos(_doc, itens, _cache);
                    // duplicates (comparing every parameter of every type) only when they are needed
                    if (PrecisaDuplicados) { _tipo.Assinar(_doc, itens); _dupOk = true; }
                }
                catch (Exception ex)
                {
                    Logger.Warn("[Gestor de Estilos] " + _tipo.Nome + ": " + ex);
                    itens = new List<EstiloItem>();
                    _status.Text = "Não foi possível ler: " + ex.Message;
                }
                _todas = itens.Select(i => new EstiloLinha(i)).ToList();
                ContarDuplicados();
                foreach (var l in _todas)
                {
                    l.Marcado = marcados.Contains(l.Item.Id);
                    l.MarcadoMudou = () => { if (!_carregando) AtualizarPainel(); };
                    l.PropertyChanged += (o, a) => { if (a.PropertyName == nameof(EstiloLinha.NovoNome) && !_emPrevia && _modo == 3) ContarRenomes(); };
                }
                _titulo.Text = _tipo.Nome;
            }
            finally { _carregando = false; Cursor = null; }
            Filtrar();
        }

        private bool _dupOk;
        private bool PrecisaDuplicados => _modo == 1 || _soDuplicados.IsChecked == true;

        private void ContarDuplicados()
        {
            foreach (var l in _todas) l.Duplicados = 0;
            foreach (var g in _todas.Where(l => !string.IsNullOrEmpty(l.Item.Assinatura)).GroupBy(l => l.Item.Compat + "#" + l.Item.Assinatura))
            {
                int n = g.Count();
                foreach (var l in g) l.Duplicados = n;
            }
        }

        /// <summary>Reads the signatures of the current kind now (first time Unir or "Só duplicados" is used). True if it read them.</summary>
        private bool GarantirDuplicados()
        {
            if (_dupOk || _tipo == null || _todas.Count == 0) return false;
            Cursor = Cursors.Wait;
            try
            {
                _status.Text = "Procurando estilos idênticos…";
                try { Dispatcher.Invoke(new Action(() => { }), System.Windows.Threading.DispatcherPriority.Render); } catch { }
                _tipo.Assinar(_doc, _todas.Select(l => l.Item).ToList());
                ContarDuplicados();
                _dupOk = true;
            }
            catch (Exception ex) { Logger.Warn("[Gestor de Estilos] duplicados: " + ex.Message); }
            finally { Cursor = null; }
            Filtrar();
            return true;
        }

        private void Filtrar()
        {
            string q = TextUtil.Fold(_busca.Text ?? "").Trim();
            _carregando = true;
            bool soNao = _soNaoUsados.IsChecked == true, soDup = _soDuplicados.IsChecked == true;
            // one new collection = one refresh of the table (not one per row)
            _linhas = new ObservableCollection<EstiloLinha>(_todas.Where(l =>
                (!soNao || l.Usos == 0) && (!soDup || l.Duplicados > 1) && (q.Length == 0 || l.Busca.Contains(q))));
            _grid.ItemsSource = _linhas;
            _carregando = false;
            int naoUsados = _todas.Count(l => l.Usos == 0 && l.Item.Apagavel);
            int grupos = _todas.Where(l => l.Duplicados > 1).Select(l => l.Item.Compat + "#" + l.Item.Assinatura).Distinct().Count();
            _status.Text = $"{_todas.Count} estilo(s) · {naoUsados} sem uso" + (grupos > 0 ? $" · {grupos} grupo(s) de duplicados" : !_dupOk ? " · duplicados: abra Unir" : "") +
                           (_linhas.Count != _todas.Count ? $" · mostrando {_linhas.Count}" : "");
            AtualizarPainel();
        }

        private void Marcar(Func<EstiloLinha, bool> regra)
        {
            _carregando = true;
            foreach (var l in _linhas) l.Marcado = regra(l);
            _carregando = false;
            AtualizarPainel();
        }

        private List<EstiloLinha> Marcados => _todas.Where(l => l.Marcado).ToList();

        // ==================================================================== right panel

        private sealed class VistaUso
        {
            public ElementId Id;
            public string Nome;
            public List<ElementId> Elementos = new List<ElementId>();
            public bool PodeAbrir;
            public int Usos;
            public override string ToString() => Elementos.Count > 0 ? $"{Nome}   ({Elementos.Count})" : Usos > 1 ? $"{Nome}   ({Usos})" : Nome;
        }

        private void AtualizarPainel()
        {
            if (_tipo == null || _paineis == null || _carregando) return;
            var marc = Marcados;
            switch (_modo)
            {
                case 0: Onde(); break;
                case 1:
                    {
                        bool pode = _tipo.PodeUnir;
                        var fontes = marc.Where(l => l.Item.Apagavel).ToList();
                        var compat = marc.Select(l => l.Item.Compat).Distinct().ToList();
                        object antes = _unirAlvo.SelectedItem;
                        _unirAlvo.ItemsSource = null;
                        if (!pode) _unirResumo.Text = _tipo is Familias ? "Famílias não se unem: use “Tipos de família” e una os tipos." : "Esta categoria não permite unir.";
                        else if (marc.Count == 0) _unirResumo.Text = "Marque na tabela os estilos que devem sumir.";
                        else if (compat.Count > 1) _unirResumo.Text = "Os marcados são de tipos diferentes entre si (veja a coluna Detalhe): marque só estilos do mesmo tipo.";
                        else
                        {
                            int usos = fontes.Sum(l => l.Usos);
                            _unirResumo.Text = $"{fontes.Count} estilo(s) marcados, {usos} uso(s), passam para o estilo escolhido e são apagados." +
                                               (fontes.Count < marc.Count ? " Os com 🔒 recebem os usos mas não são apagados." : "");
                            var alvos = _todas.Where(l => l.Item.Compat == compat[0]).OrderByDescending(l => !l.Marcado).ThenByDescending(l => l.Usos).ToList();
                            _unirAlvo.ItemsSource = alvos;
                            _unirAlvo.SelectedItem = alvos.Contains(antes as EstiloLinha) ? antes : alvos.FirstOrDefault(l => !l.Marcado) ?? alvos.FirstOrDefault();
                        }
                        _unirAlvo.IsEnabled = _unirAlvo.ItemsSource != null;
                        _unirBotao.IsEnabled = _unirAlvo.ItemsSource != null;
                        int grupos = DuplicadosGrupos().Count;
                        _unirDupResumo.Text = !pode ? "—" : grupos == 0 ? "Nenhum estilo idêntico com outro nome nesta categoria." :
                            $"{grupos} grupo(s) de estilos idênticos (coluna ≡). Em cada grupo fica o mais usado.";
                        _unirDup.IsEnabled = pode && grupos > 0;
                        break;
                    }
                case 2:
                    {
                        int apagaveis = marc.Count(l => l.Usos == 0 && l.Item.Apagavel);
                        _limparResumo.Text = marc.Count == 0
                            ? $"{_todas.Count(l => l.Usos == 0 && l.Item.Apagavel)} estilo(s) sem uso que podem sair nesta categoria. Marque os que devem sair."
                            : $"{apagaveis} dos {marc.Count} marcados podem ser apagados." + (apagaveis < marc.Count ? " Os outros estão em uso ou o Revit não deixa apagar (🔒 — passe o mouse para ver o motivo)." : "");
                        _limparBotao.Content = apagaveis > 0 ? $"Apagar {apagaveis}" : "Apagar";
                        _limparBotao.IsEnabled = apagaveis > 0;
                        break;
                    }
                case 3: Previa(); break;
            }
        }

        private void Onde()
        {
            var sel = _grid.SelectedItem as EstiloLinha;
            _ondeVistas.Items.Clear();
            _ondeNota.Text = _tipo.NotaUso ?? "";
            if (sel == null)
            {
                _ondeNome.Text = "Selecione um estilo na tabela.";
                _ondeResumo.Text = "";
                _ondeIr.IsEnabled = false;
                return;
            }
            _ondeNome.Text = sel.Nome;
            EstiloItem it = sel.Item;
            _ondeResumo.Text = it.Total == 0 ? "Sem nenhum uso no projeto." :
                $"{it.Usos} uso(s)" + (it.Refs > 0 ? $" e {it.Refs} referência(s) em tipos, estilos de objeto ou materiais" : "") +
                (it.Vistas.Count > 0 ? $", em {it.Vistas.Count} vista(s)." : ".");

            var porVista = new Dictionary<ElementId, VistaUso>();
            var modelo = new VistaUso { Id = ElementId.InvalidElementId, Nome = "No modelo (qualquer vista)", PodeAbrir = true };
            for (int i = 0; i < it.Instancias.Count; i++)
            {
                ElementId id = it.Instancias[i];
                ElementId v = i < it.InstVista.Count ? it.InstVista[i] : ElementId.InvalidElementId;
                if (v == null || v == ElementId.InvalidElementId) { modelo.Elementos.Add(id); continue; }
                if (!porVista.TryGetValue(v, out var vu))
                {
                    View view = _doc.GetElement(v) as View;
                    porVista[v] = vu = new VistaUso
                    {
                        Id = v, Nome = view == null ? "?" : view.IsTemplate ? view.Name + " (modelo de vista)" : view.Name,
                        PodeAbrir = view != null && !view.IsTemplate && view.CanBePrinted
                    };
                }
                if (!it.UsosSaoVistas) vu.Elementos.Add(id);
                else vu.Usos++;
            }
            if (modelo.Elementos.Count > 0) _ondeVistas.Items.Add(modelo);
            foreach (var vu in porVista.Values.OrderBy(v => v.Nome, StringComparer.CurrentCultureIgnoreCase)) _ondeVistas.Items.Add(vu);
            if (_ondeVistas.Items.Count > 0) _ondeVistas.SelectedIndex = 0;
            _ondeIr.IsEnabled = (_ondeVistas.SelectedItem as VistaUso)?.PodeAbrir ?? false;
        }

        private void Ir()
        {
            if (!(_ondeVistas.SelectedItem is VistaUso vu) || !vu.PodeAbrir) return;
            IrPara = new EstiloIrPara { Vista = vu.Id, Elementos = vu.Elementos.ToList() };
            DialogResult = true;
        }

        private List<List<EstiloLinha>> DuplicadosGrupos() =>
            _todas.Where(l => l.Duplicados > 1).GroupBy(l => l.Item.Compat + "#" + l.Item.Assinatura).Select(g => g.ToList()).ToList();

        private bool RegraAtiva => !string.IsNullOrEmpty(_prefixo.Text) || !string.IsNullOrEmpty(_sufixo.Text) ||
                                   !string.IsNullOrEmpty(_procurar.Text) || _caixa[0].IsChecked != true;
        private bool _emPrevia;

        private void Previa()
        {
            if (_modo != 3 || _renBotao == null || _tipo == null) return;
            if (RegraAtiva)
            {
                _emPrevia = true;
                foreach (var l in _todas) l.NovoNome = l.Marcado ? Transformar(l.Nome) : null;
                _emPrevia = false;
            }
            ContarRenomes();
        }

        /// <summary>Count of names that change (rule preview or typed by hand in the table).</summary>
        private void ContarRenomes()
        {
            if (_renBotao == null || _tipo == null) return;
            int n = _todas.Count(l => !string.IsNullOrWhiteSpace(l.NovoNome) && l.NovoNome.Trim() != l.Nome);
            _renResumo.Text = !_tipo.PodeRenomear ? "Esta categoria não permite renomear." :
                n == 0 ? "Marque estilos e preencha uma regra (ou digite na coluna “Novo nome”); a prévia aparece na tabela." :
                $"{n} estilo(s) mudam de nome." + (_tipo.RenomeiaRecriando ? " Estilos de linha são recriados com o novo nome (o Revit não renomeia); as linhas passam para o novo." : "");
            _renBotao.IsEnabled = _tipo.PodeRenomear && n > 0;
        }

        private string Transformar(string nome)
        {
            string s = nome ?? "";
            if (!string.IsNullOrEmpty(_procurar.Text))
                s = Regex.Replace(s, Regex.Escape(_procurar.Text), (_substituir.Text ?? "").Replace("$", "$$"), RegexOptions.IgnoreCase);
            CultureInfo pt = CultureInfo.GetCultureInfo("pt-BR");
            if (_caixa[1].IsChecked == true) s = s.ToUpper(pt);
            else if (_caixa[2].IsChecked == true) s = s.ToLower(pt);
            else if (_caixa[3].IsChecked == true) s = pt.TextInfo.ToTitleCase(s.ToLower(pt));
            return (_prefixo.Text ?? "") + s + (_sufixo.Text ?? "");
        }

        // ==================================================================== actions

        private bool Confirmar(string texto) =>
            MessageBox.Show(this, texto, "JA Tools — " + Titulo, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

        private void Aviso(string texto, List<string> problemas)
        {
            if (problemas.Count > 0)
                texto += "\n\nAtenção:\n• " + string.Join("\n• ", problemas.Take(12)) + (problemas.Count > 12 ? $"\n… e mais {problemas.Count - 12}." : "");
            MessageBox.Show(this, texto, "JA Tools — " + Titulo, MessageBoxButton.OK, problemas.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }

        /// <summary>Runs one change in its own transaction (one Ctrl+Z), then reads everything again.</summary>
        private bool Executar(string nome, Action<List<string>> acao, out List<string> problemas) => Executar(nome, acao, true, out problemas);

        /// <param name="reler">False: the action fixes the table itself (Limpar) — no full re-read of the uses.</param>
        private bool Executar(string nome, Action<List<string>> acao, bool reler, out List<string> problemas)
        {
            problemas = new List<string>();
            Cursor = Cursors.Wait;
            bool ok = false;
            try
            {
                using (var t = new Transaction(_doc, "JA Tools - Gestor de Estilos: " + nome))
                {
                    t.Start();
                    RevitDwgUnifier.Export.FailureSwallower.Attach(t);
                    acao(problemas);
                    ok = t.Commit() == TransactionStatus.Committed;
                    if (!ok) problemas.Add("O Revit não aceitou a alteração; nada foi mudado.");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("[Gestor de Estilos] " + nome, ex);
                problemas.Add(ex.Message);
            }
            finally
            {
                Cursor = null;
                if (reler || !ok)
                {
                    _cache.Limpar();
                    _cacheSujo = false;
                    Recarregar();
                }
            }
            return ok;
        }

        /// <summary>Merges each group of sources into its target, then deletes the sources that ended with no use at all.</summary>
        private int UnirEApagar(List<(EstiloItem alvo, List<EstiloItem> fontes)> pares, List<string> problemas, ref int apagados)
        {
            int n = 0;
            var todas = new List<EstiloItem>();
            foreach (var (alvo, fs) in pares)
            {
                var fontes = fs.Where(f => f.Id != alvo.Id).ToList();
                if (fontes.Count == 0) continue;
                n += _tipo.Unir(_doc, alvo, fontes, _cache, problemas);
                todas.AddRange(fontes);
            }
            _doc.Regenerate();
            _cache.Limpar();
            // read the uses again: a source that still has any (in a group, not accepted...) is kept
            var conferir = todas.Where(f => !f.Protegido).Select(f => new EstiloItem { Id = f.Id, Extra = f.Extra, Nome = f.Nome, Compat = f.Compat }).ToList();
            _tipo.Usos(_doc, conferir, _cache);
            foreach (var f in conferir.Where(f => f.Total > 0))
                problemas.Add($"{f.Nome}: ainda tem {f.Total} uso(s) e foi mantido.");
            apagados += GestorEstilos.ApagarEmLote(_doc, conferir.Where(f => f.Total == 0).ToList(), problemas).Count;
            _cache.Limpar();
            return n;
        }

        private void Unir()
        {
            if (!(_unirAlvo.SelectedItem is EstiloLinha alvo)) return;
            var fontes = Marcados.Where(l => l != alvo).Select(l => l.Item).ToList();
            if (fontes.Count == 0) { Aviso("Marque pelo menos um estilo diferente do escolhido em “Em”.", new List<string>()); return; }
            if (!Confirmar($"Passar os usos de {fontes.Count} estilo(s) para “{alvo.Nome}” e apagá-los?\n\n" +
                           string.Join("\n", fontes.Take(10).Select(f => "• " + f.Nome)) + (fontes.Count > 10 ? $"\n… e mais {fontes.Count - 10}" : ""))) return;
            int n = 0, apagados = 0;
            Executar("Unir", p => n = UnirEApagar(new List<(EstiloItem, List<EstiloItem>)> { (alvo.Item, fontes) }, p, ref apagados), out var problemas);
            Aviso($"{n} uso(s) passaram para “{alvo.Nome}”; {apagados} estilo(s) apagados.", problemas);
        }

        private void UnirDuplicados()
        {
            var grupos = DuplicadosGrupos();
            if (grupos.Count == 0) return;
            int fontes = grupos.Sum(g => g.Count - 1);
            if (!Confirmar($"Unir {grupos.Count} grupo(s) de estilos idênticos? Em cada grupo fica o mais usado; {fontes} estilo(s) são unidos nele e apagados.")) return;
            int n = 0, apagados = 0;
            var pares = new List<(EstiloItem, List<EstiloItem>)>();
            foreach (var g in grupos)
            {
                var alvo = g.OrderByDescending(l => !l.Item.Apagavel).ThenByDescending(l => l.Usos).ThenBy(l => l.Nome.Length).First();
                pares.Add((alvo.Item, g.Where(l => l != alvo).Select(l => l.Item).ToList()));
            }
            Executar("Unir duplicados", p => n = UnirEApagar(pares, p, ref apagados), out var problemas);
            Aviso($"{grupos.Count} grupo(s) unidos: {n} uso(s) trocados, {apagados} estilo(s) apagados.", problemas);
        }

        private void Limpar()
        {
            var alvo = Marcados.Where(l => l.Usos == 0 && l.Item.Apagavel).ToList();
            if (alvo.Count == 0) return;
            if (!Confirmar($"Apagar {alvo.Count} estilo(s) sem uso?\n\n" + string.Join("\n", alvo.Take(12).Select(l => "• " + l.Nome)) +
                           (alvo.Count > 12 ? $"\n… e mais {alvo.Count - 12}" : ""))) return;
            var apagados = new List<EstiloItem>();
            // all at once (one Delete), and no full re-read afterwards: unused styles take nothing with
            // them, so the table just loses their rows
            bool ok = Executar("Limpar", p => apagados = GestorEstilos.ApagarEmLote(_doc, alvo.Select(l => l.Item).ToList(), p), false, out var problemas);
            if (ok)
            {
                var fora = new HashSet<EstiloItem>(apagados);
                _todas = _todas.Where(l => !fora.Contains(l.Item)).ToList();
                // what was left may now be the last of its family/system: lock it
                try { GestorEstilos.MarcarNaoApagaveis(_doc, _todas.Select(l => l.Item)); foreach (var l in _todas) l.AvisarCadeado(); } catch { }
                _cacheSujo = true;
                ContarDuplicados();
                Filtrar();
            }
            Aviso($"{(ok ? apagados.Count : 0)} estilo(s) apagados.", problemas);
        }

        private void Renomear()
        {
            var lista = _todas.Where(l => !string.IsNullOrWhiteSpace(l.NovoNome) && l.NovoNome.Trim() != l.Nome).ToList();
            if (lista.Count == 0) return;
            var problemasPre = new List<string>();
            var novos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ficam = new HashSet<string>(_todas.Where(l => !lista.Contains(l)).Select(l => l.Nome), StringComparer.OrdinalIgnoreCase);
            var validos = new List<EstiloLinha>();
            foreach (var l in lista)
            {
                string nn = l.NovoNome.Trim();
                if (nn.IndexOfAny(new[] { '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~', '\\', ':' }) >= 0) { problemasPre.Add($"{l.Nome}: “{nn}” tem caractere que o Revit não aceita."); continue; }
                if (ficam.Contains(nn) || !novos.Add(nn)) { problemasPre.Add($"{l.Nome}: “{nn}” repete outro nome."); continue; }
                validos.Add(l);
            }
            if (validos.Count == 0) { Aviso("Nenhum nome pode ser aplicado.", problemasPre); return; }
            if (!Confirmar($"Renomear {validos.Count} estilo(s)?\n\n" + string.Join("\n", validos.Take(10).Select(l => $"• {l.Nome}  →  {l.NovoNome.Trim()}")) +
                           (validos.Count > 10 ? $"\n… e mais {validos.Count - 10}" : ""))) return;
            int n = 0;
            Executar("Renomear", p =>
            {
                // two passes avoid clashes when names swap (A→B and B→A)
                var temp = new List<(EstiloLinha l, string novo)>();
                foreach (var l in validos)
                {
                    if (_tipo.RenomeiaRecriando) { if (_tipo.Renomear(_doc, l.Item, l.NovoNome.Trim(), _cache, p)) n++; continue; }
                    string tmp = l.Nome + " ~" + Guid.NewGuid().ToString("N").Substring(0, 6);
                    if (_tipo.Renomear(_doc, l.Item, tmp, _cache, p)) temp.Add((l, l.NovoNome.Trim()));
                }
                foreach (var (l, novo) in temp)
                {
                    if (_tipo.Renomear(_doc, l.Item, novo, _cache, p)) n++;
                    else _tipo.Renomear(_doc, l.Item, l.Nome, _cache, p);
                }
            }, out var problemas);
            problemas.InsertRange(0, problemasPre);
            // the rule was used: clear it so the new names do not get it twice
            foreach (TextBox t in new[] { _prefixo, _sufixo, _procurar, _substituir }) t.Text = "";
            _caixa[0].IsChecked = true;
            foreach (var l in _todas) l.NovoNome = null;
            ContarRenomes();
            Aviso($"{n} estilo(s) renomeados.", problemas);
        }

        private void ExportarCsv()
        {
            if (_tipo == null) return;
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "CSV (*.csv)|*.csv", FileName = RevitUtil.Sanitize("Estilos - " + _tipo.Nome) + ".csv",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            };
            if (dlg.ShowDialog(this) != true) return;
            var sb = new StringBuilder();
            sb.AppendLine("Nome;Detalhe;Usos;Referencias;Vistas;Duplicados;Protegido;Id");
            string Q(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
            foreach (var l in _todas)
                sb.AppendLine(string.Join(";", Q(l.Nome), Q(l.Detalhe), l.Item.Usos, l.Item.Refs, l.Item.Vistas.Count, l.Duplicados > 1 ? l.Duplicados : 0,
                                          l.Item.Protegido ? "sim" : "", l.Item.Id.Value));
            try
            {
                File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
                _status.Text = "Lista gravada em " + dlg.FileName;
            }
            catch (Exception ex) { Aviso("Não foi possível gravar: " + ex.Message, new List<string>()); }
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            try { ToolPrefs.Save(); } catch { }
        }
    }
}
