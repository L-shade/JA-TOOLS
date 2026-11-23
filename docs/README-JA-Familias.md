# JA Famílias (Revit)

> Antes chamado **JA Esquadrias**. O nome visível mudou; a pasta do add-in, a DLL
> (`JAEsquadrias.Revit.dll`), o namespace e o AddInId continuam os mesmos, então a atualização
> substitui a versão anterior.

Configuradores de janelas, portas, painéis de parede cortina, bancadas, nichos e chapins para o Revit 2024–2027, no mesmo padrão visual do **JA Tools**
(mesmo tema `JaTheme.xaml`, cabeçalho com passos, cartões, pílulas, janela de resultado e Central de
Ajuda offline). Inspirado nos plugins de fabricantes do tipo “crie sua própria janela”: você monta a
esquadria num configurador visual e o plugin gera uma **família Revit de verdade**, carrega no projeto
e deixa você clicar nas paredes.

## Aba "JA Famílias"

| Painel | Botões |
|---|---|
| **Esquadrias** | **Nova Janela**, **Nova Porta**, **Parede Cortina**, **Painel de Cortina** (cassete ACM, sólido, vidro, sanduíche, veneziana, brise, emoldurado) |
| **Perfis** | **Novo Perfil** (montantes de parede cortina), **Trocar Montantes** (em planta, corte ou 3D) |
| **Pedras e nichos** | **Nova Bancada**, **Novo Nicho**, **Novo Chapim**, **Bancada no Local** (contorno desenhado na planta, áreas seca e molhada), **Soleiras e Peitoris** (em todas as portas e janelas) |
| **Ralos** | **Novo Ralo** (linear, quadrado, redondo, caixa sifonada, hemisférico — Conexões de tubo com conectores) |
| **Gerenciar** | **Editar Família** (reabre qualquer família JA; todas as cópias atualizam), Biblioteca, **Substituir pelo JA** (troca portas, janelas e bancadas de outras famílias), **Conferir Planta** (mede o desenho 2D das esquadrias) |
| **Revestimentos e Vidros** | Paginação, Rodapé de Pedra, Rodapé por Ambiente, Rodapé no Local, Escada Revestida, Forro e Sanca, Impermeabilização, Atualizar Revestimentos, Guarda-corpo, Box de Banheiro, Painel Ripado, Pergolado |
| **Documentar** | Orçamento, Lista para a Marmoraria, Lista de Compras, Memorial Descritivo, Quadro de Esquadrias, Quadro de Peças (tabelas no Revit, com painéis de cortina), Relatório (caderno HTML/PDF com elevações) |
| **Ajuda** | Configurações, Central de Ajuda |

## Novidades 1.29.0

- **Rodapé preso na parede**: cada trecho fica alinhado e com cadeado na face da parede em que foi criado (Rodapé, Rodapé por Ambiente, Rodapé no Local e Atualizar Revestimentos); movendo a parede, o rodapé vai junto. Opção "Prender o rodapé na parede", ligada por padrão.

## Novidades 1.28.0

- **Rodapés testados e corrigidos** (casos em `docs/testes/rodape`): cantos internos topo a topo (sem pedra dobrada nem metros a mais), quinas externas cobertas uma vez, nada avança sobre parede revestida ou de vidro, portas, janelas e vãos descontados também em paredes cebola, retorno dos vãos cobrindo a quina do batente e indo até o meio da parede, paredes curvas sem buracos, e o Acabamento de base aceita "10 cm", "altura 7 cm", "h.10cm".

## Novidades 1.27.0

- **Rodapé no Local**: desenhe o rodapé onde ele vai (ferramentas do Revit, linhas, bordas ou cliques com orto). Cada trecho encosta na face da parede, com a pedra para o lado do desenho, os cantos se encontram e os externos fecham, com os mesmos descontos do Rodapé (portas, janelas, vãos, móveis, cortina, revestidas).

## Novidades 1.26.0

- **Rodapé por Ambiente**: vários ambientes de uma vez, escolhidos numa lista, cada um com o seu rodapé — do Acabamento de base do ambiente (ex.: “Granito branco h 100 mm”), do nome (Rodapé B e C por palavras, ex.: cozinha, serviço) ou o Rodapé A; banheiros, lavabos etc. ficam sem rodapé. Mesmos descontos do Rodapé; resumo por tipo e por ambiente, compra e Excel.

## Novidades 1.25.0

- **Rodapé avançado**: descontos de portas (com folga), janelas até o piso, vãos sem porta (com retorno nas laterais, opcional), paredes cortina, paredes revestidas (tipo ou camada do lado do ambiente com cerâmica, porcelanato…), móveis encostados e trechos curtos; cantos externos fechados; ou rodapé num trecho desenhado. Resumo por ambiente (paredes, cada desconto, líquido), compra com perda e peças, e planilha Excel.

## Novidades 1.24.0

