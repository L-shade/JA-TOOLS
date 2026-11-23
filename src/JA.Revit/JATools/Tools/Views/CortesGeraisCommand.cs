using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDwgUnifier.Tools.Common;

namespace RevitDwgUnifier.Tools.Views
{
    /// <summary>
    /// Cortes Gerais: on a plan, click the two ends of the cut line and then the side the
    /// section looks at; repeat for as many sections as needed (Esc ends). Each section gets the
    /// full building height (lowest level - margin up to highest level + margin), the chosen
    /// depth, type, template and scale, a sequential name (CORTE A, CORTE B...) and a chain
    /// dimension of the levels.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class CortesGeraisCommand : ToolCommand
    {
        public override string Title => "Cortes Gerais";
        protected override bool PicksInView => true;   // clicks in the view: no progress box

        protected override Result Run()
        {
            if (!(Doc.ActiveView is ViewPlan))
            {
                Show("Abra uma planta (piso ou forro) e execute novamente: os cortes são desenhados sobre a planta.");
                return Result.Cancelled;
            }

            var types = new FilteredElementCollector(Doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .Where(t => t.ViewFamily == ViewFamily.Section).OrderBy(t => t.Name).ToList();
            if (types.Count == 0) { Show("O projeto não tem tipo de vista de corte."); return Result.Cancelled; }
            var templates = RevitUtil.Templates(Doc, Autodesk.Revit.DB.ViewType.Section);

            var dlg = new OptionsDialog(Title, "CortesGerais");
            var fType = dlg.Choice("type", "Tipo de corte", types.Select(t => t.Name).ToList());
            var fTemplate = dlg.Choice("template", "Modelo de vista", RevitUtil.TemplateChoices(templates));
            var fScale = dlg.Number("scale", "Escala 1:", 50, 1, 10000);
            var fDepth = dlg.Number("depth", "Profundidade do corte (m)", 15, 0.1, 10000);
            var fBelow = dlg.Number("below", "Abaixo do nível mais baixo (m)", 1.0, 0, 100);
            var fAbove = dlg.Number("above", "Acima do nível mais alto (m)", 2.0, 0, 100);
            var fPrefix = dlg.Text("prefix", "Nome (seguido de A, B, C...)", "CORTE ");
            var fLevels = dlg.Check("dimLevels", "Cotar os níveis (corrente vertical)", true);
            dlg.Note("Depois do OK: clique o início e o fim da linha de corte e, em seguida, o lado para onde o corte olha. " +
                     "Repita para outros cortes; tecle Esc para terminar.");
            if (!dlg.Show(Owner)) return Result.Cancelled;

            List<Level> levels = RevitUtil.Levels(Doc);
            double zMin = (levels.Count > 0 ? levels.First().ProjectElevation : 0) - RevitUtil.M(fBelow.Value);
            double zMax = (levels.Count > 0 ? levels.Last().ProjectElevation : RevitUtil.M(3)) + RevitUtil.M(fAbove.Value);
            if (zMax - zMin < RevitUtil.M(1)) zMax = zMin + RevitUtil.M(3);

            var created = new List<View>();
            var problems = new List<string>();
            while (true)
            {
                XYZ p1, p2, side;
                try
                {
                    p1 = RevitUtil.PickPoint(UiDoc, $"Corte {created.Count + 1}: início da linha (Esc termina)");
                    p2 = RevitUtil.PickPoint(UiDoc, "Fim da linha de corte");
                    side = RevitUtil.PickPoint(UiDoc, "Clique o lado para onde o corte deve olhar");
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { break; }

                XYZ a = RevitUtil.Flat(p1), b = RevitUtil.Flat(p2);
                if (a.DistanceTo(b) < RevitUtil.M(0.2)) { problems.Add("Linha muito curta ignorada."); continue; }

                using (var t = new Transaction(Doc, "JA Tools - " + Title))
                {
                    t.Start();
                    try
                    {
                        ViewSection v = CreateSection(types[fType.Index].Id, a, b, RevitUtil.Flat(side), zMin, zMax, RevitUtil.M(fDepth.Value));
                        RevitUtil.Configure(v, (int)fScale.Value, RevitUtil.TemplateFromChoice(templates, fTemplate.Index));
                        v.Name = NextName(fPrefix.Value);
                        if (fLevels.Value)
                        {
                            Doc.Regenerate();
                            DimensionLevels(v, levels.Where(l => l.ProjectElevation >= zMin && l.ProjectElevation <= zMax).ToList(), a, b, problems);
                        }
                        created.Add(v);
                        t.Commit();
                    }
                    catch (Exception ex)
                    {
                        t.RollBack();
                        problems.Add("Corte não criado: " + ex.Message);
                    }
                }
            }

            if (created.Count == 0 && problems.Count == 0) return Result.Cancelled;
            string text = $"{created.Count} corte(s) criado(s):\n" + string.Join("\n", created.Select(v => "  • " + v.Name));
            if (problems.Count > 0) text += "\n\nAvisos:\n" + string.Join("\n", problems.Distinct().Select(p => "  • " + p));
            Show(text);
            return Result.Succeeded;
        }

        /// <summary>
        /// Section box: X along the cut line, Y up, Z toward the viewer (Revit sections look
        /// along -Z). Near plane on the cut line (Max.Z = 0), far plane at -depth.
        /// </summary>
        private ViewSection CreateSection(ElementId typeId, XYZ a, XYZ b, XYZ side, double zMin, double zMax, double depth)
        {
            XYZ dir = (b - a).Normalize();
            XYZ mid = (a + b) / 2;
            XYZ look = XYZ.BasisZ.CrossProduct(dir);                 // left of the line
            if ((side - mid).DotProduct(look) < 0) look = -look;      // toward the clicked side
            XYZ basisZ = -look;                                       // view direction points to the viewer
            XYZ basisY = XYZ.BasisZ;
            XYZ basisX = basisY.CrossProduct(basisZ);

            var tf = Transform.Identity;
            tf.Origin = new XYZ(mid.X, mid.Y, 0);
            tf.BasisX = basisX;
            tf.BasisY = basisY;
            tf.BasisZ = basisZ;

            double half = a.DistanceTo(b) / 2;
            var box = new BoundingBoxXYZ
            {
                Transform = tf,
                Min = new XYZ(-half, zMin, -depth),
                Max = new XYZ(half, zMax, 0)
            };
            return ViewSection.CreateSection(Doc, typeId, box);
        }

        private string NextName(string prefix)
        {
            var names = new HashSet<string>(new FilteredElementCollector(Doc).OfClass(typeof(View)).Cast<View>().Select(v => v.Name),
                                             StringComparer.OrdinalIgnoreCase);
            for (int i = 0; ; i++)
            {
                string n = RevitUtil.Sanitize(prefix + GridTools.Letters(i));
                if (!names.Contains(n)) return n;
            }
        }

        /// <summary>Vertical chain of the levels, drawn at the start of the cut line.</summary>
        private void DimensionLevels(ViewSection v, List<Level> levels, XYZ a, XYZ b, List<string> problems)
        {
            if (levels.Count < 2) return;
            try
            {
                XYZ dir = (b - a).Normalize();
                XYZ at = a + dir * RevitUtil.M(0.5);
                var line = Line.CreateBound(new XYZ(at.X, at.Y, levels.First().ProjectElevation - RevitUtil.M(0.5)),
                                            new XYZ(at.X, at.Y, levels.Last().ProjectElevation + RevitUtil.M(0.5)));
                RevitUtil.CreateDimension(Doc, v, line, levels.Select(l => new Reference(l)));
            }
            catch (Exception ex)
            {
                RevitUtil.AddOnce(problems, "Cota de níveis: " + ex.Message);
            }
        }
    }
}
