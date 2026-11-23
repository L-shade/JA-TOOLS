"""Plan prototype of the JA Famílias rodapé engine (mm). versao='atual' = 1.27.0 logic, 'nova' = the fixed one."""
import math, re

def add(a, b): return (a[0] + b[0], a[1] + b[1])
def sub(a, b): return (a[0] - b[0], a[1] - b[1])
def mul(a, k): return (a[0] * k, a[1] * k)
def dot(a, b): return a[0] * b[0] + a[1] * b[1]
def cross(a, b): return a[0] * b[1] - a[1] * b[0]
def norm(a):
    l = math.hypot(*a); return (a[0] / l, a[1] / l)
def dist(a, b): return math.hypot(a[0] - b[0], a[1] - b[1])
def left(d): return (-d[1], d[0])

class Wall:
    def __init__(s, p0, p1, W, curtain=False, name='', flip=False):
        s.p0, s.p1, s.W, s.curtain, s.name, s.flip = p0, p1, W, curtain, name, flip
    def d(s): return norm(sub(s.p1, s.p0))
class Column:
    pass
class Door:
    def __init__(s, host, pt, width, kind='door', zmin=0): s.host, s.pt, s.width, s.kind, s.zmin = host, pt, width, kind, zmin
class Opening:
    def __init__(s, host, q0, q1, zmin=0): s.host, s.q0, s.q1, s.zmin = host, q0, q1, zmin
class Box:
    def __init__(s, x0, y0, x1, y1, z0=0, z1=900, cantos=None):
        s.x0, s.y0, s.x1, s.y1, s.z0, s.z1 = x0, y0, x1, y1, z0, z1
        s.cantos = cantos or [(x0, y0), (x1, y0), (x0, y1), (x1, y1)]
    @staticmethod
    def girado(c, comp, prof, ang, z0=0, z1=900):
        """a piece of furniture comp × prof centred at c, turned ang degrees (its AABB is what Revit reports)."""
        u = (math.cos(math.radians(ang)), math.sin(math.radians(ang))); v = left(u)
        cs = [add(c, add(mul(u, sx * comp / 2), mul(v, sy * prof / 2))) for sx, sy in ((-1, -1), (1, -1), (1, 1), (-1, 1))]
        xs, ys = [q[0] for q in cs], [q[1] for q in cs]
        return Box(min(xs), min(ys), max(xs), max(ys), z0, z1, cs)

class Cfg:
    def __init__(s, **k):
        s.Altura, s.Espessura, s.Portas, s.Folga, s.Moveis, s.Minimo = 70, 15, True, 0, True, 60
        s.Janelas, s.Aberturas, s.Retorno, s.SemCortina, s.Revestidas, s.Cantos = True, True, False, True, 'cerâmica, ceramica, porcelanato, azulejo, pastilha', True
        s.__dict__.update(k)
    def palavras(s): return [x.strip().lower() for x in re.split('[,;]', s.Revestidas) if x.strip()]

class Linha:
    def __init__(s): s.Bruto = s.Portas = s.Janelas = s.Vaos = s.Moveis = s.Cortina = s.Revestidas = s.Curtos = s.Retornos = s.Liquido = 0.0

class Ctx:
    def __init__(s, c, portas=(), janelas=(), aberturas=(), moveis=()):
        s.c = c; s.Portas, s.Janelas, s.Aberturas, s.Obst = list(portas), list(janelas), list(aberturas), list(moveis)
        s.Folga, s.Minimo, s.E, s.HRod = c.Folga, c.Minimo, c.Espessura, c.Altura

def externo(d1, d2): return cross(d1, d2) < -math.sin(math.radians(20))

