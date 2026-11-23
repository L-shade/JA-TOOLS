#!/usr/bin/env python3
"""Placar da equipe (docs/equipe/PROCESSO.md): lê o arquivo de um pedido (equipe/pedidos/NNN-*.md) e decide.

Os papéis acrescentam seções ao arquivo, em ordem, cada uma com o seu bloco (fica invisível no Markdown):
  <!-- JA-ENTREGA    iteracao: 1  commit: abc1234 -->                    (programador entrega a iteração)
  <!-- JA-QA          iteracao: 1  commit: abc1234  veredito: APROVADO|REPROVADO  nota: 0-100 -->
  <!-- JA-PROGRAMADOR iteracao: 1  commit: abc1234  posicao: CONCORDO|DISCORDO -->
  <!-- JA-DIRETOR     decisao: LIBERAR|PARAR|RETOMAR  commit: abc1234 -->
(um campo por linha; regras completas no PROCESSO.md)

Decisão:
  LIBERAR        QA APROVADO + programador CONCORDO na mesma iteração e no mesmo commit.
  MELHOR_VERSAO  a 3ª iteração fechou sem consenso: APROVADO > APROVADO com DISCORDO > REPROVADO; depois a nota.
  AGUARDANDO_QA / AGUARDANDO_PROGRAMADOR / NOVA_ITERACAO  enquanto isso.  DIRETOR / PARAR: decisão do diretor.

Uso:  python tools/equipe/consenso.py equipe/pedidos/008-area.md [--gravar]   # placar de um pedido
      python tools/equipe/consenso.py --quadro [--gravar]                      # todos os pedidos (equipe/QUADRO.md)
      --gravar: escreve o placar no topo do arquivo do pedido (entre as marcas JA-CONSENSO) / grava o quadro.
"""
import glob, os, re, sys, unicodedata

MAX_ITERACOES = 3
MARCA = '<!-- JA-CONSENSO -->'
FIM_MARCA = '<!-- /JA-CONSENSO -->'
RAIZ = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..'))
# o quadro vive no branch quadro: use a pasta equipe/ de onde o comando é chamado (ex.: a cópia ../ja-quadro), senão a do repositório
EQUIPE = next((x for x in (os.path.join(os.getcwd(), 'equipe'), os.path.join(RAIZ, 'equipe')) if os.path.isdir(x)),
              os.path.join(RAIZ, 'equipe'))
PASTA = os.path.join(EQUIPE, 'pedidos')

_BLOCO = re.compile(r'<!--\s*JA-(ENTREGA|QA|PROGRAMADOR|DIRETOR)\b(.*?)-->', re.S | re.I)


_CERCA = re.compile(r'^[ \t]*(```|~~~).*?(?:^[ \t]*\1[^\n]*$|\Z)', re.S | re.M)
_ABERTO = re.compile(r'<!--\s*JA-(ENTREGA|QA|PROGRAMADOR|DIRETOR)\b', re.I)
VALORES = {'veredito': ('APROVADO', 'REPROVADO'), 'posicao': ('CONCORDO', 'DISCORDO'), 'decisao': ('LIBERAR', 'PARAR', 'RETOMAR')}
EXIGE = {'ENTREGA': ('iteracao', 'commit'), 'QA': ('iteracao', 'commit', 'veredito', 'nota'),
         'PROGRAMADOR': ('iteracao', 'commit', 'posicao'), 'DIRETOR': ('decisao',)}


def _sem_acento(t):
    return unicodedata.normalize('NFKD', t).encode('ascii', 'ignore').decode().lower()


def blocos(texto, avisos=None):
    """Blocos JA-* válidos de um comentário (papel, iteracao, commit, ...). Os inválidos vão para `avisos`."""
    avisos = [] if avisos is None else avisos
    saida = []
    texto = _CERCA.sub('', (texto or '').replace('\r\n', '\n'))                         # exemplos em ``` / ~~~
    texto = '\n'.join(l for l in texto.splitlines() if not l.lstrip().startswith('>'))  # citações
    achados = list(_BLOCO.finditer(texto))
    if len(_ABERTO.findall(texto)) > len(achados): avisos.append('bloco JA- sem "-->" no fim (ignorado)')
    for m in achados:
        papel = m.group(1).upper()
        d, erros = {'papel': papel}, []
        for campo, valor in re.findall(r'([^\W\d_]+)\s*:\s*([^\n]*)', m.group(2)):
            d[_sem_acento(campo)] = re.sub(r'\(.*?\)', '', valor).strip()                 # "(ou REPROVADO)" sai
        for campo in EXIGE[papel]:
            if not d.get(campo): erros.append(f'falta {campo}')
        if d.get('iteracao'):
            if re.fullmatch(r'\d+', d['iteracao']): d['iteracao'] = int(d['iteracao'])
            else: erros.append(f"iteração “{d['iteracao']}”")
        for campo, ok in VALORES.items():
            if campo in d and d[campo]:
                v = d[campo].upper()
                if v in ok: d[campo] = v
                else: erros.append(f"{campo} “{d[campo]}” (use {' ou '.join(ok)})")
        if d.get('nota'):
            n = re.fullmatch(r'(-?\d+(?:[.,]\d+)?)\s*(?:/\s*100)?', d['nota'])
            if n: d['nota'] = max(0.0, min(100.0, float(n.group(1).replace(',', '.'))))
            else: erros.append(f"nota “{d['nota']}”")
        c = d.get('commit', '').lower()
        if c and not re.fullmatch(r'[0-9a-f]{7,40}', c): erros.append(f'commit “{c}” (7 a 40 caracteres hexadecimais)')
        d['commit'] = c
        if papel == 'DIRETOR' and d.get('decisao') == 'LIBERAR' and not c: erros.append('LIBERAR sem commit')
        if erros: avisos.append(f"JA-{papel} ignorado: " + '; '.join(erros)); continue
        d.setdefault('iteracao', 0)
        saida.append(d)
    return saida


