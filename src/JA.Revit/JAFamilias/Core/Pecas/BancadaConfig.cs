using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using JAEsquadrias.Infrastructure;

namespace JAEsquadrias.Core
{
    /// <summary>Livre = drawn in place in the project (free outline, see <see cref="BancadaConfig.Contorno"/>).</summary>
    public enum FormatoBancada { Reta = 0, L = 1, U = 2, Livre = 3 }
    /// <summary>What happens on one edge of a free-form (in place) countertop.</summary>
    public enum BordaLocal { Livre = 0, LivreSemAcabamento = 1, Parede = 2, ParedeSemFrontao = 3, Pe = 4 }

    /// <summary>A plan point (mm) of a free-form countertop, in the family's coordinates.</summary>
    [DataContract]
    public sealed class Pt
    {
        [DataMember(Order = 1)] public double X { get; set; }
        [DataMember(Order = 2)] public double Y { get; set; }
        public Pt() { }
        public Pt(double x, double y) { X = x; Y = y; }
        public P2 P => new P2(X, Y);
    }

    /// <summary>A round hole through the top of a free-form countertop (tap, soap dispenser, cable grommet).</summary>
    [DataContract]
    public sealed class Furo
    {
        [DataMember(Order = 1)] public double X { get; set; }
        [DataMember(Order = 2)] public double Y { get; set; }
        [DataMember(Order = 3)] public double DiametroMm { get; set; } = 35;
    }

    /// <summary>A dividing line between two areas of the top (dry / wet): a path from edge to edge.</summary>
    [DataContract]
    public sealed class Divisao
    {
        [DataMember(Order = 1)] public List<Pt> Pontos { get; set; } = new List<Pt>();
    }

    /// <summary>One edge of an area of the top, with what it gets (frontão, saia, leg...).</summary>
    internal sealed class ArestaBancada
    {
        public P2 A, B;
        /// <summary>"A", "B", "C" (front or wall side of a run), "Esq", "Dir" (ends): the per-run switches.</summary>
        public string Chave = "A";
        /// <summary>Against a wall · gets frontão · gets saia / tira · stone leg · inner edge between two areas.</summary>
        public bool Parede, Frontao, Borda, Pe, Divisa;
        public double Length => Poligono.Dist(A, B);
        public ArestaBancada Entre(P2 a, P2 b) { var e = (ArestaBancada)MemberwiseClone(); e.A = a; e.B = b; return e; }
    }

    /// <summary>An area of the top: the whole top, or each part after the dividing lines (dry / wet).</summary>
    public sealed class ZonaBancada
    {
        public List<P2> Pts = new List<P2>();
        /// <summary>Wet area (lowered by the rebaixo, with the containment rim).</summary>
        public bool Molhada;
        public double AreaM2 => Math.Abs(Peca.Area(Pts)) / 1e6;
        internal List<ArestaBancada> Arestas = new List<ArestaBancada>();
        internal readonly List<List<P2>> Holes = new List<List<P2>>();
        /// <summary>Height of the top surface of this area; orientation of the outline (+1 counter-clockwise).</summary>
        internal double Topo, Sinal;

        internal void Fechar()
        {
            Sinal = Math.Sign(Peca.Area(Pts));
            for (int i = 0; i < Pts.Count; i++) Arestas[i] = Arestas[i].Entre(Pts[i], Pts[(i + 1) % Pts.Count]);
        }
    }
    public enum LadoRetorno { Esquerda = 0, Direita = 1 }
    public enum BordaFrontal { Reta = 0, Saia = 1, TiraAmericana = 2 }
    public enum TipoCuba { Embutir = 0, Sobrepor = 1, Esculpida = 2, Apoio = 3 }
    /// <summary>Profile of the top arris of the free edges.</summary>
    public enum PerfilBorda { Reta = 0, Chanfrada = 1 }

    /// <summary>One sink (cuba) of a countertop, on the main run (A).</summary>
    [DataContract]
    public sealed class Cuba
    {
        [DataMember(Order = 1)] public TipoCuba Tipo { get; set; }
        [DataMember(Order = 2)] public double LarguraMm { get; set; } = 500;
        /// <summary>Front-to-back size of the sink opening.</summary>
        [DataMember(Order = 3)] public double ProfundidadeMm { get; set; } = 400;
        [DataMember(Order = 4)] public double RaioMm { get; set; } = 30;
        /// <summary>Centre of the sink from the left end of A; 0 = centred.</summary>
        [DataMember(Order = 5)] public double PosicaoXMm { get; set; }
        /// <summary>Distance from the front edge to the sink; 0 = centred in the depth.</summary>
        [DataMember(Order = 6)] public double RecuoFrenteMm { get; set; }
        /// <summary>Depth of the bowl.</summary>
        [DataMember(Order = 7)] public double FundoMm { get; set; } = 180;
        /// <summary>Hole for the tap behind the sink.</summary>
        [DataMember(Order = 8)] public bool FuroTorneira { get; set; } = true;
        /// <summary>Double sink: two bowls side by side inside the width, split by a stone bridge.</summary>
        [DataMember(Order = 9)] public bool Dupla { get; set; }
        /// <summary>Double sink: width of the stone bridge between the bowls.</summary>
        [DataMember(Order = 10)] public double SeptoMm { get; set; } = 60;
        /// <summary>Double sink: share of the left bowl in % (50 = equal bowls; 60 = 60/40).</summary>
        [DataMember(Order = 11)] public double ProporcaoPct { get; set; } = 50;
        /// <summary>Free-form countertops: the centre is (PosicaoXMm, PosicaoYMm) in the family's coordinates.</summary>
        [DataMember(Order = 12)] public double PosicaoYMm { get; set; }
        /// <summary>Free-form countertops: rotation (degrees, counter-clockwise); the tap side is the sink's +Y.</summary>
        [DataMember(Order = 13)] public double AnguloGraus { get; set; }

        public Cuba Clone() => (Cuba)MemberwiseClone();
    }

    /// <summary>
    /// Bancada (stone countertop): straight, L or U, with sinks (embutir, sobrepor, esculpida,
    /// apoio), cooktop cut-out, front edge (saia / tira americana), tento, frontão (backsplash)
    /// and stone legs. Local coordinates (mm): origin at the back-left corner on the floor,
    /// X along the main run (A), Y towards the back wall (the front edge is at Y = −depth), Z up.
    /// </summary>
    [DataContract]
    public sealed class BancadaConfig : IPecaConfig
    {
        public TipoPeca Tipo => TipoPeca.Bancada;

        [DataMember(Order = 1)] public string Codigo { get; set; } = "BC01";
        [DataMember(Order = 2)] public string Nome { get; set; } = "";
        [DataMember(Order = 3)] public string ModeloId { get; set; } = "";

        [DataMember(Order = 10)] public FormatoBancada Formato { get; set; }
        [DataMember(Order = 11)] public LadoRetorno Lado { get; set; }
        [DataMember(Order = 12)] public double ComprimentoAMm { get; set; } = 1800;
        /// <summary>L: return leg; U: left leg (measured from the back wall).</summary>
        [DataMember(Order = 13)] public double ComprimentoBMm { get; set; } = 1200;
        /// <summary>U: right leg.</summary>
        [DataMember(Order = 14)] public double ComprimentoCMm { get; set; } = 1200;
        [DataMember(Order = 15)] public double ProfundidadeMm { get; set; } = 600;
        [DataMember(Order = 16)] public double EspessuraMm { get; set; } = 20;
        /// <summary>Height of the top surface from the floor.</summary>
        [DataMember(Order = 17)] public double AlturaMm { get; set; } = 900;
        /// <summary>The left / right end of the run touches a wall.</summary>
        [DataMember(Order = 18)] public bool EncostoEsquerdo { get; set; } = true;
        [DataMember(Order = 19)] public bool EncostoDireito { get; set; }

        [DataMember(Order = 20)] public BordaFrontal Borda { get; set; } = BordaFrontal.Saia;
        [DataMember(Order = 21)] public double BordaAlturaMm { get; set; } = 40;
        [DataMember(Order = 22)] public double TiraLarguraMm { get; set; } = 50;
        [DataMember(Order = 23)] public bool Tento { get; set; }
        [DataMember(Order = 24)] public double TentoAlturaMm { get; set; } = 15;
        [DataMember(Order = 25)] public double TentoLarguraMm { get; set; } = 20;
        [DataMember(Order = 26)] public bool Frontao { get; set; } = true;
        [DataMember(Order = 27)] public double FrontaoAlturaMm { get; set; } = 100;
        [DataMember(Order = 28)] public double FrontaoEspessuraMm { get; set; } = 20;
        [DataMember(Order = 29)] public bool FrontaoLaterais { get; set; } = true;
        [DataMember(Order = 30)] public bool PeEsquerdo { get; set; }
        [DataMember(Order = 31)] public bool PeDireito { get; set; }
        [DataMember(Order = 32)] public double PeEspessuraMm { get; set; } = 30;

