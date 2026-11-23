using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using JAEsquadrias.Core.Perfis;
using JAEsquadrias.Engine;
using JAEsquadrias.UI;
using Settings = JAEsquadrias.Core.Settings;

namespace JAEsquadrias.Commands
{
    /// <summary>
    /// Shared by Novo Perfil and Trocar Montantes: reading the mullions picked by the user (in
    /// plan, section, elevation or 3D — or all mullions of the curtain walls picked), creating a
    /// profile with the configurator and swapping mullion types.
    /// </summary>
    public abstract class PerfilCommandBase : ToolCommand
    {
        protected override bool PicksInView => true;

        private sealed class MontanteOuParede : ISelectionFilter
        {
            public bool AllowElement(Element e) => e is Mullion || (e is Wall w && w.CurtainGrid != null);
            public bool AllowReference(Reference r, XYZ p) => false;
        }

        /// <summary>Mullions among the elements selected in Revit (curtain walls give all their mullions).</summary>
        protected List<Mullion> Selecionados()
        {
            var ids = new HashSet<ElementId>();
            var list = new List<Mullion>();
            foreach (ElementId id in UiDoc.Selection.GetElementIds()) Add(Doc.GetElement(id), ids, list);
            return list;
        }

        protected List<Mullion> Escolher()
        {
            var list = Selecionados();
            if (list.Count > 0) return list;
            var ids = new HashSet<ElementId>();
            IList<Reference> refs = UiDoc.Selection.PickObjects(ObjectType.Element, new MontanteOuParede(),
                "Selecione montantes (ou paredes cortina inteiras) na planta, corte ou 3D e clique em Concluir");
            foreach (Reference r in refs) Add(Doc.GetElement(r), ids, list);
            return list;
        }

        private void Add(Element e, HashSet<ElementId> ids, List<Mullion> list)
        {
            if (e is Mullion m) { if (ids.Add(m.Id)) list.Add(m); }
            else if (e is Wall w && w.CurtainGrid != null)
                foreach (ElementId id in w.CurtainGrid.GetMullionIds())
                    if (Doc.GetElement(id) is Mullion mm && ids.Add(mm.Id)) list.Add(mm);
        }

        protected static bool Vertical(Mullion m)
        {
            try
            {
                Curve c = m.LocationCurve;
                if (c != null) { XYZ d = (c.GetEndPoint(1) - c.GetEndPoint(0)).Normalize(); return Math.Abs(d.Z) > 0.7; }
            }
            catch { }
            return true;
        }

        protected Dictionary<ElementId, int> Usos() =>
            new FilteredElementCollector(Doc).OfCategory(BuiltInCategory.OST_CurtainWallMullions).WhereElementIsNotElementType()
                .GroupBy(e => e.GetTypeId()).ToDictionary(g => g.Key, g => g.Count());

        /// <summary>Swaps the type (unpinning when needed). Returns how many changed.</summary>
        protected int Trocar(IEnumerable<Mullion> ms, MullionType tipo, List<string> problems, out int iguais)
        {
            int n = 0, falhas = 0, soltos = 0;
            iguais = 0;
            using (var t = new Transaction(Doc, "JA Famílias – trocar montantes"))
            {
                t.Start();
                var opts = t.GetFailureHandlingOptions();
                opts.SetFailuresPreprocessor(new WarningSwallower());
                t.SetFailureHandlingOptions(opts);
                foreach (Mullion m in ms)
                {
                    if (!m.IsValidObject) continue;
                    if (m.GetTypeId() == tipo.Id) { iguais++; continue; }
                    try
                    {
                        if (m.Pinned) { m.Pinned = false; soltos++; }
                        m.ChangeTypeId(tipo.Id);
                        n++;
                    }
                    catch (Exception ex) { falhas++; Logger.Warn("[JA Famílias] Montante " + m.Id.Value + ": " + ex.Message); }
                }
                t.Commit();
            }
            if (soltos > 0) problems.Add($"{soltos} montante(s) estavam fixados (pino) e foram liberados para a troca.");
            if (falhas > 0) problems.Add($"{falhas} montante(s) não aceitaram o tipo (ex.: tipo de canto fora de um canto).");
            return n;
        }

