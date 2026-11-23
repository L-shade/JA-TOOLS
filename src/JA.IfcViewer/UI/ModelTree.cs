using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using JA.IfcViewer.Ifc;
using JA.IfcViewer.Viewer;

namespace JA.IfcViewer.UI
{
    public sealed class TreeNode : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private void Raise(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

        public ModelTree Owner;
        public TreeNode Parent;
        public IfcObject Obj;
        public IfcModel Model;
        public List<int> Elements = new List<int>();    // scene indices with geometry below
        private HashSet<int> _set;
        public string Text { get; set; }
        public string CountText => Obj != null && Obj.HasGeometry && Elements.Count <= 1 ? "" : "(" + Elements.Count + ")";
        public FontWeight Weight => Obj == null || Obj.IsSpatial ? FontWeights.SemiBold : FontWeights.Normal;
        public ObservableCollection<TreeNode> Children { get; } = new ObservableCollection<TreeNode>();
        internal Func<List<TreeNode>> Factory;
        private bool _loaded = true;
        private static readonly TreeNode Dummy = new TreeNode { Text = "…" };

        public bool Contains(int index)
        {
            if (_set == null) _set = new HashSet<int>(Elements);
            return _set.Contains(index);
        }

        internal void SetLazy(Func<List<TreeNode>> f)
        {
            Factory = f;
            _loaded = false;
            Children.Clear();
            Children.Add(Dummy);
        }

        public void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            Children.Clear();
            foreach (TreeNode c in Factory()) { c.Parent = this; Children.Add(c); }
        }

        public IEnumerable<TreeNode> LoadedChildren => _loaded ? Children : Enumerable.Empty<TreeNode>();

        private bool _expanded;
        public bool IsExpanded
        {
            get => _expanded;
            set { if (_expanded == value) return; _expanded = value; if (value) EnsureLoaded(); Raise(nameof(IsExpanded)); }
        }

        private bool _selected;
        public bool IsSelected
        {
            get => _selected;
            set { if (_selected == value) return; _selected = value; Raise(nameof(IsSelected)); if (value) Owner?.OnSelected(this); }
        }

        private bool? _checked = true;
        public bool? IsChecked
        {
            get => _checked;
            set { if (_checked == value) return; _checked = value; Raise(nameof(IsChecked)); Owner?.OnChecked(this, value != false); }
        }

