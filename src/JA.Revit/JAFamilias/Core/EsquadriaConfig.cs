using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using JAEsquadrias.Infrastructure;

namespace JAEsquadrias.Core
{
    /// <summary>Janela (window family) or Porta (door family).</summary>
    public enum TipoEsquadria { Janela = 0, Porta = 1 }

    /// <summary>How a module (cell) of the esquadria opens.</summary>
    public enum Abertura
    {
        Fixo = 0,
        AbrirEsquerda = 1,
        AbrirDireita = 2,
        AbrirDupla = 3,
        MaximAr = 4,
        Basculante = 5,
        Tombar = 6,
        OsciloBatenteEsquerda = 7,
        OsciloBatenteDireita = 8,
        Pivotante = 9,
        Correr = 10
    }

    /// <summary>What fills a sash or a fixed module.</summary>
    public enum Preenchimento { Vidro = 0, Veneziana = 1, Cego = 2 }

    /// <summary>Where the frame sits across the wall thickness.</summary>
    public enum PosicaoParede { Externa = 0, Centro = 1, Interna = 2 }

    /// <summary>Stone piece on the floor of a door: soleira (full threshold) or filete (strip).</summary>
    public enum TipoSoleira { Soleira = 0, Filete = 1 }

    /// <summary>How the swing is drawn in plan.</summary>
    public enum ArcoPlanta { Arco = 0, ArcoTracejado = 1, LinhaReta = 2, Nenhum = 3 }

    /// <summary>How the open door leaf is drawn in plan.</summary>
    public enum FolhaPlanta { Retangulo = 0, Linha = 1 }

    /// <summary>Where the open leaf starts in plan: on the frame face (structure) or on the hinge axis.</summary>
    public enum AlinhamentoFolha { FaceDoMarco = 0, EixoDaDobradica = 1 }

    /// <summary>Sliding symbol in the elevation.</summary>
    public enum SimboloCorrer { Triangulos = 0, Setas = 1 }

    /// <summary>
    /// How the leaves of a sliding module move: all of them (classic), from the centre to both
    /// sides (abertura central: the end leaves stay fixed when there are 4 or 6), or all stacking
    /// onto one fixed leaf at one side (recolhível / telescópica, one track per leaf).
    /// </summary>
    public enum ModoCorrer { Padrao = 0, Central = 1, Recolhe = 2 }

    /// <summary>Where the soleira/filete sits across the wall.</summary>
    public enum AlinhamentoSoleira { Centro = 0, FaceExterna = 1, FaceInterna = 2 }

    /// <summary>One module of a row. Widths are measured on the outside dimension (module).</summary>
    [DataContract]
    public sealed class Celula
    {
        /// <summary>Module width in mm; 0 = automatic (shares what is left in the row).</summary>
        [DataMember(Order = 1)] public double LarguraMm { get; set; }
        [DataMember(Order = 2)] public Abertura Abertura { get; set; }
        [DataMember(Order = 3)] public Preenchimento Preenchimento { get; set; }
        /// <summary>Number of leaves of a sliding module (2, 3, 4 or 6).</summary>
        [DataMember(Order = 4)] public int Folhas { get; set; } = 2;
        /// <summary>Sliding: leaves on the outer tracks are louvres (janela com veneziana).</summary>
        [DataMember(Order = 5)] public bool MistoVeneziana { get; set; }
        /// <summary>Hinged leaves open to the outside (default: to the inside).</summary>
        [DataMember(Order = 6)] public bool AbreParaFora { get; set; }
        /// <summary>Sliding: how the leaves move (JSON without it = classic).</summary>
        [DataMember(Order = 7)] public ModoCorrer Modo { get; set; }
        /// <summary>Sliding "recolhe": the fixed leaf and the stack are on the right (seen from outside); default left.</summary>
        [DataMember(Order = 8)] public bool RecolheDireita { get; set; }

        public Celula Clone() => (Celula)MemberwiseClone();

        public static Celula Of(Abertura a, double largura = 0, Preenchimento p = Preenchimento.Vidro, int folhas = 2) =>
            new Celula { Abertura = a, LarguraMm = largura, Preenchimento = p, Folhas = folhas };
    }

    /// <summary>One horizontal band of the esquadria (rows are listed from top to bottom).</summary>
    [DataContract]
    public sealed class Linha
    {
        /// <summary>Module height in mm; 0 = automatic.</summary>
        [DataMember(Order = 1)] public double AlturaMm { get; set; }
        [DataMember(Order = 2)] public List<Celula> Celulas { get; set; } = new List<Celula>();

