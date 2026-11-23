using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using RevitDwgUnifier.Tools.Common;

namespace RevitDwgUnifier.Tools.Parameters
{
    /// <summary>
    /// Moves every element of one level to another level WITHOUT moving it: the level parameter
    /// changes and the matching offset is corrected by the difference between the two levels, so
    /// walls, floors, ceilings, roofs, columns, stairs, beams and level-based families stay exactly
    /// where they are (and the dimensions, tags and other annotations that reference them stay).
    /// Doors / windows and other hosted pieces follow their host. Rooms cannot change level in
    /// Revit: they are counted and reported. Inside an open transaction.
    /// </summary>
    public static class LevelRehost
    {
        public sealed class Outcome
        {
            public int Moved, Failed, Rooms, EmGrupos, JaNoNivel;
            public readonly List<string> Problems = new List<string>();
        }

        /// <summary>level parameter → offset parameters (the first one present and editable is
        /// corrected; none = the level change does not move the element).</summary>
        private static readonly Tuple<BuiltInParameter, BuiltInParameter[]>[] Pairs = BuildPairs();

        // names parsed at run time: a parameter missing in some Revit version is just skipped
        private static Tuple<BuiltInParameter, BuiltInParameter[]>[] BuildPairs()
        {
            string[][] map =
            {
                new[] { "WALL_BASE_CONSTRAINT", "WALL_BASE_OFFSET" },
                new[] { "WALL_HEIGHT_TYPE", "WALL_TOP_OFFSET" },
                new[] { "LEVEL_PARAM", "FLOOR_HEIGHTABOVELEVEL_PARAM", "CEILING_HEIGHTABOVELEVEL_PARAM" },
                new[] { "ROOF_BASE_LEVEL_PARAM", "ROOF_LEVEL_OFFSET_PARAM" },
                new[] { "ROOF_CONSTRAINT_LEVEL_PARAM", "ROOF_CONSTRAINT_OFFSET_PARAM" },
                new[] { "FAMILY_LEVEL_PARAM", "INSTANCE_ELEVATION_PARAM", "INSTANCE_FREE_HOST_OFFSET_PARAM" },
                new[] { "FAMILY_BASE_LEVEL_PARAM", "FAMILY_BASE_LEVEL_OFFSET_PARAM" },
                new[] { "FAMILY_TOP_LEVEL_PARAM", "FAMILY_TOP_LEVEL_OFFSET_PARAM" },
                new[] { "STAIRS_BASE_LEVEL_PARAM", "STAIRS_BASE_OFFSET" },
                new[] { "STAIRS_TOP_LEVEL_PARAM", "STAIRS_TOP_OFFSET" },
                new[] { "INSTANCE_REFERENCE_LEVEL_PARAM", "STRUCTURAL_BEAM_END0_ELEVATION", "STRUCTURAL_BEAM_END1_ELEVATION" },
                new[] { "INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM" }
            };
            var list = new List<Tuple<BuiltInParameter, BuiltInParameter[]>>();
            foreach (string[] m in map)
            {
                if (!Enum.TryParse(m[0], out BuiltInParameter level)) continue;
                var offs = new List<BuiltInParameter>();
                foreach (string o in m.Skip(1))
                    if (Enum.TryParse(o, out BuiltInParameter b)) offs.Add(b);
                list.Add(Tuple.Create(level, offs.ToArray()));
            }
            return list.ToArray();
        }

        private static bool Is(ElementId id, Level l) => id != null && l != null && id.Value == l.Id.Value;

        /// <summary>Elements referring to the level by one of the parameters above.</summary>
        private static List<Element> Candidates(Document doc, Level from)
        {
            var list = new List<Element>();
            foreach (Element e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                if (e is Level || e is View || e is SpatialElement || e.Category == null) continue;
                try
                {
                    if (Is(e.LevelId, from) || Pairs.Any(p => Is(e.get_Parameter(p.Item1)?.AsElementId(), from))) list.Add(e);
                }
                catch { }
            }
            return list;
        }

        /// <summary>The top constraints: changed only when asked (or when a specific source level is given).</summary>
        private static bool Topo(BuiltInParameter b) =>
            b == BuiltInParameter.WALL_HEIGHT_TYPE || b == BuiltInParameter.FAMILY_TOP_LEVEL_PARAM ||
            b == BuiltInParameter.STAIRS_TOP_LEVEL_PARAM || b == BuiltInParameter.ROOF_CONSTRAINT_LEVEL_PARAM;

        /// <summary>
        /// The level an element is on: its base level parameter (walls, floors, families, stairs, roofs, schedule level…),
        /// the level of a room, the element's own level, else the level of its host. Null when it has none.
        /// </summary>
        public static Level NivelDe(Element e, int profundidade = 0)
        {
            if (e == null) return null;
            Document doc = e.Document;
            try
            {
                if (e is SpatialElement se && doc.GetElement(se.LevelId) is Level ls) return ls;
                foreach (var p in Pairs)
                {
                    if (Topo(p.Item1)) continue;
                    Parameter lp = e.get_Parameter(p.Item1);
                    if (lp != null && lp.StorageType == StorageType.ElementId && doc.GetElement(lp.AsElementId()) is Level l) return l;
                }
                if (doc.GetElement(e.LevelId) is Level own) return own;
                if (e is FamilyInstance fi && fi.Host != null && profundidade < 2) return NivelDe(fi.Host, profundidade + 1);
            }
            catch { }
            return null;
        }

