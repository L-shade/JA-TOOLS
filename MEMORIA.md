# Memória do projeto — JA para Revit 2.0 (JA Famílias + JA Tools num só add-in)

Registro do que foi pedido, decidido e feito nas conversas de desenvolvimento, para retomar o
trabalho sem perder contexto. Última atualização: 19/11/2026 (JA para Revit 2.3.0 — fórmulas e filtros no nível do Archicad, item 100; 2.2.0 — Filtrar Elementos, item 99; 2.1.2 — ambiente das portas, item 98; 2.1.1 — por elevação, item 97; 2.1.0 — JA_Nivel e Preencher Parâmetros, item 96; 2.0.5 — fora de esquadro, item 95; 2.0.4 — rente à parede, item 94; 2.0.3 — linha de ambiente, item 93; 2.0.2 — quinas, item 92; 2.0.1 — limpeza, item 91; 2.0.0 — unificação, item 90; antes: JA Tools 1.40.0, JA Famílias 1.29.1). JA Famílias: versão 1.16.0 (nicho no local, chanfro meia-esquadria no tento; chanfro meia-esquadria, plano de corte, contramarco, pingadeira, cortes, elevação no quadro, fachada paginada, aviso de versão; orçamento, emendas, ferragens, contorno pelo ambiente; bancada no local + áreas seca/molhada; chapim curvo; correr central/recolhível; painéis de parede cortina, conferência da planta 2D; soleiras/peitoris em todo o modelo, substituir pelo JA, bancadas por trecho; ralos, perfis de parede cortina, parede cortina, bancadas em ângulo, família paramétrica
na parede, nova interface; 1.3.0+ ainda não compilada). A 1.1.0 foi compilada e testada pelo usuário e funciona.

---

## 1. O que é

Add-in do Revit 2024–2027 do escritório **Joaquim Andrade Arquitetos Associados** (JA), irmão do
**JA Tools** (mesmo design: `JaTheme.xaml`, cabeçalho com passos, cartões, pílulas, ResultDialog,
Central de Ajuda offline). Inspirado no plugin da GEALAN no BIMobject ("Create your own windows and
doors"): configuradores visuais que geram **famílias Revit de verdade**, carregam no projeto e
posicionam.

- Nome visível: **JA Famílias** (aba do Revit "JA Famílias").
- Nomes internos mantidos (pedido do usuário): pasta `JAEsquadrias`, DLL `JAEsquadrias.Revit.dll`,
  namespace `JAEsquadrias`, AddInId `4F9ED87E-AF25-456D-9EB2-0BDDAF624858`, AppId do instalador
  `4D35B835-DEBF-4C29-9139-C62A19D07BE1`, pasta Documentos\JA Esquadrias (biblioteca/famílias).
- Pasta do projeto no computador do usuário: ver seção 6 (a antiga era
  `C:\Users\lucas\Downloads\JATools_1 (2)\JATools_1\JAEsquadrias`).
- Compilação: só no computador do usuário (na nuvem não há Revit API/WPF; ver seção 6).
  `CompilarTeste.bat` (compila 2024–2027) e `GerarInstalador.bat` (gera `JAFamilias_Setup_<versão>.exe`).
- Multi-target: 2024 → net48; 2025/2026 → net8.0-windows; 2027 → net10.0-windows
  (`-p:RevitVersion=…`, NuGet Nice3point.Revit.Api quando o Revit não está instalado).

## 2. Aba e botões (1.2.0)

| Painel | Botões |
|---|---|
| Esquadrias | Nova Janela, Nova Porta, Parede Cortina, Painel de Cortina (1.9.0) |
| Pedras e Nichos | Nova Bancada, Novo Nicho, Novo Chapim (empilhados) |
| Ralos (1.7.0) | Novo Ralo |
| Gerenciar | Editar Família (qualquer família JA; classe `EditarEsquadriaCommand`), Biblioteca, Substituir pelo JA, Conferir Planta (1.9.0) |
| Documentar | Quadros ▾ (SplitButton: Quadro de Esquadrias, Quadro de Peças), Relatório |
| JA | Central de Ajuda, Configurações |

Grupos do `ToolCatalog` = painéis (Esquadrias, Pedras e Nichos, Gerenciar, Documentar, Ajustes).

## 3. Arquitetura (src/JAEsquadrias.Revit)

- `Core/EsquadriaConfig.cs` — configuração de janela/porta (JSON em `JA_Config`).
- `Core/Layout.cs` — fonte única da geometria das esquadrias: `Prismas` (sólidos), `Vazios`
  (recortes na parede), `Simbolos` (linhas de abertura em elevação), `Planta` (linhas 2D com
  `GrupoPlanta`: Marco, Folha, Arco, FolhaFechada, Guarnicao, Pedra, Verga, Setas, Vao).
- `Core/Pecas/*` — bancada, nicho, chapim: `Peca` (perfil extrudado, sólido ou vazio),
  `PecaModelo`, `IPecaConfig`, `PecaCatalog` (modelos prontos), `PecaUtil` (`Gabinete(t)`,
  `Categoria(t)`, prefixos BC/NI/CH).
- `Engine/FamilyGenerator.cs` — gera janela/porta a partir do .rft; `WarningSwallower`,
  `OverwriteLoadOptions`, `VoidGuard` (vazio corta só a parede, nunca as próprias peças).
- `Engine/PecaGenerator.cs` — gera bancada/nicho/chapim (Generic Model .rft, categoria trocada).
- `Engine/ChapimPlacer.cs` — aplica chapim no topo das paredes (linha de centro, aba externa para
  fora, um tipo por espessura: `CH01-200`…).
- `Engine/SharedParams.cs` — parâmetros compartilhados (GUIDs fixos): JA_Config (oculto), JA_Linha,
  JA_Cor, JA_Vidro, JA_Abertura, JA_Uw, JA_AreaIluminacao, JA_AreaVentilacao, JA_SoleiraCodigo,
  JA_SoleiraMaterial, JA_SoleiraDimensoes, JA_Familia, JA_Material, JA_AreaPedra, JA_Dimensoes,
  JA_Comprimento (instância, fórmula = comprimento da linha do chapim).
- `Engine/ProjectEsquadrias.cs` / `ProjectPecas.cs` — leitura do projeto (esquadrias só em
  Janelas/Portas; peças em Modelos genéricos + Componentes de gabinete).
- `Engine/ScheduleBuilder.cs` — quadros de esquadrias, soleiras/peitoris e peças.
- `UI/ConfiguratorWindow.cs` (esquadrias: abas Medidas, Divisões, Perfil e vidro, Detalhes, Planta 2D),
  `UI/PecaWindow.cs` (peças), `UI/PecaRenderer.cs` (prévia 3D/planta/frente com z-buffer próprio),
  `UI/ElevationRenderer.cs`, `UI/IconFactory.cs`.
- `Commands/EsquadriaCommands.cs` + `PecaCommands.cs` (classe parcial `EsquadriaCommandBase`),
  `Commands/DocCommands.cs` (quadros, relatório, configurações).

## 4. Decisões e pedidos do usuário (em ordem)

### Esquadrias
1. Porta em planta como **bloco de CAD**: folha aberta + arco (sem folha de vidro fechada visível).
   Aba **Planta 2D** com várias formas de mudar a vista (ângulo, retângulo/linha, arco
   cheio/tracejado/reta/sem, folha começa na face do marco ou no eixo, folha fechada tracejada).
   Cada grupo vira parâmetro Sim/Não de instância "Planta - …".
2. Soleira/filete: tamanho, código e material; rente ao lado externo ou interno.
3. Janela deve ficar na abertura do modelo (não no nível): posição lida das faces da parede do .rft.
4. Revestimento interno e externo (cotas da parede bruta à face acabada) em cada lado.
5. Sem linha simbólica única no meio do vidro; folha alinhada à estrutura mais externa.
6. Padrão de desenho do escritório (PDFs de "relação de esquadrias"), **sem hachuras de superfície**:
   linhas de abertura dos cantos da dobradiça ao meio do lado que abre; correr com triângulos ▷◁;
   F nos fixos; cotas com pontos.
7. **Janelas não têm puxador** (nenhum tipo). Puxadores só em portas.
8. **Peitoril**: pode ter abas laterais; a família tem um vazio que **recorta a alvenaria** para a
   pedra (abaixo do vão e nas abas). **O peitoril não aparece na planta baixa.**
9. **Planta da janela** = corte das peças a 1,20 m do piso (como no editor de família), desenhado
   em linhas 2D (sólidos ocultos em planta) para ficar igual em qualquer plano de corte da vista:
   montantes do marco e montantes das folhas em corte, vidro, contorno completo de cada folha
   (travessa inferior vista de cima), marco inferior NÃO aparece, setas de correr (grupo próprio
   "Planta - setas de correr"). Referência aprovada: folhas em dois trilhos, vidro em duas linhas.
10. **Abertura na parede sempre visível em planta**: linhas dos dois lados do vão atravessando a
    parede + região de máscara (invisível) cobrindo a parede entre elas; parâmetro
    "Planta - abertura na parede". Motivo: janelas com peitoril acima de 1,20 m não são cortadas
    pela vista e a parede aparecia contínua.

### Bancadas, nichos e chapins
11. Pedido: criar bancadas, nichos e chapins "com tanta customização quanto as esquadrias" e
    renomear o plugin para JA Famílias (só nomes visíveis).
12. **Bancada**: família solta (Modelo genérico), posicionada por clique (origem no canto do fundo à
    esquerda, no piso). Reta, L, U; saia, tira americana, tento, frontão, pés; cubas de embutir,
    sobrepor, esculpida e apoio; cooktop.
13. **Nicho**: baseado em parede; o vazio corta a parede (corte feito na família com
    `SolidSolidCutUtils` após `Regenerate`; reserva no projeto com `InstanceVoidCutUtils`).
14. **Chapim**: baseado em linha, aplicado nos muros selecionados (acompanha o comprimento).
15. **Nichos e chapins são sempre Componentes de gabinete** (categoria trocada no .rft genérico).
    Bancadas continuam Modelo genérico.

### Instalação
16. Revit 2027 só carrega add-ins "para todos os usuários" de
    `C:\Program Files\Autodesk\Revit\Addins\2027` (não de ProgramData). O instalador usa
    `{code:AddinDir|versão}`: todos os usuários → 2024–2026 em ProgramData, 2027+ em Program Files
    (apaga a cópia antiga em ProgramData); só para mim → AppData. Mesma correção feita no JA Tools.
    Na 1ª abertura: escolher "Sempre carregar" no aviso de plugin não assinado.

### Interface (04/11/2026) — vale para JA Famílias e JA Tools
17. Pedido: "revisar toda a interface dos dois plugins, mais intuitiva, bela e prática, unindo
    ferramentas e reorganizando". Decisões do usuário: **duas abas separadas** (JA Tools e
    JA Famílias), **só agrupar na faixa** (menus/SplitButton; nenhuma ferramenta muda por dentro),
    protótipo antes do código — aprovado ("gostei da nova interface"). Protótipo no canvas
    "JA Tools e JA Famílias — nova interface" (claude.ai/artifact/EvN523CGtvB4Zt2a1FHSB5).
18. Visual novo (mesmo nos dois): fundo #F3F4F1, cartões brancos, linha #E3E6E1, tinta #18212B;
    cores de grupo harmonizadas; **ícones de traço** (`IconFactory.Paths`, grade 24×24, sintaxe
    WPF/SVG com números separados por espaço) num quadrado com a cor suave do grupo — na faixa,
    no cabeçalho das janelas e na Central de Ajuda; sem a faixa colorida de 4 px no topo;
    escolhas curtas como controle segmentado (trilho cinza, opção escolhida branca); passos
    curtos como chips 1 → 2 → 3; rodapé branco. `IconFactory.Tool()` cai no ícone de letras
    quando a ferramenta não tem ícone.
19. JA Tools 1.12.0: painéis Modelagem (Por Ambiente ▾, Em Camadas ▾), Vistas (Cortes Gerais,
    Plantas por Nível ▾, Vistas por Ambiente ▾, Eixos ▾, Ocultar em Vista), Detalhamento
    (Detalhar Ambientes, Anotar Planta, Anotações ▾, Fichas ▾, Hachuras ▾), Cotas (Cotas de
    Ambiente ▾, de Parede ▾, de Pontos ▾), Pranchas (Gestão, Renomear, Duplicar, Alinhar Vistas),
    Exportar (Pranchas, Vistas, Tabelas), Dados (Renumerar, Níveis, Remover Ambientes), JA
    (Central de Ajuda, Padrões do Escritório). 51 botões → 27 controles. Grupos do catálogo
    renomeados: Publicação → "Pranchas", Parâmetros → "Dados", novo "Geral" (Padrões).
    Pasta: `C:\Users\lucas\Downloads\Nova pasta (2)\JATools_3`.

### Esquadria em qualquer parede (1.3.0, 04/11/2026)
22. Pedido: "gerar esquadrias em qualquer parede sem informar a espessura; revestimentos como
    parâmetros de instância". `Core/Profundidade.cs` (posição através da parede = C + At·t + Ax·rx +
    Ai·ri, testado), `Engine/AncorasParede.cs` (planos travados nas faces, cota "Espessura da parede",
    revestimentos de instância, planos-âncora a 1000 mm com fórmulas "JA aux n"), planta como item de
    detalhe aninhado preso por cota bloqueada, abertura em faixas de 5 mm (50–450). Erros de restrição
    viram aviso (parte fica fixa). API a conferir: SketchPlane.Create(doc, refPlaneId), MakeReporting,
    NewAlignment linha↔ponto, LoadFamily fora de transação.

### JA Tools — Varinha Mágica (1.13.0, 04/11/2026)
23. Pedido: atalho para a varinha (padrão **Espaço**, o usuário pode trocar) e controle menos
    invasivo, no canto. Feito: `Tools/Detail/VarinhaAtalho.cs` (gancho de teclado de baixo nível
    instalado no `App.OnStartup`, `ExternalEvent`; só dispara com a janela do Revit na frente, o
    teclado na área de desenho/MDIClient, fora de campo de texto, nada selecionado e o Revit
    ocioso — evento que esperou > 600 ms é descartado; apertar de novo durante a varinha manda Esc).
    Opções: Espaço, Ctrl+Espaço, Shift+Espaço, F2–F4, F6–F9, Desligado (pref
    `VarinhaMagica.atalho`). `Tools/Detail/VarinhaPainel.cs`: painel pequeno no canto inferior
    direito do Revit (não rouba o foco, arrastável, posição guardada), com Criar (segmentado),
    tipo, "Mais opções" (contorno da hachura, modo do contorno), contagem, "alterar" atalho.
    A janela de opções antes da varinha saiu; PickPoint agora sem snaps. Resumo final só se houve
    problema. `ToolCommand.Vincular(app)` para rodar ferramentas a partir de evento externo.

### JA Famílias — Parede Cortina (1.4.0, 04/11/2026)
24. Pedido: "o JA Famílias deve ser capaz de criar esquadrias para paredes cortinas". Feito:
    botão **Parede Cortina** (painel Esquadrias, `EsquadriaParedeCortinaCommand`): selecionar
    painéis (ou PickObjects; aceita OST_CurtainWallPanels ou porta/janela hospedada em parede com
    CurtainGrid) → TaskDialog Janela/Porta → configurador com o tamanho do painel mais comum
    (campos de tamanho travados, cartão "Posição na parede cortina" só com "Deslocar p/ fora",
    sem peitoril nem "Marco na espessura") → "Gerar e trocar painéis": gera uma família por
    tamanho (os outros tamanhos ganham `NextCode`) e faz `Unpin` + `ChangeTypeId` nos painéis.
    - `EsquadriaConfig.ParedeCortina` (DataMember 130); `MarcoNaParede` é falso em cortina.
    - `Layout`: em cortina ParedeMm = MarcoProf, revestimentos 0, marco centrado no plano
      (FrameOffset = MarcoProf/2 + Recuo), sem peitoril, sem linhas de vão e verga.
    - `TemplateLocator.FindCurtain` (porta/janela + "curtain/cortina"); `FamilyGenerator`:
      `ReadCurtainPanel` (sem parede; esquerda/base pelos planos de referência que distam L/H,
      senão centro/nível), sem âncoras de parede, Largura/Altura só como dica (a grade manda),
      `Adaptavel = true`.
    - Preferência separada `ultima.cortina.<Tipo>`; aplicar modelo no configurador mantém o
      tamanho; Editar Família em painel trocado lê o tamanho do painel.
    - Testado no harness: todos os 25 modelos do catálogo em cortina sem avisos.

