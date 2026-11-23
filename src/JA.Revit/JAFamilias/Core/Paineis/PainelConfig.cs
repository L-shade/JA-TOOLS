using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization;
using JAEsquadrias.Infrastructure;

namespace JAEsquadrias.Core.Paineis
{
    public enum TipoPainel
    {
        Solido = 0,       // chapa / painel opaco liso
        Vidro = 1,        // vidro (spandrel, fixo sem caixilho)
        Sanduiche = 2,    // duas chapas + núcleo isolante
        Cassete = 3,      // ACM / chapa dobrada: face + retornos no perímetro, com junta
        Veneziana = 4,    // aletas horizontais (com ou sem moldura)
        Brise = 5,        // aletas verticais (com ou sem moldura)
        Emoldurado = 6    // moldura no perímetro + vidro ou painel cego, com travessas
    }

    public enum SlotPainel { Principal, Vidro, Nucleo, Moldura }

    /// <summary>Direction of an extrusion: through the panel, along its width or along its height.</summary>
    public enum EixoPainel { Profundidade, Largura, Altura }

    /// <summary>
    /// One solid of a panel. Coordinates in mm: x = along the width (0 = left edge seen from
    /// outside), y = up (0 = bottom edge), p = across the panel (0 = curtain-wall grid plane,
    /// + = exterior). The profile lives in the plane perpendicular to <see cref="Eixo"/>:
    /// Profundidade → (x, y), extruded from p = E0 to E1; Largura → (p, y), from x = E0 to E1;
    /// Altura → (x, p), from y = E0 to E1. The first loop is the outline, the others are holes.
    /// </summary>
    public sealed class SolidoPainel
    {
        public EixoPainel Eixo;
        public List<List<P2>> Loops = new List<List<P2>>();
        public double E0, E1;
        public SlotPainel Slot;
        public bool Coarse = true, Medium = true, Fine = true;
        public string Papel = "";

        public SolidoPainel Lod(bool coarse, bool medium, bool fine) { Coarse = coarse; Medium = medium; Fine = fine; return this; }
    }

    public sealed class PainelGeom
    {
        public double W, H;
        public readonly List<SolidoPainel> Solidos = new List<SolidoPainel>();
        public readonly List<string> Avisos = new List<string>();
        public int Aletas;
        public double AreaM2 => W * H / 1e6;
        /// <summary>Range across the panel (mm) actually occupied.</summary>
        public double PMin => Solidos.Count == 0 ? 0 : Solidos.Min(s => Faixa(s).Key);
        public double PMax => Solidos.Count == 0 ? 0 : Solidos.Max(s => Faixa(s).Value);

        public static KeyValuePair<double, double> Faixa(SolidoPainel s)
        {
            switch (s.Eixo)
            {
                case EixoPainel.Largura: return new KeyValuePair<double, double>(s.Loops[0].Min(q => q.X), s.Loops[0].Max(q => q.X));
                case EixoPainel.Altura: return new KeyValuePair<double, double>(s.Loops[0].Min(q => q.Y), s.Loops[0].Max(q => q.Y));
                default: return new KeyValuePair<double, double>(Math.Min(s.E0, s.E1), Math.Max(s.E0, s.E1));
            }
        }
    }

    /// <summary>
    /// A curtain-wall panel that is not a door or window (sólido, vidro, sanduíche, cassete ACM,
    /// veneziana, brise, emoldurado). The size comes from the curtain wall: one family per panel
    /// size, all with the same code (type mark).
    /// </summary>
    [DataContract]
    public sealed class PainelConfig
    {
        [DataMember(Order = 1)] public string Codigo { get; set; } = "PN01";
        [DataMember(Order = 2)] public string Nome { get; set; } = "";
        [DataMember(Order = 3)] public TipoPainel Tipo { get; set; } = TipoPainel.Cassete;

        /// <summary>Size of the panel this family was made for (set per family; the grid drives it).</summary>
        [DataMember(Order = 5)] public double LarguraMm { get; set; } = 1250;
        [DataMember(Order = 6)] public double AlturaMm { get; set; } = 3000;

        /// <summary>Sólido / sanduíche: total thickness. Vidro: glass. Emoldurado: infill.</summary>
        [DataMember(Order = 10)] public double EspessuraMm { get; set; } = 30;
        /// <summary>Centre of the panel from the grid plane (+ = towards the exterior).</summary>
        [DataMember(Order = 11)] public double DeslocamentoMm { get; set; }
        /// <summary>Open joint between neighbouring panels: each edge is set back half of it.</summary>
        [DataMember(Order = 12)] public double JuntaMm { get; set; } = 10;

