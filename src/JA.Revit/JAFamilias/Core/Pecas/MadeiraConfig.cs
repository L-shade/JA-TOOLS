using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using JAEsquadrias.Infrastructure;

namespace JAEsquadrias.Core
{
    public enum UsoMadeira { PainelRipado = 0, Pergolado = 1 }

    /// <summary>One line of the cutting list of a wood piece (for the Lista de Compras).</summary>
    public sealed class PecaMadeira
    {
        public string Nome;
        public double LarguraMm, AlturaMm, ComprimentoMm;
        public int Quantidade;
        public double MetrosLineares => Quantidade * ComprimentoMm / 1000.0;
        public double VolumeM3 => Quantidade * LarguraMm * AlturaMm * ComprimentoMm / 1e9;
        public string Secao => PecaUtil.Mm(LarguraMm) + " × " + PecaUtil.Mm(AlturaMm) + " mm";
    }

    /// <summary>
    /// Wood pieces. Painel ripado: point-based, origin at the middle of its bottom edge on the wall face,
    /// X along the wall, +Y away from the wall (into the room). Pergolado: point-based, origin at the
    /// first corner on the floor, X along the first side (Comprimento), +Y along the depth.
    /// </summary>
    [DataContract]
    public sealed class MadeiraConfig : IPecaConfig
    {
        public TipoPeca Tipo => TipoPeca.Madeira;

        [DataMember(Order = 1)] public string Codigo { get; set; } = "RP01";
        [DataMember(Order = 2)] public string Nome { get; set; } = "";
        [DataMember(Order = 3)] public string ModeloId { get; set; } = "";

        [DataMember(Order = 10)] public UsoMadeira Uso { get; set; }
        /// <summary>Painel: width along the wall. Pergolado: length (first side).</summary>
        [DataMember(Order = 11)] public double LarguraMm { get; set; } = 1200;
        /// <summary>Painel: height. Pergolado: clear height under the beams.</summary>
        [DataMember(Order = 12)] public double AlturaMm { get; set; } = 2600;
        /// <summary>Pergolado: depth (second side).</summary>
        [DataMember(Order = 13)] public double ProfundidadeMm { get; set; } = 3000;

        // painel ripado
        [DataMember(Order = 20)] public double RipaLarguraMm { get; set; } = 40;
        [DataMember(Order = 21)] public double RipaEspessuraMm { get; set; } = 20;
        [DataMember(Order = 22)] public double EspacamentoMm { get; set; } = 20;      // gap between ripas
        [DataMember(Order = 23)] public bool Horizontal { get; set; }
        [DataMember(Order = 24)] public bool Fundo { get; set; } = true;
        [DataMember(Order = 25)] public double FundoEspessuraMm { get; set; } = 15;
        [DataMember(Order = 26)] public double ElevacaoMm { get; set; }               // bottom of the panel above the floor

        // pergolado
        [DataMember(Order = 30)] public double PilarMm { get; set; } = 120;
        [DataMember(Order = 31)] public double VigaLarguraMm { get; set; } = 60;
        [DataMember(Order = 32)] public double VigaAlturaMm { get; set; } = 200;
        [DataMember(Order = 33)] public double CaibroLarguraMm { get; set; } = 50;
        [DataMember(Order = 34)] public double CaibroAlturaMm { get; set; } = 150;
        [DataMember(Order = 35)] public double EspacamentoCaibroMm { get; set; } = 400;  // between axes
        [DataMember(Order = 36)] public double BeiralMm { get; set; } = 300;
        [DataMember(Order = 37)] public double VaoMaximoMm { get; set; } = 3000;        // between posts
        [DataMember(Order = 38)] public bool ApoiadoNaParede { get; set; }              // side y = 0 against a wall: no posts there

        [DataMember(Order = 40)] public string MaterialMadeira { get; set; } = "Madeira cumaru";
        [DataMember(Order = 41)] public string CorMadeira { get; set; } = "8A5A3B";
        [DataMember(Order = 42)] public string MaterialFundo { get; set; } = "MDF preto";
        [DataMember(Order = 43)] public string CorFundo { get; set; } = "2B2B2B";

