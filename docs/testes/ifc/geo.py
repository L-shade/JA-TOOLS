"""Espelho em Python de Triangulator.cs e Csg.cs (JA Visualizador IFC) para testar os algoritmos sem compilar."""
import math

EPS = 1e-12

def cross2(a, b): return a[0]*b[1]-a[1]*b[0]
def sub2(a, b): return (a[0]-b[0], a[1]-b[1])
def dist2(a, b): return (a[0]-b[0])**2+(a[1]-b[1])**2

def area_idx(pts, idx):
    a = 0
    for i in range(len(idx)):
        p = pts[idx[i]]; q = pts[idx[(i+1) % len(idx)]]
        a += p[0]*q[1]-q[0]*p[1]
    return a*0.5

def area(loop):
    return area_idx(loop, list(range(len(loop))))

def clean(loop, pts, offset):
    while len(pts) < offset: pts.append((0, 0))
    pts.extend(loop)
    ext = max([max(abs(p[0]), abs(p[1])) for p in loop] + [0])
    tol2 = max(1e-12, ext*1e-10)**2
    idx = []
    for i in range(len(loop)):
        k = offset+i
        if idx and dist2(pts[idx[-1]], pts[k]) <= tol2: continue
        idx.append(k)
    while len(idx) > 1 and dist2(pts[idx[0]], pts[idx[-1]]) <= tol2: idx.pop()
    return idx

def extent(pts, idx):
    xs = [pts[i][0] for i in idx]; ys = [pts[i][1] for i in idx]
    return max(max(xs)-min(xs), max(ys)-min(ys))

def maxx(pts, loop): return max(pts[i][0] for i in loop)

def in_cone(pts, poly, i, m):
    n = len(poly)
    a = pts[poly[(i-1+n) % n]]; b = pts[poly[i]]; c = pts[poly[(i+1) % n]]
    turn = cross2(sub2(b, a), sub2(c, b))
    s1 = cross2(sub2(b, a), sub2(m, a)); s2 = cross2(sub2(c, b), sub2(m, b))
    if turn >= 0: return s1 >= -EPS and s2 >= -EPS
    return s1 >= -EPS or s2 >= -EPS

def seg_cross(p1, p2, q1, q2):
    d1 = cross2(sub2(p2, p1), sub2(q1, p1)); d2 = cross2(sub2(p2, p1), sub2(q2, p1))
    d3 = cross2(sub2(q2, q1), sub2(p1, q1)); d4 = cross2(sub2(q2, q1), sub2(p2, q1))
    return ((d1 > EPS and d2 < -EPS) or (d1 < -EPS and d2 > EPS)) and ((d3 > EPS and d4 < -EPS) or (d3 < -EPS and d4 > EPS))

def crosses_any(pts, loop, p, q, skipA, skipB):
    n = len(loop)
    for i in range(n):
        ia = loop[i]; ib = loop[(i+1) % n]
        if ia in (skipA, skipB) or ib in (skipA, skipB): continue
        a = pts[ia]; b = pts[ib]
        if dist2(a, p) < 1e-30 or dist2(b, p) < 1e-30 or dist2(a, q) < 1e-30 or dist2(b, q) < 1e-30: continue
        if seg_cross(p, q, a, b): return True
    return False

def bridge(pts, poly, hole, holes, nexthole):
    hm = max(range(len(hole)), key=lambda i: (pts[hole[i]][0], -i))
    # C#: first index with the max (strict >)
    hm = 0
    for i in range(1, len(hole)):
        if pts[hole[i]][0] > pts[hole[hm]][0]: hm = i
    m = pts[hole[hm]]
    order = sorted(range(len(poly)), key=lambda a: dist2(pts[poly[a]], m))
    best = -1; tries = 0
    for pi in order:
        tries += 1
        if tries > 400: break
        p = pts[poly[pi]]
        if dist2(p, m) < 1e-30: best = pi; break
        if not in_cone(pts, poly, pi, m): continue
        if crosses_any(pts, poly, p, m, poly[pi], -1): continue
        if crosses_any(pts, hole, p, m, -1, hole[hm]): continue
        if any(crosses_any(pts, holes[h], p, m, -1, -1) for h in range(nexthole, len(holes))): continue
        best = pi; break
    if best < 0: best = order[0]
    r = poly[:best+1]
    for k in range(len(hole)+1): r.append(hole[(hm+k) % len(hole)])
    r.append(poly[best])
    r.extend(poly[best+1:])
    return r

def collinearish(a, b, c):
    ln = math.sqrt(dist2(a, c))+math.sqrt(dist2(a, b))
    return abs(cross2(sub2(c, a), sub2(b, a))) <= 1e-9*ln*ln+1e-24

def in_tri(a, b, c, p):
    d1 = cross2(sub2(b, a), sub2(p, a)); d2 = cross2(sub2(c, b), sub2(p, b)); d3 = cross2(sub2(a, c), sub2(p, c))
    return d1 >= -EPS and d2 >= -EPS and d3 >= -EPS

