using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using RevitDwgUnifier.AcadMerge.Cleanup;
using RevitDwgUnifier.Contracts;

namespace RevitDwgUnifier.AcadMerge
{
    /// <summary>
    /// Builds the single unified DWG.
    ///
    /// SINGLE-DWG LAYOUT MERGE - how it works
    /// ---------------------------------------
    /// Input: one DWG per item, each exported by Revit from a sheet (real sheets, or temporary
    /// sheets created for standalone views), i.e. each source already contains
    ///   * Model Space : the content of its views, and
    ///   * one Paper Space layout : title block + viewports at the correct scales.
    ///
    /// For every item, in manifest order:
    ///   1. Read the source DWG into a side database (no document, no UI).
    ///   2. Bind leftover xrefs (insert-bind)                                   [pitfall 2]
    ///   3. Analyse its layouts: viewports -> model-space windows               [pitfall 5]
    ///   4. Cull model entities outside every window                            [pitfall 5]
    ///   5. Compute translation T:
    ///        Tiled           -> next free tile in a grid (rows of N) near 0,0  [pitfall 1]
    ///        TrueCoordinates -> T = 0
    ///   6. Rename block definitions that clash by name but differ by content   [pitfall 2]
    ///   7. master.Insert(Displacement(T), source)  -> model content lands in master Model Space
    ///   8. For each source layout: new master layout, clone paper space, ViewTarget += T
    ///      so every viewport still frames exactly what it framed in Revit       [pitfall 1]
    /// Then, once, on the whole drawing:
    ///   explode *U blocks, normalise hatches, repair text styles, remove exact duplicates,
    ///   purge, set PSLTSCALE/LTSCALE/MEASUREMENT, store traceability data, save.
    ///
    /// Each item is isolated in try/catch: a corrupt source is reported in result.json and the
    /// remaining items are still merged.
    /// </summary>
    internal sealed partial class MasterDrawingBuilder
    {
        private readonly MergeManifest _m;
        private readonly MergeOptions _o;
        private readonly MergeLog _log;
        private readonly MergeResult _result;

        private sealed class Placement
        {
            public MergeItem Item;
            public Vector3d Translation;
            public List<string> Layouts = new List<string>();
        }

        public MasterDrawingBuilder(MergeManifest manifest, MergeLog log, MergeResult result)
        {
            _m = manifest;
            _o = manifest.Options ?? new MergeOptions();
            _log = log;
            _result = result;
        }

        public void Build()
        {
            if (_m.Items == null || _m.Items.Count == 0) throw new InvalidOperationException("Manifest has no items.");

            // Build inside the drawing Core Console has open (a real document), not in a side
            // database: only a document can make layouts current, and AutoCAD turns a viewport ON
            // only in the current layout. Viewports cloned into a never-activated layout of a side
            // database stay OFF and show an empty frame when the DWG is opened.
            var doc = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
            bool ownsDatabase = doc == null;
            Database master = ownsDatabase ? new Database(true, true) : doc.Database;
            if (ownsDatabase) _log.Warn("No active drawing: building in a side database; viewports may need MVIEW > ON.");
            try
            {
                var defaultLayouts = ExistingLayoutNames(master);
                var cloner = new LayoutCloner(master, _o, _log);
                var tiler = new Tiler(_o);
                var placements = new List<Placement>();
                bool first = true;
                int index = 0;

                foreach (MergeItem item in _m.Items)
                {
                    index++;
                    string marker = MergeProgress.Start + item.Key;
                    MergeProgress.Mark(_m.ProgressPath, marker);
                    try
                    {
                        placements.Add(MergeItem(master, item, index, cloner, tiler, ref first));
                    }
                    catch (System.Exception ex)
                    {
                        _log.Error($"Item '{item.LayoutName}' ({item.Key}) could not be merged", ex);
                        _result.FailedItems.Add(item.LayoutName);
                    }
                    MergeProgress.Mark(_m.ProgressPath, MergeProgress.Done + marker);
                }

                if (placements.Count == 0) throw new InvalidOperationException("No item could be merged.");

                var createdLayouts = placements.SelectMany(p => p.Layouts).ToList();

                // Paper space in its own unit (e.g. mm while model is in m); viewports keep Revit's scale.
                double paperFactor = Geometry.PaperUnits.Factor(master, _o.PaperUnits, _m.Coordinates?.DwgUnits, _log);
                if (Math.Abs(paperFactor - 1.0) > 1e-12)
                {
                    Geometry.PaperUnits.ScaleLayouts(master, createdLayouts.Where(n => !cloner.AutoLayouts.Contains(n)), paperFactor, _log);
                    Geometry.PaperUnits.ScaleLinetypes(master, paperFactor, _log);
                }

                // Read every viewport exactly as cloned, before anything can re-zoom it.
                var snaps = _o.ActivateViewports ? SnapshotViewports(master, createdLayouts) : null;
                if (snaps != null && _o.UniformViewportSize) HarmonizeViewportSizes(master, snaps);

                // Model Space laid out like the sheets (title block + views), the layouts re-pointed to it.
                if (_o.ModelSheets)
                {
                    var sheetSnaps = snaps ?? SnapshotViewports(master, createdLayouts);
                    Step("modelsheets", true, () => BuildModelSheets(master, createdLayouts, sheetSnaps, writeBack: snaps == null));
                }

                // Viewports copied with WblockCloneObjects are not reliable once saved (they can be
                // dropped from the layout, leaving only their clip polyline). Every model viewport
                // is therefore re-created as a brand new Viewport, with the layout current. This
                // runs while the template "Layout1" still exists, so every sheet - including the
                // first one - is activated as a normal layout and gets its own paper viewport.
                if (_o.ActivateViewports) RebuildViewports(master, createdLayouts, snaps, makeCurrent: !ownsDatabase);

                // Template layouts last: deleting the layout that owns *Paper_Space hands it to the
                // first sheet (A101), which can re-zoom that sheet's viewports -> restore them.
                if (_o.DeleteDefaultLayouts)
                {
                    DeleteLayouts(master, defaultLayouts);
                    RestoreViews(master, snaps, "after deleting the template layouts");
                }
                VerifyViewports(master, createdLayouts, cloner.ExpectedViewportHits);

                PostProcess(master);
                if (_o.AddOriginMarkers) AddOriginMarkers(master, placements);
                WriteTraceability(master, placements);
                ConfigureHeader(master);
                // the Model tab with the dashes of the sheets (annotation scale + MSLTSCALE; LTSCALE stays 1)
                Step("ltmodel", true, () => Geometry.PaperUnits.ModelComoPapel(master, paperFactor, createdLayouts, _log));

                RestoreViews(master, snaps, "before saving");
                try
                {
                    _result.ImageCount = Images.ImageReferences.Count(master);
                    if (_result.ImageCount > 0) Images.ImageReferences.LoadAll(master, _log);
                    if (_result.ImageCount > 0) _log.Stat($"Raster images in the drawing: {_result.ImageCount}");
                }
                catch (System.Exception ex) { _log.Info("Image count failed: " + ex.Message); }
                var beforeSave = CountModelViewports(master, createdLayouts);
                _log.Stat("Before save, model viewports per layout: " + Describe(beforeSave));

                string output = _m.OutputDwgPath;
                if (File.Exists(output)) File.Delete(output);
                MergeProgress.Mark(_m.ProgressPath, MergeProgress.Step + "save");
                master.SaveAs(output, MapVersion(_o.DwgVersion));
                MergeProgress.Mark(_m.ProgressPath, MergeProgress.Done + MergeProgress.Step + "save");

                VerifySavedFile(output, createdLayouts, beforeSave, snaps);

                _result.Success = true;
                _result.OutputDwgPath = output;
                _result.LayoutsCreated.AddRange(placements.SelectMany(p => p.Layouts));
                _log.Info($"Saved {output}: {_result.LayoutsCreated.Count} layout(s), {_result.FailedItems.Count} failed item(s).");
            }
            finally
            {
                if (ownsDatabase) master.Dispose();
            }
        }

