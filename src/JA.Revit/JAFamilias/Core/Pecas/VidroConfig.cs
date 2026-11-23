using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using JAEsquadrias.Infrastructure;

namespace JAEsquadrias.Core
{
    public enum UsoVidro { GuardaCorpo = 0, Box = 1 }
    public enum FixacaoGuardaCorpo { PerfilU = 0, Lateral = 1 }
    public enum AberturaBox { Correr = 0, Abrir = 1, Fixo = 2 }
    /// <summary>Modulated guarda-corpo: the profiles run continuous, the glass is one copy per plate.</summary>
    public enum ParteGuardaCorpo { Tudo = 0, Perfis = 1, Vidro = 2 }

    /// <summary>
    /// Tempered-glass pieces: guarda-corpo (line-based: glass, base profile and handrail follow the
    /// line drawn in Revit; profile in the YZ plane centred on the line, extruded along +X) and box de
    /// banheiro (fixed width between two walls; origin at the middle of the opening on the floor,
    /// X along the opening, glass in the plane Y = 0, +Y = inside the shower).
    /// </summary>
    [DataContract]
    public sealed class VidroConfig : IPecaConfig
    {
        public TipoPeca Tipo => TipoPeca.Vidro;

        [DataMember(Order = 1)] public string Codigo { get; set; } = "GC01";
        [DataMember(Order = 2)] public string Nome { get; set; } = "";
        [DataMember(Order = 3)] public string ModeloId { get; set; } = "";

        [DataMember(Order = 10)] public UsoVidro Uso { get; set; }
        /// <summary>Guarda-corpo: default length of the line. Box: width of the opening (wall to wall).</summary>
        [DataMember(Order = 11)] public double ComprimentoMm { get; set; } = 1500;
        /// <summary>Guarda-corpo: top of the handrail from the floor (NBR 14718: 1100 mm). Box: height.</summary>
        [DataMember(Order = 12)] public double AlturaMm { get; set; } = 1100;
        [DataMember(Order = 13)] public double EspessuraVidroMm { get; set; } = 10;

        [DataMember(Order = 20)] public FixacaoGuardaCorpo Fixacao { get; set; }
        [DataMember(Order = 21)] public bool Corrimao { get; set; } = true;
        [DataMember(Order = 22)] public double CorrimaoLarguraMm { get; set; } = 40;
        [DataMember(Order = 23)] public double CorrimaoAlturaMm { get; set; } = 30;
        /// <summary>Glass plate length (0 = one continuous glass) and the gap between plates and at the ends.</summary>
        [DataMember(Order = 24)] public double ModuloMm { get; set; }
        [DataMember(Order = 25)] public double FolgaMm { get; set; } = 10;
        [DataMember(Order = 26)] public ParteGuardaCorpo Partes { get; set; }
        /// <summary>
        /// Guarda-corpo as a Revit railing (category Guarda-corpos): this config is then the glass PLATE,
        /// a baluster panel (point-based, ComprimentoMm = plate width, centred on X, glass on Y = 0).
        /// </summary>
        [DataMember(Order = 27)] public bool Balaustre { get; set; }
        /// <summary>Railing mode: equal plates per stretch (one railing per run of equal plates) or a fixed module along one railing.</summary>
        [DataMember(Order = 28)] public bool ModuloFixo { get; set; }

        [DataMember(Order = 30)] public AberturaBox Abertura { get; set; }
        [DataMember(Order = 31)] public bool Puxador { get; set; } = true;
        /// <summary>Box em L: a fixed side leg (LadoFixoMm) at the corner, square to the door leg; LadoSinal = which side (+1 = family +Y).</summary>
        [DataMember(Order = 32)] public bool Canto { get; set; }
        [DataMember(Order = 33)] public double LadoFixoMm { get; set; } = 800;
        [DataMember(Order = 34)] public int LadoSinal { get; set; } = 1;

        [DataMember(Order = 40)] public string MaterialVidro { get; set; } = "Vidro temperado incolor";
        [DataMember(Order = 41)] public string CorVidro { get; set; } = "C8E2EA";
        [DataMember(Order = 42)] public string MaterialMetal { get; set; } = "Alumínio anodizado natural";
        [DataMember(Order = 43)] public string CorMetal { get; set; } = "B9BCBE";

        public bool Linear => Uso == UsoVidro.GuardaCorpo && !Balaustre;

