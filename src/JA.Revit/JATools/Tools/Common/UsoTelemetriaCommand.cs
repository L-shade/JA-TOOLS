using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using RevitDwgUnifier.Infrastructure;

namespace RevitDwgUnifier.Tools.Common
{
    /// <summary>
    /// Uso e Telemetria: o relatório de uso do escritório (todas as máquinas, pela pasta central) em Excel — ferramentas,
    /// desistências na janela, Ctrl+Z logo depois, erros, avisos, opções mais escolhidas, sequências de ferramentas,
    /// usuários e versões, por semana — com uma aba de sugestões de melhoria tiradas desses números; e a configuração
    /// da coleta (ligar/desligar, anônimo, pasta central) e o envio imediato.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class UsoTelemetriaCommand : ToolCommand
    {
        public override string Title => "Uso e Telemetria";

        protected override Result Run()
        {
            var d = new OptionsDialog(Title, "UsoTelemetria", "Continuar");
            var fModo = d.Choice("modo", "O que fazer", new[] { "Relatório de uso (Excel)", "Configurar a coleta", "Enviar agora para a pasta central" }, 0);
            var fPeriodo = d.Choice("periodo", "Período", new[] { "Últimos 30 dias", "Últimos 90 dias", "Último ano", "Tudo" }, 1);
            var fCentral = d.Check("central", "Dados da pasta central (todo o escritório)", true);
            var fLocal = d.Check("local", "Dados deste computador", true);
            var fNomes = d.Check("esconder", "Esconder os nomes dos usuários no relatório", false, "Usuário 1, Usuário 2… (para mostrar o relatório à equipe).");
            foreach (var f in new OptionsDialog.Field[] { fPeriodo, fCentral, fLocal, fNomes }) f.VisibleWhen(fModo, 0);
            var fAtiva = d.Check("ativa", "Coletar dados de uso neste computador", Telemetria.Ativa);
            var fAnon = d.Check("anonima", "Anônimo (sem nome de usuário e de computador)", Telemetria.Anonima);
            var fPasta = d.Text("pasta", "Pasta central (vazio = pasta de atualizações\\Telemetria)", Telemetria.PastaEscolhida,
                "Uma pasta da rede ou do OneDrive/SharePoint que todos acessam: cada computador copia os seus arquivos para lá.");
            foreach (var f in new OptionsDialog.Field[] { fAtiva, fAnon, fPasta }) f.VisibleWhen(fModo, 1);
            string central = Telemetria.PastaCentral;
            d.Note("Coletado: ferramentas usadas, opções escolhidas, tempos, quantos elementos cada execução criou/alterou/apagou, " +
                   "avisos e erros mostrados, Ctrl+Z logo depois, versões do Revit e do JA. Nada do conteúdo do projeto (o projeto vira um código). " +
                   "Pasta central agora: " + (string.IsNullOrWhiteSpace(central) ? "nenhuma (só neste computador)" : central));
            if (!d.Show(Owner)) return Result.Cancelled;

            if (fModo.Index == 1)
            {
                Telemetria.Ativa = fAtiva.Value;
                Telemetria.Anonima = fAnon.Value;
                Telemetria.PastaEscolhida = fPasta.Value;
                string c2 = Telemetria.PastaCentral;
                Show((fAtiva.Value ? "Coleta ligada" : "Coleta desligada") + (fAnon.Value ? ", anônima (vale a partir do próximo início do Revit)." : ".") +
                     "\nPasta central: " + (string.IsNullOrWhiteSpace(c2) ? "nenhuma — os dados ficam só neste computador." : c2) +
                     "\nArquivos deste computador: " + Telemetria.PastaLocal, ResultKind.Success, OpenFolderAction(Telemetria.PastaLocal));
                return Result.Succeeded;
            }
            if (fModo.Index == 2)
            {
                if (string.IsNullOrWhiteSpace(central)) { Show("Nenhuma pasta central definida (Configurar a coleta, ou a pasta de atualizações em JA Famílias › Configurações).", ResultKind.Info); return Result.Cancelled; }
                int n = Telemetria.Sincronizar();
                Show(n + " arquivo(s) copiado(s) para " + central + (n == 0 ? "\n(já estava tudo lá, ou a pasta não está acessível agora)." : "."), n > 0 ? ResultKind.Success : ResultKind.Info, OpenFolderAction(central));
                return Result.Succeeded;
            }

            Telemetria.Gravar();
            int dias = new[] { 30, 90, 365, 100000 }[Math.Max(0, fPeriodo.Index)];
            var pastas = new List<string>();
            if (fCentral.Value && !string.IsNullOrWhiteSpace(central) && Directory.Exists(central)) pastas.Add(central);
            if (fLocal.Value && Directory.Exists(Telemetria.PastaLocal)) pastas.Add(Telemetria.PastaLocal);
            if (pastas.Count == 0) { Show("Não há dados ainda: a coleta começa nesta versão (2.5.0) e os arquivos ficam em " + Telemetria.PastaLocal + ".", ResultKind.Info); return Result.Cancelled; }

            var rel = RelatorioUso.Montar(pastas, DateTime.Now.AddDays(-dias), fNomes.Value);
            if (rel.Comandos == 0) { Show("Nenhum uso registrado no período.", ResultKind.Info); return Result.Cancelled; }
            string pasta = RelatorioUso.PastaPadrao;
            Directory.CreateDirectory(pasta);
            string arq = Path.Combine(pasta, "Uso do JA " + DateTime.Now.ToString("yyyy-MM-dd HHmm") + ".xlsx");
            rel.Planilha.Save(arq);
            var sb = new StringBuilder();
            sb.AppendLine($"{rel.Comandos} execuções de {rel.Ferramentas} ferramentas por {rel.Usuarios} usuário(s) em {rel.Sessoes} sessões ({rel.De:dd/MM/yyyy} a {rel.Ate:dd/MM/yyyy}).");
            if (rel.Sugestoes.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Principais sugestões:");
                foreach (string s in rel.Sugestoes.Take(8)) sb.AppendLine("• " + s);
                if (rel.Sugestoes.Count > 8) sb.AppendLine($"… e mais {rel.Sugestoes.Count - 8} na aba Sugestões.");
            }
            Show(sb.ToString(), ResultKind.Success, new ResultAction("Abrir planilha", () => ResultDialog.OpenPath(arq), true), OpenFolderAction(pasta));
            return Result.Succeeded;
        }
    }

