# Equipe JA para Revit — como uma versão nasce e é liberada

Quatro papéis, uma sala de reunião: **o GitHub** (Issues e Pull Requests). Tudo o que importa fica escrito lá, para
qualquer conversa retomar de onde parou.

| Papel | Quem | Faz | Não faz |
|---|---|---|---|
| **Diretor** | você (Lucas) | dá o rumo, aprova prioridades, testa no Revit quando quiser, pode decidir por cima de todos | — |
| **Gerente** | uma conversa do Claude só para isso ([GERENTE.md](GERENTE.md)) | transforma o pedido do diretor numa Issue com critérios de aceite; acompanha; resume para o diretor | não programa |
| **Programador** | a conversa do Claude com o código ([PROGRAMADOR.md](PROGRAMADOR.md)) | implementa, compila no CI, entrega iterações num PR, responde ao QA, libera | não se avalia sozinho |
| **QA** | um agente que **não vê o código** ([QA.md](QA.md)) | testa como usuário de fora a partir da Issue, do PR e dos resultados; dá veredito e nota | não lê `src/` nem o diff |

## Fluxo

```
Diretor ──pedido──► Gerente ──Issue "Pedido" (critérios de aceite, etiqueta pronto-para-dev)──► Programador
                                                                                                   │
            ┌──────────────────────────────────────────────────────────────────────────────────────┘
            ▼
   Iteração N (N = 1, 2, 3) no MESMO PR:
     1. Programador implementa, CI verde, comenta a ENTREGA (commit + como testar)
     2. QA recebe o pacote (tools/equipe/pacote_qa.py), testa sem código, comenta RELATÓRIO (veredito + nota)
     3. Programador lê o relatório e comenta a POSIÇÃO: CONCORDO ou DISCORDO (com motivos)
     4. Placar (workflow "Placar da equipe" / tools/equipe/consenso.py) decide:
          QA APROVADO + Programador CONCORDO (mesmo commit) ──► ✅ CONSENSO: liberar esse commit
          senão, N < 3 ──► 🔁 nova iteração (corrigir o que o QA apontou, ou provar que o QA errou)
          senão (3 iterações sem consenso) ──► 🏁 MELHOR VERSÃO: o commit de maior nota do QA
            │
            ▼
   Programador libera: tools/equipe/liberar.py --pr N  (merge do commit escolhido + Publicar versão)
   Gerente avisa o diretor: o que saiu, nota, pendências, como testar.
```

O **diretor** pode cortar qualquer etapa comentando no PR (ver [DIRETOR.md](DIRETOR.md)): liberar já, parar, ou
mudar o pedido (o gerente atualiza a Issue e a contagem de iterações continua).

## Regras

1. **Uma Issue = um PR = uma versão.** Iterações são commits novos no mesmo PR; a versão (`tools/versao.py`) sobe
   uma vez, na iteração 1. **PR que não muda o add-in** (processo, documentação, CI): não sobe versão, e a liberação é
   só o merge (`liberar.py --sem-publicar`), sem Publicar versão.
2. **Consenso** = os dois blocos da mesma iteração e do mesmo commit: QA `APROVADO` e programador `CONCORDO`.
   A posição do programador só vale depois do relatório do QA daquela iteração.
3. **APROVADO** exige nota ≥ 80 e nenhum defeito crítico ou grave aberto (o placar trata APROVADO com nota < 80
   como REPROVADO). O QA não aprova o que não conseguiu
   verificar: o que depende do Revit vira roteiro para o diretor e conta como "não verificado".
4. **Máximo de 3 iterações.** Na 3ª o consenso ainda vale. Se a 3ª fechar (relatório + posição) sem consenso, vale
   a **melhor versão**: um APROVADO vence qualquer REPROVADO; entre iguais, a maior nota; empate, a mais nova. As pendências desse commit vão para uma Issue nova (o gerente abre).
