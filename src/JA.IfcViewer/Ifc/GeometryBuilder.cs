using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using JA.IfcViewer.Geometry;
using JA.IfcViewer.Step;

namespace JA.IfcViewer.Ifc
{
    /// <summary>A coloured piece of an element: Color 0 = no style of its own (the element's colour is used).</summary>
    public sealed class GeoPart
    {
        public uint Color;
        public Mesh Mesh;
        public bool Closed;
        public GeoPart(uint c, Mesh m, bool closed) { Color = c; Mesh = m; Closed = closed; }
    }

    /// <summary>
    /// IFC geometry → triangles, in metres (every length read is multiplied by the project length unit).
    /// Supported: placements (local, axis 2D/3D, transformation operators incl. mirror and non-uniform scale), mapped items
    /// (cached per representation map), extrusions (all parameterised profiles, arbitrary with voids, composite, derived,
    /// centre-line), revolutions, sweeps along a directrix (swept disk, fixed reference, surface curve), faceted breps,
    /// advanced breps (planar, cylindrical and spherical faces exact, B-spline surfaces over their domain, others planar),
    /// shell / face based surface models, triangulated and polygonal face sets, booleans (half spaces, polygonal bounded
    /// half spaces, solids) through BSP CSG, CSG primitives and bounding boxes. Curves: polylines, indexed poly curves
    /// (with arcs), composite and trimmed curves, lines, circles, ellipses, B-splines (rational too), 2D offsets.
    /// Thread safe: one builder per model, used by several threads.
    /// </summary>
    public sealed class GeometryBuilder
    {
        private readonly StepFile S;
        private readonly double L;      // file length unit → metres
        private readonly double A;      // file plane angle unit → radians
        private readonly IReadOnlyDictionary<int, uint> _itemColors;
        private readonly ConcurrentDictionary<int, Mat> _placements = new ConcurrentDictionary<int, Mat>();
        private readonly ConcurrentDictionary<int, Vec3> _points = new ConcurrentDictionary<int, Vec3>();
        private readonly ConcurrentDictionary<int, List<GeoPart>> _maps = new ConcurrentDictionary<int, List<GeoPart>>();
        public readonly ConcurrentDictionary<string, int> Unsupported = new ConcurrentDictionary<string, int>();
        public int Segments = 24;

        public GeometryBuilder(StepFile s, double lengthScale, double angleScale, IReadOnlyDictionary<int, uint> itemColors)
        {
            S = s; L = lengthScale; A = angleScale; _itemColors = itemColors ?? new Dictionary<int, uint>();
        }

        private void Miss(string what) => Unsupported.AddOrUpdate(what, 1, (k, v) => v + 1);

        // ================================================================== basic entities

        public Vec3 Point(int id)
        {
            if (id <= 0) return Vec3.Zero;
            if (_points.TryGetValue(id, out Vec3 v)) return v;
            StepEntity e = S.Get(id);
            v = e == null ? Vec3.Zero : Coords(e.List(0));
            if (e != null && e.Type != "IFCCARTESIANPOINT") v = Vec3.Zero;
            _points[id] = v;
            return v;
        }

        private Vec3 Coords(object[] c) =>
            new Vec3(c.Length > 0 ? StepFile.AsNumber(c[0]) * L : 0, c.Length > 1 ? StepFile.AsNumber(c[1]) * L : 0, c.Length > 2 ? StepFile.AsNumber(c[2]) * L : 0);

        private Vec3 Direction(int id, Vec3 def)
        {
            if (id <= 0) return def;
            StepEntity e = S.Get(id);
            if (e == null) return def;
            if (e.Type == "IFCVECTOR") return Direction(e.Ref(0), def);
            object[] r = e.List(0);
            var v = new Vec3(r.Length > 0 ? StepFile.AsNumber(r[0]) : 0, r.Length > 1 ? StepFile.AsNumber(r[1]) : 0, r.Length > 2 ? StepFile.AsNumber(r[2]) : 0);
            return v.LengthSq > 1e-30 ? v.Normalized() : def;
        }

        /// <summary>IfcAxis2Placement3D / 2D / IfcAxis1Placement → frame (identity when absent).</summary>
        public Mat Axis(int id)
        {
            if (id <= 0) return Mat.Identity;
            StepEntity e = S.Get(id);
            if (e == null) return Mat.Identity;
            switch (e.Type)
            {
                case "IFCAXIS2PLACEMENT3D":
                    return Mat.Frame(Point(e.Ref(0)), Direction(e.Ref(1), Vec3.UnitZ), Direction(e.Ref(2), Vec3.UnitX));
                case "IFCAXIS2PLACEMENT2D":
                {
                    Vec3 x = Direction(e.Ref(1), Vec3.UnitX);
                    return Mat.Frame(Point(e.Ref(0)), Vec3.UnitZ, new Vec3(x.X, x.Y, 0));
                }
                case "IFCAXIS1PLACEMENT":
                {
                    Vec3 z = Direction(e.Ref(1), Vec3.UnitZ);
                    return Mat.Frame(Point(e.Ref(0)), z, z.AnyPerpendicular());
                }
            }
            return Mat.Identity;
        }

        /// <summary>World matrix of an IfcObjectPlacement (chain of IfcLocalPlacement), cached.</summary>
        public Mat Placement(int id)
        {
            if (id <= 0) return Mat.Identity;
            if (_placements.TryGetValue(id, out Mat m)) return m;
            m = Mat.Identity;
            var chain = new List<int>();
            int cur = id;
            var seen = new HashSet<int>();
            while (cur > 0 && seen.Add(cur))
            {
                if (_placements.TryGetValue(cur, out Mat known)) { m = known; break; }
                chain.Add(cur);
                StepEntity e = S.Get(cur);
                if (e == null || e.Type != "IFCLOCALPLACEMENT") break;
                cur = e.Ref(0);
            }
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                StepEntity e = S.Get(chain[i]);
                Mat local = e != null && e.Type == "IFCLOCALPLACEMENT" ? Axis(e.Ref(1)) : Mat.Identity;
                m = m * local;
                _placements[chain[i]] = m;
            }
            return m;
        }

        /// <summary>IfcCartesianTransformationOperator2D/3D (uniform or not), mirrored when Axis2 says so.</summary>
        private Mat Operator(int id)
        {
            if (id <= 0) return Mat.Identity;
            StepEntity e = S.Get(id);
            if (e == null) return Mat.Identity;
            bool is3 = e.Type.Contains("3D");
            Vec3 x = Direction(e.Ref(0), Vec3.UnitX);
            Vec3 origin = Point(e.Ref(2));
            double s = e.HasNum(3) ? e.Num(3) : 1;
            Vec3 z = is3 ? Direction(e.Ref(4), Vec3.UnitZ) : Vec3.UnitZ;
            if (!is3) x = new Vec3(x.X, x.Y, 0);
            Mat f = Mat.Frame(origin, z, x);
            int a2 = e.Ref(1);
            if (a2 > 0 && Direction(a2, f.Y).Dot(f.Y) < 0) f.Y = -f.Y;
            double s2 = s, s3 = s;
            if (e.Type.EndsWith("NONUNIFORM"))
            {
                if (e.HasNum(is3 ? 5 : 4)) s2 = e.Num(is3 ? 5 : 4);
                if (is3 && e.HasNum(6)) s3 = e.Num(6);
            }
            f.X = f.X * s; f.Y = f.Y * s2; f.Z = f.Z * s3;
            return f;
        }

        // ================================================================== products

