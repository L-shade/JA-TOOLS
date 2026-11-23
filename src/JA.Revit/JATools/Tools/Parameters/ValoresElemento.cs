using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using RevitDwgUnifier.Tools.Common;

namespace RevitDwgUnifier.Tools.Parameters
{
    /// <summary>
    /// What an element "is" for Preencher Parâmetros and Filtrar Elementos: its level, room (doors and windows by the
    /// side chosen), elevation, family, type, any parameter (its own, its type's, its room's, its level's, its host's)
    /// and text models with tokens. One per run (it caches the rooms).
    /// </summary>
    internal sealed class ValoresElemento
    {
        public readonly Document Doc;
        public readonly List<Level> Niveis;
        /// <summary>Without a level: the highest level below the element's base.</summary>
        public bool NivelAbaixo = true;
        /// <summary>0 = base level + base offset, 1 = lowest point, 2 = centre, 3 = highest point.</summary>
        public int ReferenciaElevacao;
        /// <summary>Doors and windows: 0 = the room it opens into, 1 = the one that is not circulation, 2 = the room it comes from.</summary>
        public int LadoPorta;

        private readonly double _baseElev;   // internal Z → elevation as the levels show it
        private readonly Dictionary<long, Room> _ambientes = new Dictionary<long, Room>();

        public static readonly CultureInfo Br = CultureInfo.GetCultureInfo("pt-BR");

        public ValoresElemento(Document doc)
        {
            Doc = doc;
            Niveis = RevitUtil.Levels(doc);
            _baseElev = Niveis.Count > 0 ? Niveis[0].Elevation - Niveis[0].ProjectElevation : 0;
        }

        // ------------------------------------------------------------------ the elements

        /// <summary>Model elements (and rooms) of a category that takes project parameters.</summary>
        public static bool Alvo(Element e) =>
            e != null && !(e is ElementType) && !(e is View) && e.Category != null && e.Category.AllowsBoundParameters &&
            (e.Category.CategoryType == CategoryType.Model || e is SpatialElement) && !e.Category.Name.StartsWith("<", StringComparison.Ordinal);

