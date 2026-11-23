using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using JAEsquadrias.Common;
using JAEsquadrias.Core;
using JAEsquadrias.Engine;
using JAEsquadrias.Infrastructure;
using Settings = JAEsquadrias.Core.Settings;

namespace JAEsquadrias.Commands
{
    public abstract partial class EsquadriaCommandBase
    {
        /// <summary>The room a placed family stands in (its phase room, or the room at its point).</summary>
        protected Room AmbienteDe(FamilyInstance fi)
        {
            try { if (fi.Room != null) return fi.Room; } catch { }
            try
            {
                XYZ p = (fi.Location as LocationPoint)?.Point ?? ((fi.Location as LocationCurve)?.Curve?.Evaluate(0.5, true));
                if (p == null) { BoundingBoxXYZ bb = fi.get_BoundingBox(null); if (bb != null) p = (bb.Min + bb.Max) / 2; }
                return p != null ? Doc.GetRoomAtPoint(p + XYZ.BasisZ * 0.5) : null;
            }
            catch { return null; }
        }

        protected double MetrosDe(FamilySymbol sym) => ProjectPecas.Metros(Doc, sym);

        protected string NomeDoProjeto => string.IsNullOrWhiteSpace(Doc.Title) ? "projeto" : Doc.Title;

        protected string SalvarEm(string prefixo, string extensao, Action<string> salvar)
        {
            string folder = Settings.Current.PastaRelatorios;
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, prefixo + " - " + FamilyGenerator.SafeName(NomeDoProjeto) + extensao);
            try { salvar(path); }
            catch (IOException) { path = Path.Combine(folder, Path.GetFileNameWithoutExtension(path) + " " + DateTime.Now.ToString("HHmmss") + extensao); salvar(path); }
            return path;
        }
    }

    /// <summary>
    /// Lista de Compras da Obra: one Excel file, one sheet per supplier — marmoraria, vidraçaria,
    /// esquadrias, revestimentos (with grout and mortar), hidráulica, marcenaria, gesso and
    /// impermeabilização — from what the JA tools placed and drew in the project.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class ListaComprasCommand : EsquadriaCommandBase
    {
        public override string Title => "Lista de Compras";

        protected override Result Run()
        {
            var esquadrias = ProjectEsquadrias.All(Doc).Where(x => x.Instancias > 0 && x.Config != null).ToList();
            var pecas = ProjectPecas.All(Doc).Where(x => x.Instancias > 0 && x.Config != null).ToList();
            var pags = new FilteredElementCollector(Doc).OfClass(typeof(Group)).Cast<Group>().Select(g => JaDados.Ler(g)).Where(d => d != null && d.Tipo == "paginacao").ToList();
            var forros = ComDadosJa("forro", BuiltInCategory.OST_Ceilings, BuiltInCategory.OST_GenericModel).Select(x => x.D).ToList();
            ConfigImper ci = ConfigImper.Salva();
            var molhados = AmbientesMolhados(ci);

            var dlg = new OptionsDialog(Title, "ListaCompras", "Gerar lista");
            dlg.Section("Fornecedores");
            var fPedra = dlg.Check("pedra", "Marmoraria (bancadas, nichos, chapins, soleiras, peitoris, rodapés, degraus)", pecas.Any(p => EPedra(p.Config)));
            var gcRevit = new FilteredElementCollector(Doc).OfClass(typeof(Railing)).Select(r => JaDados.Ler(r)).Where(d => d != null && d.Tipo == "guardacorpo").ToList();
            var fVidro = dlg.Check("vidro", "Vidraçaria (boxes, guarda-corpos)", pecas.Any(p => p.Config is VidroConfig) || gcRevit.Count > 0);
            var fEsq = dlg.Check("esq", "Esquadrias (janelas e portas do JA Famílias)", esquadrias.Count > 0);
            var fRev = dlg.Check("rev", "Revestimentos (paginações: peças, rejunte, argamassa)", pags.Count > 0);
            var fHid = dlg.Check("hid", "Hidráulica (ralos)", pecas.Any(p => p.Config is RaloConfig));
            var fMad = dlg.Check("mad", "Marcenaria / madeira (painéis ripados, pergolados)", pecas.Any(p => p.Config is MadeiraConfig));
            var fGes = dlg.Check("ges", "Gesso (forros, tabicas, sancas)", forros.Count > 0);
            var fImp = dlg.Check("imp", $"Impermeabilização ({molhados.Count} área(s) molhada(s), {ci.NomeSistema.ToLowerInvariant()})", molhados.Count > 0,
                "Usa as últimas configurações da ferramenta Impermeabilização (nomes das áreas molhadas, alturas e produto).");
            dlg.Section("Perdas");
            var fPerdaPedra = dlg.Number("perdaPedra", "Pedra (%)", 15, 0, 60, "Recortes e veio: o plano de corte da Lista da Marmoraria dá o número de chapas.");
            var fPerdaGesso = dlg.Number("perdaGesso", "Gesso (%)", 10, 0, 50);
            var fPerdaMad = dlg.Number("perdaMad", "Madeira (%)", 10, 0, 50);
            if (!dlg.Show(Owner)) return Result.Cancelled;

            var br = PecaUtil.Pt;
            string N(double v, string f = "0.00") => v.ToString(f, br);
            var abas = new List<XlsxWriter.Table>();
            var resumo = new XlsxWriter.Table { Name = "Resumo", Title = "Lista de compras — " + NomeDoProjeto };
            resumo.Rows.Add(new[] { "Fornecedor", "Item", "Quantidade", "Unidade" });
            var avisos = new List<string>();
            var texto = new List<string>();
            void R(string forn, string item, double q, string un, string f = "0.00") => resumo.Rows.Add(new[] { forn, item, N(q, f), un });

            // ---------------------------------------------------------------- marmoraria
            if (fPedra.Value)
            {
                var t = new XlsxWriter.Table { Name = "Marmoraria", Title = "Marmoraria" };
                t.Rows.Add(new[] { "Material", "Código", "Peça", "Qtd.", "Comprimento total (m)", "Área de pedra (m²)", "Comprar com perda (m²)" });
                var porMat = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                foreach (var u in pecas.Where(p => EPedra(p.Config)).OrderBy(p => p.Config.Tipo).ThenBy(p => p.Codigo))
                {
                    PecaModelo m;
                    try { m = u.Config.Build(); } catch (Exception ex) { avisos.Add(u.Codigo + ": " + ex.Message); continue; }
                    if (m.AreaPedra <= 0) continue;
                    double metros = PecaUtil.Linear(u.Config) ? MetrosDe(u.Symbol) : 0;
                    double area = PecaUtil.Linear(u.Config) ? m.AreaPedra * metros : m.AreaPedra * u.Instancias;
                    string mat = u.Config.Materiais()?.FirstOrDefault(x => x.Key == "pedra")?.Nome?.Trim() ?? u.Config.MaterialPrincipal();
                    t.Rows.Add(new[] { mat, u.Codigo, PecaUtil.NomeTipo(u.Config.Tipo) + " " + u.Config.Dimensoes(), u.Instancias.ToString(), metros > 0 ? N(metros) : "", N(area), N(area * (1 + fPerdaPedra.Value / 100)) });
                    porMat[mat] = (porMat.TryGetValue(mat, out double a0) ? a0 : 0) + area;
                }
                foreach (var kv in porMat.OrderBy(k => k.Key)) R("Marmoraria", kv.Key, kv.Value * (1 + fPerdaPedra.Value / 100), "m²");
                if (porMat.Count > 0) texto.Add("Marmoraria: " + string.Join(", ", porMat.Select(k => k.Key + " " + N(k.Value * (1 + fPerdaPedra.Value / 100)) + " m²")));
                abas.Add(t);
            }
            // ---------------------------------------------------------------- vidraçaria
            if (fVidro.Value)
            {
                var t = new XlsxWriter.Table { Name = "Vidraçaria", Title = "Vidraçaria" };
                t.Rows.Add(new[] { "Código", "Item", "Qtd.", "Comprimento (m)", "Vidro (m²)", "Descrição" });
                int boxes = 0; double mGc = 0, vidro = 0;
                foreach (var u in pecas.Where(p => p.Config is VidroConfig).OrderBy(p => p.Codigo))
                {
                    var vc = (VidroConfig)u.Config;
                    if (vc.Uso == UsoVidro.Box)
                    {
                        double a = vc.ComprimentoMm * vc.AlturaMm / 1e6 + (vc.Canto ? vc.LadoFixoMm * vc.AlturaMm / 1e6 : 0);
                        t.Rows.Add(new[] { u.Codigo, "Box " + vc.NomeAbertura, u.Instancias.ToString(), "", N(a * u.Instancias), vc.Descricao() });
                        boxes += u.Instancias; vidro += a * u.Instancias;
                    }
                    else if (vc.Partes != ParteGuardaCorpo.Vidro || !pecas.Any(o => o.Config is VidroConfig ov && ov.Codigo == vc.Codigo && ov.Partes == ParteGuardaCorpo.Perfis))
                    {
                        double m = MetrosDe(u.Symbol);
                        double a = m * (vc.AlturaMm - (vc.Corrimao ? vc.CorrimaoAlturaMm : 0)) / 1000;
                        t.Rows.Add(new[] { u.Codigo, "Guarda-corpo", u.Instancias.ToString(), N(m), N(a), vc.Descricao() });
                        mGc += m; vidro += a;
                    }
                }
                // guarda-corpos made as Revit railings (their data is on the railing)
                foreach (var g in gcRevit.GroupBy(d => (d.Descricao ?? "") + "|" + (d.Peca ?? "")).OrderBy(g => g.Key))
                {
                    string[] k = g.Key.Split('|');
                    string desc = "";
                    try { desc = Json.Deserialize<VidroConfig>(g.First().Config)?.Descricao() ?? ""; } catch { }
                    double m = g.Sum(d => d.Metros), a = g.Sum(d => d.AreaM2);
                    t.Rows.Add(new[] { k[0], "Guarda-corpo (placas de " + k[1] + " mm)", g.Count().ToString(), N(m), N(a), desc });
                    mGc += m; vidro += a;
                }
                if (boxes > 0) R("Vidraçaria", "Boxes", boxes, "un", "0");
                if (mGc > 0) R("Vidraçaria", "Guarda-corpo de vidro", mGc, "m");
                R("Vidraçaria", "Vidro temperado / laminado (total)", vidro, "m²");
                abas.Add(t);
            }
            // ---------------------------------------------------------------- esquadrias
            if (fEsq.Value)
            {
                var t = new XlsxWriter.Table { Name = "Esquadrias", Title = "Esquadrias" };
                t.Rows.Add(new[] { "Código", "Tipo", "Qtd.", "Largura (mm)", "Altura (mm)", "Área total (m²)", "Perfil", "Cor", "Vidro", "Ferragens", "Descrição" });
                foreach (var e in esquadrias.OrderBy(x => x.Config.IsPorta).ThenBy(x => x.Codigo, StringComparer.OrdinalIgnoreCase))
                {
                    var c = e.Config;
                    string desc = "";
                    try { desc = Layout.Descricao(c); } catch { }
                    t.Rows.Add(new[] { e.Codigo, c.IsPorta ? "Porta" : "Janela", e.Instancias.ToString(), N(c.LarguraMm, "0"), N(c.AlturaMm, "0"), N(c.LarguraMm * c.AlturaMm / 1e6 * e.Instancias),
                                       Catalog.Perfil(c.PerfilId)?.Nome ?? "", Catalog.Cor(c.CorId)?.Nome ?? "", Catalog.Vidro(c.VidroId)?.Nome ?? "", c.Ferragens ?? "", desc });
                }
                int jan = esquadrias.Where(x => !x.Config.IsPorta).Sum(x => x.Instancias), por = esquadrias.Where(x => x.Config.IsPorta).Sum(x => x.Instancias);
                if (jan > 0) R("Esquadrias", "Janelas", jan, "un", "0");
                if (por > 0) R("Esquadrias", "Portas", por, "un", "0");
                abas.Add(t);
            }
            // ---------------------------------------------------------------- revestimentos
            if (fRev.Value)
            {
                var t = new XlsxWriter.Table { Name = "Revestimentos", Title = "Revestimentos (paginações)" };
                t.Rows.Add(new[] { "Revestimento", "Peça (mm)", "Paginações", "Área (m²)", "Comprar (m²)", "Caixas", "Inteiras", "Recortes", "Rejunte (kg)", "Argamassa colante (kg)", "Sacos de 20 kg" });
                foreach (var g in pags.GroupBy(d => { string mat = ""; try { mat = Json.Deserialize<ConfigPaginacao>(d.Config)?.Material ?? ""; } catch { } return (mat.Length > 0 ? mat : "Revestimento") + "|" + d.Peca; })
                                      .OrderBy(g => g.Key))
                {
                    string[] k = g.Key.Split('|');
                    double arg = g.Sum(d => d.ArgamassaKg), rej = g.Sum(d => d.RejunteKg), cx = g.Sum(d => d.Caixas);
                    t.Rows.Add(new[] { k[0], k.Length > 1 ? k[1] : "", g.Count().ToString(), N(g.Sum(d => d.AreaM2)), N(g.Sum(d => d.CompraM2)), cx > 0 ? N(cx, "0") : "",
                                       g.Sum(d => d.Inteiras).ToString(), g.Sum(d => d.Recortes).ToString(), N(rej, "0.0"), N(arg, "0"), N(Math.Ceiling(arg / 20), "0") });
                    R("Revestimentos", k[0] + " " + (k.Length > 1 ? k[1] : ""), g.Sum(d => d.CompraM2), "m²");
                    if (rej > 0) R("Revestimentos", "Rejunte para " + k[0] + " " + (k.Length > 1 ? k[1] : ""), rej, "kg", "0.0");
                }
                double argT = pags.Sum(d => d.ArgamassaKg);
                if (argT > 0) R("Revestimentos", "Argamassa colante (sacos de 20 kg)", Math.Ceiling(argT / 20), "sc", "0");
                if (pags.Any(d => d.RejunteKg <= 0 && d.AreaM2 > 0)) avisos.Add("Há paginações feitas numa versão antiga (sem rejunte e argamassa): rode Atualizar Revestimentos para recalcular.");
                abas.Add(t);
            }
            // ---------------------------------------------------------------- hidráulica
            if (fHid.Value)
            {
                var t = new XlsxWriter.Table { Name = "Hidráulica", Title = "Hidráulica (ralos)" };
                t.Rows.Add(new[] { "Código", "Qtd.", "Metros (lineares)", "Descrição" });
                foreach (var u in pecas.Where(p => p.Config is RaloConfig).OrderBy(p => p.Codigo))
                {
                    var rc = (RaloConfig)u.Config;
                    double ml = rc.Linear ? (rc.NoCroqui ? rc.ComprimentoCaminho() : rc.ComprimentoMm) / 1000 * u.Instancias : 0;
                    t.Rows.Add(new[] { u.Codigo, u.Instancias.ToString(), ml > 0 ? N(ml) : "", rc.Descricao() });
                    if (ml > 0) R("Hidráulica", "Ralo linear " + u.Codigo, ml, "m"); else R("Hidráulica", "Ralo " + u.Codigo + " " + rc.Dimensoes(), u.Instancias, "un", "0");
                }
                abas.Add(t);
            }
            // ---------------------------------------------------------------- marcenaria
            if (fMad.Value)
            {
                var t = new XlsxWriter.Table { Name = "Madeira", Title = "Marcenaria e madeira (lista de corte)" };
                t.Rows.Add(new[] { "Material", "Peça", "Seção", "Comprimento (mm)", "Qtd.", "Metros lineares", "Volume (m³)", "Origem" });
                var tot = new Dictionary<string, (double ml, double m3)>(StringComparer.OrdinalIgnoreCase);
                foreach (var u in pecas.Where(p => p.Config is MadeiraConfig).OrderBy(p => p.Codigo))
                {
                    var mc = (MadeiraConfig)u.Config;
                    foreach (PecaMadeira pm in mc.ListaDeCorte())
                    {
                        int q = pm.Quantidade * u.Instancias;
                        double ml = q * pm.ComprimentoMm / 1000 * (1 + fPerdaMad.Value / 100), m3 = pm.VolumeM3 * u.Instancias * (1 + fPerdaMad.Value / 100);
                        t.Rows.Add(new[] { mc.MaterialMadeira, pm.Nome, pm.Secao, N(pm.ComprimentoMm, "0"), q.ToString(), N(ml), N(m3, "0.000"), u.Codigo + " (" + u.Instancias + "×)" });
                        string chave = mc.MaterialMadeira + " " + pm.Nome.ToLowerInvariant() + " " + pm.Secao;
                        (double ml, double m3) cur = tot.TryGetValue(chave, out var c0) ? c0 : (0.0, 0.0);
                        tot[chave] = (cur.ml + ml, cur.m3 + m3);
                    }
                    if (mc.Uso == UsoMadeira.PainelRipado && mc.Fundo)
                        t.Rows.Add(new[] { mc.MaterialFundo, "Fundo", PecaUtil.Mm(mc.FundoEspessuraMm) + " mm", "", u.Instancias.ToString(), "", "", u.Codigo + ": " + N(mc.LarguraMm * mc.AlturaMm / 1e6 * u.Instancias) + " m² de chapa" });
                }
                foreach (var kv in tot) R("Madeira", kv.Key, kv.Value.ml, "m");
                abas.Add(t);
            }
            // ---------------------------------------------------------------- gesso
            if (fGes.Value)
            {
                var t = new XlsxWriter.Table { Name = "Gesso", Title = "Gesso (forros e sancas)" };
                t.Rows.Add(new[] { "Ambiente", "Forro (m²)", "Espelho da sanca (m²)", "Tabica (m)", "Sanca (m)", "Descrição" });
                double fT = 0, eT = 0, tabT = 0, sT = 0;
                foreach (var g in forros.GroupBy(d => d.Ambiente))
                {
                    Room r = Doc.GetElement(new ElementId(g.Key)) as Room;
                    string desc = "";
                    try { desc = Json.Deserialize<ConfigForro>(g.First().Config)?.Descricao() ?? ""; } catch { }
                    double f = g.Where(d => d.Descricao != "espelho da sanca").Sum(d => d.AreaM2), e = g.Where(d => d.Descricao == "espelho da sanca").Sum(d => d.AreaM2);
                    double tab = g.Where(d => d.Descricao != "espelho da sanca").Sum(d => d.Metros), s = g.Where(d => d.Descricao == "espelho da sanca").Sum(d => d.Metros);
                    ConfigForro cf = null; try { cf = Json.Deserialize<ConfigForro>(g.First().Config); } catch { }
                    if (cf != null && cf.Tabica <= 0) tab = 0;
                    t.Rows.Add(new[] { r != null ? (r.Number + " " + r.Name).Trim() : "(ambiente apagado)", N(f), N(e), N(tab), N(s), desc });
                    fT += f; eT += e; tabT += tab; sT += s;
                }
                double k = 1 + fPerdaGesso.Value / 100;
                t.Rows.Add(new[] { "Total", N(fT), N(eT), N(tabT), N(sT), "" });
                t.Rows.Add(new[] { "Comprar (com " + N(fPerdaGesso.Value, "0") + "%)", N(fT * k), N(eT * k), N(tabT * k), N(sT * k), "Placas 1,20 × 1,80 m: " + Math.Ceiling((fT + eT) * k / 2.16) });
                R("Gesso", "Forro de gesso (placas 1,20 × 1,80 m)", Math.Ceiling((fT + eT) * k / 2.16), "placas", "0");
                if (tabT > 0) R("Gesso", "Tabica", tabT * k, "m");
                if (sT > 0) R("Gesso", "Sanca (espelho)", sT * k, "m");
                abas.Add(t);
            }
            // ---------------------------------------------------------------- impermeabilização
            if (fImp.Value && molhados.Count > 0)
            {
                var lista = CalcularImper(molhados, ci);
                var t = new XlsxWriter.Table { Name = "Impermeabilização", Title = "Impermeabilização · " + ci.NomeSistema };
                string un = ci.Sistema == 2 ? "m²" : "kg";
                t.Rows.Add(new[] { "Ambiente", "Piso (m²)", "Subida (m²)", "Box (m²)", "Total (m²)", ci.NomeSistema + " (" + un + ")" });
                foreach (var a in lista) t.Rows.Add(new[] { a.Nome, N(a.PisoM2), N(a.SubidaM2), N(a.BoxM2), N(a.TotalM2), N(ProdutoImper(ci, a.TotalM2), "0.0") });
                double tot = lista.Sum(a => a.TotalM2);
                t.Rows.Add(new[] { "Total", N(lista.Sum(a => a.PisoM2)), N(lista.Sum(a => a.SubidaM2)), N(lista.Sum(a => a.BoxM2)), N(tot), N(ProdutoImper(ci, tot), "0.0") });
                R("Impermeabilização", ci.NomeSistema, ProdutoImper(ci, tot), un, "0.0");
                abas.Add(t);
            }
            if (resumo.Rows.Count <= 1) { Show("Nada para a lista: marque pelo menos um fornecedor que tenha itens no projeto.", ResultKind.Info); return Result.Cancelled; }
            // the summary first
            var tudo = new XlsxWriter();
            tudo.Add(resumo);
            foreach (var tb in abas) tudo.Add(tb);
            string path = SalvarEm("Lista de compras", ".xlsx", tudo.Save);
            Show($"Lista de compras com {resumo.Rows.Count - 1} item(ns) no resumo e uma aba por fornecedor.\n\n" + (texto.Count > 0 ? string.Join("\n", texto) + "\n\n" : "") + path +
                 "\n\nQuantidades a partir do modelo (com as perdas digitadas): confira com os fornecedores antes de fechar o pedido." +
                 (avisos.Count > 0 ? "\n\nAvisos:\n• " + string.Join("\n• ", avisos.Distinct().Take(15)) : ""), avisos.Count > 0 ? ResultKind.Warning : ResultKind.Success, OpenFolderAction(Path.GetDirectoryName(path)));
            return Result.Succeeded;
        }

        private static bool EPedra(IPecaConfig c) => c.Tipo == TipoPeca.Bancada || c.Tipo == TipoPeca.Nicho || c.Tipo == TipoPeca.Chapim || c.Tipo == TipoPeca.Pedra;
    }

    /// <summary>
    /// Memorial Descritivo: a Word file organised by room — area, floor (paginação), rodapé, walls,
    /// ceiling (forro / sanca), the openings and the JA pieces in it, impermeabilização — followed by
    /// the schedules of esquadrias, stones, glass and wood. A first draft to complete in Word.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class MemorialCommand : EsquadriaCommandBase
    {
        public override string Title => "Memorial Descritivo";

        protected override Result Run()
        {
            var rooms = AmbientesColocados();
            var esquadrias = ProjectEsquadrias.All(Doc).Where(x => x.Instancias > 0 && x.Config != null).ToList();
            var pecas = ProjectPecas.All(Doc).Where(x => x.Instancias > 0 && x.Config != null).ToList();
            if (rooms.Count == 0 && esquadrias.Count == 0 && pecas.Count == 0) { Show("O projeto não tem ambientes nem famílias do JA Famílias para descrever.", ResultKind.Info); return Result.Cancelled; }

            var dlg = new OptionsDialog(Title, "Memorial", "Gerar memorial");
            var fAbertura = dlg.Text("abertura", "Texto de abertura", "Este memorial descreve os materiais e acabamentos previstos no projeto de arquitetura. Em caso de divergência, prevalecem os desenhos e as especificações mais recentes.");
            dlg.Section("Conteúdo");
            var fAmb = dlg.Check("amb", $"Ambientes ({rooms.Count}): piso, rodapé, paredes, teto, esquadrias e peças", rooms.Count > 0);
            var fVazios = dlg.Check("vazios", "Incluir ambientes sem nenhum acabamento informado", false);
            fVazios.EnabledWhen(fAmb);
            var fEsq = dlg.Check("esq", $"Quadro de esquadrias ({esquadrias.Count} tipo(s))", esquadrias.Count > 0);
            var fPec = dlg.Check("pec", "Pedras, vidros, ralos e madeira", pecas.Count > 0);
            var fImp = dlg.Check("imp", "Impermeabilização das áreas molhadas (configurações da ferramenta Impermeabilização)", true);
            if (!dlg.Show(Owner)) return Result.Cancelled;

            var br = PecaUtil.Pt;
            string N(double v, string f = "0.00") => v.ToString(f, br);
            var doc = new DocxWriter();
            doc.Titulo("Memorial Descritivo");
            ProjectInfo pi = Doc.ProjectInformation;
            string Info(BuiltInParameter b) { try { return pi?.get_Parameter(b)?.AsString() ?? ""; } catch { return ""; } }
            string nome = Info(BuiltInParameter.PROJECT_NAME), cliente = Info(BuiltInParameter.CLIENT_NAME), endereco = Info(BuiltInParameter.PROJECT_ADDRESS), numero = Info(BuiltInParameter.PROJECT_NUMBER);
            doc.Campo("Projeto", string.IsNullOrWhiteSpace(nome) ? NomeDoProjeto : nome);
            if (!string.IsNullOrWhiteSpace(numero)) doc.Campo("Número", numero);
            if (!string.IsNullOrWhiteSpace(cliente)) doc.Campo("Cliente", cliente);
            if (!string.IsNullOrWhiteSpace(endereco)) doc.Campo("Endereço", endereco.Replace("\r", "").Replace("\n", ", "));
            doc.Campo("Data", DateTime.Now.ToString("dd/MM/yyyy"));
            if (!string.IsNullOrWhiteSpace(fAbertura.Value)) doc.Paragrafo(fAbertura.Value.Trim());

            int secao = 0, descritos = 0;
            ConfigImper ci = ConfigImper.Salva();
            var molhados = new HashSet<long>(AmbientesMolhados(ci).Select(r => r.Id.Value));
            var imper = fImp.Value && molhados.Count > 0 ? CalcularImper(rooms.Where(r => molhados.Contains(r.Id.Value)).ToList(), ci).ToDictionary(a => a.Room.Id.Value) : new Dictionary<long, ImperAmbiente>();
            if (fAmb.Value && rooms.Count > 0)
            {
                // what the JA tools left in each room
                var pags = new FilteredElementCollector(Doc).OfClass(typeof(Group)).Cast<Group>().Select(g => JaDados.Ler(g)).Where(d => d != null && d.Tipo == "paginacao" && d.Ambiente != 0)
                    .GroupBy(d => d.Ambiente).ToDictionary(g => g.Key, g => g.First());
                var rods = new FilteredElementCollector(Doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().Select(fi => JaDados.Ler(fi)).Where(d => d != null && d.Tipo == "rodape")
                    .GroupBy(d => d.Ambiente).ToDictionary(g => g.Key, g => g.First());
                var forros = ComDadosJa("forro", BuiltInCategory.OST_Ceilings, BuiltInCategory.OST_GenericModel).Select(x => x.D).GroupBy(d => d.Ambiente).ToDictionary(g => g.Key, g => g.First());
                var aberturas = new FilteredElementCollector(Doc).WherePasses(new LogicalOrFilter(new ElementCategoryFilter(BuiltInCategory.OST_Windows), new ElementCategoryFilter(BuiltInCategory.OST_Doors)))
                    .WhereElementIsNotElementType().OfType<FamilyInstance>().ToList();
                var instPecas = new FilteredElementCollector(Doc).WherePasses(new ElementMulticategoryFilter(new List<BuiltInCategory> { BuiltInCategory.OST_GenericModel, BuiltInCategory.OST_Casework, BuiltInCategory.OST_PipeFitting }))
                    .WhereElementIsNotElementType().OfType<FamilyInstance>().Select(fi => new { Fi = fi, C = ProjectPecas.ConfigOf(fi, out _) }).Where(x => x.C != null).ToList();
                var pecasPorAmb = new Dictionary<long, List<IPecaConfig>>();
                foreach (var x in instPecas)
                {
                    if (x.C is PedraConfig pc && pc.Uso == UsoPedra.Rodape) continue;   // rodapés: by room, below
                    Room r = AmbienteDe(x.Fi);
                    if (r == null) continue;
                    if (!pecasPorAmb.TryGetValue(r.Id.Value, out var l)) pecasPorAmb[r.Id.Value] = l = new List<IPecaConfig>();
                    if (!l.Any(o => o.Codigo == x.C.Codigo && o.Tipo == x.C.Tipo)) l.Add(x.C);
                }
                doc.Secao(++secao + ". Ambientes");
                foreach (var nivel in rooms.GroupBy(r => r.Level?.Name ?? "").OrderBy(g => g.First().Level?.ProjectElevation ?? 0))
                {
                    if (rooms.Select(r => r.Level?.Name).Distinct().Count() > 1) doc.Negrito(nivel.Key);
                    foreach (Room r in nivel)
                    {
                        string P(BuiltInParameter b) { try { return r.get_Parameter(b)?.AsString()?.Trim() ?? ""; } catch { return ""; } }
                        var campos = new List<(string, string)>();
                        // floor
                        string piso = P(BuiltInParameter.ROOM_FINISH_FLOOR);
                        if (pags.TryGetValue(r.Id.Value, out DadosJa dp))
                        {
                            ConfigPaginacao cp = null; try { cp = Json.Deserialize<ConfigPaginacao>(dp.Config); } catch { }
                            string pg = ((cp?.Material ?? "").Length > 0 ? cp.Material : "Revestimento") + " " + dp.Peca + " mm" + (cp != null ? ", junta " + N(cp.Junta, "0.#") + " mm" + (cp.Direcao == 2 ? ", assentamento na diagonal" : "") : "");
                            piso = string.IsNullOrEmpty(piso) || pg.IndexOf(piso, StringComparison.OrdinalIgnoreCase) >= 0 ? pg : piso + " (" + pg + ")";
                        }
                        if (piso.Length > 0) campos.Add(("Piso", piso));
                        string rodape = P(BuiltInParameter.ROOM_FINISH_BASE);
                        if (rods.TryGetValue(r.Id.Value, out DadosJa dr))
                        {
                            ConfigRodape cr = null; try { cr = Json.Deserialize<ConfigRodape>(dr.Config); } catch { }
                            if (cr != null) rodape = cr.Material + ", altura " + N(cr.Altura, "0") + " mm, espessura " + N(cr.Espessura, "0") + " mm";
                        }
                        if (rodape.Length > 0) campos.Add(("Rodapé", rodape));
                        string paredes = P(BuiltInParameter.ROOM_FINISH_WALL);
                        if (paredes.Length > 0) campos.Add(("Paredes", paredes));
                        string teto = P(BuiltInParameter.ROOM_FINISH_CEILING);
                        if (forros.TryGetValue(r.Id.Value, out DadosJa df))
                        {
                            ConfigForro cf = null; try { cf = Json.Deserialize<ConfigForro>(df.Config); } catch { }
                            if (cf != null) teto = cf.Descricao() + ", a " + N(cf.Altura / 1000) + " m do piso" + (cf.Sanca > 0 ? " (sanca de " + N(cf.LarguraSanca, "0") + " mm, rebaixo de " + N(cf.Rebaixo, "0") + " mm)" : "");
                        }
                        if (teto.Length > 0) campos.Add(("Teto", teto));
                        var esq = aberturas.Where(fi => { try { return fi.FromRoom?.Id == r.Id || fi.ToRoom?.Id == r.Id; } catch { return false; } })
                            .Select(fi => { var c = ProjectEsquadrias.ConfigOf(fi, out _); return c != null ? c.Codigo + " (" + (c.IsPorta ? "porta" : "janela") + " " + N(c.LarguraMm, "0") + " × " + N(c.AlturaMm, "0") + ")" : fi.Symbol?.Name; })
                            .Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();
                        if (esq.Count > 0) campos.Add(("Esquadrias", string.Join("; ", esq)));
                        if (pecasPorAmb.TryGetValue(r.Id.Value, out var lp))
                            foreach (var g in lp.GroupBy(c => c.Tipo))
                                campos.Add((PecaUtil.Plural(g.Key), string.Join("; ", g.Select(c => c.Codigo + ": " + c.Descricao()))));
                        if (imper.TryGetValue(r.Id.Value, out var ia))
                            campos.Add(("Impermeabilização", ci.NomeSistema + " no piso e " + N(ci.Subida / 10, "0") + " cm nas paredes" + (ia.Boxes > 0 ? ", até " + N(ci.SubidaBox / 1000) + " m no box" : "") + " (≈ " + N(ia.TotalM2) + " m²)"));
                        if (campos.Count == 0 && !fVazios.Value) continue;
                        doc.Subsecao(((r.Number ?? "") + " " + (r.Name ?? "")).Trim() + " — " + N(r.Area * 0.09290304) + " m²");
                        if (campos.Count == 0) doc.Paragrafo("(sem acabamentos informados)");
                        foreach (var (rot, txt) in campos) doc.Campo(rot, txt);
                        descritos++;
                    }
                }
            }
            if (fEsq.Value && esquadrias.Count > 0)
            {
                doc.Secao(++secao + ". Esquadrias");
                var t = new List<string[]> { new[] { "Código", "Qtd.", "Largura × altura (mm)", "Descrição" } };
                foreach (var e in esquadrias.OrderBy(x => x.Config.IsPorta).ThenBy(x => x.Codigo, StringComparer.OrdinalIgnoreCase))
                {
                    string d = ""; try { d = Layout.Descricao(e.Config); } catch { }
                    if (!string.IsNullOrWhiteSpace(e.Config.Ferragens)) d += (d.Length > 0 ? ". " : "") + "Ferragens: " + e.Config.Ferragens.Trim();
                    t.Add(new[] { e.Codigo, e.Instancias.ToString(), N(e.Config.LarguraMm, "0") + " × " + N(e.Config.AlturaMm, "0"), d });
                }
                doc.Tabela(t);
            }
            if (fPec.Value && pecas.Count > 0)
            {
                foreach (var g in pecas.GroupBy(p => p.Config.Tipo).OrderBy(g => g.Key))
                {
                    doc.Secao(++secao + ". " + PecaUtil.Plural(g.Key));
                    var t = new List<string[]> { new[] { "Código", "Qtd.", "Descrição" } };
                    foreach (var u in g.OrderBy(p => p.Codigo, StringComparer.OrdinalIgnoreCase)) t.Add(new[] { u.Codigo, u.Instancias.ToString(), u.Config.Descricao() });
                    doc.Tabela(t);
                }
            }
            if (fImp.Value && imper.Count > 0)
            {
                doc.Secao(++secao + ". Impermeabilização");
                doc.Paragrafo($"Áreas molhadas com {ci.NomeSistema.ToLowerInvariant()}, aplicada sobre base regularizada, com subida de {N(ci.Subida / 10, "0")} cm nas paredes e de {N(ci.SubidaBox / 1000)} m nas paredes dos boxes; reforço com tela nos ralos, cantos e passagens de tubulação, teste de estanqueidade de 72 h antes da proteção mecânica (NBR 9574 / 9575).");
                var t = new List<string[]> { new[] { "Ambiente", "Área a impermeabilizar (m²)" } };
                foreach (var a in imper.Values) t.Add(new[] { a.Nome, N(a.TotalM2) });
                doc.Tabela(t);
            }
            doc.Secao(++secao + ". Observações");
            doc.Item("As medidas das esquadrias, bancadas e peças de pedra devem ser conferidas na obra antes da fabricação.");
            doc.Item("Materiais equivalentes só com a aprovação da arquitetura.");
            string path = SalvarEm("Memorial descritivo", ".docx", doc.Save);
            Show($"Memorial gerado: {descritos} ambiente(s) descrito(s)" + (fEsq.Value ? $", {esquadrias.Count} tipo(s) de esquadria" : "") + ".\n\n" + path +
                 "\n\nÉ um rascunho a partir do modelo: os acabamentos vêm dos parâmetros do ambiente (Acabamento do piso / paredes / teto / rodapé) e do que as ferramentas JA desenharam. Complete no Word.",
                 ResultKind.Success, OpenFolderAction(Path.GetDirectoryName(path)));
            return Result.Succeeded;
        }
    }
}
