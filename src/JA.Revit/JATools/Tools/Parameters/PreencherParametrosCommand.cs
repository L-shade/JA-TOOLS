using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDwgUnifier.Tools.Common;

namespace RevitDwgUnifier.Tools.Parameters
{
    /// <summary>
    /// Preencher Parâmetros: one parameter filled in bulk — of the instances or of their types — on the selected
    /// elements, or on the elements of the chosen categories in the active view or the whole project, with a piece of
    /// information of each element: its level, its room (doors and windows by the side chosen), a text model with
    /// tokens, a fixed text, elevation ranges (floors), a from → to table, or a find → replace in the current value.
    /// Optionally only within an elevation range or only where empty; a preview before writing; the text parameter can
    /// be created (shared, bound to the categories, varying between groups).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class PreencherParametrosCommand : ToolCommand
    {
        public override string Title => "Preencher Parâmetros";

        private const int FonteNivel = 0, FonteAmbiente = 1, FonteNumero = 2, FonteModelo = 3, FonteTexto = 4, FonteFaixas = 5,
                          FonteTabela = 6, FonteSubstituir = 7, FonteFormula = 8;
        private const string MarcaTipo = "  [tipo]";

        private sealed class Mudanca
        {
            public Element Alvo;        // the instance, or the type
            public Element Exemplo;     // an instance (for the preview)
            public int Instancias = 1;
            public string Antes, Depois;
            public object Bruto;        // the formula's typed result (numbers in m, m², m³), else null
        }

        protected override Result Run()
        {
            // ---- the elements
            var sel = UiDoc.Selection.GetElementIds().Select(id => Doc.GetElement(id)).Where(ValoresElemento.Alvo).ToList();
            ToolProgress.Pulse("Lendo as categorias…");
            List<Element> projeto = new FilteredElementCollector(Doc).WhereElementIsNotElementType().Where(ValoresElemento.Alvo).ToList();
            ToolProgress.Hide();
            if (projeto.Count == 0) { Show("Nenhum elemento de modelo no projeto.", ResultKind.Info); return Result.Cancelled; }
            var basePraCategorias = sel.Count > 0 ? sel : projeto;
            var categorias = basePraCategorias.GroupBy(e => e.Category.Name).OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase).ToList();

            var d1 = new OptionsDialog(Title, "PreencherParametros.Onde", "Continuar");
            var fEscopo = d1.Choice("escopo", "Onde", new List<string> { $"Selecionados ({sel.Count})", "Vista ativa", "Todo o projeto" }, sel.Count > 0 ? 0 : 2,
                "Selecionados: só os elementos selecionados antes do comando (das categorias marcadas abaixo). Dica: o Filtrar Elementos seleciona por regras.");
            if (sel.Count > 0) fEscopo.Index = 0;
            else if (fEscopo.Index == 0) fEscopo.Index = 2;
            var fCat = d1.Multi("categorias", "Categorias", categorias.Select(g => $"{g.Key} ({g.Count()})").ToList(), true, true,
                sel.Count > 0 ? "Categorias da seleção (contagem da seleção)." : "Contagem no projeto todo.");
            if (!d1.Show(Owner)) return Result.Cancelled;

            var cats = new HashSet<string>(fCat.SelectedIndices.Select(i => categorias[i].Key));
            IEnumerable<Element> fonte = fEscopo.Index == 0 ? sel
                : fEscopo.Index == 1 ? new FilteredElementCollector(Doc, Doc.ActiveView.Id).WhereElementIsNotElementType().Where(ValoresElemento.Alvo)
                : projeto;
            List<Element> alvo = fonte.Where(e => cats.Contains(e.Category.Name)).ToList();
            if (alvo.Count == 0) { Show("Nenhum elemento nessas categorias " + (fEscopo.Index == 0 ? "na seleção." : fEscopo.Index == 1 ? "na vista ativa." : "no projeto."), ResultKind.Info); return Result.Cancelled; }

            // ---- the parameters they can take (a sample of each category: fast on large projects)
            var vals = new ValoresElemento(Doc);
            ToolProgress.Pulse("Lendo os parâmetros…");
            List<string> deInstancia = vals.NomesDeParametros(alvo, false, true);
            List<string> deTipo = vals.NomesDeParametros(alvo, true, true);
            ToolProgress.Hide();
            var itens = new List<string> { "➕ Novo parâmetro de texto (nome abaixo)" };
            itens.AddRange(deInstancia);
            itens.AddRange(deTipo.Select(n => n + MarcaTipo));

            var d2 = new OptionsDialog(Title, "PreencherParametros", "Preencher");
            d2.Note($"{alvo.Count} elemento(s) de {cats.Count} categoria(s).");
            int iJaNivel = itens.FindIndex(x => string.Equals(x, ProjectParams.JaNivelNome, StringComparison.OrdinalIgnoreCase));
            var fDestino = d2.Choice("destino", "Parâmetro a preencher", itens, Math.Max(0, iJaNivel),
                "Parâmetros editáveis das instâncias e, marcados [tipo], dos tipos (o mesmo valor para todas as instâncias do tipo). “Novo” cria um parâmetro de projeto de texto nessas categorias.");
            var fNovo = d2.Text("novo", "Nome do novo parâmetro", ProjectParams.JaNivelNome,
                "Com o nome JA_Nivel, é o mesmo parâmetro que os rodapés do JA já preenchem.");
            fNovo.VisibleWhen(fDestino, 0);
            var fFonte = d2.Choice("fonte", "Com base em",
                new List<string> { "Nível do elemento", "Ambiente (nome)", "Ambiente (número)", "Modelo de texto", "Texto fixo", "Faixas de elevação", "Tabela de/para", "Substituir no valor atual", "Fórmula (como no Archicad)" }, 0,
                "Nível: a restrição de base, o nível do ambiente ou o do hospedeiro. Faixas: um valor por faixa de elevação (pavimentos). Tabela: um valor para cada valor de outro campo (ex.: o tipo). Substituir: troca um trecho do que já está escrito. Fórmula: contas, condições e textos com os campos do elemento.");
            var fModelo = d2.Text("modelo", "Modelo", "{Nível} - {Ambiente}",
                "Campos: " + string.Join(", ", ValoresElemento.Especiais.Select(x => "{" + x + "}")) + " ou {qualquer parâmetro}; de outro elemento: {Ambiente:Nome do parâmetro}, {Tipo:…}, {Nível:…}, {Hospedeiro:…}. {Seq} numera.");
            fModelo.VisibleWhen(fFonte, FonteModelo);
            var fFormula = d2.Text("formula", "Fórmula", "SE({Altura} > 2,1; \"Alta\"; \"Normal\")",
                "Ex.: ARRED({Comprimento} * {Altura}; 2) · {Nível} & \" - \" & {Ambiente} · SE(CONTÉM({Ambiente}; \"banho\"); \"Molhada\"; \"Seca\"). Comprimentos em m, áreas em m². " + Formula.Ajuda);
            fFormula.VisibleWhen(fFonte, FonteFormula);
            var fTexto = d2.Text("texto", "Texto", "", "O mesmo valor em todos (vazio limpa o parâmetro).");
            fTexto.VisibleWhen(fFonte, FonteTexto);
            var fFaixas = d2.Text("faixas", "Faixas (m)", "0-3 = Térreo; 3-6 = Primeiro pavimento",
                "De-até = valor, separadas por ponto e vírgula. A faixa inclui o início e não o fim (13,00 cai na faixa que começa em 13). Elevações como as dos níveis.");
            fFaixas.VisibleWhen(fFonte, FonteFaixas);
            var fChave = d2.Text("chave", "Campo comparado", "{Tipo}", "Um campo ou modelo, como no Modelo: {Tipo}, {Família}, {Ambiente}, {Marca}…");
            fChave.VisibleWhen(fFonte, FonteTabela);
            var fTabela = d2.Text("tabela", "Tabela", "Porta 80x210 = P1; Porta 90x210 = P2",
                "valor = novo, separados por ponto e vírgula; maiúsculas e acentos não importam. “* = valor” vale para o resto; sem ele, o resto não muda.");
            fTabela.VisibleWhen(fFonte, FonteTabela);
            var fProcurar = d2.Text("procurar", "Procurar", "", "O trecho a trocar (maiúsculas não importam).");
            fProcurar.VisibleWhen(fFonte, FonteSubstituir);
            var fTrocar = d2.Text("trocar", "Trocar por", "", "Vazio apaga o trecho.");
            fTrocar.VisibleWhen(fFonte, FonteSubstituir);

            bool UsaSeq() => (fFonte.LiveIndex == FonteModelo && TextUtil.Fold(fModelo.LiveValue).Contains("{seq")) ||
                             (fFonte.LiveIndex == FonteFormula && TextUtil.Fold(fFormula.LiveValue).Contains("{seq"));
            d2.Section("Numeração {Seq}");
            var fOrdem = d2.Choice("seqOrdem", "Ordem", new List<string> { "Nível e posição (de cima para baixo, da esquerda para a direita)", "Nível e posição (da esquerda para a direita, de cima para baixo)", "Pelo valor atual do parâmetro", "Como o Revit lista" }, 0,
                "Só aparece quando o modelo ou a fórmula usa {Seq}.");
            var fRecomeca = d2.Text("seqRecomeca", "Recomeçar a cada", "{Nível}", "Um campo ou modelo: a numeração volta ao início a cada valor diferente (vazio = uma numeração só).");
            var fInicio = d2.Number("seqInicio", "Começar em", 1, 0, 1000000);
            var fDigitos = d2.Number("seqDigitos", "Dígitos", 2, 1, 6, "2 → 01, 02…");
            foreach (var campoSeq in new OptionsDialog.Field[] { fOrdem, fRecomeca, fInicio, fDigitos }) campoSeq.VisibleWhen(UsaSeq);
            d2.Section("Opções");
            var fLetras = d2.Choice("letras", "Letras", new List<string> { "Como está", "MAIÚSCULAS", "minúsculas", "Primeira maiúscula" }, 0);
            var fFiltro = d2.Check("filtro", "Só elementos entre as elevações", false, "Para preencher um valor só num trecho do prédio (ex.: com Texto fixo).");
            var fDe = d2.Number("de", "De (m)", 0, -1000, 10000);
            var fAte = d2.Number("ate", "Até (m)", 3, -1000, 10000);
            fDe.VisibleWhen(() => fFiltro.Live); fAte.VisibleWhen(() => fFiltro.Live);
            var fRef = d2.Choice("referencia", "Elevação do elemento", new List<string> { "Nível + deslocamento da base", "Ponto mais baixo", "Centro", "Ponto mais alto" }, 0,
                "Nível + deslocamento: paredes pela base, pisos pelo topo (nível + deslocamento), famílias pela elevação; sem nível, o ponto mais baixo.");
            fRef.VisibleWhen(() => fFonte.LiveIndex == FonteFaixas || fFiltro.Live || fFonte.LiveIndex == FonteModelo || fFonte.LiveIndex == FonteTabela || fFonte.LiveIndex == FonteFormula);
            var fVazio = d2.Check("vazio", "Só onde o parâmetro está vazio", false, "Não muda o que já tem valor.");
            var fAbaixo = d2.Check("abaixo", "Sem nível: usar o nível logo abaixo do elemento", true,
                "Para elementos que não guardam nível (alguns modelos genéricos, peças em vista): o nível mais alto abaixo da base deles.");
            var fLado = d2.Choice("ladoPorta", "Portas e janelas: ambiente", new List<string> { "Lado interno (para onde abre)", "O que não é circulação", "Lado externo (de onde vem)" }, 0,
                "Lado interno: o ambiente para onde a porta abre (Para o ambiente do Revit). “Não é circulação”: entre os dois lados, o que não se chama circulação, hall, corredor, escada…. Com um lado só (porta para fora, janela), esse lado.");
            fLado.VisibleWhen(() => fFonte.LiveIndex == FonteAmbiente || fFonte.LiveIndex == FonteNumero || fFonte.LiveIndex == FonteModelo || fFonte.LiveIndex == FonteTabela || fFonte.LiveIndex == FonteFormula);
            var fPrevia = d2.Check("previa", "Mostrar a prévia antes de gravar", true, "Quantos mudam, os valores novos e alguns exemplos (antes → depois).");
            var fSelecionar = d2.Check("selecionar", "Selecionar os elementos alterados no fim", true);
            d2.Note("Elementos em grupos só mudam se o parâmetro pode variar entre grupos (os criados aqui podem). Pode ser desfeito com Ctrl+Z.");
            if (!d2.Show(Owner)) return Result.Cancelled;

            // ---- what to do
            vals.NivelAbaixo = fAbaixo.Value;
            vals.ReferenciaElevacao = Math.Max(0, fRef.Index);
            vals.LadoPorta = Math.Max(0, fLado.Index);
            int fonteIdx = Math.Max(0, fFonte.Index);
            bool criar = fDestino.Index <= 0;
            bool doTipo = !criar && fDestino.Value.EndsWith(MarcaTipo, StringComparison.Ordinal);
            string destino = criar ? (fNovo.Value ?? "").Trim() : doTipo ? fDestino.Value.Substring(0, fDestino.Value.Length - MarcaTipo.Length) : fDestino.Value;
            if (string.IsNullOrWhiteSpace(destino)) { Show("Dê um nome ao novo parâmetro.", ResultKind.Info); return Result.Cancelled; }

            List<(double de, double ate, string valor)> faixas = null;
            Dictionary<string, string> tabela = null;
            string restoTabela = null;
            if (fonteIdx == FonteFaixas && (faixas = ValoresElemento.LerFaixas(fFaixas.Value, out string erroFaixa)) == null)
            { Show("Faixas de elevação: " + erroFaixa, ResultKind.Info); return Result.Cancelled; }
            if (fonteIdx == FonteTabela && (tabela = ValoresElemento.LerTabela(fTabela.Value, out restoTabela, out string erroTabela)) == null)
            { Show("Tabela de/para: " + erroTabela, ResultKind.Info); return Result.Cancelled; }
            if (fonteIdx == FonteSubstituir && string.IsNullOrEmpty(fProcurar.Value)) { Show("Escreva o trecho a procurar.", ResultKind.Info); return Result.Cancelled; }
            if (fFiltro.Value && fAte.Value <= fDe.Value) { Show("No filtro de elevação, o “Até” precisa ser maior que o “De”.", ResultKind.Info); return Result.Cancelled; }
            Regex procurar = fonteIdx == FonteSubstituir ? new Regex(Regex.Escape(fProcurar.Value), RegexOptions.IgnoreCase) : null;
            Formula.Expr formula = null;
            if (fonteIdx == FonteFormula)
                try { formula = Formula.Compilar(fFormula.Value); }
                catch (Formula.Erro ex) { Show("Fórmula: " + ex.Message, ResultKind.Info); return Result.Cancelled; }

            // ---- numbering {Seq}: in the order chosen, restarting at each value of the "recomeçar" field
            bool comSeq = (fonteIdx == FonteModelo && TextUtil.Fold(fModelo.Value ?? "").Contains("{seq")) ||
                          (fonteIdx == FonteFormula && TextUtil.Fold(fFormula.Value ?? "").Contains("{seq"));
            if (comSeq)
            {
                var numerar = alvo.Where(e => !fFiltro.Value || (vals.Elevacao(e) is double z && ValoresElemento.Dentro(z, fDe.Value, fAte.Value))).ToList();
                numerar = Ordenar(numerar, Math.Max(0, fOrdem.Index), vals, destino, doTipo);
                vals.Sequencia = new Dictionary<long, int>();
                vals.SeqDigitos = (int)Math.Round(fDigitos.Value);
                var contadores = new Dictionary<string, int>();
                string recomeca = (fRecomeca.Value ?? "").Trim();
                foreach (Element e in numerar)
                {
                    string chave = recomeca.Length == 0 ? "" : TextUtil.Fold(vals.Expandir(recomeca, e) ?? "");
                    int n = contadores.TryGetValue(chave, out int atual) ? atual + 1 : (int)Math.Round(fInicio.Value);
                    contadores[chave] = n;
                    vals.Sequencia[e.Id.Value] = n;
                }
            }

            // ---- 1. the changes (nothing is written yet)
            int iguais = 0, semValor = 0, semParam = 0, soLeitura = 0, emGrupos = 0, jaTinham = 0, foraFaixa = 0, conflitos = 0;
            var mudancas = new List<Mudanca>();
            var porTipo = new Dictionary<long, (Element tipo, Element exemplo, HashSet<string> valores, int n)>();
            var brutoPorTipo = new Dictionary<long, object>();
            for (int i = 0; i < alvo.Count; i++)
            {
                Element e = alvo[i];
                if (i % 100 == 0) ToolProgress.Report(i, alvo.Count, "Lendo: " + (e.Category?.Name ?? ""));
                double? zm = fFiltro.Value || faixas != null ? vals.Elevacao(e) : null;
                if (fFiltro.Value && (zm == null || !ValoresElemento.Dentro(zm.Value, fDe.Value, fAte.Value))) { foraFaixa++; continue; }

                Element dono = e;
                if (doTipo)
                {
                    dono = Doc.GetElement(e.GetTypeId());
                    if (dono == null) { semParam++; continue; }
                }
                Parameter p = dono.LookupParameter(destino);
                if (p == null && !criar) { semParam++; continue; }
                if (p != null && (p.IsReadOnly || p.StorageType == StorageType.ElementId || p.StorageType == StorageType.None)) { soLeitura++; continue; }
                if (!doTipo && p != null && e.GroupId != ElementId.InvalidElementId && !(p.Definition is InternalDefinition idf && idf.VariesAcrossGroups)) { emGrupos++; continue; }
                string antes = ValoresElemento.Texto(p) ?? "";
                if (fVazio.Value && antes.Length > 0) { jaTinham++; continue; }

                string v;
                object bruto = null;
                switch (fonteIdx)
                {
                    case FonteNivel: v = vals.Nivel(e)?.Name; break;
                    case FonteAmbiente: v = vals.Ambiente(e)?.Name; break;
                    case FonteNumero: v = vals.Ambiente(e)?.Number; break;
                    case FonteModelo: v = vals.Expandir(fModelo.Value, e); break;
                    case FonteFaixas:
                        v = zm == null ? null : faixas.Where(f => ValoresElemento.Dentro(zm.Value, f.de, f.ate)).Select(f => f.valor).FirstOrDefault();
                        if (v == null) { foraFaixa++; continue; }
                        break;
                    case FonteTabela:
                        string chave = vals.Expandir(fChave.Value, e);
                        v = chave != null && tabela.TryGetValue(TextUtil.Fold(chave).Trim(), out string novo) ? novo : restoTabela;
                        break;
                    case FonteSubstituir:
                        v = antes.Length > 0 && procurar.IsMatch(antes) ? procurar.Replace(antes, (fTrocar.Value ?? "").Replace("$", "$$")) : null;
                        break;
                    case FonteFormula:
                        bruto = formula.Avaliar(n => vals.Valor(n, e));
                        v = bruto == null ? null : Formula.ParaTexto(bruto);
                        break;
                    default: v = fTexto.Value ?? ""; break;
                }
                if (v == null || (v.Length == 0 && fonteIdx != FonteTexto && fonteIdx != FonteSubstituir)) { semValor++; continue; }
                if (bruto is string) bruto = null;     // texts go through Letras and Gravar as texts
                if (bruto == null) v = Letras(v, fLetras.Index);
                if (p != null ? Igual(p, v, bruto) : v.Length == 0) { iguais++; continue; }

                if (doTipo)
                {
                    long k = dono.Id.Value;
                    if (!porTipo.TryGetValue(k, out var tt)) tt = (dono, e, new HashSet<string>(), 0);
                    if (bruto != null) brutoPorTipo[k] = bruto;
                    tt.valores.Add(v);
                    porTipo[k] = (tt.tipo, tt.exemplo, tt.valores, tt.n + 1);
                }
                else mudancas.Add(new Mudanca { Alvo = e, Exemplo = e, Antes = antes, Depois = v, Bruto = bruto });
            }
            if (doTipo)
                foreach (var tt in porTipo.Values)
                {
                    if (tt.valores.Count > 1) { conflitos++; continue; }
                    mudancas.Add(new Mudanca { Alvo = tt.tipo, Exemplo = tt.exemplo, Instancias = tt.n, Antes = ValoresElemento.Texto(tt.tipo.LookupParameter(destino)) ?? "", Depois = tt.valores.First(),
                                               Bruto = brutoPorTipo.TryGetValue(tt.tipo.Id.Value, out object bt) ? bt : null });
                }
            ToolProgress.Hide();

            string Fora()
            {
                var sb = new StringBuilder();
                if (iguais > 0) sb.Append($"\n{iguais} já tinham esse valor.");
                if (jaTinham > 0) sb.Append($"\n{jaTinham} já tinham valor (mantido).");
                if (semValor > 0) sb.Append($"\n{semValor} sem " + (fonteIdx == FonteNivel ? "nível" : fonteIdx == FonteAmbiente || fonteIdx == FonteNumero ? "ambiente"
                    : fonteIdx == FonteTabela ? "linha na tabela" : fonteIdx == FonteSubstituir ? "o trecho procurado" : "valor no modelo") + " (não mudam).");
                if (foraFaixa > 0) sb.Append($"\n{foraFaixa} fora " + (faixas != null ? "das faixas de elevação" : "das elevações do filtro") + " (não mudam).");
                if (semParam > 0) sb.Append($"\n{semParam} não têm o parâmetro “{destino}”.");
                if (soLeitura > 0) sb.Append($"\n{soLeitura} com o parâmetro só de leitura.");
                if (emGrupos > 0) sb.Append($"\n{emGrupos} em grupos (o parâmetro não pode variar entre grupos).");
                if (conflitos > 0) sb.Append($"\n{conflitos} tipo(s) pulados: as instâncias pedem valores diferentes (preencha na instância).");
                return sb.ToString();
            }

            if (mudancas.Count == 0) { Show($"Nada a mudar em “{destino}”." + Fora(), ResultKind.Info); return Result.Cancelled; }

            // ---- 2. the preview
            if (fPrevia.Value)
            {
                var conf = new OptionsDialog(Title, "PreencherParametros.Previa", "Gravar");
                int inst = mudancas.Sum(m => m.Instancias);
                conf.Note(doTipo ? $"“{destino}” vai mudar em {mudancas.Count} tipo(s) ({inst} instância(s))." : $"“{destino}” vai mudar em {mudancas.Count} elemento(s).");
                var valores = mudancas.GroupBy(m => m.Depois).OrderByDescending(g => g.Sum(m => m.Instancias)).ToList();
                conf.Note("Valores novos:\n" + string.Join("\n", valores.Take(12).Select(g => $"• “{Curto(g.Key)}” — {g.Sum(m => m.Instancias)}"))
                          + (valores.Count > 12 ? $"\n• … mais {valores.Count - 12} valor(es)" : ""));
                conf.Note("Exemplos:\n" + string.Join("\n", mudancas.Take(6).Select(m => $"• {Rotulo(m)}: “{Curto(m.Antes)}” → “{Curto(m.Depois)}”")));
                string fora = Fora();
                if (fora.Length > 0) conf.Note("Não mudam:" + fora);
                if (!conf.Show(Owner)) return Result.Cancelled;
            }

            // ---- 3. writing
            int feitos = 0, falhas = 0;
            var exemplosFalha = new List<string>();
            var alterados = new List<ElementId>();
            using (var t = new Transaction(Doc, "JA Tools - " + Title))
            {
                t.Start();
                RevitDwgUnifier.Export.FailureSwallower.Attach(t);
                if (criar)
                {
                    Guid? guid = string.Equals(destino, ProjectParams.JaNivelNome, StringComparison.OrdinalIgnoreCase) ? ProjectParams.JaNivel : (Guid?)null;
                    var categoriasAlvo = alvo.Select(e => e.Category).GroupBy(c => c.Id.Value).Select(g => g.First()).ToList();
                    if (!ProjectParams.GarantirTexto(Doc, destino, guid, categoriasAlvo, guid.HasValue ? ProjectParams.JaNivelDescricao : "Criado pelo JA (Preencher Parâmetros).", out string erroCriar))
                    {
                        t.RollBack();
                        Show("Não foi possível criar o parâmetro “" + destino + "”: " + erroCriar + ".", ResultKind.Warning);
                        return Result.Failed;
                    }
                }
                for (int i = 0; i < mudancas.Count; i++)
                {
                    Mudanca m = mudancas[i];
                    if (i % 50 == 0) ToolProgress.Report(i, mudancas.Count, "Gravando: " + (m.Exemplo.Category?.Name ?? ""));
                    Parameter p = m.Alvo.LookupParameter(destino);
                    if (p == null || p.IsReadOnly) { falhas++; continue; }
                    if (!doTipo && m.Alvo.GroupId != ElementId.InvalidElementId && !(p.Definition is InternalDefinition idf && idf.VariesAcrossGroups)) { emGrupos++; continue; }
                    try
                    {
                        if (m.Bruto != null ? GravarValor(p, m.Bruto, m.Depois) : Gravar(p, m.Depois)) { feitos += m.Instancias; alterados.Add(m.Exemplo.Id); if (doTipo) alterados.AddRange(alvo.Where(x => x.GetTypeId() == m.Alvo.Id).Select(x => x.Id)); }
                        else { falhas++; if (exemplosFalha.Count < 5) exemplosFalha.Add($"“{Curto(m.Depois)}” não serve para {destino} ({m.Exemplo.Category?.Name})"); }
                    }
                    catch (Exception ex) { falhas++; if (exemplosFalha.Count < 5) exemplosFalha.Add(ex.Message); }
                }
                if (t.Commit() != TransactionStatus.Committed)
                {
                    Show("O Revit recusou as mudanças: nada foi alterado.", ResultKind.Warning);
                    return Result.Failed;
                }
            }
            ToolProgress.End();
            if (fSelecionar.Value && alterados.Count > 0) try { UiDoc.Selection.SetElementIds(alterados.Distinct().ToList()); } catch { }

            var res = new StringBuilder($"{feitos} elemento(s) com “{destino}” preenchido" + (doTipo ? $" ({mudancas.Count} tipo(s))." : "."));
            if (criar) res.Append($"\nO parâmetro “{destino}” está nas categorias escolhidas (Dados de identidade).");
            res.Append(Fora());
            if (falhas > 0) res.Append($"\n{falhas} não aceitaram o valor" + (exemplosFalha.Count > 0 ? ":\n• " + string.Join("\n• ", exemplosFalha.Distinct()) : "."));
            Show(res.ToString(), falhas > 0 ? ResultKind.Warning : feitos > 0 ? ResultKind.Success : ResultKind.Info);
            return Result.Succeeded;
        }