        public Linha Clone() => new Linha { AlturaMm = AlturaMm, Celulas = Celulas.Select(c => c.Clone()).ToList() };

        public static Linha Of(double altura, params Celula[] cells) => new Linha { AlturaMm = altura, Celulas = cells.ToList() };
    }

    /// <summary>
    /// Everything that defines one JA esquadria. Stored as JSON inside the Revit family (hidden
    /// shared parameter JA_Config), in the library (*.json) and in the user preferences, so an
    /// esquadria can always be opened again in the configurator and regenerated.
    /// All lengths in millimetres; the elevation is seen from the EXTERIOR (left/right as seen
    /// from outside).
    /// </summary>
    [DataContract]
    public sealed class EsquadriaConfig
    {
        public const int CurrentVersion = 1;

        [DataMember(Order = 1)] public int Versao { get; set; } = CurrentVersion;
        [DataMember(Order = 2)] public TipoEsquadria Tipo { get; set; }
        /// <summary>Type mark and family identity, e.g. "J01", "P03".</summary>
        [DataMember(Order = 3)] public string Codigo { get; set; } = "J01";
        /// <summary>Free description ("Janela da sala"); the technical description is generated.</summary>
        [DataMember(Order = 4)] public string Nome { get; set; } = "";
        /// <summary>Preset the layout came from (for the catalogue highlight only).</summary>
        [DataMember(Order = 5)] public string ModeloId { get; set; } = "";

        [DataMember(Order = 10)] public double LarguraMm { get; set; } = 1200;
        [DataMember(Order = 11)] public double AlturaMm { get; set; } = 1200;
        [DataMember(Order = 12)] public double PeitorilMm { get; set; } = 1000;

        [DataMember(Order = 20)] public List<Linha> Linhas { get; set; } = new List<Linha>();

        // ---------------------------------------------------------------- profile system
        [DataMember(Order = 30)] public string PerfilId { get; set; } = "AL30";
        [DataMember(Order = 31)] public double MarcoFaceMm { get; set; } = 40;
        [DataMember(Order = 32)] public double MarcoProfMm { get; set; } = 60;
        [DataMember(Order = 33)] public double FolhaFaceMm { get; set; } = 40;
        [DataMember(Order = 34)] public double FolhaProfMm { get; set; } = 30;
        [DataMember(Order = 35)] public double TravessaMm { get; set; } = 40;
        [DataMember(Order = 36)] public string CorId { get; set; } = "branco";
        [DataMember(Order = 37)] public string CorHex { get; set; } = "F2F2EF";

        // ---------------------------------------------------------------- glazing
        [DataMember(Order = 40)] public string VidroId { get; set; } = "INC6";

        // ---------------------------------------------------------------- position in the wall
        [DataMember(Order = 50)] public PosicaoParede Posicao { get; set; } = PosicaoParede.Centro;
        /// <summary>Externa/Interna: setback from that wall face. Centro: shift towards the exterior.</summary>
        [DataMember(Order = 51)] public double RecuoMm { get; set; } = 0;
        /// <summary>Wall thickness the Externa/Interna position is computed for.</summary>
        [DataMember(Order = 52)] public double ParedeMm { get; set; } = 150;
        /// <summary>Finish (revestimento) beyond the Revit wall on the exterior face, mm: the frame,
        /// casings and stones go up to the finished face.</summary>
        [DataMember(Order = 53)] public double RevestimentoExtMm { get; set; }
        /// <summary>Finish beyond the Revit wall on the interior face, mm.</summary>
        [DataMember(Order = 54)] public double RevestimentoIntMm { get; set; }

        // ---------------------------------------------------------------- details
        [DataMember(Order = 60)] public bool Peitoril { get; set; } = true;
        [DataMember(Order = 61)] public double PeitorilProjecaoMm { get; set; } = 30;
        [DataMember(Order = 62)] public double PeitorilEspessuraMm { get; set; } = 20;
        [DataMember(Order = 63)] public bool Soleira { get; set; } = true;
        [DataMember(Order = 64)] public bool Puxadores { get; set; } = true;
        [DataMember(Order = 65)] public bool SimbolosElevacao { get; set; } = true;
        [DataMember(Order = 66)] public bool SimbolosPlanta { get; set; } = true;
        /// <summary>Coarse detail level shows only the frame and the glass.</summary>
        [DataMember(Order = 67)] public bool SimplificarBaixo { get; set; } = true;

