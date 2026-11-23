using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using JAEsquadrias.Common;
using JAEsquadrias.Core;
using JAEsquadrias.Engine;
using JAEsquadrias.Infrastructure;

namespace JAEsquadrias.Commands
{
    /// <summary>Settings of one paginação, kept on its group (to redo it with the same choices).</summary>
    [DataContract]
    public sealed class ConfigPaginacao
    {
        [DataMember(Order = 1)] public double X { get; set; }
        [DataMember(Order = 2)] public double Y { get; set; }
        [DataMember(Order = 3)] public double Junta { get; set; }
        [DataMember(Order = 4)] public int Inicio { get; set; }
        [DataMember(Order = 5)] public int Direcao { get; set; }
        [DataMember(Order = 6)] public double Perda { get; set; }
        [DataMember(Order = 7)] public double Caixa { get; set; }
        [DataMember(Order = 8)] public bool Texto { get; set; }
        [DataMember(Order = 9)] public bool Ralos { get; set; }
        [DataMember(Order = 10)] public double OrigemX { get; set; }        // clicked start (view frame, mm)
        [DataMember(Order = 11)] public double OrigemY { get; set; }
        [DataMember(Order = 12)] public string Material { get; set; }
        /// <summary>Tile thickness (mm), for the grout; grout density (kg/dm³); adhesive mortar (kg/m²).</summary>
        [DataMember(Order = 13)] public double EspessuraPeca { get; set; }
        [DataMember(Order = 14)] public double DensidadeRejunte { get; set; }
        [DataMember(Order = 15)] public double Argamassa { get; set; }

        /// <summary>Grout per m² (kg): (C + L) × E × J × D / (C × L), lengths in mm.</summary>
        public double RejunteKgM2 => X > 0 && Y > 0 ? (X + Y) * EspessuraPeca * Junta * DensidadeRejunte / (X * Y) : 0;

        public ConfigPaginacao() { Padroes(); }

        /// <summary>DataContract does not run the constructor: the defaults for fields missing in older JSON.</summary>
        [System.Runtime.Serialization.OnDeserializing] private void AoLer(System.Runtime.Serialization.StreamingContext c) => Padroes();

        private void Padroes()
        {
            X = 600;
            Y = 600;
            Junta = 2;
            Perda = 10;
            Texto = true;
            Ralos = true;
            Material = "";
            EspessuraPeca = 9;
            DensidadeRejunte = 1.58;
            Argamassa = 5;
        }
    }

    public abstract partial class EsquadriaCommandBase
    {
        protected sealed class RegiaoPag
        {
            public string Nome;
            public List<List<P2>> Lacos = new List<List<P2>>();     // view frame, mm
            public double AnguloParede;                              // longest stretch (plans)
            public P2 Centro;
            public long Ambiente;
            public string Face;
            public int Ralos;
            public List<ElementId> Antigos = new List<ElementId>();  // groups replaced by this one
        }

        protected sealed class PaginacaoExistente { public Group G; public DadosJa D; }

        protected List<PaginacaoExistente> PaginacoesDaVista(View v) =>
            new FilteredElementCollector(Doc, v.Id).OfClass(typeof(Group)).Cast<Group>()
                .Select(g => new PaginacaoExistente { G = g, D = JaDados.Ler(g) }).Where(x => x.D != null && x.D.Tipo == "paginacao").ToList();

        protected static P2 ParaVista(View v, XYZ p) { XYZ q = p - v.Origin; return new P2(q.DotProduct(v.RightDirection) / MmFt, q.DotProduct(v.UpDirection) / MmFt); }
        protected static XYZ DaVista(View v, P2 p) => v.Origin + v.RightDirection * (p.X * MmFt) + v.UpDirection * (p.Y * MmFt);

