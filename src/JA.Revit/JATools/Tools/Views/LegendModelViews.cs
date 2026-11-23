using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using RevitDwgUnifier.Infrastructure;
using RevitDwgUnifier.Tools.Common;

namespace RevitDwgUnifier.Tools.Views
{
    /// <summary>
    /// "Vista do modelo": for categories legend components refuse (plumbing fixtures, casework
    /// components, pipe fittings, mechanical equipment, structural framing...), each view of the
    /// ficha is a section placed around the first instance of the type in the model — front, back,
    /// sides and plan as elevations (cut plane in front of the element), "Corte" cut through the
    /// middle — with every other element hidden, the crop tight around the element and overall
    /// dimensions taken from the family's Left/Right/Front/Back/Top/Bottom references.
    /// The views are placed in the drawing area of the ficha when the fichas go on sheets.
    /// </summary>
    public static partial class LegendEngine
    {
        /// <summary>Local frame of a view direction: X right, Y up, Z towards the viewer.</summary>
        private static Transform ViewFrame(Element inst, string key, XYZ center)
        {
            XYZ facing = XYZ.BasisY, hand = XYZ.BasisX;
            if (inst is FamilyInstance fi)
            {
                try
                {
                    XYZ f = new XYZ(fi.FacingOrientation.X, fi.FacingOrientation.Y, 0);
                    XYZ h = new XYZ(fi.HandOrientation.X, fi.HandOrientation.Y, 0);
                    if (f.GetLength() > 1e-6) facing = f.Normalize();
                    if (h.GetLength() > 1e-6) hand = h.Normalize();
                    else hand = facing.CrossProduct(XYZ.BasisZ).Normalize();
                }
                catch { }
            }
            XYZ z, y;
            switch (key)
            {
                case Back: z = facing.Negate(); y = XYZ.BasisZ; break;
                case Right: z = hand; y = XYZ.BasisZ; break;
                case Left: z = hand.Negate(); y = XYZ.BasisZ; break;
                case Section: z = hand; y = XYZ.BasisZ; break;
                case Plan: z = XYZ.BasisZ; y = facing; break;
                default: z = facing; y = XYZ.BasisZ; break;     // front: the viewer stands in front of the element
            }
            XYZ x = y.CrossProduct(z).Normalize();
            var t = Transform.Identity;
            t.Origin = center;
            t.BasisX = x;
            t.BasisY = y;
            t.BasisZ = z;
            return t;
        }

        /// <summary>Extents of the element in a view frame (local coordinates).</summary>
        private static void LocalExtents(BoundingBoxXYZ bb, Transform frame, out XYZ min, out XYZ max)
        {
            Transform inv = frame.Inverse;
            double[] mn = { double.MaxValue, double.MaxValue, double.MaxValue }, mx = { double.MinValue, double.MinValue, double.MinValue };
            for (int i = 0; i < 8; i++)
            {
                var pt = new XYZ((i & 1) == 0 ? bb.Min.X : bb.Max.X, (i & 2) == 0 ? bb.Min.Y : bb.Max.Y, (i & 4) == 0 ? bb.Min.Z : bb.Max.Z);
                XYZ l = inv.OfPoint(pt);
                mn[0] = Math.Min(mn[0], l.X); mn[1] = Math.Min(mn[1], l.Y); mn[2] = Math.Min(mn[2], l.Z);
                mx[0] = Math.Max(mx[0], l.X); mx[1] = Math.Max(mx[1], l.Y); mx[2] = Math.Max(mx[2], l.Z);
            }
            min = new XYZ(mn[0], mn[1], mn[2]);
            max = new XYZ(mx[0], mx[1], mx[2]);
        }

        /// <summary>Sizes of each chosen view (no view is created yet) — used to pick the scale.</summary>
        private static List<Comp> ModelComps(Document doc, LegendCandidate type, LegendTemplate t)
        {
            var list = new List<Comp>();
            Element inst = doc.GetElement(type.InstanceId);
            BoundingBoxXYZ bb = inst?.get_BoundingBox(null);
            if (bb == null) return list;
            XYZ center = (bb.Min + bb.Max) / 2;
            foreach (string key in DirectionOrder.Where(k => t.Views.Contains(k)))
            {
                LocalExtents(bb, ViewFrame(inst, key, center), out XYZ mn, out XYZ mx);
                list.Add(new Comp { Key = key, E = null, W = mx.X - mn.X, H = mx.Y - mn.Y });
            }
            return list;
        }

