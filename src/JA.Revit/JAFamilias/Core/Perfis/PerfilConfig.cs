using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization;
using JAEsquadrias.Infrastructure;

namespace JAEsquadrias.Core.Perfis
{
    public enum FormaPerfil
    {
        Retangular = 0,       // barra maciça, cantos retos / chanfrados / boleados
        TuboRetangular = 1,
        TuboRedondo = 2,
        I = 3,
        T = 4,
        U = 5,
        L = 6,
        ComTampa = 7,         // corpo interno + haste no vão do vidro + tampa de pressão externa
        Canto = 8             // montante de canto do Revit (V = meia esquadria, L)
    }

    public enum CantoPerfil { Reto = 0, Chanfrado = 1, Boleado = 2 }
    public enum TipoCanto { V = 0, L = 1 }

    /// <summary>
    /// A curtain-wall mullion (montante / travessa) section. Local coordinates in mm:
    /// X across the mullion, along the curtain wall (the grid line is X = 0);
    /// Y through the wall, positive = EXTERIOR; Y = 0 is the panel plane (Revit trims the panels
    /// where the profile crosses it). The family template has the exterior below the centre
    /// plane, so the generator mirrors Y.
    /// </summary>
    [DataContract]
    public sealed class PerfilConfig
    {
        [DataMember(Order = 1)] public string Codigo { get; set; } = "MT01";
        [DataMember(Order = 2)] public string Nome { get; set; } = "";
        [DataMember(Order = 3)] public FormaPerfil Forma { get; set; } = FormaPerfil.Retangular;

        /// <summary>Across the mullion (along the wall). Round tube: diameter.</summary>
        [DataMember(Order = 10)] public double LarguraMm { get; set; } = 50;
        /// <summary>Through the wall (interior → exterior). With tampa: depth of the body only.</summary>
        [DataMember(Order = 11)] public double ProfundidadeMm { get; set; } = 150;
        /// <summary>Centre of the section from the panel plane (+ = towards the exterior).</summary>
        [DataMember(Order = 12)] public double DeslocamentoMm { get; set; } = 0;

        /// <summary>Wall thickness of tubes and of U / L sections.</summary>
        [DataMember(Order = 20)] public double ChapaMm { get; set; } = 3;
        /// <summary>I / T: flange thickness.</summary>
        [DataMember(Order = 21)] public double AbaMm { get; set; } = 8;
        /// <summary>I / T: web thickness.</summary>
        [DataMember(Order = 22)] public double AlmaMm { get; set; } = 6;
        /// <summary>T / U / L: the flange (or the closed side) is on the exterior face.</summary>
        [DataMember(Order = 23)] public bool AbaParaFora { get; set; } = true;
        /// <summary>L: the vertical leg on the right instead of the left.</summary>
        [DataMember(Order = 24)] public bool Espelhar { get; set; }
        /// <summary>Tubes and the tampa body: hollow (second loop).</summary>
        [DataMember(Order = 25)] public bool Oco { get; set; } = true;

        [DataMember(Order = 30)] public CantoPerfil Cantos { get; set; } = CantoPerfil.Reto;
        [DataMember(Order = 31)] public double CantoMm { get; set; } = 5;
        /// <summary>Only the two corners of the exterior face are chamfered / rounded.</summary>
        [DataMember(Order = 32)] public bool SoFaceExterna { get; set; }

        // tampa de pressão (pressure-plate system)
        [DataMember(Order = 40)] public double TampaLarguraMm { get; set; } = 60;
        [DataMember(Order = 41)] public double TampaProfundidadeMm { get; set; } = 20;
        /// <summary>Glass + gaskets: gap between the body and the cap, centred on the panel plane.</summary>
        [DataMember(Order = 42)] public double VaoVidroMm { get; set; } = 28;
        /// <summary>Width of the stem (screw boss) that crosses the glass gap.</summary>
        [DataMember(Order = 43)] public double HasteMm { get; set; } = 10;

        // canto (system corner mullion)
        [DataMember(Order = 50)] public TipoCanto TipoCanto { get; set; } = TipoCanto.V;
        [DataMember(Order = 51)] public double Perna1Mm { get; set; } = 100;
        [DataMember(Order = 52)] public double Perna2Mm { get; set; } = 100;
        [DataMember(Order = 53)] public double EspessuraCantoMm { get; set; } = 50;