### JA Famílias — Bancadas em ângulo (1.5.0, 04/11/2026)
25. Pedido: "criar bancadas com os recortes das bordas em ângulos também". O usuário escolheu os
    4 tipos: ponta cortada em ângulo, canto chanfrado, L/U com ângulo livre, borda chanfrada no
    perfil. Feito em `Core/Pecas/BancadaConfig.cs` (DataMember 60–69; JSON antigo sem esses campos
    vira 90°):
    - `AnguloBGraus` (L: retorno; U: lado esquerdo) e `AnguloCGraus` (U: lado direito), ângulo
      interno com o trecho A (90 = esquadro; > 90 abre). B/C medidos pela parede do retorno.
    - `PontaEsqGraus`/`PontaDirGraus`: o vértice da frente desliza pela aresta da frente
      (d = profundidade / tan α); vale também nas pontas dos retornos.
    - `ChanfroCantoEsqMm`/`DirMm`: chanfro reto no canto da frente das pontas livres (a aresta nova
      é borda livre: recebe saia/tento).
    - `Perfil` (Reta/Chanfrada), `ChanfroBordaMm` (5) e `ChanfroBordaGraus` (45): tampo dividido em
      laje inferior + laje superior recuada nas bordas livres + cunha triangular (Peca num plano
      vertical, extrudada ao longo da aresta). Com tento, o chanfro vai na quina do tento. Nos
      cantos convexos sobra uma pontinha do tamanho do chanfro (as cunhas não são em meia-esquadria).
    - `MeiaEsquadria` (saia): só dado de fabricação (descrição/quadro), sem geometria.
    - UI (`PecaWindow`): ângulos no cartão Formato, cartão novo "Pontas e cantos" (Medidas),
      cartão "Perfil da borda" e opção de meia-esquadria (Bordas). Testado no harness `/home/claude/hb`.

### JA Tools — Exportar Pranchas e Gerar Detalhes (1.14.0, 04/11/2026)
26. Pedido: "retrabalhe a interface do exportar plantas e do gerar detalhes, mais bela, mais
    eficiente, mais próxima da nova UI da varinha mágica, removendo redundâncias" (sem mudar funções).
    - `JaTheme.xaml`: abas implícitas viraram controle segmentado num trilho cinza (vale para todas).
    - `ToolChrome.DressCompact` (cabeçalho leve: ícone 32, título 16, uma linha cinza, "?"; sem
      trilha de passos), `ToolChrome.Labeled` e `ToolChrome.Track`.
    - `UI/ExportWindow.xaml`: sem faixa de passos e sem números; formatos em 3 cartões compactos;
      **Destino** (pasta, nome dos arquivos, nome do PDF único / DWG único, sobrescrever) sempre à
      vista; opções em PDF · DWG · DWG único (versão/unidades, model space, layouts, vistas fora de
      folhas, imagens) · Avançado — 6 abas viraram 4; textos longos foram para tooltips.
      Code-behind: `FolderBox`, `KeepVisibleTab()`.
    - `Tools/Views/LegendBoardWindow.cs`: `DressCompact`; cartão "Tipos" sem números, "Só usados"
      e Fase numa linha, dicas em tooltip; barra de Modelo enxuta (Salvar como… + menu ⋯ com
      Desfazer/Excluir); "Textos e cotas" + "Tabela" = "Textos e tabela"; aba Detalhes típicos só
      aparece no estilo Relação; Pranchas num cartão só; textos longos em tooltip.

### JA Tools — Fachada Planificada (1.15.0, 05/11/2026)
27. Pedido: "ferramenta para criar uma fachada ortogonal mesmo com diversos ângulos". Escolhas do
    usuário: planificada; paredes selecionadas OU cliques na planta; prancha montada.
    `Tools/Views/FachadaPlanificadaCommand.cs` (painel Vistas, ícone "fachada"): um corte vivo por
    trecho (X ao longo do trecho, recorte = comprimento, altura nível mais baixo − margem até o mais
    alto + margem, plano de corte "afastamento" à frente da face, profundidade atrás); paredes
    encadeadas pela ponta mais próxima, cantos pela interseção das linhas, curvas em cordas de 15°,
    lado exterior por voto de `Wall.Orientation`; cliques com linhas de detalhe temporárias + clique
    do lado. Prancha nova (FPnn): viewports movidas com `GetModelToProjectionTransforms` +
    `GetProjectionToSheetTransform` para cada trecho começar onde o anterior termina; linha
    tracejada nas dobras; tipo de viewport sem título; níveis só no 1º trecho.
- 05/11/2026: sem acesso às pastas do PC nesta sessão. O usuário anexou JATools_14.zip (1.10.1) e
  JAEsquadrias_6.zip (1.1.0); montei zips completos = base + meus arquivos (em
  /mnt/user-data/outputs/zips). Na base 1.10.1 do JA Tools só `LegendEngine.cs` é mais antigo que o
  da pasta do PC (683 bytes; provável "portas: só o ambiente do lado interno").

### JA Tools (CAD) 1.0.0 — plugin novo para AutoCAD (05/11/2026)
28. Pedido: "plugin para o CAD que pegue as áreas das plines (atributo/campo que calcula a área) e
    crie um quadro de áreas como o da imagem" (modelo: QUADRO DE ÁREAS com terreno, pavimentos
    coberto + descoberto, Tipo ×3, área total, AEC, unidades, vagas exigidas/projetadas, lixo,
    reservatórios, reuso/retardo). O escritório usa **AutoCAD completo** (os DWGs de exemplo foram
    salvos no LT 2025, mas o usuário confirmou o completo). Arquivo "mais correto":
    `SB - PREFEITURA - R03.dxf`: sob cada planta, título na layer `JA - TXT_COMPART`
    ("PAVIMENTO TIPO") e MText em `JA - TXT_GERAL 01` com campos AcExpr/AcObjProp ligados às
    polilinhas `JA - PL_AREA_CONST` / `JA - PL_AREA_PRIV`: "ÁREA CONSTRUÍDA: 323.88m²+104.20m²(var)
    =428.08 m²" e "ÁREA PRIVATIVA: …" (o total depois do "=" às vezes é texto fixo e está errado:
    cobertura 435,14 escrito × 434,86 real). Terreno: `JA - TERRENO`; vagas: polilinhas em
    `JA - VAGAS`. Desenho em metros (INSUNITS diz mm).
    - Projeto `JAToolsCAD` (só na nuvem + zip `JAToolsCAD_1.0.0.zip`): net48/net8/net10, NuGet
      `AutoCAD.NET` ou AutoCAD local; bundle `JAToolsCAD.bundle` (ApplicationPlugins, R24/R25/R26);
      `Compilar.bat`, `CompilarEInstalar.bat`, `dist\Instalar.bat`.
    - Comandos: JAQUADRO (janela WPF com prévia; abas Pavimentos, AEC, Unidades e vagas, Lixo e
      reservatórios, Ajustes; config salva no NOD "JA_TOOLS_CAD"), JAQUADROATUALIZAR, JAVAGA
      (xdata "JA_VAGA"="PRESA"). Quadro = bloco `JA_QUADRO_AREAS` (DBText + linhas, estilos
      JA_QUADRO/JA_QUADRO_NEGRITO, layer JA-QUADRO-AREAS).
    - Núcleo testado com .NET 8 no harness `/home/claude/hq` usando os textos do DXF. Não compilado
      no AutoCAD; risco: AdWindows no pacote NuGet AutoCAD.NET (faixa de opções).

### JA Tools — Elevações por Ambiente (1.16.0, 06/11/2026)
29. Pedido: "acelerar e otimizar a geração de elevações de ambiente + revamp visual como Gerar
    Detalhes/Exportar/Varinha". Feito: `Tools/Views/ElevacoesPorAmbienteCommand.cs` (mesma classe
    da faixa, saiu de `RoomViewCommands.cs`) + `ElevacoesAmbienteWindow.cs` (`DressCompact`).
    - Velocidade: planta por nível em cache, todos os marcadores/vistas primeiro e **um só
      Regenerate** (antes era um por elevação), depois todos os recortes; progresso com etapas e
      Cancelar (desfaz tudo).
    - Lados A (cima da planta) B (direita) C (baixo) D (esquerda); índice do marcador de cada lado
      medido numa SubTransaction descartada. "Alinhar às paredes": gira o marcador pelo ângulo
      dominante das paredes (mod 90°, −45..45). Recorte pelo contorno real do ambiente (acabamento);
      profundidade até a parede oposta + 0,30 m. Mostrar recorte opcional.
    - Nome com {ambiente} {numero} {nome} {letra} {nivel}; se já existir: Criar outras / Só as que
      faltam / Substituir (apaga as vistas com os mesmos nomes e os marcadores vazios).
    - Janela: lista com busca (sem acento), nível, "Só sem elevações", "já tem n", Shift+clique,
      agrupada por nível; cartões Lados (desenho do ambiente), Vista, Recorte, Nome (chips +
      exemplo), Se já existir; botão "Criar N elevações"; Ctrl+F / Ctrl+Enter. Prefs
      `ElevacoesAmbiente.*`. Zip `JATools_1.16.0_completo.zip`.

### JA Tools — UI única e ferramentas unidas (1.17.0, 06/11/2026)
30. Pedido (JA Tools): "mesmo tratamento de UI, código redundante, una ferramentas". 12 fusões aprovadas,
    antigos removidos da faixa. `OptionsDialog` com `ToolChrome.DressCompact`, duas colunas, busca sem
    acento, ↺ restaurar, `Field.VisibleWhen`. Fusões: PlantasPorNivel, VistasPorAmbiente, CotasDePontos,
    CotasPorSelecao (Fachada), CotasPisoForro, AnotarVista, OrganizarAnotacoes, HachuraUnica,
    RodapeTabica, ConverterEmCamadas, Exportar Vistas = PNG do Exportar Pranchas. ~50 → 37 ferramentas.
    Atalhos do Revit precisam ser refeitos; extrair o zip numa pasta NOVA.

31. 1.17.1 — "imagens OLE só começam depois de fechar a janela do plugin": o resumo do Exportar
    Pranchas era modal (`ResultDialog.Show` = ShowDialog) e segurava o comando; o OLE era iniciado
    dentro do comando. Agora `ResultDialog.ShowModeless` (Esc fecha) e `JobReport.PendingOle`: o
    comando inicia o `OleBackgroundJob` no primeiro `UIApplication.Idling` depois de retornar
    (`StartWhenIdle`, com timer de 5 s de reserva). Não compilado; aguardando teste.

### JA Famílias — Perfis de parede cortina (1.6.0, 06/11/2026)
32. Pedido: perfis de parede cortina, substituir em planta/3D/corte. Painel **Perfis** no JA Famílias:
    `Core/Perfis/PerfilConfig.cs` (X ao longo, Y+ exterior, Y=0 vidro; Retangular, Tubos, I, T, U, L,
    ComTampa, Canto V/L; cantos reto/chanfrado/boleado; harness `/home/claude/hp`). `Engine/
    PerfilGenerator.cs` (modelo Profile-Mullion, Y espelhado, tipo de montante por Duplicate + MULLION_
    PROFILE; canto pelo montante V/L do sistema). `UI/PerfilWindow.cs`, `TrocarMontantesCommand`
    (escolhidos / mesmo tipo / projeto; verticais/horizontais). Família "JA Perfil MT01".

### JA Tools — Suspender Grupos (1.18.0, 06/11/2026)
33. 1.17.2: corrigido `OleBackgroundJob` sem namespace (CS0246) no `UnifiedDwgExportCommand` — o
    usuário compilou a 1.17.1 e esse foi o único erro (o resto da 1.17 compilou nos 4 Revit).
34. Pedido: "ferramenta similar a suspender grupos do ArchiCAD". `Tools/Modeling/SuspenderGruposCommand.cs`
    (painel Modelagem): um botão só; o texto vira "Restaurar Grupos (n)" (`Faixa.Botao`, atualizado em
    ViewActivated e DocumentChanged). Suspender: selecionados / vista / projeto (+ grupos de detalhe),
    `UngroupMembers`, guarda no modelo (ExtensibleStorage, schema 5d2b9c1e-…, DataStorage com JSON):
    tipo (UniqueId, nome), membros (UniqueIds), fixado, centro e assinatura (categoria|tipo|caixa em mm).
    Grupos com detalhe anexado não são suspensos. Restaurar: `NewGroup` dos membros; não alterado →
    volta ao tipo original (troca o tipo e confere a assinatura movendo/girando 90°/espelhando numa
    SubTransaction); se o tipo não tem outras cópias, o grupo novo assume o nome (ids mantidos);
    alterado → tipo "… (editado)" (ou o nome original se era a única cópia). "Esquecer" limpa o estado.
    Não compilado.

