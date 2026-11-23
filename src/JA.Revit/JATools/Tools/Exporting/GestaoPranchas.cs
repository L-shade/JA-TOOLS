using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Autodesk.Revit.Attributes;
using RevitDwgUnifier.Tools.Common;
using DB = Autodesk.Revit.DB;
// Autodesk.Revit.UI is NOT imported: it also defines TextBox / ComboBox (ribbon controls),
// which would clash with the WPF controls of this window.
using Result = Autodesk.Revit.UI.Result;

namespace RevitDwgUnifier.Tools.Exporting
{
    /// <summary>Gestão de Pranchas: build sheets by dragging views onto them; viewports are
    /// arranged automatically inside the title block's drawing area.</summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class GestaoPranchasCommand : ToolCommand
    {
        public override string Title => "Gestão de Pranchas";
        protected override bool PicksInView => true;   // runs its own window: no automatic progress box

        protected override Result Run()
        {
            var w = new SheetManagerWindow(Doc);
            if (Owner != IntPtr.Zero) new System.Windows.Interop.WindowInteropHelper(w).Owner = Owner;
            w.ShowDialog();
            return w.Changed ? Result.Succeeded : Result.Cancelled;
        }
    }

    /// <summary>
    /// Window: views not yet on a sheet (left, filterable, multi-select) and sheets (right).
    /// Drag views onto a sheet, or select and press "Colocar". "Organizar" re-arranges the
    /// viewports of the selected sheet: shelf packing, tallest first, left→right, top→bottom,
    /// inside the title block minus margins and the strip reserved for the title block stamp.
    /// The window is modal inside the command, so Revit API calls from its buttons are allowed.
    /// </summary>
    public sealed class SheetManagerWindow : Window
    {
        private sealed class Item
        {
            public DB.ElementId Id;
            public string Text;
            public override string ToString() => Text;
        }

        private readonly DB.Document _doc;
        private readonly ObservableCollection<Item> _views = new ObservableCollection<Item>();
        private readonly ObservableCollection<Item> _sheets = new ObservableCollection<Item>();
        private readonly ListBox _viewList = new ListBox { SelectionMode = SelectionMode.Extended };
        private readonly ListBox _sheetList = new ListBox { AllowDrop = true, SelectionMode = SelectionMode.Extended };
        private readonly TextBox _filter = new TextBox();
        private readonly TextBox _margin = new TextBox { Text = "1", Width = 50 };
        private readonly TextBox _reserve = new TextBox { Text = "0", Width = 50 };
        private readonly TextBox _gap = new TextBox { Text = "1", Width = 50 };
        private readonly ComboBox _titleBlocks = new ComboBox { MinWidth = 200 };
        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        private List<DB.FamilySymbol> _tbTypes;
        private Point _dragStart;

        public bool Changed { get; private set; }

