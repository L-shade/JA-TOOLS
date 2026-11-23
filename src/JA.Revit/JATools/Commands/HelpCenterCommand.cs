using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDwgUnifier.Tools.Common;

namespace RevitDwgUnifier.Commands
{
    /// <summary>"Central de Ajuda" button: opens the offline help page (all tools, step by step).</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class HelpCenterCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            RevitDwgUnifier.Infrastructure.Telemetria.Uso("Central de Ajuda");
            HelpCenter.Open();
            return Result.Succeeded;
        }
    }

    /// <summary>Help is available always, even with no project open.</summary>
    public sealed class AlwaysAvailable : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication app, CategorySet selectedCategories) => true;
    }
}
