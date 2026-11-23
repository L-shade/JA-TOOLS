"""Espelho em Python do Formula.cs (tokens, precedência, funções principais) para testar sem o Revit."""
import re, math, unicodedata
def fold(s): return ''.join(c for c in unicodedata.normalize('NFD', (s or '').lower()) if unicodedata.category(c) != 'Mn')
NUMRX = re.compile(r'^\s*-?\d+(?:[.,]\d+)*\s*[a-zA-Z²³%°]*\s*$')
NR = re.compile(r'-?\d+(?:[.,]\d+)*')
def numero(s):
    m = NR.search(s or '');
    if not m: return None
    t = m.group(0); v = t.rfind(','); p = t.rfind('.')
    if v >= 0 and p >= 0: t = t.replace('.', '').replace(',', '.') if v > p else t.replace(',', '')
    elif v >= 0: t = t.replace(',', '.')
    elif p >= 0 and t.count('.') > 1: t = t.replace('.', '')
    return float(t)
def txt(v):
    if v is None: return ''
    if isinstance(v, bool): return 'Sim' if v else 'Não'
    if isinstance(v, float):
        s = ('%.4f' % v).rstrip('0').rstrip('.'); return s.replace('.', ',')
    return str(v)
def num(v):
    if v is None: return None
    if isinstance(v, bool): return 1.0 if v else 0.0
    if isinstance(v, float): return v
    if isinstance(v, str): return numero(v) if NUMRX.match(v) else None
def boo(v):
    if v is None: return False
    if isinstance(v, bool): return v
    if isinstance(v, float): return abs(v) > 1e-12
    f = fold(v).strip(); return not (f == '' or f in ('nao', 'falso', 'false', 'no', '0'))
def vazio(v): return v is None or v == ''
def cmp(a, b):
    x, y = num(a), num(b)
    if x is not None and y is not None and not (isinstance(a, str) and not NUMRX.match(a)) and not (isinstance(b, str) and not NUMRX.match(b)):
        return 0 if abs(x - y) < 1e-9 else (-1 if x < y else 1)
    if isinstance(a, bool) or isinstance(b, bool): return 0 if boo(a) == boo(b) else (1 if boo(a) else -1)
    sa, sb = fold(txt(a)).strip(), fold(txt(b)).strip()
    return 0 if sa == sb else (-1 if sa < sb else 1)
class Erro(Exception): pass
def tokens(s):
    r = []; i = 0
    while i < len(s):
        c = s[i]
        if c.isspace(): i += 1; continue
        ini = i
        if c.isdigit() or (c == '.' and i + 1 < len(s) and s[i+1].isdigit()):
            while i < len(s) and s[i].isdigit(): i += 1
            if i + 1 < len(s) and s[i] in '.,' and s[i+1].isdigit():
                i += 1
                while i < len(s) and s[i].isdigit(): i += 1
            r.append(('num', float(s[ini:i].replace(',', '.')))); continue
        if c in '"“”':
            b = []; i += 1
            while True:
                if i >= len(s): raise Erro('aspas')
                d = s[i]
                if d in '"”“':
                    if d == '"' and i + 1 < len(s) and s[i+1] == '"': b.append('"'); i += 2; continue
                    i += 1; break
                b.append(d); i += 1
            r.append(('str', ''.join(b))); continue
        if c == '{':
            f = s.index('}', i + 1); r.append(('campo', s[i+1:f].strip())); i = f + 1; continue
        if c.isalpha() or c == '_':
            while i < len(s) and (s[i].isalnum() or s[i] in '_.'): i += 1
            r.append(('id', s[ini:i])); continue
        if c == '(': r.append(('(', '(')); i += 1; continue
        if c == ')': r.append((')', ')')); i += 1; continue
        if c in ';,': r.append(('sep', c)); i += 1; continue
        two = s[i:i+2]
        if two in ('<=', '>=', '<>', '!=', '==', '&&', '||'): r.append(('op', two)); i += 2; continue
        if c in '+-*/^&=<>!': r.append(('op', c)); i += 1; continue
        raise Erro('char ' + c)
    r.append(('fim', None)); return r