        public SheetManagerWindow(DB.Document doc)
        {
            _doc = doc;
            Width = 980; Height = 680; MinWidth = 760; MinHeight = 520;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            _margin.Text = ToolPrefs.Get("Pranchas.margin") ?? "1";
            _reserve.Text = ToolPrefs.Get("Pranchas.reserve") ?? "0";
            _gap.Text = ToolPrefs.Get("Pranchas.gap") ?? "1";

            _tbTypes = new DB.FilteredElementCollector(doc).OfCategory(DB.BuiltInCategory.OST_TitleBlocks).OfClass(typeof(DB.FamilySymbol))
                .Cast<DB.FamilySymbol>().OrderBy(s => s.FamilyName).ThenBy(s => s.Name).ToList();
            foreach (var s in _tbTypes) _titleBlocks.Items.Add(s.FamilyName + " : " + s.Name);
            if (_titleBlocks.Items.Count > 0) _titleBlocks.SelectedIndex = 0;

            _viewList.ItemsSource = _views;
            _sheetList.ItemsSource = _sheets;
            _filter.TextChanged += (s, e) => LoadViews();

            // Drag views -> drop on a sheet.
            _viewList.PreviewMouseLeftButtonDown += (s, e) => _dragStart = e.GetPosition(null);
            _viewList.PreviewMouseMove += (s, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed || _viewList.SelectedItems.Count == 0) return;
                Vector d = e.GetPosition(null) - _dragStart;
                if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                var ids = _viewList.SelectedItems.Cast<Item>().Select(i => i.Id).ToList();
                DragDrop.DoDragDrop(_viewList, new DataObject("JAViews", ids), DragDropEffects.Copy);
            };
            _sheetList.DragOver += (s, e) => { e.Effects = e.Data.GetDataPresent("JAViews") ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
            _sheetList.Drop += (s, e) =>
            {
                if (!(e.Data.GetData("JAViews") is List<DB.ElementId> ids)) return;
                Item target = ItemUnder(e.OriginalSource as DependencyObject);
                if (target == null) { _status.Text = "Solte as vistas sobre uma prancha da lista."; return; }
                Place(target.Id, ids);
            };

            Build();
            LoadViews();
            LoadSheets();
        }

