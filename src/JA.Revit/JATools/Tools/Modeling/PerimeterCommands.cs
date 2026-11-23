using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using RevitDwgUnifier.Tools.Common;

namespace RevitDwgUnifier.Tools.Modeling
{
    /// <summary>
    /// Tabica por Ambiente. Thin walls along the inside of each room's perimeter (finish face) at the
    /// height of its ceiling (ceiling trims). The strips are inset by half their thickness so their
    /// outer face touches the wall finish, corners are mitred, they are not room-bounding and they
    /// carry a comment for later selection / removal.
    /// </summary>
    /// <remarks>2.0: the rodapé half ("Rodapé e Tabica") left — the Rodapé of the JA Famílias tab does it
    /// better (discounts of doors, windows and openings, corners, purchase, locked to the wall). Forro e
    /// Sanca (JA Famílias) makes the ceiling with its tabica; this one adds tabica to ceilings made otherwise.
    /// The preferences of the tabica (type, height) are kept.</remarks>
    [Transaction(TransactionMode.Manual)]
    public sealed class RodapeTabicaCommand : ModelToolCommand
    {
        public override string Title => "Tabica por Ambiente";
        private const string PrefsKey = "RodapeTabica";

        protected override Result Run()
        {
            var types = new FilteredElementCollector(Doc).OfClass(typeof(WallType)).Cast<WallType>()
                .Where(t => t.Kind == WallKind.Basic).OrderBy(t => t.Width).ThenBy(t => t.Name).ToList();
            if (types.Count == 0) { Show("O projeto não tem tipos de parede básica."); return Result.Cancelled; }
            var typeNames = types.Select(t => $"{t.Name}  ({ModelUtil.Cm(t.Width)} cm)").ToList();

            var dlg = new OptionsDialog(Title, PrefsKey);
            var fRooms = AddRoomList(dlg);
            int guess = Math.Max(0, types.FindIndex(t => Fold(t.Name).Contains(Fold("tabica"))));
            var fTypeK = dlg.Choice("type.1", "Tipo de parede da tabica", typeNames, guess);
            var fHeightK = dlg.Number("height.1", "Altura (m)", 0.05, 0.001, 10);
            var fBelow = dlg.Number("below", "Começar abaixo do forro (m; 0 = na face do forro)", 0, -1, 1);
            dlg.Note("Crie um tipo de parede fino (ex.: \"TABICA 1 cm\") com o material desejado e escolha-o acima. " +
                     "A tabica vai na altura do forro de cada ambiente; ambientes sem forro são ignorados. " +
                     "Rodapé: use o Rodapé da aba JA Famílias. Forro novo com tabica e sanca: Forro e Sanca (JA Famílias).");
            if (!dlg.Show(Owner)) return Result.Cancelled;

            string Kind = "tabica";
            var ceilings = new FilteredElementCollector(Doc).OfClass(typeof(Ceiling)).Cast<Ceiling>().ToList();
            double? BaseOffset(Room r) => TabicaOffset(r, ceilings, fBelow.Value);
            const bool SkipDoors = false;

            WallType type = types[fTypeK.Index];
            double height = RevitUtil.M(fHeightK.Value);
            int n = 0, skipped = 0;
            using (var t = new Transaction(Doc, "JA Tools - " + Title))
            {
                t.Start();
                RevitDwgUnifier.Export.FailureSwallower.Attach(t);
                foreach (int i in fRooms.SelectedIndices)
                {
                    Room r = AllRooms[i];
                    ToolProgress.Report(fRooms.SelectedIndices.ToList().IndexOf(i), fRooms.SelectedIndices.Count(), RoomTitle(r));
                    if (r.Level == null) continue;
                    double? baseOff = BaseOffset(r);
                    if (baseOff == null) { skipped++; continue; }
                    double z = r.Level.ProjectElevation;

                    foreach (var segs in ModelUtil.Segments(r))
                    {
                        foreach (var piece in ModelUtil.InsetLoop(r, segs, type.Width / 2, z))
                        {
                            foreach (Curve c in SkipDoors ? CutDoors(piece.Item1, piece.Item2) : new List<Curve> { piece.Item1 })
                            {
                                if (c.Length < RevitUtil.M(0.02)) continue;
                                try
                                {
                                    Wall w = Wall.Create(Doc, c, type.Id, r.LevelId, height, baseOff.Value, false, false);
                                    w.get_Parameter(BuiltInParameter.WALL_ATTR_ROOM_BOUNDING)?.Set(0);
                                    w.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set($"JA Tools - {Kind} {RoomTitle(r)}");
                                    n++;
                                }
                                catch (Exception ex) { RevitUtil.AddOnce(Problems, $"Ambiente {r.Number}: {ex.Message}"); }
                            }
                        }
                    }
                }
                t.Commit();
            }
            Done(n, $"trecho(s) de {Kind} criado(s)" + (skipped > 0 ? $" ({skipped} ambiente(s) ignorado(s))" : ""));
            return Result.Succeeded;
        }