        [DataMember(Order = 60)] public string Material { get; set; } = "Alumínio anodizado natural";
        [DataMember(Order = 61)] public string CorHex { get; set; } = "B8BCC0";
        /// <summary>Mirror interior / exterior (if a template draws the exterior on the other side).</summary>
        [DataMember(Order = 62)] public bool InverterLado { get; set; }

        public bool EhCanto => Forma == FormaPerfil.Canto;

        public PerfilConfig Normalized()
        {
            if (string.IsNullOrWhiteSpace(Codigo)) Codigo = "MT01";
            if (Nome == null) Nome = "";
            LarguraMm = C(LarguraMm, 5, 1000, 50);
            ProfundidadeMm = C(ProfundidadeMm, 5, 2000, 150);
            DeslocamentoMm = Clamp(DeslocamentoMm, -1000, 1000);
            ChapaMm = C(ChapaMm, 0.5, 100, 3);
            AbaMm = C(AbaMm, 0.5, 200, 8);
            AlmaMm = C(AlmaMm, 0.5, 200, 6);
            CantoMm = Clamp(CantoMm, 0, 200);
            TampaLarguraMm = C(TampaLarguraMm, 5, 1000, 60);
            TampaProfundidadeMm = C(TampaProfundidadeMm, 2, 500, 20);
            VaoVidroMm = C(VaoVidroMm, 0, 200, 28);
            HasteMm = C(HasteMm, 1, 200, 10);
            Perna1Mm = C(Perna1Mm, 5, 2000, 100);
            Perna2Mm = C(Perna2Mm, 5, 2000, 100);
            EspessuraCantoMm = C(EspessuraCantoMm, 1, 1000, 50);
            if (string.IsNullOrWhiteSpace(Material)) Material = "Alumínio anodizado natural";
            if (string.IsNullOrWhiteSpace(CorHex)) CorHex = "B8BCC0";
            return this;
        }

        private static double Clamp(double v, double a, double b) => double.IsNaN(v) ? a : Math.Max(a, Math.Min(b, v));
        private static double C(double v, double a, double b, double d) => v <= 0 || double.IsNaN(v) ? d : Clamp(v, a, b);

        public string ToJson() => Json.Serialize(this);
        public PerfilConfig Copiar() => Json.Deserialize<PerfilConfig>(ToJson()).Normalized();
        public static PerfilConfig FromJson(string json)
        {
            try { return string.IsNullOrWhiteSpace(json) ? null : Json.Deserialize<PerfilConfig>(json)?.Normalized(); }
            catch { return null; }
        }

        // ------------------------------------------------------------------ texts

        public static string NomeForma(FormaPerfil f)
        {
            switch (f)
            {
                case FormaPerfil.Retangular: return "Retangular";
                case FormaPerfil.TuboRetangular: return "Tubo retangular";
                case FormaPerfil.TuboRedondo: return "Tubo redondo";
                case FormaPerfil.I: return "Perfil I";
                case FormaPerfil.T: return "Perfil T";
                case FormaPerfil.U: return "Perfil U";
                case FormaPerfil.L: return "Perfil L";
                case FormaPerfil.ComTampa: return "Com tampa";
                default: return "Canto (meia esquadria)";
            }
        }

        private static string N(double v) => v.ToString("0.#", CultureInfo.GetCultureInfo("pt-BR"));

        /// <summary>"RET 50x150 CH5" — short, for the type name.</summary>
        public string Sigla()
        {
            string d = $"{N(LarguraMm)}x{N(ProfundidadeMm)}";
            string c = Cantos == CantoPerfil.Chanfrado && CantoMm > 0 ? " CH" + N(CantoMm) : Cantos == CantoPerfil.Boleado && CantoMm > 0 ? " R" + N(CantoMm) : "";
            switch (Forma)
            {
                case FormaPerfil.Retangular: return "RET " + d + c;
                case FormaPerfil.TuboRetangular: return "TUBO " + d + "x" + N(ChapaMm) + c;
                case FormaPerfil.TuboRedondo: return "TUBO D" + N(LarguraMm) + "x" + N(ChapaMm);
                case FormaPerfil.I: return "I " + d;
                case FormaPerfil.T: return "T " + d;
                case FormaPerfil.U: return "U " + d + "x" + N(ChapaMm);
                case FormaPerfil.L: return "L " + d + "x" + N(ChapaMm);
                case FormaPerfil.ComTampa: return "TAMPA " + d + "+" + N(TampaLarguraMm) + "x" + N(TampaProfundidadeMm);
                default: return (TipoCanto == TipoCanto.V ? "CANTO V " : "CANTO L ") + N(Perna1Mm) + "x" + N(Perna2Mm) + "x" + N(EspessuraCantoMm);
            }
        }

