using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDwgUnifier.Tools.Common;
using RevitDwgUnifier.Tools.Exporting;

namespace RevitDwgUnifier.Tools.Parameters
{
    /// <summary>
    /// Filtrar Elementos (like Archicad's Find &amp; Select): the elements of the chosen categories — in the selection, the
    /// active view or the whole project — that meet up to five rules. A rule is a field (level, room, elevation, length,
    /// area, material, phase, workset, host… or any parameter of the element or of its type, or of its room / level /
    /// type / host), a comparison (16 of them, wildcards and lists included) and a value; or a formula that must be true.
    /// Rules combine with E / OU. Then: select (add, take away, the ones that do NOT meet), show (zoom), isolate or hide in
    /// the view (temporary), highlight with a colour (a selection set as a view filter), or export the list to Excel.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class FiltrarElementosCommand : ToolCommand
    {
        public override string Title => "Filtrar Elementos";

        /// <summary>Labels of the special fields (with the units of their numbers) → the field name.</summary>
        private static readonly (string rotulo, string campo)[] Especiais =
            ValoresElemento.Especiais.Select(n => (Unidade(n) is string u ? $"{n} ({u})" : n, n)).ToArray();

        private static string Unidade(string campo)
        {
            switch (TextUtil.Fold(campo))
            {
                case "elevacao": case "elevacao do topo": case "comprimento": case "altura": case "largura": case "espessura": return "m";
                case "area": return "m²";
                case "volume": return "m³";
                default: return null;
            }
        }

        private const string RotuloFormula = "Fórmula (verdadeira)";

        private static readonly List<string> Operadores = new List<string>
        {
            "é igual a", "não é igual a", "contém", "não contém", "começa com", "termina com", "está vazio", "não está vazio",
            "maior que", "maior ou igual a", "menor que", "menor ou igual a", "entre", "é um de", "não é um de", "parece com (* e ?)"
        };
        private const int OIgual = 0, ODiferente = 1, OContem = 2, ONaoContem = 3, OComeca = 4, OTermina = 5, OVazio = 6, ONaoVazio = 7,
                          OMaior = 8, OMaiorIgual = 9, OMenor = 10, OMenorIgual = 11, OEntre = 12, OUmDe = 13, ONaoUmDe = 14, OCuringa = 15;

        private static readonly (string nome, Color cor)[] Cores =
        {
            ("Vermelho", new Color(220, 40, 40)), ("Laranja", new Color(240, 140, 20)), ("Amarelo", new Color(235, 200, 0)),
            ("Verde", new Color(40, 160, 70)), ("Azul", new Color(30, 110, 220)), ("Roxo", new Color(140, 60, 190)), ("Cinza", new Color(150, 150, 150))
        };

        private static readonly List<string> Acoes = new List<string>
        {
            "Selecionar", "Somar à seleção atual", "Tirar da seleção atual", "Selecionar os que NÃO atendem", "Selecionar e mostrar (zoom)",
            "Isolar na vista (temporário)", "Ocultar na vista (temporário)", "Destacar na vista com cor (filtro)", "Exportar a lista (Excel) e selecionar"
        };
        private const int ASelecionar = 0, ASomar = 1, ATirar = 2, AInverter = 3, AMostrar = 4, AIsolar = 5, AOcultar = 6, ADestacar = 7, AExportar = 8;

        private sealed class Regra
        {
            public string Rotulo, Campo;      // Campo null = formula
            public bool Especial;
            public int Op;
            public string Valor;
            public double? Num, De, Ate;
            public HashSet<string> Lista;
            public Regex Curinga;
            public Formula.Expr Expr;
        }

        private const int MaxRegras = 5;

        protected override Result Run()
        {
            var selAtual = UiDoc.Selection.GetElementIds().ToList();
            var sel = selAtual.Select(id => Doc.GetElement(id)).Where(ValoresElemento.Alvo).ToList();
            ToolProgress.Pulse("Lendo as categorias…");
            List<Element> projeto = new FilteredElementCollector(Doc).WhereElementIsNotElementType().Where(ValoresElemento.Alvo).ToList();
            View vista = Doc.ActiveView;
            var naVista = new FilteredElementCollector(Doc, vista.Id).WhereElementIsNotElementType().Where(ValoresElemento.Alvo).ToList();
            ToolProgress.Hide();
            if (projeto.Count == 0) { Show("Nenhum elemento de modelo no projeto.", ResultKind.Info); return Result.Cancelled; }

            int escopoPadrao = sel.Count > 0 ? 0 : naVista.Count > 0 ? 1 : 2;
            var basePraCategorias = escopoPadrao == 0 ? sel : escopoPadrao == 1 ? naVista : projeto;
            var categorias = projeto.GroupBy(e => e.Category.Name).OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase).ToList();
            var contagem = basePraCategorias.GroupBy(e => e.Category.Name).ToDictionary(g => g.Key, g => g.Count());

            // ---- where
            var d1 = new OptionsDialog(Title, "FiltrarElementos.Onde", "Continuar");
            var fEscopo = d1.Choice("escopo", "Procurar em", new List<string> { $"Seleção atual ({sel.Count})", $"Vista ativa ({naVista.Count})", "Todo o projeto" }, escopoPadrao);
            if (fEscopo.Index == 0 && sel.Count == 0) fEscopo.Index = escopoPadrao;
            var fCat = d1.Multi("categorias", "Categorias", categorias.Select(g => $"{g.Key} ({(contagem.TryGetValue(g.Key, out int qt) ? qt : 0)})").ToList(), true, true,
                "Contagem " + (escopoPadrao == 0 ? "na seleção" : escopoPadrao == 1 ? "na vista ativa" : "no projeto") + ".");
            if (!d1.Show(Owner)) return Result.Cancelled;
            var cats = new HashSet<string>(fCat.SelectedIndices.Select(i => categorias[i].Key));
            IEnumerable<Element> fonte = fEscopo.Index == 0 ? sel : fEscopo.Index == 1 ? naVista : projeto;
            List<Element> alvo = fonte.Where(e => cats.Contains(e.Category.Name)).ToList();
            if (alvo.Count == 0) { Show("Nenhum elemento nessas categorias " + (fEscopo.Index == 0 ? "na seleção." : fEscopo.Index == 1 ? "na vista ativa." : "no projeto."), ResultKind.Info); return Result.Cancelled; }

            // ---- the fields
            var vals = new ValoresElemento(Doc);
            ToolProgress.Pulse("Lendo os parâmetros…");
            var especiais = new HashSet<string>(ValoresElemento.Especiais.Select(x => TextUtil.Fold(x)));
            var nomes = vals.NomesDeParametros(alvo, false, false).Concat(vals.NomesDeParametros(alvo, true, false))
                .Distinct(StringComparer.CurrentCultureIgnoreCase).Where(x => !especiais.Contains(TextUtil.Fold(x)))
                .OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase).ToList();
            ToolProgress.Hide();
            // label → (field, special?)
            var campos = new List<(string rotulo, string campo, bool especial)> { (RotuloFormula, null, false) };
            campos.AddRange(Especiais.Select(x => (x.rotulo, x.campo, true)));
            campos.AddRange(nomes.Select(n => (n, n, false)));
            var rotulos = campos.Select(c => c.rotulo).ToList();
            var rotulosOpc = new List<string> { "(nenhuma)" }.Concat(rotulos).ToList();
            int iNivel = rotulos.IndexOf("Nível");

