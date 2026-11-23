using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using RevitDwgUnifier.Tools.Common;

namespace RevitDwgUnifier.Tools.Parameters
{
    /// <summary>
    /// Formulas in the style of Archicad's property expressions and of Excel in Portuguese: fields between braces
    /// ({Comprimento}, {Ambiente:Nome}), numbers (2,5 or 2.5), texts in quotes, + − * / ^, &amp; (joins texts),
    /// = &lt;&gt; &lt; &gt; &lt;= &gt;=, E / OU / NÃO, and functions with arguments separated by ";" (SE, SES, ESCOLHER, ARRED,
    /// TETO, CONCATENAR, ESQUERDA, SUBSTITUIR, DIVIDIR, CONTÉM, TEXTO…; English names too). Compiled once, evaluated
    /// per element. Values are numbers (double), texts, true/false or empty (null).
    /// </summary>
    internal static class Formula
    {
        public sealed class Erro : Exception { public Erro(string m) : base(m) { } }

        public sealed class Expr
        {
            internal Node Raiz;
            public string Texto;
            /// <summary>The value for one element; campo gives the value of a field by name.</summary>
            public object Avaliar(Func<string, object> campo) => Raiz.Eval(campo);
        }

        private static readonly CultureInfo Br = CultureInfo.GetCultureInfo("pt-BR");

        // ------------------------------------------------------------------ values

        public static string ParaTexto(object v)
        {
            switch (v)
            {
                case null: return "";
                case string s: return s;
                case bool b: return b ? "Sim" : "Não";
                case double d: return double.IsNaN(d) || double.IsInfinity(d) ? "" : d.ToString("0.####", Br);
                default: return Convert.ToString(v, Br);
            }
        }

        public static double? ParaNumero(object v)
        {
            switch (v)
            {
                case null: return null;
                case double d: return double.IsNaN(d) ? (double?)null : d;
                case bool b: return b ? 1 : 0;
                case int i: return i;
                case string s: return EhNumero(s) ? ValoresElemento.Numero(s) : null;
                default: return null;
            }
        }

        public static bool ParaBool(object v)
        {
            switch (v)
            {
                case null: return false;
                case bool b: return b;
                case double d: return Math.Abs(d) > 1e-12;
                case string s:
                    string f = TextUtil.Fold(s).Trim();
                    if (f.Length == 0 || f == "nao" || f == "falso" || f == "false" || f == "no" || f == "0") return false;
                    return true;
                default: return true;
            }
        }

        private static readonly System.Text.RegularExpressions.Regex NumRx = new System.Text.RegularExpressions.Regex(@"^\s*-?\d+(?:[.,]\d+)*\s*[a-zA-Z²³%°]*\s*$");
        private static bool EhNumero(string s) => s != null && NumRx.IsMatch(s);

        private static bool Vazio(object v) => v == null || (v is string s && s.Length == 0);

        /// <summary>-1, 0, 1: as numbers when both read as numbers, else as texts (accents and case ignored).</summary>
        private static int Comparar(object a, object b)
        {
            double? x = ParaNumero(a), y = ParaNumero(b);
            if (x != null && y != null && !(a is string sa && !EhNumero(sa)) && !(b is string sb && !EhNumero(sb)))
                return Math.Abs(x.Value - y.Value) < 1e-9 ? 0 : x.Value < y.Value ? -1 : 1;
            if (a is bool || b is bool) return ParaBool(a) == ParaBool(b) ? 0 : ParaBool(a) ? 1 : -1;
            return string.CompareOrdinal(TextUtil.Fold(ParaTexto(a)).Trim(), TextUtil.Fold(ParaTexto(b)).Trim()) switch { 0 => 0, var c when c < 0 => -1, _ => 1 };
        }

        // ------------------------------------------------------------------ tokens

        private enum T { Num, Str, Campo, Id, Op, AbreP, FechaP, Sep, Fim }
        private struct Tk { public T Tipo; public string Texto; public double Num; public int Pos; }

