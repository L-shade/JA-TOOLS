using System;
using System.Collections.Generic;
using System.Linq;
using JA.IfcViewer.Geometry;
using JA.IfcViewer.Ifc;

namespace JA.IfcViewer.Viewer
{
    /// <summary>Keep the side n·p ≤ D.</summary>
    public struct ClipPlane
    {
        public Vec3 N;
        public double D;
        public ClipPlane(Vec3 n, double d) { N = n; D = d; }
        public static ClipPlane Through(Vec3 point, Vec3 removeTowards) { Vec3 n = removeTowards.Normalized(); return new ClipPlane(n, n.Dot(point)); }
    }

    /// <summary>
    /// The federated scene: every model's objects in one list (Index = position), and the display state of each one —
    /// hidden by the user (hide / isolate / tree check boxes), by the active Smart View, its Smart View colour and
    /// transparency, and the selection.
    /// </summary>
    public sealed class Scene
    {
        public readonly List<IfcModel> Models = new List<IfcModel>();
        public readonly List<IfcObject> Objects = new List<IfcObject>();
        public Vec3 Origin;
        public bool HasOrigin;

        public bool[] UserHidden = new bool[0];
        public bool[] SvHidden = new bool[0];
        public uint[] SvColor = new uint[0];
        public bool[] SvTransparent = new bool[0];
        public bool[] Selected = new bool[0];
        public bool[] UserTransparent = new bool[0];
        public readonly HashSet<int> Selection = new HashSet<int>();

        public bool ShowSpaces;
        public bool XRay;
        public List<ClipPlane> Planes = new List<ClipPlane>();

        public int Count => Objects.Count;

        public void Add(IfcModel m)
        {
            if (!HasOrigin) { Origin = m.SuggestOrigin(); HasOrigin = true; }
            m.Bake(Origin);
            m.Index = Models.Count;
            Models.Add(m);
            if (m.Project != null) { m.Project.Index = Objects.Count; Objects.Add(m.Project); }
            foreach (IfcObject o in m.Objects) { o.Index = Objects.Count; Objects.Add(o); }
            Resize();
        }

        public void Remove(IfcModel m)
        {
            Models.Remove(m);
            Objects.Clear();
            for (int k = 0; k < Models.Count; k++)
            {
                IfcModel mm = Models[k];
                mm.Index = k;
                if (mm.Project != null) { mm.Project.Index = Objects.Count; Objects.Add(mm.Project); }
                foreach (IfcObject o in mm.Objects) { o.Index = Objects.Count; Objects.Add(o); }
            }
            UserHidden = new bool[0]; SvHidden = new bool[0]; SvColor = new uint[0]; SvTransparent = new bool[0]; Selected = new bool[0]; UserTransparent = new bool[0];
            Selection.Clear();
            if (Models.Count == 0) HasOrigin = false;
            Resize();
        }

        private void Resize()
        {
            int n = Objects.Count;
            Array.Resize(ref UserHidden, n);
            Array.Resize(ref SvHidden, n);
            Array.Resize(ref SvColor, n);
            Array.Resize(ref SvTransparent, n);
            Array.Resize(ref Selected, n);
            Array.Resize(ref UserTransparent, n);
            for (int i = 0; i < n; i++) if (Objects[i].Type == "IFCSPACE" && !ShowSpaces) SvHidden[i] = true;
        }

        public bool IsVisible(int i)
        {
            IfcObject o = Objects[i];
            return o.HasGeometry && o.Model.Visible && !UserHidden[i] && !SvHidden[i];
        }

        public IEnumerable<int> VisibleIndices()
        {
            for (int i = 0; i < Objects.Count; i++) if (IsVisible(i)) yield return i;
        }

        public Bounds BoundsOf(IEnumerable<int> indices)
        {
            Bounds b = Bounds.Empty;
            foreach (int i in indices) if (i >= 0 && i < Objects.Count && Objects[i].HasGeometry) b.Add(Objects[i].Bounds);
            return b;
        }

        public Bounds AllBounds => BoundsOf(Enumerable.Range(0, Objects.Count).Where(i => Objects[i].Model.Visible));

        /// <summary>Indices of the object and everything decomposed below it (assemblies, storeys…).</summary>
        public List<int> WithChildren(IfcObject o)
        {
            var r = new List<int>();
            var st = new Stack<IfcObject>();
            st.Push(o);
            int guard = 0;
            while (st.Count > 0 && guard++ < 2_000_000)
            {
                IfcObject c = st.Pop();
                if (c.Index >= 0) r.Add(c.Index);
                foreach (IfcObject k in c.Children) st.Push(k);
            }
            return r;
        }

        public void SetSelection(IEnumerable<int> indices, bool add = false)
        {
            if (!add)
            {
                foreach (int i in Selection) if (i < Selected.Length) Selected[i] = false;
                Selection.Clear();
            }
            foreach (int i in indices)
                if (i >= 0 && i < Selected.Length) { Selected[i] = true; Selection.Add(i); }
        }

        public void Toggle(int i)
        {
            if (i < 0 || i >= Selected.Length) return;
            if (Selected[i]) { Selected[i] = false; Selection.Remove(i); }
            else { Selected[i] = true; Selection.Add(i); }
        }
    }
}
