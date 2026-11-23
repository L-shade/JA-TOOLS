using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using RevitDwgUnifier.Tools.Common;
using RevitDwgUnifier.Tools.Exporting;

namespace RevitDwgUnifier.Tools.Parameters
{
    /// <summary>Small helpers shared by the checks (reports, numbers, plan geometry).</summary>
    internal static class Conferencia
    {
        public static readonly CultureInfo Br = new CultureInfo("pt-BR");
        public static string N(double v, string f = "0.00") => v.ToString(f, Br);
        public static string[] Palavras(string s) => (s ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0).ToArray();
        public static bool Tem(string nome, string[] palavras) { string n = (nome ?? "").ToLowerInvariant(); return palavras.Any(p => n.Contains(p)); }

        public static string Salvar(Document doc, string prefixo, XlsxWriter xw)
        {
            string folder = ExportNaming.DefaultFolder(doc);
            Directory.CreateDirectory(folder);
            string nome = Infrastructure.FileSafety.SanitizeFileName(prefixo + " - " + (string.IsNullOrWhiteSpace(doc.Title) ? "projeto" : doc.Title), 150);
            string path = Path.Combine(folder, nome + ".xlsx");
            try { xw.Save(path); }
            catch (IOException) { path = Path.Combine(folder, nome + " " + DateTime.Now.ToString("HHmmss") + ".xlsx"); xw.Save(path); }
            return path;
        }

        /// <summary>The room outline (finish faces) as 2D segments (ft).</summary>
        public static List<(XYZ a, XYZ b)> Segmentos(Room r)
        {
            var l = new List<(XYZ, XYZ)>();
            foreach (IList<BoundarySegment> laco in RevitUtil.Boundary(r) ?? new List<IList<BoundarySegment>>())
                foreach (BoundarySegment s in laco)
                {
                    Curve c = s.GetCurve();
                    if (c == null) continue;
                    IList<XYZ> t = c is Line ? new List<XYZ> { c.GetEndPoint(0), c.GetEndPoint(1) } : c.Tessellate();
                    for (int i = 0; i + 1 < t.Count; i++) l.Add((RevitUtil.Flat(t[i]), RevitUtil.Flat(t[i + 1])));
                }
            return l;
        }

        public static double DistSeg(XYZ p, XYZ a, XYZ b)
        {
            XYZ ab = b - a;
            double L2 = ab.DotProduct(ab);
            double t = L2 < 1e-12 ? 0 : Math.Max(0, Math.Min(1, (p - a).DotProduct(ab) / L2));
            return p.DistanceTo(a + ab * t);
        }

        public static double DistRet(XYZ p, double x0, double y0, double x1, double y1)
        {
            double dx = Math.Max(Math.Max(x0 - p.X, 0), p.X - x1), dy = Math.Max(Math.Max(y0 - p.Y, 0), p.Y - y1);
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>
        /// Largest free circle (radius, ft) inside the room, away from the walls and from the given
        /// obstacles (plan rectangles): a grid search, then refined around the best point.
        /// </summary>
        public static double MaiorCirculo(Room r, List<(XYZ a, XYZ b)> segs, List<double[]> obst, out XYZ centro)
        {
            centro = null;
            if (segs.Count == 0) return 0;
            double minX = segs.Min(s => Math.Min(s.a.X, s.b.X)), maxX = segs.Max(s => Math.Max(s.a.X, s.b.X));
            double minY = segs.Min(s => Math.Min(s.a.Y, s.b.Y)), maxY = segs.Max(s => Math.Max(s.a.Y, s.b.Y));
            double z = (r.Level?.ProjectElevation ?? 0) + r.BaseOffset + 1;
            double Livre(XYZ p)
            {
                double d = segs.Min(s => DistSeg(p, s.a, s.b));
                foreach (double[] o in obst) { if (p.X >= o[0] && p.X <= o[2] && p.Y >= o[1] && p.Y <= o[3]) return 0; d = Math.Min(d, DistRet(p, o[0], o[1], o[2], o[3])); }
                return d;
            }
            double passo = Math.Max(RevitUtil.M(0.05), Math.Max(maxX - minX, maxY - minY) / 60);
            double best = 0;
            for (double x = minX + passo / 2; x < maxX; x += passo)
                for (double y = minY + passo / 2; y < maxY; y += passo)
                {
                    var p = new XYZ(x, y, 0);
                    if (!r.IsPointInRoom(new XYZ(x, y, z))) continue;
                    double d = Livre(p);
                    if (d > best) { best = d; centro = p; }
                }
            if (centro == null) return 0;
            // refine
            for (double h = passo / 2; h > RevitUtil.M(0.005); h /= 2)
            {
                bool melhorou = true;
                while (melhorou)
                {
                    melhorou = false;
                    foreach (var dlt in new[] { new XYZ(h, 0, 0), new XYZ(-h, 0, 0), new XYZ(0, h, 0), new XYZ(0, -h, 0) })
                    {
                        XYZ q = centro + dlt;
                        if (!r.IsPointInRoom(new XYZ(q.X, q.Y, z))) continue;
                        double d = Livre(q);
                        if (d > best + 1e-9) { best = d; centro = q; melhorou = true; }
                    }
                }
            }
            return best;
        }

        /// <summary>
        /// Narrowest width of a corridor (ft): rays from the walls, square to them, to the opposite
        /// side; the ends of each stretch (corners) are skipped.
        /// </summary>
        public static double LarguraMinima(Room r, List<(XYZ a, XYZ b)> segs, out XYZ onde)
        {
            onde = null;
            double z = (r.Level?.ProjectElevation ?? 0) + r.BaseOffset + 1;
            double best = double.MaxValue, ponta = RevitUtil.M(0.25), passo = RevitUtil.M(0.10);
            foreach (var s in segs)
            {
                XYZ d = s.b - s.a;
                double L = d.GetLength();
                if (L < 2 * ponta + 1e-6) continue;
                d = d / L;
                XYZ n = new XYZ(-d.Y, d.X, 0);
                // the inside side
                XYZ meio = s.a + d * (L / 2);
                XYZ teste = meio + n * RevitUtil.M(0.05);
                if (!r.IsPointInRoom(new XYZ(teste.X, teste.Y, z))) n = -n;
                for (double t = ponta; t <= L - ponta + 1e-6; t += passo)
                {
                    XYZ p = s.a + d * t;
                    double hit = Raio(segs, p + n * 1e-4, n);
                    if (hit > RevitUtil.M(0.2) && hit < best) { best = hit; onde = p + n * (hit / 2); }
                }
            }
            return best == double.MaxValue ? 0 : best;
        }

        public static double Raio(List<(XYZ a, XYZ b)> segs, XYZ p, XYZ dir)
        {
            double best = double.MaxValue;
            foreach (var s in segs)
            {
                XYZ e = s.b - s.a, w = s.a - p;
                double den = dir.X * e.Y - dir.Y * e.X;
                if (Math.Abs(den) < 1e-12) continue;
                double t = (w.X * e.Y - w.Y * e.X) / den, u = (w.X * dir.Y - w.Y * dir.X) / den;
                if (t > 1e-6 && u >= -1e-6 && u <= 1 + 1e-6 && t < best) best = t;
            }
            return best == double.MaxValue ? 0 : best;
        }

        /// <summary>Length of the room along its own axis (the direction of its longest wall), ft.</summary>
        public static double Extensao(List<(XYZ a, XYZ b)> segs)
        {
            if (segs.Count == 0) return 0;
            var maior = segs.OrderByDescending(s => s.a.DistanceTo(s.b)).First();
            XYZ d = (maior.b - maior.a).Normalize();
            var proj = segs.SelectMany(s => new[] { s.a, s.b }).Select(p => p.DotProduct(d)).ToList();
            return proj.Max() - proj.Min();
        }

        public static double Dim(Element e, params BuiltInParameter[] bips)
        {
            Element t = e.Document.GetElement(e.GetTypeId());
            foreach (BuiltInParameter b in bips)
                foreach (Element x in new[] { e, t })
                {
                    Parameter p = x?.get_Parameter(b);
                    if (p != null && p.StorageType == StorageType.Double && p.AsDouble() > 0) return p.AsDouble();
                }
            return 0;
        }

        public static double Largura(FamilyInstance fi) => Dim(fi, BuiltInParameter.DOOR_WIDTH, BuiltInParameter.WINDOW_WIDTH, BuiltInParameter.FAMILY_WIDTH_PARAM, BuiltInParameter.FAMILY_ROUGH_WIDTH_PARAM);
        public static double Altura(FamilyInstance fi) => Dim(fi, BuiltInParameter.DOOR_HEIGHT, BuiltInParameter.WINDOW_HEIGHT, BuiltInParameter.FAMILY_HEIGHT_PARAM, BuiltInParameter.FAMILY_ROUGH_HEIGHT_PARAM);

        public static bool DoAmbiente(FamilyInstance fi, Room r)
        {
            try { return fi.FromRoom?.Id == r.Id || fi.ToRoom?.Id == r.Id; } catch { return false; }
        }

        public static string Nome(Room r) => ((r.Number ?? "") + " " + RevitUtil.RoomName(r)).Trim();
    }

