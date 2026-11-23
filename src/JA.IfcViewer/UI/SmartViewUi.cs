using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using JA.IfcViewer.Ifc;
using JA.IfcViewer.SmartViews;

namespace JA.IfcViewer.UI
{
    /// <summary>Smart Views list: examples and the user's own (saved in %AppData%), apply with a click.</summary>
    public sealed class SmartViewPanel : DockPanel
    {
        private readonly ListBox _list = new ListBox { BorderBrush = Ui.Line };
        private readonly TextBlock _desc = new TextBlock { Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 6, 0, 0) };
        private readonly List<SmartView> _examples = SmartViewEngine.Examples();
        private List<SmartView> _mine = SmartViewEngine.LoadUser();
        private bool _quiet;

        public SmartView Active { get; private set; }
        public event Action<SmartView> Applied;
        public Func<List<string>> Keys;
        public Func<string, List<string>> Values;
        public Func<SmartView, int> Preview;      // applies temporarily, returns visible count
        public Func<SvRule, int> CountRule;

        public SmartViewPanel()
        {
            Margin = new Thickness(6);
            var top = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            top.Children.Add(Ui.Primary("Nova", New));
            top.Children.Add(Ui.Small("Editar", Edit));
            top.Children.Add(Ui.Small("Duplicar", Duplicate));
            top.Children.Add(Ui.Small("Excluir", Delete));
            top.Children.Add(Ui.Small("Desligar", () => Apply(null), "Volta à vista normal (cores do IFC)"));
            top.Children.Add(Ui.Small("Importar…", Import));
            top.Children.Add(Ui.Small("Exportar…", Export));
            DockPanel.SetDock(top, Dock.Top);
            Children.Add(top);
            DockPanel.SetDock(_desc, Dock.Bottom);
            Children.Add(_desc);
            Children.Add(_list);
            _list.SelectionChanged += (s, e) =>
            {
                if (_quiet) return;
                if (_list.SelectedItem is ListBoxItem li && li.Tag is SmartView sv) Apply(sv);
            };
            _list.MouseDoubleClick += (s, e) => { if (Current != null) Edit(); };
            Fill();
        }

        private SmartView Current => (_list.SelectedItem as ListBoxItem)?.Tag as SmartView;

