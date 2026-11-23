using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using RevitDwgUnifier.Tools.Common;
using DB = Autodesk.Revit.DB;

namespace RevitDwgUnifier.Tools.Parameters
{
    /// <summary>One row of the level table (elevation and height in meters).</summary>
    public sealed class LevelRow : INotifyPropertyChanged
    {
        private string _name;
        private double _elevation;
        private bool _delete;
        private double? _height;

        public DB.ElementId Id { get; set; }
        public string OriginalName { get; set; }
        public string Name { get => _name; set { _name = value; On(nameof(Name)); } }

        public double ElevationM
        {
            get => _elevation;
            set { if (Math.Abs(_elevation - value) < 1e-9) return; _elevation = value; On(nameof(ElevationM)); }
        }

        public bool Delete { get => _delete; set { if (_delete == value) return; _delete = value; On(nameof(Delete)); } }

        /// <summary>Height up to the level above (m); empty for the top level. Typing a height
        /// moves the level above (and, as chosen, every level above it).</summary>
        public double? HeightM
        {
            get => _height;
            set
            {
                if (value.HasValue && _height.HasValue && Math.Abs(value.Value - _height.Value) < 1e-9) return;
                HeightEdited?.Invoke(this, value);
            }
        }

        internal Action<LevelRow, double?> HeightEdited;

        internal void SetHeight(double? h)
        {
            _height = h;
            On(nameof(HeightM));
        }

        /// <summary>Text for the "revincular" lists.</summary>
        public override string ToString() => $"{Name}  ({ElevationM:0.000} m)";