    // =====================================================================================
    //  Conferir Acessibilidade (NBR 9050)
    // =====================================================================================

    /// <summary>
    /// Conferir Acessibilidade: a first check against NBR 9050 — the clear width of the doors, the
    /// 1,50 m turning circle in the accessible toilets (fixtures and furniture as obstacles) and the
    /// width of the corridors by their length. Excel report, the failing elements selected.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class ConferirAcessibilidadeCommand : ToolCommand
    {
        public override string Title => "Conferir Acessibilidade";

        protected override Result Run()
        {
            var rooms = RevitUtil.Rooms(Doc);
            var portas = new FilteredElementCollector(Doc).OfCategory(BuiltInCategory.OST_Doors).WhereElementIsNotElementType().OfType<FamilyInstance>().ToList();
            if (rooms.Count == 0 && portas.Count == 0) { Show("O projeto não tem ambientes nem portas.", ResultKind.Info); return Result.Cancelled; }

            var dlg = new OptionsDialog(Title, "Acessibilidade", "Conferir");
            dlg.Section("Portas");
            var fPortas = dlg.Check("portas", "Conferir o vão livre das portas", true);
            var fVao = dlg.Number("vao", "Vão livre mínimo (m)", 0.80, 0.5, 2, "NBR 9050 6.11.2.4: 0,80 m de vão livre (folha aberta a 90°).");
            var fDesc = dlg.Number("desconto", "Folha → vão livre: desconto (mm)", 40, 0, 200, "O vão livre é a largura da folha menos o batente e a folha aberta (≈ 40 mm em portas de giro). 0 = a largura da família já é o vão livre.");
            var fIgnorar = dlg.Text("ignorar", "Portas de ambientes que não entram (palavras)", "shaft, depósito, despensa, closet, área técnica, casa de máquinas");
            dlg.Section("Sanitários acessíveis");
            var fSan = dlg.Text("sanitario", "Ambientes (palavras no nome)", "pcd, pne, acessível, acessivel, adaptado");
            var fGiro = dlg.Number("giro", "Diâmetro de giro livre (m)", 1.50, 1, 3, "NBR 9050 4.3.4 / 7.5: círculo de 1,50 m para giro de 360° da cadeira de rodas.");
            var fObst = dlg.Check("obst", "Louças, bancadas e móveis contam como obstáculo", true, "Usa a projeção (caixa) das peças sanitárias, marcenaria e mobiliário do ambiente.");
            dlg.Section("Circulações");
            var fCirc = dlg.Text("circ", "Ambientes (palavras no nome)", "circulação, circulacao, corredor, hall, passagem");
            var fRegra = dlg.Choice("regra", "Largura exigida", new[] { "Pela extensão (NBR 9050 6.12.1: 0,90 até 4 m; 1,20 até 10 m; 1,50 acima)", "Uso público: 1,50 m", "Valor fixo abaixo" }, 0);
            var fFixo = dlg.Number("fixo", "Largura fixa (m)", 1.20, 0.6, 5);
            fFixo.EnabledWhen(fRegra, 2);
            if (!dlg.Show(Owner)) return Result.Cancelled;

            string[] ignorar = Conferencia.Palavras(fIgnorar.Value), san = Conferencia.Palavras(fSan.Value), circ = Conferencia.Palavras(fCirc.Value);
            var falhas = new List<ElementId>();
            var linhas = new List<string>();
            var tPortas = new XlsxWriter.Table { Name = "Portas", Title = "Portas — vão livre" };
            tPortas.Rows.Add(new[] { "Porta (tipo)", "Marca", "Ambientes", "Largura da família (m)", "Vão livre estimado (m)", "Mínimo (m)", "Situação" });
            int pOk = 0, pNao = 0;
            if (fPortas.Value)
                foreach (FamilyInstance d in portas)
                {
                    Room a = null, b = null;
                    try { a = d.FromRoom; b = d.ToRoom; } catch { }
                    var ambs = new[] { a, b }.Where(x => x != null).ToList();
                    if (ambs.Count > 0 && ambs.All(x => Conferencia.Tem(RevitUtil.RoomName(x), ignorar))) continue;
                    double w = Conferencia.Largura(d) * 0.3048;
                    if (w <= 0) continue;
                    double livre = w - fDesc.Value / 1000;
                    bool ok = livre + 1e-6 >= fVao.Value;
                    string marca = d.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "";
                    tPortas.Rows.Add(new[] { d.Symbol.FamilyName + ": " + d.Symbol.Name, marca, string.Join(" / ", ambs.Select(Conferencia.Nome)), Conferencia.N(w), Conferencia.N(livre), Conferencia.N(fVao.Value), ok ? "OK" : "Não atende" });
                    if (ok) pOk++; else { pNao++; falhas.Add(d.Id); }
                }

            var tSan = new XlsxWriter.Table { Name = "Sanitários", Title = "Sanitários acessíveis — giro de 360°" };
            tSan.Rows.Add(new[] { "Ambiente", "Nível", "Área (m²)", "Maior círculo livre (m)", "Exigido (m)", "Porta: menor vão livre (m)", "Situação" });
            var tCirc = new XlsxWriter.Table { Name = "Circulações", Title = "Circulações — largura mínima" };
            tCirc.Rows.Add(new[] { "Ambiente", "Nível", "Extensão (m)", "Menor largura (m)", "Exigida (m)", "Situação" });
            var obstCats = new List<BuiltInCategory> { BuiltInCategory.OST_PlumbingFixtures, BuiltInCategory.OST_Casework, BuiltInCategory.OST_Furniture, BuiltInCategory.OST_SpecialityEquipment, BuiltInCategory.OST_GenericModel };
            var obstaculos = fObst.Value
                ? new FilteredElementCollector(Doc).WherePasses(new ElementMulticategoryFilter(obstCats)).WhereElementIsNotElementType().OfType<FamilyInstance>().ToList()
                : new List<FamilyInstance>();
            int sOk = 0, sNao = 0, cOk = 0, cNao = 0;
            foreach (Room r in rooms)
            {
                string nome = RevitUtil.RoomName(r);
                bool eSan = Conferencia.Tem(nome, san), eCirc = !eSan && Conferencia.Tem(nome, circ);
                if (!eSan && !eCirc) continue;
                var segs = Conferencia.Segmentos(r);
                if (segs.Count == 0) continue;
                double area = r.Area * 0.09290304;
                if (eSan)
                {
                    var obst = new List<double[]>();
                    double z = (r.Level?.ProjectElevation ?? 0) + r.BaseOffset + 1;
                    foreach (FamilyInstance o in obstaculos)
                    {
                        BoundingBoxXYZ bb = o.get_BoundingBox(null);
                        if (bb == null) continue;
                        XYZ c = (bb.Min + bb.Max) / 2;
                        if (!r.IsPointInRoom(new XYZ(c.X, c.Y, z))) continue;
                        // tall elements only (a floor drain or a rug is not an obstacle)
                        if (bb.Max.Z - bb.Min.Z < RevitUtil.M(0.15)) continue;
                        obst.Add(new[] { bb.Min.X, bb.Min.Y, bb.Max.X, bb.Max.Y });
                    }
                    double raio = Conferencia.MaiorCirculo(r, segs, obst, out _);
                    double diam = raio * 2 * 0.3048;
                    var suas = portas.Where(d => Conferencia.DoAmbiente(d, r)).Select(d => Conferencia.Largura(d) * 0.3048 - fDesc.Value / 1000).Where(x => x > 0).ToList();
                    double porta = suas.Count > 0 ? suas.Min() : 0;
                    var faltas = new List<string>();
                    if (diam + 1e-3 < fGiro.Value) faltas.Add("giro " + Conferencia.N(diam) + " m < " + Conferencia.N(fGiro.Value) + " m");
                    if (suas.Count > 0 && porta + 1e-6 < fVao.Value) faltas.Add("porta " + Conferencia.N(porta) + " m");
                    if (suas.Count == 0) faltas.Add("sem porta encontrada (fase?)");
                    tSan.Rows.Add(new[] { Conferencia.Nome(r), r.Level?.Name ?? "", Conferencia.N(area), Conferencia.N(diam), Conferencia.N(fGiro.Value), suas.Count > 0 ? Conferencia.N(porta) : "—", faltas.Count == 0 ? "OK" : "Não atende: " + string.Join("; ", faltas) });
                    if (faltas.Count == 0) sOk++; else { sNao++; falhas.Add(r.Id); linhas.Add("• " + Conferencia.Nome(r) + ": " + string.Join("; ", faltas)); }
                }
                else
                {
                    double larg = Conferencia.LarguraMinima(r, segs, out _) * 0.3048, ext = Conferencia.Extensao(segs) * 0.3048;
                    double exig = fRegra.Index == 1 ? 1.50 : fRegra.Index == 2 ? fFixo.Value : ext <= 4 ? 0.90 : ext <= 10 ? 1.20 : 1.50;
                    bool ok = larg <= 0 || larg + 1e-3 >= exig;
                    tCirc.Rows.Add(new[] { Conferencia.Nome(r), r.Level?.Name ?? "", Conferencia.N(ext), larg > 0 ? Conferencia.N(larg) : "—", Conferencia.N(exig), ok ? "OK" : "Não atende" });
                    if (ok) cOk++; else { cNao++; falhas.Add(r.Id); linhas.Add("• " + Conferencia.Nome(r) + ": largura " + Conferencia.N(larg) + " m < " + Conferencia.N(exig) + " m (extensão " + Conferencia.N(ext, "0.0") + " m)"); }
                }
            }
            var xw = new XlsxWriter();
            if (fPortas.Value) xw.Add(tPortas);
            xw.Add(tSan);
            xw.Add(tCirc);
            string path = Conferencia.Salvar(Doc, "Acessibilidade", xw);
            if (falhas.Count > 0) { try { UiDoc.Selection.SetElementIds(falhas); } catch { } }
            var sb = new StringBuilder();
            if (fPortas.Value) sb.AppendLine($"Portas: {pOk} atendem, {pNao} com vão livre menor que {Conferencia.N(fVao.Value)} m.");
            sb.AppendLine($"Sanitários acessíveis: {sOk} atendem, {sNao} não atendem" + (sOk + sNao == 0 ? " (nenhum ambiente com essas palavras no nome)" : "") + ".");
            sb.AppendLine($"Circulações: {cOk} atendem, {cNao} estreitas" + (cOk + cNao == 0 ? " (nenhuma com essas palavras no nome)" : "") + ".");
            if (linhas.Count > 0) sb.AppendLine().AppendLine(string.Join("\n", linhas.Take(20)));
            if (falhas.Count > 0) sb.AppendLine().AppendLine("As portas e os ambientes que não atendem ficaram selecionados.");
            sb.AppendLine().AppendLine(path);
            sb.Append("É uma primeira conferência geométrica: barras de apoio, alturas de louças, maçanetas, desníveis, rampas e sinalização ficam para a revisão do projeto.");
            Show(sb.ToString(), falhas.Count > 0 ? ResultKind.Warning : ResultKind.Success, OpenFolderAction(Path.GetDirectoryName(path)));
            return Result.Succeeded;
        }
    }

