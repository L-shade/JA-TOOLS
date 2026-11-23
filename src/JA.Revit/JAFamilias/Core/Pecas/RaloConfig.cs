using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization;
using JAEsquadrias.Infrastructure;

namespace JAEsquadrias.Core
{
    public enum TipoRalo { Linear = 0, Quadrado = 1, Redondo = 2, CaixaSifonada = 3, Hemisferico = 4 }
    public enum GrelhaRalo { Fendas = 0, Furos = 1, Quadriculada = 2, Revestivel = 3 }
    public enum PosicaoSaida { Centro = 0, Esquerda = 1, Direita = 2 }
    public enum SistemaRalo { Esgoto = 0, Pluvial = 1 }

    /// <summary>
    /// Ralo (floor drain) generated as a PIPE FITTING (Conexões de tubo) with real pipe connectors:
    /// linear (any length/width), square, round, caixa sifonada (with side inlets) or dome
    /// (hemisférico, roofs and gutters). Local coordinates (mm): origin at the centre of the grate
    /// top = finished floor; X along the length, Y across, Z up (the body is below Z = 0).
    /// </summary>
    [DataContract]
    public sealed class RaloConfig : IPecaConfig
    {
        public TipoPeca Tipo => TipoPeca.Ralo;

        [DataMember(Order = 1)] public string Codigo { get; set; } = "RL01";
        [DataMember(Order = 2)] public string Nome { get; set; } = "";
        [DataMember(Order = 3)] public string ModeloId { get; set; } = "";

        [DataMember(Order = 10)] public TipoRalo Formato { get; set; } = TipoRalo.Linear;
        /// <summary>Linear: length. Others: not used (square side / diameter = <see cref="LarguraMm"/>).</summary>
        [DataMember(Order = 11)] public double ComprimentoMm { get; set; } = 700;
        /// <summary>Linear: width; square / caixa: side; round / dome: diameter.</summary>
        [DataMember(Order = 12)] public double LarguraMm { get; set; } = 70;
        /// <summary>Depth of the body below the floor (top of the grate to the outlet).</summary>
        [DataMember(Order = 13)] public double AlturaCorpoMm { get; set; } = 80;

        [DataMember(Order = 20)] public GrelhaRalo Grelha { get; set; } = GrelhaRalo.Fendas;
        [DataMember(Order = 21)] public double EspessuraGrelhaMm { get; set; } = 5;
        /// <summary>Visible rim of the frame around the grate.</summary>
        [DataMember(Order = 22)] public double MolduraMm { get; set; } = 6;
        /// <summary>Tile-insert grate: depth of the tray for the floor tile.</summary>
        [DataMember(Order = 23)] public double RebaixoMm { get; set; } = 15;

        [DataMember(Order = 30)] public double DiametroSaidaMm { get; set; } = 50;
        [DataMember(Order = 31)] public double ComprimentoSaidaMm { get; set; } = 60;
        [DataMember(Order = 32)] public bool SaidaVertical { get; set; } = true;
        [DataMember(Order = 33)] public PosicaoSaida Posicao { get; set; } = PosicaoSaida.Centro;
        /// <summary>Linear, outlet at one end: distance from that end to the outlet axis.</summary>
        [DataMember(Order = 34)] public double DistanciaSaidaMm { get; set; } = 100;
        /// <summary>Caixa sifonada: side inlets (0–4) and their diameter.</summary>
        [DataMember(Order = 35)] public int Entradas { get; set; } = 3;
        [DataMember(Order = 36)] public double DiametroEntradaMm { get; set; } = 40;
        [DataMember(Order = 37)] public SistemaRalo Sistema { get; set; } = SistemaRalo.Esgoto;

        [DataMember(Order = 40)] public string MaterialGrelha { get; set; } = "Aço inox escovado";
        [DataMember(Order = 41)] public string CorGrelha { get; set; } = "C9CDD1";
        [DataMember(Order = 42)] public string MaterialCorpo { get; set; } = "PVC cinza";
        [DataMember(Order = 43)] public string CorCorpo { get; set; } = "8A9099";
        [DataMember(Order = 44)] public string MaterialRevestimento { get; set; } = "Porcelanato do piso";
        [DataMember(Order = 45)] public string CorRevestimento { get; set; } = "D8D2C8";

        /// <summary>
        /// Ralo no croqui: the centre line of a linear drain drawn in the plan (mm, family
        /// coordinates; the first point is the family origin). Any number of straight stretches,
        /// mitred at the corners; length = the path's.
        /// </summary>
        [DataMember(Order = 50)] public List<Pt> Caminho { get; set; }

        /// <summary>0: the path is the drain's axis. +1 / -1: the path is a wall face and the drain runs on its left / right, AfastamentoMm away.</summary>
        [DataMember(Order = 51)] public int BordaNoCaminho { get; set; }
        [DataMember(Order = 52)] public double AfastamentoMm { get; set; } = 20;
        /// <summary>Floor fall towards the drain (%), for the description and the schedules.</summary>
        [DataMember(Order = 53)] public double CaimentoPct { get; set; } = 1;

