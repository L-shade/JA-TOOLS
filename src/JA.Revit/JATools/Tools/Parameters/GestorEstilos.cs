using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using RevitDwgUnifier.Infrastructure;
using RevitDwgUnifier.Tools.Common;

namespace RevitDwgUnifier.Tools.Parameters
{
    /// <summary>One style (type, line style, pattern, template, filter, material, family) in the list.</summary>
    public sealed class EstiloItem
    {
        public ElementId Id;
        /// <summary>Line styles: the projection GraphicsStyle (what curves point to).</summary>
        public ElementId Extra = ElementId.InvalidElementId;
        public string Nome = "", Detalhe = "";
        /// <summary>Only styles with the same key can be merged into each other.</summary>
        public string Compat = "";
        public bool Protegido;
        /// <summary>Why Revit would refuse to delete it (null = can be deleted): internal element,
        /// refused by Revit, last type of a system family / of a loadable family.</summary>
        public string NaoApaga;
        public bool Apagavel => !Protegido && NaoApaga == null;
        public int Usos, Refs;
        public HashSet<ElementId> Vistas = new HashSet<ElementId>();
        public List<ElementId> Instancias = new List<ElementId>();
        /// <summary>Owner view of each instance (same index as <see cref="Instancias"/>; invalid = model).</summary>
        public List<ElementId> InstVista = new List<ElementId>();
        /// <summary>The uses are views themselves (view templates, filters).</summary>
        public bool UsosSaoVistas;

        public void Uso(ElementId id, ElementId vista)
        {
            Usos++;
            Instancias.Add(id);
            InstVista.Add(vista ?? ElementId.InvalidElementId);
            if (vista != null && vista != ElementId.InvalidElementId) Vistas.Add(vista);
        }
        public string Assinatura;
        public bool Marcado;
        public string NovoNome;
        public int Total => Usos + Refs;
    }

    /// <summary>
    /// Everything read once per window session (cleared after each change): instances by type,
    /// curves by line style, views by template and by filter, ElementId references in type
    /// parameters. Big models: one pass over the elements, only when a kind needs it.
    /// </summary>
    public sealed class UsoCache
    {
        private readonly Document _doc;
        public UsoCache(Document doc) { _doc = doc; }

        private readonly Dictionary<string, Dictionary<ElementId, List<(ElementId id, ElementId vista)>>> _porTipo =
            new Dictionary<string, Dictionary<ElementId, List<(ElementId, ElementId)>>>();
        private Dictionary<ElementId, List<ElementId>> _refsEmTipos;
        private Dictionary<ElementId, List<ElementId>> _porEstiloLinha;
        private List<View> _vistas;
        private Dictionary<ElementId, ElementId> _vistaCurva;
        public ElementId VistaDaCurva(ElementId id) => _vistaCurva != null && _vistaCurva.TryGetValue(id, out var v) ? v : ElementId.InvalidElementId;

        public void Limpar() { _porTipo.Clear(); _refsEmTipos = null; _porEstiloLinha = null; _vistas = null; }

        /// <summary>
        /// Instances by type id, read once per kind with a narrow filter (only text notes for text
        /// types, only walls for wall types...) instead of every element of the model. The owner view
        /// comes with each instance, so nothing is read twice.
        /// </summary>
        public Dictionary<ElementId, List<(ElementId id, ElementId vista)>> PorTipo(string chave, ElementFilter filtro)
        {
            if (_porTipo.TryGetValue(chave, out var d)) return d;
            d = new Dictionary<ElementId, List<(ElementId, ElementId)>>();
            var col = new FilteredElementCollector(_doc).WhereElementIsNotElementType();
            if (filtro != null) col = col.WherePasses(filtro);
            foreach (Element e in col)
            {
                ElementId t;
                try { t = e.GetTypeId(); } catch { continue; }
                if (t == null || t == ElementId.InvalidElementId) continue;
                if (!d.TryGetValue(t, out var l)) d[t] = l = new List<(ElementId, ElementId)>();
                ElementId v;
                try { v = e.OwnerViewId; } catch { v = ElementId.InvalidElementId; }
                l.Add((e.Id, v));
            }
            _porTipo[chave] = d;
            return d;
        }

        /// <summary>Types whose parameters point to an element (a door type in a curtain panel, a profile…).</summary>
        public Dictionary<ElementId, List<ElementId>> RefsEmTipos
        {
            get
            {
                if (_refsEmTipos != null) return _refsEmTipos;
                _refsEmTipos = new Dictionary<ElementId, List<ElementId>>();
                foreach (Element t in new FilteredElementCollector(_doc).WhereElementIsElementType())
                    foreach (Parameter p in t.Parameters)
                    {
                        if (p.StorageType != StorageType.ElementId) continue;
                        ElementId v;
                        try { v = p.AsElementId(); } catch { continue; }
                        if (v == null || v.Value <= 0 || v == t.Id) continue;
                        if (!_refsEmTipos.TryGetValue(v, out var l)) _refsEmTipos[v] = l = new List<ElementId>();
                        l.Add(t.Id);
                    }
                return _refsEmTipos;
            }
        }

        public Dictionary<ElementId, List<ElementId>> PorEstiloLinha
        {
            get
            {
                if (_porEstiloLinha != null) return _porEstiloLinha;
                _porEstiloLinha = new Dictionary<ElementId, List<ElementId>>();
                _vistaCurva = new Dictionary<ElementId, ElementId>();
                foreach (CurveElement c in new FilteredElementCollector(_doc).OfClass(typeof(CurveElement)).Cast<CurveElement>())
                {
                    ElementId s;
                    try { s = c.LineStyle?.Id; } catch { continue; }
                    if (s == null) continue;
                    if (!_porEstiloLinha.TryGetValue(s, out var l)) _porEstiloLinha[s] = l = new List<ElementId>();
                    l.Add(c.Id);
                    _vistaCurva[c.Id] = c.OwnerViewId;
                }
                return _porEstiloLinha;
            }
        }

        /// <summary>Views and templates that accept graphic overrides.</summary>
        public List<View> Vistas => _vistas ?? (_vistas = new FilteredElementCollector(_doc).OfClass(typeof(View)).Cast<View>()
            .Where(v => { try { return v.AreGraphicsOverridesAllowed(); } catch { return false; } }).ToList());

        public void Somar(EstiloItem it, IEnumerable<(ElementId id, ElementId vista)> usos)
        {
            if (usos == null) return;
            foreach (var u in usos) it.Uso(u.id, u.vista);
        }

        /// <summary>Line styles: curves, with their owner view read from the element.</summary>
        public void SomarCurvas(EstiloItem it, IEnumerable<ElementId> ids)
        {
            if (ids == null) return;
            foreach (ElementId id in ids) it.Uso(id, VistaDaCurva(id));
        }
    }