        [DataMember(Order = 40)] public List<Cuba> Cubas { get; set; } = new List<Cuba>();
        [DataMember(Order = 41)] public bool Cooktop { get; set; }
        [DataMember(Order = 42)] public double CooktopLarguraMm { get; set; } = 560;
        [DataMember(Order = 43)] public double CooktopProfundidadeMm { get; set; } = 480;
        /// <summary>Centre from the left end of A; 0 = right third of the run.</summary>
        [DataMember(Order = 44)] public double CooktopPosicaoXMm { get; set; }

        // ---- angles (degrees; 90 = square) and chamfers
        /// <summary>L: angle between the main run and the return; U: left leg. Inside angle, 90 = square.</summary>
        [DataMember(Order = 60)] public double AnguloBGraus { get; set; } = 90;
        /// <summary>U: angle of the right leg.</summary>
        [DataMember(Order = 61)] public double AnguloCGraus { get; set; } = 90;
        /// <summary>Cut of the left / right end, inside angle at the wall side. 90 = square; less = shorter front.</summary>
        [DataMember(Order = 62)] public double PontaEsqGraus { get; set; } = 90;
        [DataMember(Order = 63)] public double PontaDirGraus { get; set; } = 90;
        /// <summary>Plan chamfer of the free front corner at each end (mm along each side; 0 = none).</summary>
        [DataMember(Order = 64)] public double ChanfroCantoEsqMm { get; set; }
        [DataMember(Order = 65)] public double ChanfroCantoDirMm { get; set; }
        /// <summary>Profile of the top arris of the free edges.</summary>
        [DataMember(Order = 66)] public PerfilBorda Perfil { get; set; }
        /// <summary>Chamfer of the arris: horizontal size and angle (45° = equal sides).</summary>
        [DataMember(Order = 67)] public double ChanfroBordaMm { get; set; } = 5;
        [DataMember(Order = 68)] public double ChanfroBordaGraus { get; set; } = 45;
        /// <summary>Saia joined to the top at 45° (no joint line seen from the front/top). Fabrication data.</summary>
        [DataMember(Order = 69)] public bool MeiaEsquadria { get; set; }

        // ---- per run (1.8.0): depth of the legs and which edges get saia / frontão
        /// <summary>Depth of leg B (L: the return; U: left leg). 0 = same as the main run.</summary>
        [DataMember(Order = 70)] public double ProfundidadeBMm { get; set; }
        /// <summary>Depth of leg C (U: right leg). 0 = same as the main run.</summary>
        [DataMember(Order = 71)] public double ProfundidadeCMm { get; set; }
        /// <summary>Free edges WITHOUT saia / tira: "A", "B", "C" (front of each run), "Esq", "Dir" (free ends). Empty = all.</summary>
        [DataMember(Order = 72)] public List<string> SemBorda { get; set; } = new List<string>();
        /// <summary>Wall edges WITHOUT frontão: "A" (back), "B", "C" (wall side of the legs), "Esq", "Dir" (ends against a wall). Empty = all.</summary>
        [DataMember(Order = 73)] public List<string> SemFrontao { get; set; } = new List<string>();

        // ---- free form, drawn in place (1.12.0)
        /// <summary>Formato Livre: outline of the top in plan (mm, family coordinates; either direction).</summary>
        [DataMember(Order = 80)] public List<Pt> Contorno { get; set; } = new List<Pt>();
        /// <summary>Formato Livre: edge i goes from Contorno[i] to Contorno[i + 1].</summary>
        [DataMember(Order = 81)] public List<BordaLocal> Arestas { get; set; } = new List<BordaLocal>();
        /// <summary>Formato Livre: dividing lines between the areas (dry / wet).</summary>
        [DataMember(Order = 82)] public List<Divisao> Divisoes { get; set; } = new List<Divisao>();
        /// <summary>Formato Livre: a point inside each WET area (the other areas are dry).</summary>
        [DataMember(Order = 83)] public List<Pt> Molhadas { get; set; } = new List<Pt>();
        /// <summary>Wet areas: how much lower than the dry top (0 = same level, only divided).</summary>
        [DataMember(Order = 84)] public double RebaixoMm { get; set; } = 10;
        /// <summary>Wet areas: width of the rim that comes back up to the dry level on the free edges (0 = none).</summary>
        [DataMember(Order = 85)] public double ContencaoMm { get; set; }
        /// <summary>Reta / L / U: the wet area goes from … to … along the main run (mm from its left end; both 0 = no wet area).</summary>
        [DataMember(Order = 86)] public double MolhadaDeMm { get; set; }
        [DataMember(Order = 87)] public double MolhadaAteMm { get; set; }
        /// <summary>Formato Livre: centre and rotation of the cooktop cut-out.</summary>
        [DataMember(Order = 88)] public double CooktopPosicaoYMm { get; set; }
        [DataMember(Order = 89)] public double CooktopAnguloGraus { get; set; }
        /// <summary>Island: the back (and, in L / U, the wall sides of the legs) is free — saia on both sides, no frontão.</summary>
        [DataMember(Order = 90)] public bool FundoLivre { get; set; }
        /// <summary>Formato Livre: extra round holes placed in the project.</summary>
        [DataMember(Order = 91)] public List<Furo> Furos { get; set; } = new List<Furo>();

        public bool Livre => Formato == FormatoBancada.Livre;
        public bool TemAreaMolhada => Livre ? Molhadas != null && Molhadas.Count > 0 : MolhadaAteMm > MolhadaDeMm + 50;

        public double ProfB => ProfundidadeBMm > 0 ? ProfundidadeBMm : ProfundidadeMm;
        public double ProfC => ProfundidadeCMm > 0 ? ProfundidadeCMm : ProfundidadeMm;
        public bool TemBorda(string trecho) => SemBorda == null || !SemBorda.Contains(trecho);
        public bool TemFrontao(string trecho) => SemFrontao == null || !SemFrontao.Contains(trecho);

        public void Marcar(List<string> lista, string trecho, bool tem)
        {
            if (tem) lista.RemoveAll(x => x == trecho);
            else if (!lista.Contains(trecho)) lista.Add(trecho);
        }

        [DataMember(Order = 50)] public string MaterialPedra { get; set; } = "Granito São Gabriel";
        [DataMember(Order = 51)] public string CorPedra { get; set; } = "2E2E30";
        [DataMember(Order = 52)] public string MaterialCuba { get; set; } = "Aço inox escovado";
        [DataMember(Order = 53)] public string CorCuba { get; set; } = "C8CCD0";