def _mesmo(a, b):
    a, b = (a or '').lower(), (b or '').lower()
    return bool(a and b) and (a.startswith(b) or b.startswith(a)) and min(len(a), len(b)) >= 7


def _classe(h):
    """Ordem da melhor versão: APROVADO sem discordância > APROVADO com DISCORDO > REPROVADO."""
    if h['veredito'] != 'APROVADO': return 0
    return 1 if h['posicao'] == 'DISCORDO' else 2


def decidir(comentarios):
    """comentarios: lista de textos, em ordem. Devolve {'status', 'iteracao', 'commit', 'nota', 'motivo', 'historico', 'avisos'}."""
    entrega, qa, prog, diretor, avisos = {}, {}, {}, None, []
    fechada = lambda i: i in qa and i in prog
    for c in comentarios:
        for b in blocos(c, avisos):
            i = b['iteracao']
            if b['papel'] == 'DIRETOR':
                diretor = None if b['decisao'] == 'RETOMAR' else b
                continue
            if not 1 <= i <= MAX_ITERACOES:
                avisos.append(f"JA-{b['papel']} da iteração {i} ignorado (só 1 a {MAX_ITERACOES})"); continue
            if i > 1 and not fechada(i - 1):
                avisos.append(f"JA-{b['papel']} da iteração {i} ignorado: a iteração {i - 1} não fechou (relatório + posição)"); continue
            if b['papel'] == 'ENTREGA': entrega[i] = b
            elif b['papel'] == 'QA':
                if b['veredito'] == 'APROVADO' and b['nota'] < 80:
                    b['veredito'] = 'REPROVADO'; avisos.append(f'iteração {i}: APROVADO com nota {b["nota"]:.0f} vale REPROVADO')
                qa[i] = b; prog.pop(i, None)                              # nova avaliação: posição antiga não vale
            elif b['papel'] == 'PROGRAMADOR':
                if i not in qa: avisos.append(f'posição da iteração {i} antes do relatório do QA (ignorada)')
                elif not _mesmo(b['commit'], qa[i]['commit']): avisos.append(f"posição da iteração {i} com commit {b['commit'][:7]} ≠ {qa[i]['commit'][:7]} do QA (ignorada)")
                else: prog[i] = b
    historico = []
    for i in sorted(set(qa) | set(prog) | set(entrega)):
        q, p, e = qa.get(i), prog.get(i), entrega.get(i)
        historico.append({'iteracao': i, 'commit': (q or e or p)['commit'], 'veredito': q and q.get('veredito'),
                          'nota': q and q.get('nota'), 'posicao': p and p.get('posicao'),
                          'consenso': bool(q and p and q['veredito'] == 'APROVADO' and p['posicao'] == 'CONCORDO')})
    r = {'historico': historico, 'iteracao': max([h['iteracao'] for h in historico] or [0]), 'commit': '', 'nota': None,
         'avisos': avisos}

    if diretor:
        r.update(status='DIRETOR' if diretor['decisao'] == 'LIBERAR' else 'PARAR', commit=diretor['commit'],
                 motivo=f"decisão do diretor: {diretor['decisao']}" + (f" ({diretor.get('motivo')})" if diretor.get('motivo') else ''))
        return r
    for h in historico:
        if h['consenso']:
            r.update(status='LIBERAR', iteracao=h['iteracao'], commit=h['commit'], nota=h['nota'],
                     motivo=f"QA aprovou e o programador concorda na iteração {h['iteracao']}")
            return r
    if fechada(MAX_ITERACOES):                                         # a 3ª fechou (relatório + posição) sem consenso
        avaliadas = [h for h in historico if h['veredito']]
        melhor = max(avaliadas, key=lambda h: (_classe(h), h['nota'] or 0, h['iteracao']))
        r.update(status='MELHOR_VERSAO', iteracao=melhor['iteracao'], commit=melhor['commit'], nota=melhor['nota'],
                 motivo=f"{len(avaliadas)} iterações sem consenso: vale a melhor (iteração {melhor['iteracao']})")
        if _classe(melhor) < 2:
            avisos.append('diretor: a melhor versão ' + ('foi REPROVADA pelo QA' if _classe(melhor) == 0 else
                          'foi APROVADA, mas o programador discordou (sabe de um problema)') + ' — confira antes de liberar')
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
    linhas = [MARCA, f"> **Placar** (gerado — não edite): {TEXTO.get(r['status'], r['status'])} {r['motivo']}."]
    if r['status'] in ('LIBERAR', 'MELHOR_VERSAO', 'DIRETOR') and r['commit']:
        linhas.append(f"\nCommit a liberar: `{r['commit'][:12]}`" + (f" (nota do QA {r['nota']:.0f})" if r['nota'] is not None else '') +
                      ". O programador libera com `tools/equipe/liberar.py` (merge no main; Publicar versão só se o add-in mudou).")
    if r['historico']:
        linhas += ['', '| Iteração | Commit | QA | Nota | Programador | Consenso |', '|---|---|---|---|---|---|']
        for h in r['historico']:
            nota = '' if h['nota'] is None else f"{h['nota']:.0f}"
            linhas.append(f"| {h['iteracao']} | `{h['commit'][:7]}` | {h['veredito'] or '—'} | {nota} | {h['posicao'] or '—'} | {'sim' if h['consenso'] else ''} |")
    if r.get('avisos'):
        linhas += ['', '**Avisos** (blocos não reconhecidos e alertas):'] + [f'- {a}' for a in dict.fromkeys(r['avisos'])]
    linhas.append(f"\n<sub>Regras: docs/equipe/PROCESSO.md · máximo de {MAX_ITERACOES} iterações.</sub>")
    return '\n'.join(linhas)