        /// <summary>Parameter names of a sample of each category (of the instances, or of their types), alphabetical.</summary>
        public List<string> NomesDeParametros(IEnumerable<Element> elementos, bool doTipo, bool soEditaveis)
        {
            var nomes = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
            foreach (var g in elementos.GroupBy(e => e.Category.Id.Value))
            {
                IEnumerable<Element> amostra = doTipo
                    ? g.Select(e => e.GetTypeId()).Where(id => id != ElementId.InvalidElementId).Distinct().Take(60).Select(id => Doc.GetElement(id)).Where(t => t != null)
                    : g.Take(150);
                foreach (Element e in amostra)
                    foreach (Parameter p in e.Parameters)
                    {
                        if (p?.Definition == null || string.IsNullOrWhiteSpace(p.Definition.Name)) continue;
                        if (soEditaveis && (p.IsReadOnly || p.StorageType == StorageType.ElementId || p.StorageType == StorageType.None)) continue;
                        nomes.Add(p.Definition.Name);
                    }
            }
            return nomes.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        // ------------------------------------------------------------------ level, elevation, room

        /// <summary>The level of the element; without one (and when asked) the highest level below its base.</summary>
        public Level Nivel(Element e)
        {
            Level l = LevelRehost.NivelDe(e);
            if (l != null || !NivelAbaixo || Niveis.Count == 0) return l;
            try
            {
                BoundingBoxXYZ bb = e.get_BoundingBox(null);
                if (bb == null) return null;
                double z = bb.Min.Z + 10 / 304.8;
                return Niveis.LastOrDefault(x => x.ProjectElevation <= z) ?? Niveis[0];
            }
            catch { return null; }
        }

        /// <summary>Elevation of the element in metres, as the levels show it (see ReferenciaElevacao). Null without geometry.</summary>
        public double? Elevacao(Element e)
        {
            try
            {
                if (ReferenciaElevacao == 0 && LevelRehost.ReferenciaDe(e, out Level l, out double desl)) return (l.Elevation + desl) * 0.3048;
                BoundingBoxXYZ bb = e.get_BoundingBox(null);
                if (bb == null) return null;
                double z = ReferenciaElevacao == 2 ? (bb.Min.Z + bb.Max.Z) / 2 : ReferenciaElevacao == 3 ? bb.Max.Z : bb.Min.Z;
                return (z + _baseElev) * 0.3048;
            }
            catch { return null; }
        }

        private static readonly string[] Circulacao = { "circ", "hall", "corredor", "escada", "elevador", "passagem", "galeria", "vestibulo", "antecamara", "patamar" };

        /// <summary>
        /// The room of the element: itself, the room of a JA rodapé, for doors and windows the side chosen, the family's
        /// room, else the room at its location. In the element's phase.
        /// </summary>
        public Room Ambiente(Element e)
        {
            if (e == null) return null;
            if (_ambientes.TryGetValue(e.Id.Value, out Room c)) return c;
            Room r = null;
            try
            {
                if (e is Room er) r = er;
                if (r == null)
                {
                    var d = JAEsquadrias.Common.JaDados.Ler(e);
                    if (d != null && d.Ambiente != 0) r = Doc.GetElement(new ElementId(d.Ambiente)) as Room;
                }
                if (r == null && e is FamilyInstance fi)
                {
                    Phase fase = Doc.GetElement(fi.CreatedPhaseId) as Phase;
                    Room para = null, de = null;
                    try { para = fase != null ? fi.get_ToRoom(fase) : fi.ToRoom; } catch { }
                    try { de = fase != null ? fi.get_FromRoom(fase) : fi.FromRoom; } catch { }
                    if (para == null && de == null && fi.Host is Wall) LadosDaParede(fi, out para, out de);
                    if (para != null || de != null) r = EscolherLado(para, de);
                    if (r == null) try { r = fase != null ? fi.get_Room(fase) : fi.Room; } catch { }
                }
                if (r == null)
                {
                    XYZ p = null;
                    if (e.Location is LocationPoint lp) p = lp.Point;
                    else if (e.Location is LocationCurve lc) p = lc.Curve.Evaluate(0.5, true);
                    BoundingBoxXYZ bb = e.get_BoundingBox(null);
                    if (p == null && bb != null) p = (bb.Min + bb.Max) / 2;
                    if (p != null) r = Doc.GetRoomAtPoint(new XYZ(p.X, p.Y, (bb != null ? bb.Min.Z : p.Z) + 1));
                }
            }
            catch { }
            _ambientes[e.Id.Value] = r;
            return r;
        }

        private Room EscolherLado(Room para, Room de)
        {
            if (para == null || de == null || para.Id == de.Id) return para ?? de;
            if (LadoPorta == 2) return de;
            if (LadoPorta == 1)
            {
                bool Circ(Room x) { string n = TextUtil.Fold(x.Name ?? ""); return Circulacao.Any(n.Contains); }
                if (Circ(para) && !Circ(de)) return de;
            }
            return para;
        }

        /// <summary>
        /// A door or window without From/To rooms (no room calculation in the family): the rooms 30 cm in front of each
        /// face of the wall; "para" on the side the door opens into (the side opposite its facing).
        /// </summary>
        private void LadosDaParede(FamilyInstance fi, out Room para, out Room de)
        {
            para = de = null;
            try
            {
                if (!(fi.Location is LocationPoint lp) || !(fi.Host is Wall w)) return;
                XYZ n = new XYZ(fi.FacingOrientation.X, fi.FacingOrientation.Y, 0);
                if (n.GetLength() < 1e-9) return;
                n = n.Normalize() * (w.Width / 2 + 300 / 304.8);
                BoundingBoxXYZ bb = fi.get_BoundingBox(null);
                XYZ p = new XYZ(lp.Point.X, lp.Point.Y, (bb != null ? bb.Min.Z : lp.Point.Z) + 1);
                de = Doc.GetRoomAtPoint(p + n);
                para = Doc.GetRoomAtPoint(p - n);
            }
            catch { }
        }

        // ------------------------------------------------------------------ values and tokens

        /// <summary>The value of a parameter as shown in Revit (text as it is; numbers, levels, types… as their display text).</summary>
        public static string Texto(Parameter p)
        {
            if (p == null || !p.HasValue) return null;
            try { return p.StorageType == StorageType.String ? p.AsString() : p.AsValueString(); } catch { return null; }
        }

        private Element Tipo(Element e) { try { return Doc.GetElement(e.GetTypeId()); } catch { return null; } }

        /// <summary>Numbering ({Seq}): element id → number, filled by the tool in the order chosen.</summary>
        public Dictionary<long, int> Sequencia;
        public int SeqDigitos = 1;

        /// <summary>The special fields (the names the user sees; accents and case do not matter).</summary>
        public static readonly string[] Especiais =
        {
            "Nível", "Nível do topo", "Ambiente", "Número do ambiente", "Elevação", "Elevação do topo", "Comprimento", "Área", "Volume",
            "Altura", "Largura", "Espessura", "Família", "Tipo", "Categoria", "Material", "Fase de criação", "Fase de demolição", "Grupo",
            "Subprojeto", "Opção de projeto", "Hospedeiro", "Função", "Estrutural", "Fixado", "Criado por", "Alterado por", "Id", "Seq"
        };

        /// <summary>The value of a parameter: text, a number (lengths in m, areas in m², volumes in m³, angles in degrees, others as
        /// shown), yes/no as true/false, elements (level, material…) by name. Null when empty.</summary>
        public static object ValorDe(Parameter p)
        {
            if (p == null || !p.HasValue) return null;
            try
            {
                switch (p.StorageType)
                {
                    case StorageType.String: return p.AsString();
                    case StorageType.ElementId: return p.AsValueString();
                    case StorageType.Integer:
                        if (p.Definition.GetDataType() == SpecTypeId.Boolean.YesNo) return p.AsInteger() == 1;
                        return (double)p.AsInteger();
                    case StorageType.Double:
                        ForgeTypeId spec = p.Definition.GetDataType();
                        double v = p.AsDouble();
                        if (spec == SpecTypeId.Length) return UnitUtils.ConvertFromInternalUnits(v, UnitTypeId.Meters);
                        if (spec == SpecTypeId.Area) return UnitUtils.ConvertFromInternalUnits(v, UnitTypeId.SquareMeters);
                        if (spec == SpecTypeId.Volume) return UnitUtils.ConvertFromInternalUnits(v, UnitTypeId.CubicMeters);
                        if (spec == SpecTypeId.Angle) return UnitUtils.ConvertFromInternalUnits(v, UnitTypeId.Degrees);
                        if (spec != null && UnitUtils.IsMeasurableSpec(spec)) return Numero(p.AsValueString()) ?? v;
                        return v;
                }
            }
            catch { }
            return Texto(p);
        }

        private static double M(double pes) => pes * 0.3048;
        private static double M2(double pes2) => UnitUtils.ConvertFromInternalUnits(pes2, UnitTypeId.SquareMeters);
        private static double M3(double pes3) => UnitUtils.ConvertFromInternalUnits(pes3, UnitTypeId.CubicMeters);

        private static double? Dbl(Element e, params BuiltInParameter[] bips)
        {
            if (e == null) return null;
            foreach (var b in bips)
                try { Parameter p = e.get_Parameter(b); if (p != null && p.HasValue && p.StorageType == StorageType.Double) return p.AsDouble(); } catch { }
            return null;
        }

        private string NomeNivelDe(Element e, params BuiltInParameter[] bips)
        {
            foreach (var b in bips)
                try { Parameter p = e.get_Parameter(b); if (p != null && p.StorageType == StorageType.ElementId && Doc.GetElement(p.AsElementId()) is Level l) return l.Name; } catch { }
            return null;
        }

        /// <summary>A special field of the element (see Especiais); false when the name is not one.</summary>
        private bool Especial(string f, Element e, out object v)
        {
            v = null;
            FamilyInstance fi = e as FamilyInstance;
            switch (f)
            {
                case "nivel": case "pavimento": v = Nivel(e)?.Name; return true;
                case "nivel do topo":
                    v = NomeNivelDe(e, BuiltInParameter.WALL_HEIGHT_TYPE, BuiltInParameter.FAMILY_TOP_LEVEL_PARAM, BuiltInParameter.STAIRS_TOP_LEVEL_PARAM, BuiltInParameter.ROOF_CONSTRAINT_LEVEL_PARAM);
                    return true;
                case "ambiente": v = Ambiente(e)?.Name; return true;
                case "numero do ambiente": case "ambiente numero": v = Ambiente(e)?.Number; return true;
                case "familia": v = fi?.Symbol?.FamilyName ?? (Tipo(e) as ElementType)?.FamilyName; return true;
                case "tipo": v = Tipo(e)?.Name; return true;
                case "categoria": v = e.Category?.Name; return true;
                case "id": v = e.Id.Value.ToString(CultureInfo.InvariantCulture); return true;
                case "nome": if (e is SpatialElement || e is Level || e is View || e is ElementType || e is Autodesk.Revit.DB.Group) { v = e.Name; return true; } return false;
                case "elevacao": case "elevacao da base": v = Elevacao(e); return true;
                case "elevacao do topo":
                    try { BoundingBoxXYZ bb = e.get_BoundingBox(null); if (bb != null) v = M(bb.Max.Z + _baseElev); } catch { }
                    return true;
                case "comprimento":
                    {
                        double? l = null;
                        try { if (e.Location is LocationCurve lc) l = lc.Curve.Length; } catch { }
                        l = l ?? Dbl(e, BuiltInParameter.CURVE_ELEM_LENGTH, BuiltInParameter.INSTANCE_LENGTH_PARAM);
                        v = l == null ? null : (object)M(l.Value);
                        return true;
                    }
                case "area":
                    {
                        double? a = e is SpatialElement se ? se.Area : Dbl(e, BuiltInParameter.HOST_AREA_COMPUTED);
                        v = a == null ? null : (object)M2(a.Value);
                        return true;
                    }
                case "volume":
                    {
                        double? a = e is Autodesk.Revit.DB.Architecture.Room rv ? rv.Volume : Dbl(e, BuiltInParameter.HOST_VOLUME_COMPUTED);
                        v = a == null ? null : (object)M3(a.Value);
                        return true;
                    }
                case "altura":
                    {
                        double? h = e is Wall ? Dbl(e, BuiltInParameter.WALL_USER_HEIGHT_PARAM)
                                  : e is Autodesk.Revit.DB.Architecture.Room rh ? rh.UnboundedHeight
                                  : Dbl(e, BuiltInParameter.FAMILY_HEIGHT_PARAM, BuiltInParameter.GENERIC_HEIGHT) ?? Dbl(Tipo(e), BuiltInParameter.FAMILY_HEIGHT_PARAM, BuiltInParameter.GENERIC_HEIGHT);
                        if (h == null) try { BoundingBoxXYZ bb = e.get_BoundingBox(null); if (bb != null) h = bb.Max.Z - bb.Min.Z; } catch { }
                        v = h == null ? null : (object)M(h.Value);
                        return true;
                    }
                case "largura":
                    {
                        double? w = e is Wall wl ? wl.Width
                                  : Dbl(e, BuiltInParameter.FAMILY_WIDTH_PARAM, BuiltInParameter.GENERIC_WIDTH) ?? Dbl(Tipo(e), BuiltInParameter.FAMILY_WIDTH_PARAM, BuiltInParameter.GENERIC_WIDTH);
                        v = w == null ? null : (object)M(w.Value);
                        return true;
                    }
                case "espessura":
                    {
                        double? t = null;
                        if (e is Wall we) t = we.Width;
                        else
                        {
                            try { if (Tipo(e) is HostObjAttributes ha) t = ha.GetCompoundStructure()?.GetWidth(); } catch { }
                            t = t ?? Dbl(e, BuiltInParameter.FLOOR_ATTR_THICKNESS_PARAM) ?? Dbl(Tipo(e), BuiltInParameter.FLOOR_ATTR_DEFAULT_THICKNESS_PARAM);
                        }
                        v = t == null ? null : (object)M(t.Value);
                        return true;
                    }
                case "material": case "materiais":
                    try
                    {
                        var nomes = e.GetMaterialIds(false).Select(mid => Doc.GetElement(mid)?.Name).Where(nm => !string.IsNullOrEmpty(nm)).Distinct().OrderBy(nm => nm).ToList();
                        v = string.Join("; ", nomes);
                    }
                    catch { }
                    return true;
                case "fase": case "fase de criacao": v = Doc.GetElement(e.CreatedPhaseId)?.Name; return true;
                case "fase de demolicao": v = Doc.GetElement(e.DemolishedPhaseId)?.Name ?? ""; return true;
                case "grupo": v = e.GroupId != ElementId.InvalidElementId ? Doc.GetElement(e.GroupId)?.Name : ""; return true;
                case "subprojeto": case "workset":
                    try { v = Doc.IsWorkshared ? Doc.GetWorksetTable().GetWorkset(e.WorksetId)?.Name : ""; } catch { }
                    return true;
                case "opcao de projeto": v = e.DesignOption?.Name ?? ""; return true;
                case "hospedeiro": v = fi?.Host?.Name ?? ""; return true;
                case "funcao":
                    v = Texto(Tipo(e)?.get_Parameter(BuiltInParameter.FUNCTION_PARAM) ?? e.get_Parameter(BuiltInParameter.FUNCTION_PARAM));
                    return true;
                case "estrutural":
                    {
                        bool? est = null;
                        try
                        {
                            Parameter p = e.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT) ?? e.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL);
                            if (p != null && p.StorageType == StorageType.Integer) est = p.AsInteger() == 1;
                            else if (fi != null) est = fi.StructuralType != Autodesk.Revit.DB.Structure.StructuralType.NonStructural;
                        }
                        catch { }
                        v = est;
                        return true;
                    }
                case "fixado": v = e.Pinned; return true;
                case "criado por": case "alterado por":
                    try
                    {
                        if (Doc.IsWorkshared)
                        {
                            WorksharingTooltipInfo ti = WorksharingUtils.GetWorksharingTooltipInfo(Doc, e.Id);
                            v = f == "criado por" ? ti.Creator : ti.LastChangedBy;
                        }
                        else v = "";
                    }
                    catch { }
                    return true;
                case "seq":
                    if (Sequencia != null && Sequencia.TryGetValue(e.Id.Value, out int numSeq)) v = (double)numSeq;
                    return true;
            }
            return false;
        }