    /// <summary>A kind of style the manager lists (text types, line patterns, view templates…).</summary>
    public abstract class EstiloTipo
    {
        public string Nome { get; protected set; }
        public string Grupo { get; protected set; }
        public virtual bool PodeUnir => true;
        public virtual bool PodeRenomear => true;
        /// <summary>Line styles: renaming re-creates the style (the API cannot rename subcategories).</summary>
        public virtual bool RenomeiaRecriando => false;
        public virtual string NotaUso => null;

        public abstract List<EstiloItem> Listar(Document doc);
        public abstract void Usos(Document doc, List<EstiloItem> itens, UsoCache cache);
        /// <summary>Moves every use of <paramref name="fontes"/> to <paramref name="alvo"/>; the sources are deleted after.</summary>
        public abstract int Unir(Document doc, EstiloItem alvo, List<EstiloItem> fontes, UsoCache cache, List<string> problemas);

        public virtual bool Renomear(Document doc, EstiloItem it, string novo, UsoCache cache, List<string> problemas)
        {
            Element e = doc.GetElement(it.Id);
            if (e == null) return false;
            try { e.Name = novo; return true; }
            catch (Exception ex) { problemas.Add($"{it.Nome}: {ex.Message}"); return false; }
        }

        public virtual bool Apagar(Document doc, EstiloItem it, List<string> problemas)
        {
            if (!it.Apagavel) return false;
            try { doc.Delete(it.Id); return true; }
            catch (Exception ex) { problemas.Add($"{it.Nome}: {ex.Message}"); return false; }
        }

        /// <summary>Identical styles share the signature (null = no duplicate check for this kind).</summary>
        public virtual void Assinar(Document doc, List<EstiloItem> itens)
        {
            foreach (var it in itens) it.Assinatura = GestorEstilos.AssinaturaParametros(doc.GetElement(it.Id));
        }

        public override string ToString() => Nome;
    }

    // ======================================================================== type-based kinds

    /// <summary>Any ElementType whose instances carry it (text, dimension, filled region, view, wall… types).</summary>
    public class TiposDeElemento : EstiloTipo
    {
        private readonly Func<Document, IEnumerable<ElementType>> _tipos;
        private readonly Func<ElementType, string> _compat, _detalhe;
        private readonly Func<ElementFilter> _filtro;
        private readonly bool _refs;

        /// <param name="filtro">Instances to look at (null = by the categories of the listed types).</param>
        /// <param name="refs">Also count types whose parameters point to these (curtain panels, nested families…): slower.</param>
        public TiposDeElemento(string nome, string grupo, Func<Document, IEnumerable<ElementType>> tipos,
                               Func<ElementType, string> compat = null, Func<ElementType, string> detalhe = null,
                               Func<ElementFilter> filtro = null, bool refs = false)
        {
            Nome = nome; Grupo = grupo; _tipos = tipos; _filtro = filtro; _refs = refs;
            _compat = compat ?? (t => t.GetType().Name + "|" + (t.Category?.Id.Value ?? 0));
            _detalhe = detalhe ?? (t => t.FamilyName);
        }

        public override List<EstiloItem> Listar(Document doc) =>
            _tipos(doc).Where(t => t != null).Select(t => new EstiloItem
            {
                Id = t.Id, Nome = t.Name, Detalhe = Safe(() => _detalhe(t)), Compat = Safe(() => _compat(t))
            }).OrderBy(i => i.Detalhe).ThenBy(i => i.Nome, StringComparer.CurrentCultureIgnoreCase).ToList();

        private static string Safe(Func<string> f) { try { return f() ?? ""; } catch { return ""; } }

        /// <summary>The instances of this kind: the given filter, or the categories of the types listed.</summary>
        protected Dictionary<ElementId, List<(ElementId id, ElementId vista)>> Instancias(Document doc, IEnumerable<EstiloItem> itens, UsoCache cache)
        {
            ElementFilter f = _filtro?.Invoke();
            if (f == null) f = GestorEstilos.FiltroCategorias(itens.Select(i => doc.GetElement(i.Id)?.Category?.Id));
            return cache.PorTipo(Nome, f);
        }

        public override void Usos(Document doc, List<EstiloItem> itens, UsoCache cache)
        {
            var porTipo = Instancias(doc, itens, cache);
            foreach (var it in itens)
            {
                if (porTipo.TryGetValue(it.Id, out var ids)) cache.Somar(it, ids);
                if (_refs && cache.RefsEmTipos.TryGetValue(it.Id, out var refs)) it.Refs += refs.Count;
            }
        }

        public override int Unir(Document doc, EstiloItem alvo, List<EstiloItem> fontes, UsoCache cache, List<string> problemas)
        {
            int n = 0, falhas = 0;
            var fonteIds = new HashSet<ElementId>(fontes.Select(f => f.Id));
            var porTipo = Instancias(doc, fontes.Concat(new[] { alvo }), cache);
            foreach (var f in fontes)
            {
                if (!porTipo.TryGetValue(f.Id, out var ids)) continue;
                foreach (var u in ids)
                {
                    Element e = doc.GetElement(u.id);
                    if (e == null) continue;
                    try { e.ChangeTypeId(alvo.Id); n++; }
                    catch { falhas++; }
                }
            }
            if (_refs) n += GestorEstilos.TrocarReferencias(doc, cache, fonteIds, alvo.Id);
            if (falhas > 0) problemas.Add($"{falhas} elemento(s) não aceitaram {alvo.Nome} (em grupo, ou tipo incompatível) e mantêm o estilo antigo.");
            return n;
        }
    }

    /// <summary>Loadable family types (tags, symbols, doors, furniture…), merged inside the same category.</summary>
    public sealed class TiposDeFamilia : TiposDeElemento
    {
        public TiposDeFamilia(string nome, string grupo, Func<Category, bool> categoria, bool refs)
            : base(nome, grupo,
                   d => new FilteredElementCollector(d).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                        .Where(s => s.Category != null && categoria(s.Category) && !(s.Family?.IsInPlace ?? false)),
                   t => "FS|" + (t.Category?.Id.Value ?? 0),
                   t => (t.Category?.Name ?? "") + " · " + t.FamilyName,
                   null, refs)
        { }
    }

    /// <summary>Families (purge whole families; merge is done on their types).</summary>
    public sealed class Familias : EstiloTipo
    {
        public Familias() { Nome = "Famílias"; Grupo = GestorEstilos.GMateriais; }
        public override bool PodeUnir => false;
        public override string NotaUso => "Usos = cópias de todos os tipos da família no modelo. Para unir tipos, use “Tipos de família”.";

