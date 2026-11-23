using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JA.IfcViewer.Geometry;
using JA.IfcViewer.Step;

namespace JA.IfcViewer.Ifc
{
    /// <summary>A coloured triangle soup ready for the viewer: float xyz per vertex, three vertices per triangle, in scene
    /// coordinates (world metres minus the scene origin).</summary>
    public sealed class MeshPart
    {
        public uint Color;
        public float[] P;
        public int TriangleCount => P.Length / 9;
    }

    /// <summary>Any IfcProduct (element or spatial structure) or the IfcProject.</summary>
    public sealed class IfcObject
    {
        public IfcModel Model;
        public int Id;
        public string Type;          // IFCWALLSTANDARDCASE
        public string Class;         // IfcWallStandardCase
        public string GlobalId, Name, Description, ObjectType, Tag, LongName, PredefinedType;
        public bool IsSpatial;
        public IfcObject Parent;
        public readonly List<IfcObject> Children = new List<IfcObject>();
        public IfcObject Storey;
        public double Elevation;     // storeys: world z of the storey (metres)
        public int PlacementId, RepresentationId, TypeId;
        public readonly List<int> PropertyDefs = new List<int>();
        public List<int> Openings;

        // geometry
        public List<GeoPart> World;  // while loading (double precision, world)
        public MeshPart[] Parts;
        public Bounds Bounds = Bounds.Empty;     // scene coordinates
        public int Index = -1;                   // index in the scene
        public bool HasGeometry => Parts != null && Parts.Length > 0;

        internal Dictionary<string, PropValue> FlatCache;

        public string DisplayName
        {
            get
            {
                string n = !string.IsNullOrWhiteSpace(Name) ? Name : !string.IsNullOrWhiteSpace(LongName) ? LongName : null;
                if (IsSpatial && !string.IsNullOrWhiteSpace(LongName) && !string.IsNullOrWhiteSpace(Name) && LongName != Name) n = Name + " — " + LongName;
                return n ?? Class;
            }
        }

        public string TypeName => TypeId > 0 && Model.Types.TryGetValue(TypeId, out IfcTypeInfo t) ? t.Name : null;
        public override string ToString() => Class + " " + DisplayName;
    }

    public sealed class IfcTypeInfo
    {
        public int Id;
        public string Class, Name;
        public List<int> Psets = new List<int>();
    }

    /// <summary>One IFC file: units, objects, spatial tree, relations and (after loading) geometry.</summary>
    public sealed class IfcModel
    {
        public string Path, FileName;
        public StepFile Step;
        public string Schema => Step?.Schema ?? "";
        public double LengthScale = 1, AngleScale = 1;
        public string LengthUnit = "m", AreaUnit = "m²", VolumeUnit = "m³", AngleUnit = "rad", MassUnit = "kg";
        public IfcObject Project;
        public readonly List<IfcObject> Objects = new List<IfcObject>();
        public readonly Dictionary<int, IfcObject> ById = new Dictionary<int, IfcObject>();
        public readonly Dictionary<int, IfcTypeInfo> Types = new Dictionary<int, IfcTypeInfo>();
        public readonly List<IfcObject> Storeys = new List<IfcObject>();
        internal readonly Dictionary<int, List<int>> MaterialOf = new Dictionary<int, List<int>>();
        internal readonly Dictionary<int, List<int>> ClassificationOf = new Dictionary<int, List<int>>();
        internal readonly Dictionary<int, string> LayerOf = new Dictionary<int, string>();
        internal readonly Dictionary<int, uint> ItemColors = new Dictionary<int, uint>();
        internal readonly Dictionary<int, uint> MaterialColors = new Dictionary<int, uint>();
        public Dictionary<string, int> Unsupported = new Dictionary<string, int>();
        public int Index;
        public bool Visible = true;
        public int FailedElements, OpeningFailures;
        public TimeSpan LoadTime;

        private GeometryBuilder _geo;