        public bool NoCroqui => Formato == TipoRalo.Linear && Caminho != null && Caminho.Count >= 2;

        /// <summary>The axis of the drawn drain (the wall face path moved by half the width plus the gap).</summary>
        public List<P2> Eixo()
        {
            var p = Caminho.Select(q => q.P).ToList();
            return BordaNoCaminho == 0 ? p : Deslocar(p, Math.Sign(BordaNoCaminho) * (LarguraMm / 2 + AfastamentoMm));
        }

        /// <summary>Outline of the drain in plan (mm, family coordinates): what a floor tiling leaves out.</summary>
        public List<P2> ContornoEmPlanta()
        {
            var c = (RaloConfig)Copiar();
            if (c.NoCroqui) return Faixa(c.Eixo(), c.LarguraMm);
            if (c.Circular) return Peca.Circle(0, 0, c.LarguraMm / 2, 24);
            double L = c.Comprimento, W = c.LarguraMm;
            return Peca.Rect(-L / 2, -W / 2, L / 2, W / 2);
        }

        /// <summary>Length of the drawn path (mm), 0 without one.</summary>
        public double ComprimentoCaminho()
        {
            if (Caminho == null) return 0;
            double t = 0;
            for (int i = 0; i + 1 < Caminho.Count; i++) t += Math.Sqrt(Math.Pow(Caminho[i + 1].X - Caminho[i].X, 2) + Math.Pow(Caminho[i + 1].Y - Caminho[i].Y, 2));
            return t;
        }

        public static readonly double[] Diametros = { 40, 50, 75, 100, 150 };

        public bool Linear => Formato == TipoRalo.Linear;
        public bool Circular => Formato == TipoRalo.Redondo || Formato == TipoRalo.Hemisferico;
        public double Comprimento => Linear ? ComprimentoMm : LarguraMm;

        public RaloConfig Normalized()
        {
            if (string.IsNullOrWhiteSpace(Codigo)) Codigo = "RL01";
            if (Nome == null) Nome = "";
            if (ModeloId == null) ModeloId = "";
            if (!Enum.IsDefined(typeof(TipoRalo), Formato)) Formato = TipoRalo.Linear;
            if (!Enum.IsDefined(typeof(GrelhaRalo), Grelha)) Grelha = GrelhaRalo.Fendas;
            if (Formato == TipoRalo.Hemisferico) { if (Grelha == GrelhaRalo.Revestivel) Grelha = GrelhaRalo.Fendas; SaidaVertical = true; }
            if (Caminho != null)
            {
                // no repeated points, no stretch shorter than 1 mm
                var limpo = new List<Pt>();
                foreach (Pt q in Caminho.Where(q => q != null))
                    if (limpo.Count == 0 || Math.Abs(q.X - limpo[limpo.Count - 1].X) + Math.Abs(q.Y - limpo[limpo.Count - 1].Y) > 1) limpo.Add(q);
                Caminho = limpo.Count >= 2 ? limpo : null;
            }
            ComprimentoMm = NoCroqui ? PecaUtil.Clamp(ComprimentoCaminho(), 100, 50000) : PecaUtil.Clamp(ComprimentoMm, 200, 6000);
            LarguraMm = Linear ? PecaUtil.Clamp(LarguraMm, 35, 400) : PecaUtil.Clamp(LarguraMm, 60, 600);
            AlturaCorpoMm = PecaUtil.Clamp(AlturaCorpoMm, 30, 600);
            EspessuraGrelhaMm = PecaUtil.Clamp(EspessuraGrelhaMm <= 0 ? 5 : EspessuraGrelhaMm, 2, 30);
            MolduraMm = PecaUtil.Clamp(MolduraMm, 0, Math.Max(0, LarguraMm / 2 - 12));
            RebaixoMm = PecaUtil.Clamp(RebaixoMm <= 0 ? 15 : RebaixoMm, 5, 40);
            DiametroSaidaMm = PecaUtil.Clamp(DiametroSaidaMm <= 0 ? 50 : DiametroSaidaMm, 25, 200);
            ComprimentoSaidaMm = PecaUtil.Clamp(ComprimentoSaidaMm <= 0 ? 60 : ComprimentoSaidaMm, 20, 500);
            if (!Enum.IsDefined(typeof(PosicaoSaida), Posicao)) Posicao = PosicaoSaida.Centro;
            DistanciaSaidaMm = PecaUtil.Clamp(DistanciaSaidaMm, 20, Math.Max(20, ComprimentoMm / 2));
            AfastamentoMm = PecaUtil.Clamp(AfastamentoMm, 0, 2000);
            CaimentoPct = PecaUtil.Clamp(CaimentoPct, 0, 10);
            BordaNoCaminho = Math.Sign(BordaNoCaminho);
            Entradas = Math.Max(0, Math.Min(4, Entradas));
            DiametroEntradaMm = PecaUtil.Clamp(DiametroEntradaMm <= 0 ? 40 : DiametroEntradaMm, 25, 100);
            if (!Enum.IsDefined(typeof(SistemaRalo), Sistema)) Sistema = SistemaRalo.Esgoto;
            if (string.IsNullOrWhiteSpace(MaterialGrelha)) MaterialGrelha = "Aço inox escovado";
            if (string.IsNullOrWhiteSpace(CorGrelha)) CorGrelha = "C9CDD1";
            if (string.IsNullOrWhiteSpace(MaterialCorpo)) MaterialCorpo = "PVC cinza";
            if (string.IsNullOrWhiteSpace(CorCorpo)) CorCorpo = "8A9099";
            if (string.IsNullOrWhiteSpace(MaterialRevestimento)) MaterialRevestimento = "Porcelanato do piso";
            if (string.IsNullOrWhiteSpace(CorRevestimento)) CorRevestimento = "D8D2C8";
            return this;
        }