def is_ear(pts, poly, prev, nxt, a, b, c, remaining, atol):
    pa, pb, pc = pts[poly[a]], pts[poly[b]], pts[poly[c]]
    cr = cross2(sub2(pb, pa), sub2(pc, pb))
    if cr <= atol: return False
    v = nxt[c]
    for _ in range(remaining-3):
        iv = poly[v]
        if iv not in (poly[a], poly[b], poly[c]):
            p = pts[iv]
            if not (dist2(p, pa) < 1e-30 or dist2(p, pb) < 1e-30 or dist2(p, pc) < 1e-30):
                if in_tri(pa, pb, pc, p): return False
        v = nxt[v]
    return True

def ear_clip(pts, poly, tris, tol):
    n = len(poly)
    if n < 3: return
    if n == 3: tris.extend(poly); return
    prev = [(i-1+n) % n for i in range(n)]; nxt = [(i+1) % n for i in range(n)]
    remaining = n; cur = 0; guard = 0; atol = tol*tol
    while remaining > 3:
        a, b, c = prev[cur], cur, nxt[cur]
        if is_ear(pts, poly, prev, nxt, a, b, c, remaining, atol):
            tris += [poly[a], poly[b], poly[c]]
            nxt[a] = c; prev[c] = a; remaining -= 1; cur = c; guard = 0; continue
        cr = cross2(sub2(pts[poly[b]], pts[poly[a]]), sub2(pts[poly[c]], pts[poly[b]]))
        if abs(cr) <= atol and dist2(pts[poly[a]], pts[poly[c]]) > 0 and collinearish(pts[poly[a]], pts[poly[b]], pts[poly[c]]):
            nxt[a] = c; prev[c] = a; remaining -= 1; cur = c; guard = 0; continue
        cur = nxt[cur]
        guard += 1
        if guard > remaining:
            bestV = -1; bestCr = -1e300; v = cur
            for _ in range(remaining):
                x = cross2(sub2(pts[poly[v]], pts[poly[prev[v]]]), sub2(pts[poly[nxt[v]]], pts[poly[v]]))
                if x > bestCr: bestCr = x; bestV = v
                v = nxt[v]
            if bestV < 0: return
            a = prev[bestV]; c = nxt[bestV]
            if bestCr > 0: tris += [poly[a], poly[bestV], poly[c]]
            nxt[a] = c; prev[c] = a; remaining -= 1; cur = c; guard = 0
    a, b, c = prev[cur], cur, nxt[cur]
    cr = cross2(sub2(pts[poly[b]], pts[poly[a]]), sub2(pts[poly[c]], pts[poly[b]]))
    if cr > atol: tris += [poly[a], poly[b], poly[c]]

def triangulate(outer, holes=()):
    tris = []; pts = []
    oi = clean(outer, pts, 0)
    if len(oi) < 3: return tris, pts
    if area_idx(pts, oi) < 0: oi.reverse()
    scale = extent(pts, oi); tol = max(1e-12, scale*1e-9)
    hl = []; offset = len(outer)
    for h in holes:
        idx = clean(h, pts, offset); offset += len(h)
        if len(idx) < 3: continue
        if abs(area_idx(pts, idx)) < tol*tol: continue
        if area_idx(pts, idx) > 0: idx.reverse()
        hl.append(idx)
    poly = oi
    if hl:
        hl.sort(key=lambda l: -maxx(pts, l))
        for h in range(len(hl)): poly = bridge(pts, poly, hl[h], hl, h+1)
    ear_clip(pts, poly, tris, tol)
    return tris, pts

def tri_area(pts, tris):
    s = 0
    for i in range(0, len(tris), 3):
        a, b, c = pts[tris[i]], pts[tris[i+1]], pts[tris[i+2]]
        s += cross2(sub2(b, a), sub2(c, a))/2
    return s

# ------------------------------------------------------------------ CSG (BSP)
CEPS = 1e-5
def v_add(a, b): return (a[0]+b[0], a[1]+b[1], a[2]+b[2])
def v_sub(a, b): return (a[0]-b[0], a[1]-b[1], a[2]-b[2])
def v_mul(a, s): return (a[0]*s, a[1]*s, a[2]*s)
def v_dot(a, b): return a[0]*b[0]+a[1]*b[1]+a[2]*b[2]
def v_cross(a, b): return (a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0])
def v_len(a): return math.sqrt(v_dot(a, a))
def lerp(a, b, t): return v_add(a, v_mul(v_sub(b, a), t))