        /// <summary>
        /// Re-creates every model viewport of the created layouts as a NEW Viewport entity (the
        /// equivalent of drawing it again with MVIEW), copying size, position, view, scale, twist,
        /// layer, lock state and per-viewport frozen layers. The cloned original is erased.
        ///
        /// Why: viewports brought in by WblockCloneObjects from Revit's sheet DWG were present and
        /// ON during the merge, yet missing from the saved drawing (only the pink clip polyline was
        /// left). A viewport created in place, while its layout is current, is registered with the
        /// layout exactly like one drawn by hand.
        ///
        /// Non-rectangular clips: the clip polyline is re-attached only when it is closed and lies
        /// over the viewport; otherwise the viewport stays rectangular (same box Revit exported)
        /// and the polyline is kept as plain linework.
        /// </summary>
        /// <summary>Everything that defines a model viewport, read BEFORE any layout is made
        /// current: the first activation of a layout can re-zoom its viewports (seen on the first
        /// sheet, A101: scale 1:50 became ~1:9800 looking at empty space).</summary>
        private sealed class VpSnapshot
        {
            public string Layout;
            public ObjectId OldId, NewId;
            public Point3d CenterPoint;
            public double Width, Height, ViewHeight, CustomScale, TwistAngle;
            public Vector3d ViewDirection;
            public Point3d ViewTarget;
            public Point2d ViewCenter;
            public ObjectId LayerId;
            public Autodesk.AutoCAD.Colors.Color Color;
            public string Linetype;
            public bool Locked;
            public ObjectId ClipId;
            public List<ObjectId> Frozen = new List<ObjectId>();
            public double OrigWidth, OrigHeight;
        }

