using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using JA.IfcViewer.Ifc;
using JA.IfcViewer.Viewer;

namespace JA.IfcViewer.SmartViews
{
    [DataContract]
    public sealed class SvCondition
    {
        [DataMember] public string Property = PropertyReader.KClass;
        [DataMember] public string Op = Ops.Equal;
        [DataMember] public string Value = "";
        [DataMember] public string Value2 = "";

        public SvCondition Clone() => new SvCondition { Property = Property, Op = Op, Value = Value, Value2 = Value2 };
        public override string ToString() => Ops.NoValue(Op) ? $"{Short(Property)} {Op}" : Op == Ops.Between ? $"{Short(Property)} entre {Value} e {Value2}" : $"{Short(Property)} {Op} “{Value}”";
        public static string Short(string key) => key != null && key.StartsWith(PropertyReader.GElement + ".") ? key.Substring(PropertyReader.GElement.Length + 1) : key;
    }

    [DataContract]
    public sealed class SvRule
    {
        [DataMember] public bool Enabled = true;
        [DataMember] public List<SvCondition> Conditions = new List<SvCondition>();
        [DataMember] public bool Any;                 // OR between the conditions (default AND)
        [DataMember] public string Action = Actions.Show;
        [DataMember] public string Color = "#E8A33D";
        [DataMember] public string ColorBy = PropertyReader.KClass;

        public SvRule Clone() => new SvRule { Enabled = Enabled, Conditions = Conditions.Select(c => c.Clone()).ToList(), Any = Any, Action = Action, Color = Color, ColorBy = ColorBy };

        public override string ToString()
        {
            string who = Conditions.Count == 0 ? "todos" : string.Join(Any ? " OU " : " E ", Conditions.Select(c => c.ToString()));
            string what = Action == Actions.ColorByValue ? "Colorir por " + SvCondition.Short(ColorBy) : Action == Actions.Color ? "Colorir " + Color : Action;
            return what + ": " + who;
        }
    }

    [DataContract]
    public sealed class SmartView
    {
        [DataMember] public string Name = "Nova Smart View";
        [DataMember] public string Folder = "Minhas";
        [DataMember] public bool StartHidden;
        [DataMember] public List<SvRule> Rules = new List<SvRule>();
        [DataMember] public string Description = "";
        public bool BuiltIn;

        public SmartView Clone(string name = null) => new SmartView
        {
            Name = name ?? Name, Folder = BuiltIn ? "Minhas" : Folder, StartHidden = StartHidden, Description = Description,
            Rules = Rules.Select(r => r.Clone()).ToList()
        };
        public override string ToString() => Name;
    }

    public static class Ops
    {
        public const string Equal = "é igual a", NotEqual = "é diferente de", Contains = "contém", NotContains = "não contém",
            Starts = "começa com", Ends = "termina com", Greater = "maior que", Less = "menor que", GreaterEq = "maior ou igual a",
            LessEq = "menor ou igual a", Between = "entre", Exists = "existe", NotExists = "não existe", Like = "parece com (* ?)",
            OneOf = "é um de (;)", NotOneOf = "não é um de (;)";
        public static readonly string[] All = { Equal, NotEqual, Contains, NotContains, Starts, Ends, Greater, Less, GreaterEq, LessEq, Between, Exists, NotExists, Like, OneOf, NotOneOf };
        public static bool NoValue(string op) => op == Exists || op == NotExists;
    }

    public static class Actions
    {
        public const string Show = "Mostrar", Hide = "Ocultar", Color = "Colorir", ColorByValue = "Colorir por valor",
            Transparent = "Transparente", Opaque = "Opaco", Original = "Cor original";
        public static readonly string[] All = { Show, Hide, Color, ColorByValue, Transparent, Opaque, Original };
    }

    public sealed class LegendEntry
    {
        public string Value;
        public uint Color;
        public readonly List<int> Indices = new List<int>();
        public string Property;
    }

