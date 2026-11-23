# Equipe JA para Revit — como uma versão nasce e é liberada

Quatro papéis e uma sala de reunião: a pasta **`equipe/`** do projeto. Cada pedido é um arquivo
(`equipe/pedidos/NNN-assunto.md`) em que cada papel acrescenta a sua parte, em ordem; o **quadro**
(`equipe/QUADRO.md`) mostra todos. Sem Issues nem PRs: o GitHub só guarda o código e compila (Actions).

| Papel | Quem | Faz | Não faz |
|---|---|---|---|
| **Diretor** | você (Lucas) | dá o rumo, aprova critérios e prioridades, testa no Revit/AutoCAD, pode decidir por cima | — |
| **Gerente** | uma conversa do Claude só para isso ([GERENTE.md](GERENTE.md)) | escreve o pedido com critérios de aceite; acompanha; resume para o diretor | não programa |
| **Programador** | a conversa do Claude com o código ([PROGRAMADOR.md](PROGRAMADOR.md)) | implementa, compila, entrega iterações, responde ao QA, libera | não se avalia sozinho |
| **QA** | um agente que **não vê o código do add-in** ([QA.md](QA.md)) | testa como alguém de fora, a partir do pedido e das evidências; dá veredito e nota | não lê `src/` |

## Fluxo

```
Diretor ──pedido──► Gerente ──equipe/pedidos/NNN-assunto.md (critérios de aceite, Estado: pronto)──► Programador
                                                                                                        │
     ┌──────────────────────────────────────────────────────────────────────────────────────────────────┘
     ▼
  Iteração N (1, 2, 3), no MESMO branch do pedido:
    1. Programador implementa, compilação verde, acrescenta "Entrega N" (commit, como testar)
    2. QA recebe o pacote (tools/equipe/pacote_qa.py NNN), testa sem o código, acrescenta "QA N" (veredito + nota)
    3. Programador lê e acrescenta "Posição N": CONCORDO ou DISCORDO (com motivos)
    4. Placar (tools/equipe/consenso.py NNN --gravar) decide:
         QA APROVADO + Programador CONCORDO (mesmo commit) ──► ✅ CONSENSO: liberar esse commit
         senão, N < 3 ──► 🔁 nova iteração (corrigir o que o QA apontou, ou provar que o QA errou)
         senão (a 3ª fechou — relatório + posição — sem consenso) ──► 🏁 MELHOR VERSÃO (regra 4)
     │
     ▼
  Programador libera: tools/equipe/liberar.py NNN --sim  (merge no main + Publicar versão se o add-in mudou)
  Gerente avisa o diretor: o que saiu, nota, roteiro de teste no Revit, pendências.
```

O **diretor** pode cortar qualquer etapa acrescentando uma seção "Diretor" ao pedido (ver [DIRETOR.md](DIRETOR.md)):
liberar já, parar ou retomar. Para mudar o pedido, fala com o gerente.

## Onde ficam as coisas

- `equipe/pedidos/NNN-assunto.md` — um arquivo por pedido, a partir de `equipe/MODELO-PEDIDO.md`: cabeçalho
  (Estado, Branch, Prioridade), **Pedido** (objetivo, critérios), **Iterações** (as seções de cada papel, sempre
  acrescentadas no fim, nunca apagadas) e, logo abaixo do título, o placar gerado.
- `equipe/QUADRO.md` — a tabela de todos os pedidos (`consenso.py --quadro --gravar`).
- **O quadro vive no branch `quadro`** (só a pasta `equipe/`; não compila nada). Gerente e programador escrevem lá
  (`git worktree add ../ja-quadro quadro`, `pull`, editar, `commit`, `push`). Os branches de código nunca mexem em
  `equipe/` (o `liberar.py` recusa), e o `main` só recebe código liberado.
- Código: um branch por pedido (`ajuste/NNN-…`, `nova/NNN-…`), enviado ao GitHub só para guardar e compilar.

## Regras

1. **Um pedido = um branch = uma versão.** Iterações são commits novos no mesmo branch; a versão
   (`tools/versao.py`) sobe uma vez, na iteração 1. **Pedido que não muda o add-in** (processo, documentação, CI):
   não sobe versão, e a liberação é só o merge (`liberar.py --sem-publicar`).
2. **Consenso** = os dois blocos da mesma iteração e do mesmo commit: QA `APROVADO` e programador `CONCORDO`.
   A posição do programador só vale depois do relatório do QA daquela iteração.
3. **APROVADO** exige nota ≥ 80 e nenhum defeito crítico ou grave aberto (o placar trata APROVADO com nota < 80
   como REPROVADO). O QA não aprova o que não conseguiu verificar.
4. **Máximo de 3 iterações.** Na 3ª o consenso ainda vale. Se a 3ª fechar (relatório + posição) sem consenso, vale
   a **melhor versão**: APROVADO sem discordância > APROVADO com DISCORDO do programador > REPROVADO; entre iguais,
   a maior nota; empate, a mais nova. Se a escolhida não for um APROVADO sem discordância, o placar avisa o diretor.
   As pendências viram um pedido novo (o gerente abre).
