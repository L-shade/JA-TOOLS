using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitDwgUnifier.Commands
{
    /// <summary>Button is enabled only for an open project (not a family editor, not zero documents).</summary>
    public sealed class CommandAvailability : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication app, CategorySet selectedCategories)
        {
            Document doc = app?.ActiveUIDocument?.Document;
            return doc != null && !doc.IsFamilyDocument;
        }
    }
}
