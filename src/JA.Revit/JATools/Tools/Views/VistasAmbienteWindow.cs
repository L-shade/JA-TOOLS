using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RevitDwgUnifier.Tools.Common;

namespace RevitDwgUnifier.Tools.Views
{
    /// <summary>A room in the list of the Vistas por Ambiente window.</summary>
    public sealed class AmbItem
    {
        public object Id;                 // ElementId (kept as object: the window does not touch the Revit API)
        public string Numero = "", Nome = "", Nivel = "";
        public int Existentes;            // views already named after this room
        public bool Selecionado;
        public string Titulo => (string.IsNullOrWhiteSpace(Numero) ? "" : Numero + " - ") + Nome;
    }

    /// <summary>What the user asked for.</summary>
    public sealed class VistasAmbPedido
    {
        public List<AmbItem> Ambientes = new List<AmbItem>();
        public bool Planta, Elevacoes = true, Iso;
        public int Escala = 50;
        public bool MostrarRecorte;
        public int Existentes;                                // 0 criar outras, 1 só as que faltam, 2 substituir

        // planta
        public int PlantaModelo;
        public double PlantaMargem = 0.5;
        public bool PlantaEtiqueta = true;
        public string PlantaNome = "PLANTA - {ambiente}";

        // elevações
        public bool[] Lados = { true, true, true, true };     // A (cima da planta), B (direita), C (baixo), D (esquerda)
        public int Tipo, Modelo;
        public double Margem = 0.15;
        public bool Alinhar = true;
        public string Nome = "{ambiente} - ELEVAÇÃO {letra}";

        // isométrico
        public int IsoModelo, IsoDirecao;
        public double IsoCorte = 2.4, IsoMargem = 0.3;
        public string IsoNome = "ISOMÉTRICO - {ambiente}";

        // one sheet per room with its new views
        public bool Prancha;
        public int Folha;                                     // title block (index in the list given to the window)
        public string NumeroPrancha = "A101";
        public string NomePrancha = "{ambiente}";
        public string Margens = "25, 10, 10, 45";

        public int VistasPorAmbiente => (Planta ? 1 : 0) + (Elevacoes ? Lados.Count(x => x) : 0) + (Iso ? 1 : 0);
        public int Quantidade => Ambientes.Count * VistasPorAmbiente;
    }

    /// <summary>
    /// Vistas por Ambiente (1.17 — antes três botões: Vistas, Elevações e Isométrico por Ambiente).
    /// Rooms at the left (search, level, "só sem vistas", Shift+click range); at the right what to
    /// make (planta / elevações / isométrico, any mix) and a card of options for each one that is on.
    /// Same look as Gerar Detalhes and the Varinha panel.
    /// </summary>
    public sealed class VistasAmbienteWindow : Window
    {
        private const string P = "VistasAmbiente.";
        public static readonly string[] Letras = { "A", "B", "C", "D" };
        public static readonly string[] Direcoes = { "Sudeste", "Sudoeste", "Nordeste", "Noroeste" };
        private static readonly int[] Escalas = { 20, 25, 50, 75, 100 };

        private readonly List<AmbItem> _itens;
        private readonly List<string> _tiposElev, _modelosElev, _modelosPlanta, _modelos3D;
        public VistasAmbPedido Pedido { get; private set; }

        private readonly TextBox _busca = new TextBox();
        private readonly ComboBox _nivel = new ComboBox { MinWidth = 130 };
        private readonly CheckBox _soSem = new CheckBox { Content = "Só sem vistas", FontSize = 11.5 };
        private readonly StackPanel _lista = new StackPanel();
        private TextBlock _badge;

        // what to make
        private readonly CheckBox _cPlanta = new CheckBox { Content = "Planta" };
        private readonly CheckBox _cElev = new CheckBox { Content = "Elevações" };
        private readonly CheckBox _cIso = new CheckBox { Content = "Isométrico" };
        private readonly ComboBox _escala = new ComboBox { IsEditable = true, Width = 90 };
        private Border _cardPlanta, _cardElev, _cardIso;

        // planta
        private readonly ComboBox _pModelo = new ComboBox();
        private readonly TextBox _pMargem = Num();
        private readonly CheckBox _pEtiqueta = new CheckBox { Content = "Etiquetar o ambiente", FontSize = 11.5 };
        private readonly TextBox _pNome = new TextBox();