    // =====================================================================================
    //  Conferir Escadas
    // =====================================================================================

    /// <summary>
    /// Conferir Escadas: every stair of the project against the riser / tread limits, the Blondel
    /// formula (2e + p), the width of the runs, the rise between landings and the railings.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class ConferirEscadasCommand : ToolCommand
    {
        public override string Title => "Conferir Escadas";

        protected override Result Run()
        {
            var escadas = new FilteredElementCollector(Doc).OfClass(typeof(Stairs)).Cast<Stairs>().ToList();
            if (escadas.Count == 0) { Show("O projeto não tem escadas (elementos Escada do Revit).", ResultKind.Info); return Result.Cancelled; }

            var dlg = new OptionsDialog(Title, "ConferirEscadas", "Conferir");
            var fUso = dlg.Choice("uso", "Referência", new[] { "Rota acessível / uso comum (NBR 9050)", "Uso privativo (residência unifamiliar)" }, 0,
                "Os limites abaixo mudam com a escolha; confira também o código de obras e a NBR 9077 (saídas de emergência).");
            var fEMin = dlg.Number("eMin", "Espelho mínimo (cm)", 16, 10, 25);
            var fEMax = dlg.Number("eMax", "Espelho máximo (cm)", 18, 10, 25);
            var fPMin = dlg.Number("pMin", "Piso mínimo (cm)", 28, 15, 45);
            var fPMax = dlg.Number("pMax", "Piso máximo (cm)", 32, 15, 45);
            var fBMin = dlg.Number("bMin", "Blondel 2e + p: mínimo (cm)", 63, 50, 75);
            var fBMax = dlg.Number("bMax", "Blondel 2e + p: máximo (cm)", 65, 50, 75);
            var fLarg = dlg.Number("larg", "Largura mínima do lance (m)", 1.20, 0.5, 5);
            var fPat = dlg.Number("patamar", "Desnível máximo sem patamar (m)", 3.20, 1, 6, "NBR 9050 6.8.7: patamar a cada 3,20 m de desnível.");
            var fCorr = dlg.Number("corrimaos", "Corrimãos (guarda-corpos) mínimos por escada", 2, 0, 4, "NBR 9050 6.9: corrimão dos dois lados.");
            dlg.Note("Uso privativo usual: espelho até 19 cm, piso a partir de 25 cm, largura 0,80 m.");
            if (!dlg.Show(Owner)) return Result.Cancelled;
            bool priv = fUso.Index == 1;
            // the "private" choice relaxes the defaults the user did not change
            double eMin = fEMin.Value, eMax = priv && fEMax.Value == 18 ? 19 : fEMax.Value, pMin = priv && fPMin.Value == 28 ? 25 : fPMin.Value, pMax = fPMax.Value;
            double bMin = fBMin.Value, bMax = fBMax.Value, larg = priv && Math.Abs(fLarg.Value - 1.20) < 1e-6 ? 0.80 : fLarg.Value;

            var t = new XlsxWriter.Table { Name = "Escadas", Title = "Conferência de escadas" + (priv ? " (uso privativo)" : " (NBR 9050)") };
            t.Rows.Add(new[] { "Escada", "Base → topo", "Altura (m)", "Espelhos", "Espelho (cm)", "Piso (cm)", "2e + p (cm)", "Menor largura (m)", "Maior desnível sem patamar (m)", "Corrimãos", "Situação" });
            var falhas = new List<ElementId>();
            var linhas = new List<string>();
            int ok = 0;
            foreach (Stairs s in escadas)
            {
                string nome = (Doc.GetElement(s.GetTypeId())?.Name ?? "Escada") + " [" + s.Id.Value + "]";
                var faltas = new List<string>();
                double e = s.ActualRiserHeight * 30.48, p = s.ActualTreadDepth * 30.48, h = s.Height * 0.3048;
                double blondel = 2 * e + p;
                if (e < eMin - 0.05 || e > eMax + 0.05) faltas.Add($"espelho {Conferencia.N(e, "0.0")} cm");
                if (p < pMin - 0.05 || p > pMax + 0.05) faltas.Add($"piso {Conferencia.N(p, "0.0")} cm");
                if (blondel < bMin - 0.05 || blondel > bMax + 0.05) faltas.Add($"2e + p = {Conferencia.N(blondel, "0.0")} cm");
                double menorLarg = double.MaxValue, maiorLance = 0;
                try
                {
                    foreach (ElementId rid in s.GetStairsRuns())
                    {
                        if (!(Doc.GetElement(rid) is StairsRun run)) continue;
                        menorLarg = Math.Min(menorLarg, run.ActualRunWidth * 0.3048);
                        maiorLance = Math.Max(maiorLance, run.ActualRisersNumber * s.ActualRiserHeight * 0.3048);
                    }
                }
                catch { }
                if (menorLarg == double.MaxValue) menorLarg = 0;
                if (menorLarg > 0 && menorLarg + 1e-3 < larg) faltas.Add($"largura {Conferencia.N(menorLarg)} m");
                if (maiorLance > fPat.Value + 1e-3) faltas.Add($"lance de {Conferencia.N(maiorLance)} m sem patamar");
                int corr = 0;
                try { corr = s.GetAssociatedRailings().Count; } catch { }
                if (corr < fCorr.Value) faltas.Add(corr == 0 ? "sem corrimão / guarda-corpo" : $"{corr} corrimão(s)");
                string niveis = (Doc.GetElement(s.get_Parameter(BuiltInParameter.STAIRS_BASE_LEVEL_PARAM)?.AsElementId() ?? ElementId.InvalidElementId)?.Name ?? "?") + " → " +
                                (Doc.GetElement(s.get_Parameter(BuiltInParameter.STAIRS_TOP_LEVEL_PARAM)?.AsElementId() ?? ElementId.InvalidElementId)?.Name ?? "?");
                t.Rows.Add(new[] { nome, niveis, Conferencia.N(h), s.ActualRisersNumber.ToString(), Conferencia.N(e, "0.0"), Conferencia.N(p, "0.0"), Conferencia.N(blondel, "0.0"),
                                   menorLarg > 0 ? Conferencia.N(menorLarg) : "—", Conferencia.N(maiorLance), corr.ToString(), faltas.Count == 0 ? "OK" : "Não atende: " + string.Join("; ", faltas) });
                if (faltas.Count == 0) ok++;
                else { falhas.Add(s.Id); linhas.Add("• " + nome + " (" + niveis + "): " + string.Join("; ", faltas)); }
            }
            var xw = new XlsxWriter();
            xw.Add(t);
            string path = Conferencia.Salvar(Doc, "Escadas", xw);
            if (falhas.Count > 0) { try { UiDoc.Selection.SetElementIds(falhas); } catch { } }
            Show($"{escadas.Count} escada(s): {ok} atendem, {falhas.Count} não atendem." + (linhas.Count > 0 ? "\n\n" + string.Join("\n", linhas.Take(20)) + "\n\nAs que não atendem ficaram selecionadas." : "") +
                 $"\n\nLimites: espelho {Conferencia.N(eMin, "0")}–{Conferencia.N(eMax, "0")} cm, piso {Conferencia.N(pMin, "0")}–{Conferencia.N(pMax, "0")} cm, 2e + p {Conferencia.N(bMin, "0")}–{Conferencia.N(bMax, "0")} cm, largura ≥ {Conferencia.N(larg)} m.\n\n{path}",
                 falhas.Count > 0 ? ResultKind.Warning : ResultKind.Success, OpenFolderAction(Path.GetDirectoryName(path)));
            return Result.Succeeded;
        }
    }

