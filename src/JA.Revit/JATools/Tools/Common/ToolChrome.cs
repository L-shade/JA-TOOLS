using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RevitDwgUnifier.UI;

namespace RevitDwgUnifier.Tools.Common
{
    /// <summary>
    /// Building blocks shared by every JA Tools window (options dialog, Gestão de Pranchas, Editar
    /// Níveis, Gerar Detalhes, Elevações...): the same compact header, cards and buttons, so every
    /// window feels like one product.
    /// </summary>
    public static class ToolChrome
    {
        /// <summary>
        /// Lighter chrome (as the Varinha Mágica panel): small icon tile, title and one muted line,
        /// a "?" for help; no breadcrumb and no steps strip. Body sits on the grey surface.
        /// </summary>
        /// <summary>
        /// The big windows of the JA Famílias tab (configurators of esquadrias and pieces, Bancada no
        /// Local): since 2.0 the same compact chrome as every other window of both tabs.
        /// </summary>
        public static void Dress(Window w, string toolTitle, UIElement body, UIElement footer) => DressCompact(w, toolTitle, body, footer);

        public static void DressCompact(Window w, string toolTitle, UIElement body, UIElement footer, string subtitle = null)
        {
            var info = ToolCatalog.Get(toolTitle);
            JaTheme.Apply(w, info.Accent);
            w.Title = ToolCatalog.Marca(toolTitle) + " — " + toolTitle;
            var dock = new DockPanel();
            dock.SetResourceReference(Panel.BackgroundProperty, "Ja.Surface");

            var head = new Grid { Margin = new Thickness(22, 16, 22, 8) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(new Image { Source = IconFactory.Tool(64, info.Icon, info.Glyph, info.Accent), Width = 32, Height = 32, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(text, 1);
            var title = new TextBlock { Text = info.Title, FontSize = 16, FontWeight = FontWeights.SemiBold };
            title.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Ink");
            text.Children.Add(title);
            string sub = subtitle ?? info.Description;
            if (!string.IsNullOrEmpty(sub))
            {
                var d = new TextBlock { Text = sub, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = info.Description };
                d.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Muted");
                text.Children.Add(d);
            }
            head.Children.Add(text);
            var help = Ghost("?", "Central de Ajuda (F1)");
            help.FontWeight = FontWeights.SemiBold;
            help.VerticalAlignment = VerticalAlignment.Center;
            help.Click += (s, e) => HelpCenter.Open(info.Anchor);
            Grid.SetColumn(help, 2);
            head.Children.Add(help);
            DockPanel.SetDock(head, Dock.Top);
            dock.Children.Add(head);

            if (footer != null)
            {
                var f = new Border { Padding = new Thickness(22, 12, 22, 12), BorderThickness = new Thickness(0, 1, 0, 0), Child = footer };
                f.SetResourceReference(Border.BackgroundProperty, "Ja.Card");
                f.SetResourceReference(Border.BorderBrushProperty, "Ja.Line");
                DockPanel.SetDock(f, Dock.Bottom);
                dock.Children.Add(f);
            }
            dock.Children.Add(new Border { Padding = new Thickness(22, 4, 22, 0), Child = body });
            w.Content = dock;
            w.PreviewKeyDown += (s, e) => { if (e.Key == Key.F1) { HelpCenter.Open(info.Anchor); e.Handled = true; } };
        }

        /// <summary>Small muted label above a control (Varinha style).</summary>
        public static FrameworkElement Labeled(string label, UIElement control, string tip = null)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            var t = new TextBlock { Text = label, FontSize = 11, Margin = new Thickness(0, 0, 0, 3) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Muted");
            sp.Children.Add(t);
            sp.Children.Add(control);
            if (tip != null) sp.ToolTip = tip;
            return sp;
        }

        /// <summary>Grey track holding segments / toggles (Varinha style).</summary>
        public static Border Track(UIElement content)
        {
            if (content is Panel panel)
                for (int i = 0; i < panel.Children.Count; i++)
                    if (panel.Children[i] is FrameworkElement fe) fe.Margin = new Thickness(0, 0, i < panel.Children.Count - 1 ? 3 : 0, 0);
            var b = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(3), Child = content, HorizontalAlignment = HorizontalAlignment.Left };
            b.SetResourceReference(Border.BackgroundProperty, "Ja.Field");
            return b;
        }

        /// <summary>White rounded card with an optional title (and optional element at its right).</summary>
        public static Border Card(UIElement content, string title = null, UIElement titleRight = null)
        {
            var card = new Border();
            card.SetResourceReference(FrameworkElement.StyleProperty, "Ja.CardBorder");
            var dock = new DockPanel();
            if (title != null)
            {
                var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
                if (titleRight != null) { DockPanel.SetDock(titleRight, Dock.Right); head.Children.Add(titleRight); }
                var t = new TextBlock { Text = title, FontSize = 13.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
                t.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Ink");
                head.Children.Add(t);
                DockPanel.SetDock(head, Dock.Top);
                dock.Children.Add(head);
            }
            dock.Children.Add(content);
            card.Child = dock;
            return card;
        }

        public static TextBlock Muted(string text, double size = 11.5)
        {
            var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Muted");
            return t;
        }

        public static Button Primary(string text, RoutedEventHandler click = null) => Make(text, "Ja.Primary", click, null);
        public static Button Secondary(string text, RoutedEventHandler click = null, string tip = null) => Make(text, "Ja.Secondary", click, tip);
        public static Button Ghost(string text, string tip = null, RoutedEventHandler click = null) => Make(text, "Ja.Ghost", click, tip);

        private static Button Make(string text, string style, RoutedEventHandler click, string tip)
        {
            var b = new Button { Content = text, ToolTip = tip };
            b.SetResourceReference(FrameworkElement.StyleProperty, style);
            if (click != null) b.Click += click;
            return b;
        }

        /// <summary>A search TextBox with a "Buscar…" placeholder.</summary>
        public static FrameworkElement SearchBox(TextBox box, string placeholder = "Buscar…")
        {
            var ph = new TextBlock { Text = placeholder, IsHitTestVisible = false, Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            ph.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Muted");
            box.TextChanged += (s, e) => ph.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            var g = new Grid();
            g.Children.Add(box);
            g.Children.Add(ph);
            return g;
        }

        /// <summary>Small accent pill ("12 vistas").</summary>
        public static Border Badge(out TextBlock text)
        {
            text = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold };
            text.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Accent");
            var pill = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 1, 8, 2), Child = text, VerticalAlignment = VerticalAlignment.Center };
            pill.SetResourceReference(Border.BackgroundProperty, "Ja.AccentSoft");
            return pill;
        }
    }
}
