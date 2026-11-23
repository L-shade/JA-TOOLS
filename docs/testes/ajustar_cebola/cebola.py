"""Plan-geometry prototype of Ajustar Paredes Cebola (mm). Mirrors the C# logic so the cases can be tested."""
import math

def v(x, y): return (float(x), float(y))
def add(a, b): return (a[0] + b[0], a[1] + b[1])
def sub(a, b): return (a[0] - b[0], a[1] - b[1])
def mul(a, k): return (a[0] * k, a[1] * k)
def dot(a, b): return a[0] * b[0] + a[1] * b[1]
def cross(a, b): return a[0] * b[1] - a[1] * b[0]
def norm(a):
    l = math.hypot(*a); return (a[0] / l, a[1] / l)
def dist(a, b): return math.hypot(a[0] - b[0], a[1] - b[1])

class Wall:
    _n = 100
    def __init__(s, p0, p1, W, key='C', layers=None, flip=False, z0=0, z1=2800, name='', join=(True, True)):
        Wall._n += 1; s.id = Wall._n
        s.p0, s.p1, s.W, s.key, s.layers, s.flip, s.z0, s.z1, s.name = v(*p0), v(*p1), W, key, layers, flip, z0, z1, name
        s.join = list(join)
    def ori(s):
        d = norm(sub(s.p1, s.p0)); left = (-d[1], d[0])
        return mul(left, -1) if s.flip else left
    def desvio(s):
        W = s.W
        if s.key == 'FE': p = 0
        elif s.key == 'FI': p = W
        elif s.key in ('CC', 'CE', 'CI'):
            ini, fim = 0, W
            if s.layers:
                acc = 0
                for w, core in s.layers:
                    if core and ini == 0 and acc >= 0 and not hasattr(s, '_f'): pass
                cores = [i for i, (w, c) in enumerate(s.layers) if c]
                acc = 0
                for i, (w, c) in enumerate(s.layers):
                    if i == cores[0]: ini = acc
                    acc += w
                    if i == cores[-1]: fim = acc
            p = ini if s.key == 'CE' else fim if s.key == 'CI' else (ini + fim) / 2
        else: p = W / 2
        return p - W / 2
    def centre(s):
        o = mul(s.ori(), s.desvio()); return add(s.p0, o), add(s.p1, o)
    def move(s, d): s.p0, s.p1 = add(s.p0, d), add(s.p1, d)

class Info:
    def __init__(s, w, espMax, palavras):
        s.W = w; s.A, s.B = w.centre(); s.L = dist(s.A, s.B); s.D = norm(sub(s.B, s.A)); s.N = w.ori()
        s.Larg = w.W; s.Z0, s.Z1 = w.z0, w.z1
        s.Revest = w.W <= espMax + 1e-9 or any(k in w.name.lower() for k in palavras)
    def meio(s): return mul(add(s.A, s.B), 0.5)

def sobre_ao_longo(b, r):
    t0, t1 = dot(sub(r.A, b.A), b.D), dot(sub(r.B, b.A), b.D)
    return min(max(t0, t1), b.L) - max(min(t0, t1), 0)

def paralelas(a, b, lim=0.0175): return abs(cross(a.D, b.D)) <= lim
def sobre_altura(a, b): return min(a.Z1, b.Z1) - max(a.Z0, b.Z0)

class Par:
    def __init__(s, rev, core, lado, e):
        s.Rev, s.Core, s.Lado, s.E = rev, core, lado, e
        s.Base = core; s.F = 0.0; s.Folga = None; s.Prof = 1