        protected RegiaoPag RegiaoDeAmbiente(View v, Room r, List<PaginacaoExistente> existentes)
        {
            var reg = new RegiaoPag { Nome = (r.Number + " " + r.Name).Trim(), Ambiente = r.Id.Value };
            double maior = 0;
            foreach (var laco in Contornos(r))
            {
                reg.Lacos.Add(laco.Select(q => ParaVista(v, q.a)).ToList());
                foreach (var s in laco)
                {
                    P2 a = ParaVista(v, s.a), b = ParaVista(v, s.b);
                    double l = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                    if (l > maior) { maior = l; reg.AnguloParede = Math.Atan2(b.Y - a.Y, b.X - a.X); }
                }
            }
            if (r.Location is LocationPoint lp) reg.Centro = ParaVista(v, lp.Point);
            reg.Antigos.AddRange(existentes.Where(x => x.D.Ambiente == r.Id.Value).Select(x => x.G.Id));
            return reg.Lacos.Count > 0 ? reg : null;
        }

        protected RegiaoPag RegiaoDeFace(View v, Reference fr, List<PaginacaoExistente> existentes)
        {
            Element el = Doc.GetElement(fr);
            if (!(el?.GetGeometryObjectFromReference(fr) is PlanarFace pf) || Math.Abs(pf.FaceNormal.DotProduct(v.ViewDirection)) < 0.99) return null;
            string rep = null;
            try { rep = fr.ConvertToStableRepresentation(Doc); } catch { }
            var reg = new RegiaoPag { Nome = (el.Category?.Name ?? "Face") + " " + el.Id.Value, Face = rep };
            foreach (CurveLoop cl in pf.GetEdgesAsCurveLoops())
            {
                var l = new List<P2>();
                foreach (Curve c in cl)
                {
                    IList<XYZ> t = c is Line ? new List<XYZ> { c.GetEndPoint(0), c.GetEndPoint(1) } : c.Tessellate();
                    for (int i = 0; i + 1 < t.Count; i++) l.Add(ParaVista(v, t[i]));
                }
                if (l.Count >= 3) reg.Lacos.Add(l);
            }
            if (rep != null) reg.Antigos.AddRange(existentes.Where(x => x.D.Face == rep).Select(x => x.G.Id));
            return reg.Lacos.Count > 0 ? reg : null;
        }

        /// <summary>The region a stored paginação came from (null when its room / face is gone).</summary>
        protected RegiaoPag RegiaoGuardada(View v, PaginacaoExistente x, List<PaginacaoExistente> existentes)
        {
            RegiaoPag reg = null;
            if (x.D.Ambiente != 0 && Doc.GetElement(new ElementId(x.D.Ambiente)) is Room r && r.Area > 0) reg = RegiaoDeAmbiente(v, r, existentes);
            else if (!string.IsNullOrEmpty(x.D.Face))
            {
                try { reg = RegiaoDeFace(v, Reference.ParseFromStableRepresentation(Doc, x.D.Face), existentes); } catch { }
            }
            if (reg != null && !reg.Antigos.Contains(x.G.Id)) reg.Antigos.Add(x.G.Id);
            return reg;
        }

        /// <summary>JA drains on the floor: their outline becomes a hole of the room's region.</summary>
        protected void DescontarRalos(View v, List<RegiaoPag> regioes)
        {
            var ralos = new FilteredElementCollector(Doc).OfCategory(BuiltInCategory.OST_PipeFitting).WhereElementIsNotElementType().OfType<FamilyInstance>()
                .Select(fi => new { Fi = fi, C = ProjectPecas.ConfigOf(fi, out _) as RaloConfig }).Where(x => x.C != null && x.C.Formato != TipoRalo.Hemisferico).ToList();
            foreach (RegiaoPag reg in regioes.Where(r => r.Ambiente != 0))
            {
                if (!(Doc.GetElement(new ElementId(reg.Ambiente)) is Room room)) continue;
                foreach (var x in ralos)
                {
                    Transform T = x.Fi.GetTransform();
                    var contorno = x.C.ContornoEmPlanta();
                    if (contorno.Count < 3) continue;
                    var mundo = contorno.Select(q => T.OfPoint(new XYZ(q.X * MmFt, q.Y * MmFt, 0))).ToList();
                    XYZ meio = new XYZ(mundo.Average(q => q.X), mundo.Average(q => q.Y), (room.Level?.ProjectElevation ?? 0) + room.BaseOffset + 1);
                    bool dentro = room.IsPointInRoom(meio) || mundo.Any(q => room.IsPointInRoom(new XYZ(q.X, q.Y, meio.Z)));
                    if (!dentro) continue;
                    reg.Lacos.Add(mundo.Select(q => ParaVista(v, q)).ToList());
                    reg.Ralos++;
                }
            }
        }