        public override List<EstiloItem> Listar(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().Where(f => !f.IsInPlace && f.FamilyCategory != null)
                .Select(f => new EstiloItem { Id = f.Id, Nome = f.Name, Detalhe = f.FamilyCategory.Name, Compat = "F|" + f.FamilyCategory.Id.Value })
                .OrderBy(i => i.Detalhe).ThenBy(i => i.Nome, StringComparer.CurrentCultureIgnoreCase).ToList();

        public override void Usos(Document doc, List<EstiloItem> itens, UsoCache cache)
        {
            var fams = itens.Select(i => doc.GetElement(i.Id) as Family).ToList();
            var porTipo = cache.PorTipo(Nome, GestorEstilos.FiltroCategorias(fams.Select(f => f?.FamilyCategory?.Id)));
            foreach (var it in itens)
            {
                if (!(doc.GetElement(it.Id) is Family f)) continue;
                foreach (ElementId s in f.GetFamilySymbolIds())
                {
                    if (porTipo.TryGetValue(s, out var ids)) cache.Somar(it, ids);
                    if (cache.RefsEmTipos.TryGetValue(s, out var refs)) it.Refs += refs.Count;
                }
            }
        }

        public override int Unir(Document doc, EstiloItem alvo, List<EstiloItem> fontes, UsoCache cache, List<string> problemas) => 0;
        public override void Assinar(Document doc, List<EstiloItem> itens) { }
    }

    // ======================================================================== line styles and patterns

    public sealed class EstilosDeLinha : EstiloTipo
    {
        public EstilosDeLinha() { Nome = "Estilos de linha"; Grupo = GestorEstilos.GLinhas; }
        public override bool RenomeiaRecriando => true;
        public override string NotaUso => "Usos = linhas de detalhe e de modelo. Bordas de regiões preenchidas e linhas de esboço não entram.";

        private static Category Linhas(Document doc) => doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);

