using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using RevitDwgUnifier.UI;

namespace RevitDwgUnifier.Tools.Common
{
    /// <summary>
    /// The options window of every button of both tabs (JA Famílias and JA Tools), built in code from a few calls:
    ///
    ///   var dlg = new OptionsDialog("Plantas por Nível", "PlantasPorNivel");
    ///   var levels = dlg.Multi("levels", "Níveis", names);
    ///   var scale  = dlg.Number("scale", "Escala 1:", 50);
    ///   if (!dlg.Show(Owner)) throw new ToolCancelledException();
    ///
    /// UX built in, so every tool teaches itself:
    ///  • the compact chrome of every JA window (ToolChrome.DressCompact): icon, title, one line, "?";
    ///  • options grouped in cards; a list gets its own card with a live "3 de 12" counter and,
    ///    next to other options, fills the left column (two-column window, like Elevações);
    ///  • a help line in the footer explains the option under the mouse / with the keyboard focus;
    ///  • merged tools: one mode switch and VisibleWhen(...) shows only that mode's options;
    ///  • short choices drawn as one-click pills, units shown next to numbers, ↑/↓ to step values,
    ///    clickable {variáveis} for name patterns, "Procurar…" for folders, colour preview for hex;
    ///  • inline validation (red field + message) instead of message boxes;
    ///  • "Depois do OK…" notes turned into a highlighted "Próximo passo" box;
    ///  • primary button named after the action ("Criar plantas"), Esc cancels, F1 opens the help,
    ///    ↺ brings back the factory values. Every value (and the window size) is remembered.
    /// </summary>
    public sealed class OptionsDialog
    {
        // =====================================================================================
        // fields
        // =====================================================================================
        public abstract class Field
        {
            public string Key;
            public string Label;
            /// <summary>Explanation shown in the help bar / tooltip (default: ToolCatalog).</summary>
            public string Help;

            internal string Section;
            internal string Default;
            internal FrameworkElement Root;
            internal TextBlock ErrorText;
            internal Func<bool> EnabledRule;
            internal Func<bool> VisibleRule;
            internal Action Changed;

            internal abstract FrameworkElement BuildControl();
            internal virtual bool FullWidth => false;
            internal abstract string Validate();
            internal abstract void Load(string stored);
            internal abstract string Store();
            internal virtual void FocusControl() { }
            internal virtual Control MainControl => null;
            internal void RaiseChanged() { Changed?.Invoke(); }

            /// <summary>Greys this option out while the rule is false (checked live).</summary>
            public Field EnabledWhen(Func<bool> rule)
            {
                EnabledRule = rule;
                return this;
            }

            /// <summary>Greys this option out while the check box is off.</summary>
            public Field EnabledWhen(BoolField b)
            {
                EnabledRule = () => b.Live;
                return this;
            }

            /// <summary>Greys this option out unless the choice is at one of the indices.</summary>
            public Field EnabledWhen(ChoiceField c, params int[] indices)
            {
                EnabledRule = () => indices.Contains(c.LiveIndex);
                return this;
            }

            /// <summary>Shows this option only while the choice is at one of the indices (merged
            /// tools: one mode switch, each mode shows its own options). Hidden options are not
            /// validated and keep their stored value.</summary>
            public Field VisibleWhen(ChoiceField c, params int[] indices)
            {
                VisibleRule = () => indices.Contains(c.LiveIndex);
                return this;
            }

            public Field VisibleWhen(Func<bool> rule)
            {
                VisibleRule = rule;
                return this;
            }

            internal bool IsShown => VisibleRule == null || VisibleRule();
        }

        public sealed class BoolField : Field
        {
            public bool Value;
            private CheckBox _cb;
            public bool Live => _cb != null ? _cb.IsChecked == true : Value;
            internal override bool FullWidth => true;
            internal override Control MainControl => _cb;

            internal override FrameworkElement BuildControl()
            {
                _cb = new CheckBox
                {
                    Content = new TextBlock { Text = Label, TextWrapping = TextWrapping.Wrap },
                    IsChecked = Value,
                    VerticalContentAlignment = VerticalAlignment.Center
                };
                _cb.Checked += (s, e) => RaiseChanged();
                _cb.Unchecked += (s, e) => RaiseChanged();
                return _cb;
            }
            internal override string Validate() { Value = _cb.IsChecked == true; return null; }
            internal override void Load(string s) { if (s == "1") Value = true; else if (s == "0") Value = false; }
            internal override string Store() => Value ? "1" : "0";
            internal override void FocusControl() { _cb?.Focus(); }
        }

        public sealed class ChoiceField : Field
        {
            public IList<string> Items;
            public int Index;
            public string Value => Index >= 0 && Index < Items.Count ? Items[Index] : null;

            private ComboBox _combo;
            private List<RadioButton> _pills;
            public int LiveIndex
            {
                get
                {
                    if (_combo != null) return _combo.SelectedIndex;
                    if (_pills != null) return _pills.FindIndex(p => p.IsChecked == true);
                    return Index;
                }
            }
            internal override Control MainControl => (Control)_combo ?? _pills?.FirstOrDefault();

            /// <summary>2–4 short options read better as pills (one click, all visible).</summary>
            private bool UsePills => Items.Count >= 2 && Items.Count <= 4 && Items.All(i => (i ?? "").Length <= 28)
                                     && Items.Sum(i => (i ?? "").Length) <= 72;

