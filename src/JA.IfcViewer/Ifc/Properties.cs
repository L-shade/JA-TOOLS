using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using JA.IfcViewer.Step;

namespace JA.IfcViewer.Ifc
{
    /// <summary>A property value as shown (Text) and, when it is a number, the number in the file's units.</summary>
    public sealed class PropValue
    {
        public string Text;
        public double? Number;
        public PropValue(string t, double? n = null) { Text = t ?? ""; Number = n; }
        public override string ToString() => Text;
    }

    public sealed class PropGroup
    {
        public string Name;
        public bool FromType;
        public readonly List<KeyValuePair<string, PropValue>> Items = new List<KeyValuePair<string, PropValue>>();
        public void Add(string k, PropValue v) { if (v != null && !string.IsNullOrEmpty(v.Text)) Items.Add(new KeyValuePair<string, PropValue>(k, v)); }
        public void Add(string k, string v) { if (!string.IsNullOrWhiteSpace(v)) Items.Add(new KeyValuePair<string, PropValue>(k, new PropValue(v))); }
    }

    /// <summary>
    /// Everything Zoom shows for an element: attributes, location (model, storey), type, materials (layers with
    /// thickness), classification, layer, and each property set / quantity set of the element and of its type.
    /// Flat(): "Grupo.Propriedade" → value, for the Smart View rules (instance values win over the type's).
    /// </summary>
    public static class PropertyReader
    {
        public const string GElement = "Elemento";
        public const string KClass = "Elemento.Classe IFC", KName = "Elemento.Nome", KType = "Elemento.Tipo", KStorey = "Elemento.Pavimento",
            KModel = "Elemento.Modelo", KMaterial = "Material.Nome", KGlobalId = "Elemento.GlobalId", KLayer = "Elemento.Camada",
            KObjectType = "Elemento.Tipo de objeto", KPredef = "Elemento.Tipo predefinido", KDesc = "Elemento.Descrição", KTag = "Elemento.Tag",
            KClassif = "Classificação.Código";

        public static readonly CultureInfo Br = new CultureInfo("pt-BR");

        public static List<PropGroup> Read(IfcObject o)
        {
            IfcModel m = o.Model;
            var groups = new List<PropGroup>();
            var g = new PropGroup { Name = GElement };
            g.Add("Classe IFC", o.Class);
            g.Add("Nome", o.Name);
            if (o.IsSpatial) g.Add("Nome longo", o.LongName);
            g.Add("Descrição", o.Description);
            g.Add("Tipo de objeto", o.ObjectType);
            g.Add("Tipo predefinido", o.PredefinedType);
            g.Add("Tag", o.Tag);
            g.Add("Tipo", o.TypeName);
            if (o.TypeId > 0 && m.Types.TryGetValue(o.TypeId, out IfcTypeInfo ti)) g.Add("Classe do tipo", ti.Class);
            g.Add("Pavimento", o.Storey?.DisplayName);
            g.Add("Modelo", m.FileName);
            g.Add("Camada", m.LayerName(o));
            g.Add("GlobalId", o.GlobalId);
            g.Add("Id no arquivo", "#" + o.Id);
            groups.Add(g);

            var mats = Materials(o);
            if (mats.Items.Count > 0) groups.Add(mats);
            var cl = Classification(o);
            if (cl.Items.Count > 0) groups.Add(cl);

            var seen = new HashSet<int>();
            foreach (int pid in o.PropertyDefs)
                if (seen.Add(pid)) { PropGroup pg = Pset(m, pid); if (pg != null && pg.Items.Count > 0) groups.Add(pg); }
            if (o.TypeId > 0 && m.Types.TryGetValue(o.TypeId, out IfcTypeInfo t))
                foreach (int pid in t.Psets)
                    if (seen.Add(pid))
                    {
                        PropGroup pg = Pset(m, pid);
                        if (pg == null || pg.Items.Count == 0) continue;
                        pg.FromType = true;
                        groups.Add(pg);
                    }
            return groups;
        }

        /// <summary>"Grupo.Propriedade" → value (cached on the object).</summary>
        public static Dictionary<string, PropValue> Flat(IfcObject o)
        {
            if (o.FlatCache != null) return o.FlatCache;
            var d = new Dictionary<string, PropValue>(StringComparer.OrdinalIgnoreCase);
            foreach (PropGroup g in Read(o))
                foreach (var kv in g.Items)
                {
                    string key = g.Name + "." + kv.Key;
                    if (g.FromType && d.ContainsKey(key)) continue;   // instance wins
                    if (!d.ContainsKey(key)) d[key] = kv.Value;
                }
            o.FlatCache = d;
            return d;
        }

