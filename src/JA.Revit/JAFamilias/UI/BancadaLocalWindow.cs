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
using System.Windows.Shapes;
using JAEsquadrias.Core;
using JAEsquadrias.Engine;
using static JAEsquadrias.UI.FormKit;

namespace JAEsquadrias.UI
{
    /// <summary>What the user asked for when the editor of a countertop modelled in place closed.</summary>
    public enum AcaoBancadaLocal { Gerar, Contorno, Divisao, Cuba, Cooktop, Furo }

    public sealed class BancadaLocalPedido
    {
        public BancadaConfig Config;
        public AcaoBancadaLocal Acao;
    }

    /// <summary>
    /// Bancada no Local: editor of a countertop whose outline was drawn in the project. Left: the
    /// plan exactly as in the project (walls and columns around in grey) — click an edge to say
    /// what it gets (saia, frontão, leg, nothing), click an area to make it wet or dry — or a 3D
    /// view. Right: what is done back in the project (redraw the outline, divide areas, place
    /// sinks and the cooktop: the window closes, the user clicks in the plan and it opens again)
    /// and the measures, edges, sinks and materials.
    /// </summary>
    public sealed class BancadaLocalWindow
    {
        public static BancadaLocalPedido Abrir(IntPtr owner, string toolTitle, BancadaConfig cfg, List<KeyValuePair<P2, P2>> contexto,
                                               IList<string> materiais, HashSet<string> usados, bool editando, string nota)
        {
            var bw = new BancadaLocalWindow(toolTitle, cfg, contexto, materiais, usados, editando, nota);
            if (owner != IntPtr.Zero) new WindowInteropHelper(bw._w).Owner = owner;
            bw._w.ShowDialog();
            return bw._pedido;
        }

        private static readonly string[] NomesBorda = { "Livre com saia", "Livre sem saia", "Parede com frontão", "Parede sem frontão", "Pé de pedra" };
        private static readonly Color[] CoresBorda =
        {
            Color.FromRgb(0xD9, 0x7A, 0x1E),   // livre com saia / tira
            Color.FromRgb(0x6B, 0x72, 0x80),   // livre sem acabamento
            Color.FromRgb(0x1F, 0x8A, 0x5B),   // parede com frontão
            Color.FromRgb(0x9A, 0x8F, 0x7E),   // parede sem frontão
            Color.FromRgb(0x7B, 0x4F, 0xB5),   // pé
        };

        private readonly Window _w;
        private readonly ToolInfo _info;
        private readonly BancadaConfig _cfg;
        private readonly List<KeyValuePair<P2, P2>> _ctx;
        private readonly List<string> _materiais;
        private readonly HashSet<string> _usados;
        private readonly bool _editando;
        private readonly string _nota;
        private readonly HashSet<TextBox> _invalid = new HashSet<TextBox>();
        private readonly HashSet<int> _sel = new HashSet<int>();
        private BancadaLocalPedido _pedido;
        private PecaModelo _model;
        private List<ZonaBancada> _zonas = new List<ZonaBancada>();
        private bool _3d;

        private Canvas _canvas;
        private StackPanel _editor;
        private WrapPanel _stats;
        private TextBlock _status, _codeError, _badge;
        // plan → screen
        private double _k = 1, _ox, _oy;

