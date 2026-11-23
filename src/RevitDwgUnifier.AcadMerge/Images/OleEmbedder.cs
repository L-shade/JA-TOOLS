using System;
using System.Collections.Generic;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using RevitDwgUnifier.Contracts;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
// System.Drawing names spelled out: DatabaseServices also has an "Image" class.
using Bitmap = System.Drawing.Bitmap;
using DrawingImage = System.Drawing.Image;
using DColor = System.Drawing.Color;
using DRectangle = System.Drawing.Rectangle;
using Graphics = System.Drawing.Graphics;
using GraphicsUnit = System.Drawing.GraphicsUnit;
using RotateFlipType = System.Drawing.RotateFlipType;

namespace RevitDwgUnifier.AcadMerge.Images
{
    /// <summary>
    /// Converts every raster image reference of the drawing into an EMBEDDED OLE object at the
    /// same position and size (the RVTOLE command, run in full AutoCAD by the Revit add-in).
    ///
    /// AutoCAD has no API that builds an OLE object from a file: an OLE frame can only be created
    /// by pasting from the Windows clipboard. So, for every image, in every layout where it shows:
    ///   1. find where it really is on the sheet: the image may sit inside the title block block
    ///      (or nested blocks), so the full block transform is applied to its four corners;
    ///   2. prepare the bitmap: clip boundary applied, transparency flattened on white, mirrored
    ///      and rotated like the image (OLE frames are always upright rectangles), size capped;
    ///   3. put it on the clipboard and PASTECLIP it in that layout's paper space (or model space);
    ///   4. set the OLE frame to the exact rectangle the image covered, on the image's layer;
    ///   5. once every occurrence is embedded, erase the image entities and their definitions.
    /// An image whose file is missing, or whose paste fails, is left as a reference and reported.
    /// </summary>
    internal sealed class OleEmbedder
    {
        private readonly OleManifest _m;
        private readonly MergeLog _log;
        private readonly MergeResult _result;
        private readonly Editor _ed;
        private readonly Database _db;

        private sealed class Occurrence
        {
            public string Layout;
            public bool IsModel;
            public ObjectId ImageId;
            public Matrix3d Xform;     // image (block) coordinates -> layout space
            public ObjectId LayerId;
        }

        public OleEmbedder(OleManifest manifest, MergeLog log, MergeResult result)
        {
            _m = manifest;
            _log = log;
            _result = result;
            var doc = AcApp.DocumentManager.MdiActiveDocument
                      ?? throw new InvalidOperationException("No drawing is open.");
            _ed = doc.Editor;
            _db = doc.Database;
        }

