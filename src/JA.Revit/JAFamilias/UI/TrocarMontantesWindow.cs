using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace JAEsquadrias.UI
{
    public sealed class TipoMontanteItem
    {
        public string Familia = "", Nome = "";
        public bool Ja;          // made by JA Famílias
        public bool Canto;
        public int Usos;
        public object Tipo;      // MullionType
        public string Titulo => Nome + (Ja ? "" : "  (" + Familia + ")");
    }

    public sealed class TrocaPedido
    {
        public TipoMontanteItem Tipo;
        /// <summary>0 = só os selecionados, 1 = mesmo tipo nas mesmas paredes, 2 = mesmo tipo no projeto.</summary>
        public int Escopo;
        /// <summary>0 = todos, 1 = verticais (montantes), 2 = horizontais (travessas).</summary>
        public int Direcao;
        public bool NovoPerfil;
    }

    /// <summary>
    /// Trocar Montantes: the mullions picked in plan, section, elevation or 3D (or every mullion
    /// of the curtain walls picked) get another type. The list of types (JA profiles first) with
    /// search, the scope (only these / same type on the same walls / same type in the project)
    /// and the direction (all / verticals / horizontals). "Novo perfil…" opens the configurator and
    /// applies the new profile to the same mullions.
    /// </summary>
    public sealed class TrocarMontantesWindow : Window
    {
        public static TrocaPedido Abrir(IntPtr owner, List<TipoMontanteItem> tipos, int selecionados, int verticais, string atuais)
        {
            var w = new TrocarMontantesWindow(tipos, selecionados, verticais, atuais);
            if (owner != IntPtr.Zero) new WindowInteropHelper(w).Owner = owner;
            return w.ShowDialog() == true ? w._pedido : null;
        }

        private readonly List<TipoMontanteItem> _tipos;
        private readonly TextBox _busca = new TextBox();
        private readonly StackPanel _lista = new StackPanel();
        private readonly RadioButton[] _escopo = new RadioButton[3];
        private readonly RadioButton[] _dir = new RadioButton[3];
        private readonly TextBlock _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        private TipoMontanteItem _escolhido;
        private Button _ok;
        private TrocaPedido _pedido;
        private const string P = "TrocarMontantes.";

        private TrocarMontantesWindow(List<TipoMontanteItem> tipos, int sel, int vert, string atuais)
        {
            _tipos = tipos;
            Width = 760; Height = 640; MinWidth = 640; MinHeight = 480;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 260 });

            // ---- types
            var dock = new DockPanel();
            _busca.TextChanged += (s, e) => Desenhar();
            var busca = ToolChrome.SearchBox(_busca, "Buscar tipo…");
            busca.Margin = new Thickness(0, 0, 0, 8);
            DockPanel.SetDock(busca, Dock.Top);
            dock.Children.Add(busca);
            var novo = ToolChrome.Secondary("Novo perfil…", (s, e) => { _pedido = Pedido(true); DialogResult = true; },
                "Abre o configurador; o perfil criado já vai para estes montantes.");
            novo.Margin = new Thickness(0, 8, 0, 0);
            novo.HorizontalAlignment = HorizontalAlignment.Left;
            DockPanel.SetDock(novo, Dock.Bottom);
            dock.Children.Add(novo);
            var frame = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = new ScrollViewer { Content = _lista, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
            frame.SetResourceReference(Border.BorderBrushProperty, "Ja.Line");
            dock.Children.Add(frame);
            body.Children.Add(ToolChrome.Card(dock, "Novo tipo"));

            // ---- options
            var right = new StackPanel();
            var info = ToolChrome.Muted($"{sel} montante(s) escolhido(s): {vert} vertical(is), {sel - vert} horizontal(is).\nTipos atuais: {atuais}", 11.5);
            right.Children.Add(ToolChrome.Card(info, "Seleção"));

            var esc = new StackPanel();
            string[] en = { "Só os escolhidos", "Mesmo tipo nas mesmas paredes", "Mesmo tipo no projeto" };
            string[] et = { "Troca só os montantes que você escolheu.", "Também os outros montantes com o mesmo tipo atual nas mesmas paredes cortina.", "Todos os montantes com o mesmo tipo atual, em qualquer parede do projeto." };
            for (int i = 0; i < 3; i++)
            {
                var rb = new RadioButton { Content = en[i], GroupName = "troca-escopo", ToolTip = et[i], Margin = new Thickness(0, 0, 0, 4) };
                rb.SetResourceReference(StyleProperty, "Ja.Segment");
                rb.Checked += (s, e) => Atualizar();
                esc.Children.Add(rb);
                _escopo[i] = rb;
            }
            right.Children.Add(ToolChrome.Card(esc, "Trocar"));

            var dir = new WrapPanel();
            string[] dn = { "Todos", "Verticais", "Horizontais" };
            for (int i = 0; i < 3; i++)
            {
                var rb = new RadioButton { Content = dn[i], GroupName = "troca-dir" };
                rb.SetResourceReference(StyleProperty, "Ja.Segment");
                rb.Checked += (s, e) => Atualizar();
                dir.Children.Add(rb);
                _dir[i] = rb;
            }
            var dirBox = new StackPanel();
            dirBox.Children.Add(ToolChrome.Track(dir));
            dirBox.Children.Add(ToolChrome.Muted("Verticais = montantes; horizontais = travessas.", 11));
            right.Children.Add(ToolChrome.Card(dirBox, "Direção"));
            right.Children.Add(ToolChrome.Muted("Montantes fixados (pino) são liberados para a troca. Tipos de canto vão nos cantos entre paredes cortina.", 11));
            Grid.SetColumn(right, 2);
            body.Children.Add(right);

            ToolChrome.DressCompact(this, "Trocar Montantes", body, Rodape(),
                "Troca o tipo dos montantes escolhidos em planta, corte, elevação ou 3D.");

            int e0 = int.TryParse(ToolPrefs.Get(P + "escopo"), out int a) ? a : 0, d0 = int.TryParse(ToolPrefs.Get(P + "dir"), out int b) ? b : 0;
            _escopo[Math.Max(0, Math.Min(2, e0))].IsChecked = true;
            _dir[Math.Max(0, Math.Min(2, d0))].IsChecked = true;
            string last = ToolPrefs.Get(P + "tipo");
            _escolhido = _tipos.FirstOrDefault(t => t.Familia + "|" + t.Nome == last);
            Desenhar();
            Loaded += (s, e) => _busca.Focus();
            PreviewKeyDown += (s, e) => { if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control && _ok.IsEnabled) { Confirmar(); e.Handled = true; } };
        }

        private UIElement Rodape()
        {
            var bar = new DockPanel();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            var cancel = ToolChrome.Ghost("Cancelar");
            cancel.IsCancel = true; cancel.MinWidth = 88; cancel.Margin = new Thickness(0, 0, 8, 0);
            buttons.Children.Add(cancel);
            _ok = ToolChrome.Primary("Trocar", (s, e) => Confirmar());
            _ok.MinWidth = 150; _ok.IsDefault = true;
            buttons.Children.Add(_ok);
            DockPanel.SetDock(buttons, Dock.Right);
            bar.Children.Add(buttons);
            _status.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Text");
            bar.Children.Add(_status);
            return bar;
        }

        private void Desenhar()
        {
            _lista.Children.Clear();
            string q = Plain(_busca.Text);
            var vis = _tipos.Where(t => q.Length == 0 || Plain(t.Nome + " " + t.Familia).Contains(q)).ToList();
            foreach (var grupo in vis.GroupBy(t => t.Ja ? "Perfis JA" : t.Familia))
            {
                var h = new TextBlock { Text = grupo.Key, FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 10, 0, 3) };
                h.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Muted");
                _lista.Children.Add(h);
                foreach (var t in grupo)
                {
                    var rb = new RadioButton { GroupName = "troca-tipo", IsChecked = ReferenceEquals(t, _escolhido), Margin = new Thickness(2, 2, 0, 2) };
                    var row = new DockPanel();
                    if (t.Usos > 0)
                    {
                        var u = ToolChrome.Muted(t.Usos + " no modelo", 11);
                        DockPanel.SetDock(u, Dock.Right);
                        u.Margin = new Thickness(8, 0, 0, 0);
                        row.Children.Add(u);
                    }
                    var tx = new TextBlock { Text = t.Nome + (t.Canto ? "  · canto" : ""), TextTrimming = TextTrimming.CharacterEllipsis };
                    tx.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Text");
                    row.Children.Add(tx);
                    rb.Content = row;
                    rb.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                    var item = t;
                    rb.Checked += (s, e) => { _escolhido = item; Atualizar(); };
                    rb.MouseDoubleClick += (s, e) => { _escolhido = item; Confirmar(); };
                    _lista.Children.Add(rb);
                }
            }
            if (vis.Count == 0) _lista.Children.Add(ToolChrome.Muted("Nenhum tipo com essa busca.", 11.5));
            Atualizar();
        }

        private void Atualizar()
        {
            if (_ok == null) return;
            _ok.IsEnabled = _escolhido != null;
            _status.Text = _escolhido == null ? "Escolha o novo tipo na lista." : "Novo tipo: " + _escolhido.Nome;
        }

        private TrocaPedido Pedido(bool novo) => new TrocaPedido
        {
            Tipo = _escolhido,
            Escopo = Math.Max(0, Array.FindIndex(_escopo, r => r.IsChecked == true)),
            Direcao = Math.Max(0, Array.FindIndex(_dir, r => r.IsChecked == true)),
            NovoPerfil = novo
        };

        private void Confirmar()
        {
            if (_escolhido == null) return;
            _pedido = Pedido(false);
            ToolPrefs.Set(P + "escopo", _pedido.Escopo.ToString(CultureInfo.InvariantCulture));
            ToolPrefs.Set(P + "dir", _pedido.Direcao.ToString(CultureInfo.InvariantCulture));
            ToolPrefs.Set(P + "tipo", _escolhido.Familia + "|" + _escolhido.Nome);
            ToolPrefs.Save();
            DialogResult = true;
        }

        private static string Plain(string s) => TextUtil.Fold(s).Trim();
    }
}
