using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using RevitDwgUnifier.Tools.Common;
using RevitDwgUnifier.UI;

namespace RevitDwgUnifier.Tools.Detail
{
    /// <summary>A view on the sheet, in millimetres of paper (sheet coordinates, Y up).</summary>
    public sealed class LayoutBox
    {
        public string Name, Detail;
        public string Short;           // short label in the preview ("Planta", "Elevação A"...)
        public List<double[]> Sketch;  // silhouette of the drawing: polylines x0,y0,x1,y1,... (any units, Y up)
        public double W, H;            // size
        public double X, Y;            // centre
        public bool Fixed;             // a viewport already on the sheet (not moved)
        public object Tag;
        public int Index = -1;         // free boxes: position in the caller's list of viewports
        public int Scale, OrigScale;   // 1:Scale (0 = no scale, e.g. a schedule)
        public bool Excluded;          // the user took it off the sheet

        /// <summary>The viewport title (label) relative to the bottom-left corner of the box; HasLabel = false: none.</summary>
        public double LabX0, LabY0, LabX1, LabY1;
        public bool HasLabel;
        public string LabelText;

        public double L => X - W / 2;
        public double R => X + W / 2;
        public double B => Y - H / 2;
        public double T => Y + H / 2;

        // footprint = box + its title: what must not touch the other views nor leave the usable area
        public double FL => HasLabel ? Math.Min(L, L + LabX0) : L;
        public double FR => HasLabel ? Math.Max(R, L + LabX1) : R;
        public double FB => HasLabel ? Math.Min(B, B + LabY0) : B;
        public double FT => HasLabel ? Math.Max(T, B + LabY1) : T;

        public bool Overlaps(LayoutBox o, double gap) =>
            FL < o.FR + gap - 1e-6 && o.FL < FR + gap - 1e-6 && FB < o.FT + gap - 1e-6 && o.FB < FT + gap - 1e-6;

        public bool Inside(double left, double top, double right, double bottom) =>
            FL >= left - 0.01 && FR <= right + 0.01 && FB >= bottom - 0.01 && FT <= top + 0.01;

        /// <summary>New scale: the box grows or shrinks around its centre (size ∝ 1/scale).</summary>
        public void SetScale(int scale)
        {
            if (scale <= 0 || Scale <= 0 || scale == Scale) return;
            double k = (double)Scale / scale;
            W *= k; H *= k;
            Scale = scale;
        }
    }

    /// <summary>
    /// First-fit placement of the views on a sheet: each free box goes to the highest, then
    /// left-most spot of the usable area where it touches nothing (fixed viewports included), so
    /// the views fill the sheet in rows from the top left, the small ones filling the gaps.
    /// </summary>
    public static class LayoutEngine
    {
        public static bool Pack(IList<LayoutBox> boxes, double left, double top, double right, double bottom, double gap)
        {
            var placed = boxes.Where(b => b.Fixed).ToList();
            bool all = true;
            const double step = 2;
            foreach (LayoutBox b in boxes.Where(b => !b.Fixed && !b.Excluded))
            {
                // work with the footprint (box + title): offsets of the box centre from the footprint corner
                double fw = b.FR - b.FL, fh = b.FT - b.FB;
                double ox = b.X - b.FL, oy = b.Y - b.FT;
                bool found = false;
                for (double ft = top; ft - fh >= bottom - 1e-6 && !found; ft -= step)
                {
                    double fl = left;
                    while (fl + fw <= right + 1e-6)
                    {
                        b.X = fl + ox; b.Y = ft + oy;
                        LayoutBox hit = placed.FirstOrDefault(p => b.Overlaps(p, gap));
                        if (hit == null) { found = true; break; }
                        fl = Math.Max(fl + step, hit.FR + gap);
                    }
                }
                if (!found)
                {
                    all = false;
                    double low = placed.Count > 0 ? Math.Min(bottom, placed.Min(p => p.FB)) : bottom;
                    b.X = left + ox;
                    b.Y = low - gap + oy;
                }
                placed.Add(b);
            }
            return all;
        }
    }

    public enum LayoutAnswer { Apply, CancelAll }

    /// <summary>What the preview may ask Revit while it is open (the command's transaction is open).</summary>
    public interface ISheetLayoutHost
    {
        /// <summary>Title of the current sheet ("D101 - DETALHAMENTO - ...").</summary>
        string SheetTitle { get; }
        IList<string> Sheets { get; }
        int SheetIndex { get; }
        IList<string> TitleBlocks { get; }
        int TitleBlockIndex { get; }
        /// <summary>Moves the new views to another sheet; returns its paper and what is already on it.</summary>
        bool SwitchSheet(int index, out double[] paper, out List<LayoutBox> fixedBoxes, out string error);
        /// <summary>Changes the title block of the current sheet; returns the new paper.</summary>
        bool ChangeTitleBlock(int index, out double[] paper, out string error);
        /// <summary>Lines of the title block of the current sheet (polylines x0,y0,x1,y1,... in mm).</summary>
        IList<double[]> TitleBlockLines();
    }

    /// <summary>
    /// Sheet editor shown before the views are placed: the paper with the usable area (margins) and
    /// every view at its real size. Tools: drag (snap to margins and to the other views), select
    /// several (Ctrl+click or a selection window), align / distribute / centre, change the scale of
    /// the selected views (the box follows), fit a scale so everything fits, keep a view off the
    /// sheet, zoom and text size, margins, another title block, another sheet, undo / redo.
    /// Red = outside the usable area or over another view.
    /// </summary>
    public sealed class SheetLayoutDialog
    {
        private static readonly int[] StdScales = { 1, 2, 5, 10, 20, 25, 50, 75, 100, 125, 150, 200, 250, 500 };
        private const double Pad = 24;   // px around the paper

        private readonly List<LayoutBox> _boxes;
        private readonly ISheetLayoutHost _host;
        private readonly double[] _margins;         // left, top, right, bottom (mm), edited in place
        private double[] _paper;                    // x0, y0, x1, y1 (mm)
        private readonly double _gap;

        private Window _win;
        private Canvas _canvas;
        private ScrollViewer _scroll;
        private TextBlock _status, _title, _selInfo;
        private ComboBox _scaleBox, _sheetBox, _tbBox;
        private CheckBox _exclude, _snap;
        private ListBox _list;
        private TextBox[] _marginBoxes;
        private Slider _font;
        private Brush _accent, _accentSoft;
        private bool _busy;                          // ignore UI events while refreshing controls
        private IList<double[]> _tbLines;            // the title block drawn on the paper

        private double _fitK = 1, _zoom = 1;
        private double K => _fitK * _zoom;          // px per mm
        private readonly HashSet<LayoutBox> _sel = new HashSet<LayoutBox>();
        private LayoutBox _dragLead;
        private Point _dragStartMm;
        private Dictionary<LayoutBox, Point> _dragOrig;
        private bool _dragMoved;
        private Point? _bandStart;
        private Rectangle _band;
        private readonly Stack<List<double[]>> _undo = new Stack<List<double[]>>();
        private readonly Stack<List<double[]>> _redo = new Stack<List<double[]>>();

