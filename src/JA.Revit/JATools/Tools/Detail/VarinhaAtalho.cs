using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Autodesk.Revit.UI;
using RevitDwgUnifier.Infrastructure;
using RevitDwgUnifier.Tools.Common;

namespace RevitDwgUnifier.Tools.Detail
{
    /// <summary>
    /// Keyboard shortcut of the Varinha Mágica (default Space, as in ArchiCAD; the user can change it
    /// or turn it off in the wand's panel). A low-level keyboard hook watches the key while the
    /// Revit window is in front; it starts the wand through an ExternalEvent, and ends a running
    /// wand when pressed again.
    ///
    /// It stays out of the way of Revit's own uses of the key: nothing happens when the focus is in
    /// a text field, when something is selected (Space flips doors and windows), when the view
    /// doesn't take the wand, or when Revit is busy in a command (Space rotates while placing) — an
    /// event that waited more than a moment for Revit to become idle is dropped.
    /// </summary>
    internal static class VarinhaAtalho
    {
        public const string Desligado = "Desligado";
        public static readonly string[] Opcoes = { "Espaço", "Ctrl+Espaço", "Shift+Espaço", "F2", "F3", "F4", "F6", "F7", "F8", "F9", Desligado };
        private const string PrefKey = "VarinhaMagica.atalho";

        /// <summary>True while a wand session is picking (the shortcut then ends it).</summary>
        public static volatile bool SessaoAtiva;

        private static IntPtr _hook = IntPtr.Zero;
        private static LowLevelKeyboardProc _proc;     // kept alive: the hook calls it
        private static ExternalEvent _event;
        private static IntPtr _main;
        private static bool _down;
        private static DateTime _raised;

        public static string Atual
        {
            get
            {
                string s = ToolPrefs.Get(PrefKey);
                return Array.IndexOf(Opcoes, s) >= 0 ? s : Opcoes[0];
            }
        }

        public static void Definir(string atalho)
        {
            ToolPrefs.Set(PrefKey, Array.IndexOf(Opcoes, atalho) >= 0 ? atalho : Opcoes[0]);
            ToolPrefs.Save();
            Logger.Info("[JA Tools] Atalho da Varinha Mágica: " + Atual);
        }

        /// <summary>Called once at startup.</summary>
        public static void Iniciar(UIControlledApplication app)
        {
            try
            {
                _main = app.MainWindowHandle;
                _event = ExternalEvent.Create(new Handler());
                _proc = Callback;
                using (var cur = Process.GetCurrentProcess())
                using (var mod = cur.MainModule)
                    _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(mod.ModuleName), 0);
                if (_hook == IntPtr.Zero) Logger.Warn("[JA Tools] Atalho da Varinha Mágica indisponível (gancho de teclado recusado).");
            }
            catch (Exception ex) { Logger.Warn("[JA Tools] Atalho da Varinha Mágica não iniciado: " + ex.Message); }
        }