            // ---- the rules
            var d2 = new OptionsDialog(Title, "FiltrarElementos", "Filtrar");
            d2.Note($"{alvo.Count} elemento(s) de {cats.Count} categoria(s). Maiúsculas e acentos não importam; números como o Revit mostra (2,50; 80).");
            var fCampo = new OptionsDialog.ChoiceField[MaxRegras];
            var fOp = new OptionsDialog.ChoiceField[MaxRegras];
            var fValor = new OptionsDialog.TextField[MaxRegras];
            for (int k = 0; k < MaxRegras; k++)
            {
                int kk = k;
                if (k == 0) d2.Section("Regras");
                fCampo[k] = k == 0
                    ? d2.Choice("campo1", "Regra 1: campo", rotulos, Math.Max(0, iNivel), "Fórmula: uma condição livre, ex.: E({Altura} > 2,1; CONTÉM({Tipo}; \"porta\")). Ambiente:X, Tipo:X, Nível:X e Hospedeiro:X pegam um parâmetro desses.")
                    : d2.Choice("campo" + (k + 1), $"Regra {k + 1}: campo", rotulosOpc, 0);
                fOp[k] = d2.Choice("op" + (k + 1), $"Regra {k + 1}: condição", Operadores, 0);
                fValor[k] = d2.Text("valor" + (k + 1), $"Regra {k + 1}: valor", "",
                    k == 0 ? "Entre: “10 a 13”. É um de: “a; b; c”. Parece com: * = qualquer trecho, ? = uma letra (ex.: P?01*)." : null);
                if (k > 0) fCampo[k].VisibleWhen(() => Ativa(fCampo, kk - 1));
                fOp[k].VisibleWhen(() => Ativa(fCampo, kk) && !EhFormula(fCampo, kk, rotulos));
                fValor[k].VisibleWhen(() => Ativa(fCampo, kk) && (EhFormula(fCampo, kk, rotulos) || (fOp[kk].LiveIndex != OVazio && fOp[kk].LiveIndex != ONaoVazio)));
            }
            var fJunta = d2.Choice("junta", "Combinar", new List<string> { "Todas as regras (E)", "Qualquer regra (OU)" }, 0);
            fJunta.VisibleWhen(() => Ativa(fCampo, 1));