        /// <summary>
        /// "Both viewports the same size": Revit often exports one viewport of a sheet with a crop
        /// far larger than its drawing (e.g. the lower window/door of an A4 page covering the whole
        /// sheet). On every sheet with several viewports, each viewport much larger than the
        /// smallest one is shrunk to the smallest one's size, placed around its own content
        /// (aligned with the reference viewport's column when the content allows it).
        ///
        /// The view itself never moves: the frame changes, and ViewCenter is shifted by the same
        /// amount / scale, so every line stays exactly where it was on the paper and the scale is
        /// untouched. Only the snapshot is edited; the rebuild then creates the viewports with
        /// these values. Viewports with a polygonal clip, rotated views and viewports lying
        /// outside the title block are left alone.
        /// </summary>
        private void HarmonizeViewportSizes(Database db, List<VpSnapshot> snaps)
        {
            int changed = 0;
            try
            {
                var model = new List<Extents3d>();
                var sheets = new Dictionary<string, Extents3d>(StringComparer.OrdinalIgnoreCase);
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    foreach (ObjectId id in ms)
                        if (tr.GetObject(id, OpenMode.ForRead) is Entity e && Geometry.ViewportMath.TryGetExtents(e, out var x)) model.Add(x);

                    var lm = LayoutManager.Current;
                    Database prev = HostApplicationServices.WorkingDatabase;
                    HostApplicationServices.WorkingDatabase = db;
                    try
                    {
                        foreach (string name in snaps.Select(s => s.Layout).Distinct())
                        {
                            var lay = (Layout)tr.GetObject(lm.GetLayoutId(name), OpenMode.ForRead);
                            var btr = (BlockTableRecord)tr.GetObject(lay.BlockTableRecordId, OpenMode.ForRead);
                            Extents3d? sheet = null;
                            foreach (ObjectId id in btr)
                            {
                                if (!(tr.GetObject(id, OpenMode.ForRead) is Entity pe) || pe is Viewport) continue;
                                if (!Geometry.ViewportMath.TryGetExtents(pe, out var pex)) continue;
                                if (sheet.HasValue) { var u = sheet.Value; u.AddExtents(pex); sheet = u; } else sheet = pex;
                            }
                            if (sheet.HasValue) sheets[name] = sheet.Value;
                        }
                    }
                    finally { HostApplicationServices.WorkingDatabase = prev; }
                    tr.Commit();
                }

                foreach (var g in snaps.GroupBy(s => s.Layout))
                {
                    if (!sheets.TryGetValue(g.Key, out Extents3d sheet)) continue;
                    double tol = Math.Max(sheet.MaxPoint.X - sheet.MinPoint.X, sheet.MaxPoint.Y - sheet.MinPoint.Y) * 0.01;
                    var candidates = g.Where(s => IsPlainPlanView(s) &&
                        s.CenterPoint.X - s.Width / 2 >= sheet.MinPoint.X - tol && s.CenterPoint.X + s.Width / 2 <= sheet.MaxPoint.X + tol &&
                        s.CenterPoint.Y - s.Height / 2 >= sheet.MinPoint.Y - tol && s.CenterPoint.Y + s.Height / 2 <= sheet.MaxPoint.Y + tol).ToList();
                    if (candidates.Count < 2) continue;

                    VpSnapshot reference = candidates.OrderBy(s => s.Width * s.Height).First();
                    foreach (var sn in candidates)
                    {
                        if (sn == reference) continue;
                        if (sn.Width <= reference.Width * 1.25 && sn.Height <= reference.Height * 1.25) continue;
                        if (Shrink(sn, reference, model))
                        {
                            changed++;
                            _log.Info($"Layout '{g.Key}': viewport resized from {sn.OrigWidth:0.###}x{sn.OrigHeight:0.###} to " +
                                      $"{sn.Width:0.###}x{sn.Height:0.###} (like the reference {reference.Width:0.###}x{reference.Height:0.###}).");
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                _log.Warn("Viewport size harmonisation skipped: " + ex.Message);
            }
            if (changed > 0) _log.Stat($"Viewports resized to the sheet's common size: {changed}");
        }

        private static bool IsPlainPlanView(VpSnapshot s) =>
            s.ClipId.IsNull && Math.Abs(s.TwistAngle) < 1e-9 && s.CustomScale > 1e-12 &&
            s.ViewDirection.GetNormal().IsEqualTo(Vector3d.ZAxis, new Tolerance(1e-6, 1e-6));

        /// <summary>Paper point of a model point for an untwisted top view:
        /// P = CenterPoint + scale * (M - ViewTarget - ViewCenter).</summary>
        private static bool Shrink(VpSnapshot sn, VpSnapshot reference, List<Extents3d> model)
        {
            double k = sn.CustomScale;
            double wx0 = sn.ViewTarget.X + sn.ViewCenter.X - sn.Width / 2 / k, wx1 = wx0 + sn.Width / k;
            double wy0 = sn.ViewTarget.Y + sn.ViewCenter.Y - sn.Height / 2 / k, wy1 = wy0 + sn.Height / k;

            // Content = model entities lying entirely inside the window (long lines crossing the
            // whole window - grids, reference lines - would otherwise define the box).
            double cx0 = double.MaxValue, cy0 = double.MaxValue, cx1 = double.MinValue, cy1 = double.MinValue;
            foreach (var e in model)
            {
                if (e.MinPoint.X < wx0 || e.MaxPoint.X > wx1 || e.MinPoint.Y < wy0 || e.MaxPoint.Y > wy1) continue;
                cx0 = Math.Min(cx0, e.MinPoint.X); cy0 = Math.Min(cy0, e.MinPoint.Y);
                cx1 = Math.Max(cx1, e.MaxPoint.X); cy1 = Math.Max(cy1, e.MaxPoint.Y);
            }
            if (cx0 > cx1) return false;

            double ox = sn.CenterPoint.X, oy = sn.CenterPoint.Y;
            double px0 = ox + k * (cx0 - sn.ViewTarget.X - sn.ViewCenter.X), px1 = ox + k * (cx1 - sn.ViewTarget.X - sn.ViewCenter.X);
            double py0 = oy + k * (cy0 - sn.ViewTarget.Y - sn.ViewCenter.Y), py1 = oy + k * (cy1 - sn.ViewTarget.Y - sn.ViewCenter.Y);
            double margin = Math.Max(reference.Width, reference.Height) * 0.02;

            double w = Math.Min(sn.Width, Math.Max(reference.Width, px1 - px0 + 2 * margin));
            double h = Math.Min(sn.Height, Math.Max(reference.Height, py1 - py0 + 2 * margin));

            // Same column as the reference when the content fits in it, else centred on the content.
            double rx0 = reference.CenterPoint.X - reference.Width / 2, rx1 = reference.CenterPoint.X + reference.Width / 2;
            double cx = (px0 - margin >= rx0 && px1 + margin <= rx1 && Math.Abs(w - reference.Width) < 1e-9)
                ? reference.CenterPoint.X : (px0 + px1) / 2;
            double cy = (py0 + py1) / 2;

            // Stay inside the original frame: nothing that Revit cropped away may appear.
            cx = Math.Max(ox - sn.Width / 2 + w / 2, Math.Min(ox + sn.Width / 2 - w / 2, cx));
            cy = Math.Max(oy - sn.Height / 2 + h / 2, Math.Min(oy + sn.Height / 2 - h / 2, cy));

            sn.OrigWidth = sn.Width; sn.OrigHeight = sn.Height;
            sn.ViewCenter = new Point2d(sn.ViewCenter.X + (cx - ox) / k, sn.ViewCenter.Y + (cy - oy) / k);
            sn.CenterPoint = new Point3d(cx, cy, sn.CenterPoint.Z);
            sn.Width = w;
            sn.Height = h;
            sn.ViewHeight = h / k;
            return true;
        }

        /// <summary>Snapshot of every model viewport, taken right after cloning, before the
        /// template layouts are deleted and before any layout is activated.</summary>
        private List<VpSnapshot> SnapshotViewports(Database db, List<string> layoutNames)
        {
            var snaps = new List<VpSnapshot>();
            Database previous = HostApplicationServices.WorkingDatabase;
            HostApplicationServices.WorkingDatabase = db;
            try
            {
                LayoutManager lm = LayoutManager.Current;
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    foreach (string name in layoutNames)
                    {
                        ObjectId layId = lm.GetLayoutId(name);
                        if (layId.IsNull || layId.IsErased) continue;
                        var layout = (Layout)tr.GetObject(layId, OpenMode.ForRead);
                        var btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                        ObjectId overall = OverallViewportId(tr, layout, btr);
                        foreach (ObjectId id in btr)
                        {
                            if (id.IsErased || id == overall) continue;
                            if (!(tr.GetObject(id, OpenMode.ForRead, false) is Viewport v)) continue;
                            if (overall.IsNull && LooksLikePaperViewport(v)) continue;
                            var sn = new VpSnapshot
                            {
                                Layout = name, OldId = id,
                                CenterPoint = v.CenterPoint, Width = v.Width, Height = v.Height,
                                ViewHeight = v.ViewHeight, CustomScale = v.CustomScale, TwistAngle = v.TwistAngle,
                                ViewDirection = v.ViewDirection, ViewTarget = v.ViewTarget, ViewCenter = v.ViewCenter,
                                LayerId = v.LayerId, Color = v.Color, Linetype = v.Linetype, Locked = v.Locked,
                                ClipId = v.NonRectClipOn ? v.NonRectClipEntityId : ObjectId.Null
                            };
                            ObjectIdCollection fr = v.GetFrozenLayers();
                            if (fr != null)
                                sn.Frozen.AddRange(fr.Cast<ObjectId>().Where(l => !l.IsNull && !l.IsErased && l.Database == db));
                            snaps.Add(sn);
                        }
                    }
                    tr.Commit();
                }
            }
            catch (System.Exception ex)
            {
                _log.Warn("Viewport snapshot failed: " + ex.Message);
            }
            finally
            {
                HostApplicationServices.WorkingDatabase = previous;
            }
            return snaps;
        }

        private void RebuildViewports(Database db, List<string> layoutNames, List<VpSnapshot> snaps, bool makeCurrent)
        {
            MergeProgress.Mark(_m.ProgressPath, MergeProgress.Step + "activate");
            int rebuilt = 0, clipped = 0, failed = 0, restored = 0;
            Database previous = HostApplicationServices.WorkingDatabase;
            HostApplicationServices.WorkingDatabase = db;
            LayoutManager lm = LayoutManager.Current;
            try
            {
                // ---- Per layout: make it current, replace each viewport by a new one.
                foreach (string name in layoutNames)
                {
                    string stage = "make layout current";
                    try
                    {
                        if (makeCurrent)
                        {
                            try { lm.CurrentLayout = name; }
                            catch (System.Exception ex) { _log.Info($"Layout '{name}' could not be made current ({ex.Message}); rebuilding anyway."); }
                        }

                        stage = "rebuild viewports";
                        using (var tr = db.TransactionManager.StartTransaction())
                        {
                            var layout = (Layout)tr.GetObject(lm.GetLayoutId(name), OpenMode.ForRead);
                            var btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForWrite);

                            foreach (var sn in snaps.Where(x => x.Layout == name))
                            {
                                if (sn.OldId.IsErased) continue;
                                var nv = new Viewport();
                                nv.SetDatabaseDefaults(db);
                                btr.AppendEntity(nv);
                                tr.AddNewlyCreatedDBObject(nv, true);
                                sn.NewId = nv.ObjectId;

                                nv.LayerId = sn.LayerId;
                                nv.Color = sn.Color;
                                nv.Linetype = sn.Linetype;
                                ApplyView(nv, sn);
                                nv.FrontClipOn = false;
                                nv.BackClipOn = false;
                                if (sn.Frozen.Count > 0) nv.FreezeLayersInViewport(sn.Frozen.GetEnumerator());

                                // Clip polyline: detach it from the old viewport and erase the old one
                                // FIRST (a clip boundary is associative: erasing one side of the pair can
                                // take the other with it), then attach it to the new viewport if valid.
                                var old = (Viewport)tr.GetObject(sn.OldId, OpenMode.ForWrite);
                                if (old.NonRectClipOn) old.NonRectClipOn = false;
                                old.Erase();
                                if (IsUsableClip(tr, db, sn.ClipId, nv))
                                {
                                    try
                                    {
                                        nv.NonRectClipEntityId = sn.ClipId;
                                        nv.NonRectClipOn = true;
                                        clipped++;
                                    }
                                    catch (Autodesk.AutoCAD.Runtime.Exception ex)
                                    {
                                        _log.Info($"Layout '{name}': clip polyline not re-attached ({ex.Message}); viewport kept rectangular.");
                                    }
                                }

                                nv.Visible = true;
                                try { nv.On = true; } catch (Autodesk.AutoCAD.Runtime.Exception) { }
                                nv.Locked = sn.Locked;
                                rebuilt++;
                            }
                            tr.Commit();
                        }
                    }
                    catch (System.Exception ex)
                    {
                        failed++;
                        _log.Warn($"Layout '{name}': viewports could not be rebuilt at '{stage}' ({ex.Message}).");
                        _log.Info(ex.ToString());
                    }
                }
                if (makeCurrent) { try { lm.CurrentLayout = "Model"; } catch { } }

                // ---- Back in Model: put every view back exactly as Revit exported it, in case a
                // layout activation re-zoomed a viewport after it was rebuilt.
                restored = RestoreViews(db, snaps, "after activation");
            }
            finally
            {
                HostApplicationServices.WorkingDatabase = previous;
            }
            MergeProgress.Mark(_m.ProgressPath, MergeProgress.Done + MergeProgress.Step + "activate");
            _log.Stat($"Viewports rebuilt: {rebuilt} ({clipped} with polygonal clip, {restored} view(s) restored)" +
                      (failed > 0 ? $", {failed} layout(s) failed" : ""));
        }