        public VidroConfig Normalized()
        {
            if (string.IsNullOrWhiteSpace(Codigo)) Codigo = Uso == UsoVidro.Box ? "BX01" : "GC01";
            if (Nome == null) Nome = "";
            if (ModeloId == null) ModeloId = "";
            ComprimentoMm = PecaUtil.Clamp(ComprimentoMm <= 0 ? 1500 : ComprimentoMm, Balaustre ? 50 : 300, 20000);   // a railing plate can be narrow
            LadoFixoMm = PecaUtil.Clamp(LadoFixoMm <= 0 ? 800 : LadoFixoMm, 150, 4000);
            LadoSinal = LadoSinal < 0 ? -1 : 1;
            AlturaMm = PecaUtil.Clamp(AlturaMm <= 0 ? (Uso == UsoVidro.Box ? 1900 : 1100) : AlturaMm, 300, 3000);
            EspessuraVidroMm = PecaUtil.Clamp(EspessuraVidroMm <= 0 ? 10 : EspessuraVidroMm, 4, 40);
            CorrimaoLarguraMm = PecaUtil.Clamp(CorrimaoLarguraMm <= 0 ? 40 : CorrimaoLarguraMm, 15, 150);
            CorrimaoAlturaMm = PecaUtil.Clamp(CorrimaoAlturaMm <= 0 ? 30 : CorrimaoAlturaMm, 10, 150);
            ModuloMm = ModuloMm <= 0 ? 0 : PecaUtil.Clamp(ModuloMm, 300, 6000);
            FolgaMm = PecaUtil.Clamp(FolgaMm, 0, 100);
            if (string.IsNullOrWhiteSpace(MaterialVidro)) MaterialVidro = "Vidro temperado incolor";
            if (string.IsNullOrWhiteSpace(CorVidro)) CorVidro = "C8E2EA";
            if (string.IsNullOrWhiteSpace(MaterialMetal)) MaterialMetal = "Alumínio anodizado natural";
            if (string.IsNullOrWhiteSpace(CorMetal)) CorMetal = "B9BCBE";
            return this;
        }

        public string NomeUso => Uso == UsoVidro.Box ? "Box" : "Guarda-corpo";
        public string ToJson() => Json.Serialize(this);
        public IPecaConfig Copiar() => Json.Deserialize<VidroConfig>(ToJson()).Normalized();

        public IList<MaterialDef> Materiais() => new List<MaterialDef>
        {
            new MaterialDef { Key = "vidro", Parametro = "Material do vidro", Nome = MaterialVidro.Trim(), Hex = CorVidro, Transparencia = 65 },
            new MaterialDef { Key = "metal", Parametro = "Material dos perfis", Nome = MaterialMetal.Trim(), Hex = CorMetal },
        };

        public string MaterialPrincipal() => MaterialVidro;

        public string NomeAbertura => Abertura == AberturaBox.Abrir ? "de abrir" : Abertura == AberturaBox.Fixo ? "fixo" : "de correr";

        public string Dimensoes() => Uso == UsoVidro.Box
            ? (Canto ? "em L " + PecaUtil.Mm(ComprimentoMm) + " + " + PecaUtil.Mm(LadoFixoMm) : PecaUtil.Mm(ComprimentoMm)) + " x " + PecaUtil.Mm(AlturaMm) + " mm, vidro " + PecaUtil.Mm(EspessuraVidroMm) + " mm"
            : "altura " + PecaUtil.Mm(AlturaMm) + " mm, vidro " + PecaUtil.Mm(EspessuraVidroMm) + " mm (comprimento conforme o trecho)";

        public string Descricao()
        {
            string d = Uso == UsoVidro.Box
                ? "Box " + NomeAbertura + " " + Dimensoes() + "; " + MaterialVidro.ToLowerInvariant() + ", perfis " + MaterialMetal.ToLowerInvariant()
                : "Guarda-corpo de vidro " + Dimensoes() + (Fixacao == FixacaoGuardaCorpo.PerfilU ? ", perfil U no piso" : ", fixação lateral") +
                  (Corrimao ? ", corrimão " + PecaUtil.Mm(CorrimaoLarguraMm) + " x " + PecaUtil.Mm(CorrimaoAlturaMm) + " mm" : "") +
                  (ModuloMm > 0 ? ", placas de até " + PecaUtil.Mm(ModuloMm) + " mm" : "") +
                  (Partes == ParteGuardaCorpo.Perfis ? " (perfis)" : Partes == ParteGuardaCorpo.Vidro ? " (placa de vidro)" : "");
            return string.IsNullOrWhiteSpace(Nome) ? d : Nome.Trim() + " – " + d;
        }