        private void Build()
        {
            // ---- left: views not on sheets
            var left = new DockPanel();
            var search = ToolChrome.SearchBox(_filter, "Buscar vista ou tabela…");
            DockPanel.SetDock(search, Dock.Top);
            left.Children.Add(search);
            var leftHint = ToolChrome.Muted("Ctrl/Shift + clique seleciona várias. Arraste para uma prancha à direita.");
            leftHint.Margin = new Thickness(0, 6, 0, 0);
            DockPanel.SetDock(leftHint, Dock.Bottom);
            left.Children.Add(leftHint);
            _viewList.Margin = new Thickness(0, 8, 0, 0);
            left.Children.Add(_viewList);
            var leftCard = ToolChrome.Card(left, "Vistas fora de pranchas", ToolChrome.Badge(out TextBlock viewCount));
            leftCard.Margin = new Thickness(0, 0, 8, 12);
            _views.CollectionChanged += (s, e) => viewCount.Text = _views.Count + (_views.Count == 1 ? " item" : " itens");

            // ---- right: sheets (drop target)
            var right = new DockPanel();
            var sheetButtons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            var place = ToolChrome.Primary("Colocar selecionadas  →", (s, e) =>
            {
                if (_sheetList.SelectedItem is Item sh) Place(sh.Id, _viewList.SelectedItems.Cast<Item>().Select(i => i.Id).ToList());
                else _status.Text = "Selecione primeiro a prancha de destino na lista.";
            });
            place.Margin = new Thickness(0, 0, 6, 4);
            place.ToolTip = "Coloca as vistas selecionadas à esquerda na prancha selecionada (o mesmo que arrastar).";
            sheetButtons.Children.Add(place);
            var arrange = ToolChrome.Secondary("Organizar prancha", (s, e) =>
            {
                var sel = _sheetList.SelectedItems.Cast<Item>().ToList();
                if (sel.Count > 0) RunTx("Organizar prancha", () => { foreach (var sh in sel) Arrange((DB.ViewSheet)_doc.GetElement(sh.Id)); });
                else _status.Text = "Selecione a prancha a organizar.";
            }, "Reorganiza as vistas das pranchas selecionadas (Shift/Ctrl+clique para várias) com as margens abaixo.");
            arrange.Margin = new Thickness(0, 0, 6, 4);
            sheetButtons.Children.Add(arrange);
            var add = ToolChrome.Secondary("Nova prancha…", (s, e) => NewSheet(), "Cria uma prancha com o carimbo escolhido abaixo.");
            add.Margin = new Thickness(0, 0, 6, 4);
            sheetButtons.Children.Add(add);
            DockPanel.SetDock(sheetButtons, Dock.Bottom);
            right.Children.Add(sheetButtons);
            var drop = ToolChrome.Muted("Solte as vistas sobre o nome da prancha.");
            drop.Margin = new Thickness(0, 0, 0, 8);
            DockPanel.SetDock(drop, Dock.Top);
            right.Children.Add(drop);
            right.Children.Add(_sheetList);
            var rightCard = ToolChrome.Card(right, "Pranchas", ToolChrome.Badge(out TextBlock sheetCount));
            rightCard.Margin = new Thickness(8, 0, 0, 12);
            _sheets.CollectionChanged += (s, e) => sheetCount.Text = _sheets.Count + (_sheets.Count == 1 ? " prancha" : " pranchas");

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(leftCard, 0); Grid.SetColumn(rightCard, 1);
            grid.Children.Add(leftCard); grid.Children.Add(rightCard);

            // ---- automatic layout options
            var opts = new WrapPanel();
            opts.Children.Add(Lbl("Margem")); opts.Children.Add(_margin); opts.Children.Add(Unit("cm"));
            opts.Children.Add(Lbl("Reserva p/ carimbo")); opts.Children.Add(_reserve); opts.Children.Add(Unit("cm"));
            opts.Children.Add(Lbl("Espaço entre vistas")); opts.Children.Add(_gap); opts.Children.Add(Unit("cm"));
            opts.Children.Add(Lbl("Carimbo das novas pranchas")); opts.Children.Add(_titleBlocks);
            _margin.ToolTip = "Distância livre entre a borda da área útil do carimbo e as vistas.";
            _reserve.ToolTip = "Faixa inferior reservada (ex.: legenda/carimbo horizontal) onde nenhuma vista é colocada.";
            _gap.ToolTip = "Espaço mínimo entre duas vistas vizinhas.";
            var optsCard = ToolChrome.Card(opts, "Arrumação automática");
            optsCard.Margin = new Thickness(0);

            var body = new DockPanel();
            DockPanel.SetDock(optsCard, Dock.Bottom);
            body.Children.Add(optsCard);
            body.Children.Add(grid);

            // ---- footer
            var close = ToolChrome.Primary("Fechar");
            close.IsCancel = true;
            close.MinWidth = 100;
            close.Click += (s, e) => { SavePrefs(); Close(); };
            var footer = new DockPanel();
            DockPanel.SetDock(close, Dock.Right);
            footer.Children.Add(close);
            _status.VerticalAlignment = VerticalAlignment.Center;
            _status.Margin = new Thickness(0, 0, 12, 0);
            _status.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Text");
            _status.Text = "Cada alteração é aplicada na hora (Ctrl+Z no Revit desfaz depois de fechar).";
            footer.Children.Add(_status);

            ToolChrome.DressCompact(this, "Gestão de Pranchas", body, footer);
        }

        private static TextBlock Unit(string t)
        {
            var u = ToolChrome.Muted(t, 12);
            u.VerticalAlignment = VerticalAlignment.Center;
            u.Margin = new Thickness(4, 0, 0, 0);
            return u;
        }

        private static TextBlock Lbl(string t) => new TextBlock { Text = t, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 8, 0) };

        private Item ItemUnder(DependencyObject d)
        {
            while (d != null && !(d is ListBoxItem)) d = VisualTreeHelper.GetParent(d);
            return (d as ListBoxItem)?.DataContext as Item;
        }

        // ------------------------------------------------------------------ data