- **Bancadas sempre como Componentes de gabinete** (Nova Bancada e Bancada no Local). Bancadas de versões antigas (Modelo genérico) viram Componente de gabinete ao Editar Família: as cópias são trocadas no mesmo lugar.
- **Guarda-corpo sempre como Guarda-corpo do Revit** (categoria Guarda-corpos): placas de vidro como família de Balaústres, corrimão superior do tipo, perfil U como travessa. Placas iguais por trecho (um guarda-corpo por sequência de trechos iguais) ou módulo fixo (um guarda-corpo contínuo). Editar Família refaz no mesmo caminho; Orçamento e Lista de Compras contam os metros.

## Novidades 1.23.0

- **Uma janela “como desenhar” em todas as ferramentas que desenham famílias** (Ralo no Croqui, Bancada no Local, Guarda-corpo, Box de Banheiro, Painel Ripado, Pergolado, Nicho no Local): ferramentas de Linha de detalhe do Revit (linha, retângulo, círculo, polígono, arco, selecionar linhas…), selecionar linhas já desenhadas, selecionar bordas de elementos, ou clicar os pontos.
- **Orto** nos cliques: cada ponto na horizontal ou na vertical do anterior, com ângulo ajustável (paredes inclinadas); Shift no clique inverte o orto só naquele ponto. Vale em planta e em elevação.
- As linhas desenhadas para o croqui são apagadas depois que a família é colocada (opcional).

## Novidades 1.22.0

- **Ralo no Croqui** com as ferramentas do Revit: escolha “Com as ferramentas do Revit”, desenhe com Linha de detalhe (linha, retângulo, círculo, polígono, arco, selecionar linhas…), termine com Esc e clique de novo no botão. Linha aberta = eixo do ralo linear; contorno fechado (retângulo, L, U…) = formato e largura, com o eixo no meio da faixa; quadrado = ralo quadrado; círculo = ralo redondo. As linhas são apagadas depois (opcional). Também: **selecionar linhas** já desenhadas (detalhe ou modelo) ou **bordas** de pisos, paredes, forros e bancadas, com a mesma leitura.

## Novidades 1.21.0 (o que ficou de fora da 1.20)

- **Atualizar Revestimentos** aceita o que veio de versões antigas: rodapés sem configuração ganham o ambiente e as configurações (altura, espessura e pedra da família; o resto, os últimos valores do Rodapé); paginações antigas recalculam rejunte e argamassa; Iluminação sem configuração guardada usa os últimos valores da janela da ferramenta.
- Configurações guardadas em versões antigas abrem com os padrões novos nos campos que não existiam (antes vinham zerados).

## Novidades 1.20.0 (ferramentas novas)

- **Forro e Sanca**: forro de gesso por ambiente com tabica e sanca fechada ou aberta (faixa rebaixada, espelho e abertura para a luz); escreve o acabamento do teto.
- **Painel Ripado** e **Pergolado** (peças de madeira, tipo novo): painel de ripas na face da parede entre dois pontos; pergolado em três cliques com pilares, vigas, caibros e beiral; lista de corte e volume de madeira. Editar Família reabre os dois.
- **Impermeabilização**: piso, subida nas paredes (menos portas) e paredes do box por área molhada, com o consumo do produto, em Excel.
- **Paginação** calcula rejunte e argamassa colante (sacos de 20 kg).
- **Atualizar Revestimentos**: refaz de uma vez paginações, rodapés, forros e as cores de iluminação depois que o projeto muda (um só Desfazer).
- **Lista de Compras**: Excel com resumo e uma aba por fornecedor (marmoraria, vidraçaria, esquadrias, revestimentos, hidráulica, madeira, gesso, impermeabilização).
- **Memorial Descritivo**: rascunho em Word organizado por ambiente, com os quadros de esquadrias e peças.

## Novidades 1.19.0 (melhorias no que já existia)

- **Paginação refazível**: cada ambiente / face vira um grupo “JA Paginação …” com as configurações guardadas; selecione o grupo e rode de novo para refazer ou mudar; rodar num ambiente já paginado substitui a anterior. Os ralos do JA Famílias (também os do croqui) ficam fora da área. Campo de revestimento para o orçamento.
- **Rodapé**: substitui o rodapé antigo do ambiente, para em bancadas/armários/móveis encostados e escreve o Acabamento de base do ambiente (coluna Rodapé do Quadro de Acabamentos).
- **Escada Revestida**: patamares retangulares também (JA Patamar, PT01…) e conferência da fórmula de Blondel e da NBR 9050 no resultado.
- **Box em L**: três cliques (parede do lado fixo, canto, parede do lado da porta).
- **Guarda-corpo**: placas de vidro moduladas com folga (perfis e corrimão contínuos) e caminho por linhas/bordas selecionadas, retas ou curvas.
- **Ralo no Croqui**: modo “Junto de uma parede” (parede + início + fim, com afastamento até a borda do ralo) e campo de caimento do piso (vai para a descrição).
- **Orçamento em Excel**: preço por pedra, ralos (unidade / metro linear), guarda-corpo por metro, box por unidade e as paginações (m² a comprar por revestimento).
- **Plano de corte**: opção de respeitar o veio (não girar) e desenho das peças com recorte no formato real.
- **Iluminação e Ventilação**: painéis de vidro de parede cortina contam; esquadrias em parede cortina acham os ambientes pelos dois lados; resultado no parâmetro JA_Iluminacao do ambiente e regiões verdes / vermelhas na planta ativa.