        public string NomeTipo => "JA " + Codigo + " - " + Sigla();
        public string NomeFamilia => "JA Perfil " + Codigo;

        public string Descricao()
        {
            if (EhCanto)
                return $"Montante de canto em {(TipoCanto == TipoCanto.V ? "V (meia esquadria)" : "L")}: pernas {N(Perna1Mm)} e {N(Perna2Mm)} mm, espessura {N(EspessuraCantoMm)} mm · {Material}";
            var g = Build();
            string cantos = Cantos == CantoPerfil.Reto || CantoMm <= 0 ? "" :
                $", cantos {(Cantos == CantoPerfil.Chanfrado ? "chanfrados" : "boleados")} {N(CantoMm)} mm" + (SoFaceExterna ? " (face externa)" : "");
            return $"{NomeForma(Forma)} {Sigla()}{cantos} · {N(g.AreaMm2 / 100)} cm² · ≈ {N(g.AreaMm2 * 2.7 / 1000)} kg/m em alumínio · {Material}";
        }

        // ------------------------------------------------------------------ geometry

        /// <summary>The section (closed loops, mm). Corner mullions have no profile: a preview only.</summary>
        public PerfilGeom Build()
        {
            var c = Copiar();
            var g = new PerfilGeom();
            double w = c.LarguraMm, d = c.ProfundidadeMm, off = c.DeslocamentoMm, t = c.ChapaMm;
            double y0 = off - d / 2, y1 = off + d / 2, x0 = -w / 2, x1 = w / 2;
            double r = c.Cantos == CantoPerfil.Reto ? 0 : c.CantoMm;
            bool round = c.Cantos == CantoPerfil.Boleado;

            switch (c.Forma)
            {
                case FormaPerfil.Retangular:
                    g.Loops.Add(Geo.Poly(Rect(x0, y0, x1, y1), CornerSizes(4, r, c.SoFaceExterna, 2, 3), round));
                    break;

                case FormaPerfil.TuboRetangular:
                    g.Loops.Add(Geo.Poly(Rect(x0, y0, x1, y1), CornerSizes(4, r, c.SoFaceExterna, 2, 3), round));
                    if (c.Oco)
                    {
                        if (2 * t >= Math.Min(w, d)) g.Avisos.Add("A chapa é grossa demais para o tamanho do tubo: saiu maciço.");
                        else
                        {
                            double ri = round ? Math.Max(0, r - t) : c.Cantos == CantoPerfil.Chanfrado ? Math.Max(0, r - t * 0.4142) : 0;
                            g.Loops.Add(Geo.Poly(Rect(x0 + t, y0 + t, x1 - t, y1 - t), CornerSizes(4, ri, c.SoFaceExterna, 2, 3), round));
                        }
                    }
                    break;

                case FormaPerfil.TuboRedondo:
                    {
                        double rad = w / 2;
                        g.Loops.Add(Geo.Circle(0, off, rad));
                        if (c.Oco)
                        {
                            if (t >= rad) g.Avisos.Add("A chapa é grossa demais para o diâmetro: saiu maciço.");
                            else g.Loops.Add(Geo.Circle(0, off, rad - t));
                        }
                        break;
                    }

                case FormaPerfil.I:
                    {
                        double tf = Math.Min(c.AbaMm, d / 2.2), tw = Math.Min(c.AlmaMm, w * 0.9);
                        if (c.AbaMm > tf || c.AlmaMm > tw) g.Avisos.Add("Aba ou alma maior que o perfil: reduzidas para caber.");
                        var p = new List<P>
                        {
                            new P(x0, y0), new P(x1, y0), new P(x1, y0 + tf), new P(tw / 2, y0 + tf), new P(tw / 2, y1 - tf), new P(x1, y1 - tf),
                            new P(x1, y1), new P(x0, y1), new P(x0, y1 - tf), new P(-tw / 2, y1 - tf), new P(-tw / 2, y0 + tf), new P(x0, y0 + tf)
                        };
                        g.Loops.Add(Geo.Poly(p, Edges(p.Count, r, new[] { 0, 1, 6, 7 }), round));
                        break;
                    }

                case FormaPerfil.T:
                    {
                        double tf = Math.Min(c.AbaMm, d * 0.9), tw = Math.Min(c.AlmaMm, w * 0.9);
                        // flange on the exterior face (y1); mirrored when it faces the interior
                        var p = new List<P>
                        {
                            new P(-tw / 2, y0), new P(tw / 2, y0), new P(tw / 2, y1 - tf), new P(x1, y1 - tf), new P(x1, y1), new P(x0, y1), new P(x0, y1 - tf), new P(-tw / 2, y1 - tf)
                        };
                        var lt = Geo.Poly(p, Edges(p.Count, r, new[] { 4, 5 }), round);
                        g.Loops.Add(c.AbaParaFora ? lt : Geo.Mirror(lt, false, off));
                        break;
                    }

                case FormaPerfil.U:
                    {
                        double tt = Math.Min(t, Math.Min(w / 2.2, d * 0.9));
                        // closed side (base) on the exterior face; legs towards the interior
                        var p = new List<P>
                        {
                            new P(x0, y0), new P(x0 + tt, y0), new P(x0 + tt, y1 - tt), new P(x1 - tt, y1 - tt), new P(x1 - tt, y0), new P(x1, y0), new P(x1, y1), new P(x0, y1)
                        };
                        var lu = Geo.Poly(p, Edges(p.Count, r, new[] { 6, 7 }), round);
                        g.Loops.Add(c.AbaParaFora ? lu : Geo.Mirror(lu, false, off));
                        break;
                    }

                case FormaPerfil.L:
                    {
                        double tt = Math.Min(t, Math.Min(w * 0.9, d * 0.9));
                        // horizontal leg on the exterior face, vertical leg on the left
                        var p = new List<P>
                        {
                            new P(x0, y0), new P(x0 + tt, y0), new P(x0 + tt, y1 - tt), new P(x1, y1 - tt), new P(x1, y1), new P(x0, y1)
                        };
                        var ll = Geo.Poly(p, Edges(p.Count, r, new[] { 4, 5 }), round);
                        if (c.Espelhar) ll = Geo.Mirror(ll, true, 0);
                        if (!c.AbaParaFora) ll = Geo.Mirror(ll, false, off);
                        g.Loops.Add(ll);
                        break;
                    }

                case FormaPerfil.ComTampa:
                    {
                        double gap = c.VaoVidroMm, h = Math.Min(c.HasteMm, Math.Min(w, c.TampaLarguraMm) * 0.9);
                        double wc = c.TampaLarguraMm, tc = c.TampaProfundidadeMm;
                        double b0 = off - gap / 2 - d, b1 = off - gap / 2, c0 = off + gap / 2, c1 = off + gap / 2 + tc;
                        var p = new List<P>
                        {
                            new P(x0, b0), new P(x1, b0), new P(x1, b1), new P(h / 2, b1), new P(h / 2, c0), new P(wc / 2, c0),
                            new P(wc / 2, c1), new P(-wc / 2, c1), new P(-wc / 2, c0), new P(-h / 2, c0), new P(-h / 2, b1), new P(x0, b1)
                        };
                        // rounded / chamfered: the outer corners of the body and of the cap
                        g.Loops.Add(Geo.Poly(p, Edges(p.Count, r, c.SoFaceExterna ? new[] { 6, 7 } : new[] { 0, 1, 6, 7 }), round));
                        if (c.Oco)
                        {
                            if (2 * t >= Math.Min(w, d)) g.Avisos.Add("A chapa é grossa demais para o corpo: saiu maciço.");
                            else g.Loops.Add(Geo.Poly(Rect(x0 + t, b0 + t, x1 - t, b1 - t), new double[4], false));
                        }
                        if (gap <= 0) g.Avisos.Add("Vão do vidro zero: a tampa encosta no corpo.");
                        break;
                    }

                case FormaPerfil.Canto:
                    {
                        // preview of a 90° corner: two legs meeting on the bisector (meia esquadria) or overlapping (L)
                        double a = c.Perna1Mm, b = c.Perna2Mm, e = c.EspessuraCantoMm;
                        if (c.TipoCanto == TipoCanto.V)
                        {
                            g.Loops.Add(Geo.Poly(new List<P> { new P(-a, -e / 2), new P(-e / 2, -e / 2), new P(e / 2, e / 2), new P(-a, e / 2) }, new double[4], false));
                            g.Loops.Add(Geo.Poly(new List<P> { new P(-e / 2, -e / 2), new P(-e / 2, -b), new P(e / 2, -b), new P(e / 2, e / 2) }, new double[4], false));
                        }
                        else
                        {
                            g.Loops.Add(Geo.Poly(new List<P> { new P(-a, -e / 2), new P(e / 2, -e / 2), new P(e / 2, e / 2), new P(-a, e / 2) }, new double[4], false));
                            g.Loops.Add(Geo.Poly(new List<P> { new P(-e / 2, -e / 2), new P(-e / 2, -b), new P(e / 2, -b), new P(e / 2, -e / 2) }, new double[4], false));
                        }
                        g.Canto = true;
                        break;
                    }
            }

            if (!g.Canto)
            {
                var pts = g.Loops.SelectMany(l => l.Tessellate()).ToList();
                if (pts.Count > 0 && (pts.Min(q => q.Y) > 1e-6 || pts.Max(q => q.Y) < -1e-6))
                    g.Avisos.Add("O perfil não cruza o plano dos painéis (deslocamento grande): o Revit não terá onde cortar o vidro.");
                if (pts.Count > 0 && (pts.Min(q => q.X) > 1e-6 || pts.Max(q => q.X) < -1e-6))
                    g.Avisos.Add("O perfil não cruza a linha da grade.");
            }
            return g;
        }