        /// <summary>
        /// Puts every rebuilt viewport back to the view it had in the Revit sheet (scale, centre,
        /// target, twist) if anything changed it. Called after activation, after the template
        /// layouts are deleted (that swaps the first sheet into *Paper_Space, which is what
        /// re-zoomed A101) and right before saving. Also reports a model viewport that AutoCAD
        /// took as the sheet's own paper viewport.
        /// </summary>
        private int RestoreViews(Database db, List<VpSnapshot> snaps, string when)
        {
            if (snaps == null || snaps.Count == 0) return 0;
            int restored = 0;
            try
            {
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    foreach (var sn in snaps)
                    {
                        if (sn.NewId.IsNull || sn.NewId.IsErased) continue;
                        var nv = (Viewport)tr.GetObject(sn.NewId, OpenMode.ForRead);
                        if (SameView(nv, sn)) continue;
                        _log.Info($"Layout '{sn.Layout}': viewport view drifted {when} (scale {nv.CustomScale:0.######} -> {sn.CustomScale:0.######}); restored.");
                        nv.UpgradeOpen();
                        bool wasLocked = nv.Locked;
                        nv.Locked = false;
                        ApplyView(nv, sn);
                        try { nv.On = true; } catch (Autodesk.AutoCAD.Runtime.Exception) { }
                        nv.Locked = wasLocked;
                        restored++;
                    }

                    var ours = new HashSet<ObjectId>(snaps.Where(x => !x.NewId.IsNull).Select(x => x.NewId));
                    var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                    foreach (DBDictionaryEntry e in dict)
                    {
                        var lay = (Layout)tr.GetObject(e.Value, OpenMode.ForRead);
                        if (lay.ModelType) continue;
                        ObjectIdCollection v = lay.GetViewports();
                        if (v != null && v.Count > 0 && ours.Contains(v[0]))
                            _log.Warn($"Layout '{e.Key}': a model viewport is being used as the sheet's paper viewport ({when}).");
                    }
                    tr.Commit();
                }
            }
            catch (System.Exception ex)
            {
                _log.Warn($"View restore {when} failed: " + ex.Message);
            }
            if (restored > 0) _log.Stat($"Viewport views restored {when}: {restored}");
            return restored;
        }

        /// <summary>Frame on paper + what it looks at. Order matters: CustomScale last (it sets ViewHeight).</summary>
        private static void ApplyView(Viewport v, VpSnapshot s)
        {
            v.CenterPoint = s.CenterPoint;
            v.Width = s.Width;
            v.Height = s.Height;
            v.ViewDirection = s.ViewDirection;
            v.ViewTarget = s.ViewTarget;
            v.TwistAngle = s.TwistAngle;
            v.ViewCenter = s.ViewCenter;
            v.ViewHeight = s.ViewHeight;
            v.CustomScale = s.CustomScale;
        }

        private static bool SameView(Viewport v, VpSnapshot s)
        {
            double tol = Math.Max(s.ViewHeight, 1e-9) * 1e-6;
            return Math.Abs(v.CustomScale - s.CustomScale) <= Math.Abs(s.CustomScale) * 1e-6 &&
                   v.ViewCenter.GetDistanceTo(s.ViewCenter) <= tol &&
                   v.ViewTarget.DistanceTo(s.ViewTarget) <= tol &&
                   v.CenterPoint.DistanceTo(s.CenterPoint) <= Math.Max(s.Width, s.Height) * 1e-6 &&
                   Math.Abs(v.TwistAngle - s.TwistAngle) < 1e-9;
        }

