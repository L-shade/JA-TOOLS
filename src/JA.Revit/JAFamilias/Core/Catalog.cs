using System;
using System.Collections.Generic;
using System.Linq;

namespace JAEsquadrias.Core
{
    /// <summary>Profile system (linha de perfis): visible widths, depths and thermal value.</summary>
    public sealed class PerfilInfo
    {
        public string Id, Nome, Material;
        public double MarcoFace, MarcoProf, FolhaFace, FolhaProf, Travessa;
        /// <summary>Frame thermal transmittance Uf, W/m²K (typical values, EN ISO 10077-2 tables).</summary>
        public double Uf;
        public override string ToString() => Nome;
    }

    /// <summary>Glazing: thickness, thermal value and how it is drawn.</summary>
    public sealed class VidroInfo
    {
        public string Id, Nome;
        public double Espessura;
        /// <summary>Centre-of-glass Ug, W/m²K.</summary>
        public double Ug;
        /// <summary>Linear thermal transmittance of the edge (spacer), W/mK. 0 for single glass.</summary>
        public double Psi;
        public string CorHex;
        /// <summary>Revit material transparency, 0-100.</summary>
        public int Transparencia;
        public override string ToString() => Nome;
    }

    public sealed class CorInfo
    {
        public string Id, Nome, Hex;
        public override string ToString() => Nome;
    }

    /// <summary>A ready-made model in the catalogue.</summary>
    public sealed class Modelo
    {
        public string Id, Nome, Descricao;
        public TipoEsquadria Tipo;
        public double Largura, Altura, Peitoril;
        public Func<List<Linha>> Linhas;

        public EsquadriaConfig ApplyTo(EsquadriaConfig cfg, bool keepSize)
        {
            var c = cfg.Clone();
            bool sameKind = c.Tipo == Tipo;
            c.Tipo = Tipo;
            c.ModeloId = Id;
            c.Linhas = Linhas();
            if (!keepSize || !sameKind)
            {
                c.LarguraMm = Largura;
                c.AlturaMm = Altura;
                c.PeitorilMm = Peitoril;
            }
            if (!sameKind && cfg.Codigo != null && cfg.Codigo.Length > 0)
            {
                // J01 <-> P01 when switching between window and door
                char want = Tipo == TipoEsquadria.Porta ? 'P' : 'J';
                if (char.ToUpperInvariant(cfg.Codigo[0]) == (Tipo == TipoEsquadria.Porta ? 'J' : 'P'))
                    c.Codigo = want + cfg.Codigo.Substring(1);
            }
            return c.Normalized();
        }
    }

    public static class Catalog
    {
        // ------------------------------------------------------------------ profiles
        public static readonly IReadOnlyList<PerfilInfo> Perfis = new List<PerfilInfo>
        {
            new PerfilInfo { Id = "AL25", Nome = "Alumínio – linha leve (25)", Material = "Alumínio", MarcoFace = 30, MarcoProf = 45, FolhaFace = 30, FolhaProf = 22, Travessa = 30, Uf = 7.0 },
            new PerfilInfo { Id = "AL30", Nome = "Alumínio – linha média (30)", Material = "Alumínio", MarcoFace = 40, MarcoProf = 60, FolhaFace = 40, FolhaProf = 30, Travessa = 40, Uf = 6.5 },
            new PerfilInfo { Id = "AL42", Nome = "Alumínio – linha pesada (42)", Material = "Alumínio", MarcoFace = 50, MarcoProf = 80, FolhaFace = 50, FolhaProf = 42, Travessa = 50, Uf = 6.0 },
            new PerfilInfo { Id = "ALRPT", Nome = "Alumínio com ruptura térmica", Material = "Alumínio RPT", MarcoFace = 55, MarcoProf = 75, FolhaFace = 60, FolhaProf = 65, Travessa = 60, Uf = 3.2 },
            new PerfilInfo { Id = "PVC70", Nome = "PVC – 70 mm", Material = "PVC", MarcoFace = 60, MarcoProf = 70, FolhaFace = 70, FolhaProf = 70, Travessa = 70, Uf = 1.3 },
            new PerfilInfo { Id = "MAD68", Nome = "Madeira – 68 mm", Material = "Madeira", MarcoFace = 60, MarcoProf = 68, FolhaFace = 70, FolhaProf = 68, Travessa = 70, Uf = 1.5 },
            new PerfilInfo { Id = "ACO", Nome = "Aço – perfil leve", Material = "Aço", MarcoFace = 30, MarcoProf = 40, FolhaFace = 30, FolhaProf = 25, Travessa = 30, Uf = 5.9 },
        };

