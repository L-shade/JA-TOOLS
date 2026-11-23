using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using System.Windows.Threading;
using JA.IfcViewer.Geometry;
using JA.IfcViewer.Ifc;

namespace JA.IfcViewer.Viewer
{
    public enum MeasureMode { None, Distance, Area, Point, PickPlane }

    public sealed class Hit
    {
        public int Index;
        public Vec3 Point, A, B, C, Normal;
    }

    /// <summary>
    /// The 3D view (WPF Viewport3D). Objects are drawn in chunks of 256 (one model per colour per chunk), so a
    /// selection or a colour change rebuilds only the chunks it touches. Section planes clip the triangles on the CPU
    /// (back faces drawn dark, so cuts read as solid). Navigation like Zoom: left drag orbits about the point under the
    /// cursor, middle / right / Shift+left drag pans, the wheel zooms toward the cursor, double click frames the element.
    /// Measurements (distance with snapping to vertices, edge midpoints and edges; face area; point coordinates) are
    /// drawn in a 2D overlay that follows the camera.
    /// </summary>
    public sealed class SceneView : Grid
    {
        private const int ChunkSize = 256;

        private readonly Viewport3D _vp = new Viewport3D { ClipToBounds = true };
        private readonly Canvas _overlay = new Canvas { IsHitTestVisible = false };
        private readonly ModelVisual3D _lightsVisual = new ModelVisual3D();
        private readonly Model3DGroup _opaqueRoot = new Model3DGroup(), _transpRoot = new Model3DGroup();
        private readonly List<Model3DGroup> _opaqueChunks = new List<Model3DGroup>(), _transpChunks = new List<Model3DGroup>();
        private readonly List<List<MeshGeometry3D>> _chunkMeshes = new List<List<MeshGeometry3D>>();
        private readonly Dictionary<MeshGeometry3D, int[]> _triMap = new Dictionary<MeshGeometry3D, int[]>();
        private readonly HashSet<int> _dirty = new HashSet<int>();
        private bool _allDirty, _pending;
        private readonly DirectionalLight _headLight = new DirectionalLight(Color.FromRgb(0x78, 0x78, 0x78), new Vector3D(0, 0, -1));

        private readonly PerspectiveCamera _persp = new PerspectiveCamera { FieldOfView = 50 };
        private readonly OrthographicCamera _ortho = new OrthographicCamera();
        private Vec3 _pos = new Vec3(-20, -20, 15), _dir = new Vec3(1, 1, -0.75).Normalized(), _up = Vec3.UnitZ;
        private double _orthoWidth = 30;
        private bool _orthographic;
        private Bounds _sceneBounds = Bounds.Empty;

        public Scene Scene { get; private set; }
        public uint Highlight = 0xFF2F8FE8;
        public Bounds? SectionBoxOutline;

        public event Action<int, bool> ElementClicked;          // index (-1 = empty), Ctrl held
        public event Action<int, Point> ContextRequested;        // index under the cursor (-1 none)
        public event Action<int> ElementDoubleClicked;
        public event Action<Vec3, Vec3> FacePicked;              // point and normal (towards the viewer), scene coordinates
        public event Action<string> Message;
        public event Action ModeChanged;

        public SceneView()
        {
            Background = new LinearGradientBrush(Color.FromRgb(0xF5, 0xF7, 0xF9), Color.FromRgb(0xD5, 0xDB, 0xE1), 90);
            Focusable = true;
            ClipToBounds = true;
            var lights = new Model3DGroup();
            lights.Children.Add(new AmbientLight(Color.FromRgb(0x5C, 0x5C, 0x60)));
            lights.Children.Add(_headLight);
            lights.Children.Add(new DirectionalLight(Color.FromRgb(0x4C, 0x4C, 0x4C), new Vector3D(-0.35, -0.5, -1)));
            _lightsVisual.Content = lights;
            _vp.Children.Add(_lightsVisual);
            _vp.Children.Add(new ModelVisual3D { Content = _opaqueRoot });
            _vp.Children.Add(new ModelVisual3D { Content = _transpRoot });
            Children.Add(_vp);
            Children.Add(_overlay);
            _vp.Camera = _persp;
            ApplyCamera();
            SizeChanged += (s, e) => { ApplyCamera(); };
            MouseDown += OnDown;
            MouseMove += OnMove;
            MouseUp += OnUp;
            MouseWheel += OnWheel;
            MouseLeave += (s, e) => { _hover = null; if (Mode != MeasureMode.None) DrawOverlay(); };
            RenderOptions.SetEdgeMode(_vp, EdgeMode.Unspecified);
        }

        // ================================================================== scene

        public void SetScene(Scene s)
        {
            Scene = s;
            _measures.Clear();
            _current = null;
            InvalidateAll();
        }

        public void InvalidateAll()
        {
            _allDirty = true;
            Schedule();
        }

        public void Invalidate(IEnumerable<int> indices)
        {
            foreach (int i in indices) _dirty.Add(i / ChunkSize);
            Schedule();
        }

        private void Schedule()
        {
            if (_pending) return;
            _pending = true;
            Dispatcher.BeginInvoke(new Action(Rebuild), DispatcherPriority.Render);
        }

