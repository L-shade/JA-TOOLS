using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using RevitDwgUnifier.Tools.Common;
using RevitDwgUnifier.Tools.Dimensions;

namespace RevitDwgUnifier.Tools.Modeling
{
    /// <summary>
    /// Converter em Camadas (1.17 = Parede em Camadas + Piso em Camadas): converts what is selected —
    /// compound walls into one wall per layer (mitred corners, doors and windows on the core
    /// layer) and compound floors into stacked single-layer floors. Nothing selected: click them.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class ConverterEmCamadasCommand : ToolCommand
    {
        public override string Title => "Converter em Camadas";
        protected override bool PicksInView => true;   // clicks in the view: no progress box

        private sealed class WallOrFloor : ISelectionFilter
        {
            public bool AllowElement(Element e) => e is Wall || e is Floor;
            public bool AllowReference(Reference r, XYZ p) => false;
        }

        protected override Result Run()
        {
            List<Element> sel = UiDoc.Selection.GetElementIds().Select(id => Doc.GetElement(id)).Where(e => e is Wall || e is Floor).ToList();
            if (sel.Count == 0)
            {
                try
                {
                    sel = UiDoc.Selection.PickObjects(ObjectType.Element, new WallOrFloor(), "Selecione as paredes e/ou pisos a converter e clique em Concluir")
                        .Select(r => Doc.GetElement(r)).ToList();
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
            }
            var walls = sel.Where(ParedeEmCamadas.Serve).Cast<Wall>().ToList();
            var floors = sel.Where(PisoEmCamadas.Serve).Cast<Floor>().ToList();
            if (walls.Count == 0 && floors.Count == 0)
            {
                Show("Selecione paredes retas e básicas ou pisos — com mais de uma camada.");
                return Result.Cancelled;
            }

            var dlg = new OptionsDialog(Title, "ConverterEmCamadas");
            var fSkipThin = dlg.Number("thin", "Ignorar camadas com espessura menor que (mm)", 1, 0, 100);
            var fDoors = dlg.Check("doors", "Recriar portas e janelas na camada principal das paredes", true);
            fDoors.EnabledWhen(() => walls.Count > 0);
            dlg.Note($"{walls.Count} parede(s) e {floors.Count} piso(s) serão substituídos por um elemento por camada (mesmo lugar e contorno). " +
                     "Os originais são excluídos. Pisos inclinados são ignorados.");
            if (!dlg.Show(Owner)) return Result.Cancelled;

            var parts = new List<string>();
            if (walls.Count > 0) parts.Add(new ParedeEmCamadas(Doc).Converter(walls, fSkipThin.Value, fDoors.Value));
            if (floors.Count > 0) parts.Add(new PisoEmCamadas(Doc).Converter(floors, fSkipThin.Value));
            Show(string.Join("\n\n", parts));
            return Result.Succeeded;
        }
    }

    /// <summary>
    /// Parede em Camadas: replaces each selected compound wall by one wall per layer
    /// (e.g. reboco | bloco | reboco), each with a single-layer type "JA - material e cm",
    /// on the exact position of its layer. Base / top constraints are copied. Where two
    /// converted walls meet, the layer walls are trimmed to meet each other (mitred corner).
    /// Doors and windows are re-created on the core layer (same type, place, sill, flips, mark)
    /// and the other layers receive a wall opening of the same size.
    /// </summary>
    internal sealed class ParedeEmCamadas
    {
        private readonly Document Doc;
        public ParedeEmCamadas(Document doc) { Doc = doc; }

        /// <summary>Straight basic walls with more than one layer.</summary>
        public static bool Serve(Element e) => e is Wall w && w.WallType.Kind == WallKind.Basic && (w.Location as LocationCurve)?.Curve is Line &&
                                               (w.WallType.GetCompoundStructure()?.LayerCount ?? 0) > 1;

        private sealed class LayerWall
        {
            public int Index;
            public double Offset;                 // from the location line, along Orientation
            public double Width;
            public ElementId Material;
            public MaterialFunctionAssignment Function;
            public XYZ P0, P1;                    // layer line ends (flat)
            public Wall Created;
        }

        private sealed class Source
        {
            public Wall Wall;
            public XYZ P0, P1, Dir, Normal;
            public List<LayerWall> Layers = new List<LayerWall>();
            public int Core;
        }

        /// <summary>Converts the walls (one transaction); returns the summary.</summary>
        public string Converter(List<Wall> walls, double thinMm, bool recreateDoors)
        {
            var problems = new List<string>();
            int createdWalls = 0, doors = 0;
            using (var t = new Transaction(Doc, "JA Tools - Converter em Camadas"))
            {
                t.Start();
                RevitDwgUnifier.Export.FailureSwallower.Attach(t);
                var sources = walls.Select(w => Prepare(w, RevitUtil.M(thinMm / 1000.0))).Where(s => s.Layers.Count > 0).ToList();
                Miter(sources);

                foreach (Source s in sources)
                {
                    Wall w = s.Wall;
                    // Copy the vertical constraints.
                    ElementId baseLevel = w.LevelId;
                    double baseOffset = w.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0;
                    double height = w.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? RevitUtil.M(3);
                    ElementId topLevel = w.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.AsElementId() ?? ElementId.InvalidElementId;
                    double topOffset = w.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET)?.AsDouble() ?? 0;
                    double z = (Doc.GetElement(baseLevel) as Level)?.ProjectElevation ?? 0;

                    foreach (LayerWall lw in s.Layers)
                    {
                        string name = $"JA - {ModelUtil.MaterialName(Doc, lw.Material)} {ModelUtil.Cm(lw.Width)} cm";
                        WallType type = ModelUtil.SingleLayerWallType(Doc, w.WallType, RevitUtil.Sanitize(name), lw.Function, lw.Material, lw.Width);
                        var line = Line.CreateBound(new XYZ(lw.P0.X, lw.P0.Y, z), new XYZ(lw.P1.X, lw.P1.Y, z));
                        Wall nw = Wall.Create(Doc, line, type.Id, baseLevel, height, baseOffset, false, lw.Index == s.Core && w.StructuralUsage != StructuralWallUsage.NonBearing);
                        if (topLevel != ElementId.InvalidElementId) nw.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.Set(topLevel);
                        nw.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET)?.Set(topOffset);
                        nw.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM)?.Set((int)WallLocationLine.WallCenterline);
                        lw.Created = nw;
                        createdWalls++;
                    }
                }
                Doc.Regenerate();

                // Mitred joins between layer walls of converted walls.
                foreach (Source s in sources)
                    foreach (LayerWall lw in s.Layers.Where(l => l.Created != null))
                        for (int end = 0; end < 2; end++)
                        {
                            try
                            {
                                WallUtils.AllowWallJoinAtEnd(lw.Created, end);
                                ((LocationCurve)lw.Created.Location).set_JoinType(end, JoinType.Miter);
                            }
                            catch { }
                        }

                // Inserts: recreate on the core layer, open the other layers.
                foreach (Source s in sources)
                {
                    LayerWall core = s.Layers.FirstOrDefault(l => l.Index == s.Core) ?? s.Layers.OrderByDescending(l => l.Width).First();
                    var inserts = s.Wall.FindInserts(false, false, false, false).Select(id => Doc.GetElement(id)).OfType<FamilyInstance>().ToList();
                    foreach (FamilyInstance fi in inserts)
                    {
                        if (!recreateDoors) break;
                        try
                        {
                            Recreate(fi, core.Created, s);
                            doors++;
                        }
                        catch (Exception ex) { RevitUtil.AddOnce(problems, $"Abertura {fi.Symbol?.Name}: {ex.Message}"); }
                    }
                    Doc.Delete(s.Wall.Id);
                }
                t.Commit();
            }