        // ------------------------------------------------------------------ property sets

        private static PropGroup Pset(IfcModel m, int id)
        {
            StepEntity e = m.Step.Get(id);
            if (e == null) return null;
            var g = new PropGroup { Name = e.Str(2) ?? IfcNames.Pretty(e.Type) };
            switch (e.Type)
            {
                case "IFCPROPERTYSET":
                    foreach (int p in e.Refs(4)) Property(m, p, g, "", 0);
                    break;
                case "IFCELEMENTQUANTITY":
                    foreach (int q in e.Refs(5)) Quantity(m, q, g, "");
                    break;
                default:
                    // predefined property sets (IfcDoorLiningProperties…): their attributes by position
                    g.Name = IfcNames.Pretty(e.Type);
                    for (int i = 4; i < e.Count; i++)
                    {
                        object v = e[i];
                        if (v == null || v is StepRef) continue;
                        g.Add("Atributo " + (i + 1), Format(m, v, null));
                    }
                    break;
            }
            return g;
        }

        private static void Property(IfcModel m, int id, PropGroup g, string prefix, int depth)
        {
            StepEntity p = m.Step.Get(id);
            if (p == null || depth > 4) return;
            string name = prefix + (p.Str(0) ?? "?");
            switch (p.Type)
            {
                case "IFCPROPERTYSINGLEVALUE":
                    g.Add(name, Format(m, p[2], Unit(m, p.Ref(3))));
                    break;
                case "IFCPROPERTYENUMERATEDVALUE":
                    g.Add(name, string.Join("; ", p.List(2).Select(v => Format(m, v, null).Text)));
                    break;
                case "IFCPROPERTYLISTVALUE":
                    g.Add(name, string.Join("; ", p.List(2).Select(v => Format(m, v, Unit(m, p.Ref(3))).Text)));
                    break;
                case "IFCPROPERTYBOUNDEDVALUE":
                {
                    string u = Unit(m, p.Ref(4));
                    PropValue hi = Format(m, p[2], u), lo = Format(m, p[3], u);
                    g.Add(name, new PropValue((lo.Text.Length > 0 ? lo.Text : "…") + " a " + (hi.Text.Length > 0 ? hi.Text : "…"), lo.Number ?? hi.Number));
                    break;
                }
                case "IFCPROPERTYREFERENCEVALUE":
                {
                    StepEntity r = m.Step.Get(p.Ref(3));
                    g.Add(name, r == null ? "" : (r.Str(0) ?? r.Str(2) ?? IfcNames.Pretty(r.Type)));
                    break;
                }
                case "IFCPROPERTYTABLEVALUE":
                    g.Add(name, "(tabela)");
                    break;
                case "IFCCOMPLEXPROPERTY":
                    foreach (int c in p.Refs(3)) Property(m, c, g, name + " › ", depth + 1);
                    break;
            }
        }

        private static void Quantity(IfcModel m, int id, PropGroup g, string prefix)
        {
            StepEntity q = m.Step.Get(id);
            if (q == null) return;
            string name = prefix + (q.Str(0) ?? "?");
            string unit = Unit(m, q.Ref(2));
            switch (q.Type)
            {
                case "IFCQUANTITYLENGTH": g.Add(name, Num(q.Num(3), unit ?? m.LengthUnit)); break;
                case "IFCQUANTITYAREA": g.Add(name, Num(q.Num(3), unit ?? m.AreaUnit)); break;
                case "IFCQUANTITYVOLUME": g.Add(name, Num(q.Num(3), unit ?? m.VolumeUnit)); break;
                case "IFCQUANTITYWEIGHT": g.Add(name, Num(q.Num(3), unit ?? m.MassUnit)); break;
                case "IFCQUANTITYCOUNT": g.Add(name, Num(q.Num(3), unit)); break;
                case "IFCQUANTITYTIME": g.Add(name, Num(q.Num(3), unit ?? "s")); break;
                case "IFCQUANTITYNUMBER": g.Add(name, Num(q.Num(3), unit)); break;
                case "IFCPHYSICALCOMPLEXQUANTITY":
                    foreach (int c in q.Refs(2)) Quantity(m, c, g, name + " › ");
                    break;
            }
        }