## Novidades 1.18.0

- **Ralo no Croqui** (painel Ralos): desenhe o eixo do ralo linear na planta — clicando os pontos ou selecionando linhas de detalhe/modelo antes — e o ralo é gerado seguindo o traçado (L, U, vários trechos), com cantos em meia-esquadria (moldura, grelha, corpo), saída no meio, no início ou no fim do traçado, e colocado no lugar. A janela do ralo é a mesma do Novo Ralo; Editar Família reabre com o traçado.

## Novidades 1.17.0

Painel novo **Revestimentos e Vidros**:
- **Paginação de Piso e Parede**: em planta, no piso dos ambientes selecionados; em elevação/corte, na face de parede clicada (com os vãos). Peça, junta, partida (centralizada, canto ou ponto clicado), direção (parede mais longa, eixos ou 45°). Desenha as juntas (estilo de linha “JA Paginação”), conta peças inteiras e recortes, m² a comprar com a perda e caixas.
- **Rodapé de Pedra**: pelo contorno dos ambientes, só nas paredes, aberto nas portas; família JA baseada em linha (uso novo da peça de pedra, código RD01…).
- **Escada Revestida**: piso e espelho de pedra em cada degrau das escadas do Revit (lances retos), um tipo JA Degrau por tamanho (DG01…), com bocel.
- **Guarda-corpo de Vidro**: perfil U ou fixação lateral, corrimão opcional; clique os pontos na planta (família baseada em linha, GC01…). Novo tipo de peça **Vidro**.
- **Box de Banheiro**: de correr, de abrir ou fixo, na largura exata clicada entre as paredes (BX01…).

Também:
- **Iluminação e Ventilação** (Documentar): confere cada ambiente contra as frações do código de obras (prolongada / transitória / isento pelo nome), CSV e seleção dos que não atendem.
- **Trocar Acabamento** (Gerenciar): perfil, cor, vidro e ferragens de várias esquadrias de uma vez.
- **Lista para a Marmoraria**: peças corridas (chapins, rodapés) agora com os metros e os trechos de cada uma, e entram no plano de corte.
- Quadro de Peças: coluna de comprimento total para soleiras/rodapés/degraus e para guarda-corpos.

## Novidades 1.16.1

- Correção de compilação: `RenderOptions` ambíguo em DetalheCommands.cs.

## Novidades 1.16.0

- **Nicho no Local** (Pedras e Nichos): clique dois cantos opostos do vão acabado na face da parede (elevação, corte ou 3D); o nicho sai com essa largura, altura e base e é colocado ali, do lado clicado, centrado nos cliques. A profundidade fica limitada pela espessura da parede.
- **Chanfro em meia-esquadria também no tento**: as cunhas do chanfro do tento se encontram a 45° nos cantos e param na face do frontão.

## Novidades 1.15.0

- **Chanfro em meia-esquadria** (bancadas com aresta chanfrada): as cunhas do chanfro de duas bordas livres se encontram a 45° no canto; junto de parede, terminam ao longo da parede. Feitas como mescla no Revit; se a mescla falhar, volta a cunha reta (aviso no registro).
- **Plano de corte** (Lista para a Marmoraria): distribui as peças retangulares em chapas (comprimento × largura da chapa, espessura da serra), por material, e gera um HTML para imprimir com o desenho de cada chapa, aproveitamento e a lista de peças; peças maiores que a chapa vêm listadas à parte.
- **Bancada no Local › Usar como**: Bancada, Soleira / peça de piso (topo na espessura da pedra, sem saia/frontão/cubas) ou Prateleira / tampo simples — o mesmo contorno no local para soleiras, degraus, tampos de jardineira e prateleiras.
- **Contramarco** (janelas e portas): requadro em volta do vão (largura e profundidade do perfil), cortando a parede no lugar dele.
- **Pingadeira**: sulco de 8 mm por baixo da projeção do peitoril.
- **Elevação no Quadro de Esquadrias**: a elevação de cada tipo vira a Imagem do tipo e aparece na coluna “Elevação” (na prancha).
- **Cortes das Esquadrias** (Documentar): corte vertical e horizontal de cada tipo escolhido, recortados em volta da esquadria, em escala de detalhe.
- **Fachada Paginada** (Esquadrias): parede cortina na face externa das paredes selecionadas, dividida em módulos, e o Painel de Cortina nos painéis para virarem ACM cassete, brise vertical, veneziana ou painel liso.
- **Aviso de versão**: em Configurações, “Pasta de atualizações do escritório”; ao abrir o Revit, avisa quando há versão nova lá (veja Pacote do escritório no README do JA Tools).

## Novidades 1.14.0