        private static readonly Brush Red = Frozen(Color.FromRgb(0xD9, 0x3B, 0x3B));
        private static readonly Brush RedSoft = Frozen(Color.FromArgb(0x33, 0xD9, 0x3B, 0x3B));
        private static readonly Brush Gray = Frozen(Color.FromRgb(0x9A, 0xA0, 0xA6));
        private static readonly Brush GraySoft = Frozen(Color.FromArgb(0x30, 0x9A, 0xA0, 0xA6));
        private static readonly Brush Ink = Frozen(Color.FromRgb(0x1F, 0x2A, 0x33));

        private static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private SheetLayoutDialog(List<LayoutBox> boxes, ISheetLayoutHost host, double[] paper, double[] margins, double gap)
        {
            _boxes = boxes;
            _host = host;
            _paper = paper;
            _margins = margins;
            _gap = gap;
        }

        private double Left => _paper[0] + _margins[0];
        private double Top => _paper[3] - _margins[1];
        private double Right => _paper[2] - _margins[2];
        private double Bottom => _paper[1] + _margins[3];
        private IEnumerable<LayoutBox> Free => _boxes.Where(b => !b.Fixed);

        /// <param name="paper">x0, y0, x1, y1 of the title block (mm).</param>
        /// <param name="margins">left, top, right, bottom (mm); the values set in the window come back here.</param>
        /// <param name="reuseDefault">Initial state of "repeat this layout on the next sheets".</param>
        public static LayoutAnswer Show(IntPtr owner, ISheetLayoutHost host, string subtitle, double[] paper, double[] margins,
                                        List<LayoutBox> boxes, double gap, bool reuseDefault, out bool dontAskAgain, out bool reuseLayout)
        {
            ToolProgress.Hide();
            var d = new SheetLayoutDialog(boxes, host, paper, margins, gap);
            return d.Run(owner, subtitle, reuseDefault, out dontAskAgain, out reuseLayout);
        }

        // =====================================================================================
        // window
        // =====================================================================================
        private LayoutAnswer Run(IntPtr owner, string subtitle, bool reuseDefault, out bool dontAskAgain, out bool reuseLayout)
        {
            var info = ToolCatalog.Get("Detalhar Ambientes");
            _win = new Window
            {
                Title = "JA Tools — Pré-visualização da prancha",
                Width = Math.Min(1400, SystemParameters.WorkArea.Width * 0.94),
                Height = Math.Min(920, SystemParameters.WorkArea.Height * 0.94),
                MinWidth = 900, MinHeight = 560,
                WindowStartupLocation = owner != IntPtr.Zero ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                ShowInTaskbar = false
            };
            JaTheme.Apply(_win, info.Accent);
            if (owner != IntPtr.Zero) new WindowInteropHelper(_win).Owner = owner;
            _accent = new SolidColorBrush(info.Accent);
            _accentSoft = new SolidColorBrush(Color.FromArgb(0x30, info.Accent.R, info.Accent.G, info.Accent.B));

            var root = new DockPanel();
            var head = BuildHeader(subtitle);
            DockPanel.SetDock(head, Dock.Top);
            root.Children.Add(head);
            var tools = BuildToolbar();
            DockPanel.SetDock(tools, Dock.Top);
            root.Children.Add(tools);
            var legend = BuildLegend();
            DockPanel.SetDock(legend, Dock.Top);
            root.Children.Add(legend);
            LoadTitleBlock();

            LayoutAnswer answer = LayoutAnswer.Apply;
            var foot = BuildFooter(out Button apply, out Button cancel, out CheckBox skip, out CheckBox reuseBox);
            reuseBox.IsChecked = reuseDefault;
            DockPanel.SetDock(foot, Dock.Bottom);
            root.Children.Add(foot);

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
            _canvas = new Canvas { Background = Brushes.Transparent, ClipToBounds = true };
            _scroll = new ScrollViewer
            {
                Content = _canvas, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = true
            };
            _scroll.SetResourceReference(Control.BackgroundProperty, "Ja.Surface");
            _scroll.SizeChanged += (s, e) => { ComputeFit(); Redraw(); };
            _scroll.PreviewMouseWheel += OnWheel;
            body.Children.Add(_scroll);
            var side = BuildSidePanel();
            Grid.SetColumn(side, 1);
            body.Children.Add(side);
            root.Children.Add(body);
            _win.Content = root;

            _canvas.MouseLeftButtonDown += OnDown;
            _canvas.MouseMove += OnMove;
            _canvas.MouseLeftButtonUp += OnUp;
            _win.PreviewKeyDown += OnKey;
            apply.Click += (s, e) => { answer = LayoutAnswer.Apply; _win.DialogResult = true; };
            cancel.Click += (s, e) => { answer = LayoutAnswer.CancelAll; _win.DialogResult = false; };

            RefreshSide();
            _win.ShowDialog();
            dontAskAgain = skip.IsChecked == true;
            reuseLayout = reuseBox.IsChecked == true;
            return answer;
        }

        private FrameworkElement BuildHeader(string subtitle)
        {
            var head = new Border { Padding = new Thickness(20, 12, 20, 10), BorderThickness = new Thickness(0, 0, 0, 1) };
            head.SetResourceReference(Border.BackgroundProperty, "Ja.Card");
            head.SetResourceReference(Border.BorderBrushProperty, "Ja.Line");
            var hs = new StackPanel();
            var crumb = new TextBlock { Text = "JA TOOLS  ›  DETALHAMENTO  ›  PRÉ-VISUALIZAÇÃO", FontSize = 10.5, FontWeight = FontWeights.Bold };
            crumb.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Accent");
            hs.Children.Add(crumb);
            _title = new TextBlock { Text = "Prancha " + (_host?.SheetTitle ?? ""), FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 2) };
            _title.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Ink");
            hs.Children.Add(_title);
            var t2 = new TextBlock
            {
                Text = subtitle + "  Arraste para mover · Ctrl+clique ou janela para selecionar várias · setas movem 1 mm (Shift 10 mm) · Ctrl+roda = zoom · Ctrl+Z desfaz.",
                TextWrapping = TextWrapping.Wrap
            };
            t2.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Muted");
            hs.Children.Add(t2);
            head.Child = hs;
            return head;
        }

        private readonly List<Button> _needSel = new List<Button>();     // align: 1+ views
        private readonly List<Button> _needThree = new List<Button>();   // distribute: 3+ views

        private Button Tool(string text, string tip, Action click, bool prominent = false)
        {
            var b = new Button { Content = text, ToolTip = tip, Margin = new Thickness(0, 0, 4, 0), FontSize = 12.5 };
            b.SetResourceReference(FrameworkElement.StyleProperty, prominent ? "Ja.Primary" : "Ja.Secondary");
            b.Padding = new Thickness(10, 5, 10, 5);
            b.MinHeight = 30;
            b.Click += (s, e) => { click(); _scroll?.Focus(); };
            return b;
        }

