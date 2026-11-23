# Diretor — guia rápido

Você fala com duas conversas do Claude:
- **Gerente** — para pedir coisas, mudar prioridades e saber como está. Para abrir: conversa nova com o repositório
  `L-shade/JA-TOOLS` e cole o texto do topo de [GERENTE.md](GERENTE.md).
- **Programador** — a conversa do código. Diga “tem a Issue #N” quando o gerente abrir um pedido.

O QA é chamado pelo programador; você não precisa falar com ele. O relatório dele, com o **roteiro de teste no
Revit**, fica no PR.

**Ver como está:** o comentário **Placar da equipe** no PR (atualiza sozinho), ou pergunte ao gerente.

**Decidir por cima** (comentário no PR):
```
<!-- JA-DIRETOR
decisao: LIBERAR
commit: abc1234
motivo: preciso disso hoje
-->
```
`decisao: PARAR` interrompe. Para mudar o pedido, fale com o gerente.

**Baixar:** https://github.com/L-shade/JA-TOOLS/releases/latest/download/JA_Setup.exe (sempre a última versão).