        public void Run()
        {
            // Even if no OLE object can be created, the saved drawing must show the linked images.
            ImageReferences.LoadAll(_db, _log);
            List<Occurrence> found = Collect();
            _log.Info($"Raster image occurrences found: {found.Count}");
            var failedImages = new HashSet<ObjectId>();
            int converted = 0, cloned = 0;

            // Geometry and picture of every occurrence first: the same picture (the logo of the title
            // block on every sheet...) is pasted ONCE and then copied to the other places, without
            // switching layouts again. Full AutoCAD switches layouts slowly (regeneration), and the
            // clipboard round trip is the slowest step: N sheets used to cost N switches + N pastes.
            var targets = new List<KeyValuePair<Occurrence, Target>>();
            foreach (var occ in found)
            {
                Target t = null;
                try { t = Prepare(occ); }
                catch (Exception ex) { _log.Warn($"Layout '{occ.Layout}': image skipped ({ex.Message})."); }
                if (t == null) failedImages.Add(occ.ImageId);
                else targets.Add(new KeyValuePair<Occurrence, Target>(occ, t));
            }
            _log.Info($"Distinct pictures to paste: {targets.Select(x => x.Value.Key).Distinct().Count()} (for {targets.Count} occurrence(s)).");

            var masters = new Dictionary<string, ObjectId>();
            string current = null;
            // paper space first, model space last; layouts in order so each one is activated at most once
            foreach (var group in targets.GroupBy(x => x.Key.Layout).OrderBy(g => g.First().Key.IsModel ? 1 : 0))
            {
                foreach (var pair in group)
                {
                    Occurrence occ = pair.Key;
                    Target t = pair.Value;
                    try
                    {
                        if (masters.TryGetValue(t.Key, out ObjectId master) && !master.IsErased && Clone(master, occ, t))
                        {
                            converted++; cloned++;
                            continue;
                        }
                        if (_emptyPastes >= 3) { failedImages.Add(occ.ImageId); continue; }
                        if (current != occ.Layout)
                        {
                            if (!SwitchTo(occ.Layout, occ.IsModel)) { failedImages.Add(occ.ImageId); continue; }
                            current = occ.Layout;
                        }
                        ObjectId ole = PasteAndPlace(occ, t);
                        if (ole.IsNull) failedImages.Add(occ.ImageId);
                        else { converted++; masters[t.Key] = ole; }
                    }
                    catch (Exception ex)
                    {
                        failedImages.Add(occ.ImageId);
                        _log.Warn($"Layout '{occ.Layout}': image could not be embedded ({ex.Message}).");
                    }
                }
            }
            _log.Info($"OLE: {converted - cloned} pasted, {cloned} copied from an identical picture.");

            if (_emptyPastes >= 3)
                _log.Warn("PASTECLIP created no OLE object 3 times in a row; the OLE pass was stopped. " +
                          "Images stay linked to the image folder next to the DWG.");

            int erased = EraseConverted(found.Select(o => o.ImageId).Distinct().Where(id => !failedImages.Contains(id)));
            PurgeImageDefinitions();
            try { LayoutManager.Current.CurrentLayout = "Model"; } catch { }

            _result.ImageCount = converted;
            _result.ImagesFailed = found.Count - converted;
            _log.Stat($"Images embedded as OLE objects: {converted} of {found.Count} occurrence(s); " +
                      $"{erased} image reference(s) removed" +
                      (failedImages.Count > 0 ? $"; {failedImages.Count} image(s) kept as references" : "") + ".");

            _db.SaveAs(_m.OutputDwgPath, MasterDrawingBuilder.MapVersion(_m.DwgVersion));
            _result.OutputDwgPath = _m.OutputDwgPath;
            _result.Success = true;
        }

        // ------------------------------------------------------------------ collect

        private List<Occurrence> Collect()
        {
            var list = new List<Occurrence>();
            using (var tr = _db.TransactionManager.StartTransaction())
            {
                var dict = (DBDictionary)tr.GetObject(_db.LayoutDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry e in dict)
                {
                    var lay = (Layout)tr.GetObject(e.Value, OpenMode.ForRead);
                    var btr = (BlockTableRecord)tr.GetObject(lay.BlockTableRecordId, OpenMode.ForRead);
                    Walk(tr, btr, Matrix3d.Identity, e.Key, lay.ModelType, ObjectId.Null, 0, list);
                }
                tr.Commit();
            }
            return list;
        }

        private static void Walk(Transaction tr, BlockTableRecord btr, Matrix3d xform, string layout, bool isModel,
                                 ObjectId outerLayer, int depth, List<Occurrence> list)
        {
            if (depth > 12) return;
            foreach (ObjectId id in btr)
            {
                if (id.IsErased) continue;
                DBObject obj = tr.GetObject(id, OpenMode.ForRead, false);
                if (obj is RasterImage img)
                {
                    if (!img.Visible) continue;
                    ObjectId layer = img.LayerId;
                    if (!outerLayer.IsNull && IsLayerZero(tr, layer)) layer = outerLayer; // BYBLOCK-like
                    list.Add(new Occurrence { Layout = layout, IsModel = isModel, ImageId = id, Xform = xform, LayerId = layer });
                }
                else if (obj is BlockReference br)
                {
                    var def = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                    if (def.IsFromExternalReference || def.IsLayout) continue;
                    ObjectId layer = outerLayer.IsNull || !IsLayerZero(tr, br.LayerId) ? br.LayerId : outerLayer;
                    Walk(tr, def, xform * br.BlockTransform, layout, isModel, layer, depth + 1, list);
                }
            }
        }