        private void Fill(SmartView select = null)
        {
            _quiet = true;
            try
            {
                _list.Items.Clear();
                foreach (var g in _mine.Concat(_examples).GroupBy(v => v.Folder).OrderBy(x => x.First().BuiltIn ? 1 : 0).ThenBy(x => x.Key))
                {
                    _list.Items.Add(new ListBoxItem
                    {
                        Content = new TextBlock { Text = g.Key, FontWeight = FontWeights.SemiBold, Foreground = Ui.Muted, Margin = new Thickness(0, 6, 0, 2) },
                        IsEnabled = false, Focusable = false
                    });
                    foreach (SmartView v in g.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
                    {
                        var li = new ListBoxItem { Content = new TextBlock { Text = v.Name, Margin = new Thickness(8, 1, 0, 1) }, Tag = v, ToolTip = Summary(v) };
                        _list.Items.Add(li);
                        if (v == (select ?? Active)) li.IsSelected = true;
                    }
                }
            }
            finally { _quiet = false; }
        }

        private static string Summary(SmartView v)
        {
            var s = (string.IsNullOrWhiteSpace(v.Description) ? "" : v.Description + "\n") + (v.StartHidden ? "Começa com tudo oculto.\n" : "");
            return s + string.Join("\n", v.Rules.Select((r, i) => (i + 1) + ". " + r));
        }

        public void Apply(SmartView sv)
        {
            Active = sv;
            _desc.Text = sv == null ? "Vista normal: cores do IFC. Clique numa Smart View para aplicar." : Summary(sv);
            if (sv == null) { _quiet = true; _list.SelectedItem = null; _quiet = false; }
            Applied?.Invoke(sv);
        }

        private void Save() => SmartViewEngine.SaveUser(_mine);

        private void New()
        {
            var sv = new SmartView { Name = "Nova Smart View", Folder = "Minhas" };
            sv.Rules.Add(new SvRule { Action = Actions.ColorByValue, ColorBy = PropertyReader.KClass });
            if (!Editor(sv)) return;
            _mine.Add(sv); Save(); Fill(sv); Apply(sv);
        }

        /// <summary>A Smart View made from outside (right click on a property): edited, then kept.</summary>
        public void CreateFrom(SmartView sv, bool edit)
        {
            if (edit && !Editor(sv)) return;
            _mine.Add(sv); Save(); Fill(sv); Apply(sv);
        }

        private void Edit()
        {
            SmartView sv = Current ?? Active;
            if (sv == null) return;
            if (sv.BuiltIn)
            {
                var copy = sv.Clone(sv.Name + " (cópia)");
                if (!Editor(copy)) return;
                _mine.Add(copy); Save(); Fill(copy); Apply(copy);
                return;
            }
            var work = sv.Clone();
            work.Folder = sv.Folder;
            if (!Editor(work)) { Apply(sv); return; }
            sv.Name = work.Name; sv.Folder = work.Folder; sv.StartHidden = work.StartHidden; sv.Rules = work.Rules; sv.Description = work.Description;
            Save(); Fill(sv); Apply(sv);
        }

        private void Duplicate()
        {
            SmartView sv = Current ?? Active;
            if (sv == null) return;
            var c = sv.Clone(sv.Name + " (cópia)");
            _mine.Add(c); Save(); Fill(c); Apply(c);
        }

        private void Delete()
        {
            SmartView sv = Current;
            if (sv == null || sv.BuiltIn) { MessageBox.Show("Os exemplos não podem ser excluídos (use Duplicar para criar a sua versão).", "Smart Views"); return; }
            if (MessageBox.Show("Excluir a Smart View “" + sv.Name + "”?", "Smart Views", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _mine.Remove(sv); Save();
            if (Active == sv) Apply(null);
            Fill();
        }

        private void Import()
        {
            var d = new Microsoft.Win32.OpenFileDialog { Filter = "Smart Views do JA (*.json)|*.json", Multiselect = true };
            if (d.ShowDialog() != true) return;
            int n = 0;
            foreach (string f in d.FileNames)
            {
                try { var l = SmartViewEngine.Read(f); foreach (var v in l) { v.BuiltIn = false; _mine.Add(v); n++; } }
                catch (Exception ex) { MessageBox.Show("Não foi possível ler " + System.IO.Path.GetFileName(f) + ":\n" + ex.Message, "Importar"); }
            }
            Save(); Fill();
            MessageBox.Show(n + " Smart View(s) importada(s).", "Importar");
        }

        private void Export()
        {
            var list = _mine.ToList();
            if (list.Count == 0) { MessageBox.Show("Crie ou duplique uma Smart View primeiro: os exemplos já vêm em todo visualizador.", "Exportar"); return; }
            var d = new Microsoft.Win32.SaveFileDialog { Filter = "Smart Views do JA (*.json)|*.json", FileName = "SmartViews JA.json" };
            if (d.ShowDialog() != true) return;
            try { SmartViewEngine.Write(d.FileName, list); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Exportar"); }
        }

        private bool Editor(SmartView sv)
        {
            var w = new SmartViewEditor(sv, Keys, Values, Preview, CountRule, _mine.Select(v => v.Folder).Concat(new[] { "Minhas" }).Distinct().ToList())
            { Owner = Window.GetWindow(this) };
            bool ok = w.ShowDialog() == true;
            if (!ok) Applied?.Invoke(Active);    // undo the preview
            return ok;
        }
    }

    /// <summary>Editor of one Smart View: name, folder, start state and the ordered rules.</summary>
    public sealed class SmartViewEditor : Window
    {
        private readonly SmartView _sv;
        private readonly ListBox _rules = new ListBox { Height = 230, BorderBrush = Ui.Line };
        private readonly Func<List<string>> _keys;
        private readonly Func<string, List<string>> _values;
        private readonly Func<SmartView, int> _preview;
        private readonly Func<SvRule, int> _count;
        private readonly TextBlock _info = new TextBlock { Foreground = Ui.Muted, Margin = new Thickness(0, 4, 0, 0) };

        public SmartViewEditor(SmartView sv, Func<List<string>> keys, Func<string, List<string>> values, Func<SmartView, int> preview, Func<SvRule, int> count, List<string> folders)
        {
            _sv = sv; _keys = keys; _values = values; _preview = preview; _count = count;
            Title = "Smart View";
            Width = 640; SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            Background = Ui.Panel;
            var root = new StackPanel { Margin = new Thickness(14) };
            var name = new TextBox { Text = sv.Name, Padding = new Thickness(3) };
            var folder = new ComboBox { IsEditable = true, Text = sv.Folder };
            foreach (string f in folders) folder.Items.Add(f);
            var desc = new TextBox { Text = sv.Description ?? "", Padding = new Thickness(3) };
            var start = new CheckBox { Content = "Começar com todos os elementos ocultos (as regras “Mostrar” acrescentam)", IsChecked = sv.StartHidden, Margin = new Thickness(0, 6, 0, 0) };
            root.Children.Add(Row("Nome", name));
            root.Children.Add(Row("Pasta", folder));
            root.Children.Add(Row("Descrição", desc));
            root.Children.Add(start);
            root.Children.Add(Ui.Header("Regras (aplicadas de cima para baixo)"));
            root.Children.Add(_rules);
            var rb = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            rb.Children.Add(Ui.Primary("+ Regra", AddRule));
            rb.Children.Add(Ui.Small("Editar", EditRule));
            rb.Children.Add(Ui.Small("Duplicar", () => { if (_rules.SelectedIndex >= 0) { _sv.Rules.Insert(_rules.SelectedIndex + 1, _sv.Rules[_rules.SelectedIndex].Clone()); FillRules(_rules.SelectedIndex + 1); } }));
            rb.Children.Add(Ui.Small("Excluir", () => { int i = _rules.SelectedIndex; if (i >= 0) { _sv.Rules.RemoveAt(i); FillRules(Math.Min(i, _sv.Rules.Count - 1)); } }));
            rb.Children.Add(Ui.Small("▲ Subir", () => Move(-1)));
            rb.Children.Add(Ui.Small("▼ Descer", () => Move(1)));
            root.Children.Add(rb);
            root.Children.Add(Ui.Note("Mostrar / Ocultar decidem o que aparece; Colorir, Colorir por valor (com legenda), Transparente, Opaco e Cor original mudam a aparência. Uma regra sem condições vale para todos. Propriedade “*.Nome” procura em qualquer conjunto (ex.: *.FireRating)."));
            root.Children.Add(_info);
            var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            bottom.Children.Add(Ui.Small("Visualizar", () => { Collect(name, folder, desc, start); int n = _preview?.Invoke(_sv) ?? 0; _info.Text = n + " elemento(s) visível(is) com esta Smart View."; }));
            bottom.Children.Add(Ui.Primary("OK", () =>
            {
                Collect(name, folder, desc, start);
                if (string.IsNullOrWhiteSpace(_sv.Name)) { MessageBox.Show("Dê um nome à Smart View.", Title); return; }
                DialogResult = true;
            }));
            bottom.Children.Add(Ui.Small("Cancelar", () => DialogResult = false));
            root.Children.Add(bottom);
            Content = root;
            _rules.MouseDoubleClick += (s, e) => EditRule();
            FillRules(0);
        }

        private void Collect(TextBox name, ComboBox folder, TextBox desc, CheckBox start)
        {
            _sv.Name = name.Text.Trim();
            _sv.Folder = string.IsNullOrWhiteSpace(folder.Text) ? "Minhas" : folder.Text.Trim();
            _sv.Description = desc.Text.Trim();
            _sv.StartHidden = start.IsChecked == true;
        }

        private static FrameworkElement Row(string label, FrameworkElement c)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Text });
            Grid.SetColumn(c, 1);
            g.Children.Add(c);
            return g;
        }

        private void FillRules(int select)
        {
            _rules.Items.Clear();
            for (int i = 0; i < _sv.Rules.Count; i++)
            {
                SvRule r = _sv.Rules[i];
                var cb = new CheckBox { IsChecked = r.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), ToolTip = "Regra ativa" };
                cb.Click += (s, e) => r.Enabled = cb.IsChecked == true;
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(cb);
                sp.Children.Add(new TextBlock { Text = (i + 1) + ".", Foreground = Ui.Muted, Width = 22, VerticalAlignment = VerticalAlignment.Center });
                if (r.Action == Actions.Color) { var sw = Ui.Swatch(SmartViewEngine.ParseColor(r.Color)); sw.Margin = new Thickness(0, 0, 6, 0); sp.Children.Add(sw); }
                sp.Children.Add(new TextBlock { Text = r.ToString(), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 520 });
                _rules.Items.Add(new ListBoxItem { Content = sp, ToolTip = r.ToString() });
            }
            if (select >= 0 && select < _rules.Items.Count) _rules.SelectedIndex = select;
        }

