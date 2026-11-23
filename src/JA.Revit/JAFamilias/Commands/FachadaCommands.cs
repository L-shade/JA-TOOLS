using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using JAEsquadrias.Core;
using JAEsquadrias.Core.Paineis;
using JAEsquadrias.Engine;

namespace JAEsquadrias.Commands
{
    /// <summary>
    /// Fachada Paginada: an ACM / brise skin in front of the selected walls — a curtain wall on the
    /// exterior face (with the chosen gap), divided into modules (width × height, from the start or
    /// centred), and then the Painel de Cortina window opens on its panels to make them ACM cassete,
    /// brise, veneziana... in every size the paging produced.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class FachadaPaginadaCommand : EsquadriaCommandBase
    {
        public override string Title => "Fachada Paginada";


        protected override Result Run()
        {
            var paredes = UiDoc.Selection.GetElementIds().Select(id => Doc.GetElement(id)).OfType<Wall>().Where(w => w.WallType.Kind == WallKind.Basic).ToList();
            if (paredes.Count == 0)
            {
                try
                {
                    paredes = UiDoc.Selection.PickObjects(ObjectType.Element, new FiltroDeParede(w => w.WallType.Kind == WallKind.Basic), "Selecione as paredes que recebem o revestimento e clique em Concluir")
                        .Select(r => Doc.GetElement(r)).OfType<Wall>().ToList();
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
            }
            if (paredes.Count == 0) return Result.Cancelled;

            var tiposCortina = new FilteredElementCollector(Doc).OfClass(typeof(WallType)).Cast<WallType>().Where(t => t.Kind == WallKind.Curtain).ToList();
            if (tiposCortina.Count == 0) { Show("O projeto não tem nenhum tipo de parede cortina para copiar.", ResultKind.Warning); return Result.Cancelled; }

            var dlg = new OptionsDialog(Title, "FachadaPaginada", "Paginar");
            var fTipo = dlg.Choice("tipo", "Revestimento", new[] { "ACM (cassete com junta)", "Brise vertical (aletas em pé)", "Brise horizontal / veneziana", "Painel sólido liso" }, 0,
                "Só o ponto de partida: na janela seguinte (Painel de Cortina) dá para ajustar junta, aletas, material e cor.");
            dlg.Section("Paginação");
            var fLarg = dlg.Number("largura", "Largura do módulo (mm)", 1250, 100, 6000, "Chapa de ACM: 1250 ou 1500 mm de largura usual.");
            var fAlt = dlg.Number("altura", "Altura do módulo (mm)", 1000, 100, 8000, "0 ou maior que a parede = uma faixa só na altura.");
            var fAlinhar = dlg.Choice("alinhar", "Sobras", new[] { "Módulos centralizados (sobras iguais nas pontas)", "A partir do início da parede", "A partir do fim da parede" }, 0);
            var fBase = dlg.Choice("base", "Na altura", new[] { "Módulos a partir da base", "Módulos centralizados na altura" }, 0);
            dlg.Section("Posição");
            var fAfast = dlg.Number("afastamento", "Afastamento da face externa (mm)", 50, 0, 1000, "Espaço da subestrutura entre a parede acabada e o fundo do painel.");
            var fEsp = dlg.Number("espessura", "Espessura do painel (mm)", 30, 2, 500, "Usada só para posicionar a parede cortina (o centro dela fica no meio do painel).");
            if (!dlg.Show(Owner)) return Result.Cancelled;

            double mw = fLarg.Value * MmFt, mh = fAlt.Value * MmFt;
            var avisos = new List<string>();
            var novas = new List<Wall>();
            using (var t = new Transaction(Doc, "JA Famílias – " + Title))
            {
                t.Start();
                var opts = t.GetFailureHandlingOptions();
                opts.SetFailuresPreprocessor(new WarningSwallower());
                t.SetFailureHandlingOptions(opts);
                WallType tipo = TipoSemGrade(tiposCortina, avisos);
                foreach (Wall w in paredes)
                {
                    try
                    {
                        Wall nova = Revestir(w, tipo, (fAfast.Value + fEsp.Value / 2) * MmFt, avisos);
                        if (nova != null) novas.Add(nova);
                    }
                    catch (Exception ex) { avisos.Add($"Parede {w.Id.Value}: {ex.Message}"); }
                }
                Doc.Regenerate();
                foreach (Wall nova in novas)
                {
                    try { Paginar(nova, mw, mh, fAlinhar.Index, fBase.Index == 1, avisos); }
                    catch (Exception ex) { avisos.Add($"Paginação da parede {nova.Id.Value}: {ex.Message}"); }
                }
                t.Commit();
            }
            if (novas.Count == 0)
            {
                Show("Nenhuma parede revestida." + (avisos.Count > 0 ? "\n\n• " + string.Join("\n• ", avisos.Distinct().Take(15)) : ""), ResultKind.Warning);
                return Result.Failed;
            }

            // the panels: straight into the Painel de Cortina window, starting from the chosen kind
            var paineis = novas.SelectMany(w => w.CurtainGrid?.GetPanelIds() ?? new List<ElementId>()).Select(id => Doc.GetElement(id)).Where(e => e != null).ToList();
            PainelConfig inicio = PainelConfig.FromJson(ToolPrefs.Get("ultimo.painel")) ?? new PainelConfig();
            switch (fTipo.Index)
            {
                case 0: inicio.Tipo = TipoPainel.Cassete; break;
                case 1: inicio.Tipo = TipoPainel.Brise; break;
                case 2: inicio.Tipo = TipoPainel.Veneziana; break;
                default: inicio.Tipo = TipoPainel.Solido; break;
            }
            inicio.EspessuraMm = fEsp.Value;
            ToolPrefs.Set("ultimo.painel", inicio.ToJson());
            ToolPrefs.Save();
            if (avisos.Count > 0)
                TaskDialog.Show(Title, $"{novas.Count} parede(s) cortina criada(s) com {paineis.Count} painéis.\n\nAvisos:\n• " + string.Join("\n• ", avisos.Distinct().Take(15)) +
                                "\n\nA seguir: o tipo dos painéis (Painel de Cortina).");
            return AbrirPainel(paineis);
        }

        /// <summary>A curtain type with no automatic grids or mullions (copied as "JA Fachada paginada").</summary>
        private WallType TipoSemGrade(List<WallType> tipos, List<string> avisos)
        {
            const string nome = "JA Fachada paginada";
            WallType ja = tipos.FirstOrDefault(t => t.Name == nome);
            if (ja != null) return ja;
            int Lay(WallType t, BuiltInParameter b) { Parameter p = t.get_Parameter(b); return p != null && p.StorageType == StorageType.Integer ? p.AsInteger() : -1; }
            WallType baseT = tipos.FirstOrDefault(t => Lay(t, BuiltInParameter.SPACING_LAYOUT_VERT) == 0 && Lay(t, BuiltInParameter.SPACING_LAYOUT_HORIZ) == 0) ?? tipos[0];
            WallType novo = baseT.Duplicate(nome) as WallType ?? baseT;
            void Zero(BuiltInParameter b) { try { Parameter p = novo.get_Parameter(b); if (p != null && !p.IsReadOnly && p.StorageType == StorageType.Integer) p.Set(0); } catch { } }
            void Nenhum(BuiltInParameter b) { try { Parameter p = novo.get_Parameter(b); if (p != null && !p.IsReadOnly && p.StorageType == StorageType.ElementId) p.Set(ElementId.InvalidElementId); } catch { } }
            Zero(BuiltInParameter.SPACING_LAYOUT_VERT);
            Zero(BuiltInParameter.SPACING_LAYOUT_HORIZ);
            Nenhum(BuiltInParameter.AUTO_MULLION_INTERIOR_VERT);
            Nenhum(BuiltInParameter.AUTO_MULLION_INTERIOR_HORIZ);
            Nenhum(BuiltInParameter.AUTO_MULLION_BORDER1_VERT);
            Nenhum(BuiltInParameter.AUTO_MULLION_BORDER2_VERT);
            Nenhum(BuiltInParameter.AUTO_MULLION_BORDER1_HORIZ);
            Nenhum(BuiltInParameter.AUTO_MULLION_BORDER2_HORIZ);
            if (Lay(novo, BuiltInParameter.SPACING_LAYOUT_VERT) > 0 || Lay(novo, BuiltInParameter.SPACING_LAYOUT_HORIZ) > 0)
                avisos.Add("O tipo de parede cortina copiado tem grade automática: confira o tipo “" + nome + "”.");
            return novo;
        }

        /// <summary>The curtain wall on the exterior face of w, as long and as high as w.</summary>
        private Wall Revestir(Wall w, WallType tipo, double alemDaFace, List<string> avisos)
        {
            if (!(w.Location is LocationCurve lc) || !(lc.Curve is Line linha))
            {
                avisos.Add($"Parede {w.Id.Value}: só paredes retas.");
                return null;
            }
            double meio = 0;
            try { meio = CortesEsquadriasCommand.WallOffset(w); } catch { }
            XYZ desl = w.Orientation.Normalize() * (meio + w.Width / 2 + alemDaFace);
            Line nova = Line.CreateBound(linha.GetEndPoint(0) + desl, linha.GetEndPoint(1) + desl);
            BoundingBoxXYZ bb = w.get_BoundingBox(null);
            var nivel = Doc.GetElement(w.LevelId) as Level;
            if (bb == null || nivel == null) { avisos.Add($"Parede {w.Id.Value}: sem nível ou geometria."); return null; }
            double altura = bb.Max.Z - bb.Min.Z, baseOff = bb.Min.Z - nivel.ProjectElevation;
            Wall c = Wall.Create(Doc, nova, tipo.Id, nivel.Id, altura, baseOff, false, false);
            // the paging must not change rooms nor join the walls behind it
            try { c.get_Parameter(BuiltInParameter.WALL_ATTR_ROOM_BOUNDING)?.Set(0); } catch { }
            try { WallUtils.DisallowWallJoinAtEnd(c, 0); WallUtils.DisallowWallJoinAtEnd(c, 1); } catch { }
            // exterior of the skin on the same side as the wall's
            try { if (c.Orientation.DotProduct(w.Orientation) < 0) c.Flip(); } catch { }
            return c;
        }

        /// <summary>Vertical lines every mw along the wall, horizontal lines every mh up it.</summary>
        private void Paginar(Wall c, double mw, double mh, int alinhar, bool centroAltura, List<string> avisos)
        {
            CurtainGrid g = c.CurtainGrid;
            if (g == null) { avisos.Add($"Parede {c.Id.Value}: sem grade de cortina."); return; }
            var curva = (Line)((LocationCurve)c.Location).Curve;
            BoundingBoxXYZ bb = c.get_BoundingBox(null);
            double L = curva.Length, z0 = bb.Min.Z, z1 = bb.Max.Z, H = z1 - z0;
            XYZ dir = curva.Direction.Normalize(), a = curva.GetEndPoint(0);
            double zm = (z0 + z1) / 2;

            foreach (double s in Posicoes(L, mw, alinhar))
                Linha(g, false, new XYZ(a.X + dir.X * s, a.Y + dir.Y * s, zm), avisos);
            if (mh > 0 && mh < H - 10 * MmFt)
            {
                XYZ meioL = a + dir * (L / 2);
                foreach (double s in Posicoes(H, mh, centroAltura ? 0 : 1))
                    Linha(g, true, new XYZ(meioL.X, meioL.Y, z0 + s), avisos);
            }
        }

        /// <summary>Division points along a length: centred, from the start or from the end.</summary>
        private static IEnumerable<double> Posicoes(double L, double m, int alinhar)
        {
            const double min = 30 / 304.8;                 // no sliver under 30 mm at the ends
            int n = (int)Math.Floor(L / m + 1e-6);
            double resto = L - n * m;
            double ini = alinhar == 0 ? resto / 2 : alinhar == 1 ? 0 : resto;
            var r = new List<double>();
            for (int i = 0; i <= n; i++)
            {
                double s = ini + i * m;
                if (s > min && s < L - min) r.Add(s);
            }
            return r;
        }

        /// <summary>
        /// Adds a grid line and checks it came out the way asked (Revit names them U/V by the wall's
        /// own axes): a horizontal line asked for and a vertical one made is undone and redone.
        /// </summary>
        private void Linha(CurtainGrid g, bool horizontal, XYZ p, List<string> avisos)
        {
            foreach (bool u in new[] { horizontal, !horizontal })
            {
                CurtainGridLine gl = null;
                try { gl = g.AddGridLine(u, p, false); } catch { }
                if (gl == null) continue;
                XYZ d = (gl.FullCurve as Line)?.Direction;
                bool saiuHorizontal = d != null && Math.Abs(d.Z) < 0.5;
                if (d == null || saiuHorizontal == horizontal) return;
                try { Doc.Delete(gl.Id); } catch { return; }
            }
            if (avisos.Count < 30) avisos.Add("Uma linha de grade não pôde ser criada (módulo menor que o permitido?).");
        }
    }
}
