using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;

namespace JA.IfcViewer
{
    /// <summary>Look of the viewer (JA green), icons and small control factories — all in code, no XAML.</summary>
    public static class Ui
    {
        public static readonly CultureInfo Br = new CultureInfo("pt-BR");

        public static readonly Color AccentColor = Color.FromRgb(0x1F, 0x7A, 0x5A);
        public static readonly Brush Accent = F(AccentColor);
        public static readonly Brush AccentSoft = F(Color.FromRgb(0xE3, 0xEE, 0xE9));
        public static readonly Brush AccentPressed = F(Color.FromRgb(0xC9, 0xE0, 0xD6));
        public static readonly Brush Line = F(Color.FromRgb(0xD9, 0xDE, 0xE3));
        public static readonly Brush Panel = F(Color.FromRgb(0xF6, 0xF7, 0xF9));
        public static readonly Brush Text = F(Color.FromRgb(0x1E, 0x24, 0x28));
        public static readonly Brush Muted = F(Color.FromRgb(0x6B, 0x74, 0x7C));
        public static readonly Brush White = F(Colors.White);

        public static Brush F(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
        public static Brush F(uint argb) => F(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));

        public static string N(double v, string fmt = "0.###") => v.ToString(fmt, Br);

        // ------------------------------------------------------------------ icons (24 × 24, stroked)

        public static class Icons
        {
            public const string Open = "M 3 7 h 7 l 2 2 h 9 v 10 H 3 z";
            public const string Add = "M 3 7 h 7 l 2 2 h 9 v 10 H 3 z M 12 11 v 6 M 9 14 h 6";
            public const string Close = "M 6 6 L 18 18 M 18 6 L 6 18";
            public const string Fit = "M 3 9 V 3 h 6 M 15 3 h 6 v 6 M 21 15 v 6 h -6 M 9 21 H 3 v -6";
            public const string FitSel = "M 3 9 V 3 h 6 M 15 3 h 6 v 6 M 21 15 v 6 h -6 M 9 21 H 3 v -6 M 9 9 h 6 v 6 h -6 z";
            public const string Iso = "M 12 3 L 20 7.5 V 16.5 L 12 21 L 4 16.5 V 7.5 Z M 4 7.5 L 12 12 L 20 7.5 M 12 12 V 21";
            public const string Top = "M 4 4 h 16 v 16 H 4 z M 9 9 h 6 v 6 h -6 z";
            public const string Front = "M 4 9 h 11 v 11 H 4 z M 4 9 l 5 -5 h 11 l -5 5 M 20 4 v 11 l -5 5";
            public const string Persp = "M 3 20 L 8 5 h 8 l 5 15 z M 6 12 h 12";
            public const string Hide = "M 2 12 C 5 6 19 6 22 12 C 19 18 5 18 2 12 Z M 12 9.5 A 2.5 2.5 0 1 0 12.01 9.5 M 4 20 L 20 4";
            public const string Isolate = "M 12 3 A 9 9 0 1 0 12.01 3 M 12 8.5 A 3.5 3.5 0 1 0 12.01 8.5";
            public const string Show = "M 2 12 C 5 6 19 6 22 12 C 19 18 5 18 2 12 Z M 12 9.5 A 2.5 2.5 0 1 0 12.01 9.5";
            public const string Transparent = "M 3 5 h 11 v 11 H 3 z M 10 8 h 11 v 11 H 10 z";
            public const string XRay = "M 4 4 h 16 v 16 H 4 z M 4 12 h 16 M 12 4 v 16";
            public const string Spaces = "M 4 4 h 16 v 16 H 4 z M 4 13 h 9 v 7 M 13 4 v 5";
            public const string Box = "M 5 8 L 12 4 L 19 8 V 16 L 12 20 L 5 16 Z M 5 8 L 12 12 L 19 8 M 12 12 V 20";
            public const string Plane = "M 3 16 L 9 10 h 12 l -6 6 z M 12 3 v 18";
            public const string Distance = "M 3 16 L 16 3 L 21 8 L 8 21 Z M 7 12 l 2 2 M 10 9 l 2 2 M 13 6 l 2 2";
            public const string Area = "M 4 6 L 18 4 L 20 18 L 6 20 Z M 4 6 L 20 18";
            public const string Point = "M 12 2 v 7 M 12 15 v 7 M 2 12 h 7 M 15 12 h 7 M 12 10 A 2 2 0 1 0 12.01 10";
            public const string Clear = "M 5 7 h 14 M 9 7 V 4 h 6 v 3 M 7 7 l 1 13 h 8 l 1 -13";
            public const string Camera = "M 3 8 h 4 l 2 -3 h 6 l 2 3 h 4 v 11 H 3 z M 12 9.5 A 3.5 3.5 0 1 0 12.01 9.5";
            public const string Help = "M 12 3 A 9 9 0 1 0 12.01 3 M 9.5 9.5 A 2.5 2.5 0 1 1 13 11.8 C 12.3 12.1 12 12.7 12 13.5 M 12 17 v 0.5";
            public const string Info = "M 12 3 A 9 9 0 1 0 12.01 3 M 12 10 v 7 M 12 7 v 0.5";
            public const string Select = "M 5 3 L 19 12 L 12 13 L 9 20 Z";
            public const string Filter = "M 3 4 h 18 l -7 8 v 6 l -4 2 v -8 z";
        }

