using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using RevitDwgUnifier.Tools.Common;
using RevitDwgUnifier.UI;
using DB = Autodesk.Revit.DB;

namespace RevitDwgUnifier.Tools.Central
{
    /// <summary>
    /// Central JA as a light, live panel (reference: Glyph): it lives in a Revit side panel or in a
    /// floating window and never blocks the model. One search box finds tools, tasks and bundles at
    /// once; tools are compact rows (favourites and recent first, groups folded); tasks and bundles
    /// open inline; the scope is a chip above the list; the last run is one line at the bottom.
    /// Everything that touches the model goes through <see cref="CentralHost.Pedir"/>.
    /// </summary>
    public sealed class CentralPainel : UserControl
    {
        public const string AbaFerramentas = "Ferramentas", AbaTarefas = "Tarefas", AbaPacotes = "Pacotes", AbaStatus = "Status", AbaEscopo = "Escopo";
        private static readonly string[] Abas = { AbaFerramentas, AbaTarefas, AbaPacotes, AbaStatus };
        private const string PrefRecentes = "busca.recentes";
        private static readonly Color Cor = Color.FromRgb(0x2B, 0x57, 0x9A);

        private readonly bool _lateral;
        private readonly TextBox _busca = new TextBox();
        private readonly Dictionary<string, RadioButton> _abas = new Dictionary<string, RadioButton>();
        private readonly StackPanel _lista = new StackPanel();
        private readonly ScrollViewer _rolagem = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        private readonly TextBlock _escopoTexto = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, FontSize = 11.5 };
        private readonly Border _escopoEditor = new Border { Visibility = System.Windows.Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
        private readonly TextBlock _rodape = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Cursor = Cursors.Hand };
        private readonly HashSet<string> _abertos = new HashSet<string>();
        private string _aba = AbaFerramentas;
        private bool _montado, _mudandoAba;

        public CentralPainel(bool lateral)
        {
            _lateral = lateral;
            JaTheme.ApplyTo(this, Cor);
            MinWidth = 240;
            Loaded += (s, e) =>
            {
                if (!_montado) Montar();
                Recarregar(null);
            };
        }

        // ======================================================================== frame

