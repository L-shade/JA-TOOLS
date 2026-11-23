using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;

namespace JAEsquadrias.Core
{
    /// <summary>
    /// Plano de corte: lays the rectangular stone pieces out on slabs (chapas) of one size, per
    /// material, by shelves (pieces sorted from the largest; each one goes on the first shelf where it
    /// fits, turned 90° if needed; a new shelf, or a new slab, when none). Not an optimal nesting —
    /// a first estimate of how many slabs to buy and how to cut them, for the marble shop to check.
    /// </summary>
    public static class PlanoCorte
    {
        public sealed class Item
        {
            public string Rotulo, Material;
            public double C, L;          // mm
            /// <summary>Real outline (mm, 0..C along X, 0..L along Y) when the piece is not a plain rectangle; null = rectangle.</summary>
            public List<P2> Forma;
            public List<List<P2>> Furos;
        }

        public sealed class Posto
        {
            public Item Item;
            public double X, Y, W, H;    // mm, on the slab
            public bool Girada;
        }

        public sealed class Chapa
        {
            public string Material;
            public readonly List<Posto> Pecas = new List<Posto>();
            internal readonly List<double[]> Prateleiras = new List<double[]>();   // y, altura, x usado
            public double Uso(double w, double h) => Pecas.Sum(p => p.W * p.H) / (w * h);
        }

        /// <param name="girar">false: every piece keeps its length along the slab length (the vein of the stone).</param>
        public static List<Chapa> Montar(IEnumerable<Item> itens, double w, double h, double serra, List<Item> naoCabem, bool girar = true)
        {
            var chapas = new List<Chapa>();
            foreach (var mat in itens.GroupBy(i => i.Material ?? ""))
            {
                var minhas = new List<Chapa>();
                foreach (Item it in mat.OrderByDescending(i => Math.Min(i.C, i.L)).ThenByDescending(i => Math.Max(i.C, i.L)))
                {
                    double a = Math.Max(it.C, it.L), b = Math.Min(it.C, it.L);
                    bool cabe = (a <= w && b <= h) || (girar && a <= h && b <= w);
                    if (!cabe) { naoCabem.Add(it); continue; }
                    if (!minhas.Any(c => Colocar(c, it, a, b, w, h, serra, girar)))
                    {
                        var nova = new Chapa { Material = mat.Key };
                        minhas.Add(nova);
                        Colocar(nova, it, a, b, w, h, serra, girar);
                    }
                }
                chapas.AddRange(minhas);
            }
            return chapas;
        }

        // long side along the slab length first; turned when that is what fits
        private static bool Colocar(Chapa c, Item it, double a, double b, double w, double h, double serra, bool girar)
        {
            var modos = girar ? new[] { (a, b, false), (b, a, true) } : new[] { (a, b, false) };
            foreach (var (pw, ph, gir) in modos)
            {
                if (pw > w || ph > h) continue;
                foreach (double[] s in c.Prateleiras)
                {
                    if (ph <= s[1] + 1e-6 && s[2] + pw <= w + 1e-6)
                    {
                        c.Pecas.Add(new Posto { Item = it, X = s[2], Y = s[0], W = pw, H = ph, Girada = gir });
                        s[2] += pw + serra;
                        return true;
                    }
                }
            }
            foreach (var (pw, ph, gir) in modos)
            {
                if (pw > w || ph > h) continue;
                double y = c.Prateleiras.Count == 0 ? 0 : c.Prateleiras.Max(s => s[0] + s[1]) + serra;
                if (y + ph > h + 1e-6) continue;
                c.Prateleiras.Add(new[] { y, ph, pw + serra });
                c.Pecas.Add(new Posto { Item = it, X = 0, Y = y, W = pw, H = ph, Girada = gir });
                return true;
            }
            return false;
        }

