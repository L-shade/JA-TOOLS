using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using RevitDwgUnifier.Infrastructure;
using RevitDwgUnifier.Tools.Common;

namespace RevitDwgUnifier.Tools.Views
{
    /// <summary>
    /// "Atualizar Fichas": the quantity (and, if asked, location) texts of the fichas are marked when
    /// Gerar Detalhes creates them — on the sheet or in the legend — with the type they count, what
    /// they show and the phase used. Later, one click recounts the project and rewrites those texts;
    /// nothing changes by itself.
    /// </summary>
    public static partial class LegendEngine
    {
        /// <summary>What a text of a ficha shows (kept in the text, extensible storage).</summary>
        private sealed class CountTag
        {
            public string TypeUid;
            /// <summary>"row" (a table value: Extra source=...), "info" (the inline "Rótulo: valor" lines),
            /// "relqty" (quantity of a compartment), "relcomp" (compartments), "reltotal" (TOTAL line).</summary>
            public string Kind;
            public string Extra = "";
            public ElementId Phase = ElementId.InvalidElementId;
        }

        private static readonly Guid CountSchemaId = new Guid("5d8a2f61-0b7c-4e39-a4d2-9c61e7f03b18");

        private static Schema CountSchema()
        {
            Schema s = Schema.Lookup(CountSchemaId);
            if (s != null) return s;
            var b = new SchemaBuilder(CountSchemaId);
            b.SetSchemaName("JAToolsFichaContagem");
            b.SetReadAccessLevel(AccessLevel.Public);
            b.SetWriteAccessLevel(AccessLevel.Public);
            b.SetDocumentation("Texto de quantidade / localização de uma ficha do Gerar Detalhes (JA Tools).");
            b.AddSimpleField("TypeUid", typeof(string));
            b.AddSimpleField("Kind", typeof(string));
            b.AddSimpleField("Extra", typeof(string));
            b.AddSimpleField("Phase", typeof(ElementId));
            return b.Finish();
        }

        private static CountTag Tag(LegendCandidate type, string kind, string extra) =>
            new CountTag { TypeUid = type.UniqueId, Kind = kind, Extra = extra ?? "", Phase = PhaseId ?? ElementId.InvalidElementId };

        /// <summary>Template bits a Relação quantity needs to be written again.</summary>
        private static string RelExtra(LegendTemplate t, string comp) =>
            Kv("qtyByFloor", t.QtyByFloor ? "1" : "0") + Kv("floorWord", t.FloorWord) + Kv("unit", t.UnitName) + Kv("units", t.UnitPlural) +
            Kv("compSource", t.CompSource) + Kv("compPerRoom", t.CompPerRoom ? "1" : "0") + Kv("comp", comp);

        private static string Kv(string k, string v) => k + "=" + (v ?? "").Replace("\r", " ").Replace("\n", " ") + "\n";

        private static Dictionary<string, string> ReadKv(string extra, out List<string> rows)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            rows = new List<string>();
            foreach (string line in (extra ?? "").Split('\n'))
            {
                int i = line.IndexOf('=');
                if (i <= 0) continue;
                string k = line.Substring(0, i), v = line.Substring(i + 1);
                if (k == "row") rows.Add(v); else d[k] = v;
            }
            return d;
        }

        /// <summary>Marks a text created by Gerar Detalhes (inside an open transaction).</summary>
        private static void ApplyCountTag(TextNote tn, CountTag tag)
        {
            if (tn == null || tag == null || string.IsNullOrEmpty(tag.TypeUid)) return;
            try
            {
                var e = new Entity(CountSchema());
                e.Set("TypeUid", tag.TypeUid);
                e.Set("Kind", tag.Kind ?? "");
                e.Set("Extra", tag.Extra ?? "");
                e.Set("Phase", tag.Phase ?? ElementId.InvalidElementId);
                tn.SetEntity(e);
            }
            catch (Exception ex) { Logger.Debug("[JA Tools] Marca de contagem no texto: " + ex.Message); }
        }

        private static List<TextNote> TaggedTexts(Document doc)
        {
            if (Schema.Lookup(CountSchemaId) == null) return new List<TextNote>();
            return new FilteredElementCollector(doc).OfClass(typeof(TextNote))
                .WherePasses(new ExtensibleStorageFilter(CountSchemaId)).Cast<TextNote>().ToList();
        }

        /// <summary>Texts of fichas that can be updated.</summary>
        public static int CountTaggedTexts(Document doc)
        {
            try { return TaggedTexts(doc).Count; } catch { return 0; }
        }

        /// <summary>A candidate with the counts of the current phase (as the window shows it).</summary>
        private static LegendCandidate CandidateFor(Document doc, ElementType t)
        {
            bool host = t is HostObjAttributes && !(t is WallType wt && wt.Kind == WallKind.Curtain);
            var probe = new LegendCandidate { Id = t.Id, UniqueId = t.UniqueId, CategoryId = t.Category?.Id.Value ?? 0 };
            int n = probe.CategoryId != 0 ? Instances(doc, probe).Count : 0;
            var counts = new Dictionary<ElementId, int> { [t.Id] = n };
            return Candidate(t, t.Category, t is FamilySymbol fs ? fs.FamilyName : t.FamilyName, counts, host);
        }

