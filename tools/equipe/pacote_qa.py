#!/usr/bin/env python3
"""Monta o pacote do QA (docs/equipe/QA.md): tudo o que um testador de fora veria, SEM código do add-in.

Uso:  python tools/equipe/pacote_qa.py equipe/pedidos/008-area.md [--saida /tmp/qa-008]     (ou só "008")
Lê o pedido, pega o commit da última entrega (bloco JA-ENTREGA) e monta, a partir DESSE commit:
  pedido.md       cabeçalho + seção "Pedido" (objetivo, critérios de aceite)
  historico.md    as iterações (entregas, relatórios anteriores, posições), sem trechos de código
  ci.md           resultado da compilação (GitHub Actions) nesse commit, se houver
  verificacao.txt saída de tools/verificar/verificar.py nesse commit
  testes/         docs/testes desse commit (com os espelhos em Python dos algoritmos) + o placar em testes/equipe
  docs/           README, ROTINA e docs/equipe/*.md
C#/XAML/PS1/projetos nunca vão: o script recusa montar um pacote com eles.
"""
import glob, json, os, re, shutil, subprocess, sys, tempfile

AQUI = os.path.dirname(os.path.abspath(__file__))
RAIZ = os.path.abspath(os.path.join(AQUI, '..', '..'))
sys.path.insert(0, AQUI)
from consenso import ler_pedido, blocos, PASTA  # noqa: E402

_CODIGO = re.compile(r'^[ \t]*(```|~~~)\s*(?:cs|csharp|c#|xml|xaml|ps1|powershell|diff)\b.*?^[ \t]*\1', re.S | re.M | re.I)


def sem_codigo(texto):
    texto = _CODIGO.sub('[trecho de código omitido para o QA]', texto or '')
    return re.sub(r'`?src/[\w./\\-]+`?', '[arquivo-fonte]', texto)


def git(*args):
    return subprocess.run(['git', *args], cwd=RAIZ, capture_output=True, text=True, encoding='utf-8').stdout.strip()


def ci(sha):
    """Compilação no GitHub Actions (só leitura; o único uso do GitHub além de guardar o código)."""
    try:
        r = subprocess.run(['gh', 'api', f'repos/L-shade/JA-TOOLS/actions/runs?head_sha={sha}&per_page=20'],
                           capture_output=True, text=True, encoding='utf-8', timeout=60)
        runs = json.loads(r.stdout).get('workflow_runs', []) if r.returncode == 0 else None
    except Exception:
        runs = None
    if runs is None: return 'Não foi possível ler a compilação (GitHub Actions): use o que a entrega diz.\n'
    if not runs: return f'Nenhuma compilação no commit {sha[:7]} (o branch foi enviado?).\n'
    saida = []
    for x in runs:
        saida.append(f"## {x['name']} — {x['status']} / {x['conclusion']} ({x['created_at'][:16]})\n{x['html_url']}\n")
        try:
            jobs = json.loads(subprocess.run(['gh', 'api', f"repos/L-shade/JA-TOOLS/actions/runs/{x['id']}/jobs"],
                                             capture_output=True, text=True, encoding='utf-8', timeout=60).stdout)['jobs']
            saida += [f"- **{j['name']}**: {j['conclusion']}" for j in jobs]
        except Exception: pass
        saida.append('')
    return '\n'.join(saida) + '\n'


def achar(caminho):
    if os.path.exists(caminho): return caminho
    achados = glob.glob(os.path.join(PASTA, caminho.zfill(3) + '-*.md'))
    return achados[0] if achados else None