        private static bool IsLayerZero(Transaction tr, ObjectId layerId)
        {
            try { return ((LayerTableRecord)tr.GetObject(layerId, OpenMode.ForRead)).Name == "0"; }
            catch { return false; }
        }

        // ------------------------------------------------------------------ embed one

        private bool SwitchTo(string layout, bool isModel)
        {
            try
            {
                LayoutManager.Current.CurrentLayout = layout;
                if (!isModel)
                {
                    object cv = AcApp.GetSystemVariable("CVPORT");
                    if (Convert.ToInt32(cv) != 1) _ed.SwitchToPaperSpace();
                }
                // A valid on-screen view: OLE paste sizes the new frame from the current view.
                try { _ed.Command("_.ZOOM", "_E"); } catch { }
                return true;
            }
            catch (Exception ex)
            {
                _log.Warn($"Layout '{layout}' could not be activated for OLE embedding ({ex.Message}); its images stay as references.");
                return false;
            }
        }

        private sealed class Target
        {
            public string File;
            public double X0, Y0, X1, Y1;
            public Vector3d U, V;
            public double MinX, MinY, MaxX, MaxY, Z;
            public ObjectId LayerId;
            /// <summary>Same key = same bitmap (file, crop, orientation, aspect): pasted once, copied after.</summary>
            public string Key;
        }

        private static string R(double v, int digits) => Math.Round(v, digits).ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>Where the image is on the sheet and which bitmap it needs; null when it cannot be embedded.</summary>
        private Target Prepare(Occurrence occ)
        {
            var t = new Target { LayerId = occ.LayerId };
            Point3d ll, lr, ul;
            using (var tr = _db.TransactionManager.StartTransaction())
            {
                var img = (RasterImage)tr.GetObject(occ.ImageId, OpenMode.ForRead);
                var def = (RasterImageDef)tr.GetObject(img.ImageDefId, OpenMode.ForRead);
                t.File = ImageReferences.Resolve(def, Path.GetDirectoryName(_m.InputDwgPath));
                if (t.File == null)
                {
                    _log.Warn($"Layout '{occ.Layout}': image file '{def.SourceFileName}' not found; kept as a reference.");
                    tr.Commit();
                    return null;
                }
                CoordinateSystem3d cs = img.Orientation;
                ClipFractions(img, cs, out t.X0, out t.Y0, out t.X1, out t.Y1);
                Point3d o = cs.Origin;
                Vector3d u = cs.Xaxis, v = cs.Yaxis;
                ll = (o + u * t.X0 + v * t.Y0).TransformBy(occ.Xform);
                lr = (o + u * t.X1 + v * t.Y0).TransformBy(occ.Xform);
                ul = (o + u * t.X0 + v * t.Y1).TransformBy(occ.Xform);
                tr.Commit();
            }
            t.U = lr - ll; t.V = ul - ll;
            if (t.U.Length < 1e-12 || t.V.Length < 1e-12) return null;
            Point3d ur = ll + t.U + t.V;
            // target rectangle = axis-aligned box of the (possibly rotated) image on the sheet
            t.MinX = new[] { ll.X, lr.X, ul.X, ur.X }.Min(); t.MaxX = new[] { ll.X, lr.X, ul.X, ur.X }.Max();
            t.MinY = new[] { ll.Y, lr.Y, ul.Y, ur.Y }.Min(); t.MaxY = new[] { ll.Y, lr.Y, ul.Y, ur.Y }.Max();
            t.Z = ll.Z;
            bool mirrored = t.U.CrossProduct(t.V).Z < 0;
            Vector3d along = mirrored ? -t.U : t.U;
            double deg = Math.Atan2(along.Y, along.X) * 180.0 / Math.PI;
            t.Key = string.Join("|", t.File.ToLowerInvariant(), R(t.X0, 4), R(t.Y0, 4), R(t.X1, 4), R(t.Y1, 4),
                                mirrored ? "m" : "n", R(deg, 1), R(t.U.Length / t.V.Length, 3));
            return t;
        }

