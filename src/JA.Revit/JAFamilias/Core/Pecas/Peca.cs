using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace JAEsquadrias.Core
{
    /// <summary>3D vector / point in millimetres, in the family's local coordinates.</summary>
    public struct V3
    {
        public double X, Y, Z;
        public V3(double x, double y, double z) { X = x; Y = y; Z = z; }
        public static readonly V3 Ex = new V3(1, 0, 0), Ey = new V3(0, 1, 0), Ez = new V3(0, 0, 1), Zero = new V3(0, 0, 0);
        public static V3 operator +(V3 a, V3 b) => new V3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static V3 operator -(V3 a, V3 b) => new V3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static V3 operator *(V3 a, double k) => new V3(a.X * k, a.Y * k, a.Z * k);
        public static V3 operator -(V3 a) => new V3(-a.X, -a.Y, -a.Z);
        public double Dot(V3 b) => X * b.X + Y * b.Y + Z * b.Z;
        public V3 Cross(V3 b) => new V3(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
        public V3 Unit() { double l = Length; return l < 1e-12 ? this : this * (1 / l); }
    }

    /// <summary>A material the family needs: its family parameter and the material (by name).</summary>
    public sealed class MaterialDef
    {
        public string Key;          // slot used by the pieces
        public string Parametro;    // family parameter, e.g. "Material da pedra"
        public string Nome;         // material name (a project material with this name wins)
        public string Hex;
        public int Transparencia;
    }

    /// <summary>
    /// One solid (or void) of a JA family: a flat profile (outline + holes, in the plane O/U/V)
    /// extruded along N = U × V by Depth. Everything in millimetres, local coordinates.
    /// </summary>
    public sealed class Peca
    {
        public string Nome;
        public V3 O, U = V3.Ex, V = V3.Ey;
        public List<List<P2>> Loops = new List<List<P2>>();
        public double Depth;
        public bool Void;
        public string Slot = "pedra";
        public bool Coarse = true, Medium = true, Fine = true;
        /// <summary>Hidden in plan views (shown through symbolic lines instead).</summary>
        public bool OcultarEmPlanta;
        /// <summary>Line-based families: the extrusion end follows the line length.</summary>
        public bool AcompanhaComprimento;
        /// <summary>
        /// Revolution instead of extrusion (chapim on curved walls): the profile (plane O/U/V, with U
        /// radial and V up) turns about the vertical axis through the local origin, from 0 to
        /// <see cref="AnguloGraus"/> counter-clockwise; the angle follows the instance parameter "Ângulo do arco".
        /// </summary>
        public bool Revolucao;
        public double AnguloGraus;
        /// <summary>
        /// Blend instead of extrusion (mitred chamfers): Loops[0] is the base profile (plane O/U/V) and
        /// this the top profile, same plane coordinates, Depth further along N; same vertex count and order.
        /// </summary>
        public List<P2> Topo;
        /// <summary>Built instead when the blend fails in Revit.</summary>
        public Peca Alternativa;
        /// <summary>Pipe stub (ralos): built as a true cylinder, with a pipe connector on its far end.</summary>
        public ConectorPeca Conector;

        public V3 N => U.Cross(V);

        public V3 P(P2 p) => O + U * p.X + V * p.Y;

        /// <summary>Axis-aligned box.</summary>
        public static Peca Box(string nome, double x0, double y0, double z0, double x1, double y1, double z1, string slot)
        {
            var p = new Peca { Nome = nome, O = new V3(Math.Min(x0, x1), Math.Min(y0, y1), Math.Min(z0, z1)), Depth = Math.Abs(z1 - z0), Slot = slot };
            p.Loops.Add(Rect(0, 0, Math.Abs(x1 - x0), Math.Abs(y1 - y0)));
            return p;
        }

        /// <summary>Horizontal slab: outline (and holes) in plan, from z0 to z1.</summary>
        public static Peca Laje(string nome, List<P2> outline, IEnumerable<List<P2>> holes, double z0, double z1, string slot)
        {
            var p = new Peca { Nome = nome, O = new V3(0, 0, Math.Min(z0, z1)), Depth = Math.Abs(z1 - z0), Slot = slot };
            p.Loops.Add(outline);
            if (holes != null) p.Loops.AddRange(holes.Where(h => h != null && h.Count >= 3));
            return p;
        }

        public Peca Lod(bool coarse, bool medium, bool fine) { Coarse = coarse; Medium = medium; Fine = fine; return this; }

        public static List<P2> Rect(double x0, double y0, double x1, double y1) =>
            new List<P2> { new P2(x0, y0), new P2(x1, y0), new P2(x1, y1), new P2(x0, y1) };

        /// <summary>Rectangle with rounded corners (arcs as short segments).</summary>
        public static List<P2> RoundRect(double x0, double y0, double x1, double y1, double r, int seg = 6)
        {
            double w = Math.Abs(x1 - x0), h = Math.Abs(y1 - y0);
            r = Math.Max(0, Math.Min(r, Math.Min(w, h) / 2 - 0.5));
            if (r < 2) return Rect(Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1));
            double ax = Math.Min(x0, x1), ay = Math.Min(y0, y1), bx = Math.Max(x0, x1), by = Math.Max(y0, y1);
            var pts = new List<P2>();
            void Corner(double cx, double cy, double a0)
            {
                for (int i = 0; i <= seg; i++)
                {
                    double a = a0 + Math.PI / 2 * i / seg;
                    pts.Add(new P2(cx + r * Math.Cos(a), cy + r * Math.Sin(a)));
                }
            }
            Corner(bx - r, ay + r, -Math.PI / 2);
            Corner(bx - r, by - r, 0);
            Corner(ax + r, by - r, Math.PI / 2);
            Corner(ax + r, ay + r, Math.PI);
            return pts;
        }

        public static List<P2> Circle(double cx, double cy, double r, int seg = 16)
        {
            var pts = new List<P2>();
            for (int i = 0; i < seg; i++)
            {
                double a = 2 * Math.PI * i / seg;
                pts.Add(new P2(cx + r * Math.Cos(a), cy + r * Math.Sin(a)));
            }
            return pts;
        }

        public static double Area(List<P2> loop)
        {
            double a = 0;
            for (int i = 0; i < loop.Count; i++)
            {
                var p = loop[i];
                var q = loop[(i + 1) % loop.Count];
                a += p.X * q.Y - q.X * p.Y;
            }
            return a / 2;
        }
    }

    /// <summary>Pipe connector at the far end of a cylindrical piece (outlet or inlet of a drain).</summary>
    public sealed class ConectorPeca
    {
        public double DiametroMm;
        /// <summary>True = outlet (flow out of the drain); false = inlet.</summary>
        public bool Saida = true;
    }

    /// <summary>Symbolic line of the plan (local x/y, mm). Dashed for things above/below the cut.</summary>
    public sealed class LinhaPeca
    {
        public P2 A, B;
        public bool Tracejada;
        public LinhaPeca(P2 a, P2 b, bool dashed) { A = a; B = b; Tracejada = dashed; }
    }

    /// <summary>Everything a JA piece family is made of, plus the numbers for schedules.</summary>
    public sealed class PecaModelo
    {
        public readonly List<Peca> Pecas = new List<Peca>();
        public readonly List<LinhaPeca> Planta = new List<LinhaPeca>();
        public readonly List<string> Avisos = new List<string>();
        /// <summary>Stone area (m²): top, edges, splashbacks... for quotations.</summary>
        public double AreaPedra;
        /// <summary>Bancadas: top area (m²) of the wet areas (0 = none).</summary>
        public double AreaMolhada;
        /// <summary>Short numbers for the configurator footer ("Pedra 1,32 m²", ...).</summary>
        public readonly List<string> Resumo = new List<string>();
    }

    /// <summary>Kind of JA piece family.</summary>
    public enum TipoPeca { Bancada = 0, Nicho = 1, Chapim = 2, Ralo = 3, Pedra = 4, Vidro = 5, Madeira = 6 }

    /// <summary>Common contract of the piece configurations (bancada, nicho, chapim).</summary>
    public interface IPecaConfig
    {
        TipoPeca Tipo { get; }
        string Codigo { get; set; }
        string Nome { get; set; }
        string ModeloId { get; set; }
        PecaModelo Build();
        IList<MaterialDef> Materiais();
        string Descricao();
        string Dimensoes();
        string MaterialPrincipal();
        string ToJson();
        IPecaConfig Copiar();
    }

    public static class PecaUtil
    {
        public static readonly CultureInfo Pt = new CultureInfo("pt-BR");
        public static string Mm(double v) => v.ToString("0", CultureInfo.InvariantCulture);
        public static string M2(double v) => v.ToString("0.00", Pt) + " m²";
        public static string M(double v) => v.ToString("0.00", Pt) + " m";

        public static string NomeTipo(TipoPeca t) => t == TipoPeca.Bancada ? "Bancada" : t == TipoPeca.Nicho ? "Nicho" : t == TipoPeca.Ralo ? "Ralo" : t == TipoPeca.Pedra ? "Pedra" : t == TipoPeca.Vidro ? "Vidro" : t == TipoPeca.Madeira ? "Madeira" : "Chapim";
        public static string Plural(TipoPeca t) => t == TipoPeca.Bancada ? "Bancadas" : t == TipoPeca.Nicho ? "Nichos" : t == TipoPeca.Ralo ? "Ralos" : t == TipoPeca.Pedra ? "Soleiras, peitoris, rodapés e degraus" : t == TipoPeca.Vidro ? "Guarda-corpos e boxes" : t == TipoPeca.Madeira ? "Painéis ripados e pergolados" : "Chapins";
        public static string Prefixo(TipoPeca t) => t == TipoPeca.Bancada ? "BC" : t == TipoPeca.Nicho ? "NI" : t == TipoPeca.Ralo ? "RL" : t == TipoPeca.Pedra ? "SL" : t == TipoPeca.Vidro ? "GC" : t == TipoPeca.Madeira ? "RP" : "CH";

        /// <summary>Pieces whose length follows the line drawn in Revit (line-based families, JA_Comprimento).</summary>
        public static bool Linear(IPecaConfig c) => c is ChapimConfig ch ? !ch.Curvo : c is PedraConfig p ? p.Linear : c is VidroConfig v && v.Linear;
        public static string FamilyName(IPecaConfig c) => "JA " + NomeTipo(c.Tipo) + " " + c.Codigo;

        /// <summary>Bancadas (also Bancada no Local), nichos, chapins and stones are always Casework (Componentes de gabinete).</summary>
        public static bool Gabinete(TipoPeca t) => t == TipoPeca.Bancada || t == TipoPeca.Nicho || t == TipoPeca.Chapim || t == TipoPeca.Pedra;

        /// <summary>The glass plate of a guarda-corpo made as a Revit railing: a baluster panel (Balaústres).</summary>
        public static bool Balaustre(IPecaConfig c) => c is VidroConfig v && v.Balaustre;

        /// <summary>Ralos are Pipe Fittings (Conexões de tubo) with pipe connectors.</summary>
        public static bool Tubo(TipoPeca t) => t == TipoPeca.Ralo;

        /// <summary>Revit category name as shown in the Project Browser.</summary>
        public static string Categoria(TipoPeca t) => Tubo(t) ? "Conexões de tubo" : Gabinete(t) ? "Componentes de gabinete" : "Modelos genéricos";

        public static IPecaConfig FromJson(TipoPeca tipo, string json)
        {
            try
            {
                switch (tipo)
                {
                    case TipoPeca.Bancada: return Infrastructure.Json.Deserialize<BancadaConfig>(json)?.Normalized();
                    case TipoPeca.Nicho: return Infrastructure.Json.Deserialize<NichoConfig>(json)?.Normalized();
                    case TipoPeca.Ralo: return Infrastructure.Json.Deserialize<RaloConfig>(json)?.Normalized();
                    case TipoPeca.Pedra: return Infrastructure.Json.Deserialize<PedraConfig>(json)?.Normalized();
                    case TipoPeca.Vidro: return Infrastructure.Json.Deserialize<VidroConfig>(json)?.Normalized();
                    case TipoPeca.Madeira: return Infrastructure.Json.Deserialize<MadeiraConfig>(json)?.Normalized();
                    default: return Infrastructure.Json.Deserialize<ChapimConfig>(json)?.Normalized();
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("[JA Famílias] Configuração ilegível: " + ex.Message);
                return null;
            }
        }

        public static bool TryParseTipo(string s, out TipoPeca t)
        {
            foreach (TipoPeca v in Enum.GetValues(typeof(TipoPeca)))
                if (string.Equals(NomeTipo(v), s, StringComparison.OrdinalIgnoreCase)) { t = v; return true; }
            t = TipoPeca.Bancada;
            return false;
        }

        public static double Clamp(double v, double min, double max) => double.IsNaN(v) ? min : Math.Max(min, Math.Min(max, v));
    }
}
