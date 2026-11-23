using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using JAEsquadrias.Core;
using JAEsquadrias.Engine;
using static JAEsquadrias.UI.FormKit;

namespace JAEsquadrias.UI
{
    public sealed class ConfiguratorResult
    {
        public EsquadriaConfig Config;
        /// <summary>true = place instances right after generating.</summary>
        public bool Place;
    }

    /// <summary>Where the catalogue on the left takes its items from.</summary>
    public enum FonteCatalogo { Janelas, Portas, Biblioteca, Projeto }

    /// <summary>
    /// The JA Famílias configurator (one window for new windows, new doors, the library and
    /// editing). Same design language as JA Tools: header with steps, cards, accent pills,
    /// muted hints, one primary action.
    ///   left   – catalogue: ready-made models, the user's library, JA esquadrias of the project;
    ///   centre – live elevation (seen from outside) with dimensions; click a module to select it;
    ///   right  – tabs Medidas · Divisões · Perfil e vidro · Detalhes;
    ///   bottom – Uw, light and ventilation areas, warnings; actions.
    /// </summary>
    public sealed class ConfiguratorWindow
    {
        private sealed class CatalogItem
        {
            public string Name, Desc, Key;
            public EsquadriaConfig Config;
            public Modelo Modelo;
            public string LibraryPath;
        }

        private readonly Window _w;
        private readonly ToolInfo _info;
        private readonly bool _editing;
        private readonly HashSet<string> _usedCodes;
        private readonly List<KeyValuePair<string, EsquadriaConfig>> _projectItems;
        private readonly Settings _settings;
        private EsquadriaConfig _cfg;
        private Layout _layout;
        private RenderFrame _frame;
        private int _selRow, _selCol;
        private FonteCatalogo _fonte;
        private ConfiguratorResult _result;
        private bool _planta;
        private Panel _vistaPills;

        private void SetVista(bool planta)
        {
            if (_vistaPills != null && _vistaPills.Children.Count > 1 && _vistaPills.Children[planta ? 1 : 0] is RadioButton rb) rb.IsChecked = true;
            else { _planta = planta; Draw(); }
        }
        private readonly List<string> _materials;
        private readonly HashSet<TextBox> _invalid = new HashSet<TextBox>();

        // controls
        private Canvas _canvas;
        private StackPanel _catalogList;
        private TextBox _search;
        private TabControl _tabs;
        private WrapPanel _stats;
        private TextBlock _sizeBadge, _status, _bimText, _codeError;

        public static ConfiguratorResult Show(IntPtr owner, string toolTitle, EsquadriaConfig start, bool editing,
                                              HashSet<string> usedCodes, List<KeyValuePair<string, EsquadriaConfig>> projectItems,
                                              FonteCatalogo fonte, IList<string> materials = null)
        {
            var cw = new ConfiguratorWindow(toolTitle, start, editing, usedCodes, projectItems, fonte, materials);
            if (owner != IntPtr.Zero) new WindowInteropHelper(cw._w).Owner = owner;
            cw._w.ShowDialog();
            return cw._result;
        }

        private ConfiguratorWindow(string toolTitle, EsquadriaConfig start, bool editing, HashSet<string> usedCodes,
                                   List<KeyValuePair<string, EsquadriaConfig>> projectItems, FonteCatalogo fonte, IList<string> materials)
        {
            _materials = (materials ?? new List<string>()).Where(m => !string.IsNullOrWhiteSpace(m)).Distinct().OrderBy(m => m, StringComparer.CurrentCultureIgnoreCase).ToList();
            _info = ToolCatalog.Get(toolTitle);
            _settings = Settings.Current;
            _cfg = start.Clone();
            _editing = editing;
            _usedCodes = usedCodes ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _projectItems = projectItems ?? new List<KeyValuePair<string, EsquadriaConfig>>();
            _fonte = fonte;

            _w = NewConfiguratorWindow();
            var body = BuildBody();
            var footer = BuildFooter();
            ToolChrome.Dress(_w, toolTitle, body, footer);
            OnCtrlEnter(_w, () => Finish(!_editing));
            _w.Loaded += (s, e) => { FillCatalog(); Refresh(); };
            RebuildEditor();
        }

        // =========================================================================================
        // layout
        // =========================================================================================
        private UIElement BuildBody()
        {
            var g = CatalogPreviewGrid(BuildCatalog(), BuildPreview());

            _tabs = new TabControl();
            foreach (string h in new[] { "Medidas", "Divisões", "Perfil e vidro", "Detalhes", "Planta 2D" })
                _tabs.Items.Add(new TabItem { Header = h });
            // the Planta 2D tab shows the plan in the preview
            _tabs.SelectionChanged += (s, e) =>
            {
                if (!ReferenceEquals(e.OriginalSource, _tabs)) return;
                if (_tabs.SelectedIndex == 4 && !_planta) SetVista(true);
            };
            var right = new Border { Child = _tabs, Margin = new Thickness(0, -8, 0, 0) };
            Grid.SetColumn(right, 2);
            g.Children.Add(right);
            return g;
        }

        private Border BuildCatalog()
        {
            var dock = new DockPanel();
            var pills = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            string group = "fonte" + Guid.NewGuid().ToString("N");
            foreach (FonteCatalogo f in Enum.GetValues(typeof(FonteCatalogo)))
            {
                string label = f == FonteCatalogo.Projeto ? "No projeto" : f.ToString();
                var rb = new RadioButton { Content = label, GroupName = group, IsChecked = f == _fonte };
                rb.SetResourceReference(FrameworkElement.StyleProperty, "Ja.Segment");
                var ff = f;
                rb.Checked += (s, e) => { _fonte = ff; FillCatalog(); };
                pills.Children.Add(rb);
            }
            DockPanel.SetDock(pills, Dock.Top);
            dock.Children.Add(pills);

            _search = new TextBox { Margin = new Thickness(0, 0, 0, 10) };
            _search.TextChanged += (s, e) => FillCatalog();
            var sb = ToolChrome.SearchBox(_search, "Buscar modelo…");
            DockPanel.SetDock(sb, Dock.Top);
            dock.Children.Add(sb);

            _catalogList = new StackPanel();
            dock.Children.Add(new ScrollViewer { Content = _catalogList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 4, 0) });
            var card = ToolChrome.Card(dock, "Modelos");
            return card;
        }

