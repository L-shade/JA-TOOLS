using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using JAEsquadrias.Infrastructure;

namespace JAEsquadrias.Core
{
    /// <summary>
    /// Nicho embutido (wall niche, e.g. shower niche): a void that cuts the host wall, lined
    /// inside, with stone base (soleira do nicho), shelves, vertical dividers and an optional
    /// frame on the wall face. Local coordinates (mm): X along the wall (centred on the insertion
    /// point), Y INTO the wall from the face it is placed on, Z up from the bottom of the niche.
    /// </summary>
    [DataContract]
    public sealed class NichoConfig : IPecaConfig
    {
        public TipoPeca Tipo => TipoPeca.Nicho;

        [DataMember(Order = 1)] public string Codigo { get; set; } = "NI01";
        [DataMember(Order = 2)] public string Nome { get; set; } = "";
        [DataMember(Order = 3)] public string ModeloId { get; set; } = "";

        /// <summary>Finished inside width / height / depth.</summary>
        [DataMember(Order = 10)] public double LarguraMm { get; set; } = 300;
        [DataMember(Order = 11)] public double AlturaMm { get; set; } = 600;
        [DataMember(Order = 12)] public double ProfundidadeMm { get; set; } = 100;
        /// <summary>Height of the niche bottom from the floor (family Default Elevation).</summary>
        [DataMember(Order = 13)] public double AlturaBaseMm { get; set; } = 1000;

        [DataMember(Order = 20)] public int Prateleiras { get; set; } = 1;
        [DataMember(Order = 21)] public double PrateleiraEspessuraMm { get; set; } = 10;
        [DataMember(Order = 22)] public string MaterialPrateleira { get; set; } = "Vidro temperado incolor";
        [DataMember(Order = 23)] public string CorPrateleira { get; set; } = "CFE6EE";
        [DataMember(Order = 24)] public int Divisorias { get; set; }
        [DataMember(Order = 25)] public double DivisoriaEspessuraMm { get; set; } = 10;

        [DataMember(Order = 30)] public bool Revestimento { get; set; } = true;
        [DataMember(Order = 31)] public double RevestimentoEspessuraMm { get; set; } = 10;
        [DataMember(Order = 32)] public string MaterialInterno { get; set; } = "Porcelanato branco";
        [DataMember(Order = 33)] public string CorInterno { get; set; } = "EDEBE6";

        [DataMember(Order = 40)] public bool BasePedra { get; set; } = true;
        [DataMember(Order = 41)] public double BaseEspessuraMm { get; set; } = 20;
        /// <summary>How far the base stone sticks out of the wall face (pingadeira).</summary>
        [DataMember(Order = 42)] public double BaseProjecaoMm { get; set; } = 10;
        [DataMember(Order = 43)] public bool Moldura { get; set; }
        [DataMember(Order = 44)] public double MolduraLarguraMm { get; set; } = 30;
        [DataMember(Order = 45)] public double MolduraEspessuraMm { get; set; } = 10;
        [DataMember(Order = 46)] public string MaterialPedra { get; set; } = "Mármore branco";
        [DataMember(Order = 47)] public string CorPedra { get; set; } = "E9E6E0";

        /// <summary>Dashed outline in plan (niches above the cut plane).</summary>
        [DataMember(Order = 50)] public bool PlantaTracejada { get; set; }

