using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using RevitDwgUnifier.Tools.Common;

namespace RevitDwgUnifier.Tools.Views
{
    /// <summary>Shared room picking: the dialog lists every placed room; rooms selected in Revit
    /// before clicking the button come pre-checked.</summary>
    public abstract class RoomToolCommand : ToolCommand
    {
        protected List<Room> AllRooms;

        protected OptionsDialog.MultiField AddRoomList(OptionsDialog dlg)
        {
            AllRooms = RevitUtil.Rooms(Doc);
            if (AllRooms.Count == 0) throw new InvalidOperationException("O projeto não tem ambientes colocados.");
            var preselected = new HashSet<ElementId>(UiDoc.Selection.GetElementIds());
            var field = dlg.Multi("rooms", "Ambientes", AllRooms.Select(RevitUtil.RoomLabel).ToList());
            if (preselected.Count > 0)
                for (int i = 0; i < AllRooms.Count; i++) field.Checked[i] = preselected.Contains(AllRooms[i].Id);
            return field;
        }

        protected static string RoomTitle(Room r) =>
            (string.IsNullOrWhiteSpace(r.Number) ? "" : r.Number + " - ") + RevitUtil.RoomName(r);

        protected void Report(List<View> created, List<string> problems)
        {
            string text = $"{created.Count} vista(s) criada(s)." +
                          (created.Count > 0 ? "\n" + string.Join("\n", created.Take(25).Select(v => "  • " + v.Name)) + (created.Count > 25 ? "\n  ..." : "") : "");
            if (problems.Count > 0) text += "\n\nAvisos:\n" + string.Join("\n", problems.Select(p => "  • " + p));
            Show(text);
            if (created.Count > 0) TryOpen(created[0]);
        }
    }

    // Vistas por Ambiente (planta + elevações + isométrico): VistasPorAmbienteCommand.cs (1.17)
}