        /// <summary>Clipboard + PASTECLIP in the current layout, then the frame is fitted to the target.</summary>
        private ObjectId PasteAndPlace(Occurrence occ, Target t)
        {
            using (Bitmap bmp = BuildBitmap(t.File, t.X0, t.Y0, t.X1, t.Y1, t.U, t.V))
            {
                PutOnClipboard(bmp);
            }

            ObjectId pasted = Paste(new Point3d(t.MinX, t.MaxY, t.Z), occ.Layout);

            using (var tr = _db.TransactionManager.StartTransaction())
            {
                if (pasted.IsNull)
                {
                    _emptyPastes++;
                    _log.Warn($"Layout '{occ.Layout}': PASTECLIP created nothing for '{Path.GetFileName(t.File)}'.");
                    tr.Commit();
                    return ObjectId.Null;
                }
                _emptyPastes = 0;
                var ent = (Entity)tr.GetObject(pasted, OpenMode.ForWrite);
                if (!(ent is Ole2Frame ole))
                {
                    _log.Warn($"Layout '{occ.Layout}': the clipboard was pasted as {ent.GetType().Name}, not as an OLE object; removed.");
                    ent.Erase();
                    tr.Commit();
                    return ObjectId.Null;
                }

                ole.LayerId = t.LayerId;
                SetProperty(ole, "LockAspect", false);
                PlaceFrame(ole, new Point3d(t.MinX, t.MinY, t.Z), new Point3d(t.MaxX, t.MaxY, t.Z));
                // Pictures always behind the drawing (Send to Back).
                var owner = (BlockTableRecord)tr.GetObject(ole.BlockId, OpenMode.ForRead);
                ImageReferences.SendToBack(tr, owner, new List<ObjectId> { ole.ObjectId });
                tr.Commit();
            }
            _log.Info($"Layout '{occ.Layout}': '{Path.GetFileName(t.File)}' embedded at ({t.MinX:0.####},{t.MinY:0.####})-({t.MaxX:0.####},{t.MaxY:0.####}).");
            return pasted;
        }

        /// <summary>Copies an OLE frame already pasted with the same picture into another layout/place
        /// (no clipboard, no layout switch). false = not possible, the caller pastes instead.</summary>
        private bool Clone(ObjectId master, Occurrence occ, Target t)
        {
            try
            {
                ObjectId space = LayoutSpace(occ.Layout);
                if (space.IsNull) return false;
                var map = new IdMapping();
                _db.DeepCloneObjects(new ObjectIdCollection { master }, space, map, false);
                if (!map.Contains(master)) return false;
                ObjectId copyId = map[master].Value;
                using (var tr = _db.TransactionManager.StartTransaction())
                {
                    var ole = tr.GetObject(copyId, OpenMode.ForWrite) as Ole2Frame;
                    if (ole == null) { tr.Commit(); return false; }
                    ole.LayerId = t.LayerId;
                    PlaceFrame(ole, new Point3d(t.MinX, t.MinY, t.Z), new Point3d(t.MaxX, t.MaxY, t.Z));
                    var owner = (BlockTableRecord)tr.GetObject(space, OpenMode.ForRead);
                    ImageReferences.SendToBack(tr, owner, new List<ObjectId> { copyId });
                    tr.Commit();
                }
                _log.Info($"Layout '{occ.Layout}': '{Path.GetFileName(t.File)}' copied at ({t.MinX:0.####},{t.MinY:0.####})-({t.MaxX:0.####},{t.MaxY:0.####}).");
                return true;
            }
            catch (Exception ex)
            {
                _log.Info($"Layout '{occ.Layout}': OLE copy failed ({ex.Message}); pasting instead.");
                return false;
            }
        }