    // =====================================================================================
    //  Carimbo em Lote
    // =====================================================================================

    /// <summary>
    /// Carimbo em Lote: the project data (Informações do projeto) and the sheet fields (desenhado,
    /// verificado, aprovado, data) filled at once on all or some sheets, plus any other parameter
    /// as "Nome = valor"; then a report of what is still blank on each sheet.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class CarimboLoteCommand : ToolCommand
    {
        public override string Title => "Carimbo em Lote";

        private static readonly (BuiltInParameter Bip, string Rotulo)[] Projeto =
        {
            (BuiltInParameter.PROJECT_NAME, "Nome do projeto"), (BuiltInParameter.PROJECT_NUMBER, "Número do projeto"), (BuiltInParameter.CLIENT_NAME, "Cliente"),
            (BuiltInParameter.PROJECT_ADDRESS, "Endereço"), (BuiltInParameter.PROJECT_STATUS, "Etapa / status"), (BuiltInParameter.PROJECT_ISSUE_DATE, "Data de emissão do projeto"),
            (BuiltInParameter.PROJECT_AUTHOR, "Autor"), (BuiltInParameter.PROJECT_ORGANIZATION_NAME, "Escritório"), (BuiltInParameter.PROJECT_BUILDING_NAME, "Edificação"),
        };

        private static readonly (BuiltInParameter Bip, string Rotulo)[] Folha =
        {
            (BuiltInParameter.SHEET_DRAWN_BY, "Desenhado por"), (BuiltInParameter.SHEET_DESIGNED_BY, "Projetado por"), (BuiltInParameter.SHEET_CHECKED_BY, "Verificado por"),
            (BuiltInParameter.SHEET_APPROVED_BY, "Aprovado por"), (BuiltInParameter.SHEET_ISSUE_DATE, "Data da folha"),
        };

        protected override Result Run()
        {
            var sheets = new FilteredElementCollector(Doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => !s.IsPlaceholder)
                .OrderBy(s => s.SheetNumber, StringComparer.OrdinalIgnoreCase).ToList();
            if (sheets.Count == 0) { Show("O projeto não tem pranchas.", ResultKind.Info); return Result.Cancelled; }
            ProjectInfo pi = Doc.ProjectInformation;
            string Atual(Element e, BuiltInParameter b) { try { return e?.get_Parameter(b)?.AsString() ?? ""; } catch { return ""; } }

            var dlg = new OptionsDialog(Title, "CarimboLote", "Aplicar");
            dlg.Note("Campo vazio = não mexe. Os valores do projeto valem para todas as pranchas (o carimbo lê das Informações do projeto).");
            dlg.Section("Projeto");
            var fProj = Projeto.Select(x => { var f = dlg.Text("p" + (int)x.Bip, x.Rotulo, ""); f.Value = Atual(pi, x.Bip); return f; }).ToList();
            dlg.Section("Pranchas");
            var sel = new HashSet<ElementId>(UiDoc.Selection.GetElementIds());
            var fSheets = dlg.Multi("pranchas", "Pranchas", sheets.Select(s => s.SheetNumber + " - " + s.Name).ToList(), sel.Count == 0, true);
            if (sel.Count > 0) for (int i = 0; i < sheets.Count; i++) fSheets.Checked[i] = sel.Contains(sheets[i].Id);
            var fFolha = Folha.Select(x => { var f = dlg.Text("f" + (int)x.Bip, x.Rotulo, ""); f.Value = ""; return f; }).ToList();
            var fHoje = dlg.Check("hoje", "Data da folha = hoje (" + DateTime.Now.ToString("dd/MM/yyyy") + ")", false);
            dlg.Section("Outros parâmetros");
            var fOutros = dlg.Text("outros", "Nome = valor (separe por ;)", "", "Parâmetros da prancha, do carimbo (família de folha) ou das Informações do projeto, ex.: Revisão do carimbo = R02; Escala geral = Indicada");
            var fConferir = dlg.Text("conferir", "Conferir vazios nestes parâmetros (separe por ;)", "", "Além de Desenhado / Verificado / Data: nomes de parâmetros que o seu carimbo mostra.");
            if (!dlg.Show(Owner)) return Result.Cancelled;

            var alvo = fSheets.SelectedIndices.Select(i => sheets[i]).ToList();
            var outros = (fOutros.Value ?? "").Split(';').Select(x => x.Split(new[] { '=' }, 2)).Where(a => a.Length == 2 && a[0].Trim().Length > 0)
                .Select(a => (Nome: a[0].Trim(), Valor: a[1].Trim())).ToList();
            var avisos = new List<string>();
            int nProj = 0, nFolha = 0, nOutros = 0;
            bool SetStr(Element e, Parameter p, string v)
            {
                if (p == null || p.IsReadOnly) return false;
                try
                {
                    if (p.StorageType == StorageType.String) return p.Set(v);
                    if (p.StorageType == StorageType.Integer && int.TryParse(v, out int iv)) return p.Set(iv);
                    return p.SetValueString(v);
                }
                catch { return false; }
            }
            using (var t = new Transaction(Doc, "JA Tools - " + Title))
            {
                t.Start();
                for (int i = 0; i < Projeto.Length; i++)
                {
                    string v = fProj[i].Value ?? "";
                    if (string.IsNullOrWhiteSpace(v) || v == Atual(pi, Projeto[i].Bip)) continue;
                    if (SetStr(pi, pi.get_Parameter(Projeto[i].Bip), v)) nProj++;
                }
                foreach (ViewSheet s in alvo)
                {
                    for (int i = 0; i < Folha.Length; i++)
                    {
                        string v = Folha[i].Bip == BuiltInParameter.SHEET_ISSUE_DATE && fHoje.Value ? DateTime.Now.ToString("dd/MM/yyyy") : fFolha[i].Value;
                        if (string.IsNullOrWhiteSpace(v)) continue;
                        if (SetStr(s, s.get_Parameter(Folha[i].Bip), v.Trim())) nFolha++;
                    }
                    if (outros.Count == 0) continue;
                    var blocos = new FilteredElementCollector(Doc, s.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType().ToList();
                    foreach (var (nome, valor) in outros)
                    {
                        bool feito = SetStr(s, s.LookupParameter(nome), valor);
                        foreach (Element b in blocos) feito |= SetStr(b, b.LookupParameter(nome), valor);
                        if (!feito) feito = SetStr(pi, pi.LookupParameter(nome), valor);
                        if (feito) nOutros++; else RevitUtil.AddOnce(avisos, $"“{nome}” não encontrado (ou só leitura) na prancha {s.SheetNumber}.");
                    }
                }
                t.Commit();
            }

            // what is still blank
            var conferir = (fConferir.Value ?? "").Split(';').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            var tab = new XlsxWriter.Table { Name = "Carimbo", Title = "Carimbo — campos vazios" };
            var cab = new List<string> { "Número", "Nome" };
            cab.AddRange(Folha.Select(f => f.Rotulo));
            cab.AddRange(conferir);
            tab.Rows.Add(cab.ToArray());
            int comVazio = 0;
            foreach (ViewSheet s in sheets)
            {
                var row = new List<string> { s.SheetNumber, s.Name };
                bool vazio = false;
                foreach (var f in Folha) { string v = Atual(s, f.Bip); row.Add(string.IsNullOrWhiteSpace(v) ? "(vazio)" : v); vazio |= string.IsNullOrWhiteSpace(v); }
                Element bloco = new FilteredElementCollector(Doc, s.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType().FirstElement();
                foreach (string n in conferir)
                {
                    Parameter p = s.LookupParameter(n) ?? bloco?.LookupParameter(n) ?? pi?.LookupParameter(n);
                    string v = p == null ? "(não existe)" : p.StorageType == StorageType.String ? p.AsString() : p.AsValueString();
                    row.Add(string.IsNullOrWhiteSpace(v) ? "(vazio)" : v);
                    vazio |= string.IsNullOrWhiteSpace(v);
                }
                if (vazio) comVazio++;
                tab.Rows.Add(row.ToArray());
            }
            var vaziosProj = Projeto.Where(x => string.IsNullOrWhiteSpace(Atual(pi, x.Bip))).Select(x => x.Rotulo).ToList();
            var xw = new XlsxWriter { Numeros = false };
            xw.Add(tab);
            string path = Conferencia.Salvar(Doc, "Carimbo", xw);
            Show($"Aplicado: {nProj} campo(s) do projeto, {nFolha} campo(s) em {alvo.Count} prancha(s)" + (outros.Count > 0 ? $", {nOutros} outro(s) parâmetro(s)" : "") + "." +
                 $"\n\nAinda vazios: {comVazio} de {sheets.Count} prancha(s) com algum campo em branco" + (vaziosProj.Count > 0 ? "; no projeto: " + string.Join(", ", vaziosProj) : "") + "." +
                 "\n\n" + path + (avisos.Count > 0 ? "\n\nAvisos:\n• " + string.Join("\n• ", avisos.Take(12)) : ""),
                 avisos.Count > 0 ? ResultKind.Warning : ResultKind.Success, OpenFolderAction(Path.GetDirectoryName(path)));
            return Result.Succeeded;
        }
    }

    // =====================================================================================
    //  Comparar Versões
    // =====================================================================================

    /// <summary>
    /// Comparar Versões: opens another copy of the model (an earlier issue) in the background and
    /// lists what changed — rooms (name, area, level), doors and windows by type, sheets, levels,
    /// wall metres by type and the element count of each category.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class CompararVersoesCommand : ToolCommand
    {
        public override string Title => "Comparar Versões";

        private sealed class Retrato
        {
            public readonly Dictionary<string, Dictionary<string, string>> Secoes = new Dictionary<string, Dictionary<string, string>>();
            public readonly Dictionary<string, string> Rotulos = new Dictionary<string, string>();
            public Dictionary<string, string> S(string nome) { if (!Secoes.TryGetValue(nome, out var d)) Secoes[nome] = d = new Dictionary<string, string>(); return d; }
        }

        private static Retrato Fotografar(Document d)
        {
            var r = new Retrato();
            var amb = r.S("Ambientes");
            foreach (Room x in new FilteredElementCollector(d).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().OfType<Room>())
            {
                string k = x.UniqueId;
                amb[k] = (x.Area > 0 ? Conferencia.N(x.Area * 0.09290304) + " m²" : "não colocado") + " · " + (x.Level?.Name ?? "");
                r.Rotulos["Ambientes|" + k] = Conferencia.Nome(x);
            }
            var esq = r.S("Portas e janelas");
            foreach (var g in new FilteredElementCollector(d).WherePasses(new LogicalOrFilter(new ElementCategoryFilter(BuiltInCategory.OST_Doors), new ElementCategoryFilter(BuiltInCategory.OST_Windows)))
                         .WhereElementIsNotElementType().OfType<FamilyInstance>().GroupBy(f => (f.Category?.Name ?? "") + ": " + f.Symbol.FamilyName + " - " + f.Symbol.Name))
                esq[g.Key] = g.Count() + " un";
            var pr = r.S("Pranchas");
            foreach (ViewSheet s in new FilteredElementCollector(d).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => !s.IsPlaceholder))
            {
                string rev = s.get_Parameter(BuiltInParameter.SHEET_CURRENT_REVISION)?.AsString() ?? "";
                int vps = s.GetAllPlacedViews().Count;
                pr[s.SheetNumber] = s.Name + (rev.Length > 0 ? " · rev. " + rev : "") + " · " + vps + " vista(s)";
            }
            var nv = r.S("Níveis");
            foreach (Level l in new FilteredElementCollector(d).OfClass(typeof(Level)).Cast<Level>()) nv[l.Name] = Conferencia.N(l.Elevation * 0.3048, "0.000") + " m";
            var pa = r.S("Paredes por tipo");
            foreach (var g in new FilteredElementCollector(d).OfClass(typeof(Wall)).Cast<Wall>().Where(w => w.Location is LocationCurve).GroupBy(w => w.WallType?.Name ?? "?"))
                pa[g.Key] = Conferencia.N(g.Sum(w => ((LocationCurve)w.Location).Curve.Length) * 0.3048, "0.0") + " m";
            var cat = r.S("Categorias");
            foreach (var g in new FilteredElementCollector(d).WhereElementIsNotElementType().Where(e => e.Category != null && e.Category.CategoryType == CategoryType.Model && e.Category.Parent == null && !(e is ElementType))
                         .GroupBy(e => e.Category.Name))
                cat[g.Key] = g.Count().ToString();
            var vi = r.S("Vistas");
            foreach (var g in new FilteredElementCollector(d).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate).GroupBy(v => v.ViewType.ToString()))
                vi[g.Key] = g.Count().ToString();
            return r;
        }

        protected override Result Run()
        {
            var ofd = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Comparar com qual versão? (o arquivo é aberto só para leitura, sem salvar)",
                Filter = "Modelos do Revit (*.rvt)|*.rvt",
                InitialDirectory = !string.IsNullOrEmpty(Doc.PathName) ? Path.GetDirectoryName(Doc.PathName) : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };
            if (ofd.ShowDialog() != true) return Result.Cancelled;
            if (!string.IsNullOrEmpty(Doc.PathName) && string.Equals(Path.GetFullPath(ofd.FileName), Path.GetFullPath(Doc.PathName), StringComparison.OrdinalIgnoreCase))
            { Show("Escolha outro arquivo: este é o modelo aberto. Salve uma cópia da versão antiga (ou use o backup .0001.rvt) e compare com ela.", ResultKind.Info); return Result.Cancelled; }

            Retrato agora = Fotografar(Doc), antes;
            Document outro = null;
            try
            {
                ModelPath mp = ModelPathUtils.ConvertUserVisiblePathToModelPath(ofd.FileName);
                var op = new OpenOptions { Audit = false };
                try
                {
                    BasicFileInfo bi = BasicFileInfo.Extract(ofd.FileName);
                    if (bi.IsWorkshared) op.DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets;
                }
                catch { }
                outro = UiApp.Application.OpenDocumentFile(mp, op);
                antes = Fotografar(outro);
            }
            catch (Exception ex) { Show("Não foi possível abrir o arquivo: " + ex.Message + "\n\nSe ele já está aberto no Revit, feche-o antes.", ResultKind.Error); return Result.Failed; }
            finally { try { outro?.Close(false); } catch { } }

            var tab = new XlsxWriter.Table { Name = "Diferenças", Title = "Comparação: " + Path.GetFileName(ofd.FileName) + " → " + (string.IsNullOrWhiteSpace(Doc.Title) ? "modelo atual" : Doc.Title) };
            tab.Rows.Add(new[] { "Seção", "Item", "Antes", "Agora", "Mudança" });
            var resumo = new List<string>();
            foreach (string sec in agora.Secoes.Keys.Union(antes.Secoes.Keys))
            {
                var a = antes.Secoes.TryGetValue(sec, out var da) ? da : new Dictionary<string, string>();
                var b = agora.Secoes.TryGetValue(sec, out var db) ? db : new Dictionary<string, string>();
                int novos = 0, removidos = 0, mudados = 0;
                foreach (string k in a.Keys.Union(b.Keys).OrderBy(x => Rotulo(agora, antes, sec, x), StringComparer.OrdinalIgnoreCase))
                {
                    bool emA = a.TryGetValue(k, out string va), emB = b.TryGetValue(k, out string vb);
                    if (emA && emB && va == vb) continue;
                    string mud = !emA ? "novo" : !emB ? "removido" : "alterado";
                    if (!emA) novos++; else if (!emB) removidos++; else mudados++;
                    string rotA = antes.Rotulos.TryGetValue(sec + "|" + k, out string ra) ? ra : null, rotB = agora.Rotulos.TryGetValue(sec + "|" + k, out string rb) ? rb : null;
                    if (rotA != null && rotB != null && rotA != rotB) { va = rotA + " · " + va; vb = rotB + " · " + vb; }
                    tab.Rows.Add(new[] { sec, Rotulo(agora, antes, sec, k), emA ? va : "", emB ? vb : "", mud });
                }
                if (novos + removidos + mudados > 0) resumo.Add($"• {sec}: {novos} novo(s), {removidos} removido(s), {mudados} alterado(s)");
            }
            var xw = new XlsxWriter { Numeros = false };
            xw.Add(tab);
            string path = Conferencia.Salvar(Doc, "Comparação de versões", xw);
            Show((resumo.Count == 0 ? "Nenhuma diferença nas seções comparadas." : "Diferenças de " + Path.GetFileName(ofd.FileName) + " para o modelo atual:\n\n" + string.Join("\n", resumo)) +
                 "\n\nAmbientes comparados pelo elemento (renomear não conta como novo); portas e janelas por tipo; pranchas pelo número.\n\n" + path,
                 ResultKind.Success, OpenFolderAction(Path.GetDirectoryName(path)));
            return Result.Succeeded;
        }

