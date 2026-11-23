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
    public sealed class PecaResult
    {
        public IPecaConfig Config;
        /// <summary>true = place (bancada/nicho) or apply to walls (chapim) right after generating.</summary>
        public bool Place;
    }

    public enum FontePeca { Modelos, Biblioteca, Projeto }

    /// <summary>
    /// Configurator of bancadas, nichos and chapins: same layout and language as the esquadrias
    /// configurator (catalogue · live preview · tabs · footer with numbers and warnings).
    ///   preview – 3D, Planta and Frente/Elevação/Corte, drawn from the same pieces the family gets;
    ///   tabs    – depend on the kind (Bancada: Medidas · Bordas · Cubas · Materiais;
    ///             Nicho: Medidas · Interior · Pedra · Materiais; Chapim: Medidas · Perfil · Materiais).
    /// </summary>
    public sealed class PecaWindow
    {
        private sealed class CatalogItem
        {
            public string Name, Desc;
            public IPecaConfig Config;
            public ModeloPeca Modelo;
            public string LibraryPath;
        }

        private readonly Window _w;
        private readonly ToolInfo _info;
        private readonly bool _editing;
        private readonly TipoPeca _tipo;
        private readonly HashSet<string> _usedCodes;
        private readonly List<KeyValuePair<string, IPecaConfig>> _projectItems;
        private readonly Settings _settings;
        private readonly List<string> _materials;
        private readonly HashSet<TextBox> _invalid = new HashSet<TextBox>();
        private IPecaConfig _cfg;
        private PecaModelo _model;
        private FontePeca _fonte;
        private VistaPeca _vista = VistaPeca.Iso;
        private PecaResult _result;

        private Canvas _canvas;
        private StackPanel _catalogList;
        private TextBox _search;
        private TabControl _tabs;
        private WrapPanel _stats;
        private TextBlock _sizeBadge, _status, _bimText, _codeError;

        public static PecaResult Show(IntPtr owner, string toolTitle, IPecaConfig start, bool editing, HashSet<string> usedCodes,
                                      List<KeyValuePair<string, IPecaConfig>> projectItems, IList<string> materials, FontePeca fonte = FontePeca.Modelos)
        {
            var pw = new PecaWindow(toolTitle, start, editing, usedCodes, projectItems, materials, fonte);
            if (owner != IntPtr.Zero) new WindowInteropHelper(pw._w).Owner = owner;
            pw._w.ShowDialog();
            return pw._result;
        }

        private PecaWindow(string toolTitle, IPecaConfig start, bool editing, HashSet<string> usedCodes,
                           List<KeyValuePair<string, IPecaConfig>> projectItems, IList<string> materials, FontePeca fonte)
        {
            _info = ToolCatalog.Get(toolTitle);
            _settings = Settings.Current;
            _cfg = start.Copiar();
            _tipo = _cfg.Tipo;
            _editing = editing;
            _usedCodes = usedCodes ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _projectItems = projectItems ?? new List<KeyValuePair<string, IPecaConfig>>();
            _materials = (materials ?? new List<string>()).Where(m => !string.IsNullOrWhiteSpace(m)).Distinct().OrderBy(m => m, StringComparer.CurrentCultureIgnoreCase).ToList();
            _fonte = fonte;
            if (_tipo == TipoPeca.Nicho) _vista = VistaPeca.Frente;

            _w = NewConfiguratorWindow();
            var body = BuildBody();
            var footer = BuildFooter();
            ToolChrome.Dress(_w, toolTitle, body, footer);
            OnCtrlEnter(_w, () => Finish(!_editing));
            _w.Loaded += (s, e) => { FillCatalog(); Refresh(); };
            RebuildEditor();
        }

        private string Nome => PecaUtil.NomeTipo(_tipo);
        private string NomeMin => Nome.ToLowerInvariant();

        // =========================================================================================
        // layout
        // =========================================================================================
        private string[] TabNames()
        {
            switch (_tipo)
            {
                case TipoPeca.Bancada: return new[] { "Medidas", "Bordas", "Cubas", "Materiais" };
                case TipoPeca.Nicho: return new[] { "Medidas", "Interior", "Pedra", "Materiais" };
                case TipoPeca.Ralo: return new[] { "Medidas", "Grelha", "Saídas", "Materiais" };
                case TipoPeca.Pedra: return new[] { "Medidas", "Materiais" };
                default: return new[] { "Medidas", "Perfil", "Materiais" };
            }
        }

        private UIElement BuildBody()
        {
            var g = CatalogPreviewGrid(BuildCatalog(), BuildPreview());

            _tabs = new TabControl();
            foreach (string h in TabNames()) _tabs.Items.Add(new TabItem { Header = h });
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
            foreach (FontePeca f in Enum.GetValues(typeof(FontePeca)))
            {
                string label = f == FontePeca.Projeto ? "No projeto" : f.ToString();
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
            return ToolChrome.Card(dock, "Modelos");
        }

        private Border BuildPreview()
        {
            var badge = ToolChrome.Badge(out _sizeBadge);
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            var vista = Pills(new[] { PecaRenderer.NomeVista(_tipo, VistaPeca.Iso), PecaRenderer.NomeVista(_tipo, VistaPeca.Planta), PecaRenderer.NomeVista(_tipo, VistaPeca.Frente) },
                              (int)_vista, i => { _vista = (VistaPeca)i; Draw(); });
            vista.Margin = new Thickness(0, 0, 8, -6);
            head.Children.Add(vista);
            head.Children.Add(badge);
            var dock = new DockPanel();

            _stats = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            DockPanel.SetDock(_stats, Dock.Bottom);
            dock.Children.Add(_stats);

            string hint = _tipo == TipoPeca.Bancada ? "Planta e frente vistas de quem usa a bancada; a parede e o piso são só referência."
                        : _tipo == TipoPeca.Nicho ? "Elevação vista do ambiente; a parede é só referência (o vazio do nicho corta a parede real)."
                        : _tipo == TipoPeca.Ralo ? "O topo da grelha fica no piso acabado (origem da família); corpo e tubos abaixo. Saídas e entradas são conectores de tubo."
                        : "Corte com o exterior à direita; o comprimento real acompanha cada muro.";
            var h = ToolChrome.Muted(hint, 11.5);
            h.Margin = new Thickness(0, 6, 0, 0);
            DockPanel.SetDock(h, Dock.Bottom);
            dock.Children.Add(h);

            _canvas = new Canvas { Background = Brushes.White, ClipToBounds = true };
            _canvas.SizeChanged += (s, e) => Draw();
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
            if (!_editing)
            {
                var onlyFamily = ToolChrome.Secondary("Só gerar a família", (s, e) => Finish(false),
                    "Gera e carrega a família no projeto sem posicionar (fica no Navegador de Projeto › Famílias › " + PecaUtil.Categoria(_tipo) + ").");
                onlyFamily.Margin = new Thickness(0, 0, 8, 0);
                right.Children.Add(onlyFamily);
            }
            string label = _editing ? "Atualizar " + NomeMin : _tipo == TipoPeca.Chapim ? "Gerar e aplicar nos muros" : "Gerar e posicionar";
            var primary = ToolChrome.Primary(label, (s, e) => Finish(!_editing));
            primary.MinWidth = 150;
            primary.ToolTip = _editing ? "Regera a família; todas as cópias deste tipo no modelo são atualizadas. (Ctrl+Enter)"
                            : _tipo == TipoPeca.Chapim ? "Gera a família e aplica no topo das paredes selecionadas (ou que você clicar). (Ctrl+Enter)"
                            : _tipo == TipoPeca.Nicho ? "Gera a família e deixa você clicar nas paredes; o nicho corta a parede. Esc termina. (Ctrl+Enter)"
                            : "Gera a família e deixa você clicar onde colocar (Espaço gira antes de clicar). Esc termina. (Ctrl+Enter)";
            right.Children.Add(primary);
            dock.Children.Add(right);

            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(ToolChrome.Ghost("Salvar na biblioteca…", "Guarda esta peça para usar em outros projetos", (s, e) => SaveToLibrary()));
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
                case FontePeca.Modelos:
                    foreach (var m in PecaCatalog.DoTipo(_tipo))
                        list.Add(new CatalogItem { Name = m.Nome, Desc = m.Descricao, Modelo = m, Config = m.Aplicar(_cfg) });
                    break;
                case FontePeca.Biblioteca:
                    foreach (var it in PecaLibrary.List(_settings, _tipo))
                        list.Add(new CatalogItem { Name = it.Name, Desc = it.Config.Dimensoes(), Config = it.Config, LibraryPath = it.Path });
                    break;
                case FontePeca.Projeto:
                    foreach (var kv in _projectItems.Where(k => k.Value.Tipo == _tipo))
                        list.Add(new CatalogItem { Name = kv.Key, Desc = kv.Value.Dimensoes(), Config = kv.Value });
                    break;
            }
            string q = Plain(_search?.Text);
            if (q.Length > 0) list = list.Where(i => Plain(i.Name + " " + i.Desc).Contains(q)).ToList();
            return list;
        }

        private void FillCatalog()
        {
            if (_catalogList == null) return;
            _catalogList.Children.Clear();
            var items = Items();
            if (items.Count == 0)
            {
                string msg;
                if (_search != null && _search.Text.Trim().Length > 0) msg = "Nada encontrado para esta busca.";
                else if (_fonte == FontePeca.Biblioteca) msg = "Nenhuma " + NomeMin + " salva ainda (" + PecaUtil.Plural(_tipo).ToLowerInvariant() +
                                                               "). Monte uma e use “Salvar na biblioteca…”, no rodapé.\n\nPasta: " + PecaLibrary.Folder(_settings, _tipo);
                else msg = "Nenhum(a) " + NomeMin + " do JA Famílias neste projeto ainda.";
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
            try { PecaRenderer.Draw(thumb, it.Config, _tipo == TipoPeca.Chapim ? VistaPeca.Frente : VistaPeca.Iso, new PecaRenderOptions { Dimensions = false, Context = _tipo == TipoPeca.Nicho }, 62, 58); }
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
            IPecaConfig next = it.Modelo != null ? it.Modelo.Aplicar(_cfg) : it.Config.Copiar();
            if (_editing) next.Codigo = _cfg.Codigo;
            else if (it.Modelo == null && _usedCodes.Contains(next.Codigo))
                next.Codigo = !_usedCodes.Contains(_cfg.Codigo) ? _cfg.Codigo : ProjectPecas.NextCode(_usedCodes, _tipo);
            _cfg = next;
            SetStatus(it.Modelo != null ? "Modelo aplicado: " + it.Name : "Carregado: " + it.Name, false);
            RebuildEditor();
            Refresh();
            FillCatalog();
        }

        private void SaveToLibrary()
        {
            string suggestion = string.IsNullOrWhiteSpace(_cfg.Nome) ? Nome + " " + _cfg.Dimensoes().Split(',')[0] : _cfg.Nome.Trim();
            string name = Prompt.Ask(_w, "Salvar na biblioteca", "Nome (fica em " + PecaLibrary.Folder(_settings, _tipo) + "):", suggestion, _info.Accent);
            if (name == null) return;
            try
            {
                PecaLibrary.Save(_settings, name, _cfg);
                SetStatus("Salvo na biblioteca: " + name, false);
                if (_fonte == FontePeca.Biblioteca) FillCatalog();
            }
            catch (Exception ex) { SetStatus("Não foi possível salvar: " + ex.Message, true); }
        }

        // =========================================================================================
        // editor
        // =========================================================================================
        private void RebuildEditor()
        {
            _invalid.Clear();
            int idx = Math.Max(0, _tabs.SelectedIndex);
            _bimText = null;
            switch (_cfg)
            {
                case BancadaConfig b:
                    SetTab(0, BancadaMedidas(b));
                    SetTab(1, BancadaBordas(b));
                    SetTab(2, BancadaCubas(b));
                    SetTab(3, Materiais());
                    break;
                case NichoConfig n:
                    SetTab(0, NichoMedidas(n));
                    SetTab(1, NichoInterior(n));
                    SetTab(2, NichoPedra(n));
                    SetTab(3, Materiais());
                    break;
                case ChapimConfig c:
                    SetTab(0, ChapimMedidas(c));
                    SetTab(1, ChapimPerfil(c));
                    SetTab(2, Materiais());
                    break;
                case PedraConfig pd:
                    SetTab(0, PedraMedidas(pd));
                    SetTab(1, Materiais());
                    break;
                case RaloConfig r:
                    SetTab(0, RaloMedidas(r));
                    SetTab(1, RaloGrelha(r));
                    SetTab(2, RaloSaidas(r));
                    SetTab(3, Materiais());
                    break;
            }
            _tabs.SelectedIndex = Math.Min(idx, _tabs.Items.Count - 1);
        }

        private FrameworkElement Identificacao()
        {
            var id = new StackPanel();
            var code = new TextBox { Text = _cfg.Codigo, IsReadOnly = _editing, MaxLength = 20 };
            _codeError = new TextBlock { Foreground = ErrorBrush, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
            code.TextChanged += (s, e) => { _cfg.Codigo = code.Text.Trim(); ValidateCode(); RefreshBim(); };
            var codeBox = new StackPanel();
            codeBox.Children.Add(code);
            codeBox.Children.Add(_codeError);
            id.Children.Add(Field("Código", codeBox, _editing
                ? "O código identifica a família no projeto e não muda na edição."
                : "Marca de tipo e nome da família (" + PecaUtil.FamilyName(_cfg) + ")."));
            var nome = new TextBox { Text = _cfg.Nome ?? "" };
            nome.TextChanged += (s, e) => { _cfg.Nome = nome.Text; RefreshBim(); };
            id.Children.Add(Field("Descrição", nome, "Opcional, ex.: " + (_tipo == TipoPeca.Bancada ? "Bancada da cozinha" : _tipo == TipoPeca.Nicho ? "Nicho do box" : _tipo == TipoPeca.Ralo ? "Ralo linear do box" : "Chapim da platibanda") + ". Vai para a Descrição."));
            ValidateCode();
            return ToolChrome.Card(id, "Identificação");
        }

        // ---------------------------------------------------------------- bancada
        private UIElement BancadaMedidas(BancadaConfig b)
        {
            var panel = new StackPanel();
            panel.Children.Add(Identificacao());

            var fmt = new StackPanel();
            fmt.Children.Add(Field("Formato", Pills(new[] { "Reta", "Em L", "Em U" }, (int)b.Formato, i => { b.Formato = (FormatoBancada)i; RebuildEditor(); Refresh(); }), null));
            if (b.Formato == FormatoBancada.L)
                fmt.Children.Add(Field("Retorno", Pills(new[] { "À esquerda", "À direita" }, (int)b.Lado, i => { b.Lado = (LadoRetorno)i; RebuildEditor(); Refresh(); }), "Lado do retorno visto de frente para a bancada."));
            string la = b.Formato == FormatoBancada.Reta ? "Comprimento" : b.Formato == FormatoBancada.L ? "Trecho principal (A)" : "Fundo (A)";
            fmt.Children.Add(Field(la, NumBox(b.ComprimentoAMm, v => b.ComprimentoAMm = v, 300, 12000), b.Formato == FormatoBancada.Reta ? null : "Medido pela parede do fundo, de ponta a ponta."));
            if (b.Formato == FormatoBancada.L)
            {
                fmt.Children.Add(Field("Retorno (B)", NumBox(b.ComprimentoBMm, v => b.ComprimentoBMm = v, 300, 8000), "Medido pela parede lateral, a partir da parede do fundo."));
                fmt.Children.Add(Field("Ângulo do retorno", NumBox(b.AnguloBGraus, v => b.AnguloBGraus = v, 30, 170, unit: "°"),
                    "Ângulo interno entre o trecho principal e o retorno. 90° = esquadro; maior abre, menor fecha."));
            }
            if (b.Formato == FormatoBancada.U)
            {
                fmt.Children.Add(Field("Lado esquerdo (B)", NumBox(b.ComprimentoBMm, v => b.ComprimentoBMm = v, 300, 8000), null));
                fmt.Children.Add(Field("Lado direito (C)", NumBox(b.ComprimentoCMm, v => b.ComprimentoCMm = v, 300, 8000), null));
                fmt.Children.Add(Field("Ângulo do lado esquerdo", NumBox(b.AnguloBGraus, v => b.AnguloBGraus = v, 30, 170, unit: "°"), "Ângulo interno com o fundo. 90° = esquadro."));
                fmt.Children.Add(Field("Ângulo do lado direito", NumBox(b.AnguloCGraus, v => b.AnguloCGraus = v, 30, 170, unit: "°"), null));
            }
            panel.Children.Add(ToolChrome.Card(fmt, "Formato"));

            var dim = new StackPanel();
            dim.Children.Add(Field(b.Formato == FormatoBancada.Reta ? "Profundidade" : "Profundidade do trecho A", NumBox(b.ProfundidadeMm, v => b.ProfundidadeMm = v, 200, 1500), "Da parede até a borda da frente."));
            if (b.Formato == FormatoBancada.L)
                dim.Children.Add(Field("Profundidade do retorno (B)", NumBox(b.ProfundidadeBMm, v => b.ProfundidadeBMm = v, 200, 1500, allowEmpty: true), "Vazio = igual ao trecho A."));
            if (b.Formato == FormatoBancada.U)
            {
                dim.Children.Add(Field("Profundidade do lado esquerdo (B)", NumBox(b.ProfundidadeBMm, v => b.ProfundidadeBMm = v, 200, 1500, allowEmpty: true), "Vazio = igual ao fundo (A)."));
                dim.Children.Add(Field("Profundidade do lado direito (C)", NumBox(b.ProfundidadeCMm, v => b.ProfundidadeCMm = v, 200, 1500, allowEmpty: true), null));
            }
            dim.Children.Add(Field("Espessura da pedra", NumBox(b.EspessuraMm, v => b.EspessuraMm = v, 10, 100), "Granito/mármore 20 ou 30 mm; quartzo 20 mm."));
            dim.Children.Add(Field("Altura", NumBox(b.AlturaMm, v => b.AlturaMm = v, 300, 1500), "Do piso ao topo do tampo (cozinha 900, banheiro 800–850)."));
            panel.Children.Add(ToolChrome.Card(dim, "Dimensões"));

            // dry / wet areas: the wet area is a stretch of the main run, lowered (rebaixo)
            var mol = new StackPanel();
            mol.Children.Add(Check("Área molhada (o resto da bancada é área seca)", b.TemAreaMolhada, v =>
            {
                if (v)
                {
                    // around the first sink, or the left 1,2 m
                    var cu = b.Cubas.FirstOrDefault();
                    double cx = cu == null ? 0 : cu.PosicaoXMm > 0 ? cu.PosicaoXMm : b.ComprimentoAMm / 2;
                    b.MolhadaDeMm = cu == null ? 0 : Math.Max(0, Math.Round(cx - cu.LarguraMm / 2 - 300));
                    b.MolhadaAteMm = cu == null ? Math.Min(b.ComprimentoAMm, 1200) : Math.Min(b.ComprimentoAMm, Math.Round(cx + cu.LarguraMm / 2 + 300));
                }
                else { b.MolhadaDeMm = 0; b.MolhadaAteMm = 0; }
                RebuildEditor();
            }, "Divide o tampo em área molhada (cuba, escorredor) e área seca. A área de cada uma vai para o resumo e para o quadro."));
            if (b.TemAreaMolhada)
            {
                mol.Children.Add(Field("Começa em", NumBox(b.MolhadaDeMm, v => b.MolhadaDeMm = v, 0, 12000, true), "A partir da ponta esquerda do trecho principal (A). Vazio = desde a ponta."));
                mol.Children.Add(Field("Termina em", NumBox(b.MolhadaAteMm, v => b.MolhadaAteMm = v, 60, 12000), "Igual ou maior que o comprimento = até a ponta direita."));
                mol.Children.Add(Field("Rebaixo", NumBox(b.RebaixoMm, v => b.RebaixoMm = v, 0, 100, true), "Quanto a área molhada fica abaixo da seca (10 a 20 mm usual). Vazio ou 0 = mesmo nível, só dividida."));
                mol.Children.Add(Field("Borda de contenção", NumBox(b.ContencaoMm, v => b.ContencaoMm = v, 0, 200, true),
                    "Largura da borda que volta ao nível da área seca nas bordas livres da área molhada (segura a água). Vazio = sem borda. Com tento ativo, o tento faz esse papel."));
            }
            panel.Children.Add(ToolChrome.Card(mol, "Áreas seca e molhada"));

            var enc = new StackPanel();
            string pe = b.Formato == FormatoBancada.U ? "Ponta do lado esquerdo" : b.Formato == FormatoBancada.L && b.Lado == LadoRetorno.Esquerda ? "Ponta do retorno" : "Ponta esquerda";
            string pd = b.Formato == FormatoBancada.U ? "Ponta do lado direito" : b.Formato == FormatoBancada.L && b.Lado == LadoRetorno.Direita ? "Ponta do retorno" : "Ponta direita";
            enc.Children.Add(Check("Fundo livre — ilha (saia dos dois lados)", b.FundoLivre, v => { b.FundoLivre = v; RebuildEditor(); },
                b.Formato == FormatoBancada.Reta ? "O fundo deixa de encostar na parede: recebe saia/tira, tento e chanfro como a frente, e não leva frontão."
                : "O fundo e os lados de parede dos retornos ficam livres: saia/tira dos dois lados, sem frontão."));
            enc.Children.Add(Check(pe + " encosta numa parede", b.EncostoEsquerdo, v => b.EncostoEsquerdo = v, null));
            enc.Children.Add(Check(pd + " encosta numa parede", b.EncostoDireito, v => b.EncostoDireito = v, null));
            enc.Children.Add(ToolChrome.Muted("Bordas encostadas recebem frontão (se ativo) em vez de saia/tento.", 11));
            panel.Children.Add(ToolChrome.Card(enc, "Encostos"));

            var pc = new StackPanel();
            pc.Children.Add(Field(pe + " · ângulo", NumBox(b.PontaEsqGraus, v => b.PontaEsqGraus = v, 15, 165, unit: "°"),
                "Corte da ponta em planta, ângulo interno do lado da parede. 90° = reta; menor: a frente fica mais curta; maior: a frente avança."));
            pc.Children.Add(Field(pd + " · ângulo", NumBox(b.PontaDirGraus, v => b.PontaDirGraus = v, 15, 165, unit: "°"), null));
            pc.Children.Add(Field("Chanfro do canto esquerdo", NumBox(b.ChanfroCantoEsqMm, v => b.ChanfroCantoEsqMm = v, 0, 1000, allowEmpty: true),
                "Corte reto no canto da frente, medido em cada lado. Vazio ou 0 = canto vivo. Só em pontas livres."));
            pc.Children.Add(Field("Chanfro do canto direito", NumBox(b.ChanfroCantoDirMm, v => b.ChanfroCantoDirMm = v, 0, 1000, allowEmpty: true), null));
            panel.Children.Add(ToolChrome.Card(pc, "Pontas e cantos"));
            return panel;
        }

        private UIElement BancadaBordas(BancadaConfig b)
        {
            var panel = new StackPanel();
            var borda = new StackPanel();
            borda.Children.Add(Field("Acabamento", Pills(new[] { "Reta", "Saia", "Tira americana" }, (int)b.Borda, i => { b.Borda = (BordaFrontal)i; RebuildEditor(); Refresh(); }),
                b.Borda == BordaFrontal.Saia ? "Faixa de pedra vertical sob a borda livre (aparenta tampo mais grosso)."
                : b.Borda == BordaFrontal.TiraAmericana ? "Tira horizontal colada por baixo na borda (engrossa o tampo, peça em L)." : "Só a espessura da pedra."));
            if (b.Borda != BordaFrontal.Reta)
                borda.Children.Add(Field(b.Borda == BordaFrontal.Saia ? "Altura da saia" : "Altura da tira", NumBox(b.BordaAlturaMm, v => b.BordaAlturaMm = v, 5, 400), "Abaixo do tampo."));
            if (b.Borda == BordaFrontal.TiraAmericana)
                borda.Children.Add(Field("Largura da tira", NumBox(b.TiraLarguraMm, v => b.TiraLarguraMm = v, 10, 200), null));
            if (b.Borda == BordaFrontal.Saia)
                borda.Children.Add(Check("Saia em meia-esquadria (junta a 45°)", b.MeiaEsquadria, v => b.MeiaEsquadria = v,
                    "Informação de fabricação: entra na descrição, no quadro e no relatório."));
            panel.Children.Add(ToolChrome.Card(borda, "Borda frontal"));

            var pf = new StackPanel();
            pf.Children.Add(Field("Aresta de cima", Pills(new[] { "Reta", "Chanfrada" }, (int)b.Perfil, i => { b.Perfil = (PerfilBorda)i; RebuildEditor(); Refresh(); }),
                b.Perfil == PerfilBorda.Chanfrada ? "Chanfro na quina de cima das bordas livres (no tento, se houver)." : "Quina viva."));
            if (b.Perfil == PerfilBorda.Chanfrada)
            {
                pf.Children.Add(Field("Tamanho do chanfro", NumBox(b.ChanfroBordaMm, v => b.ChanfroBordaMm = v, 1, 50), "Medido no topo, da borda para dentro."));
                pf.Children.Add(Field("Ângulo do chanfro", NumBox(b.ChanfroBordaGraus, v => b.ChanfroBordaGraus = v, 10, 80, unit: "°"), "45° = chanfro igual no topo e na frente."));
            }
            panel.Children.Add(ToolChrome.Card(pf, "Perfil da borda"));

            var tento = new StackPanel();
            tento.Children.Add(Check("Tento (ressalto) nas bordas livres", b.Tento, v => { b.Tento = v; RebuildEditor(); }, "Segura a água sobre a bancada (ex.: área de serviço)."));
            if (b.Tento)
            {
                tento.Children.Add(Field("Altura do tento", NumBox(b.TentoAlturaMm, v => b.TentoAlturaMm = v, 3, 100), null));
                tento.Children.Add(Field("Largura do tento", NumBox(b.TentoLarguraMm, v => b.TentoLarguraMm = v, 5, 100), null));
            }
            panel.Children.Add(ToolChrome.Card(tento, "Tento"));

            var fr = new StackPanel();
            fr.Children.Add(Check("Frontão (rodabancada)", b.Frontao, v => { b.Frontao = v; RebuildEditor(); }, "Faixa de pedra na parede, sobre o tampo."));
            if (b.Frontao)
            {
                fr.Children.Add(Field("Altura", NumBox(b.FrontaoAlturaMm, v => b.FrontaoAlturaMm = v, 10, 2000), "Acima do tampo (100 mm usual; até o armário para revestir a parede)."));
                fr.Children.Add(Field("Espessura", NumBox(b.FrontaoEspessuraMm, v => b.FrontaoEspessuraMm = v, 5, 60), null));
                fr.Children.Add(Check("Também nas pontas encostadas", b.FrontaoLaterais, v => { b.FrontaoLaterais = v; RebuildEditor(); }, null));
            }
            panel.Children.Add(ToolChrome.Card(fr, "Frontão"));

            // per run and side: saia / tira and frontão
            if (b.Borda != BordaFrontal.Reta || b.Frontao)
            {
                var tr = new StackPanel();
                string pe0 = b.Formato == FormatoBancada.U ? "Ponta do lado esquerdo" : b.Formato == FormatoBancada.L && b.Lado == LadoRetorno.Esquerda ? "Ponta do retorno" : "Ponta esquerda";
                string pd0 = b.Formato == FormatoBancada.U ? "Ponta do lado direito" : b.Formato == FormatoBancada.L && b.Lado == LadoRetorno.Direita ? "Ponta do retorno" : "Ponta direita";
                var trechos = new List<(string k, string nome)>();
                if (b.Formato == FormatoBancada.Reta) trechos.Add(("A", "Bancada"));
                else if (b.Formato == FormatoBancada.L) { trechos.Add(("A", "Trecho principal (A)")); trechos.Add(("B", "Retorno (B)")); }
                else { trechos.Add(("A", "Fundo (A)")); trechos.Add(("B", "Lado esquerdo (B)")); trechos.Add(("C", "Lado direito (C)")); }
                string saia = b.Borda == BordaFrontal.Saia ? "saia" : "tira";
                foreach (var (k, nome) in trechos)
                {
                    var linha = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
                    linha.Children.Add(new TextBlock { Text = nome, Width = 170, VerticalAlignment = VerticalAlignment.Center });
                    string kk = k;
                    if (b.Borda != BordaFrontal.Reta)
                        linha.Children.Add(Check("Frente com " + saia, b.TemBorda(kk), v => { b.Marcar(b.SemBorda, kk, v); Refresh(); }, null));
                    if (b.Frontao)
                        linha.Children.Add(Check(kk == "A" ? "Frontão no fundo" : "Frontão na parede", b.TemFrontao(kk), v => { b.Marcar(b.SemFrontao, kk, v); Refresh(); }, null));
                    tr.Children.Add(linha);
                }
                if (b.FundoLivre && b.Borda != BordaFrontal.Reta)
                    tr.Children.Add(Check("Fundo (livre) com " + saia, b.TemBorda("Fundo"), v => { b.Marcar(b.SemBorda, "Fundo", v); Refresh(); }, null));
                foreach (var (k, nome, encosta) in new[] { ("Esq", pe0, b.EncostoEsquerdo), ("Dir", pd0, b.EncostoDireito) })
                {
                    string kk = k;
                    if (!encosta && b.Borda != BordaFrontal.Reta)
                        tr.Children.Add(Check(nome + " (livre) com " + saia, b.TemBorda(kk), v => { b.Marcar(b.SemBorda, kk, v); Refresh(); }, null));
                    if (encosta && b.Frontao && b.FrontaoLaterais)
                        tr.Children.Add(Check(nome + " (encostada) com frontão", b.TemFrontao(kk), v => { b.Marcar(b.SemFrontao, kk, v); Refresh(); }, null));
                }
                tr.Children.Add(ToolChrome.Muted("Desmarque para tirar a " + (b.Borda == BordaFrontal.Reta ? "peça" : saia) + " ou o frontão só daquele trecho ou lado.", 11));
                panel.Children.Add(ToolChrome.Card(tr, "Por trecho e lado"));
            }

            var pes = new StackPanel();
            pes.Children.Add(Check("Pé de pedra na ponta esquerda", b.PeEsquerdo, v => { b.PeEsquerdo = v; RebuildEditor(); }, null));
            pes.Children.Add(Check("Pé de pedra na ponta direita", b.PeDireito, v => { b.PeDireito = v; RebuildEditor(); }, null));
            if (b.PeEsquerdo || b.PeDireito)
                pes.Children.Add(Field("Espessura do pé", NumBox(b.PeEspessuraMm, v => b.PeEspessuraMm = v, 10, 100), "O pé desce até o piso nas pontas livres (sem encosto)."));
            panel.Children.Add(ToolChrome.Card(pes, "Pés (ilhas e penínsulas)"));
            return panel;
        }

        private UIElement BancadaCubas(BancadaConfig b)
        {
            var panel = new StackPanel();
            for (int i = 0; i < b.Cubas.Count; i++)
            {
                var cu = b.Cubas[i];
                int k = i;
                var c = new StackPanel();
                c.Children.Add(Field("Tipo", Pills(new[] { "Embutir", "Sobrepor", "Esculpida", "Apoio" }, (int)cu.Tipo, t => { cu.Tipo = (TipoCuba)t; Refresh(); }), null));
                c.Children.Add(Check("Cuba dupla (duas bacias lado a lado)", cu.Dupla, v => { cu.Dupla = v; if (v && cu.LarguraMm < 760) cu.LarguraMm = 800; RebuildEditor(); Refresh(); }, null));
                c.Children.Add(Field("Largura", NumBox(cu.LarguraMm, v => cu.LarguraMm = v, 100, 3000), cu.Dupla ? "Total das duas bacias e do septo." : "Do recorte (ou da cuba de apoio)."));
                if (cu.Dupla)
                {
                    c.Children.Add(Field("Septo entre as bacias", NumBox(cu.SeptoMm, v => cu.SeptoMm = v, 10, 400), "Faixa de pedra entre os dois recortes."));
                    c.Children.Add(Field("Bacia da esquerda", NumBox(cu.ProporcaoPct, v => cu.ProporcaoPct = v, 20, 80, unit: "%"), "50 = bacias iguais; 60 = 60/40 (esquerda maior)."));
                }
                c.Children.Add(Field("Frente-fundo", NumBox(cu.ProfundidadeMm, v => cu.ProfundidadeMm = v, 100, 1000), null));
                c.Children.Add(Field("Profundidade da cuba", NumBox(cu.FundoMm, v => cu.FundoMm = v, 30, 500), null));
                c.Children.Add(Field("Raio dos cantos", NumBox(cu.RaioMm, v => cu.RaioMm = v, 0, 200), null));
                c.Children.Add(Field("Centro a partir da esquerda", NumBox(cu.PosicaoXMm, v => cu.PosicaoXMm = v, 0, 12000, true), "Vazio = no meio do trecho principal."));
                c.Children.Add(Field("Recuo da frente", NumBox(cu.RecuoFrenteMm, v => cu.RecuoFrenteMm = v, 0, 1000, true), "Da borda da frente ao recorte. Vazio = centralizada."));
                c.Children.Add(Check("Furo para torneira de mesa", cu.FuroTorneira, v => cu.FuroTorneira = v, null));
                var remove = SmallButton("Remover", "Tira esta cuba", () => { b.Cubas.RemoveAt(k); RebuildEditor(); Refresh(); });
                panel.Children.Add(ToolChrome.Card(c, "Cuba " + (i + 1), remove));
            }
            var add = ToolChrome.Secondary("+ Adicionar cuba", (s, e) =>
            {
                double x = b.Cubas.Count == 0 ? 0 : Math.Min(b.ComprimentoAMm - 300, b.Cubas.Max(cc => cc.PosicaoXMm > 0 ? cc.PosicaoXMm : b.ComprimentoAMm / 2) + 650);
                b.Cubas.Add(new Cuba { Tipo = TipoCuba.Embutir, LarguraMm = 500, ProfundidadeMm = 400, FundoMm = 180, RaioMm = 20, PosicaoXMm = Math.Max(0, x) });
                RebuildEditor(); Refresh();
            }, "Cubas ficam no trecho principal (A).");
            add.HorizontalAlignment = HorizontalAlignment.Left;
            add.Margin = new Thickness(0, 0, 0, 12);
            panel.Children.Add(add);

            var ck = new StackPanel();
            ck.Children.Add(Check("Recorte para cooktop", b.Cooktop, v => { b.Cooktop = v; RebuildEditor(); }, null));
            if (b.Cooktop)
            {
                ck.Children.Add(Field("Largura", NumBox(b.CooktopLarguraMm, v => b.CooktopLarguraMm = v, 200, 1200), "Do recorte (veja o manual do cooktop)."));
                ck.Children.Add(Field("Frente-fundo", NumBox(b.CooktopProfundidadeMm, v => b.CooktopProfundidadeMm = v, 200, 800), null));
                ck.Children.Add(Field("Centro a partir da esquerda", NumBox(b.CooktopPosicaoXMm, v => b.CooktopPosicaoXMm = v, 0, 12000, true), "Vazio = no último terço do trecho principal."));
            }
            panel.Children.Add(ToolChrome.Card(ck, "Cooktop"));
            return panel;
        }

        // ---------------------------------------------------------------- soleira / filete / peitoril
        private UIElement PedraMedidas(PedraConfig p)
        {
            var panel = new StackPanel();
            panel.Children.Add(Identificacao());
            var dim = new StackPanel();
            if (p.Uso == UsoPedra.Rodape)
            {
                dim.Children.Add(Field("Altura", NumBox(p.LarguraMm, v => p.LarguraMm = v, 10, 1500), "Altura do rodapé; o comprimento é o de cada trecho no projeto."));
                dim.Children.Add(Field("Espessura", NumBox(p.EspessuraMm, v => p.EspessuraMm = v, 3, 100), null));
                dim.Children.Add(ToolChrome.Muted("Criado pela ferramenta Rodapé (família baseada em linha).", 11));
                panel.Children.Add(ToolChrome.Card(dim, "Rodapé"));
                return panel;
            }
            if (p.Uso == UsoPedra.Patamar)
            {
                dim.Children.Add(Field("Largura", NumBox(p.ComprimentoMm, v => p.ComprimentoMm = v, 50, 15000), "Na largura da escada."));
                dim.Children.Add(Field("Profundidade", NumBox(p.LarguraMm, v => p.LarguraMm = v, 10, 15000), "No sentido de quem sobe."));
                dim.Children.Add(Field("Espessura", NumBox(p.EspessuraMm, v => p.EspessuraMm = v, 3, 100), null));
                dim.Children.Add(ToolChrome.Muted("Criado pela ferramenta Escada Revestida.", 11));
                panel.Children.Add(ToolChrome.Card(dim, "Patamar"));
                return panel;
            }
            if (p.Uso == UsoPedra.Degrau)
            {
                dim.Children.Add(Field("Largura do degrau", NumBox(p.ComprimentoMm, v => p.ComprimentoMm = v, 50, 15000), "Largura da escada."));
                dim.Children.Add(Field("Profundidade do piso", NumBox(p.LarguraMm, v => p.LarguraMm = v, 10, 1500), "Da frente (bocel) ao fundo do degrau."));
                dim.Children.Add(Field("Espessura do piso", NumBox(p.EspessuraMm, v => p.EspessuraMm = v, 3, 100), null));
                dim.Children.Add(Field("Altura do espelho", NumBox(p.AlturaEspelhoMm, v => p.AlturaEspelhoMm = v, 0, 400), "0 = sem espelho."));
                dim.Children.Add(Field("Espessura do espelho", NumBox(p.EspessuraEspelhoMm, v => p.EspessuraEspelhoMm = v, 3, 60), null));
                dim.Children.Add(Field("Bocel", NumBox(p.ProjecaoMm, v => p.ProjecaoMm = v, 0, 300), "Quanto o piso avança além do espelho."));
                dim.Children.Add(ToolChrome.Muted("Criado pela ferramenta Escada Revestida.", 11));
                panel.Children.Add(ToolChrome.Card(dim, "Degrau"));
                return panel;
            }
            dim.Children.Add(Field("Peça", Pills(new[] { "Soleira", "Filete", "Peitoril" }, (int)p.Uso, i => { p.Uso = (UsoPedra)i; RebuildEditor(); Refresh(); }), null));
            dim.Children.Add(Field("Comprimento", NumBox(p.ComprimentoMm, v => p.ComprimentoMm = v, 50, 15000), "Ao longo da parede."));
            dim.Children.Add(Field("Largura", NumBox(p.LarguraMm, v => p.LarguraMm = v, 10, 1500), "Através da parede."));
            dim.Children.Add(Field("Espessura", NumBox(p.EspessuraMm, v => p.EspessuraMm = v, 3, 100), null));
            dim.Children.Add(Field("Deslocamento p/ fora", NumBox(p.CentroMm, v => p.CentroMm = v, -1000, 1000), "Centro da largura a partir do eixo da parede (+ = exterior)."));
            if (p.Uso == UsoPedra.Peitoril)
                dim.Children.Add(Check("Pingadeira (sulco sob a projeção)", p.Pingadeira, v => p.Pingadeira = v, null));
            dim.Children.Add(ToolChrome.Muted("Criada pela ferramenta Soleiras e Peitoris: o topo da pedra fica na base da porta / no peitoril da janela.", 11));
            panel.Children.Add(ToolChrome.Card(dim, "Medidas"));
            return panel;
        }

        // ---------------------------------------------------------------- nicho
        private UIElement NichoMedidas(NichoConfig n)
        {
            var panel = new StackPanel();
            panel.Children.Add(Identificacao());
            var dim = new StackPanel();
            dim.Children.Add(Field("Largura", NumBox(n.LarguraMm, v => n.LarguraMm = v, 80, 4000), "Medidas internas acabadas (vão útil)."));
            dim.Children.Add(Field("Altura", NumBox(n.AlturaMm, v => n.AlturaMm = v, 80, 3000), null));
            dim.Children.Add(Field("Profundidade", NumBox(n.ProfundidadeMm, v => n.ProfundidadeMm = v, 30, 600), "A partir da face da parede onde o nicho é colocado."));
            dim.Children.Add(Field("Altura da base", NumBox(n.AlturaBaseMm, v => n.AlturaBaseMm = v, 0, 4000), "Do piso até o fundo do nicho (Elevação padrão; cada cópia pode mudar no Revit)."));
            panel.Children.Add(ToolChrome.Card(dim, "Dimensões"));
            var pl = new StackPanel();
            pl.Children.Add(Check("Contorno tracejado em planta", n.PlantaTracejada, v => n.PlantaTracejada = v, "Para nichos acima do plano de corte (o Revit não mostra o recorte)."));
            panel.Children.Add(ToolChrome.Card(pl, "Planta"));
            return panel;
        }

        private UIElement NichoInterior(NichoConfig n)
        {
            var panel = new StackPanel();
            var pr = new StackPanel();
            pr.Children.Add(Field("Prateleiras", Pills(new[] { "0", "1", "2", "3", "4" }, Math.Min(4, n.Prateleiras), i => { n.Prateleiras = i; RebuildEditor(); Refresh(); }), "Distribuídas por igual na altura."));
            if (n.Prateleiras > 0)
                pr.Children.Add(Field("Espessura", NumBox(n.PrateleiraEspessuraMm, v => n.PrateleiraEspessuraMm = v, 4, 60), "Vidro 8–10 mm; pedra 20 mm."));
            pr.Children.Add(Field("Divisórias", Pills(new[] { "0", "1", "2", "3" }, Math.Min(3, n.Divisorias), i => { n.Divisorias = i; RebuildEditor(); Refresh(); }), "Verticais, distribuídas na largura."));
            if (n.Divisorias > 0)
                pr.Children.Add(Field("Espessura", NumBox(n.DivisoriaEspessuraMm, v => n.DivisoriaEspessuraMm = v, 4, 60), null));
            panel.Children.Add(ToolChrome.Card(pr, "Prateleiras e divisórias"));

            var rv = new StackPanel();
            rv.Children.Add(Check("Revestir o interior", n.Revestimento, v => { n.Revestimento = v; RebuildEditor(); }, "Fundo, laterais e teto (e o piso, sem base de pedra)."));
            if (n.Revestimento)
                rv.Children.Add(Field("Espessura", NumBox(n.RevestimentoEspessuraMm, v => n.RevestimentoEspessuraMm = v, 3, 50), "Revestimento + argamassa; o vazio na parede aumenta nesta medida."));
            panel.Children.Add(ToolChrome.Card(rv, "Revestimento interno"));
            return panel;
        }

        private UIElement NichoPedra(NichoConfig n)
        {
            var panel = new StackPanel();
            var bs = new StackPanel();
            bs.Children.Add(Check("Base de pedra (soleira do nicho)", n.BasePedra, v => { n.BasePedra = v; RebuildEditor(); }, null));
            if (n.BasePedra)
            {
                bs.Children.Add(Field("Espessura", NumBox(n.BaseEspessuraMm, v => n.BaseEspessuraMm = v, 5, 60), null));
                bs.Children.Add(Field("Projeção", NumBox(n.BaseProjecaoMm, v => n.BaseProjecaoMm = v, 0, 100), "Quanto a base avança além da face da parede (pingadeira)."));
            }
            panel.Children.Add(ToolChrome.Card(bs, "Base"));
            var md = new StackPanel();
            md.Children.Add(Check("Moldura de pedra na face da parede", n.Moldura, v => { n.Moldura = v; RebuildEditor(); }, null));
            if (n.Moldura)
            {
                md.Children.Add(Field("Largura", NumBox(n.MolduraLarguraMm, v => n.MolduraLarguraMm = v, 5, 300), null));
                md.Children.Add(Field("Espessura", NumBox(n.MolduraEspessuraMm, v => n.MolduraEspessuraMm = v, 2, 60), "Saliência sobre a face da parede."));
            }
            panel.Children.Add(ToolChrome.Card(md, "Moldura"));
            return panel;
        }

        // ---------------------------------------------------------------- chapim
        private UIElement ChapimMedidas(ChapimConfig c)
        {
            var panel = new StackPanel();
            panel.Children.Add(Identificacao());
            var dim = new StackPanel();
            dim.Children.Add(Field("Espessura do muro", NumBox(c.EspessuraMuroMm, v => c.EspessuraMuroMm = v, 50, 1000),
                "Ao aplicar nos muros, paredes de outra espessura ganham um tipo próprio automaticamente (" + c.Codigo + "-200, …)."));
            dim.Children.Add(Field("Aba externa", NumBox(c.AbaExternaMm, v => c.AbaExternaMm = v, 0, 300), "Quanto a pedra passa da face externa."));
            dim.Children.Add(Field("Aba interna", NumBox(c.AbaInternaMm, v => c.AbaInternaMm = v, 0, 300), null));
            dim.Children.Add(Field("Espessura da pedra", NumBox(c.EspessuraMm, v => c.EspessuraMm = v, 10, 150), "Na borda mais baixa."));
            panel.Children.Add(ToolChrome.Card(dim, "Seção"));
            var len = new StackPanel();
            len.Children.Add(Field("Comprimento das peças", NumBox(c.PecaComprimentoMm, v => c.PecaComprimentoMm = v, 0, 5000, true), "Desenha as juntas em planta. Vazio = sem juntas."));
            len.Children.Add(Field("Comprimento inicial", NumBox(c.ComprimentoMm, v => c.ComprimentoMm = v, 100, 50000), "Só ao posicionar à mão; aplicado num muro, acompanha o comprimento da parede."));
            panel.Children.Add(ToolChrome.Card(len, "Comprimento"));
            return panel;
        }

        private UIElement ChapimPerfil(ChapimConfig c)
        {
            var panel = new StackPanel();
            var cai = new StackPanel();
            cai.Children.Add(Field("Caimento", Pills(new[] { "Plano", "Um lado", "Duas águas" }, (int)c.Caimento, i => { c.Caimento = (CaimentoChapim)i; RebuildEditor(); Refresh(); }), null));
            if (c.Caimento != CaimentoChapim.Plano)
                cai.Children.Add(Field("Inclinação", NumBox(c.InclinacaoPct, v => c.InclinacaoPct = v, 0, 30, false, "%"), "Mínimo usual 2%."));
            if (c.Caimento == CaimentoChapim.UmLado)
                cai.Children.Add(Field("Água escorre", Pills(new[] { "Para dentro", "Para fora" }, c.CaiParaDentro ? 0 : 1, i => { c.CaiParaDentro = i == 0; Refresh(); }), "Para dentro: não suja a fachada."));
            panel.Children.Add(ToolChrome.Card(cai, "Caimento"));

            var pg = new StackPanel();
            pg.Children.Add(Check("Pingadeira (friso sob as abas)", c.Pingadeira, v => { c.Pingadeira = v; RebuildEditor(); }, "Corta a água para ela não escorrer pela parede."));
            if (c.Pingadeira)
            {
                pg.Children.Add(Field("Largura", NumBox(c.PingadeiraLarguraMm, v => c.PingadeiraLarguraMm = v, 3, 30), null));
                pg.Children.Add(Field("Profundidade", NumBox(c.PingadeiraProfundidadeMm, v => c.PingadeiraProfundidadeMm = v, 2, 20), null));
                pg.Children.Add(Field("Distância da borda", NumBox(c.PingadeiraDistanciaMm, v => c.PingadeiraDistanciaMm = v, 3, 100), null));
            }
            panel.Children.Add(ToolChrome.Card(pg, "Pingadeira"));
            return panel;
        }

        // ---------------------------------------------------------------- ralo (conexão de tubo)
        private static readonly string[] TiposRalo = { "Linear", "Quadrado", "Redondo", "Caixa sifonada", "Hemisférico" };

        /// <summary>Sensible sizes when the kind of drain changes.</summary>
        private static void AjustarRalo(RaloConfig r)
        {
            switch (r.Formato)
            {
                case TipoRalo.Linear:
                    if (r.LarguraMm > 200) r.LarguraMm = 70;
                    if (r.AlturaCorpoMm > 120) r.AlturaCorpoMm = 80;
                    break;
                case TipoRalo.Quadrado:
                    if (r.LarguraMm < 90) r.LarguraMm = 150;
                    if (r.AlturaCorpoMm > 140) r.AlturaCorpoMm = 90;
                    break;
                case TipoRalo.Redondo:
                    if (r.LarguraMm < 90) r.LarguraMm = 100;
                    if (r.AlturaCorpoMm > 140) r.AlturaCorpoMm = 70;
                    break;
                case TipoRalo.CaixaSifonada:
                    if (r.LarguraMm < 100) r.LarguraMm = 150;
                    if (r.AlturaCorpoMm < 140) r.AlturaCorpoMm = 185;
                    if (r.Entradas == 0) r.Entradas = 3;
                    break;
                case TipoRalo.Hemisferico:
                    if (r.LarguraMm < 90) r.LarguraMm = 110;
                    r.SaidaVertical = true;
                    if (r.DiametroSaidaMm < 75) r.DiametroSaidaMm = 100;
                    r.Sistema = SistemaRalo.Pluvial;
                    break;
            }
            r.Normalized();
        }

        private UIElement RaloMedidas(RaloConfig r)
        {
            var panel = new StackPanel();
            panel.Children.Add(Identificacao());
            var tp = new StackPanel();
            tp.Children.Add(Field("Tipo", Pills(TiposRalo, (int)r.Formato, i => { r.Formato = (TipoRalo)i; AjustarRalo(r); RebuildEditor(); Refresh(); }),
                r.Formato == TipoRalo.CaixaSifonada ? "Com entradas laterais (conectores) para ligar lavatório, chuveiro…"
                : r.Formato == TipoRalo.Hemisferico ? "Grelha em domo para lajes, calhas e jardineiras."
                : r.Formato == TipoRalo.Linear ? "Canaleta de qualquer comprimento e largura." : null));
            panel.Children.Add(ToolChrome.Card(tp, "Tipo de ralo"));

            var dim = new StackPanel();
            if (r.NoCroqui)
            {
                dim.Children.Add(ToolChrome.Muted("Traçado do croqui: " + (r.Caminho.Count - 1) + " trecho(s), " + PecaUtil.Mm(r.ComprimentoCaminho()) +
                                                  " mm no total. O comprimento segue o desenho; para mudar o traçado, rode Ralo no Local de novo.", 11.5));
                dim.Children.Add(Field("Largura", NumBox(r.LarguraMm, v => r.LarguraMm = v, 35, 400), "Largura total, com a moldura."));
                if (r.BordaNoCaminho != 0)
                    dim.Children.Add(Field("Afastamento da parede", NumBox(r.AfastamentoMm, v => r.AfastamentoMm = v, 0, 2000), "Da face da parede até a borda do ralo (muda a largura, a borda fica no lugar)."));
            }
            else if (r.Linear)
            {
                dim.Children.Add(Field("Comprimento", NumBox(r.ComprimentoMm, v => r.ComprimentoMm = v, 200, 6000), "Qualquer medida, de 20 cm a 6 m (parede a parede, soleira do box…)."));
                dim.Children.Add(Field("Largura", NumBox(r.LarguraMm, v => r.LarguraMm = v, 35, 400), "Largura total, com a moldura."));
            }
            else dim.Children.Add(Field(r.Circular ? "Diâmetro" : "Lado", NumBox(r.LarguraMm, v => r.LarguraMm = v, 60, 600), r.Circular ? null : "Grelha quadrada (com a moldura)."));
            dim.Children.Add(Field(r.Formato == TipoRalo.Hemisferico ? "Comprimento do tubo" : "Altura do corpo", NumBox(r.AlturaCorpoMm, v => r.AlturaCorpoMm = v, 30, 600),
                r.Formato == TipoRalo.Hemisferico ? "Tubo de saída abaixo da laje." : "Do topo da grelha (piso acabado) até a saída; confira o rebaixo do contrapiso."));
            if (r.Linear)
                dim.Children.Add(Field("Caimento do piso (%)", NumBox(r.CaimentoPct, v => r.CaimentoPct = v, 0, 10, false, "%"), "Só informativo: vai para a descrição e o Quadro de Peças (1% usual em box)."));
            panel.Children.Add(ToolChrome.Card(dim, "Dimensões"));

            var sis = new StackPanel();
            sis.Children.Add(Field("Sistema", Pills(new[] { "Esgoto", "Águas pluviais" }, (int)r.Sistema, i => { r.Sistema = (SistemaRalo)i; Refresh(); }),
                "Classificação dos conectores; o tubo ligado herda o sistema."));
            panel.Children.Add(ToolChrome.Card(sis, "Sistema"));
            return panel;
        }

        private UIElement RaloGrelha(RaloConfig r)
        {
            var panel = new StackPanel();
            if (r.Formato == TipoRalo.Hemisferico)
            {
                panel.Children.Add(ToolChrome.Card(ToolChrome.Muted("O ralo hemisférico tem grelha em domo, com aba apoiada na laje.", 11.5), "Grelha"));
                return panel;
            }
            var g = new StackPanel();
            g.Children.Add(Field("Desenho", Pills(new[] { "Fendas", "Furos", "Quadriculada", "Revestível" }, (int)r.Grelha, i => { r.Grelha = (GrelhaRalo)i; RebuildEditor(); Refresh(); }),
                r.Grelha == GrelhaRalo.Revestivel ? "Tampa invisível: recebe o mesmo piso; a água entra pela fresta em volta." : "Em detalhe fino aparecem as aberturas; em baixo e médio, a grelha lisa."));
            g.Children.Add(Field("Espessura", NumBox(r.EspessuraGrelhaMm, v => r.EspessuraGrelhaMm = v, 2, 30), null));
            g.Children.Add(Field("Moldura", NumBox(r.MolduraMm, v => r.MolduraMm = v, 0, 60), "Aro visível em volta da grelha (0 = sem moldura)."));
            if (r.Grelha == GrelhaRalo.Revestivel)
                g.Children.Add(Field("Rebaixo para o piso", NumBox(r.RebaixoMm, v => r.RebaixoMm = v, 5, 40), "Espessura do piso + argamassa dentro da tampa."));
            panel.Children.Add(ToolChrome.Card(g, "Grelha"));
            return panel;
        }

        private UIElement RaloSaidas(RaloConfig r)
        {
            var panel = new StackPanel();
            string[] dns = RaloConfig.Diametros.Select(d => "DN" + d.ToString("0", System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            int Mais(double v) => Array.IndexOf(RaloConfig.Diametros, RaloConfig.Diametros.OrderBy(d => Math.Abs(d - v)).First());

            var s = new StackPanel();
            s.Children.Add(Field("Diâmetro", Pills(dns, Mais(r.DiametroSaidaMm), i => { r.DiametroSaidaMm = RaloConfig.Diametros[i]; Refresh(); }), "Conector de tubo no fim da saída."));
            if (r.Formato != TipoRalo.Hemisferico)
                s.Children.Add(Field("Direção", Pills(new[] { "Vertical (para baixo)", "Horizontal" }, r.SaidaVertical ? 0 : 1, i => { r.SaidaVertical = i == 0; RebuildEditor(); Refresh(); }),
                    r.SaidaVertical ? null : r.Linear ? "Sai pela ponta escolhida abaixo (no centro: pelo lado de trás)." : "Sai pelo lado de trás (−Y)."));
            if (r.Linear)
            {
                s.Children.Add(Field("Posição", Pills(r.NoCroqui ? new[] { "Meio do traçado", "Início", "Fim" } : new[] { "Centro", "Esquerda", "Direita" }, (int)r.Posicao, i => { r.Posicao = (PosicaoSaida)i; RebuildEditor(); Refresh(); }),
                    r.NoCroqui ? "Início = o primeiro ponto clicado (ou a ponta solta das linhas)." : null));
                if (r.Posicao != PosicaoSaida.Centro && r.SaidaVertical)
                    s.Children.Add(Field("Distância da ponta", NumBox(r.DistanciaSaidaMm, v => r.DistanciaSaidaMm = v, 20, 3000), "Da ponta até o eixo da saída."));
            }
            s.Children.Add(Field("Comprimento do tubo", NumBox(r.ComprimentoSaidaMm, v => r.ComprimentoSaidaMm = v, 20, 500), "Ponta de tubo onde o conector fica."));
            panel.Children.Add(ToolChrome.Card(s, "Saída"));

            if (r.Formato == TipoRalo.CaixaSifonada)
            {
                var en = new StackPanel();
                en.Children.Add(Field("Entradas", Pills(new[] { "0", "1", "2", "3", "4" }, r.Entradas, i => { r.Entradas = i; RebuildEditor(); Refresh(); }),
                    "Laterais, cada uma com conector: esquerda, direita, fundo" + (r.SaidaVertical ? " e frente." : " (a frente é da saída).")));
                if (r.Entradas > 0)
                    en.Children.Add(Field("Diâmetro", Pills(dns.Take(3).ToArray(), Math.Min(2, Mais(r.DiametroEntradaMm)), i => { r.DiametroEntradaMm = RaloConfig.Diametros[i]; Refresh(); }), null));
                panel.Children.Add(ToolChrome.Card(en, "Entradas"));
            }
            return panel;
        }

        // ---------------------------------------------------------------- materiais (all kinds)
        private UIElement Materiais()
        {
            var panel = new StackPanel();
            switch (_cfg)
            {
                case BancadaConfig b:
                    panel.Children.Add(MaterialCard("Pedra", b.MaterialPedra, v => b.MaterialPedra = v, b.CorPedra, v => b.CorPedra = v, "Tampo, saia, frontão, tento e pés."));
                    panel.Children.Add(MaterialCard("Cuba", b.MaterialCuba, v => b.MaterialCuba = v, b.CorCuba, v => b.CorCuba = v, "Cubas de embutir, sobrepor e apoio (a esculpida é da pedra)."));
                    break;
                case NichoConfig n:
                    panel.Children.Add(MaterialCard("Revestimento interno", n.MaterialInterno, v => n.MaterialInterno = v, n.CorInterno, v => n.CorInterno = v, null));
                    panel.Children.Add(MaterialCard("Pedra", n.MaterialPedra, v => n.MaterialPedra = v, n.CorPedra, v => n.CorPedra = v, "Base e moldura."));
                    panel.Children.Add(MaterialCard("Prateleiras e divisórias", n.MaterialPrateleira, v => n.MaterialPrateleira = v, n.CorPrateleira, v => n.CorPrateleira = v, "Com “vidro” no nome o material fica transparente."));
                    break;
                case ChapimConfig c:
                    panel.Children.Add(MaterialCard("Pedra", c.MaterialPedra, v => c.MaterialPedra = v, c.CorPedra, v => c.CorPedra = v, null));
                    break;
                case PedraConfig pd:
                    panel.Children.Add(MaterialCard("Pedra", pd.Material, v => pd.Material = v, pd.Cor, v => pd.Cor = v, null));
                    break;
                case RaloConfig r:
                    panel.Children.Add(MaterialCard("Grelha e moldura", r.MaterialGrelha, v => r.MaterialGrelha = v, r.CorGrelha, v => r.CorGrelha = v, "Inox, latão, ferro fundido, alumínio…"));
                    panel.Children.Add(MaterialCard("Corpo e tubos", r.MaterialCorpo, v => r.MaterialCorpo = v, r.CorCorpo, v => r.CorCorpo = v, "Fica sob o piso."));
                    if (r.Grelha == GrelhaRalo.Revestivel && r.Formato != TipoRalo.Hemisferico)
                        panel.Children.Add(MaterialCard("Revestimento da tampa", r.MaterialRevestimento, v => r.MaterialRevestimento = v, r.CorRevestimento, v => r.CorRevestimento = v, "O mesmo piso do ambiente."));
                    break;
            }
            var bim = new StackPanel();
            _bimText = ToolChrome.Muted("", 11.5);
            _bimText.TextWrapping = TextWrapping.Wrap;
            bim.Children.Add(_bimText);
            panel.Children.Add(ToolChrome.Card(bim, "Dados BIM da família"));
            RefreshBim();
            return panel;
        }

        private FrameworkElement MaterialCard(string title, string mat, Action<string> setMat, string hex, Action<string> setHex, string hint)
        {
            var sp = new StackPanel();
            sp.Children.Add(Field("Material", MaterialInput(mat, setMat), "Se o projeto já tiver um material com este nome, ele é usado."));
            sp.Children.Add(Field("Cor (prévia)", HexInput(hex, setHex), hint));
            return ToolChrome.Card(sp, title);
        }

        // =========================================================================================
        // controls (same behaviour as the esquadrias configurator)
        // =========================================================================================
        // the shared controls (FormKit), bound to this window
        private FrameworkElement MaterialInput(string value, Action<string> set) => FormKit.MaterialInput(_materials, value, m => { set(m); RefreshBim(); });
        private FrameworkElement HexInput(string value, Action<string> set) => FormKit.HexInput(value, h => { set(h); Refresh(); }, MarkInvalid);
        private FrameworkElement Check(string label, bool value, Action<bool> set, string hint) => FormKit.Check(label, value, v => { set(v); Refresh(); }, hint);
        private void MarkInvalid(TextBox tb, bool invalid) => FormKit.MarkInvalid(_invalid, tb, invalid);
        private void SetStatus(string text, bool error) => FormKit.SetStatus(_status, text, error);
        private void SetTab(int i, UIElement content) => FormKit.SetTab(_tabs, i, content);

        /// <summary>Number box: applies the value live when valid, red border otherwise; ↑/↓ steps 10 (Shift 100).</summary>
        private FrameworkElement NumBox(double value, Action<double> set, double min, double max, bool allowEmpty = false, string unit = "mm")
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
                double step = unit == "%" ? ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 5 : 0.5) : (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 100 : 10;
                v = Math.Max(min, Math.Min(max, v + (e.Key == Key.Up ? step : -step)));
                tb.Text = v.ToString("0.#", CultureInfo.CurrentCulture);
                tb.CaretIndex = tb.Text.Length;
                e.Handled = true;
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            if (allowEmpty) row.Children.Add(new Border { Child = ToolChrome.SearchBox(tb, "auto"), Width = 92 });
            else row.Children.Add(tb);
            var u = ToolChrome.Muted(unit, 11.5);
            u.VerticalAlignment = VerticalAlignment.Center;
            u.Margin = new Thickness(6, 0, 0, 0);
            row.Children.Add(u);
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
            if (code.Length == 0) err = "Informe um código, ex.: " + PecaUtil.Prefixo(_tipo) + "01.";
            else if (!Regex.IsMatch(code, @"^[A-Za-z0-9][A-Za-z0-9 ._\-]{0,19}$")) err = "Use letras, números, espaço, ponto, hífen ou sublinhado.";
            else if (!_editing && _usedCodes.Contains(code)) err = "Já existe " + NomeMin + " " + code + " neste projeto. Escolha outro código ou use Editar Família. Sugestão: " + ProjectPecas.NextCode(_usedCodes, _tipo) + ".";
            _codeError.Text = err ?? "";
            _codeError.Visibility = err == null ? Visibility.Collapsed : Visibility.Visible;
            return err == null;
        }

        private void Refresh()
        {
            if (_canvas == null) return;
            try { _model = _cfg.Build(); }
            catch (Exception ex)
            {
                SetStatus("Erro ao montar a prévia: " + ex.Message, true);
                return;
            }
            Draw();
            if (_sizeBadge != null) _sizeBadge.Text = _cfg.Dimensoes();

            _stats.Children.Clear();
            foreach (string r in _model.Resumo) _stats.Children.Add(Stat(r, null));
            _stats.Children.Add(Stat(_model.Pecas.Count(p => !p.Void) + " sólidos" + (_model.Pecas.Any(p => p.Void) ? " + vazio" : ""), "Peças 3D da família (com parâmetros de material)."));
            foreach (string a in _model.Avisos.Distinct())
            {
                var t = new TextBlock { Text = "⚠ " + a, TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Margin = new Thickness(0, 4, 0, 0) };
                t.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Warn");
                _stats.Children.Add(new Border { Child = t, MaxWidth = 900 });
            }
            RefreshBim();
        }

        private void Draw()
        {
            if (_canvas == null) return;
            try { PecaRenderer.Draw(_canvas, _cfg, _vista, new PecaRenderOptions { Accent = _info.Accent }, _canvas.ActualWidth, _canvas.ActualHeight); }
            catch (Exception ex) { SetStatus("Erro ao desenhar a prévia: " + ex.Message, true); }
        }

        private void RefreshBim()
        {
            if (_bimText == null) return;
            var m = _model;
            _bimText.Text =
                "Família: " + FamilyGenerator.SafeName(PecaUtil.FamilyName(_cfg)) + " (" + (PecaUtil.Tubo(_tipo) ? "Conexão de tubo, com conectores" : PecaUtil.Gabinete(_tipo) ? "Componente de gabinete" : "Modelo genérico") +
                    (_tipo == TipoPeca.Nicho ? ", baseado em parede" : _tipo == TipoPeca.Chapim ? ", baseado em linha" : "") + ")\n" +
                "Marca de tipo: " + _cfg.Codigo + "\n" +
                "Descrição: " + _cfg.Descricao() + "\n" +
                "JA_Familia: " + Nome + "\n" +
                "JA_Material: " + _cfg.MaterialPrincipal() + "\n" +
                "JA_Dimensoes: " + _cfg.Dimensoes() +
                (m != null && m.AreaPedra > 0 ? "\nJA_AreaPedra: " + PecaUtil.M2(m.AreaPedra) + (_tipo == TipoPeca.Chapim ? " (por metro)" : "") : "") +
                (_tipo == TipoPeca.Chapim ? "\nJA_Comprimento: comprimento de cada chapim (soma no quadro)" : "") +
                "\nParâmetros de material: " + string.Join(", ", _cfg.Materiais().Where(md => m == null || m.Pecas.Any(p => !p.Void && p.Slot == md.Key)).Select(md => md.Parametro));
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
                SetStatus("Confira o código.", true);
                return;
            }
            _result = new PecaResult { Config = _cfg.Copiar(), Place = place };
            try { _w.DialogResult = true; } catch { _w.Close(); }
        }

    }
}
