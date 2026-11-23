using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using JAEsquadrias.Infrastructure;

namespace JAEsquadrias.Core
{
    public enum CaimentoChapim { Plano = 0, UmLado = 1, DuasAguas = 2 }

    /// <summary>
    /// Chapim (coping stone on top of walls, parapets and muretas): a profile extruded along the
    /// wall (line-based family, so it follows the wall length), with overhangs (abas), drip
    /// grooves (pingadeiras) and slope (caimento). Local coordinates (mm): X along the line,
    /// Y across (positive = exterior side), Z up from the top of the wall.
    /// </summary>
    [DataContract]
    public sealed class ChapimConfig : IPecaConfig
    {
        public TipoPeca Tipo => TipoPeca.Chapim;

        [DataMember(Order = 1)] public string Codigo { get; set; } = "CH01";
        [DataMember(Order = 2)] public string Nome { get; set; } = "";
        [DataMember(Order = 3)] public string ModeloId { get; set; } = "";

        [DataMember(Order = 10)] public double EspessuraMuroMm { get; set; } = 150;
        [DataMember(Order = 11)] public double AbaExternaMm { get; set; } = 30;
        [DataMember(Order = 12)] public double AbaInternaMm { get; set; } = 30;
        [DataMember(Order = 13)] public double EspessuraMm { get; set; } = 30;
        /// <summary>Length when placed by hand (applied to walls it follows the wall).</summary>
        [DataMember(Order = 14)] public double ComprimentoMm { get; set; } = 3000;

        [DataMember(Order = 20)] public CaimentoChapim Caimento { get; set; } = CaimentoChapim.UmLado;
        /// <summary>Slope in percent.</summary>
        [DataMember(Order = 21)] public double InclinacaoPct { get; set; } = 2;
        /// <summary>One-side slope: water runs to the interior side (default) or to the exterior.</summary>
        [DataMember(Order = 22)] public bool CaiParaDentro { get; set; } = true;

        [DataMember(Order = 30)] public bool Pingadeira { get; set; } = true;
        [DataMember(Order = 31)] public double PingadeiraLarguraMm { get; set; } = 8;
        [DataMember(Order = 32)] public double PingadeiraProfundidadeMm { get; set; } = 6;
        /// <summary>Distance from the edge of the overhang to the groove.</summary>
        [DataMember(Order = 33)] public double PingadeiraDistanciaMm { get; set; } = 12;

        /// <summary>Length of each stone piece, for the joints in plan (0 = no joints).</summary>
        [DataMember(Order = 40)] public double PecaComprimentoMm { get; set; } = 0;

        /// <summary>Curved walls: radius (mm) of the wall centre line; 0 = straight (line-based family).</summary>
        [DataMember(Order = 45)] public double RaioMm { get; set; }
        /// <summary>Curved: the exterior of the wall (+Y of the profile) is on the outside of the arc.</summary>
        [DataMember(Order = 46)] public bool ExteriorParaFora { get; set; } = true;
        /// <summary>Curved: default angle of the arc (each copy has its own "Ângulo do arco").</summary>
        [DataMember(Order = 47)] public double AnguloGraus { get; set; } = 90;

        [DataMember(Order = 50)] public string MaterialPedra { get; set; } = "Granito cinza andorinha";
        [DataMember(Order = 51)] public string CorPedra { get; set; } = "9C9A96";

