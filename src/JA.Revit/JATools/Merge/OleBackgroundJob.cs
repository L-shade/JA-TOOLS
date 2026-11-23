using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using RevitDwgUnifier.Contracts;
using RevitDwgUnifier.Infrastructure;

namespace RevitDwgUnifier.Merge
{
    /// <summary>
    /// Embeds the images of a delivered DWG as OLE objects WITHOUT blocking Revit: full AutoCAD
    /// runs from a background thread, a small window in the corner of the screen (its own UI
    /// thread, never Revit's) shows what is happening and can cancel, and when AutoCAD finishes
    /// the delivered DWG is replaced (waiting while it is open somewhere; if it stays locked the
    /// result is saved next to it as "_OLE.dwg"). Nothing here touches the Revit API.
    /// </summary>
    public sealed class OleBackgroundJob
    {
        public CoreConsoleInstall Install;
        public TimeSpan Timeout;
        public string MergedDwg, EngineDll, WorkDir, DwgVersion, TargetDwg, ImageFolder;
        public bool DeleteImageFolder, KeepWork;
        public int ImageCount;
        /// <summary>Started when this one ends (DWG por prancha: one AutoCAD at a time, file after file).</summary>
        public OleBackgroundJob Next;

        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private Dispatcher _ui;
        private Window _win;
        private TextBlock _title, _status;
        private ProgressBar _bar;
        private Button _cancel, _close, _open;

        public static void Start(OleBackgroundJob job)
        {
            var worker = new Thread(job.Work) { IsBackground = true, Name = "JA Tools - OLE" };
            worker.Start();
        }

        private void Work()
        {
            StartWindow();
            string name = Path.GetFileName(TargetDwg);
            try
            {
                SetStatus($"Abrindo o AutoCAD {Install?.Year}…", null);
                var engine = new OleEmbedEngine(Install, Timeout) { Report = s => SetStatus(s, null) };
                MergeResult r = engine.Run(MergedDwg, EngineDll, WorkDir, DwgVersion, _cts.Token);
                foreach (string stat in r.Statistics ?? new System.Collections.Generic.List<string>()) Logger.Info("AutoCAD (OLE): " + stat);
                foreach (string w in r.Warnings ?? new System.Collections.Generic.List<string>()) Logger.Warn("AutoCAD (OLE): " + w);

                if (!r.Success || string.IsNullOrEmpty(r.OutputDwgPath) || !File.Exists(r.OutputDwgPath))
                {
                    string err = r.Errors != null && r.Errors.Count > 0 ? r.Errors[0] : "erro desconhecido";
                    Logger.Warn("OLE em segundo plano: " + err);
                    Finish(false, _cts.IsCancellationRequested
                        ? $"Cancelado. {name} continua com as imagens vinculadas à pasta {Path.GetFileName(ImageFolder)}."
                        : $"As imagens não foram incorporadas ({err}). {name} continua com as imagens vinculadas à pasta {Path.GetFileName(ImageFolder)}.");
                    return;
                }

                // replace the delivered file (it may be open in AutoCAD for a while)
                string saved = null;
                var waited = Stopwatch.StartNew();
                while (saved == null && waited.Elapsed < TimeSpan.FromMinutes(10) && !_cts.IsCancellationRequested)
                {
                    try { File.Copy(r.OutputDwgPath, TargetDwg, true); saved = TargetDwg; }
                    catch (IOException)
                    {
                        SetStatus($"{name} está aberto em outro programa: feche-o para ele receber as imagens…", null);
                        Thread.Sleep(3000);
                    }
                    catch (UnauthorizedAccessException) { break; }
                }
                if (saved == null)
                {
                    saved = Path.Combine(Path.GetDirectoryName(TargetDwg), Path.GetFileNameWithoutExtension(TargetDwg) + "_OLE.dwg");
                    File.Copy(r.OutputDwgPath, saved, true);
                }
                if (r.ImagesFailed == 0 && DeleteImageFolder) FileSafety.TryDeleteDirectory(ImageFolder);
                Logger.Info($"OLE em segundo plano concluído: {saved}");
                Finish(true, r.ImagesFailed == 0
                    ? $"Pronto: {r.ImageCount} imagem(ns) incorporadas em {Path.GetFileName(saved)}."
                    : $"{r.ImageCount} imagem(ns) incorporadas em {Path.GetFileName(saved)}; {r.ImagesFailed} continuam vinculadas à pasta {Path.GetFileName(ImageFolder)}.");
            }
            catch (Exception ex)
            {
                Logger.Error("OLE em segundo plano falhou.", ex);
                Finish(false, $"As imagens não foram incorporadas ({ex.Message}). {name} continua com as imagens vinculadas.");
            }
            finally
            {
                if (!KeepWork) FileSafety.TryDeleteDirectory(WorkDir);
                if (Next != null)
                {
                    try { Start(Next); }
                    catch (Exception ex) { Logger.Error("OLE do próximo DWG não iniciou.", ex); }
                }
            }
        }