        public NichoConfig Normalized()
        {
            if (string.IsNullOrWhiteSpace(Codigo)) Codigo = "NI01";
            if (Nome == null) Nome = "";
            if (ModeloId == null) ModeloId = "";
            LarguraMm = PecaUtil.Clamp(LarguraMm, 80, 4000);
            AlturaMm = PecaUtil.Clamp(AlturaMm, 80, 3000);
            ProfundidadeMm = PecaUtil.Clamp(ProfundidadeMm, 30, 600);
            AlturaBaseMm = PecaUtil.Clamp(AlturaBaseMm, 0, 4000);
            Prateleiras = Math.Max(0, Math.Min(10, Prateleiras));
            Divisorias = Math.Max(0, Math.Min(10, Divisorias));
            PrateleiraEspessuraMm = PecaUtil.Clamp(PrateleiraEspessuraMm <= 0 ? 10 : PrateleiraEspessuraMm, 4, 60);
            DivisoriaEspessuraMm = PecaUtil.Clamp(DivisoriaEspessuraMm <= 0 ? 10 : DivisoriaEspessuraMm, 4, 60);
            RevestimentoEspessuraMm = PecaUtil.Clamp(RevestimentoEspessuraMm <= 0 ? 10 : RevestimentoEspessuraMm, 3, 50);
            BaseEspessuraMm = PecaUtil.Clamp(BaseEspessuraMm <= 0 ? 20 : BaseEspessuraMm, 5, 60);
            BaseProjecaoMm = PecaUtil.Clamp(BaseProjecaoMm, 0, 100);
            MolduraLarguraMm = PecaUtil.Clamp(MolduraLarguraMm <= 0 ? 30 : MolduraLarguraMm, 5, 300);
            MolduraEspessuraMm = PecaUtil.Clamp(MolduraEspessuraMm <= 0 ? 10 : MolduraEspessuraMm, 2, 60);
            if (string.IsNullOrWhiteSpace(MaterialPrateleira)) MaterialPrateleira = "Vidro temperado incolor";
            if (string.IsNullOrWhiteSpace(CorPrateleira)) CorPrateleira = "CFE6EE";
            if (string.IsNullOrWhiteSpace(MaterialInterno)) MaterialInterno = "Porcelanato branco";
            if (string.IsNullOrWhiteSpace(CorInterno)) CorInterno = "EDEBE6";
            if (string.IsNullOrWhiteSpace(MaterialPedra)) MaterialPedra = "Mármore branco";
            if (string.IsNullOrWhiteSpace(CorPedra)) CorPedra = "E9E6E0";
            return this;
        }

        public string ToJson() => Json.Serialize(this);
        public IPecaConfig Copiar() => Json.Deserialize<NichoConfig>(ToJson()).Normalized();

        public IList<MaterialDef> Materiais() => new List<MaterialDef>
        {
            new MaterialDef { Key = "pedra", Parametro = "Material da pedra", Nome = MaterialPedra.Trim(), Hex = CorPedra },
            new MaterialDef { Key = "interno", Parametro = "Material interno", Nome = MaterialInterno.Trim(), Hex = CorInterno },
            new MaterialDef { Key = "prateleira", Parametro = "Material das prateleiras", Nome = MaterialPrateleira.Trim(), Hex = CorPrateleira,
                              Transparencia = MaterialPrateleira.IndexOf("vidro", StringComparison.OrdinalIgnoreCase) >= 0 ? 60 : 0 },
        };

        public string MaterialPrincipal() => Revestimento ? MaterialInterno : MaterialPedra;

        public string Dimensoes() =>
            PecaUtil.Mm(LarguraMm) + " x " + PecaUtil.Mm(AlturaMm) + " x " + PecaUtil.Mm(ProfundidadeMm) + " mm, base a " + PecaUtil.Mm(AlturaBaseMm);

        public string Descricao()
        {
            var parts = new List<string> { "Nicho embutido " + Dimensoes() };
            if (Prateleiras > 0) parts.Add(Prateleiras + (Prateleiras == 1 ? " prateleira" : " prateleiras") + " (" + MaterialPrateleira.ToLowerInvariant() + ")");
            if (Divisorias > 0) parts.Add(Divisorias + (Divisorias == 1 ? " divisória" : " divisórias"));
            if (Revestimento) parts.Add("revestido em " + MaterialInterno.ToLowerInvariant());
            if (BasePedra) parts.Add("base em " + MaterialPedra.ToLowerInvariant());
            if (Moldura) parts.Add("moldura " + PecaUtil.Mm(MolduraLarguraMm) + " mm");
            string d = string.Join("; ", parts);
            return string.IsNullOrWhiteSpace(Nome) ? d : Nome.Trim() + " – " + d;
        }

