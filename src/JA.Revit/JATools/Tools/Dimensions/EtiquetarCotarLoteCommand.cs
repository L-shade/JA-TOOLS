using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using RevitDwgUnifier.Tools.Common;
using RevitDwgUnifier.Tools.Detail;

namespace RevitDwgUnifier.Tools.Dimensions
{
    /// <summary>
    /// Etiquetar e Cotar em Lote: in every chosen floor plan, tags the doors, windows and rooms
    /// that have no tag yet (office identifiers, automatic placement) and dimensions the openings
    /// of the exterior walls — one chain per wall, outside it: wall ends / crossing walls and the
    /// sides of each door and window.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class EtiquetarCotarLoteCommand : DimToolCommand
    {
        public override string Title => "Etiquetar e Cotar em Lote";

        protected override Result Run()
        {
            var plantas = new FilteredElementCollector(Doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan && v.GenLevel != null)
                .OrderBy(v => v.GenLevel.Elevation).ThenBy(v => v.Name).ToList();
            if (plantas.Count == 0) { Show("O projeto não tem plantas baixas."); return Result.Cancelled; }

            var dlg = new OptionsDialog(Title, "EtiquetarCotarLote", "Aplicar");
            var fVistas = dlg.Multi("vistas", "Plantas", plantas.Select(v => v.Name + "  (" + v.GenLevel.Name + ")").ToList(), false);
            for (int i = 0; i < plantas.Count; i++) fVistas.Checked[i] = plantas[i].Id == Doc.ActiveView.Id;
            dlg.Section("Etiquetas");
            var fEsq = dlg.Check("esquadrias", "Portas e janelas", true);
            var fAmb = dlg.Check("ambientes", "Ambientes", true);
            dlg.Section("Cotas");
            var fCotas = dlg.Check("cotas", "Cotar as aberturas das paredes", true, "Uma corrente por parede, do lado de fora: pontas / paredes que cruzam e os lados de cada porta e janela.");
            var fSoExt = dlg.Check("soExterior", "Só paredes externas (Função = Exterior no tipo)", true);
            var fOff = dlg.Number("afastamento", "Afastamento da cota até a face da parede (mm)", 800, 100, 5000);
            var fInternas = dlg.Check("internas", "Cotas internas de cada ambiente (largura e comprimento)", false,
                "Como em Cotas por Ambiente: de face a face das paredes, passando perto do ponto de etiqueta; ambientes já cotados são pulados.");
            var fOffIn = dlg.Number("afastInterno", "Afastar as cotas internas do centro do ambiente (m)", 0.6, 0, 20);
            fOffIn.EnabledWhen(fInternas);
            var fType = AddTypeChoice(dlg);
            fSoExt.EnabledWhen(fCotas); fOff.EnabledWhen(fCotas);
            dlg.Note("Elementos já etiquetados e paredes / ambientes que já têm cota na planta são pulados: pode rodar de novo depois de acrescentar portas ou ambientes.");
            if (!dlg.Show(Owner)) return Result.Cancelled;
            var vistas = fVistas.SelectedIndices.Select(i => plantas[i]).ToList();
            if (vistas.Count == 0) return Result.Cancelled;
            DimensionType type = SelectedType(fType);

            int tags = 0, tagsAmb = 0, k = 0;
            ToolProgress.Setup($"{vistas.Count} planta(s)", new[] { "Etiquetar e cotar", "Concluir" });
            List<Room> ambientes = null;   // placed rooms, read once for all the plans
            foreach (ViewPlan v in vistas)
            {
                ToolProgress.Report(k++, vistas.Count, v.Name);
                using (var t = new Transaction(Doc, "JA Tools - " + Title + " - " + v.Name))
                {
                    t.Start();
                    RevitDwgUnifier.Export.FailureSwallower.Attach(t);
                    try
                    {
                        if (fEsq.Value)
                        {
                            var ja = EtiquetarElementosCommand.TaggedIds(v);
                            foreach (BuiltInCategory bic in new[] { BuiltInCategory.OST_Doors, BuiltInCategory.OST_Windows })
                            {
                                Category cat = Category.GetCategory(Doc, bic);
                                var todo = new FilteredElementCollector(Doc, v.Id).OfCategory(bic).WhereElementIsNotElementType()
                                    .Where(e => !ja.Contains(e.Id) && RevitUtil.HasMark(Doc, e)).ToList();
                                if (todo.Count == 0) continue;
                                ElementId tipoTag = TagLibrary.Ensure(Doc, cat, Problems);
                                tags += TagPlacer.Tag(Doc, v, todo, tipoTag, null, false, Problems, cat?.Name ?? bic.ToString()).Count;
                            }
                        }
                        if (fAmb.Value)
                        {
                            var comTag = new HashSet<ElementId>(new FilteredElementCollector(Doc, v.Id).OfCategory(BuiltInCategory.OST_RoomTags)
                                .OfType<RoomTag>().Select(rt => rt.TaggedLocalRoomId));
                            ambientes = ambientes ?? new FilteredElementCollector(Doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().OfType<Room>()
                                .Where(r => r.Area > 0).ToList();
                            var rooms = ambientes.Where(r => r.LevelId == v.GenLevel.Id && !comTag.Contains(r.Id)).ToList();
                            tagsAmb += RevitUtil.TagRooms(Doc, v, rooms, Problems);
                        }
                        var cotados = Cotados(v);
                        if (fCotas.Value) Cotar(v, fSoExt.Value, RevitUtil.M(fOff.Value / 1000), type, cotados);
                        if (fInternas.Value)
                        {
                            List<Wall> walls = DimEngine.WallsInView(Doc, v);
                            double z = DimEngine.ViewZ(v);
                            foreach (Room r in new FilteredElementCollector(Doc, v.Id).OfCategory(BuiltInCategory.OST_Rooms).OfType<Room>().Where(r => r.Area > 1e-6))
                            {
                                // a room whose boundary walls are all already in some dimension of this plan: skipped
                                var limites = (RevitUtil.Boundary(r) ?? new List<IList<BoundarySegment>>()).SelectMany(l => l).Select(s => s.ElementId).Where(id => Doc.GetElement(id) is Wall).Distinct().ToList();
                                if (limites.Count > 0 && limites.All(cotados.Contains)) continue;
                                DimRoomAll(v, walls, r, RevitUtil.M(fOffIn.Value), z, type, false);
                            }
                        }
                    }
                    catch (Exception ex) { RevitUtil.AddOnce(Problems, v.Name + ": " + ex.Message); }
                    t.Commit();
                }
            }
            ToolProgress.End();
            Report($"{tags} etiqueta(s) de portas e janelas e {tagsAmb} de ambientes em {vistas.Count} planta(s).");
            return Result.Succeeded;
        }

        /// <summary>Elements referenced by the dimensions already in the plan.</summary>
        private HashSet<ElementId> Cotados(View v)
        {
            var set = new HashSet<ElementId>();
            foreach (Dimension d in new FilteredElementCollector(Doc, v.Id).OfClass(typeof(Dimension)).Cast<Dimension>())
            {
                try { foreach (Reference r in d.References) if (r != null) set.Add(r.ElementId); } catch { }
            }
            return set;
        }

        private void Cotar(ViewPlan v, bool soExterior, double afast, DimensionType type, HashSet<ElementId> cotados)
        {
            List<Wall> walls = DimEngine.WallsInView(Doc, v);
            var aberturas = new FilteredElementCollector(Doc, v.Id).WherePasses(new LogicalOrFilter(new ElementCategoryFilter(BuiltInCategory.OST_Doors), new ElementCategoryFilter(BuiltInCategory.OST_Windows)))
                .WhereElementIsNotElementType().OfType<FamilyInstance>().Where(f => f.Host is Wall).GroupBy(f => f.Host.Id).ToDictionary(g => g.Key, g => g.ToList());
            double z = DimEngine.ViewZ(v);
            foreach (Wall w in walls)
            {
                if (!aberturas.TryGetValue(w.Id, out var fis) || w.CurtainGrid != null) continue;
                if (soExterior && w.WallType.Function != WallFunction.Exterior) continue;
                // already dimensioned: one of its openings is in a dimension of this plan
                if (fis.Any(f => cotados.Contains(f.Id))) continue;
                Line wl = DimEngine.WallLine(w);
                if (wl == null) continue;
                XYZ u = DimEngine.FlatDir(wl);
                double ext = w.Width + RevitUtil.M(0.05);
                XYZ a = RevitUtil.Flat(wl.GetEndPoint(0)) - u * ext, b = RevitUtil.Flat(wl.GetEndPoint(1)) + u * ext;
                var refs = new List<RefAt>();
                refs.AddRange(DimEngine.WallEndFaces(w, a, u));
                refs.AddRange(DimEngine.WallFacesAcross(walls.Where(x => x.Id != w.Id), a, b));
                foreach (FamilyInstance fi in fis) refs.AddRange(DimEngine.OpeningSides(fi, a, u));
                if (refs.Count(r => r.Kind == "opening") < 2) continue;
                // outside the wall: its exterior side (Orientation), the face plus the gap
                XYZ meio = RevitUtil.Flat(wl.Evaluate(0.5, true));
                XYZ fora = new XYZ(w.Orientation.X, w.Orientation.Y, 0).Normalize();
                XYZ through = meio + fora * (w.Width / 2 + afast) + new XYZ(0, 0, z);
                Make(v, a, u, through, refs, type, "Parede " + w.Id.Value);
            }
        }
    }
}
