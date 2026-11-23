using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using RevitDwgUnifier.Core;

namespace RevitDwgUnifier.UI
{
    /// <summary>
    /// Progress of the DWG export, as a 4-step tracker (Exportar do Revit → Unir no AutoCAD →
    /// Incorporar imagens → Concluir) with the current item, a bar, elapsed time and an estimate.
    /// The user always knows where the job is and roughly how long it still takes.
    ///
    /// The Revit API is single-threaded, so the export runs on Revit's UI thread; <see cref="Report"/>
    /// pumps the dispatcher once per item so the window repaints and Cancel stays clickable
    /// (cancellation is checked at item boundaries and inside the merge wait loop).
    /// </summary>
    public sealed class ProgressWindow : Window, IProgressSink
    {
        /// <summary>Stages of the DWG único job (detected from its progress messages).</summary>
        public static readonly string[] DwgStages = { "Exportar do Revit", "Unir no AutoCAD", "Incorporar imagens" };

        private readonly string[] _stageNames;

        private readonly ProgressBar _bar = new ProgressBar { Height = 8, Minimum = 0, BorderThickness = new Thickness(0) };
        private readonly TextBlock _text = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 13 };
        private readonly TextBlock _detail = new TextBlock { FontSize = 11.5, Margin = new Thickness(0, 6, 0, 0) };
        private readonly TextBlock _time = new TextBlock { FontSize = 11.5, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        private readonly Border[] _dots;
        private readonly TextBlock[] _labels;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Stopwatch _clockStage = Stopwatch.StartNew();
        private readonly Button _cancel;
        private int _step = -1;
        private int _current, _total;
        private int _dwgOffset = -1;   // >= 0 while the DWG único job reports

        public ProgressWindow() : this("Exportando DWG único", new[] { "Exportar do Revit", "Unir no AutoCAD", "Incorporar imagens", "Concluir" })
        {
            _dwgOffset = 0;
        }

        /// <param name="stages">Names shown in the step tracker (last one is usually "Concluir").</param>
        public ProgressWindow(string headline, string[] stages)
            : this("JA Tools — Exportar Pranchas", headline, stages, Color.FromRgb(0x2B, 0x57, 0x9A), true)
        {
        }

        /// <summary>Progress of any JA Tools tool: window title, headline, steps, the colour of its
        /// ribbon group, and whether the tool can stop when Cancel is clicked.</summary>
        public ProgressWindow(string title, string headline, string[] stages, Color accent, bool cancellable)
        {
            _stageNames = stages;
            _dots = new Border[stages.Length];
            _labels = new TextBlock[stages.Length];
            Title = title;
            Width = Math.Max(520, 130 * stages.Length);
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            JaTheme.Apply(this, accent);

            var root = new StackPanel();

            // header
            var head = new Border { Padding = new Thickness(22, 16, 22, 14), BorderThickness = new Thickness(0, 4, 0, 1) };
            head.SetResourceReference(Border.BorderBrushProperty, "Ja.Accent");
            head.SetResourceReference(Border.BackgroundProperty, "Ja.Card");
            var hp = new StackPanel { Orientation = Orientation.Horizontal };
            hp.Children.Add(new Image { Source = IconFactory.Mark(64), Width = 32, Height = 32, Margin = new Thickness(0, 0, 12, 0) });
            var ht = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var t1 = new TextBlock { Text = headline, FontSize = 16, FontWeight = FontWeights.SemiBold };
            t1.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Ink");
            var t2 = new TextBlock { Text = "Pode continuar olhando — o Revit fica ocupado até terminar.", FontSize = 11.5 };
            t2.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Muted");
            ht.Children.Add(t1);
            ht.Children.Add(t2);
            hp.Children.Add(ht);
            head.Child = hp;
            root.Children.Add(head);

            // stepper
            var steps = new Grid { Margin = new Thickness(22, 18, 22, 4) };
            for (int i = 0; i < _stageNames.Length; i++)
            {
                steps.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                _dots[i] = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Center };
                _dots[i].Child = new TextBlock { Text = (i + 1).ToString(), FontSize = 11, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                _labels[i] = new TextBlock { Text = _stageNames[i], FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxWidth = 110, Margin = new Thickness(0, 5, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center };
                col.Children.Add(_dots[i]);
                col.Children.Add(_labels[i]);
                Grid.SetColumn(col, i);
                steps.Children.Add(col);
            }
            root.Children.Add(steps);

            // current item + bar
            var body = new StackPanel { Margin = new Thickness(22, 14, 22, 0) };
            _text.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Ink");
            body.Children.Add(_text);
            _bar.Margin = new Thickness(0, 10, 0, 0);
            _bar.SetResourceReference(Control.ForegroundProperty, "Ja.Accent");
            _bar.SetResourceReference(Control.BackgroundProperty, "Ja.Line");
            body.Children.Add(_bar);
            var info = new Grid();
            info.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            info.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _detail.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Muted");
            _time.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Muted");
            Grid.SetColumn(_time, 1);
            info.Children.Add(_detail);
            info.Children.Add(_time);
            body.Children.Add(info);
            root.Children.Add(body);

            // footer
            _cancel = new Button { Content = "Cancelar", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(22, 16, 22, 16), ToolTip = "Para depois da etapa atual; nada no modelo é alterado." };
            _cancel.SetResourceReference(StyleProperty, "Ja.Secondary");
            _cancel.Click += (s, e) =>
            {
                _cts.Cancel();
                _text.Text = "Cancelando após a etapa atual…";
                _cancel.IsEnabled = false;
            };
            if (!cancellable) _cancel.Visibility = Visibility.Collapsed;
            root.Children.Add(_cancel);
            Content = root;

            SetStep(0);
            _text.Text = "Preparando…";
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (s, e) => UpdateTime();
            timer.Start();
            Closed += (s, e) => timer.Stop();
        }

        public CancellationToken Token => _cts.Token;

        public int StageCount => _stageNames.Length;

        private bool _determinate;

        /// <summary>Keeps the window alive and repainted; with a message, shows it. Before any
        /// counted progress the bar runs as "working" (animated).</summary>
        public void Pulse(string message)
        {
            if (!string.IsNullOrEmpty(message)) _text.Text = message;
            if (!_determinate && _dwgOffset < 0) { _bar.IsIndeterminate = true; _detail.Text = ""; }
            UpdateTime();
            DoEvents();
        }

        /// <summary>Starts a simple stage (PDF, DWG por prancha...): the bar shows current/total.</summary>
        public void BeginStage(int stage, string message)
        {
            _dwgOffset = -1;
            SetStep(stage);
            _bar.IsIndeterminate = false;
            _bar.Value = 0;
            _determinate = true;
            _detail.Text = "";
            _text.Text = message;
            DoEvents();
        }

        /// <summary>From now on Report() comes from the DWG único job, whose stages start at <paramref name="offset"/>.</summary>
        public void BeginDwg(int offset)
        {
            _dwgOffset = offset;
            SetStep(offset);
            _clockStage.Restart();
            _text.Text = "Preparando o DWG único…";
            DoEvents();
        }

        /// <summary>Marks every stage as done (last one active) before closing.</summary>
        public void Finish(string message)
        {
            _dwgOffset = -1;
            SetStep(_stageNames.Length - 1);
            _bar.IsIndeterminate = false;
            _bar.Maximum = 1; _bar.Value = 1;
            _text.Text = message;
            DoEvents();
        }

        public void Report(int current, int total, string message)
        {
            _current = current;
            _total = Math.Max(1, total);
            string m = message ?? "";

            if (_dwgOffset < 0)
            {
                // simple stage: plain current/total
                _determinate = true;
                _bar.IsIndeterminate = false;
                _bar.Maximum = _total;
                _bar.Value = Math.Min(current, _total);
                _detail.Text = $"{Math.Min(current, _total)} de {_total}";
                _text.Text = m;
                UpdateTime();
                DoEvents();
                return;
            }

            int local = _step < _dwgOffset ? 0 : _step - _dwgOffset;
            if (m.IndexOf("OLE", StringComparison.OrdinalIgnoreCase) >= 0 || m.StartsWith("Incorporando", StringComparison.OrdinalIgnoreCase)) local = 2;
            else if (m.IndexOf("Unindo", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("união", StringComparison.OrdinalIgnoreCase) >= 0) local = 1;
            else if (m.StartsWith("Exportando", StringComparison.OrdinalIgnoreCase)) local = 0;
            SetStep(_dwgOffset + local);
            int step = local;

            if (step == 0)
            {
                _bar.IsIndeterminate = false;
                _bar.Maximum = Math.Max(1, _total - 1);
                _bar.Value = Math.Min(current, _bar.Maximum);
                _detail.Text = $"Item {Math.Min(current + 1, _total - 1)} de {Math.Max(1, _total - 1)}";
            }
            else
            {
                // AutoCAD steps report no item progress: an animated bar shows it is alive.
                _bar.IsIndeterminate = true;
                _detail.Text = step == 1 ? "O AutoCAD Core Console está montando os layouts…" : "Uma janela do AutoCAD abre por alguns segundos. Não use copiar/colar agora.";
            }
            _text.Text = m;
            UpdateTime();
            DoEvents();
        }

        private void SetStep(int step)
        {
            if (step == _step) return;
            _step = step;
            var accent = (Brush)FindResource("Ja.Accent");
            var line = (Brush)FindResource("Ja.Line");
            var ok = (Brush)FindResource("Ja.Ok");
            var muted = (Brush)FindResource("Ja.Muted");
            var ink = (Brush)FindResource("Ja.Ink");
            for (int i = 0; i < _stageNames.Length; i++)
            {
                var dot = _dots[i];
                var num = (TextBlock)dot.Child;
                if (i < step)
                {
                    dot.Background = ok; dot.BorderBrush = ok;
                    num.Text = "✓"; num.FontFamily = new FontFamily("Segoe UI Symbol"); num.Foreground = Brushes.White;
                    _labels[i].Foreground = muted; _labels[i].FontWeight = FontWeights.Normal;
                }
                else if (i == step)
                {
                    dot.Background = accent; dot.BorderBrush = accent;
                    num.Text = (i + 1).ToString(); num.FontFamily = FontFamily; num.Foreground = Brushes.White;
                    _labels[i].Foreground = ink; _labels[i].FontWeight = FontWeights.SemiBold;
                }
                else
                {
                    dot.Background = Brushes.White; dot.BorderBrush = line;
                    num.Text = (i + 1).ToString(); num.FontFamily = FontFamily; num.Foreground = muted;
                    _labels[i].Foreground = muted; _labels[i].FontWeight = FontWeights.Normal;
                }
            }
        }

        private void UpdateTime()
        {
            TimeSpan e = _clock.Elapsed;
            string text = "Tempo: " + Fmt(e);
            // Estimate only while exporting items (the AutoCAD steps have no measurable progress).
            if (_dwgOffset >= 0 && _step == _dwgOffset && _current >= 2 && _total > 2)
            {
                double perItem = _clockStage.Elapsed.TotalSeconds / _current;
                double left = perItem * Math.Max(0, (_total - 1) - _current);
                text += "  ·  restam ~" + Fmt(TimeSpan.FromSeconds(left)) + " nesta etapa";
            }
            else if (_dwgOffset < 0 && _determinate && _current >= 1 && _total > _current)
            {
                // counted work (rooms, sheets...): estimate from the average time per item so far
                double perItem = e.TotalSeconds / _current;
                text += "  ·  restam ~" + Fmt(TimeSpan.FromSeconds(perItem * (_total - _current)));
            }
            _time.Text = text;
        }

        private static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

        private static void DoEvents()
        {
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
        }
    }
}
