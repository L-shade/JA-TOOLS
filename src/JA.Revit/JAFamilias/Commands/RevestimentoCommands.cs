using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using JAEsquadrias.Common;
using JAEsquadrias.Core;
using JAEsquadrias.Engine;
using JAEsquadrias.Infrastructure;
using Settings = JAEsquadrias.Core.Settings;

namespace JAEsquadrias.Commands
{
    public abstract partial class EsquadriaCommandBase
    {
        /// <summary>Rough colour for a stone name (the project material with that name wins anyway).</summary>
        protected static string CorDaPedra(string nome)
        {
            string n = (nome ?? "").ToLowerInvariant();
            if (n.Contains("preto") || n.Contains("absoluto")) return "2B2B2B";
            if (n.Contains("branco") || n.Contains("carrara") || n.Contains("marfim")) return "E9E6E0";
            if (n.Contains("bege") || n.Contains("travertino")) return "D8C8AE";
            if (n.Contains("verde")) return "4E6B58";
            return "B9B6B0";
        }

        /// <summary>Rooms selected before the command, or picked now (plan view).</summary>
        protected List<Room> AmbientesSelecionados(string prompt)
        {
            var rooms = UiDoc.Selection.GetElementIds().Select(id => Doc.GetElement(id))
                .Select(e => e is RoomTag t ? t.Room : e as Room).Where(r => r != null && r.Area > 0).Distinct().ToList();
            if (rooms.Count > 0) return rooms;
            try
            {
                return UiDoc.Selection.PickObjects(ObjectType.Element, new FiltroAmbiente(), prompt)
                    .Select(r => Doc.GetElement(r) as Room).Where(r => r != null && r.Area > 0).ToList();
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return new List<Room>(); }
        }

        private sealed class FiltroAmbiente : ISelectionFilter
        {
            public bool AllowElement(Element e) => e is Room;
            public bool AllowReference(Reference r, XYZ p) => false;
        }