        public string ToJson() => Json.Serialize(this);
        public IPecaConfig Copiar() => Json.Deserialize<RaloConfig>(ToJson()).Normalized();

        public IList<MaterialDef> Materiais() => new List<MaterialDef>
        {
            new MaterialDef { Key = "grelha", Parametro = "Material da grelha", Nome = MaterialGrelha.Trim(), Hex = CorGrelha },
            new MaterialDef { Key = "corpo", Parametro = "Material do corpo", Nome = MaterialCorpo.Trim(), Hex = CorCorpo },
            new MaterialDef { Key = "revest", Parametro = "Material do revestimento", Nome = MaterialRevestimento.Trim(), Hex = CorRevestimento },
        };

        public string MaterialPrincipal() => MaterialGrelha;

        public static string NomeFormato(TipoRalo t)
        {
            switch (t)
            {
                case TipoRalo.Linear: return "Ralo linear";
                case TipoRalo.Quadrado: return "Ralo quadrado";
                case TipoRalo.Redondo: return "Ralo redondo";
                case TipoRalo.CaixaSifonada: return "Caixa sifonada";
                default: return "Ralo hemisférico";
            }
        }

        public static string NomeGrelha(GrelhaRalo g)
        {
            switch (g)
            {
                case GrelhaRalo.Fendas: return "grelha de fendas";
                case GrelhaRalo.Furos: return "grelha de furos";
                case GrelhaRalo.Quadriculada: return "grelha quadriculada";
                default: return "tampa revestível (invisível)";
            }
        }

        private static string Dn(double d) => "DN" + d.ToString("0", CultureInfo.InvariantCulture);

        public string Dimensoes()
        {
            string tam = NoCroqui ? PecaUtil.Mm(ComprimentoCaminho()) + " x " + PecaUtil.Mm(LarguraMm) + " mm em " + (Caminho.Count - 1) + " trecho(s)"
                       : Linear ? PecaUtil.Mm(ComprimentoMm) + " x " + PecaUtil.Mm(LarguraMm) + " mm"
                       : Circular ? "Ø" + PecaUtil.Mm(LarguraMm) + " mm"
                       : PecaUtil.Mm(LarguraMm) + " x " + PecaUtil.Mm(LarguraMm) + " mm";
            string s = tam + ", altura " + PecaUtil.Mm(AlturaCorpoMm) + ", saída " + Dn(DiametroSaidaMm);
            if (Formato == TipoRalo.CaixaSifonada && Entradas > 0) s += ", " + Entradas + " entrada(s) " + Dn(DiametroEntradaMm);
            return s;
        }

        public string Descricao()
        {
            var parts = new List<string> { NomeFormato(Formato) + " " + Dimensoes().Split(',')[0] };
            if (Formato != TipoRalo.Hemisferico) parts.Add(NomeGrelha(Grelha) + " em " + MaterialGrelha.ToLowerInvariant());
            string pos = Linear && Posicao != PosicaoSaida.Centro ? (Posicao == PosicaoSaida.Esquerda ? " na ponta esquerda" : " na ponta direita") : "";
            parts.Add("saída " + Dn(DiametroSaidaMm) + (SaidaVertical ? " vertical" : " horizontal") + pos);
            if (Formato == TipoRalo.CaixaSifonada && Entradas > 0) parts.Add(Entradas + " entrada(s) " + Dn(DiametroEntradaMm));
            parts.Add(Sistema == SistemaRalo.Pluvial ? "águas pluviais" : "esgoto");
            if (Linear && CaimentoPct > 0) parts.Add("caimento do piso " + CaimentoPct.ToString("0.#", CultureInfo.GetCultureInfo("pt-BR")) + "% para o ralo");
            if (NoCroqui && BordaNoCaminho != 0) parts.Add("a " + PecaUtil.Mm(AfastamentoMm) + " mm da parede");
            string d = string.Join("; ", parts);
            return string.IsNullOrWhiteSpace(Nome) ? d : Nome.Trim() + " – " + d;
        }