        /// <summary>
        /// The value of a field of the element, typed (for formulas and comparisons): the special fields, any parameter of
        /// the element or of its type, and Ambiente:X, Nível:X, Tipo:X, Hospedeiro:X for the room, the level, the type or the host.
        /// </summary>
        public object Valor(string nome, Element e, int profundidade = 0)
        {
            if (e == null || string.IsNullOrWhiteSpace(nome)) return null;
            nome = nome.Trim();
            int dois = nome.IndexOf(':');
            if (dois > 0 && profundidade < 2)
            {
                string alvo = TextUtil.Fold(nome.Substring(0, dois)).Trim(), resto = nome.Substring(dois + 1).Trim();
                if (alvo == "ambiente" || alvo == "nivel" || alvo == "tipo" || alvo == "hospedeiro")
                {
                    Element rel = alvo == "ambiente" ? (Element)Ambiente(e) : alvo == "nivel" ? (Element)Nivel(e) : alvo == "tipo" ? Tipo(e) : (e as FamilyInstance)?.Host;
                    return rel == null ? null : Valor(resto, rel, profundidade + 1);
                }
            }
            if (Especial(TextUtil.Fold(nome).Trim(), e, out object v)) return v;
            return ValorDe(e.LookupParameter(nome) ?? Tipo(e)?.LookupParameter(nome));
        }

