using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using RevitDwgUnifier.Contracts;

namespace RevitDwgUnifier.AcadMerge.Cleanup
{
    /// <summary>
    /// Padrão JA (office CAD standard), applied to the unified DWG and to the "DWG por prancha" files:
    ///  * Pilares: every block that represents a column / structural column (insert or content on
    ///    the column layers of the Revit export setup, or a name with PILAR / COLUNA / COLUMN / COLS)
    ///    is exploded where it is — model space, layouts and inside other blocks (views kept as
    ///    blocks) — nested column blocks too. Entities on layer 0 / ByBlock take the insert's
    ///    layer, colour, linetype and lineweight (like AutoCAD's EXPLODE). The column hatches go to
    ///    layer "JA - EST_PILAR_HATCH" (colour ID 6), ByLayer.
    ///  * Ambientes: the ceiling height (PD) and area (m²) texts of the room tags go to layer
    ///    "JA - TXT_AREA_CONSTR" (colour ID 22), ByLayer. The room name stays where it is.
    ///  * Instalações: notes and tags of the building services go to the "JA - INST_..." layers
    ///    (see <see cref="InstallationMover"/>).
    /// </summary>
    internal static class JaStandards
    {
        private static readonly string[] ColumnWords = { "PILAR", "COLUN", "COLUMN", "-COLS", "_COLS", " COLS" };

        public static void Apply(Database db, MergeOptions o, MergeLog log)
        {
            string hatchLayer = string.IsNullOrWhiteSpace(o.ColumnHatchLayer) ? "JA - EST_PILAR_HATCH" : o.ColumnHatchLayer.Trim();
            string textLayer = string.IsNullOrWhiteSpace(o.AreaTextLayer) ? "JA - TXT_AREA_CONSTR" : o.AreaTextLayer.Trim();
            int hatchColor = o.ColumnHatchColor >= 1 && o.ColumnHatchColor <= 255 ? o.ColumnHatchColor : 6;
            int textColor = o.AreaTextColor >= 1 && o.AreaTextColor <= 255 ? o.AreaTextColor : 22;
            var columnLayers = new List<string>((o.ColumnLayers ?? new List<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim().ToUpperInvariant()).Distinct());

            var columnDefs = new HashSet<ObjectId>();
            var newHatches = new HashSet<ObjectId>();
            var guard = new InstallationGuard(o);    // building-services and furniture families are never exploded
            int exploded = ExplodeColumns(db, columnLayers, guard, columnDefs, newHatches, out int failed);
            int hatches = MoveColumnHatches(db, columnLayers, columnDefs, newHatches, hatchLayer, hatchColor);
            int texts = MoveAreaTexts(db, textLayer, textColor, out int mixed);

            log.Stat($"Padrão JA: {exploded} bloco(s) de pilar explodido(s)" + (failed > 0 ? $" ({failed} não puderam ser explodidos)" : "") +
                     $", {hatches} hachura(s) em '{hatchLayer}', {texts} texto(s) de PD/área em '{textLayer}'.");
            if (mixed > 0)
                log.Warn($"Padrão JA: {mixed} texto(s) de ambiente misturam o nome com PD/área numa linha só e ficaram na layer original.");

            // notes and tags of the building services → "JA - INST_..." layers
            if (o.InstallationLayers)
            {
                try { InstallationMover.Apply(db, o, log); }
                catch (Exception ex) { log.Warn("Padrão JA – instalações não aplicado: " + ex.Message); }
            }
        }

        // ================================================================== columns
        internal static bool IsColumnLayer(string layer, List<string> columnLayers)
        {
            if (string.IsNullOrEmpty(layer)) return false;
            return IsSetupColumnLayer(layer, columnLayers) || HasColumnWord(layer.ToUpperInvariant());
        }

