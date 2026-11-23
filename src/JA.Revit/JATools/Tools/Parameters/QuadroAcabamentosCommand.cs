using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using RevitDwgUnifier.Tools.Common;

namespace RevitDwgUnifier.Tools.Parameters
{
    /// <summary>
    /// Quadro de Acabamentos: fills Acabamento do piso / da parede / do forro of every room from the
    /// materials actually modelled — the top layer of the floor under the room, the layer of each
    /// boundary wall that faces the room, the bottom layer of the ceiling over it — and creates the
    /// room schedule "JA - Quadro de Acabamentos".
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class QuadroAcabamentosCommand : ToolCommand
    {
        public override string Title => "Quadro de Acabamentos";

        protected override Result Run()
        {
            var sel = new HashSet<ElementId>(UiDoc.Selection.GetElementIds());
            var todos = new FilteredElementCollector(Doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().OfType<Room>()
                .Where(r => r.Area > 0 && r.Location != null).ToList();
            if (todos.Count == 0) { Show("Nenhum ambiente colocado no projeto."); return Result.Cancelled; }
            int nSel = todos.Count(r => sel.Contains(r.Id));

            var dlg = new OptionsDialog(Title, "QuadroAcabamentos", "Preencher");
            var fSel = dlg.Check("soSel", $"Só os ambientes selecionados ({nSel})", nSel > 0);
            fSel.EnabledWhen(() => nSel > 0);
            dlg.Section("Preencher");
            var fPiso = dlg.Check("piso", "Acabamento do piso (camada de cima do piso)", true);
            var fParede = dlg.Check("parede", "Acabamento das paredes (camada voltada para o ambiente)", true);
            var fForro = dlg.Check("forro", "Acabamento do forro (camada de baixo do forro)", true);
            var fSobre = dlg.Check("sobrescrever", "Sobrescrever o que já foi preenchido à mão", false, "Desligado, só os campos vazios são preenchidos.");
            var fSemForro = dlg.Text("semForro", "Sem forro: escrever", "Laje pintada", "Texto quando não há forro sobre o ambiente (vazio = deixar em branco).");
            dlg.Section("Quadro");
            var fQuadro = dlg.Check("quadro", "Criar / atualizar a tabela “JA - Quadro de Acabamentos”", true);
            if (!dlg.Show(Owner)) return Result.Cancelled;
            var rooms = fSel.Value && nSel > 0 ? todos.Where(r => sel.Contains(r.Id)).ToList() : todos;

            var pisos = new FilteredElementCollector(Doc).OfClass(typeof(Floor)).Cast<Floor>().ToList();
            var forros = new FilteredElementCollector(Doc).OfClass(typeof(Ceiling)).Cast<Ceiling>().ToList();
            var contornoPiso = new Dictionary<ElementId, List<List<XYZ>>>();
            List<List<XYZ>> Contorno(Element e, ElementId sketchId)
            {
                if (contornoPiso.TryGetValue(e.Id, out var c)) return c;
                c = new List<List<XYZ>>();
                try
                {
                    if (Doc.GetElement(sketchId) is Sketch sk)
                        foreach (CurveArray ca in sk.Profile)
                        {
                            var l = new List<XYZ>();
                            foreach (Curve cv in ca) { var t = cv.Tessellate(); for (int i = 0; i + 1 < t.Count; i++) l.Add(t[i]); }
                            if (l.Count >= 3) c.Add(l);
                        }
                }
                catch { }
                contornoPiso[e.Id] = c;
                return c;
            }
            bool Dentro(List<List<XYZ>> lacos, XYZ p)
            {
                int n = 0;
                foreach (var l in lacos)
                {
                    bool c = false;
                    for (int i = 0, j = l.Count - 1; i < l.Count; j = i++)
                        if ((l[i].Y > p.Y) != (l[j].Y > p.Y) && p.X < (l[j].X - l[i].X) * (p.Y - l[i].Y) / (l[j].Y - l[i].Y) + l[i].X) c = !c;
                    if (c) n++;
                }
                return n % 2 == 1;
            }
            string Mat(ElementId id) => id == null || id == ElementId.InvalidElementId ? null : Doc.GetElement(id)?.Name;
            var opt = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine };
            // material painted with the Paint tool on the faces of e that pass the test (first one found)
            string Pintura(Element e, Func<PlanarFace, bool> teste)
            {
                try
                {
                    foreach (GeometryObject go in e.get_Geometry(opt) ?? Enumerable.Empty<GeometryObject>())
                    {
                        if (!(go is Solid so) || so.Volume <= 0) continue;
                        foreach (Face f in so.Faces)
                            if (f is PlanarFace pf && teste(pf) && Doc.IsPainted(e.Id, f))
                            {
                                string m = Mat(Doc.GetPaintedMaterial(e.Id, f));
                                if (m != null) return m;
                            }
                    }
                }
                catch { }
                return null;
            }
            string Camada(HostObjAttributes tipo, bool primeira)
            {
                CompoundStructure cs = tipo?.GetCompoundStructure();
                if (cs == null || cs.LayerCount == 0) return tipo?.Name;
                // the first / last layer with a material (membranes have no thickness)
                var idx = Enumerable.Range(0, cs.LayerCount).Where(i => cs.GetLayerWidth(i) > 1e-6).ToList();
                if (idx.Count == 0) return tipo.Name;
                int k = primeira ? idx.First() : idx.Last();
                return Mat(cs.GetMaterialId(k)) ?? tipo.Name;
            }

            int nPiso = 0, nParede = 0, nForro = 0;
            var problemas = new List<string>();
            ViewSchedule quadro = null;
            using (var t = new Transaction(Doc, "JA Tools - " + Title))
            {
                t.Start();
                RevitDwgUnifier.Export.FailureSwallower.Attach(t);
                foreach (Room r in rooms)
                {
                    XYZ c = RevitUtil.RoomPoint(r);
                    if (c == null) continue;
                    double z0 = (r.Level?.Elevation ?? 0) + r.BaseOffset;
                    bool Livre(BuiltInParameter b) => fSobre.Value || string.IsNullOrWhiteSpace(r.get_Parameter(b)?.AsString());
                    void Escrever(BuiltInParameter b, string v, ref int n)
                    {
                        Parameter p = r.get_Parameter(b);
                        if (p == null || p.IsReadOnly || v == null) return;
                        if (p.AsString() != v) { p.Set(v); n++; }
                    }
                    try
                    {
                        if (fPiso.Value && Livre(BuiltInParameter.ROOM_FINISH_FLOOR))
                        {
                            // the highest floor around the room's base containing its point (a finish floor on a slab wins)
                            Floor f = pisos.Where(x =>
                            {
                                BoundingBoxXYZ bb = x.get_BoundingBox(null);
                                return bb != null && bb.Max.Z > z0 - RevitUtil.M(0.6) && bb.Max.Z < z0 + RevitUtil.M(0.6) &&
                                       c.X >= bb.Min.X && c.X <= bb.Max.X && c.Y >= bb.Min.Y && c.Y <= bb.Max.Y && Dentro(Contorno(x, x.SketchId), c);
                            }).OrderByDescending(x => x.get_BoundingBox(null).Max.Z).FirstOrDefault();
                            if (f != null) Escrever(BuiltInParameter.ROOM_FINISH_FLOOR, Pintura(f, pf => pf.FaceNormal.Z > 0.9) ?? Camada(f.FloorType, true), ref nPiso);
                        }
                        if (fForro.Value && Livre(BuiltInParameter.ROOM_FINISH_CEILING))
                        {
                            double topo = z0 + r.UnboundedHeight;
                            Ceiling ce = forros.Where(x =>
                            {
                                BoundingBoxXYZ bb = x.get_BoundingBox(null);
                                return bb != null && bb.Min.Z > z0 && bb.Min.Z < topo + RevitUtil.M(0.3) &&
                                       c.X >= bb.Min.X && c.X <= bb.Max.X && c.Y >= bb.Min.Y && c.Y <= bb.Max.Y && Dentro(Contorno(x, x.SketchId), c);
                            }).OrderBy(x => x.get_BoundingBox(null).Min.Z).FirstOrDefault();
                            string v = ce != null ? Pintura(ce, pf => pf.FaceNormal.Z < -0.9) ?? Camada(Doc.GetElement(ce.GetTypeId()) as HostObjAttributes, false)
                                     : string.IsNullOrWhiteSpace(fSemForro.Value) ? null : fSemForro.Value.Trim();
                            Escrever(BuiltInParameter.ROOM_FINISH_CEILING, v, ref nForro);
                        }
                        if (fParede.Value && Livre(BuiltInParameter.ROOM_FINISH_WALL))
                        {
                            var mats = new List<string>();
                            foreach (IList<BoundarySegment> laco in RevitUtil.Boundary(r) ?? new List<IList<BoundarySegment>>())
                                foreach (BoundarySegment s in laco)
                                {
                                    if (!(Doc.GetElement(s.ElementId) is Wall w)) continue;
                                    string m;
                                    if (w.WallType.Kind != WallKind.Basic) m = w.WallType.Name;
                                    else
                                    {
                                        // which side of the wall faces the room: exterior = layer 0
                                        Curve cv = s.GetCurve();
                                        XYZ meio = cv.Evaluate(0.5, true);
                                        XYZ teste = meio + w.Orientation * RevitUtil.M(0.08);
                                        bool externo = r.IsPointInRoom(new XYZ(teste.X, teste.Y, z0 + RevitUtil.M(0.5)));
                                        // painted face on the room side first, then the layer of that side
                                        XYZ lado = externo ? w.Orientation : -w.Orientation;
                                        m = Pintura(w, pf => pf.FaceNormal.DotProduct(lado) > 0.9) ?? Camada(w.WallType, externo);
                                    }
                                    if (!string.IsNullOrWhiteSpace(m) && !mats.Contains(m)) mats.Add(m);
                                }
                            if (mats.Count > 0) Escrever(BuiltInParameter.ROOM_FINISH_WALL, string.Join(" / ", mats), ref nParede);
                        }
                    }
                    catch (Exception ex) { RevitUtil.AddOnce(problemas, RevitUtil.RoomLabel(r) + ": " + ex.Message); }
                }

                if (fQuadro.Value)
                {
                    try { quadro = Quadro(); }
                    catch (Exception ex) { problemas.Add("Tabela: " + ex.Message); }
                }
                t.Commit();
            }
            if (quadro != null) { try { UiDoc.ActiveView = quadro; } catch { } }
            Show($"{rooms.Count} ambiente(s): {nPiso} piso(s), {nParede} parede(s) e {nForro} forro(s) preenchidos." +
                 (fQuadro.Value ? "\nTabela “JA - Quadro de Acabamentos” criada / atualizada (Número, Ambiente, Nível, Área, Piso, Parede, Forro, Rodapé)." : "") +
                 "\n\nVale a pintura aplicada com a ferramenta Pintar na face voltada para o ambiente; sem pintura, a camada daquele lado." +
                 (problemas.Count > 0 ? "\n\nAvisos:\n• " + string.Join("\n• ", problemas.Take(15)) : ""));
            return Result.Succeeded;
        }

        private ViewSchedule Quadro()
        {
            const string nome = "JA - Quadro de Acabamentos";
            ViewSchedule vs = new FilteredElementCollector(Doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>().FirstOrDefault(s => s.Name == nome);
            if (vs != null) return vs;
            vs = ViewSchedule.CreateSchedule(Doc, new ElementId(BuiltInCategory.OST_Rooms));
            vs.Name = nome;
            ScheduleDefinition def = vs.Definition;
            var campos = def.GetSchedulableFields();
            ScheduleField Campo(BuiltInParameter b, string titulo)
            {
                SchedulableField sf = campos.FirstOrDefault(x => x.ParameterId.Value == (long)b);
                if (sf == null) return null;
                ScheduleField f = def.AddField(sf);
                if (titulo != null) f.ColumnHeading = titulo;
                return f;
            }
            ScheduleField nivel = Campo(BuiltInParameter.ROOM_LEVEL_ID, "Nível");
            ScheduleField num = Campo(BuiltInParameter.ROOM_NUMBER, "Nº");
            Campo(BuiltInParameter.ROOM_NAME, "Ambiente");
            ScheduleField area = Campo(BuiltInParameter.ROOM_AREA, "Área");
            Campo(BuiltInParameter.ROOM_FINISH_FLOOR, "Piso");
            Campo(BuiltInParameter.ROOM_FINISH_BASE, "Rodapé");
            Campo(BuiltInParameter.ROOM_FINISH_WALL, "Parede");
            Campo(BuiltInParameter.ROOM_FINISH_CEILING, "Forro");
            if (nivel != null) def.AddSortGroupField(new ScheduleSortGroupField(nivel.FieldId) { ShowHeader = true });
            if (num != null) def.AddSortGroupField(new ScheduleSortGroupField(num.FieldId));
            if (area != null) { try { def.AddFilter(new ScheduleFilter(area.FieldId, ScheduleFilterType.GreaterThan, 0.0)); } catch { } }
            def.IsItemized = true;
            return vs;
        }
    }
}
