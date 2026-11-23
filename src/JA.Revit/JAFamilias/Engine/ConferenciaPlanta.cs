using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using JAEsquadrias.Core;

namespace JAEsquadrias.Engine
{
    /// <summary>A plan line found in Revit: position across the wall (mm from the wall centre, + = exterior) and extent along it (mm from the left of the opening, seen from outside).</summary>
    internal sealed class SegMedido
    {
        public double Pos, X0, X1;
    }

    /// <summary>A plan line the layout says should be there (only lines parallel to the wall: they carry the depth).</summary>
    internal sealed class LinhaEsperada
    {
        public double Pos, Prof, X0, X1;
        public GrupoPlanta Grupo;
    }

    internal sealed class Desvio
    {
        public LinhaEsperada E;
        /// <summary>Position of the closest line with the same extent; null = none within 100 mm.</summary>
        public double? Pos;
        public double Delta => Pos.HasValue ? Pos.Value - E.Pos : double.NaN;
    }

    internal sealed class ResultadoConferencia
    {
        public int Esperadas, Medidas;
        public readonly List<Desvio> Desvios = new List<Desvio>();
        /// <summary>The instance is mirrored left/right (hand) or across the wall (facing).</summary>
        public bool Espelhada, Virada;
        public bool Ok => Desvios.Count == 0;
        public double MaiorDesvio => Desvios.Where(d => d.Pos.HasValue).Select(d => Math.Abs(d.Delta)).DefaultIfEmpty(0).Max();
    }

    /// <summary>
    /// Checks the 2D plan of a JA window/door: every plan line parallel to the wall (marco, folhas,
    /// vidro, soleira, verga...) must be where the <see cref="Layout"/> puts it for the real wall
    /// thickness and finishes. Used right after generating a family (in the family itself, at the
    /// template wall and at two other thicknesses) and by "Conferir Planta" in the project.
    /// A line is matched to a found line with the same extent along the wall (± 1.5 mm), so a
    /// line that moved onto the depth of another one is still caught.
    /// </summary>
    internal static class ConferenciaPlanta
    {
        public const double Tolerancia = 0.8;      // mm
        private const double TolX = 1.5;           // mm
        private const string Prefixo = "JA Planta";

        /// <summary>Lines expected for <paramref name="c"/> (ParedeMm and the finishes already set to the real values).</summary>
        public static List<LinhaEsperada> Esperadas(EsquadriaConfig c, Func<GrupoPlanta, bool> ligado)
        {
            var r = new List<LinhaEsperada>();
            if (c == null || !c.SimbolosPlanta) return r;
            Layout l = Layout.Build(c);
            double off = Layout.FrameOffsetFromCenter(c);
            var vistos = new HashSet<string>();
            foreach (TracoPlanta t in l.Planta)
            {
                if (t.IsArc || t.Grupo == GrupoPlanta.Vao) continue;
                if (Math.Abs(t.A.Y - t.B.Y) > 0.01) continue;            // across the wall or oblique: no depth to check
                if (Math.Abs(t.A.X - t.B.X) < 1.5) continue;
                if (ligado != null && !ligado(t.Grupo)) continue;
                double x0 = Math.Min(t.A.X, t.B.X), x1 = Math.Max(t.A.X, t.B.X);
                string k = Math.Round(x0, 1).ToString(CultureInfo.InvariantCulture) + "|" + Math.Round(x1, 1).ToString(CultureInfo.InvariantCulture) + "|" +
                           Math.Round(t.A.Y, 1).ToString(CultureInfo.InvariantCulture);
                if (!vistos.Add(k)) continue;
                r.Add(new LinhaEsperada { Pos = off - t.A.Y, Prof = t.A.Y, X0 = x0, X1 = x1, Grupo = t.Grupo });
            }
            return r;
        }

        /// <summary>Plan lines (JA Planta styles) of a geometry, nested instances included.</summary>
        public static void Coletar(GeometryElement ge, Document doc, Func<XYZ, double> pos, Func<XYZ, double> x, List<SegMedido> into, int nivel = 0)
        {
            if (ge == null || nivel > 6) return;
            foreach (GeometryObject go in ge)
            {
                if (go is GeometryInstance gi)
                {
                    GeometryElement inner = null;
                    try { inner = gi.GetInstanceGeometry(); } catch { }
                    Coletar(inner, doc, pos, x, into, nivel + 1);
                    continue;
                }
                if (go is Line ln && EhPlanta(doc, ln.GraphicsStyleId)) Adicionar(ln.GetEndPoint(0), ln.GetEndPoint(1), pos, x, into);
            }
        }

