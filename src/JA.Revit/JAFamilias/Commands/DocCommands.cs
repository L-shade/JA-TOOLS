using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using JAEsquadrias.Common;
using JAEsquadrias.Core;
using JAEsquadrias.Engine;
using JAEsquadrias.Infrastructure;
using Settings = JAEsquadrias.Core.Settings;

namespace JAEsquadrias.Commands
{
    [Transaction(TransactionMode.Manual)]
    public sealed class QuadroEsquadriasCommand : ToolCommand
    {
        public override string Title => "Quadro de Esquadrias";

        protected override Result Run()
        {
            if (SharedParams.ProjectId(Doc, SharedParams.Abertura) == ElementId.InvalidElementId)
            {
                Show("Ainda não há esquadrias do JA Famílias neste projeto.\nGere pelo menos uma (Nova Janela ou Nova Porta) e rode o Quadro de novo.", ResultKind.Info);
                return Result.Cancelled;
            }
            var dlg = new OptionsDialog(Title, "Quadro");
            var what = dlg.Choice("what", "Criar quadro de", new[] { "Janelas e portas", "Só janelas", "Só portas" }, 0);
            var janelaName = dlg.Text("nomeJ", "Nome do quadro de janelas", "JA - Quadro de Janelas");
            var portaName = dlg.Text("nomeP", "Nome do quadro de portas", "JA - Quadro de Portas");
            var stones = dlg.Check("pedras", "Também o quadro de soleiras, filetes e peitoris (por código)", true);
            var pedraName = dlg.Text("nomeS", "Nome do quadro de soleiras", "JA - Quadro de Soleiras e Peitoris");
            var elev = dlg.Check("elevacao", "Elevação de cada tipo no quadro (imagem do tipo)", true,
                "Desenha a elevação de cada esquadria (como no configurador) e coloca como Imagem do tipo; a coluna “Elevação” mostra a figura quando o quadro está na prancha.");
            var recreate = dlg.Check("recreate", "Recriar o quadro se já existir (atualiza as colunas)", true);
            if (!dlg.Show(Owner)) throw new ToolCancelledException();
            int imagens = 0;

            var warnings = new List<string>();
            var created = new List<ViewSchedule>();
            using (var t = new Transaction(Doc, "JA Famílias – Quadro de Esquadrias"))
            {
                t.Start();
                if (elev.Value)
                {
                    var tipos = ProjectEsquadrias.All(Doc).Where(x => x.Config != null && (what.Index == 0 || x.Config.IsPorta == (what.Index == 2))).ToList();
                    imagens = ImagemDoTipo.Aplicar(Doc, tipos, Path.Combine(Settings.Current.PastaRelatorios, "Elevações"), warnings);
                }
                if (what.Index != 2) created.Add(ScheduleBuilder.Create(Doc, BuiltInCategory.OST_Windows, janelaName.Value.Trim(), recreate.Value, warnings, elev.Value));
                if (what.Index != 1) created.Add(ScheduleBuilder.Create(Doc, BuiltInCategory.OST_Doors, portaName.Value.Trim(), recreate.Value, warnings, elev.Value));
                if (stones.Value && SharedParams.ProjectId(Doc, SharedParams.SoleiraCodigo) != ElementId.InvalidElementId)
                    created.Add(ScheduleBuilder.CreateStones(Doc, pedraName.Value.Trim(), recreate.Value, warnings));
                t.Commit();
            }
            created = created.Where(v => v != null && v.IsValidObject).ToList();
            if (created.Count > 0) TryOpen(created[0]);

            var sb = new StringBuilder();
            sb.AppendLine(created.Count + " quadro(s) criado(s).");
            foreach (var v in created) sb.AppendLine("• " + v.Name);
            sb.AppendLine();
            sb.AppendLine("Arraste o quadro do Navegador de Projeto para a prancha.");
            if (imagens > 0) sb.AppendLine(imagens + " elevação(ões) colocadas como Imagem do tipo: elas aparecem na coluna “Elevação” na prancha (na vista do quadro o Revit mostra só o nome).");
            if (warnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Avisos:");
                foreach (string w in warnings.Distinct()) sb.AppendLine("• " + w);
            }
            Show(sb.ToString());
            return Result.Succeeded;
        }
    }