        public override List<EstiloItem> Listar(Document doc)
        {
            var list = new List<EstiloItem>();
            foreach (Category c in Linhas(doc).SubCategories)
            {
                GraphicsStyle gs = null;
                try { gs = c.GetGraphicsStyle(GraphicsStyleType.Projection); } catch { }
                list.Add(new EstiloItem
                {
                    Id = c.Id, Extra = gs?.Id ?? ElementId.InvalidElementId, Nome = c.Name, Compat = "linha",
                    Protegido = c.Id.Value < 0,
                    Detalhe = $"{Peso(c)} · {Cor(c.LineColor)} · {Padrao(doc, c.GetLinePatternId(GraphicsStyleType.Projection))}"
                });
            }
            return list.OrderBy(i => i.Nome, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static string Peso(Category c) { try { return "espessura " + (c.GetLineWeight(GraphicsStyleType.Projection) ?? 0); } catch { return ""; } }
        private static string Cor(Color c) { try { return c == null || !c.IsValid ? "sem cor" : $"RGB {c.Red},{c.Green},{c.Blue}"; } catch { return ""; } }
        internal static string Padrao(Document doc, ElementId id) =>
            id == null || id == ElementId.InvalidElementId ? "—" : id == LinePatternElement.GetSolidPatternId() ? "Sólido" : doc.GetElement(id)?.Name ?? "—";

        public override void Usos(Document doc, List<EstiloItem> itens, UsoCache cache)
        {
            foreach (var it in itens)
                if (cache.PorEstiloLinha.TryGetValue(it.Extra, out var ids)) cache.SomarCurvas(it, ids);
        }

        public override int Unir(Document doc, EstiloItem alvo, List<EstiloItem> fontes, UsoCache cache, List<string> problemas)
        {
            GraphicsStyle gs = doc.GetElement(alvo.Extra) as GraphicsStyle;
            if (gs == null) { problemas.Add("Estilo de linha de destino inválido."); return 0; }
            int n = 0, falhas = 0;
            foreach (var f in fontes)
            {
                if (!cache.PorEstiloLinha.TryGetValue(f.Extra, out var ids)) continue;
                foreach (ElementId id in ids)
                {
                    if (!(doc.GetElement(id) is CurveElement c)) continue;
                    try { c.LineStyle = gs; n++; } catch { falhas++; }
                }
            }
            if (falhas > 0) problemas.Add($"{falhas} linha(s) não aceitaram {alvo.Nome} (em grupo ou em esboço).");
            return n;
        }

        /// <summary>Re-creates the style with the new name (same weight, colour, pattern), moves the lines, deletes the old one.</summary>
        public override bool Renomear(Document doc, EstiloItem it, string novo, UsoCache cache, List<string> problemas)
        {
            if (it.Protegido) { problemas.Add($"{it.Nome}: estilo do Revit, não pode ser renomeado."); return false; }
            try
            {
                Category linhas = Linhas(doc);
                Category antiga = linhas.SubCategories.Cast<Category>().FirstOrDefault(c => c.Id == it.Id);
                if (antiga == null) return false;
                if (linhas.SubCategories.Cast<Category>().Any(c => string.Equals(c.Name, novo, StringComparison.OrdinalIgnoreCase)))
                { problemas.Add($"{it.Nome}: já existe um estilo “{novo}”."); return false; }
                Category nova = doc.Settings.Categories.NewSubcategory(linhas, novo);
                try { nova.SetLineWeight(antiga.GetLineWeight(GraphicsStyleType.Projection) ?? 1, GraphicsStyleType.Projection); } catch { }
                try { nova.LineColor = antiga.LineColor; } catch { }
                try { nova.SetLinePatternId(antiga.GetLinePatternId(GraphicsStyleType.Projection), GraphicsStyleType.Projection); } catch { }
                doc.Regenerate();
                var alvo = new EstiloItem { Id = nova.Id, Extra = nova.GetGraphicsStyle(GraphicsStyleType.Projection).Id, Nome = novo };
                Unir(doc, alvo, new List<EstiloItem> { it }, cache, problemas);
                doc.Delete(antiga.Id);
                return true;
            }
            catch (Exception ex) { problemas.Add($"{it.Nome}: {ex.Message}"); return false; }
        }

        public override void Assinar(Document doc, List<EstiloItem> itens)
        {
            Category linhas = Linhas(doc);
            foreach (var it in itens)
            {
                Category c = linhas.SubCategories.Cast<Category>().FirstOrDefault(x => x.Id == it.Id);
                if (c == null) continue;
                try { it.Assinatura = $"{c.GetLineWeight(GraphicsStyleType.Projection)}|{c.LineColor?.Red},{c.LineColor?.Green},{c.LineColor?.Blue}|{c.GetLinePatternId(GraphicsStyleType.Projection)?.Value}"; }
                catch { }
            }
        }
    }

    public sealed class PadroesDeLinha : EstiloTipo
    {
        public PadroesDeLinha() { Nome = "Padrões de linha"; Grupo = GestorEstilos.GLinhas; }
        public override string NotaUso => "Usos = estilos de objeto e de linha (todas as categorias) e sobreposições de filtros nas vistas. Sobreposições por elemento ou por categoria numa vista não entram.";

        public override List<EstiloItem> Listar(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(LinePatternElement)).Cast<LinePatternElement>()
                .Select(p => new EstiloItem { Id = p.Id, Nome = p.Name, Compat = "lp", Detalhe = Segs(p) })
                .OrderBy(i => i.Nome, StringComparer.CurrentCultureIgnoreCase).ToList();

        private static string Segs(LinePatternElement p)
        {
            try { return string.Join(" ", p.GetLinePattern().GetSegments().Select(s => s.Type.ToString().Substring(0, 1) + (s.Type == LinePatternSegmentType.Dot ? "" : Math.Round(s.Length * 304.8, 1).ToString(CultureInfo.InvariantCulture)))); }
            catch { return ""; }
        }

        public override void Usos(Document doc, List<EstiloItem> itens, UsoCache cache)
        {
            var map = itens.ToDictionary(i => i.Id);
            foreach (Category c in GestorEstilos.TodasCategorias(doc))
                foreach (GraphicsStyleType g in new[] { GraphicsStyleType.Projection, GraphicsStyleType.Cut })
                {
                    ElementId id;
                    try { id = c.GetLinePatternId(g); } catch { continue; }
                    if (id != null && map.TryGetValue(id, out var it)) it.Refs++;
                }
            foreach (View v in cache.Vistas)
                foreach (ElementId f in GestorEstilos.Filtros(v))
                {
                    OverrideGraphicSettings o;
                    try { o = v.GetFilterOverrides(f); } catch { continue; }
                    foreach (ElementId id in new[] { o.ProjectionLinePatternId, o.CutLinePatternId })
                        if (map.TryGetValue(id, out var it)) { it.UsosSaoVistas = true; it.Uso(v.Id, v.Id); }
                }
        }

        public override int Unir(Document doc, EstiloItem alvo, List<EstiloItem> fontes, UsoCache cache, List<string> problemas)
        {
            var src = new HashSet<ElementId>(fontes.Select(f => f.Id));
            int n = 0;
            foreach (Category c in GestorEstilos.TodasCategorias(doc))
                foreach (GraphicsStyleType g in new[] { GraphicsStyleType.Projection, GraphicsStyleType.Cut })
                {
                    try { if (src.Contains(c.GetLinePatternId(g))) { c.SetLinePatternId(alvo.Id, g); n++; } } catch { }
                }
            foreach (View v in cache.Vistas)
                foreach (ElementId f in GestorEstilos.Filtros(v))
                {
                    try
                    {
                        OverrideGraphicSettings o = v.GetFilterOverrides(f);
                        bool mudou = false;
                        if (src.Contains(o.ProjectionLinePatternId)) { o.SetProjectionLinePatternId(alvo.Id); mudou = true; }
                        if (src.Contains(o.CutLinePatternId)) { o.SetCutLinePatternId(alvo.Id); mudou = true; }
                        if (mudou) { v.SetFilterOverrides(f, o); n++; }
                    }
                    catch { }
                }
            return n;
        }

        public override void Assinar(Document doc, List<EstiloItem> itens)
        {
            foreach (var it in itens) it.Assinatura = doc.GetElement(it.Id) is LinePatternElement p ? Segs(p) : null;
        }
    }

    public sealed class PadroesDePreenchimento : EstiloTipo
    {
        public PadroesDePreenchimento() { Nome = "Padrões de preenchimento"; Grupo = GestorEstilos.GLinhas; }
        public override string NotaUso => "Usos = tipos de região preenchida, materiais (superfície e corte) e sobreposições de filtros nas vistas.";

        public override List<EstiloItem> Listar(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                .Select(p =>
                {
                    FillPattern fp = null;
                    try { fp = p.GetFillPattern(); } catch { }
                    return new EstiloItem
                    {
                        Id = p.Id, Nome = p.Name, Protegido = fp?.IsSolidFill ?? false,
                        Compat = "fp|" + (fp?.Target.ToString() ?? ""),
                        Detalhe = fp == null ? "" : (fp.Target == FillPatternTarget.Drafting ? "Desenho" : "Modelo") + (fp.IsSolidFill ? " · sólido" : $" · {fp.GridCount} linha(s)")
                    };
                })
                .OrderBy(i => i.Detalhe).ThenBy(i => i.Nome, StringComparer.CurrentCultureIgnoreCase).ToList();

        private static IEnumerable<(Element dono, Func<ElementId> get, Action<ElementId> set)> Lugares(Document doc, UsoCache cache)
        {
            foreach (FilledRegionType t in new FilteredElementCollector(doc).OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>())
            {
                var tt = t;
                yield return (tt, () => tt.ForegroundPatternId, v => tt.ForegroundPatternId = v);
                yield return (tt, () => tt.BackgroundPatternId, v => tt.BackgroundPatternId = v);
            }
            foreach (Material m in new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>())
            {
                var mm = m;
                yield return (mm, () => mm.SurfaceForegroundPatternId, v => mm.SurfaceForegroundPatternId = v);
                yield return (mm, () => mm.SurfaceBackgroundPatternId, v => mm.SurfaceBackgroundPatternId = v);
                yield return (mm, () => mm.CutForegroundPatternId, v => mm.CutForegroundPatternId = v);
                yield return (mm, () => mm.CutBackgroundPatternId, v => mm.CutBackgroundPatternId = v);
            }
        }

        public override void Usos(Document doc, List<EstiloItem> itens, UsoCache cache)
        {
            var map = itens.ToDictionary(i => i.Id);
            foreach (var l in Lugares(doc, cache))
            {
                ElementId id;
                try { id = l.get(); } catch { continue; }
                if (id != null && map.TryGetValue(id, out var it)) it.Refs++;
            }
            foreach (View v in cache.Vistas)
                foreach (ElementId f in GestorEstilos.Filtros(v))
                {
                    OverrideGraphicSettings o;
                    try { o = v.GetFilterOverrides(f); } catch { continue; }
                    foreach (ElementId id in new[] { o.SurfaceForegroundPatternId, o.SurfaceBackgroundPatternId, o.CutForegroundPatternId, o.CutBackgroundPatternId })
                        if (map.TryGetValue(id, out var it)) { it.UsosSaoVistas = true; it.Uso(v.Id, v.Id); }
                }
        }

        public override int Unir(Document doc, EstiloItem alvo, List<EstiloItem> fontes, UsoCache cache, List<string> problemas)
        {
            var src = new HashSet<ElementId>(fontes.Select(f => f.Id));
            int n = 0, falhas = 0;
            foreach (var l in Lugares(doc, cache))
            {
                try { if (src.Contains(l.get())) { l.set(alvo.Id); n++; } } catch { falhas++; }
            }
            foreach (View v in cache.Vistas)
                foreach (ElementId f in GestorEstilos.Filtros(v))
                {
                    try
                    {
                        OverrideGraphicSettings o = v.GetFilterOverrides(f);
                        bool mudou = false;
                        if (src.Contains(o.SurfaceForegroundPatternId)) { o.SetSurfaceForegroundPatternId(alvo.Id); mudou = true; }
                        if (src.Contains(o.SurfaceBackgroundPatternId)) { o.SetSurfaceBackgroundPatternId(alvo.Id); mudou = true; }
                        if (src.Contains(o.CutForegroundPatternId)) { o.SetCutForegroundPatternId(alvo.Id); mudou = true; }
                        if (src.Contains(o.CutBackgroundPatternId)) { o.SetCutBackgroundPatternId(alvo.Id); mudou = true; }
                        if (mudou) { v.SetFilterOverrides(f, o); n++; }
                    }
                    catch { falhas++; }
                }
            if (falhas > 0) problemas.Add($"{falhas} lugar(es) não aceitaram {alvo.Nome} (padrão de desenho × de modelo?).");
            return n;
        }

        public override void Assinar(Document doc, List<EstiloItem> itens)
        {
            foreach (var it in itens)
            {
                try
                {
                    FillPattern fp = (doc.GetElement(it.Id) as FillPatternElement)?.GetFillPattern();
                    if (fp == null || fp.IsSolidFill) { it.Assinatura = fp != null ? "solid" : null; continue; }
                    var sb = new StringBuilder(fp.Target + "|" + fp.HostOrientation + "|");
                    foreach (FillGrid g in fp.GetFillGrids())
                        sb.Append($"{Math.Round(g.Angle, 4)};{Math.Round(g.Origin.U, 5)};{Math.Round(g.Origin.V, 5)};{Math.Round(g.Offset, 5)};{Math.Round(g.Shift, 5)};{string.Join(",", g.GetSegments().Select(s => Math.Round(s, 5)))}/");
                    it.Assinatura = sb.ToString();
                }
                catch { }
            }
        }
    }

    // ======================================================================== views

    public sealed class ModelosDeVista : EstiloTipo
    {
        public ModelosDeVista() { Nome = "Modelos de vista"; Grupo = GestorEstilos.GVistas; }
        public override string NotaUso => "Usos = vistas com o modelo aplicado; referências = tipos de vista que o usam como modelo padrão.";

        public override List<EstiloItem> Listar(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => v.IsTemplate)
                .Select(v => new EstiloItem { Id = v.Id, Nome = v.Name, Detalhe = GestorEstilos.NomeTipoVista(v.ViewType), Compat = "vt|" + v.ViewType })
                .OrderBy(i => i.Detalhe).ThenBy(i => i.Nome, StringComparer.CurrentCultureIgnoreCase).ToList();

        public override void Usos(Document doc, List<EstiloItem> itens, UsoCache cache)
        {
            var map = itens.ToDictionary(i => i.Id);
            foreach (View v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate))
                if (map.TryGetValue(v.ViewTemplateId, out var it)) { it.UsosSaoVistas = true; it.Uso(v.Id, v.Id); }
            foreach (ViewFamilyType t in new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>())
            {
                try { if (map.TryGetValue(t.DefaultTemplateId, out var it)) it.Refs++; } catch { }
            }
        }

        public override int Unir(Document doc, EstiloItem alvo, List<EstiloItem> fontes, UsoCache cache, List<string> problemas)
        {
            var src = new HashSet<ElementId>(fontes.Select(f => f.Id));
            int n = 0, falhas = 0;
            foreach (View v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate && src.Contains(v.ViewTemplateId)))
            {
                try { if (v.IsValidViewTemplate(alvo.Id)) { v.ViewTemplateId = alvo.Id; n++; } else falhas++; } catch { falhas++; }
            }
            foreach (ViewFamilyType t in new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>())
            {
                try { if (src.Contains(t.DefaultTemplateId)) { t.DefaultTemplateId = alvo.Id; n++; } } catch { }
            }
            if (falhas > 0) problemas.Add($"{falhas} vista(s) não aceitam {alvo.Nome} (tipo de vista diferente) e perderam o modelo.");
            return n;
        }

        public override void Assinar(Document doc, List<EstiloItem> itens) { }
    }