        public PecaModelo Build()
        {
            var c = (NichoConfig)Copiar();
            var m = new PecaModelo();
            double W = c.LarguraMm, H = c.AlturaMm, D = c.ProfundidadeMm;
            double t = c.Revestimento ? c.RevestimentoEspessuraMm : 0;
            double b = c.BasePedra ? c.BaseEspessuraMm : t;
            double x0 = -W / 2, x1 = W / 2;

            // the hole in the wall (void cuts the host): inside size + lining + base
            m.Pecas.Add(new Peca { Nome = "Vazio do nicho", O = new V3(x0 - t, -2, -b), Depth = H + t + b, Void = true, Loops = { Peca.Rect(0, 0, W + 2 * t, D + t + 2) } });

            double area = 0, areaInt = 0;
            if (t > 0)
            {
                m.Pecas.Add(Peca.Box("Fundo", x0 - t, D, -b, x1 + t, D + t, H + t, "interno"));
                m.Pecas.Add(Peca.Box("Lateral esquerda", x0 - t, 0, 0, x0, D, H + t, "interno"));
                m.Pecas.Add(Peca.Box("Lateral direita", x1, 0, 0, x1 + t, D, H + t, "interno"));
                m.Pecas.Add(Peca.Box("Teto", x0, 0, H, x1, D, H + t, "interno"));
                if (!c.BasePedra) m.Pecas.Add(Peca.Box("Piso", x0 - t, 0, -t, x1 + t, D, 0, "interno"));
                areaInt = (W * H + 2 * D * H + W * D * (c.BasePedra ? 1 : 2)) / 1e6;
            }
            if (c.BasePedra)
            {
                m.Pecas.Add(Peca.Box("Base", x0 - t, -c.BaseProjecaoMm, -b, x1 + t, D, 0, "pedra"));
                area += (W + 2 * t) * (D + c.BaseProjecaoMm);
            }
            for (int i = 1; i <= c.Prateleiras; i++)
            {
                double z = H * i / (c.Prateleiras + 1);
                double e = c.PrateleiraEspessuraMm;
                m.Pecas.Add(Peca.Box("Prateleira " + i, x0, 0, z - e / 2, x1, D - 2, z + e / 2, "prateleira"));
            }
            for (int i = 1; i <= c.Divisorias; i++)
            {
                double x = x0 + W * i / (c.Divisorias + 1);
                double e = c.DivisoriaEspessuraMm;
                m.Pecas.Add(Peca.Box("Divisória " + i, x - e / 2, 0, 0, x + e / 2, D, H, "prateleira"));
            }
            if (c.Moldura)
            {
                // frame on the wall face, around the opening (plane XZ, extruded out of the wall)
                double mw = c.MolduraLarguraMm, me = c.MolduraEspessuraMm;
                double zb = c.BasePedra ? 0 : -b;
                var frame = new Peca { Nome = "Moldura", O = new V3(0, 0, 0), U = V3.Ex, V = V3.Ez, Depth = me, Slot = "pedra" };
                // N = Ex × Ez = −Ey: out of the wall
                double xo0 = x0 - t - mw, xo1 = x1 + t + mw, xi0 = x0 - t, xi1 = x1 + t, zi1 = H + t, zo1 = H + t + mw;
                if (c.BasePedra)
                {
                    // U-shaped frame standing on the base stone
                    frame.Loops.Add(new List<P2> { new P2(xo0, 0), new P2(xi0, 0), new P2(xi0, zi1), new P2(xi1, zi1), new P2(xi1, 0), new P2(xo1, 0), new P2(xo1, zo1), new P2(xo0, zo1) });
                    area += (xo1 - xo0) * (zo1) - (xi1 - xi0) * zi1;
                }
                else
                {
                    frame.Loops.Add(Peca.Rect(xo0, zb - mw, xo1, zo1));
                    frame.Loops.Add(Peca.Rect(xi0, zb, xi1, zi1));
                    area += (xo1 - xo0) * (zo1 - zb + mw) - (xi1 - xi0) * (zi1 - zb);
                }
                m.Pecas.Add(frame);
            }
            if (c.PlantaTracejada)
            {
                var r = Peca.Rect(x0 - t, 0, x1 + t, D + t);
                for (int i = 0; i < 4; i++) m.Planta.Add(new LinhaPeca(r[i], r[(i + 1) % 4], true));
            }
            if (D + t > 200) m.Avisos.Add("Profundidade grande: confira se a parede comporta o nicho (ele corta a parede a partir da face onde foi colocado).");

            m.AreaPedra = area / 1e6;
            m.Resumo.Add("Vão acabado " + PecaUtil.Mm(W) + " × " + PecaUtil.Mm(H));
            if (areaInt > 0) m.Resumo.Add("Revestimento " + PecaUtil.M2(areaInt));
            if (m.AreaPedra > 0) m.Resumo.Add("Pedra " + PecaUtil.M2(m.AreaPedra));
            return m;
        }
    }
}
