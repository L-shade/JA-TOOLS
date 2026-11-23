using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDwgUnifier.Tools.Common;
using RevitDwgUnifier.UI;

namespace RevitDwgUnifier.Tools.Views
{
    /// <summary>
    /// Gerar Detalhes (in the spirit of id:legend): one detail ("ficha") per type — doors,
    /// windows, casework or any family. Categories accepted by legend components are drawn with
    /// them; the others (plumbing fixtures, casework components, pipe fittings, mechanical
    /// equipment, structural framing...) with sections around a placed instance. Templates control
    /// the look; the fichas can be laid out on sheets. Every step shows its progress.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class GerarDetalhesCommand : ToolCommand
    {
        public override string Title => "Gerar Detalhes";
        protected override bool PicksInView => true;   // runs its own window: no automatic progress box

        protected override Result Run()
        {
            LegendProbe probe = LegendEngine.Probe(Doc);
            if (probe.Problem != null) { Show(probe.Problem, ResultKind.Info); return Result.Cancelled; }
            if (probe.Categories.Count == 0)
            {
                Show("O projeto não tem famílias de modelo carregadas.\n\nCarregue as famílias (portas, janelas, gabinetes...) e tente de novo.", ResultKind.Info);
                return Result.Cancelled;
            }

            // types (and categories) of what is selected in Revit come pre-marked
            var pre = new List<ElementId>();
            var preCats = new List<string>();
            foreach (ElementId id in UiDoc.Selection.GetElementIds())
            {
                Element e = Doc.GetElement(id);
                if (e == null) continue;
                ElementId typeId = e is ElementType ? id : e.GetTypeId();
                if (typeId == ElementId.InvalidElementId) continue;
                pre.Add(typeId);
                string cat = e.Category?.Name;
                if (cat != null && !preCats.Contains(cat) && probe.Categories.Any(c => c.Name == cat)) preCats.Add(cat);
            }

            var window = new LegendBoardWindow(probe, pre,
                (cat, report) => LegendEngine.LoadCategory(Doc, probe, cat, report),
                report => { LegendEngine.MeasureTitleBlocks(Doc, probe, report); return null; },
                preCats);
            window.LoadDetailFamilies = paths =>
            {
                var problems = new List<string>();
                using (var tx = new Transaction(Doc, "JA Tools - Carregar detalhes típicos"))
                {
                    tx.Start();
                    LegendEngine.LoadDetailFamilies(Doc, paths, problems);
                    tx.Commit();
                }
                LegendEngine.CollectDetailSymbols(Doc, probe);
                return problems.Count > 0 ? string.Join("; ", problems) : null;
            };
            window.SetPhase = phase => { LegendEngine.SetPhase(Doc, probe, phase); return null; };
            window.FichasExistentes = LegendEngine.CountTaggedTexts(Doc);
            new WindowInteropHelper(window).Owner = Owner;
            try { return Generate(window, probe); }
            finally { LegendEngine.PhaseId = ElementId.InvalidElementId; }   // the phase is per run
        }

        private Result Generate(LegendBoardWindow window, LegendProbe probe)
        {
            bool ok = window.ShowDialog() == true;
            if (ok && window.AtualizarFichas) return AtualizarFichas(window.FichasExistentes);
            if (!ok || window.Request == null) return Result.Cancelled;
            LegendRequest req = window.Request;

            // progress of every step
            var stages = new List<string> { "Preparar", "Desenhar fichas" };
            if (req.Sheets.Enabled) stages.Add("Pranchas");
            stages.Add("Concluir");
            var progress = new ProgressWindow("Gerando " + req.Types.Count + (req.Types.Count == 1 ? " detalhe" : " detalhes"), stages.ToArray());
            progress.Title = "JA Tools — Gerar Detalhes";
            new WindowInteropHelper(progress).Owner = Owner;
            progress.Show();
            var prog = new LegendProgress
            {
                Stage = (i, m) => progress.BeginStage(Math.Min(i, stages.Count - 2), m),
                Report = (c, n, m) => progress.Report(c, n, m),
                Cancelled = () => progress.Token.IsCancellationRequested
            };

            LegendResult res;
            try
            {
                using (var t = new Transaction(Doc, "JA Tools - Gerar Detalhes"))
                {
                    t.Start();
                    RevitDwgUnifier.Export.FailureSwallower.Attach(t);
                    res = LegendEngine.Build(Doc, probe, req, prog);
                    if (res.Legends.Count == 0 || res.Cancelled)
                    {
                        t.RollBack();
                        progress.Close();
                        if (res.Cancelled) { Show("Geração cancelada. Nada foi alterado no modelo.", ResultKind.Info); return Result.Cancelled; }
                        Show("Nenhum detalhe foi gerado." + (res.Problems.Count > 0 ? "\n\nAvisos:\n" + string.Join("\n", res.Problems.Take(15).Select(p => "  • " + p)) : ""),
                             ResultKind.Warning);
                        return Result.Failed;
                    }
                    progress.Finish("Gravando no modelo…");
                    t.Commit();
                }
            }
            finally
            {
                if (progress.IsVisible) progress.Close();
            }

            string text = $"{res.Legends.Count} detalhe(s) gerado(s)" +
                          (res.Components > 0 ? $", {res.Components} componente(s) de legenda" : "") +
                          (res.ModelViews.Count > 0 ? $", {res.ModelViews.Count} vista(s) do modelo" : "") + ".";
            text += "\n" + string.Join("\n", res.Legends.Take(20).Select(v => "  • " + v.Name)) + (res.Legends.Count > 20 ? "\n  ..." : "");
            if (res.Sheets.Count > 0)
                text += $"\n\n{res.Sheets.Count} prancha(s): " + string.Join(", ", res.Sheets.Select(s => s.SheetNumber));
            if (res.MarksFilled > 0) text += $"\n\nMarca de tipo preenchida em {res.MarksFilled} tipo(s).";
            if (res.ScaleChanges.Count > 0)
                text += "\n\nEscalas:\n" + string.Join("\n", res.ScaleChanges.Take(15).Select(x => "  • " + x));
            if (res.Sheets.Count == 0) text += "\n\nPróximo passo: arraste os detalhes para as pranchas (ou use a aba Pranchas na próxima vez).";
            if (res.Problems.Count > 0) text += "\n\nAvisos:\n" + string.Join("\n", res.Problems.Take(15).Select(p => "  • " + p));
            Show(text);
            if (res.Sheets.Count > 0) TryOpen(res.Sheets[0]); else TryOpen(res.Legends[0]);
            return Result.Succeeded;
        }
    
        // ================================================================== Atualizar fichas (1.17: was its own button)

        /// <summary>Recounts the project and rewrites the quantity texts (and, if asked, the
        /// locations) of every ficha already made — on the sheets or in the legends.</summary>
        private Result AtualizarFichas(int tagged)
        {
            var phases = new List<KeyValuePair<ElementId, string>>();
            try { foreach (Phase ph in Doc.Phases) phases.Add(new KeyValuePair<ElementId, string>(ph.Id, ph.Name)); } catch { }
            var phaseItems = new List<string> { "A mesma de quando a ficha foi gerada", "Todas as fases" };
            phaseItems.AddRange(phases.Select(p => p.Value));

            var dlg = new OptionsDialog(Title, "AtualizarFichas", "Atualizar fichas");
            var fQty = dlg.Check("qty", "Quantidades", true,
                "Quantidade no projeto, quantidade por compartimento e a linha TOTAL do estilo Relação.");
            var fLoc = dlg.Check("loc", "Localização (ambientes / compartimentos)", false);
            var fPhase = dlg.Choice("phase", "Fase dos elementos", phaseItems, 0,
                "Só os elementos existentes nessa fase são contados (novos ou existentes).");
            dlg.Note($"{tagged} texto(s) de fichas podem ser atualizados (nas pranchas e nas legendas).");
            dlg.Note("Depois do OK: as quantidades são contadas de novo no projeto e os textos das fichas são reescritos. O resto da ficha não muda.");
            if (!dlg.Show(Owner)) return Result.Cancelled;
            if (!fQty.Value && !fLoc.Value) { Show("Marque o que atualizar (quantidades e/ou localização).", ResultKind.Info); return Result.Cancelled; }

            ElementId phase = fPhase.Index == 0 ? null
                            : fPhase.Index == 1 ? ElementId.InvalidElementId
                            : phases[fPhase.Index - 2].Key;
            var problems = new List<string>();
            int changed, found, fichas;
            using (var t = new Transaction(Doc, "JA Tools - Atualizar fichas"))
            {
                t.Start();
                changed = LegendEngine.UpdateCounts(Doc, fQty.Value, fLoc.Value, phase, problems, out found, out fichas);
                t.Commit();
            }

            string text = changed > 0
                ? $"{changed} texto(s) atualizado(s) em fichas de {fichas} tipo(s)."
                : $"Tudo em dia: as {found} contagem(ns) das fichas já batem com o projeto.";
            if (problems.Count > 0) text += "\n\n" + string.Join("\n", problems.Take(10));
            Show(text);
            return Result.Succeeded;
        }
    }
}
