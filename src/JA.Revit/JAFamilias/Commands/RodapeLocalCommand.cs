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
    public abstract partial class EsquadriaCommandBase
    {
        /// <summary>A drawn stretch of rodapé: the points, snapped to the wall face, and what it runs along.</summary>
        private sealed class TrechoLocal
        {
            public XYZ A, B;
            public Wall Parede;
            public Element Pilar;  // column (architectural or structural) whose face it runs on, when nearer than any wall
            public bool Virado;   // turned on its own (against the rest of its chain): no corner closing
        }

        /// <summary>
        /// Rodapé no local: along the drawn path (Revit tools, lines, edges or clicks with orto), in a plan.
        /// With <paramref name="encostar"/> each stretch next to a straight wall (up to 50 cm away, or inside it)
        /// goes onto that wall's face on the drawing side, the stone facing away from the wall, and the
        /// corners meet; elsewhere the stone is on the left of the drawing (right with <paramref name="inverter"/>).
        /// Same discounts as the room rodapé along each wall. Ambiente = 0: the room tools never touch it.
        /// </summary>
        protected ResultadoRodape RodapeNoLocal(EntradaCroqui ent, ConfigRodape c, bool encostar, bool inverter, double baseMm, List<string> avisos)
        {
            Level nivel = (Doc.ActiveView as ViewPlan)?.GenLevel;
            if (nivel == null) { Show("Abra uma planta baixa para desenhar o rodapé.", ResultKind.Info); return null; }

            // the path(s): chains of straight pieces in plan
            var cadeias = new List<(List<XYZ> pts, bool fechada)>();
            if (ent.Curvas != null) cadeias = CadeiasEmPlanta(ent.Curvas);
            else
            {
                var pts = PegarPontos("Rodapé: clique o início junto da parede", "próximo ponto (Shift inverte o orto)", 500, true);
                if (pts != null && pts.Count >= 2)
                {
                    pts = pts.Select(p => new XYZ(p.X, p.Y, 0)).ToList();
                    bool fechada = pts.Count > 3 && pts[0].DistanceTo(pts[pts.Count - 1]) < 5 * MmFt;
                    if (fechada) pts.RemoveAt(pts.Count - 1);
                    cadeias.Add((pts, fechada));
                }
            }
            if (cadeias.Count == 0) { if (ent.Curvas != null) Show("Nenhum trecho reto no desenho.", ResultKind.Warning); return null; }

            FamilySymbol sym = SimboloRodape(c, avisos, out PedraConfig cfg);
            var res = new ResultadoRodape { Codigo = cfg.Codigo };
            if (sym == null) return res;
            CtxRodape k = PrepararRodape(c);
            double z = nivel.ProjectElevation + baseMm * MmFt;
            string json = Json.Serialize(c);
            var linhas = new Dictionary<string, LinhaRodape>();
            var paredes = new FilteredElementCollector(Doc).OfClass(typeof(Wall)).Cast<Wall>().Where(w => (w.Location as LocationCurve)?.Curve is Line).ToList();
            var pilares = encostar ? PilaresNoNivel(z) : new List<(Element el, List<FacePilar> faces)>();

            // the pieces and their locks: one Ctrl+Z (the family is already loaded here: safe in a group)
            var grupo = new TransactionGroup(Doc, "JA Famílias – rodapé");
            try { grupo.Start(); } catch { grupo = null; }
            try
            {
            using (var t = new Transaction(Doc, "JA Famílias – rodapé no local"))
            {
                t.Start();
                var o = t.GetFailureHandlingOptions(); o.SetFailuresPreprocessor(new WarningSwallower()); t.SetFailureHandlingOptions(o);
                var salas = new Dictionary<string, Room>();
                FamilyInstance Colocar(XYZ p0, XYZ p1)
                {
                    try
                    {
                        FamilyInstance novo = ChapimPlacer.PlaceLine(Doc, sym, p0, p1, nivel, z, avisos);
                        if (novo != null)
                        {
                            res.Trechos++;
                            res.Ids.Add(novo.Id);
                            JaDados.Gravar(novo, new DadosJa { Tipo = "rodape", Ambiente = 0, Config = json, Descricao = "local" });
                            RevitDwgUnifier.Tools.Parameters.ProjectParams.GravarNivel(novo, nivel);   // JA_Nivel + schedule level
                        }
                        return novo;
                    }
                    catch (Exception ex) { AddUm(avisos, "Trecho: " + ex.Message); return null; }
                }
                foreach (var (pts0, fechada) in cadeias)
                {
                    List<TrechoLocal> tr = Encostar(pts0, fechada, paredes, pilares, encostar, inverter);
                    var calc = new List<TrechoCalc>();
                    foreach (TrechoLocal s in tr)
                    {
                        if (s.A.DistanceTo(s.B) < 20 * MmFt) { calc.Add(null); continue; }
                        XYZ d = (s.B - s.A).Normalize(), esq = XYZ.BasisZ.CrossProduct(d);
                        // the room on the stone side (for the summary and its Acabamento de base)
                        XYZ m = (s.A + s.B) / 2 + esq * (300 * MmFt);
                        Room r = null;
                        try { r = Doc.GetRoomAtPoint(new XYZ(m.X, m.Y, z + 1)); } catch { }
                        string chave = r != null ? (r.Number + " " + (r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? r.Name)).Trim() : "Fora de ambiente";
                        if (!linhas.TryGetValue(chave, out LinhaRodape lr)) linhas[chave] = lr = new LinhaRodape { Ambiente = chave };
                        if (r != null) salas[chave] = r;
                        TrechoCalc tc = CalcularTrecho(k, c, s.A, s.B, (Element)s.Parede ?? s.Pilar, z, true, lr);
                        if (tc != null) tc.Virado = s.Virado;
                        calc.Add(tc);
                    }
                    ColocarTrechos(calc, fechada, c, k, Colocar, nivel.Id);
                }
                if (c.Acabamento)
                    foreach (var kv in salas)
                    {
                        if (!linhas.TryGetValue(kv.Key, out LinhaRodape lr) || lr.Liquido <= 0) continue;
                        Parameter pb = kv.Value.get_Parameter(BuiltInParameter.ROOM_FINISH_BASE);
                        if (pb != null && !pb.IsReadOnly && string.IsNullOrWhiteSpace(pb.AsString()))
                            try { pb.Set(cfg.Material + " h " + PecaUtil.Mm(cfg.LarguraMm) + " mm"); } catch { }
                    }
                ApagarCroqui(ent);
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
            var br = PecaUtil.Pt;
            foreach (LinhaRodape lr in linhas.Values)
            {
                res.Metros += lr.Liquido + lr.Retornos;
                res.Linhas.Add(lr);
                double desc = lr.Portas + lr.Janelas + lr.Vaos + lr.Moveis + lr.Cortina + lr.Revestidas + lr.Curtos;
                res.PorAmbiente.Add($"• {lr.Ambiente}: {(lr.Liquido + lr.Retornos).ToString("0.00", br)} m (desenhado {lr.Bruto.ToString("0.00", br)} m − descontos {desc.ToString("0.00", br)} m" +
                                    (lr.Retornos > 0 ? $" + retornos {lr.Retornos.ToString("0.00", br)} m" : "") + ")");
            }
            return res;
        }

        /// <summary>
        /// The drawing as chains in plan (z = 0): arcs tessellated, vertical and repeated pieces dropped
        /// (top and bottom edges of a face), pieces joined end to end (5 mm); closed when the ends meet.
        /// </summary>
        private static List<(List<XYZ> pts, bool fechada)> CadeiasEmPlanta(IEnumerable<Curve> curvas)
        {
            double tol = 5 * MmFt;
            XYZ P(XYZ p) => new XYZ(p.X, p.Y, 0);
            var segs = new List<(XYZ a, XYZ b)>();
            foreach (Curve cv in curvas)
            {
                IList<XYZ> q = cv is Line ? new List<XYZ> { cv.GetEndPoint(0), cv.GetEndPoint(1) } : cv.Tessellate();
                for (int i = 0; i + 1 < q.Count; i++)
                {
                    XYZ a = P(q[i]), b = P(q[i + 1]);
                    if (a.DistanceTo(b) < 20 * MmFt) continue;
                    if (segs.Any(x => (x.a.DistanceTo(a) < tol && x.b.DistanceTo(b) < tol) || (x.a.DistanceTo(b) < tol && x.b.DistanceTo(a) < tol))) continue;
                    segs.Add((a, b));
                }
            }
            var res = new List<(List<XYZ>, bool)>();
            while (segs.Count > 0)
            {
                var cadeia = new List<XYZ> { segs[0].a, segs[0].b };
                segs.RemoveAt(0);
                bool achou = true;
                while (achou)
                {
                    achou = false;
                    for (int i = 0; i < segs.Count; i++)
                    {
                        var (a, b) = segs[i];
                        XYZ ini = cadeia[0], fim = cadeia[cadeia.Count - 1];
                        if (fim.DistanceTo(a) < tol) cadeia.Add(b);
                        else if (fim.DistanceTo(b) < tol) cadeia.Add(a);
                        else if (ini.DistanceTo(b) < tol) cadeia.Insert(0, a);
                        else if (ini.DistanceTo(a) < tol) cadeia.Insert(0, b);
                        else continue;
                        segs.RemoveAt(i);
                        achou = true;
                        break;
                    }
                }
                bool fechada = cadeia.Count > 3 && cadeia[0].DistanceTo(cadeia[cadeia.Count - 1]) < tol;
                if (fechada) cadeia.RemoveAt(cadeia.Count - 1);
                res.Add((cadeia, fechada));
            }
            return res;
        }

        /// <summary>
        /// The stretches of one chain, each onto the face of the straight wall it runs along (if any) with the
        /// stone facing away from the wall; the chain turned so that most of it agrees; neighbours meet.
        /// Without <paramref name="encostar"/> the drawing stays as it is (the wall only for the discounts).
        /// </summary>
        private List<TrechoLocal> Encostar(List<XYZ> pts, bool fechada, List<Wall> paredes, List<(Element el, List<FacePilar> faces)> pilares, bool encostar, bool inverter)
        {
            int nSeg = fechada ? pts.Count : pts.Count - 1;
            var tr = new List<TrechoLocal>();
            var normais = new List<XYZ>();   // the face normal (away from the wall), null = no wall
            for (int i = 0; i < nSeg; i++)
            {
                XYZ a = pts[i], b = pts[(i + 1) % pts.Count];
                var s = new TrechoLocal { A = a, B = b };
                XYZ nrm = null;
                bool temParede = FaceDaParede(a, b, paredes, out Wall w, out XYZ q, out XYZ nf);
                if (temParede) s.Parede = w;
                // a column face nearer than the wall (pillar standing out of the wall, free-standing column): its face,
                // the stone facing out of the column — never the drawing side, which may be turned with the chain
                if (encostar && FaceDoPilar(a, b, pilares, out Element pil, out XYZ qp, out XYZ np, out double custoP))
                {
                    XYZ mm = (a + b) / 2;
                    double custoW = temParede ? Math.Max(0, (mm - q).DotProduct(nf)) : double.MaxValue;
                    if (custoP < custoW - 1 * MmFt) { s.Parede = null; s.Pilar = pil; q = qp; nf = np; }
                }
                if ((s.Parede != null || s.Pilar != null) && encostar)
                {
                    nrm = nf;
                    XYZ dw = XYZ.BasisZ.CrossProduct(nf);   // along the face
                    s.A = q + dw * (a - q).DotProduct(dw);
                    s.B = q + dw * (b - q).DotProduct(dw);
                }
                tr.Add(s);
                normais.Add(nrm);
            }
            // turn the chain? by length: stretches along walls vote for their face, free ones for the drawing side
            double contra = 0, aFavor = 0;
            for (int i = 0; i < tr.Count; i++)
            {
                double L = tr[i].A.DistanceTo(tr[i].B);
                if (L < 1e-6) continue;
                XYZ esq = XYZ.BasisZ.CrossProduct((tr[i].B - tr[i].A) / L);
                bool vira = normais[i] != null ? esq.DotProduct(normais[i]) < 0 : inverter;
                if (vira) contra += L; else aFavor += L;
            }
            if (contra > aFavor)
            {
                tr.Reverse(); normais.Reverse();
                foreach (TrechoLocal s in tr) { XYZ x = s.A; s.A = s.B; s.B = x; }
            }
            // stretches still against their face: turned alone
            for (int i = 0; i < tr.Count; i++)
            {
                if (normais[i] == null) continue;
                double L = tr[i].A.DistanceTo(tr[i].B);
                if (L < 1e-6) continue;
                if (XYZ.BasisZ.CrossProduct((tr[i].B - tr[i].A) / L).DotProduct(normais[i]) < 0)
                {
                    XYZ x = tr[i].A; tr[i].A = tr[i].B; tr[i].B = x; tr[i].Virado = true;
                }
            }
            if (!encostar) return tr;
            // neighbours meet: the crossing of the two lines, when it is near the drawn corner
            int n = tr.Count;
            for (int i = 0; i < n; i++)
            {
                if (!fechada && i == n - 1) break;
                TrechoLocal s = tr[i], p = tr[(i + 1) % n];
                if (s.Virado || p.Virado) continue;
                XYZ d1 = s.B - s.A, d2 = p.B - p.A;
                if (d1.GetLength() < 1e-6 || d2.GetLength() < 1e-6) continue;
                d1 = d1.Normalize(); d2 = d2.Normalize();
                double cr = d1.X * d2.Y - d1.Y * d2.X;
                if (Math.Abs(cr) < Math.Sin(5 * Math.PI / 180))
                {
                    if (s.B.DistanceTo(p.A) < 0.6 / 0.3048) { XYZ mid = (s.B + p.A) / 2; s.B = s.A + d1 * (mid - s.A).DotProduct(d1); p.A = p.B - d2 * (p.B - mid).DotProduct(d2); }
                    continue;
                }
                XYZ w0 = p.A - s.A;
                double t1 = (w0.X * d2.Y - w0.Y * d2.X) / cr;
                XYZ x = s.A + d1 * t1;
                if (x.DistanceTo(s.B) < 0.6 / 0.3048 && x.DistanceTo(p.A) < 0.6 / 0.3048) { s.B = x; p.A = x; }
            }
            return tr;
        }

        /// <summary>
        /// The face of a straight wall that a → b runs along: parallel (3°; up to 10° when the drawing runs on / in the
        /// wall), overlapping, up to 50 cm in front of the face, on it or a few mm into the wall (that face), or deeper
        /// inside the wall (then the face that looks into a room, else the one on the left of the drawing). The faces are read from the model (the room rodapé and the lock use the same faces).
        /// q = a point of the face, n = its normal (out of the wall).
        /// </summary>
        private static bool FaceDaParede(XYZ a, XYZ b, List<Wall> paredes, out Wall parede, out XYZ q, out XYZ n)
        {
            parede = null; q = n = null;
            double L = a.DistanceTo(b);
            if (L < 1e-6) return false;
            XYZ d = (b - a) / L, m = (a + b) / 2, esq = XYZ.BasisZ.CrossProduct(d);
            var cands = new List<(Wall w, double custo, XYZ n, XYZ q, List<(XYZ o, XYZ n)> faces)>();
            foreach (Wall w in paredes)
            {
                Line ln = (w.Location as LocationCurve)?.Curve as Line;
                if (ln == null) continue;
                XYZ s = new XYZ(ln.GetEndPoint(0).X, ln.GetEndPoint(0).Y, 0), e = new XYZ(ln.GetEndPoint(1).X, ln.GetEndPoint(1).Y, 0);
                double Lw = s.DistanceTo(e);
                if (Lw < 1e-6) continue;
                XYZ wd = (e - s) / Lw;
                double cos = Math.Abs(wd.DotProduct(d));
                if (cos < 0.9848) continue;                                              // more than 10°
                double ta = (a - s).DotProduct(wd), tb = (b - s).DotProduct(wd);
                if (Math.Max(ta, tb) < 0.05 / 0.3048 || Math.Min(ta, tb) > Lw - 0.05 / 0.3048) continue;   // no overlap
                double fora;              // in front of the face (negative = inside the wall)
                XYZ nw, qw;
                var faces = FacesLaterais(w, m);
                if (faces.Count == 2 && faces[0].n.DotProduct(faces[1].n) < -0.99)
                {
                    // the faces as they are in the model (layers, location line, joins already in them)
                    double Frente(XYZ p) => Math.Max((p - faces[0].o).DotProduct(faces[0].n), (p - faces[1].o).DotProduct(faces[1].n));
                    // 3° to 10° off the wall: only a drawing that runs on / in the wall (clicked points a little off)
                    if (cos < 0.9986 && (Frente(a) > 0.1 / 0.3048 || Frente(b) > 0.1 / 0.3048)) continue;
                    double t0 = (m - faces[0].o).DotProduct(faces[0].n), t1 = (m - faces[1].o).DotProduct(faces[1].n);
                    int f = t0 >= t1 ? 0 : 1;
                    fora = Math.Max(t0, t1);
                    // drawn on the face or a few mm into the wall (snapped to a layer line): that face; deeper inside the
                    // wall: the face that looks into a room (the other side outdoors), else the one on the left of the drawing
                    if (fora < -Math.Min(10 * MmFt, w.Width / 4))
                    {
                        int sala = LadoComAmbiente(w, m, faces);
                        f = sala >= 0 ? sala : esq.DotProduct(faces[0].n) >= 0 ? 0 : 1;
                    }
                    nw = faces[f].n; qw = faces[f].o;
                }
                else if (cos < 0.9986) continue;
                else
                {
                    // no faces to read: from the location line and the thickness
                    XYZ ori = new XYZ(w.Orientation.X, w.Orientation.Y, 0).Normalize();
                    double off = 0;
                    try { off = CortesEsquadriasCommand.WallOffset(w); } catch { }
                    XYZ centro = s + ori * off;
                    double sd = (m - centro).DotProduct(ori), meia = w.Width / 2;
                    double lado = Math.Abs(sd) > meia - Math.Min(10 * MmFt, meia / 2) ? Math.Sign(sd) : (esq.DotProduct(ori) >= 0 ? 1 : -1);
                    fora = Math.Abs(sd) - meia;
                    nw = ori * lado; qw = centro + nw * meia;
                }
                if (fora > 0.5 / 0.3048) continue;
                cands.Add((w, Math.Max(0, fora), nw, qw, faces));
            }
            // the nearest face; on a tie (a finish wall against the core wall: the line on the plane where they touch)
            // the face that looks out, not into the other wall
            bool DentroDeOutra(XYZ p, Wall propria) => cands.Any(x => x.w.Id != propria.Id && x.faces.Count == 2 &&
                (p - x.faces[0].o).DotProduct(x.faces[0].n) < -1 * MmFt && (p - x.faces[1].o).DotProduct(x.faces[1].n) < -1 * MmFt);
            if (cands.Count == 0) return false;
            double min = cands.Min(c => c.custo);
            var esc = cands.Where(c => c.custo <= min + 1 * MmFt)
                           .OrderBy(c => DentroDeOutra(m + c.n * (5 * MmFt), c.w) ? 1 : 0).ThenBy(c => c.custo).First();
            parede = esc.w; n = esc.n; q = esc.q;
            return true;
        }

        /// <summary>A vertical face of a column in plan: a point, the normal out of the column, the direction along it and its extent.</summary>
        private sealed class FacePilar
        {
            public XYZ O, N, Dir;
            public double T0, T1;
        }

        /// <summary>Columns (architectural and structural) standing at height z, with their vertical faces read from the model.</summary>
        private List<(Element el, List<FacePilar> faces)> PilaresNoNivel(double z)
        {
            var res = new List<(Element, List<FacePilar>)>();
            double zt = z + 30 * MmFt;
            var cats = new ElementMulticategoryFilter(new List<BuiltInCategory> { BuiltInCategory.OST_Columns, BuiltInCategory.OST_StructuralColumns });
            foreach (Element el in new FilteredElementCollector(Doc).WherePasses(cats).WhereElementIsNotElementType())
            {
                BoundingBoxXYZ bb = el.get_BoundingBox(null);
                if (bb == null || bb.Min.Z > zt || bb.Max.Z < zt) continue;
                var faces = new List<FacePilar>();
                var solidos = new List<Solid>();
                try { SolidosDe(el.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine }), solidos, 0); } catch { }
                foreach (Solid so in solidos)
                    foreach (Face f in so.Faces)
                    {
                        if (!(f is PlanarFace pf) || Math.Abs(pf.FaceNormal.Z) > 0.02) continue;
                        XYZ n = new XYZ(pf.FaceNormal.X, pf.FaceNormal.Y, 0).Normalize();
                        XYZ o = new XYZ(pf.Origin.X, pf.Origin.Y, 0), dir = XYZ.BasisZ.CrossProduct(n);
                        double t0 = double.MaxValue, t1 = double.MinValue, z0 = double.MaxValue, z1 = double.MinValue;
                        foreach (EdgeArray loop in pf.EdgeLoops)
                            foreach (Edge e in loop)
                                foreach (XYZ p in e.Tessellate())
                                {
                                    double t = (new XYZ(p.X, p.Y, 0) - o).DotProduct(dir);
                                    if (t < t0) t0 = t; if (t > t1) t1 = t;
                                    if (p.Z < z0) z0 = p.Z; if (p.Z > z1) z1 = p.Z;
                                }
                        if (t1 - t0 < 20 * MmFt || z0 > zt || z1 < zt) continue;
                        faces.Add(new FacePilar { O = o, N = n, Dir = dir, T0 = t0, T1 = t1 });
                    }
                if (faces.Count > 0) res.Add((el, faces));
            }
            return res;
        }

        private static void SolidosDe(GeometryElement ge, List<Solid> res, int prof)
        {
            if (ge == null || prof > 6) return;
            foreach (GeometryObject go in ge)
            {
                if (go is Solid so && so.Volume > 1e-9) res.Add(so);
                else if (go is GeometryInstance gi) SolidosDe(gi.GetInstanceGeometry(), res, prof + 1);
            }
        }

        /// <summary>
        /// The column face that a → b runs along: parallel (3°; up to 10° when drawn on the face), overlapping it, up to
        /// 50 cm in front or a few mm into the column. custo = distance in front of the face (0 on it or inside).
        /// </summary>
        private static bool FaceDoPilar(XYZ a, XYZ b, List<(Element el, List<FacePilar> faces)> pilares, out Element pilar, out XYZ q, out XYZ n, out double custo)
        {
            pilar = null; q = n = null; custo = double.MaxValue;
            double L = a.DistanceTo(b);
            if (L < 1e-6 || pilares == null) return false;
            XYZ d = (b - a) / L, m = (a + b) / 2;
            double melhorAbs = double.MaxValue;
            foreach (var (el, faces) in pilares)
                foreach (FacePilar f in faces)
                {
                    double cos = Math.Abs(f.Dir.DotProduct(d));
                    if (cos < 0.9848) continue;
                    double ta = (a - f.O).DotProduct(f.Dir), tb = (b - f.O).DotProduct(f.Dir);
                    if (Math.Max(ta, tb) < f.T0 + 20 * MmFt || Math.Min(ta, tb) > f.T1 - 20 * MmFt) continue;
                    double fora = (m - f.O).DotProduct(f.N);
                    if (fora > 0.5 / 0.3048 || fora < -30 * MmFt) continue;
                    double fa = (a - f.O).DotProduct(f.N), fb = (b - f.O).DotProduct(f.N);
                    if (cos < 0.9986 && (fa > 0.1 / 0.3048 || fb > 0.1 / 0.3048)) continue;
                    double cu = Math.Max(0, fora);
                    if (cu > custo + 1 * MmFt || (Math.Abs(cu - custo) <= 1 * MmFt && Math.Abs(fora) >= melhorAbs)) continue;
                    custo = cu; melhorAbs = Math.Abs(fora);
                    pilar = el; q = f.O; n = f.N;
                }
            return pilar != null;
        }

        /// <summary>The face (0 / 1) whose front, 30 cm out, is inside a room while the other's is not; −1 = both or neither.</summary>
        private static int LadoComAmbiente(Wall w, XYZ m, List<(XYZ o, XYZ n)> faces)
        {
            double z = 0;
            try
            {
                if (w.Document.GetElement(w.LevelId) is Level lv)
                    z = lv.ProjectElevation + (w.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0) + 1;
            }
            catch { }
            var dentro = new bool[2];
            for (int k = 0; k < 2; k++)
            {
                XYZ naFace = m - faces[k].n * (m - faces[k].o).DotProduct(faces[k].n);
                XYZ p = naFace + faces[k].n * (300 * MmFt);
                try { dentro[k] = w.Document.GetRoomAtPoint(new XYZ(p.X, p.Y, z)) != null; } catch { }
            }
            return dentro[0] == dentro[1] ? -1 : dentro[0] ? 0 : 1;
        }

        /// <summary>
        /// The two side faces of a wall as they are in the model (exterior, interior): a point of the plane (plan) and the
        /// normal out of the wall. On a side with several faces, the one in front of the drawing near the floor.
        /// </summary>
        private static List<(XYZ o, XYZ n)> FacesLaterais(Wall w, XYZ m)
        {
            var res = new List<(XYZ o, XYZ n)>();
            double zb = double.NaN;
            try
            {
                if (w.Document.GetElement(w.LevelId) is Level lv)
                    zb = lv.ProjectElevation + (w.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0) + 30 * MmFt;
            }
            catch { }
            foreach (ShellLayerType lado in new[] { ShellLayerType.Exterior, ShellLayerType.Interior })
            {
                IList<Reference> refs;
                try { refs = HostObjectUtils.GetSideFaces(w, lado); } catch { continue; }
                (XYZ o, XYZ n)? primeira = null, naFrente = null;
                foreach (Reference r in refs)
                {
                    PlanarFace pf = null;
                    try { pf = w.GetGeometryObjectFromReference(r) as PlanarFace; } catch { }
                    if (pf == null) continue;
                    XYZ nh = new XYZ(pf.FaceNormal.X, pf.FaceNormal.Y, 0);
                    if (nh.GetLength() < 0.99) continue;                      // vertical faces only
                    var face = (o: new XYZ(pf.Origin.X, pf.Origin.Y, 0), n: nh.Normalize());
                    if (primeira == null) primeira = face;
                    if (naFrente == null && !double.IsNaN(zb))
                        try { if (pf.Project(new XYZ(m.X, m.Y, zb)) != null) naFrente = face; } catch { }
                }
                var esc = naFrente ?? primeira;
                if (esc != null) res.Add(esc.Value);
            }
            return res;
        }
    }

    /// <summary>
    /// Rodapé no Local: draw the rodapé where it goes (Revit tools, lines, edges, clicks with orto) in a
    /// plan; each stretch goes onto the face of the wall it runs along, with the stone facing out, corners
    /// closed, and the same discounts of the room rodapé (doors, windows, openings, furniture…).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class RodapeNoLocalCommand : EsquadriaCommandBase
    {
        public override string Title => "Rodapé no Local";

        protected override Result Run()
        {
            if (!(Doc.ActiveView is ViewPlan vp) || vp.GenLevel == null)
            {
                Show("Abra uma planta baixa para desenhar o rodapé.", ResultKind.Info);
                return Result.Cancelled;
            }
            EntradaCroqui ent = EntradaDoCroqui("rodape", Doc.ActiveView,
                "Desenhe o rodapé junto das paredes, do lado onde ele vai (até 50 cm da face). Cada trecho encosta na face da parede; longe de parede, a pedra fica do lado esquerdo do sentido do desenho.");
            if (ent == null) return Result.Cancelled;
            if (ent.Iniciado) return Result.Succeeded;
            CroquiAtual = ent;

            var dlg = new OptionsDialog(Title, "RodapeLocal");
            dlg.Section("Peça");
            var fAlt = dlg.Number("altura", "Altura do rodapé (mm)", 70, 20, 400);
            var fEsp = dlg.Number("espessura", "Espessura (mm)", 15, 5, 40);
            var fMat = dlg.Text("material", "Material", "Granito cinza andorinha", "Pedra, porcelanato recortado, MDF, poliestireno… (vai para a lista e o orçamento).");
            dlg.Section("Posição");
            var fEnc = dlg.Check("encostar", "Encostar na face da parede mais próxima (até 50 cm)", true, "A pedra fica virada para o lado do desenho e os cantos se encontram.");
            var fInv = dlg.Check("inverter", "Longe de parede: pedra do lado direito do sentido do desenho", false);
            var fBase = dlg.Number("base", "Base acima do nível (mm)", 0, -500, 3000, "Ex.: piso elevado ou degrau.");
            CamposRodape fd = DescontosRodape(dlg, true);
            dlg.Section("Compra");
            var fPerda = dlg.Number("perda", "Perda (%)", 10, 0, 50);
            var fBarra = dlg.Number("barra", "Comprimento da peça (mm; 0 = por metro)", 0, 0, 6000);
            var fExcel = dlg.Check("excel", "Salvar a planilha dos descontos (Excel)", false);
            if (!dlg.Show(Owner)) return Result.Cancelled;

            var c = new ConfigRodape { Altura = fAlt.Value, Espessura = fEsp.Value, Material = fMat.Value, Perda = fPerda.Value, Barra = fBarra.Value };
            fd.Aplicar(c);
            var avisos = new List<string>();
            ResultadoRodape r0 = RodapeNoLocal(ent, c, fEnc.Value, fInv.Value, fBase.Value, avisos);
            if (r0 == null) return Result.Cancelled;
            MostrarRodape(new List<(ConfigRodape, ResultadoRodape)> { (c, r0) }, fExcel.Value, avisos,
                "Rodapé no local: fica fora do Rodapé por ambiente e do Atualizar Revestimentos (apague e desenhe de novo para mudar).");
            return Result.Succeeded;
        }
    }
}
