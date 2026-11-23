"""
Verificação rápida do JA para Revit (sem compilar): pega os erros que já quebraram a compilação ou o Revit antes
de gastar uma rodada de build. Roda no GitHub Actions (job "verificar") e em qualquer máquina com Python 3:

    python tools/verificar/verificar.py            # tudo
    python tools/verificar/verificar.py --rapido   # sem os testes de algoritmo (docs/testes)

Verificações:
  1. chaves, parênteses e colchetes de todo .cs (strings, @"", $"" e comentários ignorados);
  2. versão igual em Directory.Build.props e installer/JA.iss, e registrada no MEMORIA.md;
  3. cada botão da faixa (App.cs das duas abas) tem a ferramenta no catálogo (ToolCatalog*.cs);
  4. cada comando que herda de ToolCommand (direta ou indiretamente) e não é abstrato tem [Transaction(...)];
  5. cada ícone citado no catálogo existe no IconFactory, e cada título com ícone existe no catálogo;
  6. armadilhas já vistas: "Group" sem qualificar onde Autodesk.Revit.DB e System.Text.RegularExpressions se
     encontram (CS0104); classe aninhada e método com o mesmo nome na mesma classe (CS0102);
  7. os testes de algoritmo (docs/testes/**/test_*.py) — saída ≠ 0 = falha.
No GitHub os problemas aparecem como anotações no arquivo e na linha.
"""
import os, re, subprocess, sys, glob

RAIZ = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))
GH = os.environ.get('GITHUB_ACTIONS') == 'true'
erros, avisos = [], []


def rel(p): return os.path.relpath(p, RAIZ).replace('\\', '/')


def erro(arq, linha, msg):
    erros.append((arq, linha, msg))
    if GH: print(f'::error file={rel(arq)},line={linha or 1}::{msg}')
    else: print(f'ERRO  {rel(arq)}:{linha or ""}  {msg}')


def aviso(arq, linha, msg):
    avisos.append((arq, linha, msg))
    if GH: print(f'::warning file={rel(arq)},line={linha or 1}::{msg}')
    else: print(f'aviso {rel(arq)}:{linha or ""}  {msg}')


def ler(p):
    with open(p, encoding='utf-8-sig') as f: return f.read()


def cs_files():
    out = []
    for base, dirs, files in os.walk(os.path.join(RAIZ, 'src')):
        dirs[:] = [d for d in dirs if d not in ('bin', 'obj', '.vs')]
        out += [os.path.join(base, f) for f in files if f.endswith('.cs')]
    return sorted(out)


# ------------------------------------------------------------------ 1. balance
def sem_strings(src):
    """The code with strings and comments blanked (line breaks kept, so line numbers stay right)."""
    out = []; i = 0; n = len(src)
    def pular(ate):
        out.append(''.join('\n' if c == '\n' else ' ' for c in src[i:ate]))
    while i < n:
        c = src[i]
        if src.startswith('//', i):
            j = src.find('\n', i); j = n if j < 0 else j; pular(j); i = j; continue
        if src.startswith('/*', i):
            j = src.find('*/', i + 2); j = n if j < 0 else j + 2; pular(j); i = j; continue
        if c == '@' and i + 1 < n and src[i + 1] == '"' or (c == '$' and src.startswith('$@"', i)) or (c == '@' and src.startswith('@$"', i)):
            k = src.index('"', i) + 1; j = k
            while j < n:
                if src[j] == '"' and j + 1 < n and src[j + 1] == '"': j += 2; continue
                if src[j] == '"': j += 1; break
                j += 1
            pular(j); i = j; continue
        if c == '$' and i + 1 < n and src[i + 1] == '"':
            # interpolated: the code inside { } stays
            j = i + 2; depth = 0; buf = ['  ']
            while j < n:
                ch = src[j]
                if depth == 0 and ch == '\\': buf.append('  '); j += 2; continue
                if depth == 0 and src.startswith('{{', j): buf.append('  '); j += 2; continue
                if depth == 0 and src.startswith('}}', j): buf.append('  '); j += 2; continue
                if ch == '{': depth += 1; buf.append(' '); j += 1; continue
                if ch == '}' and depth > 0: depth -= 1; buf.append(' '); j += 1; continue
                if depth == 0 and ch == '"': buf.append(' '); j += 1; break
                if depth > 0 and ch == '"':
                    k = j + 1
                    while k < n and src[k] != '"':
                        if src[k] == '\\': k += 1
                        k += 1
                    buf.append(' ' * (k + 1 - j)); j = k + 1; continue
                buf.append(ch if depth > 0 or ch == '\n' else ' '); j += 1
            out.append(''.join(buf)); i = j; continue
        if c == '"':
            j = i + 1
            while j < n and src[j] != '"' and src[j] != '\n':
                if src[j] == '\\': j += 1
                j += 1
            pular(j + 1); i = j + 1; continue
        if c == "'":
            j = i + 1
            if j < n and src[j] == '\\': j += 2
            else: j += 1
            if j < n and src[j] == "'": pular(j + 1); i = j + 1; continue
        out.append(c); i += 1
    return ''.join(out)