### JA Tools — Gestor de Estilos (1.19.0, 06/11/2026)
35. Pedido: "função similar ao Ideate StyleManager". Escolhas: todas as categorias (textos e cotas,
    linhas e padrões, vistas, materiais e famílias) e todas as ações (ver uso e onde, unir/substituir,
    limpar não usados, renomear em lote). `Tools/Parameters/GestorEstilos.cs` (motor + comando) e
    `GestorEstilosWindow.cs`; botão grande no painel Dados (ícone "estilos").
    - `EstiloTipo` (Listar/Usos/Unir/Renomear/Apagar/Assinar) e `UsoCache` (instâncias por tipo,
      referências ElementId em parâmetros de tipos, curvas por estilo de linha, vistas com filtros).
      Tipos: texto, cota (por StyleType), etiquetas/símbolos (por categoria), estilos de linha
      (subcategorias de Linhas; renomear = recriar + passar as linhas + apagar), padrões de linha
      (estilos de objeto + filtros nas vistas), padrões de preenchimento (regiões, materiais,
      filtros), tipos de região, modelos de vista (vistas + padrão dos tipos de vista), tipos de vista,
      filtros (AddFilter copiando sobreposições/visibilidade), materiais (camadas, parâmetros de tipos
      e de famílias colocadas, estilos de objeto), tipos de parede/piso/forro/telhado, tipos de família,
      famílias (só limpar/renomear).
    - Janela: categorias à esquerda; tabela com marcar (Espaço), Usos/Vistas/≡ duplicados, busca sem
      acento, "Só não usados"/"Só duplicados"; à direita Onde (vistas com contagem, "Abrir e
      selecionar" fecha e seleciona), Unir (destino compatível; "Unir duplicados" deixa o mais usado),
      Limpar (só 0 usos, 🔒 nunca), Renomear (prefixo/sufixo/procurar/maiúsculas, prévia na coluna
      "Novo nome", dois passos para trocas A↔B). Cada ação = uma transação; após unir, a origem só é
      apagada se ficou sem uso. CSV da lista. Não compilado.

### JA Tools — Documentos Vinculados (1.20.0, 06/11/2026)
36. Pedido: "função similar ao Ideate Sticky" (Excel como tabela; PDF/Word/Excel como imagem; avisar e
    1 clique). `Tools/Publish/PlanilhaLeitor.cs` (xlsx/csv sem Revit, testado em `/home/claude/hx`),
    `DocVinculos.cs` (ExtensibleStorage 8e3f1b6a-…; tabela = ViewSchedule com a planilha no cabeçalho,
    estilos/mesclas/bordas; imagens por ImageType.Create, Office→PDF por COM; `DocAviso` ao abrir) e
    `DocVinculosWindow`. Botão no painel Pranchas. Não compilado.

### JA Tools — DWG: cores ByLayer e viewport sem impressão (1.21.0, 06/11/2026)
37. Pedidos: "o exportador para DWG deve automaticamente [pôr] o object color de todos os elementos
    para ByLayer" e "a layer viewport deve estar sempre exportada com a função de não imprimir".
    No motor AutoCAD (`RevitDwgUnifier.AcadMerge`): `Cleanup/ByLayerColors.cs` (ColorIndex 256 em
    todas as entidades de model/layouts/blocos — menos blocos *D de cota —, atributos, Dimclrd/Dimclre
    de estilos e cotas, códigos \C/\c do MText; Dimclrt só se a cor das cotas = 0) e
    `Cleanup/ViewportLayer.cs` (layer só de viewports → IsPlottable = false; viewports em layer
    compartilhada/0 → "JA - VIEWPORT", cor 8, não imprime; viewport do papel ignorada).
    `MergeOptions.ColorsByLayer`/`ViewportsNoPlot`/`ViewportLayerName` (Order 41–43, schema v12 liga),
    passos "vpnoplot" (antes do purge) e "bylayer" (antes do dimcolor) no DWG único; no DWG por prancha
    o RVTSTANDARDS roda se qualquer um (padrão JA, ByLayer, viewports) estiver ligado. Duas caixas na
    aba DWG do Exportar Pranchas. Não compilado.
38. 1.21.1 — erros da compilação da 1.21.0 (1ª compilação de 1.18–1.21): `Color.FromColorMethod` não
    existe (→ `Color.FromColorIndex(ColorMethod.ByLayer, 256)`); `Group.GetShownAttachedDetailGroupTypeIds`
    pede View (→ `GroupType.GetAvailableAttachedDetailGroupTypeIds()`); `MontarPainel` devolvia UIElement
    (sem Margin); em janelas WPF que importam `Autodesk.Revit.DB`, `Visibility.Visible` é ambíguo →
    usar sempre `System.Windows.Visibility.X`. NU1102 do AutoCAD.NET 26.1.* é só uma tentativa do build.
39. 1.22.0 — "Gestor de Estilos lento". `UsoCache.PorTipo(chave, filtro)`: instâncias lidas por tipo
    de estilo com filtro estreito (TextNote, Dimension + categorias de cota, FilledRegion, View, Wall,
    Floor, Ceiling, OST_Roofs; senão as categorias dos tipos listados) em vez do modelo inteiro, com a
    vista dona guardada junto (`EstiloItem.Uso(id, vista)`, `InstVista`, `UsosSaoVistas`) — o "Onde" não
    relê elementos. `RefsEmTipos` (varre parâmetros de todos os tipos) só para tipos de parede, de família
    e famílias (`refs`). Materiais: parâmetros de material das famílias colocadas descobertos uma vez
    por família (`ParametrosMaterial`, SpecTypeId.Reference.Material). Duplicados (assinaturas) só ao
    abrir Unir ou "Só duplicados". Janela: filtro gera uma coleção nova (1 atualização), busca com
    espera de 180 ms e texto sem acento pré-calculado, virtualização com reciclagem, aviso "Lendo…".
40. 1.22.1 — "Limpar não usados demorado": `GestorEstilos.ApagarEmLote` apaga todos com um só
    `Document.Delete(lista)` numa SubTransaction; se o Revit recusar, divide ao meio e tenta de novo
    (só os recusados ficam, com o motivo). Usado no Limpar e no apagar depois de Unir. Limpar não relê
    os usos (`Executar(..., reler: false)`): tira as linhas apagadas da tabela e marca `_cacheSujo`
    (o cache é refeito ao trocar de categoria); se a transação falhar, relê tudo.

### JA Tools — DWG por prancha = mesmas opções do DWG único (1.23.0, 06/11/2026)
41. Pedido: "o DWG único e o DWG do exportador devem ter as mesmas opções de customização". O DWG por
    prancha agora passa pelo mesmo pipeline: `Core/UnifiedExportJob.PerSheet.cs` (`RunPerSheet`,
    classe virou `partial`): mesmo `DwgOptionsFactory` (versão, unidade, coordenadas, texto exato,
    fontes, hachuras), mesmo staging (recortes, vistas fora de folha no carimbo), e cada item vai
    sozinho pelo motor RVTUNIFY (manifesto clonado por JSON, pasta própria p001…), até 3 Core Consoles
    em paralelo (`Parallel.ForEach` numa Task; o progresso é relatado na thread da janela). Etapa que
    trava é desligada só para aquele arquivo e salva nas configurações. Nome = padrão do exportador
    (`ExportNaming`). Sem AutoCAD: entrega os DWGs crus do Revit. OLE: um `OleBackgroundJob` por
    arquivo, encadeados por `Next` (o do DWG único primeiro). `ExportDwgPerSheet`/`ApplyJaStandards`
    saíram do comando (RVTSTANDARDS continua no motor, sem uso). Abas "Layouts e model" (antes "DWG
    único") e "Avançado" aparecem para qualquer DWG; nota no topo da aba DWG. Não compilado.
42. 1.24.0 — Pedido: "incorpore funções, ferramentas e conceitos do Glyph". Escolhas: Tarefas e
    Pacotes, Relatório de status, Seleção unificada, Mapeador de folhas; interface "Central JA";
    assistente "Busca de comandos". Feito:
    - Motor `Tools/Common/Automacao.cs` (Tarefa, Pacote, Escopo, Biblioteca, StatusPasso/Execucao;
      `%AppData%\RevitDwgUnifier\central_ja.json`; pasta do escritório com `*.jatarefas` somente
      leitura; Importar/Exportar). Tarefa roda sem janela: `OptionsDialog.Show` devolve OK com os
      valores salvos (ToolPrefs com backup/restauro); `ToolCommand.Show` captura mensagens quando
      `Automacao.Ativa`; `ToolCommand.Executar(UIApplication)` roda a ferramenta por código; Exportar
      Pranchas via ExternalCommandData criado por reflexão. Ferramentas com janela própria viram
      tarefa "interativa" (registra `ToolPrefs.Gravando`). Botão "☆ Tarefa" no rodapé de toda
      OptionsDialog; modo "Salvar tarefa" ao configurar.
    - `Tools/Central/CentralWindow.cs` + `CentralJaCommand.cs` (abas Ferramentas com favoritas,
      Tarefas, Pacotes, Escopo, Status; `PedidoCentral` + `CentralExecutor`, reabre em Status após
      rodar). `Tools/Central/BuscaComandos.cs` (paleta, recentes em `busca.recentes`, t:/p:/f:).
    - `Tools/Publish/MapeadorFolhas.cs` + `MapeadorFolhasWindow.cs` (vistas sem prancha → uma por
      vista / N / encaixar com `LayoutEngine.Pack`; carimbo medido por `LegendEngine.MeasureTitleBlocks`;
      grade editável nº/nome).
    - Faixa: painel JA ganhou Central JA e Buscar Comando; Pranchas ganhou Mapeador de Folhas.
      ToolCatalog + ícones "central", "busca", "mapeador". README seção "Central JA". Não compilado.
43. 1.24.1 — a compilação da 1.24.0 (motores AutoCAD ok) deu só 2 erros CS0104 em
    `Automacao.PerguntarNome`: `Panel` (WPF × Revit DB) e `TextBox` (WPF × Revit UI) → nomes completos
    `System.Windows.Controls.*`. Regra: em arquivo que importa Revit DB/UI e WPF, qualificar Panel,
    TextBox, ComboBox, Visibility, Grid, Binding.
44. 1.25.0 — Pedido: "a Central deve ser um menu mais leve e fluido e funcionar também como barra
    lateral bem como janela, sem atrapalhar o usuário". `CentralWindow.cs` (modal) apagado →
    `Tools/Central/CentralPainel.cs` (UserControl vivo + `CentralJanela` flutuante não modal) e
    `CentralHost` em `CentralJaCommand.cs`: DockablePane registrado no `App.OnStartup` (direita,
    EditorInteraction KeepAlive, VisibleByDefault por reflexão), um `ExternalEvent` com fila
    (`CentralHost.Pedir`) para tudo que mexe no modelo; ⧉/⇥ alterna lateral/janela (`central.modo`),
    botão da faixa alterna mostrar/esconder. Visual: busca única (Enter roda o 1º), abas em segmento
    (Ferramentas/Tarefas/Pacotes/Status), linhas compactas com ações no hover (▶ ☆ ⋯), favoritas +
    recentes + grupos recolhidos, tarefas/pacotes abrem no lugar, chip de Escopo com editor embutido,
    última execução no rodapé. Extrair em pasta nova (arquivo apagado). Não compilado.
45. 1.25.1 — "O Gestor de Estilos não deve tentar eliminar elementos que não podem ser eliminados".
    `EstiloItem.NaoApaga` (motivo) + `Apagavel`; `GestorEstilos.MarcarNaoApagaveis` depois de cada
    leitura e de cada Limpar: Id < 0, `DocumentValidation.CanDeleteElement` (por reflexão) e "último
    tipo" por `GrupoMinimo` (família carregável, ViewFamily, StyleType de cota, família de sistema =
    classe|categoria|FamilyName). `ApagarEmLote` filtra antes (`SoApagaveis`: nunca apaga todos os
    tipos restantes de um grupo; relata "mantido" com o motivo) — o Revit nem é chamado. 🔒 com dica
    do motivo na tabela; "Não usados"/Limpar/Unir só contam os apagáveis. Não compilado.
46. 1.25.2 — "A criação de vistas de ambiente deve ser paralela às paredes, independente dos ângulos".
    `Tools/Views/ParedesAmbiente.cs` (compartilhado): `Lados(room, 0,6 m)` = trechos retos do contorno
    externo agrupados pela direção para onde olham (±3°, sentido do laço pela área assinada; direções
    com < 0,6 m somem); letras horárias a partir de cima no referencial das paredes (A–D se ≤ 4 lados
    em quadrantes distintos; senão A, B, C, D, E…); `CriarElevacoes`: um marcador por "quadro"
    (ângulo mod 90°) girado, índice escolhido pelas direções medidas do marcador (`DirecoesDoMarcador`,
    cache por tipo); `Recortar` na largura da própria parede, chão→topo, far clip atrás da parede;
    `GirarPlanta` (acha o elemento de recorte apagando a vista numa SubTransaction desfeita, gira pelo
    ângulo dominante e reajusta o recorte no referencial girado); isométrico com caixa de corte e
    olho girados. Vistas por Ambiente usa tudo (opção "Paralelas às paredes", antiga "Alinhar");
    Detalhar Ambientes usa as elevações (planta/isométrico dele não giram ainda). Não compilado.

### JA Tools — Unir Elementos (1.26.0, 06/11/2026)
48. Pedido: "ferramenta que una paredes e revestimentos (paredes cebola), paredes e colunas e afins em
    todo o projeto; identificar grupos e unir dentro deles sem desvincular nem quebrar". Feito:
    `Tools/Modeling/UnirElementosCommand.cs` (painel Modelagem, ícone "unir", OptionsDialog com escopo
    selecionados/vista/projeto; pares: cebola, paredes×pilares, paredes×pisos (desligado por padrão),
    pisos×pilares, vigas; ordem pilar › viga › piso › parede, parede grossa corta revestimento;
    corrigir ordem nos já unidos; folga mm; grupos: unir também / ignorar; "refazer tipo").
    `GuardaGrupos` (IFailuresPreprocessor): apaga avisos, desfaz "unidos mas não se tocam"
    (JoiningDisjoint por reflexão + resolução UnjoinElements por TryParse), qualquer ERRO → rollback.
    Lotes de 250 com bissecção. Grupo: tenta direto (1 cópia); várias cópias → refaz o tipo
    (UngroupMembers, unir, NewGroup, trocar GroupType das outras cópias, mover pela diferença do centro
    dos membros, apagar tipo antigo, renomear) num TransactionGroup; pula aninhados, membros
    excluídos e detalhe anexado. Mistos (grupo × fora) depois dos grupos. Não compilado.

### JA Tools — Paredes nas Lajes (1.27.0, 06/11/2026)
49. Pedido: "ferramenta para verificar paredes que estejam atravessando lajes e ajustar suas alturas
    para ficarem rente à laje" (mesmo cuidado com grupos). Feito: `Tools/Modeling/ParedesNasLajesCommand.cs`
    (painel Modelagem, ícone "paredesLajes"). Mede a laje em 7 pontos ao longo da parede (linha vertical
    no eixo e ao lado das duas faces × sólido da laje); topo dentro da laje → rente à face de baixo;
    opções: subir parede curta até a laje (folga mm), subir base que começa dentro da laje de baixo
    (desligado), considerar telhados, não mexer em parede de vários andares (mm), modo altura exata
    (delta em altura desconectada ou deslocamento superior) ou anexar topo (AddAttachment por
    reflexão), só verificar, selecionar ajustadas/atenção. Lista: perfil editado, já anexada, laje
    só sobre parte, laje inclinada (modo exato). Grupos: helper novo `Tools/Modeling/GruposSeguros.cs`
    (GuardaGrupos + RefazerTipo, agora usado também pelo Unir Elementos); várias cópias só se todas
    precisarem do mesmo ajuste. Não compilado.

50. 1.27.0 compilou e rodou (Revit 2026.5). 1.27.1–1.27.2: `ParedesAmbiente.CriarElevacoes` cria **um
    marcador por lado** no centro (sobrepostos), só o índice 0 (o único cuja direção o Revit lê certo),
    um Regenerate, gira cada um até a parede — testado OK em ambiente girado e trapézio. Espaço fecha a
    Varinha: `VarinhaAtalho.Encerrar` manda Esc fora do gancho (thread, PostMessage + keybd_event) e a
    tecla é engolida durante a sessão (não testado).
51. 1.27.3–1.27.4: Cotas por Ambiente em ambiente sem paredes paralelas: `DimLados` cota o comprimento
    livre de cada lado entre as arestas verticais dos cantos (`CantoVertical`; aceitas em planta,
    testado). Lado pulado só se os dois vizinhos forem perpendiculares e já cotados entre si. Vale também
    no Detalhar Ambientes e Anotar Vista. 1.27.4 não testada.
52. 1.27.5 — Pedido: estudar a vista "FORNECER CLAUDE" do RVT CVIVA-IGLF-ARQ-PE-0000-MOD-GERL_R07.rvt
    (Revit 2026, 400 MB) para o Detalhar Ambientes gerar vistas parecidas, "com níveis e afins". Na nuvem
    não dá para abrir RVT; pelo binário (olefile + gzip, strings UTF-16) só se vê que é planta em
    "EX - EXECUTIVO / 1 - Civil / 1 - Plantas" (partição 1611), com vínculos ESG/ESP e DWGs de esgoto.
    Criado **Estudar Vista** (`Tools/Views/EstudarVistaCommand.cs`, painel JA, ícone "estudar"): JSON em
    Documentos\JA Tools\Estudo de vistas (vista, modelo, faixa, recorte, filtros, categorias, prancha,
    parâmetros; cada elemento da vista com família/tipo/texto/parâmetros/posição mm no recorte; tipos
    usados; ambientes; modelo visível; vínculos) + PNG 4000 px. 1.27.5 compilou com 1 erro só:
    `PlanViewRange.SameLevel` não existe (→ `LevelBelow`/`Current`) = 1.27.6. Aguardando os arquivos.
53. 1.28.0 — 1.27.6 compilou; o usuário rodou Estudar Vista em FORNECER CLAUDE (planta 1:50, Fino,
    tipo "LB. Planta. Nivel Acabado", modelo "LB - PE - Plantas. Civil", faixa 2800/1500/-60/-60) e pediu
    "não considere o LB no nome das coisas". Achados: etiqueta de ambiente IND NOME AMBIENTE : NOME E AREA;
    1 cota de nível "JA - Nível (Relativo)" por ambiente no piso (~35 cm à esq., 55 cm abaixo da etiqueta;
    base relativa = nível "... - Osso"; P.A./P.O.); "Soleira/Filete - Relativo a Nível" nas soleiras;
    cotas lineares EXECUTIVO - 1,5mm_RomansD (paredes, pisos, portas); cotas angulares
    "LB_1,5mm_vermelho" nos cantos de 86°/94°; Tag Porta EX : Com Soleira; Tag Janela EX : Com/Sem
    Peitoril; TAG - Identificador de Guarda corpo; identificadores JA de hidráulica/tubos. Feito:
    `Tools/Detail/PadraoPlanta.cs` (Norm sem LB/acentos, Melhor; NivelDoAmbiente, NiveisDasSoleiras,
    AngulosDoAmbiente) e Detalhar Ambientes com tipo de vista da planta + padrões por nome + 3 opções
    novas (nível do piso, soleiras, angulares); guarda-corpos nas categorias de etiqueta. Não compilado.
54. 1.29.0 — "renomeie essas famílias e elementos de forma mais técnica e clara". Padrão "JA - [o quê] -
    [variante]": `Tools/Common/PadraoNomes.cs` (regras antigo→novo: etiquetas JA - Etiqueta - Ambiente/
    Porta/Janela/Guarda-corpo/Equipamento Hidráulico…, tipos Nome + Área, Código + Soleira, Código +
    Peitoril, Código; JA - Nível - Piso Acabado (P.A./P.O.), JA - Nível - Soleira; JA - Cota Linear -
    Executivo/Detalhe 1,5 mm, JA - Cota Angular - 1,5 mm Vermelha; JA - Texto x mm - Arial/RomansD;
    JA - Planta - Nível Acabado; JA - PE - Planta Baixa Civil). Comando **Padronizar Nomes**
    (`Tools/Parameters/PadronizarNomesCommand.cs`, painel Dados, ícone "padronizar"): lista e renomeia
    (só nomes). PadraoPlanta e TagLibrary aceitam nomes novos e antigos. Não compilado.
55. 1.29.1 — "otimizações para acelerar a ferramenta de união" (Unir Elementos): lê paredes/pisos/
    pilares/vigas uma vez (papel + caixa; antes varria TODOS os elementos do projeto e abria um
    coletor por elemento), vizinhos por grade 2 m em memória (lajes enormes numa lista à parte), pares
    já unidos na ordem certa pulados antes de qualquer teste de geometria, parede×parede só pelo teste
    barato, demais vizinhos testados num único coletor por elemento (ElementIntersectsElementFilter),
    lotes de 1000 pares soltos. Não compilado.

### JA Famílias — Ralos (1.7.0, 06/11/2026)
47. 1.7.0 — "ralos lineares de tamanhos diversos, ralos simples e outros; os ralos devem ser conexões de
    tubo". Painel **Ralos**, `NovoRaloCommand`, `TipoPeca.Ralo` (RL), `Core/Pecas/RaloConfig.cs`: linear,
    quadrado, redondo, caixa sifonada (0–4 entradas), hemisférico; origem = topo da grelha no piso acabado;
    grelha fendas/furos/quadriculada/revestível; saída DN40–150. `PecaGenerator`: OST_PipeFitting MultiPort,
    `ConnectorElement.CreatePipeConnector` na face da ponta. Harness `/home/claude/hr`. Não compilado.