        /// <summary>Configurator → profile family + mullion type (→ applied to the mullions given).</summary>
        protected Result CriarPerfil(List<Mullion> aplicarEm)
        {
            var settings = Settings.Current;
            var types = PerfilGenerator.Types(Doc);
            var usos = Usos();
            var existentes = new List<PerfilExistente>();
            foreach (MullionType t in types)
            {
                PerfilConfig c = PerfilGenerator.ConfigOf(Doc, t);
                if (c != null) existentes.Add(new PerfilExistente { Nome = t.Name, Config = c, Usos = usos.TryGetValue(t.Id, out int u) ? u : 0, Tipo = t });
            }
            var used = new HashSet<string>(existentes.Select(e => e.Config.Codigo), StringComparer.OrdinalIgnoreCase);

            PerfilConfig start = PerfilConfig.FromJson(ToolPrefs.Get("ultimo.perfil")) ?? new PerfilConfig();
            if (used.Contains(start.Codigo)) start.Codigo = NextCode(used, start.Codigo);
            var materiais = new FilteredElementCollector(Doc).OfClass(typeof(Material)).Cast<Material>().Select(m => m.Name).Distinct().ToList();

            PerfilPedido p = PerfilWindow.Abrir(Owner, start, existentes, materiais, aplicarEm?.Count ?? 0);
            if (p == null) return Result.Cancelled;
            PerfilConfig cfg = p.Config;
            bool editing = p.Editando != null;
            string renamed = null;
            if (!editing && used.Contains(cfg.Codigo))
            {
                string old = cfg.Codigo;
                cfg.Codigo = NextCode(used, cfg.Codigo);
                renamed = $"O código {old} já existe no projeto: o perfil novo ficou como {cfg.Codigo}.";
            }
            ToolPrefs.Set("ultimo.perfil", cfg.ToJson());
            ToolPrefs.Save();

            PerfilResult res = PerfilGenerator.Generate(UiApp.Application, Doc, cfg, settings, p.Editando?.Tipo as MullionType);
            if (cfg.EhCanto && res.Tipo != null) { ToolPrefs.Set("perfil.canto." + res.Tipo.Name, cfg.ToJson()); ToolPrefs.Save(); }

            var warnings = new List<string>(res.Warnings);
            if (renamed != null) warnings.Insert(0, renamed);
            int trocados = 0, iguais = 0;
            if (p.Aplicar && aplicarEm != null && aplicarEm.Count > 0 && res.Tipo != null)
                trocados = Trocar(aplicarEm, res.Tipo, warnings, out iguais);

            var sb = new StringBuilder();
            sb.AppendLine(editing ? $"Perfil {res.Tipo?.Name} atualizado: todos os montantes desse tipo acompanham."
                                  : $"Tipo de montante {res.Tipo?.Name} criado.");
            sb.AppendLine(cfg.Descricao());
            if (trocados > 0) sb.AppendLine($"Aplicado em {trocados} montante(s)" + (iguais > 0 ? $" ({iguais} já eram desse tipo)." : "."));
            else if (!p.Aplicar && !editing)
            {
                sb.AppendLine();
                sb.AppendLine("Para usar: selecione montantes (planta, corte ou 3D) e clique em Trocar Montantes — ou escolha o tipo nas propriedades da parede cortina.");
            }
            if (res.RfaPath != null)
            {
                sb.AppendLine();
                sb.AppendLine("Arquivo do perfil:");
                sb.AppendLine(res.RfaPath);
            }
            if (warnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Avisos:");
                foreach (string w in warnings.Distinct()) sb.AppendLine("• " + w);
            }
            Show(sb.ToString(), warnings.Count > 0 ? ResultKind.Warning : ResultKind.Success,
                 res.RfaPath != null ? new[] { OpenFolderAction(res.RfaPath) } : new ResultAction[0]);
            return Result.Succeeded;
        }

        /// <summary>"MT01" → "MT02" (first free).</summary>
        protected static string NextCode(HashSet<string> used, string code)
        {
            string prefix = new string((code ?? "MT").TakeWhile(ch => !char.IsDigit(ch)).ToArray());
            if (prefix.Length == 0) prefix = "MT";
            for (int i = 1; i < 1000; i++)
            {
                string c = prefix + i.ToString("00");
                if (!used.Contains(c)) return c;
            }
            return prefix + Guid.NewGuid().ToString("N").Substring(0, 4);
        }
    }

