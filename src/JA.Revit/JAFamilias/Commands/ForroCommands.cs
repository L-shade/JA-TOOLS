using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
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
    /// <summary>Settings of a forro, kept on each piece (Atualizar Revestimentos redoes it with them).</summary>
    [DataContract]
    public sealed class ConfigForro
    {
        [DataMember(Order = 1)] public string Tipo { get; set; }
        [DataMember(Order = 2)] public double Altura { get; set; }           // mm, from the room's floor
        [DataMember(Order = 3)] public double Tabica { get; set; }             // shadow gap at the walls, mm
        [DataMember(Order = 4)] public int Sanca { get; set; }                       // 0 none, 1 fechada, 2 aberta
        [DataMember(Order = 5)] public double LarguraSanca { get; set; }
        [DataMember(Order = 6)] public double Rebaixo { get; set; }
        [DataMember(Order = 7)] public double Abertura { get; set; }          // light slot of the sanca aberta
        [DataMember(Order = 8)] public double EspessuraEspelho { get; set; }
        [DataMember(Order = 9)] public bool Acabamento { get; set; }
        [DataMember(Order = 10)] public string Material { get; set; }

        public string Descricao() => Material + (Sanca == 1 ? " com sanca fechada" : Sanca == 2 ? " com sanca aberta" : "") + (Tabica > 0 ? ", tabica" : "");

        public ConfigForro() { Padroes(); }

        /// <summary>DataContract does not run the constructor: the defaults for fields missing in older JSON.</summary>
        [System.Runtime.Serialization.OnDeserializing] private void AoLer(System.Runtime.Serialization.StreamingContext c) => Padroes();

        private void Padroes()
        {
            Tipo = "";
            Altura = 2600;
            Tabica = 10;
            LarguraSanca = 400;
            Rebaixo = 250;
            Abertura = 120;
            EspessuraEspelho = 12.5;
            Acabamento = true;
            Material = "Forro de gesso";
        }
    }

    public abstract partial class EsquadriaCommandBase
    {
        protected sealed class ResultadoForro
        {
            public int Ambientes, Substituidos;
            public double AreaM2, TabicaM, SancaM, EspelhoM2;
            public readonly List<string> PorAmbiente = new List<string>();
        }

        /// <summary>Elements of the categories that carry JA data of this kind.</summary>
        protected List<(Element E, DadosJa D)> ComDadosJa(string tipo, params BuiltInCategory[] cats)
        {
            var res = new List<(Element, DadosJa)>();
            var col = new FilteredElementCollector(Doc).WhereElementIsNotElementType();
            IEnumerable<Element> els = cats.Length > 0 ? col.WherePasses(new ElementMulticategoryFilter(cats.ToList())) : col;
            foreach (Element e in els)
            {
                DadosJa d = JaDados.Ler(e);
                if (d != null && d.Tipo == tipo) res.Add((e, d));
            }
            return res;
        }

        /// <summary>The room's boundary (finish faces) as closed loops; the largest first.</summary>
        protected static List<CurveLoop> LacosDoAmbiente(Room r)
        {
            var res = new List<CurveLoop>();
            var op = new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish };
            foreach (IList<BoundarySegment> laco in r.GetBoundarySegments(op) ?? new List<IList<BoundarySegment>>())
            {
                var curvas = laco.Select(s => s.GetCurve()).Where(c => c != null).ToList();
                if (curvas.Count < 2) continue;
                CurveLoop cl = null;
                try { cl = CurveLoop.Create(curvas); } catch { }
                if (cl == null || cl.IsOpen())
                {
                    // small gaps: straight lines through the start points
                    var pts = new List<XYZ>();
                    foreach (Curve c in curvas)
                    {
                        XYZ p = c.GetEndPoint(0);
                        if (pts.Count == 0 || pts[pts.Count - 1].DistanceTo(p) > 0.005) pts.Add(p);
                    }
                    if (pts.Count >= 3 && pts[0].DistanceTo(pts[pts.Count - 1]) < 0.005) pts.RemoveAt(pts.Count - 1);
                    if (pts.Count < 3) continue;
                    try
                    {
                        var ls = new List<Curve>();
                        for (int i = 0; i < pts.Count; i++) ls.Add(Line.CreateBound(pts[i], pts[(i + 1) % pts.Count]));
                        cl = CurveLoop.Create(ls);
                    }
                    catch { continue; }
                }
                res.Add(cl);
            }
            return res.OrderByDescending(x => AreaDoLaco(x)).ToList();
        }

        /// <summary>Plan area of a loop (ft²), by its tessellation.</summary>
        protected static double AreaDoLaco(CurveLoop cl)
        {
            var pts = new List<XYZ>();
            foreach (Curve c in cl)
            {
                IList<XYZ> t = c is Line ? new List<XYZ> { c.GetEndPoint(0), c.GetEndPoint(1) } : c.Tessellate();
                for (int i = 0; i + 1 < t.Count; i++) pts.Add(t[i]);
            }
            double a = 0;
            for (int i = 0; i < pts.Count; i++) { XYZ p = pts[i], q = pts[(i + 1) % pts.Count]; a += p.X * q.Y - q.X * p.Y; }
            return Math.Abs(a) / 2;
        }

        protected static double Perimetro(CurveLoop cl) => cl.Sum(c => c.Length);

        /// <summary>The loop moved inward (dentro) or outward by d (ft); null when it does not fit.</summary>
        protected static CurveLoop Deslocar(CurveLoop cl, double d, bool dentro)
        {
            if (d < 1e-6) return cl;
            double a0 = AreaDoLaco(cl);
            CurveLoop melhor = null;
            foreach (double s in new[] { d, -d })
            {
                try
                {
                    CurveLoop o = CurveLoop.CreateViaOffset(cl, s, XYZ.BasisZ);
                    if (o == null || o.IsOpen()) continue;
                    double a = AreaDoLaco(o);
                    if (dentro ? a < a0 - 1e-6 && a > 1e-4 : a > a0 + 1e-6) { melhor = o; break; }
                }
                catch { }
            }
            return melhor;
        }

        private static CurveLoop NaCota(CurveLoop cl, double z)
        {
            double z0 = cl.First().GetEndPoint(0).Z;
            return CurveLoop.CreateViaTransform(cl, Transform.CreateTranslation(new XYZ(0, 0, z - z0)));
        }

        /// <summary>A ceiling (first loop outside, the rest holes) at h (ft) above the level. Inside a transaction.</summary>
        protected Ceiling NovoForro(List<CurveLoop> lacos, ElementId tipo, ElementId nivel, double h)
        {
            Ceiling c;
            try { c = Ceiling.Create(Doc, lacos, tipo, nivel); }
            catch (Exception) when (lacos.Count > 1) { c = Ceiling.Create(Doc, new List<CurveLoop> { lacos[0] }, tipo, nivel); }   // a column hole that crosses the edge
            c.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM)?.Set(h);
            return c;
        }

        /// <summary>A vertical ring (the espelho of a sanca) between two loops, from z (absolute, ft) up by altura.</summary>
        protected DirectShape Anel(CurveLoop fora, CurveLoop dentro, double z, double altura, string nome)
        {
            CurveLoop a = NaCota(fora, z), b = NaCota(dentro, z);
            if (!a.IsCounterclockwise(XYZ.BasisZ)) a.Flip();
            if (b.IsCounterclockwise(XYZ.BasisZ)) b.Flip();
            Solid s = GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { a, b }, XYZ.BasisZ, altura);
            var cat = new ElementId(BuiltInCategory.OST_Ceilings);
            if (!DirectShape.IsValidCategoryId(cat, Doc)) cat = new ElementId(BuiltInCategory.OST_GenericModel);
            DirectShape ds = DirectShape.CreateElement(Doc, cat);
            ds.ApplicationId = "JA Famílias";
            ds.ApplicationDataId = "forro";
            ds.SetShape(new GeometryObject[] { s });
            try { ds.Name = nome; } catch { }
            return ds;
        }

        protected List<CeilingType> TiposDeForro() =>
            new FilteredElementCollector(Doc).OfClass(typeof(CeilingType)).Cast<CeilingType>().OrderBy(t => t.Name).ToList();

        /// <summary>
        /// Forro in each room: the ceiling at the height asked, set back from the walls by the tabica,
        /// with or without a sanca (lowered band + espelho). The room's old JA forro is replaced.
        /// Writes the room's ceiling finish. Its own transaction.
        /// </summary>
        protected ResultadoForro AplicarForros(List<Room> rooms, ConfigForro c, List<string> avisos)
        {
            var res = new ResultadoForro();
            var tipos = TiposDeForro();
            CeilingType tipo = tipos.FirstOrDefault(t => t.Name == c.Tipo)
                ?? tipos.FirstOrDefault(t => t.Name.IndexOf("gesso", StringComparison.OrdinalIgnoreCase) >= 0) ?? tipos.FirstOrDefault();
            if (tipo == null) { avisos.Add("O projeto não tem nenhum tipo de forro (Ceiling): carregue ou crie um."); return res; }
            if (c.Tipo != tipo.Name && !string.IsNullOrEmpty(c.Tipo)) AddUm(avisos, "O tipo de forro “" + c.Tipo + "” não existe mais; usei “" + tipo.Name + "”.");
            var antigos = ComDadosJa("forro", BuiltInCategory.OST_Ceilings, BuiltInCategory.OST_GenericModel);
            string json = Json.Serialize(c);
            double tab = c.Tabica * MmFt, larg = c.LarguraSanca * MmFt, reb = c.Rebaixo * MmFt, esp = Math.Max(1, c.EspessuraEspelho) * MmFt;

            using (var t = new Transaction(Doc, "JA Famílias – forro"))
            {
                t.Start();
                var o = t.GetFailureHandlingOptions(); o.SetFailuresPreprocessor(new WarningSwallower()); t.SetFailureHandlingOptions(o);
                foreach (Room r in rooms)
                {
                    string nome = (r.Number + " " + r.Name).Trim();
                    // one room = one sub-transaction: if it fails, the room keeps its old forro (no half-made pieces)
                    var st = new SubTransaction(Doc);
                    st.Start();
                    try
                    {
                        var velhos = antigos.Where(x => x.D.Ambiente == r.Id.Value).Select(x => x.E.Id).ToList();
                        if (velhos.Count > 0) { Doc.Delete(velhos); res.Substituidos++; }
                        Level nivel = Doc.GetElement(r.LevelId) as Level;
                        var lacos = LacosDoAmbiente(r);
                        if (nivel == null || lacos.Count == 0) { st.RollBack(); avisos.Add(nome + ": sem contorno fechado."); continue; }
                        double h = r.BaseOffset + c.Altura * MmFt;
                        double topo = r.UnboundedHeight;
                        if (topo > 0 && c.Altura * MmFt > topo + 1e-6)
                            AddUm(avisos, nome + ": o forro (" + c.Altura.ToString("0") + " mm) passa do limite superior do ambiente; o ambiente não vai enxergar o forro no cálculo de volume.");
                        CurveLoop ext = Deslocar(lacos[0], tab, true) ?? lacos[0];
                        var furos = lacos.Skip(1).Select(l => Deslocar(l, tab, false) ?? l).ToList();
                        var feitos = new List<(Element E, double Area, double Metros, string Desc)>();
                        int sanca = c.Sanca;
                        CurveLoop borda = null, dentro = null;
                        if (sanca > 0)
                        {
                            borda = Deslocar(lacos[0], larg, true);
                            dentro = borda != null ? Deslocar(lacos[0], larg + esp, true) : null;
                            if (borda == null || dentro == null) { avisos.Add(nome + ": a sanca de " + c.LarguraSanca.ToString("0") + " mm não cabe; forro liso."); sanca = 0; }
                        }
                        if (sanca == 0)
                        {
                            var l = new List<CurveLoop> { ext }; l.AddRange(furos);
                            feitos.Add((NovoForro(l, tipo.Id, nivel.Id, h), (AreaDoLaco(ext) - furos.Sum(x => AreaDoLaco(x))) * 0.09290304, Perimetro(ext) * 0.3048, "forro"));
                        }
                        else
                        {
                            // the lowered band along the walls
                            var faixa = NovoForro(new List<CurveLoop> { ext, borda }, tipo.Id, nivel.Id, h - reb);
                            feitos.Add((faixa, (AreaDoLaco(ext) - AreaDoLaco(borda)) * 0.09290304, Perimetro(ext) * 0.3048, "faixa da sanca"));
                            // the main ceiling: inside the espelho (fechada) or up to the walls, over the band (aberta)
                            var l = new List<CurveLoop> { sanca == 1 ? borda : ext }; l.AddRange(furos);
                            var central = NovoForro(l, tipo.Id, nivel.Id, h);
                            feitos.Add((central, (AreaDoLaco(l[0]) - furos.Sum(x => AreaDoLaco(x))) * 0.09290304, sanca == 2 ? Perimetro(ext) * 0.3048 : 0, "forro central"));
                            double alt = sanca == 1 ? reb : reb - c.Abertura * MmFt;
                            if (alt > 0.01)
                            {
                                var esp1 = Anel(borda, dentro, nivel.ProjectElevation + h - reb, alt, "JA Espelho da sanca " + nome);
                                double m = Perimetro(borda) * 0.3048;
                                feitos.Add((esp1, m * alt * 0.3048, m, "espelho da sanca"));
                                res.SancaM += m; res.EspelhoM2 += m * alt * 0.3048;
                            }
                            else AddUm(avisos, nome + ": a abertura da sanca é maior que o rebaixo; ficou sem espelho.");
                        }
                        foreach (var f in feitos)
                        {
                            JaDados.Gravar(f.E, new DadosJa { Tipo = "forro", Ambiente = r.Id.Value, Config = json, AreaM2 = f.Area, Metros = f.Metros, Descricao = f.Desc });
                            if (f.Desc != "espelho da sanca") res.AreaM2 += f.Area;
                        }
                        double tabM = tab > 0 ? Perimetro(ext) * 0.3048 * (sanca == 2 ? 2 : 1) : 0;
                        res.TabicaM += tabM;
                        if (c.Acabamento) r.get_Parameter(BuiltInParameter.ROOM_FINISH_CEILING)?.Set(c.Descricao());
                        res.Ambientes++;
                        var br = PecaUtil.Pt;
                        res.PorAmbiente.Add(nome + ": " + feitos.Where(f => f.Desc != "espelho da sanca").Sum(f => f.Area).ToString("0.00", br) + " m² de forro" +
                            (sanca > 0 ? " (sanca " + (sanca == 1 ? "fechada" : "aberta") + " " + c.LarguraSanca.ToString("0") + " mm, rebaixo " + c.Rebaixo.ToString("0") + " mm)" : "") +
                            (tabM > 0 ? " · tabica " + tabM.ToString("0.0", br) + " m" : ""));
                        st.Commit();
                    }
                    catch (Exception ex)
                    {
                        if (st.HasStarted()) st.RollBack();
                        avisos.Add(nome + ": " + ex.Message + " (o forro anterior ficou como estava).");
                    }
                }
                t.Commit();
            }
            return res;
        }
    }

    /// <summary>
    /// Forro de Gesso / Sanca: the ceiling of the selected rooms at the height asked, with tabica at
    /// the walls and, if asked, a sanca fechada or aberta (lowered band, espelho and the light slot).
    /// Running again on a room replaces its forro; Atualizar Revestimentos redoes it after the walls change.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class ForroCommand : EsquadriaCommandBase
    {
        public override string Title => "Forro e Sanca";

        protected override Result Run()
        {
            var tipos = TiposDeForro();
            if (tipos.Count == 0) { Show("O projeto não tem nenhum tipo de forro (Ceiling): carregue ou crie um tipo de forro de gesso.", ResultKind.Warning); return Result.Cancelled; }
            var rooms = AmbientesSelecionados("Forro: selecione os ambientes e clique em Concluir");
            if (rooms.Count == 0) return Result.Cancelled;

            var nomes = tipos.Select(t => t.Name).ToList();
            int def = Math.Max(0, nomes.FindIndex(n => n.IndexOf("gesso", StringComparison.OrdinalIgnoreCase) >= 0));
            var dlg = new OptionsDialog(Title, "Forro", "Criar forro");
            dlg.Section("Forro");
            var fTipo = dlg.Choice("tipo", "Tipo de forro", nomes, def);
            var fAlt = dlg.Number("altura", "Altura do forro, do piso (mm)", 2600, 1800, 8000);
            var fTab = dlg.Number("tabica", "Tabica / negativo junto às paredes (mm, 0 = sem)", 10, 0, 50);
            var fMat = dlg.Text("material", "Descrição (acabamento do teto)", "Forro de gesso");
            dlg.Section("Sanca");
            var fSanca = dlg.Choice("sanca", "Sanca", new[] { "Sem sanca", "Sanca fechada (faixa rebaixada junto às paredes)", "Sanca aberta (luz indireta para o forro)" }, 0);
            var fLarg = dlg.Number("largura", "Largura da sanca (mm)", 400, 100, 2000);
            var fReb = dlg.Number("rebaixo", "Rebaixo da sanca (mm)", 250, 50, 1000, "Distância entre o forro central e a faixa rebaixada.");
            var fAb = dlg.Number("abertura", "Abertura para a luz (mm)", 120, 40, 500, "Vão entre o espelho e o forro central, por onde a luz rebate.");
            var fEsp = dlg.Number("espelho", "Espessura do espelho (mm)", 12.5, 5, 60);
            foreach (var f in new OptionsDialog.Field[] { fLarg, fReb, fEsp }) f.EnabledWhen(fSanca, 1, 2);
            fAb.EnabledWhen(fSanca, 2);
            var fAcab = dlg.Check("acabamento", "Escrever no acabamento do teto do ambiente", true);
            int comForro = ComDadosJa("forro", BuiltInCategory.OST_Ceilings, BuiltInCategory.OST_GenericModel).Select(x => x.D.Ambiente).Distinct().Count(a => rooms.Any(r => r.Id.Value == a));
            if (comForro > 0) dlg.Note(comForro + " ambiente(s) já têm forro do JA Famílias: o antigo é substituído.");
            if (!dlg.Show(Owner)) return Result.Cancelled;

            var c = new ConfigForro
            {
                Tipo = fTipo.Value, Altura = fAlt.Value, Tabica = fTab.Value, Material = string.IsNullOrWhiteSpace(fMat.Value) ? "Forro de gesso" : fMat.Value.Trim(),
                Sanca = fSanca.Index, LarguraSanca = fLarg.Value, Rebaixo = fReb.Value, Abertura = fAb.Value, EspessuraEspelho = fEsp.Value, Acabamento = fAcab.Value
            };
            var avisos = new List<string>();
            ResultadoForro res = AplicarForros(rooms, c, avisos);
            var br = PecaUtil.Pt;
            Show($"Forro em {res.Ambientes} ambiente(s)" + (res.Substituidos > 0 ? $" ({res.Substituidos} substituído(s))" : "") + $": {res.AreaM2.ToString("0.00", br)} m²" +
                 (res.TabicaM > 0 ? $", tabica {res.TabicaM.ToString("0.0", br)} m" : "") +
                 (res.SancaM > 0 ? $", sanca {res.SancaM.ToString("0.0", br)} m (espelho {res.EspelhoM2.ToString("0.00", br)} m²)" : "") + ".\n\n• " + string.Join("\n• ", res.PorAmbiente) +
                 "\n\nRode de novo no ambiente para mudar; Atualizar Revestimentos refaz depois que as paredes mudarem. A Lista de Compras soma o gesso, a tabica e a sanca." +
                 (avisos.Count > 0 ? "\n\nAvisos:\n• " + string.Join("\n• ", avisos) : ""), avisos.Count > 0 ? ResultKind.Warning : ResultKind.Success);
            return Result.Succeeded;
        }
    }
}
