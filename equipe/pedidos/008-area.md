# 008 — Área da etiqueta de ambiente fora da layer de área (2.6.1)

<!-- JA-CONSENSO -->
> **Placar** (gerado — não edite): 🛠️ Aguardando o programador. QA REPROVADO (54) na iteração 1; falta a posição do programador.

| Iteração | Commit | QA | Nota | Programador | Consenso |
|---|---|---|---|---|---|
| 1 | `0abeb59` | REPROVADO | 54 | — |  |

<sub>Regras: docs/equipe/PROCESSO.md · máximo de 3 iterações.</sub>
<!-- /JA-CONSENSO -->

- **Estado:** em-dev
- **Branch:** `ajuste/area-layer-2`
- **Prioridade:** alta
- **Aberto em:** 23/11/2026 (pelo programador, no lugar do gerente; migrado da Issue #8 / PR #9 do GitHub)

## Pedido

### Objetivo
"A área não está na layer correta": no DWG exportado, o texto de área da etiqueta de ambiente (ex.: “BICICLETÁRIO / 272.60 m²”) continua na layer da etiqueta, em vez de ir para a layer de área.

### Contexto
Relato do diretor em 23/11/2026, depois da 2.6.1 (que já tentou corrigir o “CIRC. / 2.59 m²”). Na imagem, o nome está em Arial e a área em outra fonte (são textos separados), os dois na mesma cor, ou seja, na layer da etiqueta. Relacionado: MEMORIA itens 107 e 111. Issue aberta pelo programador no lugar do gerente.

### Critérios de aceite
1. No DWG exportado com o Padrão JA, o texto de área de cada etiqueta de ambiente (só número + m²) fica na layer de área (“JA - TXT_AREA_CONSTR” ou a escolhida), com cor ByLayer. (diretor)
2. O nome do ambiente continua na layer original.
3. Funciona com as formas como o export pode escrever a área: texto simples, texto com formatação (fonte, altura, sobrescrito), “m2”, “m²”, “㎡”, espaço não separável e caracteres invisíveis.
4. Se o nome e a área estão num texto só, de duas linhas, só a linha da área vai para a layer de área, inclusive quando o “²” é sobrescrito. (diretor)
5. Se uma parte do Padrão JA falhar (ex.: explodir pilares), as áreas são processadas mesmo assim, e o registro diz o que falhou. (diretor)
6. O registro da exportação lista os textos com cara de área que ficaram fora da layer, mostrando caracteres escondidos. (diretor)
7. (não regressão) Textos que não são área (nome, “P211”, “25x100”, “3.000”) não mudam de layer; o CI continua verde.

### Fora do escopo
Mudar a família de etiqueta no Revit.

### Prioridade
alta (padrão de entrega dos DWGs)

Os critérios marcados (diretor) só se veem no AutoCAD (PROCESSO, regra 7), marcação feita na migração para arquivos.

## Iterações

### Entrega 1 (programador)
**Entrega — iteração 1** (commit `0abeb59`, CI verde: Revit 2024–2027 + motores do AutoCAD)

Versão 2.6.2. A área da etiqueta de ambiente vai para a layer de área em mais formas do export:
- formatação de fonte e altura, "²" sobrescrito, ㎡, m2, espaço não separável e caracteres invisíveis;
- as partes do Padrão JA rodam independentes;
- o registro mostra o que ficou fora.

Limitação: a parte geométrica (separar as linhas de um texto de duas linhas) só se verifica no AutoCAD.

<!-- JA-ENTREGA
iteracao: 1
commit: 0abeb59
-->


### QA 1
#### QA — iteração 1 (commit 0abeb59)
**Veredito:** REPROVADO · **Nota:** 54/100 (critérios 26/60 · robustez 10/15 · clareza 8/10 · regressão 13/15 · menores −3)

Resumo: o reconhecimento do texto de área (espelho em Python) está bom e cobre bem as formas pedidas. Mas o que resolve
o pedido do diretor (mudar a layer, cor ByLayer, separar linhas, partes independentes, registro) só acontece no AutoCAD
e não tem evidência. O próprio PR diz que não se sabe como o "272.60 m²" do DWG do diretor foi escrito. Nenhum defeito
grave comprovado; reprova pela nota (critérios não verificados = 0). O roteiro abaixo cobre o que falta.

Testes rodados: `test_area.py` deu 43 ok, 0 falha. Escrevi `testes/padrao_ja/qa_area.py` (92 cenários, que reaproveitam
as funções do `test_area.py`): 86 ok, 6 diferentes do que eu esperava, analisados abaixo.

##### Critérios de aceite
1. ◐ (parcial) Área de cada etiqueta na layer de área, com cor ByLayer. O reconhecimento está certo: "272.60 m²" e 26
   variações minhas (sem espaço, vírgula, "1.272,60", M², espaços finos U+202F/U+2009/U+2007, U+2060, U+200E, `\U+33A1`,
   `\u+00b2`, `\f…;`, `\C7;`, `\W`, `\Q`, `\L`, `\S^2;`, "ÁREA:", "A=…", "Á" decomposto) dão "só área". A mudança de
   layer e a cor ByLayer não foram verificadas (AutoCAD). Ponto aberto: a cor pode estar embutida no conteúdo do MText
   (`\C7;`), e aí o texto fica "ByLayer" nas propriedades mas continua com a cor da etiqueta (pergunta 2).
2. ◐ (parcial) O nome continua na layer original. "BICICLETÁRIO" e "{\H2.5;\C1;SALA}" não são reconhecidos como área
   (meus cenários B). Quando nome e área estão no mesmo texto, isso depende da separação, que é feita no AutoCAD: não
   verificado.
3. ✅ Funciona com texto simples, formatação (fonte, altura, sobrescrito), m2, m², ㎡, espaço não separável e invisíveis.
   Os 13 casos do `test_area.py` mais os meus cenários A passam (`\H`, `\S2^;`, `\A`, `\T`, `\p`, `\~`, ㎡, fullwidth,
   NBSP, ZWSP, BOM, hífen suave, TAB). Uma falha menor: o "combining grapheme joiner" (U+034F), que também é invisível,
   não é removido: '272.60 m²\u034f' fica como "mistura" e não vai para a layer de área (ver Defeitos). O "²" desenhado
   como um texto separado (forma geométrica) depende do Pares, no AutoCAD.
4. ❔ Texto de duas linhas: só a linha da área muda, mesmo com "²" sobrescrito. Não verificado (AutoCAD, como diz o PR).
   O espelho também não cobre a forma real. `linhas()` divide por "\n", mas o MText do export usa `\P`, e
   `normalizar()` troca `\P` por espaço. Exemplo: `linhas('{\Farial|b0;BICICLETÁRIO}\P{\Fsimplex;272.60 m{\H0.7x;\S2^;}}')`
   devolve uma linha só, "mistura". O teste "MText nome + área" com "\n" não representa o DWG.
5. ❔ Se uma parte do Padrão JA falha, as áreas são feitas mesmo assim e o registro diz o que falhou. Não verificado: não
   há teste nem saída de registro no pacote.
6. ❔ O registro lista os textos com cara de área que ficaram fora, mostrando caracteres escondidos. Não verificado: o
   pacote não traz um exemplo da linha do registro.
7. ✅ Não regressão. "P211", "25x100", "3.000", "1.200", "CIRC.", "SALA" continuam "não é" (teste do programador). Meus
   cenários B (44 casos) também: "1:50", "ESC. 1:50", "A-101", "FOLHA 02/10", "ÁREA TÉCNICA", "m² por pessoa", "12 m³",
   `m%%179`, "12 m", "2,80 m", "2²", "PM2", "M2", "PD" sozinho, vazio, só ZWSP. As frases com área no meio (cenários C:
   "2 m² por pessoa", "ÁREA TÉCNICA 12 m²", "Área total do terreno: 1.500,00 m² (ver planta)") ficam como "mistura" e não
   mudam de layer. O CI está verde nos dois runs, a verificação dá 0 erro e os testes passam.

##### Defeitos
- [menor] Invisíveis que não são da categoria Cf continuam no texto. Reproduzir: `area_ou_pd('272.60 m²\u034f')`.
  Esperado (True, False), obtido (False, True). O mesmo vale para os seletores de variação U+FE00–U+FE0F. É raro, mas o
  critério 3 fala de "caracteres invisíveis".
- (risco, não é defeito comprovado) O padrão aceita espaço entre "m" e "2". O texto "3 m 2" e o "3 m₂" (subscrito, que
  o NFKC transforma em 2) viram "só área". Sozinhos são improváveis. Mas se o Pares juntar uma cota "3 m" com um número
  "2" ao lado, esse "2" iria para a layer de área (pergunta 4). "1M2" e "2M2" exatos também vão para a área.

##### Perguntas ao programador
1. Como o Separar divide as linhas? Pelo `\P` do conteúdo ou pela geometria? E quando o MText quebra a linha pela
   largura, sem `\P`? Na separação geométrica, o "²" sobrescrito (menor e mais alto) pode virar uma "linha" sozinha e
   deixar "272.60 m" na layer da etiqueta?
2. Ao pôr a cor ByLayer, o motor também tira as cores embutidas no conteúdo (`\C`, `\c`) do MText?
3. Se o export escreveu a etiqueta dentro de um bloco, os textos de dentro dele são tratados? E em todas as pranchas e
   viewports do DWG unido?
4. Que textos o Pares junta (distância, altura, alinhamento)? Uma cota "3.00 m" ao lado de um "2" pode virar área?
5. Dá para pedir ao diretor o LIST, ou o próprio DWG, do "272.60 m²" que escapou na 2.6.1 e pôr essa forma exata no
   `test_area.py`? Sem isso não há prova de que o caso real foi corrigido.
6. Registro: como é a linha (ex.: mostra "U+200B")? Tem limite em projeto grande? Lista também os mistos que o Separar
   já resolveu?
7. Como o diretor provoca uma falha de uma parte do Padrão JA para testar o critério 5?
8. Sugiro acrescentar ao `test_area.py` o caso com `\P` (critério 4), U+034F/U+FE0F e "3 m 2". Os cenários estão em
   `qa_area.py`.
9. O PR deixa desmarcada a caixa "Verificar e compilar verde", mas o CI está verde. A Issue cita o item 111 do MEMORIA
   como relacionado, e o PR diz que o item 111 é o novo, da 2.6.2. Qual dos dois está certo?
10. Fora do PR: o `test_rodape.py` imprime "FALHOU: atual: retângulo desenhado sem sobreposição 900.0" e mesmo assim
    conta como ok. É esperado?

##### Roteiro de teste no Revit (diretor)
1. No Revit, abra a planta com "BICICLETÁRIO 272.60 m²" e "CIRC. 2.59 m²" e use o Exportar DWG Unificado com o Padrão
   JA ligado. Esperado: a exportação termina sem erro.
2. No AutoCAD, clique no "272.60 m²". Esperado: layer "JA - TXT_AREA_CONSTR", cor ByLayer, e a cor na tela é a da layer
   de área (diferente da do nome). Se aparecer ByLayer mas a cor ainda for a do nome, anote: a cor está embutida no texto.
3. Clique no "BICICLETÁRIO". Esperado: continua na layer original, sem mudança.
4. Rode o comando LIST no texto de área e cole a saída no PR (tipo TEXT/MTEXT/bloco e conteúdo). Isso fecha a dúvida
   "como o export escreveu".
5. Se houver uma etiqueta com nome e área num texto só, em duas linhas, clique nas duas partes. Esperado: só a linha da
   área na layer de área, e o "²" junto do número, na mesma layer (não solto).
6. Confira textos que não podem mudar: códigos "P211", cotas "25x100" e "3.000", escala "1:50", número da folha, "ÁREA
   TÉCNICA". Esperado: todos na layer original. "CIRC. 2.59 m²" (o caso da 2.6.1) continua com a área na layer de área.
7. Exporte mais de uma prancha no mesmo DWG unido. Esperado: as áreas estão na layer de área em todas as pranchas.
8. Abra o registro da exportação e procure "Padrão JA: textos com cara de área/PD que ficaram fora…". Esperado: se a
   linha existir, cada texto aparece com a layer e os caracteres escondidos legíveis. Cole a linha no PR.
9. Se o programador explicar como provocar uma falha numa parte do Padrão JA (ex.: pilares), repita o passo 2. Esperado:
   as áreas continuam na layer de área e o registro diz qual parte falhou.

<!-- JA-QA
iteracao: 1
commit: 0abeb59
veredito: REPROVADO
nota: 54
-->
