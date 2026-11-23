using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDwgUnifier.Tools.Common;

namespace RevitDwgUnifier.Tools.Dimensions
{
    /// <summary>
    /// Dimensions point-like family instances (lights, outlets, switches, plumbing points) from
    /// the walls. Instances are grouped in ROWS (same line, 5 cm tolerance); every row gets one
    /// chain: nearest wall face before the row, the centre plane of every instance, nearest wall
    /// face after the row.
    ///
    ///   Free-standing points (lights, floor drains): rows along X and along Y.
    ///   Points hosted on / facing a wall (outlets, switches, taps): one row along the wall,
    ///   placed in front of it, measured from the walls that meet it.
    /// The family must have its "Center (Left/Right)" / "Center (Front/Back)" reference planes
    /// defined as references (standard templates do).
    /// </summary>
    /// <remarks>1.17: Cotas de Luminárias, de Pontos Elétricos e de Pontos Hidrossanitários viraram
    /// uma ferramenta só (Cotas de Pontos) com a escolha "Pontos" no topo; cada escolha tem as suas
    /// categorias e o seu padrão de "ao longo da parede".</remarks>
    [Transaction(TransactionMode.Manual)]
    public sealed class CotasDePontosCommand : DimToolCommand
    {
        public override string Title => "Cotas de Pontos";
        private const string PrefsKey = "CotasDePontos";

        private sealed class Modo
        {
            public string Key, Label;
            public bool WallRows;
            public Tuple<BuiltInCategory, string, bool>[] Categories;
        }

        private static readonly Modo[] Modos =
        {
            new Modo { Key = "lumi", Label = "Luminárias", WallRows = false, Categories = new[]
            {
                Tuple.Create(BuiltInCategory.OST_LightingFixtures, "Luminárias", true),
                Tuple.Create(BuiltInCategory.OST_GenericModel, "Modelos genéricos", false)
            } },
            new Modo { Key = "eletrico", Label = "Elétricos", WallRows = true, Categories = new[]
            {
                Tuple.Create(BuiltInCategory.OST_ElectricalFixtures, "Dispositivos elétricos (tomadas)", true),
                Tuple.Create(BuiltInCategory.OST_LightingDevices, "Dispositivos de iluminação (interruptores)", true),
                Tuple.Create(BuiltInCategory.OST_DataDevices, "Dispositivos de dados", true),
                Tuple.Create(BuiltInCategory.OST_CommunicationDevices, "Dispositivos de comunicação", false),
                Tuple.Create(BuiltInCategory.OST_TelephoneDevices, "Dispositivos de telefone", false),
                Tuple.Create(BuiltInCategory.OST_ElectricalEquipment, "Equipamentos elétricos (quadros)", false),
                Tuple.Create(BuiltInCategory.OST_GenericModel, "Modelos genéricos", false)
            } },
            new Modo { Key = "hidro", Label = "Hidrossanitários", WallRows = true, Categories = new[]
            {
                Tuple.Create(BuiltInCategory.OST_PlumbingFixtures, "Peças hidrossanitárias", true),
                Tuple.Create(BuiltInCategory.OST_PipeAccessory, "Acessórios de tubulação (registros, ralos)", true),
                Tuple.Create(BuiltInCategory.OST_PipeFitting, "Conexões de tubulação", false),
                Tuple.Create(BuiltInCategory.OST_MechanicalEquipment, "Equipamentos mecânicos", false),
                Tuple.Create(BuiltInCategory.OST_GenericModel, "Modelos genéricos", false)
            } }
        };

        protected override Result Run()
        {
            ViewPlan view = RequirePlan();
            if (view == null) return Result.Cancelled;

            var dlg = new OptionsDialog(Title, PrefsKey);
            var fMode = dlg.Choice("mode", "Pontos", Modos.Select(m => m.Label).ToList(), DefaultMode(view));
            var fCats = new OptionsDialog.MultiField[Modos.Length];
            var fWallRows = new OptionsDialog.BoolField[Modos.Length];
            for (int m = 0; m < Modos.Length; m++)
            {
                var md = Modos[m];
                fCats[m] = dlg.Multi("cats." + md.Key, "Categorias", md.Categories.Select(c => c.Item2).ToList(), false);
                if (ToolPrefs.Get(PrefsKey + ".cats." + md.Key) == null)
                    for (int i = 0; i < md.Categories.Length; i++) fCats[m].Checked[i] = md.Categories[i].Item3;
                fCats[m].VisibleWhen(fMode, m);
            }
            var fType = AddTypeChoice(dlg);
            var fSel = dlg.Check("sel", "Somente os elementos selecionados (senão: todos os visíveis)", UiDoc.Selection.GetElementIds().Count > 0);
            for (int m = 0; m < Modos.Length; m++)
            {
                fWallRows[m] = dlg.Check("wallRows." + Modos[m].Key, "Elementos em parede: cotar ao longo da parede", Modos[m].WallRows);
                fWallRows[m].VisibleWhen(fMode, m);
            }
            var fOff = dlg.Number("offset", "Afastamento da linha de cota (m)", 0.3, 0, 10);
            var fTol = dlg.Number("tol", "Tolerância para alinhar em fila (cm)", 5, 0.1, 100);
            dlg.Note("Luminárias: use numa planta de forro. Pontos alinhados formam uma fila com uma única corrente.");
            if (!dlg.Show(Owner)) return Result.Cancelled;
            DimensionType type = SelectedType(fType);
            int mi = Math.Max(0, fMode.Index);
            Modo modo = Modos[mi];

            var cats = fCats[mi].SelectedIndices.Select(i => modo.Categories[i].Item1).ToList();
            IEnumerable<Element> source = fSel.Value
                ? UiDoc.Selection.GetElementIds().Select(id => Doc.GetElement(id))
                : new FilteredElementCollector(Doc, view.Id).WherePasses(new ElementMulticategoryFilter(cats)).WhereElementIsNotElementType();
            var instances = source.OfType<FamilyInstance>()
                .Where(fi => fi.Category != null && cats.Any(c => fi.Category.Id.Value == (long)c)).ToList();
            if (instances.Count == 0) { Show("Nenhum elemento das categorias escolhidas foi encontrado."); return Result.Cancelled; }

            List<Wall> walls = DimEngine.WallsInView(Doc, view);
            int rowCount;
            using (var t = new Transaction(Doc, "JA Tools - " + Title))
            {
                t.Start();
                rowCount = DimPoints(view, instances, walls, fWallRows[mi].Value, RevitUtil.M(fTol.Value / 100.0), RevitUtil.M(fOff.Value), type);
                t.Commit();
            }
            Report($"{instances.Count} elemento(s), {rowCount} fila(s).");
            return Result.Succeeded;
        }

        /// <summary>A ceiling plan opens on "Luminárias" the first time.</summary>
        private static int DefaultMode(ViewPlan view) => view.ViewType == Autodesk.Revit.DB.ViewType.CeilingPlan ? 0 : 1;
    }
}
