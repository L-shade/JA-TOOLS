using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDwgUnifier.Tools.Common;
using RevitDwgUnifier.Tools.Views;

namespace RevitDwgUnifier.Tools.Publish
{
    /// <summary>Sheets of the project, for the publication tools (pre-checked from the selection).</summary>
    public abstract class SheetToolCommand : ToolCommand
    {
        protected List<ViewSheet> Sheets;

        protected OptionsDialog.MultiField AddSheetList(OptionsDialog dlg, string key = "sheets", string label = "Pranchas")
        {
            Sheets = new FilteredElementCollector(Doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .Where(s => !s.IsPlaceholder && !s.IsTemplate)
                .OrderBy(s => s.SheetNumber, new NaturalStringComparer()).ToList();
            if (Sheets.Count == 0) throw new InvalidOperationException("O projeto não tem pranchas.");
            var pre = new HashSet<ElementId>(UiDoc.Selection.GetElementIds());
            if (Doc.ActiveView is ViewSheet active) pre.Add(active.Id);
            var f = dlg.Multi(key, label, Sheets.Select(s => s.SheetNumber + " - " + s.Name).ToList(), false);
            if (pre.Count > 0) for (int i = 0; i < Sheets.Count; i++) f.Checked[i] = pre.Contains(Sheets[i].Id);
            return f;
        }

        protected static string NextFree(string number, HashSet<string> used)
        {
            string n = string.IsNullOrWhiteSpace(number) ? "A101" : number.Trim();
            while (used.Contains(n)) n = LegendEngine.NextNumber(n);
            return n;
        }
    }

    // =====================================================================================
    //  Renomear Pranchas
    // =====================================================================================