        /// <summary>Removes the parts of a straight strip that run along door openings of the
        /// wall that produced it.</summary>
        private List<Curve> CutDoors(Curve c, ElementId hostId)
        {
            var result = new List<Curve>();
            if (!(c is Line l) || !(Doc.GetElement(hostId) is Wall host)) { result.Add(c); return result; }

            XYZ a = l.GetEndPoint(0), d = l.Direction;
            double len = l.Length;
            var gaps = new List<Tuple<double, double>>();
            foreach (ElementId id in host.FindInserts(false, false, false, false))
            {
                if (!(Doc.GetElement(id) is FamilyInstance fi) || fi.Category == null ||
                    fi.Category.Id.Value != (long)BuiltInCategory.OST_Doors) continue;
                BoundingBoxXYZ bb = fi.get_BoundingBox(null);
                if (bb == null) continue;
                double lo = double.MaxValue, hi = double.MinValue;
                foreach (XYZ p in new[] { bb.Min, bb.Max, new XYZ(bb.Min.X, bb.Max.Y, 0), new XYZ(bb.Max.X, bb.Min.Y, 0) })
                {
                    double s = (RevitUtil.Flat(p) - RevitUtil.Flat(a)).DotProduct(d);
                    lo = Math.Min(lo, s); hi = Math.Max(hi, s);
                }
                // The bounding box includes the frame and swing: use the opening width when known.
                double width = fi.Symbol?.get_Parameter(BuiltInParameter.DOOR_WIDTH)?.AsDouble()
                               ?? fi.Symbol?.get_Parameter(BuiltInParameter.FAMILY_WIDTH_PARAM)?.AsDouble() ?? 0;
                XYZ loc = (fi.Location as LocationPoint)?.Point;
                if (width > 0 && loc != null)
                {
                    double cpos = (RevitUtil.Flat(loc) - RevitUtil.Flat(a)).DotProduct(d);
                    lo = cpos - width / 2; hi = cpos + width / 2;
                }
                if (hi > 0 && lo < len) gaps.Add(Tuple.Create(Math.Max(0, lo), Math.Min(len, hi)));
            }
            if (gaps.Count == 0) { result.Add(c); return result; }

            double cur = 0;
            foreach (var g in gaps.OrderBy(g => g.Item1))
            {
                if (g.Item1 - cur > RevitUtil.M(0.02)) result.Add(Line.CreateBound(a + d * cur, a + d * g.Item1));
                cur = Math.Max(cur, g.Item2);
            }
            if (len - cur > RevitUtil.M(0.02)) result.Add(Line.CreateBound(a + d * cur, a + d * len));
            return result;
        }
    
        /// <summary>Tabica: base at the ceiling height of the room (null = no ceiling: skipped).</summary>
        private static double? TabicaOffset(Room r, List<Ceiling> ceilings, double below)
        {
            XYZ p = RevitUtil.RoomPoint(r);
            if (p == null) return null;
            Ceiling c = ceilings.FirstOrDefault(x =>
            {
                if (x.LevelId != r.LevelId) return false;
                BoundingBoxXYZ b = x.get_BoundingBox(null);
                return b != null && p.X >= b.Min.X && p.X <= b.Max.X && p.Y >= b.Min.Y && p.Y <= b.Max.Y;
            });
            if (c == null) return null;
            double h = c.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM)?.AsDouble() ?? 0;
            return h - RevitUtil.M(below);
        }
    }
}