        public MadeiraConfig Normalized()
        {
            bool pg = Uso == UsoMadeira.Pergolado;
            if (string.IsNullOrWhiteSpace(Codigo)) Codigo = pg ? "PG01" : "RP01";
            if (Nome == null) Nome = "";
            if (ModeloId == null) ModeloId = "";
            LarguraMm = PecaUtil.Clamp(LarguraMm <= 0 ? (pg ? 4000 : 1200) : LarguraMm, 100, 30000);
            AlturaMm = PecaUtil.Clamp(AlturaMm <= 0 ? (pg ? 2500 : 2600) : AlturaMm, 100, 8000);
            ProfundidadeMm = PecaUtil.Clamp(ProfundidadeMm <= 0 ? 3000 : ProfundidadeMm, 300, 20000);
            RipaLarguraMm = PecaUtil.Clamp(RipaLarguraMm <= 0 ? 40 : RipaLarguraMm, 5, 500);
            RipaEspessuraMm = PecaUtil.Clamp(RipaEspessuraMm <= 0 ? 20 : RipaEspessuraMm, 3, 200);
            EspacamentoMm = PecaUtil.Clamp(EspacamentoMm, 0, 1000);
            FundoEspessuraMm = PecaUtil.Clamp(FundoEspessuraMm <= 0 ? 15 : FundoEspessuraMm, 3, 60);
            ElevacaoMm = PecaUtil.Clamp(ElevacaoMm, 0, 6000);
            PilarMm = PecaUtil.Clamp(PilarMm <= 0 ? 120 : PilarMm, 40, 600);
            VigaLarguraMm = PecaUtil.Clamp(VigaLarguraMm <= 0 ? 60 : VigaLarguraMm, 20, 400);
            VigaAlturaMm = PecaUtil.Clamp(VigaAlturaMm <= 0 ? 200 : VigaAlturaMm, 30, 800);
            CaibroLarguraMm = PecaUtil.Clamp(CaibroLarguraMm <= 0 ? 50 : CaibroLarguraMm, 10, 300);
            CaibroAlturaMm = PecaUtil.Clamp(CaibroAlturaMm <= 0 ? 150 : CaibroAlturaMm, 10, 600);
            EspacamentoCaibroMm = PecaUtil.Clamp(EspacamentoCaibroMm <= 0 ? 400 : EspacamentoCaibroMm, CaibroLarguraMm + 5, 3000);
            BeiralMm = PecaUtil.Clamp(BeiralMm, 0, 1500);
            VaoMaximoMm = PecaUtil.Clamp(VaoMaximoMm <= 0 ? 3000 : VaoMaximoMm, 500, 10000);
            if (string.IsNullOrWhiteSpace(MaterialMadeira)) MaterialMadeira = "Madeira cumaru";
            if (string.IsNullOrWhiteSpace(CorMadeira)) CorMadeira = "8A5A3B";
            if (string.IsNullOrWhiteSpace(MaterialFundo)) MaterialFundo = "MDF preto";
            if (string.IsNullOrWhiteSpace(CorFundo)) CorFundo = "2B2B2B";
            return this;
        }

        public string NomeUso => Uso == UsoMadeira.Pergolado ? "Pergolado" : "Painel ripado";
        public string ToJson() => Json.Serialize(this);
        public IPecaConfig Copiar() => Json.Deserialize<MadeiraConfig>(ToJson()).Normalized();

        public IList<MaterialDef> Materiais()
        {
            var l = new List<MaterialDef> { new MaterialDef { Key = "madeira", Parametro = "Material da madeira", Nome = MaterialMadeira.Trim(), Hex = CorMadeira } };
            if (Uso == UsoMadeira.PainelRipado && Fundo) l.Add(new MaterialDef { Key = "fundo", Parametro = "Material do fundo", Nome = MaterialFundo.Trim(), Hex = CorFundo });
            return l;
        }

        public string MaterialPrincipal() => MaterialMadeira;

        public string Dimensoes() => Uso == UsoMadeira.Pergolado
            ? PecaUtil.Mm(LarguraMm) + " x " + PecaUtil.Mm(ProfundidadeMm) + " mm, vão livre " + PecaUtil.Mm(AlturaMm) + " mm"
            : PecaUtil.Mm(LarguraMm) + " x " + PecaUtil.Mm(AlturaMm) + " mm, ripas " + PecaUtil.Mm(RipaLarguraMm) + " x " + PecaUtil.Mm(RipaEspessuraMm) + " a cada " + PecaUtil.Mm(RipaLarguraMm + EspacamentoMm) + " mm";

        public string Descricao()
        {
            string d = Uso == UsoMadeira.Pergolado
                ? "Pergolado " + Dimensoes() + "; pilares " + PecaUtil.Mm(PilarMm) + " x " + PecaUtil.Mm(PilarMm) + ", vigas " + PecaUtil.Mm(VigaLarguraMm) + " x " + PecaUtil.Mm(VigaAlturaMm) +
                  ", caibros " + PecaUtil.Mm(CaibroLarguraMm) + " x " + PecaUtil.Mm(CaibroAlturaMm) + " a cada " + PecaUtil.Mm(EspacamentoCaibroMm) + " mm" + (ApoiadoNaParede ? ", apoiado na parede" : "") + "; " + MaterialMadeira.ToLowerInvariant()
                : "Painel ripado " + (Horizontal ? "horizontal " : "vertical ") + Dimensoes() + "; " + MaterialMadeira.ToLowerInvariant() + (Fundo ? ", fundo " + MaterialFundo.ToLowerInvariant() + " " + PecaUtil.Mm(FundoEspessuraMm) + " mm" : "");
            return string.IsNullOrWhiteSpace(Nome) ? d : Nome.Trim() + " – " + d;
        }