            d2.Section("Resultado");
            var fAcao = d2.Choice("acao", "Fazer", Acoes, 0,
                "Isolar/Ocultar: modo temporário da vista (o óculos na barra de baixo desfaz). Destacar: um conjunto de seleção com o nome abaixo aplicado como filtro da vista ativa, na cor escolhida.");
            var fCor = d2.Choice("cor", "Cor", Cores.Select(c => c.nome).ToList(), 0);
            fCor.VisibleWhen(fAcao, ADestacar);
            var fSalvar = d2.Check("salvar", "Salvar como conjunto de seleção", false, "Fica no projeto (Gerenciar › Seleção › Carregar) e pode virar filtro de vista.");
            fSalvar.VisibleWhen(() => fAcao.LiveIndex != ADestacar);
            var fNome = d2.Text("nome", "Nome do conjunto", "JA Filtro");
            fNome.VisibleWhen(() => fAcao.LiveIndex == ADestacar || fSalvar.Live);

            d2.Section("Opções");
            bool Usa(string campo) => Enumerable.Range(0, MaxRegras).Any(k => Ativa(fCampo, k) && CampoDe(fCampo, k, rotulos, campos) is string c && TextUtil.Fold(c) == campo);
            var fRef = d2.Choice("referencia", "Elevação do elemento", new List<string> { "Nível + deslocamento da base", "Ponto mais baixo", "Centro", "Ponto mais alto" }, 0,
                "Elevação entre “10 a 13” inclui o 10 e não o 13 (como as faixas do Preencher Parâmetros).");
            fRef.VisibleWhen(() => Usa("elevacao") || EhFormula(fCampo, 0, rotulos));
            var fLado = d2.Choice("ladoPorta", "Portas e janelas: ambiente", new List<string> { "Lado interno (para onde abre)", "O que não é circulação", "Lado externo (de onde vem)" }, 0);
            fLado.VisibleWhen(() => Usa("ambiente") || Usa("numero do ambiente") || EhFormula(fCampo, 0, rotulos));
            var fAbaixo = d2.Check("abaixo", "Sem nível: usar o nível logo abaixo do elemento", true);
            fAbaixo.VisibleWhen(() => Usa("nivel"));
            d2.Note("Funções das fórmulas — " + Formula.Ajuda);
            if (!d2.Show(Owner)) return Result.Cancelled;

            vals.ReferenciaElevacao = Math.Max(0, fRef.Index);
            vals.LadoPorta = Math.Max(0, fLado.Index);
            vals.NivelAbaixo = fAbaixo.Value;