        // ==========================================================================================
        public PecaModelo Build()
        {
            var c = (RaloConfig)Copiar();
            var m = new PecaModelo();
            double e = c.EspessuraGrelhaMm, a = c.MolduraMm, H = Math.Max(c.AlturaCorpoMm, e + 15);
            double L = c.Comprimento, W = c.LarguraMm;
            double ds = c.DiametroSaidaMm;

            if (c.Formato == TipoRalo.Hemisferico) return Hemisferico(c, m);
            if (c.NoCroqui) return Croqui(c, m);

            // ---- top: frame (rim) + grate (or tile tray)
            if (c.Circular)
            {
                double r = W / 2, ri = r - a;
                if (a > 0.5) m.Pecas.Add(Peca.Laje("Moldura", Peca.Circle(0, 0, r, 40), new[] { Peca.Circle(0, 0, ri, 40) }, -e, 0, "grelha"));
                AddGrelha(m, c, null, ri, e);
                m.Pecas.Add(Peca.Laje("Corpo", Peca.Circle(0, 0, r * 0.85, 32), null, -H, -e, "corpo"));
            }
            else
            {
                double x0 = -L / 2, x1 = L / 2, y0 = -W / 2, y1 = W / 2;
                if (a > 0.5) m.Pecas.Add(Peca.Laje("Moldura", Peca.Rect(x0, y0, x1, y1), new[] { Peca.Rect(x0 + a, y0 + a, x1 - a, y1 - a) }, -e, 0, "grelha"));
                AddGrelha(m, c, new[] { x0 + a, y0 + a, x1 - a, y1 - a }, 0, e);
                double inset = c.Formato == TipoRalo.CaixaSifonada ? 0 : Math.Min(6, W * 0.05);
                m.Pecas.Add(Peca.Laje("Corpo", Peca.Rect(x0 + inset, y0 + inset, x1 - inset, y1 - inset), null, -H, -e, "corpo"));
            }

            // ---- outlet: a pipe stub with a connector at its end
            double corpoMeia = c.Circular ? W / 2 * 0.85 : W / 2;
            double xs = 0;
            if (c.Linear && c.Posicao != PosicaoSaida.Centro)
                xs = (c.Posicao == PosicaoSaida.Esquerda ? -1 : 1) * Math.Max(0, L / 2 - c.DistanciaSaidaMm);
            if (c.SaidaVertical)
            {
                if (ds > 2 * corpoMeia - 4) m.Avisos.Add("A saída " + Dn(ds) + " é mais larga que o corpo do ralo: confira a largura.");
                m.Pecas.Add(Tubo("Saída", new V3(xs, 0, -H), new V3(0, 0, -1), ds, c.ComprimentoSaidaMm, true));
            }
            else
            {
                double hc = H - e;
                double dsH = Math.Min(ds, hc - 4);
                if (dsH < ds) m.Avisos.Add("A saída horizontal " + Dn(ds) + " não cabe na altura do corpo: aumente a altura (o tubo foi desenhado com " + PecaUtil.Mm(dsH) + " mm).");
                double zc = -e - hc / 2;
                V3 dir; V3 o;
                if (c.Linear && c.Posicao == PosicaoSaida.Esquerda) { dir = new V3(-1, 0, 0); o = new V3(-L / 2, 0, zc); }
                else if (c.Linear && c.Posicao == PosicaoSaida.Direita) { dir = new V3(1, 0, 0); o = new V3(L / 2, 0, zc); }
                else { dir = new V3(0, -1, 0); o = new V3(0, -corpoMeia, zc); }
                m.Pecas.Add(Tubo("Saída", o, dir, Math.Max(20, dsH), c.ComprimentoSaidaMm, true));
            }

            // ---- caixa sifonada: side inlets (−X, +X, +Y, −Y), each a pipe connector
            if (c.Formato == TipoRalo.CaixaSifonada && c.Entradas > 0)
            {
                double hc = H - e;
                double di = Math.Min(c.DiametroEntradaMm, hc * 0.6);
                double zi = -e - hc * 0.4;
                var lados = new List<(V3 o, V3 d)> { (new V3(-W / 2, 0, zi), new V3(-1, 0, 0)), (new V3(W / 2, 0, zi), new V3(1, 0, 0)), (new V3(0, W / 2, zi), new V3(0, 1, 0)) };
                if (c.SaidaVertical) lados.Add((new V3(0, -W / 2, zi), new V3(0, -1, 0)));
                else if (c.Entradas > 3) m.Avisos.Add("Com a saída horizontal (no lado −Y) cabem 3 entradas.");
                for (int i = 0; i < Math.Min(c.Entradas, lados.Count); i++)
                    m.Pecas.Add(Tubo("Entrada " + (i + 1), lados[i].o, lados[i].d, di, 50, false));
            }

            // ---- numbers
            double areaGrelha = c.Circular ? Math.PI * W * W / 4 : L * W;
            m.Resumo.Add((c.Linear ? PecaUtil.Mm(L) + " × " + PecaUtil.Mm(W) : c.Circular ? "Ø" + PecaUtil.Mm(W) : PecaUtil.Mm(W) + " × " + PecaUtil.Mm(W)) + " mm");
            m.Resumo.Add("Saída " + Dn(ds) + (c.SaidaVertical ? " ↓" : " →"));
            if (c.Formato == TipoRalo.CaixaSifonada && c.Entradas > 0) m.Resumo.Add(c.Entradas + " entrada(s) " + Dn(c.DiametroEntradaMm));
            m.Resumo.Add("Grelha " + PecaUtil.M2(areaGrelha / 1e6));
            return m;
        }