# ------------------------------------------------------------------ per stretch
def cortes_do_trecho(k, a, b, host, z, versao, livre=False):
    """(cuts, accounting, retornos, L, d) for one stretch with the stone on its left."""
    c = k.c
    lr = Linha()
    coluna = isinstance(host, Column)
    if not isinstance(host, Wall) and not coluna and not livre: return None, lr, [], 0, None
    L = dist(a, b); d = norm(sub(b, a)); esq = left(d)
    lr.Bruto += L
    w = host if isinstance(host, Wall) else None
    if w and c.SemCortina and w.curtain: lr.Cortina += L; return [], lr, [], L, d
    if w and c.palavras() and any(p in w.name.lower() for p in c.palavras()): lr.Revestidas += L; return [], lr, [], L, d
    cortes = []; retornos = []
    def Dist(p): return abs(dot(sub(p, a), esq))
    def S(p): return dot(sub(p, a), d)
    def candidato(h, p):
        """the insert is in the wall behind this stretch"""
        if versao == 'atual': return h is w and Dist(p) <= w.W + 152.4
        if not isinstance(h, Wall) or abs(cross(h.d(), d)) > 0.02: return False
        tt = dot(sub(p, a), esq)
        return tt <= 15 and -tt <= h.W + 152.4
    for dr in k.Portas:
        if not c.Portas or not candidato(dr.host, dr.pt): continue
        s = S(dr.pt); cortes.append((s - dr.width / 2 - k.Folga, s + dr.width / 2 + k.Folga, 0, None))
    for jn in k.Janelas:
        if not c.Janelas or not candidato(jn.host, jn.pt) or jn.zmin > z + k.HRod + 10: continue
        s = S(jn.pt); cortes.append((s - jn.width / 2 - k.Folga, s + jn.width / 2 + k.Folga, 1, None))
    for ab in k.Aberturas:
        if not c.Aberturas or not candidato(ab.host, mul(add(ab.q0, ab.q1), 0.5)) or ab.zmin > z + k.HRod + 10: continue
        s0, s1 = sorted((S(ab.q0), S(ab.q1)))
        if s1 <= 0 or s0 >= L: continue
        cortes.append((s0, s1, 2, ab))
        if c.Retorno:
            h = ab.host
            # depth: to the centre of the host wall (half its thickness when the stretch is on it)
            prof = h.W / 2 if versao == 'atual' else abs(dot(sub(h.p0, a), esq))
            for sj, sinal in ((s0, 1), (s1, -1)):
                if sj <= 1e-6 or sj >= L - 1e-6: continue
                face = add(a, mul(d, sj)); dentro = sub(face, mul(esq, prof))
                ida = dot(left(norm(sub(dentro, face))), mul(d, sinal)) > 0
                retornos.append((face, dentro) if ida else (dentro, face))
    for bb in k.Obst:
        if bb.z0 > z + k.HRod + 50 or bb.z1 < z + 10: continue
        cantos = bb.cantos if versao == 'v2' else [(bb.x0, bb.y0), (bb.x1, bb.y0), (bb.x0, bb.y1), (bb.x1, bb.y1)]
        t0 = min(dot(sub(q, a), esq) for q in cantos); t1 = max(dot(sub(q, a), esq) for q in cantos)
        if t1 < -30 or t0 > 150: continue
        s0 = min(dot(sub(q, a), d) for q in cantos); s1 = max(dot(sub(q, a), d) for q in cantos)
        if s1 <= 0 or s0 >= L: continue
        cortes.append((s0, s1, 3, None))
    trechos = [(0.0, L, False, False)]
    for (c0, c1, tipo, ab) in sorted(cortes, key=lambda x: x[2]):
        tirado = sum(max(0, min(t1, c1) - max(t0, c0)) for (t0, t1, _, _) in trechos)
        if tipo == 0: lr.Portas += tirado
        elif tipo == 1: lr.Janelas += tirado
        elif tipo == 2: lr.Vaos += tirado
        else: lr.Moveis += tirado
        ret = tipo == 2 and c.Retorno
        novo = []
        for (t0, t1, ri, rf) in trechos:
            if c1 <= t0 or c0 >= t1: novo.append((t0, t1, ri, rf)); continue
            if c0 > t0: novo.append((t0, c0, ri, ret and 0 < c0 < L))
            if c1 < t1: novo.append((c1, t1, ret and 0 < c1 < L, rf))
        trechos = [x for x in novo if x[1] - x[0] > 1e-6]
    return trechos, lr, retornos, L, d