        private static string Unit(IfcModel m, int id)
        {
            if (id <= 0) return null;
            StepEntity u = m.Step.Get(id);
            if (u == null) return null;
            if (u.Type == "IFCSIUNIT")
            {
                string pre = u.Enum(2), n = u.Enum(3);
                string ps = pre == "MILLI" ? "m" : pre == "CENTI" ? "c" : pre == "KILO" ? "k" : "";
                switch (n)
                {
                    case "METRE": return ps + "m";
                    case "SQUARE_METRE": return ps + "m²";
                    case "CUBIC_METRE": return ps + "m³";
                    case "GRAM": return ps + "g";
                    case "SECOND": return "s";
                    case "WATT": return ps + "W";
                    case "VOLT": return "V";
                    case "AMPERE": return "A";
                    case "PASCAL": return ps + "Pa";
                    case "DEGREE_CELSIUS": return "°C";
                    case "KELVIN": return "K";
                    case "LUMEN": return "lm";
                    case "LUX": return "lx";
                    case "NEWTON": return ps + "N";
                    case "HERTZ": return "Hz";
                    case "RADIAN": return "rad";
                }
                return n?.ToLowerInvariant();
            }
            if (u.Type == "IFCCONVERSIONBASEDUNIT") return u.Str(2);
            if (u.Type == "IFCDERIVEDUNIT") return u.Str(2) ?? "";
            return null;
        }

        private static PropValue Num(double v, string unit) =>
            new PropValue(v.ToString("0.####", Br) + (string.IsNullOrEmpty(unit) ? "" : " " + unit), v);

        /// <summary>A STEP value as text (+ number), units from the measure type and the project.</summary>
        public static PropValue Format(IfcModel m, object v, string unit)
        {
            switch (v)
            {
                case null: return new PropValue("");
                case StepTyped t:
                {
                    string ty = t.Type;
                    if (ty == "IFCBOOLEAN" || ty == "IFCLOGICAL")
                    {
                        string e = (t.Value as StepEnum)?.Value;
                        return new PropValue(e == "T" ? "Sim" : e == "F" ? "Não" : "Indefinido", e == "T" ? 1 : e == "F" ? 0 : (double?)null);
                    }
                    if (t.Value is double d)
                    {
                        string u = unit;
                        if (u == null)
                        {
                            if (ty.Contains("AREAMEASURE")) u = m.AreaUnit;
                            else if (ty.Contains("VOLUMEMEASURE")) u = m.VolumeUnit;
                            else if (ty.Contains("LENGTHMEASURE")) u = m.LengthUnit;
                            else if (ty.Contains("PLANEANGLEMEASURE")) u = m.AngleUnit;
                            else if (ty.Contains("MASSMEASURE")) u = m.MassUnit;
                            else if (ty == "IFCTHERMALTRANSMITTANCEMEASURE") u = "W/(m²·K)";
                            else if (ty == "IFCPOSITIVERATIOMEASURE" || ty == "IFCRATIOMEASURE" || ty == "IFCNORMALISEDRATIOMEASURE") u = null;
                            else if (ty == "IFCPOWERMEASURE") u = "W";
                            else if (ty == "IFCELECTRICVOLTAGEMEASURE") u = "V";
                            else if (ty == "IFCELECTRICCURRENTMEASURE") u = "A";
                            else if (ty == "IFCTHERMODYNAMICTEMPERATUREMEASURE") u = "°C";
                            else if (ty == "IFCMASSDENSITYMEASURE") u = "kg/m³";
                        }
                        return Num(d, u);
                    }
                    return Format(m, t.Value, unit);
                }
                case double d2: return Num(d2, unit);
                case string s: return new PropValue(s);
                case StepEnum en:
                    if (en.Value == "T") return new PropValue("Sim", 1);
                    if (en.Value == "F") return new PropValue("Não", 0);
                    if (en.Value == "U") return new PropValue("Indefinido");
                    return new PropValue(en.Value);
                case object[] list: return new PropValue(string.Join("; ", list.Select(x => Format(m, x, unit).Text)));
                case StepRef r:
                {
                    StepEntity e = m.Step.Get(r.Id);
                    return new PropValue(e == null ? r.ToString() : (e.Str(0) ?? IfcNames.Pretty(e.Type)));
                }
            }
            return new PropValue(v.ToString());
        }

        // ------------------------------------------------------------------ materials / classification

