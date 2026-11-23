using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using RevitDwgUnifier.Tools.Common;
using RevitDwgUnifier.Tools.Exporting;

namespace RevitDwgUnifier.Tools.Parameters
{
    /// <summary>
    /// Quadro de Áreas (prefeitura): per level the useful area (rooms) and the built area (floors
    /// of the model, or the areas of an area scheme), the non-computable rooms, the permeable area,
    /// and the indices over the lot — taxa de ocupação, coeficiente de aproveitamento and taxa de
    /// permeabilidade. Excel file + optionally a drafting view with the table, ready for the sheet.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class QuadroAreasCommand : ToolCommand
    {
        public override string Title => "Quadro de Áreas";

        private static double M2(double ft2) => UnitUtils.ConvertFromInternalUnits(ft2, UnitTypeId.SquareMeters);

        protected override Result Run()
        {
            List<Level> niveis = RevitUtil.Levels(Doc);
            var esquemas = new FilteredElementCollector(Doc).OfClass(typeof(AreaScheme)).Cast<AreaScheme>().ToList();
            double lote = 0;
            try
            {
                foreach (Element pl in new FilteredElementCollector(Doc).OfCategory(BuiltInCategory.OST_SiteProperty).WhereElementIsNotElementType())
                    lote += M2(pl.get_Parameter(BuiltInParameter.PROPERTY_AREA)?.AsDouble() ?? 0);
            }
            catch { }

            var dlg = new OptionsDialog(Title, "QuadroAreas", "Gerar");
            var fLote = dlg.Number("lote", "Área do terreno (m²)", Math.Round(lote, 2), 0, 1e7,
                lote > 0 ? "Lida da linha de propriedade do projeto; corrija se a matrícula for diferente." : "Do levantamento / matrícula (o projeto não tem linha de propriedade).");
            if (lote > 0) fLote.Value = Math.Round(lote, 2);
            var fConstr = dlg.Choice("construida", "Área construída por", new[] { "Pisos do modelo (lajes de cada nível)" }.Concat(esquemas.Select(e => "Esquema de áreas: " + e.Name)).ToList(), 0,
                "Pisos: soma das lajes de cada nível (as estruturais, quando houver; senão todas). Esquema de áreas: soma das áreas desenhadas nas plantas de área dele.");
            var fProj = dlg.Choice("projecao", "Projeção (taxa de ocupação)", new[] { "Maior pavimento (automático)" }.Concat(niveis.Select(l => l.Name)).ToList(), 0);
            dlg.Section("Palavras no nome (ambientes; pisos: nome do tipo)");
            var fNaoComp = dlg.Text("naoComputavel", "Não computáveis no coeficiente", "", "Conforme a lei de uso do solo do município (ex.: garagem, ático, caixa d'água, sacada até X m²). Vazio = tudo computável.");
            var fPerm = dlg.Text("permeavel", "Área permeável", "jardim, grama, gramado, permeável, canteiro");
            dlg.Section("Saída");
            var fVista = dlg.Check("vista", "Criar a vista de desenho “JA - Quadro de Áreas” para a prancha", true);
            if (!dlg.Show(Owner)) return Result.Cancelled;

            string[] Lista(string s) => (s ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0).ToArray();
            string[] naoComp = Lista(fNaoComp.Value), perm = Lista(fPerm.Value);
            bool Tem(string nome, string[] k) => k.Length > 0 && k.Any(x => (nome ?? "").ToLowerInvariant().Contains(x));

            var rooms = new FilteredElementCollector(Doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().OfType<Room>().Where(r => r.Area > 0).ToList();
            var floors = new FilteredElementCollector(Doc).OfClass(typeof(Floor)).Cast<Floor>().ToList();
            AreaScheme esquema = fConstr.Index > 0 ? esquemas[fConstr.Index - 1] : null;
            var areas = esquema == null ? new List<Area>() : new FilteredElementCollector(Doc).OfCategory(BuiltInCategory.OST_Areas).WhereElementIsNotElementType()
                .OfType<Area>().Where(a => a.Area > 0 && a.AreaScheme?.Id == esquema.Id).ToList();

            var linhas = new List<(Level lv, double util, double constr, double naoc)>();
            double permeavel = 0;
            foreach (Level lv in niveis)
            {
                double util = rooms.Where(r => r.LevelId == lv.Id).Sum(r => M2(r.Area));
                double naoc = rooms.Where(r => r.LevelId == lv.Id && Tem(r.Name, naoComp)).Sum(r => M2(r.Area));
                double constr;
                if (esquema != null) constr = areas.Where(a => a.LevelId == lv.Id).Sum(a => M2(a.Area));
                else
                {
                    var noNivel = floors.Where(f => f.LevelId == lv.Id && !Tem(f.FloorType?.Name, perm)).ToList();
                    var estruturais = noNivel.Where(f => f.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL)?.AsInteger() == 1).ToList();
                    constr = (estruturais.Count > 0 ? estruturais : noNivel).Sum(f => M2(f.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)?.AsDouble() ?? 0));
                }
                permeavel += rooms.Where(r => r.LevelId == lv.Id && Tem(r.Name, perm)).Sum(r => M2(r.Area));
                if (util > 0.01 || constr > 0.01) linhas.Add((lv, util, constr, naoc));
            }
            permeavel += floors.Where(f => Tem(f.FloorType?.Name, perm)).Sum(f => M2(f.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)?.AsDouble() ?? 0));
            if (linhas.Count == 0) { Show("Nenhuma área encontrada (ambientes, pisos ou áreas)."); return Result.Cancelled; }