def F(n): return fold(n).replace('_', '.').strip()
class P:
    def __init__(s, t): s.t = t; s.i = 0
    def at(s): return s.t[s.i]
    def nx(s): return s.t[min(s.i + 1, len(s.t) - 1)]
    def eat(s): s.i += 1; return s.t[s.i - 1]
    def isop(s, *o): return s.at()[0] == 'op' and s.at()[1] in o
    def word(s, *w): return s.at()[0] == 'id' and F(s.at()[1]) in w
    def ou(s):
        a = s.e()
        while s.isop('||') or (s.word('ou', 'or') and s.nx()[0] != '('): s.eat(); a = ('ou', a, s.e())
        return a
    def e(s):
        a = s.cmpn()
        while s.isop('&&') or (s.word('e', 'and') and s.nx()[0] != '('): s.eat(); a = ('e', a, s.cmpn())
        return a
    def cmpn(s):
        a = s.cat()
        if s.isop('=', '==', '<>', '!=', '<', '>', '<=', '>='):
            op = s.eat()[1]; op = {'==': '=', '!=': '<>'}.get(op, op); a = (op, a, s.cat())
        return a
    def cat(s):
        a = s.soma()
        while s.isop('&'): s.eat(); a = ('&', a, s.soma())
        return a
    def soma(s):
        a = s.mult()
        while s.isop('+', '-'): op = s.eat()[1]; a = (op, a, s.mult())
        return a
    def mult(s):
        a = s.un()
        while s.isop('*', '/'): op = s.eat()[1]; a = (op, a, s.un())
        return a
    def un(s):
        if s.isop('-'): s.eat(); return ('neg', s.un())
        if s.isop('+'): s.eat(); return s.un()
        if s.isop('!') or s.word('nao', 'not'): s.eat(); return ('nao1', s.un())
        return s.pot()
    def pot(s):
        a = s.prim()
        if s.isop('^'): s.eat(); a = ('^', a, s.un())
        return a
    def prim(s):
        t = s.eat()
        if t[0] == 'num' or t[0] == 'str': return ('c', t[1])
        if t[0] == 'campo': return ('campo', t[1])
        if t[0] == '(':
            d = s.ou()
            if s.eat()[0] != ')': raise Erro(')')
            return d
        if t[0] == 'id':
            n = F(t[1])
            if s.at()[0] != '(':
                if n in ('verdadeiro', 'true', 'sim'): return ('c', True)
                if n in ('falso', 'false'): return ('c', False)
                if n == 'pi': return ('c', math.pi)
                raise Erro('id ' + t[1])
            s.eat(); args = []
            if s.at()[0] != ')':
                args.append(s.ou())
                while s.at()[0] == 'sep': s.eat(); args.append(s.ou())
            if s.eat()[0] != ')': raise Erro(') de ' + t[1])
            return ('f', NOMES[n], args)
        raise Erro('fora ' + str(t))
NOMES = {'se': 'SE', 'if': 'SE', 'ses': 'SES', 'escolher': 'ESCOLHER', 'e': 'E', 'ou': 'OU', 'nao': 'NAO', 'arred': 'ARRED', 'round': 'ARRED',
         'teto': 'TETO', 'piso': 'PISO', 'concatenar': 'CONCAT', 'esquerda': 'ESQ', 'direita': 'DIR', 'ext.texto': 'MID', 'num.caract': 'LEN',
         'maiuscula': 'UP', 'substituir': 'SUBST', 'dividir': 'SPLIT', 'contem': 'CONTEM', 'texto': 'TEXTO', 'min': 'MIN', 'max': 'MAX', 'vazio': 'VAZIO',
         'arredondar.para.cima': 'ACIMA', 'mod': 'MOD', 'soma': 'SOMA'}
def ev(n, c):
    k = n[0]
    if k == 'c': return n[1]
    if k == 'campo': return c(n[1])
    if k == 'neg': x = num(ev(n[1], c)); return None if x is None else -x
    if k == 'nao1': return not boo(ev(n[1], c))
    if k == 'e': return boo(ev(n[1], c)) and boo(ev(n[2], c))
    if k == 'ou': return boo(ev(n[1], c)) or boo(ev(n[2], c))
    if k == 'f': return fn(n[1], n[2], c)
    a, b = ev(n[1], c), ev(n[2], c)
    if k == '&': return txt(a) + txt(b)
    if k == '=': return cmp(a, b) == 0
    if k == '<>': return cmp(a, b) != 0
    if k in ('<', '>', '<=', '>='):
        if vazio(a) or vazio(b): return False
        r = cmp(a, b); return {'<': r < 0, '>': r > 0, '<=': r <= 0, '>=': r >= 0}[k]
    x, y = num(a), num(b)
    if x is None or y is None: return None
    return {'+': x + y, '-': x - y, '*': x * y, '/': (x / y if abs(y) > 1e-15 else None), '^': x ** y}[k]
