"""Quinas que não fechavam (18/11/2026): plantas giradas, faces que quase se encontram, móveis girados.
python3 test_quinas.py — compara 'nova' (1.29/2.0) × 'v2' (2.0.2)."""
import sys, os, math
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from rodape import *
E = 15
ANG = 37.0
def gira(p, ang=ANG, c=(0, 0)):
    a = math.radians(ang); x, y = p[0] - c[0], p[1] - c[1]
    return (c[0] + x * math.cos(a) - y * math.sin(a), c[1] + x * math.sin(a) + y * math.cos(a))
def paredes(pts, W=140):
    laco = []
    for i in range(len(pts)):
        a, b = pts[i], pts[(i + 1) % len(pts)]
        if dist(a, b) < 1e-9: continue
        d = norm(sub(b, a)); fora = mul(left(d), -W / 2)
        laco.append((a, b, Wall(add(a, fora), add(b, fora), W)))
    return laco
def rodar(laco, v, moveis=(), **kw):
    k = Ctx(Cfg(**kw), moveis=moveis)
    return colocar_laco(k, laco, 0, v)
def buraco(pecas, quina, d1, d2):
    """the corner square (stone thickness) of an outer corner at quina between directions d1 (incoming) and d2 is covered"""
    # points just inside the stone band on both sides of the corner and at the corner itself
    n1, n2 = left(d1), left(d2)
    pts = [add(quina, add(mul(n1, E * 0.5), mul(n2, E * 0.5))), add(quina, mul(n1, E * 0.5)), add(quina, mul(n2, E * 0.5))]
    return [q for q in pts if not coberto(pecas, E, q)]
res = {}
def ck(v, nome, cond, info=''): res.setdefault(v, []).append((nome, bool(cond), info))

L_ROOM = [(0, 0), (4000, 0), (4000, 2000), (2000, 2000), (2000, 4000), (0, 4000)]
for v in ('nova', 'v2'):
    for ang in (0, ANG):
        g = lambda p: gira(p, ang)
        # 1. plain L room
        pts = [g(p) for p in L_ROOM]
        pecas, t = rodar(paredes(pts), v)
        d1, d2 = norm(sub(pts[3], pts[2])), norm(sub(pts[4], pts[3]))
        ck(v, f'L {ang:.0f}°: quina externa fechada', not buraco(pecas, pts[3], d1, d2))
        ck(v, f'L {ang:.0f}°: sem sobreposição', sobreposicao(pecas, E) < 1, f'{sobreposicao(pecas, E):.0f}')
        # 2. outer corner with a micro step: the faces of the two walls miss by a few mm
        for nome, meio in (('degrau 6 mm', [(2008, 2000), (2008, 2006), (2000, 2006)]),
                           ('chanfro 5 mm', [(2005, 2000), (2000, 2005)]),
                           ('ponta de 4 mm', [(1996, 2000), (1996, 2004), (2000, 2004)])):
            base = L_ROOM[:3] + meio + L_ROOM[4:]
            pts = [g(p) for p in base]
            laco = paredes(pts)
            pecas, t = rodar(laco, v)
            q = g((2000, 2000)); d1 = norm(sub(g((2000, 2000)), g((4000, 2000)))); d2 = norm(sub(g((2000, 4000)), g((2000, 2000))))
            falta = buraco(pecas, q, d1, d2)
            ck(v, f'L {ang:.0f}° com {nome} na quina: quina fechada', not falta, falta)
            ck(v, f'L {ang:.0f}° com {nome}: sobreposição < 1 cm²', sobreposicao(pecas, E) < 100, f'{sobreposicao(pecas, E):.0f}')
        # 2b. the loop starts at the micro stretch
        base = [(2005, 2000), (2000, 2005), (2000, 4000), (0, 4000), (0, 0), (4000, 0), (4000, 2000)]
        pts = [g(p) for p in base]
        pecas, t = rodar(paredes(pts), v)
        q = g((2000, 2000)); d1 = norm(sub(g((2000, 2000)), g((4000, 2000)))); d2 = norm(sub(g((2000, 4000)), g((2000, 2000))))
        ck(v, f'L {ang:.0f}°, laço começando no chanfro de 5 mm: quina fechada', not buraco(pecas, q, d1, d2))
        ck(v, f'L {ang:.0f}°, laço começando no chanfro: sobreposição < 1 cm²', sobreposicao(pecas, E) < 100, f'{sobreposicao(pecas, E):.0f}')
        # 3. the two boundary segments do not touch (3 mm apart)
        pts = [g(p) for p in L_ROOM]
        laco = paredes(pts)
        a3, b3, w3 = laco[3]
        laco[3] = (add(a3, mul(norm(sub(b3, a3)), 3)), b3, w3)
        pecas, t = rodar(laco, v)
        q = pts[3]; d1 = norm(sub(pts[3], pts[2])); d2 = norm(sub(pts[4], pts[3]))
        ck(v, f'L {ang:.0f}°, segmentos separados 3 mm: quina fechada', not buraco(pecas, q, d1, d2))
        # 4. inner corner with a micro chamfer: no double stone, no hole
        base = [(0, 0), (3995, 0), (4000, 5), (4000, 3000), (0, 3000)]
        pts = [g(p) for p in base]
        pecas, t = rodar(paredes(pts), v)
        ov = sobreposicao(pecas, E)
        ck(v, f'retângulo {ang:.0f}° com chanfro 5 mm no canto interno: sobreposição < 1 cm²', ov < 100, f'{ov:.0f}')
        ck(v, f'retângulo {ang:.0f}° com chanfro 5 mm: canto coberto', coberto(pecas, E, g((3990, 10))))
        # 5. a sofa turned with the room, 30 cm from the corner: its AABB must not eat the rodapé to the corner
        pts = [g(p) for p in [(0, 0), (5000, 0), (5000, 4000), (0, 4000)]]
        sofa = Box.girado(g((1500, 4000 - 450 - 20)), 2000, 900, ang)
        pecas, t = rodar(paredes(pts), v, moveis=[sofa])
        ck(v, f'sala {ang:.0f}° com sofá a 20 mm da parede: desconto = comprimento do sofá', abs(t.Moveis - 2000) < 5, f'{t.Moveis:.0f} mm')
        ck(v, f'sala {ang:.0f}° com sofá: canto (0, 4000) coberto', coberto(pecas, E, g((7, 3993))))