        /// <summary>
        /// The base reference of an element: its base level and the offset from it (wall base offset, floor height
        /// offset, family elevation, stair base offset…; a room's base offset). False when it has no base level.
        /// </summary>
        public static bool ReferenciaDe(Element e, out Level nivel, out double deslocamento)
        {
            nivel = null; deslocamento = 0;
            if (e == null) return false;
            Document doc = e.Document;
            try
            {
                if (e is SpatialElement se && doc.GetElement(se.LevelId) is Level ls)
                {
                    nivel = ls;
                    if (e is Room rm) deslocamento = rm.BaseOffset;
                    return true;
                }
                foreach (var p in Pairs)
                {
                    if (Topo(p.Item1)) continue;
                    Parameter lp = e.get_Parameter(p.Item1);
                    if (lp == null || lp.StorageType != StorageType.ElementId || !(doc.GetElement(lp.AsElementId()) is Level l)) continue;
                    nivel = l;
                    foreach (BuiltInParameter o in p.Item2)
                    {
                        Parameter op = e.get_Parameter(o);
                        if (op != null && op.StorageType == StorageType.Double && op.HasValue) { deslocamento = op.AsDouble(); break; }
                    }
                    return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>Has at least one editable level parameter (the elements this tool can change).</summary>
        public static bool TemNivel(Element e)
        {
            if (e == null || e is Level || e is View || e is SpatialElement || e.Category == null || e is ElementType) return false;
            try
            {
                return Pairs.Any(p =>
                {
                    Parameter lp = e.get_Parameter(p.Item1);
                    return lp != null && !lp.IsReadOnly && lp.StorageType == StorageType.ElementId && e.Document.GetElement(lp.AsElementId()) is Level;
                });
            }
            catch { return false; }
        }

        /// <summary>
        /// Puts the given elements on level <paramref name="to"/> without moving them. from = null:
        /// whatever level each one is on (base constraints only, plus the top ones when
        /// <paramref name="topo"/>); else only the parameters that point at <paramref name="from"/>.
        /// Elements in groups are skipped (Revit only edits them inside "Editar grupo"). Inside an open transaction.
        /// </summary>
        public static Outcome Revincular(Document doc, IList<Element> elements, Level to, Level from, bool topo, string progresso, bool pularGrupos = true)
        {
            var res = new Outcome();
            int done = 0;
            foreach (Element e in elements)
            {
                if (++done % 50 == 0) ToolProgress.Report(done, elements.Count, progresso);
                if (pularGrupos && e.GroupId != null && e.GroupId != ElementId.InvalidElementId) { res.EmGrupos++; continue; }
                bool any = false, failed = false, ja = false;
                foreach (var p in Pairs)
                {
                    Parameter lp = e.get_Parameter(p.Item1);
                    if (lp == null || lp.IsReadOnly || lp.StorageType != StorageType.ElementId) continue;
                    if (!(doc.GetElement(lp.AsElementId()) is Level atual)) continue;
                    if (from != null ? !Is(atual.Id, from) : (!topo && Topo(p.Item1))) continue;
                    if (Is(atual.Id, to)) { ja = true; continue; }
                    double delta = atual.Elevation - to.Elevation;      // added to each offset: the element stays where it is
                    Parameter op = p.Item2.Select(b => e.get_Parameter(b)).FirstOrDefault(x => x != null && !x.IsReadOnly && x.StorageType == StorageType.Double);
                    // a beam has two end offsets: both are corrected
                    var ops = p.Item1 == BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM
                        ? p.Item2.Select(b => e.get_Parameter(b)).Where(x => x != null && !x.IsReadOnly && x.StorageType == StorageType.Double).ToList()
                        : (op != null ? new List<Parameter> { op } : new List<Parameter>());
                    var old = ops.Select(x => x.AsDouble()).ToList();
                    try
                    {
                        if (!lp.Set(to.Id)) { failed = true; continue; }
                        for (int i = 0; i < ops.Count; i++) ops[i].Set(old[i] + delta);
                        any = true;
                    }
                    catch (Exception ex)
                    {
                        failed = true;
                        RevitUtil.AddOnce(res.Problems, $"{e.Category?.Name} {e.Id.Value}: {ex.Message}");
                    }
                }
                if (any) res.Moved++;
                else if (failed) res.Failed++;
                else if (ja) res.JaNoNivel++;
            }
            return res;
        }

        public static Outcome Move(Document doc, Level from, Level to)
        {
            Outcome res = Revincular(doc, Candidates(doc, from), to, from, true, $"Revinculando {from.Name} → {to.Name}", false);
            try
            {
                res.Rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
                    .OfType<Room>().Count(r => Is(r.LevelId, from));
            }
            catch { }
            return res;
        }
    }
}