class Plane:
    def __init__(s, n, w): s.n = n; s.w = w
    def clone(s): return Plane(s.n, s.w)
    def flip(s): s.n = v_mul(s.n, -1); s.w = -s.w
    @staticmethod
    def frm(a, b, c):
        n = v_cross(v_sub(b, a), v_sub(c, a)); l = v_len(n)
        if l < 1e-14: return None
        n = v_mul(n, 1/l); return Plane(n, v_dot(n, a))
    def split(s, poly, cof, cob, front, back):
        t = 0; types = []
        for v in poly.v:
            d = v_dot(s.n, v)-s.w
            ty = 2 if d < -CEPS else 1 if d > CEPS else 0
            t |= ty; types.append(ty)
        if t == 0: (cof if v_dot(s.n, poly.p.n) > 0 else cob).append(poly)
        elif t == 1: front.append(poly)
        elif t == 2: back.append(poly)
        else:
            f = []; b = []; n = len(poly.v)
            for i in range(n):
                j = (i+1) % n; ti = types[i]; tj = types[j]; vi = poly.v[i]; vj = poly.v[j]
                if ti != 2: f.append(vi)
                if ti != 1: b.append(vi)
                if (ti | tj) == 3:
                    den = v_dot(s.n, v_sub(vj, vi)); tt = (s.w-v_dot(s.n, vi))/den if abs(den) > 1e-300 else 0.5
                    v = lerp(vi, vj, tt); f.append(v); b.append(v)
            if len(f) >= 3: front.append(Poly(f, poly.p))
            if len(b) >= 3: back.append(Poly(b, poly.p))

class Poly:
    def __init__(s, v, p): s.v = v; s.p = p
    def flipped(s):
        p = s.p.clone(); p.flip(); return Poly(list(reversed(s.v)), p)

class Node:
    def __init__(s, polys=None):
        s.p = None; s.front = None; s.back = None; s.polys = []
        if polys is not None: s.build(polys)
    def build(s, polys):
        st = [(s, polys)]
        while st:
            node, ps = st.pop()
            if not ps: continue
            if node.p is None: node.p = ps[0].p.clone()
            f = []; b = []
            for p in ps: node.p.split(p, node.polys, node.polys, f, b)
            if f:
                if node.front is None: node.front = Node()
                st.append((node.front, f))
            if b:
                if node.back is None: node.back = Node()
                st.append((node.back, b))
    def nodes(s):
        st = [s]
        while st:
            n = st.pop(); yield n
            if n.front: st.append(n.front)
            if n.back: st.append(n.back)
    def invert(s):
        for n in s.nodes():
            n.polys = [p.flipped() for p in n.polys]
            if n.p: n.p.flip()
            n.front, n.back = n.back, n.front
    def clip_polygons(s, polys):
        res = []; st = [(s, polys)]
        while st:
            node, ps = st.pop()
            if node.p is None: res += ps; continue
            f = []; b = []
            for p in ps: node.p.split(p, f, b, f, b)
            if node.front: st.append((node.front, f))
            else: res += f
            if node.back: st.append((node.back, b))
        return res
    def clip_to(s, bsp):
        for n in list(s.nodes()): n.polys = bsp.clip_polygons(n.polys)
    def all_polygons(s):
        r = []
        for n in s.nodes(): r += n.polys
        return r

def to_polys(tris):
    r = []
    for t in tris:
        p = Plane.frm(*t)
        if p: r.append(Poly(list(t), p))
    return r

def to_tris(polys):
    out = []
    for p in polys:
        for i in range(1, len(p.v)-1): out.append((p.v[0], p.v[i], p.v[i+1]))
    return out

def subtract(a, b):
    A = Node(to_polys(a)); B = Node(to_polys(b))
    A.invert(); A.clip_to(B); B.clip_to(A); B.invert(); B.clip_to(A); B.invert(); A.build(B.all_polygons()); A.invert()
    return to_tris(A.all_polygons())

def union(a, b):
    A = Node(to_polys(a)); B = Node(to_polys(b))
    A.clip_to(B); B.clip_to(A); B.invert(); B.clip_to(A); B.invert(); A.build(B.all_polygons())
    return to_tris(A.all_polygons())

def intersect(a, b):
    A = Node(to_polys(a)); B = Node(to_polys(b))
    A.invert(); B.clip_to(A); B.invert(); A.clip_to(B); B.clip_to(A); A.build(B.all_polygons()); A.invert()
    return to_tris(A.all_polygons())

def box(o, u, v, w):
    c = [o, v_add(o, u), v_add(v_add(o, u), v), v_add(o, v)]
    c += [v_add(p, w) for p in c]
    out = []
    def q(a, b, cc, d): out.extend([(c[a], c[b], c[cc]), (c[a], c[cc], c[d])])
    q(0, 3, 2, 1); q(4, 5, 6, 7); q(0, 1, 5, 4); q(1, 2, 6, 5); q(2, 3, 7, 6); q(3, 0, 4, 7)
    if volume(out) < 0: out = [(a, cc, b) for a, b, cc in out]
    return out

def volume(tris):
    v = 0
    for a, b, c in tris: v += v_dot(a, v_cross(b, c))
    return v/6