        /// <summary>A layer the Revit export setup gives to Columns / Structural Columns.</summary>
        private static bool IsSetupColumnLayer(string layer, List<string> columnLayers)
        {
            if (string.IsNullOrEmpty(layer)) return false;
            string u = layer.ToUpperInvariant();
            foreach (string c in columnLayers)
            {
                if (u == c) return true;
                // Revit layer modifiers append "-xxx", "_xxx" or " xxx" to the setup's name
                if (u.Length > c.Length && u.StartsWith(c, StringComparison.Ordinal) && "-_ $".IndexOf(u[c.Length]) >= 0) return true;
            }
            return false;
        }

        private static bool HasColumnWord(string upper) => ColumnWords.Any(w => upper.IndexOf(w, StringComparison.Ordinal) >= 0);

        private static bool IsColumnBlock(Transaction tr, BlockReference br, BlockTableRecord def, List<string> columnLayers,
                                          InstallationGuard guard, Dictionary<ObjectId, bool> cache)
        {
            if (def.IsLayout || def.IsFromExternalReference || def.IsDependent) return false;
            if (IsSetupColumnLayer(br.Layer, columnLayers)) return true;        // a real column of the export setup
            // building-services and furniture families ("Coluna AF", luminárias, mesas, armários...) stay blocks
            if (guard != null && guard.Block(br.Layer, def.Name)) return false;
            if (IsColumnLayer(br.Layer, columnLayers)) return true;
            if (cache.TryGetValue(def.ObjectId, out bool known)) return known;

            bool result = HasColumnWord(def.Name.ToUpperInvariant());
            if (!result && def.IsDynamicBlock == false)
            {
                // content: everything on the column layers (or on layer 0), at least one on a column layer
                int n = 0, onColumn = 0;
                bool other = false;
                foreach (ObjectId id in def)
                {
                    if (++n > 600) { other = true; break; }
                    if (!(tr.GetObject(id, OpenMode.ForRead) is Entity e)) continue;
                    if (e is AttributeDefinition) continue;
                    if (IsColumnLayer(e.Layer, columnLayers)) onColumn++;
                    else if (e.Layer != "0") { other = true; break; }
                }
                result = !other && onColumn > 0;
            }
            cache[def.ObjectId] = result;
            return result;
        }

        private static int ExplodeColumns(Database db, List<string> columnLayers, InstallationGuard guard, HashSet<ObjectId> columnDefs,
                                          HashSet<ObjectId> newHatches, out int failed)
        {
            int total = 0;
            failed = 0;
            var cache = new Dictionary<ObjectId, bool>();
            var gaveUp = new HashSet<ObjectId>();
            HashSet<ObjectId> protectedDefs = InstallationDefs(db, guard);
            for (int pass = 0; pass < 8; pass++)
            {
                int n = 0;
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    foreach (ObjectId spaceId in bt.Cast<ObjectId>().ToList())
                    {
                        var space = (BlockTableRecord)tr.GetObject(spaceId, OpenMode.ForRead);
                        if (space.IsFromExternalReference || space.IsDependent) continue;
                        if (protectedDefs.Contains(spaceId)) continue;   // inside a building-services family: untouched

                        foreach (ObjectId id in space.Cast<ObjectId>().ToList())
                        {
                            if (gaveUp.Contains(id)) continue;
                            var br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                            if (br == null || br is Table) continue;
                            var def = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                            if (!IsColumnBlock(tr, br, def, columnLayers, guard, cache)) continue;
                            columnDefs.Add(def.ObjectId);
                            try
                            {
                                var parts = new DBObjectCollection();
                                br.Explode(parts);
                                if (!space.IsWriteEnabled) space.UpgradeOpen();
                                foreach (DBObject part in parts)
                                {
                                    if (!(part is Entity ent) || part is AttributeDefinition) { part.Dispose(); continue; }
                                    Inherit(ent, br);
                                    ObjectId nid = space.AppendEntity(ent);
                                    tr.AddNewlyCreatedDBObject(ent, true);
                                    if (ent is Hatch) newHatches.Add(nid);
                                }
                                var w = (BlockReference)tr.GetObject(id, OpenMode.ForWrite, false, true);
                                w.Erase();
                                n++;
                            }
                            catch (Exception)
                            {
                                failed++;   // e.g. arcs in a non-uniformly scaled block: left as a block
                                gaveUp.Add(id);
                            }
                        }
                    }
                    tr.Commit();
                }
                total += n;
                if (n == 0) break;
            }
            return total;
        }

