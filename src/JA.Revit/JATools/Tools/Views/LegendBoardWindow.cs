using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using RevitDwgUnifier.Tools.Common;
using DB = Autodesk.Revit.DB;
// Autodesk.Revit.UI / Autodesk.Revit.DB are NOT imported: they define TextBox, ComboBox, Grid...

namespace RevitDwgUnifier.Tools.Views
{
    /// <summary>
    /// Gerar Detalhes window: one legend ("ficha") per type, drawn with a template the user
    /// picks and customises (layout, texts, views, dimensions, information table), optionally
    /// placed on sheets. Left: types. Right: template bar, tabs, live preview of the card / sheet.
    /// </summary>
    public sealed class LegendBoardWindow : Window
    {
        private const string P = "QuadroLegendas.";
        private readonly LegendProbe _probe;
        private List<LegendCandidate> _items = new List<LegendCandidate>();
        private readonly Func<CategoryInfo, Action<int, int, string>, string> _loadCategory;
        private readonly Func<Action<int, int, string>, string> _measureTitleBlocks;
        private readonly HashSet<DB.ElementId> _preselected;
        private readonly Dictionary<CategoryInfo, CheckBox> _catPills = new Dictionary<CategoryInfo, CheckBox>();
        private readonly Border _busy = new Border { Visibility = Visibility.Collapsed, CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 0, 8) };
        private readonly TextBlock _busyText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11.5 };
        private readonly ProgressBar _busyBar = new ProgressBar { Height = 6, Margin = new Thickness(0, 6, 0, 0), Minimum = 0, Maximum = 1 };
        private bool _working;
        public LegendRequest Request { get; private set; }

        /// <summary>Texts of fichas already in the project that "Atualizar fichas" can rewrite
        /// (set before showing; 0 hides the button).</summary>
        public int FichasExistentes { get; set; }

        /// <summary>The user chose "Atualizar fichas" (1.17: the old Atualizar Fichas button).</summary>
        public bool AtualizarFichas { get; private set; }

        // ---- templates
        private readonly List<LegendTemplate> _templates;
        private LegendTemplate _t;                 // working copy shown in the controls
        private readonly ComboBox _templateCombo = new ComboBox { MinWidth = 260 };
        private readonly TextBlock _templateState = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        private bool _dirty, _loading;
        private readonly List<Action> _loaders = new List<Action>();

        // ---- left list
        private readonly TextBox _search = new TextBox();
        private readonly CheckBox _onlyUsed = new CheckBox { Content = "Só tipos usados no projeto", Margin = new Thickness(0, 8, 0, 4) };
        private readonly ComboBox _phase = new ComboBox { MinWidth = 200 };
        /// <summary>Phase of the elements (Invalid = all): recounts the loaded types. Set by the command.</summary>
        public Func<DB.ElementId, string> SetPhase { get; set; }
        private string _initialPhase;
        private readonly StackPanel _list = new StackPanel { Margin = new Thickness(6, 4, 6, 4) };
        private readonly Dictionary<LegendCandidate, CheckBox> _boxes = new Dictionary<LegendCandidate, CheckBox>();
        private CheckBox _lastClicked;
        private readonly Dictionary<string, TextBlock> _groupHeads = new Dictionary<string, TextBlock>();
        private TextBlock _badge;
        private readonly TextBlock _empty = new TextBlock { Margin = new Thickness(4, 8, 4, 8), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };

        // ---- per-run options (not part of the template)
        private readonly TextBox _pattern = new TextBox();
        private readonly CheckBox _fillMarks = new CheckBox { Content = "Preencher a Marca de tipo vazia (P01 portas, J01 janelas, outras pelas iniciais)" };
        private readonly CheckBox _sheetsOn = new CheckBox { Content = "Colocar as legendas em pranchas", FontWeight = FontWeights.SemiBold };
        private readonly ComboBox _titleBlock = new ComboBox { MinWidth = 260 };
        private readonly TextBox _sRows = new TextBox { Width = 50 }, _sCols = new TextBox { Width = 50 };
        private readonly TextBox _mLeft = new TextBox { Width = 50 }, _mTop = new TextBox { Width = 50 }, _mRight = new TextBox { Width = 50 }, _mBottom = new TextBox { Width = 50 };
        private readonly CheckBox _fit = new CheckBox { Content = "Ajustar o tamanho da ficha à prancha (linhas × colunas preenchem a área útil)" };
        private readonly TextBox _firstNumber = new TextBox { Width = 90 };
        private readonly TextBox _sheetName = new TextBox();
        private readonly CheckBox _hideTitle = new CheckBox { Content = "Viewport sem título (só a ficha)" };
        private readonly TextBlock _sheetInfo = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Margin = new Thickness(0, 8, 0, 0) };
        private FrameworkElement _sheetBody;
        private readonly CheckBox _autoMargins = new CheckBox { Content = "Encaixar na área livre do carimbo (moldura e faixa de informações detectadas)", Margin = new Thickness(0, 10, 0, 0) };
        private readonly TextBox _inset = new TextBox { Width = 50 };
        private readonly TextBlock _freeInfo = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };
        private WrapPanel _marginPanel;
        private bool _applyingMargins;

        // ---- dynamic template controls
        private readonly Dictionary<string, CheckBox> _dirs = new Dictionary<string, CheckBox>();
        private readonly TextBlock _dirHint = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Margin = new Thickness(0, 6, 0, 0) };
        private readonly StackPanel _rowsPanel = new StackPanel();
        private TextBox _cardW, _cardH;

        // ---- preview + footer
        private readonly Canvas _preview = new Canvas { Height = 210, ClipToBounds = true };
        private readonly TextBlock _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        private Button _primary;

        // ---- relação (bancadas / granito)
        private readonly TextBox _note = new TextBox();
        private FrameworkElement _fichaHeadCard, _relCard;
        private readonly WrapPanel _defaultDetails = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        private readonly StackPanel _perType = new StackPanel();
        private readonly CheckBox _allAnnotations = new CheckBox { Content = "Mostrar todas as anotações genéricas (não só as famílias “Det…”)", Margin = new Thickness(0, 8, 0, 0) };
        private TabItem _detailsTab;
        private TabControl _tabs;
        private string _perTypeSig;
        /// <summary>Typical details chosen per type (UniqueId → "Família : Tipo" labels; null = the template's).</summary>
        private readonly Dictionary<string, List<string>> _typeDetails = new Dictionary<string, List<string>>();
        /// <summary>Loads .rfa detail families into the project (set by the command; returns an error text or null).</summary>
        public Func<IList<string>, string> LoadDetailFamilies { get; set; }

        private static readonly string[] SortChoices = { "Marca de tipo (P01, P02… J01…)", "Família e tipo", "Largura (menor → maior)" };
        private readonly ComboBox _sort = new ComboBox { MinWidth = 200 };

        public LegendBoardWindow(LegendProbe probe, ICollection<DB.ElementId> preselected,
                                 Func<CategoryInfo, Action<int, int, string>, string> loadCategory,
                                 Func<Action<int, int, string>, string> measureTitleBlocks,
                                 IList<string> preselectedCategories)
        {
            _probe = probe;
            _preselectedCategories = preselectedCategories ?? new List<string>();
            _loadCategory = loadCategory;
            _measureTitleBlocks = measureTitleBlocks;
            _preselected = new HashSet<DB.ElementId>(preselected ?? new List<DB.ElementId>());

            _templates = LegendTemplate.LoadAll(out string last);
            var start = _templates.FirstOrDefault(x => x.Name == last) ?? _templates[0];
            _t = start.Clone();
            _t.Name = start.Name;

            Width = 1160; Height = 800; MinWidth = 940; MinHeight = 600;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            LoadPrefs(preselected);
            var body = BuildBody();
            var footer = BuildFooter();
            ToolChrome.DressCompact(this, "Gerar Detalhes", body, footer, "Uma ficha por tipo (portas, janelas, gabinetes ou qualquer família), com tabela, cotas e pranchas.");

            foreach (var t in _templates) _templateCombo.Items.Add(TemplateLabel(t));
            _templateCombo.SelectedIndex = _templates.IndexOf(start);
            _templateCombo.SelectionChanged += (s, e) => SwitchTemplate();

            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { _search.Focus(); e.Handled = true; }
            };
            Loaded += (s, e) =>
            {
                // the chosen phase applies before any category is counted
                if (SetPhase != null) { try { SetPhase(SelectedPhase()); } catch { } }
                LoadTemplateIntoControls();
                RebuildList();
                Refresh();
                // categories used last time are loaded right after the window shows (with progress)
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    foreach (var kv in _catPills.Where(x => _initialCats.Contains(x.Key.Name)).ToList()) kv.Value.IsChecked = true;
                    if (_sheetsOn.IsChecked == true) EnsureTitleBlocks();
                }), System.Windows.Threading.DispatcherPriority.ContextIdle);
            };
            _preview.SizeChanged += (s, e) => DrawPreview();
        }

        private static string TemplateLabel(LegendTemplate t) => (t.BuiltIn ? "" : "★ ") + t.Name;

        // ================================================================== layout
        private UIElement BuildBody()
        {
            var grid = new System.Windows.Controls.Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.9, GridUnitType.Star), MinWidth = 320 });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star), MinWidth = 520 });

            grid.Children.Add(BuildTypesCard());

            var right = new DockPanel();
            right.Children.Add(Top(BuildTemplateBar()));

            var previewCard = ToolChrome.Card(_preview, "Prévia");
            previewCard.Margin = new Thickness(0, 4, 0, 10);
            previewCard.Padding = new Thickness(14, 10, 14, 10);
            DockPanel.SetDock(previewCard, Dock.Bottom);
            right.Children.Add(previewCard);

            var tabs = new TabControl();
            tabs.Items.Add(new TabItem { Header = "Vistas", Content = Scroll(BuildViewsTab()) });
            tabs.Items.Add(new TabItem { Header = "Layout", Content = Scroll(BuildLayoutTab()) });
            var texts = new StackPanel();
            texts.Children.Add(BuildTextsTab());
            texts.Children.Add(BuildTableTab());
            tabs.Items.Add(new TabItem { Header = "Textos e tabela", Content = Scroll(texts) });
            _detailsTab = new TabItem { Header = "Detalhes típicos", Content = Scroll(BuildDetailsTab()) };
            tabs.Items.Add(_detailsTab);
            tabs.Items.Add(new TabItem { Header = "Pranchas", Content = Scroll(BuildSheetsTab()) });
            tabs.SelectionChanged += (s, e) => { if (ReferenceEquals(e.OriginalSource, tabs) && _detailsTab.IsSelected) RebuildPerType(); };
            _tabs = tabs;
            right.Children.Add(tabs);
            System.Windows.Controls.Grid.SetColumn(right, 2);
            grid.Children.Add(right);
            return grid;
        }

        private static UIElement Top(UIElement e) { DockPanel.SetDock(e, Dock.Top); return e; }
        private static ScrollViewer Scroll(UIElement e) => new ScrollViewer { Content = e, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        private FrameworkElement BuildTemplateBar()
        {
            var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var save = ToolChrome.Ghost("Salvar como…", "Guarda as opções atuais como um modelo seu (reutilizável em outros projetos)", (s, e) => SaveTemplateAs());
            var more = ToolChrome.Ghost("⋯", "Mais ações do modelo");
            more.FontWeight = FontWeights.Bold;
            var menu = new ContextMenu();
            var miReset = new MenuItem { Header = "Desfazer alterações", ToolTip = "Volta ao modelo como foi salvo" };
            miReset.Click += (s, e) => SwitchTemplate();
            var miDel = new MenuItem { Header = "Excluir este modelo", ToolTip = "Só modelos seus (★)" };
            miDel.Click += (s, e) => DeleteTemplate();
            menu.Items.Add(miReset);
            menu.Items.Add(miDel);
            more.ContextMenu = menu;
            more.Click += (s, e) => { menu.PlacementTarget = more; menu.IsOpen = true; };
            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(save);
            right.Children.Add(more);
            DockPanel.SetDock(right, Dock.Right);
            bar.Children.Add(right);
            var lab = new TextBlock { Text = "Modelo", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            lab.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Muted");
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            left.Children.Add(lab);
            left.Children.Add(_templateCombo);
            _templateState.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Muted");
            left.Children.Add(_templateState);
            bar.Children.Add(left);
            return bar;
        }

        private FrameworkElement BuildTypesCard()
        {
            var dock = new DockPanel();

            // 1. categories: only the ones the user picks are read (fast opening)
            var catHead = ToolChrome.Muted("Categorias", 11);
            catHead.ToolTip = "Só as categorias marcadas são lidas do modelo (abre mais rápido).";
            catHead.Margin = new Thickness(0, 0, 0, 6);
            DockPanel.SetDock(catHead, Dock.Top);
            dock.Children.Add(catHead);
            var pills = new WrapPanel();
            foreach (var cat in _probe.Categories)
            {
                var cb = new CheckBox { Content = $"{cat.Name}  {cat.TypeCount}", ToolTip = $"{cat.TypeCount} tipo(s) carregado(s) no projeto", FontSize = 11.5 };
                cb.SetResourceReference(StyleProperty, "Ja.Toggle");
                cb.Margin = new Thickness(0, 0, 4, 4);
                var c = cat;
                cb.Checked += (s, e) => OnCategoryToggled(c, true);
                cb.Unchecked += (s, e) => OnCategoryToggled(c, false);
                _catPills[cat] = cb;
                pills.Children.Add(cb);
            }
            var pillScroll = new ScrollViewer { Content = pills, MaxHeight = 104, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(pillScroll, Dock.Top);
            dock.Children.Add(pillScroll);

            // progress of the category being loaded
            _busy.SetResourceReference(Border.BackgroundProperty, "Ja.AccentSoft");
            _busyText.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Ink");
            _busyBar.SetResourceReference(Control.ForegroundProperty, "Ja.Accent");
            var busyStack = new StackPanel();
            busyStack.Children.Add(_busyText);
            busyStack.Children.Add(_busyBar);
            _busy.Child = busyStack;
            DockPanel.SetDock(_busy, Dock.Top);
            dock.Children.Add(_busy);

            var filters = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            filters.Children.Add(ToolChrome.SearchBox(_search, "Buscar família, tipo ou marca…"));
            _search.TextChanged += (s, e) => ApplyFilter();
            _search.ToolTip = "Digite parte do nome, da família ou da marca (Ctrl+F)";
            DockPanel.SetDock(filters, Dock.Top);
            dock.Children.Add(filters);

            _onlyUsed.Checked += (s, e) => ApplyFilter();
            _onlyUsed.Unchecked += (s, e) => ApplyFilter();
            _onlyUsed.ToolTip = "Esconde tipos carregados mas sem nenhuma instância no modelo.";
            _onlyUsed.Content = "Só usados";
            _onlyUsed.FontSize = 11.5;
            _onlyUsed.SetResourceReference(StyleProperty, "Ja.Toggle");
            _onlyUsed.Margin = new Thickness(0, 0, 8, 0);
            _onlyUsed.VerticalAlignment = VerticalAlignment.Center;

            // phase the elements are taken from (quantities, rooms, instance drawn from the model)
            _phase.Items.Add("Todas as fases");
            foreach (var ph in _probe.Phases) _phase.Items.Add(ph.Value);
            int pi = _initialPhase == null ? 0 : _probe.Phases.FindIndex(x => x.Value == _initialPhase) + 1;
            _phase.SelectedIndex = Math.Max(0, pi);
            _phase.ToolTip = "Só os elementos que existem nessa fase (novos ou existentes) entram: quantidades, ambientes e a instância desenhada do modelo. Demolidos e futuros ficam de fora.";
            _phase.SelectionChanged += (s, e) =>
            {
                if (SetPhase == null) return;
                RunBusy(report => { report(0, 1, "Contando os elementos da fase…"); return SetPhase(SelectedPhase()); });
                RebuildList();
                ApplyFilter();
                Refresh();
            };
            var phaseRow = new DockPanel { Margin = new Thickness(0, 4, 0, 6) };
            DockPanel.SetDock(_onlyUsed, Dock.Left);
            phaseRow.Children.Add(_onlyUsed);
            var phaseLabel = new TextBlock { Text = "Fase", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            phaseLabel.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Muted");
            DockPanel.SetDock(phaseLabel, Dock.Left);
            phaseRow.Children.Add(phaseLabel);
            _phase.MinWidth = 140;
            phaseRow.Children.Add(_phase);
            DockPanel.SetDock(phaseRow, Dock.Top);
            dock.Children.Add(phaseRow);

            var bottom = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var hint = ToolChrome.Muted("Shift+clique: intervalo", 11);
            hint.VerticalAlignment = VerticalAlignment.Center;
            hint.ToolTip = "Shift+clique marca/desmarca um intervalo inteiro; Ctrl+clique alterna um item.";
            DockPanel.SetDock(hint, Dock.Right);
            bottom.Children.Add(hint);
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            var all = ToolChrome.Ghost("Marcar visíveis", "Marca todos os tipos que aparecem na lista", (s, e) => SetVisible(true));
            all.Margin = new Thickness(0, 0, 4, 0);
            actions.Children.Add(all);
            actions.Children.Add(ToolChrome.Ghost("Limpar", "Desmarca tudo", (s, e) => { foreach (var c in _items) c.IsSelected = false; SyncBoxes(); }));
            bottom.Children.Add(actions);
            DockPanel.SetDock(bottom, Dock.Bottom);
            dock.Children.Add(bottom);
            if (!_probe.HasLegendSource)
            {
                // only worth a line when it changes the result
                var legendNote = ToolChrome.Muted("Sem componente de legenda no projeto: todos os tipos serão desenhados com vistas do modelo.", 11);
                legendNote.Margin = new Thickness(0, 6, 0, 0);
                legendNote.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Warn");
                DockPanel.SetDock(legendNote, Dock.Bottom);
                dock.Children.Add(legendNote);
            }

            _empty.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Muted");
            var frame = new Border
            {
                BorderThickness = new Thickness(0, 1, 0, 0),
                Child = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                ToolTip = _probe.HasLegendSource ? "Categorias que o Revit não aceita em legendas são desenhadas com vistas do modelo (cortes em volta de uma instância)." : null
            };
            frame.SetResourceReference(Border.BorderBrushProperty, "Ja.Line");
            dock.Children.Add(frame);

            var card = ToolChrome.Card(dock, "Tipos", ToolChrome.Badge(out _badge));
            card.Margin = new Thickness(0, 0, 0, 10);
            return card;
        }

        /// <summary>Runs a Revit job with the inline progress box (the window stays responsive).</summary>
        private string RunBusy(Func<Action<int, int, string>, string> job)
        {
            _working = true;
            _busy.Visibility = Visibility.Visible;
            _primary.IsEnabled = false;
            Cursor = Cursors.Wait;
            try
            {
                return job((cur, tot, msg) =>
                {
                    _busyText.Text = msg;
                    _busyBar.Maximum = Math.Max(1, tot);
                    _busyBar.Value = Math.Min(cur, tot);
                    Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() => { }));
                });
            }
            finally
            {
                _working = false;
                _busy.Visibility = Visibility.Collapsed;
                Cursor = null;
                Refresh();
            }
        }

        private DB.ElementId SelectedPhase() =>
            _phase.SelectedIndex > 0 && _phase.SelectedIndex - 1 < _probe.Phases.Count ? _probe.Phases[_phase.SelectedIndex - 1].Key : DB.ElementId.InvalidElementId;

        private void OnCategoryToggled(CategoryInfo cat, bool on)
        {
            if (on && !cat.Loaded)
            {
                string error = RunBusy(report => _loadCategory(cat, report));
                if (error != null)
                {
                    _status.Text = $"{cat.Name}: não foi possível carregar ({error}).";
                    _catPills[cat].IsChecked = false;
                    return;
                }
                // preselection: types of what was selected in Revit, else doors and windows in use
                var fresh = _probe.Candidates.Where(c => c.Category == cat.Name).ToList();
                bool any = false;
                foreach (var c in fresh) if (_preselected.Contains(c.Id) && c.Available) { c.IsSelected = true; any = true; }
                if (!any && _preselected.Count == 0 && (cat.Id == (long)DB.BuiltInCategory.OST_Doors || cat.Id == (long)DB.BuiltInCategory.OST_Windows))
                    foreach (var c in fresh) if (c.Count > 0 && c.Available) c.IsSelected = true;
                RebuildRows();
            }
            if (!on) foreach (var c in _probe.Candidates.Where(x => x.Category == cat.Name)) c.IsSelected = false;
            RebuildList();
            Refresh();
        }

        /// <summary>The list shows the types of the checked categories.</summary>
        private void RebuildList()
        {
            var shown = new HashSet<string>(_catPills.Where(kv => kv.Value.IsChecked == true && kv.Key.Loaded).Select(kv => kv.Key.Name));
            _items = _probe.Candidates.Where(c => shown.Contains(c.Category))
                .OrderBy(c => c.Category, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(c => c.Family, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(c => c.TypeName, new NaturalStringComparer()).ToList();
            _list.Children.Clear();
            _boxes.Clear();
            _groupHeads.Clear();
            string lastGroup = null;
            foreach (var c in _items)
            {
                c.Changed = Refresh;
                if (c.Category != lastGroup)
                {
                    lastGroup = c.Category;
                    bool model = c.ModelMode;
                    var h = new TextBlock { Text = c.Category + (model ? "  ·  vistas do modelo" : "  ·  legenda"), FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 3) };
                    h.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Muted");
                    _groupHeads[c.Category] = h;
                    _list.Children.Add(h);
                }
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                var title = new TextBlock { Text = c.Title };
                title.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Text");
                var detail = new TextBlock { Text = c.Detail, Margin = new Thickness(8, 0, 0, 0), FontSize = 11 };
                detail.SetResourceReference(TextBlock.ForegroundProperty, c.Available ? "Ja.Muted" : "Ja.Warn");
                row.Children.Add(title);
                row.Children.Add(detail);
                var cb = new CheckBox { Content = row, IsChecked = c.IsSelected, Margin = new Thickness(0, 2, 0, 2), IsEnabled = c.Available };
                if (!c.Available) cb.ToolTip = "Esta categoria é desenhada a partir do modelo e o tipo não tem nenhuma instância colocada.";
                var item = c;
                cb.Checked += (s, e) => item.IsSelected = true;
                cb.Unchecked += (s, e) => item.IsSelected = false;
                cb.Click += (s, e) => RangeClick(cb);
                _boxes[c] = cb;
                _list.Children.Add(cb);
            }
            _list.Children.Add(_empty);
            ApplyFilter();
        }

        /// <summary>Shift+click: the whole range since the previous click takes the state of the clicked box.
        /// Ctrl+click (or a plain click) marks/unmarks only that item.</summary>
        private void RangeClick(CheckBox cb)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 && _lastClicked != null && _lastClicked != cb)
            {
                int a = _list.Children.IndexOf(_lastClicked), b = _list.Children.IndexOf(cb);
                if (a >= 0 && b >= 0)
                {
                    bool state = cb.IsChecked == true;
                    for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++)
                        if (_list.Children[i] is CheckBox x && x.Visibility == Visibility.Visible && x.IsEnabled) x.IsChecked = state;
                    Refresh();
                }
            }
            _lastClicked = cb;
        }

        private void ApplyFilter()
        {
            string f = _search.Text.Trim();
            bool used = _onlyUsed.IsChecked == true;
            int shown = 0;
            var visibleGroups = new HashSet<string>();
            foreach (var kv in _boxes)
            {
                var c = kv.Key;
                bool show = (!used || c.Count > 0 || c.IsSelected) &&
                            (f.Length == 0 || c.Title.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0 || c.Category.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0);
                kv.Value.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                if (show) { shown++; visibleGroups.Add(c.Category); }
            }
            foreach (var kv in _groupHeads) kv.Value.Visibility = visibleGroups.Contains(kv.Key) ? Visibility.Visible : Visibility.Collapsed;
            _empty.Text = _items.Count == 0 ? "Marque uma ou mais categorias acima para ver os tipos." : $"Nada encontrado para “{f}”.";
            _empty.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetVisible(bool state)
        {
            foreach (var kv in _boxes) if (kv.Value.Visibility == Visibility.Visible && kv.Value.IsEnabled) kv.Key.IsSelected = state;
            SyncBoxes();
        }

        private void SyncBoxes()
        {
            foreach (var kv in _boxes) kv.Value.IsChecked = kv.Key.IsSelected;
            Refresh();
        }

        /// <summary>Title block sizes are measured the first time sheets are used.</summary>
        private void EnsureTitleBlocks()
        {
            if (_probe.TitleBlocksMeasured) return;
            string error = RunBusy(report => _measureTitleBlocks(report));
            if (error != null) _status.Text = "Carimbos: " + error;
            _titleBlock.Items.Clear();
            foreach (var tb in _probe.TitleBlocks) _titleBlock.Items.Add(tb.Name);
            int tbi = _probe.TitleBlocks.FindIndex(x => x.Name == _initialTitleBlock);
            _titleBlock.SelectedIndex = tbi >= 0 ? tbi : (_probe.TitleBlocks.Count > 0 ? 0 : -1);
            if (_probe.TitleBlocks.Count == 0) { _sheetsOn.IsChecked = false; _sheetInfo.Text = "O projeto não tem carimbos carregados."; }
            Refresh();
        }

        // ---------------------------------------------------------------- Vistas
        private UIElement BuildViewsTab()
        {
            var stack = new StackPanel();
            var pills = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            foreach (string key in new[] { LegendEngine.Front, LegendEngine.Left, LegendEngine.Right, LegendEngine.Back, LegendEngine.Plan, LegendEngine.Section })
            {
                var cb = new CheckBox { Content = LegendEngine.DirectionLabel(key), ToolTip = DirTip(key) };
                cb.SetResourceReference(StyleProperty, "Ja.Toggle");
                string k = key;
                cb.Checked += (s, e) => { if (_loading) return; if (!_t.Views.Contains(k)) _t.Views.Add(k); Changed(); };
                cb.Unchecked += (s, e) => { if (_loading) return; _t.Views.Remove(k); Changed(); };
                _dirs[key] = cb;
                pills.Children.Add(cb);
                _loaders.Add(() => cb.IsChecked = _t.Views.Contains(k));
            }
            var inner = new StackPanel();
            pills.Margin = new Thickness(0);
            pills.ToolTip = "As elevações ficam lado a lado sobre a linha de piso; a planta logo abaixo, alinhada com a vista frontal.";
            inner.Children.Add(pills);
            inner.Children.Add(_dirHint);
            stack.Children.Add(ToolChrome.Card(inner, "Vistas de cada tipo"));

            var rep = new StackPanel();
            var detail = new WrapPanel();
            string[] names = { "Grosso", "Médio", "Fino" };
            for (int i = 0; i < 3; i++)
            {
                int level = i + 1;
                var rb = new RadioButton { Content = names[i], GroupName = "legendDetail" };
                rb.SetResourceReference(StyleProperty, "Ja.Segment");
                rb.Checked += (s, e) => { if (_loading) return; _t.DetailLevel = level; Changed(); };
                _loaders.Add(() => rb.IsChecked = _t.DetailLevel == level);
                detail.Children.Add(rb);
            }
            rep.Children.Add(Row("Nível de detalhe", ToolChrome.Track(detail), "Como no Revit: Fino mostra batentes, folhas e ferragens com todo o detalhe da família."));
            rep.Children.Add(Row("Desenhar", Check("Sempre de vistas do modelo (cortes em volta de uma instância)",
                () => _t.ForceModel, v => _t.ForceModel = v), "Útil para mostrar o objeto real como está no projeto (materiais, ferragens, parâmetros de instância)."));
            rep.Children.Add(Row("Linha de piso", Check("Desenhar o piso sob as elevações (janela na altura do peitoril)", () => _t.FloorLine, v => _t.FloorLine = v), null));
            rep.Children.Add(Row("Espaço entre vistas", Num(() => _t.ViewGapMm, v => _t.ViewGapMm = v, 2, 100, "mm"), "Distância entre as vistas, medida na prancha impressa."));
            rep.Children.Add(Row("Paredes, pisos, forros", Num(() => _t.HostLengthM, v => _t.HostLengthM = v, 0.1, 50, "m de comprimento"), "Tamanho do trecho mostrado para tipos de sistema."));
            var modes = new WrapPanel();
            string[] modeNames = { "Fixa", "Reduzir se não couber", "Maior que caiba na ficha" };
            string[] modeTips =
            {
                "Sempre a escala escolhida abaixo.",
                "Usa a escala abaixo; se os desenhos não couberem, passa para a próxima usual (1:25 → 1:50…).",
                "Escolhe a maior escala usual (1:5, 1:10, 1:20, 1:25, 1:50…) em que os desenhos cabem na ficha."
            };
            for (int i = 0; i < 3; i++)
            {
                int mode = i;
                var rb = new RadioButton { Content = modeNames[i], GroupName = "legendScaleMode", ToolTip = modeTips[i] };
                rb.SetResourceReference(StyleProperty, "Ja.Segment");
                rb.Checked += (s, e) => { if (_loading) return; _t.ScaleMode = mode; Changed(); };
                _loaders.Add(() => rb.IsChecked = _t.ScaleMode == mode);
                modes.Children.Add(rb);
            }
            rep.Children.Add(Row("Escala", ToolChrome.Track(modes), "Como escolher a escala de cada legenda."));
            var scale = new StackPanel { Orientation = Orientation.Horizontal };
            scale.Children.Add(Unit("1 :", new Thickness(0, 0, 6, 0)));
            scale.Children.Add(Num(() => _t.Scale, v => _t.Scale = (int)Math.Round(v), 1, 5000, null));
            rep.Children.Add(Row("Escala base", scale, "Usada nos modos Fixa e Reduzir (e quando a ficha não tem tamanho fixo)."));
            rep.Children.Add(Row("", Check("Mesma escala em todas as fichas (a que serve para todas)", () => _t.SameScale, v => _t.SameScale = v),
                "Deixa o caderno uniforme: todas as fichas na mesma escala."));
            stack.Children.Add(ToolChrome.Card(rep, "Representação"));
            return stack;
        }

        // ---------------------------------------------------------------- Layout
        private UIElement BuildLayoutTab()
        {
            var stack = new StackPanel();

            var style = new StackPanel();
            var seg = new WrapPanel();
            var rbFicha = new RadioButton { Content = "Ficha (marca e tabela)", GroupName = "legendStyle" };
            var rbRel = new RadioButton { Content = "Relação (tabela em cima)", GroupName = "legendStyle" };
            foreach (var rb in new[] { rbFicha, rbRel }) { rb.SetResourceReference(StyleProperty, "Ja.Segment"); seg.Children.Add(rb); }
            rbFicha.Checked += (s, e) => { if (_loading) return; _t.Layout = 0; UpdateStyleVisibility(); Changed(); };
            rbRel.Checked += (s, e) =>
            {
                if (_loading) return;
                _t.Layout = 1;
                // a relação usually stacks three cards per sheet, with the note row
                if (_sRows.Text.Trim() == "2" && _sCols.Text.Trim() == "1") _sRows.Text = "3";
                if (string.IsNullOrWhiteSpace(_note.Text)) _note.Text = "OBS.: CONFERIR MEDIDAS NO LOCAL; COTAS EM METRO.";
                UpdateStyleVisibility();
                Changed();
            };
            _loaders.Add(() => { rbFicha.IsChecked = _t.Layout != 1; rbRel.IsChecked = _t.Layout == 1; UpdateStyleVisibility(); });
            rbFicha.ToolTip = "Marca em coluna (ou acima) e tabela embaixo.";
            rbRel.ToolTip = "Tabela em cima (especificação, compartimento, quantidade), desenho cotado à esquerda, detalhes típicos à direita e total embaixo — para relações de peças (bancadas, granitos, soleiras, peitoris, louças, mobiliário…).";
            style.Children.Add(ToolChrome.Track(seg));
            stack.Children.Add(ToolChrome.Card(style, "Estilo"));

            stack.Children.Add(_relCard = BuildRelacaoCard());

            var mark = new StackPanel();
            var pos = new WrapPanel();
            var left = new RadioButton { Content = "À esquerda, em coluna", GroupName = "legendMarkPos" };
            var top = new RadioButton { Content = "Acima dos desenhos", GroupName = "legendMarkPos" };
            foreach (var rb in new[] { left, top }) { rb.SetResourceReference(StyleProperty, "Ja.Segment"); pos.Children.Add(rb); }
            left.Checked += (s, e) => { if (_loading) return; _t.MarkLeft = true; Changed(); };
            top.Checked += (s, e) => { if (_loading) return; _t.MarkLeft = false; Changed(); };
            _loaders.Add(() => { left.IsChecked = _t.MarkLeft; top.IsChecked = !_t.MarkLeft; });
            mark.Children.Add(Row("Marca e medidas", ToolChrome.Track(pos), "Onde ficam a marca (PA01) e as medidas (1.60 m x 2.20 m)."));
            mark.Children.Add(Row("Largura da coluna", Num(() => _t.LeftColumnMm, v => _t.LeftColumnMm = v, 15, 200, "mm"), "Só quando a marca fica à esquerda."));
            stack.Children.Add(_fichaHeadCard = ToolChrome.Card(mark, "Cabeçalho"));

            var card = new StackPanel();
            card.Children.Add(Check("Moldura em volta da ficha", () => _t.Frame, v => _t.Frame = v));
            card.Children.Add(Check("Linha divisória entre a coluna da marca e os desenhos", () => _t.Divider, v => _t.Divider = v));
            card.Children.Add(Check("Tamanho fixo (todas as fichas iguais — encaixam lado a lado na prancha)", () => _t.FixedSize, v => _t.FixedSize = v));
            var size = new StackPanel { Orientation = Orientation.Horizontal };
            _cardW = (TextBox)NumBox(() => _t.WidthMm, v => _t.WidthMm = v, 40, 2000);
            _cardH = (TextBox)NumBox(() => _t.HeightMm, v => _t.HeightMm = v, 40, 2000);
            size.Children.Add(_cardW);
            size.Children.Add(Unit("×", new Thickness(6, 0, 6, 0)));
            size.Children.Add(_cardH);
            size.Children.Add(Unit("mm (largura × altura)"));
            card.Children.Add(Row("Tamanho da ficha", size, "Com “Ajustar à prancha” (aba Pranchas) o tamanho é calculado pelo carimbo."));
            card.Children.Add(Row("Margem interna", Num(() => _t.PaddingMm, v => _t.PaddingMm = v, 0, 50, "mm"), null));
            stack.Children.Add(ToolChrome.Card(card, "Ficha"));

            var naming = new StackPanel();
            var patternStack = new StackPanel();
            patternStack.Children.Add(_pattern);
            var chips = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            foreach (string token in new[] { "{marca}", "{tipo}", "{familia}", "{categoria}" })
            {
                var chip = ToolChrome.Ghost(token, "Inserir " + token);
                chip.FontSize = 11; chip.Padding = new Thickness(7, 1, 7, 2); chip.Margin = new Thickness(0, 0, 4, 4); chip.Focusable = false;
                chip.SetResourceReference(Control.BackgroundProperty, "Ja.Field");
                string tk = token;
                chip.Click += (s, e) =>
                {
                    int at = _pattern.CaretIndex > 0 || _pattern.IsKeyboardFocusWithin ? _pattern.CaretIndex : _pattern.Text.Length;
                    _pattern.Text = _pattern.Text.Insert(at, tk);
                    _pattern.Focus();
                    _pattern.CaretIndex = at + tk.Length;
                };
                chips.Children.Add(chip);
            }
            patternStack.Children.Add(chips);
            naming.Children.Add(Row("Nome de cada legenda", patternStack, "Uma legenda por tipo; as variáveis são trocadas pelos dados do tipo. Nomes repetidos são numerados."));
            foreach (string s in SortChoices) _sort.Items.Add(s);
            _sort.SelectedIndex = _initialSort;
            naming.Children.Add(Row("Ordem", _sort, "Ordem de criação (e de colocação nas pranchas)."));
            stack.Children.Add(ToolChrome.Card(naming, "Nomes e ordem"));
            _pattern.TextChanged += (s, e) => Refresh();
            return stack;
        }

        private FrameworkElement BuildRelacaoCard()
        {
            var rel = new StackPanel();
            var heads = new System.Windows.Controls.Grid();
            for (int i = 0; i < 3; i++) heads.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var h1 = new TextBox { Margin = new Thickness(0, 0, 4, 0) }; Bind(h1, () => _t.HeadSpec, v => _t.HeadSpec = v);
            var h2 = new TextBox { Margin = new Thickness(0, 0, 4, 0) }; Bind(h2, () => _t.HeadComp, v => _t.HeadComp = v);
            var h3 = new TextBox(); Bind(h3, () => _t.HeadQty, v => _t.HeadQty = v);
            System.Windows.Controls.Grid.SetColumn(h2, 1); System.Windows.Controls.Grid.SetColumn(h3, 2);
            heads.Children.Add(h1); heads.Children.Add(h2); heads.Children.Add(h3);
            rel.Children.Add(Row("Títulos das colunas", heads, "Textos do cabeçalho da tabela."));
            rel.Children.Add(Row("Especificação", SourceCombo(() => _t.SpecSource, v => _t.SpecSource = v),
                "Parâmetro do tipo (ou da instância) com o texto, ex.: Descrição (GRANITO A SER DEFINIDO PELA CONSTRUTORA), Comentários do tipo, Material."));
            rel.Children.Add(Row("Compartimento", SourceCombo(() => _t.CompSource, v => _t.CompSource = v),
                "[Localização: ambientes] = ambientes onde as peças estão (portas: só o ambiente do lado interno, para onde a porta abre). Ou um parâmetro de instância com o texto do compartimento (ex.: BLOCO 1 - APTOs (COL. 1) BANH. SOCIAL)."));
            rel.Children.Add(Row("", Check("Uma linha por compartimento, cada uma com a sua quantidade (como em PE01, SL01)", () => _t.CompPerRoom, v => _t.CompPerRoom = v), null));
            var units = new StackPanel { Orientation = Orientation.Horizontal };
            var u1 = new TextBox { Width = 110 }; Bind(u1, () => _t.UnitName, v => _t.UnitName = v);
            var u2 = new TextBox { Width = 110 }; Bind(u2, () => _t.UnitPlural, v => _t.UnitPlural = v);
            units.Children.Add(u1); units.Children.Add(Unit("singular", new Thickness(6, 0, 14, 0)));
            units.Children.Add(u2); units.Children.Add(Unit("plural"));
            rel.Children.Add(Row("Unidade", units, "Ex.: BANCA / BANCAS, TENTO / TENTOS, UNIDADE / UNIDADES."));
            var floors = new StackPanel { Orientation = Orientation.Horizontal };
            floors.Children.Add(Check("Por pavimento:", () => _t.QtyByFloor, v => _t.QtyByFloor = v));
            var fw = new TextBox { Width = 60, Margin = new Thickness(6, 0, 0, 0) }; Bind(fw, () => _t.FloorWord, v => _t.FloorWord = v);
            floors.Children.Add(fw);
            floors.Children.Add(Unit("→ 1 x 6 PAV. = 6 BANCAS (quando todos os pavimentos têm o mesmo número)"));
            rel.Children.Add(Row("Quantidade", floors, "Conta as peças de cada pavimento (nível). Se forem diferentes, mostra só o total."));
            rel.Children.Add(Row("", Check("Linha de total no pé da ficha (TOTAL: 6 BANCAS)", () => _t.ShowTotal, v => _t.ShowTotal = v), null));
            var cols = new WrapPanel();
            cols.Children.Add(Unit("marca", new Thickness(0, 0, 4, 0))); cols.Children.Add(NumBox(() => _t.MarkColMm, v => _t.MarkColMm = v, 8, 80)); cols.Children.Add(Unit("mm", new Thickness(4, 0, 12, 0)));
            cols.Children.Add(Unit("especificação", new Thickness(0, 0, 4, 0))); cols.Children.Add(NumBox(() => _t.SpecColPct, v => _t.SpecColPct = v, 10, 80)); cols.Children.Add(Unit("%", new Thickness(4, 0, 12, 0)));
            cols.Children.Add(Unit("compartimento", new Thickness(0, 0, 4, 0))); cols.Children.Add(NumBox(() => _t.CompColPct, v => _t.CompColPct = v, 10, 80)); cols.Children.Add(Unit("%"));
            rel.Children.Add(Row("Colunas", cols, "A quantidade fica com o resto. A coluna da marca aparece com “Marca de tipo” ligada (aba Textos e tabela)."));
            rel.Children.Add(Row("Altura do cabeçalho", Num(() => _t.HeadRowMm, v => _t.HeadRowMm = v, 3, 30, "mm"), null));
            rel.Children.Add(Row("Detalhes típicos", Num(() => _t.DetailsPct, v => _t.DetailsPct = v, 0, 80, "% da largura, à direita"), "Espaço reservado aos detalhes típicos (aba Detalhes típicos)."));
            return ToolChrome.Card(rel, "Relação");
        }

        private void UpdateStyleVisibility()
        {
            bool rel = _t != null && _t.Layout == 1;
            if (_relCard != null) _relCard.Visibility = rel ? Visibility.Visible : Visibility.Collapsed;
            if (_fichaHeadCard != null) _fichaHeadCard.Visibility = rel ? Visibility.Collapsed : Visibility.Visible;
            // typical details are drawn only in the Relação style: the tab shows only then
            if (_detailsTab != null)
            {
                _detailsTab.Visibility = rel ? Visibility.Visible : Visibility.Collapsed;
                if (!rel && _detailsTab.IsSelected && _tabs != null) _tabs.SelectedIndex = 0;
            }
        }

        /// <summary>Editable combo of calculated values and parameter names.</summary>
        private ComboBox SourceCombo(Func<string> get, Action<string> set)
        {
            var c = new ComboBox { IsEditable = true, MinWidth = 260, ToolTip = "Escolha um dado calculado ou digite/escolha o nome de um parâmetro" };
            Func<List<string>> items = () => LegendSources.All.Concat(_probe.ParameterNames).ToList();
            c.ItemsSource = items();
            c.DropDownOpened += (s, e) => { string keep = c.Text; c.ItemsSource = items(); c.Text = keep; };
            c.SelectionChanged += (s, e) => { if (_loading) return; if (c.SelectedItem is string v) { set(v); Changed(); } };
            c.LostKeyboardFocus += (s, e) => { if (_loading) return; if (get() != c.Text) { set(c.Text); Changed(); } };
            _loaders.Add(() => c.Text = get() ?? "");
            return c;
        }

        // ---------------------------------------------------------------- Detalhes típicos
        private UIElement BuildDetailsTab()
        {
            var stack = new StackPanel();
            var def = new StackPanel();
            def.Children.Add(ToolChrome.Muted("Anotações genéricas à direita do desenho (Det.Frontispício, Det.Saia…). Valem para os tipos sem escolha própria.", 11));
            def.Children.Add(_defaultDetails);
            var load = ToolChrome.Secondary("Carregar famílias (.rfa)…", (s, e) => OnLoadFamilies(), "Carrega no projeto as famílias de detalhe típico escolhidas (as do escritório ficam na pasta Familias instalada com o JA Tools)");
            load.HorizontalAlignment = HorizontalAlignment.Left;
            load.Margin = new Thickness(0, 10, 0, 0);
            load.Padding = new Thickness(10, 4, 10, 4);
            def.Children.Add(load);
            def.Children.Add(_allAnnotations);
            _allAnnotations.Checked += (s, e) => RebuildDetailPills();
            _allAnnotations.Unchecked += (s, e) => RebuildDetailPills();
            _loaders.Add(RebuildDetailPills);
            stack.Children.Add(ToolChrome.Card(def, "Detalhes típicos"));

            var per = new StackPanel();
            per.Children.Add(ToolChrome.Muted("Cada tipo pode ter os seus; a escolha fica guardada por tipo.", 11));
            per.Children.Add(_perType);
            stack.Children.Add(ToolChrome.Card(per, "Por tipo"));
            return stack;
        }

        private List<KeyValuePair<DB.ElementId, string>> VisibleSymbols() =>
            _probe.DetailSymbols.Where(kv => _allAnnotations.IsChecked == true
                                             || kv.Value.StartsWith("Det", StringComparison.OrdinalIgnoreCase)
                                             || TemplateLabels().Contains(kv.Value)).ToList();

        /// <summary>The template's details as exact "Família : Tipo" labels (a family name alone = its first type).</summary>
        private List<string> TemplateLabels() =>
            LegendEngine.ResolveDetails(_probe, _t?.Details).Select(id => _probe.DetailSymbols.First(kv => kv.Key == id).Value).ToList();

        private void RebuildDetailPills()
        {
            _defaultDetails.Children.Clear();
            var syms = VisibleSymbols();
            var chosen = TemplateLabels();
            if (syms.Count == 0)
                _defaultDetails.Children.Add(ToolChrome.Muted("Nenhuma família de detalhe no projeto. Use “Carregar famílias (.rfa)…”.", 11.5));
            foreach (var kv in syms)
            {
                var cb = new CheckBox { Content = kv.Value, IsChecked = chosen.Contains(kv.Value) };
                cb.SetResourceReference(StyleProperty, "Ja.Toggle");
                string label = kv.Value;
                cb.Checked += (s, e) => { var l = TemplateLabels(); if (!l.Contains(label)) l.Add(label); _t.Details = l; Changed(); _perTypeSig = null; RebuildPerType(); };
                cb.Unchecked += (s, e) => { var l = TemplateLabels(); l.Remove(label); _t.Details = l; Changed(); _perTypeSig = null; RebuildPerType(); };
                _defaultDetails.Children.Add(cb);
            }
            _perTypeSig = null;
            RebuildPerType();
        }

        private List<string> OwnDetails(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return null;
            if (!_typeDetails.TryGetValue(uid, out var own))
            {
                string saved = Get("det." + uid);
                own = string.IsNullOrEmpty(saved) ? null : saved.Split('\u001f').Where(x => x.Length > 0).ToList();
                if (saved == "\u0001") own = new List<string>();      // explicitly none
                _typeDetails[uid] = own;
            }
            return own;
        }

        private void RebuildPerType()
        {
            if (_t == null) return;
            var sel = _items.Where(c => c.IsSelected).ToList();
            string sig = string.Join("|", sel.Select(c => c.UniqueId)) + "#" + string.Join("|", _t.Details ?? new List<string>()) +
                         "#" + (_allAnnotations.IsChecked == true) + "#" + _probe.DetailSymbols.Count;
            if (sig == _perTypeSig) return;
            _perTypeSig = sig;
            _perType.Children.Clear();
            if (sel.Count == 0) { _perType.Children.Add(ToolChrome.Muted("Marque tipos na lista à esquerda.", 11.5)); return; }
            var syms = VisibleSymbols();
            foreach (var c in sel.Take(60))
            {
                string uid = c.UniqueId;
                var box = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
                var title = new TextBlock { Text = c.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
                title.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Text");
                box.Children.Add(title);
                List<string> own = OwnDetails(uid);
                var useDefault = new CheckBox { Content = "Usar os detalhes do modelo", IsChecked = own == null, Margin = new Thickness(0, 4, 0, 0) };
                var wrap = new WrapPanel { IsEnabled = own == null ? false : true, Margin = new Thickness(0, 4, 0, 0) };
                List<string> shown = own ?? TemplateLabels();
                foreach (var kv in syms)
                {
                    var cb = new CheckBox { Content = kv.Value, IsChecked = shown.Contains(kv.Value) };
                    cb.SetResourceReference(StyleProperty, "Ja.Toggle");
                    string label = kv.Value;
                    cb.Checked += (s, e) => { var l = OwnDetails(uid) ?? new List<string>(); if (!l.Contains(label)) l.Add(label); _typeDetails[uid] = l; };
                    cb.Unchecked += (s, e) => { var l = OwnDetails(uid) ?? new List<string>(); l.Remove(label); _typeDetails[uid] = l; };
                    wrap.Children.Add(cb);
                }
                useDefault.Checked += (s, e) => { _typeDetails[uid] = null; _perTypeSig = null; RebuildPerType(); };
                useDefault.Unchecked += (s, e) => { _typeDetails[uid] = new List<string>(TemplateLabels()); wrap.IsEnabled = true; };
                box.Children.Add(useDefault);
                box.Children.Add(wrap);
                _perType.Children.Add(box);
            }
            if (sel.Count > 60) _perType.Children.Add(ToolChrome.Muted($"…e mais {sel.Count - 60} tipo(s): usam a escolha guardada ou os do modelo.", 11.5));
        }

        private void OnLoadFamilies()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Famílias do Revit (*.rfa)|*.rfa",
                Multiselect = true,
                Title = "Carregar famílias de detalhe típico"
            };
            // the installer puts the office's detail families (Det.*.rfa) in ...\RevitDwgUnifier\Familias
            foreach (var root in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.CommonApplicationData })
            {
                string dir = System.IO.Path.Combine(Environment.GetFolderPath(root), "RevitDwgUnifier", "Familias");
                if (System.IO.Directory.Exists(dir)) { dlg.InitialDirectory = dir; break; }
            }
            if (dlg.ShowDialog(this) != true || LoadDetailFamilies == null) return;
            string error = RunBusy(report =>
            {
                report(0, 1, "Carregando " + dlg.FileNames.Length + " família(s)…");
                return LoadDetailFamilies(dlg.FileNames);
            });
            RebuildDetailPills();
            _status.Text = error == null ? "Famílias carregadas." : "Famílias: " + error;
            _status.SetResourceReference(TextBlock.ForegroundProperty, error == null ? "Ja.Ok" : "Ja.Warn");
        }

        // ---------------------------------------------------------------- Textos e cotas
        private UIElement BuildTextsTab()
        {
            var stack = new StackPanel();
            var head = new StackPanel();
            head.Children.Add(Check("Marca de tipo (ou nome do tipo, se não houver marca)", () => _t.ShowMark, v => _t.ShowMark = v));
            head.Children.Add(Check("Linha sob a marca", () => _t.MarkUnderline, v => _t.MarkUnderline = v));
            head.Children.Add(Check("Medidas sob a marca", () => _t.ShowSize, v => _t.ShowSize = v));
            var fmt = new TextBox();
            Bind(fmt, () => _t.SizeFormat, v => _t.SizeFormat = v);
            head.Children.Add(Row("Formato das medidas", fmt, "{L} = largura, {A} = altura, {P} = profundidade, em metros com 2 casas. Ex.: “{L} m x {A} m” → 1.60 m x 2.20 m"));
            head.Children.Add(Row("Texto da marca", TypeCombo(_probe.TextTypes, () => _t.MarkTextType, v => _t.MarkTextType = v), "Tipo de texto do Revit (tamanho, fonte, negrito)."));
            head.Children.Add(Row("Texto das medidas", TypeCombo(_probe.TextTypes, () => _t.SizeTextType, v => _t.SizeTextType = v), null));
            head.Children.Add(Row("Texto da tabela", TypeCombo(_probe.TextTypes, () => _t.BodyTextType, v => _t.BodyTextType = v), null));
            stack.Children.Add(ToolChrome.Card(head, "Marca e textos"));

            var dims = new StackPanel();
            dims.Children.Add(Check("Largura (sob a planta, ou sob a elevação se não houver planta)", () => _t.DimWidth, v => _t.DimWidth = v));
            dims.Children.Add(Check("Altura (à direita da elevação frontal)", () => _t.DimHeight, v => _t.DimHeight = v));
            dims.Children.Add(Check("Altura em relação ao piso acabado: peitoril de janelas, base de bancadas e armários (na mesma corrente da altura)", () => _t.DimSill, v => _t.DimSill = v));
            dims.Children.Add(Check("Profundidade também em portas e janelas (bancadas, gabinetes e mobiliário já recebem largura × profundidade na planta)", () => _t.DimDepth, v => _t.DimDepth = v));
            dims.Children.Add(Row("Tipo de cota", TypeCombo(_probe.DimensionTypes, () => _t.DimType, v => _t.DimType = v), "Formato e unidade das cotas (ex.: 0.60 m) vêm do tipo de cota."));
            var dimsCard = ToolChrome.Card(dims, "Cotas");
            dimsCard.ToolTip = "Medidas nominais do tipo (Largura / Altura). A altura em relação ao piso vem da primeira instância no modelo: peitoril nas janelas, posição real da base nas demais famílias; portas ficam no piso.";
            stack.Children.Add(dimsCard);

            var marks = new StackPanel();
            marks.Children.Add(_fillMarks);
            _fillMarks.ToolTip = "Altera o parâmetro Marca de tipo dos tipos sem marca, para que fichas, etiquetas e tabelas usem o mesmo código.";
            stack.Children.Add(ToolChrome.Card(marks, "Marca de tipo"));
            return stack;
        }

        // ---------------------------------------------------------------- Tabela
        private UIElement BuildTableTab()
        {
            var stack = new StackPanel();
            var rows = new StackPanel();
            rows.Children.Add(ToolChrome.Muted("Rótulo e valor: um parâmetro do tipo/instância ou um dado calculado [entre colchetes].", 11));
            var head = new System.Windows.Controls.Grid { Margin = new Thickness(0, 10, 0, 4) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var h1 = ToolChrome.Muted("RÓTULO", 10.5); h1.FontWeight = FontWeights.Bold;
            var h2 = ToolChrome.Muted("VALOR", 10.5); h2.FontWeight = FontWeights.Bold;
            System.Windows.Controls.Grid.SetColumn(h2, 1);
            head.Children.Add(h1); head.Children.Add(h2);
            rows.Children.Add(head);
            rows.Children.Add(_rowsPanel);
            var add = ToolChrome.Ghost("+  Adicionar linha", "Acrescenta uma linha à tabela", (s, e) =>
            {
                _t.Rows.Add(new LegendRow("Novo", LegendSources.Description));
                RebuildRows();
                Changed();
            });
            add.HorizontalAlignment = HorizontalAlignment.Left;
            add.Margin = new Thickness(0, 6, 0, 0);
            rows.Children.Add(add);
            _loaders.Add(RebuildRows);
            stack.Children.Add(ToolChrome.Card(rows, "Tabela"));

            var look = new StackPanel();
            look.Children.Add(Check("Linhas separando as linhas da tabela (como uma ficha)", () => _t.TableLines, v => _t.TableLines = v));
            look.Children.Add(Check("Mostrar os rótulos", () => _t.ShowLabels, v => _t.ShowLabels = v));
            look.Children.Add(Row("Coluna do rótulo", Num(() => _t.LabelColumnPct, v => _t.LabelColumnPct = v, 0, 90, "% da largura da tabela"), null));
            look.Children.Add(Row("Coluna do valor", Num(() => _t.ValueColumnPct, v => _t.ValueColumnPct = v, 5, 95, "% da largura da tabela"), null));
            look.Children.Add(Row("Altura mínima da linha", Num(() => _t.RowHeightMm, v => _t.RowHeightMm = v, 3, 40, "mm"), "Textos longos quebram a linha e a linha cresce."));
            stack.Children.Add(ToolChrome.Card(look, "Aparência"));
            return stack;
        }

        private void RebuildRows()
        {
            _rowsPanel.Children.Clear();
            var sources = LegendSources.All.Concat(_probe.ParameterNames).ToList();
            for (int i = 0; i < _t.Rows.Count; i++)
            {
                int idx = i;
                LegendRow row = _t.Rows[i];
                var g = new System.Windows.Controls.Grid { Margin = new Thickness(0, 0, 0, 4) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var label = new TextBox { Text = row.Label ?? "", Margin = new Thickness(0, 0, 6, 0) };
                label.TextChanged += (s, e) => { row.Label = label.Text; Changed(); };
                var src = new ComboBox { IsEditable = true, ItemsSource = sources, Text = row.Source ?? "", ToolTip = "Escolha um dado calculado ou digite/escolha o nome de um parâmetro" };
                src.SelectionChanged += (s, e) => { if (src.SelectedItem is string v) { row.Source = v; Changed(); } };
                src.LostKeyboardFocus += (s, e) => { if (row.Source != src.Text) { row.Source = src.Text; Changed(); } };
                System.Windows.Controls.Grid.SetColumn(src, 1);
                var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 0, 0) };
                tools.Children.Add(ToolChrome.Ghost("↑", "Subir", (s, e) => MoveRow(idx, -1)));
                tools.Children.Add(ToolChrome.Ghost("↓", "Descer", (s, e) => MoveRow(idx, +1)));
                tools.Children.Add(ToolChrome.Ghost("✕", "Remover a linha", (s, e) => { _t.Rows.RemoveAt(idx); RebuildRows(); Changed(); }));
                System.Windows.Controls.Grid.SetColumn(tools, 2);
                g.Children.Add(label); g.Children.Add(src); g.Children.Add(tools);
                _rowsPanel.Children.Add(g);
            }
            if (_t.Rows.Count == 0) _rowsPanel.Children.Add(ToolChrome.Muted("Sem tabela. Use “Adicionar linha”.", 11.5));
        }

        private void MoveRow(int i, int delta)
        {
            int j = i + delta;
            if (j < 0 || j >= _t.Rows.Count) return;
            var r = _t.Rows[i]; _t.Rows[i] = _t.Rows[j]; _t.Rows[j] = r;
            RebuildRows();
            Changed();
        }

        // ---------------------------------------------------------------- Pranchas
        private UIElement BuildSheetsTab()
        {
            var stack = new StackPanel();
            var top = new StackPanel();
            top.Children.Add(_sheetsOn);
            _sheetsOn.ToolTip = "Cria as pranchas com o carimbo escolhido e coloca uma ficha em cada célula, na ordem da lista — como um caderno de esquadrias. " +
                                "A legenda leva só os desenhos e as cotas; moldura, linhas e textos (marca, medidas, descrição, quantidade, localização…) são criados na prancha.";
            var body = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            // filled by EnsureTitleBlocks() when sheets are switched on
            body.Children.Add(Row("Carimbo", _titleBlock, null));
            var grid = new StackPanel { Orientation = Orientation.Horizontal };
            grid.Children.Add(_sRows); grid.Children.Add(Unit("linha(s)  ×", new Thickness(6, 0, 6, 0)));
            grid.Children.Add(_sCols); grid.Children.Add(Unit("coluna(s) por prancha"));
            body.Children.Add(Row("Fichas por prancha", grid, "Ex.: 2 × 1 numa A4 em pé = duas fichas empilhadas."));
            body.Children.Add(_fit);
            body.Children.Add(_autoMargins);
            var inset = new StackPanel { Orientation = Orientation.Horizontal };
            inset.Children.Add(_inset);
            inset.Children.Add(Unit("mm para dentro da área livre (0 = fichas encostadas na moldura)"));
            body.Children.Add(Row("Afastamento", inset, "Somado às margens detectadas no carimbo."));
            body.Children.Add(_freeInfo);
            var margins = new WrapPanel();
            foreach (var pair in new[] { Tuple.Create("esq.", _mLeft), Tuple.Create("sup.", _mTop), Tuple.Create("dir.", _mRight), Tuple.Create("inf.", _mBottom) })
            {
                margins.Children.Add(Unit(pair.Item1, new Thickness(0, 0, 4, 0)));
                margins.Children.Add(pair.Item2);
                margins.Children.Add(new Border { Width = 12 });
            }
            _marginPanel = margins;
            body.Children.Add(Row("Margens (mm)", margins, "Distância da borda do carimbo até as fichas, em cada lado. Desligue o encaixe automático para digitar à mão."));
            body.Children.Add(Row("Primeira prancha", _firstNumber, "As próximas seguem a numeração (A106, A107…). Números em uso são pulados."));
            body.Children.Add(Row("Nome das pranchas", _sheetName, null));
            body.Children.Add(Row("Observação", _note, "Linha emoldurada sob as fichas de cada prancha (ex.: OBS.: CONFERIR MEDIDAS NO LOCAL; COTAS EM METRO.). Vazio = sem linha."));
            body.Children.Add(_hideTitle);
            body.Children.Add(_sheetInfo);
            _sheetBody = body;
            top.Children.Add(body);
            stack.Children.Add(ToolChrome.Card(top, "Pranchas"));

            foreach (var t in new[] { _sRows, _sCols, _mLeft, _mTop, _mRight, _mBottom, _firstNumber, _sheetName, _note }) t.TextChanged += (s, e) => Refresh();
            foreach (var c in new[] { _sheetsOn, _fit, _hideTitle, _autoMargins }) { c.Checked += (s, e) => Refresh(); c.Unchecked += (s, e) => Refresh(); }
            _inset.TextChanged += (s, e) => Refresh();
            _titleBlock.SelectionChanged += (s, e) => Refresh();
            _sheetsOn.Checked += (s, e) => EnsureTitleBlocks();
            return stack;
        }

        private FrameworkElement BuildFooter()
        {
            var dock = new DockPanel();
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(right, Dock.Right);
            var cancel = ToolChrome.Ghost("Cancelar");
            cancel.IsCancel = true; cancel.MinWidth = 88; cancel.Margin = new Thickness(0, 0, 8, 0);
            _primary = ToolChrome.Primary("Criar", (s, e) => OnCreate());
            _primary.IsDefault = true; _primary.MinWidth = 200;
            var update = ToolChrome.Secondary("Atualizar fichas…", (s, e) => { AtualizarFichas = true; DialogResult = true; },
                "Conta de novo os elementos e reescreve as quantidades (e, se quiser, a localização) das fichas já feitas, nas pranchas e nas legendas.");
            update.Margin = new Thickness(0, 0, 8, 0);
            Loaded += (s, e) =>
            {
                update.Visibility = FichasExistentes > 0 ? Visibility.Visible : Visibility.Collapsed;
                update.Content = $"Atualizar fichas ({FichasExistentes})…";
            };
            right.Children.Add(update);
            right.Children.Add(cancel);
            right.Children.Add(_primary);
            dock.Children.Add(right);
            dock.Children.Add(_status);
            return dock;
        }

        // ================================================================== templates
        private void SwitchTemplate()
        {
            int i = _templateCombo.SelectedIndex;
            if (i < 0 || i >= _templates.Count) return;
            _t = _templates[i].Clone();
            _t.Name = _templates[i].Name;
            _dirty = false;
            LoadTemplateIntoControls();
            Refresh();
        }

        private void LoadTemplateIntoControls()
        {
            _loading = true;
            try { foreach (var a in _loaders) a(); }
            finally { _loading = false; }
            UpdateTemplateState();
        }

        private void Changed()
        {
            if (_loading) return;
            _dirty = true;
            UpdateTemplateState();
            Refresh();
        }

        private void UpdateTemplateState()
        {
            var sel = _templateCombo.SelectedIndex >= 0 && _templateCombo.SelectedIndex < _templates.Count ? _templates[_templateCombo.SelectedIndex] : null;
            _templateState.Text = _dirty ? "● alterado — “Salvar como…” para reutilizar" : sel != null && sel.BuiltIn ? "modelo do JA Tools" : "seu modelo";
        }

        private void SaveTemplateAs()
        {
            string suggestion = _t.Name != null && !_templates.Any(x => x.BuiltIn && x.Name == _t.Name) ? _t.Name : "Meu modelo";
            var dlg = new OptionsDialog("Salvar modelo de legenda", "QuadroLegendas.SaveTemplate", "Salvar");
            var name = dlg.Text("name", "Nome do modelo", suggestion, "Um modelo com o mesmo nome é substituído. Fica disponível em todos os projetos.");
            if (!dlg.Show(new System.Windows.Interop.WindowInteropHelper(this).Handle)) return;
            string n = (name.Value ?? "").Trim();
            if (n.Length == 0) return;
            if (_templates.Any(x => x.BuiltIn && x.Name == n)) n += " (meu)";
            var saved = _t.Clone();
            saved.Name = n;
            int existing = _templates.FindIndex(x => !x.BuiltIn && x.Name == n);
            if (existing >= 0) _templates[existing] = saved; else _templates.Add(saved);
            LegendTemplate.SaveAll(_templates, n);
            ReloadTemplateCombo(n);
        }

        private void DeleteTemplate()
        {
            int i = _templateCombo.SelectedIndex;
            if (i < 0 || i >= _templates.Count) return;
            if (_templates[i].BuiltIn)
            {
                _templateState.Text = "Os modelos do JA Tools não podem ser excluídos.";
                return;
            }
            if (MessageBox.Show(this, $"Excluir o modelo “{_templates[i].Name}”?", Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _templates.RemoveAt(i);
            LegendTemplate.SaveAll(_templates, _templates[0].Name);
            ReloadTemplateCombo(_templates[0].Name);
        }

        private void ReloadTemplateCombo(string select)
        {
            _templateCombo.Items.Clear();
            foreach (var t in _templates) _templateCombo.Items.Add(TemplateLabel(t));
            _templateCombo.SelectedIndex = Math.Max(0, _templates.FindIndex(x => x.Name == select));
        }

        // ================================================================== bound controls
        private CheckBox Check(string text, Func<bool> get, Action<bool> set)
        {
            var cb = new CheckBox { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap } };
            cb.Checked += (s, e) => { if (_loading) return; set(true); Changed(); };
            cb.Unchecked += (s, e) => { if (_loading) return; set(false); Changed(); };
            _loaders.Add(() => cb.IsChecked = get());
            return cb;
        }

        private void Bind(TextBox tb, Func<string> get, Action<string> set)
        {
            tb.TextChanged += (s, e) => { if (_loading) return; set(tb.Text); Changed(); };
            _loaders.Add(() => tb.Text = get() ?? "");
        }

        private FrameworkElement Num(Func<double> get, Action<double> set, double min, double max, string unit)
        {
            var tb = NumBox(get, set, min, max);
            if (unit == null) return tb;
            var p = new StackPanel { Orientation = Orientation.Horizontal };
            p.Children.Add(tb);
            p.Children.Add(Unit(unit));
            return p;
        }

        private FrameworkElement NumBox(Func<double> get, Action<double> set, double min, double max)
        {
            var tb = new TextBox { Width = 64, HorizontalContentAlignment = HorizontalAlignment.Right };
            tb.TextChanged += (s, e) =>
            {
                if (_loading) return;
                bool ok = OptionsDialog.TryParse(tb.Text, out double v) && v >= min && v <= max;
                if (ok) { set(v); tb.ClearValue(Control.BorderBrushProperty); Changed(); }
                else tb.SetResourceReference(Control.BorderBrushProperty, "Ja.Error");
                tb.ToolTip = ok ? null : $"Use um valor entre {min.ToString(CultureInfo.CurrentCulture)} e {max.ToString(CultureInfo.CurrentCulture)}.";
            };
            _loaders.Add(() => tb.Text = get().ToString("0.##", CultureInfo.CurrentCulture));
            return tb;
        }

        /// <summary>Combo of Revit type names; first item "(padrão do projeto)" = null.</summary>
        private ComboBox TypeCombo(List<KeyValuePair<DB.ElementId, string>> items, Func<string> get, Action<string> set)
        {
            var c = new ComboBox { MinWidth = 220 };
            c.Items.Add("(padrão do projeto)");
            foreach (var kv in items) c.Items.Add(kv.Value);
            c.SelectionChanged += (s, e) => { if (_loading) return; set(c.SelectedIndex <= 0 ? null : c.SelectedItem as string); Changed(); };
            _loaders.Add(() =>
            {
                int i = items.FindIndex(kv => kv.Value == get());
                c.SelectedIndex = i >= 0 ? i + 1 : 0;
            });
            return c;
        }

        // ================================================================== state
        private List<string> SelectedDirs() => LegendEngine.DirectionOrder.Where(k => _t.Views.Contains(k)).ToList();

        private TitleBlockInfo SelectedTitleBlock() =>
            _titleBlock.SelectedIndex >= 0 && _titleBlock.SelectedIndex < _probe.TitleBlocks.Count ? _probe.TitleBlocks[_titleBlock.SelectedIndex] : null;

        private SheetPlacement ReadSheets(out string error)
        {
            error = null;
            var s = new SheetPlacement
            {
                Enabled = _sheetsOn.IsChecked == true && _probe.TitleBlocks.Count > 0,
                FitCards = _fit.IsChecked == true,
                HideViewportTitle = _hideTitle.IsChecked == true,
                FirstNumber = _firstNumber.Text.Trim(),
                Name = _sheetName.Text.Trim(),
                Note = _note.Text.Trim()
            };
            var tb = SelectedTitleBlock();
            if (tb != null) s.TitleBlock = tb.Id;
            if (!s.Enabled) return s;
            if (tb == null) { error = "Escolha o carimbo (aba Pranchas)."; return s; }
            if (!int.TryParse(_sRows.Text.Trim(), out s.Rows) || s.Rows < 1 || s.Rows > 20) { error = "Fichas por prancha: linhas de 1 a 20 (aba Pranchas)."; return s; }
            if (!int.TryParse(_sCols.Text.Trim(), out s.Columns) || s.Columns < 1 || s.Columns > 20) { error = "Fichas por prancha: colunas de 1 a 20 (aba Pranchas)."; return s; }
            double v;
            if (!OptionsDialog.TryParse(_mLeft.Text, out v) || v < 0) { error = "Margem esquerda inválida (aba Pranchas)."; return s; } s.MarginLeftMm = v;
            if (!OptionsDialog.TryParse(_mTop.Text, out v) || v < 0) { error = "Margem superior inválida (aba Pranchas)."; return s; } s.MarginTopMm = v;
            if (!OptionsDialog.TryParse(_mRight.Text, out v) || v < 0) { error = "Margem direita inválida (aba Pranchas)."; return s; } s.MarginRightMm = v;
            if (!OptionsDialog.TryParse(_mBottom.Text, out v) || v < 0) { error = "Margem inferior inválida (aba Pranchas)."; return s; } s.MarginBottomMm = v;
            if (string.IsNullOrWhiteSpace(s.FirstNumber)) { error = "Defina o número da primeira prancha (aba Pranchas)."; return s; }
            return s;
        }

        /// <summary>With "encaixar na área livre": the margins come from the title block's frame and info band (+ afastamento).</summary>
        private void ApplyAutoMargins()
        {
            if (_applyingMargins) return;
            bool auto = _autoMargins.IsChecked == true;
            if (_marginPanel != null) _marginPanel.IsEnabled = !auto;
            _inset.IsEnabled = auto;
            var tb = SelectedTitleBlock();
            if (tb == null) { _freeInfo.Text = ""; return; }
            if (!tb.FreeDetected)
            {
                _freeInfo.Text = "Não foi possível achar a moldura deste carimbo: ajuste as margens à mão.";
                _freeInfo.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Warn");
                if (_marginPanel != null) _marginPanel.IsEnabled = true;
                return;
            }
            _freeInfo.Text = $"Área livre do carimbo: {tb.FreeLeftMm:0} mm à esquerda, {tb.FreeTopMm:0} acima, {tb.FreeRightMm:0} à direita, {tb.FreeBottomMm:0} abaixo (faixa de informações).";
            _freeInfo.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Muted");
            if (!auto) return;
            double inset = OptionsDialog.TryParse(_inset.Text, out double v) && v >= 0 ? v : 0;
            _applyingMargins = true;
            try
            {
                SetIfDifferent(_mLeft, tb.FreeLeftMm + inset);
                SetIfDifferent(_mTop, tb.FreeTopMm + inset);
                SetIfDifferent(_mRight, tb.FreeRightMm + inset);
                SetIfDifferent(_mBottom, tb.FreeBottomMm + inset);
            }
            finally { _applyingMargins = false; }
        }

        private static void SetIfDifferent(TextBox box, double mm)
        {
            string text = Math.Round(mm, 1).ToString("0.#", CultureInfo.CurrentCulture);
            if (box.Text != text) box.Text = text;
        }

        /// <summary>Card size the legends will have (mm): the template's, or computed from the sheet.</summary>
        private void CardSize(SheetPlacement s, out double w, out double h, out bool fromSheet)
        {
            var tb = SelectedTitleBlock();
            fromSheet = s.Enabled && s.FitCards && tb != null;
            if (fromSheet) LegendEngine.CardFromSheet(tb, s, out w, out h);
            else { w = _t.WidthMm; h = _t.HeightMm; }
        }

        private void Refresh()
        {
            if (_badge == null || _primary == null) return;
            ApplyAutoMargins();
            var sel = _items.Where(c => c.IsSelected).ToList();
            var dirs = SelectedDirs();
            _badge.Text = $"{sel.Count} de {_items.Count}";

            var lines = new List<string>();
            foreach (string d in dirs)
            {
                var missing = sel.Select(c => c.Category).Distinct()
                    .Where(cat => !_probe.Directions.TryGetValue(cat, out var map) || !map.ContainsKey(d)).ToList();
                if (missing.Count > 0) lines.Add($"• {LegendEngine.DirectionLabel(d)}: não existe para {string.Join(", ", missing)} — será pulada nesses tipos.");
            }
            _dirHint.Text = sel.Count == 0 ? "Marque tipos na lista para ver quais vistas cada categoria oferece."
                          : lines.Count == 0 ? "✓ Todas as vistas marcadas existem para os tipos escolhidos."
                          : string.Join("\n", lines);
            _dirHint.SetResourceReference(TextBlock.ForegroundProperty, lines.Count == 0 ? "Ja.Ok" : "Ja.Warn");

            SheetPlacement sheets = ReadSheets(out string sheetError);
            if (_sheetBody != null) _sheetBody.IsEnabled = _sheetsOn.IsChecked == true;
            CardSize(sheets, out double cw, out double ch, out bool fromSheet);
            if (_cardW != null) { _cardW.IsEnabled = !fromSheet && _t.FixedSize; _cardH.IsEnabled = !fromSheet && _t.FixedSize; }
            int pages = 0;
            if (sheets.Enabled && SelectedTitleBlock() != null)
            {
                var tb = SelectedTitleBlock();
                double uw = tb.Width * 304.8 - sheets.MarginLeftMm - sheets.MarginRightMm;
                double uh = tb.Height * 304.8 - sheets.MarginTopMm - sheets.MarginBottomMm;
                int cols = fromSheet ? sheets.Columns : Math.Max(1, (int)Math.Floor(uw / Math.Max(1, cw)));
                int rows = fromSheet ? sheets.Rows : Math.Max(1, (int)Math.Floor(uh / Math.Max(1, ch)));
                pages = (int)Math.Ceiling(sel.Count / (double)Math.Max(1, cols * rows));
                _sheetInfo.Text = $"Carimbo {tb.Width * 304.8:0} × {tb.Height * 304.8:0} mm · área útil {uw:0} × {uh:0} mm · ficha {cw:0} × {ch:0} mm" +
                                  (fromSheet ? " (calculada)" : "") + $" · {cols * rows} por prancha → {pages} prancha(s).";
                _sheetInfo.SetResourceReference(TextBlock.ForegroundProperty, (!fromSheet && (cw > uw + 0.5 || ch > uh + 0.5)) ? "Ja.Warn" : "Ja.Muted");
            }
            else _sheetInfo.Text = "";

            string error = sheetError ?? (string.IsNullOrWhiteSpace(_pattern.Text) ? "Defina o nome de cada legenda (aba Layout)." : null);
            int comps = sel.Sum(c => dirs.Count(d => _probe.Directions.TryGetValue(c.Category, out var m) && m.ContainsKey(d)));
            _primary.IsEnabled = !_working && sel.Count > 0 && dirs.Count > 0 && error == null;
            _primary.Content = sel.Count == 0 ? "Marque os tipos" : dirs.Count == 0 ? "Marque as vistas"
                             : $"Gerar {sel.Count} {(sel.Count == 1 ? "detalhe" : "detalhes")}" + (pages > 0 ? $" em {pages} prancha(s)" : "");
            if (error != null)
            {
                _status.Text = error;
                _status.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Error");
            }
            else
            {
                _status.Text = sel.Count == 0 ? "Marque as categorias e, na lista, os tipos que terão detalhe."
                             : $"{sel.Count} tipo(s) × {dirs.Count} vista(s) → {sel.Count} legenda(s), {comps} componente(s)" +
                               (_t.FixedSize || fromSheet ? $" · ficha {cw:0} × {ch:0} mm" : "") + (pages > 0 ? $" · {pages} prancha(s)" : "");
                _status.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Text");
            }
            if (_detailsTab != null && _detailsTab.IsSelected) RebuildPerType();
            DrawPreview();
        }

        // ================================================================== preview
        /// <summary>Sketch of one card with the current template (left) and, with sheets on,
        /// the sheet with its cells (right).</summary>
        private void DrawPreview()
        {
            _preview.Children.Clear();
            if (_t == null) return;
            var accent = (Brush)FindResource("Ja.Accent");
            var soft = (Brush)FindResource("Ja.AccentSoft");
            var ink = (Brush)FindResource("Ja.Ink");
            var muted = (Brush)FindResource("Ja.Muted");
            var line = (Brush)FindResource("Ja.Line");

            SheetPlacement sheets = ReadSheets(out _);
            CardSize(sheets, out double cw, out double ch, out bool _);
            if (!_t.FixedSize && !(sheets.Enabled && sheets.FitCards)) { cw = 170; ch = 110; }
            double totalW = _preview.ActualWidth > 0 ? _preview.ActualWidth : 560;
            bool showSheet = sheets.Enabled && SelectedTitleBlock() != null;
            double cardArea = showSheet ? totalW * 0.62 : totalW;
            double s = Math.Min((cardArea - 16) / cw, (_preview.Height - 16) / ch);
            double ox = 8, oy = 8, W = cw * s, H = ch * s;

            if (_t.Layout == 1) DrawRelacaoPreview(ox, oy, W, H, s, cw);
            else
            {
            // frame
            var frame = new Rectangle { Width = W, Height = H, Stroke = _t.Frame ? ink : line, StrokeThickness = 1, Fill = Brushes.White };
            if (!_t.Frame) frame.StrokeDashArray = new DoubleCollection { 3, 3 };
            Put(frame, ox, oy);
            double lc = _t.MarkLeft ? Math.Min(_t.LeftColumnMm, cw * 0.6) * s : 0;
            double pad = _t.PaddingMm * s;
            if (_t.MarkLeft && _t.Divider) Seg(ox + lc, oy, ox + lc, oy + H, ink);

            // table
            var rows = _t.Rows.Where(r => !string.IsNullOrWhiteSpace(r.Source)).ToList();
            double rh = Math.Max(3, _t.RowHeightMm) * s;
            double tableH = _t.TableLines ? rows.Count * rh : 0;
            if (_t.TableLines)
            {
                double tl = ox + lc, tw = W - lc;
                for (int i = 0; i < rows.Count; i++)
                {
                    double y = oy + H - tableH + i * rh;
                    Seg(tl, y, tl + tw, y, ink);
                    if (_t.ShowLabels) Txt(rows[i].Label, tl + tw * _t.LabelColumnPct / 100, y + rh * 0.12, Math.Max(6, rh * 0.55), ink, false);
                    Txt(SampleValue(rows[i].Source), tl + tw * _t.ValueColumnPct / 100, y + rh * 0.12, Math.Max(6, rh * 0.55), muted, false);
                }
            }

            // header
            double headerH = 0;
            if (_t.MarkLeft)
            {
                double cx = ox + lc / 2, cy = oy + H / 2;
                double fs = Math.Max(8, Math.Min(lc * 0.28, 22));
                if (_t.ShowMark) TxtCentered("PA01", cx, cy - fs * 1.1, fs, ink, true);
                if (_t.MarkUnderline && _t.ShowMark) Seg(cx - lc * 0.38, cy + 1, cx + lc * 0.38, cy + 1, ink);
                if (_t.ShowSize) TxtCentered("1.60 m x 2.20 m", cx, cy + 3, Math.Max(6, fs * 0.42), ink, false);
            }
            else if (_t.ShowMark || _t.ShowSize)
            {
                double fs = Math.Max(8, Math.Min(H * 0.07, 16));
                if (_t.ShowMark) Txt("PA01", ox + pad, oy + pad, fs, ink, true);
                if (_t.ShowSize) Txt("1.60 m x 2.20 m", ox + pad, oy + pad + fs * 1.3, fs * 0.6, ink, false);
                headerH = fs * 2.2;
            }

            // drawings
            var dirs = SelectedDirs();
            var elev = dirs.Where(d => d != LegendEngine.Plan).ToList();
            bool plan = dirs.Contains(LegendEngine.Plan);
            double ax = ox + lc + pad, ay = oy + pad + headerH;
            double aw = W - lc - 2 * pad, ah = H - 2 * pad - headerH - tableH - (!_t.TableLines && rows.Count > 0 ? rows.Count * rh : 0);
            if (aw > 10 && ah > 10)
            {
                int n = Math.Max(1, elev.Count);
                double ew = Math.Min(aw / (n + 0.6) * 0.75, ah * 0.33);
                double eh = ew * 1.35;
                double rowW = elev.Count * ew + Math.Max(0, elev.Count - 1) * ew * 0.35;
                double blockH = (elev.Count > 0 ? eh + (_t.FloorLine ? eh * 0.25 : 0) : 0) + (plan ? ew * 0.45 + 10 : 0);
                double top = ay + Math.Max(0, (ah - blockH) / 2);
                double x = ax + (aw - rowW) / 2;
                double floorY = top + (elev.Count > 0 ? eh + (_t.FloorLine ? eh * 0.25 : 0) : 0);
                double frontCx = ax + aw / 2;
                for (int i = 0; i < elev.Count; i++)
                {
                    double ey = floorY - (_t.FloorLine ? eh * 0.25 : 0) - eh;
                    Box(x, ey, ew, eh, soft, accent);
                    TxtCentered(Short(elev[i]), x + ew / 2, ey + eh / 2 - 6, 9, accent, true);
                    if (i == 0)
                    {
                        frontCx = x + ew / 2;
                        if (_t.DimHeight) Seg(x + ew + 5, ey, x + ew + 5, ey + eh, muted);
                        if (_t.DimSill && _t.FloorLine) Seg(x + ew + 5, ey + eh, x + ew + 5, floorY, muted);
                    }
                    x += ew * 1.35;
                }
                if (_t.FloorLine && elev.Count > 0) Seg(ax + (aw - rowW) / 2 - 10, floorY, ax + (aw + rowW) / 2 + 10, floorY, ink);
                if (plan)
                {
                    double py = floorY + 6;
                    Box(frontCx - ew * 0.9, py, ew * 1.8, ew * 0.3, Brushes.White, ink);
                    Box(frontCx - ew / 2, py, ew, ew * 0.3, soft, accent);
                    if (_t.DimWidth) Seg(frontCx - ew / 2, py + ew * 0.3 + 4, frontCx + ew / 2, py + ew * 0.3 + 4, muted);
                    if (_t.DimDepth) Seg(frontCx + ew / 2 + 5, py, frontCx + ew / 2 + 5, py + ew * 0.3, muted);
                }
                if (dirs.Count == 0) TxtCentered("Marque as vistas", ax + aw / 2, ay + ah / 2 - 6, 10, muted, false);
            }
            if (!_t.TableLines && rows.Count > 0)
                for (int i = 0; i < Math.Min(rows.Count, 4); i++)
                    Seg(ax, oy + H - pad - (rows.Count - i) * rh + rh / 2, ax + aw * 0.4, oy + H - pad - (rows.Count - i) * rh + rh / 2, muted, 2);

            }
            Txt($"ficha {cw:0} × {ch:0} mm", ox, oy + H + 1, 9.5, muted, false);

            // sheet
            if (showSheet)
            {
                var tb = SelectedTitleBlock();
                double sw = tb.Width * 304.8, sh = tb.Height * 304.8;
                double avW = totalW - cardArea - 16, avH = _preview.Height - 24;
                double k = Math.Min(avW / sw, avH / sh);
                double sx = cardArea + 8, sy = 8;
                Put(new Rectangle { Width = sw * k, Height = sh * k, Stroke = ink, StrokeThickness = 1, Fill = Brushes.White }, sx, sy);
                Box(sx + 2, sy + sh * k - Math.Max(4, sheets.MarginBottomMm * k * 0.8), sw * k - 4, Math.Max(3, sheets.MarginBottomMm * k * 0.7), soft, line);
                double uw = sw - sheets.MarginLeftMm - sheets.MarginRightMm, uh = sh - sheets.MarginTopMm - sheets.MarginBottomMm;
                CardSize(sheets, out double ccw, out double cch, out bool fromSheet);
                int cols = fromSheet ? sheets.Columns : Math.Max(1, (int)Math.Floor(uw / Math.Max(1, ccw)));
                int rws = fromSheet ? sheets.Rows : Math.Max(1, (int)Math.Floor(uh / Math.Max(1, cch)));
                for (int r = 0; r < rws; r++)
                    for (int c = 0; c < cols; c++)
                        Box(sx + (sheets.MarginLeftMm + c * ccw) * k, sy + (sheets.MarginTopMm + r * cch) * k,
                            Math.Max(2, ccw * k - 1), Math.Max(2, cch * k - 1), soft, accent);
                if (sheets.NoteHeightMm > 0)
                    Box(sx + sheets.MarginLeftMm * k, sy + (sheets.MarginTopMm + rws * cch) * k, Math.Max(2, cols * ccw * k - 1), Math.Max(2, sheets.NoteHeightMm * k), Brushes.White, ink);
                Txt($"{cols * rws} ficha(s) por prancha", sx, sy + sh * k + 2, 9.5, muted, false);
            }
        }

        /// <summary>Sketch of a "relação" card: table on top, drawing + typical details, total.</summary>
        private void DrawRelacaoPreview(double ox, double oy, double W, double H, double s, double cwMm)
        {
            var accent = (Brush)FindResource("Ja.Accent");
            var soft = (Brush)FindResource("Ja.AccentSoft");
            var ink = (Brush)FindResource("Ja.Ink");
            var muted = (Brush)FindResource("Ja.Muted");
            var line = (Brush)FindResource("Ja.Line");
            var frame = new Rectangle { Width = W, Height = H, Stroke = _t.Frame ? ink : line, StrokeThickness = 1, Fill = Brushes.White };
            if (!_t.Frame) frame.StrokeDashArray = new DoubleCollection { 3, 3 };
            Put(frame, ox, oy);

            double mw = _t.ShowMark ? Math.Min(_t.MarkColMm, cwMm * 0.3) * s : 0;
            double rest = W - mw;
            double sw = rest * Math.Max(10, Math.Min(80, _t.SpecColPct)) / 100, cwid = rest * Math.Max(10, Math.Min(80, _t.CompColPct)) / 100;
            double qw = Math.Max(rest * 0.1, rest - sw - cwid);
            double hh = Math.Max(3, _t.HeadRowMm) * s;
            int n = _t.CompPerRoom ? 2 : 1;
            double vh = Math.Max(8, _t.HeadRowMm * 1.6) * s;
            double th = hh + n * vh;
            double x1 = ox + mw, x2 = x1 + sw, x3 = x2 + cwid;
            Seg(x1, oy + hh, ox + W, oy + hh, ink);
            Seg(ox, oy + th, ox + W, oy + th, ink);
            if (mw > 0) Seg(x1, oy, x1, oy + th, ink);
            Seg(x2, oy, x2, oy + th, ink);
            Seg(x3, oy, x3, oy + th, ink);
            double fs = Math.Max(6, Math.Min(hh * 0.6, 11));
            TxtCentered(_t.HeadSpec, x1 + sw / 2, oy + (hh - fs * 1.3) / 2, fs, ink, false);
            TxtCentered(_t.HeadComp, x2 + cwid / 2, oy + (hh - fs * 1.3) / 2, fs, ink, false);
            TxtCentered(_t.HeadQty, x3 + qw / 2, oy + (hh - fs * 1.3) / 2, fs, ink, false);
            if (mw > 0) TxtCentered("TE01", ox + mw / 2, oy + th / 2 - fs, fs * 1.4, ink, true);
            TxtCentered("GRANITO A SER DEFINIDO", x1 + sw / 2, oy + hh + (n * vh - fs * 1.3) / 2, fs * 0.9, muted, false);
            string[] rooms = { "BANHEIRO SUÍTE", "LAVABO" };
            for (int i = 0; i < n; i++)
            {
                double y = oy + hh + i * vh;
                if (i > 0) Seg(x2, y, ox + W, y, ink);
                TxtCentered(rooms[i], x2 + cwid / 2, y + (vh - fs * 1.3) / 2, fs * 0.9, muted, false);
                TxtCentered(_t.QtyByFloor ? $"1 x 6 {_t.FloorWord}" : "6 " + _t.UnitPlural, x3 + qw / 2, y + (vh - fs * 1.3) / 2, fs * 0.9, muted, false);
            }
            double tot = _t.ShowTotal ? Math.Max(4, _t.HeadRowMm) * s : 0;
            if (tot > 0)
            {
                Seg(ox, oy + H - tot, ox + W, oy + H - tot, ink);
                Txt("TOTAL: 6 " + _t.UnitPlural, ox + 4, oy + H - tot + (tot - fs * 1.3) / 2, fs * 0.9, ink, false);
            }

            // drawing (left) + typical details (right)
            double pad = _t.PaddingMm * s;
            double ax = ox + pad, ay = oy + th + pad, aw = W - 2 * pad, ah = H - th - tot - 2 * pad;
            int nd = Math.Max(0, TemplateLabels().Count);
            double dw = nd > 0 ? aw * (1 - Math.Max(0, Math.Min(80, _t.DetailsPct)) / 100) : aw;
            if (ah > 8 && aw > 20)
            {
                double pw = Math.Min(dw * 0.7, ah * 3), ph = Math.Min(ah * 0.55, pw * 0.3);
                double px = ax + (dw - pw) / 2, py = ay + (ah - ph) / 2 - 4;
                Box(px, py, pw, ph, soft, accent);
                Put(new Ellipse { Width = ph * 0.9, Height = ph * 0.6, Stroke = accent, StrokeThickness = 1 }, px + pw * 0.25, py + ph * 0.2);
                if (_t.DimWidth) Seg(px, py + ph + 5, px + pw, py + ph + 5, muted);
                if (_t.DimDepth || _t.DimWidth) Seg(px + pw + 5, py, px + pw + 5, py + ph, muted);
                if (nd > 0)
                {
                    double rx = ax + dw, rw = aw - dw;
                    double d = Math.Min(ah * 0.85, rw / nd * 0.8);
                    double gx = rx + (rw - nd * d - (nd - 1) * d * 0.15) / 2;
                    for (int i = 0; i < nd; i++)
                    {
                        Put(new Ellipse { Width = d, Height = d, Stroke = ink, StrokeThickness = 1, Fill = Brushes.White }, gx, ay + (ah - d) / 2);
                        Seg(gx + d * 0.45, ay + (ah - d) / 2 + d * 0.2, gx + d * 0.45, ay + (ah + d) / 2 - d * 0.2, accent, 2);
                        gx += d * 1.15;
                    }
                }
                else Txt("sem detalhes típicos", ax + dw - 90, ay + ah / 2, 9, muted, false);
            }
        }

        private void Put(UIElement e, double x, double y) { Canvas.SetLeft(e, x); Canvas.SetTop(e, y); _preview.Children.Add(e); }
        private void Box(double x, double y, double w, double h, Brush fill, Brush stroke) =>
            Put(new Rectangle { Width = Math.Max(0, w), Height = Math.Max(0, h), Fill = fill, Stroke = stroke, StrokeThickness = 1 }, x, y);
        private void Seg(double x1, double y1, double x2, double y2, Brush b, double thick = 1) =>
            _preview.Children.Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = b, StrokeThickness = thick });
        private void Txt(string text, double x, double y, double size, Brush b, bool bold) =>
            Put(new TextBlock { Text = text ?? "", FontSize = size, Foreground = b, FontWeight = bold ? FontWeights.Bold : FontWeights.Normal }, x, y);
        private void TxtCentered(string text, double cx, double y, double size, Brush b, bool bold)
        {
            var t = new TextBlock { Text = text, FontSize = size, Foreground = b, FontWeight = bold ? FontWeights.Bold : FontWeights.Normal };
            t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Put(t, cx - t.DesiredSize.Width / 2, y);
        }

        private static string SampleValue(string source)
        {
            switch (source)
            {
                case LegendSources.Quantity: return "68";
                case LegendSources.Location: return "QUARTO, CIRC., SALA";
                case LegendSources.Size: return "1.60 m x 2.20 m";
                case LegendSources.Description: return "PORTA DE CORRER - 2 FOLHAS";
                case LegendSources.Mark: return "PA01";
                case LegendSources.Family: return "Porta de correr";
                case LegendSources.TypeName: return "160x220";
                default: return "‹" + source + "›";
            }
        }

        private static string Short(string key)
        {
            switch (key)
            {
                case LegendEngine.Front: return "F";
                case LegendEngine.Back: return "P";
                case LegendEngine.Left: return "LE";
                case LegendEngine.Right: return "LD";
                case LegendEngine.Section: return "C";
                default: return "";
            }
        }

        private static string DirTip(string key)
        {
            switch (key)
            {
                case LegendEngine.Front: return "Elevação frontal (como a família é vista de frente).";
                case LegendEngine.Back: return "Elevação posterior (traseira).";
                case LegendEngine.Left: return "Elevação lateral esquerda (vista adjacente).";
                case LegendEngine.Right: return "Elevação lateral direita (vista adjacente).";
                case LegendEngine.Plan: return "Planta (vista de cima), abaixo das elevações.";
                case LegendEngine.Section: return "Corte — existe para paredes, pisos, forros e telhados.";
                default: return null;
            }
        }

        // ================================================================== create
        private void OnCreate()
        {
            SheetPlacement sheets = ReadSheets(out string error);
            if (error != null) { Refresh(); return; }
            var sel = _items.Where(c => c.IsSelected).ToList();
            if (sel.Count == 0 || SelectedDirs().Count == 0) return;

            var nat = new NaturalStringComparer();
            IEnumerable<LegendCandidate> ordered;
            switch (_sort.SelectedIndex)
            {
                case 1: ordered = sel.OrderBy(c => c.Family, StringComparer.CurrentCultureIgnoreCase).ThenBy(c => c.TypeName, nat); break;
                case 2: ordered = sel.OrderBy(c => c.Width).ThenBy(c => c.Height).ThenBy(c => c.TypeName, nat); break;
                default: ordered = sel.OrderBy(c => string.IsNullOrEmpty(c.Mark) ? 1 : 0).ThenBy(c => c.Mark, nat).ThenBy(c => c.Family).ThenBy(c => c.TypeName, nat); break;
            }
            var template = _t.Clone();
            template.Name = _t.Name;
            template.Rows = template.Rows.Where(r => !string.IsNullOrWhiteSpace(r.Source)).ToList();
            Request = new LegendRequest
            {
                Types = ordered.ToList(),
                Template = template,
                NamePattern = _pattern.Text.Trim(),
                FillMarks = _fillMarks.IsChecked == true,
                Sheets = sheets,
                PhaseId = SelectedPhase()
            };
            foreach (var c in Request.Types)
            {
                List<string> own = OwnDetails(c.UniqueId);
                if (own == null) continue;
                Request.TypeDetails[c.Id] = own.Select(label => _probe.DetailSymbols.FirstOrDefault(kv => kv.Value == label).Key)
                                               .Where(id => id != null).ToList();
            }
            SavePrefs();
            DialogResult = true;
        }

        // ================================================================== prefs (per run, not template)
        private int _initialSort;

        private void LoadPrefs(ICollection<DB.ElementId> preselected)
        {
            _onlyUsed.IsChecked = Get("onlyUsed") != "0";
            _initialPhase = Get("phase");
            _fillMarks.IsChecked = Get("fillMarks") == "1";
            _pattern.Text = Get("pattern") ?? "DETALHE - {marca}";
            _initialSort = int.TryParse(Get("sort"), out int so) && so >= 0 && so < SortChoices.Length ? so : 0;
            _sheetsOn.IsChecked = Get("sheets") == "1";
            _sRows.Text = Get("sRows") ?? "2";
            _sCols.Text = Get("sCols") ?? "1";
            _mLeft.Text = Get("mLeft") ?? "10";
            _mTop.Text = Get("mTop") ?? "10";
            _mRight.Text = Get("mRight") ?? "10";
            _mBottom.Text = Get("mBottom") ?? "25";
            _fit.IsChecked = Get("fit") != "0";
            _firstNumber.Text = Get("firstNumber") ?? "A101";
            _sheetName.Text = Get("sheetName") ?? "CADERNO DE ESQUADRIA";
            _hideTitle.IsChecked = Get("hideTitle") != "0";
            _note.Text = Get("note") ?? "";
            _autoMargins.IsChecked = Get("autoMargins") != "0";
            _inset.Text = Get("inset") ?? "0";
            _initialTitleBlock = Get("titleBlock");
            _initialCats = new HashSet<string>((Get("cats") ?? "").Split('\u001f').Where(x => x.Length > 0));
            if (_initialCats.Count == 0) _initialCats.UnionWith(_probe.Categories
                .Where(c => c.Id == (long)DB.BuiltInCategory.OST_Doors || c.Id == (long)DB.BuiltInCategory.OST_Windows).Select(c => c.Name));
            // what is selected in Revit decides the categories to open with
            if (_preselectedCategories.Count > 0)
                _initialCats = new HashSet<string>(_preselectedCategories);
        }
        private string _initialTitleBlock;
        private HashSet<string> _initialCats = new HashSet<string>();
        /// <summary>Categories of the elements selected in Revit.</summary>
        private readonly IList<string> _preselectedCategories;

        private void SavePrefs()
        {
            Set("onlyUsed", _onlyUsed.IsChecked == true ? "1" : "0");
            Set("phase", _phase.SelectedIndex > 0 ? _probe.Phases[_phase.SelectedIndex - 1].Value : "");
            Set("fillMarks", _fillMarks.IsChecked == true ? "1" : "0");
            Set("pattern", _pattern.Text);
            Set("sort", _sort.SelectedIndex.ToString(CultureInfo.InvariantCulture));
            Set("sheets", _sheetsOn.IsChecked == true ? "1" : "0");
            Set("sRows", _sRows.Text); Set("sCols", _sCols.Text);
            Set("mLeft", _mLeft.Text); Set("mTop", _mTop.Text); Set("mRight", _mRight.Text); Set("mBottom", _mBottom.Text);
            Set("fit", _fit.IsChecked == true ? "1" : "0");
            Set("firstNumber", _firstNumber.Text);
            Set("sheetName", _sheetName.Text);
            Set("hideTitle", _hideTitle.IsChecked == true ? "1" : "0");
            Set("note", _note.Text);
            Set("autoMargins", _autoMargins.IsChecked == true ? "1" : "0");
            Set("inset", _inset.Text);
            foreach (var kv in _typeDetails)
                Set("det." + kv.Key, kv.Value == null ? "" : kv.Value.Count == 0 ? "\u0001" : string.Join("\u001f", kv.Value));
            Set("titleBlock", SelectedTitleBlock()?.Name ?? "");
            Set("cats", string.Join("\u001f", _catPills.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key.Name)));
            ToolPrefs.Save();
            // the template in use (unsaved changes included) is remembered as "last"
            int i = _templateCombo.SelectedIndex;
            if (i >= 0 && i < _templates.Count) LegendTemplate.SaveAll(_templates, _templates[i].Name);
        }

        private static string Get(string key) => ToolPrefs.Get(P + key);
        private static void Set(string key, string value) => ToolPrefs.Set(P + key, value ?? "");

        // ================================================================== small helpers
        private static FrameworkElement Row(string label, UIElement control, string tip)
        {
            var g = new System.Windows.Controls.Grid { Margin = new Thickness(0, 6, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Text");
            g.Children.Add(t);
            System.Windows.Controls.Grid.SetColumn(control, 1);
            if (control is FrameworkElement fe && !(control is Panel)) fe.HorizontalAlignment = fe is TextBox ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
            g.Children.Add(control);
            if (tip != null) g.ToolTip = tip;
            return g;
        }

        private static TextBlock Unit(string text) => Unit(text, new Thickness(6, 0, 0, 0));

        private static TextBlock Unit(string text, Thickness margin)
        {
            var u = ToolChrome.Muted(text, 12);
            u.VerticalAlignment = VerticalAlignment.Center;
            u.Margin = margin;
            return u;
        }
    }
}
