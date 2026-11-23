"""Espelho de JaStandards.IsAreaOrPd (motor do AutoCAD): que textos de ambiente vão para a layer de área/PD.
Mantenha os padrões iguais aos de src/RevitDwgUnifier.AcadMerge/Cleanup/JaStandards.cs."""
import re, sys, unicodedata
NUM = r'(?:\d[\d.,]*\d|\d)'
PD = re.compile(r'(?:p\s*\.?\s*d\s*\.?|p[ée]\s*[-\s]?\s*direito)\s*[:=]?\s*(?:\d+[.,]\d+\s*(?:m(?![a-zà-ú]))?|\d+\s*m(?![a-zà-ú])|vari[áa]vel)', re.I)
AREA = re.compile(r'(?:(?:á|a)rea|a)?\s*[:=]?\s*' + NUM + r'\s*m\s*(?:²|2)', re.I)
REST = re.compile(r'[\s|/\\,;:=\-–—()\[\].]+')

UNI = re.compile(r'\\[Uu]\+([0-9A-Fa-f]{4})')
STACK = re.compile(r'\\S([^;]*);')
CODE = re.compile(r'\\[ACFHQTWpfhqtwac][^;\\{}]*;|\\[LlOoKkNn]')

def normalizar(raw):
    """Espelho de JaStandards.Normalizar: códigos de MText, \\U+, %%178, NFKC (² → 2, ㎡ → m2), invisíveis."""
    s = UNI.sub(lambda m: chr(int(m.group(1), 16)), raw)
    s = s.replace('%%178', '²').replace('%%179', '³').replace('\\~', ' ').replace('\\P', ' ')
    s = STACK.sub(lambda m: m.group(1).replace('^', '').replace('#', '').replace('/', ''), s)
    s = CODE.sub('', s).replace('{', '').replace('}', '').replace('^', '')
    s = unicodedata.normalize('NFKC', s)
    return ''.join(' ' if c.isspace() else c for c in s
                   if not (unicodedata.category(c) == 'Cf' or (unicodedata.category(c) == 'Cc' and not c.isspace())))

def area_ou_pd(raw):
    """(True, False) = só área/PD; (False, True) = mistura com outro texto; (False, False) = não é."""
    s = normalizar(raw)
    if len(s) > 120 or not s.strip(): return False, False
    hits = [0]
    def tira(m): hits[0] += 1; return ' '
    s = PD.sub(tira, s); s = AREA.sub(tira, s)
    if hits[0] == 0: return False, False
    if REST.sub('', s) == '': return True, False
    return False, True

def linhas(texto):
    """Separar(): as linhas de um MText misturado e para onde cada uma vai."""
    return [(l, area_ou_pd(l)[0]) for l in texto.split('\n') if l.strip()]

ok = falhas = 0
def check(nome, cond, info=''):
    global ok, falhas
    if cond: ok += 1
    else: falhas += 1; print('FALHOU:', nome, info)

for t in ['2.59 m²', '2,59 m²', '12.50 m2', 'A: 12,50 m²', 'Área = 3,1 m²', '2.59 m²', '2.59 m\\U+00B2', '2.59 m%%178', '2.59 m^2', '(2.59 m²)',
          'PD = 2,80', 'P.D. 2,80 m', 'PD: variável', '12.50 m2 | PD 2.70 m', 'pé-direito 2,60']:
    check('só área/PD: ' + t, area_ou_pd(t) == (True, False), area_ou_pd(t))
# 2.6.2: o "BICICLETÁRIO / 272.60 m²" e outras formas de o export escrever a área
for t in ['272.60 m²', '272.60\u00a0m²', '272.60 m²\u200b', '\ufeff272.60 m²', '272.60 ㎡', '272.60 m\u00ad²', '２７２.６０ ｍ²',
          '{\\H0.7x;272.60 m\\S2^;}', '{\\Fsimplex|c0;272.60 m}{\\H0.6x;\\A2;²}', '\\A1;272.60 m\\U+00B2', '272.60\\~m²',
          '\\pxqc;{\\T1.2;272.60 m²}', '272.60 m\t²']:
    check('só área (formas do export): ' + repr(t), area_ou_pd(t) == (True, False), area_ou_pd(t))
check('MText nome + área com códigos', area_ou_pd('{\\Farial|b0;BICICLETÁRIO}\\P{\\Fsimplex;272.60 m²}') == (False, True))
for t in ['CIRC.', 'SALA', 'P211', '25x100', '3.000', 'CIRC. 2.59', '1.200']:
    check('não é: ' + t, area_ou_pd(t) == (False, False), area_ou_pd(t))
for t in ['CIRC. 2.59 m²', 'SALA 12,50 m2']:
    check('mistura numa linha: ' + t, area_ou_pd(t) == (False, True), area_ou_pd(t))
# multi-line MText (the case of the picture: name over area)
check('MText nome + área: só a área vai', linhas('CIRC.\n2.59 m²') == [('CIRC.', False), ('2.59 m²', True)], linhas('CIRC.\n2.59 m²'))
check('MText nome + área + PD', [x[1] for x in linhas('SALA\n12,50 m²\nPD 2,80')] == [False, True, True])
# number and unit as separate texts on one line (Pares)
check('número + unidade separados', area_ou_pd(' '.join(['2.59', 'm²']))[0])
check('PD + valor separados', area_ou_pd(' '.join(['PD', '2,80']))[0])
check('nome + número na mesma linha não vai', not area_ou_pd(' '.join(['CIRC.', '2.59']))[0])
print(f'{ok} ok, {falhas} falha(s)')
sys.exit(1 if falhas else 0)