- **Orçamento** (painel Documentar): estimativa em CSV das famílias JA colocadas — janelas e portas por m² de vão, pedra por m² (chapins pelo comprimento), cubas por unidade — com os preços digitados na janela (ficam salvos) e subtotais por grupo.
- **Lista para a Marmoraria › emendas**: informe o comprimento máximo de uma peça (chapa); peças mais compridas saem com "N partes de até X mm (N-1 emendas)" na observação.
- **Ferragens**: campo "Especificação das ferragens" no configurador (aba de acabamentos) → parâmetro JA_Ferragens → coluna "Ferragens" no Quadro de Esquadrias (ao lado do Vidro).
- **Bancada no Local a partir do ambiente**: selecione o ambiente (ou a etiqueta dele) antes do comando e o contorno do tampo sai do contorno do ambiente (faces de acabamento), para tampos de parede a parede.

## Novidades 1.13.0

- **Lista para a Marmoraria** (painel Documentar): CSV para Excel com todas as peças de pedra das bancadas, nichos e chapins JA colocados no projeto — peça, quantidade, medida de corte, espessura, área e material; peças com recorte vêm marcadas.
- **Bancada no Local › Furos**: clique no projeto o centro de cada furo (torneira, dosador, passa-cabo); o diâmetro de cada um é ajustado na janela.

## Novidades 1.12.2

**Ilha: saia dos dois lados** (Nova Bancada › Medidas › Encostos › “Fundo livre — ilha”)
- O fundo deixa de ser parede: recebe saia/tira, tento e chanfro como a frente e não leva frontão (em L e U, também os lados de parede dos retornos). Os cantos se unem em meia-esquadria com as pontas.
- “Por trecho e lado” ganhou “Fundo (livre) com saia” para tirar só a do fundo.
- Modelo novo “Ilha com saia dos dois lados”; o modelo “Ilha com pés” passou a ter o fundo livre.
- Bancada no Local: já era possível — clique na borda e escolha “Livre com saia”.

## Novidades 1.12.1

**Frontões, saias, tentos e pés unidos nos cantos** (todas as bancadas: no local, reta, L e U)
- Antes cada peça era um retângulo por borda: nos cantos sobrava um vão (cantos para fora, como os de pilar) ou as peças se sobrepunham, e em ângulos fora de 90° o defeito aumentava.
- Agora as peças vizinhas se encontram em **meia-esquadria** em qualquer ângulo; onde a vizinha não tem a peça, a ponta acompanha a borda vizinha (fica rente ao contorno); tento e borda de contenção param na face do frontão; a saia para na face do pé de pedra (a borda com pé não leva saia por baixo).
- Para atualizar uma bancada já feita: selecione-a, Editar Família e Atualizar.

## Novidades 1.12.0

**Bancada no Local** (painel Pedras e Nichos) — bancada de qualquer formato modelada onde ela fica
- Na planta baixa, clique os cantos do tampo em sequência, contornando paredes, pilares e recortes (snaps do Revit; Esc ou um clique no 1º canto fecha). Ou desenhe o contorno com linhas de detalhe/modelo, selecione-as e clique no botão (arcos viram trechos retos curtos).
- As bordas que encostam em **parede ou pilar** (estrutural ou arquitetônico) são detectadas e já vêm como “Parede com frontão”; as demais, “Livre com saia”. Na janela, clique numa borda da planta para mudar: livre com/sem saia, parede com/sem frontão, pé de pedra (Ctrl+clique seleciona várias).
- A janela mostra a planta como no projeto, com paredes e pilares em cinza, e uma vista 3D. Botões **Modelar no projeto** fecham a janela para clicar na planta e voltam com o resultado: Dividir áreas, Posicionar cuba, Posicionar cooktop, Redesenhar contorno.
- Cubas (embutir, sobrepor, esculpida, apoio, dupla) e cooktop: clique o centro no projeto; vêm alinhados com a parede mais próxima, com ângulo ajustável.
- A família (JA Bancada BC02…, Modelo genérico) nasce com o contorno exato e é colocada onde foi desenhada. Selecionar a bancada e usar Editar Família (ou Bancada no Local) reabre o editor.

**Áreas seca e molhada**
- Bancada no Local: “Dividir áreas” — uma linha de uma borda à outra (dois cliques, mesmo fora do tampo, ou vários para uma linha quebrada) separa o tampo; clique numa área na planta da janela para marcá-la **molhada** (azul) ou **seca**. Várias divisões são aceitas.
- Nova Bancada (reta, L, U): aba Medidas › “Áreas seca e molhada” — onde a área molhada começa e termina no trecho principal.
- **Rebaixo** (a área molhada fica mais baixa; 0 = mesmo nível, só dividida) e **borda de contenção** (as bordas livres da área molhada voltam ao nível da seca). Frontão, saia, tento, cubas e chanfros acompanham o nível de cada área; rebaixo maior que a pedra ganha um espelho no degrau.
- Resumo e Quadro de Peças: “Área molhada” (parâmetro JA_AreaMolhada) além da área de pedra; a descrição cita o rebaixo.

## Novidades 1.11.0