        /// <summary>Rebuilds now what is pending.</summary>
        public void Rebuild()
        {
            _pending = false;
            if (Scene == null) return;
            int chunks = (Scene.Count + ChunkSize - 1) / ChunkSize;
            while (_opaqueChunks.Count < chunks)
            {
                var a = new Model3DGroup(); var b = new Model3DGroup();
                _opaqueChunks.Add(a); _transpChunks.Add(b); _chunkMeshes.Add(new List<MeshGeometry3D>());
                _opaqueRoot.Children.Add(a); _transpRoot.Children.Add(b);
            }
            while (_opaqueChunks.Count > chunks)
            {
                int last = _opaqueChunks.Count - 1;
                foreach (var m in _chunkMeshes[last]) _triMap.Remove(m);
                _opaqueRoot.Children.Remove(_opaqueChunks[last]); _transpRoot.Children.Remove(_transpChunks[last]);
                _opaqueChunks.RemoveAt(last); _transpChunks.RemoveAt(last); _chunkMeshes.RemoveAt(last);
            }
            List<int> todo = _allDirty ? Enumerable.Range(0, chunks).ToList() : _dirty.Where(c => c < chunks).ToList();
            _allDirty = false;
            _dirty.Clear();
            if (todo.Count == 0) return;
            var snap = new BuildState(this);
            var results = new ChunkResult[todo.Count];
            Parallel.For(0, todo.Count, k => results[k] = BuildChunk(todo[k], snap));
            for (int k = 0; k < todo.Count; k++)
            {
                int c = todo[k];
                foreach (var m in _chunkMeshes[c]) _triMap.Remove(m);
                _chunkMeshes[c].Clear();
                ChunkResult r = results[k];
                _opaqueChunks[c].Children = new Model3DCollection(r.Opaque);
                _transpChunks[c].Children = new Model3DCollection(r.Transparent);
                for (int i = 0; i < r.Meshes.Count; i++) { _triMap[r.Meshes[i]] = r.Maps[i]; _chunkMeshes[c].Add(r.Meshes[i]); }
            }
            _sceneBounds = Scene.BoundsOf(Scene.VisibleIndices());
            if (_sceneBounds.IsEmpty) _sceneBounds = Scene.AllBounds;
            DrawOverlay();
        }

        private sealed class BuildState
        {
            public Scene S;
            public ClipPlane[] Planes;
            public uint Highlight;
            public Material Back;
            public BuildState(SceneView v)
            {
                S = v.Scene;
                Planes = v.Scene.Planes.ToArray();
                Highlight = v.Highlight;
                Back = Planes.Length > 0 ? CutMaterial : null;
            }
        }

        private sealed class ChunkResult
        {
            public readonly List<Model3D> Opaque = new List<Model3D>(), Transparent = new List<Model3D>();
            public readonly List<MeshGeometry3D> Meshes = new List<MeshGeometry3D>();
            public readonly List<int[]> Maps = new List<int[]>();
        }

        private sealed class Batch
        {
            public readonly List<Point3D> P = new List<Point3D>();
            public readonly List<Vector3D> N = new List<Vector3D>();
            public readonly List<int> Map = new List<int>();
        }

        private static readonly ConcurrentDictionary<uint, Material> Materials = new ConcurrentDictionary<uint, Material>();
        private static readonly Material CutMaterial = MakeCut();
        private static Material MakeCut()
        {
            var b = new SolidColorBrush(Color.FromRgb(0x4A, 0x4E, 0x55)); b.Freeze();
            var m = new DiffuseMaterial(b); m.Freeze();
            return m;
        }

