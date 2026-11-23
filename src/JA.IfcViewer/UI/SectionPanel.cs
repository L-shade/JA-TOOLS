using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using JA.IfcViewer.Geometry;
using JA.IfcViewer.Ifc;
using JA.IfcViewer.Viewer;

namespace JA.IfcViewer.UI
{
    /// <summary>
    /// Cuts: a section box (six sliders, fit to selection or to what is visible) and free section planes (horizontal,
    /// vertical in X or Y, or on a clicked face), each with an offset slider, flip and on/off; and the storey plan cut
    /// (horizontal plane at storey + height, seen from above). Changes are coalesced and sent as one list of planes.
    /// </summary>
    public sealed class SectionPanel : ScrollViewer
    {
        private sealed class PlaneItem
        {
            public string Label;
            public Vec3 N;            // removes what is beyond N·p > D
            public double D;
            public bool Enabled = true;
            public double Min, Max;
            public FrameworkElement Row;
            public Slider Slider;
        }

        private readonly CheckBox _box = new CheckBox { Content = "Caixa de corte", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 4) };
        private readonly Slider[] _boxSliders = new Slider[6];
        private readonly TextBlock[] _boxLabels = new TextBlock[6];
        private readonly StackPanel _planesPanel = new StackPanel();
        private readonly List<PlaneItem> _planes = new List<PlaneItem>();
        private readonly ComboBox _storeys = new ComboBox { Margin = new Thickness(0, 0, 4, 0), MinWidth = 150 };
        private readonly TextBox _cutHeight = new TextBox { Text = "1,20", Width = 50, Padding = new Thickness(3, 1, 3, 1) };
        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        private Bounds _range = Bounds.Empty;
        private bool _quiet;
        private Scene _scene;

        public event Action Changed;
        public event Action PickFaceRequested;
        public event Action<IfcObject> StoreyPlanRequested;
        public Func<Bounds> SelectionBounds;
        public Func<Bounds> VisibleBounds;