        // ------------------------------------------------------------------ glazing
        public static readonly IReadOnlyList<VidroInfo> Vidros = new List<VidroInfo>
        {
            new VidroInfo { Id = "INC4", Nome = "Incolor 4 mm", Espessura = 4, Ug = 5.8, Psi = 0, CorHex = "CFE6EE", Transparencia = 75 },
            new VidroInfo { Id = "INC6", Nome = "Incolor 6 mm", Espessura = 6, Ug = 5.7, Psi = 0, CorHex = "CFE6EE", Transparencia = 70 },
            new VidroInfo { Id = "TEMP8", Nome = "Temperado incolor 8 mm", Espessura = 8, Ug = 5.6, Psi = 0, CorHex = "C8E2EA", Transparencia = 70 },
            new VidroInfo { Id = "LAM8", Nome = "Laminado incolor 8 mm (4+4)", Espessura = 8, Ug = 5.6, Psi = 0, CorHex = "C8E2EA", Transparencia = 68 },
            new VidroInfo { Id = "VERDE6", Nome = "Verde 6 mm", Espessura = 6, Ug = 5.7, Psi = 0, CorHex = "9CC9A8", Transparencia = 60 },
            new VidroInfo { Id = "FUME6", Nome = "Fumê 6 mm", Espessura = 6, Ug = 5.7, Psi = 0, CorHex = "8A9199", Transparencia = 50 },
            new VidroInfo { Id = "REF6", Nome = "Refletivo prata 6 mm", Espessura = 6, Ug = 5.4, Psi = 0, CorHex = "AEBBC7", Transparencia = 30 },
            new VidroInfo { Id = "BOREAL4", Nome = "Mini-boreal 4 mm (translúcido)", Espessura = 4, Ug = 5.8, Psi = 0, CorHex = "E3EEF2", Transparencia = 35 },
            new VidroInfo { Id = "INS24", Nome = "Insulado 6+12+6 (ar)", Espessura = 24, Ug = 2.8, Psi = 0.08, CorHex = "C3DDE6", Transparencia = 65 },
            new VidroInfo { Id = "INS24LE", Nome = "Insulado 6+12+6 low-e (argônio)", Espessura = 24, Ug = 1.4, Psi = 0.08, CorHex = "BCD7DF", Transparencia = 62 },
        };

        // ------------------------------------------------------------------ colours
        public static readonly IReadOnlyList<CorInfo> Cores = new List<CorInfo>
        {
            new CorInfo { Id = "branco", Nome = "Branco", Hex = "F2F2EF" },
            new CorInfo { Id = "preto", Nome = "Preto", Hex = "1F1F1F" },
            new CorInfo { Id = "grafite", Nome = "Grafite", Hex = "3F4448" },
            new CorInfo { Id = "bronze", Nome = "Bronze", Hex = "5A4632" },
            new CorInfo { Id = "natural", Nome = "Anodizado natural", Hex = "B9BCBE" },
            new CorInfo { Id = "champagne", Nome = "Champagne", Hex = "C9B38A" },
            new CorInfo { Id = "amadeirado", Nome = "Amadeirado", Hex = "8B5A2B" },
            new CorInfo { Id = "verde", Nome = "Verde inglês", Hex = "2F4F3E" },
        };

