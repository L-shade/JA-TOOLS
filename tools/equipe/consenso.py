#!/usr/bin/env python3
"""Placar da equipe (docs/equipe/PROCESSO.md): lê os comentários de um PR e decide.

Blocos que contam (escondidos no comentário, em HTML):
  <!-- JA-ENTREGA    iteracao: 1  commit: abc1234 -->                    (programador entrega a iteração)
  <!-- JA-QA          iteracao: 1  commit: abc1234  veredito: APROVADO|REPROVADO  nota: 0-100 -->
  <!-- JA-PROGRAMADOR iteracao: 1  commit: abc1234  posicao: CONCORDO|DISCORDO -->
(um campo por linha; o último bloco de cada papel numa iteração é o que vale; a posição do programador só conta
 se vier depois do relatório do QA daquela iteração)

Decisão:
  LIBERAR        QA APROVADO + programador CONCORDO na mesma iteração e no mesmo commit.
  MELHOR_VERSAO  3 iterações avaliadas sem consenso: vale o commit de maior nota (empate: o mais novo).
  AGUARDANDO_QA / AGUARDANDO_PROGRAMADOR / NOVA_ITERACAO  enquanto isso.
O diretor pode decidir outra coisa a qualquer momento (bloco JA-DIRETOR decisao: LIBERAR|PARAR|RETOMAR, commit: ...).
Não contam: blocos dentro de ``` ou de citação (> ), iterações acima de 3, APROVADO com nota < 80 (vale REPROVADO),
posição do programador com commit diferente do relatório.

Uso:  python tools/equipe/consenso.py --pr 12 [--repo dono/repo] [--publicar]
      --publicar: comenta o placar no PR (atualiza o comentário anterior) e põe as etiquetas.
"""
import json, os, re, subprocess, sys

MAX_ITERACOES = 3
MARCA = '<!-- JA-CONSENSO -->'
ETIQUETAS = {'LIBERAR': 'consenso', 'MELHOR_VERSAO': 'melhor-versao', 'DIRETOR': 'decisao-do-diretor',
             'AGUARDANDO_QA': 'em-qa', 'AGUARDANDO_PROGRAMADOR': 'em-dev', 'NOVA_ITERACAO': 'em-dev', 'PARAR': 'parado'}

_BLOCO = re.compile(r'<!--\s*JA-(ENTREGA|QA|PROGRAMADOR|DIRETOR)\b(.*?)-->', re.S | re.I)


_CERCA = re.compile(r'```.*?(?:```|$)', re.S)


def blocos(texto):
    """Todos os blocos JA-* de um comentário, como dicionários (papel, iteracao, commit, ...)."""
    saida = []
    texto = _CERCA.sub('', texto or '')                                                  # exemplos em ``` não contam
    texto = '\n'.join(l for l in texto.splitlines() if not l.lstrip().startswith('>'))   # nem citações
    for m in _BLOCO.finditer(texto):
        d = {'papel': m.group(1).upper()}
        for campo, valor in re.findall(r'(\w+)\s*:\s*([^\n]+)', m.group(2)):
            d[campo.lower()] = valor.strip()
        try: d['iteracao'] = int(d.get('iteracao', '0'))
        except ValueError: d['iteracao'] = 0
        for k in ('veredito', 'posicao', 'decisao'):
            if k in d: d[k] = d[k].upper().split()[0]
        if 'nota' in d:
            try: d['nota'] = max(0.0, min(100.0, float(d['nota'].replace(',', '.').split()[0])))
            except ValueError: d['nota'] = 0.0
        d['commit'] = d.get('commit', '').lower()[:40]
        saida.append(d)
    return saida


def _mesmo(a, b):
    a, b = (a or '').lower(), (b or '').lower()
    return bool(a and b) and (a.startswith(b) or b.startswith(a)) and min(len(a), len(b)) >= 7