        public BancadaConfig Normalized()
        {
            if (string.IsNullOrWhiteSpace(Codigo)) Codigo = "BC01";
            if (Nome == null) Nome = "";
            if (ModeloId == null) ModeloId = "";
            if (Cubas == null) Cubas = new List<Cuba>();
            if (SemBorda == null) SemBorda = new List<string>();
            if (SemFrontao == null) SemFrontao = new List<string>();
            if (Contorno == null) Contorno = new List<Pt>();
            Contorno.RemoveAll(q => q == null);
            if (Arestas == null) Arestas = new List<BordaLocal>();
            while (Arestas.Count < Contorno.Count) Arestas.Add(BordaLocal.Livre);
            if (Arestas.Count > Contorno.Count) Arestas.RemoveRange(Contorno.Count, Arestas.Count - Contorno.Count);
            if (Divisoes == null) Divisoes = new List<Divisao>();
            Divisoes.RemoveAll(d => d == null || d.Pontos == null || d.Pontos.Count(q => q != null) < 2);
            foreach (var d in Divisoes) d.Pontos.RemoveAll(q => q == null);
            if (Furos == null) Furos = new List<Furo>();
            Furos.RemoveAll(q => q == null);
            foreach (var fu in Furos) fu.DiametroMm = fu.DiametroMm <= 0 ? 35 : PecaUtil.Clamp(fu.DiametroMm, 5, 400);
            if (Molhadas == null) Molhadas = new List<Pt>();
            Molhadas.RemoveAll(q => q == null);
            if (!Enum.IsDefined(typeof(FormatoBancada), Formato) || (Formato == FormatoBancada.Livre && Contorno.Count < 3)) Formato = FormatoBancada.Reta;
            RebaixoMm = PecaUtil.Clamp(RebaixoMm, 0, 100);
            ContencaoMm = PecaUtil.Clamp(ContencaoMm, 0, 200);
            MolhadaDeMm = Math.Max(0, MolhadaDeMm);
            MolhadaAteMm = Math.Max(0, MolhadaAteMm);
            ProfundidadeBMm = ProfundidadeBMm <= 0 ? 0 : PecaUtil.Clamp(ProfundidadeBMm, 200, 1500);
            ProfundidadeCMm = ProfundidadeCMm <= 0 ? 0 : PecaUtil.Clamp(ProfundidadeCMm, 200, 1500);
            Cubas.RemoveAll(c => c == null);
            ComprimentoAMm = PecaUtil.Clamp(ComprimentoAMm, 300, 12000);
            ProfundidadeMm = PecaUtil.Clamp(ProfundidadeMm, 200, 1500);
            ComprimentoBMm = PecaUtil.Clamp(ComprimentoBMm, ProfundidadeMm + 100, 8000);
            ComprimentoCMm = PecaUtil.Clamp(ComprimentoCMm, ProfundidadeMm + 100, 8000);
            EspessuraMm = PecaUtil.Clamp(EspessuraMm <= 0 ? 20 : EspessuraMm, 10, 100);
            // free form (no lugar) can also be a floor piece: threshold, step, bench top at any height
            AlturaMm = PecaUtil.Clamp(AlturaMm <= 0 ? 900 : AlturaMm, Formato == FormatoBancada.Livre ? EspessuraMm : 300, 1500);
            BordaAlturaMm = PecaUtil.Clamp(BordaAlturaMm <= 0 ? 40 : BordaAlturaMm, 5, 400);
            TiraLarguraMm = PecaUtil.Clamp(TiraLarguraMm <= 0 ? 50 : TiraLarguraMm, 10, 200);
            TentoAlturaMm = PecaUtil.Clamp(TentoAlturaMm <= 0 ? 15 : TentoAlturaMm, 3, 100);
            TentoLarguraMm = PecaUtil.Clamp(TentoLarguraMm <= 0 ? 20 : TentoLarguraMm, 5, 100);
            FrontaoAlturaMm = PecaUtil.Clamp(FrontaoAlturaMm <= 0 ? 100 : FrontaoAlturaMm, 10, 2000);
            FrontaoEspessuraMm = PecaUtil.Clamp(FrontaoEspessuraMm <= 0 ? 20 : FrontaoEspessuraMm, 5, 60);
            PeEspessuraMm = PecaUtil.Clamp(PeEspessuraMm <= 0 ? 30 : PeEspessuraMm, 10, 100);
            CooktopLarguraMm = PecaUtil.Clamp(CooktopLarguraMm <= 0 ? 560 : CooktopLarguraMm, 200, 1200);
            CooktopProfundidadeMm = PecaUtil.Clamp(CooktopProfundidadeMm <= 0 ? 480 : CooktopProfundidadeMm, 200, 800);
            foreach (var c in Cubas)
            {
                c.LarguraMm = PecaUtil.Clamp(c.LarguraMm <= 0 ? 500 : c.LarguraMm, 100, 3000);
                c.ProfundidadeMm = PecaUtil.Clamp(c.ProfundidadeMm <= 0 ? 400 : c.ProfundidadeMm, 100, 1000);
                c.RaioMm = PecaUtil.Clamp(c.RaioMm, 0, 200);
                c.FundoMm = PecaUtil.Clamp(c.FundoMm <= 0 ? 180 : c.FundoMm, 30, 500);
                if (c.PosicaoXMm < 0 && Formato != FormatoBancada.Livre) c.PosicaoXMm = 0;
                if (c.RecuoFrenteMm < 0) c.RecuoFrenteMm = 0;
                if (double.IsNaN(c.AnguloGraus) || double.IsInfinity(c.AnguloGraus)) c.AnguloGraus = 0;
                c.SeptoMm = PecaUtil.Clamp(c.SeptoMm <= 0 ? 60 : c.SeptoMm, 10, 400);
                c.ProporcaoPct = PecaUtil.Clamp(c.ProporcaoPct <= 0 ? 50 : c.ProporcaoPct, 20, 80);
                if (c.Dupla && c.LarguraMm < 2 * 150 + c.SeptoMm) c.LarguraMm = 2 * 150 + c.SeptoMm;
            }
            // older configurations have no angles (0): square
            AnguloBGraus = PecaUtil.Clamp(AnguloBGraus <= 0 ? 90 : AnguloBGraus, 30, 170);
            AnguloCGraus = PecaUtil.Clamp(AnguloCGraus <= 0 ? 90 : AnguloCGraus, 30, 170);
            PontaEsqGraus = PecaUtil.Clamp(PontaEsqGraus <= 0 ? 90 : PontaEsqGraus, 15, 165);
            PontaDirGraus = PecaUtil.Clamp(PontaDirGraus <= 0 ? 90 : PontaDirGraus, 15, 165);
            ChanfroCantoEsqMm = PecaUtil.Clamp(ChanfroCantoEsqMm, 0, 1000);
            ChanfroCantoDirMm = PecaUtil.Clamp(ChanfroCantoDirMm, 0, 1000);
            ChanfroBordaMm = PecaUtil.Clamp(ChanfroBordaMm <= 0 ? 5 : ChanfroBordaMm, 1, 50);
            ChanfroBordaGraus = PecaUtil.Clamp(ChanfroBordaGraus <= 0 ? 45 : ChanfroBordaGraus, 10, 80);
            if (string.IsNullOrWhiteSpace(MaterialPedra)) MaterialPedra = "Granito São Gabriel";
            if (string.IsNullOrWhiteSpace(CorPedra)) CorPedra = "2E2E30";
            if (string.IsNullOrWhiteSpace(MaterialCuba)) MaterialCuba = "Aço inox escovado";
            if (string.IsNullOrWhiteSpace(CorCuba)) CorCuba = "C8CCD0";
            return this;
        }

        public string ToJson() => Json.Serialize(this);
        public IPecaConfig Copiar() => Json.Deserialize<BancadaConfig>(ToJson()).Normalized();

        public IList<MaterialDef> Materiais() => new List<MaterialDef>
        {
            new MaterialDef { Key = "pedra", Parametro = "Material da pedra", Nome = MaterialPedra.Trim(), Hex = CorPedra },
            new MaterialDef { Key = "cuba", Parametro = "Material da cuba", Nome = MaterialCuba.Trim(), Hex = CorCuba },
        };

        public string MaterialPrincipal() => MaterialPedra;

        public string Dimensoes()
        {
            if (Livre)
            {
                Poligono.Envolvente(Contorno.Select(q => q.P).ToList(), out double comp, out double larg);
                return PecaUtil.Mm(comp) + " x " + PecaUtil.Mm(larg) + " x " + PecaUtil.Mm(EspessuraMm) + " mm, h " + PecaUtil.Mm(AlturaMm);
            }
            string s = Formato == FormatoBancada.Reta ? PecaUtil.Mm(ComprimentoAMm)
                     : Formato == FormatoBancada.L ? PecaUtil.Mm(ComprimentoAMm) + " + " + PecaUtil.Mm(ComprimentoBMm)
                     : PecaUtil.Mm(ComprimentoBMm) + " + " + PecaUtil.Mm(ComprimentoAMm) + " + " + PecaUtil.Mm(ComprimentoCMm);
            string p = PecaUtil.Mm(ProfundidadeMm);
            if (Formato == FormatoBancada.L && Math.Abs(ProfB - ProfundidadeMm) > 0.5) p += "/" + PecaUtil.Mm(ProfB);
            if (Formato == FormatoBancada.U && (Math.Abs(ProfB - ProfundidadeMm) > 0.5 || Math.Abs(ProfC - ProfundidadeMm) > 0.5))
                p = PecaUtil.Mm(ProfB) + "/" + p + "/" + PecaUtil.Mm(ProfC);
            return s + " x " + p + " x " + PecaUtil.Mm(EspessuraMm) + " mm, h " + PecaUtil.Mm(AlturaMm);
        }

