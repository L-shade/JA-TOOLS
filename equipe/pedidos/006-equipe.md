# 006 — Equipe: gerente, diretor, programador e QA com consenso

<!-- JA-CONSENSO -->
> **Placar** (gerado — não edite): 🔁 Nova iteração. sem consenso na iteração 2 (QA REPROVADO, programador CONCORDO); faltam 1 iteração(ões).

| Iteração | Commit | QA | Nota | Programador | Consenso |
|---|---|---|---|---|---|
| 1 | `34cb03e` | REPROVADO | 58 | CONCORDO |  |
| 2 | `f3a8ac6` | REPROVADO | 60 | CONCORDO |  |

<sub>Regras: docs/equipe/PROCESSO.md · máximo de 3 iterações.</sub>
<!-- /JA-CONSENSO -->

- **Estado:** em-dev
- **Branch:** `nova/equipe`
- **Prioridade:** alta
- **Aberto em:** 23/11/2026 (pelo programador, no lugar do gerente; migrado da Issue #6 / PR #7 do GitHub)

## Pedido

### Objetivo
Estrutura de equipe: gerente (outra conversa), diretor (usuário), programador (conversa do código) e QA (agente que não conhece o código); a versão é liberada quando programador e QA entram em consenso; se não entrarem, depois da 3ª iteração vale a melhor versão.

### Contexto
Pedido do diretor em 23/11/2026, na conversa do programador. Aberta pelo programador no lugar do gerente (a conversa do gerente ainda não existe).

### Critérios de aceite
1. Existe um documento do processo com os quatro papéis, o fluxo de uma iteração e a regra de liberação (consenso ou melhor versão após 3 iterações).
2. O diretor consegue abrir a conversa do gerente só colando um texto curto indicado na documentação.
3. O QA recebe um pacote sem código-fonte (sem arquivos de src/ nem trechos de código) e tem regras de nota e veredito claras.
4. Um placar automático lê os comentários do PR e diz: aguardando, nova iteração, liberar (consenso) ou melhor versão (maior nota após 3 iterações); empate escolhe a mais nova.
5. A posição do programador só vale depois do relatório do QA da mesma iteração e do mesmo commit.
6. O diretor pode decidir por cima (liberar ou parar).
7. Liberar faz o merge do commit escolhido e dispara a publicação; recusa se a versão não mudou ou se o CI não estiver verde.
8. (não regressão) Verificar e compilar continua verde; a verificação continua com 0 erro; nada do add-in muda.

### Fora do escopo
Testar o add-in dentro do Revit pelo QA (não há Revit no CI).

### Prioridade
alta

### Alteração do pedido (23/11/2026, diretor)
“Vamos sem usar o GitHub”: a equipe toda trabalha sem Issues nem PRs — pedidos, entregas, relatórios do QA e placar
em arquivos (`equipe/`); o GitHub só guarda o código e compila. Os critérios passam a ler assim:
- 4\. Um placar automático lê **o arquivo do pedido** e diz: aguardando, nova iteração, liberar (consenso) ou melhor
  versão (após 3 iterações); empate escolhe a mais nova.
- 7\. Liberar faz o merge do commit escolhido no `main` e dispara a publicação **quando o add-in muda**; recusa se a
  versão não mudou (nesse caso) ou se a compilação não estiver verde. Pedido sem mudança no add-in: só o merge.
- Proposta do programador, **aguardando o diretor**: critério 3 = “o QA não recebe código do add-in (C#, XAML,
  PowerShell); os testes automáticos em Python, inclusive os espelhos dos algoritmos e o placar, podem ir no pacote”.

## Iterações

### Entrega 1 (programador)
**Entrega — iteração 1** (commit `34cb03e`, CI verde)

O que está pronto: processo e papéis em `docs/equipe/`, placar automático, pacote do QA sem código, liberação por script, modelos de Issue e PR. Nada muda no add-in.
Limitações: o QA não roda o Revit; o workflow do placar só roda sozinho depois do merge.

<!-- JA-ENTREGA
iteracao: 1
commit: 34cb03e
-->


### QA 1
#### QA — iteração 1 (commit 34cb03e)
**Veredito:** REPROVADO · **Nota:** 58/100 (critérios 45/60 · robustez 8/15 · clareza 7/10 · regressão 13/15 · 5 defeitos menores −15)

Cálculo: 8 critérios de 7,5 pontos cada; 6 ✅ = 45 (o ❌ e o ❔ valem 0). 45 + 8 + 7 + 13 = 73; com os 5 menores (−3 cada), dá **58**. Reprovado por 3 defeitos graves (sem crítico, então não há teto de 40).

O que rodei: `test_consenso.py` **não roda no pacote** (`ModuleNotFoundError: No module named 'consenso'`, porque o import procura `/tmp/tools/equipe`, que não existe). Escrevi 10 cenários novos em `testes/equipe/qa_extra.py`; eles também ficaram **não verificados** pelo mesmo motivo. Os outros testes do pacote rodam com código de saída 0: cebola 43 ok, fórmulas 45/45, ifc 111 ok, área 29 ok, quinas e rodapé com 0. Quinas e rodapé imprimem linhas "FALHOU" nos blocos "nova"/"atual", mas são anteriores a este PR.

##### Critérios de aceite
1. ✅ **Documento do processo com os 4 papéis, o fluxo de uma iteração e a regra de liberação.** `PROCESSO.md` traz a tabela de papéis, o fluxo da iteração N (1 a 3) e as regras 2 e 4 (consenso / maior nota, empate fica com a mais nova). A contradição da iteração 3 está no defeito G2.
2. ✅ **O diretor abre o gerente colando um texto curto.** O topo do `GERENTE.md` tem as 2 linhas para colar, e o `DIRETOR.md` aponta para elas.
3. ❌ **Pacote sem código-fonte (sem arquivos de src/ nem trechos de código).** Não há arquivos de `src/` nem C#, mas o pacote traz código do produto espelhado em Python:
   - `testes/formulas/formula.py`: "Espelho em Python do Formula.cs";
   - `testes/ifc/geo.py`: "Espelho em Python de Triangulator.cs e Csg.cs";
   - `testes/ajustar_cebola/cebola.py`: "Mirrors the C# logic";
   - `testes/rodape/rodape.py`: "prototype of the JA Famílias rodapé engine".
   As regras de nota e veredito existem (tabela do `PROCESSO.md`). Ver G3.
4. ✅ **O placar diz aguardando / nova iteração / liberar / melhor versão; empate fica com a mais nova.** A evidência é o `verificacao.txt`: "test_consenso.py (17 ok, 0 falha(s))". Os 17 casos do teste batem com os estados pedidos: AGUARDANDO_QA/PROGRAMADOR, NOVA_ITERACAO, LIBERAR, MELHOR_VERSAO e o empate → C.
   - Não verifiquei a leitura dos comentários reais no GitHub: o workflow só roda depois do merge.
   - Não consegui rodar o teste eu mesmo.
5. ✅ **A posição do programador só vale depois do relatório do QA da mesma iteração e do mesmo commit.** Cobrem isso os casos "posição antes do QA não conta", "QA reavalia: posição antiga cai" e "aprovado mas commit diferente" (17 ok). Ressalva no menor m2.
6. ✅ **O diretor decide por cima (liberar ou parar).** O `DIRETOR.md` traz o bloco, e os casos "diretor libera" → DIRETOR e "diretor para" → PARAR estão entre os 17 ok. Há buracos: retomar depois de PARAR, LIBERAR sem commit e quem pode postar o bloco (ver perguntas).
7. ❔ **Liberar faz o merge e publica; recusa se a versão não mudou ou se o CI não está verde.** O `liberar.py` não está no pacote e não há teste nem saída dele. Ele vai para o roteiro do diretor. Ver também G1.
8. ✅ **Não regressão.** No CI de push do 34cb03e, os 3 jobs (Verificar, Compilar 2024–2027 + AutoCAD, Visualizador IFC) deram success, com o artefato `JA-para-Revit-34cb03e…` (7 MB). A verificação deu "0 erro(s), 0 aviso(s)" e a versão continua 2.6.1. Ressalva: a execução do pull_request ainda estava `in_progress` quando o pacote foi montado (m3).

##### Defeitos
- **[grave] G1 — A regra da versão impede liberar este próprio PR.** O critério 7 diz que o liberar "recusa se a versão não mudou". A regra 1 do PROCESSO diz que a versão "sobe uma vez, na iteração 1". Mas o PR diz "sem versão nova (o add-in não muda)", e a verificação mostra JA 2.6.1.
  - Reproduzir: chegar ao consenso no PR #7 e rodar `liberar.py --pr 7`.
  - Esperado: o PR sai, ou há uma regra escrita para PRs que não mudam o add-in (docs/processo).
  - Obtido pelos documentos: ou o liberar recusa (o primeiro ensaio não fecha), ou não recusa (o critério 7 falha). E "Publicar versão" de novo na 2.6.1 colide com a Release existente.
- **[grave] G2 — Na 3ª iteração não há consenso possível, e a "melhor versão" pode passar um REPROVADO na frente de um APROVADO.**
  - O caso "na 3 o QA ainda não leu a posição" espera MELHOR_VERSAO logo depois do 3º relatório, sem a posição do programador. O fluxo do PROCESSO manda: relatório → posição → placar.
  - Além disso, um defeito grave reprova mas não limita a nota (só o crítico limita a 40).
  - Cenário: it.2 = REPROVADO 85 (com grave aberto); it.3 = APROVADO 82, e o programador CONCORDA.
  - Esperado: LIBERAR C (consenso).
  - Obtido pela lógica do teste: MELHOR_VERSAO B, ou seja, sai o commit com defeito grave aberto, sem aviso. O cenário está em `qa_extra.py` e não pôde ser rodado.
- **[grave] G3 — O pacote do QA tem trechos de código do produto** (os 4 espelhos em Python citados no critério 3). Isso fere a letra do critério 3.
  - Duas saídas: tirar os espelhos (e aceitar que esses testes não rodam no pacote), ou o gerente reescrever o critério ("sem código do add-in; protótipos de teste em Python são permitidos").
  - O pacote também é incoerente: traz os espelhos dos outros testes, mas não traz o `consenso.py`.
- **[menor] m1 — `test_consenso.py` não roda no pacote** (ImportError do caminho `../../../tools/equipe`). Isso contradiz o QA.md ("testes/ — pode ler e rodar") e o "Como testar" do PR. Sem ele, o QA não verifica o placar nem cenários novos.
- **[menor] m2 — Posição com o commit errado vira NOVA_ITERACAO.** O caso "aprovado mas commit diferente" espera NOVA_ITERACAO. Pela regra 2 essa posição "não vale", então o placar deveria continuar AGUARDANDO_PROGRAMADOR. Um erro de digitação do commit gasta 1 das 3 iterações.
- **[menor] m3 — O PR se contradiz sobre o CI.** A conferência tem "[ ] Verificar e compilar verde" desmarcado, mas o comentário de entrega diz "CI verde". E a execução do pull_request estava `in_progress` no pacote.
- **[menor] m4 — Menção "@programador" no `GERENTE.md` (item 5).** No GitHub isso notifica a conta real chamada "programador", se ela existir. Escreva sem `@` ou entre crases.
- **[menor] m5 — Caminhos e descrição do pacote desatualizados.**
  - Os documentos citam `docs/equipe/*.md` e `docs/testes/equipe/…`, mas no pacote estão em `docs/` e `testes/`.
  - O QA.md diz que `docs/` traz "README e ROTINA", e veio mais.
  - O "Como testar" pergunta "estão no pacote? se não, pedir". O pacote deveria garantir isso.

##### Perguntas ao programador
1. Na iteração 3, se o QA aprova e o programador CONCORDA, o placar dá LIBERAR? Por que MELHOR_VERSAO antes da posição?
2. A "melhor versão" deveria preferir APROVADO a REPROVADO, ou um defeito grave deveria limitar a nota (por exemplo, a 79)?
3. Como o `liberar.py` trata o PR #7, que não sobe a versão? Qual é a regra para PRs que não mudam o add-in?
4. Todas as conversas postam com a mesma conta do GitHub? Se sim, o placar não distingue quem postou JA-QA ou JA-DIRETOR, e a confiança é só de protocolo. Isso está escrito em algum lugar?
5. Blocos dentro de citação (`> `), de bloco de código, do corpo do PR ou de exemplos do modelo (`abc1234`) contam?
6. Se alguém copia o modelo ao pé da letra (`veredito: APROVADO        (ou REPROVADO)`), o que o placar lê? E se a palavra REPROVADO for procurada no texto?
7. Depois de `decisao: PARAR`, como se retoma? E `LIBERAR` sem `commit:`, o que faz?
8. O placar barra APROVADO com nota < 80? E o que faz com um bloco de iteração 4, com o mesmo commit em duas iterações (quando se prova que o QA errou sem mudar o código) ou com iteração sem JA-ENTREGA?
9. Qual é o tamanho mínimo do prefixo do commit para casar (o teste aceita 7 contra 11)?
10. O "−3 por menor" é aplicado no total, além das partes, ou dentro de robustez/clareza? E "proporcional" admite um critério parcialmente atendido?
11. As etiquetas `pedido`, `pronto-para-dev`, `em-dev` e o modelo "Pedido da equipe" já existem no repositório?
12. O comentário "Placar da equipe" é editado (um só) ou um novo a cada execução? O workflow tem permissão de comentar em PR?

##### Roteiro de teste (diretor) — GitHub e Revit
1. Abra uma conversa nova com o repositório e cole o texto do topo do `GERENTE.md`. Esperado: responde em português, como Gerente, sem programar.
2. Peça ao gerente um pedido pequeno de teste. Esperado: uma Issue com Objetivo/Contexto/Critérios (3–8, um de não regressão)/Fora do escopo/Prioridade e as etiquetas `pedido` + `pronto-para-dev`. Depois feche ou descarte a Issue.
3. Com este relatório postado no PR #7, rode `python tools/equipe/consenso.py --pr 7`. Esperado: aguardando programador (ou nova iteração), commit 34cb03e.
4. Rode `python tools/equipe/liberar.py --pr 7` **sem** `--sim`. Esperado: mostra o que faria. Confira se recusa por "versão não mudou" (G1) e anote a mensagem.
5. Num PR de teste (não no #7), poste `JA-DIRETOR` com `PARAR` e depois `LIBERAR` com commit. Esperado: o placar mostra PARAR e depois DIRETOR com esse commit (pergunta 7).
6. Rode `tools/equipe/pacote_qa.py --pr 7 --saida <pasta>` e procure arquivos `.cs` e espelhos `.py`. Esperado: nada de `src/`. Confirme se os espelhos devem ficar (G3).
7. Baixe o artefato `JA-para-Revit-34cb03e…` da execução de push e instale no Revit 2024 e no 2027. Esperado: as abas JA Famílias e JA Tools aparecem iguais às de antes, versão 2.6.1 (nada muda no add-in).

<!-- JA-QA
iteracao: 1
commit: 34cb03e
veredito: REPROVADO
nota: 58
-->


### Posição 1 (programador)
**Programador — posição sobre a iteração 1:** concordo com o REPROVADO. Na iteração 2 vou corrigir:
- **G1:** PR sem mudança no add-in ganha regra própria (`liberar.py --sem-publicar` já existe; vai estar escrito no PROCESSO e no critério 7).
- **G2:** na 3ª iteração, o placar espera a posição do programador (consenso ainda possível). Na melhor versão, um APROVADO vence um REPROVADO, e um defeito grave limita a nota a 79.
- **G3/m1:** o pacote leva o `consenso.py` (os testes rodam lá). A regra fica escrita: espelhos de algoritmo em Python são **testes** e podem ir para o QA; o código do add-in (C#/XAML/PS1), nunca.
- **m2:** uma posição com commit diferente do relatório não vale (continua aguardando o programador).
- **m3/m4/m5:** conferência do PR, menção com `@` e caminhos dentro do pacote.
- As perguntas 4 a 12 serão respondidas no PROCESSO: mesma conta e confiança por protocolo; o que conta como bloco; modelo sem comentários na linha; PARAR e retomar; LIBERAR sem commit; nota menor que 80; iteração 4; mesmo commit em duas iterações; prefixo mínimo; os "−3"; etiquetas; placar num único comentário.

<!-- JA-PROGRAMADOR
iteracao: 1
commit: 34cb03e
posicao: CONCORDO
-->


### Entrega 2 (programador)
**Entrega — iteração 2** (commit `f3a8ac6`, CI verde)

Corrigido o que o QA apontou na iteração 1:
- **G1:** a regra para PR sem mudança no add-in está escrita (PROCESSO, regra 1): libera só com o merge (`--sem-publicar`), sem Publicar versão.
- **G2:** na 3ª iteração o placar espera a posição. Na melhor versão, APROVADO vence REPROVADO. Grave limita a nota a 79, e APROVADO com nota < 80 vale como REPROVADO.
- **G3/m1:** o pacote leva o placar e os documentos da equipe, e os testes rodam nele. A regra 6 diz o que pode ir: espelhos de algoritmo em Python são testes. Código do add-in (C#/XAML/PS1) nunca vai, e o script recusa montar o pacote se houver.
  - Proposta ao diretor: ler o critério 3 como "sem código do add-in" (comentado na Issue #6).
- **m2:** uma posição com commit errado não vale.
- **m3:** conferência deste PR marcada.
- **m4:** menção sem `@`.
- **m5:** caminhos do pacote explicados no QA.md.
- **Perguntas:** respondidas em "Perguntas frequentes" no PROCESSO. Os 10 cenários do QA foram adotados (`test_consenso_qa.py`, 10 ok); `test_consenso.py` tem 26 casos.

<!-- JA-ENTREGA
iteracao: 2
commit: f3a8ac6
-->


### QA 2
#### QA — iteração 2 (commit f3a8ac6)
**Veredito:** REPROVADO · **Nota:** 60/100 (critérios 41/60 · robustez 9/15 · clareza 7/10 · regressão 15/15 · 4 defeitos menores −12)

Cálculo: 8 critérios de 7,5 pontos cada.
- Critérios: 5 ✅ = 37,5, mais o critério 4 parcial = 3,75. Total 41,25 (o ❌ e o ❔ valem 0).
- 41,25 + 9 + 7 + 15 = 72,25; menos 12 dos menores, dá **60**. O teto de 79 por grave não chega a pesar.
- Pela redação proposta do critério 3 (ver abaixo), a nota seria **68**, ainda REPROVADO pelos graves N1 e N2.

##### O que mudou desde o relatório da iteração 1
| Item | Situação | Evidência |
|---|---|---|
| G1 versão × liberar | **corrigido no processo** | Regra 1 do PROCESSO: um PR sem mudança no add-in só faz o merge (`--sem-publicar`). O texto do critério 7 na Issue continua o mesmo (a proposta aguarda o diretor), e o liberar segue sem poder ser testado (❔). |
| G2 3ª iteração / melhor versão | **corrigido** | Passam os casos "na 3 espera a posição", "consenso na 3", "aprovado vence reprovado de nota maior" e "APROVADO com nota < 80 vale REPROVADO". O grave agora limita a nota a 79. |
| G3 código no pacote | **ainda aberto pela letra** | Os 4 espelhos são os mesmos da iteração 1 (conferi byte a byte) e agora o placar (`consenso.py`) também veio. Pela redação proposta, ficaria resolvido. |
| m1 teste não roda | **corrigido** | `test_consenso.py` dá 26 ok e `test_consenso_qa.py` dá 10 ok, os dois com saída 0 e rodados por mim. Os meus 10 cenários foram adotados sem mudar nenhum esperado. |
| m2 commit errado | **corrigido** | Caso "posição com commit errado não vale" → AGUARDANDO_PROGRAMADOR. |
| m3 CI do PR | **corrigido** | A conferência está marcada, e as execuções pull_request e push do f3a8ac6 deram success nos 3 jobs. |
| m4 "@programador" | **corrigido** | O GERENTE.md diz "sem `@`". |
| m5 caminhos | **parcial** | O QA.md explica os caminhos. A descrição do PR ainda diz "`docs/equipe/…` (estão no pacote? se não, pedir)" e só cita `test_consenso.py`. |
| Perguntas 4–12 | **respondidas** | "Perguntas frequentes" do PROCESSO. Fora delas: o modelo de bloco ainda tem "(ou REPROVADO)" na linha, embora isso tenha sido prometido. É inofensivo, porque o placar lê certo (caso "modelo copiado com comentário na linha"). |
| Novo | N1, N2 (graves) e N3–N6 (menores) | Vieram dos meus 27 cenários em `testes/equipe/qa_extra2.py`: 18 ok, 9 diferentes. |

##### Critérios de aceite
1. ✅ **Processo com os 4 papéis, o fluxo e a regra de liberação.** PROCESSO.md (tabela, fluxo, regras 1–4). A regra 4 agora diz como fecha a 3ª iteração e como se escolhe a melhor versão.
2. ✅ **O diretor abre o gerente colando um texto curto.** Topo do GERENTE.md; o DIRETOR.md aponta para ele.
3. ❌ **Pacote sem código-fonte (sem arquivos de src/ nem trechos de código)**, pela letra atual.
   - O pacote não tem C#, XAML, PS1 nem projetos. Procurei por `*.cs`, `*.xaml`, `*.ps1`, `*.csproj`, `*.sln` e `*.props`: nada.
   - Mas traz código Python do produto: os 4 espelhos de algoritmo e o próprio placar (`testes/equipe/consenso.py`).
   - As regras de nota e veredito estão claras e mais completas (critério parcial = metade, menores no total, teto de 79 para grave).
   - **Pela redação proposta** ("o QA não recebe código do add-in (C#, XAML, PowerShell); os testes automáticos em Python, inclusive os espelhos dos algoritmos, podem ir"), seria ✅: não há código do add-in, e o que veio é teste ou espelho, como a regra 6 do PROCESSO permite. Uma ressalva: o `consenso.py` não é teste nem espelho, é a própria ferramenta. A regra 6 o cobre ("e o placar"), mas a redação proposta para o critério não. Sugiro acrescentar "e o placar" ao texto do critério.
   - Não verifiquei a recusa do `pacote_qa.py` a montar pacote com código do add-in (vai no roteiro).
4. ❌ **(parcial, metade dos pontos) Placar: aguardando / nova iteração / liberar (consenso) / melhor versão; empate fica com a mais nova.**
   - Todos os estados e o empate funcionam (26 + 10 ok, rodados).
   - Mas o placar dá "✅ Consenso — liberar" sem consenso real (N1) e quebra com uma nota vazia (N2).
   - A leitura dos comentários reais do GitHub continua sem verificação: o workflow só roda depois do merge.
5. ✅ **A posição só vale depois do relatório do QA da mesma iteração e do mesmo commit.** Casos "posição antes do QA não conta", "QA reavalia: posição antiga cai", "posição com commit errado não vale" e "prefixo de 6 não casa" (meu).
6. ✅ **O diretor decide por cima.** LIBERAR, PARAR, RETOMAR, LIBERAR sem commit e PARAR→RETOMAR→LIBERAR funcionam (testes + `qa_extra2.py`). Uma decisão que o placar não reconhece vira PARAR. Isso é conservador e aparece no placar, mas é calado (N3).
7. ❔ **Liberar faz o merge e publica; recusa se a versão não mudou ou se o CI não estiver verde.** O `liberar.py` não está no pacote, e não há saída nem teste dele. Além disso, o texto do critério ("recusa se a versão não mudou") conflita com a regra 1 nova (PR sem add-in libera sem versão nova) até o diretor confirmar a proposta da Issue #6. Vai para o roteiro.
8. ✅ **Não regressão.**
   - "Verificar e compilar" deu success nas execuções push e pull_request do f3a8ac6, nos 3 jobs, com o artefato `JA-para-Revit-f3a8ac6…` (7 MB).
   - A verificação deu "0 erro(s), 0 aviso(s)", e a versão continua 2.6.1.
   - Os testes de algoritmo rodam no pacote com saída 0, iguais à iteração 1: cebola 43, fórmulas 45/45, ifc 111, área 29, quinas e rodapé. A linha "FALHOU: atual…" do rodapé já existia antes.

##### Defeitos
- **[grave] N1 — "Concordo em parte" vira consenso.** Com o QA APROVADO 90, as posições `CONCORDO EM PARTE`, `CONCORDO PARCIALMENTE` e `concordo com ressalvas` dão **LIBERAR**, e o placar escreve "✅ Consenso — liberar… o programador concorda". O mesmo vale para o veredito `APROVADO COM RESSALVAS`.
  - É incoerente: `CONCORDO, mas falta…` dá NOVA_ITERACAO.
  - Esperado: só `CONCORDO`/`DISCORDO` exatos (o comentário do modelo entre parênteses pode continuar aceito). Qualquer outra coisa deve dar "posição não reconhecida", e nunca LIBERAR.
  - Reproduzir: `qa_extra2.py`, casos "CONCORDO EM PARTE", "concordo com ressalvas" e "APROVADO COM RESSALVAS".
- **[grave] N2 — Uma nota vazia derruba o placar do PR inteiro.** Um bloco JA-QA com `nota:` sem valor faz o placar parar com erro (`IndexError`), sem dar status nenhum. É o que acontece se alguém copia o modelo e esquece a nota.
  - Esperado: ignorar o bloco ou tratá-lo como nota 0, com aviso.
  - Reproduzir: `qa_extra2.py`, caso "nota vazia não quebra o placar".
- **[menor] N3 — Blocos fora do formato são ignorados ou trocados sem aviso.**
  - Chaves com acento (`iteração:`, `posição:`, como se escreve em português) são ignoradas, e o placar diz "nenhuma iteração entregue ainda".
  - `nota: 86/100`, o formato do cabeçalho do relatório, vira 0, e um APROVADO passa a valer REPROVADO.
  - `decisao: LIBERA`, `RETOMA`, `APROVAR` ou vazio vira **PARAR**.
  - Iteração vazia ou "dois", bloco sem commit e bloco sem `-->` são ignorados.
  - Tudo isso é conservador, mas calado. Esperado: o placar listar "blocos não reconhecidos" (quem, onde, por quê).
- **[menor] N4 — O placar não aplica duas regras do FAQ.**
  - Um bloco dentro de cerca `~~~` conta como bloco. O FAQ só fala de ```, mas o GitHub também mostra `~~~` como código.
  - `JA-DIRETOR` com commit de 6 caracteres é aceito, contra a regra de "prefixo de 7+".
- **[menor] N5 — Textos que não acompanharam as correções.**
  - O placar sempre manda "roda **Publicar versão**", inclusive no PR #7, que pela regra 1 é `--sem-publicar`.
  - O fluxo do PROCESSO e o PROGRAMADOR.md ("Depois da 3ª avaliação sem consenso…") não dizem que a 3ª só fecha com a posição.
  - O DIRETOR.md, que é o que o diretor lê, não fala de `RETOMAR` nem de `LIBERAR` sem commit (isso só aparece no FAQ do PROCESSO).
- **[menor] N6 (era m5) — Descrição do PR desatualizada.** O "Como testar" ainda pede `docs/equipe/…` "(estão no pacote? se não, pedir)" e a conferência só cita `test_consenso.py`, sem `test_consenso_qa.py`.

##### Perguntas ao programador
1. N1: o placar passa a aceitar só a palavra exata? O que aparece no placar para "CONCORDO EM PARTE"?
2. N2: quando o placar quebra, o workflow "Placar da equipe" avisa no PR, ou o comentário só para de atualizar?
3. `LIBERAR` sem commit "libera a cabeça do PR": é a cabeça no momento do bloco ou no momento do `liberar.py`? Se entrar um push depois do bloco, sai um commit que ninguém avaliou?
4. Como o `liberar.py --sem-publicar` sabe que o PR "não muda o add-in"? Ele recusa `--sem-publicar` num PR que mexe em `src/`, para não pular a versão por engano? E sem `--sem-publicar`, o PR #7 é recusado por "versão não mudou"?
5. Na 3ª iteração, APROVADO + DISCORDO fecha como melhor versão **desse** commit, justamente aquele em que o programador disse saber de um problema. É intencional? O placar ou o gerente avisa o diretor nesse caso?
6. Um QA que pula da iteração 1 para a 3 fecha o PR como melhor versão (caso "QA pula da iteração 1 para a 3"). Não deveria exigir as iterações em ordem?
7. O diretor confirmou a redação dos critérios 3 e 7? Se confirmou, a Issue #6 vai ter os critérios marcados "(alterado em dd/mm)", como manda o GERENTE.md?

##### Roteiro de teste (diretor) — GitHub e Revit
1. Na Issue #6, responda à proposta do programador para os critérios 3 e 7: confirme ou corrija. Isso decide o G3.
2. Abra uma conversa nova com o repositório e cole o texto do topo do `GERENTE.md`. Esperado: responde em português, como Gerente, sem programar.
3. Com este relatório postado, rode `python tools/equipe/consenso.py --pr 7`. Esperado: aguardando o programador, commit f3a8ac6, iteração 2.
4. Rode `python tools/equipe/liberar.py --pr 7 --sem-publicar` **sem** `--sim`. Esperado: mostra só o merge, sem Publicar versão. Depois rode sem `--sem-publicar`. Esperado: recusa por "versão não mudou" (pergunta 4).
5. Num PR de teste (não no #7), comente `JA-DIRETOR` com `PARAR` e depois com `RETOMAR`. Esperado: o placar mostra PARAR e depois volta ao estado normal, num único comentário editado.
6. Monte o pacote de um branch de teste que tenha um `.cs` alterado: `pacote_qa.py --pr <n> --saida <pasta>`. Esperado: o script recusa, ou o pacote sai sem nenhum `.cs`, `.xaml` ou `.ps1`.
7. Baixe o artefato `JA-para-Revit-f3a8ac6…` e instale no Revit 2024 e no 2027. Esperado: as abas JA Famílias e JA Tools iguais às de antes, versão 2.6.1.

<!-- JA-QA
iteracao: 2
commit: f3a8ac6
veredito: REPROVADO
nota: 60
-->


### Posição 2 (programador)
**Programador — posição sobre a iteração 2:** concordo com o REPROVADO. N1 e N2 são defeitos reais do placar. Na iteração 3 (a última):
- **N1:** só valem as palavras exatas (CONCORDO/DISCORDO, APROVADO/REPROVADO, LIBERAR/PARAR/RETOMAR). Qualquer outra coisa é ignorada e aparece em "blocos não reconhecidos".
- **N2/N3:** nota vazia ou inválida não quebra mais; o bloco é ignorado com aviso. Agora aceita "86/100" e chaves com acento.
- **N4:** cercas `~~~` também não contam; o diretor precisa de commit com 7+ caracteres.
- **N5/N6:** os textos do placar, do PROCESSO, do PROGRAMADOR e do DIRETOR, e a descrição do PR, ficam alinhados às regras.
- **Perguntas:**
  - **3:** LIBERAR do diretor passa a exigir commit.
  - **4:** `--sem-publicar` recusa um PR que mexe em `src/` ou na versão.
  - **5:** na melhor versão, APROVADO com DISCORDO fica abaixo de APROVADO sem discordância, e o placar avisa o diretor.
  - **6:** as iterações têm de vir em ordem.
  - **2:** se o placar falhar, o workflow comenta no PR.
  - **7:** pergunto ao diretor.

<!-- JA-PROGRAMADOR
iteracao: 2
commit: f3a8ac6
posicao: CONCORDO
-->