def decidir(comentarios):
    """comentarios: lista de textos, em ordem. Devolve {'status', 'iteracao', 'commit', 'nota', 'motivo', 'historico'}."""
    entrega, qa, prog, diretor = {}, {}, {}, None
    for c in comentarios:
        for b in blocos(c):
            i = b['iteracao']
            if b['papel'] == 'DIRETOR' and b.get('decisao'):
                diretor = None if b['decisao'] == 'RETOMAR' else b
                continue
            if not 0 < i <= MAX_ITERACOES: continue                       # iteração 4+ não existe
            if b['papel'] == 'ENTREGA': entrega[i] = b
            elif b['papel'] == 'QA':
                if b.get('veredito') == 'APROVADO' and (b.get('nota') or 0) < 80: b['veredito'] = 'REPROVADO'
                qa[i] = b; prog.pop(i, None)                              # nova avaliação: posição antiga não vale
            elif b['papel'] == 'PROGRAMADOR' and i in qa and _mesmo(b['commit'], qa[i]['commit']): prog[i] = b
    historico = []
    for i in sorted(set(qa) | set(prog) | set(entrega)):
        q, p, e = qa.get(i), prog.get(i), entrega.get(i)
        historico.append({'iteracao': i, 'commit': (q or e or p)['commit'], 'veredito': q and q.get('veredito'),
                          'nota': q and q.get('nota'), 'posicao': p and p.get('posicao'),
                          'consenso': bool(q and p and q.get('veredito') == 'APROVADO' and p.get('posicao') == 'CONCORDO'
                                           and _mesmo(q['commit'], p['commit']))})
    r = {'historico': historico, 'iteracao': max([h['iteracao'] for h in historico] or [0]), 'commit': '', 'nota': None}

    if diretor:
        r.update(status='DIRETOR' if diretor['decisao'] == 'LIBERAR' else 'PARAR', commit=diretor['commit'],
                 motivo=f"decisão do diretor: {diretor['decisao']}" + (f" ({diretor.get('motivo')})" if diretor.get('motivo') else ''))
        return r
    for h in historico:
        if h['consenso']:
            r.update(status='LIBERAR', iteracao=h['iteracao'], commit=h['commit'], nota=h['nota'],
                     motivo=f"QA aprovou e o programador concorda na iteração {h['iteracao']}")
            return r
    avaliadas = [h for h in historico if h['veredito']]
    if MAX_ITERACOES in qa and MAX_ITERACOES in prog:                 # a 3ª fechou (relatório + posição) sem consenso
        # aprovado antes de reprovado; depois a nota; empate: a mais nova
        melhor = max(avaliadas, key=lambda h: (h['veredito'] == 'APROVADO', h['nota'] or 0, h['iteracao']))
        r.update(status='MELHOR_VERSAO', iteracao=melhor['iteracao'], commit=melhor['commit'], nota=melhor['nota'],
                 motivo=f"{len(avaliadas)} iterações sem consenso: vale a de maior nota (iteração {melhor['iteracao']})")
        return r
    if not historico:
        r.update(status='AGUARDANDO_PROGRAMADOR', iteracao=1, motivo='nenhuma iteração entregue ainda')
        return r
    u = historico[-1]
    if not u['veredito']:
        r.update(status='AGUARDANDO_QA', commit=u['commit'], motivo=f"iteração {u['iteracao']} entregue; falta o QA")
    elif not u['posicao']:
        r.update(status='AGUARDANDO_PROGRAMADOR', commit=u['commit'], nota=u['nota'],
                 motivo=f"QA {u['veredito']} ({u['nota']:.0f}) na iteração {u['iteracao']}; falta a posição do programador")
    elif u['iteracao'] >= MAX_ITERACOES:
        r.update(status='AGUARDANDO_PROGRAMADOR', commit=u['commit'], motivo='iteração 3: falta a posição do programador')
    else:
        r.update(status='NOVA_ITERACAO', iteracao=u['iteracao'] + 1, commit=u['commit'],
                 motivo=f"sem consenso na iteração {u['iteracao']} (QA {u['veredito']}, programador {u['posicao']}); "
                        f"faltam {MAX_ITERACOES - u['iteracao']} iteração(ões)")
    return r