        /// <summary>
        /// Draws the paginação of the regions in the view (its own transaction): the old groups go,
        /// each region becomes one detail group "JA Paginação …" with the settings and the numbers.
        /// Returns one summary line per region.
        /// </summary>
        protected List<string> DesenharPaginacao(View v, List<RegiaoPag> regioes, ConfigPaginacao cfg, List<string> avisos, out int totalLinhas, out int trocadas)
        {
            bool planta = v is ViewPlan;
            var resumo = new List<string>();
            totalLinhas = 0; trocadas = 0;
            P2? clicado = cfg.Inicio == 2 ? new P2(cfg.OrigemX, cfg.OrigemY) : (P2?)null;
            using (var t = new Transaction(Doc, "JA Famílias – paginação"))
            {
                t.Start();
                var op = t.GetFailureHandlingOptions(); op.SetFailuresPreprocessor(new WarningSwallower()); t.SetFailureHandlingOptions(op);
                GraphicsStyle estilo = EstiloPaginacao();
                ElementId texto = Doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
                foreach (RegiaoPag reg in regioes)
                {
                    var pg = new Paginacao { PecaX = cfg.X, PecaY = cfg.Y, Junta = cfg.Junta };
                    pg.Angulo = !planta ? 0 : cfg.Direcao == 0 ? reg.AnguloParede : cfg.Direcao == 2 ? Math.PI / 4 : 0;
                    double c = Math.Cos(-pg.Angulo), s = Math.Sin(-pg.Angulo);
                    var gir = reg.Lacos.SelectMany(l => l).Select(p => new P2(p.X * c - p.Y * s, p.X * s + p.Y * c)).ToList();
                    double minX = gir.Min(p => p.X), maxX = gir.Max(p => p.X), minY = gir.Min(p => p.Y), maxY = gir.Max(p => p.Y);
                    double mx = pg.PecaX + pg.Junta, my = pg.PecaY + pg.Junta;
                    P2 o0;
                    if (clicado.HasValue) o0 = new P2(clicado.Value.X * c - clicado.Value.Y * s, clicado.Value.X * s + clicado.Value.Y * c);
                    else if (cfg.Inicio == 1) o0 = new P2(minX, minY);
                    else
                    {
                        double ox = (minX + maxX) / 2 - pg.PecaX / 2 - Math.Ceiling(((maxX - minX) / 2 - pg.PecaX / 2) / mx) * mx;
                        double oy = planta ? (minY + maxY) / 2 - pg.PecaY / 2 - Math.Ceiling(((maxY - minY) / 2 - pg.PecaY / 2) / my) * my : minY;
                        o0 = new P2(ox, oy);
                    }
                    double cb = Math.Cos(pg.Angulo), sb = Math.Sin(pg.Angulo);
                    pg.Origem = new P2(o0.X * cb - o0.Y * sb, o0.X * sb + o0.Y * cb);
                    try { pg.Calcular(reg.Lacos); }
                    catch (Exception ex) { avisos.Add(reg.Nome + ": " + ex.Message + " (a paginação anterior ficou como estava)."); continue; }
                    // only now that the new one is computed: the old one goes
                    foreach (ElementId gid in reg.Antigos.Distinct().Where(id => Doc.GetElement(id) != null))
                    {
                        try
                        {
                            ElementId tipo = (Doc.GetElement(gid) as Group)?.GroupType?.Id;
                            Doc.Delete(gid);
                            if (tipo != null && Doc.GetElement(tipo) is GroupType gt && gt.Groups.Size == 0) Doc.Delete(tipo);
                            trocadas++;
                        }
                        catch { }
                    }

                    var ids = new List<ElementId>();
                    foreach (P2[] ln in pg.Linhas)
                    {
                        try
                        {
                            DetailCurve dc = Doc.Create.NewDetailCurve(v, Line.CreateBound(DaVista(v, ln[0]), DaVista(v, ln[1])));
                            if (estilo != null) dc.LineStyle = estilo;
                            ids.Add(dc.Id);
                            totalLinhas++;
                        }
                        catch { }
                    }
                    int pecas = pg.Inteiras + pg.Recortes;
                    double aPeca = pg.PecaX * pg.PecaY / 1e6;
                    double compra = Math.Max(pg.AreaM2 * (1 + cfg.Perda / 100), pecas * aPeca);
                    double caixas = cfg.Caixa > 0 ? Math.Ceiling(compra / cfg.Caixa - 1e-9) : 0;
                    double rejunte = cfg.RejunteKgM2 * pg.AreaM2 * (1 + cfg.Perda / 100), argamassa = cfg.Argamassa * pg.AreaM2 * 1.05;
                    var br = PecaUtil.Pt;
                    resumo.Add($"{reg.Nome}: {pg.AreaM2.ToString("0.00", br)} m² · {pg.Inteiras} inteiras + {pg.Recortes} recortes = {pecas} peças {PecaUtil.Mm(pg.PecaX)}×{PecaUtil.Mm(pg.PecaY)}" +
                               $" · comprar {compra.ToString("0.00", br)} m² (perda {cfg.Perda:0}%)" + (caixas > 0 ? $" = {caixas} caixa(s)" : "") +
                               $" · rejunte {rejunte.ToString("0.0", br)} kg · argamassa {argamassa.ToString("0", br)} kg ({Math.Ceiling(argamassa / 20)} saco(s) de 20 kg)" +
                               (reg.Ralos > 0 ? $" · {reg.Ralos} ralo(s) descontado(s)" : ""));
                    if (cfg.Texto && texto != ElementId.InvalidElementId)
                    {
                        try
                        {
                            P2 pt = planta && (reg.Centro.X != 0 || reg.Centro.Y != 0) ? reg.Centro : new P2(reg.Lacos[0].Average(p => p.X), reg.Lacos[0].Max(p => p.Y) + 150);
                            var tn = TextNote.Create(Doc, v.Id, DaVista(v, pt), $"{PecaUtil.Mm(pg.PecaX)}×{PecaUtil.Mm(pg.PecaY)} junta {pg.Junta:0.#} · {pg.Inteiras} int. + {pg.Recortes} rec. · {compra.ToString("0.00", br)} m²", texto);
                            ids.Add(tn.Id);
                        }
                        catch { }
                    }
                    if (ids.Count > 0)
                    {
                        try
                        {
                            Group g = Doc.Create.NewGroup(ids);
                            string nome = "JA Paginação " + reg.Nome, n0 = nome;
                            for (int k = 2; k < 200; k++)
                            {
                                try { g.GroupType.Name = nome; break; } catch { nome = n0 + " (" + k + ")"; }
                            }
                            JaDados.Gravar(g, new DadosJa
                            {
                                Tipo = "paginacao", Ambiente = reg.Ambiente, Vista = v.Id.Value, Face = reg.Face, Config = Json.Serialize(cfg),
                                AreaM2 = pg.AreaM2, CompraM2 = compra, Peca = PecaUtil.Mm(pg.PecaX) + "×" + PecaUtil.Mm(pg.PecaY), Inteiras = pg.Inteiras, Recortes = pg.Recortes,
                                RejunteKg = rejunte, ArgamassaKg = argamassa, Caixas = caixas
                            });
                        }
                        catch (Exception ex) { avisos.Add(reg.Nome + ": grupo não criado (" + ex.Message + "); as linhas ficaram soltas."); }
                    }
                }
                t.Commit();
            }
            return resumo;
        }