    /// <summary>
    /// Orçamento: a first estimate from the JA families placed in the project — windows and doors
    /// by opening area, stone by area (chapins by their length), sinks by unit — with the prices
    /// typed in the dialog (remembered), as a CSV that opens in Excel.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class OrcamentoCommand : ToolCommand
    {
        public override string Title => "Orçamento";

        private static string Chave(string s) => new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        /// <summary>The stone of a piece (for the price per material).</summary>
        private static string Pedra(IPecaConfig c) =>
            c.Materiais()?.FirstOrDefault(m => m.Key == "pedra")?.Nome?.Trim() is string n && n.Length > 0 ? n : c.MaterialPrincipal();

        protected override Result Run()
        {
            var esquadrias = ProjectEsquadrias.All(Doc).Where(x => x.Instancias > 0 && x.Config != null).ToList();
            var pecas = ProjectPecas.All(Doc).Where(x => x.Instancias > 0 && x.Config != null).ToList();
            // paginações drawn with the JA tool (their area to buy is kept on the group)
            var paginacoes = new FilteredElementCollector(Doc).OfClass(typeof(Group)).Cast<Group>()
                .Select(g => JaDados.Ler(g)).Where(d => d != null && d.Tipo == "paginacao" && d.CompraM2 > 0).ToList();
            bool temGuardaCorpos = new FilteredElementCollector(Doc).OfClass(typeof(Autodesk.Revit.DB.Architecture.Railing)).Any(r => JaDados.Ler(r)?.Tipo == "guardacorpo");
            if (esquadrias.Count == 0 && pecas.Count == 0 && paginacoes.Count == 0 && !temGuardaCorpos)
            {
                Show("Nenhuma família do JA Famílias nem paginação neste projeto.", ResultKind.Info);
                return Result.Cancelled;
            }
            string RotuloPag(DadosJa d)
            {
                string mat = "";
                try { mat = Json.Deserialize<ConfigPaginacao>(d.Config)?.Material ?? ""; } catch { }
                return ((mat.Length > 0 ? mat : "Revestimento") + " " + d.Peca).Trim();
            }
            bool EPedra(IPecaConfig c) => c.Tipo == TipoPeca.Bancada || c.Tipo == TipoPeca.Nicho || c.Tipo == TipoPeca.Chapim || c.Tipo == TipoPeca.Pedra;
            var materiais = pecas.Where(u => EPedra(u.Config)).Select(u => Pedra(u.Config)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
            var revest = paginacoes.Select(RotuloPag).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();

            var dlg = new OptionsDialog(Title, "Orcamento");
            dlg.Section("Esquadrias (R$)");
            var pJanela = dlg.Number("janela", "Janela, por m² de vão", 900, 0, 1000000);
            var pPorta = dlg.Number("porta", "Porta, por m² de vão", 1100, 0, 1000000);
            var pPedra = new Dictionary<string, OptionsDialog.NumberField>(StringComparer.OrdinalIgnoreCase);
            if (materiais.Count > 0)
            {
                dlg.Section("Pedras, por m² (R$)");
                foreach (string m in materiais)
                    pPedra[m] = dlg.Number("pedra." + Chave(m), m, 650, 0, 1000000, "Área de pedra das famílias desta pedra (tampo, saia, frontão, tento, pés, soleiras, rodapés, degraus).");
            }
            dlg.Section("Cubas, ralos e vidros (R$)");
            var pCuba = dlg.Number("cuba", "Cuba de embutir, sobrepor ou apoio, por unidade", 450, 0, 1000000);
            var pEsculpida = dlg.Number("esculpida", "Cuba esculpida (mão de obra), por unidade", 900, 0, 1000000);
            var pRalo = dlg.Number("ralo", "Ralo / caixa sifonada, por unidade", 120, 0, 1000000);
            var pRaloLin = dlg.Number("raloLinear", "Ralo linear, por metro", 650, 0, 1000000);
            var pGc = dlg.Number("guardaCorpo", "Guarda-corpo de vidro, por metro", 1400, 0, 1000000);
            var pBox = dlg.Number("box", "Box de banheiro, por unidade", 1800, 0, 1000000);
            OptionsDialog.NumberField pRipado = null, pPergolado = null;
            if (pecas.Any(u => u.Config is MadeiraConfig))
            {
                dlg.Section("Madeira (R$)");
                pRipado = dlg.Number("ripado", "Painel ripado, por m² de painel", 650, 0, 1000000);
                pPergolado = dlg.Number("pergolado", "Pergolado, por m² coberto", 1200, 0, 1000000);
            }
            var pRev = new Dictionary<string, OptionsDialog.NumberField>(StringComparer.OrdinalIgnoreCase);
            if (revest.Count > 0)
            {
                dlg.Section("Revestimentos paginados, por m² a comprar (R$)");
                foreach (string r in revest) pRev[r] = dlg.Number("rev." + Chave(r), r, 120, 0, 1000000, "m² com a perda, das paginações desenhadas com a ferramenta Paginação.");
            }
            if (!dlg.Show(Owner)) return Result.Cancelled;

            var br = System.Globalization.CultureInfo.GetCultureInfo("pt-BR");
            string N(double v, string f) => v.ToString(f, br);
            var tab = new XlsxWriter.Table { Name = "Orçamento", Title = "Orçamento — " + (string.IsNullOrWhiteSpace(Doc.Title) ? "projeto" : Doc.Title) };
            tab.Rows.Add(new[] { "Grupo", "Código", "Descrição", "Qtd.", "Medida", "Unidade", "Preço unitário (R$)", "Total (R$)" });
            double total = 0;
            var subtotais = new Dictionary<string, double>();
            var avisos = new List<string>();
            void Linha(string grupo, string codigo, string descricao, int qtd, double medida, string unidade, double preco)
            {
                double valor = medida * preco;
                total += valor;
                subtotais[grupo] = (subtotais.TryGetValue(grupo, out double s0) ? s0 : 0) + valor;
                tab.Rows.Add(new[] { grupo, codigo, descricao, qtd.ToString(), N(medida, "0.00"), unidade, N(preco, "0.00"), N(valor, "0.00") });
            }

            foreach (var e in esquadrias.OrderBy(x => x.Config.IsPorta).ThenBy(x => x.Codigo, StringComparer.OrdinalIgnoreCase))
            {
                double area = e.Config.LarguraMm * e.Config.AlturaMm / 1e6 * e.Instancias;
                Linha(e.Config.IsPorta ? "Portas" : "Janelas", e.Codigo, N(e.Config.LarguraMm, "0") + " x " + N(e.Config.AlturaMm, "0") + " mm",
                      e.Instancias, area, "m²", e.Config.IsPorta ? pPorta.Value : pJanela.Value);
            }
            // guarda-corpos made as Revit railings: metres from the railing's JA data
            foreach (var g in new FilteredElementCollector(Doc).OfClass(typeof(Autodesk.Revit.DB.Architecture.Railing)).Select(r => JaDados.Ler(r))
                         .Where(d => d != null && d.Tipo == "guardacorpo").GroupBy(d => d.Descricao ?? ""))
            {
                string desc = "";
                try { desc = Json.Deserialize<VidroConfig>(g.First().Config)?.Dimensoes() ?? ""; } catch { }
                Linha("Vidros", g.Key, "Guarda-corpo " + desc, g.Count(), g.Sum(d => d.Metros), "m", pGc.Value);
            }
            foreach (var u in pecas.OrderBy(x => x.Config.Tipo).ThenBy(x => x.Codigo, StringComparer.OrdinalIgnoreCase))
            {
                if (u.Config is RaloConfig rc)
                {
                    if (rc.Linear)
                    {
                        double ml = rc.NoCroqui ? rc.ComprimentoCaminho() / 1000 * u.Instancias : rc.ComprimentoMm / 1000 * u.Instancias;
                        Linha("Ralos", u.Codigo, u.Config.Dimensoes(), u.Instancias, ml, "m", pRaloLin.Value);
                    }
                    else Linha("Ralos", u.Codigo, u.Config.Dimensoes(), u.Instancias, u.Instancias, "un", pRalo.Value);
                    continue;
                }
                if (u.Config is VidroConfig vc)
                {
                    if (vc.Uso == UsoVidro.Box) Linha("Vidros", u.Codigo, u.Config.Descricao(), u.Instancias, u.Instancias, "un", pBox.Value);
                    // modulated: the profiles family carries the metres (the plates are counted with it)
                    else if (vc.Partes != ParteGuardaCorpo.Vidro || !pecas.Any(o => o.Config is VidroConfig ov && ov.Codigo == vc.Codigo && ov.Partes == ParteGuardaCorpo.Perfis))
                        Linha("Vidros", u.Codigo, "Guarda-corpo " + vc.Dimensoes(), u.Instancias, ProjectPecas.Metros(Doc, u.Symbol), "m", pGc.Value);
                    continue;
                }
                if (u.Config is MadeiraConfig mc)
                {
                    bool pg = mc.Uso == UsoMadeira.Pergolado;
                    double a = mc.LarguraMm * (pg ? mc.ProfundidadeMm : mc.AlturaMm) / 1e6 * u.Instancias;
                    Linha("Madeira", u.Codigo, mc.NomeUso + " " + mc.Dimensoes() + ", " + mc.MaterialMadeira.ToLowerInvariant(), u.Instancias, a, "m²", (pg ? pPergolado : pRipado)?.Value ?? 0);
                    continue;
                }
                PecaModelo m;
                try { m = u.Config.Build(); }
                catch (Exception ex) { avisos.Add(u.Codigo + ": " + ex.Message); continue; }
                if (m.AreaPedra <= 0) continue;
                double pedra;
                if (PecaUtil.Linear(u.Config))
                {
                    // stone per metre × the length of every copy
                    double metros = ProjectPecas.Metros(Doc, u.Symbol);
                    pedra = m.AreaPedra * metros;
                    if (metros <= 0) avisos.Add(u.Codigo + ": comprimento das peças corridas não encontrado (ficou sem valor).");
                }
                else pedra = m.AreaPedra * u.Instancias;
                string mat = Pedra(u.Config);
                double preco = pPedra.TryGetValue(mat, out var pf) ? pf.Value : 0;
                Linha("Pedras - " + mat, u.Codigo, PecaUtil.NomeTipo(u.Config.Tipo) + " " + u.Config.Dimensoes(), u.Instancias, pedra, "m²", preco);
                if (u.Config is BancadaConfig b)
                {
                    int esc = b.Cubas.Count(c => c.Tipo == TipoCuba.Esculpida) * u.Instancias, outras = (b.Cubas.Count - b.Cubas.Count(c => c.Tipo == TipoCuba.Esculpida)) * u.Instancias;
                    if (outras > 0) Linha("Cubas", u.Codigo, "Cuba de embutir / sobrepor / apoio", outras, outras, "un", pCuba.Value);
                    if (esc > 0) Linha("Cubas", u.Codigo, "Cuba esculpida", esc, esc, "un", pEsculpida.Value);
                }
            }
            foreach (var g in paginacoes.GroupBy(RotuloPag, StringComparer.OrdinalIgnoreCase))
                Linha("Revestimentos", g.Key, g.Count() + " paginação(ões), " + N(g.Sum(d => d.AreaM2), "0.00") + " m² de área", g.Count(), g.Sum(d => d.CompraM2), "m²",
                      pRev.TryGetValue(g.Key, out var rf) ? rf.Value : 0);

            tab.Rows.Add(new[] { "", "", "", "", "", "", "", "" });
            foreach (var kv in subtotais) tab.Rows.Add(new[] { "", "", "", "", "", "", "Subtotal " + kv.Key, N(kv.Value, "0.00") });
            tab.Rows.Add(new[] { "", "", "", "", "", "", "Total", N(total, "0.00") });

            string folder = Settings.Current.PastaRelatorios;
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "Orçamento - " + FamilyGenerator.SafeName(string.IsNullOrWhiteSpace(Doc.Title) ? "projeto" : Doc.Title) + ".xlsx");
            var xw = new XlsxWriter();
            xw.Add(tab);
            try { xw.Save(path); }
            catch (IOException) { path = Path.Combine(folder, Path.GetFileNameWithoutExtension(path) + " " + DateTime.Now.ToString("HHmmss") + ".xlsx"); xw.Save(path); }

            var texto = new StringBuilder("Estimativa: R$ " + N(total, "N2") + "\n");
            foreach (var kv in subtotais) texto.AppendLine("• " + kv.Key + ": R$ " + N(kv.Value, "N2"));
            texto.AppendLine();
            texto.AppendLine(path);
            texto.AppendLine("É uma estimativa pelos preços digitados (ficam salvos); painéis de cortina, ferragens e instalação não entram.");
            if (avisos.Count > 0) texto.AppendLine("\nAvisos:\n" + string.Join("\n", avisos.Take(15).Select(a => "• " + a)));
            Show(texto.ToString().TrimEnd(), avisos.Count > 0 ? ResultKind.Warning : ResultKind.Success, OpenFolderAction(folder));
            return Result.Succeeded;
        }
    }