        public event PropertyChangedEventHandler PropertyChanged;
        private void On(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    /// <summary>"Revincular": every element of From goes to To.</summary>
    public sealed class LevelMove
    {
        public LevelRow From, To;
        public override string ToString() => $"{From?.Name}  →  {To?.Name}";
    }

    /// <summary>Table editor for all levels (built in code, no XAML).</summary>
    public sealed class LevelEditorWindow : Window
    {
        public ObservableCollection<LevelRow> Rows { get; }
        public bool RenameViews => _renameViews.IsChecked == true;
        public bool CreatePlans => _createPlans.IsChecked == true;

        /// <summary>"Revincular": every element of the first level goes to the second one.</summary>
        public ObservableCollection<LevelMove> Moves { get; } = new ObservableCollection<LevelMove>();

        private readonly CheckBox _moveAbove = new CheckBox
        {
            Content = new TextBlock { Text = "Ao mudar uma altura, mover junto os níveis de cima (mantém as alturas deles)", TextWrapping = TextWrapping.Wrap },
            IsChecked = true, Margin = new Thickness(0, 6, 0, 0),
            ToolTip = "Desmarcado: só o nível logo acima se move (a altura dele até o próximo muda)."
        };
        private TextBlock _heightNote;

        private readonly CheckBox _renameViews = new CheckBox { Content = new TextBlock { Text = "Renomear também as plantas que têm o nome antigo do nível", TextWrapping = TextWrapping.Wrap }, IsChecked = true, Margin = new Thickness(0, 6, 0, 0) };
        private readonly CheckBox _createPlans = new CheckBox { Content = new TextBlock { Text = "Criar planta de piso para os níveis novos", TextWrapping = TextWrapping.Wrap }, IsChecked = true, Margin = new Thickness(0, 2, 0, 0) };

        public LevelEditorWindow(IEnumerable<LevelRow> rows)
        {
            Rows = new ObservableCollection<LevelRow>(rows.OrderByDescending(r => r.ElevationM));
            foreach (LevelRow r in Rows) Hook(r);
            Rows.CollectionChanged += (s, e) =>
            {
                if (e.NewItems != null) foreach (LevelRow r in e.NewItems) Hook(r);
                Recompute();
            };
            Recompute();
            // wide and as tall as the screen allows: the level table always keeps room for its rows
            double workH = SystemParameters.WorkArea.Height, workW = SystemParameters.WorkArea.Width;
            Width = Math.Min(1040, Math.Max(760, workW - 80));
            Height = Math.Min(900, Math.Max(600, workH - 40));
            MinWidth = 760;
            MinHeight = 520;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var grid = new DataGrid
            {
                ItemsSource = Rows,
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                RowHeight = 28,
                BorderThickness = new Thickness(0),
                Background = System.Windows.Media.Brushes.White,
                SelectionUnit = DataGridSelectionUnit.FullRow,
                CanUserResizeColumns = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            grid.SetResourceReference(DataGrid.HorizontalGridLinesBrushProperty, "Ja.Line");
            grid.Columns.Add(new DataGridTextColumn { Header = "Nome", Binding = new Binding("Name"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "Elevação (m)",
                Binding = new Binding("ElevationM") { StringFormat = "0.000", ConverterCulture = System.Globalization.CultureInfo.CurrentCulture },
                Width = new DataGridLength(120)
            });
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "Altura até o de cima (m)",
                Binding = new Binding("HeightM")
                {
                    StringFormat = "0.000", TargetNullValue = "", ConverterCulture = System.Globalization.CultureInfo.CurrentCulture,
                    UpdateSourceTrigger = UpdateSourceTrigger.LostFocus
                },
                Width = new DataGridLength(170)
            });
            grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Excluir", Binding = new Binding("Delete") { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = new DataGridLength(70) });

            var add = ToolChrome.Secondary("+  Novo nível", null, "Acrescenta um nível 3 m acima do mais alto (edite nome e elevação na tabela).");
            add.HorizontalAlignment = HorizontalAlignment.Left;
            add.Margin = new Thickness(0, 10, 0, 0);
            add.Click += (s, e) =>
            {
                double top = Rows.Count > 0 ? Rows.Max(r => r.ElevationM) : 0;
                var row = new LevelRow { Id = DB.ElementId.InvalidElementId, Name = "Novo nível", ElevationM = top + 3.0 };
                Rows.Insert(0, row);
                grid.SelectedItem = row;
                grid.ScrollIntoView(row);
            };

            var table = new DockPanel();
            var hint = ToolChrome.Muted("Clique duas vezes numa célula para editar a elevação ou a altura até o nível de cima (metros; vírgula ou ponto).");
            hint.TextWrapping = TextWrapping.Wrap;
            hint.Margin = new Thickness(0, 0, 0, 8);
            DockPanel.SetDock(hint, Dock.Top);
            table.Children.Add(hint);
            DockPanel.SetDock(add, Dock.Bottom);
            table.Children.Add(add);
            var frame = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = grid, ClipToBounds = true };
            frame.SetResourceReference(Border.BorderBrushProperty, "Ja.Line");
            table.Children.Add(frame);
            var tableCard = ToolChrome.Card(table, "Níveis do projeto", ToolChrome.Badge(out TextBlock count));
            count.Text = Rows.Count + " níveis";
            Rows.CollectionChanged += (s, e) => count.Text = Rows.Count + " níveis";

            // ---- revincular
            var existing = Rows.Where(r => r.Id != null && r.Id != DB.ElementId.InvalidElementId).ToList();
            var from = new ComboBox { ItemsSource = existing, MinWidth = 190, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Nível de onde os elementos saem" };
            var to = new ComboBox { ItemsSource = Rows, MinWidth = 190, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Nível que passa a hospedar os elementos (pode ser um nível novo desta tabela)" };
            var addMove = ToolChrome.Secondary("Adicionar", null, "Inclui na lista; tudo é aplicado em \"Aplicar alterações\"");
            var moveList = new ListBox { ItemsSource = Moves, MinHeight = 30, MaxHeight = 72, Margin = new Thickness(0, 8, 0, 0) };
            var removeMove = ToolChrome.Ghost("Remover da lista", "Tira o revínculo selecionado");
            removeMove.HorizontalAlignment = HorizontalAlignment.Left;
            var moveMsg = ToolChrome.Muted("");
            addMove.Click += (s, e) =>
            {
                if (!(from.SelectedItem is LevelRow a) || !(to.SelectedItem is LevelRow b)) { moveMsg.Text = "Escolha o nível de origem e o de destino."; return; }
                if (a == b) { moveMsg.Text = "Origem e destino são o mesmo nível."; return; }
                if (Moves.Any(m => m.From == a)) { moveMsg.Text = "Esse nível de origem já está na lista."; return; }
                Moves.Add(new LevelMove { From = a, To = b });
                moveMsg.Text = "";
            };
            removeMove.Click += (s, e) => { if (moveList.SelectedItem is LevelMove m) Moves.Remove(m); };
            var pickRow = new WrapPanel();
            pickRow.Children.Add(new TextBlock { Text = "De", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            pickRow.Children.Add(from);
            pickRow.Children.Add(new TextBlock { Text = "para", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            pickRow.Children.Add(to);
            pickRow.Children.Add(addMove);
            var moveStack = new StackPanel();
            var moveHint = ToolChrome.Muted("Os elementos do nível de origem passam para o de destino SEM sair do lugar (o deslocamento é corrigido): cotas, etiquetas e vistas continuam valendo.");
            moveHint.ToolTip = "Paredes, pisos, forros, telhados, pilares, escadas, vigas, mobiliário, louças e demais famílias por nível. " +
                               "Portas e janelas seguem a parede; ambientes não mudam de nível no Revit (são avisados no relatório).";
            moveHint.TextWrapping = TextWrapping.Wrap;
            moveHint.Margin = new Thickness(0, 0, 0, 8);
            moveStack.Children.Add(moveHint);
            moveStack.Children.Add(pickRow);
            moveStack.Children.Add(moveList);
            moveStack.Children.Add(removeMove);
            moveStack.Children.Add(moveMsg);
            var moveCard = ToolChrome.Card(moveStack, "Revincular elementos de um nível para outro");
            moveCard.Margin = new Thickness(0);

            var optStack = new StackPanel();
            optStack.Children.Add(_moveAbove);
            _heightNote = ToolChrome.Muted("");
            optStack.Children.Add(_heightNote);
            optStack.Children.Add(_renameViews);
            optStack.Children.Add(_createPlans);
            var warn = ToolChrome.Muted("Atenção: excluir um nível exclui as vistas dele (com cotas e anotações) e os elementos ainda nele. Revincule antes para mantê-los (a revinculação é feita antes da exclusão).");
            warn.TextWrapping = TextWrapping.Wrap;
            warn.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Warn");
            warn.Margin = new Thickness(0, 8, 0, 0);
            optStack.Children.Add(warn);
            var optCard = ToolChrome.Card(optStack, "Ao aplicar");
            optCard.Margin = new Thickness(0);

            // table on top (takes the free height); the two option cards side by side below it.
            // Both dividers can be dragged: table ↕ cards, and "Revincular" ↔ "Ao aplicar".
            var lower = new Grid();
            lower.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star), MinWidth = 260 });
            lower.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            lower.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 220 });
            Grid.SetColumn(moveCard, 0);
            Grid.SetColumn(optCard, 2);
            lower.Children.Add(moveCard);
            lower.Children.Add(Splitter(false, 1));
            lower.Children.Add(optCard);
            // the cards scroll when the divider makes their area smaller than their content
            var lowerScroll = new ScrollViewer
            {
                Content = lower,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            var body = new Grid();
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 200 });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto, MinHeight = 110 });
            Grid.SetRow(tableCard, 0);
            Grid.SetRow(lowerScroll, 2);
            body.Children.Add(tableCard);
            body.Children.Add(Splitter(true, 1));
            body.Children.Add(lowerScroll);