        // ---------------------------------------------------------------- soleira / filete (doors)
        [DataMember(Order = 70)] public TipoSoleira SoleiraTipo { get; set; } = TipoSoleira.Soleira;
        /// <summary>Code of the stone piece (tags / schedules), e.g. "SL01".</summary>
        [DataMember(Order = 71)] public string SoleiraCodigo { get; set; } = "SL01";
        /// <summary>Material name; a project material with this name is used when the family is loaded.</summary>
        [DataMember(Order = 72)] public string SoleiraMaterial { get; set; } = "Granito cinza";
        [DataMember(Order = 73)] public string SoleiraCorHex { get; set; } = "B9B6B0";
        /// <summary>Length along the wall; 0 = width of the opening.</summary>
        [DataMember(Order = 74)] public double SoleiraComprimentoMm { get; set; }
        /// <summary>Width across the wall; 0 = wall thickness.</summary>
        [DataMember(Order = 75)] public double SoleiraLarguraMm { get; set; }
        [DataMember(Order = 76)] public double SoleiraEspessuraMm { get; set; } = 20;
        /// <summary>Soleira/filete centred in the wall or flush with the esquadria's exterior/interior side.</summary>
        [DataMember(Order = 77)] public AlinhamentoSoleira SoleiraAlinhamento { get; set; } = AlinhamentoSoleira.Centro;

        // ---------------------------------------------------------------- peitoril (windows): code + material
        [DataMember(Order = 80)] public string PeitorilCodigo { get; set; } = "PE01";
        [DataMember(Order = 81)] public string PeitorilMaterial { get; set; } = "Granito cinza";
        [DataMember(Order = 82)] public string PeitorilCorHex { get; set; } = "B9B6B0";
        /// <summary>How far the stone enters each jamb (abas laterais), mm.</summary>
        [DataMember(Order = 83)] public double PeitorilAbaMm { get; set; } = 30;
        /// <summary>Total width of the stone across the wall (with the projection); 0 = up to the interior face.</summary>
        [DataMember(Order = 84)] public double PeitorilLarguraMm { get; set; }

        // ---------------------------------------------------------------- door frame in plan
        /// <summary>Doors: the frame (marco/batente) takes the whole wall thickness.</summary>
        [DataMember(Order = 90)] public bool MarcoParede { get; set; } = true;
        /// <summary>Doors: width of the stop (batente) the leaf closes against, mm.</summary>
        [DataMember(Order = 91)] public double BatenteMm { get; set; } = 12;
        /// <summary>Doors: casings (guarnições/alizares) on both wall faces.</summary>
        [DataMember(Order = 92)] public bool Guarnicoes { get; set; } = true;
        [DataMember(Order = 93)] public double GuarnicaoLarguraMm { get; set; } = 60;
        [DataMember(Order = 94)] public double GuarnicaoEspessuraMm { get; set; } = 12;

        // ---------------------------------------------------------------- plan 2D (aba Planta 2D)
        /// <summary>Opening angle of the door leaf in plan, degrees (15-180).</summary>
        [DataMember(Order = 100)] public double PlantaAngulo { get; set; } = 90;
        [DataMember(Order = 101)] public ArcoPlanta PlantaArco { get; set; } = ArcoPlanta.ArcoTracejado;
        [DataMember(Order = 102)] public FolhaPlanta PlantaFolha { get; set; } = FolhaPlanta.Retangulo;
        [DataMember(Order = 103)] public AlinhamentoFolha PlantaAlinhamento { get; set; } = AlinhamentoFolha.FaceDoMarco;
        /// <summary>Default visibility of each 2D group (each one is also a Yes/No instance parameter in Revit).</summary>
        [DataMember(Order = 104)] public bool PlantaFolhaFechada { get; set; }
        [DataMember(Order = 105)] public bool PlantaVerga { get; set; }
        [DataMember(Order = 106)] public bool PlantaMarco { get; set; } = true;
        [DataMember(Order = 107)] public bool PlantaGuarnicoes { get; set; } = true;
        [DataMember(Order = 108)] public bool PlantaPedra { get; set; } = true;
        [DataMember(Order = 109)] public bool PlantaFolhas { get; set; } = true;
        /// <summary>Arrows beside the sliding leaves in plan; null = automatic (doors yes, windows no).</summary>
        [DataMember(Order = 111)] public bool? PlantaSetasCorrer { get; set; }