    public sealed class FiltrosDeVista : EstiloTipo
    {
        public FiltrosDeVista() { Nome = "Filtros de vista"; Grupo = GestorEstilos.GVistas; }
        public override string NotaUso => "Usos = vistas e modelos de vista que têm o filtro. Ao unir, a vista recebe o filtro de destino com as mesmas sobreposições e visibilidade.";

        public override List<EstiloItem> Listar(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(FilterElement)).Cast<FilterElement>()
                .Select(f => new EstiloItem
                {
                    Id = f.Id, Nome = f.Name, Compat = "filtro",
                    Detalhe = f is ParameterFilterElement pf ? Cats(doc, pf) : "Seleção"
                })
                .OrderBy(i => i.Nome, StringComparer.CurrentCultureIgnoreCase).ToList();

        private static string Cats(Document doc, ParameterFilterElement pf)
        {
            try
            {
                var names = pf.GetCategories().Select(id => Category.GetCategory(doc, id)?.Name).Where(n => n != null).ToList();
                return names.Count <= 3 ? string.Join(", ", names) : string.Join(", ", names.Take(3)) + $" +{names.Count - 3}";
            }
            catch { return ""; }
        }

        public override void Usos(Document doc, List<EstiloItem> itens, UsoCache cache)
        {
            var map = itens.ToDictionary(i => i.Id);
            foreach (View v in cache.Vistas)
                foreach (ElementId f in GestorEstilos.Filtros(v))
                    if (map.TryGetValue(f, out var it)) { it.UsosSaoVistas = true; it.Uso(v.Id, v.Id); }
        }

        public override int Unir(Document doc, EstiloItem alvo, List<EstiloItem> fontes, UsoCache cache, List<string> problemas)
        {
            var src = new HashSet<ElementId>(fontes.Select(f => f.Id));
            int n = 0;
            foreach (View v in cache.Vistas)
            {
                var fs = GestorEstilos.Filtros(v);
                foreach (ElementId f in fs.Where(src.Contains).ToList())
                {
                    try
                    {
                        if (!fs.Contains(alvo.Id))
                        {
                            OverrideGraphicSettings o = v.GetFilterOverrides(f);
                            bool vis = v.GetFilterVisibility(f);
                            v.AddFilter(alvo.Id);
                            v.SetFilterOverrides(alvo.Id, o);
                            v.SetFilterVisibility(alvo.Id, vis);
                            fs.Add(alvo.Id);
                        }
                        v.RemoveFilter(f);
                        n++;
                    }
                    catch (Exception ex) { RevitUtil.AddOnce(problemas, $"{v.Name}: {ex.Message}"); }
                }
            }
            return n;
        }

        public override void Assinar(Document doc, List<EstiloItem> itens) { }
    }

    // ======================================================================== materials

    public sealed class Materiais : EstiloTipo
    {
        public Materiais() { Nome = "Materiais"; Grupo = GestorEstilos.GMateriais; }
        public override string NotaUso => "Usos = camadas de paredes, pisos, telhados e forros, parâmetros de material de tipos e de famílias colocadas e estilos de objeto. Materiais fixos dentro de famílias e pinturas de face não entram.";

