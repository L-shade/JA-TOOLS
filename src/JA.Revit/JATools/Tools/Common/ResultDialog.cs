using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using RevitDwgUnifier.Infrastructure;
using RevitDwgUnifier.UI;

namespace RevitDwgUnifier.Tools.Common
{
    public enum ResultKind { Auto, Success, Warning, Error, Info }

    /// <summary>Extra button of a result window ("Abrir pasta", "Abrir DWG"...).</summary>
    public sealed class ResultAction
    {
        public string Label;
        public Action Run;
        public bool Primary;
        public ResultAction(string label, Action run, bool primary = false) { Label = label; Run = run; Primary = primary; }
    }

    /// <summary>
    /// The end of every tool: a calm, scannable result instead of a wall of text.
    ///  • status at a glance: colour + icon + plain title ("Concluído", "Concluído com avisos"…);
    ///  • the first line of the message becomes the headline; "Avisos:" / "Erros:" blocks become
    ///    coloured cards with bullets; file paths become links that open the folder;
    ///  • "Copiar" (for e-mail / support), "Abrir registro" when something went wrong, "Ajuda"
    ///    when the tool could not start (the message then says what to do first).
    /// The kind is inferred from the text, so existing tools only call Show(text).
    /// </summary>
    public static class ResultDialog
    {
        public static void Show(IntPtr owner, string toolTitle, string text, ResultKind kind = ResultKind.Auto,
                                string headline = null, params ResultAction[] actions) =>
            Build(owner, toolTitle, text, kind, headline, actions).ShowDialog();