TEXTO = {'LIBERAR': '✅ **Consenso — liberar.**', 'MELHOR_VERSAO': '🏁 **Sem consenso em 3 iterações — vale a melhor versão.**',
         'DIRETOR': '🎬 **Decisão do diretor — liberar.**', 'PARAR': '⛔ **Decisão do diretor — parar.**',
         'AGUARDANDO_QA': '🧪 Aguardando o QA.', 'AGUARDANDO_PROGRAMADOR': '🛠️ Aguardando o programador.',
         'NOVA_ITERACAO': '🔁 Nova iteração.'}


def markdown(r):
    linhas = [MARCA, f"### Placar da equipe\n{TEXTO.get(r['status'], r['status'])} {r['motivo']}."]
    if r['status'] in ('LIBERAR', 'MELHOR_VERSAO', 'DIRETOR') and r['commit']:
        linhas.append(f"\nCommit a liberar: `{r['commit'][:12]}`" + (f" (nota do QA {r['nota']:.0f})" if r['nota'] is not None else '') +
                      ". O programador faz o merge desse commit e roda **Publicar versão**.")
    if r['historico']:
        linhas += ['', '| Iteração | Commit | QA | Nota | Programador | Consenso |', '|---|---|---|---|---|---|']
        for h in r['historico']:
            nota = '' if h['nota'] is None else f"{h['nota']:.0f}"
            linhas.append(f"| {h['iteracao']} | `{h['commit'][:7]}` | {h['veredito'] or '—'} | {nota} | {h['posicao'] or '—'} | {'sim' if h['consenso'] else ''} |")
    linhas.append(f"\n<sub>Regras: docs/equipe/PROCESSO.md · máximo de {MAX_ITERACOES} iterações.</sub>")
    return '\n'.join(linhas)


def _gh(*args, entrada=None):
    r = subprocess.run(['gh', 'api', *args], input=entrada, capture_output=True, text=True, encoding='utf-8')
    if r.returncode != 0: raise RuntimeError(r.stderr.strip() or r.stdout.strip())
    return json.loads(r.stdout) if r.stdout.strip() else None


def main():
    a = sys.argv[1:]
    def opc(n, padrao=None): return a[a.index(n) + 1] if n in a else padrao
    pr = opc('--pr')
    if not pr: print(__doc__); return 2
    repo = opc('--repo', os.environ.get('GITHUB_REPOSITORY', 'L-shade/JA-TOOLS'))
    corpo = (_gh(f'repos/{repo}/pulls/{pr}') or {}).get('body') or ''
    comentarios = _gh(f'repos/{repo}/issues/{pr}/comments?per_page=100', '--paginate', '--slurp')
    comentarios = [c for pagina in comentarios for c in (pagina if isinstance(pagina, list) else [pagina])]
    textos = [corpo] + [c.get('body') or '' for c in comentarios if MARCA not in (c.get('body') or '')]
    r = decidir(textos)
    texto = markdown(r)
    print(texto)
    if '--publicar' in a:
        anterior = next((c for c in comentarios if MARCA in (c.get('body') or '')), None)
        dados = json.dumps({'body': texto})
        if anterior: _gh('-X', 'PATCH', f"repos/{repo}/issues/comments/{anterior['id']}", '--input', '-', entrada=dados)
        else: _gh('-X', 'POST', f'repos/{repo}/issues/{pr}/comments', '--input', '-', entrada=dados)
        todas = set(ETIQUETAS.values()) | {f'iteracao-{i}' for i in range(1, MAX_ITERACOES + 2)}
        atuais = [l['name'] for l in (_gh(f'repos/{repo}/issues/{pr}/labels') or [])]
        for l in atuais:
            if l in todas: _gh('-X', 'DELETE', f'repos/{repo}/issues/{pr}/labels/{l}')
        novas = [ETIQUETAS[r['status']]] + ([f"iteracao-{min(r['iteracao'], MAX_ITERACOES + 1)}"] if r['iteracao'] else [])
        _gh('-X', 'POST', f'repos/{repo}/issues/{pr}/labels', '--input', '-', entrada=json.dumps({'labels': novas}))
    if os.environ.get('GITHUB_OUTPUT'):
        with open(os.environ['GITHUB_OUTPUT'], 'a', encoding='utf-8') as f:
            f.write(f"status={r['status']}\ncommit={r['commit']}\n")
    return 0


if __name__ == '__main__':
    sys.exit(main())
