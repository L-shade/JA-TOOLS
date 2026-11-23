using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Input;
using RevitDwgUnifier.Contracts;
using RevitDwgUnifier.Core;

namespace RevitDwgUnifier.UI
{
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void Raise([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }
    }

    public sealed class RelayCommand : ICommand
    {
        private readonly Action<object> _run;
        private readonly Func<object, bool> _can;
        public RelayCommand(Action<object> run, Func<object, bool> can = null) { _run = run; _can = can; }
        public bool CanExecute(object p) => _can == null || _can(p);
        public void Execute(object p) => _run(p);
        public event EventHandler CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }
    }

    public sealed class SelectableView : ObservableObject
    {
        private readonly Action _onChanged;
        private bool _isSelected;
        public SelectableView(ViewInfo info, Action onChanged) { Info = info; _onChanged = onChanged; }
        public ViewInfo Info { get; }
        public string Title => Info.DisplayName;
        public string Group => Info.Group;
        public string Detail
        {
            get
            {
                if (Info.IsSheet) return Info.PlacedViewIds.Count + " viewport(s)";
                string scale = Info.Scale > 0 ? "1:" + Info.Scale : "";
                string onSheet = Info.HostSheetId > 0 ? "  na folha " + Info.HostSheetLabel : "";
                return scale + onSheet;
            }
        }
        public bool IsSelected
        {
            get => _isSelected;
            set { if (Set(ref _isSelected, value)) _onChanged?.Invoke(); }
        }
    }

    /// <summary>A stored value with a display label (combo boxes).</summary>
    public sealed class Choice
    {
        public Choice(string value, string label) { Value = value; Label = label; }
        public string Value { get; }
        public string Label { get; }
        public override string ToString() => Label;
    }

    public sealed class NamedId
    {
        public long Id { get; set; }
        public string Label { get; set; }
        public override string ToString() => Label;
    }

    /// <summary>MVVM state of the export dialog. Revit-free: the command builds the lists.</summary>
    public sealed class ExportViewModel : ObservableObject
    {
        private string _search = "";
        private bool _showSheets = true;
        private bool _showViews = true;
        private string _status;

        public ExportViewModel(IEnumerable<ViewInfo> catalog, IEnumerable<long> preselected,
                               IList<string> exportSetups, string activeSetup,
                               IList<NamedId> titleBlocks, ExportSettings settings,
                               SheetExportOptions formats, IList<string> pdfColorChoices)
        {
            Settings = settings;
            Formats = formats ?? new SheetExportOptions();
            PdfColorChoices = pdfColorChoices ?? new List<string> { "Colorido" };
            var pre = new HashSet<long>(preselected ?? Enumerable.Empty<long>());
            Items = new ObservableCollection<SelectableView>(
                catalog.Select(v => new SelectableView(v, OnSelectionChanged) { IsSelected = pre.Contains(v.Id) }));

            ItemsView = CollectionViewSource.GetDefaultView(Items);
            ItemsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SelectableView.Group)));
            ItemsView.Filter = Filter;

            ExportSetups = exportSetups;
            if (string.IsNullOrEmpty(Settings.ExportSetupName) || !exportSetups.Contains(Settings.ExportSetupName))
                Settings.ExportSetupName = activeSetup != null && exportSetups.Contains(activeSetup) ? activeSetup : exportSetups.FirstOrDefault();

            TitleBlocks = titleBlocks;
            SelectedTitleBlock = titleBlocks.FirstOrDefault(t => t.Id == Settings.TitleBlockTypeId) ?? titleBlocks.First();

            FontMapText = string.Join(Environment.NewLine, Settings.FontMap ?? new List<string>());

            SelectAllVisibleCommand = new RelayCommand(_ => SetVisibleSelection(true));
            ClearCommand = new RelayCommand(_ => { foreach (var i in Items) i.IsSelected = false; });
            OnSelectionChanged();
        }

        public ExportSettings Settings { get; }
        public MergeOptions Merge => Settings.Merge;

        // ---------------------------------------------------------------- formats (Exportar Pranchas)
        public SheetExportOptions Formats { get; }
        public IList<string> PdfColorChoices { get; }

        public bool ExportPdf { get => Formats.Pdf; set { Formats.Pdf = value; FormatsChanged(); } }
        public bool ExportDwgUnified { get => Formats.DwgUnified; set { Formats.DwgUnified = value; FormatsChanged(); } }
        public bool ExportDwgPerSheet { get => Formats.DwgPerSheet; set { Formats.DwgPerSheet = value; FormatsChanged(); } }
        public bool ExportPng { get => Formats.Png; set { Formats.Png = value; FormatsChanged(); } }
        public int PngPixels { get => Formats.PngPixels; set { Formats.PngPixels = Math.Max(200, Math.Min(15000, value)); Raise(); } }
        public bool PngTransparent { get => Formats.PngTransparent; set { Formats.PngTransparent = value; Raise(); } }
        public bool PdfCombine { get => Formats.PdfCombine; set { Formats.PdfCombine = value; Raise(); Raise(nameof(UsesPattern)); OnSelectionChanged(); } }
        public string PdfCombinedName { get => Formats.PdfCombinedName; set { Formats.PdfCombinedName = value; Raise(); } }
        public int PdfColorIndex { get => Formats.PdfColorIndex; set { Formats.PdfColorIndex = value; Raise(); } }
        public string FilePattern { get => Formats.FilePattern; set { Formats.FilePattern = value; Raise(); Raise(nameof(PatternPreview)); } }

        /// <summary>Any DWG at all (setup, version and units matter).</summary>
        public bool AnyDwg => ExportDwgUnified || ExportDwgPerSheet;
        /// <summary>One file per item (pattern matters).</summary>
        public bool UsesPattern => (ExportPdf && !PdfCombine) || ExportDwgPerSheet || ExportPng;
        public bool AnyFormat => ExportPdf || ExportDwgUnified || ExportDwgPerSheet || ExportPng;
        public bool CanExport => HasSelection && AnyFormat;

        /// <summary>Example of the file name for the first marked sheet (live, didactic).</summary>
        public string PatternPreview
        {
            get
            {
                var first = Items?.FirstOrDefault(i => i.IsSelected && i.Info.IsSheet)?.Info
                            ?? Items?.FirstOrDefault(i => i.Info.IsSheet)?.Info;
                string p = FilePattern ?? "";
                string ex = p.Replace("{numero}", first?.SheetNumber ?? "A101")
                             .Replace("{nome}", first?.Name ?? "PLANTA BAIXA")
                             .Replace("{revisao}", "R00").Replace("{projeto}", "Projeto")
                             .Replace("{codigo}", "001").Replace("{data}", DateTime.Now.ToString("yyyy-MM-dd")).Trim(' ', '-', '_');
                return "Exemplo: " + (ex.Length == 0 ? "(vazio)" : ex) + (ExportPdf && !PdfCombine ? ".pdf" : ExportDwgPerSheet ? ".dwg" : ".png");
            }
        }

        private void FormatsChanged()
        {
            foreach (string n in new[] { nameof(ExportPdf), nameof(ExportDwgUnified), nameof(ExportDwgPerSheet), nameof(ExportPng), nameof(AnyDwg), nameof(UsesPattern), nameof(AnyFormat), nameof(CanExport), nameof(PatternPreview) })
                Raise(n);
            OnSelectionChanged();
        }

        public ObservableCollection<SelectableView> Items { get; }
        public ICollectionView ItemsView { get; }

        public IList<string> ExportSetups { get; }
        public IList<NamedId> TitleBlocks { get; }
        public NamedId SelectedTitleBlock { get; set; }

        public IList<string> DwgVersions { get; } = new[] { "2018", "2013", "2010", "2007" };
        /// <summary>Stored values stay the API names (ExportUnit / PaperUnits); labels are Portuguese.</summary>
        public IList<Choice> TargetUnits { get; } = new[]
        {
            new Choice("Default", "Padrão do projeto"), new Choice("Millimeter", "Milímetro"), new Choice("Centimeter", "Centímetro"),
            new Choice("Meter", "Metro"), new Choice("Inch", "Polegada"), new Choice("Foot", "Pé")
        };
        /// <summary>Paper space unit; "Model" = same unit as model space.</summary>
        public IList<Choice> PaperUnitChoices { get; } = new[]
        {
            new Choice("Model", "Igual ao model"), new Choice("Millimeter", "Milímetro"), new Choice("Centimeter", "Centímetro"),
            new Choice("Meter", "Metro"), new Choice("Inch", "Polegada"), new Choice("Foot", "Pé")
        };

        public bool UseSharedCoordinates
        {
            get => Settings.CoordinateBase == CoordinateBase.Shared;
            set { Settings.CoordinateBase = value ? CoordinateBase.Shared : CoordinateBase.InternalOrigin; Raise(); }
        }

        public bool UseTrueCoordinates
        {
            get => Merge.Arrangement == ModelSpaceArrangement.TrueCoordinates;
            set { Merge.Arrangement = value ? ModelSpaceArrangement.TrueCoordinates : ModelSpaceArrangement.Tiled; Raise(); }
        }

        public string FontMapText { get; set; }

        public string SearchText
        {
            get => _search;
            set { if (Set(ref _search, value)) ItemsView.Refresh(); }
        }
        public bool ShowSheets
        {
            get => _showSheets;
            set { if (Set(ref _showSheets, value)) ItemsView.Refresh(); }
        }
        public bool ShowViews
        {
            get => _showViews;
            set { if (Set(ref _showViews, value)) ItemsView.Refresh(); }
        }

        public string Status { get => _status; private set => Set(ref _status, value); }
        /// <summary>"3 de 120" counter shown next to the list title.</summary>
        public string SelectionBadge => $"{Items?.Count(i => i.IsSelected) ?? 0} de {Items?.Count ?? 0}";
        /// <summary>Text of the main button: says exactly what will happen.</summary>
        public string ExportButtonText
        {
            get
            {
                int n = Items?.Count(i => i.IsSelected) ?? 0;
                if (n == 0) return "Marque o que exportar";
                if (!AnyFormat) return "Escolha um formato";
                return $"Exportar {n} {(n == 1 ? "item" : "itens")}";
            }
        }
        public bool HasSelection => Items.Any(i => i.IsSelected);

        public ICommand SelectAllVisibleCommand { get; }
        public ICommand ClearCommand { get; }

        public IList<ViewInfo> GetSelection() => Items.Where(i => i.IsSelected).Select(i => i.Info).ToList();

        /// <summary>Copies UI-only values back into the settings object before it is persisted.</summary>
        public ExportSettings Commit()
        {
            Settings.TitleBlockTypeId = SelectedTitleBlock?.Id ?? -1;
            Settings.FontMap = (FontMapText ?? "")
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim()).Where(l => l.Contains("=")).ToList();
            return Settings;
        }

        /// <summary>"12 PDFs + 1 DWG único + 12 DWGs": what will be written.</summary>
        private string Outputs(int n)
        {
            var parts = new List<string>();
            if (ExportPdf) parts.Add(PdfCombine ? "1 PDF único" : $"{n} PDF(s)");
            if (ExportDwgUnified) parts.Add("1 DWG único");
            if (ExportDwgPerSheet) parts.Add($"{n} DWG(s) separados");
            if (ExportPng) parts.Add($"{n} PNG(s)");
            return string.Join(" + ", parts);
        }

        private bool Filter(object o)
        {
            var v = (SelectableView)o;
            if (v.Info.IsSheet && !ShowSheets) return false;
            if (!v.Info.IsSheet && !ShowViews) return false;
            return string.IsNullOrWhiteSpace(SearchText) ||
                   RevitDwgUnifier.Tools.Common.TextUtil.Fold(v.Title).Contains(RevitDwgUnifier.Tools.Common.TextUtil.Fold(SearchText.Trim()));
        }

        private void SetVisibleSelection(bool value)
        {
            foreach (SelectableView v in ItemsView) v.IsSelected = value;
        }

        private void OnSelectionChanged()
        {
            if (Items == null) return;
            var sel = Items.Where(i => i.IsSelected).ToList();
            int sheets = sel.Count(s => s.Info.IsSheet);
            int views = sel.Count - sheets;
            var coveredBySheets = new HashSet<long>(sel.Where(s => s.Info.IsSheet).SelectMany(s => s.Info.PlacedViewIds));
            int redundant = sel.Count(s => !s.Info.IsSheet && coveredBySheets.Contains(s.Info.Id));
            Status = sel.Count == 0 ? "Nada marcado ainda: escolha folhas e/ou vistas na lista."
                   : !AnyFormat ? "Escolha pelo menos um formato (PDF, DWG único, DWG por prancha ou PNG)."
                   : $"{sheets} folha(s) e {views} vista(s)  →  " + Outputs(sel.Count) +
                     (redundant > 0 && ExportDwgUnified ? $"  ·  {redundant} vista(s) já aparecem numa folha marcada" : "");
            Raise(nameof(HasSelection));
            Raise(nameof(CanExport));
            Raise(nameof(PatternPreview));
            Raise(nameof(SelectionBadge));
            Raise(nameof(ExportButtonText));
        }
    }
}