        // ------------------------------------------------------------------ window (own UI thread)
        private void StartWindow()
        {
            var ready = new ManualResetEventSlim(false);
            var ui = new Thread(() =>
            {
                try
                {
                    _ui = Dispatcher.CurrentDispatcher;
                    BuildWindow();
                    _win.Show();
                }
                catch (Exception ex) { Logger.Warn("Janela do OLE não aberta: " + ex.Message); }
                finally { ready.Set(); }
                Dispatcher.Run();
            }) { IsBackground = true, Name = "JA Tools - OLE UI" };
            ui.SetApartmentState(ApartmentState.STA);
            ui.Start();
            ready.Wait(10000);
        }

        private void BuildWindow()
        {
            var ink = new SolidColorBrush(Color.FromRgb(0x0C, 0x34, 0x42));
            var muted = new SolidColorBrush(Color.FromRgb(0x5B, 0x67, 0x72));
            _win = new Window
            {
                Title = "JA Tools – imagens no DWG",
                Width = 390, SizeToContent = SizeToContent.Height,
                WindowStyle = WindowStyle.ToolWindow, ResizeMode = ResizeMode.NoResize,
                Topmost = true, ShowInTaskbar = true, ShowActivated = false,
                Background = Brushes.White, FontFamily = new FontFamily("Segoe UI"), FontSize = 12.5
            };
            var root = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };
            _title = new TextBlock { Text = $"Incorporando {ImageCount} imagem(ns) em {Path.GetFileName(TargetDwg)}", FontWeight = FontWeights.SemiBold, Foreground = ink, TextWrapping = TextWrapping.Wrap };
            _status = new TextBlock { Text = "Preparando…", Foreground = muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            _bar = new ProgressBar { Height = 6, IsIndeterminate = true, Margin = new Thickness(0, 10, 0, 0), Foreground = ink };
            var note = new TextBlock { Text = "Pode continuar usando o Revit. O DWG já está salvo com as imagens vinculadas e é atualizado sozinho no fim.", Foreground = muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            _open = new Button { Content = "Abrir pasta", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0), Visibility = Visibility.Collapsed };
            _cancel = new Button { Content = "Cancelar", Padding = new Thickness(10, 3, 10, 3) };
            _close = new Button { Content = "Fechar", Padding = new Thickness(10, 3, 10, 3), Visibility = Visibility.Collapsed };
            _open.Click += (s, e) => { try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + TargetDwg + "\"") { UseShellExecute = true }); } catch { } };
            _cancel.Click += (s, e) => { _cancel.IsEnabled = false; _status.Text = "Cancelando…"; _cts.Cancel(); };
            _close.Click += (s, e) => _win.Close();
            _win.Closed += (s, e) => { if (_close.Visibility != Visibility.Visible) _cts.Cancel(); _ui.BeginInvokeShutdown(DispatcherPriority.Background); };
            buttons.Children.Add(_open); buttons.Children.Add(_cancel); buttons.Children.Add(_close);
            root.Children.Add(_title); root.Children.Add(_status); root.Children.Add(_bar); root.Children.Add(note); root.Children.Add(buttons);
            _win.Content = root;
            _win.Loaded += (s, e) =>
            {
                Rect area = SystemParameters.WorkArea;
                _win.Left = area.Right - _win.ActualWidth - 16;
                _win.Top = area.Bottom - _win.ActualHeight - 16;
            };
        }

        private void SetStatus(string text, bool? ok)
        {
            Dispatcher d = _ui;
            if (d == null || d.HasShutdownStarted) return;
            d.BeginInvoke(new Action(() =>
            {
                if (_status != null && !_cts.IsCancellationRequested || ok != null) _status.Text = text;
            }));
        }

        private void Finish(bool ok, string text)
        {
            Dispatcher d = _ui;
            if (d == null || d.HasShutdownStarted) return;
            d.BeginInvoke(new Action(() =>
            {
                if (_win == null) return;
                _title.Text = ok ? "Imagens incorporadas no DWG" : "Imagens não incorporadas";
                _status.Text = text;
                _bar.IsIndeterminate = false;
                _bar.Value = ok ? 100 : 0;
                _cancel.Visibility = Visibility.Collapsed;
                _close.Visibility = Visibility.Visible;
                _open.Visibility = Visibility.Visible;
                if (ok)
                {
                    // success closes itself after a while; a failure stays until it is read
                    var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
                    t.Tick += (s, e) => { t.Stop(); try { _win.Close(); } catch { } };
                    t.Start();
                }
            }));
        }
    }
}