        /// <summary>Cassete: sheet. Sanduíche: each face sheet.</summary>
        [DataMember(Order = 20)] public double ChapaMm { get; set; } = 4;
        /// <summary>Cassete: depth of the folded returns (towards the interior).</summary>
        [DataMember(Order = 21)] public double RetornoMm { get; set; } = 40;

        /// <summary>Veneziana / brise: blade depth (perpendicular to the façade when the angle is 0).</summary>
        [DataMember(Order = 30)] public double AletaLarguraMm { get; set; } = 100;
        [DataMember(Order = 31)] public double AletaEspessuraMm { get; set; } = 10;
        /// <summary>Distance between blade centres (it is adjusted so the blades fill the panel evenly).</summary>
        [DataMember(Order = 32)] public double PassoMm { get; set; } = 120;
        /// <summary>0 = blade perpendicular to the façade; + = exterior edge down (veneziana) / turned right (brise).</summary>
        [DataMember(Order = 33)] public double AnguloGraus { get; set; } = 30;

        /// <summary>Frame (veneziana, brise, emoldurado): face width; 0 = no frame.</summary>
        [DataMember(Order = 40)] public double MolduraMm { get; set; } = 50;
        [DataMember(Order = 41)] public double MolduraProfMm { get; set; } = 100;
        /// <summary>Emoldurado: glass (true) or opaque infill.</summary>
        [DataMember(Order = 42)] public bool PreenchimentoVidro { get; set; } = true;
        /// <summary>Emoldurado: horizontal intermediate rails (0–10).</summary>
        [DataMember(Order = 43)] public int Travessas { get; set; }

        [DataMember(Order = 50)] public string Material { get; set; } = "Alumínio composto (ACM) cinza grafite";
        [DataMember(Order = 51)] public string CorHex { get; set; } = "4A4F55";
        [DataMember(Order = 52)] public string MaterialVidro { get; set; } = "Vidro laminado incolor";
        [DataMember(Order = 53)] public string MaterialNucleo { get; set; } = "Lã de rocha";
        [DataMember(Order = 54)] public string MaterialMoldura { get; set; } = "Alumínio anodizado natural";
        [DataMember(Order = 55)] public string CorMolduraHex { get; set; } = "B8BCC0";
        /// <summary>Mirror interior / exterior (if a template draws the exterior on the other side).</summary>
        [DataMember(Order = 60)] public bool InverterLado { get; set; }

        public PainelConfig Normalized()
        {
            if (string.IsNullOrWhiteSpace(Codigo)) Codigo = "PN01";
            Codigo = Codigo.Trim();
            if (Nome == null) Nome = "";
            if (!Enum.IsDefined(typeof(TipoPainel), Tipo)) Tipo = TipoPainel.Solido;
            LarguraMm = C(LarguraMm, 50, 20000, 1250);
            AlturaMm = C(AlturaMm, 50, 20000, 3000);
            EspessuraMm = C(EspessuraMm, 1, 1000, 30);
            DeslocamentoMm = Clamp(DeslocamentoMm, -2000, 2000);
            JuntaMm = Clamp(JuntaMm, 0, 200);
            ChapaMm = C(ChapaMm, 1, 100, 4);
            RetornoMm = C(RetornoMm, 2, 1000, 40);
            AletaLarguraMm = C(AletaLarguraMm, 5, 2000, 100);
            AletaEspessuraMm = C(AletaEspessuraMm, 1, 500, 10);
            PassoMm = C(PassoMm, 10, 5000, 120);
            AnguloGraus = Clamp(AnguloGraus, -80, 80);
            MolduraMm = Clamp(MolduraMm, 0, 1000);
            MolduraProfMm = C(MolduraProfMm, 2, 2000, 100);
            Travessas = Math.Max(0, Math.Min(10, Travessas));
            if (string.IsNullOrWhiteSpace(Material)) Material = "Alumínio composto (ACM) cinza grafite";
            if (string.IsNullOrWhiteSpace(CorHex)) CorHex = "4A4F55";
            if (string.IsNullOrWhiteSpace(MaterialVidro)) MaterialVidro = "Vidro laminado incolor";
            if (string.IsNullOrWhiteSpace(MaterialNucleo)) MaterialNucleo = "Lã de rocha";
            if (string.IsNullOrWhiteSpace(MaterialMoldura)) MaterialMoldura = "Alumínio anodizado natural";
            if (string.IsNullOrWhiteSpace(CorMolduraHex)) CorMolduraHex = "B8BCC0";
            return this;
        }

