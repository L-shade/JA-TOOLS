using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace JAEsquadrias.Core
{
    /// <summary>
    /// Position across the wall (mm, + = towards the exterior, 0 = wall centre) written as a linear
    /// function of the wall thickness t and of the two finishes (revestimento externo rx, interno
    /// ri): C + At·t + Ax·rx + Ai·ri. Every depth of the esquadria is such a function (faces of the
    /// wall, finished faces, frame, sill...), which is what lets the family follow any wall.
    /// </summary>
    public struct Forma
    {
        public double C, At, Ax, Ai;
        public bool Fixa;   // not linear (a threshold in the layout): drawn where it is, not constrained

        public double Valor(double t, double rx, double ri) => C + At * t + Ax * rx + Ai * ri;

        /// <summary>Same dependence on t, rx and ri (only C differs): moves together.</summary>
        public bool MesmaAncora(Forma o) => Near(At, o.At) && Near(Ax, o.Ax) && Near(Ai, o.Ai);

        public string Chave => K(At) + "|" + K(Ax) + "|" + K(Ai);
        public string ChaveCompleta => Chave + "|" + Math.Round(C, 2).ToString("0.00", CultureInfo.InvariantCulture);

        private static string K(double v) => Math.Round(v * 2).ToString(CultureInfo.InvariantCulture);
        internal static bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;

        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "{0:0.##} {1:+0.##;-0.##}t {2:+0.##;-0.##}rx {3:+0.##;-0.##}ri{4}", C, At, Ax, Ai, Fixa ? " (fixa)" : "");
    }

    /// <summary>
    /// Finds the <see cref="Forma"/> of every depth the <see cref="Layout"/> produces, by building
    /// the layout again with the wall a little thicker / thinner and each finish a little bigger /
    /// smaller (central differences). Plain arithmetic, no Revit: the family generator turns the
    /// results into reference planes and constraints.
    /// </summary>
    public sealed class AnaliseProfundidade
    {
        public bool Ok;
        public string Motivo;
        public Layout Layout;
        /// <summary>Nominal values (mm): the template wall and the default finishes.</summary>
        public double T, Rx, Ri;
        public Forma[] Prisma0, Prisma1, Vazio0, Vazio1, PlantaA, PlantaB, PlantaM;
        /// <summary>Exterior face of the frame (depth 0 of the layout).</summary>
        public Forma Marco;
        public int NaoLineares;

        private const double D = 5;

        public static AnaliseProfundidade Executar(EsquadriaConfig cfg, double paredeMm)
        {
            var a = new AnaliseProfundidade { T = paredeMm, Rx = cfg.RevestimentoExtMm, Ri = cfg.RevestimentoIntMm };
            var v = new List<KeyValuePair<Layout, double>>
            {
                Build(cfg, a.T, a.Rx, a.Ri),
                Build(cfg, a.T + D, a.Rx, a.Ri), Build(cfg, a.T - D, a.Rx, a.Ri),
                Build(cfg, a.T, a.Rx + D, a.Ri), Build(cfg, a.T, a.Rx - D, a.Ri),
                Build(cfg, a.T, a.Rx, a.Ri + D), Build(cfg, a.T, a.Rx, a.Ri - D),
            };
            a.Layout = v[0].Key;
            var l0 = v[0].Key;
            if (v.Any(x => x.Key.Prismas.Count != l0.Prismas.Count || x.Key.Vazios.Count != l0.Vazios.Count || x.Key.Planta.Count != l0.Planta.Count))
            {
                a.Motivo = "o desenho da esquadria muda de forma com a espessura da parede";
                return a;
            }

            Forma F(Func<Layout, double> depth)
            {
                double[] p = v.Select(x => x.Value - depth(x.Key)).ToArray();   // position = frame offset - depth
                double at = (p[1] - p[2]) / (2 * D), ax = (p[3] - p[4]) / (2 * D), ai = (p[5] - p[6]) / (2 * D);
                bool fixa = false;
                double R(double c)
                {
                    double r = Math.Round(c * 2) / 2;
                    if (Math.Abs(r - c) > 0.02) fixa = true;
                    return r;
                }
                at = R(at); ax = R(ax); ai = R(ai);
                // second check: the one-sided differences must agree (no threshold crossed)
                if (Math.Abs((p[1] - p[0]) - (p[0] - p[2])) > 0.05 || Math.Abs((p[3] - p[0]) - (p[0] - p[4])) > 0.05 || Math.Abs((p[5] - p[0]) - (p[0] - p[6])) > 0.05)
                    fixa = true;
                if (fixa) return new Forma { C = p[0], Fixa = true };
                return new Forma { C = p[0] - at * a.T - ax * a.Rx - ai * a.Ri, At = at, Ax = ax, Ai = ai };
            }

            int n = l0.Prismas.Count;
            a.Prisma0 = new Forma[n];
            a.Prisma1 = new Forma[n];
            for (int i = 0; i < n; i++)
            {
                int k = i;
                a.Prisma0[i] = F(l => l.Prismas[k].D0);
                a.Prisma1[i] = F(l => l.Prismas[k].D1);
            }
            n = l0.Vazios.Count;
            a.Vazio0 = new Forma[n];
            a.Vazio1 = new Forma[n];
            for (int i = 0; i < n; i++)
            {
                int k = i;
                a.Vazio0[i] = F(l => l.Vazios[k].D0);
                a.Vazio1[i] = F(l => l.Vazios[k].D1);
            }
            n = l0.Planta.Count;
            a.PlantaA = new Forma[n];
            a.PlantaB = new Forma[n];
            a.PlantaM = new Forma[n];
            for (int i = 0; i < n; i++)
            {
                int k = i;
                a.PlantaA[i] = F(l => l.Planta[k].A.Y);
                a.PlantaB[i] = F(l => l.Planta[k].B.Y);
                a.PlantaM[i] = l0.Planta[k].IsArc ? F(l => l.Planta[k].Mid.Y) : a.PlantaA[i];
            }
            a.Marco = F(l => 0);
            a.NaoLineares = a.Prisma0.Concat(a.Prisma1).Concat(a.Vazio0).Concat(a.Vazio1).Concat(a.PlantaA).Concat(a.PlantaB).Concat(a.PlantaM).Count(f => f.Fixa);
            a.Ok = true;
            return a;
        }

        private static KeyValuePair<Layout, double> Build(EsquadriaConfig cfg, double t, double rx, double ri)
        {
            var c = cfg.Clone();
            c.ParedeMm = t;
            c.RevestimentoExtMm = rx;
            c.RevestimentoIntMm = ri;
            return new KeyValuePair<Layout, double>(Layout.Build(c), Layout.FrameOffsetFromCenter(c));
        }
    }
}