### JA Famílias 1.8.0 (11/11/2026)
56. Pedido: bancadas com profundidade por trecho do L/U, saia/frontão por trecho e lado, cubas duplas;
    "filetes, soleiras e peitoris em todas as janelas e portas existentes" (resposta: **todas do modelo** —
    JA liga na própria família, outras ganham peça separada); "substituir portas e janelas, bancadas e
    outros elementos ... já presentes no modelo" (resposta: **trocar pelos do JA**). Feito:
    - `BancadaConfig`: Cuba.Dupla/SeptoMm/ProporcaoPct (DM 9–11); ProfundidadeBMm/CMm (0 = igual A),
      listas SemBorda/SemFrontao com chaves A, B, C, Esq, Dir (DM 70–73; listas = exclusão, padrão seguro
      no DataContract). Harness `/home/claude/hbd`. PecaWindow: campos por trecho, card "Por trecho e
      lado", cuba dupla. Modelos B-COZ-DUPLA e B-COZ-L-PEN.
    - `TipoPeca.Pedra = 4` (SL, Componentes de gabinete), `Core/Pecas/PedraConfig.cs` (Soleira/Filete/
      Peitoril; origem no centro do vão no eixo da parede, topo em Z = 0, Y+ = exterior; família
      "JA Soleira SL01 800x150x20").
    - `Commands/PedrasCommands.cs`: **SoleirasPeitorisCommand** (painel Pedras e Nichos): escopo seleção/
      vista/projeto; JA → muda a config e `FamilyGenerator.Generate` com os nomes atuais; outras → centro
      da parede por WALL_KEY_REF_PARAM + camadas, largura do vão (rough/width), z = nível + peitoril, uma
      família por tamanho (+ " e±N" no filete fora do centro), gira para w.Orientation; pula vãos com peça
      a 15 cm (ou "Refazer"). **SubstituirPorJaCommand** (painel Gerenciar, "Substituir pelo JA"): portas/
      janelas → `OpenConfigurator(..., paineis, cortina: false)` → TrocarPaineis (um tipo por tamanho,
      ChangeTypeId); bancadas → caixa da geometria no sistema da família, altura = topo acima do nível,
      origem (X0, Y1) ou (X0, Y0) se espelhada, rotação de BasisX, apaga a antiga. Nichos/ralos não.
    - Ícones "soleira"/"substituir", ToolCatalog, README "Novidades 1.8.0". Zip
      `JAFamilias_1.8.0_completo.zip`. Não compilado.

### JA Famílias 1.9.0 (13/11/2026) — "atenda as pendências registradas"
57. **Painéis de parede cortina** (pendência 20). Botão **Painel de Cortina** (painel Esquadrias, `NovoPainelCommand`,
    ícone "painel"). `Core/Paineis/PainelConfig.cs`: TipoPainel Solido/Vidro/Sanduiche/Cassete/Veneziana/Brise/
    Emoldurado; coordenadas x (largura, vista de fora), y (altura), p (através, 0 = linha da grade, + exterior);
    `SolidoPainel` com Eixo Profundidade (perfil x,y), Largura (perfil p,y — aletas horizontais) ou Altura
    (perfil x,p — brise); junta recua metade em cada borda; cassete = face + 4 retornos (profundidade total
    centrada no deslocamento); aletas inclinadas (+ = borda externa para baixo / para a direita), passo
    ajustado para preencher por igual, chapa fina só no detalhe baixo; moldura opcional (emoldurado sempre,
    com travessas e vidro/cego). Conferido em Python (`/home/claude/tools/painel_check.py`).
    `Engine/PainelGenerator.cs`: modelo "Curtain Wall Panel" (`TemplateLocator.FindCurtainPanel`, exclui
    Pattern Based; `Settings.TemplatePainelCortina` + campo em Configurações), uma família por tamanho
    "JA Painel PN01 1250x3000" (tipo e marca = código; pasta Familias\Paineis), Width/Height como dica,
    esquerda/base pelos planos W/H (como `ReadCurtainPanel`), exterior −Y (opção Inverter), parâmetros de
    material por slot, JA_Config (com o tamanho), JA_Familia = "JA Painel", JA_Material, JA_Dimensoes.
    `UI/PainelWindow.cs` (frente + corte vertical/planta ao vivo, cartões só do tipo, `MedidaPainel`),
    `Commands/PainelCommands.cs` (partial `EsquadriaCommandBase.AbrirPainel`: seleção de painéis ou da parede
    cortina inteira, um tamanho por família, ChangeTypeId; editar = refaz todos os tamanhos do código).
    Editar Família reconhece painéis; Quadro de Peças ganhou "Painéis de cortina" (`ScheduleBuilder.CreatePaineis`).
58. **Folha/marco "soltos" na planta** (pendência 21): sem o print, não deu para reproduzir — o relato é da
    1.1.0/1.2.0, antes da planta ancorada (1.3.0), em que todas as linhas do marco/folha/vidro ficam num
    mesmo grupo aninhado (mesma âncora) e não podem se deslocar umas das outras. Feito em vez de adivinhar:
    `Engine/ConferenciaPlanta.cs` (linhas esperadas do `Layout` paralelas à parede × linhas medidas nos
    estilos "JA Planta*", casadas pela mesma extensão ao longo da parede ± 1,5 mm, tolerância 0,8 mm;
    no projeto testa espelhada/virada e alinha x pela 1ª linha). `FamilyGenerator.Builder.Conferir()`: depois
    da planta, mede na família na parede do modelo e com parede +60/−40 mm e revestimentos trocados
    (tipo de parede duplicado, tudo em transação desfeita) → aviso + registro. Comando **Conferir Planta**
    (painel Gerenciar, `ConferirPlantaCommand`, ícone "conferir"): planta ativa, seleção ou toda a vista,
    usa `SoleirasPeitorisCommand.Vao` (centro/exterior/espessura reais) e os revestimentos da instância;
    relatório por código; "Gerar de novo e conferir" regenera os tipos com diferença e confere de novo.
    `FamilyGenerator.NomeGrupoPlanta` virou público. Se o usuário ainda vir diferença: pedir o registro.
    Não compilado.

### 13/11/2026 (tarde) — JA Famílias 1.10.0 e JA Tools 1.30.0
59. Pedido (lista): bancadas com profundidade por trecho, saia/frontão por trecho e lado, cubas duplas → já
    feitos na 1.8.0 (item 56), só faltam compilar/testar; nada mudou.
60. **Portas de correr para os dois lados ou recolhendo para um lado com uma folha fixa** (JA Famílias 1.10.0):
    `ModoCorrer { Padrao, Central, Recolhe }` em `Celula.Modo` (DM 7) + `Celula.RecolheDireita` (DM 8); Central
    ímpar vira par (3→4) no Normalized. `Layout.FolhasCorrer` monta as folhas (Rc, trilho, sentido, fixa, lado
    do puxador): Padrão idêntico ao antigo (mesmos prismas e ordem); Central = n/2 por lado, as do meio se
    encontram no centro no mesmo trilho (n/2−1), as das pontas fixas com 4/6, trilhos = n/2, abre (h−1)/h
    (2 folhas: 1,0 + aviso “correm para fora do vão”); Recolhe = fixa no trilho 0 no lado escolhido, folha j no
    trilho j, todas para o lado da fixa, abre (n−1)/n. Fixa: “F” (MarcarFixo e vidro), sem seta/puxador/seta de
    planta. Aviso se a folha ficar < 60% da profundidade do perfil. ConfiguratorWindow: pills Movimento,
    Folhas (2/4/6 no central), Recolhem para (Esq/Dir); “Aplicar a todos” copia o modo. ResumoAberturas com o
    modo. Modelos J-CORRER4-CENTRAL, P-CORRER4-CENTRAL, P-CORRER6-CENTRAL, P-CORRER3-RECOLHE, P-CORRER4-RECOLHE.
    Conferido em Python (`/home/claude/tools/correr_check.py`). Não compilado.
61. **Exportar Tabelas: dados em vez de texto** (JA Tools 1.30.0): o XlsxWriter só aceitava número em formato
    invariante (pt-BR “12,50 m²” virava texto e “1.250” virava 1,25). `LeitorNumero` (em XlsxWriter.cs): moeda
    (R$, US$, $, €, £) + sinal (−, -) + inteiro com/sem agrupamento + decimais + unidade de uma lista
    (mm, cm, m, m², m³, %, °, kg, un, pç, W, kW, ft…); separadores lidos de `Doc.GetUnits()` (DecimalSymbol /
    DigitGroupingSymbol por ToString); zeros à esquerda, códigos (J01, 101A), datas, frações, pés-polegadas
    ficam texto; % vira fração com formato 0%. Formato numérico próprio por célula (numFmts 164+, cellXfs 5+
    por estilo base corpo/zebra) — styles.xml escrito depois das abas. Opção “Números como dados” (padrão
    ligada). Conferido em Python (`/home/claude/tools/leitor_check.py`). Não compilado.

### JA Famílias 1.11.0 (13/11/2026) — chapim em parede curva
62. Pedido (com print do CAD): "os chapins não são gerados em paredes curvas, apenas formam uma reta" (família
    baseada em linha só segue reta; antes ia na corda). Feito: `ChapimConfig.RaioMm` (DM 45, 0 = reto),
    `ExteriorParaFora` (46), `AnguloGraus` (47); `Build` curvo = `Peca.Revolucao` (perfil em (raio ± y, z),
    + y = exterior). `PecaGenerator`: chapim curvo usa o Modelo genérico LIVRE (categoria Gabinete),
    `NewRevolution` (eixo Z na origem, de 0 ao ângulo, anti-horário a partir de +X), parâmetros
    `ParamAngulo` "Ângulo do arco" (instância, ligado ao REVOLUTION_END_ANGLE) e `ParamRaio` "Raio do eixo"
    (tipo); JA_Comprimento = fórmula "Raio do eixo * Ângulo do arco / 180° * pi()" (ou valor gravado na
    instância). `GenerationResult.AnguloParametrico`. `ChapimPlacer.Arco(wall)` (centro, raio do eixo pelo
    CentreShift, lado externo por Orientation × raio no meio do arco — senão sentido + Flipped —, início e
    varredura anti-horários) e `PlaceArc` (no centro, gira pelo início, ângulo, altura como o reto).
    `ApplyChapim`: arcos separados, uma família por espessura|raio|lado (código CH01[-esp]-R<raio>[i]); sem
    ângulo paramétrico, uma família por ângulo (-A<graus>). Editar curvo: prévia com RaioMm = 0, mantém
    raio/lado/ângulo/espessura, não reaplica; editar a base refaz as variações curvas. Ângulos conferidos em
    Python (`/home/claude/tools/arco_check.py`). Não compilado.

63. 1.11.1 — 1ª compilação da 1.9–1.11: só CS0246 `Result` em ConferirPlantaCommand (faltava
    `using Autodesk.Revit.UI;`). Regra: todo comando novo importa Autodesk.Revit.UI (Result, TaskDialog).

### JA Famílias 1.12.0 (16/11/2026) — Bancada no Local e áreas seca/molhada
64. Pedido (com print de uma cozinha com parede inclinada e pilares P204/P206): "ferramenta para o usuário
    modelar no local a família, para se ajustar ao entorno" + "áreas de bancada seca e molhada". A 1.11.1
    compilou? (o usuário não mandou mais erros depois do `using` — presumir que sim, não confirmado).
    **Modelo de dados** (`BancadaConfig`, tudo DataMember novo): `FormatoBancada.Livre = 3`; `Contorno`
    (List<Pt>, mm, eixos = X/Y do projeto, origem = 1º canto, no nível); `Arestas` (List<BordaLocal>:
    Livre, LivreSemAcabamento, Parede, ParedeSemFrontao, Pe — aresta i = Contorno[i]→[i+1]); `Divisoes`
    (List<Divisao{Pontos}>: caminho de borda a borda); `Molhadas` (um ponto dentro de cada área molhada);
    `RebaixoMm` (0 = só dividida), `ContencaoMm`; para Reta/L/U: `MolhadaDeMm`/`MolhadaAteMm` (faixa do
    trecho A, vira 2 cordas verticais); `Cuba.PosicaoYMm`/`AnguloGraus` e `CooktopPosicaoYMm`/
    `CooktopAnguloGraus` (no Livre a posição é absoluta e gira em torno do centro).
    **Geometria**: `Build()` reescrito em 3 passos — `Contornar` (contorno + `ArestaBancada` com flags
    Parede/Frontao/Borda/Pe; o código antigo de Reta/L/U ficou igual), `Zonear` (corta o contorno pelas
    divisões: `Dividir` insere as pontas na borda e separa em dois polígonos, arestas novas = Divisa;
    confere sinais das áreas) e por zona: tampo em Topo = H − rebaixo (laje inteira desce, espessura
    constante), frontão de Topo até H + altura, saia com a base alinhada (H − e − altura), tento ou borda
    de contenção de Topo até H, chanfro por zona, espelho no degrau se rebaixo > e − 2. Furos (cubas,
    cooktop, torneira) vão para a zona que contém o centro. `Zonas()` e `AjustarDivisao()` públicos para a
    janela/comando. `Core/Pecas/Poligono.cs`: Dentro, DistSeg, DistBorda, Cortes (reta × polígono),
    Interior, Ajustar (2 pontos = corda da reta que contém o meio dos cliques, tolera cliques fora; 3+ =
    pontas encaixadas na borda até 60 mm), Girar, Envolvente (caixa alinhada à maior aresta → Dimensoes).
    `PecaModelo.AreaMolhada`, `SharedParams.AreaMolhada` (JA_AreaMolhada, GUID 7c1e4b52-…), coluna no
    Quadro de Peças quando existe. Conferido em Python (`/home/claude/tools/zonas_check.py`).
    **Revit**: `Engine/EntornoBancada.cs` (faces planas verticais de paredes/pilares perto do contorno,
    na altura do tampo → `Detectar`: paralela, ≤ 15 mm, olhando para o tampo, ≥ 60 % da aresta = Parede;
    também desenhadas em cinza na janela). `Commands/BancadaLocalCommand.cs` (partial
    `EsquadriaCommandBase.AbrirBancadaLocal`): contorno por cliques (`PickPoint` em laço dentro de um
    TransactionGroup com linhas de detalhe magenta temporárias, RollBack no fim) ou por linhas
    selecionadas (`Encadear`, arcos tesselados a cada ≥ 30 mm), `ValidarContorno` (≥ 3 cantos, área,
    sem cruzar); laço janela ⇄ projeto (`NoProjeto`: Contorno, Divisao — termina sozinha ao clicar numa
    borda —, Cuba, Cooktop com `AnguloParaParede`); gera com `PecaGenerator` (modelo genérico livre) e
    cria a instância no nível da planta em T.Origin, corrigindo a posição pelo GetTransform. Edição: T =
    transform da instância (aceita movida/girada/espelhada). `UI/BancadaLocalWindow.cs`: planta própria
    com clique em borda (tipo, multi com Ctrl) e em área (molhada/seca), 3D pelo PecaRenderer (sem
    contexto), cartões Modelar no projeto / Borda / Áreas / Medidas / Bordas / Cubas / Cooktop /
    Materiais / Identificação. Editar Família e o próprio botão com a bancada selecionada reabrem.
    `PecaWindow`: cartão "Áreas seca e molhada" na aba Medidas; bancadas Livre não aparecem em "No
    projeto" nem como ponto de partida da Nova Bancada. Pref "ultima.bancada.local", dica
    "bancada.local.dica". Botão no painel Pedras e Nichos, ícone "bancadalocal". Não compilado.
    Dúvidas a confirmar no teste: PickPoint dentro de TransactionGroup; NewDetailCurve no Z do plano da
    vista; paredes de vínculo não são detectadas.

65. 1.12.1 (16/11/2026) — a 1.12.0 **compilou e gerou** a bancada no local (print do usuário em 3D, com
    cooktop e recortes de pilar). Queixa: "frontões e saias não se unem" (cada peça era um retângulo por
    aresta → vão nos cantos reentrantes, sobreposição nos salientes, pior fora de 90°). Feito:
    `Poligono.Faixa(poly, sinal, i, o, w, ini, fim)` — faixa ao longo da aresta com as pontas cortadas pela
    reta da aresta vizinha deslocada {c0, c1}: {on, on+wn} = meia-esquadria com a faixa vizinha, {0,0} =
    rente à aresta vizinha, {wn,wn} = encosta na face de uma peça vizinha; rente/encosto só em canto
    convexo (reentrante = ponta reta); quase paralelas (< ~1,1°) ou cruzamento longe (> 6·largura) =
    ponta reta; `Poligono.Vizinha` pula arestas < 0,5 mm. Em `Build`, a função local `Faixa(nome, i, w,
    z0, z1, corte, o)` substituiu `Strip`: CorteFrontao (esquadria com frontão), CorteSaia (esquadria;
    encosta no pé), CortePe, CorteBorda(o, w) para tento/contenção (esquadria; encosta no frontão). Aresta
    com pé não leva mais saia. As cunhas do chanfro (`Cunha`) continuam com ponta reta (pendência menor).
    Conferido em Python (`/home/claude/tools/faixa_check.py`: juntas coincidem e a soma das faixas = anel
    exato). Não compilado.

66. 1.12.2 (16/11/2026) — pedido (print de ilha 2,50 × 0,90 com cooktop): "opção de adicionar saia dos dois
    lados". `BancadaConfig.FundoLivre` (DM 90): arestas Fundo e Parede (lados de parede dos retornos em L/U)
    deixam de ser parede (Chave "Fundo" para o SemBorda) → saia/tira/tento/chanfro, sem frontão. UI: check em
    Encostos + linha "Fundo (livre) com saia" em Por trecho e lado; renderer não desenha a parede do fundo.
    Modelos: B-ILHA ganhou FundoLivre; novo B-ILHA-SAIA. Não compilado.

