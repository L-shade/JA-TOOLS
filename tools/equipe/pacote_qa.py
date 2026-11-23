#!/usr/bin/env python3
"""Monta o pacote do QA (docs/equipe/QA.md): tudo o que um testador de fora veria, SEM código-fonte.

Uso:  python tools/equipe/pacote_qa.py --pr 12 [--saida /tmp/qa-12] [--repo dono/repo]
Gera: issue.md, pr.md, ci.md, verificacao.txt, testes/ (docs/testes), docs/ (README, ROTINA).
"""
import json, os, re, shutil, subprocess, sys

RAIZ = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..'))
_CODIGO = re.compile(r'```(?:cs|csharp|c#|xml|xaml|ps1|powershell|diff)\b.*?```', re.S | re.I)


def gh(caminho, *extra):
    r = subprocess.run(['gh', 'api', caminho, *extra], capture_output=True, text=True, encoding='utf-8')
    if r.returncode != 0: raise RuntimeError(f'{caminho}: {r.stderr.strip()}')
    return json.loads(r.stdout) if r.stdout.strip() else None


def sem_codigo(texto):
    """Tira blocos de código e caminhos de src/ — o QA avalia sem ver código."""
    texto = _CODIGO.sub('[trecho de código omitido para o QA]', texto or '')
    return re.sub(r'`?src/[\w./\\-]+`?', '[arquivo-fonte]', texto)


def main():
    a = sys.argv[1:]
    def opc(n, padrao=None): return a[a.index(n) + 1] if n in a else padrao
    pr = opc('--pr')
    if not pr: print(__doc__); return 2
    repo = opc('--repo', 'L-shade/JA-TOOLS')
    saida = os.path.abspath(opc('--saida', f'/tmp/qa-{pr}'))
    if os.path.isdir(saida): shutil.rmtree(saida)
    os.makedirs(os.path.join(saida, 'docs'))

    p = gh(f'repos/{repo}/pulls/{pr}')
    sha = p['head']['sha']
    coms = gh(f'repos/{repo}/issues/{pr}/comments?per_page=100')

    # issue(s) ligadas
    nums = re.findall(r'(?:closes|fixes|resolve[s]?|fecha|resolve)\s+#(\d+)', p.get('body') or '', re.I) or \
           re.findall(r'#(\d+)', p.get('body') or '')
    with open(os.path.join(saida, 'issue.md'), 'w', encoding='utf-8') as f:
        if not nums: f.write('(o PR não cita a Issue: use o texto do PR como pedido e anote isso no relatório)\n')
        for n in dict.fromkeys(nums):
            try: i = gh(f'repos/{repo}/issues/{n}')
            except RuntimeError: continue
            if i.get('pull_request'): continue
            f.write(f"# Issue #{n} — {i['title']}\n\n{sem_codigo(i.get('body'))}\n\n")
            for c in gh(f'repos/{repo}/issues/{n}/comments?per_page=100') or []:
                f.write(f"---\n**Comentário ({c['created_at'][:10]}):**\n{sem_codigo(c.get('body'))}\n\n")

    with open(os.path.join(saida, 'pr.md'), 'w', encoding='utf-8') as f:
        f.write(f"# PR #{pr} — {p['title']}\nCommit entregue (cabeça do PR): `{sha[:7]}` · estado: {p['state']}\n\n")
        f.write(sem_codigo(p.get('body')) + '\n\n')
        for c in coms or []:
            f.write(f"---\n**Comentário ({c['created_at'][:16].replace('T', ' ')}):**\n{sem_codigo(c.get('body'))}\n\n")

    with open(os.path.join(saida, 'ci.md'), 'w', encoding='utf-8') as f:
        f.write(f'# CI no commit {sha[:7]}\n\n')
        runs = (gh(f'repos/{repo}/actions/runs?head_sha={sha}&per_page=20') or {}).get('workflow_runs', [])
        if not runs: f.write('Nenhuma execução do CI neste commit (ainda).\n')
        for r in runs:
            f.write(f"## {r['name']} — {r['status']} / {r['conclusion']} ({r['event']}, {r['created_at'][:16]})\n{r['html_url']}\n\n")
            for j in (gh(f"repos/{repo}/actions/runs/{r['id']}/jobs") or {}).get('jobs', []):
                f.write(f"- **{j['name']}**: {j['conclusion']}\n")
                for s in j.get('steps', []):
                    if s.get('conclusion') not in ('success', 'skipped', None):
                        f.write(f"  - passo “{s['name']}”: {s['conclusion']}\n")
            arts = (gh(f"repos/{repo}/actions/runs/{r['id']}/artifacts") or {}).get('artifacts', [])
            if arts:
                f.write('\nArtefatos: ' + ', '.join(f"{x['name']} ({x['size_in_bytes'] // 1048576} MB)" for x in arts) + '\n')
            f.write('\n')

    local = subprocess.run(['git', 'rev-parse', 'HEAD'], cwd=RAIZ, capture_output=True, text=True).stdout.strip()
    v = subprocess.run([sys.executable, os.path.join(RAIZ, 'tools', 'verificar', 'verificar.py')], cwd=RAIZ,
                       capture_output=True, text=True, encoding='utf-8', errors='replace',
                       env=dict(os.environ, PYTHONUTF8='1', PYTHONIOENCODING='utf-8'))
    with open(os.path.join(saida, 'verificacao.txt'), 'w', encoding='utf-8') as f:
        aviso = '' if local == sha else f'ATENÇÃO: verificação rodada no commit {local[:7]}, não no entregue {sha[:7]}.\n'
        f.write(aviso + re.sub(r'src/[\w./\\-]+', '[arquivo-fonte]', v.stdout + v.stderr) + f'\n(código de saída {v.returncode})\n')

    shutil.copytree(os.path.join(RAIZ, 'docs', 'testes'), os.path.join(saida, 'testes'),
                    ignore=shutil.ignore_patterns('__pycache__'))
    for arq in ('README.md', os.path.join('docs', 'ROTINA.md')):
        if os.path.exists(os.path.join(RAIZ, arq)): shutil.copy(os.path.join(RAIZ, arq), os.path.join(saida, 'docs'))
    print(saida)
    return 0


if __name__ == '__main__':
    sys.exit(main())