            var ok = ToolChrome.Primary("Aplicar alterações");
            ok.IsDefault = true;
            ok.MinWidth = 140;
            var cancel = ToolChrome.Ghost("Cancelar");
            cancel.IsCancel = true;
            cancel.MinWidth = 90;
            cancel.Margin = new Thickness(0, 0, 8, 0);
            ok.Click += (s, e) => { grid.CommitEdit(DataGridEditingUnit.Row, true); DialogResult = true; };
            var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            bottom.Children.Add(cancel);
            bottom.Children.Add(ok);

            ToolChrome.DressCompact(this, "Editar Níveis", body, bottom);
        }

        /// <summary>Draggable divider between two areas (horizontal = resizes rows).</summary>
        private static GridSplitter Splitter(bool horizontal, int index)
        {
            var sp = new GridSplitter
            {
                ShowsPreview = false,
                Focusable = false,
                ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                ToolTip = "Arraste para redimensionar"
            };
            sp.SetResourceReference(Control.BackgroundProperty, "Ja.Line");
            if (horizontal)
            {
                sp.Height = 5;
                sp.Margin = new Thickness(0, 5, 0, 5);
                sp.HorizontalAlignment = HorizontalAlignment.Stretch;
                sp.VerticalAlignment = VerticalAlignment.Center;
                sp.ResizeDirection = GridResizeDirection.Rows;
                sp.Cursor = System.Windows.Input.Cursors.SizeNS;
                Grid.SetRow(sp, index);
            }
            else
            {
                sp.Width = 5;
                sp.Margin = new Thickness(5, 0, 5, 0);
                sp.VerticalAlignment = VerticalAlignment.Stretch;
                sp.HorizontalAlignment = HorizontalAlignment.Center;
                sp.ResizeDirection = GridResizeDirection.Columns;
                sp.Cursor = System.Windows.Input.Cursors.SizeWE;
                Grid.SetColumn(sp, index);
            }
            return sp;
        }