        /// <summary>
        /// A token of a text model, as text: parameters as Revit shows them (with their units), the special numbers with
        /// up to two decimals (m, m², m³), yes/no as Sim/Não, {Seq} with the digits chosen.
        /// </summary>
        public string Token(string nome, Element e, int profundidade = 0)
        {
            if (e == null || string.IsNullOrWhiteSpace(nome)) return null;
            nome = nome.Trim();
            int dois = nome.IndexOf(':');
            if (dois > 0 && profundidade < 2)
            {
                string alvo = TextUtil.Fold(nome.Substring(0, dois)).Trim(), resto = nome.Substring(dois + 1).Trim();
                if (alvo == "ambiente" || alvo == "nivel" || alvo == "tipo" || alvo == "hospedeiro")
                {
                    Element rel = alvo == "ambiente" ? (Element)Ambiente(e) : alvo == "nivel" ? (Element)Nivel(e) : alvo == "tipo" ? Tipo(e) : (e as FamilyInstance)?.Host;
                    return rel == null ? null : Token(resto, rel, profundidade + 1);
                }
            }
            string f = TextUtil.Fold(nome).Trim();
            if (Especial(f, e, out object v))
            {
                if (f == "seq" && v is double sq) return ((int)sq).ToString(new string('0', Math.Max(1, SeqDigitos)), CultureInfo.InvariantCulture);
                return v is double d ? d.ToString("0.##", Br) : Formula.ParaTexto(v);
            }
            Parameter p = e.LookupParameter(nome) ?? Tipo(e)?.LookupParameter(nome);
            if (p != null && p.StorageType == StorageType.Integer && p.HasValue && p.Definition.GetDataType() == SpecTypeId.Boolean.YesNo) return p.AsInteger() == 1 ? "Sim" : "Não";
            return Texto(p);
        }