        private static double Clamp(double v, double a, double b) => double.IsNaN(v) ? a : Math.Max(a, Math.Min(b, v));
        private static double C(double v, double a, double b, double d) => v <= 0 || double.IsNaN(v) ? d : Clamp(v, a, b);

        public string ToJson() => Json.Serialize(this);
        public PainelConfig Copiar() => Json.Deserialize<PainelConfig>(ToJson()).Normalized();
        public static PainelConfig FromJson(string json)
        {
            try { return string.IsNullOrWhiteSpace(json) ? null : Json.Deserialize<PainelConfig>(json)?.Normalized(); }
            catch { return null; }
        }

        /// <summary>Sensible values when the kind changes in the configurator.</summary>
        public void PadroesDe(TipoPainel t)
        {
            Tipo = t;
            switch (t)
            {
                case TipoPainel.Vidro: if (EspessuraMm > 40) EspessuraMm = 10; break;
                case TipoPainel.Sanduiche: if (EspessuraMm < 30) EspessuraMm = 50; if (ChapaMm > 3) ChapaMm = 1; break;
                case TipoPainel.Solido: if (EspessuraMm < 3) EspessuraMm = 30; break;
                case TipoPainel.Cassete: if (ChapaMm < 2 || ChapaMm > 10) ChapaMm = 4; if (JuntaMm <= 0) JuntaMm = 10; break;
                case TipoPainel.Emoldurado: if (EspessuraMm > 60) EspessuraMm = 24; if (MolduraMm <= 0) MolduraMm = 50; break;
            }
        }

        // ------------------------------------------------------------------ texts

        public static string NomeTipo(TipoPainel t)
        {
            switch (t)
            {
                case TipoPainel.Solido: return "Sólido";
                case TipoPainel.Vidro: return "Vidro";
                case TipoPainel.Sanduiche: return "Sanduíche";
                case TipoPainel.Cassete: return "Cassete (ACM)";
                case TipoPainel.Veneziana: return "Veneziana";
                case TipoPainel.Brise: return "Brise vertical";
                default: return "Emoldurado";
            }
        }

        private static string N(double v) => v.ToString("0.#", CultureInfo.GetCultureInfo("pt-BR"));
        private static string I(double v) => Math.Round(v).ToString("0", CultureInfo.InvariantCulture);

        /// <summary>"JA Painel PN01 1250x3000" — one family per size.</summary>
        public string NomeFamilia(double w, double h) => "JA Painel " + Codigo + " " + I(w) + "x" + I(h);

        public string Descricao()
        {
            switch (Tipo)
            {
                case TipoPainel.Solido: return $"Painel sólido {N(EspessuraMm)} mm, {Material}" + Junta();
                case TipoPainel.Vidro: return $"Painel de vidro {N(EspessuraMm)} mm, {MaterialVidro}" + Junta();
                case TipoPainel.Sanduiche: return $"Painel sanduíche {N(EspessuraMm)} mm (chapas {N(ChapaMm)} mm + {MaterialNucleo.ToLowerInvariant()}), {Material}" + Junta();
                case TipoPainel.Cassete: return $"Cassete {N(ChapaMm)} mm com retorno de {N(RetornoMm)} mm, {Material}" + Junta();
                case TipoPainel.Veneziana: return $"Veneziana, aletas {N(AletaLarguraMm)} × {N(AletaEspessuraMm)} mm a cada {N(PassoMm)} mm, {N(AnguloGraus)}°, {Material}" + Moldura();
                case TipoPainel.Brise: return $"Brise vertical, aletas {N(AletaLarguraMm)} × {N(AletaEspessuraMm)} mm a cada {N(PassoMm)} mm, {N(AnguloGraus)}°, {Material}" + Moldura();
                default:
                    return $"Emoldurado {N(MolduraMm)} × {N(MolduraProfMm)} mm" + (Travessas > 0 ? $" com {Travessas} travessa(s)" : "") +
                           ", " + (PreenchimentoVidro ? MaterialVidro.ToLowerInvariant() : Material.ToLowerInvariant()) + $" {N(EspessuraMm)} mm" + Junta();
            }
        }

        private string Junta() => JuntaMm > 0 ? $", junta {N(JuntaMm)} mm" : "";
        private string Moldura() => MolduraMm > 0 ? $", moldura {N(MolduraMm)} × {N(MolduraProfMm)} mm" : ", sem moldura";

        /// <summary>"Cassete 4 mm · 1250 × 3000 mm" — for JA_Dimensoes.</summary>
        public string Dimensoes(double w, double h) => I(w) + " x " + I(h) + " x " + I(Profundidade()) + " mm";

