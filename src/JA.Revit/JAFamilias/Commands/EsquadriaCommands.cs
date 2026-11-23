using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using JAEsquadrias.Common;
using JAEsquadrias.Core;
using Settings = JAEsquadrias.Core.Settings;
using JAEsquadrias.Engine;
using JAEsquadrias.UI;

namespace JAEsquadrias.Commands
{
    /// <summary>
    /// Shared flow of Nova Janela / Nova Porta / Biblioteca / Editar Família:
    /// configurator → generate + load the family → (optionally) place instances → result.
    /// </summary>
    public abstract partial class EsquadriaCommandBase : ToolCommand
    {
        /// <summary>The JA Famílias tools pick and draw in the view after their windows and run their own
        /// configurators: no automatic "carregando" box (a tool that knows its work opens one itself).</summary>
        protected override bool PicksInView => true;

        protected Result OpenConfigurator(EsquadriaConfig start, FonteCatalogo fonte, FamilySymbol editing = null, IList<Element> paineis = null, bool cortina = true)
        {
            var settings = Settings.Current;
            var all = ProjectEsquadrias.All(Doc);
            var used = new HashSet<string>(all.Select(a => a.Codigo), StringComparer.OrdinalIgnoreCase);
            bool isEdit = editing != null;
            if (isEdit) used.Remove(start.Codigo);
            else if (used.Contains(start.Codigo)) start.Codigo = ProjectEsquadrias.NextCode(used, start.Tipo);

            var items = all.Select(a => new KeyValuePair<string, EsquadriaConfig>(
                a.Codigo + " · " + (a.Instancias == 1 ? "1 no modelo" : a.Instancias + " no modelo"), a.Config)).ToList();

            var materials = new FilteredElementCollector(Doc).OfClass(typeof(Material)).Cast<Material>().Select(m => m.Name).ToList();
            ConfiguratorResult res = ConfiguratorWindow.Show(Owner, Title, start, isEdit, used, items, fonte, materials);
            if (res == null) return Result.Cancelled;
            EsquadriaConfig cfg = res.Config;

            ToolPrefs.Set(PrefKey(cfg.Tipo, cfg.ParedeCortina), cfg.ToJson());
            ToolPrefs.Save();

            if (paineis != null && !isEdit) return TrocarPaineis(cfg, paineis, used, settings, cortina);

            GenerationResult gen = FamilyGenerator.Generate(UiApp.Application, Doc, cfg, settings,
                isEdit ? editing.Family?.Name : null, isEdit ? editing.Name : null);

            int placed = 0;
            string placeProblem = null;
            if (res.Place && gen.Symbol != null) placed = Place(gen.Symbol, out placeProblem);

            // a family that could not be made parametric is built for one wall thickness: say so when it went into other walls
            if (gen.Symbol != null && !gen.Adaptavel)
            {
                var other = new FilteredElementCollector(Doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                    .Where(i => i.Symbol != null && i.Symbol.Id == gen.Symbol.Id)
                    .Select(i => ProjectEsquadrias.HostThicknessMm(i)).Where(t => t > 0 && Math.Abs(t - cfg.ParedeMm) > 1)
                    .Select(t => Math.Round(t)).Distinct().ToList();
                if (other.Count > 0 && (cfg.MarcoNaParede || cfg.Posicao != PosicaoParede.Centro || (cfg.IsPorta ? cfg.Soleira : cfg.Peitoril)))
                    gen.Warnings.Add($"{cfg.Codigo} foi gerada para parede de {Layout.Mm(cfg.ParedeMm)} mm, mas está em parede(s) de " +
                                     string.Join(", ", other.Select(t => Layout.Mm(t))) + " mm. Para ajustar marco, soleira e peitoril, gere um código para cada espessura (ou use Editar Família).");
            }

            bool quiet = res.Place && placed > 0 && gen.Warnings.Count == 0;
            if (quiet) return Result.Succeeded;

            var sb = new StringBuilder();
            if (isEdit) sb.AppendLine($"Esquadria {cfg.Codigo} atualizada em todo o modelo.");
            else if (placed > 0) sb.AppendLine($"Esquadria {cfg.Codigo} gerada e colocada {placed} vez(es).");
            else sb.AppendLine($"Esquadria {cfg.Codigo} gerada e carregada no projeto.");
            sb.AppendLine(Layout.Descricao(cfg));
            if (gen.Layout != null)
                sb.AppendLine("Uw ≈ " + gen.Layout.Uw.ToString("0.0", CultureInfo.CurrentCulture) + " W/m²K · iluminação " +
                              gen.Layout.AreaIluminacao.ToString("0.00", CultureInfo.CurrentCulture) + " m² · ventilação " +
                              gen.Layout.AreaVentilacao.ToString("0.00", CultureInfo.CurrentCulture) + " m²");
            if (!isEdit && placed == 0)
            {
                sb.AppendLine();
                sb.AppendLine(res.Place && placeProblem != null ? placeProblem
                    : "Para colocar: Navegador de Projeto › Famílias › " + (cfg.IsPorta ? "Portas" : "Janelas") + " › " + gen.FamilyName + ", arraste o tipo " + gen.TypeName + " para a planta.");
            }
            sb.AppendLine();
            sb.AppendLine("Arquivo da família:");
            sb.AppendLine(gen.RfaPath);
            if (gen.Warnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Avisos:");
                foreach (string w in gen.Warnings.Distinct()) sb.AppendLine("• " + w);
            }
            Show(sb.ToString(), gen.Warnings.Count > 0 ? ResultKind.Warning : ResultKind.Success, OpenFolderAction(gen.RfaPath));
            return Result.Succeeded;
        }

        /// <summary>Lets the user click on walls until Esc; returns how many were placed.</summary>
        private int Place(FamilySymbol symbol, out string problem)
        {
            problem = null;
            int before = Count(symbol);
            try { UiDoc.PromptForFamilyInstancePlacement(symbol); }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { }
            catch (Exception ex)
            {
                Logger.Warn("[JA Famílias] Posicionamento: " + ex.Message);
                problem = "Não deu para posicionar na vista ativa (" + ex.Message + "). Abra uma planta ou vista 3D e arraste o tipo a partir do Navegador de Projeto.";
            }
            return Math.Max(0, Count(symbol) - before);
        }

        private int Count(FamilySymbol s) =>
            new FilteredElementCollector(Doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().Count(i => i.Symbol != null && i.Symbol.Id == s.Id);

        private static string PrefKey(TipoEsquadria tipo, bool cortina) => (cortina ? "ultima.cortina." : "ultima.") + tipo;

        /// <summary>The last configuration used for this kind (keeps the office's profile, colour and glass).</summary>
        protected static EsquadriaConfig Start(TipoEsquadria tipo, bool cortina = false)
        {
            var last = EsquadriaConfig.FromJson(ToolPrefs.Get(PrefKey(tipo, cortina)));
            if (last == null && cortina) last = EsquadriaConfig.FromJson(ToolPrefs.Get(PrefKey(tipo, false)));
            var cfg = last != null && last.Tipo == tipo ? last : Catalog.Default(tipo, Settings.Current);
            cfg.ParedeCortina = cortina;
            return cfg;
        }

        // ------------------------------------------------------------------ curtain walls

        /// <summary>Panel size (mm) of a curtain panel or of a door/window already in a curtain wall.</summary>
        internal static bool TamanhoPainel(Element e, out double larguraMm, out double alturaMm)
        {
            larguraMm = Medida(e, BuiltInParameter.CURTAIN_WALL_PANELS_WIDTH, BuiltInParameter.FAMILY_WIDTH_PARAM, BuiltInParameter.WINDOW_WIDTH, BuiltInParameter.DOOR_WIDTH, "Width", "Largura");
            alturaMm = Medida(e, BuiltInParameter.CURTAIN_WALL_PANELS_HEIGHT, BuiltInParameter.FAMILY_HEIGHT_PARAM, BuiltInParameter.WINDOW_HEIGHT, BuiltInParameter.DOOR_HEIGHT, "Height", "Altura");
            return larguraMm > 1 && alturaMm > 1;
        }

        private static double Medida(Element e, BuiltInParameter a, BuiltInParameter b, BuiltInParameter c, BuiltInParameter d, string en, string pt)
        {
            Element tipo = null;
            try { tipo = e.Document.GetElement(e.GetTypeId()); } catch { }
            foreach (Element src in new[] { e, tipo })
            {
                if (src == null) continue;
                foreach (BuiltInParameter bip in new[] { a, b, c, d })
                {
                    try
                    {
                        Parameter p = src.get_Parameter(bip);
                        if (p != null && p.HasValue && p.StorageType == StorageType.Double && p.AsDouble() > 1e-6) return Math.Round(p.AsDouble() * 304.8);
                    }
                    catch { }
                }
                foreach (string n in new[] { en, pt })
                {
                    Parameter p = src.LookupParameter(n);
                    if (p != null && p.HasValue && p.StorageType == StorageType.Double && p.AsDouble() > 1e-6) return Math.Round(p.AsDouble() * 304.8);
                }
            }
            return 0;
        }

        internal static bool EmParedeCortina(Element e)
        {
            if (e == null || e.Category == null) return false;
            if (e.Category.BuiltInCategory == BuiltInCategory.OST_CurtainWallPanels) return true;
            return e is FamilyInstance fi && fi.Host is Wall w && w.CurtainGrid != null;
        }

        /// <summary>Generates one family per panel size and swaps the selected panels to it.</summary>
        private Result TrocarPaineis(EsquadriaConfig cfg, IList<Element> paineis, HashSet<string> used, Settings settings, bool cortina = true)
        {
            var grupos = paineis
                .Select(p => { TamanhoPainel(p, out double w, out double h); return new { P = p, W = w, H = h }; })
                .Where(x => x.W > 1 && x.H > 1)
                .GroupBy(x => x.W.ToString("0", CultureInfo.InvariantCulture) + "x" + x.H.ToString("0", CultureInfo.InvariantCulture))
                .Select(g => new { W = g.First().W, H = g.First().H, Paineis = g.Select(x => x.P).ToList() })
                // the size shown in the configurator goes first and keeps the chosen code
                .OrderBy(g => Math.Abs(g.W - cfg.LarguraMm) < 1 && Math.Abs(g.H - cfg.AlturaMm) < 1 ? 0 : 1)
                .ToList();

            var feitos = new List<string>();
            var avisos = new List<string>();
            string rfa = null;
            int trocados = 0, falhas = 0;
            used.Add(cfg.Codigo);
            for (int gi = 0; gi < grupos.Count; gi++)
            {
                var g = grupos[gi];
                EsquadriaConfig c = cfg.Clone();
                c.ParedeCortina = cortina;
                c.LarguraMm = g.W;
                c.AlturaMm = g.H;
                if (gi > 0) { c.Codigo = ProjectEsquadrias.NextCode(used, c.Tipo); used.Add(c.Codigo); }

                GenerationResult gen;
                try { gen = FamilyGenerator.Generate(UiApp.Application, Doc, c, settings, null, null); }
                catch (Exception ex)
                {
                    if (gi == 0) throw;
                    avisos.Add($"{c.Codigo} ({Layout.Mm(g.W)} × {Layout.Mm(g.H)}): {ex.Message}");
                    continue;
                }
                rfa = rfa ?? gen.RfaPath;
                avisos.AddRange(gen.Warnings);
                if (gen.Symbol == null) { avisos.Add($"{c.Codigo}: a família foi gerada, mas o tipo não foi encontrado no projeto."); continue; }

                int ok = 0;
                using (var t = new Transaction(Doc, "JA Famílias · trocar " + (cortina ? "painéis" : "esquadrias") + " por " + c.Codigo))
                {
                    t.Start();
                    try { if (!gen.Symbol.IsActive) gen.Symbol.Activate(); } catch { }
                    foreach (Element p in g.Paineis)
                    {
                        try
                        {
                            if (p.Pinned) p.Pinned = false;
                            p.ChangeTypeId(gen.Symbol.Id);
                            ok++;
                        }
                        catch (Exception ex) { falhas++; Logger.Warn($"[JA Famílias] Painel {p.Id}: {ex.Message}"); }
                    }
                    t.Commit();
                }
                trocados += ok;
                feitos.Add($"{c.Codigo} · {Layout.Mm(g.W)} × {Layout.Mm(g.H)} · {ok} " + (cortina ? "painel(éis)" : c.IsPorta ? "porta(s)" : "janela(s)"));
            }

            if (falhas == 0 && avisos.Count == 0 && trocados > 0) return Result.Succeeded;

            var sb = new StringBuilder();
            string oque = cortina ? "painel(éis) da parede cortina" : cfg.IsPorta ? "porta(s)" : "janela(s)";
            sb.AppendLine(trocados > 0 ? $"{trocados} {oque} trocado(s) por esquadrias JA." : "Nada foi trocado.");
            foreach (string f in feitos) sb.AppendLine("• " + f);
            if (falhas > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"{falhas} {oque} não aceitaram a troca (detalhes no registro). Selecione-os e escolha o tipo em Propriedades.");
            }
            if (avisos.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Avisos:");
                foreach (string w in avisos.Distinct()) sb.AppendLine("• " + w);
            }
            var kind = falhas > 0 || avisos.Count > 0 ? ResultKind.Warning : ResultKind.Success;
            if (rfa != null) Show(sb.ToString(), kind, OpenFolderAction(rfa));
            else Show(sb.ToString(), kind);
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.Manual)]
    public sealed class NovaJanelaCommand : EsquadriaCommandBase
    {
        public override string Title => "Nova Janela";
        protected override Result Run() => OpenConfigurator(Start(TipoEsquadria.Janela), FonteCatalogo.Janelas);
    }