        // ---------------------------------------------------------------- elevation (padrão do escritório)
        /// <summary>Dashed opening lines when the leaf opens inwards (off = all solid, office standard).</summary>
        [DataMember(Order = 120)] public bool TracejarParaDentro { get; set; }
        [DataMember(Order = 121)] public SimboloCorrer ElevacaoCorrer { get; set; } = SimboloCorrer.Triangulos;
        /// <summary>Letter F on fixed panes.</summary>
        [DataMember(Order = 122)] public bool MarcarFixo { get; set; } = true;
        /// <summary>Doors: gap under the leaf (fresta), mm.</summary>
        [DataMember(Order = 123)] public double FrestaMm { get; set; } = 8;
        /// <summary>Hardware specification typed by the user (goes to JA_Ferragens and the schedule).</summary>
        [DataMember(Order = 124)] public string Ferragens { get; set; }
        /// <summary>Drip groove (pingadeira) under the sill's projection.</summary>
        [DataMember(Order = 125)] public bool PeitorilPingadeira { get; set; }
        /// <summary>Sub-frame (contramarco) around the opening, outside W × H, set into the masonry.</summary>
        [DataMember(Order = 126)] public bool Contramarco { get; set; }
        [DataMember(Order = 127)] public double ContramarcoLarguraMm { get; set; } = 20;
        /// <summary>Depth of the contramarco (0 = the frame depth).</summary>
        [DataMember(Order = 128)] public double ContramarcoProfMm { get; set; }
        /// <summary>Door/window of a curtain wall (curtain panel family): no host wall, no sill, the
        /// size comes from the panel it replaces.</summary>
        [DataMember(Order = 130)] public bool ParedeCortina { get; set; }
        /// <summary>Set by Normalized on older JSON (fields above default to false there).</summary>
        [DataMember(Order = 110)] public int PlantaVersao { get; set; } = 2;

        /// <summary>Doors with the frame across the whole wall.</summary>
        public bool MarcoNaParede => IsPorta && MarcoParede && !ParedeCortina;

        public string NomeSoleira => SoleiraTipo == TipoSoleira.Filete ? "Filete" : "Soleira";

        public bool IsPorta => Tipo == TipoEsquadria.Porta;

        public string FamilyName(string prefix) => (string.IsNullOrWhiteSpace(prefix) ? "JA Esquadria" : prefix.Trim()) + " " + Codigo;

        public EsquadriaConfig Clone() => Json.Deserialize<EsquadriaConfig>(Json.Serialize(this)).Normalized();

        public string ToJson() => Json.Serialize(this);

        public static EsquadriaConfig FromJson(string json)
        {
            try { return Json.Deserialize<EsquadriaConfig>(json)?.Normalized(); }
            catch (Exception ex)
            {
                Logger.Warn("[JA Famílias] Configuração ilegível: " + ex.Message);
                return null;
            }
        }

