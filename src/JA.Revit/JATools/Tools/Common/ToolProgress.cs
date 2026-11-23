using System;
using System.Windows.Interop;
using System.Windows.Media;
using RevitDwgUnifier.Infrastructure;
using RevitDwgUnifier.UI;

namespace RevitDwgUnifier.Tools.Common
{
    /// <summary>
    /// The "carregando" box of every JA Tools tool: the moment the user clicks the main button of
    /// the options window, a progress window opens over Revit (title, steps, bar, elapsed time) and
    /// stays until the result window shows. Nothing to do in a tool for this:
    ///   * ToolCommand opens/closes the session; OptionsDialog arms it on OK (and hides it while an
    ///     options window is open); ToolCommand.Show closes it before the result;
    ///   * every transaction started with FailureSwallower repaints it.
    /// Tools that know their work give steps and counts (<see cref="Setup"/>, <see cref="Stage"/>,
    /// <see cref="Report"/>) and may offer Cancel (<see cref="Cancelled"/>).
    /// Tools that make the user click in the view call <see cref="Interactive"/> (the box would be in
    /// the way): RevitUtil.PickPoint does it, and ToolCommand.Interactive = true does it up front.
    /// Revit runs the tools on its UI thread: the window repaints when the tool reports.
    /// </summary>
    public static class ToolProgress
    {
        private static ProgressWindow _win;
        private static IntPtr _owner;
        private static string _title = "JA Tools", _headline;
        private static Color _accent = Color.FromRgb(0x2B, 0x57, 0x9A);
        private static string[] _stages;
        private static bool _armed, _interactive, _cancellable;

        /// <summary>A tool starts (called by ToolCommand).</summary>
        internal static void Begin(IntPtr owner, string title, bool interactive)
        {
            End();
            _owner = owner;
            _title = title ?? "JA Tools";
            _headline = null;
            _stages = null;
            _cancellable = false;
            _interactive = interactive;
            try { _accent = ToolCatalog.Get(_title).Accent; } catch { }
        }

        /// <summary>The tool ended or shows its result: the box closes.</summary>
        public static void End()
        {
            _armed = false;
            if (_win == null) return;
            try { _win.Close(); } catch { }
            _win = null;
        }

        /// <summary>The options were confirmed: the box opens now ("Processando…").</summary>
        public static void Arm()
        {
            if (_interactive) return;
            _armed = true;
            Pulse(null);
        }

        /// <summary>Headline and steps of the job; cancellable = the tool checks <see cref="Cancelled"/>.</summary>
        public static void Setup(string headline, string[] stages, bool cancellable = false)
        {
            if (_win != null) { try { _win.Close(); } catch { } _win = null; }
            _headline = headline;
            _stages = stages != null && stages.Length > 0 ? stages : null;
            _cancellable = cancellable;
            _interactive = false;
            _armed = true;
            Ensure();
        }

        /// <summary>Starts a step (index in the steps of <see cref="Setup"/>).</summary>
        public static void Stage(int stage, string message)
        {
            if (!Ensure()) return;
            try { _win.BeginStage(Math.Max(0, Math.Min(stage, _win.StageCount - 1)), message); } catch { }
        }

        /// <summary>current of total done, and what is being done now.</summary>
        public static void Report(int current, int total, string message)
        {
            if (!Ensure()) return;
            try { _win.Report(current, total, message); } catch { }
        }

        /// <summary>Repaints the box (and shows the message, if any) without a count.</summary>
        public static void Pulse(string message)
        {
            if (!Ensure()) return;
            try { _win.Pulse(message); } catch { }
        }

        /// <summary>Hides the box while another window is open; the next report shows it again.</summary>
        public static void Hide()
        {
            if (_win == null) return;
            try { if (_win.IsVisible) _win.Hide(); } catch { }
        }

        /// <summary>The tool makes the user click in the view: no box for the rest of this run.</summary>
        public static void Interactive()
        {
            _interactive = true;
            _armed = false;
            Hide();
        }

        /// <summary>The user clicked Cancel (only offered after Setup(..., cancellable: true)).</summary>
        public static bool Cancelled
        {
            get
            {
                try { return _win != null && _win.Token.IsCancellationRequested; } catch { return false; }
            }
        }

        private static bool Ensure()
        {
            if (!_armed) return false;
            try
            {
                if (_win == null)
                {
                    string[] stages = _stages ?? new[] { "Processar", "Concluir" };
                    _win = new ProgressWindow(ToolCatalog.Marca(_title) + " — " + _title, _headline ?? _title, stages, _accent, _cancellable);
                    if (_owner != IntPtr.Zero) new WindowInteropHelper(_win).Owner = _owner;
                    _win.Show();
                    _win.Pulse(_stages == null ? "Processando…" : "Preparando…");
                }
                else if (!_win.IsVisible) _win.Show();
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn("[JA Tools] Janela de andamento: " + ex.Message);
                _armed = false;
                _win = null;
                return false;
            }
        }
    }
}
