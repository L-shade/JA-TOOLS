using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace JA.IfcViewer
{
    /// <summary>Entry point: JA.VisualizadorIFC.exe [arquivo.ifc ...] — files given on the command line (or by the .ifc
    /// association, "Abrir com") are opened together, as one federated model.</summary>
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            Thread.CurrentThread.CurrentCulture = Ui.Br;
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.DispatcherUnhandledException += (s, e) =>
            {
                Log.Error("não tratado", e.Exception);
                MessageBox.Show("Erro: " + e.Exception.Message + "\n\nDetalhes em " + Log.Path, "JA Visualizador IFC", MessageBoxButton.OK, MessageBoxImage.Error);
                e.Handled = true;
            };
            var w = new UI.MainWindow();
            app.Startup += (s, e) =>
            {
                var files = new System.Collections.Generic.List<string>();
                foreach (string a in args) if (File.Exists(a)) files.Add(Path.GetFullPath(a));
                if (files.Count > 0) w.Dispatcher.BeginInvoke(new Action(() => w.OpenFiles(files)), DispatcherPriority.ApplicationIdle);
            };
            app.Run(w);
        }
    }

    /// <summary>Small log in %AppData%\JA\VisualizadorIFC\registro.txt (errors and unsupported geometry).</summary>
    public static class Log
    {
        public static readonly string Folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JA", "VisualizadorIFC");
        public static string Path => System.IO.Path.Combine(Folder, "registro.txt");
        private static readonly object Lock = new object();

        public static void Info(string msg) => Write("info", msg);
        public static void Error(string msg, Exception ex) => Write("erro", msg + ": " + ex);

        private static void Write(string level, string msg)
        {
            try
            {
                lock (Lock)
                {
                    Directory.CreateDirectory(Folder);
                    var fi = new FileInfo(Path);
                    if (fi.Exists && fi.Length > 2_000_000) fi.Delete();
                    File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {msg}\r\n");
                }
            }
            catch { }
        }
    }
}
