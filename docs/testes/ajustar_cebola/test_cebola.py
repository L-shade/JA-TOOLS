import sys, math
import os; sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from cebola import *

ok = falhas = 0
def check(nome, cond, info=''):
    global ok, falhas
    if cond: ok += 1
    else: falhas += 1; print('FALHOU:', nome, info)

def cy(w): return w.centre()[0][1]
def cx(w): return w.centre()[0][0]
def near(a, b, t=0.01): return abs(a - b) <= t

def core_x(W=140, key='C', layers=None, flip=False, x0=0, x1=5000, y=0):
    return Wall((x0, y), (x1, y), W, key, layers, flip)

# 1-2 gap / inside
for y, nome in ((90, 'afastado 10'), (75, 'dentro 5'), (80, 'já certo')):
    c = core_x(); f = Wall((100, y), (4900, y), 20)
    pares, m, _, _ = rodar([c, f])
    check(nome, near(cy(f), 80), cy(f))
    check(nome + ' movidos', m == (0 if y == 80 else 1), m)

# 3 other side, flipped finish
c = core_x(); f = Wall((4900, -95), (100, -95), 20, flip=True)
rodar([c, f]); check('lado de baixo, revestimento invertido', near(cy(f), -80), cy(f))

# 4 finish located by its exterior face
c = core_x(); f = Wall((100, 100), (4900, 100), 20, key='FE')     # ori +y, location = exterior face, centre 90
rodar([c, f]); check('revestimento pela face externa', near(cy(f), 80) and near(f.p0[1], 90), (cy(f), f.p0))

# 5 core located by its exterior face
c = core_x(key='FE', y=70); f = Wall((100, 88), (4900, 88), 20)
rodar([c, f]); check('núcleo pela face externa', near(cy(f), 80), cy(f))

# 6 core centre-line with asymmetric layers
c = core_x(key='CC', layers=[(30, False), (100, True), (10, False)], y=-10)
check('desvio do núcleo assimétrico', near(c.centre()[0][1], 0), c.centre())
f = Wall((100, 85), (4900, 85), 20)
rodar([c, f]); check('núcleo pelo eixo do núcleo', near(cy(f), 80), cy(f))

# 7 plaster + tile, both off
c = core_x(); pl = Wall((100, 92), (4900, 92), 20, name='Reboco'); tl = Wall((100, 100), (4900, 100), 10, name='Cerâmica')
pares, m, _, _ = rodar([c, pl, tl])
check('reboco', near(cy(pl), 80), cy(pl)); check('cerâmica sobre reboco', near(cy(tl), 95), cy(tl))
check('cerâmica presa no reboco', [p for p in pares if p.Rev.W is tl][0].Base.W is pl)

# 8 tile entering the plaster
c = core_x(); pl = Wall((100, 80), (4900, 80), 20); tl = Wall((100, 88), (4900, 88), 10)
rodar([c, pl, tl]); check('cerâmica entrando no reboco', near(cy(tl), 95) and near(cy(pl), 80), (cy(pl), cy(tl)))

# 9 two equal layers
c = core_x(); a = Wall((100, 82), (4900, 82), 20); b = Wall((100, 99), (4900, 99), 20)
rodar([c, a, b]); check('duas camadas iguais', near(cy(a), 80) and near(cy(b), 100), (cy(a), cy(b)))

# 10 finish over two collinear walls
c1 = core_x(x1=2500); c2 = core_x(x0=2500); f = Wall((100, 90), (4900, 90), 20)
pares, *_ = rodar([c1, c2, f]); check('sobre duas paredes', near(cy(f), 80), cy(f))

# 11 lone thin partition stays; tile on it goes onto it
c = core_x(); dw = Wall((0, 2000), (5000, 2000), 50); tl = Wall((100, 2035), (4900, 2035), 10)
pares, m, _, _ = rodar([c, dw, tl])
check('divisória fina não mexe', near(cy(dw), 2000), cy(dw)); check('cerâmica na divisória', near(cy(tl), 2030), cy(tl))

# 12 nothing near: untouched
c = core_x(); f = Wall((100, 130), (4900, 130), 20)
pares, m, _, _ = rodar([c, f]); check('longe demais', near(cy(f), 130) and m == 0, cy(f))

# corners: L with inner and outer finishes
def L(join):
    A = core_x(x0=0, x1=5000); B = Wall((0, 0), (0, 5000), 140, flip=True)      # B ori = +x
    ia = Wall((95, 80), (5000, 80), 20, join=join)                       # inner A, start short of the corner
    ib = Wall((80, 5000), (80, 110), 20, flip=True, join=join)           # inner B, ends short (runs -y)
    oa = Wall((5000, -80), (-60, -80), 20, flip=True, join=join)         # outer A
    ob = Wall((-80, -50), (-80, 5000), 20, flip=True, join=join)         # outer B (ori -x)
    return A, B, ia, ib, oa, ob
A, B, ia, ib, oa, ob = L((True, True))
pares, m, c, e = rodar([A, B, ia, ib, oa, ob])
check('L: 4 revestimentos achados', len(pares) == 4, len(pares))
check('L junção: interno A no cruzamento', near(min(ia.centre()[0][0], ia.centre()[1][0]), 80), ia.centre())
check('L junção: interno B no cruzamento', near(min(ib.centre()[0][1], ib.centre()[1][1]), 80), ib.centre())
check('L junção: externo A', near(min(oa.centre()[0][0], oa.centre()[1][0]), -80), oa.centre())
check('L junção: externo B', near(min(ob.centre()[0][1], ob.centre()[1][1]), -80), ob.centre())
check('L junção: 2 cantos', c == 2, c)