        public SectionPanel()
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            var root = new StackPanel { Margin = new Thickness(8) };
            root.Children.Add(_box);
            _box.Click += (s, e) => Fire();
            string[] names = { "X mín.", "X máx.", "Y mín.", "Y máx.", "Z mín.", "Z máx." };
            for (int i = 0; i < 6; i++)
            {
                int k = i;
                var g = new Grid { Margin = new Thickness(0, 0, 0, 2) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
                g.Children.Add(new TextBlock { Text = names[i], Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center });
                var sl = new Slider { Minimum = 0, Maximum = 1, Value = i % 2 == 0 ? 0 : 1, VerticalAlignment = VerticalAlignment.Center };
                sl.ValueChanged += (s, e) => { if (_quiet) return; Clamp(k); if (_box.IsChecked != true) _box.IsChecked = true; UpdateLabels(); Fire(); };
                Grid.SetColumn(sl, 1);
                g.Children.Add(sl);
                var lb = new TextBlock { Foreground = Ui.Text, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
                Grid.SetColumn(lb, 2);
                g.Children.Add(lb);
                _boxSliders[i] = sl; _boxLabels[i] = lb;
                root.Children.Add(g);
            }
            var bb = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            bb.Children.Add(Ui.Small("Ajustar à seleção", () => FitBox(SelectionBounds?.Invoke() ?? Bounds.Empty)));
            bb.Children.Add(Ui.Small("Ajustar ao visível", () => FitBox(VisibleBounds?.Invoke() ?? Bounds.Empty)));
            bb.Children.Add(Ui.Small("Abrir caixa", () => { SetRange(_range); _box.IsChecked = false; Fire(); }));
            root.Children.Add(bb);

            root.Children.Add(Ui.Header("Planos de corte"));
            var pb = new WrapPanel();
            pb.Children.Add(Ui.Small("+ Horizontal", () => AddPlane("Horizontal", Vec3.UnitZ, _range.IsEmpty ? 1.2 : _range.Min.Z + Math.Min(1.2, _range.Size.Z / 2))));
            pb.Children.Add(Ui.Small("+ Vertical X", () => AddPlane("Vertical X", Vec3.UnitX, _range.IsEmpty ? 0 : _range.Center.X)));
            pb.Children.Add(Ui.Small("+ Vertical Y", () => AddPlane("Vertical Y", Vec3.UnitY, _range.IsEmpty ? 0 : _range.Center.Y)));
            pb.Children.Add(Ui.Small("+ Pela face (clique)", () => PickFaceRequested?.Invoke(), "Clique numa face no 3D: o corte passa por ela e tira o lado de quem olha"));
            root.Children.Add(pb);
            root.Children.Add(_planesPanel);

            root.Children.Add(Ui.Header("Planta do pavimento"));
            var sp = new WrapPanel();
            sp.Children.Add(_storeys);
            sp.Children.Add(new TextBlock { Text = "corte a", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
            sp.Children.Add(_cutHeight);
            sp.Children.Add(new TextBlock { Text = "m", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 6, 0) });
            root.Children.Add(sp);
            var go = Ui.Primary("Cortar e ver de cima", StoreyCut);
            go.HorizontalAlignment = HorizontalAlignment.Left;
            go.Margin = new Thickness(0, 6, 0, 0);
            root.Children.Add(go);
            root.Children.Add(Ui.Note("Mostra só o pavimento escolhido, cortado na altura indicada (acima do piso do pavimento), em vista de topo ortogonal."));

            var clear = Ui.Small("Remover todos os cortes", ClearAll);
            clear.HorizontalAlignment = HorizontalAlignment.Left;
            clear.Margin = new Thickness(0, 12, 0, 0);
            root.Children.Add(clear);
            Content = root;
            _timer.Tick += (s, e) => { _timer.Stop(); Changed?.Invoke(); };
        }

        public void SetScene(Scene s, Bounds range)
        {
            _scene = s;
            SetRange(range);
            _storeys.Items.Clear();
            if (s != null)
                foreach (IfcModel m in s.Models)
                    foreach (IfcObject st in m.Storeys.OrderByDescending(x => x.Elevation))
                        _storeys.Items.Add(new ComboBoxItem { Content = st.DisplayName + (s.Models.Count > 1 ? "  (" + m.FileName + ")" : "") + "  " + Ui.N(st.Elevation + s.Origin.Z, "0.00"), Tag = st });
            if (_storeys.Items.Count > 0) _storeys.SelectedIndex = _storeys.Items.Count - 1;
        }

        private void SetRange(Bounds b)
        {
            if (b.IsEmpty) return;
            Vec3 pad = new Vec3(0.5, 0.5, 0.5);
            _range = new Bounds { Min = b.Min - pad, Max = b.Max + pad };
            _quiet = true;
            for (int i = 0; i < 6; i++)
            {
                double lo = i < 2 ? _range.Min.X : i < 4 ? _range.Min.Y : _range.Min.Z;
                double hi = i < 2 ? _range.Max.X : i < 4 ? _range.Max.Y : _range.Max.Z;
                _boxSliders[i].Minimum = lo; _boxSliders[i].Maximum = hi;
                _boxSliders[i].Value = i % 2 == 0 ? lo : hi;
            }
            _quiet = false;
            UpdateLabels();
        }

        private void FitBox(Bounds b)
        {
            if (b.IsEmpty) return;
            if (_range.IsEmpty) SetRange(b);
            _quiet = true;
            double[] v = { b.Min.X - 0.05, b.Max.X + 0.05, b.Min.Y - 0.05, b.Max.Y + 0.05, b.Min.Z - 0.05, b.Max.Z + 0.05 };
            for (int i = 0; i < 6; i++) _boxSliders[i].Value = Math.Max(_boxSliders[i].Minimum, Math.Min(_boxSliders[i].Maximum, v[i]));
            _quiet = false;
            _box.IsChecked = true;
            UpdateLabels();
            Fire();
        }

        public void FitBoxTo(Bounds b) => FitBox(b);

        private void Clamp(int k)
        {
            int a = k - k % 2, b = a + 1;
            if (_boxSliders[a].Value > _boxSliders[b].Value - 0.01)
            {
                _quiet = true;
                if (k == a) _boxSliders[a].Value = _boxSliders[b].Value - 0.01; else _boxSliders[b].Value = _boxSliders[a].Value + 0.01;
                _quiet = false;
            }
        }

        private void UpdateLabels()
        {
            double[] o = { _scene?.Origin.X ?? 0, _scene?.Origin.X ?? 0, _scene?.Origin.Y ?? 0, _scene?.Origin.Y ?? 0, _scene?.Origin.Z ?? 0, _scene?.Origin.Z ?? 0 };
            for (int i = 0; i < 6; i++) _boxLabels[i].Text = Ui.N(_boxSliders[i].Value + o[i], "0.00");
        }

        private void Fire()
        {
            _timer.Stop();
            _timer.Start();
        }

        public bool BoxActive => _box.IsChecked == true;

        public Bounds Box => new Bounds
        {
            Min = new Vec3(_boxSliders[0].Value, _boxSliders[2].Value, _boxSliders[4].Value),
            Max = new Vec3(_boxSliders[1].Value, _boxSliders[3].Value, _boxSliders[5].Value)
        };

        /// <summary>All active planes (box faces + free planes), keep n·p ≤ D.</summary>
        public List<ClipPlane> Planes()
        {
            var r = new List<ClipPlane>();
            if (BoxActive)
            {
                Bounds b = Box;
                r.Add(new ClipPlane(Vec3.UnitX, b.Max.X)); r.Add(new ClipPlane(-Vec3.UnitX, -b.Min.X));
                r.Add(new ClipPlane(Vec3.UnitY, b.Max.Y)); r.Add(new ClipPlane(-Vec3.UnitY, -b.Min.Y));
                r.Add(new ClipPlane(Vec3.UnitZ, b.Max.Z)); r.Add(new ClipPlane(-Vec3.UnitZ, -b.Min.Z));
            }
            foreach (PlaneItem p in _planes) if (p.Enabled) r.Add(new ClipPlane(p.N, p.D));
            return r;
        }

        public bool Any => BoxActive || _planes.Any(p => p.Enabled);

        /// <summary>A plane through a point, removing what lies toward 'remove' (the viewer's side of a face).</summary>
        public void AddFacePlane(Vec3 point, Vec3 remove)
        {
            Vec3 n = remove.Normalized();
            string label = Math.Abs(n.Z) > 0.99 ? "Horizontal (face)" : Math.Abs(n.Z) < 0.01 ? "Vertical (face)" : "Inclinado (face)";
            AddPlane(label, n, n.Dot(point) + 0.001);
        }

        private void AddPlane(string label, Vec3 n, double d)
        {
            double span = _range.IsEmpty ? 50 : _range.Diagonal;
            double center = _range.IsEmpty ? d : n.Dot(_range.Center);
            var p = new PlaneItem { Label = label, N = n, D = d, Min = Math.Min(d, center - span / 2), Max = Math.Max(d, center + span / 2) };
            _planes.Add(p);
            BuildRow(p);
            Fire();
        }

        private void BuildRow(PlaneItem p)
        {
            var box = new Border { BorderBrush = Ui.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 4, 0, 0), Background = Ui.White };
            var sp = new StackPanel();
            var head = new DockPanel();
            var on = new CheckBox { IsChecked = true, Content = p.Label, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            on.Click += (s, e) => { p.Enabled = on.IsChecked == true; Fire(); };
            var del = Ui.Small("✕", () => { _planes.Remove(p); _planesPanel.Children.Remove(p.Row); Fire(); }, "Remover o plano");
            var flip = Ui.Small("Inverter", () => { p.N = -p.N; p.D = -p.D; double mn = -p.Max, mx = -p.Min; p.Min = mn; p.Max = mx; _quiet = true; p.Slider.Minimum = mn; p.Slider.Maximum = mx; p.Slider.Value = p.D; _quiet = false; Fire(); }, "Mostra o outro lado");
            DockPanel.SetDock(del, Dock.Right); DockPanel.SetDock(flip, Dock.Right);
            head.Children.Add(del); head.Children.Add(flip); head.Children.Add(on);
            sp.Children.Add(head);
            var sl = new Slider { Minimum = p.Min, Maximum = p.Max, Value = p.D, Margin = new Thickness(0, 2, 0, 0) };
            sl.ValueChanged += (s, e) => { if (_quiet) return; p.D = sl.Value; Fire(); };
            p.Slider = sl;
            sp.Children.Add(sl);
            box.Child = sp;
            p.Row = box;
            _planesPanel.Children.Add(box);
        }

        private void StoreyCut()
        {
            if (!(_storeys.SelectedItem is ComboBoxItem it) || !(it.Tag is IfcObject st)) return;
            if (!Ui.TryNumber(_cutHeight.Text, out double h)) h = 1.2;
            _planes.Clear();
            _planesPanel.Children.Clear();
            _box.IsChecked = false;
            AddPlane("Planta: " + st.DisplayName, Vec3.UnitZ, st.Elevation + h);
            StoreyPlanRequested?.Invoke(st);
        }

        public void ClearAll()
        {
            _planes.Clear();
            _planesPanel.Children.Clear();
            _box.IsChecked = false;
            SetRange(_range);
            Fire();
        }
    }
}