        public static IfcModel Load(string path, Action<string, double> progress, CancellationToken ct)
        {
            var t0 = DateTime.Now;
            var m = new IfcModel { Path = path, FileName = System.IO.Path.GetFileName(path) };
            progress?.Invoke("Lendo " + m.FileName, 0);
            byte[] data = ReadBytes(path);
            m.Step = StepFile.Load(data, f => progress?.Invoke("Lendo " + m.FileName, f * 0.25));
            ct.ThrowIfCancellationRequested();
            progress?.Invoke("Organizando o modelo", 0.26);
            m.ReadUnits();
            m.ReadObjects();
            m.ReadRelations();
            m.ReadStyles();
            ct.ThrowIfCancellationRequested();
            m.BuildGeometry(progress, ct);
            m.LoadTime = DateTime.Now - t0;
            return m;
        }

        private static byte[] ReadBytes(string path)
        {
            if (path.EndsWith(".ifczip", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using (ZipArchive z = ZipFile.OpenRead(path))
                {
                    ZipArchiveEntry e = z.Entries.FirstOrDefault(x => x.FullName.EndsWith(".ifc", StringComparison.OrdinalIgnoreCase))
                                        ?? z.Entries.OrderByDescending(x => x.Length).FirstOrDefault();
                    if (e == null) throw new InvalidDataException("O arquivo .ifczip está vazio.");
                    using (Stream s = e.Open())
                    using (var ms = new MemoryStream((int)Math.Min(int.MaxValue, Math.Max(1024, e.Length))))
                    {
                        s.CopyTo(ms);
                        return ms.ToArray();
                    }
                }
            }
            if (path.EndsWith(".ifcxml", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("IFC em XML (.ifcxml) não é lido; exporte em .ifc.");
            return File.ReadAllBytes(path);
        }

        // ================================================================== units

        private void ReadUnits()
        {
            int pid = Step.OfType("IFCPROJECT").FirstOrDefault();
            StepEntity proj = Step.Get(pid);
            if (proj == null) return;
            StepEntity ua = Step.Get(proj.Ref(8));
            if (ua == null) return;
            foreach (int uid in ua.Refs(0))
            {
                StepEntity u = Step.Get(uid);
                if (u == null) continue;
                string kind = u.Enum(1);
                if (u.Type == "IFCSIUNIT")
                {
                    string prefix = u.Enum(2), name = u.Enum(3);
                    double f = Prefix(prefix);
                    string ps = PrefixSymbol(prefix);
                    switch (kind)
                    {
                        case "LENGTHUNIT": LengthScale = f; LengthUnit = ps + "m"; break;
                        case "AREAUNIT": AreaUnit = ps + "m²"; break;
                        case "VOLUMEUNIT": VolumeUnit = ps + "m³"; break;
                        case "PLANEANGLEUNIT": AngleScale = 1; AngleUnit = "rad"; break;
                        case "MASSUNIT": MassUnit = ps + "g"; break;
                    }
                }
                else if (u.Type == "IFCCONVERSIONBASEDUNIT" || u.Type == "IFCCONVERSIONBASEDUNITWITHOFFSET")
                {
                    string name = (u.Str(2) ?? "").ToUpperInvariant();
                    StepEntity mw = Step.Get(u.Ref(3));
                    double factor = mw != null ? mw.Num(0, 1) : 1;
                    if (mw != null)
                    {
                        StepEntity baseU = Step.Get(mw.Ref(1));
                        if (baseU != null && baseU.Type == "IFCSIUNIT") factor *= Prefix(baseU.Enum(2));
                    }
                    switch (kind)
                    {
                        case "LENGTHUNIT": LengthScale = factor; LengthUnit = name.Contains("FOOT") ? "ft" : name.Contains("INCH") ? "in" : name.ToLowerInvariant(); break;
                        case "AREAUNIT": AreaUnit = name.Contains("FOOT") ? "ft²" : name.ToLowerInvariant(); break;
                        case "VOLUMEUNIT": VolumeUnit = name.Contains("FOOT") ? "ft³" : name.ToLowerInvariant(); break;
                        case "PLANEANGLEUNIT":
                            AngleScale = factor > 0 ? factor : Math.PI / 180;
                            AngleUnit = name.Contains("DEGREE") || Math.Abs(AngleScale - Math.PI / 180) < 1e-6 ? "°" : name.ToLowerInvariant();
                            break;
                    }
                }
            }
            if (LengthScale <= 0) LengthScale = 1;
        }

        private static double Prefix(string p)
        {
            switch (p)
            {
                case "EXA": return 1e18; case "PETA": return 1e15; case "TERA": return 1e12; case "GIGA": return 1e9; case "MEGA": return 1e6;
                case "KILO": return 1e3; case "HECTO": return 1e2; case "DECA": return 1e1; case "DECI": return 1e-1; case "CENTI": return 1e-2;
                case "MILLI": return 1e-3; case "MICRO": return 1e-6; case "NANO": return 1e-9;
            }
            return 1;
        }

        private static string PrefixSymbol(string p)
        {
            switch (p) { case "KILO": return "k"; case "DECI": return "d"; case "CENTI": return "c"; case "MILLI": return "m"; case "MICRO": return "µ"; }
            return "";
        }

        // ================================================================== objects

        private static readonly HashSet<string> Skip = new HashSet<string>
        {
            "IFCDISTRIBUTIONPORT", "IFCPORT", "IFCOPENINGELEMENT", "IFCOPENINGSTANDARDCASE", "IFCVOIDINGFEATURE", "IFCVIRTUALELEMENT",
            "IFCANNOTATION", "IFCGRID", "IFCSTRUCTURALPOINTCONNECTION", "IFCSTRUCTURALCURVEMEMBER", "IFCSTRUCTURALSURFACEMEMBER",
            "IFCSTRUCTURALPOINTACTION", "IFCSTRUCTURALCURVEACTION", "IFCSTRUCTURALSURFACEACTION", "IFCSTRUCTURALCURVECONNECTION",
            "IFCSTRUCTURALSURFACECONNECTION", "IFCSTRUCTURALPOINTREACTION", "IFCSTRUCTURALCURVEREACTION", "IFCALIGNMENT", "IFCREFERENT"
        };

        /// <summary>Whether instances of the type are IfcProducts: GlobalId, …, ObjectPlacement (5), Representation (6).</summary>
        private bool IsProductType(string type)
        {
            IReadOnlyList<int> ids = Step.OfType(type);
            int any = 0;
            for (int i = 0; i < ids.Count && i < 3; i++)
            {
                StepEntity e = Step.Get(ids[i]);
                if (e == null || e.Count < 7) return false;
                if (!(e[0] is string g) || g.Length < 16 || g.Length > 24) return false;
                object p = e[5], r = e[6];
                if (p != null && !(p is StepRef)) return false;
                if (r != null && !(r is StepRef)) return false;
                if (p is StepRef pr) { string pt = Step.TypeOf(pr.Id); if (pt == null || !pt.EndsWith("PLACEMENT")) return false; any++; }
                if (r is StepRef rr) { string rt = Step.TypeOf(rr.Id); if (rt != "IFCPRODUCTDEFINITIONSHAPE") return false; any++; }
            }
            return any > 0 || IfcNames.IsSpatial(type);
        }

        private void ReadObjects()
        {
            var openingTypes = new[] { "IFCOPENINGELEMENT", "IFCOPENINGSTANDARDCASE" };
            foreach (string type in Step.Types.ToList())
            {
                if (type == "IFCPROJECT" || Skip.Contains(type) || !type.StartsWith("IFC")) continue;
                if (type.EndsWith("TYPE") || type.StartsWith("IFCREL") || type.EndsWith("STYLE")) continue;
                if (!IsProductType(type)) continue;
                foreach (int id in Step.OfType(type)) Add(Make(id, type));
            }
            int pid = Step.OfType("IFCPROJECT").FirstOrDefault();
            StepEntity p = Step.Get(pid);
            Project = new IfcObject
            {
                Model = this, Id = pid, Type = "IFCPROJECT", Class = "IfcProject", IsSpatial = true,
                GlobalId = p?.Str(0), Name = p?.Str(2), Description = p?.Str(3), ObjectType = p?.Str(4), LongName = p?.Str(5)
            };
            if (pid > 0) ById[pid] = Project;
            if (string.IsNullOrWhiteSpace(Project.Name)) Project.Name = System.IO.Path.GetFileNameWithoutExtension(FileName);
        }

        private void Add(IfcObject o)
        {
            if (o == null || ById.ContainsKey(o.Id)) return;
            Objects.Add(o);
            ById[o.Id] = o;
        }

        private IfcObject Make(int id, string type)
        {
            StepEntity e = Step.Get(id);
            if (e == null) return null;
            var o = new IfcObject
            {
                Model = this, Id = id, Type = type, Class = IfcNames.Pretty(type), IsSpatial = IfcNames.IsSpatial(type),
                GlobalId = e.Str(0), Name = e.Str(2), Description = e.Str(3), ObjectType = e.Str(4),
                PlacementId = e.Ref(5), RepresentationId = e.Ref(6)
            };
            if (o.IsSpatial) o.LongName = e.Str(7); else o.Tag = e.Str(7);
            if (e.Count > 8 && e[e.Count - 1] is StepEnum pe && pe.Value != "T" && pe.Value != "F") o.PredefinedType = pe.Value;
            return o;
        }

        // ================================================================== relations

        private void ReadRelations()
        {
            foreach (string rel in new[] { "IFCRELAGGREGATES", "IFCRELNESTS" })
                foreach (int id in Step.OfType(rel))
                {
                    StepEntity r = Step.Get(id);
                    if (r == null || !ById.TryGetValue(r.Ref(4), out IfcObject parent)) continue;
                    foreach (int c in r.Refs(5))
                        if (ById.TryGetValue(c, out IfcObject child) && child.Parent == null && child != parent) { child.Parent = parent; parent.Children.Add(child); }
                }
            foreach (int id in Step.OfType("IFCRELCONTAINEDINSPATIALSTRUCTURE"))
            {
                StepEntity r = Step.Get(id);
                if (r == null || !ById.TryGetValue(r.Ref(5), out IfcObject parent)) continue;
                foreach (int c in r.Refs(4))
                    if (ById.TryGetValue(c, out IfcObject child) && child.Parent == null && child != parent) { child.Parent = parent; parent.Children.Add(child); }
            }
            // orphans: spatial ones under the project, the rest too (shown as "sem pavimento")
            foreach (IfcObject o in Objects)
                if (o.Parent == null) { o.Parent = Project; Project.Children.Add(o); }
            // storey of each object
            foreach (IfcObject o in Objects)
            {
                IfcObject p = o.Parent;
                int guard = 0;
                while (p != null && guard++ < 64) { if (IfcNames.IsStoreyLike(p.Type)) break; p = p.Parent; }
                o.Storey = IfcNames.IsStoreyLike(o.Type) ? o : p;
            }

            foreach (int id in Step.OfType("IFCRELVOIDSELEMENT"))
            {
                StepEntity r = Step.Get(id);
                if (r == null || !ById.TryGetValue(r.Ref(4), out IfcObject el)) continue;
                (el.Openings ?? (el.Openings = new List<int>())).Add(r.Ref(5));
            }
            foreach (int id in Step.OfType("IFCRELDEFINESBYTYPE"))
            {
                StepEntity r = Step.Get(id);
                if (r == null) continue;
                int tid = r.Ref(5);
                if (!Types.ContainsKey(tid))
                {
                    StepEntity t = Step.Get(tid);
                    if (t == null) continue;
                    Types[tid] = new IfcTypeInfo { Id = tid, Class = IfcNames.Pretty(t.Type), Name = t.Str(2) ?? IfcNames.Pretty(t.Type), Psets = t.Refs(5) };
                }
                foreach (int c in r.Refs(4)) if (ById.TryGetValue(c, out IfcObject o)) o.TypeId = tid;
            }
            foreach (int id in Step.OfType("IFCRELDEFINESBYPROPERTIES"))
            {
                StepEntity r = Step.Get(id);
                if (r == null) continue;
                var defs = new List<int>();
                if (r[5] is StepRef one) defs.Add(one.Id);
                else defs.AddRange(r.Refs(5));
                foreach (int c in r.Refs(4))
                    if (ById.TryGetValue(c, out IfcObject o)) o.PropertyDefs.AddRange(defs);
                    else if (Types.TryGetValue(c, out IfcTypeInfo ti)) ti.Psets.AddRange(defs);
            }
            foreach (int id in Step.OfType("IFCRELASSOCIATESMATERIAL"))
            {
                StepEntity r = Step.Get(id);
                if (r == null) continue;
                int mat = r.Ref(5);
                foreach (int c in r.Refs(4)) { if (!MaterialOf.TryGetValue(c, out List<int> l)) MaterialOf[c] = l = new List<int>(); l.Add(mat); }
            }
            foreach (int id in Step.OfType("IFCRELASSOCIATESCLASSIFICATION"))
            {
                StepEntity r = Step.Get(id);
                if (r == null) continue;
                int cl = r.Ref(5);
                foreach (int c in r.Refs(4)) { if (!ClassificationOf.TryGetValue(c, out List<int> l)) ClassificationOf[c] = l = new List<int>(); l.Add(cl); }
            }
            foreach (string lt in new[] { "IFCPRESENTATIONLAYERASSIGNMENT", "IFCPRESENTATIONLAYERWITHSTYLE" })
                foreach (int id in Step.OfType(lt))
                {
                    StepEntity r = Step.Get(id);
                    if (r == null) continue;
                    string name = r.Str(0);
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    foreach (int it in r.Refs(2)) if (!LayerOf.ContainsKey(it)) LayerOf[it] = name;
                }
        }

        // ================================================================== styles

        private void ReadStyles()
        {
            foreach (int id in Step.OfType("IFCSTYLEDITEM"))
            {
                StepEntity s = Step.Get(id);
                if (s == null) continue;
                int item = s.Ref(0);
                if (item <= 0) continue;
                uint c = StyleColor(s.List(1), 0);
                if (c != 0) ItemColors[item] = c;
            }
            foreach (int id in Step.OfType("IFCINDEXEDCOLOURMAP"))
            {
                StepEntity s = Step.Get(id);
                if (s == null) continue;
                int item = s.Ref(0);
                if (item <= 0 || ItemColors.ContainsKey(item)) continue;
                StepEntity list = Step.Get(s.Ref(2));
                if (list == null) continue;
                object[] cols = list.List(0);
                var count = new Dictionary<int, int>();
                foreach (object ci in s.List(3)) { int k = (int)StepFile.AsNumber(ci); count[k] = count.TryGetValue(k, out int n) ? n + 1 : 1; }
                int best = count.Count > 0 ? count.OrderByDescending(kv => kv.Value).First().Key : 1;
                if (best >= 1 && best <= cols.Length && cols[best - 1] is object[] rgb && rgb.Length >= 3)
                {
                    double op = s.HasNum(1) ? s.Num(1) : 1;
                    ItemColors[item] = Rgb(StepFile.AsNumber(rgb[0]), StepFile.AsNumber(rgb[1]), StepFile.AsNumber(rgb[2]), 1 - op);
                }
            }
            foreach (int id in Step.OfType("IFCMATERIALDEFINITIONREPRESENTATION"))
            {
                StepEntity md = Step.Get(id);
                if (md == null) continue;
                int mat = md.Ref(3);
                foreach (int rep in md.Refs(2))
                {
                    StepEntity sr = Step.Get(rep);
                    if (sr == null) continue;
                    foreach (int si in sr.Refs(3))
                    {
                        StepEntity s = Step.Get(si);
                        if (s == null || s.Type != "IFCSTYLEDITEM") continue;
                        uint c = StyleColor(s.List(1), 0);
                        if (c != 0) { MaterialColors[mat] = c; break; }
                    }
                    if (MaterialColors.ContainsKey(mat)) break;
                }
            }
        }

        private uint StyleColor(object[] styles, int depth)
        {
            if (depth > 4) return 0;
            foreach (object o in styles)
            {
                if (!(o is StepRef r)) continue;
                StepEntity s = Step.Get(r.Id);
                if (s == null) continue;
                if (s.Type == "IFCPRESENTATIONSTYLEASSIGNMENT")
                {
                    uint c = StyleColor(s.List(0), depth + 1);
                    if (c != 0) return c;
                }
                else if (s.Type == "IFCSURFACESTYLE")
                {
                    foreach (int el in s.Refs(2))
                    {
                        StepEntity ss = Step.Get(el);
                        if (ss == null || (ss.Type != "IFCSURFACESTYLERENDERING" && ss.Type != "IFCSURFACESTYLESHADING")) continue;
                        StepEntity rgb = Step.Get(ss.Ref(0));
                        if (rgb == null || rgb.Type != "IFCCOLOURRGB") continue;
                        double tr = ss.HasNum(1) ? ss.Num(1) : 0;
                        return Rgb(rgb.Num(1), rgb.Num(2), rgb.Num(3), tr);
                    }
                }
            }
            return 0;
        }

        private static uint Rgb(double r, double g, double b, double transparency)
        {
            int C(double v) => (int)Math.Round(Math.Max(0, Math.Min(1, v)) * 255);
            int a = (int)Math.Round((1 - Math.Max(0, Math.Min(0.92, transparency))) * 255);
            return IfcNames.Argb(Math.Max(20, a), C(r), C(g), C(b));
        }

        /// <summary>Colour of the element's material (thickest layer, first constituent…), 0 when none.</summary>
        private uint MaterialColor(IfcObject o)
        {
            if (MaterialColors.Count == 0) return 0;
            uint c = 0;
            if (MaterialOf.TryGetValue(o.Id, out List<int> l)) foreach (int m in l) { c = MaterialColorOf(m, 0); if (c != 0) return c; }
            if (o.TypeId > 0 && MaterialOf.TryGetValue(o.TypeId, out l)) foreach (int m in l) { c = MaterialColorOf(m, 0); if (c != 0) return c; }
            return 0;
        }

        private uint MaterialColorOf(int id, int depth)
        {
            if (depth > 4) return 0;
            if (MaterialColors.TryGetValue(id, out uint c)) return c;
            StepEntity e = Step.Get(id);
            if (e == null) return 0;
            switch (e.Type)
            {
                case "IFCMATERIALLAYERSETUSAGE": return MaterialColorOf(e.Ref(0), depth + 1);
                case "IFCMATERIALPROFILESETUSAGE": return MaterialColorOf(e.Ref(0), depth + 1);
                case "IFCMATERIALLAYERSET":
                {
                    int best = 0; double bt = -1;
                    foreach (int ly in e.Refs(0))
                    {
                        StepEntity l = Step.Get(ly);
                        if (l == null) continue;
                        if (MaterialColors.ContainsKey(l.Ref(0)) && l.Num(1) > bt) { bt = l.Num(1); best = l.Ref(0); }
                    }
                    return best > 0 ? MaterialColors[best] : 0;
                }
                case "IFCMATERIALLIST":
                    foreach (int m in e.Refs(0)) { c = MaterialColorOf(m, depth + 1); if (c != 0) return c; }
                    return 0;
                case "IFCMATERIALCONSTITUENTSET":
                    foreach (int k in e.Refs(2)) { StepEntity mc = Step.Get(k); if (mc == null) continue; c = MaterialColorOf(mc.Ref(2), depth + 1); if (c != 0) return c; }
                    return 0;
                case "IFCMATERIALPROFILESET":
                    foreach (int k in e.Refs(2)) { StepEntity mp = Step.Get(k); if (mp == null) continue; c = MaterialColorOf(mp.Ref(2), depth + 1); if (c != 0) return c; }
                    return 0;
                case "IFCMATERIALLAYER":
                case "IFCMATERIALLAYERWITHOFFSETS":
                    return MaterialColorOf(e.Ref(0), depth + 1);
            }
            return 0;
        }

        // ================================================================== geometry

        private void BuildGeometry(Action<string, double> progress, CancellationToken ct)
        {
            _geo = new GeometryBuilder(Step, LengthScale, AngleScale, ItemColors);
            // storey elevations from their placements
            foreach (IfcObject o in Objects)
                if (IfcNames.IsStoreyLike(o.Type))
                {
                    o.Elevation = _geo.Placement(o.PlacementId).T.Z;
                    Storeys.Add(o);
                }
            Storeys.Sort((a, b) => a.Elevation.CompareTo(b.Elevation));

            var todo = Objects.Where(o => o.RepresentationId > 0).ToList();
            var openings = new ConcurrentDictionary<int, Mesh>();
            int done = 0, failed = 0, openFail = 0;
            int total = Math.Max(1, todo.Count);
            var po = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) };
            Parallel.ForEach(todo, po, o =>
            {
                try
                {
                    List<GeoPart> parts = _geo.ProductShape(o.RepresentationId);
                    if (parts.Count > 0)
                    {
                        Mat w = _geo.Placement(o.PlacementId);
                        uint fallback = MaterialColor(o);
                        if (fallback == 0) fallback = IfcNames.DefaultColor(o.Type);
                        var world = new List<GeoPart>(parts.Count);
                        foreach (GeoPart p in parts)
                            world.Add(new GeoPart(p.Color != 0 ? p.Color : fallback, w.IsIdentity ? p.Mesh : p.Mesh.Transformed(w), p.Closed));
                        if (o.Openings != null && o.Openings.Count > 0)
                        {
                            if (!CutOpenings(o, world, openings)) Interlocked.Increment(ref openFail);
                        }
                        o.World = world;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    Log.Error("elemento #" + o.Id + " " + o.Class, ex);
                }
                int d = Interlocked.Increment(ref done);
                if (d % 64 == 0) progress?.Invoke("Gerando a geometria (" + d + " de " + total + ")", 0.3 + 0.7 * d / total);
            });
            FailedElements = failed;
            OpeningFailures = openFail;
            Unsupported = _geo.Unsupported.ToDictionary(k => k.Key, k => k.Value);
        }

        private bool CutOpenings(IfcObject o, List<GeoPart> parts, ConcurrentDictionary<int, Mesh> cache)
        {
            bool ok = true;
            var ops = new List<Mesh>();
            foreach (int oid in o.Openings)
            {
                Mesh m = cache.GetOrAdd(oid, id =>
                {
                    StepEntity op = Step.Get(id);
                    if (op == null || op.Count < 7) return null;
                    var p = _geo.ProductShape(op.Ref(6));
                    if (p.Count == 0) return null;
                    Mat w = _geo.Placement(op.Ref(5));
                    var all = new Mesh();
                    foreach (GeoPart g in p) all.AddAll(w.IsIdentity ? g.Mesh : g.Mesh.Transformed(w));
                    all.Orient();
                    return all;
                });
                if (m != null && !m.IsEmpty) ops.Add(m);
            }
            if (ops.Count == 0) return true;
            for (int i = 0; i < parts.Count; i++)
            {
                GeoPart p = parts[i];
                if (!p.Closed || p.Mesh.TriangleCount > 20000) continue;
                Mesh cur = p.Mesh;
                cur.Orient();
                Bounds pb = cur.Bounds;
                foreach (Mesh op in ops)
                {
                    if (!pb.Intersects(op.Bounds, 1e-4)) continue;
                    try
                    {
                        Mesh r = Csg.Subtract(cur, op);
                        if (r != null && !r.IsEmpty) cur = r; else if (r == null) ok = false;
                    }
                    catch (Exception ex) { ok = false; Log.Error("abertura em #" + o.Id, ex); }
                }
                p.Mesh = cur;
            }
            return ok;
        }

        /// <summary>Robust centre for the scene origin: the median of the element centres.</summary>
        public Vec3 SuggestOrigin()
        {
            var xs = new List<double>(); var ys = new List<double>(); var zs = new List<double>();
            foreach (IfcObject o in Objects)
            {
                if (o.World == null) continue;
                Bounds b = Bounds.Empty;
                foreach (GeoPart p in o.World) { if (p.Mesh.P.Count > 0) { b.Add(p.Mesh.P[0]); b.Add(p.Mesh.P[p.Mesh.P.Count - 1]); } }
                if (b.IsEmpty) continue;
                Vec3 c = b.Center; xs.Add(c.X); ys.Add(c.Y); zs.Add(c.Z);
            }
            if (xs.Count == 0) return Vec3.Zero;
            double Med(List<double> l) { l.Sort(); return l[l.Count / 2]; }
            double z = Med(zs);
            return new Vec3(Math.Round(Med(xs)), Math.Round(Med(ys)), Math.Abs(z) > 500 ? Math.Round(z) : 0);
        }

        /// <summary>Converts the world geometry to scene floats (minus the origin) and frees the doubles.</summary>
        public void Bake(Vec3 origin)
        {
            foreach (IfcObject o in Objects)
            {
                if (o.World == null) continue;
                var list = new List<MeshPart>(o.World.Count);
                Bounds b = Bounds.Empty;
                foreach (GeoPart g in o.World)
                {
                    List<Vec3> P = g.Mesh.P;
                    int n = P.Count - P.Count % 3;
                    if (n < 3) continue;
                    var f = new float[n * 3];
                    for (int i = 0; i < n; i++)
                    {
                        Vec3 q = P[i] - origin;
                        f[i * 3] = (float)q.X; f[i * 3 + 1] = (float)q.Y; f[i * 3 + 2] = (float)q.Z;
                        b.Add(q);
                    }
                    // merge parts of the same colour
                    MeshPart same = list.FirstOrDefault(x => x.Color == g.Color);
                    if (same != null)
                    {
                        var merged = new float[same.P.Length + f.Length];
                        Array.Copy(same.P, merged, same.P.Length);
                        Array.Copy(f, 0, merged, same.P.Length, f.Length);
                        same.P = merged;
                    }
                    else list.Add(new MeshPart { Color = g.Color, P = f });
                }
                o.Parts = list.ToArray();
                o.Bounds = b;
                o.World = null;
            }
            foreach (IfcObject s in Storeys) s.Elevation -= origin.Z;
            SceneOrigin = origin;
        }

        public Vec3 SceneOrigin;

        public int TriangleCount => Objects.Where(o => o.Parts != null).Sum(o => o.Parts.Sum(p => p.TriangleCount));

        /// <summary>Layer (IfcPresentationLayerAssignment) of the object's body representation or its items.</summary>
        internal string LayerName(IfcObject o)
        {
            if (LayerOf.Count == 0 || o.RepresentationId <= 0) return null;
            StepEntity shape = Step.Get(o.RepresentationId);
            if (shape == null) return null;
            foreach (int rep in shape.Refs(2))
            {
                if (LayerOf.TryGetValue(rep, out string n)) return n;
                StepEntity r = Step.Get(rep);
                if (r == null) continue;
                foreach (int it in r.Refs(3)) if (LayerOf.TryGetValue(it, out n)) return n;
            }
            return null;
        }
    }
}
