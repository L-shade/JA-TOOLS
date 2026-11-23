#!/usr/bin/env python3
"""Libera a versão de um PR quando o placar permite (docs/equipe/PROCESSO.md).

Uso:  python tools/equipe/liberar.py --pr 12            # só mostra o que faria
      python tools/equipe/liberar.py --pr 12 --sim      # faz
      --sem-publicar: só o merge (mudança sem versão nova do add-in, ex.: processo/documentação)
Passos: placar (LIBERAR / MELHOR_VERSAO / decisão do diretor) → branch no commit escolhido → CI verde nesse commit
        → versão nova (diferente da última Release) → merge → Publicar versão → comentário no PR.
"""
import base64, json, os, re, subprocess, sys, time

AQUI = os.path.dirname(os.path.abspath(__file__))
RAIZ = os.path.abspath(os.path.join(AQUI, '..', '..'))
sys.path.insert(0, AQUI)
from consenso import decidir, MARCA, _gh as gh  # noqa: E402


def versao_em(repo, ref):
    c = gh(f'repos/{repo}/contents/Directory.Build.props?ref={ref}')
    m = re.search(r'<Version>([^<]+)</Version>', base64.b64decode(c['content']).decode('utf-8-sig'))
    return m.group(1).strip() if m else None


def ci_verde(repo, sha):
    runs = (gh(f'repos/{repo}/actions/runs?head_sha={sha}&per_page=20') or {}).get('workflow_runs', [])
    comp = [r for r in runs if r.get('path', '').endswith('compilar.yml')]
    return any(r['conclusion'] == 'success' for r in comp), comp


def main():
    a = sys.argv[1:]
    def opc(n, padrao=None): return a[a.index(n) + 1] if n in a else padrao
    pr, sim = opc('--pr'), '--sim' in a
    if not pr: print(__doc__); return 2
    repo = opc('--repo', 'L-shade/JA-TOOLS')
    p = gh(f'repos/{repo}/pulls/{pr}')
    coms = gh(f'repos/{repo}/issues/{pr}/comments?per_page=100') or []
    r = decidir([p.get('body') or ''] + [c['body'] or '' for c in coms if MARCA not in (c['body'] or '')])
    print(f"Placar: {r['status']} — {r['motivo']}")
    if r['status'] not in ('LIBERAR', 'MELHOR_VERSAO', 'DIRETOR'):
        print('Nada a liberar ainda.'); return 1
    if p['state'] != 'open':
        print(f"O PR #{pr} não está aberto ({p['state']}, merged={p.get('merged')})."); return 1
    branch, cabeca = p['head']['ref'], p['head']['sha']
    alvo = r['commit'] or cabeca
    sha = gh(f'repos/{repo}/commits/{alvo}')['sha']

    ultima = None
    try: ultima = gh(f'repos/{repo}/releases/latest')['tag_name'].lstrip('v')
    except RuntimeError: pass
    v = versao_em(repo, sha)
    print(f'Commit escolhido: {sha[:7]} (cabeça do PR: {cabeca[:7]}) · versão {v} · última Release {ultima}')
    publicar = '--sem-publicar' not in a
    if publicar and v == ultima and '--mesma-versao' not in a:
        print('A versão não mudou (tools/versao.py na iteração 1). Use --mesma-versao para reenviar os arquivos.'); return 1
    ok, comp = ci_verde(repo, sha)
    print('CI (Verificar e compilar) nesse commit: ' + ('verde' if ok else 'NÃO verde: ' + ', '.join(f"{x['status']}/{x['conclusion']}" for x in comp)))
    if not ok: return 1
    if not sim:
        print(('Faria: mover o branch para o commit escolhido; ' if sha != cabeca else '') +
              f'merge do PR #{pr}; ' + ('Publicar versão no main; ' if publicar else '') + 'comentário no PR. Rode com --sim.')
        return 0

    if sha != cabeca:
        subprocess.run(['git', 'fetch', '-q', 'origin', branch], cwd=RAIZ, check=True)
        subprocess.run(['git', '-c', 'push.negotiate=true', 'push', '--force', 'origin', f'{sha}:refs/heads/{branch}'], cwd=RAIZ, check=True)
        for _ in range(30):
            if gh(f'repos/{repo}/pulls/{pr}')['head']['sha'] == sha: break
            time.sleep(4)
    m = gh('-X', 'PUT', f'repos/{repo}/pulls/{pr}/merge', '-f', 'merge_method=merge', '-f', f'sha={sha}',
           '-f', f"commit_title=JA para Revit {v} (PR #{pr}, {r['status'].lower()})")
    print(f"Merge: {m.get('merged')} {m.get('sha', '')[:7]}")
    if not publicar:
        gh('-X', 'POST', f'repos/{repo}/issues/{pr}/comments', '--input', '-', entrada=json.dumps(
            {'body': f"✅ Merge do commit `{sha[:7]}` ({r['status'].lower().replace('_', ' ')}); sem versão nova do add-in."}))
        return 0
    gh('-X', 'POST', f'repos/{repo}/actions/workflows/publicar.yml/dispatches', '-f', 'ref=main')
    print('Publicar versão disparado (≈15 min).')
    texto = (f"🚀 **Liberada a {v}** ({r['status'].lower().replace('_', ' ')}, commit `{sha[:7]}`" +
             (f", nota do QA {r['nota']:.0f}" if r['nota'] is not None else '') + ").\n\n"
             f"Release: https://github.com/L-shade/JA-TOOLS/releases/tag/v{v} (o instalador aparece em ~15 min) · "
             "sempre a última: https://github.com/L-shade/JA-TOOLS/releases/latest/download/JA_Setup.exe")
    gh('-X', 'POST', f'repos/{repo}/issues/{pr}/comments', '--input', '-', entrada=json.dumps({'body': texto}))
    return 0


if __name__ == '__main__':
    sys.exit(main())