        private BancadaLocalWindow(string toolTitle, BancadaConfig cfg, List<KeyValuePair<P2, P2>> contexto, IList<string> materiais,
                                   HashSet<string> usados, bool editando, string nota)
        {
            _info = ToolCatalog.Get(toolTitle);
            _cfg = (BancadaConfig)cfg.Copiar();
            _ctx = contexto ?? new List<KeyValuePair<P2, P2>>();
            _materiais = (materiais ?? new List<string>()).Where(m => !string.IsNullOrWhiteSpace(m)).Distinct().OrderBy(m => m, StringComparer.CurrentCultureIgnoreCase).ToList();
            _usados = usados ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _editando = editando;
            _nota = nota;

            var area = SystemParameters.WorkArea;
            _w = new Window
            {
                Width = Math.Min(1280, area.Width * 0.96),
                Height = Math.Min(880, area.Height * 0.94),
                MinWidth = 980,
                MinHeight = 620,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
                ResizeMode = ResizeMode.CanResizeWithGrip
            };

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 420 });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(430) });
            var left = BuildPreview();
            left.Margin = new Thickness(0, 0, 12, 12);
            body.Children.Add(left);
            _editor = new StackPanel();
            var right = new ScrollViewer { Content = _editor, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 6, 0) };
            Grid.SetColumn(right, 1);
            body.Children.Add(right);

            ToolChrome.Dress(_w, toolTitle, body, BuildFooter());
            _w.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { Finish(AcaoBancadaLocal.Gerar); e.Handled = true; }
            };
            _w.Loaded += (s, e) => Refresh();
            RebuildEditor();
        }

        // =========================================================================================
        // layout
        // =========================================================================================
        private Border BuildPreview()
        {
            var badge = ToolChrome.Badge(out _badge);
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            var vista = Pills(new[] { "Planta (editar)", "3D" }, 0, i => { _3d = i == 1; Draw(); });
            vista.Margin = new Thickness(0, 0, 8, -6);
            head.Children.Add(vista);
            head.Children.Add(badge);

            var dock = new DockPanel();
            _stats = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            DockPanel.SetDock(_stats, Dock.Bottom);
            dock.Children.Add(_stats);

            var legenda = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            for (int i = 0; i < NomesBorda.Length; i++) legenda.Children.Add(Chip(CoresBorda[i], NomesBorda[i], false));
            legenda.Children.Add(Chip(Color.FromRgb(0x9F, 0xD3, 0xEE), "Área molhada", true));
            legenda.Children.Add(Chip(Color.FromRgb(0xE9, 0xE4, 0xDA), "Área seca", true));
            DockPanel.SetDock(legenda, Dock.Bottom);
            dock.Children.Add(legenda);

            var h = ToolChrome.Muted("Planta como no projeto (paredes e pilares em cinza). Clique numa borda para escolher o que ela recebe (Ctrl+clique soma bordas); clique numa área para marcar molhada ou seca.", 11.5);
            h.Margin = new Thickness(0, 6, 0, 0);
            DockPanel.SetDock(h, Dock.Bottom);
            dock.Children.Add(h);

            _canvas = new Canvas { Background = Brushes.White, ClipToBounds = true, Cursor = Cursors.Hand };
            _canvas.SizeChanged += (s, e) => Draw();
            _canvas.MouseLeftButtonDown += (s, e) => Clicar(e.GetPosition(_canvas));
            var frame = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Child = _canvas, MinHeight = 280 };
            frame.SetResourceReference(Border.BorderBrushProperty, "Ja.Line");
            dock.Children.Add(frame);
            return ToolChrome.Card(dock, "Bancada no local", head);
        }

        private static FrameworkElement Chip(Color c, string text, bool area)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 2) };
            sp.Children.Add(new Border
            {
                Width = area ? 14 : 18, Height = area ? 12 : 4, Background = new SolidColorBrush(c), CornerRadius = new CornerRadius(2),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0)
            });
            sp.Children.Add(ToolChrome.Muted(text, 11));
            return sp;
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
            var primary = ToolChrome.Primary(_editando ? "Atualizar bancada" : "Gerar bancada no local", (s, e) => Finish(AcaoBancadaLocal.Gerar));
            primary.MinWidth = 170;
            primary.ToolTip = _editando ? "Regera a família; a bancada no modelo é atualizada no lugar. (Ctrl+Enter)"
                                        : "Gera a família com este contorno e a coloca exatamente onde foi desenhada. (Ctrl+Enter)";
            right.Children.Add(primary);
            dock.Children.Add(right);
            _status = ToolChrome.Muted("", 11.5);
            _status.VerticalAlignment = VerticalAlignment.Center;
            dock.Children.Add(_status);
            return dock;
        }

        // =========================================================================================
        // editor (right side)
        // =========================================================================================
        private void RebuildEditor()
        {
            _invalid.Clear();
            _editor.Children.Clear();
            var b = _cfg;

            if (!string.IsNullOrWhiteSpace(_nota))
            {
                var t = new TextBlock { Text = _nota, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
                t.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Warn");
                _editor.Children.Add(ToolChrome.Card(t));
            }

            // ---- back to the project
            var proj = new StackPanel();
            var botoes = new WrapPanel();
            botoes.Children.Add(Acao("Dividir áreas", "Clique dois pontos (ou mais) de uma borda à outra: a linha separa o tampo em duas áreas. Depois clique numa área, aqui na planta, para marcá-la como molhada.", AcaoBancadaLocal.Divisao));
            botoes.Children.Add(Acao("Posicionar cuba", "Clique no projeto o centro da cuba; ela fica alinhada com a parede mais próxima (o ângulo pode ser ajustado aqui).", AcaoBancadaLocal.Cuba));
            botoes.Children.Add(Acao("Posicionar cooktop", "Clique no projeto o centro do recorte do cooktop.", AcaoBancadaLocal.Cooktop));
            botoes.Children.Add(Acao("Furos", "Clique no projeto o centro de cada furo (torneira, dosador, passa-cabo); Esc termina. O diâmetro é ajustado aqui.", AcaoBancadaLocal.Furo));
            botoes.Children.Add(Acao("Redesenhar contorno", "Clique de novo os cantos do tampo no projeto (as divisões são refeitas depois).", AcaoBancadaLocal.Contorno));
            proj.Children.Add(botoes);
            proj.Children.Add(ToolChrome.Muted("Esta janela fecha, você clica na planta (com os snaps do Revit; Esc termina) e ela volta com o resultado.", 11));
            _editor.Children.Add(ToolChrome.Card(proj, "Modelar no projeto"));

            // ---- selected edges
            var sel = new StackPanel();
            if (_sel.Count == 0)
                sel.Children.Add(ToolChrome.Muted("Clique numa borda da planta. As bordas que tocam parede ou pilar já vêm como “Parede com frontão”; as outras, como “Livre com saia”.", 11.5));
            else
            {
                var idx = _sel.Where(i => i < b.Arestas.Count).OrderBy(i => i).ToList();
                var tipos = idx.Select(i => b.Arestas[i]).Distinct().ToList();
                double total = idx.Sum(i => Poligono.Dist(b.Contorno[i].P, b.Contorno[(i + 1) % b.Contorno.Count].P));
                sel.Children.Add(ToolChrome.Muted(idx.Count == 1 ? "Borda de " + PecaUtil.Mm(total) + " mm" : idx.Count + " bordas, " + PecaUtil.Mm(total) + " mm no total", 11.5));
                var pills = Pills(NomesBorda, tipos.Count == 1 ? (int)tipos[0] : -1, i =>
                {
                    foreach (int k in idx) b.Arestas[k] = (BordaLocal)i;
                    RebuildEditor();
                    Refresh();
                });
                pills.Margin = new Thickness(0, 6, 0, 0);
                sel.Children.Add(pills);
                sel.Children.Add(ToolChrome.Muted("Saia / tira e frontão usam as medidas do cartão Bordas. Pé de pedra: a borda livre ganha um pé até o piso.", 11));
            }
            var todas = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            todas.Children.Add(Small("Selecionar as livres", "Seleciona todas as bordas livres", () => Selecionar(t => t == BordaLocal.Livre || t == BordaLocal.LivreSemAcabamento || t == BordaLocal.Pe)));
            todas.Children.Add(Small("Selecionar as de parede", "Seleciona todas as bordas encostadas", () => Selecionar(t => t == BordaLocal.Parede || t == BordaLocal.ParedeSemFrontao)));
            sel.Children.Add(todas);
            _editor.Children.Add(ToolChrome.Card(sel, _sel.Count > 1 ? "Bordas selecionadas" : "Borda selecionada"));

            // ---- dry / wet areas
            var ar = new StackPanel();
            int nd = b.Divisoes.Count;
            ar.Children.Add(ToolChrome.Muted(nd == 0
                ? "O tampo é uma área só. Use “Dividir áreas” para separar a parte molhada (cuba, escorredor) da seca; depois clique na área para marcá-la."
                : (nd + 1) + " áreas. Clique numa área da planta para alternar entre molhada e seca.", 11.5));
            ar.Children.Add(Field("Rebaixo", NumBox(b.RebaixoMm, v => b.RebaixoMm = v, 0, 100, true), "Quanto a área molhada fica abaixo da seca (10 a 20 mm usual). Vazio ou 0 = mesmo nível, só dividida."));
            ar.Children.Add(Field("Borda de contenção", NumBox(b.ContencaoMm, v => b.ContencaoMm = v, 0, 200, true),
                "Largura da borda que volta ao nível da área seca nas bordas livres da área molhada. Vazio = sem borda. Com tento ativo, o tento faz esse papel."));
            if (nd > 0)
            {
                var wp = new WrapPanel();
                wp.Children.Add(Small("Remover a última divisão", null, () => { b.Divisoes.RemoveAt(b.Divisoes.Count - 1); LimparMarcas(); RebuildEditor(); Refresh(); }));
                wp.Children.Add(Small("Remover todas", null, () => { b.Divisoes.Clear(); LimparMarcas(); RebuildEditor(); Refresh(); }));
                ar.Children.Add(wp);
            }
            _editor.Children.Add(ToolChrome.Card(ar, "Áreas seca e molhada"));

            // ---- measures
            var dim = new StackPanel();
            dim.Children.Add(Field("Espessura da pedra", NumBox(b.EspessuraMm, v => b.EspessuraMm = v, 10, 100), "Granito/mármore 20 ou 30 mm; quartzo 20 mm."));
            dim.Children.Add(Field("Altura", NumBox(b.AlturaMm, v => b.AlturaMm = v, b.EspessuraMm, 1500), "Do piso (nível da planta) ao topo do tampo. Soleira ou peça de piso: a própria espessura."));
            _editor.Children.Add(ToolChrome.Card(dim, "Medidas"));

            // ---- what the piece is: the same in-place outline, other defaults
            var uso = new WrapPanel();
            uso.Children.Add(Small("Bancada", "Altura 900 mm; bordas livres com saia, de parede com frontão.", () =>
                Preset(900, BordaLocal.Livre, BordaLocal.Parede, BordaFrontal.Saia, "Bancada")));
            uso.Children.Add(Small("Soleira / peça de piso", "Topo na espessura da pedra acima do nível, sem saia, frontão, tento, cubas nem divisões (soleira, degrau, tampo de jardineira, piso de box).", () =>
                Preset(b.EspessuraMm, BordaLocal.LivreSemAcabamento, BordaLocal.ParedeSemFrontao, BordaFrontal.Reta, "Soleira")));
            uso.Children.Add(Small("Prateleira / tampo simples", "Mesma altura, só o tampo: sem saia nem frontão (prateleira, tampo de nicho, aparador).", () =>
                Preset(b.AlturaMm, BordaLocal.LivreSemAcabamento, BordaLocal.ParedeSemFrontao, BordaFrontal.Reta, "Prateleira")));
            var usoSp = new StackPanel();
            usoSp.Children.Add(uso);
            usoSp.Children.Add(ToolChrome.Muted("O contorno fica; muda a altura e o que cada borda recebe (dá para ajustar borda por borda depois).", 11));
            _editor.Children.Add(ToolChrome.Card(usoSp, "Usar como"));

            // ---- edges
            var bd = new StackPanel();
            bd.Children.Add(Field("Bordas livres", Pills(new[] { "Reta", "Saia", "Tira americana" }, (int)b.Borda, i => { b.Borda = (BordaFrontal)i; RebuildEditor(); Refresh(); }),
                b.Borda == BordaFrontal.Saia ? "Faixa de pedra vertical sob as bordas “Livre com saia”." : b.Borda == BordaFrontal.TiraAmericana ? "Tira horizontal colada por baixo das bordas “Livre com saia”." : "Só a espessura da pedra em todas as bordas livres."));
            if (b.Borda != BordaFrontal.Reta)
                bd.Children.Add(Field(b.Borda == BordaFrontal.Saia ? "Altura da saia" : "Altura da tira", NumBox(b.BordaAlturaMm, v => b.BordaAlturaMm = v, 5, 400), "Abaixo do tampo."));
            if (b.Borda == BordaFrontal.TiraAmericana)
                bd.Children.Add(Field("Largura da tira", NumBox(b.TiraLarguraMm, v => b.TiraLarguraMm = v, 10, 200), null));
            bd.Children.Add(Field("Altura do frontão", NumBox(b.FrontaoAlturaMm, v => b.FrontaoAlturaMm = v, 10, 2000), "Acima do tampo, nas bordas “Parede com frontão”."));
            bd.Children.Add(Field("Espessura do frontão", NumBox(b.FrontaoEspessuraMm, v => b.FrontaoEspessuraMm = v, 5, 60), null));
            bd.Children.Add(Check("Tento (ressalto) nas bordas livres", b.Tento, v => { b.Tento = v; RebuildEditor(); }, "Segura a água sobre a bancada."));
            if (b.Tento)
            {
                bd.Children.Add(Field("Altura do tento", NumBox(b.TentoAlturaMm, v => b.TentoAlturaMm = v, 3, 100), null));
                bd.Children.Add(Field("Largura do tento", NumBox(b.TentoLarguraMm, v => b.TentoLarguraMm = v, 5, 100), null));
            }
            bd.Children.Add(Field("Aresta de cima", Pills(new[] { "Reta", "Chanfrada" }, (int)b.Perfil, i => { b.Perfil = (PerfilBorda)i; RebuildEditor(); Refresh(); }), null));
            if (b.Perfil == PerfilBorda.Chanfrada)
                bd.Children.Add(Field("Tamanho do chanfro", NumBox(b.ChanfroBordaMm, v => b.ChanfroBordaMm = v, 1, 50), "Na quina de cima das bordas livres."));
            if (b.Arestas.Contains(BordaLocal.Pe))
                bd.Children.Add(Field("Espessura do pé", NumBox(b.PeEspessuraMm, v => b.PeEspessuraMm = v, 10, 100), null));
            _editor.Children.Add(ToolChrome.Card(bd, "Bordas"));

            // ---- sinks
            for (int i = 0; i < b.Cubas.Count; i++)
            {
                var cu = b.Cubas[i];
                int k = i;
                var c = new StackPanel();
                c.Children.Add(Field("Tipo", Pills(new[] { "Embutir", "Sobrepor", "Esculpida", "Apoio" }, (int)cu.Tipo, t => { cu.Tipo = (TipoCuba)t; Refresh(); }), null));
                c.Children.Add(Check("Cuba dupla (duas bacias lado a lado)", cu.Dupla, v => { cu.Dupla = v; if (v && cu.LarguraMm < 760) cu.LarguraMm = 800; RebuildEditor(); }, null));
                c.Children.Add(Field("Largura", NumBox(cu.LarguraMm, v => cu.LarguraMm = v, 100, 3000), cu.Dupla ? "Total das duas bacias e do septo." : "Do recorte (ou da cuba de apoio)."));
                if (cu.Dupla)
                {
                    c.Children.Add(Field("Septo entre as bacias", NumBox(cu.SeptoMm, v => cu.SeptoMm = v, 10, 400), null));
                    c.Children.Add(Field("Bacia da esquerda", NumBox(cu.ProporcaoPct, v => cu.ProporcaoPct = v, 20, 80, unit: "%"), "50 = bacias iguais."));
                }
                c.Children.Add(Field("Frente-fundo", NumBox(cu.ProfundidadeMm, v => cu.ProfundidadeMm = v, 100, 1000), null));
                c.Children.Add(Field("Profundidade da cuba", NumBox(cu.FundoMm, v => cu.FundoMm = v, 30, 500), null));
                c.Children.Add(Field("Raio dos cantos", NumBox(cu.RaioMm, v => cu.RaioMm = v, 0, 200, true), null));
                c.Children.Add(Field("Ângulo", Angulo(cu.AnguloGraus, v => cu.AnguloGraus = v), "Giro em planta. Ao posicionar, a cuba já vem alinhada com a parede mais próxima (torneira do lado da parede)."));
                c.Children.Add(Check("Furo para torneira de mesa", cu.FuroTorneira, v => cu.FuroTorneira = v, null));
                var remove = Small("Remover", "Tira esta cuba", () => { b.Cubas.RemoveAt(k); RebuildEditor(); Refresh(); });
                _editor.Children.Add(ToolChrome.Card(c, "Cuba " + (i + 1), remove));
            }
            if (b.Cooktop)
            {
                var ck = new StackPanel();
                ck.Children.Add(Field("Largura", NumBox(b.CooktopLarguraMm, v => b.CooktopLarguraMm = v, 200, 1200), "Do recorte (veja o manual do cooktop)."));
                ck.Children.Add(Field("Frente-fundo", NumBox(b.CooktopProfundidadeMm, v => b.CooktopProfundidadeMm = v, 200, 800), null));
                ck.Children.Add(Field("Ângulo", Angulo(b.CooktopAnguloGraus, v => b.CooktopAnguloGraus = v), null));
                var remove = Small("Remover", "Tira o recorte do cooktop", () => { b.Cooktop = false; RebuildEditor(); Refresh(); });
                _editor.Children.Add(ToolChrome.Card(ck, "Cooktop", remove));
            }

            if (b.Furos.Count > 0)
            {
                var fs = new StackPanel();
                for (int i = 0; i < b.Furos.Count; i++)
                {
                    Furo fu = b.Furos[i];
                    int k = i;
                    var linha = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
                    var nomeFuro = new TextBlock { Text = "Furo " + (i + 1) + "  ⌀", Width = 70, VerticalAlignment = VerticalAlignment.Center };
                    nomeFuro.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Text");
                    linha.Children.Add(nomeFuro);
                    linha.Children.Add(NumBox(fu.DiametroMm, v => fu.DiametroMm = v, 5, 400));
                    var tirar = Small("Remover", null, () => { b.Furos.RemoveAt(k); RebuildEditor(); Refresh(); });
                    tirar.Margin = new Thickness(10, 0, 0, 0);
                    linha.Children.Add(tirar);
                    fs.Children.Add(linha);
                }
                fs.Children.Add(ToolChrome.Muted("Torneira de mesa: 35 mm. Dosador: 28 a 32 mm. Passa-cabo: 60 mm.", 11));
                _editor.Children.Add(ToolChrome.Card(fs, "Furos"));
            }

            // ---- materials
            var mat = new StackPanel();
            mat.Children.Add(Field("Pedra", MaterialInput(b.MaterialPedra, v => b.MaterialPedra = v), "Se o projeto já tiver um material com este nome, ele é usado."));
            mat.Children.Add(Field("Cor (prévia)", HexInput(b.CorPedra, v => b.CorPedra = v), null));
            if (b.Cubas.Any(cu => cu.Tipo != TipoCuba.Esculpida))
            {
                mat.Children.Add(Field("Cuba", MaterialInput(b.MaterialCuba, v => b.MaterialCuba = v), null));
                mat.Children.Add(Field("Cor (prévia)", HexInput(b.CorCuba, v => b.CorCuba = v), null));
            }
            _editor.Children.Add(ToolChrome.Card(mat, "Materiais"));

            // ---- identification
            var id = new StackPanel();
            var code = new TextBox { Text = b.Codigo, IsReadOnly = _editando, MaxLength = 20 };
            _codeError = new TextBlock { Foreground = ErrorBrush, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
            code.TextChanged += (s, e) => { b.Codigo = code.Text.Trim(); ValidateCode(); };
            var codeBox = new StackPanel();
            codeBox.Children.Add(code);
            codeBox.Children.Add(_codeError);
            id.Children.Add(Field("Código", codeBox, _editando ? "O código identifica a família no projeto e não muda na edição." : "Marca de tipo e nome da família (JA Bancada " + b.Codigo + "). Cada bancada no local tem o seu."));
            var nome = new TextBox { Text = b.Nome ?? "" };
            nome.TextChanged += (s, e) => b.Nome = nome.Text;
            id.Children.Add(Field("Descrição", nome, "Opcional, ex.: Bancada da cozinha. Vai para a Descrição."));
            _editor.Children.Add(ToolChrome.Card(id, "Identificação"));
            ValidateCode();
        }

        private Button Acao(string text, string tip, AcaoBancadaLocal acao)
        {
            var bt = ToolChrome.Secondary(text, (s, e) => Finish(acao), tip);
            bt.Margin = new Thickness(0, 0, 6, 6);
            return bt;
        }

        private void Selecionar(Func<BordaLocal, bool> quais)
        {
            _sel.Clear();
            for (int i = 0; i < _cfg.Arestas.Count; i++) if (quais(_cfg.Arestas[i])) _sel.Add(i);
            RebuildEditor();
            Draw();
        }

        /// <summary>Wet marks that no longer fall in exactly one area are dropped.</summary>
        private void LimparMarcas()
        {
            var zonas = _cfg.Zonas();
            _cfg.Molhadas.RemoveAll(m => !zonas.Any(z => Poligono.Dentro(z.Pts, m.P)));
        }

        // =========================================================================================
        // plan: drawing and clicks
        // =========================================================================================
        private void Refresh()
        {
            if (_canvas == null) return;
            try
            {
                _model = _cfg.Build();
                _zonas = _cfg.Zonas();
            }
            catch (Exception ex)
            {
                SetStatus("Erro ao montar a prévia: " + ex.Message, true);
                return;
            }
            Draw();
            if (_badge != null) _badge.Text = _cfg.Dimensoes();
            _stats.Children.Clear();
            foreach (string r in _model.Resumo) _stats.Children.Add(Stat(r));
            foreach (string a in _model.Avisos.Distinct())
            {
                var t = new TextBlock { Text = "⚠ " + a, TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Margin = new Thickness(0, 4, 0, 0) };
                t.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Warn");
                _stats.Children.Add(new Border { Child = t, MaxWidth = 900 });
            }
        }

        private Point S(P2 p) => new Point(_ox + p.X * _k, _oy - p.Y * _k);
        private P2 M(Point s) => new P2((s.X - _ox) / _k, (_oy - s.Y) / _k);

        private void Draw()
        {
            if (_canvas == null) return;
            double cw = _canvas.ActualWidth, ch = _canvas.ActualHeight;
            if (cw < 40 || ch < 40) return;
            if (_3d)
            {
                try { PecaRenderer.Draw(_canvas, _cfg, VistaPeca.Iso, new PecaRenderOptions { Accent = _info.Accent, Context = false }, cw, ch); }
                catch (Exception ex) { SetStatus("Erro ao desenhar a prévia: " + ex.Message, true); }
                return;
            }
            _canvas.Children.Clear();
            var pts = _cfg.Contorno.Select(q => q.P).ToList();
            if (pts.Count < 3) return;
            double x0 = pts.Min(p => p.X), x1 = pts.Max(p => p.X), y0 = pts.Min(p => p.Y), y1 = pts.Max(p => p.Y);
            double folga = Math.Max(250, Math.Max(x1 - x0, y1 - y0) * 0.12);
            x0 -= folga; x1 += folga; y0 -= folga; y1 += folga;
            _k = Math.Min(cw / (x1 - x0), ch / (y1 - y0));
            _ox = (cw - (x1 - x0) * _k) / 2 - x0 * _k;
            _oy = (ch + (y1 - y0) * _k) / 2 + y0 * _k;

            // surroundings
            var cinza = new SolidColorBrush(Color.FromRgb(0xB9, 0xB4, 0xAB));
            foreach (var seg in _ctx) Linha(S(seg.Key), S(seg.Value), cinza, 1.6, null);

            // areas
            var seca = new SolidColorBrush(Color.FromArgb(0xD0, 0xE9, 0xE4, 0xDA));
            var molhada = new SolidColorBrush(Color.FromArgb(0xD0, 0x9F, 0xD3, 0xEE));
            foreach (var z in _zonas) Poligono2D(z.Pts.Select(S), z.Molhada ? molhada : seca, null, 0);

            // dividing lines
            var azul = new SolidColorBrush(Color.FromRgb(0x1F, 0x5F, 0x8C));
            foreach (var z in _zonas)
                for (int i = 0; i < z.Arestas.Count; i++)
                    if (z.Arestas[i].Divisa) Linha(S(z.Pts[i]), S(z.Pts[(i + 1) % z.Pts.Count]), azul, 1.4, new DoubleCollection { 5, 3 });

            // sinks and cooktop
            var tinta = new SolidColorBrush(Color.FromRgb(0x2A, 0x33, 0x3D));
            var branco = new SolidColorBrush(Color.FromRgb(0xFB, 0xFB, 0xFB));
            int n = 0;
            foreach (var cu in _cfg.Cubas)
            {
                n++;
                double cx = cu.PosicaoXMm, cy = cu.PosicaoYMm, hw = cu.LarguraMm / 2, hd = cu.ProfundidadeMm / 2;
                if (cu.Dupla)
                {
                    double wl = (cu.LarguraMm - cu.SeptoMm) * cu.ProporcaoPct / 100;
                    Poligono2D(Poligono.Girar(Peca.RoundRect(cx - hw, cy - hd, cx - hw + wl, cy + hd, cu.RaioMm), cx, cy, cu.AnguloGraus).Select(S), branco, tinta, 1);
                    Poligono2D(Poligono.Girar(Peca.RoundRect(cx - hw + wl + cu.SeptoMm, cy - hd, cx + hw, cy + hd, cu.RaioMm), cx, cy, cu.AnguloGraus).Select(S), branco, tinta, 1);
                }
                else Poligono2D(Poligono.Girar(Peca.RoundRect(cx - hw, cy - hd, cx + hw, cy + hd, cu.RaioMm), cx, cy, cu.AnguloGraus).Select(S), branco, tinta, 1);
                if (cu.FuroTorneira && cu.Tipo != TipoCuba.Apoio)
                {
                    P2 tp = Poligono.Girar(new P2(cx, cy + hd + 55), cx, cy, cu.AnguloGraus);
                    Poligono2D(Peca.Circle(tp.X, tp.Y, 17.5, 12).Select(S), branco, tinta, 0.8);
                }
                Texto("Cuba " + n, S(new P2(cx, cy)), tinta, 10.5, true);
            }
            if (_cfg.Cooktop)
            {
                double cx = _cfg.CooktopPosicaoXMm, cy = _cfg.CooktopPosicaoYMm, hw = _cfg.CooktopLarguraMm / 2, hd = _cfg.CooktopProfundidadeMm / 2;
                Poligono2D(Poligono.Girar(Peca.Rect(cx - hw, cy - hd, cx + hw, cy + hd), cx, cy, _cfg.CooktopAnguloGraus).Select(S), branco, tinta, 1);
                if (_model != null) foreach (var l in _model.Planta) Linha(S(l.A), S(l.B), tinta, 0.8, null);
            }

            foreach (Furo fu in _cfg.Furos) Poligono2D(Peca.Circle(fu.X, fu.Y, fu.DiametroMm / 2, 16).Select(S), branco, tinta, 0.8);

            // outline: each edge in the colour of what it gets, with its length
            for (int i = 0; i < pts.Count; i++)
            {
                P2 a = pts[i], b = pts[(i + 1) % pts.Count];
                BordaLocal t = i < _cfg.Arestas.Count ? _cfg.Arestas[i] : BordaLocal.Livre;
                bool sel = _sel.Contains(i);
                var cor = new SolidColorBrush(CoresBorda[(int)t]);
                Point sa = S(a), sb = S(b);
                if (sel) Linha(sa, sb, new SolidColorBrush(Color.FromArgb(0x50, CoresBorda[(int)t].R, CoresBorda[(int)t].G, CoresBorda[(int)t].B)), 11, null);
                Linha(sa, sb, cor, t == BordaLocal.LivreSemAcabamento || t == BordaLocal.ParedeSemFrontao ? 2 : 3.5, null);
                double len = Poligono.Dist(a, b), tela = len * _k;
                if (tela > 46)
                {
                    // length, a little outside the top
                    double dx = (sb.X - sa.X) / tela, dy = (sb.Y - sa.Y) / tela;
                    var mid = new Point((sa.X + sb.X) / 2, (sa.Y + sb.Y) / 2);
                    var fora = new Point(mid.X - dy * 11, mid.Y + dx * 11);
                    if (Poligono.Dentro(pts, M(fora))) fora = new Point(mid.X + dy * 11, mid.Y - dx * 11);
                    Texto(PecaUtil.Mm(len), fora, cor, 10, true);
                }
            }
            foreach (P2 p in pts) Ponto(S(p), tinta);

            // area labels
            if (_zonas.Count > 1 || _zonas.Any(z => z.Molhada))
                foreach (var z in _zonas)
                    Texto((z.Molhada ? "Molhada " : "Seca ") + z.AreaM2.ToString("0.00", PecaUtil.Pt) + " m²", S(Poligono.Interior(z.Pts)), z.Molhada ? azul : tinta, 11, true, true);
        }

        private void Clicar(Point s)
        {
            if (_3d || _cfg.Contorno.Count < 3) return;
            var pts = _cfg.Contorno.Select(q => q.P).ToList();
            // an edge within 7 px?
            int best = -1;
            double bestD = 7;
            for (int i = 0; i < pts.Count; i++)
            {
                Point a = S(pts[i]), b = S(pts[(i + 1) % pts.Count]);
                double d = Poligono.DistSeg(new P2(s.X, s.Y), new P2(a.X, a.Y), new P2(b.X, b.Y), out _);
                if (d < bestD) { bestD = d; best = i; }
            }
            if (best >= 0)
            {
                bool soma = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
                if (!soma) { bool so = _sel.Count == 1 && _sel.Contains(best); _sel.Clear(); if (!so) _sel.Add(best); }
                else if (!_sel.Remove(best)) _sel.Add(best);
                RebuildEditor();
                Draw();
                return;
            }
            // an area: wet ↔ dry
            P2 m = M(s);
            ZonaBancada z = _zonas.FirstOrDefault(q => Poligono.Dentro(q.Pts, m));
            if (z == null) return;
            if (z.Molhada) _cfg.Molhadas.RemoveAll(q => Poligono.Dentro(z.Pts, q.P));
            else
            {
                P2 c = Poligono.Interior(z.Pts);
                _cfg.Molhadas.Add(new Pt(Math.Round(c.X, 1), Math.Round(c.Y, 1)));
            }
            Refresh();
            SetStatus(z.Molhada ? "Área marcada como seca." : "Área marcada como molhada" + (_cfg.RebaixoMm > 0 ? " (rebaixo de " + PecaUtil.Mm(_cfg.RebaixoMm) + " mm)." : "."), false);
        }

        private void Linha(Point a, Point b, Brush cor, double esp, DoubleCollection traco)
        {
            var ln = new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = cor, StrokeThickness = esp, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false };
            if (traco != null) ln.StrokeDashArray = traco;
            _canvas.Children.Add(ln);
        }

        private void Poligono2D(IEnumerable<Point> pontos, Brush fill, Brush stroke, double esp)
        {
            var pg = new Polygon { Points = new PointCollection(pontos), Fill = fill, Stroke = stroke, StrokeThickness = esp, IsHitTestVisible = false };
            _canvas.Children.Add(pg);
        }

        private void Ponto(Point p, Brush cor)
        {
            var e = new Ellipse { Width = 5, Height = 5, Fill = cor, IsHitTestVisible = false };
            Canvas.SetLeft(e, p.X - 2.5);
            Canvas.SetTop(e, p.Y - 2.5);
            _canvas.Children.Add(e);
        }

        private void Texto(string t, Point centro, Brush cor, double size, bool fundo, bool negrito = false)
        {
            var tb = new TextBlock { Text = t, Foreground = cor, FontSize = size, FontWeight = negrito ? FontWeights.SemiBold : FontWeights.Normal, IsHitTestVisible = false };
            if (fundo) { tb.Background = new SolidColorBrush(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF)); tb.Padding = new Thickness(2, 0, 2, 0); }
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(tb, centro.X - tb.DesiredSize.Width / 2);
            Canvas.SetTop(tb, centro.Y - tb.DesiredSize.Height / 2);
            _canvas.Children.Add(tb);
        }

        // =========================================================================================
        // controls (as in the other configurators)
        // =========================================================================================
        // the shared controls (FormKit), bound to this window
        private static FrameworkElement Field(string label, UIElement control, string hint) => FormKit.Field(label, control, hint, 124);
        private FrameworkElement MaterialInput(string value, Action<string> set) => FormKit.MaterialInput(_materiais, value, set);
        private FrameworkElement HexInput(string value, Action<string> set) => FormKit.HexInput(value, h => { set(h); Refresh(); }, MarkInvalid);
        private FrameworkElement Check(string label, bool value, Action<bool> set, string hint) => FormKit.Check(label, value, v => { set(v); Refresh(); }, hint);
        private void MarkInvalid(TextBox tb, bool invalid) => FormKit.MarkInvalid(_invalid, tb, invalid);
        private void SetStatus(string text, bool error) => FormKit.SetStatus(_status, text, error);

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
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(tb);
            var u = ToolChrome.Muted(unit, 11.5);
            u.VerticalAlignment = VerticalAlignment.Center;
            u.Margin = new Thickness(6, 0, 0, 0);
            row.Children.Add(u);
            return row;
        }

        /// <summary>Angle box (any value, degrees) with −90 / +90 buttons.</summary>
        private FrameworkElement Angulo(double value, Action<double> set)
        {
            var tb = new TextBox { Text = value.ToString("0.#", CultureInfo.CurrentCulture), Width = 70, HorizontalContentAlignment = HorizontalAlignment.Right };
            tb.TextChanged += (s, e) =>
            {
                bool ok = OptionsDialog.TryParse(tb.Text, out double v) && v >= -360 && v <= 360;
                MarkInvalid(tb, !ok);
                if (!ok) return;
                set(v);
                Refresh();
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(tb);
            var u = ToolChrome.Muted("°", 11.5);
            u.VerticalAlignment = VerticalAlignment.Center;
            u.Margin = new Thickness(6, 0, 8, 0);
            row.Children.Add(u);
            Action<double> girar = d =>
            {
                if (!OptionsDialog.TryParse(tb.Text, out double v)) v = 0;
                v += d;
                while (v > 180) v -= 360;
                while (v <= -180) v += 360;
                tb.Text = v.ToString("0.#", CultureInfo.CurrentCulture);
            };
            row.Children.Add(Small("−90°", "Gira 90° no sentido horário", () => girar(-90)));
            row.Children.Add(Small("+90°", "Gira 90° no sentido anti-horário", () => girar(90)));
            return row;
        }

        private void Preset(double altura, BordaLocal livre, BordaLocal parede, BordaFrontal borda, string nome)
        {
            var b = _cfg;
            bool piso = altura <= b.EspessuraMm + 0.5;
            b.AlturaMm = altura;
            b.Borda = borda;
            for (int i = 0; i < b.Arestas.Count; i++)
            {
                bool naParede = b.Arestas[i] == BordaLocal.Parede || b.Arestas[i] == BordaLocal.ParedeSemFrontao;
                b.Arestas[i] = naParede ? parede : livre;
            }
            if (borda == BordaFrontal.Reta)
            {
                b.Tento = false;
                b.Perfil = PerfilBorda.Reta;
            }
            if (piso)
            {
                b.Cubas?.Clear();
                b.Divisoes?.Clear();
                b.Molhadas?.Clear();
                b.Cooktop = false;
                b.Furos?.Clear();
            }
            if (string.IsNullOrWhiteSpace(b.Nome) || b.Nome.StartsWith("Bancada") || b.Nome.StartsWith("Soleira") || b.Nome.StartsWith("Prateleira"))
                b.Nome = nome + " no local";
            LimparMarcas();
            RebuildEditor();
            Refresh();
        }

        private Button Small(string text, string tip, Action click)
        {
            var b = ToolChrome.Secondary(text, (s, e) => click(), tip);
            b.Padding = new Thickness(9, 2, 9, 3);
            b.MinWidth = 28;
            b.Margin = new Thickness(0, 0, 6, 4);
            return b;
        }

        // =========================================================================================
        // state
        // =========================================================================================
        private bool ValidateCode()
        {
            if (_codeError == null) return true;
            string code = (_cfg.Codigo ?? "").Trim();
            string err = null;
            if (code.Length == 0) err = "Informe um código, ex.: BC01.";
            else if (!Regex.IsMatch(code, @"^[A-Za-z0-9][A-Za-z0-9 ._\-]{0,19}$")) err = "Use letras, números, espaço, ponto, hífen ou sublinhado.";
            else if (!_editando && _usados.Contains(code)) err = "Já existe bancada " + code + " neste projeto. Sugestão: " + ProjectPecas.NextCode(_usados, TipoPeca.Bancada) + ".";
            _codeError.Text = err ?? "";
            _codeError.Visibility = err == null ? Visibility.Collapsed : Visibility.Visible;
            return err == null;
        }

        private void Finish(AcaoBancadaLocal acao)
        {
            if (_invalid.Count > 0)
            {
                SetStatus("Corrija os campos em vermelho antes de continuar.", true);
                return;
            }
            if (acao == AcaoBancadaLocal.Gerar && !ValidateCode())
            {
                SetStatus("Confira o código.", true);
                return;
            }
            _pedido = new BancadaLocalPedido { Config = (BancadaConfig)_cfg.Copiar(), Acao = acao };
            try { _w.DialogResult = true; } catch { _w.Close(); }
        }
    }
}