        private static readonly Regex TokenRx = new Regex(@"\{([^{}]+)\}");

        /// <summary>The model with its tokens replaced; null when every token came out empty.</summary>
        public string Expandir(string modelo, Element e)
        {
            int tokens = 0, vazios = 0;
            string r = TokenRx.Replace(modelo ?? "", m =>
            {
                tokens++;
                string v = Token(m.Groups[1].Value, e) ?? "";
                if (v.Length == 0) vazios++;
                return v;
            });
            return tokens > 0 && vazios == tokens ? null : r.Trim();
        }

        // ------------------------------------------------------------------ numbers and ranges

        private static readonly Regex NumeroRx = new Regex(@"-?\d+(?:[.,]\d+)*");

        /// <summary>The first number in a text ("2,50 m" → 2.5; "1.250,5" → 1250.5); null without one.</summary>
        public static double? Numero(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            Match m = NumeroRx.Match(s);
            if (!m.Success) return null;
            string t = m.Value;
            int virg = t.LastIndexOf(','), ponto = t.LastIndexOf('.');
            if (virg >= 0 && ponto >= 0) t = virg > ponto ? t.Replace(".", "").Replace(',', '.') : t.Replace(",", "");
            else if (virg >= 0) t = t.Replace(',', '.');
            else if (ponto >= 0 && t.Count(ch => ch == '.') > 1) t = t.Replace(".", "");
            return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : (double?)null;
        }