        // ------------------------------------------------------------------ helpers

        private static List<P> Rect(double x0, double y0, double x1, double y1) =>
            new List<P> { new P(x0, y0), new P(x1, y0), new P(x1, y1), new P(x0, y1) };

        /// <summary>Corner size per vertex of a rectangle; exterior-only = the two top vertices.</summary>
        private static double[] CornerSizes(int n, double r, bool onlyExt, params int[] ext)
        {
            var s = new double[n];
            for (int i = 0; i < n; i++) s[i] = !onlyExt || ext.Contains(i) ? r : 0;
            return s;
        }

        /// <summary>Corner size only at the listed (outer) vertices.</summary>
        private static double[] Edges(int n, double r, int[] at)
        {
            var s = new double[n];
            foreach (int i in at) if (i >= 0 && i < n) s[i] = r;
            return s;
        }

    }

    /// <summary>2D point (mm).</summary>
    public struct P
    {
        public double X, Y;
        public P(double x, double y) { X = x; Y = y; }
        public static P operator +(P a, P b) => new P(a.X + b.X, a.Y + b.Y);
        public static P operator -(P a, P b) => new P(a.X - b.X, a.Y - b.Y);
        public static P operator *(P a, double k) => new P(a.X * k, a.Y * k);
        public double Len => Math.Sqrt(X * X + Y * Y);
        public P Unit => Len < 1e-12 ? new P(0, 0) : new P(X / Len, Y / Len);
        public override string ToString() => $"({X:0.###}; {Y:0.###})";
    }

