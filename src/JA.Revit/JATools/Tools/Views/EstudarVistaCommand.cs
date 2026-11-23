using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using RevitDwgUnifier.Infrastructure;
using RevitDwgUnifier.Tools.Common;

namespace RevitDwgUnifier.Tools.Views
{
    /// <summary>
    /// Estudar Vista: writes everything that makes up the active view to a JSON file plus a PNG of
    /// the view, so a view drawn by hand can be studied and reproduced by the tools (Detalhar
    /// Ambientes): view settings and parameters, template, view range, crop, filters and hidden
    /// categories, the sheet it is on, every element drawn only in this view (tags, dimensions,
    /// spot elevations, texts, detail items, symbols, lines, regions) with family, type, text,
    /// instance parameters and position in the view frame (mm, from the crop's bottom-left), the
    /// types used, rooms and counts of model elements seen. Changes nothing in the model.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class EstudarVistaCommand : ToolCommand
    {
        public override string Title => "Estudar Vista";

        private Transform _paraVista = Transform.Identity;   // model → view frame (crop box)
        private XYZ _origem = XYZ.Zero;

        protected override Result Run()
        {
            View v = Doc.ActiveView;
            if (v == null || v.IsTemplate) { Show("Abra a vista que quer estudar e rode de novo."); return Result.Cancelled; }

            string pasta = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "JA Tools", "Estudo de vistas");
            Directory.CreateDirectory(pasta);
            string nome = Seguro(v.Name) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmm");
            string json = Path.Combine(pasta, nome + ".json");

            try
            {
                BoundingBoxXYZ cb = v.CropBox;
                if (cb != null)
                {
                    _paraVista = cb.Transform.Inverse;
                    _origem = new XYZ(cb.Min.X, cb.Min.Y, 0);
                }
            }
            catch { }

            var j = new J();
            j.Abre();
            j.Par("ferramenta", "JA Tools - Estudar Vista");
            j.Par("arquivo", Doc.Title);
            j.Par("data", DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            Vista(j, v);
            Elementos(j, v);
            Ambientes(j, v);
            Modelo(j, v);
            j.Fecha();
            File.WriteAllText(json, j.ToString(), new UTF8Encoding(false));

            string png = null;
            try { png = Imagem(v, pasta, nome); } catch (Exception ex) { Logger.Info("[Estudar Vista] imagem: " + ex.Message); }

            ResultDialog.OpenPath(json);
            Show($"Vista “{v.Name}” estudada.\n\nArquivos em:\n{pasta}\n\n• {Path.GetFileName(json)}" +
                 (png != null ? $"\n• {Path.GetFileName(png)}" : "\n(a imagem não pôde ser exportada)") +
                 "\n\nEnvie os dois arquivos para o Claude.", ResultKind.Success);
            return Result.Succeeded;
        }

        // ================================================================== view

        private void Vista(J j, View v)
        {
            j.Obj("vista");
            j.Par("nome", v.Name);
            j.Par("id", v.Id.Value);
            j.Par("tipoVista", v.ViewType.ToString());
            j.Par("familiaTipo", (Doc.GetElement(v.GetTypeId()) as ElementType)?.Name);
            j.Par("escala", v.Scale);
            j.Par("nivelDetalhe", v.DetailLevel.ToString());
            j.Par("estiloVisual", Tenta(() => v.DisplayStyle.ToString()));
            j.Par("disciplina", Tenta(() => v.Discipline.ToString()));
            j.Par("modelo", (Doc.GetElement(v.ViewTemplateId) as View)?.Name);
            j.Par("nivel", v.GenLevel?.Name);
            j.Par("nivelCotaMm", v.GenLevel != null ? Mm(v.GenLevel.Elevation) : (double?)null);
            j.Par("direcaoVista", Vetor(v.ViewDirection));
            j.Par("direcaoCima", Vetor(v.UpDirection));
            j.Par("direcaoDireita", Vetor(v.RightDirection));
            try
            {
                BoundingBoxXYZ cb = v.CropBox;
                j.Obj("recorte");
                j.Par("ativo", v.CropBoxActive);
                j.Par("visivel", v.CropBoxVisible);
                j.Par("larguraMm", Mm(cb.Max.X - cb.Min.X));
                j.Par("alturaMm", Mm(cb.Max.Y - cb.Min.Y));
                j.Par("profundidadeMm", Mm(cb.Max.Z - cb.Min.Z));
                j.Par("origemModeloMm", Ponto(cb.Transform.OfPoint(cb.Min)));
                j.Par("centroModeloMm", Ponto(cb.Transform.OfPoint((cb.Min + cb.Max) / 2)));
                j.Par("eixoX", Vetor(cb.Transform.BasisX));
                j.Fim();
            }
            catch { }
            if (v is ViewPlan vp)
            {
                try
                {
                    PlanViewRange r = vp.GetViewRange();
                    j.Obj("faixaVista");
                    foreach (PlanViewPlane pl in new[] { PlanViewPlane.TopClipPlane, PlanViewPlane.CutPlane, PlanViewPlane.BottomClipPlane, PlanViewPlane.ViewDepthPlane })
                    {
                        j.Obj(pl.ToString());
                        ElementId lv = r.GetLevelId(pl);
                        j.Par("nivel", lv == PlanViewRange.Unlimited ? "Ilimitado" : lv == PlanViewRange.LevelAbove ? "Nível acima" : lv == PlanViewRange.LevelBelow ? "Nível abaixo" : lv == PlanViewRange.Current ? "Nível associado" : (Doc.GetElement(lv) as Level)?.Name);
                        j.Par("deslocamentoMm", Mm(r.GetOffset(pl)));
                        j.Fim();
                    }
                    j.Fim();
                }
                catch { }
            }
            // sheet
            try
            {
                Viewport vpo = new FilteredElementCollector(Doc).OfClass(typeof(Viewport)).Cast<Viewport>().FirstOrDefault(x => x.ViewId == v.Id);
                if (vpo != null)
                {
                    var sh = Doc.GetElement(vpo.SheetId) as ViewSheet;
                    j.Obj("prancha");
                    j.Par("numero", sh?.SheetNumber);
                    j.Par("nome", sh?.Name);
                    j.Par("tipoViewport", (Doc.GetElement(vpo.GetTypeId()) as ElementType)?.Name);
                    j.Par("centroMm", Ponto(vpo.GetBoxCenter()));
                    j.Fim();
                }
            }
            catch { }
            // filters
            try
            {
                j.Lista("filtros");
                foreach (ElementId fid in v.GetFilters())
                {
                    j.Item();
                    j.Par("nome", Doc.GetElement(fid)?.Name);
                    j.Par("visivel", v.GetFilterVisibility(fid));
                    OverrideGraphicSettings o = v.GetFilterOverrides(fid);
                    j.Par("sobreposicao", Sobreposicao(o));
                    j.Fim();
                }
                j.FimLista();
            }
            catch { }
            // categories hidden / overridden
            try
            {
                j.Lista("categoriasOcultas");
                foreach (Category c in Doc.Settings.Categories)
                {
                    try { if (v.CanCategoryBeHidden(c.Id) && v.GetCategoryHidden(c.Id)) j.Valor(c.Name); } catch { }
                    foreach (Category s in c.SubCategories)
                        try { if (v.CanCategoryBeHidden(s.Id) && v.GetCategoryHidden(s.Id)) j.Valor(c.Name + " / " + s.Name); } catch { }
                }
                j.FimLista();
                j.Lista("categoriasSobrepostas");
                foreach (Category c in Doc.Settings.Categories)
                {
                    try
                    {
                        string s = Sobreposicao(v.GetCategoryOverrides(c.Id));
                        if (!string.IsNullOrEmpty(s)) { j.Item(); j.Par("categoria", c.Name); j.Par("sobreposicao", s); j.Fim(); }
                    }
                    catch { }
                }
                j.FimLista();
            }
            catch { }
            Parametros(j, "parametros", v);
            j.Fim();
        }

        // ================================================================== elements of the view

        private void Elementos(J j, View v)
        {
            List<Element> proprios = new FilteredElementCollector(Doc, v.Id).WhereElementIsNotElementType()
                .Where(e => e.OwnerViewId == v.Id && e.Category != null).ToList();
            var tiposUsados = new Dictionary<ElementId, int>();

            j.Obj("resumoAnotacoes");
            foreach (var g in proprios.GroupBy(e => e.Category.Name + " | " + Familia(e) + " | " + TipoNome(e)).OrderByDescending(g => g.Count()))
                j.Par(g.Key, g.Count());
            j.Fim();

            j.Lista("anotacoes");
            foreach (Element e in proprios.Take(3000))
            {
                ElementId tid = e.GetTypeId();
                if (tid != ElementId.InvalidElementId) tiposUsados[tid] = (tiposUsados.TryGetValue(tid, out int n) ? n : 0) + 1;
                j.Item();
                j.Par("id", e.Id.Value);
                j.Par("categoria", e.Category.Name);
                j.Par("classe", e.GetType().Name);
                j.Par("familia", Familia(e));
                j.Par("tipo", TipoNome(e));
                try
                {
                    switch (e)
                    {
                        case IndependentTag t:
                            j.Par("texto", Tenta(() => t.TagText));
                            j.Par("cabecaMm", Local(t.TagHeadPosition));
                            j.Par("chamada", t.HasLeader);
                            j.Par("orientacao", t.TagOrientation.ToString());
                            try
                            {
                                foreach (ElementId tid2 in t.GetTaggedLocalElementIds())
                                {
                                    Element te = Doc.GetElement(tid2);
                                    if (te == null) continue;
                                    j.Par("etiquetado", (te.Category?.Name ?? "") + " | " + Familia(te) + " | " + TipoNome(te));
                                    break;
                                }
                            }
                            catch { }
                            break;
                        case SpatialElementTag st:
                            j.Par("cabecaMm", Local(st.TagHeadPosition));
                            j.Par("chamada", st.HasLeader);
                            if (st is RoomTag rt) j.Par("ambiente", rt.Room != null ? rt.Room.Number + " - " + RevitUtil.RoomName(rt.Room) : null);
                            break;
                        case SpotDimension sd:
                            j.Par("origemMm", Local(sd.Origin));
                            j.Par("valor", Tenta(() => sd.ValueString));
                            j.Par("referencia", Tenta(() => sd.References.Size > 0 ? Doc.GetElement(sd.References.get_Item(0))?.Category?.Name : null));
                            break;
                        case Dimension d:
                            j.Par("valor", Tenta(() => d.NumberOfSegments > 1 ? string.Join(" + ", d.Segments.Cast<DimensionSegment>().Select(s => s.ValueString)) : d.ValueString));
                            j.Par("segmentos", Tenta(() => d.NumberOfSegments));
                            if (d.Curve is Line dl && dl.IsBound) j.Par("linhaMm", new[] { Local(dl.GetEndPoint(0)), Local(dl.GetEndPoint(1)) });
                            else try { j.Par("origemMm", Local(d.Origin)); } catch { }
                            try
                            {
                                var refs = new List<string>();
                                foreach (Reference r in d.References)
                                {
                                    Element re = Doc.GetElement(r);
                                    refs.Add((re?.Category?.Name ?? re?.GetType().Name ?? "?") + " (" + r.ElementReferenceType + ")");
                                }
                                j.Par("referencias", refs);
                            }
                            catch { }
                            break;
                        case TextNote tn:
                            j.Par("texto", tn.Text?.TrimEnd());
                            j.Par("posicaoMm", Local(tn.Coord));
                            j.Par("larguraMm", Mm(tn.Width * v.Scale));
                            j.Par("alinhamento", tn.HorizontalAlignment.ToString());
                            break;
                        case FilledRegion fr:
                            try { j.Par("caixaMm", Caixa(fr.get_BoundingBox(v))); } catch { }
                            break;
                        case DetailCurve dc:
                            j.Par("estiloLinha", dc.LineStyle?.Name);
                            if (dc.GeometryCurve is Curve c && c.IsBound) j.Par("linhaMm", new[] { Local(c.GetEndPoint(0)), Local(c.GetEndPoint(1)) });
                            break;
                        case FamilyInstance fi:
                            if (fi.Location is LocationPoint lp) { j.Par("posicaoMm", Local(lp.Point)); j.Par("rotacaoGraus", Math.Round(lp.Rotation * 180 / Math.PI, 2)); }
                            else if (fi.Location is LocationCurve lc && lc.Curve.IsBound) j.Par("linhaMm", new[] { Local(lc.Curve.GetEndPoint(0)), Local(lc.Curve.GetEndPoint(1)) });
                            j.Par("espelhado", fi.Mirrored);
                            break;
                        default:
                            try { j.Par("caixaMm", Caixa(e.get_BoundingBox(v))); } catch { }
                            break;
                    }
                }
                catch (Exception ex) { j.Par("erro", ex.Message); }
                Parametros(j, "parametros", e, soInstancia: true);
                j.Fim();
            }
            j.FimLista();

            // the types used, with their parameters (text size, symbols, leaders...)
            j.Lista("tiposUsados");
            foreach (var kv in tiposUsados.OrderByDescending(k => k.Value))
            {
                Element t = Doc.GetElement(kv.Key);
                if (t == null) continue;
                j.Item();
                j.Par("categoria", t.Category?.Name);
                j.Par("familia", (t as ElementType)?.FamilyName);
                j.Par("tipo", t.Name);
                j.Par("usos", kv.Value);
                Parametros(j, "parametros", t);
                j.Fim();
            }
            j.FimLista();
        }

        // ================================================================== rooms and model

        private void Ambientes(J j, View v)
        {
            j.Lista("ambientes");
            try
            {
                foreach (Room r in new FilteredElementCollector(Doc, v.Id).OfCategory(BuiltInCategory.OST_Rooms).OfType<Room>().Where(r => r.Area > 0).Take(300))
                {
                    j.Item();
                    j.Par("numero", r.Number);
                    j.Par("nome", RevitUtil.RoomName(r));
                    j.Par("nivel", r.Level?.Name);
                    j.Par("areaM2", Math.Round(r.Area * 0.09290304, 2));
                    if (r.Location is LocationPoint lp) j.Par("pontoMm", Local(lp.Point));
                    try { j.Par("caixaMm", Caixa(r.get_BoundingBox(v))); } catch { }
                    j.Fim();
                }
            }
            catch { }
            j.FimLista();
        }

        private void Modelo(J j, View v)
        {
            j.Obj("elementosDoModeloVisiveis");
            try
            {
                var vis = new FilteredElementCollector(Doc, v.Id).WhereElementIsNotElementType()
                    .Where(e => e.Category != null && e.Category.CategoryType == CategoryType.Model && e.OwnerViewId == ElementId.InvalidElementId).ToList();
                foreach (var g in vis.GroupBy(e => e.Category.Name + " | " + Familia(e) + " | " + TipoNome(e)).OrderByDescending(g => g.Count()).Take(400))
                    j.Par(g.Key, g.Count());
            }
            catch { }
            j.Fim();
            j.Lista("vinculos");
            try
            {
                foreach (RevitLinkInstance li in new FilteredElementCollector(Doc, v.Id).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
                    j.Valor(li.Name);
                foreach (ImportInstance ii in new FilteredElementCollector(Doc, v.Id).OfClass(typeof(ImportInstance)).Cast<ImportInstance>())
                    j.Valor("CAD: " + (ii.Category?.Name ?? ii.Name) + (ii.ViewSpecific ? " (só nesta vista)" : ""));
            }
            catch { }
            j.FimLista();
        }

        // ================================================================== image

        private string Imagem(View v, string pasta, string nome)
        {
            var o = new ImageExportOptions
            {
                ExportRange = ExportRange.SetOfViews,
                FilePath = Path.Combine(pasta, nome),
                FitDirection = FitDirectionType.Horizontal,
                HLRandWFViewsFileType = ImageFileType.PNG,
                ShadowViewsFileType = ImageFileType.PNG,
                ImageResolution = ImageResolution.DPI_150,
                ZoomType = ZoomFitType.FitToPage,
                PixelSize = 4000
            };
            o.SetViewsAndSheets(new List<ElementId> { v.Id });
            Doc.ExportImage(o);
            return Directory.GetFiles(pasta, nome + "*.png").OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
        }

        // ================================================================== helpers

        private string Familia(Element e)
        {
            try
            {
                if (e is FamilyInstance fi) return fi.Symbol?.FamilyName;
                return (Doc.GetElement(e.GetTypeId()) as ElementType)?.FamilyName ?? e.GetType().Name;
            }
            catch { return e.GetType().Name; }
        }

        private string TipoNome(Element e)
        {
            try { return Doc.GetElement(e.GetTypeId())?.Name ?? ""; } catch { return ""; }
        }

        private void Parametros(J j, string chave, Element e, bool soInstancia = false)
        {
            j.Obj(chave);
            try
            {
                foreach (Parameter p in e.Parameters.Cast<Parameter>().OrderBy(p => p.Definition?.Name))
                {
                    if (p.Definition == null || !p.HasValue) continue;
                    string val;
                    try
                    {
                        switch (p.StorageType)
                        {
                            case StorageType.String: val = p.AsString(); break;
                            case StorageType.ElementId:
                                ElementId id = p.AsElementId();
                                val = id == ElementId.InvalidElementId ? null : (Doc.GetElement(id)?.Name ?? id.Value.ToString());
                                break;
                            default: val = p.AsValueString() ?? (p.StorageType == StorageType.Integer ? p.AsInteger().ToString() : p.AsDouble().ToString(CultureInfo.InvariantCulture)); break;
                        }
                    }
                    catch { continue; }
                    if (string.IsNullOrEmpty(val)) continue;
                    if (soInstancia && (p.Definition.Name == "Imagem" || p.Definition.Name == "Image")) continue;
                    j.Par(p.Definition.Name, val);
                }
            }
            catch { }
            j.Fim();
        }

        private static string Sobreposicao(OverrideGraphicSettings o)
        {
            if (o == null) return null;
            var s = new List<string>();
            try { if (o.ProjectionLineColor.IsValid) s.Add($"linha proj. RGB {o.ProjectionLineColor.Red},{o.ProjectionLineColor.Green},{o.ProjectionLineColor.Blue}"); } catch { }
            try { if (o.ProjectionLineWeight > 0) s.Add("peso proj. " + o.ProjectionLineWeight); } catch { }
            try { if (o.CutLineColor.IsValid) s.Add($"linha corte RGB {o.CutLineColor.Red},{o.CutLineColor.Green},{o.CutLineColor.Blue}"); } catch { }
            try { if (o.CutLineWeight > 0) s.Add("peso corte " + o.CutLineWeight); } catch { }
            try { if (o.Transparency > 0) s.Add("transparência " + o.Transparency); } catch { }
            try { if (o.Halftone) s.Add("meio-tom"); } catch { }
            try { if (o.SurfaceForegroundPatternColor.IsValid) s.Add($"superfície RGB {o.SurfaceForegroundPatternColor.Red},{o.SurfaceForegroundPatternColor.Green},{o.SurfaceForegroundPatternColor.Blue}"); } catch { }
            try { if (o.CutForegroundPatternColor.IsValid) s.Add($"corte RGB {o.CutForegroundPatternColor.Red},{o.CutForegroundPatternColor.Green},{o.CutForegroundPatternColor.Blue}"); } catch { }
            return s.Count == 0 ? null : string.Join("; ", s);
        }

        private static double Mm(double pes) => Math.Round(pes * 304.8, 1);

        /// <summary>Point in the view frame, mm from the crop's bottom-left (x right, y up).</summary>
        private double[] Local(XYZ p)
        {
            if (p == null) return null;
            XYZ l = _paraVista.OfPoint(p) - _origem;
            return new[] { Mm(l.X), Mm(l.Y) };
        }

        private static double[] Ponto(XYZ p) => p == null ? null : new[] { Mm(p.X), Mm(p.Y), Mm(p.Z) };
        private static double[] Vetor(XYZ v) => v == null ? null : new[] { Math.Round(v.X, 4), Math.Round(v.Y, 4), Math.Round(v.Z, 4) };

        private double[][] Caixa(BoundingBoxXYZ b) => b == null ? null : new[] { Local(b.Min), Local(b.Max) };

        private static string Tenta(Func<string> f) { try { return f(); } catch { return null; } }
        private static int? Tenta(Func<int> f) { try { return f(); } catch { return null; } }

        private static string Seguro(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Length > 60 ? s.Substring(0, 60) : s;
        }

        /// <summary>Tiny JSON writer (objects, lists, scalar values, arrays of numbers/strings).</summary>
        private sealed class J
        {
            private readonly StringBuilder _sb = new StringBuilder();
            private readonly Stack<bool> _primeiro = new Stack<bool>();
            private int Nivel => _primeiro.Count;

            private void Virgula()
            {
                if (_primeiro.Count == 0) return;
                bool p = _primeiro.Pop();
                if (!p) _sb.Append(',');
                _primeiro.Push(false);
                _sb.Append('\n').Append(' ', Nivel * 2);
            }

            public void Abre() { _sb.Append('{'); _primeiro.Push(true); }
            public void Fecha() { _primeiro.Pop(); _sb.Append("\n}"); }
            public void Obj(string k) { Virgula(); _sb.Append(Str(k)).Append(": {"); _primeiro.Push(true); }
            public void Fim() { _primeiro.Pop(); _sb.Append('\n').Append(' ', Nivel * 2).Append('}'); }
            public void Lista(string k) { Virgula(); _sb.Append(Str(k)).Append(": ["); _primeiro.Push(true); }
            public void FimLista() { _primeiro.Pop(); _sb.Append('\n').Append(' ', Nivel * 2).Append(']'); }
            public void Item() { Virgula(); _sb.Append('{'); _primeiro.Push(true); }
            public void Valor(string v) { Virgula(); _sb.Append(Str(v)); }

            public void Par(string k, object v)
            {
                Virgula();
                _sb.Append(Str(k)).Append(": ").Append(Val(v));
            }

            private static string Val(object v)
            {
                switch (v)
                {
                    case null: return "null";
                    case bool b: return b ? "true" : "false";
                    case string s: return Str(s);
                    case double d: return d.ToString("0.###", CultureInfo.InvariantCulture);
                    case int i: return i.ToString(CultureInfo.InvariantCulture);
                    case long l: return l.ToString(CultureInfo.InvariantCulture);
                    case double[] a: return "[" + string.Join(", ", a.Select(x => x.ToString("0.###", CultureInfo.InvariantCulture))) + "]";
                    case double[][] aa: return "[" + string.Join(", ", aa.Select(x => Val(x))) + "]";
                    case IEnumerable<string> ls: return "[" + string.Join(", ", ls.Select(Str)) + "]";
                    default: return Str(Convert.ToString(v, CultureInfo.InvariantCulture));
                }
            }

            private static string Str(string s)
            {
                if (s == null) return "null";
                var b = new StringBuilder("\"");
                foreach (char c in s)
                {
                    switch (c)
                    {
                        case '"': b.Append("\\\""); break;
                        case '\\': b.Append("\\\\"); break;
                        case '\n': b.Append("\\n"); break;
                        case '\r': break;
                        case '\t': b.Append("\\t"); break;
                        default: if (c < 32) b.Append("\\u").Append(((int)c).ToString("x4")); else b.Append(c); break;
                    }
                }
                return b.Append('"').ToString();
            }

            public override string ToString() => _sb.ToString();
        }
    }
}