        /// <summary>The boundary loops of a room (finish faces), each stretch tessellated, with the element of each stretch.</summary>
        protected static List<List<(XYZ a, XYZ b, ElementId el)>> Contornos(Room r)
        {
            var res = new List<List<(XYZ, XYZ, ElementId)>>();
            var op = new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish };
            foreach (IList<BoundarySegment> laco in r.GetBoundarySegments(op) ?? new List<IList<BoundarySegment>>())
            {
                var l = new List<(XYZ, XYZ, ElementId)>();
                foreach (BoundarySegment seg in laco)
                {
                    Curve c = seg.GetCurve();
                    if (c == null) continue;
                    IList<XYZ> t = c is Line ? new List<XYZ> { c.GetEndPoint(0), c.GetEndPoint(1) } : c.Tessellate();
                    for (int i = 0; i + 1 < t.Count; i++) l.Add((t[i], t[i + 1], seg.ElementId));
                }
                if (l.Count > 0) res.Add(l);
            }
            return res;
        }
    }

    /// <summary>Settings of a rodapé run, kept on each stretch (Atualizar Revestimentos redoes it with them).</summary>
    [System.Runtime.Serialization.DataContract]
    public sealed class ConfigRodape
    {
        [System.Runtime.Serialization.DataMember(Order = 1)] public double Altura { get; set; }
        [System.Runtime.Serialization.DataMember(Order = 2)] public double Espessura { get; set; }
        [System.Runtime.Serialization.DataMember(Order = 3)] public string Material { get; set; }
        [System.Runtime.Serialization.DataMember(Order = 4)] public bool Portas { get; set; }
        [System.Runtime.Serialization.DataMember(Order = 5)] public double Folga { get; set; }
        [System.Runtime.Serialization.DataMember(Order = 6)] public bool Moveis { get; set; }
        [System.Runtime.Serialization.DataMember(Order = 7)] public double Minimo { get; set; }
        [System.Runtime.Serialization.DataMember(Order = 8)] public bool Acabamento { get; set; }
        [System.Runtime.Serialization.DataMember(Order = 9)] public bool Janelas { get; set; }          // windows down to the floor (portas-janela)
        [System.Runtime.Serialization.DataMember(Order = 10)] public bool Aberturas { get; set; }       // wall openings (vãos sem porta)
        [System.Runtime.Serialization.DataMember(Order = 11)] public bool Retorno { get; set; }         // the rodapé turns into the jambs of the openings
        [System.Runtime.Serialization.DataMember(Order = 12)] public bool SemCortina { get; set; }      // no rodapé on curtain walls / storefronts
        [System.Runtime.Serialization.DataMember(Order = 13)] public string Revestidas { get; set; }    // walls finished with these (type / room-side layer): no rodapé
        [System.Runtime.Serialization.DataMember(Order = 14)] public bool Cantos { get; set; }          // close the outside corners (extend by the thickness)
        [System.Runtime.Serialization.DataMember(Order = 15)] public double Perda { get; set; }         // % for the purchase
        [System.Runtime.Serialization.DataMember(Order = 16)] public double Barra { get; set; }         // length of one piece (mm; 0 = by the metre)
        [System.Runtime.Serialization.DataMember(Order = 17)] public bool Prender { get; set; }         // locked to the wall face (moves with the wall)

        public ConfigRodape() { Padroes(); }

        /// <summary>DataContract does not run the constructor: the defaults for fields missing in older JSON.</summary>
        [System.Runtime.Serialization.OnDeserializing] private void AoLer(System.Runtime.Serialization.StreamingContext c) => Padroes();

        private void Padroes()
        {
            Altura = 70;
            Espessura = 15;
            Material = "Granito cinza";
            Portas = true;
            Moveis = true;
            Minimo = 60;
            Acabamento = true;
            Janelas = true;
            Aberturas = true;
            Retorno = false;
            SemCortina = true;
            Revestidas = "cerâmica, ceramica, porcelanato, azulejo, pastilha";
            Cantos = true;
            Perda = 10;
            Barra = 0;
            Prender = true;
        }

        public string[] PalavrasRevestidas() => (Revestidas ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0).ToArray();
    }

    public abstract partial class EsquadriaCommandBase
    {
        protected sealed class ResultadoRodape
        {
            public string Codigo;
            public int Trechos, Substituidos, Presos, NaoPresos;
            public double Metros;
            public readonly List<string> PorAmbiente = new List<string>();
            /// <summary>Per room: bruto, the discounts by kind, líquido, retornos (m).</summary>
            public readonly List<LinhaRodape> Linhas = new List<LinhaRodape>();
            /// <summary>The pieces placed (selected at the end, so the user sees them).</summary>
            public readonly List<ElementId> Ids = new List<ElementId>();
        }

        private static void AddUm(List<string> l, string s) { if (!l.Contains(s)) l.Add(s); }

        /// <summary>
        /// Rodapé along the boundary of the rooms (finish faces, walls and columns only), open at the
        /// doors and at the furniture against the wall; the rooms' old JA rodapé is replaced when asked.
        /// Generates the family first (outside a transaction), then places in one transaction.
        /// </summary>
        /// <summary>
        /// Rodapés from older versions (no stored settings, or not even marked as rodapé): each stretch
        /// gets its room and settings rebuilt from its family (height, thickness, stone) and the values
        /// the Rodapé dialog remembers. Then Atualizar Revestimentos can redo them. Its own transaction.
        /// </summary>
        protected int AdotarRodapesAntigos(List<string> avisos)
        {
            var achados = new List<(FamilyInstance Fi, DadosJa D, string Json)>();
            double N(string k, double d) => double.TryParse(ToolPrefs.Get("Rodape." + k), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : d;
            bool B(string k, bool d) { string v = ToolPrefs.Get("Rodape." + k); return v == "1" ? true : v == "0" ? false : d; }
            int semAmbiente = 0;
            foreach (FamilyInstance fi in new FilteredElementCollector(Doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>())
            {
                if (!(ProjectPecas.ConfigOf(fi, out _) is PedraConfig pc) || pc.Uso != UsoPedra.Rodape) continue;
                DadosJa d = JaDados.Ler(fi);
                bool valido = false;
                // with settings: made by this version (rooms) or along a croqui (Ambiente = 0): leave them
                if (d != null && d.Tipo == "rodape" && !string.IsNullOrWhiteSpace(d.Config))
                    try { valido = Json.Deserialize<ConfigRodape>(d.Config) != null; } catch { }
                if (valido) continue;
                long amb = d != null && d.Tipo == "rodape" ? d.Ambiente : 0;
                if (amb == 0 || !(Doc.GetElement(new ElementId(amb)) is Room))
                {
                    // the stone lies on the left of the line (+Y of the family): the room is there
                    Room r = null;
                    if (fi.Location is LocationCurve lc && lc.Curve != null)
                    {
                        XYZ a = lc.Curve.GetEndPoint(0), b = lc.Curve.GetEndPoint(1), m = lc.Curve.Evaluate(0.5, true);
                        XYZ dir = new XYZ(b.X - a.X, b.Y - a.Y, 0);
                        if (dir.GetLength() > 1e-6)
                        {
                            XYZ esq = XYZ.BasisZ.CrossProduct(dir.Normalize());
                            foreach (double s in new[] { 0.15, -0.15 })
                            {
                                r = Doc.GetRoomAtPoint(m + esq * s + XYZ.BasisZ * 0.5);
                                if (r != null) break;
                            }
                        }
                    }
                    if (r == null) { semAmbiente++; continue; }
                    amb = r.Id.Value;
                }
                var c = new ConfigRodape
                {
                    Altura = pc.LarguraMm, Espessura = pc.EspessuraMm, Material = pc.Material,
                    Portas = B("portas", true), Folga = N("folga", 0), Moveis = B("moveis", true), Minimo = N("minimo", 60), Acabamento = B("acabamento", true)
                };
                achados.Add((fi, new DadosJa { Tipo = "rodape", Ambiente = amb }, Json.Serialize(c)));
            }
            if (semAmbiente > 0) avisos.Add(semAmbiente + " trecho(s) de rodapé antigo(s) fora de qualquer ambiente: ficaram como estavam.");
            if (achados.Count == 0) return 0;
            // stretches of one room must share one setting (the first one found)
            var porAmb = achados.GroupBy(x => x.D.Ambiente).ToDictionary(g => g.Key, g => g.First().Json);
            using (var t = new Transaction(Doc, "JA Famílias – rodapés antigos"))
            {
                t.Start();
                foreach (var x in achados)
                {
                    x.D.Config = porAmb[x.D.Ambiente];
                    JaDados.Gravar(x.Fi, x.D);
                }
                t.Commit();
            }
            return achados.Count;
        }

        /// <summary>The discount and finishing fields of the rodapé, the same in Rodapé and Rodapé por Ambiente.</summary>
        protected sealed class CamposRodape
        {
            public OptionsDialog.BoolField Portas, Janelas, Aberturas, Retorno, Cortina, Moveis, Cantos, Refazer, Acabamento, Prender, Previa;
            public OptionsDialog.NumberField Folga, Minimo;
            public OptionsDialog.TextField Revestidas;

            public void Aplicar(ConfigRodape c)
            {
                c.Portas = Portas.Value; c.Folga = Folga.Value; c.Janelas = Janelas.Value; c.Aberturas = Aberturas.Value; c.Retorno = Retorno.Value;
                c.SemCortina = Cortina.Value; c.Revestidas = Revestidas.Value ?? ""; c.Moveis = Moveis.Value; c.Minimo = Minimo.Value; c.Cantos = Cantos.Value; c.Acabamento = Acabamento.Value;
                c.Prender = Prender.Value;
            }
        }

        /// <param name="local">Rodapé no local: no "replace the room's rodapé", Acabamento de base only when empty.</param>
        protected static CamposRodape DescontosRodape(OptionsDialog dlg, bool local = false)
        {
            var f = new CamposRodape();
            dlg.Section("Descontos");
            f.Portas = dlg.Check("portas", "Portas", true, "O rodapé para no batente: a largura da porta (mais a folga) fica sem pedra.");
            f.Folga = dlg.Number("folga", "Folga de cada lado das portas e janelas (mm)", 0, -200, 300, "Positiva aumenta a abertura (ex.: guarnição); negativa a diminui.");
            f.Janelas = dlg.Check("janelas", "Janelas que descem até o rodapé (portas-janela, janelas até o piso)", true);
            f.Aberturas = dlg.Check("aberturas", "Vãos sem porta (aberturas de parede até o piso)", true);
            f.Retorno = dlg.Check("retorno", "Dobrar o rodapé nas laterais dos vãos sem porta (metade da espessura da parede)", false);
            f.Retorno.EnabledWhen(f.Aberturas);
            f.Cortina = dlg.Check("cortina", "Paredes cortina e vitrines", true, "Sem rodapé no vidro.");
            f.Revestidas = dlg.Text("revestidas", "Paredes revestidas (sem rodapé): tipo ou camada do lado do ambiente contém", "cerâmica, ceramica, porcelanato, azulejo, pastilha",
                "Vazio = rodapé em todas as paredes. Confere o nome do tipo de parede e o material da camada do lado do ambiente.");
            f.Moveis = dlg.Check("moveis", "Bancadas, armários e móveis encostados na parede", true,
                "Componentes de gabinete, mobiliário, modelos genéricos e louças que tocam a faixa do rodapé (até 15 cm da parede).");
            f.Minimo = dlg.Number("minimo", "Trechos menores que isto ficam de fora (mm)", 60, 0, 1000);
            dlg.Section("Acabamento");
            f.Cantos = dlg.Check("cantos", "Acertar os cantos (internos topo a topo, quinas externas fechadas)", true, "Sem pedra dobrada nos cantos e sem buraco na quina de pilares e paredes que avançam no ambiente.");
            f.Prender = dlg.Check("prender", "Prender o rodapé na parede (alinhar e cadear)", true,
                "Movendo a parede, o rodapé vai junto. O cadeado aparece ao selecionar o rodapé numa planta do nível.");
            if (!local) f.Previa = dlg.Check("previa", "Mostrar a prévia (metros, descontos e compra) antes de colocar", true);
            if (!local) f.Refazer = dlg.Check("refazer", "Substituir o rodapé que estes ambientes já têm", true, "Sem marcar, um rodapé novo é somado ao que já existe.");
            f.Acabamento = local
                ? dlg.Check("acabamento", "Escrever o rodapé no Acabamento de base do ambiente, se estiver vazio", true, "Aparece na coluna Rodapé do Quadro de Acabamentos (JA Tools).")
                : dlg.Check("acabamento", "Escrever o rodapé no Acabamento de base do ambiente", true, "Aparece na coluna Rodapé do Quadro de Acabamentos (JA Tools).");
            return f;
        }

        /// <summary>The summary of a rodapé result (and its Excel, if asked); shown to the user.</summary>
        protected void MostrarRodape(List<(ConfigRodape c, ResultadoRodape r)> feitos, bool excel, List<string> avisos, string cabecalho = null)
        {
            // what was placed stays selected (Esc clears it)
            try
            {
                var ids = feitos.Where(f => f.r != null).SelectMany(f => f.r.Ids).Where(id => Doc.GetElement(id) != null).ToList();
                if (ids.Count > 0) UiDoc.Selection.SetElementIds(ids);
            }
            catch { }
            var br = PecaUtil.Pt;
            string N(double v) => v.ToString("0.00", br);
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(cabecalho)) sb.AppendLine(cabecalho.TrimEnd()).AppendLine();
            double totalCompra = 0;
            foreach (var (c, r0) in feitos)
            {
                double compra = r0.Metros * (1 + c.Perda / 100);
                totalCompra += compra;
                string pecas = c.Barra > 0 ? $" = {Math.Ceiling(compra * 1000 / c.Barra - 1e-9)} peça(s) de {PecaUtil.Mm(c.Barra)} mm" : "";
                sb.AppendLine($"Rodapé {r0.Codigo} ({PecaUtil.Mm(c.Altura)} × {PecaUtil.Mm(c.Espessura)} mm, {c.Material}): {r0.Trechos} trecho(s), {N(r0.Metros)} m" +
                              (r0.Substituidos > 0 ? $"; {r0.Substituidos} trecho(s) antigo(s) substituído(s)" : "") + $" → comprar {N(compra)} m com {c.Perda:0}% de perda{pecas}.");
                if (r0.Presos + r0.NaoPresos > 0)
                    sb.AppendLine($"   Presos na parede (cadeado): {r0.Presos} trecho(s)" + (r0.NaoPresos > 0 ? $"; {r0.NaoPresos} ficaram soltos (sem planta do nível ou o Revit recusou)" : "") + ".");
                if (r0.Linhas.Count > 0)
                {
                    double Soma(Func<LinhaRodape, double> f) => r0.Linhas.Sum(f);
                    sb.AppendLine($"   Paredes {N(Soma(x => x.Bruto))} m − portas {N(Soma(x => x.Portas))} − janelas {N(Soma(x => x.Janelas))} − vãos {N(Soma(x => x.Vaos))}" +
                                  $" − móveis {N(Soma(x => x.Moveis))} − cortina {N(Soma(x => x.Cortina))} − revestidas {N(Soma(x => x.Revestidas))} − curtos {N(Soma(x => x.Curtos))}" +
                                  (Soma(x => x.Retornos) > 0 ? $" + retornos {N(Soma(x => x.Retornos))}" : "") + " m");
                    sb.AppendLine(string.Join("\n", r0.PorAmbiente.Take(30).Select(x => "   " + x)));
                }
                sb.AppendLine();
            }
            if (feitos.Count > 1) sb.AppendLine($"Total a comprar: {N(totalCompra)} m (somando os {feitos.Count} rodapés).").AppendLine();
            string pasta = null;
            if (excel && feitos.Any(x => x.r.Linhas.Count > 0))
            {
                var tab = new XlsxWriter.Table { Name = "Rodapé", Title = "Rodapés — descontos por ambiente" };
                tab.Rows.Add(new[] { "Ambiente", "Rodapé", "Material", "Altura × espessura (mm)", "Paredes (m)", "Portas (m)", "Janelas (m)", "Vãos (m)", "Móveis (m)", "Cortina (m)", "Revestidas (m)", "Curtos (m)", "Retornos (m)", "Líquido (m)", "Comprar (m)" });
                foreach (var (c, r0) in feitos)
                    foreach (var x in r0.Linhas)
                        tab.Rows.Add(new[] { x.Ambiente, r0.Codigo, c.Material, PecaUtil.Mm(c.Altura) + " × " + PecaUtil.Mm(c.Espessura), N(x.Bruto), N(x.Portas), N(x.Janelas), N(x.Vaos), N(x.Moveis), N(x.Cortina),
                                             N(x.Revestidas), N(x.Curtos), N(x.Retornos), N(x.Liquido + x.Retornos), N((x.Liquido + x.Retornos) * (1 + c.Perda / 100)) });
                tab.Rows.Add(new[] { "Total", "", "", "", N(feitos.Sum(f => f.r.Linhas.Sum(x => x.Bruto))), "", "", "", "", "", "", "", "", N(feitos.Sum(f => f.r.Metros)), N(totalCompra) });
                var xw = new XlsxWriter();
                xw.Add(tab);
                string path = SalvarEm("Rodapé", ".xlsx", xw.Save);
                pasta = System.IO.Path.GetDirectoryName(path);
                sb.AppendLine(path).AppendLine();
            }
            sb.Append("Os trechos entram na Lista para a Marmoraria, na Lista de Compras e no Quadro de Peças (JA_Comprimento); Atualizar Revestimentos refaz cada ambiente com o seu rodapé.");
            if (avisos.Count > 0) sb.Append("\n\nAvisos:\n• " + string.Join("\n• ", avisos.Distinct().Take(15)));
            var tipo = avisos.Count > 0 ? ResultKind.Warning : ResultKind.Success;
            if (pasta != null) Show(sb.ToString().TrimEnd(), tipo, OpenFolderAction(pasta));
            else Show(sb.ToString().TrimEnd(), tipo);
        }

        protected sealed class LinhaRodape
        {
            public string Ambiente;
            public double Bruto, Portas, Janelas, Vaos, Moveis, Cortina, Revestidas, Curtos, Retornos, Liquido;
        }

        /// <summary>The stone family of this rodapé (same height, thickness and stone = the same family), generated if needed.</summary>
        protected FamilySymbol SimboloRodape(ConfigRodape c, List<string> avisos, out PedraConfig cfg)
        {
            cfg = new PedraConfig
            {
                Uso = UsoPedra.Rodape, LarguraMm = c.Altura, EspessuraMm = c.Espessura, ComprimentoMm = 1000,
                Material = string.IsNullOrWhiteSpace(c.Material) ? "Granito cinza" : c.Material.Trim()
            };
            cfg.Cor = CorDaPedra(cfg.Material);
            PedraConfig alvo = cfg;
            var mesmo = ProjectPecas.All(Doc, TipoPeca.Pedra).FirstOrDefault(p => p.Config is PedraConfig pc && pc.Uso == UsoPedra.Rodape &&
                Math.Abs(pc.LarguraMm - alvo.LarguraMm) < 0.5 && Math.Abs(pc.EspessuraMm - alvo.EspessuraMm) < 0.5 && string.Equals(pc.Material, alvo.Material, StringComparison.OrdinalIgnoreCase));
            cfg.Codigo = mesmo != null ? mesmo.Codigo : ProjectPecas.NextCode(ProjectPecas.UsedCodes(Doc, TipoPeca.Pedra), "RD");
            cfg.Normalized();
            if (mesmo?.Symbol != null) return mesmo.Symbol;
            GenerationResult gen = PecaGenerator.Generate(UiApp.Application, Doc, cfg, Settings.Current, cfg.NomeFamilia(), null);
            avisos.AddRange(gen.Warnings);
            if (gen.Symbol == null) avisos.Add("A família do rodapé não foi gerada.");
            return gen.Symbol;
        }

        /// <summary>
        /// Rodapé along the walls of each room (finish faces), with the discounts: doors (+ gap), windows
        /// down to the floor, wall openings (optionally turning into their jambs), curtain walls, walls
        /// finished with tiles (type or the room-side layer), furniture against the wall and stretches too
        /// short; outside corners closed by the thickness. The room's old rodapé is replaced. Its own transaction.
        /// </summary>
        protected ResultadoRodape AplicarRodapes(List<Room> rooms, ConfigRodape c, bool refazer, List<string> avisos, List<Room> outros = null)
        {
            var res = new ResultadoRodape();
            List<Room> vizinhos = Vizinhos(rooms, outros);
            FamilySymbol sym = SimboloRodape(c, avisos, out PedraConfig cfg);
            res.Codigo = cfg.Codigo;
            if (sym == null) return res;

            CtxRodape k = PrepararRodape(c);
            // the rodapés these rooms already have (made by this tool)
            var antigos = new Dictionary<long, List<ElementId>>();
            if (refazer)
                foreach (FamilyInstance fi in new FilteredElementCollector(Doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>())
                {
                    DadosJa dj = JaDados.Ler(fi);
                    if (dj == null || dj.Tipo != "rodape") continue;
                    if (!antigos.TryGetValue(dj.Ambiente, out var l)) antigos[dj.Ambiente] = l = new List<ElementId>();
                    l.Add(fi.Id);
                }
            string json = Json.Serialize(c);
            // the pieces and their locks: one Ctrl+Z (the family is already loaded here: safe in a group)
            var grupo = new TransactionGroup(Doc, "JA Famílias – rodapé");
            try { grupo.Start(); } catch { grupo = null; }
            try
            {
            using (var t = new Transaction(Doc, "JA Famílias – rodapé"))
            {
                t.Start();
                var o = t.GetFailureHandlingOptions(); o.SetFailuresPreprocessor(new WarningSwallower()); t.SetFailureHandlingOptions(o);
                int iAmb = 0;
                foreach (Room r in rooms)
                {
                    ToolProgress.Report(iAmb++, rooms.Count, "Rodapé: " + (r.Number + " " + r.Name).Trim());
                    Level nivel = Doc.GetElement(r.LevelId) as Level;
                    if (nivel == null) continue;
                    double z = nivel.ProjectElevation + r.BaseOffset;
                    var lr = new LinhaRodape { Ambiente = (r.Number + " " + r.Name).Trim() };
                    if (antigos.TryGetValue(r.Id.Value, out var velhos))
                    {
                        try { Doc.Delete(velhos); res.Substituidos += velhos.Count; } catch (Exception ex) { AddUm(avisos, r.Name + ": rodapé antigo não apagado (" + ex.Message + ")"); }
                    }
                    FamilyInstance Colocar(XYZ p0, XYZ p1)
                    {
                        try
                        {
                            FamilyInstance novo = ChapimPlacer.PlaceLine(Doc, sym, p0, p1, nivel, z, avisos);
                            if (novo != null)
                            {
                                res.Trechos++;
                                res.Ids.Add(novo.Id);
                                JaDados.Gravar(novo, new DadosJa { Tipo = "rodape", Ambiente = r.Id.Value, Config = json });
                                RevitDwgUnifier.Tools.Parameters.ProjectParams.GravarNivel(novo, nivel);   // JA_Nivel + schedule level
                            }
                            return novo;
                        }
                        catch (Exception ex) { AddUm(avisos, r.Name + ": " + ex.Message); return null; }
                    }
                    foreach (var laco in LacosOrientados(r, z))
                    {
                        // every stretch first, then the corners from what really reaches them
                        var calc = laco.Select(x => TrechoDoAmbiente(k, c, r, x.a, x.b, Doc.GetElement(x.el), z, lr)).ToList();
                        ColocarTrechos(calc, true, c, k, Colocar, nivel.Id, laco.Select(x => (x.a, x.b)).ToList(), Vizinho(vizinhos, r, z));
                    }
                    res.Metros += lr.Liquido + lr.Retornos;
                    res.Linhas.Add(lr);
                    if (c.Acabamento && lr.Liquido > 0)
                    {
                        try { r.get_Parameter(BuiltInParameter.ROOM_FINISH_BASE)?.Set(cfg.Material + " h " + PecaUtil.Mm(cfg.LarguraMm) + " mm"); } catch { }
                    }
                    var br = PecaUtil.Pt;
                    double desc = lr.Portas + lr.Janelas + lr.Vaos + lr.Moveis + lr.Cortina + lr.Revestidas + lr.Curtos;
                    res.PorAmbiente.Add($"• {lr.Ambiente}: {(lr.Liquido + lr.Retornos).ToString("0.00", br)} m (paredes {lr.Bruto.ToString("0.00", br)} m − descontos {desc.ToString("0.00", br)} m" +
                                        (lr.Retornos > 0 ? $" + retornos {lr.Retornos.ToString("0.00", br)} m" : "") + ")");
                }
                t.Commit();
            }
            PrenderRodapes(k, res);
            }
            finally
            {
                if (grupo != null)
                {
                    try { if (grupo.GetStatus() == TransactionStatus.Started) grupo.Assimilate(); } catch { }
                    grupo.Dispose();
                }
            }
            return res;
        }

        /// <summary>
        /// One boundary stretch of a room. Along a column the stone must face the room: a stretch whose left (30 mm out)
        /// is not in the room while its right is (column faces that came turned in the boundary) is turned alone, so the
        /// stone never goes into the column.
        /// </summary>
        private TrechoCalc TrechoDoAmbiente(CtxRodape k, ConfigRodape c, Room r, XYZ a, XYZ b, Element host, double z, LinhaRodape lr)
        {
            bool virado = false;
            bool coluna = host is FamilyInstance fic && fic.Category != null &&
                          (fic.Category.Id.Value == (long)BuiltInCategory.OST_Columns || fic.Category.Id.Value == (long)BuiltInCategory.OST_StructuralColumns);
            double L = a.DistanceTo(b);
            if (coluna && L > 1e-6)
            {
                XYZ d = (b - a) / L, esq = XYZ.BasisZ.CrossProduct(d), m = (a + b) / 2;
                bool Dentro(XYZ p) { try { return r.IsPointInRoom(new XYZ(p.X, p.Y, z + 1)); } catch { return true; } }
                if (!Dentro(m + esq * (30 * MmFt)) && Dentro(m - esq * (30 * MmFt))) { XYZ t = a; a = b; b = t; virado = true; }
            }
            TrechoCalc tc = CalcularTrecho(k, c, a, b, host, z, false, lr);
            if (tc != null && virado) tc.Virado = true;
            return tc;
        }

        /// <summary>The loops of a room in plan, each turned so that the room is on the left of every stretch (the stone side).</summary>
        private List<List<(XYZ a, XYZ b, ElementId el)>> LacosOrientados(Room r, double z)
        {
            var res = new List<List<(XYZ a, XYZ b, ElementId el)>>();
            foreach (var laco0 in Contornos(r))
            {
                var laco = laco0.Select(x => (a: new XYZ(x.a.X, x.a.Y, 0), b: new XYZ(x.b.X, x.b.Y, 0), el: x.el)).Where(x => x.a.DistanceTo(x.b) > 1e-6).ToList();
                if (laco.Count == 0) continue;
                var maior = laco.OrderByDescending(x => x.a.DistanceTo(x.b)).First();
                XYZ dm = (maior.b - maior.a).Normalize(), tst = (maior.a + maior.b) / 2 + XYZ.BasisZ.CrossProduct(dm) * (60 * MmFt);
                if (!r.IsPointInRoom(new XYZ(tst.X, tst.Y, z + 1)))
                {
                    laco.Reverse();
                    laco = laco.Select(x => (a: x.b, b: x.a, el: x.el)).ToList();
                }
                res.Add(laco);
            }
            return res;
        }

        /// <summary>
        /// The rooms whose rodapé meets these at a room separation line: the ones of this run, the other rooms of the
        /// same tool run (outros) and the rooms that already have rodapé of the JA.
        /// </summary>
        private List<Room> Vizinhos(List<Room> rooms, List<Room> outros)
        {
            var ids = new HashSet<long>(rooms.Select(x => x.Id.Value));
            var res = new List<Room>(rooms);
            foreach (Room o in outros ?? new List<Room>()) if (ids.Add(o.Id.Value)) res.Add(o);
            foreach (FamilyInstance fi in new FilteredElementCollector(Doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>())
            {
                DadosJa d = JaDados.Ler(fi);
                if (d == null || d.Tipo != "rodape" || d.Ambiente == 0 || !ids.Add(d.Ambiente)) continue;
                if (Doc.GetElement(new ElementId(d.Ambiente)) is Room o && o.Area > 0) res.Add(o);
            }
            return res;
        }

        /// <summary>Whether a point of the plan is in another of these rooms (with rodapé) than r.</summary>
        private static Func<XYZ, bool> Vizinho(List<Room> vizinhos, Room r, double z) =>
            p => vizinhos.Any(o => o.Id != r.Id && o.IsPointInRoom(new XYZ(p.X, p.Y, z + 1)));

        /// <summary>The rodapé of these rooms worked out without changing the model (the preview): metres and discounts.</summary>
        protected ResultadoRodape PreverRodapes(List<Room> rooms, ConfigRodape c, List<Room> outros = null)
        {
            var res = new ResultadoRodape();
            List<Room> vizinhos = Vizinhos(rooms, outros);
            CtxRodape k = PrepararRodape(c);
            foreach (Room r in rooms)
            {
                if (!(Doc.GetElement(r.LevelId) is Level nivel)) continue;
                double z = nivel.ProjectElevation + r.BaseOffset;
                var lr = new LinhaRodape { Ambiente = (r.Number + " " + r.Name).Trim() };
                foreach (var laco in LacosOrientados(r, z))
                {
                    var calc = laco.Select(x => TrechoDoAmbiente(k, c, r, x.a, x.b, Doc.GetElement(x.el), z, lr)).ToList();
                    ColocarTrechos(calc, true, c, k, null, nivel.Id, laco.Select(x => (x.a, x.b)).ToList(), Vizinho(vizinhos, r, z));
                }
                res.Metros += lr.Liquido + lr.Retornos;
                res.Linhas.Add(lr);
            }
            return res;
        }

        /// <summary>
        /// The preview of Rodapé / Rodapé por Ambiente: per rodapé the metres of wall, each discount, the
        /// rodapé and the purchase, and the rooms with most rodapé. Nothing is placed until "Colocar".
        /// </summary>
        protected bool ConfirmarRodape(List<(ConfigRodape c, List<Room> rooms)> grupos)
        {
            var br = PecaUtil.Pt;
            string N(double v) => v.ToString("0.00", br);
            var conf = new OptionsDialog(Title, "RodapePrevia", "Colocar");
            double total = 0, compraTotal = 0;
            var todosAmb = grupos.SelectMany(g => g.rooms).ToList();
            foreach (var (c, rooms) in grupos)
            {
                ResultadoRodape p = PreverRodapes(rooms, c, todosAmb);
                double Soma(Func<LinhaRodape, double> f) => p.Linhas.Sum(f);
                double compra = p.Metros * (1 + c.Perda / 100);
                conf.Section($"{c.Material} — {PecaUtil.Mm(c.Altura)} × {PecaUtil.Mm(c.Espessura)} mm: {rooms.Count} ambiente(s)");
                conf.Note($"Paredes {N(Soma(x => x.Bruto))} m; descontos: portas {N(Soma(x => x.Portas))}, janelas {N(Soma(x => x.Janelas))}, vãos {N(Soma(x => x.Vaos))}, " +
                          $"móveis {N(Soma(x => x.Moveis))}, vidro {N(Soma(x => x.Cortina))}, revestidas {N(Soma(x => x.Revestidas))}, curtos {N(Soma(x => x.Curtos))} m.");
                conf.Note($"Rodapé: {N(p.Metros)} m" + (Soma(x => x.Retornos) > 0 ? $" (com {N(Soma(x => x.Retornos))} m de retornos)" : "") +
                          $" → comprar {N(compra)} m com {c.Perda:0}% de perda" + (c.Barra > 0 ? $" = {Math.Ceiling(compra * 1000 / c.Barra - 1e-9)} peça(s) de {PecaUtil.Mm(c.Barra)} mm." : "."));
                foreach (LinhaRodape x in p.Linhas.OrderByDescending(x => x.Liquido + x.Retornos).Take(6))
                    conf.Note($"   • {x.Ambiente}: {N(x.Liquido + x.Retornos)} m");
                if (p.Linhas.Count > 6) conf.Note($"   … e mais {p.Linhas.Count - 6} ambiente(s).");
                total += p.Metros; compraTotal += compra;
            }
            if (grupos.Count > 1) conf.Note($"Total: {N(total)} m de rodapé; comprar {N(compraTotal)} m.");
            conf.Note("Nada foi colocado ainda: Colocar faz o rodapé (um Ctrl+Z desfaz tudo); Cancelar volta sem mudar o modelo.");
            return conf.Show(Owner);
        }

        /// <summary>What the rodapé discounts look at (collected once per run).</summary>
        protected sealed class CtxRodape
        {
            public List<FamilyInstance> Portas, Janelas;
            public List<Opening> Aberturas;
            /// <summary>Furniture against the walls: plan footprint (turned with the piece) and heights.</summary>
            public readonly List<(XYZ[] cantos, double z0, double z1)> Obstaculos = new List<(XYZ[], double, double)>();
            public string[] Revestidas;
            public readonly Dictionary<long, bool> Cache = new Dictionary<long, bool>();   // wall finished with tiles, per wall id + side
            /// <summary>Solids of doors / windows (their points, model coordinates), to stop the rodapé at the frame.</summary>
            public readonly Dictionary<long, List<XYZ[]>> Solidos = new Dictionary<long, List<XYZ[]>>();
            public double Folga, Minimo, E, HRod;
            /// <summary>Pieces placed along a wall, to lock to its face once they exist (PrenderRodapes).</summary>
            public readonly List<(ElementId peca, ElementId parede, XYZ p0, XYZ p1, double z, ElementId nivel)> Prender = new List<(ElementId, ElementId, XYZ, XYZ, double, ElementId)>();
        }

        protected CtxRodape PrepararRodape(ConfigRodape c)
        {
            var k = new CtxRodape { Folga = c.Folga * MmFt, Minimo = c.Minimo * MmFt, E = c.Espessura * MmFt, HRod = c.Altura * MmFt };
            k.Portas = new FilteredElementCollector(Doc).OfCategory(BuiltInCategory.OST_Doors).WhereElementIsNotElementType().OfType<FamilyInstance>().ToList();
            k.Janelas = c.Janelas ? new FilteredElementCollector(Doc).OfCategory(BuiltInCategory.OST_Windows).WhereElementIsNotElementType().OfType<FamilyInstance>().ToList() : new List<FamilyInstance>();
            k.Aberturas = c.Aberturas ? new FilteredElementCollector(Doc).OfClass(typeof(Opening)).Cast<Opening>().Where(o => o.Host is Wall).ToList() : new List<Opening>();
            k.Revestidas = c.PalavrasRevestidas();
            // what stands against the walls (rodapé stops there): bounding boxes
            if (c.Moveis)
            {
                var cats = new List<BuiltInCategory> { BuiltInCategory.OST_Casework, BuiltInCategory.OST_Furniture, BuiltInCategory.OST_GenericModel,
                                                       BuiltInCategory.OST_PlumbingFixtures, BuiltInCategory.OST_SpecialityEquipment };
                foreach (FamilyInstance fi in new FilteredElementCollector(Doc).WherePasses(new ElementMulticategoryFilter(cats)).WhereElementIsNotElementType().OfType<FamilyInstance>())
                {
                    IPecaConfig pc = ProjectPecas.ConfigOf(fi, out _);
                    if (pc is PedraConfig || pc is VidroConfig || pc is RaloConfig || pc is ChapimConfig) continue;
                    var pegada = Pegada(fi);
                    if (pegada != null) k.Obstaculos.Add(pegada.Value);
                }
            }
            return k;
        }

        /// <summary>
        /// Plan footprint of a piece of furniture as it stands, and its heights. Revit's bounding box is aligned to the
        /// project axes: around a sofa turned with a turned plan it is much larger than the sofa (a 2 m sofa at 37° took
        /// 4,7 m of rodapé). So the box of the family's own geometry, turned with the instance; Revit's box when the
        /// family is tilted or its geometry is not a single instance.
        /// </summary>
        private static (XYZ[] cantos, double z0, double z1)? Pegada(FamilyInstance fi)
        {
            BoundingBoxXYZ bb = fi.get_BoundingBox(null);
            if (bb == null) return null;
            XYZ[] cantos = { new XYZ(bb.Min.X, bb.Min.Y, 0), new XYZ(bb.Max.X, bb.Min.Y, 0), new XYZ(bb.Max.X, bb.Max.Y, 0), new XYZ(bb.Min.X, bb.Max.Y, 0) };
            try
            {
                GeometryElement ge = fi.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Coarse });
                GeometryInstance gi = ge?.OfType<GeometryInstance>().FirstOrDefault();
                BoundingBoxXYZ sb = gi?.GetSymbolGeometry()?.GetBoundingBox();
                if (sb != null && Math.Abs(gi.Transform.BasisZ.Z) > 0.999)
                {
                    Transform t = gi.Transform.Multiply(sb.Transform ?? Transform.Identity);
                    cantos = new[] { new XYZ(sb.Min.X, sb.Min.Y, sb.Min.Z), new XYZ(sb.Max.X, sb.Min.Y, sb.Min.Z), new XYZ(sb.Max.X, sb.Max.Y, sb.Min.Z), new XYZ(sb.Min.X, sb.Max.Y, sb.Min.Z) }
                        .Select(q => t.OfPoint(q)).Select(q => new XYZ(q.X, q.Y, 0)).ToArray();
                }
            }
            catch { }
            return (cantos, bb.Min.Z, bb.Max.Z);
        }

        /// <summary>One stretch of a loop / chain: its pieces (where the stone goes along it) before the corners.</summary>
        protected sealed class TrechoCalc
        {
            public XYZ A, D;
            public double L, Z;
            public LinhaRodape Linha;
            public Element Host;
            public bool Virado;                                                     // turned alone (drawn rodapé): corners left alone
            public readonly List<(double s0, double s1, bool retIni, bool retFim)> Pecas = new List<(double, double, bool, bool)>();
            public readonly List<(XYZ p, XYZ q)> Retornos = new List<(XYZ, XYZ)>();
            public bool Chega(int fim) => fim == 0 ? Pecas.Any(x => x.s0 < 1e-9) : Pecas.Any(x => x.s1 > L - 1e-9);
        }

        /// <summary>
        /// One stretch a → b (stone on the left) along a host (wall or column; any host or none with livre):
        /// curtain / tiled walls skipped; doors, windows, openings and furniture cut — the inserts of the wall
        /// behind the stretch, also when the stretch runs on a finish wall (parede cebola) and the door is in
        /// the wall behind it; the returns into the openings as deep as the middle of that wall. The pieces
        /// are not placed yet (ColocarTrechos does the corners and the short pieces). Null = no rodapé here
        /// (room separation line).
        /// </summary>
        protected TrechoCalc CalcularTrecho(CtxRodape k, ConfigRodape c, XYZ a, XYZ b, Element host, double z, bool livre, LinhaRodape lr)
        {
            bool coluna = host is FamilyInstance fic && fic.Category != null &&
                          (fic.Category.Id.Value == (long)BuiltInCategory.OST_Columns || fic.Category.Id.Value == (long)BuiltInCategory.OST_StructuralColumns);
            if (!(host is Wall) && !coluna && !livre) return null;          // room separation lines: no rodapé
            double L = a.DistanceTo(b);
            XYZ d = (b - a) / L, esq = XYZ.BasisZ.CrossProduct(d);
            var tc = new TrechoCalc { A = a, D = d, L = L, Z = z, Linha = lr, Host = host };
            lr.Bruto += L * 0.3048;
            Wall w = host as Wall;
            if (w != null && c.SemCortina && w.WallType.Kind == WallKind.Curtain) { lr.Cortina += L * 0.3048; return tc; }
            if (w != null && k.Revestidas.Length > 0 && Revestida(w, (a + b) / 2 + esq * 0.1, k.Revestidas, k.Cache)) { lr.Revestidas += L * 0.3048; return tc; }

            // the discounts along this stretch: (start, end, kind, return into the jambs)
            var cortes = new List<(double s0, double s1, int tipo, bool ret)>();
            double S(XYZ p) => (new XYZ(p.X, p.Y, 0) - a).DotProduct(d);
            double Atras(XYZ p) => (new XYZ(p.X, p.Y, 0) - a).DotProduct(esq);    // < 0 = behind the stretch (in the wall)
            // an insert belongs here when its wall is parallel and behind the stretch: its own wall, or the wall behind a finish wall
            bool DaParede(Element h, XYZ p)
            {
                if (!(h is Wall hw) || !((hw.Location as LocationCurve)?.Curve is Line hl)) return false;
                XYZ hd = new XYZ(hl.Direction.X, hl.Direction.Y, 0).Normalize();
                if (Math.Abs(hd.X * d.Y - hd.Y * d.X) > 0.02) return false;
                double t = Atras(p);
                return t <= 50 * MmFt && -t <= hw.Width + 0.5;
            }
            if (c.Portas)
                foreach (FamilyInstance dr in k.Portas)
                {
                    if (!(dr.Location is LocationPoint lp) || !DaParede(dr.Host, lp.Point)) continue;
                    double larg = TamanhoPainel(dr, out double lmm, out _) ? lmm * MmFt : 0.9 / 0.3048;
                    double s = S(lp.Point);
                    // the frame as modelled (aduela / alizar at this face): the rodapé stops against it
                    double e0 = s - larg / 2, e1 = s + larg / 2;
                    if (BordasDaEsquadria(k, dr, S, Atras, lp.Point, z, larg, out double b0, out double b1)) { e0 = b0; e1 = b1; }
                    if (e1 + k.Folga <= 0 || e0 - k.Folga >= L) continue;
                    cortes.Add((e0 - k.Folga, e1 + k.Folga, 0, false));
                }
            foreach (FamilyInstance jn in k.Janelas)
            {
                if (!(jn.Location is LocationPoint lp) || !DaParede(jn.Host, lp.Point)) continue;
                BoundingBoxXYZ bb = jn.get_BoundingBox(null);
                if (bb == null || bb.Min.Z > z + k.HRod + 10 * MmFt) continue;   // only windows that come down to the rodapé
                double larg = TamanhoPainel(jn, out double lmm, out _) ? lmm * MmFt : 0;
                if (larg <= 0) continue;
                double s = S(lp.Point);
                double e0 = s - larg / 2, e1 = s + larg / 2;
                if (BordasDaEsquadria(k, jn, S, Atras, lp.Point, z, larg, out double b0, out double b1)) { e0 = b0; e1 = b1; }
                if (e1 + k.Folga <= 0 || e0 - k.Folga >= L) continue;
                cortes.Add((e0 - k.Folga, e1 + k.Folga, 1, false));
            }
            foreach (Opening ab in k.Aberturas)
            {
                BoundingBoxXYZ bb = ab.get_BoundingBox(null);
                XYZ q0, q1;
                if (ab.IsRectBoundary && ab.BoundaryRect != null && ab.BoundaryRect.Count >= 2) { q0 = ab.BoundaryRect[0]; q1 = ab.BoundaryRect[1]; }
                else if (bb != null) { q0 = bb.Min; q1 = bb.Max; }
                else continue;
                if (!DaParede(ab.Host, (q0 + q1) / 2)) continue;
                if (Math.Min(q0.Z, q1.Z) > z + k.HRod + 10 * MmFt) continue;     // openings above the floor (windows without frame) keep the rodapé
                double s0 = Math.Min(S(q0), S(q1)), s1 = Math.Max(S(q0), S(q1));
                if (s1 <= 0 || s0 >= L) continue;
                cortes.Add((s0, s1, 2, c.Retorno));
                if (c.Retorno && ab.Host is Wall hw)
                {
                    // into each jamb, from the face to the middle of the wall the opening is in, the stone facing the opening
                    double prof = w != null && w.Id == hw.Id ? hw.Width / 2 : Math.Abs(Atras(CentroDaParede(hw)));
                    if (prof < 10 * MmFt) prof = hw.Width / 2;
                    foreach (var (sj, sinal) in new[] { (s0, 1.0), (s1, -1.0) })
                    {
                        if (sj <= 1e-6 || sj >= L - 1e-6) continue;
                        XYZ face = a + d * sj, dentro = face - esq * prof;
                        bool ida = XYZ.BasisZ.CrossProduct((dentro - face).Normalize()).DotProduct(d * sinal) > 0;
                        tc.Retornos.Add(ida ? (face, dentro) : (dentro, face));
                    }
                }
            }
            foreach (var (cantos, oz0, oz1) in k.Obstaculos)
            {
                if (oz0 > z + k.HRod + 50 * MmFt || oz1 < z + 10 * MmFt) continue;
                double t0 = cantos.Min(q => (q - a).DotProduct(esq)), t1 = cantos.Max(q => (q - a).DotProduct(esq));
                if (t1 < -30 * MmFt || t0 > 150 * MmFt) continue;
                double s0 = cantos.Min(q => (q - a).DotProduct(d)), s1 = cantos.Max(q => (q - a).DotProduct(d));
                if (s1 <= 0 || s0 >= L) continue;
                cortes.Add((s0, s1, 3, false));
            }
            // what is left, and how much each kind took; a piece beside an opening with return remembers it
            var trechos = new List<(double s0, double s1, bool ri, bool rf)> { (0, L, false, false) };
            foreach (var (c0, c1, tipo, ret) in cortes.OrderBy(x => x.tipo))
            {
                double tirado = trechos.Sum(tr => Math.Max(0, Math.Min(tr.s1, c1) - Math.Max(tr.s0, c0))) * 0.3048;
                if (tipo == 0) lr.Portas += tirado; else if (tipo == 1) lr.Janelas += tirado; else if (tipo == 2) lr.Vaos += tirado; else lr.Moveis += tirado;
                var novo = new List<(double, double, bool, bool)>();
                foreach (var tr in trechos)
                {
                    if (c1 <= tr.s0 || c0 >= tr.s1) { novo.Add(tr); continue; }
                    if (c0 > tr.s0) novo.Add((tr.s0, c0, tr.ri, ret && c0 > 1e-6 && c0 < L - 1e-6));
                    if (c1 < tr.s1) novo.Add((c1, tr.s1, ret && c1 > 1e-6 && c1 < L - 1e-6, tr.rf));
                }
                trechos = novo.Where(tr => tr.Item2 - tr.Item1 > 1e-6).Select(tr => (tr.Item1, tr.Item2, tr.Item3, tr.Item4)).ToList();
            }
            tc.Pecas.AddRange(trechos);
            return tc;
        }

        /// <summary>
        /// Where the door (or floor window) really ends along the stretch: the outer edges of its solids at this face —
        /// the alizar when the family has one on this side, else the aduela (frame) — at the rodapé height. The solids
        /// touching the face (5 mm into the wall to 3 cm in front) are used; when none does (frame set back in the
        /// wall), all the solids across the wall. False (panel width used) when the family has no solids or the
        /// result does not look like the frame (narrower than half the panel or wider than panel + 60 cm).
        /// </summary>
        private static bool BordasDaEsquadria(CtxRodape k, FamilyInstance fi, Func<XYZ, double> S, Func<XYZ, double> Atras, XYZ centro, double z,
                                              double larg, out double s0, out double s1)
        {
            s0 = s1 = 0;
            if (!k.Solidos.TryGetValue(fi.Id.Value, out List<XYZ[]> solidos))
            {
                solidos = new List<XYZ[]>();
                try
                {
                    var opt = new Options { DetailLevel = ViewDetailLevel.Fine, IncludeNonVisibleObjects = false };
                    JuntarSolidos(fi.get_Geometry(opt), solidos, 0);
                }
                catch { }
                k.Solidos[fi.Id.Value] = solidos;
            }
            if (solidos.Count == 0) return false;
            double hostW = fi.Host is Wall hw ? hw.Width : 0.3 / 0.3048;
            double fundo = Math.Min(Atras(centro), 0) - hostW / 2 - 20 * MmFt;   // the far side of the wall the door is in
            double z0 = z - 5 * MmFt, z1 = z + Math.Max(k.HRod, 50 * MmFt);
            double c = S(centro);
            double Faixa(double t0, double t1, double a, double b) => Math.Min(t1, b) - Math.Max(t0, a);
            double perto0 = double.MaxValue, perto1 = double.MinValue, todo0 = double.MaxValue, todo1 = double.MinValue;
            foreach (XYZ[] pts in solidos)
            {
                double tmin = double.MaxValue, tmax = double.MinValue, zmin = double.MaxValue, zmax = double.MinValue, smin = double.MaxValue, smax = double.MinValue;
                foreach (XYZ p in pts)
                {
                    double t = Atras(p), sp = S(p);
                    if (t < tmin) tmin = t; if (t > tmax) tmax = t;
                    if (p.Z < zmin) zmin = p.Z; if (p.Z > zmax) zmax = p.Z;
                    if (sp < smin) smin = sp; if (sp > smax) smax = sp;
                }
                if (zmax < z0 || zmin > z1) continue;                       // not at the rodapé height
                if (smax < c - larg / 2 - 0.4 / 0.3048 || smin > c + larg / 2 + 0.4 / 0.3048) continue;   // something far along the wall
                if (Faixa(tmin, tmax, fundo, 30 * MmFt) <= 0) continue;     // not across this wall
                if (smax - smin > larg + 0.6 / 0.3048) continue;            // not a frame part (a big solid of the family)
                todo0 = Math.Min(todo0, smin); todo1 = Math.Max(todo1, smax);
                if (Faixa(tmin, tmax, -5 * MmFt, 30 * MmFt) > 0) { perto0 = Math.Min(perto0, smin); perto1 = Math.Max(perto1, smax); }
            }
            if (perto1 > perto0) { s0 = perto0; s1 = perto1; }
            else if (todo1 > todo0) { s0 = todo0; s1 = todo1; }
            else return false;
            double w = s1 - s0;
            return w >= larg / 2 && w <= larg + 0.6 / 0.3048 && Math.Abs((s0 + s1) / 2 - c) < 0.4 / 0.3048;
        }

        /// <summary>The solids of a family's geometry (nested instances too), each as the points of its edges.</summary>
        private static void JuntarSolidos(GeometryElement ge, List<XYZ[]> res, int prof)
        {
            if (ge == null || prof > 6) return;
            foreach (GeometryObject go in ge)
            {
                if (go is Solid so && so.Volume > 1e-9 && so.Edges.Size > 0)
                {
                    var pts = new List<XYZ>();
                    foreach (Edge e in so.Edges) { try { pts.AddRange(e.Tessellate()); } catch { } }
                    if (pts.Count > 0) res.Add(pts.ToArray());
                }
                else if (go is GeometryInstance gi) JuntarSolidos(gi.GetInstanceGeometry(), res, prof + 1);
            }
        }

        /// <summary>Centre of a wall's location line, moved to the middle of its thickness (plan).</summary>
        private static XYZ CentroDaParede(Wall w)
        {
            Line l = (w.Location as LocationCurve)?.Curve as Line;
            if (l == null) return XYZ.Zero;
            double off = 0;
            try { off = CortesEsquadriasCommand.WallOffset(w); } catch { }
            XYZ ori = new XYZ(w.Orientation.X, w.Orientation.Y, 0);
            XYZ m = l.Evaluate(0.5, true);
            return new XYZ(m.X, m.Y, 0) + (ori.GetLength() > 1e-9 ? ori.Normalize() * off : XYZ.Zero);
        }

        /// <summary>
        /// Places the pieces of a loop (closed) or chain with the corners worked out from what really reaches them:
        /// inside corners butt (the next piece starts at the face of the previous one — no double stone), outside
        /// corners covered once (the incoming piece grows to the outer corner), nothing grows into a neighbour
        /// without rodapé (curtain or tiled wall, separation line); beside an opening with return the piece covers
        /// the jamb corner; pieces shorter than the minimum dropped — except where they continue smoothly into
        /// the next stretch (a curved wall in short chords). Turns under 20° are left as they are. The corner is where
        /// the lines of the two stretches meet, so faces that miss by a few mm still close; micro stretches at a corner
        /// (up to 3, shorter than the minimum: a step, gap or chamfer where two faces do not quite meet) are bridged.
        /// </summary>
        protected void ColocarTrechos(List<TrechoCalc> laco, bool fechado, ConfigRodape c, CtxRodape k, Func<XYZ, XYZ, FamilyInstance> Colocar, ElementId nivel,
                                      List<(XYZ a, XYZ b)> geo = null, Func<XYZ, bool> vizinho = null)
        {
            int n = laco.Count;
            var ajIni = new double[n]; var ajFim = new double[n];          // + trims, − grows
            var lisoIni = new bool[n]; var lisoFim = new bool[n];
            var absorvido = new bool[n];                                    // micro stretch bridged by its neighbours
            // a stretch this short at a corner is where two faces do not quite meet (a step, gap or chamfer of a few mm)
            double microLim = Math.Max(k.Minimo, Math.Max(2 * k.E, 20 * MmFt));
            bool Valido(int q) => laco[q] != null && !laco[q].Virado;
            bool EhMicro(int q) => Valido(q) && laco[q].L < microLim;
            int Proximo(int q) => q + 1 < n ? q + 1 : fechado && n > 1 ? 0 : -1;
            // the corner between i and j (the micro stretches between them bridged): both run to where their lines
            // meet, then inside corners butt and outside corners are covered once; false = not a corner to bridge
            bool FecharCanto(int i, int j, List<int> pulados)
            {
                TrechoCalc ti = laco[i], tj = laco[j];
                double sen = ti.D.X * tj.D.Y - ti.D.Y * tj.D.X;
                double phi = Math.Atan2(sen, ti.D.DotProduct(tj.D));
                if (Math.Abs(phi) < 20 * Math.PI / 180)
                {
                    if (pulados.Count > 0) return false;
                    if (ti.Chega(1) && tj.Chega(0)) lisoFim[i] = lisoIni[j] = true;
                    return true;
                }
                if (!ti.Chega(1) || !tj.Chega(0)) return pulados.Count == 0;
                XYZ w = tj.A - ti.A;
                double ext = (w.X * tj.D.Y - w.Y * tj.D.X) / sen - ti.L;     // + = the meeting point is past the end of i
                double u = (w.X * ti.D.Y - w.Y * ti.D.X) / sen;               // − = it is before the start of j
                double tol = pulados.Sum(q => laco[q].L) + 3 * k.E + 20 * MmFt;
                if (Math.Abs(ext) > tol || Math.Abs(u) > tol)
                {
                    if (pulados.Count > 0) return false;
                    ext = 0; u = 0;
                }
                foreach (int q in pulados) absorvido[q] = true;
                ajFim[i] = -ext; ajIni[j] = u;
                if (c.Cantos)
                {
                    if (phi < 0) ajFim[i] -= Math.Min(3 * k.E, k.E * Math.Tan(-phi / 2));   // outside corner: the incoming piece covers it
                    else ajIni[j] += Math.Min(3 * k.E, k.E / Math.Sin(phi));              // inside corner: the next one butts on it
                }
                return true;
            }
            // a closed loop is walked from a stretch that is not micro: a micro stretch is reached from its neighbour first
            int ini = 0;
            if (fechado) for (int q = 0; q < n; q++) if (Valido(q) && !EhMicro(q)) { ini = q; break; }
            for (int m = 0; m < n; m++)
            {
                int i = (ini + m) % n;
                if (absorvido[i] || !Valido(i)) continue;
                int j = Proximo(i);
                if (j < 0 || j == i) continue;
                if (!EhMicro(i))
                {
                    var pulados = new List<int>();
                    int q = j;
                    while (q >= 0 && q != i && EhMicro(q) && pulados.Count < 3) { pulados.Add(q); q = Proximo(q); }
                    if (pulados.Count > 0 && q >= 0 && q != i && Valido(q) && !EhMicro(q) && FecharCanto(i, q, pulados)) continue;
                }
                if (Valido(j) && !absorvido[j]) FecharCanto(i, j, new List<int>());
            }
            // outside corner of a wall split by a room separation line that continues this face (CIRC. | SALA): the
            // room on this side of the line covers the corner square, when the room on the other side gets or has rodapé
            if (c.Cantos && vizinho != null && geo != null && geo.Count == n)
            {
                bool Separacao(int q) => laco[q] == null && geo[q].a.DistanceTo(geo[q].b) > 1e-6;
                bool Segue(int q, XYZ d)
                {
                    XYZ ds = (geo[q].b - geo[q].a).Normalize();
                    return Math.Abs(ds.X * d.Y - ds.Y * d.X) < Math.Sin(20 * Math.PI / 180) && ds.DotProduct(d) > 0;
                }
                for (int i = 0; i < n; i++)
                {
                    if (!Valido(i) || absorvido[i]) continue;
                    TrechoCalc t = laco[i];
                    XYZ esq = XYZ.BasisZ.CrossProduct(t.D), fim = t.A + t.D * t.L;
                    int pv = i > 0 ? i - 1 : fechado && n > 1 ? n - 1 : -1, nx = Proximo(i);
                    if (pv >= 0 && pv != i && Separacao(pv) && Segue(pv, t.D) && t.Chega(0) && ajIni[i] == 0 &&
                        vizinho(t.A - t.D * (k.E / 2) - esq * (k.E / 2)))
                        ajIni[i] = -k.E;
                    if (nx >= 0 && nx != i && Separacao(nx) && Segue(nx, t.D) && t.Chega(1) && ajFim[i] == 0 &&
                        vizinho(fim + t.D * (k.E / 2) - esq * (k.E / 2)))
                        ajFim[i] = -k.E;
                }
            }
            for (int i = 0; i < n; i++)
            {
                TrechoCalc t = laco[i];
                if (t == null) continue;
                LinhaRodape lr = t.Linha;
                if (absorvido[i]) { lr.Curtos += t.Pecas.Sum(x => x.s1 - x.s0) * 0.3048; continue; }
                foreach (var (s0, s1, ri, rf) in t.Pecas)
                {
                    double i0 = s0, i1 = s1;
                    if (s0 < 1e-9) i0 += ajIni[i]; else if (ri) i0 -= k.E;      // beside an opening with return: covers the jamb corner
                    if (s1 > t.L - 1e-9) i1 -= ajFim[i]; else if (rf) i1 += k.E;
                    bool continua = (s0 < 1e-9 && lisoIni[i]) || (s1 > t.L - 1e-9 && lisoFim[i]);
                    if ((i1 - i0 < k.Minimo && !continua) || i1 - i0 < 1 * MmFt) { lr.Curtos += Math.Max(0, s1 - s0) * 0.3048; continue; }
                    XYZ p0 = t.A + t.D * i0, p1 = t.A + t.D * i1;
                    FamilyInstance fi = Colocar?.Invoke(p0, p1);
                    if (Colocar != null && fi == null) continue;          // no Colocar = preview: only the metres
                    lr.Liquido += (i1 - i0) * 0.3048;
                    if (fi != null && c.Prender && t.Host is Wall hw) k.Prender.Add((fi.Id, hw.Id, p0, p1, t.Z, nivel));
                }
                foreach (var (p, q) in t.Retornos)
                    if (Colocar == null || Colocar(p, q) != null) lr.Retornos += p.DistanceTo(q) * 0.3048;
            }
        }

        // ------------------------------------------------------------------ locked to the wall

        /// <summary>Warnings deleted; an error rolls the transaction back (no Revit dialog) and is remembered.</summary>
        private sealed class AvisosOuDesfaz : IFailuresPreprocessor
        {
            public bool Desfez;
            public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
            {
                bool erro = false;
                foreach (FailureMessageAccessor f in a.GetFailureMessages())
                {
                    if (f.GetSeverity() == FailureSeverity.Warning) a.DeleteWarning(f);
                    else erro = true;
                }
                if (erro) { Desfez = true; return FailureProcessingResult.ProceedWithRollBack; }
                return FailureProcessingResult.Continue;
            }
        }

        /// <summary>
        /// Locks every rodapé piece placed along a wall to that wall's face (a locked alignment, like Alinhar +
        /// cadeado): moving the wall moves the rodapé. Its own transactions, after the pieces exist: in batches;
        /// a batch Revit refuses is done piece by piece, so only the refused pieces stay free (counted).
        /// </summary>
        protected void PrenderRodapes(CtxRodape k, ResultadoRodape res)
        {
            if (k.Prender.Count == 0) return;
            var itens = k.Prender.ToList();
            k.Prender.Clear();
            var vistas = new Dictionary<ElementId, List<View>>();
            void Lote(List<(ElementId peca, ElementId parede, XYZ p0, XYZ p1, double z, ElementId nivel)> l, bool sozinho)
            {
                int ok = 0, falhou = 0;
                var guarda = new AvisosOuDesfaz();
                bool feito = false;
                try
                {
                    using (var t = new Transaction(Doc, "JA Famílias – prender rodapés"))
                    {
                        t.Start();
                        var o = t.GetFailureHandlingOptions(); o.SetFailuresPreprocessor(guarda); o.SetClearAfterRollback(true); t.SetFailureHandlingOptions(o);
                        foreach (var x in l) { if (PrenderPeca(k, x.peca, x.parede, x.p0, x.p1, x.z, x.nivel, vistas)) ok++; else falhou++; }
                        feito = t.Commit() == TransactionStatus.Committed && !guarda.Desfez;
                    }
                }
                catch { feito = false; }
                if (feito) { res.Presos += ok; res.NaoPresos += falhou; return; }
                if (sozinho) { res.NaoPresos += l.Count; return; }
                foreach (var x in l) Lote(new List<(ElementId, ElementId, XYZ, XYZ, double, ElementId)> { x }, true);
            }
            for (int i = 0; i < itens.Count; i += 100) Lote(itens.Skip(i).Take(100).ToList(), false);
        }

        /// <summary>One piece: the wall face it lies on (normal into the room, containing the piece) aligned with the
        /// back of the piece — its Center (Front/Back) plane, else its back face — in a plan of the level.</summary>
        private bool PrenderPeca(CtxRodape k, ElementId idPeca, ElementId idParede, XYZ p0, XYZ p1, double z, ElementId nivel, Dictionary<ElementId, List<View>> cache)
        {
            if (!(Doc.GetElement(idPeca) is FamilyInstance fi) || !(Doc.GetElement(idParede) is Wall w)) return false;
            XYZ a = new XYZ(p0.X, p0.Y, 0), b = new XYZ(p1.X, p1.Y, 0);
            if (a.DistanceTo(b) < 1e-6) return false;
            XYZ d = (b - a).Normalize(), esq = XYZ.BasisZ.CrossProduct(d);
            XYZ ponto = new XYZ((a.X + b.X) / 2, (a.Y + b.Y) / 2, z + Math.Max(10 * MmFt, k.HRod / 2));
            Reference rw = FaceLateral(w, esq, ponto);
            if (rw == null) return false;
            List<Reference> fundo = FundoDaPeca(fi, -esq, a);
            if (fundo.Count == 0) return false;
            foreach (View v in PlantasDoNivel(nivel, cache))
                foreach (Reference rp in fundo)
                {
                    try { Doc.Create.NewAlignment(v, rw, rp); return true; }
                    catch { }
                }
            return false;
        }

        /// <summary>The side face of the wall with this normal (plan) that contains the point (else one in that plane).</summary>
        private static Reference FaceLateral(Wall w, XYZ normal, XYZ ponto)
        {
            Reference noPlano = null;
            foreach (ShellLayerType lado in new[] { ShellLayerType.Exterior, ShellLayerType.Interior })
            {
                IList<Reference> refs;
                try { refs = HostObjectUtils.GetSideFaces(w, lado); } catch { continue; }
                foreach (Reference r in refs)
                {
                    PlanarFace pf = null;
                    try { pf = w.GetGeometryObjectFromReference(r) as PlanarFace; } catch { }
                    if (pf == null) continue;
                    XYZ n = new XYZ(pf.FaceNormal.X, pf.FaceNormal.Y, 0);
                    if (n.GetLength() < 1e-9 || n.Normalize().DotProduct(normal) < 0.999) continue;
                    if (Math.Abs((pf.Origin - ponto).DotProduct(normal)) > 2 * MmFt) continue;   // another plane (the other side of a thick join…)
                    try { IntersectionResult ir = pf.Project(ponto); if (ir != null && ir.Distance < 2 * MmFt) return r; } catch { }
                    if (noPlano == null) noPlano = r;
                }
            }
            return noPlano;
        }

        /// <summary>References at the back of a line-based piece (the plane through its line): the family's
        /// Center (Front/Back) plane, then its faces with that normal on that plane (symbol, then instance geometry).</summary>
        private static List<Reference> FundoDaPeca(FamilyInstance fi, XYZ normal, XYZ naLinha)
        {
            var l = new List<Reference>();
            try { l.AddRange(fi.GetReferences(FamilyInstanceReferenceType.CenterFrontBack)); } catch { }
            bool NoFundo(XYZ n, XYZ o) =>
                new XYZ(n.X, n.Y, 0).GetLength() > 1e-9 && new XYZ(n.X, n.Y, 0).Normalize().DotProduct(normal) > 0.999 &&
                Math.Abs((new XYZ(o.X, o.Y, 0) - naLinha).DotProduct(normal)) < 1 * MmFt;
            try
            {
                GeometryElement geo = fi.get_Geometry(new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine });
                if (geo != null)
                {
                    var deInstancia = new List<Reference>();
                    foreach (GeometryObject go in geo)
                    {
                        if (go is GeometryInstance gi)
                        {
                            Transform tr = gi.Transform;
                            foreach (GeometryObject so in gi.GetSymbolGeometry())
                                if (so is Solid s)
                                    foreach (Face f in s.Faces)
                                        if (f is PlanarFace pf && pf.Reference != null && NoFundo(tr.OfVector(pf.FaceNormal), tr.OfPoint(pf.Origin))) l.Add(pf.Reference);
                            foreach (GeometryObject io in gi.GetInstanceGeometry())
                                if (io is Solid s2)
                                    foreach (Face f in s2.Faces)
                                        if (f is PlanarFace pf && pf.Reference != null && NoFundo(pf.FaceNormal, pf.Origin)) deInstancia.Add(pf.Reference);
                        }
                        else if (go is Solid s3)
                            foreach (Face f in s3.Faces)
                                if (f is PlanarFace pf && pf.Reference != null && NoFundo(pf.FaceNormal, pf.Origin)) l.Add(pf.Reference);
                    }
                    l.AddRange(deInstancia);
                }
            }
            catch { }
            return l;
        }

        /// <summary>Plans of the level (the active one first, floor plans before others).</summary>
        private List<View> PlantasDoNivel(ElementId nivel, Dictionary<ElementId, List<View>> cache)
        {
            if (cache.TryGetValue(nivel, out List<View> l)) return l;
            l = new List<View>();
            if (Doc.ActiveView is ViewPlan ap && !ap.IsTemplate && ap.GenLevel?.Id == nivel) l.Add(ap);
            foreach (ViewPlan vp in new FilteredElementCollector(Doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                         .Where(x => !x.IsTemplate && x.GenLevel?.Id == nivel).OrderBy(x => x.ViewType == ViewType.FloorPlan ? 0 : 1))
            {
                if (l.Count >= 4) break;
                if (!l.Contains(vp)) l.Add(vp);
            }
            cache[nivel] = l;
            return l;
        }

        /// <summary>Is the wall finished with tiles on the side of point p (its type name or the layer on that side)?</summary>
        private bool Revestida(Wall w, XYZ p, string[] palavras, Dictionary<long, bool> cache)
        {
            bool exterior = false;
            try
            {
                LocationCurve lc = w.Location as LocationCurve;
                XYZ q = lc.Curve.Project(new XYZ(p.X, p.Y, lc.Curve.GetEndPoint(0).Z))?.XYZPoint ?? lc.Curve.GetEndPoint(0);
                exterior = (new XYZ(p.X - q.X, p.Y - q.Y, 0)).DotProduct(new XYZ(w.Orientation.X, w.Orientation.Y, 0)) > 0;
            }
            catch { }
            long chave = w.Id.Value * 2 + (exterior ? 1 : 0);
            if (cache.TryGetValue(chave, out bool v)) return v;
            bool Tem(string s) { string x = (s ?? "").ToLowerInvariant(); return palavras.Any(k => x.Contains(k)); }
            bool res = Tem(w.WallType?.Name);
            if (!res)
            {
                try
                {
                    CompoundStructure cs = w.WallType?.GetCompoundStructure();
                    var camadas = cs?.GetLayers();
                    if (camadas != null && camadas.Count > 0)
                    {
                        CompoundStructureLayer lado = exterior ? camadas[0] : camadas[camadas.Count - 1];
                        res = Tem((Doc.GetElement(lado.MaterialId) as Material)?.Name);
                    }
                }
                catch { }
            }
            cache[chave] = res;
            return res;
        }
    }

    /// <summary>
    /// Rodapé: along the walls of the selected rooms (drawn stretches: Rodapé no Local) with the discounts of doors (+ gap), windows down to the floor, wall openings
    /// (optionally turning into the jambs), curtain walls, tiled walls and furniture; outside corners
    /// closed; per room: walls, each discount, net, purchase with waste and pieces; Excel if asked.
    /// One line-based stone strip per stretch (JA_Comprimento, Lista para a Marmoraria); the settings stay
    /// on each stretch (Atualizar Revestimentos).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class RodapeCommand : EsquadriaCommandBase
    {
        public override string Title => "Rodapé";

        protected override Result Run()
        {
            List<Room> rooms = AmbientesSelecionados("Rodapé: selecione os ambientes e clique em Concluir");
            if (rooms.Count == 0) return Result.Cancelled;

            var dlg = new OptionsDialog(Title, "Rodape");
            dlg.Section("Peça");
            var fAlt = dlg.Number("altura", "Altura do rodapé (mm)", 70, 20, 400);
            var fEsp = dlg.Number("espessura", "Espessura (mm)", 15, 5, 40);
            var fMat = dlg.Text("material", "Material", "Granito cinza andorinha", "Pedra, porcelanato recortado, MDF, poliestireno… (vai para a lista e o orçamento).");
            CamposRodape fd = DescontosRodape(dlg);
            dlg.Section("Compra");
            var fPerda = dlg.Number("perda", "Perda (%)", 10, 0, 50);
            var fBarra = dlg.Number("barra", "Comprimento da peça (mm; 0 = por metro)", 0, 0, 6000, "Ex.: 1200 ou 2400 para rodapé em peças; a quantidade é arredondada para cima.");
            var fExcel = dlg.Check("excel", "Salvar a planilha dos descontos (Excel)", false);
            dlg.Note("Só nas paredes e pilares: linhas de separação de ambiente ficam sem rodapé. Para desenhar o rodapé onde quiser, use Rodapé no Local.");
            if (!dlg.Show(Owner)) return Result.Cancelled;

            var c = new ConfigRodape
            {
                Altura = fAlt.Value, Espessura = fEsp.Value, Material = fMat.Value, Perda = fPerda.Value, Barra = fBarra.Value
            };
            fd.Aplicar(c);
            if (fd.Previa.Value && !ConfirmarRodape(new List<(ConfigRodape, List<Room>)> { (c, rooms) })) return Result.Cancelled;
            var avisos = new List<string>();
            ToolProgress.Setup("Colocando o rodapé", null);
            ResultadoRodape r0 = AplicarRodapes(rooms, c, fd.Refazer.Value, avisos);
            MostrarRodape(new List<(ConfigRodape, ResultadoRodape)> { (c, r0) }, fExcel.Value, avisos);
            return Result.Succeeded;
        }
    }

    /// <summary>
    /// Escada Revestida: stone tread (piso) and riser (espelho) on every tread of the selected Revit
    /// stairs (straight runs), from the stair geometry; one JA Degrau type per tread size.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class EscadaRevestidaCommand : EsquadriaCommandBase
    {
        public override string Title => "Escada Revestida";

        private sealed class FiltroEscada : ISelectionFilter
        {
            public bool AllowElement(Element e) => e is Stairs;
            public bool AllowReference(Reference r, XYZ p) => false;
        }

        private sealed class Piso { public XYZ Frente, Dir; public double Largura, Prof, Topo, Espelho; public Level Nivel; }

        protected override Result Run()
        {
            var escadas = UiDoc.Selection.GetElementIds().Select(id => Doc.GetElement(id)).OfType<Stairs>().ToList();
            if (escadas.Count == 0)
            {
                try { escadas = UiDoc.Selection.PickObjects(ObjectType.Element, new FiltroEscada(), "Escada revestida: selecione as escadas e clique em Concluir").Select(r => Doc.GetElement(r)).OfType<Stairs>().ToList(); }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
            }
            if (escadas.Count == 0) return Result.Cancelled;

            var dlg = new OptionsDialog(Title, "EscadaRevestida");
            var fEsp = dlg.Number("espessura", "Espessura do piso (mm)", 30, 10, 60);
            var fEspelho = dlg.Check("espelho", "Espelhos (pedra na face de cada degrau)", true);
            var fEspE = dlg.Number("espEspelho", "Espessura do espelho (mm)", 20, 5, 40);
            var fBocel = dlg.Number("bocel", "Bocel: quanto o piso avança além do espelho (mm)", 20, 0, 60);
            fEspE.EnabledWhen(fEspelho); fBocel.EnabledWhen(fEspelho);
            var fMat = dlg.Text("material", "Material", "Granito cinza andorinha");
            var fPatamar = dlg.Check("patamares", "Patamares também (piso de pedra no contorno retangular de cada um)", true,
                "Patamar que não é retangular fica de fora (aviso): faça-o com Bancada no Local › Soleira / peça de piso.");
            dlg.Note("Os degraus ficam no topo do piso da escada do Revit (a escada é tratada como acabada). Lances curvos ficam de fora. A fórmula de Blondel é conferida em cada escada.");
            if (!dlg.Show(Owner)) return Result.Cancelled;
            double e = fEsp.Value;
            string material = string.IsNullOrWhiteSpace(fMat.Value) ? "Granito cinza" : fMat.Value.Trim();

            var pisos = new List<Piso>();
            var patamaresPisos = new List<Piso>();
            var avisos = new List<string>();
            var blondel = new List<string>();
            int patamares = 0;
            var opt = new Options { DetailLevel = ViewDetailLevel.Fine, ComputeReferences = false };
            foreach (Stairs st in escadas)
            {
                double riser = st.ActualRiserHeight;
                Level nivel = Doc.GetElement(st.get_Parameter(BuiltInParameter.STAIRS_BASE_LEVEL_PARAM)?.AsElementId() ?? ElementId.InvalidElementId) as Level;
                if (nivel == null) { avisos.Add($"Escada {st.Id.Value}: sem nível base."); continue; }
                patamares += st.GetStairsLandings().Count;
                // Blondel: 2 risers + 1 tread between 63 and 65 cm; NBR 9050 (accessible): riser 16–18 cm, tread 28–32 cm
                {
                    double h = st.ActualRiserHeight / MmFt, p = st.ActualTreadDepth / MmFt, b = 2 * h + p;
                    var falhas = new List<string>();
                    if (b < 630 || b > 650) falhas.Add($"2h + p = {b:0} mm (Blondel: 630 a 650)");
                    if (h > 180) falhas.Add($"espelho {h:0} mm > 180");
                    if (p < 280) falhas.Add($"piso {p:0} mm < 280");
                    blondel.Add($"Escada {st.Id.Value}: espelho {h:0} mm, piso {p:0} mm, 2h + p = {b:0} mm" + (falhas.Count > 0 ? " ⚠ " + string.Join("; ", falhas) : " ✔"));
                }
                // landings: their top face as a rectangle in the walking direction of the first run
                if (fPatamar.Value)
                {
                    XYZ d0 = null;
                    foreach (ElementId rid0 in st.GetStairsRuns())
                    {
                        try { if (Doc.GetElement(rid0) is StairsRun r0 && r0.GetStairsPath()?.FirstOrDefault() is Line l0) { d0 = new XYZ(l0.Direction.X, l0.Direction.Y, 0).Normalize(); break; } } catch { }
                    }
                    foreach (ElementId lid in st.GetStairsLandings())
                    {
                        if (!(Doc.GetElement(lid) is StairsLanding land) || d0 == null) continue;
                        PlanarFace topo = Faces(land.get_Geometry(opt)).Where(f => f.FaceNormal.Z > 0.99).OrderByDescending(f => f.Area).FirstOrDefault();
                        if (topo == null) continue;
                        var pts = topo.GetEdgesAsCurveLoops().SelectMany(l => l).SelectMany(cv => new[] { cv.GetEndPoint(0), cv.GetEndPoint(1) }).ToList();
                        // the side directions of the landing: the run direction or across it, whichever its edges follow
                        XYZ d = d0, perp = XYZ.BasisZ.CrossProduct(d);
                        double s0 = pts.Min(q => q.DotProduct(d)), s1 = pts.Max(q => q.DotProduct(d)), t0 = pts.Min(q => q.DotProduct(perp)), t1 = pts.Max(q => q.DotProduct(perp));
                        if (topo.Area < (s1 - s0) * (t1 - t0) * 0.97) { avisos.Add($"Patamar {lid.Value}: não é retangular, ficou de fora."); continue; }
                        double tm = (t0 + t1) / 2, zt = pts.Max(q => q.Z);
                        XYZ frente = d * s0 + perp * tm;
                        patamaresPisos.Add(new Piso { Frente = new XYZ(frente.X, frente.Y, zt), Dir = d, Largura = t1 - t0, Prof = s1 - s0, Topo = zt, Espelho = 0, Nivel = nivel });
                    }
                }
                foreach (ElementId rid in st.GetStairsRuns())
                {
                    if (!(Doc.GetElement(rid) is StairsRun run)) continue;
                    Curve c0 = null;
                    try { c0 = run.GetStairsPath()?.FirstOrDefault(); } catch { }
                    if (!(c0 is Line pl)) { avisos.Add($"Escada {st.Id.Value}: lance curvo ou em leque fica de fora."); continue; }
                    XYZ d = new XYZ(pl.Direction.X, pl.Direction.Y, 0).Normalize(), perp = XYZ.BasisZ.CrossProduct(d);
                    double larguraLance = run.ActualRunWidth, profPiso = st.ActualTreadDepth;
                    var vistos = new HashSet<string>();
                    foreach (PlanarFace f in Faces(run.get_Geometry(opt)))
                    {
                        if (f.FaceNormal.Z < 0.99) continue;
                        var pts = f.GetEdgesAsCurveLoops().SelectMany(l => l).SelectMany(cv => new[] { cv.GetEndPoint(0), cv.GetEndPoint(1) }).ToList();
                        if (pts.Count < 3) continue;
                        double s0 = pts.Min(p => p.DotProduct(d)), s1 = pts.Max(p => p.DotProduct(d));
                        double t0 = pts.Min(p => p.DotProduct(perp)), t1 = pts.Max(p => p.DotProduct(perp));
                        double prof = s1 - s0, larg = t1 - t0;
                        if (larg < larguraLance * 0.8 || prof > profPiso * 1.6 || prof < 50 * MmFt) continue;   // stringer tops, the run body...
                        double topo = pts.Max(p => p.Z);
                        string chave = Math.Round(topo / MmFt) + "|" + Math.Round(s0 / MmFt);
                        if (!vistos.Add(chave)) continue;
                        double tm = (t0 + t1) / 2;
                        XYZ frente = d * s0 + perp * tm;
                        pisos.Add(new Piso { Frente = new XYZ(frente.X, frente.Y, topo), Dir = d, Largura = larg, Prof = prof, Topo = topo, Espelho = riser, Nivel = nivel });
                    }
                    if (riser <= 0) avisos.Add($"Escada {st.Id.Value}: altura do espelho não encontrada.");
                }
            }
            if (pisos.Count == 0 && patamaresPisos.Count == 0)
            {
                Show("Nenhum degrau reconhecido nas escadas selecionadas." + (avisos.Count > 0 ? "\n\n• " + string.Join("\n• ", avisos.Distinct()) : ""), ResultKind.Warning);
                return Result.Cancelled;
            }

            // one JA Degrau type per size (width × depth × riser)
            var usados = ProjectPecas.UsedCodes(Doc, TipoPeca.Pedra);
            var tipos = new Dictionary<string, FamilySymbol>();
            int n = 0;
            foreach (var g in pisos.GroupBy(p => Math.Round(p.Largura / MmFt / 5) * 5 + "|" + Math.Round(p.Prof / MmFt / 5) * 5 + "|" + Math.Round(p.Espelho / MmFt)))
            {
                Piso a = g.First();
                double riserMm = a.Espelho / MmFt;
                var cfg = new PedraConfig
                {
                    Uso = UsoPedra.Degrau, ComprimentoMm = Math.Round(a.Largura / MmFt), LarguraMm = Math.Round(a.Prof / MmFt), EspessuraMm = e,
                    AlturaEspelhoMm = fEspelho.Value ? Math.Max(0, Math.Round(riserMm - e)) : 0, EspessuraEspelhoMm = fEspE.Value, ProjecaoMm = fBocel.Value,
                    Material = material, Cor = CorDaPedra(material), Codigo = ProjectPecas.NextCode(usados, "DG")
                };
                usados.Add(cfg.Codigo);
                cfg.Normalized();
                try
                {
                    GenerationResult gen = PecaGenerator.Generate(UiApp.Application, Doc, cfg, Settings.Current, cfg.NomeFamilia(), null);
                    avisos.AddRange(gen.Warnings);
                    if (gen.Symbol != null) tipos[g.Key] = gen.Symbol;
                }
                catch (Exception ex) { avisos.Add(cfg.Codigo + ": " + ex.Message); }
            }
            // landings: one JA Patamar type per size
            var tiposPatamar = new Dictionary<string, FamilySymbol>();
            foreach (var g in patamaresPisos.GroupBy(p => Math.Round(p.Largura / MmFt / 5) * 5 + "|" + Math.Round(p.Prof / MmFt / 5) * 5))
            {
                Piso a = g.First();
                var cfg = new PedraConfig
                {
                    Uso = UsoPedra.Patamar, ComprimentoMm = Math.Round(a.Largura / MmFt), LarguraMm = Math.Round(a.Prof / MmFt), EspessuraMm = e,
                    Material = material, Cor = CorDaPedra(material), Codigo = ProjectPecas.NextCode(usados, "PT")
                };
                usados.Add(cfg.Codigo);
                cfg.Normalized();
                try
                {
                    GenerationResult gen = PecaGenerator.Generate(UiApp.Application, Doc, cfg, Settings.Current, cfg.NomeFamilia(), null);
                    avisos.AddRange(gen.Warnings);
                    if (gen.Symbol != null) tiposPatamar[g.Key] = gen.Symbol;
                }
                catch (Exception ex) { avisos.Add(cfg.Codigo + ": " + ex.Message); }
            }
            int nPat = 0;
            using (var t = new Transaction(Doc, "JA Famílias – " + Title))
            {
                t.Start();
                var o = t.GetFailureHandlingOptions(); o.SetFailuresPreprocessor(new WarningSwallower()); t.SetFailureHandlingOptions(o);
                foreach (var g in patamaresPisos.GroupBy(p => Math.Round(p.Largura / MmFt / 5) * 5 + "|" + Math.Round(p.Prof / MmFt / 5) * 5))
                {
                    if (!tiposPatamar.TryGetValue(g.Key, out FamilySymbol s)) continue;
                    foreach (Piso p in g)
                    {
                        try { ColocarNoPonto(s, p.Frente, p.Dir.CrossProduct(XYZ.BasisZ), p.Nivel); nPat++; }
                        catch (Exception ex) { avisos.Add(ex.Message); }
                    }
                }
                foreach (var g in pisos.GroupBy(p => Math.Round(p.Largura / MmFt / 5) * 5 + "|" + Math.Round(p.Prof / MmFt / 5) * 5 + "|" + Math.Round(p.Espelho / MmFt)))
                {
                    if (!tipos.TryGetValue(g.Key, out FamilySymbol s)) continue;
                    foreach (Piso p in g)
                    {
                        // family +Y (back of the tread) along the walking direction: X = d × Z
                        try { ColocarNoPonto(s, p.Frente, p.Dir.CrossProduct(XYZ.BasisZ), p.Nivel); n++; }
                        catch (Exception ex) { avisos.Add(ex.Message); }
                    }
                }
                t.Commit();
            }
            Show($"{n} degrau(s) revestido(s) em {escadas.Count} escada(s), {tipos.Count} tipo(s) de degrau ({string.Join(", ", tipos.Keys.Select(k => k.Split('|')[0] + " × " + k.Split('|')[1]))} mm)." +
                 (nPat > 0 ? $"\n{nPat} patamar(es) revestido(s)." : patamares > 0 && !fPatamar.Value ? $"\n{patamares} patamar(es) ficaram de fora." : "") +
                 "\n\nConferência:\n" + string.Join("\n", blondel) +
                 "\nPiso e espelho entram na Lista para a Marmoraria e no plano de corte." +
                 (avisos.Count > 0 ? "\n\nAvisos:\n• " + string.Join("\n• ", avisos.Distinct().Take(15)) : ""), avisos.Count > 0 ? ResultKind.Warning : ResultKind.Success);
            return Result.Succeeded;
        }

        private static IEnumerable<PlanarFace> Faces(GeometryElement ge)
        {
            if (ge == null) yield break;
            foreach (GeometryObject go in ge)
            {
                if (go is Solid so && so.Volume > 0) { foreach (Face f in so.Faces) if (f is PlanarFace pf) yield return pf; }
                else if (go is GeometryInstance gi) foreach (PlanarFace pf in Faces(gi.GetInstanceGeometry())) yield return pf;
            }
        }
    }
}
