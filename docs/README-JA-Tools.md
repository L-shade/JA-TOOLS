# JA Tools (Revit)

The add-in adds the **JA Tools** ribbon tab to Revit.

| Panel | Tools |
|---|---|
| **Modelagem** | Por Ambiente ▾ (Pisos, Forro, Rodapé e Tabica, Luminárias), Converter em Camadas |
| **Vistas** | Cortes Gerais, Fachada Planificada, Plantas por Nível, Vistas por Ambiente, Eixos ▾, Ocultar em Vista |
| **Detalhamento** | Detalhar Ambientes, Anotar Vista, Anotações ▾ (Etiquetar Elementos, Organizar Anotações), Gerar Detalhes, Hachuras ▾ (Hachura Única, Varinha Mágica) |
| **Cotas** | Cotas de Ambiente ▾ (por Ambiente, de Piso e Forro), Cotas de Parede ▾ (por Parede, por Seleção, por Pontos), Cotas de Pontos |
| **Pranchas** | Gestão de Pranchas, Renomear, Duplicar, Alinhar Vistas, Documentos Vinculados |
| **Exportar** | **Exportar Pranchas** (PDF + DWG único + DWG por prancha + PNG, described below), Exportar Tabelas |
| **Dados** | Gestor de Estilos, Renumerar Elementos, Editar Níveis, Remover Ambientes, Mudar Nível, Quadro de Acabamentos, Quadro de Áreas, Quantitativo de Pintura, Conferências ▾ (Acessibilidade, Escadas), Carimbo em Lote, Comparar Versões |
| **JA** | Central de Ajuda (offline page with every tool, step by step, with search), Padrões do Escritório |

**1.17 — tools merged** (one button, a choice inside; the sections below still describe each engine):
Plantas por Nível = Planta Baixa + Plantas de Forro + Luminotécnicas · Vistas por Ambiente = Vistas + Elevações + Isométrico por Ambiente ·
Cotas de Pontos = Luminárias + Pontos Elétricos + Hidrossanitários · Cotas por Seleção = Seleção + Totais (modo Fachada) ·
Cotas de Piso e Forro = Cotas por Piso + por Forro · Anotar Vista = Anotar Planta + Anotação Completa ·
Organizar Anotações = Ajustar + Alinhar Anotações · Hachura Única absorbs Unir Hachuras · Atualizar Fichas is a button in Gerar Detalhes ·
Rodapé e Tabica = Rodapé + Tabica por Ambiente · Converter em Camadas = Parede + Piso em Camadas · Exportar Vistas = the PNG format of Exportar Pranchas.
The options window of every tool (`OptionsDialog`) uses the compact chrome (`ToolChrome.DressCompact`); with a list it opens in two columns.

Planned in the next batch: Materiais and Importar (from a template chosen at run time).

Internal names (assembly `RevitDwgUnifier.Revit.dll`, folder `RevitDwgUnifier`, settings in `%AppData%\RevitDwgUnifier`) are kept, so updates install over the existing add-in.

## Interface (UX)

Every window uses one design system (`UI/JaTheme.xaml`): neutral surfaces, one accent colour per
ribbon group, 8 px spacing, one clear primary action. The texts come from one place,
`Tools/Common/ToolCatalog.cs` (description, numbered steps, tips, button verb, option help), which
feeds the ribbon tooltips, the dialog headers and the Help Center, so they never disagree.

- **Ribbon**: short tooltip = what the tool does; hold the mouse a little longer for the numbered
  steps; **F1** over a button opens the Help Center at that tool.
- **Tool dialogs** (`OptionsDialog`): header with icon, description and "Como usar" steps (they fold
  away by themselves after the first 3 uses); options in cards; lists with search, "3 de 12" counter,
  Todos / Nenhum / Inverter and Shift+click ranges; short choices as one-click pills; units next to
  numbers and ↑/↓ to step them; clickable `{variáveis}` for name patterns; "Procurar…" for folders;
  colour preview for hex fields; options that depend on another one grey out live; a help bar explains
  the option under the mouse; "Depois do OK" instructions shown as a highlighted *Próximo passo*;
  inline validation (no message boxes); main button named after the action ("Criar plantas");
  Enter / Esc / F1; "Restaurar padrões". All values are remembered.
- **Results** (`ResultDialog`): status at a glance (Concluído / com avisos / erro / antes de
  continuar), headline + cards for "Avisos" and "Erros", clickable paths, "Abrir pasta" / "Abrir DWG",
  "Copiar", "Abrir registro", and a Ctrl+Z reminder for tools that change the model.
- **Exportar Pranchas** (export hub): 1 list "O que exportar" with search, Folhas/Vistas filters and
  counter; 2 three format tiles (PDF, DWG único, DWG por prancha, any combination); 3 option tabs that
  appear only when the format that uses them is on (Arquivos, PDF, DWG, Layouts, Imagens, Avançado);
  clickable `{variáveis}` with a live file-name example; a footer that says what will be written
  ("12 folha(s) → 12 PDF(s) + 1 DWG único"). Progress shows every stage (PDF → DWG por prancha →
  Exportar do Revit → Unir no AutoCAD → Incorporar imagens → Concluir), elapsed time and an estimate;
  one result window covers all formats ("Abrir pasta", "Abrir DWG").
- **Help Center**: `%AppData%\RevitDwgUnifier\Ajuda\index.html`, regenerated from the catalog.

## Cotas: sempre a estrutura da parede (1.40; not compiled yet)

Rule: wall dimensions use the STRUCTURE ("osso") only, never a finish.
- `Tools/Common/ParedesCebola.cs` (shared with Ajustar Paredes Cebola, same prefs "AjustarCebola.esp/tol/palavras"):
  `DesvioDoCentro`, `Eixo`, `Acabamentos(type)` (finish width per side = layers outside the core + outer
  Finish1/Finish2/Membrane layers inside it), `ParedeDoRevestimento` (nearest thicker parallel overlapping wall,
  faces within tol), `EhRevestimento` (thin — or keyword with ≤ 10 cm — and against a thicker wall), `Estrutura`.