for v in res:
    f = [x for x in res[v] if not x[1]]
    print(f'== {v}: {len(res[v]) - len(f)} ok, {len(f)} falha(s)')
    for nome, _, info in f: print('   FALHOU:', nome, info)

# ---- 6. outer corner of a wall split by a room separation line (CIRC. | SALA): the corner square has an owner
def dentro(poly, p):
    x, y = p; inside = False
    for i in range(len(poly)):
        (x1, y1), (x2, y2) = poly[i], poly[(i + 1) % len(poly)]
        if (y1 > y) != (y2 > y) and x < (x2 - x1) * (y - y1) / (y2 - y1) + x1: inside = not inside
    return inside
res2 = {}
for v in ('nova', 'v2'):
    for ang in (0, ANG):
        g = lambda p: gira(p, ang)
        W = Wall((0, 0), (1, 0), 140)
        PA = [(-3000, 0), (0, 0), (0, 3000), (-3000, 3000)]
        PB = [(0, 3000), (0, 0), (0, -3000), (3000, -3000), (3000, 3000)]
        A = [(g(PA[0]), g(PA[1]), W), (g(PA[1]), g(PA[2]), None), (g(PA[2]), g(PA[3]), W), (g(PA[3]), g(PA[0]), W)]
        B = [(g(PB[i]), g(PB[(i + 1) % 5]), None if i == 0 else W) for i in range(5)]
        for nome, sala_a, sala_b in (('os dois ambientes juntos', True, True), ('só a circulação', False, True), ('só a sala', True, False)):
            todos = [PA] * sala_a + [PB] * sala_b
            def viz_de(proprio):
                return lambda p: any(dentro([g(q) for q in poly], p) for poly in todos if poly is not proprio)
            pecas = []
            if sala_a: pecas += colocar_laco(Ctx(Cfg()), A, 0, v, vizinho=viz_de(PA))[0]
            if sala_b: pecas += colocar_laco(Ctx(Cfg()), B, 0, v, vizinho=viz_de(PB))[0]
            sq = g((7, 7)); fora = g((-7, -7))
            if sala_a and sala_b:
                res2.setdefault(v, []).append((f'{ang:.0f}° linha de ambiente na quina, {nome}: quadrado coberto', coberto(pecas, E, sq), ''))
                res2[v].append((f'{ang:.0f}° {nome}: sem sobreposição', sobreposicao(pecas, E) < 1, f'{sobreposicao(pecas, E):.0f}'))
            else:
                res2.setdefault(v, []).append((f'{ang:.0f}° {nome}: nada sobra para o outro ambiente', not coberto(pecas, E, sq), ''))
for v in res2:
    f = [x for x in res2[v] if not x[1]]
    print(f'== {v} (linha de ambiente): {len(res2[v]) - len(f)} ok, {len(f)} falha(s)')
    for nome, _, info in f: print('   FALHOU:', nome, info)
# CI: the current engine is v2
import sys
sys.exit(1 if any(not x[1] for x in res.get('v2', []) + res2.get('v2', [])) else 0)