        public PecaModelo Build()
        {
            var c = (VidroConfig)Copiar();
            return c.Uso == UsoVidro.Box ? c.BuildBox() : c.Balaustre ? c.BuildPainel() : c.BuildGuardaCorpo();
        }

        // a strip that follows the line: rectangle (y0..y1, z0..z1) in the YZ plane, along +X
        private static Peca Corrida(string nome, double y0, double y1, double z0, double z1, double len, string slot)
        {
            var p = new Peca { Nome = nome, O = V3.Zero, U = V3.Ey, V = V3.Ez, Depth = len, Slot = slot, AcompanhaComprimento = true };
            p.Loops.Add(Peca.Rect(y0, z0, y1, z1));
            return p;
        }

        /// <summary>Altura do vidro (base, topo) do guarda-corpo, em mm a partir do piso.</summary>
        public void AlturasDoVidro(out double baseVidro, out double topoVidro)
        {
            topoVidro = Corrimao ? AlturaMm - CorrimaoAlturaMm : AlturaMm;
            baseVidro = Fixacao == FixacaoGuardaCorpo.PerfilU ? 4 : 0;
        }

        /// <summary>One glass plate of a railing (baluster panel): width ComprimentoMm centred on the origin.</summary>
        private PecaModelo BuildPainel()
        {
            var m = new PecaModelo();
            double W = ComprimentoMm, t = EspessuraVidroMm;
            AlturasDoVidro(out double z0, out double z1);
            m.Pecas.Add(Peca.Box("Vidro", -W / 2, -t / 2, z0, W / 2, t / 2, z1, "vidro"));
            m.Planta.Add(new LinhaPeca(new P2(-W / 2, -t / 2), new P2(W / 2, -t / 2), false));
            m.Planta.Add(new LinhaPeca(new P2(-W / 2, t / 2), new P2(W / 2, t / 2), false));
            m.AreaPedra = W * (z1 - z0) / 1e6;
            m.Resumo.Add("Placa " + PecaUtil.Mm(W) + " x " + PecaUtil.Mm(z1 - z0) + " mm");
            return m;
        }

        private PecaModelo BuildGuardaCorpo()
        {
            var m = new PecaModelo();
            double L = ComprimentoMm, H = AlturaMm, t = EspessuraVidroMm;
            double topoVidro = Corrimao ? H - CorrimaoAlturaMm : H;
            double baseVidro = 0;
            bool perfis = Partes != ParteGuardaCorpo.Vidro, vidro = Partes != ParteGuardaCorpo.Perfis;
            if (Fixacao == FixacaoGuardaCorpo.PerfilU && perfis)
            {
                // U profile on the floor: 2 walls 4 mm thick, 100 mm high, glass sits inside it
                double w = t + 2 * 4 + 2 * 12, hu = 100;
                m.Pecas.Add(Corrida("Perfil U (lado 1)", -w / 2, -w / 2 + 4, 0, hu, L, "metal"));
                m.Pecas.Add(Corrida("Perfil U (lado 2)", w / 2 - 4, w / 2, 0, hu, L, "metal"));
                m.Pecas.Add(Corrida("Perfil U (fundo)", -w / 2, w / 2, 0, 4, L, "metal"));
                baseVidro = 4;
            }
            if (Fixacao == FixacaoGuardaCorpo.PerfilU) baseVidro = 4;
            if (vidro) m.Pecas.Add(Corrida("Vidro", -t / 2, t / 2, baseVidro, topoVidro, L, "vidro"));
            if (Corrimao && perfis)
                m.Pecas.Add(Corrida("Corrimão", -CorrimaoLarguraMm / 2, CorrimaoLarguraMm / 2, topoVidro, H, L, "metal"));
            m.Resumo.Add("Vidro " + PecaUtil.M2((topoVidro - baseVidro) / 1000.0) + " por metro");
            if (H < 1100) m.Avisos.Add("NBR 14718: guarda-corpo de edificação com pelo menos 1,10 m de altura.");
            return m;
        }