        /// <summary>The layout's own paper-space viewport (never a model viewport).</summary>
        private static ObjectId OverallViewportId(Transaction tr, Layout layout, BlockTableRecord btr)
        {
            ObjectIdCollection lvps = layout.GetViewports();
            if (lvps != null && lvps.Count > 0) return lvps[0];
            // Not yet initialised: the overall viewport is the one with Number 1, if any.
            foreach (ObjectId id in btr)
                if (!id.IsErased && tr.GetObject(id, OpenMode.ForRead, false) is Viewport vp && vp.Number == 1) return id;
            return ObjectId.Null;
        }

        /// <summary>The overall paper viewport of a layout that was never made current: 1:1 and
        /// looking at its own position.</summary>
        private static bool LooksLikePaperViewport(Viewport vp) =>
            vp.Number == 1 ||
            (Math.Abs(vp.CustomScale - 1.0) < 1e-9 &&
             new Point2d(vp.CenterPoint.X, vp.CenterPoint.Y).GetDistanceTo(vp.ViewCenter) < 1e-6);

        /// <summary>Closed curve, same drawing, not erased, and over the viewport box.</summary>
        private static bool IsUsableClip(Transaction tr, Database db, ObjectId clipId, Viewport vp)
        {
            if (clipId.IsNull || clipId.IsErased || clipId.Database != db) return false;
            if (!(tr.GetObject(clipId, OpenMode.ForRead, false) is Curve c) || !c.Closed) return false;
            if (!Geometry.ViewportMath.TryGetExtents(c, out var cx)) return false;
            double hw = vp.Width / 2, hh = vp.Height / 2, tol = Math.Max(vp.Width, vp.Height) * 0.02;
            return cx.MinPoint.X >= vp.CenterPoint.X - hw - tol && cx.MaxPoint.X <= vp.CenterPoint.X + hw + tol &&
                   cx.MinPoint.Y >= vp.CenterPoint.Y - hh - tol && cx.MaxPoint.Y <= vp.CenterPoint.Y + hh + tol &&
                   (cx.MaxPoint.X - cx.MinPoint.X) > hw * 0.2 && (cx.MaxPoint.Y - cx.MinPoint.Y) > hh * 0.2;
        }