        /// <summary>
        /// Rewrites the marked texts with today's numbers. phase = null: the phase each ficha was made
        /// with; Invalid = all phases; otherwise that phase. Returns the texts changed.
        /// </summary>
        public static int UpdateCounts(Document doc, bool quantities, bool locations, ElementId phase, List<string> problems,
                                       out int found, out int fichas)
        {
            found = 0;
            fichas = 0;
            int changed = 0;
            Schema schema = Schema.Lookup(CountSchemaId);
            if (schema == null) return 0;
            var types = new HashSet<string>();
            ElementId before = PhaseId;
            try
            {
                foreach (TextNote tn in TaggedTexts(doc))
                {
                    Entity e;
                    try { e = tn.GetEntity(schema); } catch { continue; }
                    if (e == null || !e.IsValid()) continue;
                    found++;
                    string uid = e.Get<string>("TypeUid"), kind = e.Get<string>("Kind"), extra = e.Get<string>("Extra");
                    ElementId stored = e.Get<ElementId>("Phase");
                    types.Add(uid);

                    Dictionary<string, string> kv = ReadKv(extra, out List<string> rows);
                    kv.TryGetValue("source", out string source);
                    bool want =
                        kind == "row" ? (source == LegendSources.Quantity ? quantities : source == LegendSources.Location && locations) :
                        kind == "relqty" || kind == "reltotal" ? quantities :
                        kind == "relcomp" ? locations :
                        kind == "info" && (quantities || locations);
                    if (!want) continue;

                    var t = doc.GetElement(uid) as ElementType;
                    if (t == null || t.Category == null)
                    {
                        RevitUtil.AddOnce(problems, "Um tipo de uma ficha não existe mais no projeto: o texto ficou como estava.");
                        continue;
                    }
                    PhaseId = phase ?? stored ?? ElementId.InvalidElementId;
                    LegendCandidate type = CandidateFor(doc, t);
                    string value = Compute(doc, type, kind, kv, rows, source);
                    if (value == null) continue;
                    string now = (tn.Text ?? "").TrimEnd('\r', '\n');
                    if (string.Equals(now, value, StringComparison.Ordinal)) continue;
                    try { tn.Text = value; changed++; }
                    catch (Exception ex) { RevitUtil.AddOnce(problems, "Texto não atualizado: " + ex.Message); }
                }
            }
            finally { PhaseId = before; }
            fichas = types.Count;
            return changed;
        }

        private static string Compute(Document doc, LegendCandidate type, string kind, Dictionary<string, string> kv, List<string> rows, string source)
        {
            switch (kind)
            {
                case "row":
                    return RowValue(doc, type, source) ?? "";
                case "info":
                {
                    bool labels = kv.TryGetValue("labels", out string l) && l == "1";
                    var lines = new List<string>();
                    foreach (string r in rows)
                    {
                        int tab = r.IndexOf('\t');
                        string label = tab >= 0 ? r.Substring(0, tab) : "", src = tab >= 0 ? r.Substring(tab + 1) : r;
                        string v = RowValue(doc, type, src);
                        if (string.IsNullOrWhiteSpace(v)) continue;
                        lines.Add(labels && label.Length > 0 ? label + ": " + v : v);
                    }
                    return string.Join("\r", lines);
                }
                case "relqty":
                case "reltotal":
                case "relcomp":
                {
                    var t = new LegendTemplate
                    {
                        QtyByFloor = kv.TryGetValue("qtyByFloor", out string q) && q == "1",
                        FloorWord = kv.TryGetValue("floorWord", out string fw) ? fw : "PAV.",
                        UnitName = kv.TryGetValue("unit", out string u) ? u : "UNIDADE",
                        UnitPlural = kv.TryGetValue("units", out string us) ? us : "UNIDADES",
                        CompSource = kv.TryGetValue("compSource", out string cs) ? cs : LegendSources.Location,
                        CompPerRoom = kv.TryGetValue("compPerRoom", out string cp) && cp == "1"
                    };
                    List<RelGroup> groups = RelGroups(doc, type, t, out int total);
                    if (kind == "reltotal") return $"TOTAL: {total} {UnitFor(t, total)}";
                    if (!t.CompPerRoom) return kind == "relcomp" ? groups[0].Comp : groups[0].Qty;
                    if (kind == "relcomp") return null;                         // the compartment is the row itself
                    kv.TryGetValue("comp", out string comp);
                    RelGroup g = groups.FirstOrDefault(x => string.Equals(x.Comp, comp ?? "", StringComparison.OrdinalIgnoreCase));
                    return g != null ? g.Qty : $"0 {UnitFor(t, 0)}";            // no instance left in that compartment
                }
            }
            return null;
        }
    }
}