        /// <summary>Overall depth (mm) of the panel.</summary>
        public double Profundidade()
        {
            var g = Build(LarguraMm, AlturaMm);
            return g.Solidos.Count == 0 ? 0 : g.PMax - g.PMin;
        }

        // ------------------------------------------------------------------ geometry

        public PainelGeom Build() => Build(LarguraMm, AlturaMm);

        public PainelGeom Build(double w, double h)
        {
            var g = new PainelGeom { W = w, H = h };
            double j = Math.Min(JuntaMm / 2, Math.Min(w, h) / 4);
            var R = new Rc(j, j, w - j, h - j);
            double o = DeslocamentoMm;
            if (!R.Valid) { g.Avisos.Add("Painel pequeno demais para a junta escolhida."); return g; }

            switch (Tipo)
            {
                case TipoPainel.Solido:
                    g.Solidos.Add(Frontal(R, o - EspessuraMm / 2, o + EspessuraMm / 2, SlotPainel.Principal, "painel"));
                    break;

                case TipoPainel.Vidro:
                    g.Solidos.Add(Frontal(R, o - EspessuraMm / 2, o + EspessuraMm / 2, SlotPainel.Vidro, "vidro"));
                    break;

                case TipoPainel.Sanduiche:
                {
                    double e = EspessuraMm, ch = Math.Min(ChapaMm, e / 3);
                    double p0 = o - e / 2, p1 = o + e / 2;
                    g.Solidos.Add(Frontal(R, p1 - ch, p1, SlotPainel.Principal, "chapa externa"));
                    g.Solidos.Add(Frontal(R, p0 + ch, p1 - ch, SlotPainel.Nucleo, "núcleo"));
                    g.Solidos.Add(Frontal(R, p0, p0 + ch, SlotPainel.Principal, "chapa interna"));
                    if (ChapaMm > e / 3) g.Avisos.Add("Chapas grossas demais para a espessura: reduzidas a 1/3 da espessura.");
                    break;
                }

                case TipoPainel.Cassete:
                {
                    double ch = Math.Min(ChapaMm, Math.Min(R.W, R.H) / 6), ret = Math.Max(RetornoMm, ch + 1);
                    double front = o + ret / 2;             // exterior face of the sheet
                    double back = o - ret / 2;              // end of the returns
                    g.Solidos.Add(Frontal(R, front - ch, front, SlotPainel.Principal, "face"));
                    // returns: left/right full height, top/bottom between them
                    g.Solidos.Add(Frontal(new Rc(R.X0, R.Y0, R.X0 + ch, R.Y1), back, front - ch, SlotPainel.Principal, "retorno"));
                    g.Solidos.Add(Frontal(new Rc(R.X1 - ch, R.Y0, R.X1, R.Y1), back, front - ch, SlotPainel.Principal, "retorno"));
                    g.Solidos.Add(Frontal(new Rc(R.X0 + ch, R.Y0, R.X1 - ch, R.Y0 + ch), back, front - ch, SlotPainel.Principal, "retorno"));
                    g.Solidos.Add(Frontal(new Rc(R.X0 + ch, R.Y1 - ch, R.X1 - ch, R.Y1), back, front - ch, SlotPainel.Principal, "retorno"));
                    break;
                }

                case TipoPainel.Veneziana:
                case TipoPainel.Brise:
                    Aletas(g, R, o, Tipo == TipoPainel.Brise);
                    break;

                default:
                    Emoldurado(g, R, o);
                    break;
            }
            return g;
        }

        private static SolidoPainel Frontal(Rc r, double p0, double p1, SlotPainel slot, string papel)
        {
            var s = new SolidoPainel { Eixo = EixoPainel.Profundidade, E0 = Math.Min(p0, p1), E1 = Math.Max(p0, p1), Slot = slot, Papel = papel };
            s.Loops.Add(r.Loop());
            return s;
        }

        private static SolidoPainel Anel(Rc outer, Rc inner, double p0, double p1, SlotPainel slot, string papel)
        {
            var s = Frontal(outer, p0, p1, slot, papel);
            if (inner.Valid) s.Loops.Add(inner.Loop());
            return s;
        }

        /// <summary>Frame ring (when MolduraMm &gt; 0); returns the clear opening inside it.</summary>
        private Rc Moldura(PainelGeom g, Rc R, double o, double face)
        {
            if (face <= 0) return R;
            double mf = Math.Min(face, Math.Min(R.W, R.H) / 3);
            Rc luz = R.Inset(mf);
            g.Solidos.Add(Anel(R, luz, o - MolduraProfMm / 2, o + MolduraProfMm / 2, SlotPainel.Moldura, "moldura"));
            if (mf < face - 0.01) g.Avisos.Add("Moldura reduzida: o painel é estreito para a largura escolhida.");
            return luz;
        }