67. 16/11/2026 — o usuário pediu sugestões de melhoria e depois "adiciona as funcionalidades, sem os testes
    de estabilidade". Entrega em lotes. **Lote 1** (não compilado): JA Tools 1.31.0 — Exportar Tabelas com
    "Linha de totais com fórmula (SOMA)" (`XlsxWriter.Totais`: colunas só numéricas, não soma %; desligado
    por padrão porque o total geral do Revit seria somado de novo) e "Salvar também em CSV"
    (`XlsxWriter.SaveCsv`, ";" e BOM). JA Famílias 1.13.0 — `ListaMarmorariaCommand` (DocCommands.cs, painel
    Documentar: peças Slot "pedra" de cada `Config.Build()`, agrupadas, medida = caixa envolvente, CSV em
    PastaRelatorios) e furos livres na Bancada no Local (`Furo{X,Y,DiametroMm}`, `BancadaConfig.Furos` DM 91,
    `AcaoBancadaLocal.Furo`, cartão Furos). **Fila dos próximos lotes** (sugestões aceitas): contorno
    automático da bancada por clique no ambiente; chanfro em meia-esquadria; plano de corte (emendas por
    chapa); bancada no local para soleiras/nichos; puxadores/ferragens; vidro com especificação e área no
    quadro; pingadeira/contramarco; cortes de detalhe automáticos; ACM paginado na fachada; brise em parede;
    elevação no quadro; orçamento; JA Tools: importar do Excel, verificador de padrões, prévia em Unir
    Elementos/Padronizar Nomes; instalador único/aviso de versão.

68. 16/11/2026 — "Faça todo o restante pro próximo lote". **Lote 2** (não compilado):
    JA Famílias 1.14.0 — `OrcamentoCommand` (DocCommands.cs; preços no OptionsDialog "Orcamento"; esquadria =
    Largura×Altura×Instancias; pedra = `PecaModelo.AreaPedra`×Instancias, chapim = AreaPedra (por metro) × soma
    de JA_Comprimento das instâncias; cubas por unidade; CSV em PastaRelatorios); Lista para a Marmoraria com
    "Comprimento máximo de uma peça" → observação "N partes… (emendas)"; `EsquadriaConfig.Ferragens` (DM 124)
    → `SharedParams.Ferragens` (JA_Ferragens, GUID novo) → coluna no Quadro de Esquadrias; Bancada no Local:
    ambiente (ou etiqueta) selecionado antes → `ContornoDoAmbiente` (boundary Finish, maior laço).
    JA Tools 1.32.0 — `VerificarModeloCommand` e `PlanilhaParametrosCommand` (Tools/Parameters; painel
    Exportar; gerar CSV por tabela com coluna ID / importar .csv/.xlsx com prévia em Multi, até 400 itens
    listados; limite de células do `PlanilhaLeitor`); Unir Elementos com prévia (`fPrevia`, confirmação por
    tipo; `ToolProgress.Hide`). Padronizar Nomes já tinha prévia (lista marcável); puxadores e vidro
    (JA_Vidro, JA_AreaIluminacao) já existiam.
    **Não feito, continua na fila** (precisa de geometria nova e de teste no Revit a cada passo): chanfro em
    meia-esquadria (a `Cunha` é extrusão reta de perfil triangular; o canto exige sólido de mescla/varredura
    que o `PecaGenerator` não tem); bancada no local para soleiras/nichos; pingadeira/contramarco; cortes de
    detalhe automáticos; elevação no quadro; ACM paginado; brise ao longo de parede; plano de corte gráfico
    (encaixe das peças na chapa); instalador único/aviso de versão (sem servidor).
    O lote 2 não chegou a ser enviado: foi junto com o lote 3.

69. 17/11/2026 — "Faça todo o restante pro próximo lote" (de novo). **Lote 3** (não compilado), JA Famílias
    1.15.0 + JA Tools 1.33.0, levando também o lote 2:
    - Chanfro meia-esquadria: `Peca.Topo` (perfil de cima) + `Peca.Alternativa`; `PecaGenerator.MakeBlend`
      (`NewBlend(sólido, topo, base, plano)`; linhas < ShortCurveTolerance → null → peça reta). Em
      `BancadaConfig`, `CunhaMeiaEsquadria(pts, dentro=Recuar(...), i, ...)`: base = faixa P_i,P_i+1,In_i+1,In_i
      no z0; topo = faixa de 2 mm junto da linha interna no z0+alt. Só no tampo (o chanfro no tento continua
      reta). `PecaRenderer` desenha a mescla. Lista para a Marmoraria ignora "Chanfro da borda" e "(topo)".
    - `Core/Pecas/PlanoCorte.cs` (prateleiras, gira 90°, por material) → HTML em PastaRelatorios.
    - Bancada no Local: cartão "Usar como" (`Preset`); `Normalized` aceita AlturaMm ≥ espessura no formato Livre.
    - `EsquadriaConfig` DM 125 PeitorilPingadeira, 126 Contramarco, 127 ContramarcoLarguraMm, 128
      ContramarcoProfMm; `Layout`: peitoril em 3 caixas com o sulco; contramarco = caixas Papel.Marco fora de
      W×H + Vazios do mesmo tamanho (cortam a parede, como o peitoril) + linhas de planta.
    - `Commands/DetalheCommands.cs`: `ImagemDoTipo` (ElevationRenderer → PNG em PastaRelatorios\Elevações →
      ImageType → ALL_MODEL_TYPE_IMAGE; coluna "Elevação" com `ScheduleBuilder.Create(..., elevacao)`);
      `CortesEsquadriasCommand` (ViewSection.CreateSection com BasisX = FacingOrientation, cortando no meio;
      CreateCallout na planta do nível; `WallOffset` = linha de locação → centro).
    - `Commands/FachadaCommands.cs`: `FachadaPaginadaCommand` (tipo de cortina "JA Fachada paginada" sem grade;
      Wall.Create na face externa + afastamento; AddGridLine conferindo se saiu horizontal/vertical; depois
      `AbrirPainel(paineis)` com o tipo escolhido em "ultimo.painel"). Brise ao longo de parede = este comando
      com Brise/Veneziana.
    - Aviso de versão (`AvisoVersao`, nos dois) + `GerarPacoteEscritorio.bat` no JA Tools.
    **Ainda na fila**: nicho no local (vazio na parede por cliques); chanfro em meia-esquadria no tento.

70. 17/11/2026 — "Faça o que falta". JA Famílias 1.16.0 (não compilado): `NichoNoLocalCommand` (PecaCommands.cs;
    2 × PickObject(ObjectType.Face) na mesma parede → largura ao longo da face, altura, base; `OpenPeca(cfg,
    null, colocar)` com o delegate novo; `Colocar`: NewFamilyInstance hospedado, INSTANCE_ELEVATION_PARAM,
    flipFacing se o corpo ficou fora da face, move ao longo da parede até centrar). Chanfro do tento em
    meia-esquadria: `CunhaMeiaEsquadria(foraTento, dentroTento, ...)` com Recuar (livre: 0/th; parede com
    frontão: fe/fe; resto 0). A fila de sugestões ficou vazia.

71. 17/11/2026 — compilação da 1.16.0 falhou: CS0104 `RenderOptions` ambíguo (JAEsquadrias.UI × System.Windows.Media)
    em DetalheCommands.cs → alias `using RenderOptions = JAEsquadrias.UI.RenderOptions;` (1.16.1). Arquivos fora
    do namespace UI que importam System.Windows.Media precisam de alias para RenderOptions, Transform e View.

72. 17/11/2026 — pedido: "opção de mudar os níveis por elementos selecionados/categorias". JA Tools 1.34.0
    (não compilado): `Tools/Parameters/MudarNivelCommand.cs` (painel Dados, botão Mudar Nível): escopo
    seleção/vista/projeto + categorias (Multi) + origem (qualquer nível ou um nível) + destino + "também o
    topo". `LevelRehost.Revincular` (novo, genérico; `TemNivel`; pula grupos; conta JaNoNivel/EmGrupos);
    `Move` (Editar Níveis) passou a usá-lo com pularGrupos=false (comportamento antigo).

73. 17/11/2026 — "Incorpore essas implementações menos a numeração amarrada ao ambiente" (lista de sugestões
    1-6, 7, 8, 10, 11). Não compilado. JA Famílias 1.17.0:
    - `PedraConfig`: Uso Rodape (3, baseado em linha, `Linear`; perfil YZ extrudado em +X, LarguraMm = altura,
      AreaPedra por metro) e Degrau (4; origem no bocel, piso em +Y, espelho recuado ProjecaoMm; DM 17
      AlturaEspelhoMm, 18 EspessuraEspelhoMm). `PecaWindow.PedraMedidas` com cartões próprios para os dois.
    - `TipoPeca.Vidro = 5` + `Core/Pecas/VidroConfig.cs` (UsoVidro GuardaCorpo/Box; slots "vidro"/"metal";
      box: +Y = dentro do box, puxador e giro para -Y). `PecaUtil.Linear(cfg)` decide template de linha e
      `SetupLine` no `PecaGenerator`. `ProjectPecas.NextCode(used, prefixo)`. Editar Família → `AbrirVidro`.
    - `Commands/VidroCommands.cs` (partial base: `PegarPontos`, `NivelDaPlanta`, `ColocarNoPonto`,
      `DialogoVidro`, `MmFt`; `GuardaCorpoCommand`, `BoxBanheiroCommand`); `ChapimPlacer.PlaceLine`.
    - `Commands/RevestimentoCommands.cs` (`AmbientesSelecionados`, `Contornos`, `CorDaPedra`; `RodapeCommand`
      com cortes de porta por projeção do ponto da porta na parede; `EscadaRevestidaCommand` pelas faces
      planas de topo de cada StairsRun reto, agrupadas por largura×profundidade×espelho).
    - `Core/Paginacao.cs` (grade girada, linhas de junta recortadas par-ímpar, contagem por Sutherland–Hodgman;
      testado em Python: 3000×2000 com 600+2 → 12 inteiras + 8 recortes) + `Commands/PaginacaoCommand.cs`
      (planta: ambientes; ViewSection: face plana de frente; coordenadas da vista O/R/U; estilo "JA Paginação").
    - `Commands/EsquadriaLoteCommands.cs`: `IluminacaoVentilacaoCommand` (FromRoom/ToRoom; JA = Layout.Build
      áreas; outras = vão × %), `TrocarAcabamentoCommand` (ApplyPerfil/CorId/VidroId/Ferragens → regenera).
    - Lista para a Marmoraria: peças corridas por JA_Comprimento das instâncias (`Comprimentos(sym)`), no plano
      de corte; Orçamento usa `PecaUtil.Linear`; Quadro de Peças com Comprimento total em Pedra e Vidro.
    - Painel novo "Revestimentos e Vidros"; Trocar Acabamento em Gerenciar; Iluminação em Documentar.
    JA Tools 1.35.0: `Tools/Parameters/QuadroAcabamentosCommand.cs`, `QuadroAreasCommand.cs` (painel Dados),
    `Tools/Dimensions/EtiquetarCotarLoteCommand.cs` (painel Cotas), Verificar Modelo com `Corrigir`.
    Pontos de risco no Revit: NewBlend do chanfro (lote anterior), faces de topo dos lances (escadas
    monolíticas × montadas), orientação das famílias de linha (rodapé deve ficar do lado do ambiente),
    posição do texto da paginação em elevação.

74. 17/11/2026 — "apenas a opção do usuário fazer um croqui pelo JA Famílias e um ralo linear ser criado com base
    no croqui". JA Famílias 1.18.0 (não compilado): `RaloConfig.Caminho` (DM 50, List<Pt> em mm, 1º ponto = origem
    da família), `NoCroqui`, `ComprimentoCaminho()`; `Build` → `Croqui()`: `Faixa(caminho, largura)` (contorno em
    meia-esquadria, testado em Python no L 1000+800 × 70 → área 126000 mm²), `Aparar` (recuo nas pontas),
    `FurosCaminho` (aberturas por trecho, longe dos cantos), moldura/grelha/bandeja/corpo, saída no ponto do
    traçado (Posicao: Centro = meio, Esquerda = início, Direita = fim). `RaloCroquiCommand` (PecaCommands.cs; linhas
    selecionadas → `EncadearCaminho`, senão `ClicarPontos` com traço provisório) → `OpenPeca(cfg, null, colocar)` →
    `ColocarNoPonto` na origem, sem rotação. PecaWindow: no croqui, sem campo Comprimento e saída rotulada
    Meio/Início/Fim. Botão "Ralo no Croqui" no painel Ralos.

75. 17/11/2026 — "implemente todos" (14 melhorias no que já existe). Não compilado.
    JA Famílias 1.19.0:
    - `Common/JaDados.cs`: extensible storage (schema 3b6f2c1e…, campo Json) com `DadosJa` (Tipo, Ambiente, Vista,
      Face, Config, AreaM2, CompraM2, Peca, Inteiras, Recortes). Usado por paginação (grupo), rodapé (instância) e
      regiões da iluminação.
    - `Infrastructure/XlsxWriter.cs` (cópia do JA Tools; csproj net48 com System.IO.Compression).
    - Paginação: reescrita (`ConfigPaginacao` no grupo; refazer pela seleção do grupo; substitui a do ambiente/face;
      ralos como furos via `RaloConfig.ContornoEmPlanta()` × `GetTransform()`).
    - Rodapé: apaga os antigos do ambiente (JaDados), obstáculos = bbox de casework/mobiliário/genéricos/louças na
      faixa de 15 cm e altura do rodapé (exclui JA pedra/vidro/ralo/chapim), ROOM_FINISH_BASE.
    - Escada: `UsoPedra.Patamar = 5` (degrau sem espelho, LarguraMm até 15000), face de topo do StairsLanding no
      sentido do 1º lance, retângulo (área ≥ 97%); Blondel 630–650, espelho ≤ 180, piso ≥ 280.
    - Box: `VidroConfig.Canto/LadoFixoMm/LadoSinal` (DM 32–34), `BuildBox` com `Tr` (x+W/2, y·sinal), origem no canto;
      `PegarPontos(Func<int,string>, max)`.
    - Guarda-corpo: `ModuloMm/FolgaMm/Partes` (DM 24–26; Perfis / Vidro), duas famílias quando modulado;
      `RaloCroquiCommand.EncadearCaminho(curvas, cordaMm, ...)` agora internal e amostra curvas.
    - Ralo: `BordaNoCaminho/AfastamentoMm/CaimentoPct` (DM 51–53), `Eixo()` = `Deslocar`, modo "Junto de uma parede"
      (`JuntoDaParede`, `CortesEsquadriasCommand.WallOffset`).
    - Orçamento: reescrito, xlsx, preço por material (`Pedra(cfg)`), ralos, vidros, paginações (grupos JaDados).
    - Plano de corte: `Montar(..., girar)`, `Item.Forma/Furos` (no referencial da maior aresta, como `Envolvente`).
    - Iluminação: `Lados()` (GetRoomAtPoint ±0,5 m na FacingOrientation), painéis de vidro (nome do tipo), shared
      param `JA_Iluminacao` (GUID novo, ligado a Ambientes com ParameterBindings.Insert), `Pintar` (FilledRegionType
      "JA Atende"/"JA Não atende", sólido, SendToBack).
    JA Tools 1.36.0: Quadro de Áreas redesenha no lugar; Etiquetar e Cotar com `Cotados()` e cotas internas;
    Quadro de Acabamentos com `Pintura()`; Verificar Modelo (níveis sem planta, grupos/modelos sem uso, linhas de
    modelo planas); Planilha de Parâmetros em xlsx multi-aba (`Tabela(s)`), importação de todas as abas.