        public string Descricao()
        {
            string f = Formato == FormatoBancada.Reta ? "reta" : Formato == FormatoBancada.L ? "em L" : Formato == FormatoBancada.U ? "em U" : "sob medida (modelada no local)";
            var parts = new List<string> { "Bancada " + f + " " + Dimensoes() };
            if (Cubas.Count > 0)
                parts.Add(string.Join(" + ", Cubas.GroupBy(c => (c.Tipo, c.Dupla)).Select(g =>
                    (g.Count() > 1 ? g.Count() + (g.Key.Dupla ? " cubas duplas " : " cubas ") : (g.Key.Dupla ? "cuba dupla " : "cuba ")) + NomeCuba(g.Key.Tipo).ToLowerInvariant())));
            if (Cooktop) parts.Add("recorte p/ cooktop");
            if (Formato == FormatoBancada.L && Math.Abs(AnguloBGraus - 90) > 0.1) parts.Add("retorno a " + Graus(AnguloBGraus));
            if (Formato == FormatoBancada.U && (Math.Abs(AnguloBGraus - 90) > 0.1 || Math.Abs(AnguloCGraus - 90) > 0.1))
                parts.Add("lados a " + Graus(AnguloBGraus) + " e " + Graus(AnguloCGraus));
            if (Math.Abs(PontaEsqGraus - 90) > 0.1 || Math.Abs(PontaDirGraus - 90) > 0.1)
                parts.Add("pontas a " + Graus(PontaEsqGraus) + " / " + Graus(PontaDirGraus));
            if (ChanfroCantoEsqMm > 0 || ChanfroCantoDirMm > 0)
                parts.Add("cantos chanfrados " + PecaUtil.Mm(Math.Max(ChanfroCantoEsqMm, ChanfroCantoDirMm)) + " mm");
            if (Borda != BordaFrontal.Reta) parts.Add((Borda == BordaFrontal.Saia ? "saia " : "tira americana ") + PecaUtil.Mm(BordaAlturaMm) + " mm" +
                                                      (Borda == BordaFrontal.Saia && MeiaEsquadria ? " em meia-esquadria" : ""));
            if (Perfil == PerfilBorda.Chanfrada) parts.Add("borda chanfrada " + PecaUtil.Mm(ChanfroBordaMm) + " mm a " + Graus(ChanfroBordaGraus));
            if (TemAreaMolhada)
                parts.Add("área molhada" + (RebaixoMm > 0 ? " rebaixada " + PecaUtil.Mm(RebaixoMm) + " mm" : "") +
                          (RebaixoMm > 0 && ContencaoMm > 0 && !Tento ? " com borda de contenção" : ""));
            if (Livre ? Arestas.Contains(BordaLocal.Parede) : Frontao)
                parts.Add("frontão " + PecaUtil.Mm(FrontaoAlturaMm) + " mm" + (!Livre && SemFrontao.Count > 0 ? " (parcial)" : ""));
            if (!Livre && Borda != BordaFrontal.Reta && SemBorda.Count > 0) parts.Add("sem " + (Borda == BordaFrontal.Saia ? "saia" : "tira") + " em " + string.Join(", ", SemBorda.Select(NomeTrecho)));
            if (FundoLivre && !Livre) parts.Add("ilha (fundo livre)");
            if (Tento) parts.Add("tento");
            parts.Add(MaterialPedra);
            string d = string.Join("; ", parts);
            return string.IsNullOrWhiteSpace(Nome) ? d : Nome.Trim() + " – " + d;
        }

        public static string NomeTrecho(string k) => k == "Esq" ? "ponta esquerda" : k == "Dir" ? "ponta direita" : "trecho " + k;

        private static string Graus(double g) => g.ToString("0.#", PecaUtil.Pt) + "°";

        public static string NomeCuba(TipoCuba t) =>
            t == TipoCuba.Embutir ? "De embutir" : t == TipoCuba.Sobrepor ? "De sobrepor" : t == TipoCuba.Esculpida ? "Esculpida" : "De apoio";

        // =========================================================================================
        // geometry
        // =========================================================================================
        private enum Aresta { Fundo, Parede, Frente, PontaEsq, PontaDir }

        /// <summary>Outline of the top (plan) and what each edge gets. Runs on a normalised copy.</summary>
        private void Contornar(PecaModelo m, out List<P2> pts, out List<ArestaBancada> arestas)
        {
            arestas = new List<ArestaBancada>();
            if (Livre)
            {
                pts = Contorno.Select(q => q.P).ToList();
                for (int i = 0; i < pts.Count; i++)
                {
                    BordaLocal t = i < Arestas.Count ? Arestas[i] : BordaLocal.Livre;
                    bool parede = t == BordaLocal.Parede || t == BordaLocal.ParedeSemFrontao;
                    arestas.Add(new ArestaBancada
                    {
                        Parede = parede,
                        Frontao = t == BordaLocal.Parede,
                        Borda = t == BordaLocal.Livre || t == BordaLocal.Pe,
                        Pe = t == BordaLocal.Pe
                    });
                }
                return;
            }
            double A = ComprimentoAMm, P = ProfundidadeMm, B = ComprimentoBMm, C = ComprimentoCMm;
            bool left = Lado == LadoRetorno.Esquerda;
            List<Aresta> tags;
            List<string> trechos;
            // a leg (return) leaving the back wall at the left (dir = +1) or right (dir = −1) corner,
            // at an inside angle θ to the main run: wall-side end, inner end, inner corner on the run's front
            void Perna(double theta, int dir, double len, double Pl, out P2 endOut, out P2 endIn, out P2 corner)
            {
                double t = theta * Math.PI / 180;
                P2 o = dir > 0 ? new P2(0, 0) : new P2(A, 0);
                double ux = dir * Math.Cos(t), uy = -Math.Sin(t);
                double nx = dir > 0 ? -uy : uy, ny = dir > 0 ? ux : -ux;     // towards the inside of the leg
                endOut = new P2(o.X + ux * len, o.Y + uy * len);
                endIn = new P2(endOut.X + nx * Pl, endOut.Y + ny * Pl);
                double k = (-P - endIn.Y) / uy;                              // inner edge meets the run's front (y = −P)
                corner = new P2(endIn.X + ux * k, -P);
                if (-k < 50) m.Avisos.Add("Retorno curto demais para esse ângulo: aumente o comprimento do retorno.");
            }
            switch (Formato)
            {
                case FormatoBancada.L when left:
                {
                    Perna(AnguloBGraus, +1, B, ProfB, out P2 eo, out P2 ei, out P2 ic);
                    pts = new List<P2> { new P2(0, 0), new P2(A, 0), new P2(A, -P), ic, ei, eo };
                    tags = new List<Aresta> { Aresta.Fundo, Aresta.PontaDir, Aresta.Frente, Aresta.Frente, Aresta.PontaEsq, Aresta.Parede };
                    trechos = new List<string> { "A", "A", "A", "B", "B", "B" };
                    break;
                }
                case FormatoBancada.L:
                {
                    Perna(AnguloBGraus, -1, B, ProfB, out P2 eo, out P2 ei, out P2 ic);
                    pts = new List<P2> { new P2(0, 0), new P2(A, 0), eo, ei, ic, new P2(0, -P) };
                    tags = new List<Aresta> { Aresta.Fundo, Aresta.Parede, Aresta.PontaDir, Aresta.Frente, Aresta.Frente, Aresta.PontaEsq };
                    trechos = new List<string> { "A", "B", "B", "B", "A", "A" };
                    break;
                }
                case FormatoBancada.U:
                {
                    Perna(AnguloBGraus, +1, B, ProfB, out P2 lo, out P2 li, out P2 lc);
                    Perna(AnguloCGraus, -1, C, ProfC, out P2 ro, out P2 ri, out P2 rc);
                    pts = new List<P2> { new P2(0, 0), new P2(A, 0), ro, ri, rc, lc, li, lo };
                    tags = new List<Aresta> { Aresta.Fundo, Aresta.Parede, Aresta.PontaDir, Aresta.Frente, Aresta.Frente, Aresta.Frente, Aresta.PontaEsq, Aresta.Parede };
                    trechos = new List<string> { "A", "C", "C", "C", "A", "B", "B", "B" };
                    if (rc.X - lc.X < 200) m.Avisos.Add("Bancada em U: o comprimento A precisa ser maior que duas profundidades.");
                    break;
                }
                default:
                    pts = new List<P2> { new P2(0, 0), new P2(A, 0), new P2(A, -P), new P2(0, -P) };
                    tags = new List<Aresta> { Aresta.Fundo, Aresta.PontaDir, Aresta.Frente, Aresta.PontaEsq };
                    trechos = new List<string> { "A", "A", "A", "A" };
                    break;
            }
            // ends cut at an angle, then chamfered front corners
            CortarPonta(pts, tags, Aresta.PontaEsq, PontaEsqGraus, m);
            CortarPonta(pts, tags, Aresta.PontaDir, PontaDirGraus, m);
            if (!EncostoEsquerdo) ChanfrarCanto(pts, tags, trechos, Aresta.PontaEsq, ChanfroCantoEsqMm, m);
            else if (ChanfroCantoEsqMm > 0) m.Avisos.Add("Canto esquerdo encostado na parede: o chanfro só é feito em pontas livres.");
            if (!EncostoDireito) ChanfrarCanto(pts, tags, trechos, Aresta.PontaDir, ChanfroCantoDirMm, m);
            else if (ChanfroCantoDirMm > 0) m.Avisos.Add("Canto direito encostado na parede: o chanfro só é feito em pontas livres.");


            for (int i = 0; i < pts.Count; i++)
            {
                Aresta t = tags[i];
                bool ponta = t == Aresta.PontaEsq || t == Aresta.PontaDir;
                var ed = new ArestaBancada { Chave = t == Aresta.PontaEsq ? "Esq" : t == Aresta.PontaDir ? "Dir" : trechos[i] };
                bool fundo = t == Aresta.Fundo || t == Aresta.Parede;
                if (fundo && FundoLivre) ed.Chave = "Fundo";       // its own switch in "Por trecho e lado"
                ed.Parede = (fundo && !FundoLivre) || (t == Aresta.PontaEsq && EncostoEsquerdo) || (t == Aresta.PontaDir && EncostoDireito);
                ed.Frontao = ed.Parede && Frontao && TemFrontao(ed.Chave) && (!ponta || FrontaoLaterais);
                ed.Borda = !ed.Parede && TemBorda(ed.Chave);
                ed.Pe = !ed.Parede && ((t == Aresta.PontaEsq && PeEsquerdo) || (t == Aresta.PontaDir && PeDireito));
                arestas.Add(ed);
            }
        }