            // ---- the rules, read
            var regras = new List<Regra>();
            for (int k = 0; k < MaxRegras; k++)
            {
                bool ativa = k == 0 || Enumerable.Range(1, k).All(j => fCampo[j].Index > 0);
                if (!ativa) break;
                int ic = k == 0 ? fCampo[k].Index : fCampo[k].Index - 1;
                if (ic < 0 || ic >= campos.Count) break;
                var (rotulo, campo, especial) = campos[ic];
                var r = new Regra { Rotulo = rotulo, Campo = campo, Especial = especial, Op = Math.Max(0, fOp[k].Index), Valor = fValor[k].Value ?? "" };
                try { if (!Montar(r, out string erro)) { Show($"Regra {k + 1}: {erro}", ResultKind.Info); return Result.Cancelled; } }
                catch (Formula.Erro ex) { Show($"Regra {k + 1}, fórmula: {ex.Message}", ResultKind.Info); return Result.Cancelled; }
                regras.Add(r);
            }
            bool todas = fJunta.Index != 1 || regras.Count == 1;

            // ---- filter
            var achados = new List<Element>();
            int errosFormula = 0;
            for (int i = 0; i < alvo.Count; i++)
            {
                if (i % 200 == 0) ToolProgress.Report(i, alvo.Count, "Filtrando…");
                Element e = alvo[i];
                bool ok = todas;
                foreach (Regra r in regras)
                {
                    bool atende;
                    try { atende = Atende(r, e, vals); }
                    catch { atende = false; errosFormula++; }
                    if (todas && !atende) { ok = false; break; }
                    if (!todas && atende) { ok = true; break; }
                }
                if (ok) achados.Add(e);
            }
            ToolProgress.Hide();

            int acao = Math.Max(0, fAcao.Index);
            var ids = (acao == AInverter ? alvo.Except(achados) : achados).Select(e => e.Id).ToList();
            string descricao = string.Join(todas ? " E " : " OU ", regras.Select(Descrever));
            if (ids.Count == 0)
            {
                Show(acao == AInverter ? $"Todos os {alvo.Count} elemento(s) atendem a: {descricao}." : $"Nenhum dos {alvo.Count} elemento(s) atende a: {descricao}.", ResultKind.Info);
                return Result.Cancelled;
            }

