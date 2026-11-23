using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using RevitDwgUnifier.Tools.Common;
using RevitDwgUnifier.UI;

namespace RevitDwgUnifier.Tools.Detail
{
    /// <summary>
    /// The wand's small panel, in the bottom-right corner of the Revit window: what to create, its
    /// type, a few more options folded away, the count and the shortcut. It never takes the focus
    /// from the view (the clicks keep going to Revit), stays above Revit, can be dragged (the place
    /// is remembered) and its choices are read at every click, so they can change mid-session.
    /// </summary>
    internal sealed class VarinhaPainel
    {
        public sealed class Grupo
        {
            public string Rotulo;            // segment text ("Hachura")
            public string Chave;             // preference key of its type list
            public List<string> Tipos;       // types to choose (empty = none in the project)
            public string SemTipos;          // message when there is none
        }

        private readonly IntPtr _owner;
        private readonly string _prefs;
        private readonly List<Grupo> _grupos;
        private readonly List<string> _bordas, _modos;
        private Window _win;
        private readonly List<RadioButton> _seg = new List<RadioButton>();
        private ComboBox _tipo, _borda, _modo;
        private StackPanel _mais, _bordaRow;
        private TextBlock _contagem, _aviso, _atalho;
        private int _grupo;
        private readonly int[] _tipoIdx;
        private bool _carregando;

        public int Criar => _grupo;
        public int Tipo => _tipoIdx[_grupo];
        public int Borda => _borda?.SelectedIndex ?? 0;
        public int Modo => _modo?.SelectedIndex ?? 0;

        public VarinhaPainel(IntPtr owner, string prefsKey, List<Grupo> grupos, List<string> bordas, List<string> modos)
        {
            _owner = owner;
            _prefs = prefsKey;
            _grupos = grupos;
            _bordas = bordas;
            _modos = modos;
            _tipoIdx = new int[grupos.Count];
            _grupo = Math.Max(0, grupos.FindIndex(g => g.Rotulo == ToolPrefs.Get(prefsKey + ".what")));
            for (int i = 0; i < grupos.Count; i++)
                _tipoIdx[i] = Math.Max(0, grupos[i].Tipos.IndexOf(ToolPrefs.Get(prefsKey + "." + grupos[i].Chave)));
        }

        public void Mostrar()
        {
            var info = ToolCatalog.Get("Varinha Mágica");
            _win = new Window
            {
                Title = "JA Tools — Varinha Mágica",
                Width = 300,
                SizeToContent = SizeToContent.Height,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000,
                Top = -10000
            };
            JaTheme.Apply(_win, info.Accent);
            _win.Background = Brushes.Transparent;
            if (_owner != IntPtr.Zero) new WindowInteropHelper(_win).Owner = _owner;

            var card = new Border
            {
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(10),
                Padding = new Thickness(14, 12, 14, 12),
                Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.18, Color = Colors.Black }
            };
            card.SetResourceReference(Border.BackgroundProperty, "Ja.Card");
            card.SetResourceReference(Border.BorderBrushProperty, "Ja.Line");
            var root = new StackPanel();
            card.Child = root;
            _win.Content = card;

            // ---- header (drag handle)
            var head = new Grid { Margin = new Thickness(0, 0, 0, 10), Background = Brushes.Transparent, Cursor = Cursors.SizeAll };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(new Image { Source = IconFactory.Tool(32, info.Icon, info.Glyph, info.Accent), Width = 24, Height = 24, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
            var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(titles, 1);
            var title = new TextBlock { Text = "Varinha Mágica", FontWeight = FontWeights.SemiBold, FontSize = 13 };
            title.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Ink");
            titles.Children.Add(title);
            _contagem = new TextBlock { Text = "Clique dentro de uma área fechada", FontSize = 11 };
            _contagem.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Muted");
            titles.Children.Add(_contagem);
            head.Children.Add(titles);
            var close = new Button { Content = "✕", ToolTip = "Terminar (Esc)", Padding = new Thickness(8, 2, 8, 2), VerticalAlignment = VerticalAlignment.Top, Cursor = Cursors.Hand };
            close.SetResourceReference(FrameworkElement.StyleProperty, "Ja.Ghost");
            close.Click += (s, e) => VarinhaAtalho.Encerrar();
            Grid.SetColumn(close, 2);
            head.Children.Add(close);
            head.MouseLeftButtonDown += (s, e) => { try { _win.DragMove(); } catch { } GuardarPosicao(); VarinhaAtalho.DevolverFoco(); };
            root.Children.Add(head);

            // ---- what to create (segmented)
            var wrap = new WrapPanel();
            string group = "w" + Guid.NewGuid().ToString("N");
            for (int i = 0; i < _grupos.Count; i++)
            {
                int k = i;
                var rb = new RadioButton { Content = _grupos[i].Rotulo, GroupName = group, IsChecked = i == _grupo, FontSize = 11.5 };
                rb.SetResourceReference(FrameworkElement.StyleProperty, "Ja.Segment");
                rb.Margin = new Thickness(0, 0, i < _grupos.Count - 1 ? 3 : 0, 0);
                rb.Padding = new Thickness(0);
                rb.Checked += (s, e) => { if (_carregando) return; _grupo = k; Guardar(); AtualizarTipo(); VarinhaAtalho.DevolverFoco(); };
                _seg.Add(rb);
                wrap.Children.Add(rb);
            }
            var track = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(3), Child = wrap, Margin = new Thickness(0, 0, 0, 8) };
            track.SetResourceReference(Border.BackgroundProperty, "Ja.Field");
            root.Children.Add(track);

            // ---- type
            _tipo = new ComboBox { Margin = new Thickness(0, 0, 0, 6) };
            _tipo.SelectionChanged += (s, e) =>
            {
                if (_carregando || _tipo.SelectedIndex < 0) return;
                _tipoIdx[_grupo] = _tipo.SelectedIndex;
                Guardar();
                VarinhaAtalho.DevolverFoco();
            };
            root.Children.Add(_tipo);
            _aviso = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 6) };
            _aviso.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Warn");
            root.Children.Add(_aviso);

