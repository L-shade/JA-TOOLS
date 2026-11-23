using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;

namespace JAEsquadrias.Core
{
    public sealed class ReportItem
    {
        public EsquadriaConfig Config;
        public int Quantidade;
    }

    /// <summary>
    /// "Relatório de Esquadrias": one printable HTML page (A4, two esquadrias per page) with the
    /// elevation of each type drawn as SVG from its Layout, and its technical data.
    /// </summary>
    public static class ReportHtml
    {
        private static readonly CultureInfo Pt = new CultureInfo("pt-BR");

        public static string Build(string projectName, IList<ReportItem> items, string fabricante)
        {
            var sb = new StringBuilder();
            sb.Append(@"<!doctype html><html lang=""pt-BR""><head><meta charset=""utf-8"">
<title>Esquadrias — ").Append(H(projectName)).Append(@"</title>
<style>
:root{--ink:#1E2A38;--text:#2B3440;--muted:#6B7685;--line:#DDE2E8;--accent:#1F6E8C;--soft:#E7F0F4}
*{box-sizing:border-box}
body{margin:0;font:13px/1.5 'Segoe UI',system-ui,sans-serif;color:var(--text);background:#F5F7FA}
header{background:#0C3442;color:#fff;padding:22px 32px}
header h1{margin:0;font-size:22px;font-weight:600}
header p{margin:4px 0 0;color:#C9D3DF}
.bar{display:flex;gap:10px;align-items:center;padding:12px 32px;background:#fff;border-bottom:1px solid var(--line)}
.bar button{font:inherit;padding:7px 16px;border-radius:6px;border:0;background:var(--accent);color:#fff;font-weight:600;cursor:pointer}
.bar span{color:var(--muted)}
main{max-width:1000px;margin:0 auto;padding:20px 32px 60px}
.sum{width:100%;border-collapse:collapse;background:#fff;border:1px solid var(--line);border-radius:8px;overflow:hidden;margin-bottom:22px}
.sum th{background:var(--soft);color:var(--ink);text-align:left;font-size:11.5px;letter-spacing:.03em;text-transform:uppercase;padding:7px 10px}
.sum td{padding:6px 10px;border-top:1px solid var(--line)}
.card{display:flex;gap:22px;background:#fff;border:1px solid var(--line);border-radius:10px;padding:18px 20px;margin-bottom:16px;page-break-inside:avoid}
.card .draw{flex:0 0 360px}
.card .draw svg{width:360px;height:300px}.card .draw svg.plan{height:150px;margin-top:6px}
.card h2{margin:0 0 2px;font-size:20px;color:var(--ink)}
.card .desc{color:var(--muted);margin:0 0 10px}
.card table{border-collapse:collapse;width:100%}
.card td{padding:3px 0;border-bottom:1px dotted var(--line);vertical-align:top}
.card td:first-child{color:var(--muted);width:42%}
.tag{display:inline-block;background:var(--accent);color:#fff;border-radius:5px;padding:1px 9px;font-weight:700;margin-right:8px}
.note{color:var(--muted);font-size:11.5px;margin-top:24px}
@media print{body{background:#fff}.bar{display:none}header{-webkit-print-color-adjust:exact;print-color-adjust:exact}main{padding:10px 0}.card{border-color:#ccc}@page{size:A4;margin:12mm}}
</style></head><body>");
            sb.Append("<header><h1>Quadro e especificação de esquadrias</h1><p>")
              .Append(H(projectName)).Append(" · ").Append(DateTime.Now.ToString("dd/MM/yyyy", Pt))
              .Append(" · ").Append(items.Count).Append(" tipo(s), ").Append(items.Sum(i => i.Quantidade)).Append(" unidade(s)</p></header>");
            sb.Append("<div class=\"bar\"><button onclick=\"window.print()\">Imprimir / salvar PDF</button><span>Gerado pelo JA Famílias.</span></div><main>");

            // summary table
            sb.Append("<table class=\"sum\"><tr><th>Código</th><th>Tipo</th><th>Abertura</th><th>L × A (mm)</th><th>Peitoril</th><th>Soleira / pedra</th><th>Qtd.</th><th>Uw</th></tr>");
            foreach (var it in items)
            {
                var c = it.Config;
                var l = Layout.Build(c);
                sb.Append("<tr><td><b>").Append(H(c.Codigo)).Append("</b></td><td>").Append(c.IsPorta ? "Porta" : "Janela")
                  .Append("</td><td>").Append(H(Layout.ResumoAberturas(c))).Append("</td><td>")
                  .Append(Layout.Mm(c.LarguraMm)).Append(" × ").Append(Layout.Mm(c.AlturaMm)).Append("</td><td>")
                  .Append(c.IsPorta ? "—" : Layout.Mm(c.PeitorilMm)).Append("</td><td>")
                  .Append(H(l.Pedra.HasValue ? (c.IsPorta ? c.SoleiraCodigo : c.PeitorilCodigo) : "—")).Append("</td><td>").Append(it.Quantidade)
                  .Append("</td><td>").Append(l.Uw.ToString("0.0", Pt)).Append("</td></tr>");
            }
            sb.Append("</table>");

            foreach (var it in items)
            {
                var c = it.Config;
                var l = Layout.Build(c);
                var perfil = Catalog.Perfil(c.PerfilId);
                var vidro = Catalog.Vidro(c.VidroId);
                var cor = Catalog.Cor(c.CorId);
                sb.Append("<div class=\"card\"><div class=\"draw\">").Append(Svg(c, l)).Append(PlanSvg(l)).Append("</div><div style=\"flex:1\">");
                sb.Append("<h2><span class=\"tag\">").Append(H(c.Codigo)).Append("</span>").Append(c.IsPorta ? "Porta" : "Janela").Append("</h2>");
                sb.Append("<p class=\"desc\">").Append(H(Layout.Descricao(c))).Append("</p><table>");
                Row(sb, "Quantidade no projeto", it.Quantidade.ToString(Pt));
                Row(sb, "Vão (L × A)", Layout.Mm(c.LarguraMm) + " × " + Layout.Mm(c.AlturaMm) + " mm");
                if (!c.IsPorta) Row(sb, "Peitoril", Layout.Mm(c.PeitorilMm) + " mm");
                Row(sb, "Abertura", Layout.ResumoAberturas(c));
                Row(sb, "Linha de perfis", perfil.Nome + " (marco " + Layout.Mm(c.MarcoFaceMm) + " × " + Layout.Mm(c.MarcoProfMm) + " mm)");
                Row(sb, "Cor", cor != null && string.Equals(cor.Hex, c.CorHex, StringComparison.OrdinalIgnoreCase) ? cor.Nome : "#" + c.CorHex);
                if (Layout.UsesGlass(c)) Row(sb, "Vidro", vidro.Nome);
                Row(sb, "Folhas", l.NumFolhas.ToString(Pt));
                Row(sb, "Área de iluminação", l.AreaIluminacao.ToString("0.00", Pt) + " m²");
                Row(sb, "Área de ventilação (est.)", l.AreaVentilacao.ToString("0.00", Pt) + " m²");
                Row(sb, "Transmitância Uw (est.)", l.Uw.ToString("0.0", Pt) + " W/m²K");
                Row(sb, "Posição na parede", Catalog.NomePosicao(c.Posicao));
                if (l.Pedra.HasValue)
                {
                    var pd = l.Pedra.Value;
                    string nome = c.IsPorta ? c.NomeSoleira : "Peitoril";
                    string cod = c.IsPorta ? c.SoleiraCodigo : c.PeitorilCodigo;
                    string mat = c.IsPorta ? c.SoleiraMaterial : c.PeitorilMaterial;
                    double esp = c.IsPorta ? c.SoleiraEspessuraMm : c.PeitorilEspessuraMm;
                    Row(sb, nome, cod + " · " + mat + " · " + Layout.Mm(pd.W) + " × " + Layout.Mm(pd.H) + " × " + Layout.Mm(esp) + " mm");
                }
                if (!string.IsNullOrWhiteSpace(fabricante)) Row(sb, "Fabricante (família)", fabricante);
                sb.Append("</table></div></div>");
            }
            sb.Append("<p class=\"note\">Elevações vistas pelo lado externo; linhas tracejadas = folha abre para dentro. ")
              .Append("Uw estimado pela EN ISO 10077-1 com valores típicos de perfis e vidros; áreas de ventilação estimadas pelo tipo de abertura. ")
              .Append("Confirme com o fabricante antes da compra.</p></main></body></html>");
            return sb.ToString();
        }

        private static void Row(StringBuilder sb, string k, string v) =>
            sb.Append("<tr><td>").Append(H(k)).Append("</td><td>").Append(H(v)).Append("</td></tr>");

        private static string H(string s) => WebUtility.HtmlEncode(s ?? "");

        private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        /// <summary>Plan (2D representation) as SVG, interior up.</summary>
        public static string PlanSvg(Layout l)
        {
            var lines = l.Planta.Where(t => t.Visivel).ToList();
            if (lines.Count == 0) return "";
            const double vw = 360, vh = 150, m = 12;
            double stub = Math.Max(150, l.W * 0.12);
            double dMin = Math.Min(l.FaceExt, lines.Min(t => Math.Min(t.A.Y, t.B.Y))) - 30;
            double dMax = Math.Max(l.FaceInt, lines.Max(t => Math.Max(Math.Max(t.A.Y, t.B.Y), t.IsArc ? t.Mid.Y : double.MinValue))) + 30;
            double s = Math.Min((vw - 2 * m) / (l.W + 2 * stub), (vh - 2 * m) / (dMax - dMin));
            double ox = (vw - l.W * s) / 2, oy = (vh + (dMax - dMin) * s) / 2 + dMin * s;
            Func<double, double> X = x => ox + x * s;
            Func<double, double> Y = d => oy - d * s;
            var sb = new StringBuilder();
            sb.Append($"<svg class=\"plan\" viewBox=\"0 0 {F(vw)} {F(vh)}\" xmlns=\"http://www.w3.org/2000/svg\">");
            foreach (bool left in new[] { true, false })
            {
                double x0 = left ? -stub : l.W, x1 = left ? 0 : l.W + stub;
                sb.Append($"<rect x=\"{F(X(x0))}\" y=\"{F(Y(l.ParedeInt))}\" width=\"{F((x1 - x0) * s)}\" height=\"{F((l.ParedeInt - l.ParedeExt) * s)}\" fill=\"#ECE8E2\" stroke=\"#C9C2B8\" stroke-width=\"0.6\"/>");
            }
            foreach (var t in lines)
            {
                // "w" closes the stroke-width attribute itself, so the dash attribute can follow
                string w = (t.Fino ? "0.5" : "0.9") + (t.Tracejado ? "\" stroke-dasharray=\"4 3" : "");
                if (t.IsArc)
                {
                    // radius of the circle through A, Mid, B
                    double ax = t.A.X, ay = t.A.Y, bx = t.Mid.X, by = t.Mid.Y, cx = t.B.X, cy = t.B.Y;
                    double dd = 2 * (ax * (by - cy) + bx * (cy - ay) + cx * (ay - by));
                    double r = Math.Sqrt(Math.Pow(t.A.X - t.B.X, 2) + Math.Pow(t.A.Y - t.B.Y, 2)) / Math.Sqrt(2) * s;
                    if (Math.Abs(dd) > 1e-9)
                    {
                        double ux = ((ax * ax + ay * ay) * (by - cy) + (bx * bx + by * by) * (cy - ay) + (cx * cx + cy * cy) * (ay - by)) / dd;
                        double uy = ((ax * ax + ay * ay) * (cx - bx) + (bx * bx + by * by) * (ax - cx) + (cx * cx + cy * cy) * (bx - ax)) / dd;
                        r = Math.Sqrt((ax - ux) * (ax - ux) + (ay - uy) * (ay - uy)) * s;
                    }
                    // quarter arcs: sweep chosen from the side the middle point is on
                    double cross = (X(t.Mid.X) - X(t.A.X)) * (Y(t.B.Y) - Y(t.A.Y)) - (Y(t.Mid.Y) - Y(t.A.Y)) * (X(t.B.X) - X(t.A.X));
                    string sweep = cross > 0 ? "1" : "0";
                    sb.Append($"<path d=\"M{F(X(t.A.X))} {F(Y(t.A.Y))} A{F(r)} {F(r)} 0 0 {sweep} {F(X(t.B.X))} {F(Y(t.B.Y))}\" fill=\"none\" stroke=\"#1E2A38\" stroke-width=\"{w}\"/>");
                }
                else
                    sb.Append($"<line x1=\"{F(X(t.A.X))}\" y1=\"{F(Y(t.A.Y))}\" x2=\"{F(X(t.B.X))}\" y2=\"{F(Y(t.B.Y))}\" stroke=\"#1E2A38\" stroke-width=\"{w}\"/>");
            }
            sb.Append("</svg>");
            return sb.ToString();
        }

        /// <summary>Elevation as SVG (360 × 300), with overall dimensions.</summary>
        public static string Svg(EsquadriaConfig c, Layout l)
        {
            const double vw = 360, vh = 300, m = 34;
            double below = !c.IsPorta && c.Peitoril ? c.PeitorilEspessuraMm : 0;
            double s = Math.Min((vw - 2 * m) / (l.W + 60), (vh - 2 * m) / (l.H + below));
            double ox = (vw - l.W * s) / 2, oy = m + (vh - 2 * m - (l.H + below) * s) / 2 + l.H * s;
            Func<double, double> X = x => ox + x * s;
            Func<double, double> Y = y => oy - y * s;

            string profile = "#" + c.CorHex;
            var vidro = Catalog.Vidro(c.VidroId);
            var sb = new StringBuilder();
            sb.Append($"<svg viewBox=\"0 0 {F(vw)} {F(vh)}\" xmlns=\"http://www.w3.org/2000/svg\" font-family=\"Segoe UI, sans-serif\">");
            if (c.IsPorta)
                sb.Append($"<line x1=\"{F(X(-60))}\" y1=\"{F(Y(0))}\" x2=\"{F(X(l.W + 60))}\" y2=\"{F(Y(0))}\" stroke=\"#1E2A38\" stroke-width=\"1.4\"/>");
            foreach (var p in l.Prismas.Where(p => p.Papel != Papel.Soleira && (p.Papel == Papel.Puxador ? c.IsPorta : p.Medium)).OrderByDescending(p => p.D0))
            {
                string fill, opacity = "1";
                switch (p.Material)
                {
                    case MaterialSlot.Vidro: fill = "#" + vidro.CorHex; opacity = "0.8"; break;
                    case MaterialSlot.Peitoril: fill = "#D8D3CA"; break;
                    default: fill = profile; break;
                }
                var d = new StringBuilder();
                foreach (var loop in p.Loops)
                {
                    for (int i = 0; i < loop.Count; i++) d.Append(i == 0 ? "M" : "L").Append(F(X(loop[i].X))).Append(' ').Append(F(Y(loop[i].Y))).Append(' ');
                    d.Append("Z ");
                }
                sb.Append($"<path d=\"{d}\" fill=\"{fill}\" fill-opacity=\"{opacity}\" fill-rule=\"evenodd\" stroke=\"#5C6570\" stroke-width=\"0.6\"/>");
            }
            const string dash = " stroke-dasharray=\"4 3\"";
            foreach (var t in l.Simbolos)
            {
                string style = t.Tracejado ? dash : "";
                sb.Append($"<line x1=\"{F(X(t.A.X))}\" y1=\"{F(Y(t.A.Y))}\" x2=\"{F(X(t.B.X))}\" y2=\"{F(Y(t.B.Y))}\" stroke=\"#1E2A38\" stroke-width=\"0.8\"{style}/>");
            }

            // overall dimensions
            double yb = Y(-below) + 16, xr = X(l.W) + 16;
            sb.Append($"<g stroke=\"#6B7685\" stroke-width=\"0.8\"><line x1=\"{F(X(0))}\" y1=\"{F(yb)}\" x2=\"{F(X(l.W))}\" y2=\"{F(yb)}\"/>")
              .Append($"<line x1=\"{F(X(0))}\" y1=\"{F(yb - 6)}\" x2=\"{F(X(0))}\" y2=\"{F(yb + 3)}\"/><line x1=\"{F(X(l.W))}\" y1=\"{F(yb - 6)}\" x2=\"{F(X(l.W))}\" y2=\"{F(yb + 3)}\"/>")
              .Append($"<line x1=\"{F(xr)}\" y1=\"{F(Y(0))}\" x2=\"{F(xr)}\" y2=\"{F(Y(l.H))}\"/>")
              .Append($"<line x1=\"{F(xr - 3)}\" y1=\"{F(Y(0))}\" x2=\"{F(xr + 6)}\" y2=\"{F(Y(0))}\"/><line x1=\"{F(xr - 3)}\" y1=\"{F(Y(l.H))}\" x2=\"{F(xr + 6)}\" y2=\"{F(Y(l.H))}\"/></g>");
            foreach (var pt in new[] { (X(0), yb), (X(l.W), yb), (xr, Y(0)), (xr, Y(l.H)) })
                sb.Append($"<circle cx=\"{F(pt.Item1)}\" cy=\"{F(pt.Item2)}\" r=\"2.2\" fill=\"#1E2A38\"/>");
            sb.Append($"<text x=\"{F((X(0) + X(l.W)) / 2)}\" y=\"{F(yb - 4)}\" font-size=\"11\" text-anchor=\"middle\" fill=\"#1E2A38\">{Layout.Mm(l.W)}</text>");
            double ym = (Y(0) + Y(l.H)) / 2;
            sb.Append($"<text x=\"{F(xr - 4)}\" y=\"{F(ym)}\" font-size=\"11\" text-anchor=\"middle\" fill=\"#1E2A38\" transform=\"rotate(-90 {F(xr - 4)} {F(ym)})\">{Layout.Mm(l.H)}</text>");
            sb.Append("</svg>");
            return sb.ToString();
        }
    }
}