        /// <summary>
        /// Same window, but it does not hold Revit: the command ends right away and Revit is idle
        /// while the result stays on screen (used by Exportar Pranchas, whose AutoCAD image pass
        /// runs in the background and must not wait for this window to be closed).
        /// </summary>
        public static void ShowModeless(IntPtr owner, string toolTitle, string text, ResultKind kind = ResultKind.Auto,
                                        string headline = null, params ResultAction[] actions)
        {
            Window win = Build(owner, toolTitle, text, kind, headline, actions);
            win.ShowInTaskbar = false;
            win.PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) { win.Close(); e.Handled = true; } };   // IsCancel only works in ShowDialog
            win.Show();
            win.Activate();
        }

        private static Window Build(IntPtr owner, string toolTitle, string text, ResultKind kind,
                                    string headline, ResultAction[] actions)
        {
            var info = ToolCatalog.Get(toolTitle);
            var lines = (text ?? "").Replace("\r", "").Split('\n').ToList();
            if (headline == null)
            {
                int first = lines.FindIndex(l => l.Trim().Length > 0);
                headline = first >= 0 ? lines[first].Trim() : "";
                if (first >= 0) lines.RemoveAt(first);
            }
            if (kind == ResultKind.Auto) kind = Infer(headline, lines);

            var win = new Window
            {
                Title = ToolCatalog.Marca(toolTitle) + " — " + toolTitle,
                Width = 540,
                SizeToContent = SizeToContent.Height,
                MaxHeight = SystemParameters.WorkArea.Height * 0.85,
                ResizeMode = ResizeMode.CanResizeWithGrip,
                WindowStartupLocation = owner != IntPtr.Zero ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                ShowInTaskbar = false
            };
            JaTheme.Apply(win, info.Accent);
            if (owner != IntPtr.Zero) new WindowInteropHelper(win).Owner = owner;

            Color status = StatusColor(kind);
            var layout = new DockPanel();

            // ---------- header: status icon + status word + headline
            var head = new Border { Padding = new Thickness(22, 20, 22, 16), BorderThickness = new Thickness(0, 0, 0, 1) };
            head.SetResourceReference(Border.BackgroundProperty, "Ja.Card");
            head.SetResourceReference(Border.BorderBrushProperty, "Ja.Line");
            var hg = new Grid();
            hg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            hg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var circle = new Border
            {
                Width = 44, Height = 44, CornerRadius = new CornerRadius(11), Margin = new Thickness(0, 2, 14, 0), VerticalAlignment = VerticalAlignment.Top,
                Background = new SolidColorBrush(IconFactory.Soft(status, 0.14)),
                Child = new TextBlock
                {
                    Text = Symbol(kind), FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = 20, FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(status), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                }
            };
            hg.Children.Add(circle);
            var ht = new StackPanel();
            Grid.SetColumn(ht, 1);
            ht.Children.Add(new TextBlock
            {
                Text = (StatusWord(kind) + "  ·  " + toolTitle).ToUpperInvariant(), FontSize = 10.5, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(status), TextTrimming = TextTrimming.CharacterEllipsis
            });
            var hl = new TextBlock { Text = headline, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
            hl.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Ink");
            ht.Children.Add(hl);
            hg.Children.Add(ht);
            head.Child = hg;
            DockPanel.SetDock(head, Dock.Top);
            layout.Children.Add(head);

            // ---------- footer
            var footer = new Border { Padding = new Thickness(22, 12, 22, 14), BorderThickness = new Thickness(0, 1, 0, 0) };
            footer.SetResourceReference(Border.BorderBrushProperty, "Ja.Line");
            var fd = new DockPanel();
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(right, Dock.Right);
            foreach (var a in actions ?? new ResultAction[0])
            {
                var b = new Button { Content = a.Label, Margin = new Thickness(0, 0, 8, 0) };
                b.SetResourceReference(FrameworkElement.StyleProperty, a.Primary ? "Ja.Primary" : "Ja.Secondary");
                var act = a;
                b.Click += (s, e) => { try { act.Run?.Invoke(); } catch (Exception ex) { Logger.Warn("[JA Tools] " + ex.Message); } win.Close(); };
                right.Children.Add(b);
            }
            var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, MinWidth = 90 };
            ok.SetResourceReference(FrameworkElement.StyleProperty, "Ja.Primary");
            ok.Click += (s, e) => win.Close();
            right.Children.Add(ok);
            fd.Children.Add(right);

            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var copy = Ghost("Copiar", "Copia o texto para colar num e-mail ou mensagem");
            copy.Click += (s, e) =>
            {
                try { Clipboard.SetText(toolTitle + "\n" + headline + "\n" + string.Join("\n", lines)); copy.Content = "Copiado ✓"; } catch { }
            };
            left.Children.Add(copy);
            if (kind == ResultKind.Error || kind == ResultKind.Warning)
            {
                var log = Ghost("Abrir registro", "Abre o arquivo de registro com os detalhes técnicos");
                log.Click += (s, e) => { Logger.Flush(); OpenPath(Logger.DailyLogPath); };
                left.Children.Add(log);
            }
            if (kind == ResultKind.Info || kind == ResultKind.Error)
            {
                var help = Ghost("Ajuda", "Abre a Central de Ajuda nesta ferramenta");
                help.Click += (s, e) => HelpCenter.Open(info.Anchor);
                left.Children.Add(help);
            }
            fd.Children.Add(left);
            footer.Child = fd;
            DockPanel.SetDock(footer, Dock.Bottom);
            layout.Children.Add(footer);

            // ---------- body
            var body = BuildBody(lines, kind, info);
            if (body != null)
                layout.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            else
                layout.Children.Add(new Border { Height = 4 });

            win.Content = layout;
            win.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0) copy.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            };
            return win;
        }

        // ------------------------------------------------------------------ inference
        private static ResultKind Infer(string headline, List<string> lines)
        {
            string h = headline ?? "";
            if (h.StartsWith("Erro", StringComparison.OrdinalIgnoreCase)) return ResultKind.Error;
            bool warnBlock = lines.Any(l => Regex.IsMatch(l.Trim(), @"^(Avisos?|Erros?|Problemas?)\b.*:$", RegexOptions.IgnoreCase));
            if (Regex.IsMatch(h, @"^0\b") || h.StartsWith("Nenhum", StringComparison.OrdinalIgnoreCase) && lines.All(l => l.Trim().Length == 0))
                return char.IsDigit(h.FirstOrDefault()) ? ResultKind.Warning : ResultKind.Info;
            if (warnBlock) return ResultKind.Warning;
            if (h.Length > 0 && char.IsDigit(h[0])) return ResultKind.Success;
            if (Regex.IsMatch(h, @"(criad|exportad|removid|renumerad|atualizad|conclu|salv|gerad)", RegexOptions.IgnoreCase)) return ResultKind.Success;
            return ResultKind.Info;
        }

        private static Color StatusColor(ResultKind k)
        {
            switch (k)
            {
                case ResultKind.Success: return Color.FromRgb(0x2E, 0x8B, 0x57);
                case ResultKind.Warning: return Color.FromRgb(0xB7, 0x79, 0x1F);
                case ResultKind.Error: return Color.FromRgb(0xC0, 0x39, 0x2B);
                default: return Color.FromRgb(0x2B, 0x57, 0x9A);
            }
        }

        private static string Symbol(ResultKind k)
        {
            switch (k)
            {
                case ResultKind.Success: return "✓";
                case ResultKind.Warning: return "!";
                case ResultKind.Error: return "✕";
                default: return "i";
            }
        }

        private static string StatusWord(ResultKind k)
        {
            switch (k)
            {
                case ResultKind.Success: return "Concluído";
                case ResultKind.Warning: return "Concluído com avisos";
                case ResultKind.Error: return "Não foi possível concluir";
                default: return "Antes de continuar";
            }
        }

        // ------------------------------------------------------------------ body
        private static FrameworkElement BuildBody(List<string> lines, ResultKind kind, ToolInfo info)
        {
            // Split into blocks: free text first, then one block per "Título:" line.
            var blocks = new List<KeyValuePair<string, List<string>>> { new KeyValuePair<string, List<string>>(null, new List<string>()) };
            foreach (string raw in lines)
            {
                string l = raw.TrimEnd();
                string t = l.Trim();
                if (t.EndsWith(":", StringComparison.Ordinal) && t.Length <= 28 && !IsBullet(t) && !LooksLikePath(t))
                {
                    blocks.Add(new KeyValuePair<string, List<string>>(t.TrimEnd(':'), new List<string>()));
                    continue;
                }
                blocks[blocks.Count - 1].Value.Add(l);
            }

            var panel = new StackPanel { Margin = new Thickness(22, 14, 22, 8) };
            foreach (var block in blocks)
            {
                var content = block.Value.SkipWhile(l => l.Trim().Length == 0).ToList();
                while (content.Count > 0 && content[content.Count - 1].Trim().Length == 0) content.RemoveAt(content.Count - 1);
                if (content.Count == 0 && block.Key == null) continue;

                bool bad = block.Key != null && Regex.IsMatch(block.Key, @"^(Erro|Problema)", RegexOptions.IgnoreCase);
                bool warn = block.Key != null && Regex.IsMatch(block.Key, @"^Aviso", RegexOptions.IgnoreCase);
                var card = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 10), BorderThickness = new Thickness(1) };
                if (bad || warn)
                {
                    Color c = bad ? Color.FromRgb(0xC0, 0x39, 0x2B) : Color.FromRgb(0xB7, 0x79, 0x1F);
                    card.Background = new SolidColorBrush(Color.FromArgb(0x14, c.R, c.G, c.B));
                    card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, c.R, c.G, c.B));
                }
                else
                {
                    card.SetResourceReference(Border.BackgroundProperty, "Ja.Card");
                    card.SetResourceReference(Border.BorderBrushProperty, "Ja.Line");
                }

                var inner = new StackPanel();
                if (block.Key != null)
                {
                    int bullets = content.Count(IsBullet);
                    var cap = new TextBlock { Text = block.Key.ToUpperInvariant() + (bullets > 1 && !block.Key.Contains("(") ? $"  ({bullets})" : ""), FontSize = 10.5, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) };
                    if (bad) cap.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
                    else if (warn) cap.Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x63, 0x12));
                    else cap.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Muted");
                    inner.Children.Add(cap);
                }

                var list = new StackPanel();
                foreach (string l in content) list.Children.Add(Line(l));
                if (content.Count > 14)
                    inner.Children.Add(new ScrollViewer { Content = list, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
                else
                    inner.Children.Add(list);
                card.Child = inner;
                panel.Children.Add(card);
            }

            // Undo hint for tools that change the model.
            if ((kind == ResultKind.Success || kind == ResultKind.Warning) && info != null && info.Group != ToolCatalog.Exportar && info.Group != ToolCatalog.Documentar && info.Group != ToolCatalog.Ajustes && !string.IsNullOrEmpty(info.Group))
            {
                var undo = new TextBlock { Text = "Não ficou como esperado? Ctrl+Z no Revit desfaz tudo o que esta ferramenta criou.", FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 6) };
                undo.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Muted");
                panel.Children.Add(undo);
            }
            return panel.Children.Count == 0 ? null : panel;
        }

        private static bool IsBullet(string l)
        {
            string t = l.TrimStart();
            return t.StartsWith("•", StringComparison.Ordinal) || t.StartsWith("- ", StringComparison.Ordinal) || t.StartsWith("* ", StringComparison.Ordinal);
        }

        private static readonly Regex PathRx = new Regex(@"([A-Za-z]:\\|\\\\)[^\t""<>|]*$");
        private static bool LooksLikePath(string l) => PathRx.IsMatch(l.Trim());

        private static FrameworkElement Line(string raw)
        {
            string t = raw.Trim();
            bool bullet = IsBullet(t);
            if (bullet) t = t.TrimStart('•', '-', '*', ' ');

            var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Text");
            if (t.Length == 0) { tb.Height = 6; return tb; }

            var m = PathRx.Match(t);
            if (m.Success)
            {
                if (m.Index > 0) tb.Inlines.Add(new Run(t.Substring(0, m.Index)));
                string path = m.Value.Trim();
                var link = new Hyperlink(new Run(path)) { ToolTip = "Abrir no Explorador de Arquivos" };
                link.SetResourceReference(TextElement.ForegroundProperty, "Ja.Accent");
                link.Click += (s, e) => OpenPath(path);
                tb.Inlines.Add(link);
            }
            else tb.Inlines.Add(new Run(t));

            if (!bullet) return tb;
            var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
            var dot = new TextBlock { Text = "•", Margin = new Thickness(2, 1, 8, 0), VerticalAlignment = VerticalAlignment.Top };
            dot.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Muted");
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot);
            tb.Margin = new Thickness(0);
            row.Children.Add(tb);
            return row;
        }

        private static Button Ghost(string text, string tip)
        {
            var b = new Button { Content = text, ToolTip = tip };
            b.SetResourceReference(FrameworkElement.StyleProperty, "Ja.Ghost");
            return b;
        }

        /// <summary>Opens a file's folder with the file selected, or a folder.</summary>
        public static void OpenPath(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;
                if (File.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
                else if (Directory.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                else
                {
                    string dir = Path.GetDirectoryName(path);
                    if (Directory.Exists(dir)) Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
                }
            }
            catch (Exception ex) { Logger.Warn("[JA Tools] Não foi possível abrir " + path + ": " + ex.Message); }
        }

        /// <summary>Opens a file with its program (DWG → AutoCAD, PDF → reader...).</summary>
        public static void OpenFile(string path)
        {
            try { if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { Logger.Warn("[JA Tools] Não foi possível abrir " + path + ": " + ex.Message); }
        }
    }
}
