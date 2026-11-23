using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
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
            try
            {
                List<string> sobras = Sobras(db, textLayer);
                if (sobras.Count > 0) log.Warn("Padrão JA: textos com cara de área/PD que ficaram fora de '" + textLayer + "':\n  " + string.Join("\n  ", sobras));
            }
            catch (Exception ex) { log.Info("Padrão JA: lista de sobras não feita (" + ex.Message + ")."); }

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

        /// <summary>
        /// PD / area texts of the room tags → the area layer, wherever they are (model, layouts, inside every block —
        /// the tag blocks, the views kept as blocks — and attributes / constant attribute definitions):
        ///  • a text that is only PD and/or area ("2.59 m²", "A: 12,50 m2", "PD = 2,80") is moved;
        ///  • a multi-line MText that mixes the name with the area ("CIRC.\P2.59 m²") is split where it is: its lines
        ///    become texts in the same place, the PD/area lines on the area layer, the others on the original one;
        ///  • a number and its unit written as separate texts on one line ("2.59" + "m²", "PD" + "2,80") move together.
        /// The texts that look like an area but stayed are listed in the log (layer, kind, text) to show why.
        /// </summary>
        private static int MoveAreaTexts(Database db, string layer, int aci, out int mixed)
        {
            int n = 0;
            mixed = 0;
            var misturados = new List<ObjectId>();
            using (var tr = db.TransactionManager.StartTransaction())
            {
                ObjectId layerId = EnsureLayer(db, tr, layer, aci);
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId spaceId in bt)
                {
                    var space = (BlockTableRecord)tr.GetObject(spaceId, OpenMode.ForRead);
                    if (space.IsFromExternalReference || space.IsDependent) continue;
                    var linha = new List<DBText>();      // single-line texts of this space, for the number + unit pairs
                    foreach (ObjectId id in space)
                    {
                        DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                        if (obj is BlockReference br && !(obj is Table))
                        {
                            foreach (ObjectId aid in br.AttributeCollection)
                                if (tr.GetObject(aid, OpenMode.ForRead) is AttributeReference ar)
                                {
                                    string at = ar.TextString;
                                    try { if (ar.IsMTextAttribute) at = ar.MTextAttribute.Text; } catch (Exception) { }
                                    int dummy = 0;
                                    n += Retag(tr, ar, at, layerId, ref dummy);
                                }
                            continue;
                        }
                        if (obj is AttributeDefinition ad)
                        {
                            int dummy = 0;
                            n += Retag(tr, ad, ad.TextString, layerId, ref dummy);    // constant / preset tag texts
                            continue;
                        }
                        if (obj is DBText t)
                        {
                            int r = Retag(tr, t, t.TextString, layerId, ref mixed);
                            n += r;
                            if (r == 0 && t.LayerId != layerId) linha.Add(t);
                        }
                        else if (obj is MText m)
                        {
                            string plain;
                            try { plain = m.Text; } catch (Exception) { plain = m.Contents; }
                            int antes = mixed;
                            n += Retag(tr, m, plain, layerId, ref mixed);
                            if (mixed > antes) misturados.Add(m.ObjectId);
                        }
                    }
                    n += Pares(tr, linha, layerId);
                }
                // mixed multi-line texts: split by lines (after the walk: the blocks change)
                int separados = 0;
                foreach (ObjectId id in misturados)
                {
                    int r = Separar(tr, id, layerId);
                    if (r > 0) { n += r; separados++; }
                }
                mixed -= separados;
                tr.Commit();
            }
            return n;
        }

        /// <summary>Texts on one line (same direction and baseline, gaps under 1,5 × height) that together are PD / area.</summary>
        private static int Pares(Transaction tr, List<DBText> textos, ObjectId layerId)
        {
            if (textos.Count < 2) return 0;
            int n = 0;
            var usados = new HashSet<ObjectId>();
            foreach (DBText a in textos.OrderBy(x => x.Position.X))
            {
                if (usados.Contains(a.ObjectId) || a.Height <= 0) continue;
                Vector3d dir = new Vector3d(Math.Cos(a.Rotation), Math.Sin(a.Rotation), 0), nrm = new Vector3d(-dir.Y, dir.X, 0);
                double h = a.Height;
                var grupo = textos.Where(b => !usados.Contains(b.ObjectId) && Math.Abs(b.Rotation - a.Rotation) < 1e-3 &&
                                              Math.Abs((b.Position - a.Position).DotProduct(nrm)) < 0.6 * h &&
                                              Math.Abs((b.Position - a.Position).DotProduct(dir)) < 12 * h)
                                  .OrderBy(b => (b.Position - a.Position).DotProduct(dir)).ToList();
                if (grupo.Count < 2) continue;
                // consecutive pieces only (a gap breaks the line)
                var linha = new List<DBText> { grupo[0] };
                for (int i = 1; i < grupo.Count; i++)
                {
                    DBText ant = linha[linha.Count - 1];
                    double fim = (ant.Position - grupo[0].Position).DotProduct(dir) + Fim(ant, dir);
                    double ini = (grupo[i].Position - grupo[0].Position).DotProduct(dir);
                    if (ini - fim > 1.5 * h) break;
                    linha.Add(grupo[i]);
                }
                if (linha.Count < 2) continue;
                string junto = string.Join(" ", linha.Select(x => x.TextString));
                if (!IsAreaOrPd(junto, out _)) continue;
                int dummy = 0;
                foreach (DBText x in linha) { usados.Add(x.ObjectId); n += Retag(tr, x, "1 m2", layerId, ref dummy); }
            }
            return n;
        }

        /// <summary>Length of a text along its direction, from its position (bounds, else an estimate).</summary>
        private static double Fim(DBText t, Vector3d dir)
        {
            try
            {
                Extents3d e = t.GeometricExtents;
                double a = (e.MinPoint - Point3d.Origin).DotProduct(dir), b = (e.MaxPoint - Point3d.Origin).DotProduct(dir);
                return Math.Max(a, b) - (t.Position - Point3d.Origin).DotProduct(dir);
            }
            catch (Exception) { return (t.TextString ?? "").Length * t.Height * 0.8; }
        }

        /// <summary>
        /// A multi-line MText with the room name and the PD / area: exploded into its line texts in the same place
        /// (AutoCAD's own explode keeps position, height and style), the PD / area lines on the area layer.
        /// </summary>
        private static int Separar(Transaction tr, ObjectId id, ObjectId layerId)
        {
            try
            {
                var m = (MText)tr.GetObject(id, OpenMode.ForRead);
                var partes = new DBObjectCollection();
                m.Explode(partes);
                var textos = partes.OfType<DBText>().ToList();
                if (textos.Count < 2) { foreach (DBObject o in partes) o.Dispose(); return 0; }
                // lines: same baseline across the MText direction
                Vector3d dir = m.Direction.GetNormal(), nrm = m.Normal.CrossProduct(dir).GetNormal();
                double tol = Math.Max(1e-6, textos.Min(x => x.Height) * 0.5);
                var linhas = new List<List<DBText>>();
                foreach (DBText t in textos.OrderByDescending(x => (x.Position - m.Location).DotProduct(nrm)))
                {
                    double y = (t.Position - m.Location).DotProduct(nrm);
                    var l = linhas.FirstOrDefault(g => Math.Abs((g[0].Position - m.Location).DotProduct(nrm) - y) < tol);
                    if (l == null) linhas.Add(l = new List<DBText>());
                    l.Add(t);
                }
                var area = new HashSet<DBText>();
                foreach (var l in linhas)
                {
                    string texto = string.Join(" ", l.OrderBy(x => (x.Position - m.Location).DotProduct(dir)).Select(x => x.TextString));
                    if (IsAreaOrPd(texto, out _)) foreach (DBText t in l) area.Add(t);
                }
                if (area.Count == 0 || area.Count == textos.Count) { foreach (DBObject o in partes) o.Dispose(); return 0; }
                var dono = (BlockTableRecord)tr.GetObject(m.OwnerId, OpenMode.ForWrite);
                foreach (DBText t in textos)
                {
                    if (area.Contains(t)) { t.LayerId = layerId; t.ColorIndex = 256; }
                    dono.AppendEntity(t);
                    tr.AddNewlyCreatedDBObject(t, true);
                }
                foreach (DBObject o in partes) if (!(o is DBText)) o.Dispose();
                m.UpgradeOpen();
                m.Erase();
                return area.Count;
            }
            catch (Exception) { return 0; }
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

        /// <summary>For the log: texts that mention an area or PD but did not go to the area layer (where and what).</summary>
        internal static List<string> Sobras(Database db, string layer, int max = 8)
        {
            var r = new List<string>();
            var pista = new Regex(@"\d\s*m\s*(²|2|\\U\+00B2|%%178)|\bp\.?\s*d\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId spaceId in bt)
                {
                    var space = (BlockTableRecord)tr.GetObject(spaceId, OpenMode.ForRead);
                    if (space.IsFromExternalReference || space.IsDependent) continue;
                    foreach (ObjectId id in space)
                    {
                        if (r.Count >= max) break;
                        if (!(tr.GetObject(id, OpenMode.ForRead) is Entity e) || string.Equals(e.Layer, layer, StringComparison.OrdinalIgnoreCase)) continue;
                        string raw = e is DBText t ? t.TextString : e is MText m ? m.Contents : null;
                        if (raw == null || !pista.IsMatch(raw)) continue;
                        r.Add($"{e.GetType().Name} na layer '{e.Layer}' (bloco '{space.Name}'): «{(raw.Length > 80 ? raw.Substring(0, 80) + "…" : raw)}»");
                    }
                }
                tr.Commit();
            }
            return r;
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