        private readonly Dictionary<string, ObjectId> _spaces = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);

        private ObjectId LayoutSpace(string layout)
        {
            if (_spaces.TryGetValue(layout, out ObjectId id)) return id;
            id = ObjectId.Null;
            using (var tr = _db.TransactionManager.StartTransaction())
            {
                var dict = (DBDictionary)tr.GetObject(_db.LayoutDictionaryId, OpenMode.ForRead);
                if (dict.Contains(layout)) id = ((Layout)tr.GetObject(dict.GetAt(layout), OpenMode.ForRead)).BlockTableRecordId;
                tr.Commit();
            }
            _spaces[layout] = id;
            return id;
        }

        /// <summary>Visible part of the image as fractions of its full frame (0..1, Y up).</summary>
        private void ClipFractions(RasterImage img, CoordinateSystem3d cs, out double x0, out double y0, out double x1, out double y1)
        {
            x0 = 0; y0 = 0; x1 = 1; y1 = 1;
            if (!img.IsClipped) return;
            try
            {
                // Called by reflection: the managed names differ slightly between releases.
                var t = img.GetType();
                var boundary = t.GetMethod("GetClipBoundary", Type.EmptyTypes)?.Invoke(img, null) as Point2dCollection;
                object mx = t.GetProperty("PixelToModelTransform")?.GetValue(img, null)
                            ?? t.GetMethod("GetPixelToModelTransform", Type.EmptyTypes)?.Invoke(img, null);
                if (boundary == null || boundary.Count < 2 || !(mx is Matrix3d pix2model)) return;

                Vector3d u = cs.Xaxis, v = cs.Yaxis;
                double fx0 = 1, fy0 = 1, fx1 = 0, fy1 = 0;
                foreach (Point2d p in boundary)
                {
                    Vector3d d = new Point3d(p.X, p.Y, 0).TransformBy(pix2model) - cs.Origin;
                    double fx = d.DotProduct(u) / u.LengthSqrd, fy = d.DotProduct(v) / v.LengthSqrd;
                    fx0 = Math.Min(fx0, fx); fx1 = Math.Max(fx1, fx);
                    fy0 = Math.Min(fy0, fy); fy1 = Math.Max(fy1, fy);
                }
                fx0 = Clamp01(fx0); fx1 = Clamp01(fx1); fy0 = Clamp01(fy0); fy1 = Clamp01(fy1);
                if (fx1 - fx0 > 1e-3 && fy1 - fy0 > 1e-3) { x0 = fx0; x1 = fx1; y0 = fy0; y1 = fy1; }
            }
            catch (Exception ex)
            {
                _log.Info("Image clip boundary ignored: " + ex.Message);
            }
        }

        private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

