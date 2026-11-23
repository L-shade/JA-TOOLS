using Autodesk.Revit.DB;
using RevitDwgUnifier.Infrastructure;

namespace RevitDwgUnifier.Export
{
    /// <summary>
    /// Deletes warnings raised by the temporary staging transactions (e.g. "view is already
    /// on a sheet", "elements outside crop region") so no modal Revit dialog interrupts a
    /// batch export. Errors are NOT swallowed: they roll the transaction back and the item is
    /// reported as failed.
    /// </summary>
    public sealed class FailureSwallower : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            foreach (FailureMessageAccessor msg in accessor.GetFailureMessages())
            {
                if (msg.GetSeverity() == FailureSeverity.Warning)
                {
                    Logger.Debug("Suppressed Revit warning: " + msg.GetDescriptionText());
                    accessor.DeleteWarning(msg);
                }
            }
            return FailureProcessingResult.Continue;
        }

        public static void Attach(Transaction t)
        {
            RevitDwgUnifier.Tools.Common.ToolProgress.Pulse(null);   // keeps the progress box painted
            FailureHandlingOptions o = t.GetFailureHandlingOptions();
            o.SetFailuresPreprocessor(new FailureSwallower());
            o.SetClearAfterRollback(true);
            t.SetFailureHandlingOptions(o);
        }
    }
}