def colocar_laco(k, laco, z, versao, fechado=True, livre=False, virados=None, vizinho=None):
    """laco: [(a, b, host)] stone on the left. Returns (pieces [(p0,p1)], Linha total)."""
    c = k.c
    tot = Linha(); pecas = []
    calc = [cortes_do_trecho(k, a, b, h, z, versao, livre) for (a, b, h) in laco]
    n = len(laco)
    virados = virados or [False] * n
    for i, (tr, lr, ret, L, d) in enumerate(calc):
        for f in tot.__dict__: setattr(tot, f, getattr(tot, f) + getattr(lr, f))
    if versao == 'atual':
        for i, (tr, lr, ret, L, d) in enumerate(calc):
            if tr is None: continue
            a = laco[i][0]
            dAnt = calc[(i - 1) % n][4] or norm(sub(laco[(i - 1) % n][1], laco[(i - 1) % n][0]))
            dProx = calc[(i + 1) % n][4] or norm(sub(laco[(i + 1) % n][1], laco[(i + 1) % n][0]))
            temAnt, temProx = fechado or i > 0, fechado or i < n - 1
            extIni = c.Cantos and temAnt and externo(dAnt, d)
            extFim = c.Cantos and temProx and externo(d, dProx)
            for (s0, s1, _, _) in tr:
                if s1 - s0 < k.Minimo: tot.Curtos += s1 - s0; continue
                i0 = -k.E if s0 < 1e-6 and extIni else s0
                i1 = L + k.E if s1 > L - 1e-6 and extFim else s1
                pecas.append((add(a, mul(d, i0)), add(a, mul(d, i1)))); tot.Liquido += i1 - i0
            for (p, q) in ret: pecas.append((p, q)); tot.Retornos += dist(p, q)
        return pecas, tot
    # ---- nova: corners from what really reaches them; inner corners butt, outer corners covered once
    aj = [[0.0, 0.0] for _ in range(n)]          # + trims, − extends (start, end)
    liso = [[False, False] for _ in range(n)]     # continues smoothly into the neighbour (curves): no "short" test there
    def chega(i, fim):
        tr, _, _, L, _ = calc[i]
        if not tr: return False
        return any(t0 < 1e-6 for (t0, _, _, _) in tr) if fim == 0 else any(t1 > L - 1e-6 for (_, t1, _, _) in tr)
    absorvido = [False] * n
    if versao == 'v2':
        # corners from the meeting point of the two lines; micro stretches at a corner (a few mm of step, gap or
        # chamfer where two faces do not quite meet) are bridged: the neighbours run to their meeting point
        micro_lim = max(k.Minimo, 2 * k.E, 20)
        def valido(i): return calc[i][0] is not None and not virados[i]
        def micro(i): return valido(i) and calc[i][3] < micro_lim
        def prox(i): return (i + 1) % n if (i + 1 < n or fechado) else None
        def canto(i, j, pulados):
            """corner between i and j (the micro stretches between them bridged); False = not a bridgeable corner"""
            di, dj = calc[i][4], calc[j][4]
            phi = math.atan2(cross(di, dj), dot(di, dj))
            if abs(phi) < math.radians(20):
                if pulados: return False
                if chega(i, 1) and chega(j, 0): liso[i][1] = liso[j][0] = True
                return True
            if not chega(i, 1) or not chega(j, 0): return not pulados
            den = cross(di, dj); w_ = sub(laco[j][0], laco[i][0])
            ext = cross(w_, dj) / den - calc[i][3]; uX = cross(w_, di) / den
            tol = sum(calc[q][3] for q in pulados) + 3 * k.E + 20
            if abs(ext) > tol or abs(uX) > tol:
                if pulados: return False
                ext, uX = 0.0, 0.0
            for q in pulados: absorvido[q] = True
            aj[i][1] = -ext; aj[j][0] = uX
            if c.Cantos:
                if phi < 0: aj[i][1] -= min(3 * k.E, k.E * math.tan(-phi / 2))
                else: aj[j][0] += min(3 * k.E, k.E / math.sin(phi))
            return True
        # a closed loop is walked from a stretch that is not micro, so a micro stretch is always reached from its
        # neighbour before it is taken on its own
        ini = next((q for q in range(n) if valido(q) and not micro(q)), 0) if fechado else 0
        for i in [(ini + q) % n for q in range(n)]:
            if absorvido[i] or not valido(i): continue
            j = prox(i)
            if j is None or j == i: continue
            if not micro(i):
                pulados, q = [], j
                while q is not None and q != i and micro(q) and len(pulados) < 3:
                    pulados.append(q); q = prox(q)
                if pulados and q is not None and q != i and valido(q) and not micro(q) and canto(i, q, pulados): continue
            if valido(j) and not absorvido[j]: canto(i, j, [])
        # outer corner of a wall split by a room separation line that continues this face: the room on this side of
        # the line covers the corner square (when the room on the other side gets / has rodapé)
        if c.Cantos and vizinho:
            def sep(q): return calc[q][0] is None and laco[q][2] is None and dist(laco[q][0], laco[q][1]) > 1e-6
            for i in range(n):
                if not valido(i) or absorvido[i] or not calc[i][0]: continue
                di, Li, Ai = calc[i][4], calc[i][3], laco[i][0]
                ni = left(di)
                p_ = (i - 1) % n if (i > 0 or fechado) else None
                if p_ is not None and p_ != i and sep(p_) and chega(i, 0) and aj[i][0] == 0:
                    ds = norm(sub(laco[p_][1], laco[p_][0]))
                    if abs(cross(ds, di)) < math.sin(math.radians(20)) and dot(ds, di) > 0 and \
                       vizinho(sub(sub(Ai, mul(di, k.E / 2)), mul(ni, k.E / 2))):
                        aj[i][0] = -k.E
                q_ = prox(i)
                Bi = add(Ai, mul(di, Li))
                if q_ is not None and q_ != i and sep(q_) and chega(i, 1) and aj[i][1] == 0:
                    ds = norm(sub(laco[q_][1], laco[q_][0]))
                    if abs(cross(ds, di)) < math.sin(math.radians(20)) and dot(ds, di) > 0 and \
                       vizinho(sub(add(Bi, mul(di, k.E / 2)), mul(ni, k.E / 2))):
                        aj[i][1] = -k.E
    else:
        pares = [(i, i + 1) for i in range(n - 1)] + ([(n - 1, 0)] if fechado and n > 1 else [])
        for (i, j) in pares:
            if virados[i] or virados[j] or not chega(i, 1) or not chega(j, 0): continue
            di, dj = calc[i][4], calc[j][4]
            phi = math.atan2(cross(di, dj), dot(di, dj))
            if abs(phi) < math.radians(20):
                liso[i][1] = liso[j][0] = True; continue
            if not c.Cantos: continue
            if phi < 0: aj[i][1] = -min(3 * k.E, k.E * math.tan(-phi / 2))       # outer: the incoming piece covers the corner
            else: aj[j][0] = min(3 * k.E, k.E / math.sin(phi))                  # inner: the outgoing piece butts on it
    for i, (tr, lr, ret, L, d) in enumerate(calc):
        if not tr: continue
        a = laco[i][0]
        if absorvido[i]:
            tot.Curtos += sum(t1 - t0 for (t0, t1, _, _) in tr); continue
        for (s0, s1, ri, rf) in tr:
            i0, i1 = s0, s1
            if s0 < 1e-6: i0 += aj[i][0]
            elif ri: i0 -= k.E                     # beside an opening with return: covers the jamb corner
            if s1 > L - 1e-6: i1 -= aj[i][1]
            elif rf: i1 += k.E
            curto = (i1 - i0) < k.Minimo and not ((s0 < 1e-6 and liso[i][0]) or (s1 > L - 1e-6 and liso[i][1]))
            if curto or i1 - i0 < 1: tot.Curtos += max(0, s1 - s0); continue
            pecas.append((add(a, mul(d, i0)), add(a, mul(d, i1)))); tot.Liquido += i1 - i0
        for (p, q) in ret:
            pecas.append((p, q)); tot.Retornos += dist(p, q)
    return pecas, tot

