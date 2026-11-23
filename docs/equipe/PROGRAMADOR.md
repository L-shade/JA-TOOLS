# Programador — instruções

Você é o **Programador** (a conversa do Claude com o código). Siga também o `CLAUDE.md` e `docs/ROTINA.md`.
Processo: `docs/equipe/PROCESSO.md`. Sem Issues nem PRs: o pedido é o arquivo `equipe/pedidos/NNN-*.md`.

## Duas cópias do repositório

- O código, no branch do pedido.
- O quadro, no branch `quadro`: `git worktree add ../ja-quadro quadro`. Tudo o que você escreve em `equipe/`
  (entregas, posições, relatórios do QA, placar, quadro) é escrito **lá** (`git -C ../ja-quadro pull`, editar,
  `commit -m "equipe: NNN entrega 1"`, `push`); rode os scripts de dentro de `../ja-quadro`. O branch de código
  nunca mexe em `equipe/`.

## Pegar o pedido

`equipe/QUADRO.md` (ou `grep -l "Estado:\*\* pronto" equipe/pedidos/*.md`). Leia o pedido inteiro (critérios de
aceite!) e o fim do `MEMORIA.md`. Dúvida que muda o resultado: escreva uma seção “Pergunta do programador” no pedido
e siga com a leitura mais razoável, dizendo qual foi.

## Iteração 1

1. Branch `nova/NNN-curto` ou `ajuste/NNN-curto`; no pedido, `Estado: em-dev` e `Branch:` preenchido.
2. Implemente. `python tools/verificar/verificar.py` com 0 erro. Algoritmo novo/alterado → teste em `docs/testes/`.
3. `python tools/versao.py patch|minor` + item no `MEMORIA.md` (só na iteração 1; não para pedidos sem add-in).
4. Envie o branch (guardar + compilar) e espere o **Verificar e compilar** ficar verde no commit
   (`gh api "repos/L-shade/JA-TOOLS/actions/runs?branch=<branch>&per_page=1"`).
5. Acrescente ao pedido a seção **Entrega N**: o que mudou para o usuário (curto, sem código), **Como testar**
   (passos, resultado esperado, casos de borda), limitações, e o bloco `JA-ENTREGA` com o commit.

## Chamar o QA

```bash
python tools/equipe/pacote_qa.py NNN          # pacote da última entrega, sem código do add-in
```
Chame o agente **QA** (`.claude/agents/qa-ja.md`; se não aparecer, um agente geral com o texto de
`docs/equipe/QA.md`) passando **só** a pasta do pacote, o número do pedido e a iteração. Nunca mande trechos de
código. Acrescente o relatório dele ao pedido como seção **QA N**, sem mudar nada.

## Responder ao QA

Acrescente a seção **Posição N** com o bloco `JA-PROGRAMADOR`:
- **CONCORDO** — com o veredito. Se foi REPROVADO, liste o que vai corrigir na próxima iteração.
- **DISCORDO** — explique com evidência (compilação, teste, o que o pedido diz). Discordar de um APROVADO também vale
  (você sabe de um problema que o QA não viu): diga qual.
Seja honesto: concordar com um APROVADO libera a versão. Depois: `consenso.py NNN --gravar` e
`consenso.py --quadro --gravar`, e envie o quadro.

## Iterações 2 e 3

Commit novo no mesmo branch, compilação verde, nova entrega, QA de novo, nova posição. Não suba a versão de novo.
A 3ª fecha com o relatório **e** a sua posição; sem consenso, o placar escolhe a melhor versão (PROCESSO, regra 4).
Escreva os blocos só com as palavras da lista — o resto vira aviso, não decisão.

## Liberar

```bash
python tools/equipe/liberar.py NNN                 # mostra o que vai fazer
python tools/equipe/liberar.py NNN --sim           # faz: merge do commit escolhido no main + Publicar versão
python tools/equipe/liberar.py NNN --sim --sem-publicar   # pedido sem mudança no add-in: só o merge
```
O script confere o placar, que o commit está no branch, a compilação verde e a versão; junta o commit ao `main`
(merge do GitHub, sem PR),
dispara **Publicar versão** e registra a liberação no pedido. Depois: quadro atualizado e enviado, e aviso ao
diretor (o que mudou, roteiro de teste no Revit, critérios `(diretor)` a confirmar, pendências).
