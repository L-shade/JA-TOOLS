using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using RevitDwgUnifier.Contracts;
using RevitDwgUnifier.Infrastructure;

namespace RevitDwgUnifier.Export
{
    /// <summary>
    /// Padrão JA – instalações, Revit side: tells the AutoCAD step which notes and tags are of
    /// a building-services discipline and which layer each one goes to.
    ///  * Text notes: by the name of their text type ("INST_GAS 2,5mm" → JA - INST_GAS). The DWG
    ///    text is recognised by its text (and by its text style, which carries the type name).
    ///  * Tags: by the name of the tag family / type ("JA - Identificador de Gás"), else by the
    ///    tagged element (system of the pipe or duct, name of its family / type), else by the tag
    ///    category (electrical, data, fire, ducts). When every tag of a category is of the same
    ///    discipline, its whole DWG layer goes (texts, circles and leaders); otherwise each tag
    ///    text is recognised by its text.
    ///  * Luminaires: the whole layer of the Lighting Fixtures category goes to JA - INST_ILUM.
    /// Read only (no transaction). Never throws: on error the AutoCAD step just moves less.
    /// </summary>
    public static class InstallationExport
    {
        /// <summary>Tag category → a word of its discipline (when the tag and the element say nothing).</summary>
        private static readonly string[][] CategoryHints =
        {
            new[] { "LUMINARIA", "OST_LightingFixtureTags" },
            new[] { "ELETRICA", "OST_ElectricalFixtureTags", "OST_LightingDeviceTags", "OST_ElectricalEquipmentTags",
                    "OST_ConduitTags", "OST_ConduitFittingTags", "OST_CableTrayTags", "OST_CableTrayFittingTags", "OST_WireTags", "OST_ElectricalCircuitTags" },
            new[] { "DADOS", "OST_CommunicationDeviceTags", "OST_DataDeviceTags", "OST_SecurityDeviceTags", "OST_TelephoneDeviceTags", "OST_NurseCallDeviceTags" },
            new[] { "INCENDIO", "OST_SprinklerTags", "OST_FireAlarmDeviceTags" },
            new[] { "EXAUSTAO", "OST_DuctTags", "OST_DuctFittingTags", "OST_DuctAccessoryTags", "OST_DuctTerminalTags", "OST_FlexDuctTags" }
        };

        /// <summary>System parameters of pipes / ducts (parsed once; a name missing in some Revit version is skipped).</summary>
        private static readonly BuiltInParameter[] SystemParams = new[]
        {
            "RBS_PIPING_SYSTEM_TYPE_PARAM", "RBS_DUCT_SYSTEM_TYPE_PARAM", "RBS_SYSTEM_CLASSIFICATION_PARAM", "RBS_SYSTEM_NAME_PARAM"
        }.Select(n => Enum.TryParse(n, out BuiltInParameter b) ? (BuiltInParameter?)b : null).Where(b => b.HasValue).Select(b => b.Value).ToArray();

        public static void Fill(Document doc, DWGExportOptions options, MergeOptions o)
        {
            if (o == null) return;
            o.InstallationNotes = new List<InstallationText>();
            o.InstallationTags = new List<InstallationText>();
            o.TagLayers = new List<string>();
            o.InstallationLayerMoves = new List<InstallationLayerMove>();
            if (!o.InstallationLayers || doc == null) return;
            if (o.InstallationLayerTable == null || o.InstallationLayerTable.Count == 0) o.InstallationLayerTable = InstallationRules.Defaults();
            List<InstallationLayer> table = o.InstallationLayerTable;

            try { Notes(doc, table, o); }
            catch (Exception ex) { Logger.Warn("Instalações (notas): " + ex.Message); }
            try { Tags(doc, options, table, o); }
            catch (Exception ex) { Logger.Warn("Instalações (etiquetas): " + ex.Message); }
            Logger.Info($"Instalações: {o.InstallationNotes.Count} texto(s) de nota, {o.InstallationTags.Count} texto(s) de etiqueta, " +
                        $"{o.InstallationLayerMoves.Count} layer(s) de etiqueta inteira(s), {o.TagLayers.Count} layer(s) de etiqueta.");
        }

        // ------------------------------------------------------------------ notes
        private static void Notes(Document doc, List<InstallationLayer> table, MergeOptions o)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            var conflict = new HashSet<string>(StringComparer.Ordinal);
            var typeLayer = new Dictionary<long, string>();
            foreach (TextNote tn in new FilteredElementCollector(doc).OfClass(typeof(TextNote)).Cast<TextNote>())
            {
                string text;
                try { text = tn.Text; } catch { continue; }
                long typeId = tn.GetTypeId().Value;
                if (!typeLayer.TryGetValue(typeId, out string layer))
                {
                    layer = InstallationRules.Classify(table, doc.GetElement(tn.GetTypeId())?.Name);
                    typeLayer[typeId] = layer;
                }
                // the whole text and each of its lines (the DWG may split a note line by line)
                Add(map, conflict, InstallationRules.TextKey(text), layer, 2);
                foreach (string line in (text ?? "").Split('\r', '\n', '\v'))
                    Add(map, conflict, InstallationRules.TextKey(line), layer, 4);
            }
            o.InstallationNotes = Export(map, conflict);
        }

