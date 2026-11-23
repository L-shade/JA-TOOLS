using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace JAEsquadrias.UI
{
    /// <summary>Small themed input box ("Nome na biblioteca").</summary>
    public static class Prompt
    {
        public static string Ask(Window owner, string title, string label, string initial, Color accent)
        {
            var w = new Window
            {
                Title = "JA Famílias — " + title,
                Width = 420,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                ShowInTaskbar = false,
                Owner = owner
            };
            JaTheme.Apply(w, accent);
            var box = new TextBox { Text = initial ?? "", Margin = new Thickness(0, 6, 0, 0) };
            var panel = new StackPanel { Margin = new Thickness(20, 18, 20, 12) };
            var t = new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Ink");
            panel.Children.Add(t);
            panel.Children.Add(ToolChrome.Muted(label, 12));
            panel.Children.Add(box);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            var cancel = ToolChrome.Secondary("Cancelar");
            cancel.IsCancel = true;
            cancel.Margin = new Thickness(0, 0, 8, 0);
            var ok = ToolChrome.Primary("Salvar");
            ok.IsDefault = true;
            string result = null;
            ok.Click += (s, e) =>
            {
                if (box.Text.Trim().Length == 0) { box.Focus(); return; }
                result = box.Text.Trim();
                w.DialogResult = true;
            };
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
            panel.Children.Add(buttons);
            w.Content = panel;
            w.Loaded += (s, e) => { box.Focus(); box.SelectAll(); };
            return w.ShowDialog() == true ? result : null;
        }
    }
}
