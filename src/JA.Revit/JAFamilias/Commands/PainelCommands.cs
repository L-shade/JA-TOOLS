using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using JAEsquadrias.Core.Paineis;
using JAEsquadrias.Engine;
using JAEsquadrias.UI;
using Settings = JAEsquadrias.Core.Settings;

namespace JAEsquadrias.Commands
{
    /// <summary>
    /// Painel de Cortina (shared with Editar Família): configurator → one family per panel size
    /// (all with the same code) → the selected curtain panels swapped to the family of their size.
    /// Editing a code regenerates every size of it already in the project.
    /// </summary>
    public abstract partial class EsquadriaCommandBase
    {
        private static string ChaveTamanho(double w, double h) =>
            Math.Round(w).ToString("0", CultureInfo.InvariantCulture) + "x" + Math.Round(h).ToString("0", CultureInfo.InvariantCulture);

        protected Result AbrirPainel(List<Element> paineis, PainelConfig editar = null)
        {
            paineis = paineis ?? new List<Element>();
            var settings = Settings.Current;
            List<PainelNoProjeto> todos = PainelGenerator.All(Doc);
            var existentes = todos.GroupBy(p => p.Codigo, StringComparer.OrdinalIgnoreCase)
                .Select(g => new PainelExistente { Codigo = g.First().Codigo, Config = g.First().Config, Tamanhos = g.Count(), Usos = g.Sum(x => x.Instancias) })
                .ToList();
            var used = new HashSet<string>(existentes.Select(e => e.Codigo), StringComparer.OrdinalIgnoreCase);

            // sizes of the selected panels
            var medidos = paineis.Select(p => { TamanhoPainel(p, out double w, out double h); return new { P = p, W = w, H = h }; })
                                 .Where(x => x.W > 1 && x.H > 1).ToList();
            var tams = medidos.GroupBy(x => ChaveTamanho(x.W, x.H))
                .Select(g => new MedidaPainel { W = Math.Round(g.First().W), H = Math.Round(g.First().H), N = g.Count() })
                .OrderByDescending(t => t.N).ThenByDescending(t => t.W * t.H).ToList();
            PainelExistente editando = null;
            if (editar != null)
            {
                editando = existentes.FirstOrDefault(e => string.Equals(e.Codigo, editar.Codigo, StringComparison.OrdinalIgnoreCase));
                if (tams.Count == 0)
                    tams = todos.Where(p => string.Equals(p.Codigo, editar.Codigo, StringComparison.OrdinalIgnoreCase))
                                .Select(p => new MedidaPainel { W = Math.Round(p.Config.LarguraMm), H = Math.Round(p.Config.AlturaMm), N = p.Instancias })
                                .GroupBy(t => ChaveTamanho(t.W, t.H)).Select(g => g.First()).ToList();
            }

            PainelConfig start = editar?.Copiar() ?? PainelConfig.FromJson(ToolPrefs.Get("ultimo.painel")) ?? new PainelConfig();
            if (editar == null && used.Contains(start.Codigo)) start.Codigo = PainelGenerator.NextCode(used);
            if (tams.Count > 0) { start.LarguraMm = tams[0].W; start.AlturaMm = tams[0].H; }

            var materiais = new FilteredElementCollector(Doc).OfClass(typeof(Material)).Cast<Material>().Select(m => m.Name).Distinct().ToList();
            PainelPedido pedido = PainelWindow.Abrir(Owner, start, existentes, materiais, tams, medidos.Count, editando);
            if (pedido == null) return Result.Cancelled;
            PainelConfig cfg = pedido.Config;
            var avisos = new List<string>();
            bool editing = pedido.Editando != null;
            if (editing && !string.Equals(cfg.Codigo, pedido.Editando.Codigo, StringComparison.OrdinalIgnoreCase))
            {
                avisos.Add($"Ao editar, o código continua {pedido.Editando.Codigo} (as famílias existentes são atualizadas no lugar).");
                cfg.Codigo = pedido.Editando.Codigo;
            }
            else if (!editing && used.Contains(cfg.Codigo))
            {
                string old = cfg.Codigo;
                cfg.Codigo = PainelGenerator.NextCode(used);
                avisos.Add($"O código {old} já existe no projeto: o painel novo ficou como {cfg.Codigo}.");
            }
            ToolPrefs.Set("ultimo.painel", cfg.ToJson());
            ToolPrefs.Save();

            // what to generate: every size of the code being edited + the sizes of the selection (or the typed size)
            var alvos = new Dictionary<string, KeyValuePair<double, double>>();
            var nomes = new Dictionary<string, string>();
            if (editing)
                foreach (var p in todos.Where(x => string.Equals(x.Codigo, cfg.Codigo, StringComparison.OrdinalIgnoreCase)))
                {
                    string k = ChaveTamanho(p.Config.LarguraMm, p.Config.AlturaMm);
                    alvos[k] = new KeyValuePair<double, double>(p.Config.LarguraMm, p.Config.AlturaMm);
                    nomes[k] = p.Symbol.Family?.Name;
                }
            if (medidos.Count > 0)
            {
                foreach (var t in tams) alvos[ChaveTamanho(t.W, t.H)] = new KeyValuePair<double, double>(t.W, t.H);
            }
            else if (!editing) alvos[ChaveTamanho(cfg.LarguraMm, cfg.AlturaMm)] = new KeyValuePair<double, double>(cfg.LarguraMm, cfg.AlturaMm);

            var simbolos = new Dictionary<string, FamilySymbol>();
            string rfa = null;
            int geradas = 0;
            foreach (var kv in alvos)
            {
                PainelConfig c = cfg.Copiar();
                c.LarguraMm = kv.Value.Key;
                c.AlturaMm = kv.Value.Value;
                c.Normalized();
                try
                {
                    nomes.TryGetValue(kv.Key, out string nome);
                    PainelResult r = PainelGenerator.Generate(UiApp.Application, Doc, c, settings, nome);
                    avisos.AddRange(r.Warnings);
                    rfa = rfa ?? r.RfaPath;
                    if (r.Symbol != null) simbolos[kv.Key] = r.Symbol;
                    geradas++;
                }
                catch (Exception ex)
                {
                    if (geradas == 0 && alvos.Count == 1) throw;
                    avisos.Add($"{c.Codigo} {kv.Key}: {ex.Message}");
                }
            }

            int trocados = 0, falhas = 0;
            if (pedido.Aplicar && medidos.Count > 0)
            {
                using (var t = new Transaction(Doc, "JA Famílias · trocar painéis por " + cfg.Codigo))
                {
                    t.Start();
                    var opts = t.GetFailureHandlingOptions();
                    opts.SetFailuresPreprocessor(new WarningSwallower());
                    t.SetFailureHandlingOptions(opts);
                    foreach (var m in medidos)
                    {
                        if (!simbolos.TryGetValue(ChaveTamanho(m.W, m.H), out FamilySymbol s)) continue;
                        try
                        {
                            if (!s.IsActive) s.Activate();
                            if (m.P.GetTypeId() == s.Id) { trocados++; continue; }
                            if (m.P.Pinned) m.P.Pinned = false;
                            m.P.ChangeTypeId(s.Id);
                            trocados++;
                        }
                        catch (Exception ex) { falhas++; Logger.Warn($"[JA Famílias] Painel {m.P.Id.Value}: {ex.Message}"); }
                    }
                    t.Commit();
                }
            }

            if (avisos.Count == 0 && falhas == 0 && trocados > 0 && !editing) return Result.Succeeded;   // quiet when all went well

            var sb = new StringBuilder();
            if (editing) sb.AppendLine($"Painel {cfg.Codigo} atualizado: {geradas} família(s) (tamanhos), todas as cópias acompanham.");
            else sb.AppendLine($"Painel {cfg.Codigo} gerado: {geradas} família(s).");
            sb.AppendLine(cfg.Descricao());
            foreach (var kv in alvos.Take(12)) sb.AppendLine("• " + kv.Key.Replace("x", " × ") + " mm");
            if (alvos.Count > 12) sb.AppendLine($"• … e mais {alvos.Count - 12} tamanho(s).");
            if (trocados > 0) sb.AppendLine($"{trocados} painel(éis) da parede cortina trocado(s).");
            else if (medidos.Count == 0 && !editing)
            {
                sb.AppendLine();
                sb.AppendLine("Para usar: selecione painéis da parede cortina (Tab alterna entre parede e painel) e escolha o tipo " + cfg.Codigo + " em Propriedades — ou rode Painel de Cortina com os painéis selecionados.");
            }
            if (falhas > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"{falhas} painel(éis) não aceitaram a troca (detalhes no registro). Selecione-os e escolha o tipo em Propriedades.");
            }
            if (avisos.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Avisos:");
                foreach (string w in avisos.Distinct().Take(25)) sb.AppendLine("• " + w);
            }
            var kind = falhas > 0 || avisos.Count > 0 ? ResultKind.Warning : ResultKind.Success;
            if (rfa != null) Show(sb.ToString(), kind, OpenFolderAction(rfa));
            else Show(sb.ToString(), kind);
            return Result.Succeeded;
        }
    }

    /// <summary>
    /// Painel de Cortina: curtain-wall panels that are not doors or windows (cassete ACM, sólido,
    /// vidro, sanduíche, veneziana, brise, emoldurado), in the size of the selected panels.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class NovoPainelCommand : EsquadriaCommandBase
    {
        public override string Title => "Painel de Cortina";

        protected override Result Run()
        {
            // selected panels; a selected curtain wall gives all its panels
            var paineis = new List<Element>();
            foreach (Element e in UiDoc.Selection.GetElementIds().Select(id => Doc.GetElement(id)).Where(x => x != null))
            {
                if (EmParedeCortina(e)) paineis.Add(e);
                else if (e is Wall w && w.CurtainGrid != null)
                    paineis.AddRange(w.CurtainGrid.GetPanelIds().Select(id => Doc.GetElement(id)).Where(EmParedeCortina));
            }
            if (paineis.Count == 0)
            {
                var td = new TaskDialog(Title)
                {
                    MainInstruction = "Trocar painéis da parede cortina?",
                    MainContent = "Selecione os painéis agora para gerar no tamanho deles, ou crie só a família num tamanho digitado.",
                    CommonButtons = TaskDialogCommonButtons.Cancel
                };
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Selecionar painéis", "Clique nos painéis (Tab alterna entre parede e painel) e em Concluir. Dica: selecionar a parede cortina antes pega todos os painéis dela.");
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Só criar a família", "Largura e altura digitadas no configurador.");
                TaskDialogResult r = td.Show();
                if (r == TaskDialogResult.CommandLink1)
                {
                    try
                    {
                        IList<Reference> refs = UiDoc.Selection.PickObjects(ObjectType.Element, new PainelFilter(),
                            "Selecione os painéis da parede cortina (Tab alterna entre parede e painel) e clique em Concluir");
                        paineis = refs.Select(x => Doc.GetElement(x)).Where(e => e != null).ToList();
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
                }
                else if (r != TaskDialogResult.CommandLink2) return Result.Cancelled;
            }
            paineis = paineis.GroupBy(p => p.Id).Select(g => g.First()).ToList();
            return AbrirPainel(paineis);
        }

        private sealed class PainelFilter : ISelectionFilter
        {
            public bool AllowElement(Element e) => EmParedeCortina(e);
            public bool AllowReference(Reference reference, XYZ position) => false;
        }
    }
}
