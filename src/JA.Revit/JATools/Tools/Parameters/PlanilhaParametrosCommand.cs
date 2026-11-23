using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDwgUnifier.Tools.Common;
using RevitDwgUnifier.Tools.Publish;

namespace RevitDwgUnifier.Tools.Parameters
{
    /// <summary>
    /// Planilha de Parâmetros: the round trip Revit → Excel → Revit. "Gerar" writes one row per
    /// element of a schedule (first column = element ID, then the schedule's own columns);
    /// "Importar" reads the edited .csv/.xlsx back and writes only the cells that changed, after
    /// showing what will change.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class PlanilhaParametrosCommand : ToolCommand
    {
        public override string Title => "Planilha de Parâmetros";
        private const string ColId = "ID";

        protected override Result Run()
        {
            var schedules = new FilteredElementCollector(Doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                .Where(s => !s.IsTemplate && !s.IsTitleblockRevisionSchedule && !s.IsInternalKeynoteSchedule)
                .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

            var dlg = new OptionsDialog(Title, "PlanilhaParametros", "Continuar");
            var fModo = dlg.Choice("modo", "O que fazer", new[] { "Gerar a planilha para editar no Excel", "Importar a planilha editada" }, 0);
            var fTabela = dlg.Multi("tabelas", "Tabelas do Revit", schedules.Count > 0 ? schedules.Select(s => s.Name).ToList() : new List<string> { "(nenhuma tabela no projeto)" }, false, false,
                "Um arquivo Excel com uma aba por tabela: uma linha por elemento e as mesmas colunas dela.");
            fTabela.VisibleWhen(fModo, 0);
            if (Doc.ActiveView is ViewSchedule av) { int a = schedules.FindIndex(s => s.Id == av.Id); if (a >= 0) fTabela.Checked[a] = true; }
            var fPasta = dlg.Text("pasta", "Pasta de destino", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "JA Tools"));
            fPasta.VisibleWhen(fModo, 0);
            var fTipo = dlg.Check("tipo", "Alterar também parâmetros de tipo", false,
                "Um parâmetro de tipo muda em todos os elementos daquele tipo. Desligado, só os parâmetros de instância são alterados.");
            fTipo.VisibleWhen(fModo, 1);
            dlg.Note("Edite só as células de valores: não mexa na coluna ID nem nos títulos das colunas. Salve em .csv ou .xlsx.");
            if (!dlg.Show(Owner)) return Result.Cancelled;

            if (fModo.Index == 0)
            {
                if (schedules.Count == 0) { Show("O projeto não tem tabelas.", ResultKind.Info); return Result.Cancelled; }
                var escolhidas = fTabela.SelectedIndices.Select(i => schedules[i]).ToList();
                if (escolhidas.Count == 0) { Show("Marque pelo menos uma tabela.", ResultKind.Info); return Result.Cancelled; }
                return Gerar(escolhidas, fPasta.Value);
            }
            return Importar(fTipo.Value);
        }

        // ------------------------------------------------------------------ export

        private static Parameter Achar(Element e, string nome, bool comTipo, out bool deTipo)
        {
            deTipo = false;
            Parameter p = e.LookupParameter(nome);
            if (p != null) return p;
            if (!comTipo) return null;
            Element tipo = e.Document.GetElement(e.GetTypeId());
            p = tipo?.LookupParameter(nome);
            deTipo = p != null;
            return p;
        }

        private static string Valor(Parameter p)
        {
            if (p == null || !p.HasValue) return "";
            if (p.StorageType == StorageType.String) return p.AsString() ?? "";
            return p.AsValueString() ?? "";
        }

        private Result Gerar(List<ViewSchedule> tabelas, string pasta)
        {
            var xw = new RevitDwgUnifier.Tools.Exporting.XlsxWriter { Numeros = false };   // text as in Revit: the round trip compares texts
            var abas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var resumo = new List<string>();
            foreach (ViewSchedule s in tabelas)
            {
                var tab = Tabela(s);
                if (tab == null) { resumo.Add("• " + s.Name + ": sem elementos ou colunas editáveis (pulada)"); continue; }
                string nome = new string(s.Name.Where(ch => "[]:*?/\\".IndexOf(ch) < 0).ToArray()).Trim();
                if (nome.Length > 28) nome = nome.Substring(0, 28);
                string unico = nome.Length == 0 ? "Tabela" : nome; int k = 2;
                while (!abas.Add(unico)) unico = (nome.Length > 25 ? nome.Substring(0, 25) : nome) + " " + k++;
                tab.Name = unico;
                xw.Add(tab);
                resumo.Add("• " + s.Name + ": " + (tab.Rows.Count - 1) + " elemento(s), " + (tab.Rows[0].Length - 1) + " coluna(s)");
            }
            if (abas.Count == 0) { Show("Nenhuma das tabelas tem elementos ou colunas de parâmetros para editar.\n" + string.Join("\n", resumo), ResultKind.Info); return Result.Cancelled; }
            Directory.CreateDirectory(pasta);
            string titulo = tabelas.Count == 1 ? tabelas[0].Name : (string.IsNullOrWhiteSpace(Doc.Title) ? "projeto" : Doc.Title);
            string path = Path.Combine(pasta, Infrastructure.FileSafety.SanitizeFileName("Parâmetros - " + titulo, 150) + ".xlsx");
            try { xw.Save(path); }
            catch (IOException) { path = Path.Combine(pasta, Path.GetFileNameWithoutExtension(path) + " " + DateTime.Now.ToString("HHmmss") + ".xlsx"); xw.Save(path); }
            Show($"{abas.Count} aba(s) em:\n{path}\n\n{string.Join("\n", resumo)}\n\nEdite os valores no Excel, salve e volte em Planilha de Parâmetros › Importar a planilha editada (todas as abas são lidas).\nColunas calculadas, de contagem e parâmetros só de leitura são ignorados na importação.",
                 ResultKind.Success, OpenFolderAction(pasta));
            return Result.Succeeded;
        }

        /// <summary>One schedule as a table: ID + its parameter columns, one row per element (null = nothing to edit).</summary>
        private RevitDwgUnifier.Tools.Exporting.XlsxWriter.Table Tabela(ViewSchedule s)
        {
            var nomes = new List<string>();
            ScheduleDefinition def = s.Definition;
            for (int i = 0; i < def.GetFieldCount(); i++)
            {
                ScheduleField f = def.GetField(i);
                if (f.IsCalculatedField || f.FieldType == ScheduleFieldType.Count) continue;
                string n = f.GetName();
                if (!string.IsNullOrWhiteSpace(n) && !nomes.Contains(n) && !string.Equals(n, ColId, StringComparison.OrdinalIgnoreCase)) nomes.Add(n);
            }
            var elementos = new FilteredElementCollector(Doc, s.Id).WhereElementIsNotElementType().ToElements()
                .Where(e => e != null && e.Category != null).OrderBy(e => e.Id.Value).ToList();
            if (nomes.Count == 0 || elementos.Count == 0) return null;
            var tab = new RevitDwgUnifier.Tools.Exporting.XlsxWriter.Table { Title = s.Name };
            tab.Rows.Add(new[] { ColId }.Concat(nomes).ToArray());
            foreach (Element e in elementos)
            {
                var linha = new List<string> { e.Id.Value.ToString() };
                foreach (string n in nomes) linha.Add(Valor(Achar(e, n, true, out _)));
                tab.Rows.Add(linha.ToArray());
            }
            return tab;
        }

        // ------------------------------------------------------------------ import

        private sealed class Mudanca
        {
            public Parameter P;
            public string Id, Nome, Antes, Depois;
        }

        private Result Importar(bool comTipo)
        {
            var ofd = new Microsoft.Win32.OpenFileDialog { Title = Title, Filter = "Planilhas (*.csv;*.xlsx;*.xlsm)|*.csv;*.xlsx;*.xlsm|Todos os arquivos|*.*" };
            if (ofd.ShowDialog() != true) return Result.Cancelled;
            // every visible sheet (a CSV is one)
            var tabelas = new List<PlTabela>();
            try
            {
                if (PlanilhaLeitor.EhCsv(ofd.FileName)) tabelas.Add(PlanilhaLeitor.Ler(ofd.FileName, null, null));
                else foreach (string aba in PlanilhaLeitor.Abas(ofd.FileName)) tabelas.Add(PlanilhaLeitor.Ler(ofd.FileName, aba, null));
            }
            catch (Exception ex) { Show("Não consegui ler a planilha:\n" + ex.Message, ResultKind.Error); return Result.Failed; }
            tabelas = tabelas.Where(x => x != null && x.Celulas != null && x.Linhas >= 2).ToList();
            if (tabelas.Count == 0) { Show("A planilha está vazia.", ResultKind.Info); return Result.Cancelled; }

            var mudancas = new List<Mudanca>();
            var avisos = new List<string>();
            var vistos = new HashSet<string>();
            int linhas = 0, semElemento = 0, soLeitura = 0, semColunaId = 0;
            foreach (PlTabela t in tabelas)
            {
            string Cel(int l, int c) => (t.Celulas[l, c]?.Texto ?? "").Trim();
            int cab = -1, colId = -1;
            for (int l = 0; l < Math.Min(t.Linhas, 10) && cab < 0; l++)
                for (int c = 0; c < t.Colunas; c++)
                    if (string.Equals(Cel(l, c), ColId, StringComparison.OrdinalIgnoreCase)) { cab = l; colId = c; break; }
            if (cab < 0) { semColunaId++; continue; }
            for (int l = cab + 1; l < t.Linhas; l++)
            {
                string idTxt = Cel(l, colId);
                if (idTxt.Length == 0) continue;
                // Excel may show a big ID as 1.234.567 or 1234567,00
                string limpo = new string(idTxt.Split(',')[0].Where(char.IsDigit).ToArray());
                if (!long.TryParse(limpo, out long idNum)) { semElemento++; continue; }
                Element e = Doc.GetElement(new ElementId(idNum));
                if (e == null) { semElemento++; continue; }
                linhas++;
                for (int c = 0; c < t.Colunas; c++)
                {
                    if (c == colId) continue;
                    string nome = Cel(cab, c);
                    if (nome.Length == 0) continue;
                    Parameter p = Achar(e, nome, comTipo, out bool deTipo);
                    if (p == null) continue;
                    string novo = Cel(l, c), antes = Valor(p).Trim();
                    if (string.Equals(novo, antes, StringComparison.Ordinal)) continue;
                    if (p.IsReadOnly || p.StorageType == StorageType.ElementId) { soLeitura++; continue; }
                    // one type parameter appears in many rows: keep the first different value
                    string chave = (deTipo ? "T" + e.GetTypeId().Value : "E" + e.Id.Value) + "|" + nome;
                    if (!vistos.Add(chave)) continue;
                    mudancas.Add(new Mudanca { P = p, Id = idTxt, Nome = nome + (deTipo ? " (tipo)" : ""), Antes = antes, Depois = novo });
                }
            }
            }
            if (semColunaId == tabelas.Count)
            {
                Show("Não achei a coluna “ID” na planilha. Use uma planilha criada por Planilha de Parâmetros › Gerar.", ResultKind.Warning);
                return Result.Cancelled;
            }
            if (mudancas.Count == 0)
            {
                Show($"Nada a alterar: {linhas} linha(s) lida(s), todos os valores já são os do modelo." +
                     (semElemento > 0 ? $"\n{semElemento} linha(s) com ID que não existe neste projeto." : "") +
                     (soLeitura > 0 ? $"\n{soLeitura} célula(s) diferente(s) em parâmetros só de leitura (ignoradas)." : ""), ResultKind.Info);
                return Result.Cancelled;
            }

            // ---- preview
            var conf = new OptionsDialog(Title, "PlanilhaParametrosConfirmar", "Aplicar");
            conf.Section($"{mudancas.Count} valor(es) serão alterados");
            var fLista = conf.Multi("lista", "Alterações", mudancas.Take(400).Select(m => $"{m.Id} · {m.Nome}: {(m.Antes.Length == 0 ? "(vazio)" : m.Antes)}  →  {(m.Depois.Length == 0 ? "(vazio)" : m.Depois)}").ToList(), true, false);
            if (mudancas.Count > 400) conf.Note($"Mostrando as 400 primeiras; as outras {mudancas.Count - 400} também serão aplicadas.");
            conf.Note("Desmarque o que não quiser alterar. Pode ser desfeito com Ctrl+Z.");
            if (!conf.Show(Owner)) return Result.Cancelled;
            var fora = new HashSet<int>(Enumerable.Range(0, Math.Min(400, mudancas.Count)).Except(fLista.SelectedIndices));

            int feitas = 0;
            using (var tx = new Transaction(Doc, "JA Tools - " + Title))
            {
                tx.Start();
                for (int i = 0; i < mudancas.Count; i++)
                {
                    if (fora.Contains(i)) continue;
                    Mudanca m = mudancas[i];
                    try
                    {
                        bool ok;
                        if (m.P.StorageType == StorageType.String) ok = m.P.Set(m.Depois);
                        else if (m.P.StorageType == StorageType.Integer && SimNao(m.Depois, out int sn)) ok = m.P.Set(sn);
                        else ok = m.P.SetValueString(m.Depois);
                        if (ok) feitas++;
                        else if (avisos.Count < 20) avisos.Add($"{m.Id} · {m.Nome}: o Revit não aceitou “{m.Depois}”.");
                    }
                    catch (Exception ex) { if (avisos.Count < 20) avisos.Add($"{m.Id} · {m.Nome}: {ex.Message}"); }
                }
                tx.Commit();
            }
            Show($"{feitas} valor(es) alterado(s) em {linhas} linha(s)." +
                 (semElemento > 0 ? $"\n{semElemento} linha(s) com ID que não existe neste projeto." : "") +
                 (soLeitura > 0 ? $"\n{soLeitura} célula(s) em parâmetros só de leitura (ignoradas)." : "") +
                 (avisos.Count > 0 ? "\n\nNão alterados:\n• " + string.Join("\n• ", avisos) : ""),
                 avisos.Count > 0 ? ResultKind.Warning : ResultKind.Success);
            return Result.Succeeded;
        }

        private static bool SimNao(string v, out int n)
        {
            n = 0;
            switch ((v ?? "").Trim().ToLowerInvariant())
            {
                case "sim": case "yes": case "verdadeiro": case "true": n = 1; return true;
                case "não": case "nao": case "no": case "falso": case "false": n = 0; return true;
            }
            return false;
        }
    }
}