76. 17/11/2026 — "Implemente" (lista de "Tem mais implementações?", menos a numeração amarrada ao ambiente).
    JA Famílias 1.20.0 e JA Tools 1.37.0, NÃO compilados (1.17–1.20 / 1.35–1.37 nunca compilaram).
    JA Famílias:
    - Paginação: núcleo virou métodos da base (`RegiaoPag`, `PaginacoesDaVista`, `RegiaoDeAmbiente/DeFace/Guardada`,
      `DescontarRalos`, `DesenharPaginacao`, `EstiloPaginacao`, `ParaVista/DaVista`); rejunte
      `ConfigPaginacao.RejunteKgM2` = (C+L)·E·J·D/(C·L) (DM 13–15: EspessuraPeca, DensidadeRejunte 1,58, Argamassa kg/m²);
      `DadosJa` ganhou RejunteKg/ArgamassaKg/Caixas/Metros/Descricao (DM 11–15).
    - Rodapé: `ConfigRodape` + `AplicarRodapes(rooms, cfg, refazer, avisos)` (cada trecho guarda a config).
    - Forro e Sanca (`Commands/ForroCommands.cs`): `ConfigForro`, `AplicarForros`; Ceiling.Create com tabica
      (`Deslocar` = CurveLoop.CreateViaOffset escolhendo o sinal pela área), sanca fechada (faixa H−rebaixo + central
      dentro do espelho) / aberta (central até as paredes, espelho até H−abertura); espelho = DirectShape (OST_Ceilings
      se válido); JaDados Tipo "forro" (Descricao forro/faixa da sanca/forro central/espelho da sanca); ROOM_FINISH_CEILING.
    - Madeira: `TipoPeca.Madeira = 6`, `MadeiraConfig` (UsoMadeira PainelRipado/Pergolado, `ListaDeCorte()`), códigos RP/PG,
      slots madeira/fundo, Modelo genérico, edição por `AbrirMadeira` (diálogo, como o vidro); `PainelRipadoCommand`
      (parede + 2 pontos, face pelo lado clicado) e `PergoladoCommand` (3 cliques; origem no 1º canto, +Y para o 3º);
      `GerarMadeira` reaproveita família igual. Orçamento: grupo Madeira; Lista da Marmoraria ignora madeira/vidro/ralo.
    - Impermeabilização: `ConfigImper` (ToolPrefs "impermeabilizacao.config"), `CalcularImper` (piso + subida −
      portas + paredes do box JA: reto = W + 2·profundidade por raio até a parede; L = W + lado fixo), xlsx.
    - Iluminação: `ConfigIluminacao` (ToolPrefs "iluminacao.config") + `ConferirIluminacao(rooms, cfg, plantas)`.
    - Atualizar Revestimentos: TransactionGroup; refaz paginações por vista, rodapés e forros agrupados pela Config,
      repinta iluminação nas plantas que já tinham regiões.
    - Lista de Compras (`Commands/ObraCommands.cs`): xlsx Resumo + abas por fornecedor. Memorial Descritivo:
      `Infrastructure/DocxWriter.cs` (docx mínimo, validado com python-docx/LibreOffice), por ambiente.
    JA Tools (`Tools/Parameters/ConferenciasCommands.cs`): Conferir Acessibilidade (vão livre, círculo de giro por
    grade + refino com obstáculos, largura mínima por raios), Conferir Escadas (Stairs/StairsRun, Blondel, patamar,
    GetAssociatedRailings), Carimbo em Lote, Comparar Versões (OpenDocumentFile + Close(false), ambientes por UniqueId),
    Quantitativo de Pintura. Painel Dados: pilha Quadro de Acabamentos/Áreas/Pintura e pilha Conferências/Carimbo/Comparar.
    Item "vistas internas por ambiente" NÃO foi feito: já existe (Vistas por Ambiente + Detalhar Ambientes com prancha).

77. 18/11/2026 — "Atenda o que ficou de fora". JA Famílias 1.21.0 e JA Tools 1.38.0, NÃO compilados.
    - DataContractJsonSerializer não roda construtor/inicializadores: `ConfigPaginacao/ConfigRodape/ConfigForro/
      ConfigImper/ConfigIluminacao` agora têm `Padroes()` chamado no construtor e em `[OnDeserializing]`
      (JSON antigo sem EspessuraPeca etc. vinha com 0 → rejunte 0).
    - `AdotarRodapesAntigos()` (RevestimentoCommands): trechos PedraConfig Rodape sem Config ganham DadosJa (ambiente
      pelo lado +Y da linha, GetRoomAtPoint ±0,15 ft; config da família + prefs "Rodape.*"); Atualizar usa uma
      config por ambiente (o 1º trecho) antes de agrupar.
    - `ConfigIluminacao.Salva()` cai em `DasPreferencias()` (prefs "IluminacaoVentilacao.*") — nunca null.
    - JA Tools: Vistas por Ambiente → opção Prancha (`VistasAmbPedido.Prancha/Folha/NumeroPrancha/NomePrancha/Margens`,
      `Pranchas()` usa `DetalharAmbientesCommand.SheetHost/Usable/ParseMargins`, agora internal, e LayoutEngine.Pack).
      Forro por Ambiente: `ForroGeo` (Deslocar/Anel), tabica, sanca, substitui pelos Comentários "JA Tools - forro …".
    - Geometria nova usa `Level.ProjectElevation` (DirectShape do espelho, IsPointInRoom).

78. 18/11/2026 — 1ª compilação da 1.21.0 (Revit 2024): 12 erros, corrigidos na 1.21.1:
    - CS1061 tupla perdeu os nomes: `var cur = dic.TryGetValue(k, out var c0) ? c0 : (0.0, 0.0, ...)` — o tipo do
      condicional fica sem nomes; declarar o tipo explícito `(double il, double ve, List<string> quais) cur = ...`
      (EsquadriaLoteCommands, ObraCommands).
    - CS0122 `ClicarPontos` era private na base (BancadaLocalCommand.cs) e o Ralo no Croqui (PecaCommands) usa → protected.

79. 18/11/2026 — "Verifique o código por redundâncias ou problemas". JA Famílias 1.21.2, JA Tools 1.38.1 (não compilados).
    Problemas corrigidos:
    - Cota Z: o JA Famílias inteiro usava `Level.Elevation` como Z interno (ChapimPlacer, soleiras, bancada no local,
      vidros, rodapé, nicho, fachada, cortes). Errado quando a base de elevação do nível não é a origem interna
      (ponto base / topográfico com cota). Trocado por `Level.ProjectElevation` em todo o add-in.
    - Paginação: a antiga era apagada antes de calcular a nova; se o cálculo falhava o ambiente ficava sem nada.
      Agora só apaga depois de `Calcular` dar certo.
    - Forro: cada ambiente numa SubTransaction; erro no meio desfaz só aquele ambiente (antes ficavam forros
      soltos sem JaDados e o antigo já apagado).
    Redundâncias removidas:
    - Um filtro de parede só na base (`FiltroDeParede(Func<Wall,bool>, referencias)` + `FiltroDeParede.Reta`) no lugar
      de 4 classes (Fachada, Nicho no Local, Ralo junto da parede, Painel Ripado).
    - Constantes mm→pé: `MmPe` (Nicho, Bancada no Local) e `Mm` (Fachada) → `MmFt` da base (ficam só nas classes
      que não herdam da base: ConferirPlanta, Cortes).
    - Comprimento de peças lineares: `ProjectPecas.ComprimentosMm/Metros(doc, sym)` no lugar de 3 cópias
      (Orçamento, Lista da Marmoraria, Lista de Compras).
    - Largura de porta da Impermeabilização usa `TamanhoPainel` (o mesmo do rodapé).
    - Chaves repetidas nos dicionários de ícones ("trocar" no JA Famílias; "unir", "paredesLajes" no JA Tools):
      mantida a que estava valendo (a última).

80. 18/11/2026 — "Deve ter como desenhar o contorno do ralo, usando ferramentas linha, retângulo… do Revit". JA Famílias 1.22.0
    (não compilado). Ralo no Croqui: modo "Com as ferramentas do Revit" — guarda `_desenho` (doc, vista, maior ElementId
    do modelo), mostra instruções e `PostCommand(PostableCommand.DetailLine)`; na próxima execução sem seleção, lê as
    DetailCurve da vista com Id maior e (opção) apaga depois de colocar. `LerCroqui`: arcos de mesmo centro/raio somando
    360° = Redondo; cadeia aberta = eixo (EncadearCaminho; curva única fechada conta como contorno); contorno fechado só
    de retas (`Fechar`, `SemColineares`): 4 lados retos com proporção < 1,5 = Quadrado (centro, DirX do 1º lado);
    senão `EixoDoContorno` (pares de lados antiparalelos na largura mínima → trechos do eixo no meio; vizinhos a ≤ 1,5·w
    unidos pela interseção; ramo/anel = erro). Protótipo em Python validado em retângulo, L e U.

81. 18/11/2026 — "Deve ter também a seleção por linha". JA Famílias 1.22.1 (não compilado). Ralo no Croqui ganhou os
    modos "Selecionar linhas já desenhadas" (PickObjects Element, filtro CurveElement) e "Selecionar bordas de
    elementos" (PickObjects Edge → Edge.AsCurve; FamilyInstance sem geometria modificada × GetTotalTransform). As
    curvas passam pelo mesmo `LerCroqui` (eixo / contorno / quadrado / círculo). Ordem dos modos: 0 ferramentas do
    Revit, 1 linhas, 2 bordas, 3 clicar, 4 junto da parede.

82. 18/11/2026 — "Deve ter a ferramenta de orto também e essas configurações devem estar disponíveis para todas as
    ferramentas que desenham famílias". JA Famílias 1.23.0 (não compilado).
    - `Commands/CroquiCommon.cs` (base): `ModoCroqui` (Ferramentas, Linhas, Bordas, Clicar), `EntradaCroqui`
      (Curvas, Extra = modo próprio da ferramenta, Apagar, Iniciado), `EntradaDoCroqui(ferramenta, vista, dica, extras)`:
      pré-seleção de linhas → volta do desenho (`_pendentes[ferramenta]`, DetailCurve com Id maior) → janela
      "como desenhar" (prefs "Croqui.<ferramenta>": modo, apagar, orto, angulo). `SelecionarLinhas/Bordas`,
      `FecharPoligono`, `SemColineares`, `ApagarCroqui` e `CroquiAtual` passaram para a base.
    - Orto: `OrtoLigado/OrtoAngulo`, `AplicarOrto(anterior, p)` no plano da vista (Right/Up girados; serve em elevação),
      Shift no clique inverte (WPF Keyboard.Modifiers). Aplicado em `ClicarPontos` (só cliques encadeados, depois do
      teste de fechar no 1º canto) e em `PegarPontos(..., orto: true)`. `OrtoDasPreferencias` no redesenho da bancada.
    - Ferramentas ligadas: Ralo no Croqui (extra "Junto de uma parede"), Bancada no Local (ambiente pré-selecionado
      continua; `Encadear(List<Curve>)`), Guarda-corpo (janela antes do diálogo; contorno fechado de retas = caminho
      que volta ao início), Box (1 linha = reto, 2 linhas = L, na ordem de desenho), Painel Ripado (`ParedeAoLongo`
      acha a parede paralela a ≤ 50 cm da face), Pergolado (`RetanguloDoCroqui`: retângulo, 1º lado = o junto de
      parede, senão o mais comprido; ou 2 linhas em esquadro), Nicho no Local (retângulo na elevação + 1 clique na face;
      cantos levados ao plano da face pela direção da vista).

83. 18/11/2026 — "O guarda-corpo tem que sair classificado sempre como guarda-corpo e as bancadas sempre como componente
    de gabinete". JA Famílias 1.24.0 (não compilado).
    - Bancadas: `PecaUtil.Gabinete` inclui Bancada (Bancada no Local também). Família já carregada como Modelo genérico:
      `PecaGenerator.ConverterParaGabinete` renomeia a antiga "(antiga)", carrega o .rfa novo (Casework), troca cada
      cópia livre no mesmo lugar/giro (marca e comentários copiados; espelhadas → aviso) e apaga a antiga. Cópias
      hospedadas / baseadas em linha: não converte, fica o aviso antigo.
    - Guarda-corpo = Guarda-corpo do Revit: `GuardaCorpoDoRevit(c, pts, nivel, croqui, avisos)` — placa de vidro =
      `VidroConfig.Balaustre` (DM 27; ponto, largura centrada, `BuildPainel`), família "JA Placa de vidro GC01 1150"
      (template de painel de balaústre via `TemplateLocator.FindBalusterPanel`, senão genérico + FamilyCategory =
      OST_StairsRailingBaluster); tipo "JA Guarda-corpo GC01 placa 1150" duplicado do 1º RailingType:
      TopRailHeight = altura, TopRailType (corrimão; senão Invalid), sem corrimãos laterais, travessas removidas
      (Perfil U = travessa "JA Perfil U" a 100 mm), pilaretes sem família, padrão com 1 balaústre: Length = p + g,
      distância g + p/2 (Beginning) ou (p+g)/2 (Center, `ModuloFixo` DM 28), BreakPattern EachSegmentEnd.
      Iguais por trecho: um Railing por sequência de trechos com a mesma placa; módulo fixo: um Railing só.
      JaDados no Railing: Tipo "guardacorpo", Config, Metros, AreaM2, Peca (placa), Descricao (código).
      Falha (placa não vira balaústre, exceção) → famílias JA antigas + aviso. Editar Família com Railing JA
      selecionado → `EditarGuardaCorpos` (refaz no mesmo caminho via `GetPath`). Orçamento e Lista de Compras
      leem os Railings JA. ProjectPecas inclui OST_StairsRailingBaluster.
      API conferida em rvtdocs: BalusterPattern (GetBalusterCount/GetBaluster/RemoveBaluster/Length/
      DistributionJustification/BreakPattern/EndSpace/ExcessLengthFillBalusterId), PostPattern, NonContinuousRailStructure
      (GetNonContinuousRailCount/RemoveNonContinuousRail/AddNonContinuousRail(name,h,offset)), RailingType (TopRailType,
      TopRailHeight, Primary/SecondaryHandrailType), Railing.Create(doc, CurveLoop, typeId, levelId).

84. 18/11/2026 — "ferramentas avançadas para criação de rodapés que realizem os descontos de portas e vãos". JA Famílias
    1.25.0 (não compilado). `ConfigRodape` DM 9–16: Janelas, Aberturas, Retorno, SemCortina, Revestidas (palavras),
    Cantos, Perda, Barra. `AplicarRodapes` reescrito: laço girado p/ o ambiente ficar à esquerda (teste no maior trecho),
    cortes tipados (0 portas + folga, 1 janelas com bbox.Min.Z ≤ piso + altura + 10 mm, 2 `Opening` hospedadas na parede
    até o piso — BoundaryRect ou bbox —, 3 móveis), contabilidade por tipo (`LinhaRodape`: Bruto, Portas, Janelas, Vaos,
    Moveis, Cortina, Revestidas, Curtos, Retornos, Liquido); parede cortina (WallKind.Curtain) e revestida
    (`Revestida`: nome do tipo ou material da camada do lado do ambiente via Orientation; cache) pulam o trecho inteiro;
    retorno nos vãos = trecho de meia espessura de parede para dentro do vão, pedra virada para o vão; cantos externos
    (curva à direita > 20°) prolongados pela espessura. `SimboloRodape` separado. RodapeCommand: origem "ambientes" ou
    "trecho desenhado" (`EntradaDoCroqui("rodape")`, pedra à esquerda, `RodapeNoTrecho`, Ambiente = 0 — Adotar não mexe),
    compra (perda, peças de N mm) e Excel. Botão "Rodapé" (título interno continua "Rodapé de Pedra").
85. 18/11/2026 — "Tem que ter também o rodapé por ambiente". JA Famílias 1.26.0 (não compilado). Novo
    `RodapePorAmbienteCommand` (Commands/RodapeAmbienteCommand.cs; botão empilhado com Rodapé e Escada Revestida;
    prefs "RodapeAmbiente"): lista Multi de todos os ambientes (`AmbientesColocados`; marcados os pré-selecionados
    ou os do nível da planta ativa — Checked forçado depois do Add, que carrega as prefs); rodapé por ambiente na
    ordem sem rodapé (palavras no nome, ROOM_NAME) → Acabamento de base (`LerAcabamento`: "mat h 100 mm",
    "h 10 cm × 15", "e=15"; ignora o texto que o próprio JA escreveu, `Material h <altura> mm` das peças com
    Ambiente = id) → Rodapé B → C (palavras) → A. Agrupa por JSON da ConfigRodape e chama `AplicarRodapes` por
    grupo. "Sem rodapé" + refazer apaga os trechos antigos e limpa o Acabamento escrito pelo JA. Base: os campos de
    desconto viraram `CamposRodape`/`DescontosRodape(dlg)` (mesmas chaves de prefs) e o resumo virou
    `MostrarRodape(feitos, excel, avisos, cabecalho)` — usados pelos dois comandos.
86. 18/11/2026 — "Deve ter como desenhar rodapé no local também". JA Famílias 1.27.0 (não compilado). Novo
    `RodapeNoLocalCommand` (Commands/RodapeLocalCommand.cs; prefs "RodapeLocal"; croqui "rodape"). O trecho a trecho
    de `AplicarRodapes` virou `TrechoRodape(CtxRodape, c, a, b, host, z, livre, extIni, extFim, lr, Colocar)` +
    `PrepararRodape(c)` (portas, janelas, aberturas, obstáculos, revestidas, cache) + `CantoExterno` — usados pelos
    ambientes e pelo local. Local: `CadeiasEmPlanta` (arcos tesselados, verticais e repetidas fora — bordas de cima e
    de baixo da face —, encadeia a 5 mm, fechada se as pontas se encontram); `Encostar`: `FaceDaParede` (paralela 3°,
    sobreposta, até 50 cm na frente da face ou dentro da parede → face à esquerda do desenho; centro = location +
    Orientation·WallOffset), projeta o trecho na face, vira a cadeia pela maioria (comprimento), trechos contrários
    viram sozinhos (Virado, sem fechar canto), vizinhos se encontram no cruzamento (≤ 60 cm). Sem parede: pedra à
    esquerda (inverter). Descontos só com parede; móveis sempre. JaDados Ambiente = 0, Descricao = "local". Acabamento
    de base só se vazio. `DescontosRodape(dlg, local)` sem "Substituir". RodapeCommand voltou a ser só ambientes (saiu a
    janela de origem e o `RodapeNoTrecho`). Fita: Rodapé / Rodapé por Ambiente / Rodapé no Local; Escada Revestida foi
    para a pilha do Guarda-corpo e Box.