# ------------------------------------------------------------------ geometry checks
def retangulo(p, q, e):
    d = norm(sub(q, p)); n = mul(left(d), e)
    return [p, q, add(q, n), add(p, n)]
def area(poly):
    return abs(sum(cross(poly[i], poly[(i + 1) % len(poly)]) for i in range(len(poly)))) / 2
def clip(subj, clipper):
    """Sutherland–Hodgman (convex clipper, CCW or CW)."""
    def ccw(poly): return sum(cross(poly[i], poly[(i + 1) % len(poly)]) for i in range(len(poly))) > 0
    if not ccw(clipper): clipper = clipper[::-1]
    out = subj
    for i in range(len(clipper)):
        A, B = clipper[i], clipper[(i + 1) % len(clipper)]
        inp, out = out, []
        if not inp: break
        def inside(p): return cross(sub(B, A), sub(p, A)) >= -1e-9
        def inter(p, q):
            r, s_ = sub(q, p), sub(B, A)
            t = cross(sub(A, p), s_) / cross(r, s_)
            return add(p, mul(r, t))
        for j in range(len(inp)):
            P, Q = inp[j], inp[(j + 1) % len(inp)]
            if inside(Q):
                if not inside(P): out.append(inter(P, Q))
                out.append(Q)
            elif inside(P): out.append(inter(P, Q))
    return out