    /// <summary>
    /// Renomear Pranchas: on the chosen sheets, find/replace in number and/or name, prefix and
    /// suffix, upper case, and sequential renumbering (start + step). Numbers are changed in two
    /// steps (temporary numbers first), so swapping numbers between sheets never collides.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class RenomearPranchasCommand : SheetToolCommand
    {
        public override string Title => "Renomear Pranchas";

        protected override Result Run()
        {
            var dlg = new OptionsDialog(Title, "RenomearPranchas", "Renomear");
            var fSheets = AddSheetList(dlg);
            dlg.Section("Nome");
            var fFind = dlg.Text("find", "Procurar", "");
            var fRepl = dlg.Text("replace", "Substituir por", "");
            var fWhere = dlg.Choice("where", "Em", new[] { "Nome", "Número", "Nome e número" });
            var fPrefix = dlg.Text("prefix", "Prefixo do nome", "");
            var fSuffix = dlg.Text("suffix", "Sufixo do nome", "");
            var fUpper = dlg.Check("upper", "Nome em MAIÚSCULAS", false);
            dlg.Section("Numeração");
            var fRenum = dlg.Check("renum", "Renumerar em sequência (na ordem da lista)", false);
            var fStart = dlg.Text("start", "Primeiro número", "A101");
            var fStep = dlg.Number("step", "Passo", 1, 1, 100);
            fStart.EnabledWhen(fRenum); fStep.EnabledWhen(fRenum);
            dlg.Note("Exemplo: procurar \"PLANTA\" e substituir por \"PLANTA BAIXA\"; ou renumerar A101, A102… Números em uso por pranchas fora da lista são pulados.");
            if (!dlg.Show(Owner)) return Result.Cancelled;

            var chosen = fSheets.SelectedIndices.Select(i => Sheets[i]).ToList();
            if (chosen.Count == 0) { Show("Marque ao menos uma prancha."); return Result.Cancelled; }
            var others = new HashSet<string>(Sheets.Except(chosen).Select(s => s.SheetNumber), StringComparer.OrdinalIgnoreCase);
            var log = new List<string>();
            int changed = 0;

            using (var t = new Transaction(Doc, "JA Tools - " + Title))
            {
                t.Start();
                // ---- new numbers
                var newNumbers = new Dictionary<ElementId, string>();
                string next = fStart.Value;
                foreach (ViewSheet s in chosen)
                {
                    string num = s.SheetNumber;
                    if (fRenum.Value)
                    {
                        next = NextFree(next, others);
                        num = next;
                        others.Add(num);
                        for (int k = 0; k < (int)fStep.Value; k++) next = LegendEngine.NextNumber(next);
                    }
                    else if (fWhere.Index >= 1 && fFind.Value.Length > 0) num = num.Replace(fFind.Value, fRepl.Value);
                    newNumbers[s.Id] = num;
                }
                // two steps: temporary numbers first, so A101 <-> A102 swaps work
                int tmp = 0;
                foreach (ViewSheet s in chosen.Where(s => newNumbers[s.Id] != s.SheetNumber))
                    try { s.SheetNumber = "~JA_tmp_" + (++tmp) + "_" + s.Id.Value; } catch { }
                // numbers in use, read once and kept up to date (was one collector of every sheet per sheet)
                var all = new HashSet<string>(new FilteredElementCollector(Doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                    .Select(x => x.SheetNumber), StringComparer.OrdinalIgnoreCase);
                foreach (ViewSheet s in chosen)
                {
                    string oldNum = s.SheetNumber.StartsWith("~JA_tmp_", StringComparison.Ordinal) ? null : s.SheetNumber;
                    string wanted = newNumbers[s.Id];
                    if (oldNum == null)
                    {
                        string final = wanted, temp = s.SheetNumber;
                        if (all.Contains(final)) final = NextFree(final, all);     // the sheet's own (temporary) number never matches
                        try
                        {
                            s.SheetNumber = final;
                            all.Remove(temp);
                            all.Add(final);
                        }
                        catch (Exception ex) { log.Add($"{wanted}: número não aplicado ({ex.Message})."); }
                    }

                    string name = s.Name;
                    if (fWhere.Index != 1 && fFind.Value.Length > 0) name = name.Replace(fFind.Value, fRepl.Value);
                    name = fPrefix.Value + name + fSuffix.Value;
                    if (fUpper.Value) name = name.ToUpperInvariant();
                    if (name != s.Name) { try { s.Name = name; } catch (Exception ex) { log.Add($"{s.SheetNumber}: nome não aplicado ({ex.Message})."); } }
                    changed++;
                }
                t.Commit();
            }
            string text = $"{changed} prancha(s) atualizada(s).\n" + string.Join("\n", chosen.Take(20).Select(s => "  • " + s.SheetNumber + " - " + s.Name)) + (chosen.Count > 20 ? "\n  …" : "");
            if (log.Count > 0) text += "\n\nAvisos:\n" + string.Join("\n", log.Take(10).Select(l => "  • " + l));
            Show(text);
            return Result.Succeeded;
        }
    }

    // =====================================================================================
    //  Duplicar Pranchas
    // =====================================================================================

    /// <summary>
    /// Duplicar Pranchas: copies of the chosen sheets with the same title block (and its instance
    /// parameters), the sheet's own annotation (texts, lines, symbols), legends and schedules placed
    /// again, and the views either left out, duplicated (with or without detailing) or as dependents,
    /// each viewport in the same position.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class DuplicarPranchasCommand : SheetToolCommand
    {
        public override string Title => "Duplicar Pranchas";

        protected override Result Run()
        {
            var dlg = new OptionsDialog(Title, "DuplicarPranchas", "Duplicar");
            var fSheets = AddSheetList(dlg);
            var fCopies = dlg.Number("copies", "Cópias de cada prancha", 1, 1, 50);
            var fViews = dlg.Choice("views", "Vistas", new[]
            {
                "Não copiar (só carimbo, legendas, tabelas e anotações da folha)",
                "Duplicar as vistas",
                "Duplicar as vistas com detalhamento",
                "Duplicar como dependentes"
            }, 2);
            var fSuffix = dlg.Text("suffix", "Acrescentar ao nome", " - CÓPIA");
            dlg.Note("Legendas e tabelas aparecem de novo na cópia (o Revit permite a mesma legenda/tabela em várias folhas). O número é o próximo livre.");
            if (!dlg.Show(Owner)) return Result.Cancelled;

            var chosen = fSheets.SelectedIndices.Select(i => Sheets[i]).ToList();
            if (chosen.Count == 0) { Show("Marque ao menos uma prancha."); return Result.Cancelled; }
            var used = new HashSet<string>(Sheets.Select(s => s.SheetNumber), StringComparer.OrdinalIgnoreCase);
            var created = new List<ViewSheet>();
            var problems = new List<string>();

            using (var t = new Transaction(Doc, "JA Tools - " + Title))
            {
                t.Start();
                RevitDwgUnifier.Export.FailureSwallower.Attach(t);
                foreach (ViewSheet src in chosen)
                    for (int c = 0; c < (int)fCopies.Value; c++)
                    {
                        ToolProgress.Report(created.Count, chosen.Count * (int)fCopies.Value, $"Duplicando {src.SheetNumber} - {src.Name}");
                        try { created.Add(Copy(src, fViews.Index, fSuffix.Value, used, problems)); }
                        catch (Exception ex) { RevitUtil.AddOnce(problems, $"{src.SheetNumber}: {ex.Message}"); }
                    }
                t.Commit();
            }
            string text = $"{created.Count} prancha(s) criada(s):\n" + string.Join("\n", created.Take(20).Select(s => "  • " + s.SheetNumber + " - " + s.Name));
            if (problems.Count > 0) text += "\n\nAvisos:\n" + string.Join("\n", problems.Take(12).Select(p => "  • " + p));
            Show(text);
            return Result.Succeeded;
        }

        private ViewSheet Copy(ViewSheet src, int viewMode, string suffix, HashSet<string> used, List<string> problems)
        {
            Element tbInst = new FilteredElementCollector(Doc, src.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).FirstElement();
            ViewSheet dst = ViewSheet.Create(Doc, tbInst?.GetTypeId() ?? ElementId.InvalidElementId);
            string number = NextFree(LegendEngine.NextNumber(src.SheetNumber), used);
            used.Add(number);
            dst.SheetNumber = number;
            dst.Name = src.Name + (suffix ?? "");
            Doc.Regenerate();

            // title block instance values (custom fields of the stamp)
            Element tbNew = new FilteredElementCollector(Doc, dst.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).FirstElement();
            if (tbInst != null && tbNew != null)
            {
                foreach (Parameter p in tbInst.Parameters)
                {
                    if (p.IsReadOnly || !p.HasValue) continue;
                    Parameter q = tbNew.get_Parameter(p.Definition);
                    if (q == null || q.IsReadOnly) continue;
                    try
                    {
                        switch (p.StorageType)
                        {
                            case StorageType.String: q.Set(p.AsString() ?? ""); break;
                            case StorageType.Double: q.Set(p.AsDouble()); break;
                            case StorageType.Integer: q.Set(p.AsInteger()); break;
                            case StorageType.ElementId: q.Set(p.AsElementId()); break;
                        }
                    }
                    catch { }
                }
            }

            // sheet annotation (texts, lines, symbols, images) except title block and viewports
            var own = new FilteredElementCollector(Doc).OwnedByView(src.Id).WhereElementIsNotElementType().ToElements()
                .Where(e => e.Category != null && !(e is Viewport) && !(e is ScheduleSheetInstance) &&
                            e.Category.Id.Value != (long)BuiltInCategory.OST_TitleBlocks && !(e is ViewSheet))
                .Select(e => e.Id).ToList();
            if (own.Count > 0)
            {
                try { ElementTransformUtils.CopyElements(src, own, dst, Transform.Identity, new CopyPasteOptions()); }
                catch (Exception ex) { RevitUtil.AddOnce(problems, "Anotações da folha não copiadas: " + ex.Message); }
            }

            // schedules
            foreach (ScheduleSheetInstance si in new FilteredElementCollector(Doc, src.Id).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>())
            {
                if (si.IsTitleblockRevisionSchedule) continue;
                try { ScheduleSheetInstance.Create(Doc, dst.Id, si.ScheduleId, si.Point); }
                catch (Exception ex) { RevitUtil.AddOnce(problems, "Tabela não colocada: " + ex.Message); }
            }

            // views
            foreach (ElementId vpId in src.GetAllViewports())
            {
                var vp = (Viewport)Doc.GetElement(vpId);
                var view = (View)Doc.GetElement(vp.ViewId);
                ElementId placeId;
                if (view.ViewType == Autodesk.Revit.DB.ViewType.Legend) placeId = view.Id;
                else if (viewMode == 0) continue;
                else
                {
                    var opt = viewMode == 1 ? ViewDuplicateOption.Duplicate : viewMode == 2 ? ViewDuplicateOption.WithDetailing : ViewDuplicateOption.AsDependent;
                    if (!view.CanViewBeDuplicated(opt)) { RevitUtil.AddOnce(problems, $"{view.Name}: não pode ser duplicada desse jeito."); continue; }
                    placeId = view.Duplicate(opt);
                }
                try
                {
                    Viewport nv = Viewport.Create(Doc, dst.Id, placeId, vp.GetBoxCenter());
                    try { nv.ChangeTypeId(vp.GetTypeId()); } catch { }
                    Doc.Regenerate();
                    nv.SetBoxCenter(vp.GetBoxCenter());
                }
                catch (Exception ex) { RevitUtil.AddOnce(problems, $"{view.Name}: não colocada ({ex.Message})."); }
            }
            return dst;
        }
    }

    // =====================================================================================
    //  Alinhar Vistas entre Pranchas
    // =====================================================================================

    /// <summary>
    /// Alinhar Vistas: places the viewports of the chosen sheets like those of a reference sheet.
    ///   * same model point: plans of different levels line up on paper (the classic "same origin"
    ///     alignment): the project origin falls on the same point of every sheet;
    ///   * same position: each viewport takes the centre of the reference viewport of the same kind
    ///     (paired by view type and size order).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class AlinharVistasCommand : SheetToolCommand
    {
        public override string Title => "Alinhar Vistas";

        protected override Result Run()
        {
            var dlg = new OptionsDialog(Title, "AlinharVistas", "Alinhar");
            var probe = new FilteredElementCollector(Doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .Where(s => !s.IsPlaceholder).OrderBy(s => s.SheetNumber, new NaturalStringComparer()).ToList();
            if (probe.Count < 2) { Show("São necessárias ao menos duas pranchas."); return Result.Cancelled; }
            int activeIndex = Doc.ActiveView is ViewSheet a ? Math.Max(0, probe.FindIndex(s => s.Id == a.Id)) : 0;
            var fRef = dlg.Choice("ref", "Prancha de referência", probe.Select(s => s.SheetNumber + " - " + s.Name).ToList(), activeIndex);
            var fSheets = AddSheetList(dlg, "targets", "Pranchas a alinhar");
            var fMode = dlg.Choice("mode", "Como", new[] { "Mesmo ponto do modelo (plantas alinhadas)", "Mesma posição na folha" });
            dlg.Note("Mesmo ponto do modelo: vale para plantas e vistas com região de recorte; o ponto (0,0) do projeto cai no mesmo lugar em todas as pranchas.");
            if (!dlg.Show(Owner)) return Result.Cancelled;

            ViewSheet reference = probe[fRef.Index];
            var targets = fSheets.SelectedIndices.Select(i => Sheets[i]).Where(s => s.Id != reference.Id).ToList();
            var refVps = reference.GetAllViewports().Select(id => (Viewport)Doc.GetElement(id)).ToList();
            if (refVps.Count == 0) { Show("A prancha de referência não tem vistas."); return Result.Cancelled; }
            int moved = 0, skipped = 0;

            using (var t = new Transaction(Doc, "JA Tools - " + Title))
            {
                t.Start();
                foreach (ViewSheet s in targets)
                {
                    var vps = s.GetAllViewports().Select(id => (Viewport)Doc.GetElement(id)).ToList();
                    foreach (Viewport vp in vps)
                    {
                        var view = (View)Doc.GetElement(vp.ViewId);
                        Viewport match = Pair(vp, view, vps, refVps);
                        if (match == null) { skipped++; continue; }
                        XYZ delta;
                        if (fMode.Index == 0)
                        {
                            XYZ a1 = SheetPointOfOrigin(match), a2 = SheetPointOfOrigin(vp);
                            if (a1 == null || a2 == null) { skipped++; continue; }
                            delta = a1 - a2;
                        }
                        else delta = match.GetBoxCenter() - vp.GetBoxCenter();
                        if (delta.GetLength() < 1e-9) continue;
                        vp.SetBoxCenter(vp.GetBoxCenter() + new XYZ(delta.X, delta.Y, 0));
                        moved++;
                    }
                }
                t.Commit();
            }
            Show($"{moved} vista(s) alinhada(s) em {targets.Count} prancha(s)." + (skipped > 0 ? $"\n{skipped} vista(s) sem par na prancha de referência (ou sem região de recorte) ficaram onde estavam." : ""));
            return Result.Succeeded;
        }

        /// <summary>Reference viewport of the same view type, in the same size order.</summary>
        private Viewport Pair(Viewport vp, View view, List<Viewport> mine, List<Viewport> refs)
        {
            Func<Viewport, Autodesk.Revit.DB.ViewType> typeOf = x => ((View)Doc.GetElement(x.ViewId)).ViewType;
            Func<Viewport, double> area = x => { Outline o = x.GetBoxOutline(); return (o.MaximumPoint.X - o.MinimumPoint.X) * (o.MaximumPoint.Y - o.MinimumPoint.Y); };
            var sameMine = mine.Where(x => typeOf(x) == view.ViewType).OrderByDescending(area).ToList();
            var sameRef = refs.Where(x => typeOf(x) == view.ViewType).OrderByDescending(area).ToList();
            int i = sameMine.IndexOf(vp);
            return i >= 0 && i < sameRef.Count ? sameRef[i] : null;
        }

        /// <summary>Where the project origin falls on the sheet (views with an active crop box, no rotation).</summary>
        private XYZ SheetPointOfOrigin(Viewport vp)
        {
            var view = (View)Doc.GetElement(vp.ViewId);
            if (!view.CropBoxActive) return null;
            BoundingBoxXYZ cb = view.CropBox;
            XYZ cropCenter = cb.Transform.OfPoint((cb.Min + cb.Max) / 2);
            double scale = Math.Max(1, view.Scale);
            XYZ right = view.RightDirection, up = view.UpDirection;
            // the viewport box centre is the crop centre (annotation crop ignored: same view type -> same offsets)
            XYZ c = vp.GetBoxCenter();
            XYZ rel = XYZ.Zero - cropCenter;
            return new XYZ(c.X + rel.DotProduct(right) / scale, c.Y + rel.DotProduct(up) / scale, 0);
        }
    }

    // =====================================================================================
    //  Padrões do Escritório
    // =====================================================================================

    /// <summary>
    /// Padrões do Escritório: exports every JA Tools setting of this user (options of each tool,
    /// detail templates, export settings) to one ".jatools" file for the team, and imports one
    /// (the current settings are backed up first). The file can live on the office network.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class PadroesEscritorioCommand : ToolCommand
    {
        public override string Title => "Padrões do Escritório";
        protected override bool PicksInView => true;   // opens file windows: no automatic progress box

        private static string SettingsFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RevitDwgUnifier");

        protected override Result Run()
        {
            var dlg = new OptionsDialog(Title, "PadroesEscritorio", "Continuar");
            var fMode = dlg.Choice("mode", "O que fazer", new[]
            {
                "Exportar os meus padrões para um arquivo (.jatools)",
                "Importar padrões de um arquivo (.jatools)",
                "Abrir a pasta das configurações"
            });
            dlg.Note("Levam: as opções de todas as ferramentas, os modelos do Gerar Detalhes e as configurações do Exportar Pranchas. " +
                     "Na importação, os seus padrões atuais são guardados antes numa cópia de segurança.");
            if (!dlg.Show(Owner)) return Result.Cancelled;
            Directory.CreateDirectory(SettingsFolder);

            if (fMode.Index == 2) { ResultDialog.OpenPath(SettingsFolder); return Result.Succeeded; }
            if (fMode.Index == 0)
            {
                var save = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Exportar padrões do JA (JA Famílias e JA Tools)", Filter = "Padrões JA (*.jatools)|*.jatools",
                    FileName = "Padroes JA " + DateTime.Now.ToString("yyyy-MM-dd") + ".jatools"
                };
                if (save.ShowDialog() != true) return Result.Cancelled;
                ToolPrefs.Save();
                int n = Pack(save.FileName);
                Show($"{n} arquivo(s) de configuração exportado(s) para:\n{save.FileName}\n\nCompartilhe esse arquivo com a equipe; cada pessoa usa \"Importar\".",
                     ResultKind.Success, OpenFolderAction(Path.GetDirectoryName(save.FileName)));
                return Result.Succeeded;
            }

            var open = new Microsoft.Win32.OpenFileDialog { Title = "Importar padrões do JA (JA Famílias e JA Tools)", Filter = "Padrões JA (*.jatools)|*.jatools" };
            if (open.ShowDialog() != true) return Result.Cancelled;
            string backup = Path.Combine(SettingsFolder, "backup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".jatools");
            Pack(backup);
            int imported = Unpack(open.FileName);
            ToolPrefs.Reload();
            try { JAEsquadrias.Core.Settings.Recarregar(); } catch { }
            Show($"{imported} arquivo(s) de configuração importado(s).\nCópia de segurança dos padrões anteriores:\n{backup}", ResultKind.Success);
            return Result.Succeeded;
        }

        private static IEnumerable<string> SettingFiles() =>
            Directory.Exists(SettingsFolder)
                ? Directory.GetFiles(SettingsFolder, "*.json", SearchOption.TopDirectoryOnly)
                : Enumerable.Empty<string>();

        /// <summary>The JA Famílias settings (folders, model families, office defaults) travel in the same file
        /// under this name; they go back to %AppData%\JAEsquadrias\settings.json on import.</summary>
        private const string EntradaFamilias = "jafamilias_settings.json";
        private static string ArquivoFamilias => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JAEsquadrias", "settings.json");

        private static int Pack(string zipPath)
        {
            if (File.Exists(zipPath)) File.Delete(zipPath);
            int n = 0;
            using (var fs = new FileStream(zipPath, FileMode.CreateNew))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (string f in SettingFiles())
                {
                    ZipArchiveEntry entry = zip.CreateEntry(Path.GetFileName(f));
                    using (var w = entry.Open())
                    using (var r = File.OpenRead(f)) r.CopyTo(w);
                    n++;
                }
                if (File.Exists(ArquivoFamilias))
                {
                    ZipArchiveEntry fam = zip.CreateEntry(EntradaFamilias);
                    using (var w = fam.Open())
                    using (var r = File.OpenRead(ArquivoFamilias)) r.CopyTo(w);
                    n++;
                }
                ZipArchiveEntry info = zip.CreateEntry("_info.txt");
                using (var w = new StreamWriter(info.Open(), new UTF8Encoding(false)))
                    w.Write($"JA para Revit (JA Famílias e JA Tools) - padrões exportados em {DateTime.Now:yyyy-MM-dd HH:mm} por {Environment.UserName}");
            }
            return n;
        }

        private static int Unpack(string zipPath)
        {
            int n = 0;
            using (var zip = ZipFile_Open(zipPath))
            {
                foreach (ZipArchiveEntry e in zip.Entries)
                {
                    if (!e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || e.FullName.Contains("/") || e.FullName.Contains("\\")) continue;
                    string target = string.Equals(e.Name, EntradaFamilias, StringComparison.OrdinalIgnoreCase) ? ArquivoFamilias : Path.Combine(SettingsFolder, e.Name);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (var r = e.Open())
                    using (var w = new FileStream(target, FileMode.Create)) r.CopyTo(w);
                    n++;
                }
            }
            return n;
        }

        private static ZipArchive ZipFile_Open(string path) => new ZipArchive(File.OpenRead(path), ZipArchiveMode.Read);
    }
}
