using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using RevitDwgUnifier.Tools.Common;

namespace RevitDwgUnifier.Tools.Modeling
{
    /// <summary>
    /// Varinha de Pontos: the office's installation families (outlets, switches, water points, drain markers, data,
    /// lighting devices…) placed with clicks, like the magic wand. In a plan: click near a wall, on the room side — the
    /// family goes onto the nearest wall face, facing the room, at the height chosen (face-based on the face itself,
    /// wall-hosted in the wall, level-based or annotation in front of the face, turned to it); away from walls (drains,
    /// floor points) at the point clicked. In sections, elevations and 3D: click on the wall face itself, at the height
    /// wanted. Esc ends a family; then another can be chosen. Each family round undoes with one Ctrl+Z.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class VarinhaPontosCommand : ToolCommand
    {
        public override string Title => "Varinha de Pontos";
        protected override bool PicksInView => true;

        private static readonly BuiltInCategory[] Categorias =
        {
            BuiltInCategory.OST_ElectricalFixtures, BuiltInCategory.OST_LightingDevices, BuiltInCategory.OST_LightingFixtures,
            BuiltInCategory.OST_ElectricalEquipment, BuiltInCategory.OST_CommunicationDevices, BuiltInCategory.OST_DataDevices,
            BuiltInCategory.OST_TelephoneDevices, BuiltInCategory.OST_FireAlarmDevices, BuiltInCategory.OST_SecurityDevices,
            BuiltInCategory.OST_NurseCallDevices, BuiltInCategory.OST_PlumbingFixtures, BuiltInCategory.OST_PipeAccessory,
            BuiltInCategory.OST_Sprinklers, BuiltInCategory.OST_MechanicalEquipment, BuiltInCategory.OST_SpecialityEquipment,
            BuiltInCategory.OST_GenericModel, BuiltInCategory.OST_DetailComponents, BuiltInCategory.OST_GenericAnnotation
        };

        private static readonly (string rotulo, double m)[] Alturas =
        {
            ("No piso (0,00 m) — ralos, esgoto, caixas no piso", 0.00),
            ("0,30 m — tomada baixa", 0.30),
            ("0,60 m — água fria do lavatório", 0.60),
            ("1,10 m — interruptor, tomada de bancada", 1.10),
            ("1,20 m — interruptor (alternativa), registro", 1.20),
            ("2,20 m — tomada alta, ar-condicionado, chuveiro", 2.20),
            ("Outra (abaixo)", double.NaN)
        };

        private const string CopiarDoModelo = "↖ Copiar a família de um elemento já colocado (clicar nele)";

        private sealed class SoParedes : ISelectionFilter
        {
            public bool AllowElement(Element e) => e is Wall;
            public bool AllowReference(Reference r, XYZ p) => true;
        }

        private sealed class SoInstancias : ISelectionFilter
        {
            public bool AllowElement(Element e) => e is FamilyInstance;
            public bool AllowReference(Reference r, XYZ p) => false;
        }

        /// <summary>The wall face found for a click.</summary>
        private sealed class FaceParede
        {
            public Wall Parede;
            public Reference Ref;
            public XYZ Ponto;     // on the face, at the height of the click / of the point
            public XYZ Normal;    // out of the wall, horizontal
        }

        protected override Result Run()
        {
            View vista = Doc.ActiveView;
            bool planta = vista is ViewPlan;
            if (!planta && !(vista is ViewSection) && !(vista is View3D))
            { Show("Abra uma planta, um corte, uma elevação ou uma vista 3D.", ResultKind.Info); return Result.Cancelled; }

            var tipos = new FilteredElementCollector(Doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Where(s => s.Category != null && Categorias.Any(b => s.Category.Id.Value == (long)b))
                .Where(s => !(s.Category.Id.Value == (long)BuiltInCategory.OST_GenericAnnotation || s.Category.Id.Value == (long)BuiltInCategory.OST_DetailComponents) || !(vista is View3D))
                .OrderBy(s => s.Category.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(s => s.FamilyName, new NaturalStringComparer()).ThenBy(s => s.Name, new NaturalStringComparer())
                .ToList();
            if (tipos.Count == 0)
            { Show("Nenhuma família de instalações carregada (dispositivos elétricos, iluminação, aparelhos sanitários, acessórios de tubo, modelos genéricos, itens de detalhe…). Carregue as famílias do escritório e rode de novo.", ResultKind.Info); return Result.Cancelled; }

            var feitos = new Dictionary<string, int>();
            int semParede = 0, falhas = 0, semEtiqueta = 0;
            var motivos = new List<string>();

            while (true)
            {
                // ---- which family, how high
                var rotulos = new List<string> { CopiarDoModelo };
                rotulos.AddRange(tipos.Select(Rotulo));
                var d = new OptionsDialog(Title, "VarinhaPontos", "Começar a clicar");
                var fTipo = d.Choice("tipo", "Família e tipo", rotulos, tipos.Count > 0 ? 1 : 0,
                    "As famílias de instalações carregadas no projeto, por categoria. Digite as primeiras letras para achar. A primeira opção copia o tipo de um elemento já colocado.");
                var fAltura = d.Choice("altura", "Altura", Alturas.Select(a => a.rotulo).ToList(), 1,
                    "Do piso do nível da planta à origem da família (o centro da caixa, na maioria). Em cortes, elevações e 3D vale a altura do clique.");
                var fOutra = d.Number("outra", "Altura (m)", 1.10, -10, 50);
                fOutra.VisibleWhen(fAltura, Alturas.Length - 1);
                var fAfast = d.Number("afastamento", "Afastamento da face (cm)", 0, -50, 200,
                    "Para famílias que não são de face (modelos genéricos, itens de detalhe): a distância do ponto até a face da parede.");
                var fRaio = d.Number("raio", "Achar a parede até (cm)", 60, 5, 500, "Clique a até esta distância da face da parede, do lado do ambiente.");
                var fSolto = d.Check("solto", "Sem parede perto: colocar no ponto clicado", true, "Para ralos, caixas de esgoto e pontos no piso; desligado, o clique longe de parede é ignorado.");
                var fEtiq = d.Check("etiquetar", "Etiquetar cada um (etiqueta da categoria carregada)", false);
                var fDeNovo = d.Check("denovo", "No Esc, escolher outra família", true, "Para ir de tomadas a interruptores e a pontos de água sem sair da ferramenta.");
                d.Note(planta
                    ? "Clique perto da parede, do lado do ambiente: a família vai para a face mais próxima, de frente para o ambiente. Esc termina."
                    : "Clique na face da parede onde vai a peça (a altura é a do clique). Esc termina.");
                if (!d.Show(Owner)) break;

                FamilySymbol sym;
                if (fTipo.Index <= 0)
                {
                    try
                    {
                        Reference rr = UiDoc.Selection.PickObject(ObjectType.Element, new SoInstancias(), $"{Title}: clique numa peça já colocada para usar a mesma família e tipo (Esc volta)");
                        sym = (Doc.GetElement(rr) as FamilyInstance)?.Symbol;
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException) { continue; }
                    if (sym == null) continue;
                }
                else sym = tipos[fTipo.Index - 1];
                double altura = double.IsNaN(Alturas[Math.Max(0, fAltura.Index)].m) ? fOutra.Value : Alturas[Math.Max(0, fAltura.Index)].m;
                double h = altura / 0.3048, afast = fAfast.Value / 30.48, raio = fRaio.Value / 30.48;
                FamilyPlacementType tipoColoc = sym.Family.FamilyPlacementType;
                if (tipoColoc == FamilyPlacementType.ViewBased && vista is View3D) { Show("Família de anotação: use numa planta, corte ou elevação.", ResultKind.Info); continue; }
                if (tipoColoc == FamilyPlacementType.CurveBased || tipoColoc == FamilyPlacementType.CurveBasedDetail || tipoColoc == FamilyPlacementType.Adaptive || tipoColoc == FamilyPlacementType.TwoLevelsBased)
                { Show($"“{sym.FamilyName}” é baseada em linha, adaptativa ou de dois níveis: esta ferramenta coloca famílias de ponto.", ResultKind.Info); continue; }

                Level nivelPlanta = (vista as ViewPlan)?.GenLevel;
                var paredes = new FilteredElementCollector(Doc, vista.Id).OfClass(typeof(Wall)).Cast<Wall>().ToList();
                string nomeTipo = sym.FamilyName + " : " + sym.Name;
                int nesta = 0;

                using (var grupo = new TransactionGroup(Doc, $"JA Tools - {Title} ({sym.FamilyName})"))
                {
                    grupo.Start();
                    using (var ta = new Transaction(Doc, "JA Tools - ativar tipo"))
                    {
                        ta.Start();
                        if (!sym.IsActive) sym.Activate();
                        ta.Commit();
                    }
                    while (true)
                    {
                        string prompt = $"{Title}: {nomeTipo}" + (planta ? $" a {altura:0.00} m — clique perto da parede (Esc termina)" : " — clique na face da parede (Esc termina)");
                        FaceParede face = null;
                        XYZ clique;
                        Level nivel = nivelPlanta;
                        double hAqui = h;
                        try
                        {
                            if (planta)
                            {
                                clique = UiDoc.Selection.PickPoint(ObjectSnapTypes.Nearest | ObjectSnapTypes.Endpoints | ObjectSnapTypes.Midpoints | ObjectSnapTypes.Intersections, prompt);
                                double zRef = (nivel?.ProjectElevation ?? clique.Z) + h;
                                face = FaceMaisProxima(paredes, clique, zRef, raio);
                            }
                            else
                            {
                                Reference r = UiDoc.Selection.PickObject(ObjectType.Face, new SoParedes(), prompt);
                                face = FaceClicada(r);
                                clique = face?.Ponto ?? r.GlobalPoint;
                                nivel = NivelAbaixo(clique.Z);
                                if (nivel != null) hAqui = clique.Z - nivel.ProjectElevation;
                            }
                        }
                        catch (Autodesk.Revit.Exceptions.OperationCanceledException) { break; }
                        catch (Exception ex) { falhas++; if (motivos.Count < 5) motivos.Add(ex.Message); continue; }

                        if (nivel == null) nivel = NivelAbaixo(clique.Z);
                        if (nivel == null) { falhas++; continue; }
                        if (face == null && !fSolto.Value) { semParede++; continue; }
                        if (face == null) semParede++;

                        using (var t = new Transaction(Doc, $"JA Tools - {Title}"))
                        {
                            t.Start();
                            RevitDwgUnifier.Export.FailureSwallower.Attach(t);
                            FamilyInstance fi = null;
                            try { fi = Colocar(sym, tipoColoc, vista, nivel, face, clique, hAqui, afast, out string motivo); if (fi == null && motivo != null && motivos.Count < 5 && !motivos.Contains(motivo)) motivos.Add(motivo); }
                            catch (Exception ex) { if (motivos.Count < 5) motivos.Add(ex.Message); }
                            if (fi == null) { t.RollBack(); falhas++; continue; }
                            if (fEtiq.Value && !Etiquetar(fi, vista, face)) semEtiqueta++;
                            if (t.Commit() == TransactionStatus.Committed) { nesta++; feitos[nomeTipo] = feitos.TryGetValue(nomeTipo, out int c) ? c + 1 : 1; }
                            else falhas++;
                        }
                    }
                    if (nesta > 0) grupo.Assimilate(); else grupo.RollBack();
                }
                if (!fDeNovo.Value) break;
            }

            if (feitos.Count == 0 && falhas == 0) return Result.Cancelled;
            var sb = new StringBuilder($"{feitos.Values.Sum()} peça(s) colocada(s):");
            foreach (var kv in feitos.OrderByDescending(x => x.Value)) sb.Append($"\n• {kv.Key}: {kv.Value}");
            if (semParede > 0) sb.Append($"\n\n{semParede} clique(s) sem parede perto (colocados no ponto, se a opção estava ligada).");
            if (semEtiqueta > 0) sb.Append($"\n{semEtiqueta} sem etiqueta (carregue uma etiqueta da categoria).");
            if (falhas > 0) sb.Append($"\n{falhas} não colocada(s)" + (motivos.Count > 0 ? ":\n• " + string.Join("\n• ", motivos) : "."));
            sb.Append("\n\nCada família colocada se desfaz com um Ctrl+Z.");
            Show(sb.ToString(), falhas > 0 ? ResultKind.Warning : ResultKind.Success);
            return Result.Succeeded;
        }

        private static string Rotulo(FamilySymbol s) => $"{s.Category.Name} › {s.FamilyName} : {s.Name}";

        private Level NivelAbaixo(double z)
        {
            var niveis = RevitUtil.Levels(Doc);
            return niveis.LastOrDefault(l => l.ProjectElevation <= z + 1e-6) ?? niveis.FirstOrDefault();
        }

        // ------------------------------------------------------------------ faces

        /// <summary>The wall face nearest to the click (plan) within the radius, with the click in front of it; at height z.</summary>
        private FaceParede FaceMaisProxima(List<Wall> paredes, XYZ clique, double z, double raio)
        {
            FaceParede melhor = null;
            double dMelhor = raio;
            foreach (Wall w in paredes)
            {
                BoundingBoxXYZ bb = w.get_BoundingBox(null);
                if (bb == null) continue;
                if (clique.X < bb.Min.X - raio || clique.X > bb.Max.X + raio || clique.Y < bb.Min.Y - raio || clique.Y > bb.Max.Y + raio) continue;
                double zp = Math.Max(bb.Min.Z + 0.05, Math.Min(bb.Max.Z - 0.05, z));
                foreach (ShellLayerType lado in new[] { ShellLayerType.Exterior, ShellLayerType.Interior })
                {
                    IList<Reference> refs;
                    try { refs = HostObjectUtils.GetSideFaces(w, lado); } catch { continue; }
                    foreach (Reference r in refs)
                    {
                        Autodesk.Revit.DB.Face f;
                        try { f = w.GetGeometryObjectFromReference(r) as Autodesk.Revit.DB.Face; } catch { continue; }
                        if (f == null) continue;
                        IntersectionResult ir;
                        try { ir = f.Project(new XYZ(clique.X, clique.Y, zp)); } catch { continue; }
                        if (ir == null) continue;
                        XYZ q = ir.XYZPoint;
                        XYZ n = f.ComputeNormal(ir.UVPoint);
                        n = new XYZ(n.X, n.Y, 0);
                        if (n.GetLength() < 0.5) continue;
                        n = n.Normalize();
                        XYZ dq = new XYZ(clique.X - q.X, clique.Y - q.Y, 0);
                        if (dq.DotProduct(n) < -2 / 304.8) continue;               // clicked behind this face (inside the wall or the other side)
                        double dist = dq.GetLength();
                        if (dist > dMelhor) continue;
                        dMelhor = dist;
                        melhor = new FaceParede { Parede = w, Ref = r, Ponto = new XYZ(q.X, q.Y, z), Normal = n };
                    }
                }
            }
            return melhor;
        }

        /// <summary>The wall face picked in a section, elevation or 3D view, at the point clicked.</summary>
        private FaceParede FaceClicada(Reference r)
        {
            if (!(Doc.GetElement(r) is Wall w)) return null;
            if (!(w.GetGeometryObjectFromReference(r) is Autodesk.Revit.DB.Face f)) return null;
            XYZ p = r.GlobalPoint;
            IntersectionResult ir = f.Project(p);
            XYZ n = ir != null ? f.ComputeNormal(ir.UVPoint) : XYZ.BasisY;
            n = new XYZ(n.X, n.Y, 0);
            if (n.GetLength() < 0.5) return null;          // top or bottom of the wall
            return new FaceParede { Parede = w, Ref = r, Ponto = ir?.XYZPoint ?? p, Normal = n.Normalize() };
        }

        // ------------------------------------------------------------------ placing

        private FamilyInstance Colocar(FamilySymbol sym, FamilyPlacementType tipo, View vista, Level nivel, FaceParede face, XYZ clique, double h, double afast, out string motivo)
        {
            motivo = null;
            double z = nivel.ProjectElevation + h;
            XYZ naFace = face != null ? new XYZ(face.Ponto.X, face.Ponto.Y, z) : null;
            XYZ ponto = face != null ? naFace + face.Normal * afast : new XYZ(clique.X, clique.Y, z);
            FamilyInstance fi;
            switch (tipo)
            {
                case FamilyPlacementType.WorkPlaneBased:
                    if (face != null)
                    {
                        XYZ ao = XYZ.BasisZ.CrossProduct(face.Normal).Normalize();       // along the wall: the family stands upright
                        return Doc.Create.NewFamilyInstance(face.Ref, naFace, ao, sym);
                    }
                    Reference piso = FaceDoPiso(vista, clique, nivel, out XYZ noPiso);
                    if (piso != null)
                    {
                        fi = Doc.Create.NewFamilyInstance(piso, noPiso, XYZ.BasisX, sym);
                        if (Math.Abs(h) > 1e-6) Elevar(fi, h - (noPiso.Z - nivel.ProjectElevation));
                        return fi;
                    }
                    try
                    {
                        fi = Doc.Create.NewFamilyInstance(new XYZ(clique.X, clique.Y, nivel.ProjectElevation), sym, nivel, StructuralType.NonStructural);
                        Elevar(fi, h);
                        return fi;
                    }
                    catch { motivo = $"“{sym.FamilyName}” é de face: clique perto de uma parede ou sobre um piso."; return null; }
                case FamilyPlacementType.OneLevelBasedHosted:
                    if (face == null) { motivo = $"“{sym.FamilyName}” é hospedada em parede: clique perto de uma parede."; return null; }
                    fi = Doc.Create.NewFamilyInstance(naFace, sym, face.Parede, nivel, StructuralType.NonStructural);
                    Elevar(fi, h);
                    Doc.Regenerate();
                    try { if (fi.CanFlipFacing && fi.FacingOrientation.DotProduct(face.Normal) < 0) fi.flipFacing(); } catch { }
                    return fi;
                case FamilyPlacementType.OneLevelBased:
                    fi = Doc.Create.NewFamilyInstance(new XYZ(ponto.X, ponto.Y, nivel.ProjectElevation), sym, nivel, StructuralType.NonStructural);
                    Elevar(fi, h);
                    if (face != null) Virar(fi, new XYZ(ponto.X, ponto.Y, nivel.ProjectElevation), face.Normal);
                    return fi;
                case FamilyPlacementType.ViewBased:
                    XYZ pv = vista is ViewPlan ? new XYZ(ponto.X, ponto.Y, vista.Origin.Z) : (face != null ? ponto : clique);
                    fi = Doc.Create.NewFamilyInstance(pv, sym, vista);
                    if (face != null && vista is ViewPlan) Virar(fi, pv, face.Normal);
                    return fi;
            }
            motivo = $"“{sym.FamilyName}”: tipo de colocação não aceito ({tipo}).";
            return null;
        }

        /// <summary>The top face of a floor under the click (ralos, caixas de piso face-based): its reference and the point on it.</summary>
        private Reference FaceDoPiso(View vista, XYZ clique, Level nivel, out XYZ ponto)
        {
            ponto = null;
            double z0 = nivel.ProjectElevation;
            Reference melhor = null;
            double dz = 1.0;                                   // up to 30 cm from the level
            foreach (Floor piso in new FilteredElementCollector(Doc, vista.Id).OfClass(typeof(Floor)).Cast<Floor>())
            {
                BoundingBoxXYZ bb = piso.get_BoundingBox(null);
                if (bb == null || clique.X < bb.Min.X || clique.X > bb.Max.X || clique.Y < bb.Min.Y || clique.Y > bb.Max.Y) continue;
                IList<Reference> refs;
                try { refs = HostObjectUtils.GetTopFaces(piso); } catch { continue; }
                foreach (Reference r in refs)
                {
                    try
                    {
                        if (!(piso.GetGeometryObjectFromReference(r) is Autodesk.Revit.DB.Face f)) continue;
                        IntersectionResult ir = f.Project(new XYZ(clique.X, clique.Y, z0));
                        if (ir == null) continue;
                        XYZ q = ir.XYZPoint;
                        if (Math.Abs(q.X - clique.X) > 1e-3 || Math.Abs(q.Y - clique.Y) > 1e-3) continue;   // outside this face
                        double d = Math.Abs(q.Z - z0);
                        if (d >= dz) continue;
                        dz = d; melhor = r; ponto = q;
                    }
                    catch { }
                }
            }
            return melhor;
        }

        /// <summary>The height above the level (elevation from level, else the offset).</summary>
        private static void Elevar(FamilyInstance fi, double h)
        {
            foreach (var b in new[] { BuiltInParameter.INSTANCE_ELEVATION_PARAM, BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM, BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM })
            {
                Parameter p = fi.get_Parameter(b);
                if (p != null && !p.IsReadOnly) { try { p.Set(h); return; } catch { } }
            }
        }

        /// <summary>Turns the family about the vertical so its front faces the room (away from the wall).</summary>
        private void Virar(FamilyInstance fi, XYZ eixo, XYZ normal)
        {
            try
            {
                Doc.Regenerate();
                XYZ f = new XYZ(fi.FacingOrientation.X, fi.FacingOrientation.Y, 0);
                if (f.GetLength() < 1e-6) return;
                f = f.Normalize();
                double ang = Math.Atan2(f.X * normal.Y - f.Y * normal.X, f.DotProduct(normal));
                if (Math.Abs(ang) < 1e-6) return;
                ElementTransformUtils.RotateElement(Doc, fi.Id, Line.CreateBound(eixo, eixo + XYZ.BasisZ), ang);
            }
            catch { }
        }

        /// <summary>The category tag in front of the piece (30 cm into the room); false when no tag of the category is loaded.</summary>
        private bool Etiquetar(FamilyInstance fi, View vista, FaceParede face)
        {
            try
            {
                if (vista is View3D) return false;
                Doc.Regenerate();
                XYZ p = (fi.Location as LocationPoint)?.Point ?? face?.Ponto;
                if (p == null) return false;
                if (face != null) p += face.Normal * (300 / 304.8);
                IndependentTag.Create(Doc, vista.Id, new Reference(fi), false, TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal, p);
                return true;
            }
            catch { return false; }
        }
    }
}