        internal void SetCheckSilently(bool? v) { if (_checked == v) return; _checked = v; Raise(nameof(IsChecked)); }
        internal void SetSelectedSilently(bool v) { if (_selected == v) return; _selected = v; Raise(nameof(IsSelected)); }
    }

    /// <summary>
    /// The model tree, like Zoom's: by spatial structure (model › site › building › storey › class › element, parts of
    /// assemblies below their element) or grouped by class, storey, type, material or layer. Children are made when a
    /// node is opened and the list is virtualised, so 100 000 elements stay fast. The check boxes hide and show.
    /// </summary>
    public sealed class ModelTree : DockPanel
    {
        public static readonly string[] Modes = { "Estrutura espacial", "Classe IFC", "Pavimento", "Tipo", "Material", "Camada", "Modelo" };

        private readonly TreeView _tree = new TreeView { BorderThickness = new Thickness(0), Background = Brushes.White };
        private readonly ComboBox _mode = new ComboBox { Margin = new Thickness(0, 0, 0, 4) };
        private readonly TextBox _search = new TextBox { Margin = new Thickness(0, 0, 0, 4), Padding = new Thickness(3, 2, 3, 2) };
        private readonly ObservableCollection<TreeNode> _roots = new ObservableCollection<TreeNode>();
        private Scene _scene;
        private bool _quiet;

        public event Action<TreeNode> NodeSelected;
        public event Action<TreeNode> NodeDoubleClicked;
        public event Action VisibilityChanged;
        public event Action<string> SearchRequested;

        public ModelTree()
        {
            LastChildFill = true;
            Margin = new Thickness(6);
            foreach (string m in Modes) _mode.Items.Add(m);
            _mode.SelectedIndex = 0;
            _mode.SelectionChanged += (s, e) => Rebuild();
            var top = new StackPanel();
            top.Children.Add(_mode);
            var sg = new Grid();
            sg.ColumnDefinitions.Add(new ColumnDefinition());
            sg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            sg.Children.Add(_search);
            var go = Ui.Small("Buscar", () => SearchRequested?.Invoke(_search.Text), "Seleciona os elementos cujo nome, tipo, classe, tag ou GlobalId contém o texto");
            go.Margin = new Thickness(4, 0, 0, 4);
            Grid.SetColumn(go, 1);
            sg.Children.Add(go);
            _search.KeyDown += (s, e) => { if (e.Key == Key.Enter) { SearchRequested?.Invoke(_search.Text); e.Handled = true; } };
            _search.ToolTip = "Buscar por nome, tipo, classe, tag ou GlobalId (Enter)";
            top.Children.Add(sg);
            DockPanel.SetDock(top, Dock.Top);
            Children.Add(top);

            var tpl = new HierarchicalDataTemplate { ItemsSource = new Binding("Children") };
            var sp = new FrameworkElementFactory(typeof(StackPanel));
            sp.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            var cb = new FrameworkElementFactory(typeof(CheckBox));
            cb.SetBinding(ToggleButton.IsCheckedProperty, new Binding("IsChecked") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            cb.SetValue(UIElement.FocusableProperty, false);
            cb.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            cb.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 4, 0));
            sp.AppendChild(cb);
            var tb = new FrameworkElementFactory(typeof(TextBlock));
            tb.SetBinding(TextBlock.TextProperty, new Binding("Text"));
            tb.SetBinding(TextBlock.FontWeightProperty, new Binding("Weight"));
            tb.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            sp.AppendChild(tb);
            var cnt = new FrameworkElementFactory(typeof(TextBlock));
            cnt.SetBinding(TextBlock.TextProperty, new Binding("CountText"));
            cnt.SetValue(TextBlock.ForegroundProperty, Ui.Muted);
            cnt.SetValue(FrameworkElement.MarginProperty, new Thickness(5, 0, 0, 0));
            cnt.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            sp.AppendChild(cnt);
            tpl.VisualTree = sp;
            _tree.ItemTemplate = tpl;
            var st = new Style(typeof(TreeViewItem));
            st.Setters.Add(new Setter(TreeViewItem.IsExpandedProperty, new Binding("IsExpanded") { Mode = BindingMode.TwoWay }));
            st.Setters.Add(new Setter(TreeViewItem.IsSelectedProperty, new Binding("IsSelected") { Mode = BindingMode.TwoWay }));
            st.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(1)));
            _tree.ItemContainerStyle = st;
            VirtualizingPanel.SetIsVirtualizing(_tree, true);
            VirtualizingPanel.SetVirtualizationMode(_tree, VirtualizationMode.Recycling);
            ScrollViewer.SetCanContentScroll(_tree, true);
            _tree.ItemsSource = _roots;
            _tree.MouseDoubleClick += (s, e) => { if (_tree.SelectedItem is TreeNode n) NodeDoubleClicked?.Invoke(n); };
            Children.Add(new Border { BorderBrush = Ui.Line, BorderThickness = new Thickness(1), Child = _tree });
        }

        public int Mode => _mode.SelectedIndex;

        public void SetScene(Scene s) { _scene = s; Rebuild(); }

        public void Rebuild()
        {
            _roots.Clear();
            if (_scene == null) return;
            switch (Mode)
            {
                case 0:
                    foreach (IfcModel m in _scene.Models) _roots.Add(SpatialNode(m.Project, m, true));
                    break;
                default:
                {
                    Func<IfcObject, string> key = KeyFor(Mode);
                    var objs = _scene.Objects.Where(o => o.HasGeometry && o.Model.Visible).ToList();
                    foreach (TreeNode n in Groups(objs, key, Mode == 1 ? (Func<IfcObject, string>)(o => o.TypeName ?? "(sem tipo)") : null)) _roots.Add(n);
                    break;
                }
            }
            if (_roots.Count == 1) _roots[0].IsExpanded = true;
            RefreshChecks();
        }

        private static Func<IfcObject, string> KeyFor(int mode)
        {
            switch (mode)
            {
                case 1: return o => IfcNames.Label(o.Class);
                case 2: return o => o.Storey?.DisplayName ?? "(sem pavimento)";
                case 3: return o => o.TypeName ?? "(sem tipo)";
                case 4: return o => { var f = PropertyReader.Flat(o); return f.TryGetValue(PropertyReader.KMaterial, out PropValue v) && v.Text.Length > 0 ? v.Text : "(sem material)"; };
                case 5: return o => { var f = PropertyReader.Flat(o); return f.TryGetValue(PropertyReader.KLayer, out PropValue v) && v.Text.Length > 0 ? v.Text : "(sem camada)"; };
                case 6: return o => o.Model.FileName;
            }
            return o => o.Class;
        }

        private List<TreeNode> Groups(List<IfcObject> objs, Func<IfcObject, string> key, Func<IfcObject, string> sub)
        {
            var r = new List<TreeNode>();
            IEnumerable<IGrouping<string, IfcObject>> groups = objs.GroupBy(key);
            if (Mode == 2)
                groups = groups.OrderBy(g => g.First().Storey?.Elevation ?? double.MaxValue).ThenBy(g => g.Key);
            else groups = groups.OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase);
            foreach (var g in groups)
            {
                var items = g.ToList();
                var n = new TreeNode { Owner = this, Text = g.Key, Elements = items.Select(o => o.Index).ToList() };
                if (sub != null) n.SetLazy(() => Groups2(items, sub));
                else n.SetLazy(() => Leaves(items));
                r.Add(n);
            }
            return r;
        }

        private List<TreeNode> Groups2(List<IfcObject> objs, Func<IfcObject, string> key)
        {
            var r = new List<TreeNode>();
            foreach (var g in objs.GroupBy(key).OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
            {
                var items = g.ToList();
                var n = new TreeNode { Owner = this, Text = g.Key, Elements = items.Select(o => o.Index).ToList() };
                n.SetLazy(() => Leaves(items));
                r.Add(n);
            }
            return r;
        }

        private List<TreeNode> Leaves(List<IfcObject> objs) =>
            objs.OrderBy(o => o.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .Select(o => new TreeNode { Owner = this, Obj = o, Model = o.Model, Text = LeafText(o), Elements = new List<int> { o.Index } }).ToList();

        private static string LeafText(IfcObject o)
        {
            string n = o.DisplayName;
            if (!string.IsNullOrWhiteSpace(o.Tag) && !n.Contains(o.Tag)) n += "  [" + o.Tag + "]";
            return n;
        }

        private TreeNode SpatialNode(IfcObject o, IfcModel m, bool root)
        {
            var n = new TreeNode
            {
                Owner = this, Obj = o, Model = m,
                Text = root ? m.FileName + (string.IsNullOrWhiteSpace(o.Name) ? "" : "  —  " + o.Name) : (o.IsSpatial ? o.DisplayName : LeafText(o)),
                Elements = _scene.WithChildren(o).Where(i => _scene.Objects[i].HasGeometry).ToList()
            };
            if (o.Children.Count > 0) n.SetLazy(() => SpatialChildren(o, m));
            return n;
        }

        private List<TreeNode> SpatialChildren(IfcObject o, IfcModel m)
        {
            var r = new List<TreeNode>();
            var spatial = o.Children.Where(c => c.IsSpatial && c.Type != "IFCSPACE").ToList();
            spatial.Sort((a, b) => IfcNames.IsStoreyLike(a.Type) && IfcNames.IsStoreyLike(b.Type) ? b.Elevation.CompareTo(a.Elevation) : string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase));
            foreach (IfcObject c in spatial) r.Add(SpatialNode(c, m, false));
            var rest = o.Children.Where(c => !c.IsSpatial || c.Type == "IFCSPACE").ToList();
            foreach (var g in rest.GroupBy(c => IfcNames.Label(c.Class)).OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
            {
                var items = g.ToList();
                var gn = new TreeNode
                {
                    Owner = this, Model = m, Text = g.Key,
                    Elements = items.SelectMany(i => _scene.WithChildren(i)).Where(i => _scene.Objects[i].HasGeometry).ToList()
                };
                gn.SetLazy(() => items.OrderBy(i => i.DisplayName, StringComparer.CurrentCultureIgnoreCase).Select(i => SpatialNode(i, m, false)).ToList());
                r.Add(gn);
            }
            return r;
        }

        // ------------------------------------------------------------------ check boxes (visibility)

        internal void OnChecked(TreeNode n, bool visible)
        {
            if (_quiet || _scene == null) return;
            if (n.Obj != null && n.Parent == null && Mode == 0 && n.Model != null)
            {
                // a model's root: the whole model on/off
                n.Model.Visible = visible;
                foreach (int i in n.Elements) _scene.UserHidden[i] = false;
            }
            else foreach (int i in n.Elements) _scene.UserHidden[i] = !visible;
            RefreshChecks();
            VisibilityChanged?.Invoke();
        }

        /// <summary>Check box states from the scene (after hide / isolate / show all).</summary>
        public void RefreshChecks()
        {
            if (_scene == null) return;
            _quiet = true;
            try
            {
                var st = new Stack<TreeNode>(_roots);
                while (st.Count > 0)
                {
                    TreeNode n = st.Pop();
                    bool? v;
                    if (n.Parent == null && Mode == 0 && n.Model != null && !n.Model.Visible) v = false;
                    else
                    {
                        int hidden = 0;
                        foreach (int i in n.Elements) if (_scene.UserHidden[i]) hidden++;
                        v = hidden == 0 ? true : hidden == n.Elements.Count ? false : (bool?)null;
                    }
                    n.SetCheckSilently(v);
                    foreach (TreeNode c in n.LoadedChildren) st.Push(c);
                }
            }
            finally { _quiet = false; }
        }

        // ------------------------------------------------------------------ selection

        internal void OnSelected(TreeNode n)
        {
            if (_quiet) return;
            NodeSelected?.Invoke(n);
        }

        /// <summary>Opens the tree down to the object and selects its node (without echoing the selection).</summary>
        public void Reveal(IfcObject target)
        {
            if (target == null) return;
            _quiet = true;
            try
            {
                TreeNode found = null;
                foreach (TreeNode root in _roots)
                {
                    found = Find(root, target, 0);
                    if (found != null) break;
                }
                if (found == null) return;
                for (TreeNode p = found.Parent; p != null; p = p.Parent) p.IsExpanded = true;
                ClearSelection();
                found.SetSelectedSilently(true);
            }
            finally { _quiet = false; }
        }

        private void ClearSelection()
        {
            var st = new Stack<TreeNode>(_roots);
            while (st.Count > 0)
            {
                TreeNode n = st.Pop();
                if (n.IsSelected) n.SetSelectedSilently(false);
                foreach (TreeNode c in n.LoadedChildren) st.Push(c);
            }
        }

        public void Deselect() { _quiet = true; try { ClearSelection(); } finally { _quiet = false; } }

        private static TreeNode Find(TreeNode n, IfcObject target, int depth)
        {
            if (n.Obj == target) return n;
            if (depth > 40) return null;
            bool may = target.Index >= 0 && n.Contains(target.Index) || n.Obj != null && IsAncestor(n.Obj, target) || n.Obj == null && n.Model == target.Model && !target.HasGeometry;
            if (!may) return null;
            n.EnsureLoaded();
            foreach (TreeNode c in n.Children)
            {
                TreeNode f = Find(c, target, depth + 1);
                if (f != null) return f;
            }
            return null;
        }

        private static bool IsAncestor(IfcObject a, IfcObject b)
        {
            int guard = 0;
            for (IfcObject p = b.Parent; p != null && guard++ < 64; p = p.Parent) if (p == a) return true;
            return false;
        }
    }
}