        /// <summary>One self-contained HTML page: one drawing per slab, with the piece labels and sizes.</summary>
        public static string Html(string titulo, List<Chapa> chapas, double w, double h, double serra, List<Item> naoCabem)
        {
            var br = CultureInfo.GetCultureInfo("pt-BR");
            var inv = CultureInfo.InvariantCulture;
            string E(string s) => WebUtility.HtmlEncode(s ?? "");
            string F(double v) => v.ToString("0.#", inv);
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html><html lang=\"pt-BR\"><head><meta charset=\"utf-8\"><title>").Append(E(titulo)).Append("</title><style>")
              .Append("body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#1d2b26}h1{font-size:20px;margin:0 0 4px}h2{font-size:15px;margin:22px 0 6px}")
              .Append(".m{color:#5b6b65;font-size:13px}svg{width:100%;max-width:1100px;height:auto;border:1px solid #c9d3cf;background:#f7f9f8}")
              .Append("table{border-collapse:collapse;font-size:12px;margin-top:6px}td,th{border:1px solid #d5ddd9;padding:3px 8px;text-align:left}th{background:#1f7a5a;color:#fff}")
              .Append("@media print{h2{page-break-before:always}h2:first-of-type{page-break-before:auto}}</style></head><body>");
            sb.Append("<h1>").Append(E(titulo)).Append("</h1><div class=\"m\">Chapa ").Append(w.ToString("0", br)).Append(" × ").Append(h.ToString("0", br))
              .Append(" mm · serra ").Append(serra.ToString("0.#", br)).Append(" mm · ").Append(chapas.Count).Append(" chapa(s). Estimativa por prateleiras: confira com a marmoraria (veios, emendas e acabamento de borda).</div>");
            foreach (var mat in chapas.GroupBy(c => c.Material))
            {
                int n = 0, total = mat.Count();
                foreach (Chapa c in mat)
                {
                    n++;
                    sb.Append("<h2>").Append(E(string.IsNullOrWhiteSpace(mat.Key) ? "Pedra" : mat.Key)).Append(" — chapa ").Append(n).Append(" de ").Append(total)
                      .Append(" <span class=\"m\">(aproveitamento ").Append((c.Uso(w, h) * 100).ToString("0", br)).Append("%)</span></h2>");
                    sb.Append("<svg viewBox=\"-20 -20 ").Append(F(w + 40)).Append(' ').Append(F(h + 40)).Append("\" xmlns=\"http://www.w3.org/2000/svg\">");
                    sb.Append("<rect x=\"0\" y=\"0\" width=\"").Append(F(w)).Append("\" height=\"").Append(F(h)).Append("\" fill=\"#e9eeec\" stroke=\"#7d8c86\" stroke-width=\"4\"/>");
                    int k = 0;
                    foreach (Posto p in c.Pecas)
                    {
                        k++;
                        double fs = Math.Max(22, Math.Min(70, Math.Min(p.W / 8, p.H / 3)));
                        if (p.Item.Forma != null && p.Item.Forma.Count >= 3)
                        {
                            // the real outline (and its cut-outs) inside the piece's rectangle
                            string Pts(IEnumerable<P2> l) => string.Join(" ", l.Select(q => p.Girada ? F(p.X + q.Y) + "," + F(p.Y + q.X) : F(p.X + q.X) + "," + F(p.Y + q.Y)));
                            sb.Append("<rect x=\"").Append(F(p.X)).Append("\" y=\"").Append(F(p.Y)).Append("\" width=\"").Append(F(p.W)).Append("\" height=\"").Append(F(p.H))
                              .Append("\" fill=\"none\" stroke=\"#9bb5aa\" stroke-width=\"2\" stroke-dasharray=\"12 8\"/>");
                            sb.Append("<polygon points=\"").Append(Pts(p.Item.Forma)).Append("\" fill=\"#cfe3da\" stroke=\"#1f7a5a\" stroke-width=\"3\"/>");
                            foreach (var furo in p.Item.Furos ?? new List<List<P2>>())
                                if (furo.Count >= 3) sb.Append("<polygon points=\"").Append(Pts(furo)).Append("\" fill=\"#e9eeec\" stroke=\"#1f7a5a\" stroke-width=\"2\"/>");
                        }
                        else
                            sb.Append("<rect x=\"").Append(F(p.X)).Append("\" y=\"").Append(F(p.Y)).Append("\" width=\"").Append(F(p.W)).Append("\" height=\"").Append(F(p.H))
                              .Append("\" fill=\"#cfe3da\" stroke=\"#1f7a5a\" stroke-width=\"3\"/>");
                        sb.Append("<text x=\"").Append(F(p.X + p.W / 2)).Append("\" y=\"").Append(F(p.Y + p.H / 2)).Append("\" font-size=\"").Append(F(fs))
                          .Append("\" text-anchor=\"middle\" dominant-baseline=\"middle\" fill=\"#123\">").Append(k).Append("</text>");
                    }
                    sb.Append("</svg><table><tr><th>#</th><th>Peça</th><th>Medida (mm)</th><th>Na chapa</th></tr>");
                    k = 0;
                    foreach (Posto p in c.Pecas)
                    {
                        k++;
                        sb.Append("<tr><td>").Append(k).Append("</td><td>").Append(E(p.Item.Rotulo)).Append("</td><td>")
                          .Append(p.Item.C.ToString("0", br)).Append(" × ").Append(p.Item.L.ToString("0", br)).Append("</td><td>")
                          .Append(p.Girada ? "girada 90°" : "").Append("</td></tr>");
                    }
                    sb.Append("</table>");
                }
            }
            if (naoCabem.Count > 0)
            {
                sb.Append("<h2>Não cabem na chapa</h2><table><tr><th>Peça</th><th>Medida (mm)</th></tr>");
                foreach (Item it in naoCabem)
                    sb.Append("<tr><td>").Append(E(it.Rotulo)).Append("</td><td>").Append(it.C.ToString("0", br)).Append(" × ").Append(it.L.ToString("0", br)).Append("</td></tr>");
                sb.Append("</table>");
            }
            sb.Append("</body></html>");
            return sb.ToString();
        }
    }
}