        public override List<EstiloItem> Listar(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>()
                .Select(m => new EstiloItem { Id = m.Id, Nome = m.Name, Compat = "mat", Detalhe = string.IsNullOrEmpty(m.MaterialClass) ? m.MaterialCategory : m.MaterialClass })
                .OrderBy(i => i.Nome, StringComparer.CurrentCultureIgnoreCase).ToList();

        /// <summary>
        /// Instance material parameters of a placed family. Which parameters are materials is found once
        /// per family (on its first instance); the other instances only read those, not all ~60 parameters.
        /// </summary>
        private static IEnumerable<Parameter> ParametrosMaterial(FamilyInstance fi, Dictionary<ElementId, List<Definition>> cache)
        {
            ElementId fam;
            try { fam = fi.Symbol?.Family?.Id ?? ElementId.InvalidElementId; } catch { fam = ElementId.InvalidElementId; }
            if (!cache.TryGetValue(fam, out var lista))
            {
                lista = new List<Definition>();
                foreach (Parameter p in fi.Parameters)
                {
                    try
                    {
                        if (p.StorageType == StorageType.ElementId && p.Definition != null &&
                            p.Definition.GetDataType() == SpecTypeId.Reference.Material) lista.Add(p.Definition);
                    }
                    catch { }
                }
                cache[fam] = lista;
            }
            foreach (Definition d in lista)
            {
                Parameter p;
                try { p = fi.get_Parameter(d); } catch { continue; }
                if (p != null && p.StorageType == StorageType.ElementId) yield return p;
            }
        }

        private static IEnumerable<HostObjAttributes> Hospedeiros(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(HostObjAttributes)).Cast<HostObjAttributes>();

        public override void Usos(Document doc, List<EstiloItem> itens, UsoCache cache)
        {
            var map = itens.ToDictionary(i => i.Id);
            foreach (HostObjAttributes t in Hospedeiros(doc))
            {
                CompoundStructure cs = null;
                try { cs = t.GetCompoundStructure(); } catch { }
                if (cs == null) continue;
                foreach (CompoundStructureLayer l in cs.GetLayers())
                    if (map.TryGetValue(l.MaterialId, out var it)) { it.Refs++; }
            }
            foreach (var kv in cache.RefsEmTipos)
                if (map.TryGetValue(kv.Key, out var it)) it.Refs += kv.Value.Count;
            var defs = new Dictionary<ElementId, List<Definition>>();
            foreach (FamilyInstance fi in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>())
                foreach (Parameter p in ParametrosMaterial(fi, defs))
                {
                    ElementId v;
                    try { v = p.AsElementId(); } catch { continue; }
                    if (v != null && map.TryGetValue(v, out var it)) it.Uso(fi.Id, fi.OwnerViewId);
                }
            foreach (Category c in GestorEstilos.TodasCategorias(doc))
            {
                try { if (c.Material != null && map.TryGetValue(c.Material.Id, out var it)) it.Refs++; } catch { }
            }
        }

        public override int Unir(Document doc, EstiloItem alvo, List<EstiloItem> fontes, UsoCache cache, List<string> problemas)
        {
            var src = new HashSet<ElementId>(fontes.Select(f => f.Id));
            Material m = doc.GetElement(alvo.Id) as Material;
            int n = 0, falhas = 0;
            foreach (HostObjAttributes t in Hospedeiros(doc))
            {
                try
                {
                    CompoundStructure cs = t.GetCompoundStructure();
                    if (cs == null) continue;
                    bool mudou = false;
                    IList<CompoundStructureLayer> layers = cs.GetLayers();
                    for (int i = 0; i < layers.Count; i++)
                        if (src.Contains(layers[i].MaterialId)) { cs.SetMaterialId(i, alvo.Id); mudou = true; }
                    if (mudou) { t.SetCompoundStructure(cs); n++; }
                }
                catch { falhas++; }
            }
            n += GestorEstilos.TrocarReferencias(doc, cache, src, alvo.Id);
            var defs = new Dictionary<ElementId, List<Definition>>();
            foreach (FamilyInstance fi in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>())
                foreach (Parameter p in ParametrosMaterial(fi, defs))
                {
                    if (p.IsReadOnly) continue;
                    try { if (src.Contains(p.AsElementId())) { p.Set(alvo.Id); n++; } } catch { falhas++; }
                }
            foreach (Category c in GestorEstilos.TodasCategorias(doc))
            {
                try { if (c.Material != null && src.Contains(c.Material.Id)) { c.Material = m; n++; } } catch { }
            }
            if (falhas > 0) problemas.Add($"{falhas} lugar(es) não aceitaram o material {alvo.Nome}.");
            return n;
        }

        public override void Assinar(Document doc, List<EstiloItem> itens)
        {
            foreach (var it in itens)
            {
                if (!(doc.GetElement(it.Id) is Material m)) continue;
                try
                {
                    it.Assinatura = string.Join("|", m.Color?.Red, m.Color?.Green, m.Color?.Blue, m.Transparency, m.Shininess, m.Smoothness,
                        m.SurfaceForegroundPatternId?.Value, m.SurfaceBackgroundPatternId?.Value, m.CutForegroundPatternId?.Value, m.CutBackgroundPatternId?.Value,
                        m.AppearanceAssetId?.Value, m.MaterialClass);
                }
                catch { }
            }
        }
    }

    // ======================================================================== catalogue + helpers

    public static class GestorEstilos
    {
        public const string GTextos = "Textos e cotas", GLinhas = "Linhas e padrões", GVistas = "Vistas", GMateriais = "Materiais e famílias";

        public static List<EstiloTipo> Tipos() => new List<EstiloTipo>
        {
            new TiposDeElemento("Tipos de texto", GTextos, d => Of<TextNoteType>(d), t => "texto", t => Fonte(t),
                () => new ElementClassFilter(typeof(TextNote))),
            new TiposDeElemento("Tipos de cota", GTextos, d => Of<DimensionType>(d).Where(t => !string.IsNullOrEmpty(t.Name)),
                t => "cota|" + ((DimensionType)t).StyleType, t => NomeCota(((DimensionType)t).StyleType),
                () => new LogicalOrFilter(new ElementClassFilter(typeof(Dimension)), new ElementMulticategoryFilter(new List<BuiltInCategory>
                    { BuiltInCategory.OST_Dimensions, BuiltInCategory.OST_SpotElevations, BuiltInCategory.OST_SpotCoordinates, BuiltInCategory.OST_SpotSlopes }))),
            new TiposDeFamilia("Etiquetas e símbolos", GTextos, c => c.CategoryType == CategoryType.Annotation, false),
            new EstilosDeLinha(),
            new PadroesDeLinha(),
            new PadroesDePreenchimento(),
            new TiposDeElemento("Tipos de região preenchida", GLinhas, d => Of<FilledRegionType>(d), t => "regiao", t => "",
                () => new ElementClassFilter(typeof(FilledRegion))),
            new ModelosDeVista(),
            new TiposDeElemento("Tipos de vista", GVistas, d => Of<ViewFamilyType>(d), t => "vft|" + ((ViewFamilyType)t).ViewFamily, t => ((ViewFamilyType)t).ViewFamily.ToString(),
                () => new ElementClassFilter(typeof(View))),
            new FiltrosDeVista(),
            new Materiais(),
            new TiposDeElemento("Tipos de parede", GMateriais, d => Of<WallType>(d), t => "wall|" + ((WallType)t).Kind, t => t.FamilyName,
                () => new ElementClassFilter(typeof(Wall)), true),
            new TiposDeElemento("Tipos de piso", GMateriais, d => Of<FloorType>(d), t => "floor|" + ((FloorType)t).IsFoundationSlab, t => t.FamilyName,
                () => new ElementClassFilter(typeof(Floor))),
            new TiposDeElemento("Tipos de forro", GMateriais, d => Of<CeilingType>(d), t => "ceiling", t => t.FamilyName,
                () => new ElementClassFilter(typeof(Ceiling))),
            new TiposDeElemento("Tipos de telhado", GMateriais, d => Of<RoofType>(d), t => "roof", t => t.FamilyName,
                () => new ElementCategoryFilter(BuiltInCategory.OST_Roofs)),
            new TiposDeFamilia("Tipos de família", GMateriais, c => c.CategoryType == CategoryType.Model, true),
            new Familias()
        };