        private static string Letras(string v, int modo)
        {
            if (string.IsNullOrEmpty(v)) return v;
            switch (modo)
            {
                case 1: return v.ToUpper(ValoresElemento.Br);
                case 2: return v.ToLower(ValoresElemento.Br);
                case 3: return char.ToUpper(v[0], ValoresElemento.Br) + v.Substring(1);
                default: return v;
            }
        }

        private static string Curto(string s) => string.IsNullOrEmpty(s) ? "" : s.Length > 60 ? s.Substring(0, 57) + "…" : s;

        private string Rotulo(Mudanca m)
        {
            Element e = m.Exemplo;
            string marca = ValoresElemento.Texto(e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK));
            string tipo = Doc.GetElement(e.GetTypeId())?.Name;
            return (e.Category?.Name ?? "") + " " + (!string.IsNullOrEmpty(marca) ? marca : (e is Autodesk.Revit.DB.Architecture.Room r ? r.Number + " " + r.Name : (tipo ?? e.Id.Value.ToString(CultureInfo.InvariantCulture))))
                   + (m.Instancias > 1 ? $" (tipo, {m.Instancias} inst.)" : "");
        }

        private static bool Igual(Parameter p, string v, object bruto = null)
        {
            try
            {
                if (bruto is double d && p.StorageType != StorageType.String)
                    return ValoresElemento.ValorDe(p) is double atual && Math.Abs(atual - d) < 1e-6;
                if (p.StorageType == StorageType.String) return (p.AsString() ?? "") == v;
                return p.HasValue && string.Equals(p.AsValueString(), v, StringComparison.CurrentCultureIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>A formula's typed result: numbers into number parameters by their kind (lengths given in m, areas in m²,
        /// volumes in m³, angles in degrees), true/false into yes/no; anything else as text.</summary>
        private static bool GravarValor(Parameter p, object bruto, string texto)
        {
            try
            {
                if (p.StorageType == StorageType.Double && Formula.ParaNumero(bruto) is double x)
                {
                    ForgeTypeId spec = p.Definition.GetDataType();
                    if (spec == SpecTypeId.Length) return p.Set(UnitUtils.ConvertToInternalUnits(x, UnitTypeId.Meters));
                    if (spec == SpecTypeId.Area) return p.Set(UnitUtils.ConvertToInternalUnits(x, UnitTypeId.SquareMeters));
                    if (spec == SpecTypeId.Volume) return p.Set(UnitUtils.ConvertToInternalUnits(x, UnitTypeId.CubicMeters));
                    if (spec == SpecTypeId.Angle) return p.Set(UnitUtils.ConvertToInternalUnits(x, UnitTypeId.Degrees));
                    if (spec == SpecTypeId.Number) return p.Set(x);
                    return Gravar(p, texto);
                }
                if (p.StorageType == StorageType.Integer && (bruto is bool || bruto is double))
                    return p.Set(bruto is bool b ? (b ? 1 : 0) : (int)Math.Round((double)bruto));
            }
            catch { return false; }
            return Gravar(p, texto);
        }

        /// <summary>The elements in the order of the numbering.</summary>
        private List<Element> Ordenar(List<Element> l, int ordem, ValoresElemento vals, string destino, bool doTipo)
        {
            if (ordem == 3) return l;
            if (ordem == 2)
                return l.OrderBy(e => ValoresElemento.Texto((doTipo ? Doc.GetElement(e.GetTypeId()) : e)?.LookupParameter(destino)) ?? "", new NaturalStringComparer()).ToList();
            XYZ Ponto(Element e)
            {
                try
                {
                    if (e.Location is LocationPoint lp) return lp.Point;
                    if (e.Location is LocationCurve lc) return lc.Curve.Evaluate(0.5, true);
                    BoundingBoxXYZ bb = e.get_BoundingBox(null);
                    if (bb != null) return (bb.Min + bb.Max) / 2;
                }
                catch { }
                return XYZ.Zero;
            }
            double Faixa(double v) => Math.Round(v / (0.3 / 0.3048));      // rows/columns of 30 cm
            var comPonto = l.Select(e => (e, p: Ponto(e), nivel: vals.Nivel(e)?.Elevation ?? 0)).ToList();
            var ord = comPonto.OrderBy(x => x.nivel);
            ord = ordem == 0 ? ord.ThenByDescending(x => Faixa(x.p.Y)).ThenBy(x => x.p.X) : ord.ThenBy(x => Faixa(x.p.X)).ThenByDescending(x => x.p.Y);
            return ord.Select(x => x.e).ToList();
        }

        /// <summary>Text as it is; numbers in the project units ("2,50", "250 cm"), integers and yes/no as numbers or Sim/Não.</summary>
        private static bool Gravar(Parameter p, string v)
        {
            switch (p.StorageType)
            {
                case StorageType.String:
                    return p.Set(v);
                case StorageType.Integer:
                    string f = TextUtil.Fold(v).Trim();
                    if (f == "sim" || f == "yes" || f == "verdadeiro") return p.Set(1);
                    if (f == "nao" || f == "no" || f == "falso") return p.Set(0);
                    if (int.TryParse(v.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) return p.Set(n);
                    try { return p.SetValueString(v); } catch { return false; }
                case StorageType.Double:
                    try { if (p.SetValueString(v)) return true; } catch { }
                    double? x = ValoresElemento.Numero(v);
                    if (x != null && p.Definition.GetDataType() == SpecTypeId.Number) return p.Set(x.Value);
                    return false;
            }
            return false;
        }
    }
}