    /// <summary>A line (Mid null) or an arc through Mid.</summary>
    public sealed class Seg
    {
        public P A, B;
        public P? Mid;
        public bool Arco => Mid.HasValue;
    }

    public sealed class Loop
    {
        public List<Seg> Segs = new List<Seg>();

        /// <summary>Points along the loop (arcs in 8 steps): preview and area.</summary>
        public List<P> Tessellate()
        {
            var pts = new List<P>();
            foreach (var s in Segs)
            {
                pts.Add(s.A);
                if (s.Arco) pts.AddRange(Geo.ArcPoints(s.A, s.Mid.Value, s.B, 8).Skip(1).Take(7));
            }
            return pts;
        }

        public double SignedArea()
        {
            var p = Tessellate();
            double a = 0;
            for (int i = 0; i < p.Count; i++) { P u = p[i], v = p[(i + 1) % p.Count]; a += u.X * v.Y - v.X * u.Y; }
            return a / 2;
        }
    }

    public sealed class PerfilGeom
    {
        public List<Loop> Loops = new List<Loop>();
        public List<string> Avisos = new List<string>();
        public bool Canto;
        /// <summary>Net area: first loop minus the holes (mm²).</summary>
        public double AreaMm2 => Loops.Count == 0 ? 0
            : Canto ? Loops.Sum(l => Math.Abs(l.SignedArea()))
            : Math.Abs(Loops[0].SignedArea()) - Loops.Skip(1).Sum(l => Math.Abs(l.SignedArea()));
    }

