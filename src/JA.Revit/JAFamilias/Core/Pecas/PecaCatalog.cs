using System;
using System.Collections.Generic;
using System.Linq;

namespace JAEsquadrias.Core
{
    /// <summary>A ready-made piece in the catalogue.</summary>
    public sealed class ModeloPeca
    {
        public string Id, Nome, Descricao;
        public TipoPeca Tipo;
        /// <summary>Applies the model to a configuration (keeps code, name and materials).</summary>
        public Func<IPecaConfig, IPecaConfig> Aplicar;
    }

    public static class PecaCatalog
    {
        public static IPecaConfig Novo(TipoPeca t)
        {
            IPecaConfig c;
            switch (t)
            {
                case TipoPeca.Bancada: c = new BancadaConfig().Normalized(); break;
                case TipoPeca.Nicho: c = new NichoConfig().Normalized(); break;
                case TipoPeca.Ralo: c = new RaloConfig().Normalized(); break;
                case TipoPeca.Pedra: c = new PedraConfig().Normalized(); break;
                case TipoPeca.Vidro: c = new VidroConfig().Normalized(); break;
                case TipoPeca.Madeira: c = new MadeiraConfig().Normalized(); break;
                default: c = new ChapimConfig().Normalized(); break;
            }
            var first = Modelos.FirstOrDefault(m => m.Tipo == t);
            return first != null ? first.Aplicar(c) : c;
        }

        private static ModeloPeca B(string id, string nome, string desc, Action<BancadaConfig> set) => new ModeloPeca
        {
            Id = id, Nome = nome, Descricao = desc, Tipo = TipoPeca.Bancada,
            Aplicar = cfg =>
            {
                var src = cfg as BancadaConfig ?? new BancadaConfig();
                var c = new BancadaConfig { Codigo = src.Codigo, Nome = src.Nome, MaterialPedra = src.MaterialPedra, CorPedra = src.CorPedra, MaterialCuba = src.MaterialCuba, CorCuba = src.CorCuba, ModeloId = id };
                set(c);
                return c.Normalized();
            }
        };

        private static ModeloPeca N(string id, string nome, string desc, Action<NichoConfig> set) => new ModeloPeca
        {
            Id = id, Nome = nome, Descricao = desc, Tipo = TipoPeca.Nicho,
            Aplicar = cfg =>
            {
                var src = cfg as NichoConfig ?? new NichoConfig();
                var c = new NichoConfig
                {
                    Codigo = src.Codigo, Nome = src.Nome, ModeloId = id, AlturaBaseMm = src.AlturaBaseMm,
                    MaterialPedra = src.MaterialPedra, CorPedra = src.CorPedra, MaterialInterno = src.MaterialInterno, CorInterno = src.CorInterno,
                    MaterialPrateleira = src.MaterialPrateleira, CorPrateleira = src.CorPrateleira
                };
                set(c);
                return c.Normalized();
            }
        };

        private static ModeloPeca C(string id, string nome, string desc, Action<ChapimConfig> set) => new ModeloPeca
        {
            Id = id, Nome = nome, Descricao = desc, Tipo = TipoPeca.Chapim,
            Aplicar = cfg =>
            {
                var src = cfg as ChapimConfig ?? new ChapimConfig();
                var c = new ChapimConfig { Codigo = src.Codigo, Nome = src.Nome, ModeloId = id, MaterialPedra = src.MaterialPedra, CorPedra = src.CorPedra, EspessuraMuroMm = src.EspessuraMuroMm };
                set(c);
                return c.Normalized();
            }
        };

        private static ModeloPeca R(string id, string nome, string desc, Action<RaloConfig> set) => new ModeloPeca
        {
            Id = id, Nome = nome, Descricao = desc, Tipo = TipoPeca.Ralo,
            Aplicar = cfg =>
            {
                var src = cfg as RaloConfig ?? new RaloConfig();
                var c = new RaloConfig
                {
                    Codigo = src.Codigo, Nome = src.Nome, ModeloId = id, Sistema = src.Sistema,
                    MaterialGrelha = src.MaterialGrelha, CorGrelha = src.CorGrelha, MaterialCorpo = src.MaterialCorpo, CorCorpo = src.CorCorpo,
                    MaterialRevestimento = src.MaterialRevestimento, CorRevestimento = src.CorRevestimento
                };
                set(c);
                return c.Normalized();
            }
        };

        private static Cuba Cu(TipoCuba t, double w, double d, double fundo, double r = 30, double x = 0) =>
            new Cuba { Tipo = t, LarguraMm = w, ProfundidadeMm = d, FundoMm = fundo, RaioMm = r, PosicaoXMm = x, FuroTorneira = t != TipoCuba.Apoio };