        public static PerfilInfo Perfil(string id) => Perfis.FirstOrDefault(p => p.Id == id) ?? Perfis[1];
        public static VidroInfo Vidro(string id) => Vidros.FirstOrDefault(v => v.Id == id) ?? Vidros[1];
        public static CorInfo Cor(string id) => Cores.FirstOrDefault(c => c.Id == id);

        /// <summary>Copies the profile system's dimensions into the configuration.</summary>
        public static void ApplyPerfil(EsquadriaConfig c, PerfilInfo p)
        {
            c.PerfilId = p.Id;
            c.MarcoFaceMm = p.MarcoFace;
            c.MarcoProfMm = p.MarcoProf;
            c.FolhaFaceMm = p.FolhaFace;
            c.FolhaProfMm = p.FolhaProf;
            c.TravessaMm = p.Travessa;
        }

        // ------------------------------------------------------------------ names
        public static string NomeAbertura(Abertura a)
        {
            switch (a)
            {
                case Abertura.Fixo: return "Fixo";
                case Abertura.AbrirEsquerda: return "Abrir – dobradiça à esquerda";
                case Abertura.AbrirDireita: return "Abrir – dobradiça à direita";
                case Abertura.AbrirDupla: return "Abrir – 2 folhas";
                case Abertura.MaximAr: return "Maxim-ar";
                case Abertura.Basculante: return "Basculante";
                case Abertura.Tombar: return "Tombar (projetante p/ dentro)";
                case Abertura.OsciloBatenteEsquerda: return "Oscilo-batente – dobradiça à esquerda";
                case Abertura.OsciloBatenteDireita: return "Oscilo-batente – dobradiça à direita";
                case Abertura.Pivotante: return "Pivotante vertical";
                case Abertura.Correr: return "Correr";
                default: return a.ToString();
            }
        }

        public static string NomeCurto(Abertura a)
        {
            switch (a)
            {
                case Abertura.AbrirEsquerda:
                case Abertura.AbrirDireita: return "abrir";
                case Abertura.AbrirDupla: return "abrir 2 folhas";
                case Abertura.OsciloBatenteEsquerda:
                case Abertura.OsciloBatenteDireita: return "oscilo-batente";
                default: return NomeAbertura(a).ToLowerInvariant();
            }
        }

        public static readonly Abertura[] Aberturas = (Abertura[])Enum.GetValues(typeof(Abertura));

        public static string NomePosicao(PosicaoParede p) =>
            p == PosicaoParede.Externa ? "Face externa" : p == PosicaoParede.Interna ? "Face interna" : "Centro da parede";

        // ------------------------------------------------------------------ presets
        private static Celula C(Abertura a, double w = 0, Preenchimento p = Preenchimento.Vidro, int folhas = 2, bool misto = false,
                                ModoCorrer modo = ModoCorrer.Padrao, bool direita = false) =>
            new Celula { Abertura = a, LarguraMm = w, Preenchimento = p, Folhas = folhas, MistoVeneziana = misto, Modo = modo, RecolheDireita = direita };

        private static List<Linha> L(params Linha[] rows) => rows.ToList();
        private static Linha R(double h, params Celula[] cells) => Linha.Of(h, cells);

