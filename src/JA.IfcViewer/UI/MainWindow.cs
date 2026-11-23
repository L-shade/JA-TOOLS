using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using JA.IfcViewer.Geometry;
using JA.IfcViewer.Ifc;
using JA.IfcViewer.SmartViews;
using JA.IfcViewer.Viewer;

namespace JA.IfcViewer.UI
{
    /// <summary>
    /// JA Visualizador IFC: toolbar (open, views, visibility, cuts, measurements, image), model tree and Smart Views and
    /// cuts on the left, the 3D in the middle (with the Smart View legend), properties on the right, status below.
    /// </summary>
    public sealed class MainWindow : Window
    {
        private readonly Scene _scene = new Scene();
        private readonly SceneView _view = new SceneView();
        private readonly ModelTree _tree = new ModelTree();
        private readonly PropertiesPanel _props = new PropertiesPanel();
        private readonly SmartViewPanel _svPanel = new SmartViewPanel();
        private readonly SectionPanel _sections = new SectionPanel();
        private readonly TextBlock _status = new TextBlock { Foreground = Ui.Text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _counts = new TextBlock { Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center };
        private readonly Border _legend = new Border();
        private readonly Border _loading = new Border();
        private readonly TextBlock _loadText = new TextBlock { Foreground = Ui.Text, FontSize = 14, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
        private readonly ProgressBar _loadBar = new ProgressBar { Height = 6, Minimum = 0, Maximum = 1, Width = 360 };
        private readonly Border _empty = new Border();
        private ToggleButton _tDist, _tArea, _tPoint, _tPersp, _tXRay, _tSpaces, _tBox;
        private CancellationTokenSource _cancel;
        private bool _loadingNow;
        private List<string> _keyCache;
        private List<LegendEntry> _legendData = new List<LegendEntry>();

        public MainWindow()
        {
            Title = "JA Visualizador IFC";
            Width = 1400; Height = 860;
            MinWidth = 900; MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Ui.Panel;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 12;
            AllowDrop = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            UseLayoutRounding = true;

            var dock = new DockPanel();
            var toolbar = BuildToolbar();
            DockPanel.SetDock(toolbar, Dock.Top);
            dock.Children.Add(toolbar);
            var statusBar = BuildStatus();
            DockPanel.SetDock(statusBar, Dock.Bottom);
            dock.Children.Add(statusBar);

            var main = new Grid();
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(310), MinWidth = 200 });
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 300 });
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
            main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330), MinWidth = 200 });

            var tabs = new TabControl { Margin = new Thickness(6, 6, 0, 6), Background = Ui.White, BorderBrush = Ui.Line };
            tabs.Items.Add(new TabItem { Header = "Modelo", Content = _tree });
            tabs.Items.Add(new TabItem { Header = "Smart Views", Content = _svPanel });
            tabs.Items.Add(new TabItem { Header = "Cortes", Content = _sections });
            main.Children.Add(tabs);
            var sp1 = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent };
            Grid.SetColumn(sp1, 1);
            main.Children.Add(sp1);

            var center = new Grid { Margin = new Thickness(0, 6, 0, 6) };
            center.Children.Add(new Border { BorderBrush = Ui.Line, BorderThickness = new Thickness(1), Child = _view });
            BuildLegend();
            center.Children.Add(_legend);
            BuildEmpty();
            center.Children.Add(_empty);
            BuildLoading();
            center.Children.Add(_loading);
            Grid.SetColumn(center, 2);
            main.Children.Add(center);
            var sp2 = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent };
            Grid.SetColumn(sp2, 3);
            main.Children.Add(sp2);
            var right = new Border { Background = Ui.White, BorderBrush = Ui.Line, BorderThickness = new Thickness(1), Margin = new Thickness(0, 6, 6, 6), Child = _props };
            Grid.SetColumn(right, 4);
            main.Children.Add(right);
            dock.Children.Add(main);
            Content = dock;

            _view.SetScene(_scene);
            _tree.SetScene(_scene);
            Wire();
            UpdateCounts();
            Status("Abra um ou mais arquivos IFC (Ctrl+O) ou arraste-os para a janela.");
        }

        // ================================================================== layout

        private FrameworkElement BuildToolbar()
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 4, 4, 2) };
            sp.Children.Add(Ui.Group("Arquivo",
                Ui.Tool("Abrir", Ui.Icons.Open, "Abrir IFC (Ctrl+O) — vários juntos formam um modelo federado", OpenDialog),
                Ui.Tool("Recentes", Ui.Icons.Add, "Arquivos abertos recentemente", ShowRecent),
                Ui.Tool("Fechar", Ui.Icons.Close, "Fechar todos os modelos", CloseAll),
                Ui.Tool("Modelos", Ui.Icons.Info, "Informações dos modelos: esquema, programa de origem, unidades, geometria não lida", ModelsInfo)));
            sp.Children.Add(Ui.Separator());
            _tPersp = Ui.ToolToggle("Ortogonal", Ui.Icons.Persp, "Alterna perspectiva e ortogonal (P)", v => _view.Orthographic = v);
            Button front = Ui.Tool("Frente", Ui.Icons.Front, "Vista frontal, olhando para o norte (Ctrl+3); clique direito: fundos, esquerda, direita", () => _view.ViewFront());
            front.ContextMenu = ViewsMenu();
            sp.Children.Add(Ui.Group("Vista",
                Ui.Tool("Tudo", Ui.Icons.Fit, "Enquadrar tudo (F sem seleção)", () => _view.Fit(null)),
                Ui.Tool("Seleção", Ui.Icons.FitSel, "Enquadrar a seleção (F)", FitSelection),
                Ui.Tool("3D", Ui.Icons.Iso, "Vista 3D isométrica (Ctrl+1)", () => _view.ViewIso()),
                Ui.Tool("Topo", Ui.Icons.Top, "Vista de topo (Ctrl+2)", () => _view.ViewTop()),
                front,
                _tPersp));
            sp.Children.Add(Ui.Separator());
            _tXRay = Ui.ToolToggle("Raio-X", Ui.Icons.XRay, "Tudo transparente, a seleção em destaque (X)", v => { _scene.XRay = v; _view.InvalidateAll(); });
            _tSpaces = Ui.ToolToggle("Ambientes", Ui.Icons.Spaces, "Mostrar os ambientes (IfcSpace)", v => { _scene.ShowSpaces = v; ApplySmartView(_svPanel.Active); });
            sp.Children.Add(Ui.Group("Visibilidade",
                Ui.Tool("Ocultar", Ui.Icons.Hide, "Ocultar a seleção (H ou Delete)", Hide),
                Ui.Tool("Isolar", Ui.Icons.Isolate, "Mostrar só a seleção (I)", Isolate),
                Ui.Tool("Mostrar", Ui.Icons.Show, "Mostrar tudo de novo (Shift+H)", ShowAll),
                Ui.Tool("Transp.", Ui.Icons.Transparent, "Seleção transparente / opaca (T)", ToggleTransparent),
                _tXRay, _tSpaces));
            sp.Children.Add(Ui.Separator());
            _tBox = Ui.ToolToggle("Caixa", Ui.Icons.Box, "Caixa de corte na seleção (ou no visível); clique de novo para tirar", v => { if (v) _sections.FitBoxTo(SelectionOrVisibleBounds()); else _sections.ClearAll(); });
            sp.Children.Add(Ui.Group("Cortes",
                _tBox,
                Ui.Tool("Plano", Ui.Icons.Plane, "Plano de corte na face clicada", () => _view.Mode = MeasureMode.PickPlane),
                Ui.Tool("Limpar", Ui.Icons.Clear, "Remover todos os cortes", () => { _sections.ClearAll(); _tBox.IsChecked = false; })));
            sp.Children.Add(Ui.Separator());
            _tDist = Ui.ToolToggle("Distância", Ui.Icons.Distance, "Medir distância (D): atrai vértices, meios e arestas", v => _view.Mode = v ? MeasureMode.Distance : MeasureMode.None);
            _tArea = Ui.ToolToggle("Área", Ui.Icons.Area, "Medir a área de uma face (R)", v => _view.Mode = v ? MeasureMode.Area : MeasureMode.None);
            _tPoint = Ui.ToolToggle("Ponto", Ui.Icons.Point, "Coordenadas e cota de um ponto (C)", v => _view.Mode = v ? MeasureMode.Point : MeasureMode.None);
            sp.Children.Add(Ui.Group("Medir",
                _tDist, _tArea, _tPoint,
                Ui.Tool("Apagar", Ui.Icons.Clear, "Apagar as medições", () => _view.ClearMeasurements())));
            sp.Children.Add(Ui.Separator());
            sp.Children.Add(Ui.Group("Saída",
                Ui.Tool("Imagem", Ui.Icons.Camera, "Salvar a vista como imagem PNG (e copiar)", SaveImage),
                Ui.Tool("Ajuda", Ui.Icons.Help, "Atalhos e dicas", HelpDialog)));
            return new Border { Background = Ui.White, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1), Child = new ScrollViewer { Content = sp, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled } };
        }

        private ContextMenu ViewsMenu()
        {
            var m = new ContextMenu();
            void Add(string t, Action a) { var mi = new MenuItem { Header = t }; mi.Click += (s, e) => a(); m.Items.Add(mi); }
            Add("Frente (olhando para o norte)", () => _view.ViewFront());
            Add("Fundos (olhando para o sul)", () => _view.ViewBack());
            Add("Esquerda (olhando para o leste)", () => _view.ViewLeft());
            Add("Direita (olhando para o oeste)", () => _view.ViewRight());
            Add("Topo", () => _view.ViewTop());
            Add("3D isométrica", () => _view.ViewIso());
            return m;
        }

        private FrameworkElement BuildStatus()
        {
            var g = new Grid { Margin = new Thickness(8, 3, 8, 4) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(_status);
            Grid.SetColumn(_counts, 1);
            g.Children.Add(_counts);
            return new Border { Background = Ui.White, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 0, 0), Child = g };
        }

        private void BuildLegend()
        {
            _legend.HorizontalAlignment = HorizontalAlignment.Left;
            _legend.VerticalAlignment = VerticalAlignment.Bottom;
            _legend.Margin = new Thickness(10);
            _legend.Background = new SolidColorBrush(Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF));
            _legend.BorderBrush = Ui.Line;
            _legend.BorderThickness = new Thickness(1);
            _legend.CornerRadius = new CornerRadius(4);
            _legend.Padding = new Thickness(8, 6, 8, 6);
            _legend.MaxWidth = 340;
            _legend.Visibility = Visibility.Collapsed;
        }

        private void BuildEmpty()
        {
            var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(Ui.Icon(Ui.Icons.Iso, 56, Ui.Muted));
            sp.Children.Add(new TextBlock { Text = "JA Visualizador IFC", FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Ui.Text, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 4) });
            sp.Children.Add(new TextBlock { Text = "Arraste arquivos .ifc / .ifczip para cá ou clique em Abrir.\nVários arquivos juntos formam um modelo federado (arquitetura + estrutura + instalações).", Foreground = Ui.Muted, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center });
            var b = Ui.Primary("Abrir IFC…", OpenDialog);
            b.HorizontalAlignment = HorizontalAlignment.Center;
            b.Margin = new Thickness(0, 14, 0, 0);
            sp.Children.Add(b);
            _empty.Child = sp;
            _empty.IsHitTestVisible = true;
            _empty.Background = Brushes.Transparent;
        }

        private void BuildLoading()
        {
            var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(_loadText);
            sp.Children.Add(_loadBar);
            var cancel = Ui.Small("Cancelar", () => _cancel?.Cancel());
            cancel.HorizontalAlignment = HorizontalAlignment.Center;
            cancel.Margin = new Thickness(0, 12, 0, 0);
            sp.Children.Add(cancel);
            _loading.Child = new Border { Child = sp, Background = Ui.White, BorderBrush = Ui.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(24, 18, 24, 18), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            _loading.Background = new SolidColorBrush(Color.FromArgb(0x88, 0xF6, 0xF7, 0xF9));
            _loading.Visibility = Visibility.Collapsed;
        }

        // ================================================================== wiring

        private void Wire()
        {
            _view.ElementClicked += (i, ctrl) =>
            {
                if (i < 0) { if (!ctrl) Select(new int[0], false); return; }
                if (ctrl) { var old = _scene.Selection.ToList(); _scene.Toggle(i); SelectionChanged(old, true); }
                else Select(new[] { i }, false);
            };
            _view.ElementDoubleClicked += i => { Select(new[] { i }, false); _view.Fit(new[] { i }); };
            _view.ContextRequested += (i, p) =>
            {
                if (i >= 0 && !_scene.Selected[i]) Select(new[] { i }, false);
                var m = ContextMenuFor(i);
                m.PlacementTarget = _view;
                m.IsOpen = true;
            };
            _view.Message += Status;
            _view.ModeChanged += () =>
            {
                _tDist.IsChecked = _view.Mode == MeasureMode.Distance;
                _tArea.IsChecked = _view.Mode == MeasureMode.Area;
                _tPoint.IsChecked = _view.Mode == MeasureMode.Point;
            };
            _view.FacePicked += (p, n) => { _sections.AddFacePlane(p, n); Status("Plano de corte criado: ajuste-o na aba Cortes."); };

            _tree.NodeSelected += n =>
            {
                if (n.Obj != null && n.Obj.Children.Count == 0) { SelectObject(n.Obj); return; }   // one element
                Select(n.Elements, false, true);
                // a storey, building, assembly…: its own properties, with its elements selected
                if (n.Obj != null && n.Obj.Index >= 0 && !(n.Obj.HasGeometry && n.Elements.Count == 1)) _props.Show(_scene, new[] { n.Obj.Index });
            };
            _tree.NodeDoubleClicked += n => { if (n.Elements.Count > 0) _view.Fit(n.Elements); };
            _tree.VisibilityChanged += () => { _view.InvalidateAll(); UpdateCounts(); };
            _tree.SearchRequested += Search;

            _props.SelectByValue += (key, value) =>
            {
                var hits = _scene.Objects.Where(o => o.HasGeometry && SmartViewEngine.Lookup(PropertyReader.Flat(o), key)?.Text == value).Select(o => o.Index).ToList();
                Select(hits, false);
                Status(hits.Count + " elemento(s) com " + SvCondition.Short(key) + " = " + value);
            };
            _props.ColorBy += key =>
            {
                var sv = new SmartView { Name = "Por " + SvCondition.Short(key), Folder = "Minhas" };
                sv.Rules.Add(new SvRule { Action = Actions.ColorByValue, ColorBy = key });
                _svPanel.Apply(sv);
            };
            _props.FilterRule += (key, value) =>
            {
                var sv = new SmartView { Name = SvCondition.Short(key) + " = " + value, Folder = "Minhas" };
                sv.Rules.Add(new SvRule { Action = Actions.Transparent });
                sv.Rules.Add(new SvRule { Action = Actions.Color, Color = "#E8A33D", Conditions = { new SvCondition { Property = key, Op = Ops.Equal, Value = value } } });
                sv.Rules.Add(new SvRule { Action = Actions.Opaque, Conditions = { new SvCondition { Property = key, Op = Ops.Equal, Value = value } } });
                _svPanel.CreateFrom(sv, true);
            };

            _svPanel.Applied += ApplySmartView;
            _svPanel.Keys = AllKeys;
            _svPanel.Values = ValuesOf;
            _svPanel.Preview = sv => { ApplySmartView(sv); return _scene.VisibleIndices().Count(); };
            _svPanel.CountRule = r => _scene.Objects.Count(o => o.HasGeometry && SmartViewEngine.Matches(o, r));

            _sections.Changed += () =>
            {
                _scene.Planes = _sections.Planes();
                _view.SectionBoxOutline = _sections.BoxActive ? _sections.Box : (Bounds?)null;
                _tBox.IsChecked = _sections.BoxActive;
                _view.InvalidateAll();
            };
            _sections.PickFaceRequested += () => _view.Mode = MeasureMode.PickPlane;
            _sections.SelectionBounds = () => _scene.BoundsOf(_scene.Selection);
            _sections.VisibleBounds = () => _scene.BoundsOf(_scene.VisibleIndices());
            _sections.StoreyPlanRequested += st =>
            {
                for (int i = 0; i < _scene.Count; i++) _scene.UserHidden[i] = _scene.Objects[i].HasGeometry && _scene.Objects[i].Storey != st;
                _tree.RefreshChecks();
                _view.Orthographic = true;
                _tPersp.IsChecked = true;
                _view.InvalidateAll();
                _view.Rebuild();
                _view.SetView(new Vec3(0, 0, -1), Vec3.UnitY, false);
                _view.Fit(_scene.Objects.Where(o => o.Storey == st && o.HasGeometry).Select(o => o.Index));
                UpdateCounts();
                Status("Planta de " + st.DisplayName + ": “Mostrar” traz o resto de volta; “Limpar” tira o corte.");
            };

            PreviewKeyDown += OnKey;
            Drop += (s, e) =>
            {
                if (!(e.Data.GetData(DataFormats.FileDrop) is string[] files)) return;
                var ok = files.Where(f => f.EndsWith(".ifc", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".ifczip", StringComparison.OrdinalIgnoreCase)).ToList();
                if (ok.Count > 0) OpenFiles(ok);
            };
            Closing += (s, e) => _cancel?.Cancel();
        }

        // ================================================================== files

        private void OpenDialog()
        {
            var d = new Microsoft.Win32.OpenFileDialog { Filter = "Modelos IFC (*.ifc;*.ifczip)|*.ifc;*.ifczip|Todos os arquivos|*.*", Multiselect = true, Title = "Abrir IFC" };
            if (d.ShowDialog(this) == true) OpenFiles(d.FileNames.ToList());
        }

        public async void OpenFiles(IList<string> files)
        {
            if (_loadingNow || files == null || files.Count == 0) return;
            _loadingNow = true;
            _cancel = new CancellationTokenSource();
            CancellationToken ct = _cancel.Token;
            _loading.Visibility = Visibility.Visible;
            _empty.Visibility = Visibility.Collapsed;
            var errors = new List<string>();
            var notes = new List<string>();
            bool first = _scene.Models.Count == 0;
            try
            {
                foreach (string f in files)
                {
                    if (ct.IsCancellationRequested) break;
                    if (_scene.Models.Any(m => string.Equals(m.Path, f, StringComparison.OrdinalIgnoreCase))) { notes.Add(System.IO.Path.GetFileName(f) + " já está aberto."); continue; }
                    DateTime last = DateTime.MinValue;
                    Action<string, double> progress = (text, p) =>
                    {
                        if ((DateTime.Now - last).TotalMilliseconds < 80 && p < 1) return;
                        last = DateTime.Now;
                        Dispatcher.BeginInvoke(new Action(() => { _loadText.Text = text; _loadBar.Value = Math.Max(0, Math.Min(1, p)); }));
                    };
                    _loadText.Text = "Abrindo " + System.IO.Path.GetFileName(f);
                    _loadBar.Value = 0;
                    try
                    {
                        IfcModel m = await Task.Run(() => IfcModel.Load(f, progress, ct), ct);
                        _loadText.Text = "Montando a vista…";
                        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                        _scene.Add(m);
                        Recent.Add(f);
                        var miss = m.Unsupported.Where(k => !k.Key.EndsWith("(erro)")).OrderByDescending(k => k.Value).Take(4).ToList();
                        if (m.FailedElements > 0 || miss.Count > 0)
                            notes.Add(m.FileName + ": " + (m.FailedElements > 0 ? m.FailedElements + " elemento(s) com erro de geometria; " : "") +
                                      (miss.Count > 0 ? "formas não lidas: " + string.Join(", ", miss.Select(k => k.Key + " ×" + k.Value)) : ""));
                        Log.Info($"{m.FileName}: {m.Schema}, {m.Objects.Count} objetos, {m.TriangleCount} triângulos, {m.LoadTime.TotalSeconds:0.0} s");
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        Log.Error("abrir " + f, ex);
                        errors.Add(System.IO.Path.GetFileName(f) + ": " + ex.Message);
                    }
                }
            }
            finally
            {
                _loadingNow = false;
                _loading.Visibility = Visibility.Collapsed;
                AfterModelsChanged(first);
            }
            if (errors.Count > 0) MessageBox.Show(this, "Não foi possível abrir:\n\n" + string.Join("\n", errors), "JA Visualizador IFC", MessageBoxButton.OK, MessageBoxImage.Warning);
            if (notes.Count > 0) Status(string.Join("   ·   ", notes));
            else if (_scene.Models.Count > 0) Status("Pronto. Clique para selecionar, arraste para girar, botão do meio / direito para mover, roda para aproximar.");
        }

        private void AfterModelsChanged(bool fit)
        {
            _keyCache = null;
            _empty.Visibility = _scene.Models.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Title = _scene.Models.Count == 0 ? "JA Visualizador IFC" : "JA Visualizador IFC — " + string.Join(" + ", _scene.Models.Select(m => m.FileName));
            ApplySmartView(_svPanel.Active, false);
            _view.InvalidateAll();
            _view.Rebuild();
            if (fit) { _view.ViewIso(); }
            _tree.SetScene(_scene);
            _sections.SetScene(_scene, _scene.AllBounds);
            _props.ShowNothing();
            UpdateCounts();
        }

        private void CloseAll()
        {
            if (_scene.Models.Count == 0) return;
            foreach (IfcModel m in _scene.Models.ToList()) _scene.Remove(m);
            _view.ClearMeasurements();
            _sections.ClearAll();
            AfterModelsChanged(false);
            GC.Collect();
        }

        private void ShowRecent()
        {
            var m = new ContextMenu();
            foreach (string f in Recent.List())
            {
                var mi = new MenuItem { Header = f };
                mi.Click += (s, e) => OpenFiles(new List<string> { f });
                m.Items.Add(mi);
            }
            if (m.Items.Count == 0) m.Items.Add(new MenuItem { Header = "(nenhum arquivo recente)", IsEnabled = false });
            m.IsOpen = true;
        }

        private void ModelsInfo()
        {
            if (_scene.Models.Count == 0) { MessageBox.Show(this, "Nenhum modelo aberto.", "Modelos"); return; }
            var sb = new StringBuilder();
            foreach (IfcModel m in _scene.Models)
            {
                StepFileInfo(sb, m);
                sb.AppendLine();
            }
            sb.AppendLine("Origem da cena (coordenadas do IFC): " + Ui.N(_scene.Origin.X, "0.###") + "; " + Ui.N(_scene.Origin.Y, "0.###") + "; " + Ui.N(_scene.Origin.Z, "0.###"));
            sb.AppendLine("Registro de erros: " + Log.Path);
            var w = new Window
            {
                Title = "Modelos", Width = 640, Height = 520, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new TextBox { Text = sb.ToString(), IsReadOnly = true, FontFamily = new FontFamily("Consolas"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(8) }
            };
            w.ShowDialog();
        }

        private static void StepFileInfo(StringBuilder sb, IfcModel m)
        {
            sb.AppendLine(m.FileName);
            sb.AppendLine("  Caminho: " + m.Path);
            sb.AppendLine("  Esquema: " + m.Schema + "    Programa: " + m.Step.OriginatingSystem + (string.IsNullOrWhiteSpace(m.Step.Preprocessor) ? "" : " (" + m.Step.Preprocessor + ")"));
            if (!string.IsNullOrWhiteSpace(m.Step.Author) || !string.IsNullOrWhiteSpace(m.Step.Organization)) sb.AppendLine("  Autor: " + m.Step.Author + "  " + m.Step.Organization);
            sb.AppendLine("  Data: " + m.Step.FileTimeStamp);
            sb.AppendLine("  Unidades: comprimento " + m.LengthUnit + ", área " + m.AreaUnit + ", volume " + m.VolumeUnit + ", ângulo " + m.AngleUnit);
            sb.AppendLine("  Instâncias no arquivo: " + m.Step.InstanceCount.ToString("N0", Ui.Br) + "    objetos: " + m.Objects.Count.ToString("N0", Ui.Br)
                          + "    com geometria: " + m.Objects.Count(o => o.HasGeometry).ToString("N0", Ui.Br) + "    triângulos: " + m.TriangleCount.ToString("N0", Ui.Br));
            sb.AppendLine("  Pavimentos: " + string.Join(", ", m.Storeys.Select(s => s.DisplayName)));
            sb.AppendLine("  Tempo de leitura: " + m.LoadTime.TotalSeconds.ToString("0.0", Ui.Br) + " s");
            if (m.FailedElements > 0) sb.AppendLine("  Elementos com erro de geometria: " + m.FailedElements);
            if (m.OpeningFailures > 0) sb.AppendLine("  Elementos com aberturas não recortadas (geometria grande demais): " + m.OpeningFailures);
            if (m.Unsupported.Count > 0)
                sb.AppendLine("  Formas não lidas: " + string.Join(", ", m.Unsupported.OrderByDescending(k => k.Value).Select(k => k.Key + " ×" + k.Value)));
        }

        // ================================================================== selection

        private void Select(IEnumerable<int> indices, bool add, bool fromTree = false)
        {
            var old = _scene.Selection.ToList();
            _scene.SetSelection(indices, add);
            SelectionChanged(old, fromTree);
        }

        private void SelectObject(IfcObject o)
        {
            var old = _scene.Selection.ToList();
            _scene.SetSelection(new[] { o.Index }, false);
            SelectionChanged(old, true);
        }

        private void SelectionChanged(List<int> old, bool fromTree)
        {
            _view.Invalidate(old.Concat(_scene.Selection));
            _props.Show(_scene, _scene.Selection);
            if (!fromTree)
            {
                if (_scene.Selection.Count == 1) _tree.Reveal(_scene.Objects[_scene.Selection.First()]);
                else _tree.Deselect();
            }
            UpdateCounts();
        }

        private void FitSelection()
        {
            if (_scene.Selection.Count > 0) _view.Fit(_scene.Selection); else _view.Fit(null);
        }

        private Bounds SelectionOrVisibleBounds()
        {
            Bounds b = _scene.BoundsOf(_scene.Selection);
            return b.IsEmpty ? _scene.BoundsOf(_scene.VisibleIndices()) : b;
        }

        private void Search(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return;
            bool Has(string s) => !string.IsNullOrEmpty(s) && s.IndexOf(text, StringComparison.CurrentCultureIgnoreCase) >= 0;
            var hits = _scene.Objects.Where(o => o.HasGeometry && (Has(o.Name) || Has(o.TypeName) || Has(o.Class) || Has(o.Tag) || Has(o.GlobalId) || Has(o.ObjectType) || Has(o.LongName)))
                                     .Select(o => o.Index).ToList();
            Select(hits, false);
            if (hits.Count > 0) _view.Fit(hits);
            Status(hits.Count == 0 ? "Nada encontrado com “" + text + "”." : hits.Count + " elemento(s) com “" + text + "”.");
        }

        // ================================================================== visibility

        private void Hide()
        {
            if (_scene.Selection.Count == 0) { Status("Selecione o que ocultar."); return; }
            var sel = _scene.Selection.ToList();
            foreach (int i in sel) _scene.UserHidden[i] = true;
            Select(new int[0], false);
            _view.Invalidate(sel);
            _tree.RefreshChecks();
            UpdateCounts();
        }

        private void Isolate()
        {
            if (_scene.Selection.Count == 0) { Status("Selecione o que isolar."); return; }
            for (int i = 0; i < _scene.Count; i++) _scene.UserHidden[i] = !_scene.Selected[i];
            _view.InvalidateAll();
            _tree.RefreshChecks();
            UpdateCounts();
        }

        private void ShowAll()
        {
            for (int i = 0; i < _scene.Count; i++) { _scene.UserHidden[i] = false; _scene.UserTransparent[i] = false; }
            foreach (IfcModel m in _scene.Models) m.Visible = true;
            _view.InvalidateAll();
            _tree.RefreshChecks();
            UpdateCounts();
        }

        private void ToggleTransparent()
        {
            if (_scene.Selection.Count == 0) { Status("Selecione o que deixar transparente."); return; }
            bool to = !_scene.Selection.All(i => _scene.UserTransparent[i]);
            var sel = _scene.Selection.ToList();
            foreach (int i in sel) _scene.UserTransparent[i] = to;
            Select(new int[0], false);
            _view.Invalidate(sel);
        }

        // ================================================================== smart views

        private void ApplySmartView(SmartView sv) => ApplySmartView(sv, true);

        private void ApplySmartView(SmartView sv, bool redraw)
        {
            Cursor = Cursors.Wait;
            try
            {
                _legendData = SmartViewEngine.Apply(_scene, sv);
                FillLegend(sv);
                if (redraw) _view.InvalidateAll();
                UpdateCounts();
                if (sv != null) Status("Smart View: " + sv.Name + " — " + _scene.VisibleIndices().Count() + " elemento(s) visível(is).");
            }
            catch (Exception ex) { Log.Error("smart view", ex); Status("Erro na Smart View: " + ex.Message); }
            finally { Cursor = null; }
        }

        private void FillLegend(SmartView sv)
        {
            if (_legendData.Count == 0) { _legend.Visibility = Visibility.Collapsed; return; }
            var sp = new StackPanel();
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var close = Ui.Small("✕", () => _legend.Visibility = Visibility.Collapsed, "Fechar a legenda");
            close.Margin = new Thickness(6, 0, 0, 0);
            DockPanel.SetDock(close, Dock.Right);
            head.Children.Add(close);
            string props = string.Join(", ", _legendData.Select(l => SvCondition.Short(l.Property)).Distinct());
            head.Children.Add(new TextBlock { Text = (sv?.Name ?? "") + (props.Length > 0 ? " — " + props : ""), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(head);
            var rows = new StackPanel();
            foreach (LegendEntry le in _legendData)
            {
                var r = new DockPanel { Margin = new Thickness(0, 1, 0, 1), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = "Clique para selecionar" };
                var sw = Ui.Swatch(le.Color, 13);
                sw.Margin = new Thickness(0, 0, 6, 0);
                DockPanel.SetDock(sw, Dock.Left);
                r.Children.Add(sw);
                var cnt = new TextBlock { Text = le.Indices.Count.ToString("N0", Ui.Br), Foreground = Ui.Muted, Margin = new Thickness(8, 0, 0, 0) };
                DockPanel.SetDock(cnt, Dock.Right);
                r.Children.Add(cnt);
                r.Children.Add(new TextBlock { Text = le.Value, TextTrimming = TextTrimming.CharacterEllipsis });
                LegendEntry entry = le;
                r.MouseLeftButtonUp += (s, e) => { Select(entry.Indices, (Keyboard.Modifiers & ModifierKeys.Control) != 0); Status(entry.Indices.Count + " elemento(s): " + entry.Value); };
                rows.Children.Add(r);
            }
            sp.Children.Add(new ScrollViewer { Content = rows, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            _legend.Child = sp;
            _legend.Visibility = Visibility.Visible;
        }

        private List<string> AllKeys()
        {
            if (_keyCache != null) return _keyCache;
            Cursor = Cursors.Wait;
            try
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (IfcObject o in _scene.Objects) if (o.HasGeometry) foreach (string k in PropertyReader.Flat(o).Keys) set.Add(k);
                var l = set.ToList();
                l.Sort((a, b) =>
                {
                    bool ea = a.StartsWith(PropertyReader.GElement + "."), eb = b.StartsWith(PropertyReader.GElement + ".");
                    if (ea != eb) return ea ? -1 : 1;
                    return string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase);
                });
                _keyCache = l;
                return l;
            }
            finally { Cursor = null; }
        }

        private List<string> ValuesOf(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return new List<string>();
            var set = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
            foreach (IfcObject o in _scene.Objects)
            {
                if (!o.HasGeometry) continue;
                PropValue v = SmartViewEngine.Lookup(PropertyReader.Flat(o), key.Trim());
                if (v != null && v.Text.Length > 0) set.Add(v.Text);
                if (set.Count > 800) break;
            }
            var l = set.ToList();
            l.Sort((a, b) =>
            {
                double? x = SmartViewEngine.ParseNumber(a), y = SmartViewEngine.ParseNumber(b);
                if (x.HasValue && y.HasValue) return x.Value.CompareTo(y.Value);
                return string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase);
            });
            return l;
        }

        // ================================================================== context menu, keys

        private ContextMenu ContextMenuFor(int i)
        {
            var m = new ContextMenu();
            void Add(string t, Action a, string key = null, bool enabled = true)
            {
                var mi = new MenuItem { Header = t, InputGestureText = key ?? "", IsEnabled = enabled };
                mi.Click += (s, e) => a();
                m.Items.Add(mi);
            }
            bool sel = _scene.Selection.Count > 0;
            IfcObject o = i >= 0 ? _scene.Objects[i] : null;
            Add("Ocultar", Hide, "H", sel);
            Add("Isolar", Isolate, "I", sel);
            Add("Transparente / opaco", ToggleTransparent, "T", sel);
            Add("Mostrar tudo", ShowAll, "Shift+H");
            m.Items.Add(new Separator());
            if (o != null)
            {
                Add("Selecionar a mesma classe (" + o.Class + ")", () => Select(_scene.Objects.Where(x => x.HasGeometry && x.Class == o.Class).Select(x => x.Index), false));
                if (o.TypeName != null) Add("Selecionar o mesmo tipo (" + o.TypeName + ")", () => Select(_scene.Objects.Where(x => x.HasGeometry && x.TypeId == o.TypeId && x.Model == o.Model).Select(x => x.Index), false));
                if (o.Storey != null) Add("Selecionar o mesmo pavimento (" + o.Storey.DisplayName + ")", () => Select(_scene.Objects.Where(x => x.HasGeometry && x.Storey == o.Storey).Select(x => x.Index), false));
                Add("Mostrar na árvore", () => _tree.Reveal(o));
                m.Items.Add(new Separator());
            }
            Add("Enquadrar a seleção", FitSelection, "F", sel);
            Add("Caixa de corte na seleção", () => _sections.FitBoxTo(_scene.BoundsOf(_scene.Selection)), null, sel);
            Add("Plano de corte numa face…", () => _view.Mode = MeasureMode.PickPlane);
            if (o != null)
            {
                m.Items.Add(new Separator());
                Add("Copiar GlobalId", () => { try { Clipboard.SetText(o.GlobalId ?? ""); } catch { } });
                Add("Copiar nome", () => { try { Clipboard.SetText(o.DisplayName ?? ""); } catch { } });
            }
            return m;
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            if (e.OriginalSource is TextBox || e.OriginalSource is ComboBox) return;
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0, shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            switch (e.Key)
            {
                case Key.O when ctrl: OpenDialog(); break;
                case Key.Escape:
                    if (_view.Mode != MeasureMode.None) _view.CancelCurrent();
                    else Select(new int[0], false);
                    break;
                case Key.H when shift: ShowAll(); break;
                case Key.H: case Key.Delete: Hide(); break;
                case Key.I: Isolate(); break;
                case Key.T: ToggleTransparent(); break;
                case Key.F: FitSelection(); break;
                case Key.A when ctrl: Select(_scene.VisibleIndices().ToList(), false); break;
                case Key.D: _view.Mode = _view.Mode == MeasureMode.Distance ? MeasureMode.None : MeasureMode.Distance; break;
                case Key.R: _view.Mode = _view.Mode == MeasureMode.Area ? MeasureMode.None : MeasureMode.Area; break;
                case Key.C when !ctrl: _view.Mode = _view.Mode == MeasureMode.Point ? MeasureMode.None : MeasureMode.Point; break;
                case Key.C when ctrl:
                    if (_scene.Selection.Count == 1) { try { Clipboard.SetText(_scene.Objects[_scene.Selection.First()].GlobalId ?? ""); } catch { } Status("GlobalId copiado."); }
                    break;
                case Key.P: _view.Orthographic = !_view.Orthographic; _tPersp.IsChecked = _view.Orthographic; break;
                case Key.X: _tXRay.IsChecked = !(_tXRay.IsChecked == true); _scene.XRay = _tXRay.IsChecked == true; _view.InvalidateAll(); break;
                case Key.D1 when ctrl: _view.ViewIso(); break;
                case Key.D2 when ctrl: _view.ViewTop(); break;
                case Key.D3 when ctrl: _view.ViewFront(); break;
                case Key.D4 when ctrl: _view.ViewRight(); break;
                case Key.D5 when ctrl: _view.ViewBack(); break;
                case Key.D6 when ctrl: _view.ViewLeft(); break;
                default: return;
            }
            e.Handled = true;
        }

        // ================================================================== misc

        private void SaveImage()
        {
            if (_scene.Models.Count == 0) return;
            try
            {
                BitmapSource img = _view.Snapshot(2);
                try { Clipboard.SetImage(img); } catch { }
                var d = new Microsoft.Win32.SaveFileDialog { Filter = "Imagem PNG (*.png)|*.png", FileName = System.IO.Path.GetFileNameWithoutExtension(_scene.Models[0].FileName) + ".png" };
                if (d.ShowDialog(this) != true) { Status("Imagem copiada para a área de transferência."); return; }
                using (var fs = System.IO.File.Create(d.FileName))
                {
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(img));
                    enc.Save(fs);
                }
                Status("Imagem salva em " + d.FileName + " (e copiada).");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Imagem"); }
        }

        private void HelpDialog()
        {
            string t =
                "NAVEGAR\n" +
                "  Arrastar com o botão esquerdo: girar em torno do ponto clicado\n" +
                "  Botão do meio, botão direito ou Shift+esquerdo: mover\n" +
                "  Roda: aproximar / afastar em direção ao cursor\n" +
                "  Duplo clique: enquadrar o elemento (no vazio: tudo)\n" +
                "  F enquadrar seleção · P perspectiva/ortogonal · Ctrl+1 3D · Ctrl+2 topo · Ctrl+3 frente · Ctrl+4 direita · Ctrl+5 fundos · Ctrl+6 esquerda\n\n" +
                "SELECIONAR\n" +
                "  Clique: seleciona · Ctrl+clique: soma/tira · Esc: limpa · Ctrl+A: todos os visíveis\n" +
                "  Clique direito: ocultar, isolar, selecionar mesma classe/tipo/pavimento, cortes, copiar GlobalId\n" +
                "  Árvore: as caixas de seleção ocultam e mostram; duplo clique enquadra; Buscar por nome, tipo, tag ou GlobalId\n\n" +
                "VISIBILIDADE\n" +
                "  H ou Delete ocultar · I isolar · Shift+H mostrar tudo · T transparente · X raio-X\n\n" +
                "SMART VIEWS\n" +
                "  Regras de cima para baixo: Mostrar, Ocultar, Colorir, Colorir por valor (legenda clicável), Transparente, Opaco, Cor original.\n" +
                "  Condições sobre qualquer propriedade: Elemento.*, Material.Nome, Classificação.Código, Pset_….Propriedade ou *.Propriedade\n" +
                "  Clique direito numa propriedade: selecionar quem tem o mesmo valor, colorir por ela, criar regra\n" +
                "  As suas ficam em " + SmartViewEngine.StorePath + " (Exportar/Importar para passar à equipe)\n\n" +
                "CORTES E MEDIÇÕES\n" +
                "  Caixa de corte, planos (horizontal, vertical, pela face) e planta do pavimento na aba Cortes\n" +
                "  D distância (atrai vértices, meios e arestas; mostra ΔX ΔY ΔZ) · R área da face · C coordenada e cota · Esc termina\n";
            var w = new Window
            {
                Title = "Ajuda — JA Visualizador IFC", Width = 760, Height = 600, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new TextBox { Text = t, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(10), BorderThickness = new Thickness(0) }
            };
            w.ShowDialog();
        }

        private void Status(string s) => _status.Text = s ?? "";

        private void UpdateCounts()
        {
            int geo = _scene.Objects.Count(o => o.HasGeometry);
            int vis = _scene.VisibleIndices().Count();
            _counts.Text = _scene.Models.Count == 0 ? "" :
                $"{_scene.Models.Count} modelo(s) · {geo.ToString("N0", Ui.Br)} elementos · {vis.ToString("N0", Ui.Br)} visíveis · {_scene.Selection.Count.ToString("N0", Ui.Br)} selecionado(s)";
        }
    }

    /// <summary>Recently opened files (%AppData%\JA\VisualizadorIFC\recentes.txt).</summary>
    public static class Recent
    {
        private static string File => System.IO.Path.Combine(Log.Folder, "recentes.txt");

        public static List<string> List()
        {
            try { return System.IO.File.Exists(File) ? System.IO.File.ReadAllLines(File).Where(l => l.Trim().Length > 0 && System.IO.File.Exists(l)).Distinct().Take(12).ToList() : new List<string>(); }
            catch { return new List<string>(); }
        }

        public static void Add(string path)
        {
            try
            {
                var l = List();
                l.RemoveAll(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase));
                l.Insert(0, path);
                System.IO.Directory.CreateDirectory(Log.Folder);
                System.IO.File.WriteAllLines(File, l.Take(12));
            }
            catch { }
        }
    }
}