        /// <summary>
        /// Deletes the styles with one <c>Document.Delete</c> for the whole list (Revit analyses the
        /// dependencies once, instead of once per style). If Revit refuses the batch (one style that
        /// cannot go: the last text type, a style in use by something not counted...), the list is
        /// split in halves and retried, so only the refused ones stay. Inside a transaction.
        /// </summary>
        public static List<EstiloItem> ApagarEmLote(Document doc, IList<EstiloItem> itens, List<string> problemas)
        {
            var apagados = new List<EstiloItem>();
            List<EstiloItem> candidatos = SoApagaveis(doc, itens, problemas);
            void Lote(List<EstiloItem> lista)
            {
                if (lista.Count == 0) return;
                using (var st = new SubTransaction(doc))
                {
                    st.Start();
                    try
                    {
                        doc.Delete(lista.Select(i => i.Id).ToList());
                        st.Commit();
                        apagados.AddRange(lista);
                        return;
                    }
                    catch (Exception ex)
                    {
                        st.RollBack();
                        if (lista.Count == 1) { problemas.Add($"{lista[0].Nome}: {ex.Message}"); return; }
                    }
                }
                int meio = lista.Count / 2;
                Lote(lista.Take(meio).ToList());
                Lote(lista.Skip(meio).ToList());
            }
            Lote(candidatos);
            return apagados;
        }

        // ------------------------------------------------------------------ what Revit refuses to delete

        private static Func<Document, ElementId, bool> _podeApagar;
        private static bool _podeApagarLido;

        /// <summary>Revit's own answer (DocumentValidation.CanDeleteElement, read by reflection so a
        /// missing member never breaks the build); true when it cannot be asked.</summary>
        public static bool RevitDeixaApagar(Document doc, ElementId id)
        {
            if (!_podeApagarLido)
            {
                _podeApagarLido = true;
                try
                {
                    var m = typeof(Document).Assembly.GetType("Autodesk.Revit.DB.DocumentValidation")?
                        .GetMethod("CanDeleteElement", new[] { typeof(Document), typeof(ElementId) });
                    if (m != null) _podeApagar = (d, i) => (bool)m.Invoke(null, new object[] { d, i });
                }
                catch { }
            }
            if (_podeApagar == null) return true;
            try { return _podeApagar(doc, id); } catch { return true; }
        }

        /// <summary>Types Revit needs at least one of: same loadable family, same view family, same
        /// dimension style kind, or same system family (wall, floor, text, filled region…). Null = no rule.</summary>
        public static string GrupoMinimo(Element e)
        {
            try
            {
                switch (e)
                {
                    case FamilySymbol fs: return "fam:" + fs.Family?.Id.Value;
                    case ViewFamilyType vft: return "vft:" + (int)vft.ViewFamily;
                    case DimensionType dt: return "dim:" + (int)dt.StyleType + "|" + dt.FamilyName;
                    case ElementType t: return "t:" + t.GetType().Name + "|" + (t.Category?.Id.Value ?? 0) + "|" + t.FamilyName;
                    default: return null;
                }
            }
            catch { return null; }
        }

        private static Dictionary<string, int> ContarGrupos(Document doc)
        {
            var d = new Dictionary<string, int>();
            foreach (Element e in new FilteredElementCollector(doc).WhereElementIsElementType())
            {
                string g = GrupoMinimo(e);
                if (g != null) d[g] = d.TryGetValue(g, out int n) ? n + 1 : 1;
            }
            return d;
        }

        private static string MotivoUltimo(Element e) => e is FamilySymbol
            ? "último tipo da família — para tirá-la do projeto, apague a família em “Famílias”"
            : "último tipo deste sistema — o Revit exige pelo menos um";

        /// <summary>Marks (🔒 + reason) the listed styles that Revit would refuse to delete, so they are
        /// never offered for "Limpar" nor tried. Called after each listing and after each deletion.</summary>
        public static void MarcarNaoApagaveis(Document doc, IEnumerable<EstiloItem> itens)
        {
            Dictionary<string, int> total = null;
            foreach (EstiloItem it in itens)
            {
                it.NaoApaga = null;
                if (it.Protegido || it.Id == null || it.Id == ElementId.InvalidElementId) continue;
                if (it.Id.Value < 0) { it.NaoApaga = "elemento interno do Revit"; continue; }
                Element e = doc.GetElement(it.Id);
                if (e == null) continue;
                if (!RevitDeixaApagar(doc, it.Id)) { it.NaoApaga = "o Revit não permite apagar este elemento"; continue; }
                string g = GrupoMinimo(e);
                if (g == null) continue;
                if (total == null) total = ContarGrupos(doc);
                if (total.TryGetValue(g, out int n) && n <= 1) it.NaoApaga = MotivoUltimo(e);
            }
        }

        /// <summary>
        /// The ones that can really go: never internal/protected elements, never what Revit refuses,
        /// and never all the remaining types of a family/system (one is kept). Each one left out is
        /// reported once, with the reason — Revit is not even asked.
        /// </summary>
        private static List<EstiloItem> SoApagaveis(Document doc, IList<EstiloItem> itens, List<string> problemas)
        {
            var ok = new List<EstiloItem>();
            Dictionary<string, int> total = null;
            var saindo = new Dictionary<string, int>();
            foreach (EstiloItem it in itens)
            {
                if (it == null || it.Id == null || it.Id == ElementId.InvalidElementId) continue;
                if (it.Protegido || it.Id.Value < 0) { problemas.Add($"{it.Nome}: estilo do Revit — mantido."); continue; }
                Element e = doc.GetElement(it.Id);
                if (e == null) continue;
                if (!RevitDeixaApagar(doc, it.Id)) { problemas.Add($"{it.Nome}: o Revit não permite apagar — mantido."); continue; }
                string g = GrupoMinimo(e);
                if (g != null)
                {
                    if (total == null) total = ContarGrupos(doc);
                    int ja = saindo.TryGetValue(g, out int k) ? k : 0;
                    if (total.TryGetValue(g, out int n) && ja + 1 >= n) { problemas.Add($"{it.Nome}: {MotivoUltimo(e)} — mantido."); continue; }
                    saindo[g] = ja + 1;
                }
                ok.Add(it);
            }
            return ok;
        }