        /// <summary>Definitions of the building-services and furniture families (and the blocks nested in them).</summary>
        private static HashSet<ObjectId> InstallationDefs(Database db, InstallationGuard guard)
        {
            var defs = new HashSet<ObjectId>();
            if (guard == null) return defs;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var stack = new Stack<ObjectId>();
                foreach (ObjectId spaceId in bt)
                {
                    var space = (BlockTableRecord)tr.GetObject(spaceId, OpenMode.ForRead);
                    if (space.IsFromExternalReference || space.IsDependent) continue;
                    if (!space.IsLayout && guard.Name(space.Name) && defs.Add(spaceId)) stack.Push(spaceId);
                    foreach (ObjectId id in space)
                    {
                        if (!(tr.GetObject(id, OpenMode.ForRead) is BlockReference br) || br is Table) continue;
                        if (!guard.Layer(br.Layer)) continue;
                        if (defs.Add(br.BlockTableRecord)) stack.Push(br.BlockTableRecord);
                    }
                }
                // nested blocks of those families
                while (stack.Count > 0)
                {
                    var def = (BlockTableRecord)tr.GetObject(stack.Pop(), OpenMode.ForRead);
                    foreach (ObjectId id in def)
                        if (tr.GetObject(id, OpenMode.ForRead) is BlockReference nb && !(nb is Table) && defs.Add(nb.BlockTableRecord))
                            stack.Push(nb.BlockTableRecord);
                }
                tr.Commit();
            }
            return defs;
        }

        /// <summary>What AutoCAD's EXPLODE does: layer 0 and ByBlock properties come from the insert.</summary>
        private static void Inherit(Entity e, BlockReference br)
        {
            try
            {
                if (e.Layer == "0") e.LayerId = br.LayerId;
                if (e.Color.IsByBlock) e.Color = br.Color;
                if (string.Equals(e.Linetype, "BYBLOCK", StringComparison.OrdinalIgnoreCase)) e.LinetypeId = br.LinetypeId;
                if (e.LineWeight == LineWeight.ByBlock) e.LineWeight = br.LineWeight;
            }
            catch (Exception) { }
        }

        private static int MoveColumnHatches(Database db, List<string> columnLayers, HashSet<ObjectId> columnDefs,
                                             HashSet<ObjectId> newHatches, string layer, int aci)
        {
            int n = 0;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                ObjectId layerId = EnsureLayer(db, tr, layer, aci);
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId spaceId in bt)
                {
                    var space = (BlockTableRecord)tr.GetObject(spaceId, OpenMode.ForRead);
                    if (space.IsFromExternalReference || space.IsDependent) continue;
                    bool insideColumn = columnDefs.Contains(spaceId);
                    foreach (ObjectId id in space)
                    {
                        if (!(tr.GetObject(id, OpenMode.ForRead) is Hatch h)) continue;
                        if (!(insideColumn || newHatches.Contains(id) || IsColumnLayer(h.Layer, columnLayers))) continue;
                        if (h.LayerId == layerId && h.ColorIndex == 256) continue;
                        try
                        {
                            var w = (Hatch)tr.GetObject(id, OpenMode.ForWrite, false, true);
                            w.LayerId = layerId;
                            w.ColorIndex = 256;   // ByLayer: shows in the layer colour
                            n++;
                        }
                        catch (Exception) { }
                    }
                }
                tr.Commit();
            }
            return n;
        }

        // ================================================================== room PD / area texts
        private const string Num = @"(?:\d[\d.,]*\d|\d)";
        private static readonly Regex PdToken = new Regex(
            @"(?:p\s*\.?\s*d\s*\.?|p[ée]\s*[-\s]?\s*direito)\s*[:=]?\s*(?:\d+[.,]\d+\s*(?:m(?![a-zà-ú]))?|\d+\s*m(?![a-zà-ú])|vari[áa]vel)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex AreaToken = new Regex(
            @"(?:(?:á|a)rea|a)?\s*[:=]?\s*" + Num + @"\s*m\s*(?:²|2)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex Rest = new Regex(@"[\s|/\\,;:=\-–—()\[\].]+", RegexOptions.CultureInvariant);

        /// <summary>The whole text is ceiling height and/or area ("PD = 2,80", "A: 12,50 m²", "12.50 m2 | PD 2.70 m").</summary>
        internal static bool IsAreaOrPd(string raw, out bool partial)
        {
            partial = false;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            string s = raw.Replace("\\U+00B2", "²").Replace("\\u+00b2", "²").Replace("%%178", "²").Replace("^", "");
            if (s.Length > 120) return false;
            int hits = 0;
            s = PdToken.Replace(s, m => { hits++; return " "; });
            s = AreaToken.Replace(s, m => { hits++; return " "; });
            if (hits == 0) return false;
            if (Rest.Replace(s, "").Length == 0) return true;
            partial = true;
            return false;
        }

        private static int MoveAreaTexts(Database db, string layer, int aci, out int mixed)
        {
            int n = 0;
            mixed = 0;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                ObjectId layerId = EnsureLayer(db, tr, layer, aci);
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId spaceId in bt)
                {
                    var space = (BlockTableRecord)tr.GetObject(spaceId, OpenMode.ForRead);
                    if (space.IsFromExternalReference || space.IsDependent) continue;
                    foreach (ObjectId id in space)
                    {
                        DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                        if (obj is BlockReference br && !(obj is Table))
                        {
                            foreach (ObjectId aid in br.AttributeCollection)
                                if (tr.GetObject(aid, OpenMode.ForRead) is AttributeReference ar)
                                    n += Retag(tr, ar, ar.TextString, layerId, ref mixed);
                            continue;
                        }
                        if (obj is AttributeDefinition) continue;
                        if (obj is DBText t) n += Retag(tr, t, t.TextString, layerId, ref mixed);
                        else if (obj is MText m)
                        {
                            string plain;
                            try { plain = m.Text; } catch (Exception) { plain = m.Contents; }
                            n += Retag(tr, m, plain, layerId, ref mixed);
                        }
                    }
                }
                tr.Commit();
            }
            return n;
        }

        private static int Retag(Transaction tr, Entity e, string text, ObjectId layerId, ref int mixed)
        {
            if (!IsAreaOrPd(text, out bool partial))
            {
                if (partial) mixed++;
                return 0;
            }
            if (e.LayerId == layerId && e.ColorIndex == 256) return 0;
            try
            {
                var w = (Entity)tr.GetObject(e.ObjectId, OpenMode.ForWrite, false, true);
                w.LayerId = layerId;
                w.ColorIndex = 256;
                return 1;
            }
            catch (Exception) { return 0; }
        }

        // ================================================================== layers
        internal static ObjectId EnsureLayer(Database db, Transaction tr, string name, int aci)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            Color color = Color.FromColorIndex(ColorMethod.ByAci, (short)aci);
            if (lt.Has(name))
            {
                var rec = (LayerTableRecord)tr.GetObject(lt[name], OpenMode.ForWrite);
                rec.Color = color;
                return rec.ObjectId;
            }
            var ltr = new LayerTableRecord { Name = name, Color = color };
            lt.UpgradeOpen();
            ObjectId id = lt.Add(ltr);
            tr.AddNewlyCreatedDBObject(ltr, true);
            return id;
        }
    }
}