        /// <summary>The areas of the top: the outline cut by the dividing lines, each one dry or wet. Runs on a normalised copy.</summary>
        private List<ZonaBancada> Zonear(PecaModelo m, List<P2> pts, List<ArestaBancada> arestas)
        {
            var zonas = new List<ZonaBancada> { new ZonaBancada { Pts = new List<P2>(pts), Arestas = new List<ArestaBancada>(arestas) } };
            var caminhos = new List<List<P2>>();
            var marcas = new List<P2>();
            if (Livre)
            {
                caminhos.AddRange(Divisoes.Select(d => d.Pontos.Select(q => q.P).ToList()));
                marcas.AddRange(Molhadas.Select(q => q.P));
            }
            else if (TemAreaMolhada)
            {
                double A = ComprimentoAMm, P = ProfundidadeMm;
                double x0 = Math.Max(0, MolhadaDeMm), x1 = Math.Min(A, MolhadaAteMm);
                if (x1 - x0 < 50) m.Avisos.Add("Área molhada: confira o início e o fim (fora do trecho principal).");
                else
                {
                    if (x0 > 1) caminhos.Add(new List<P2> { new P2(x0, 0), new P2(x0, -P) });
                    if (x1 < A - 1) caminhos.Add(new List<P2> { new P2(x1, 0), new P2(x1, -P) });
                    marcas.Add(new P2((x0 + x1) / 2, -P / 2));
                }
            }
            int n = 0;
            foreach (var cam in caminhos)
            {
                n++;
                if (!Dividir(zonas, cam, out string erro)) m.Avisos.Add((Livre ? "Divisão " + n : "Área molhada") + ": " + erro);
            }
            foreach (var z in zonas) z.Molhada = marcas.Any(q => Poligono.Dentro(z.Pts, q));
            return zonas;
        }

        /// <summary>The areas of the top in plan (for the editors: drawing, picking an area, numbers).</summary>
        public List<ZonaBancada> Zonas(List<string> avisos = null)
        {
            var c = (BancadaConfig)Copiar();
            var m = new PecaModelo();
            c.Contornar(m, out List<P2> pts, out List<ArestaBancada> ar);
            var zonas = pts.Count < 3 ? new List<ZonaBancada>() : c.Zonear(m, pts, ar);
            avisos?.AddRange(m.Avisos);
            return zonas;
        }

        /// <summary>A dividing path fitted to the top as it is now (already cut by the other lines), or null with the reason.</summary>
        public List<P2> AjustarDivisao(IList<P2> caminho, out string erro)
        {
            var zonas = Zonas();
            erro = "a linha não atravessa o tampo.";
            foreach (var z in Candidatas(zonas, caminho))
                if (Poligono.Ajustar(z.Pts, caminho, out List<P2> res, out erro)) return res;
            return null;
        }

        private static IEnumerable<ZonaBancada> Candidatas(List<ZonaBancada> zonas, IList<P2> caminho)
        {
            if (caminho == null || caminho.Count < 2) return zonas;
            P2 alvo = caminho.Count == 2 ? Poligono.Meio(caminho[0], caminho[1]) : caminho[1];
            var dentro = zonas.Where(z => Poligono.Dentro(z.Pts, alvo)).ToList();
            return dentro.Count > 0 ? dentro : zonas;
        }

        /// <summary>Cuts the area that holds the path in two along it.</summary>
        private static bool Dividir(List<ZonaBancada> zonas, IList<P2> caminho, out string erro)
        {
            erro = "a linha não atravessa o tampo.";
            foreach (var z in Candidatas(zonas, caminho).ToList())
            {
                if (!Poligono.Ajustar(z.Pts, caminho, out List<P2> cam, out erro)) continue;
                var pts = new List<P2>(z.Pts);
                var ar = new List<ArestaBancada>(z.Arestas);
                int i = NaBorda(pts, ar, cam[0]);
                int j = NaBorda(pts, ar, cam[cam.Count - 1]);
                if (i < 0 || j < 0) { erro = "a linha não chega à borda do tampo."; continue; }
                // the second insertion may have pushed the first point one place on
                if (Poligono.Dist(pts[i], cam[0]) > Poligono.Tol) i = (i + 1) % pts.Count;
                if (i == j) { erro = "a linha começa e termina no mesmo ponto."; continue; }
                var meio = cam.Skip(1).Take(cam.Count - 2).ToList();
                var divisa = new ArestaBancada { Divisa = true };
                int n = pts.Count;

                var a = new ZonaBancada();
                for (int k = i; k != j; k = (k + 1) % n) { a.Pts.Add(pts[k]); a.Arestas.Add(ar[k]); }
                a.Pts.Add(pts[j]); a.Arestas.Add(divisa);
                for (int k = meio.Count - 1; k >= 0; k--) { a.Pts.Add(meio[k]); a.Arestas.Add(divisa); }

                var b = new ZonaBancada();
                for (int k = j; k != i; k = (k + 1) % n) { b.Pts.Add(pts[k]); b.Arestas.Add(ar[k]); }
                b.Pts.Add(pts[i]); b.Arestas.Add(divisa);
                foreach (P2 q in meio) { b.Pts.Add(q); b.Arestas.Add(divisa); }

                double s0 = Peca.Area(z.Pts), sa = Peca.Area(a.Pts), sb = Peca.Area(b.Pts);
                if (Math.Abs(sa) < 2500 || Math.Abs(sb) < 2500 || Math.Sign(sa) != Math.Sign(s0) || Math.Sign(sb) != Math.Sign(s0))
                {
                    erro = "a linha sai do tampo ou não separa duas áreas.";
                    continue;
                }
                int at = zonas.IndexOf(z);
                zonas[at] = a;
                zonas.Insert(at + 1, b);
                erro = null;
                return true;
            }
            return false;
        }

        /// <summary>Index of the vertex at p, adding it on the edge it lies on; −1 if p is not on the boundary.</summary>
        private static int NaBorda(List<P2> pts, List<ArestaBancada> ar, P2 p)
        {
            for (int k = 0; k < pts.Count; k++) if (Poligono.Dist(pts[k], p) <= Poligono.Tol) return k;
            if (Poligono.DistBorda(pts, p, out int e, out _) > Poligono.Tol) return -1;
            pts.Insert(e + 1, p);
            ar.Insert(e + 1, ar[e]);
            return e + 1;
        }