        /// <summary>Creates the sections of one type (named "{ficha} - Frontal"...).</summary>
        private static void CreateModelViews(Ctx c, LegendProbe p, LegendTemplate t, LegendCandidate type, int scale,
                                             List<KeyValuePair<string, View>> output, LegendResult res)
        {
            Document doc = c.Doc;
            if (p.SectionTypeId == ElementId.InvalidElementId)
            {
                res.Problems.Add($"{type.Title}: o projeto não tem tipo de vista de corte para desenhar a partir do modelo.");
                return;
            }
            Element inst = doc.GetElement(type.InstanceId);
            BoundingBoxXYZ bb = inst?.get_BoundingBox(null);
            if (bb == null)
            {
                res.Problems.Add($"{type.Title}: nenhuma instância no modelo para desenhar.");
                return;
            }
            XYZ center = (bb.Min + bb.Max) / 2;
            double margin = Math.Max(3, t.ViewGapMm * 0.6) * scale / 304.8;
            var keep = new HashSet<ElementId> { inst.Id };
            if (inst is FamilyInstance fi)
                foreach (ElementId sub in fi.GetSubComponentIds()) keep.Add(sub);

            foreach (string key in DirectionOrder.Where(k => t.Views.Contains(k)))
            {
                try
                {
                    Transform frame = ViewFrame(inst, key, center);
                    LocalExtents(bb, frame, out XYZ mn, out XYZ mx);
                    var box = new BoundingBoxXYZ
                    {
                        Transform = frame,
                        // crop around the element; the cut plane (Max.Z) stays in front of it, except
                        // for "Corte", which cuts through the middle
                        Min = new XYZ(mn.X - margin, mn.Y - margin, mn.Z - margin),
                        Max = new XYZ(mx.X + margin, mx.Y + margin, key == Section ? 0 : mx.Z + margin)
                    };
                    ViewSection v = ViewSection.CreateSection(doc, p.SectionTypeId, box);
                    RevitUtil.Rename(v, c.View.Name + " - " + DirectionLabel(key));
                    try { v.Scale = scale; } catch { }
                    try { v.DetailLevel = (ViewDetailLevel)Math.Max(1, Math.Min(3, t.DetailLevel)); } catch { }
                    try { v.DisplayStyle = DisplayStyle.HLR; } catch { }
                    try { v.CropBoxActive = true; v.CropBoxVisible = false; } catch { }
                    // the view shows the chosen phase (the instance drawn exists in it)
                    if (HasPhase) { try { v.get_Parameter(BuiltInParameter.VIEW_PHASE)?.Set(PhaseId); } catch { } }
                    RevitUtil.SetAnnotationCrop(v, false);
                    doc.Regenerate();

                    // only the element
                    var hide = new FilteredElementCollector(doc, v.Id).WhereElementIsNotElementType().ToElements()
                        .Where(e => !keep.Contains(e.Id) && e.Category != null && e.CanBeHidden(v)).Select(e => e.Id).ToList();
                    if (hide.Count > 0) { try { v.HideElements(hide); } catch (Exception ex) { Logger.Warn("[JA Tools] Ocultar na vista: " + ex.Message); } }

                    if (inst is FamilyInstance f) AddModelDimensions(c, v, f, key, t, mn, mx, frame, scale, WantsDepth(t, type));
                    output.Add(new KeyValuePair<string, View>(key, v));
                    res.ModelViews.Add(v);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[JA Tools] Vista {DirectionLabel(key)} de {type.Title}: {ex.Message}");
                    res.Problems.Add($"{type.Title}: vista {DirectionLabel(key)} não criada ({ex.Message}).");
                }
            }
        }

        /// <summary>Overall dimensions from the family's reference planes (when the family has them).</summary>
        private static void AddModelDimensions(Ctx c, View v, FamilyInstance fi, string key, LegendTemplate t,
                                               XYZ mn, XYZ mx, Transform frame, int scale, bool depth)
        {
            Document doc = c.Doc;
            double off = 5 * scale / 304.8;
            Func<FamilyInstanceReferenceType, Reference> one = rt =>
            {
                try { return fi.GetReferences(rt).FirstOrDefault(); } catch { return null; }
            };
            Reference left = one(FamilyInstanceReferenceType.Left), right = one(FamilyInstanceReferenceType.Right);
            Reference front = one(FamilyInstanceReferenceType.Front), back = one(FamilyInstanceReferenceType.Back);
            Reference top = one(FamilyInstanceReferenceType.Top), bottom = one(FamilyInstanceReferenceType.Bottom);

            // horizontal reference pair and vertical pair for this direction
            Reference h1, h2, v1, v2;
            bool horizIsWidth;
            switch (key)
            {
                case Plan: h1 = left; h2 = right; v1 = front; v2 = back; horizIsWidth = true; break;
                case Left:
                case Right:
                case Section: h1 = front; h2 = back; v1 = bottom; v2 = top; horizIsWidth = false; break;
                default: h1 = left; h2 = right; v1 = bottom; v2 = top; horizIsWidth = true; break;
            }
            XYZ o = v.Origin, n = v.ViewDirection;
            Func<double, double, XYZ> world = (lx, ly) =>
            {
                XYZ pt = frame.OfPoint(new XYZ(lx, ly, 0));
                return pt - n.Multiply(n.DotProduct(pt - o));     // onto the view plane
            };
            bool wantH = horizIsWidth ? t.DimWidth : depth;
            bool wantV = key == Plan ? depth : t.DimHeight;
            if (wantH && h1 != null && h2 != null)
                TryDimRefs(c, v, Line.CreateBound(world(mn.X, mn.Y - off), world(mx.X, mn.Y - off)), h1, h2);
            if (wantV && v1 != null && v2 != null)
                TryDimRefs(c, v, Line.CreateBound(world(mx.X + off, mn.Y), world(mx.X + off, mx.Y)), v1, v2);
        }

        private static void TryDimRefs(Ctx c, View v, Line line, Reference a, Reference b)
        {
            try
            {
                var ra = new ReferenceArray();
                ra.Append(a);
                ra.Append(b);
                if (c.DimType != null) c.Doc.Create.NewDimension(v, line, ra, c.DimType);
                else c.Doc.Create.NewDimension(v, line, ra);
            }
            catch (Exception ex) { Logger.Debug("[JA Tools] Cota da vista do modelo: " + ex.Message); }
        }
    }
}