        private static TextBlock Label(string text)
        {
            var t = new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 4), FontSize = 10.5, FontWeight = FontWeights.Bold };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Muted");
            return t;
        }

        /// <summary>A labelled group of tools ("MONTAGEM AUTOMÁTICA", "ALINHAR"...).</summary>
        private static FrameworkElement Group(string title, params UIElement[] items)
        {
            var g = new Border { Padding = new Thickness(10, 6, 8, 8), Margin = new Thickness(0, 0, 8, 6), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
            g.SetResourceReference(Border.BorderBrushProperty, "Ja.Line");
            g.SetResourceReference(Border.BackgroundProperty, "Ja.Surface");
            var s = new StackPanel();
            s.Children.Add(Label(title));
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var i in items) row.Children.Add(i);
            s.Children.Add(row);
            g.Child = s;
            return g;
        }

        private FrameworkElement BuildToolbar()
        {
            var bar = new Border { Padding = new Thickness(14, 8, 14, 2), BorderThickness = new Thickness(0, 0, 0, 1) };
            bar.SetResourceReference(Border.BackgroundProperty, "Ja.Card");
            bar.SetResourceReference(Border.BorderBrushProperty, "Ja.Line");
            var w = new WrapPanel();

            w.Children.Add(Group("MONTAGEM AUTOMÁTICA",
                Tool("▦  Organizar automaticamente", "Coloca TODAS as vistas sozinho, em filas a partir do canto superior esquerdo da área útil, sem sobrepor", AutoArrange, true),
                Tool("⤢  Escala que cabe", "Procura a maior escala padrão (1:20, 1:25, 1:50...) em que as vistas — selecionadas ou todas — cabem na área útil, e organiza", FitScale),
                Tool("⊡  Centralizar", "Centraliza o conjunto (selecionadas ou todas) na área útil da folha", CenterGroup)));

            Button a0 = Tool("⇤ Esquerda", "Alinha as selecionadas pela esquerda (uma vista sozinha: encosta na margem esquerda)", () => Align(0));
            Button a1 = Tool("↔ Centro", "Centraliza na horizontal (uma vista: no meio da folha)", () => Align(1));
            Button a2 = Tool("Direita ⇥", "Alinha pela direita (uma vista: na margem direita)", () => Align(2));
            Button a3 = Tool("⤒ Topo", "Alinha pelo topo (uma vista: na margem superior)", () => Align(3));
            Button a4 = Tool("↕ Meio", "Centraliza na vertical", () => Align(4));
            Button a5 = Tool("⤓ Base", "Alinha pela base (uma vista: na margem inferior)", () => Align(5));
            _needSel.AddRange(new[] { a0, a1, a2, a3, a4, a5 });
            w.Children.Add(Group("ALINHAR AS SELECIONADAS", a0, a1, a2, a3, a4, a5));

            Button dh = Tool("⇹ Horizontal", "Espaços iguais entre as selecionadas, da esquerda para a direita (3 ou mais vistas)", () => Distribute(true));
            Button dv = Tool("⇳ Vertical", "Espaços iguais entre as selecionadas, de cima para baixo (3 ou mais vistas)", () => Distribute(false));
            _needThree.AddRange(new[] { dh, dv });
            w.Children.Add(Group("DISTRIBUIR", dh, dv));

            _font = new Slider { Minimum = 9, Maximum = 30, Value = 14, Width = 90, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0), ToolTip = "Tamanho dos nomes das vistas na pré-visualização (texto grande)" };
            _font.ValueChanged += (s, e) => Redraw();
            var fontLabel = new TextBlock { Text = "Aa", FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), ToolTip = "Tamanho do texto" };
            fontLabel.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Ink");
            _snap = new CheckBox { Content = "Encaixe", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), ToolTip = "Ao arrastar, encosta nas margens e nas bordas e centros das outras vistas" };
            w.Children.Add(Group("VER",
                Tool("−", "Afastar (Ctrl + roda do mouse)", () => SetZoom(_zoom / 1.25)),
                Tool("Folha inteira", "Mostra a folha inteira (tecla 0)", () => SetZoom(1)),
                Tool("+", "Aproximar (Ctrl + roda do mouse)", () => SetZoom(_zoom * 1.25)),
                fontLabel, _font, _snap));

            w.Children.Add(Group("EDITAR",
                Tool("↶ Desfazer", "Desfaz a última mudança (Ctrl+Z)", Undo),
                Tool("↷ Refazer", "Refaz (Ctrl+Y)", Redo)));
            bar.Child = w;
            return bar;
        }

        /// <summary>How to use + what the colours mean.</summary>
        private FrameworkElement BuildLegend()
        {
            var strip = new Border { Padding = new Thickness(16, 7, 16, 7), BorderThickness = new Thickness(0, 0, 0, 1) };
            strip.SetResourceReference(Border.BackgroundProperty, "Ja.AccentSoft");
            strip.SetResourceReference(Border.BorderBrushProperty, "Ja.Line");
            var w = new WrapPanel();
            var how = new TextBlock { Text = "Como montar:  ① Organizar automaticamente ou arraste as vistas   ② escala e \"não colocar\" no painel à direita   ③ Aplicar layout.", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 24, 0) };
            how.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Ink");
            w.Children.Add(how);
            w.Children.Add(Swatch(_accentSoft, _accent, "vista nova (arraste)", false));
            w.Children.Add(Swatch(GraySoft, Gray, "já está na prancha", false));
            w.Children.Add(Swatch(RedSoft, Red, "fora da área útil / sobreposta", false));
            w.Children.Add(Swatch(Brushes.Transparent, _accent, "área útil", true));
            w.Children.Add(Swatch(MarginFill, Gray, "margem do carimbo", false));
            w.Children.Add(Swatch(TitleBack, Gray, "título da vista (também não pode sobrepor)", false));
            strip.Child = w;
            return strip;
        }

        private static FrameworkElement Swatch(Brush fill, Brush stroke, string text, bool dashed)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
            var r = new Rectangle { Width = 18, Height = 12, Fill = fill, Stroke = stroke, StrokeThickness = 1.4, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            if (dashed) r.StrokeDashArray = new DoubleCollection { 3, 2 };
            p.Children.Add(r);
            var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Text");
            p.Children.Add(t);
            return p;
        }

        private FrameworkElement Card(string title, params UIElement[] content)
        {
            var b = new Border { Padding = new Thickness(12, 10, 12, 12), Margin = new Thickness(0, 0, 0, 10), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
            b.SetResourceReference(Border.BackgroundProperty, "Ja.Card");
            b.SetResourceReference(Border.BorderBrushProperty, "Ja.Line");
            var s = new StackPanel();
            var t = new TextBlock { Text = title, FontSize = 10.5, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Accent");
            s.Children.Add(t);
            foreach (var c in content) s.Children.Add(c);
            b.Child = s;
            return b;
        }

        private static TextBlock Small(string text)
        {
            var t = new TextBlock { Text = text, FontSize = 11, Margin = new Thickness(0, 6, 0, 2) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Muted");
            return t;
        }

        private FrameworkElement BuildSidePanel()
        {
            var stack = new StackPanel { Margin = new Thickness(0, 12, 12, 12) };

            // ---- sheet
            var sheetItems = new List<UIElement>();
            if (_host != null && _host.Sheets != null && _host.Sheets.Count > 0)
            {
                sheetItems.Add(Small("Prancha"));
                _sheetBox = new ComboBox { ItemsSource = _host.Sheets, ToolTip = "Coloca as vistas em outra prancha (as que já estão nela ficam em cinza)" };
                _sheetBox.SelectionChanged += (s, e) => { if (!_busy) SwitchSheet(_sheetBox.SelectedIndex); };
                sheetItems.Add(_sheetBox);
            }
            if (_host != null && _host.TitleBlocks != null && _host.TitleBlocks.Count > 0)
            {
                sheetItems.Add(Small("Carimbo (formato da folha)"));
                _tbBox = new ComboBox { ItemsSource = _host.TitleBlocks, ToolTip = "Troca o carimbo desta prancha (A0, A1, A2...)" };
                _tbBox.SelectionChanged += (s, e) => { if (!_busy) ChangeTitleBlock(_tbBox.SelectedIndex); };
                sheetItems.Add(_tbBox);
            }
            sheetItems.Add(Small("Margens (mm): esquerda, topo, direita, base"));
            var mg = new UniformGrid { Columns = 4 };
            _marginBoxes = new TextBox[4];
            for (int i = 0; i < 4; i++)
            {
                int k = i;
                var tb = new TextBox { Margin = new Thickness(0, 0, 4, 0), Padding = new Thickness(4, 2, 4, 2) };
                tb.LostFocus += (s, e) => ReadMargin(k);
                tb.KeyDown += (s, e) => { if (e.Key == Key.Enter) { ReadMargin(k); e.Handled = true; } };
                _marginBoxes[i] = tb;
                mg.Children.Add(tb);
            }
            sheetItems.Add(mg);
            stack.Children.Add(Card("PRANCHA", sheetItems.ToArray()));

            // ---- selected view(s)
            _selInfo = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
            _selInfo.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Ink");
            _scaleBox = new ComboBox { IsEditable = true, ItemsSource = StdScales.Select(s => "1:" + s).ToList(), ToolTip = "Escolha ou digite (ex.: 1:30) e clique em Aplicar escala" };
            var scaleRow = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
            var applyScale = new Button { Content = "Aplicar escala", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 3, 8, 3) };
            applyScale.SetResourceReference(FrameworkElement.StyleProperty, "Ja.Secondary");
            applyScale.Padding = new Thickness(8, 3, 8, 3);
            applyScale.Click += (s, e) => ApplyScaleFromBox();
            DockPanel.SetDock(applyScale, Dock.Right);
            scaleRow.Children.Add(applyScale);
            scaleRow.Children.Add(_scaleBox);
            _scaleBox.KeyDown += (s, e) => { if (e.Key == Key.Enter) { ApplyScaleFromBox(); e.Handled = true; } };
            _exclude = new CheckBox { Content = "Não colocar na prancha", Margin = new Thickness(0, 10, 0, 0), ToolTip = "A vista continua no projeto, só não entra nesta prancha (Delete)" };
            _exclude.Click += (s, e) => SetExcluded(_exclude.IsChecked == true);
            var selAll = new Button { Content = "Selecionar todas (Ctrl+A)", Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            selAll.SetResourceReference(FrameworkElement.StyleProperty, "Ja.Ghost");
            selAll.Click += (s, e) => { _sel.Clear(); foreach (var b in Free) _sel.Add(b); RefreshSide(); };
            stack.Children.Add(Card("VISTA SELECIONADA", _selInfo, Small("Escala"), scaleRow, _exclude, selAll));

            // ---- list of views
            _list = new ListBox { MaxHeight = 260, SelectionMode = SelectionMode.Extended, BorderThickness = new Thickness(0) };
            _list.SelectionChanged += (s, e) =>
            {
                if (_busy) return;
                _sel.Clear();
                foreach (var o in _list.SelectedItems) if (o is ListBoxItem li && li.Tag is LayoutBox b) _sel.Add(b);
                RefreshSide();
            };
            stack.Children.Add(Card("VISTAS NESTA PRANCHA", _list));

            return new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }

        private FrameworkElement BuildFooter(out Button apply, out Button cancel, out CheckBox skip, out CheckBox reuse)
        {
            var foot = new Border { Padding = new Thickness(20, 10, 20, 12), BorderThickness = new Thickness(0, 1, 0, 0) };
            foot.SetResourceReference(Border.BorderBrushProperty, "Ja.Line");
            var bar = new DockPanel();
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(right, Dock.Right);
            cancel = new Button { Content = "Desfazer tudo", MinWidth = 110, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Cancela o Detalhar Ambientes: nenhuma vista ou prancha fica no projeto" };
            cancel.SetResourceReference(FrameworkElement.StyleProperty, "Ja.Secondary");
            apply = new Button { Content = "✓  Aplicar e colocar na prancha", MinWidth = 210, IsDefault = true, ToolTip = "Coloca as vistas na prancha exatamente como estão aqui (Enter)" };
            apply.SetResourceReference(FrameworkElement.StyleProperty, "Ja.Primary");
            right.Children.Add(cancel);
            right.Children.Add(apply);
            bar.Children.Add(right);
            var leftBar = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 700 };
            _status.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Text");
            leftBar.Children.Add(_status);
            var checks = new StackPanel { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            reuse = new CheckBox
            {
                Content = "Repetir este layout (posições, escalas) nas próximas pranchas",
                ToolTip = "As próximas pranchas desta execução são montadas sozinhas com este layout; só aparecem aqui se não couberem"
            };
            skip = new CheckBox { Content = "Não mostrar a pré-visualização nas próximas pranchas", Margin = new Thickness(0, 4, 0, 0) };
            checks.Children.Add(reuse);
            checks.Children.Add(skip);
            leftBar.Children.Add(checks);
            bar.Children.Add(leftBar);
            foot.Child = bar;
            return foot;
        }

        // =====================================================================================
        // drawing
        // =====================================================================================
        private double Px(double xmm) => Pad + (xmm - _paper[0]) * K;
        private double Py(double ymm) => Pad + (_paper[3] - ymm) * K;
        private Point ToMm(Point p) => new Point(_paper[0] + (p.X - Pad) / K, _paper[3] - (p.Y - Pad) / K);

        private void ComputeFit()
        {
            double pw = Math.Max(10, _paper[2] - _paper[0]), ph = Math.Max(10, _paper[3] - _paper[1]);
            double vw = Math.Max(200, _scroll.ViewportWidth > 0 ? _scroll.ViewportWidth : _scroll.ActualWidth);
            double vh = Math.Max(200, _scroll.ViewportHeight > 0 ? _scroll.ViewportHeight : _scroll.ActualHeight);
            _fitK = Math.Max(0.05, Math.Min((vw - 2 * Pad - 4) / pw, (vh - 2 * Pad - 4) / ph));
        }

        private void SetZoom(double z)
        {
            _zoom = Math.Max(0.5, Math.Min(12, z));
            Redraw();
        }

        private void Redraw()
        {
            if (_canvas == null) return;
            _canvas.Children.Clear();
            double pw = (_paper[2] - _paper[0]) * K, ph = (_paper[3] - _paper[1]) * K;
            _canvas.Width = pw + 2 * Pad;
            _canvas.Height = ph + 2 * Pad;

            // the fixed layer (paper, margins, title block, usable area) is built once per zoom /
            // sheet / margins and reused: dragging a view no longer redraws thousands of title block lines
            string skey = StaticKey();
            if (skey == _staticKey && _staticEls != null)
                foreach (UIElement el in _staticEls) _canvas.Children.Add(el);
            else
            {
            // paper
            var paper = new Rectangle { Width = pw, Height = ph, Fill = Brushes.White, Stroke = Gray, StrokeThickness = 1, Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.25 } };
            Canvas.SetLeft(paper, Pad); Canvas.SetTop(paper, Pad);
            _canvas.Children.Add(paper);

            // margins: the strips outside the usable area, shaded, with their size
            AddRect(_paper[0], _paper[3], Left, _paper[1]);              // left
            AddRect(Right, _paper[3], _paper[2], _paper[1]);             // right
            AddRect(Left, _paper[3], Right, Top);                         // top
            AddRect(Left, Bottom, Right, _paper[1]);                      // bottom

            // the title block itself (its lines, as drawn on the sheet)
            if (_tbLines != null && _tbLines.Count > 0)
            {
                var g = new StreamGeometry();
                using (StreamGeometryContext c = g.Open())
                {
                    foreach (double[] pl in _tbLines)
                    {
                        if (pl.Length < 4) continue;
                        c.BeginFigure(new Point(Px(pl[0]), Py(pl[1])), false, false);
                        for (int i = 2; i + 1 < pl.Length; i += 2) c.LineTo(new Point(Px(pl[i]), Py(pl[i + 1])), true, false);
                    }
                }
                g.Freeze();
                _canvas.Children.Add(new Path { Data = g, Stroke = TbInk, StrokeThickness = 0.8, IsHitTestVisible = false });
            }

            // usable area
            var area = new Rectangle
            {
                Width = Math.Max(1, (Right - Left) * K), Height = Math.Max(1, (Top - Bottom) * K),
                Stroke = _accent, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 6, 4 }, IsHitTestVisible = false
            };
            Canvas.SetLeft(area, Px(Left)); Canvas.SetTop(area, Py(Top));
            _canvas.Children.Add(area);
            MarginLabel($"{_margins[0]:0.#} mm", Px((_paper[0] + Left) / 2), Py((Top + Bottom) / 2), true);
            MarginLabel($"{_margins[2]:0.#} mm", Px((_paper[2] + Right) / 2), Py((Top + Bottom) / 2), true);
            MarginLabel($"{_margins[1]:0.#} mm", Px((Left + Right) / 2), Py((_paper[3] + Top) / 2), false);
            MarginLabel($"{_margins[3]:0.#} mm", Px((Left + Right) / 2), Py((_paper[1] + Bottom) / 2), false);
            var areaTag = new TextBlock { Text = "ÁREA ÚTIL", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = _accent, IsHitTestVisible = false };
            Canvas.SetLeft(areaTag, Px(Left) + 4); Canvas.SetTop(areaTag, Py(Top) + 3);
            _canvas.Children.Add(areaTag);
                _staticEls = _canvas.Children.Cast<UIElement>().ToList();
                _staticKey = skey;
            }

            foreach (LayoutBox b in _boxes.Where(x => x.Fixed)) DrawBox(b);
            foreach (LayoutBox b in _boxes.Where(x => !x.Fixed && x.Excluded)) DrawBox(b);
            foreach (LayoutBox b in _boxes.Where(x => !x.Fixed && !x.Excluded)) DrawBox(b);
            if (_band != null) _canvas.Children.Add(_band);
            UpdateStatus();
        }

        private string _staticKey;
        private List<UIElement> _staticEls;

        private string StaticKey() =>
            string.Join("|", K.ToString("R"), string.Join(",", _paper.Select(v => v.ToString("R"))),
                string.Join(",", _margins.Select(v => v.ToString("R"))),
                _tbLines == null ? "0" : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_tbLines).ToString());

        private static readonly Brush MarginFill = Frozen(Color.FromArgb(0x26, 0x60, 0x66, 0x70));
        private static readonly Brush TbInk = Frozen(Color.FromRgb(0x6B, 0x72, 0x7A));

        private void AddRect(double x0, double y1, double x1, double y0)
        {
            double w = (x1 - x0) * K, h = (y1 - y0) * K;
            if (w <= 0.5 || h <= 0.5) return;
            var r = new Rectangle { Width = w, Height = h, Fill = MarginFill, IsHitTestVisible = false };
            Canvas.SetLeft(r, Px(x0)); Canvas.SetTop(r, Py(y1));
            _canvas.Children.Add(r);
        }

        private void MarginLabel(string text, double cx, double cy, bool vertical)
        {
            var t = new TextBlock { Text = text, FontSize = 10.5, Foreground = Ink, Background = Brushes.White, Padding = new Thickness(2, 0, 2, 0), IsHitTestVisible = false };
            t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double w = t.DesiredSize.Width, h = t.DesiredSize.Height;
            if (vertical) t.LayoutTransform = new RotateTransform(-90);
            Canvas.SetLeft(t, cx - (vertical ? h : w) / 2);
            Canvas.SetTop(t, cy - (vertical ? w : h) / 2);
            _canvas.Children.Add(t);
        }

        private bool Bad(LayoutBox b) =>
            !b.Fixed && !b.Excluded && (!b.Inside(Left, Top, Right, Bottom) || _boxes.Any(o => o != b && !o.Excluded && b.Overlaps(o, 0)));

        private void DrawBox(LayoutBox b)
        {
            bool selected = _sel.Contains(b), bad = Bad(b);
            double bw = Math.Max(2, b.W * K), bh = Math.Max(2, b.H * K);
            var shape = new Border
            {
                Width = bw, Height = bh,
                BorderThickness = new Thickness(selected ? 3 : b.Excluded ? 1 : 1.5),
                BorderBrush = b.Fixed || b.Excluded ? Gray : bad ? Red : _accent,
                Background = b.Fixed || b.Excluded ? GraySoft : bad ? RedSoft : _accentSoft,
                Opacity = b.Excluded ? 0.5 : 1,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(shape, Px(b.L));
            Canvas.SetTop(shape, Py(b.T));
            _canvas.Children.Add(shape);

            // the viewport title as it will be on the sheet (number, name, line, scale)
            if (b.HasLabel)
            {
                double lx0 = b.L + b.LabX0, lx1 = b.L + b.LabX1, ly0 = b.B + b.LabY0, ly1 = b.B + b.LabY1;
                double lw = Math.Max(2, (lx1 - lx0) * K), lh = Math.Max(2, (ly1 - ly0) * K);
                var lab = new Border
                {
                    Width = lw, Height = lh, BorderThickness = new Thickness(0, 0, 0, 1.2),
                    BorderBrush = b.Fixed || b.Excluded ? Gray : bad ? Red : _accent,
                    Background = TitleBack, Opacity = b.Excluded ? 0.5 : 1, IsHitTestVisible = false,
                    Child = new TextBlock
                    {
                        Text = "● " + (b.LabelText ?? b.Short ?? b.Name), FontSize = Math.Max(6, Math.Min(lh * 0.62, 13)),
                        Foreground = Ink, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                        Margin = new Thickness(2, 0, 2, 0)
                    }
                };
                Canvas.SetLeft(lab, Px(lx0));
                Canvas.SetTop(lab, Py(ly1));
                _canvas.Children.Add(lab);
            }

            // silhouette of the drawing (room outline, elevation, isometric), fitted in the box
            if (b.Sketch != null && b.Sketch.Count > 0 && bw > 16 && bh > 16)
            {
                double pad = Math.Min(bw, bh) * 0.08, sw = bw - 2 * pad, sh = bh - 2 * pad;
                double minX = b.Sketch.SelectMany(p => Xs(p)).DefaultIfEmpty(0).Min(), maxX = b.Sketch.SelectMany(p => Xs(p)).DefaultIfEmpty(1).Max();
                double minY = b.Sketch.SelectMany(p => Ys(p)).DefaultIfEmpty(0).Min(), maxY = b.Sketch.SelectMany(p => Ys(p)).DefaultIfEmpty(1).Max();
                double k = Math.Min(sw / Math.Max(1e-6, maxX - minX), sh / Math.Max(1e-6, maxY - minY));
                double ox = Px(b.L) + pad + (sw - (maxX - minX) * k) / 2, oy = Py(b.T) + pad + (sh - (maxY - minY) * k) / 2;
                var g = new StreamGeometry();
                using (StreamGeometryContext c = g.Open())
                    foreach (double[] pl in b.Sketch)
                    {
                        if (pl.Length < 4) continue;
                        c.BeginFigure(new Point(ox + (pl[0] - minX) * k, oy + (maxY - pl[1]) * k), false, false);
                        for (int i = 2; i + 1 < pl.Length; i += 2) c.LineTo(new Point(ox + (pl[i] - minX) * k, oy + (maxY - pl[i + 1]) * k), true, false);
                    }
                g.Freeze();
                _canvas.Children.Add(new Path { Data = g, Stroke = b.Fixed || b.Excluded ? Gray : SketchInk, StrokeThickness = 1.1, Opacity = 0.75, IsHitTestVisible = false });
            }

            // name + scale, as large as the slider asks and the box allows (never cut)
            string name = string.IsNullOrEmpty(b.Short) ? b.Name : b.Short;
            string text = name + (b.Scale > 0 ? $"\n1:{b.Scale}" : "") + (b.Excluded ? "\nfora da prancha" : "") + (b.Fixed ? "\njá na prancha" : "");
            double fs = _font?.Value ?? 14;
            int longest = text.Split('\n').Max(l => l.Length);
            fs = Math.Max(7, Math.Min(fs, (bw - 8) / Math.Max(1, longest * 0.58)));
            var label = new TextBlock
            {
                Text = text, FontSize = fs, FontWeight = FontWeights.SemiBold, Foreground = Ink, TextAlignment = TextAlignment.Center,
                Background = LabelBack, Padding = new Thickness(4, 1, 4, 1), IsHitTestVisible = false, MaxWidth = Math.Max(10, bw - 4)
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, Px(b.L) + (bw - Math.Min(label.DesiredSize.Width, bw - 4)) / 2);
            Canvas.SetTop(label, Py(b.T) + Math.Max(2, (bh - label.DesiredSize.Height) / 2));
            _canvas.Children.Add(label);
        }

        private static readonly Brush TitleBack = Frozen(Color.FromArgb(0x55, 0xE8, 0xEC, 0xF0));
        private static readonly Brush SketchInk = Frozen(Color.FromRgb(0x2E, 0x3A, 0x46));
        private static readonly Brush LabelBack = Frozen(Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF));

        private static IEnumerable<double> Xs(double[] p) { for (int i = 0; i + 1 < p.Length; i += 2) yield return p[i]; }
        private static IEnumerable<double> Ys(double[] p) { for (int i = 1; i < p.Length; i += 2) yield return p[i]; }

        private void UpdateStatus()
        {
            if (_status == null) return;
            var live = Free.Where(b => !b.Excluded).ToList();
            int outside = live.Count(b => !b.Inside(Left, Top, Right, Bottom));
            int over = live.Count(b => _boxes.Any(o => o != b && !o.Excluded && b.Overlaps(o, 0)));
            int fixedN = _boxes.Count(b => b.Fixed), excl = Free.Count(b => b.Excluded);
            _status.Text = $"{live.Count} vista(s) a colocar" + (excl > 0 ? $", {excl} fora da prancha" : "") +
                           (fixedN > 0 ? $", {fixedN} já na prancha (cinza)" : "") +
                           (outside > 0 ? $"  ·  {outside} fora da área útil" : "") +
                           (over > 0 ? $"  ·  {over} sobreposta(s)" : "") +
                           (outside == 0 && over == 0 ? "  ·  tudo dentro da área útil." : "  (em vermelho).");
        }

        /// <summary>Selection panel, list, sheet controls and margins from the current state.</summary>
        private void RefreshSide()
        {
            _busy = true;
            try
            {
                int nSel = _sel.Count(b => !b.Excluded);
                foreach (Button b in _needSel) b.IsEnabled = nSel >= 1;
                foreach (Button b in _needThree) b.IsEnabled = nSel >= 3;
                for (int i = 0; i < 4; i++) _marginBoxes[i].Text = _margins[i].ToString("0.#", CultureInfo.CurrentCulture);
                if (_sheetBox != null) _sheetBox.SelectedIndex = _host.SheetIndex;
                if (_tbBox != null) _tbBox.SelectedIndex = _host.TitleBlockIndex;
                if (_title != null && _host != null) _title.Text = "Prancha " + _host.SheetTitle;

                _list.Items.Clear();
                foreach (LayoutBox b in Free)
                {
                    var li = new ListBoxItem
                    {
                        Content = (b.Excluded ? "○ " : "● ") + b.Name + (b.Scale > 0 ? $"  (1:{b.Scale})" : ""),
                        Tag = b, IsSelected = _sel.Contains(b), FontSize = 12
                    };
                    _list.Items.Add(li);
                }

                var sel = _sel.ToList();
                if (sel.Count == 0)
                {
                    _selInfo.Text = "Nenhuma vista selecionada. Clique numa vista (Ctrl+clique para várias) ou desenhe uma janela.";
                    _scaleBox.Text = "";
                    _exclude.IsChecked = false;
                }
                else
                {
                    _selInfo.Text = sel.Count == 1
                        ? $"{sel[0].Name}\n{sel[0].W:0} × {sel[0].H:0} mm no papel"
                        : $"{sel.Count} vistas selecionadas";
                    var scales = sel.Select(b => b.Scale).Distinct().ToList();
                    _scaleBox.Text = scales.Count == 1 && scales[0] > 0 ? "1:" + scales[0] : "";
                    _exclude.IsChecked = sel.All(b => b.Excluded);
                }
            }
            finally { _busy = false; }
            Redraw();
        }

        // =====================================================================================
        // mouse / keyboard
        // =====================================================================================
        private LayoutBox HitTest(Point mm) =>
            _boxes.Where(b => !b.Fixed).OrderBy(b => b.Excluded ? 1 : 0).ThenBy(b => b.W * b.H)
                  .FirstOrDefault(b => mm.X >= b.L && mm.X <= b.R && mm.Y >= b.B && mm.Y <= b.T);

        private void OnDown(object sender, MouseButtonEventArgs e)
        {
            _scroll.Focus();
            Point mm = ToMm(e.GetPosition(_canvas));
            LayoutBox hit = HitTest(mm);
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (hit != null)
            {
                if (ctrl) { if (!_sel.Remove(hit)) _sel.Add(hit); }
                else if (!_sel.Contains(hit)) { _sel.Clear(); _sel.Add(hit); }
                _dragLead = _sel.Contains(hit) ? hit : null;
                _dragStartMm = mm;
                _dragOrig = _sel.ToDictionary(b => b, b => new Point(b.X, b.Y));
                _dragMoved = false;
                if (_dragLead != null) PushUndo();
            }
            else
            {
                if (!ctrl) _sel.Clear();
                _bandStart = e.GetPosition(_canvas);
                _band = new Rectangle { Stroke = _accent, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 2 }, Fill = _accentSoft, IsHitTestVisible = false };
            }
            _canvas.CaptureMouse();
            RefreshSide();
            e.Handled = true;
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            Point p = e.GetPosition(_canvas);
            if (_dragLead != null && _dragOrig != null)
            {
                Point mm = ToMm(p);
                double dx = mm.X - _dragStartMm.X, dy = mm.Y - _dragStartMm.Y;
                if (!_dragMoved && Math.Abs(dx) * K < 2 && Math.Abs(dy) * K < 2) return;
                _dragMoved = true;
                foreach (var kv in _dragOrig) { kv.Key.X = kv.Value.X + dx; kv.Key.Y = kv.Value.Y + dy; }
                if (_snap.IsChecked == true)
                {
                    Vector s = SnapOffset(_dragLead);
                    foreach (var kv in _dragOrig) { kv.Key.X += s.X; kv.Key.Y += s.Y; }
                }
                Redraw();
            }
            else if (_bandStart.HasValue && _band != null)
            {
                Point a = _bandStart.Value;
                _band.Width = Math.Abs(p.X - a.X);
                _band.Height = Math.Abs(p.Y - a.Y);
                Canvas.SetLeft(_band, Math.Min(p.X, a.X));
                Canvas.SetTop(_band, Math.Min(p.Y, a.Y));
                if (!_canvas.Children.Contains(_band)) _canvas.Children.Add(_band);
            }
        }

        private void OnUp(object sender, MouseButtonEventArgs e)
        {
            _canvas.ReleaseMouseCapture();
            if (_dragLead != null && !_dragMoved && _undo.Count > 0) _undo.Pop();   // a click, not a move
            if (_bandStart.HasValue)
            {
                Point a = ToMm(_bandStart.Value), b = ToMm(e.GetPosition(_canvas));
                double x0 = Math.Min(a.X, b.X), x1 = Math.Max(a.X, b.X), y0 = Math.Min(a.Y, b.Y), y1 = Math.Max(a.Y, b.Y);
                if (x1 - x0 > 0.5 || y1 - y0 > 0.5)
                    foreach (LayoutBox bx in Free)
                        if (bx.R >= x0 && bx.L <= x1 && bx.T >= y0 && bx.B <= y1) _sel.Add(bx);
            }
            _dragLead = null;
            _dragOrig = null;
            _bandStart = null;
            _band = null;
            RefreshSide();
        }

        private void OnWheel(object sender, MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            SetZoom(e.Delta > 0 ? _zoom * 1.15 : _zoom / 1.15);
            e.Handled = true;
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            if (e.OriginalSource is TextBox || (e.OriginalSource is ComboBox cb && cb.IsEditable)) return;
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            double step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
            switch (e.Key)
            {
                case Key.Z when ctrl: Undo(); break;
                case Key.Y when ctrl: Redo(); break;
                case Key.A when ctrl: _sel.Clear(); foreach (var b in Free) _sel.Add(b); RefreshSide(); break;
                case Key.Delete: if (_sel.Count > 0) SetExcluded(!_sel.All(b => b.Excluded)); break;
                case Key.Add: case Key.OemPlus: SetZoom(_zoom * 1.25); break;
                case Key.Subtract: case Key.OemMinus: SetZoom(_zoom / 1.25); break;
                case Key.D0: case Key.NumPad0: SetZoom(1); break;
                case Key.Left: Nudge(-step, 0); break;
                case Key.Right: Nudge(step, 0); break;
                case Key.Up: Nudge(0, step); break;
                case Key.Down: Nudge(0, -step); break;
                default: return;
            }
            e.Handled = true;
        }

        private void Nudge(double dx, double dy)
        {
            if (_sel.Count == 0) return;
            PushUndo();
            foreach (var b in _sel) { b.X += dx; b.Y += dy; }
            RefreshSide();
        }

        /// <summary>Offset that puts an edge or the centre of b on a margin or on an edge/centre of
        /// another view (within 8 px).</summary>
        private Vector SnapOffset(LayoutBox b)
        {
            double reach = 8 / K;
            var xs = new List<double> { Left, Right, (Left + Right) / 2 };
            var ys = new List<double> { Top, Bottom, (Top + Bottom) / 2 };
            foreach (LayoutBox o in _boxes)
            {
                if (_sel.Contains(o) || o.Excluded) continue;
                xs.AddRange(new[] { o.L, o.X, o.R, o.L - _gap, o.R + _gap });
                ys.AddRange(new[] { o.B, o.Y, o.T, o.B - _gap, o.T + _gap });
            }
            double bx = reach, dx = 0, by = reach, dy = 0;
            foreach (double v in new[] { b.L, b.X, b.R })
                foreach (double x in xs)
                    if (Math.Abs(x - v) < bx) { bx = Math.Abs(x - v); dx = x - v; }
            foreach (double v in new[] { b.B, b.Y, b.T })
                foreach (double y in ys)
                    if (Math.Abs(y - v) < by) { by = Math.Abs(y - v); dy = y - v; }
            return new Vector(dx, dy);
        }

        // =====================================================================================
        // tools
        // =====================================================================================
        private List<LayoutBox> Targets() => (_sel.Count > 0 ? _sel.ToList() : Free.ToList()).Where(b => !b.Excluded).ToList();

        private void Align(int mode)
        {
            var sel = _sel.Where(b => !b.Excluded).ToList();
            if (sel.Count == 0) { Note("Selecione as vistas a alinhar (uma vista sozinha alinha pela área útil)."); return; }
            PushUndo();
            double l, r, bt, tp;
            if (sel.Count == 1) { l = Left; r = Right; bt = Bottom; tp = Top; }
            else { l = sel.Min(b => b.L); r = sel.Max(b => b.R); bt = sel.Min(b => b.B); tp = sel.Max(b => b.T); }
            foreach (var b in sel)
            {
                switch (mode)
                {
                    case 0: b.X = l + b.W / 2; break;
                    case 1: b.X = (l + r) / 2; break;
                    case 2: b.X = r - b.W / 2; break;
                    case 3: b.Y = tp - b.H / 2; break;
                    case 4: b.Y = (bt + tp) / 2; break;
                    default: b.Y = bt + b.H / 2; break;
                }
            }
            RefreshSide();
        }

        private void Distribute(bool horizontal)
        {
            var sel = _sel.Where(b => !b.Excluded).ToList();
            if (sel.Count < 3) { Note("Selecione 3 ou mais vistas para distribuir."); return; }
            PushUndo();
            var ordered = horizontal ? sel.OrderBy(b => b.X).ToList() : sel.OrderByDescending(b => b.Y).ToList();
            double span = horizontal ? ordered.Last().R - ordered.First().L : ordered.First().T - ordered.Last().B;
            double sizes = ordered.Sum(b => horizontal ? b.W : b.H);
            double gap = (span - sizes) / (ordered.Count - 1);
            double pos = horizontal ? ordered.First().L : ordered.First().T;
            foreach (var b in ordered)
            {
                if (horizontal) { b.X = pos + b.W / 2; pos += b.W + gap; }
                else { b.Y = pos - b.H / 2; pos -= b.H + gap; }
            }
            RefreshSide();
        }

        private void CenterGroup()
        {
            var t = Targets();
            if (t.Count == 0) return;
            PushUndo();
            double dx = (Left + Right) / 2 - (t.Min(b => b.L) + t.Max(b => b.R)) / 2;
            double dy = (Top + Bottom) / 2 - (t.Min(b => b.B) + t.Max(b => b.T)) / 2;
            foreach (var b in t) { b.X += dx; b.Y += dy; }
            RefreshSide();
        }

        private void AutoArrange()
        {
            PushUndo();
            LayoutEngine.Pack(_boxes, Left, Top, Right, Bottom, _gap);
            RefreshSide();
        }

        /// <summary>The largest standard scale (smallest 1:n) at which the target views and the
        /// others fit the usable area when arranged.</summary>
        private void FitScale()
        {
            var t = Targets().Where(b => b.Scale > 0).ToList();
            if (t.Count == 0) return;
            PushUndo();
            var saved = _boxes.Select(Snap).ToList();
            foreach (int s in StdScales)
            {
                foreach (var b in t) b.SetScale(s);
                if (LayoutEngine.Pack(_boxes, Left, Top, Right, Bottom, _gap))
                {
                    Note($"Escala 1:{s}: tudo cabe na área útil.");
                    RefreshSide();
                    return;
                }
            }
            Restore(saved);
            Note("Nem na menor escala padrão as vistas cabem: use um carimbo maior ou tire vistas da prancha.");
            RefreshSide();
        }

        private void ApplyScaleFromBox()
        {
            string s = (_scaleBox.Text ?? "").Trim();
            int colon = s.IndexOf(':');
            if (colon >= 0) s = s.Substring(colon + 1);
            if (!int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int scale) || scale < 1 || scale > 5000)
            {
                Note("Escala inválida: use 1:25, 1:50...");
                return;
            }
            var sel = _sel.Where(b => b.Scale > 0).ToList();
            if (sel.Count == 0) { Note("Selecione as vistas que mudam de escala."); return; }
            PushUndo();
            foreach (var b in sel) b.SetScale(scale);
            RefreshSide();
        }

        private void SetExcluded(bool on)
        {
            if (_sel.Count == 0) return;
            PushUndo();
            foreach (var b in _sel) b.Excluded = on;
            RefreshSide();
        }

        private void ReadMargin(int i)
        {
            if (_busy) return;
            if (!OptionsDialog.TryParse(_marginBoxes[i].Text, out double v) || v < 0 || v > 300) { RefreshSide(); return; }
            if (Math.Abs(v - _margins[i]) < 1e-6) return;
            PushUndo();
            _margins[i] = v;
            RefreshSide();
        }

        private void SwitchSheet(int index)
        {
            if (_host == null || index < 0 || index == _host.SheetIndex) return;
            if (!_host.SwitchSheet(index, out double[] paper, out List<LayoutBox> fixedBoxes, out string error))
            {
                Note(error ?? "Não foi possível mudar de prancha.");
                RefreshSide();
                return;
            }
            _boxes.RemoveAll(b => b.Fixed);
            _boxes.InsertRange(0, fixedBoxes ?? new List<LayoutBox>());
            _paper = paper;
            LoadTitleBlock();
            _undo.Clear(); _redo.Clear();
            LayoutEngine.Pack(_boxes, Left, Top, Right, Bottom, _gap);
            ComputeFit();
            RefreshSide();
        }

        private void ChangeTitleBlock(int index)
        {
            if (_host == null || index < 0 || index == _host.TitleBlockIndex) return;
            if (!_host.ChangeTitleBlock(index, out double[] paper, out string error))
            {
                Note(error ?? "Não foi possível trocar o carimbo.");
                RefreshSide();
                return;
            }
            _paper = paper;
            LoadTitleBlock();
            _undo.Clear(); _redo.Clear();
            LayoutEngine.Pack(_boxes, Left, Top, Right, Bottom, _gap);
            ComputeFit();
            RefreshSide();
        }

        private void LoadTitleBlock()
        {
            try { _tbLines = _host?.TitleBlockLines(); } catch { _tbLines = null; }
        }

        private void Note(string text)
        {
            RefreshSide();
            if (_status != null) _status.Text = text;
        }

        // ---- undo / redo: positions, sizes, scales, exclusion and margins
        private static double[] Snap(LayoutBox b) => new[] { b.X, b.Y, b.W, b.H, b.Scale, b.Excluded ? 1 : 0 };

        private List<double[]> State()
        {
            var s = _boxes.Select(Snap).ToList();
            s.Add((double[])_margins.Clone());
            return s;
        }

        private void Restore(List<double[]> s)
        {
            for (int i = 0; i < _boxes.Count && i < s.Count; i++)
            {
                var v = s[i];
                if (v.Length < 6) continue;
                _boxes[i].X = v[0]; _boxes[i].Y = v[1]; _boxes[i].W = v[2]; _boxes[i].H = v[3];
                _boxes[i].Scale = (int)v[4]; _boxes[i].Excluded = v[5] > 0.5;
            }
            if (s.Count == _boxes.Count + 1 && s[s.Count - 1].Length == 4)
                for (int i = 0; i < 4; i++) _margins[i] = s[s.Count - 1][i];
        }

        private void PushUndo()
        {
            _undo.Push(State());
            _redo.Clear();
        }

        private void Undo()
        {
            if (_undo.Count == 0) return;
            _redo.Push(State());
            Restore(_undo.Pop());
            RefreshSide();
        }

        private void Redo()
        {
            if (_redo.Count == 0) return;
            _undo.Push(State());
            Restore(_redo.Pop());
            RefreshSide();
        }
    }
}
