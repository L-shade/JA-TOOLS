using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using RevitDwgUnifier.AcadMerge.Geometry;
using RevitDwgUnifier.Contracts;

namespace RevitDwgUnifier.AcadMerge
{
    /// <summary>
    /// Re-creates a source layout (title block, sheet annotations, viewports) as a NEW layout tab
    /// in the master drawing, pointing at the item's (possibly moved) model content.
    ///
    /// Steps:
    ///   1. LayoutManager.CreateLayout (WorkingDatabase temporarily = master: LayoutManager is
    ///      bound to the working database, Core Console has no other document to switch to).
    ///   2. Plot settings copied through a detached PlotSettings object (copying a Layout onto a
    ///      Layout would also copy its block-record link and corrupt the tab).
    ///   3. WblockCloneObjects of every paper-space entity except the overall viewport. Hard/soft
    ///      references inside the set (non-rectangular clip boundaries, per-viewport frozen layers,
    ///      title block definitions) are translated by the IdMapping automatically.
    ///   4. Viewport re-targeting: ViewTarget += T where T is the tiling translation applied to
    ///      model space. ViewCenter/scale/twist stay -> identical framing, any rotation.
    ///   5. Optional paper-origin normalisation: everything moves so the sheet's lower-left corner
    ///      is 0,0. This fixes title block families whose insertion point is not bottom-left
    ///      (plots offset / clipped by the printer margin in AutoCAD).
    /// </summary>
    internal sealed class LayoutCloner
    {
        private readonly Database _master;
        private readonly MergeOptions _o;
        private readonly MergeLog _log;