    /// <summary>Novo Perfil: curtain-wall mullion profile (straight, chamfered, tubes, I, T, U, L,
    /// pressure cap) or corner mullion (meia esquadria), applied to the selected mullions if any.</summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class NovoPerfilCommand : PerfilCommandBase
    {
        public override string Title => "Novo Perfil";
        protected override Result Run() => CriarPerfil(Selecionados());
    }

    /// <summary>Trocar Montantes: swaps the type of the mullions picked in any view.</summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class TrocarMontantesCommand : PerfilCommandBase
    {
        public override string Title => "Trocar Montantes";

        protected override Result Run()
        {
            List<Mullion> picked = Escolher();
            if (picked.Count == 0) { Show("Nenhum montante escolhido."); return Result.Cancelled; }

            var usos = Usos();
            var items = PerfilGenerator.Types(Doc).Select(t => new TipoMontanteItem
            {
                Familia = t.FamilyName,
                Nome = t.Name,
                Ja = PerfilGenerator.ConfigOf(Doc, t) != null,
                Canto = PerfilGenerator.IsCorner(t, true) || PerfilGenerator.IsCorner(t, false),
                Usos = usos.TryGetValue(t.Id, out int u) ? u : 0,
                Tipo = t
            }).OrderByDescending(i => i.Ja).ThenBy(i => i.Familia).ThenBy(i => i.Nome).ToList();
            int vert = picked.Count(Vertical);
            string atuais = string.Join(", ", picked.GroupBy(m => m.GetTypeId()).Select(g => (Doc.GetElement(g.Key) as ElementType)?.Name + " (" + g.Count() + ")").Take(4)) +
                            (picked.Select(m => m.GetTypeId()).Distinct().Count() > 4 ? "…" : "");

            TrocaPedido p = TrocarMontantesWindow.Abrir(Owner, items, picked.Count, vert, atuais);
            if (p == null) return Result.Cancelled;
            if (p.NovoPerfil) return CriarPerfil(Filtrar(picked, p.Direcao));

            var alvo = Alvo(picked, p.Escopo);
            alvo = Filtrar(alvo, p.Direcao);
            if (alvo.Count == 0) { Show("Nenhum montante nessa direção.", ResultKind.Info); return Result.Cancelled; }

            var tipo = (MullionType)p.Tipo.Tipo;
            var problems = new List<string>();
            int n = Trocar(alvo, tipo, problems, out int iguais);
            if (problems.Count == 0 && n > 0 && iguais == 0) return Result.Succeeded;   // quiet when all went well
            var sb = new StringBuilder();
            sb.AppendLine($"{n} montante(s) trocados para {tipo.Name}." + (iguais > 0 ? $" {iguais} já eram desse tipo." : ""));
            if (problems.Count > 0)
            {
                sb.AppendLine();
                foreach (string w in problems) sb.AppendLine("• " + w);
            }
            Show(sb.ToString(), problems.Count > 0 ? ResultKind.Warning : ResultKind.Success);
            return Result.Succeeded;
        }

        /// <summary>Scope: only these / same current type on the same walls / same type in the project.</summary>
        private List<Mullion> Alvo(List<Mullion> picked, int escopo)
        {
            if (escopo == 0) return picked;
            var tipos = new HashSet<ElementId>(picked.Select(m => m.GetTypeId()));
            IEnumerable<Mullion> pool;
            if (escopo == 1)
            {
                var hosts = new HashSet<ElementId>(picked.Select(m => m.Host?.Id).Where(id => id != null));
                pool = hosts.Select(id => Doc.GetElement(id)).OfType<Wall>().Where(w => w.CurtainGrid != null)
                            .SelectMany(w => w.CurtainGrid.GetMullionIds()).Select(id => Doc.GetElement(id)).OfType<Mullion>();
            }
            else
                pool = new FilteredElementCollector(Doc).OfCategory(BuiltInCategory.OST_CurtainWallMullions).WhereElementIsNotElementType().OfType<Mullion>();
            var ids = new HashSet<ElementId>(picked.Select(m => m.Id));
            var list = new List<Mullion>(picked);
            foreach (Mullion m in pool)
                if (tipos.Contains(m.GetTypeId()) && ids.Add(m.Id)) list.Add(m);
            return list;
        }

        private static List<Mullion> Filtrar(List<Mullion> ms, int direcao) =>
            direcao == 0 ? ms : ms.Where(m => Vertical(m) == (direcao == 1)).ToList();
    }
}