def emparelhar(infos, tol, empilha=150.0):
    """core of every finish (nearest non-finish wall on its side), then the layers of each core side in order."""
    def acha(revs, nucleos):
        achados = {}
        for r in revs:
            best = None
            for b in nucleos:
                if b is r or not paralelas(b, r) or sobre_altura(b, r) < 30 or sobre_ao_longo(b, r) < 50: continue
                sd = dot(sub(r.meio(), b.A), b.N)
                if abs(sd) < 1: continue
                e = abs(sd) - (b.Larg + r.Larg) / 2
                if e < -tol or e > tol + empilha: continue
                if best is None or abs(e) < abs(best.E) - 1 or (abs(abs(e) - abs(best.E)) <= 1 and sobre_ao_longo(b, r) > sobre_ao_longo(best.Core, r)):
                    best = Par(r, b, 1 if sd > 0 else -1, e)
            if best: achados[r.W.id] = best
        return achados
    nucleos = [i for i in infos if not i.Revest]
    revs = [i for i in infos if i.Revest]
    achados = acha(revs, nucleos)
    sem = [r for r in revs if r.W.id not in achados]
    # a thin wall with nothing to sit on is a wall (a thin partition) when no thicker coreless finish is next to it
    def vizinho_mais_grosso(r):
        for o in sem:
            if o is r or o.Larg <= r.Larg or not paralelas(o, r) or sobre_ao_longo(o, r) < 50 or sobre_altura(o, r) < 30: continue
            sd = dot(sub(r.meio(), o.A), o.N)
            if abs(abs(sd) - (o.Larg + r.Larg) / 2) <= tol: return True
        return False
    promovidos = [r for r in sem if not vizinho_mais_grosso(r)]
    if promovidos:
        for r in promovidos: r.Revest = False
        nucleos += promovidos
        revs = [i for i in infos if i.Revest]
        achados = acha(revs, nucleos)
    # layers per core side, inside out
    grupos = {}
    for p in achados.values(): grupos.setdefault((p.Core.W.id, p.Lado), []).append(p)
    pares = []
    import functools
    def cmp(a, b):
        if abs(a.E - b.E) < 1: return -1 if a.Rev.Larg > b.Rev.Larg else 1 if a.Rev.Larg < b.Rev.Larg else (a.Rev.W.id - b.Rev.W.id)
        return -1 if a.E < b.E else 1
    for k, l in grupos.items():
        l.sort(key=functools.cmp_to_key(cmp))
        postos = []
        for p in l:
            opc = [(0.0, p.Core, 1)]
            for q in postos:
                if sobre_ao_longo(q.Rev, p.Rev) > 50 and sobre_altura(q.Rev, p.Rev) > 30:
                    opc.append((q.Fc + q.Rev.Larg, q.Rev, q.Prof + 1))
            F, base, prof = min(opc, key=lambda o: abs(p.E - o[0]))
            p.Folga = p.E - F
            if abs(p.Folga) <= tol:
                p.F, p.Base, p.Prof, p.Fc = F, base, prof, F
                pares.append(p)
            else:
                p.Fc = p.E          # stays; may still carry the next layer
            postos.append(p)
    return pares

def girar(w, centro, ang):
    c, s_ = math.cos(ang), math.sin(ang)
    def r(p):
        q = sub(p, centro); return add(centro, (q[0] * c - q[1] * s_, q[0] * s_ + q[1] * c))
    w.p0, w.p1 = r(w.p0), r(w.p1)

def encostar(pares):
    n = 0
    for p in pares:
        # a little turned: parallel to the wall first (around the middle of its centre line)
        r = p.Rev
        ang = math.atan2(cross(r.D, p.Core.D), dot(r.D, p.Core.D))
        if abs(ang) > math.pi / 2: ang = ang - math.copysign(math.pi, ang)
        if abs(ang) > 1e-5:
            girar(r.W, r.meio(), ang); n += 1
            r2 = Info(r.W, 1e9, ()); r.A, r.B, r.D, r.N = r2.A, r2.B, r2.D, r2.N
            sd = dot(sub(r.meio(), p.Core.A), p.Core.N); p.E = abs(sd) - (p.Core.Larg + r.Larg) / 2
        delta = p.F - p.E
        if abs(delta) > 0.05:
            p.Rev.W.move(mul(p.Core.N, p.Lado * delta)); n += 1 if abs(ang) <= 1e-5 else 0
    return n

def esticar(w, fim, alvo):
    c0, c1 = w.centre(); d = norm(sub(w.p1, w.p0))
    ce = c0 if fim == 0 else c1
    t = dot(sub(alvo, ce), d)
    if abs(t) < 0.05: return False
    if fim == 0: w.p0 = add(w.p0, mul(d, t))
    else: w.p1 = add(w.p1, mul(d, t))
    return True

def cruzar(p, d, q, e):
    c = cross(d, e)
    if abs(c) < 1e-9: return None
    t = cross(sub(q, p), e) / c
    return add(p, mul(d, t))