        private void Move(int d)
        {
            int i = _rules.SelectedIndex, j = i + d;
            if (i < 0 || j < 0 || j >= _sv.Rules.Count) return;
            SvRule t = _sv.Rules[i]; _sv.Rules[i] = _sv.Rules[j]; _sv.Rules[j] = t;
            FillRules(j);
        }

        private void AddRule()
        {
            var r = new SvRule { Action = Actions.Show };
            r.Conditions.Add(new SvCondition());
            var w = new RuleEditor(r, _keys, _values, _count) { Owner = this };
            if (w.ShowDialog() != true) return;
            int at = _rules.SelectedIndex >= 0 ? _rules.SelectedIndex + 1 : _sv.Rules.Count;
            _sv.Rules.Insert(at, r);
            FillRules(at);
        }

        private void EditRule()
        {
            int i = _rules.SelectedIndex;
            if (i < 0) return;
            SvRule copy = _sv.Rules[i].Clone();
            var w = new RuleEditor(copy, _keys, _values, _count) { Owner = this };
            if (w.ShowDialog() != true) return;
            _sv.Rules[i] = copy;
            FillRules(i);
        }
    }

    /// <summary>One rule: conditions (property, condition, value) combined by E / OU, and the action.</summary>
    public sealed class RuleEditor : Window
    {
        private readonly SvRule _r;
        private readonly Func<List<string>> _keys;
        private readonly Func<string, List<string>> _values;
        private readonly Func<SvRule, int> _count;
        private readonly StackPanel _conds = new StackPanel();
        private readonly ComboBox _action = new ComboBox();
        private readonly ComboBox _combine = new ComboBox();
        private readonly ComboBox _colorBy = new ComboBox { IsEditable = true };
        private readonly TextBox _colorText = new TextBox { Width = 80, Padding = new Thickness(3) };
        private readonly Border _colorSwatch = new Border { Width = 26, Height = 20, BorderBrush = Ui.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 6, 0) };
        private readonly FrameworkElement _colorRow, _byRow;
        private readonly TextBlock _info = new TextBlock { Foreground = Ui.Muted, Margin = new Thickness(0, 6, 0, 0) };
        private List<string> _keyCache;

        public RuleEditor(SvRule r, Func<List<string>> keys, Func<string, List<string>> values, Func<SvRule, int> count)
        {
            _r = r; _keys = keys; _values = values; _count = count;
            Title = "Regra da Smart View";
            Width = 760; SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Ui.Panel;
            var root = new StackPanel { Margin = new Thickness(14) };

            foreach (string a in Actions.All) _action.Items.Add(a);
            _action.SelectedItem = r.Action;
            if (_action.SelectedIndex < 0) _action.SelectedIndex = 0;
            root.Children.Add(Line("Ação", _action, 220));

            // colour
            var palette = new WrapPanel { Margin = new Thickness(6, 0, 0, 0) };
            for (int k = 0; k < 20; k++)
            {
                uint c = SmartViewEngine.Palette(k);
                var b = new Button { Width = 18, Height = 18, Margin = new Thickness(1), Background = Ui.F(c), BorderBrush = Ui.Line, ToolTip = SmartViewEngine.ColorText(c), Focusable = false };
                b.Click += (s, e) => { _colorText.Text = SmartViewEngine.ColorText(c); };
                palette.Children.Add(b);
            }
            _colorText.Text = r.Color ?? "#E8A33D";
            _colorText.TextChanged += (s, e) => _colorSwatch.Background = Ui.F(SmartViewEngine.ParseColor(_colorText.Text));
            _colorSwatch.Background = Ui.F(SmartViewEngine.ParseColor(_colorText.Text));
            var crow = new StackPanel { Orientation = Orientation.Horizontal };
            crow.Children.Add(_colorSwatch); crow.Children.Add(_colorText); crow.Children.Add(palette);
            _colorRow = Line("Cor", crow, 0);
            root.Children.Add(_colorRow);
            _colorBy.Text = r.ColorBy ?? PropertyReader.KClass;
            _colorBy.DropDownOpened += (s, e) => FillKeys(_colorBy);
            _byRow = Line("Propriedade", _colorBy, 420);
            root.Children.Add(_byRow);
            _action.SelectionChanged += (s, e) => UpdateRows();

            root.Children.Add(Ui.Header("Elementos"));
            _combine.Items.Add("que atendem a TODAS as condições (E)");
            _combine.Items.Add("que atendem a QUALQUER condição (OU)");
            _combine.SelectedIndex = r.Any ? 1 : 0;
            root.Children.Add(Line("", _combine, 300));
            root.Children.Add(_conds);
            var add = Ui.Small("+ Condição", () => { var c = new SvCondition(); _r.Conditions.Add(c); _conds.Children.Add(CondRow(c)); });
            add.HorizontalAlignment = HorizontalAlignment.Left;
            root.Children.Add(add);
            root.Children.Add(Ui.Note("Sem condições, a regra vale para todos os elementos. Números aceitam vírgula ou ponto (valores na unidade do IFC). “Classe IFC é igual a IfcWall” inclui IfcWallStandardCase."));
            root.Children.Add(_info);

            var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            bottom.Children.Add(Ui.Small("Contar", () => { Collect(); int n = _count?.Invoke(_r) ?? 0; _info.Text = n + " elemento(s) atendem às condições."; }));
            bottom.Children.Add(Ui.Primary("OK", () => { Collect(); DialogResult = true; }));
            bottom.Children.Add(Ui.Small("Cancelar", () => DialogResult = false));
            root.Children.Add(bottom);
            Content = root;
            foreach (SvCondition c in r.Conditions) _conds.Children.Add(CondRow(c));
            UpdateRows();
        }

        private static FrameworkElement Line(string label, FrameworkElement c, double width)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Text });
            if (width > 0) { c.Width = width; c.HorizontalAlignment = HorizontalAlignment.Left; }
            Grid.SetColumn(c, 1);
            g.Children.Add(c);
            return g;
        }

        private void UpdateRows()
        {
            string a = _action.SelectedItem as string;
            _colorRow.Visibility = a == Actions.Color ? Visibility.Visible : Visibility.Collapsed;
            _byRow.Visibility = a == Actions.ColorByValue ? Visibility.Visible : Visibility.Collapsed;
        }

        private void FillKeys(ComboBox cb)
        {
            if (cb.Items.Count > 0) return;
            string text = cb.Text;
            if (_keyCache == null) _keyCache = _keys?.Invoke() ?? new List<string>();
            foreach (string k in _keyCache) cb.Items.Add(k);
            cb.Text = text;
        }

        private FrameworkElement CondRow(SvCondition c)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var prop = new ComboBox { IsEditable = true, Text = c.Property, Margin = new Thickness(0, 0, 4, 0), ToolTip = "Propriedade: Grupo.Nome (ex.: Pset_WallCommon.IsExternal) ou *.Nome" };
            prop.DropDownOpened += (s, e) => FillKeys(prop);
            var op = new ComboBox { Margin = new Thickness(0, 0, 4, 0) };
            foreach (string o in Ops.All) op.Items.Add(o);
            op.SelectedItem = c.Op;
            if (op.SelectedIndex < 0) op.SelectedIndex = 0;
            var val = new ComboBox { IsEditable = true, Text = c.Value, Margin = new Thickness(0, 0, 4, 0) };
            val.DropDownOpened += (s, e) =>
            {
                string t = val.Text;
                val.Items.Clear();
                foreach (string v in _values?.Invoke(prop.Text) ?? new List<string>()) val.Items.Add(v);
                val.Text = t;
            };
            var val2 = new TextBox { Text = c.Value2, Width = 80, Padding = new Thickness(3, 2, 3, 2), Margin = new Thickness(0, 0, 4, 0), ToolTip = "até" };
            var del = Ui.Small("✕", () => { _r.Conditions.Remove(c); _conds.Children.Remove(g); }, "Remover a condição");
            del.Margin = new Thickness(0);
            void Sync()
            {
                c.Property = prop.Text.Trim();
                c.Op = op.SelectedItem as string ?? Ops.Equal;
                c.Value = val.Text;
                c.Value2 = val2.Text;
                val.IsEnabled = !Ops.NoValue(c.Op);
                val2.Visibility = c.Op == Ops.Between ? Visibility.Visible : Visibility.Collapsed;
            }
            prop.LostFocus += (s, e) => Sync();
            prop.SelectionChanged += (s, e) => Dispatcher.BeginInvoke(new Action(Sync));
            op.SelectionChanged += (s, e) => Sync();
            val.LostFocus += (s, e) => Sync();
            val.SelectionChanged += (s, e) => Dispatcher.BeginInvoke(new Action(Sync));
            val2.TextChanged += (s, e) => Sync();
            g.Tag = (Action)Sync;
            Grid.SetColumn(op, 1); Grid.SetColumn(val, 2); Grid.SetColumn(val2, 3); Grid.SetColumn(del, 4);
            g.Children.Add(prop); g.Children.Add(op); g.Children.Add(val); g.Children.Add(val2); g.Children.Add(del);
            Sync();
            return g;
        }

        private void Collect()
        {
            foreach (FrameworkElement row in _conds.Children) (row.Tag as Action)?.Invoke();
            _r.Action = _action.SelectedItem as string ?? Actions.Show;
            _r.Any = _combine.SelectedIndex == 1;
            _r.Color = SmartViewEngine.ColorText(SmartViewEngine.ParseColor(_colorText.Text));
            _r.ColorBy = string.IsNullOrWhiteSpace(_colorBy.Text) ? PropertyReader.KClass : _colorBy.Text.Trim();
        }
    }
}