        /// <summary>Grate (fine: with its openings; coarse/medium: plain) or tile tray. Rectangle
        /// <paramref name="ret"/> = x0,y0,x1,y1; or disc of radius <paramref name="raio"/>.</summary>
        private static void AddGrelha(PecaModelo m, RaloConfig c, double[] ret, double raio, double e)
        {
            List<P2> contorno = ret != null ? Peca.Rect(ret[0], ret[1], ret[2], ret[3]) : Peca.Circle(0, 0, raio, 40);
            if (c.Grelha == GrelhaRalo.Revestivel)
            {
                // tile tray: a thin gap all round (where the water goes in), the floor tile on top
                const double g = 4;
                List<P2> bandeja = ret != null ? Peca.Rect(ret[0] + g, ret[1] + g, ret[2] - g, ret[3] - g) : Peca.Circle(0, 0, raio - g, 40);
                double R = c.RebaixoMm;
                m.Pecas.Add(Peca.Laje("Bandeja", bandeja, null, -R - 3, -R, "grelha"));
                m.Pecas.Add(Peca.Laje("Revestimento", bandeja, null, -R, 0, "revest"));
                return;
            }
            var furos = ret != null ? FurosRet(c.Grelha, ret[0], ret[1], ret[2], ret[3], c.Linear) : FurosDisco(c.Grelha, raio);
            m.Pecas.Add(Peca.Laje("Grelha", contorno, furos, -e, 0, "grelha").Lod(false, false, true));
            m.Pecas.Add(Peca.Laje("Grelha (simplificada)", contorno, null, -e, 0, "grelha").Lod(true, true, false));
        }

        private static List<List<P2>> FurosRet(GrelhaRalo g, double x0, double y0, double x1, double y1, bool linear)
        {
            var r = new List<List<P2>>();
            const double borda = 5;
            double w = x1 - x0, h = y1 - y0;
            if (w < 2 * borda + 4 || h < 2 * borda + 4) return r;
            if (g == GrelhaRalo.Fendas)
            {
                // slots across the narrow side, distributed along the long side
                bool alongX = w >= h;
                double comp = alongX ? w : h, larg = alongX ? h : w;
                double passo = 12, fenda = 5;
                int n = (int)Math.Floor((comp - 2 * borda) / passo);
                while (n > 80) { passo *= 1.25; n = (int)Math.Floor((comp - 2 * borda) / passo); }
                double inicio = -((n - 1) * passo) / 2;
                double meia = larg / 2 - borda - 2;
                if (meia < 3) return r;
                double cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;
                for (int i = 0; i < n; i++)
                {
                    double t = inicio + i * passo;
                    r.Add(alongX ? Peca.Rect(cx + t - fenda / 2, cy - meia, cx + t + fenda / 2, cy + meia)
                                 : Peca.Rect(cx - meia, cy + t - fenda / 2, cx + meia, cy + t + fenda / 2));
                }
                return r;
            }
            double passoG = g == GrelhaRalo.Furos ? 14 : 13, tam = g == GrelhaRalo.Furos ? 7 : 9;
            int nx() => (int)Math.Floor((w - 2 * borda - tam) / passoG) + 1;
            int ny() => (int)Math.Floor((h - 2 * borda - tam) / passoG) + 1;
            while (nx() * ny() > 160) passoG *= 1.2;
            int cxN = Math.Max(0, nx()), cyN = Math.Max(0, ny());
            double ox = (x0 + x1) / 2 - (cxN - 1) * passoG / 2, oy = (y0 + y1) / 2 - (cyN - 1) * passoG / 2;
            for (int i = 0; i < cxN; i++)
                for (int j = 0; j < cyN; j++)
                {
                    double px = ox + i * passoG, py = oy + j * passoG;
                    r.Add(g == GrelhaRalo.Furos ? Peca.Circle(px, py, tam / 2, 8) : Peca.Rect(px - tam / 2, py - tam / 2, px + tam / 2, py + tam / 2));
                }
            return r;
        }

