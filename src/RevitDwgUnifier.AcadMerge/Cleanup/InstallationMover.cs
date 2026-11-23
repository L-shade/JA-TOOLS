using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using RevitDwgUnifier.Contracts;

namespace RevitDwgUnifier.AcadMerge.Cleanup
{
    /// <summary>
    /// Padrão JA – instalações, AutoCAD side: notes and tags of the building services go to the
    /// office layers "JA - INST_..." (colour ID and linetype of the office table), ByLayer.
    /// Recognised by (see InstallationExport on the Revit side):
    ///  * the layer of a tag category whose tags are all of one discipline (everything on it);
    ///  * the text style of a text (Revit names it after the text type: "INST_GAS 2,5mm");
    ///  * the text itself, when Revit listed it as an installation note, or as an installation
    ///    tag and the text is on a tag layer;
    ///  * a block on a tag layer whose name carries the discipline (tag exported as a block);
    ///  * the whole layer of the Lighting Fixtures category → JA - INST_ILUM (block and its lines).
    /// Nothing is exploded: the families stay blocks (see <see cref="InstallationGuard"/>).
    /// Model space, layouts and every block (views kept as blocks); xrefs untouched.
    /// </summary>
    internal static class InstallationMover
    {
        public static void Apply(Database db, MergeOptions o, MergeLog log)
        {
            List<InstallationLayer> table = o.InstallationLayerTable != null && o.InstallationLayerTable.Count > 0
                ? o.InstallationLayerTable : InstallationRules.Defaults();
            Dictionary<string, string> notes = ToMap(o.InstallationNotes);
            Dictionary<string, string> tags = ToMap(o.InstallationTags);
            List<string> tagLayers = (o.TagLayers ?? new List<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToList();
            List<InstallationLayerMove> moves = (o.InstallationLayerMoves ?? new List<InstallationLayerMove>())
                .Where(m => m != null && !string.IsNullOrWhiteSpace(m.From) && !string.IsNullOrWhiteSpace(m.To)).ToList();

            string areaLayer = string.IsNullOrWhiteSpace(o.AreaTextLayer) ? "JA - TXT_AREA_CONSTR" : o.AreaTextLayer.Trim();
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var layerIds = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
            var styleTarget = new Dictionary<ObjectId, string>();
            var blockTarget = new Dictionary<ObjectId, string>();
            var layerMove = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var onTagLayer = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var warned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId spaceId in bt)
                {
                    var space = (BlockTableRecord)tr.GetObject(spaceId, OpenMode.ForRead);
                    if (space.IsFromExternalReference || space.IsDependent) continue;
                    foreach (ObjectId id in space)
                    {
                        if (!(tr.GetObject(id, OpenMode.ForRead) is Entity e)) continue;
                        if (e is AttributeDefinition) continue;
                        string layer = e.Layer;
                        if (string.Equals(layer, areaLayer, StringComparison.OrdinalIgnoreCase)) continue;   // PD / área dos ambientes: ficam

                        // 1. the whole layer of a tag category of one discipline
                        string target = LayerMove(layer, moves, layerMove);
                        bool tagLayer = OnTagLayer(layer, tagLayers, onTagLayer);

                        if (target == null)
                        {
                            if (e is DBText t) target = TextTarget(tr, t.TextStyleId, t.TextString, tagLayer, table, notes, tags, styleTarget);
                            else if (e is MText m)
                            {
                                string plain;
                                try { plain = m.Text; } catch (Exception) { plain = m.Contents; }
                                target = TextTarget(tr, m.TextStyleId, plain, tagLayer, table, notes, tags, styleTarget);
                            }
                            else if (e is BlockReference br && !(e is Table) && tagLayer)
                                target = BlockTarget(tr, br, table, blockTarget);
                        }

                        if (target != null) Move(db, tr, e, target, table, layerIds, counts, log, warned);

                        // texts of the attributes of an insert
                        if (e is BlockReference ins && !(e is Table))
                        {
                            foreach (ObjectId aid in ins.AttributeCollection)
                            {
                                if (!(tr.GetObject(aid, OpenMode.ForRead) is AttributeReference ar)) continue;
                                string at = target ?? LayerMove(ar.Layer, moves, layerMove);
                                if (at == null)
                                {
                                    string text = ar.TextString;
                                    try { if (ar.IsMTextAttribute) text = ar.MTextAttribute.Text; } catch (Exception) { }
                                    at = TextTarget(tr, ar.TextStyleId, text, tagLayer || OnTagLayer(ar.Layer, tagLayers, onTagLayer),
                                                    table, notes, tags, styleTarget);
                                }
                                if (at != null) Move(db, tr, ar, at, table, layerIds, counts, log, warned);
                            }
                        }
                    }
                }
                tr.Commit();
            }

            if (counts.Count == 0)
                log.Stat("Padrão JA – instalações: nenhuma nota ou etiqueta de instalação encontrada (tipos de texto / etiquetas sem a disciplina no nome).");
            else
                log.Stat("Padrão JA – instalações: " + string.Join(", ", counts.OrderBy(k => k.Key).Select(k => $"{k.Value} em '{k.Key}'")) + ".");
        }