        private static readonly HashSet<string> BodyTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SweptSolid", "AdvancedSweptSolid", "Brep", "AdvancedBrep", "Tessellation", "MappedRepresentation", "CSG", "Clipping",
            "SurfaceModel", "SolidModel", "Surface3D", "SectionedSpine"
        };

        /// <summary>Body geometry of an IfcProductDefinitionShape, in the product's local coordinates.</summary>
        public List<GeoPart> ProductShape(int shapeId)
        {
            var parts = new List<GeoPart>();
            StepEntity shape = S.Get(shapeId);
            if (shape == null) return parts;
            var reps = shape.Refs(2).Select(S.Get).Where(r => r != null).ToList();
            StepEntity Pick(Func<StepEntity, bool> f) => reps.FirstOrDefault(f);
            string Ident(StepEntity r) => (r.Str(1) ?? "").Trim();
            string Kind(StepEntity r) => (r.Str(2) ?? "").Trim();
            StepEntity body = Pick(r => Ident(r).Equals("Body", StringComparison.OrdinalIgnoreCase))
                ?? Pick(r => Ident(r).Equals("Facetation", StringComparison.OrdinalIgnoreCase))
                ?? Pick(r => Ident(r).Equals("Mesh", StringComparison.OrdinalIgnoreCase))
                ?? Pick(r => BodyTypes.Contains(Kind(r)) && !Ident(r).Equals("Axis", StringComparison.OrdinalIgnoreCase)
                             && !Ident(r).Equals("FootPrint", StringComparison.OrdinalIgnoreCase) && !Ident(r).Equals("Box", StringComparison.OrdinalIgnoreCase))
                ?? Pick(r => Kind(r).Equals("BoundingBox", StringComparison.OrdinalIgnoreCase) || Ident(r).Equals("Box", StringComparison.OrdinalIgnoreCase));
            if (body == null) return parts;
            foreach (int item in body.Refs(3)) Item(item, 0, Mat.Identity, parts, 0);
            return parts;
        }

        private void Item(int id, uint color, Mat m, List<GeoPart> parts, int depth)
        {
            if (id <= 0 || depth > 12) return;
            if (_itemColors.TryGetValue(id, out uint c)) color = c;
            string type = S.TypeOf(id);
            if (type == null) return;
            if (type == "IFCMAPPEDITEM")
            {
                StepEntity e = S.Get(id);
                int srcId = e.Ref(0);
                StepEntity src = S.Get(srcId);
                if (src == null) return;
                List<GeoPart> local = _maps.GetOrAdd(srcId, k =>
                {
                    var l = new List<GeoPart>();
                    StepEntity rep = S.Get(src.Ref(1));
                    if (rep != null) foreach (int it in rep.Refs(3)) Item(it, 0, Mat.Identity, l, depth + 1);
                    return l;
                });
                Mat t = m * Operator(e.Ref(1)) * Axis(src.Ref(0));
                foreach (GeoPart p in local)
                    parts.Add(new GeoPart(p.Color != 0 ? p.Color : color, t.IsIdentity ? p.Mesh : p.Mesh.Transformed(t), p.Closed));
                return;
            }
            Mesh mesh;
            bool closed;
            try { mesh = Solid(id, out closed); }
            catch (Exception ex) { Miss(type + " (erro)"); Log.Error("geometria #" + id + " " + type, ex); return; }
            if (mesh == null || mesh.IsEmpty) return;
            parts.Add(new GeoPart(color, m.IsIdentity ? mesh : mesh.Transformed(m), closed));
        }

        /// <summary>Mesh of one representation item (local coordinates); closed tells whether it is a solid.</summary>
        public Mesh Solid(int id, out bool closed)
        {
            closed = true;
            StepEntity e = S.Get(id);
            if (e == null) return null;
            switch (e.Type)
            {
                case "IFCEXTRUDEDAREASOLID":
                case "IFCEXTRUDEDAREASOLIDTAPERED":
                    return Extrusion(e);
                case "IFCREVOLVEDAREASOLID":
                case "IFCREVOLVEDAREASOLIDTAPERED":
                    return Revolution(e);
                case "IFCFACETEDBREP":
                case "IFCFACETEDBREPWITHVOIDS":
                case "IFCADVANCEDBREP":
                case "IFCADVANCEDBREPWITHVOIDS":
                    return Shell(e.Ref(0));
                case "IFCCLOSEDSHELL":
                    return Shell(id);
                case "IFCOPENSHELL":
                    closed = false;
                    return Shell(id);
                case "IFCSHELLBASEDSURFACEMODEL":
                {
                    closed = false;
                    var m = new Mesh();
                    foreach (int sh in e.Refs(0)) m.AddAll(Shell(sh));
                    return m;
                }
                case "IFCFACEBASEDSURFACEMODEL":
                {
                    closed = false;
                    var m = new Mesh();
                    foreach (int fs in e.Refs(0)) m.AddAll(Shell(fs));
                    return m;
                }
                case "IFCTRIANGULATEDFACESET":
                case "IFCTRIANGULATEDIRREGULARNETWORK":
                    return TriangulatedFaceSet(e, out closed);
                case "IFCPOLYGONALFACESET":
                    closed = e.Bool(1) == true;
                    return PolygonalFaceSet(e);
                case "IFCBOOLEANRESULT":
                case "IFCBOOLEANCLIPPINGRESULT":
                    return Boolean(e);
                case "IFCCSGSOLID":
                    return Solid(e.Ref(0), out closed);
                case "IFCBLOCK":
                {
                    Mat p = Axis(e.Ref(0));
                    return Csg.Box(p.T, p.X * (e.Num(1) * L), p.Y * (e.Num(2) * L), p.Z * (e.Num(3) * L));
                }
                case "IFCRIGHTCIRCULARCYLINDER":
                    return Revolved2D(Axis(e.Ref(0)), new List<Vec2> { new Vec2(0, 0), new Vec2(e.Num(2) * L, 0), new Vec2(e.Num(2) * L, e.Num(1) * L), new Vec2(0, e.Num(1) * L) });
                case "IFCRIGHTCIRCULARCONE":
                    return Revolved2D(Axis(e.Ref(0)), new List<Vec2> { new Vec2(0, 0), new Vec2(e.Num(2) * L, 0), new Vec2(0, e.Num(1) * L) });
                case "IFCSPHERE":
                {
                    double r = e.Num(1) * L;
                    var prof = new List<Vec2>();
                    int n = Segments / 2;
                    for (int i = 0; i <= n; i++) { double a = -Math.PI / 2 + Math.PI * i / n; prof.Add(new Vec2(r * Math.Cos(a), r * Math.Sin(a))); }
                    prof[0] = new Vec2(0, -r); prof[n] = new Vec2(0, r);
                    return Revolved2D(Axis(e.Ref(0)), prof);
                }
                case "IFCRECTANGULARPYRAMID":
                {
                    Mat p = Axis(e.Ref(0));
                    double x = e.Num(1) * L, y = e.Num(2) * L, h = e.Num(3) * L;
                    Vec3 o = p.T, ex = p.X * x, ey = p.Y * y, apex = p.T + ex * 0.5 + ey * 0.5 + p.Z * h;
                    var m = new Mesh();
                    m.Add(o, o + ey, o + ex + ey); m.Add(o, o + ex + ey, o + ex);
                    m.Add(o, o + ex, apex); m.Add(o + ex, o + ex + ey, apex); m.Add(o + ex + ey, o + ey, apex); m.Add(o + ey, o, apex);
                    m.Orient();
                    return m;
                }
                case "IFCSWEPTDISKSOLID":
                case "IFCSWEPTDISKSOLIDPOLYGONAL":
                    return SweptDisk(e);
                case "IFCSURFACECURVESWEPTAREASOLID":
                case "IFCFIXEDREFERENCESWEPTAREASOLID":
                case "IFCDIRECTRIXCURVESWEPTAREASOLID":
                    return Sweep(e);
                case "IFCBOUNDINGBOX":
                {
                    Vec3 c = Point(e.Ref(0));
                    return Csg.Box(c, Vec3.UnitX * (e.Num(1) * L), Vec3.UnitY * (e.Num(2) * L), Vec3.UnitZ * (e.Num(3) * L));
                }
                case "IFCHALFSPACESOLID":
                case "IFCPOLYGONALBOUNDEDHALFSPACE":
                case "IFCBOXEDHALFSPACE":
                    return null;   // only meaningful inside a boolean
            }
            if (e.Type.Contains("CURVE") || e.Type.Contains("POLYLINE") || e.Type.Contains("POINT") || e.Type == "IFCGEOMETRICSET"
                || e.Type == "IFCGEOMETRICCURVESET" || e.Type.Contains("TEXT") || e.Type.Contains("ANNOTATION")) return null;
            Miss(IfcNames.Pretty(e.Type));
            return null;
        }

        // ================================================================== profiles

        private sealed class Loops
        {
            public List<Vec2> Outer;
            public List<List<Vec2>> Holes = new List<List<Vec2>>();
        }

        private List<Loops> Profile(int id, int depth = 0)
        {
            var r = new List<Loops>();
            StepEntity e = S.Get(id);
            if (e == null || depth > 8) return r;
            string t = e.Type;
            if (t == "IFCCOMPOSITEPROFILEDEF")
            {
                foreach (int p in e.Refs(2)) r.AddRange(Profile(p, depth + 1));
                return r;
            }
            if (t == "IFCDERIVEDPROFILEDEF" || t == "IFCMIRROREDPROFILEDEF")
            {
                Mat op = Operator(e.Ref(3));
                if (t == "IFCMIRROREDPROFILEDEF") op = new Mat(new Vec3(-1, 0, 0), Vec3.UnitY, Vec3.UnitZ, Vec3.Zero);
                foreach (Loops l in Profile(e.Ref(2), depth + 1)) r.Add(Transform(l, op));
                return r;
            }
            if (t == "IFCARBITRARYCLOSEDPROFILEDEF" || t == "IFCARBITRARYPROFILEDEFWITHVOIDS")
            {
                var lp = new Loops { Outer = Flat(Curve(e.Ref(2))) };
                if (t == "IFCARBITRARYPROFILEDEFWITHVOIDS") foreach (int c in e.Refs(3)) lp.Holes.Add(Flat(Curve(c)));
                if (lp.Outer.Count >= 3) r.Add(lp);
                return r;
            }
            if (t == "IFCCENTERLINEPROFILEDEF")
            {
                List<Vec2> line = Flat(Curve(e.Ref(2)));
                double th = e.Num(3) * L;
                List<Vec2> o = Outline(line, th);
                if (o.Count >= 3) r.Add(new Loops { Outer = o });
                return r;
            }
            if (t == "IFCARBITRARYOPENPROFILEDEF") { Miss("IfcArbitraryOpenProfileDef"); return r; }

            Mat pos = Axis(e.Ref(2));
            double N(int i) => e.Num(i) * L;
            var loops = new Loops();
            switch (t)
            {
                case "IFCRECTANGLEPROFILEDEF":
                    loops.Outer = Rect(N(3), N(4));
                    break;
                case "IFCROUNDEDRECTANGLEPROFILEDEF":
                    loops.Outer = RoundRect(N(3), N(4), N(5));
                    break;
                case "IFCRECTANGLEHOLLOWPROFILEDEF":
                {
                    double x = N(3), y = N(4), w = N(5);
                    loops.Outer = e.HasNum(7) && N(7) > 0 ? RoundRect(x, y, N(7)) : Rect(x, y);
                    if (w > 0 && w * 2 < Math.Min(x, y))
                        loops.Holes.Add(e.HasNum(6) && N(6) > 0 ? RoundRect(x - 2 * w, y - 2 * w, N(6)) : Rect(x - 2 * w, y - 2 * w));
                    break;
                }
                case "IFCCIRCLEPROFILEDEF":
                    loops.Outer = Ellipse(N(3), N(3));
                    break;
                case "IFCCIRCLEHOLLOWPROFILEDEF":
                    loops.Outer = Ellipse(N(3), N(3));
                    if (N(4) > 0 && N(4) < N(3)) loops.Holes.Add(Ellipse(N(3) - N(4), N(3) - N(4)));
                    break;
                case "IFCELLIPSEPROFILEDEF":
                    loops.Outer = Ellipse(N(3), N(4));
                    break;
                case "IFCISHAPEPROFILEDEF":
                {
                    double b = N(3), h = N(4), tw = N(5), tf = N(6);
                    loops.Outer = P(-b / 2, -h / 2, b / 2, -h / 2, b / 2, -h / 2 + tf, tw / 2, -h / 2 + tf, tw / 2, h / 2 - tf, b / 2, h / 2 - tf,
                                    b / 2, h / 2, -b / 2, h / 2, -b / 2, h / 2 - tf, -tw / 2, h / 2 - tf, -tw / 2, -h / 2 + tf, -b / 2, -h / 2 + tf);
                    break;
                }
                case "IFCASYMMETRICISHAPEPROFILEDEF":
                {
                    double bb = N(3), h = N(4), tw = N(5), tfb = N(6), bt = e.HasNum(8) ? N(8) : bb, tft = e.HasNum(9) ? N(9) : tfb;
                    loops.Outer = P(-bb / 2, -h / 2, bb / 2, -h / 2, bb / 2, -h / 2 + tfb, tw / 2, -h / 2 + tfb, tw / 2, h / 2 - tft, bt / 2, h / 2 - tft,
                                    bt / 2, h / 2, -bt / 2, h / 2, -bt / 2, h / 2 - tft, -tw / 2, h / 2 - tft, -tw / 2, -h / 2 + tfb, -bb / 2, -h / 2 + tfb);
                    break;
                }
                case "IFCLSHAPEPROFILEDEF":
                {
                    double h = N(3), b = e.HasNum(4) ? N(4) : h, th = N(5);
                    loops.Outer = P(-b / 2, -h / 2, b / 2, -h / 2, b / 2, -h / 2 + th, -b / 2 + th, -h / 2 + th, -b / 2 + th, h / 2, -b / 2, h / 2);
                    break;
                }
                case "IFCTSHAPEPROFILEDEF":
                {
                    double h = N(3), b = N(4), tw = N(5), tf = N(6);
                    loops.Outer = P(-tw / 2, -h / 2, tw / 2, -h / 2, tw / 2, h / 2 - tf, b / 2, h / 2 - tf, b / 2, h / 2, -b / 2, h / 2, -b / 2, h / 2 - tf, -tw / 2, h / 2 - tf);
                    break;
                }
                case "IFCUSHAPEPROFILEDEF":
                {
                    double h = N(3), b = N(4), tw = N(5), tf = N(6);
                    loops.Outer = P(-b / 2, -h / 2, b / 2, -h / 2, b / 2, -h / 2 + tf, -b / 2 + tw, -h / 2 + tf, -b / 2 + tw, h / 2 - tf, b / 2, h / 2 - tf, b / 2, h / 2, -b / 2, h / 2);
                    break;
                }
                case "IFCCSHAPEPROFILEDEF":
                {
                    double h = N(3), b = N(4), th = N(5), g = N(6);
                    loops.Outer = P(-b / 2, -h / 2, b / 2, -h / 2, b / 2, -h / 2 + g, b / 2 - th, -h / 2 + g, b / 2 - th, -h / 2 + th, -b / 2 + th, -h / 2 + th,
                                    -b / 2 + th, h / 2 - th, b / 2 - th, h / 2 - th, b / 2 - th, h / 2 - g, b / 2, h / 2 - g, b / 2, h / 2, -b / 2, h / 2);
                    break;
                }
                case "IFCZSHAPEPROFILEDEF":
                {
                    double h = N(3), b = N(4), tw = N(5), tf = N(6);
                    loops.Outer = P(-tw / 2, -h / 2, b - tw / 2, -h / 2, b - tw / 2, -h / 2 + tf, tw / 2, -h / 2 + tf, tw / 2, h / 2, -b + tw / 2, h / 2,
                                    -b + tw / 2, h / 2 - tf, -tw / 2, h / 2 - tf);
                    break;
                }
                case "IFCTRAPEZIUMPROFILEDEF":
                {
                    double bx = N(3), tx = N(4), y = N(5), off = N(6);
                    loops.Outer = P(-bx / 2, -y / 2, bx / 2, -y / 2, -bx / 2 + off + tx, y / 2, -bx / 2 + off, y / 2);
                    break;
                }
                default:
                    Miss(IfcNames.Pretty(t));
                    return r;
            }
            if (loops.Outer == null || loops.Outer.Count < 3) return r;
            r.Add(pos.IsIdentity ? loops : Transform(loops, pos));
            return r;
        }

        private static List<Vec2> P(params double[] xy)
        {
            var l = new List<Vec2>(xy.Length / 2);
            for (int i = 0; i + 1 < xy.Length; i += 2) l.Add(new Vec2(xy[i], xy[i + 1]));
            return l;
        }

        private static List<Vec2> Rect(double x, double y) => P(-x / 2, -y / 2, x / 2, -y / 2, x / 2, y / 2, -x / 2, y / 2);

        private List<Vec2> RoundRect(double x, double y, double r)
        {
            r = Math.Max(0, Math.Min(r, Math.Min(x, y) / 2));
            if (r <= 0) return Rect(x, y);
            var l = new List<Vec2>();
            int n = Math.Max(2, Segments / 4);
            void Corner(double cx, double cy, double a0)
            {
                for (int i = 0; i <= n; i++) { double a = a0 + Math.PI / 2 * i / n; l.Add(new Vec2(cx + r * Math.Cos(a), cy + r * Math.Sin(a))); }
            }
            Corner(x / 2 - r, -y / 2 + r, -Math.PI / 2);
            Corner(x / 2 - r, y / 2 - r, 0);
            Corner(-x / 2 + r, y / 2 - r, Math.PI / 2);
            Corner(-x / 2 + r, -y / 2 + r, Math.PI);
            return l;
        }

        private List<Vec2> Ellipse(double a, double b)
        {
            var l = new List<Vec2>(Segments);
            for (int i = 0; i < Segments; i++) { double t = 2 * Math.PI * i / Segments; l.Add(new Vec2(a * Math.Cos(t), b * Math.Sin(t))); }
            return l;
        }

        private static Loops Transform(Loops l, Mat m)
        {
            List<Vec2> T(List<Vec2> pts)
            {
                var r = new List<Vec2>(pts.Count);
                foreach (Vec2 p in pts) { Vec3 q = m.Point(new Vec3(p.X, p.Y, 0)); r.Add(new Vec2(q.X, q.Y)); }
                return r;
            }
            var o = new Loops { Outer = T(l.Outer) };
            foreach (var h in l.Holes) o.Holes.Add(T(h));
            return o;
        }

        private static List<Vec2> Flat(List<Vec3> pts)
        {
            var r = new List<Vec2>(pts.Count);
            foreach (Vec3 p in pts) r.Add(new Vec2(p.X, p.Y));
            if (r.Count > 2 && (r[0] - r[r.Count - 1]).Length < 1e-9) r.RemoveAt(r.Count - 1);
            return r;
        }

        /// <summary>Closed outline of an open polyline thickened by th (mitred joins).</summary>
        private static List<Vec2> Outline(List<Vec2> line, double th)
        {
            var left = new List<Vec2>();
            var right = new List<Vec2>();
            int n = line.Count;
            if (n < 2 || th <= 0) return left;
            double h = th / 2;
            for (int i = 0; i < n; i++)
            {
                Vec2 d0 = i > 0 ? Unit(line[i] - line[i - 1]) : Unit(line[1] - line[0]);
                Vec2 d1 = i < n - 1 ? Unit(line[i + 1] - line[i]) : d0;
                Vec2 nrm0 = new Vec2(-d0.Y, d0.X), nrm1 = new Vec2(-d1.Y, d1.X);
                Vec2 bis = Unit(nrm0 + nrm1);
                double c = bis.Dot(nrm1);
                double k = Math.Abs(c) > 0.2 ? h / c : h;
                left.Add(line[i] + bis * k);
                right.Add(line[i] - bis * k);
            }
            right.Reverse();
            left.AddRange(right);
            return left;
        }

        private static Vec2 Unit(Vec2 v) { double l = v.Length; return l > 1e-12 ? v * (1 / l) : new Vec2(1, 0); }

        // ================================================================== curves

        /// <summary>Points along a curve (3D; 2D curves have z = 0).</summary>
        public List<Vec3> Curve(int id, int depth = 0)
        {
            var r = new List<Vec3>();
            StepEntity e = S.Get(id);
            if (e == null || depth > 10) return r;
            switch (e.Type)
            {
                case "IFCPOLYLINE":
                    foreach (int p in e.Refs(0)) r.Add(Point(p));
                    return r;
                case "IFCINDEXEDPOLYCURVE":
                    return IndexedPolyCurve(e);
                case "IFCCOMPOSITECURVE":
                case "IFCCOMPOSITECURVEONSURFACE":
                case "IFCGRADIENTCURVE":
                case "IFCSEGMENTEDREFERENCECURVE":
                    foreach (int sid in e.Refs(0))
                    {
                        StepEntity seg = S.Get(sid);
                        if (seg == null) continue;
                        List<Vec3> pts;
                        if (seg.Type == "IFCCOMPOSITECURVESEGMENT" || seg.Type == "IFCREPARAMETRISEDCOMPOSITECURVESEGMENT")
                        {
                            pts = Curve(seg.Ref(2), depth + 1);
                            if (seg.Bool(1) == false) pts.Reverse();
                        }
                        else { Miss(IfcNames.Pretty(seg.Type)); continue; }
                        Append(r, pts);
                    }
                    return r;
                case "IFCTRIMMEDCURVE":
                    return Trimmed(e, depth);
                case "IFCCIRCLE":
                case "IFCELLIPSE":
                {
                    Mat f = Axis(e.Ref(0));
                    double a = e.Num(1) * L, b = e.Type == "IFCELLIPSE" ? e.Num(2) * L : a;
                    for (int i = 0; i < Segments; i++)
                    {
                        double t = 2 * Math.PI * i / Segments;
                        r.Add(f.Point(new Vec3(a * Math.Cos(t), b * Math.Sin(t), 0)));
                    }
                    return r;
                }
                case "IFCLINE":
                {
                    Vec3 p = Point(e.Ref(0));
                    r.Add(p); r.Add(p + LineDir(e));
                    return r;
                }
                case "IFCBSPLINECURVEWITHKNOTS":
                case "IFCRATIONALBSPLINECURVEWITHKNOTS":
                    return BSpline(e);
                case "IFCOFFSETCURVE2D":
                {
                    List<Vec2> basis = Flat(Curve(e.Ref(0), depth + 1));
                    double d = e.Num(1) * L;
                    foreach (Vec2 p in OffsetLine(basis, d)) r.Add(new Vec3(p.X, p.Y, 0));
                    return r;
                }
                case "IFCOFFSETCURVE3D":
                case "IFCOFFSETCURVEBYDISTANCES":
                    return Curve(e.Ref(0), depth + 1);
            }
            Miss(IfcNames.Pretty(e.Type));
            return r;
        }

        private static void Append(List<Vec3> r, List<Vec3> pts)
        {
            foreach (Vec3 p in pts)
            {
                if (r.Count > 0 && (r[r.Count - 1] - p).LengthSq < 1e-16) continue;
                r.Add(p);
            }
        }

        private static List<Vec2> OffsetLine(List<Vec2> line, double d)
        {
            var r = new List<Vec2>();
            int n = line.Count;
            for (int i = 0; i < n; i++)
            {
                Vec2 d0 = i > 0 ? Unit(line[i] - line[i - 1]) : Unit(line[Math.Min(1, n - 1)] - line[0]);
                Vec2 d1 = i < n - 1 ? Unit(line[i + 1] - line[i]) : d0;
                Vec2 n0 = new Vec2(d0.Y, -d0.X), n1 = new Vec2(d1.Y, -d1.X);
                Vec2 bis = Unit(n0 + n1);
                double c = bis.Dot(n1);
                r.Add(line[i] + bis * (Math.Abs(c) > 0.2 ? d / c : d));
            }
            return r;
        }

        private Vec3 LineDir(StepEntity line)
        {
            StepEntity v = S.Get(line.Ref(1));
            if (v == null) return Vec3.UnitX;
            if (v.Type == "IFCVECTOR") return Direction(v.Ref(0), Vec3.UnitX) * (v.Num(1) * L);
            return Direction(v.Id, Vec3.UnitX);
        }

        private List<Vec3> IndexedPolyCurve(StepEntity e)
        {
            var r = new List<Vec3>();
            StepEntity pl = S.Get(e.Ref(0));
            if (pl == null) return r;
            var pts = new List<Vec3>();
            foreach (object o in pl.List(0)) if (o is object[] c) pts.Add(Coords(c));
            Vec3 At(object o) { int i = (int)StepFile.AsNumber(o) - 1; return i >= 0 && i < pts.Count ? pts[i] : Vec3.Zero; }
            object[] segs = e.List(1);
            if (segs.Length == 0) return pts;
            foreach (object so in segs)
            {
                if (!(so is StepTyped st)) continue;
                object[] idx = st.Value as object[] ?? new[] { st.Value };
                if (st.Type == "IFCARCINDEX" && idx.Length >= 3)
                    Append(r, Arc3(At(idx[0]), At(idx[1]), At(idx[2])));
                else
                {
                    var l = new List<Vec3>();
                    foreach (object i in idx) l.Add(At(i));
                    Append(r, l);
                }
            }
            return r;
        }

        /// <summary>Arc through three points.</summary>
        private List<Vec3> Arc3(Vec3 a, Vec3 b, Vec3 c)
        {
            Vec3 ab = b - a, ac = c - a;
            Vec3 n = ab.Cross(ac);
            double nl = n.LengthSq;
            if (nl < 1e-24) return new List<Vec3> { a, b, c };
            Vec3 center = a + (n.Cross(ab) * ac.LengthSq + ac.Cross(n) * ab.LengthSq) / (2 * nl);
            Vec3 zz = n.Normalized();
            Vec3 xx = (a - center).Normalized();
            Vec3 yy = zz.Cross(xx);
            double r = (a - center).Length;
            double Ang(Vec3 p) { Vec3 d = p - center; double t = Math.Atan2(d.Dot(yy), d.Dot(xx)); return t < 0 ? t + 2 * Math.PI : t; }
            double end = Ang(c);
            if (end < 1e-9) end = 2 * Math.PI;
            int steps = Math.Max(2, (int)Math.Ceiling(end / (2 * Math.PI / Segments)));
            var l = new List<Vec3>(steps + 1);
            for (int i = 0; i <= steps; i++) { double t = end * i / steps; l.Add(center + xx * (r * Math.Cos(t)) + yy * (r * Math.Sin(t))); }
            l[0] = a; l[l.Count - 1] = c;
            return l;
        }

        private List<Vec3> Trimmed(StepEntity e, int depth)
        {
            var r = new List<Vec3>();
            StepEntity basis = S.Get(e.Ref(0));
            if (basis == null) return r;
            object[] t1 = e.List(1), t2 = e.List(2);
            bool sense = e.Bool(3) != false;
            string master = e.Enum(4) ?? "UNSPECIFIED";
            bool TrimParam(object[] t, out double v)
            {
                foreach (object o in t) if (o is StepTyped ty && ty.Type == "IFCPARAMETERVALUE") { v = StepFile.AsNumber(ty.Value); return true; }
                v = 0; return false;
            }
            bool TrimPoint(object[] t, out Vec3 p)
            {
                foreach (object o in t) if (o is StepRef rr && S.TypeOf(rr.Id) == "IFCCARTESIANPOINT") { p = Point(rr.Id); return true; }
                p = Vec3.Zero; return false;
            }
            bool hasP1 = TrimParam(t1, out double p1), hasP2 = TrimParam(t2, out double p2);
            bool hasC1 = TrimPoint(t1, out Vec3 c1), hasC2 = TrimPoint(t2, out Vec3 c2);
            bool useParam = hasP1 && hasP2 && (master != "CARTESIAN" || !(hasC1 && hasC2));
            bool usePoint = !useParam && hasC1 && hasC2;

            if (basis.Type == "IFCLINE")
            {
                Vec3 o = Point(basis.Ref(0)), d = LineDir(basis);
                if (useParam) { r.Add(o + d * p1); r.Add(o + d * p2); }
                else if (usePoint) { r.Add(c1); r.Add(c2); }
                return r;
            }
            if (basis.Type == "IFCCIRCLE" || basis.Type == "IFCELLIPSE")
            {
                Mat f = Axis(basis.Ref(0));
                double ra = basis.Num(1) * L, rb = basis.Type == "IFCELLIPSE" ? basis.Num(2) * L : ra;
                double a1, a2;
                if (useParam) { a1 = p1 * A; a2 = p2 * A; }
                else if (usePoint)
                {
                    double Ang(Vec3 p) { Vec3 d = p - f.T; return Math.Atan2(d.Dot(f.Y) / rb, d.Dot(f.X) / ra); }
                    a1 = Ang(c1); a2 = Ang(c2);
                }
                else { a1 = 0; a2 = 2 * Math.PI; }
                if (sense) { while (a2 <= a1 + 1e-12) a2 += 2 * Math.PI; }
                else { while (a2 >= a1 - 1e-12) a2 -= 2 * Math.PI; }
                double span = Math.Abs(a2 - a1);
                int steps = Math.Max(1, (int)Math.Ceiling(span / (2 * Math.PI / Segments)));
                for (int i = 0; i <= steps; i++)
                {
                    double t = a1 + (a2 - a1) * i / steps;
                    r.Add(f.Point(new Vec3(ra * Math.Cos(t), rb * Math.Sin(t), 0)));
                }
                if (usePoint) { r[0] = c1; r[r.Count - 1] = c2; }
                return r;
            }
            // other basis curves (polyline, B-spline…): the whole curve, from the trim point nearest each end
            List<Vec3> all = Curve(basis.Id, depth + 1);
            if (usePoint && all.Count > 1)
            {
                int i1 = Nearest(all, c1), i2 = Nearest(all, c2);
                if (i1 <= i2) r.AddRange(all.GetRange(i1, i2 - i1 + 1));
                else { r.AddRange(all.GetRange(i2, i1 - i2 + 1)); r.Reverse(); }
                if (r.Count > 0) { r[0] = c1; r[r.Count - 1] = c2; }
                return r;
            }
            if (!sense) all.Reverse();
            return all;
        }

        private static int Nearest(List<Vec3> pts, Vec3 p)
        {
            int best = 0; double bd = double.MaxValue;
            for (int i = 0; i < pts.Count; i++) { double d = (pts[i] - p).LengthSq; if (d < bd) { bd = d; best = i; } }
            return best;
        }

        private List<Vec3> BSpline(StepEntity e)
        {
            int p = (int)e.Num(0);
            var ctrl = e.Refs(1).Select(Point).ToList();
            var mult = e.List(5).Select(o => (int)StepFile.AsNumber(o)).ToList();
            var kv = e.List(6).Select(o => StepFile.AsNumber(o)).ToList();
            var w = e.Type == "IFCRATIONALBSPLINECURVEWITHKNOTS" ? e.List(8).Select(o => StepFile.AsNumber(o, 1)).ToList() : null;
            var knots = new List<double>();
            for (int i = 0; i < kv.Count && i < mult.Count; i++) for (int k = 0; k < mult[i]; k++) knots.Add(kv[i]);
            int n = ctrl.Count;
            if (n < 2 || p < 1 || knots.Count != n + p + 1) return ctrl;
            double u0 = knots[p], u1 = knots[n];
            int samples = Math.Max(8, Math.Min(400, n * 6));
            var r = new List<Vec3>(samples + 1);
            for (int i = 0; i <= samples; i++) r.Add(DeBoor(p, knots, ctrl, w, u0 + (u1 - u0) * i / samples));
            return r;
        }

        private static Vec3 DeBoor(int p, List<double> U, List<Vec3> P, List<double> W, double u)
        {
            int n = P.Count;
            int k = p;
            while (k < n - 1 && u >= U[k + 1]) k++;
            var d = new double[p + 1, 4];
            for (int j = 0; j <= p; j++)
            {
                int idx = j + k - p;
                if (idx < 0) idx = 0; if (idx >= n) idx = n - 1;
                double wt = W != null && idx < W.Count ? W[idx] : 1;
                d[j, 0] = P[idx].X * wt; d[j, 1] = P[idx].Y * wt; d[j, 2] = P[idx].Z * wt; d[j, 3] = wt;
            }
            for (int r = 1; r <= p; r++)
                for (int j = p; j >= r; j--)
                {
                    int i = j + k - p;
                    double den = U[i + p - r + 1] - U[i];
                    double a = den > 1e-300 ? (u - U[i]) / den : 0;
                    for (int c = 0; c < 4; c++) d[j, c] = (1 - a) * d[j - 1, c] + a * d[j, c];
                }
            double ww = Math.Abs(d[p, 3]) > 1e-300 ? d[p, 3] : 1;
            return new Vec3(d[p, 0] / ww, d[p, 1] / ww, d[p, 2] / ww);
        }

        // ================================================================== swept solids

        private Mesh Extrusion(StepEntity e)
        {
            List<Loops> prof = Profile(e.Ref(0));
            if (prof.Count == 0) return null;
            Mat pos = Axis(e.Ref(1));
            Vec3 dir = Direction(e.Ref(2), Vec3.UnitZ) * (e.Num(3) * L);
            if (dir.LengthSq < 1e-18) return null;
            var m = new Mesh();
            foreach (Loops l in prof) ExtrudeLoops(m, l, dir);
            m.Orient();
            return pos.IsIdentity ? m : m.Transformed(pos);
        }

        private static void ExtrudeLoops(Mesh m, Loops l, Vec3 d)
        {
            var outer = new List<Vec2>(l.Outer);
            if (Triangulator.Area(outer) < 0) outer.Reverse();
            var holes = new List<IList<Vec2>>();
            foreach (var h in l.Holes)
            {
                if (h.Count < 3) continue;
                var hh = new List<Vec2>(h);
                if (Triangulator.Area(hh) > 0) hh.Reverse();
                holes.Add(hh);
            }
            var all = new List<Vec2>(outer);
            foreach (var h in holes) all.AddRange(h);
            List<int> tri = Triangulator.Triangulate(outer, holes);
            Vec3 B(int i) => new Vec3(all[i].X, all[i].Y, 0);
            for (int i = 0; i + 2 < tri.Count; i += 3)
            {
                Vec3 a = B(tri[i]), b = B(tri[i + 1]), c = B(tri[i + 2]);
                m.Add(a, c, b);                 // bottom, facing -z
                m.Add(a + d, b + d, c + d);     // top
            }
            void Sides(IList<Vec2> loop)
            {
                for (int i = 0; i < loop.Count; i++)
                {
                    Vec2 p = loop[i], q = loop[(i + 1) % loop.Count];
                    Vec3 p0 = new Vec3(p.X, p.Y, 0), q0 = new Vec3(q.X, q.Y, 0);
                    m.Add(p0, q0, q0 + d);
                    m.Add(p0, q0 + d, p0 + d);
                }
            }
            Sides(outer);
            foreach (var h in holes) Sides(h);
        }

        private Mesh Revolution(StepEntity e)
        {
            List<Loops> prof = Profile(e.Ref(0));
            if (prof.Count == 0) return null;
            Mat pos = Axis(e.Ref(1));
            StepEntity ax = S.Get(e.Ref(2));
            Vec3 o = ax != null ? Point(ax.Ref(0)) : Vec3.Zero;
            Vec3 k = ax != null ? Direction(ax.Ref(1), Vec3.UnitY) : Vec3.UnitY;
            double ang = e.Num(3) * A;
            if (Math.Abs(ang) < 1e-9) return null;
            var m = new Mesh();
            foreach (Loops l in prof) Revolve(m, l, o, k, ang);
            m.Orient();
            return pos.IsIdentity ? m : m.Transformed(pos);
        }

        private void Revolve(Mesh m, Loops l, Vec3 o, Vec3 k, double ang)
        {
            bool full = Math.Abs(Math.Abs(ang) - 2 * Math.PI) < 1e-6;
            int steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(ang) / (2 * Math.PI / Segments)));
            var loops = new List<List<Vec2>> { l.Outer };
            loops.AddRange(l.Holes);
            Vec3 Rot(Vec2 p, int s)
            {
                Mat r = Mat.Rotation(k, ang * s / steps);
                return o + r.Vector(new Vec3(p.X, p.Y, 0) - o);
            }
            foreach (List<Vec2> loop in loops)
                for (int s = 0; s < steps; s++)
                    for (int i = 0; i < loop.Count; i++)
                    {
                        Vec2 p = loop[i], q = loop[(i + 1) % loop.Count];
                        Vec3 a = Rot(p, s), b = Rot(q, s), c = Rot(q, s + 1), d = Rot(p, s + 1);
                        m.Add(a, b, c); m.Add(a, c, d);
                    }
            if (!full)
            {
                var holes = l.Holes.Cast<IList<Vec2>>().ToList();
                var all = new List<Vec2>(l.Outer);
                foreach (var h in l.Holes) all.AddRange(h);
                List<int> tri = Triangulator.Triangulate(l.Outer, holes);
                for (int i = 0; i + 2 < tri.Count; i += 3)
                {
                    m.Add(Rot(all[tri[i]], 0), Rot(all[tri[i + 2]], 0), Rot(all[tri[i + 1]], 0));
                    m.Add(Rot(all[tri[i]], steps), Rot(all[tri[i + 1]], steps), Rot(all[tri[i + 2]], steps));
                }
            }
        }

        /// <summary>A profile in the XZ plane of a frame (x = radius, y = height) revolved about the frame's Z axis.</summary>
        private Mesh Revolved2D(Mat frame, List<Vec2> rz)
        {
            var m = new Mesh();
            int n = Segments;
            for (int s = 0; s < n; s++)
            {
                double a0 = 2 * Math.PI * s / n, a1 = 2 * Math.PI * (s + 1) / n;
                for (int i = 0; i < rz.Count; i++)
                {
                    Vec2 p = rz[i], q = rz[(i + 1) % rz.Count];
                    Vec3 Pt(Vec2 v, double a) => frame.Point(new Vec3(v.X * Math.Cos(a), v.X * Math.Sin(a), v.Y));
                    m.Add(Pt(p, a0), Pt(q, a0), Pt(q, a1));
                    m.Add(Pt(p, a0), Pt(q, a1), Pt(p, a1));
                }
            }
            m.Orient();
            return m;
        }

        private Mesh SweptDisk(StepEntity e)
        {
            List<Vec3> path = Curve(e.Ref(0));
            double r = e.Num(1) * L;
            if (path.Count < 2 || r <= 0) return null;
            int n = Math.Max(8, Segments / 2);
            var ring = new List<Vec2>(n);
            for (int i = 0; i < n; i++) { double a = 2 * Math.PI * i / n; ring.Add(new Vec2(r * Math.Cos(a), r * Math.Sin(a))); }
            var m = new Mesh();
            SweepLoops(m, new Loops { Outer = ring }, path, null);
            m.Orient();
            return m;
        }

        private Mesh Sweep(StepEntity e)
        {
            List<Loops> prof = Profile(e.Ref(0));
            if (prof.Count == 0) return null;
            Mat pos = Axis(e.Ref(1));
            List<Vec3> path = Curve(e.Ref(2));
            if (path.Count < 2) return null;
            Vec3? fixedRef = null;
            if (e.Type == "IFCFIXEDREFERENCESWEPTAREASOLID") fixedRef = Direction(e.Ref(5), Vec3.UnitX);
            var m = new Mesh();
            foreach (Loops l in prof) SweepLoops(m, l, path, fixedRef);
            m.Orient();
            return pos.IsIdentity ? m : m.Transformed(pos);
        }

        /// <summary>Profile loops (x, y) carried along a path: the profile's z follows the path tangent.</summary>
        private static void SweepLoops(Mesh m, Loops l, List<Vec3> path, Vec3? fixedRef)
        {
            var pts = new List<Vec3>();
            foreach (Vec3 p in path) if (pts.Count == 0 || (pts[pts.Count - 1] - p).LengthSq > 1e-16) pts.Add(p);
            int n = pts.Count;
            if (n < 2) return;
            var frames = new List<(Vec3 x, Vec3 y)>(n);
            Vec3 prevX = Vec3.Zero;
            for (int i = 0; i < n; i++)
            {
                Vec3 t0 = i > 0 ? (pts[i] - pts[i - 1]).Normalized() : (pts[1] - pts[0]).Normalized();
                Vec3 t1 = i < n - 1 ? (pts[i + 1] - pts[i]).Normalized() : t0;
                Vec3 t = (t0 + t1).LengthSq > 1e-12 ? (t0 + t1).Normalized() : t1;
                Vec3 x;
                if (fixedRef.HasValue) x = fixedRef.Value - t * fixedRef.Value.Dot(t);
                else if (i == 0) x = Math.Abs(t.Z) < 0.95 ? Vec3.UnitZ.Cross(t) : t.AnyPerpendicular();
                else x = prevX - t * prevX.Dot(t);   // parallel transport
                if (x.LengthSq < 1e-16) x = t.AnyPerpendicular();
                x = x.Normalized();
                prevX = x;
                frames.Add((x, t.Cross(x)));
            }
            var loops = new List<List<Vec2>> { l.Outer };
            loops.AddRange(l.Holes);
            Vec3 At(Vec2 p, int i) => pts[i] + frames[i].x * p.X + frames[i].y * p.Y;
            foreach (List<Vec2> loop in loops)
                for (int s = 0; s < n - 1; s++)
                    for (int i = 0; i < loop.Count; i++)
                    {
                        Vec2 p = loop[i], q = loop[(i + 1) % loop.Count];
                        Vec3 a = At(p, s), b = At(q, s), c = At(q, s + 1), d = At(p, s + 1);
                        m.Add(a, b, c); m.Add(a, c, d);
                    }
            var holes = l.Holes.Cast<IList<Vec2>>().ToList();
            var all = new List<Vec2>(l.Outer);
            foreach (var h in l.Holes) all.AddRange(h);
            List<int> tri = Triangulator.Triangulate(l.Outer, holes);
            for (int i = 0; i + 2 < tri.Count; i += 3)
            {
                m.Add(At(all[tri[i]], 0), At(all[tri[i + 2]], 0), At(all[tri[i + 1]], 0));
                m.Add(At(all[tri[i]], n - 1), At(all[tri[i + 1]], n - 1), At(all[tri[i + 2]], n - 1));
            }
        }

        // ================================================================== B-reps and face sets

        /// <summary>Faces of an IfcClosedShell / IfcOpenShell / IfcConnectedFaceSet.</summary>
        private Mesh Shell(int id)
        {
            StepEntity sh = S.Get(id);
            if (sh == null) return null;
            List<int> faces = sh.Refs(0);
            var m = new Mesh(faces.Count * 6);
            foreach (int f in faces)
            {
                try { Face(m, f); }
                catch (Exception ex) { Miss("face (erro)"); Log.Error("face #" + f, ex); }
            }
            return m;
        }

        private void Face(Mesh m, int id)
        {
            StepEntity f = S.Get(id);
            if (f == null) return;
            List<int> bounds = f.Refs(0);
            bool advanced = f.Type == "IFCADVANCEDFACE" || f.Type == "IFCFACESURFACE";
            if (!advanced && bounds.Count == 1)
            {
                // fast path: one poly loop (the bulk of faceted breps)
                StepEntity b = S.Get(bounds[0]);
                if (b == null) return;
                StepEntity loop = S.Get(b.Ref(0));
                if (loop == null || loop.Type != "IFCPOLYLOOP") return;
                List<int> ps = loop.Refs(0);
                bool rev = b.Bool(1) == false;
                if (ps.Count == 3)
                {
                    Vec3 a = Point(ps[0]), bb = Point(ps[1]), c = Point(ps[2]);
                    if (rev) m.Add(a, c, bb); else m.Add(a, bb, c);
                    return;
                }
                var pts = ps.Select(Point).ToList();
                if (rev) pts.Reverse();
                Triangulator.AddFace(m, pts);
                return;
            }
            List<Vec3> outer = null;
            var holes = new List<IList<Vec3>>();
            foreach (int bid in bounds)
            {
                StepEntity b = S.Get(bid);
                if (b == null) continue;
                List<Vec3> pts = LoopPoints(b.Ref(0));
                if (pts.Count < 3) continue;
                if (b.Bool(1) == false) pts.Reverse();
                if (b.Type == "IFCFACEOUTERBOUND" && outer == null) outer = pts;
                else holes.Add(pts);
            }
            if (outer == null)
            {
                if (holes.Count == 0) return;
                // the largest loop is the outer one
                int best = 0; double ba = -1;
                for (int i = 0; i < holes.Count; i++) { double a = Triangulator.Newell(holes[i]).Length; if (a > ba) { ba = a; best = i; } }
                outer = (List<Vec3>)holes[best];
                holes.RemoveAt(best);
            }
            if (!advanced) { Triangulator.AddFace(m, outer, holes); return; }
            StepEntity surf = S.Get(f.Ref(1));
            bool same = f.Bool(2) != false;
            var fm = new Mesh();
            switch (surf?.Type)
            {
                case "IFCCYLINDRICALSURFACE":
                {
                    Mat fr = Axis(surf.Ref(0));
                    double r = surf.Num(1) * L;
                    ParametricFace(fm, outer, holes,
                        p => { Vec3 d = p - fr.T; return new Vec2(Math.Atan2(d.Dot(fr.Y), d.Dot(fr.X)), d.Dot(fr.Z)); },
                        uv => fr.Point(new Vec3(r * Math.Cos(uv.X), r * Math.Sin(uv.X), uv.Y)), true, false, r);
                    break;
                }
                case "IFCSPHERICALSURFACE":
                {
                    Mat fr = Axis(surf.Ref(0));
                    double r = surf.Num(1) * L;
                    ParametricFace(fm, outer, holes,
                        p => { Vec3 d = p - fr.T; return new Vec2(Math.Atan2(d.Dot(fr.Y), d.Dot(fr.X)), Math.Asin(Math.Max(-1, Math.Min(1, d.Dot(fr.Z) / Math.Max(r, 1e-12))))); },
                        uv => fr.Point(new Vec3(r * Math.Cos(uv.Y) * Math.Cos(uv.X), r * Math.Cos(uv.Y) * Math.Sin(uv.X), r * Math.Sin(uv.Y))), true, true, r);
                    break;
                }
                case "IFCBSPLINESURFACEWITHKNOTS":
                case "IFCRATIONALBSPLINESURFACEWITHKNOTS":
                    if (!BSplineSurface(fm, surf)) Triangulator.AddFace(fm, outer, holes);
                    else same = true;   // grid follows the surface's own orientation
                    break;
                default:
                    Triangulator.AddFace(fm, outer, holes);
                    same = true;        // planar: the loop already gives the orientation
                    break;
            }
            if (!same) fm.Flip();
            m.AddAll(fm);
        }

        /// <summary>Points of an IfcPolyLoop or IfcEdgeLoop (edges sampled along their curves).</summary>
        private List<Vec3> LoopPoints(int id)
        {
            var r = new List<Vec3>();
            StepEntity loop = S.Get(id);
            if (loop == null) return r;
            if (loop.Type == "IFCPOLYLOOP") { foreach (int p in loop.Refs(0)) r.Add(Point(p)); return r; }
            if (loop.Type != "IFCEDGELOOP") return r;
            foreach (int oeId in loop.Refs(0))
            {
                StepEntity oe = S.Get(oeId);
                if (oe == null) continue;
                StepEntity edge = oe.Type == "IFCORIENTEDEDGE" ? S.Get(oe.Ref(2)) : oe;
                bool orient = oe.Type != "IFCORIENTEDEDGE" || oe.Bool(3) != false;
                if (edge == null) continue;
                List<Vec3> pts = EdgePoints(edge);
                if (!orient) pts.Reverse();
                if (pts.Count > 0) pts.RemoveAt(pts.Count - 1);
                Append(r, pts);
            }
            return r;
        }

        private List<Vec3> EdgePoints(StepEntity edge)
        {
            Vec3 a = Vertex(edge.Ref(0)), b = Vertex(edge.Ref(1));
            var r = new List<Vec3>();
            if (edge.Type != "IFCEDGECURVE") { r.Add(a); r.Add(b); return r; }
            bool same = edge.Bool(3) != false;
            StepEntity c = S.Get(edge.Ref(2));
            string t = c?.Type;
            if (t == "IFCCIRCLE" || t == "IFCELLIPSE")
            {
                Mat f = Axis(c.Ref(0));
                double ra = c.Num(1) * L, rb = t == "IFCELLIPSE" ? c.Num(2) * L : ra;
                double Ang(Vec3 p) { Vec3 d = p - f.T; return Math.Atan2(d.Dot(f.Y) / rb, d.Dot(f.X) / ra); }
                Vec3 s0 = same ? a : b, s1 = same ? b : a;
                double a1 = Ang(s0), a2 = Ang(s1);
                while (a2 <= a1 + 1e-9) a2 += 2 * Math.PI;
                int steps = Math.Max(1, (int)Math.Ceiling((a2 - a1) / (2 * Math.PI / Segments)));
                for (int i = 0; i <= steps; i++) { double ang = a1 + (a2 - a1) * i / steps; r.Add(f.Point(new Vec3(ra * Math.Cos(ang), rb * Math.Sin(ang), 0))); }
                r[0] = s0; r[r.Count - 1] = s1;
                if (!same) r.Reverse();
                return r;
            }
            if (t == "IFCPOLYLINE" || t == "IFCBSPLINECURVEWITHKNOTS" || t == "IFCRATIONALBSPLINECURVEWITHKNOTS" || t == "IFCTRIMMEDCURVE" || t == "IFCINDEXEDPOLYCURVE" || t == "IFCCOMPOSITECURVE")
            {
                List<Vec3> all = Curve(c.Id);
                if (all.Count >= 2)
                {
                    if ((all[0] - a).LengthSq + (all[all.Count - 1] - b).LengthSq > (all[0] - b).LengthSq + (all[all.Count - 1] - a).LengthSq) all.Reverse();
                    all[0] = a; all[all.Count - 1] = b;
                    return all;
                }
            }
            r.Add(a); r.Add(b);
            return r;
        }

        private Vec3 Vertex(int id)
        {
            StepEntity v = S.Get(id);
            return v != null && v.Type == "IFCVERTEXPOINT" ? Point(v.Ref(0)) : Vec3.Zero;
        }

        /// <summary>Face on a curved surface: loops mapped to (u, v), triangulated there, refined and mapped back.</summary>
        private void ParametricFace(Mesh m, List<Vec3> outer, List<IList<Vec3>> holes, Func<Vec3, Vec2> toUv, Func<Vec2, Vec3> fromUv, bool wrapU, bool wrapV, double radius)
        {
            List<Vec2> Map(IList<Vec3> loop)
            {
                var l = new List<Vec2>(loop.Count);
                foreach (Vec3 p in loop)
                {
                    Vec2 uv = toUv(p);
                    if (l.Count > 0)
                    {
                        Vec2 prev = l[l.Count - 1];
                        if (wrapU) { while (uv.X - prev.X > Math.PI) uv.X -= 2 * Math.PI; while (uv.X - prev.X < -Math.PI) uv.X += 2 * Math.PI; }
                    }
                    l.Add(uv);
                }
                return l;
            }
            List<Vec2> o2 = Map(outer);
            var h2 = holes.Select(h => (IList<Vec2>)Map(h)).ToList();
            var all = new List<Vec2>(o2);
            foreach (var h in h2) all.AddRange(h);
            // scale u by the radius so the triangulation works in true lengths
            var sc = all.Select(p => new Vec2(p.X * radius, wrapV ? p.Y * radius : p.Y)).ToList();
            var so = sc.GetRange(0, o2.Count);
            var sh = new List<IList<Vec2>>();
            int k = o2.Count;
            foreach (var h in h2) { sh.Add(sc.GetRange(k, h.Count)); k += h.Count; }
            List<int> tri = Triangulator.Triangulate(so, sh);
            double maxStep = 2 * Math.PI / Segments;
            for (int i = 0; i + 2 < tri.Count; i += 3)
                Refine(m, all[tri[i]], all[tri[i + 1]], all[tri[i + 2]], fromUv, maxStep, wrapV, 0);
        }

        private static void Refine(Mesh m, Vec2 a, Vec2 b, Vec2 c, Func<Vec2, Vec3> f, double maxStep, bool refineV, int depth)
        {
            double la = Math.Abs(a.X - b.X), lb = Math.Abs(b.X - c.X), lc = Math.Abs(c.X - a.X);
            double lmax = Math.Max(la, Math.Max(lb, lc));
            double va = Math.Abs(a.Y - b.Y), vb = Math.Abs(b.Y - c.Y), vc = Math.Abs(c.Y - a.Y);
            if (depth >= 4 || lmax <= maxStep && (!refineV || Math.Max(va, Math.Max(vb, vc)) <= maxStep))
            {
                m.Add(f(a), f(b), f(c));
                return;
            }
            Vec2 ab = (a + b) * 0.5, bc = (b + c) * 0.5, ca = (c + a) * 0.5;
            Refine(m, a, ab, ca, f, maxStep, refineV, depth + 1);
            Refine(m, ab, b, bc, f, maxStep, refineV, depth + 1);
            Refine(m, ca, bc, c, f, maxStep, refineV, depth + 1);
            Refine(m, ab, bc, ca, f, maxStep, refineV, depth + 1);
        }

        /// <summary>Untrimmed B-spline surface over its whole domain (good enough for the usual one-face patches).</summary>
        private bool BSplineSurface(Mesh m, StepEntity s)
        {
            // IfcBSplineSurfaceWithKnots: UDegree, VDegree, ControlPointsList, SurfaceForm, UClosed, VClosed, SelfIntersect,
            //   UMultiplicities, VMultiplicities, UKnots, VKnots, KnotSpec [, WeightsData]
            int pu = (int)s.Num(0), pv = (int)s.Num(1);
            var rows = new List<List<Vec3>>();
            foreach (object row in s.List(2))
                if (row is object[] ro) rows.Add(ro.OfType<StepRef>().Select(r => Point(r.Id)).ToList());
            if (rows.Count < 2 || rows[0].Count < 2) return false;
            List<double> Knots(int mi, int ki)
            {
                var mult = s.List(mi).Select(o => (int)StepFile.AsNumber(o)).ToList();
                var kv = s.List(ki).Select(o => StepFile.AsNumber(o)).ToList();
                var k = new List<double>();
                for (int i = 0; i < kv.Count && i < mult.Count; i++) for (int j = 0; j < mult[i]; j++) k.Add(kv[i]);
                return k;
            }
            List<double> ku = Knots(7, 9), kvv = Knots(8, 10);
            int nu = rows.Count, nv = rows[0].Count;
            if (ku.Count != nu + pu + 1 || kvv.Count != nv + pv + 1) return false;
            List<List<double>> w = null;
            if (s.Type == "IFCRATIONALBSPLINESURFACEWITHKNOTS")
            {
                w = new List<List<double>>();
                foreach (object row in s.List(12)) if (row is object[] ro) w.Add(ro.Select(o => StepFile.AsNumber(o, 1)).ToList());
            }
            int su = Math.Max(6, Math.Min(48, nu * 3)), sv = Math.Max(6, Math.Min(48, nv * 3));
            var grid = new Vec3[su + 1, sv + 1];
            for (int i = 0; i <= su; i++)
            {
                double u = ku[pu] + (ku[nu] - ku[pu]) * i / su;
                // evaluate each column along u, then along v
                var col = new List<Vec3>(nv);
                var colW = w != null ? new List<double>(nv) : null;
                for (int j = 0; j < nv; j++)
                {
                    var ctrl = new List<Vec3>(nu);
                    var ww = w != null ? new List<double>(nu) : null;
                    for (int r = 0; r < nu; r++) { ctrl.Add(rows[r][Math.Min(j, rows[r].Count - 1)]); if (ww != null) ww.Add(r < w.Count && j < w[r].Count ? w[r][j] : 1); }
                    // homogeneous evaluation keeps rational surfaces exact
                    if (ww != null)
                    {
                        var hc = ctrl.Select((p, r) => p * ww[r]).ToList();
                        Vec3 num = DeBoor(pu, ku, hc, null, u);
                        double den = DeBoor(pu, ku, ww.Select(x => new Vec3(x, 0, 0)).ToList(), null, u).X;
                        col.Add(den != 0 ? num / den : num); colW.Add(den);
                    }
                    else col.Add(DeBoor(pu, ku, ctrl, null, u));
                }
                for (int j = 0; j <= sv; j++)
                {
                    double v = kvv[pv] + (kvv[nv] - kvv[pv]) * j / sv;
                    grid[i, j] = DeBoor(pv, kvv, col, colW, v);
                }
            }
            for (int i = 0; i < su; i++)
                for (int j = 0; j < sv; j++)
                {
                    m.Add(grid[i, j], grid[i + 1, j], grid[i + 1, j + 1]);
                    m.Add(grid[i, j], grid[i + 1, j + 1], grid[i, j + 1]);
                }
            return true;
        }

        private List<Vec3> PointList(int id)
        {
            var pts = new List<Vec3>();
            StepEntity pl = S.Get(id);
            if (pl == null) return pts;
            foreach (object o in pl.List(0)) if (o is object[] c) pts.Add(Coords(c));
            return pts;
        }

        private Mesh TriangulatedFaceSet(StepEntity e, out bool closed)
        {
            List<Vec3> pts = PointList(e.Ref(0));
            // IFC4: Coordinates, Normals, Closed, CoordIndex, PnIndex — IFC4x3: Coordinates, Closed, Normals, CoordIndex, PnIndex
            closed = e.Bool(2) == true || e.Bool(1) == true;
            object[] idx = e.List(3);
            object[] pn = e.List(4);
            var map = pn.Length > 0 ? pn.Select(o => (int)StepFile.AsNumber(o)).ToArray() : null;
            Vec3 At(object o)
            {
                int i = (int)StepFile.AsNumber(o);
                if (map != null) i = i >= 1 && i <= map.Length ? map[i - 1] : 0;
                return i >= 1 && i <= pts.Count ? pts[i - 1] : Vec3.Zero;
            }
            var m = new Mesh(idx.Length * 3);
            foreach (object t in idx)
                if (t is object[] tri && tri.Length >= 3) m.Add(At(tri[0]), At(tri[1]), At(tri[2]));
            return m;
        }

        private Mesh PolygonalFaceSet(StepEntity e)
        {
            List<Vec3> pts = PointList(e.Ref(0));
            object[] pn = e.List(3);
            var map = pn.Length > 0 ? pn.Select(o => (int)StepFile.AsNumber(o)).ToArray() : null;
            Vec3 At(object o)
            {
                int i = (int)StepFile.AsNumber(o);
                if (map != null) i = i >= 1 && i <= map.Length ? map[i - 1] : 0;
                return i >= 1 && i <= pts.Count ? pts[i - 1] : Vec3.Zero;
            }
            var m = new Mesh();
            foreach (int fid in e.Refs(2))
            {
                StepEntity f = S.Get(fid);
                if (f == null) continue;
                var outer = f.List(0).Select(At).ToList();
                var holes = new List<IList<Vec3>>();
                if (f.Type == "IFCINDEXEDPOLYGONALFACEWITHVOIDS")
                    foreach (object h in f.List(1)) if (h is object[] hl) holes.Add(hl.Select(At).ToList());
                Triangulator.AddFace(m, outer, holes);
            }
            return m;
        }

        // ================================================================== booleans

        private Mesh Boolean(StepEntity e)
        {
            string op = e.Enum(0) ?? "DIFFERENCE";
            Mesh a = Solid(e.Ref(1), out _);
            if (a == null || a.IsEmpty) return op == "UNION" ? Solid(e.Ref(2), out _) : null;
            a.Orient();
            int bid = e.Ref(2);
            StepEntity b = S.Get(bid);
            if (b == null) return a;
            Mesh region;
            if (b.Type == "IFCHALFSPACESOLID" || b.Type == "IFCBOXEDHALFSPACE" || b.Type == "IFCPOLYGONALBOUNDEDHALFSPACE")
            {
                if (op != "DIFFERENCE" && op != "INTERSECTION") return a;
                region = HalfSpace(b, a.Bounds, out bool untouched, out bool all);
                if (op == "DIFFERENCE") { if (untouched) return a; if (all) return null; }
                else { if (untouched) return null; if (all) return a; }
                if (region == null) return a;
            }
            else
            {
                region = Solid(bid, out bool closedB);
                if (region == null || region.IsEmpty) return op == "INTERSECTION" ? null : a;
                region.Orient();
                if (op == "DIFFERENCE" && !a.Bounds.Intersects(region.Bounds, 1e-6)) return a;
            }
            Mesh r = op == "UNION" ? Csg.Union(a, region) : op == "INTERSECTION" ? Csg.Intersect(a, region) : Csg.Subtract(a, region);
            return r ?? a;
        }

        /// <summary>The part of the half space near the solid's box, as a closed mesh. untouched: the box lies outside
        /// the half space; all: the box lies wholly inside an unbounded half space.</summary>
        private Mesh HalfSpace(StepEntity hs, Bounds box, out bool untouched, out bool all)
        {
            untouched = false; all = false;
            StepEntity plane = S.Get(hs.Ref(0));
            if (plane == null || plane.Type != "IFCPLANE") { Miss("meio-espaço sem plano"); untouched = true; return null; }
            Mat pf = Axis(plane.Ref(0));
            Vec3 n = pf.Z, o = pf.T;
            bool agree = hs.Bool(1) != false;
            Vec3 side = agree ? -n : n;                  // direction into the half space
            // classify the solid's box corners
            int inside = 0, outside = 0;
            for (int i = 0; i < 8; i++)
            {
                Vec3 c = new Vec3((i & 1) == 0 ? box.Min.X : box.Max.X, (i & 2) == 0 ? box.Min.Y : box.Max.Y, (i & 4) == 0 ? box.Min.Z : box.Max.Z);
                double d = (c - o).Dot(side);
                if (d > 1e-6) inside++; else if (d < -1e-6) outside++;
            }
            if (inside == 0) { untouched = true; return null; }
            bool bounded = hs.Type == "IFCPOLYGONALBOUNDEDHALFSPACE";
            if (outside == 0 && !bounded) { all = true; return null; }
            double R = box.Diagonal + 1.0;
            Vec3 c0 = box.Center - n * (box.Center - o).Dot(n);
            Vec3 u = n.AnyPerpendicular(), v = n.Cross(u);
            Mesh half = Csg.Box(c0 - u * R - v * R, u * (2 * R), v * (2 * R), side * (2 * R + Math.Abs((box.Center - o).Dot(n))));
            if (!bounded) return half;
            Mat pos = Axis(hs.Ref(2));
            List<Vec2> poly = Flat(Curve(hs.Ref(3)));
            if (poly.Count < 3) { untouched = true; return null; }
            double H = (box.Center - pos.T).Length + R;
            var prism = new Mesh();
            ExtrudeLoops(prism, new Loops { Outer = poly }, new Vec3(0, 0, 2 * H));
            prism = prism.Transformed(pos * Mat.Translation(new Vec3(0, 0, -H)));
            prism.Orient();
            return Csg.Intersect(half, prism) ?? prism;
        }
    }
}
