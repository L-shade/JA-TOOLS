using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.DatabaseServices.Filters;
using Autodesk.AutoCAD.Geometry;
using RevitDwgUnifier.AcadMerge.Cleanup;
using RevitDwgUnifier.AcadMerge.Geometry;

namespace RevitDwgUnifier.AcadMerge
{
    /// <summary>
    /// "Model com as folhas montadas": Model Space becomes a row-by-row replica of the sheets, like
    /// a drawing set laid out on the table — every sheet with its title block and its views where
    /// they are on the paper, in sheet order, named, nothing loose. The layouts keep printing
    /// exactly as before: each viewport is re-pointed at its own view inside the replica.
    ///
    /// Scale: each replica is drawn at the REAL size of its main view (the largest viewport of the
    /// sheet): that view's geometry keeps its true size and dimension values; the title block
    /// and paper texts are enlarged by the same factor (an A4 at 1:50 becomes 10.5 x 14.85 m).
    /// Views at another scale on the same sheet (a 1:5 detail on a 1:50 sheet) are placed as a
    /// block scaled by their ratio, so their dimensions still show the original values.
    ///
    /// How: for every viewport, the model entities inside its window (minus the layers frozen in
    /// that viewport) are copied into a block "JA_&lt;sheet&gt;_V&lt;n&gt;" inserted with the
    /// model -&gt; paper -&gt; replica transform. The block is clipped to the viewport window when
    /// something crosses its edge; otherwise, at scale 1, it is exploded into plain geometry.
    /// The original scattered model content is then erased.
    /// </summary>
    internal sealed partial class MasterDrawingBuilder
    {
        private static bool IsPlan(VpSnapshot s)
        {
            try { return s.ViewDirection.Length > 1e-9 && Math.Abs(s.ViewDirection.GetNormal().Z) > 0.999; }
            catch { return false; }
        }

        /// <summary>model (WCS) -&gt; paper of a viewport (DCS = rotated by the twist, ViewCenter at CenterPoint).</summary>
        private static Matrix3d ModelToPaper(VpSnapshot s) =>
            Matrix3d.Displacement(s.CenterPoint.GetAsVector()) *
            Matrix3d.Scaling(s.CustomScale, Point3d.Origin) *
            Matrix3d.Displacement(new Vector3d(-s.ViewCenter.X, -s.ViewCenter.Y, 0)) *
            Matrix3d.Rotation(s.TwistAngle, Vector3d.ZAxis, Point3d.Origin) *
            Matrix3d.Displacement(new Vector3d(-s.ViewTarget.X, -s.ViewTarget.Y, 0));