            double totalUtil = linhas.Sum(l => l.util), totalConstr = linhas.Sum(l => l.constr), totalNaoC = linhas.Sum(l => l.naoc);
            double projecao = fProj.Index == 0 ? linhas.Max(l => l.constr) : linhas.Where(l => l.lv.Id == niveis[fProj.Index - 1].Id).Sum(l => l.constr);
            double terreno = fLote.Value;
            var br = CultureInfo.GetCultureInfo("pt-BR");
            string N(double v) => v.ToString("N2", br);
            string P(double v) => terreno > 0 ? (v / terreno * 100).ToString("0.00", br) + "%" : "—";
            string I(double v) => terreno > 0 ? (v / terreno).ToString("0.00", br) : "—";

            var tab = new XlsxWriter.Table { Name = "Quadro de Áreas", Title = "Quadro de Áreas — " + Doc.Title };
            tab.Rows.Add(new[] { "Pavimento", "Área útil (m²)", "Área construída (m²)", "Não computável (m²)", "Computável (m²)" });
            foreach (var l in linhas) tab.Rows.Add(new[] { l.lv.Name, N(l.util), N(l.constr), N(l.naoc), N(l.constr - l.naoc) });
            tab.Rows.Add(new[] { "Total", N(totalUtil), N(totalConstr), N(totalNaoC), N(totalConstr - totalNaoC) });
            var indices = new List<string[]>
            {
                new[] { "Área do terreno", N(terreno) + " m²" },
                new[] { "Área de projeção", N(projecao) + " m²" },
                new[] { "Taxa de ocupação (projeção / terreno)", P(projecao) },
                new[] { "Coeficiente de aproveitamento (computável / terreno)", I(totalConstr - totalNaoC) },
                new[] { "Área permeável", N(permeavel) + " m²" },
                new[] { "Taxa de permeabilidade (permeável / terreno)", P(permeavel) },
            };
            var tab2 = new XlsxWriter.Table { Name = "Índices", Title = "Índices urbanísticos" };
            tab2.Rows.Add(new[] { "Índice", "Valor" });
            tab2.Rows.AddRange(indices);