def cantos(walls, espMax, palavras, tol):
    infos = [Info(w, espMax, palavras) for w in walls]
    res, usados = [], set()
    for i in range(len(infos)):
        for j in range(i + 1, len(infos)):
            a, b = infos[i], infos[j]
            cr = cross(a.D, b.D)
            if abs(cr) < math.sin(math.radians(10)) or sobre_altura(a, b) < 30: continue
            x = cruzar(a.A, a.D, b.A, b.D)
            ta, tb = dot(sub(x, a.A), a.D), dot(sub(x, b.A), b.D)
            fa = 0 if abs(ta) < abs(ta - a.L) else 1
            fb = 0 if abs(tb) < abs(tb - b.L) else 1
            ea, eb = (a.A if fa == 0 else a.B), (b.A if fb == 0 else b.B)
            if dist(ea, x) > tol or dist(eb, x) > tol: continue
            if (a.L - ta if fa == 0 else ta) < a.L / 2 or (b.L - tb if fb == 0 else tb) < b.L / 2: continue
            if (a.W.id, fa) in usados or (b.W.id, fb) in usados: continue
            # targets: centre-line crossing when Revit cleans the join, else a butt joint (the longer runs through)
            if a.W.join[fa] and b.W.join[fb]:
                ta_, tb_ = x, x
            else:
                T, U, fT, fU = (a, b, fa, fb) if a.L >= b.L else (b, a, fb, fa)
                corpoT = sub((T.B if fT == 0 else T.A), x)
                corpoU = sub((U.B if fU == 0 else U.A), x)
                nU = (-U.D[1], U.D[0]); nT = (-T.D[1], T.D[0])
                sU = 1 if dot(nU, corpoT) < 0 else -1          # U's far face (away from T's body)
                sT = 1 if dot(nT, corpoU) > 0 else -1          # T's near face (towards U's body)
                alvoT = cruzar(T.A, T.D, add(U.A, mul(nU, sU * U.Larg / 2)), U.D)
                alvoU = cruzar(U.A, U.D, add(T.A, mul(nT, sT * T.Larg / 2)), T.D)
                if T is a: ta_, tb_ = alvoT, alvoU
                else: ta_, tb_ = alvoU, alvoT
            if dist(ea, ta_) < 0.1 and dist(eb, tb_) < 0.1: continue
            usados.add((a.W.id, fa)); usados.add((b.W.id, fb))
            res.append((a.W, fa, ta_, b.W, fb, tb_))
    for (wa, fa, xa, wb, fb, xb) in res:
        esticar(wa, fa, xa); esticar(wb, fb, xb)
    return len(res)

def emendas(pares, tol, espMax, palavras):
    """collinear pieces of the same layer (same core side and face): small gaps / overlaps closed at the middle."""
    grupos = {}
    for p in pares: grupos.setdefault((p.Core.W.id, p.Lado, round(p.F, 1)), []).append(p)
    n = 0
    for l in grupos.values():
        if len(l) < 2: continue
        core = l[0].Core
        itens = []
        for p in l:
            r = Info(p.Rev.W, espMax, palavras)
            t0, t1 = dot(sub(r.A, core.A), core.D), dot(sub(r.B, core.A), core.D)
            itens.append((min(t0, t1), max(t0, t1), p, r, 0 if t0 < t1 else 1))
        itens.sort(key=lambda x: x[0])
        for (a0, a1, pa, ra, fa_ini), (b0, b1, pb, rb, fb_ini) in zip(itens, itens[1:]):
            g = b0 - a1
            if abs(g) <= 0.1 or abs(g) > tol: continue
            if b1 <= a1: continue                                 # one inside the other: leave
            alt = sobre_altura(ra, rb) / max(1e-9, max(ra.Z1 - ra.Z0, rb.Z1 - rb.Z0))   # similar heights only
            if g < 0 and alt < 0.9: continue
            m = (a1 + b0) / 2
            alvo = add(core.A, mul(core.D, m))
            # the end of a at a1 is its index 1 - fa_ini; of b at b0 is fb_ini
            fimA = 1 - fa_ini; fimB = fb_ini
            # target on each centre line
            ca = cruzar(ra.A, ra.D, alvo, core.N); cb = cruzar(rb.A, rb.D, alvo, core.N)
            esticar(pa.Rev.W, fimA, ca); esticar(pb.Rev.W, fimB, cb); n += 1
    return n

def rodar(walls, espMax=60, palavras=(), tol=30, tolCanto=100):
    infos = [Info(w, espMax, palavras) for w in walls]
    pares = emparelhar(infos, tol)
    m = encostar(pares)
    c = cantos([p.Rev.W for p in pares], espMax, palavras, tolCanto)
    e = emendas(pares, tolCanto, espMax, palavras)
    return pares, m, c, e
