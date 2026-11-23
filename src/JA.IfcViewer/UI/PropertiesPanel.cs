using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using JA.IfcViewer.Ifc;
using JA.IfcViewer.Viewer;

namespace JA.IfcViewer.UI
{
    /// <summary>
    /// Properties of the selection: every group (attributes, materials, classification, each Pset / Qto, the type's
    /// sets marked "tipo"), filter box, values selectable for copying; with several elements selected, the values they
    /// share and "‹vários›" where they differ. Right click on a property: select the elements with that value, colour
    /// by it (new Smart View), copy.
    /// </summary>
    public sealed class PropertiesPanel : DockPanel
    {
        private readonly TextBlock _title = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 14, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Text };
        private readonly TextBlock _subtitle = new TextBlock { Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, FontSize = 11 };
        private readonly TextBox _filter = new TextBox { Margin = new Thickness(0, 6, 0, 6), Padding = new Thickness(3, 2, 3, 2), ToolTip = "Filtrar propriedades" };
        private readonly StackPanel _list = new StackPanel();
        private readonly HashSet<string> _collapsed = new HashSet<string>();
        private List<PropGroup> _groups = new List<PropGroup>();

        public event Action<string, string> SelectByValue;   // key, value text
        public event Action<string> ColorBy;                  // key
        public event Action<string, string> FilterRule;       // key, value

        public PropertiesPanel()
        {
            Margin = new Thickness(8, 6, 8, 6);
            var head = new StackPanel();
            head.Children.Add(_title);
            head.Children.Add(_subtitle);
            head.Children.Add(_filter);
            DockPanel.SetDock(head, Dock.Top);
            Children.Add(head);
            Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
            _filter.TextChanged += (s, e) => Render();
            ShowNothing();
        }

        public void ShowNothing()
        {
            _title.Text = "Propriedades";
            _subtitle.Text = "Clique num elemento (Ctrl+clique soma à seleção).";
            _groups = new List<PropGroup>();
            Render();
        }

        public void Show(Scene s, ICollection<int> sel)
        {
            if (s == null || sel == null || sel.Count == 0) { ShowNothing(); return; }
            if (sel.Count == 1)
            {
                IfcObject o = s.Objects[sel.First()];
                _title.Text = o.DisplayName;
                _subtitle.Text = o.Class + (o.TypeName != null ? " · " + o.TypeName : "") + (o.Storey != null ? " · " + o.Storey.DisplayName : "");
                _groups = PropertyReader.Read(o);
                Render();
                return;
            }
            var objs = sel.Take(5000).Select(i => s.Objects[i]).ToList();
            _title.Text = sel.Count + " elementos selecionados";
            _subtitle.Text = string.Join(", ", objs.GroupBy(o => o.Class).OrderByDescending(g => g.Count()).Take(6).Select(g => g.Key + " " + g.Count()))
                             + (sel.Count > 5000 ? "  (valores dos primeiros 5000)" : "");
            // shared values: key → value or ‹vários›
            var flats = objs.Select(PropertyReader.Flat).ToList();
            var keys = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in flats) foreach (string k in f.Keys) if (seen.Add(k)) keys.Add(k);
            var groups = new Dictionary<string, PropGroup>();
            var order = new List<PropGroup>();
            foreach (string k in keys)
            {
                if (k.EndsWith(".GlobalId") || k.EndsWith(".Id no arquivo")) continue;
                int dot = k.IndexOf('.');
                string gname = dot > 0 ? k.Substring(0, dot) : "";
                string pname = dot > 0 ? k.Substring(dot + 1) : k;
                string first = null;
                bool same = true;
                int have = 0;
                foreach (var f in flats)
                {
                    if (!f.TryGetValue(k, out PropValue v)) { same = false; continue; }
                    have++;
                    if (first == null) first = v.Text;
                    else if (first != v.Text) same = false;
                }
                if (!groups.TryGetValue(gname, out PropGroup g)) { g = new PropGroup { Name = gname }; groups[gname] = g; order.Add(g); }
                g.Add(pname, new PropValue(same ? first : "‹vários›" + (have < flats.Count ? "  (" + have + " de " + flats.Count + ")" : "")));
            }
            _groups = order;
            Render();
        }

        private void Render()
        {
            _list.Children.Clear();
            string f = (_filter.Text ?? "").Trim();
            foreach (PropGroup g in _groups)
            {
                var items = f.Length == 0 ? g.Items : g.Items.Where(kv => kv.Key.IndexOf(f, StringComparison.CurrentCultureIgnoreCase) >= 0
                                                                       || kv.Value.Text.IndexOf(f, StringComparison.CurrentCultureIgnoreCase) >= 0
                                                                       || g.Name.IndexOf(f, StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();
                if (items.Count == 0) continue;
                string id = g.Name + (g.FromType ? "|t" : "");
                var grid = new Grid { Margin = new Thickness(4, 2, 0, 6) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.45, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.55, GridUnitType.Star) });
                int row = 0;
                foreach (var kv in items)
                {
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    var name = new TextBlock { Text = kv.Key, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 6, 1), VerticalAlignment = VerticalAlignment.Top };
                    var value = new TextBox
                    {
                        Text = kv.Value.Text, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                        TextWrapping = TextWrapping.Wrap, Padding = new Thickness(0), Margin = new Thickness(0, 1, 0, 1), Foreground = Ui.Text
                    };
                    string key = g.Name + "." + kv.Key, val = kv.Value.Text;
                    value.ContextMenu = RowMenu(key, val);
                    name.ContextMenu = RowMenu(key, val);
                    Grid.SetRow(name, row); Grid.SetRow(value, row); Grid.SetColumn(value, 1);
                    grid.Children.Add(name); grid.Children.Add(value);
                    row++;
                }
                var header = new TextBlock { Text = g.Name + (g.FromType ? "  (tipo)" : ""), FontWeight = FontWeights.SemiBold, Foreground = Ui.Text };
                var exp = new Expander { Header = header, Content = grid, IsExpanded = f.Length > 0 || !_collapsed.Contains(id), Margin = new Thickness(0, 0, 0, 2) };
                exp.Expanded += (s, e) => _collapsed.Remove(id);
                exp.Collapsed += (s, e) => _collapsed.Add(id);
                _list.Children.Add(new Border { Child = exp, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 3, 0, 3) });
            }
            if (_list.Children.Count == 0 && _groups.Count > 0) _list.Children.Add(Ui.Note("Nenhuma propriedade contém “" + f + "”."));
        }

        private ContextMenu RowMenu(string key, string value)
        {
            var m = new ContextMenu();
            void Item(string text, Action a) { var mi = new MenuItem { Header = text }; mi.Click += (s, e) => a(); m.Items.Add(mi); }
            bool multi = value.StartsWith("‹vários›");
            Item("Copiar valor", () => { try { Clipboard.SetText(value); } catch { } });
            Item("Copiar nome e valor", () => { try { Clipboard.SetText(key + ": " + value); } catch { } });
            m.Items.Add(new Separator());
            if (!multi) Item("Selecionar os elementos com este valor", () => SelectByValue?.Invoke(key, value));
            Item("Colorir o modelo por esta propriedade (Smart View)", () => ColorBy?.Invoke(key));
            if (!multi) Item("Nova regra de Smart View com este valor…", () => FilterRule?.Invoke(key, value));
            return m;
        }
    }
}