        public static Path Icon(string data, double size = 20, Brush stroke = null)
        {
            var p = new Path
            {
                Data = System.Windows.Media.Geometry.Parse(data),
                Stroke = stroke ?? Text,
                StrokeThickness = 1.5,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Width = size,
                Height = size,
                Stretch = Stretch.Uniform,
                SnapsToDevicePixels = true
            };
            return p;
        }

        // ------------------------------------------------------------------ templates

        private static ControlTemplate _flat, _flatToggle;

        private static ControlTemplate FlatTemplate(Type t)
        {
            var tpl = new ControlTemplate(t);
            var border = new FrameworkElementFactory(typeof(Border), "b");
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(cp);
            tpl.VisualTree = border;
            var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            over.Setters.Add(new Setter(Border.BackgroundProperty, AccentSoft, "b"));
            tpl.Triggers.Add(over);
            var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(Border.BackgroundProperty, AccentPressed, "b"));
            tpl.Triggers.Add(pressed);
            if (t == typeof(ToggleButton))
            {
                var chk = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
                chk.Setters.Add(new Setter(Border.BackgroundProperty, AccentPressed, "b"));
                chk.Setters.Add(new Setter(Border.BorderBrushProperty, Accent, "b"));
                tpl.Triggers.Add(chk);
            }
            var dis = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            dis.Setters.Add(new Setter(UIElement.OpacityProperty, 0.4));
            tpl.Triggers.Add(dis);
            return tpl;
        }

        private static void Flat(ButtonBase b)
        {
            if (b is ToggleButton) b.Template = _flatToggle ?? (_flatToggle = FlatTemplate(typeof(ToggleButton)));
            else b.Template = _flat ?? (_flat = FlatTemplate(typeof(Button)));
            b.Background = Brushes.Transparent;
            b.BorderBrush = Brushes.Transparent;
            b.BorderThickness = new Thickness(1);
            b.Foreground = Text;
            b.Cursor = System.Windows.Input.Cursors.Hand;
            b.Focusable = false;
        }

        private static StackPanel ToolContent(string label, string icon)
        {
            var sp = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center };
            sp.Children.Add(Icon(icon, 22));
            sp.Children.Add(new TextBlock { Text = label, FontSize = 11, Margin = new Thickness(0, 2, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, Foreground = Text });
            return sp;
        }

        public static Button Tool(string label, string icon, string tip, Action click)
        {
            var b = new Button { Content = ToolContent(label, icon), ToolTip = tip, Padding = new Thickness(6, 4, 6, 3), MinWidth = 54, Margin = new Thickness(1, 0, 1, 0) };
            Flat(b);
            b.Click += (s, e) => click();
            return b;
        }

        public static ToggleButton ToolToggle(string label, string icon, string tip, Action<bool> changed)
        {
            var b = new ToggleButton { Content = ToolContent(label, icon), ToolTip = tip, Padding = new Thickness(6, 4, 6, 3), MinWidth = 54, Margin = new Thickness(1, 0, 1, 0) };
            Flat(b);
            b.Click += (s, e) => changed(b.IsChecked == true);
            return b;
        }

        public static Button Small(string text, Action click, string tip = null)
        {
            var b = new Button
            {
                Content = new TextBlock { Text = text, Foreground = Text },
                ToolTip = tip,
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(0, 0, 4, 4)
            };
            Flat(b);
            b.BorderBrush = Line;
            b.Background = White;
            b.Click += (s, e) => click();
            return b;
        }

        public static Button Primary(string text, Action click)
        {
            var b = Small(text, click);
            b.Background = Accent;
            b.BorderBrush = Accent;
            ((TextBlock)b.Content).Foreground = White;
            ((TextBlock)b.Content).FontWeight = FontWeights.SemiBold;
            return b;
        }

        public static FrameworkElement Separator() => new Border { Width = 1, Background = Line, Margin = new Thickness(5, 6, 5, 6) };

        public static TextBlock Header(string text) => new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Foreground = Text, Margin = new Thickness(0, 8, 0, 4) };

        public static TextBlock Note(string text) => new TextBlock { Text = text, Foreground = Muted, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 0, 0, 6) };

        /// <summary>A labelled group of toolbar buttons (label under the group, like a ribbon).</summary>
        public static FrameworkElement Group(string title, params UIElement[] items)
        {
            var g = new Grid { Margin = new Thickness(2, 0, 2, 0) };
            g.RowDefinitions.Add(new RowDefinition());
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (UIElement i in items) sp.Children.Add(i);
            g.Children.Add(sp);
            var t = new TextBlock { Text = title, FontSize = 10, Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1, 0, 0) };
            Grid.SetRow(t, 1);
            g.Children.Add(t);
            return g;
        }

        public static Rectangle Swatch(uint argb, double size = 12) =>
            new Rectangle { Width = size, Height = size, Fill = F(argb | 0xFF000000), Stroke = Line, StrokeThickness = 1, RadiusX = 2, RadiusY = 2, VerticalAlignment = VerticalAlignment.Center };

        /// <summary>Asks for a number with pt-BR or invariant decimals.</summary>
        public static bool TryNumber(string s, out double v)
        {
            v = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim().Replace(" ", "");
            if (double.TryParse(s, NumberStyles.Float, Br, out v)) return true;
            return double.TryParse(s.Replace(",", "."), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }
    }
}