def balance(arq, codigo):
    pares = {')': '(', ']': '[', '}': '{'}
    pilha = []; linha = 1
    for ch in codigo:
        if ch == '\n': linha += 1
        elif ch in '([{': pilha.append((ch, linha))
        elif ch in ')]}':
            if not pilha or pilha[-1][0] != pares[ch]:
                erro(arq, linha, f"'{ch}' sem par (aberto: {pilha[-1] if pilha else 'nada'})"); return
            pilha.pop()
    if pilha: erro(arq, pilha[-1][1], f"'{pilha[-1][0]}' aberto e não fechado")


# ------------------------------------------------------------------ 2. version
def versao():
    props = os.path.join(RAIZ, 'Directory.Build.props')
    iss = os.path.join(RAIZ, 'installer', 'JA.iss')
    m1 = re.search(r'<Version>([^<]+)</Version>', ler(props))
    m2 = re.search(r'#define AppVersion "([^"]+)"', ler(iss))
    v1 = m1.group(1) if m1 else None; v2 = m2.group(1) if m2 else None
    if v1 != v2: erro(iss, 1, f'versão diferente: Directory.Build.props {v1} × JA.iss {v2}')
    mem = os.path.join(RAIZ, 'MEMORIA.md')
    if v1 and os.path.exists(mem) and ('JA para Revit ' + v1) not in ler(mem):
        aviso(mem, 1, f'a versão {v1} não aparece no MEMORIA.md (registre o que mudou)')
    return v1


# ------------------------------------------------------------------ 3-6. code structure
def estrutura(arquivos, codigos):
    # classes and bases
    classes = {}
    for arq in arquivos:
        cod = codigos[arq]
        for m in re.finditer(r'\b((?:public|internal|private|protected|sealed|abstract|static|partial|\s)*)class\s+(\w+)(?:<[^>]*>)?\s*(?::\s*([\w.<>, ]+))?', cod):
            mods, nome, bases = m.group(1), m.group(2), m.group(3) or ''
            base = bases.split(',')[0].strip().split('.')[-1] if bases else ''
            linha = cod.count('\n', 0, m.start()) + 1
            c = classes.setdefault(nome, {'base': '', 'abstrato': False, 'locais': []})
            if base and not base.startswith('I') or (base and base[:2] != 'IE' and not re.match(r'I[A-Z]', base)): c['base'] = c['base'] or base
            c['abstrato'] = c['abstrato'] or 'abstract' in mods or 'static' in mods
            c['locais'].append((arq, linha, m.start()))

    def herda(nome, alvo, prof=0):
        if prof > 12 or nome not in classes: return False
        b = classes[nome]['base']
        return b == alvo or herda(b, alvo, prof + 1)

    # 4. [Transaction] on every concrete command
    titulos_cmd = {}
    for nome, c in classes.items():
        if not herda(nome, 'ToolCommand') or c['abstrato']: continue
        tem = False
        for arq, linha, pos in c['locais']:
            antes = ler(arq)[max(0, pos - 400):pos]
            if re.search(r'\[Transaction\(', antes.split('}')[-1] if '}' in antes else antes): tem = True
        if not tem:
            arq, linha, _ = c['locais'][0]
            erro(arq, linha, f'{nome} herda de ToolCommand e não tem [Transaction(TransactionMode.Manual)] (o Revit recusa o comando)')

    for nome, c in classes.items():
        for arq, linha, pos in c['locais']:
            m = re.search(r'\bTitle\s*(?:=>|=)\s*"([^"]+)"', ler(arq)[pos:pos + 3000])
            if m: titulos_cmd[nome] = m.group(1); break

    # catalogue titles and icons
    cat_arqs = [a for a in arquivos if os.path.basename(a).startswith('ToolCatalog')]
    titulos = set(); icones_cat = []
    for a in cat_arqs:
        t = ler(a)
        titulos |= set(re.findall(r'Title\s*=\s*"([^"]+)"', t))
        for m in re.finditer(r'\["([^"]+)"\]\s*=\s*"([^"]+)"\s*,', t):
            icones_cat.append((a, t.count('\n', 0, m.start()) + 1, m.group(1), m.group(2)))
    textos_app = set()
    for a in arquivos:
        if os.path.basename(a) == 'App.cs' or 'HelpCenter' in a: textos_app |= set(re.findall(r'"([^"\\]+)"', ler(a)))
    icon_arq = next((a for a in arquivos if a.endswith('IconFactory.cs')), None)
    icones = set(re.findall(r'\["(\w+)"\]\s*=\s*"M', ler(icon_arq))) if icon_arq else set()
    for a, linha, titulo, icone in icones_cat:
        if not re.match(r'^[a-zA-Z]\w*$', icone): continue        # field help texts share the syntax
        if icone not in icones and len(icone) < 30 and ' ' not in icone:
            erro(a, linha, f'ícone "{icone}" (de "{titulo}") não existe no IconFactory')
        if titulo not in titulos and not titulo.islower() and titulo not in textos_app:
            aviso(a, linha, f'"{titulo}" tem ícone mas não está no catálogo')

    # 3. ribbon buttons → catalogue
    for a in arquivos:
        if os.path.basename(a) != 'App.cs': continue
        t = ler(a)
        for m in re.finditer(r'Button<\s*([\w.]+)\s*>\s*\(\s*"[^"]*"\s*,\s*"[^"]*"\s*(?:,\s*"([^"]+)")?', t):
            tipo = m.group(1).split('.')[-1]
            titulo = m.group(2) or titulos_cmd.get(tipo)
            linha = t.count('\n', 0, m.start()) + 1
            if titulo is None: aviso(a, linha, f'botão de {tipo}: título não encontrado'); continue
            if titulo not in titulos: erro(a, linha, f'botão "{titulo}" ({tipo}) sem ferramenta no catálogo (ToolCatalog*.cs)')

    # 6. pitfalls
    for a in arquivos:
        cod = codigos[a]; bruto = ler(a)
        if 'using Autodesk.Revit.DB;' in bruto and 'System.Text.RegularExpressions' in bruto and 'using System.Text.RegularExpressions;' in bruto:
            for m in re.finditer(r'(?<![\w.])Group\b(?!\s*\()', cod):
                antes = cod[max(0, m.start() - 12):m.start()]
                if re.search(r'(is|as|new|typeof\(|\(|<|,)\s*$', antes) or re.match(r'Group\s+\w+\s*[=;)]', cod[m.start():m.start() + 40]):
                    erro(a, cod.count('\n', 0, m.start()) + 1, '"Group" ambíguo (Autodesk.Revit.DB × RegularExpressions): use Autodesk.Revit.DB.Group')
        # nested class and method with the same name inside one class (CS0102)
        for m in re.finditer(r'\b(?:private|internal|public|protected)\s+(?:sealed\s+|static\s+)*class\s+(\w+)', cod):
            nome = m.group(1)
            if re.search(r'\b(?:public|private|internal|protected)\s+(?:static\s+|override\s+|virtual\s+)*[\w<>\[\],. ]+\s+' + nome + r'\s*\(', cod) and \
               not re.search(r'\b' + nome + r'\s*\(\s*\)\s*\{', cod[m.end():m.end() + 2000]) and \
               re.search(r'(?:void|bool|int|double|string|[A-Z]\w*)\s+' + nome + r'\s*\((?!\s*\))', cod):
                linha = cod.count('\n', 0, m.start()) + 1
                aviso(a, linha, f'classe "{nome}" e um método com o mesmo nome no arquivo (CS0102 se for na mesma classe)')