        /// <summary>
        /// Bitmap exactly as the image looks on the sheet: cropped to the clip, flattened on white
        /// (DIB has no alpha: transparent PNG areas would turn black), mirrored and rotated, with
        /// square pixels, and limited to MaxPixels on its longest side.
        /// </summary>
        private Bitmap BuildBitmap(string file, double x0, double y0, double x1, double y1, Vector3d U, Vector3d V)
        {
            using (var src = LoadUnlocked(file))
            {
                // 1. crop (fractions are Y-up, bitmap rows are Y-down)
                int cx = (int)Math.Floor(x0 * src.Width), cw = Math.Max(1, (int)Math.Ceiling(x1 * src.Width) - cx);
                int cy = (int)Math.Floor((1 - y1) * src.Height), ch = Math.Max(1, (int)Math.Ceiling((1 - y0) * src.Height) - cy);
                cw = Math.Min(cw, src.Width - cx); ch = Math.Min(ch, src.Height - cy);

                // 2. square pixels on the sheet + size cap
                double pxU = U.Length / cw, pxV = V.Length / ch;
                double unit = Math.Min(pxU, pxV);
                double w = U.Length / unit, h = V.Length / unit;
                int max = _m.MaxPixels > 0 ? _m.MaxPixels : 3000;
                double fit = Math.Min(1.0, max / Math.Max(w, h));
                int bw = Math.Max(1, (int)Math.Round(w * fit)), bh = Math.Max(1, (int)Math.Round(h * fit));

                var upright = new Bitmap(bw, bh, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(upright))
                {
                    g.Clear(DColor.White);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(src, new DRectangle(0, 0, bw, bh), cx, cy, cw, ch, GraphicsUnit.Pixel);
                }

                // 3. mirror: U x V pointing down means the image was mirrored in the block / view.
                bool mirrored = U.CrossProduct(V).Z < 0;
                Vector3d along = U;
                if (mirrored)
                {
                    upright.RotateFlip(RotateFlipType.RotateNoneFlipX);
                    along = -U;
                }

                // 4. rotation (counter-clockwise on the sheet = clockwise in bitmap coordinates)
                double angle = Math.Atan2(along.Y, along.X);
                double deg = angle * 180.0 / Math.PI;
                double snapped = Math.Round(deg / 90.0) * 90.0;
                if (Math.Abs(deg) < 1e-3) return upright;
                if (Math.Abs(deg - snapped) < 1e-3)
                {
                    int quarter = (((int)Math.Round(snapped / 90.0)) % 4 + 4) % 4;
                    upright.RotateFlip(quarter == 1 ? RotateFlipType.Rotate270FlipNone
                                     : quarter == 2 ? RotateFlipType.Rotate180FlipNone
                                     : RotateFlipType.Rotate90FlipNone);
                    return upright;
                }

                double c = Math.Abs(Math.Cos(angle)), s = Math.Abs(Math.Sin(angle));
                int rw = (int)Math.Ceiling(upright.Width * c + upright.Height * s);
                int rh = (int)Math.Ceiling(upright.Width * s + upright.Height * c);
                var rotated = new Bitmap(rw, rh, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(rotated))
                {
                    g.Clear(DColor.White);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.TranslateTransform(rw / 2f, rh / 2f);
                    g.RotateTransform((float)-deg);
                    g.DrawImage(upright, -upright.Width / 2f, -upright.Height / 2f, upright.Width, upright.Height);
                }
                upright.Dispose();
                return rotated;
            }
        }

        /// <summary>Loads through a memory copy so the image file is never kept locked.</summary>
        private static Bitmap LoadUnlocked(string file)
        {
            byte[] bytes = File.ReadAllBytes(file);
            using (var ms = new MemoryStream(bytes))
            using (var img = DrawingImage.FromStream(ms))
                return new Bitmap(img);
        }