        public static void Parar()
        {
            try { if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook); } catch { }
            _hook = IntPtr.Zero;
        }

        /// <summary>Ends a running wand: Revit gets an Esc (its pick is cancelled).</summary>
        public static void Encerrar()
        {
            // never inject from inside the keyboard hook: Windows may drop or reorder it. A moment later,
            // on another thread: Revit in front, then Esc to the window that has the keyboard (PostMessage)
            // and a real Esc keystroke as well — the pick ends with whichever arrives first.
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    System.Threading.Thread.Sleep(40);
                    if (_main != IntPtr.Zero) SetForegroundWindow(_main);
                    IntPtr alvo = FocoDoRevit();
                    if (alvo != IntPtr.Zero)
                    {
                        PostMessage(alvo, WM_KEYDOWN, (IntPtr)VK_ESCAPE, (IntPtr)0x00010001);
                        PostMessage(alvo, WM_KEYUP, (IntPtr)VK_ESCAPE, unchecked((IntPtr)(int)0xC0010001));
                    }
                    System.Threading.Thread.Sleep(30);
                    if (SessaoAtiva)
                    {
                        keybd_event(VK_ESCAPE, 0, 0, UIntPtr.Zero);
                        keybd_event(VK_ESCAPE, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                    }
                }
                catch { }
            });
        }

        private static IntPtr FocoDoRevit()
        {
            try
            {
                var info = new GUITHREADINFO { cbSize = Marshal.SizeOf(typeof(GUITHREADINFO)) };
                uint thread = GetWindowThreadProcessId(_main, out _);
                if (GetGUIThreadInfo(thread, ref info) && info.hwndFocus != IntPtr.Zero) return info.hwndFocus;
            }
            catch { }
            return _main;
        }

        /// <summary>The window in front is Revit or one of its own windows (the wand's panel).</summary>
        private static bool RevitNaFrente()
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == _main) return true;
            if (fg == IntPtr.Zero) return false;
            GetWindowThreadProcessId(fg, out uint pid);
            using (var cur = Process.GetCurrentProcess()) return pid == (uint)cur.Id;
        }

        /// <summary>Gives the keyboard back to Revit (after a click in the wand's panel).</summary>
        public static void DevolverFoco()
        {
            try { if (_main != IntPtr.Zero) SetForegroundWindow(_main); } catch { }
        }

        // ------------------------------------------------------------------ the hook
        private static bool Parse(string s, out int vk, out bool ctrl, out bool shift)
        {
            vk = 0; ctrl = false; shift = false;
            if (string.IsNullOrEmpty(s) || s == Desligado) return false;
            ctrl = s.StartsWith("Ctrl+", StringComparison.Ordinal);
            shift = s.StartsWith("Shift+", StringComparison.Ordinal);
            string key = s.Substring(s.LastIndexOf('+') + 1);
            if (key == "Espaço") vk = VK_SPACE;
            else if (key.Length >= 2 && key[0] == 'F' && int.TryParse(key.Substring(1), out int f) && f >= 1 && f <= 12) vk = VK_F1 + f - 1;
            return vk != 0;
        }

        private static bool _engolir;   // the key that ended the wand: its key-up is not passed on either

        private static IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try { if (nCode >= 0 && OnKey((int)wParam, Marshal.ReadInt32(lParam))) return (IntPtr)1; }
            catch { }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        /// <summary>True = the key is swallowed (Revit doesn't get it).</summary>
        private static bool OnKey(int msg, int vkCode)
        {
            if (!Parse(Atual, out int vk, out bool ctrl, out bool shift) || vkCode != vk) return false;
            if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
            {
                _down = false;
                bool e = _engolir; _engolir = false;
                return e;
            }
            if (msg != WM_KEYDOWN && msg != WM_SYSKEYDOWN) return false;
            if (_down) return SessaoAtiva || _engolir;           // auto-repeat while held
            _down = true;

            if (Down(VK_CONTROL) != ctrl || Down(VK_SHIFT) != shift || Down(VK_MENU)) return false;
            if (SessaoAtiva)
            {
                // the wand is running: the key ends it (Revit or the wand's panel in front)
                if (!RevitNaFrente()) return false;
                _engolir = true;
                Encerrar();
                return true;
            }
            if (GetForegroundWindow() != _main) return false;    // other programs, floating palettes, dialogs
            if (Digitando() || !NaVista()) return false;
            _raised = DateTime.UtcNow;
            _event?.Raise();
            return false;
        }

        private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

        /// <summary>The keyboard is in a text field (Properties, a search box...): the key is a character.</summary>
        private static bool Digitando()
        {
            try
            {
                var f = Keyboard.FocusedElement;
                if (f is TextBoxBase || f is PasswordBox) return true;
                if (f is System.Windows.Controls.ComboBox cb && cb.IsEditable) return true;
            }
            catch { }
            try
            {
                var info = new GUITHREADINFO { cbSize = Marshal.SizeOf(typeof(GUITHREADINFO)) };
                uint thread = GetWindowThreadProcessId(_main, out _);
                if (GetGUIThreadInfo(thread, ref info) && info.hwndFocus != IntPtr.Zero)
                {
                    var sb = new StringBuilder(64);
                    GetClassName(info.hwndFocus, sb, sb.Capacity);
                    string cls = sb.ToString();
                    if (cls.IndexOf("Edit", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
            }
            catch { }
            return false;
        }

        private static int _temMdi = -1;

        /// <summary>
        /// The keyboard is in the drawing area (a view of the MDI client), not in a palette, the
        /// Project Browser or the ribbon. When this Revit has no MDI client the check is skipped.
        /// </summary>
        private static bool NaVista()
        {
            try
            {
                var info = new GUITHREADINFO { cbSize = Marshal.SizeOf(typeof(GUITHREADINFO)) };
                uint thread = GetWindowThreadProcessId(_main, out _);
                IntPtr focus = GetGUIThreadInfo(thread, ref info) ? info.hwndFocus : IntPtr.Zero;
                for (IntPtr h = focus; h != IntPtr.Zero && h != _main; h = GetParent(h))
                    if (Classe(h) == "MDIClient") return true;
                if (_temMdi < 0)
                {
                    _temMdi = 0;
                    EnumChildWindows(_main, (h, l) => { if (Classe(h) == "MDIClient") { _temMdi = 1; return false; } return true; }, IntPtr.Zero);
                }
                return _temMdi == 0;
            }
            catch { return true; }
        }

        private static string Classe(IntPtr h)
        {
            var sb = new StringBuilder(64);
            GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }

        /// <summary>Runs the wand in Revit's API context.</summary>
        private sealed class Handler : IExternalEventHandler
        {
            public string GetName() => "JA Tools – Varinha Mágica (atalho)";

            public void Execute(UIApplication app)
            {
                // queued while Revit was busy (Space during a command): not meant for the wand
                if ((DateTime.UtcNow - _raised).TotalMilliseconds > 600) return;
                if (SessaoAtiva) return;
                UIDocument uidoc = app.ActiveUIDocument;
                if (uidoc?.Document == null || uidoc.Document.IsFamilyDocument) return;
                try { if (uidoc.Selection.GetElementIds().Count > 0) return; } catch { return; }

                var cmd = new VarinhaMagicaCommand();
                cmd.Vincular(app);
                SessionCache.Clear();
                try { cmd.Sessao(true); }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { }
                catch (ToolCancelledException) { }
                catch (Exception ex) { Logger.Error("[JA Tools] Varinha Mágica (atalho) falhou.", ex); }
                finally
                {
                    SessaoAtiva = false;
                    SessionCache.Clear();
                    Logger.Flush();
                }
            }
        }

        // ------------------------------------------------------------------ Win32
        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        private const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
        private const int VK_SPACE = 0x20, VK_F1 = 0x70, VK_CONTROL = 0x11, VK_SHIFT = 0x10, VK_MENU = 0x12;
        private const byte VK_ESCAPE = 0x1B;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct GUITHREADINFO
        {
            public int cbSize;
            public uint flags;
            public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
            public RECT rcCaret;
        }

        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string lpModuleName);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
        [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hWnd);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);
    }
}