        public static readonly IReadOnlyList<ModeloPeca> Modelos = new List<ModeloPeca>
        {
            // ---------------------------------------------------------------- bancadas
            B("B-COZ-RETA", "Cozinha reta", "Cuba de embutir, frontão e saia; encostada à esquerda.", c =>
            {
                c.Formato = FormatoBancada.Reta; c.ComprimentoAMm = 1800; c.ProfundidadeMm = 600; c.AlturaMm = 900;
                c.Cubas.Add(Cu(TipoCuba.Embutir, 500, 400, 180, 20, 600));
            }),
            B("B-COZ-L", "Cozinha em L", "Retorno à esquerda, cuba e recorte de cooktop.", c =>
            {
                c.Formato = FormatoBancada.L; c.Lado = LadoRetorno.Esquerda; c.ComprimentoAMm = 2600; c.ComprimentoBMm = 1600; c.ProfundidadeMm = 600; c.AlturaMm = 900;
                c.EncostoEsquerdo = false; c.EncostoDireito = true;
                c.Cubas.Add(Cu(TipoCuba.Embutir, 560, 400, 200, 20, 1300)); c.Cooktop = true; c.CooktopPosicaoXMm = 2100;
            }),
            B("B-COZ-DUPLA", "Cozinha com cuba dupla", "Reta, cuba dupla de embutir 60/40, frontão só no fundo.", c =>
            {
                c.Formato = FormatoBancada.Reta; c.ComprimentoAMm = 2200; c.ProfundidadeMm = 600; c.AlturaMm = 900;
                var cu = Cu(TipoCuba.Embutir, 840, 420, 200, 15, 800); cu.Dupla = true; cu.SeptoMm = 60; cu.ProporcaoPct = 60;
                c.Cubas.Add(cu);
                c.SemFrontao.Add("Esq");
            }),
            B("B-COZ-L-PEN", "Cozinha em L com península", "Retorno mais raso (45 cm) e sem frontão, cuba dupla.", c =>
            {
                c.Formato = FormatoBancada.L; c.Lado = LadoRetorno.Direita; c.ComprimentoAMm = 2600; c.ComprimentoBMm = 1500; c.ProfundidadeMm = 600; c.ProfundidadeBMm = 450; c.AlturaMm = 900;
                c.EncostoEsquerdo = true; c.EncostoDireito = false;
                var cu = Cu(TipoCuba.Embutir, 800, 400, 200, 15, 1000); cu.Dupla = true; c.Cubas.Add(cu);
                c.SemFrontao.Add("B");
            }),
            B("B-COZ-U", "Cozinha em U", "Três trechos, cuba no centro.", c =>
            {
                c.Formato = FormatoBancada.U; c.ComprimentoAMm = 2800; c.ComprimentoBMm = 1500; c.ComprimentoCMm = 1500; c.ProfundidadeMm = 600; c.AlturaMm = 900;
                c.EncostoEsquerdo = false; c.EncostoDireito = false;
                c.Cubas.Add(Cu(TipoCuba.Embutir, 560, 400, 200, 20));
            }),
            B("B-LAV-EMB", "Lavatório – cuba de embutir", "Banheiro, 80 cm de altura, saia de 10 cm.", c =>
            {
                c.Formato = FormatoBancada.Reta; c.ComprimentoAMm = 1000; c.ProfundidadeMm = 500; c.AlturaMm = 800; c.BordaAlturaMm = 100;
                c.EncostoEsquerdo = true; c.EncostoDireito = true;
                c.Cubas.Add(Cu(TipoCuba.Embutir, 420, 330, 160, 60));
                c.MaterialCuba = "Louça branca"; c.CorCuba = "F4F4F2";
            }),
            B("B-LAV-ESC", "Lavatório – cuba esculpida", "Cuba esculpida na própria pedra.", c =>
            {
                c.Formato = FormatoBancada.Reta; c.ComprimentoAMm = 1200; c.ProfundidadeMm = 500; c.AlturaMm = 800; c.BordaAlturaMm = 150;
                c.EncostoEsquerdo = true; c.EncostoDireito = true;
                c.Cubas.Add(Cu(TipoCuba.Esculpida, 600, 300, 120, 10));
            }),
            B("B-LAV-APOIO", "Lavatório – cuba de apoio", "Cuba sobre a pedra, saia de 15 cm.", c =>
            {
                c.Formato = FormatoBancada.Reta; c.ComprimentoAMm = 900; c.ProfundidadeMm = 500; c.AlturaMm = 750; c.BordaAlturaMm = 150;
                c.EncostoEsquerdo = true; c.EncostoDireito = true;
                c.Cubas.Add(Cu(TipoCuba.Apoio, 450, 350, 140, 80));
                c.MaterialCuba = "Louça branca"; c.CorCuba = "F4F4F2";
            }),
            B("B-SERV", "Área de serviço com tanque", "Tanque de embutir fundo.", c =>
            {
                c.Formato = FormatoBancada.Reta; c.ComprimentoAMm = 1200; c.ProfundidadeMm = 600; c.AlturaMm = 900;
                c.Cubas.Add(Cu(TipoCuba.Embutir, 550, 420, 280, 20, 400));
            }),
            B("B-SECA", "Bancada seca", "Sem cuba (aparador, apoio, banca seca).", c =>
            {
                c.Formato = FormatoBancada.Reta; c.ComprimentoAMm = 1500; c.ProfundidadeMm = 450; c.AlturaMm = 900;
                c.EncostoEsquerdo = false; c.Frontao = true;
            }),
            B("B-ILHA", "Ilha com pés", "Sem frontão, pés de pedra nas duas pontas.", c =>
            {
                c.Formato = FormatoBancada.Reta; c.ComprimentoAMm = 2000; c.ProfundidadeMm = 900; c.AlturaMm = 900;
                c.EncostoEsquerdo = false; c.EncostoDireito = false; c.Frontao = false; c.PeEsquerdo = true; c.PeDireito = true;
                c.FundoLivre = true;
                c.Cooktop = true; c.CooktopPosicaoXMm = 1000;
            }),
            B("B-ILHA-SAIA", "Ilha com saia dos dois lados", "Fundo livre: saia na frente, no fundo e nas pontas; cooktop.", c =>
            {
                c.Formato = FormatoBancada.Reta; c.ComprimentoAMm = 2500; c.ProfundidadeMm = 900; c.AlturaMm = 900;
                c.EncostoEsquerdo = false; c.EncostoDireito = false; c.Frontao = false; c.FundoLivre = true;
                c.Borda = BordaFrontal.Saia; c.BordaAlturaMm = 40;
                c.Cooktop = true; c.CooktopPosicaoXMm = 1250;
            }),

            // ---------------------------------------------------------------- nichos
            N("N-BOX", "Nicho de box", "30 × 60 cm, uma prateleira de vidro.", c =>
            {
                c.LarguraMm = 300; c.AlturaMm = 600; c.ProfundidadeMm = 100; c.Prateleiras = 1;
            }),
            N("N-LARGO", "Nicho largo", "90 × 40 cm, sem prateleira.", c =>
            {
                c.LarguraMm = 900; c.AlturaMm = 400; c.ProfundidadeMm = 100; c.Prateleiras = 0;
            }),
            N("N-DIV", "Nicho com divisória", "60 × 40 cm com divisória vertical.", c =>
            {
                c.LarguraMm = 600; c.AlturaMm = 400; c.ProfundidadeMm = 100; c.Prateleiras = 0; c.Divisorias = 1;
            }),
            N("N-ALTO", "Nicho alto 2 prateleiras", "30 × 90 cm, duas prateleiras.", c =>
            {
                c.LarguraMm = 300; c.AlturaMm = 900; c.ProfundidadeMm = 100; c.Prateleiras = 2;
            }),
            N("N-MOLD", "Nicho com moldura", "45 × 60 cm com moldura de pedra na face da parede.", c =>
            {
                c.LarguraMm = 450; c.AlturaMm = 600; c.ProfundidadeMm = 100; c.Prateleiras = 1; c.Moldura = true;
            }),

            // ---------------------------------------------------------------- chapins
            C("C-UMLADO", "Chapim com caimento", "Caimento de 2% para dentro, pingadeiras dos dois lados.", c =>
            {
                c.Caimento = CaimentoChapim.UmLado; c.InclinacaoPct = 2; c.AbaExternaMm = 30; c.AbaInternaMm = 30;
            }),
            C("C-PLANO", "Chapim plano", "Reto, com pingadeiras.", c =>
            {
                c.Caimento = CaimentoChapim.Plano; c.AbaExternaMm = 30; c.AbaInternaMm = 30;
            }),
            C("C-2AGUAS", "Chapim duas águas", "Caimento para os dois lados.", c =>
            {
                c.Caimento = CaimentoChapim.DuasAguas; c.InclinacaoPct = 3; c.AbaExternaMm = 30; c.AbaInternaMm = 30;
            }),
            C("C-MURETA", "Chapim de mureta", "Mureta de 10 cm, abas de 2 cm, sem pingadeira.", c =>
            {
                c.EspessuraMuroMm = 100; c.AbaExternaMm = 20; c.AbaInternaMm = 20; c.Pingadeira = false; c.Caimento = CaimentoChapim.Plano; c.EspessuraMm = 20;
            }),
            C("C-PLATIB", "Chapim de platibanda", "Aba só para fora, caimento para dentro.", c =>
            {
                c.AbaExternaMm = 40; c.AbaInternaMm = 0; c.Caimento = CaimentoChapim.UmLado; c.InclinacaoPct = 3;
            }),

            // ---------------------------------------------------------------- ralos (conexões de tubo)
            R("R-LIN-BOX", "Ralo linear de box", "70 cm × 7 cm, fendas, saída DN50 no centro.", c =>
            {
                c.Formato = TipoRalo.Linear; c.ComprimentoMm = 700; c.LarguraMm = 70; c.AlturaCorpoMm = 80; c.Grelha = GrelhaRalo.Fendas; c.DiametroSaidaMm = 50;
            }),
            R("R-LIN-LONGO", "Ralo linear parede a parede", "1,20 m × 5 cm, saída DN50 numa ponta.", c =>
            {
                c.Formato = TipoRalo.Linear; c.ComprimentoMm = 1200; c.LarguraMm = 50; c.AlturaCorpoMm = 70; c.Grelha = GrelhaRalo.Fendas;
                c.DiametroSaidaMm = 50; c.Posicao = PosicaoSaida.Esquerda; c.DistanciaSaidaMm = 100;
            }),
            R("R-LIN-INV", "Ralo linear invisível", "80 cm, tampa revestível com o piso.", c =>
            {
                c.Formato = TipoRalo.Linear; c.ComprimentoMm = 800; c.LarguraMm = 80; c.AlturaCorpoMm = 90; c.Grelha = GrelhaRalo.Revestivel; c.RebaixoMm = 15; c.DiametroSaidaMm = 50;
            }),
            R("R-LIN-AREA", "Canaleta de área externa", "1,50 m × 15 cm, grelha quadriculada, saída DN100.", c =>
            {
                c.Formato = TipoRalo.Linear; c.ComprimentoMm = 1500; c.LarguraMm = 150; c.AlturaCorpoMm = 150; c.Grelha = GrelhaRalo.Quadriculada; c.EspessuraGrelhaMm = 20;
                c.MolduraMm = 10; c.DiametroSaidaMm = 100; c.Sistema = SistemaRalo.Pluvial;
                c.MaterialGrelha = "Ferro fundido"; c.CorGrelha = "4A4F55";
            }),
            R("R-QUAD-10", "Ralo seco 10 × 10", "Quadrado, furos, saída DN40.", c =>
            {
                c.Formato = TipoRalo.Quadrado; c.LarguraMm = 100; c.AlturaCorpoMm = 70; c.Grelha = GrelhaRalo.Furos; c.DiametroSaidaMm = 40;
            }),
            R("R-QUAD-15", "Ralo 15 × 15", "Quadrado, fendas, saída DN50.", c =>
            {
                c.Formato = TipoRalo.Quadrado; c.LarguraMm = 150; c.AlturaCorpoMm = 90; c.Grelha = GrelhaRalo.Fendas; c.DiametroSaidaMm = 50;
            }),
            R("R-RED-10", "Ralo redondo Ø10", "Grelha de furos, saída DN40.", c =>
            {
                c.Formato = TipoRalo.Redondo; c.LarguraMm = 100; c.AlturaCorpoMm = 70; c.Grelha = GrelhaRalo.Furos; c.DiametroSaidaMm = 40;
            }),
            R("R-CXS-15", "Caixa sifonada 15 × 15", "3 entradas DN40, saída DN50 vertical.", c =>
            {
                c.Formato = TipoRalo.CaixaSifonada; c.LarguraMm = 150; c.AlturaCorpoMm = 185; c.Grelha = GrelhaRalo.Fendas;
                c.Entradas = 3; c.DiametroEntradaMm = 40; c.DiametroSaidaMm = 50; c.MolduraMm = 8;
            }),
            R("R-CXS-10", "Caixa sifonada 10 × 10", "1 entrada DN40, saída DN40.", c =>
            {
                c.Formato = TipoRalo.CaixaSifonada; c.LarguraMm = 100; c.AlturaCorpoMm = 150; c.Grelha = GrelhaRalo.Furos;
                c.Entradas = 1; c.DiametroEntradaMm = 40; c.DiametroSaidaMm = 40;
            }),
            R("R-HEMI", "Ralo hemisférico (calha/laje)", "Grelha em domo Ø100, saída DN100, águas pluviais.", c =>
            {
                c.Formato = TipoRalo.Hemisferico; c.LarguraMm = 110; c.DiametroSaidaMm = 100; c.AlturaCorpoMm = 80; c.Sistema = SistemaRalo.Pluvial;
                c.MaterialGrelha = "Alumínio fundido"; c.CorGrelha = "A9AEB4";
            }),

        };

        public static IEnumerable<ModeloPeca> DoTipo(TipoPeca t) => Modelos.Where(m => m.Tipo == t);
    }
}