        private void Aletas(PainelGeom g, Rc R, double o, bool vertical)
        {
            Rc luz = Moldura(g, R, o, MolduraMm);
            if (!luz.Valid) { g.Avisos.Add("Sem espaço para as aletas dentro da moldura."); return; }
            double vao = vertical ? luz.W : luz.H;
            int n = Math.Max(1, Math.Min(400, (int)Math.Round(vao / PassoMm)));
            double passo = vao / n;
            double a = AletaLarguraMm, b = Math.Min(AletaEspessuraMm, passo * 0.9);
            double th = AnguloGraus * Math.PI / 180;
            // blade direction across the façade at angle th (0 = perpendicular to it), and its thickness direction
            double dp = Math.Cos(th), ds = -Math.Sin(th);       // (p, s): s = y for veneziana (down), x for brise
            double np = Math.Sin(th), ns = Math.Cos(th);
            if (vertical) ds = -ds;                               // brise: + angle turns the exterior edge to the right
            if (vertical) np = -np;
            // coarse: one thin panel instead of the blades
            g.Solidos.Add(Frontal(luz, o - 3, o + 3, SlotPainel.Principal, "aletas (simplificado)").Lod(true, false, false));
            for (int k = 0; k < n; k++)
            {
                double c = (vertical ? luz.X0 : luz.Y0) + passo * (k + 0.5);
                var pts = new List<P2>();
                foreach (var (sa, sb) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
                {
                    double pp = o + sa * a / 2 * dp + sb * b / 2 * np;
                    double ss = c + sa * a / 2 * ds + sb * b / 2 * ns;
                    pts.Add(vertical ? new P2(ss, pp) : new P2(pp, ss));
                }
                var s = new SolidoPainel
                {
                    Eixo = vertical ? EixoPainel.Altura : EixoPainel.Largura,
                    E0 = vertical ? luz.Y0 : luz.X0,
                    E1 = vertical ? luz.Y1 : luz.X1,
                    Slot = SlotPainel.Principal,
                    Papel = "aleta"
                }.Lod(false, true, true);
                s.Loops.Add(pts);
                g.Solidos.Add(s);
            }
            g.Aletas = n;
            // blades that would cross the neighbours (too wide for the spacing at this angle)
            double ocupa = Math.Abs(a * ds) + Math.Abs(b * ns);
            if (ocupa > passo + 0.5) g.Avisos.Add("As aletas se sobrepõem neste ângulo: aumente o passo ou diminua a largura da aleta.");
            if (MolduraMm > 0 && Math.Abs(a * dp) + Math.Abs(b * np) > MolduraProfMm + 0.5)
                g.Avisos.Add("As aletas passam da profundidade da moldura.");
        }

        private void Emoldurado(PainelGeom g, Rc R, double o)
        {
            double mf = Math.Max(10, MolduraMm);    // an emoldurado panel always has its frame
            Rc luz = Moldura(g, R, o, mf);
            if (!luz.Valid) { g.Avisos.Add("Painel pequeno demais para a moldura."); return; }
            int n = Travessas;
            double t = Math.Min(mf, luz.H / (2 * n + 2));
            var faixas = new List<KeyValuePair<double, double>>();
            double y0 = luz.Y0;
            for (int k = 1; k <= n; k++)
            {
                double ym = luz.Y0 + luz.H * k / (n + 1);
                g.Solidos.Add(Frontal(new Rc(luz.X0, ym - t / 2, luz.X1, ym + t / 2), o - MolduraProfMm / 2, o + MolduraProfMm / 2, SlotPainel.Moldura, "travessa"));
                faixas.Add(new KeyValuePair<double, double>(y0, ym - t / 2));
                y0 = ym + t / 2;
            }
            faixas.Add(new KeyValuePair<double, double>(y0, luz.Y1));
            double e = Math.Min(EspessuraMm, MolduraProfMm - 2);
            if (e < EspessuraMm) g.Avisos.Add("Preenchimento mais grosso que a moldura: reduzido.");
            foreach (var f in faixas)
            {
                var r = new Rc(luz.X0, f.Key, luz.X1, f.Value);
                if (!r.Valid) continue;
                g.Solidos.Add(Frontal(r, o - e / 2, o + e / 2, PreenchimentoVidro ? SlotPainel.Vidro : SlotPainel.Principal, PreenchimentoVidro ? "vidro" : "painel"));
            }
        }
    }
}
