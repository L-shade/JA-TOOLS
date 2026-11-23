using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using RevitApp = Autodesk.Revit.ApplicationServices.Application;

namespace JAEsquadrias.Engine
{
    /// <summary>One plan line of a nested group: x along the wall (mm), pos across it (mm from the wall centre).</summary>
    internal sealed class Traco2D
    {
        public double XA, PA, XB, PB, XM, PM;
        public bool Arco, Fino, Tracejado;
        /// <summary>Name of the host Yes/No parameter that shows it ("Planta - folhas"...).</summary>
        public string Grupo;
    }

    /// <summary>
    /// Plan lines that move together across the wall (all on the same anchor): they become a
    /// detail-item family nested in the window/door family, placed on its anchor plane by a locked
    /// dimension. A group with <see cref="Faixas"/> is the opening in the wall: lines across the
    /// wall plus a masking region, one set per band of wall thickness, switched by "Espessura".
    /// </summary>
    internal sealed class GrupoPlanta2D
    {
        public string Nome;
        public AncorasParede.Ancora Ancora;
        /// <summary>Position (mm, nominal) of the nested family's origin across the wall.</summary>
        public double Origem;
        public readonly List<Traco2D> Tracos = new List<Traco2D>();
        public bool Faixas;
        public double Largura, Espessura;
        public Family Familia;
    }

    internal static class PlantaAninhada
    {
        private const double Mm = 1.0 / 304.8;
        public const string PEspessura = "Espessura";
        public const string PAbertura = "Abertura";
        /// <summary>Wall thickness bands of the opening (mm): every 5 mm from 50 to 450.</summary>
        public const int FaixaMin = 50, FaixaMax = 450, Passo = 5;