87. 18/11/2026 — "Agora faça testes na ferramenta de criar rodapés". JA Famílias 1.28.0 (não compilado). Protótipo em
    Python + 42 casos em docs/testes/rodape (`python3 test_rodape.py`, compara 'atual' × 'nova'). A 1.27 falhava em 15:
    sobreposição e×e em todo canto interno (e a quina externa coberta duas vezes: metros a mais e peças da marmoraria
    15 mm mais longas por canto), porta/janela/vão em parede cebola não descontados (só o hospedeiro do trecho),
    retorno sem cobrir a quina do batente e com profundidade da parede de revestimento, parede curva em cordas curtas
    virando "curtos" (buracos), quina crescendo sobre parede revestida/cortina, e o Acabamento de base sem "h"
    ("10 cm", "altura 7 cm", "h.10cm"). Correção: `TrechoRodape` virou `CalcularTrecho` (só calcula: peças com marca de
    retorno, retornos, contas) + `ColocarTrechos(laço, fechado, …)`: canto pelo que chega nele — interno: a peça
    seguinte começa E/sen φ depois (topo a topo); externo: a que chega cresce E·tan(φ/2) (uma vez só); nada cresce
    para vizinho sem rodapé; < 20° = liso (curva: não é "curto"); ao lado de vão com retorno a peça cresce E.
    Inserções: qualquer porta/janela/Opening de parede paralela atrás do trecho (até largura + 15 cm); retorno até o
    meio da parede do vão (`CentroDaParede` com WallOffset). Rodapé no Local usa o mesmo (Virado = sem canto).
    `LerAcabamento`: h | alt | altura | só número com unidade; material com ≥ 2 letras.
88. 18/11/2026 — "deixar os rodapés restritos à parede onde foram criados". JA Famílias 1.29.0 (não compilado).
    `ConfigRodape.Prender` (DM 17, padrão true; campo "prender" em `DescontosRodape`, vale nos três comandos e no
    Atualizar). `TrechoCalc` guarda Host e Z; `ColocarTrechos(…, nivel)` junta em `CtxRodape.Prender` cada peça
    colocada ao longo de uma Wall; depois do commit, `PrenderRodapes` (transações próprias de 100, `AvisosOuDesfaz`
    desfaz em erro sem diálogo; lote recusado → peça por peça) faz `NewAlignment(planta do nível, face lateral da
    parede com normal para o ambiente que contém a peça, fundo da peça)`: fundo = `GetReferences(CenterFrontBack)`,
    depois faces do símbolo / da instância no plano da linha com normal para a parede; tenta até 4 plantas do nível.
    Resumo: "Presos na parede (cadeado): n; m soltos". Retornos e pilares não são presos.
89. 18/11/2026 — log da 1.29.0 (2024–2027, 9 erros, os primeiros desde a 1.21): CS0104 `Group` ambíguo
    (Autodesk.Revit.DB × RegularExpressions) em RodapeAmbienteCommand.LerAcabamento → nome completo; CS0200
    `BalusterPattern.Length` é só leitura (comprimento = distâncias + EndSpace) → EndSpace = p + g − distância da
    placa. JA Famílias 1.29.1.

