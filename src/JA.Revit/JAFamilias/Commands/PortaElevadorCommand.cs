using System;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using JAEsquadrias.Engine;
using Settings = JAEsquadrias.Core.Settings;

namespace JAEsquadrias.Commands
{
    /// <summary>
    /// Porta de Elevador: generates the parametric elevator landing door family (ElevadorGenerator) — vão, portal, two
    /// centre-opening panels, sill and call panel, all following their parameters and the wall — loads it and places it.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class PortaElevadorCommand : ToolCommand
    {
        public override string Title => "Porta de Elevador";
        protected override bool PicksInView => true;

        protected override Result Run()
        {
            var d = new OptionsDialog(Title, "PortaElevador", "Gerar");
            d.Section("Porta");
            var fCod = d.Text("codigo", "Código (marca de tipo)", "PE01");
            var fLarg = d.Number("largura", "Vão livre — largura (mm)", 900, 600, 2500, "NBR 313 / NBR 9050: 800 mm mínimo; 900 mm usual; 1100 mm para maca.");
            var fAlt = d.Number("altura", "Vão livre — altura (mm)", 2100, 1800, 3000);
            var fPortal = d.Number("portal", "Largura do portal (mm)", 100, 20, 400, "Faixa do portal (batente) em volta do vão, na face do hall.");
            d.Section("Botoeira");
            var fBot = d.Check("botoeira", "Botoeira ao lado do portal", true, "Liga/desliga por porta no projeto (parâmetro Botoeira).");
            var fAfast = d.Number("afastamento", "Afastamento do portal (mm)", 150, 0, 1000);
            var fAltBot = d.Number("alturaBot", "Altura da base da botoeira (mm)", 1000, 300, 1800, "NBR 9050: comandos entre 0,89 m e 1,35 m.");
            d.Section("Materiais e tipos");
            var fMatP = d.Text("matPortal", "Material do portal", "Aço inox escovado");
            var fMatF = d.Text("matFolhas", "Material das folhas", "Aço inox escovado");
            var fMatS = d.Text("matSoleira", "Material da soleira", "Alumínio");
            var fTipos = d.Check("tipos", "Criar também os tipos 800 × 2000, 900 × 2100 e 1100 × 2100", true);
            var fColocar = d.Check("colocar", "Colocar nas paredes ao terminar", true);
            d.Note("A face do portal é a face EXTERNA da família (o hall); use as setas de inversão no projeto se ficar do lado do poço. " +
                   "Tudo é paramétrico: largura, altura, portal, botoeira e materiais mudam em Propriedades / Editar tipo.");
            if (!d.Show(Owner)) return Result.Cancelled;

            var cfg = new ElevadorConfig
            {
                Codigo = string.IsNullOrWhiteSpace(fCod.Value) ? "PE01" : fCod.Value.Trim(),
                LarguraMm = fLarg.Value, AlturaMm = fAlt.Value, PortalMm = fPortal.Value,
                Botoeira = fBot.Value, AfastamentoBotoeiraMm = fAfast.Value, AlturaBotoeiraMm = fAltBot.Value,
                MaterialPortal = fMatP.Value, MaterialFolhas = fMatF.Value, MaterialSoleira = fMatS.Value, TiposExtras = fTipos.Value
            };
            GenerationResult gen = ElevadorGenerator.Generate(UiApp.Application, Doc, cfg, Settings.Current);

            int colocadas = 0;
            string problema = null;
            if (fColocar.Value && gen.Symbol != null)
            {
                int antes = Contar(gen.Symbol);
                try { UiDoc.PromptForFamilyInstancePlacement(gen.Symbol); }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { }
                catch (Exception ex) { problema = "Não deu para colocar na vista ativa (" + ex.Message + "): arraste o tipo do Navegador de Projeto para uma planta."; }
                colocadas = Math.Max(0, Contar(gen.Symbol) - antes);
            }
            if (colocadas > 0 && gen.Warnings.Count == 0) return Result.Succeeded;

            var sb = new StringBuilder();
            sb.AppendLine(colocadas > 0 ? $"Porta de elevador {cfg.Codigo} gerada e colocada {colocadas} vez(es)." : $"Porta de elevador {cfg.Codigo} gerada e carregada no projeto.");
            sb.AppendLine($"Tipo {gen.TypeName}" + (cfg.TiposExtras ? " (e os tipos 800 × 2000, 900 × 2100, 1100 × 2100)" : "") + $"; {gen.Solids} peça(s).");
            if (colocadas == 0)
                sb.AppendLine(problema ?? "Para colocar: Navegador de Projeto › Famílias › Portas › " + gen.FamilyName + ", arraste o tipo para uma planta.");
            sb.AppendLine();
            sb.AppendLine("Arquivo da família:");
            sb.AppendLine(gen.RfaPath);
            if (gen.Warnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Avisos:");
                foreach (string w in gen.Warnings.Distinct()) sb.AppendLine("• " + w);
            }
            Show(sb.ToString(), gen.Warnings.Count > 0 ? ResultKind.Warning : ResultKind.Success, OpenFolderAction(System.IO.Path.GetDirectoryName(gen.RfaPath)));
            return Result.Succeeded;
        }

        private int Contar(FamilySymbol s) =>
            new FilteredElementCollector(Doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().Count(i => i.Symbol != null && i.Symbol.Id == s.Id);
    }
}