A, B, ia, ib, oa, ob = L((False, False))
pares, m, c, e = rodar([A, B, ia, ib, oa, ob])
# butt: the longer (A pieces) runs through
check('L topo: interno A até a face do núcleo B', near(min(ia.centre()[0][0], ia.centre()[1][0]), 70), ia.centre())
check('L topo: interno B encosta no A', near(min(ib.centre()[0][1], ib.centre()[1][1]), 90), ib.centre())
check('L topo: externo A passa por fora', near(min(oa.centre()[0][0], oa.centre()[1][0]), -90), oa.centre())
check('L topo: externo B encosta no A', near(min(ob.centre()[0][1], ob.centre()[1][1]), -70), ob.centre())

# corner too far / T junction
A = core_x(); B = Wall((0, 0), (0, 5000), 140, flip=True)
ia = Wall((300, 80), (5000, 80), 20); ib = Wall((80, 5000), (80, 300), 20, flip=True)
_, _, c, _ = rodar([A, B, ia, ib]); check('canto longe demais', c == 0, c)
A = core_x(); B = Wall((2500, 0), (2500, 5000), 140, flip=True)
fa = Wall((100, 80), (4900, 80), 20); fb = Wall((2580, 5000), (2580, 100), 20, flip=True)
_, _, c, _ = rodar([A, B, fa, fb]); check('T não é canto', c == 0, c)

# seams
c0 = core_x(); a = Wall((100, 80), (2000, 80), 20); b = Wall((2010, 80), (4000, 80), 20)
_, _, _, e = rodar([c0, a, b])
check('emenda com fresta', e == 1 and near(a.p1[0], 2005) and near(b.p0[0], 2005), (a.p1, b.p0))
c0 = core_x(); a = Wall((100, 80), (2010, 80), 20); b = Wall((2000, 80), (4000, 80), 20)
_, _, _, e = rodar([c0, a, b]); check('emenda sobreposta', e == 1 and near(a.p1[0], 2005) and near(b.p0[0], 2005), (a.p1, b.p0))
c0 = core_x(); a = Wall((100, 80), (2010, 80), 20, z1=1200); b = Wall((2000, 80), (4000, 80), 20)
_, _, _, e = rodar([c0, a, b]); check('alturas diferentes sobrepostas não mexem', e == 0, e)
c0 = core_x(); a = Wall((100, 80), (2000, 80), 20); b = Wall((4000, 80), (2010, 80), 20, flip=True)
_, _, _, e = rodar([c0, a, b]); check('emenda com peça invertida', e == 1 and near(a.p1[0], 2005) and near(b.p1[0], 2005), (a.p1, b.p1))

# mixed corner (inner of one, outer of the other): nothing
A = core_x(); B = Wall((0, 0), (0, 5000), 140, flip=True)
ia = Wall((95, 80), (5000, 80), 20); ob = Wall((-80, -50), (-80, 5000), 20, flip=True)
_, _, c, _ = rodar([A, B, ia, ob]); check('interno × externo não fecha', c == 0, c)

# oblique corner (60°)
A = core_x(); ang = math.radians(60); d = (math.cos(ang), math.sin(ang)); n = (-d[1], d[0])
B = Wall((0, 0), (5000 * d[0], 5000 * d[1]), 140, flip=True)                 # ori = right normal
ia = Wall((200, 80), (5000, 80), 20)
off = mul(n, -80)
ib = Wall(add(mul(d, 5000), off), add(mul(d, 200), off), 20, flip=True)
pares, m, c, _ = rodar([A, B, ia, ib])
x = cruzar(Info(ia, 60, ()).A, (1, 0), off, d)
check('canto oblíquo fechado', c == 1 and dist(ia.centre()[0], x) < 0.05, (c, ia.centre()[0], x))

# slightly turned finish
c = core_x(); f = Wall((100, 85), (4900, 95), 20)
pares, m, _, _ = rodar([c, f]); a0, a1 = f.centre()
check('revestimento torto fica paralelo e encostado', near(a0[1], 80) and near(a1[1], 80) and m == 1, (a0, a1))
c = core_x(); f = Wall((4900, 95), (100, 85), 20, flip=True)
pares, m, _, _ = rodar([c, f]); a0, a1 = f.centre()
check('torto e invertido', near(a0[1], 80) and near(a1[1], 80), (a0, a1))
# at the centre line of the wall: ambiguous, left alone
c = core_x(); f = Wall((100, 0.5), (4900, 0.5), 20)
pares, m, _, _ = rodar([c, f]); check('no eixo da parede: não mexe', m == 0 and near(cy(f), 0.5), cy(f))
# between two walls 40 mm apart: goes to the nearer
c1 = core_x(); c2 = core_x(y=210); f = Wall((100, 92), (4900, 92), 20)
pares, m, _, _ = rodar([c1, c2, f]); check('entre duas paredes vai para a mais próxima', near(cy(f), 80), cy(f))
# plaster too far (not moved) still carries its tile
c = core_x(); pl = Wall((100, 120), (4900, 120), 20); tl = Wall((100, 135), (4900, 135), 10)
pares, m, _, _ = rodar([c, pl, tl]); check('reboco longe fica; cerâmica no reboco', near(cy(pl), 120) and near(cy(tl), 135) and m == 0, (cy(pl), cy(tl)))

print(f'{ok} ok, {falhas} falha(s)')