        private static List<Tk> Tokens(string s)
        {
            var r = new List<Tk>();
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                int ini = i;
                if (char.IsDigit(c) || (c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1])))
                {
                    while (i < s.Length && char.IsDigit(s[i])) i++;
                    if (i + 1 < s.Length && (s[i] == '.' || s[i] == ',') && char.IsDigit(s[i + 1]))
                    {
                        i++;
                        while (i < s.Length && char.IsDigit(s[i])) i++;
                    }
                    string t = s.Substring(ini, i - ini).Replace(',', '.');
                    r.Add(new Tk { Tipo = T.Num, Num = double.Parse(t, CultureInfo.InvariantCulture), Texto = t, Pos = ini });
                    continue;
                }
                if (c == '"' || c == '“' || c == '”')
                {
                    var sb = new StringBuilder();
                    i++;
                    while (true)
                    {
                        if (i >= s.Length) throw new Erro("texto sem as aspas de fechamento.");
                        char d = s[i];
                        if (d == '"' || d == '”' || d == '“')
                        {
                            if (d == '"' && i + 1 < s.Length && s[i + 1] == '"') { sb.Append('"'); i += 2; continue; }
                            i++; break;
                        }
                        sb.Append(d); i++;
                    }
                    r.Add(new Tk { Tipo = T.Str, Texto = sb.ToString(), Pos = ini });
                    continue;
                }
                if (c == '{')
                {
                    int f = s.IndexOf('}', i + 1);
                    if (f < 0) throw new Erro("campo sem a chave de fechamento }.");
                    r.Add(new Tk { Tipo = T.Campo, Texto = s.Substring(i + 1, f - i - 1).Trim(), Pos = ini });
                    i = f + 1;
                    continue;
                }
                if (char.IsLetter(c) || c == '_')
                {
                    while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_' || s[i] == '.')) i++;
                    r.Add(new Tk { Tipo = T.Id, Texto = s.Substring(ini, i - ini), Pos = ini });
                    continue;
                }
                if (c == '(') { r.Add(new Tk { Tipo = T.AbreP, Texto = "(", Pos = i++ }); continue; }
                if (c == ')') { r.Add(new Tk { Tipo = T.FechaP, Texto = ")", Pos = i++ }); continue; }
                if (c == ';' || c == ',') { r.Add(new Tk { Tipo = T.Sep, Texto = c.ToString(), Pos = i++ }); continue; }
                string dois = i + 1 < s.Length ? s.Substring(i, 2) : "";
                if (dois == "<=" || dois == ">=" || dois == "<>" || dois == "!=" || dois == "==" || dois == "&&" || dois == "||")
                { r.Add(new Tk { Tipo = T.Op, Texto = dois, Pos = i }); i += 2; continue; }
                if ("+-*/^&=<>!".IndexOf(c) >= 0) { r.Add(new Tk { Tipo = T.Op, Texto = c.ToString(), Pos = i++ }); continue; }
                if (c == '≤' || c == '≥' || c == '≠') { r.Add(new Tk { Tipo = T.Op, Texto = c == '≤' ? "<=" : c == '≥' ? ">=" : "<>", Pos = i++ }); continue; }
                throw new Erro($"caractere “{c}” não esperado (posição {i + 1}).");
            }
            r.Add(new Tk { Tipo = T.Fim, Pos = s.Length });
            return r;
        }

        // ------------------------------------------------------------------ parser

        public static Expr Compilar(string texto)
        {
            if (string.IsNullOrWhiteSpace(texto)) throw new Erro("a fórmula está vazia.");
            string s = texto.Trim();
            if (s.StartsWith("=")) s = s.Substring(1);
            var p = new Parser { Lista = Tokens(s) };
            Node raiz = p.Ou();
            if (p.Atual.Tipo != T.Fim) throw new Erro($"não entendi a partir de “{s.Substring(Math.Min(s.Length, p.Atual.Pos))}”.");
            return new Expr { Raiz = raiz, Texto = texto };
        }

        private static string F(string id) => TextUtil.Fold(id).Replace('_', '.').Trim();
        private static bool Palavra(Tk t, params string[] ws) => t.Tipo == T.Id && ws.Contains(F(t.Texto));

        private sealed class Parser
        {
            public List<Tk> Lista;
            private int _i;
            public Tk Atual => Lista[_i];
            private Tk Prox => Lista[Math.Min(_i + 1, Lista.Count - 1)];
            private Tk Comer() => Lista[_i++];
            private bool EhOp(params string[] ops) => Atual.Tipo == T.Op && ops.Contains(Atual.Texto);

            public Node Ou()
            {
                Node a = E();
                while (EhOp("||") || (Palavra(Atual, "ou", "or") && Prox.Tipo != T.AbreP)) { Comer(); a = new Bin("ou", a, E()); }
                return a;
            }
            private Node E()
            {
                Node a = Cmp();
                while (EhOp("&&") || (Palavra(Atual, "e", "and") && Prox.Tipo != T.AbreP)) { Comer(); a = new Bin("e", a, Cmp()); }
                return a;
            }
            private Node Cmp()
            {
                Node a = Concat();
                if (EhOp("=", "==", "<>", "!=", "<", ">", "<=", ">="))
                {
                    string op = Comer().Texto;
                    if (op == "==") op = "="; if (op == "!=") op = "<>";
                    a = new Bin(op, a, Concat());
                }
                return a;
            }
            private Node Concat()
            {
                Node a = Soma();
                while (EhOp("&")) { Comer(); a = new Bin("&", a, Soma()); }
                return a;
            }
            private Node Soma()
            {
                Node a = Mult();
                while (EhOp("+", "-")) { string op = Comer().Texto; a = new Bin(op, a, Mult()); }
                return a;
            }
            private Node Mult()
            {
                Node a = Unario();
                while (EhOp("*", "/")) { string op = Comer().Texto; a = new Bin(op, a, Unario()); }
                return a;
            }
            private Node Unario()
            {
                if (EhOp("-")) { Comer(); return new Un("-", Unario()); }
                if (EhOp("+")) { Comer(); return Unario(); }
                if (EhOp("!") || Palavra(Atual, "nao", "not")) { Comer(); return new Un("nao", Unario()); }
                return Pot();
            }
            private Node Pot()
            {
                Node a = Primario();
                if (EhOp("^")) { Comer(); a = new Bin("^", a, Unario()); }
                return a;
            }
            private Node Primario()
            {
                Tk t = Comer();
                switch (t.Tipo)
                {
                    case T.Num: return new Const(t.Num);
                    case T.Str: return new Const(t.Texto);
                    case T.Campo:
                        if (string.IsNullOrWhiteSpace(t.Texto)) throw new Erro("campo vazio {}.");
                        return new Campo(t.Texto);
                    case T.AbreP:
                        Node dentro = Ou();
                        if (Comer().Tipo != T.FechaP) throw new Erro("falta fechar um parêntese.");
                        return dentro;
                    case T.Id:
                        string nome = F(t.Texto);
                        if (Atual.Tipo != T.AbreP)
                        {
                            if (nome == "verdadeiro" || nome == "true" || nome == "sim") return new Const(true);
                            if (nome == "falso" || nome == "false") return new Const(false);
                            if (nome == "pi") return new Const(Math.PI);
                            throw new Erro($"“{t.Texto}” não é uma função nem um valor (campos vão entre chaves: {{{t.Texto}}}).");
                        }
                        Comer();
                        var args = new List<Node>();
                        if (Atual.Tipo != T.FechaP)
                        {
                            args.Add(Ou());
                            while (Atual.Tipo == T.Sep) { Comer(); args.Add(Ou()); }
                        }
                        if (Comer().Tipo != T.FechaP) throw new Erro($"falta fechar o parêntese de {t.Texto}.");
                        return Chamada.Criar(t.Texto, nome, args);
                }
                throw new Erro(t.Tipo == T.Fim ? "a fórmula termina no meio." : $"“{t.Texto}” fora do lugar.");
            }
        }

        // ------------------------------------------------------------------ nodes

        internal abstract class Node { public abstract object Eval(Func<string, object> c); }

        private sealed class Const : Node { private readonly object _v; public Const(object v) { _v = v; } public override object Eval(Func<string, object> c) => _v; }

        private sealed class Campo : Node
        {
            private readonly string _n;
            public Campo(string n) { _n = n; }
            public override object Eval(Func<string, object> c) => c(_n);
        }

        private sealed class Un : Node
        {
            private readonly string _op; private readonly Node _a;
            public Un(string op, Node a) { _op = op; _a = a; }
            public override object Eval(Func<string, object> c)
            {
                object v = _a.Eval(c);
                if (_op == "nao") return !ParaBool(v);
                double? x = ParaNumero(v);
                return x == null ? null : (object)(-x.Value);
            }
        }

        private sealed class Bin : Node
        {
            private readonly string _op; private readonly Node _a, _b;
            public Bin(string op, Node a, Node b) { _op = op; _a = a; _b = b; }
            public override object Eval(Func<string, object> c)
            {
                if (_op == "e") return ParaBool(_a.Eval(c)) && ParaBool(_b.Eval(c));
                if (_op == "ou") return ParaBool(_a.Eval(c)) || ParaBool(_b.Eval(c));
                object a = _a.Eval(c), b = _b.Eval(c);
                switch (_op)
                {
                    case "&": return ParaTexto(a) + ParaTexto(b);
                    case "=": return Comparar(a, b) == 0;
                    case "<>": return Comparar(a, b) != 0;
                    case "<": return !Vazio(a) && !Vazio(b) && Comparar(a, b) < 0;
                    case ">": return !Vazio(a) && !Vazio(b) && Comparar(a, b) > 0;
                    case "<=": return !Vazio(a) && !Vazio(b) && Comparar(a, b) <= 0;
                    case ">=": return !Vazio(a) && !Vazio(b) && Comparar(a, b) >= 0;
                }
                double? x = ParaNumero(a), y = ParaNumero(b);
                if (x == null || y == null) return null;
                switch (_op)
                {
                    case "+": return x.Value + y.Value;
                    case "-": return x.Value - y.Value;
                    case "*": return x.Value * y.Value;
                    case "/": return Math.Abs(y.Value) < 1e-15 ? null : (object)(x.Value / y.Value);
                    case "^": return Math.Pow(x.Value, y.Value);
                }
                return null;
            }
        }

        /// <summary>The functions, by name without accents (Portuguese as in Excel, and English).</summary>
        private static readonly Dictionary<string, string> Nomes = new Dictionary<string, string>
        {
            ["se"] = "SE", ["if"] = "SE", ["ses"] = "SES", ["ifs"] = "SES", ["escolher"] = "ESCOLHER", ["switch"] = "ESCOLHER", ["e"] = "E", ["and"] = "E",
            ["ou"] = "OU", ["or"] = "OU", ["nao"] = "NAO", ["not"] = "NAO",
            ["abs"] = "ABS", ["arred"] = "ARRED", ["arredondar"] = "ARRED", ["round"] = "ARRED",
            ["arredondar.para.cima"] = "ACIMA", ["roundup"] = "ACIMA", ["arredondar.para.baixo"] = "ABAIXO", ["rounddown"] = "ABAIXO",
            ["teto"] = "TETO", ["ceiling"] = "TETO", ["piso"] = "PISO", ["floor"] = "PISO", ["int"] = "INT", ["inteiro"] = "INT",
            ["raiz"] = "RAIZ", ["sqrt"] = "RAIZ", ["pot"] = "POT", ["potencia"] = "POT", ["power"] = "POT", ["mod"] = "MOD", ["resto"] = "MOD",
            ["min"] = "MIN", ["max"] = "MAX", ["soma"] = "SOMA", ["sum"] = "SOMA", ["media"] = "MEDIA", ["average"] = "MEDIA",
            ["concatenar"] = "CONCAT", ["concat"] = "CONCAT", ["esquerda"] = "ESQUERDA", ["left"] = "ESQUERDA", ["direita"] = "DIREITA", ["right"] = "DIREITA",
            ["ext.texto"] = "MID", ["mid"] = "MID", ["num.caract"] = "LEN", ["len"] = "LEN", ["maiuscula"] = "UPPER", ["upper"] = "UPPER",
            ["minuscula"] = "LOWER", ["lower"] = "LOWER", ["pri.maiuscula"] = "PROPER", ["proper"] = "PROPER", ["arrumar"] = "TRIM", ["trim"] = "TRIM",
            ["substituir"] = "SUBST", ["substitute"] = "SUBST", ["replace"] = "SUBST", ["dividir"] = "SPLIT", ["split"] = "SPLIT",
            ["contem"] = "CONTEM", ["contains"] = "CONTEM", ["comeca"] = "COMECA", ["comeca.com"] = "COMECA", ["startswith"] = "COMECA",
            ["termina"] = "TERMINA", ["termina.com"] = "TERMINA", ["endswith"] = "TERMINA", ["localizar"] = "FIND", ["find"] = "FIND",
            ["texto"] = "TEXTO", ["text"] = "TEXTO", ["str"] = "TEXTO", ["valor"] = "VALOR", ["value"] = "VALOR", ["num"] = "VALOR",
            ["vazio"] = "VAZIO", ["e.vazio"] = "VAZIO", ["isempty"] = "VAZIO", ["isblank"] = "VAZIO", ["seerro"] = "SEERRO", ["iferror"] = "SEERRO",
        };

        private static readonly Dictionary<string, (int min, int max)> Aridade = new Dictionary<string, (int, int)>
        {
            ["SE"] = (2, 3), ["SES"] = (2, 99), ["ESCOLHER"] = (3, 99), ["E"] = (1, 99), ["OU"] = (1, 99), ["NAO"] = (1, 1),
            ["ABS"] = (1, 1), ["ARRED"] = (1, 2), ["ACIMA"] = (1, 2), ["ABAIXO"] = (1, 2), ["TETO"] = (1, 2), ["PISO"] = (1, 2), ["INT"] = (1, 1),
            ["RAIZ"] = (1, 1), ["POT"] = (2, 2), ["MOD"] = (2, 2), ["MIN"] = (1, 99), ["MAX"] = (1, 99), ["SOMA"] = (1, 99), ["MEDIA"] = (1, 99),
            ["CONCAT"] = (1, 99), ["ESQUERDA"] = (1, 2), ["DIREITA"] = (1, 2), ["MID"] = (3, 3), ["LEN"] = (1, 1), ["UPPER"] = (1, 1),
            ["LOWER"] = (1, 1), ["PROPER"] = (1, 1), ["TRIM"] = (1, 1), ["SUBST"] = (3, 3), ["SPLIT"] = (3, 3), ["CONTEM"] = (2, 2),
            ["COMECA"] = (2, 2), ["TERMINA"] = (2, 2), ["FIND"] = (2, 2), ["TEXTO"] = (1, 2), ["VALOR"] = (1, 1), ["VAZIO"] = (1, 1), ["SEERRO"] = (2, 2),
        };

        /// <summary>The functions for the help text.</summary>
        public const string Ajuda =
            "Lógica: SE(cond; sim; não), SES(c1; v1; c2; v2…), ESCOLHER(valor; caso1; res1; …; padrão), E(…), OU(…), NÃO(…), VAZIO(x), SEERRO(x; alternativa). " +
            "Números: ABS, ARRED(x; casas), ARREDONDAR.PARA.CIMA, ARREDONDAR.PARA.BAIXO, TETO(x; múltiplo), PISO(x; múltiplo), INT, RAIZ, POT, MOD, MÍN, MÁX, SOMA, MÉDIA, PI. " +
            "Textos: & junta, CONCATENAR, ESQUERDA(t; n), DIREITA(t; n), EXT.TEXTO(t; início; n), NÚM.CARACT, MAIÚSCULA, MINÚSCULA, PRI.MAIÚSCULA, ARRUMAR, " +
            "SUBSTITUIR(t; de; para), DIVIDIR(t; separador; parte), CONTÉM(t; trecho), COMEÇA(t; trecho), TERMINA(t; trecho), LOCALIZAR(trecho; t), TEXTO(x; casas), VALOR(t). " +
            "Comparações: = <> < > <= >=; separe os argumentos com ponto e vírgula. Comprimentos em m, áreas em m², volumes em m³, ângulos em graus.";

        private sealed class Chamada : Node
        {
            private readonly string _f; private readonly List<Node> _a;
            private Chamada(string f, List<Node> a) { _f = f; _a = a; }

            public static Node Criar(string original, string nome, List<Node> args)
            {
                if (!Nomes.TryGetValue(nome, out string f)) throw new Erro($"não conheço a função {original}.");
                var (min, max) = Aridade[f];
                if (args.Count < min || args.Count > max)
                    throw new Erro($"{original} pede {(min == max ? min.ToString() : max >= 99 ? $"{min} ou mais" : $"{min} a {max}")} argumento(s); tem {args.Count}.");
                if (f == "ESCOLHER" && args.Count < 3) throw new Erro("ESCOLHER pede o valor e ao menos um caso e um resultado.");
                return new Chamada(f, args);
            }

            private object A(int i, Func<string, object> c) => i < _a.Count ? _a[i].Eval(c) : null;
            private double? N(int i, Func<string, object> c) => ParaNumero(A(i, c));
            private string S(int i, Func<string, object> c) => ParaTexto(A(i, c));

            private static double Arred(double x, int casas, int modo)
            {
                double m = Math.Pow(10, casas);
                double v = x * m;
                v = modo == 0 ? Math.Round(v + (v >= 0 ? 1e-9 : -1e-9), MidpointRounding.AwayFromZero) : modo > 0 ? (v >= 0 ? Math.Ceiling(v - 1e-9) : Math.Floor(v + 1e-9)) : (v >= 0 ? Math.Floor(v + 1e-9) : Math.Ceiling(v - 1e-9));
                return v / m;
            }

            public override object Eval(Func<string, object> c)
            {
                switch (_f)
                {
                    case "SE": return ParaBool(A(0, c)) ? A(1, c) : (_a.Count > 2 ? A(2, c) : false);
                    case "SES":
                        for (int i = 0; i + 1 < _a.Count; i += 2) if (ParaBool(A(i, c))) return A(i + 1, c);
                        return _a.Count % 2 == 1 ? A(_a.Count - 1, c) : null;
                    case "ESCOLHER":
                        {
                            object v = A(0, c);
                            int i = 1;
                            for (; i + 1 < _a.Count; i += 2) if (Comparar(v, A(i, c)) == 0) return A(i + 1, c);
                            return i < _a.Count ? A(i, c) : null;
                        }
                    case "E": return _a.All(x => ParaBool(x.Eval(c)));
                    case "OU": return _a.Any(x => ParaBool(x.Eval(c)));
                    case "NAO": return !ParaBool(A(0, c));
                    case "VAZIO": return Vazio(A(0, c));
                    case "SEERRO": { object v = A(0, c); return Vazio(v) ? A(1, c) : v; }
                    case "ABS": { var x = N(0, c); return x == null ? null : (object)Math.Abs(x.Value); }
                    case "ARRED": case "ACIMA": case "ABAIXO":
                        {
                            var x = N(0, c); if (x == null) return null;
                            int casas = (int)Math.Round(N(1, c) ?? 0);
                            return Arred(x.Value, casas, _f == "ARRED" ? 0 : _f == "ACIMA" ? 1 : -1);
                        }
                    case "TETO": case "PISO":
                        {
                            var x = N(0, c); if (x == null) return null;
                            double m = Math.Abs(N(1, c) ?? 1); if (m < 1e-12) return 0.0;
                            double q = x.Value / m;
                            return (_f == "TETO" ? Math.Ceiling(q - 1e-9) : Math.Floor(q + 1e-9)) * m;
                        }
                    case "INT": { var x = N(0, c); return x == null ? null : (object)Math.Floor(x.Value + 1e-9); }
                    case "RAIZ": { var x = N(0, c); return x == null || x.Value < 0 ? null : (object)Math.Sqrt(x.Value); }
                    case "POT": { var x = N(0, c); var y = N(1, c); return x == null || y == null ? null : (object)Math.Pow(x.Value, y.Value); }
                    case "MOD": { var x = N(0, c); var y = N(1, c); return x == null || y == null || Math.Abs(y.Value) < 1e-15 ? null : (object)(x.Value - y.Value * Math.Floor(x.Value / y.Value)); }
                    case "MIN": case "MAX": case "SOMA": case "MEDIA":
                        {
                            var ns = _a.Select(x => ParaNumero(x.Eval(c))).Where(x => x != null).Select(x => x.Value).ToList();
                            if (ns.Count == 0) return _f == "SOMA" ? 0.0 : (object)null;
                            return _f == "MIN" ? ns.Min() : _f == "MAX" ? ns.Max() : _f == "SOMA" ? ns.Sum() : ns.Average();
                        }
                    case "CONCAT": return string.Concat(_a.Select(x => ParaTexto(x.Eval(c))));
                    case "ESQUERDA": case "DIREITA":
                        {
                            string s = S(0, c); int n = Math.Max(0, (int)Math.Round(N(1, c) ?? 1));
                            n = Math.Min(n, s.Length);
                            return _f == "ESQUERDA" ? s.Substring(0, n) : s.Substring(s.Length - n);
                        }
                    case "MID":
                        {
                            string s = S(0, c); int ini = Math.Max(1, (int)Math.Round(N(1, c) ?? 1)) - 1, n = Math.Max(0, (int)Math.Round(N(2, c) ?? 0));
                            if (ini >= s.Length) return "";
                            return s.Substring(ini, Math.Min(n, s.Length - ini));
                        }
                    case "LEN": return (double)S(0, c).Length;
                    case "UPPER": return S(0, c).ToUpper(Br);
                    case "LOWER": return S(0, c).ToLower(Br);
                    case "PROPER": return Br.TextInfo.ToTitleCase(S(0, c).ToLower(Br));
                    case "TRIM": return string.Join(" ", S(0, c).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
                    case "SUBST":
                        {
                            string s = S(0, c), de = S(1, c);
                            if (de.Length == 0) return s;
                            return System.Text.RegularExpressions.Regex.Replace(s, System.Text.RegularExpressions.Regex.Escape(de), S(2, c).Replace("$", "$$"), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        }
                    case "SPLIT":
                        {
                            string s = S(0, c), sep = S(1, c); int n = (int)Math.Round(N(2, c) ?? 1);
                            string[] partes = sep.Length == 0 ? new[] { s } : s.Split(new[] { sep }, StringSplitOptions.None);
                            if (n < 0) n = partes.Length + n + 1;
                            return n >= 1 && n <= partes.Length ? partes[n - 1].Trim() : "";
                        }
                    case "CONTEM": return TextUtil.Fold(S(0, c)).Contains(TextUtil.Fold(S(1, c)));
                    case "COMECA": return TextUtil.Fold(S(0, c)).StartsWith(TextUtil.Fold(S(1, c)), StringComparison.Ordinal);
                    case "TERMINA": return TextUtil.Fold(S(0, c)).EndsWith(TextUtil.Fold(S(1, c)), StringComparison.Ordinal);
                    case "FIND":
                        {
                            int i = TextUtil.Fold(S(1, c)).IndexOf(TextUtil.Fold(S(0, c)), StringComparison.Ordinal);
                            return (double)(i + 1);
                        }
                    case "TEXTO":
                        {
                            object v = A(0, c);
                            if (_a.Count < 2) return ParaTexto(v);
                            double? x = ParaNumero(v); if (x == null) return ParaTexto(v);
                            int casas = Math.Max(0, Math.Min(10, (int)Math.Round(N(1, c) ?? 0)));
                            return x.Value.ToString("F" + casas, Br);
                        }
                    case "VALOR": { object v = A(0, c); return v is double ? v : ValoresElemento.Numero(ParaTexto(v)); }
                }
                return null;
            }
        }
    }
}