        // elevações
        private readonly CheckBox[] _lados = new CheckBox[4];
        private readonly ComboBox _eTipo = new ComboBox(), _eModelo = new ComboBox();
        private readonly TextBox _eMargem = Num();
        private readonly CheckBox _alinhar = new CheckBox { Content = "Paralelas às paredes", FontSize = 11.5, ToolTip = "Cada parede ganha uma elevação de frente, em qualquer ângulo; a planta e o isométrico giram junto com o ambiente." };
        private readonly TextBox _eNome = new TextBox();

        // isométrico
        private readonly ComboBox _iModelo = new ComboBox();
        private readonly RadioButton[] _iDir = new RadioButton[4];
        private readonly TextBox _iCorte = Num(), _iMargem = Num();
        private readonly TextBox _iNome = new TextBox();

        // common
        private readonly CheckBox _recorte = new CheckBox { Content = "Mostrar o recorte", FontSize = 11.5 };
        private readonly CheckBox _prancha = new CheckBox { Content = "Montar uma prancha por ambiente", FontSize = 11.5 };
        private readonly ComboBox _folha = new ComboBox();
        private readonly TextBox _numPrancha = new TextBox(), _nomePrancha = new TextBox(), _margens = new TextBox();
        private readonly List<string> _folhas;
        private UIElement _cardPrancha;
        private readonly RadioButton[] _exist = new RadioButton[3];
        private readonly TextBlock _exemplo = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        private readonly TextBlock _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        private Button _criar;
        private int _ultimo = -1;
        private List<AmbItem> _visiveis = new List<AmbItem>();