**Chapim em paredes curvas**
- Antes o chapim de uma parede em arco era aplicado na corda (reta entre as pontas). Agora, em paredes cujo eixo é um arco de círculo, o chapim é uma **revolução do mesmo perfil** (abas, pingadeiras, caimento) em torno do centro do arco, no eixo do muro e no topo da parede, com a aba externa para o lado de fora da parede.
- Uma família por espessura de muro, raio (no eixo) e lado: “JA Chapim CH01-R3575” (ou CH01-200-R3575 para outra espessura; “i” no fim quando o exterior da parede fica para dentro do arco). O **Ângulo do arco** é parâmetro de instância: todos os trechos de mesmo raio usam a mesma família. Se o Revit não deixar ligar o ângulo, sai uma família por ângulo (…-A90).
- JA_Comprimento = raio × ângulo (comprimento no eixo) para o Quadro de Peças.
- Editar Família num chapim curvo: o configurador mostra o perfil; raio, lado e ângulo são mantidos. Editar o CH01 também refaz as variações curvas.
- Curvas que não são arco (elipse, spline) continuam na corda, com aviso. Juntas das peças não são desenhadas no chapim curvo.

## Novidades 1.10.0

**Portas e janelas de correr: como as folhas se movem** (aba Divisões, módulo de correr › Movimento)
- **Todas correm** (como antes).
- **Para os dois lados — abertura central**: metade das folhas de cada lado; as do meio se encontram no centro e correm para fora. Com 4 folhas as das pontas ficam fixas (2 trilhos); com 6, duas recolhem de cada lado sobre a fixa (3 trilhos). Com 2 folhas elas correm para fora do vão (sobre a parede ou embutidas).
- **Recolhem para um lado**: uma folha fixa no lado escolhido (esquerda/direita, vista de fora) e cada folha móvel no seu trilho, todas recolhendo sobre a fixa (porta telescópica). 2 a 6 folhas.
- Folha fixa leva o “F” na elevação, sem seta nem puxador; puxador das móveis no lado que fecha; setas na planta só nas móveis; área de ventilação pela parte que abre; descrição/quadro com o modo (“correr 4 folhas (abertura central, pontas fixas)”).
- Modelos novos: porta Correr 4 e 6 folhas com abertura central, porta Correr 3 e 4 folhas recolhendo para um lado, janela Correr 4 folhas com abertura central.
- Bancadas: profundidade por trecho do L/U, saia/frontão por trecho e lado e cuba dupla já estão na 1.8.0 (abas Medidas, Bordas › “Por trecho e lado” e Cubas).

## Novidades 1.9.0

**Painel de Cortina** (painel Esquadrias) — painéis de parede cortina que não são porta nem janela
- Tipos: **cassete ACM** (face + retornos dobrados no perímetro, junta aberta), **sólido**, **vidro** (spandrel), **sanduíche** (duas chapas + núcleo), **veneziana** (aletas horizontais inclinadas), **brise vertical** e **emoldurado** (moldura + vidro ou painel cego, com travessas).
- Selecione painéis (ou a parede cortina inteira) e clique no botão: o configurador abre no tamanho dos painéis; cada tamanho vira uma família “JA Painel PN01 1250x3000”, todas com o mesmo código (marca de tipo PN01), e os painéis são trocados.
- Prévia ao vivo: frente vista de fora + corte vertical (planta, no brise). Medidas, deslocamento em relação à grade, junta, chapa/retorno, aletas (largura, espessura, passo, ângulo), moldura e materiais (painel com cor, vidro, núcleo, moldura).
- Família do modelo do Revit “Curtain Wall Panel” (categoria Painéis de parede cortina); pode ser indicado em Configurações. Aletas só nos detalhes médio e fino (no baixo, uma chapa fina).
- Editar Família num painel JA (ou escolher o código no topo do configurador) refaz todos os tamanhos daquele código.
- Quadro de Peças: opção “Painéis de cortina” (código, dimensões, material, quantidade, área).

**Planta das esquadrias: conferência** (pendência da folha/marco “soltos” na planta)
- Toda janela/porta gerada é **medida no editor de famílias**: cada linha da planta paralela à parede (marco, folhas, vidro, pedra, verga) é comparada com o desenho do configurador — na parede do modelo e em mais duas espessuras e revestimentos (alterações desfeitas). Linha fora do lugar vira aviso no fim da geração e os detalhes vão para o registro.
- **Conferir Planta** (painel Gerenciar): na planta ativa, mede as esquadrias JA selecionadas (ou todas da vista) com a parede e os revestimentos reais, lista o que está fora do lugar em mm por código (considera cópias espelhadas/viradas) e oferece **Gerar de novo e conferir** — famílias feitas por versões antigas são a causa mais comum.

## Novidades 1.8.0

**Bancadas**
- Profundidade própria para cada trecho do L e do U (B e C; vazio = igual à de A). Aba Medidas.
- Saia/tira e frontão por trecho e por lado: card “Por trecho e lado” na aba Bordas (A, B, C, ponta esquerda, ponta direita). O frontão das paredes do L/U não depende mais de “frontão nas laterais”.
- Cuba dupla: septo (mm) e proporção da bacia da esquerda (%); uma borda de sobrepor e um furo de torneira. Modelos novos: Cozinha com cuba dupla, Cozinha em L com península rasa.