        private void Hook(LevelRow r)
        {
            r.HeightEdited = OnHeightEdited;
            r.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(LevelRow.ElevationM) || e.PropertyName == nameof(LevelRow.Delete)) Recompute();
            };
        }

        private bool _recomputing;

        /// <summary>Height of each level up to the next one above (deleted levels do not count).</summary>
        private void Recompute()
        {
            if (_recomputing) return;
            _recomputing = true;
            try
            {
                var live = Rows.Where(r => !r.Delete).OrderBy(r => r.ElevationM).ToList();
                foreach (LevelRow r in Rows.Where(r => r.Delete)) r.SetHeight(null);
                for (int i = 0; i < live.Count; i++)
                    live[i].SetHeight(i + 1 < live.Count ? Math.Round(live[i + 1].ElevationM - live[i].ElevationM, 4) : (double?)null);
            }
            finally { _recomputing = false; }
        }

        /// <summary>A height typed in the table: the level above goes to elevation + height; with the
        /// option on, every level above moves by the same amount (their heights stay).</summary>
        private void OnHeightEdited(LevelRow row, double? h)
        {
            var live = Rows.Where(r => !r.Delete).OrderBy(r => r.ElevationM).ToList();
            int i = live.IndexOf(row);
            if (h == null || h.Value <= 0 || i < 0 || i == live.Count - 1)
            {
                if (_heightNote != null)
                    _heightNote.Text = i == live.Count - 1 ? "O nível mais alto não tem nível acima: mude a elevação dele." : "A altura precisa ser maior que zero.";
                Recompute();
                return;
            }
            LevelRow next = live[i + 1];
            double delta = Math.Round(row.ElevationM + h.Value - next.ElevationM, 4);
            if (Math.Abs(delta) < 1e-9) return;
            _recomputing = true;
            try
            {
                if (_moveAbove.IsChecked == true)
                    foreach (LevelRow r in live.Skip(i + 1)) r.ElevationM = Math.Round(r.ElevationM + delta, 4);
                else
                {
                    if (i + 2 < live.Count && next.ElevationM + delta >= live[i + 2].ElevationM)
                    {
                        _recomputing = false;
                        if (_heightNote != null) _heightNote.Text = $"Com essa altura, {next.Name} passaria do nível acima dele: marque a opção de mover os níveis de cima.";
                        Recompute();
                        return;
                    }
                    next.ElevationM = Math.Round(next.ElevationM + delta, 4);
                }
            }
            finally { _recomputing = false; }
            if (_heightNote != null) _heightNote.Text = "";
            Recompute();
        }
    }
}
