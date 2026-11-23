using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDwgUnifier.Tools.Common;

namespace RevitDwgUnifier.Tools.Exporting
{
    /// <summary>
    /// Exportar Tabelas: schedules to Excel (.xlsx) with the office colours (header fill, header
    /// text, zebra rows), remembered between uses. One workbook with one tab per schedule, or one
    /// file per schedule. Values go as data: numbers (with the project's decimal and grouping
    /// symbols) become real numbers, the unit stays in the cell format.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class ExportarTabelasCommand : ToolCommand
    {
        public override string Title => "Exportar Tabelas";

        protected override Result Run()
        {
            var schedules = new FilteredElementCollector(Doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                .Where(s => !s.IsTemplate && !s.IsTitleblockRevisionSchedule && !s.Definition.IsKeySchedule)
                .OrderBy(s => s.Name).ToList();
            if (schedules.Count == 0) { Show("O projeto não tem tabelas."); return Result.Cancelled; }
            var pre = new HashSet<ElementId>(UiDoc.Selection.GetElementIds());

            var dlg = new OptionsDialog(Title, "ExportarTabelas");
            var fList = dlg.Multi("schedules", "Tabelas", schedules.Select(s => s.Name).ToList(), false);
            if (pre.Count > 0) for (int i = 0; i < schedules.Count; i++) fList.Checked[i] = pre.Contains(schedules[i].Id);
            else if (Doc.ActiveView is ViewSchedule av) { int a = schedules.FindIndex(s => s.Id == av.Id); if (a >= 0) fList.Checked[a] = true; }
            dlg.Section("Arquivos");
            var fMode = dlg.Choice("mode", "Arquivos", new[] { "Uma planilha com uma aba por tabela", "Um arquivo por tabela" });
            var fFolder = dlg.Text("folder", "Pasta de destino", ExportNaming.DefaultFolder(Doc));
            var fFile = dlg.Text("file", "Nome da planilha única", "{codigo} {projeto} - Tabelas",
                "Nome do arquivo .xlsx com todas as tabelas. Variáveis: {projeto} {codigo} {data}.");
            fFile.EnabledWhen(fMode, 0);
            dlg.Section("Aparência");
            var fHeader = dlg.Text("headerColor", "Cor do cabeçalho (hex)", "1F7A5A", "Cor de fundo da linha de títulos das colunas e do título da tabela.");
            var fHeaderText = dlg.Text("headerText", "Cor do texto do cabeçalho (hex)", "FFFFFF");
            var fZebra = dlg.Check("zebra", "Linhas alternadas coloridas", true, "Pinta uma linha sim, outra não, para facilitar a leitura.");
            var fZebraColor = dlg.Text("zebraColor", "Cor das linhas alternadas (hex)", "EEF5F1");
            fZebraColor.EnabledWhen(fZebra);
            var fTitle = dlg.Check("title", "Título da tabela na primeira linha", true);
            dlg.Section("Dados");
            var fNum = dlg.Check("numeros", "Números como dados (somáveis no Excel)", true,
                "Áreas, comprimentos, quantidades, % e valores em R$ viram números de verdade; a unidade fica no formato da célula (o Excel mostra igual). Códigos como J01, 007 ou 101A continuam texto.");
            var fTot = dlg.Check("totais", "Linha de totais com fórmula (SOMA)", false,
                "Acrescenta uma linha “Total” somando cada coluna numérica com fórmula do Excel. Deixe desligado se a tabela do Revit já traz o total geral (ele seria somado de novo).");
            var fCsv = dlg.Check("csv", "Salvar também em CSV (um por tabela)", false, "Arquivo de texto separado por ponto e vírgula, para Google Planilhas e outros programas.");
            if (!dlg.Show(Owner)) return Result.Cancelled;

            string folder = fFolder.Value;
            Directory.CreateDirectory(folder);
            var chosen = fList.SelectedIndices.Select(i => schedules[i]).ToList();
            var problems = new List<string>();
            int files = 0;

            XlsxWriter NewWriter() => new XlsxWriter
            {
                HeaderFill = Hex(fHeader.Value, "1F7A5A"),
                TitleFont = Hex(fHeader.Value, "1F7A5A"),
                HeaderFont = Hex(fHeaderText.Value, "FFFFFF"),
                ZebraFill = Hex(fZebraColor.Value, "EEF5F1"),
                Zebra = fZebra.Value,
                Numeros = fNum.Value,
                Totais = fTot.Value,
                Leitor = Leitor(Doc)
            };
            void Csv(XlsxWriter.Table t)
            {
                if (!fCsv.Value) return;
                try { XlsxWriter.SaveCsv(t, Path.Combine(folder, Infrastructure.FileSafety.SanitizeFileName(t.Name, 150) + ".csv")); }
                catch (Exception ex) { problems.Add($"{t.Name} (CSV): {ex.Message}"); }
            }

            if (fMode.Index == 0)
            {
                var w = NewWriter();
                foreach (ViewSchedule s in chosen)
                {
                    try { var t = Read(s, fTitle.Value); w.Add(t); Csv(t); }
                    catch (Exception ex) { problems.Add($"{s.Name}: {ex.Message}"); }
                }
                string name = ExportNaming.Name(Doc, chosen[0], fFile.Value);
                w.Save(Path.Combine(folder, name + ".xlsx"));
                files++;
            }
            else
            {
                foreach (ViewSchedule s in chosen)
                {
                    try
                    {
                        var w = NewWriter();
                        var t = Read(s, fTitle.Value);
                        w.Add(t);
                        Csv(t);
                        w.Save(Path.Combine(folder, Infrastructure.FileSafety.SanitizeFileName(s.Name, 150) + ".xlsx"));
                        files++;
                    }
                    catch (Exception ex) { problems.Add($"{s.Name}: {ex.Message}"); }
                }
            }

            string text = $"{files} planilha(s) Excel criada(s) em:\n{folder}";
            if (problems.Count > 0) text += "\n\nAvisos:\n" + string.Join("\n", problems.Take(15).Select(p => "  • " + p));
            Show(text, ResultKind.Auto, OpenFolderAction(folder));
            return Result.Succeeded;
        }

        /// <summary>The schedule body as text, exactly as Revit displays it (formatted values,
        /// grouping headers / footers included). The first row(s) are the column headers.</summary>
        private static XlsxWriter.Table Read(ViewSchedule s, bool withTitle)
        {
            TableData td = s.GetTableData();
            TableSectionData body = td.GetSectionData(SectionType.Body);
            var table = new XlsxWriter.Table { Name = s.Name, Title = withTitle ? s.Name : null };
            for (int r = body.FirstRowNumber; r <= body.LastRowNumber; r++)
            {
                var row = new string[body.LastColumnNumber - body.FirstColumnNumber + 1];
                for (int c = body.FirstColumnNumber; c <= body.LastColumnNumber; c++)
                    row[c - body.FirstColumnNumber] = s.GetCellText(SectionType.Body, r, c);
                table.Rows.Add(row);
            }
            // Column headers: the rows before the first data row. Revit shows one header row
            // (plus grouped-header rows); use 1 when headers are shown, 0 otherwise.
            table.HeaderRows = s.Definition.ShowHeaders ? Math.Min(1, table.Rows.Count) : 0;
            return table;
        }

        /// <summary>Decimal and grouping symbols of the project units (how Revit writes the numbers in the schedules).</summary>
        private static LeitorNumero Leitor(Document doc)
        {
            var l = new LeitorNumero();
            try
            {
                Units u = doc.GetUnits();
                l.SeparadorDecimal = u.DecimalSymbol.ToString() == "Dot" ? '.' : ',';
                switch (u.DigitGroupingSymbol.ToString())
                {
                    case "Dot": l.Agrupamento = "."; break;
                    case "Comma": l.Agrupamento = ","; break;
                    case "Apostrophe": l.Agrupamento = "'"; break;
                    case "Space": l.Agrupamento = " "; break;
                    default: l.Agrupamento = ""; break;
                }
                if (l.Agrupamento == l.SeparadorDecimal.ToString()) l.Agrupamento = "";
            }
            catch (Exception ex) { Infrastructure.Logger.Warn("[JA Tools] Unidades do projeto não lidas: " + ex.Message); }
            return l;
        }

        private static string Hex(string s, string fallback)
        {
            string h = (s ?? "").Trim().TrimStart('#').ToUpperInvariant();
            return Regex.IsMatch(h, "^[0-9A-F]{6}$") ? h : fallback;
        }
    }
}