90. 18/11/2026 — "realize todas essas melhorias e unifique os plugins de forma que ainda se gerem duas abas, mas
    seja um único instalador, corte redundâncias sempre mantendo a versão melhor". **JA para Revit 2.0.0** (não
    compilado). Um só projeto `src/JA.Revit` → `JA.Revit.dll` em `Addins\<ano>\JA\`; um só `JA.addin` com duas
    `<AddIn>` (mesmos AddInIds de antes: JA Famílias 4F9ED87E… `JAEsquadrias.App`, JA Tools 48046ECB…
    `RevitDwgUnifier.App`) → duas abas. Pastas: `JAFamilias/` (antigo JAEsquadrias.Revit), `JATools/` (antigo
    RevitDwgUnifier.Revit), `Comum/GlobalUsings.cs` (aliases globais: os nomes do JA Famílias apontam para a
    infraestrutura do JA Tools — OptionsDialog, ToolCommand, ResultDialog, ToolPrefs, ToolCatalog, ToolChrome,
    ToolProgress, HelpCenter, AvisoVersao, IconFactory, JaTheme, Logger, AddinLocation, XlsxWriter, Availability).
    As cópias do JA Famílias desses arquivos foram apagadas (ficou a versão do JA Tools, mais completa).
    `ToolCatalog` é `partial`: a metade do JA Famílias está em `ToolCatalog.Familias.cs` exposta por métodos
    (ordem de inicialização estática entre partes não é garantida). `ToolCatalog.Aba/Marca` dão o nome da aba no
    título das janelas. Central JA / Buscar Comando / Ajuda veem as duas abas; ToolPrefs traz uma vez as opções de
    %AppData%\JAEsquadrias\prefs.json; Padrões do Escritório leva também o settings.json do JA Famílias.
    Instalador único `installer/JA.iss` (AppId {8C1E5F2A-6B3D-4A7E-9F10-2D4C6B8A0E35}), desinstala em silêncio o
    JA Tools e o JA Famílias antigos e apaga as pastas/manifestos velhos. Redundâncias cortadas: Rodapé e Tabica →
    Tabica por Ambiente (rodapé só no JA Famílias); Forro por Ambiente saiu (Forro e Sanca fica). Faixa do JA
    Famílias: painel Revestimentos (Paginação, Rodapé ▾ [Rodapé, por Ambiente, no Local], Forro/Imperm./Escada,
    Ralo ▾, Atualizar) e Vidros e Madeira; Ralos saiu como painel; "Rodapé de Pedra" → "Rodapé", "Ralo no Croqui"
    → "Ralo no Local"; Central JA também na aba JA Famílias. UX: `ToolCommand.UmDesfazer` (TransactionGroup) em
    Ajustar Cebola e Unir; rodapés com prévia (`ConfirmarRodape`, `ColocarTrechos` com Colocar = null), um Ctrl+Z,
    barra de progresso e as peças colocadas ficam selecionadas; Atualizar Revestimentos com etapas de progresso;
    Quantitativo de Pintura desconta a altura real do rodapé JA de cada ambiente (JaDados "rodape" + ConfigRodape).
    Dicas cruzadas Pisos por Ambiente ↔ Paginação. Não feito ainda: seletor de ambientes comum às duas abas e
    clique de pontos com orto comum.

91. 18/11/2026 — "estude e analise o código do plugin e deixe limpo, sem perder funções ou eficiência". JA para Revit
    2.0.1 (não compilado; ~1100 linhas a menos em 76 arquivos). Análise com scripts (Pygments) em /home/claude/tools/clean:
    analyze/analyze2 (nomes sem uso), statics.py (estáticos por Classe.Nome), privates.py (privados por classe),
    usings.py (usings sem uso: nossos namespaces + listas de tipos do System/Revit), dups.py (métodos iguais),
    blocks.py (blocos de 8 linhas iguais), rm.py (remove um membro com o comentário). Removido: ~25 métodos/campos
    sem uso (FitElevation, DirecoesDoMarcador, LoadAll/Files, UsedCodes de esquadria, GetDouble, Q, Count…) e
    ~70 usings. Unificado: `JAFamilias/UI/FormKit.cs` (controles das janelas Esquadria/Peça/Bancada: Field, Pills,
    Check, HexInput, MaterialInput, MarkInvalid, SetStatus, SetTab, Stat, SmallButton, Plain, ErrorBrush, janela e
    grade catálogo|prévia; as janelas têm só invólucros de uma linha ligados ao próprio estado, e `using static`)
    e `CompactForm` (Perfil/Painel: Num, N, Br, Vis, Toggle, Field, D); `Engine/FamUtil.cs` (Find, MaterialParam,
    Shared, ToColor, Clean dos geradores); TemplateLocator com um só varredor `Best(roots, own, metric, rank)`;
    `HelpCenter.Describe` (dicas da faixa das duas abas); `TagLibrary.KeepValues` (opções de carga da legenda);
    HachuraUnica.Inside/SignedArea usados pela Varinha; ViewCatalog.SafeScale; OleEmbedEngine.TryKill; acentos via
    `TextUtil.Fold` (alias global). Etiquetar e Cotar em Lote lê os ambientes uma vez para todas as plantas.
    Ficaram de propósito: WarningSwallower × FailureSwallower (o do JA Tools registra no log), Changed() das janelas
    Perfil/Painel (estado de cada uma), Armazem/Palavras/Frozen (3–6 linhas, módulos sem relação).

92. 18/11/2026 — "em algumas quinas a ferramenta de rodapés não está unindo" (prints de planta girada ~37°, quina externa
    com as duas peças parando na projeção da quina). JA para Revit 2.0.2 (não compilado). Simulador:
    docs/testes/rodape/test_quinas.py ('nova' × 'v2'; a 'nova' falhava em 7 de 30). Causas: (1) quando as faces das
    duas paredes não se encontram (degrau, folga ou chanfro de poucos mm — paredes cebola, junções), o contorno do
    ambiente tem um trecho minúsculo na quina; ele caía em "curtos" e a quina ficava aberta; (2) móveis girados: o
    BoundingBox do Revit é alinhado ao projeto — um sofá de 2 m a 37° descontava 4,7 m de rodapé. Correção em
    `ColocarTrechos`: o canto é o encontro das retas dos dois trechos (ext/u), trechos "micro" (< máx(mínimo, 2E,
    20 mm), até 3) entre dois trechos que viram ≥ 20° são absorvidos (contam como curtos); laço fechado percorrido a
    partir de um trecho não micro; curvas em cordas e pilares rasos não mudam (o micro só é pulado se o vizinho
    seguinte não é micro e o encontro fica a até (micros + 3E + 20 mm)). `Pegada(fi)`: caixa da geometria do
    símbolo girada com a instância (cai no BoundingBox se inclinada ou sem GeometryInstance); `CtxRodape.Obstaculos`
    virou (cantos, z0, z1).

93. 18/11/2026 — 2.0.2 compilou depois de um erro (`Select(Clean)` em FamilyGenerator: grupo de método que o
    script de checagem não via → `FamUtil.Clean`). Novo print: quina externa girada com as duas peças parando na
    projeção e uma linha passando pelo vértice → linha de separação de ambientes que continua uma das faces
    (CIRC. | SALA): cada ambiente fazia o seu lado e o quadrado da quina não era de ninguém. JA para Revit 2.0.3:
    `ColocarTrechos(…, geo, vizinho)`: trecho de parede precedido/seguido por linha de ambiente colinear (< 20°)
    cresce E naquela ponta se o ponto atrás da linha, logo depois da quina, está em outro ambiente com rodapé
    (`Vizinhos`: os da rodada + `outros` (todos os grupos do Rodapé por Ambiente / da prévia) + os que já têm
    rodapé JA; `IsPointInRoom` a z + 1 pé). Só o ambiente do lado colinear cobre (o outro faz curva para dentro):
    sem pedra dobrada. Se os ambientes forem feitos um de cada vez na ordem contrária fica o buraco até rodar de
    novo / Atualizar Revestimentos. Testes: test_quinas.py seção 6 (8 casos).

94. 18/11/2026 — "quando as linhas criadas para o rodapé estão faceadas com a parede, os componentes deslocam-se para o
    núcleo dela" (Rodapé no Local). Causa provável: `FaceDaParede` calculava a face pelo centro (linha de localização +
    `WallOffset` + Width/2); se a conta fica ≥ 1 mm fora da face real, a linha rente era tida como "dentro da parede" e
    o lado vinha do sentido do desenho (podia ir para a outra face) e a peça ficava deslocada da face real. JA para
    Revit 2.0.4: `FacesLaterais(w, m)` lê as faces do modelo (HostObjectUtils.GetSideFaces, a que fica na frente do
    desenho perto do piso), a mesma que o rodapé por ambiente e a trava usam; conta antiga só sem faces. Linha até
    mín(10 mm, ¼ da espessura) para dentro = aquela face; mais funda = lado do desenho. Empate entre paredes que se
    encostam (cebola): a face cujo lado de fora (5 mm) não está dentro de outra parede. Não testado no Revit; se
    persistir, pedir print, tipo da parede (camadas? cebola?) e como desenhou (ferramentas, linhas, bordas, cliques).

95. 18/11/2026 — 2.0.4 "deu certo na maior parte das paredes", mas numa parede (lavabo, planta girada) o rodapé ficou
    dentro da parede: no print a peça cruza a espessura em diagonal (perto da face do banheiro em cima, da face de fora
    embaixo) → o desenho não estava paralelo à parede (> 3°, ex.: pontos clicados com orto em planta girada) e
    FaceDaParede não achou parede: a linha ficou como desenhada. JA para Revit 2.0.5: aceita até 10° quando as duas
    pontas estão até 10 cm na frente da face ou dentro da parede (projeta na face, fica paralela); linha funda dentro
    da parede: a face cuja frente (30 cm) cai num ambiente e a outra não (`LadoComAmbiente`, GetRoomAtPoint), senão o
    lado do desenho.

96. 19/11/2026 — "os rodapés criados pela ferramenta tenham seu nível em um parâmetro" + "uma forma de inserir
    informações em massa em elementos selecionados ou de uma categoria com base em uma informação específica, como o
    Nível". JA para Revit 2.1.0 (não compilado). `JATools/Tools/Parameters/ProjectParams.cs`: `GarantirTexto` (parâmetro
    de projeto de texto, compartilhado num arquivo temporário, instância, categorias acrescentadas se já existe,
    SetAllowVaryBetweenGroups), `GravarNivel` (JA_Nivel GUID 3f6a9c1e-2b74-4d8e-a5c3-7e1d0b9f4a26 + Nível da tabela se
    editável). Rodapé (Aplicar) e Rodapé no Local chamam GravarNivel em cada peça (Modelos genéricos ganham JA_Nivel).
    `LevelRehost.NivelDe(e)`: restrição de base / nível do ambiente / LevelId / hospedeiro. Nova ferramenta JA Tools
    **Preencher Parâmetros** (Dados, ao lado de Mudar Nível; ícone "preencher"): 1ª janela onde + categorias; 2ª
    parâmetro (instância editáveis, amostra de 150 por categoria; "➕ Novo parâmetro de texto"), com base em Nível /
    Ambiente nome / número / Modelo ({Nível} {Ambiente} {Número do ambiente} {Família} {Tipo} {Categoria} {Id} {qualquer
    parâmetro}) / Texto fixo; só vazios; sem nível → nível abaixo da base; seleciona os alterados. Ambiente: o próprio,
    JaDados do rodapé, FamilyInstance.Room/To/From, GetRoomAtPoint. Grupos: só se o parâmetro varia entre grupos.
    Números por SetValueString (unidades do projeto), Sim/Não.

97. 19/11/2026 — exemplo de uso do Preencher Parâmetros: "elementos entre a elevação 10 - 13 devem estar no primeiro
    pavimento". 2.1.1: fonte "Faixas de elevação" (texto "10-13 = Primeiro pavimento; 13-16 = Segundo", separador ; ou
    quebra; de-até aceita -, –, a, até; valor depois de =, :, -> ou →; [de, até) com 0,5 mm de folga; fora das faixas não
    muda) e filtro "Só elementos entre as elevações" (De/Até m) para qualquer fonte. "Elevação do elemento": nível +
    deslocamento da base (`LevelRehost.ReferenciaDe`: pares nível→deslocamento, ambiente: BaseOffset) — paredes pela
    base, pisos pelo topo, famílias pela elevação; sem nível, o ponto mais baixo; ou ponto mais baixo / centro / mais alto
    da caixa (+ Elevation − ProjectElevation: como os níveis mostram).

98. 19/11/2026 — "deve ser possível preencher pelo ambiente em que está localizado, no caso de portas normalmente o
    lado interno". 2.1.2: Preencher Parâmetros, opção "Portas e janelas: ambiente" (visível com Ambiente nome/número e
    Modelo): lado interno = get_ToRoom(fase da criação) (para onde abre; padrão) / o que não é circulação (nomes circ,
    hall, corredor, escada, elevador, passagem, galeria, vestíbulo, antecâmara, patamar) / lado externo = FromRoom; com
    um lado só, esse. Sem From/To (família sem cálculo de ambiente): GetRoomAtPoint a ±(meia parede + 30 cm) da
    FacingOrientation, "para" no lado oposto à facing. Depois fi.Room e o ponto da locação.

99. 19/11/2026 — "pense, analise e implemente funcionalidades nessa ferramenta de preencher parâmetros além de criar
    uma ferramenta de filtro". JA para Revit 2.2.0 (não compilado). `Tools/Parameters/ValoresElemento.cs` (comum):
    Alvo, NomesDeParametros (amostra por categoria; instância ou tipo), Nivel (+ abaixo), Elevacao (4 referências),
    Ambiente (portas pelo lado), Token/Expandir ({Nível} {Ambiente} {Número do ambiente} {Família} {Tipo} {Categoria}
    {Id} {Elevação} {qualquer parâmetro}; {Ambiente:X} {Nível:X} {Tipo:X} {Hospedeiro:X}), Numero ("2,50 m", "1.250,5"),
    Intervalo, LerFaixas, LerTabela, Dentro. Preencher Parâmetros: parâmetros [tipo] (um valor por tipo; tipos com
    instâncias pedindo valores diferentes são pulados), fontes novas Tabela de/para (campo/modelo comparado sem
    acento/maiúscula, "* = resto") e Substituir no valor atual (sem maiúsculas), Letras (MAIÚSCULAS/minúsculas/primeira),
    prévia (OptionsDialog "PreencherParametros.Previa": quantos, valores novos com contagem, exemplos antes→depois, os
    que não mudam), cálculo antes da transação. Nova ferramenta **Filtrar Elementos** (Dados, ao lado do Preencher;
    ícone "filtrar"): onde (seleção/vista/projeto) + categorias; até 3 regras (campo especial ou parâmetro, 11
    condições, E/OU; números só quando os dois lados são números com unidade); fazer: selecionar, somar/tirar da
    seleção, isolar/ocultar temporário, destacar com cor (SelectionFilterElement como filtro da vista, linhas + padrão
    sólido); salvar como conjunto de seleção. Elevação "entre" = [de, até) como as faixas; outros campos inclusivo.

100. 19/11/2026 — "devem ter tantas opções de filtros e preenchimentos quanto o Archicad". JA para Revit 2.3.0 (não
    compilado). `Tools/Parameters/Formula.cs`: motor de fórmulas tipo expressões do Archicad / Excel pt-BR (campos {…},
    números 2,5 ou 2.5, "textos", + − * / ^ &, = <> < > <= >=, E/OU/NÃO, separador ";" (vírgula fora de número também),
    SE, SES, ESCOLHER, E, OU, NÃO, VAZIO, SEERRO, ABS, ARRED, ARREDONDAR.PARA.CIMA/BAIXO, TETO/PISO(x; múltiplo), INT,
    RAIZ, POT, MOD, MÍN, MÁX, SOMA, MÉDIA, PI, CONCATENAR, ESQUERDA, DIREITA, EXT.TEXTO, NÚM.CARACT, MAIÚSCULA,
    MINÚSCULA, PRI.MAIÚSCULA, ARRUMAR, SUBSTITUIR, DIVIDIR (parte negativa = do fim), CONTÉM, COMEÇA, TERMINA,
    LOCALIZAR, TEXTO(x; casas), VALOR; nomes em inglês também). Espelho em Python + 45 casos:
    docs/testes/formulas/test_formula.py. `ValoresElemento`: `Valor()` tipado (comprimento m, área m², volume m³,
    ângulo graus, sim/não bool) e `Token()` em texto; campos especiais (`ValoresElemento.Especiais`): Nível, Nível do
    topo, Ambiente, Número do ambiente, Elevação, Elevação do topo, Comprimento, Área, Volume, Altura, Largura,
    Espessura, Família, Tipo, Categoria, Material, Fase de criação/demolição, Grupo, Subprojeto, Opção de projeto,
    Hospedeiro, Função, Estrutural, Fixado, Criado/Alterado por, Id, Seq. Filtrar Elementos: 5 regras, campo
    "Fórmula (verdadeira)", 16 condições (+ ≥, ≤, é um de, não é um de, parece com * ?), ações + inverter, mostrar
    (ShowElements), exportar a lista (Excel via Conferencia.Salvar). Preencher: fonte Fórmula (resultado numérico grava
    direto em comprimento/área/volume/ângulo/número; bool em sim/não) e numeração {Seq} (ordem nível+posição Y/X, valor
    atual, Revit; recomeça a cada {campo}; início; dígitos). OptionsDialog.TextField.LiveValue (visibilidade ao vivo).

101. 19/11/2026 — "ferramenta no estilo da varinha mágica para inserir minhas famílias de instalações, tomadas,
    interruptor, pontos hidráulicos, marcadores de esgoto e outros". JA para Revit 2.4.0 (não compilado).
    `JATools/Tools/Modeling/VarinhaPontosCommand.cs` (Modelagem › Varinha de Pontos): diálogo (família por categoria de
    instalação — dispositivos elétricos/iluminação/dados/telefone/alarme/segurança, luminárias, equipamentos,
    aparelhos sanitários, acessórios de tubo, sprinklers, modelo genérico, item de detalhe, anotação genérica — ou
    copiar o tipo de uma peça clicada; alturas 0/0,30/0,60/1,10/1,20/2,20/outra; afastamento; raio de busca da parede;
    solto sem parede; etiquetar; no Esc escolher outra). Planta: PickPoint → face lateral mais próxima
    (HostObjectUtils.GetSideFaces + Face.Project, clique na frente da face) → coloca por FamilyPlacementType:
    WorkPlaneBased na face (refDir ao longo da parede; sem parede: face de topo do piso ou nível), OneLevelBasedHosted
    na parede (altura + flipFacing para o ambiente), OneLevelBased e ViewBased girados para a normal. Corte/elevação/3D:
    PickObject(Face) de parede, altura do clique. Um TransactionGroup por família (um Ctrl+Z).

102. 23/11/2026 — rodapé "adentrando" pilares/colunas e "o recorte nas portas tem que parar na aduela". JA para Revit 2.4.1
    (não compilado). Rodapé no Local: `PilaresNoNivel` (OST_Columns + OST_StructuralColumns que cortam a altura, faces
    verticais lidas do sólido) e `FaceDoPilar` (paralela até 10°, sobreposta, até 50 cm na frente ou 30 mm dentro); em
    `Encostar` a face do pilar vence a da parede quando está mais perto, o trecho encosta nela com a pedra para fora e
    vota na virada da cadeia (antes o trecho do pilar era "livre" e virava junto da cadeia → pedra dentro do pilar).
    Host do trecho = pilar (sem trava de parede). Rodapé por Ambiente/Prever: `TrechoDoAmbiente` vira sozinho (Virado)
    um trecho de pilar cuja esquerda (30 mm) não está no ambiente e a direita está. Portas e janelas até o piso:
    `BordasDaEsquadria` — bordas dos sólidos da família na face do trecho (alizar, senão aduela; sólidos que tocam
    −5 mm..+30 mm; senão todos através da parede), na altura do rodapé, cache por instância em CtxRodape.Solidos;
    validação: largura entre metade do painel e painel + 60 cm, centro a < 40 cm; senão largura do painel como antes.
    A folga continua somada. Pausado: Visualizador IFC (src/JA.IfcViewer, fora da solução e do pacote; teste do
    triangulador "pente" falhando em docs/testes/ifc/test_geo.py).

103. 23/11/2026 — "telemetria avançada para analisar o andamento do usuário e estudar melhorias". JA para Revit 2.5.0
    (não compilado). `JATools/Infrastructure/Telemetria.cs`: JSONL por evento em %LOCALAPPDATA%\RevitDwgUnifier\Telemetria\
    AAAA-MM_usuario_maquina.jsonl (buffer + timer 30 s + ao fim de cada comando), copiado para a pasta central
    (ToolPrefs "__telemetria.pasta", senão pasta de atualizações\Telemetria) 20 s após abrir e ao fechar (espera 3 s).
    Eventos: sessao (JA, Revit/build, SO, .NET, cultura), revit_pronto, doc (hash do projeto, MB, central, segundos para
    abrir), comando (ferr, aba, res, ms, ms_dlg, dlg, dlg_canc, msg, sel, vista, proj, add/mod/del/trans pelo
    DocumentChanged durante a rodada, erro {tipo, msg, onde = 1º quadro do código JA}, auto), dialogo (valores: listas
    "marcados/total", textos ≤ 80), mensagem (tipo + 1ª linha com números → #), desfeito (TransactionUndone cujo nome
    é de uma das 40 últimas rodadas: nomes das transações + título), uso (Central de Ajuda), fim_sessao. Ganchos:
    ToolCommand.Executar/Show, OptionsDialog.Show (RegistrarUso), UnifiedDwgExportCommand (wrapper), HelpCenterCommand,
    App OnStartup/OnShutdown. Ativa/Anônima (anon-hash) em ToolPrefs. `UsoTelemetriaCommand` (painel JA, catálogo Geral,
    ícone "telemetria"): Relatório (MiniJson; Resumo, Sugestões, Ferramentas, Opções, Erros, Avisos, Usuários, Sem uso,
    Sequências ≤ 5 min, Por semana, Contexto) em Documentos\JA\Relatórios de uso; Configurar; Enviar agora.
    Regras de sugestão: desistência na janela ≥ 30%, desfeitas ≥ 20%, erros ≥ 3 ou ≥ 5%, mediana > 60 s, janela > 45 s,
    opção ≥ 85% num valor (n ≥ 8), aviso ≥ 10×, usuário em versão antiga, ferramenta de um só usuário, sem uso,
    pares A → B ≥ 10×.

104. 23/11/2026 — "deve ter um botão para exportar o relatório de uso". JA para Revit 2.5.1 (não compilado).
    `ExportarRelatorioUsoCommand` (painel JA, ao lado de Uso e Telemetria; catálogo Geral, ícone "exportar"): período,
    fontes, esconder nomes, formato (Excel / Excel + CSV / só CSV), SaveFileDialog (padrão Documentos\JA\Relatórios de
    uso), abre ao terminar. `RelatorioUso.Bruto`: um evento por linha (data, hora, usuário, sessão, evento, ferramenta,
    aba, resultado, tempos, criados/alterados/apagados, vista, seleção, projeto, tipo/janela, mensagem/erro, onde,
    desfeita em, versões) via XlsxWriter.SaveCsv; `RelatorioUso.PastaPadrao`.

105. 23/11/2026 — exportador DWG: "model em metro e paper em cm, o tracejado fica inadequado no model; precisa parecer
    com o paper sem alterar o LTScale". JA para Revit 2.5.2 (não compilado). Motor AutoCAD,
    `Geometry/PaperUnits.ModelComoPapel` (passo "ltmodel" depois de ConfigureHeader): escala de cada folha = maior viewport
    de modelo (fora a do papel); cria uma AnnotationScale "1:n (JA)" por escala (PaperUnits 1, DrawingUnits =
    1/CustomScale, n = DrawingUnits × f), CANNOSCALE = a da maioria das folhas, MSLTSCALE = 1 (propriedade por reflexão
    ou SetSystemVariable com o banco como WorkingDatabase). As definições de linha continuam em unidades de papel
    (ScaleLinetypes) e PSLTSCALE = 1, LTSCALE = 1: o paper não muda; no model o tracejado é o do papel × escala.
    Folhas de outra escala: escolher a "1:n (JA)" na escala de anotação da barra de status.

106. 23/11/2026 — "crie uma família de porta de elevador paramétrica simplificada". JA para Revit 2.6.0 (não compilado).
    `JAFamilias/Engine/ElevadorGenerator.cs` + `Commands/PortaElevadorCommand.cs` (JA Famílias › Esquadrias › Porta de
    Elevador; catálogo Esquadrias, ícone "porta"). Do modelo de porta: Largura/Altura = vão livre (corte do modelo);
    planos Left/Right/Center do modelo achados pela posição (senão criados e alinhados às faces do vão, EQ no centro);
    plano "JA - face do hall" alinhado à face externa da parede do modelo (segue a parede); planos do portal com cota
    rotulada "Largura do portal", soleira ±50 mm (cadeado), botoeira (cota "Afastamento da botoeira", 80 mm cadeado);
    planos ao longo da parede com cadeado a partir da face (portal +20, folhas −30/−70, soleira +10/−120, botoeira +15).
    Peças = extrusões desenhadas em planta no nível, faces laterais alinhadas aos planos (planta, senão elevações/3D),
    início/fim associados: portal até "JA - topo do portal" (= Altura + Largura do portal), verga de Altura ao topo,
    folhas até Altura, botoeira de "Altura da botoeira" a "JA - topo da botoeira" (+250 mm), visibilidade "Botoeira"
    (instância). Materiais (parâmetros de tipo, criados no arquivo), subcategorias Painel / Moldura-Montante, marca
    de tipo = código, tipos 800×2000, 900×2100, 1100×2100. Salva "JA Porta de Elevador <código>.rfa" na pasta de
    famílias, carrega e PromptForFamilyInstancePlacement.

107. 23/11/2026 — exportador DWG: "a área não está na layer correta" (texto “2.59 m²” da etiqueta de ambiente ficou
    na layer da etiqueta). JA para Revit 2.6.1. Motor AutoCAD, `Cleanup/JaStandards.MoveAreaTexts`: além de textos só
    de área/PD, agora (1) MText de várias linhas que mistura o nome com a área (“CIRC.\P2.59 m²”) é explodido no lugar
    (`Separar`: linhas pelo baseline; as de área/PD na layer de área, as outras na original); (2) número e unidade em
    textos separados na mesma linha (“2.59” + “m²”, “PD” + “2,80”) vão juntos (`Pares`); (3) definições de atributo
    (textos fixos de etiqueta) também; (4) `InstallationMover` não tira mais nada da layer de área; (5) o registro da
    exportação lista os textos com cara de área/PD que ficaram fora (`Sobras`). Teste: docs/testes/padrao_ja/test_area.py
    (espelho do IsAreaOrPd). Primeira versão feita pela rotina do GitHub (CI compilou).
108. 23/11/2026 — "crie uma página fixa de download". Página = Releases do GitHub. O workflow **Publicar versão**
    agora anexa os arquivos à Release se ela já existir (criada pelo site em Releases › Draft a new release, com a
    etiqueta vX.Y.Z no main) e, se não existir, cria. Além de `JA_Setup_X.Y.Z.exe` e do zip, anexa cópias com nome
    fixo (`JA_Setup.exe`, `JA_Instalador.zip`): o link
    https://github.com/L-shade/JA-TOOLS/releases/latest/download/JA_Setup.exe baixa sempre a última versão
    (está no README). Sem mudança no add-in (continua 2.6.1).
109. 23/11/2026 — o usuário criou a Release v2.6.1 pelo site, mas o **Publicar versão** falhou na verificação: no
    Windows o console do Python é cp1252 e um teste do rodapé quebrava ao imprimir “−” (UnicodeEncodeError). No Linux
    passava. `verificar.py` força UTF-8 (na própria saída e nos testes, `-X utf8`/PYTHONUTF8) e o job Windows do
    **Verificar e compilar** roda a verificação também, para pegar isso antes. A etiqueta v2.6.1 foi movida para o
    main com a correção e a Release recebeu o instalador.

### Pedidos em aberto
20. ~~Painéis de parede cortina~~ — feito na 1.9.0 (item 57).
21. ~~Folha "solta" na planta~~ — conferência automática + Conferir Planta na 1.9.0 (item 58); aguardando o
    resultado da Conferir Planta no projeto do usuário. Relato original: na família a janela padrão (Abrir 1 folha,
    700 mm, AL30) sai certa (marco 0–60, folha 15–45, vidro 27–33 mm), mas no projeto as linhas
    internas do marco (60) e da folha (45) aparecem ~15 mm para fora do lugar (marco 45, folha
    30) e o vidro fica certo. Usuário não mudou medidas nem tem versão antiga. Pedi print da
    família aberta pelo projeto (Editar família do Revit) para decidir: erro no gerado ou
    "amarrar" as linhas em planos de referência. Sem resposta ainda.

## 5. Problemas conhecidos / a verificar

- 04/11/2026: versão 1.1.0 compilada e testada pelo usuário, funcionando. Mudanças futuras
  continuam sem compilação na nuvem: pedir para rodar `CompilarTeste.bat` e mandar os erros.
- Seta de correr em planta já apareceu com a ponta torta/faltando no projeto — não confirmado se
  continua acontecendo depois da 1.1.0.
- Máscara da abertura: desde a 1.3.0 vem em faixas de 5 mm (50–450 mm); fora dessa faixa, ou sem o
  modelo de item de detalhe, volta a usar a espessura do modelo de família.
- Ao trocar a categoria de uma família já carregada (Modelo genérico → Gabinete), o Revit pode
  manter a categoria antiga: excluir a família e gerar de novo.
- `FilledRegion.CreateMaskingRegion` e `SetLineStyleId` são chamados por reflexão (se faltarem, só
  aparece um aviso).

## 6. Como trabalhar neste projeto

- Desde a 2.0.0 (18/11/2026) é uma pasta só, `JA` (JA.sln, src/JA.Revit); as pastas antigas JAEsquadrias e JATools
  ficam só como histórico. Os README antigos estão em docs/README-JA-Familias.md e docs/README-JA-Tools.md.
- Na nuvem dá para instalar o .NET 8 (`apt-get install dotnet-sdk-8.0`) e rodar a parte de
  geometria (`Core/Layout.cs` + `EsquadriaConfig` + `Catalog`) num programa de teste, e o
  `csc` do SDK para pegar erros de sintaxe; NuGet é bloqueado, então Revit/WPF não compilam aqui.
- Pastas atuais no computador do usuário (conta do Lucas, a partir de 04/11/2026):
  `C:\Users\lucas\Downloads\Nova pasta (2)\JAEsquadrias_8\JAEsquadrias` (JA Famílias) e
  `C:\Users\lucas\Downloads\Nova pasta (2)\JATools_3` (JA Tools).
- Com as pastas conectadas: trazer os arquivos para a nuvem, editar, checar chaves/parênteses com o
  script de balanço e gravar de volta direto na pasta. Sem acesso: entregar `JAEsquadrias.zip` e
  listar os arquivos a trocar.
- Responder sempre em português, de forma curta, dizendo o que mudou, que não foi compilado e
  como testar (extrair, `CompilarTeste.bat`, Editar Família › Atualizar).
- Não copiar hachuras de superfície dos PDFs do escritório.
- 04/11/2026: o usuário extraiu zips antigos por cima das pastas e as duas voltaram para versões
  velhas; restaurei comparando o tamanho dos arquivos. Antes de gravar, conferir com
  `device_list_dir` se a pasta não voltou. Pedi para não extrair zips antigos dentro das pastas.
