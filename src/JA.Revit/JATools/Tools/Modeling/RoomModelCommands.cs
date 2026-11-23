using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using RevitDwgUnifier.Tools.Common;
using RevitDwgUnifier.Tools.Views;

namespace RevitDwgUnifier.Tools.Modeling
{
    /// <summary>Report helper for the modelling tools.</summary>
    public abstract class ModelToolCommand : RoomToolCommand
    {
        protected readonly List<string> Problems = new List<string>();

        protected void Done(int count, string what)
        {
            string text = $"{count} {what}.";
            if (Problems.Count > 0) text += "\n\nAvisos:\n" + string.Join("\n", Problems.Take(15).Select(p => "  • " + p));
            Show(text);
        }

        /// <summary>Words separated by ; or , (case and accent insensitive match on the room name).</summary>
        protected static bool NameMatches(Room r, string words)
        {
            if (string.IsNullOrWhiteSpace(words)) return false;
            string name = Fold(RevitUtil.RoomName(r));
            return words.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(w => Fold(w.Trim())).Any(w => w.Length > 0 && name.Contains(w));
        }

        protected static string Fold(string s) => TextUtil.Fold(s);
    }

    /// <summary>Pisos por Ambiente: a finish floor for each room on its boundary (finish faces),
    /// holes included; wet rooms can be recessed (rebaixo).</summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class PisosPorAmbienteCommand : ModelToolCommand
    {
        public override string Title => "Pisos por Ambiente";

        protected override Result Run()
        {
            var types = new FilteredElementCollector(Doc).OfClass(typeof(FloorType)).Cast<FloorType>()
                .Where(t => t.Category != null && t.Category.Id.Value == (long)BuiltInCategory.OST_Floors).OrderBy(t => t.Name).ToList();
            if (types.Count == 0) { Show("O projeto não tem tipos de piso."); return Result.Cancelled; }

            var dlg = new OptionsDialog(Title, "PisosPorAmbiente");
            var fRooms = AddRoomList(dlg);
            var fType = dlg.Choice("type", "Tipo de piso", types.Select(t => t.Name).ToList());
            var fOffset = dlg.Number("offset", "Deslocamento do nível (m)", 0, -100, 100);
            var fWet = dlg.Text("wet", "Ambientes com rebaixo (palavras do nome)", "BANHO; WC; LAVABO; SERVIÇO; VARANDA; SACADA");
            var fRecess = dlg.Number("recess", "Rebaixo (cm)", 2, 0, 100);
            dlg.Note("O contorno é o da face de acabamento das paredes; pilares e vazios do ambiente viram recortes no piso.");
            if (!dlg.Show(Owner)) return Result.Cancelled;

            int n = 0;
            using (var t = new Transaction(Doc, "JA Tools - " + Title))
            {
                t.Start();
                RevitDwgUnifier.Export.FailureSwallower.Attach(t);
                foreach (int i in fRooms.SelectedIndices)
                {
                    Room r = AllRooms[i];
                    ToolProgress.Report(fRooms.SelectedIndices.ToList().IndexOf(i), fRooms.SelectedIndices.Count(), RoomTitle(r));
                    if (r.Level == null) continue;
                    var loops = ModelUtil.RoomLoops(r, r.Level.ProjectElevation, Problems);
                    if (loops.Count == 0) continue;
                    try
                    {
                        Floor f = Floor.Create(Doc, loops, types[fType.Index].Id, r.LevelId);
                        double off = RevitUtil.M(fOffset.Value) - (NameMatches(r, fWet.Value) ? RevitUtil.M(fRecess.Value / 100.0) : 0);
                        f.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.Set(off);
                        f.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set("JA Tools - piso " + RoomTitle(r));
                        n++;
                    }
                    catch (Exception ex) { Problems.Add($"Ambiente {r.Number}: {ex.Message}"); }
                }
                t.Commit();
            }
            Done(n, "piso(s) criado(s)");
            return Result.Succeeded;
        }
    }

