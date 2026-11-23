using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using JAEsquadrias.Core;
using JAEsquadrias.Engine;
using JAEsquadrias.UI;
using Settings = JAEsquadrias.Core.Settings;

namespace JAEsquadrias.Commands
{
    /// <summary>
    /// Bancada no Local (shared with Editar Família): a countertop modelled where it goes. The
    /// outline is clicked in the plan (or comes from selected lines), the edges that touch walls
    /// and columns are found, and the editor opens; from it the user goes back to the project to
    /// divide dry / wet areas, place sinks and the cooktop, or redraw the outline. The family is
    /// generated with that exact outline and placed where it was drawn.
    /// Family coordinates: the project's X / Y directions, origin at the first corner, on the level.
    /// </summary>
    public abstract partial class EsquadriaCommandBase
    {
        private const ObjectSnapTypes SnapsBancada = ObjectSnapTypes.Endpoints | ObjectSnapTypes.Midpoints | ObjectSnapTypes.Intersections |
                                                     ObjectSnapTypes.Nearest | ObjectSnapTypes.Perpendicular;
        private const string TituloBancadaLocal = "Bancada no Local";

        protected Result AbrirBancadaLocal(BancadaConfig editar = null, FamilySymbol editing = null, FamilyInstance inst = null)
        {
            if (!(Doc.ActiveView is ViewPlan vista) || vista.IsTemplate || vista.GenLevel == null)
            {
                Show("Abra a planta baixa do pavimento onde a bancada fica e rode de novo.\nA bancada no local é desenhada (e editada) clicando pontos na planta.", ResultKind.Info);
                return Result.Cancelled;
            }
            bool isEdit = editar != null && editing != null;
            var settings = Settings.Current;
            HashSet<string> usados = ProjectPecas.UsedCodes(Doc, TipoPeca.Bancada);
            BancadaConfig cfg;
            Transform T;                 // family (feet) → project
            string nota = null;

            if (isEdit)
            {
                var copias = new FilteredElementCollector(Doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                    .Where(i => i.Symbol != null && i.Symbol.Id == editing.Id).ToList();
                if (inst == null || !inst.IsValidObject || inst.Symbol == null || inst.Symbol.Id != editing.Id) inst = copias.FirstOrDefault();
                if (inst == null)
                {
                    Show("A bancada " + editar.Codigo + " não tem nenhuma cópia no modelo: não há onde editá-la no local.\nColoque o tipo a partir do Navegador de Projeto, ou crie outra com Bancada no Local.", ResultKind.Info);
                    return Result.Cancelled;
                }
                T = inst.GetTransform();
                cfg = (BancadaConfig)editar.Copiar();
                usados.Remove(cfg.Codigo);
                if (copias.Count > 1) nota = "Esta bancada tem " + copias.Count + " cópias no modelo: todas mudam juntas. Os cliques no projeto valem para a cópia selecionada.";
            }
            else
            {
                List<XYZ> mundo = ContornoDaBancada(vista, null, null, out string erro);
                if (mundo == null)
                {
                    if (CroquiAtual?.Iniciado == true) return Result.Succeeded;   // the Revit tool is open: back when run again
                    if (erro != null) Show(erro, ResultKind.Warning);
                    return Result.Cancelled;
                }
                T = Transform.CreateTranslation(new XYZ(mundo[0].X, mundo[0].Y, vista.GenLevel.ProjectElevation));
                cfg = NovaBancadaLocal(usados);
                AplicarContorno(cfg, mundo, T);
            }

            var materiais = new FilteredElementCollector(Doc).OfClass(typeof(Material)).Cast<Material>().Select(m => m.Name).ToList();
            List<KeyValuePair<P2, P2>> contexto = null;
            while (true)
            {
                if (contexto == null) contexto = ContextoDaBancada(cfg, T);
                BancadaLocalPedido ped = BancadaLocalWindow.Abrir(Owner, TituloBancadaLocal, cfg, contexto, materiais, usados, isEdit, nota);
                if (ped == null) return Result.Cancelled;
                cfg = ped.Config;
                nota = null;
                if (!cfg.Livre) { Show("O contorno da bancada ficou inválido. Rode Bancada no Local de novo.", ResultKind.Warning); return Result.Cancelled; }
                if (ped.Acao == AcaoBancadaLocal.Gerar) break;
                try
                {
                    nota = NoProjeto(ped.Acao, cfg, vista, T);
                    if (ped.Acao == AcaoBancadaLocal.Contorno) contexto = null;
                }
                catch (Exception ex)
                {
                    Logger.Warn("[JA Famílias] Bancada no local (" + ped.Acao + "): " + ex);
                    nota = "Não deu para concluir no projeto: " + ex.Message;
                }
            }

            ToolPrefs.Set("ultima.bancada.local", cfg.ToJson());
            ToolPrefs.Save();
            GenerationResult gen = PecaGenerator.Generate(UiApp.Application, Doc, cfg, settings,
                isEdit ? editing.Family?.Name : null, isEdit ? editing.Name : null);
            var warnings = new List<string>(gen.Warnings);

            if (!isEdit)
            {
                if (gen.Symbol == null) throw new InvalidOperationException("A família foi gerada (" + gen.RfaPath + ") mas o tipo não apareceu no projeto.");
                ElementId novo;
                using (var t = new Transaction(Doc, "JA Famílias – bancada no local"))
                {
                    t.Start();
                    var opts = t.GetFailureHandlingOptions();
                    opts.SetFailuresPreprocessor(new WarningSwallower());
                    t.SetFailureHandlingOptions(opts);
                    if (!gen.Symbol.IsActive) gen.Symbol.Activate();
                    Level nivel = vista.GenLevel;
                    var alvo = new XYZ(T.Origin.X, T.Origin.Y, nivel.ProjectElevation);
                    FamilyInstance fi = Doc.Create.NewFamilyInstance(alvo, gen.Symbol, nivel, StructuralType.NonStructural);
                    Doc.Regenerate();
                    // exactly where it was drawn, whatever the template does with the level offset
                    XYZ delta = alvo - fi.GetTransform().Origin;
                    if (delta.GetLength() > 0.5 * MmFt) ElementTransformUtils.MoveElement(Doc, fi.Id, delta);
                    novo = fi.Id;
                    ApagarCroqui(CroquiAtual);
                    t.Commit();
                }
                try { UiDoc.Selection.SetElementIds(new List<ElementId> { novo }); } catch { }
                if (warnings.Count == 0) return Result.Succeeded;        // quiet when all went well
            }

            var sb = new StringBuilder();
            sb.AppendLine(isEdit ? $"Bancada {cfg.Codigo} atualizada no lugar." : $"Bancada {cfg.Codigo} gerada e colocada onde foi desenhada.");
            sb.AppendLine(cfg.Descricao());
            sb.AppendLine();
            sb.AppendLine("Arquivo da família:");
            sb.AppendLine(gen.RfaPath);
            if (warnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Avisos:");
                foreach (string w in warnings.Distinct()) sb.AppendLine("• " + w);
            }
            Show(sb.ToString(), warnings.Count > 0 ? ResultKind.Warning : ResultKind.Success, OpenFolderAction(gen.RfaPath));
            return Result.Succeeded;
        }

        // ------------------------------------------------------------------ configuration
        /// <summary>A new free-form countertop with the office's stone and edges (from the last one made, in place or not).</summary>
        private static BancadaConfig NovaBancadaLocal(HashSet<string> usados)
        {
            BancadaConfig b = null;
            string json = ToolPrefs.Get("ultima.bancada.local");
            if (!string.IsNullOrWhiteSpace(json)) b = PecaUtil.FromJson(TipoPeca.Bancada, json) as BancadaConfig;
            bool primeira = b == null;
            if (b == null) b = StartPeca(TipoPeca.Bancada) as BancadaConfig ?? new BancadaConfig();
            b = (BancadaConfig)b.Copiar();
            b.Nome = "";
            b.ModeloId = "";
            b.Cubas = new List<Cuba>();
            b.Cooktop = false;
            b.Divisoes = new List<Divisao>();
            b.Molhadas = new List<Pt>();
            b.MolhadaDeMm = 0;
            b.MolhadaAteMm = 0;
            if (primeira) { b.RebaixoMm = 10; b.ContencaoMm = 0; }
            b.Codigo = ProjectPecas.NextCode(usados, TipoPeca.Bancada);
            return b;
        }

        private static Pt LocalDe(Transform T, XYZ p)
        {
            XYZ q = T.Inverse.OfPoint(new XYZ(p.X, p.Y, T.Origin.Z));
            return new Pt(Math.Round(q.X / MmFt, 2), Math.Round(q.Y / MmFt, 2));
        }

        private static XYZ MundoDe(Transform T, P2 p, double z)
        {
            XYZ q = T.OfPoint(new XYZ(p.X * MmFt, p.Y * MmFt, 0));
            return new XYZ(q.X, q.Y, z);
        }

        /// <summary>Height (project Z) where walls and columns are looked for: the middle of the stone.</summary>
        private static double AlturaDoTampo(BancadaConfig cfg, Transform T) => T.Origin.Z + (cfg.AlturaMm - cfg.EspessuraMm / 2) * MmFt;

        /// <summary>The outline becomes the countertop's; edges along walls and columns get the frontão, the others the saia.</summary>
        private void AplicarContorno(BancadaConfig cfg, List<XYZ> mundo, Transform T)
        {
            cfg.Contorno = mundo.Select(p => LocalDe(T, p)).ToList();
            var faces = EntornoBancada.Faces(Doc, mundo, AlturaDoTampo(cfg, T));
            cfg.Arestas = EntornoBancada.Detectar(mundo, faces);
            cfg.Formato = FormatoBancada.Livre;
        }

        /// <summary>Walls and columns around the countertop, in its own coordinates (mm), for the editor's plan.</summary>
        private List<KeyValuePair<P2, P2>> ContextoDaBancada(BancadaConfig cfg, Transform T)
        {
            var res = new List<KeyValuePair<P2, P2>>();
            try
            {
                var mundo = cfg.Contorno.Select(q => MundoDe(T, q.P, 0)).ToList();
                foreach (FaceEntorno f in EntornoBancada.Faces(Doc, mundo, AlturaDoTampo(cfg, T)))
                    res.Add(new KeyValuePair<P2, P2>(LocalDe(T, f.A).P, LocalDe(T, f.B).P));
            }
            catch (Exception ex) { Logger.Warn("[JA Famílias] Contexto da bancada: " + ex.Message); }
            return res;
        }

        // ------------------------------------------------------------------ back in the project
        /// <summary>One step in the project asked from the editor; returns the note shown when it opens again.</summary>
        private string NoProjeto(AcaoBancadaLocal acao, BancadaConfig cfg, ViewPlan vista, Transform T)
        {
            var contorno = cfg.Contorno.Select(q => q.P).ToList();
            switch (acao)
            {
                case AcaoBancadaLocal.Contorno:
                {
                    List<XYZ> mundo = ContornoDaBancada(vista, cfg, T, out string erro);
                    if (mundo == null) return erro ?? "Contorno mantido.";
                    int divisoes = cfg.Divisoes.Count;
                    AplicarContorno(cfg, mundo, T);
                    cfg.Divisoes.Clear();
                    cfg.Molhadas.Clear();
                    return "Contorno redesenhado; confira as bordas." + (divisoes > 0 ? " As divisões de áreas foram removidas: refaça com “Dividir áreas”." : "");
                }
                case AcaoBancadaLocal.Divisao:
                {
                    List<ZonaBancada> zonas = cfg.Zonas();
                    // the line ends by itself when a click (after the first) lands on an edge
                    List<XYZ> pts = ClicarPontos(vista, cfg, T,
                        n => n == 0 ? "Dividir áreas: clique o 1º ponto numa borda do tampo (Esc cancela)" : "clique o próximo ponto; a linha termina ao clicar na outra borda (ou Esc)",
                        false, 0,
                        l => l.Count >= 2 && zonas.Any(z => Poligono.DistBorda(z.Pts, LocalDe(T, l[l.Count - 1]).P, out _, out _) <= 2));
                    if (pts.Count < 2) return "Divisão cancelada (são precisos dois pontos).";
                    List<P2> cam = cfg.AjustarDivisao(pts.Select(p => LocalDe(T, p).P).ToList(), out string erro);
                    if (cam == null) return "Divisão não criada: " + erro;
                    var d = new Divisao { Pontos = cam.Select(q => new Pt(Math.Round(q.X, 2), Math.Round(q.Y, 2))).ToList() };
                    cfg.Divisoes.Add(d);
                    var avisos = new List<string>();
                    if (cfg.Zonas(avisos).Count <= zonas.Count)
                    {
                        cfg.Divisoes.Remove(d);
                        return "Divisão não criada: " + (avisos.Count > 0 ? avisos[avisos.Count - 1] : "a linha não separa duas áreas.");
                    }
                    return "Tampo dividido. Clique numa área da planta, aqui na janela, para marcá-la como molhada (azul) ou seca.";
                }
                case AcaoBancadaLocal.Cuba:
                {
                    List<XYZ> pts = ClicarPontos(vista, cfg, T, n => "Cuba: clique o centro da cuba sobre o tampo (Esc cancela)", false, 1, null);
                    if (pts.Count == 0) return "Nenhuma cuba posicionada.";
                    P2 c = LocalDe(T, pts[0]).P;
                    if (!Poligono.Dentro(contorno, c)) return "O ponto clicado está fora do tampo: a cuba não foi criada.";
                    Cuba modelo = cfg.Cubas.LastOrDefault();
                    Cuba cu = modelo != null ? modelo.Clone()
                        : new Cuba { Tipo = TipoCuba.Embutir, LarguraMm = 500, ProfundidadeMm = 400, FundoMm = 180, RaioMm = 20, FuroTorneira = true };
                    cu.PosicaoXMm = c.X;
                    cu.PosicaoYMm = c.Y;
                    cu.RecuoFrenteMm = 0;
                    cu.AnguloGraus = AnguloParaParede(cfg, c);
                    cfg.Cubas.Add(cu);
                    return "Cuba " + cfg.Cubas.Count + " posicionada, com a torneira para o lado da parede mais próxima. Ajuste tamanho e ângulo no cartão da cuba.";
                }
                case AcaoBancadaLocal.Furo:
                {
                    List<XYZ> pts = ClicarPontos(vista, cfg, T, n => "Furos: clique o centro de cada furo sobre o tampo (Esc termina)", false, 0, null, false);
                    double diam = cfg.Furos.Count > 0 ? cfg.Furos[cfg.Furos.Count - 1].DiametroMm : 35;
                    int feitos = 0;
                    foreach (XYZ p in pts)
                    {
                        P2 c = LocalDe(T, p).P;
                        if (!Poligono.Dentro(contorno, c)) continue;
                        cfg.Furos.Add(new Furo { X = c.X, Y = c.Y, DiametroMm = diam });
                        feitos++;
                    }
                    return feitos == 0 ? "Nenhum furo criado (clique sobre o tampo)." : feitos + " furo(s) criado(s). Ajuste o diâmetro no cartão Furos.";
                }
                case AcaoBancadaLocal.Cooktop:
                {
                    List<XYZ> pts = ClicarPontos(vista, cfg, T, n => "Cooktop: clique o centro do recorte sobre o tampo (Esc cancela)", false, 1, null);
                    if (pts.Count == 0) return "Cooktop não posicionado.";
                    P2 c = LocalDe(T, pts[0]).P;
                    if (!Poligono.Dentro(contorno, c)) return "O ponto clicado está fora do tampo: o cooktop não foi criado.";
                    cfg.Cooktop = true;
                    cfg.CooktopPosicaoXMm = c.X;
                    cfg.CooktopPosicaoYMm = c.Y;
                    cfg.CooktopAnguloGraus = AnguloParaParede(cfg, c);
                    return "Recorte do cooktop posicionado. Ajuste as medidas no cartão Cooktop.";
                }
            }
            return null;
        }

        /// <summary>
        /// Rotation (degrees) that turns the back of a sink (+Y) to the nearest wall edge of the
        /// outline — the nearest edge of any kind when no wall edge is within 2 m.
        /// </summary>
        private static double AnguloParaParede(BancadaConfig cfg, P2 c)
        {
            var pts = cfg.Contorno.Select(q => q.P).ToList();
            int melhor = -1, melhorParede = -1;
            double d = double.MaxValue, dParede = double.MaxValue;
            for (int i = 0; i < pts.Count; i++)
            {
                double di = Poligono.DistSeg(c, pts[i], pts[(i + 1) % pts.Count], out _);
                if (di < d) { d = di; melhor = i; }
                bool parede = i < cfg.Arestas.Count && (cfg.Arestas[i] == BordaLocal.Parede || cfg.Arestas[i] == BordaLocal.ParedeSemFrontao);
                if (parede && di < dParede) { dParede = di; melhorParede = i; }
            }
            int k = melhorParede >= 0 && dParede <= 2000 ? melhorParede : melhor;
            if (k < 0) return 0;
            P2 a = pts[k], b = pts[(k + 1) % pts.Count];
            double len = Poligono.Dist(a, b);
            if (len < 1) return 0;
            double ux = (b.X - a.X) / len, uy = (b.Y - a.Y) / len;
            // from the centre straight to the line of the edge
            double t = (c.X - a.X) * ux + (c.Y - a.Y) * uy;
            double vx = a.X + ux * t - c.X, vy = a.Y + uy * t - c.Y;
            if (Math.Sqrt(vx * vx + vy * vy) < 1) { vx = -uy; vy = ux; }
            double ang = Math.Atan2(vy, vx) * 180 / Math.PI - 90;
            while (ang > 180) ang -= 360;
            while (ang <= -180) ang += 360;
            return Math.Round(ang, 1);
        }

        // ------------------------------------------------------------------ outline
        /// <summary>
        /// The outline of the top in the project (feet, on the plan's plane): from the lines
        /// selected before the command (detail or model lines forming one closed loop; arcs
        /// become short straight stretches), else clicked corner by corner. Null = cancelled
        /// (erro = why, when there is something to tell).
        /// </summary>
        private List<XYZ> ContornoDaBancada(ViewPlan vista, BancadaConfig guias, Transform T, out string erro)
        {
            erro = null;
            double z = PlanoDaVista(vista);
            if (guias == null)
            {
                // a room selected before the command: its boundary (finish faces) is the outline —
                // for tops that take the whole room, wall to wall (lavatory niches, service counters)
                var amb = UiDoc.Selection.GetElementIds().Select(id => Doc.GetElement(id))
                    .Select(e => e is Autodesk.Revit.DB.Architecture.RoomTag tg ? tg.Room : e as Autodesk.Revit.DB.Architecture.Room).FirstOrDefault(r => r != null);
                if (amb != null)
                {
                    List<XYZ> laco = ContornoDoAmbiente(amb, z);
                    if (laco == null) { erro = "Não consegui ler o contorno do ambiente “" + amb.Name + "” (ele está fechado e colocado?)."; return null; }
                    return ValidarContorno(laco, out erro);
                }
                // the same croqui window as every drawing tool: Revit tools, lines, edges or clicks (orto)
                CroquiAtual = EntradaDoCroqui("bancada", vista,
                    "Desenhe o contorno fechado do tampo (retângulo, polígono, linhas e arcos), contornando paredes e pilares. Tampo de parede a parede: selecione o ambiente antes de rodar.");
                if (CroquiAtual == null || CroquiAtual.Iniciado) return null;
                if (CroquiAtual.Curvas != null)
                {
                    List<XYZ> laco = Encadear(CroquiAtual.Curvas, z, out erro);
                    if (laco == null && CroquiAtual.Modo == ModoCroqui.Ferramentas) erro += "\nAs linhas desenhadas ficaram na vista: corrija e rode de novo com elas selecionadas.";
                    return laco == null ? null : ValidarContorno(laco, out erro);
                }
                if (ToolPrefs.Get("bancada.local.dica") != "0")
                {
                    var td = new TaskDialog(TituloBancadaLocal)
                    {
                        MainInstruction = "Clique os cantos do tampo na planta",
                        MainContent = "• Clique os cantos em sequência, contornando paredes e pilares (os snaps do Revit funcionam).\n" +
                                      "• Esc, ou um clique no 1º canto, fecha o contorno.\n" +
                                      "• Tampo de parede a parede, ocupando o ambiente todo (nicho, lavabo): selecione o ambiente antes e o contorno sai pronto.\n" +
                                      "• Formas com curvas ou muitos recortes: desenhe o contorno com linhas de detalhe, selecione as linhas e clique em Bancada no Local.\n" +
                                      "• Depois, na janela: bordas (saia, frontão), áreas seca e molhada, cubas e cooktop.",
                        CommonButtons = TaskDialogCommonButtons.Ok | TaskDialogCommonButtons.Cancel,
                        DefaultButton = TaskDialogResult.Ok,
                        VerificationText = "Não mostrar de novo"
                    };
                    TaskDialogResult r = td.Show();
                    if (td.WasVerificationChecked()) { ToolPrefs.Set("bancada.local.dica", "0"); ToolPrefs.Save(); }
                    if (r != TaskDialogResult.Ok) return null;
                }
            }
            if (guias != null) OrtoDasPreferencias("bancada");   // redrawing from the editor: the last orto of the croqui window
            List<XYZ> pts;
            try
            {
                pts = ClicarPontos(vista, guias, T,
                    n => n == 0 ? "Bancada no local: clique o 1º canto do tampo (Esc cancela)" : n < 3 ? "clique o próximo canto do tampo (Shift inverte o orto)" : "próximo canto — Esc (ou um clique no 1º canto) fecha o contorno; Shift inverte o orto",
                    true, 0, null);
            }
            catch (Exception ex)
            {
                Logger.Warn("[JA Famílias] Contorno da bancada: " + ex);
                erro = "Não deu para clicar pontos nesta vista (" + ex.Message + "). Use uma planta baixa.";
                return null;
            }
            if (pts.Count == 0) return null;
            if (pts.Count < 3) { erro = "O contorno precisa de pelo menos 3 cantos: nada foi alterado."; return null; }
            return ValidarContorno(pts, out erro);
        }

        /// <summary>Outer boundary of a room (finish faces) as corner points on the plane z; curved walls become short stretches.</summary>
        private static List<XYZ> ContornoDoAmbiente(Autodesk.Revit.DB.Architecture.Room amb, double z)
        {
            try
            {
                var op = new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish };
                IList<IList<BoundarySegment>> lacos = amb.GetBoundarySegments(op);
                if (lacos == null || lacos.Count == 0) return null;
                List<XYZ> melhor = null;
                double maior = 0;
                foreach (IList<BoundarySegment> laco in lacos)
                {
                    var pts = new List<XYZ>();
                    foreach (BoundarySegment seg in laco)
                    {
                        Curve cv = seg.GetCurve();
                        if (cv == null) continue;
                        IList<XYZ> t = cv is Line ? new List<XYZ> { cv.GetEndPoint(0), cv.GetEndPoint(1) } : cv.Tessellate();
                        for (int i = 0; i < t.Count - 1; i++)                   // the end of one stretch is the start of the next
                        {
                            var q = new XYZ(t[i].X, t[i].Y, z);
                            if (pts.Count == 0 || pts[pts.Count - 1].DistanceTo(q) > 0.003) pts.Add(q);
                        }
                    }
                    if (pts.Count > 1 && pts[0].DistanceTo(pts[pts.Count - 1]) < 0.003) pts.RemoveAt(pts.Count - 1);
                    if (pts.Count < 3) continue;
                    double a2 = 0;
                    for (int i = 0; i < pts.Count; i++) { XYZ p0 = pts[i], p1 = pts[(i + 1) % pts.Count]; a2 += p0.X * p1.Y - p1.X * p0.Y; }
                    if (Math.Abs(a2) > maior) { maior = Math.Abs(a2); melhor = pts; }
                }
                return melhor;
            }
            catch (Exception ex) { Logger.Warn("[JA Famílias] Contorno do ambiente: " + ex.Message); return null; }
        }

