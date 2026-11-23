import math, random, sys
sys.path.insert(0, __import__('os').path.dirname(__file__))
import geo

ok = 0; fail = 0
def check(name, cond, info=""):
    global ok, fail
    if cond: ok += 1
    else: fail += 1; print("FALHOU:", name, info)

def tri_test(name, outer, holes, expected_area):
    tris, pts = geo.triangulate(outer, holes)
    a = geo.tri_area(pts, tris)
    neg = any(geo.cross2(geo.sub2(pts[tris[i+1]], pts[tris[i]]), geo.sub2(pts[tris[i+2]], pts[tris[i]])) < -1e-9 for i in range(0, len(tris), 3))
    check(name + " área", abs(a-expected_area) < 1e-6*max(1, expected_area), f"{a} != {expected_area} ({len(tris)//3} tri)")
    check(name + " orientação", not neg)

tri_test("quadrado", [(0,0),(1,0),(1,1),(0,1)], [], 1)
tri_test("quadrado horário", [(0,0),(0,1),(1,1),(1,0)], [], 1)
tri_test("L", [(0,0),(4,0),(4,1),(1,1),(1,3),(0,3)], [], 4+2)
tri_test("furo", [(0,0),(10,0),(10,10),(0,10)], [[(3,3),(7,3),(7,7),(3,7)]], 100-16)
tri_test("3 furos", [(0,0),(20,0),(20,10),(0,10)], [[(1,1),(4,1),(4,4),(1,4)], [(8,2),(12,2),(12,8),(8,8)], [(15,5),(18,5),(18,9),(15,9)]], 200-9-24-12)
tri_test("colineares", [(0,0),(1,0),(2,0),(3,0),(3,2),(1.5,2),(0,2)], [], 6)
tri_test("pente", [(0,0),(9,0),(9,1),(8,1),(8,5),(7,5),(7,1),(6,1),(6,5),(5,5),(5,1),(4,1),(4,5),(3,5),(3,1),(2,1),(2,5),(1,5),(1,1),(0,1)], [], 9+5*4)
# circle with circular hole (pipe section, many points)
N = 48
outer = [(math.cos(2*math.pi*i/N)*1.0, math.sin(2*math.pi*i/N)*1.0) for i in range(N)]
inner = [(math.cos(2*math.pi*i/N)*0.6, math.sin(2*math.pi*i/N)*0.6) for i in range(N)]
tri_test("tubo", outer, [inner], geo.area(outer) - geo.area(inner))
# profile with duplicated closing point
tri_test("fechamento repetido", [(0,0),(2,0),(2,2),(0,2),(0,0)], [], 4)
# hole touching nothing but close to edge
tri_test("furo perto da borda", [(0,0),(5,0),(5,3),(0,3)], [[(4.9,1),(4.95,1),(4.95,2),(4.9,2)]], 15-0.05)
# I-shape
b,h,tw,tf = 0.2,0.4,0.01,0.015
I = [(-b/2,-h/2),(b/2,-h/2),(b/2,-h/2+tf),(tw/2,-h/2+tf),(tw/2,h/2-tf),(b/2,h/2-tf),(b/2,h/2),(-b/2,h/2),(-b/2,h/2-tf),(-tw/2,h/2-tf),(-tw/2,-h/2+tf),(-b/2,-h/2+tf)]
tri_test("perfil I", I, [], 2*b*tf + (h-2*tf)*tw)
# random star polygons
random.seed(3)
for k in range(30):
    n = random.randint(5, 40)
    pts = []
    for i in range(n):
        a = 2*math.pi*i/n; r = random.uniform(0.3, 1.0)
        pts.append((r*math.cos(a), r*math.sin(a)))
    tri_test("estrela %d" % k, pts, [], abs(geo.area(pts)))
# random star with a small square hole at the centre
for k in range(10):
    n = random.randint(8, 30)
    pts = []
    for i in range(n):
        a = 2*math.pi*i/n; r = random.uniform(0.5, 1.0)
        pts.append((r*math.cos(a), r*math.sin(a)))
    hole = [(-0.2,-0.2),(0.2,-0.2),(0.2,0.2),(-0.2,0.2)]
    tri_test("estrela com furo %d" % k, pts, [hole], abs(geo.area(pts)) - 0.16)

# ------------------------------------------------------------------ CSG
cube = geo.box((0,0,0),(1,0,0),(0,1,0),(0,0,1))
check("volume cubo", abs(geo.volume(cube)-1) < 1e-9)
wall = geo.box((0,0,0),(5,0,0),(0,0.2,0),(0,0,3))          # 5 x 0.2 x 3 = 3
door = geo.box((1,-0.1,0),(0.9,0,0),(0,0.4,0),(0,0,2.1))   # through the wall: 0.9 x 0.2 x 2.1 = 0.378
r = geo.subtract(wall, door)
check("parede − porta", abs(geo.volume(r) - (3 - 0.378)) < 1e-6, geo.volume(r))
win = geo.box((3,-0.1,1),(1.2,0,0),(0,0.4,0),(0,0,1.2))    # 1.2*0.2*1.2 = 0.288
r2 = geo.subtract(r, win)
check("parede − porta − janela", abs(geo.volume(r2) - (3 - 0.378 - 0.288)) < 1e-6, geo.volume(r2))
far = geo.box((10,10,10),(1,0,0),(0,1,0),(0,0,1))
check("subtrair fora", abs(geo.volume(geo.subtract(cube, far)) - 1) < 1e-9)
half = geo.box((0.5,-1,-1),(0,2,0),(0,0,3),(5,0,0)) if False else geo.box((0.5,-1,-1),(5,0,0),(0,3,0),(0,0,3))
check("meio-espaço (x > 0,5)", abs(geo.volume(geo.subtract(cube, half)) - 0.5) < 1e-9)
check("interseção", abs(geo.volume(geo.intersect(cube, half)) - 0.5) < 1e-9)
c2 = geo.box((0.5,0.5,0.5),(1,0,0),(0,1,0),(0,0,1))
check("união", abs(geo.volume(geo.union(cube, c2)) - (2 - 0.125)) < 1e-9)
# recess (opening not through): pocket 0.1 deep
pocket = geo.box((2,-0.05,1),(0.5,0,0),(0,0.15,0),(0,0,0.5))   # overlaps wall 0.1 in y
check("nicho", abs(geo.volume(geo.subtract(wall, pocket)) - (3 - 0.5*0.1*0.5)) < 1e-6)
# rotated cube
def rot(p, a):
    c, s = math.cos(a), math.sin(a)
    return (c*p[0]-s*p[1], s*p[0]+c*p[1], p[2])
rc = [tuple(rot(p, 0.3) for p in t) for t in cube]
check("cubo girado − meio-espaço", abs(geo.volume(geo.subtract(rc, geo.box((-5,-5,0.5),(10,0,0),(0,10,0),(0,0,5)))) - 0.5) < 1e-9)
print(f"{ok} ok, {fail} falhas")
sys.exit(1 if fail else 0)
