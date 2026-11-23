using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using JAEsquadrias.Common;
using JAEsquadrias.Core;
using JAEsquadrias.Engine;
using JAEsquadrias.Infrastructure;

namespace JAEsquadrias.Commands
{
    /// <summary>
    /// Atualizar Revestimentos: after the walls / rooms change, redoes in one go (one undo) what the
    /// JA tools drew with their own stored settings: the paginações (in their views), the rodapés,
    /// the forros and sancas and the colours of Iluminação e Ventilação.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class AtualizarRevestimentosCommand : EsquadriaCommandBase
    {
        public override string Title => "Atualizar Revestimentos";

        protected override Result Run()
        {
            // what there is to redo
            var pags = new FilteredElementCollector(Doc).OfClass(typeof(Group)).Cast<Group>()
                .Select(g => new PaginacaoExistente { G = g, D = JaDados.Ler(g) }).Where(x => x.D != null && x.D.Tipo == "paginacao").ToList();
            List<DadosJa> LerRodapes() => new FilteredElementCollector(Doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                .Select(fi => JaDados.Ler(fi)).Where(d => d != null && d.Tipo == "rodape").ToList();
            var rodapes = LerRodapes();
            // rodapés of older versions (no stored settings): adopted when the rodapés are redone
            int antigos = new FilteredElementCollector(Doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                .Count(fi => ProjectPecas.ConfigOf(fi, out _) is PedraConfig pc && pc.Uso == UsoPedra.Rodape && string.IsNullOrWhiteSpace(JaDados.Ler(fi)?.Config));
            var forros = ComDadosJa("forro", BuiltInCategory.OST_Ceilings, BuiltInCategory.OST_GenericModel).Select(x => x.D).ToList();
            var regioesIlum = new FilteredElementCollector(Doc).OfClass(typeof(FilledRegion)).Select(e => JaDados.Ler(e)).Where(d => d != null && d.Tipo == "iluminacao").ToList();
            ConfigIluminacao cIlum = ConfigIluminacao.Salva();
            int nRodAmb = rodapes.Select(d => d.Ambiente).Distinct().Count(), nForAmb = forros.Select(d => d.Ambiente).Distinct().Count();
            if (pags.Count == 0 && nRodAmb == 0 && antigos == 0 && nForAmb == 0 && regioesIlum.Count == 0)
            {
                Show("Nada para atualizar: este projeto ainda não tem paginação, rodapé, forro ou cores de iluminação feitos pelo JA Famílias.", ResultKind.Info);
                return Result.Cancelled;
            }

            var dlg = new OptionsDialog(Title, "AtualizarRevestimentos", "Atualizar");
            dlg.Note("Refaz com as configurações guardadas em cada um, seguindo as paredes e os ambientes como estão agora.");
            var fPag = dlg.Check("pag", $"Paginações ({pags.Count} grupo(s) em {pags.Select(x => x.D.Vista).Distinct().Count()} vista(s))", pags.Count > 0);
            var fRod = dlg.Check("rod", $"Rodapés ({nRodAmb} ambiente(s)" + (antigos > 0 ? $", mais {antigos} trecho(s) de versões antigas" : "") + ")", nRodAmb > 0 || antigos > 0,
                antigos > 0 ? "Os trechos antigos ganham o ambiente e as configurações (altura, espessura e pedra da própria família; o resto, os últimos valores do Rodapé) e são refeitos junto." : null);
            var fFor = dlg.Check("for", $"Forros e sancas ({nForAmb} ambiente(s))", nForAmb > 0);
            var fIlum = dlg.Check("ilum", $"Iluminação e ventilação ({regioesIlum.Select(d => d.Vista).Distinct().Count()} planta(s) pintada(s))", regioesIlum.Count > 0,
                "Reconfere com as últimas frações usadas (as guardadas ou, de versões antigas, as que a janela da ferramenta lembra) e repinta as plantas que já tinham cores.");
            if (!dlg.Show(Owner)) return Result.Cancelled;

            var avisos = new List<string>();
            var linhas = new List<string>();
            var br = PecaUtil.Pt;
            ToolProgress.Setup("Atualizando os revestimentos", new[] { "Paginações", "Rodapés", "Forros", "Iluminação" });
            using (var tg = new TransactionGroup(Doc, "JA Famílias – " + Title))
            {
                tg.Start();
                // ---------------------------------------------------------------- paginações
                if (fPag.Value && pags.Count > 0)
                {
                    ToolProgress.Stage(0, "Paginações");
                    int feitas = 0, perdidas = 0, linhasJunta = 0;
                    foreach (var porVista in pags.GroupBy(x => x.D.Vista))
                    {
                        if (!(Doc.GetElement(new ElementId(porVista.Key)) is View v)) { perdidas += porVista.Count(); continue; }
                        var existentes = PaginacoesDaVista(v);
                        var feitosAqui = new HashSet<long>();
                        foreach (var x in existentes)
                        {
                            if (!x.G.IsValidObject || feitosAqui.Contains(x.G.Id.Value)) continue;
                            ConfigPaginacao cfg = null;
                            try { cfg = Json.Deserialize<ConfigPaginacao>(x.D.Config); } catch { }
                            if (cfg == null) { perdidas++; continue; }
                            RegiaoPag reg = RegiaoGuardada(v, x, existentes);
                            if (reg == null) { perdidas++; avisos.Add("Paginação “" + x.G.GroupType?.Name + "”: o ambiente ou a face não existe mais (ficou como estava)."); continue; }
                            foreach (ElementId id in reg.Antigos) feitosAqui.Add(id.Value);
                            var regs = new List<RegiaoPag> { reg };
                            if (v is ViewPlan && cfg.Ralos) DescontarRalos(v, regs);
                            DesenharPaginacao(v, regs, cfg, avisos, out int nl, out _);
                            linhasJunta += nl; feitas++;
                        }
                    }
                    linhas.Add($"Paginações: {feitas} refeita(s)" + (perdidas > 0 ? $", {perdidas} sem ambiente / face (ficaram como estavam)" : "") + ".");
                }
                // ---------------------------------------------------------------- rodapés
                if (fRod.Value && (nRodAmb > 0 || antigos > 0))
                {
                    ToolProgress.Stage(1, "Rodapés");
                    int amb = 0, trechos = 0, adotados = 0; double metros = 0;
                    if (antigos > 0) { adotados = AdotarRodapesAntigos(avisos); rodapes = LerRodapes(); }
                    // one setting per room (the first stretch found), then the rooms with the same setting together
                    var porAmbiente = rodapes.Where(d => !string.IsNullOrWhiteSpace(d.Config)).GroupBy(d => d.Ambiente).Select(g => g.First());
                    foreach (var g in porAmbiente.GroupBy(d => d.Config))
                    {
                        ConfigRodape cfg = null;
                        try { cfg = Json.Deserialize<ConfigRodape>(g.Key); } catch { }
                        if (cfg == null) continue;
                        var rooms = g.Select(d => d.Ambiente).Distinct().Select(id => Doc.GetElement(new ElementId(id)) as Room).Where(r => r != null && r.Area > 0).ToList();
                        if (rooms.Count == 0) continue;
                        ResultadoRodape rr = AplicarRodapes(rooms, cfg, true, avisos);
                        amb += rooms.Count; trechos += rr.Trechos; metros += rr.Metros;
                    }
                    linhas.Add($"Rodapés: {amb} ambiente(s), {trechos} trecho(s), {metros.ToString("0.00", br)} m" + (adotados > 0 ? $" ({adotados} trecho(s) de versões antigas incorporados)" : "") + ".");
                }
                // ---------------------------------------------------------------- forros
                if (fFor.Value && nForAmb > 0)
                {
                    ToolProgress.Stage(2, "Forros e sancas");
                    int amb = 0; double area = 0;
                    foreach (var g in forros.GroupBy(d => d.Config ?? ""))
                    {
                        ConfigForro cfg = null;
                        try { cfg = Json.Deserialize<ConfigForro>(g.Key); } catch { }
                        if (cfg == null) continue;
                        var rooms = g.Select(d => d.Ambiente).Distinct().Select(id => Doc.GetElement(new ElementId(id)) as Room).Where(r => r != null && r.Area > 0).ToList();
                        if (rooms.Count == 0) continue;
                        ResultadoForro rf = AplicarForros(rooms, cfg, avisos);
                        amb += rf.Ambientes; area += rf.AreaM2;
                    }
                    linhas.Add($"Forros: {amb} ambiente(s), {area.ToString("0.00", br)} m².");
                }
                // ---------------------------------------------------------------- iluminação
                if (fIlum.Value)
                {
                    ToolProgress.Stage(3, "Iluminação e ventilação");
                    var plantas = regioesIlum.Select(d => d.Vista).Distinct().Select(id => Doc.GetElement(new ElementId(id)) as ViewPlan).Where(v => v != null).ToList();
                    ResultadoIluminacao ri = ConferirIluminacao(AmbientesColocados(), cIlum, plantas);
                    linhas.Add($"Iluminação e ventilação: {ri.Ok} atendem, {ri.Falhas.Count} não atendem" + (ri.Pintados > 0 ? $"; {plantas.Count} planta(s) repintada(s)" : "") + ".");
                }
                tg.Assimilate();
            }
            Show("Atualizado (um só Desfazer volta tudo):\n\n• " + string.Join("\n• ", linhas) +
                 "\n\nO Orçamento e a Lista de Compras já leem os números novos." +
                 (avisos.Count > 0 ? "\n\nAvisos:\n• " + string.Join("\n• ", avisos.Distinct().Take(20)) : ""), avisos.Count > 0 ? ResultKind.Warning : ResultKind.Success);
            return Result.Succeeded;
        }
    }
}
