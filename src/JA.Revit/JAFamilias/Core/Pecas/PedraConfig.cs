using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using JAEsquadrias.Infrastructure;

namespace JAEsquadrias.Core
{
    public enum UsoPedra { Soleira = 0, Filete = 1, Peitoril = 2, Rodape = 3, Degrau = 4, Patamar = 5 }

    /// <summary>
    /// Stone piece of an opening (soleira, filete or peitoril) placed by "Soleiras e Peitoris" in
    /// doors and windows that are not JA families. Local coordinates (mm): origin at the centre of
    /// the opening on the wall axis, X along the wall, Y across it (+ = exterior), Z up; the TOP of
    /// the stone is at Z = 0 (the door base / window sill).
    /// </summary>
    [DataContract]
    public sealed class PedraConfig : IPecaConfig
    {
        public TipoPeca Tipo => TipoPeca.Pedra;

        [DataMember(Order = 1)] public string Codigo { get; set; } = "SL01";
        [DataMember(Order = 2)] public string Nome { get; set; } = "";
        [DataMember(Order = 3)] public string ModeloId { get; set; } = "";

        [DataMember(Order = 10)] public UsoPedra Uso { get; set; }
        /// <summary>Length along the wall (opening + the abas of a peitoril).</summary>
        [DataMember(Order = 11)] public double ComprimentoMm { get; set; } = 800;
        /// <summary>Width across the wall (wall thickness, or the filete width; peitoril: up to the exterior face).</summary>
        [DataMember(Order = 12)] public double LarguraMm { get; set; } = 150;
        [DataMember(Order = 13)] public double EspessuraMm { get; set; } = 20;
        /// <summary>Centre of the width from the wall axis, + = towards the exterior.</summary>
        [DataMember(Order = 14)] public double CentroMm { get; set; }
        /// <summary>Peitoril: projection beyond the exterior face (pingadeira).</summary>
        [DataMember(Order = 15)] public double ProjecaoMm { get; set; } = 30;
        /// <summary>Peitoril: a drip groove under the projection.</summary>
        [DataMember(Order = 16)] public bool Pingadeira { get; set; } = true;

        /// <summary>Degrau: height of the riser stone (espelho) under the tread; 0 = no riser.</summary>
        [DataMember(Order = 17)] public double AlturaEspelhoMm { get; set; } = 160;
        [DataMember(Order = 18)] public double EspessuraEspelhoMm { get; set; } = 20;

        /// <summary>
        /// Rodapé: line-based — the length follows the line drawn in Revit (JA_Comprimento); the
        /// strip stands on the left of the line (family +Y), LarguraMm high and EspessuraMm thick.
        /// </summary>
        public bool Linear => Uso == UsoPedra.Rodape;

        [DataMember(Order = 20)] public string Material { get; set; } = "Granito cinza";
        [DataMember(Order = 21)] public string Cor { get; set; } = "B9B6B0";

        public PedraConfig Normalized()
        {
            if (string.IsNullOrWhiteSpace(Codigo)) Codigo = Uso == UsoPedra.Peitoril ? "PE01" : Uso == UsoPedra.Rodape ? "RD01" : Uso == UsoPedra.Degrau ? "DG01" : Uso == UsoPedra.Patamar ? "PT01" : "SL01";
            AlturaEspelhoMm = PecaUtil.Clamp(AlturaEspelhoMm, 0, 400);
            EspessuraEspelhoMm = PecaUtil.Clamp(EspessuraEspelhoMm <= 0 ? 20 : EspessuraEspelhoMm, 3, 60);
            if (Nome == null) Nome = "";
            if (ModeloId == null) ModeloId = "";
            ComprimentoMm = PecaUtil.Clamp(ComprimentoMm, 50, 15000);
            LarguraMm = PecaUtil.Clamp(LarguraMm, 10, Uso == UsoPedra.Patamar ? 15000 : 1500);
            EspessuraMm = PecaUtil.Clamp(EspessuraMm <= 0 ? 20 : EspessuraMm, 3, 100);
            ProjecaoMm = PecaUtil.Clamp(ProjecaoMm, 0, 300);
            if (string.IsNullOrWhiteSpace(Material)) Material = "Granito cinza";
            if (string.IsNullOrWhiteSpace(Cor)) Cor = "B9B6B0";
            return this;
        }

        public string NomeUso => Uso == UsoPedra.Peitoril ? "Peitoril" : Uso == UsoPedra.Filete ? "Filete" : Uso == UsoPedra.Rodape ? "Rodapé" : Uso == UsoPedra.Degrau ? "Degrau" : Uso == UsoPedra.Patamar ? "Patamar" : "Soleira";

        public string ToJson() => Json.Serialize(this);
        public IPecaConfig Copiar() => Json.Deserialize<PedraConfig>(ToJson()).Normalized();

        public IList<MaterialDef> Materiais() => new List<MaterialDef>
        {
            new MaterialDef { Key = "pedra", Parametro = "Material da pedra", Nome = Material.Trim(), Hex = Cor },
        };

        public string MaterialPrincipal() => Material;

        public string Dimensoes() =>
            Uso == UsoPedra.Rodape ? "altura " + PecaUtil.Mm(LarguraMm) + " x " + PecaUtil.Mm(EspessuraMm) + " mm (comprimento conforme o trecho)"
            : Uso == UsoPedra.Patamar ? PecaUtil.Mm(ComprimentoMm) + " x " + PecaUtil.Mm(LarguraMm) + " x " + PecaUtil.Mm(EspessuraMm) + " mm"
            : Uso == UsoPedra.Degrau ? "piso " + PecaUtil.Mm(ComprimentoMm) + " x " + PecaUtil.Mm(LarguraMm) + " x " + PecaUtil.Mm(EspessuraMm) + " mm" +
                                       (AlturaEspelhoMm > 0 ? ", espelho " + PecaUtil.Mm(AlturaEspelhoMm) + " x " + PecaUtil.Mm(EspessuraEspelhoMm) + " mm" : "")
            : PecaUtil.Mm(ComprimentoMm) + " x " + PecaUtil.Mm(LarguraMm) + " x " + PecaUtil.Mm(EspessuraMm) + " mm";

