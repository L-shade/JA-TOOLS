using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using JAEsquadrias.Core;
using JAEsquadrias.Engine;
using Settings = JAEsquadrias.Core.Settings;

namespace JAEsquadrias.Commands
{
    /// <summary>
    /// Conferir Planta: measures, in the active plan view, the 2D plan of the JA windows and doors
    /// (selected, or all in the view) — every line parallel to the wall: marco, folhas, vidro,
    /// soleira, verga — and compares it with the drawing the configurator makes for the REAL host
    /// wall and finishes. Types with lines out of place can be regenerated with one click (an old
    /// family, made by an older version, is the usual cause) and are checked again.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class ConferirPlantaCommand : ToolCommand
    {
        public override string Title => "Conferir Planta";
        private const double Mm = 1.0 / 304.8;

        private sealed class Item
        {
            public FamilyInstance Inst;
            public FamilySymbol Tipo;
            public EsquadriaConfig Cfg;
            public ResultadoConferencia R;
            public EsquadriaConfig Usada;
            public bool Adaptavel;
        }

        protected override Result Run()
        {
            if (!(Doc.ActiveView is ViewPlan vista) || vista.IsTemplate)
            {
                Show("Abra uma planta baixa (ou de forro) com as janelas e portas a conferir e rode de novo.\nA conferência usa o que a vista mostra.", ResultKind.Info);
                return Result.Cancelled;
            }
            var avisos = new List<string>();
            List<FamilyInstance> alvos = Alvos(vista, avisos);
            if (alvos.Count == 0)
            {
                Show("Nenhuma janela ou porta do JA Famílias " + (UiDoc.Selection.GetElementIds().Count > 0 ? "na seleção." : "nesta vista.") +
                     "\nSelecione esquadrias JA (ou deixe a seleção vazia para conferir todas as da vista).", ResultKind.Info);
                return Result.Cancelled;
            }

            List<Item> itens = Conferir(alvos, vista, avisos);
            var ruins = itens.Where(i => !i.R.Ok).GroupBy(i => i.Tipo.Id).ToList();
            bool refazer = false;
            string texto = Relatorio(itens, vista, avisos, false);
            if (ruins.Count == 0)
            {
                Show(texto, avisos.Count > 0 ? ResultKind.Warning : ResultKind.Success);
                return Result.Succeeded;
            }
            Show(texto, ResultKind.Warning, new ResultAction("Gerar de novo e conferir", () => refazer = true, true));
            if (!refazer) return Result.Succeeded;

            // regenerate the types with differences (same family and type names: every copy updates)
            var settings = Settings.Current;
            var avisos2 = new List<string>();
            int feitos = 0;
            foreach (var g in ruins)
            {
                Item first = g.First();
                try
                {
                    GenerationResult gen = FamilyGenerator.Generate(UiApp.Application, Doc, first.Cfg, settings, first.Tipo.Family?.Name, first.Tipo.Name);
                    avisos2.AddRange(gen.Warnings);
                    feitos++;
                }
                catch (Exception ex) { avisos2.Add($"{first.Cfg.Codigo}: não foi gerada de novo ({ex.Message})."); }
            }
            var alvos2 = alvos.Where(a => a.IsValidObject).ToList();
            List<Item> depois = Conferir(alvos2, vista, avisos2);
            var sb = new StringBuilder();
            sb.AppendLine($"{feitos} tipo(s) gerado(s) de novo e conferido(s).");
            sb.Append(Relatorio(depois, vista, avisos2, true));
            Show(sb.ToString(), depois.All(i => i.R.Ok) && avisos2.Count == 0 ? ResultKind.Success : ResultKind.Warning);
            return Result.Succeeded;
        }

        // ------------------------------------------------------------------ what to check
        private List<FamilyInstance> Alvos(View vista, List<string> avisos)
        {
            var sel = UiDoc.Selection.GetElementIds().Select(id => Doc.GetElement(id)).OfType<FamilyInstance>().ToList();
            IEnumerable<FamilyInstance> pool = sel.Count > 0 ? sel
                : new FilteredElementCollector(Doc, vista.Id)
                    .WherePasses(new ElementMulticategoryFilter(new List<BuiltInCategory> { BuiltInCategory.OST_Windows, BuiltInCategory.OST_Doors }))
                    .WhereElementIsNotElementType().OfType<FamilyInstance>();
            var list = new List<FamilyInstance>();
            int cortina = 0;
            foreach (FamilyInstance fi in pool)
            {
                if (ProjectEsquadrias.ConfigOf(fi, out _) == null) continue;
                if (!(fi.Host is Wall w)) continue;
                if (w.CurtainGrid != null) { cortina++; continue; }
                list.Add(fi);
            }
            if (cortina > 0) avisos.Add($"{cortina} esquadria(s) em parede cortina ficaram de fora (a planta delas é a do painel).");
            return list;
        }

        private List<Item> Conferir(List<FamilyInstance> alvos, View vista, List<string> avisos)
        {
            var itens = new List<Item>();
            var opt = new Options { View = vista };
            foreach (FamilyInstance fi in alvos)
            {
                try
                {
                    EsquadriaConfig cfg = ProjectEsquadrias.ConfigOf(fi, out FamilySymbol sym);
                    if (cfg == null || sym == null) continue;
                    if (!SoleirasPeitorisCommand.Vao(fi, out XYZ centro, out XYZ ext, out double paredeMm, out _, out _, out _))
                    {
                        avisos.Add($"{cfg.Codigo} ({fi.Id.Value}): posição na parede não encontrada.");
                        continue;
                    }
                    // the drawing expected for this copy: real wall and finishes when the family follows the wall
                    EsquadriaConfig c = cfg.Clone();
                    double? rx = Comprimento(fi, AncorasParede.NomeRevExt), ri = Comprimento(fi, AncorasParede.NomeRevInt);
                    bool adapt = rx.HasValue && ri.HasValue;
                    if (adapt)
                    {
                        c.ParedeMm = Math.Round(paredeMm, 2);
                        c.RevestimentoExtMm = rx.Value;
                        c.RevestimentoIntMm = ri.Value;
                    }
                    var esperadas = ConferenciaPlanta.Esperadas(c, g => Ligado(fi, cfg, g));
                    if (esperadas.Count == 0) continue;     // 2D plan switched off

                    XYZ u = XYZ.BasisZ.CrossProduct(ext).Normalize();
                    double meio = cfg.LarguraMm / 2;
                    var medidas = new List<SegMedido>();
                    ConferenciaPlanta.Coletar(fi.get_Geometry(opt), Doc,
                        p => (p - centro).DotProduct(ext) / Mm,
                        p => (p - centro).DotProduct(u) / Mm + meio, medidas);
                    var r = ConferenciaPlanta.Comparar(esperadas, medidas, cfg.LarguraMm, true);
                    itens.Add(new Item { Inst = fi, Tipo = sym, Cfg = cfg, Usada = c, R = r, Adaptavel = adapt });
                    if (!r.Ok)
                        Logger.Warn($"[JA Famílias] Conferir Planta {cfg.Codigo} ({fi.Id.Value}), parede {Layout.Mm(paredeMm)} mm, " +
                                    $"revestimentos {Layout.Mm(c.RevestimentoExtMm)}/{Layout.Mm(c.RevestimentoIntMm)} mm, {r.Medidas} linhas medidas" +
                                    (r.Espelhada ? ", espelhada" : "") + (r.Virada ? ", virada" : "") + ":\n" + ConferenciaPlanta.Texto(c, r, 60));
                }
                catch (Exception ex)
                {
                    Logger.Warn("[JA Famílias] Conferir Planta " + fi.Id.Value + ": " + ex);
                    avisos.Add($"Elemento {fi.Id.Value}: {ex.Message}");
                }
            }
            return itens;
        }

        private static double? Comprimento(FamilyInstance fi, string nome)
        {
            try
            {
                Parameter p = fi.LookupParameter(nome);
                if (p != null && p.StorageType == StorageType.Double) return Math.Round(p.AsDouble() / Mm, 2);
            }
            catch { }
            return null;
        }

        /// <summary>The instance's own Yes/No "Planta - …" value (hidden groups are not drawn).</summary>
        private static bool Ligado(FamilyInstance fi, EsquadriaConfig cfg, GrupoPlanta g)
        {
            try
            {
                Parameter p = fi.LookupParameter(FamilyGenerator.NomeGrupoPlanta(cfg, g));
                if (p != null && p.StorageType == StorageType.Integer) return p.AsInteger() != 0;
            }
            catch { }
            return true;
        }

        // ------------------------------------------------------------------ report
        private static string Relatorio(List<Item> itens, View vista, List<string> avisos, bool segunda)
        {
            var sb = new StringBuilder();
            var tipos = itens.GroupBy(i => i.Tipo.Id).ToList();
            var ruins = tipos.Where(g => g.Any(i => !i.R.Ok)).ToList();
            if (!segunda)
            {
                if (ruins.Count == 0) sb.AppendLine($"Planta conferida: {itens.Count} esquadria(s) de {tipos.Count} tipo(s), todas as linhas no lugar.");
                else sb.AppendLine($"{ruins.Count} de {tipos.Count} tipo(s) com linhas da planta fora do lugar.");
                sb.AppendLine($"Vista: {vista.Name}. Cada linha do marco, das folhas, do vidro e da pedra foi comparada com o desenho do configurador para a parede real.");
            }
            else if (ruins.Count == 0) sb.AppendLine("Agora todas as linhas estão no lugar.");
            else sb.AppendLine($"Ainda há {ruins.Count} tipo(s) com diferença: mande o registro (Abrir registro) para análise.");

            foreach (var g in ruins)
            {
                var pior = g.Where(i => !i.R.Ok).OrderByDescending(i => i.R.Desvios.Count).First();
                int n = g.Count(), m = g.Count(i => !i.R.Ok);
                sb.AppendLine();
                string extra = (pior.R.Espelhada ? ", espelhada" : "") + (pior.R.Virada ? ", virada" : "");
                sb.AppendLine($"{pior.Cfg.Codigo} — {m} de {n} cópia(s) · parede {Layout.Mm(pior.Usada.ParedeMm)} mm{extra}" + (pior.Adaptavel ? "" : " · família não acompanha a parede (gerada para " + Layout.Mm(pior.Cfg.ParedeMm) + " mm)"));
                if (pior.R.Medidas == 0)
                    sb.AppendLine("• Nenhuma linha da planta aparece nesta vista: confira Visibilidade/Gráficos (subcategorias JA Planta) e o nível de detalhe.");
                else
                    foreach (string l in ConferenciaPlanta.Texto(pior.Cfg, pior.R).Split('\n'))
                        if (!string.IsNullOrWhiteSpace(l)) sb.AppendLine("• " + l.Trim());
            }
            if (ruins.Count > 0 && !segunda)
            {
                sb.AppendLine();
                sb.AppendLine("“Gerar de novo e conferir” refaz esses tipos com a versão atual (todas as cópias acompanham) e confere outra vez.");
            }
            if (avisos.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Avisos:");
                foreach (string w in avisos.Distinct().Take(20)) sb.AppendLine("• " + w);
            }
            return sb.ToString().TrimEnd();
        }
    }
}
