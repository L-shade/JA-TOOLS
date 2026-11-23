using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace RevitDwgUnifier.Infrastructure
{
    /// <summary>
    /// Telemetria do JA (uso das ferramentas, para estudar melhorias). Nada do conteúdo do modelo é gravado: só o nome
    /// das ferramentas, as opções escolhidas nos diálogos, tempos, quantos elementos cada execução criou / mudou / apagou,
    /// as mensagens e erros mostrados, os Ctrl+Z logo depois de uma ferramenta, as sessões (versões do Revit e do JA) e
    /// os projetos como um código (hash do caminho, nunca o nome).
    ///
    /// Uma linha JSON por evento em %LOCALAPPDATA%\RevitDwgUnifier\Telemetria\AAAA-MM_usuario_maquina.jsonl; copiada para a
    /// pasta central (a pasta de atualizações do escritório + \Telemetria, ou a escolhida em Uso e Telemetria) ao abrir e
    /// ao fechar o Revit, em segundo plano. Desligável e anônima por usuário (Uso e Telemetria › Configurar). Nunca lança
    /// exceção nem segura o Revit.
    /// </summary>
    public static class Telemetria
    {
        public const int Versao = 1;
        private static readonly object Sync = new object();
        private static readonly StringBuilder Pendente = new StringBuilder();
        private static Timer _timer;
        private static bool _iniciada;
        private static string _sessao, _usuario, _maquina, _arquivoMes;
        private static DateTime _inicioSessao;
        private static int _comandos;

        // ------------------------------------------------------------------ settings (ToolPrefs keys)

        private const string KAtiva = "__telemetria.ativa", KAnonima = "__telemetria.anonima", KPasta = "__telemetria.pasta";

        public static bool Ativa
        {
            get { try { return RevitDwgUnifier.Tools.Common.ToolPrefs.Get(KAtiva) != "0"; } catch { return true; } }
            set { try { RevitDwgUnifier.Tools.Common.ToolPrefs.Set(KAtiva, value ? "1" : "0"); RevitDwgUnifier.Tools.Common.ToolPrefs.Save(); } catch { } }
        }

        public static bool Anonima
        {
            get { try { return RevitDwgUnifier.Tools.Common.ToolPrefs.Get(KAnonima) == "1"; } catch { return false; } }
            set { try { RevitDwgUnifier.Tools.Common.ToolPrefs.Set(KAnonima, value ? "1" : "0"); RevitDwgUnifier.Tools.Common.ToolPrefs.Save(); } catch { } }
        }

        /// <summary>The folder typed by the user ("" = automatic).</summary>
        public static string PastaEscolhida
        {
            get { try { return RevitDwgUnifier.Tools.Common.ToolPrefs.Get(KPasta) ?? ""; } catch { return ""; } }
            set { try { RevitDwgUnifier.Tools.Common.ToolPrefs.Set(KPasta, (value ?? "").Trim().Trim('"')); RevitDwgUnifier.Tools.Common.ToolPrefs.Save(); } catch { } }
        }

        /// <summary>Where the office collects everybody's files: the chosen folder, else the update folder + \Telemetria; null = none.</summary>
        public static string PastaCentral
        {
            get
            {
                string p = PastaEscolhida;
                if (!string.IsNullOrWhiteSpace(p)) return p;
                try
                {
                    string upd = RevitDwgUnifier.Tools.Common.AvisoVersao.PastaAtualizacoes();
                    if (!string.IsNullOrWhiteSpace(upd)) return Path.Combine(upd, "Telemetria");
                }
                catch { }
                return null;
            }
        }

        public static string PastaLocal => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitDwgUnifier", "Telemetria");

        public static string Usuario => _usuario ?? Identidade().u;

        // ------------------------------------------------------------------ start / end

        /// <summary>Once per Revit session (both tabs call it).</summary>
        public static void Iniciar(UIControlledApplication app)
        {
            if (_iniciada) return;
            _iniciada = true;
            try
            {
                _sessao = Guid.NewGuid().ToString("N").Substring(0, 10);
                _inicioSessao = DateTime.Now;
                var id = Identidade();
                _usuario = id.u; _maquina = id.m;
                _arquivoMes = Path.Combine(PastaLocal, DateTime.Now.ToString("yyyy-MM") + "_" + Seguro(_usuario) + "_" + Seguro(_maquina) + ".jsonl");
                string rv = "", build = "";
                try { rv = app.ControlledApplication.VersionNumber; build = app.ControlledApplication.VersionBuild; } catch { }
                Evento("sessao", new Dictionary<string, object>
                {
                    ["ja"] = VersaoJa, ["rv"] = rv, ["rvb"] = build,
                    ["so"] = Environment.OSVersion.VersionString, ["net"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                    ["cult"] = CultureInfo.CurrentUICulture.Name, ["cpu"] = Environment.ProcessorCount, ["anon"] = Anonima
                });
                try
                {
                    app.ControlledApplication.DocumentChanged += AoMudar;
                    app.ControlledApplication.DocumentOpening += (s, e) => { _abrindo = DateTime.Now; };
                    app.ControlledApplication.DocumentOpened += AoAbrir;
                    app.ControlledApplication.ApplicationInitialized += (s, e) => Evento("revit_pronto", new Dictionary<string, object> { ["seg"] = Math.Round((DateTime.Now - _inicioSessao).TotalSeconds, 1) });
                }
                catch { }
                _timer = new Timer(_ => Gravar(), null, 30000, 30000);
                // the office copy of past files, a little after start (network never holds Revit)
                Task.Run(() => { Thread.Sleep(20000); Sincronizar(); });
            }
            catch (Exception ex) { Logger.Warn("Telemetria não iniciada: " + ex.Message); }
        }

        public static void Encerrar()
        {
            try
            {
                if (!_iniciada) return;
                Evento("fim_sessao", new Dictionary<string, object> { ["min"] = Math.Round((DateTime.Now - _inicioSessao).TotalMinutes, 1), ["comandos"] = _comandos });
                Gravar();
                var t = Task.Run(() => Sincronizar());
                t.Wait(3000);
            }
            catch { }
        }

        public static string VersaoJa
        {
            get
            {
                try
                {
                    var a = Assembly.GetExecutingAssembly();
                    var iv = a.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                    if (!string.IsNullOrEmpty(iv)) return iv.Split('+')[0];
                    return a.GetName().Version.ToString(3);
                }
                catch { return "?"; }
            }
        }

        private static (string u, string m) Identidade()
        {
            string u = Environment.UserName ?? "?", m = Environment.MachineName ?? "?";
            if (Anonima) return ("anon-" + Hash(u + "|" + m), "");
            return (u, m);
        }

        public static string Hash(string s)
        {
            try
            {
                using (var sha = SHA1.Create())
                {
                    byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes((s ?? "").ToLowerInvariant()));
                    return BitConverter.ToString(h, 0, 5).Replace("-", "").ToLowerInvariant();
                }
            }
            catch { return "?"; }
        }

        private static string Seguro(string s)
        {
            if (string.IsNullOrEmpty(s)) return "x";
            var sb = new StringBuilder();
            foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '.' ? c : '_');
            return sb.ToString();
        }

        // ------------------------------------------------------------------ documents

        private static DateTime _abrindo;

        private static void AoAbrir(object sender, DocumentOpenedEventArgs e)
        {
            try
            {
                Document d = e.Document;
                if (d == null || d.IsFamilyDocument) return;
                double mb = 0;
                try { if (!string.IsNullOrEmpty(d.PathName) && File.Exists(d.PathName)) mb = new FileInfo(d.PathName).Length / 1048576.0; } catch { }
                Evento("doc", new Dictionary<string, object>
                {
                    ["proj"] = Projeto(d), ["mb"] = Math.Round(mb, 1), ["central"] = d.IsWorkshared,
                    ["seg"] = _abrindo == default(DateTime) ? 0 : Math.Round((DateTime.Now - _abrindo).TotalSeconds, 1)
                });
            }
            catch { }
        }

        public static string Projeto(Document d)
        {
            try
            {
                string p = d?.PathName;
                if (string.IsNullOrEmpty(p)) p = d?.Title;
                if (d != null && d.IsWorkshared)
                {
                    try { p = ModelPathUtils.ConvertModelPathToUserVisiblePath(d.GetWorksharingCentralModelPath()); } catch { }
                }
                return Hash(p);
            }
            catch { return "?"; }
        }

        // ------------------------------------------------------------------ commands

        /// <summary>One run of a tool, from the button to the end.</summary>
        public sealed class Rodada
        {
            public int Id;
            public string Ferramenta, Aba, Projeto, Vista;
            public DateTime Inicio = DateTime.Now;
            public int Selecao, Criados, Alterados, Apagados, Transacoes, Dialogos, DialogosCancelados, Mensagens;
            public double MsDialogo;
            public bool Automacao;
            public readonly HashSet<string> NomesTransacoes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal bool Encerrada;
        }

        private static Rodada _atual;
        private static readonly List<(Rodada r, DateTime fim)> Recentes = new List<(Rodada, DateTime)>();

        public static Rodada Atual => _atual;

        public static Rodada IniciarComando(string ferramenta, UIDocument uidoc, bool automacao)
        {
            if (!_iniciada || !Ativa) return null;
            try
            {
                var r = new Rodada
                {
                    Id = Interlocked.Increment(ref _comandos), Ferramenta = ferramenta,
                    Aba = Aba(ferramenta), Automacao = automacao
                };
                try
                {
                    Document d = uidoc?.Document;
                    r.Projeto = Projeto(d);
                    r.Vista = d?.ActiveView?.ViewType.ToString();
                    r.Selecao = uidoc?.Selection?.GetElementIds()?.Count ?? 0;
                }
                catch { }
                r.NomesTransacoes.Add(ferramenta);
                _atual = r;
                return r;
            }
            catch { return null; }
        }

        private static string Aba(string ferramenta)
        {
            try
            {
                var info = RevitDwgUnifier.Tools.Common.ToolCatalog.Tools.FirstOrDefault(t => t.Title == ferramenta);
                return info != null ? RevitDwgUnifier.Tools.Common.ToolCatalog.Aba(info.Group) : "";
            }
            catch { return ""; }
        }

        /// <summary>End of a run: Succeeded / Cancelled / Failed, the exception when there was one.</summary>
        public static void FimComando(Rodada r, Result resultado, Exception erro)
        {
            if (r == null || r.Encerrada) return;
            r.Encerrada = true;
            if (_atual == r) _atual = null;
            try
            {
                var ev = new Dictionary<string, object>
                {
                    ["c"] = r.Id, ["ferr"] = r.Ferramenta, ["aba"] = r.Aba, ["res"] = resultado.ToString(),
                    ["ms"] = Math.Round((DateTime.Now - r.Inicio).TotalMilliseconds), ["ms_dlg"] = Math.Round(r.MsDialogo),
                    ["dlg"] = r.Dialogos, ["dlg_canc"] = r.DialogosCancelados, ["msg"] = r.Mensagens,
                    ["sel"] = r.Selecao, ["vista"] = r.Vista ?? "", ["proj"] = r.Projeto ?? "",
                    ["add"] = r.Criados, ["mod"] = r.Alterados, ["del"] = r.Apagados, ["trans"] = r.Transacoes
                };
                if (r.Automacao) ev["auto"] = true;
                if (erro != null)
                {
                    ev["erro"] = new Dictionary<string, object>
                    {
                        ["tipo"] = erro.GetType().Name, ["msg"] = Curto(erro.Message, 300), ["onde"] = Onde(erro)
                    };
                }
                Evento("comando", ev);
                lock (Recentes)
                {
                    Recentes.Add((r, DateTime.Now));
                    if (Recentes.Count > 40) Recentes.RemoveAt(0);
                }
            }
            catch { }
            Gravar();
        }

        /// <summary>The first frame of the JA code in the stack (where to look), without line numbers of Revit's own.</summary>
        private static string Onde(Exception ex)
        {
            try
            {
                string st = ex.StackTrace ?? "";
                foreach (string l in st.Split('\n'))
                {
                    string t = l.Trim();
                    if (t.Contains("RevitDwgUnifier.") || t.Contains("JAEsquadrias."))
                        return Curto(t.StartsWith("at ") ? t.Substring(3) : t.StartsWith("em ") ? t.Substring(3) : t, 240);
                }
                return Curto(st.Split('\n').FirstOrDefault()?.Trim() ?? "", 240);
            }
            catch { return ""; }
        }

        /// <summary>An options dialog closed: the values chosen (lists as "marcados/total", long texts cut) and the time in it.</summary>
        public static void Dialogo(string ferramenta, string chave, IDictionary<string, string> valores, bool ok, double ms)
        {
            if (!_iniciada || !Ativa) return;
            try
            {
                Rodada r = _atual;
                if (r != null) { r.Dialogos++; if (!ok) r.DialogosCancelados++; r.MsDialogo += ms; }
                var ev = new Dictionary<string, object>
                {
                    ["c"] = r?.Id ?? 0, ["ferr"] = ferramenta ?? r?.Ferramenta ?? "", ["dlg"] = chave ?? "", ["ok"] = ok, ["ms"] = Math.Round(ms)
                };
                if (ok && valores != null)
                {
                    var v = new Dictionary<string, object>();
                    foreach (var kv in valores) v[kv.Key] = Curto(kv.Value, 80);
                    ev["val"] = v;
                }
                Evento("dialogo", ev);
            }
            catch { }
        }

        /// <summary>A result / warning window shown by a tool (kind and first line).</summary>
        public static void Mensagem(string ferramenta, string tipo, string texto)
        {
            if (!_iniciada || !Ativa) return;
            try
            {
                Rodada r = _atual;
                if (r != null) r.Mensagens++;
                string primeira = (texto ?? "").Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0) ?? "";
                Evento("mensagem", new Dictionary<string, object>
                {
                    ["c"] = r?.Id ?? 0, ["ferr"] = ferramenta ?? "", ["tipo"] = tipo ?? "", ["txt"] = Curto(Generalizar(primeira), 160)
                });
            }
            catch { }
        }

        /// <summary>Numbers out of a message ("12 peças" and "3 peças" count as the same message).</summary>
        public static string Generalizar(string s) => System.Text.RegularExpressions.Regex.Replace(s ?? "", @"\d+([.,]\d+)?", "#");

        /// <summary>Anything else worth counting (help opened, a feature of a tool used…).</summary>
        public static void Uso(string oque, string detalhe = null)
        {
            if (!_iniciada || !Ativa) return;
            var ev = new Dictionary<string, object> { ["oque"] = oque ?? "" };
            if (!string.IsNullOrEmpty(detalhe)) ev["det"] = Curto(detalhe, 160);
            if (_atual != null) ev["c"] = _atual.Id;
            Evento("uso", ev);
        }

        // ------------------------------------------------------------------ model changes and undo

        private static void AoMudar(object sender, DocumentChangedEventArgs e)
        {
            try
            {
                if (!Ativa) return;
                UndoOperation op = e.Operation;
                if (op == UndoOperation.TransactionCommitted)
                {
                    Rodada r = _atual;
                    if (r == null) return;
                    r.Transacoes++;
                    r.Criados += e.GetAddedElementIds().Count;
                    r.Alterados += e.GetModifiedElementIds().Count;
                    r.Apagados += e.GetDeletedElementIds().Count;
                    foreach (string n in e.GetTransactionNames()) r.NomesTransacoes.Add(n);
                    return;
                }
                if (op == UndoOperation.TransactionUndone)
                {
                    var nomes = e.GetTransactionNames();
                    if (nomes == null || nomes.Count == 0) return;
                    (Rodada r, DateTime fim) achou = default((Rodada, DateTime));
                    lock (Recentes)
                    {
                        for (int i = Recentes.Count - 1; i >= 0; i--)
                            if (nomes.Any(n => Recentes[i].r.NomesTransacoes.Contains(n))) { achou = Recentes[i]; Recentes.RemoveAt(i); break; }
                    }
                    if (achou.r == null) return;
                    Evento("desfeito", new Dictionary<string, object>
                    {
                        ["c"] = achou.r.Id, ["ferr"] = achou.r.Ferramenta, ["seg"] = Math.Round((DateTime.Now - achou.fim).TotalSeconds)
                    });
                }
            }
            catch { }
        }

        // ------------------------------------------------------------------ writing

        public static void Evento(string tipo, Dictionary<string, object> campos)
        {
            if (!_iniciada) return;
            if (!Ativa) return;
            try
            {
                var o = new Dictionary<string, object>
                {
                    ["v"] = Versao, ["t"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
                    ["ev"] = tipo, ["s"] = _sessao, ["u"] = _usuario
                };
                if (tipo == "sessao") o["m"] = _maquina;
                if (campos != null) foreach (var kv in campos) o[kv.Key] = kv.Value;
                string linha = Json(o);
                lock (Sync) Pendente.Append(linha).Append('\n');
            }
            catch { }
        }

        public static void Gravar()
        {
            string texto;
            lock (Sync)
            {
                if (Pendente.Length == 0 || _arquivoMes == null) return;
                texto = Pendente.ToString();
                Pendente.Clear();
            }
            try
            {
                Directory.CreateDirectory(PastaLocal);
                // a new month: a new file
                string mes = Path.Combine(PastaLocal, DateTime.Now.ToString("yyyy-MM") + "_" + Seguro(_usuario) + "_" + Seguro(_maquina) + ".jsonl");
                _arquivoMes = mes;
                lock (Sync) File.AppendAllText(mes, texto, new UTF8Encoding(false));
            }
            catch (Exception ex) { Logger.Warn("Telemetria: " + ex.Message); }
        }

        /// <summary>Copies the local files of the last 13 months to the central folder when newer there. Returns how many.</summary>
        public static int Sincronizar()
        {
            try
            {
                Gravar();
                string central = PastaCentral;
                if (string.IsNullOrWhiteSpace(central) || !Directory.Exists(PastaLocal)) return 0;
                string raiz = Path.GetPathRoot(central);
                if (!string.IsNullOrEmpty(raiz) && !Directory.Exists(raiz)) return 0;   // network drive not there
                Directory.CreateDirectory(central);
                int n = 0;
                string limite = DateTime.Now.AddMonths(-13).ToString("yyyy-MM");
                foreach (string f in Directory.GetFiles(PastaLocal, "*.jsonl"))
                {
                    string nome = Path.GetFileName(f);
                    if (string.CompareOrdinal(nome.Substring(0, Math.Min(7, nome.Length)), limite) < 0) continue;
                    string dest = Path.Combine(central, nome);
                    var fl = new FileInfo(f);
                    var fd = new FileInfo(dest);
                    if (fd.Exists && fd.Length >= fl.Length) continue;
                    lock (Sync) File.Copy(f, dest, true);
                    n++;
                }
                return n;
            }
            catch (Exception ex) { Logger.Warn("Telemetria (pasta central): " + ex.Message); return 0; }
        }

        private static string Curto(string s, int max)
        {
            if (s == null) return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        // ------------------------------------------------------------------ JSON (flat writer)

        public static string Json(object o)
        {
            var sb = new StringBuilder();
            Escrever(sb, o);
            return sb.ToString();
        }

        private static void Escrever(StringBuilder sb, object o)
        {
            switch (o)
            {
                case null: sb.Append("null"); return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case string s: Texto(sb, s); return;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); return;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); return;
                case double d: sb.Append(double.IsNaN(d) || double.IsInfinity(d) ? "0" : d.ToString("R", CultureInfo.InvariantCulture)); return;
                case IDictionary<string, object> dic:
                {
                    sb.Append('{');
                    bool first = true;
                    foreach (var kv in dic)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        Texto(sb, kv.Key);
                        sb.Append(':');
                        Escrever(sb, kv.Value);
                    }
                    sb.Append('}');
                    return;
                }
                case System.Collections.IEnumerable en:
                {
                    sb.Append('[');
                    bool first = true;
                    foreach (object x in en) { if (!first) sb.Append(','); first = false; Escrever(sb, x); }
                    sb.Append(']');
                    return;
                }
            }
            Texto(sb, Convert.ToString(o, CultureInfo.InvariantCulture));
        }

        private static void Texto(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
