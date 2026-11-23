#!/usr/bin/env python3
"""Libera um pedido quando o placar permite (docs/equipe/PROCESSO.md) — sem Issues nem PRs.

Uso:  python tools/equipe/liberar.py equipe/pedidos/008-area.md          # só mostra o que faria (ou só "008")
      python tools/equipe/liberar.py 008 --sim                          # faz
      --sem-publicar  pedido que não muda o add-in (processo, docs, CI): só o merge
      --mesma-versao  reenviar os arquivos da versão atual
Passos: placar (LIBERAR / MELHOR_VERSAO / diretor) → o commit escolhido está no branch do pedido → compilação verde
        nesse commit → versão nova (se mudou o add-in) → merge no main (GitHub, sem PR) → Publicar versão → registro
        no pedido (rode de dentro da cópia do quadro, ../ja-quadro, e envie o quadro depois).
"""
import glob, json, os, re, subprocess, sys

AQUI = os.path.dirname(os.path.abspath(__file__))
RAIZ = os.path.abspath(os.path.join(AQUI, '..', '..'))
sys.path.insert(0, AQUI)
from consenso import decidir, ler_pedido, gravar_placar, PASTA  # noqa: E402

ADDIN = ('src/', 'build/', 'Directory.Build.props', 'JA.iss')


def git(*args, check=False):
    r = subprocess.run(['git', *args], cwd=RAIZ, capture_output=True, text=True, encoding='utf-8')
    if check and r.returncode != 0: raise RuntimeError('git ' + ' '.join(args) + ': ' + r.stderr.strip())
    return r.stdout.strip()


def versao_em(ref):
    m = re.search(r'<Version>([^<]+)</Version>', git('show', f'{ref}:Directory.Build.props'))
    return m.group(1).strip() if m else None


def ultima_publicada():
    tags = re.findall(r'refs/tags/v(\d+\.\d+\.\d+)$', git('ls-remote', '--tags', 'origin'), re.M)
    return max(tags, key=lambda t: tuple(map(int, t.split('.'))), default=None)


def compilacao_verde(sha):
    try:
        r = subprocess.run(['gh', 'api', f'repos/L-shade/JA-TOOLS/actions/runs?head_sha={sha}&per_page=20'],
                           capture_output=True, text=True, encoding='utf-8', timeout=60)
        runs = [x for x in json.loads(r.stdout).get('workflow_runs', []) if x.get('path', '').endswith('compilar.yml')]
        return any(x['conclusion'] == 'success' for x in runs), ', '.join(f"{x['status']}/{x['conclusion']}" for x in runs) or 'nenhuma'
    except Exception as e:
        return False, f'não lida ({e})'


def main():
    a = sys.argv[1:]
    arqs = [x for x in a if not x.startswith('--')]
    if not arqs: print(__doc__); return 2
    caminho = arqs[0] if os.path.exists(arqs[0]) else next(iter(glob.glob(os.path.join(PASTA, arqs[0].zfill(3) + '-*.md'))), None)
    if not caminho: print('pedido não encontrado:', arqs[0]); return 2
    sim, publicar = '--sim' in a, '--sem-publicar' not in a
    bruto, texto, cab = ler_pedido(caminho)
    r = decidir([texto])
    print(f"Placar: {r['status']} — {r['motivo']}")
    if r['status'] not in ('LIBERAR', 'MELHOR_VERSAO', 'DIRETOR'): print('Nada a liberar ainda.'); return 1
    if 'liberado' in cab['estado'].lower(): print('Este pedido já foi liberado.'); return 1
    branch = cab['branch']
    if not branch: print('O pedido não diz o branch (**Branch:** no cabeçalho).'); return 1

    git('fetch', '-q', 'origin')
    sha = git('rev-parse', '--verify', '--quiet', r['commit'] + '^{commit}')
    if not sha: print(f"commit {r['commit']} não encontrado"); return 1
    if subprocess.run(['git', 'merge-base', '--is-ancestor', sha, f'origin/{branch}'], cwd=RAIZ).returncode != 0:
        print(f'o commit {sha[:7]} não está no branch origin/{branch}'); return 1
    arquivos = git('diff', '--name-only', f'origin/main...{sha}').splitlines()
    addin = [f for f in arquivos if f.startswith(ADDIN)]
    if any(f.startswith('equipe/') for f in arquivos):
        print('o branch mexe em equipe/ (o quadro vive no branch quadro): tire essas mudanças do branch'); return 1
    if not publicar and addin:
        print('--sem-publicar recusado: o pedido muda o add-in (' + ', '.join(addin[:5]) + ').'); return 1
    if publicar and not addin:
        print('O pedido não muda o add-in: use --sem-publicar (só o merge).'); return 1
    v, ultima = versao_em(sha), ultima_publicada()
    if publicar and v == ultima and '--mesma-versao' not in a:
        print(f'A versão {v} já foi publicada: suba a versão na iteração 1 (tools/versao.py).'); return 1
    ok, estado = compilacao_verde(sha)
    print(f'Commit {sha[:7]} · versão {v} (última publicada {ultima}) · compilação: {estado}')
    if not ok: print('A compilação (Verificar e compilar) não está verde nesse commit.'); return 1
    if not sim:
        print(f'Faria: merge de {sha[:7]} no main' + (', Publicar versão' if publicar else '') + ', registro no pedido. Rode com --sim.')
        return 0

    # merge do GitHub (sem PR): o main só recebe código pelo processo
    m = subprocess.run(['gh', 'api', '-X', 'POST', 'repos/L-shade/JA-TOOLS/merges', '-f', 'base=main', '-f', f'head={sha}',
                        '-f', f"commit_message=JA para Revit {v}: {cab['titulo']} ({r['status'].lower()})"],
                       capture_output=True, text=True, encoding='utf-8')
    if m.returncode != 0: print('Merge recusado: ' + (m.stderr or m.stdout).strip()); return 1
    print('Merge no main feito (' + (json.loads(m.stdout).get('sha', '')[:7] if m.stdout.strip() else 'já estava') + ').')
    if publicar:
        subprocess.run(['gh', 'api', '-X', 'POST', 'repos/L-shade/JA-TOOLS/actions/workflows/publicar.yml/dispatches',
                        '-f', 'ref=main'], check=True, capture_output=True)
        print('Publicar versão disparado (≈15 min).')
    nota = '' if r['nota'] is None else f", nota do QA {r['nota']:.0f}"
    registro = (f"\n### Liberação\n{r['status'].lower().replace('_', ' ')} — commit `{sha[:7]}`{nota}; merge no main" +
                (f"; versão {v} em https://github.com/L-shade/JA-TOOLS/releases/tag/v{v}" if publicar else '; sem versão nova do add-in') + '.\n')
    bruto = open(caminho, encoding='utf-8').read() + registro
    bruto = re.sub(r'(\*\*Estado:\*\*\s*)[^\n]*', r'\1liberado', bruto, count=1)
    gravar_placar(caminho, bruto, r)
    print(f'Registro gravado em {caminho} — atualize e envie o quadro (consenso.py --quadro --gravar; commit; push).')
    return 0


if __name__ == '__main__':
    sys.exit(main())