    // 2.0: Forro por Ambiente left (and its ForroGeo helper): Forro e Sanca of the JA Famílias tab does the
    // same (tabica, sanca fechada/aberta, acabamento do teto) and also enters Atualizar Revestimentos,
    // the Lista de Compras and the Orçamento.

    /// <summary>
    /// Luminárias por Ambiente: a regular grid of fixtures in each room (distance to the walls =
    /// half the spacing, the usual lighting layout). A preview (count per room) is shown and
    /// must be confirmed before anything is placed. Ceiling-hosted / face-based families are
    /// placed on the ceiling of the room; level-based ones at the chosen height.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class LuminariasPorAmbienteCommand : ModelToolCommand
    {
        public override string Title => "Luminárias por Ambiente";

        protected override Result Run()
        {
            var symbols = ModelUtil.Symbols(Doc, BuiltInCategory.OST_LightingFixtures);
            if (symbols.Count == 0) { Show("Carregue uma família de luminária no projeto."); return Result.Cancelled; }

            var dlg = new OptionsDialog(Title, "LuminariasPorAmbiente");
            var fRooms = AddRoomList(dlg);
            var fSym = dlg.Choice("symbol", "Luminária", symbols.Select(ModelUtil.SymbolLabel).ToList());
            var fSpacing = dlg.Number("spacing", "Espaçamento desejado (m)", 1.5, 0.1, 100);
            var fMax = dlg.Number("max", "Máximo por ambiente", 30, 1, 1000);
            var fHeight = dlg.Number("height", "Altura (m), se não houver forro", 2.6, 0, 100);
            if (!dlg.Show(Owner)) return Result.Cancelled;

            FamilySymbol sym = symbols[fSym.Index];
            double spacing = RevitUtil.M(fSpacing.Value);
            var ceilings = new FilteredElementCollector(Doc).OfClass(typeof(Ceiling)).Cast<Ceiling>().ToList();

            // ---------- plan
            var plan = new List<Tuple<Room, List<XYZ>, Ceiling>>();
            foreach (int i in fRooms.SelectedIndices)
            {
                Room r = AllRooms[i];
                ToolProgress.Report(fRooms.SelectedIndices.ToList().IndexOf(i), fRooms.SelectedIndices.Count(), RoomTitle(r));
                BoundingBoxXYZ b = RevitUtil.RoomBox(r);
                if (b == null) continue;
                double w = b.Max.X - b.Min.X, h = b.Max.Y - b.Min.Y;
                int nx = Math.Max(1, (int)Math.Round(w / spacing)), ny = Math.Max(1, (int)Math.Round(h / spacing));
                double px = w / nx, py = h / ny;
                var pts = new List<XYZ>();
                for (int ix = 0; ix < nx; ix++)
                    for (int iy = 0; iy < ny; iy++)
                    {
                        var p = new XYZ(b.Min.X + px * (ix + 0.5), b.Min.Y + py * (iy + 0.5), b.Min.Z);
                        // Irregular rooms: keep only points inside, at least a quarter spacing from the outline.
                        double q = Math.Min(px, py) / 4;
                        bool inside = new[] { XYZ.Zero, new XYZ(q, 0, 0), new XYZ(-q, 0, 0), new XYZ(0, q, 0), new XYZ(0, -q, 0) }
                            .All(d => r.IsPointInRoom(p + d + new XYZ(0, 0, RevitUtil.M(1))));
                        if (inside) pts.Add(p);
                    }
                if (pts.Count > fMax.Value) pts = pts.Take((int)fMax.Value).ToList();
                Ceiling ceiling = ceilings.FirstOrDefault(c => c.LevelId == r.LevelId && Contains(c, RevitUtil.RoomPoint(r)));
                plan.Add(Tuple.Create(r, pts, ceiling));
            }

            int total = plan.Sum(p => p.Item2.Count);
            if (total == 0) { Show("Nenhuma posição válida (ambientes muito pequenos para o espaçamento?)."); return Result.Cancelled; }
            var preview = new TaskDialog("JA Tools - " + Title)
            {
                MainInstruction = $"Criar {total} luminária(s) em {plan.Count(p => p.Item2.Count > 0)} ambiente(s)?",
                MainContent = string.Join("\n", plan.Where(p => p.Item2.Count > 0).Take(30)
                    .Select(p => $"{RoomTitle(p.Item1)}: {p.Item2.Count}" + (p.Item3 != null ? " (no forro)" : $" (a {fHeight.Value:0.##} m)"))),
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                DefaultButton = TaskDialogResult.Yes
            };
            ToolProgress.Hide();
            if (preview.Show() != TaskDialogResult.Yes) return Result.Cancelled;
            ToolProgress.Pulse("Criando as luminárias…");

            // ---------- place
            int n = 0;
            using (var t = new Transaction(Doc, "JA Tools - " + Title))
            {
                t.Start();
                RevitDwgUnifier.Export.FailureSwallower.Attach(t);
                if (!sym.IsActive) { sym.Activate(); Doc.Regenerate(); }
                FamilyPlacementType placement = sym.Family.FamilyPlacementType;
                foreach (var item in plan)
                {
                    Room r = item.Item1;
                    Ceiling ceiling = item.Item3;
                    // Face-based families report WorkPlaneBased: place them on the ceiling's lower
                    // face. Legacy ceiling-hosted families (OneLevelBasedHosted) take the ceiling as host.
                    Reference face = null;
                    double faceZ = r.Level.ProjectElevation + (ceiling != null ? CeilingHeight(ceiling) : RevitUtil.M(fHeight.Value));
                    if (ceiling != null && placement == FamilyPlacementType.WorkPlaneBased)
                    {
                        face = BottomFace(ceiling, out double z);
                        if (face != null) faceZ = z;
                    }

                    foreach (XYZ p in item.Item2)
                    {
                        try
                        {
                            FamilyInstance fi;
                            if (placement == FamilyPlacementType.OneLevelBasedHosted && ceiling != null)
                                fi = Doc.Create.NewFamilyInstance(new XYZ(p.X, p.Y, faceZ), sym, ceiling, StructuralType.NonStructural);
                            else if (face != null)
                                fi = Doc.Create.NewFamilyInstance(face, new XYZ(p.X, p.Y, faceZ), XYZ.BasisX, sym);
                            else
                            {
                                fi = Doc.Create.NewFamilyInstance(new XYZ(p.X, p.Y, r.Level.ProjectElevation), sym, r.Level, StructuralType.NonStructural);
                                double h = ceiling != null ? CeilingHeight(ceiling) : RevitUtil.M(fHeight.Value);
                                (fi.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM)
                                 ?? fi.get_Parameter(BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM))?.Set(h);
                            }
                            n++;
                        }
                        catch (Exception ex) { RevitUtil.AddOnce(Problems, $"{RoomTitle(r)}: {ex.Message}"); }
                    }
                }
                t.Commit();
            }
            Done(n, "luminária(s) criada(s)");
            return Result.Succeeded;
        }

        private static bool Contains(Ceiling c, XYZ p)
        {
            if (p == null) return false;
            BoundingBoxXYZ b = c.get_BoundingBox(null);
            return b != null && p.X >= b.Min.X && p.X <= b.Max.X && p.Y >= b.Min.Y && p.Y <= b.Max.Y;
        }

        private static double CeilingHeight(Ceiling c) =>
            c.get_Parameter(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM)?.AsDouble() ?? RevitUtil.M(2.6);

        private static Reference BottomFace(Ceiling c, out double z)
        {
            z = 0;
            PlanarFace best = null;
            foreach (PlanarFace pf in Dimensions.DimEngine.PlanarFaces(c))
                if (pf.FaceNormal.Z < -0.99 && pf.Reference != null && (best == null || pf.Origin.Z < best.Origin.Z)) best = pf;
            if (best == null) return null;
            z = best.Origin.Z;
            return best.Reference;
        }
    }
}