    [Transaction(TransactionMode.Manual)]
    public sealed class NovaPortaCommand : EsquadriaCommandBase
    {
        public override string Title => "Nova Porta";
        protected override Result Run() => OpenConfigurator(Start(TipoEsquadria.Porta), FonteCatalogo.Portas);
    }

    [Transaction(TransactionMode.Manual)]
    public sealed class BibliotecaCommand : EsquadriaCommandBase
    {
        public override string Title => "Biblioteca";
        protected override Result Run() => OpenConfigurator(Start(TipoEsquadria.Janela), FonteCatalogo.Biblioteca);
    }

    /// <summary>
    /// Editar Família: reopens the configurator of any JA family (window, door, bancada, nicho,
    /// chapim) from the selection or a click; updating rebuilds the type everywhere.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class EditarEsquadriaCommand : EsquadriaCommandBase
    {
        public override string Title => "Editar Família";

        private static bool IsJa(Element e) => ProjectEsquadrias.ConfigOf(e, out _) != null || ProjectPecas.ConfigOf(e, out _) != null ||
                                              PainelGenerator.ConfigOf(e, out _) != null;

        protected override Result Run()
        {
            // a JA guarda-corpo made as a Revit railing: its own dialog, then the railings are made again
            var gcs = UiDoc.Selection.GetElementIds().Select(id => Doc.GetElement(id)).OfType<Autodesk.Revit.DB.Architecture.Railing>()
                .Where(r => JaDados.Ler(r)?.Tipo == "guardacorpo").ToList();
            if (gcs.Count > 0) return EditarGuardaCorpos(gcs);
            Element target = null;
            foreach (ElementId id in UiDoc.Selection.GetElementIds())
            {
                Element e = Doc.GetElement(id);
                if (IsJa(e)) { target = e; break; }
            }
            if (target == null)
            {
                if (!ProjectEsquadrias.All(Doc).Any() && !ProjectPecas.All(Doc).Any() && !PainelGenerator.All(Doc).Any())
                {
                    Show("Nenhuma família do JA Famílias neste projeto.\nCrie uma (janela, porta, bancada, nicho, chapim ou painel de cortina); depois ela pode ser editada aqui.", ResultKind.Info);
                    return Result.Cancelled;
                }
                Reference r = UiDoc.Selection.PickObject(ObjectType.Element, new JaFilter(), "Clique numa janela, porta, bancada, nicho, chapim ou painel de cortina do JA Famílias (Esc cancela)");
                target = Doc.GetElement(r);
            }

            // curtain panel: every size of its code is regenerated
            Core.Paineis.PainelConfig painel = PainelGenerator.ConfigOf(target, out _);
            if (painel != null) return AbrirPainel(new List<Element>(), painel);

            IPecaConfig peca = ProjectPecas.ConfigOf(target, out FamilySymbol pecaSymbol);
            // a countertop modelled in place goes back to its own editor (outline, areas, sinks in the project)
            if (peca is BancadaConfig local && local.Livre && pecaSymbol != null) return AbrirBancadaLocal(local, pecaSymbol, target as FamilyInstance);
            // guarda-corpo / box: their own editor
            if (peca is VidroConfig vidro && pecaSymbol != null) return AbrirVidro(vidro, pecaSymbol);
            // painel ripado / pergolado: their own dialog
            if (peca is MadeiraConfig madeira && pecaSymbol != null) return AbrirMadeira(madeira, pecaSymbol);
            if (peca != null && pecaSymbol != null) return OpenPeca(peca, pecaSymbol);

            EsquadriaConfig cfg = ProjectEsquadrias.ConfigOf(target, out FamilySymbol symbol);
            if (cfg == null || symbol == null)
            {
                Show("Esta família não foi criada pelo JA Famílias, então não pode ser reaberta no configurador.", ResultKind.Info);
                return Result.Cancelled;
            }
            if (cfg.ParedeCortina)
            {
                // curtain panel: the size is the panel's
                if (TamanhoPainel(target, out double pw, out double ph)) { cfg.LarguraMm = pw; cfg.AlturaMm = ph; }
            }
            else
            {
                // the preview uses the host wall; the family itself follows any wall
                double host = ProjectEsquadrias.HostThicknessMm(target);
                if (host > 0) cfg.ParedeMm = Math.Round(host);
            }
            return OpenConfigurator(cfg, cfg.IsPorta ? FonteCatalogo.Portas : FonteCatalogo.Janelas, symbol);
        }

        private sealed class JaFilter : ISelectionFilter
        {
            public bool AllowElement(Element e) => IsJa(e);
            public bool AllowReference(Reference reference, XYZ position) => false;
        }
    }