            string text = $"{walls.Count} parede(s) convertida(s) em {createdWalls} parede(s) de camada; {doors} porta(s)/janela(s) recriada(s).";
            if (problems.Count > 0) text += "\n\nAvisos:\n" + string.Join("\n", problems.Take(15).Select(p => "  • " + p));
            return text;
        }

        private Source Prepare(Wall w, double minWidth)
        {
            var l = (Line)((LocationCurve)w.Location).Curve;
            var s = new Source
            {
                Wall = w,
                P0 = RevitUtil.Flat(l.GetEndPoint(0)),
                P1 = RevitUtil.Flat(l.GetEndPoint(1)),
                Dir = DimEngine.FlatDir(l),
                Normal = new XYZ(w.Orientation.X, w.Orientation.Y, 0).Normalize()
            };
            CompoundStructure cs = w.WallType.GetCompoundStructure();
            IList<CompoundStructureLayer> layers = cs.GetLayers();
            double total = layers.Sum(x => x.Width);
            // The location line is the wall centre line; layers go from the exterior face (+Normal).
            double cursor = total / 2;
            int first = cs.GetFirstCoreLayerIndex(), last = cs.GetLastCoreLayerIndex();
            s.Core = first;
            double coreWidth = 0;
            for (int i = 0; i < layers.Count; i++)
            {
                CompoundStructureLayer layer = layers[i];
                double mid = cursor - layer.Width / 2;
                cursor -= layer.Width;
                if (layer.Width < minWidth) continue;
                if (i >= first && i <= last && layer.Width > coreWidth) { coreWidth = layer.Width; s.Core = i; }
                s.Layers.Add(new LayerWall
                {
                    Index = i, Offset = mid, Width = layer.Width, Material = layer.MaterialId, Function = layer.Function,
                    P0 = s.P0 + s.Normal * mid, P1 = s.P1 + s.Normal * mid
                });
            }
            return s;
        }

        /// <summary>Where two converted walls share an end point, every layer line is trimmed /
        /// extended to the intersection with the same-numbered layer of the other wall (or the
        /// nearest available layer), giving clean mitred corners.</summary>
        private static void Miter(List<Source> sources)
        {
            double tol = RevitUtil.M(0.02);
            foreach (Source a in sources)
                foreach (Source b in sources)
                {
                    if (a == b || Math.Abs(a.Dir.DotProduct(b.Dir)) > 0.999) continue;
                    for (int ea = 0; ea < 2; ea++)
                        for (int eb = 0; eb < 2; eb++)
                        {
                            XYZ pa = ea == 0 ? a.P0 : a.P1, pb = eb == 0 ? b.P0 : b.P1;
                            if (pa.DistanceTo(pb) > tol + Math.Max(a.Wall.Width, b.Wall.Width)) continue;
                            for (int k = 0; k < a.Layers.Count; k++)
                            {
                                LayerWall la = a.Layers[k];
                                LayerWall lb = k < b.Layers.Count ? b.Layers[k] : b.Layers.Last();
                                XYZ x = ModelUtil.Intersect(la.P0, a.Dir, lb.P0, b.Dir);
                                if (x == null) continue;
                                if (ea == 0) la.P0 = x; else la.P1 = x;
                            }
                        }
                }
        }

        /// <summary>Same door / window on the core layer wall; rectangular openings in the others.</summary>
        private void Recreate(FamilyInstance fi, Wall coreWall, Source s)
        {
            XYZ loc = (fi.Location as LocationPoint)?.Point ?? throw new InvalidOperationException("sem ponto de inserção");
            Level level = Doc.GetElement(fi.LevelId) as Level ?? Doc.GetElement(coreWall.LevelId) as Level;
            double sill = fi.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM)?.AsDouble() ?? 0;
            BoundingBoxXYZ bb = fi.get_BoundingBox(null);
            string mark = fi.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();
            bool facingFlipped = fi.FacingFlipped, handFlipped = fi.HandFlipped;
            FamilySymbol sym = fi.Symbol;

            // Opening size for the other layers (width along the wall, full insert height).
            double width = sym.get_Parameter(BuiltInParameter.DOOR_WIDTH)?.AsDouble()
                           ?? sym.get_Parameter(BuiltInParameter.WINDOW_WIDTH)?.AsDouble()
                           ?? sym.get_Parameter(BuiltInParameter.FAMILY_WIDTH_PARAM)?.AsDouble() ?? 0;
            double h = sym.get_Parameter(BuiltInParameter.DOOR_HEIGHT)?.AsDouble()
                       ?? sym.get_Parameter(BuiltInParameter.WINDOW_HEIGHT)?.AsDouble()
                       ?? sym.get_Parameter(BuiltInParameter.FAMILY_HEIGHT_PARAM)?.AsDouble() ?? 0;
            double zBottom = (level?.ProjectElevation ?? 0) + sill;
            if (width <= 0 && bb != null) width = Math.Abs((bb.Max - bb.Min).DotProduct(s.Dir));
            if (h <= 0 && bb != null) h = bb.Max.Z - bb.Min.Z;

            Doc.Delete(fi.Id);
            FamilyInstance nf = Doc.Create.NewFamilyInstance(loc, sym, coreWall, level, StructuralType.NonStructural);
            nf.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM)?.Set(sill);
            if (!string.IsNullOrEmpty(mark)) nf.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set(mark);
            Doc.Regenerate();
            if (nf.FacingFlipped != facingFlipped) nf.flipFacing();
            if (nf.HandFlipped != handFlipped) nf.flipHand();

            if (width <= 0 || h <= 0) return;
            XYZ c = RevitUtil.Flat(loc);
            foreach (LayerWall lw in s.Layers.Where(l => l.Created != null && l.Created.Id != coreWall.Id))
            {
                XYZ onLayer = c + s.Normal * (lw.Offset - (c - s.P0).DotProduct(s.Normal));
                XYZ p1 = onLayer - s.Dir * (width / 2) + new XYZ(0, 0, zBottom);
                XYZ p2 = onLayer + s.Dir * (width / 2) + new XYZ(0, 0, zBottom + h);
                try { Doc.Create.NewOpening(lw.Created, p1, p2); } catch { }
            }
        }
    }

    /// <summary>
    /// Piso em Camadas: replaces each selected compound floor by one floor per layer
    /// (e.g. contrapiso + acabamento), stacked at the right heights, with the original outline
    /// and openings (taken from the top face). Each layer gets a single-layer floor type.
    /// </summary>
    internal sealed class PisoEmCamadas
    {
        private readonly Document Doc;
        public PisoEmCamadas(Document doc) { Doc = doc; }

        /// <summary>Floors with more than one layer.</summary>
        public static bool Serve(Element e) => e is Floor f && (f.FloorType.GetCompoundStructure()?.LayerCount ?? 0) > 1;

        /// <summary>Converts the floors (one transaction); returns the summary.</summary>
        public string Converter(List<Floor> floors, double thinMm)
        {
            var problems = new List<string>();
            int created = 0, converted = 0;
            using (var t = new Transaction(Doc, "JA Tools - Converter em Camadas"))
            {
                t.Start();
                RevitDwgUnifier.Export.FailureSwallower.Attach(t);
                foreach (Floor f in floors)
                {
                    try
                    {
                        List<CurveLoop> loops = TopLoops(f, out double topZ);
                        if (loops == null) { problems.Add($"Piso {f.Id.Value}: face superior não horizontal (inclinado?)."); continue; }
                        Level level = Doc.GetElement(f.LevelId) as Level;
                        double levelZ = level?.ProjectElevation ?? 0;

                        CompoundStructure cs = f.FloorType.GetCompoundStructure();
                        IList<CompoundStructureLayer> layers = cs.GetLayers();      // top (exterior) first
                        double cursor = topZ;
                        foreach (CompoundStructureLayer layer in layers)
                        {
                            double layerTop = cursor;
                            cursor -= layer.Width;
                            if (layer.Width < RevitUtil.M(thinMm / 1000.0)) continue;
                            string name = RevitUtil.Sanitize($"JA - {ModelUtil.MaterialName(Doc, layer.MaterialId)} {ModelUtil.Cm(layer.Width)} cm");
                            FloorType type = ModelUtil.SingleLayerFloorType(Doc, f.FloorType, name, layer.Function, layer.MaterialId, layer.Width);
                            var flat = loops.Select(l => Flatten(l, levelZ)).ToList();
                            Floor nf = Floor.Create(Doc, flat, type.Id, f.LevelId);
                            nf.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM)?.Set(layerTop - levelZ);
                            nf.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set("JA Tools - camada de " + f.FloorType.Name);
                            created++;
                        }
                        Doc.Delete(f.Id);
                        converted++;
                    }
                    catch (Exception ex) { problems.Add($"Piso {f.Id.Value}: {ex.Message}"); }
                }
                t.Commit();
            }

            string text = $"{converted} piso(s) convertido(s) em {created} piso(s) de camada.";
            if (problems.Count > 0) text += "\n\nAvisos:\n" + string.Join("\n", problems.Take(15).Select(p => "  • " + p));
            return text;
        }

        /// <summary>Outer + inner loops of the (horizontal) top face; null for sloped floors.</summary>
        private static List<CurveLoop> TopLoops(Floor f, out double z)
        {
            z = 0;
            PlanarFace top = null;
            foreach (PlanarFace pf in DimEngine.PlanarFaces(f))
                if (pf.FaceNormal.Z > 0.9999 && (top == null || pf.Origin.Z > top.Origin.Z)) top = pf;
            if (top == null) return null;
            z = top.Origin.Z;
            return top.GetEdgesAsCurveLoops().ToList();
        }

        private static CurveLoop Flatten(CurveLoop loop, double z)
        {
            var result = new CurveLoop();
            foreach (Curve c in loop) result.Append(ModelUtil.Flatten(c, z));
            return result;
        }
    }
}