        /// <summary>The "JA Paginação" line style (thin, grey), created once. Inside a transaction.</summary>
        protected GraphicsStyle EstiloPaginacao()
        {
            try
            {
                Category linhas = Doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
                Category sub = linhas.SubCategories.Cast<Category>().FirstOrDefault(c => c.Name == "JA Paginação");
                if (sub == null)
                {
                    sub = Doc.Settings.Categories.NewSubcategory(linhas, "JA Paginação");
                    sub.LineColor = new Color(128, 128, 128);
                    sub.SetLineWeight(1, GraphicsStyleType.Projection);
                }
                return sub.GetGraphicsStyle(GraphicsStyleType.Projection);
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// Paginação de Piso e Parede: in a plan, the floor of the selected rooms (less the JA drains);
    /// in an elevation or section, the clicked wall face (with its openings). The joints are detail
    /// lines (line style "JA Paginação") gathered in one detail group per room / face, which keeps
    /// the settings: select the group and run again to redo it (or change it); running again on a
    /// room replaces its paginação. Counts whole and cut pieces, area with waste, boxes, grout and mortar.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class PaginacaoCommand : EsquadriaCommandBase
    {
        public override string Title => "Paginação de Piso e Parede";

        protected override Result Run()
        {
            View v = Doc.ActiveView;
            bool planta = v is ViewPlan;
            if (!planta && !(v is ViewSection))
            {
                Show("Abra uma planta (piso dos ambientes) ou uma elevação / corte (face de parede).", ResultKind.Info);
                return Result.Cancelled;
            }
            var existentes = PaginacoesDaVista(v);
            var sel = new HashSet<ElementId>(UiDoc.Selection.GetElementIds());
            var refazer = existentes.Where(x => sel.Contains(x.G.Id)).ToList();
            ConfigPaginacao cfg0 = null;
            if (refazer.Count > 0) { try { cfg0 = Json.Deserialize<ConfigPaginacao>(refazer[0].D.Config); } catch { } }

            var regioes = new List<RegiaoPag>();
            if (refazer.Count > 0)
            {
                foreach (var x in refazer) { RegiaoPag reg = RegiaoGuardada(v, x, existentes); if (reg != null) regioes.Add(reg); }
                if (regioes.Count == 0) { Show("O ambiente ou a face dessa paginação não existe mais: apague o grupo e pagine de novo.", ResultKind.Warning); return Result.Cancelled; }
            }
            else if (planta)
            {
                foreach (Room r in AmbientesSelecionados("Paginação: selecione os ambientes e clique em Concluir"))
                {
                    RegiaoPag reg = RegiaoDeAmbiente(v, r, existentes);
                    if (reg != null) regioes.Add(reg);
                }
            }
            else
            {
                try
                {
                    RegiaoPag reg = RegiaoDeFace(v, UiDoc.Selection.PickObject(ObjectType.Face, "Paginação: clique na face da parede (de frente para a vista)"), existentes);
                    if (reg == null) { Show("Clique numa face plana de frente para a vista.", ResultKind.Warning); return Result.Cancelled; }
                    regioes.Add(reg);
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
            }
            if (regioes.Count == 0) return Result.Cancelled;

            var dlg = new OptionsDialog(Title, planta ? "PaginacaoPiso" : "PaginacaoParede", refazer.Count > 0 ? "Refazer" : "Paginar");
            dlg.Section("Peça");
            var fMat = dlg.Text("material", "Revestimento (para o orçamento)", planta ? "Porcelanato" : "Revestimento cerâmico");
            var fX = dlg.Number("x", planta ? "Largura da peça (mm)" : "Largura da peça, na horizontal (mm)", planta ? 600 : 300, 20, 3000);
            var fY = dlg.Number("y", planta ? "Comprimento da peça (mm)" : "Altura da peça (mm)", 600, 20, 3000);
            var fJ = dlg.Number("junta", "Junta (mm)", 2, 0, 30);
            var fE = dlg.Number("espessura", "Espessura da peça (mm)", 9, 3, 40, "Para o consumo de rejunte.");
            dlg.Section("Partida");
            var fIni = dlg.Choice("inicio", "Começa", planta
                ? new[] { "Centralizada no ambiente (recortes iguais nas bordas)", "Do canto inferior esquerdo", "De um ponto que eu clicar" }
                : new[] { "Da base, centralizada na largura", "Do canto inferior esquerdo", "De um ponto que eu clicar" }, 0);
            OptionsDialog.ChoiceField fAng = null;
            if (planta) fAng = dlg.Choice("angulo", "Direção", new[] { "Alinhada com a parede mais longa", "Eixos da vista", "Diagonal (45°)" }, 0);
            OptionsDialog.BoolField fRalos = null;
            if (planta) fRalos = dlg.Check("ralos", "Descontar os ralos do JA Famílias (também os lineares no croqui)", true, "A área do ralo fica sem peças e sem juntas.");
            dlg.Section("Compra");
            var fPerda = dlg.Number("perda", "Perda (%)", 10, 0, 50, "Quebras e recortes: 10% em peças retas, 15% em diagonal ou formatos grandes.");
            var fCaixa = dlg.Number("caixa", "m² por caixa (0 = não calcular)", 0, 0, 20, "Está na embalagem do porcelanato (ex.: 1,44 m² com 4 peças 60 × 60).");
            var fArg = dlg.Number("argamassa", "Argamassa colante (kg/m²)", 5, 1, 20, "Cerca de 5 kg/m² com desempenadeira 8 mm; dupla colagem em peças grandes (60 × 60 ou mais): 8 a 10 kg/m².");
            var fTexto = dlg.Check("texto", "Escrever o resumo na vista", true);
            if (cfg0 != null)
            {
                fMat.Value = cfg0.Material ?? ""; fX.Value = cfg0.X; fY.Value = cfg0.Y; fJ.Value = cfg0.Junta; fE.Value = cfg0.EspessuraPeca;
                fIni.Index = Math.Max(0, Math.Min(2, cfg0.Inicio)); if (fAng != null) fAng.Index = Math.Max(0, Math.Min(2, cfg0.Direcao));
                if (fRalos != null) fRalos.Value = cfg0.Ralos;
                fPerda.Value = cfg0.Perda; fCaixa.Value = cfg0.Caixa; fArg.Value = cfg0.Argamassa; fTexto.Value = cfg0.Texto;
                dlg.Note("Refazer: as configurações vieram da paginação selecionada; a antiga é substituída.");
            }
            else if (regioes.Any(r => r.Antigos.Count > 0)) dlg.Note("Os ambientes que já têm paginação nesta vista vão ter a antiga substituída.");
            if (!dlg.Show(Owner)) return Result.Cancelled;

            P2? clicado = null;
            if (fIni.Index == 2)
            {
                if (cfg0 != null && cfg0.Inicio == 2) clicado = new P2(cfg0.OrigemX, cfg0.OrigemY);
                else
                {
                    try { clicado = ParaVista(v, UiDoc.Selection.PickPoint(ObjectSnapTypes.Endpoints | ObjectSnapTypes.Intersections | ObjectSnapTypes.Midpoints, "Clique o canto de partida da paginação")); }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
                }
            }
            var cfg = new ConfigPaginacao
            {
                X = fX.Value, Y = fY.Value, Junta = fJ.Value, EspessuraPeca = fE.Value, Inicio = fIni.Index, Direcao = fAng?.Index ?? 0, Perda = fPerda.Value, Caixa = fCaixa.Value,
                Argamassa = fArg.Value, Texto = fTexto.Value, Ralos = fRalos?.Value ?? false, OrigemX = clicado?.X ?? 0, OrigemY = clicado?.Y ?? 0, Material = (fMat.Value ?? "").Trim()
            };
            if (planta && cfg.Ralos) DescontarRalos(v, regioes);

            var avisos = new List<string>();
            List<string> resumo = DesenharPaginacao(v, regioes, cfg, avisos, out int totalLinhas, out int trocadas);
            Show($"Paginação desenhada ({totalLinhas} linhas de junta, estilo “JA Paginação”)" + (trocadas > 0 ? $"; {trocadas} paginação(ões) antiga(s) substituída(s)" : "") + ".\n\n• " +
                 string.Join("\n• ", resumo) +
                 "\n\nCada ambiente / face virou um grupo “JA Paginação …”: selecione o grupo e rode de novo para refazer ou mudar; Atualizar Revestimentos refaz todas. O Orçamento e a Lista de Compras usam os números." +
                 (avisos.Count > 0 ? "\n\nAvisos:\n• " + string.Join("\n• ", avisos) : ""), avisos.Count > 0 ? ResultKind.Warning : ResultKind.Success);
            return Result.Succeeded;
        }
    }
}