def sobreposicao(pecas, e):
    rs = [retangulo(p, q, e) for (p, q) in pecas]
    tot = 0
    for i in range(len(rs)):
        for j in range(i + 1, len(rs)):
            x = clip(rs[i], rs[j])
            if len(x) >= 3: tot += area(x)
    return tot
def coberto(pecas, e, pt):
    for (p, q) in pecas:
        r = retangulo(p, q, e)
        if all(cross(sub(r[(i + 1) % 4], r[i]), sub(pt, r[i])) >= -1e-6 for i in range(4)) or \
           all(cross(sub(r[(i + 1) % 4], r[i]), sub(pt, r[i])) <= 1e-6 for i in range(4)): return True
    return False

# ------------------------------------------------------------------ Acabamento de base (Rodapé por Ambiente)
RE_ACAB = re.compile(r'^\s*(?P<mat>.+?)\s*[-–,;]?\s*\bh\s*=?\s*(?P<h>\d+(?:[.,]\d+)?)\s*(?P<u>mm|cm)?(?:\s*(?:[x×]|e\s*=?|esp\.?\s*=?)\s*(?P<e>\d+(?:[.,]\d+)?)\s*(?P<ue>mm|cm)?)?', re.I)
RE_ACAB_NOVA = re.compile(r'^\s*(?P<mat>.+?)\s*[-–,;:]?\s*(?:\b(?:h|alt(?:ura)?)\.?\s*[=:]?\s*(?P<h>\d+(?:[.,]\d+)?)\s*(?P<u>mm|cm)?|(?P<h2>\d+(?:[.,]\d+)?)\s*(?P<u2>mm|cm)\b)'
                          r'(?:\s*(?:[x×]|e\s*[=:]?|esp\.?\s*[=:]?)\s*(?P<e>\d+(?:[.,]\d+)?)\s*(?P<ue>mm|cm)?)?', re.I)
def ler_acabamento(s, esp=15, regex=RE_ACAB):
    m = regex.match(s or '')
    if not m: return None
    def num(g, u):
        v = float(m.group(g).replace(',', '.'))
        return v * 10 if m.group(u) and m.group(u).lower() == 'cm' else v
    mat = m.group('mat').strip(' -–,;:')
    gd = m.groupdict()
    alt = num('h', 'u') if gd.get('h') else num('h2', 'u2')
    e = num('e', 'ue') if m.group('e') else esp
    if regex is not RE_ACAB and sum(ch.isalpha() for ch in mat) < 2: return None
    if not mat or alt < 20 or alt > 400: return None
    if e < 5 or e > 40: e = esp
    return mat, alt, e

# ------------------------------------------------------------------ Rodapé no Local: chains of the drawing
def cadeias(segs3d, tol=5):
    segs = []
    for (a, b) in segs3d:
        a, b = (a[0], a[1]), (b[0], b[1])
        if dist(a, b) < 20: continue
        if any((dist(x, a) < tol and dist(y, b) < tol) or (dist(x, b) < tol and dist(y, a) < tol) for (x, y) in segs): continue
        segs.append((a, b))
    res = []
    while segs:
        cad = [segs[0][0], segs[0][1]]; segs.pop(0)
        achou = True
        while achou:
            achou = False
            for i, (a, b) in enumerate(segs):
                ini, fim = cad[0], cad[-1]
                if dist(fim, a) < tol: cad.append(b)
                elif dist(fim, b) < tol: cad.append(a)
                elif dist(ini, b) < tol: cad.insert(0, a)
                elif dist(ini, a) < tol: cad.insert(0, b)
                else: continue
                segs.pop(i); achou = True; break
        fechada = len(cad) > 3 and dist(cad[0], cad[-1]) < tol
        if fechada: cad.pop()
        res.append((cad, fechada))
    return res