        private PecaModelo BuildBox()
        {
            var m = new PecaModelo();
            double W = ComprimentoMm, H = AlturaMm, t = EspessuraVidroMm;
            double x0 = -W / 2, x1 = W / 2;
            // box em L: the corner at the origin (door leg along +X), the inside towards LadoSinal·Y
            double xo = Canto ? W / 2 : 0, sy = Canto ? LadoSinal : 1;
            P2 Tr(P2 q) => new P2(q.X + xo, q.Y * sy);
            void Caixa(string nome, double ax0, double ay0, double ax1, double ay1, double z0, double z1, string slot)
            {
                P2 a = Tr(new P2(ax0, ay0)), b = Tr(new P2(ax1, ay1));
                m.Pecas.Add(Peca.Laje(nome, Peca.Rect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)), null, z0, z1, slot));
            }
            void Lin(P2 a, P2 b, bool tracejada) => m.Planta.Add(new LinhaPeca(Tr(a), Tr(b), tracejada));
            double area = 0;
            switch (Abertura)
            {
                case AberturaBox.Correr:
                {
                    // fixed leaf at the back, sliding leaf in front, overlapping 50 mm; top rail and bottom guide
                    double meio = 25, off = t / 2 + 6;
                    Caixa("Trilho superior", x0, -off - t / 2 - 8, x1, off + t / 2 + 8, H - 40, H, "metal");
                    Caixa("Guia inferior", x0, -off - t / 2 - 4, x1, off + t / 2 + 4, 0, 15, "metal");
                    // +Y = inside the shower: the fixed leaf inside, the sliding leaf and the handle outside
                    Caixa("Vidro fixo", x0, off - t / 2, meio, off + t / 2, 15, H - 40, "vidro");
                    Caixa("Vidro de correr", -meio, -off - t / 2, x1, -off + t / 2, 15, H - 30, "vidro");
                    if (Puxador) Caixa("Puxador", x1 - 120, -off - t / 2 - 25, x1 - 100, -off - t / 2, 900, 1250, "metal");
                    area = ((meio - x0) + (x1 + meio)) * (H - 55) / 1e6;
                    Lin(new P2(x0, off), new P2(meio, off), false);
                    Lin(new P2(-meio, -off), new P2(x1, -off), false);
                    break;
                }
                case AberturaBox.Abrir:
                {
                    Caixa("Vidro da porta", x0 + 5, -t / 2, x1 - 5, t / 2, 10, H, "vidro");
                    Caixa("Dobradiça inferior", x0 + 5, -t / 2 - 8, x0 + 65, t / 2 + 8, 150, 210, "metal");
                    Caixa("Dobradiça superior", x0 + 5, -t / 2 - 8, x0 + 65, t / 2 + 8, H - 210, H - 150, "metal");
                    if (Puxador) Caixa("Puxador", x1 - 100, -t / 2 - 25, x1 - 80, -t / 2, 900, 1250, "metal");
                    area = (W - 10) * (H - 10) / 1e6;
                    Lin(new P2(x0, 0), new P2(x1, 0), false);
                    // swing towards -Y (out of the shower): quarter circle as segments
                    double r = W - 10;
                    for (int i = 0; i < 8; i++)
                    {
                        double a0 = Math.PI / 2 * i / 8, a1 = Math.PI / 2 * (i + 1) / 8;
                        Lin(new P2(x0 + 5 + r * Math.Cos(a0), -r * Math.Sin(a0)), new P2(x0 + 5 + r * Math.Cos(a1), -r * Math.Sin(a1)), true);
                    }
                    break;
                }
                default:
                {
                    Caixa("Perfil U inferior", x0, -t / 2 - 4, x1, t / 2 + 4, 0, 20, "metal");
                    Caixa("Vidro fixo", x0, -t / 2, x1, t / 2, 4, H, "vidro");
                    area = W * (H - 4) / 1e6;
                    Lin(new P2(x0, 0), new P2(x1, 0), false);
                    break;
                }
            }
            if (Canto)
            {
                // the fixed side leg, from the corner to the other wall
                Caixa("Guia lateral", x0 - t / 2 - 4, 0, x0 + t / 2 + 4, LadoFixoMm, 0, 15, "metal");
                Caixa("Vidro fixo lateral", x0 - t / 2, 0, x0 + t / 2, LadoFixoMm, 15, H - 40, "vidro");
                Lin(new P2(x0, 0), new P2(x0, LadoFixoMm), false);
                area += LadoFixoMm * (H - 55) / 1e6;
            }
            m.Resumo.Add("Vidro ≈ " + PecaUtil.M2(area));
            return m;
        }
    }
}
