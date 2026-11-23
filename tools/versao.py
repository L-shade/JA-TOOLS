"""
Muda a versão do JA para Revit nos dois lugares (Directory.Build.props e installer/JA.iss):

    python tools/versao.py            # mostra a versão atual
    python tools/versao.py 2.6.1      # troca para 2.6.1
    python tools/versao.py patch      # 2.6.0 -> 2.6.1   (minor: 2.7.0, major: 3.0.0)

Depois: registrar a mudança no MEMORIA.md (a verificação avisa se faltar) e, para publicar, a etiqueta vX.Y.Z.
"""
import re, sys, os
RAIZ = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
PROPS = os.path.join(RAIZ, 'Directory.Build.props')
ISS = os.path.join(RAIZ, 'installer', 'JA.iss')


def ler(p, enc='utf-8-sig'):
    with open(p, encoding=enc) as f: return f.read()


def gravar(p, s, bom):
    with open(p, 'w', encoding='utf-8-sig' if bom else 'utf-8', newline='') as f: f.write(s)


def main():
    props = ler(PROPS)
    atual = re.search(r'<Version>([^<]+)</Version>', props).group(1)
    if len(sys.argv) < 2:
        print(atual); return
    arg = sys.argv[1]
    a, b, c = (int(x) for x in atual.split('.')[:3])
    nova = {'patch': f'{a}.{b}.{c + 1}', 'minor': f'{a}.{b + 1}.0', 'major': f'{a + 1}.0.0'}.get(arg, arg)
    if not re.fullmatch(r'\d+\.\d+\.\d+', nova): sys.exit('versão inválida: ' + nova)
    bom_props = open(PROPS, 'rb').read(3) == b'\xef\xbb\xbf'
    bom_iss = open(ISS, 'rb').read(3) == b'\xef\xbb\xbf'
    gravar(PROPS, props.replace(f'<Version>{atual}</Version>', f'<Version>{nova}</Version>'), bom_props)
    iss = ler(ISS)
    gravar(ISS, re.sub(r'#define AppVersion "[^"]+"', f'#define AppVersion "{nova}"', iss), bom_iss)
    print(f'{atual} -> {nova}  (registre no MEMORIA.md: "JA para Revit {nova}")')


if __name__ == '__main__':
    main()