- `DimEngine.IsFinishWall`: all-finish layers, or a keyword name only when the type is ≤ 10 cm ("Alvenaria
  revestida 15 cm" is a wall), or `ParedesCebola.EhRevestimento` (cached per wall + stamp).
- `ReadCoreFaces`: offsets from `Acabamentos`; no structure face found = that side gives nothing and a warning
  (`DimEngine.CoreWarnings`, shown by `DimToolCommand.Report`) — the finish face is never used any more.
- `WallEndFaces`: only end faces whose centre lies in the structure band (finish layer ends, also wrapped ones, out).
- Cotas por Seleção (corrente and fachada): picked finish walls are replaced by their structure wall
  (`DimEngine.StructureWalls`); fachada "paredes que chegam" skip finish walls.
- Room side lengths (`DimLados`): core boundary; a segment on a finish wall is moved onto the room-side structure
  face of the wall behind it; corners = crossings of those lines; corner edges read at Fine detail.

## Ajustar Paredes Cebola (1.39, improved in 1.39.1; not compiled yet)

`Tools/Modeling/AjustarCebolaCommand.cs` (painel Modelagem, botão "Ajustar Cebola"; prefs "AjustarCebola").
The plan geometry is prototyped and tested in `docs/testes/ajustar_cebola/` (`python3 test_cebola.py`, 43 cases:
gap / inside / already right, flipped finish, finish and core located by faces or by an asymmetric core,
plaster + tile, tile inside plaster, two equal layers, finish over two walls, thin partition, too far,
L corners joined and butt, too far, T, inner × outer, oblique, seams with gap / overlap / different heights /
reversed piece, turned finish, finish on the wall axis, between two walls, plaster out of reach carrying its tile).

- Finish = straight basic wall ≤ N mm (60) or with a keyword in the type name. Centre line = location line +
  Orientation · (p − W/2), p = location line from the exterior face (WALL_KEY_REF_PARAM + core layers).
- `Emparelhar`: core of each finish = nearest non-finish parallel (~1°) wall overlapping ≥ 5 cm along / 3 cm in
  height, inner face from −tol to tol + 15 cm (room for layers). Coreless thin walls with no thicker coreless
  finish next to them are promoted to walls (partitions) and the search runs again. Per core side, inside out
  (by E; same place → thicker first): each goes onto the core (F = 0) or onto the outer face of the nearest
  overlapping layer below (whichever is closer to it); within tol → fixed (Base = that wall/layer, Prof),
  else it stays where it is but can still carry the next layer.
- Scope: selected (groups → members; selecting the wall brings its finishes), active view, project; the pairing
  always uses every wall of the project.
- Steps, each in `Lote` (GuardaGrupos; a refused batch is split in halves): 1) RotateElement (around the middle
  of its centre line) when turned, then MoveElement by F − E along the core normal; 2) corners between finish
  walls: both ends allow joins → centre-line crossing; otherwise butt (the longer to the far face of the other,
  the other to its near face); never more than half a wall; seams: pieces of the same layer (core, side, F)
  with a gap / overlap ≤ tolCanto and ≥ 90 % the same height meet at the middle; optional base/top;
  3) `NewAlignment` in a plan of the level (active first, up to 4 more) between the touching side faces
  (GetSideFaces with the right normal, sampled along the overlap and in height to avoid door holes);
  skipped when a locked dimension already joins the two; 4) JoinGeometry with every wall the finish touches
  behind it (the wall cuts). Changed finishes are left selected. Walls in groups / pinned skipped and listed;
  thin curved walls counted.

## Lote 1.38

- **Vistas por Ambiente** monta, se marcado, uma prancha por ambiente com as vistas novas (folha escolhida, numeração a partir do primeiro número, nome com {ambiente}, margens), arrumadas como no Detalhar Ambientes, sem anotar.
- **Forro por Ambiente** ganhou tabica, sanca fechada / aberta (faixa rebaixada, espelho, abertura para a luz; só em alguns ambientes, se quiser), substituição do forro que o botão já fez (marca em Comentários) e o acabamento do teto do ambiente.

## Lote 1.37

- **Conferir Acessibilidade** (NBR 9050, primeira conferência geométrica): vão livre das portas (largura da família − desconto), círculo de giro de 1,50 m nos sanitários acessíveis (louças, marcenaria e móveis do ambiente como obstáculo; busca em grade com refino) e largura mínima das circulações (raios das paredes), exigida pela extensão (0,90 / 1,20 / 1,50 m) ou fixa. Excel com abas Portas / Sanitários / Circulações; o que não atende fica selecionado.
- **Conferir Escadas**: espelho, piso, Blondel (2e + p), menor largura dos lances, desnível sem patamar e número de corrimãos (`GetAssociatedRailings`); referência NBR 9050 ou uso privativo.
- **Carimbo em Lote**: Informações do projeto + campos das pranchas (desenhado, projetado, verificado, aprovado, data / hoje) + "Nome = valor" em prancha, carimbo ou projeto; planilha dos campos ainda vazios.
- **Comparar Versões**: abre outro .rvt em segundo plano (desanexado se for central, fecha sem salvar) e compara ambientes (pelo UniqueId), portas e janelas por tipo, pranchas, níveis, metros de parede por tipo, categorias e vistas.
- **Quantitativo de Pintura**: paredes (perímetro × altura − aberturas − rodapé; ambientes revestidos fora) e teto, litros de tinta e selador em latas de 18 L e galões de 3,6 L.

## Melhorias 1.36

- **Quadro de Áreas**: the drafting view is emptied and redrawn in place (it stays on its sheet).
- **Etiquetar e Cotar em Lote**: walls whose openings are already in a dimension of the plan are skipped; optional interior room dimensions (DimRoomAll, rooms whose boundary walls are all dimensioned are skipped).
- **Quadro de Acabamentos**: material painted with the Paint tool on the face towards the room wins over the layer (Document.IsPainted / GetPaintedMaterial).
- **Verificar Modelo**: new checks — levels without a floor plan, unused groups, unused view templates, flat model lines; the last three also in Corrigir.
- **Planilha de Parâmetros**: generates one .xlsx with a sheet per ticked schedule (text cells, `Numeros = false`, so the round trip compares texts); import reads every visible sheet.

## Lote 1.35

- **Quadro de Acabamentos** (Dados): fills ROOM_FINISH_FLOOR / WALL / CEILING from the modelled layers (floor under the room point: highest floor within ±0.6 m of the room base whose sketch contains the point, its first layer with thickness; walls of the boundary: the layer on the room side, found by testing a point 80 mm along Orientation; ceiling: lowest one over the room, last layer; "Sem forro" text), optional overwrite; creates the room schedule "JA - Quadro de Acabamentos" (Nível, Nº, Ambiente, Área, Piso, Rodapé, Parede, Forro; sorted by level/number; area > 0).
- **Quadro de Áreas** (Dados): per level útil (rooms), construída (structural floors of the level, else all floors; or the areas of a chosen area scheme), não computável (room name keywords); permeável (room names + floor type names); lot area from property lines or typed; projeção = biggest level or a chosen one; TO, CA, TP. XLSX (2 sheets) in Documentos\JA Tools + drafting view "JA - Quadro de Áreas" (text notes and lines, 1:50, recreated).
- **Etiquetar e Cotar em Lote** (Cotas): for the ticked floor plans, tags untagged doors/windows (TagPlacer, office identifiers) and rooms, and one dimension chain per exterior wall with openings (WallEndFaces + WallFacesAcross + OpeningSides), outside the wall at the chosen gap. One transaction per plan.
- **Verificar Modelo › Corrigir**: after the report, an OptionsDialog per kind (switch "Apagar" off by default + Multi preview, all ticked): unused families, empty sheets, "Cópia" views on no sheet (not the active one), unplaced/unenclosed rooms; one transaction.

## Mudar Nível (1.34)

