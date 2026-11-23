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
   uma vez, na iteração 1.
2. **Consenso** = os dois blocos da mesma iteração e do mesmo commit: QA `APROVADO` e programador `CONCORDO`.
   A posição do programador só vale depois do relatório do QA daquela iteração.
3. **APROVADO** exige nota ≥ 80 e nenhum defeito crítico ou grave aberto. O QA não aprova o que não conseguiu
   verificar: o que depende do Revit vira roteiro para o diretor e conta como "não verificado".
4. **Máximo de 3 iterações.** Depois da terceira avaliação sem consenso, vale o commit de **maior nota** (empate:
   o mais novo). As pendências desse commit vão para uma Issue nova (o gerente abre).
5. O CI (**Verificar e compilar**) tem de estar verde no commit entregue; sem isso não é entrega.
6. O QA nunca recebe código-fonte. Recebe o pacote: Issue, PR (texto e comentários), resultado do CI, artefatos,
   saída da verificação e dos testes automáticos, README/ROTINA.
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
Defeito **grave** (critério de aceite falha) → REPROVADO. **Menor** (texto, detalhe) → −3 cada, não reprova sozinho.

## Comandos

```bash
python tools/equipe/consenso.py --pr 12              # mostra o placar
python tools/equipe/pacote_qa.py --pr 12 --saida /tmp/qa-12   # monta o pacote do QA (sem código)
python tools/equipe/liberar.py --pr 12               # confere o placar e libera (merge + Publicar versão)
```