        private static PropGroup Materials(IfcObject o)
        {
            IfcModel m = o.Model;
            var g = new PropGroup { Name = "Material" };
            var names = new List<string>();
            void Walk(int id, int depth)
            {
                StepEntity e = m.Step.Get(id);
                if (e == null || depth > 5) return;
                switch (e.Type)
                {
                    case "IFCMATERIAL": names.Add(e.Str(0)); g.Add("Material" + (names.Count > 1 ? " " + names.Count : ""), e.Str(0)); break;
                    case "IFCMATERIALLAYERSETUSAGE": Walk(e.Ref(0), depth + 1); break;
                    case "IFCMATERIALPROFILESETUSAGE": Walk(e.Ref(0), depth + 1); break;
                    case "IFCMATERIALLAYERSET":
                    {
                        if (!string.IsNullOrWhiteSpace(e.Str(1))) g.Add("Conjunto de camadas", e.Str(1));
                        int k = 0;
                        double total = 0;
                        foreach (int ly in e.Refs(0))
                        {
                            StepEntity l = m.Step.Get(ly);
                            if (l == null) continue;
                            StepEntity mat = m.Step.Get(l.Ref(0));
                            string mn = mat?.Str(0) ?? "?";
                            double th = l.Num(1);
                            total += th;
                            names.Add(mn);
                            g.Add("Camada " + (++k), new PropValue(mn + " — " + th.ToString("0.###", Br) + " " + m.LengthUnit, th));
                        }
                        if (k > 0) g.Add("Espessura total", new PropValue(total.ToString("0.###", Br) + " " + m.LengthUnit, total));
                        break;
                    }
                    case "IFCMATERIALLIST": foreach (int x in e.Refs(0)) Walk(x, depth + 1); break;
                    case "IFCMATERIALCONSTITUENTSET":
                        if (!string.IsNullOrWhiteSpace(e.Str(0))) g.Add("Conjunto", e.Str(0));
                        foreach (int c in e.Refs(2))
                        {
                            StepEntity mc = m.Step.Get(c);
                            StepEntity mat = mc == null ? null : m.Step.Get(mc.Ref(2));
                            if (mat == null) continue;
                            names.Add(mat.Str(0));
                            g.Add(mc.Str(0) ?? ("Constituinte " + names.Count), mat.Str(0));
                        }
                        break;
                    case "IFCMATERIALPROFILESET":
                        foreach (int c in e.Refs(2))
                        {
                            StepEntity mp = m.Step.Get(c);
                            StepEntity mat = mp == null ? null : m.Step.Get(mp.Ref(2));
                            if (mat == null) continue;
                            names.Add(mat.Str(0));
                            g.Add(mp.Str(0) ?? ("Perfil " + names.Count), mat.Str(0));
                        }
                        break;
                    case "IFCMATERIALLAYER":
                    case "IFCMATERIALLAYERWITHOFFSETS":
                        Walk(e.Ref(0), depth + 1);
                        break;
                }
            }
            bool any = false;
            if (m.MaterialOf.TryGetValue(o.Id, out List<int> l1)) { foreach (int id in l1) Walk(id, 0); any = true; }
            if (!any && o.TypeId > 0 && m.MaterialOf.TryGetValue(o.TypeId, out List<int> l2)) foreach (int id in l2) Walk(id, 0);
            var distinct = names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
            if (distinct.Count > 0) g.Items.Insert(0, new KeyValuePair<string, PropValue>("Nome", new PropValue(string.Join("; ", distinct))));
            return g;
        }

        private static PropGroup Classification(IfcObject o)
        {
            IfcModel m = o.Model;
            var g = new PropGroup { Name = "Classificação" };
            var ids = new List<int>();
            if (m.ClassificationOf.TryGetValue(o.Id, out List<int> a)) ids.AddRange(a);
            if (o.TypeId > 0 && m.ClassificationOf.TryGetValue(o.TypeId, out List<int> b)) ids.AddRange(b);
            int k = 0;
            foreach (int id in ids.Distinct())
            {
                StepEntity r = m.Step.Get(id);
                if (r == null) continue;
                if (r.Type == "IFCCLASSIFICATIONREFERENCE")
                {
                    string code = r.Str(1), name = r.Str(2);
                    StepEntity src = m.Step.Get(r.Ref(3));
                    string sys = src == null ? null : src.Type == "IFCCLASSIFICATION" ? (src.Str(3) ?? src.Str(0)) : src.Str(2);
                    k++;
                    if (k == 1) g.Add("Código", code ?? name);
                    g.Add((sys ?? "Referência") + (k > 1 ? " " + k : ""), string.Join(" — ", new[] { code, name }.Where(x => !string.IsNullOrWhiteSpace(x))));
                }
                else if (r.Type == "IFCCLASSIFICATION")
                    g.Add("Sistema", r.Str(3) ?? r.Str(0));
            }
            return g;
        }
    }
}