# ------------------------------------------------------------------ 7. algorithm tests
def testes():
    alvo = sorted(glob.glob(os.path.join(RAIZ, 'docs', 'testes', '**', 'test_*.py'), recursive=True))
    for t in alvo:
        try:
            r = subprocess.run([sys.executable, '-I', os.path.basename(t)], cwd=os.path.dirname(t), capture_output=True, text=True, timeout=900, encoding='utf-8', errors='replace')
            ultimas = [l for l in (r.stdout + r.stderr).splitlines() if l.strip()][-6:]
            if r.returncode != 0:
                erro(t, 1, 'teste falhou: ' + ' | '.join(ultimas))
            else:
                print(f'ok    {rel(t)}  ({ultimas[-1].strip() if ultimas else ""})')
        except subprocess.TimeoutExpired:
            erro(t, 1, 'teste passou de 15 min')


def main():
    rapido = '--rapido' in sys.argv
    arquivos = cs_files()
    codigos = {}
    for a in arquivos:
        c = sem_strings(ler(a))
        codigos[a] = c
        balance(a, c)
    v = versao()
    estrutura(arquivos, codigos)
    if not rapido: testes()
    print()
    print(f'JA {v}: {len(arquivos)} arquivos .cs, {len(erros)} erro(s), {len(avisos)} aviso(s).')
    resumo = os.environ.get('GITHUB_STEP_SUMMARY')
    if resumo:
        with open(resumo, 'a', encoding='utf-8') as f:
            f.write(f'### Verificação do JA {v}\n\n{len(arquivos)} arquivos .cs · **{len(erros)} erro(s)** · {len(avisos)} aviso(s)\n\n')
            for a, l, m in erros: f.write(f'- ❌ `{rel(a)}:{l}` {m}\n')
            for a, l, m in avisos: f.write(f'- ⚠️ `{rel(a)}:{l}` {m}\n')
    sys.exit(1 if erros else 0)


if __name__ == '__main__':
    main()