        private void BuildModelSheets(Database db, List<string> layoutNames, List<VpSnapshot> snaps, bool writeBack)
        {
            Database previous = HostApplicationServices.WorkingDatabase;
            HostApplicationServices.WorkingDatabase = db;
            int sheets = 0, blocks = 0, exploded = 0, clipped = 0;
            try
            {
                LayoutManager lm = LayoutManager.Current;
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    ObjectId msId = bt[BlockTableRecord.ModelSpace];
                    var ms = (BlockTableRecord)tr.GetObject(msId, OpenMode.ForRead);

                    // the scattered model content as it is now (+ extents, read once)
                    var originals = new List<ObjectId>();
                    var ext = new Dictionary<ObjectId, Extents3d>();
                    var layerOf = new Dictionary<ObjectId, ObjectId>();
                    foreach (ObjectId id in ms)
                    {
                        if (id.IsErased || !(tr.GetObject(id, OpenMode.ForRead, false) is Entity e)) continue;
                        originals.Add(id);
                        layerOf[id] = e.LayerId;
                        if (ViewportMath.TryGetExtents(e, out Extents3d x)) ext[id] = x;
                    }

                    bool allPlan = snaps.All(IsPlan);
                    if (!allPlan) _log.Warn("Model com as folhas: há viewports que não são vistas de topo (3D); o conteúdo original do model foi mantido.");

                    var scales = snaps.Where(s => s.CustomScale > 1e-12).Select(s => s.CustomScale).OrderBy(v => v).ToList();
                    double defaultMain = scales.Count > 0 ? scales[scales.Count / 2] : 1.0;
                    int perRow = Math.Max(1, _o.ModelSheetsPerRow);
                    ObjectId labelLayer = JaStandards.EnsureLayer(db, tr, "JA - NOME_PRANCHA", 7);

                    double cursorX = 0, rowTop = 0, rowH = 0;
                    int inRow = 0;
                    var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (string name in layoutNames)
                    {
                        ObjectId layId = lm.GetLayoutId(name);
                        if (layId.IsNull || layId.IsErased) continue;
                        var layout = (Layout)tr.GetObject(layId, OpenMode.ForRead);
                        var paper = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                        var lsnaps = snaps.Where(s => s.Layout == name && s.CustomScale > 1e-12).ToList();
                        double main = lsnaps.Count > 0 ? lsnaps.OrderByDescending(s => s.Width * s.Height).First().CustomScale : defaultMain;

                        // paper entities (title block, texts, lines...) without viewports and their clip outlines
                        var clipIds = new HashSet<ObjectId>(lsnaps.Where(s => !s.ClipId.IsNull).Select(s => s.ClipId));
                        var paperIds = new ObjectIdCollection();
                        double px0 = double.MaxValue, py0 = double.MaxValue, px1 = double.MinValue, py1 = double.MinValue;
                        foreach (ObjectId id in paper)
                        {
                            if (id.IsErased || clipIds.Contains(id)) continue;
                            if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity e) || e is Viewport) continue;
                            paperIds.Add(id);
                            if (ViewportMath.TryGetExtents(e, out Extents3d x))
                            {
                                px0 = Math.Min(px0, x.MinPoint.X); py0 = Math.Min(py0, x.MinPoint.Y);
                                px1 = Math.Max(px1, x.MaxPoint.X); py1 = Math.Max(py1, x.MaxPoint.Y);
                            }
                        }
                        foreach (var s in lsnaps)
                        {
                            px0 = Math.Min(px0, s.CenterPoint.X - s.Width / 2); px1 = Math.Max(px1, s.CenterPoint.X + s.Width / 2);
                            py0 = Math.Min(py0, s.CenterPoint.Y - s.Height / 2); py1 = Math.Max(py1, s.CenterPoint.Y + s.Height / 2);
                        }
                        if (px0 > px1) continue;

                        double W = (px1 - px0) / main, H = (py1 - py0) / main;
                        double gap = Math.Max(W, H) * 0.08;
                        if (inRow >= perRow) { rowTop -= rowH + gap; cursorX = 0; rowH = 0; inRow = 0; }
                        var origin = new Point3d(cursorX, rowTop - H, 0);
                        Matrix3d paperToReplica = Matrix3d.Displacement(origin.GetAsVector()) *
                                                  Matrix3d.Scaling(1.0 / main, Point3d.Origin) *
                                                  Matrix3d.Displacement(new Vector3d(-px0, -py0, 0));

                        // 1. title block and paper annotation, enlarged to the real size of the main view
                        if (paperIds.Count > 0)
                        {
                            var map = new IdMapping();
                            db.DeepCloneObjects(paperIds, msId, map, false);
                            foreach (ObjectId id in paperIds)
                            {
                                if (!map.Contains(id)) continue;
                                try
                                {
                                    var copy = tr.GetObject(map[id].Value, OpenMode.ForWrite) as Entity;
                                    copy?.TransformBy(paperToReplica);
                                }
                                catch (Exception ex) { _log.Info($"Model com as folhas: item da folha {name} não copiado ({ex.Message})."); }
                            }
                        }

                        // 2. every view, copied from the scattered model into its place on the sheet
                        int n = 0;
                        foreach (var s in lsnaps)
                        {
                            n++;
                            if (!IsPlan(s)) continue;
                            Matrix3d toPaper = ModelToPaper(s);
                            Matrix3d m = paperToReplica * toPaper;
                            Matrix3d toModel = toPaper.Inverse();
                            var corners = new[]
                            {
                                new Point3d(s.CenterPoint.X - s.Width / 2, s.CenterPoint.Y - s.Height / 2, 0),
                                new Point3d(s.CenterPoint.X + s.Width / 2, s.CenterPoint.Y - s.Height / 2, 0),
                                new Point3d(s.CenterPoint.X + s.Width / 2, s.CenterPoint.Y + s.Height / 2, 0),
                                new Point3d(s.CenterPoint.X - s.Width / 2, s.CenterPoint.Y + s.Height / 2, 0)
                            }.Select(p => p.TransformBy(toModel)).ToArray();
                            double wx0 = corners.Min(p => p.X), wx1 = corners.Max(p => p.X);
                            double wy0 = corners.Min(p => p.Y), wy1 = corners.Max(p => p.Y);
                            double tol = Math.Max(wx1 - wx0, wy1 - wy0) * 1e-4;
                            var frozen = new HashSet<ObjectId>(s.Frozen);

                            var ids = new ObjectIdCollection();
                            bool inside = true;
                            foreach (ObjectId id in originals)
                            {
                                if (!ext.TryGetValue(id, out Extents3d x)) continue;
                                if (x.MaxPoint.X < wx0 || x.MinPoint.X > wx1 || x.MaxPoint.Y < wy0 || x.MinPoint.Y > wy1) continue;
                                if (frozen.Contains(layerOf[id])) continue;
                                ids.Add(id);
                                if (x.MinPoint.X < wx0 - tol || x.MaxPoint.X > wx1 + tol || x.MinPoint.Y < wy0 - tol || x.MaxPoint.Y > wy1 + tol) inside = false;
                            }

                            if (ids.Count > 0)
                            {
                                string blockName = UniqueBlockName(bt, tr, usedNames, $"JA_{name}_V{n}");
                                var def = new BlockTableRecord { Name = blockName, Origin = Point3d.Origin };
                                if (!bt.IsWriteEnabled) bt.UpgradeOpen();
                                ObjectId defId = bt.Add(def);
                                tr.AddNewlyCreatedDBObject(def, true);
                                db.DeepCloneObjects(ids, defId, new IdMapping(), false);

                                if (!ms.IsWriteEnabled) ms.UpgradeOpen();
                                var br = new BlockReference(Point3d.Origin, defId) { BlockTransform = m };
                                br.SetDatabaseDefaults(db);
                                ms.AppendEntity(br);
                                tr.AddNewlyCreatedDBObject(br, true);
                                blocks++;

                                bool unitScale = Math.Abs(s.CustomScale / main - 1) < 1e-6;
                                if (!inside)
                                {
                                    Clip(tr, br, corners);
                                    clipped++;
                                }
                                else if (unitScale)
                                {
                                    try { br.ExplodeToOwnerSpace(); br.Erase(); exploded++; }
                                    catch (Exception ex) { _log.Info($"Model com as folhas: {blockName} mantido como bloco ({ex.Message})."); }
                                }
                            }

                            // the layout now looks at the replica (same frame, same scale on paper)
                            Point3d rc = s.CenterPoint.TransformBy(paperToReplica);
                            s.ViewTarget = Point3d.Origin;
                            s.ViewDirection = Vector3d.ZAxis;
                            s.TwistAngle = 0;
                            s.ViewCenter = new Point2d(rc.X, rc.Y);
                            s.CustomScale = main;
                            s.ViewHeight = s.Height / main;
                            if (writeBack)
                            {
                                try
                                {
                                    var v = (Viewport)tr.GetObject(s.OldId, OpenMode.ForWrite);
                                    ApplyView(v, s);
                                }
                                catch (Exception ex) { _log.Info("Model com as folhas: viewport não reapontada: " + ex.Message); }
                            }
                        }

                        // 3. the sheet's name above it
                        if (!ms.IsWriteEnabled) ms.UpgradeOpen();
                        var label = new DBText
                        {
                            TextString = name,
                            Height = Math.Max(1e-6, Math.Max(W, H) * 0.02),
                            Position = new Point3d(origin.X, origin.Y + H + Math.Max(W, H) * 0.01, 0),
                            LayerId = labelLayer
                        };
                        label.SetDatabaseDefaults(db);
                        label.LayerId = labelLayer;
                        ms.AppendEntity(label);
                        tr.AddNewlyCreatedDBObject(label, true);

                        cursorX += W + gap;
                        rowH = Math.Max(rowH, H + Math.Max(W, H) * 0.05);
                        inRow++;
                        sheets++;
                    }