    /// <summary>
    /// Parede Cortina: select curtain wall panels (or pick them), choose window or door; the
    /// configurator opens with the panel size, and generating swaps the panels to the new family.
    /// Panels of other sizes get one code per size.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class EsquadriaParedeCortinaCommand : EsquadriaCommandBase
    {
        public override string Title => "Parede Cortina";

        protected override Result Run()
        {
            var paineis = UiDoc.Selection.GetElementIds().Select(id => Doc.GetElement(id)).Where(EmParedeCortina).ToList();
            if (paineis.Count == 0)
            {
                IList<Reference> refs;
                try
                {
                    refs = UiDoc.Selection.PickObjects(ObjectType.Element, new PainelFilter(),
                        "Selecione os painéis da parede cortina (Tab alterna entre parede e painel) e clique em Concluir");
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
                paineis = refs.Select(r => Doc.GetElement(r)).Where(e => e != null).ToList();
            }
            paineis = paineis.GroupBy(p => p.Id).Select(g => g.First()).ToList();
            if (paineis.Count == 0) return Result.Cancelled;

            var medidas = paineis.Select(p => { TamanhoPainel(p, out double w, out double h); return new { W = w, H = h }; })
                                 .Where(m => m.W > 1 && m.H > 1).ToList();
            if (medidas.Count == 0)
            {
                Show("Não consegui ler a largura e a altura dos painéis selecionados.", ResultKind.Warning);
                return Result.Cancelled;
            }

            var td = new TaskDialog(Title)
            {
                MainInstruction = paineis.Count == 1 ? "Trocar o painel por:" : $"Trocar os {paineis.Count} painéis por:",
                MainContent = medidas.Select(m => Layout.Mm(m.W) + " × " + Layout.Mm(m.H)).Distinct().Count() > 1
                    ? "Os painéis têm tamanhos diferentes: cada tamanho ganha um código."
                    : "Tamanho do painel: " + Layout.Mm(medidas[0].W) + " × " + Layout.Mm(medidas[0].H) + " mm.",
                CommonButtons = TaskDialogCommonButtons.Cancel
            };
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Janela", "Janela de parede cortina (fixa, maxim-ar, de correr...).");
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Porta", "Porta de parede cortina (de abrir, de correr, pivotante...).");
            TaskDialogResult r = td.Show();
            TipoEsquadria tipo;
            if (r == TaskDialogResult.CommandLink1) tipo = TipoEsquadria.Janela;
            else if (r == TaskDialogResult.CommandLink2) tipo = TipoEsquadria.Porta;
            else return Result.Cancelled;

            // the configurator opens with the most common size
            var comum = medidas.GroupBy(m => Math.Round(m.W) + "x" + Math.Round(m.H)).OrderByDescending(g => g.Count()).First().First();
            EsquadriaConfig cfg = Start(tipo, true);
            cfg.LarguraMm = comum.W;
            cfg.AlturaMm = comum.H;
            return OpenConfigurator(cfg, tipo == TipoEsquadria.Porta ? FonteCatalogo.Portas : FonteCatalogo.Janelas, null, paineis);
        }

        private sealed class PainelFilter : ISelectionFilter
        {
            public bool AllowElement(Element e) => EmParedeCortina(e);
            public bool AllowReference(Reference reference, XYZ position) => false;
        }
    }
}