            // ---- do
            string feito;
            string caminho = null;
            string nome = string.IsNullOrWhiteSpace(fNome.Value) ? "JA Filtro" : fNome.Value.Trim();
            try
            {
                switch (acao)
                {
                    case ASomar: UiDoc.Selection.SetElementIds(selAtual.Union(ids).ToList()); feito = "somados à seleção atual"; break;
                    case ATirar:
                        {
                            var tirar = new HashSet<long>(ids.Select(x => x.Value));
                            UiDoc.Selection.SetElementIds(selAtual.Where(x => !tirar.Contains(x.Value)).ToList());
                            feito = "tirados da seleção atual";
                            break;
                        }
                    case AMostrar:
                        UiDoc.Selection.SetElementIds(ids);
                        try { UiDoc.ShowElements(ids); } catch { }
                        feito = "selecionados e mostrados";
                        break;
                    case AIsolar:
                    case AOcultar:
                        {
                            var visiveis = new HashSet<long>(naVista.Select(x => x.Id.Value));
                            var naV = ids.Where(x => visiveis.Contains(x.Value)).ToList();
                            if (naV.Count == 0) { Show("Nenhum dos elementos encontrados está na vista ativa.", ResultKind.Info); return Result.Cancelled; }
                            if (!vista.CanUseTemporaryVisibilityModes()) { Show("Esta vista não aceita isolar/ocultar temporário.", ResultKind.Info); return Result.Cancelled; }
                            using (var t = new Transaction(Doc, "JA Tools - " + Title))
                            {
                                t.Start();
                                if (acao == AIsolar) vista.IsolateElementsTemporary(naV); else vista.HideElementsTemporary(naV);
                                t.Commit();
                            }
                            feito = (acao == AIsolar ? "isolados" : "ocultados") + $" na vista ({naV.Count} nela; para voltar: óculos › Redefinir ocultar/isolar temporário)";
                            break;
                        }
                    case ADestacar:
                        using (var t = new Transaction(Doc, "JA Tools - " + Title))
                        {
                            t.Start();
                            SelectionFilterElement sf = Conjunto(nome, ids);
                            int ic = Math.Max(0, Math.Min(Cores.Length - 1, fCor.Index));
                            string aviso = Destacar(vista, sf, Cores[ic].cor);
                            if (t.Commit() != TransactionStatus.Committed) { Show("O Revit recusou o filtro: nada foi alterado.", ResultKind.Warning); return Result.Failed; }
                            feito = aviso ?? $"destacados em {Cores[ic].nome.ToLowerInvariant()} na vista pelo filtro “{nome}” (Visibilidade/Gráficos › Filtros para mudar ou tirar)";
                        }
                        UiDoc.Selection.SetElementIds(ids);
                        break;
                    case AExportar:
                        caminho = Exportar(ids.Select(x => Doc.GetElement(x)).Where(x => x != null).ToList(), regras, vals);
                        UiDoc.Selection.SetElementIds(ids);
                        feito = "selecionados e exportados para o Excel";
                        break;
                    default:
                        UiDoc.Selection.SetElementIds(ids);
                        feito = acao == AInverter ? "selecionados (os que NÃO atendem)" : "selecionados";
                        break;
                }
                if (acao != ADestacar && fSalvar.Value)
                    using (var t = new Transaction(Doc, "JA Tools - " + Title + " (conjunto)"))
                    {
                        t.Start();
                        Conjunto(nome, ids);
                        t.Commit();
                        feito += $"; salvos no conjunto de seleção “{nome}”";
                    }
            }
            catch (Exception ex) { Show("Não foi possível concluir: " + ex.Message, ResultKind.Warning); return Result.Failed; }