        private void LoadViews()
        {
            var onSheets = new HashSet<DB.ElementId>(new DB.FilteredElementCollector(_doc).OfClass(typeof(DB.Viewport)).Cast<DB.Viewport>().Select(v => v.ViewId));
            string f = (_filter.Text ?? "").Trim();
            var types = new[] { DB.ViewType.FloorPlan, DB.ViewType.CeilingPlan, DB.ViewType.Elevation, DB.ViewType.Section, DB.ViewType.ThreeD,
                                DB.ViewType.DraftingView, DB.ViewType.Legend, DB.ViewType.Detail, DB.ViewType.AreaPlan, DB.ViewType.Schedule, DB.ViewType.EngineeringPlan };
            var views = new DB.FilteredElementCollector(_doc).OfClass(typeof(DB.View)).Cast<DB.View>()
                .Where(v => !v.IsTemplate && types.Contains(v.ViewType) && (v.ViewType == DB.ViewType.Legend || !onSheets.Contains(v.Id)))
                .Where(v => !(v is DB.ViewSchedule vs) || (!vs.IsTitleblockRevisionSchedule && !vs.Definition.IsKeySchedule))
                .Where(v => f.Length == 0 || v.Name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(v => v.ViewType.ToString()).ThenBy(v => v.Name);
            _views.Clear();
            foreach (var v in views) _views.Add(new Item { Id = v.Id, Text = $"{v.Name}   [{v.ViewType}]" });
        }

        private void LoadSheets(DB.ElementId select = null)
        {
            _sheets.Clear();
            foreach (var s in new DB.FilteredElementCollector(_doc).OfClass(typeof(DB.ViewSheet)).Cast<DB.ViewSheet>()
                         .Where(s => !s.IsPlaceholder).OrderBy(s => s.SheetNumber, new NaturalStringComparer()))
            {
                var item = new Item { Id = s.Id, Text = $"{s.SheetNumber} - {s.Name}   ({s.GetAllViewports().Count} vistas)" };
                _sheets.Add(item);
                if (select != null && s.Id == select) _sheetList.SelectedItem = item;
            }
        }

        private void SavePrefs()
        {
            ToolPrefs.Set("Pranchas.margin", _margin.Text);
            ToolPrefs.Set("Pranchas.reserve", _reserve.Text);
            ToolPrefs.Set("Pranchas.gap", _gap.Text);
            ToolPrefs.Save();
        }

        private double Cm(TextBox tb, double def) => OptionsDialog.TryParse(tb.Text, out double v) ? RevitUtil.M(v / 100.0) : RevitUtil.M(def / 100.0);

        private void RunTx(string name, Action action)
        {
            using (var t = new DB.Transaction(_doc, "JA Tools - " + name))
            {
                t.Start();
                RevitDwgUnifier.Export.FailureSwallower.Attach(t);
                try
                {
                    action();
                    t.Commit();
                    Changed = true;
                }
                catch (Exception ex)
                {
                    t.RollBack();
                    _status.Text = "Erro: " + ex.Message;
                }
            }
        }

        // ------------------------------------------------------------------ actions

        private void Place(DB.ElementId sheetId, List<DB.ElementId> viewIds)
        {
            if (viewIds == null || viewIds.Count == 0) { _status.Text = "Selecione vistas na lista da esquerda."; return; }
            var sheet = (DB.ViewSheet)_doc.GetElement(sheetId);
            int placed = 0;
            var refused = new List<string>();
            RunTx("Colocar vistas", () =>
            {
                foreach (DB.ElementId id in viewIds)
                {
                    var v = _doc.GetElement(id) as DB.View;
                    if (v is DB.ViewSchedule)
                    {
                        DB.ScheduleSheetInstance.Create(_doc, sheetId, id, DB.XYZ.Zero);
                        placed++;
                    }
                    else if (DB.Viewport.CanAddViewToSheet(_doc, sheetId, id))
                    {
                        DB.Viewport.Create(_doc, sheetId, id, DB.XYZ.Zero);
                        placed++;
                    }
                    else refused.Add(v?.Name ?? id.ToString());
                }
                _doc.Regenerate();
                Arrange(sheet);
            });
            _status.Text = $"{placed} vista(s) colocada(s) em {sheet.SheetNumber}." +
                           (refused.Count > 0 ? " Não colocadas (já em outra prancha ou incompatíveis): " + string.Join(", ", refused) : "");
            LoadViews();
            LoadSheets(sheetId);
        }

        private void NewSheet()
        {
            if (_tbTypes.Count == 0 || _titleBlocks.SelectedIndex < 0) { _status.Text = "O projeto não tem família de carimbo (title block)."; return; }
            var dlg = new OptionsDialog("Nova prancha", "NovaPrancha", "Criar prancha");
            var fNum = dlg.Text("num", "Número", NextNumber());
            var fName = dlg.Text("name", "Nome", "PRANCHA");
            if (!dlg.Show(new System.Windows.Interop.WindowInteropHelper(this).Handle)) return;
            DB.ElementId created = null;
            RunTx("Nova prancha", () =>
            {
                var tb = _tbTypes[_titleBlocks.SelectedIndex];
                if (!tb.IsActive) tb.Activate();
                DB.ViewSheet s = DB.ViewSheet.Create(_doc, tb.Id);
                s.SheetNumber = fNum.Value;
                s.Name = fName.Value;
                created = s.Id;
            });
            LoadSheets(created);
            if (created != null) _status.Text = $"Prancha {fNum.Value} criada.";
        }

        private string NextNumber()
        {
            var nums = new DB.FilteredElementCollector(_doc).OfClass(typeof(DB.ViewSheet)).Cast<DB.ViewSheet>().Select(s => s.SheetNumber).ToList();
            string last = nums.OrderBy(n => n, new NaturalStringComparer()).LastOrDefault() ?? "A100";
            var m = System.Text.RegularExpressions.Regex.Match(last, @"^(.*?)(\d+)$");
            if (!m.Success) return last + "-1";
            string digits = m.Groups[2].Value;
            string next = (long.Parse(digits) + 1).ToString(new string('0', digits.Length));
            string candidate = m.Groups[1].Value + next;
            return nums.Contains(candidate) ? candidate + "-1" : candidate;
        }

        /// <summary>Shelf packing of the sheet's viewports inside the title block's drawing area.</summary>
        private void Arrange(DB.ViewSheet sheet)
        {
            DB.Element tb = new DB.FilteredElementCollector(_doc, sheet.Id).OfCategory(DB.BuiltInCategory.OST_TitleBlocks).FirstElement();
            DB.BoundingBoxXYZ area = tb?.get_BoundingBox(sheet);
            if (area == null) { _status.Text = "Prancha sem carimbo: vistas colocadas no centro."; return; }

            double margin = Cm(_margin, 1), reserve = Cm(_reserve, 0), gap = Cm(_gap, 1);
            double left = area.Min.X + margin, right = area.Max.X - margin, top = area.Max.Y - margin, bottom = area.Min.Y + margin + reserve;

            var vps = sheet.GetAllViewports().Select(id => (DB.Viewport)_doc.GetElement(id))
                .Select(vp => new { Vp = vp, Box = vp.GetBoxOutline() })
                .Select(x => new { x.Vp, W = x.Box.MaximumPoint.X - x.Box.MinimumPoint.X, H = x.Box.MaximumPoint.Y - x.Box.MinimumPoint.Y })
                .OrderByDescending(x => x.H).ThenByDescending(x => x.W).ToList();

            double x0 = left, y0 = top, rowH = 0;
            bool overflow = false;
            foreach (var v in vps)
            {
                if (x0 > left && x0 + v.W > right) { y0 -= rowH + gap; x0 = left; rowH = 0; }
                v.Vp.SetBoxCenter(new DB.XYZ(x0 + v.W / 2, y0 - v.H / 2, 0));
                if (y0 - v.H < bottom || x0 + v.W > right) overflow = true;
                x0 += v.W + gap;
                rowH = Math.Max(rowH, v.H);
            }
            _status.Text = $"{vps.Count} vista(s) organizada(s) em {sheet.SheetNumber}." + (overflow ? " Atenção: as vistas não cabem todas na área útil." : "");
        }
    }
}