            // ---- more options (folded)
            var toggle = new Button { Content = "▸  Mais opções", HorizontalAlignment = HorizontalAlignment.Left, FontSize = 11.5 };
            toggle.SetResourceReference(FrameworkElement.StyleProperty, "Ja.Ghost");
            toggle.Padding = new Thickness(0, 2, 4, 2);
            root.Children.Add(toggle);
            _mais = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
            _bordaRow = Linha("Contorno da hachura", _borda = Combo(_bordas, "border"));
            _mais.Children.Add(_bordaRow);
            _mais.Children.Add(Linha("Contorno", _modo = Combo(_modos, "mode")));
            root.Children.Add(_mais);
            bool aberto = ToolPrefs.Get(_prefs + ".__mais") == "1";
            Action dobrar = () =>
            {
                _mais.Visibility = aberto ? Visibility.Visible : Visibility.Collapsed;
                toggle.Content = aberto ? "▾  Menos opções" : "▸  Mais opções";
            };
            toggle.Click += (s, e) => { aberto = !aberto; ToolPrefs.Set(_prefs + ".__mais", aberto ? "1" : "0"); ToolPrefs.Save(); dobrar(); VarinhaAtalho.DevolverFoco(); };
            dobrar();

            // ---- footer: how to end + shortcut
            var foot = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(0, 8, 0, 0) };
            foot.SetResourceReference(Border.BorderBrushProperty, "Ja.Line");
            var fp = new DockPanel();
            var change = new Button { Content = "alterar", FontSize = 11, Cursor = Cursors.Hand, ToolTip = "Escolher outra tecla de atalho para a varinha" };
            change.SetResourceReference(FrameworkElement.StyleProperty, "Ja.Ghost");
            change.Padding = new Thickness(4, 0, 0, 0);
            change.Click += (s, e) => MenuAtalho(change);
            DockPanel.SetDock(change, Dock.Right);
            fp.Children.Add(change);
            _atalho = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            _atalho.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Muted");
            fp.Children.Add(_atalho);
            foot.Child = fp;
            root.Children.Add(foot);
            TextoAtalho();

            _win.PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) { e.Handled = true; VarinhaAtalho.Encerrar(); } };
            AtualizarTipo();
            _win.Show();
            Posicionar();
        }

        public void Fechar()
        {
            try { _win?.Close(); } catch { }
            _win = null;
        }

        /// <summary>Running count, shown under the title.</summary>
        public void Contar(int feitos, int falhas)
        {
            if (_contagem == null) return;
            string s = feitos == 1 ? "1 criado" : feitos + " criados";
            if (falhas > 0) s += " · " + falhas + (falhas == 1 ? " clique sem área fechada" : " cliques sem área fechada");
            _contagem.Text = s;
        }

        public void Aviso(string texto)
        {
            if (_aviso == null) return;
            _aviso.Text = texto ?? "";
            _aviso.Visibility = string.IsNullOrEmpty(texto) ? Visibility.Collapsed : Visibility.Visible;
        }

        // ------------------------------------------------------------------ helpers
        private void AtualizarTipo()
        {
            _carregando = true;
            var g = _grupos[_grupo];
            _tipo.ItemsSource = g.Tipos;
            _tipo.SelectedIndex = g.Tipos.Count > 0 ? Math.Min(_tipoIdx[_grupo], g.Tipos.Count - 1) : -1;
            _tipo.Visibility = g.Tipos.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            _bordaRow.Visibility = _grupo == 0 ? Visibility.Visible : Visibility.Collapsed;
            Aviso(g.Tipos.Count == 0 ? g.SemTipos : null);
            _carregando = false;
        }

        private ComboBox Combo(List<string> items, string key)
        {
            var cb = new ComboBox { ItemsSource = items, FontSize = 11.5 };
            int i = items.IndexOf(ToolPrefs.Get(_prefs + "." + key));
            cb.SelectedIndex = items.Count == 0 ? -1 : Math.Max(0, i);
            cb.SelectionChanged += (s, e) =>
            {
                if (cb.SelectedIndex < 0) return;
                ToolPrefs.Set(_prefs + "." + key, items[cb.SelectedIndex]);
                ToolPrefs.Save();
                VarinhaAtalho.DevolverFoco();
            };
            return cb;
        }

        private static StackPanel Linha(string label, Control control)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
            var t = new TextBlock { Text = label, FontSize = 11, Margin = new Thickness(0, 0, 0, 3) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Muted");
            sp.Children.Add(t);
            sp.Children.Add(control);
            return sp;
        }

        private void Guardar()
        {
            ToolPrefs.Set(_prefs + ".what", _grupos[_grupo].Rotulo);
            var g = _grupos[_grupo];
            if (g.Tipos.Count > 0) ToolPrefs.Set(_prefs + "." + g.Chave, g.Tipos[Math.Min(_tipoIdx[_grupo], g.Tipos.Count - 1)]);
            ToolPrefs.Save();
        }

        private void TextoAtalho()
        {
            string a = VarinhaAtalho.Atual;
            _atalho.Text = a == VarinhaAtalho.Desligado
                ? "Esc termina · atalho desligado"
                : "Esc ou " + a + " termina · atalho: " + a;
        }

        private void MenuAtalho(Button anchor)
        {
            var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
            var head = new MenuItem { Header = "Tecla que inicia a varinha", IsEnabled = false };
            menu.Items.Add(head);
            foreach (string o in VarinhaAtalho.Opcoes)
            {
                string opt = o;
                var mi = new MenuItem { Header = opt, IsCheckable = true, IsChecked = opt == VarinhaAtalho.Atual };
                mi.Click += (s, e) => { VarinhaAtalho.Definir(opt); TextoAtalho(); VarinhaAtalho.DevolverFoco(); };
                menu.Items.Add(mi);
            }
            menu.IsOpen = true;
        }

        // ------------------------------------------------------------------ position
        /// <summary>Bottom-right corner of the Revit window (or where the user dragged it last).</summary>
        private void Posicionar()
        {
            try
            {
                if (_owner == IntPtr.Zero || !GetWindowRect(_owner, out RECT r)) { _win.Left = 40; _win.Top = 40; return; }
                var src = PresentationSource.FromVisual(_win);
                Matrix m = src?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
                Point br = m.Transform(new Point(r.Right, r.Bottom));
                Point tl = m.Transform(new Point(r.Left, r.Top));
                double dx = Ler(".__dx", 24), dy = Ler(".__dy", 56);
                _win.UpdateLayout();
                double w = _win.ActualWidth > 0 ? _win.ActualWidth : _win.Width, h = _win.ActualHeight > 0 ? _win.ActualHeight : 300;
                _win.Left = Math.Max(tl.X, br.X - w - dx);
                _win.Top = Math.Max(tl.Y, br.Y - h - dy);
            }
            catch { _win.Left = 40; _win.Top = 40; }
        }

        /// <summary>Remembers the distance to the bottom-right corner of Revit after a drag.</summary>
        private void GuardarPosicao()
        {
            try
            {
                if (_owner == IntPtr.Zero || !GetWindowRect(_owner, out RECT r)) return;
                var src = PresentationSource.FromVisual(_win);
                Matrix m = src?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
                Point br = m.Transform(new Point(r.Right, r.Bottom));
                ToolPrefs.Set(_prefs + ".__dx", (br.X - (_win.Left + _win.ActualWidth)).ToString("0", CultureInfo.InvariantCulture));
                ToolPrefs.Set(_prefs + ".__dy", (br.Y - (_win.Top + _win.ActualHeight)).ToString("0", CultureInfo.InvariantCulture));
                ToolPrefs.Save();
            }
            catch { }
        }

        private double Ler(string key, double fallback) =>
            double.TryParse(ToolPrefs.Get(_prefs + key), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > -2000 && v < 4000 ? v : fallback;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    }
}