        private static Dictionary<string, string> ToMap(List<InstallationText> list)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (list == null) return map;
            foreach (InstallationText t in list)
                if (t != null && !string.IsNullOrEmpty(t.Text) && !string.IsNullOrWhiteSpace(t.Layer)) map[t.Text] = t.Layer.Trim();
            return map;
        }

        /// <summary>Same layer, or the setup's name followed by a Revit modifier ("-xxx", "_xxx", " xxx").</summary>
        internal static bool SameLayer(string layer, string setup)
        {
            if (string.IsNullOrEmpty(layer) || string.IsNullOrEmpty(setup)) return false;
            if (string.Equals(layer, setup, StringComparison.OrdinalIgnoreCase)) return true;
            return layer.Length > setup.Length && layer.StartsWith(setup, StringComparison.OrdinalIgnoreCase) && "-_ $".IndexOf(layer[setup.Length]) >= 0;
        }

        private static string LayerMove(string layer, List<InstallationLayerMove> moves, Dictionary<string, string> cache)
        {
            if (string.IsNullOrEmpty(layer) || moves.Count == 0) return null;
            if (cache.TryGetValue(layer, out string known)) return known;
            InstallationLayerMove m = moves.FirstOrDefault(x => SameLayer(layer, x.From.Trim()));
            string r = m?.To.Trim();
            if (r != null && string.Equals(r, layer, StringComparison.OrdinalIgnoreCase)) r = null;
            cache[layer] = r;
            return r;
        }

        private static bool OnTagLayer(string layer, List<string> tagLayers, Dictionary<string, bool> cache)
        {
            if (string.IsNullOrEmpty(layer) || tagLayers.Count == 0) return false;
            if (cache.TryGetValue(layer, out bool known)) return known;
            bool r = tagLayers.Any(t => SameLayer(layer, t));
            cache[layer] = r;
            return r;
        }

        private static string TextTarget(Transaction tr, ObjectId styleId, string text, bool tagLayer, List<InstallationLayer> table,
                                         Dictionary<string, string> notes, Dictionary<string, string> tags, Dictionary<ObjectId, string> styleCache)
        {
            // the text style carries the name of the Revit text type
            if (!styleId.IsNull)
            {
                if (!styleCache.TryGetValue(styleId, out string byStyle))
                {
                    string name = null;
                    try { name = (tr.GetObject(styleId, OpenMode.ForRead) as TextStyleTableRecord)?.Name; } catch (Exception) { }
                    byStyle = InstallationRules.Classify(table, name);
                    styleCache[styleId] = byStyle;
                }
                if (byStyle != null) return byStyle;
            }
            string key = InstallationRules.TextKey(text);
            if (key.Length == 0) return null;
            if (notes.TryGetValue(key, out string byNote)) return byNote;
            if (tagLayer && tags.TryGetValue(key, out string byTag)) return byTag;
            return null;
        }

        private static string BlockTarget(Transaction tr, BlockReference br, List<InstallationLayer> table, Dictionary<ObjectId, string> cache)
        {
            ObjectId defId = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
            if (cache.TryGetValue(defId, out string known)) return known;
            string r = null;
            try
            {
                var def = (BlockTableRecord)tr.GetObject(defId, OpenMode.ForRead);
                if (!def.IsLayout && !def.IsFromExternalReference) r = InstallationRules.Classify(table, def.Name);
            }
            catch (Exception) { }
            cache[defId] = r;
            return r;
        }

        private static void Move(Database db, Transaction tr, Entity e, string target, List<InstallationLayer> table,
                                 Dictionary<string, ObjectId> layerIds, Dictionary<string, int> counts, MergeLog log, HashSet<string> warned)
        {
            if (!layerIds.TryGetValue(target, out ObjectId layerId))
            {
                InstallationLayer def = table.FirstOrDefault(l => string.Equals(l?.Name?.Trim(), target, StringComparison.OrdinalIgnoreCase));
                int aci = def != null && def.Color >= 1 && def.Color <= 255 ? def.Color : 7;
                try
                {
                    layerId = JaStandards.EnsureLayer(db, tr, target, aci);
                    SetLinetype(db, tr, layerId, def?.Linetype, log, warned);
                }
                catch (Exception ex)
                {
                    if (warned.Add("layer:" + target)) log.Warn($"Padrão JA – instalações: layer '{target}' não criada ({ex.Message}).");
                    layerId = ObjectId.Null;
                }
                layerIds[target] = layerId;
            }
            if (layerId.IsNull) return;
            if (e.LayerId == layerId && e.ColorIndex == 256) return;
            try
            {
                var w = (Entity)tr.GetObject(e.ObjectId, OpenMode.ForWrite, false, true);
                w.LayerId = layerId;
                w.ColorIndex = 256;                       // ByLayer: the colour of the office table
                // linetype untouched: ByLayer ones take the table's (DIVIDE2...), hidden/dashed lines stay
                counts.TryGetValue(target, out int n);
                counts[target] = n + 1;
            }
            catch (Exception) { }
        }

        /// <summary>Linetype of the office table (DIVIDE2...), loaded from acadiso.lin / acad.lin when missing.</summary>
        private static void SetLinetype(Database db, Transaction tr, ObjectId layerId, string name, MergeLog log, HashSet<string> warned)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            name = name.Trim();
            ObjectId ltId = ObjectId.Null;
            var lt = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
            if (lt.Has(name)) ltId = lt[name];
            else
            {
                foreach (string file in new[] { "acadiso.lin", "acad.lin" })
                {
                    try
                    {
                        db.LoadLineTypeFile(name, file);
                        lt = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
                        if (lt.Has(name)) { ltId = lt[name]; break; }
                    }
                    catch (Exception) { }
                }
            }
            if (ltId.IsNull)
            {
                if (warned.Add("lt:" + name)) log.Warn($"Padrão JA – instalações: tipo de linha '{name}' não encontrado; a layer ficou com o tipo de linha atual.");
                return;
            }
            var rec = (LayerTableRecord)tr.GetObject(layerId, OpenMode.ForWrite);
            if (rec.LinetypeObjectId != ltId) rec.LinetypeObjectId = ltId;
        }
    }
}