        public ChapimConfig Normalized()
        {
            if (string.IsNullOrWhiteSpace(Codigo)) Codigo = "CH01";
            if (Nome == null) Nome = "";
            if (ModeloId == null) ModeloId = "";
            EspessuraMuroMm = PecaUtil.Clamp(EspessuraMuroMm, 50, 1000);
            AbaExternaMm = PecaUtil.Clamp(AbaExternaMm, 0, 300);
            AbaInternaMm = PecaUtil.Clamp(AbaInternaMm, 0, 300);
            EspessuraMm = PecaUtil.Clamp(EspessuraMm <= 0 ? 30 : EspessuraMm, 10, 150);
            ComprimentoMm = PecaUtil.Clamp(ComprimentoMm <= 0 ? 3000 : ComprimentoMm, 100, 50000);
            InclinacaoPct = PecaUtil.Clamp(InclinacaoPct, 0, 30);
            PingadeiraLarguraMm = PecaUtil.Clamp(PingadeiraLarguraMm <= 0 ? 8 : PingadeiraLarguraMm, 3, 30);
            PingadeiraProfundidadeMm = PecaUtil.Clamp(PingadeiraProfundidadeMm <= 0 ? 6 : PingadeiraProfundidadeMm, 2, 20);
            PingadeiraDistanciaMm = PecaUtil.Clamp(PingadeiraDistanciaMm <= 0 ? 12 : PingadeiraDistanciaMm, 3, 100);
            PecaComprimentoMm = PecaComprimentoMm <= 0 ? 0 : PecaUtil.Clamp(PecaComprimentoMm, 200, 5000);
            RaioMm = RaioMm <= 0 ? 0 : PecaUtil.Clamp(RaioMm, 100, 1000000);
            AnguloGraus = AnguloGraus <= 0 ? 90 : PecaUtil.Clamp(AnguloGraus, 0.5, 359.5);
            if (string.IsNullOrWhiteSpace(MaterialPedra)) MaterialPedra = "Granito cinza andorinha";
            if (string.IsNullOrWhiteSpace(CorPedra)) CorPedra = "9C9A96";
            return this;
        }

        public string ToJson() => Json.Serialize(this);
        public IPecaConfig Copiar() => Json.Deserialize<ChapimConfig>(ToJson()).Normalized();

        public IList<MaterialDef> Materiais() => new List<MaterialDef>
        {
            new MaterialDef { Key = "pedra", Parametro = "Material da pedra", Nome = MaterialPedra.Trim(), Hex = CorPedra },
        };

        public string MaterialPrincipal() => MaterialPedra;

        public double LarguraTotalMm => EspessuraMuroMm + AbaExternaMm + AbaInternaMm;

        public bool Curvo => RaioMm > 0;

        public string Dimensoes() =>
            "largura " + PecaUtil.Mm(LarguraTotalMm) + " x " + PecaUtil.Mm(EspessuraMm) + " mm (muro " + PecaUtil.Mm(EspessuraMuroMm) + ")" +
            (Curvo ? ", curvo R " + PecaUtil.Mm(RaioMm) + " mm" : "");

        public string Descricao()
        {
            var parts = new List<string> { "Chapim " + Dimensoes() };
            if (Caimento == CaimentoChapim.UmLado) parts.Add("caimento " + InclinacaoPct.ToString("0.#", PecaUtil.Pt) + "% para " + (CaiParaDentro ? "dentro" : "fora"));
            if (Caimento == CaimentoChapim.DuasAguas) parts.Add("duas águas " + InclinacaoPct.ToString("0.#", PecaUtil.Pt) + "%");
            if (Pingadeira) parts.Add("com pingadeira");
            if (PecaComprimentoMm > 0) parts.Add("peças de " + PecaUtil.Mm(PecaComprimentoMm) + " mm");
            parts.Add(MaterialPedra);
            string d = string.Join("; ", parts);
            return string.IsNullOrWhiteSpace(Nome) ? d : Nome.Trim() + " – " + d;
        }