        private static double PlanoDaVista(ViewPlan vista)
        {
            try { if (vista.SketchPlane != null) return vista.SketchPlane.GetPlane().Origin.Z; } catch { }
            return vista.GenLevel.ProjectElevation;
        }

        private static double DistPlano(XYZ a, XYZ b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

        /// <summary>Joins the curves end to end into one closed loop (2 mm tolerance); curved ones as short chords.</summary>
        private static List<XYZ> Encadear(List<Curve> linhas, double z, out string erro)
        {
            erro = null;
            const string dica = "\nSelecione só as linhas do contorno (pontas coincidentes, sem sobras) ou rode sem seleção para clicar os cantos.";
            var trechos = new List<List<XYZ>>();
            foreach (Curve c in linhas)
            {
                IList<XYZ> t = c is Line ? new List<XYZ> { c.GetEndPoint(0), c.GetEndPoint(1) } : c.Tessellate();
                var pts = new List<XYZ>();
                for (int i = 0; i < t.Count; i++)
                {
                    var p = new XYZ(t[i].X, t[i].Y, z);
                    // curves: a point every 30 mm at least (the ends always stay)
                    if (pts.Count > 0 && i < t.Count - 1 && DistPlano(pts[pts.Count - 1], p) < 30 * MmFt) continue;
                    pts.Add(p);
                }
                if (pts.Count >= 2) trechos.Add(pts);
            }
            if (trechos.Count == 0) { erro = "As linhas selecionadas não têm geometria utilizável." + dica; return null; }
            double tol = 2 * MmFt;
            var laco = new List<XYZ>(trechos[0]);
            trechos.RemoveAt(0);
            while (trechos.Count > 0)
            {
                XYZ fim = laco[laco.Count - 1];
                int k = -1;
                bool inverter = false;
                for (int i = 0; i < trechos.Count && k < 0; i++)
                {
                    if (DistPlano(trechos[i][0], fim) < tol) k = i;
                    else if (DistPlano(trechos[i][trechos[i].Count - 1], fim) < tol) { k = i; inverter = true; }
                }
                if (k < 0) { erro = "As linhas selecionadas não formam um contorno fechado (há uma ponta solta ou mais de um contorno)." + dica; return null; }
                List<XYZ> s = trechos[k];
                if (inverter) s.Reverse();
                laco.AddRange(s.Skip(1));
                trechos.RemoveAt(k);
            }
            if (DistPlano(laco[0], laco[laco.Count - 1]) > tol) { erro = "As linhas selecionadas não fecham o contorno (a última ponta não encontra a primeira)." + dica; return null; }
            laco.RemoveAt(laco.Count - 1);
            return laco;
        }

        /// <summary>Repeated points out; at least 3 corners, some area, no crossing edges.</summary>
        private static List<XYZ> ValidarContorno(List<XYZ> pts, out string erro)
        {
            erro = null;
            var limpo = new List<XYZ>();
            foreach (XYZ p in pts) if (limpo.Count == 0 || DistPlano(limpo[limpo.Count - 1], p) > 1 * MmFt) limpo.Add(p);
            while (limpo.Count > 1 && DistPlano(limpo[0], limpo[limpo.Count - 1]) <= 1 * MmFt) limpo.RemoveAt(limpo.Count - 1);
            int n = limpo.Count;
            if (n < 3) { erro = "O contorno precisa de pelo menos 3 cantos diferentes."; return null; }
            double area = 0;
            for (int i = 0; i < n; i++) area += limpo[i].X * limpo[(i + 1) % n].Y - limpo[(i + 1) % n].X * limpo[i].Y;
            if (Math.Abs(area) / 2 < 0.01 / (0.3048 * 0.3048)) { erro = "O contorno é pequeno demais (menos de 0,01 m²)."; return null; }
            for (int i = 0; i < n; i++)
                for (int j = i + 2; j < n; j++)
                {
                    if (i == 0 && j == n - 1) continue;          // neighbours
                    if (Cruzam(limpo[i], limpo[(i + 1) % n], limpo[j], limpo[(j + 1) % n]))
                    {
                        erro = "O contorno se cruza (as bordas " + (i + 1) + " e " + (j + 1) + " se atravessam). Clique os cantos em sequência, dando a volta no tampo.";
                        return null;
                    }
                }
            return limpo;
        }

        private static bool Cruzam(XYZ a, XYZ b, XYZ c, XYZ d)
        {
            double O(XYZ p, XYZ q, XYZ r) => (q.X - p.X) * (r.Y - p.Y) - (q.Y - p.Y) * (r.X - p.X);
            double o1 = O(a, b, c), o2 = O(a, b, d), o3 = O(c, d, a), o4 = O(c, d, b);
            const double eps = 1e-9;
            return ((o1 > eps && o2 < -eps) || (o1 < -eps && o2 > eps)) && ((o3 > eps && o4 < -eps) || (o3 < -eps && o4 > eps));
        }

        // ------------------------------------------------------------------ clicks and guides
        /// <summary>
        /// Points clicked in the plan (with snaps) until Esc, <paramref name="max"/> points, a
        /// click back on the first point (closed outlines) or <paramref name="fim"/> says so.
        /// While clicking, the countertop as it is (<paramref name="guias"/>) and the stretches
        /// already clicked are shown as temporary lines — everything is rolled back at the end.
        /// </summary>
        protected List<XYZ> ClicarPontos(ViewPlan vista, BancadaConfig guias, Transform T, Func<int, string> prompt, bool fechado, int max, Func<List<XYZ>, bool> fim, bool ligar = true)
        {
            var pts = new List<XYZ>();
            double z = PlanoDaVista(vista);
            using (var tg = new TransactionGroup(Doc, "JA Famílias – modelar no local"))
            {
                tg.Start();
                try
                {
                    if (guias != null && T != null) Guias(vista, guias, T, z);
                    while (max <= 0 || pts.Count < max)
                    {
                        XYZ p;
                        try { p = UiDoc.Selection.PickPoint(SnapsBancada, prompt(pts.Count)); }
                        catch (Autodesk.Revit.Exceptions.OperationCanceledException) { break; }
                        p = new XYZ(p.X, p.Y, z);
                        if (fechado && pts.Count >= 3 && DistPlano(p, pts[0]) < 3 * MmFt) break;
                        // orto (window option; Shift inverts it): only for chained clicks
                        if (ligar && pts.Count > 0) { XYZ q = AplicarOrto(pts[pts.Count - 1], p); p = new XYZ(q.X, q.Y, z); }
                        if (pts.Count > 0 && DistPlano(p, pts[pts.Count - 1]) < 1 * MmFt) continue;
                        pts.Add(p);
                        if (pts.Count > 1 && ligar) Tracos(vista, new List<KeyValuePair<XYZ, XYZ>> { new KeyValuePair<XYZ, XYZ>(pts[pts.Count - 2], p) });
                        if (fim != null && fim(pts)) break;
                    }
                }
                finally
                {
                    try { tg.RollBack(); }
                    catch (Exception ex) { Logger.Warn("[JA Famílias] Linhas-guia não removidas: " + ex.Message); }
                }
            }
            return pts;
        }

        /// <summary>The countertop as it is now (outline, dividing lines, sinks, cooktop) as temporary lines to snap to.</summary>
        private void Guias(ViewPlan vista, BancadaConfig cfg, Transform T, double z)
        {
            var segs = new List<KeyValuePair<XYZ, XYZ>>();
            void Laco(List<P2> l, bool fechar)
            {
                for (int i = 0; i + 1 < l.Count || (fechar && i < l.Count); i++)
                    segs.Add(new KeyValuePair<XYZ, XYZ>(MundoDe(T, l[i], z), MundoDe(T, l[(i + 1) % l.Count], z)));
            }
            Laco(cfg.Contorno.Select(q => q.P).ToList(), true);
            foreach (Divisao d in cfg.Divisoes) Laco(d.Pontos.Select(q => q.P).ToList(), false);
            foreach (Cuba cu in cfg.Cubas)
                Laco(Poligono.Girar(Peca.Rect(cu.PosicaoXMm - cu.LarguraMm / 2, cu.PosicaoYMm - cu.ProfundidadeMm / 2, cu.PosicaoXMm + cu.LarguraMm / 2, cu.PosicaoYMm + cu.ProfundidadeMm / 2),
                                    cu.PosicaoXMm, cu.PosicaoYMm, cu.AnguloGraus), true);
            if (cfg.Cooktop)
                Laco(Poligono.Girar(Peca.Rect(cfg.CooktopPosicaoXMm - cfg.CooktopLarguraMm / 2, cfg.CooktopPosicaoYMm - cfg.CooktopProfundidadeMm / 2,
                                              cfg.CooktopPosicaoXMm + cfg.CooktopLarguraMm / 2, cfg.CooktopPosicaoYMm + cfg.CooktopProfundidadeMm / 2),
                                    cfg.CooktopPosicaoXMm, cfg.CooktopPosicaoYMm, cfg.CooktopAnguloGraus), true);
            Tracos(vista, segs);
        }

        /// <summary>Temporary detail lines (magenta, thick) in the plan; they live until the transaction group is rolled back.</summary>
        private void Tracos(ViewPlan vista, List<KeyValuePair<XYZ, XYZ>> segs)
        {
            if (segs == null || segs.Count == 0) return;
            using (var t = new Transaction(Doc, "JA Famílias – linhas-guia"))
            {
                try
                {
                    t.Start();
                    var ogs = new OverrideGraphicSettings();
                    ogs.SetProjectionLineColor(new Color(230, 0, 126));
                    ogs.SetProjectionLineWeight(5);
                    foreach (var s in segs)
                    {
                        if (s.Key.DistanceTo(s.Value) < 1 * MmFt) continue;
                        try
                        {
                            DetailCurve dc = Doc.Create.NewDetailCurve(vista, Line.CreateBound(s.Key, s.Value));
                            if (dc != null) vista.SetElementOverrides(dc.Id, ogs);
                        }
                        catch (Exception ex) { Logger.Info("[JA Famílias] Linha-guia: " + ex.Message); }
                    }
                    t.Commit();
                }
                catch (Exception ex)
                {
                    Logger.Warn("[JA Famílias] Linhas-guia: " + ex.Message);
                    try { if (t.HasStarted() && !t.HasEnded()) t.RollBack(); } catch { }
                }
            }
            try { UiDoc.RefreshActiveView(); } catch { }
        }
    }

    /// <summary>
    /// Bancada no Local: a countertop of any shape modelled where it goes — the outline is
    /// clicked in the plan around walls and columns (or comes from selected lines), with dry and
    /// wet areas, sinks and cooktop placed by clicking in the project.
    /// With a countertop made this way selected, it opens for editing.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class BancadaLocalCommand : EsquadriaCommandBase
    {
        public override string Title => "Bancada no Local";

        protected override Result Run()
        {
            foreach (ElementId id in UiDoc.Selection.GetElementIds())
            {
                Element e = Doc.GetElement(id);
                if (ProjectPecas.ConfigOf(e, out FamilySymbol s) is BancadaConfig b && b.Livre && s != null)
                    return AbrirBancadaLocal(b, s, e as FamilyInstance);
            }
            return AbrirBancadaLocal();
        }
    }
}