        /// <summary>Fills missing parts (older JSON, hand-edited library files) with safe values.</summary>
        public EsquadriaConfig Normalized()
        {
            if (Linhas == null) Linhas = new List<Linha>();
            Linhas.RemoveAll(l => l == null);
            foreach (var l in Linhas)
            {
                if (l.Celulas == null) l.Celulas = new List<Celula>();
                l.Celulas.RemoveAll(c => c == null);
                if (l.Celulas.Count == 0) l.Celulas.Add(Celula.Of(Abertura.Fixo));
                foreach (var c in l.Celulas)
                {
                    if (c.Folhas < 2) c.Folhas = 2;
                    if (c.Folhas > 6) c.Folhas = 6;
                    if (c.Folhas == 5) c.Folhas = 4;
                    if (!Enum.IsDefined(typeof(ModoCorrer), c.Modo)) c.Modo = ModoCorrer.Padrao;
                    // opening to both sides needs an even number of leaves
                    if (c.Modo == ModoCorrer.Central && c.Folhas == 3) c.Folhas = 4;
                    if (c.LarguraMm < 0) c.LarguraMm = 0;
                }
                if (l.AlturaMm < 0) l.AlturaMm = 0;
            }
            if (Linhas.Count == 0) Linhas.Add(Linha.Of(0, Celula.Of(Abertura.Fixo)));
            if (string.IsNullOrWhiteSpace(Codigo)) Codigo = IsPorta ? "P01" : "J01";
            if (Nome == null) Nome = "";
            if (ModeloId == null) ModeloId = "";
            if (string.IsNullOrWhiteSpace(CorHex)) CorHex = "F2F2EF";
            if (string.IsNullOrWhiteSpace(PerfilId)) PerfilId = "AL30";
            if (string.IsNullOrWhiteSpace(VidroId)) VidroId = "INC6";
            LarguraMm = Clamp(LarguraMm, 200, 12000);
            AlturaMm = Clamp(AlturaMm, 200, 6000);
            PeitorilMm = Clamp(PeitorilMm, 0, 5000);
            MarcoFaceMm = Clamp(MarcoFaceMm, 10, 200);
            MarcoProfMm = Clamp(MarcoProfMm, 20, 300);
            FolhaFaceMm = Clamp(FolhaFaceMm, 10, 200);
            FolhaProfMm = Clamp(FolhaProfMm, 10, 200);
            TravessaMm = Clamp(TravessaMm, 10, 200);
            ParedeMm = Clamp(ParedeMm, 50, 1000);
            PeitorilProjecaoMm = Clamp(PeitorilProjecaoMm, 0, 200);
            PeitorilEspessuraMm = Clamp(PeitorilEspessuraMm, 5, 100);
            if (string.IsNullOrWhiteSpace(SoleiraCodigo)) SoleiraCodigo = "SL01";
            if (string.IsNullOrWhiteSpace(SoleiraMaterial)) SoleiraMaterial = "Granito cinza";
            if (string.IsNullOrWhiteSpace(SoleiraCorHex)) SoleiraCorHex = "B9B6B0";
            if (string.IsNullOrWhiteSpace(PeitorilCodigo)) PeitorilCodigo = "PE01";
            if (string.IsNullOrWhiteSpace(PeitorilMaterial)) PeitorilMaterial = "Granito cinza";
            if (string.IsNullOrWhiteSpace(PeitorilCorHex)) PeitorilCorHex = "B9B6B0";
            SoleiraComprimentoMm = SoleiraComprimentoMm <= 0 ? 0 : Clamp(SoleiraComprimentoMm, 50, 15000);
            SoleiraLarguraMm = SoleiraLarguraMm <= 0 ? 0 : Clamp(SoleiraLarguraMm, 10, 1500);
            SoleiraEspessuraMm = SoleiraEspessuraMm <= 0 ? 20 : Clamp(SoleiraEspessuraMm, 3, 100);
            BatenteMm = Clamp(BatenteMm, 0, 60);
            RevestimentoExtMm = Clamp(RevestimentoExtMm, 0, 200);
            if (PlantaVersao <= 0)
            {
                // configuration saved before the Planta 2D tab existed
                PlantaAngulo = 90; PlantaMarco = true; PlantaGuarnicoes = true; PlantaPedra = true; PlantaFolhas = true;
                PlantaVersao = 1;
            }
            PlantaAngulo = Clamp(PlantaAngulo, 15, 180);
            if (PlantaVersao < 2)
            {
                // before the office standard (relação de esquadrias): F on fixed panes, 8 mm gap
                MarcarFixo = true;
                if (FrestaMm <= 0) FrestaMm = 8;
                PlantaVersao = 2;
            }
            FrestaMm = Clamp(FrestaMm, 0, 100);
            // windows: frame and leaves come from the cut of the 3D in the plan (no switch for them)
            if (Tipo == TipoEsquadria.Janela) { PlantaMarco = true; PlantaFolhas = true; }
            RevestimentoIntMm = Clamp(RevestimentoIntMm, 0, 200);
            PeitorilAbaMm = Clamp(PeitorilAbaMm, 0, 200);
            PeitorilLarguraMm = PeitorilLarguraMm <= 0 ? 0 : Clamp(PeitorilLarguraMm, 10, 1500);
            GuarnicaoLarguraMm = GuarnicaoLarguraMm <= 0 ? 60 : Clamp(GuarnicaoLarguraMm, 10, 300);
            GuarnicaoEspessuraMm = GuarnicaoEspessuraMm <= 0 ? 12 : Clamp(GuarnicaoEspessuraMm, 3, 60);
            ContramarcoLarguraMm = ContramarcoLarguraMm <= 0 ? 20 : Clamp(ContramarcoLarguraMm, 5, 100);
            ContramarcoProfMm = ContramarcoProfMm <= 0 ? 0 : Clamp(ContramarcoProfMm, 10, 400);
            if (Versao <= 0) Versao = CurrentVersion;
            return this;
        }

        private static double Clamp(double v, double min, double max) =>
            double.IsNaN(v) ? min : Math.Max(min, Math.Min(max, v));
    }
}
