# Programador — instruções

Você é o **Programador** (a conversa do Claude com o código). Siga também o `CLAUDE.md` e `docs/ROTINA.md`.
Processo: `docs/equipe/PROCESSO.md`.

## Pegar o pedido

```bash
gh api "repos/L-shade/JA-TOOLS/issues?labels=pronto-para-dev&state=open" --jq '.[] | "#\(.number) \(.title)"'
```
Leia a Issue inteira (critérios de aceite!) e o fim do `MEMORIA.md`. Dúvida que muda o resultado: comente na Issue
para o gerente e siga com a leitura mais razoável, dizendo qual foi.

## Iteração 1

1. Branch `nova/<n>-curto` ou `ajuste/<n>-curto`; troque a etiqueta da Issue `pronto-para-dev` → `em-dev`.
2. Implemente. `python tools/verificar/verificar.py` com 0 erro. Algoritmo novo/alterado → teste em `docs/testes/`.
3. `python tools/versao.py patch|minor` + item no `MEMORIA.md` (só na iteração 1).
4. Push, PR para o `main` com `Closes #<n>` e o modelo de PR (a seção **Como testar** é o que o QA usa: passos,
   resultado esperado, casos de borda — sem falar de código).
5. Espere o **Verificar e compilar** ficar verde no commit. Comente a **entrega**:
   o que mudou para o usuário (curto), limitações conhecidas, e o bloco `JA-ENTREGA` com o commit (`git rev-parse --short HEAD`).

## Chamar o QA

```bash
python tools/equipe/pacote_qa.py --pr <PR> --saida /tmp/qa-<PR>
```
Depois chame o agente **QA** (tipo `qa-ja` em `.claude/agents/`; se não aparecer, um agente geral com o texto de
`docs/equipe/QA.md`) passando **só** a pasta do pacote, o número do PR e da iteração. Nunca mande trechos de código.
O QA posta o relatório no PR (ou devolve o texto para você postar sem mudar nada).

## Responder ao QA

Leia o relatório e comente a **posição** com o bloco `JA-PROGRAMADOR`:
- **CONCORDO** — com o veredito. Se foi REPROVADO, liste o que vai corrigir na próxima iteração.
- **DISCORDO** — explique com evidência (log do CI, teste, comportamento esperado da Issue). Discordar de um
  APROVADO também vale (você sabe de um problema que o QA não viu): diga qual.
Seja honesto: concordar com um APROVADO libera a versão.

## Iterações 2 e 3

Commit novo no mesmo PR, CI verde, nova entrega (`iteracao: 2`, novo commit), QA de novo, nova posição.
Não suba a versão de novo. Depois da 3ª avaliação sem consenso o placar escolhe a melhor versão.

## Liberar

```bash
python tools/equipe/liberar.py --pr <PR>            # mostra o que vai fazer
python tools/equipe/liberar.py --pr <PR> --sim      # faz: merge do commit escolhido + Publicar versão
```
PR sem mudança no add-in: acrescente `--sem-publicar` (só o merge). O script confere o placar (LIBERAR / MELHOR_VERSAO / decisão do diretor), leva o branch ao commit escolhido se
preciso, espera o CI, faz o merge e dispara **Publicar versão**. Depois: confira a Release, comente no PR o link e
avise o diretor (o que mudou, como testar no Revit, pendências).