        private static readonly Regex IntervaloRx = new Regex(@"^\s*(-?\d+(?:[.,]\d+)?)\s*(?:m\s*)?(?:;|–|—|-|a|até|ate)\s*(-?\d+(?:[.,]\d+)?)\s*(?:m\s*)?", RegexOptions.IgnoreCase);
        private static readonly Regex FaixaRx = new Regex(@"^\s*(-?\d+(?:[.,]\d+)?)\s*(?:m\s*)?(?:–|—|-|a|até|ate)\s*(-?\d+(?:[.,]\d+)?)\s*(?:m\s*)?(?:=|:|→|->)\s*(.+?)\s*$", RegexOptions.IgnoreCase);

        /// <summary>"10 a 13", "10;13", "10-13" → (10, 13); false when it does not read.</summary>
        public static bool Intervalo(string s, out double de, out double ate)
        {
            de = ate = 0;
            Match m = IntervaloRx.Match(s ?? "");
            if (!m.Success) return false;
            de = double.Parse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            ate = double.Parse(m.Groups[2].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            if (ate < de) { double x = de; de = ate; ate = x; }
            return true;
        }

        /// <summary>"10-13 = Primeiro pavimento; 13-16 = Segundo" → the ranges (m); null with the reason when one does not read.</summary>
        public static List<(double de, double ate, string valor)> LerFaixas(string texto, out string erro)
        {
            erro = null;
            var r = new List<(double de, double ate, string valor)>();
            foreach (string parte in (texto ?? "").Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.IsNullOrWhiteSpace(parte)) continue;
                Match m = FaixaRx.Match(parte);
                if (!m.Success) { erro = $"não entendi “{parte.Trim()}”. Use de-até = valor, ex.: 10-13 = Primeiro pavimento."; return null; }
                double de = double.Parse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
                double ate = double.Parse(m.Groups[2].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
                if (ate <= de) { erro = $"em “{parte.Trim()}” o fim precisa ser maior que o início."; return null; }
                r.Add((de, ate, m.Groups[3].Value));
            }
            if (r.Count == 0) { erro = "escreva ao menos uma faixa, ex.: 10-13 = Primeiro pavimento."; return null; }
            return r;
        }

        /// <summary>"Porta 80 = P1; Porta 90 = P2; * = Outra" → key (accents and case ignored) → value; "*" = the rest.</summary>
        public static Dictionary<string, string> LerTabela(string texto, out string resto, out string erro)
        {
            erro = null; resto = null;
            var r = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string parte in (texto ?? "").Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.IsNullOrWhiteSpace(parte)) continue;
                int i = parte.IndexOf('=');
                if (i <= 0) { erro = $"não entendi “{parte.Trim()}”. Use valor = novo, ex.: Porta 80 = P1."; return null; }
                string k = parte.Substring(0, i).Trim(), v = parte.Substring(i + 1).Trim();
                if (k == "*") resto = v;
                else r[TextUtil.Fold(k).Trim()] = v;
            }
            if (r.Count == 0 && resto == null) { erro = "escreva ao menos uma linha, ex.: Porta 80 = P1."; return null; }
            return r;
        }

        /// <summary>In [de, até) with half a millimetre of tolerance (m).</summary>
        public static bool Dentro(double z, double de, double ate) => z >= de - 0.0005 && z < ate - 0.0005;
    }
}
