using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace RevitDwgUnifier.Tools.Common
{
    /// <summary>
    /// Aviso de versão: the office's update folder (a network or OneDrive folder, written by the
    /// "Instalar tudo" package into %APPDATA%\JA Arquitetos\pasta-atualizacoes.txt or typed in
    /// JA Famílias › Configurações) holds versoes.txt ("JA Famílias=1.15.0"). At startup the file is
    /// read in the background (a slow network never holds Revit) and, when it lists a newer version
    /// than this DLL, one notice per version shows when Revit is idle. Never throws.
    /// </summary>
    internal static class AvisoVersao
    {
        private static Task<string> _tarefa;
        private static string _produto;

        public static string Pasta => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JA Arquitetos");
        public static string ArquivoPasta => Path.Combine(Pasta, "pasta-atualizacoes.txt");

        public static string PastaAtualizacoes()
        {
            try { return File.Exists(ArquivoPasta) ? File.ReadAllLines(ArquivoPasta).Select(l => l.Trim().Trim('"')).FirstOrDefault(l => l.Length > 0) : null; }
            catch { return null; }
        }

        public static void DefinirPasta(string pasta)
        {
            try
            {
                Directory.CreateDirectory(Pasta);
                if (string.IsNullOrWhiteSpace(pasta)) { if (File.Exists(ArquivoPasta)) File.Delete(ArquivoPasta); }
                else File.WriteAllText(ArquivoPasta, pasta.Trim().Trim('"'));
            }
            catch { }
        }

        public static void Iniciar(UIControlledApplication app, string produto)
        {
            try
            {
                string pasta = PastaAtualizacoes();
                if (string.IsNullOrWhiteSpace(pasta)) return;
                _produto = produto;
                Version minha = Assembly.GetExecutingAssembly().GetName().Version;
                _tarefa = Task.Run(() => Verificar(pasta, produto, minha));
                app.Idling += Ocioso;
            }
            catch { }
        }

        private static string Verificar(string pasta, string produto, Version minha)
        {
            try
            {
                string arq = Path.Combine(pasta, "versoes.txt");
                if (!File.Exists(arq)) return null;
                foreach (string linha in File.ReadAllLines(arq))
                {
                    int i = linha.IndexOf('=');
                    if (i <= 0 || !string.Equals(linha.Substring(0, i).Trim(), produto, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!Version.TryParse(linha.Substring(i + 1).Trim(), out Version nova)) return null;
                    if (Normal(nova) <= Normal(minha)) return null;
                    string marca = Path.Combine(Pasta, "avisado-" + produto.Replace(' ', '-') + ".txt");
                    if (File.Exists(marca) && File.ReadAllText(marca).Trim() == nova.ToString()) return null;
                    return nova + "|" + pasta;
                }
            }
            catch { }
            return null;
        }

        private static Version Normal(Version v) => new Version(v.Major, v.Minor, Math.Max(0, v.Build));

        private static void Ocioso(object sender, IdlingEventArgs e)
        {
            if (_tarefa == null || !_tarefa.IsCompleted) return;
            try { if (sender is UIApplication ua) ua.Idling -= Ocioso; } catch { }
            string r = _tarefa.Status == TaskStatus.RanToCompletion ? _tarefa.Result : null;
            _tarefa = null;
            if (r == null) return;
            try
            {
                string[] p = r.Split('|');
                Directory.CreateDirectory(Pasta);
                File.WriteAllText(Path.Combine(Pasta, "avisado-" + _produto.Replace(' ', '-') + ".txt"), p[0]);
                var td = new TaskDialog(_produto)
                {
                    MainInstruction = "Nova versão do " + _produto + ": " + p[0],
                    MainContent = "Está na pasta de atualizações do escritório:\n" + p[1] + "\n\nFeche o Revit e rode “Instalar tudo” (ou o instalador do " + _produto + ") dessa pasta.",
                    CommonButtons = TaskDialogCommonButtons.Close
                };
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Abrir a pasta");
                if (td.Show() == TaskDialogResult.CommandLink1) Process.Start(new ProcessStartInfo("explorer.exe", "\"" + p[1] + "\"") { UseShellExecute = true });
            }
            catch { }
        }
    }
}
