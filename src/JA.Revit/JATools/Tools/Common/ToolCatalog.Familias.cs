using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace RevitDwgUnifier.Tools.Common
{
    /// <summary>
    /// The JA Famílias half of the catalog (aba "JA Famílias"): groups, tools, icons and option help.
    /// ToolCatalog.cs (aba "JA Tools") joins both halves: one catalog for the ribbon tooltips, the
    /// option windows, the results, the Central de Ajuda and the Central JA / Buscar Comando.
    /// </summary>
    public static partial class ToolCatalog
    {
        // Groups of the JA Famílias tab = its ribbon panels.
        public const string Esquadrias = "Esquadrias", Perfis = "Perfis", Pecas = "Pedras e Nichos", Ralos = "Ralos", Editar = "Gerenciar", Documentar = "Documentar", Ajustes = "Ajustes";
        public const string Revestimentos = "Revestimentos", VidrosMadeira = "Vidros e Madeira";

        // 2.0: Ralos went into Revestimentos; the old "Revestimentos e Vidros" panel is split in two
        private static string[] GroupOrderFamilias() => new[] { Esquadrias, Perfis, Pecas, Revestimentos, VidrosMadeira, Editar, Documentar, Ajustes };

        private static Color? CorFamilias(string group)
        {
            switch (group)
            {
                case Esquadrias: return Color.FromRgb(0x2F, 0x6F, 0x8F);
                case Perfis: return Color.FromRgb(0x3E, 0x5F, 0x7A);
                case Pecas: return Color.FromRgb(0x8A, 0x5A, 0x3C);
                case Ralos:
                case Revestimentos: return Color.FromRgb(0x9A, 0x6B, 0x2F);
                case VidrosMadeira: return Color.FromRgb(0x2E, 0x6E, 0x86);
                case Editar: return Color.FromRgb(0x4F, 0x5D, 0x6E);
                case Documentar: return Color.FromRgb(0x5C, 0x6B, 0x33);
                case Ajustes: return Color.FromRgb(0x18, 0x21, 0x2B);
                default: return null;
            }
        }

        private static string IntroFamilias(string group)
        {
            switch (group)
            {
                case Esquadrias: return "Monte janelas, portas e painéis de parede cortina num configurador visual e gere famílias Revit prontas, já carregadas e posicionadas no projeto.";
                case Perfis: return "Perfis de parede cortina (montantes e travessas): reto, chanfrado, boleado, tubos, I, T, U, L, com tampa de pressão e cantos em meia esquadria — e a troca dos montantes em qualquer vista.";
                case Pecas: return "Bancadas, nichos e chapins de pedra com a mesma customização das esquadrias.";
                case Ralos:
                case Revestimentos: return "Revestimentos por ambiente: paginação de piso e parede, rodapé (por ambiente, em vários ambientes ou desenhado no local, com os descontos e preso na parede), forro e sanca, impermeabilização, escada revestida e ralos; Atualizar Revestimentos refaz tudo depois que o projeto muda.";
                case VidrosMadeira: return "Guarda-corpo de vidro, box de banheiro, painel ripado e pergolado, desenhados no local.";
                case Editar: return "Reabra qualquer família gerada e todas as cópias no modelo são atualizadas; a biblioteca guarda os modelos salvos para reutilizar; Conferir Planta mede o desenho 2D das esquadrias.";
                case Documentar: return "Quadros de esquadrias e de peças (pedra, comprimentos, áreas) e relatório com elevações, vidros, transmitância e áreas de iluminação e ventilação.";
                case Ajustes: return "Pastas, modelos de família e padrões do escritório.";
                default: return null;
            }
        }

        private static Dictionary<string, string> CommonFamilias() => new Dictionary<string, string>
        {
            ["folder"] = "Pasta onde os arquivos serão gravados (criada se não existir).",
        };

        private static List<ToolInfo> FerramentasFamilias() => new List<ToolInfo>
        {
            // ------------------------------------------------------------------ Criar
            new ToolInfo { Title = "Nova Janela", Group = Esquadrias, Glyph = "JN", Verb = "Gerar e posicionar",
                Description = "Configurador de janelas: escolha um modelo pronto ou monte a sua (construção livre), defina medidas, perfis, cor e vidro e gere a família.",
                Steps = new[] { "Escolha um modelo na lista (ou comece de um e altere).", "Ajuste medidas, divisões e a abertura de cada módulo clicando na prévia.", "Defina perfil, cor e vidro.", "Gerar e posicionar: clique nas paredes; Esc termina." },
                Tips = new[] { "A prévia mostra a esquadria vista de FORA; esquerda/direita das dobradiças são vistas de fora.",
                               "Não é preciso informar a espessura da parede: a janela se ajusta à parede onde for colocada, e os revestimentos de cada janela mudam em Propriedades › Cotas.",
                               "Deixe uma medida de linha ou módulo vazia para ela ocupar o espaço que sobrar.",
                               "O código (J01, J02…) vira a Marca de tipo e o nome da família: use Editar Família para mudar uma existente.",
                               "Salve suas composições na Biblioteca para usar em outros projetos." } },
            new ToolInfo { Title = "Nova Porta", Group = Esquadrias, Glyph = "PT", Verb = "Gerar e posicionar",
                Description = "Configurador de portas: abrir, correr, pivotante, com bandeira, vidro, veneziana ou folha cega.",
                Steps = new[] { "Escolha um modelo de porta.", "Ajuste medidas, divisões e abertura de cada módulo.", "Defina perfil, cor, vidro, soleira e puxadores.", "Gerar e posicionar nas paredes." },
                Tips = new[] { "Em planta a porta aparece como bloco de CAD: marco com batente, guarnições, folha aberta com o arco e a soleira. A aba Planta 2D muda ângulo, folha, arco e o que aparece; no Revit cada item é um parâmetro Sim/Não “Planta - …”.",
                               "Soleira ou filete: código, material, comprimento, largura e espessura na aba Detalhes; entram no Quadro de Soleiras e Peitoris.",
                               "“Abre para fora” fica no módulo selecionado, na aba Divisões.",
                               "Correr: em Movimento escolha “Todas correm”, “Para os dois lados” (abertura central; com 4 ou 6 folhas as das pontas ficam fixas) ou “Recolhem p/ um lado” (uma folha fixa e as outras recolhendo sobre ela, cada uma no seu trilho)." } },
            new ToolInfo { Title = "Porta de Elevador", Group = Esquadrias, Glyph = "PE", Verb = "Gerar",
                Description = "Gera uma família paramétrica simplificada de porta de elevador de pavimento (abertura central): portal na face do hall, duas folhas, soleira e botoeira. Largura, altura, portal, botoeira e materiais são parâmetros; a família acompanha a espessura da parede.",
                Steps = new[] { "Informe o vão livre (largura e altura), a largura do portal e a botoeira.", "Gerar: a família é criada, carregada e fica pronta para clicar nas paredes (Esc termina).", "No projeto, mude medidas e materiais em Editar tipo; a botoeira liga/desliga por porta." },
                Tips = new[] { "A face do portal é a face externa da família (o hall): inverta com as setas se aparecer do lado do poço.",
                               "Tipos prontos: 800 × 2000, 900 × 2100 e 1100 × 2100 (maca).",
                               "Folhas e portal estão nas subcategorias Painel e Moldura/Montante da porta: controle de visibilidade e espessura de linha por elas." } },
            new ToolInfo { Title = "Parede Cortina", Group = Esquadrias, Glyph = "PC", Verb = "Gerar e trocar painéis",
                Description = "Janelas e portas para paredes cortina: troca os painéis selecionados por uma esquadria do configurador, já no tamanho do painel.",
                Steps = new[] { "Selecione os painéis da parede cortina (Tab alterna entre parede e painel).", "Clique em Parede Cortina e escolha Janela ou Porta.", "Monte a esquadria; largura e altura vêm do painel.", "Gerar e trocar painéis." },
                Tips = new[] { "Painéis de tamanhos diferentes ganham um código para cada tamanho (J01, J02…).",
                               "A esquadria fica centrada no plano da parede cortina; “Deslocar p/ fora” move o marco para fora ou para dentro.",
                               "Usa o modelo de família do Revit para parede cortina (ex.: Metric Window - Curtain Wall.rft).",
                               "Se a grade mudar de tamanho, use Editar Família no painel trocado: o configurador abre com o tamanho novo." } },
            new ToolInfo { Title = "Fachada Paginada", Group = Esquadrias, Glyph = "FP", Verb = "Paginar",
                Description = "Revestimento de ACM ou brise na frente das paredes selecionadas: cria uma parede cortina na face externa (com o afastamento da subestrutura), divide em módulos de largura × altura e abre o Painel de Cortina nos painéis para virarem cassete de ACM, brise vertical, veneziana ou painel liso, em todos os tamanhos da paginação.",
                Steps = new[] { "Selecione as paredes (retas).", "Escolha o revestimento, o módulo, as sobras e o afastamento.", "Na janela do Painel de Cortina, ajuste junta, material e cor e gere." },
                Tips = new[] { "Módulos centralizados deixam sobras iguais nas duas pontas.", "A parede cortina não delimita ambientes nem se une às paredes de trás.", "Para mudar a paginação: apague a parede cortina e rode de novo." } },
            new ToolInfo { Title = "Painel de Cortina", Group = Esquadrias, Glyph = "PN", Verb = "Gerar e trocar painéis",
                Description = "Painéis de parede cortina que não são porta nem janela: cassete ACM (com retornos e junta), sólido, vidro, sanduíche, veneziana, brise vertical e emoldurado — no tamanho dos painéis selecionados, trocando-os na parede cortina.",
                Steps = new[] { "Selecione os painéis (Tab alterna entre parede e painel) ou a parede cortina inteira.", "Clique em Painel de Cortina, escolha o tipo e ajuste medidas, aletas, moldura e materiais.", "Gerar e trocar: uma família por tamanho de painel, todas com o mesmo código (PN01…)." },
                Tips = new[] { "A prévia mostra a frente vista de fora e um corte (planta, no brise); o deslocamento leva o painel para fora (+) ou para dentro (−) da linha da grade.",
                               "Painéis de tamanhos diferentes viram famílias “JA Painel PN01 1250x3000”, “… 900x3000”: o código (marca de tipo) é o mesmo, para o quadro.",
                               "Para mudar um painel, use Editar Família nele (ou abra Painel de Cortina e escolha o código no topo): todos os tamanhos são refeitos.",
                               "Em detalhe baixo, veneziana e brise aparecem como uma chapa fina (modelo mais leve); as aletas aparecem no médio e no fino.",
                               "Se o painel sair com o exterior para dentro, gere de novo com “Inverter interior / exterior”.",
                               "O Quadro de Peças tem a opção Painéis de cortina: código, dimensões, material, quantidade e área." } },
            new ToolInfo { Title = "Novo Perfil", Group = Perfis, Glyph = "PF", Verb = "Criar perfil",
                Description = "Cria perfis de parede cortina (montantes e travessas): retangular reto, chanfrado ou boleado, tubos, I, T, U, L, com tampa de pressão — ou montantes de canto em meia esquadria (V) e em L.",
                Steps = new[] { "Opcional: selecione montantes para já receberem o perfil.", "Escolha a forma e as medidas; o desenho mostra a seção com o exterior para cima.", "Criar perfil: a família de perfil e o tipo de montante entram no projeto." },
                Tips = new[] { "O vidro é cortado onde o perfil cruza o plano dos painéis (linha tracejada do desenho): o deslocamento leva o perfil para fora ou para dentro.",
                               "Para editar, abra Novo Perfil e escolha o perfil no topo: todos os montantes desse tipo acompanham.",
                               "Meia esquadria: o Revit só aceita cantos do próprio sistema (V e L) — as pernas e a espessura são ajustadas; o perfil próprio não vale nos cantos.",
                               "Se o perfil aparecer virado na parede, gere de novo com “Inverter interior / exterior”.",
                               "A família de perfil vai para a pasta das famílias, subpasta Perfis." },
                FieldHelp = { } },
            new ToolInfo { Title = "Trocar Montantes", Group = Perfis, Glyph = "TM", Verb = "Trocar",
                Description = "Troca o tipo dos montantes escolhidos em planta, corte, elevação ou 3D — só eles, os do mesmo tipo nas mesmas paredes ou em todo o projeto, verticais ou horizontais.",
                Steps = new[] { "Selecione montantes (Tab alterna entre parede e montante) ou paredes cortina inteiras — ou clique no botão e escolha.", "Escolha o novo tipo e o alcance.", "Trocar." },
                Tips = new[] { "Montantes fixados (pino) são liberados automaticamente.",
                               "“Novo perfil…” abre o configurador e aplica o perfil criado nos mesmos montantes.",
                               "Selecionar a parede cortina inteira pega todos os montantes dela; use Direção para trocar só as travessas." } },
            new ToolInfo { Title = "Biblioteca", Group = Editar, Glyph = "BI", Verb = "Gerar e posicionar",
                Description = "Abre o configurador de esquadrias nas suas composições salvas (Biblioteca) e nas esquadrias JA deste projeto.",
                Steps = new[] { "Escolha uma composição salva.", "Ajuste o que precisar.", "Gerar." },
                Tips = new[] { "A biblioteca é uma pasta de arquivos .json: pode ficar na rede para o escritório todo (Configurações).",
                               "Bancadas, nichos, chapins e ralos têm a própria biblioteca: botão Biblioteca no topo da lista de modelos de cada configurador (subpastas Bancadas, Nichos, Chapins e Ralos)." } },

            new ToolInfo { Title = "Nova Bancada", Group = Pecas, Glyph = "BC", Verb = "Gerar e posicionar",
                Description = "Configurador de bancadas de pedra: reta, em L ou em U (em qualquer ângulo), pontas em ângulo e cantos chanfrados, com cubas (embutir, sobrepor, esculpida, apoio), cooktop, saia ou tira americana, tento, frontão e pés.",
                Steps = new[] { "Escolha um modelo (cozinha, lavatório, área de serviço, ilha…).", "Ajuste formato e medidas; marque as pontas que encostam em parede.", "Defina bordas, cubas e materiais.", "Gerar e posicionar: clique onde a bancada vai (Espaço gira); Esc termina." },
                Tips = new[] { "A origem da família é o canto do fundo à esquerda, no piso: a bancada fica encostada na parede ao clicar nela com Espaço para girar.",
                               "Bordas encostadas recebem o frontão; bordas livres recebem saia, tira americana ou tento.",
                               "A área de pedra (tampo, saia, frontão, tento, pés e cuba esculpida) vai para JA_AreaPedra e para o Quadro de Peças.",
                               "Cada cuba e o cooktop têm posição própria; deixe vazio para centralizar.",
                               "Áreas seca e molhada (aba Medidas): marque “Área molhada”, diga onde começa e termina no trecho principal, o rebaixo e a borda de contenção.",
                               "Formato fora do padrão (recortes de pilar, paredes tortas)? Use Bancada no Local e desenhe o contorno na planta.",
                               "Ângulos: L e U com retorno em qualquer ângulo (Medidas › Formato), pontas cortadas em ângulo e cantos chanfrados (Medidas › Pontas e cantos), aresta chanfrada e saia em meia-esquadria (Bordas)." } },
            new ToolInfo { Title = "Bancada no Local", Group = Pecas, Glyph = "BL", Verb = "Gerar no local",
                Description = "Bancada de qualquer formato modelada onde ela fica: você clica os cantos do tampo na planta, contornando paredes e pilares (ou usa linhas desenhadas), e a família nasce com esse contorno exato, já no lugar — com áreas seca e molhada, cubas e cooktop posicionados no projeto.",
                Steps = new[] { "Abra a planta baixa e clique em Bancada no Local.", "Clique os cantos do tampo em sequência (Esc ou um clique no 1º canto fecha).", "Na janela: confira as bordas (parede com frontão / livre com saia), divida as áreas, posicione cubas e cooktop.", "Gerar: a bancada aparece exatamente onde foi desenhada." },
                Tips = new[] { "Como desenhar (janela comum a todas as ferramentas de desenho): ferramentas de Linha de detalhe do Revit, selecionar linhas, selecionar bordas ou clicar — com orto (Shift inverte num clique) e ângulo do orto.", "As bordas que encostam em parede ou pilar já vêm como “Parede com frontão” e as outras como “Livre com saia”; clique numa borda da planta para mudar (sem saia, sem frontão, pé de pedra). Ctrl+clique seleciona várias.",
                               "Áreas seca e molhada: “Dividir áreas” fecha a janela para você clicar uma linha de uma borda à outra (dois cliques, ou mais para uma linha quebrada); depois clique na área, na planta da janela, para marcá-la como molhada. O rebaixo e a borda de contenção ficam no cartão Áreas.",
                               "Cubas e cooktop: “Posicionar cuba” e clique o centro no projeto; a cuba vem alinhada com a parede mais próxima e o ângulo pode ser ajustado.",
                               "Tampo de parede a parede no ambiente inteiro (nicho, lavabo): selecione o ambiente (ou a etiqueta dele) antes de clicar em Bancada no Local.",
                               "Contornos com curvas ou muitos recortes: desenhe com linhas de detalhe (ou de modelo), selecione as linhas e clique em Bancada no Local. Arcos viram trechos retos curtos.",
                               "Para mudar depois: selecione a bancada e use Editar Família (ou Bancada no Local): contorno, divisões e cubas podem ser refeitos no projeto.",
                               "Cada bancada no local é uma família própria (JA Bancada BC02…), entra no Quadro de Peças com a área de pedra e a área molhada.",
                               "Paredes de arquivos vinculados não são detectadas: marque as bordas de parede clicando nelas." } },
            new ToolInfo { Title = "Forro e Sanca", Group = Revestimentos, Glyph = "FS", Verb = "Criar forro",
                Description = "Forro de gesso nos ambientes selecionados, na altura escolhida, com tabica junto às paredes e, se quiser, sanca fechada ou aberta (faixa rebaixada, espelho e a abertura para a luz indireta). Escreve o acabamento do teto; rodar de novo substitui.",
                Steps = new[] { "Selecione os ambientes (ou clique-os depois).", "Tipo de forro, altura e tabica.", "Sanca: largura, rebaixo e, na aberta, a abertura para a luz." },
                Tips = new[] { "Atualizar Revestimentos refaz os forros depois que as paredes mudarem.", "A Lista de Compras soma placas, tabica e sanca." } },
            new ToolInfo { Title = "Painel Ripado", Group = VidrosMadeira, Glyph = "RP", Verb = "Gerar e colocar",
                Description = "Painel de ripas de madeira (ou MDF) na face de uma parede, entre dois pontos: ripas verticais ou horizontais centralizadas, vão entre elas e painel de fundo opcional. Sai a lista de corte.",
                Steps = new[] { "Abra a planta.", "Clique a parede do lado onde vai o painel, depois o início e o fim.", "Ripa, vão, altura, fundo e madeira; gere." },
                Tips = new[] { "Como desenhar (janela comum a todas as ferramentas de desenho): ferramentas de Linha de detalhe do Revit, selecionar linhas, selecionar bordas ou clicar — com orto (Shift inverte num clique) e ângulo do orto.", "Painéis iguais reaproveitam a mesma família.", "Editar Família no painel muda todas as cópias." } },
            new ToolInfo { Title = "Pergolado", Group = VidrosMadeira, Glyph = "PE", Verb = "Gerar e colocar",
                Description = "Pergolado de madeira em três cliques: pilares (com vão máximo entre eles), vigas e caibros com beiral; pode encostar numa parede (sem pilares desse lado). Mostra o volume de madeira e a lista de corte.",
                Steps = new[] { "Abra a planta.", "Clique dois cantos do primeiro lado e um ponto do lado oposto.", "Seções, espaçamento dos caibros, beiral e madeira; gere." },
                Tips = new[] { "Como desenhar (janela comum a todas as ferramentas de desenho): ferramentas de Linha de detalhe do Revit, selecionar linhas, selecionar bordas ou clicar — com orto (Shift inverte num clique) e ângulo do orto.", "As seções são de pré-dimensionamento: confirme com o cálculo estrutural." } },
            new ToolInfo { Title = "Impermeabilização", Group = Revestimentos, Glyph = "IM", Verb = "Calcular",
                Description = "Área a impermeabilizar de cada área molhada (pelo nome do ambiente ou os selecionados): piso, subida nas paredes menos as portas e as paredes do box até a altura escolhida, com o consumo do produto. Sai em Excel.",
                Steps = new[] { "Confira as palavras das áreas molhadas (ou selecione os ambientes).", "Subida nas paredes e no box.", "Sistema e consumo; Calcular." },
                Tips = new[] { "Os boxes do JA Famílias dão as paredes do chuveiro.", "A Lista de Compras usa as mesmas configurações." } },
            new ToolInfo { Title = "Atualizar Revestimentos", Group = Revestimentos, Glyph = "AR", Verb = "Atualizar",
                Description = "Depois que as paredes ou os ambientes mudam, refaz de uma vez — com as configurações guardadas em cada um — as paginações, os rodapés, os forros e sancas e as cores de Iluminação e Ventilação. Um só Desfazer volta tudo.",
                Steps = new[] { "Mude o projeto à vontade.", "Clique em Atualizar Revestimentos e marque o que refazer.", "Atualizar." },
                Tips = new[] { "Paginações e rodapés de versões antigas entram também: os rodapés ganham o ambiente e as configurações da própria família; as paginações recalculam rejunte e argamassa.", "Iluminação sem configuração guardada usa os últimos valores da janela da ferramenta." } },
            new ToolInfo { Title = "Paginação de Piso e Parede", Group = Revestimentos, Glyph = "PG", Verb = "Paginar",
                Description = "Desenha a paginação de porcelanato, cerâmica ou pedra: em planta, no piso dos ambientes selecionados; em elevação ou corte, na face de parede clicada (com os vãos). Conta peças inteiras e recortes e calcula o m² a comprar com a perda, as caixas, o rejunte e a argamassa colante.",
                Steps = new[] { "Planta: selecione os ambientes. Elevação/corte: abra a vista de frente para a parede.", "Peça, junta, partida (centralizada, canto ou ponto clicado) e direção.", "Perda e m² por caixa para a compra." },
                Tips = new[] { "Cada ambiente / face vira um grupo “JA Paginação …”: selecione o grupo e rode de novo para refazer ou mudar; rodar num ambiente já paginado substitui a anterior.", "Os ralos do JA ficam fora da área paginada.", "O piso modelado vem do Pisos por Ambiente (aba JA Tools › Modelagem).", "Centralizada deixa recortes iguais nas duas bordas; alinhada com a parede mais longa segue ambientes girados.", "Na parede, as fiadas começam na base." } },
            new ToolInfo { Title = "Rodapé", Group = Revestimentos, Glyph = "RD", Verb = "Gerar e aplicar",
                Description = "Rodapé (pedra, porcelanato, MDF…) pelo contorno dos ambientes, com os descontos: portas (com folga), janelas até o piso, vãos sem porta (com retorno nas laterais, se quiser), paredes cortina, paredes revestidas (cerâmica, porcelanato…), móveis encostados e trechos curtos; cantos externos fechados. Mostra paredes, cada desconto, líquido e compra (perda e peças) e salva em Excel.",
                Steps = new[] { "Selecione os ambientes numa planta.", "Altura, espessura e material.", "Marque os descontos, a perda e o tamanho da peça." },
                Tips = new[] { "Cada peça sai com o nível no parâmetro JA_Nivel (Dados de identidade; dá para usar em tabelas e filtros). Para os rodapés antigos, use Preencher Parâmetros (aba JA Tools).", "Cada trecho fica preso na face da parede em que foi criado (alinhamento com cadeado): movendo a parede, o rodapé vai junto. Os retornos nos vãos e os trechos longe de parede ficam soltos.",
                               "“Mostrar prévia” lista os metros por ambiente antes de colocar; tudo o que a ferramenta faz se desfaz com um único Ctrl+Z.",
                               "Rodar de novo num ambiente substitui o rodapé que ele já tem.", "Parede revestida: o nome do tipo de parede ou o material da camada do lado do ambiente contém uma das palavras.",
                               "Vãos sem porta = aberturas de parede (Abertura de parede / vão) que descem até o rodapé; aberturas altas ficam com rodapé embaixo.",
                               "Para um trecho solto, use Rodapé no Local.",
                               "O Acabamento de base do ambiente recebe a pedra e a altura (coluna Rodapé do Quadro de Acabamentos)." } },
            new ToolInfo { Title = "Rodapé no Local", Group = Revestimentos, Glyph = "RL", Verb = "Desenhar",
                Description = "Desenhe o rodapé onde ele vai, numa planta: com as ferramentas do Revit (linha, retângulo, arco…), selecionando linhas ou bordas, ou clicando os pontos com orto. Cada trecho encosta na face da parede ao lado, com a pedra virada para o lado do desenho, os cantos se encontram e os cantos externos fecham; os mesmos descontos do Rodapé (portas, janelas até o piso, vãos, móveis, paredes cortina e revestidas).",
                Steps = new[] { "Abra a planta e escolha como desenhar (ferramentas do Revit, linhas, bordas ou cliques).", "Desenhe junto das paredes, do lado onde vai o rodapé (até 50 cm da face).", "Altura, espessura, material, descontos e compra." },
                Tips = new[] { "Cada peça sai com o nível no parâmetro JA_Nivel (Dados de identidade; dá para usar em tabelas e filtros). Para os rodapés antigos, use Preencher Parâmetros (aba JA Tools).", "Cada trecho fica preso na face da parede em que foi criado (alinhamento com cadeado): movendo a parede, o rodapé vai junto. Os retornos nos vãos e os trechos longe de parede ficam soltos.",
                               "Longe de parede a pedra fica do lado esquerdo do sentido do desenho (dá para inverter).",
                               "Dá para desenhar várias linhas soltas de uma vez: cada sequência ligada vira uma cadeia, com os cantos fechados.",
                               "Fica fora do Rodapé por Ambiente e do Atualizar Revestimentos: para mudar, apague os trechos e desenhe de novo.",
                               "Base acima do nível para piso elevado ou degrau." } },
            new ToolInfo { Title = "Rodapé por Ambiente", Group = Revestimentos, Glyph = "RA", Verb = "Aplicar",
                Description = "Rodapé em muitos ambientes de uma vez, cada um com o seu: escolha os ambientes numa lista (todos os do projeto) e o rodapé de cada um vem do Acabamento de base dele, do nome (Rodapé B e C por palavras, ex.: cozinha, serviço) ou do Rodapé A; ambientes como banheiro e lavabo ficam sem rodapé. Mesmos descontos do Rodapé. Resumo por tipo e por ambiente, compra e Excel.",
                Steps = new[] { "Marque os ambientes (vêm marcados os selecionados ou os do nível da planta).", "Defina os rodapés A, B e C e as palavras de cada um.", "Descontos, perda e peça." },
                Tips = new[] { "Cada peça sai com o nível no parâmetro JA_Nivel (Dados de identidade; dá para usar em tabelas e filtros). Para os rodapés antigos, use Preencher Parâmetros (aba JA Tools).", "“Mostrar prévia” lista os metros por ambiente antes de colocar; um único Ctrl+Z desfaz tudo.", "Cada trecho fica preso na face da parede em que foi criado (alinhamento com cadeado): movendo a parede, o rodapé vai junto. Os retornos nos vãos e os trechos longe de parede ficam soltos.",
                               "Ordem: sem rodapé → Acabamento de base → B → C → A.",
                               "Acabamento de base lido: “Granito branco h 100 mm”, “Poliestireno h 10 cm × 15”. O texto que o próprio JA Famílias escreveu é ignorado, então mudar o Rodapé A e rodar de novo troca o rodapé.",
                               "Rodar de novo substitui o rodapé de cada ambiente; os ambientes “sem rodapé” perdem o que tinham.",
                               "Atualizar Revestimentos refaz cada ambiente com o seu rodapé." } },
            new ToolInfo { Title = "Escada Revestida", Group = Revestimentos, Glyph = "ES", Verb = "Revestir",
                Description = "Piso e espelho de pedra em cada degrau das escadas do Revit selecionadas e piso nos patamares, a partir da geometria da escada; um tipo JA Degrau / JA Patamar por tamanho, com bocel. Confere a fórmula de Blondel. Vai para a Lista para a Marmoraria e o plano de corte.",
                Steps = new[] { "Selecione as escadas.", "Espessuras, bocel e pedra.", "Os degraus são colocados sobre cada piso da escada." },
                Tips = new[] { "A escada é tratada como acabada: a pedra fica no topo dos pisos.", "Patamar que não é retangular e lances curvos ficam de fora: use Bancada no Local › Soleira / peça de piso.", "Blondel: 2 espelhos + 1 piso entre 63 e 65 cm; NBR 9050: espelho até 18 cm e piso de pelo menos 28 cm." } },
            new ToolInfo { Title = "Guarda-corpo de Vidro", Group = VidrosMadeira, Glyph = "GC", Verb = "Gerar e desenhar",
                Description = "Guarda-corpo de vidro temperado/laminado com perfil U no piso ou fixação lateral e corrimão opcional, criado como Guarda-corpo do Revit (categoria Guarda-corpos): as placas de vidro são uma família de Balaústres (“JA Placa de vidro …”), o corrimão é o corrimão superior do tipo e o perfil U uma travessa no piso.",
                Steps = new[] { "Abra a planta do nível. Opcional: selecione linhas ou bordas desenhadas (retas ou curvas).", "Altura (1,10 m pela NBR 14718), vidro, fixação, corrimão e o tamanho das placas.", "Sem seleção: clique os pontos; Esc termina." },
                Tips = new[] { "Como desenhar (janela comum a todas as ferramentas de desenho): ferramentas de Linha de detalhe do Revit, selecionar linhas, selecionar bordas ou clicar — com orto (Shift inverte num clique) e ângulo do orto.", "O vidro fica centrado na linha.", "Placas iguais por trecho: um guarda-corpo do Revit por sequência de trechos com a mesma placa. Placas de módulo fixo: um guarda-corpo só, corrimão contínuo, sobra nas pontas.",
                               "Selecione os guarda-corpos e use Editar Família para mudar: eles são refeitos no mesmo caminho.", "Se o Revit não aceitar a placa como balaústre, o guarda-corpo é feito com as famílias do JA (Modelo genérico) e um aviso diz por quê." } },
            new ToolInfo { Title = "Box de Banheiro", Group = VidrosMadeira, Glyph = "BX", Verb = "Gerar e colocar",
                Description = "Box de vidro de correr (1 fixa + 1 de correr), de abrir ou fixo, na largura exata do vão: clique as duas faces das paredes e o box é gerado e colocado ali.",
                Steps = new[] { "Abra a planta.", "Box reto: clique a parede da esquerda e a da direita, vendo de fora, e Esc. Box em L: a parede do lado fixo, o canto e a parede do lado da porta.", "Escolha abertura, altura, vidro e cor dos perfis." },
                Tips = new[] { "Como desenhar (janela comum a todas as ferramentas de desenho): ferramentas de Linha de detalhe do Revit, selecionar linhas, selecionar bordas ou clicar — com orto (Shift inverte num clique) e ângulo do orto.", "O puxador e a abertura da porta ficam do lado de fora.", "Cada largura vira um tipo (BX01 900…)." } },
            new ToolInfo { Title = "Nicho no Local", Group = Pecas, Glyph = "NL", Verb = "Gerar no local",
                Description = "Nicho do tamanho exato do vão desenhado: clique dois cantos opostos do vão acabado na face da parede (elevação, corte ou 3D) e o nicho sai com essa largura, altura e base, colocado ali, do lado clicado da parede.",
                Steps = new[] { "Abra uma elevação, corte ou vista 3D da parede.", "Clique um canto do vão acabado e depois o canto oposto, na mesma face.", "Confira prateleiras, revestimento e base no configurador e gere." },
                Tips = new[] { "Como desenhar (janela comum a todas as ferramentas de desenho): ferramentas de Linha de detalhe do Revit, selecionar linhas, selecionar bordas ou clicar — com orto (Shift inverte num clique) e ângulo do orto.", "A profundidade é limitada pela espessura da parede (fica pelo menos 30 mm de parede atrás).", "Os cliques são na face da parede: use linhas de referência ou uma cota para acertar a medida, ou corrija os números no configurador." } },
            new ToolInfo { Title = "Novo Nicho", Group = Pecas, Glyph = "NI", Verb = "Gerar e posicionar",
                Description = "Configurador de nichos embutidos (box, banheiro, sala): o vazio corta a parede, com revestimento interno, base de pedra, prateleiras, divisórias e moldura.",
                Steps = new[] { "Escolha um modelo de nicho.", "Defina as medidas internas acabadas e a altura da base.", "Prateleiras, divisórias, revestimento, base e moldura.", "Gerar e posicionar: clique nas paredes (a face clicada é a frente do nicho)." },
                Tips = new[] { "As medidas são do vão acabado; o recorte na parede soma o revestimento e a base.",
                               "Nichos e chapins são gerados como Componentes de gabinete.",
                               "A altura da base é a Elevação padrão: cada cópia pode ter outra altura nas propriedades.",
                               "Prateleiras com “vidro” no nome do material ficam transparentes." } },
            new ToolInfo { Title = "Novo Chapim", Group = Pecas, Glyph = "CH", Verb = "Gerar e aplicar nos muros",
                Description = "Configurador de chapins para muros, platibandas e muretas: abas, pingadeiras e caimento (plano, um lado ou duas águas); aplicado no topo das paredes, acompanhando o comprimento de cada uma.",
                Steps = new[] { "Selecione os muros (opcional: também dá para clicar neles depois).", "Escolha um modelo e ajuste abas, espessura, caimento e pingadeira.", "Gerar e aplicar nos muros: um chapim por parede, no topo, com a aba externa para fora." },
                Tips = new[] { "Paredes de outra espessura ganham um tipo próprio (CH01-200, …) automaticamente.",
                               "Paredes curvas (arco): o chapim acompanha a curva — família própria por raio (CH01-R3575), com o “Ângulo do arco” de cada cópia nas propriedades.",
                               "O chapim é uma família baseada em linha: arraste as pontas para ajustar cantos e encontros.",
                               "JA_Comprimento soma os metros de chapim no Quadro de Peças; JA_AreaPedra é a pedra por metro." } },

            new ToolInfo { Title = "Ralo no Local", Group = Revestimentos, Glyph = "RC", Verb = "Gerar no local",
                Description = "Desenhe o ralo na planta com as próprias ferramentas de Linha de detalhe do Revit (linha, retângulo, círculo, polígono, arco, selecionar linhas…) — ou clicando ponto a ponto, junto de uma parede, ou selecionando linhas já desenhadas — e o ralo é gerado seguindo o desenho e colocado ali. Linha aberta = eixo de um ralo linear; contorno fechado (retângulo, L, U…) = formato e largura; quadrado = ralo quadrado; círculo = ralo redondo.",
                Steps = new[] { "Abra a planta e clique em Ralo no Local; escolha “Com as ferramentas do Revit”.", "A Linha de detalhe abre: desenhe o eixo ou o contorno do ralo e termine com Esc.", "Clique de novo em Ralo no Local (sem selecionar nada) e escolha grelha, saída e materiais na janela do ralo." },
                Tips = new[] { "Como desenhar (janela comum a todas as ferramentas de desenho): ferramentas de Linha de detalhe do Revit, selecionar linhas, selecionar bordas ou clicar — com orto (Shift inverte num clique) e ângulo do orto.", "Contorno fechado: só linhas retas, uma faixa de largura constante (sem ramos em T); o eixo sai no meio da faixa e a largura vem do desenho.",
                               "As linhas desenhadas são apagadas depois de criar o ralo (dá para desmarcar).",
                               "Linhas já desenhadas: modo “Selecionar linhas” (clique, Ctrl+clique, janela ou Tab para a cadeia), ou selecione-as antes de rodar.",
                               "Modo “Selecionar bordas”: use as arestas de um piso, forro, parede ou bancada como eixo ou contorno do ralo.", "A grelha fica sem aberturas junto dos cantos." } },
            new ToolInfo { Title = "Novo Ralo", Group = Revestimentos, Glyph = "RL", Verb = "Gerar e posicionar",
                Description = "Configurador de ralos: linear (qualquer comprimento e largura), quadrado, redondo, caixa sifonada com entradas e hemisférico — família de Conexões de tubo com conectores de tubo na saída (e nas entradas), prontos para ligar a tubulação.",
                Steps = new[] { "Escolha um modelo (linear de box, invisível, canaleta, 10 × 10, caixa sifonada…).", "Tipo, medidas e altura do corpo; desenho da grelha (fendas, furos, quadriculada ou revestível).", "Saída: diâmetro (DN40–DN150), vertical ou horizontal, posição; na caixa sifonada, as entradas.", "Gerar e posicionar: clique no piso (a origem é o topo da grelha, no piso acabado)." },
                Tips = new[] { "A família é uma Conexão de tubo (tipo de peça Multi Port): desenhe o tubo a partir do conector da saída ou arraste o tubo até ele.",
                               "Sistema Esgoto usa a classificação Sanitário; Águas pluviais usa a de águas pluviais do Revit (ou Outro).",
                               "Em detalhe fino a grelha mostra fendas, furos ou quadrícula; em baixo e médio fica lisa (planta mais leve).",
                               "Ralo linear com saída numa ponta: escolha Esquerda/Direita e a distância da ponta; a saída horizontal sai pela ponta.",
                               "O ralo entra no Quadro de Peças (Ralos) com código, descrição, dimensões e quantidade." } },

            new ToolInfo { Title = "Soleiras e Peitoris", Group = Pecas, Glyph = "SL", Verb = "Aplicar",
                Description = "Coloca soleira ou filete em todas as portas e peitoril em todas as janelas (seleção, vista ou projeto). Esquadrias JA ganham a peça na própria família; portas e janelas de outros fabricantes recebem uma peça de pedra separada no vão.",
                Steps = new[] { "Opcional: selecione as portas e janelas.", "Escolha onde aplicar, o tipo de peça, código, material e medidas.", "Aplicar: tipos JA são atualizados; nos outros vãos entra uma família JA Pedra por tamanho, no centro da parede." },
                Tips = new[] { "Esquadrias JA: a peça vale para o tipo inteiro (todas as cópias daquele tipo mudam juntas).",
                               "Peças separadas são Componentes de gabinete: entram no Quadro de Peças (Soleiras e peitoris) com código, material e medidas.",
                               "Rodar de novo não duplica: vãos que já têm peça ficam como estão. Marque “Refazer” para trocar as medidas.",
                               "Paredes cortina ficam de fora." } },

            new ToolInfo { Title = "Substituir pelos do JA", Group = Editar, Glyph = "SB", Verb = "Substituir",
                Description = "Troca portas, janelas e bancadas de outras famílias pelas do JA Famílias, no mesmo lugar: as esquadrias mantêm a parede, a posição e o peitoril; as bancadas mantêm posição, rotação, comprimento, profundidade e altura.",
                Steps = new[] { "Selecione portas, janelas ou bancadas (modelos genéricos, gabinetes, mobiliário).", "Escolha o modelo JA no configurador (abre com o tamanho mais comum).", "Gerar: um tipo JA por tamanho; cada elemento recebe o tipo do seu tamanho." },
                Tips = new[] { "Portas e janelas trocam de tipo (o elemento é o mesmo): etiquetas, cotas e o lado de abertura continuam. Confira o sentido de abertura depois.",
                               "Bancadas são apagadas e recriadas: confira a frente; se alguma ficou virada, gire 180°.",
                               "Esquadrias em parede cortina: use Parede Cortina. Nichos e ralos de outras famílias não são trocados." } },

            new ToolInfo { Title = "Conferir Planta", Group = Editar, Glyph = "CP", Verb = "Conferir",
                Description = "Confere, na planta ativa, o desenho 2D das janelas e portas JA: cada linha do marco, das folhas, do vidro, da soleira e da verga é medida e comparada com o desenho do configurador para a parede e os revestimentos reais.",
                Steps = new[] { "Abra a planta baixa.", "Opcional: selecione as esquadrias (sem seleção, confere todas as da vista).", "Conferir: o resultado lista o que está fora do lugar, em mm, por código.", "“Gerar de novo e conferir” refaz os tipos com diferença e confere outra vez." },
                Tips = new[] { "A causa mais comum de linha fora do lugar é uma família feita por uma versão antiga do JA Famílias: gerar de novo resolve.",
                               "Toda família gerada já é conferida no próprio editor de famílias (na parede do modelo e em mais duas espessuras); se algo sair do lugar, o aviso aparece no fim da geração.",
                               "Se nenhuma linha aparece, confira Visibilidade/Gráficos (subcategorias JA Planta) e o nível de detalhe da vista.",
                               "Os detalhes de cada linha ficam no registro (botão Abrir registro): mande para análise se a diferença continuar." } },

            // ------------------------------------------------------------------ Editar
            new ToolInfo { Title = "Editar Família", Group = Editar, Glyph = "ED", Verb = "Atualizar",
                Description = "Selecione uma janela, porta, bancada, nicho, chapim ou painel de cortina gerado pelo JA Famílias para reabrir o configurador; ao atualizar, todas as cópias daquele tipo mudam juntas.",
                Steps = new[] { "Selecione uma família JA no modelo (ou clique no botão e escolha uma).", "Altere o que quiser no configurador.", "Atualizar." },
                Tips = new[] { "Esquadrias: a família acompanha a espessura de qualquer parede; os revestimentos externo e interno são parâmetros de instância (Propriedades › Cotas).",
                               "Chapins: os tipos por espessura (CH01-200…) são atualizados junto.",
                               "Famílias de outras origens não podem ser editadas aqui." } },

            // ------------------------------------------------------------------ Documentar
            new ToolInfo { Title = "Quadro de Esquadrias", Group = Documentar, Glyph = "QE", Verb = "Criar quadro",
                Description = "Cria as tabelas de janelas e portas JA com código, elevação (imagem do tipo), descrição, medidas, quantidade, perfil, vidro, ferragens, Uw e áreas.",
                Steps = new[] { "Escolha janelas, portas ou ambas.", "Criar quadro.", "Arraste a tabela para a prancha." },
                Tips = new[] { "Só entram as esquadrias geradas pelo JA Famílias (que têm o parâmetro JA_Abertura)." } },
            new ToolInfo { Title = "Orçamento", Group = Documentar, Glyph = "R$", Verb = "Calcular",
                Description = "Estimativa de custo do que o JA Famílias colocou no projeto: janelas e portas por m² de vão, pedra por m² com preço por material (peças corridas pelo comprimento), cubas, ralos, guarda-corpos, boxes e as paginações (m² a comprar), com os preços que você digita (ficam salvos). Sai em Excel, com subtotais.",
                Steps = new[] { "Clique em Orçamento.", "Confira os preços unitários.", "O arquivo é salvo na pasta de relatórios." },
                Tips = new[] { "Painéis de cortina, ralos, ferragens e instalação não entram na conta.", "A área de pedra é a mesma do Quadro de Peças (JA_AreaPedra)." } },
            new ToolInfo { Title = "Cortes das Esquadrias", Group = Documentar, Glyph = "CT", Verb = "Criar cortes",
                Description = "Cria, para cada tipo de esquadria JA escolhido, um corte vertical atravessando a parede no meio da esquadria e um corte horizontal (chamada da planta do pavimento), recortados em volta dela, em escala de detalhe.",
                Steps = new[] { "Selecione esquadrias (opcional: senão, marque os tipos na lista).", "Escolha as vistas, a escala e a folga.", "Criar cortes e arrastar as vistas para a prancha." },
                Tips = new[] { "O corte horizontal usa a altura de corte da planta do nível (1,20 m usual): confira se ela passa pela janela.", "As vistas se chamam “JA Corte vertical J01” e “JA Corte horizontal J01”." } },
            new ToolInfo { Title = "Iluminação e Ventilação", Group = Documentar, Glyph = "IV", Verb = "Conferir",
                Description = "Confere cada ambiente: soma a área de iluminação e de ventilação das janelas e portas que dão para ele e compara com a fração mínima da área do piso (permanência prolongada ou transitória, pelo nome). Gera um CSV e seleciona os ambientes que não atendem.",
                Steps = new[] { "Confira as frações do código de obras do município.", "Ajuste as palavras que definem ambientes transitórios e isentos.", "Conferir: o CSV vai para a pasta de relatórios." },
                Tips = new[] { "Esquadrias JA usam as áreas calculadas pelo configurador; outras famílias, largura × altura × a porcentagem digitada.", "Painéis de vidro de parede cortina contam como iluminação.", "O resultado vai para JA_Iluminacao do ambiente e, na planta ativa, em regiões verdes / vermelhas (rodar de novo troca)." } },
            new ToolInfo { Title = "Trocar Acabamento", Group = Editar, Glyph = "TA", Verb = "Trocar",
                Description = "Troca de uma vez a linha de perfil, a cor, o vidro e/ou as ferragens de várias esquadrias JA (as selecionadas ou as marcadas na lista); cada família é regerada e todas as cópias mudam juntas.",
                Steps = new[] { "Selecione esquadrias (opcional).", "Marque os tipos e escolha o que trocar (o resto fica como está).", "Trocar." },
                Tips = new[] { "Ex.: o cliente mudou o alumínio de preto para branco: marque todos, Cor = Branco." } },
            new ToolInfo { Title = "Lista de Compras", Group = Documentar, Glyph = "LC", Verb = "Gerar lista",
                Description = "Lista de compras da obra em Excel, uma aba por fornecedor: marmoraria, vidraçaria, esquadrias, revestimentos (peças, caixas, rejunte e argamassa), hidráulica, marcenaria, gesso e impermeabilização — com um resumo na primeira aba.",
                Steps = new[] { "Marque os fornecedores.", "Ajuste as perdas.", "Gerar lista: o arquivo vai para a pasta de relatórios." },
                Tips = new[] { "Rode Atualizar Revestimentos antes, se o projeto mudou." } },
            new ToolInfo { Title = "Memorial Descritivo", Group = Documentar, Glyph = "MD", Verb = "Gerar memorial",
                Description = "Rascunho do memorial descritivo em Word, organizado por ambiente: piso (paginação), rodapé, paredes, teto (forro e sanca), esquadrias, peças e impermeabilização; depois os quadros de esquadrias, pedras, vidros e madeira.",
                Steps = new[] { "Preencha os acabamentos dos ambientes (ou use Quadro de Acabamentos do JA Tools).", "Escolha o que entra.", "Gerar memorial e complete no Word." },
                Tips = new[] { "Os dados do projeto (nome, cliente, endereço) vêm das Informações do projeto." } },
            new ToolInfo { Title = "Lista para a Marmoraria", Group = Documentar, Glyph = "LM", Verb = "Exportar",
                Description = "Exporta para Excel (CSV) todas as peças de pedra das bancadas, nichos e chapins JA colocados no projeto: tampo, saia, frontão, tento, pés e cubas esculpidas, com medida de corte, espessura, área e quantidade.",
                Steps = new[] { "Clique em Lista para a Marmoraria.", "O arquivo é salvo na pasta de relatórios; abra no Excel." },
                Tips = new[] { "Peças iguais da mesma bancada ficam numa linha só (coluna “Peças por bancada”).",
                               "Tampos com recorte (cuba, cooktop, pilar) trazem a observação: a medida é a da caixa envolvente e a área já desconta os recortes.",
                               "Chapins são peças corridas: o comprimento está no Quadro de Peças." } },
            new ToolInfo { Title = "Quadro de Peças", Group = Documentar, Glyph = "QP", Verb = "Criar quadro",
                Description = "Cria os quadros de bancadas, nichos, chapins, ralos e painéis de cortina JA: código, descrição, dimensões, material, quantidade, área de pedra (ou do painel) e, nos chapins, o comprimento total.",
                Steps = new[] { "Marque os quadros que quer.", "Criar quadro.", "Arraste a tabela para a prancha." },
                Tips = new[] { "Útil para orçar com a marmoraria: área de pedra por código e metros de chapim.",
                               "Só entram as peças geradas pelo JA Famílias (parâmetro JA_Familia)." } },
            new ToolInfo { Title = "Relatório de Esquadrias", Group = Documentar, Glyph = "RL", Verb = "Gerar relatório",
                Description = "Gera um caderno de especificação (HTML para imprimir ou salvar em PDF) com a elevação de cada esquadria e seus dados técnicos.",
                Steps = new[] { "Confira a pasta.", "Gerar relatório.", "No navegador, Imprimir → Salvar como PDF." },
                Tips = new[] { "Uw é estimado pela EN ISO 10077-1 com valores típicos de perfis e vidros; não substitui o ensaio do fabricante." } },

            // ------------------------------------------------------------------ Ajustes
            new ToolInfo { Title = "Configurações", Group = Ajustes, Glyph = "CF", Verb = "Salvar",
                Description = "Pastas das famílias, da biblioteca e dos relatórios, modelos de família (.rft) e padrões de perfil, cor e vidro.",
                Steps = new[] { "Ajuste as pastas e padrões.", "Salvar." },
                FieldHelp =
                {
                    ["tplGenerico"] = "Deixe vazio para encontrar o Modelo genérico métrico (Metric Generic Model.rft) automaticamente.",
                    ["tplGenericoParede"] = "Deixe vazio para encontrar o Modelo genérico baseado em parede (nichos; a família sai como Componente de gabinete).",
                    ["tplGenericoLinha"] = "Deixe vazio para encontrar o Modelo genérico baseado em linha (chapins; a família sai como Componente de gabinete).",
                    ["tplJanela"] = "Deixe vazio para o JA Famílias encontrar o modelo de janela do Revit (ex.: Metric Window.rft / Janela métrica.rft). Preencha só se usar um modelo próprio.",
                    ["tplPorta"] = "Deixe vazio para encontrar o modelo de porta automaticamente.",
                    ["tplPainelCortina"] = "Deixe vazio para encontrar o modelo de painel de parede cortina (Metric Curtain Wall Panel.rft) automaticamente — usado pelo Painel de Cortina.",
                    ["prefixo"] = "Início do nome das famílias geradas, seguido do código: “JA Esquadria J01”.",
                    ["fabricante"] = "Gravado no parâmetro Fabricante de cada família gerada.",
                    ["parede"] = "Espessura de parede inicial nas novas esquadrias (posições face externa/interna).",
                } },
        };

        private static Dictionary<string, string> IconesFamilias() => new Dictionary<string, string>
        {
            ["Nova Janela"] = "janela",
            ["Nova Porta"] = "porta",
            ["Porta de Elevador"] = "porta",
            ["Parede Cortina"] = "cortina",
            ["Painel de Cortina"] = "painel",
            ["Fachada Paginada"] = "fachadaPaginada",
            ["Cortes das Esquadrias"] = "corteEsquadria",
            ["Conferir Planta"] = "conferir",
            ["Novo Perfil"] = "perfil",
            ["Trocar Montantes"] = "trocar",
            ["Biblioteca"] = "biblioteca",
            ["Nova Bancada"] = "bancada",
            ["Bancada no Local"] = "bancadalocal",
            ["Novo Nicho"] = "nicho",
            ["Nicho no Local"] = "nicho",
            ["Paginação de Piso e Parede"] = "paginacao",
            ["Forro e Sanca"] = "forroSanca",
            ["Painel Ripado"] = "ripado",
            ["Pergolado"] = "pergolado",
            ["Impermeabilização"] = "gotaImperm",
            ["Atualizar Revestimentos"] = "atualizar",
            ["Lista de Compras"] = "compras",
            ["Memorial Descritivo"] = "relatorio",
            ["Rodapé"] = "rodapePedra",
            ["Rodapé por Ambiente"] = "rodapePedra",
            ["Rodapé no Local"] = "rodapePedra",
            ["Escada Revestida"] = "escadaRevestida",
            ["Guarda-corpo de Vidro"] = "guardacorpo",
            ["Box de Banheiro"] = "box",
            ["Iluminação e Ventilação"] = "sol",
            ["Trocar Acabamento"] = "trocar",
            ["Novo Chapim"] = "chapim",
            ["Novo Ralo"] = "ralo",
            ["Ralo no Local"] = "ralo",
            ["Editar Família"] = "editar",
            ["Soleiras e Peitoris"] = "soleira",
            ["Substituir pelos do JA"] = "substituir",
            ["Quadro de Esquadrias"] = "tabela",
            ["Quadro de Peças"] = "tabela",
            ["Lista para a Marmoraria"] = "tabela",
            ["Orçamento"] = "relatorio",
            ["Relatório de Esquadrias"] = "relatorio",
            ["Configurações"] = "config",
            ["Central de Ajuda"] = "ajuda",
        };
    }
}