        /// <summary>Elements of these categories (null in the list = unknown: no filter, whole model).</summary>
        public static ElementFilter FiltroCategorias(IEnumerable<ElementId> categorias)
        {
            var ids = new HashSet<ElementId>();
            foreach (ElementId c in categorias)
            {
                if (c == null || c == ElementId.InvalidElementId) return null;
                ids.Add(c);
            }
            return ids.Count == 0 ? null : new ElementMulticategoryFilter(ids);
        }

        private static IEnumerable<ElementType> Of<T>(Document d) where T : ElementType =>
            new FilteredElementCollector(d).OfClass(typeof(T)).Cast<ElementType>();

        private static string Fonte(ElementType t)
        {
            try
            {
                string f = t.get_Parameter(BuiltInParameter.TEXT_FONT)?.AsString() ?? "";
                double h = t.get_Parameter(BuiltInParameter.TEXT_SIZE)?.AsDouble() ?? 0;
                return $"{f} {Math.Round(h * 304.8, 2).ToString(CultureInfo.GetCultureInfo("pt-BR"))} mm";
            }
            catch { return ""; }
        }

        private static string NomeCota(DimensionStyleType s)
        {
            switch (s)
            {
                case DimensionStyleType.Linear: return "Linear";
                case DimensionStyleType.Angular: return "Angular";
                case DimensionStyleType.Radial: return "Radial";
                case DimensionStyleType.Diameter: return "Diâmetro";
                case DimensionStyleType.ArcLength: return "Comprimento de arco";
                case DimensionStyleType.SpotElevation: return "Elevação de ponto";
                case DimensionStyleType.SpotCoordinate: return "Coordenada de ponto";
                case DimensionStyleType.SpotSlope: return "Inclinação de ponto";
                default: return s.ToString();
            }
        }

        public static string NomeTipoVista(ViewType t)
        {
            switch (t)
            {
                case ViewType.FloorPlan: return "Planta de piso";
                case ViewType.CeilingPlan: return "Planta de forro";
                case ViewType.Elevation: return "Elevação";
                case ViewType.Section: return "Corte";
                case ViewType.ThreeD: return "3D";
                case ViewType.DraftingView: return "Desenho";
                case ViewType.Schedule: return "Tabela";
                case ViewType.AreaPlan: return "Planta de área";
                case ViewType.Detail: return "Detalhe";
                default: return t.ToString();
            }
        }

        /// <summary>All categories and their subcategories (object styles).</summary>
        public static IEnumerable<Category> TodasCategorias(Document doc)
        {
            foreach (Category c in doc.Settings.Categories)
            {
                yield return c;
                CategoryNameMap subs = null;
                try { subs = c.SubCategories; } catch { }
                if (subs == null) continue;
                foreach (Category s in subs) yield return s;
            }
        }

        public static HashSet<ElementId> Filtros(View v)
        {
            try { return new HashSet<ElementId>(v.GetFilters()); } catch { return new HashSet<ElementId>(); }
        }

        /// <summary>Type parameters pointing to a source now point to the target.</summary>
        public static int TrocarReferencias(Document doc, UsoCache cache, HashSet<ElementId> src, ElementId alvo)
        {
            int n = 0;
            foreach (ElementId s in src)
            {
                if (!cache.RefsEmTipos.TryGetValue(s, out var tipos)) continue;
                foreach (ElementId tid in tipos)
                {
                    Element t = doc.GetElement(tid);
                    if (t == null) continue;
                    foreach (Parameter p in t.Parameters)
                    {
                        if (p.StorageType != StorageType.ElementId || p.IsReadOnly) continue;
                        try { if (p.AsElementId() == s) { p.Set(alvo); n++; } } catch { }
                    }
                }
            }
            return n;
        }

        /// <summary>Values of the type's own parameters (identity data and the name left out).</summary>
        public static string AssinaturaParametros(Element e)
        {
            if (e == null) return null;
            var parts = new List<string>();
            foreach (Parameter p in e.Parameters)
            {
                try
                {
                    if (p.Definition == null) continue;
                    if (p.Definition.GetGroupTypeId() == GroupTypeId.IdentityData) continue;
                    if (p.Id.Value == (long)BuiltInParameter.ALL_MODEL_TYPE_NAME || p.Id.Value == (long)BuiltInParameter.SYMBOL_NAME_PARAM) continue;
                    string v;
                    switch (p.StorageType)
                    {
                        case StorageType.Double: v = Math.Round(p.AsDouble(), 6).ToString(CultureInfo.InvariantCulture); break;
                        case StorageType.Integer: v = p.AsInteger().ToString(CultureInfo.InvariantCulture); break;
                        case StorageType.String: v = p.AsString() ?? ""; break;
                        case StorageType.ElementId: v = p.AsElementId()?.Value.ToString(CultureInfo.InvariantCulture) ?? ""; break;
                        default: continue;
                    }
                    parts.Add(p.Definition.Name + "=" + v);
                }
                catch { }
            }
            if (e is ElementType et) parts.Add("F=" + et.FamilyName);
            parts.Sort(StringComparer.Ordinal);
            return string.Join(";", parts);
        }
    }

    /// <summary>
    /// Gestor de Estilos (like Ideate StyleManager): text/dimension types, tags, line styles and
    /// patterns, fill patterns, view templates/types, filters, materials, wall/floor/... types and
    /// families: where each is used, merge/replace, purge the unused, rename in batch.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class GestorEstilosCommand : ToolCommand
    {
        public override string Title => "Gestor de Estilos";
        protected override bool PicksInView => true;

        protected override Result Run()
        {
            var window = new GestorEstilosWindow(Doc);
            if (Owner != IntPtr.Zero) new System.Windows.Interop.WindowInteropHelper(window).Owner = Owner;
            window.ShowDialog();
            EstiloIrPara ir = window.IrPara;
            if (ir == null) return Result.Succeeded;

            View vista = ir.Vista != ElementId.InvalidElementId ? Doc.GetElement(ir.Vista) as View : null;
            if (vista != null) TryOpen(vista);
            var ids = ir.Elementos.Where(id => Doc.GetElement(id) != null).ToList();
            if (ids.Count == 0) return Result.Succeeded;
            try
            {
                UiDoc.Selection.SetElementIds(ids);
                if (vista == null) UiDoc.ShowElements(ids);
            }
            catch (Exception ex) { Logger.Warn("[Gestor de Estilos] seleção: " + ex.Message); }
            return Result.Succeeded;
        }
    }
}