        public PecaModelo Build()
        {
            var c = (BancadaConfig)Copiar();
            var m = new PecaModelo();
            c.Contornar(m, out List<P2> pts, out List<ArestaBancada> arestas);
            if (pts.Count < 3) { m.Avisos.Add("Contorno da bancada incompleto."); return m; }
            List<ZonaBancada> zonas = c.Zonear(m, pts, arestas);
            double A = c.ComprimentoAMm, P = c.ProfundidadeMm;
            double H = c.AlturaMm, e = c.EspessuraMm;
            bool livre = c.Livre;
            // wet areas sit lower than the dry top
            double r = zonas.Any(z => z.Molhada) ? c.RebaixoMm : 0;
            foreach (var z in zonas)
            {
                z.Topo = z.Molhada ? H - r : H;
                z.Fechar();
            }
            ZonaBancada ZonaDe(P2 q) => zonas.FirstOrDefault(z => Poligono.Dentro(z.Pts, q));

            // chamfer of the top arris along an edge: a wedge (sh wide, sv high) whose top runs from
            // the edge at z0 up to sh inside at z0 + sv; drawn in the plane across the edge
            Peca Cunha(ArestaBancada ed, double sign, double larg, double alt, double z0)
            {
                double dx = ed.B.X - ed.A.X, dy = ed.B.Y - ed.A.Y, l = Math.Sqrt(dx * dx + dy * dy);
                var nrm = new V3(-dy / l * sign, dx / l * sign, 0);
                var axis = nrm.Cross(V3.Ez);                                // U × V = extrusion direction
                P2 start = axis.Dot(new V3(dx, dy, 0)) > 0 ? ed.A : ed.B;
                var cunha = new Peca { Nome = "Chanfro da borda", O = new V3(start.X, start.Y, z0), U = nrm, V = V3.Ez, Depth = l, Slot = "pedra" };
                cunha.Loops.Add(new List<P2> { new P2(0, 0), new P2(larg, 0), new P2(larg, alt) });
                return cunha;
            }

            // mitred chamfer: the wedge of edge i fills the band between the outline and the inset
            // top (its ends on the corner lines P→inset point, so the wedges of two chamfered edges
            // meet at 45° and the ones next to a wall end along the wall); built as a blend from the
            // band at z0 up to a 2 mm strip at the inner line, z0 + alt
            Peca CunhaMeiaEsquadria(List<P2> pts, List<P2> dentro, int i, double larg, double alt, double z0, Peca reta)
            {
                int n = pts.Count, j = (i + 1) % n;
                P2 p0 = pts[i], p1 = pts[j], q0 = dentro[i], q1 = dentro[j];
                var baseQ = new List<P2> { p0, p1, q1, q0 };
                // a twisted or vanishing band (short edges, deep chamfer): the straight wedge
                if (Math.Abs(Peca.Area(baseQ)) < 1 || Poligono.Dist(q0, q1) < 1 || Cruza(p0, q0, p1, q1)) return reta;
                double t = Math.Min(2, larg / 3) / larg;           // 2 mm: Revit refuses shorter lines
                P2 Em(P2 q, P2 p) => new P2(q.X + (p.X - q.X) * t, q.Y + (p.Y - q.Y) * t);
                var cunha = Peca.Laje("Chanfro da borda", baseQ, null, z0, z0 + alt, "pedra");
                cunha.Topo = new List<P2> { Em(q0, p0), Em(q1, p1), q1, q0 };
                cunha.Alternativa = reta;
                return cunha;
            }
            static bool Cruza(P2 a, P2 b, P2 c, P2 d)
            {
                double O(P2 p, P2 q, P2 r) => (q.X - p.X) * (r.Y - p.Y) - (q.Y - p.Y) * (r.X - p.X);
                return O(a, b, c) * O(a, b, d) < 0 && O(c, d, a) * O(c, d, b) < 0;
            }

            // ---------------------------------------------------- holes: sinks, cooktop, taps
            double area = Math.Abs(Peca.Area(pts));
            int n = 0;
            foreach (var cu in c.Cubas)
            {
                n++;
                double cx, cy, ang = 0;
                if (livre) { cx = cu.PosicaoXMm; cy = cu.PosicaoYMm; ang = cu.AnguloGraus; }
                else
                {
                    cx = cu.PosicaoXMm > 0 ? cu.PosicaoXMm : A / 2;
                    cy = cu.RecuoFrenteMm > 0 ? -P + cu.RecuoFrenteMm + cu.ProfundidadeMm / 2 : -P / 2;
                }
                double x0 = cx - cu.LarguraMm / 2, x1 = cx + cu.LarguraMm / 2, y0 = cy - cu.ProfundidadeMm / 2, y1 = cy + cu.ProfundidadeMm / 2;
                // free form: the sink is turned about its centre
                List<P2> R(List<P2> l) => Poligono.Girar(l, cx, cy, ang);
                ZonaBancada zc = ZonaDe(new P2(cx, cy));
                if (zc == null)
                {
                    m.Avisos.Add($"Cuba {n}: o centro está fora do tampo.");
                    zc = zonas[0];
                }
                else if (livre || zonas.Count > 1)
                {
                    if (R(Peca.Rect(x0 - 15, y0 - 15, x1 + 15, y1 + 15)).Any(q => !Poligono.Dentro(zc.Pts, q)))
                        m.Avisos.Add($"Cuba {n}: não cabe inteira " + (zonas.Count > 1 ? "na área em que está (confira as divisões)" : "no tampo") + ".");
                }
                if (!livre && (x0 < 30 || x1 > A - 30 || y0 < -P + 20 || y1 > -20))
                    m.Avisos.Add($"Cuba {n}: não cabe inteira no trecho A com essa posição (ajuste largura/posição).");
                double r0 = cu.RaioMm;
                double zTop = zc.Topo, zUnder = zc.Topo - e;
                // the bowls: one, or two side by side split by a stone bridge (double sink)
                var bacias = new List<(double a, double b, string nome)>();
                if (cu.Dupla)
                {
                    double util = cu.LarguraMm - cu.SeptoMm;
                    double wl = util * cu.ProporcaoPct / 100;
                    bacias.Add((x0, x0 + wl, "Cuba " + n + "a"));
                    bacias.Add((x0 + wl + cu.SeptoMm, x1, "Cuba " + n + "b"));
                }
                else bacias.Add((x0, x1, "Cuba " + n));
                double rb = cu.Dupla ? Math.Min(r0, Math.Min(bacias[0].b - bacias[0].a, bacias[1].b - bacias[1].a) / 2 - 1) : r0;
                rb = Math.Max(0, rb);
                foreach (var (ba, bb, nome) in bacias)
                {
                    switch (cu.Tipo)
                    {
                        case TipoCuba.Apoio:
                        {
                            // vessel on top: drain hole in the stone, bowl above
                            P2 dreno = Poligono.Girar(new P2((ba + bb) / 2, cy), cx, cy, ang);
                            zc.Holes.Add(Peca.Circle(dreno.X, dreno.Y, 25));
                            var outer = R(Peca.RoundRect(ba, y0, bb, y1, rb));
                            var inner = R(Peca.RoundRect(ba + 6, y0 + 6, bb - 6, y1 - 6, Math.Max(0, rb - 6)));
                            m.Pecas.Add(Peca.Laje(nome, outer, new[] { inner }, zTop, zTop + cu.FundoMm, "cuba"));
                            m.Pecas.Add(Peca.Laje("Fundo da " + nome.ToLowerInvariant(), outer, null, zTop, zTop + 6, "cuba"));
                            break;
                        }
                        case TipoCuba.Esculpida:
                        {
                            // carved in the stone itself: stone walls and bottom under the opening
                            zc.Holes.Add(R(Peca.RoundRect(ba, y0, bb, y1, rb)));
                            var outer = R(Peca.RoundRect(ba - e, y0 - e, bb + e, y1 + e, rb + e));
                            m.Pecas.Add(Peca.Laje("Paredes da " + nome.ToLowerInvariant(), outer, new[] { R(Peca.RoundRect(ba, y0, bb, y1, rb)) }, zUnder - cu.FundoMm, zUnder, "pedra"));
                            m.Pecas.Add(Peca.Laje("Fundo da " + nome.ToLowerInvariant(), outer, null, zUnder - cu.FundoMm - e, zUnder - cu.FundoMm, "pedra"));
                            area += 2 * ((bb - ba) + cu.ProfundidadeMm) * cu.FundoMm + (bb - ba) * cu.ProfundidadeMm;
                            break;
                        }
                        default:
                        {
                            zc.Holes.Add(R(Peca.RoundRect(ba, y0, bb, y1, rb)));
                            const double t = 3;
                            var outer = R(Peca.RoundRect(ba - t, y0 - t, bb + t, y1 + t, rb + t));
                            m.Pecas.Add(Peca.Laje(nome, outer, new[] { R(Peca.RoundRect(ba, y0, bb, y1, rb)) }, zUnder - cu.FundoMm, zUnder, "cuba"));
                            m.Pecas.Add(Peca.Laje("Fundo da " + nome.ToLowerInvariant(), outer, null, zUnder - cu.FundoMm - t, zUnder - cu.FundoMm, "cuba"));
                            break;
                        }
                    }
                    area -= cu.Tipo == TipoCuba.Apoio ? 0 : (bb - ba) * cu.ProfundidadeMm;
                }
                // drop-in sink: one rim over the whole unit (both bowls)
                if (cu.Tipo == TipoCuba.Sobrepor)
                    m.Pecas.Add(Peca.Laje("Aba da cuba " + n, R(Peca.RoundRect(x0 - 20, y0 - 20, x1 + 20, y1 + 20, r0 + 20)),
                        bacias.Select(bc => R(Peca.RoundRect(bc.a, y0, bc.b, y1, rb))).ToArray(), zTop, zTop + 4, "cuba"));
                if (cu.FuroTorneira && cu.Tipo != TipoCuba.Apoio)
                {
                    P2 tp = Poligono.Girar(new P2(cx, y1 + 55), cx, cy, ang);
                    bool cabe = livre || zonas.Count > 1
                        ? Poligono.Dentro(zc.Pts, tp) && Poligono.DistBorda(zc.Pts, tp, out _, out _) >= 25 + (livre ? c.FrontaoEspessuraMm : 0)
                        : true;
                    if (!livre) cabe = cabe && y1 + 55 < -(c.Frontao ? c.FrontaoEspessuraMm : 0) - 25;
                    if (cabe) zc.Holes.Add(Peca.Circle(tp.X, tp.Y, 17.5, 12));
                }
            }
            if (c.Cooktop)
            {
                double w = c.CooktopLarguraMm, d = c.CooktopProfundidadeMm;
                double cx = livre ? c.CooktopPosicaoXMm : c.CooktopPosicaoXMm > 0 ? c.CooktopPosicaoXMm : A * 0.75;
                double cy = livre ? c.CooktopPosicaoYMm : -P / 2;
                double ang = livre ? c.CooktopAnguloGraus : 0;
                ZonaBancada zk = ZonaDe(new P2(cx, cy));
                if (zk == null)
                {
                    m.Avisos.Add("Cooktop: o centro está fora do tampo.");
                    zk = zonas[0];
                }
                else if ((livre || zonas.Count > 1) && Poligono.Girar(Peca.Rect(cx - w / 2 - 15, cy - d / 2 - 15, cx + w / 2 + 15, cy + d / 2 + 15), cx, cy, ang).Any(q => !Poligono.Dentro(zk.Pts, q)))
                    m.Avisos.Add("Cooktop: o recorte não cabe inteiro " + (zonas.Count > 1 ? "na área em que está." : "no tampo."));
                zk.Holes.Add(Poligono.Girar(Peca.Rect(cx - w / 2, cy - d / 2, cx + w / 2, cy + d / 2), cx, cy, ang));
                area -= w * d;
                // burners drawn on the plan as circles (symbolic) — 4 points
                foreach (var q in new[] { (-0.25, -0.25), (0.25, -0.25), (-0.25, 0.25), (0.25, 0.25) })
                {
                    P2 bc = Poligono.Girar(new P2(cx + q.Item1 * w, cy + q.Item2 * d), cx, cy, ang);
                    var circ = Peca.Circle(bc.X, bc.Y, Math.Min(w, d) * 0.14, 16);
                    for (int i = 0; i < circ.Count; i++) m.Planta.Add(new LinhaPeca(circ[i], circ[(i + 1) % circ.Count], false));
                }
            }

            if (livre)
            {
                int nf = 0;
                foreach (var fu in c.Furos)
                {
                    nf++;
                    var centro = new P2(fu.X, fu.Y);
                    ZonaBancada zf = ZonaDe(centro);
                    if (zf == null || Poligono.DistBorda(zf.Pts, centro, out _, out _) < fu.DiametroMm / 2 + 10)
                    {
                        m.Avisos.Add($"Furo {nf}: fora do tampo ou perto demais da borda; não foi feito.");
                        continue;
                    }
                    zf.Holes.Add(Peca.Circle(fu.X, fu.Y, fu.DiametroMm / 2, 16));
                }
                if (c.Furos.Count > 0) m.Resumo.Add(c.Furos.Count + (c.Furos.Count == 1 ? " furo" : " furos"));
            }

            // ---------------------------------------------------- each area: top and edges
            bool Free(ArestaBancada ed) => !ed.Parede && !ed.Divisa;
            double sh = c.ChanfroBordaMm, svBase = sh * Math.Tan(c.ChanfroBordaGraus * Math.PI / 180);
            double frontLen = 0, splashLen = 0, molhada = 0;
            foreach (var z in zonas)
            {
                double Zt = z.Topo, sg = z.Sinal;
                // rim that brings the free edges of a lowered wet area back up to the dry level
                bool contencao = z.Molhada && r > 0.5 && c.ContencaoMm > 0 && !c.Tento;
                bool chanfro = c.Perfil == PerfilBorda.Chanfrada && !contencao && z.Arestas.Any(Free);
                double sv = svBase;
                string tampo = zonas.Count == 1 ? "Tampo" : z.Molhada ? "Tampo (área molhada)" : "Tampo (área seca)";
                if (chanfro && !c.Tento)
                {
                    sv = Math.Min(sv, e - 2);
                    m.Pecas.Add(Peca.Laje(tampo, z.Pts, z.Holes, Zt - e, Zt - sv, "pedra"));
                    List<P2> dentro = Recuar(z.Pts, z.Arestas.Select(ed => Free(ed) ? sh : 0).ToList(), sg);
                    m.Pecas.Add(Peca.Laje(tampo + " (topo)", dentro, z.Holes, Zt - sv, Zt, "pedra"));
                    for (int i = 0; i < z.Arestas.Count; i++)
                    {
                        if (!Free(z.Arestas[i])) continue;
                        Peca reta = Cunha(z.Arestas[i], sg, sh, sv, Zt - sv);
                        m.Pecas.Add(dentro.Count == z.Pts.Count && z.Arestas.Count == z.Pts.Count
                            ? CunhaMeiaEsquadria(z.Pts, dentro, i, sh, sv, Zt - sv, reta) : reta);
                    }
                }
                else m.Pecas.Add(Peca.Laje(tampo, z.Pts, z.Holes, Zt - e, Zt, "pedra"));
                if (z.Molhada) molhada += Math.Abs(Peca.Area(z.Pts));

                double zb = Zt - e;                      // underside of the top in this area
                double zSaia = H - e - c.BordaAlturaMm;   // bottom of the saia / tira: the same all around
                double fe = c.FrontaoEspessuraMm, pe = c.PeEspessuraMm;
                bool saia = c.Borda == BordaFrontal.Saia && zb - zSaia > 1, tira = c.Borda == BordaFrontal.TiraAmericana && zb - zSaia > 1;
                double wSaia = saia ? e : c.TiraLarguraMm;

                // A strip along edge i (from o to o + w inside, from z0 to z1). Its ends are cut so
                // that it meets the strips of the neighbour edges: "corte" tells, for a neighbour
                // edge, what stands there at these heights (see Poligono.Faixa).
                void Faixa(string nome, int i, double w, double z0, double z1, Func<ArestaBancada, double[]> corte, double o = 0)
                {
                    ArestaBancada antes = z.Arestas[Poligono.Vizinha(z.Pts, i, -1)], depois = z.Arestas[Poligono.Vizinha(z.Pts, i, 1)];
                    List<P2> poly = Poligono.Faixa(z.Pts, sg, i, o, w, corte(antes), corte(depois));
                    if (poly != null) m.Pecas.Add(Peca.Laje(nome, poly, null, z0, z1, "pedra"));
                }
                double[] rente = { 0, 0 };
                List<P2> foraTento = null, dentroTento = null;      // the tento's chamfer band (built on first use)
                // frontão: mitre with the next frontão; else it runs to the corner
                double[] CorteFrontao(ArestaBancada nb) => nb.Parede && nb.Frontao ? new[] { 0, fe } : rente;
                // saia / tira: mitre with the next one; stops at a stone leg
                double[] CorteSaia(ArestaBancada nb) => !Free(nb) ? rente : nb.Pe ? new[] { pe, pe } : nb.Borda ? new[] { 0, wSaia } : rente;
                // leg: mitre with the next leg
                double[] CortePe(ArestaBancada nb) => Free(nb) && nb.Pe ? new[] { 0, pe } : rente;
                // tento and containment rim (on every free edge of the area): mitre; they stop at the face of a frontão
                Func<ArestaBancada, double[]> CorteBorda(double o, double w) =>
                    nb => Free(nb) ? new[] { o, o + w } : nb.Parede && nb.Frontao ? new[] { fe, fe } : rente;

                for (int i = 0; i < z.Arestas.Count; i++)
                {
                    ArestaBancada ed = z.Arestas[i];
                    double L = ed.Length;
                    if (L < 0.5) continue;
                    if (ed.Divisa)
                    {
                        // a step taller than the stone: a riser on the dry side closes it
                        if (!z.Molhada && r > e - 2)
                        {
                            double dx = ed.B.X - ed.A.X, dy = ed.B.Y - ed.A.Y;
                            P2 mid = Poligono.Meio(ed.A, ed.B);
                            var fora = new P2(mid.X + dy / L * sg * 3, mid.Y - dx / L * sg * 3);
                            ZonaBancada viz = ZonaDe(fora);
                            if (viz != null && viz.Molhada)
                            {
                                Faixa("Espelho do rebaixo", i, e, H - r - e, H - e, nb => rente);
                                area += L * r;
                            }
                        }
                        continue;
                    }
                    if (ed.Parede)
                    {
                        if (ed.Frontao)
                        {
                            Faixa("Frontão", i, fe, Zt, H + c.FrontaoAlturaMm, CorteFrontao);
                            splashLen += L;
                            area += L * (c.FrontaoAlturaMm + (H - Zt));
                        }
                        continue;
                    }
                    frontLen += L;
                    // (an edge with a stone leg has no saia: the leg takes its place)
                    if (saia && ed.Borda && !ed.Pe)
                    {
                        Faixa("Saia", i, wSaia, zSaia, zb, CorteSaia);
                        area += L * (zb - zSaia);
                    }
                    else if (tira && ed.Borda && !ed.Pe)
                    {
                        Faixa("Tira americana", i, wSaia, zSaia, zb, CorteSaia);
                        area += L * (c.TiraLarguraMm + (zb - zSaia));
                    }
                    if (c.Tento && chanfro)
                    {
                        // the chamfer goes on the tento's arris
                        double tv = Math.Min(sv, c.TentoAlturaMm - 1), th = Math.Min(sh, c.TentoLarguraMm - 2);
                        double zt = H + c.TentoAlturaMm;
                        Faixa("Tento", i, c.TentoLarguraMm, Zt, zt - tv, CorteBorda(0, c.TentoLarguraMm));
                        Faixa("Tento (topo)", i, c.TentoLarguraMm - th, zt - tv, zt, CorteBorda(th, c.TentoLarguraMm - th), th);
                        // mitred like the tento itself: between the free edges at 45°, against a frontão at its face
                        if (foraTento == null)
                        {
                            List<double> Off(double livre) => z.Arestas.Select(a => Free(a) ? livre : a.Parede && a.Frontao ? fe : 0).ToList();
                            foraTento = Recuar(z.Pts, Off(0), sg);
                            dentroTento = Recuar(z.Pts, Off(th), sg);
                        }
                        Peca retaT = Cunha(ed, sg, th, tv, zt - tv);
                        m.Pecas.Add(foraTento.Count == z.Pts.Count && dentroTento.Count == z.Pts.Count && z.Arestas.Count == z.Pts.Count
                            ? CunhaMeiaEsquadria(foraTento, dentroTento, i, th, tv, zt - tv, retaT) : retaT);
                        area += L * (c.TentoLarguraMm + c.TentoAlturaMm + (H - Zt));
                    }
                    else if (c.Tento)
                    {
                        Faixa("Tento", i, c.TentoLarguraMm, Zt, H + c.TentoAlturaMm, CorteBorda(0, c.TentoLarguraMm));
                        area += L * (c.TentoLarguraMm + c.TentoAlturaMm + (H - Zt));
                    }
                    else if (contencao)
                    {
                        Faixa("Borda de contenção", i, c.ContencaoMm, Zt, H, CorteBorda(0, c.ContencaoMm));
                        area += L * (c.ContencaoMm + r);
                    }
                    if (ed.Pe)
                    {
                        Faixa("Pé", i, pe, 0, zb, CortePe);
                        area += L * zb;
                    }
                }
            }
            if (!livre && ((c.PeEsquerdo && c.EncostoEsquerdo && c.Formato != FormatoBancada.L) || (c.PeDireito && c.EncostoDireito)))
                m.Avisos.Add("Pé de pedra só é criado em pontas livres (sem encosto na parede).");
            if (r > e - 2 && r > 0) m.Avisos.Add("Rebaixo maior que a espessura da pedra: um espelho de pedra fecha o degrau entre as áreas.");

            m.AreaPedra = area / 1e6;
            m.AreaMolhada = molhada / 1e6;
            m.Resumo.Add("Pedra ≈ " + PecaUtil.M2(m.AreaPedra));
            if (zonas.Count > 1 || molhada > 0)
            {
                m.Resumo.Add("Área molhada " + PecaUtil.M2(molhada / 1e6));
                m.Resumo.Add("Área seca " + PecaUtil.M2((Math.Abs(Peca.Area(pts)) - molhada) / 1e6));
            }
            if (frontLen > 0) m.Resumo.Add("Bordas livres " + PecaUtil.M(frontLen / 1000));
            if (splashLen > 0) m.Resumo.Add("Frontão " + PecaUtil.M(splashLen / 1000));
            if (c.Cubas.Count > 0) m.Resumo.Add(c.Cubas.Count + (c.Cubas.Count == 1 ? " cuba" : " cubas") + (c.Cubas.Any(x => x.Dupla) ? " (dupla)" : ""));
            return m;
        }

