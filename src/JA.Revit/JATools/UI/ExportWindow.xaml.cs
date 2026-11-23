using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using RevitDwgUnifier.Tools.Common;

namespace RevitDwgUnifier.UI
{
    /// <summary>Exportar Pranchas window (PDF + DWG único + DWG por prancha).</summary>
    public partial class ExportWindow : Window
    {
        private readonly ExportViewModel _vm;

        public ExportWindow(ExportViewModel vm)
        {
            InitializeComponent();
            JaTheme.Apply(this, ToolCatalog.GroupColor(ToolCatalog.Exportar));
            var info = ToolCatalog.Get("Exportar Pranchas");
            HeaderIcon.Source = info != null ? IconFactory.Tool(64, info.Icon, info.Glyph, info.Accent) : IconFactory.Create(40);
            _vm = vm;
            DataContext = vm;

            // A tab that disappears (its format was unticked) never stays selected.
            _vm.PropertyChanged += OnVmChanged;
            Closed += (s, e) => _vm.PropertyChanged -= OnVmChanged;

            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.F1) { OnHelp(this, null); e.Handled = true; }
                else if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
            };
            Loaded += (s, e) => { SearchBox.Focus(); KeepVisibleTab(); };
        }

        private void OnVmChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ExportViewModel.AnyFormat) && e.PropertyName != nameof(ExportViewModel.ExportDwgUnified) &&
                e.PropertyName != nameof(ExportViewModel.ExportPdf) && e.PropertyName != nameof(ExportViewModel.ExportDwgPerSheet) &&
                e.PropertyName != nameof(ExportViewModel.AnyDwg) && e.PropertyName != nameof(ExportViewModel.ExportPng)) return;
            Dispatcher.BeginInvoke(new System.Action(KeepVisibleTab), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>The option segment shown is always one the chosen formats use.</summary>
        private void KeepVisibleTab()
        {
            if (!(Tabs.SelectedItem is TabItem t) || t.Visibility != Visibility.Visible)
                Tabs.SelectedItem = Tabs.Items.OfType<TabItem>().FirstOrDefault(i => i.Visibility == Visibility.Visible);
        }

        private SelectableView _lastClicked;

        /// <summary>Shift+click marks/unmarks the whole range (in the order shown) since the previous click.</summary>
        private void OnItemClick(object sender, RoutedEventArgs e)
        {
            if (!(sender is FrameworkElement fe) || !(fe.DataContext is SelectableView item)) return;
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 && _lastClicked != null && !ReferenceEquals(_lastClicked, item))
            {
                // visual order: group by group when the list is grouped
                var order = _vm.ItemsView.Groups != null && _vm.ItemsView.Groups.Count > 0
                    ? _vm.ItemsView.Groups.OfType<System.Windows.Data.CollectionViewGroup>().SelectMany(g => g.Items.OfType<SelectableView>()).ToList()
                    : _vm.ItemsView.Cast<SelectableView>().ToList();
                int a = order.IndexOf(_lastClicked), b = order.IndexOf(item);
                if (a >= 0 && b >= 0)
                {
                    bool state = item.IsSelected;
                    for (int i = System.Math.Min(a, b); i <= System.Math.Max(a, b); i++) order[i].IsSelected = state;
                }
            }
            _lastClicked = item;
        }

        private void OnHelp(object sender, RoutedEventArgs e) => HelpCenter.Open(ToolCatalog.Get("Exportar Pranchas").Anchor);

        private void OnExport(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_vm.Settings.OutputFolder))
            {
                MessageBox.Show(this, "Escolha primeiro a pasta de destino (Destino › Pasta).", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                FolderBox.Focus();
                return;
            }
            if (_vm.UsesPattern && string.IsNullOrWhiteSpace(_vm.FilePattern))
            {
                MessageBox.Show(this, "Defina o nome dos arquivos (Destino), por exemplo {numero} - {nome}.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                PatternBox.Focus();
                return;
            }
            try
            {
                Directory.CreateDirectory(_vm.Settings.OutputFolder);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show(this, "Não foi possível criar a pasta de destino:\n" + ex.Message, Title,
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _vm.Commit();
            DialogResult = true;
        }

        private void OnBrowseFolder(object sender, RoutedEventArgs e)
        {
            string picked = OptionsDialog.PickFolder(_vm.Settings.OutputFolder, this);
            if (picked == null) return;
            _vm.Settings.OutputFolder = picked;
            RefreshBindings();
        }

        /// <summary>Inserts a {variável} at the caret of the file-name pattern.</summary>
        private void OnToken(object sender, RoutedEventArgs e)
        {
            string token = (sender as FrameworkElement)?.Tag as string;
            if (string.IsNullOrEmpty(token)) return;
            string text = PatternBox.Text ?? "";
            int at = PatternBox.IsKeyboardFocusWithin || PatternBox.CaretIndex > 0 ? PatternBox.CaretIndex : text.Length;
            at = System.Math.Max(0, System.Math.Min(at, text.Length));
            PatternBox.Text = text.Insert(at, token);
            PatternBox.Focus();
            PatternBox.CaretIndex = at + token.Length;
        }

        private void OnBrowsePat(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "Mapeamento de hachuras (*.txt;*.pat)|*.txt;*.pat|Todos os arquivos|*.*" };
            if (dlg.ShowDialog(this) == true)
            {
                _vm.Settings.HatchPatternFile = dlg.FileName;
                RefreshBindings();
            }
        }

        private void OnBrowseConsole(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "AutoCAD Core Console|accoreconsole.exe" };
            if (dlg.ShowDialog(this) == true)
            {
                _vm.Settings.CoreConsolePath = dlg.FileName;
                RefreshBindings();
            }
        }

        // Settings is a plain DataContract POCO (no change notification): re-bind to show edits.
        private void RefreshBindings()
        {
            int tab = Tabs.SelectedIndex;
            DataContext = null;
            DataContext = _vm;
            Tabs.SelectedIndex = tab;
            KeepVisibleTab();
        }
    }
}