    /// <summary>
    /// Smart Views, the BIMcollab Zoom way: an ordered list of rules, each one picking elements by conditions on any
    /// property (attributes, storey, type, material, classification, layer, every Pset/Qto, "*.Nome" = in any set) and doing
    /// one thing to them — show, hide, colour, colour by value (with a legend), transparent, opaque, original colour.
    /// </summary>
    public static class SmartViewEngine
    {
        public static readonly CultureInfo Br = PropertyReader.Br;

        public static PropValue Lookup(Dictionary<string, PropValue> flat, string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (flat.TryGetValue(key, out PropValue v)) return v;
            if (key.StartsWith("*."))
            {
                string tail = key.Substring(1);
                foreach (var kv in flat) if (kv.Key.EndsWith(tail, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            }
            return null;
        }

        public static bool Matches(IfcObject o, SvCondition c)
        {
            var flat = PropertyReader.Flat(o);
            PropValue v = Lookup(flat, c.Property);
            switch (c.Op)
            {
                case Ops.Exists: return v != null && v.Text.Length > 0;
                case Ops.NotExists: return v == null || v.Text.Length == 0;
            }
            string text = v?.Text ?? "";
            string want = c.Value ?? "";
            bool isClass = string.Equals(c.Property, PropertyReader.KClass, StringComparison.OrdinalIgnoreCase);
            switch (c.Op)
            {
                case Ops.Equal: return v != null && (Eq(text, want) || isClass && ClassEq(text, want) || NumEq(v, want));
                case Ops.NotEqual: return v == null || !(Eq(text, want) || isClass && ClassEq(text, want) || NumEq(v, want));
                case Ops.Contains: return v != null && text.IndexOf(want, StringComparison.CurrentCultureIgnoreCase) >= 0;
                case Ops.NotContains: return v == null || text.IndexOf(want, StringComparison.CurrentCultureIgnoreCase) < 0;
                case Ops.Starts: return v != null && text.StartsWith(want, StringComparison.CurrentCultureIgnoreCase);
                case Ops.Ends: return v != null && text.EndsWith(want, StringComparison.CurrentCultureIgnoreCase);
                case Ops.Like: return v != null && Wildcard(want).IsMatch(text);
                case Ops.OneOf:
                case Ops.NotOneOf:
                {
                    var items = want.Split(';').Select(x => x.Trim()).Where(x => x.Length > 0);
                    bool any = v != null && items.Any(x => Eq(text, x) || isClass && ClassEq(text, x) || NumEq(v, x));
                    return c.Op == Ops.OneOf ? any : !any;
                }
            }
            double? n = Number(v);
            double? a = ParseNumber(want);
            if (n == null) return false;
            switch (c.Op)
            {
                case Ops.Greater: return a != null && n > a;
                case Ops.Less: return a != null && n < a;
                case Ops.GreaterEq: return a != null && n >= a - 1e-9;
                case Ops.LessEq: return a != null && n <= a + 1e-9;
                case Ops.Between:
                {
                    double? b = ParseNumber(c.Value2);
                    if (a == null || b == null) return false;
                    double lo = Math.Min(a.Value, b.Value), hi = Math.Max(a.Value, b.Value);
                    return n >= lo - 1e-9 && n <= hi + 1e-9;
                }
            }
            return false;
        }

        private static bool Eq(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.CurrentCultureIgnoreCase);

        /// <summary>IfcWall also matches IfcWallStandardCase / IfcWallElementedCase.</summary>
        private static bool ClassEq(string actual, string want)
        {
            if (!actual.StartsWith(want, StringComparison.OrdinalIgnoreCase)) return false;
            string rest = actual.Substring(want.Length);
            return rest.Equals("StandardCase", StringComparison.OrdinalIgnoreCase) || rest.Equals("ElementedCase", StringComparison.OrdinalIgnoreCase);
        }

        private static bool NumEq(PropValue v, string want)
        {
            double? n = Number(v), a = ParseNumber(want);
            return n != null && a != null && Math.Abs(n.Value - a.Value) <= 1e-9 * Math.Max(1, Math.Abs(a.Value));
        }

        private static double? Number(PropValue v)
        {
            if (v == null) return null;
            if (v.Number.HasValue) return v.Number;
            return ParseNumber(v.Text);
        }

        public static double? ParseNumber(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            var m = Regex.Match(s, @"^[-+]?\d[\d.,]*([eE][-+]?\d+)?");
            if (!m.Success) return null;
            string t = m.Value;
            if (t.Contains(",") && t.Contains(".")) t = t.Replace(".", "").Replace(",", ".");
            else t = t.Replace(",", ".");
            return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : (double?)null;
        }

        private static readonly Dictionary<string, Regex> WildCache = new Dictionary<string, Regex>();
        private static Regex Wildcard(string p)
        {
            lock (WildCache)
            {
                if (WildCache.TryGetValue(p, out Regex r)) return r;
                r = new Regex("^" + Regex.Escape(p).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                WildCache[p] = r;
                return r;
            }
        }

        public static bool Matches(IfcObject o, SvRule r)
        {
            if (r.Conditions.Count == 0) return true;
            return r.Any ? r.Conditions.Any(c => Matches(o, c)) : r.Conditions.All(c => Matches(o, c));
        }

        /// <summary>Applies the Smart View to the scene's Sv arrays (null = the plain view). Returns the legend.</summary>
        public static List<LegendEntry> Apply(Scene s, SmartView sv)
        {
            int n = s.Count;
            var legend = new List<LegendEntry>();
            for (int i = 0; i < n; i++)
            {
                IfcObject o = s.Objects[i];
                bool space = o.Type == "IFCSPACE";
                s.SvHidden[i] = (sv != null && sv.StartHidden) || (space && !s.ShowSpaces);
                s.SvColor[i] = 0;
                s.SvTransparent[i] = false;
            }
            if (sv == null) return legend;
            var byValue = new Dictionary<string, LegendEntry>(StringComparer.CurrentCultureIgnoreCase);
            foreach (SvRule r in sv.Rules)
            {
                if (!r.Enabled) continue;
                uint color = ParseColor(r.Color);
                for (int i = 0; i < n; i++)
                {
                    IfcObject o = s.Objects[i];
                    if (!o.HasGeometry) continue;
                    if (!Matches(o, r)) continue;
                    switch (r.Action)
                    {
                        case Actions.Show: s.SvHidden[i] = false; break;
                        case Actions.Hide: s.SvHidden[i] = true; break;
                        case Actions.Color: s.SvColor[i] = color; break;
                        case Actions.Original: s.SvColor[i] = 0; break;
                        case Actions.Transparent: s.SvTransparent[i] = true; break;
                        case Actions.Opaque: s.SvTransparent[i] = false; break;
                        case Actions.ColorByValue:
                        {
                            PropValue v = Lookup(PropertyReader.Flat(o), r.ColorBy);
                            string key = v == null || v.Text.Length == 0 ? "(sem valor)" : v.Text;
                            if (!byValue.TryGetValue(key, out LegendEntry le))
                            {
                                le = new LegendEntry { Value = key, Property = r.ColorBy };
                                byValue[key] = le;
                                legend.Add(le);
                            }
                            le.Indices.Add(i);
                            break;
                        }
                    }
                }
            }
            // colours for the values: sorted (numbers by value), distinct hues, grey for "no value"
            var sorted = legend.OrderBy(l => l.Value == "(sem valor)" ? 1 : 0)
                               .ThenBy(l => ParseNumber(l.Value) ?? double.MaxValue)
                               .ThenBy(l => l.Value, StringComparer.CurrentCultureIgnoreCase).ToList();
            int k = 0;
            foreach (LegendEntry le in sorted)
            {
                le.Color = le.Value == "(sem valor)" ? 0xFFB4B4B4 : Palette(k++);
                foreach (int i in le.Indices) s.SvColor[i] = le.Color;
            }
            return sorted;
        }

        private static readonly uint[] Base =
        {
            0xFF4E79A7, 0xFFF28E2B, 0xFFE15759, 0xFF76B7B2, 0xFF59A14F, 0xFFEDC948, 0xFFB07AA1, 0xFFFF9DA7, 0xFF9C755F, 0xFFBAB0AC,
            0xFF1F7A5A, 0xFF8CD17D, 0xFFD37295, 0xFF499894, 0xFFF1CE63, 0xFF86BCB6, 0xFFE8A33D, 0xFF6B4FA0, 0xFF2F8FE8, 0xFFA0CBE8
        };

        public static uint Palette(int k)
        {
            if (k < Base.Length) return Base[k];
            double h = (k * 137.508) % 360, sat = 0.55 + 0.25 * ((k / 7) % 2), val = 0.85 - 0.2 * ((k / 3) % 2);
            return Hsv(h, sat, val);
        }

        private static uint Hsv(double h, double s, double v)
        {
            double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
            double r = 0, g = 0, b = 0;
            if (h < 60) { r = c; g = x; } else if (h < 120) { r = x; g = c; } else if (h < 180) { g = c; b = x; }
            else if (h < 240) { g = x; b = c; } else if (h < 300) { r = x; b = c; } else { r = c; b = x; }
            return IfcNames.Argb(255, (int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
        }

        public static uint ParseColor(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0xFFE8A33D;
            s = s.Trim().TrimStart('#');
            if (uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v))
                return s.Length <= 6 ? 0xFF000000 | v : v;
            return 0xFFE8A33D;
        }

        public static string ColorText(uint c) => "#" + (c & 0xFFFFFF).ToString("X6");

        // ================================================================== examples

        private static SvCondition C(string p, string op, string v = "", string v2 = "") => new SvCondition { Property = p, Op = op, Value = v, Value2 = v2 };
        private static SvRule R(string action, params SvCondition[] c) => new SvRule { Action = action, Conditions = c.ToList() };
        private static SvRule Rc(string action, string color, params SvCondition[] c) => new SvRule { Action = action, Color = color, Conditions = c.ToList() };
        private static SvRule By(string key, params SvCondition[] c) => new SvRule { Action = Actions.ColorByValue, ColorBy = key, Conditions = c.ToList() };

        public static List<SmartView> Examples()
        {
            const string F = "Exemplos JA";
            var l = new List<SmartView>
            {
                new SmartView { Name = "Por classe IFC", Folder = F, Description = "Cada classe (paredes, lajes, portas…) numa cor.", Rules = { By(PropertyReader.KClass) } },
                new SmartView { Name = "Por pavimento", Folder = F, Rules = { By(PropertyReader.KStorey) } },
                new SmartView { Name = "Por tipo", Folder = F, Rules = { By(PropertyReader.KType) } },
                new SmartView { Name = "Por material", Folder = F, Rules = { By(PropertyReader.KMaterial) } },
                new SmartView { Name = "Por modelo (disciplina)", Folder = F, Rules = { By(PropertyReader.KModel) } },
                new SmartView { Name = "Por camada (layer)", Folder = F, Rules = { By(PropertyReader.KLayer) } },
                new SmartView
                {
                    Name = "Paredes externas e internas", Folder = F, StartHidden = true,
                    Rules = { R(Actions.Show, C(PropertyReader.KClass, Ops.OneOf, "IfcWall; IfcCurtainWall")), By("*.IsExternal") }
                },
                new SmartView
                {
                    Name = "Estrutura (portantes)", Folder = F, StartHidden = true,
                    Rules =
                    {
                        new SvRule { Action = Actions.Show, Any = true, Conditions = { C(PropertyReader.KClass, Ops.OneOf, "IfcColumn; IfcBeam; IfcFooting; IfcPile; IfcMember"), C("*.LoadBearing", Ops.Equal, "Sim") } },
                        By(PropertyReader.KClass)
                    }
                },
                new SmartView { Name = "Resistência ao fogo", Folder = F, Rules = { R(Actions.Transparent), By("*.FireRating", C("*.FireRating", Ops.Exists)), R(Actions.Opaque, C("*.FireRating", Ops.Exists)) } },
                new SmartView { Name = "Elementos sem pavimento", Folder = F, Rules = { R(Actions.Transparent), Rc(Actions.Color, "#E15759", C(PropertyReader.KStorey, Ops.NotExists)), R(Actions.Opaque, C(PropertyReader.KStorey, Ops.NotExists)) } },
                new SmartView { Name = "Elementos sem material", Folder = F, Rules = { R(Actions.Transparent), Rc(Actions.Color, "#E15759", C(PropertyReader.KMaterial, Ops.NotExists)), R(Actions.Opaque, C(PropertyReader.KMaterial, Ops.NotExists)) } },
                new SmartView { Name = "Elementos sem classificação", Folder = F, Rules = { R(Actions.Transparent), Rc(Actions.Color, "#E15759", C(PropertyReader.KClassif, Ops.NotExists)), R(Actions.Opaque, C(PropertyReader.KClassif, Ops.NotExists)) } },
                new SmartView { Name = "Genéricos (IfcBuildingElementProxy)", Folder = F, Rules = { R(Actions.Transparent), Rc(Actions.Color, "#F28E2B", C(PropertyReader.KClass, Ops.Equal, "IfcBuildingElementProxy")), R(Actions.Opaque, C(PropertyReader.KClass, Ops.Equal, "IfcBuildingElementProxy")) } },
                new SmartView { Name = "Ambientes por nome", Folder = F, StartHidden = true, Rules = { R(Actions.Show, C(PropertyReader.KClass, Ops.Equal, "IfcSpace")), By(PropertyReader.KName, C(PropertyReader.KClass, Ops.Equal, "IfcSpace")) } },
                new SmartView
                {
                    Name = "Portas e janelas", Folder = F,
                    Rules = { R(Actions.Transparent), By(PropertyReader.KType, C(PropertyReader.KClass, Ops.OneOf, "IfcDoor; IfcWindow")), R(Actions.Opaque, C(PropertyReader.KClass, Ops.OneOf, "IfcDoor; IfcWindow")) }
                },
                new SmartView { Name = "Instalações (tubos, dutos, terminais)", Folder = F, StartHidden = true, Rules = { R(Actions.Show, C(PropertyReader.KClass, Ops.Like, "IfcFlow*")), R(Actions.Show, C(PropertyReader.KClass, Ops.Like, "Ifc*Segment")), R(Actions.Show, C(PropertyReader.KClass, Ops.Like, "Ifc*Fitting")), R(Actions.Show, C(PropertyReader.KClass, Ops.Like, "Ifc*Terminal")), By(PropertyReader.KClass) } },
            };
            foreach (SmartView v in l) v.BuiltIn = true;
            return l;
        }

        // ================================================================== storage

        public static string StorePath => Path.Combine(Log.Folder, "smartviews.json");

        public static List<SmartView> LoadUser()
        {
            try { if (File.Exists(StorePath)) return Read(StorePath); }
            catch (Exception ex) { Log.Error("ler smart views", ex); }
            return new List<SmartView>();
        }

        public static void SaveUser(List<SmartView> list)
        {
            try { Directory.CreateDirectory(Log.Folder); Write(StorePath, list.Where(v => !v.BuiltIn).ToList()); }
            catch (Exception ex) { Log.Error("gravar smart views", ex); }
        }

        public static List<SmartView> Read(string path)
        {
            var ser = new DataContractJsonSerializer(typeof(List<SmartView>));
            using (var fs = File.OpenRead(path))
            {
                var l = (List<SmartView>)ser.ReadObject(fs) ?? new List<SmartView>();
                foreach (SmartView v in l)
                {
                    if (v.Rules == null) v.Rules = new List<SvRule>();
                    foreach (SvRule r in v.Rules) if (r.Conditions == null) r.Conditions = new List<SvCondition>();
                    if (string.IsNullOrWhiteSpace(v.Folder)) v.Folder = "Minhas";
                }
                return l;
            }
        }

        public static void Write(string path, List<SmartView> list)
        {
            var ser = new DataContractJsonSerializer(typeof(List<SmartView>));
            using (var ms = new MemoryStream())
            {
                ser.WriteObject(ms, list);
                File.WriteAllText(path, Encoding.UTF8.GetString(ms.ToArray()), new UTF8Encoding(false));
            }
        }
    }
}
