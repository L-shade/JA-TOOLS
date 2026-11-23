"""Placar em arquivo (equipe/pedidos/NNN-*.md): cabeçalho, placar gravado no topo e releitura sem efeito colateral."""
import os, sys, tempfile
_aqui = os.path.dirname(os.path.abspath(__file__))
for _p in (os.path.join(_aqui, '..', '..', '..', 'tools', 'equipe'), _aqui):
    if os.path.exists(os.path.join(_p, 'consenso.py')): sys.path.insert(0, os.path.abspath(_p)); break
from consenso import ler_pedido, gravar_placar, decidir, MARCA

A = 'abcdef1234'
PEDIDO = f"""# 042 — Teste

- **Estado:** em-dev
- **Branch:** `ajuste/042-teste`
- **Prioridade:** alta

## Pedido
### Critérios de aceite
1. algo

## Iterações

### Entrega 1
Exemplo que não conta:
```
<!-- JA-QA
iteracao: 1
commit: {A}
veredito: APROVADO
nota: 99
-->
```
<!-- JA-ENTREGA
iteracao: 1
commit: {A}
-->

### QA 1
<!-- JA-QA
iteração: 1
commit: {A}
veredito: APROVADO
nota: 90/100
-->
"""
ok = falhas = 0
def check(nome, cond, info=''):
    global ok, falhas
    if cond: ok += 1
    else: falhas += 1; print('FALHOU:', nome, info)

with tempfile.TemporaryDirectory() as d:
    p = os.path.join(d, '042-teste.md')
    open(p, 'w', encoding='utf-8').write(PEDIDO)
    bruto, texto, cab = ler_pedido(p)
    check('cabeçalho', cab == {'titulo': '042 — Teste', 'estado': 'em-dev', 'branch': 'ajuste/042-teste', 'prioridade': 'alta'}, cab)
    r = decidir([texto])
    check('aguardando programador', r['status'] == 'AGUARDANDO_PROGRAMADOR' and r['commit'] == A.lower(), r)
    gravar_placar(p, bruto, r)
    s = open(p, encoding='utf-8').read()
    check('placar logo abaixo do título', s.startswith('# 042 — Teste\n\n' + MARCA), s[:80])
    # programador concorda; placar regravado no mesmo lugar, um só
    open(p, 'a', encoding='utf-8').write(f"\n### Posição 1\n<!-- JA-PROGRAMADOR\niteracao: 1\ncommit: {A[:7]}\nposicao: CONCORDO\n-->\n")
    bruto, texto, _ = ler_pedido(p)
    check('o placar gravado não entra na leitura', MARCA not in texto)
    r = decidir([texto])
    check('consenso', r['status'] == 'LIBERAR', r['status'])
    gravar_placar(p, bruto, r)
    s = open(p, encoding='utf-8').read()
    check('um placar só', s.count(MARCA) == 1, s.count(MARCA))
    check('nada foi apagado', '### Posição 1' in s and '### Entrega 1' in s)
print(f'{ok} ok, {falhas} falha(s)')
sys.exit(1 if falhas else 0)
