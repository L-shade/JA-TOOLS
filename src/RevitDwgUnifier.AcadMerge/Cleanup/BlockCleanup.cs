using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using RevitDwgUnifier.AcadMerge.Geometry;

namespace RevitDwgUnifier.AcadMerge.Cleanup
{
    /// <summary>
    /// PITFALL #2 - CAD block explode / unnecessary nested &amp; anonymous blocks.
    ///
    ///  * BindXrefs: anything Revit still wrote as an xref (CAD links, or a setup that has
    ///    "export views on sheets and links as external references" ON) is INSERT-bound, not
    ///    BIND-bound, so layer names stay "A-WALL" instead of "Link$0$A-WALL". Unresolvable
    ///    xrefs are detached and reported instead of leaving broken references.
    ///  * RenameConflictingBlocks: Revit names blocks after family/type. Two items can contain a
    ///    block with the same name but different content (different detail level per view, or
    ///    a title block whose label text differs per sheet). A plain merge would silently keep
    ///    the first definition everywhere. Same-name blocks are compared by a content signature;
    ///    identical ones are shared (no bloat), different ones get a per-item suffix.
    ///  * ExplodeAnonymousBlocks: Revit/RealDWG wrap some geometry in "*U" anonymous blocks that
    ///    cannot be edited by name and multiply when drawings are merged. They are exploded one
    ///    level into their owner space (layers/colours preserved). Dynamic block references,
    ///    tables (*T), dimensions (*D) and hatch/group anonymous blocks are left untouched.
    ///  * Purge: removes the definitions, layers, linetypes and styles left unused afterwards.
    /// </summary>
    internal static class BlockCleanup
    {
        public static void BindXrefs(Database src, MergeLog log)
        {
            var xrefs = new List<ObjectId>();
            using (var tr = src.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(src.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId id in bt)
                {
                    var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    if (btr.IsFromExternalReference && !btr.IsDependent) xrefs.Add(id);
                }
                tr.Commit();
            }
            if (xrefs.Count == 0) return;

            src.ResolveXrefs(false, false);

            var bindable = new ObjectIdCollection();
            var unresolved = new List<ObjectId>();
            using (var tr = src.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in xrefs)
                {
                    var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    if (btr.XrefStatus == XrefStatus.Resolved) bindable.Add(id);
                    else
                    {
                        unresolved.Add(id);
                        log.Warn($"Xref '{btr.Name}' ({btr.PathName}) could not be resolved and was detached.");
                    }
                }
                tr.Commit();
            }
            foreach (ObjectId id in unresolved)
            {
                try { src.DetachXref(id); }
                catch (Autodesk.AutoCAD.Runtime.Exception ex) { log.Warn("Detach failed: " + ex.Message); }
            }
            if (bindable.Count > 0)
            {
                src.BindXrefs(bindable, true); // insertBind = true -> no "$0$" prefixes
                log.Info($"Bound {bindable.Count} xref(s) with INSERT semantics.");
            }
        }

        public static int RenameConflictingBlocks(Database src, Database master, string suffix, MergeLog log)
        {
            int renamed = 0, shared = 0;
            using (var str = src.TransactionManager.StartTransaction())
            using (var mtr = master.TransactionManager.StartTransaction())
            {
                var sbt = (BlockTable)str.GetObject(src.BlockTableId, OpenMode.ForRead);
                var mbt = (BlockTable)mtr.GetObject(master.BlockTableId, OpenMode.ForRead);

                foreach (ObjectId id in sbt)
                {
                    var btr = (BlockTableRecord)str.GetObject(id, OpenMode.ForRead);
                    if (btr.IsLayout || btr.IsAnonymous || btr.IsFromExternalReference || btr.IsDependent) continue;
                    if (btr.Name.StartsWith("*", StringComparison.Ordinal)) continue;
                    if (!mbt.Has(btr.Name)) continue;

                    var existing = (BlockTableRecord)mtr.GetObject(mbt[btr.Name], OpenMode.ForRead);
                    if (Signature(btr, str) == Signature(existing, mtr)) { shared++; continue; }

                    string baseName = btr.Name.Length > 200 ? btr.Name.Substring(0, 200) : btr.Name;
                    string candidate = baseName + suffix;
                    for (int i = 2; mbt.Has(candidate) || sbt.Has(candidate); i++)
                        candidate = baseName + suffix + "_" + i.ToString(CultureInfo.InvariantCulture);

                    btr.UpgradeOpen();
                    btr.Name = candidate;
                    renamed++;
                }
                str.Commit();
                mtr.Commit();
            }
            if (renamed + shared > 0)
                log.Info($"Block name conflicts: {renamed} renamed (different content), {shared} shared (identical).");
            return renamed;
        }