        public static void Adicionar(XYZ a, XYZ b, Func<XYZ, double> pos, Func<XYZ, double> x, List<SegMedido> into)
        {
            double pa = pos(a), pb = pos(b);
            if (Math.Abs(pa - pb) > 0.3) return;                       // not parallel to the wall
            double xa = x(a), xb = x(b);
            if (Math.Abs(xa - xb) < 1.5) return;
            into.Add(new SegMedido { Pos = (pa + pb) / 2, X0 = Math.Min(xa, xb), X1 = Math.Max(xa, xb) });
        }

        public static bool EhPlanta(Document doc, ElementId styleId)
        {
            if (styleId == null || styleId == ElementId.InvalidElementId) return false;
            try
            {
                string n = (doc.GetElement(styleId) as GraphicsStyle)?.GraphicsStyleCategory?.Name ?? "";
                return n.StartsWith(Prefixo, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>
        /// Matches every expected line to a found one. With <paramref name="espelhos"/> the found
        /// lines are also tried mirrored left/right (width <paramref name="largura"/>) and across
        /// the wall, and the best fit is kept (flipped instances in the project).
        /// </summary>
        public static ResultadoConferencia Comparar(List<LinhaEsperada> esperadas, List<SegMedido> medidas, double largura, bool espelhos)
        {
            ResultadoConferencia melhor = null;
            double melhorNota = double.MaxValue;
            foreach (bool mx in espelhos ? new[] { false, true } : new[] { false })
                foreach (bool my in espelhos ? new[] { false, true } : new[] { false })
                {
                    var m = medidas.Select(s => new SegMedido
                    {
                        Pos = my ? -s.Pos : s.Pos,
                        X0 = mx ? largura - s.X1 : s.X0,
                        X1 = mx ? largura - s.X0 : s.X1
                    }).ToList();
                    // project: the family origin may not be at the middle of the opening — line the
                    // drawings up along the wall by their first line (the left jamb of the frame)
                    if (espelhos && m.Count > 0 && esperadas.Count > 0)
                    {
                        double dx = esperadas.Min(e => e.X0) - m.Min(s => s.X0);
                        if (Math.Abs(dx) > 0.01 && Math.Abs(dx) < largura / 2)
                            foreach (var s in m) { s.X0 += dx; s.X1 += dx; }
                    }
                    var r = new ResultadoConferencia { Esperadas = esperadas.Count, Medidas = medidas.Count, Espelhada = mx, Virada = my };
                    double nota = 0;
                    foreach (var e in esperadas)
                    {
                        double? best = null;
                        foreach (var s in m)
                        {
                            if (Math.Abs(s.X0 - e.X0) > TolX || Math.Abs(s.X1 - e.X1) > TolX) continue;
                            if (Math.Abs(s.Pos - e.Pos) > 100) continue;
                            if (!best.HasValue || Math.Abs(s.Pos - e.Pos) < Math.Abs(best.Value - e.Pos)) best = s.Pos;
                        }
                        if (best.HasValue && Math.Abs(best.Value - e.Pos) <= Tolerancia) continue;
                        r.Desvios.Add(new Desvio { E = e, Pos = best });
                        nota += best.HasValue ? Math.Min(100, Math.Abs(best.Value - e.Pos)) : 100;
                    }
                    if (nota < melhorNota - 1e-9) { melhorNota = nota; melhor = r; }
                }
            return melhor ?? new ResultadoConferencia { Esperadas = esperadas.Count, Medidas = medidas.Count };
        }

        /// <summary>Short text, one line per deviation (at most <paramref name="max"/>).</summary>
        public static string Texto(EsquadriaConfig c, ResultadoConferencia r, int max = 6)
        {
            var sb = new StringBuilder();
            foreach (var d in r.Desvios.OrderByDescending(x => x.Pos.HasValue ? Math.Abs(x.Delta) : 1000).Take(max))
            {
                string grupo = Grupo(c, d.E.Grupo);
                string onde = $"de {N(d.E.X0)} a {N(d.E.X1)}";
                if (!d.Pos.HasValue)
                    sb.AppendLine($"{grupo}: falta a linha a {N(d.E.Prof)} mm da face externa do marco ({onde}).");
                else
                {
                    double prof = d.E.Prof - d.Delta;
                    sb.AppendLine($"{grupo}: linha a {N(d.E.Prof)} mm da face externa do marco está a {N(prof)} mm ({(d.Delta > 0 ? "−" : "+")}{N(Math.Abs(d.Delta))} mm, {onde}).");
                }
            }
            if (r.Desvios.Count > max) sb.AppendLine($"… e mais {r.Desvios.Count - max}.");
            return sb.ToString().TrimEnd();
        }

        private static string Grupo(EsquadriaConfig c, GrupoPlanta g)
        {
            string n = FamilyGenerator.NomeGrupoPlanta(c, g);
            n = n.StartsWith("Planta - ") ? n.Substring(9) : n;
            return n.Length == 0 ? n : char.ToUpper(n[0]) + n.Substring(1);
        }

        private static string N(double v) => v.ToString("0.#", CultureInfo.GetCultureInfo("pt-BR"));
    }
}