            internal override FrameworkElement BuildControl()
            {
                _combo = null;
                _pills = null;
                if (UsePills)
                {
                    _pills = new List<RadioButton>();
                    var wrap = new WrapPanel();
                    string group = "g" + Guid.NewGuid().ToString("N");
                    for (int i = 0; i < Items.Count; i++)
                    {
                        var rb = new RadioButton { Content = Items[i], GroupName = group, IsChecked = i == Index };
                        rb.SetResourceReference(FrameworkElement.StyleProperty, "Ja.Segment");
                        rb.Margin = new Thickness(0, 0, i < Items.Count - 1 ? 3 : 0, 0);
                        rb.Checked += (s, e) => RaiseChanged();
                        _pills.Add(rb);
                        wrap.Children.Add(rb);
                    }
                    // segmented control: the options sit on a grey track, the chosen one is white
                    var track = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(3), HorizontalAlignment = HorizontalAlignment.Left, Child = wrap };
                    track.SetResourceReference(Border.BackgroundProperty, "Ja.Field");
                    return track;
                }
                _combo = new ComboBox { ItemsSource = Items, SelectedIndex = Index, MinWidth = 220 };
                _combo.SelectionChanged += (s, e) => RaiseChanged();
                return _combo;
            }
            internal override string Validate()
            {
                int i = LiveIndex;
                if (Items.Count > 0 && i < 0) return "Escolha uma opção.";
                Index = i;
                return null;
            }
            internal override void Load(string s) { int i = Items.IndexOf(s); if (i >= 0) Index = i; }
            internal override string Store() => Value ?? "";
            internal override void FocusControl() { MainControl?.Focus(); }
        }

        public sealed class NumberField : Field
        {
            public double Value;
            public double Min = double.MinValue, Max = double.MaxValue;
            private TextBox _tb;
            internal override Control MainControl => _tb;

            internal override FrameworkElement BuildControl()
            {
                SplitUnit(Label, out string _, out string prefix, out string suffix);
                _tb = new TextBox { Text = Format(Value), Width = 96, HorizontalContentAlignment = HorizontalAlignment.Right };
                _tb.TextChanged += (s, e) => RaiseChanged();
                _tb.GotKeyboardFocus += (s, e) => _tb.SelectAll();
                _tb.PreviewMouseLeftButtonDown += (s, e) =>
                {
                    if (!_tb.IsKeyboardFocusWithin) { _tb.Focus(); e.Handled = true; }
                };
                // ↑ / ↓ step the value (Shift = ×10): quick tuning without typing.
                _tb.PreviewKeyDown += (s, e) =>
                {
                    if (e.Key != System.Windows.Input.Key.Up && e.Key != System.Windows.Input.Key.Down) return;
                    if (!TryParse(_tb.Text, out double v)) v = Value;
                    double step = Step(v) * ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1);
                    v = Math.Round(v + (e.Key == System.Windows.Input.Key.Up ? step : -step), 6);
                    v = Math.Max(Min, Math.Min(Max, v));
                    _tb.Text = Format(v);
                    _tb.SelectAll();
                    e.Handled = true;
                };
                _tb.ToolTip = "Use ↑ / ↓ para ajustar (Shift = passo maior).";

                var row = new StackPanel { Orientation = Orientation.Horizontal };
                if (prefix != null) row.Children.Add(Unit(prefix, new Thickness(0, 0, 6, 0)));
                row.Children.Add(_tb);
                if (suffix != null) row.Children.Add(Unit(suffix, new Thickness(6, 0, 0, 0)));
                if (Min > double.MinValue / 2 && Max < double.MaxValue / 2 && Max - Min < 1e6)
                {
                    var range = new TextBlock { Text = $"{Format(Min)} a {Format(Max)}", Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
                    range.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Muted");
                    row.Children.Add(range);
                }
                return row;
            }

            private static double Step(double v)
            {
                double a = Math.Abs(v);
                if (a == 0) return 1;
                string s = a.ToString("0.######", CultureInfo.InvariantCulture);
                int dot = s.IndexOf('.');
                int decimals = dot < 0 ? 0 : s.Length - dot - 1;
                return decimals >= 2 ? 0.05 : decimals == 1 ? 0.1 : 1;
            }

            private static TextBlock Unit(string text, Thickness margin)
            {
                var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = margin };
                t.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Muted");
                return t;
            }

