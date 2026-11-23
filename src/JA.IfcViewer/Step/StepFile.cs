using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace JA.IfcViewer.Step
{
    /// <summary>Reference to another instance: #123.</summary>
    public sealed class StepRef
    {
        public readonly int Id;
        public StepRef(int id) { Id = id; }
        public override string ToString() => "#" + Id;
    }

    /// <summary>Enumeration or boolean: .ELEMENT. / .T. / .F. / .U.</summary>
    public sealed class StepEnum
    {
        public readonly string Value;
        public StepEnum(string v) { Value = v; }
        public override string ToString() => Value;
    }

    /// <summary>Typed value of a select: IFCLABEL('x'), IFCLENGTHMEASURE(2.5), IFCPARAMETERVALUE(0.)…</summary>
    public sealed class StepTyped
    {
        public readonly string Type;
        public readonly object Value;
        public StepTyped(string t, object v) { Type = t; Value = v; }
        public override string ToString() => Type + "(" + Value + ")";
    }

    /// <summary>The '*' of a derived attribute.</summary>
    public sealed class StepStar
    {
        public static readonly StepStar Value = new StepStar();
        private StepStar() { }
        public override string ToString() => "*";
    }

    /// <summary>One parsed instance: #Id = TYPE(Args).</summary>
    public sealed class StepEntity
    {
        public int Id;
        public string Type;
        public object[] Args;

        public int Count => Args.Length;
        public object this[int i] => i >= 0 && i < Args.Length ? Args[i] : null;

        public int Ref(int i) => this[i] is StepRef r ? r.Id : 0;
        public string Str(int i) => StepFile.AsString(this[i]);
        public double Num(int i, double def = 0) => StepFile.AsNumber(this[i], def);
        public bool HasNum(int i) => StepFile.IsNumber(this[i]);
        public string Enum(int i) => this[i] is StepEnum e ? e.Value : null;
        public object[] List(int i) => this[i] as object[] ?? Array.Empty<object>();
        public bool? Bool(int i)
        {
            object o = this[i];
            if (o is StepTyped t) o = t.Value;
            if (o is StepEnum e) { if (e.Value == "T") return true; if (e.Value == "F") return false; }
            return null;
        }
        public List<int> Refs(int i)
        {
            var r = new List<int>();
            foreach (object o in List(i)) if (o is StepRef s) r.Add(s.Id);
            return r;
        }
        public override string ToString() => "#" + Id + "=" + Type;
    }

    /// <summary>
    /// ISO 10303-21 (STEP physical file) reader for IFC. The file is kept as bytes and indexed in one pass
    /// (id → offset and type); instances are parsed only when asked (Get), so a 500 MB model costs little more
    /// than its size in memory. Thread safe for reading once built.
    /// Strings: '' and \\ escapes, \X\hh, \S\c, \X2\…\X0\ (UTF-16), \X4\…\X0\ (UTF-32); raw bytes as UTF-8 (falls
    /// back to Latin-1 when not valid UTF-8, as older exporters write).
    /// </summary>
    public sealed class StepFile
    {
        private readonly byte[] _d;
        private int[] _offDense;                 // id -> offset of '(' of the arguments (0 = none)
        private string[] _typeDense;
        private Dictionary<int, int> _offSparse;
        private Dictionary<int, string> _typeSparse;
        private readonly Dictionary<string, List<int>> _byType = new Dictionary<string, List<int>>();

        public string Schema { get; private set; } = "";
        public string OriginatingSystem { get; private set; } = "";
        public string Preprocessor { get; private set; } = "";
        public string FileTimeStamp { get; private set; } = "";
        public string Author { get; private set; } = "";
        public string Organization { get; private set; } = "";
        public int InstanceCount { get; private set; }
        public int MaxId { get; private set; }

        public IEnumerable<string> Types => _byType.Keys;

        private StepFile(byte[] d) { _d = d; }

        public static StepFile Load(byte[] data, Action<double> progress = null)
        {
            var f = new StepFile(data);
            f.Index(progress);
            return f;
        }

        public IReadOnlyList<int> OfType(string type) =>
            _byType.TryGetValue(type, out List<int> l) ? (IReadOnlyList<int>)l : Array.Empty<int>();

        public string TypeOf(int id)
        {
            if (_typeDense != null) return id > 0 && id < _typeDense.Length ? _typeDense[id] : null;
            return _typeSparse.TryGetValue(id, out string t) ? t : null;
        }

        private int OffsetOf(int id)
        {
            if (_offDense != null) return id > 0 && id < _offDense.Length ? _offDense[id] : 0;
            return _offSparse.TryGetValue(id, out int o) ? o : 0;
        }

        public bool Exists(int id) => OffsetOf(id) > 0;

        /// <summary>The instance, parsed now (null when the id does not exist).</summary>
        public StepEntity Get(int id)
        {
            int off = OffsetOf(id);
            if (off <= 0) return null;
            int p = off;
            object[] args;
            try { args = ParseList(ref p); }
            catch { args = Array.Empty<object>(); }
            return new StepEntity { Id = id, Type = TypeOf(id), Args = args };
        }

        // ================================================================== indexing

        private void Index(Action<double> progress)
        {
            byte[] d = _d;
            int n = d.Length;
            ReadHeader();
            int p = Find("DATA;", 0);
            if (p < 0) throw new FormatException("Arquivo sem seção DATA: não parece um IFC (STEP).");
            p += 5;
            var ids = new List<int>(Math.Max(16, n / 60));
            var offs = new List<int>(ids.Capacity);
            var types = new List<string>(ids.Capacity);
            var names = new Dictionary<ulong, string>();
            int maxId = 0, nextReport = 0;
            while (p < n)
            {
                p = SkipBlank(p);
                if (p >= n) break;
                byte c = d[p];
                if (c != '#')
                {
                    if (c == 'E' && Match(p, "ENDSEC")) { int q = Find("DATA;", p); if (q < 0) break; p = q + 5; continue; }
                    p = SkipToSemicolon(p) + 1;
                    continue;
                }
                p++;
                int id = 0;
                while (p < n && d[p] >= '0' && d[p] <= '9') { id = id * 10 + (d[p] - '0'); p++; }
                p = SkipBlank(p);
                if (p >= n || d[p] != '=') { p = SkipToSemicolon(p) + 1; continue; }
                p = SkipBlank(p + 1);
                string type;
                int argStart;
                if (p < n && d[p] == '(')
                {
                    // complex instance: (IFCA(...) IFCB(...)) — keep the first part
                    int q = SkipBlank(p + 1);
                    int s = q;
                    while (q < n && IsNameChar(d[q])) q++;
                    type = Intern(names, s, q);
                    q = SkipBlank(q);
                    argStart = q < n && d[q] == '(' ? q : 0;
                }
                else
                {
                    int s = p;
                    while (p < n && IsNameChar(d[p])) p++;
                    type = Intern(names, s, p);
                    p = SkipBlank(p);
                    argStart = p < n && d[p] == '(' ? p : 0;
                }
                p = SkipToSemicolon(p) + 1;
                if (id <= 0 || argStart <= 0 || type.Length == 0) continue;
                ids.Add(id); offs.Add(argStart); types.Add(type);
                if (id > maxId) maxId = id;
                if (!_byType.TryGetValue(type, out List<int> l)) _byType[type] = l = new List<int>();
                l.Add(id);
                if (progress != null && p > nextReport) { progress((double)p / n); nextReport = p + n / 100 + 1; }
            }
            InstanceCount = ids.Count;
            MaxId = maxId;
            if (maxId <= ids.Count * 4L + 1024)
            {
                _offDense = new int[maxId + 1];
                _typeDense = new string[maxId + 1];
                for (int i = 0; i < ids.Count; i++) { _offDense[ids[i]] = offs[i]; _typeDense[ids[i]] = types[i]; }
            }
            else
            {
                _offSparse = new Dictionary<int, int>(ids.Count);
                _typeSparse = new Dictionary<int, string>(ids.Count);
                for (int i = 0; i < ids.Count; i++) { _offSparse[ids[i]] = offs[i]; _typeSparse[ids[i]] = types[i]; }
            }
            progress?.Invoke(1);
        }

        private void ReadHeader()
        {
            int end = Find("ENDSEC", 0);
            if (end < 0) end = Math.Min(_d.Length, 65536);
            int p = Find("FILE_SCHEMA", 0);
            if (p >= 0 && p < end)
            {
                int q = p + 11;
                q = SkipBlank(q);
                if (q < _d.Length && _d[q] == '(')
                {
                    try
                    {
                        object[] a = ParseList(ref q);
                        if (a.Length > 0 && a[0] is object[] l && l.Length > 0) Schema = AsString(l[0]) ?? "";
                    }
                    catch { }
                }
            }
            p = Find("FILE_NAME", 0);
            if (p >= 0 && p < end)
            {
                int q = SkipBlank(p + 9);
                if (q < _d.Length && _d[q] == '(')
                {
                    try
                    {
                        object[] a = ParseList(ref q);
                        if (a.Length > 1) FileTimeStamp = AsString(a[1]) ?? "";
                        if (a.Length > 2 && a[2] is object[] au) Author = string.Join(", ", Strings(au));
                        if (a.Length > 3 && a[3] is object[] org) Organization = string.Join(", ", Strings(org));
                        if (a.Length > 4) Preprocessor = AsString(a[4]) ?? "";
                        if (a.Length > 5) OriginatingSystem = AsString(a[5]) ?? "";
                    }
                    catch { }
                }
            }
        }

        private static IEnumerable<string> Strings(object[] l)
        {
            foreach (object o in l) { string s = AsString(o); if (!string.IsNullOrWhiteSpace(s)) yield return s; }
        }

        private static bool IsNameChar(byte c) => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_';

        private string Intern(Dictionary<ulong, string> names, int s, int e)
        {
            ulong h = 14695981039346656037UL;
            for (int i = s; i < e; i++) { h ^= (byte)(_d[i] & 0xDF); h *= 1099511628211UL; }   // case-insensitive
            h ^= (ulong)(e - s) << 56;
            if (names.TryGetValue(h, out string t)) return t;
            t = Encoding.ASCII.GetString(_d, s, e - s).ToUpperInvariant();
            names[h] = t;
            return t;
        }

        private bool Match(int p, string s)
        {
            if (p + s.Length > _d.Length) return false;
            for (int i = 0; i < s.Length; i++) if (_d[p + i] != s[i]) return false;
            return true;
        }

        private int Find(string s, int from)
        {
            byte first = (byte)s[0];
            int last = _d.Length - s.Length;
            for (int i = from; i <= last; i++)
            {
                if (_d[i] != first) continue;
                if (Match(i, s)) return i;
            }
            return -1;
        }

        private int SkipBlank(int p)
        {
            byte[] d = _d;
            int n = d.Length;
            while (p < n)
            {
                byte c = d[p];
                if (c == ' ' || c == '\r' || c == '\n' || c == '\t') { p++; continue; }
                if (c == '/' && p + 1 < n && d[p + 1] == '*')
                {
                    p += 2;
                    while (p + 1 < n && !(d[p] == '*' && d[p + 1] == '/')) p++;
                    p += 2;
                    continue;
                }
                break;
            }
            return p;
        }

        /// <summary>Position of the ';' that ends the instance starting at p (strings skipped).</summary>
        private int SkipToSemicolon(int p)
        {
            byte[] d = _d;
            int n = d.Length;
            while (p < n)
            {
                byte c = d[p];
                if (c == ';') return p;
                if (c == '\'')
                {
                    p++;
                    while (p < n)
                    {
                        if (d[p] == '\'') { if (p + 1 < n && d[p + 1] == '\'') { p += 2; continue; } break; }
                        p++;
                    }
                }
                else if (c == '/' && p + 1 < n && d[p + 1] == '*')
                {
                    p += 2;
                    while (p + 1 < n && !(d[p] == '*' && d[p + 1] == '/')) p++;
                    p++;
                }
                p++;
            }
            return n;
        }

        // ================================================================== parsing

        private object[] ParseList(ref int p)
        {
            byte[] d = _d;
            // at '('
            p++;
            var items = new List<object>(4);
            p = SkipBlank(p);
            if (p < d.Length && d[p] == ')') { p++; return Array.Empty<object>(); }
            while (p < d.Length)
            {
                items.Add(ParseValue(ref p));
                p = SkipBlank(p);
                if (p >= d.Length) break;
                if (d[p] == ',') { p++; continue; }
                if (d[p] == ')') { p++; break; }
                throw new FormatException("STEP: esperado ',' ou ')' na posição " + p);
            }
            return items.ToArray();
        }

        private object ParseValue(ref int p)
        {
            byte[] d = _d;
            p = SkipBlank(p);
            byte c = d[p];
            switch (c)
            {
                case (byte)'$': p++; return null;
                case (byte)'*': p++; return StepStar.Value;
                case (byte)'#':
                {
                    p++;
                    int id = 0;
                    while (p < d.Length && d[p] >= '0' && d[p] <= '9') { id = id * 10 + (d[p] - '0'); p++; }
                    return new StepRef(id);
                }
                case (byte)'\'': return ParseString(ref p);
                case (byte)'.':
                {
                    int s = ++p;
                    while (p < d.Length && d[p] != '.') p++;
                    string v = Encoding.ASCII.GetString(d, s, p - s).ToUpperInvariant();
                    p++;
                    return v == "T" ? EnumT : v == "F" ? EnumF : v == "U" ? EnumU : new StepEnum(v);
                }
                case (byte)'"':
                {
                    int s = ++p;
                    while (p < d.Length && d[p] != '"') p++;
                    string v = Encoding.ASCII.GetString(d, s, p - s);
                    p++;
                    return v;
                }
                case (byte)'(': return ParseList(ref p);
            }
            if (c == '-' || c == '+' || (c >= '0' && c <= '9')) return ParseNumber(ref p);
            if (IsNameChar(c))
            {
                int s = p;
                while (p < d.Length && IsNameChar(d[p])) p++;
                string t = Encoding.ASCII.GetString(d, s, p - s).ToUpperInvariant();
                p = SkipBlank(p);
                if (p < d.Length && d[p] == '(')
                {
                    object[] inner = ParseList(ref p);
                    return new StepTyped(t, inner.Length == 1 ? inner[0] : inner);
                }
                return new StepEnum(t);
            }
            throw new FormatException("STEP: valor inesperado na posição " + p);
        }

        private static readonly StepEnum EnumT = new StepEnum("T"), EnumF = new StepEnum("F"), EnumU = new StepEnum("U");

        private static readonly double[] Pow10 = BuildPow10();
        private static double[] BuildPow10() { var a = new double[64]; double v = 1; for (int i = 0; i < 64; i++) { a[i] = v; v *= 10; } return a; }

        private object ParseNumber(ref int p)
        {
            byte[] d = _d;
            int n = d.Length, start = p;
            bool neg = false;
            if (d[p] == '-') { neg = true; p++; } else if (d[p] == '+') p++;
            long mant = 0;
            int digits = 0, exp = 0;
            bool any = false, real = false;
            while (p < n && d[p] >= '0' && d[p] <= '9')
            {
                any = true;
                if (digits < 18) { mant = mant * 10 + (d[p] - '0'); if (mant != 0) digits++; } else exp++;
                p++;
            }
            if (p < n && d[p] == '.')
            {
                real = true;
                p++;
                while (p < n && d[p] >= '0' && d[p] <= '9')
                {
                    any = true;
                    if (digits < 18) { mant = mant * 10 + (d[p] - '0'); if (mant != 0) digits++; exp--; }
                    p++;
                }
            }
            if (p < n && (d[p] == 'E' || d[p] == 'e'))
            {
                real = true;
                p++;
                bool eneg = false;
                if (p < n && d[p] == '-') { eneg = true; p++; } else if (p < n && d[p] == '+') p++;
                int e = 0;
                while (p < n && d[p] >= '0' && d[p] <= '9') { e = e * 10 + (d[p] - '0'); p++; }
                exp += eneg ? -e : e;
            }
            if (!any)
            {
                string s = Encoding.ASCII.GetString(d, start, Math.Max(1, p - start));
                double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v0);
                return v0;
            }
            double v = mant;
            if (exp > 0) v *= exp < 64 ? Pow10[exp] : Math.Pow(10, exp);
            else if (exp < 0) v /= -exp < 64 ? Pow10[-exp] : Math.Pow(10, -exp);
            if (neg) v = -v;
            if (!real && v >= int.MinValue && v <= int.MaxValue) return (double)(long)v;
            return v;
        }

        private static readonly Encoding Utf8Strict = new UTF8Encoding(false, true);
        private static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

        private string ParseString(ref int p)
        {
            byte[] d = _d;
            int n = d.Length;
            p++;   // opening quote
            StringBuilder sb = null;
            int runStart = p;
            var run = new List<byte>();
            bool simple = true;
            int s0 = p;
            while (p < n)
            {
                byte c = d[p];
                if (c == '\'')
                {
                    if (p + 1 < n && d[p + 1] == '\'') { simple = false; break; }
                    break;
                }
                if (c == '\\' || c >= 0x80) { simple = false; break; }
                p++;
            }
            if (simple)
            {
                string r = Encoding.ASCII.GetString(d, s0, p - s0);
                p++;
                return r;
            }
            // slow path
            p = s0;
            sb = new StringBuilder();
            void Flush()
            {
                if (run.Count == 0) return;
                byte[] b = run.ToArray();
                try { sb.Append(Utf8Strict.GetString(b)); }
                catch (DecoderFallbackException) { sb.Append(Latin1.GetString(b)); }
                run.Clear();
            }
            while (p < n)
            {
                byte c = d[p];
                if (c == '\'')
                {
                    if (p + 1 < n && d[p + 1] == '\'') { run.Add((byte)'\''); p += 2; continue; }
                    p++;
                    break;
                }
                if (c == '\\' && p + 1 < n)
                {
                    byte k = d[p + 1];
                    if (k == '\\') { run.Add((byte)'\\'); p += 2; continue; }
                    if (k == 'X' && p + 2 < n && d[p + 2] == '\\' && p + 4 < n)
                    {
                        Flush();
                        sb.Append((char)Hex(p + 3, 2));
                        p += 5;
                        continue;
                    }
                    if (k == 'X' && p + 3 < n && (d[p + 2] == '2' || d[p + 2] == '4') && d[p + 3] == '\\')
                    {
                        Flush();
                        int w = d[p + 2] == '2' ? 4 : 8;
                        p += 4;
                        while (p + w <= n && !(d[p] == '\\' && p + 3 < n && d[p + 1] == 'X' && d[p + 2] == '0' && d[p + 3] == '\\'))
                        {
                            int cp = Hex(p, w);
                            if (w == 4) sb.Append((char)cp);
                            else { try { sb.Append(char.ConvertFromUtf32(cp)); } catch { } }
                            p += w;
                        }
                        p += 4;   // \X0\
                        continue;
                    }
                    if (k == 'S' && p + 3 < n && d[p + 2] == '\\')
                    {
                        Flush();
                        sb.Append((char)(d[p + 3] + 128));
                        p += 4;
                        continue;
                    }
                    if (k == 'P' && p + 3 < n && d[p + 3] == '\\') { p += 4; continue; }   // code page switch
                    if (k == 'N' && p + 2 < n && d[p + 2] == '\\') { Flush(); sb.Append('\n'); p += 3; continue; }
                }
                run.Add(c);
                p++;
            }
            Flush();
            return sb.ToString();
        }

        private int Hex(int p, int len)
        {
            int v = 0;
            for (int i = 0; i < len && p + i < _d.Length; i++)
            {
                byte c = _d[p + i];
                int h = c >= '0' && c <= '9' ? c - '0' : c >= 'A' && c <= 'F' ? c - 'A' + 10 : c >= 'a' && c <= 'f' ? c - 'a' + 10 : 0;
                v = v * 16 + h;
            }
            return v;
        }

        // ================================================================== value helpers

        public static string AsString(object o)
        {
            switch (o)
            {
                case null: return null;
                case string s: return s;
                case StepTyped t: return AsString(t.Value);
                case StepEnum e: return e.Value;
                case double d: return d.ToString(CultureInfo.InvariantCulture);
                case StepRef r: return r.ToString();
                default: return null;
            }
        }

        public static bool IsNumber(object o) => o is double || (o is StepTyped t && t.Value is double);

        public static double AsNumber(object o, double def = 0)
        {
            switch (o)
            {
                case double d: return d;
                case StepTyped t: return AsNumber(t.Value, def);
                case string s: return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : def;
                default: return def;
            }
        }
    }
}
