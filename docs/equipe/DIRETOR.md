# Diretor — guia rápido

Você fala com duas conversas do Claude:
- **Gerente** — para pedir coisas, mudar prioridades e saber como está. Para abrir: conversa nova com o repositório
  `L-shade/JA-TOOLS` e cole o texto do topo de [GERENTE.md](GERENTE.md).
- **Programador** — a conversa do código. Diga “tem o pedido NNN” quando o gerente criar um.

O QA é chamado pelo programador; você não precisa falar com ele. O relatório dele, com o **roteiro de teste no
Revit**, fica no arquivo do pedido.

**Ver como está:** `equipe/QUADRO.md` (todos os pedidos) ou o placar no topo de `equipe/pedidos/NNN-*.md`; ou pergunte
ao gerente.

**Testou no Revit/AutoCAD?** Conte ao gerente ou ao programador: eles escrevem a seção “Diretor — teste” no pedido,
e o QA usa como evidência (os critérios marcados `(diretor)` dependem disso).

**Decidir por cima** — peça ao gerente ou ao programador para acrescentar ao pedido:
```
<!-- JA-DIRETOR
decisao: LIBERAR
commit: abc1234
motivo: preciso disso hoje
-->
```
`LIBERAR` precisa do `commit:` (o código de 7+ letras que aparece no placar). `PARAR` congela o pedido; `RETOMAR`
volta ao normal. Use só essas palavras. Para mudar o pedido, fale com o gerente.

**Baixar:** https://github.com/L-shade/JA-TOOLS/releases/latest/download/JA_Setup.exe (sempre a última versão).