def main():
    a = sys.argv[1:]
    saida_opc = a[a.index('--saida') + 1] if '--saida' in a else None
    arqs = [x for i, x in enumerate(a) if not x.startswith('--') and (i == 0 or a[i - 1] != '--saida')]
    if not arqs: print(__doc__); return 2
    caminho = achar(arqs[0])
    if not caminho: print('pedido não encontrado:', arqs[0]); return 2
    _, texto, cab = ler_pedido(caminho)
    entregas = [b for b in blocos(texto) if b['papel'] == 'ENTREGA']
    if not entregas: print('o pedido ainda não tem entrega (bloco JA-ENTREGA)'); return 1
    git('fetch', '-q', 'origin')
    sha = git('rev-parse', '--verify', '--quiet', entregas[-1]['commit'] + '^{commit}')
    if not sha: print(f"commit {entregas[-1]['commit']} não encontrado (o branch foi enviado?)"); return 1

    nome = os.path.splitext(os.path.basename(caminho))[0]
    saida = os.path.abspath(saida_opc or os.path.join(tempfile.gettempdir(), 'qa-' + nome))
    if os.path.isdir(saida): shutil.rmtree(saida)
    os.makedirs(os.path.join(saida, 'docs'))

    partes = re.split(r'^##\s+Itera[cç][õo]es\b.*$', texto, maxsplit=1, flags=re.M)
    with open(os.path.join(saida, 'pedido.md'), 'w', encoding='utf-8') as f:
        f.write(sem_codigo(partes[0]))
    with open(os.path.join(saida, 'historico.md'), 'w', encoding='utf-8') as f:
        f.write(f'# Iterações — para avaliar: commit `{sha[:7]}` (branch {cab["branch"] or "?"})\n\n')
        f.write(sem_codigo(partes[1]) if len(partes) > 1 else '(sem iterações anteriores)\n')
    with open(os.path.join(saida, 'ci.md'), 'w', encoding='utf-8') as f:
        f.write(f'# Compilação no commit {sha[:7]}\n\n' + ci(sha))

    # verificação e testes do commit entregue (cópia temporária desse commit)
    wt = tempfile.mkdtemp(prefix='qa-wt-')
    try:
        subprocess.run(['git', 'worktree', 'add', '-q', '--detach', '-f', wt, sha], cwd=RAIZ, check=True, capture_output=True)
        v = subprocess.run([sys.executable, os.path.join(wt, 'tools', 'verificar', 'verificar.py')], cwd=wt,
                           capture_output=True, text=True, encoding='utf-8', errors='replace',
                           env=dict(os.environ, PYTHONUTF8='1', PYTHONIOENCODING='utf-8'))
        with open(os.path.join(saida, 'verificacao.txt'), 'w', encoding='utf-8') as f:
            f.write(f'Verificação no commit {sha[:7]}\n' + re.sub(r'src/[\w./\\-]+', '[arquivo-fonte]', v.stdout + v.stderr) +
                    f'\n(código de saída {v.returncode})\n')
        shutil.copytree(os.path.join(wt, 'docs', 'testes'), os.path.join(saida, 'testes'), ignore=shutil.ignore_patterns('__pycache__'))
        for arq in ['README.md', os.path.join('docs', 'ROTINA.md')]:
            if os.path.exists(os.path.join(wt, arq)): shutil.copy(os.path.join(wt, arq), os.path.join(saida, 'docs'))
    finally:
        subprocess.run(['git', 'worktree', 'remove', '--force', wt], cwd=RAIZ, capture_output=True)
        shutil.rmtree(wt, ignore_errors=True)
    os.makedirs(os.path.join(saida, 'testes', 'equipe'), exist_ok=True)
    shutil.copy(os.path.join(AQUI, 'consenso.py'), os.path.join(saida, 'testes', 'equipe'))
    equipe = os.path.join(RAIZ, 'docs', 'equipe')
    for x in sorted(os.listdir(equipe)):
        if x.endswith('.md'): shutil.copy(os.path.join(equipe, x), os.path.join(saida, 'docs'))

    proibidos = [os.path.join(r, f) for r, _, fs in os.walk(saida) for f in fs
                 if f.lower().endswith(('.cs', '.xaml', '.ps1', '.csproj', '.sln', '.props'))]
    if proibidos:
        shutil.rmtree(saida, ignore_errors=True)
        raise RuntimeError('o pacote do QA não pode ter código do add-in: ' + ', '.join(proibidos))
    print(saida)
    return 0


if __name__ == '__main__':
    sys.exit(main())