        private static List<List<P2>> FurosDisco(GrelhaRalo g, double raio)
        {
            var r = new List<List<P2>>();
            const double borda = 5;
            double rr = raio - borda;
            if (rr < 8) return r;
            if (g == GrelhaRalo.Fendas)
            {
                double passo = 12, fenda = 5;
                int n = (int)Math.Floor(2 * rr / passo);
                double inicio = -((n - 1) * passo) / 2;
                for (int i = 0; i < n; i++)
                {
                    double x = inicio + i * passo;
                    double xm = Math.Abs(x) + fenda / 2;
                    if (xm >= rr) continue;
                    double meia = Math.Sqrt(rr * rr - xm * xm) - 2;
                    if (meia < 4) continue;
                    r.Add(Peca.Rect(x - fenda / 2, -meia, x + fenda / 2, meia));
                }
                return r;
            }
            double passoG = g == GrelhaRalo.Furos ? 14 : 13, tam = g == GrelhaRalo.Furos ? 7 : 9;
            int k = (int)Math.Ceiling(rr / passoG);
            for (int i = -k; i <= k; i++)
                for (int j = -k; j <= k; j++)
                {
                    double px = i * passoG, py = j * passoG;
                    if (Math.Sqrt(px * px + py * py) + tam * 0.75 > rr) continue;
                    r.Add(g == GrelhaRalo.Furos ? Peca.Circle(px, py, tam / 2, 8) : Peca.Rect(px - tam / 2, py - tam / 2, px + tam / 2, py + tam / 2));
                }
            return r;
        }

        // ========================================================================= ralo no croqui
        private static P2 Soma(P2 a, P2 b, double k) => new P2(a.X + b.X * k, a.Y + b.Y * k);
        private static double Dist(P2 a, P2 b) => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
        private static P2 Dir(P2 a, P2 b) { double l = Dist(a, b); return l < 1e-9 ? new P2(1, 0) : new P2((b.X - a.X) / l, (b.Y - a.Y) / l); }
        private static P2 Esq(P2 d) => new P2(-d.Y, d.X);

        /// <summary>The path shortened by t0 at the start and t1 at the end (kept at least 1 mm long).</summary>
        private static List<P2> Aparar(List<P2> p, double t0, double t1)
        {
            var q = new List<P2>(p);
            if (q.Count < 2) return q;
            double l0 = Dist(q[0], q[1]), l1 = Dist(q[q.Count - 1], q[q.Count - 2]);
            q[0] = Soma(q[0], Dir(q[0], q[1]), Math.Min(t0, Math.Max(0, l0 - 1)));
            q[q.Count - 1] = Soma(q[q.Count - 1], Dir(q[q.Count - 1], q[q.Count - 2]), Math.Min(t1, Math.Max(0, l1 - 1)));
            return q;
        }

        /// <summary>The path moved sideways by dist (left of the direction positive), mitred at the corners.</summary>
        private static List<P2> Deslocar(List<P2> p, double dist)
        {
            if (Math.Abs(dist) < 1e-9 || p.Count < 2) return new List<P2>(p);
            var f = Faixa(p, 2 * Math.Abs(dist));
            // Faixa = left side (n points) then right side reversed (n points)
            var esq = f.Take(p.Count).ToList();
            var dir = f.Skip(p.Count).Reverse().ToList();
            return dist > 0 ? esq : dir;
        }

        /// <summary>Outline of a strip of width w along the path, mitred at the corners, square at the ends.</summary>
        private static List<P2> Faixa(List<P2> p, double w)
        {
            int n = p.Count;
            var e = new List<P2>(); var d = new List<P2>();
            for (int i = 0; i < n; i++)
            {
                if (i == 0 || i == n - 1)
                {
                    P2 dd = i == 0 ? Dir(p[0], p[1]) : Dir(p[n - 2], p[n - 1]), nn = Esq(dd);
                    e.Add(Soma(p[i], nn, w / 2)); d.Add(Soma(p[i], nn, -w / 2));
                    continue;
                }
                P2 d1 = Dir(p[i - 1], p[i]), d2 = Dir(p[i], p[i + 1]);
                P2 n1 = Esq(d1), n2 = Esq(d2);
                foreach (int lado in new[] { 1, -1 })
                {
                    P2 a = Soma(p[i], n1, lado * w / 2), b = Soma(p[i], n2, lado * w / 2);
                    double den = d1.X * d2.Y - d1.Y * d2.X;
                    P2 x;
                    if (Math.Abs(den) < 1e-9) x = b;
                    else
                    {
                        double t = ((b.X - a.X) * d2.Y - (b.Y - a.Y) * d2.X) / den;
                        x = Soma(a, d1, t);
                        // very sharp corner: limit the mitre
                        if (Dist(x, p[i]) > 4 * w) x = Soma(p[i], new P2((n1.X + n2.X) / 2, (n1.Y + n2.Y) / 2), lado * w / 2);
                    }
                    (lado == 1 ? e : d).Add(x);
                }
            }
            d.Reverse();
            e.AddRange(d);
            return e;
        }