        /// <summary>Self-check data: tab name -> number of model entities the viewport showed in
        /// the ORIGINAL sheet DWG. Compared after the merge by MasterDrawingBuilder.</summary>
        public Dictionary<string, int> ExpectedViewportHits { get; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Layouts built by <see cref="CreateAuto"/>: already drawn in mm / inches.</summary>
        public HashSet<string> AutoLayouts { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public LayoutCloner(Database master, MergeOptions options, MergeLog log)
        {
            _master = master;
            _o = options;
            _log = log;
        }

        public string Clone(Database src, SourceLayout sl, MergeItem item, Vector3d translation, bool multipleLayouts)
        {
            string desired = multipleLayouts ? item.LayoutName + " (" + sl.Name + ")" : item.LayoutName;
            return CloneSubset(src, sl, item, translation, desired, sl.EntityIds, null, new Vector3d(0, 0, 0));
        }

        /// <summary>
        /// Window/door book mode: one layout per viewport of the source sheet (see
        /// <see cref="ViewportSplitter"/>). Returns the created tab names.
        /// </summary>
        public List<string> CloneSplit(Database src, SourceLayout sl, MergeItem item, Vector3d translation, bool multipleLayouts)
        {
            List<PaperViewport> vps;
            using (var str = src.TransactionManager.StartTransaction())
            {
                vps = ViewportSplitter.Collect(str, sl, out _);
                CountSourceHits(src, str, vps);
                str.Commit();
            }
            if (vps.Count == 0)
                return new List<string> { Clone(src, sl, item, translation, multipleLayouts) };

            ViewportSplitter.Match(vps, item.Viewports);
            Rect2 drawingArea = Rect2.Union(vps.Select(v => v.Rect));

            // Left-to-right, top-to-bottom: the reading order of the original sheet.
            var ordered = vps.OrderByDescending(v => Math.Round(v.Rect.CenterY / Math.Max(drawingArea.Height * 0.25, 1e-9)))
                             .ThenBy(v => v.Rect.CenterX).ToList();

            var names = new List<string>();
            int index = 0;
            foreach (var keep in ordered)
            {
                index++;
                // Everything except the other viewports and their labels.
                var exclude = new HashSet<ObjectId>();
                foreach (var other in vps)
                {
                    if (other == keep) continue;
                    exclude.Add(other.Id);
                    foreach (var sat in other.Satellites)
                        if (!keep.Satellites.Contains(sat)) exclude.Add(sat);
                }
                var ids = sl.EntityIds.Where(id => !exclude.Contains(id)).ToList();

                // Centre the viewport (and its label) in the sheet's drawing area; scale unchanged.
                var shift = new Vector3d(drawingArea.CenterX - keep.Rect.CenterX, drawingArea.CenterY - keep.Rect.CenterY, 0);
                var moveSet = new HashSet<ObjectId>(keep.Satellites) { keep.Id };

                string desired = keep.Spec?.LayoutName;
                if (string.IsNullOrWhiteSpace(desired))
                {
                    desired = item.LayoutName + " - " + index;
                    _log.Warn($"'{item.LayoutName}': viewport {index} could not be matched to a Revit view; tab named '{desired}'.");
                }
                string created = CloneSubset(src, sl, item, translation, desired, ids, moveSet, shift);
                if (keep.SourceHits >= 0) ExpectedViewportHits[created] = keep.SourceHits;
                names.Add(created);
            }
            return names;
        }

        /// <summary>How many model entities each viewport shows in the source sheet DWG.</summary>
        private static void CountSourceHits(Database src, Transaction str, List<PaperViewport> vps)
        {
            var model = new List<Extents3d>();
            var bt = (BlockTable)str.GetObject(src.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)str.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            foreach (ObjectId id in ms)
                if (str.GetObject(id, OpenMode.ForRead) is Entity e && ViewportMath.TryGetExtents(e, out var x)) model.Add(x);

            foreach (var pv in vps)
            {
                var vp = (Viewport)str.GetObject(pv.Id, OpenMode.ForRead);
                if (!ViewportMath.IsTopView(vp)) continue;
                Extents3d? w = ViewportMath.ModelWindow(vp, 0);
                if (w.HasValue) pv.SourceHits = model.Count(m => ViewportMath.IntersectsXY(m, w.Value));
            }
        }

        private string CloneSubset(Database src, SourceLayout sl, MergeItem item, Vector3d translation, string desired,
                                   IEnumerable<ObjectId> entityIds, HashSet<ObjectId> moveSet, Vector3d moveBy)
        {
            ObjectId layoutId = CreateLayout(desired, out string name);
            ObjectId btrId;

            using (var tr = _master.TransactionManager.StartTransaction())
            using (var str = src.TransactionManager.StartTransaction())
            {
                var lay = (Layout)tr.GetObject(layoutId, OpenMode.ForWrite);
                var srcLay = (Layout)str.GetObject(sl.LayoutId, OpenMode.ForRead);
                try
                {
                    using (var ps = new PlotSettings(false))
                    {
                        ps.CopyFrom(srcLay);
                        lay.CopyFrom(ps);
                    }
                }
                catch (Autodesk.AutoCAD.Runtime.Exception ex)
                {
                    _log.Warn($"Layout '{name}': plot settings not copied ({ex.Message}).");
                }
                // Tab order: CreateLayout appends at the end, so creation order == manifest order.
                btrId = lay.BlockTableRecordId;
                str.Commit();
                tr.Commit();
            }

            var sourceList = entityIds.ToList();
            var ids = new ObjectIdCollection(sourceList.ToArray());
            var map = new IdMapping();
            src.WblockCloneObjects(ids, btrId, map, DuplicateRecordCloning.Ignore, false);

            var sourceSet = new HashSet<ObjectId>(sourceList);
            var clones = new List<ObjectId>();
            var movedClones = new List<ObjectId>();
            foreach (IdPair pair in map)
            {
                if (!(pair.IsCloned && pair.IsPrimary && sourceSet.Contains(pair.Key))) continue;
                clones.Add(pair.Value);
                if (moveSet != null && moveSet.Contains(pair.Key)) movedClones.Add(pair.Value);
            }

            using (var tr = _master.TransactionManager.StartTransaction())
            {
                int retargeted = 0;
                foreach (ObjectId id in clones)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead) is Viewport vp)) continue;
                    vp.UpgradeOpen();
                    vp.ViewTarget = vp.ViewTarget + translation;
                    try { vp.On = true; } catch (Autodesk.AutoCAD.Runtime.Exception) { /* activates on open */ }
                    retargeted++;
                }

                if (movedClones.Count > 0 && moveBy.Length > 1e-9)
                {
                    Matrix3d mv = Matrix3d.Displacement(moveBy);
                    foreach (ObjectId id in movedClones)
                        ((Entity)tr.GetObject(id, OpenMode.ForWrite)).TransformBy(mv); // frame moves, view unchanged
                }

                Extents3d? paper = Union(tr, clones);
                if (!item.HasTitleBlock && paper.HasValue)
                {
                    clones.Add(AddBorder(tr, btrId, paper.Value, item));
                    paper = Union(tr, clones);
                }

