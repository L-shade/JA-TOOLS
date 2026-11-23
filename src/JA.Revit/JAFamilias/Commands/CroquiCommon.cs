using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace JAEsquadrias.Commands
{
    /// <summary>How a tool that draws a family gets its croqui (the same choices in every one of them).</summary>
    public enum ModoCroqui { Ferramentas = 0, Linhas = 1, Bordas = 2, Clicar = 3 }

    public abstract partial class EsquadriaCommandBase
    {
        /// <summary>The croqui of a tool: curves (from the Revit tools, picked lines or edges) or "click the points".</summary>
        protected sealed class EntradaCroqui
        {
            public ModoCroqui Modo = ModoCroqui.Clicar;
            /// <summary>Index of an extra mode of the tool (0, 1… after the four common ones), -1 = none.</summary>
            public int Extra = -1;
            public List<Curve> Curvas;
            public string Origem = "";
            /// <summary>Lines drawn with the Revit tools, to delete once the family is placed.</summary>
            public readonly List<ElementId> Apagar = new List<ElementId>();
            /// <summary>The Revit tool was opened: the command ends now and reads the drawing when run again.</summary>
            public bool Iniciado;
        }

        /// <summary>The croqui of this run (its drawn lines are deleted when the family is placed).</summary>
        protected EntradaCroqui CroquiAtual;

        private sealed class DesenhoPendente { public string Doc; public long Vista; public long UltimaId; public bool Apagar; }
        private static readonly Dictionary<string, DesenhoPendente> _pendentes = new Dictionary<string, DesenhoPendente>();
        private static string ChaveDoc(Document d) => (d.PathName ?? "") + "|" + d.Title;

        // ------------------------------------------------------------------ orto
        /// <summary>Orto for the clicks of this run (from the croqui window); Shift while clicking inverts it.</summary>
        protected bool OrtoLigado;
        /// <summary>Angle of the orto axes from the view's horizontal (degrees).</summary>
        protected double OrtoAngulo;

        private static bool ShiftApertado()
        {
            try { return (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0; }
            catch { return false; }
        }

        /// <summary>
        /// The clicked point p made orthogonal to the previous one: along the view's horizontal or
        /// vertical (turned by OrtoAngulo), whichever is closer. Works in plans and in elevations.
        /// </summary>
        protected XYZ AplicarOrto(XYZ anterior, XYZ p)
        {
            if (anterior == null || p == null) return p;
            if (!(OrtoLigado ^ ShiftApertado())) return p;
            View v = Doc.ActiveView;
            XYZ r = v.RightDirection, u = v.UpDirection, n = v.ViewDirection;
            double a = OrtoAngulo * Math.PI / 180;
            XYZ r2 = r * Math.Cos(a) + u * Math.Sin(a), u2 = u * Math.Cos(a) - r * Math.Sin(a);
            XYZ d = p - anterior;
            double x = d.DotProduct(r2), y = d.DotProduct(u2), z = d.DotProduct(n);
            return anterior + (Math.Abs(x) >= Math.Abs(y) ? r2 * x : u2 * y) + n * z;
        }

        /// <summary>Loads the orto of a tool from its croqui window's last values (for clicks that skip the window).</summary>
        protected void OrtoDasPreferencias(string ferramenta)
        {
            OrtoLigado = ToolPrefs.Get("Croqui." + ferramenta + ".orto") == "1";
            double.TryParse(ToolPrefs.Get("Croqui." + ferramenta + ".angulo"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out OrtoAngulo);
        }

        // ------------------------------------------------------------------ the common window
        /// <summary>
        /// The croqui of a tool, the same way in all of them: lines selected before the command;
        /// back from drawing with the Revit tools (the detail lines drawn since); else a small window —
        /// Revit's Detail Line tools (line, rectangle, circle, polygon, arc, pick lines…), pick lines,
        /// pick edges of elements, or click (with orto) — plus the tool's own extra modes.
        /// Null = cancelled.
        /// </summary>
        protected EntradaCroqui EntradaDoCroqui(string ferramenta, View vista, string comoDesenhar, string[] extras = null, bool aceitaPreSelecao = true)
        {
            var e = new EntradaCroqui();
            // 1. lines selected before the command
            if (aceitaPreSelecao)
            {
                var sel = UiDoc.Selection.GetElementIds().Select(id => Doc.GetElement(id)).OfType<CurveElement>().Select(c => c.GeometryCurve).Where(c => c != null).ToList();
                if (sel.Count > 0)
                {
                    _pendentes.Remove(ferramenta);
                    e.Modo = ModoCroqui.Linhas; e.Curvas = sel; e.Origem = sel.Count + " linha(s) selecionada(s)";
                    return e;
                }
            }
            // 2. back from drawing with the Revit tools in this view
            if (_pendentes.TryGetValue(ferramenta, out DesenhoPendente pd) && pd.Doc == ChaveDoc(Doc) && pd.Vista == vista.Id.Value)
            {
                _pendentes.Remove(ferramenta);
                var novas = new FilteredElementCollector(Doc, vista.Id).OfClass(typeof(CurveElement)).Cast<CurveElement>()
                    .Where(c => c is DetailCurve && c.Id.Value > pd.UltimaId && c.GeometryCurve != null).ToList();
                if (novas.Count > 0)
                {
                    e.Modo = ModoCroqui.Ferramentas; e.Curvas = novas.Select(c => c.GeometryCurve).ToList(); e.Origem = novas.Count + " linha(s) desenhada(s)";
                    if (pd.Apagar) e.Apagar.AddRange(novas.Select(c => c.Id));
                    return e;
                }
            }
            // 3. the window
            var modos = new List<string>
            {
                "Com as ferramentas do Revit (linha, retângulo, círculo, polígono, arco, selecionar linhas…)",
                "Selecionar linhas já desenhadas (de detalhe ou de modelo)",
                "Selecionar bordas de elementos (piso, parede, forro, bancada…)",
                "Clicar os pontos"
            };
            if (extras != null) modos.AddRange(extras);
            var dlg = new OptionsDialog(Title + " – como desenhar", "Croqui." + ferramenta, "Continuar");
            var fModo = dlg.Choice("modo", "Como desenhar", modos, 0);
            var fApagar = dlg.Check("apagar", "Apagar as linhas desenhadas depois de criar a família", true);
            fApagar.EnabledWhen(fModo, 0);
            var fOrto = dlg.Check("orto", "Orto: cada clique na horizontal ou na vertical do anterior", false, "Segure Shift ao clicar para inverter o orto só naquele clique.");
            var fAng = dlg.Number("angulo", "Ângulo do orto (°, a partir da horizontal da vista)", 0, -90, 90, "Para paredes inclinadas: digite o ângulo delas.");
            fAng.EnabledWhen(fOrto);
            int[] comClique = new[] { 3 }.Concat(Enumerable.Range(4, extras?.Length ?? 0)).ToArray();
            fOrto.EnabledWhen(fModo, comClique);
            if (!string.IsNullOrWhiteSpace(comoDesenhar)) dlg.Note(comoDesenhar);
            if (!dlg.Show(Owner)) return null;
            OrtoLigado = fOrto.Value;
            OrtoAngulo = fAng.Value;
            e.Modo = fModo.Index <= 3 ? (ModoCroqui)fModo.Index : ModoCroqui.Clicar;
            e.Extra = fModo.Index > 3 ? fModo.Index - 4 : -1;
            switch (fModo.Index)
            {
                case 0:
                    IniciarDesenho(ferramenta, vista, fApagar.Value, comoDesenhar);
                    e.Iniciado = true;
                    return e;
                case 1:
                    e.Curvas = SelecionarLinhas();
                    if (e.Curvas == null || e.Curvas.Count == 0) return null;
                    e.Origem = e.Curvas.Count + " linha(s) selecionada(s)";
                    return e;
                case 2:
                    e.Curvas = SelecionarBordas();
                    if (e.Curvas == null || e.Curvas.Count == 0) return null;
                    e.Origem = e.Curvas.Count + " borda(s) selecionada(s)";
                    return e;
                default:
                    e.Origem = "clicado";
                    return e;
            }
        }

        /// <summary>Opens Revit's Detail Line tool; the next run of the tool reads what was drawn in this view.</summary>
        private void IniciarDesenho(string ferramenta, View vista, bool apagar, string comoDesenhar)
        {
            long ultima = new FilteredElementCollector(Doc).WhereElementIsNotElementType().ToElementIds().Select(id => id.Value).DefaultIfEmpty(0).Max();
            _pendentes[ferramenta] = new DesenhoPendente { Doc = ChaveDoc(Doc), Vista = vista.Id.Value, UltimaId = ultima, Apagar = apagar };
            Show("Desenhe com as ferramentas de Linha de detalhe do Revit (a ferramenta abre ao fechar esta janela; também em Anotar › Linha de detalhe): " +
                 "linha, retângulo, círculo, polígono, arco, selecionar linhas… O orto do Revit (Shift ao desenhar) e os snaps funcionam.\n\n" +
                 (string.IsNullOrWhiteSpace(comoDesenhar) ? "" : comoDesenhar + "\n\n") +
                 "Termine com Esc e clique de novo em " + Title + " (sem selecionar nada): a família é criada com o que você desenhou nesta vista.",
                 ResultKind.Info);
            try { UiApp.PostCommand(RevitCommandId.LookupPostableCommandId(PostableCommand.DetailLine)); }
            catch { }
        }

        private sealed class FiltroLinhas : ISelectionFilter
        {
            public bool AllowElement(Element e) => e is CurveElement c && c.GeometryCurve != null;
            public bool AllowReference(Reference r, XYZ p) => false;
        }

        /// <summary>Lines already in the view (detail or model): click, Ctrl+click, window or Tab for the chain; Concluir ends.</summary>
        protected List<Curve> SelecionarLinhas()
        {
            try
            {
                return UiDoc.Selection.PickObjects(ObjectType.Element, new FiltroLinhas(),
                        Title + ": selecione as linhas (clique, Ctrl+clique ou janela; Tab seleciona a cadeia) e clique em Concluir")
                    .Select(r => (Doc.GetElement(r) as CurveElement)?.GeometryCurve).Where(c => c != null).ToList();
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return null; }
        }

        /// <summary>
        /// Edges of model elements (floor, wall, ceiling, worktop, stair…), in project coordinates
        /// (family geometry not modified by the instance is in the family's own frame).
        /// </summary>
        protected List<Curve> SelecionarBordas()
        {
            IList<Reference> refs;
            try
            {
                refs = UiDoc.Selection.PickObjects(ObjectType.Edge, Title + ": clique as bordas (arestas) de pisos, paredes, forros ou bancadas e clique em Concluir");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return null; }
            var res = new List<Curve>();
            foreach (Reference r in refs)
            {
                try
                {
                    Element el = Doc.GetElement(r);
                    if (!(el?.GetGeometryObjectFromReference(r) is Edge ed)) continue;
                    Curve c = ed.AsCurve();
                    if (el is FamilyInstance fi && !fi.HasModifiedGeometry()) c = c.CreateTransformed(fi.GetTotalTransform());
                    res.Add(c);
                }
                catch { }
            }
            if (res.Count < refs.Count) Show((refs.Count - res.Count) + " borda(s) não puderam ser lidas e ficaram de fora.", ResultKind.Warning);
            return res;
        }

        // ------------------------------------------------------------------ croqui geometry
        /// <summary>Straight segments joined end to end into one closed polygon (plan, Z = 0); null = not closed / branched.</summary>
        protected static List<XYZ> FecharPoligono(IEnumerable<Curve> curvas, double tol)
        {
            var segs = curvas.Select(c => (a: new XYZ(c.GetEndPoint(0).X, c.GetEndPoint(0).Y, 0), b: new XYZ(c.GetEndPoint(1).X, c.GetEndPoint(1).Y, 0))).ToList();
            if (segs.Count < 3) return null;
            var resto = segs.Skip(1).ToList();
            var pts = new List<XYZ> { segs[0].a, segs[0].b };
            while (resto.Count > 0)
            {
                XYZ fim = pts[pts.Count - 1];
                int k = resto.FindIndex(s => s.a.DistanceTo(fim) < tol || s.b.DistanceTo(fim) < tol);
                if (k < 0) return null;
                var s0 = resto[k];
                resto.RemoveAt(k);
                pts.Add(s0.a.DistanceTo(fim) < tol ? s0.b : s0.a);
            }
            if (pts[0].DistanceTo(pts[pts.Count - 1]) > tol) return null;
            pts.RemoveAt(pts.Count - 1);
            return pts;
        }

        protected static List<XYZ> SemColineares(List<XYZ> p)
        {
            var r = new List<XYZ>();
            for (int i = 0; i < p.Count; i++)
            {
                XYZ a = p[(i - 1 + p.Count) % p.Count], b = p[i], c = p[(i + 1) % p.Count];
                XYZ u = b - a, v = c - b;
                if (u.GetLength() < 1e-9 || v.GetLength() < 1e-9) continue;
                if (Math.Abs(u.Normalize().CrossProduct(v.Normalize()).Z) > 1e-4) r.Add(b);
            }
            return r;
        }

        /// <summary>Apaga, dentro da transação aberta, as linhas desenhadas para o croqui.</summary>
        protected void ApagarCroqui(EntradaCroqui e)
        {
            if (e == null || e.Apagar.Count == 0) return;
            try { Doc.Delete(e.Apagar.Where(id => Doc.GetElement(id) != null).ToList()); } catch { }
        }
    }
}
