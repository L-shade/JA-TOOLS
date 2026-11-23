using System.Windows;
using System.Windows.Media;

namespace RevitDwgUnifier.UI
{
    /// <summary>
    /// The JA Tools resource dictionary (JaTheme.xaml), created through its generated class so it
    /// loads reliably inside Revit (no pack-URI lookup by the host application).
    /// </summary>
    public partial class JaTheme : ResourceDictionary
    {
        public JaTheme()
        {
            InitializeComponent();
        }

        /// <summary>Applies the theme to a window, with the accent colour of its ribbon group.</summary>
        public static void Apply(Window w, Color accent)
        {
            var theme = new JaTheme();
            theme["Ja.Accent"] = new SolidColorBrush(accent);
            theme["Ja.AccentSoft"] = new SolidColorBrush(IconFactory.Soft(accent, 0.12));
            w.Resources.MergedDictionaries.Add(theme);
            w.FontFamily = new FontFamily("Segoe UI");
            w.FontSize = 12.5;
            w.Background = (Brush)theme["Ja.Surface"];
            w.UseLayoutRounding = true;
            w.SnapsToDevicePixels = true;
        }

        /// <summary>Applies the theme to a panel hosted outside a JA window (Revit dockable pane).</summary>
        public static void ApplyTo(System.Windows.Controls.Control c, Color accent)
        {
            var theme = new JaTheme();
            theme["Ja.Accent"] = new SolidColorBrush(accent);
            theme["Ja.AccentSoft"] = new SolidColorBrush(IconFactory.Soft(accent, 0.12));
            c.Resources.MergedDictionaries.Add(theme);
            c.FontFamily = new FontFamily("Segoe UI");
            c.FontSize = 12.5;
            c.Background = (Brush)theme["Ja.Surface"];
            c.UseLayoutRounding = true;
            c.SnapsToDevicePixels = true;
        }
    }
}