            var lista = ids.Select(x => Doc.GetElement(x)).Where(x => x?.Category != null).ToList();
            var sb = new StringBuilder(acao == AInverter
                ? $"{lista.Count} de {alvo.Count} elemento(s) NÃO atendem a: {descricao}.\nForam {feito}."
                : $"{lista.Count} de {alvo.Count} elemento(s) atendem a: {descricao}.\nForam {feito}.");
            var porCat = lista.GroupBy(e => e.Category.Name).OrderByDescending(g => g.Count()).ToList();
            sb.Append("\n\n" + string.Join("\n", porCat.Take(12).Select(g => $"• {g.Key}: {g.Count()}")));
            if (porCat.Count > 12) sb.Append($"\n• … mais {porCat.Count - 12} categoria(s)");
            if (errosFormula > 0) sb.Append($"\n\n{errosFormula} avaliação(ões) da fórmula deram erro (contadas como não atendem).");
            if (caminho != null) sb.Append("\n\n" + caminho);
            else sb.Append("\n\nPara preencher um parâmetro neles: Preencher Parâmetros › Selecionados.");
            if (caminho != null) Show(sb.ToString(), ResultKind.Success, OpenFolderAction(Path.GetDirectoryName(caminho)));
            else Show(sb.ToString(), ResultKind.Success);
            return Result.Succeeded;
        }

        // ------------------------------------------------------------------ dialog helpers

        private static bool Ativa(OptionsDialog.ChoiceField[] campos, int k)
        {
            if (k == 0) return true;
            for (int j = 1; j <= k; j++) if (campos[j].LiveIndex <= 0) return false;
            return true;
        }

        private static bool EhFormula(OptionsDialog.ChoiceField[] campos, int k, List<string> rotulos)
        {
            int i = k == 0 ? campos[0].LiveIndex : campos[k].LiveIndex - 1;
            return i >= 0 && i < rotulos.Count && rotulos[i] == RotuloFormula;
        }

        private static string CampoDe(OptionsDialog.ChoiceField[] fc, int k, List<string> rotulos, List<(string rotulo, string campo, bool especial)> campos)
        {
            int i = k == 0 ? fc[0].LiveIndex : fc[k].LiveIndex - 1;
            return i >= 0 && i < campos.Count ? campos[i].campo : null;
        }

        // ------------------------------------------------------------------ rules

        private static bool Montar(Regra r, out string erro)
        {
            erro = null;
            if (r.Campo == null)
            {
                r.Expr = Formula.Compilar(r.Valor);
                return true;
            }
            r.Num = ValoresElemento.Numero(r.Valor);
            switch (r.Op)
            {
                case OEntre:
                    if (!ValoresElemento.Intervalo(r.Valor, out double de, out double ate)) { erro = $"“{r.Valor}” não é um intervalo (ex.: 10 a 13)."; return false; }
                    r.De = de; r.Ate = ate;
                    break;
                case OMaior: case OMaiorIgual: case OMenor: case OMenorIgual:
                    if (r.Num == null) { erro = $"“{r.Valor}” não é um número."; return false; }
                    break;
                case OUmDe: case ONaoUmDe:
                    r.Lista = new HashSet<string>(r.Valor.Split(';').Select(x => TextUtil.Fold(x).Trim()).Where(x => x.Length > 0));
                    if (r.Lista.Count == 0) { erro = "escreva os valores separados por ponto e vírgula (ex.: Térreo; Mezanino)."; return false; }
                    break;
                case OCuringa:
                    r.Curinga = new Regex("^" + Regex.Escape(TextUtil.Fold(r.Valor).Trim()).Replace(@"\*", ".*").Replace(@"\?", ".") + "$");
                    break;
            }
            return true;
        }

        private static string Descrever(Regra r) =>
            r.Campo == null ? $"fórmula “{r.Valor}”"
            : $"{r.Rotulo} {Operadores[r.Op]}" + (r.Op == OVazio || r.Op == ONaoVazio ? "" : $" “{r.Valor}”");

        private static readonly Regex NumericoRx = new Regex(@"^-?\d+(?:[.,]\d+)*\s*[a-z²³%°]*$");

        /// <summary>A number with at most a unit after it ("2,50", "80 cm", "12 m²").</summary>
        private static bool Numerico(string s) => NumericoRx.IsMatch(s ?? "");

        /// <summary>Does the element meet the rule (accents and case ignored; special numbers in m, parameters as Revit shows them)?</summary>
        private static bool Atende(Regra r, Element e, ValoresElemento vals)
        {
            if (r.Campo == null) return Formula.ParaBool(r.Expr.Avaliar(n => vals.Valor(n, e)));

            string texto = vals.Token(r.Campo, e);
            double? num = null;
            if (r.Especial)
            {
                object tv = vals.Valor(r.Campo, e);
                num = tv is double d ? d : tv is bool b ? (b ? 1 : 0) : ValoresElemento.Numero(texto);
                if (tv is bool) texto = (bool)tv ? "Sim" : "Não";
            }
            else num = ValoresElemento.Numero(texto);
            string a = TextUtil.Fold(texto ?? "").Trim(), b2 = TextUtil.Fold(r.Valor).Trim();
            // compared as numbers only when both read as numbers ("2,50 m" = "2,5"; "P01" and "P1" stay texts)
            bool numeros = num != null && r.Num != null && (Numerico(a) || (r.Especial && Unidade(r.Campo) != null)) && Numerico(b2);
            bool igual = a == b2 || (numeros && Math.Abs(num.Value - r.Num.Value) < 1e-6);
            switch (r.Op)
            {
                case OIgual: return igual;
                case ODiferente: return !igual;
                case OContem: return b2.Length == 0 || a.Contains(b2);
                case ONaoContem: return b2.Length == 0 || !a.Contains(b2);
                case OComeca: return a.StartsWith(b2, StringComparison.Ordinal);
                case OTermina: return a.EndsWith(b2, StringComparison.Ordinal);
                case OVazio: return a.Length == 0;
                case ONaoVazio: return a.Length > 0;
                case OMaior: return num != null && num.Value > r.Num.Value + 1e-9;
                case OMaiorIgual: return num != null && num.Value >= r.Num.Value - 1e-9;
                case OMenor: return num != null && num.Value < r.Num.Value - 1e-9;
                case OMenorIgual: return num != null && num.Value <= r.Num.Value + 1e-9;
                case OEntre:
                    if (num == null) return false;
                    return r.Especial && TextUtil.Fold(r.Campo) == "elevacao"
                        ? ValoresElemento.Dentro(num.Value, r.De.Value, r.Ate.Value)
                        : num.Value >= r.De.Value - 1e-9 && num.Value <= r.Ate.Value + 1e-9;
                case OUmDe: case ONaoUmDe:
                    {
                        bool um = r.Lista.Contains(a) || (num != null && r.Lista.Any(x => Numerico(x) && ValoresElemento.Numero(x) is double y && Math.Abs(y - num.Value) < 1e-6));
                        return r.Op == OUmDe ? um : !um;
                    }
                case OCuringa: return r.Curinga.IsMatch(a);
            }
            return false;
        }

        // ------------------------------------------------------------------ results

        private string Exportar(List<Element> elementos, List<Regra> regras, ValoresElemento vals)
        {
            var colunas = new List<(string titulo, string campo)> { ("Categoria", "Categoria"), ("Família", "Família"), ("Tipo", "Tipo"), ("Id", "Id"), ("Nível", "Nível"), ("Ambiente", "Ambiente"), ("Marca", "Marca") };
            foreach (Regra r in regras)
                if (r.Campo != null && !colunas.Any(c => TextUtil.Fold(c.campo) == TextUtil.Fold(r.Campo))) colunas.Add((r.Rotulo, r.Campo));
            var tab = new XlsxWriter.Table { Name = "Filtro", Title = "Filtrar Elementos — " + string.Join(" / ", regras.Select(Descrever)) };
            tab.Rows.Add(colunas.Select(c => c.titulo).ToArray());
            foreach (Element e in elementos)
                tab.Rows.Add(colunas.Select(c => vals.Token(c.campo, e) ?? "").ToArray());
            var xw = new XlsxWriter();
            xw.Add(tab);
            return Conferencia.Salvar(Doc, "Filtro", xw);
        }

        /// <summary>The selection set of that name with these elements (created or replaced). Inside a transaction.</summary>
        private SelectionFilterElement Conjunto(string nome, List<ElementId> ids)
        {
            SelectionFilterElement sf = new FilteredElementCollector(Doc).OfClass(typeof(SelectionFilterElement)).Cast<SelectionFilterElement>()
                .FirstOrDefault(x => string.Equals(x.Name, nome, StringComparison.CurrentCultureIgnoreCase));
            if (sf == null) sf = SelectionFilterElement.Create(Doc, nome);
            sf.SetElementIds(ids);
            return sf;
        }

        /// <summary>The set as a filter of the view, with the colour on lines and surfaces; a warning when the view does not take it.</summary>
        private string Destacar(View v, SelectionFilterElement sf, Color cor)
        {
            try
            {
                if (!v.IsFilterApplied(sf.Id)) v.AddFilter(sf.Id);
                var ogs = new OverrideGraphicSettings();
                ogs.SetProjectionLineColor(cor);
                ogs.SetCutLineColor(cor);
                FillPatternElement solido = new FilteredElementCollector(Doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                    .FirstOrDefault(f => f.GetFillPattern().IsSolidFill);
                if (solido != null)
                {
                    ogs.SetSurfaceForegroundPatternId(solido.Id);
                    ogs.SetSurfaceForegroundPatternColor(cor);
                    ogs.SetCutForegroundPatternId(solido.Id);
                    ogs.SetCutForegroundPatternColor(cor);
                }
                v.SetFilterOverrides(sf.Id, ogs);
                v.SetFilterVisibility(sf.Id, true);
                return null;
            }
            catch (Exception ex)
            {
                return $"guardados no conjunto “{sf.Name}”, mas a vista não aceitou o filtro ({ex.Message}; o modelo de vista pode estar controlando os filtros)";
            }
        }
    }
}