5. O CI (**Verificar e compilar**) tem de estar verde no commit entregue; sem isso não é entrega.
6. O QA nunca recebe código do add-in (C#, XAML, PowerShell, projetos). Recebe o pacote: Issue, PR (texto e
   comentários, sem trechos de código), resultado do CI, artefatos, saída da verificação, os testes automáticos (com
   os espelhos em Python dos algoritmos e o placar: são testes, para ele rodar e criar cenários), README, ROTINA e
   estes documentos. O `pacote_qa.py` recusa montar um pacote com código do add-in.
7. Tudo em português, curto. Comentários com os blocos abaixo — o placar só lê os blocos.

## Blocos (copie e preencha; ficam invisíveis no GitHub)

Entrega (programador):
```
<!-- JA-ENTREGA
iteracao: 1
commit: abc1234
-->
```
Relatório (QA):
```
<!-- JA-QA
iteracao: 1
commit: abc1234
veredito: APROVADO        (ou REPROVADO)
nota: 86
-->
```
Posição (programador):
```
<!-- JA-PROGRAMADOR
iteracao: 1
commit: abc1234
posicao: CONCORDO         (ou DISCORDO)
-->
```
Diretor (opcional, decide por cima):
```
<!-- JA-DIRETOR
decisao: LIBERAR          (ou PARAR)
commit: abc1234
motivo: ...
-->
```

## Nota do QA (0–100)

| Parte | Pontos | O que conta |
|---|---|---|
| Critérios de aceite da Issue | 60 | proporcional aos critérios atendidos **com evidência** (não verificado = 0 nesse critério, mas fica no roteiro do diretor) |
| Robustez | 15 | casos de borda, entradas estranhas, mensagens de erro, desfazer, projetos grandes |
| Clareza para o usuário | 10 | nomes, textos dos diálogos e da ajuda, “Como testar” do PR, coerência com as outras ferramentas |
| Sem regressão | 15 | CI verde, verificação 0 erro, testes automáticos passando, nada que funcionava quebrou |

Defeito **crítico** (perde trabalho, trava o Revit, resultado errado silencioso) → REPROVADO e nota máxima 40.
Defeito **grave** (critério de aceite falha) → REPROVADO e nota máxima 79. **Menor** (texto, detalhe) → −3 cada, no
total, não reprova sozinho. Critério **parcial** vale metade dos seus pontos.

## Perguntas frequentes

- **Quem postou o bloco?** Todas as conversas usam a mesma conta do GitHub: o placar confia no protocolo (cada papel
  só posta o seu bloco). O diretor confere pelo histórico do PR.
- **O que conta como bloco?** Só blocos `<!-- JA-… -->` fora de ``` e fora de citações (`> `), em comentários do PR,
  com iteração de 1 a 3. Vale o último de cada papel por iteração. Um relatório novo do QA derruba a posição anterior.
- **Commit:** prefixo de 7+ caracteres; a posição do programador tem de citar o mesmo commit do relatório.
  O mesmo commit pode aparecer em duas iterações (o programador provou que o QA errou sem mudar o código).
- **Diretor:** `PARAR` congela; `RETOMAR` volta ao placar normal; `LIBERAR` sem `commit:` libera a cabeça do PR.
- **Etiquetas** (`pedido`, `pronto-para-dev`, `em-dev`, `em-qa`, `consenso`, `melhor-versao`, `iteracao-N`…) já
  existem; o placar é **um** comentário, editado a cada mudança, pelo workflow **Placar da equipe**.

## Comandos

```bash
python tools/equipe/consenso.py --pr 12              # mostra o placar
python tools/equipe/pacote_qa.py --pr 12 --saida /tmp/qa-12   # monta o pacote do QA (sem código)
python tools/equipe/liberar.py --pr 12 --sim         # confere o placar e libera (merge + Publicar versão)
python tools/equipe/liberar.py --pr 12 --sim --sem-publicar   # PR sem mudança no add-in: só o merge
```