        private static List<P2> Rot(P2 c, P2 dd, double a0, double a1, double b0, double b1)
        {
            P2 nn = Esq(dd);
            return new List<P2> { Soma(Soma(c, dd, a0), nn, b0), Soma(Soma(c, dd, a1), nn, b0), Soma(Soma(c, dd, a1), nn, b1), Soma(Soma(c, dd, a0), nn, b1) };
        }

        /// <summary>Grate openings along each stretch, kept away from the corners.</summary>
        private static List<List<P2>> FurosCaminho(GrelhaRalo g, List<P2> p, double larg)
        {
            var r = new List<List<P2>>();
            const double borda = 5;
            double meia = larg / 2 - borda - 2;
            if (meia < 3) return r;
            bool fendas = g == GrelhaRalo.Fendas;
            double passo = fendas ? 12 : g == GrelhaRalo.Furos ? 14 : 13, tam = fendas ? 5 : g == GrelhaRalo.Furos ? 7 : 9;
            double total = 0;
            for (int i = 0; i + 1 < p.Count; i++) total += Dist(p[i], p[i + 1]);
            int linhas = fendas ? 1 : Math.Max(1, (int)Math.Floor((2 * meia - tam) / passo) + 1);
            while (total / passo * linhas > 1500) passo *= 1.25;
            for (int i = 0; i + 1 < p.Count; i++)
            {
                P2 a = p[i], b = p[i + 1], dd = Dir(a, b);
                double L = Dist(a, b);
                double s0 = borda + (i > 0 ? larg / 2 + borda : 0), s1 = L - borda - (i + 2 < p.Count ? larg / 2 + borda : 0);
                if (s1 - s0 < tam) continue;
                int n = (int)Math.Floor((s1 - s0 - tam) / passo) + 1;
                double ini = (s0 + s1) / 2 - (n - 1) * passo / 2;
                for (int k = 0; k < n; k++)
                {
                    double s = ini + k * passo;
                    if (fendas) { r.Add(Rot(a, dd, s - tam / 2, s + tam / 2, -meia, meia)); continue; }
                    for (int j = 0; j < linhas; j++)
                    {
                        double y = -(linhas - 1) * passo / 2 + j * passo;
                        P2 c = Soma(Soma(a, dd, s), Esq(dd), y);
                        r.Add(g == GrelhaRalo.Furos ? Peca.Circle(c.X, c.Y, tam / 2, 8) : Rot(c, dd, -tam / 2, tam / 2, -tam / 2, tam / 2));
                    }
                }
            }
            return r;
        }