        // ------------------------------------------------------------------ tags
        private static void Tags(Document doc, DWGExportOptions options, List<InstallationLayer> table, MergeOptions o)
        {
            var hints = new Dictionary<long, string>();
            foreach (string[] h in CategoryHints)
                foreach (string name in h.Skip(1))
                    if (Enum.TryParse(name, out BuiltInCategory bic)) hints[(long)bic] = InstallationRules.Classify(table, h[0]);

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            var conflict = new HashSet<string>(StringComparer.Ordinal);
            var perCategory = new Dictionary<long, HashSet<string>>();   // category → layers of its tags ("" = none)
            var typeLayer = new Dictionary<long, string>();

            foreach (IndependentTag tag in new FilteredElementCollector(doc).OfClass(typeof(IndependentTag)).Cast<IndependentTag>())
            {
                long cat = tag.Category?.Id.Value ?? 0;
                if (cat == 0) continue;
                long typeId = tag.GetTypeId().Value;
                if (!typeLayer.TryGetValue(typeId, out string layer))
                {
                    var sym = doc.GetElement(tag.GetTypeId()) as FamilySymbol;
                    layer = InstallationRules.Classify(table, sym?.FamilyName, sym?.Name);
                    typeLayer[typeId] = layer;
                }
                if (layer == null) layer = FromElement(doc, tag, table);
                if (layer == null && hints.TryGetValue(cat, out string hint)) layer = hint;

                if (!perCategory.TryGetValue(cat, out HashSet<string> set)) perCategory[cat] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(layer ?? "");

                string text = null;
                try { text = tag.TagText; } catch { }
                Add(map, conflict, InstallationRules.TextKey(text), layer, 1);
            }
            o.InstallationTags = Export(map, conflict);

            // DWG layers of the tag categories; whole layers when all the tags of a category agree
            var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var moves = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var bad = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<long, HashSet<string>> kv in perCategory)
            {
                BuiltInCategory bic = (BuiltInCategory)(int)kv.Key;
                List<string> layers = ExportLayers.For(doc, options, bic);
                foreach (string l in layers) all.Add(l);
                string only = kv.Value.Count == 1 ? kv.Value.First() : null;
                foreach (string l in layers)
                {
                    if (string.IsNullOrEmpty(only)) { bad.Add(l); continue; }
                    if (moves.TryGetValue(l, out string prev) && !string.Equals(prev, only, StringComparison.OrdinalIgnoreCase)) bad.Add(l);
                    moves[l] = only;
                }
            }
            // luminaires: the whole family (block and lines) goes to the lighting layer
            string ilum = InstallationRules.Classify(table, "LUMINARIA");
            if (ilum != null)
            {
                foreach (string l in ExportLayers.For(doc, options, BuiltInCategory.OST_LightingFixtures))
                {
                    if (moves.TryGetValue(l, out string prev) && !string.Equals(prev, ilum, StringComparison.OrdinalIgnoreCase)) bad.Add(l);
                    moves[l] = ilum;
                }
            }

            o.TagLayers = all.ToList();
            o.InstallationLayerMoves = moves.Where(kv => !bad.Contains(kv.Key))
                .Select(kv => new InstallationLayerMove { From = kv.Key, To = kv.Value }).ToList();
        }

        /// <summary>Discipline of the tagged element: its system (pipes, ducts, fittings...) or the
        /// name of its family / type.</summary>
        private static string FromElement(Document doc, IndependentTag tag, List<InstallationLayer> table)
        {
            ICollection<ElementId> ids;
            try { ids = tag.GetTaggedLocalElementIds(); } catch { return null; }
            foreach (ElementId id in ids)
            {
                Element e = doc.GetElement(id);
                if (e == null) continue;
                var names = new List<string>();
                foreach (BuiltInParameter bip in SystemParams)
                {
                    try
                    {
                        Parameter par = e.get_Parameter(bip);
                        if (par == null || !par.HasValue) continue;
                        names.Add(par.StorageType == StorageType.String ? par.AsString() : par.AsValueString());
                    }
                    catch { }
                }
                string sys = InstallationRules.Classify(table, names.ToArray());
                if (sys != null) return sys;
                var fi = e as FamilyInstance;
                string byName = InstallationRules.Classify(table, fi?.Symbol?.FamilyName, fi?.Symbol?.Name, doc.GetElement(e.GetTypeId())?.Name);
                if (byName != null) return byName;
            }
            return null;
        }

        // ------------------------------------------------------------------ helpers
        /// <summary>A text and its layer (null = not an installation). The same text with two
        /// different layers (or once as installation, once not) is ambiguous and left alone.</summary>
        private static void Add(Dictionary<string, string> map, HashSet<string> conflict, string key, string layer, int minLength)
        {
            if (string.IsNullOrEmpty(key) || key.Length < minLength) return;
            if (map.TryGetValue(key, out string prev))
            {
                if (!string.Equals(prev, layer, StringComparison.OrdinalIgnoreCase)) conflict.Add(key);
            }
            else map[key] = layer;
        }

        private static List<InstallationText> Export(Dictionary<string, string> map, HashSet<string> conflict) =>
            map.Where(kv => kv.Value != null && !conflict.Contains(kv.Key))
               .Select(kv => new InstallationText { Text = kv.Key, Layer = kv.Value }).ToList();
    }
}