        public string Descricao()
        {
            string d = NomeUso + " " + Dimensoes() + (Uso == UsoPedra.Peitoril && ProjecaoMm > 0 ? ", projeção " + PecaUtil.Mm(ProjecaoMm) + " mm" + (Pingadeira ? " com pingadeira" : "") : "") + "; " + Material;
            return string.IsNullOrWhiteSpace(Nome) ? d : Nome.Trim() + " – " + d;
        }

        /// <summary>Family name per size: "JA Soleira SL01 800x150x20".</summary>
        public string NomeFamilia() => Uso == UsoPedra.Rodape ? "JA Rodapé " + Codigo + " " + PecaUtil.Mm(LarguraMm) + "x" + PecaUtil.Mm(EspessuraMm)
            : "JA " + NomeUso + " " + Codigo + " " + PecaUtil.Mm(ComprimentoMm) + "x" + PecaUtil.Mm(LarguraMm) + "x" + PecaUtil.Mm(EspessuraMm);

        public PecaModelo Build()
        {
            var c = (PedraConfig)Copiar();
            var m = new PecaModelo();
            double L = c.ComprimentoMm, W = c.LarguraMm, e = c.EspessuraMm;
            if (c.Uso == UsoPedra.Rodape)
            {
                // profile in the YZ plane (Y = into the room, Z = up), extruded along +X with the line
                var rp = new Peca { Nome = "Rodapé", O = V3.Zero, U = V3.Ey, V = V3.Ez, Depth = L, Slot = "pedra", AcompanhaComprimento = true };
                rp.Loops.Add(Peca.Rect(0, 0, e, W));
                m.Pecas.Add(rp);
                m.AreaPedra = (W + e) / 1000.0;            // per metre: face + top edge
                m.Resumo.Add("Pedra " + PecaUtil.M2(m.AreaPedra) + " por metro");
                return m;
            }
            if (c.Uso == UsoPedra.Degrau || c.Uso == UsoPedra.Patamar)
            {
                if (c.Uso == UsoPedra.Patamar) c.AlturaEspelhoMm = 0;
                // origin: front edge (nosing) of the tread, at the middle of the width, top of the tread at Z = 0;
                // the tread runs back along +Y; the riser stone stands under it, set back by the bocel
                var piso = Peca.Rect(-L / 2, 0, L / 2, W);
                m.Pecas.Add(Peca.Laje(c.Uso == UsoPedra.Patamar ? "Piso do patamar" : "Piso do degrau", piso, null, -e, 0, "pedra"));
                double h = c.AlturaEspelhoMm, b = Math.Min(c.ProjecaoMm, W - c.EspessuraEspelhoMm);
                if (h > 0)
                {
                    // extruded along +Y (U = Z, V = X: N = Z × X = Y)
                    var esp = new Peca { Nome = "Espelho", O = new V3(0, b, 0), U = V3.Ez, V = V3.Ex, Depth = c.EspessuraEspelhoMm, Slot = "pedra" };
                    esp.Loops.Add(Peca.Rect(-e - h, -L / 2, -e, L / 2));
                    m.Pecas.Add(esp);
                }
                for (int i = 0; i < piso.Count; i++) m.Planta.Add(new LinhaPeca(piso[i], piso[(i + 1) % piso.Count], false));
                if (h > 0 && b > 1) m.Planta.Add(new LinhaPeca(new P2(-L / 2, b), new P2(L / 2, b), true));
                m.AreaPedra = (L * W + 2 * (L + W) * e + (h > 0 ? L * h : 0)) / 1e6;
                m.Resumo.Add("Pedra ≈ " + PecaUtil.M2(m.AreaPedra) + (c.Uso == UsoPedra.Patamar ? " por patamar" : " por degrau"));
                return m;
            }
            double y0 = c.CentroMm - W / 2, y1 = c.CentroMm + W / 2;
            var outline = Peca.Rect(-L / 2, y0, L / 2, y1);
            if (c.Uso == UsoPedra.Peitoril && c.Pingadeira && c.ProjecaoMm >= 15 && e >= 15)
            {
                // drip groove 5 x 5 mm under the projection, 10 mm from the edge
                double g = y1 - 10;
                m.Pecas.Add(Peca.Laje(c.NomeUso, outline, null, -e + 5, 0, "pedra"));
                m.Pecas.Add(Peca.Laje(c.NomeUso + " (base)", Peca.Rect(-L / 2, y0, L / 2, g - 5), null, -e, -e + 5, "pedra"));
                m.Pecas.Add(Peca.Laje(c.NomeUso + " (aba da pingadeira)", Peca.Rect(-L / 2, g, L / 2, y1), null, -e, -e + 5, "pedra"));
            }
            else m.Pecas.Add(Peca.Laje(c.NomeUso, outline, null, -e, 0, "pedra"));
            for (int i = 0; i < outline.Count; i++) m.Planta.Add(new LinhaPeca(outline[i], outline[(i + 1) % outline.Count], false));
            m.AreaPedra = (L * W + 2 * (L + W) * e) / 1e6;
            m.Resumo.Add("Pedra ≈ " + PecaUtil.M2(m.AreaPedra));
            return m;
        }
    }
}
