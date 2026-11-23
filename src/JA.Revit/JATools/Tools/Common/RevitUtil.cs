using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace RevitDwgUnifier.Tools.Common
{
    /// <summary>Revit helpers shared by the JA Tools buttons. Lengths in the UI are meters;
    /// Revit works in feet internally.</summary>
    public static class RevitUtil
    {
        public const double FeetPerMeter = 1.0 / 0.3048;
        public static double M(double meters) => meters * FeetPerMeter;
        public static double ToM(double feet) => feet * 0.3048;

        // ------------------------------------------------------------------ view types / templates

        public static ViewFamilyType FamilyType(Document doc, ViewFamily family) =>
            new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(t => t.ViewFamily == family)
            ?? throw new InvalidOperationException($"O projeto não tem nenhum tipo de vista '{family}'.");

        /// <summary>View templates usable for views of <paramref name="viewType"/> (by name).</summary>
        public static List<View> Templates(Document doc, params ViewType[] viewTypes) =>
            new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => v.IsTemplate && viewTypes.Contains(v.ViewType))
                .OrderBy(v => v.Name).ToList();

        /// <summary>"(nenhum)" + template names, for a ChoiceField.</summary>
        public static List<string> TemplateChoices(IEnumerable<View> templates) =>
            new[] { "(nenhum)" }.Concat(templates.Select(t => t.Name)).ToList();

        public static ElementId TemplateFromChoice(IList<View> templates, int choiceIndex) =>
            choiceIndex >= 1 && choiceIndex <= templates.Count ? templates[choiceIndex - 1].Id : ElementId.InvalidElementId;

        /// <summary>Scale first (a template may lock it), then the template.</summary>
        public static void Configure(View v, int scale, ElementId templateId)
        {
            if (scale > 0) { try { v.Scale = scale; } catch { } }
            if (templateId != null && templateId != ElementId.InvalidElementId)
            {
                try { v.ViewTemplateId = templateId; } catch { }
            }
        }

        public static string UniqueViewName(Document doc, string wanted)
        {
            HashSet<string> names = ViewNames(doc);
            string clean = Sanitize(wanted);
            string name = clean;
            for (int i = 2; names.Contains(name); i++) name = $"{clean} ({i})";
            return name;
        }

        /// <summary>Names of the views of the document, read once per tool run (renaming 6 views per
        /// room no longer walks every view of the project each time).</summary>
        private static HashSet<string> ViewNames(Document doc) =>
            SessionCache.Get("viewnames|" + doc.GetHashCode(), () =>
                new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Select(v => v.Name), StringComparer.OrdinalIgnoreCase));

        public static void Rename(View v, string wanted)
        {
            Document doc = v.Document;
            try
            {
                string n = UniqueViewName(doc, wanted);
                v.Name = n;
                ViewNames(doc).Add(n);
            }
            catch
            {
                // a name taken outside this tool: read the names again and retry once
                SessionCache.Remove("viewnames|" + doc.GetHashCode());
                try
                {
                    string n = UniqueViewName(doc, wanted);
                    v.Name = n;
                    ViewNames(doc).Add(n);
                }
                catch { }
            }
        }

        /// <summary>Names may not contain { } [ ] | ; &lt; &gt; ? ` ~ \ :</summary>
        public static string Sanitize(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "Vista";
            var chars = s.Select(c => "{}[]|;<>?`~\\:".IndexOf(c) >= 0 ? '-' : c).ToArray();
            return new string(chars).Trim();
        }

        // ------------------------------------------------------------------ levels / rooms

        public static List<Level> Levels(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();

        public static List<Room> Rooms(Document doc) =>
            new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
                .OfType<Room>().Where(r => r.Area > 1e-6 && r.Location != null)
                .OrderBy(r => r.Level?.Elevation ?? 0).ThenBy(r => r.Number, new NaturalStringComparer()).ToList();

        public static string RoomName(Room r) => r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "";

        public static string RoomLabel(Room r) =>
            $"{r.Number} - {RoomName(r)}" + (r.Level != null ? $"  ({r.Level.Name})" : "");

        public static XYZ RoomPoint(Room r) => (r.Location as LocationPoint)?.Point;

        /// <summary>
        /// Boundary loops of a room, read once per tool run (SessionCache): Detalhar Ambientes needs the
        /// same boundary for the box, the crop, the wall thickness, the preview sketch, the dimension
        /// directions... The key carries the room's area, so a room changed during the run is read again.
        /// </summary>
        public static IList<IList<BoundarySegment>> Boundary(Room r, SpatialElementBoundaryLocation where = SpatialElementBoundaryLocation.Finish)
        {
            if (r == null) return new List<IList<BoundarySegment>>();
            string key = "boundary|" + r.Document.GetHashCode() + "|" + r.Id.Value + "|" + where + "|" + r.Area.ToString("R");
            return SessionCache.Get(key, () =>
            {
                try { return r.GetBoundarySegments(new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = where }) ?? new List<IList<BoundarySegment>>(); }
                catch { return (IList<IList<BoundarySegment>>)new List<IList<BoundarySegment>>(); }
            });
        }

        /// <summary>Room box from its boundary (more exact than the element bounding box) with
        /// Z from the level to the room's upper limit.</summary>
        public static BoundingBoxXYZ RoomBox(Room r)
        {
            if (r == null) return null;
            // once per tool run per room (the key changes if the room changes); callers only read it
            string key = "roombox|" + r.Document.GetHashCode() + "|" + r.Id.Value + "|" + r.Area.ToString("R") + "|" +
                         r.BaseOffset.ToString("R") + "|" + r.UnboundedHeight.ToString("R");
            if (SessionCache.TryGet(key, out BoundingBoxXYZ cached)) return cached;
            BoundingBoxXYZ box = ComputeRoomBox(r);
            if (box != null) SessionCache.Set(key, box);
            return box;
        }

        private static BoundingBoxXYZ ComputeRoomBox(Room r)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            var segs = Boundary(r);
            if (segs != null)
                foreach (var loop in segs)
                    foreach (var s in loop)
                        foreach (XYZ p in s.GetCurve().Tessellate())
                        {
                            minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y);
                            maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
                        }
            if (minX > maxX)
            {
                BoundingBoxXYZ bb = r.get_BoundingBox(null);
                if (bb == null) return null;
                minX = bb.Min.X; minY = bb.Min.Y; maxX = bb.Max.X; maxY = bb.Max.Y;
            }
            double z0 = r.Level?.ProjectElevation ?? 0;
            double h = r.UnboundedHeight > 1e-6 ? r.UnboundedHeight : M(3.0);
            return new BoundingBoxXYZ { Min = new XYZ(minX, minY, z0 + r.BaseOffset), Max = new XYZ(maxX, maxY, z0 + r.BaseOffset + h) };
        }

        /// <summary>An existing (non-template) floor plan of the level, or a new one.</summary>
        public static ViewPlan PlanOf(Document doc, Level level, ViewFamily family = ViewFamily.FloorPlan)
        {
            var existing = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .FirstOrDefault(v => !v.IsTemplate && v.GenLevel != null && v.GenLevel.Id == level.Id &&
                                     ((family == ViewFamily.FloorPlan && v.ViewType == Autodesk.Revit.DB.ViewType.FloorPlan) ||
                                      (family == ViewFamily.CeilingPlan && v.ViewType == Autodesk.Revit.DB.ViewType.CeilingPlan)));
            return existing ?? ViewPlan.Create(doc, FamilyType(doc, family).Id, level.Id);
        }

        // ------------------------------------------------------------------ crop

        public static void CropPlan(View v, double minX, double minY, double maxX, double maxY, bool showCrop)
        {
            BoundingBoxXYZ cb = v.CropBox;
            v.CropBox = new BoundingBoxXYZ { Transform = Transform.Identity, Min = new XYZ(minX, minY, cb.Min.Z), Max = new XYZ(maxX, maxY, cb.Max.Z) };
            v.CropBoxActive = true;
            v.CropBoxVisible = showCrop;
            SetAnnotationCrop(v, true);
        }

        public static void SetAnnotationCrop(View v, bool on)
        {
            try { v.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE)?.Set(on ? 1 : 0); } catch { }
        }

        // ------------------------------------------------------------------ tags

        /// <summary>Tags every room of the view at its location point. Returns tags created.</summary>
        public static int TagRooms(Document doc, View view, IEnumerable<Room> rooms, List<string> problems)
        {
            int n = 0;
            foreach (Room r in rooms)
            {
                XYZ p = RoomPoint(r);
                if (p == null) continue;
                try
                {
                    if (doc.Create.NewRoomTag(new LinkElementId(r.Id), new UV(p.X, p.Y), view.Id) != null) n++;
                }
                catch (Exception ex)
                {
                    AddOnce(problems, "Etiqueta de ambiente: " + ex.Message);
                }
            }
            return n;
        }

        /// <summary>Tags every element of a category visible in the view (needs a tag family of
        /// that category loaded in the project). Returns tags created.</summary>
        public static int TagCategory(Document doc, View view, BuiltInCategory cat, List<string> problems,
                                      Func<Element, XYZ> where = null, bool leader = false)
        {
            int n = 0, unmarked = 0;
            var elements = new FilteredElementCollector(doc, view.Id).OfCategory(cat).WhereElementIsNotElementType().ToElements();
            // the office's identifier for this category (loaded now if the project does not have it)
            ElementId jaTag = null;
            try { jaTag = TagLibrary.Ensure(doc, Category.GetCategory(doc, cat), problems); } catch { }
            if (where == null && view is ViewPlan)
            {
                // plans: each tag where a draftsman would put it (in front of doors, beside basins,
                // along long pieces), away from walls, pieces and other annotations
                var marked = new List<Element>();
                foreach (Element e in elements)
                {
                    if (HasMark(doc, e)) marked.Add(e); else unmarked++;
                }
                n = TagPlacer.Tag(doc, view, marked, jaTag, null, leader, problems, CategoryName(doc, cat)).Count;
                if (unmarked > 0) AddOnce(problems, $"{unmarked} elemento(s) de {CategoryName(doc, cat)} sem Marca nem Marca de tipo não foram etiquetados.");
                return n;
            }
            foreach (Element e in elements)
            {
                if (!HasMark(doc, e)) { unmarked++; continue; }
                try
                {
                    XYZ p = where?.Invoke(e) ?? Center(e, view);
                    if (p == null) continue;
                    var tag = IndependentTag.Create(doc, view.Id, new Reference(e), leader, TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal, p);
                    if (tag != null && jaTag != null) { try { tag.ChangeTypeId(jaTag); } catch { } }
                    if (tag != null) n++;
                }
                catch (Exception ex)
                {
                    AddOnce(problems, $"Etiqueta de {CategoryName(doc, cat)}: {ex.Message} (carregue uma família de etiqueta dessa categoria).");
                    break; // same failure for every element of the category
                }
            }
            if (unmarked > 0) AddOnce(problems, $"{unmarked} elemento(s) de {CategoryName(doc, cat)} sem Marca nem Marca de tipo não foram etiquetados.");
            return n;
        }

        /// <summary>
        /// Several categories of a plan tagged in one batch (one regeneration, walls and pieces read
        /// once): the result per category is the same as calling TagCategory for each one.
        /// Other views: one TagCategory per category. Returns the tags made per category.
        /// </summary>
        public static Dictionary<BuiltInCategory, int> TagCategories(Document doc, View view, IEnumerable<BuiltInCategory> cats, List<string> problems)
        {
            var made = new Dictionary<BuiltInCategory, int>();
            if (!(view is ViewPlan))
            {
                foreach (BuiltInCategory c in cats) made[c] = TagCategory(doc, view, c, problems);
                return made;
            }
            var jobs = new List<KeyValuePair<BuiltInCategory, TagPlacer.TagJob>>();
            foreach (BuiltInCategory cat in cats)
            {
                ElementId jaTag = null;
                try { jaTag = TagLibrary.Ensure(doc, Category.GetCategory(doc, cat), problems); } catch { }
                var marked = new List<Element>();
                int unmarked = 0;
                foreach (Element e in new FilteredElementCollector(doc, view.Id).OfCategory(cat).WhereElementIsNotElementType())
                {
                    if (HasMark(doc, e)) marked.Add(e); else unmarked++;
                }
                if (unmarked > 0) AddOnce(problems, $"{unmarked} elemento(s) de {CategoryName(doc, cat)} sem Marca nem Marca de tipo não foram etiquetados.");
                jobs.Add(new KeyValuePair<BuiltInCategory, TagPlacer.TagJob>(cat,
                    new TagPlacer.TagJob { Elements = marked, TypeId = jaTag, What = CategoryName(doc, cat) }));
            }
            TagPlacer.TagMany(doc, view, jobs.Select(j => j.Value), null, false, problems);
            foreach (var j in jobs) made[j.Key] = j.Value.Created;
            return made;
        }

        /// <summary>The element has a Mark (instance) or a Type Mark: only those are tagged
        /// (a tag of an element without mark shows "?" or nothing).</summary>
        public static bool HasMark(Document doc, Element e)
        {
            try
            {
                string mark = e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();
                if (!string.IsNullOrWhiteSpace(mark)) return true;
                Element type = doc.GetElement(e.GetTypeId());
                string typeMark = type?.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_MARK)?.AsString();
                if (!string.IsNullOrWhiteSpace(typeMark)) return true;
                // doors / windows: their type mark parameter
                string winMark = type?.get_Parameter(BuiltInParameter.WINDOW_TYPE_ID)?.AsString();
                return !string.IsNullOrWhiteSpace(winMark);
            }
            catch { return false; }
        }

        public static XYZ Center(Element e, View view = null)
        {
            if (e.Location is LocationPoint lp) return lp.Point;
            BoundingBoxXYZ bb = e.get_BoundingBox(view) ?? e.get_BoundingBox(null);
            return bb == null ? null : (bb.Min + bb.Max) / 2;
        }

        public static string CategoryName(Document doc, BuiltInCategory cat)
        {
            try { return Category.GetCategory(doc, cat)?.Name ?? cat.ToString(); } catch { return cat.ToString(); }
        }

        public static void AddOnce(List<string> list, string text)
        {
            if (list != null && !list.Contains(text)) list.Add(text);
        }

        // ------------------------------------------------------------------ dimensions

        public static Dimension CreateDimension(Document doc, View view, Line line, IEnumerable<Reference> refs, DimensionType type = null)
        {
            var ra = new ReferenceArray();
            foreach (Reference r in refs) ra.Append(r);
            if (ra.Size < 2) return null;
            Dimension d = type != null ? doc.Create.NewDimension(view, line, ra, type) : doc.Create.NewDimension(view, line, ra);
            return d;
        }

        public static List<DimensionType> LinearDimensionTypes(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(DimensionType)).Cast<DimensionType>()
                .Where(t => t.StyleType == DimensionStyleType.Linear && !string.IsNullOrEmpty(t.Name))
                .OrderBy(t => t.Name).ToList();

        // ------------------------------------------------------------------ picking

        /// <summary>Picks a point in the active view, giving it a work plane if it has none.</summary>
        public static XYZ PickPoint(UIDocument uidoc, string prompt)
        {
            ToolProgress.Interactive();
            EnsureWorkPlane(uidoc);
            return uidoc.Selection.PickPoint(ObjectSnapTypes.Endpoints | ObjectSnapTypes.Midpoints | ObjectSnapTypes.Nearest |
                                             ObjectSnapTypes.Intersections | ObjectSnapTypes.Perpendicular, prompt);
        }

        public static void EnsureWorkPlane(UIDocument uidoc)
        {
            View v = uidoc.ActiveView;
            if (v.SketchPlane != null) return;
            Document doc = uidoc.Document;
            using (var t = new Transaction(doc, "JA Tools - plano de trabalho"))
            {
                t.Start();
                Plane plane = v is ViewPlan vp && vp.GenLevel != null
                    ? Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, vp.GenLevel.ProjectElevation))
                    : Plane.CreateByNormalAndOrigin(v.ViewDirection, v.Origin);
                v.SketchPlane = SketchPlane.Create(doc, plane);
                t.Commit();
            }
        }

        public static XYZ Flat(XYZ p) => new XYZ(p.X, p.Y, 0);
    }

    /// <summary>"P2" before "P10".</summary>
    public sealed class NaturalStringComparer : IComparer<string>
    {
        public int Compare(string a, string b)
        {
            a = a ?? ""; b = b ?? "";
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    int si = i, sj = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;
                    string na = a.Substring(si, i - si).TrimStart('0'), nb = b.Substring(sj, j - sj).TrimStart('0');
                    int c = na.Length != nb.Length ? na.Length.CompareTo(nb.Length) : string.CompareOrdinal(na, nb);
                    if (c != 0) return c;
                }
                else
                {
                    int c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                    if (c != 0) return c;
                    i++; j++;
                }
            }
            return (a.Length - i).CompareTo(b.Length - j);
        }
    }
}
