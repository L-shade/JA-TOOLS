using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using JAEsquadrias.Core;
using JAEsquadrias.Engine;
using JAEsquadrias.UI;
using Settings = JAEsquadrias.Core.Settings;
using Transform = Autodesk.Revit.DB.Transform;
using View = Autodesk.Revit.DB.View;
using RenderOptions = JAEsquadrias.UI.RenderOptions;

namespace JAEsquadrias.Commands
{
    /// <summary>
    /// Elevation of each JA esquadria type as its Revit "type image" (Imagem do tipo): drawn with
    /// the configurator's own renderer into a PNG, imported as an image type and set on the type, so
    /// the Quadro de Esquadrias can show it in an "Elevação" column (Revit shows schedule images on sheets).
    /// </summary>
    internal static class ImagemDoTipo
    {
        public static void Png(EsquadriaConfig cfg, string path)
        {
            double w = 520, h = Math.Max(260, Math.Min(900, w * cfg.AlturaMm / Math.Max(1, cfg.LarguraMm)));
            var canvas = new Canvas { Width = w, Height = h, Background = Brushes.White };
            ElevationRenderer.Draw(canvas, cfg, Layout.Build(cfg), new RenderOptions { Dimensions = true, Wall = false, Caption = false }, w, h);
            canvas.Measure(new Size(w, h));
            canvas.Arrange(new Rect(0, 0, w, h));
            canvas.UpdateLayout();
            var bmp = new RenderTargetBitmap((int)(w * 2), (int)(h * 2), 192, 192, PixelFormats.Pbgra32);
            bmp.Render(canvas);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(path)) enc.Save(fs);
        }