        private static Material MaterialOf(uint c) => Materials.GetOrAdd(c, k =>
        {
            var br = new SolidColorBrush(Color.FromArgb((byte)(k >> 24), (byte)(k >> 16), (byte)(k >> 8), (byte)k));
            br.Freeze();
            var g = new MaterialGroup();
            g.Children.Add(new DiffuseMaterial(br));
            if ((k >> 24) >= 250)
            {
                var sb = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)); sb.Freeze();
                g.Children.Add(new SpecularMaterial(sb, 30));
            }
            g.Freeze();
            return g;
        });

        private static uint Effective(BuildState s, int i, uint part)
        {
            Scene sc = s.S;
            uint c = sc.SvColor[i] != 0 ? sc.SvColor[i] : part;
            uint a = c >> 24;
            if (sc.Selected[i]) { c = s.Highlight; a = Math.Max(a, 0xC8); }
            else
            {
                if (sc.SvTransparent[i] || sc.UserTransparent[i]) a = Math.Min(a, 0x48);
                if (sc.XRay) a = Math.Min(a, 0x36);
            }
            return (a << 24) | (c & 0x00FFFFFF);
        }

        private static ChunkResult BuildChunk(int chunk, BuildState s)
        {
            var r = new ChunkResult();
            Scene sc = s.S;
            int from = chunk * ChunkSize, to = Math.Min(sc.Count, from + ChunkSize);
            var batches = new Dictionary<uint, Batch>();
            var poly = new List<Vec3>(12);
            var tmp = new List<Vec3>(12);
            for (int i = from; i < to; i++)
            {
                if (!sc.IsVisible(i)) continue;
                IfcObject o = sc.Objects[i];
                // planes: skip what is wholly cut away, clip only what crosses a plane
                bool clip = false, gone = false;
                foreach (ClipPlane pl in s.Planes)
                {
                    double min = double.MaxValue, max = double.MinValue;
                    for (int k = 0; k < 8; k++)
                    {
                        Vec3 c = new Vec3((k & 1) == 0 ? o.Bounds.Min.X : o.Bounds.Max.X, (k & 2) == 0 ? o.Bounds.Min.Y : o.Bounds.Max.Y, (k & 4) == 0 ? o.Bounds.Min.Z : o.Bounds.Max.Z);
                        double d = pl.N.Dot(c) - pl.D;
                        if (d < min) min = d; if (d > max) max = d;
                    }
                    if (min > 0) { gone = true; break; }
                    if (max > 0) clip = true;
                }
                if (gone) continue;
                foreach (MeshPart part in o.Parts)
                {
                    uint col = Effective(s, i, part.Color);
                    if (!batches.TryGetValue(col, out Batch b)) batches[col] = b = new Batch();
                    float[] f = part.P;
                    for (int t = 0; t + 8 < f.Length; t += 9)
                    {
                        Vec3 a = new Vec3(f[t], f[t + 1], f[t + 2]), bb = new Vec3(f[t + 3], f[t + 4], f[t + 5]), c = new Vec3(f[t + 6], f[t + 7], f[t + 8]);
                        Vec3 n = (bb - a).Cross(c - a);
                        double nl = n.Length;
                        if (nl < 1e-14) continue;
                        n = n / nl;
                        var nv = new Vector3D(n.X, n.Y, n.Z);
                        if (!clip)
                        {
                            b.P.Add(new Point3D(a.X, a.Y, a.Z)); b.P.Add(new Point3D(bb.X, bb.Y, bb.Z)); b.P.Add(new Point3D(c.X, c.Y, c.Z));
                            b.N.Add(nv); b.N.Add(nv); b.N.Add(nv);
                            b.Map.Add(i);
                            continue;
                        }
                        poly.Clear(); poly.Add(a); poly.Add(bb); poly.Add(c);
                        foreach (ClipPlane pl in s.Planes)
                        {
                            ClipPoly(poly, tmp, pl);
                            var sw = poly; poly = tmp; tmp = sw;
                            if (poly.Count < 3) break;
                        }
                        for (int k = 1; k + 1 < poly.Count; k++)
                        {
                            b.P.Add(new Point3D(poly[0].X, poly[0].Y, poly[0].Z)); b.P.Add(new Point3D(poly[k].X, poly[k].Y, poly[k].Z)); b.P.Add(new Point3D(poly[k + 1].X, poly[k + 1].Y, poly[k + 1].Z));
                            b.N.Add(nv); b.N.Add(nv); b.N.Add(nv);
                            b.Map.Add(i);
                        }
                    }
                }
            }
            foreach (var kv in batches)
            {
                Batch b = kv.Value;
                if (b.P.Count == 0) continue;
                var mesh = new MeshGeometry3D { Positions = new Point3DCollection(b.P), Normals = new Vector3DCollection(b.N) };
                mesh.Freeze();
                Material mat = MaterialOf(kv.Key);
                var gm = new GeometryModel3D(mesh, mat) { BackMaterial = s.Back ?? mat };
                gm.Freeze();
                if ((kv.Key >> 24) < 250) r.Transparent.Add(gm); else r.Opaque.Add(gm);
                r.Meshes.Add(mesh);
                r.Maps.Add(b.Map.ToArray());
            }
            return r;
        }

        /// <summary>Sutherland–Hodgman: the part of the polygon with n·p ≤ D.</summary>
        private static void ClipPoly(List<Vec3> src, List<Vec3> dst, ClipPlane pl)
        {
            dst.Clear();
            int n = src.Count;
            for (int i = 0; i < n; i++)
            {
                Vec3 a = src[i], b = src[(i + 1) % n];
                double da = pl.N.Dot(a) - pl.D, db = pl.N.Dot(b) - pl.D;
                bool ina = da <= 0, inb = db <= 0;
                if (ina) dst.Add(a);
                if (ina != inb) dst.Add(Vec3.Lerp(a, b, da / (da - db)));
            }
        }

        // ================================================================== camera

        public bool Orthographic
        {
            get => _orthographic;
            set
            {
                if (_orthographic == value) return;
                _orthographic = value;
                Vec3 pivot = PivotDefault();
                double dist = Math.Max(1, (pivot - _pos).Dot(_dir));
                if (value)
                {
                    double tanH = Math.Tan(_persp.FieldOfView * Math.PI / 360);
                    _orthoWidth = 2 * dist * tanH;
                    double back = Math.Max(dist, _sceneBounds.Diagonal * 1.5 + 5);
                    _pos = pivot - _dir * back;
                }
                else
                {
                    double tanH = Math.Tan(_persp.FieldOfView * Math.PI / 360);
                    double d = _orthoWidth / (2 * tanH);
                    _pos = pivot - _dir * d;
                }
                ApplyCamera();
            }
        }

        private Vec3 PivotDefault()
        {
            if (_sceneBounds.IsEmpty) return _pos + _dir * 10;
            Vec3 c = _sceneBounds.Center;
            // the point on the view axis nearest to the scene centre
            return _pos + _dir * Math.Max(0.5, (c - _pos).Dot(_dir));
        }

        private void ApplyCamera()
        {
            var p = new Point3D(_pos.X, _pos.Y, _pos.Z);
            var d = new Vector3D(_dir.X, _dir.Y, _dir.Z);
            var u = new Vector3D(_up.X, _up.Y, _up.Z);
            double R = _sceneBounds.IsEmpty ? 50 : Math.Max(1, _sceneBounds.Diagonal / 2);
            double dc = _sceneBounds.IsEmpty ? 50 : (_sceneBounds.Center - _pos).Length;
            if (_orthographic)
            {
                _ortho.Position = p; _ortho.LookDirection = d; _ortho.UpDirection = u; _ortho.Width = Math.Max(0.01, _orthoWidth);
                _ortho.NearPlaneDistance = 0.01; _ortho.FarPlaneDistance = dc + R * 4 + 10;
                if (_vp.Camera != _ortho) _vp.Camera = _ortho;
            }
            else
            {
                _persp.Position = p; _persp.LookDirection = d; _persp.UpDirection = u;
                double near = dc > R ? Math.Max(0.05, (dc - R) * 0.5) : Math.Max(0.02, R * 0.0004);
                _persp.NearPlaneDistance = Math.Min(near, 5);
                _persp.FarPlaneDistance = dc + R * 4 + 10;
                if (_vp.Camera != _persp) _vp.Camera = _persp;
            }
            _headLight.Direction = d + new Vector3D(0, 0, -0.35 * d.Length);
            DrawOverlay();
        }

        public void SetView(Vec3 dir, Vec3 up, bool fit = true)
        {
            _dir = dir.Normalized();
            _up = (up - _dir * up.Dot(_dir)).Normalized();
            if (fit) Fit(null); else ApplyCamera();
        }

        public void ViewIso() => SetView(new Vec3(1, 1, -0.8), Vec3.UnitZ);
        public void ViewTop() => SetView(new Vec3(0, 0, -1), Vec3.UnitY);
        public void ViewFront() => SetView(new Vec3(0, 1, 0), Vec3.UnitZ);
        public void ViewBack() => SetView(new Vec3(0, -1, 0), Vec3.UnitZ);
        public void ViewLeft() => SetView(new Vec3(1, 0, 0), Vec3.UnitZ);
        public void ViewRight() => SetView(new Vec3(-1, 0, 0), Vec3.UnitZ);

        /// <summary>Frames the given objects (all visible when null).</summary>
        public void Fit(IEnumerable<int> indices)
        {
            if (Scene == null) return;
            if (_pending) Rebuild();
            Bounds b = indices == null ? Scene.BoundsOf(Scene.VisibleIndices()) : Scene.BoundsOf(indices);
            if (b.IsEmpty) b = Scene.AllBounds;
            if (b.IsEmpty) return;
            if (indices == null) _sceneBounds = b;
            FitBounds(b);
        }

        public void FitBounds(Bounds b)
        {
            Vec3 c = b.Center;
            double r = Math.Max(0.3, b.Diagonal / 2);
            double w = Math.Max(1, ActualWidth), h = Math.Max(1, ActualHeight);
            if (_orthographic)
            {
                _orthoWidth = 2.1 * r * Math.Max(1, w / h);
                _pos = c - _dir * (r * 3 + 5);
            }
            else
            {
                double hf = _persp.FieldOfView * Math.PI / 180;
                double vf = 2 * Math.Atan(Math.Tan(hf / 2) * h / w);
                double half = Math.Min(hf, vf) / 2;
                _pos = c - _dir * (r / Math.Sin(half) * 1.02);
            }
            ApplyCamera();
        }

        private void Orbit(double dx, double dy, Vec3 pivot)
        {
            double k = 0.008;
            Mat yaw = Mat.Rotation(Vec3.UnitZ, -dx * k);
            Vec3 pos = pivot + yaw.Vector(_pos - pivot);
            Vec3 dir = yaw.Vector(_dir), up = yaw.Vector(_up);
            Vec3 right = dir.Cross(up).Normalized();
            Mat pitch = Mat.Rotation(right, -dy * k);
            Vec3 dir2 = pitch.Vector(dir);
            if (Math.Abs(dir2.Z) < 0.9995)
            {
                pos = pivot + pitch.Vector(pos - pivot);
                dir = dir2;
                up = pitch.Vector(up);
            }
            _dir = dir.Normalized();
            if (Math.Abs(_dir.Z) < 0.999) _up = (Vec3.UnitZ - _dir * _dir.Z).Normalized();
            else _up = (up - _dir * up.Dot(_dir)).Normalized();
            _pos = pos;
            ApplyCamera();
        }

        private double WorldPerPixel(Vec3 at)
        {
            double w = Math.Max(1, ActualWidth);
            if (_orthographic) return _orthoWidth / w;
            double depth = Math.Max(0.05, (at - _pos).Dot(_dir));
            return 2 * depth * Math.Tan(_persp.FieldOfView * Math.PI / 360) / w;
        }

        private void Pan(double dx, double dy, Vec3 at)
        {
            double wpp = WorldPerPixel(at);
            Vec3 right = _dir.Cross(_up).Normalized();
            _pos = _pos - right * (dx * wpp) + _up * (dy * wpp);
            ApplyCamera();
        }

        /// <summary>Ray through a screen point: origin and unit direction (scene coordinates).</summary>
        private void Ray(Point s, out Vec3 o, out Vec3 d)
        {
            double w = Math.Max(1, ActualWidth), h = Math.Max(1, ActualHeight);
            double x = (s.X - w / 2) / (w / 2), y = (h / 2 - s.Y) / (w / 2);
            Vec3 right = _dir.Cross(_up).Normalized();
            if (_orthographic)
            {
                o = _pos + right * (x * _orthoWidth / 2) + _up * (y * _orthoWidth / 2);
                d = _dir;
            }
            else
            {
                double t = Math.Tan(_persp.FieldOfView * Math.PI / 360);
                o = _pos;
                d = (_dir + right * (x * t) + _up * (y * t)).Normalized();
            }
        }

        private void ZoomAt(Point s, double notches)
        {
            Hit hit = HitTest(s);
            Ray(s, out Vec3 o, out Vec3 d);
            if (_orthographic)
            {
                double f = Math.Pow(0.85, notches);
                double w = Math.Max(1, ActualWidth), h = Math.Max(1, ActualHeight);
                Vec3 right = _dir.Cross(_up).Normalized();
                double x = s.X - w / 2, y = h / 2 - s.Y;
                double before = _orthoWidth / w, after = _orthoWidth * f / w;
                _pos = _pos + right * (x * (before - after)) + _up * (y * (before - after));
                _orthoWidth *= f;
            }
            else
            {
                Vec3 target = hit != null ? hit.Point : o + d * Math.Max(1, (PivotDefault() - _pos).Length);
                double dist = (target - _pos).Length;
                double step = dist * (1 - Math.Pow(0.82, notches));
                if (notches > 0 && step > dist - 0.05) step = Math.Max(0, dist - 0.05);
                _pos = _pos + d * step;
            }
            ApplyCamera();
        }

        // ================================================================== picking

        public Hit HitTest(Point s)
        {
            if (Scene == null) return null;
            RayMeshGeometry3DHitTestResult best = null;
            HitTestResultBehavior Cb(HitTestResult r)
            {
                if (r is RayMeshGeometry3DHitTestResult m && _triMap.ContainsKey(m.MeshHit))
                    if (best == null || m.DistanceToRayOrigin < best.DistanceToRayOrigin) best = m;
                return HitTestResultBehavior.Continue;
            }
            try { VisualTreeHelper.HitTest(_vp, null, Cb, new PointHitTestParameters(s)); }
            catch { return null; }
            if (best == null) return null;
            int[] map = _triMap[best.MeshHit];
            int tri = best.VertexIndex1 / 3;
            if (tri < 0 || tri >= map.Length) return null;
            Point3DCollection P = best.MeshHit.Positions;
            Vec3 V(int k) { Point3D q = P[k]; return new Vec3(q.X, q.Y, q.Z); }
            Vec3 a = V(tri * 3), b = V(tri * 3 + 1), c = V(tri * 3 + 2);
            Vec3 n = (b - a).Cross(c - a).Normalized();
            if (n.Dot(_dir) > 0) n = -n;
            return new Hit { Index = map[tri], Point = new Vec3(best.PointHit.X, best.PointHit.Y, best.PointHit.Z), A = a, B = b, C = c, Normal = n };
        }

        // ================================================================== mouse

        private Point _down;
        private MouseButton _downButton;
        private bool _dragging, _isDown;
        private Vec3 _pivot;
        private Point _last;
        private DateTime _lastHover;

        private void OnDown(object sender, MouseButtonEventArgs e)
        {
            Focus();
            if (e.ClickCount == 2 && e.ChangedButton == MouseButton.Left && Mode == MeasureMode.None)
            {
                Hit h = HitTest(e.GetPosition(this));
                if (h != null) ElementDoubleClicked?.Invoke(h.Index);
                else Fit(null);
                e.Handled = true;
                return;
            }
            _down = _last = e.GetPosition(this);
            _downButton = e.ChangedButton;
            _isDown = true;
            _dragging = false;
            Hit hit = HitTest(_down);
            _pivot = hit != null ? hit.Point : PivotDefault();
            CaptureMouse();
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            Point p = e.GetPosition(this);
            if (_isDown)
            {
                if (!_dragging && (Math.Abs(p.X - _down.X) > 3 || Math.Abs(p.Y - _down.Y) > 3)) _dragging = true;
                if (_dragging)
                {
                    double dx = p.X - _last.X, dy = p.Y - _last.Y;
                    bool pan = _downButton == MouseButton.Middle || _downButton == MouseButton.Right
                               || (_downButton == MouseButton.Left && (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
                    if (pan) Pan(dx, dy, _pivot);
                    else if (_downButton == MouseButton.Left) Orbit(dx, dy, _pivot);
                }
                _last = p;
                return;
            }
            if (Mode == MeasureMode.Distance || Mode == MeasureMode.Point || Mode == MeasureMode.Area || Mode == MeasureMode.PickPlane)
            {
                if ((DateTime.Now - _lastHover).TotalMilliseconds < 30) return;
                _lastHover = DateTime.Now;
                Hit h = HitTest(p);
                _hover = h == null ? null : (Mode == MeasureMode.Area || Mode == MeasureMode.PickPlane ? new Snap { P = h.Point, Kind = "face", Hit = h } : SnapOf(h, p));
                DrawOverlay();
            }
        }

        private void OnUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDown) return;
            _isDown = false;
            ReleaseMouseCapture();
            Point p = e.GetPosition(this);
            if (_dragging) { _dragging = false; return; }
            if (e.ChangedButton == MouseButton.Right)
            {
                Hit h = HitTest(p);
                ContextRequested?.Invoke(h?.Index ?? -1, p);
                return;
            }
            if (e.ChangedButton != MouseButton.Left) return;
            if (Mode != MeasureMode.None) { MeasureClick(p); return; }
            Hit hit = HitTest(p);
            ElementClicked?.Invoke(hit?.Index ?? -1, (Keyboard.Modifiers & ModifierKeys.Control) != 0);
        }

        private void OnWheel(object sender, MouseWheelEventArgs e)
        {
            ZoomAt(e.GetPosition(this), e.Delta / 120.0);
            e.Handled = true;
        }

        // ================================================================== measuring

        private sealed class Snap { public Vec3 P; public string Kind; public Hit Hit; }

        private sealed class Measure
        {
            public MeasureMode Kind;
            public readonly List<Vec3> Pts = new List<Vec3>();
            public List<Vec3> Outline;     // pairs
            public string Text, Sub;
            public Vec3 LabelAt;
        }

        private readonly List<Measure> _measures = new List<Measure>();
        private Measure _current;
        private Snap _hover;
        private MeasureMode _mode;

        public MeasureMode Mode
        {
            get => _mode;
            set
            {
                _mode = value;
                _current = null;
                _hover = null;
                Cursor = value == MeasureMode.None ? null : Cursors.Cross;
                DrawOverlay();
                ModeChanged?.Invoke();
                switch (value)
                {
                    case MeasureMode.Distance: Message?.Invoke("Distância: clique no primeiro ponto (atrai vértices, meios e arestas). Esc termina."); break;
                    case MeasureMode.Area: Message?.Invoke("Área: clique numa face. Esc termina."); break;
                    case MeasureMode.Point: Message?.Invoke("Coordenada: clique num ponto. Esc termina."); break;
                    case MeasureMode.PickPlane: Message?.Invoke("Plano de corte: clique na face onde cortar (o lado de cá some)."); break;
                }
            }
        }

        public bool HasMeasurements => _measures.Count > 0;

        public void ClearMeasurements()
        {
            _measures.Clear();
            _current = null;
            DrawOverlay();
        }

        public void CancelCurrent()
        {
            if (_current != null) { _current = null; DrawOverlay(); }
            else Mode = MeasureMode.None;
        }

        private Snap SnapOf(Hit h, Point mouse)
        {
            var best = new Snap { P = h.Point, Kind = "face", Hit = h };
            double bestD = double.MaxValue;
            void Try(Vec3 q, string kind, double radius)
            {
                if (!Project(q, out Point s)) return;
                double d = (s - mouse).Length;
                if (d <= radius && d < bestD) { bestD = d; best = new Snap { P = q, Kind = kind, Hit = h }; }
            }
            Try(h.A, "vértice", 14); Try(h.B, "vértice", 14); Try(h.C, "vértice", 14);
            if (bestD < double.MaxValue) return best;
            Try((h.A + h.B) * 0.5, "meio", 10); Try((h.B + h.C) * 0.5, "meio", 10); Try((h.C + h.A) * 0.5, "meio", 10);
            if (bestD < double.MaxValue) return best;
            foreach (var (a, b) in new[] { (h.A, h.B), (h.B, h.C), (h.C, h.A) })
            {
                Vec3 ab = b - a;
                double t = ab.LengthSq > 1e-18 ? Math.Max(0, Math.Min(1, (h.Point - a).Dot(ab) / ab.LengthSq)) : 0;
                Try(a + ab * t, "aresta", 7);
            }
            return best;
        }

        private void MeasureClick(Point p)
        {
            Hit h = HitTest(p);
            if (h == null) return;
            Vec3 o = Scene.Origin;
            switch (Mode)
            {
                case MeasureMode.PickPlane:
                    FacePicked?.Invoke(h.Point, h.Normal);
                    Mode = MeasureMode.None;
                    return;
                case MeasureMode.Point:
                {
                    Snap s = SnapOf(h, p);
                    var m = new Measure { Kind = MeasureMode.Point, LabelAt = s.P };
                    m.Pts.Add(s.P);
                    Vec3 w = s.P + o;
                    m.Text = $"X {Fmt(w.X)}   Y {Fmt(w.Y)}";
                    m.Sub = $"Z {Fmt(w.Z)}" + StoreyNote(h.Index, s.P.Z);
                    _measures.Add(m);
                    Message?.Invoke("Coordenada: " + m.Text + "   " + m.Sub);
                    break;
                }
                case MeasureMode.Distance:
                {
                    Snap s = SnapOf(h, p);
                    if (_current == null)
                    {
                        _current = new Measure { Kind = MeasureMode.Distance };
                        _current.Pts.Add(s.P);
                        Message?.Invoke("Distância: clique no segundo ponto.");
                    }
                    else
                    {
                        _current.Pts.Add(s.P);
                        Finish(_current);
                        _measures.Add(_current);
                        Message?.Invoke("Distância: " + _current.Text + "   (" + _current.Sub + ")");
                        _current = null;
                    }
                    break;
                }
                case MeasureMode.Area:
                {
                    Measure m = FaceArea(h);
                    if (m != null) { _measures.Add(m); Message?.Invoke("Área da face: " + m.Text + "   " + m.Sub); }
                    break;
                }
            }
            DrawOverlay();
        }

        private string StoreyNote(int index, double z)
        {
            IfcObject st = Scene.Objects[index].Storey;
            if (st == null) return "";
            return $"   ({Fmt(z - st.Elevation)} acima de {st.DisplayName})";
        }

        private static string Fmt(double v) => v.ToString("0.000", PropertyReader.Br);

        private static void Finish(Measure m)
        {
            Vec3 a = m.Pts[0], b = m.Pts[1], d = b - a;
            m.Text = Fmt(d.Length) + " m";
            m.Sub = $"ΔX {Fmt(Math.Abs(d.X))}  ΔY {Fmt(Math.Abs(d.Y))}  ΔZ {Fmt(Math.Abs(d.Z))}  horiz. {Fmt(Math.Sqrt(d.X * d.X + d.Y * d.Y))}";
            m.LabelAt = (a + b) * 0.5;
        }

        /// <summary>The connected coplanar triangles of the element around the clicked one.</summary>
        private Measure FaceArea(Hit h)
        {
            IfcObject o = Scene.Objects[h.Index];
            if (!o.HasGeometry) return null;
            Vec3 n0 = (h.B - h.A).Cross(h.C - h.A).Normalized();
            double d0 = n0.Dot(h.A);
            var tris = new List<Vec3[]>();
            foreach (MeshPart part in o.Parts)
            {
                float[] f = part.P;
                for (int t = 0; t + 8 < f.Length; t += 9)
                {
                    var a = new Vec3(f[t], f[t + 1], f[t + 2]); var b = new Vec3(f[t + 3], f[t + 4], f[t + 5]); var c = new Vec3(f[t + 6], f[t + 7], f[t + 8]);
                    Vec3 n = (b - a).Cross(c - a);
                    if (n.LengthSq < 1e-18) continue;
                    n = n.Normalized();
                    if (Math.Abs(n.Dot(n0)) < 0.9995) continue;
                    if (Math.Abs(n0.Dot(a) - d0) > 0.002 || Math.Abs(n0.Dot(b) - d0) > 0.002 || Math.Abs(n0.Dot(c) - d0) > 0.002) continue;
                    tris.Add(new[] { a, b, c });
                }
            }
            if (tris.Count == 0) return null;
            string Key(Vec3 v) => Math.Round(v.X * 5000) + "," + Math.Round(v.Y * 5000) + "," + Math.Round(v.Z * 5000);
            // connectivity by shared vertices; start from the triangle containing the hit point
            var byVertex = new Dictionary<string, List<int>>();
            for (int i = 0; i < tris.Count; i++) foreach (Vec3 v in tris[i]) { string k = Key(v); if (!byVertex.TryGetValue(k, out var l)) byVertex[k] = l = new List<int>(); l.Add(i); }
            int start = 0; double bd = double.MaxValue;
            for (int i = 0; i < tris.Count; i++)
            {
                Vec3 c = (tris[i][0] + tris[i][1] + tris[i][2]) / 3;
                double d = (c - h.Point).LengthSq;
                if (InTri(tris[i], h.Point)) { start = i; break; }
                if (d < bd) { bd = d; start = i; }
            }
            var seen = new HashSet<int> { start };
            var queue = new Queue<int>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                foreach (Vec3 v in tris[i]) foreach (int j in byVertex[Key(v)]) if (seen.Add(j)) queue.Enqueue(j);
            }
            double area = 0;
            var edges = new Dictionary<string, (Vec3 a, Vec3 b, int n)>();
            Vec3 centroid = Vec3.Zero;
            foreach (int i in seen)
            {
                Vec3[] t = tris[i];
                double a = (t[1] - t[0]).Cross(t[2] - t[0]).Length / 2;
                area += a;
                centroid = centroid + (t[0] + t[1] + t[2]) / 3 * a;
                for (int k = 0; k < 3; k++)
                {
                    Vec3 p = t[k], q = t[(k + 1) % 3];
                    string kp = Key(p), kq = Key(q);
                    string ek = string.CompareOrdinal(kp, kq) < 0 ? kp + "|" + kq : kq + "|" + kp;
                    edges[ek] = edges.TryGetValue(ek, out var e) ? (e.a, e.b, e.n + 1) : (p, q, 1);
                }
            }
            var m = new Measure { Kind = MeasureMode.Area, Outline = new List<Vec3>() };
            double perim = 0;
            foreach (var e in edges.Values) if (e.n == 1) { m.Outline.Add(e.a); m.Outline.Add(e.b); perim += (e.b - e.a).Length; }
            m.LabelAt = area > 0 ? centroid / area : h.Point;
            m.Text = area.ToString("0.00", PropertyReader.Br) + " m²";
            m.Sub = "perímetro " + perim.ToString("0.00", PropertyReader.Br) + " m";
            return m;
        }

        private static bool InTri(Vec3[] t, Vec3 p)
        {
            Vec3 n = (t[1] - t[0]).Cross(t[2] - t[0]);
            for (int k = 0; k < 3; k++)
                if ((t[(k + 1) % 3] - t[k]).Cross(p - t[k]).Dot(n) < -1e-12) return false;
            return true;
        }

        // ================================================================== overlay

        private bool Project(Vec3 p, out Point s)
        {
            s = default(Point);
            try
            {
                var tr = _lightsVisual.TransformToAncestor(_vp);
                return tr != null && tr.TryTransform(new Point3D(p.X, p.Y, p.Z), out s) && IsInFront(p);
            }
            catch { return false; }
        }

        private bool IsInFront(Vec3 p) => _orthographic || (p - _pos).Dot(_dir) > 0.01;

        private static readonly Brush Accent = Frozen(Color.FromRgb(0xE8, 0x4A, 0x1C));
        private static readonly Brush BoxBrush = Frozen(Color.FromRgb(0x1F, 0x7A, 0x5A));
        private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        public void DrawOverlay()
        {
            _overlay.Children.Clear();
            if (Scene == null) return;
            if (SectionBoxOutline.HasValue && !SectionBoxOutline.Value.IsEmpty)
            {
                Bounds b = SectionBoxOutline.Value;
                Vec3 C(int k) => new Vec3((k & 1) == 0 ? b.Min.X : b.Max.X, (k & 2) == 0 ? b.Min.Y : b.Max.Y, (k & 4) == 0 ? b.Min.Z : b.Max.Z);
                int[,] e = { { 0, 1 }, { 2, 3 }, { 4, 5 }, { 6, 7 }, { 0, 2 }, { 1, 3 }, { 4, 6 }, { 5, 7 }, { 0, 4 }, { 1, 5 }, { 2, 6 }, { 3, 7 } };
                for (int i = 0; i < 12; i++) Seg(C(e[i, 0]), C(e[i, 1]), BoxBrush, 1.2, true);
            }
            foreach (Measure m in _measures) DrawMeasure(m);
            if (_current != null)
            {
                DrawMeasure(_current);
                if (_hover != null && _current.Pts.Count == 1)
                {
                    Seg(_current.Pts[0], _hover.P, Accent, 1.5, true);
                    Vec3 d = _hover.P - _current.Pts[0];
                    Label((_current.Pts[0] + _hover.P) * 0.5, Fmt(d.Length) + " m", null);
                }
            }
            if (_hover != null && Mode != MeasureMode.None) Marker(_hover.P, _hover.Kind);
        }

        private void DrawMeasure(Measure m)
        {
            switch (m.Kind)
            {
                case MeasureMode.Distance:
                    foreach (Vec3 p in m.Pts) Marker(p, "vértice");
                    if (m.Pts.Count == 2) { Seg(m.Pts[0], m.Pts[1], Accent, 2, false); Label(m.LabelAt, m.Text, m.Sub); }
                    break;
                case MeasureMode.Area:
                    for (int i = 0; i + 1 < m.Outline.Count; i += 2) Seg(m.Outline[i], m.Outline[i + 1], Accent, 2, false);
                    Label(m.LabelAt, m.Text, m.Sub);
                    break;
                case MeasureMode.Point:
                    Marker(m.Pts[0], "vértice");
                    Label(m.LabelAt, m.Text, m.Sub);
                    break;
            }
        }

        private void Seg(Vec3 a, Vec3 b, Brush br, double th, bool dashed)
        {
            if (!Project(a, out Point pa) || !Project(b, out Point pb)) return;
            var l = new Line { X1 = pa.X, Y1 = pa.Y, X2 = pb.X, Y2 = pb.Y, Stroke = br, StrokeThickness = th, SnapsToDevicePixels = true };
            if (dashed) l.StrokeDashArray = new DoubleCollection { 4, 3 };
            _overlay.Children.Add(l);
        }

        private void Marker(Vec3 p, string kind)
        {
            if (!Project(p, out Point s)) return;
            Shape sh;
            if (kind == "vértice") sh = new Rectangle { Width = 9, Height = 9 };
            else if (kind == "meio") sh = new Polygon { Points = new PointCollection { new Point(4.5, 0), new Point(9, 9), new Point(0, 9) } };
            else if (kind == "aresta") sh = new Rectangle { Width = 9, Height = 9, RenderTransform = new RotateTransform(45, 4.5, 4.5) };
            else sh = new Ellipse { Width = 8, Height = 8 };
            sh.Stroke = Accent; sh.StrokeThickness = 1.6; sh.Fill = Brushes.White;
            Canvas.SetLeft(sh, s.X - 4.5); Canvas.SetTop(sh, s.Y - 4.5);
            _overlay.Children.Add(sh);
        }

        private void Label(Vec3 at, string text, string sub)
        {
            if (!Project(at, out Point s)) return;
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 13, Foreground = Brushes.White });
            if (!string.IsNullOrEmpty(sub)) sp.Children.Add(new TextBlock { Text = sub, FontSize = 10.5, Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xE4, 0xD8)) });
            var b = new Border { Child = sp, Background = Accent, CornerRadius = new CornerRadius(3), Padding = new Thickness(6, 2, 6, 3) };
            b.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(b, s.X - b.DesiredSize.Width / 2);
            Canvas.SetTop(b, s.Y - b.DesiredSize.Height - 8);
            _overlay.Children.Add(b);
        }

        // ================================================================== image

        public BitmapSource Snapshot(double scale = 2)
        {
            int w = (int)Math.Max(1, ActualWidth * scale), h = (int)Math.Max(1, ActualHeight * scale);
            var rtb = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (DrawingContext dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new VisualBrush(this), null, new Rect(0, 0, ActualWidth, ActualHeight));
            }
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }

        public Vec3 CameraDirection => _dir;
        public Bounds SceneBounds => _sceneBounds;
    }
}
