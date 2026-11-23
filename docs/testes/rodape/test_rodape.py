import sys, os, math
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from rodape import *

E = 15
res = {'atual': [], 'nova': []}
def check(v, nome, cond, info=''):
    res[v].append((nome, bool(cond), info))

def paredes_do_poligono(pts, W=140):
    """walls around a CCW room polygon (faces on the polygon), the loop with the room on the left."""
    laco = []
    n = len(pts)
    for i in range(n):
        a, b = pts[i], pts[(i + 1) % n]
        d = norm(sub(b, a)); fora = mul(left(d), -W / 2)
        w = Wall(add(a, fora), add(b, fora), W)
        laco.append((a, b, w))
    return laco

def rodar(laco, versao, **kw):
    cfg = Cfg(**{k: v for k, v in kw.items() if k in Cfg().__dict__})
    k = Ctx(cfg, kw.get('portas', ()), kw.get('janelas', ()), kw.get('aberturas', ()), kw.get('moveis', ()))
    return colocar_laco(k, laco, 0, versao, fechado=kw.get('fechado', True), livre=kw.get('livre', False))

for v in ('atual', 'nova'):
    # A. rectangle 4 x 3 m
    laco = paredes_do_poligono([(0, 0), (4000, 0), (4000, 3000), (0, 3000)])
    pecas, t = rodar(laco, v)
    check(v, 'retângulo: sem sobreposição nos cantos internos', sobreposicao(pecas, E) < 1, f'{sobreposicao(pecas, E):.0f} mm²')
    check(v, 'retângulo: canto coberto', coberto(pecas, E, (7, 7)))
    check(v, 'retângulo: metros = perímetro − 4 × espessura', abs(t.Liquido - (14000 - 4 * E)) < 1, t.Liquido)

    # B. pillar in the middle (inner loop, clockwise)
    pil = [(2000, 1000), (2000, 1300), (2300, 1300), (2300, 1000)]
    col = Column()
    laco = [(pil[i], pil[(i + 1) % 4], col) for i in range(4)]
    pecas, t = rodar(laco, v)
    check(v, 'pilar: sem sobreposição', sobreposicao(pecas, E) < 1, f'{sobreposicao(pecas, E):.0f} mm²')
    check(v, 'pilar: quinas cobertas', all(coberto(pecas, E, p) for p in [(1993, 993), (1993, 1307), (2307, 1307), (2307, 993)]))
    check(v, 'pilar: 1,20 m + 4 quinas', abs(t.Liquido - (1200 + 4 * E)) < 1, t.Liquido)

    # C. L-shaped room (one convex wall corner inside the room)
    laco = paredes_do_poligono([(0, 0), (4000, 0), (4000, 2000), (2000, 2000), (2000, 4000), (0, 4000)])
    pecas, t = rodar(laco, v)
    check(v, 'L: sem sobreposição', sobreposicao(pecas, E) < 1, f'{sobreposicao(pecas, E):.0f} mm²')
    check(v, 'L: quina externa coberta', coberto(pecas, E, (1992, 1992)))

    # D. chamfered corner (45°)
    laco = paredes_do_poligono([(0, 0), (4000, 0), (4000, 2500), (3500, 3000), (0, 3000)])
    pecas, t = rodar(laco, v)
    ov = sobreposicao(pecas, E)
    check(v, 'chanfro 45°: sobreposição pequena (< 1 cm²)', ov < 100, f'{ov:.0f} mm²')

    # E. door in a parede cebola: door in the core wall, the room bounded by the finish wall
    core = Wall((-200, -90), (4200, -90), 140); fin = Wall((0, -10), (4000, -10), 20)
    laco = paredes_do_poligono([(0, 0), (4000, 0), (4000, 3000), (0, 3000)]); laco[0] = ((0, 0), (4000, 0), fin)
    porta = Door(core, (2000, -90), 800)
    pecas, t = rodar(laco, v, portas=[porta])
    check(v, 'parede cebola: porta na parede de trás é descontada', abs(t.Portas - 800) < 1, t.Portas)

    # F. opening with return: the jamb corner covered, the return as deep as the wall
    laco = paredes_do_poligono([(0, 0), (4000, 0), (4000, 3000), (0, 3000)])
    ab = Opening(laco[0][2], (1500, -140), (2500, 0))
    pecas, t = rodar(laco, v, aberturas=[ab], Retorno=True)
    check(v, 'vão com retorno: quina do batente coberta', coberto(pecas, E, (1507, 7)) and coberto(pecas, E, (2493, 7)))
    check(v, 'vão com retorno: retornos de 7 cm', abs(t.Retornos - 140) < 1, t.Retornos)
    laco[0] = ((0, 0), (4000, 0), fin)
    ab2 = Opening(core, (1500, -160), (2500, -20))
    pecas, t = rodar(laco, v, aberturas=[ab2], Retorno=True)
    check(v, 'vão em parede cebola: retorno até o meio da parede (9 cm)', abs(t.Retornos - 180) < 1 and abs(t.Vaos - 1000) < 1, (t.Retornos, t.Vaos))

    # G. windows
    laco = paredes_do_poligono([(0, 0), (4000, 0), (4000, 3000), (0, 3000)])
    j1 = Door(laco[0][2], (1000, -70), 1200, zmin=0); j2 = Door(laco[0][2], (3000, -70), 800, zmin=1000)
    pecas, t = rodar(laco, v, janelas=[j1, j2])
    check(v, 'janela até o piso descontada, a alta não', abs(t.Janelas - 1200) < 1, t.Janelas)

    # H. furniture
    pecas, t = rodar(laco, v, moveis=[Box(500, 2400, 1100, 3000), Box(2000, 2700, 2600, 3000, z0=1500, z1=2200)])
    check(v, 'armário no piso descontado, aéreo não', abs(t.Moveis - 600) < 1, t.Moveis)

    # I. curtain wall on one side: neighbours run to the corner
    laco = paredes_do_poligono([(0, 0), (4000, 0), (4000, 3000), (0, 3000)])
    laco[1][2].curtain = True
    pecas, t = rodar(laco, v)
    ok_fim = any(abs(q[0] - 4000) < 0.5 and abs(q[1]) < 0.5 for (p, q) in pecas)
    ok_ini = any(abs(p[0] - 4000) < 0.5 and abs(p[1] - 3000) < 0.5 for (p, q) in pecas)
    check(v, 'pele de vidro: vizinhos vão até o canto', ok_fim and ok_ini and abs(t.Cortina - 3000) < 1, (ok_fim, ok_ini))

    # J. door close to a corner: a 5 cm piece dropped, the other wall runs to the corner
    pecas, t = rodar(laco, v, portas=[Door(paredes_do_poligono([(0, 0), (4000, 0), (4000, 3000), (0, 3000)])[0][2], (450, -70), 800)])
    # the door host must be the wall of the stretch: rebuild
    laco = paredes_do_poligono([(0, 0), (4000, 0), (4000, 3000), (0, 3000)])
    pecas, t = rodar(laco, v, portas=[Door(laco[0][2], (450, -70), 800)])
    check(v, 'porta junto do canto: pedaço de 5 cm fora', abs(t.Curtos - 50) < 1, t.Curtos)
    check(v, 'porta junto do canto: a outra parede vai até o canto', any(abs(q[0]) < 0.5 and abs(q[1]) < 0.5 for (p, q) in pecas))

    # K. curved wall tessellated in short chords (radius 300 mm)
    arco = [(2000 + 300 * math.cos(t_), 3000 - 300 + 300 * math.sin(t_)) for t_ in [math.pi * i / 24 for i in range(25)]]
    pts = [(0, 0), (4000, 0), (4000, 3000)] + [(1700 + 600 - (p[0] - 1700), p[1]) for p in []]
    w = Wall((0, 0), (1, 0), 140)
    laco = paredes_do_poligono([(0, 0), (4000, 0), (4000, 3000), (2300, 3000)])
    laco = laco[:3]
    curva = [(2300 - 300 + 300 * math.cos(math.pi * i / 24), 3000 + 300 * math.sin(math.pi * i / 24)) for i in range(25)]
    for i in range(24): laco.append((curva[i], curva[i + 1], w))
    laco += paredes_do_poligono([(0, 0), (4000, 0), (4000, 3000), (1700, 3000), (0, 3000)])[3:]
    pecas, t = rodar(laco, v)
    corda = dist(curva[0], curva[1])
    check(v, f'parede curva em cordas de {corda:.0f} mm: sem buracos', t.Curtos < 1, f'curtos {t.Curtos:.0f} mm')

    # L. outer corner next to a tiled wall: no piece sticks out into it
    laco = paredes_do_poligono([(0, 0), (4000, 0), (4000, 2000), (2000, 2000), (2000, 4000), (0, 4000)])
    laco[3][2].name = 'Revestimento cerâmica'
    pecas, t = rodar(laco, v)
    passa = any(min(p[0], q[0]) < 2000 - 1 and abs(p[1] - 2000) < 0.5 and abs(q[1] - 2000) < 0.5 for (p, q) in pecas)
    check(v, 'quina com parede revestida: não avança sobre ela', not passa)