**Soleiras e Peitoris** (painel Pedras e nichos)
- Soleira ou filete em todas as portas e peitoril em todas as janelas — seleção, vista ativa ou projeto inteiro.
- Esquadrias JA: a opção é ligada na própria família (o tipo é regerado; todas as cópias recebem).
- Portas e janelas de outros fabricantes: peça separada “JA Soleira/Filete/Peitoril SL01 CxLxE” (Componentes de gabinete, entra no Quadro de Peças), uma família por tamanho, no centro da parede (considera a linha de localização e as camadas), topo na base da porta/no peitoril da janela, Y para o exterior. Peitoril com projeção, abas e pingadeira.
- Rodar de novo não duplica (vãos com peça a até 15 cm são pulados); “Refazer” apaga e recoloca.
- Novo tipo de peça Pedra (TipoPeca.Pedra = 4, prefixo SL) no configurador de peças: abas Medidas e Materiais.

**Substituir pelo JA** (painel Editar)
- Portas e janelas (fora de parede cortina): o configurador abre no tamanho mais comum, com a espessura da parede e o peitoril do primeiro; gera um tipo JA por tamanho (L × A) e troca o tipo de cada elemento (ChangeTypeId) — mesma parede, posição, peitoril, etiquetas e cotas.
- Bancadas (modelos genéricos, gabinetes, mobiliário, peças sanitárias): mede a caixa da geometria no sistema da família, abre o configurador de bancadas com o tamanho da primeira, gera um tipo por tamanho (comprimento × profundidade × altura do topo) e recria no mesmo lugar e rotação (frente = −Y da família antiga; instâncias espelhadas tratadas), apagando a antiga.
- Elementos já do JA são ignorados (use Editar Família). Nichos e ralos de outras famílias não são trocados.

## Ralos (1.7.0)

`Core/Pecas/RaloConfig.cs` (TipoPeca.Ralo, prefixo RL, testado no harness `/home/claude/hr`):
linear (comprimento 200–6000 mm × largura 35–400), quadrado, redondo, caixa sifonada (0–4 entradas
laterais) e hemisférico (domo para laje/calha). Origem = topo da grelha no piso acabado; corpo e tubos
abaixo. Grelha: fendas, furos, quadriculada (aberturas só em detalhe fino; baixo/médio = grelha lisa)
ou revestível (bandeja com rebaixo para o piso, fresta de 4 mm). Saída DN40–DN150 vertical ou
horizontal (linear: centro, ponta esquerda/direita com distância da ponta).

A família é gerada do modelo Genérico e passa para **Conexões de tubo** (`OST_PipeFitting`, tipo de
peça Multi Port). Cada tubo (`Peca.Conector`) é extrudado como círculo verdadeiro (dois arcos) e
recebe `ConnectorElement.CreatePipeConnector` na face da ponta (diâmetro, sentido do fluxo: saída
Out, entradas In; sistema Sanitário ou, para pluvial, Storm/OtherPipe se existir). Materiais:
grelha, corpo, revestimento. Entra no Quadro de Peças (sem área de pedra).

## Perfis de parede cortina (1.6.0)

- **Novo Perfil** (`Commands/PerfilCommands.cs`, `UI/PerfilWindow.cs`): seção desenhada ao vivo
  (exterior para cima, plano do vidro e linha da grade). Formas (`Core/Perfis/PerfilConfig.cs`):
  retangular (cantos retos, chanfrados ou boleados, só na face externa opcional), tubo retangular,
  tubo redondo, I, T, U, L, com tampa de pressão (corpo + haste no vão do vidro + tampa) e canto
  (V = meia esquadria, L). `Engine/PerfilGenerator.cs`: família de perfil de montante (modelo
  “Profile-Mullion”, linhas de detalhe na vista de planta, Y espelhado porque o modelo põe o
  exterior embaixo) + tipo de montante duplicado do montante retangular com Perfil = a família;
  cantos = tipo de canto V/L do sistema com pernas e espessura. A configuração fica no JA_Config
  da família de perfil (editar = escolher o perfil no topo da janela).
- **Trocar Montantes**: montantes (ou paredes cortina inteiras) escolhidos em qualquer vista →
  novo tipo; alcance só os escolhidos / mesmo tipo nas mesmas paredes / mesmo tipo no projeto;
  direção todos / verticais / horizontais; libera montantes fixados; “Novo perfil…” cria e aplica.

## Bancadas, nichos e chapins

Mesmo configurador (modelos · prévia · abas · rodapé com números e avisos), com prévia em **3D**,
**planta** e **frente/elevação/corte** desenhada a partir das mesmas peças que vão para a família
(renderizador próprio com z-buffer, `UI/PecaRenderer.cs`). Geometria em `Core/Pecas/*`, gerador em
`Engine/PecaGenerator.cs`.