    /// <summary>
    /// Exportar Relatório de Uso: one button that saves the usage report where you choose — the Excel workbook (summary,
    /// suggestions and the detail sheets) and, if wanted, every event as CSV (dados brutos for Power BI or a pivot table).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class ExportarRelatorioUsoCommand : ToolCommand
    {
        public override string Title => "Exportar Relatório de Uso";

        protected override Result Run()
        {
            string central = Telemetria.PastaCentral;
            var d = new OptionsDialog(Title, "ExportarRelatorioUso", "Exportar");
            var fPeriodo = d.Choice("periodo", "Período", new[] { "Últimos 30 dias", "Últimos 90 dias", "Último ano", "Tudo" }, 1);
            var fCentral = d.Check("central", "Dados da pasta central (todo o escritório)", true);
            var fLocal = d.Check("local", "Dados deste computador", true);
            var fNomes = d.Check("esconder", "Esconder os nomes dos usuários", false, "Usuário 1, Usuário 2… (para mostrar o relatório à equipe).");
            var fFormato = d.Choice("formato", "Exportar", new[] { "Relatório em Excel", "Relatório em Excel + dados brutos (CSV)", "Só os dados brutos (CSV)" }, 0,
                "Dados brutos: um evento por linha (cada execução, janela, aviso, Ctrl+Z…), para Power BI ou tabela dinâmica.");
            var fAbrir = d.Check("abrir", "Abrir depois de exportar", true);
            d.Note("Pasta central: " + (string.IsNullOrWhiteSpace(central) ? "nenhuma (só os dados deste computador)" : central));
            if (!d.Show(Owner)) return Result.Cancelled;

            Telemetria.Gravar();
            var pastas = new List<string>();
            if (fCentral.Value && !string.IsNullOrWhiteSpace(central) && Directory.Exists(central)) pastas.Add(central);
            if (fLocal.Value && Directory.Exists(Telemetria.PastaLocal)) pastas.Add(Telemetria.PastaLocal);
            if (pastas.Count == 0) { Show("Não há dados de uso ainda (a coleta começa na versão 2.5.0; arquivos em " + Telemetria.PastaLocal + ").", ResultKind.Info); return Result.Cancelled; }
            int dias = new[] { 30, 90, 365, 100000 }[Math.Max(0, fPeriodo.Index)];
            var rel = RelatorioUso.Montar(pastas, DateTime.Now.AddDays(-dias), fNomes.Value);
            if (rel.Comandos == 0) { Show("Nenhum uso registrado no período.", ResultKind.Info); return Result.Cancelled; }

            bool xlsx = fFormato.Index <= 1, csv = fFormato.Index >= 1;
            string pastaPadrao = RelatorioUso.PastaPadrao;
            Directory.CreateDirectory(pastaPadrao);
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = Title,
                InitialDirectory = pastaPadrao,
                FileName = "Uso do JA " + DateTime.Now.ToString("yyyy-MM-dd") + (xlsx ? ".xlsx" : ".csv"),
                Filter = xlsx ? "Planilha do Excel (*.xlsx)|*.xlsx" : "CSV (*.csv)|*.csv",
                OverwritePrompt = true
            };
            if (dlg.ShowDialog() != true) return Result.Cancelled;
            string arq = dlg.FileName, pasta = Path.GetDirectoryName(arq);
            var feitos = new List<string>();
            try
            {
                if (xlsx) { rel.Planilha.Save(arq); feitos.Add(arq); }
                if (csv)
                {
                    string c = xlsx ? Path.Combine(pasta, Path.GetFileNameWithoutExtension(arq) + " - eventos.csv") : arq;
                    XlsxWriter.SaveCsv(rel.Bruto, c);
                    feitos.Add(c);
                }
            }
            catch (IOException ex) { Show("Não foi possível gravar (o arquivo está aberto no Excel?): " + ex.Message, ResultKind.Error); return Result.Failed; }
            if (fAbrir.Value) ResultDialog.OpenPath(feitos[0]);
            Show($"Relatório de uso exportado: {rel.Comandos} execuções de {rel.Ferramentas} ferramentas, {rel.Usuarios} usuário(s), {rel.De:dd/MM/yyyy} a {rel.Ate:dd/MM/yyyy}" +
                 (rel.Sugestoes.Count > 0 ? $", {rel.Sugestoes.Count} sugestão(ões)." : ".") + "\n\n" + string.Join("\n", feitos),
                 ResultKind.Success, new ResultAction("Abrir", () => ResultDialog.OpenPath(feitos[0]), true), OpenFolderAction(pasta));
            return Result.Succeeded;
        }
    }

    /// <summary>Reads the telemetria files and builds the workbook and the suggestions.</summary>
    internal sealed class RelatorioUso
    {
        public XlsxWriter Planilha = new XlsxWriter();
        public int Comandos, Ferramentas, Usuarios, Sessoes;
        public DateTime De = DateTime.MaxValue, Ate = DateTime.MinValue;
        public List<string> Sugestoes = new List<string>();
        /// <summary>Every event as a row (dados brutos, for Power BI / Excel).</summary>
        public XlsxWriter.Table Bruto = new XlsxWriter.Table { Name = "Eventos", Title = "Eventos de uso do JA" };
        private readonly List<XlsxWriter.Table> _tabs = new List<XlsxWriter.Table>();

        private static readonly CultureInfo Br = new CultureInfo("pt-BR");
        public static string PastaPadrao => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "JA", "Relatórios de uso");
        private static string N(double v, string f = "0.0") => v.ToString(f, Br);
        private static string P(double v) => (v * 100).ToString("0", Br) + "%";

        private sealed class Ev
        {
            public Dictionary<string, object> D;
            public DateTime T;
            public string Tipo, Sessao, Usuario;
            public string S(string k) => D.TryGetValue(k, out object o) && o != null ? Convert.ToString(o, CultureInfo.InvariantCulture) : "";
            public double Num(string k) => D.TryGetValue(k, out object o) && o is double d ? d : 0;
            public bool B(string k) => D.TryGetValue(k, out object o) && o is bool b && b;
        }

        public static RelatorioUso Montar(List<string> pastas, DateTime desde, bool esconder)
        {
            var r = new RelatorioUso();
            // the newest copy of each file (central and local hold the same names)
            var arquivos = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (string p in pastas)
                foreach (string f in Directory.GetFiles(p, "*.jsonl"))
                {
                    var fi = new FileInfo(f);
                    if (!arquivos.TryGetValue(fi.Name, out FileInfo old) || fi.Length > old.Length) arquivos[fi.Name] = fi;
                }
            var evs = new List<Ev>();
            foreach (FileInfo fi in arquivos.Values)
            {
                IEnumerable<string> linhas;
                try { linhas = File.ReadAllLines(fi.FullName, Encoding.UTF8); } catch { continue; }
                foreach (string l in linhas)
                {
                    if (string.IsNullOrWhiteSpace(l)) continue;
                    if (!(MiniJson.Parse(l) is Dictionary<string, object> o)) continue;
                    var e = new Ev { D = o };
                    e.Tipo = e.S("ev"); e.Sessao = e.S("s"); e.Usuario = e.S("u");
                    if (!DateTime.TryParse(e.S("t"), CultureInfo.InvariantCulture, DateTimeStyles.None, out e.T)) continue;
                    if (e.T < desde) continue;
                    evs.Add(e);
                }
            }
            evs.Sort((a, b) => a.T.CompareTo(b.T));
            r.Construir(evs, esconder);
            return r;
        }

        private void Construir(List<Ev> evs, bool esconder)
        {
            var sessoes = evs.Where(e => e.Tipo == "sessao").GroupBy(e => e.Sessao).ToDictionary(g => g.Key, g => g.First());
            var cmds = evs.Where(e => e.Tipo == "comando").ToList();
            var dlgs = evs.Where(e => e.Tipo == "dialogo").ToList();
            var msgs = evs.Where(e => e.Tipo == "mensagem").ToList();
            var desfeitos = new Dictionary<string, double>();
            foreach (Ev e in evs.Where(x => x.Tipo == "desfeito")) desfeitos[e.Sessao + "#" + e.S("c")] = e.Num("seg");
            Comandos = cmds.Count;
            if (Comandos == 0) return;
            De = evs.First().T; Ate = evs.Last().T;
            var usuarios = evs.Select(e => e.Usuario).Where(u => u.Length > 0).Distinct().OrderBy(u => u).ToList();
            var apelido = new Dictionary<string, string>();
            for (int i = 0; i < usuarios.Count; i++) apelido[usuarios[i]] = esconder ? "Usuário " + (i + 1) : usuarios[i];
            string U(string u) => apelido.TryGetValue(u, out string a) ? a : u;
            Usuarios = usuarios.Count;
            Sessoes = evs.Select(e => e.Sessao).Distinct().Count();
            Ferramentas = cmds.Select(c => c.S("ferr")).Distinct().Count();
            bool Desfeito(Ev c, double seg = 120) => desfeitos.TryGetValue(c.Sessao + "#" + c.S("c"), out double s) && s <= seg;
            bool CancelouNoDialogo(Ev c) => c.S("res") == "Cancelled" && c.Num("dlg_canc") > 0;

            // ------------------------------------------------------------ tools
            var tFer = new XlsxWriter.Table { Name = "Ferramentas", Title = "Uso por ferramenta" };
            tFer.Rows.Add(new[] { "Ferramenta", "Aba", "Execuções", "Usuários", "Concluídas", "Desistiu na janela", "Cancelou depois", "Erros", "Desfeitas (Ctrl+Z ≤ 2 min)",
                                  "Tempo médio sem janela (s)", "Mediana (s)", "Tempo médio na janela (s)", "Elementos criados (média)", "Alterados (média)", "Apagados (média)", "Último uso" });
            var porFer = cmds.GroupBy(c => c.S("ferr")).OrderByDescending(g => g.Count()).ToList();
            foreach (var g in porFer)
            {
                var l = g.ToList();
                int n = l.Count;
                double ok = l.Count(c => c.S("res") == "Succeeded"), desist = l.Count(CancelouNoDialogo), canc = l.Count(c => c.S("res") == "Cancelled") - desist,
                       err = l.Count(c => c.S("res") == "Failed"), desf = l.Count(c => Desfeito(c));
                var tempos = l.Where(c => c.S("res") == "Succeeded").Select(c => Math.Max(0, c.Num("ms") - c.Num("ms_dlg")) / 1000).OrderBy(x => x).ToList();
                double med = tempos.Count > 0 ? tempos.Average() : 0, mediana = tempos.Count > 0 ? tempos[tempos.Count / 2] : 0;
                double dlg = l.Where(c => c.Num("dlg") > 0).Select(c => c.Num("ms_dlg") / 1000).DefaultIfEmpty(0).Average();
                tFer.Rows.Add(new[]
                {
                    g.Key, l[0].S("aba"), n.ToString(), l.Select(c => c.Usuario).Distinct().Count().ToString(), P(ok / n), P(desist / n), P(canc / n), P(err / n), P(desf / n),
                    N(med), N(mediana), N(dlg), N(l.Average(c => c.Num("add"))), N(l.Average(c => c.Num("mod"))), N(l.Average(c => c.Num("del"))), l.Max(c => c.T).ToString("dd/MM/yyyy HH:mm")
                });
                // suggestions
                if (n >= 5 && desist / n >= 0.3) Sugestoes.Add($"{g.Key}: {P(desist / n)} das vezes o usuário desiste na janela de opções — rever textos, opções e valores-padrão.");
                if (n >= 5 && desf / n >= 0.2) Sugestoes.Add($"{g.Key}: {P(desf / n)} das execuções são desfeitas em até 2 min — o resultado não é o esperado; ver os casos com os usuários.");
                if (err >= 3 || (n >= 10 && err / n >= 0.05))
                {
                    var top = l.Where(c => c.S("res") == "Failed").Select(c => Erro(c)).GroupBy(x => x).OrderByDescending(x => x.Count()).First();
                    Sugestoes.Add($"{g.Key}: {err} erro(s) ({P(err / n)}). Mais comum ({top.Count()}×): {top.Key}");
                }
                if (tempos.Count >= 3 && mediana > 60) Sugestoes.Add($"{g.Key}: mediana de {N(mediana, "0")} s por execução (sem contar a janela) — candidata a otimização ou a rodar em segundo plano.");
                if (n >= 5 && dlg > 45) Sugestoes.Add($"{g.Key}: {N(dlg, "0")} s em média na janela de opções — janela difícil? Simplificar ou dividir.");
            }
            _tabs.Add(tFer);

            // ------------------------------------------------------------ options
            var tOp = new XlsxWriter.Table { Name = "Opções", Title = "Opções escolhidas nas janelas (quando confirmadas)" };
            tOp.Rows.Add(new[] { "Ferramenta", "Opção", "Valor", "Vezes", "Parcela" });
            foreach (var g in dlgs.Where(x => x.B("ok") && x.D.ContainsKey("val")).GroupBy(x => x.S("ferr")).OrderBy(x => x.Key))
            {
                var porCampo = new Dictionary<string, Dictionary<string, int>>();
                int total = 0;
                foreach (Ev e in g)
                {
                    if (!(e.D["val"] is Dictionary<string, object> v)) continue;
                    total++;
                    foreach (var kv in v)
                    {
                        if (!porCampo.TryGetValue(kv.Key, out var cont)) porCampo[kv.Key] = cont = new Dictionary<string, int>();
                        string val = Convert.ToString(kv.Value, CultureInfo.InvariantCulture);
                        cont[val] = cont.TryGetValue(val, out int k) ? k + 1 : 1;
                    }
                }
                foreach (var campo in porCampo.OrderBy(x => x.Key))
                {
                    int n = campo.Value.Values.Sum();
                    foreach (var val in campo.Value.OrderByDescending(x => x.Value).Take(8))
                        tOp.Rows.Add(new[] { g.Key, campo.Key, Legivel(val.Key), val.Value.ToString(), P((double)val.Value / n) });
                    var mais = campo.Value.OrderByDescending(x => x.Value).First();
                    if (n >= 8 && (double)mais.Value / n >= 0.85 && campo.Value.Count > 1 && !campo.Key.StartsWith("__"))
                        Sugestoes.Add($"{g.Key} › {campo.Key}: quase sempre “{Legivel(mais.Key)}” ({P((double)mais.Value / n)} de {n}) — confirmar que é o padrão, ou tirar a opção da janela.");
                }
            }
            _tabs.Add(tOp);

            // ------------------------------------------------------------ errors and messages
            var tErr = new XlsxWriter.Table { Name = "Erros", Title = "Erros (exceções) por ferramenta" };
            tErr.Rows.Add(new[] { "Ferramenta", "Erro", "Onde (código)", "Vezes", "Usuários", "Versões do JA", "Último" });
            foreach (var g in cmds.Where(c => c.S("res") == "Failed").GroupBy(c => c.S("ferr") + "|" + Erro(c)).OrderByDescending(x => x.Count()))
            {
                Ev f0 = g.First();
                var er = f0.D.TryGetValue("erro", out object eo) ? eo as Dictionary<string, object> : null;
                string onde = er != null && er.TryGetValue("onde", out object w) ? Convert.ToString(w) : "";
                tErr.Rows.Add(new[] { f0.S("ferr"), Erro(f0), onde, g.Count().ToString(), g.Select(c => c.Usuario).Distinct().Count().ToString(),
                                      string.Join(", ", g.Select(c => Versao(sessoes, c.Sessao)).Distinct()), g.Max(c => c.T).ToString("dd/MM/yyyy HH:mm") });
            }
            _tabs.Add(tErr);

            var tMsg = new XlsxWriter.Table { Name = "Avisos", Title = "Mensagens mostradas (números trocados por #)" };
            tMsg.Rows.Add(new[] { "Ferramenta", "Tipo", "Mensagem (1ª linha)", "Vezes", "Usuários" });
            foreach (var g in msgs.GroupBy(m => m.S("ferr") + "|" + m.S("tipo") + "|" + m.S("txt")).OrderByDescending(x => x.Count()).Take(400))
            {
                Ev m0 = g.First();
                tMsg.Rows.Add(new[] { m0.S("ferr"), TipoMsg(m0.S("tipo")), m0.S("txt"), g.Count().ToString(), g.Select(x => x.Usuario).Distinct().Count().ToString() });
                string tipo = m0.S("tipo");
                if ((tipo == "Warning" || tipo == "Error") && g.Count() >= 10)
                    Sugestoes.Add($"{m0.S("ferr")}: o aviso “{m0.S("txt")}” apareceu {g.Count()} vezes — dá para evitar a situação antes (validar na janela, corrigir sozinho)?");
            }
            _tabs.Add(tMsg);

            // ------------------------------------------------------------ users and versions
            var tUs = new XlsxWriter.Table { Name = "Usuários", Title = "Usuários" };
            tUs.Rows.Add(new[] { "Usuário", "Computadores", "Sessões", "Dias com uso", "Execuções", "Ferramentas diferentes", "Mais usadas", "Versão do JA (última)", "Revit", "Último uso" });
            string maiorVersao = sessoes.Values.Select(s => s.S("ja")).Where(v => v.Length > 0).OrderByDescending(v => V(v)).FirstOrDefault() ?? "";
            foreach (string u in usuarios)
            {
                var cu = cmds.Where(c => c.Usuario == u).ToList();
                var su = sessoes.Values.Where(s => s.Usuario == u).OrderBy(s => s.T).ToList();
                string ultimaJa = su.Count > 0 ? su.Last().S("ja") : "";
                tUs.Rows.Add(new[]
                {
                    U(u), esconder ? su.Select(s => s.S("m")).Distinct().Count().ToString() : string.Join(", ", su.Select(s => s.S("m")).Where(x => x.Length > 0).Distinct()),
                    su.Count.ToString(), evs.Where(e => e.Usuario == u).Select(e => e.T.Date).Distinct().Count().ToString(), cu.Count.ToString(),
                    cu.Select(c => c.S("ferr")).Distinct().Count().ToString(),
                    string.Join(", ", cu.GroupBy(c => c.S("ferr")).OrderByDescending(g => g.Count()).Take(5).Select(g => g.Key + " " + g.Count())),
                    ultimaJa, string.Join(", ", su.Select(s => s.S("rv")).Distinct()), evs.Where(e => e.Usuario == u).Max(e => e.T).ToString("dd/MM/yyyy HH:mm")
                });
                if (ultimaJa.Length > 0 && maiorVersao.Length > 0 && V(ultimaJa) < V(maiorVersao))
                    Sugestoes.Add($"{U(u)} ainda usa o JA {ultimaJa} (há a {maiorVersao}) — atualizar.");
            }
            _tabs.Add(tUs);

            // ------------------------------------------------------------ adoption
            if (usuarios.Count >= 3)
                foreach (var g in porFer)
                {
                    int nu = g.Select(c => c.Usuario).Distinct().Count();
                    if (nu == 1 && g.Count() >= 5) Sugestoes.Add($"{g.Key}: só {U(g.First().Usuario)} usa ({g.Count()} vezes) — mostrar à equipe?");
                }
            var usadas = new HashSet<string>(cmds.Select(c => c.S("ferr")));
            var nunca = ToolCatalog.Tools.Where(t => !usadas.Contains(t.Title)).Select(t => t.Title).OrderBy(x => x).ToList();
            var tNunca = new XlsxWriter.Table { Name = "Sem uso", Title = "Ferramentas sem nenhum uso no período" };
            tNunca.Rows.Add(new[] { "Ferramenta", "Aba", "Grupo" });
            foreach (var t in ToolCatalog.Tools.Where(t => !usadas.Contains(t.Title)).OrderBy(t => t.Group).ThenBy(t => t.Title))
                tNunca.Rows.Add(new[] { t.Title, ToolCatalog.Aba(t.Group), t.Group });
            _tabs.Add(tNunca);
            if (Comandos >= 50 && nunca.Count > 0 && (Ate - De).TotalDays >= 20)
                Sugestoes.Add($"{nunca.Count} ferramenta(s) sem uso no período (aba Sem uso) — treinar a equipe, juntar com outras ou tirar da faixa.");

            // ------------------------------------------------------------ sequences (what comes after what)
            var pares = new Dictionary<string, int>();
            foreach (var s in cmds.Where(c => c.S("res") == "Succeeded").GroupBy(c => c.Sessao))
            {
                var l = s.OrderBy(c => c.T).ToList();
                for (int i = 0; i + 1 < l.Count; i++)
                {
                    string a = l[i].S("ferr"), b = l[i + 1].S("ferr");
                    if (a == b || (l[i + 1].T - l[i].T).TotalMinutes > 5) continue;
                    string k = a + " → " + b;
                    pares[k] = pares.TryGetValue(k, out int n) ? n + 1 : 1;
                }
            }
            var tSeq = new XlsxWriter.Table { Name = "Sequências", Title = "Ferramenta seguida de outra em até 5 min (mesma sessão)" };
            tSeq.Rows.Add(new[] { "Sequência", "Vezes" });
            foreach (var kv in pares.OrderByDescending(x => x.Value).Take(200)) tSeq.Rows.Add(new[] { kv.Key, kv.Value.ToString() });
            _tabs.Add(tSeq);
            foreach (var kv in pares.Where(x => x.Value >= 10).OrderByDescending(x => x.Value).Take(5))
                Sugestoes.Add($"{kv.Key}: {kv.Value} vezes seguidas — candidata a um pacote da Central JA ou a uma opção que faça as duas.");

            // ------------------------------------------------------------ weeks
            var tSem = new XlsxWriter.Table { Name = "Por semana", Title = "Uso por semana" };
            tSem.Rows.Add(new[] { "Semana (segunda-feira)", "Usuários ativos", "Execuções", "Concluídas", "Erros", "Horas de Revit (sessões)" });
            var fins = evs.Where(e => e.Tipo == "fim_sessao").GroupBy(e => e.Sessao).ToDictionary(g => g.Key, g => g.Last().Num("min"));
            foreach (var g in cmds.GroupBy(c => Segunda(c.T)).OrderBy(x => x.Key))
            {
                var ss = new HashSet<string>(g.Select(c => c.Sessao));
                double horas = ss.Sum(s => fins.TryGetValue(s, out double m) ? m : 0) / 60;
                tSem.Rows.Add(new[] { g.Key.ToString("dd/MM/yyyy"), g.Select(c => c.Usuario).Distinct().Count().ToString(), g.Count().ToString(),
                                      g.Count(c => c.S("res") == "Succeeded").ToString(), g.Count(c => c.S("res") == "Failed").ToString(), N(horas) });
            }
            _tabs.Add(tSem);

            // ------------------------------------------------------------ projects and views
            var tVis = new XlsxWriter.Table { Name = "Contexto", Title = "Onde as ferramentas rodam (tipo de vista ativa, com ou sem seleção)" };
            tVis.Rows.Add(new[] { "Ferramenta", "Tipo de vista", "Com seleção", "Vezes" });
            foreach (var g in cmds.GroupBy(c => c.S("ferr") + "|" + c.S("vista") + "|" + (c.Num("sel") > 0 ? "sim" : "não")).OrderBy(x => x.Key))
            {
                string[] p = g.Key.Split('|');
                tVis.Rows.Add(new[] { p[0], p[1], p[2], g.Count().ToString() });
            }
            _tabs.Add(tVis);

            // ------------------------------------------------------------ summary and suggestions first
            var tRes = new XlsxWriter.Table { Name = "Resumo", Title = "Uso do JA para Revit — " + De.ToString("dd/MM/yyyy") + " a " + Ate.ToString("dd/MM/yyyy") };
            tRes.Rows.Add(new[] { "Indicador", "Valor" });
            tRes.Rows.Add(new[] { "Usuários", Usuarios.ToString() });
            tRes.Rows.Add(new[] { "Sessões do Revit", Sessoes.ToString() });
            tRes.Rows.Add(new[] { "Horas de Revit com o JA", N(fins.Values.Sum() / 60) });
            tRes.Rows.Add(new[] { "Execuções de ferramentas", Comandos.ToString() });
            tRes.Rows.Add(new[] { "Ferramentas usadas", Ferramentas + " de " + ToolCatalog.Tools.Count });
            tRes.Rows.Add(new[] { "Concluídas", P((double)cmds.Count(c => c.S("res") == "Succeeded") / Comandos) });
            tRes.Rows.Add(new[] { "Desistências na janela", P((double)cmds.Count(CancelouNoDialogo) / Comandos) });
            tRes.Rows.Add(new[] { "Erros", P((double)cmds.Count(c => c.S("res") == "Failed") / Comandos) });
            tRes.Rows.Add(new[] { "Desfeitas em até 2 min", P((double)cmds.Count(c => Desfeito(c)) / Comandos) });
            tRes.Rows.Add(new[] { "Elementos criados pelas ferramentas", N(cmds.Sum(c => c.Num("add")), "0") });
            tRes.Rows.Add(new[] { "Projetos diferentes", cmds.Select(c => c.S("proj")).Where(p => p.Length > 0).Distinct().Count().ToString() });
            tRes.Rows.Add(new[] { "Versões do JA em uso", string.Join(", ", sessoes.Values.Select(s => s.S("ja")).Where(v => v.Length > 0).Distinct().OrderByDescending(V)) });
            tRes.Rows.Add(new[] { "Versões do Revit", string.Join(", ", sessoes.Values.Select(s => s.S("rv")).Where(v => v.Length > 0).Distinct().OrderBy(v => v)) });
            tRes.Rows.Add(new[] { "", "" });
            tRes.Rows.Add(new[] { "Mais usadas", "" });
            foreach (var g in porFer.Take(15)) tRes.Rows.Add(new[] { g.Key, g.Count().ToString() });

            var tSug = new XlsxWriter.Table { Name = "Sugestões", Title = "Sugestões de melhoria tiradas do uso" };
            tSug.Rows.Add(new[] { "#", "Sugestão" });
            for (int i = 0; i < Sugestoes.Count; i++) tSug.Rows.Add(new[] { (i + 1).ToString(), Sugestoes[i] });
            if (Sugestoes.Count == 0) tSug.Rows.Add(new[] { "", "Nada fora do normal no período." });

            // raw events
            Bruto.Rows.Add(new[] { "Data", "Hora", "Usuário", "Sessão", "Evento", "Ferramenta", "Aba", "Resultado", "Tempo total (s)", "Tempo na janela (s)",
                                   "Criados", "Alterados", "Apagados", "Tipo de vista", "Seleção", "Projeto", "Tipo / janela", "Mensagem / erro", "Onde (código)",
                                   "Desfeita em (s)", "Versão do JA", "Revit" });
            foreach (Ev e in evs)
            {
                if (e.Tipo == "dialogo" && !e.B("ok") && e.S("ferr").Length == 0) continue;
                string ferr = e.S("ferr"), res = e.S("res"), tipo = "", texto = "", onde = "", desf = "";
                switch (e.Tipo)
                {
                    case "comando":
                        if (e.D.TryGetValue("erro", out object eo) && eo is Dictionary<string, object> er)
                        {
                            texto = Erro(e);
                            onde = er.TryGetValue("onde", out object w) ? Convert.ToString(w) : "";
                        }
                        if (desfeitos.TryGetValue(e.Sessao + "#" + e.S("c"), out double ds)) desf = N(ds, "0");
                        break;
                    case "dialogo":
                        tipo = e.S("dlg");
                        res = e.B("ok") ? "OK" : "Cancelou";
                        if (e.D.TryGetValue("val", out object vo) && vo is Dictionary<string, object> vals)
                            texto = string.Join("; ", vals.Select(kv => kv.Key + "=" + Legivel(Convert.ToString(kv.Value, CultureInfo.InvariantCulture))));
                        break;
                    case "mensagem": tipo = TipoMsg(e.S("tipo")); texto = e.S("txt"); break;
                    case "uso": tipo = e.S("oque"); texto = e.S("det"); break;
                    case "desfeito": desf = N(e.Num("seg"), "0"); break;
                    case "doc": tipo = (e.B("central") ? "central" : "local") + ", " + N(e.Num("mb")) + " MB"; texto = "abriu em " + N(e.Num("seg")) + " s"; break;
                }
                bool cmd = e.Tipo == "comando";
                Ev se = sessoes.TryGetValue(e.Sessao, out Ev sx) ? sx : null;
                Bruto.Rows.Add(new[]
                {
                    e.T.ToString("dd/MM/yyyy"), e.T.ToString("HH:mm:ss"), U(e.Usuario), e.Sessao, e.Tipo, ferr, e.S("aba"), res,
                    cmd || e.Tipo == "dialogo" ? N(e.Num("ms") / 1000, "0.0") : "", cmd ? N(e.Num("ms_dlg") / 1000, "0.0") : "",
                    cmd ? e.S("add") : "", cmd ? e.S("mod") : "", cmd ? e.S("del") : "", e.S("vista"), cmd ? e.S("sel") : "", e.S("proj"),
                    tipo, texto, onde, desf, se?.S("ja") ?? "", se?.S("rv") ?? ""
                });
            }

            Planilha = new XlsxWriter();
            Planilha.Add(tRes);
            Planilha.Add(tSug);
            foreach (var t in _tabs) Planilha.Add(t);
        }

        private static string Erro(Ev c)
        {
            if (!(c.D.TryGetValue("erro", out object o) && o is Dictionary<string, object> e)) return "(sem detalhe)";
            string tipo = e.TryGetValue("tipo", out object t) ? Convert.ToString(t) : "", msg = e.TryGetValue("msg", out object m) ? Convert.ToString(m) : "";
            return tipo + ": " + Telemetria.Generalizar(msg);
        }

        private static string Versao(Dictionary<string, Ev> sessoes, string s) => sessoes.TryGetValue(s, out Ev e) ? e.S("ja") : "?";

        private static Version V(string s) { Version.TryParse((s ?? "").Split('-')[0], out Version v); return v ?? new Version(0, 0); }

        private static DateTime Segunda(DateTime t) { int d = ((int)t.DayOfWeek + 6) % 7; return t.Date.AddDays(-d); }

        private static string TipoMsg(string t)
        {
            switch (t) { case "Success": return "Sucesso"; case "Warning": return "Aviso"; case "Error": return "Erro"; case "Info": return "Informação"; default: return "Automático"; }
        }

        /// <summary>Stored option values as people read them: booleans, list separators.</summary>
        private static string Legivel(string v)
        {
            if (v == "1") return "Sim (1)";
            if (v == "0") return "Não (0)";
            return v.Replace("\u001f", "; ");
        }
    }

    /// <summary>Tiny JSON reader for the telemetria lines: objects, arrays, strings, numbers (double), booleans, null.</summary>
    internal static class MiniJson
    {
        public static object Parse(string s)
        {
            try { int i = 0; object o = Valor(s, ref i); return o; }
            catch { return null; }
        }

        private static void Branco(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        private static object Valor(string s, ref int i)
        {
            Branco(s, ref i);
            char c = s[i];
            if (c == '{')
            {
                var d = new Dictionary<string, object>();
                i++;
                Branco(s, ref i);
                if (s[i] == '}') { i++; return d; }
                while (true)
                {
                    Branco(s, ref i);
                    string k = Texto(s, ref i);
                    Branco(s, ref i);
                    i++;   // ':'
                    d[k] = Valor(s, ref i);
                    Branco(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    i++;   // '}'
                    return d;
                }
            }
            if (c == '[')
            {
                var l = new List<object>();
                i++;
                Branco(s, ref i);
                if (s[i] == ']') { i++; return l; }
                while (true)
                {
                    l.Add(Valor(s, ref i));
                    Branco(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    i++;
                    return l;
                }
            }
            if (c == '"') return Texto(s, ref i);
            if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (s.Length - i >= 5 && string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            int ini = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            return double.Parse(s.Substring(ini, i - ini), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private static string Texto(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++;   // opening quote
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') break;
                if (c != '\\') { sb.Append(c); continue; }
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u': sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16)); i += 4; break;
                    default: sb.Append(e); break;
                }
            }
            return sb.ToString();
        }
    }
}
