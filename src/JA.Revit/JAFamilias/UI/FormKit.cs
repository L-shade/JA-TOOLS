using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace JAEsquadrias.UI
{
    /// <summary>
    /// The form controls shared by the JA Famílias configurators (esquadria, peça, bancada): one copy of
    /// each, the windows bind them to their own state (refresh, invalid fields, status line).
    /// </summary>
    internal static class FormKit
    {
        public static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));

        /// <summary>The large configurator window (catalog | preview | tabs), not dressed yet.</summary>
        public static Window NewConfiguratorWindow()
        {
            var area = SystemParameters.WorkArea;
            return new Window
            {
                Width = Math.Min(1300, area.Width * 0.96),
                Height = Math.Min(880, area.Height * 0.94),
                MinWidth = 1040,
                MinHeight = 640,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
                ResizeMode = ResizeMode.CanResizeWithGrip
            };
        }

        /// <summary>Ctrl+Enter runs the main action.</summary>
        public static void OnCtrlEnter(Window w, Action action)
        {
            w.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { action(); e.Handled = true; }
            };
        }

        /// <summary>Three columns: the catalog, the preview and (column 2, added by the window) the tabs.</summary>
        public static Grid CatalogPreviewGrid(FrameworkElement left, FrameworkElement center)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(276) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 340 });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(410) });
            left.Margin = new Thickness(0, 0, 12, 12);
            g.Children.Add(left);
            center.Margin = new Thickness(0, 0, 12, 12);
            Grid.SetColumn(center, 1);
            g.Children.Add(center);
            return g;
        }

        /// <summary>Label on the left, the control and an optional hint on the right.</summary>
        public static FrameworkElement Field(string label, UIElement control, string hint, double labelWidth = 116)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 8, 0), TextWrapping = TextWrapping.Wrap };
            l.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Text");
            g.Children.Add(l);
            var right = new StackPanel();
            right.Children.Add(control);
            if (!string.IsNullOrEmpty(hint))
            {
                var h = ToolChrome.Muted(hint, 11);
                h.Margin = new Thickness(0, 3, 0, 0);
                right.Children.Add(h);
            }
            Grid.SetColumn(right, 1);
            g.Children.Add(right);
            return g;
        }

        /// <summary>Editable list of the project's materials; set gets the text as typed or picked.</summary>
        public static FrameworkElement MaterialInput(IEnumerable<string> materials, string value, Action<string> set)
        {
            var cb = new ComboBox { IsEditable = true, IsTextSearchEnabled = true };
            foreach (string m in materials) cb.Items.Add(m);
            cb.Text = value ?? "";
            cb.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
                new TextChangedEventHandler((s, e) => set((cb.Text ?? "").Trim())));
            cb.SelectionChanged += (s, e) => { if (cb.SelectedItem is string m) set(m); };
            return cb;
        }

        /// <summary>RRGGBB box with a swatch; set gets the valid colour (upper case), markInvalid flags the box.</summary>
        public static FrameworkElement HexInput(string value, Action<string> set, Action<TextBox, bool> markInvalid)
        {
            var tb = new TextBox { Text = value ?? "", MaxLength = 7, Width = 92, HorizontalAlignment = HorizontalAlignment.Left };
            var sw = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Margin = new Thickness(6, 0, 0, 0) };
            sw.SetResourceReference(Border.BorderBrushProperty, "Ja.Line");
            Action paint = () => sw.Background = new SolidColorBrush(ElevationRenderer.Hex(tb.Text, Colors.Transparent));
            paint();
            tb.TextChanged += (s, e) =>
            {
                string h = tb.Text.Trim().TrimStart('#');
                bool ok = Regex.IsMatch(h, "^[0-9A-Fa-f]{6}$");
                markInvalid(tb, !ok);
                if (!ok) return;
                paint();
                set(h.ToUpperInvariant());
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(tb);
            row.Children.Add(sw);
            return row;
        }

        /// <summary>Red border and remembered (the window refuses to finish), or back to normal.</summary>
        public static void MarkInvalid(HashSet<TextBox> invalid, TextBox tb, bool bad)
        {
            if (bad) { invalid.Add(tb); tb.BorderBrush = ErrorBrush; }
            else { invalid.Remove(tb); tb.SetResourceReference(Control.BorderBrushProperty, "Ja.Line"); }
        }

        /// <summary>Segmented choice (one of the labels).</summary>
        public static FrameworkElement Pills(string[] labels, int selected, Action<int> pick)
        {
            var wp = new WrapPanel();
            string group = "p" + Guid.NewGuid().ToString("N");
            for (int i = 0; i < labels.Length; i++)
            {
                var rb = new RadioButton { Content = labels[i], GroupName = group, IsChecked = i == selected };
                rb.SetResourceReference(FrameworkElement.StyleProperty, "Ja.Segment");
                int k = i;
                rb.Checked += (s, e) => pick(k);
                wp.Children.Add(rb);
            }
            return wp;
        }

        /// <summary>Check box with an optional hint under it.</summary>
        public static FrameworkElement Check(string label, bool value, Action<bool> set, string hint)
        {
            var cb = new CheckBox { Content = label, IsChecked = value };
            cb.Checked += (s, e) => set(true);
            cb.Unchecked += (s, e) => set(false);
            if (string.IsNullOrEmpty(hint)) { cb.Margin = new Thickness(0, 2, 0, 8); return cb; }
            var sp = new StackPanel { Margin = new Thickness(0, 2, 0, 8) };
            sp.Children.Add(cb);
            var h = ToolChrome.Muted(hint, 11);
            h.Margin = new Thickness(22, 0, 0, 0);
            sp.Children.Add(h);
            return sp;
        }

        public static Button SmallButton(string text, string tip, Action click)
        {
            var b = ToolChrome.Secondary(text, (s, e) => click(), tip);
            b.Padding = new Thickness(9, 2, 9, 3);
            b.MinWidth = 28;
            return b;
        }

        /// <summary>Pill with a figure of the summary.</summary>
        public static FrameworkElement Stat(string text, string tip = null)
        {
            var t = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Accent");
            var b = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 3, 10, 4), Margin = new Thickness(0, 0, 8, 4), Child = t, ToolTip = tip };
            b.SetResourceReference(Border.BackgroundProperty, "Ja.AccentSoft");
            return b;
        }

        public static void SetStatus(TextBlock status, string text, bool error)
        {
            if (status == null) return;
            status.Text = text;
            if (error) status.Foreground = ErrorBrush;
            else status.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Muted");
        }

        /// <summary>Puts the content of a tab in a scroll viewer.</summary>
        public static void SetTab(TabControl tabs, int i, UIElement content) =>
            ((TabItem)tabs.Items[i]).Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 6, 0) };

        /// <summary>Lower case without accents, trimmed (searches).</summary>
        public static string Plain(string s) => TextUtil.Fold(s).Trim();
    }

    /// <summary>
    /// The compact form of the profile and panel windows (Perfil, Painel): small number boxes with the
    /// unit beside them, label above, pt-BR numbers.
    /// </summary>
    internal static class CompactForm
    {
        public static readonly CultureInfo Br = CultureInfo.GetCultureInfo("pt-BR");

        public static TextBox Num() => new TextBox { Width = 72, HorizontalContentAlignment = HorizontalAlignment.Right };

        public static string N(double v) => v.ToString("0.##", Br);

        public static void Vis(FrameworkElement e, bool on) { if (e != null) e.Visibility = on ? Visibility.Visible : Visibility.Collapsed; }

        public static CheckBox Toggle(string text)
        {
            var c = new CheckBox { Content = text, FontSize = 11.5 };
            c.SetResourceReference(FrameworkElement.StyleProperty, "Ja.Toggle");
            return c;
        }

        public static FrameworkElement Field(string label, TextBox box, string unit, string tip, out TextBlock labelText)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 14, 8) };
            labelText = new TextBlock { Text = label, FontSize = 11, Margin = new Thickness(0, 0, 0, 3) };
            labelText.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Muted");
            sp.Children.Add(labelText);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(box);
            var u = ToolChrome.Muted(unit, 11.5);
            u.VerticalAlignment = VerticalAlignment.Center;
            u.Margin = new Thickness(5, 0, 0, 0);
            row.Children.Add(u);
            sp.Children.Add(row);
            if (tip != null) sp.ToolTip = tip;
            return sp;
        }

        /// <summary>The number in the box (pt-BR or with a dot), or the fallback; zero/negative only if allowed.</summary>
        public static double D(TextBox tb, double fallback, bool allowZeroOrNegative = false)
        {
            string s = (tb.Text ?? "").Trim().Replace(" ", "");
            if (!double.TryParse(s, NumberStyles.Float, Br, out double v) && !double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return fallback;
            return allowZeroOrNegative || v > 0 ? v : fallback;
        }
    }
}