            internal override string Validate()
            {
                if (!TryParse(_tb.Text, out double v)) return "Digite um número (ex.: 1,5).";
                if (v < Min || v > Max) return $"Use um valor entre {Format(Min)} e {Format(Max)}.";
                Value = v;
                return null;
            }
            internal override void Load(string s) { if (TryParse(s, out double v) && v >= Min && v <= Max) Value = v; }
            internal override string Store() => Value.ToString("R", CultureInfo.InvariantCulture);
            internal override void FocusControl() { _tb?.Focus(); }
        }

        public sealed class TextField : Field
        {
            public string Value;
            /// <summary>What is typed now (while the window is open), else the value.</summary>
            public string LiveValue => _tb != null ? _tb.Text : Value;
            internal OptionsDialog Dialog;
            private TextBox _tb;
            private Border _swatch;
            internal override Control MainControl => _tb;

            private bool IsFolder => Key == "folder" || (Label ?? "").StartsWith("Pasta", StringComparison.OrdinalIgnoreCase);
            private bool IsHexColor => (Label ?? "").IndexOf("(hex)", StringComparison.OrdinalIgnoreCase) >= 0;

            internal override FrameworkElement BuildControl()
            {
                _tb = new TextBox { Text = Value ?? "", MinWidth = 200 };
                _tb.TextChanged += (s, e) => { UpdateSwatch(); RaiseChanged(); };

                var line = new DockPanel { LastChildFill = true };
                if (IsFolder)
                {
                    var browse = new Button { Content = "Procurar…", Margin = new Thickness(6, 0, 0, 0) };
                    browse.SetResourceReference(FrameworkElement.StyleProperty, "Ja.Secondary");
                    browse.Padding = new Thickness(10, 4, 10, 4);
                    browse.Click += (s, e) =>
                    {
                        string picked = PickFolder(_tb.Text, Window.GetWindow(_tb));
                        if (picked != null) _tb.Text = picked;
                    };
                    DockPanel.SetDock(browse, Dock.Right);
                    line.Children.Add(browse);
                }
                if (IsHexColor)
                {
                    _swatch = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Margin = new Thickness(6, 0, 0, 0) };
                    _swatch.SetResourceReference(Border.BorderBrushProperty, "Ja.Line");
                    DockPanel.SetDock(_swatch, Dock.Right);
                    line.Children.Add(_swatch);
                    UpdateSwatch();
                }
                line.Children.Add(_tb);

                // {variáveis}: clickable chips that insert the token at the caret.
                var tokens = Tokens(Label + " " + (Dialog?.HelpOf(this) ?? ""));
                if (tokens.Count == 0) return line;
                var panel = new StackPanel();
                panel.Children.Add(line);
                var chips = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
                var lead = new TextBlock { Text = "Inserir:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 4), FontSize = 11 };
                lead.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Muted");
                chips.Children.Add(lead);
                foreach (string token in tokens)
                {
                    var chip = new Button { Content = token, FontSize = 11, Margin = new Thickness(0, 0, 4, 4), Focusable = false, ToolTip = "Inserir " + token + " no nome" };
                    chip.SetResourceReference(FrameworkElement.StyleProperty, "Ja.Secondary");
                    chip.Padding = new Thickness(7, 1, 7, 2);
                    chip.Click += (s, e) =>
                    {
                        int at = _tb.IsKeyboardFocusWithin || _tb.CaretIndex > 0 ? _tb.CaretIndex : _tb.Text.Length;
                        _tb.Text = _tb.Text.Insert(at, token);
                        _tb.Focus();
                        _tb.CaretIndex = at + token.Length;
                    };
                    chips.Children.Add(chip);
                }
                panel.Children.Add(chips);
                return panel;
            }

            private void UpdateSwatch()
            {
                if (_swatch == null) return;
                string hex = (_tb.Text ?? "").Trim().TrimStart('#');
                try
                {
                    if (hex.Length == 6)
                    {
                        var c = (Color)ColorConverter.ConvertFromString("#" + hex);
                        _swatch.Background = new SolidColorBrush(c);
                        _swatch.ToolTip = "#" + hex.ToUpperInvariant();
                        return;
                    }
                }
                catch { }
                _swatch.Background = new SolidColorBrush(Colors.Transparent);
                _swatch.ToolTip = "Cor inválida";
            }

            internal override string Validate()
            {
                string text = _tb.Text ?? "";
                if (IsHexColor && !Regex.IsMatch(text.Trim().TrimStart('#'), "^[0-9A-Fa-f]{6}$"))
                    return "Use 6 dígitos hexadecimais, ex.: 1F7A5A.";
                if (IsFolder && text.Trim().Length == 0) return "Escolha a pasta de destino.";
                Value = IsHexColor ? text.Trim().TrimStart('#') : text;
                return null;
            }
            internal override void Load(string s) { if (s != null) Value = s; }
            internal override string Store() => Value ?? "";
            internal override void FocusControl() { _tb?.Focus(); _tb?.SelectAll(); }
        }

        public sealed class MultiField : Field
        {
            public IList<string> Items;
            public bool[] Checked;
            public bool Required = true;
            public IEnumerable<int> SelectedIndices => Enumerable.Range(0, Items.Count).Where(i => Checked[i]);

            private readonly List<CheckBox> _boxes = new List<CheckBox>();
            private TextBlock _badge;
            private TextBlock _empty;
            private TextBox _search;
            private int _lastClicked = -1;
            internal override bool FullWidth => true;
            internal override Control MainControl => _search;

            /// <summary>True when the list fills the left column (two-column window).</summary>
            internal bool Fill;

            internal override FrameworkElement BuildControl()
            {
                _boxes.Clear();
                var panel = new DockPanel { LastChildFill = true };

                _search = new TextBox { ToolTip = "Digite parte do nome para filtrar a lista (sem acentos também)" };
                var searchHost = ToolChrome.SearchBox(_search);
                searchHost.Margin = new Thickness(0, 0, 0, 6);
                DockPanel.SetDock(searchHost, Dock.Top);
                panel.Children.Add(searchHost);

                // bulk actions under the list; the range tip lives in their tooltip
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
                actions.Children.Add(LinkButton("Todos", "Marca todos os itens visíveis", () => SetVisible(true)));
                actions.Children.Add(LinkButton("Nenhum", "Desmarca todos os itens visíveis", () => SetVisible(false)));
                actions.Children.Add(LinkButton("Inverter", "Inverte a marcação dos itens visíveis", () =>
                {
                    foreach (var cb in _boxes) if (cb.Visibility == Visibility.Visible) cb.IsChecked = cb.IsChecked != true;
                }));
                if (Items.Count > 6)
                {
                    var hint = new TextBlock { Text = "Shift+clique: intervalo", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
                    hint.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Muted");
                    actions.Children.Add(hint);
                }
                DockPanel.SetDock(actions, Dock.Bottom);
                panel.Children.Add(actions);

                var list = new StackPanel { Margin = new Thickness(6, 4, 6, 4) };
                for (int i = 0; i < Items.Count; i++)
                {
                    int index = i;
                    var cb = new CheckBox { Content = Items[i], IsChecked = Checked[i], Margin = new Thickness(0, 2, 0, 2) };
                    cb.Checked += (s, e) => UpdateBadge();
                    cb.Unchecked += (s, e) => UpdateBadge();
                    // Shift+click marks/unmarks the whole range since the previous click.
                    cb.Click += (s, e) =>
                    {
                        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 && _lastClicked >= 0 && _lastClicked != index)
                        {
                            bool state = cb.IsChecked == true;
                            int a = Math.Min(_lastClicked, index), b = Math.Max(_lastClicked, index);
                            for (int k = a; k <= b; k++)
                                if (_boxes[k].Visibility == Visibility.Visible) _boxes[k].IsChecked = state;
                        }
                        _lastClicked = index;
                    };
                    _boxes.Add(cb);
                    list.Children.Add(cb);
                }
                _empty = new TextBlock { Margin = new Thickness(4, 8, 4, 8), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
                _empty.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Muted");
                list.Children.Add(_empty);
                if (Items.Count == 0)
                {
                    _empty.Text = "Nenhum item disponível neste projeto.";
                    _empty.Visibility = Visibility.Visible;
                }

                var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                if (!Fill) scroll.MaxHeight = 230;
                var frame = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = scroll };
                frame.SetResourceReference(Border.BorderBrushProperty, "Ja.Line");
                frame.SetResourceReference(Border.BackgroundProperty, "Ja.Card");
                panel.Children.Add(frame);

                _search.TextChanged += (s, e) =>
                {
                    string f = Plain(_search.Text);
                    int shown = 0;
                    foreach (var cb in _boxes)
                    {
                        bool show = f.Length == 0 || Plain(cb.Content.ToString()).Contains(f);
                        cb.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                        if (show) shown++;
                    }
                    if (Items.Count > 0)
                    {
                        _empty.Text = $"Nada encontrado para “{_search.Text.Trim()}”.";
                        _empty.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
                    }
                };
                UpdateBadge();
                return panel;
            }

            /// <summary>The "3 de 12" pill shown in the card header.</summary>
            internal FrameworkElement Badge()
            {
                _badge = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold };
                _badge.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Accent");
                var pill = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 1, 8, 2), Child = _badge, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                pill.SetResourceReference(Border.BackgroundProperty, "Ja.AccentSoft");
                UpdateBadge();
                return pill;
            }

            private void UpdateBadge()
            {
                if (_badge == null) return;
                int n = _boxes.Count == 0 ? Checked.Count(c => c) : _boxes.Count(b => b.IsChecked == true);
                _badge.Text = $"{n} de {Items.Count}";
                RaiseChanged();
            }

            private void SetVisible(bool state)
            {
                foreach (var cb in _boxes) if (cb.Visibility == Visibility.Visible) cb.IsChecked = state;
            }

            internal override string Validate()
            {
                for (int i = 0; i < _boxes.Count; i++) Checked[i] = _boxes[i].IsChecked == true;
                if (Required && !Checked.Any(c => c)) return "Marque pelo menos um item da lista.";
                return null;
            }

            internal override void Load(string s)
            {
                if (s == null) return;
                var set = new HashSet<string>(s.Split('\u001f'));
                for (int i = 0; i < Items.Count; i++) Checked[i] = set.Contains(Items[i]);
            }

            internal override string Store() => string.Join("\u001f", Enumerable.Range(0, Items.Count).Where(i => Checked[i]).Select(i => Items[i]));
            internal override void FocusControl() { _search?.Focus(); }
        }

        private sealed class NoteField : Field
        {
            internal override bool FullWidth => true;
            internal override FrameworkElement BuildControl()
            {
                var t = new TextBlock { Text = Label, TextWrapping = TextWrapping.Wrap, FontSize = 11.5 };
                t.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Muted");
                var bar = new Border { BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 2, 0, 2), Child = t };
                bar.SetResourceReference(Border.BorderBrushProperty, "Ja.Line");
                return bar;
            }
            internal override string Validate() => null;
            internal override void Load(string s) { }
            internal override string Store() => null;
        }

        // =====================================================================================
        // dialog
        // =====================================================================================
        private const string DefaultSection = "Opções";
        private const string ListCardPrefix = "#lista#";
        private readonly string _title;
        private readonly string _prefsKey;
        private readonly string _verb;
        private readonly ToolInfo _info;
        private readonly List<Field> _fields = new List<Field>();
        private readonly List<string> _nextSteps = new List<string>();
        private string _section;

        private Window _win;
        private Grid _body;
        private bool _twoColumns;
        private Grid _left;
        private readonly Dictionary<Field, Border> _cardOf = new Dictionary<Field, Border>();
        private TextBlock _helpText;
        private TextBlock _footerError;

        /// <param name="verb">Text of the main button; default: the tool's verb in ToolCatalog.</param>
        public OptionsDialog(string title, string prefsKey, string verb = null)
        {
            _title = title;
            _prefsKey = prefsKey;
            _info = ToolCatalog.Get(title);
            _verb = verb ?? _info.Verb ?? "OK";
        }

        /// <summary>Starts a new card; the next options go into it.</summary>
        public void Section(string title) { _section = title; }

        public BoolField Check(string key, string label, bool def, string help = null) =>
            Add(new BoolField { Key = key, Label = label, Value = def, Help = help });

        public ChoiceField Choice(string key, string label, IList<string> items, int defaultIndex = 0, string help = null) =>
            Add(new ChoiceField
            {
                Key = key, Label = label, Help = help, Items = items ?? new List<string>(),
                Index = items != null && items.Count > 0 ? Math.Max(0, Math.Min(defaultIndex, items.Count - 1)) : -1
            });

        public NumberField Number(string key, string label, double def, double min = double.MinValue, double max = double.MaxValue, string help = null) =>
            Add(new NumberField { Key = key, Label = label, Value = def, Min = min, Max = max, Help = help });

        public TextField Text(string key, string label, string def, string help = null) =>
            Add(new TextField { Key = key, Label = label, Value = def, Help = help, Dialog = this });

        public MultiField Multi(string key, string label, IList<string> items, bool defaultChecked = true, bool required = true, string help = null) =>
            Add(new MultiField
            {
                Key = key, Label = label, Help = help, Items = items ?? new List<string>(),
                Checked = Enumerable.Repeat(defaultChecked, items?.Count ?? 0).ToArray(), Required = required
            });

        /// <summary>
        /// Explanatory text. A note that tells what happens after the dialog ("Depois do OK: …")
        /// becomes the highlighted "Próximo passo" box next to the main button.
        /// </summary>
        public void Note(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var m = Regex.Match(text, @"^\s*Depois do OK[:,]?\s*", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                string rest = text.Substring(m.Length);
                if (rest.Length > 0) rest = char.ToUpper(rest[0]) + rest.Substring(1);
                _nextSteps.Add(rest);
                return;
            }
            _fields.Add(new NoteField { Label = text, Section = _section });
        }

        private T Add<T>(T f) where T : Field
        {
            f.Section = _section;
            f.Default = f.Store();
            f.Load(ToolPrefs.Get(_prefsKey + "." + f.Key));
            _fields.Add(f);
            return f;
        }

        /// <summary>Unified selection (Central JA scope): lists keep only the items named in the scope.</summary>
        private void AplicarEscopo()
        {
            if (Automacao.NomesEscopo == null || Automacao.NomesEscopo.Count == 0) return;
            foreach (MultiField m in _fields.OfType<MultiField>())
            {
                var marcar = new bool[m.Items.Count];
                bool algum = false;
                for (int i = 0; i < m.Items.Count; i++)
                    if (Automacao.CorrespondeEscopo(m.Items[i])) { marcar[i] = true; algum = true; }
                if (algum) m.Checked = marcar;
            }
        }

        /// <summary>"Salvar como tarefa": the current values become a Central JA task, without running the tool.</summary>
        private void SalvarComoTarefa()
        {
            string nome = Automacao.PerguntarNome(_win, "Salvar como tarefa", "Nome da tarefa (ex.: “Plantas de forro 1:50”):", _title);
            if (string.IsNullOrWhiteSpace(nome)) return;
            var valores = new Dictionary<string, string>();
            foreach (var f in _fields)
            {
                string v = f.Store();
                if (v != null && f.Key != null) valores[_prefsKey + "." + f.Key] = v;
            }
            Automacao.NovaTarefa(nome.Trim(), _title, valores, false);
            System.Windows.MessageBox.Show(_win, $"Tarefa “{nome.Trim()}” salva. Rode-a ou junte-a a um pacote na Central JA.",
                ToolCatalog.Marca(_title) + " — " + _title, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }

        internal string HelpOf(Field f) => f.Help ?? ToolCatalog.FieldHelp(_info, f.Key);

        // -------------------------------------------------------------------------------------
        /// <summary>Shows the dialog; false = cancelled. Values are remembered for next time.</summary>
        public bool Show(IntPtr owner)
        {
            Automacao.DialogoVisto = true;
            // task / bundle: no window, the values come from the task (already loaded from the preferences)
            if (Automacao.Ativa && Automacao.SemJanela)
            {
                AplicarEscopo();
                if (_fields.OfType<MultiField>().Any(m => m.Required && m.Items.Count > 0 && !m.Checked.Any(c => c)))
                {
                    Automacao.Capturar(_title, "Nenhum item da lista corresponde ao escopo/tarefa.", ResultKind.Warning);
                    return false;
                }
                ToolProgress.Arm();
                return true;
            }
            ToolProgress.Hide();
            // a list next to options: two columns (list on the left, filling the height)
            _twoColumns = _fields.Any(f => f is MultiField && f.Section == null) &&
                          _fields.Any(f => !(f is MultiField && f.Section == null));
            _win = new Window
            {
                Width = ReadDouble(_twoColumns ? "__w2" : "__w", _twoColumns ? 900 : 580),
                MinWidth = _twoColumns ? 760 : 460,
                MaxHeight = SystemParameters.WorkArea.Height * 0.94,
                WindowStartupLocation = owner != IntPtr.Zero ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.CanResizeWithGrip,
                ShowInTaskbar = false
            };
            if (_twoColumns) { _win.Height = Math.Min(ReadHeight(640), _win.MaxHeight); _win.MinHeight = 480; }
            else _win.SizeToContent = SizeToContent.Height;
            if (owner != IntPtr.Zero) new WindowInteropHelper(_win).Owner = owner;

            _body = new Grid();
            BuildBody();
            var footer = BuildFooter(out Button primary);
            ToolChrome.DressCompact(_win, _title, _body, footer);

            primary.Click += (s, e) => { if (ValidateAll()) _win.DialogResult = true; };
            _win.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
                {
                    if (ValidateAll()) _win.DialogResult = true;
                    e.Handled = true;
                }
            };
            _win.Loaded += (s, e) =>
            {
                var first = _fields.FirstOrDefault(f => f.MainControl != null && !(f is MultiField) && f.IsShown);
                (first ?? _fields.FirstOrDefault(f => f.MainControl != null))?.FocusControl();
            };
            _win.Closing += (s, e) =>
            {
                ToolPrefs.Set(_prefsKey + (_twoColumns ? ".__w2" : ".__w"), _win.ActualWidth.ToString("0", CultureInfo.InvariantCulture));
                if (_twoColumns) ToolPrefs.Set(_prefsKey + ".__h", _win.ActualHeight.ToString("0", CultureInfo.InvariantCulture));
            };

            var relogio = System.Diagnostics.Stopwatch.StartNew();
            bool ok = _win.ShowDialog() == true;
            RegistrarUso(ok, relogio.Elapsed.TotalMilliseconds);
            // Central JA "configurar tarefa": the values go into the task, the tool does not run
            if (Automacao.EditandoTarefa != null)
            {
                if (ok)
                {
                    var valores = new Dictionary<string, string>();
                    foreach (var f in _fields)
                    {
                        string v = f.Store();
                        if (v != null && f.Key != null) valores[_prefsKey + "." + f.Key] = v;
                    }
                    Automacao.EditandoTarefa.Valores = valores;
                    Automacao.EditandoTarefa.Interativa = false;
                    Automacao.TarefaSalva = true;
                }
                ToolPrefs.Save();
                return false;
            }
            if (ok)
                foreach (var f in _fields)
                {
                    string v = f.Store();
                    if (v != null && f.Key != null) ToolPrefs.Set(_prefsKey + "." + f.Key, v);
                }
            ToolPrefs.Save();
            if (ok) ToolProgress.Arm();   // the "carregando" box opens while the tool works
            return ok;
        }

        /// <summary>Telemetria: the values chosen (lists as "marcados/total") and the time spent in the window.</summary>
        private void RegistrarUso(bool ok, double ms)
        {
            try
            {
                if (Automacao.EditandoTarefa != null) return;
                var valores = new Dictionary<string, string>();
                if (ok)
                    foreach (var f in _fields)
                    {
                        if (f.Key == null || !f.IsShown) continue;
                        if (f is MultiField mf) { valores[f.Key] = mf.Checked.Count(x => x) + "/" + mf.Items.Count; continue; }
                        string v = f.Store();
                        if (v != null) valores[f.Key] = v;
                    }
                RevitDwgUnifier.Infrastructure.Telemetria.Dialogo(_title, _prefsKey, valores, ok, ms);
            }
            catch { }
        }

        // ---------------------------------------------------------------- body (cards)
        private void BuildBody()
        {
            _body.Children.Clear();
            _body.ColumnDefinitions.Clear();
            _cardOf.Clear();

            // Cards in order of first appearance. Lists without an explicit section get their
            // own card (title = list name + counter); everything else goes to "Opções".
            var cards = new List<KeyValuePair<string, List<Field>>>();
            foreach (var f in _fields)
            {
                string key = f.Section ?? (f is MultiField ? ListCardPrefix + f.Key : DefaultSection);
                var card = cards.FirstOrDefault(c => c.Key == key);
                if (card.Value == null)
                {
                    card = new KeyValuePair<string, List<Field>>(key, new List<Field>());
                    cards.Add(card);
                }
                card.Value.Add(f);
            }

            var left = new Grid();          // list cards, sharing the height
            _left = left;
            var right = new StackPanel();   // option cards
            bool onlyDefault = cards.Count(c => !IsListCard(c.Key)) == 1 && cards.Any(c => c.Key == DefaultSection);
            foreach (var card in cards)
            {
                bool listCard = IsListCard(card.Key);
                var multiOnly = listCard ? card.Value[0] as MultiField : null;
                if (multiOnly != null) multiOnly.Fill = _twoColumns;

                var content = new StackPanel();
                FrameworkElement inner = content;
                if (multiOnly != null)
                {
                    var row = BuildRow(multiOnly, true);
                    inner = row;
                }
                else
                    for (int i = 0; i < card.Value.Count; i++)
                    {
                        var f = card.Value[i];
                        var row = BuildRow(f, false);
                        if (i > 0) row.Margin = new Thickness(0, f is BoolField && card.Value[i - 1] is BoolField ? 2 : 10, 0, 0);
                        content.Children.Add(row);
                    }

                string title = multiOnly != null ? multiOnly.Label : onlyDefault || card.Key == DefaultSection && cards.Count == 1 ? null : card.Key;
                var border = ToolChrome.Card(inner, title, multiOnly?.Badge());
                foreach (var f in card.Value) _cardOf[f] = border;

                if (_twoColumns && listCard)
                {
                    left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    Grid.SetRow(border, left.RowDefinitions.Count - 1);
                    left.Children.Add(border);
                }
                else right.Children.Add(border);
            }

            if (_twoColumns)
            {
                _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 300 });
                _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
                _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star), MinWidth = 380 });
                _body.Children.Add(left);
                var sv = new ScrollViewer { Content = right, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                Grid.SetColumn(sv, 2);
                _body.Children.Add(sv);
            }
            else
                _body.Children.Add(new ScrollViewer { Content = right, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

            // live enable / disable / show
            foreach (var f in _fields) f.Changed = RefreshEnabled;
            RefreshEnabled();
        }

        private static bool IsListCard(string key) => key.StartsWith(ListCardPrefix, StringComparison.Ordinal);

        private FrameworkElement BuildRow(Field f, bool labelInHeader)
        {
            var control = f.BuildControl();
            f.ErrorText = new TextBlock { Visibility = Visibility.Collapsed, FontSize = 11.5, Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap };
            f.ErrorText.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Error");

            FrameworkElement root;
            if (f is MultiField && labelInHeader)
            {
                var dock = new DockPanel();
                DockPanel.SetDock(f.ErrorText, Dock.Bottom);
                dock.Children.Add(f.ErrorText);
                dock.Children.Add(control);
                root = dock;
            }
            else if (f.FullWidth)
            {
                var stack = new StackPanel();
                if (f is MultiField)
                {
                    var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
                    var t = new TextBlock { Text = f.Label, FontWeight = FontWeights.SemiBold };
                    t.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Ink");
                    head.Children.Add(t);
                    var badge = ((MultiField)f).Badge();
                    badge.Margin = new Thickness(8, 0, 0, 0);
                    head.Children.Add(badge);
                    stack.Children.Add(head);
                }
                stack.Children.Add(control);
                stack.Children.Add(f.ErrorText);
                root = stack;
            }
            else
            {
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_twoColumns ? 150 : 175) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                SplitUnit(f.Label, out string clean, out string _, out string __);
                if (f is TextField) clean = CleanTokenLabel(clean);
                var label = new TextBlock { Text = clean, Margin = new Thickness(0, 5, 12, 0), FontSize = 12 };
                label.SetResourceReference(FrameworkElement.StyleProperty, "Ja.Label");
                label.VerticalAlignment = VerticalAlignment.Top;
                grid.Children.Add(label);
                var right = new StackPanel();
                right.Children.Add(control);
                right.Children.Add(f.ErrorText);
                Grid.SetColumn(right, 1);
                grid.Children.Add(right);
                root = grid;
            }

            f.Root = root;
            string help = HelpOf(f);
            if (!string.IsNullOrEmpty(help)) root.ToolTip = help;
            root.MouseEnter += (s, e) => ShowHelp(f);
            root.MouseLeave += (s, e) => ShowHelp(null);
            root.IsKeyboardFocusWithinChanged += (s, e) => { if ((bool)e.NewValue) ShowHelp(f); };
            return root;
        }

        private void RefreshEnabled()
        {
            foreach (var f in _fields)
            {
                if (f.Root == null) continue;
                f.Root.Visibility = f.IsShown ? Visibility.Visible : Visibility.Collapsed;
                if (f.EnabledRule != null)
                {
                    bool on = f.EnabledRule();
                    f.Root.IsEnabled = on;
                    f.Root.Opacity = on ? 1 : 0.45;
                }
            }
            // a card whose options are all hidden goes away
            foreach (var card in _cardOf.GroupBy(kv => kv.Value))
            {
                var real = card.Where(kv => !(kv.Key is NoteField)).ToList();
                card.Key.Visibility = real.Count == 0 || real.Any(kv => kv.Key.IsShown) ? Visibility.Visible : Visibility.Collapsed;
            }
            // hidden list cards give their height to the visible ones
            if (_twoColumns && _left != null)
                foreach (UIElement c in _left.Children)
                {
                    int r = Grid.GetRow(c);
                    if (r < _left.RowDefinitions.Count)
                        _left.RowDefinitions[r].Height = c.Visibility == Visibility.Visible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
                }
        }

        // ---------------------------------------------------------------- help line (footer)
        private void ShowHelp(Field f)
        {
            if (_helpText == null) return;
            _helpText.Inlines.Clear();
            string help = f != null ? HelpOf(f) : null;
            if (f != null && !string.IsNullOrEmpty(help))
            {
                SplitUnit(f.Label, out string clean, out string _, out string __);
                _helpText.Inlines.Add(new System.Windows.Documents.Run(CleanTokenLabel(clean) + ": ") { FontWeight = FontWeights.SemiBold });
                _helpText.Inlines.Add(new System.Windows.Documents.Run(help));
                _helpText.ToolTip = help;
                return;
            }
            string tip = _info.Tips != null && _info.Tips.Length > 0 ? _info.Tips[0] : null;
            _helpText.Inlines.Add(new System.Windows.Documents.Run(tip ?? "Passe o mouse sobre uma opção para ver o que ela faz. F1: ajuda."));
            _helpText.ToolTip = _info.Tips != null && _info.Tips.Length > 0 ? string.Join("\n\n", _info.Tips) : null;
        }

        // ---------------------------------------------------------------- footer
        private FrameworkElement BuildFooter(out Button primary)
        {
            var stack = new StackPanel();

            if (_nextSteps.Count > 0)
            {
                var box = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 10) };
                box.SetResourceReference(Border.BackgroundProperty, "Ja.AccentSoft");
                var inner = new StackPanel();
                var cap = new TextBlock { Text = "Depois de “" + _verb + "”", FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2) };
                cap.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Accent");
                inner.Children.Add(cap);
                foreach (string n in _nextSteps)
                {
                    var t = new TextBlock { Text = n, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
                    t.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Ink");
                    inner.Children.Add(t);
                }
                box.Child = inner;
                stack.Children.Add(box);
            }

            var bar = new DockPanel();
            var right = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0) };
            DockPanel.SetDock(right, Dock.Right);
            if (_fields.Any(f => f.Key != null && !(f is NoteField)))
            {
                var reset = ToolChrome.Ghost("↺", "Restaurar padrões: volta todas as opções aos valores de fábrica");
                reset.Margin = new Thickness(0, 0, 4, 0);
                reset.Click += (s, e) =>
                {
                    foreach (var f in _fields) if (f.Default != null) f.Load(f.Default);
                    BuildBody();
                    _footerError.Visibility = Visibility.Collapsed;
                    _helpText.Visibility = Visibility.Visible;
                };
                right.Children.Add(reset);
                if (Automacao.EditandoTarefa == null)
                {
                    var tarefa = ToolChrome.Ghost("☆ Tarefa", "Salvar estas opções como tarefa da Central JA (sem rodar agora). Tarefas rodam com 1 clique e entram em pacotes.");
                    tarefa.Margin = new Thickness(0, 0, 4, 0);
                    tarefa.Click += (s, e) => { if (ValidateAll()) SalvarComoTarefa(); };
                    right.Children.Add(tarefa);
                }
            }
            var cancel = ToolChrome.Ghost("Cancelar");
            cancel.IsCancel = true; cancel.MinWidth = 88; cancel.Margin = new Thickness(0, 0, 8, 0);
            primary = ToolChrome.Primary(Automacao.EditandoTarefa != null ? "Salvar tarefa" : _verb);
            primary.IsDefault = true; primary.MinWidth = 150; primary.ToolTip = "Enter ou Ctrl+Enter";
            right.Children.Add(cancel);
            right.Children.Add(primary);
            bar.Children.Add(right);

            var left = new Grid { VerticalAlignment = VerticalAlignment.Center };
            _helpText = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, MaxHeight = 34, TextTrimming = TextTrimming.CharacterEllipsis };
            _helpText.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Muted");
            left.Children.Add(_helpText);
            _footerError = new TextBlock { Visibility = Visibility.Collapsed, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeights.SemiBold };
            _footerError.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Error");
            left.Children.Add(_footerError);
            bar.Children.Add(left);
            stack.Children.Add(bar);
            ShowHelp(null);
            return stack;
        }

        // ---------------------------------------------------------------- validation
        private bool ValidateAll()
        {
            Field firstBad = null;
            int bad = 0;
            foreach (var f in _fields)
            {
                string error = f.Validate();
                bool enabled = (f.EnabledRule == null || f.EnabledRule()) && f.IsShown;
                if (!enabled) error = null; // a greyed-out or hidden option never blocks the user
                if (f.ErrorText != null)
                {
                    f.ErrorText.Text = error ?? "";
                    f.ErrorText.Visibility = error == null ? Visibility.Collapsed : Visibility.Visible;
                }
                var c = f.MainControl;
                if (c is TextBox || c is ComboBox)
                {
                    if (error != null) c.SetResourceReference(Control.BorderBrushProperty, "Ja.Error");
                    else c.SetResourceReference(Control.BorderBrushProperty, "Ja.InputLine");
                }
                if (error != null) { bad++; if (firstBad == null) firstBad = f; }
            }
            if (firstBad == null)
            {
                _footerError.Visibility = Visibility.Collapsed;
                _helpText.Visibility = Visibility.Visible;
                return true;
            }
            _helpText.Visibility = Visibility.Collapsed;
            _footerError.Text = bad == 1 ? "Corrija a opção destacada." : $"Corrija as {bad} opções destacadas.";
            _footerError.Visibility = Visibility.Visible;
            firstBad.Root?.BringIntoView();
            firstBad.FocusControl();
            return false;
        }

        // ---------------------------------------------------------------- helpers
        private double ReadDouble(string suffix, double fallback)
        {
            return TryParse(ToolPrefs.Get(_prefsKey + "." + suffix), out double v) && v >= 420 && v <= SystemParameters.WorkArea.Width
                ? v : fallback;
        }

        private double ReadHeight(double fallback) =>
            TryParse(ToolPrefs.Get(_prefsKey + ".__h"), out double v) && v >= 420 && v <= SystemParameters.WorkArea.Height ? v : fallback;

        /// <summary>Lower case, no accents: "Elevação" matches "elevacao".</summary>
        internal static string Plain(string text) => TextUtil.Fold(text).Trim();

        private static Button LinkButton(string text, string tip, Action click)
        {
            var b = new Button { Content = text, ToolTip = tip, FontSize = 11.5 };
            b.SetResourceReference(FrameworkElement.StyleProperty, "Ja.Ghost");
            b.Padding = new Thickness(6, 3, 6, 3);
            b.Click += (s, e) => click();
            return b;
        }

        /// <summary>"Altura (m)" → "Altura" + suffix "m"; "Escala 1:" → "Escala" + prefix "1 :".</summary>
        private static void SplitUnit(string label, out string clean, out string prefix, out string suffix)
        {
            clean = label ?? "";
            prefix = null;
            suffix = null;
            var m = Regex.Match(clean, @"\s*\((m|cm|mm|pixels|px|%)\)\s*$");
            if (m.Success)
            {
                suffix = m.Groups[1].Value == "pixels" ? "px" : m.Groups[1].Value;
                clean = clean.Substring(0, m.Index);
                return;
            }
            var s = Regex.Match(clean, @"\s*1\s*:\s*$");
            if (s.Success)
            {
                prefix = "1 :";
                clean = clean.Substring(0, s.Index);
            }
        }

        /// <summary>"Nome ({ambiente}, {letra})" → "Nome" (the tokens become chips).</summary>
        private static string CleanTokenLabel(string label)
        {
            return Regex.Replace(label ?? "", @"\s*\([^)]*\{[^)]*\)\s*$", "");
        }

        private static List<string> Tokens(string text)
        {
            return Regex.Matches(text ?? "", @"\{[a-zA-ZçÇãõáéíóúâêô_]+\}").Cast<Match>().Select(m => m.Value).Distinct().ToList();
        }

        private static string Format(double v) => v.ToString("0.###", CultureInfo.CurrentCulture);

        internal static string PickFolder(string current, Window owner)
        {
            string start = System.IO.Directory.Exists(current) ? current : null;
#if NET8_0_OR_GREATER
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Escolha a pasta de destino" };
            if (start != null) dlg.InitialDirectory = start;
            return dlg.ShowDialog(owner) == true ? dlg.FolderName : null;
#else
            // .NET 4.8 has no WPF folder picker: a save dialog with a fixed file name picks the folder.
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Escolha a pasta de destino (abra a pasta e clique em Salvar)",
                FileName = "Selecionar esta pasta",
                Filter = "Pasta|*.",
                InitialDirectory = start ?? "",
                OverwritePrompt = false,
                CheckPathExists = true
            };
            return dlg.ShowDialog(owner) == true ? System.IO.Path.GetDirectoryName(dlg.FileName) : null;
#endif
        }

        public static bool TryParse(string text, out double value) =>
            double.TryParse((text ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