                if (_o.NormalizePaperOrigin && paper.HasValue)
                {
                    Vector3d shift = Point3d.Origin - new Point3d(paper.Value.MinPoint.X, paper.Value.MinPoint.Y, 0);
                    double size = paper.Value.MinPoint.DistanceTo(paper.Value.MaxPoint);
                    if (shift.Length > size * 1e-6)
                    {
                        Matrix3d m = Matrix3d.Displacement(shift);
                        foreach (ObjectId id in clones)
                        {
                            var ent = (Entity)tr.GetObject(id, OpenMode.ForWrite);
                            ent.TransformBy(m); // viewports move as frames; their view is unchanged
                        }
                        _log.Info($"Layout '{name}': paper space moved by ({shift.X:0.###}, {shift.Y:0.###}) to start at 0,0.");
                    }
                }
                tr.Commit();
                _log.Info($"Layout '{name}': {clones.Count} entities, {retargeted} viewport(s) re-targeted" +
                          (movedClones.Count > 0 ? $", viewport centred by ({moveBy.X:0.###}, {moveBy.Y:0.###})." : "."));
            }
            return name;
        }

        /// <summary>Fallback for sources without a paper-space layout: builds one viewport at the
        /// item's view scale around its model content, plus a border and a title line.</summary>
        public string CreateAuto(MergeItem item, Extents3d modelExtents)
        {
            ObjectId layoutId = CreateLayout(item.LayoutName, out string name);
            bool metric = _master.Insunits == UnitsValue.Millimeters || _master.Insunits == UnitsValue.Centimeters ||
                          _master.Insunits == UnitsValue.Meters;
            double paperPerModel = PaperUnitsPerModelUnit(_master.Insunits) / Math.Max(1, item.ViewScale);
            double margin = metric ? 10.0 : 0.5;
            double titleH = metric ? 12.0 : 0.5;

            double w = (modelExtents.MaxPoint.X - modelExtents.MinPoint.X) * paperPerModel;
            double h = (modelExtents.MaxPoint.Y - modelExtents.MinPoint.Y) * paperPerModel;
            Point3d modelCenter = new Point3d((modelExtents.MinPoint.X + modelExtents.MaxPoint.X) / 2,
                                              (modelExtents.MinPoint.Y + modelExtents.MaxPoint.Y) / 2, 0);

            using (var tr = _master.TransactionManager.StartTransaction())
            {
                var lay = (Layout)tr.GetObject(layoutId, OpenMode.ForRead);
                var ps = (BlockTableRecord)tr.GetObject(lay.BlockTableRecordId, OpenMode.ForWrite);

                var vp = new Viewport();
                ps.AppendEntity(vp);
                tr.AddNewlyCreatedDBObject(vp, true);
                vp.CenterPoint = new Point3d(margin + w / 2, margin + titleH + h / 2, 0);
                vp.Width = Math.Max(w, 1e-3);
                vp.Height = Math.Max(h, 1e-3);
                vp.ViewDirection = Vector3d.ZAxis;
                vp.ViewTarget = Point3d.Origin;
                vp.ViewCenter = new Point2d(modelCenter.X, modelCenter.Y);
                vp.CustomScale = paperPerModel;
                vp.Locked = true;
                try { vp.On = true; } catch (Autodesk.AutoCAD.Runtime.Exception) { }

                var frame = new Extents3d(Point3d.Origin, new Point3d(w + 2 * margin, h + 2 * margin + titleH, 0));
                AddRectangle(tr, ps, frame);

                var title = new MText
                {
                    Location = new Point3d(margin, margin + titleH * 0.75, 0),
                    TextHeight = titleH * 0.35,
                    Contents = (item.Title ?? item.LayoutName) + (item.ViewScale > 0 ? "   1:" + item.ViewScale : ""),
                    Attachment = AttachmentPoint.MiddleLeft
                };
                ps.AppendEntity(title);
                tr.AddNewlyCreatedDBObject(title, true);
                tr.Commit();
            }
            _log.Warn($"'{item.LayoutName}': source had no layout; an automatic 1:{item.ViewScale} layout was built.");
            AutoLayouts.Add(name);
            return name;
        }

        private ObjectId CreateLayout(string desired, out string name)
        {
            Database previous = HostApplicationServices.WorkingDatabase;
            HostApplicationServices.WorkingDatabase = _master;
            try
            {
                LayoutManager lm = LayoutManager.Current;
                name = UniqueName(lm, SanitizeLayoutName(desired));
                ObjectId id = lm.CreateLayout(name);
                TryInitialize(id);
                return id;
            }
            finally
            {
                HostApplicationServices.WorkingDatabase = previous;
            }
        }

        /// <summary>Layout.Initialize() creates the overall paper viewport so the cloned
        /// viewports display on first open without a regen. Called via reflection because the
        /// managed wrapper is not exposed identically in every AutoCAD release.</summary>
        private void TryInitialize(ObjectId layoutId)
        {
            try
            {
                using (var tr = _master.TransactionManager.StartTransaction())
                {
                    var lay = (Layout)tr.GetObject(layoutId, OpenMode.ForWrite);
                    var mi = typeof(Layout).GetMethod("Initialize", Type.EmptyTypes);
                    mi?.Invoke(lay, null);
                    tr.Commit();
                }
            }
            catch (System.Exception ex)
            {
                _log.Info("Layout initialisation deferred to first open: " + ex.Message);
            }
        }

        private static string UniqueName(LayoutManager lm, string baseName)
        {
            string name = baseName;
            for (int i = 2; lm.LayoutExists(name); i++)
            {
                string suffix = " (" + i + ")";
                name = (baseName.Length + suffix.Length > 255 ? baseName.Substring(0, 255 - suffix.Length) : baseName) + suffix;
            }
            return name;
        }

        /// <summary>Layout names: max 255 chars, none of  &lt; &gt; / \ " : ; ? * | , = `</summary>
        public static string SanitizeLayoutName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) s = "Layout";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) sb.Append("<>/\\\":;?*|,=`".IndexOf(c) >= 0 || char.IsControl(c) ? '_' : c);
            string r = sb.ToString().Trim();
            if (r.Equals("Model", StringComparison.OrdinalIgnoreCase)) r = "Model_";
            return r.Length > 255 ? r.Substring(0, 255) : r;
        }

        private ObjectId AddBorder(Transaction tr, ObjectId btrId, Extents3d paper, MergeItem item)
        {
            double d = Math.Max(paper.MaxPoint.X - paper.MinPoint.X, paper.MaxPoint.Y - paper.MinPoint.Y) * 0.03;
            var frame = new Extents3d(new Point3d(paper.MinPoint.X - d, paper.MinPoint.Y - d, 0),
                                      new Point3d(paper.MaxPoint.X + d, paper.MaxPoint.Y + d, 0));
            var ps = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForWrite);
            return AddRectangle(tr, ps, frame);
        }

        private static ObjectId AddRectangle(Transaction tr, BlockTableRecord space, Extents3d e)
        {
            var pl = new Polyline(4);
            pl.AddVertexAt(0, new Point2d(e.MinPoint.X, e.MinPoint.Y), 0, 0, 0);
            pl.AddVertexAt(1, new Point2d(e.MaxPoint.X, e.MinPoint.Y), 0, 0, 0);
            pl.AddVertexAt(2, new Point2d(e.MaxPoint.X, e.MaxPoint.Y), 0, 0, 0);
            pl.AddVertexAt(3, new Point2d(e.MinPoint.X, e.MaxPoint.Y), 0, 0, 0);
            pl.Closed = true;
            ObjectId id = space.AppendEntity(pl);
            tr.AddNewlyCreatedDBObject(pl, true);
            return id;
        }

        private static Extents3d? Union(Transaction tr, IEnumerable<ObjectId> ids)
        {
            Extents3d? u = null;
            foreach (var id in ids)
            {
                if (!(tr.GetObject(id, OpenMode.ForRead) is Entity e)) continue;
                if (!ViewportMath.TryGetExtents(e, out var x)) continue;
                if (u.HasValue) { var v = u.Value; v.AddExtents(x); u = v; } else u = x;
            }
            return u;
        }

        public static double PaperUnitsPerModelUnit(UnitsValue units)
        {
            // Paper is mm for metric drawings and inches for imperial ones.
            switch (units)
            {
                case UnitsValue.Centimeters: return 10.0;
                case UnitsValue.Meters: return 1000.0;
                case UnitsValue.Feet: return 12.0;
                default: return 1.0; // mm->mm, in->in, unitless
            }
        }
    }
}