        private void Montar()
        {
            _montado = true;
            var raiz = new DockPanel { Margin = new Thickness(10, 8, 10, 8) };

            // header: mark, name, window/side toggle, help
            var topo = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var marca = new Image { Source = IconFactory.Mark(64), Width = 18, Height = 18, Margin = new Thickness(0, 0, 8, 0) };
            DockPanel.SetDock(marca, Dock.Left);
            topo.Children.Add(marca);
            var botoes = new StackPanel { Orientation = Orientation.Horizontal };
            botoes.Children.Add(Mini(_lateral ? "⧉" : "⇥", _lateral ? "Abrir como janela flutuante" : "Fixar na barra lateral do Revit",
                                     () => { if (_lateral) CentralHost.ParaJanela(); else CentralHost.ParaLateral(); }));
            botoes.Children.Add(Mini("?", "Ajuda da Central JA", () => HelpCenter.Open(ToolCatalog.Get("Central JA").Anchor)));
            DockPanel.SetDock(botoes, Dock.Right);
            topo.Children.Add(botoes);
            var nome = new TextBlock { Text = "Central JA", FontWeight = FontWeights.SemiBold, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center };
            nome.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Ink");
            topo.Children.Add(nome);
            DockPanel.SetDock(topo, Dock.Top);
            raiz.Children.Add(topo);

            // search: one box for everything
            var busca = ToolChrome.SearchBox(_busca, "Buscar ferramenta, tarefa ou pacote…");
            busca.Margin = new Thickness(0, 0, 0, 8);
            var espera = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
            espera.Tick += (s, e) => { espera.Stop(); Mostrar(); };
            _busca.TextChanged += (s, e) => { espera.Stop(); espera.Start(); };
            _busca.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape && _busca.Text.Length > 0) { _busca.Clear(); e.Handled = true; }
                else if (e.Key == Key.Enter) { RodarPrimeiroResultado(); e.Handled = true; }
            };
            DockPanel.SetDock(busca, Dock.Top);
            raiz.Children.Add(busca);

            // tabs as a compact segmented control
            var seg = new UniformGrid { Rows = 1 };
            foreach (string a in Abas)
            {
                string aa = a;
                var rb = new RadioButton { Content = a, GroupName = "central-aba-" + (_lateral ? "l" : "j"), FontSize = 11.5, Padding = new Thickness(4, 3, 4, 3), HorizontalContentAlignment = HorizontalAlignment.Center };
                rb.SetResourceReference(StyleProperty, "Ja.Segment");
                rb.Checked += (s, e) => { if (!_mudandoAba) { _aba = aa; _busca.Clear(); Mostrar(); } };
                _abas[a] = rb;
                seg.Children.Add(rb);
            }
            var trilho = ToolChrome.Track(seg);
            trilho.Margin = new Thickness(0, 0, 0, 8);
            DockPanel.SetDock(trilho, Dock.Top);
            raiz.Children.Add(trilho);

            // scope chip + inline editor
            var chip = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 4, 8, 4), Cursor = Cursors.Hand, ToolTip = "Onde as tarefas e pacotes trabalham (seleção unificada). Clique para mudar." };
            chip.SetResourceReference(Border.BackgroundProperty, "Ja.AccentSoft");
            var chipDp = new DockPanel();
            var seta = new TextBlock { Text = "▾", Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            seta.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Accent");
            DockPanel.SetDock(seta, Dock.Right);
            chipDp.Children.Add(seta);
            _escopoTexto.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Ink");
            chipDp.Children.Add(_escopoTexto);
            chip.Child = chipDp;
            chip.MouseLeftButtonUp += (s, e) =>
            {
                bool abrir = _escopoEditor.Visibility != System.Windows.Visibility.Visible;
                _escopoEditor.Visibility = abrir ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                seta.Text = abrir ? "▴" : "▾";
                if (abrir) _escopoEditor.Child = EditorEscopo();
            };
            var escopo = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            escopo.Children.Add(chip);
            escopo.Children.Add(_escopoEditor);
            DockPanel.SetDock(escopo, Dock.Top);
            raiz.Children.Add(escopo);

            // last run, one line
            _rodape.Margin = new Thickness(0, 8, 0, 0);
            _rodape.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Muted");
            _rodape.MouseLeftButtonUp += (s, e) => Recarregar(AbaStatus);
            DockPanel.SetDock(_rodape, Dock.Bottom);
            raiz.Children.Add(_rodape);

            _rolagem.Content = _lista;
            raiz.Children.Add(_rolagem);
            Content = raiz;
            _abas[_aba].IsChecked = true;
        }

        /// <summary>Shows a tab (or refreshes the current one when null).</summary>
        public void Recarregar(string aba)
        {
            if (!_montado) return;
            if (aba == AbaEscopo)
            {
                _escopoEditor.Visibility = System.Windows.Visibility.Visible;
                _escopoEditor.Child = EditorEscopo();
                aba = null;
            }
            if (aba != null && _abas.ContainsKey(aba))
            {
                _aba = aba;
                _mudandoAba = true;
                _abas[aba].IsChecked = true;
                _mudandoAba = false;
                if (aba == AbaStatus) _busca.Clear();
            }
            Mostrar();
        }

        public void AtualizarCabecalho()
        {
            if (!_montado) return;
            _escopoTexto.Text = "◎  Escopo: " + EscopoTexto();
            StatusExecucao st = Automacao.Historico.FirstOrDefault();
            _rodape.Text = st == null ? "Nada rodou ainda nesta sessão." : $"Última: {st.Quando:HH:mm} · {st.Titulo} · {st.Resumo}";
            _rodape.Visibility = System.Windows.Visibility.Visible;
        }

        private void Mostrar()
        {
            if (!_montado) return;
            AtualizarCabecalho();
            double y = _rolagem.VerticalOffset;
            _lista.Children.Clear();
            try
            {
                if (!string.IsNullOrWhiteSpace(_busca.Text)) Resultados();
                else if (_aba == AbaTarefas) Tarefas();
                else if (_aba == AbaPacotes) Pacotes();
                else if (_aba == AbaStatus) Status();
                else Ferramentas();
            }
            catch (Exception ex)
            {
                RevitDwgUnifier.Infrastructure.Logger.Warn("[Central JA] " + ex);
                _lista.Children.Add(ToolChrome.Muted("Não foi possível montar a lista: " + ex.Message));
            }
            _rolagem.ScrollToVerticalOffset(y);
        }

        // ======================================================================== building blocks

        private static Button Mini(string glifo, string dica, Action clique)
        {
            var b = new Button { Content = glifo, ToolTip = dica, Padding = new Thickness(6, 1, 6, 1), MinWidth = 0, MinHeight = 0, FontSize = 12.5, Margin = new Thickness(2, 0, 0, 0), Focusable = false };
            b.SetResourceReference(StyleProperty, "Ja.Ghost");
            b.Click += (s, e) => { e.Handled = true; clique(); };
            return b;
        }

        private static Button Menu(string dica, Func<ContextMenu> criar)
        {
            Button b = null;
            b = Mini("⋯", dica, () =>
            {
                ContextMenu m = criar();
                m.PlacementTarget = b;
                m.Placement = PlacementMode.Bottom;
                m.IsOpen = true;
            });
            return b;
        }

        private static MenuItem Item(string texto, Action clique, bool ativo = true)
        {
            var mi = new MenuItem { Header = texto, IsEnabled = ativo };
            mi.Click += (s, e) => clique();
            return mi;
        }

        private TextBlock Secao(string texto)
        {
            var tb = new TextBlock { Text = texto.ToUpperInvariant(), FontSize = 10.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 10, 0, 4) };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Muted");
            return tb;
        }

        /// <summary>A compact row: icon, title (+ line under it), actions that light up on hover.</summary>
        private Border Linha(ImageSource icone, string titulo, string sub, object dica, Action clique, params Button[] acoes)
        {
            var b = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 4, 4, 4), Background = Brushes.Transparent, Cursor = clique != null ? Cursors.Hand : Cursors.Arrow, ToolTip = dica };
            ToolTipService.SetInitialShowDelay(b, 700);
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (icone != null) g.Children.Add(new Image { Source = icone, Width = 20, Height = 20, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            var txt = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(txt, 1);
            var t = new TextBlock { Text = titulo, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12.5 };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Ink");
            txt.Children.Add(t);
            if (!string.IsNullOrEmpty(sub))
            {
                var d = ToolChrome.Muted(sub, 10.5);
                d.TextWrapping = TextWrapping.NoWrap;
                d.TextTrimming = TextTrimming.CharacterEllipsis;
                txt.Children.Add(d);
            }
            g.Children.Add(txt);
            var ac = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.45 };
            foreach (Button a in acoes) if (a != null) ac.Children.Add(a);
            Grid.SetColumn(ac, 2);
            g.Children.Add(ac);
            b.Child = g;
            b.MouseEnter += (s, e) => { b.SetResourceReference(Border.BackgroundProperty, "Ja.Hover"); ac.Opacity = 1; };
            b.MouseLeave += (s, e) => { b.Background = Brushes.Transparent; ac.Opacity = 0.45; };
            if (clique != null)
                b.MouseLeftButtonUp += (s, e) => { if (!(e.OriginalSource is DependencyObject o && SobreBotao(o, b))) clique(); };
            return b;
        }

        private static bool SobreBotao(DependencyObject d, DependencyObject parar)
        {
            for (; d != null && d != parar; d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
                if (d is ButtonBase || d is TextBox || d is ToggleButton) return true;
            return false;
        }

        private static ImageSource IconeDe(string ferramenta)
        {
            ToolInfo i = ToolCatalog.Get(ferramenta);
            return IconFactory.Tool(64, i.Icon, i.Glyph, i.Accent);
        }

        private static ImageSource IconePacote() => IconFactory.Tool(64, "central", "P", Color.FromRgb(0x18, 0x21, 0x2B));

        private Window Dono => Window.GetWindow(this);

        private void Aviso(string texto)
        {
            Window w = Dono;
            if (w != null) MessageBox.Show(w, texto, "Central JA", MessageBoxButton.OK, MessageBoxImage.Information);
            else MessageBox.Show(texto, "Central JA", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private bool Confirma(string texto)
        {
            Window w = Dono;
            return (w != null ? MessageBox.Show(w, texto, "Central JA", MessageBoxButton.OKCancel, MessageBoxImage.Question)
                              : MessageBox.Show(texto, "Central JA", MessageBoxButton.OKCancel, MessageBoxImage.Question)) == MessageBoxResult.OK;
        }

        private string Perguntar(string titulo, string pergunta, string inicial) => Automacao.PerguntarNome(Dono, titulo, pergunta, inicial);

        private static void Lembrar(string chave)
        {
            var rec = (ToolPrefs.Get(PrefRecentes) ?? "").Split(new[] { '\u001f' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            rec.Remove(chave);
            rec.Insert(0, chave);
            ToolPrefs.Set(PrefRecentes, string.Join("\u001f", rec.Take(10)));
            ToolPrefs.Save();
        }

        private static List<string> Recentes() =>
            (ToolPrefs.Get(PrefRecentes) ?? "").Split(new[] { '\u001f' }, StringSplitOptions.RemoveEmptyEntries).ToList();

        // ======================================================================== actions

        private void RodarFerramenta(string titulo)
        {
            Lembrar("f:" + titulo);
            CentralHost.Executar(new PedidoCentral { Acao = PedidoCentral.Ferramenta, Alvo = titulo });
        }

        private void RodarTarefa(Tarefa t)
        {
            Lembrar("t:" + t.Id);
            CentralHost.Executar(new PedidoCentral { Acao = PedidoCentral.Tarefa, Alvo = t.Id });
        }

        private void RodarPacote(Pacote p)
        {
            if (p.Passos.Count == 0) { Aviso("O pacote não tem passos."); return; }
            Lembrar("p:" + p.Id);
            CentralHost.Executar(new PedidoCentral { Acao = PedidoCentral.Pacote, Alvo = p.Id });
        }

        private void NovaTarefa(string ferramenta)
        {
            string n = Perguntar("Nova tarefa", "Nome da tarefa (a ferramenta abre para você escolher as opções e salvar):", ferramenta);
            if (!string.IsNullOrWhiteSpace(n))
                CentralHost.Executar(new PedidoCentral { Acao = PedidoCentral.NovaTarefa, Alvo = ferramenta, Nome = n.Trim() });
        }

        private void RodarPrimeiroResultado()
        {
            foreach (UIElement el in _lista.Children)
                if (el is Border b && b.Tag is Action a) { a(); return; }
        }

        // ======================================================================== search (all kinds at once)

        private void Resultados()
        {
            string[] q = TextUtil.Fold(_busca.Text).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var rec = Recentes();
            var achados = new List<(int pts, Border linha)>();
            int Pontos(string titulo, string resto, string chave)
            {
                string t = TextUtil.Fold(titulo), tudo = t + " " + TextUtil.Fold(resto ?? "");
                int p = 0;
                foreach (string w in q)
                {
                    int i = t.IndexOf(w, StringComparison.Ordinal);
                    if (i == 0) p += 40;
                    else if (i > 0 && !char.IsLetterOrDigit(t[i - 1])) p += 25;
                    else if (i > 0) p += 12;
                    else if (tudo.Contains(w)) p += 4;
                    else return -1;
                }
                int r = rec.IndexOf(chave);
                return r >= 0 ? p + 30 - r * 3 : p;
            }

            foreach (ToolInfo f in Automacao.FerramentasDoCatalogo())
            {
                int p = Pontos(f.Title, f.Group + " " + f.Description, "f:" + f.Title);
                if (p < 0) continue;
                string titulo = f.Title;
                Action rodar = () => RodarFerramenta(titulo);
                Border l = Linha(IconFactory.Tool(64, f.Icon, f.Glyph, f.Accent), f.Title, "Ferramenta · " + f.Group, f.Description, rodar,
                                 Mini("☆", "Salvar as opções como tarefa", () => NovaTarefa(titulo)));
                l.Tag = rodar;
                achados.Add((p, l));
            }
            foreach (Tarefa t in Automacao.TodasTarefas())
            {
                int p = Pontos(t.Nome, t.Ferramenta + " " + t.Descricao, "t:" + t.Id);
                if (p < 0) continue;
                Tarefa tt = t;
                Action rodar = () => RodarTarefa(tt);
                Border l = Linha(IconeDe(t.Ferramenta), t.Nome, "Tarefa · " + t.Ferramenta, ResumoOpcoes(t), rodar, Mini("▶", "Rodar", rodar));
                l.Tag = rodar;
                achados.Add((p + 2, l));
            }
            foreach (Pacote pk in Automacao.TodosPacotes())
            {
                int p = Pontos(pk.Nome, pk.Descricao + " " + string.Join(" ", pk.Passos.Select(Automacao.NomePasso)), "p:" + pk.Id);
                if (p < 0) continue;
                Pacote pp = pk;
                Action rodar = () => RodarPacote(pp);
                Border l = Linha(IconePacote(), pk.Nome, "Pacote · " + pk.Passos.Count + " passo(s)", string.Join("\n", pk.Passos.Select((x, i) => (i + 1) + ". " + Automacao.NomePasso(x))), rodar, Mini("▶", "Rodar", rodar));
                l.Tag = rodar;
                achados.Add((p + 2, l));
            }
            if (achados.Count == 0) { _lista.Children.Add(ToolChrome.Muted("Nada encontrado.")); return; }
            _lista.Children.Add(Secao("Resultados · Enter roda o primeiro"));
            foreach (var a in achados.OrderByDescending(x => x.pts).Take(40)) _lista.Children.Add(a.linha);
        }

        // ======================================================================== tools

        private void Ferramentas()
        {
            var todas = Automacao.FerramentasDoCatalogo();
            var porTitulo = todas.ToDictionary(t => t.Title, StringComparer.OrdinalIgnoreCase);
            var favs = new HashSet<string>(Automacao.Bib.Favoritos, StringComparer.OrdinalIgnoreCase);

            var fav = todas.Where(t => favs.Contains(t.Title)).ToList();
            if (fav.Count > 0)
            {
                _lista.Children.Add(Secao("★ Favoritas"));
                foreach (ToolInfo t in fav) _lista.Children.Add(LinhaFerramenta(t, favs));
            }
            var recentes = Recentes().Where(k => k.StartsWith("f:")).Select(k => k.Substring(2))
                                     .Where(n => porTitulo.ContainsKey(n) && !favs.Contains(n)).Take(4).ToList();
            if (recentes.Count > 0)
            {
                _lista.Children.Add(Secao("Recentes"));
                foreach (string n in recentes) _lista.Children.Add(LinhaFerramenta(porTitulo[n], favs));
            }
            _lista.Children.Add(Secao("Todas"));
            foreach (string g in ToolCatalog.GroupOrder)
            {
                var doGrupo = todas.Where(t => t.Group == g).ToList();
                if (doGrupo.Count == 0) continue;
                string chave = "central.grupo." + g;
                bool aberto = ToolPrefs.Get(chave) == "1";
                var corpo = new StackPanel { Visibility = aberto ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed, Margin = new Thickness(8, 0, 0, 2) };
                foreach (ToolInfo t in doGrupo) corpo.Children.Add(LinhaFerramenta(t, favs));
                var seta = new TextBlock { Text = aberto ? "▾" : "▸", Width = 14 };
                var cab = Linha(null, g, null, null, null);
                ((Grid)cab.Child).Children.Insert(0, seta);
                Grid.SetColumn(seta, 0);
                var cont = ToolChrome.Muted(doGrupo.Count.ToString(CultureInfo.InvariantCulture), 11);
                ((StackPanel)((Grid)cab.Child).Children.OfType<StackPanel>().Last()).Children.Add(cont);
                cab.Cursor = Cursors.Hand;
                cab.MouseLeftButtonUp += (s, e) =>
                {
                    bool abrir = corpo.Visibility != System.Windows.Visibility.Visible;
                    corpo.Visibility = abrir ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                    seta.Text = abrir ? "▾" : "▸";
                    ToolPrefs.Set(chave, abrir ? "1" : "0");
                    ToolPrefs.Save();
                };
                _lista.Children.Add(cab);
                _lista.Children.Add(corpo);
            }
        }

        private Border LinhaFerramenta(ToolInfo t, HashSet<string> favs)
        {
            string titulo = t.Title;
            bool ehFav = favs.Contains(titulo);
            var dica = new TextBlock { Text = t.Description + (t.Steps.Length > 0 ? "\n\n" + string.Join("\n", t.Steps.Select((x, i) => (i + 1) + ". " + x)) : ""), TextWrapping = TextWrapping.Wrap, MaxWidth = 360 };
            return Linha(IconFactory.Tool(64, t.Icon, t.Glyph, t.Accent), titulo, null, dica, () => RodarFerramenta(titulo),
                Mini(ehFav ? "★" : "☆", ehFav ? "Tirar das favoritas" : "Pôr nas favoritas", () =>
                {
                    if (!Automacao.Bib.Favoritos.Remove(titulo)) Automacao.Bib.Favoritos.Add(titulo);
                    Automacao.Salvar();
                    Mostrar();
                }),
                Menu("Mais", () =>
                {
                    var m = new ContextMenu();
                    m.Items.Add(Item("Rodar", () => RodarFerramenta(titulo)));
                    m.Items.Add(Item("Salvar opções como tarefa…", () => NovaTarefa(titulo)));
                    m.Items.Add(new Separator());
                    m.Items.Add(Item("Ajuda", () => HelpCenter.Open(t.Anchor)));
                    return m;
                }));
        }

        // ======================================================================== tasks

        private static string ResumoOpcoes(Tarefa t)
        {
            var linhas = new List<string>();
            if (!string.IsNullOrWhiteSpace(t.Descricao)) { linhas.Add(t.Descricao.Trim()); linhas.Add(""); }
            foreach (var kv in t.Valores.OrderBy(k => k.Key).Take(12))
            {
                string k = kv.Key.Contains('.') ? kv.Key.Substring(kv.Key.LastIndexOf('.') + 1) : kv.Key;
                string v = (kv.Value ?? "").Replace('\u001f', ',');
                if (v == "1") v = "sim"; else if (v == "0") v = "não";
                linhas.Add(k + ": " + (v.Length > 50 ? v.Substring(0, 50) + "…" : v));
            }
            if (t.Valores.Count > 12) linhas.Add($"… e mais {t.Valores.Count - 12}");
            if (t.Valores.Count == 0) linhas.Add("(sem opções salvas)");
            return string.Join("\n", linhas);
        }

        private void Tarefas()
        {
            var barra = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var mais = Menu("Importar, exportar, pasta do escritório", () =>
            {
                var m = new ContextMenu();
                m.Items.Add(Item("Importar…", Importar));
                m.Items.Add(Item("Exportar todas…", () => Exportar(Automacao.Bib.Tarefas.ToList(), new List<Pacote>(), "Tarefas JA")));
                m.Items.Add(Item("Pasta do escritório…", PastaEscritorio));
                return m;
            });
            DockPanel.SetDock(mais, Dock.Right);
            barra.Children.Add(mais);
            var nova = ToolChrome.Ghost("+ Nova tarefa", "Escolha a ferramenta: ela abre, você define as opções e salva.", null);
            nova.HorizontalAlignment = HorizontalAlignment.Left;
            nova.Click += (s, e) =>
            {
                var m = new ContextMenu { PlacementTarget = nova, Placement = PlacementMode.Bottom };
                foreach (string g in ToolCatalog.GroupOrder)
                {
                    var sub = new MenuItem { Header = g };
                    foreach (ToolInfo f in Automacao.FerramentasDoCatalogo().Where(x => x.Group == g))
                    {
                        string titulo = f.Title;
                        sub.Items.Add(Item(titulo, () => NovaTarefa(titulo)));
                    }
                    if (sub.Items.Count > 0) m.Items.Add(sub);
                }
                m.IsOpen = true;
            };
            barra.Children.Add(nova);
            _lista.Children.Add(barra);

            var tarefas = Automacao.TodasTarefas().OrderBy(t => t.Ferramenta).ThenBy(t => t.Nome).ToList();
            if (tarefas.Count == 0)
            {
                _lista.Children.Add(ToolChrome.Muted("Nenhuma tarefa ainda. Use “+ Nova tarefa”, o ☆ de uma ferramenta, ou o botão “☆ Tarefa” no rodapé de qualquer janela de opções do JA Tools.", 11.5));
                return;
            }
            foreach (Tarefa t in tarefas) _lista.Children.Add(LinhaTarefa(t));
        }

        private UIElement LinhaTarefa(Tarefa t)
        {
            bool aberto = _abertos.Contains(t.Id);
            var sp = new StackPanel();
            string sub = t.Ferramenta + (t.Interativa ? " · abre a janela" : "") + (t.DoEscritorio ? " · escritório" : "");
            var linha = Linha(IconeDe(t.Ferramenta), t.Nome, sub, null, () => { if (!_abertos.Remove(t.Id)) _abertos.Add(t.Id); Mostrar(); },
                Mini("▶", "Rodar com o escopo atual (sem abrir as opções)", () => RodarTarefa(t)),
                Menu("Mais", () => MenuTarefa(t)));
            sp.Children.Add(linha);
            if (aberto)
            {
                var det = new StackPanel { Margin = new Thickness(34, 0, 4, 8) };
                var txt = ToolChrome.Muted(ResumoOpcoes(t), 11);
                txt.TextWrapping = TextWrapping.Wrap;
                det.Children.Add(txt);
                if (t.Interativa) det.Children.Add(ToolChrome.Muted("Ferramenta com janela própria: ao rodar, ela abre já com estas opções e espera o seu clique.", 10.5));
                var bts = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
                var rodar = ToolChrome.Primary("Rodar", (s, e) => RodarTarefa(t));
                rodar.Padding = new Thickness(12, 3, 12, 3);
                rodar.Margin = new Thickness(0, 0, 6, 0);
                bts.Children.Add(rodar);
                if (!t.DoEscritorio)
                {
                    var conf = ToolChrome.Secondary("Configurar…", (s, e) => CentralHost.Executar(new PedidoCentral { Acao = PedidoCentral.Configurar, Alvo = t.Id }), "Abre a ferramenta com estas opções para mudar e salvar.");
                    conf.Padding = new Thickness(10, 3, 10, 3);
                    bts.Children.Add(conf);
                }
                det.Children.Add(bts);
                sp.Children.Add(det);
            }
            return sp;
        }

        private ContextMenu MenuTarefa(Tarefa t)
        {
            var m = new ContextMenu();
            m.Items.Add(Item("Rodar", () => RodarTarefa(t)));
            m.Items.Add(Item("Configurar…", () => CentralHost.Executar(new PedidoCentral { Acao = PedidoCentral.Configurar, Alvo = t.Id }), !t.DoEscritorio));
            m.Items.Add(Item("Renomear…", () =>
            {
                string n = Perguntar("Renomear tarefa", "Novo nome:", t.Nome);
                if (string.IsNullOrWhiteSpace(n)) return;
                t.Nome = n.Trim(); Automacao.Salvar(); Mostrar();
            }, !t.DoEscritorio));
            m.Items.Add(Item("Descrição…", () =>
            {
                string n = Perguntar("Descrição", "Para que serve esta tarefa:", t.Descricao);
                if (n == null) return;
                t.Descricao = n.Trim(); Automacao.Salvar(); Mostrar();
            }, !t.DoEscritorio));
            m.Items.Add(Item("Duplicar", () =>
            {
                var c = Automacao.NovaTarefa(t.Nome + " (cópia)", t.Ferramenta, new Dictionary<string, string>(t.Valores), t.Interativa);
                c.Descricao = t.Descricao; Automacao.Salvar(); Mostrar();
            }));
            var pac = new MenuItem { Header = "Adicionar ao pacote" };
            foreach (Pacote p in Automacao.Bib.Pacotes)
            {
                Pacote pp = p;
                pac.Items.Add(Item(p.Nome, () => { pp.Passos.Add(t.Id); Automacao.Salvar(); Aviso($"“{t.Nome}” entrou em “{pp.Nome}” (passo {pp.Passos.Count})."); }));
            }
            if (pac.Items.Count > 0) pac.Items.Add(new Separator());
            pac.Items.Add(Item("Novo pacote…", () =>
            {
                string n = Perguntar("Novo pacote", "Nome do pacote:", "Pacote " + (Automacao.Bib.Pacotes.Count + 1));
                if (string.IsNullOrWhiteSpace(n)) return;
                var p = new Pacote { Nome = n.Trim() };
                p.Passos.Add(t.Id);
                Automacao.Bib.Pacotes.Add(p); Automacao.Salvar();
                _abertos.Add(p.Id);
                Recarregar(AbaPacotes);
            }));
            m.Items.Add(pac);
            m.Items.Add(Item("Exportar…", () => Exportar(new List<Tarefa> { t }, new List<Pacote>(), t.Nome)));
            m.Items.Add(new Separator());
            m.Items.Add(Item("Excluir", () =>
            {
                if (!Confirma($"Excluir a tarefa “{t.Nome}”?")) return;
                Automacao.Bib.Tarefas.Remove(t); Automacao.Salvar(); Mostrar();
            }, !t.DoEscritorio));
            return m;
        }

        private void Importar()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Tarefas JA (*.jatarefas)|*.jatarefas|JSON|*.json" };
            if (dlg.ShowDialog() != true) return;
            try { Aviso($"{Automacao.Importar(dlg.FileName)} tarefa(s)/pacote(s) importados."); }
            catch (Exception ex) { Aviso("Arquivo ilegível: " + ex.Message); }
            Mostrar();
        }

        private void Exportar(List<Tarefa> tarefas, List<Pacote> pacotes, string nome)
        {
            if (tarefas.Count + pacotes.Count == 0) { Aviso("Nada para exportar."); return; }
            var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "Tarefas JA (*.jatarefas)|*.jatarefas", FileName = RevitUtil.Sanitize(nome) + ".jatarefas" };
            if (dlg.ShowDialog() != true) return;
            try { Automacao.Exportar(dlg.FileName, tarefas, pacotes); Aviso("Arquivo gravado. Para a equipe toda ver, ponha-o na pasta do escritório."); }
            catch (Exception ex) { Aviso(ex.Message); }
        }

        private void PastaEscritorio()
        {
            string n = Perguntar("Pasta do escritório", "Pasta compartilhada com os arquivos .jatarefas da equipe (vazio = nenhuma):", Automacao.PastaEscritorio);
            if (n == null) return;
            Automacao.PastaEscritorio = n.Trim().Trim('"');
            Mostrar();
        }

        // ======================================================================== bundles

        private void Pacotes()
        {
            var nova = ToolChrome.Ghost("+ Novo pacote", "Várias tarefas em ordem, rodadas com 1 clique.", (s, e) =>
            {
                string n = Perguntar("Novo pacote", "Nome do pacote (ex.: “Documentar apartamento”):", "Pacote " + (Automacao.Bib.Pacotes.Count + 1));
                if (string.IsNullOrWhiteSpace(n)) return;
                var p = new Pacote { Nome = n.Trim() };
                Automacao.Bib.Pacotes.Add(p); Automacao.Salvar();
                _abertos.Add(p.Id);
                Mostrar();
            });
            nova.HorizontalAlignment = HorizontalAlignment.Left;
            nova.Margin = new Thickness(0, 0, 0, 4);
            _lista.Children.Add(nova);
            var pacotes = Automacao.TodosPacotes();
            if (pacotes.Count == 0)
            {
                _lista.Children.Add(ToolChrome.Muted("Nenhum pacote. Crie um e adicione tarefas: elas rodam em ordem, com um clique, e o resultado vai para Status.", 11.5));
                return;
            }
            foreach (Pacote p in pacotes) _lista.Children.Add(LinhaPacote(p));
        }

        private UIElement LinhaPacote(Pacote p)
        {
            bool ro = p.DoEscritorio, aberto = _abertos.Contains(p.Id);
            var sp = new StackPanel();
            sp.Children.Add(Linha(IconePacote(), p.Nome, p.Passos.Count + " passo(s)" + (p.PararNoErro ? " · para no erro" : "") + (ro ? " · escritório" : ""), null,
                () => { if (!_abertos.Remove(p.Id)) _abertos.Add(p.Id); Mostrar(); },
                Mini("▶", "Rodar o pacote", () => RodarPacote(p)),
                Menu("Mais", () =>
                {
                    var m = new ContextMenu();
                    m.Items.Add(Item("Rodar", () => RodarPacote(p)));
                    m.Items.Add(Item("Renomear…", () =>
                    {
                        string n = Perguntar("Renomear pacote", "Novo nome:", p.Nome);
                        if (string.IsNullOrWhiteSpace(n)) return;
                        p.Nome = n.Trim(); Automacao.Salvar(); Mostrar();
                    }, !ro));
                    var parar = new MenuItem { Header = "Parar no primeiro erro", IsCheckable = true, IsChecked = p.PararNoErro, IsEnabled = !ro };
                    parar.Click += (s, e) => { p.PararNoErro = parar.IsChecked; Automacao.Salvar(); Mostrar(); };
                    m.Items.Add(parar);
                    m.Items.Add(Item("Exportar…", () => Exportar(new List<Tarefa>(), new List<Pacote> { p }, p.Nome)));
                    m.Items.Add(new Separator());
                    m.Items.Add(Item("Excluir", () =>
                    {
                        if (!Confirma($"Excluir o pacote “{p.Nome}”? As tarefas continuam.")) return;
                        Automacao.Bib.Pacotes.Remove(p); Automacao.Salvar(); Mostrar();
                    }, !ro));
                    return m;
                })));
            if (!aberto) return sp;

            var det = new StackPanel { Margin = new Thickness(34, 0, 4, 8) };
            for (int i = 0; i < p.Passos.Count; i++)
            {
                int k = i;
                var l = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
                if (!ro)
                {
                    var bts = new StackPanel { Orientation = Orientation.Horizontal };
                    bts.Children.Add(Mini("↑", "Subir", () => { if (k > 0) { var x = p.Passos[k]; p.Passos.RemoveAt(k); p.Passos.Insert(k - 1, x); Automacao.Salvar(); Mostrar(); } }));
                    bts.Children.Add(Mini("↓", "Descer", () => { if (k < p.Passos.Count - 1) { var x = p.Passos[k]; p.Passos.RemoveAt(k); p.Passos.Insert(k + 1, x); Automacao.Salvar(); Mostrar(); } }));
                    bts.Children.Add(Mini("✕", "Tirar do pacote", () => { p.Passos.RemoveAt(k); Automacao.Salvar(); Mostrar(); }));
                    DockPanel.SetDock(bts, Dock.Right);
                    l.Children.Add(bts);
                }
                var n = new TextBlock { Text = (i + 1) + ".  " + Automacao.NomePasso(p.Passos[i]), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
                n.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Ink");
                l.Children.Add(n);
                det.Children.Add(l);
            }
            if (p.Passos.Count == 0) det.Children.Add(ToolChrome.Muted("Sem passos ainda.", 11));
            var acoes = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            if (!ro)
            {
                var add = ToolChrome.Ghost("+ Passo", "Adicionar uma tarefa (ou uma ferramenta que abre a própria janela).", null);
                add.Margin = new Thickness(0, 0, 6, 0);
                add.Click += (s, e) =>
                {
                    var m = new ContextMenu { PlacementTarget = add, Placement = PlacementMode.Bottom };
                    var tar = new MenuItem { Header = "Tarefa" };
                    foreach (Tarefa t in Automacao.TodasTarefas().OrderBy(x => x.Nome))
                    {
                        string id = t.Id;
                        tar.Items.Add(Item(t.Nome, () => { p.Passos.Add(id); Automacao.Salvar(); Mostrar(); }));
                    }
                    if (tar.Items.Count == 0) tar.Items.Add(Item("(nenhuma tarefa)", () => { }, false));
                    m.Items.Add(tar);
                    var fer = new MenuItem { Header = "Ferramenta (abre a janela)" };
                    foreach (string g in ToolCatalog.GroupOrder)
                    {
                        var sub = new MenuItem { Header = g };
                        foreach (ToolInfo f in Automacao.FerramentasDoCatalogo().Where(x => x.Group == g))
                        {
                            string passo = "ferramenta:" + f.Title;
                            sub.Items.Add(Item(f.Title, () => { p.Passos.Add(passo); Automacao.Salvar(); Mostrar(); }));
                        }
                        if (sub.Items.Count > 0) fer.Items.Add(sub);
                    }
                    m.Items.Add(fer);
                    m.IsOpen = true;
                };
                acoes.Children.Add(add);
            }
            var rodar = ToolChrome.Primary("Rodar pacote", (s, e) => RodarPacote(p));
            rodar.Padding = new Thickness(12, 3, 12, 3);
            rodar.IsEnabled = p.Passos.Count > 0;
            acoes.Children.Add(rodar);
            det.Children.Add(acoes);
            sp.Children.Add(det);
            return sp;
        }

        // ======================================================================== status

        private void Status()
        {
            if (Automacao.Historico.Count == 0)
            {
                _lista.Children.Add(ToolChrome.Muted("Nenhuma tarefa ou pacote rodou nesta sessão. O resultado de cada passo — o que foi criado, avisos e erros — aparece aqui.", 11.5));
                return;
            }
            for (int n = 0; n < Automacao.Historico.Count; n++)
            {
                int i = n;
                StatusExecucao st = Automacao.Historico[i];
                string chave = "st:" + st.Quando.Ticks;
                bool aberto = i == 0 ? !_abertos.Contains(chave + ":f") : _abertos.Contains(chave);
                int ok = st.Passos.Count(p => p.Estado == StatusPasso.Ok), av = st.Passos.Count(p => p.Estado == StatusPasso.Aviso), er = st.Passos.Count(p => p.Estado == StatusPasso.Erro);
                string marca = er > 0 ? "✕" : av > 0 ? "!" : "✓";
                var cab = Linha(null, $"{marca}  {st.Titulo}", $"{st.Quando:HH:mm} · {st.Resumo}", null, () =>
                {
                    if (i == 0) { if (!_abertos.Remove(chave + ":f")) _abertos.Add(chave + ":f"); }
                    else if (!_abertos.Remove(chave)) _abertos.Add(chave);
                    Mostrar();
                });
                _lista.Children.Add(cab);
                if (!aberto) continue;
                var corpo = new StackPanel { Margin = new Thickness(10, 0, 4, 8) };
                foreach (StatusPasso p in st.Passos) corpo.Children.Add(Passo(p));
                _lista.Children.Add(corpo);
            }
        }

        private UIElement Passo(StatusPasso p)
        {
            string icone, cor;
            switch (p.Estado)
            {
                case StatusPasso.Ok: icone = "✓"; cor = "Ja.Ok"; break;
                case StatusPasso.Aviso: icone = "!"; cor = "Ja.Warn"; break;
                case StatusPasso.Erro: icone = "✕"; cor = "Ja.Error"; break;
                default: icone = "–"; cor = "Ja.Muted"; break;
            }
            var g = new Grid { Margin = new Thickness(0, 2, 0, 6) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var ic = new TextBlock { Text = icone, FontWeight = FontWeights.Bold, FontSize = 13 };
            ic.SetResourceReference(TextBlock.ForegroundProperty, cor);
            g.Children.Add(ic);
            var sp = new StackPanel();
            Grid.SetColumn(sp, 1);
            var n = new TextBlock { Text = p.Nome + $"  ·  {p.Segundos:0.0} s", FontWeight = FontWeights.SemiBold, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
            n.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Ink");
            sp.Children.Add(n);
            if (!string.IsNullOrWhiteSpace(p.Texto))
            {
                var t = ToolChrome.Muted(p.Texto.Trim(), 11);
                t.TextWrapping = TextWrapping.Wrap;
                t.MaxHeight = 46;
                t.TextTrimming = TextTrimming.CharacterEllipsis;
                t.ToolTip = new TextBlock { Text = p.Texto.Trim(), TextWrapping = TextWrapping.Wrap, MaxWidth = 420 };
                sp.Children.Add(t);
            }
            if (p.Criados.Count > 0 || p.VistasCriadas.Count > 0)
            {
                var bts = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
                if (p.Criados.Count > 0)
                    bts.Children.Add(Mini($"Selecionar criados ({p.Criados.Count})", "Seleciona no Revit o que este passo criou",
                        () => CentralHost.Executar(new PedidoCentral { Acao = PedidoCentral.Selecionar, Ids = p.Criados.ToList() })));
                if (p.VistasCriadas.Count > 0)
                    bts.Children.Add(Mini("Abrir vista criada", null, () => CentralHost.Executar(new PedidoCentral { Acao = PedidoCentral.Abrir, Vista = p.VistasCriadas[0] })));
                sp.Children.Add(bts);
            }
            g.Children.Add(sp);
            return g;
        }

        // ======================================================================== scope (unified selection)

        private static string EscopoTexto()
        {
            Escopo e = Automacao.Bib.Escopo;
            switch (e.Modo)
            {
                case Escopo.VistaAtiva: return "vista/folha ativa";
                case Escopo.Selecionados: return "selecionados no Revit";
                case Escopo.Filtrar:
                    return string.Join(", ", e.Categorias.Count == 0 ? new List<string> { "Ambientes" } : e.Categorias) +
                           (e.Niveis.Count > 0 ? " · " + string.Join(", ", e.Niveis) : "") + (string.IsNullOrEmpty(e.ScopeBox) ? "" : " · " + e.ScopeBox) +
                           (string.IsNullOrEmpty(e.Filtro) ? "" : " · “" + e.Filtro + "”");
                default: return "nenhum (como de costume)";
            }
        }

        private UIElement EditorEscopo()
        {
            Escopo e = Automacao.Bib.Escopo;
            var sp = new StackPanel();
            var seg = new UniformGrid { Rows = 1 };
            string[] nomes = { "Nenhum", "Vista", "Seleção", "Filtro" };
            string[] dicas = { "Cada ferramenta como de costume", "Vista ou folha ativa", "Elementos selecionados no Revit", "Por categoria, nível, scope box e nome" };
            var filtro = new StackPanel { Margin = new Thickness(0, 8, 0, 0), Visibility = e.Modo == Escopo.Filtrar ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed };
            for (int i = 0; i < 4; i++)
            {
                int k = i;
                var rb = new RadioButton { Content = nomes[i], ToolTip = dicas[i], GroupName = "central-escopo-" + (_lateral ? "l" : "j"), IsChecked = e.Modo == i, FontSize = 11.5, Padding = new Thickness(4, 2, 4, 2), HorizontalContentAlignment = HorizontalAlignment.Center };
                rb.SetResourceReference(StyleProperty, "Ja.Segment");
                rb.Checked += (s, a) =>
                {
                    e.Modo = k; Automacao.Salvar(); AtualizarCabecalho();
                    filtro.Visibility = k == Escopo.Filtrar ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                };
                seg.Children.Add(rb);
            }
            sp.Children.Add(ToolChrome.Track(seg));

            // categories
            var cats = new WrapPanel();
            foreach (string c in Automacao.CategoriasEscopo)
            {
                string cc = c;
                var cb = new CheckBox { Content = c, IsChecked = e.Categorias.Contains(c), Margin = new Thickness(0, 0, 10, 3), FontSize = 11.5 };
                cb.Checked += (s, a) => { if (!e.Categorias.Contains(cc)) e.Categorias.Add(cc); Automacao.Salvar(); AtualizarCabecalho(); };
                cb.Unchecked += (s, a) => { e.Categorias.Remove(cc); Automacao.Salvar(); AtualizarCabecalho(); };
                cats.Children.Add(cb);
            }
            filtro.Children.Add(ToolChrome.Labeled("Categorias (nenhuma = Ambientes)", cats));

            // levels and scope boxes come from the model: read them in Revit's context
            var niveis = new WrapPanel();
            niveis.Children.Add(ToolChrome.Muted("Lendo…", 11));
            filtro.Children.Add(ToolChrome.Labeled("Níveis (nenhum = todos)", niveis));
            var caixa = new ComboBox();
            caixa.Items.Add("(nenhuma)");
            caixa.SelectedIndex = 0;
            filtro.Children.Add(ToolChrome.Labeled("Scope box", caixa));
            var nome = new TextBox { Text = e.Filtro };
            nome.LostFocus += (s, a) => { e.Filtro = nome.Text; Automacao.Salvar(); AtualizarCabecalho(); };
            filtro.Children.Add(ToolChrome.Labeled("Nome contém", nome, "Ex.: “suíte”, “101”, “WC”. Sem diferença de acentos."));
            CentralHost.Pedir(app =>
            {
                DB.Document doc = app.ActiveUIDocument?.Document;
                var lv = doc == null ? new List<string>() : new DB.FilteredElementCollector(doc).OfClass(typeof(DB.Level)).Cast<DB.Level>().OrderBy(l => l.Elevation).Select(l => l.Name).ToList();
                var cx = doc == null ? new List<string>() : new DB.FilteredElementCollector(doc).OfCategory(DB.BuiltInCategory.OST_VolumeOfInterest).WhereElementIsNotElementType().Select(x => x.Name).OrderBy(x => x).ToList();
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    niveis.Children.Clear();
                    foreach (string n in lv)
                    {
                        string nn = n;
                        var cb = new CheckBox { Content = n, IsChecked = e.Niveis.Contains(n), Margin = new Thickness(0, 0, 10, 3), FontSize = 11.5 };
                        cb.Checked += (s, a) => { if (!e.Niveis.Contains(nn)) e.Niveis.Add(nn); Automacao.Salvar(); AtualizarCabecalho(); };
                        cb.Unchecked += (s, a) => { e.Niveis.Remove(nn); Automacao.Salvar(); AtualizarCabecalho(); };
                        niveis.Children.Add(cb);
                    }
                    if (lv.Count == 0) niveis.Children.Add(ToolChrome.Muted("Nenhum projeto aberto.", 11));
                    foreach (string c in cx) caixa.Items.Add(c);
                    caixa.SelectedItem = !string.IsNullOrEmpty(e.ScopeBox) && cx.Contains(e.ScopeBox) ? (object)e.ScopeBox : "(nenhuma)";
                    caixa.SelectionChanged += (s, a) => { e.ScopeBox = caixa.SelectedIndex <= 0 ? "" : (string)caixa.SelectedItem; Automacao.Salvar(); AtualizarCabecalho(); };
                }));
            });

            // preview with hand-picking
            var previa = new StackPanel();
            var rolar = new ScrollViewer { MaxHeight = 190, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = previa };
            var bts = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
            var ver = ToolChrome.Secondary("Pré-visualizar", null, "Lista o que entra no escopo; desmarque para tirar.");
            ver.Padding = new Thickness(10, 3, 10, 3);
            ver.Margin = new Thickness(0, 0, 6, 0);
            ver.Click += (s, a) =>
            {
                e.Filtro = nome.Text; Automacao.Salvar();
                previa.Children.Clear();
                previa.Children.Add(ToolChrome.Muted("Lendo…", 11));
                var tmp = new Escopo { Modo = Escopo.Filtrar, Categorias = e.Categorias, Niveis = e.Niveis, ScopeBox = e.ScopeBox, Filtro = e.Filtro };
                CentralHost.Pedir(app =>
                {
                    var uidoc = app.ActiveUIDocument;
                    if (uidoc == null) return;
                    var r = Automacao.Resolver(uidoc, tmp);
                    var itens = r.Elementos.Take(300).Select(id => (id: id.Value, nome: Automacao.Rotulo(uidoc.Document.GetElement(id)))).ToList();
                    int total = r.Elementos.Count;
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        previa.Children.Clear();
                        var excl = new HashSet<long>(e.Excluidos);
                        previa.Children.Add(ToolChrome.Muted($"{total} encontrado(s)" + (total > 300 ? " (mostrando 300)" : ""), 11));
                        foreach (var it in itens)
                        {
                            long v = it.id;
                            var cb = new CheckBox { Content = it.nome, IsChecked = !excl.Contains(v), FontSize = 11.5, Margin = new Thickness(0, 1, 0, 1) };
                            cb.Checked += (s2, a2) => { e.Excluidos.Remove(v); Automacao.Salvar(); };
                            cb.Unchecked += (s2, a2) => { if (!e.Excluidos.Contains(v)) e.Excluidos.Add(v); Automacao.Salvar(); };
                            previa.Children.Add(cb);
                        }
                    }));
                });
            };
            bts.Children.Add(ver);
            bts.Children.Add(Mini("Selecionar no Revit", "Seleciona os elementos do escopo", () =>
            {
                e.Filtro = nome.Text; Automacao.Salvar();
                CentralHost.Pedir(app =>
                {
                    if (app.ActiveUIDocument == null) return;
                    var r = Automacao.Resolver(app.ActiveUIDocument, Automacao.Bib.Escopo);
                    CentralExecutor.Executar(app, new PedidoCentral { Acao = PedidoCentral.Selecionar, Ids = r.Elementos });
                });
            }));
            bts.Children.Add(Mini("Limpar escolhas", "Volta a incluir os itens desmarcados", () => { e.Excluidos.Clear(); Automacao.Salvar(); previa.Children.Clear(); }));
            filtro.Children.Add(bts);
            filtro.Children.Add(rolar);
            sp.Children.Add(filtro);

            var card = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(8) };
            card.SetResourceReference(Border.BackgroundProperty, "Ja.Card");
            card.SetResourceReference(Border.BorderBrushProperty, "Ja.Line");
            card.Child = sp;
            return card;
        }
    }

    /// <summary>The Central as a floating, non-blocking window (position and size remembered).</summary>
    public sealed class CentralJanela : Window
    {
        private const string Pref = "central.janela";
        public CentralPainel Painel { get; }

        public CentralJanela(IntPtr revit)
        {
            Title = "Central JA";
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            MinWidth = 280; MinHeight = 360;
            Width = 380; Height = Math.Min(760, SystemParameters.WorkArea.Height - 80);
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = SystemParameters.WorkArea.Right - Width - 40;
            Top = SystemParameters.WorkArea.Top + 80;
            string[] g = (ToolPrefs.Get(Pref) ?? "").Split(';');
            if (g.Length == 4 && g.All(x => double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            {
                double[] v = g.Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                var area = SystemParameters.VirtualScreenWidth > 0
                    ? new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight)
                    : SystemParameters.WorkArea;
                if (area.Contains(new Point(v[0] + 40, v[1] + 20))) { Left = v[0]; Top = v[1]; Width = Math.Max(MinWidth, v[2]); Height = Math.Max(MinHeight, v[3]); }
            }
            JaTheme.Apply(this, Color.FromRgb(0x2B, 0x57, 0x9A));
            if (revit != IntPtr.Zero) new System.Windows.Interop.WindowInteropHelper(this).Owner = revit;
            Painel = new CentralPainel(false);
            Content = Painel;
            Closing += (s, e) =>
            {
                if (WindowState == WindowState.Normal)
                {
                    ToolPrefs.Set(Pref, string.Join(";", new[] { Left, Top, Width, Height }.Select(x => x.ToString("0", CultureInfo.InvariantCulture))));
                    ToolPrefs.Save();
                }
            };
        }
    }
}