# ---- Acabamento de base
casos = {
    'Granito branco h 100 mm': ('Granito branco', 100, 15),
    'Poliestireno h 10 cm × 15': ('Poliestireno', 100, 15),
    'Poliestireno h 10 cm x 12': ('Poliestireno', 100, 12),
    'MDF - h=70 e=15': ('MDF', 70, 15),
    'Porcelanato h70': ('Porcelanato', 70, 15),
    'Granito Hidra h 80 mm': ('Granito Hidra', 80, 15),
    'Mármore Branco Piguês H=10cm': ('Mármore Branco Piguês', 100, 15),
    'Granito cinza andorinha h 70 mm': ('Granito cinza andorinha', 70, 15),
    'Granito cinza h 70,5 mm': ('Granito cinza', 70.5, 15),
    'Rodapé de madeira': None,
    'h 7 cm': None,
    'Madeira h 2 mm': None,
    'Rodapé poliestireno 10 cm': ('Rodapé poliestireno', 100, 15),
    'Granito h.10cm': ('Granito', 100, 15),
    'Rodapé em granito, altura 7 cm': ('Rodapé em granito', 70, 15),
}
for v in ('atual', 'nova'):
    rx = RE_ACAB if v == 'atual' else RE_ACAB_NOVA
    for s, esperado in casos.items():
        r = ler_acabamento(s, regex=rx)
        ok = (r is None and esperado is None) or (r is not None and esperado is not None and r[0] == esperado[0] and abs(r[1] - esperado[1]) < 1e-6 and abs(r[2] - esperado[2]) < 1e-6)
        check(v, f'acabamento “{s}”', ok, r)