            string pasta = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "JA Tools");
            Directory.CreateDirectory(pasta);
            string path = Path.Combine(pasta, Infrastructure.FileSafety.SanitizeFileName("Quadro de áreas - " + (string.IsNullOrWhiteSpace(Doc.Title) ? "projeto" : Doc.Title), 150) + ".xlsx");
            var xw = new XlsxWriter();
            xw.Add(tab); xw.Add(tab2);
            string salvo = path;
            try { xw.Save(path); }
            catch (IOException) { salvo = Path.Combine(pasta, Path.GetFileNameWithoutExtension(path) + " " + DateTime.Now.ToString("HHmmss") + ".xlsx"); xw.Save(salvo); }

            View vista = null;
            if (fVista.Value)
            {
                using (var t = new Transaction(Doc, "JA Tools - " + Title))
                {
                    t.Start();
                    try { vista = Desenhar(tab.Rows, indices); }
                    catch (Exception ex) { RevitUtil.AddOnce(_problemas, "Vista: " + ex.Message); }
                    t.Commit();
                }
                if (vista != null) { try { UiDoc.ActiveView = vista; } catch { } }
            }
            var sb = new StringBuilder();
            sb.AppendLine($"Área útil {N(totalUtil)} m² · construída {N(totalConstr)} m² · computável {N(totalConstr - totalNaoC)} m²");
            foreach (var r in indices.Skip(2)) sb.AppendLine("• " + r[0] + ": " + r[1]);
            if (terreno <= 0) sb.AppendLine("Sem área do terreno: os índices ficaram em branco.");
            sb.AppendLine();
            sb.AppendLine(salvo);
            if (vista != null) sb.AppendLine("Vista de desenho “" + vista.Name + "”: arraste para a prancha.");
            sb.AppendLine("Confira as definições (área construída, não computáveis) com a lei do município.");
            if (_problemas.Count > 0) sb.AppendLine("\nAvisos:\n• " + string.Join("\n• ", _problemas));
            Show(sb.ToString().TrimEnd(), ResultKind.Success, OpenFolderAction(pasta));
            return Result.Succeeded;
        }

        private readonly List<string> _problemas = new List<string>();

        /// <summary>
        /// The table as text notes and lines in the drafting view "JA - Quadro de Áreas" (1:50). An
        /// existing view is emptied and redrawn in place, so it stays on its sheet where it was.
        /// </summary>
        private View Desenhar(List<string[]> pav, List<string[]> ind)
        {
            const string nome = "JA - Quadro de Áreas";
            ViewDrafting v = new FilteredElementCollector(Doc).OfClass(typeof(ViewDrafting)).Cast<ViewDrafting>().FirstOrDefault(x => x.Name == nome);
            if (v != null)
            {
                var velhos = new FilteredElementCollector(Doc, v.Id).WhereElementIsNotElementType()
                    .Where(e => e is TextNote || e is CurveElement).Select(e => e.Id).ToList();
                if (velhos.Count > 0) Doc.Delete(velhos);
            }
            else
            {
                ViewFamilyType vft = new FilteredElementCollector(Doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(x => x.ViewFamily == ViewFamily.Drafting);
                if (vft == null) throw new InvalidOperationException("sem tipo de vista de desenho");
                v = ViewDrafting.Create(Doc, vft.Id);
                try { v.Name = nome; } catch { }
                v.Scale = 50;
            }
            ElementId tt = Doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
            double mm = 50 / 304.8;                 // 1 mm on paper
            double[] col = { 0, 45, 75, 105, 135, 165 };
            double linha = 7;
            double y = 0;
            void Linha(double x0, double x1, double yy) => Doc.Create.NewDetailCurve(v, Line.CreateBound(new XYZ(x0 * mm, yy * mm, 0), new XYZ(x1 * mm, yy * mm, 0)));
            void Texto(double x, double yy, string s) => TextNote.Create(Doc, v.Id, new XYZ((x + 1.5) * mm, (yy - 1.5) * mm, 0), s, tt);
            Linha(0, col[5], y);
            for (int r = 0; r < pav.Count; r++)
            {
                for (int c = 0; c < pav[r].Length && c < 5; c++) Texto(col[c], y, pav[r][c]);
                y -= linha;
                Linha(0, col[5], y);
            }
            y -= linha;
            Linha(0, col[5], y);
            foreach (string[] r in ind)
            {
                Texto(col[0], y, r[0]);
                Texto(col[4], y, r[1]);
                y -= linha;
                Linha(0, col[5], y);
            }
            return v;
        }
    }
}
