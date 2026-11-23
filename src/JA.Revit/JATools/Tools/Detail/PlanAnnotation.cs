using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using RevitDwgUnifier.Tools.Common;

namespace RevitDwgUnifier.Tools.Detail
{
    /// <summary>A category to tag in plan, with the tag types the user can choose.</summary>
    public sealed class TagCatOption
    {
        public BuiltInCategory Cat;
        public string Name;
        public List<FamilySymbol> Types;
        public OptionsDialog.ChoiceField Field;
    }

    /// <summary>
    /// The plan annotation shared by Detalhar Ambientes and Anotar Vista: element tags per
    /// category (type chosen in the dialog; the office identifiers by default), room tags, and the
    /// rule of which pieces belong to a room.
    /// </summary>
    public static class PlanAnnotation
    {
        public static readonly string[] TagCatNames =
        {
            "OST_Doors", "OST_Windows", "OST_PlumbingFixtures", "OST_Casework", "OST_Furniture", "OST_SpecialityEquipment",
            "OST_GenericModel", "OST_LightingFixtures", "OST_ElectricalFixtures", "OST_LightingDevices", "OST_PipeAccessory",
            "OST_PlumbingEquipment", "OST_StructuralColumns", "OST_Columns", "OST_StairsRailing"
        };

        /// <summary>The categories that have pieces in the project, each with its tag types.</summary>
        public static List<TagCatOption> Categories(Document doc)
        {
            var multiTags = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_MultiCategoryTags)
                .Cast<FamilySymbol>().OrderBy(s => s.FamilyName).ThenBy(s => s.Name).ToList();
            var list = new List<TagCatOption>();
            foreach (string name in TagCatNames)
            {
                if (!Enum.TryParse(name, out BuiltInCategory c)) continue;
                ElementId any = null;
                try { any = new FilteredElementCollector(doc).OfCategory(c).WhereElementIsNotElementType().FirstElementId(); } catch { }
                if (any == null || any == ElementId.InvalidElementId) continue;
                BuiltInCategory? tc = TagPlacer.TagCategoryFor(c);
                var types = new List<FamilySymbol>();
                if (tc.HasValue)
                    types.AddRange(new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(tc.Value)
                        .Cast<FamilySymbol>().OrderBy(s => s.FamilyName).ThenBy(s => s.Name));
                types.AddRange(multiTags);
                list.Add(new TagCatOption { Cat = c, Name = RevitUtil.CategoryName(doc, c), Types = types });
            }
            return list;
        }

        /// <summary>"Etiquetas por categoria" section: one choice per category.</summary>
        public static void AddFields(Document doc, OptionsDialog dlg, List<TagCatOption> cats, Func<bool> enabled)
        {
            if (cats.Count == 0) return;
            dlg.Section("Etiquetas por categoria");
            foreach (TagCatOption tc in cats)
            {
                bool ja = TagLibrary.TagCategoryOf(Category.GetCategory(doc, tc.Cat)).HasValue;
                var items = new List<string> { ja ? "Identificador JA (carregado se faltar)" : "(etiqueta padrão do projeto)" };
                items.AddRange(tc.Types.Select(s => s.FamilyName + " : " + s.Name +
                    (s.Category?.Id.Value == (long)BuiltInCategory.OST_MultiCategoryTags ? "  [multicategoria]" : "")));
                items.Add("(não etiquetar)");
                int def = 0;
                if (PadraoPlanta.EtiquetaCategoria.TryGetValue(tc.Cat.ToString(), out string[] nomes))
                {
                    int m = PadraoPlanta.Melhor(tc.Types.Select(s => s.FamilyName + " : " + s.Name).ToList(), nomes);
                    if (m >= 0) def = m + 1;
                }
                tc.Field = dlg.Choice("tag_" + tc.Cat, tc.Name, items, def);
                tc.Field.EnabledWhen(enabled);
            }
            dlg.Note("Elementos sem Marca nem Marca de tipo e elementos já etiquetados na vista são pulados. Portas e janelas: etiqueta em frente, " +
                     "no sentido da parede; peças compridas (tento, soleira): sobre a peça, no sentido dela; as demais ao lado, no espaço livre.");
        }