        private static string Rotulo(Retrato agora, Retrato antes, string sec, string k) =>
            agora.Rotulos.TryGetValue(sec + "|" + k, out string r) ? r : antes.Rotulos.TryGetValue(sec + "|" + k, out r) ? r : k;
    }

    // =====================================================================================
    //  Quantitativo de Pintura
    // =====================================================================================

    /// <summary>
    /// Quantitativo de Pintura: per room, the walls (perimeter × height, less the doors and windows
    /// and the rodapé) and the ceiling, the rooms with tiled walls left out of the walls; litres of
    /// paint and primer, in 18 L cans and 3,6 L gallons.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class QuantitativoPinturaCommand : ToolCommand
    {
        public override string Title => "Quantitativo de Pintura";

        protected override Result Run()
        {
            var rooms = RevitUtil.Rooms(Doc);
            if (rooms.Count == 0) { Show("O projeto não tem ambientes colocados.", ResultKind.Info); return Result.Cancelled; }
            var pre = new HashSet<ElementId>(UiDoc.Selection.GetElementIds());

            var dlg = new OptionsDialog(Title, "QuantitativoPintura", "Calcular");
            var fAmb = dlg.Multi("ambientes", "Ambientes", rooms.Select(r => Conferencia.Nome(r) + " (" + (r.Level?.Name ?? "") + ")").ToList(), pre.Count == 0, true);
            if (pre.Count > 0) for (int i = 0; i < rooms.Count; i++) fAmb.Checked[i] = pre.Contains(rooms[i].Id);
            dlg.Section("Paredes");
            var fPe = dlg.Number("pe", "Pé-direito a pintar (m; 0 = altura do ambiente)", 0, 0, 20, "Com forro, use a altura do forro.");
            var fRod = dlg.Number("rodape", "Desconto do rodapé (m)", 0.07, 0, 1, "Nos ambientes com rodapé da aba JA Famílias, usa a altura real dele; este valor vale para os demais.");
            var fAzul = dlg.Text("azulejo", "Paredes revestidas (acabamento de parede com)", "cerâmica, ceramica, porcelanato, azulejo, pastilha, revestimento", "Esses ambientes não entram nas paredes (o teto entra).");
            var fAberturas = dlg.Check("aberturas", "Descontar portas e janelas", true, "Largura × altura de cada abertura do ambiente; vãos de até 2 m² muitas vezes não se descontam no orçamento: desmarque se for o seu caso.");
            dlg.Section("Teto");
            var fTeto = dlg.Check("teto", "Pintar o teto (área do ambiente)", true);
            dlg.Section("Rendimento");
            var fDemaos = dlg.Number("demaos", "Demãos de tinta", 2, 1, 5);
            var fRendP = dlg.Number("rendP", "Rendimento da tinta, paredes (m² por litro, por demão)", 10, 1, 30, "Látex acrílico: 8 a 12 m²/L por demão (veja a lata).");
            var fRendT = dlg.Number("rendT", "Rendimento da tinta, teto (m² por litro, por demão)", 12, 1, 30);
            var fSel = dlg.Check("selador", "Selador / fundo preparador (1 demão)", true);
            var fRendS = dlg.Number("rendS", "Rendimento do selador (m² por litro)", 10, 1, 30);
            fRendS.EnabledWhen(fSel);
            var fPerda = dlg.Number("perda", "Perda (%)", 10, 0, 50);
            if (!dlg.Show(Owner)) return Result.Cancelled;

            string[] azul = Conferencia.Palavras(fAzul.Value);
            var aberturas = fAberturas.Value
                ? new FilteredElementCollector(Doc).WherePasses(new LogicalOrFilter(new ElementCategoryFilter(BuiltInCategory.OST_Doors), new ElementCategoryFilter(BuiltInCategory.OST_Windows)))
                    .WhereElementIsNotElementType().OfType<FamilyInstance>().ToList()
                : new List<FamilyInstance>();
            double k = 1 + fPerda.Value / 100;
            var rodJa = RodapesJa(Doc);
            var tab = new XlsxWriter.Table { Name = "Pintura", Title = "Quantitativo de pintura" };
            tab.Rows.Add(new[] { "Ambiente", "Nível", "Perímetro (m)", "Altura (m)", "Aberturas (m²)", "Paredes (m²)", "Teto (m²)", "Tinta paredes (L)", "Tinta teto (L)", "Selador (L)", "Observação" });
            double sP = 0, sT = 0, lP = 0, lT = 0, lS = 0;
            foreach (Room r in fAmb.SelectedIndices.Select(i => rooms[i]))
            {
                var segs = Conferencia.Segmentos(r);
                double per = segs.Sum(s => s.a.DistanceTo(s.b)) * 0.3048;
                double h = fPe.Value > 0 ? fPe.Value : r.UnboundedHeight * 0.3048;
                string acab = r.get_Parameter(BuiltInParameter.ROOM_FINISH_WALL)?.AsString() ?? "";
                bool revestida = Conferencia.Tem(acab, azul);
                double ab = aberturas.Where(fi => Conferencia.DoAmbiente(fi, r)).Sum(fi => Conferencia.Largura(fi) * Conferencia.Altura(fi) * 0.09290304);
                bool temJa = rodJa.TryGetValue(r.Id.Value, out double hRodJa);
                double paredes = revestida ? 0 : Math.Max(0, per * (h - (temJa ? hRodJa : fRod.Value)) - ab);
                double teto = fTeto.Value ? r.Area * 0.09290304 : 0;
                double litP = paredes * fDemaos.Value / fRendP.Value * k, litT = teto * fDemaos.Value / fRendT.Value * k, litS = fSel.Value ? (paredes + teto) / fRendS.Value * k : 0;
                tab.Rows.Add(new[] { Conferencia.Nome(r), r.Level?.Name ?? "", Conferencia.N(per), Conferencia.N(h), Conferencia.N(ab), Conferencia.N(paredes), Conferencia.N(teto),
                                     Conferencia.N(litP, "0.0"), Conferencia.N(litT, "0.0"), Conferencia.N(litS, "0.0"), revestida ? "paredes revestidas: " + acab : "" });
                sP += paredes; sT += teto; lP += litP; lT += litT; lS += litS;
            }
            tab.Rows.Add(new[] { "Total", "", "", "", "", Conferencia.N(sP), Conferencia.N(sT), Conferencia.N(lP, "0.0"), Conferencia.N(lT, "0.0"), Conferencia.N(lS, "0.0"), "" });
            string Latas(double l) { int latas = (int)Math.Floor(l / 18); double resto = l - latas * 18; int gal = (int)Math.Ceiling(resto / 3.6 - 1e-9); if (gal >= 5) { latas++; gal = 0; } return (latas > 0 ? latas + " lata(s) de 18 L" : "") + (latas > 0 && gal > 0 ? " + " : "") + (gal > 0 ? gal + " galão(ões) de 3,6 L" : "") + (latas == 0 && gal == 0 ? "—" : ""); }
            tab.Rows.Add(new[] { "Embalagens", "", "", "", "", "", "", Latas(lP), Latas(lT), Latas(lS), "" });
            var xw = new XlsxWriter();
            xw.Add(tab);
            string path = Conferencia.Salvar(Doc, "Pintura", xw);
            Show($"Paredes: {Conferencia.N(sP)} m² → {Conferencia.N(lP, "0.0")} L ({Latas(lP)})\nTeto: {Conferencia.N(sT)} m² → {Conferencia.N(lT, "0.0")} L ({Latas(lT)})" +
                 (fSel.Value ? $"\nSelador: {Conferencia.N(lS, "0.0")} L ({Latas(lS)})" : "") + $"\n\n{Conferencia.N(fDemaos.Value, "0")} demão(s), {Conferencia.N(fPerda.Value, "0")}% de perda.\n\n{path}",
                 ResultKind.Success, OpenFolderAction(Path.GetDirectoryName(path)));
            return Result.Succeeded;
        }

        /// <summary>Room id → height (m) of the rodapé placed by the JA Famílias tab in that room (the tallest, if more than one).</summary>
        private static Dictionary<long, double> RodapesJa(Document doc)
        {
            var r = new Dictionary<long, double>();
            foreach (FamilyInstance fi in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>())
            {
                var d = JAEsquadrias.Common.JaDados.Ler(fi);
                if (d == null || d.Tipo != "rodape" || d.Ambiente == 0 || string.IsNullOrWhiteSpace(d.Config)) continue;
                double alt;
                try { alt = (JAEsquadrias.Infrastructure.Json.Deserialize<JAEsquadrias.Commands.ConfigRodape>(d.Config)?.Altura ?? 0) / 1000.0; }
                catch { continue; }
                if (alt <= 0) continue;
                if (!r.TryGetValue(d.Ambiente, out double h) || alt > h) r[d.Ambiente] = alt;
            }
            return r;
        }
    }
}