        public PecaModelo Build()
        {
            var c = (MadeiraConfig)Copiar();
            return c.Uso == UsoMadeira.Pergolado ? c.BuildPergolado() : c.BuildPainel();
        }

        /// <summary>Positions (start of each piece) of n pieces of width w spread over L with gaps g, centred.</summary>
        private static List<double> Distribuir(double L, double w, double g, out int n)
        {
            n = Math.Max(1, (int)Math.Floor((L + g) / (w + g) + 1e-9));
            double usado = n * w + (n - 1) * g, x0 = (L - usado) / 2;
            var r = new List<double>();
            for (int i = 0; i < n; i++) r.Add(x0 + i * (w + g));
            return r;
        }

        /// <summary>The ripas, the backing board and the cutting list.</summary>
        public List<PecaMadeira> ListaDeCorte()
        {
            var l = new List<PecaMadeira>();
            if (Uso == UsoMadeira.PainelRipado)
            {
                Distribuir(Horizontal ? AlturaMm : LarguraMm, RipaLarguraMm, EspacamentoMm, out int n);
                l.Add(new PecaMadeira { Nome = "Ripa", LarguraMm = RipaLarguraMm, AlturaMm = RipaEspessuraMm, ComprimentoMm = Horizontal ? LarguraMm : AlturaMm, Quantidade = n });
                return l;
            }
            Pergolado(out var postes, out var vigas, out var caibros);
            if (postes.Count > 0) l.Add(new PecaMadeira { Nome = "Pilar", LarguraMm = PilarMm, AlturaMm = PilarMm, ComprimentoMm = AlturaMm, Quantidade = postes.Count });
            foreach (var g in vigas.GroupBy(v => Math.Round(v.Item2 - v.Item1)))
                l.Add(new PecaMadeira { Nome = "Viga", LarguraMm = VigaLarguraMm, AlturaMm = VigaAlturaMm, ComprimentoMm = g.Key, Quantidade = g.Count() });
            l.Add(new PecaMadeira { Nome = "Caibro", LarguraMm = CaibroLarguraMm, AlturaMm = CaibroAlturaMm, ComprimentoMm = Math.Round(caibros.Y1 - caibros.Y0), Quantidade = caibros.Xs.Count });
            return l;
        }

        private PecaModelo BuildPainel()
        {
            var m = new PecaModelo();
            double W = LarguraMm, H = AlturaMm, z0 = ElevacaoMm, y0 = Fundo ? FundoEspessuraMm : 0, y1 = y0 + RipaEspessuraMm;
            if (Fundo) m.Pecas.Add(Peca.Box("Fundo", -W / 2, 0, z0, W / 2, FundoEspessuraMm, z0 + H, "fundo"));
            var pos = Distribuir(Horizontal ? H : W, RipaLarguraMm, EspacamentoMm, out int n);
            foreach (double p in pos)
            {
                if (Horizontal) m.Pecas.Add(Peca.Box("Ripa", -W / 2, y0, z0 + p, W / 2, y1, z0 + p + RipaLarguraMm, "madeira"));
                else m.Pecas.Add(Peca.Box("Ripa", -W / 2 + p, y0, z0, -W / 2 + p + RipaLarguraMm, y1, z0 + H, "madeira"));
            }
            // plan: the outline of the panel
            m.Planta.Add(new LinhaPeca(new P2(-W / 2, y1), new P2(W / 2, y1), false));
            m.Planta.Add(new LinhaPeca(new P2(-W / 2, 0), new P2(-W / 2, y1), false));
            m.Planta.Add(new LinhaPeca(new P2(W / 2, 0), new P2(W / 2, y1), false));
            double ml = n * (Horizontal ? W : H) / 1000.0;
            m.AreaPedra = W * H / 1e6;
            m.Resumo.Add(n + " ripas = " + PecaUtil.M(ml));
            m.Resumo.Add("Painel " + PecaUtil.M2(m.AreaPedra));
            if (Horizontal ? W > 6000 : H > 6000) m.Avisos.Add("Ripas com mais de 6 m: confira o comprimento comercial (emendas).");
            return m;
        }