        private Border BuildPreview()
        {
            _sizeBadge = null;
            var badge = ToolChrome.Badge(out _sizeBadge);
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            var vista = Pills(new[] { "Elevação", "Planta" }, _planta ? 1 : 0, i => { _planta = i == 1; Draw(); });
            vista.Margin = new Thickness(0, 0, 8, -6);
            _vistaPills = vista as Panel;
            head.Children.Add(vista);
            head.Children.Add(badge);
            var dock = new DockPanel();

            _stats = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            DockPanel.SetDock(_stats, Dock.Bottom);
            dock.Children.Add(_stats);

            var hint = ToolChrome.Muted("Clique num módulo da prévia para escolher a abertura dele (aba Divisões). Esquerda e direita como vistas de fora.", 11.5);
            hint.Margin = new Thickness(0, 6, 0, 0);
            DockPanel.SetDock(hint, Dock.Bottom);
            dock.Children.Add(hint);

            _canvas = new Canvas { Background = Brushes.White, ClipToBounds = true, Cursor = Cursors.Hand };
            _canvas.SizeChanged += (s, e) => Draw();
            _canvas.MouseLeftButtonDown += (s, e) =>
            {
                if (_planta || _frame == null || _frame.Scale <= 0 || _layout == null) return;
                var p = _frame.ToModel(e.GetPosition(_canvas));
                var m = _layout.Modulos.FirstOrDefault(x => x.Mod.Contains(p.X, p.Y));
                if (m == null) return;
                _selRow = m.Row;
                _selCol = m.Col;
                RebuildEditor();
                _tabs.SelectedIndex = 1;
                Draw();
            };
            var frame = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Child = _canvas, MinHeight = 260 };
            frame.SetResourceReference(Border.BorderBrushProperty, "Ja.Line");
            dock.Children.Add(frame);
            return ToolChrome.Card(dock, "Pré-visualização", head);
        }

        private UIElement BuildFooter()
        {
            var dock = new DockPanel();
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(right, Dock.Right);

            var cancel = ToolChrome.Secondary("Cancelar");
            cancel.IsCancel = true;
            cancel.Margin = new Thickness(0, 0, 8, 0);
            right.Children.Add(cancel);
            if (!_editing && !_cfg.ParedeCortina)
            {
                var onlyFamily = ToolChrome.Secondary("Só gerar a família", (s, e) => Finish(false),
                    "Gera e carrega a família no projeto sem posicionar (fica no Navegador de Projeto).");
                onlyFamily.Margin = new Thickness(0, 0, 8, 0);
                right.Children.Add(onlyFamily);
            }
            var primary = ToolChrome.Primary(_editing ? "Atualizar esquadria" : _cfg.ParedeCortina ? "Gerar e trocar painéis" : "Gerar e posicionar", (s, e) => Finish(!_editing));
            primary.MinWidth = 150;
            primary.ToolTip = _editing ? "Regera a família; todas as cópias deste tipo no modelo são atualizadas. (Ctrl+Enter)"
                            : _cfg.ParedeCortina ? "Gera a família e troca os painéis selecionados da parede cortina por ela. (Ctrl+Enter)"
                                                 : "Gera a família, carrega no projeto e deixa você clicar nas paredes. Esc termina. (Ctrl+Enter)";
            right.Children.Add(primary);
            dock.Children.Add(right);

            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(ToolChrome.Ghost("Salvar na biblioteca…", "Guarda esta composição para usar em outros projetos", (s, e) => SaveToLibrary()));
            _status = ToolChrome.Muted("", 11.5);
            _status.VerticalAlignment = VerticalAlignment.Center;
            _status.Margin = new Thickness(10, 0, 0, 0);
            left.Children.Add(_status);
            dock.Children.Add(left);
            return dock;
        }

        // =========================================================================================
        // catalogue
        // =========================================================================================
        private List<CatalogItem> Items()
        {
            var list = new List<CatalogItem>();
            switch (_fonte)
            {
                case FonteCatalogo.Janelas:
                case FonteCatalogo.Portas:
                    var tipo = _fonte == FonteCatalogo.Portas ? TipoEsquadria.Porta : TipoEsquadria.Janela;
                    foreach (var m in Catalog.Modelos.Where(x => x.Tipo == tipo))
                        list.Add(new CatalogItem { Name = m.Nome, Desc = m.Descricao, Key = m.Id, Modelo = m, Config = m.ApplyTo(_cfg, false) });
                    break;
                case FonteCatalogo.Biblioteca:
                    foreach (var it in Library.List(_settings))
                        list.Add(new CatalogItem { Name = it.Name, Desc = Summary(it.Config), Key = "lib:" + it.Path, Config = it.Config, LibraryPath = it.Path });
                    break;
                case FonteCatalogo.Projeto:
                    foreach (var kv in _projectItems)
                        list.Add(new CatalogItem { Name = kv.Key, Desc = Summary(kv.Value), Key = "prj:" + kv.Key, Config = kv.Value });
                    break;
            }
            string q = Plain(_search?.Text);
            if (q.Length > 0) list = list.Where(i => Plain(i.Name + " " + i.Desc).Contains(q)).ToList();
            return list;
        }

        private static string Summary(EsquadriaConfig c) =>
            (c.IsPorta ? "Porta " : "Janela ") + Layout.ResumoAberturas(c) + " · " + Layout.Mm(c.LarguraMm) + " × " + Layout.Mm(c.AlturaMm);

        private void FillCatalog()
        {
            if (_catalogList == null) return;
            _catalogList.Children.Clear();
            var items = Items();
            if (items.Count == 0)
            {
                string msg;
                if (_search != null && _search.Text.Trim().Length > 0) msg = "Nada encontrado para esta busca.";
                else if (_fonte == FonteCatalogo.Biblioteca) msg = "Nenhuma composição salva ainda. Monte uma esquadria e use “Salvar na biblioteca…”, no rodapé.\n\nPasta: " + _settings.PastaBiblioteca;
                else msg = "Nenhuma esquadria do JA Famílias neste projeto ainda.";
                var t = ToolChrome.Muted(msg, 11.5);
                t.Margin = new Thickness(2, 8, 2, 0);
                _catalogList.Children.Add(t);
                return;
            }
            foreach (var it in items) _catalogList.Children.Add(Tile(it));
        }

        private FrameworkElement Tile(CatalogItem it)
        {
            bool current = it.Modelo != null && it.Modelo.Id == _cfg.ModeloId;
            var border = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8),
                Margin = new Thickness(0, 0, 0, 6),
                BorderThickness = new Thickness(current ? 2 : 1),
                Cursor = Cursors.Hand,
                ToolTip = it.Desc
            };
            border.SetResourceReference(Border.BackgroundProperty, current ? "Ja.AccentSoft" : "Ja.Card");
            border.SetResourceReference(Border.BorderBrushProperty, current ? "Ja.Accent" : "Ja.Line");
            border.MouseEnter += (s, e) => { if (!current) border.SetResourceReference(Border.BackgroundProperty, "Ja.Hover"); };
            border.MouseLeave += (s, e) => { if (!current) border.SetResourceReference(Border.BackgroundProperty, "Ja.Card"); };
            border.MouseLeftButtonUp += (s, e) => Apply(it);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var thumb = new Canvas { Width = 62, Height = 58, ClipToBounds = true, IsHitTestVisible = false };
            try { ElevationRenderer.Draw(thumb, it.Config, Layout.Build(it.Config), new RenderOptions { Dimensions = false, Wall = false, Caption = false }, 62, 58); }
            catch { }
            grid.Children.Add(new Border { Child = thumb, Background = Brushes.White, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left });

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
            var name = new TextBlock { Text = it.Name, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Ink");
            text.Children.Add(name);
            var desc = ToolChrome.Muted(it.Desc, 11);
            desc.MaxHeight = 44;
            desc.TextTrimming = TextTrimming.CharacterEllipsis;
            text.Children.Add(desc);
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            border.Child = grid;

            if (it.LibraryPath != null)
            {
                var menu = new ContextMenu();
                var del = new MenuItem { Header = "Excluir da biblioteca" };
                del.Click += (s, e) =>
                {
                    if (MessageBox.Show(_w, "Excluir “" + it.Name + "” da biblioteca?", "JA Famílias", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    {
                        Library.Delete(it.LibraryPath);
                        FillCatalog();
                    }
                };
                menu.Items.Add(del);
                border.ContextMenu = menu;
            }
            return border;
        }

        private void Apply(CatalogItem it)
        {
            EsquadriaConfig next = it.Modelo != null ? it.Modelo.ApplyTo(_cfg, false) : it.Config.Clone();
            if (_editing)
            {
                if (next.Tipo != _cfg.Tipo)
                {
                    SetStatus("Ao editar não dá para trocar " + (_cfg.IsPorta ? "porta por janela" : "janela por porta") + ": gere uma nova esquadria.", true);
                    return;
                }
                next.Codigo = _cfg.Codigo;
            }
            else if (it.Modelo == null || next.Tipo != _cfg.Tipo || _usedCodes.Contains(next.Codigo))
            {
                next.Codigo = next.Tipo == _cfg.Tipo && !_usedCodes.Contains(_cfg.Codigo) ? _cfg.Codigo : ProjectEsquadrias.NextCode(_usedCodes, next.Tipo);
            }
            if (_cfg.ParedeCortina)
            {
                // curtain panel: the size belongs to the panel, not to the model
                next.ParedeCortina = true;
                next.LarguraMm = _cfg.LarguraMm;
                next.AlturaMm = _cfg.AlturaMm;
            }
            _cfg = next.Normalized();
            _selRow = 0;
            _selCol = 0;
            SetStatus(it.Modelo != null ? "Modelo aplicado: " + it.Name : "Carregado: " + it.Name, false);
            RebuildEditor();
            Refresh();
            FillCatalog();
        }

        private void SaveToLibrary()
        {
            string suggestion = (_cfg.IsPorta ? "Porta " : "Janela ") + Layout.ResumoAberturas(_cfg) + " " + Layout.Mm(_cfg.LarguraMm) + "x" + Layout.Mm(_cfg.AlturaMm);
            if (!string.IsNullOrWhiteSpace(_cfg.Nome)) suggestion = _cfg.Nome.Trim();
            string name = Prompt.Ask(_w, "Salvar na biblioteca", "Nome da composição (fica em " + _settings.PastaBiblioteca + "):", suggestion, _info.Accent);
            if (name == null) return;
            try
            {
                Library.Save(_settings, name, _cfg);
                SetStatus("Salvo na biblioteca: " + name, false);
                if (_fonte == FonteCatalogo.Biblioteca) FillCatalog();
            }
            catch (Exception ex) { SetStatus("Não foi possível salvar: " + ex.Message, true); }
        }

        // =========================================================================================
        // editor tabs
        // =========================================================================================
        private void RebuildEditor()
        {
            _invalid.Clear();
            int idx = Math.Max(0, _tabs.SelectedIndex);
            ClampSelection();
            SetTab(0, BuildMedidas());
            SetTab(1, BuildDivisoes());
            SetTab(2, BuildPerfil());
            SetTab(3, BuildDetalhes());
            SetTab(4, BuildPlanta2D());
            _tabs.SelectedIndex = idx;
        }

        // ---------------------------------------------------------------- Medidas
        private UIElement BuildMedidas()
        {
            var panel = new StackPanel();

            var id = new StackPanel();
            var code = new TextBox { Text = _cfg.Codigo, IsReadOnly = _editing, MaxLength = 20 };
            _codeError = new TextBlock { Foreground = ErrorBrush, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
            code.TextChanged += (s, e) => { _cfg.Codigo = code.Text.Trim(); ValidateCode(); RefreshBim(); };
            var codeBox = new StackPanel();
            codeBox.Children.Add(code);
            codeBox.Children.Add(_codeError);
            id.Children.Add(Field("Código", codeBox, _editing
                ? "O código identifica a família no projeto e não muda na edição."
                : "Marca de tipo e nome da família (" + _cfg.FamilyName(_settings.PrefixoFamilia) + ")."));
            var nome = new TextBox { Text = _cfg.Nome ?? "" };
            nome.TextChanged += (s, e) => { _cfg.Nome = nome.Text; RefreshBim(); };
            id.Children.Add(Field("Descrição", nome, "Opcional, ex.: Janela da sala. Vai para a Descrição da família."));
            panel.Children.Add(ToolChrome.Card(id, "Identificação"));
            ValidateCode();

            var dim = new StackPanel();
            if (_cfg.ParedeCortina)
            {
                dim.Children.Add(Note("Parede cortina: largura e altura vêm do painel (a grade da parede cortina manda). " +
                                      "Painéis de outros tamanhos ganham um código para cada tamanho."));
                var wl = NumBox(_cfg.LarguraMm, v => _cfg.LarguraMm = v, 200, 12000); wl.IsEnabled = false;
                var hl = NumBox(_cfg.AlturaMm, v => _cfg.AlturaMm = v, 200, 6000); hl.IsEnabled = false;
                dim.Children.Add(Field("Largura", wl, null));
                dim.Children.Add(Field("Altura", hl, null));
                if (_cfg.IsPorta)
                    dim.Children.Add(Field("Fresta inferior", NumBox(_cfg.FrestaMm, v => _cfg.FrestaMm = v, 0, 100), "Folga entre a folha e o piso."));
                panel.Children.Add(ToolChrome.Card(dim, "Dimensões do painel"));
                var pc = new StackPanel();
                pc.Children.Add(Note("A esquadria fica centrada no plano da parede cortina, no lugar do painel."));
                pc.Children.Add(Field("Deslocar p/ fora", NumBox(_cfg.RecuoMm, v => _cfg.RecuoMm = v, -500, 500), "Positivo = para o lado de fora da parede cortina."));
                panel.Children.Add(ToolChrome.Card(pc, "Posição na parede cortina"));
                return panel;
            }
            dim.Children.Add(Field("Largura", NumBox(_cfg.LarguraMm, v => _cfg.LarguraMm = v, 200, 12000), null));
            dim.Children.Add(Field("Altura", NumBox(_cfg.AlturaMm, v => _cfg.AlturaMm = v, 200, 6000), null));
            if (!_cfg.IsPorta)
                dim.Children.Add(Field("Peitoril", NumBox(_cfg.PeitorilMm, v => _cfg.PeitorilMm = v, 0, 5000), "Do piso até a base da janela (Altura do peitoril padrão da família)."));
            else
                dim.Children.Add(Field("Fresta inferior", NumBox(_cfg.FrestaMm, v => _cfg.FrestaMm = v, 0, 100), "Folga entre a folha e o piso (ex.: 30 mm em cozinhas)."));
            panel.Children.Add(ToolChrome.Card(dim, "Dimensões do vão"));

            var pos = new StackPanel();
            if (_cfg.MarcoNaParede)
            {
                pos.Children.Add(Note("O marco ocupa toda a espessura da parede, com batente e guarnições nas duas faces acabadas. " +
                                      "A família se ajusta sozinha à espessura de qualquer parede onde for colocada."));
            }
            else
            {
                pos.Children.Add(Pills(new[] { "Face externa", "Centro", "Face interna" }, (int)_cfg.Posicao, i => { _cfg.Posicao = (PosicaoParede)i; RebuildEditor(); Refresh(); }));
                pos.Children.Add(Field(_cfg.Posicao == PosicaoParede.Centro ? "Deslocar p/ fora" : "Recuo da face",
                    NumBox(_cfg.RecuoMm, v => _cfg.RecuoMm = v, -500, 500), null));
                pos.Children.Add(Note("A família se ajusta sozinha à espessura de qualquer parede onde for colocada: " +
                                      "posição, peitoril e planta acompanham as faces da parede."));
            }
            pos.Children.Add(Field("Revestimento externo", NumBox(_cfg.RevestimentoExtMm, v => _cfg.RevestimentoExtMm = v, 0, 200),
                "Valor inicial. Da face da parede do Revit até a face acabada, do lado de fora (reboco, cerâmica, pedra). " +
                "Na família é parâmetro de instância: ajuste esquadria por esquadria em Propriedades › Cotas."));
            pos.Children.Add(Field("Revestimento interno", NumBox(_cfg.RevestimentoIntMm, v => _cfg.RevestimentoIntMm = v, 0, 200),
                "Valor inicial, do lado de dentro. Também é parâmetro de instância. " +
                (_cfg.MarcoNaParede ? "O marco e as guarnições vão até as faces acabadas." : "Posições e peitoril contam a partir das faces acabadas.")));
            panel.Children.Add(ToolChrome.Card(pos, "Posição na parede"));
            return panel;
        }

        // ---------------------------------------------------------------- Divisões
        private UIElement BuildDivisoes()
        {
            var panel = new StackPanel();

            // rows
            var rows = new StackPanel();
            for (int i = 0; i < _cfg.Linhas.Count; i++)
            {
                int r = i;
                var linha = _cfg.Linhas[r];
                var line = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };

                var tag = new TextBlock { Text = "Linha " + (r + 1), Width = 58, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
                tag.SetResourceReference(TextBlock.ForegroundProperty, r == _selRow ? "Ja.Accent" : "Ja.Ink");
                DockPanel.SetDock(tag, Dock.Left);
                line.Children.Add(tag);

                if (_cfg.Linhas.Count > 1)
                {
                    var del = SmallButton("✕", "Remover esta linha", () =>
                    {
                        _cfg.Linhas.RemoveAt(r);
                        RebuildEditor();
                        Refresh();
                    });
                    DockPanel.SetDock(del, Dock.Right);
                    line.Children.Add(del);
                }
                var counter = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 6, 0) };
                counter.Children.Add(SmallButton("−", "Um módulo a menos", () =>
                {
                    if (linha.Celulas.Count <= 1) return;
                    linha.Celulas.RemoveAt(linha.Celulas.Count - 1);
                    RebuildEditor();
                    Refresh();
                }));
                var count = new TextBlock { Text = linha.Celulas.Count + (linha.Celulas.Count == 1 ? " módulo" : " módulos"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0), MinWidth = 64, TextAlignment = TextAlignment.Center };
                count.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Text");
                counter.Children.Add(count);
                counter.Children.Add(SmallButton("+", "Dividir: um módulo a mais", () =>
                {
                    if (linha.Celulas.Count >= 8) return;
                    var copy = linha.Celulas[linha.Celulas.Count - 1].Clone();
                    copy.LarguraMm = 0;
                    linha.Celulas.Add(copy);
                    RebuildEditor();
                    Refresh();
                }));
                DockPanel.SetDock(counter, Dock.Right);
                line.Children.Add(counter);
                line.Children.Add(NumBox(linha.AlturaMm, v => linha.AlturaMm = v, 0, 6000, allowEmpty: true));
                rows.Children.Add(line);
            }
            var addRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 4) };
            addRow.Children.Add(ToolChrome.Ghost("+ Linha em cima", "Acrescenta uma bandeira acima", (s, e) =>
            {
                _cfg.Linhas.Insert(0, Linha.Of(Math.Min(400, _cfg.AlturaMm / 3), Celula.Of(Abertura.Fixo)));
                _selRow = 0; _selCol = 0;
                RebuildEditor(); Refresh();
            }));
            addRow.Children.Add(ToolChrome.Ghost("+ Linha embaixo", "Acrescenta uma linha abaixo", (s, e) =>
            {
                _cfg.Linhas.Add(Linha.Of(Math.Min(500, _cfg.AlturaMm / 3), Celula.Of(_cfg.IsPorta ? Abertura.Fixo : Abertura.MaximAr)));
                _selRow = _cfg.Linhas.Count - 1; _selCol = 0;
                RebuildEditor(); Refresh();
            }));
            rows.Children.Add(addRow);
            rows.Children.Add(ToolChrome.Muted("Linhas de cima para baixo. Altura do módulo inclui os perfis; deixe vazio para ocupar o restante.", 11.5));
            panel.Children.Add(ToolChrome.Card(rows, "Linhas e módulos"));

            // selected module
            var cell = SelectedCell();
            var linhaSel = _cfg.Linhas[_selRow];
            var cp = new StackPanel();
            if (linhaSel.Celulas.Count > 1)
                cp.Children.Add(Field("Largura", NumBox(cell.LarguraMm, v => cell.LarguraMm = v, 0, 12000, allowEmpty: true), "Vazio = divide o restante da linha."));

            var combo = new ComboBox();
            foreach (var a in Catalog.Aberturas) combo.Items.Add(Catalog.NomeAbertura(a));
            combo.SelectedIndex = Array.IndexOf(Catalog.Aberturas, cell.Abertura);
            combo.SelectionChanged += (s, e) =>
            {
                if (combo.SelectedIndex < 0) return;
                cell.Abertura = Catalog.Aberturas[combo.SelectedIndex];
                RebuildEditor();
                Refresh();
            };
            cp.Children.Add(Field("Abertura", combo, ExplainAbertura(cell.Abertura)));

            if (cell.Abertura == Abertura.Correr)
            {
                cp.Children.Add(Field("Movimento", Pills(new[] { "Todas correm", "Para os dois lados", "Recolhem p/ um lado" }, (int)cell.Modo, i =>
                    {
                        cell.Modo = (ModoCorrer)i;
                        if (cell.Modo == ModoCorrer.Central && cell.Folhas == 3) cell.Folhas = 4;
                        if (cell.Modo == ModoCorrer.Recolhe && cell.Folhas == 2) cell.Folhas = 3;
                        RebuildEditor();
                        Refresh();
                    }),
                    cell.Modo == ModoCorrer.Central ? "Abertura central: as folhas do meio correm para os dois lados, por cima das das pontas (fixas com 4 ou 6 folhas)."
                    : cell.Modo == ModoCorrer.Recolhe ? "Recolhível: uma folha fixa num lado e todas as outras, cada uma no seu trilho, recolhem sobre ela."
                    : "Folhas que deslizam em trilhos e se sobrepõem (as das pontas correm para o centro)."));
                int[] options = cell.Modo == ModoCorrer.Central ? new[] { 2, 4, 6 } : new[] { 2, 3, 4, 6 };
                cp.Children.Add(Field("Folhas", Pills(options.Select(o => o.ToString()).ToArray(), Math.Max(0, Array.IndexOf(options, cell.Folhas)), i => { cell.Folhas = options[i]; Refresh(); }),
                    cell.Modo == ModoCorrer.Recolhe ? "Uma folha fixa + as móveis; cada folha tem o seu trilho (o marco precisa de profundidade para todos)."
                    : cell.Modo == ModoCorrer.Central ? "2 folhas: correm para fora do vão (sobre a parede ou embutidas). 4 e 6: as das pontas ficam fixas."
                    : "3 folhas usam 3 trilhos."));
                if (cell.Modo == ModoCorrer.Recolhe)
                    cp.Children.Add(Field("Recolhem para", Pills(new[] { "Esquerda", "Direita" }, cell.RecolheDireita ? 1 : 0, i => { cell.RecolheDireita = i == 1; Refresh(); }),
                        "Lado da folha fixa, visto de fora."));
                cp.Children.Add(Check("Folhas externas em veneziana", cell.MistoVeneziana, v => cell.MistoVeneziana = v,
                    "Janela de dormitório: as folhas do trilho externo viram veneziana."));
            }
            cp.Children.Add(Field("Preenchimento", Pills(new[] { "Vidro", "Veneziana", "Painel cego" }, (int)cell.Preenchimento, i => { cell.Preenchimento = (Preenchimento)i; Refresh(); }), null));
            if (IsHinged(cell.Abertura))
                cp.Children.Add(Check("Abre para fora", cell.AbreParaFora, v => cell.AbreParaFora = v, "Padrão: abre para dentro (linhas tracejadas na vista externa)."));

            var applyAll = ToolChrome.Ghost("Aplicar a todos", "Copia abertura e preenchimento deste módulo para todos os outros", (s, e) =>
            {
                foreach (var l in _cfg.Linhas)
                    foreach (var c in l.Celulas)
                    {
                        if (ReferenceEquals(c, cell)) continue;
                        c.Abertura = cell.Abertura;
                        c.Preenchimento = cell.Preenchimento;
                        c.Folhas = cell.Folhas;
                        c.MistoVeneziana = cell.MistoVeneziana;
                        c.AbreParaFora = cell.AbreParaFora;
                        c.Modo = cell.Modo;
                        c.RecolheDireita = cell.RecolheDireita;
                    }
                RebuildEditor();
                Refresh();
            });
            panel.Children.Add(ToolChrome.Card(cp, $"Módulo selecionado · L{_selRow + 1} C{_selCol + 1}", applyAll));
            return panel;
        }

        private static bool IsHinged(Abertura a) =>
            a == Abertura.AbrirEsquerda || a == Abertura.AbrirDireita || a == Abertura.AbrirDupla;

        private static string ExplainAbertura(Abertura a)
        {
            switch (a)
            {
                case Abertura.Fixo: return "Sem folha: o vidro (ou veneziana/painel) fica direto no marco.";
                case Abertura.AbrirEsquerda: return "Folha de abrir com dobradiças à esquerda (vista de fora).";
                case Abertura.AbrirDireita: return "Folha de abrir com dobradiças à direita (vista de fora).";
                case Abertura.AbrirDupla: return "Duas folhas de abrir que se encontram no meio, sem montante.";
                case Abertura.MaximAr: return "Articulada em cima, projeta para fora.";
                case Abertura.Basculante: return "Gira num eixo horizontal; comum em banheiros.";
                case Abertura.Tombar: return "Articulada embaixo, tomba para dentro.";
                case Abertura.OsciloBatenteEsquerda:
                case Abertura.OsciloBatenteDireita: return "Abre como folha de abrir ou tomba para dentro para ventilar.";
                case Abertura.Pivotante: return "Gira num eixo vertical (pivô).";
                case Abertura.Correr: return "Folhas que deslizam em trilhos: todas correm, abrem para os dois lados (central) ou recolhem para um lado sobre uma fixa.";
                default: return null;
            }
        }

        private Celula SelectedCell()
        {
            ClampSelection();
            return _cfg.Linhas[_selRow].Celulas[_selCol];
        }

        private void ClampSelection()
        {
            _cfg.Normalized();
            _selRow = Math.Max(0, Math.Min(_selRow, _cfg.Linhas.Count - 1));
            _selCol = Math.Max(0, Math.Min(_selCol, _cfg.Linhas[_selRow].Celulas.Count - 1));
        }

        // ---------------------------------------------------------------- Perfil e vidro
        private UIElement BuildPerfil()
        {
            var panel = new StackPanel();

            var pp = new StackPanel();
            var perfis = new ComboBox();
            foreach (var p in Catalog.Perfis) perfis.Items.Add(p.Nome);
            var current = Catalog.Perfil(_cfg.PerfilId);
            perfis.SelectedIndex = Catalog.Perfis.ToList().IndexOf(current);
            perfis.SelectionChanged += (s, e) =>
            {
                if (perfis.SelectedIndex < 0) return;
                Catalog.ApplyPerfil(_cfg, Catalog.Perfis[perfis.SelectedIndex]);
                RebuildEditor();
                Refresh();
            };
            pp.Children.Add(Field("Linha", perfis, "Uf ≈ " + current.Uf.ToString("0.0", CultureInfo.CurrentCulture) + " W/m²K (valor típico). As medidas abaixo podem ser ajustadas."));
            pp.Children.Add(Field("Marco – face", NumBox(_cfg.MarcoFaceMm, v => _cfg.MarcoFaceMm = v, 10, 200), null));
            pp.Children.Add(Field("Marco – prof.", NumBox(_cfg.MarcoProfMm, v => _cfg.MarcoProfMm = v, 20, 300), null));
            pp.Children.Add(Field("Folha – face", NumBox(_cfg.FolhaFaceMm, v => _cfg.FolhaFaceMm = v, 10, 200), null));
            pp.Children.Add(Field("Folha – prof.", NumBox(_cfg.FolhaProfMm, v => _cfg.FolhaProfMm = v, 10, 200), null));
            pp.Children.Add(Field("Montante/travessa", NumBox(_cfg.TravessaMm, v => _cfg.TravessaMm = v, 10, 200), null));
            panel.Children.Add(ToolChrome.Card(pp, "Perfis"));

            var cores = new StackPanel();
            var sw = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            foreach (var c in Catalog.Cores)
            {
                bool sel = string.Equals(c.Hex, _cfg.CorHex, StringComparison.OrdinalIgnoreCase);
                var dot = new Border
                {
                    Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Margin = new Thickness(0, 0, 8, 8), Cursor = Cursors.Hand,
                    Background = new SolidColorBrush(ElevationRenderer.Hex(c.Hex, Colors.White)),
                    BorderThickness = new Thickness(sel ? 3 : 1), ToolTip = c.Nome
                };
                dot.SetResourceReference(Border.BorderBrushProperty, sel ? "Ja.Accent" : "Ja.Line");
                var cc = c;
                dot.MouseLeftButtonUp += (s, e) => { _cfg.CorId = cc.Id; _cfg.CorHex = cc.Hex; RebuildEditor(); Refresh(); FillCatalog(); };
                sw.Children.Add(dot);
            }
            cores.Children.Add(sw);
            var corAtual = Catalog.Cores.FirstOrDefault(c => string.Equals(c.Hex, _cfg.CorHex, StringComparison.OrdinalIgnoreCase));
            var hexBox = new TextBox { Text = _cfg.CorHex, MaxLength = 7 };
            hexBox.TextChanged += (s, e) =>
            {
                string h = hexBox.Text.Trim().TrimStart('#');
                bool ok = Regex.IsMatch(h, "^[0-9A-Fa-f]{6}$");
                MarkInvalid(hexBox, !ok);
                if (!ok) return;
                if (!string.Equals(h, _cfg.CorHex, StringComparison.OrdinalIgnoreCase))
                {
                    _cfg.CorHex = h.ToUpperInvariant();
                    var known = Catalog.Cores.FirstOrDefault(c => string.Equals(c.Hex, h, StringComparison.OrdinalIgnoreCase));
                    _cfg.CorId = known?.Id ?? "";
                    Refresh();
                }
            };
            cores.Children.Add(Field("Hex", hexBox, corAtual != null ? corAtual.Nome : "Cor personalizada (6 dígitos, ex.: 3F4448)."));
            panel.Children.Add(ToolChrome.Card(cores, "Cor dos perfis"));

            var vp = new StackPanel();
            var vidros = new ComboBox();
            foreach (var v in Catalog.Vidros) vidros.Items.Add(v.Nome);
            var vAtual = Catalog.Vidro(_cfg.VidroId);
            vidros.SelectedIndex = Catalog.Vidros.ToList().IndexOf(vAtual);
            vidros.SelectionChanged += (s, e) =>
            {
                if (vidros.SelectedIndex < 0) return;
                _cfg.VidroId = Catalog.Vidros[vidros.SelectedIndex].Id;
                RebuildEditor();
                Refresh();
            };
            vp.Children.Add(Field("Vidro", vidros, $"{Layout.Mm(vAtual.Espessura)} mm · Ug ≈ {vAtual.Ug.ToString("0.0", CultureInfo.CurrentCulture)} W/m²K" +
                                                   (Layout.UsesGlass(_cfg) ? "" : " · (esta esquadria não tem vidro)")));
            panel.Children.Add(ToolChrome.Card(vp, "Vidro"));
            return panel;
        }

        // ---------------------------------------------------------------- Detalhes
        private UIElement BuildDetalhes()
        {
            var panel = new StackPanel();

            if (_cfg.IsPorta)
            {
                // ---- soleira / filete
                var so = new StackPanel();
                so.Children.Add(Check("Soleira / filete sob a porta", _cfg.Soleira, v => { _cfg.Soleira = v; RebuildEditor(); }, null));
                if (_cfg.Soleira)
                {
                    so.Children.Add(Field("Tipo", Pills(new[] { "Soleira", "Filete" }, (int)_cfg.SoleiraTipo, i =>
                    {
                        _cfg.SoleiraTipo = (TipoSoleira)i;
                        if (i == 1 && _cfg.SoleiraLarguraMm <= 0) _cfg.SoleiraLarguraMm = 30;
                        if (i == 0 && _cfg.SoleiraLarguraMm > 0 && _cfg.SoleiraLarguraMm < 60) _cfg.SoleiraLarguraMm = 0;
                        RebuildEditor(); Refresh();
                    }), "Soleira: ocupa a espessura da parede. Filete: faixa estreita na troca de piso."));
                    so.Children.Add(Field("Código", TextInput(_cfg.SoleiraCodigo, v => _cfg.SoleiraCodigo = v, 20), "Vai para JA_SoleiraCodigo (etiquetas e quadro), ex.: SL01, FL02."));
                    so.Children.Add(Field("Material", MaterialInput(_cfg.SoleiraMaterial, v => _cfg.SoleiraMaterial = v),
                        "Escolha um material do projeto ou digite um nome: se existir no projeto, é ele que vale."));
                    so.Children.Add(Field("Cor", HexInput(_cfg.SoleiraCorHex, v => _cfg.SoleiraCorHex = v), "Cor do material novo (se ele ainda não existir no projeto)."));
                    so.Children.Add(Field("Comprimento", NumBox(_cfg.SoleiraComprimentoMm, v => _cfg.SoleiraComprimentoMm = v, 0, 15000, allowEmpty: true), "Vazio = entre os marcos."));
                    so.Children.Add(Field("Posição", Pills(new[] { "Centralizada", "Rente ao lado externo", "Rente ao lado interno" }, (int)_cfg.SoleiraAlinhamento,
                        i => { _cfg.SoleiraAlinhamento = (AlinhamentoSoleira)i; Refresh(); }),
                        "Rente: a borda da pedra alinha com a face externa ou interna da esquadria (marco)."));
                    so.Children.Add(Field("Largura", NumBox(_cfg.SoleiraLarguraMm, v => _cfg.SoleiraLarguraMm = v, 0, 1500, allowEmpty: true), "Vazio = espessura acabada da parede (com revestimentos)."));
                    so.Children.Add(Field("Espessura", NumBox(_cfg.SoleiraEspessuraMm, v => _cfg.SoleiraEspessuraMm = v, 3, 100), null));
                }
                panel.Children.Add(ToolChrome.Card(so, _cfg.Soleira ? _cfg.NomeSoleira : "Soleira / filete"));

                // ---- frame across the wall, stop, casings (not in curtain walls)
                if (!_cfg.ParedeCortina) {
                var mc = new StackPanel();
                mc.Children.Add(Check("Marco na espessura da parede", _cfg.MarcoParede, v => { _cfg.MarcoParede = v; RebuildEditor(); },
                    "Portas de madeira/batente: o marco ocupa a parede toda, como no detalhe em planta."));
                if (_cfg.MarcoParede)
                {
                    mc.Children.Add(Field("Batente (encosto)", NumBox(_cfg.BatenteMm, v => _cfg.BatenteMm = v, 0, 60), "Ressalto do marco onde a folha fecha. 0 = sem batente."));
                    mc.Children.Add(Check("Guarnições (alizares) nas duas faces", _cfg.Guarnicoes, v => { _cfg.Guarnicoes = v; RebuildEditor(); }, null));
                    if (_cfg.Guarnicoes)
                    {
                        mc.Children.Add(Field("Largura", NumBox(_cfg.GuarnicaoLarguraMm, v => _cfg.GuarnicaoLarguraMm = v, 10, 300), null));
                        mc.Children.Add(Field("Espessura", NumBox(_cfg.GuarnicaoEspessuraMm, v => _cfg.GuarnicaoEspessuraMm = v, 3, 60), null));
                    }
                }
                panel.Children.Add(ToolChrome.Card(mc, "Marco, batente e guarnições"));
                }
            }
            else if (!_cfg.ParedeCortina)
            {
                // ---- peitoril
                var pe = new StackPanel();
                pe.Children.Add(Check("Peitoril / pingadeira", _cfg.Peitoril, v => { _cfg.Peitoril = v; RebuildEditor(); }, "Pedra sob a janela; aparece em planta com as bordas do vão."));
                if (_cfg.Peitoril)
                {
                    pe.Children.Add(Field("Código", TextInput(_cfg.PeitorilCodigo, v => _cfg.PeitorilCodigo = v, 20), "Vai para JA_SoleiraCodigo (etiquetas e quadro), ex.: PE01."));
                    pe.Children.Add(Field("Material", MaterialInput(_cfg.PeitorilMaterial, v => _cfg.PeitorilMaterial = v),
                        "Escolha um material do projeto ou digite um nome: se existir no projeto, é ele que vale."));
                    pe.Children.Add(Field("Cor", HexInput(_cfg.PeitorilCorHex, v => _cfg.PeitorilCorHex = v), "Cor do material novo (se ele ainda não existir no projeto)."));
                    pe.Children.Add(Field("Projeção p/ fora", NumBox(_cfg.PeitorilProjecaoMm, v => _cfg.PeitorilProjecaoMm = v, 0, 200), "Além da face externa acabada (pingadeira). 0 = alinhado."));
                    pe.Children.Add(Field("Largura total", NumBox(_cfg.PeitorilLarguraMm, v => _cfg.PeitorilLarguraMm = v, 0, 1500, allowEmpty: true), "Da borda externa para dentro. Vazio = até a face interna da parede."));
                    pe.Children.Add(Field("Abas laterais", NumBox(_cfg.PeitorilAbaMm, v => _cfg.PeitorilAbaMm = v, 0, 200), "Quanto a pedra entra em cada lado do vão."));
                    pe.Children.Add(Field("Espessura", NumBox(_cfg.PeitorilEspessuraMm, v => _cfg.PeitorilEspessuraMm = v, 5, 100), null));
                    pe.Children.Add(Check("Sulco de pingadeira por baixo", _cfg.PeitorilPingadeira, v => { _cfg.PeitorilPingadeira = v; Refresh(); },
                        "Sulco de 8 mm a 15 mm da borda externa, sob a parte que fica para fora (precisa de 25 mm ou mais de projeção)."));
                }
                panel.Children.Add(ToolChrome.Card(pe, "Peitoril"));
            }
            if (!_cfg.ParedeCortina)
            {
                var cm = new StackPanel();
                cm.Children.Add(Check("Contramarco", _cfg.Contramarco, v => { _cfg.Contramarco = v; RebuildEditor(); },
                    "Requadro de alumínio chumbado na alvenaria, em volta do vão (fora da largura × altura). Corta a parede no lugar dele."));
                if (_cfg.Contramarco)
                {
                    cm.Children.Add(Field("Largura do perfil", NumBox(_cfg.ContramarcoLarguraMm, v => _cfg.ContramarcoLarguraMm = v, 5, 100), "Em cada lado do vão (20 mm usual)."));
                    cm.Children.Add(Field("Profundidade", NumBox(_cfg.ContramarcoProfMm, v => _cfg.ContramarcoProfMm = v, 10, 400, allowEmpty: true), "Vazio = a mesma do marco, centrado nele."));
                }
                panel.Children.Add(ToolChrome.Card(cm, "Contramarco"));
            }

            var ac = new StackPanel();
            if (_cfg.IsPorta)
            {
                ac.Children.Add(Check("Puxadores e fechos", _cfg.Puxadores, v => _cfg.Puxadores = v, "Aparecem só no nível de detalhe Alto (3D e elevações)."));
            }
            ac.Children.Add(Field("Especificação das ferragens", TextInput(_cfg.Ferragens, v => _cfg.Ferragens = v, 120),
                "Texto livre que vai para o Quadro de Esquadrias (JA_Ferragens). Ex.: fechadura rolete inox + puxador 40 cm; fecho concha e roldanas duplas."));
            panel.Children.Add(ToolChrome.Card(ac, "Ferragens"));

            var rep = new StackPanel();
            rep.Children.Add(Check("Linhas de abertura na elevação", _cfg.SimbolosElevacao, v => { _cfg.SimbolosElevacao = v; RebuildEditor(); },
                "Padrão do escritório: linhas saindo dos cantos do lado da dobradiça até o meio do lado que abre; correr com ▷ ◁; fixo com F."));
            if (_cfg.SimbolosElevacao)
            {
                rep.Children.Add(Field("Símbolo de correr", Pills(new[] { "Triângulos ▷◁", "Setas" }, (int)_cfg.ElevacaoCorrer, i => { _cfg.ElevacaoCorrer = (SimboloCorrer)i; Refresh(); }), null));
                rep.Children.Add(Check("Marcar vidros fixos com F", _cfg.MarcarFixo, v => _cfg.MarcarFixo = v, null));
                rep.Children.Add(Check("Tracejar quando abre para dentro", _cfg.TracejarParaDentro, v => _cfg.TracejarParaDentro = v, "Desligado (padrão): todas as linhas contínuas, como na relação de esquadrias."));
            }
            rep.Children.Add(Check("Nível Baixo simplificado", _cfg.SimplificarBaixo, v => _cfg.SimplificarBaixo = v,
                "Baixo: marco e vidros · Médio: + folhas e venezianas · Alto: + puxadores."));
            panel.Children.Add(ToolChrome.Card(rep, "Representação no Revit"));

            _bimText = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 19 };
            _bimText.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Text");
            panel.Children.Add(ToolChrome.Card(_bimText, "Dados BIM gerados"));
            RefreshBim();
            return panel;
        }

        // ---------------------------------------------------------------- Planta 2D
        private UIElement BuildPlanta2D()
        {
            var panel = new StackPanel();

            var main = new StackPanel();
            main.Children.Add(Check("Planta em 2D (como bloco de CAD)", _cfg.SimbolosPlanta, v => { _cfg.SimbolosPlanta = v; RebuildEditor(); },
                _cfg.IsPorta ? "Nas plantas do Revit os sólidos ficam ocultos e aparece este desenho. Desligado: o Revit mostra o corte do 3D."
                             : "Janelas: o desenho é o corte da janela a 1,20 m do piso (marco, folhas nos trilhos, vidros), sem o peitoril, e fica igual qualquer que seja o plano de corte da vista. Desligado: o Revit mostra o corte do 3D."));
            panel.Children.Add(ToolChrome.Card(main, "Representação em planta"));
            if (!_cfg.SimbolosPlanta) return panel;

            if (_cfg.IsPorta)
            {
                var fo = new StackPanel();
                double[] angles = { 90, 60, 45, 30 };
                int ai = Array.FindIndex(angles, a => Math.Abs(a - _cfg.PlantaAngulo) < 0.5);
                fo.Children.Add(Field("Ângulo de abertura", Pills(new[] { "90°", "60°", "45°", "30°" }, ai, i => { _cfg.PlantaAngulo = angles[i]; RebuildEditor(); Refresh(); }), null));
                fo.Children.Add(Field("Outro ângulo", DegBox(_cfg.PlantaAngulo, v => _cfg.PlantaAngulo = v), "Entre 15° e 180°."));
                fo.Children.Add(Field("Desenho da folha", Pills(new[] { "Retângulo", "Linha" }, (int)_cfg.PlantaFolha, i => { _cfg.PlantaFolha = (FolhaPlanta)i; Refresh(); }),
                    "Retângulo na espessura da folha, ou uma linha simples."));
                fo.Children.Add(Field("Folha começa", Pills(new[] { "Na face do marco", "No eixo da dobradiça" }, (int)_cfg.PlantaAlinhamento, i => { _cfg.PlantaAlinhamento = (AlinhamentoFolha)i; Refresh(); }),
                    "Na face do marco: alinhada com a estrutura mais externa (linha da parede acabada)."));
                fo.Children.Add(Check("Mostrar a folha fechada (tracejada)", _cfg.PlantaFolhaFechada, v => _cfg.PlantaFolhaFechada = v, null));
                panel.Children.Add(ToolChrome.Card(fo, "Folha da porta"));

                var ar = new StackPanel();
                ar.Children.Add(Pills(new[] { "Arco", "Arco tracejado", "Linha reta", "Sem arco" }, (int)_cfg.PlantaArco, i => { _cfg.PlantaArco = (ArcoPlanta)i; Refresh(); }));
                panel.Children.Add(ToolChrome.Card(ar, "Arco de abertura"));
            }

            var el = new StackPanel();
            if (_cfg.IsPorta) el.Children.Add(Check("Marco e batente", _cfg.PlantaMarco, v => _cfg.PlantaMarco = v, null));
            if (_cfg.Linhas.Any(l => l.Celulas.Any(x => x.Abertura == Abertura.Correr)))
                el.Children.Add(Check("Setas de correr ao lado das folhas", _cfg.PlantaSetasCorrer ?? _cfg.IsPorta, v => _cfg.PlantaSetasCorrer = v, null));
            if (_cfg.MarcoNaParede && _cfg.Guarnicoes) el.Children.Add(Check("Guarnições", _cfg.PlantaGuarnicoes, v => _cfg.PlantaGuarnicoes = v, null));
            if (_cfg.IsPorta && _cfg.Soleira) el.Children.Add(Check(_cfg.NomeSoleira, _cfg.PlantaPedra, v => _cfg.PlantaPedra = v, null));
            el.Children.Add(Check("Projeção da verga (tracejada)", _cfg.PlantaVerga, v => _cfg.PlantaVerga = v, "Linhas tracejadas nas faces acabadas, indicando a verga acima do corte."));
            panel.Children.Add(ToolChrome.Card(el, "O que aparece"));

            panel.Children.Add(ToolChrome.Card(ToolChrome.Muted(
                "Cada item acima também vira um parâmetro Sim/Não de instância na família (Propriedades › Gráficos, “Planta - …”): " +
                "dá para ligar e desligar porta a porta no Revit, sem gerar de novo. Ângulo, desenho da folha e tipo de arco são definidos aqui.", 11.5), "No Revit"));
            return panel;
        }

        /// <summary>Number box in degrees (same behaviour as NumBox).</summary>
        private FrameworkElement DegBox(double value, Action<double> set)
        {
            var tb = new TextBox { Text = value.ToString("0.#", CultureInfo.CurrentCulture), Width = 92, HorizontalAlignment = HorizontalAlignment.Left, HorizontalContentAlignment = HorizontalAlignment.Right };
            tb.TextChanged += (s, e) =>
            {
                bool ok = OptionsDialog.TryParse(tb.Text, out double v) && v >= 15 && v <= 180;
                MarkInvalid(tb, !ok);
                if (!ok) return;
                set(v);
                Refresh();
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(tb);
            var unit = ToolChrome.Muted("°", 11.5);
            unit.VerticalAlignment = VerticalAlignment.Center;
            unit.Margin = new Thickness(6, 0, 0, 0);
            row.Children.Add(unit);
            return row;
        }

        private FrameworkElement TextInput(string value, Action<string> set, int maxLength)
        {
            var tb = new TextBox { Text = value ?? "", MaxLength = maxLength };
            tb.TextChanged += (s, e) => { set(tb.Text.Trim()); RefreshBim(); };
            return tb;
        }

        // =========================================================================================
        // controls
        // =========================================================================================
        // the shared controls (FormKit), bound to this window
        private FrameworkElement MaterialInput(string value, Action<string> set) => FormKit.MaterialInput(_materials, value, m => { set(m); RefreshBim(); });
        private FrameworkElement HexInput(string value, Action<string> set) => FormKit.HexInput(value, h => { set(h); Refresh(); }, MarkInvalid);
        private FrameworkElement Check(string label, bool value, Action<bool> set, string hint) => FormKit.Check(label, value, v => { set(v); Refresh(); }, hint);
        private void MarkInvalid(TextBox tb, bool invalid) => FormKit.MarkInvalid(_invalid, tb, invalid);
        private void SetStatus(string text, bool error) => FormKit.SetStatus(_status, text, error);
        private void SetTab(int i, UIElement content) => FormKit.SetTab(_tabs, i, content);

        /// <summary>Highlighted note (soft accent box) inside a card.</summary>
        private static FrameworkElement Note(string text)
        {
            var box = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 0, 10) };
            box.SetResourceReference(Border.BackgroundProperty, "Ja.AccentSoft");
            var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 11.5 };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Ink");
            box.Child = t;
            return box;
        }

        /// <summary>Number box in mm: applies the value live when valid, red border otherwise.</summary>
        private FrameworkElement NumBox(double value, Action<double> set, double min, double max, bool allowEmpty = false)
        {
            var tb = new TextBox
            {
                Text = allowEmpty && value <= 0 ? "" : value.ToString("0.#", CultureInfo.CurrentCulture),
                Width = 92,
                HorizontalAlignment = HorizontalAlignment.Left,
                HorizontalContentAlignment = HorizontalAlignment.Right
            };
            tb.TextChanged += (s, e) =>
            {
                string t = tb.Text.Trim();
                if (allowEmpty && t.Length == 0) { MarkInvalid(tb, false); set(0); Refresh(); return; }
                bool ok = OptionsDialog.TryParse(t, out double v) && v >= min && v <= max;
                MarkInvalid(tb, !ok);
                tb.ToolTip = ok ? null : $"Use um número entre {min:0} e {max:0}.";
                if (!ok) return;
                set(v);
                Refresh();
            };
            tb.PreviewKeyDown += (s, e) =>
            {
                if (e.Key != Key.Up && e.Key != Key.Down) return;
                if (!OptionsDialog.TryParse(tb.Text, out double v)) v = 0;
                double step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 100 : 10;
                v = Math.Max(min, Math.Min(max, v + (e.Key == Key.Up ? step : -step)));
                tb.Text = v.ToString("0.#", CultureInfo.CurrentCulture);
                tb.CaretIndex = tb.Text.Length;
                e.Handled = true;
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            if (allowEmpty) row.Children.Add(new Border { Child = ToolChrome.SearchBox(tb, "auto"), Width = 92 });
            else row.Children.Add(tb);
            var unit = ToolChrome.Muted("mm", 11.5);
            unit.VerticalAlignment = VerticalAlignment.Center;
            unit.Margin = new Thickness(6, 0, 0, 0);
            row.Children.Add(unit);
            return row;
        }

        // =========================================================================================
        // state
        // =========================================================================================
        private bool ValidateCode()
        {
            if (_codeError == null) return true;
            string code = (_cfg.Codigo ?? "").Trim();
            string err = null;
            if (code.Length == 0) err = "Informe um código, ex.: " + (_cfg.IsPorta ? "P01" : "J01") + ".";
            else if (!Regex.IsMatch(code, @"^[A-Za-z0-9][A-Za-z0-9 ._\-]{0,19}$")) err = "Use letras, números, espaço, ponto, hífen ou sublinhado.";
            else if (!_editing && _usedCodes.Contains(code)) err = "Já existe a esquadria " + code + " neste projeto. Escolha outro código ou use Editar Família. Sugestão: " + ProjectEsquadrias.NextCode(_usedCodes, _cfg.Tipo) + ".";
            _codeError.Text = err ?? "";
            _codeError.Visibility = err == null ? Visibility.Collapsed : Visibility.Visible;
            return err == null;
        }

        private void Refresh()
        {
            if (_canvas == null) return;
            try { _layout = Layout.Build(_cfg); }
            catch (Exception ex)
            {
                SetStatus("Erro ao montar a prévia: " + ex.Message, true);
                return;
            }
            Draw();
            if (_sizeBadge != null)
                _sizeBadge.Text = Layout.Mm(_cfg.LarguraMm) + " × " + Layout.Mm(_cfg.AlturaMm) + " mm" + (_cfg.IsPorta ? "" : " · peitoril " + Layout.Mm(_cfg.PeitorilMm));

            _stats.Children.Clear();
            _stats.Children.Add(Stat("Uw ≈ " + _layout.Uw.ToString("0.0", CultureInfo.CurrentCulture) + " W/m²K", "Transmitância térmica estimada (EN ISO 10077-1, valores típicos)."));
            _stats.Children.Add(Stat("Iluminação " + _layout.AreaIluminacao.ToString("0.00", CultureInfo.CurrentCulture) + " m²", "Área envidraçada."));
            _stats.Children.Add(Stat("Ventilação " + _layout.AreaVentilacao.ToString("0.00", CultureInfo.CurrentCulture) + " m²", "Área que abre (estimativa: correr 2 e 4 folhas = 50%, 3 folhas = 2/3)."));
            _stats.Children.Add(Stat(_layout.NumFolhas + (_layout.NumFolhas == 1 ? " folha" : " folhas"), null));
            foreach (string a in _layout.Avisos.Distinct())
            {
                var t = new TextBlock { Text = "⚠ " + a, TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Margin = new Thickness(0, 4, 0, 0) };
                t.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Warn");
                _stats.Children.Add(new Border { Child = t, Width = double.NaN, MaxWidth = 900 });
            }
            RefreshBim();
        }

        private void Draw()
        {
            if (_canvas == null || _layout == null) return;
            var opts = new RenderOptions { SelRow = _selRow, SelCol = _selCol, Accent = _info.Accent };
            _frame = _planta
                ? ElevationRenderer.DrawPlan(_canvas, _cfg, _layout, opts, _canvas.ActualWidth, _canvas.ActualHeight)
                : ElevationRenderer.Draw(_canvas, _cfg, _layout, opts, _canvas.ActualWidth, _canvas.ActualHeight);
            _canvas.Cursor = _planta ? Cursors.Arrow : Cursors.Hand;
        }

        private void RefreshBim()
        {
            if (_bimText == null) return;
            var l = _layout ?? Layout.Build(_cfg);
            _bimText.Text =
                "Família: " + FamilyGenerator.SafeName(_cfg.FamilyName(_settings.PrefixoFamilia)) + "\n" +
                "Marca de tipo: " + _cfg.Codigo + "\n" +
                "Descrição: " + Layout.Descricao(_cfg) + "\n" +
                "JA_Uw: " + l.Uw.ToString("0.00", CultureInfo.CurrentCulture) + " W/m²K\n" +
                "JA_AreaIluminacao: " + l.AreaIluminacao.ToString("0.00", CultureInfo.CurrentCulture) + " m²\n" +
                "JA_AreaVentilacao: " + l.AreaVentilacao.ToString("0.00", CultureInfo.CurrentCulture) + " m²" +
                (l.Pedra.HasValue
                    ? "\n" + (_cfg.IsPorta ? _cfg.NomeSoleira : "Peitoril") + ": " + (_cfg.IsPorta ? _cfg.SoleiraCodigo : _cfg.PeitorilCodigo) + " · " +
                      (_cfg.IsPorta ? _cfg.SoleiraMaterial : _cfg.PeitorilMaterial) + " · " + Layout.Mm(l.Pedra.Value.W) + " × " + Layout.Mm(l.Pedra.Value.H) + " × " +
                      Layout.Mm(_cfg.IsPorta ? _cfg.SoleiraEspessuraMm : _cfg.PeitorilEspessuraMm) + " mm"
                    : "");
        }

        private void Finish(bool place)
        {
            if (_invalid.Count > 0)
            {
                SetStatus("Corrija os campos em vermelho antes de continuar.", true);
                return;
            }
            if (!ValidateCode())
            {
                _tabs.SelectedIndex = 0;
                SetStatus("Confira o código da esquadria.", true);
                return;
            }
            _result = new ConfiguratorResult { Config = _cfg.Clone(), Place = place };
            try { _w.DialogResult = true; } catch { _w.Close(); }
        }

    }
}