Panel Dados. Puts elements on another level without moving them: scope = selection / active view /
whole project, filtered by the ticked categories (counted on the selection when there is one, else on
the project); source = any level (base constraints only, plus top constraints when "Também a restrição
superior" is on) or one level (every parameter pointing at it). Engine: `LevelRehost.Revincular(doc,
elements, to, from, topo, ...)` — the same pairs and offset correction as Editar Níveis › Revincular
(`Move` now calls it). Elements in groups are skipped and counted; hosted doors/windows follow their host.

## Pacote do escritório e aviso de versão (1.33)

- `GerarPacoteEscritorio.bat` (build\make-package.ps1): finds the JA Famílias folder next to this one (or asks), runs both `make-installer.ps1`, and writes `dist\Pacote_JA_<date>\` with both setups, `versoes.txt` ("JA Tools=1.33.0", "JA Famílias=1.15.0"), `Instalar tudo.bat` (refuses while Revit is open; runs both setups with /SILENT; writes its own folder to `%APPDATA%\JA Arquitetos\pasta-atualizacoes.txt`) and LEIA-ME.txt.
- `Tools/Common/AvisoVersao.cs` (same code in JA Famílias `Common/AvisoVersao.cs`): at startup, if the update folder is set, reads `versoes.txt` in a background task; on the first Idling after it finishes, shows one notice per new version (remembered in `%APPDATA%\JA Arquitetos\avisado-<produto>.txt`). The folder can also be typed in JA Famílias › Configurações.

## Unir Elementos (1.26; faster in 1.29.1)

1.32: option "Mostrar o que será unido antes de aplicar" (on by default) — after the search, a confirmation lists the pairs per kind with examples; nothing changes before it.

Speed (1.29.1): walls/floors/columns/beams read once with role + box (no whole-project
`WhereElementIsNotElementType()` scan, no collector per element); neighbours from an in-memory 2 m
plan grid (huge slabs in a separate list); pairs already joined in the right order are skipped
before any geometry test; walls × walls use only the cheap side-by-side test; the remaining
neighbours of an element are tested in ONE `FilteredElementCollector(doc, ids)` +
`ElementIntersectsElementFilter(a)` pass; free pairs in batches of 1000.

`Tools/Modeling/UnirElementosCommand.cs` (painel Modelagem). Pairs found with
`BoundingBoxIntersectsFilter` (+ tolerance) and checked: parallel walls side by side (line or
concentric arcs, axes ≤ (w1+w2)/2 + tolerance apart, overlapping along and in height) = "paredes
cebola"; others by `ElementIntersectsElementFilter`, floors also by vertical contact. Cutting order:
column › beam › floor › wall; thicker wall cuts the thinner. `JoinGeometryUtils.JoinGeometry` +
`SwitchJoinOrder`; existing joins get the order fixed (option).

Groups are never broken: every batch runs in a transaction whose failures preprocessor
(`GuardaGrupos`) deletes warnings, resolves "joined but do not intersect" by unjoining, and rolls the
transaction back on ANY error (e.g. "changes to groups only in group edit mode", which Revit would
resolve by ungrouping). Refused batches are split in halves. Pairs inside one group: first tried
directly (works for a single copy); for a type with several copies the type is rebuilt in one
TransactionGroup: ungroup one copy (`UngroupMembers`), join, `NewGroup`, switch every other copy to
the new type, move each back by the change of its members' box centre, delete the old type, give the
new one the old name. Skipped (listed): nested groups, copies with excluded members, types with
attached detail groups.


## Paredes nas Lajes (1.27)

`Tools/Modeling/ParedesNasLajesCommand.cs` (painel Modelagem). For each basic wall: floors (and
optionally roofs) near its box; at 7 points along the location curve, a vertical line (on the axis and
just beside both faces, since a joined slab may have a hole where the wall is) is intersected with the
slab solids (`Solid.IntersectWithCurve`). Slab above = lowest underside higher than 5 cm over the wall
base. Top into the slab → top = underside; top short of it by ≤ N mm → raised (option); base inside the
slab below → base = slab top (option, off by default). Applied as a delta: unconnected walls change
`WALL_USER_HEIGHT_PARAM`, constrained walls `WALL_TOP_OFFSET`, base `WALL_BASE_OFFSET` (height
compensated). "Anexar" mode: `Wall.AddAttachment(id, AttachmentLocation.Top)` by reflection; sloped
slabs are only listed in exact-height mode. Skipped and listed: edited profile, top already attached,
slab over only part of the wall, walls going more than N mm past the slab top (multi-storey).

Groups: shared `GruposSeguros.cs` (also used by Unir Elementos now): `GuardaGrupos` rolls back any
change Revit would solve by ungrouping; free walls go in batches split in halves; grouped walls are
tried directly, then `GruposSeguros.RefazerTipo` when every copy needs the same adjustments
(signature per copy), otherwise listed as "ajuste em Editar grupo". Grouped walls are never attached.
## Central JA (1.24, Glyph-style automation)

Panel **JA** → **Central JA** and **Buscar Comando**; panel Pranchas → **Mapeador de Folhas**.

1.25: the Central is a live, non-blocking panel (`Tools/Central/CentralPainel.cs`) hosted either in
a Revit **dockable pane** (registered in `App.OnStartup`, id 6C1E4B7A-…, right side, KeepAlive
keyboard) or in a floating modeless window (`CentralJanela`, size/position remembered); ⧉ / ⇥ switch
and the choice is kept (`central.modo`). The ribbon button toggles it. The panels never call the
Revit API: `CentralHost.Pedir` queues work for one `ExternalEvent` (runs, reading levels/scope
boxes, scope preview, selection), then the panels refresh (Status after tasks/bundles; only the
header on view change). Layout: one search for tools + tasks + bundles (Enter runs the first),
4-segment tabs, compact rows with hover actions (▶ ☆ ⋯ menus), favourites + recent + folded groups,
tasks/bundles expand inline, scope as a chip with an inline editor, last run as one line.

- **Tarefas**: a tool + saved option values (`Tools/Common/Automacao.cs`, stored in
  `%AppData%\RevitDwgUnifier\central_ja.json`). Every `OptionsDialog` has "☆ Tarefa" in its footer.
  A task runs without its dialog: `OptionsDialog.Show` returns OK at once with the saved values
  (applied to ToolPrefs for the run and restored afterwards); result messages are captured
  (`ToolCommand.Show` → `Automacao.Capturar`) instead of shown. Tools with their own window
  (Exportar Pranchas, Gestor de Estilos…) become *interactive* tasks: the window opens with the saved
  options. "Configurar" reopens the tool in "Salvar tarefa" mode.
- **Pacotes**: ordered list of tasks (or tools), "parar no erro"; one click runs all.
- **Escopo (seleção unificada)**: none / active view / Revit selection / filter (categories, levels,
  scope box, name text, hand-picked exclusions). Before each step the elements are selected and the
  view opened, and MultiField lists of the option dialogs are pre-checked by name match.
- **Status**: per step ✓ / ! / ✕ / –, time, messages, created elements (DocumentChanged), buttons
  "Selecionar criados" and "Abrir vista criada". Last 15 runs of the session.
- **Pasta do escritório**: `*.jatarefas` files in a shared folder appear read-only for everyone;
  Importar / Exportar move tasks and bundles between people.
- **Buscar Comando**: palette over tools, tasks, bundles and Central tabs; accent-insensitive,
  word order free, initials ("dp"), recent first; `t:` / `p:` / `f:` prefixes. Assign a keyboard
  shortcut to it in Revit.
- **Mapeador de Folhas**: views not on any sheet (filters by type, level, text; Project Browser
  selection pre-checked) → one per view / N per sheet / as many as fit (same packer as Gestão de
  Pranchas, `LayoutEngine.Pack`), title block (free area detected from its lines), start number
  (used numbers skipped), name pattern `{vista} {vistas} {nivel} {tipo} {n}`, extra margin and gap;
  editable plan grid (number, name) → sheets created and views packed in one transaction.

## Parâmetros tools

| Tool | What it does |
|---|---|
| Renumerar Elementos | Sets Mark = prefix + number (digits, start) for doors, windows or walls. Order is by level, then top→bottom (row tolerance), then left→right; or by clicking in sequence; or only the selection. Duplicate-mark warnings are suppressed. |
| Documentos Vinculados | Like Ideate Sticky: Excel (.xlsx/.xlsm, .xls via Excel) and CSV as a native Revit table in a schedule header (number formats, fonts, colors, fills, borders, merged cells, widths/heights, hidden rows/cols skipped); PDF pages, Word (via Word → PDF) and Excel print (via Excel → PDF) as linked images. Stored in the model; warns on project open when sources changed and updates with one click. |
| Gestor de Estilos | Like Ideate StyleManager: every style of the project with its uses (text/dimension types, tags, line styles and patterns, fill patterns, filled region types, view templates/types, filters, materials, wall/floor/ceiling/roof types, family types, families). Where used (opens the view and selects), merge/replace, merge identical duplicates, purge unused, batch rename with preview, CSV list. |
| Editar Níveis | A table of all levels (name, elevation in m) where you can edit, add (optionally with a floor plan) and delete levels, and rename the plans that carried the old level name. |
| Remover Ambientes | Deletes unplaced rooms and placed rooms with zero area (not enclosed / redundant), after showing the counts. |

## Exportar tools

| Tool | What it does |
|---|---|
| Gestão de Pranchas | Window with views and schedules not yet on sheets (filterable) and the sheets. Drag views onto a sheet (or select and use "Colocar"), create sheets with a chosen title block (next number suggested), and "Organizar". Organizar shelf-packs the viewports inside the title block, minus margin, the bottom strip reserved for the stamp, and the gap between views. |
| Exportar Pranchas | Export hub, in one run: PDF per item or one combined PDF (colour / grey / B&W); **DWG único** (see below); DWG per item with the chosen export setup. Files are named by a pattern (`{numero} {nome} {revisao} {projeto} {codigo} {data}`). Order: PDF, DWG por prancha, then DWG único (AutoCAD). Since 1.23 the DWG por prancha runs through the same pipeline as the DWG único (each item merged alone, several Core Consoles in parallel), so every DWG option applies to both; both DWG kinds get, in AutoCAD, object colour ByLayer on every entity and the viewport layer set to not plot (options on by default). |
| Exportar Vistas | Views to PDF or PNG (pixel width chosen) without a sheet. For "transparent", near-white pixels get alpha 0, with soft edges for anti-aliasing. |
| Planilha de Parâmetros (1.32) | Round trip Revit → Excel → Revit. "Gerar": one row per element of a chosen schedule (first column = element ID, then the schedule's parameter columns) as CSV. "Importar": reads the edited .csv/.xlsx (`PlanilhaLeitor`), compares each cell with the model and, after a preview list (uncheck what you don't want), writes only the changed values (`Set` for text, `SetValueString` for the rest; Sim/Não for yes/no). Type parameters only with the option on; read-only and ElementId parameters are skipped. |
| Verificar Modelo (1.32) | Read-only report: Revit warnings, views on no sheet, empty sheets, names left as "Cópia", imported CAD, families without instances, rooms not placed/enclosed. |
| Exportar Tabelas | Schedules to .xlsx, written without external libraries. It adds a title row, a header in the office colour (hex, remembered), zebra rows, borders, a frozen header and widths fitted to the content. Since 1.30 values go as **data**: `LeitorNumero` reads the text Revit shows with the project's decimal and grouping symbols (`Doc.GetUnits()`), so "1.250,5 m²", "45%", "R$ 1.200,00" or "−3" become real numbers, with the unit, decimals and grouping kept in a custom number format (Excel shows the same and can sum and filter). Only known units count, so codes like J01, 007 or 101A, dates, fractions and feet-inches stay text. The option "Números como dados" can be switched off. Choose one workbook with one tab per schedule, or one file per schedule. |

## Modelagem tools

All modelling tools run in one transaction, suppress warnings, and tag what they create with the comment `JA Tools - ...`, so it is easy to select or delete later.

| Tool | What it does |
|---|---|
| Pisos por Ambiente | A floor from each room's finish boundary. Columns and voids become cut-outs. Rooms whose name contains the given words (BANHO; WC; …) are recessed by N cm. |
| Forro por Ambiente | A ceiling in each room at the chosen height, with a second height for rooms matching given words. |
| Luminárias por Ambiente | A regular grid per room (spacing target; distance to walls = half spacing; points kept ≥ ¼ spacing inside irregular rooms). A preview with the count per room must be confirmed. Face-based families go on the ceiling's lower face, ceiling-hosted ones on the ceiling, level-based ones at the ceiling height (or a given height). |
| Rodapé por Ambiente | Thin walls (chosen type, e.g. "RODAPÉ 1,5 cm") along each room's finish perimeter, inset by half their width, with mitred corners. They are cut at every door of the wall behind, are not room-bounding, and skip rooms matching given words. |
| Tabica por Ambiente | The same strip at the height of the ceiling found in the room (optionally starting below it). Rooms without a ceiling are skipped. |
| Parede em Camadas | Each selected compound wall becomes one wall per layer (single-layer types "JA - material N cm"), with the base/top constraints copied. Layer lines are intersected at shared corners and set to mitre joins. Doors and windows are re-created on the core layer (type, position, sill, flips, mark), and the other layers get matching wall openings. |
| Piso em Camadas | Each selected compound floor becomes one floor per layer, stacked from the original top face down, with the same outline and openings. Sloped floors are skipped. |

## Cotas tools

All Cotas tools work on the active plan (floor or ceiling) and share one engine (`Tools/Dimensions/DimEngine.cs`). References come from:

* **walls:** side faces (`HostObjectUtils.GetSideFaces`);
* **doors and windows:** Left/Right reference planes;
* **point families:** the centre plane perpendicular to the dimension (Center Left/Right or Front/Back, chosen from `HandOrientation`);
* **floors and ceilings:** edges of the top or bottom face (`ComputeReferences`);
* **grids:** the grid itself.

References are sorted along the dimension direction, de-duplicated (1 mm) and made into one chain. Each tool lets you choose the dimension type.

| Tool | What it does |
|---|---|
| Detalhar Ambientes | (Detalhamento) One click per room, in the spirit of Ascent.Detail: cropped plan with room dimensions in every wall direction, room tag (type chosen), element tags (type chosen per category; office identifiers by default) placed by `TagPlacer` (doors/windows in front of the opening and along the wall, long pieces such as a *tento* on the piece and along it, other pieces beside them in free space, never over walls/pieces/annotations), overlap check, plan regions over windows above the cut plane (`PlanRegionMaker`: copies a model plan region of the project and redraws its boundary with `SketchEditScope`, since the API cannot create one; fallback: raise the cut plane or report); four interior elevations annotated by `ElevationAnnotator` (overall width and floor-to-ceiling height, counter heights, a ceiling text such as FORRO EM VISTA); isometric with section box; views on a new sheet per room or on an existing sheet, first-fit packed in the usable area and shown in a draggable preview (`SheetLayoutDialog`) before being placed. |
| Etiquetar Elementos | (Detalhamento) Tags the chosen categories of the active view with the office identifiers (`content/Identificadores`, loaded on demand), skipping already tagged elements and elements without Mark/Type Mark; position above/right/below/centre, optional leader, then the overlap check on the new tags. |
| Alinhar Anotações | (Detalhamento) Aligns (left/centre/right/top/middle/bottom) or distributes the selected tags, room tags and texts. |
| Renomear Pranchas | (Publicação) Find/replace in sheet name and/or number, prefix, suffix, upper case, sequential renumbering (two-step, so numbers can be swapped). |
| Duplicar Pranchas | (Publicação) Copies of sheets: same title block and its instance values, sheet annotation, schedules and legends again, views duplicated / with detailing / dependent at the same positions. |
| Alinhar Vistas | (Publicação) Aligns viewports of several sheets to a reference sheet: same model point (project origin at the same paper point; plans line up) or same position (paired by view type and size order). |
| Padrões do Escritório | (Publicação) Exports / imports every JA Tools setting (`%AppData%\RevitDwgUnifier\*.json`) as a `.jatools` file for the team; import makes a backup first. |
| Anotar Planta | One click on the active plan: room dimensions, water/sewer point dimensions (rows from the walls), room tags on untagged rooms, tags on water points and sewer points (each with its own tag type; classified by pipe connectors – domestic cold/hot water vs sanitary/vent – or by editable name keywords; fixtures with both get two tags; re-running never duplicates), then the overlap check. |
| Ajustar Anotações | Overlap check alone on any view: tags, room tags and text notes that overlap each other or a dimension value are moved to the nearest free spot (rings, 16 directions; room tags stay in their room; nothing leaves the crop; a leader is added when moved far). Dimensions are never moved. |
| Cotas por Ambiente | For every room (or the selected ones): clear width and length, face to face of the bounding walls, through the room point (shifted by an offset, kept inside the room). |
| Cotas por Piso | For every floor: chain of the top-face edges crossed by the middle line, in X and Y, plus the overall dimension. |
| Cotas por Forro | Same for ceilings (bottom face), plus the nearest wall face on each side, so the trim/gap (tabica) is in the same chain. |
| Cotas por Parede | Click a wall: a chain through the clicked point across every parallel wall in that direction (configurable reach, optional grids). Repeats until Esc. |
| Cotas por Seleção | Select parallel walls, click the line position: one chain over both faces of each wall (or only the face toward the click). |
| Cotas Totais (por Seleção) | Select a facade's walls. Up to three rows are drawn outside the exterior face: openings, the walls meeting the facade, and the total. The ends are the outer faces of the corner walls (or the free end faces). |
| Cotas por Pontos | Click two points: every perpendicular wall face (and grid) the segment crosses is dimensioned. |
| Luminárias | Lights grouped into rows (5 cm): one chain per row in X and in Y, from the nearest wall face to the nearest wall face. |
| Pontos Elétricos / Hidrossanitários | Categories are selectable. Wall points (hosted or facing a wall) get one chain along the wall, in front of it, from the walls that meet it. Free points (drains) get X and Y rows like the lights. |

## Vistas tools

Every tool opens a small options dialog. It remembers the last values in `%AppData%\RevitDwgUnifier\jatools_prefs.json` and works in one transaction, so one Ctrl+Z undoes it. Missing tag families or references are listed in the final report; they are never fatal.

| Tool | What it does |
|---|---|
| Cortes Gerais | On a plan: click start, end and the side to look at. Repeat, or press Esc to finish. Each section is named CORTE A, B, C… and spans from the lowest level − margin to the highest level + margin, at the chosen depth, type, template and scale, with a chain dimension of the levels. |
| Gerar Detalhes | **One detail ("ficha") per type**, in the spirit of id:legend, for any family or system type. Opening is fast: only the category list is read; types, instance counts, legend support and view directions of a category are read when the user ticks it (inline progress), title block sizes when sheets are switched on; results are cached for the Revit session. Categories legend components accept are drawn with legend components; the others (plumbing fixtures, casework components, pipe fittings/accessories, mechanical equipment, structural framing…) – or every type with "Sempre de vistas do modelo" – are drawn from the model: sections around the first placed instance (front/back/sides/plan as elevations, "Corte" through the middle), everything else hidden, tight crop, overall dimensions from the family's Left/Right/Front/Back/Top/Bottom references; on sheets these views are placed inside the ficha's drawing area. If the project has no legend component at all, the fichas are drafting views and everything is drawn from the model. **Templates** (built-in "Ficha – marca à esquerda, com moldura", "… sem moldura", "Ficha – três vistas (frente, lateral e planta)", "Ficha compacta – marca acima, sem tabela"; user templates in `%AppData%\RevitDwgUnifier\legend_templates.json`) control layout, texts, views, dimensions (width, height, sill/floor, depth), scale (fixed / reduce / largest that fits, same for all) and the information table (any parameter or computed values: Descrição, Quantidade, Localização, Largura × Altura…). **Sheets**: title block, rows × columns, margins, card size fitted to the sheet, numbering, viewports without title. A progress window shows every step (prepare, draw fichas, sheets) and can cancel (nothing is changed). **Relação style** (presets "Relação – tabela, desenho e detalhes típicos" and "Relação – com marca e uma linha por compartimento"; for bancadas, granite pieces, sills, thresholds, fixtures, furniture…): table on top (optional mark column, ESPECIFICAÇÃO from any parameter, COMPARTIMENTO from the rooms of the instances or an instance parameter, optionally one row per compartment, QUANTIDADE as "1 x 6 PAV. = 6 BANCAS" when every level has the same count), the plan with width and depth on the left, the **typical details** (generic annotation families such as Det.Frontispício, Det.Saia, Det.Tira Americana, Det.Tento Simples, Det.Nicho Embutido) on the right – chosen per template and per type (remembered by type UniqueId), .rfa loadable from the window – and "TOTAL: n BANCAS" at the bottom; three cards per sheet with a framed note row ("OBS.: CONFERIR MEDIDAS NO LOCAL; COTAS EM METRO."). |
| Planta Baixa | One floor plan per chosen level (type, template, scale, name pattern), tagging rooms, doors and windows. |
| Plantas de Forro | Reflected ceiling plans per level; tags ceilings and rooms. |
| Plantas Luminotécnicas | Ceiling plans per level; tags lighting fixtures and, optionally, switches. |
| Vistas por Ambiente | One floor plan per room, cropped to the room's boundary + margin (annotation crop on); tags the room. |
| Isométrico por Ambiente | One isometric 3D view per room. The section box covers the room + margin and is cut at a chosen height, so the interior shows. Observer at SE/SW/NE/NW. |
| Elevações por Ambiente | An elevation marker at each room's centre with 4 interior elevations (A–D), each cropped to the room's width and height and far-clipped at the opposite wall. |
| Vistas de Eixos | Floor plans per level with every family of parallel grids dimensioned (chain + overall). |
| Eixos de Paredes | Grids on the axes of straight walls (selected, or all visible), merging collinear walls and skipping axes that already have a grid. Grids are renumbered (vertical 1, 2, 3 left→right; horizontal A, B, C top→bottom, or the reverse) and dimensioned. |
| Recalcular Eixos | Renames all grids in order and rebuilds the grid dimension chains of the active plan. |

## DWG único (in Exportar Pranchas)

The "DWG único" format of Exportar Pranchas (formerly the separate "Exportador de DWG" button) exports **several views and sheets into one DWG**.
Model content goes in **Model Space**. Each view or sheet gets its own correctly scaled
**Paper Space layout**, with its title block and viewports.

### Model montado como as folhas (DWG único)

On by default (DWG tab, "Model montado como as folhas"). After the layouts are built, step `modelsheets`
rebuilds Model Space as a set of sheet replicas, side by side in rows (20 per row by default), each with
its name above it: the title block and paper annotation are copied into model enlarged by the main view's
scale (largest viewport), and each viewport's model content (inside its window, minus its frozen layers)
is copied into a block `JA_<sheet>_V<n>` inserted with the model -> paper -> replica transform. A block
that crosses its window is XCLIPped; one fully inside at the main scale is exploded into plain geometry
(true size, dimension values unchanged); other scales stay as scaled blocks (dimension values kept).
Every viewport is re-pointed at its replica (same frame, same printed scale) and the scattered original
content is erased. 3D (non-top) viewports keep the original content.

### Padrão JA (DWG único and DWG por prancha)

On by default (DWG tab, card "Padrão JA"). Runs in AutoCAD Core Console: step `jastd` of the merge
for the unified DWG, command `RVTSTANDARDS` (files saved in place, same DWG version) for the per-sheet DWGs.

- **Pilares**: blocks of Columns / Structural Columns are exploded wherever they are (model space,
  layouts, inside other blocks, nested). A block counts as a column when its insert or all of its
  content is on a layer the export setup gives only to those categories, or its name or layer
  contains PILAR / COLUNA / COLUMN / COLS. Layer 0 and ByBlock take the insert's properties, as with EXPLODE.
  The hatches of the columns go to `JA - EST_PILAR_HATCH` (colour ID 6), ByLayer.
- **Ambientes**: texts that are only ceiling height and/or area (`PD = 2,80`, `P.D. 2.70 m`,
  `Pé-direito 2,80`, `A = 12,50 m²`, `12.50 m2`) go to `JA - TXT_AREA_CONSTR` (colour ID 22), ByLayer.
  The room name keeps its layer. A text that mixes the name with the area on one line is left
  where it is and reported.
- Layer names and colour IDs can be changed in the card.

## Why there are two assemblies

Revit's API cannot write several sheets into one DWG. `Document.Export(..., DWGExportOptions)`
always writes **one DWG per view or sheet**. No option in `DWGExportOptions` merges them.
So a real DWG engine has to combine the files, and the job has two halves:

```
 Revit process                                        AutoCAD Core Console (headless, separate process)
 ───────────────────────────────────────────────      ─────────────────────────────────────────────────
 WPF dialog → plan (dedupe)                            RVTUNIFY <manifest.json>
 TransactionGroup {                                    for each item:
   force crops, temp sheets for standalone views         ReadDwgFile → bind xrefs → analyse viewports
   Document.Export → items/S-123/S-123.dwg  ──────►      cull strays → tile (T) → rename clashing blocks
 } RollBack  (project untouched)                         master.Insert(Displacement(T))      → Model Space
 manifest.json  ───────────────────────────────►         clone layout, ViewTarget += T       → Layout tab
                                                       explode *U, hatches, text styles, dedupe, purge
 lock-safe copy ◄──────────── merged.dwg + result.json  SaveAs