for v in ('atual', 'nova'):
    falhas = [x for x in res[v] if not x[1]]
    print(f'== {v}: {len(res[v]) - len(falhas)} ok, {len(falhas)} falha(s)')
    for nome, _, info in falhas: print('   FALHOU:', nome, info)

# ---- Rodapé no Local: the drawing as chains
r = []
def ck(nome, cond, info=''): r.append((nome, bool(cond), info))
# top and bottom edges of two wall faces (picked with Bordas), the vertical edges, in any order
segs = [((0, 0, 0), (4000, 0, 0)), ((0, 0, 2800), (4000, 0, 2800)), ((4000, 0, 0), (4000, 0, 2800)),
        ((4000, 3000, 0), (4000, 0, 0)), ((4000, 0, 2800), (4000, 3000, 2800))]
c = cadeias(segs)
ck('bordas: uma cadeia de 3 pontos', len(c) == 1 and len(c[0][0]) == 3 and not c[0][1], c)
# rectangle drawn as 4 lines, two of them reversed
segs = [((0, 0, 0), (4000, 0, 0)), ((4000, 3000, 0), (4000, 0, 0)), ((4000, 3000, 0), (0, 3000, 0)), ((0, 0, 0), (0, 3000, 0))]
c = cadeias(segs)
ck('retângulo com linhas invertidas: fechado, 4 pontos', len(c) == 1 and c[0][1] and len(c[0][0]) == 4, c)
# two separate stretches
segs = [((0, 0, 0), (1000, 0, 0)), ((3000, 0, 0), (4000, 0, 0))]
ck('dois trechos soltos: duas cadeias', len(cadeias(segs)) == 2)
# drawn rectangle placed with the corners of the room rodapé engine (closed chain, stone inside)
for v in ('atual', 'nova'):
    pts = [(0, 0), (4000, 0), (4000, 3000), (0, 3000)]
    laco = [(pts[i], pts[(i + 1) % 4], None) for i in range(4)]
    k = Ctx(Cfg())
    pecas, t = colocar_laco(k, laco, 0, v, fechado=True, livre=True)
    ck(f'{v}: retângulo desenhado sem sobreposição', sobreposicao(pecas, E) < 1, sobreposicao(pecas, E))
    laco = [(pts[i], pts[i + 1], None) for i in range(3)]
    pecas, t = colocar_laco(k, laco, 0, v, fechado=False, livre=True)
    ck(f'{v}: U aberto: pontas soltas não crescem', abs(pecas[0][0][0]) < 0.5 and dist(pecas[-1][1], (0, 3000)) < 0.5, pecas)
fal = [x for x in r if not x[1]]
print(f'== no local: {len(r) - len(fal)} ok, {len(fal)} falha(s)')
for nome, _, info in fal: print('   FALHOU:', nome, info)
# CI: the current engine is 'nova' ('atual' = the old one, kept for comparison)
import sys
sys.exit(1 if any(not x[1] for x in res['nova']) or any(not x[1] and not x[0].startswith('atual') for x in r) else 0)