        /// <summary>
        /// Tags, in one batch, the marked and untagged pieces of every chosen category visible in the
        /// plan. rooms = null: every piece of the view; otherwise only the pieces of those rooms.
        /// Inside an open transaction. Returns the tags created.
        /// </summary>
        public static int TagElements(Document doc, ViewPlan plan, IList<Room> rooms, List<TagCatOption> cats, List<string> problems)
        {
            HashSet<ElementId> tagged = EtiquetarElementosCommand.TaggedIds(plan);
            var jobs = new List<TagPlacer.TagJob>();
            foreach (TagCatOption tc in cats)
            {
                if (tc.Field == null) continue;
                int idx = tc.Field.Index, last = tc.Field.Items.Count - 1;
                if (idx < 0 || idx == last) continue;
                ElementId typeId = null;
                bool multi = false;
                if (idx == 0)
                {
                    try { typeId = TagLibrary.Ensure(doc, Category.GetCategory(doc, tc.Cat), problems); } catch { }
                }
                else
                {
                    FamilySymbol s = tc.Types[idx - 1];
                    typeId = s.Id;
                    multi = s.Category?.Id.Value == (long)BuiltInCategory.OST_MultiCategoryTags;
                }
                var list = new List<Element>();
                int unmarked = 0;
                foreach (Element e in new FilteredElementCollector(doc, plan.Id).OfCategory(tc.Cat).WhereElementIsNotElementType())
                {
                    if (tagged.Contains(e.Id)) continue;
                    if (rooms != null && !rooms.Any(r => BelongsTo(e, r))) continue;
                    if (!RevitUtil.HasMark(doc, e)) { unmarked++; continue; }
                    list.Add(e);
                }
                if (unmarked > 0) RevitUtil.AddOnce(problems, $"{unmarked} elemento(s) de {tc.Name} sem Marca nem Marca de tipo não foram etiquetados.");
                if (list.Count > 0) jobs.Add(new TagPlacer.TagJob { Elements = list, TypeId = typeId, Multi = multi, What = tc.Name });
            }
            if (jobs.Count == 0) return 0;
            ToolProgress.Pulse($"{plan.Name}: posicionando {jobs.Sum(j => j.Elements.Count)} etiqueta(s)…");
            Room single = rooms != null && rooms.Count == 1 ? rooms[0] : null;
            return TagPlacer.TagMany(doc, plan, jobs, single, false, problems).Count;
        }

        /// <summary>A door/window in a wall of the room, or a piece whose centre (±15 cm) is in it.</summary>
        public static bool BelongsTo(Element e, Room r)
        {
            // quick reject: far from the room's box (no IsPointInRoom calls for the rest of the plan)
            BoundingBoxXYZ rb = RevitUtil.RoomBox(r);
            XYZ cc = RevitUtil.Center(e);
            double near = RevitUtil.M(1.0);
            if (rb != null && cc != null &&
                (cc.X < rb.Min.X - near || cc.X > rb.Max.X + near || cc.Y < rb.Min.Y - near || cc.Y > rb.Max.Y + near))
                return false;
            double z = (r.Level?.ProjectElevation ?? 0) + r.BaseOffset + RevitUtil.M(0.5);
            if (e is FamilyInstance fi && fi.Host is Wall &&
                (fi.Category?.Id.Value == (long)BuiltInCategory.OST_Doors || fi.Category?.Id.Value == (long)BuiltInCategory.OST_Windows))
            {
                Obb ob = TagPlacer.OpeningBox(fi);
                if (ob != null)
                {
                    foreach (int side in new[] { 1, -1 })
                    {
                        double d = ob.HV + RevitUtil.M(0.15);
                        try { if (r.IsPointInRoom(new XYZ(ob.CX - ob.VX * d * side, ob.CY - ob.VY * d * side, z))) return true; } catch { }
                    }
                    return false;
                }
            }
            XYZ c = RevitUtil.Center(e);
            if (c == null) return false;
            double k = RevitUtil.M(0.15);
            foreach (double[] o in new[] { new[] { 0.0, 0.0 }, new[] { k, 0 }, new[] { -k, 0 }, new[] { 0, k }, new[] { 0, -k } })
            {
                try { if (r.IsPointInRoom(new XYZ(c.X + o[0], c.Y + o[1], z))) return true; } catch { }
            }
            return false;
        }

        /// <summary>Room tag at the room point (type optional). Inside an open transaction.</summary>
        public static bool TagRoom(Document doc, View view, Room r, FamilySymbol type, List<string> problems)
        {
            XYZ p = RevitUtil.RoomPoint(r);
            if (p == null) return false;
            try
            {
                RoomTag rt = doc.Create.NewRoomTag(new LinkElementId(r.Id), new UV(p.X, p.Y), view.Id);
                if (rt != null && type != null) { try { rt.ChangeTypeId(type.Id); } catch { } }
                return rt != null;
            }
            catch (Exception ex)
            {
                RevitUtil.AddOnce(problems, "Etiqueta de ambiente: " + ex.Message);
                return false;
            }
        }

        /// <summary>Rooms that already have a room tag in the view.</summary>
        public static HashSet<ElementId> TaggedRooms(Document doc, View view)
        {
            var set = new HashSet<ElementId>();
            foreach (RoomTag rt in new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_RoomTags).OfType<RoomTag>())
            {
                try { set.Add(rt.TaggedLocalRoomId); } catch { }
            }
            return set;
        }

        /// <summary>The room an interior elevation looks into: the one that holds its marker.</summary>
        public static Room RoomOfElevation(Document doc, View ev, IEnumerable<Room> rooms)
        {
            XYZ o = ev.Origin;
            foreach (Room r in rooms)
            {
                try
                {
                    double z = (r.Level?.ProjectElevation ?? 0) + r.BaseOffset + RevitUtil.M(1.0);
                    if (r.IsPointInRoom(new XYZ(o.X, o.Y, z))) return r;
                }
                catch { }
            }
            return null;
        }
    }
}