        public static readonly IReadOnlyList<Modelo> Modelos = new List<Modelo>
        {
            // ---------------------------------------------------------------- janelas
            new Modelo { Id = "J-CORRER2", Tipo = TipoEsquadria.Janela, Nome = "Correr 2 folhas", Descricao = "A mais comum: duas folhas de vidro de correr.", Largura = 1500, Altura = 1200, Peitoril = 1000,
                         Linhas = () => L(R(0, C(Abertura.Correr, folhas: 2))) },
            new Modelo { Id = "J-CORRER4", Tipo = TipoEsquadria.Janela, Nome = "Correr 4 folhas", Descricao = "Duas folhas centrais de correr e duas fixas.", Largura = 2400, Altura = 1200, Peitoril = 1000,
                         Linhas = () => L(R(0, C(Abertura.Correr, folhas: 4))) },
            new Modelo { Id = "J-CORRER4-CENTRAL", Tipo = TipoEsquadria.Janela, Nome = "Correr 4 folhas, abertura central", Descricao = "As duas folhas do meio abrem para os lados, por cima das fixas das pontas.", Largura = 2400, Altura = 1200, Peitoril = 1000,
                         Linhas = () => L(R(0, C(Abertura.Correr, folhas: 4, modo: ModoCorrer.Central))) },
            new Modelo { Id = "J-CORRER3V", Tipo = TipoEsquadria.Janela, Nome = "Correr 3 folhas c/ veneziana", Descricao = "Dormitório: duas venezianas e um vidro, três trilhos.", Largura = 1500, Altura = 1200, Peitoril = 1000,
                         Linhas = () => L(R(0, C(Abertura.Correr, folhas: 3, misto: true))) },
            new Modelo { Id = "J-CORRER-BAND", Tipo = TipoEsquadria.Janela, Nome = "Correr c/ bandeira", Descricao = "Correr 2 folhas com bandeira basculante em cima.", Largura = 1500, Altura = 1500, Peitoril = 800,
                         Linhas = () => L(R(400, C(Abertura.Basculante)), R(0, C(Abertura.Correr, folhas: 2))) },
            new Modelo { Id = "J-MAXIMAR", Tipo = TipoEsquadria.Janela, Nome = "Maxim-ar", Descricao = "Folha projetante, articulada em cima.", Largura = 600, Altura = 600, Peitoril = 1500,
                         Linhas = () => L(R(0, C(Abertura.MaximAr))) },
            new Modelo { Id = "J-MAXIMAR2", Tipo = TipoEsquadria.Janela, Nome = "Maxim-ar duplo", Descricao = "Dois maxim-ares lado a lado.", Largura = 1200, Altura = 600, Peitoril = 1500,
                         Linhas = () => L(R(0, C(Abertura.MaximAr), C(Abertura.MaximAr))) },
            new Modelo { Id = "J-MAXIMAR-FIXO", Tipo = TipoEsquadria.Janela, Nome = "Fixo + maxim-ar", Descricao = "Vidro fixo em cima e maxim-ar embaixo.", Largura = 1000, Altura = 1400, Peitoril = 900,
                         Linhas = () => L(R(0, C(Abertura.Fixo)), R(500, C(Abertura.MaximAr))) },
            new Modelo { Id = "J-BASC", Tipo = TipoEsquadria.Janela, Nome = "Basculante", Descricao = "Banheiros e áreas de serviço.", Largura = 600, Altura = 600, Peitoril = 1500,
                         Linhas = () => L(R(0, C(Abertura.Basculante))) },
            new Modelo { Id = "J-ABRIR1", Tipo = TipoEsquadria.Janela, Nome = "Abrir 1 folha", Descricao = "Uma folha de abrir.", Largura = 700, Altura = 1200, Peitoril = 1000,
                         Linhas = () => L(R(0, C(Abertura.AbrirEsquerda))) },
            new Modelo { Id = "J-ABRIR2", Tipo = TipoEsquadria.Janela, Nome = "Abrir 2 folhas", Descricao = "Duas folhas de abrir sem montante.", Largura = 1200, Altura = 1200, Peitoril = 1000,
                         Linhas = () => L(R(0, C(Abertura.AbrirDupla))) },
            new Modelo { Id = "J-OSCILO", Tipo = TipoEsquadria.Janela, Nome = "Oscilo-batente", Descricao = "Abre como porta ou tomba para ventilar.", Largura = 900, Altura = 1300, Peitoril = 900,
                         Linhas = () => L(R(0, C(Abertura.OsciloBatenteEsquerda))) },
            new Modelo { Id = "J-TRIPLA", Tipo = TipoEsquadria.Janela, Nome = "Fixo · abrir · fixo", Descricao = "Três módulos: folha de abrir entre dois fixos.", Largura = 2100, Altura = 1300, Peitoril = 900,
                         Linhas = () => L(R(0, C(Abertura.Fixo), C(Abertura.AbrirEsquerda, 700), C(Abertura.Fixo))) },
            new Modelo { Id = "J-PIVOT", Tipo = TipoEsquadria.Janela, Nome = "Pivotante", Descricao = "Folha que gira em eixo vertical.", Largura = 900, Altura = 1200, Peitoril = 1000,
                         Linhas = () => L(R(0, C(Abertura.Pivotante))) },
            new Modelo { Id = "J-FIXA", Tipo = TipoEsquadria.Janela, Nome = "Fixa", Descricao = "Visor fixo, sem abertura.", Largura = 1000, Altura = 1000, Peitoril = 1000,
                         Linhas = () => L(R(0, C(Abertura.Fixo))) },
            new Modelo { Id = "J-VENEZ", Tipo = TipoEsquadria.Janela, Nome = "Veneziana fixa", Descricao = "Ventilação permanente (casa de máquinas, depósitos).", Largura = 600, Altura = 400, Peitoril = 1800,
                         Linhas = () => L(R(0, C(Abertura.Fixo, p: Preenchimento.Veneziana))) },

            // ---------------------------------------------------------------- portas
            new Modelo { Id = "P-ABRIR1", Tipo = TipoEsquadria.Porta, Nome = "Abrir 1 folha (cega)", Descricao = "Porta interna de abrir, folha lisa.", Largura = 800, Altura = 2100, Peitoril = 0,
                         Linhas = () => L(R(0, C(Abertura.AbrirEsquerda, p: Preenchimento.Cego))) },
            new Modelo { Id = "P-ABRIR1V", Tipo = TipoEsquadria.Porta, Nome = "Abrir 1 folha c/ vidro", Descricao = "Porta de abrir envidraçada.", Largura = 900, Altura = 2100, Peitoril = 0,
                         Linhas = () => L(R(0, C(Abertura.AbrirEsquerda))) },
            new Modelo { Id = "P-ABRIR2", Tipo = TipoEsquadria.Porta, Nome = "Abrir 2 folhas", Descricao = "Porta de duas folhas de vidro.", Largura = 1400, Altura = 2200, Peitoril = 0,
                         Linhas = () => L(R(0, C(Abertura.AbrirDupla))) },
            new Modelo { Id = "P-ABRIR-BAND", Tipo = TipoEsquadria.Porta, Nome = "Abrir c/ bandeira", Descricao = "Folha de abrir com bandeira fixa de vidro.", Largura = 900, Altura = 2500, Peitoril = 0,
                         Linhas = () => L(R(400, C(Abertura.Fixo)), R(0, C(Abertura.AbrirEsquerda, p: Preenchimento.Cego))) },
            new Modelo { Id = "P-CORRER2", Tipo = TipoEsquadria.Porta, Nome = "Correr 2 folhas", Descricao = "Porta-balcão de correr, duas folhas de vidro.", Largura = 1600, Altura = 2200, Peitoril = 0,
                         Linhas = () => L(R(0, C(Abertura.Correr, folhas: 2))) },
            new Modelo { Id = "P-CORRER4", Tipo = TipoEsquadria.Porta, Nome = "Correr 4 folhas", Descricao = "Porta-balcão larga, folhas centrais de correr.", Largura = 3000, Altura = 2200, Peitoril = 0,
                         Linhas = () => L(R(0, C(Abertura.Correr, folhas: 4))) },
            new Modelo { Id = "P-CORRER4-CENTRAL", Tipo = TipoEsquadria.Porta, Nome = "Correr 4 folhas, abertura central", Descricao = "Folhas do meio correm para os dois lados sobre as fixas das pontas.", Largura = 3000, Altura = 2200, Peitoril = 0,
                         Linhas = () => L(R(0, C(Abertura.Correr, folhas: 4, modo: ModoCorrer.Central))) },
            new Modelo { Id = "P-CORRER6-CENTRAL", Tipo = TipoEsquadria.Porta, Nome = "Correr 6 folhas, abertura central", Descricao = "Vão grande: duas folhas recolhem de cada lado sobre as fixas das pontas (3 trilhos).", Largura = 4500, Altura = 2400, Peitoril = 0,
                         Linhas = () => L(R(0, C(Abertura.Correr, folhas: 6, modo: ModoCorrer.Central))) },
            new Modelo { Id = "P-CORRER3-RECOLHE", Tipo = TipoEsquadria.Porta, Nome = "Correr 3 folhas recolhendo p/ um lado", Descricao = "Uma folha fixa à esquerda; as outras duas recolhem sobre ela (3 trilhos).", Largura = 2700, Altura = 2200, Peitoril = 0,
                         Linhas = () => L(R(0, C(Abertura.Correr, folhas: 3, modo: ModoCorrer.Recolhe))) },
            new Modelo { Id = "P-CORRER4-RECOLHE", Tipo = TipoEsquadria.Porta, Nome = "Correr 4 folhas recolhendo p/ um lado", Descricao = "Telescópica: uma fixa e três móveis recolhendo sobre ela (4 trilhos).", Largura = 3600, Altura = 2400, Peitoril = 0,
                         Linhas = () => L(R(0, C(Abertura.Correr, folhas: 4, modo: ModoCorrer.Recolhe))) },
            new Modelo { Id = "P-CORRER3V", Tipo = TipoEsquadria.Porta, Nome = "Correr 3 folhas c/ veneziana", Descricao = "Porta-balcão de dormitório: venezianas e vidro.", Largura = 1800, Altura = 2200, Peitoril = 0,
                         Linhas = () => L(R(0, C(Abertura.Correr, folhas: 3, misto: true))) },
            new Modelo { Id = "P-CORRER-BAND", Tipo = TipoEsquadria.Porta, Nome = "Correr c/ bandeira", Descricao = "Porta de correr com bandeira fixa.", Largura = 1600, Altura = 2600, Peitoril = 0,
                         Linhas = () => L(R(400, C(Abertura.Fixo)), R(0, C(Abertura.Correr, folhas: 2))) },
            new Modelo { Id = "P-PIVOT", Tipo = TipoEsquadria.Porta, Nome = "Pivotante", Descricao = "Porta de entrada pivotante, folha cega.", Largura = 1200, Altura = 2400, Peitoril = 0,
                         Linhas = () => L(R(0, C(Abertura.Pivotante, p: Preenchimento.Cego))) },
            new Modelo { Id = "P-VENEZ", Tipo = TipoEsquadria.Porta, Nome = "Abrir c/ veneziana", Descricao = "Área de serviço: folha de veneziana.", Largura = 800, Altura = 2100, Peitoril = 0,
                         Linhas = () => L(R(0, C(Abertura.AbrirEsquerda, p: Preenchimento.Veneziana))) },
        };

        /// <summary>A starting configuration (first model of the kind, user defaults applied).</summary>
        public static EsquadriaConfig Default(TipoEsquadria tipo, Settings s)
        {
            var cfg = new EsquadriaConfig { Tipo = tipo };
            var perfil = Perfil(s?.PerfilPadrao);
            ApplyPerfil(cfg, perfil);
            var cor = Cor(s?.CorPadrao) ?? Cores[0];
            cfg.CorId = cor.Id;
            cfg.CorHex = cor.Hex;
            cfg.VidroId = Vidro(s?.VidroPadrao).Id;
            cfg.ParedeMm = s != null && s.ParedePadraoMm > 0 ? s.ParedePadraoMm : 150;
            cfg.Posicao = s?.PosicaoPadrao ?? PosicaoParede.Centro;
            var m = Modelos.First(x => x.Tipo == tipo);
            cfg = m.ApplyTo(cfg, false);
            cfg.Codigo = tipo == TipoEsquadria.Porta ? "P01" : "J01";
            return cfg;
        }
    }
}