    public static class Geo
    {
        /// <summary>Closed polygon with a chamfer (round = false) or a fillet of the given size at each vertex.</summary>
        public static Loop Poly(List<P> pts, double[] size, bool round)
        {
            int n = pts.Count;
            var cut = new P[n, 2];           // (entry, exit) point of each vertex
            var has = new bool[n];
            var mid = new P?[n];
            for (int i = 0; i < n; i++)
            {
                P v = pts[i], prev = pts[(i + n - 1) % n], next = pts[(i + 1) % n];
                double s = size != null && i < size.Length ? size[i] : 0;
                cut[i, 0] = v; cut[i, 1] = v;
                if (s <= 1e-9) continue;
                P u1 = (prev - v).Unit, u2 = (next - v).Unit;
                double cos = Math.Max(-1, Math.Min(1, u1.X * u2.X + u1.Y * u2.Y));
                double theta = Math.Acos(cos);                    // interior angle at v
                if (theta < 1e-3 || Math.PI - theta < 1e-3) continue;
                double tdist = round ? s / Math.Tan(theta / 2) : s;
                double lim = Math.Min((prev - v).Len, (next - v).Len) * 0.49;
                tdist = Math.Min(tdist, lim);
                cut[i, 0] = v + u1 * tdist;
                cut[i, 1] = v + u2 * tdist;
                has[i] = true;
                if (round)
                {
                    double rr = tdist * Math.Tan(theta / 2);
                    P bis = (u1 + u2).Unit;
                    double dc = rr / Math.Sin(theta / 2);
                    P center = v + bis * dc;
                    mid[i] = center - bis * rr;
                }
            }
            var loop = new Loop();
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                if (has[i]) loop.Segs.Add(new Seg { A = cut[i, 0], B = cut[i, 1], Mid = mid[i] });
                P a = cut[i, 1], b = cut[j, 0];
                if ((b - a).Len > 1e-6) loop.Segs.Add(new Seg { A = a, B = b });
            }
            return loop;
        }

        /// <summary>Mirror image of a loop: about X = 0 (inX) or about Y = about.</summary>
        public static Loop Mirror(Loop l, bool inX, double about)
        {
            P M(P p) => inX ? new P(-p.X, p.Y) : new P(p.X, 2 * about - p.Y);
            var r = new Loop();
            for (int i = l.Segs.Count - 1; i >= 0; i--)
            {
                Seg s = l.Segs[i];
                r.Segs.Add(new Seg { A = M(s.B), B = M(s.A), Mid = s.Mid.HasValue ? M(s.Mid.Value) : (P?)null });
            }
            return r;
        }

        public static Loop Circle(double cx, double cy, double r)
        {
            var l = new Loop();
            l.Segs.Add(new Seg { A = new P(cx + r, cy), B = new P(cx - r, cy), Mid = new P(cx, cy + r) });
            l.Segs.Add(new Seg { A = new P(cx - r, cy), B = new P(cx + r, cy), Mid = new P(cx, cy - r) });
            return l;
        }

        /// <summary>n+1 points on the arc a → m → b (circle through the three points).</summary>
        public static List<P> ArcPoints(P a, P m, P b, int n)
        {
            double ax = a.X, ay = a.Y, bx = m.X, by = m.Y, cx = b.X, cy = b.Y;
            double d = 2 * (ax * (by - cy) + bx * (cy - ay) + cx * (ay - by));
            if (Math.Abs(d) < 1e-12) return new List<P> { a, b };
            double ux = ((ax * ax + ay * ay) * (by - cy) + (bx * bx + by * by) * (cy - ay) + (cx * cx + cy * cy) * (ay - by)) / d;
            double uy = ((ax * ax + ay * ay) * (cx - bx) + (bx * bx + by * by) * (ax - cx) + (cx * cx + cy * cy) * (bx - ax)) / d;
            double r = Math.Sqrt((ax - ux) * (ax - ux) + (ay - uy) * (ay - uy));
            double t0 = Math.Atan2(ay - uy, ax - ux), tm = Math.Atan2(by - uy, bx - ux), t1 = Math.Atan2(cy - uy, cx - ux);
            // go from t0 to t1 passing through tm
            double Norm(double x) { while (x < 0) x += 2 * Math.PI; while (x >= 2 * Math.PI) x -= 2 * Math.PI; return x; }
            double sweep = Norm(t1 - t0), toMid = Norm(tm - t0);
            if (toMid > sweep) sweep -= 2 * Math.PI;      // the other way round
            var pts = new List<P>();
            for (int i = 0; i <= n; i++)
            {
                double t = t0 + sweep * i / n;
                pts.Add(new P(ux + r * Math.Cos(t), uy + r * Math.Sin(t)));
            }
            return pts;
        }
    }
}