        public VistasAmbienteWindow(List<AmbItem> itens, List<string> tiposElev, List<string> modelosElev, List<string> modelosPlanta, List<string> modelos3D,
                                    List<string> folhas = null)
        {
            _folhas = folhas ?? new List<string>();
            _itens = itens;
            _tiposElev = tiposElev;
            _modelosElev = modelosElev;
            _modelosPlanta = modelosPlanta;
            _modelos3D = modelos3D;
            Width = 1000; Height = 720; MinWidth = 840; MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 340 });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star), MinWidth = 420 });
            body.Children.Add(Ambientes());
            var right = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = Opcoes() };
            Grid.SetColumn(right, 2);
            body.Children.Add(right);

            ToolChrome.DressCompact(this, "Vistas por Ambiente", body, Rodape(),
                "Planta, elevações internas e isométrico de cada ambiente, recortados nele, numa passada só.");
            Carregar();
            Filtrar();
            Atualizar();
            Loaded += (s, e) => _busca.Focus();
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { _busca.Focus(); e.Handled = true; }
                else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control && _criar.IsEnabled) { Confirmar(); e.Handled = true; }
            };
        }

        // ================================================================== rooms

        private UIElement Ambientes()
        {
            var dock = new DockPanel();

            _busca.TextChanged += (s, e) => Filtrar();
            _busca.ToolTip = "Número ou nome do ambiente (Ctrl+F)";
            var busca = ToolChrome.SearchBox(_busca, "Buscar ambiente…");
            DockPanel.SetDock(busca, Dock.Top);
            dock.Children.Add(busca);

            var filtros = new DockPanel { Margin = new Thickness(0, 8, 0, 8) };
            _soSem.SetResourceReference(StyleProperty, "Ja.Toggle");
            _soSem.Margin = new Thickness(0, 0, 8, 0);
            _soSem.VerticalAlignment = VerticalAlignment.Center;
            _soSem.ToolTip = "Esconde os ambientes que já têm vistas com o nome deles.";
            _soSem.Checked += (s, e) => Filtrar();
            _soSem.Unchecked += (s, e) => Filtrar();
            DockPanel.SetDock(_soSem, Dock.Left);
            filtros.Children.Add(_soSem);
            _nivel.Items.Add("Todos os níveis");
            foreach (string n in _itens.Select(i => i.Nivel).Distinct()) _nivel.Items.Add(n);
            _nivel.SelectedIndex = 0;
            _nivel.SelectionChanged += (s, e) => Filtrar();
            filtros.Children.Add(_nivel);
            DockPanel.SetDock(filtros, Dock.Top);
            dock.Children.Add(filtros);

            var bottom = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var hint = ToolChrome.Muted("Shift+clique: intervalo", 11);
            hint.VerticalAlignment = VerticalAlignment.Center;
            hint.ToolTip = "Shift+clique marca/desmarca um intervalo inteiro. Ambientes selecionados no Revit já vêm marcados.";
            DockPanel.SetDock(hint, Dock.Right);
            bottom.Children.Add(hint);
            var acts = new StackPanel { Orientation = Orientation.Horizontal };
            var all = ToolChrome.Ghost("Marcar visíveis", "Marca os ambientes que aparecem na lista", (s, e) => { foreach (var i in _visiveis) i.Selecionado = true; Desenhar(); });
            all.Margin = new Thickness(0, 0, 4, 0);
            acts.Children.Add(all);
            acts.Children.Add(ToolChrome.Ghost("Limpar", "Desmarca tudo", (s, e) => { foreach (var i in _itens) i.Selecionado = false; Desenhar(); }));
            bottom.Children.Add(acts);
            DockPanel.SetDock(bottom, Dock.Bottom);
            dock.Children.Add(bottom);

            var frame = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = new ScrollViewer { Content = _lista, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
            frame.SetResourceReference(Border.BorderBrushProperty, "Ja.Line");
            dock.Children.Add(frame);
            return ToolChrome.Card(dock, "Ambientes", ToolChrome.Badge(out _badge));
        }

        private void Filtrar()
        {
            string q = TextUtil.Fold(_busca.Text).Trim();
            string nivel = _nivel.SelectedIndex > 0 ? _nivel.SelectedItem as string : null;
            _visiveis = _itens.Where(i => (nivel == null || i.Nivel == nivel) &&
                                          (_soSem.IsChecked != true || i.Existentes == 0) &&
                                          (q.Length == 0 || TextUtil.Fold(i.Titulo).Contains(q))).ToList();
            _ultimo = -1;
            Desenhar();
        }

        private void Desenhar()
        {
            _lista.Children.Clear();
            string grupo = null;
            for (int k = 0; k < _visiveis.Count; k++)
            {
                var it = _visiveis[k];
                if (it.Nivel != grupo)
                {
                    grupo = it.Nivel;
                    var h = new TextBlock { Text = grupo, FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 10, 0, 3) };
                    h.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Muted");
                    _lista.Children.Add(h);
                }
                int idx = k;
                var row = new DockPanel();
                if (it.Existentes > 0)
                {
                    var ex = ToolChrome.Muted("já tem " + it.Existentes, 11);
                    ex.VerticalAlignment = VerticalAlignment.Center;
                    ex.ToolTip = "Vistas já existentes com o nome deste ambiente.";
                    DockPanel.SetDock(ex, Dock.Right);
                    row.Children.Add(ex);
                }
                var cb = new CheckBox { IsChecked = it.Selecionado, Margin = new Thickness(2, 2, 0, 2), VerticalContentAlignment = VerticalAlignment.Center };
                var txt = new TextBlock { Text = it.Titulo, TextTrimming = TextTrimming.CharacterEllipsis };
                txt.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Text");
                cb.Content = txt;
                cb.Click += (s, e) =>
                {
                    bool v = cb.IsChecked == true;
                    if (Keyboard.Modifiers == ModifierKeys.Shift && _ultimo >= 0 && _ultimo < _visiveis.Count)
                    {
                        int a = Math.Min(_ultimo, idx), b = Math.Max(_ultimo, idx);
                        for (int j = a; j <= b; j++) _visiveis[j].Selecionado = v;
                        _ultimo = idx;
                        Desenhar();
                        return;
                    }
                    it.Selecionado = v;
                    _ultimo = idx;
                    Atualizar();
                };
                row.Children.Add(cb);
                _lista.Children.Add(row);
            }
            if (_visiveis.Count == 0) _lista.Children.Add(ToolChrome.Muted("Nenhum ambiente com esse filtro.", 11.5));
            Atualizar();
        }

        // ================================================================== options

        private UIElement Opcoes()
        {
            var sp = new StackPanel();

            // ---- what to make + scale
            var criar = new StackPanel();
            var kinds = new WrapPanel();
            foreach (var (cb, tip) in new[] { (_cPlanta, "Planta recortada e enquadrada no ambiente."), (_cElev, "Elevações internas, uma por lado escolhido."), (_cIso, "3D isométrico aberto por cima.") })
            {
                cb.SetResourceReference(StyleProperty, "Ja.Toggle");
                cb.FontWeight = FontWeights.SemiBold;
                cb.ToolTip = tip;
                cb.Checked += (s, e) => Atualizar();
                cb.Unchecked += (s, e) => Atualizar();
                kinds.Children.Add(cb);
            }
            criar.Children.Add(kinds);
            foreach (int e in Escalas) _escala.Items.Add("1:" + e);
            _escala.ToolTip = "Escolha ou digite (ex.: 1:30). Vale para planta e elevações.";
            criar.Children.Add(Linha("Escala", _escala));
            sp.Children.Add(ToolChrome.Card(criar, "Criar"));

            // ---- planta
            var pl = new StackPanel();
            foreach (string m in _modelosPlanta) _pModelo.Items.Add(m);
            pl.Children.Add(ToolChrome.Labeled("Modelo de vista", _pModelo));
            var plRow = new WrapPanel();
            plRow.Children.Add(Linha("Margem", _pMargem, "m"));
            _pEtiqueta.SetResourceReference(StyleProperty, "Ja.Toggle");
            _pEtiqueta.Margin = new Thickness(14, 0, 0, 8);
            plRow.Children.Add(_pEtiqueta);
            pl.Children.Add(plRow);
            pl.Children.Add(NomeCampo(_pNome, false));
            _cardPlanta = ToolChrome.Card(pl, "Planta");
            sp.Children.Add(_cardPlanta);

            // ---- elevações: sides drawn around a small room
            var el = new StackPanel();
            var lados = new Grid { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6) };
            for (int i = 0; i < 3; i++) { lados.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); lados.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); }
            var sala = new Border { Width = 104, Height = 64, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(4), Margin = new Thickness(6) };
            sala.SetResourceReference(Border.BorderBrushProperty, "Ja.InputLine");
            sala.SetResourceReference(Border.BackgroundProperty, "Ja.Field");
            var marca = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            marca.SetResourceReference(Border.BackgroundProperty, "Ja.Accent");
            sala.Child = marca;
            Grid.SetRow(sala, 1); Grid.SetColumn(sala, 1);
            lados.Children.Add(sala);
            (int r, int c, string tip)[] pos = { (0, 1, "A — parede de cima da planta"), (1, 2, "B — parede da direita"), (2, 1, "C — parede de baixo"), (1, 0, "D — parede da esquerda") };
            for (int i = 0; i < 4; i++)
            {
                var cb = new CheckBox { Content = Letras[i], ToolTip = pos[i].tip, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
                cb.SetResourceReference(StyleProperty, "Ja.Toggle");
                cb.Margin = new Thickness(0);
                cb.Checked += (s, e) => Atualizar();
                cb.Unchecked += (s, e) => Atualizar();
                Grid.SetRow(cb, pos[i].r); Grid.SetColumn(cb, pos[i].c);
                lados.Children.Add(cb);
                _lados[i] = cb;
            }
            var ladosBox = new DockPanel();
            DockPanel.SetDock(lados, Dock.Left);
            ladosBox.Children.Add(lados);
            var lnote = ToolChrome.Muted("Letras no sentido horário a partir da parede de cima. Com “Paralelas às paredes”, cada parede ganha sua elevação de frente, em qualquer ângulo (parede inclinada a mais vira E, F…), recortada na largura da parede; a planta e o isométrico giram com o ambiente.", 11);
            lnote.Margin = new Thickness(14, 0, 0, 0);
            lnote.VerticalAlignment = VerticalAlignment.Center;
            ladosBox.Children.Add(lnote);
            el.Children.Add(ladosBox);
            foreach (string t in _tiposElev) _eTipo.Items.Add(t);
            foreach (string m in _modelosElev) _eModelo.Items.Add(m);
            el.Children.Add(Duas(ToolChrome.Labeled("Tipo de elevação", _eTipo), ToolChrome.Labeled("Modelo de vista", _eModelo)));
            var elRow = new WrapPanel();
            elRow.Children.Add(Linha("Margem", _eMargem, "m"));
            _alinhar.SetResourceReference(StyleProperty, "Ja.Toggle");
            _alinhar.Margin = new Thickness(14, 0, 0, 8);
            _alinhar.ToolTip = "Gira o marcador para as elevações ficarem de frente para as paredes, mesmo em ambientes inclinados.";
            elRow.Children.Add(_alinhar);
            el.Children.Add(elRow);
            el.Children.Add(NomeCampo(_eNome, true));
            _cardElev = ToolChrome.Card(el, "Elevações");
            sp.Children.Add(_cardElev);

            // ---- isométrico
            var iso = new StackPanel();
            foreach (string m in _modelos3D) _iModelo.Items.Add(m);
            iso.Children.Add(ToolChrome.Labeled("Modelo de vista", _iModelo));
            var dirs = new WrapPanel();
            for (int i = 0; i < 4; i++)
            {
                var rb = new RadioButton { Content = Direcoes[i], GroupName = "vamb-iso" };
                rb.SetResourceReference(StyleProperty, "Ja.Segment");
                dirs.Children.Add(rb);
                _iDir[i] = rb;
            }
            iso.Children.Add(ToolChrome.Labeled("Observador a", ToolChrome.Track(dirs)));
            var isoRow = new WrapPanel();
            isoRow.Children.Add(Linha("Cortar a", _iCorte, "m do piso", "0 = altura do ambiente. A caixa abre o ambiente pelo alto para mostrar o interior."));
            var mg = Linha("Margem", _iMargem, "m");
            mg.Margin = new Thickness(14, 0, 0, 8);
            isoRow.Children.Add(mg);
            iso.Children.Add(isoRow);
            iso.Children.Add(NomeCampo(_iNome, false));
            _cardIso = ToolChrome.Card(iso, "Isométrico");
            sp.Children.Add(_cardIso);

            // ---- common: existing views, crop, example
            var ex = new StackPanel();
            var exist = new WrapPanel();
            string[] ops = { "Criar outras", "Só as que faltam", "Substituir" };
            string[] tips = { "Cria novas vistas (o nome ganha um número).", "Cria só as vistas que ainda não existem com estes nomes; as existentes ficam como estão.", "Apaga as vistas com estes nomes e cria de novo (o marcador de elevação vazio também sai)." };
            for (int i = 0; i < 3; i++)
            {
                var rb = new RadioButton { Content = ops[i], GroupName = "vamb-exist", ToolTip = tips[i] };
                rb.SetResourceReference(StyleProperty, "Ja.Segment");
                exist.Children.Add(rb);
                _exist[i] = rb;
            }
            ex.Children.Add(ToolChrome.Labeled("Se o ambiente já tiver estas vistas", ToolChrome.Track(exist)));
            _recorte.SetResourceReference(StyleProperty, "Ja.Toggle");
            _recorte.ToolTip = "Deixa o contorno do recorte visível na planta e nas elevações.";
            ex.Children.Add(_recorte);
            _exemplo.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Accent");
            ex.Children.Add(_exemplo);
            sp.Children.Add(ToolChrome.Card(ex, "Geral"));

            // ---- prancha
            var pr = new StackPanel();
            _prancha.SetResourceReference(StyleProperty, "Ja.Toggle");
            _prancha.ToolTip = "As vistas novas de cada ambiente vão para uma prancha nova, arrumadas a partir do canto superior esquerdo (como no Detalhar Ambientes, sem anotar).";
            _prancha.Checked += (s, e) => Atualizar();
            _prancha.Unchecked += (s, e) => Atualizar();
            pr.Children.Add(_prancha);
            var prOpc = new StackPanel();
            foreach (string f in _folhas) _folha.Items.Add(f);
            prOpc.Children.Add(ToolChrome.Labeled("Folha (carimbo)", _folha));
            var prRow = new WrapPanel();
            prRow.Children.Add(Linha("Primeiro número", _numPrancha, null, "As pranchas seguintes continuam a numeração (A101, A102…); números já usados são pulados."));
            pr.Children.Add(prOpc);
            pr.Children.Add(prRow);
            pr.Children.Add(ToolChrome.Labeled("Nome da prancha ({ambiente}, {numero}, {nome}, {nivel})", _nomePrancha));
            pr.Children.Add(ToolChrome.Labeled("Margens esq., sup., dir., inf. (mm)", _margens));
            _cardPrancha = ToolChrome.Card(pr, "Prancha");
            if (_folhas.Count == 0) { _prancha.IsEnabled = false; _prancha.ToolTip = "O projeto não tem família de folha (carimbo) carregada."; }
            sp.Children.Add(_cardPrancha);
            return sp;
        }

        /// <summary>Name pattern box with token chips.</summary>
        private UIElement NomeCampo(TextBox box, bool letra)
        {
            var nm = new StackPanel();
            box.TextChanged += (s, e) => Atualizar();
            nm.Children.Add(ToolChrome.Labeled("Nome", box));
            var chips = new WrapPanel { Margin = new Thickness(0, -4, 0, 0) };
            var tokens = new List<string> { "{ambiente}", "{numero}", "{nome}", "{nivel}" };
            if (letra) tokens.Insert(3, "{letra}");
            foreach (string tk in tokens)
            {
                var chip = ToolChrome.Ghost(tk, "Inserir " + tk);
                chip.FontSize = 11; chip.Padding = new Thickness(7, 1, 7, 2); chip.Margin = new Thickness(0, 0, 4, 4); chip.Focusable = false;
                chip.SetResourceReference(Control.BackgroundProperty, "Ja.Field");
                string token = tk;
                chip.Click += (s, e) => { int at = box.CaretIndex; box.Text = box.Text.Insert(at, token); box.CaretIndex = at + token.Length; box.Focus(); };
                chips.Children.Add(chip);
            }
            nm.Children.Add(chips);
            return nm;
        }

        private UIElement Rodape()
        {
            var bar = new DockPanel();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            var cancel = ToolChrome.Ghost("Cancelar");
            cancel.IsCancel = true; cancel.MinWidth = 88; cancel.Margin = new Thickness(0, 0, 8, 0);
            buttons.Children.Add(cancel);
            _criar = ToolChrome.Primary("Criar vistas", (s, e) => Confirmar());
            _criar.MinWidth = 180;
            _criar.IsDefault = true;
            _criar.ToolTip = "Enter";
            buttons.Children.Add(_criar);
            DockPanel.SetDock(buttons, Dock.Right);
            bar.Children.Add(buttons);
            _status.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Text");
            bar.Children.Add(_status);
            return bar;
        }

        // ================================================================== state

        private void Atualizar()
        {
            if (_criar == null || _cardPlanta == null) return;
            bool p = _cPlanta.IsChecked == true, e = _cElev.IsChecked == true, i = _cIso.IsChecked == true;
            _cardPlanta.Visibility = p ? Visibility.Visible : Visibility.Collapsed;
            _cardElev.Visibility = e ? Visibility.Visible : Visibility.Collapsed;
            _cardIso.Visibility = i ? Visibility.Visible : Visibility.Collapsed;
            bool pr = _prancha.IsChecked == true;
            _folha.IsEnabled = _numPrancha.IsEnabled = _nomePrancha.IsEnabled = _margens.IsEnabled = pr;

            int amb = _itens.Count(x => x.Selecionado), lados = _lados.Count(c => c?.IsChecked == true);
            int porAmb = (p ? 1 : 0) + (e ? lados : 0) + (i ? 1 : 0);
            int n = amb * porAmb;
            _badge.Text = $"{amb} de {_itens.Count}";
            _criar.Content = n == 0 ? "Criar vistas" : n == 1 ? "Criar 1 vista" : $"Criar {n} vistas";
            _criar.IsEnabled = n > 0;
            _status.Text = amb == 0 ? "Marque os ambientes à esquerda."
                         : !p && !e && !i ? "Escolha o que criar: planta, elevações ou isométrico."
                         : e && lados == 0 && !p && !i ? "Escolha ao menos um lado."
                         : $"{amb} ambiente(s) × {porAmb} vista(s)" + (pr ? $" + {amb} prancha(s)" : "");

            var ex = _itens.FirstOrDefault(x => x.Selecionado) ?? _itens.FirstOrDefault();
            if (ex == null) { _exemplo.Text = ""; return; }
            var nomes = new List<string>();
            if (p) nomes.Add(Nomear(_pNome.Text, "PLANTA - {ambiente}", ex, ""));
            if (e) nomes.Add(Nomear(_eNome.Text, "{ambiente} - ELEVAÇÃO {letra}", ex, Letras[Math.Max(0, Array.FindIndex(_lados, c => c?.IsChecked == true))]));
            if (i) nomes.Add(Nomear(_iNome.Text, "ISOMÉTRICO - {ambiente}", ex, ""));
            _exemplo.Text = nomes.Count == 0 ? "" : "Ex.: " + string.Join("  ·  ", nomes);
        }

        public static string Nomear(string padrao, string padrao0, AmbItem it, string letra) =>
            (string.IsNullOrWhiteSpace(padrao) ? padrao0 : padrao)
                .Replace("{ambiente}", it.Titulo).Replace("{numero}", it.Numero).Replace("{nome}", it.Nome)
                .Replace("{letra}", letra).Replace("{nivel}", it.Nivel).Trim();

        private void Carregar()
        {
            var d = new VistasAmbPedido();
            _cPlanta.IsChecked = Bool("planta", d.Planta);
            _cElev.IsChecked = Bool("elevacoes", d.Elevacoes);
            _cIso.IsChecked = Bool("iso", d.Iso);
            _escala.Text = "1:" + Int("escala", d.Escala);
            _recorte.IsChecked = Bool("recorte", d.MostrarRecorte);
            _prancha.IsChecked = _folhas.Count > 0 && Bool("prancha", d.Prancha);
            _folha.SelectedIndex = Clamp(Int("folha", 0), _folhas.Count);
            _numPrancha.Text = ToolPrefs.Get(P + "numPrancha") ?? d.NumeroPrancha;
            _nomePrancha.Text = ToolPrefs.Get(P + "nomePrancha") ?? d.NomePrancha;
            _margens.Text = ToolPrefs.Get(P + "margens") ?? d.Margens;
            _exist[Clamp(Int("existentes", 1), 3)].IsChecked = true;

            _pModelo.SelectedIndex = Clamp(Int("plantaModelo", 0), _modelosPlanta.Count);
            _pMargem.Text = Txt(Dbl("plantaMargem", d.PlantaMargem));
            _pEtiqueta.IsChecked = Bool("plantaEtiqueta", d.PlantaEtiqueta);
            _pNome.Text = ToolPrefs.Get(P + "plantaNome") ?? d.PlantaNome;

            string lados = ToolPrefs.Get(P + "lados") ?? "1111";
            for (int i = 0; i < 4; i++) _lados[i].IsChecked = lados.Length > i ? lados[i] == '1' : true;
            _eTipo.SelectedIndex = Clamp(Int("tipo", 0), _tiposElev.Count);
            _eModelo.SelectedIndex = Clamp(Int("modelo", 0), _modelosElev.Count);
            _eMargem.Text = Txt(Dbl("margem", d.Margem));
            _alinhar.IsChecked = Bool("alinhar", d.Alinhar);
            _eNome.Text = ToolPrefs.Get(P + "nome") ?? d.Nome;

            _iModelo.SelectedIndex = Clamp(Int("isoModelo", 0), _modelos3D.Count);
            _iDir[Clamp(Int("isoDirecao", 0), 4)].IsChecked = true;
            _iCorte.Text = Txt(Dbl("isoCorte", d.IsoCorte));
            _iMargem.Text = Txt(Dbl("isoMargem", d.IsoMargem));
            _iNome.Text = ToolPrefs.Get(P + "isoNome") ?? d.IsoNome;
        }

        private void Confirmar()
        {
            if (!int.TryParse((_escala.Text ?? "").Replace("1:", "").Trim(), out int esc) || esc < 1)
            { Aviso("Escala inválida (ex.: 1:50)."); return; }
            if (!Ler(_pMargem, out double pm) || !Ler(_eMargem, out double em) || !Ler(_iCorte, out double ic) || !Ler(_iMargem, out double im))
            { Aviso("Medida inválida (ex.: 0,15)."); return; }

            Pedido = new VistasAmbPedido
            {
                Ambientes = _itens.Where(i => i.Selecionado).ToList(),
                Planta = _cPlanta.IsChecked == true,
                Elevacoes = _cElev.IsChecked == true,
                Iso = _cIso.IsChecked == true,
                Escala = esc,
                MostrarRecorte = _recorte.IsChecked == true,
                Existentes = Math.Max(0, Array.FindIndex(_exist, r => r.IsChecked == true)),
                PlantaModelo = Math.Max(0, _pModelo.SelectedIndex),
                PlantaMargem = pm,
                PlantaEtiqueta = _pEtiqueta.IsChecked == true,
                PlantaNome = Padrao(_pNome, "PLANTA - {ambiente}"),
                Lados = _lados.Select(c => c.IsChecked == true).ToArray(),
                Tipo = Math.Max(0, _eTipo.SelectedIndex),
                Modelo = Math.Max(0, _eModelo.SelectedIndex),
                Margem = em,
                Alinhar = _alinhar.IsChecked == true,
                Nome = Padrao(_eNome, "{ambiente} - ELEVAÇÃO {letra}"),
                IsoModelo = Math.Max(0, _iModelo.SelectedIndex),
                IsoDirecao = Math.Max(0, Array.FindIndex(_iDir, r => r.IsChecked == true)),
                IsoCorte = ic,
                IsoMargem = im,
                IsoNome = Padrao(_iNome, "ISOMÉTRICO - {ambiente}"),
                Prancha = _prancha.IsChecked == true && _folhas.Count > 0,
                Folha = Math.Max(0, _folha.SelectedIndex),
                NumeroPrancha = string.IsNullOrWhiteSpace(_numPrancha.Text) ? "A101" : _numPrancha.Text.Trim(),
                NomePrancha = Padrao(_nomePrancha, "{ambiente}"),
                Margens = string.IsNullOrWhiteSpace(_margens.Text) ? "25, 10, 10, 45" : _margens.Text.Trim()
            };
            var q = Pedido;
            Set("planta", q.Planta); Set("elevacoes", q.Elevacoes); Set("iso", q.Iso);
            ToolPrefs.Set(P + "escala", esc.ToString());
            Set("recorte", q.MostrarRecorte);
            ToolPrefs.Set(P + "existentes", q.Existentes.ToString());
            ToolPrefs.Set(P + "plantaModelo", q.PlantaModelo.ToString());
            ToolPrefs.Set(P + "plantaMargem", q.PlantaMargem.ToString(CultureInfo.InvariantCulture));
            Set("plantaEtiqueta", q.PlantaEtiqueta);
            ToolPrefs.Set(P + "plantaNome", q.PlantaNome);
            ToolPrefs.Set(P + "lados", string.Concat(q.Lados.Select(b => b ? '1' : '0')));
            ToolPrefs.Set(P + "tipo", q.Tipo.ToString());
            ToolPrefs.Set(P + "modelo", q.Modelo.ToString());
            ToolPrefs.Set(P + "margem", q.Margem.ToString(CultureInfo.InvariantCulture));
            Set("alinhar", q.Alinhar);
            ToolPrefs.Set(P + "nome", q.Nome);
            ToolPrefs.Set(P + "isoModelo", q.IsoModelo.ToString());
            ToolPrefs.Set(P + "isoDirecao", q.IsoDirecao.ToString());
            ToolPrefs.Set(P + "isoCorte", q.IsoCorte.ToString(CultureInfo.InvariantCulture));
            ToolPrefs.Set(P + "isoMargem", q.IsoMargem.ToString(CultureInfo.InvariantCulture));
            ToolPrefs.Set(P + "isoNome", q.IsoNome);
            Set("prancha", q.Prancha);
            ToolPrefs.Set(P + "folha", q.Folha.ToString());
            ToolPrefs.Set(P + "numPrancha", q.NumeroPrancha);
            ToolPrefs.Set(P + "nomePrancha", q.NomePrancha);
            ToolPrefs.Set(P + "margens", q.Margens);
            ToolPrefs.Save();
            DialogResult = true;
        }

        // ================================================================== helpers

        private void Aviso(string text) => MessageBox.Show(this, text, Title, MessageBoxButton.OK, MessageBoxImage.Information);

        private static TextBox Num() => new TextBox { Width = 64, HorizontalContentAlignment = HorizontalAlignment.Right };

        private static bool Ler(TextBox tb, out double v)
        {
            string t = (tb.Text ?? "").Trim();
            v = 0;
            if (t.Length == 0) return true;
            if (!OptionsDialog.TryParse(t, out v)) return false;
            v = Math.Max(0, v);
            return true;
        }

        private static string Padrao(TextBox tb, string d) => string.IsNullOrWhiteSpace(tb.Text) ? d : tb.Text.Trim();
        private static string Txt(double v) => v.ToString("0.##", CultureInfo.GetCultureInfo("pt-BR"));
        private static bool Bool(string k, bool d) { string v = ToolPrefs.Get(P + k); return v == null ? d : v == "1"; }
        private static void Set(string k, bool v) => ToolPrefs.Set(P + k, v ? "1" : "0");
        private static int Int(string k, int d) => int.TryParse(ToolPrefs.Get(P + k), out int v) ? v : d;
        private static double Dbl(string k, double d) => double.TryParse(ToolPrefs.Get(P + k), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : d;
        private static int Clamp(int v, int count) => count <= 0 ? -1 : Math.Max(0, Math.Min(count - 1, v));

        /// <summary>"Margem [0,15] m" on one line.</summary>
        private static FrameworkElement Linha(string label, Control box, string unit = null, string tip = null)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            var l = ToolChrome.Muted(label, 11.5);
            l.VerticalAlignment = VerticalAlignment.Center;
            l.Margin = new Thickness(0, 0, 8, 0);
            row.Children.Add(l);
            row.Children.Add(box);
            if (unit != null)
            {
                var u = ToolChrome.Muted(unit, 11.5);
                u.VerticalAlignment = VerticalAlignment.Center;
                u.Margin = new Thickness(6, 0, 0, 0);
                row.Children.Add(u);
            }
            if (tip != null) row.ToolTip = tip;
            return row;
        }

        private static UIElement Duas(UIElement a, UIElement b)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(a);
            Grid.SetColumn(b, 2);
            g.Children.Add(b);
            return g;
        }
    }
}