- **Bancada** — Modelo genérico solto (origem no canto do fundo à esquerda, no piso). Reta, em L
  (retorno à esquerda/direita) ou em U; profundidade, espessura, altura; pontas encostadas; borda
  frontal reta, **saia** ou **tira americana**; **tento**; **frontão** (também nas laterais);
  **pés** de pedra; **cubas** de embutir, sobrepor, esculpida (na própria pedra) e de apoio, com
  posição, recuo, raio, profundidade e furo de torneira; **cooktop**. Materiais da pedra e da cuba.
- **Nicho** — **Componente de gabinete** baseado em parede (gerado do modelo genérico baseado em parede, com a categoria trocada): o **vazio corta a parede** (corte feito na família; se falhar, o projeto corta cada parede com o vazio). Medidas internas
  acabadas, altura da base (Elevação padrão), prateleiras e divisórias, revestimento interno,
  base de pedra com projeção, moldura na face da parede, contorno tracejado em planta.
- **Chapim** — **Componente de gabinete** baseado em linha (acompanha o comprimento). Espessura do muro, abas
  externa/interna, espessura, caimento plano/um lado/duas águas, pingadeiras, juntas em planta.
  **Gerar e aplicar nos muros**: um chapim por parede selecionada, na linha de centro, no topo, com
  a aba externa para o lado externo; paredes de outra espessura ganham um tipo próprio (`CH01-200`).
- Parâmetros: `JA_Familia`, `JA_Material`, `JA_Dimensoes`, `JA_AreaPedra` (chapim: por metro),
  `JA_Comprimento` (chapim, instância = comprimento da linha) e `JA_Config` (oculto).
  O **Quadro de Peças** cria um quadro por tipo com código, descrição, dimensões, material,
  quantidade, área de pedra e metros de chapim.

## Configurador

- **Modelos** (esquerda): janelas de correr 2/3/4 folhas, com veneziana, com bandeira, maxim-ar,
  maxim-ar duplo, fixo + maxim-ar, basculante, abrir 1/2 folhas, oscilo-batente, fixo·abrir·fixo,
  pivotante, fixa, veneziana; portas de abrir (cega, vidro, 2 folhas, com bandeira, veneziana),
  de correr 2/3/4 folhas, com bandeira, pivotante. Também **Biblioteca** (suas composições, `.json`)
  e **No projeto** (esquadrias JA já carregadas).
- **Prévia** (centro): elevação vista de fora, com parede, cotas, linhas de abertura e o módulo
  selecionado. **Clique num módulo** para escolher a abertura dele — é a *construção livre*.
- **Abas** (direita):
  - *Medidas*: código (vira Marca de tipo e nome da família), descrição, largura, altura, peitoril,
    posição na parede (face externa / centro / face interna), recuo, espessura da parede.
  - *Divisões*: linhas (de cima para baixo) com altura fixa ou automática, nº de módulos por linha,
    e o módulo selecionado: largura, abertura (fixo, abrir esq./dir., abrir 2 folhas, maxim-ar,
    basculante, tombar, oscilo-batente esq./dir., pivotante, correr 2/3/4/6 folhas), preenchimento
    (vidro, veneziana, painel cego), folhas externas em veneziana, abre para fora.
  - *Perfil e vidro*: linha de perfis (alumínio leve/médio/pesado, RPT, PVC, madeira, aço) com as
    medidas editáveis, cor (paleta + hex), vidro (incolor, temperado, laminado, verde, fumê,
    refletivo, mini-boreal, insulado, insulado low-e).
  - *Detalhes*: peitoril/pingadeira, soleira, puxadores, linhas de abertura em elevação, arco em
    planta (portas), nível Baixo simplificado; e os **dados BIM** que serão gravados.
- **Rodapé**: Uw estimado, área de iluminação, área de ventilação, nº de folhas, avisos;
  *Salvar na biblioteca*, *Só gerar a família*, **Gerar e posicionar** (Ctrl+Enter).

## Padrão de desenho (relação de esquadrias do escritório)

- **Elevação (vista externa)**: linhas de abertura saindo dos cantos do lado da dobradiça até o meio do
  lado que abre (maxim-ar: dos cantos de cima ao meio de baixo), todas contínuas; correr com triângulos
  ▷ ◁ (folhas do meio ▷◁); vidro fixo marcado com F; puxadores das portas; cotas com pontos.
  Sem hachuras/degradês de superfície.
- **Planta**: folha de porta aberta com arco tracejado; correr com folhas sobrepostas nos trilhos e
  setas ao lado das folhas (portas); fresta inferior configurável nas portas.

## Planta (2D)

As famílias usam uma representação em planta de bloco de CAD, feita com linhas simbólicas
(os sólidos ficam ocultos nas plantas e aparecem em 3D, elevações e cortes):

- **Portas**: marco na espessura da parede com batente, guarnições (alizares) nas duas faces,
  folha **aberta a 90°** (retângulo na espessura da folha) com o arco até o batente oposto — a folha
  fechada não aparece —, e a soleira/filete. Duas folhas, pivotante e abrir para fora também.
- **Janelas** (corte a 1,20 m): marco, folhas nos trilhos com a linha do vidro, montantes, peitoril com
  abas e as bordas do vão sobre a pedra.