def _regiao():
    return re.compile(re.escape(MARCA) + r'.*?' + re.escape(FIM_MARCA) + r'\n?', re.S)


def ler_pedido(caminho):
    """Texto do pedido sem o placar gravado, e o cabeçalho (título, estado, branch, prioridade)."""
    with open(caminho, encoding='utf-8') as f: bruto = f.read()
    texto = _regiao().sub('', bruto)
    cab = {'titulo': (re.search(r'^#\s+(.+)$', texto, re.M) or [None, os.path.basename(caminho)])[1].strip()}
    for campo in ('Estado', 'Branch', 'Prioridade'):
        m = re.search(r'\*\*' + campo + r':\*\*\s*`?([^`\n]+?)`?\s*$', texto, re.M)
        cab[campo.lower()] = m.group(1).strip() if m else ''
    return bruto, texto, cab


def gravar_placar(caminho, bruto, r):
    """Placar logo abaixo do título do pedido (substitui o anterior)."""
    bloco = markdown(r) + '\n' + FIM_MARCA + '\n'
    if MARCA in bruto: novo = _regiao().sub(lambda _: bloco, bruto, count=1)
    else:
        linhas = bruto.split('\n', 1)
        novo = linhas[0] + '\n\n' + bloco + (linhas[1] if len(linhas) > 1 else '')
    with open(caminho, 'w', encoding='utf-8') as f: f.write(novo)


def quadro():
    linhas = ['# Quadro da equipe', '', 'Gerado por `python tools/equipe/consenso.py --quadro --gravar`. Regras: docs/equipe/PROCESSO.md.', '',
              '| Pedido | Estado | Placar | Iteração | Nota | Commit | Branch |', '|---|---|---|---|---|---|---|']
    for c in sorted(glob.glob(os.path.join(PASTA, '*.md'))):
        _, texto, cab = ler_pedido(c)
        r = decidir([texto])
        nota = '' if r['nota'] is None else f"{r['nota']:.0f}"
        nome = os.path.basename(c)
        linhas.append(f"| [{cab['titulo']}](pedidos/{nome}) | {cab['estado']} | {TEXTO.get(r['status'], r['status'])} | "
                      f"{r['iteracao'] or ''} | {nota} | {('`' + r['commit'][:7] + '`') if r['commit'] else ''} | {cab['branch']} |")
    return '\n'.join(linhas) + '\n'


def main():
    a = sys.argv[1:]
    if '--quadro' in a:
        q = quadro(); print(q)
        if '--gravar' in a:
            with open(os.path.join(EQUIPE, 'QUADRO.md'), 'w', encoding='utf-8') as f: f.write(q)
        return 0
    arqs = [x for x in a if not x.startswith('--')]
    if not arqs: print(__doc__); return 2
    caminho = arqs[0]
    if not os.path.exists(caminho):
        achados = glob.glob(os.path.join(PASTA, caminho.zfill(3) + '-*.md'))   # "8" ou "008" → equipe/pedidos/008-*.md
        if not achados: print('pedido não encontrado:', caminho); return 2
        caminho = achados[0]
    bruto, texto, _ = ler_pedido(caminho)
    r = decidir([texto])
    print(markdown(r))
    if '--gravar' in a: gravar_placar(caminho, bruto, r)
    return 0


if __name__ == '__main__':
    sys.exit(main())