        /// <summary>
        /// Puts the bitmap on the Windows clipboard as a packed DIB (CF_DIB), the format AutoCAD
        /// turns into a "Picture (Device Independent Bitmap)" OLE object. Done with the Win32 API
        /// directly (owned by AutoCAD's main window, as SetClipboardData requires); Windows
        /// synthesises CF_BITMAP from it. The WinForms clipboard is the fallback.
        /// </summary>
        private void PutOnClipboard(Bitmap bmp)
        {
            byte[] dib;
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Bmp);
                byte[] file = ms.ToArray();
                dib = new byte[file.Length - 14];               // drop BITMAPFILEHEADER
                Buffer.BlockCopy(file, 14, dib, 0, dib.Length);
            }

            IntPtr owner = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                if (NativeClipboard.OpenClipboard(owner))
                {
                    IntPtr h = IntPtr.Zero;
                    try
                    {
                        NativeClipboard.EmptyClipboard();
                        h = NativeClipboard.GlobalAlloc(NativeClipboard.GMEM_MOVEABLE, (UIntPtr)dib.Length);
                        IntPtr p = NativeClipboard.GlobalLock(h);
                        System.Runtime.InteropServices.Marshal.Copy(dib, 0, p, dib.Length);
                        NativeClipboard.GlobalUnlock(h);
                        if (NativeClipboard.SetClipboardData(NativeClipboard.CF_DIB, h) != IntPtr.Zero)
                        {
                            h = IntPtr.Zero; // owned by the clipboard now
                            return;
                        }
                        _log.Info("SetClipboardData(CF_DIB) failed, error " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                        break;
                    }
                    finally
                    {
                        if (h != IntPtr.Zero) NativeClipboard.GlobalFree(h);
                        NativeClipboard.CloseClipboard();
                    }
                }
                System.Threading.Thread.Sleep(100);
            }

            var data = new System.Windows.Forms.DataObject();
            data.SetData(System.Windows.Forms.DataFormats.Bitmap, true, bmp);
            System.Windows.Forms.Clipboard.SetDataObject(data, true, 10, 100);
        }

        private static class NativeClipboard
        {
            public const uint CF_DIB = 8;
            public const uint GMEM_MOVEABLE = 0x0002;
            [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
            public static extern bool OpenClipboard(IntPtr hWndNewOwner);
            [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
            public static extern bool CloseClipboard();
            [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
            public static extern bool EmptyClipboard();
            [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
            public static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
            [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
            public static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
            [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
            public static extern IntPtr GlobalLock(IntPtr hMem);
            [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool GlobalUnlock(IntPtr hMem);
            [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
            public static extern IntPtr GlobalFree(IntPtr hMem);
        }

        private int _emptyPastes;
        private bool _diagnosed;

        /// <summary>
        /// PASTECLIP at the given point; returns the entity it created. Every object appended to
        /// the database during the command is captured (more reliable than "last entity", which
        /// can miss objects when the window has no valid view).
        /// </summary>
        private ObjectId Paste(Point3d insert, string layout)
        {
            if (!_diagnosed) LogClipboardFormats();

            var appended = new List<ObjectId>();
            ObjectEventHandler onAppend = (s, e) =>
            {
                try { if (e.DBObject is Entity) appended.Add(e.DBObject.ObjectId); } catch { }
            };
            ObjectId before = LastEntity();
            _db.ObjectAppended += onAppend;
            try
            {
                _ed.Command("_.PASTECLIP", insert);
            }
            finally
            {
                _db.ObjectAppended -= onAppend;
            }

            ObjectId found = ObjectId.Null;
            using (var tr = _db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in appended)
                {
                    if (id.IsNull || id.IsErased) continue;
                    if (tr.GetObject(id, OpenMode.ForRead, false) is Ole2Frame) { found = id; break; }
                    if (found.IsNull) found = id;
                }
                tr.Commit();
            }
            if (found.IsNull)
            {
                ObjectId last = LastEntity();
                if (!last.IsNull && last != before) found = last;
            }

            if (!_diagnosed)
            {
                _diagnosed = true;
                string cmd = "";
                try { cmd = Convert.ToString(AcApp.GetSystemVariable("CMDNAMES")); } catch { }
                _log.Info($"First paste in '{layout}': {appended.Count} object(s) appended, result={(found.IsNull ? "none" : found.ObjectClass.DxfName)}, " +
                          $"CMDNAMES after='{cmd}'.");
            }
            return found;
        }

        private void LogClipboardFormats()
        {
            try
            {
                var d = System.Windows.Forms.Clipboard.GetDataObject();
                string[] f = d?.GetFormats(false) ?? new string[0];
                _log.Info("Clipboard formats before PASTECLIP: " + string.Join(", ", f));
            }
            catch (Exception ex) { _log.Info("Clipboard formats unreadable: " + ex.Message); }
        }

        private ObjectId LastEntity()
        {
            try
            {
                PromptSelectionResult r = _ed.SelectLast();
                if (r.Status == PromptStatus.OK && r.Value != null && r.Value.Count > 0) return r.Value[0].ObjectId;
            }
            catch { }
            return ObjectId.Null;
        }

        /// <summary>
        /// Sets the OLE frame to the target box. Position3d (a Rectangle3d) is used when the API
        /// allows setting it; otherwise, or if the result is off, the frame is scaled and moved.
        /// </summary>
        private void PlaceFrame(Ole2Frame ole, Point3d min, Point3d max)
        {
            double tw = max.X - min.X, th = max.Y - min.Y;
            try
            {
                PropertyInfo pos = typeof(Ole2Frame).GetProperty("Position3d");
                if (pos != null && pos.CanWrite)
                {
                    object rect = Activator.CreateInstance(pos.PropertyType,
                        new Point3d(min.X, max.Y, min.Z), new Point3d(max.X, max.Y, min.Z),
                        new Point3d(min.X, min.Y, min.Z), new Point3d(max.X, min.Y, min.Z));
                    pos.SetValue(ole, rect, null);
                }
            }
            catch (Exception ex) { _log.Info("OLE Position3d not set: " + ex.Message); }

            Extents3d e;
            try { e = ole.GeometricExtents; } catch { return; }
            double ew = e.MaxPoint.X - e.MinPoint.X, eh = e.MaxPoint.Y - e.MinPoint.Y;
            double tol = Math.Max(tw, th) * 0.005;
            if (Math.Abs(ew - tw) <= tol && Math.Abs(eh - th) <= tol &&
                e.MinPoint.DistanceTo(new Point3d(min.X, min.Y, e.MinPoint.Z)) <= tol) return;

            // Fallback: the bitmap already has the target aspect ratio -> uniform scale + move.
            if (ew > 1e-12)
            {
                try { ole.TransformBy(Matrix3d.Scaling(tw / ew, e.MinPoint)); } catch (Exception ex) { _log.Info("OLE scale: " + ex.Message); }
                try { e = ole.GeometricExtents; } catch { }
            }
            try { ole.TransformBy(Matrix3d.Displacement(new Point3d(min.X, min.Y, e.MinPoint.Z) - e.MinPoint)); }
            catch (Exception ex) { _log.Info("OLE move: " + ex.Message); }
        }

        private static void SetProperty(object target, string name, object value)
        {
            try
            {
                PropertyInfo p = target.GetType().GetProperty(name);
                if (p != null && p.CanWrite) p.SetValue(target, value, null);
            }
            catch { }
        }

        // ------------------------------------------------------------------ clean up

        private int EraseConverted(IEnumerable<ObjectId> imageIds)
        {
            int n = 0;
            using (var tr = _db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in imageIds)
                {
                    try
                    {
                        if (id.IsErased) continue;
                        tr.GetObject(id, OpenMode.ForWrite).Erase();
                        n++;
                    }
                    catch (Exception ex) { _log.Info("Image not erased: " + ex.Message); }
                }
                tr.Commit();
            }
            return n;
        }

        /// <summary>Removes image definitions nothing refers to any more (no dangling references
        /// listed in the External References palette).</summary>
        private void PurgeImageDefinitions()
        {
            try
            {
                ObjectId dictId = RasterImageDef.GetImageDictionary(_db);
                if (dictId.IsNull) return;
                var ids = new ObjectIdCollection();
                using (var tr = _db.TransactionManager.StartTransaction())
                {
                    var dict = (DBDictionary)tr.GetObject(dictId, OpenMode.ForRead);
                    foreach (DBDictionaryEntry e in dict) ids.Add(e.Value);
                    tr.Commit();
                }
                _db.Purge(ids);
                if (ids.Count == 0) return;
                using (var tr = _db.TransactionManager.StartTransaction())
                {
                    var dict = (DBDictionary)tr.GetObject(dictId, OpenMode.ForWrite);
                    foreach (ObjectId id in ids)
                    {
                        try
                        {
                            dict.Remove(id);
                            tr.GetObject(id, OpenMode.ForWrite).Erase();
                        }
                        catch (Exception ex) { _log.Info("Image definition kept: " + ex.Message); }
                    }
                    tr.Commit();
                }
                _log.Info($"Unused image definitions removed: {ids.Count}");
            }
            catch (Exception ex)
            {
                _log.Info("Image definition purge skipped: " + ex.Message);
            }
        }
    }
}