                    // 4. the scattered original content is no longer needed by any layout
                    if (allPlan)
                    {
                        int erased = 0;
                        foreach (ObjectId id in originals)
                        {
                            try { tr.GetObject(id, OpenMode.ForWrite, false, true).Erase(); erased++; }
                            catch { }
                        }
                        _log.Info($"Model com as folhas: {erased} entidade(s) soltas removidas do model.");
                    }
                    tr.Commit();
                }
                _log.Stat($"Model com as folhas montadas: {sheets} folha(s), {blocks} vista(s) ({exploded} em geometria simples, {clipped} recortadas).");
            }
            finally
            {
                HostApplicationServices.WorkingDatabase = previous;
            }
        }

        private static string UniqueBlockName(BlockTable bt, Transaction tr, HashSet<string> used, string wanted)
        {
            var sb = new StringBuilder();
            foreach (char c in wanted) sb.Append("<>/\\\":;?*|,=`".IndexOf(c) >= 0 || char.IsControl(c) ? '_' : c);
            string baseName = sb.ToString().Trim();
            if (baseName.Length > 200) baseName = baseName.Substring(0, 200);
            string name = baseName;
            for (int i = 2; bt.Has(name) || used.Contains(name); i++) name = baseName + "_" + i;
            used.Add(name);
            return name;
        }

        /// <summary>XCLIP of a block reference to a polygon given in the block's own coordinates.</summary>
        private void Clip(Transaction tr, BlockReference br, Point3d[] polygon)
        {
            try
            {
                var pts = new Point2dCollection();
                foreach (Point3d p in polygon) pts.Add(new Point2d(p.X, p.Y));
                var def = new SpatialFilterDefinition(pts, Vector3d.ZAxis, 0.0, 0.0, 0.0, true);
                var filter = new SpatialFilter { Definition = def };
                if (br.ExtensionDictionary.IsNull) br.CreateExtensionDictionary();
                var ext = (DBDictionary)tr.GetObject(br.ExtensionDictionary, OpenMode.ForWrite);
                const string filterDict = "ACAD_FILTER", spatial = "SPATIAL";
                DBDictionary fd;
                if (ext.Contains(filterDict)) fd = (DBDictionary)tr.GetObject(ext.GetAt(filterDict), OpenMode.ForWrite);
                else
                {
                    fd = new DBDictionary();
                    ext.SetAt(filterDict, fd);
                    tr.AddNewlyCreatedDBObject(fd, true);
                }
                if (fd.Contains(spatial)) fd.Remove(spatial);
                fd.SetAt(spatial, filter);
                tr.AddNewlyCreatedDBObject(filter, true);
            }
            catch (Exception ex) { _log.Info("Recorte (XCLIP) não aplicado: " + ex.Message); }
        }
    }
}