        /// <summary>Inside an open transaction. Returns how many types got their image.</summary>
        public static int Aplicar(Document doc, IEnumerable<EsquadriaNoProjeto> itens, string pasta, List<string> avisos)
        {
            Directory.CreateDirectory(pasta);
            var existentes = new FilteredElementCollector(doc).OfClass(typeof(ImageType)).Cast<ImageType>().ToList();
            int n = 0;
            foreach (var e in itens)
            {
                try
                {
                    string arquivo = "JA Elevação " + FamilyGenerator.SafeName(e.Codigo) + ".png";
                    string path = Path.Combine(pasta, arquivo);
                    Png(e.Config, path);
                    var opts = new ImageTypeOptions(path, false, ImageTypeSource.Import);
                    ImageType it = existentes.FirstOrDefault(x => string.Equals(x.Name, arquivo, StringComparison.OrdinalIgnoreCase) ||
                                                                  string.Equals(x.Name, Path.GetFileNameWithoutExtension(arquivo), StringComparison.OrdinalIgnoreCase));
                    if (it != null) it.ReloadFrom(opts);
                    else { it = ImageType.Create(doc, opts); existentes.Add(it); }
                    Parameter p = e.Symbol.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_IMAGE);
                    if (p == null || p.IsReadOnly) { avisos.Add(e.Codigo + ": o tipo não aceita imagem."); continue; }
                    p.Set(it.Id);
                    n++;
                }
                catch (Exception ex) { avisos.Add(e.Codigo + " (elevação): " + ex.Message); }
            }
            return n;
        }
    }

    /// <summary>
    /// Cortes das Esquadrias: for each chosen JA type, one placed copy gets a vertical section across
    /// the wall through its middle and a horizontal one (a callout of the level's floor plan), cropped
    /// around it, at a detail scale, ready to drag to a sheet.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class CortesEsquadriasCommand : ToolCommand
    {
        public override string Title => "Cortes das Esquadrias";
        private const double Mm = 1 / 304.8;

        protected override Result Run()
        {
            var tipos = ProjectEsquadrias.All(Doc).Where(x => x.Instancias > 0 && x.Config != null)
                .OrderBy(x => x.Config.IsPorta).ThenBy(x => x.Codigo, StringComparer.OrdinalIgnoreCase).ToList();
            if (tipos.Count == 0)
            {
                Show("Nenhuma esquadria do JA Famílias colocada neste projeto.", ResultKind.Info);
                return Result.Cancelled;
            }
            var selecionados = new HashSet<ElementId>(UiDoc.Selection.GetElementIds()
                .Select(id => Doc.GetElement(id)).OfType<FamilyInstance>().Select(f => f.Symbol.Id));
            var dlg = new OptionsDialog(Title, "CortesEsquadrias", "Criar cortes");
            var fTipos = dlg.Multi("tipos", "Tipos", tipos.Select(t => t.Codigo + " — " + Layout.Mm(t.Config.LarguraMm) + " × " + Layout.Mm(t.Config.AlturaMm) + " (" + t.Instancias + " un.)").ToList(), selecionados.Count == 0);
            if (selecionados.Count > 0) for (int i = 0; i < tipos.Count; i++) fTipos.Checked[i] = selecionados.Contains(tipos[i].Symbol.Id);
            dlg.Section("Vistas");
            var fVert = dlg.Check("vertical", "Corte vertical (atravessando a parede, no meio da esquadria)", true);
            var fHoriz = dlg.Check("horizontal", "Corte horizontal (chamada da planta do pavimento)", true,
                "Usa a altura de corte da planta do nível da esquadria (1,20 m usual): confira se ela passa pela janela.");
            var fEscala = dlg.Choice("escala", "Escala", new[] { "1:5", "1:10", "1:20", "1:25" }, 1);
            var fFolga = dlg.Number("folga", "Folga em volta (mm)", 300, 50, 2000, "Quanto da parede aparece além da esquadria.");
            if (!dlg.Show(Owner)) return Result.Cancelled;
            var escolhidos = fTipos.SelectedIndices.Select(i => tipos[i]).ToList();
            if (escolhidos.Count == 0 || (!fVert.Value && !fHoriz.Value)) return Result.Cancelled;
            int escala = new[] { 5, 10, 20, 25 }[Math.Max(0, fEscala.Index)];
            double folga = fFolga.Value * Mm;

            ViewFamilyType tipoCorte = new FilteredElementCollector(Doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(v => v.ViewFamily == ViewFamily.Section);
            var plantas = new FilteredElementCollector(Doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan && v.GenLevel != null).ToList();
            var nomes = new HashSet<string>(new FilteredElementCollector(Doc).OfClass(typeof(View)).Cast<View>().Select(v => v.Name), StringComparer.OrdinalIgnoreCase);
            string Unico(string nome) { string n = nome; int k = 2; while (nomes.Contains(n)) n = nome + " (" + k++ + ")"; nomes.Add(n); return n; }

            var criadas = new List<View>();
            var avisos = new List<string>();
            using (var t = new Transaction(Doc, "JA Famílias – " + Title))
            {
                t.Start();
                foreach (var tipo in escolhidos)
                {
                    FamilyInstance fi = new FilteredElementCollector(Doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                        .Where(i => i.Symbol != null && i.Symbol.Id == tipo.Symbol.Id)
                        .OrderByDescending(i => selecionados.Count > 0 && UiDoc.Selection.GetElementIds().Contains(i.Id)).FirstOrDefault();
                    if (fi == null) continue;
                    BoundingBoxXYZ bb = fi.get_BoundingBox(null);
                    if (bb == null) { avisos.Add(tipo.Codigo + ": sem geometria."); continue; }
                    XYZ c = (bb.Min + bb.Max) / 2;
                    XYZ frente = new XYZ(fi.FacingOrientation.X, fi.FacingOrientation.Y, 0);
                    if (frente.GetLength() < 1e-6) { avisos.Add(tipo.Codigo + ": orientação da esquadria não encontrada."); continue; }
                    frente = frente.Normalize();
                    XYZ lado = XYZ.BasisZ.CrossProduct(frente).Normalize();
                    double meiaLarg = tipo.Config.LarguraMm / 2 * Mm;
                    double meiaParede = Math.Max(((fi.Host as Wall)?.Width ?? 0.5) / 2, 100 * Mm);
                    // the wall's middle: through the host's centre line when there is one
                    if (fi.Host is Wall w && w.Location is LocationCurve lc)
                    {
                        XYZ p = lc.Curve.Project(new XYZ(c.X, c.Y, lc.Curve.GetEndPoint(0).Z))?.XYZPoint;
                        if (p != null)
                        {
                            double off = 0;
                            try { off = WallOffset(w); } catch { }
                            XYZ meio = p + w.Orientation.Normalize() * off;
                            c = new XYZ(meio.X, meio.Y, c.Z);
                        }
                    }

                    if (fVert.Value && tipoCorte != null)
                    {
                        try
                        {
                            var tr = Transform.Identity;
                            tr.Origin = c;
                            tr.BasisX = frente;
                            tr.BasisY = XYZ.BasisZ;
                            tr.BasisZ = frente.CrossProduct(XYZ.BasisZ);
                            var box = new BoundingBoxXYZ
                            {
                                Transform = tr,
                                Min = new XYZ(-meiaParede - folga, bb.Min.Z - c.Z - folga, -(meiaLarg + 50 * Mm)),
                                Max = new XYZ(meiaParede + folga, bb.Max.Z - c.Z + folga, 0)
                            };
                            ViewSection vs = ViewSection.CreateSection(Doc, tipoCorte.Id, box);
                            vs.Scale = escala;
                            vs.DetailLevel = ViewDetailLevel.Fine;
                            try { vs.Name = Unico("JA Corte vertical " + tipo.Codigo); } catch { }
                            criadas.Add(vs);
                        }
                        catch (Exception ex) { avisos.Add(tipo.Codigo + " (corte vertical): " + ex.Message); }
                    }
                    else if (fVert.Value) avisos.Add("O projeto não tem tipo de vista de corte: corte vertical não criado.");

                    if (fHoriz.Value)
                    {
                        ViewPlan mae = plantas.FirstOrDefault(v => v.GenLevel.Id == fi.LevelId) ?? plantas.FirstOrDefault(v => Doc.GetElement(fi.LevelId) is Level lv && Math.Abs(v.GenLevel.ProjectElevation - lv.ProjectElevation) < 0.01);
                        if (mae == null) { avisos.Add(tipo.Codigo + ": nenhuma planta baixa do nível da esquadria (corte horizontal)."); continue; }
                        try
                        {
                            var cantos = new[] { -1, 1 }.SelectMany(a => new[] { -1, 1 }.Select(b => c + lado * (a * (meiaLarg + folga)) + frente * (b * (meiaParede + folga)))).ToList();
                            var p1 = new XYZ(cantos.Min(q => q.X), cantos.Min(q => q.Y), mae.GenLevel.ProjectElevation);
                            var p2 = new XYZ(cantos.Max(q => q.X), cantos.Max(q => q.Y), mae.GenLevel.ProjectElevation);
                            View ch = ViewSection.CreateCallout(Doc, mae.Id, mae.GetTypeId(), p1, p2);
                            ch.Scale = escala;
                            ch.DetailLevel = ViewDetailLevel.Fine;
                            try { ch.Name = Unico("JA Corte horizontal " + tipo.Codigo); } catch { }
                            criadas.Add(ch);
                        }
                        catch (Exception ex) { avisos.Add(tipo.Codigo + " (corte horizontal): " + ex.Message); }
                    }
                }
                t.Commit();
            }
            if (criadas.Count > 0) TryOpen(criadas[0]);
            var sb = new StringBuilder(criadas.Count + " vista(s) criada(s) na escala 1:" + escala + ":\n");
            foreach (View v in criadas.Take(30)) sb.AppendLine("• " + v.Name);
            sb.AppendLine("\nArraste as vistas para a prancha. As vistas horizontais são chamadas da planta: a altura de corte é a dela.");
            if (avisos.Count > 0) sb.AppendLine("\nAvisos:\n• " + string.Join("\n• ", avisos.Distinct().Take(20)));
            Show(sb.ToString().TrimEnd(), avisos.Count > 0 ? ResultKind.Warning : ResultKind.Success);
            return Result.Succeeded;
        }

        /// <summary>Distance from the wall's location line to its centre, along Orientation (feet).</summary>
        internal static double WallOffset(Wall w)
        {
            Parameter p = w.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM);
            if (p == null) return 0;
            double h = w.Width / 2;
            double ext = 0, nucleo = 0;
            try
            {
                CompoundStructure cs = w.WallType.GetCompoundStructure();
                if (cs != null)
                {
                    int f = cs.GetFirstCoreLayerIndex(), l = cs.GetLastCoreLayerIndex();
                    for (int i = 0; i < cs.LayerCount; i++)
                    {
                        double e = cs.GetLayerWidth(i);
                        if (i < f) ext += e; else if (i <= l) nucleo += e;
                    }
                }
            }
            catch { }
            // position along Orientation (exterior side positive), the wall's centre at 0:
            // the location line sits at "linha"; the centre is -linha away from it
            double linha;
            switch ((WallLocationLine)p.AsInteger())
            {
                case WallLocationLine.FinishFaceExterior: linha = h; break;
                case WallLocationLine.FinishFaceInterior: linha = -h; break;
                case WallLocationLine.CoreExterior: linha = h - ext; break;
                case WallLocationLine.CoreInterior: linha = h - ext - nucleo; break;
                case WallLocationLine.CoreCenterline: linha = nucleo > 0 ? h - ext - nucleo / 2 : 0; break;
                default: linha = 0; break;
            }
            return -linha;
        }
    }
}