**Aba Planta 2D**: ângulo de abertura (90°, 60°, 45°, 30° ou outro), folha em retângulo ou linha,
folha começando na face do marco (estrutura mais externa) ou no eixo da dobradiça, arco / arco
tracejado / linha reta / sem arco, folha fechada tracejada, projeção da verga, e liga/desliga de marco,
folhas, guarnições e soleira/peitoril. Na família, cada grupo vira um parâmetro **Sim/Não de instância**
(`Planta - folha aberta`, `Planta - arco de abertura`, `Planta - guarnições`…, e `Elevação - linhas de
abertura`), para mudar porta a porta no Revit sem gerar de novo.

**Revestimentos** (aba Medidas): espessura do revestimento externo e interno além da parede do Revit.
O marco, as guarnições, o peitoril e a soleira vão até as faces acabadas.

**Soleira / filete** (portas, centralizada ou rente ao lado externo/interno da esquadria) e **peitoril** (janelas): código (ex.: SL01, PE01), material (lista de
materiais do projeto ou nome livre — se existir no projeto, é ele que vale), cor, comprimento, largura e
espessura. Gravados em `JA_SoleiraCodigo`, `JA_SoleiraMaterial`, `JA_SoleiraDimensoes` e listados no
**Quadro de Soleiras e Peitoris** (agrupado por código, com quantidade).

## A família gerada

`Engine/FamilyGenerator.cs`:

1. Novo documento a partir do modelo de família de janela/porta do Revit (encontrado
   automaticamente em qualquer idioma — `Metric Window.rft`, `Janela métrica.rft`… — ou o indicado em
   Configurações). O modelo já traz a parede hospedeira e o recorte que segue Largura/Altura.
2. Largura, Altura e Altura do peitoril padrão; um tipo com o nome do código (J01, P01…).
3. Cada sólido do `Core/Layout.cs` vira uma extrusão: marco, montantes/travessas, folhas, vidros,
   venezianas (lâminas), painéis, puxadores, peitoril, soleira. Materiais por parâmetro
   (*Material do perfil / vidro / painel / peitoril*) e visibilidade por nível de detalhe
   (Baixo: marco e vidro · Médio: + folhas e lâminas · Alto: + puxadores).
4. Linhas simbólicas de abertura na elevação (tracejado = abre para dentro) e arco em planta nas
   portas, em subcategorias próprias (`JA Abertura`, `JA Abertura (para dentro)`).
5. Parâmetros compartilhados (GUIDs fixos, entram em tabelas e etiquetas): `JA_Linha`, `JA_Cor`,
   `JA_Vidro`, `JA_Abertura`, `JA_Uw`, `JA_AreaIluminacao`, `JA_AreaVentilacao` e `JA_Config`
   (oculto, com a configuração completa em JSON — é o que permite *Editar Esquadria*). Também
   Marca de tipo, Descrição, Fabricante, Modelo e Comentários de tipo.
6. Salva o `.rfa` em `Documentos\JA Esquadrias\Familias` e carrega no projeto, sobrescrevendo a
   versão anterior do mesmo código (as instâncias existentes atualizam).

O mesmo `Layout` alimenta a prévia, a família e o relatório, então o que aparece na tela é o que é
modelado. Uw: EN ISO 10077-1 simplificada, com Uf/Ug/Ψ típicos (estimativa; não substitui ensaio).

## Compilar e instalar

Igual ao JA Tools: **duplo clique em `GerarInstalador.bat`**. Ele confere/instala o .NET SDK e o
Inno Setup (winget), compila para Revit 2024 (net48), 2025/2026 (net8) e 2027 (net10) — com o Revit
instalado ou pelos pacotes NuGet `Nice3point.Revit.Api.*` — e gera
`dist\Instalador\JAEsquadrias_Setup_<versão>.exe` + um `.zip` para enviar.

Para testar direto no seu Revit: `.\build\build.ps1 -RevitVersions 2025 -Deploy`.

O JA Famílias é um add-in separado (AddInId próprio, pasta `JAEsquadrias`, aba própria) e convive
com o JA Tools.

## Estrutura

```
src/JAEsquadrias.Revit/
  App.cs                    faixa de opções
  Commands/                 Nova Janela/Porta, Biblioteca, Editar, Quadro, Relatório, Configurações
  Core/                     modelo (EsquadriaConfig), catálogo (modelos, perfis, vidros, cores),
                            Layout (geometria + Uw + áreas), biblioteca, relatório HTML, configurações
  Engine/                   gerador de família, parâmetros compartilhados, modelos .rft, quadro
  UI/                       configurador, desenho da elevação, tema JA, ícones
  Common/                   peças do JA Tools: OptionsDialog, ResultDialog, ToolChrome, HelpCenter…
```

Arquivos do usuário: configurações e preferências em `%AppData%\JAEsquadrias`, registro em
`%LOCALAPPDATA%\JAEsquadrias\Logs`, ajuda em `%AppData%\JAEsquadrias\Ajuda\index.html`.