        /// <summary>Model viewports (overall paper viewport excluded) per layout.</summary>
        private static Dictionary<string, int> CountModelViewports(Database db, IEnumerable<string> layoutNames)
        {
            var wanted = new HashSet<string>(layoutNames, StringComparer.OrdinalIgnoreCase);
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry entry in dict)
                {
                    if (!wanted.Contains(entry.Key)) continue;
                    var layout = (Layout)tr.GetObject(entry.Value, OpenMode.ForRead);
                    var btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                    ObjectId overall = OverallViewportId(tr, layout, btr);
                    int n = 0;
                    foreach (ObjectId id in btr)
                    {
                        if (id.IsErased || id == overall) continue;
                        if (tr.GetObject(id, OpenMode.ForRead, false) is Viewport vp)
                        {
                            // Saved, never-opened layouts have no overall viewport id; skip a 1:1
                            // viewport that frames paper space itself.
                            if (overall.IsNull && LooksLikePaperViewport(vp)) continue;
                            n++;
                        }
                    }
                    counts[entry.Key] = n;
                }
                tr.Commit();
            }
            foreach (string w in wanted) if (!counts.ContainsKey(w)) counts[w] = -1; // layout missing
            return counts;
        }

        private static Dictionary<string, List<double>> ScalesByLayout(Database db, IEnumerable<string> layoutNames)
        {
            var wanted = new HashSet<string>(layoutNames, StringComparer.OrdinalIgnoreCase);
            var r = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry entry in dict)
                {
                    if (!wanted.Contains(entry.Key)) continue;
                    var layout = (Layout)tr.GetObject(entry.Value, OpenMode.ForRead);
                    var btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                    ObjectId overall = OverallViewportId(tr, layout, btr);
                    var list = new List<double>();
                    foreach (ObjectId id in btr)
                    {
                        if (id.IsErased || id == overall) continue;
                        if (tr.GetObject(id, OpenMode.ForRead, false) is Viewport vp && !(overall.IsNull && LooksLikePaperViewport(vp)))
                            list.Add(Math.Round(vp.CustomScale, 6));
                    }
                    r[entry.Key] = list;
                }
                tr.Commit();
            }
            return r;
        }

        private static string Describe(Dictionary<string, int> counts) =>
            string.Join(", ", counts.Select(kv => kv.Key + "=" + (kv.Value < 0 ? "MISSING" : kv.Value.ToString())));

        /// <summary>
        /// Re-reads the DWG that was just written (side database, no UI) and checks that every
        /// layout still has its viewport(s). This is the definitive answer to "is the viewport in
        /// the file?", independent of what the in-memory drawing looked like.
        /// </summary>
        private void VerifySavedFile(string path, List<string> layoutNames, Dictionary<string, int> beforeSave, List<VpSnapshot> snaps)
        {
            try
            {
                using (var check = new Database(false, true))
                {
                    check.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, null);
                    check.CloseInput(true);
                    var after = CountModelViewports(check, layoutNames);
                    _log.Stat("Saved file, model viewports per layout: " + Describe(after));
                    var lost = after.Where(kv => beforeSave.TryGetValue(kv.Key, out int b) && b > 0 && kv.Value < b).Select(kv => kv.Key).ToList();
                    var none = after.Where(kv => kv.Value <= 0).Select(kv => kv.Key).ToList();
                    if (lost.Count > 0) _log.Warn("Viewports LOST while saving in: " + string.Join(", ", lost));
                    if (none.Count > 0) _log.Warn("Layouts WITHOUT a model viewport in the saved file: " + string.Join(", ", none));
                    if (lost.Count == 0 && none.Count == 0) _log.Info("Saved file check: every layout has its viewport(s).");

                    // Scales in the file vs. the Revit sheet: catches a viewport re-zoomed on save.
                    if (snaps != null && snaps.Count > 0)
                    {
                        var saved = ScalesByLayout(check, layoutNames);
                        foreach (var g in snaps.GroupBy(x => x.Layout))
                        {
                            var want = g.Select(x => Math.Round(x.CustomScale, 6)).OrderBy(x => x).ToList();
                            saved.TryGetValue(g.Key, out var got);
                            got = (got ?? new List<double>()).OrderBy(x => x).ToList();
                            if (!want.SequenceEqual(got))
                                _log.Warn($"Saved file, layout '{g.Key}': viewport scales {string.Join("/", got)} differ from Revit {string.Join("/", want)}.");
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                _log.Warn("Saved file check skipped: " + ex.Message);
            }
        }

        /// <summary>
        /// Diagnostic: for every layout viewport, counts the model-space entities inside the window
        /// it looks at. A viewport looking at empty model space is reported by name, which tells
        /// "viewport is off" apart from "viewport aims at the wrong place" without opening the DWG.
        /// </summary>
        private void VerifyViewports(Database db, List<string> layoutNames, Dictionary<string, int> expected)
        {
            try
            {
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    // Model entities with their layer, so "present" can be told apart from "visible".
                    var model = new List<Tuple<Extents3d, ObjectId>>();
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    foreach (ObjectId id in ms)
                        if (tr.GetObject(id, OpenMode.ForRead) is Entity e && Geometry.ViewportMath.TryGetExtents(e, out var x))
                            model.Add(Tuple.Create(x, e.LayerId));

                    var hiddenLayers = new HashSet<ObjectId>();
                    var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    foreach (ObjectId lid in lt)
                    {
                        var l = (LayerTableRecord)tr.GetObject(lid, OpenMode.ForRead);
                        if (l.IsFrozen || l.IsOff) hiddenLayers.Add(lid);
                    }

                    var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                    var wanted = new HashSet<string>(layoutNames, StringComparer.OrdinalIgnoreCase);
                    int empty = 0, total = 0, repaired = 0;
                    foreach (DBDictionaryEntry entry in dict)
                    {
                        if (!wanted.Contains(entry.Key)) continue;
                        var layout = (Layout)tr.GetObject(entry.Value, OpenMode.ForRead);
                        var btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                        ObjectIdCollection lvps = layout.GetViewports();
                        ObjectId overall = lvps != null && lvps.Count > 0 ? lvps[0] : ObjectId.Null;

                        // Paper extents of everything except viewports (title block, notes).
                        Extents3d? sheet = null;
                        foreach (ObjectId id in btr)
                        {
                            if (!(tr.GetObject(id, OpenMode.ForRead) is Entity pe) || pe is Viewport) continue;
                            if (!Geometry.ViewportMath.TryGetExtents(pe, out var pex)) continue;
                            if (sheet.HasValue) { var u = sheet.Value; u.AddExtents(pex); sheet = u; } else sheet = pex;
                        }

                        foreach (ObjectId id in btr)
                        {
                            if (id == overall) continue;
                            if (!(tr.GetObject(id, OpenMode.ForRead) is Viewport vp)) continue;
                            if (overall.IsNull && Math.Abs(vp.CustomScale - 1.0) < 1e-9 &&
                                new Point2d(vp.CenterPoint.X, vp.CenterPoint.Y).GetDistanceTo(vp.ViewCenter) < 1e-6) continue;
                            total++;

                            // --- self-repair of what makes a correct viewport invisible
                            var vpLayer = (LayerTableRecord)tr.GetObject(vp.LayerId, OpenMode.ForRead);
                            string vpLayerState = (vpLayer.IsFrozen ? "frozen " : "") + (vpLayer.IsOff ? "off " : "");
                            if (vpLayer.IsFrozen || vpLayer.IsOff)
                            {
                                try
                                {
                                    vpLayer.UpgradeOpen();
                                    if (vpLayer.IsFrozen && db.Clayer != vpLayer.ObjectId) vpLayer.IsFrozen = false;
                                    vpLayer.IsOff = false;
                                    repaired++;
                                }
                                catch (Autodesk.AutoCAD.Runtime.Exception) { }
                            }
                            if (!vp.Visible) { vp.UpgradeOpen(); vp.Visible = true; repaired++; }

                            // Clipping that hides everything: a non-rectangular clip whose boundary
                            // is missing / from another database / not over the viewport, or front/back
                            // clipping planes (meaningless for Revit's 2D views, fatal if Z differs).
                            string clipInfo = "clip=none";
                            if (vp.NonRectClipOn)
                            {
                                ObjectId cid = vp.NonRectClipEntityId;
                                string problem = null;
                                if (cid.IsNull) problem = "boundary missing";
                                else if (cid.Database != db) problem = "boundary belongs to another drawing";
                                else if (cid.IsErased) problem = "boundary erased";
                                else
                                {
                                    var ce = tr.GetObject(cid, OpenMode.ForRead) as Entity;
                                    if (ce == null || !Geometry.ViewportMath.TryGetExtents(ce, out var cx)) problem = "boundary has no geometry";
                                    else
                                    {
                                        double hw = vp.Width / 2, hh = vp.Height / 2;
                                        bool overlaps = cx.MinPoint.X <= vp.CenterPoint.X + hw && cx.MaxPoint.X >= vp.CenterPoint.X - hw &&
                                                        cx.MinPoint.Y <= vp.CenterPoint.Y + hh && cx.MaxPoint.Y >= vp.CenterPoint.Y - hh;
                                        if (!overlaps) problem = "boundary is not over the viewport";
                                        clipInfo = $"clip=polygon ({cx.MinPoint.X:0.###},{cx.MinPoint.Y:0.###})-({cx.MaxPoint.X:0.###},{cx.MaxPoint.Y:0.###})";
                                    }
                                }
                                if (problem != null)
                                {
                                    vp.UpgradeOpen();
                                    vp.NonRectClipOn = false;
                                    repaired++;
                                    clipInfo = "clip=REMOVED (" + problem + ")";
                                    _log.Warn($"Layout '{entry.Key}': viewport clip {problem}; clipping switched off so the view is visible.");
                                }
                            }
                            if (Geometry.ViewportMath.IsTopView(vp) && (vp.FrontClipOn || vp.BackClipOn))
                            {
                                clipInfo += $" front/back clip was on (front {vp.FrontClipDistance:0.###}, back {vp.BackClipDistance:0.###}) -> off";
                                if (!vp.IsWriteEnabled) vp.UpgradeOpen();
                                vp.FrontClipOn = false;
                                vp.BackClipOn = false;
                                repaired++;
                            }

                            ObjectIdCollection vpFrozen = vp.GetFrozenLayers();
                            var vpFrozenSet = new HashSet<ObjectId>(vpFrozen.Cast<ObjectId>());

                            string where = sheet.HasValue
                                ? $"vp centre ({vp.CenterPoint.X:0.###},{vp.CenterPoint.Y:0.###}) size {vp.Width:0.###}x{vp.Height:0.###}, " +
                                  $"sheet ({sheet.Value.MinPoint.X:0.###},{sheet.Value.MinPoint.Y:0.###})-({sheet.Value.MaxPoint.X:0.###},{sheet.Value.MaxPoint.Y:0.###})"
                                : "no title block found";
                            _log.Stat($"Layout '{entry.Key}': viewport on layer '{vpLayer.Name}' {vpLayerState}" +
                                      $"on={vp.On} visible={vp.Visible} scale={vp.CustomScale:0.######} vpFrozenLayers={vpFrozen.Count} {clipInfo}; {where}.");

                            if (!Geometry.ViewportMath.IsTopView(vp)) continue;
                            Extents3d? w = Geometry.ViewportMath.ModelWindow(vp, 0);
                            if (!w.HasValue) continue;

                            var inWindow = model.Where(m => Geometry.ViewportMath.IntersectsXY(m.Item1, w.Value)).ToList();
                            int hits = inWindow.Count;
                            int globallyHidden = inWindow.Count(m => hiddenLayers.Contains(m.Item2));
                            int vpHidden = inWindow.Count(m => !hiddenLayers.Contains(m.Item2) && vpFrozenSet.Contains(m.Item2));
                            int visible = hits - globallyHidden - vpHidden;

                            string src = expected != null && expected.TryGetValue(entry.Key, out int exp) ? $" (source sheet showed {exp})" : "";
                            _log.Stat($"Layout '{entry.Key}': viewport covers {hits} model entities{src}, {visible} visible " +
                                      $"({globallyHidden} on frozen/off layers, {vpHidden} frozen in this viewport).");
                            if (hits == 0)
                            {
                                empty++;
                                _log.Warn($"Layout '{entry.Key}': viewport looks at an EMPTY model area.");
                            }
                            else if (visible == 0)
                            {
                                _log.Warn($"Layout '{entry.Key}': all {hits} entities in the viewport are on hidden layers.");
                            }
                        }
                    }
                    tr.Commit();
                    _log.Stat($"Viewport check: {total} viewport(s), {empty} looking at empty model space, " +
                              $"{repaired} visibility repair(s), {model.Count} model entities.");
                }
            }
            catch (System.Exception ex)
            {
                _log.Warn("Viewport check failed: " + ex.Message);
            }
        }

        private Placement MergeItem(Database master, MergeItem item, int index, LayoutCloner cloner,
                                    Tiler tiler, ref bool first)
        {
            if (!File.Exists(item.SourceDwgPath)) throw new FileNotFoundException("Source DWG missing", item.SourceDwgPath);

            using (var src = new Database(false, true))
            {
                src.ReadDwgFile(item.SourceDwgPath, FileOpenMode.OpenForReadAndAllShare, true, "");
                src.CloseInput(true);

                if (first)
                {
                    master.Insunits = src.Insunits;
                    master.Measurement = src.Measurement;
                    first = false;
                }
                else if (src.Insunits != master.Insunits)
                {
                    _log.Warn($"'{item.LayoutName}' uses {src.Insunits} but the drawing uses {master.Insunits}.");
                }

                if (_o.BindXrefs) BlockCleanup.BindXrefs(src, _log);
                // Logos / pictures: absolute paths + copy next to the final DWG (before cloning).
                Images.ImageReferences.Relocate(src, item.SourceDwgPath, _m.ImageFolder, item.Key, _log);

                SourceAnalysis analysis = SourceAnalyzer.Analyze(src, _o.CullMargin);
                if (_o.CullOutsideViewports) CropCuller.Cull(src, analysis, item.Key, _log);

                // Tile size = what the viewports show (robust against any remaining stray entity);
                // fall back to model extents when the item has no model viewports.
                Extents3d? tileExtents = analysis.WindowUnion;
                if (!tileExtents.HasValue)
                {
                    using (var tr = src.TransactionManager.StartTransaction())
                    {
                        tileExtents = SourceAnalyzer.ModelSpaceExtents(src, tr);
                        tr.Commit();
                    }
                }

                Vector3d t = new Vector3d(0, 0, 0);
                if (_o.Arrangement != ModelSpaceArrangement.TrueCoordinates && tileExtents.HasValue)
                    t = tiler.Next(tileExtents.Value);

                if (_o.RenameConflictingBlocks)
                    BlockCleanup.RenameConflictingBlocks(src, master, "~" + index, _log);

                // Model space of the source -> model space of the master, moved by T.
                master.Insert(Matrix3d.Displacement(t), src, true);

                var placement = new Placement { Item = item, Translation = t };
                bool multiple = analysis.Layouts.Count > 1;
                foreach (SourceLayout sl in analysis.Layouts)
                {
                    if (_o.SplitSheetsByViewport && item.Kind == "Sheet")
                        placement.Layouts.AddRange(cloner.CloneSplit(src, sl, item, t, multiple));
                    else
                        placement.Layouts.Add(cloner.Clone(src, sl, item, t, multiple));
                }

                if (analysis.Layouts.Count == 0 && tileExtents.HasValue)
                {
                    var moved = new Extents3d(tileExtents.Value.MinPoint + t, tileExtents.Value.MaxPoint + t);
                    placement.Layouts.Add(cloner.CreateAuto(item, moved));
                }

                _log.Info($"Merged '{item.LayoutName}': T=({t.X:0.###}, {t.Y:0.###}), layouts={placement.Layouts.Count}, " +
                          $"viewport windows={analysis.Windows.Count}");
                return placement;
            }
        }

        private void PostProcess(Database master)
        {
            // Step ids are understood by the Revit side: a native crash inside a step disables
            // that option and the merge is retried.
            Step("explode", _o.ExplodeAnonymousBlocks, () => BlockCleanup.ExplodeAnonymousBlocks(master, _log, new InstallationGuard(_o)));
            Step("jastd", _o.JaStandards, () => JaStandards.Apply(master, _o, _log));
            Step("hatch", _o.NormalizeHatches, () => HatchNormalizer.Normalize(master, _o.MaxHatchLines, _log));
            Step("text", _o.RepairTextStyles, () => TextStyleRepair.Repair(master, _o, _log));
            Step("dedupe", _o.RemoveDuplicateEntities, () => DuplicateRemover.Remove(master, _log));
            Step("vpnoplot", _o.ViewportsNoPlot, () => ViewportLayer.Apply(master, _o.ViewportLayerName, _log));
            Step("purge", _o.PurgeUnused, () => BlockCleanup.Purge(master, _log));
            Step("bylayer", _o.ColorsByLayer, () => ByLayerColors.Apply(master, _o.DimTextColorIndex <= 0, _log));
            Step("dimcolor", _o.DimTextColorIndex > 0, () => DimensionColor.Apply(master, _o.DimTextColorIndex, _log));
        }

        /// <summary>A failing clean-up step is logged, never fatal: an un-purged drawing is
        /// still a far better outcome than no drawing.</summary>
        private void Step(string name, bool enabled, Action action)
        {
            if (!enabled) return;
            string marker = MergeProgress.Step + name;
            MergeProgress.Mark(_m.ProgressPath, marker);
            try { action(); }
            catch (System.Exception ex) { _log.Warn($"Clean-up step '{name}' failed: {ex.Message}"); }
            MergeProgress.Mark(_m.ProgressPath, MergeProgress.Done + marker);
        }

        /// <summary>Linetype scaling that is correct in every viewport at once (see Revit side:
        /// LineScaling.PaperSpace), plus MEASUREMENT aligned with INSUNITS for hatch lookups.</summary>
        private static void ConfigureHeader(Database db)
        {
            db.Psltscale = true;
            // LTSCALE is ALWAYS 1. When paper is in another unit than model, the linetype
            // definitions themselves are scaled instead (PaperUnits.ScaleLinetypes).
            db.Ltscale = 1.0;
            // MSLTSCALE (model-space annotative ltype scale) has no managed Database property;
            // its default (1) is already what we want.
            HatchNormalizer.AlignMeasurement(db);
        }

        /// <summary>
        /// Traceability for pitfall #1: NOD/RVT_UNIFY keeps, per item, the translation applied in
        /// model space and the Revit coordinate context, so the original (internal or shared)
        /// position can be restored (MOVE by -T, then by the stored shared offsets).
        /// </summary>
        private void WriteTraceability(Database db, List<Placement> placements)
        {
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForWrite);
                var dict = new DBDictionary();
                nod.SetAt("RVT_UNIFY", dict);
                tr.AddNewlyCreatedDBObject(dict, true);

                var c = _m.Coordinates ?? new CoordinateInfo();
                var coords = new Xrecord
                {
                    Data = new ResultBuffer(
                        new TypedValue((int)DxfCode.Text, "Base=" + c.Base + ";Units=" + c.DwgUnits + ";Source=" + _m.SourceProject),
                        new TypedValue((int)DxfCode.Real, c.SharedEastWest),
                        new TypedValue((int)DxfCode.Real, c.SharedNorthSouth),
                        new TypedValue((int)DxfCode.Real, c.SharedElevation),
                        new TypedValue((int)DxfCode.Real, c.SharedAngleRadians),
                        new TypedValue((int)DxfCode.Real, c.ProjectBasePointX),
                        new TypedValue((int)DxfCode.Real, c.ProjectBasePointY),
                        new TypedValue((int)DxfCode.Real, c.SurveyPointX),
                        new TypedValue((int)DxfCode.Real, c.SurveyPointY))
                };
                dict.SetAt("COORDINATES", coords);
                tr.AddNewlyCreatedDBObject(coords, true);

                foreach (var p in placements)
                {
                    var x = new Xrecord
                    {
                        Data = new ResultBuffer(
                            new TypedValue((int)DxfCode.Text, p.Item.Kind + ":" + p.Item.RevitElementId),
                            new TypedValue((int)DxfCode.Text, string.Join(" | ", p.Layouts)),
                            new TypedValue((int)DxfCode.Real, p.Translation.X),
                            new TypedValue((int)DxfCode.Real, p.Translation.Y),
                            new TypedValue((int)DxfCode.Real, p.Translation.Z))
                    };
                    dict.SetAt(p.Item.Key, x);
                    tr.AddNewlyCreatedDBObject(x, true);
                }
                tr.Commit();
            }
        }

        /// <summary>Non-plotting point + label at each item's original 0,0 (after tiling).</summary>
        private void AddOriginMarkers(Database db, List<Placement> placements)
        {
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForWrite);
                ObjectId layerId;
                const string layerName = "RVT-UNIFY-ORIGIN";
                if (lt.Has(layerName)) layerId = lt[layerName];
                else
                {
                    var layer = new LayerTableRecord { Name = layerName, IsPlottable = false };
                    layerId = lt.Add(layer);
                    tr.AddNewlyCreatedDBObject(layer, true);
                }

                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                foreach (var p in placements)
                {
                    Point3d o = Point3d.Origin + p.Translation;
                    var pt = new DBPoint(o) { LayerId = layerId };
                    ms.AppendEntity(pt);
                    tr.AddNewlyCreatedDBObject(pt, true);
                    var label = new DBText { Position = o, Height = 1.0, TextString = "0,0 of " + p.Item.LayoutName, LayerId = layerId };
                    ms.AppendEntity(label);
                    tr.AddNewlyCreatedDBObject(label, true);
                }
                tr.Commit();
            }
        }

        private static HashSet<string> ExistingLayoutNames(Database db)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry e in dict)
                    if (!((Layout)tr.GetObject(e.Value, OpenMode.ForRead)).ModelType) names.Add(e.Key);
                tr.Commit();
            }
            return names;
        }

        /// <summary>Removes the empty template layouts ("Layout1"...) so only Revit items remain.</summary>
        private void DeleteLayouts(Database db, HashSet<string> names)
        {
            Database previous = HostApplicationServices.WorkingDatabase;
            HostApplicationServices.WorkingDatabase = db;
            try
            {
                foreach (string n in names)
                {
                    try { LayoutManager.Current.DeleteLayout(n); }
                    catch (System.Exception ex) { _log.Info($"Default layout '{n}' kept: {ex.Message}"); }
                }
            }
            finally
            {
                HostApplicationServices.WorkingDatabase = previous;
            }
        }

        internal static DwgVersion MapVersion(string v)
        {
            switch (v)
            {
                case "2007": return DwgVersion.AC1021;
                case "2010": return DwgVersion.AC1024;
                case "2013": return DwgVersion.AC1027;
                case "2018": return DwgVersion.AC1032;
                default: return DwgVersion.Current;
            }
        }

        /// <summary>Row-major grid of tiles growing +X then -Y, top-aligned per row.</summary>
        private sealed class Tiler
        {
            private readonly MergeOptions _o;
            private double _cursorX, _rowTop, _rowHeight, _rowGap;
            private int _col;

            public Tiler(MergeOptions o) { _o = o; }

            public Vector3d Next(Extents3d e)
            {
                double w = e.MaxPoint.X - e.MinPoint.X;
                double h = e.MaxPoint.Y - e.MinPoint.Y;
                double gap = _o.TileGap > 0 ? _o.TileGap : Math.Max(Math.Max(w, h) * 0.25, 1.0);
                int perRow = Math.Max(1, _o.TilesPerRow);

                if (_col >= perRow)
                {
                    _rowTop -= _rowHeight + _rowGap;
                    _cursorX = 0;
                    _rowHeight = 0;
                    _rowGap = 0;
                    _col = 0;
                }

                var t = new Vector3d(_cursorX - e.MinPoint.X, _rowTop - e.MaxPoint.Y, 0);
                _cursorX += w + gap;
                _rowHeight = Math.Max(_rowHeight, h);
                _rowGap = Math.Max(_rowGap, gap);
                _col++;
                return t;
            }
        }
    }
}