        private static double Dist(P2 a, P2 b) => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));

        private static P2 Towards(P2 from, P2 to, double d)
        {
            double l = Dist(from, to);
            return l < 1e-9 ? from : new P2(from.X + (to.X - from.X) / l * d, from.Y + (to.Y - from.Y) / l * d);
        }

        /// <summary>
        /// The end edge tagged <paramref name="ponta"/> and its front vertex (the one shared with a
        /// front edge): index of the front vertex, index of the vertex at the wall side, and of the
        /// other end of the front edge. False if there is no such end.
        /// </summary>
        private static bool Ponta(List<P2> pts, List<Aresta> tags, Aresta ponta, out int front, out int back, out int along)
        {
            int n = pts.Count;
            front = back = along = -1;
            int i = tags.IndexOf(ponta);
            if (i < 0) return false;
            if (tags[(i + 1) % n] == Aresta.Frente) { front = (i + 1) % n; back = i; along = (i + 2) % n; return true; }
            if (tags[(i - 1 + n) % n] == Aresta.Frente) { front = i; back = (i + 1) % n; along = (i - 1 + n) % n; return true; }
            return false;
        }

        /// <summary>End cut at an angle: the front vertex slides along the front edge so the inside angle at the wall side is <paramref name="graus"/>.</summary>
        private static void CortarPonta(List<P2> pts, List<Aresta> tags, Aresta ponta, double graus, PecaModelo m)
        {
            if (Math.Abs(graus - 90) < 0.05 || !Ponta(pts, tags, ponta, out int f, out int b, out int a)) return;
            double w = Dist(pts[b], pts[f]);
            double d = w / Math.Tan(graus * Math.PI / 180);
            double max = Dist(pts[f], pts[a]) - 50;
            if (d > max) { d = max; m.Avisos.Add("Ponta em ângulo: o corte foi limitado para a frente não sumir (aumente o comprimento ou o ângulo)."); }
            pts[f] = Towards(pts[f], pts[a], d);
        }

        /// <summary>Straight chamfer of the free front corner of an end, <paramref name="s"/> mm along each side.</summary>
        private static void ChanfrarCanto(List<P2> pts, List<Aresta> tags, List<string> trechos, Aresta ponta, double s, PecaModelo m)
        {
            if (s <= 0 || !Ponta(pts, tags, ponta, out int f, out int b, out int a)) return;
            double max = Math.Min(Dist(pts[f], pts[b]), Dist(pts[f], pts[a])) - 10;
            if (s > max) { s = Math.Max(0, max); m.Avisos.Add("Canto chanfrado: o chanfro foi limitado ao tamanho da ponta."); }
            if (s < 1) return;
            int n = pts.Count;
            P2 v = pts[f];
            int prev = (f - 1 + n) % n, next = (f + 1) % n;
            P2 pa = Towards(v, pts[prev], s), pb = Towards(v, pts[next], s);
            pts[f] = pa;
            pts.Insert(f + 1, pb);
            // the chamfer edge belongs to the run of the front edge at that corner
            string tr = tags[(f - 1 + n) % n] == Aresta.Frente ? trechos[(f - 1 + n) % n] : trechos[f];
            tags.Insert(f, Aresta.Frente);   // new edge pa → pb; the old edge from v now starts at pb
            trechos.Insert(f, tr);
        }

        /// <summary>Outline with each edge moved inside by its own offset (0 = stays).</summary>
        private static List<P2> Recuar(List<P2> pts, List<double> off, double sign)
        {
            int n = pts.Count;
            var res = new List<P2>();
            for (int k = 0; k < n; k++)
            {
                int i0 = (k - 1 + n) % n, i1 = k;
                if (!Linha(pts[i0], pts[(i0 + 1) % n], off[i0], sign, out P2 a0, out P2 d0) ||
                    !Linha(pts[i1], pts[(i1 + 1) % n], off[i1], sign, out P2 a1, out P2 d1)) { res.Add(pts[k]); continue; }
                double den = d0.X * d1.Y - d0.Y * d1.X;
                if (Math.Abs(den) < 1e-9) { res.Add(new P2(a1.X, a1.Y)); continue; }   // collinear: start of the next edge
                double t = ((a1.X - a0.X) * d1.Y - (a1.Y - a0.Y) * d1.X) / den;
                res.Add(new P2(a0.X + d0.X * t, a0.Y + d0.Y * t));
            }
            return res;
        }

        private static bool Linha(P2 a, P2 b, double o, double sign, out P2 start, out P2 dir)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y, l = Math.Sqrt(dx * dx + dy * dy);
            start = a; dir = new P2(0, 0);
            if (l < 1e-9) return false;
            double nx = -dy / l * sign, ny = dx / l * sign;
            start = new P2(a.X + nx * o, a.Y + ny * o);
            dir = new P2(dx / l, dy / l);
            return true;
        }
    }
}
