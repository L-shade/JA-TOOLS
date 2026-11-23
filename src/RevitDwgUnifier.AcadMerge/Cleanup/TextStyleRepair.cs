using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.GraphicsInterface;
using RevitDwgUnifier.Contracts;

namespace RevitDwgUnifier.AcadMerge.Cleanup
{
    /// <summary>
    /// PITFALL #3 - Text formatting &amp; custom font distortion (DWG half).
    ///
    /// The Revit side already maps fonts and exports text as "Exact". This pass makes the final
    /// DWG robust on ANY receiving machine:
    ///  * SHX styles whose .shx cannot be found  -> TrueType fallback (otherwise AutoCAD asks for
    ///    a replacement on open, or silently uses simplex.shx with different widths).
    ///  * SHX styles used by text containing non-ASCII characters (accents, Ø, ², ±, CJK) and
    ///    without a big font -> TrueType fallback (otherwise characters render as "?").
    ///  * TrueType styles whose .ttf is not installed -> fallback face.
    ///  * Corrupt width factors (0 / huge) reset to 1.0.
    ///  * Optional MText width slack (+x%) to absorb residual metric differences so lines that
    ///    fitted in Revit do not wrap onto an extra line.
    /// Bold/italic flags of the original style are preserved.
    /// </summary>
    internal static class TextStyleRepair
    {
        public static void Repair(Database db, MergeOptions o, MergeLog log)
        {
            int replaced = 0, widthFixed = 0, slackApplied = 0;
            string fontsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var unicodeStyles = new HashSet<ObjectId>();
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId btrId in bt)
                {
                    var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
                    if (btr.IsFromExternalReference || btr.IsDependent) continue;
                    foreach (ObjectId id in btr)
                    {
                        var obj = tr.GetObject(id, OpenMode.ForRead);
                        if (obj is DBText t)
                        {
                            if (o.ForceTrueTypeForUnicode && HasNonAscii(t.TextString)) unicodeStyles.Add(t.TextStyleId);
                        }
                        else if (obj is MText m)
                        {
                            if (o.ForceTrueTypeForUnicode && HasNonAscii(m.Text)) unicodeStyles.Add(m.TextStyleId);
                            if (o.MTextWidthSlack > 0 && m.Width > 0)
                            {
                                m.UpgradeOpen();
                                m.Width *= 1.0 + o.MTextWidthSlack;
                                slackApplied++;
                            }
                        }
                    }
                }

                var tst = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
                foreach (ObjectId id in tst)
                {
                    var ts = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    if (ts.IsShapeFile) continue; // linetype shape containers, not text

                    FontDescriptor fd = ts.Font;
                    bool isTrueType = !string.IsNullOrEmpty(fd.TypeFace);
                    string reason = null;

                    if (!isTrueType)
                    {
                        if (!FileFound(db, ts.FileName, FindFileHint.CompiledShapeFile))
                            reason = $"SHX '{ts.FileName}' not found";
                        else if (unicodeStyles.Contains(id) && string.IsNullOrEmpty(ts.BigFontFileName))
                            reason = $"SHX '{ts.FileName}' used by non-ASCII text";
                    }
                    else if (!string.IsNullOrEmpty(ts.FileName) &&
                             !File.Exists(Path.Combine(fontsDir, Path.GetFileName(ts.FileName))) &&
                             !FileFound(db, ts.FileName, FindFileHint.TrueTypeFontFile))
                    {
                        reason = $"TrueType '{fd.TypeFace}' ({ts.FileName}) not installed";
                    }

                    if (reason != null)
                    {
                        ts.UpgradeOpen();
                        ts.FileName = o.FallbackTrueTypeFile;
                        ts.BigFontFileName = "";
                        ts.Font = new FontDescriptor(o.FallbackTrueTypeFace, fd.Bold, fd.Italic, 0, 0);
                        replaced++;
                        log.Info($"Text style '{ts.Name}': {reason} -> {o.FallbackTrueTypeFace}.");
                    }

                    if (ts.XScale <= 0.01 || ts.XScale > 10.0)
                    {
                        if (!ts.IsWriteEnabled) ts.UpgradeOpen();
                        ts.XScale = 1.0;
                        widthFixed++;
                    }
                }
                tr.Commit();
            }
            log.Stat($"Text styles replaced: {replaced}, width factors reset: {widthFixed}, MText widened: {slackApplied}");
        }

        private static bool HasNonAscii(string s) => !string.IsNullOrEmpty(s) && s.Any(c => c > 127);

        private static bool FileFound(Database db, string file, FindFileHint hint)
        {
            if (string.IsNullOrWhiteSpace(file)) return false;
            try
            {
                string found = HostApplicationServices.Current.FindFile(file, db, hint);
                return !string.IsNullOrEmpty(found);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                return false;
            }
        }
    }
}
