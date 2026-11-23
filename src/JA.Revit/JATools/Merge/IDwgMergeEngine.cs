using RevitDwgUnifier.Contracts;

namespace RevitDwgUnifier.Merge
{
    /// <summary>
    /// Abstraction over "whatever can combine N DWG files into one DWG with N layouts".
    ///
    /// Revit's API has no switch for "export several sheets into ONE DWG with one layout each"
    /// (Document.Export always writes one DWG per view/sheet), so the consolidation has to be
    /// done by a real DWG engine. The shipped implementation drives AutoCAD Core Console
    /// (<see cref="CoreConsoleMergeEngine"/>). Another engine (e.g. ODA Drawings SDK, licensed)
    /// can be plugged in by implementing this interface and consuming the same manifest.
    /// </summary>
    public interface IDwgMergeEngine
    {
        string Name { get; }
        bool IsAvailable(out string reason);
        MergeResult Merge(MergeManifest manifest, string manifestPath, System.Threading.CancellationToken token);
    }
}