        private struct Caibros { public List<double> Xs; public double Y0, Y1; }

        /// <summary>Pergolado layout: post centres (x, y), beams (x0, x1, y centre), caibros.</summary>
        private void Pergolado(out List<P2> postes, out List<Tuple<double, double, double>> vigas, out Caibros caibros)
        {
            double L = LarguraMm, D = ProfundidadeMm, P = PilarMm;
            postes = new List<P2>();
            vigas = new List<Tuple<double, double, double>>();
            int vaos = Math.Max(1, (int)Math.Ceiling((L - P) / VaoMaximoMm - 1e-9));
            var xs = Enumerable.Range(0, vaos + 1).Select(i => P / 2 + (L - P) * i / vaos).ToList();
            double yFrente = D - P / 2, yAtras = P / 2;
            foreach (double x in xs) postes.Add(new P2(x, yFrente));
            if (!ApoiadoNaParede) foreach (double x in xs) postes.Add(new P2(x, yAtras));
            // beams along X, on the posts (the wall side: against the wall, no overhang)
            vigas.Add(Tuple.Create(-BeiralMm, L + BeiralMm, yFrente));
            vigas.Add(ApoiadoNaParede ? Tuple.Create(0.0, L, VigaLarguraMm / 2) : Tuple.Create(-BeiralMm, L + BeiralMm, yAtras));
            // caibros along Y over the beams
            double c = CaibroLarguraMm;
            int n = Math.Max(2, (int)Math.Ceiling((L - c) / EspacamentoCaibroMm - 1e-9) + 1);
            caibros = new Caibros
            {
                Xs = Enumerable.Range(0, n).Select(i => (L - c) * i / (n - 1)).ToList(),
                Y0 = ApoiadoNaParede ? 0 : -BeiralMm,
                Y1 = D + BeiralMm
            };
        }

        private PecaModelo BuildPergolado()
        {
            var m = new PecaModelo();
            double H = AlturaMm, P = PilarMm, vw = VigaLarguraMm, vh = VigaAlturaMm, cw = CaibroLarguraMm, ch = CaibroAlturaMm;
            Pergolado(out var postes, out var vigas, out var caibros);
            foreach (P2 q in postes) m.Pecas.Add(Peca.Box("Pilar", q.X - P / 2, q.Y - P / 2, 0, q.X + P / 2, q.Y + P / 2, H, "madeira"));
            foreach (var v in vigas) m.Pecas.Add(Peca.Box("Viga", v.Item1, v.Item3 - vw / 2, H, v.Item2, v.Item3 + vw / 2, H + vh, "madeira"));
            foreach (double x in caibros.Xs) m.Pecas.Add(Peca.Box("Caibro", x, caibros.Y0, H + vh, x + cw, caibros.Y1, H + vh + ch, "madeira"));
            // plan: posts (cut) and the outline of the caibros above (dashed)
            foreach (P2 q in postes)
            {
                var r = Peca.Rect(q.X - P / 2, q.Y - P / 2, q.X + P / 2, q.Y + P / 2);
                for (int i = 0; i < 4; i++) m.Planta.Add(new LinhaPeca(r[i], r[(i + 1) % 4], false));
            }
            double xa = caibros.Xs.First(), xb = caibros.Xs.Last() + cw;
            m.Planta.Add(new LinhaPeca(new P2(xa, caibros.Y0), new P2(xb, caibros.Y0), true));
            m.Planta.Add(new LinhaPeca(new P2(xb, caibros.Y0), new P2(xb, caibros.Y1), true));
            m.Planta.Add(new LinhaPeca(new P2(xb, caibros.Y1), new P2(xa, caibros.Y1), true));
            m.Planta.Add(new LinhaPeca(new P2(xa, caibros.Y1), new P2(xa, caibros.Y0), true));
            m.AreaPedra = LarguraMm * ProfundidadeMm / 1e6;
            double vol = ListaDeCorte().Sum(p => p.VolumeM3);
            m.Resumo.Add(postes.Count + " pilares, " + caibros.Xs.Count + " caibros");
            m.Resumo.Add("Madeira ≈ " + vol.ToString("0.00", PecaUtil.Pt) + " m³");
            if (VigaAlturaMm < (VaoMaximoMm / 20)) m.Avisos.Add("Viga baixa para o vão entre pilares (regra prática: altura ≥ vão / 20). Confirme com o cálculo estrutural.");
            if (caibros.Y1 - caibros.Y0 > 6000) m.Avisos.Add("Caibros com mais de 6 m: confira o comprimento comercial ou preveja emendas sobre uma viga intermediária.");
            return m;
        }
    }
}
