using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using JAEsquadrias.Core;
using JAEsquadrias.Engine;
using JAEsquadrias.UI;
using Settings = JAEsquadrias.Core.Settings;

namespace JAEsquadrias.Commands
{
    /// <summary>
    /// Flow of Nova Bancada / Novo Nicho / Novo Chapim (and of Editar Família on a piece):
    /// configurator → generate + load the family → place (bancada, nicho) or apply on top of
    /// walls (chapim: one type per wall thickness) → result.
    /// </summary>
    public abstract partial class EsquadriaCommandBase
    {
        protected Result OpenPeca(IPecaConfig start, FamilySymbol editing = null, Func<FamilySymbol, int> colocar = null)
        {
            var settings = Settings.Current;
            TipoPeca tipo = start.Tipo;
            var all = ProjectPecas.All(Doc, tipo);
            var used = new HashSet<string>(all.Select(a => a.Codigo), StringComparer.OrdinalIgnoreCase);
            bool isEdit = editing != null;
            if (isEdit) used.Remove(start.Codigo);
            else if (used.Contains(start.Codigo)) start.Codigo = ProjectPecas.NextCode(used, tipo);

            // chapim: walls selected before the click are the ones that get it
            List<Wall> preWalls = tipo == TipoPeca.Chapim && !isEdit ? SelectedWalls() : new List<Wall>();
            if (preWalls.Count > 0 && start is ChapimConfig cs)
                cs.EspessuraMuroMm = preWalls.GroupBy(w => ChapimPlacer.ThicknessMm(w)).OrderByDescending(g => g.Count()).First().Key;

            // countertops modelled in place have their own editor (Bancada no Local)
            var items = all.Where(a => !(a.Config is BancadaConfig bl && bl.Livre)).Select(a => new KeyValuePair<string, IPecaConfig>(
                a.Codigo + " · " + (a.Instancias == 1 ? "1 no modelo" : a.Instancias + " no modelo"), a.Config)).ToList();
            var materials = new FilteredElementCollector(Doc).OfClass(typeof(Material)).Cast<Material>().Select(m => m.Name).ToList();

            // a curved chapim is edited as its section (the preview draws it straight); radius, side and angle are kept
            ChapimConfig curvo = start is ChapimConfig sc0 && sc0.Curvo ? (ChapimConfig)sc0.Copiar() : null;
            if (curvo != null) ((ChapimConfig)start).RaioMm = 0;

            PecaResult res = PecaWindow.Show(Owner, Title, start, isEdit, used, items, materials);
            if (res == null) return Result.Cancelled;
            IPecaConfig cfg = res.Config;
            if (curvo != null && cfg is ChapimConfig cc0)
            {
                cc0.RaioMm = curvo.RaioMm;
                cc0.ExteriorParaFora = curvo.ExteriorParaFora;
                cc0.AnguloGraus = curvo.AnguloGraus;
                cc0.EspessuraMuroMm = curvo.EspessuraMuroMm;
                res.Place = false;      // the copies already sit on their walls
            }
            if (curvo == null) ToolPrefs.Set("ultima.peca." + cfg.Tipo, cfg.ToJson());
            ToolPrefs.Save();

            GenerationResult gen = PecaGenerator.Generate(UiApp.Application, Doc, cfg, settings,
                isEdit ? editing.Family?.Name : null, isEdit ? editing.Name : null);
            var warnings = new List<string>(gen.Warnings);
            var extra = new List<string>();

            // editing a chapim also rebuilds its per-thickness types (CH01-200, ...)
            if (isEdit && cfg is ChapimConfig ce)
            {
                foreach (var v in ProjectPecas.All(Doc, TipoPeca.Chapim).Where(p => p.Codigo.StartsWith(ce.Codigo + "-", StringComparison.OrdinalIgnoreCase) && p.Config is ChapimConfig))
                {
                    var vc = (ChapimConfig)ce.Copiar();
                    vc.Codigo = v.Codigo;
                    var vo = (ChapimConfig)v.Config;
                    vc.EspessuraMuroMm = vo.EspessuraMuroMm;
                    vc.RaioMm = vo.RaioMm;
                    vc.ExteriorParaFora = vo.ExteriorParaFora;
                    vc.AnguloGraus = vo.AnguloGraus;
                    try
                    {
                        var g = PecaGenerator.Generate(UiApp.Application, Doc, vc, settings, v.Symbol.Family?.Name, v.Symbol.Name);
                        extra.Add(g.FamilyName);
                        warnings.AddRange(g.Warnings);
                    }
                    catch (Exception ex) { warnings.Add(v.Codigo + " não atualizado: " + ex.Message); }
                }
            }

            int placed = 0;
            string problem = null;
            if (res.Place && gen.Symbol != null)
            {
                if (cfg is ChapimConfig cc) placed = ApplyChapim(cc, gen, preWalls, settings, warnings, extra, out problem);
                else if (colocar != null && !isEdit)
                {
                    try { placed = colocar(gen.Symbol); }
                    catch (Exception ex) { problem = "Não deu para colocar no lugar clicado: " + ex.Message; }
                }
                else placed = Place(gen.Symbol, out problem);
            }
            // nichos: make sure every copy cuts its wall (also the ones placed before this version)
            if (cfg.Tipo == TipoPeca.Nicho && gen.Symbol != null) EnsureNicheCuts(gen.Symbol, warnings);

            bool quiet = res.Place && placed > 0 && warnings.Count == 0 && extra.Count == 0;
            if (quiet) return Result.Succeeded;

            string nome = PecaUtil.NomeTipo(cfg.Tipo);
            var sb = new StringBuilder();
            if (isEdit) sb.AppendLine($"{nome} {cfg.Codigo} atualizada em todo o modelo.");
            else if (placed > 0) sb.AppendLine(cfg.Tipo == TipoPeca.Chapim ? $"Chapim {cfg.Codigo} aplicado em {placed} parede(s)." : $"{nome} {cfg.Codigo} gerada e colocada {placed} vez(es).");
            else sb.AppendLine($"{nome} {cfg.Codigo} gerada e carregada no projeto.");
            sb.AppendLine(cfg.Descricao());
            if (extra.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine(isEdit ? "Tipos por espessura de muro também atualizados:" : "Tipos criados para outras espessuras de muro:");
                foreach (string e in extra.Distinct()) sb.AppendLine("• " + e);
            }
            if (!isEdit && placed == 0)
            {
                sb.AppendLine();
                sb.AppendLine(res.Place && problem != null ? problem
                    : "Para colocar: Navegador de Projeto › Famílias › " + PecaUtil.Categoria(cfg.Tipo) + " › " + gen.FamilyName + ", arraste o tipo " + gen.TypeName + " para a vista" +
                      (cfg.Tipo == TipoPeca.Nicho ? " (sobre uma parede)." : cfg.Tipo == TipoPeca.Chapim ? " (ou use Novo Chapim com as paredes selecionadas)." : "."));
            }
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

        /// <summary>
        /// Fallback when the void of a niche family could not cut the family's own wall: the
        /// project cuts each host wall with the instance ("Cortar com vazios"). Instances whose
        /// family already cuts the host have no unattached void and are skipped.
        /// </summary>
        private void EnsureNicheCuts(FamilySymbol s, List<string> warnings)
        {
            var insts = new FilteredElementCollector(Doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                .Where(i => i.Symbol != null && i.Symbol.Id == s.Id && i.Host is Wall).ToList();
            if (insts.Count == 0) return;
            int failed = 0;
            try
            {
                using (var t = new Transaction(Doc, "JA Famílias – nichos cortam a parede"))
                {
                    t.Start();
                    var opts = t.GetFailureHandlingOptions();
                    opts.SetFailuresPreprocessor(new WarningSwallower());
                    t.SetFailureHandlingOptions(opts);
                    foreach (var fi in insts)
                    {
                        Element wall = fi.Host;
                        try
                        {
                            if (!InstanceVoidCutUtils.IsVoidInstanceCuttingElement(fi)) continue;   // the family already cuts its host
                            if (InstanceVoidCutUtils.InstanceVoidCutExists(wall, fi)) continue;
                            if (!InstanceVoidCutUtils.CanBeCutWithVoid(wall)) { failed++; continue; }
                            InstanceVoidCutUtils.AddInstanceVoidCut(Doc, wall, fi);
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            Logger.Warn("[JA Famílias] Corte do nicho " + fi.Id.Value + ": " + ex.Message);
                        }
                    }
                    t.Commit();
                }
            }
            catch (Exception ex) { Logger.Warn("[JA Famílias] Corte dos nichos: " + ex.Message); failed = insts.Count; }
            if (failed > 0)
                warnings.Add(failed + " nicho(s) não cortaram a parede. No Revit: Modificar › Cortar › Cortar geometria, clique na parede e depois no nicho.");
        }

        private List<Wall> SelectedWalls()
        {
            try
            {
                return UiDoc.Selection.GetElementIds().Select(id => Doc.GetElement(id)).OfType<Wall>()
                    .Where(w => w.Location is LocationCurve).ToList();
            }
            catch { return new List<Wall>(); }
        }

        /// <summary>Chapim on top of walls: one type per wall thickness, then one instance per wall.</summary>
        private int ApplyChapim(ChapimConfig cfg, GenerationResult gen, List<Wall> walls, Settings settings, List<string> warnings, List<string> extra, out string problem)
        {
            problem = null;
            if (walls == null || walls.Count == 0)
            {
                try
                {
                    var refs = UiDoc.Selection.PickObjects(ObjectType.Element, new WallFilter(),
                        "Selecione as paredes/muros que recebem o chapim " + cfg.Codigo + " e clique em Concluir (Esc cancela)");
                    walls = refs.Select(r => Doc.GetElement(r)).OfType<Wall>().ToList();
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { walls = new List<Wall>(); }
                catch (Exception ex)
                {
                    problem = "Não deu para selecionar paredes na vista ativa (" + ex.Message + ").";
                    return 0;
                }
            }
            if (walls.Count == 0)
            {
                problem = "Nenhuma parede selecionada. Selecione os muros e rode Novo Chapim de novo (o tipo " + cfg.Codigo + " já está no projeto), ou arraste o tipo do Navegador de Projeto.";
                return 0;
            }

            // curved walls (arcs): a curved family per wall thickness, radius and side; the angle of
            // each arc is an instance parameter (when Revit lets the revolution follow it)
            var arcos = new List<KeyValuePair<Wall, ArcoParede>>();
            foreach (Wall w in walls)
            {
                ArcoParede ap = null;
                try { ap = ChapimPlacer.Arco(w); } catch (Exception ex) { Logger.Warn("[JA Famílias] Arco da parede " + w.Id.Value + ": " + ex.Message); }
                if (ap != null) arcos.Add(new KeyValuePair<Wall, ArcoParede>(w, ap));
            }
            var curvas = new HashSet<ElementId>(arcos.Select(x => x.Key.Id));
            walls = walls.Where(w => !curvas.Contains(w.Id)).ToList();
            var simbolosArco = new Dictionary<string, (FamilySymbol s, bool param)>();
            foreach (var g in arcos.GroupBy(x => ChapimPlacer.ThicknessMm(x.Key) + "|" + x.Value.Chave))
            {
                var first = g.First();
                double esp = ChapimPlacer.ThicknessMm(first.Key);
                var vc = (ChapimConfig)cfg.Copiar();
                vc.EspessuraMuroMm = esp;
                vc.RaioMm = Math.Round(first.Value.RaioMm);
                vc.ExteriorParaFora = first.Value.ExteriorParaFora;
                vc.AnguloGraus = first.Value.Varredura * 180 / Math.PI;
                vc.Codigo = cfg.Codigo + (Math.Abs(esp - cfg.EspessuraMuroMm) < 0.6 ? "" : "-" + Layout.Mm(esp)) + "-R" + first.Value.Chave;
                try
                {
                    var vg = PecaGenerator.Generate(UiApp.Application, Doc, vc.Normalized(), settings);
                    warnings.AddRange(vg.Warnings);
                    if (vg.Symbol == null) continue;
                    extra.Add(vg.FamilyName + " (muro curvo, raio " + Layout.Mm(vc.RaioMm) + " mm no eixo)");
                    simbolosArco[g.Key] = (vg.Symbol, vg.AnguloParametrico);
                    if (!vg.AnguloParametrico && g.Select(x => Math.Round(x.Value.Varredura * 1800 / Math.PI)).Distinct().Count() > 1)
                    {
                        // fixed angle: one family per arc angle
                        foreach (var ga in g.GroupBy(x => Math.Round(x.Value.Varredura * 1800 / Math.PI)).Skip(1))
                        {
                            var va = (ChapimConfig)vc.Copiar();
                            va.AnguloGraus = ga.First().Value.Varredura * 180 / Math.PI;
                            va.Codigo = vc.Codigo + "-A" + Math.Round(va.AnguloGraus).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
                            var vga = PecaGenerator.Generate(UiApp.Application, Doc, va.Normalized(), settings);
                            warnings.AddRange(vga.Warnings);
                            if (vga.Symbol != null) simbolosArco[g.Key + "|" + ga.Key] = (vga.Symbol, false);
                        }
                    }
                }
                catch (Exception ex) { warnings.Add($"Chapim curvo para a parede {first.Key.Id.Value} não gerado ({ex.Message})."); }
            }

            // a type for each other wall thickness (generated before the placing transaction)
            var symbols = new Dictionary<double, FamilySymbol>();
            foreach (var g in walls.GroupBy(w => ChapimPlacer.ThicknessMm(w)))
            {
                if (Math.Abs(g.Key - cfg.EspessuraMuroMm) < 0.6) { symbols[g.Key] = gen.Symbol; continue; }
                var vc = (ChapimConfig)cfg.Copiar();
                vc.EspessuraMuroMm = g.Key;
                vc.Codigo = cfg.Codigo + "-" + Layout.Mm(g.Key);
                try
                {
                    var vg = PecaGenerator.Generate(UiApp.Application, Doc, vc, settings);
                    if (vg.Symbol != null) symbols[g.Key] = vg.Symbol;
                    extra.Add(vg.FamilyName + " (muro de " + Layout.Mm(g.Key) + " mm)");
                    warnings.AddRange(vg.Warnings);
                }
                catch (Exception ex)
                {
                    warnings.Add($"Tipo para muro de {Layout.Mm(g.Key)} mm não gerado ({ex.Message}); usei {cfg.Codigo}.");
                    symbols[g.Key] = gen.Symbol;
                }
            }

            int placed = 0;
            using (var t = new Transaction(Doc, "JA Famílias – aplicar chapim"))
            {
                t.Start();
                var opts = t.GetFailureHandlingOptions();
                opts.SetFailuresPreprocessor(new WarningSwallower());
                t.SetFailureHandlingOptions(opts);
                foreach (var g in walls.GroupBy(w => ChapimPlacer.ThicknessMm(w)))
                {
                    FamilySymbol s = symbols.TryGetValue(g.Key, out FamilySymbol fs) ? fs : gen.Symbol;
                    if (!s.IsActive) s.Activate();
                    placed += ChapimPlacer.Apply(Doc, s, g, warnings);
                }
                foreach (var x in arcos)
                {
                    string k = ChapimPlacer.ThicknessMm(x.Key) + "|" + x.Value.Chave;
                    if (!simbolosArco.TryGetValue(k + "|" + Math.Round(x.Value.Varredura * 1800 / Math.PI), out var sa) && !simbolosArco.TryGetValue(k, out sa)) continue;
                    try
                    {
                        if (!sa.s.IsActive) sa.s.Activate();
                        if (ChapimPlacer.PlaceArc(Doc, sa.s, x.Key, x.Value, sa.param, warnings) != null) placed++;
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"Parede {x.Key.Id.Value}: chapim curvo não aplicado ({ex.Message}).");
                        Logger.Warn("[JA Famílias] Chapim curvo na parede " + x.Key.Id.Value + ": " + ex);
                    }
                }
                t.Commit();
            }
            if (placed == 0) problem = "O chapim não pôde ser aplicado nas paredes selecionadas (veja os avisos).";
            return placed;
        }

        private sealed class WallFilter : ISelectionFilter
        {
            public bool AllowElement(Element e) => e is Wall w && w.Location is LocationCurve;
            public bool AllowReference(Reference reference, XYZ position) => false;
        }

        /// <summary>The last configuration of this kind (keeps the office's stone and sizes), or the first model.</summary>
        protected static IPecaConfig StartPeca(TipoPeca tipo)
        {
            string json = ToolPrefs.Get("ultima.peca." + tipo);
            IPecaConfig last = string.IsNullOrWhiteSpace(json) ? null : PecaUtil.FromJson(tipo, json);
            if (last is BancadaConfig bl && bl.Livre) last = null;
            return last ?? PecaCatalog.Novo(tipo);
        }
    }

    [Transaction(TransactionMode.Manual)]
    public sealed class NovaBancadaCommand : EsquadriaCommandBase
    {
        public override string Title => "Nova Bancada";
        protected override Result Run() => OpenPeca(StartPeca(TipoPeca.Bancada));
    }

    [Transaction(TransactionMode.Manual)]
    public sealed class NovoNichoCommand : EsquadriaCommandBase
    {
        public override string Title => "Novo Nicho";
        protected override Result Run() => OpenPeca(StartPeca(TipoPeca.Nicho));
    }

    /// <summary>
    /// Nicho no Local: two clicks on the wall face (in an elevation, section or 3D view) give the
    /// finished opening — width along the wall, height and base — and the niche is generated in that
    /// size (configurator in between) and placed exactly there, on the clicked side of the wall.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class NichoNoLocalCommand : EsquadriaCommandBase
    {
        public override string Title => "Nicho no Local";


        protected override Result Run()
        {
            View vista = Doc.ActiveView;
            EntradaCroqui ent = EntradaDoCroqui("nicho", vista,
                "Em elevação ou corte, de frente para a parede: desenhe o retângulo do vão acabado (ou selecione linhas / bordas); depois clique a face da parede onde vai o nicho. Clicar: dois cantos opostos do vão na face da parede.");
            if (ent == null) return Result.Cancelled;
            if (ent.Iniciado) return Result.Succeeded;
            CroquiAtual = ent;
            Reference r1;
            XYZ p1, p2;
            try
            {
                if (ent.Curvas != null)
                {
                    // the rectangle drawn: its extent in the view, then carried to the face clicked
                    XYZ rd = vista.RightDirection, up = vista.UpDirection, vd = vista.ViewDirection;
                    var pts = ent.Curvas.SelectMany(c => new[] { c.GetEndPoint(0), c.GetEndPoint(1), c.Evaluate(0.5, true) }).ToList();
                    double x0 = pts.Min(q => q.DotProduct(rd)), x1 = pts.Max(q => q.DotProduct(rd)), y0 = pts.Min(q => q.DotProduct(up)), y1 = pts.Max(q => q.DotProduct(up));
                    double prof = pts.Average(q => q.DotProduct(vd));
                    r1 = UiDoc.Selection.PickObject(Autodesk.Revit.UI.Selection.ObjectType.Face, new FiltroDeParede(null, true), "Nicho no local: clique a face da parede onde vai o nicho");
                    PlanarFace pf = (Doc.GetElement(r1) as Wall)?.GetGeometryObjectFromReference(r1) as PlanarFace;
                    if (pf == null || Math.Abs(pf.FaceNormal.DotProduct(vd)) < 0.2)
                    {
                        Show("Clique numa face plana da parede, de frente para a vista.", ResultKind.Warning);
                        return Result.Cancelled;
                    }
                    XYZ g = r1.GlobalPoint, fn = pf.FaceNormal;
                    XYZ NaFace(double x, double y)
                    {
                        XYZ q = rd * x + up * y + vd * prof;
                        double t = (g - q).DotProduct(fn) / vd.DotProduct(fn);
                        return q + vd * t;
                    }
                    p1 = NaFace(x0, y0); p2 = NaFace(x1, y1);
                }
                else
                {
                    r1 = UiDoc.Selection.PickObject(Autodesk.Revit.UI.Selection.ObjectType.Face, new FiltroDeParede(null, true),
                        "Nicho no local: clique um canto do vão acabado na face da parede (elevação, corte ou 3D)");
                    Reference r2 = UiDoc.Selection.PickObject(Autodesk.Revit.UI.Selection.ObjectType.Face, new FiltroDeParede(null, true),
                        "agora o canto oposto, na mesma face da parede");
                    if (r2.ElementId != r1.ElementId) { Show("Os dois cliques precisam ser na mesma parede.", ResultKind.Warning); return Result.Cancelled; }
                    p1 = r1.GlobalPoint; p2 = r2.GlobalPoint;
                }
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException)
            {
                Show("Nesta vista não dá para clicar na face da parede. Abra uma elevação, um corte ou uma vista 3D.", ResultKind.Info);
                return Result.Cancelled;
            }
            var wall = Doc.GetElement(r1) as Wall;
            if (wall == null)
            {
                Show("Clique na face de uma parede.", ResultKind.Warning);
                return Result.Cancelled;
            }
            XYZ n = (wall.GetGeometryObjectFromReference(r1) as PlanarFace)?.FaceNormal;
            if (n == null || Math.Abs(n.Z) > 0.1)
            {
                Show("Clique na face vertical (plana) da parede.", ResultKind.Warning);
                return Result.Cancelled;
            }
            n = new XYZ(n.X, n.Y, 0).Normalize();
            XYZ u = new XYZ(-n.Y, n.X, 0);
            double s = (p2 - p1).DotProduct(u);
            double largura = Math.Abs(s) / MmFt, altura = Math.Abs(p2.Z - p1.Z) / MmFt;
            var nivel = Doc.GetElement(wall.LevelId) as Level;
            if (largura < 80 || altura < 80 || nivel == null)
            {
                Show(nivel == null ? "A parede não tem nível." : "O vão clicado é pequeno demais (mínimo 80 × 80 mm).", ResultKind.Warning);
                return Result.Cancelled;
            }
            double zBase = Math.Min(p1.Z, p2.Z);
            XYZ centro = p1 + u * (s / 2);
            centro = new XYZ(centro.X, centro.Y, zBase);

            var cfg = StartPeca(TipoPeca.Nicho) as NichoConfig ?? new NichoConfig();
            var usados = new HashSet<string>(ProjectPecas.All(Doc, TipoPeca.Nicho).Select(a => a.Codigo), StringComparer.OrdinalIgnoreCase);
            cfg.Codigo = ProjectPecas.NextCode(usados, TipoPeca.Nicho);
            cfg.LarguraMm = Math.Round(largura);
            cfg.AlturaMm = Math.Round(altura);
            cfg.AlturaBaseMm = Math.Max(0, Math.Round((zBase - nivel.ProjectElevation) / MmFt));
            double paredeMm = wall.Width / MmFt;
            double maxProf = paredeMm - 30 - (cfg.Revestimento ? cfg.RevestimentoEspessuraMm : 0);
            if (maxProf >= 30 && cfg.ProfundidadeMm > maxProf) cfg.ProfundidadeMm = Math.Floor(maxProf);
            cfg.Normalized();
            return OpenPeca(cfg, null, simbolo => Colocar(simbolo, wall, nivel, centro, n, u));
        }

        /// <summary>Places the niche on the clicked face: on the right side of the wall, centred and at the clicked base.</summary>
        private int Colocar(FamilySymbol s, Wall wall, Level nivel, XYZ centro, XYZ n, XYZ u)
        {
            using (var t = new Transaction(Doc, "JA Famílias – nicho no local"))
            {
                t.Start();
                var opts = t.GetFailureHandlingOptions();
                opts.SetFailuresPreprocessor(new WarningSwallower());
                t.SetFailureHandlingOptions(opts);
                if (!s.IsActive) s.Activate();
                FamilyInstance fi = Doc.Create.NewFamilyInstance(centro, s, wall, nivel, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                Doc.Regenerate();
                try { fi.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM)?.Set(centro.Z - nivel.ProjectElevation); } catch { }
                Doc.Regenerate();
                // the niche body must be behind the clicked face (inside the wall)
                BoundingBoxXYZ bb = fi.get_BoundingBox(null);
                if (bb != null && ((bb.Min + bb.Max) / 2 - centro).DotProduct(n) > 0 && fi.CanFlipFacing)
                {
                    fi.flipFacing();
                    Doc.Regenerate();
                    bb = fi.get_BoundingBox(null);
                }
                // centred on the clicks along the wall, whatever the template's origin
                if (bb != null)
                {
                    double d = (centro - (bb.Min + bb.Max) / 2).DotProduct(u);
                    if (Math.Abs(d) > 2 * MmFt) ElementTransformUtils.MoveElement(Doc, fi.Id, u * d);
                }
                ApagarCroqui(CroquiAtual);
                t.Commit();
                try { UiDoc.Selection.SetElementIds(new List<ElementId> { fi.Id }); } catch { }
            }
            return 1;
        }
    }

    /// <summary>
    /// Ralo no Local (antes "Ralo no Croqui"): the drain sketched in the plan, in any of these ways —
    /// · drawn with Revit's own Detail Line tools (line, rectangle, circle, polygon, arc, pick lines…):
    ///   the command opens the tool, you draw, and clicking Ralo no Local again reads what was drawn;
    /// · the lines (detail / model) selected before the command;
    /// · clicked point by point, or along a wall.
    /// An open line is the axis of a linear drain; a closed outline gives the shape and the width
    /// (rectangle, L, U… = linear drain along its centre line; square = square drain); a circle =
    /// round drain. One family is configured in the usual window and placed exactly there.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class RaloCroquiCommand : EsquadriaCommandBase
    {
        public override string Title => "Ralo no Local";

        /// <summary>What a croqui turned out to be.</summary>
        private sealed class Croqui
        {
            public TipoRalo Formato = TipoRalo.Linear;
            public List<XYZ> Eixo;          // linear: the axis (ft, plan)
            public XYZ Centro;              // square / round
            public XYZ DirX = XYZ.BasisX;
            public double LarguraMm;        // from a closed outline / circle (0 = from the window)
            public string Origem;           // for the message
        }

        protected override Result Run()
        {
            if (!(Doc.ActiveView is ViewPlan vista) || vista.GenLevel == null)
            {
                Show("Abra uma planta baixa: o croqui do ralo é desenhado nela.", ResultKind.Info);
                return Result.Cancelled;
            }
            Level nivel = vista.GenLevel;
            int borda = 0;
            double afastamento = 20;
            EntradaCroqui ent = EntradaDoCroqui("ralo", vista,
                "Linha aberta = eixo de um ralo linear; contorno fechado (retângulo, L, U…) = formato e largura do ralo; quadrado = ralo quadrado; círculo = ralo redondo.",
                new[] { "Junto de uma parede: clicar a parede e o início e o fim do ralo" });
            if (ent == null) return Result.Cancelled;
            if (ent.Iniciado) return Result.Succeeded;
            Croqui cq;
            if (ent.Curvas != null)
            {
                cq = LerCroqui(ent.Curvas, out string erro);
                if (cq == null)
                {
                    Show(erro + (ent.Modo == ModoCroqui.Ferramentas ? "\n\nAs linhas desenhadas ficaram na vista: corrija e rode de novo com elas selecionadas." : ""), ResultKind.Warning);
                    return Result.Cancelled;
                }
            }
            else
            {
                List<XYZ> pts;
                if (ent.Extra == 0)
                {
                    var dlgA = new OptionsDialog(Title, "RaloCroquiParede", "Clicar");
                    var fAf = dlgA.Number("afastamento", "Afastamento da face da parede até a borda do ralo (mm)", 20, 0, 2000);
                    if (!dlgA.Show(Owner)) return Result.Cancelled;
                    afastamento = fAf.Value;
                    pts = JuntoDaParede(vista, out borda, out string erro);
                    if (pts == null) { if (erro != null) Show(erro, ResultKind.Warning); return Result.Cancelled; }
                }
                else
                    pts = ClicarPontos(vista, null, null, n => n == 0 ? "Ralo no croqui: clique o início do eixo do ralo (Esc cancela)"
                                                                     : "Clique o próximo ponto do eixo (Esc termina; " + n + " ponto(s); Shift inverte o orto)", false, 0, null);
                if (pts == null || pts.Count < 2) return Result.Cancelled;
                cq = new Croqui { Eixo = pts };
            }

            var cfg = StartPeca(TipoPeca.Ralo) as RaloConfig ?? new RaloConfig();
            cfg.Codigo = ProjectPecas.NextCode(ProjectPecas.UsedCodes(Doc, TipoPeca.Ralo), TipoPeca.Ralo);
            XYZ o, dirX = XYZ.BasisX;
            if (cq.Formato == TipoRalo.Linear)
            {
                o = cq.Eixo[0];
                cfg.Formato = TipoRalo.Linear;
                cfg.BordaNoCaminho = borda;
                cfg.AfastamentoMm = afastamento;
                cfg.Caminho = cq.Eixo.Select(q => new Pt(Math.Round((q.X - o.X) * 304.8, 1), Math.Round((q.Y - o.Y) * 304.8, 1))).ToList();
                if (cq.LarguraMm > 0) cfg.LarguraMm = Math.Round(cq.LarguraMm);
                if (string.IsNullOrWhiteSpace(cfg.Nome)) cfg.Nome = "Ralo linear no croqui";
            }
            else
            {
                // square / round: at the centre of the drawing, turned with it
                o = cq.Centro;
                dirX = cq.DirX;
                cfg.Formato = cq.Formato;
                cfg.Caminho = null;
                cfg.BordaNoCaminho = 0;
                cfg.LarguraMm = Math.Round(cq.LarguraMm);
            }
            cfg.Normalized();
            if (cfg.Formato == TipoRalo.Linear && !cfg.NoCroqui) { Show("O croqui precisa de pelo menos um trecho com mais de 1 mm.", ResultKind.Warning); return Result.Cancelled; }
            return OpenPeca(cfg, null, simbolo =>
            {
                using (var t = new Transaction(Doc, "JA Famílias – " + Title))
                {
                    t.Start();
                    var op = t.GetFailureHandlingOptions(); op.SetFailuresPreprocessor(new WarningSwallower()); t.SetFailureHandlingOptions(op);
                    FamilyInstance fi = ColocarNoPonto(simbolo, new XYZ(o.X, o.Y, nivel.ProjectElevation), dirX, nivel);
                    ApagarCroqui(ent);
                    t.Commit();
                    try { UiDoc.Selection.SetElementIds(new List<ElementId> { fi.Id }); } catch { }
                }
                return 1;
            });
        }

        /// <summary>
        /// What the lines are: a circle (round drain), an open chain (the axis) or a closed outline of
        /// straight lines (square drain, or the centre line and width of a linear drain).
        /// </summary>
        private static Croqui LerCroqui(List<Curve> curvas, out string erro)
        {
            erro = null;
            const double tol = 2 / 304.8;
            // circle: arcs with one centre and radius closing 360°
            var arcos = curvas.OfType<Arc>().ToList();
            if (arcos.Count == curvas.Count && arcos.Count > 0)
            {
                XYZ c0 = arcos[0].Center;
                double r0 = arcos[0].Radius;
                double giro = arcos.Sum(a => a.IsBound ? a.Length / a.Radius : 2 * Math.PI);
                if (arcos.All(a => new XYZ(a.Center.X - c0.X, a.Center.Y - c0.Y, 0).GetLength() < tol && Math.Abs(a.Radius - r0) < tol) && Math.Abs(giro - 2 * Math.PI) < 0.02)
                    return new Croqui { Formato = TipoRalo.Redondo, Centro = new XYZ(c0.X, c0.Y, 0), LarguraMm = 2 * r0 * 304.8 };
            }
            // open chain: the axis
            List<XYZ> eixo = EncadearCaminho(curvas, 100, out string erroAberto);
            if (eixo != null && eixo.Count > 2 && eixo[0].DistanceTo(eixo[eixo.Count - 1]) < tol) eixo = null;   // one closed curve (ellipse, closed spline)
            if (eixo != null) return new Croqui { Eixo = eixo };
            // closed outline
            if (curvas.Any(c => !(c is Line)))
            {
                erro = "Contorno fechado com arcos ou curvas: desenhe o contorno só com linhas retas (retângulo, L, U…), ou o eixo do ralo como linha aberta.";
                return null;
            }
            List<XYZ> poli = FecharPoligono(curvas, tol);
            if (poli == null) { erro = erroAberto; return null; }
            poli = SemColineares(poli);
            if (poli.Count < 4) { erro = "O contorno desenhado tem menos de 4 lados."; return null; }
            if (poli.Count == 4)
            {
                XYZ e0 = poli[1] - poli[0], e1 = poli[2] - poli[1];
                bool reto = Enumerable.Range(0, 4).All(k => Math.Abs((poli[(k + 1) % 4] - poli[k]).Normalize().DotProduct((poli[(k + 2) % 4] - poli[(k + 1) % 4]).Normalize())) < 0.01);
                double a = e0.GetLength(), b = e1.GetLength();
                if (reto && Math.Max(a, b) / Math.Min(a, b) < 1.5)
                {
                    XYZ centro = (poli[0] + poli[2]) / 2;
                    erro = null;
                    return new Croqui { Formato = TipoRalo.Quadrado, Centro = centro, DirX = e0.Normalize(), LarguraMm = (a + b) / 2 * 304.8 };
                }
            }
            List<XYZ> meio = EixoDoContorno(poli, tol, out double largura, out string erroEixo);
            if (meio == null) { erro = erroEixo; return null; }
            return new Croqui { Eixo = meio, LarguraMm = largura * 304.8 };
        }

        /// <summary>
        /// Centre line of a band-shaped outline (rectangle, L, U, Z…): its edges are paired with the
        /// parallel edge in front at the band width; each pair gives a stretch of the centre line,
        /// and neighbouring stretches meet at the intersection of their lines (the corners).
        /// </summary>
        private static List<XYZ> EixoDoContorno(List<XYZ> p, double tol, out double largura, out string erro)
        {
            erro = null; largura = 0;
            int n = p.Count;
            var pares = new List<(double dist, XYZ s0, XYZ s1)>();
            for (int i = 0; i < n; i++)
            {
                XYZ a = p[i], b = p[(i + 1) % n];
                double L = a.DistanceTo(b);
                XYZ d = (b - a) / L, nn = new XYZ(-d.Y, d.X, 0);
                for (int j = i + 1; j < n; j++)
                {
                    XYZ c = p[j], e = p[(j + 1) % n];
                    XYZ d2 = (e - c).Normalize();
                    if (d.DotProduct(d2) > -0.9995) continue;               // only edges facing each other
                    double dist = (c - a).DotProduct(nn);
                    if (Math.Abs(dist) < tol) continue;
                    double t1 = (c - a).DotProduct(d), t2 = (e - a).DotProduct(d);
                    double lo = Math.Max(0, Math.Min(t1, t2)), hi = Math.Min(L, Math.Max(t1, t2));
                    if (hi - lo < tol) continue;
                    XYZ lado = dist > 0 ? nn : -nn;
                    pares.Add((Math.Abs(dist), a + d * lo + lado * (Math.Abs(dist) / 2), a + d * hi + lado * (Math.Abs(dist) / 2)));
                }
            }
            if (pares.Count == 0) { erro = "Não achei a largura do ralo no contorno (lados paralelos frente a frente)."; return null; }
            double w = pares.Min(x => x.dist);
            largura = w;
            var segs = pares.Where(x => Math.Abs(x.dist - w) < tol).Select(x => new[] { x.s0, x.s1 }).ToList();
            int m = segs.Count;
            var viz = Enumerable.Range(0, m).Select(_ => new List<(int j, int ei, int ej)>()).ToList();
            for (int i = 0; i < m; i++)
                for (int j = i + 1; j < m; j++)
                {
                    double best = double.MaxValue; int bi = 0, bj = 0;
                    for (int x = 0; x < 2; x++)
                        for (int y = 0; y < 2; y++)
                        {
                            double dd = segs[i][x].DistanceTo(segs[j][y]);
                            if (dd < best) { best = dd; bi = x; bj = y; }
                        }
                    if (best <= 1.5 * w) { viz[i].Add((j, bi, bj)); viz[j].Add((i, bj, bi)); }
                }
            if (viz.Any(v => v.Count > 2)) { erro = "O contorno tem um ramo (forma de T ou cruz): desenhe um ralo de cada vez."; return null; }
            if (m == 1) return new List<XYZ> { segs[0][0], segs[0][1] };
            var pontas = Enumerable.Range(0, m).Where(k => viz[k].Count == 1).ToList();
            if (pontas.Count != 2) { erro = "O contorno não é uma faixa com começo e fim (é um anel?): desenhe o eixo do ralo como linha aberta."; return null; }
            int cur = pontas[0], prev = -1, ultimaPonta = 0;
            var eixo = new List<XYZ> { segs[cur][1 - viz[cur][0].ei] };
            while (true)
            {
                var prox = viz[cur].Where(v => v.j != prev).ToList();
                if (prox.Count == 0) break;
                var (j, ei, ej) = prox[0];
                XYZ s0 = segs[cur][0], d1 = segs[cur][1] - segs[cur][0], t0 = segs[j][0], d2 = segs[j][1] - segs[j][0];
                double den = d1.X * d2.Y - d1.Y * d2.X;
                XYZ canto;
                if (Math.Abs(den) < 1e-12) canto = (segs[cur][ei] + segs[j][ej]) / 2;
                else
                {
                    XYZ w0 = t0 - s0;
                    double u = (w0.X * d2.Y - w0.Y * d2.X) / den;
                    canto = s0 + d1 * u;
                }
                eixo.Add(canto);
                prev = cur; cur = j; ultimaPonta = ej;
            }
            eixo.Add(segs[cur][1 - ultimaPonta]);
            return eixo;
        }

        /// <summary>
        /// "Junto de uma parede": the wall, then the start and the end of the drain on the room
        /// side. The path is the wall face between the two points; borda tells on which side of it
        /// the drain runs (the side of the clicks).
        /// </summary>
        private List<XYZ> JuntoDaParede(ViewPlan vista, out int borda, out string erro)
        {
            borda = 0; erro = null;
            Wall w;
            try { w = Doc.GetElement(UiDoc.Selection.PickObject(ObjectType.Element, new FiltroDeParede(FiltroDeParede.Reta), "Ralo junto da parede: clique a parede (reta)")) as Wall; }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return null; }
            if (!(w?.Location is LocationCurve lc) || !(lc.Curve is Line linha)) { erro = "Só paredes retas."; return null; }
            List<XYZ> cl = ClicarPontos(vista, null, null, n => n == 0 ? "Clique o início do ralo, do lado do ambiente" : "Clique o fim do ralo", false, 2, null);
            if (cl.Count < 2) return null;
            double z = cl[0].Z;
            XYZ Plano(XYZ q) => new XYZ(q.X, q.Y, 0);
            XYZ dir = Plano(linha.Direction).Normalize(), ori = Plano(w.Orientation).Normalize();
            double off = 0;
            try { off = CortesEsquadriasCommand.WallOffset(w); } catch { }
            XYZ centro = Plano(linha.GetEndPoint(0)) + ori * off;
            double lado = Math.Sign((Plano(cl[0]) - centro).DotProduct(ori));
            if (lado == 0) lado = 1;
            XYZ face = centro + ori * (lado * w.Width / 2);
            XYZ Proj(XYZ q) { double t = (Plano(q) - face).DotProduct(dir); XYZ f = face + dir * t; return new XYZ(f.X, f.Y, z); }
            XYZ a = Proj(cl[0]), b = Proj(cl[1]);
            if (a.DistanceTo(b) < 50 / 304.8) { erro = "O ralo ficou curto demais (menos de 5 cm)."; return null; }
            XYZ esq = XYZ.BasisZ.CrossProduct((b - a).Normalize());
            borda = Math.Sign(esq.DotProduct(ori * lado));
            return new List<XYZ> { a, b };
        }

        /// <summary>Selected lines chained into one open path; arcs and splines as stretches of about <paramref name="cordaMm"/>.</summary>
        internal static List<XYZ> EncadearCaminho(List<Curve> curvas, double cordaMm, out string erro)
        {
            erro = null;
            const double tol = 2 / 304.8;
            var segs = new List<List<XYZ>>();
            foreach (Curve c in curvas)
            {
                IList<XYZ> t;
                if (c is Line) t = new List<XYZ> { c.GetEndPoint(0), c.GetEndPoint(1) };
                else
                {
                    int n = Math.Max(2, (int)Math.Ceiling(c.Length * 304.8 / Math.Max(20, cordaMm)));
                    t = Enumerable.Range(0, n + 1).Select(i => c.Evaluate((double)i / n, true)).ToList();
                }
                segs.Add(t.Select(q => new XYZ(q.X, q.Y, 0)).ToList());
            }
            bool Perto(XYZ a, XYZ b) => a.DistanceTo(b) < tol;
            // start at an end that touches no other line
            List<XYZ> inicio = null;
            bool inv = false;
            foreach (var s0 in segs)
            {
                foreach (bool ponta in new[] { false, true })
                {
                    XYZ q = ponta ? s0[s0.Count - 1] : s0[0];
                    if (!segs.Any(o2 => !ReferenceEquals(o2, s0) && (Perto(o2[0], q) || Perto(o2[o2.Count - 1], q)))) { inicio = s0; inv = ponta; break; }
                }
                if (inicio != null) break;
            }
            if (inicio == null) { erro = "As linhas selecionadas formam um contorno fechado: o ralo precisa de um começo e um fim."; return null; }
            var res = new List<XYZ>(inv ? Enumerable.Reverse(inicio) : inicio);
            var resto = segs.Where(x => !ReferenceEquals(x, inicio)).ToList();
            while (resto.Count > 0)
            {
                XYZ fim = res[res.Count - 1];
                var prox = resto.FirstOrDefault(x => Perto(x[0], fim) || Perto(x[x.Count - 1], fim));
                if (prox == null) { erro = "As linhas selecionadas não formam um caminho contínuo (há " + resto.Count + " linha(s) soltas ou um ramo)."; return null; }
                var pts = Perto(prox[0], fim) ? prox : Enumerable.Reverse(prox).ToList();
                res.AddRange(pts.Skip(1));
                resto.Remove(prox);
            }
            return res;
        }
    }

    [Transaction(TransactionMode.Manual)]
    public sealed class NovoRaloCommand : EsquadriaCommandBase
    {
        public override string Title => "Novo Ralo";
        protected override Result Run() => OpenPeca(StartPeca(TipoPeca.Ralo));
    }

    [Transaction(TransactionMode.Manual)]
    public sealed class NovoChapimCommand : EsquadriaCommandBase
    {
        public override string Title => "Novo Chapim";
        protected override Result Run() => OpenPeca(StartPeca(TipoPeca.Chapim));
    }
}
