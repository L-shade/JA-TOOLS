# JA para Revit — instruções para o Claude

Escritório: JA Arquitetos Associados (Brasil). **Responda sempre em português, curto.** Diga o que mudou, se foi
compilado (pela execução do GitHub Actions) e como testar.

## O projeto
- Um add-in, uma DLL (`JA.Revit.dll`), duas abas: **JA Famílias** (`src/JA.Revit/JAFamilias`, namespace
  `JAEsquadrias`) e **JA Tools** (`src/JA.Revit/JATools`, namespace `RevitDwgUnifier`). Revit 2024 (net48),
  2025/2026 (net8), 2027 (net10). Motor de união do DWG para o AutoCAD em `src/RevitDwgUnifier.AcadMerge`.
- Visualizador IFC (`src/JA.IfcViewer`, WPF net48) em desenvolvimento, fora da solução.
- `MEMORIA.md`: histórico numerado de pedidos e decisões — leia o fim antes de mudar algo parecido; acrescente um
  item a cada versão.
- Catálogo das ferramentas (ajuda, faixa, ícones): `JATools/Tools/Common/ToolCatalog*.cs`; ícones em
  `JATools/UI/IconFactory.cs`; botões em `JATools/App.cs` e `JAFamilias/App.cs`.
- Base dos comandos: `ToolCommand` (Run, Title, Show, OptionsDialog, telemetria); diálogos com `OptionsDialog`.

## Rotina (docs/ROTINA.md)
1. Trabalhe num branch (`ajuste/...`, `nova/...`); nunca direto no `main`.
2. Antes de enviar: `python tools/verificar/verificar.py` (tem de dar 0 erro).
3. Envie: o workflow **Verificar e compilar** compila Revit 2024–2027 + AutoCAD. Acompanhe com
   `gh run list --branch <branch>` / `gh run view <id> --log-failed`; corrija até ficar verde.
4. Versão: `python tools/versao.py patch|minor|X.Y.Z` + item no `MEMORIA.md`.
5. Pull request para o `main`; publicar = etiqueta `vX.Y.Z` (workflow **Publicar versão**).
6. Uma armadilha nova vira verificação em `tools/verificar/verificar.py`.

## Armadilhas já vistas (C#)
- `Group` ambíguo (Autodesk.Revit.DB × System.Text.RegularExpressions): use `Autodesk.Revit.DB.Group`.
- Grupos de métodos (`Select(Clean)`) sobrevivem a renomeações: procure ao mover métodos.
- Ramos de `?:` com subtipos diferentes de Element precisam de `(Element)`.
- Classe aninhada e método com o mesmo nome na mesma classe (CS0102).
- net48: sem `Math.Clamp`, `GetValueOrDefault`, `string.Contains(string, StringComparison)`, ranges/índices `^1`.
- Toda classe concreta que herda de `ToolCommand` precisa de `[Transaction(TransactionMode.Manual)]`.
- LTSCALE do DWG é sempre 1 (o motor reescala as definições de linha; o model usa escala de anotação + MSLTSCALE).