    /// <summary>
    /// Lista para a Marmoraria: every stone piece of the JA countertops, niches and chapins used
    /// in the project (top, saia, frontão, tento, legs, carved sinks…), one line per piece with
    /// its cut size, thickness, area and quantity, as a CSV that opens in Excel.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class ListaMarmorariaCommand : ToolCommand
    {
        public override string Title => "Lista para a Marmoraria";

        protected override Result Run()
        {
            var usados = ProjectPecas.All(Doc).Where(p => p.Instancias > 0 && p.Config != null && !(p.Config is MadeiraConfig) && !(p.Config is VidroConfig) && !(p.Config is RaloConfig)).ToList();
            if (usados.Count == 0)
            {
                Show("Nenhuma bancada, nicho ou chapim do JA Famílias colocado neste projeto.", ResultKind.Info);
                return Result.Cancelled;
            }
            var dlg = new OptionsDialog(Title, "ListaMarmoraria");
            var fChapa = dlg.Number("chapa", "Comprimento máximo de uma peça (mm)", 2800, 500, 6000,
                "Peças mais compridas que isto são divididas em partes iguais, com emenda (a chapa de granito tem cerca de 2,80 a 3,20 m).");
            dlg.Section("Plano de corte");
            var fPlano = dlg.Check("plano", "Gerar plano de corte nas chapas (HTML para imprimir)", true,
                "Distribui as peças retangulares em chapas do tamanho abaixo, por material, e mostra quantas chapas comprar. É uma estimativa: a marmoraria confere veios e emendas.");
            var fLargChapa = dlg.Number("largChapa", "Largura da chapa (mm)", 1600, 300, 3000, "O comprimento da chapa é o comprimento máximo de uma peça, acima.");
            var fSerra = dlg.Number("serra", "Espessura do corte da serra (mm)", 5, 0, 20);
            var fVeio = dlg.Check("veio", "Respeitar o veio: não girar as peças na chapa", false,
                "Cada peça fica com o comprimento no sentido do comprimento da chapa (mármores e granitos com veio marcado). Usa mais chapas.");
            fLargChapa.EnabledWhen(fPlano);
            fSerra.EnabledWhen(fPlano);
            fVeio.EnabledWhen(fPlano);
            if (!dlg.Show(Owner)) return Result.Cancelled;
            double chapa = fChapa.Value;
            var itensPlano = new List<PlanoCorte.Item>();
            var br = System.Globalization.CultureInfo.GetCultureInfo("pt-BR");
            string N(double v, string f = "0.##") => v.ToString(f, br);
            string Q(string v) => (v ?? "").IndexOfAny(new[] { ';', '"', '\n' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v ?? "";
            var sb = new StringBuilder();
            sb.AppendLine("Código;Família;Peça;Qtd. de bancadas;Peças por bancada;Comprimento (mm);Largura (mm);Espessura (mm);Área da peça (m²);Área total (m²);Material;Observação");
            double total = 0;
            int linhas = 0;
            var avisos = new List<string>();
            foreach (var u in usados.OrderBy(x => x.Config.Tipo).ThenBy(x => x.Codigo, StringComparer.OrdinalIgnoreCase))
            {
                PecaModelo m;
                try { m = u.Config.Build(); }
                catch (Exception ex) { avisos.Add(u.Codigo + ": " + ex.Message); continue; }
                string material = u.Config.Materiais().FirstOrDefault(x => x.Key == "pedra")?.Nome ?? u.Config.MaterialPrincipal();
                // equal pieces of one family on one line
                var grupos = new Dictionary<string, (int n, string[] cols, double area)>();
                foreach (Peca p in m.Pecas)
                {
                    if (p.Void || p.Slot != "pedra" || p.Loops.Count == 0 || p.Loops[0].Count < 3) continue;
                    // the chamfer layer is the same stone as the top: not a piece of its own
                    if (p.Nome == "Chanfro da borda" || (p.Nome ?? "").EndsWith(" (topo)")) continue;
                    string obs = "";
                    double comp, larg, esp, area;
                    if (p.Revolucao) { comp = larg = esp = area = 0; obs = "peça curva: medir pelo raio e ângulo (ver família)"; }
                    else
                    {
                        Poligono.Envolvente(p.Loops[0], out double a, out double b);
                        double face = Math.Abs(Peca.Area(p.Loops[0])) - p.Loops.Skip(1).Sum(h => Math.Abs(Peca.Area(h)));
                        double d = Math.Abs(p.Depth);
                        // the cut face is the largest one: thin strips (saia, frontão) are drawn on edge
                        var dims = new[] { a, b, d }.OrderByDescending(x => x).ToArray();
                        comp = dims[0]; larg = dims[1]; esp = dims[2];
                        bool deitada = d <= Math.Min(a, b) + 0.01;
                        area = deitada ? face / 1e6 : comp * larg / 1e6;
                        if (deitada && face < a * b * 0.985) obs = p.Loops.Count > 1 ? "com recortes (cuba, cooktop ou furos)" : "contorno recortado (ver desenho)";
                        if (p.AcompanhaComprimento)
                        {
                            // line-based (chapim, rodapé): the length of each copy in the project
                            var trechos = ProjectPecas.ComprimentosMm(Doc, u.Symbol);
                            double metros = trechos.Sum() / 1000;
                            if (metros <= 0) { obs = "peça corrida: comprimento conforme o trecho (ver Quadro de Peças)"; comp = 0; area = 0; }
                            else
                            {
                                double perfil = Math.Max(a, b);
                                obs = "peça corrida: " + N(metros, "0.00") + " m em " + trechos.Count + " trecho(s), maior " + N(trechos.Max(), "0") + " mm";
                                comp = 0; larg = perfil; esp = Math.Min(a, b);
                                area = perfil / 1000 * metros / Math.Max(1, u.Instancias);
                                foreach (double tl in trechos)
                                {
                                    int partes = Math.Max(1, (int)Math.Ceiling(tl / chapa));
                                    for (int pt = 0; pt < partes; pt++)
                                        itensPlano.Add(new PlanoCorte.Item { Rotulo = u.Codigo + " · " + p.Nome + " " + N(tl, "0") + (partes > 1 ? " (" + (pt + 1) + "/" + partes + ")" : ""), Material = material, C = tl / partes, L = perfil });
                                }
                            }
                        }
                        else if (comp > chapa)
                        {
                            int partes = (int)Math.Ceiling(comp / chapa);
                            obs = (obs.Length > 0 ? obs + "; " : "") + partes + " partes de até " + N(comp / partes, "0") + " mm (" + (partes - 1) + " emenda" + (partes > 2 ? "s" : "") + ")";
                        }
                    }
                    if (comp > 0 && larg > 0 && area > 0 && !p.AcompanhaComprimento)
                    {
                        int partes = Math.Max(1, (int)Math.Ceiling(comp / chapa));
                        // the real outline of a flat piece with cut-outs (one part only), long side along X
                        List<P2> forma = null;
                        List<List<P2>> furos = null;
                        if (partes == 1 && !p.Revolucao && Math.Abs(p.Depth) <= 120 && p.Loops.Count > 0 &&
                            (p.Loops.Count > 1 || Math.Abs(Peca.Area(p.Loops[0])) < comp * larg * 0.985))
                        {
                            // the same frame as the measure: along the longest edge
                            var l0 = p.Loops[0];
                            double ux = 1, uy = 0, best = -1;
                            for (int q0 = 0; q0 < l0.Count; q0++)
                            {
                                P2 e0 = l0[q0], e1 = l0[(q0 + 1) % l0.Count];
                                double le = Math.Sqrt((e1.X - e0.X) * (e1.X - e0.X) + (e1.Y - e0.Y) * (e1.Y - e0.Y));
                                if (le > best) { best = le; ux = (e1.X - e0.X) / le; uy = (e1.Y - e0.Y) / le; }
                            }
                            P2 Gira(P2 q) => new P2(q.X * ux + q.Y * uy, -q.X * uy + q.Y * ux);
                            var g0 = l0.Select(Gira).ToList();
                            double x0 = g0.Min(q => q.X), y0 = g0.Min(q => q.Y), dx = g0.Max(q => q.X) - x0, dy = g0.Max(q => q.Y) - y0;
                            bool troca = dy > dx;
                            Func<P2, P2> f = q0 => { P2 q = Gira(q0); return troca ? new P2(q.Y - y0, q.X - x0) : new P2(q.X - x0, q.Y - y0); };
                            forma = p.Loops[0].Select(f).ToList();
                            furos = p.Loops.Skip(1).Select(l => l.Select(f).ToList()).ToList();
                        }
                        for (int k0 = 0; k0 < u.Instancias; k0++)
                            for (int pt = 0; pt < partes; pt++)
                                itensPlano.Add(new PlanoCorte.Item { Rotulo = u.Codigo + " · " + p.Nome + (partes > 1 ? " (" + (pt + 1) + "/" + partes + ")" : ""), Material = material, C = comp / partes, L = larg, Forma = forma, Furos = furos });
                    }
                    var cols = new[] { p.Nome, comp > 0 ? N(comp, "0") : "", larg > 0 ? N(larg, "0") : "", esp > 0 ? N(esp, "0.#") : "", area > 0 ? N(area, "0.000") : "", obs };
                    string k = string.Join("|", cols);
                    grupos[k] = grupos.TryGetValue(k, out var g) ? (g.n + 1, cols, area) : (1, cols, area);
                }
                foreach (var g in grupos.Values)
                {
                    double at = g.area * g.n * u.Instancias;
                    total += at;
                    linhas++;
                    sb.AppendLine(string.Join(";", new[]
                    {
                        Q(u.Codigo), Q(PecaUtil.NomeTipo(u.Config.Tipo)), Q(g.cols[0]), u.Instancias.ToString(), g.n.ToString(),
                        g.cols[1], g.cols[2], g.cols[3], g.cols[4], at > 0 ? N(at, "0.000") : "", Q(material), Q(g.cols[5])
                    }));
                }
            }
            sb.AppendLine(";;;;;;;;Total;" + N(total, "0.000") + ";;");
            string folder = Settings.Current.PastaRelatorios;
            Directory.CreateDirectory(folder);
            string nome = "Lista para a marmoraria - " + FamilyGenerator.SafeName(string.IsNullOrWhiteSpace(Doc.Title) ? "projeto" : Doc.Title) + ".csv";
            string path = Path.Combine(folder, nome);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));

            string totalTxt = N(total, "0.00");
            string text = $"Lista criada: {linhas} linha(s) de peças de pedra, {totalTxt} m² no total.\n{path}\n\nAs medidas são as de corte de cada peça (caixa envolvente); confira as peças com recorte pelo desenho.";
            if (fPlano.Value && itensPlano.Count > 0)
            {
                try
                {
                    var naoCabem = new List<PlanoCorte.Item>();
                    var chapas = PlanoCorte.Montar(itensPlano, chapa, fLargChapa.Value, fSerra.Value, naoCabem, !fVeio.Value);
                    string html = PlanoCorte.Html("Plano de corte — " + (string.IsNullOrWhiteSpace(Doc.Title) ? "projeto" : Doc.Title), chapas, chapa, fLargChapa.Value, fSerra.Value, naoCabem);
                    string hp = Path.Combine(folder, "Plano de corte - " + FamilyGenerator.SafeName(string.IsNullOrWhiteSpace(Doc.Title) ? "projeto" : Doc.Title) + ".html");
                    File.WriteAllText(hp, html, new UTF8Encoding(false));
                    text += $"\n\nPlano de corte: {chapas.Count} chapa(s) de {N(chapa, "0")} × {N(fLargChapa.Value, "0")} mm" +
                            string.Concat(chapas.GroupBy(c => c.Material).Select(g => $"\n• {g.Key}: {g.Count()}")) +
                            (naoCabem.Count > 0 ? $"\n{naoCabem.Count} peça(s) maiores que a chapa (listadas no fim)." : "") + "\n" + hp;
                }
                catch (Exception ex) { avisos.Add("Plano de corte: " + ex.Message); }
            }
            if (avisos.Count > 0) text += "\n\nAvisos:\n" + string.Join("\n", avisos.Take(15).Select(a => "• " + a));
            Show(text, avisos.Count > 0 ? ResultKind.Warning : ResultKind.Success, OpenFolderAction(folder));
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.Manual)]
    public sealed class QuadroPecasCommand : ToolCommand
    {
        public override string Title => "Quadro de Peças";