def arred(x, casas, modo):
    m = 10 ** casas; v = x * m
    if modo == 0: v = math.floor(abs(v) + 0.5) * (1 if v >= 0 else -1)
    elif modo > 0: v = math.ceil(v - 1e-9) if v >= 0 else math.floor(v + 1e-9)
    return v / m
def fn(f, a, c):
    A = lambda i: ev(a[i], c) if i < len(a) else None
    N = lambda i: num(A(i)); S = lambda i: txt(A(i))
    if f == 'SE': return A(1) if boo(A(0)) else (A(2) if len(a) > 2 else False)
    if f == 'SES':
        for i in range(0, len(a) - 1, 2):
            if boo(A(i)): return A(i + 1)
        return A(len(a) - 1) if len(a) % 2 == 1 else None
    if f == 'ESCOLHER':
        v = A(0); i = 1
        while i + 1 < len(a):
            if cmp(v, A(i)) == 0: return A(i + 1)
            i += 2
        return A(i) if i < len(a) else None
    if f == 'E': return all(boo(ev(x, c)) for x in a)
    if f == 'OU': return any(boo(ev(x, c)) for x in a)
    if f == 'NAO': return not boo(A(0))
    if f == 'VAZIO': return vazio(A(0))
    if f in ('ARRED', 'ACIMA'): x = N(0); return None if x is None else arred(x, int(round(N(1) or 0)), 0 if f == 'ARRED' else 1)
    if f in ('TETO', 'PISO'):
        x = N(0); m = abs(N(1) if len(a) > 1 else 1.0); q = x / m
        return (math.ceil(q - 1e-9) if f == 'TETO' else math.floor(q + 1e-9)) * m
    if f == 'CONCAT': return ''.join(txt(ev(x, c)) for x in a)
    if f == 'ESQ': s = S(0); n = max(0, int(round(N(1) if len(a) > 1 else 1))); return s[:n]
    if f == 'DIR': s = S(0); n = max(0, int(round(N(1) if len(a) > 1 else 1))); return s[len(s) - min(n, len(s)):]
    if f == 'MID': s = S(0); i = max(1, int(round(N(1)))) - 1; n = max(0, int(round(N(2)))); return s[i:i + n]
    if f == 'LEN': return float(len(S(0)))
    if f == 'UP': return S(0).upper()
    if f == 'SUBST': return re.sub(re.escape(S(1)), S(2).replace('\\', '\\\\'), S(0), flags=re.I) if S(1) else S(0)
    if f == 'SPLIT':
        s, sep, n = S(0), S(1), int(round(N(2))); p = s.split(sep) if sep else [s]
        if n < 0: n = len(p) + n + 1
        return p[n - 1].strip() if 1 <= n <= len(p) else ''
    if f == 'CONTEM': return fold(S(1)) in fold(S(0))
    if f == 'TEXTO':
        v = A(0)
        if len(a) < 2: return txt(v)
        x = num(v); k = int(round(N(1)));
        return txt(v) if x is None else ('%.*f' % (k, x)).replace('.', ',')
    if f in ('MIN', 'MAX', 'SOMA'):
        ns = [num(ev(x, c)) for x in a]; ns = [x for x in ns if x is not None]
        if not ns: return 0.0 if f == 'SOMA' else None
        return min(ns) if f == 'MIN' else max(ns) if f == 'MAX' else sum(ns)
    if f == 'MOD': x, y = N(0), N(1); return x - y * math.floor(x / y)
def calc(expr, campos):
    s = expr.strip(); s = s[1:] if s.startswith('=') else s
    p = P(tokens(s)); n = p.ou()
    if p.at()[0] != 'fim': raise Erro('resto')
    return ev(n, lambda k: campos.get(k))
