using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDwgUnifier.Tools.Common;

namespace RevitDwgUnifier.Tools.Parameters
{
    /// <summary>
    /// Mudar Nível: puts the selected elements — or the elements of the chosen categories, in the
    /// active view or in the whole project — on another level WITHOUT moving them (the offsets are
    /// corrected by the difference between the levels, the same engine as Editar Níveis › Revincular).
    /// Optionally only the ones on a given level, and also the top constraint.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class MudarNivelCommand : ToolCommand
    {
        public override string Title => "Mudar Nível";

        protected override Result Run()
        {
            List<Level> niveis = RevitUtil.Levels(Doc);
            if (niveis.Count < 2) { Show("O projeto precisa de pelo menos dois níveis.", ResultKind.Info); return Result.Cancelled; }

            // the selection (a selected group gives its members; they are reported, Revit edits them only in Editar grupo)
            var sel = UiDoc.Selection.GetElementIds().Select(id => Doc.GetElement(id)).Where(e => e != null).ToList();
            var selecionados = sel.Where(LevelRehost.TemNivel).ToList();

            ToolProgress.Pulse("Lendo as categorias…");
            List<Element> projeto = new FilteredElementCollector(Doc).WhereElementIsNotElementType().Where(LevelRehost.TemNivel).ToList();
            ToolProgress.Hide();
            if (projeto.Count == 0) { Show("Nenhum elemento com nível editável no projeto.", ResultKind.Info); return Result.Cancelled; }

            // categories: of the selection when there is one, else of the whole project
            var basePraCategorias = selecionados.Count > 0 ? selecionados : projeto;
            var categorias = basePraCategorias.GroupBy(e => e.Category.Name).OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase).ToList();

            var dlg = new OptionsDialog(Title, "MudarNivel", "Mudar nível");
            var escopos = new List<string> { $"Selecionados ({selecionados.Count})", "Vista ativa", "Todo o projeto" };
            var fEscopo = dlg.Choice("escopo", "Onde", escopos, selecionados.Count > 0 ? 0 : 2,
                "Selecionados: só os elementos selecionados antes do comando (das categorias marcadas abaixo).");
            if (selecionados.Count > 0) fEscopo.Index = 0;
            else if (fEscopo.Index == 0) fEscopo.Index = 2;
            var fCat = dlg.Multi("categorias", "Categorias", categorias.Select(g => $"{g.Key} ({g.Count()})").ToList(), true, true,
                selecionados.Count > 0 ? "Categorias da seleção (contagem da seleção)." : "Contagem no projeto todo.");
            dlg.Section("Níveis");
            var nomesNiveis = niveis.Select(l => $"{l.Name}  ({RevitUtil.ToM(l.Elevation):0.00} m)").ToList();
            var fDe = dlg.Choice("de", "Mudar quem está em", new[] { "Qualquer nível" }.Concat(nomesNiveis).ToList(), 0,
                "Qualquer nível: a base de cada elemento vai para o destino. Um nível: só o que aponta para ele (base e topo).");
            var fPara = dlg.Choice("para", "Para o nível", nomesNiveis, 0);
            var fTopo = dlg.Check("topo", "Também a restrição superior (topo de paredes, pilares, escadas)", false,
                "Com “Qualquer nível”, só a base muda; marque para levar também o topo para o nível de destino (a altura fica igual, o deslocamento é corrigido).");
            fTopo.EnabledWhen(fDe, 0);
            dlg.Note("Os elementos não saem do lugar: o deslocamento (da base, do topo, a elevação) é corrigido pela diferença entre os níveis. Elementos em grupos são pulados. Pode ser desfeito com Ctrl+Z.");
            if (!dlg.Show(Owner)) return Result.Cancelled;

            var cats = new HashSet<string>(fCat.SelectedIndices.Select(i => categorias[i].Key));
            Level para = niveis[Math.Max(0, fPara.Index)];
            Level de = fDe.Index > 0 ? niveis[fDe.Index - 1] : null;
            if (de != null && de.Id == para.Id) { Show("O nível de origem e o de destino são o mesmo.", ResultKind.Info); return Result.Cancelled; }

            IEnumerable<Element> fonte;
            switch (fEscopo.Index)
            {
                case 0: fonte = selecionados; break;
                case 1: fonte = new FilteredElementCollector(Doc, Doc.ActiveView.Id).WhereElementIsNotElementType().Where(LevelRehost.TemNivel); break;
                default: fonte = projeto; break;
            }
            var alvo = fonte.Where(e => cats.Contains(e.Category.Name)).ToList();
            if (alvo.Count == 0) { Show("Nenhum elemento nessas categorias " + (fEscopo.Index == 0 ? "na seleção." : fEscopo.Index == 1 ? "na vista ativa." : "no projeto."), ResultKind.Info); return Result.Cancelled; }

            LevelRehost.Outcome o;
            using (var t = new Transaction(Doc, "JA Tools - " + Title))
            {
                t.Start();
                RevitDwgUnifier.Export.FailureSwallower.Attach(t);
                o = LevelRehost.Revincular(Doc, alvo, para, de, de != null || fTopo.Value, $"Mudando para {para.Name}");
                if (t.Commit() != TransactionStatus.Committed)
                {
                    Show("O Revit recusou a mudança de nível: nada foi alterado.", ResultKind.Warning);
                    return Result.Failed;
                }
            }
            ToolProgress.End();

            var sb = new StringBuilder($"{o.Moved} elemento(s) agora no nível {para.Name}, sem sair do lugar.");
            if (o.JaNoNivel > 0) sb.Append($"\n{o.JaNoNivel} já estavam nele.");
            int semMudanca = alvo.Count - o.Moved - o.JaNoNivel - o.Failed - o.EmGrupos;
            if (de != null && semMudanca > 0) sb.Append($"\n{semMudanca} não estavam no nível {de.Name}.");
            if (o.EmGrupos > 0) sb.Append($"\n{o.EmGrupos} em grupos foram pulados (mude dentro de Editar grupo, ou desagrupe).");
            if (o.Failed > 0) sb.Append($"\n{o.Failed} não aceitaram a mudança.");
            if (sel.Any(e => e is FamilyInstance fi && fi.Host != null && !LevelRehost.TemNivel(e)))
                sb.Append("\nPortas, janelas e outras peças hospedadas acompanham o nível da parede (ou do piso) onde estão.");
            if (o.Problems.Count > 0) sb.Append("\n\nAvisos:\n• " + string.Join("\n• ", o.Problems.Take(15)));
            Show(sb.ToString(), o.Failed > 0 || o.Problems.Count > 0 ? ResultKind.Warning : ResultKind.Success);
            return Result.Succeeded;
        }
    }
}