        /// <summary>Cheap but discriminating content fingerprint: entity types, layers, rounded
        /// extents and text strings (title block labels differ per sheet only by their text).</summary>
        private static string Signature(BlockTableRecord btr, Transaction tr)
        {
            var sb = new StringBuilder();
            int n = 0;
            foreach (ObjectId id in btr)
            {
                if (++n > 4000) { sb.Append("|+more"); break; }
                if (!(tr.GetObject(id, OpenMode.ForRead) is Entity e)) continue;
                sb.Append(e.GetType().Name).Append('@').Append(e.Layer);
                if (ViewportMath.TryGetExtents(e, out var x))
                    sb.Append(F(x.MinPoint.X)).Append(',').Append(F(x.MinPoint.Y)).Append(',')
                      .Append(F(x.MaxPoint.X)).Append(',').Append(F(x.MaxPoint.Y));
                if (e is DBText t) sb.Append('"').Append(t.TextString);
                else if (e is MText m) sb.Append('"').Append(m.Contents);
                else if (e is BlockReference br) sb.Append('#').Append(br.Name);
                sb.Append(';');
            }
            return n + ":" + sb;
        }

        private static string F(double v) => v.ToString("F5", CultureInfo.InvariantCulture);

        public static int ExplodeAnonymousBlocks(Database db, MergeLog log, InstallationGuard guard = null)
        {
            int total = 0, failed = 0;
            for (int pass = 0; pass < 4; pass++)
            {
                int n = 0;
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    foreach (ObjectId spaceId in bt)
                    {
                        var space = (BlockTableRecord)tr.GetObject(spaceId, OpenMode.ForRead);
                        if (!space.IsLayout) continue;

                        // Snapshot ids first: exploding appends to the same record.
                        var ids = space.Cast<ObjectId>().ToList();
                        foreach (ObjectId id in ids)
                        {
                            var br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                            if (br == null || br is Table) continue;
                            if (br.IsDynamicBlock) continue;

                            var def = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                            if (!def.IsAnonymous || def.IsLayout || def.IsFromExternalReference) continue;
                            if (!def.Name.StartsWith("*U", StringComparison.OrdinalIgnoreCase)) continue;
                            if (guard != null && guard.Layer(br.Layer)) continue;   // building-services family: stays a block

                            try
                            {
                                br.UpgradeOpen();
                                br.ExplodeToOwnerSpace();
                                br.Erase();
                                n++;
                            }
                            catch (Autodesk.AutoCAD.Runtime.Exception)
                            {
                                failed++; // e.g. non-uniformly scaled arcs: leave the block as is
                            }
                        }
                    }
                    tr.Commit();
                }
                total += n;
                if (n == 0) break;
            }
            log.Stat($"Anonymous *U blocks exploded: {total}" + (failed > 0 ? $" ({failed} left intact)" : ""));
            return total;
        }

        public static int Purge(Database db, MergeLog log)
        {
            int total = 0;
            for (int pass = 0; pass < 6; pass++)
            {
                var ids = new ObjectIdCollection();
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    AddRecords<BlockTable>(tr, db.BlockTableId, ids, r =>
                    {
                        var b = (BlockTableRecord)r;
                        return !b.IsLayout && !b.IsFromExternalReference && !b.IsDependent;
                    });
                    AddRecords<LayerTable>(tr, db.LayerTableId, ids, r =>
                        r.Name != "0" && !r.Name.Equals("Defpoints", StringComparison.OrdinalIgnoreCase) && r.ObjectId != db.Clayer);
                    AddRecords<LinetypeTable>(tr, db.LinetypeTableId, ids, r =>
                        !new[] { "BYLAYER", "BYBLOCK", "CONTINUOUS" }.Contains(r.Name.ToUpperInvariant()));
                    AddRecords<TextStyleTable>(tr, db.TextStyleTableId, ids, r =>
                        !r.Name.Equals("Standard", StringComparison.OrdinalIgnoreCase) && r.ObjectId != db.Textstyle);
                    AddRecords<DimStyleTable>(tr, db.DimStyleTableId, ids, r =>
                        !r.Name.Equals("Standard", StringComparison.OrdinalIgnoreCase) && r.ObjectId != db.Dimstyle);
                    tr.Commit();
                }

                db.Purge(ids); // leaves only the ids that are really unreferenced
                if (ids.Count == 0) break;

                using (var tr = db.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in ids)
                    {
                        try { tr.GetObject(id, OpenMode.ForWrite).Erase(); total++; }
                        catch (Autodesk.AutoCAD.Runtime.Exception) { }
                    }
                    tr.Commit();
                }
            }
            log.Stat($"Purged records: {total}");
            return total;
        }

        private static void AddRecords<T>(Transaction tr, ObjectId tableId, ObjectIdCollection ids,
                                          Func<SymbolTableRecord, bool> include) where T : SymbolTable
        {
            var table = (T)tr.GetObject(tableId, OpenMode.ForRead);
            foreach (ObjectId id in table)
            {
                var rec = (SymbolTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (include(rec)) ids.Add(id);
            }
        }
    }
}
