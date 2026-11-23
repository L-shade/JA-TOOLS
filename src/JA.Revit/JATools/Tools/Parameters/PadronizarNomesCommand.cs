using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDwgUnifier.Tools.Common;

namespace RevitDwgUnifier.Tools.Parameters
{
    /// <summary>
    /// Padronizar Nomes: renames the families and types of the office plan standard to the
    /// technical names of <see cref="PadraoNomes"/> ("JA - Etiqueta - Porta : Código + Soleira",
    /// "JA - Nível - Piso Acabado (P.A./P.O.)", "JA - Cota Linear - Executivo 1,5 mm"...). Only the
    /// names change: every element keeps its type, so the drawings stay the same. Old names are
    /// found without accents and without the "LB" prefix/suffix.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class PadronizarNomesCommand : ToolCommand
    {
        public override string Title => "Padronizar Nomes";

        private sealed class Troca
        {
            public Element Elemento;
            public string Grupo, Antigo, Novo, Escopo;
            public override string ToString() => $"{Grupo}: {Antigo}  →  {Novo}";
        }

        protected override Result Run()
        {
            List<Troca> trocas = Encontrar();
            if (trocas.Count == 0)
            {
                Show("Nada a renomear: o projeto não tem nenhuma das famílias e tipos do padrão com o nome antigo (ou já estão com os nomes novos).", ResultKind.Info);
                return Result.Cancelled;
            }

            var dlg = new OptionsDialog(Title, "PadronizarNomes", "Renomear");
            dlg.Section("Renomear");
            var fLista = dlg.Multi("lista", $"{trocas.Count} nome(s) para padronizar", trocas.Select(t => t.ToString()).ToList(), true);
            dlg.Note("Só o nome muda: cada elemento continua com o mesmo tipo e o desenho fica igual. As ferramentas do JA Tools reconhecem os nomes novos e os antigos.");
            if (!dlg.Show(Owner)) return Result.Cancelled;
            var escolhidas = fLista.SelectedIndices.Select(i => trocas[i]).ToList();
            if (escolhidas.Count == 0) return Result.Cancelled;

            int feitas = 0;
            var problemas = new List<string>();
            using (var t = new Transaction(Doc, "JA Tools - " + Title))
            {
                t.Start();
                foreach (Troca tr in escolhidas)
                {
                    try
                    {
                        if (!tr.Elemento.IsValidObject) continue;
                        if (tr.Elemento is Family fam) fam.Name = tr.Novo;
                        else tr.Elemento.Name = tr.Novo;
                        feitas++;
                    }
                    catch (Exception ex) { problemas.Add($"{tr.Antigo}: {ex.Message}"); }
                }
                t.Commit();
            }
            Show($"{feitas} nome(s) padronizado(s)." + (problemas.Count > 0 ? "\n\nNão renomeados (nome já usado?):\n• " + string.Join("\n• ", problemas.Take(20)) : ""),
                 problemas.Count > 0 ? ResultKind.Warning : ResultKind.Success);
            return Result.Succeeded;
        }

        private List<Troca> Encontrar()
        {
            var lista = new List<Troca>();
            Func<string, IEnumerable<string>, bool> casa = (nome, antigos) => antigos.Any(a => PadraoNomes.Norm(a) == PadraoNomes.Norm(nome));

            var familias = new FilteredElementCollector(Doc).OfClass(typeof(Family)).Cast<Family>().ToList();
            var simbolos = new FilteredElementCollector(Doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().ToList();
            var sistema = new FilteredElementCollector(Doc).OfClass(typeof(DimensionType)).Cast<Element>()
                .Concat(new FilteredElementCollector(Doc).OfClass(typeof(TextNoteType))).ToList();
            var tiposVista = new FilteredElementCollector(Doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().ToList();
            var modelos = new FilteredElementCollector(Doc).OfClass(typeof(View)).Cast<View>().Where(v => v.IsTemplate).ToList();
            var regrasFamilia = PadraoNomes.Regras.Where(r => r.Tipo == PadraoNomes.Tipo.Familia).ToList();

            foreach (PadraoNomes.Regra r in PadraoNomes.Regras)
            {
                switch (r.Tipo)
                {
                    case PadraoNomes.Tipo.Familia:
                        foreach (Family f in familias.Where(f => casa(f.Name, r.Antigos)))
                            Add(lista, f, r.Grupo, f.Name, r.Novo, familias.Select(x => x.Name), "familia");
                        break;
                    case PadraoNomes.Tipo.TipoDeFamilia:
                        PadraoNomes.Regra rf = regrasFamilia.FirstOrDefault(x => x.Novo == r.Familia);
                        var nomesFamilia = rf == null ? new[] { r.Familia } : new[] { rf.Novo }.Concat(rf.Antigos).ToArray();
                        foreach (FamilySymbol s in simbolos.Where(s => casa(s.FamilyName, nomesFamilia) && casa(s.Name, r.Antigos)))
                            Add(lista, s, r.Grupo, s.FamilyName + " : " + s.Name, r.Novo,
                                simbolos.Where(x => x.Family?.Id == s.Family?.Id).Select(x => x.Name), "tipo|" + s.Family?.Id.Value);
                        break;
                    case PadraoNomes.Tipo.TipoDeSistema:
                        foreach (Element e in sistema.Where(e => casa(e.Name, r.Antigos)))
                            Add(lista, e, r.Grupo, e.Name, r.Novo, sistema.Where(x => x.GetType() == e.GetType()).Select(x => x.Name), "sis|" + e.GetType().Name);
                        break;
                    case PadraoNomes.Tipo.TipoDeVista:
                        foreach (ViewFamilyType v in tiposVista.Where(v => casa(v.Name, r.Antigos)))
                            Add(lista, v, r.Grupo, v.Name, r.Novo, tiposVista.Where(x => x.ViewFamily == v.ViewFamily).Select(x => x.Name), "vista|" + v.ViewFamily);
                        break;
                    case PadraoNomes.Tipo.ModeloDeVista:
                        foreach (View v in modelos.Where(v => casa(v.Name, r.Antigos)))
                            Add(lista, v, r.Grupo, v.Name, r.Novo, modelos.Select(x => x.Name), "modelo");
                        break;
                }
            }
            // the same element can match twice (e.g. a type named like its family): keep the first
            return lista.GroupBy(t => t.Elemento.Id).Select(g => g.First()).ToList();
        }

        private static void Add(List<Troca> lista, Element e, string grupo, string antigo, string novo, IEnumerable<string> irmaos, string escopo)
        {
            string atual = e is FamilySymbol fs ? fs.Name : e.Name;
            if (string.Equals(atual, novo, StringComparison.Ordinal)) return;
            // two old types that become the same new name: number the second
            var usados = new HashSet<string>(irmaos, StringComparer.OrdinalIgnoreCase);
            usados.Remove(atual);
            usados.UnionWith(lista.Where(t => t.Escopo == escopo).Select(t => t.Novo));
            string nome = novo;
            for (int i = 2; usados.Contains(nome) && i < 50; i++) nome = novo + " (" + i + ")";
            lista.Add(new Troca { Elemento = e, Grupo = grupo, Antigo = antigo, Novo = nome, Escopo = escopo });
        }
    }
}