```

| Project | Runs in | Target |
|---|---|---|
| `RevitDwgUnifier.Revit` | Revit | net48 (2024) / net8.0-windows (2025, 2026) / net10.0-windows (2027+) |
| `RevitDwgUnifier.AcadMerge` | `accoreconsole.exe` | net48 (AutoCAD 2021–2024) / net8.0-windows (2025, 2026) / net10.0-windows (2027+) |
| `src/Shared/MergeManifest.cs` | both (linked file) | the JSON contract |

## How the single-DWG layout merge works

1. **Every item becomes a sheet.** Sheets are exported as they are. A standalone view is placed
   on a *temporary* sheet inside a `TransactionGroup`. The add-in picks the smallest size of the
   chosen title block that fits the view. The group is **always rolled back**. Revit then writes
   the viewport, scale, rotation and title block itself, so the merge needs only one code path.
2. **Each source DWG keeps Revit's framing.** Every source DWG has model content plus one
   layout whose viewports frame that content. The merge never recomputes Revit geometry.
3. **Tiling.** Each item's model content is inserted with a translation **T**, so different
   sheets never overlap. Levels 1 and 2 would otherwise stack on top of each other. T places the
   item in a grid near 0,0. The tile size comes from the viewport windows, not from the raw
   extents, so stray objects cannot inflate it.
4. **Viewport re-targeting.** A viewport shows the model around `ViewTarget`. Its `ViewCenter`
   is measured relative to that target. If the model moves by T, then `ViewTarget += T` shows
   exactly the same picture. This holds for any scale, rotation or clip shape.
5. **Layouts are cloned** with `WblockCloneObjects`. Each layout keeps its title block, sheet
   annotations, viewports, non-rectangular clips and per-viewport layer freezes.
6. Each item's T, plus Revit's base point and survey point data, is stored in the named
   dictionary `RVT_UNIFY`. This lets you restore true coordinates later. The
   *TrueCoordinates* option skips tiling.

## Known export problems and how each is fixed

| # | Problem | Revit side | DWG side |
|---|---|---|---|
| 1 | Title block / model displacement | One `SharedCoords` base for the whole job; base and survey points measured, with a warning when far from origin | Tiling near 0,0, `ViewTarget += T`, paper space moved so the sheet starts at 0,0, offsets stored |
| 2 | Anonymous / nested blocks, xrefs | `MergedViews = true`, `ACAPreference = Geometry`, polymesh solids | Leftover xrefs insert-bound (no `$0$`), clashing block names compared by content, `*U` exploded, purge |
| 3 | Text overflow / missing glyphs | `TextTreatment.Exact`; export font table maps user fonts and fonts missing on this PC | Missing SHX/TTF → fallback; SHX styles used by non-ASCII text → TrueType; width factors fixed; optional MText slack |
| 4 | Hatch scaling | `LineScaling.PaperSpace`, optional `.pat` mapping file | Measures generated hatch lines and re-scales dense or invisible hatches; `MEASUREMENT` matched to `INSUNITS`; `PSLTSCALE=1` |
| 5 | Geometry outside the crop | Crop and annotation crop forced on (rolled back afterwards); crop frame hidden; scope boxes, ref planes and unreferenced tags hidden | Model entities outside every viewport window deleted before insert |
| 6 | Duplicated geometry | Each sheet and view exported once; views already on a selected sheet skipped; `PreserveCoincidentLines = false` | Exact coincident duplicates removed (type, layer, style, geometry) |
| 7 | Locked / bad file names | File names cleaned (illegal characters, reserved names, MAX_PATH) | `.dwg` lock or `.dwl/.dwl2` → `Name_01.dwg`; copy retries; ASCII-only work folder for the console |

## Images as embedded OLE objects

Revit exports images such as title block logos and pictures on sheets as separate files that the DWG links to. The add-in handles them in three steps:

1. **Merge (Core Console).** Each image definition is re-pathed to an absolute path, and the file is copied to `<name>_imagens` next to the final DWG.
2. **OLE pass (full AutoCAD, `RVTOLE`).** This step is optional and on by default. An AutoCAD window opens (it must not be minimized: the OLE paste needs a real view) and, for every image in every layout, does the following:
   * computes the image's real position on the sheet (images nested in the title block block get the full block transform);
   * prepares the bitmap: applies the clip, flattens transparency onto white, and applies mirror and rotation;
   * pastes the bitmap from the clipboard;
   * sizes the OLE frame to the exact rectangle the image covered, on the same layer.

   Afterwards the image references and definitions are removed. Core Console cannot do this step: OLE needs the Windows clipboard and an OLE container.
3. **Clean-up.** If every image was embedded, the `_imagens` folder created by the export is deleted. Otherwise the remaining images keep working as links to that folder.

The OLE pass uses the user's own AutoCAD profile. It restores FILEDIA and SECURELOAD after use, and it overwrites the clipboard contents.

## Layout finishing options

* **Images to back.** Raster images (in spaces and in block definitions) and the OLE objects made from them are always sent to the back of the draw order.
* **Same size for all viewports of a sheet** (`UniformViewportSize`, on by default). On a sheet with several viewports, any viewport more than 25% larger than the smallest one is shrunk to that size, around the model content that lies entirely inside its window. ViewCenter is shifted by the frame move divided by the scale, so the drawing stays at the same place on paper and at the same scale. These viewports are left alone:
  * clipped viewports;
  * rotated views;
  * viewports outside the title block.
* **Dimension text colour ID** (`DimTextColorIndex`, default 140, 0 = keep). DIMCLRT is set on every dimension style and every dimension entity, and the dimension blocks are regenerated.

## Paper units different from model units

`PaperUnits` sets the unit of paper space. The default is `Millimeter`; `Model` keeps paper space in the model unit. Example: DWG unit = Meter and paper = Millimeter.

1. **Scale factor.** f = model unit / paper unit, here 1000.
2. **Paper space.** All paper-space objects are scaled by f about 0,0, so the A4 sheet becomes 210 x 297.
3. **Model viewports.**
   * `CenterPoint`, `Width`, `Height` and `CustomScale` are multiplied by f. For example, 0.02 becomes 20, which is still 1:50.
   * `ViewCenter` and `ViewTarget` are unchanged, so each viewport shows exactly the same model area at Revit's scale.
4. **Paper-space dimensions.** DIMLFAC is divided by f, so they show the same values, and DIMSCALE is multiplied by f, so they look the same.
5. **Plot scale.** The plot scale is divided by f: "1000 mm = 1 unit" becomes "1 mm = 1 unit".
6. **Linetypes.** LTSCALE always stays 1 (PSLTSCALE = 1). Revit writes dash lengths as paper sizes in the export unit (3 mm = 0.003 m), so the linetype definitions themselves are multiplied by f: dash lengths, pattern length, and the scale and offset of text and shape elements.

Automatic layouts, built for sources that have no sheet layout, are already drawn in mm or inches and are left alone.

## Installer (Setup.exe)

On **any** Windows 10/11 PC (Revit and AutoCAD are not needed on the build machine) double-click
**`GerarInstalador.bat`**. It checks the prerequisites and offers to install what is missing with
winget (.NET 10 SDK, Inno Setup 6), builds everything and opens the result:

```
dist\Instalador\JATools_Setup_<version>.exe     <- copy this folder (or the zip) to any PC
dist\Instalador\LEIA-ME.txt
dist\JATools_Instalador_<version>.zip
```

What is packaged (all dependencies):

* the add-in for **Revit 2024, 2025, 2026 and 2027**. A version installed on the build machine is
  compiled against its own `RevitAPI.dll`; the others against the NuGet reference packages
  `Nice3point.Revit.Api.*` (never copied to the output);
* the DWG merge engine for each AutoCAD runtime: net48 (AutoCAD 2021-2024), net8 (2025-2026),
  net10 (2027+), from a local AutoCAD or from Autodesk's `AutoCAD.NET.Model` / `AutoCAD.NET.Core`
  packages (oldest release of each family, so it loads in all of them);
* the typical-detail families `content\Familias\Det.*.rfa` (saved in Revit 2026).

Nothing else is needed at runtime: Revit 2024 uses the .NET Framework 4.8 shipped with
Windows 10/11, Revit 2025+ bring their own .NET.

What Setup.exe does:

* first page: install for me (no admin, `%AppData%`) or for all users (`%ProgramData%`; Revit 2027+ only accepts all-users add-ins in `C:\Program Files\Autodesk\Revit\Addins\<year>`, so 2027 goes there and an old ProgramData copy is removed);
* installs into `...\Autodesk\Revit\Addins\<version>` for the Revit versions found (exe, registry
  or add-in folder); with none found it offers to install for all of them;
* removes the previous binaries first; settings, templates and logs are kept;
* warns when Revit is running and when no AutoCAD 2021+ is found (only the single DWG needs it);
* families in `...\RevitDwgUnifier\Familias` (Gerar Detalhes opens there); Start menu shortcuts;
* uninstalls from Windows "Installed apps". Silent install: `JATools_Setup_x.exe /VERYSILENT /ALLUSERS`.

## Build and deploy

Requirements: .NET SDK 8 (10 for the 2027 builds). Revit/AutoCAD installs are optional (NuGet
references are used for what is missing). AutoCAD LT has no Core Console, so it cannot run the merge.

```powershell
.\build\build.ps1                          # every version into .\dist
.\build\build.ps1 -RevitVersions 2025 -Deploy
.\build\build.ps1 -PreferNuGet             # ignore the local installs
```

Manual build: `dotnet build src\RevitDwgUnifier.Revit -c Release -p:RevitVersion=2025` and
`dotnet build src\RevitDwgUnifier.AcadMerge -c Release -p:AcadVersion=2025`. Local installs are used
when present (`-p:RevitInstallDir=`, `-p:AcadRefDir=`); otherwise NuGet (`-p:UseRevitNuGet=true`,
`-p:UseAcadNuGet=true -p:AcadNuGetVersion=25.0.*`).

**Without AutoCAD**, the add-in still exports and saves the individual DWGs to `<name>_parts\`,
along with `manifest.json`. The merge can run later on a machine that has AutoCAD.
A different engine, such as the ODA Drawings SDK, can be plugged in through `IDwgMergeEngine`.

## Logs

* `%LOCALAPPDATA%\RevitDwgUnifier\Logs\RevitDwgUnifier_yyyyMMdd.log` (kept for 30 days)
* When *Keep intermediate files* is on, the work folder in `%TEMP%\RvtDwgU\…` keeps `job.log`,
  `merge.log`, `accoreconsole_output.log`, `manifest.json` and `result.json`.

## Check on first build (written without access to the Revit/AutoCAD SDKs)

These members are used as documented, but confirm them against your SDK version:

* Central JA (1.24): running `UnifiedDwgExportCommand` from code builds its `ExternalCommandData`
  with `FormatterServices.GetUninitializedObject` and sets `Application` by reflection — confirm it
  opens from a task/bundle. `View.Outline` of views not on sheets (Mapeador de Folhas sizes) and
  `Viewport.GetBoxOutline` after `Regenerate`.
* Revit: `ExportFontTable.ContainsKey/SetFontInfo/Add`, `ExportFontInfo(string)`,
  `ExportDWGSettings.GetActivePredefinedSettings`, `DWGExportOptions.PreserveCoincidentLines`.
* AutoCAD: `Hatch.NumberOfHatchLines`, `Database.Msltscale`. `Layout.Initialize()` is called by
  reflection on purpose.
* Runtime behaviour to confirm on a real project: that `Document.Export` runs while the outer
  `TransactionGroup` is open (all inner transactions are already committed). The fallback is to
  commit the staging group and delete the temporary elements afterwards.
* OLE pass (AutoCAD .NET):
  * `Ole2Frame.Position3d` (set through reflection, with a scale-and-move fallback);
  * `RasterImage.GetClipBoundary` / `PixelToModelTransform` (reflection);
  * `Editor.Command("_.PASTECLIP", point)`: AutoCAD must create an OLE frame from a clipboard bitmap and prompt for an insertion point.

### Progress box

Every tool shows a progress window (`ToolProgress` + `UI/ProgressWindow`) from the moment the options are confirmed until the result: steps, bar, current item and elapsed time. `OptionsDialog` arms it on OK, `FailureSwallower.Attach` keeps it painted, `ToolCommand.Show` closes it. Long tools report steps and counts (Detalhar Ambientes, with Cancel that undoes everything; Etiquetar Elementos; Anotar Planta; per-room and per-level tools; Duplicar Pranchas). Tools that click in the view or run their own window set `PicksInView = true`.

### Sheet editor (Detalhar Ambientes preview)

`SheetLayoutDialog` is a small layout editor shown before the viewports are placed: drag with snapping, multi-select (Ctrl+click, selection window), align left/centre/right/top/middle/bottom (one view aligns to the usable area), distribute, centre the group, auto-arrange, fit the largest standard scale, per-view scale (box resized ∝ 1/scale; applied to the view on OK), keep a view off the sheet, zoom (Ctrl+wheel) and label text size, editable margins, change the title block or move the views to another sheet (through `ISheetLayoutHost`, inside the open transaction), undo/redo. "Repeat this layout" stores the corners/scales per kind of view (plan, isometric, elevations A–D) and builds the next sheets without asking when they fit. The isometric gets a crop fitted to its section box so its viewport is the size of the drawing.

### Speed / compartments

* `TagPlacer.TagMany`: all categories of a plan in one batch (one regeneration, one obstacle scan); each piece keeps to the room it is in.
* Elevations are created together and regenerated once; `ElevationAnnotator` reads walls, floor and ceiling once per room.
* Detalhar Ambientes "Rápido" mode: only views and sheets. **Anotação Completa** (`AnotacaoCompletaCommand`) annotates existing views afterwards — the active plan/elevation, the views selected in the browser, or every view of the active sheet.
* The progress box shows the time left once the first room is done.

### Performance (1.6.0)

* `SessionCache` (cleared by `ToolCommand` at the start and end of every tool): wall side faces (`DimEngine.SideFaces`, keyed by width/ends), room boundaries and boxes (`RevitUtil.Boundary` / `RoomBox`, keyed by area/offsets), view names (`RevitUtil.Rename`), instances per family type (`LegendEngine.InstancesOfType`, one pass per category instead of a slow filter per type), the model plan region.
* Spatial grids: `ObbGrid` in `TagPlacer` (candidates compared only with nearby obstacles, room test skipped when a candidate is already worse) and `BoxGrid` in `AnnotationAdjuster`.
* Plans per level tag all chosen categories in one `TagPlacer.TagMany` batch (`RevitUtil.TagCategories`).
* Elevation counters: top face read once per piece. `PlanAnnotation.BelongsTo` rejects far pieces by the room box before `IsPointInRoom`.
* Sheet preview: the fixed layer (paper, margins, title block lines) is built once per zoom/sheet/margins; dragging only redraws the views.
* `Logger` buffers lines (flush every 1.5 s / 64 lines / on error / end of tool / Revit exit). `IconFactory.Glyph` caches icons.
* Code: Detalhamento tools split into `DetalharAmbientesCommand.cs`, `EtiquetarElementosCommand.cs`, `AlinharAnotacoesCommand.cs`; one category→tag map (`TagPlacer.TagCategoryFor`); unused `SheetPacker` removed.

## Detalhar Ambientes — padrão da vista modelo (1.28)

Learned from the office reference plan "FORNECER CLAUDE" (dumped with **Estudar Vista**,
`Tools/Views/EstudarVistaCommand.cs`). `Tools/Detail/PadraoPlanta.cs`: names compared without accents
and without the "LB" prefix/suffix (`Norm`, `Melhor`). Defaults: view type "Planta. Nivel Acabado",
template "PE - Plantas. Civil", room tag "IND NOME AMBIENTE : NOME E AREA", linear dims
"EXECUTIVO - 1,5mm_RomansD", door tag "Tag Porta EX : Com Soleira", window tag "Tag Janela EX",
railing tag "TAG - Identificador de Guarda corpo" (railings added to the tag categories). New in the
plan: spot elevation "JA - Nível (Relativo)" on the finish floor of each room, 35 cm left / 55 cm below
the room point, relative base = the "Osso" level of the floor (`SPOT_ELEV_RELATIVE_BASE`); spot
"Soleira/Filete - Relativo a Nível" on the top face of the soleira/floor under each door; angular
dimension ("1,5mm_vermelho") at every room corner that is not square, between the two wall faces.

## Padronizar Nomes (1.29)

`Tools/Common/PadraoNomes.cs` = table old → new technical names ("JA - Etiqueta - Porta : Código +
Soleira", "JA - Nível - Piso Acabado (P.A./P.O.)", "JA - Cota Linear - Executivo 1,5 mm", "JA - Texto
1,5 mm - Arial", "JA - Planta - Nível Acabado", "JA - PE - Planta Baixa Civil"...). Old names matched
without accents / "LB". `Tools/Parameters/PadronizarNomesCommand.cs` (painel Dados) lists the matches
and renames families, family types (only inside the right family), dimension/spot/text types, the view
type and the template; a clash gets " (2)". `PadraoPlanta` and `TagLibrary.FindSymbol` accept new and
old names.