        /// <summary>Cross-section (Y across, Z up), counter-clockwise.</summary>
        public List<P2> Perfil(out List<string> avisos)
        {
            avisos = new List<string>();
            double yE = EspessuraMuroMm / 2 + AbaExternaMm, yI = -(EspessuraMuroMm / 2 + AbaInternaMm);
            double w = yE - yI, e = EspessuraMm, i = InclinacaoPct / 100.0;
            double zE = e, zI = e, zPeak = double.NaN;
            if (Caimento == CaimentoChapim.UmLado)
            {
                if (CaiParaDentro) zE = e + i * w; else zI = e + i * w;
            }
            else if (Caimento == CaimentoChapim.DuasAguas) zPeak = e + i * w / 2;

            var pts = new List<P2> { new P2(yI, 0) };
            double g = PingadeiraLarguraMm, pd = Math.Min(PingadeiraProfundidadeMm, e * 0.5), d = PingadeiraDistanciaMm;
            if (Pingadeira)
            {
                if (AbaInternaMm >= d + g + 3) pts.AddRange(new[] { new P2(yI + d, 0), new P2(yI + d, pd), new P2(yI + d + g, pd), new P2(yI + d + g, 0) });
                else if (AbaInternaMm > 0) avisos.Add("Aba interna curta demais para a pingadeira.");
                if (AbaExternaMm >= d + g + 3) pts.AddRange(new[] { new P2(yE - d - g, 0), new P2(yE - d - g, pd), new P2(yE - d, pd), new P2(yE - d, 0) });
                else if (AbaExternaMm > 0) avisos.Add("Aba externa curta demais para a pingadeira.");
            }
            pts.Add(new P2(yE, 0));
            pts.Add(new P2(yE, zE));
            if (!double.IsNaN(zPeak)) pts.Add(new P2((yE + yI) / 2, zPeak));
            pts.Add(new P2(yI, zI));
            return pts;
        }

        public PecaModelo Build()
        {
            var c = (ChapimConfig)Copiar();
            var m = new PecaModelo();
            var perfil = c.Perfil(out var avisos);
            m.Avisos.AddRange(avisos);
            if (c.Curvo)
            {
                // curved wall: the same section turned about the centre of the arc (profile in the
                // radial/vertical plane, +Y of the section = exterior side of the wall)
                double s = c.ExteriorParaFora ? 1 : -1;
                double rMin = c.RaioMm - c.EspessuraMuroMm / 2 - (s > 0 ? c.AbaInternaMm : c.AbaExternaMm);   // inner edge of the stone
                if (rMin <= 5) throw new InvalidOperationException("Raio da parede curva pequeno demais para a largura do chapim.");
                var prof = perfil.Select(q => new P2(c.RaioMm + s * q.X, q.Y)).ToList();
                var rv = new Peca { Nome = "Chapim curvo", O = V3.Zero, U = V3.Ex, V = V3.Ez, Slot = "pedra", Revolucao = true, AnguloGraus = c.AnguloGraus };
                rv.Loops.Add(prof);
                m.Pecas.Add(rv);
                if (c.PecaComprimentoMm > 0) m.Avisos.Add("Chapim curvo: as juntas das peças não são desenhadas em planta.");
                m.AreaPedra = c.LarguraTotalMm / 1000.0;
                m.Resumo.Add("Largura " + PecaUtil.Mm(c.LarguraTotalMm) + " mm");
                m.Resumo.Add("Curvo, raio " + PecaUtil.Mm(c.RaioMm) + " mm no eixo do muro");
                m.Resumo.Add("Pedra " + PecaUtil.M2(c.LarguraTotalMm / 1000) + " por metro");
                return m;
            }
            // profile in the YZ plane, extruded along +X (follows the line length in Revit)
            var p = new Peca { Nome = "Chapim", O = V3.Zero, U = V3.Ey, V = V3.Ez, Depth = c.ComprimentoMm, Slot = "pedra", AcompanhaComprimento = true };
            p.Loops.Add(perfil);
            m.Pecas.Add(p);

            if (c.PecaComprimentoMm > 0)
            {
                double yE = c.EspessuraMuroMm / 2 + c.AbaExternaMm, yI = -(c.EspessuraMuroMm / 2 + c.AbaInternaMm);
                for (double x = c.PecaComprimentoMm; x < c.ComprimentoMm - 1; x += c.PecaComprimentoMm)
                    m.Planta.Add(new LinhaPeca(new P2(x, yI), new P2(x, yE), false));
            }
            // per metre of wall: the length comes from each instance (JA_Comprimento)
            m.AreaPedra = c.LarguraTotalMm / 1000.0;
            m.Resumo.Add("Largura " + PecaUtil.Mm(c.LarguraTotalMm) + " mm");
            m.Resumo.Add("Pedra " + PecaUtil.M2(c.LarguraTotalMm / 1000) + " por metro");
            return m;
        }
    }
}