        /// <summary>Linear drain along the drawn path: rim, grate (or tile tray), body and one outlet.</summary>
        private static PecaModelo Croqui(RaloConfig c, PecaModelo m)
        {
            var p = c.Eixo();
            double e = c.EspessuraGrelhaMm, a = c.MolduraMm, H = Math.Max(c.AlturaCorpoMm, e + 15), W = c.LarguraMm, ds = c.DiametroSaidaMm;
            double total = 0;
            for (int i = 0; i + 1 < p.Count; i++) total += Dist(p[i], p[i + 1]);

            var dentro = Aparar(p, a, a);
            if (a > 0.5) m.Pecas.Add(Peca.Laje("Moldura", Faixa(p, W), new[] { Faixa(dentro, W - 2 * a) }, -e, 0, "grelha"));
            if (c.Grelha == GrelhaRalo.Revestivel)
            {
                const double g = 4;
                var bandeja = Faixa(Aparar(p, a + g, a + g), W - 2 * a - 2 * g);
                m.Pecas.Add(Peca.Laje("Bandeja", bandeja, null, -c.RebaixoMm - 3, -c.RebaixoMm, "grelha"));
                m.Pecas.Add(Peca.Laje("Revestimento", bandeja, null, -c.RebaixoMm, 0, "revest"));
            }
            else
            {
                var contorno = Faixa(dentro, W - 2 * a);
                m.Pecas.Add(Peca.Laje("Grelha", contorno, FurosCaminho(c.Grelha, dentro, W - 2 * a), -e, 0, "grelha").Lod(false, false, true));
                m.Pecas.Add(Peca.Laje("Grelha (simplificada)", contorno, null, -e, 0, "grelha").Lod(true, true, false));
            }
            double inset = Math.Min(6, W * 0.05);
            m.Pecas.Add(Peca.Laje("Corpo", Faixa(Aparar(p, inset, inset), W - 2 * inset), null, -H, -e, "corpo"));

            // outlet: at the middle of the path, or at DistanciaSaida from its start / end
            double sOut = c.Posicao == PosicaoSaida.Centro ? total / 2 : c.Posicao == PosicaoSaida.Esquerda ? Math.Min(c.DistanciaSaidaMm, total / 2) : Math.Max(total - c.DistanciaSaidaMm, total / 2);
            P2 ponto = p[0], dir = Dir(p[0], p[1]);
            double acc = 0;
            for (int i = 0; i + 1 < p.Count; i++)
            {
                double L = Dist(p[i], p[i + 1]);
                if (acc + L >= sOut - 1e-6 || i + 2 == p.Count) { dir = Dir(p[i], p[i + 1]); ponto = Soma(p[i], dir, Math.Max(0, Math.Min(L, sOut - acc))); break; }
                acc += L;
            }
            if (c.SaidaVertical)
            {
                if (ds > W - 4) m.Avisos.Add("A saída " + Dn(ds) + " é mais larga que o corpo do ralo: confira a largura.");
                m.Pecas.Add(Tubo("Saída", new V3(ponto.X, ponto.Y, -H), new V3(0, 0, -1), ds, c.ComprimentoSaidaMm, true));
            }
            else
            {
                double hc = H - e, dsH = Math.Min(ds, hc - 4), zc = -e - hc / 2;
                if (dsH < ds) m.Avisos.Add("A saída horizontal " + Dn(ds) + " não cabe na altura do corpo: aumente a altura.");
                V3 o, d;
                if (c.Posicao == PosicaoSaida.Esquerda) { P2 d0 = Dir(p[1], p[0]); o = new V3(p[0].X, p[0].Y, zc); d = new V3(d0.X, d0.Y, 0); }
                else if (c.Posicao == PosicaoSaida.Direita) { P2 d1 = Dir(p[p.Count - 2], p[p.Count - 1]); o = new V3(p[p.Count - 1].X, p[p.Count - 1].Y, zc); d = new V3(d1.X, d1.Y, 0); }
                else { P2 nn = Esq(dir); o = new V3(ponto.X - nn.X * W / 2, ponto.Y - nn.Y * W / 2, zc); d = new V3(-nn.X, -nn.Y, 0); }
                m.Pecas.Add(Tubo("Saída", o, d, Math.Max(20, dsH), c.ComprimentoSaidaMm, true));
            }
            m.Resumo.Add((p.Count - 1) + " trecho(s), " + PecaUtil.Mm(total) + " × " + PecaUtil.Mm(W) + " mm");
            m.Resumo.Add("Saída " + Dn(ds) + (c.SaidaVertical ? " ↓" : " →"));
            m.Resumo.Add("Grelha " + PecaUtil.M2(total * W / 1e6));
            return m;
        }

        /// <summary>Dome drain (roofs, gutters): flange on the slab, dome of stacked rings, outlet pipe.</summary>
        private static PecaModelo Hemisferico(RaloConfig c, PecaModelo m)
        {
            double D = c.LarguraMm, r = D / 2, e = c.EspessuraGrelhaMm;
            double flange = r + Math.Max(15, c.MolduraMm * 3);
            m.Pecas.Add(Peca.Laje("Aba", Peca.Circle(0, 0, flange, 40), null, 0, e, "grelha"));
            double hDomo = r * 0.9;
            int aneis = 5;
            for (int i = 0; i < aneis; i++)
            {
                double z0 = e + hDomo * i / aneis, z1 = e + hDomo * (i + 1) / aneis;
                double zm = (z0 + z1) / 2 - e;
                double ri = Math.Sqrt(Math.Max(1, r * r - (zm / hDomo * r) * (zm / hDomo * r)));
                var anel = Peca.Laje("Domo " + (i + 1), Peca.Circle(0, 0, ri, 32), i == aneis - 1 ? null : new[] { Peca.Circle(0, 0, Math.Max(1, ri - 4), 32) }, z0, z1 - 1.5, "grelha");
                m.Pecas.Add(anel);
            }
            double ds = Math.Min(c.DiametroSaidaMm, D - 4);
            m.Pecas.Add(Tubo("Saída", new V3(0, 0, 0), new V3(0, 0, -1), ds, Math.Max(c.AlturaCorpoMm, 40), true));
            m.Resumo.Add("Ø" + PecaUtil.Mm(D) + " mm");
            m.Resumo.Add("Saída " + Dn(c.DiametroSaidaMm) + " ↓");
            return m;
        }

        /// <summary>A pipe stub from <paramref name="o"/> along <paramref name="dir"/>, with a pipe connector at its far end.</summary>
        private static Peca Tubo(string nome, V3 o, V3 dir, double diametro, double comprimento, bool saida)
        {
            V3 d = dir.Unit();
            V3 u = Math.Abs(d.Z) < 0.9 ? V3.Ez.Cross(d).Unit() : V3.Ex;
            V3 v = d.Cross(u).Unit();   // u × v = d
            var p = new Peca { Nome = nome, O = o, U = u, V = v, Depth = comprimento, Slot = "corpo", Conector = new ConectorPeca { DiametroMm = diametro, Saida = saida } };
            p.Loops.Add(Peca.Circle(0, 0, diametro / 2, 24));
            return p;
        }
    }
}
