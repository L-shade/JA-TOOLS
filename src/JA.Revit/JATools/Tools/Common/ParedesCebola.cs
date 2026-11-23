using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitDwgUnifier.Tools.Common
{
    /// <summary>
    /// Paredes cebola, shared by Ajustar Paredes Cebola and every dimension tool: what a finish wall
    /// (revestimento modelled as its own wall) is and which wall it belongs to. Same settings as the
    /// Ajustar Paredes Cebola window (thickness, keywords, distance), so both tools agree.
    /// </summary>
    public static class ParedesCebola
    {
        private const double Mm = 1 / 304.8;
        public const string PalavrasPadrao = "revest, reboco, acabamento, cerâmica, ceramica, porcelanato, pastilha, cebola";

        public static double EspessuraMax => Num("AjustarCebola.esp", 60) * Mm;
        public static double Tolerancia => Num("AjustarCebola.tol", 30) * Mm;
        public static string[] Palavras =>
            (ToolPrefs.Get("AjustarCebola.palavras") ?? PalavrasPadrao).Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0).ToArray();

        private static double Num(string k, double d) =>
            double.TryParse(ToolPrefs.Get(k), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 0 ? v : d;

        /// <summary>From the location line to the wall's centre, along Orientation (exterior side positive).</summary>
        public static double DesvioDoCentro(Wall w)
        {
            double W = w.Width;
            int chave = 0;
            try { chave = w.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM)?.AsInteger() ?? 0; } catch { }
            double p;   // the location line measured from the exterior face inwards
            switch ((WallLocationLine)chave)
            {
                case WallLocationLine.FinishFaceExterior: p = 0; break;
                case WallLocationLine.FinishFaceInterior: p = W; break;
                case WallLocationLine.CoreCenterline:
                case WallLocationLine.CoreExterior:
                case WallLocationLine.CoreInterior:
                {
                    double ini = 0, fim = W;
                    try
                    {
                        CompoundStructure cs = w.WallType.GetCompoundStructure();
                        if (cs != null && cs.GetFirstCoreLayerIndex() >= 0)
                        {
                            int f = cs.GetFirstCoreLayerIndex(), l = cs.GetLastCoreLayerIndex();
                            double acc = 0;
                            for (int i = 0; i < cs.LayerCount; i++)
                            {
                                if (i == f) ini = acc;
                                acc += cs.GetLayerWidth(i);
                                if (i == l) fim = acc;
                            }
                        }
                    }
                    catch { }
                    p = (WallLocationLine)chave == WallLocationLine.CoreExterior ? ini : (WallLocationLine)chave == WallLocationLine.CoreInterior ? fim : (ini + fim) / 2;
                    break;
                }
                default: p = W / 2; break;
            }
            return p - W / 2;
        }

        /// <summary>
        /// The width of the finish on each side of a compound wall (exterior, interior): the layers outside
        /// the core and, inside the core boundaries, the outer layers that are finish or membrane anyway.
        /// What is left is the wall's structure ("osso").
        /// </summary>
        public static void Acabamentos(WallType t, out double ext, out double inn)
        {
            ext = inn = 0;
            try
            {
                CompoundStructure cs = t?.GetCompoundStructure();
                IList<CompoundStructureLayer> layers = cs?.GetLayers();
                if (layers == null || layers.Count < 2) return;
                bool Acab(CompoundStructureLayer l) => l.Function == MaterialFunctionAssignment.Finish1 || l.Function == MaterialFunctionAssignment.Finish2 ||
                                                     l.Function == MaterialFunctionAssignment.Membrane;
                int first = cs.GetFirstCoreLayerIndex(), last = cs.GetLastCoreLayerIndex();
                if (first < 0 || last < first || last >= layers.Count) { first = 0; last = layers.Count - 1; }
                int a = first, b = last;
                while (a < b && Acab(layers[a])) a++;
                while (b > a && Acab(layers[b])) b--;
                if (layers.All(Acab)) return;                    // all finish: the wall itself is a finish wall
                for (int i = 0; i < a; i++) ext += layers[i].Width;
                for (int i = b + 1; i < layers.Count; i++) inn += layers[i].Width;
            }
            catch { ext = inn = 0; }
        }

        private static bool Reta(Wall w, out Line ln)
        {
            ln = (w?.Location as LocationCurve)?.Curve as Line;
            try { return ln != null && w.WallType != null && w.WallType.Kind == WallKind.Basic; } catch { return false; }
        }

        private static XYZ Plano(XYZ v) => new XYZ(v.X, v.Y, 0);

        /// <summary>Centre line ends (plan), direction, Orientation (plan) and length of a straight wall.</summary>
        public static bool Eixo(Wall w, out XYZ a, out XYZ b, out XYZ d, out XYZ n, out double L)
        {
            a = b = d = n = null; L = 0;
            if (!Reta(w, out Line ln)) return false;
            XYZ p0 = Plano(ln.GetEndPoint(0)), p1 = Plano(ln.GetEndPoint(1));
            L = p0.DistanceTo(p1);
            n = Plano(w.Orientation);
            if (L < 1e-6 || n.GetLength() < 1e-9) return false;
            n = n.Normalize();
            XYZ desl = n * DesvioDoCentro(w);
            a = p0 + desl; b = p1 + desl; d = (p1 - p0) / L;
            return true;
        }

        /// <summary>
        /// The wall a finish wall sits on: straight, strictly thicker, parallel (~1°), overlapping along (5 cm) and in
        /// height (3 cm), faces within the tolerance (away or inside). The nearest one; null = not a finish of any wall.
        /// </summary>
        public static Wall ParedeDoRevestimento(Wall r)
        {
            if (!Eixo(r, out XYZ ra, out XYZ rb, out XYZ rd, out _, out double rl)) return null;
            BoundingBoxXYZ bb = r.get_BoundingBox(null);
            if (bb == null) return null;
            double tol = Tolerancia, alcance = tol + 0.5 / 0.3048;   // walls up to ~50 cm thick + tolerance
            var caixa = new Outline(new XYZ(bb.Min.X - alcance, bb.Min.Y - alcance, bb.Min.Z), new XYZ(bb.Max.X + alcance, bb.Max.Y + alcance, bb.Max.Z));
            XYZ meio = (ra + rb) / 2;
            Wall melhor = null;
            double best = double.MaxValue;
            foreach (Wall b in new FilteredElementCollector(r.Document).OfClass(typeof(Wall)).WherePasses(new BoundingBoxIntersectsFilter(caixa)).Cast<Wall>())
            {
                if (b.Id == r.Id || b.Width <= r.Width + 1e-6) continue;
                if (!Eixo(b, out XYZ ba, out XYZ bbp, out XYZ bd, out XYZ bn, out double bl)) continue;
                if (Math.Abs(bd.X * rd.Y - bd.Y * rd.X) > 0.0175) continue;
                BoundingBoxXYZ bx = b.get_BoundingBox(null);
                if (bx == null || Math.Min(bx.Max.Z, bb.Max.Z) - Math.Max(bx.Min.Z, bb.Min.Z) < 30 * Mm) continue;
                double t0 = (ra - ba).DotProduct(bd), t1 = (rb - ba).DotProduct(bd);
                if (Math.Min(Math.Max(t0, t1), bl) - Math.Max(Math.Min(t0, t1), 0) < 50 * Mm) continue;
                double sd = (meio - ba).DotProduct(bn);
                double folga = Math.Abs(Math.Abs(sd) - (b.Width + r.Width) / 2);
                if (folga > tol || folga >= best) continue;
                best = folga; melhor = b;
            }
            return melhor;
        }

        /// <summary>
        /// Is this a finish wall by its place: thin (or a keyword in the type name) and against a thicker wall.
        /// A thin wall standing alone (a partition) is a wall.
        /// </summary>
        public static bool EhRevestimento(Wall w)
        {
            if (!Reta(w, out _)) return false;
            string nome = "";
            try { nome = (w.WallType.Name ?? "").ToLowerInvariant(); } catch { }
            bool fina = w.Width <= EspessuraMax + 1e-9 || (Palavras.Any(nome.Contains) && w.Width <= Math.Max(EspessuraMax, 100 * Mm) + 1e-9);
            return fina && ParedeDoRevestimento(w) != null;
        }

        /// <summary>The structure behind a finish wall (through layers on layers: tile → plaster → wall).</summary>
        public static Wall Estrutura(Wall w, Func<Wall, bool> ehRevestimento)
        {
            Wall atual = w;
            for (int i = 0; i < 6 && atual != null && ehRevestimento(atual); i++) atual = ParedeDoRevestimento(atual);
            return atual != null && !ehRevestimento(atual) ? atual : null;
        }
    }
}
