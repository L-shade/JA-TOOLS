using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using JAEsquadrias.Core;
using JAEsquadrias.Engine;
using JAEsquadrias.UI;
using Settings = JAEsquadrias.Core.Settings;

namespace JAEsquadrias.Commands
{
    /// <summary>
    /// Soleiras e Peitoris: soleira or filete in every door and peitoril in every window of the
    /// scope. JA esquadrias get it in their own family (configuration changed and the family
    /// regenerated: every instance of that type follows). Other doors and windows get a separate
    /// stone piece (JA Pedra, Casework) at the opening: one family per size ("JA Soleira SL01
    /// 800x150x20"), centred on the wall, top at the door base / window sill, Y towards the exterior.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class SoleirasPeitorisCommand : EsquadriaCommandBase
    {
        public override string Title => "Soleiras e Peitoris";

        protected override Result Run()
        {
            var sel = UiDoc.Selection.GetElementIds();
            var dlg = new OptionsDialog(Title, "SoleirasPeitoris", "Aplicar");
            var fScope = dlg.Choice("scope", "Onde", new[] { $"Selecionados ({sel.Count})", "Vista ativa", "Todo o projeto" }, 2);
            if (sel.Count > 0) fScope.Index = 0;
            else if (fScope.Index == 0) fScope.Index = 2;

            dlg.Section("Portas");
            var fPortas = dlg.Check("portas", "Soleira ou filete nas portas", true);
            var fTipo = dlg.Choice("tipo", "Peça", new[] { "Soleira (toda a espessura da parede)", "Filete (faixa estreita)" }, 0);
            var fLargF = dlg.Number("largF", "Largura do filete (mm)", 50, 10, 500);
            fLargF.EnabledWhen(fTipo, 1);
            var fAlin = dlg.Choice("alin", "Filete na parede", new[] { "No centro", "Rente à face externa", "Rente à face interna" }, 0);
            fAlin.EnabledWhen(fTipo, 1);
            var fCodS = dlg.Text("codS", "Código da soleira", "SL01");
            var fMatS = dlg.Text("matS", "Material da soleira", "Granito cinza");
            var fEspS = dlg.Number("espS", "Espessura (mm)", 20, 3, 100);
            var fAvS = dlg.Number("avS", "Avanço em cada lado do vão (mm)", 0, 0, 300, "0 = comprimento igual à largura do vão.");

            dlg.Section("Janelas");
            var fJanelas = dlg.Check("janelas", "Peitoril nas janelas", true);
            var fCodP = dlg.Text("codP", "Código do peitoril", "PE01");
            var fMatP = dlg.Text("matP", "Material do peitoril", "Granito cinza");
            var fEspP = dlg.Number("espP", "Espessura (mm)", 20, 5, 100);
            var fProj = dlg.Number("proj", "Projeção p/ fora da parede (mm)", 30, 0, 300);
            var fAba = dlg.Number("aba", "Abas em cada lado do vão (mm)", 30, 0, 300);
            var fPing = dlg.Check("ping", "Pingadeira (sulco sob a projeção)", true);
            foreach (var f in new OptionsDialog.Field[] { fTipo, fCodS, fMatS, fEspS, fAvS }) f.EnabledWhen(fPortas);
            foreach (var f in new OptionsDialog.Field[] { fCodP, fMatP, fEspP, fProj, fAba, fPing }) f.EnabledWhen(fJanelas);

            dlg.Section("Como");
            var fJa = dlg.Check("ja", "Esquadrias JA: ligar na própria família", true, "Atualiza a família: todas as portas/janelas do mesmo tipo recebem a peça.");
            var fOutras = dlg.Check("outras", "Outras portas e janelas: peça de pedra separada no vão", true, "Uma família JA Pedra por tamanho, colocada no centro da parede.");
            var fRefazer = dlg.Check("refazer", "Refazer as peças já colocadas por esta ferramenta", false, "Sem marcar, vãos que já têm peça ficam como estão.");
            if (!dlg.Show(Owner)) return Result.Cancelled;
            if (fScope.Index == 0 && sel.Count == 0) fScope.Index = 2;
            if (!fPortas.Value && !fJanelas.Value) return Result.Cancelled;

            // ---- doors and windows in walls (not in curtain walls)
            IEnumerable<Element> escopo = fScope.Index == 0 ? sel.Select(id => Doc.GetElement(id)).Where(e => e != null)
                : (fScope.Index == 1 ? new FilteredElementCollector(Doc, Doc.ActiveView.Id) : new FilteredElementCollector(Doc))
                    .WherePasses(new ElementMulticategoryFilter(new List<BuiltInCategory> { BuiltInCategory.OST_Doors, BuiltInCategory.OST_Windows }))
                    .WhereElementIsNotElementType().ToElements();
            var vaos = escopo.OfType<FamilyInstance>().Where(fi =>
            {
                long c = fi.Category?.Id.Value ?? 0;
                bool porta = c == (long)BuiltInCategory.OST_Doors, janela = c == (long)BuiltInCategory.OST_Windows;
                return ((porta && fPortas.Value) || (janela && fJanelas.Value)) && fi.Host is Wall w && w.CurtainGrid == null;
            }).ToList();
            if (vaos.Count == 0) { Show("Nenhuma porta ou janela em parede no escopo.", ResultKind.Info); return Result.Cancelled; }

            var settings = Settings.Current;
            var avisos = new List<string>();
            int jaTipos = 0, colocadas = 0, jaTinham = 0, puladas = 0, refeitas = 0;

            // ---- JA esquadrias: in the family itself
            var outras = new List<FamilyInstance>();
            foreach (var g in vaos.GroupBy(v => v.Symbol?.Id ?? ElementId.InvalidElementId))
            {
                FamilyInstance first = g.First();
                EsquadriaConfig cfg = ProjectEsquadrias.ConfigOf(first, out FamilySymbol sym);
                if (cfg == null) { outras.AddRange(g); continue; }
                if (!fJa.Value) { puladas += g.Count(); continue; }
                EsquadriaConfig c = cfg.Clone();
                if (c.IsPorta)
                {
                    c.Soleira = true;
                    c.SoleiraTipo = fTipo.Index == 1 ? TipoSoleira.Filete : TipoSoleira.Soleira;
                    c.SoleiraCodigo = fCodS.Value;
                    c.SoleiraMaterial = fMatS.Value;
                    c.SoleiraEspessuraMm = fEspS.Value;
                    c.SoleiraLarguraMm = fTipo.Index == 1 ? fLargF.Value : 0;
                    c.SoleiraComprimentoMm = fAvS.Value > 0 ? c.LarguraMm + 2 * fAvS.Value : 0;
                    c.SoleiraAlinhamento = fTipo.Index == 1 ? (AlinhamentoSoleira)fAlin.Index : AlinhamentoSoleira.Centro;
                }
                else
                {
                    c.Peitoril = true;
                    c.PeitorilCodigo = fCodP.Value;
                    c.PeitorilMaterial = fMatP.Value;
                    c.PeitorilEspessuraMm = fEspP.Value;
                    c.PeitorilProjecaoMm = fProj.Value;
                    c.PeitorilAbaMm = fAba.Value;
                }
                c = c.Normalized();
                if (c.ToJson() == cfg.Clone().ToJson()) { jaTinham += g.Count(); continue; }
                try
                {
                    GenerationResult gen = FamilyGenerator.Generate(UiApp.Application, Doc, c, settings, sym.Family?.Name, sym.Name);
                    avisos.AddRange(gen.Warnings);
                    jaTipos++;
                }
                catch (Exception ex) { avisos.Add($"{cfg.Codigo}: não atualizada ({ex.Message})."); }
            }

            // ---- other families: separate stone pieces
            if (outras.Count > 0 && !fOutras.Value) puladas += outras.Count;
            else if (outras.Count > 0)
            {
                var alvos = new List<(FamilyInstance vao, PedraConfig cfg, XYZ centro, XYZ exterior, Level nivel, double z)>();
                foreach (FamilyInstance fi in outras)
                {
                    try
                    {
                        if (!Vao(fi, out XYZ centro, out XYZ ext, out double paredeMm, out double larguraMm, out Level nivel, out double z))
                        {
                            avisos.Add($"{Nome(fi)}: posição na parede não encontrada.");
                            continue;
                        }
                        bool porta = fi.Category.Id.Value == (long)BuiltInCategory.OST_Doors;
                        var pc = new PedraConfig { Material = porta ? fMatS.Value : fMatP.Value };
                        if (porta)
                        {
                            pc.Uso = fTipo.Index == 1 ? UsoPedra.Filete : UsoPedra.Soleira;
                            pc.Codigo = fCodS.Value;
                            pc.EspessuraMm = fEspS.Value;
                            pc.ComprimentoMm = Math.Round(larguraMm + 2 * fAvS.Value);
                            if (pc.Uso == UsoPedra.Filete)
                            {
                                pc.LarguraMm = fLargF.Value;
                                pc.CentroMm = fAlin.Index == 1 ? paredeMm / 2 - fLargF.Value / 2 : fAlin.Index == 2 ? -(paredeMm / 2 - fLargF.Value / 2) : 0;
                            }
                            else pc.LarguraMm = Math.Round(paredeMm);
                        }
                        else
                        {
                            pc.Uso = UsoPedra.Peitoril;
                            pc.Codigo = fCodP.Value;
                            pc.EspessuraMm = fEspP.Value;
                            pc.ComprimentoMm = Math.Round(larguraMm + 2 * fAba.Value);
                            pc.LarguraMm = Math.Round(paredeMm + fProj.Value);
                            pc.CentroMm = fProj.Value / 2;
                            pc.ProjecaoMm = fProj.Value;
                            pc.Pingadeira = fPing.Value;
                        }
                        pc.Normalized();
                        alvos.Add((fi, pc, centro, ext, nivel, z));
                    }
                    catch (Exception ex) { avisos.Add($"{Nome(fi)}: {ex.Message}"); }
                }

                // one family per size (already in the project = reused)
                var simbolos = new Dictionary<string, FamilySymbol>();
                foreach (var tam in alvos.GroupBy(a => Chave(a.cfg)))
                {
                    string nome = FamilyGenerator.SafeName(tam.Key);
                    Family f = new FilteredElementCollector(Doc).OfClass(typeof(Family)).Cast<Family>().FirstOrDefault(x => x.Name == nome);
                    FamilySymbol s = f == null ? null : f.GetFamilySymbolIds().Select(id => Doc.GetElement(id) as FamilySymbol).FirstOrDefault(x => x != null);
                    if (s == null)
                    {
                        try
                        {
                            GenerationResult gen = PecaGenerator.Generate(UiApp.Application, Doc, tam.First().cfg, settings, nome, tam.First().cfg.Codigo);
                            avisos.AddRange(gen.Warnings);
                            s = gen.Symbol;
                        }
                        catch (Exception ex) { avisos.Add($"{nome}: {ex.Message}"); }
                    }
                    if (s != null) simbolos[tam.Key] = s;
                }

                // pieces already placed by this tool, to skip or redo
                var existentes = new FilteredElementCollector(Doc).OfClass(typeof(FamilyInstance)).OfCategory(BuiltInCategory.OST_Casework)
                    .Cast<FamilyInstance>().Where(i => ProjectPecas.ConfigOf(i, out _) is PedraConfig).ToList();

                using (var t = new Transaction(Doc, "JA Famílias · " + Title))
                {
                    t.Start();
                    var opts = t.GetFailureHandlingOptions();
                    opts.SetFailuresPreprocessor(new WarningSwallower());
                    t.SetFailureHandlingOptions(opts);
                    foreach (var a in alvos)
                    {
                        if (!simbolos.TryGetValue(Chave(a.cfg), out FamilySymbol s)) continue;
                        var perto = existentes.Where(i => i.IsValidObject && i.Location is LocationPoint lp &&
                                                          new XYZ(lp.Point.X - a.centro.X, lp.Point.Y - a.centro.Y, 0).GetLength() < 0.5 &&
                                                          Math.Abs(Z(i) - a.z) < 0.3).ToList();
                        if (perto.Count > 0)
                        {
                            if (!fRefazer.Value) { jaTinham++; continue; }
                            foreach (var i in perto) { try { Doc.Delete(i.Id); } catch { } }
                            refeitas++;
                        }
                        try
                        {
                            if (!s.IsActive) s.Activate();
                            FamilyInstance novo = Doc.Create.NewFamilyInstance(new XYZ(a.centro.X, a.centro.Y, a.z), s, a.nivel, StructuralType.NonStructural);
                            double ang = Math.Atan2(a.exterior.Y, a.exterior.X) - Math.PI / 2;          // local +Y → exterior
                            if (Math.Abs(ang) > 1e-9)
                                ElementTransformUtils.RotateElement(Doc, novo.Id, Line.CreateBound(new XYZ(a.centro.X, a.centro.Y, a.z), new XYZ(a.centro.X, a.centro.Y, a.z + 1)), ang);
                            DefinirAltura(novo, a.nivel, a.z);
                            colocadas++;
                        }
                        catch (Exception ex) { avisos.Add($"{Nome(a.vao)}: {ex.Message}"); }
                    }
                    t.Commit();
                }
            }

            var sb = new StringBuilder();
            if (jaTipos > 0) sb.AppendLine($"{jaTipos} tipo(s) de esquadria JA atualizado(s) com soleira/peitoril.");
            if (colocadas > 0) sb.AppendLine($"{colocadas} peça(s) de pedra colocada(s) em portas e janelas de outros fabricantes" + (refeitas > 0 ? $" ({refeitas} refeita(s))" : "") + ".");
            if (jaTinham > 0) sb.AppendLine($"{jaTinham} vão(s) já tinham a peça (marque “Refazer” para trocar).");
            if (puladas > 0) sb.AppendLine($"{puladas} vão(s) pulados pelas opções escolhidas.");
            if (sb.Length == 0) sb.AppendLine("Nada a fazer.");
            if (avisos.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Avisos:");
                foreach (string w in avisos.Distinct().Take(30)) sb.AppendLine("• " + w);
            }
            Show(sb.ToString(), avisos.Count > 0 ? ResultKind.Warning : ResultKind.Success);
            return Result.Succeeded;
        }

        /// <summary>Family name of a stone piece; an off-centre filete gets its offset (" e+60") so each position is its own family.</summary>
        private static string Chave(PedraConfig c) =>
            c.NomeFamilia() + (Math.Abs(c.CentroMm) > 0.5 ? " e" + (c.CentroMm > 0 ? "+" : "") + Math.Round(c.CentroMm).ToString(CultureInfo.InvariantCulture) : "");

        private static string Nome(Element e) => (e.Category?.Name ?? "Elemento") + " " + e.Id.Value;

        /// <summary>Centre of the opening on the wall axis, exterior direction, wall thickness and
        /// opening width (mm), level and elevation of the door base / window sill.</summary>
        internal static bool Vao(FamilyInstance fi, out XYZ centro, out XYZ exterior, out double paredeMm, out double larguraMm, out Level nivel, out double z)
        {
            centro = exterior = null; paredeMm = larguraMm = z = 0; nivel = null;
            if (!(fi.Host is Wall w) || !(w.Location is LocationCurve lc) || !(fi.Location is LocationPoint lp)) return false;
            Document doc = fi.Document;
            Curve eixo = lc.Curve;
            IntersectionResult ir = eixo.Project(new XYZ(lp.Point.X, lp.Point.Y, eixo.GetEndPoint(0).Z));
            if (ir == null) return false;
            XYZ q = ir.XYZPoint;
            exterior = new XYZ(w.Orientation.X, w.Orientation.Y, 0).Normalize();
            double W = w.Width;
            // location line → wall centre (distance s of the location line from the exterior face)
            double s = W / 2;
            int key = w.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM)?.AsInteger() ?? 0;
            if (key == 2) s = 0;
            else if (key == 3) s = W;
            else if (key == 1 || key == 4 || key == 5)
            {
                try
                {
                    CompoundStructure cs = w.WallType.GetCompoundStructure();
                    if (cs != null)
                    {
                        int f0 = cs.GetFirstCoreLayerIndex(), f1 = cs.GetLastCoreLayerIndex();
                        double antes = 0, nucleo = 0;
                        for (int i = 0; i < cs.LayerCount; i++)
                        {
                            if (i < f0) antes += cs.GetLayerWidth(i);
                            else if (i <= f1) nucleo += cs.GetLayerWidth(i);
                        }
                        s = key == 1 ? antes + nucleo / 2 : key == 4 ? antes : antes + nucleo;
                    }
                }
                catch { }
            }
            centro = new XYZ(q.X, q.Y, 0) + exterior * (s - W / 2);
            paredeMm = W * 304.8;
            larguraMm = Largura(fi);
            if (larguraMm < 50) return false;
            nivel = doc.GetElement(fi.LevelId) as Level ?? doc.GetElement(w.LevelId) as Level;
            if (nivel == null) return false;
            double peitoril = 0;
            try { peitoril = fi.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM)?.AsDouble() ?? 0; } catch { }
            z = nivel.ProjectElevation + peitoril;
            return true;
        }

        /// <summary>Opening width (mm): rough width if the family has it, else width.</summary>
        internal static double Largura(FamilyInstance fi)
        {
            Element tipo = fi.Symbol;
            foreach (Element src in new Element[] { fi, tipo })
            {
                if (src == null) continue;
                foreach (BuiltInParameter bip in new[] { BuiltInParameter.FAMILY_ROUGH_WIDTH_PARAM, BuiltInParameter.DOOR_WIDTH, BuiltInParameter.WINDOW_WIDTH, BuiltInParameter.FAMILY_WIDTH_PARAM })
                {
                    try
                    {
                        Parameter p = src.get_Parameter(bip);
                        if (p != null && p.HasValue && p.StorageType == StorageType.Double && p.AsDouble() > 1e-6) return Math.Round(p.AsDouble() * 304.8);
                    }
                    catch { }
                }
                foreach (string n in new[] { "Largura", "Width", "Largura do vão", "Rough Width" })
                {
                    Parameter p = src.LookupParameter(n);
                    if (p != null && p.HasValue && p.StorageType == StorageType.Double && p.AsDouble() > 1e-6) return Math.Round(p.AsDouble() * 304.8);
                }
            }
            return 0;
        }

        private static double Z(FamilyInstance i) => (i.Location as LocationPoint)?.Point.Z ?? 0;

        /// <summary>Offset from the level so the top of the stone is at <paramref name="z"/>.</summary>
        internal static void DefinirAltura(FamilyInstance i, Level nivel, double z)
        {
            foreach (BuiltInParameter bip in new[] { BuiltInParameter.INSTANCE_ELEVATION_PARAM, BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM })
            {
                try
                {
                    Parameter p = i.get_Parameter(bip);
                    if (p != null && !p.IsReadOnly) { p.Set(z - nivel.ProjectElevation); return; }
                }
                catch { }
            }
        }
    }

    /// <summary>
    /// Substituir pelos do JA: doors, windows and countertops of any family become JA ones. Doors
    /// and windows: the configurator opens with the most common size, then one JA type per size
    /// (W × H) is generated and every element gets it (same host, position and sill). Countertops
    /// (Generic Models, Casework, Furniture): the bancada configurator opens with the size of the
    /// first one; each element is replaced by a JA bancada of its own length, depth and height,
    /// at the same place and rotation (the old family's front is taken as −Y, Revit's convention).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class SubstituirPorJaCommand : EsquadriaCommandBase
    {
        public override string Title => "Substituir pelos do JA";

        protected override Result Run()
        {
            var sel = UiDoc.Selection.GetElementIds().Select(id => Doc.GetElement(id)).Where(e => e != null).ToList();
            if (sel.Count == 0)
            {
                try
                {
                    sel = UiDoc.Selection.PickObjects(Autodesk.Revit.UI.Selection.ObjectType.Element, new Filtro(),
                        "Selecione portas, janelas ou bancadas para trocar pelas do JA Famílias e clique em Concluir").Select(r => Doc.GetElement(r)).ToList();
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
            }
            var inst = sel.OfType<FamilyInstance>().Where(Aceita).ToList();
            var portas = inst.Where(i => Cat(i) == BuiltInCategory.OST_Doors && !(i.Host is Wall w && w.CurtainGrid != null)).Cast<Element>().ToList();
            var janelas = inst.Where(i => Cat(i) == BuiltInCategory.OST_Windows && !(i.Host is Wall w && w.CurtainGrid != null)).Cast<Element>().ToList();
            var bancadas = inst.Where(i => Cat(i) != BuiltInCategory.OST_Doors && Cat(i) != BuiltInCategory.OST_Windows).ToList();
            if (portas.Count + janelas.Count + bancadas.Count == 0)
            {
                Show("Selecione portas, janelas ou bancadas (modelos genéricos, gabinetes ou mobiliário) que não sejam do JA Famílias.\n" +
                     "Esquadrias em parede cortina: use Parede Cortina. Para editar uma esquadria JA: Editar Família.", ResultKind.Info);
                return Result.Cancelled;
            }

            Result r = Result.Cancelled;
            if (portas.Count > 0) r = Melhor(r, Esquadrias(TipoEsquadria.Porta, portas));
            if (janelas.Count > 0) r = Melhor(r, Esquadrias(TipoEsquadria.Janela, janelas));
            if (bancadas.Count > 0) r = Melhor(r, Bancadas(bancadas));
            return r;
        }

        private static Result Melhor(Result a, Result b) => a == Result.Succeeded || b == Result.Succeeded ? Result.Succeeded : b;

        private static BuiltInCategory Cat(Element e) => (BuiltInCategory)(e.Category?.Id.Value ?? 0);

        private static bool Aceita(FamilyInstance i)
        {
            BuiltInCategory c = Cat(i);
            bool tipo = c == BuiltInCategory.OST_Doors || c == BuiltInCategory.OST_Windows || c == BuiltInCategory.OST_GenericModel
                        || c == BuiltInCategory.OST_Casework || c == BuiltInCategory.OST_Furniture || c == BuiltInCategory.OST_FurnitureSystems
                        || c == BuiltInCategory.OST_PlumbingFixtures;
            return tipo && ProjectEsquadrias.ConfigOf(i, out _) == null && ProjectPecas.ConfigOf(i, out _) == null;
        }

        private sealed class Filtro : Autodesk.Revit.UI.Selection.ISelectionFilter
        {
            public bool AllowElement(Element e) => e is FamilyInstance fi && Aceita(fi);
            public bool AllowReference(Reference reference, XYZ position) => false;
        }

        // ------------------------------------------------------------------ doors / windows

        private Result Esquadrias(TipoEsquadria tipo, List<Element> elementos)
        {
            EsquadriaConfig start = Start(tipo);
            start.ParedeCortina = false;
            var medidas = elementos.Select(e => { TamanhoPainel(e, out double w, out double h); return new { W = w, H = h }; })
                                   .Where(m => m.W > 1 && m.H > 1).ToList();
            if (medidas.Count > 0)
            {
                var comum = medidas.GroupBy(m => Math.Round(m.W) + "x" + Math.Round(m.H)).OrderByDescending(g => g.Count()).First().First();
                start.LarguraMm = comum.W;
                start.AlturaMm = comum.H;
            }
            double parede = elementos.Select(e => ProjectEsquadrias.HostThicknessMm(e)).FirstOrDefault(t => t > 0);
            if (parede > 0) start.ParedeMm = Math.Round(parede);
            if (tipo == TipoEsquadria.Janela)
            {
                try
                {
                    double sill = elementos[0].get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM)?.AsDouble() ?? 0;
                    if (sill > 0) start.PeitorilMm = Math.Round(sill * 304.8);
                }
                catch { }
            }
            start = start.Normalized();
            return OpenConfigurator(start, tipo == TipoEsquadria.Porta ? FonteCatalogo.Portas : FonteCatalogo.Janelas, null, elementos, cortina: false);
        }

        // ------------------------------------------------------------------ countertops

        private sealed class Caixa
        {
            public FamilyInstance E;
            public Transform T;
            public double X0, X1, Y0, Y1, Z0, Z1;    // family coordinates (ft)
            public Level Nivel;
            public double A => (X1 - X0) * 304.8;
            public double P => (Y1 - Y0) * 304.8;
            /// <summary>Top of the old element above its level (mm) = height of the JA bancada.</summary>
            public double Topo;
            /// <summary>JA origin (back-left corner) in the project, at the level.</summary>
            public XYZ Origem
            {
                get
                {
                    // mirrored instance: the family's −Y side is the back in the project
                    XYZ o = T.OfPoint(new XYZ(X0, T.HasReflection ? Y0 : Y1, Z0));
                    return new XYZ(o.X, o.Y, Nivel.ProjectElevation);
                }
            }
        }

        /// <summary>Box of the instance in its own family coordinates (symbol geometry).</summary>
        private static Caixa Medir(FamilyInstance fi)
        {
            var pts = new List<XYZ>();
            GeometryElement geo = fi.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine });
            if (geo == null) return null;
            Transform t = fi.GetTransform();
            foreach (GeometryObject o in geo)
            {
                if (o is GeometryInstance gi)
                {
                    foreach (GeometryObject s in gi.GetSymbolGeometry())
                        if (s is Solid so && so.Volume > 1e-9)
                            foreach (Edge ed in so.Edges) pts.AddRange(ed.Tessellate().Select(p => gi.Transform.OfPoint(p)));
                }
                else if (o is Solid so && so.Volume > 1e-9)
                {
                    Transform inv = t.Inverse;
                    foreach (Edge ed in so.Edges) pts.AddRange(ed.Tessellate().Select(p => inv.OfPoint(p)));
                }
            }
            if (pts.Count == 0) return null;
            Document doc = fi.Document;
            var c = new Caixa
            {
                E = fi, T = t,
                X0 = pts.Min(p => p.X), X1 = pts.Max(p => p.X), Y0 = pts.Min(p => p.Y), Y1 = pts.Max(p => p.Y), Z0 = pts.Min(p => p.Z), Z1 = pts.Max(p => p.Z)
            };
            double topo = t.OfPoint(new XYZ(c.X0, c.Y0, c.Z1)).Z;
            c.Nivel = doc.GetElement(fi.LevelId) as Level
                      ?? new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().Where(l => l.ProjectElevation <= topo + 1e-6)
                             .OrderByDescending(l => l.ProjectElevation).FirstOrDefault();
            if (c.Nivel == null) return null;
            c.Topo = Math.Round((topo - c.Nivel.ProjectElevation) * 304.8);
            return c;
        }

        private Result Bancadas(List<FamilyInstance> elementos)
        {
            var caixas = elementos.Select(Medir).Where(c => c != null && c.A > 200 && c.P > 150 && c.Topo > 100).ToList();
            if (caixas.Count == 0) { Show("Não consegui medir as bancadas selecionadas (sem geometria).", ResultKind.Warning); return Result.Cancelled; }

            var settings = Settings.Current;
            var last = PecaUtil.FromJson(TipoPeca.Bancada, ToolPrefs.Get("ultima.peca." + TipoPeca.Bancada)) as BancadaConfig;
            var start = (BancadaConfig)(last ?? (BancadaConfig)PecaCatalog.Novo(TipoPeca.Bancada)).Copiar();
            Caixa c0 = caixas[0];
            Ajustar(start, c0);
            var all = ProjectPecas.All(Doc, TipoPeca.Bancada);
            var used = new HashSet<string>(all.Select(a => a.Codigo), StringComparer.OrdinalIgnoreCase);
            if (used.Contains(start.Codigo)) start.Codigo = ProjectPecas.NextCode(used, TipoPeca.Bancada);
            var items = all.Select(a => new KeyValuePair<string, IPecaConfig>(a.Codigo + " · " + a.Instancias + " no modelo", a.Config)).ToList();
            var materials = new FilteredElementCollector(Doc).OfClass(typeof(Material)).Cast<Material>().Select(m => m.Name).ToList();
            PecaResult res = PecaWindow.Show(Owner, Title + " – bancadas (" + caixas.Count + ")", start, false, used, items, materials);
            if (res == null || !(res.Config is BancadaConfig modelo)) return Result.Cancelled;
            ToolPrefs.Set("ultima.peca." + TipoPeca.Bancada, modelo.ToJson());
            ToolPrefs.Save();

            // one JA type per size; the first keeps the chosen code
            var avisos = new List<string>();
            var porTamanho = caixas.GroupBy(c => Math.Round(c.A) + "x" + Math.Round(c.P) + "x" + Math.Round(c.Topo))
                                   .OrderBy(g => g.Contains(c0) ? 0 : 1).ToList();
            used.Add(modelo.Codigo);
            int trocadas = 0;
            string rfa = null;
            for (int gi = 0; gi < porTamanho.Count; gi++)
            {
                var g = porTamanho[gi];
                var cfg = (BancadaConfig)modelo.Copiar();
                Ajustar(cfg, g.First(), manterCubas: true);
                if (gi > 0) { cfg.Codigo = ProjectPecas.NextCode(used, TipoPeca.Bancada); used.Add(cfg.Codigo); }
                cfg.Normalized();
                GenerationResult gen;
                try { gen = PecaGenerator.Generate(UiApp.Application, Doc, cfg, settings); }
                catch (Exception ex) { avisos.Add($"{cfg.Codigo}: {ex.Message}"); continue; }
                rfa = rfa ?? gen.RfaPath;
                avisos.AddRange(gen.Warnings);
                if (gen.Symbol == null) continue;

                using (var t = new Transaction(Doc, "JA Famílias · substituir bancadas por " + cfg.Codigo))
                {
                    t.Start();
                    var opts = t.GetFailureHandlingOptions();
                    opts.SetFailuresPreprocessor(new WarningSwallower());
                    t.SetFailureHandlingOptions(opts);
                    if (!gen.Symbol.IsActive) gen.Symbol.Activate();
                    foreach (Caixa c in g)
                    {
                        try
                        {
                            // JA origin = back-left corner at the level; the top goes to the old element's top
                            XYZ o = c.Origem;
                            Level nivel = c.Nivel;
                            FamilyInstance novo = Doc.Create.NewFamilyInstance(o, gen.Symbol, nivel, StructuralType.NonStructural);
                            double ang = Math.Atan2(c.T.BasisX.Y, c.T.BasisX.X);
                            if (Math.Abs(ang) > 1e-9) ElementTransformUtils.RotateElement(Doc, novo.Id, Line.CreateBound(o, o + XYZ.BasisZ), ang);
                            SoleirasPeitorisCommand.DefinirAltura(novo, nivel, o.Z);
                            try { if (c.E.Pinned) c.E.Pinned = false; Doc.Delete(c.E.Id); } catch (Exception ex) { avisos.Add($"{c.E.Id.Value}: a antiga não foi apagada ({ex.Message})."); }
                            trocadas++;
                        }
                        catch (Exception ex) { avisos.Add($"{c.E.Id.Value}: {ex.Message}"); }
                    }
                    t.Commit();
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine($"{trocadas} bancada(s) trocada(s) por bancadas JA ({porTamanho.Count} tamanho(s)).");
            sb.AppendLine("Confira a frente: famílias de outros fabricantes nem sempre têm a frente para baixo (−Y); se alguma ficou virada, gire 180°.");
            if (avisos.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Avisos:");
                foreach (string w in avisos.Distinct().Take(30)) sb.AppendLine("• " + w);
            }
            if (rfa != null) Show(sb.ToString(), avisos.Count > 0 ? ResultKind.Warning : ResultKind.Success, OpenFolderAction(rfa));
            else Show(sb.ToString(), avisos.Count > 0 ? ResultKind.Warning : ResultKind.Success);
            return Result.Succeeded;
        }

        /// <summary>Length, depth and height of the JA bancada from the old element's box.</summary>
        private static void Ajustar(BancadaConfig b, Caixa c, bool manterCubas = false)
        {
            double a = Math.Round(c.A), p = Math.Round(c.P);
            switch (b.Formato)
            {
                case FormatoBancada.L:
                    b.ComprimentoAMm = a; b.ComprimentoBMm = p;
                    break;
                case FormatoBancada.U:
                    b.ComprimentoAMm = a; b.ComprimentoBMm = p; b.ComprimentoCMm = p;
                    break;
                default:
                    b.ComprimentoAMm = a; b.ProfundidadeMm = p;
                    break;
            }
            b.AlturaMm = c.Topo;
            // a sink that would fall off the new length goes back to the centre (0 = centred)
            foreach (var cu in b.Cubas)
            {
                double limite = manterCubas ? a - cu.LarguraMm / 2 - 50 : a - 100;
                if (cu.PosicaoXMm > limite) cu.PosicaoXMm = 0;
            }
        }
    }
}