        /// <summary>
        /// Builds the group's detail-item family in its own document, saves it in the temp folder
        /// and loads it into <paramref name="destino"/> (which must not be in a transaction).
        /// </summary>
        public static Family Construir(RevitApp app, string template, GrupoPlanta2D g, XYZ u, XYZ n, Document destino)
        {
            Document doc = app.NewFamilyDocument(template);
            if (doc == null) return null;
            try
            {
                using (var t = new Transaction(doc, "JA Famílias – planta"))
                {
                    t.Start();
                    var opts = t.GetFailureHandlingOptions();
                    opts.SetFailuresPreprocessor(new WarningSwallower());
                    t.SetFailureHandlingOptions(opts);
                    View view = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                        .FirstOrDefault(v => !v.IsTemplate && (v.ViewType == ViewType.FloorPlan || v.ViewType == ViewType.DraftingView || v.ViewType == ViewType.Detail || v.ViewType == ViewType.EngineeringPlan));
                    if (view == null) throw new InvalidOperationException("o modelo de item de detalhe não tem vista");
                    var b = new Desenho(doc, view, u, n, g.Origem);
                    if (g.Faixas) b.Faixas(g.Largura, g.Espessura);
                    else b.Linhas(g.Tracos);
                    t.Commit();
                }
                string dir = Path.Combine(Path.GetTempPath(), "JA Familias");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, g.Nome + ".rfa");
                doc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true, MaximumBackups = 1 });
                return doc.LoadFamily(destino, new OverwriteLoadOptions());
            }
            finally
            {
                try { doc.Close(false); } catch { }
            }
        }

        private sealed class Desenho
        {
            private readonly Document _doc;
            private readonly View _view;
            private readonly FamilyManager _fm;
            private readonly XYZ _u, _n;
            private readonly double _origem;
            private readonly Dictionary<string, FamilyParameter> _vis = new Dictionary<string, FamilyParameter>();

            public Desenho(Document doc, View view, XYZ u, XYZ n, double origem)
            {
                _doc = doc;
                _view = view;
                _fm = doc.FamilyManager;
                _u = u;
                _n = n;
                _origem = origem;
                if (_fm.CurrentType == null) _fm.NewType("Padrão");
            }

            /// <summary>Local point (z = 0) of x along the wall and pos across it (mm).</summary>
            private XYZ L(double x, double pos)
            {
                XYZ o = _u * (x * Mm) + _n * ((pos - _origem) * Mm);
                return new XYZ(o.X, o.Y, 0);
            }

            private FamilyParameter YesNo(string name, bool value)
            {
                if (_vis.TryGetValue(name, out FamilyParameter p)) return p;
                p = _fm.get_Parameter(name) ?? _fm.AddParameter(name, GroupTypeId.Graphics, SpecTypeId.Boolean.YesNo, true);
                _fm.Set(p, value ? 1 : 0);
                _vis[name] = p;
                return p;
            }

            private void Bind(Element e, FamilyParameter p)
            {
                if (e == null || p == null) return;
                try
                {
                    Parameter vis = e.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM);
                    if (vis != null) _fm.AssociateElementParameterToFamilyParameter(vis, p);
                }
                catch (Exception ex) { Logger.Warn("[JA Famílias] Visibilidade (planta aninhada): " + ex.Message); }
            }

            public void Linhas(IEnumerable<Traco2D> tracos)
            {
                GraphicsStyle outline = Style("JA Planta", false, 2);
                GraphicsStyle thin = Style("JA Planta (linha fina)", false, 1) ?? outline;
                GraphicsStyle dash = Style("JA Planta (tracejada)", true, 1) ?? thin;
                foreach (var t in tracos)
                {
                    try
                    {
                        XYZ a = L(t.XA, t.PA), b = L(t.XB, t.PB);
                        if (a.DistanceTo(b) < 1.5 * Mm) continue;
                        Curve c = t.Arco ? (Curve)Arc.Create(a, b, L(t.XM, t.PM)) : Line.CreateBound(a, b);
                        DetailCurve dc = _doc.FamilyCreate.NewDetailCurve(_view, c);
                        GraphicsStyle gs = t.Tracejado ? dash : t.Fino ? thin : outline;
                        if (gs != null) dc.LineStyle = gs;
                        if (!string.IsNullOrEmpty(t.Grupo)) Bind(dc, YesNo(t.Grupo, true));
                    }
                    catch (Exception ex) { Logger.Warn("[JA Famílias] Linha de planta (aninhada): " + ex.Message); }
                }
            }

            /// <summary>
            /// The opening: for each band of wall thickness, two lines across the wall (jambs) and a
            /// masking region over the wall between them; only the band of the real wall shows.
            /// Origin = exterior face of the wall (raw), depth towards the interior.
            /// </summary>
            public void Faixas(double largura, double espessura)
            {
                GraphicsStyle outline = Style("JA Planta", false, 2);
                FamilyParameter pe = _fm.get_Parameter(PEspessura) ?? _fm.AddParameter(PEspessura, GroupTypeId.Geometry, SpecTypeId.Length, true);
                _fm.Set(pe, espessura * Mm);
                FamilyParameter pa = YesNo(PAbertura, true);
                var mi = typeof(FilledRegion).GetMethod("CreateMaskingRegion", new[] { typeof(Document), typeof(ElementId), typeof(IList<CurveLoop>) });
                GraphicsStyle inv = Category.GetCategory(_doc, BuiltInCategory.OST_InvisibleLines)?.GetGraphicsStyle(GraphicsStyleType.Projection);
                for (int k = FaixaMin; k <= FaixaMax; k += Passo)
                {
                    try
                    {
                        string name = "Faixa " + k.ToString(CultureInfo.InvariantCulture);
                        FamilyParameter p = _fm.AddParameter(name, GroupTypeId.Graphics, SpecTypeId.Boolean.YesNo, true);
                        string lo = (k * 10 - Passo * 5).ToString(CultureInfo.InvariantCulture), hi = (k * 10 + Passo * 5).ToString(CultureInfo.InvariantCulture);
                        _fm.SetFormula(p, $"and({PAbertura}, and(not({PEspessura} < ({lo} mm / 10)), {PEspessura} < ({hi} mm / 10)))");

                        double e = _origem, i = _origem - k;    // faces of a wall of k mm
                        foreach (double x in new[] { 0.0, largura })
                        {
                            DetailCurve dc = _doc.FamilyCreate.NewDetailCurve(_view, Line.CreateBound(L(x, e), L(x, i)));
                            if (outline != null) dc.LineStyle = outline;
                            Bind(dc, p);
                        }
                        if (mi != null)
                        {
                            var pts = new[] { L(0, e + 1), L(largura, e + 1), L(largura, i - 1), L(0, i - 1) };
                            var loop = new CurveLoop();
                            for (int j = 0; j < 4; j++) loop.Append(Line.CreateBound(pts[j], pts[(j + 1) % 4]));
                            var region = mi.Invoke(null, new object[] { _doc, _view.Id, new List<CurveLoop> { loop } }) as Element;
                            if (region != null)
                            {
                                try
                                {
                                    var set = region.GetType().GetMethod("SetLineStyleId", new[] { typeof(ElementId) });
                                    if (inv != null && set != null) set.Invoke(region, new object[] { inv.Id });
                                }
                                catch { }
                                Bind(region, p);
                            }
                        }
                    }
                    catch (Exception ex) { Logger.Warn("[JA Famílias] Faixa da abertura " + k + ": " + (ex.InnerException?.Message ?? ex.Message)); }
                }
            }

            private GraphicsStyle Style(string name, bool dashed, int weight)
            {
                try
                {
                    Category parent = _doc.OwnerFamily.FamilyCategory;
                    if (parent == null) return null;
                    Category sub = null;
                    foreach (Category c in parent.SubCategories) if (c.Name == name) sub = c;
                    if (sub == null) sub = _doc.Settings.Categories.NewSubcategory(parent, name);
                    sub.SetLineWeight(weight, GraphicsStyleType.Projection);
                    if (dashed)
                    {
                        ElementId pat = DashPattern();
                        if (pat != ElementId.InvalidElementId) sub.SetLinePatternId(pat, GraphicsStyleType.Projection);
                    }
                    return sub.GetGraphicsStyle(GraphicsStyleType.Projection);
                }
                catch (Exception ex)
                {
                    Logger.Warn("[JA Famílias] Subcategoria (planta aninhada) " + name + ": " + ex.Message);
                    return null;
                }
            }

            private ElementId DashPattern()
            {
                var all = new FilteredElementCollector(_doc).OfClass(typeof(LinePatternElement)).Cast<LinePatternElement>().ToList();
                var found = all.FirstOrDefault(p => p.Name == "JA Tracejado")
                            ?? all.FirstOrDefault(p => { string nm = p.Name.ToLowerInvariant(); return nm.StartsWith("dash") || nm.StartsWith("tracej") || nm.StartsWith("hidden") || nm.StartsWith("oculta"); });
                if (found != null) return found.Id;
                var lp = new LinePattern("JA Tracejado");
                lp.SetSegments(new List<LinePatternSegment>
                {
                    new LinePatternSegment(LinePatternSegmentType.Dash, 2.5 * Mm),
                    new LinePatternSegment(LinePatternSegmentType.Space, 1.5 * Mm)
                });
                return LinePatternElement.Create(_doc, lp).Id;
            }
        }
    }
}