        protected override Result Run()
        {
            var all = ProjectPecas.All(Doc);
            int paineis = PainelGenerator.All(Doc).Select(p => p.Codigo).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if ((all.Count == 0 && paineis == 0) || SharedParams.ProjectId(Doc, SharedParams.Familia) == ElementId.InvalidElementId)
            {
                Show("Ainda não há bancadas, nichos, chapins, ralos ou painéis de cortina do JA Famílias neste projeto.\nGere pelo menos um e rode o Quadro de novo.", ResultKind.Info);
                return Result.Cancelled;
            }
            var dlg = new OptionsDialog(Title, "QuadroPecas");
            var kinds = new List<KeyValuePair<TipoPeca, Func<bool>>>();
            var names = new Dictionary<TipoPeca, Func<string>>();
            foreach (TipoPeca t in Enum.GetValues(typeof(TipoPeca)))
            {
                int n = all.Count(p => p.Config.Tipo == t);
                string plural = PecaUtil.Plural(t);
                var chk = dlg.Check("k" + t, plural + " (" + n + (n == 1 ? " tipo" : " tipos") + " no projeto)", n > 0);
                var nm = dlg.Text("n" + t, "Nome do quadro de " + plural.ToLowerInvariant(), "JA - Quadro de " + plural);
                kinds.Add(new KeyValuePair<TipoPeca, Func<bool>>(t, () => chk.Value));
                names[t] = () => nm.Value.Trim();
            }
            var chkP = dlg.Check("kPaineis", "Painéis de cortina (" + paineis + (paineis == 1 ? " código" : " códigos") + " no projeto)", paineis > 0);
            var nmP = dlg.Text("nPaineis", "Nome do quadro de painéis de cortina", "JA - Quadro de Painéis de Cortina");
            var recreate = dlg.Check("recreate", "Recriar o quadro se já existir (atualiza as colunas)", true);
            if (!dlg.Show(Owner)) throw new ToolCancelledException();

            var warnings = new List<string>();
            var created = new List<ViewSchedule>();
            using (var t = new Transaction(Doc, "JA Famílias – Quadro de Peças"))
            {
                t.Start();
                foreach (var k in kinds.Where(k => k.Value()))
                    created.Add(ScheduleBuilder.CreatePecas(Doc, k.Key, names[k.Key](), recreate.Value, warnings));
                if (chkP.Value) created.Add(ScheduleBuilder.CreatePaineis(Doc, nmP.Value.Trim(), recreate.Value, warnings));
                t.Commit();
            }
            created = created.Where(v => v != null && v.IsValidObject).ToList();
            if (created.Count == 0)
            {
                Show("Nenhum quadro marcado.", ResultKind.Info);
                return Result.Cancelled;
            }
            TryOpen(created[0]);
            var sb = new StringBuilder();
            sb.AppendLine(created.Count + " quadro(s) criado(s).");
            foreach (var v in created) sb.AppendLine("• " + v.Name);
            sb.AppendLine();
            sb.AppendLine("Chapins: o comprimento total soma todas as cópias (cada uma acompanha o seu muro).");
            if (warnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Avisos:");
                foreach (string w in warnings.Distinct()) sb.AppendLine("• " + w);
            }
            Show(sb.ToString());
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public sealed class RelatorioEsquadriasCommand : ToolCommand
    {
        public override string Title => "Relatório de Esquadrias";

        protected override Result Run()
        {
            var all = ProjectEsquadrias.All(Doc);
            if (all.Count == 0)
            {
                Show("Ainda não há esquadrias do JA Famílias neste projeto.\nGere pelo menos uma com Nova Janela ou Nova Porta.", ResultKind.Info);
                return Result.Cancelled;
            }
            var settings = Settings.Current;
            var dlg = new OptionsDialog(Title, "Relatorio");
            var folder = dlg.Text("folder", "Pasta do relatório", settings.PastaRelatorios);
            var unused = dlg.Check("unused", "Incluir tipos que não estão colocados no modelo", false);
            var open = dlg.Check("open", "Abrir no navegador ao terminar", true);
            if (!dlg.Show(Owner)) throw new ToolCancelledException();

            var items = all.Where(a => unused.Value || a.Instancias > 0)
                           .Select(a => new ReportItem { Config = a.Config, Quantidade = a.Instancias }).ToList();
            if (items.Count == 0)
            {
                Show("Nenhuma esquadria JA está colocada no modelo.\nMarque “Incluir tipos que não estão colocados” para listar mesmo assim.", ResultKind.Info);
                return Result.Cancelled;
            }
            string project = string.IsNullOrWhiteSpace(Doc.Title) ? "Projeto" : Path.GetFileNameWithoutExtension(Doc.Title);
            Directory.CreateDirectory(folder.Value);
            string path = Path.Combine(folder.Value, FamilyGenerator.SafeName(project) + " - Esquadrias.html");
            File.WriteAllText(path, ReportHtml.Build(project, items, settings.Fabricante), new UTF8Encoding(false));
            if (open.Value) ResultDialog.OpenFile(path);

            Show($"Relatório gerado com {items.Count} tipo(s) e {items.Sum(i => i.Quantidade)} unidade(s).\nNo navegador: Imprimir → Salvar como PDF.\n\nArquivo:\n{path}",
                 ResultKind.Success, new ResultAction("Abrir relatório", () => ResultDialog.OpenFile(path), true));
            return Result.Succeeded;
        }
    }

    /// <summary>Settings work with or without a project open.</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class ConfiguracoesCommand : IExternalCommand
    {
        public const string Title = "Configurações";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            IntPtr owner = IntPtr.Zero;
            try { owner = commandData.Application.MainWindowHandle; } catch { }
            try
            {
                var s = Settings.Current;
                string autoJ = null, autoP = null, autoG = null, autoGP = null, autoGL = null, autoPC = null;
                try
                {
                    var probe = new Settings().Fill();
                    autoJ = TemplateLocator.Find(commandData.Application.Application, TipoEsquadria.Janela, probe, out _);
                    autoP = TemplateLocator.Find(commandData.Application.Application, TipoEsquadria.Porta, probe, out _);
                    autoG = TemplateLocator.FindGeneric(commandData.Application.Application, TemplateLocator.Hospedeiro.Livre, probe, out _);
                    autoGP = TemplateLocator.FindGeneric(commandData.Application.Application, TemplateLocator.Hospedeiro.Parede, probe, out _);
                    autoGL = TemplateLocator.FindGeneric(commandData.Application.Application, TemplateLocator.Hospedeiro.Linha, probe, out _);
                    autoPC = TemplateLocator.FindCurtainPanel(commandData.Application.Application, probe, out _);
                }
                catch { }

                var dlg = new OptionsDialog(Title, "Configuracoes");
                dlg.Section("Pastas");
                var fam = dlg.Text("pastaFamilias", "Pasta das famílias geradas (.rfa)", s.PastaFamilias);
                var lib = dlg.Text("pastaBiblioteca", "Pasta da biblioteca", s.PastaBiblioteca);
                var rel = dlg.Text("pastaRelatorios", "Pasta dos relatórios", s.PastaRelatorios);
                var atual = dlg.Text("pastaAtualizacoes", "Pasta de atualizações do escritório (rede ou OneDrive)", AvisoVersao.PastaAtualizacoes() ?? "",
                    "Onde fica o pacote “Instalar tudo” com o versoes.txt: ao abrir o Revit, o JA Famílias e o JA Tools avisam quando há versão nova lá. Vazio = sem aviso.");
                dlg.Section("Modelos de família");
                var tj = dlg.Text("tplJanela", "Modelo de janela (.rft)", s.TemplateJanela);
                var tp = dlg.Text("tplPorta", "Modelo de porta (.rft)", s.TemplatePorta);
                var tg = dlg.Text("tplGenerico", "Modelo genérico – bancadas (.rft)", s.TemplateGenerico);
                var tgp = dlg.Text("tplGenericoParede", "Modelo genérico baseado em parede – nichos (.rft)", s.TemplateGenericoParede);
                var tgl = dlg.Text("tplGenericoLinha", "Modelo genérico baseado em linha – chapins (.rft)", s.TemplateGenericoLinha);
                var tpc = dlg.Text("tplPainelCortina", "Modelo de painel de parede cortina (.rft)", s.TemplatePainelCortina);
                dlg.Note("Encontrados automaticamente:\nJanela: " + (autoJ ?? "nenhum") + "\nPorta: " + (autoP ?? "nenhum") +
                         "\nBancadas: " + (autoG ?? "nenhum") + "\nNichos: " + (autoGP ?? "nenhum") + "\nChapins: " + (autoGL ?? "nenhum") + "\nPainéis de cortina: " + (autoPC ?? "nenhum"));
                dlg.Section("Padrões do escritório");
                var prefixo = dlg.Text("prefixo", "Prefixo do nome da família", s.PrefixoFamilia);
                var fab = dlg.Text("fabricante", "Fabricante", s.Fabricante);
                var perfil = dlg.Choice("perfil", "Linha de perfis", Catalog.Perfis.Select(p => p.Nome).ToList(), Math.Max(0, Catalog.Perfis.ToList().FindIndex(p => p.Id == s.PerfilPadrao)));
                var cor = dlg.Choice("cor", "Cor", Catalog.Cores.Select(c => c.Nome).ToList(), Math.Max(0, Catalog.Cores.ToList().FindIndex(c => c.Id == s.CorPadrao)));
                var vidro = dlg.Choice("vidro", "Vidro", Catalog.Vidros.Select(v => v.Nome).ToList(), Math.Max(0, Catalog.Vidros.ToList().FindIndex(v => v.Id == s.VidroPadrao)));
                var pos = dlg.Choice("posicao", "Posição na parede", new[] { "Face externa", "Centro", "Face interna" }, (int)s.PosicaoPadrao);
                var parede = dlg.Number("parede", "Espessura de parede da prévia (mm)", s.ParedePadraoMm, 50, 1000, "Só para desenhar a prévia do configurador: as esquadrias geradas acompanham qualquer parede.");
                dlg.Note("Os padrões valem para novas esquadrias quando ainda não há uma “última usada”: o configurador sempre começa da última esquadria que você gerou.");
                if (!dlg.Show(owner)) return Result.Cancelled;

                s.PastaFamilias = fam.Value.Trim();
                s.PastaBiblioteca = lib.Value.Trim();
                s.PastaRelatorios = rel.Value.Trim();
                AvisoVersao.DefinirPasta(atual.Value);
                s.TemplateJanela = tj.Value.Trim().Trim('"');
                s.TemplatePorta = tp.Value.Trim().Trim('"');
                s.TemplateGenerico = tg.Value.Trim().Trim('"');
                s.TemplateGenericoParede = tgp.Value.Trim().Trim('"');
                s.TemplateGenericoLinha = tgl.Value.Trim().Trim('"');
                s.TemplatePainelCortina = tpc.Value.Trim().Trim('"');
                s.PrefixoFamilia = prefixo.Value.Trim();
                s.Fabricante = fab.Value.Trim();
                if (perfil.Index >= 0) s.PerfilPadrao = Catalog.Perfis[perfil.Index].Id;
                if (cor.Index >= 0) s.CorPadrao = Catalog.Cores[cor.Index].Id;
                if (vidro.Index >= 0) s.VidroPadrao = Catalog.Vidros[vidro.Index].Id;
                if (pos.Index >= 0) s.PosicaoPadrao = (PosicaoParede)pos.Index;
                s.ParedePadraoMm = parede.Value;
                s.Fill().Save();

                var problems = new List<string>();
                if (s.TemplateJanela.Length > 0 && !File.Exists(s.TemplateJanela)) problems.Add("Modelo de janela não encontrado: " + s.TemplateJanela);
                if (s.TemplatePorta.Length > 0 && !File.Exists(s.TemplatePorta)) problems.Add("Modelo de porta não encontrado: " + s.TemplatePorta);
                foreach (string g in new[] { s.TemplateGenerico, s.TemplateGenericoParede, s.TemplateGenericoLinha })
                    if (g.Length > 0 && !File.Exists(g)) problems.Add("Modelo genérico não encontrado: " + g);
                if (s.TemplatePainelCortina.Length > 0 && !File.Exists(s.TemplatePainelCortina)) problems.Add("Modelo de painel de parede cortina não encontrado: " + s.TemplatePainelCortina);
                string text = "Configurações salvas.\n\nPasta das famílias:\n" + s.PastaFamilias +
                              (problems.Count > 0 ? "\n\nAvisos:\n• " + string.Join("\n• ", problems) : "");
                ResultDialog.Show(owner, Title, text, problems.Count > 0 ? ResultKind.Warning : ResultKind.Success);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Logger.Error("[JA Famílias] Configurações falharam.", ex);
                TaskDialog.Show("JA Famílias", "Erro: " + ex.Message);
                return Result.Failed;
            }
        }
    }
}