5. A compilação (**Verificar e compilar**, no GitHub Actions) tem de estar verde no commit entregue.
6. O QA nunca recebe código do add-in (C#, XAML, PowerShell, projetos). Recebe o pacote: o pedido, as iterações
   (sem trechos de código), a compilação, a verificação, os testes automáticos (com os espelhos em Python dos
   algoritmos e o placar: são testes, para ele rodar e criar cenários), README, ROTINA e estes documentos.
   O `pacote_qa.py` recusa montar um pacote com código do add-in.
7. **Critérios que só o Revit/AutoCAD mostram** (o QA não tem Revit): o gerente marca esses critérios com
   `(diretor)`. O QA não os pontua — os 60 pontos de critérios são divididos só entre os que ele pode verificar — mas
   escreve o roteiro de teste deles. Ao liberar, esses critérios ficam **“a confirmar pelo diretor”**; se o teste do
   diretor falhar, o gerente abre um pedido novo. Se o diretor já testou e escreveu o resultado no pedido (seção
   "Diretor — teste"), o QA usa como evidência.
8. Tudo em português, curto. Cada seção termina com o seu bloco — o placar só lê os blocos.

## Blocos (no fim de cada seção; ficam invisíveis quando o Markdown é exibido)

Use exatamente estas palavras (maiúsculas ou minúsculas); qualquer outra (“CONCORDO EM PARTE”, “APROVADA”) é
ignorada e aparece nos **Avisos** do placar. Veredito `APROVADO`/`REPROVADO`; posição `CONCORDO`/`DISCORDO`;
decisão `LIBERAR`/`PARAR`/`RETOMAR`. Nota: número de 0 a 100 (`86` ou `86/100`). Commit: 7+ caracteres.

Entrega (programador):

    <!-- JA-ENTREGA
    iteracao: 1
    commit: abc1234
    -->

Relatório (QA):

    <!-- JA-QA
    iteracao: 1
    commit: abc1234
    veredito: APROVADO
    nota: 86
    -->

Posição (programador):

    <!-- JA-PROGRAMADOR
    iteracao: 1
    commit: abc1234
    posicao: CONCORDO
    -->

Diretor (opcional, decide por cima):

    <!-- JA-DIRETOR
    decisao: LIBERAR
    commit: abc1234
    motivo: ...
    -->

## Nota do QA (0–100)

| Parte | Pontos | O que conta |
|---|---|---|
| Critérios de aceite | 60 | divididos entre os critérios que o QA pode verificar; atendido **com evidência** = cheio, parcial = metade, não atendido ou não verificado = 0. Os marcados `(diretor)` ficam fora da conta e vão para o roteiro |
| Robustez | 15 | casos de borda, entradas estranhas, mensagens de erro, desfazer, projetos grandes |
| Clareza para o usuário | 10 | nomes, textos dos diálogos e da ajuda, “Como testar” da entrega, coerência com as outras ferramentas |
| Sem regressão | 15 | compilação verde, verificação 0 erro, testes automáticos passando, nada que funcionava quebrou |

Defeito **crítico** (perde trabalho, trava o Revit, resultado errado silencioso) → REPROVADO e nota máxima 40.
Defeito **grave** (critério de aceite falha) → REPROVADO e nota máxima 79. **Menor** (texto, detalhe) → −3 cada, no
total, não reprova sozinho.

## Perguntas frequentes

- **Quem escreveu a seção?** Todos usam o mesmo repositório: o placar confia no protocolo (cada papel só escreve o
  seu bloco). O diretor confere pelo histórico do arquivo (`git log -p equipe/pedidos/NNN-*.md`).
- **O que conta como bloco?** Só blocos `<!-- JA-… -->` completos, fora de cercas de código (``` ou ~~~) e de
  citações (`> `), com iteração de 1 a 3 **em ordem** (a N só começa depois que a N−1 fechou com relatório +
  posição). Vale o último de cada papel por iteração; um relatório novo do QA derruba a posição anterior. Chaves com
  ou sem acento. Bloco incompleto, palavra fora da lista, nota inválida ou commit curto: ignorado e listado nos
  **Avisos** do placar — nada é trocado em silêncio.
- **Commit:** a posição do programador tem de citar o mesmo commit do relatório. O mesmo commit pode aparecer em
  duas iterações (o programador provou que o QA errou sem mudar o código).
- **Diretor:** `PARAR` congela; `RETOMAR` volta ao placar normal; `LIBERAR` exige `commit:`, para nunca liberar algo
  que ninguém avaliou. Vale a última decisão do diretor.
- **`--sem-publicar`** é recusado se o branch mexe em `src/`, `build/`, `Directory.Build.props` ou `JA.iss`.

## Comandos

```bash
python tools/equipe/consenso.py 008 --gravar        # placar do pedido 008 (grava abaixo do título)
python tools/equipe/consenso.py --quadro --gravar   # equipe/QUADRO.md
python tools/equipe/pacote_qa.py 008                # pacote do QA (sem código do add-in), da última entrega
python tools/equipe/liberar.py 008                  # o que faria;  --sim faz;  --sem-publicar só o merge
```
