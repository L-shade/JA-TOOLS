using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using JAEsquadrias.Common;
using JAEsquadrias.Core;
using JAEsquadrias.Infrastructure;

namespace JAEsquadrias.Commands
{
    /// <summary>
    /// Rodapé por Ambiente: many rooms at once (a list of every placed room), each with its own rodapé.
    /// The rodapé of a room comes from its Acabamento de base (when it gives material and height and was
    /// not written by this tool), else from the room name (Rodapé B / C keywords), else Rodapé A; rooms
    /// whose name has a "sem rodapé" keyword get none (and lose the one they had). Same discounts as Rodapé;
    /// one AplicarRodapes per distinct rodapé; summary per rodapé and per room, purchase and Excel.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class RodapePorAmbienteCommand : EsquadriaCommandBase
    {
        public override string Title => "Rodapé por Ambiente";

        protected override Result Run()
        {
            var todos = AmbientesColocados();
            if (todos.Count == 0)
            {
                Show("O projeto não tem ambientes colocados.", ResultKind.Warning);
                return Result.Cancelled;
            }
            string Rotulo(Room r) => Amb(r) + " (" + (Doc.GetElement(r.LevelId)?.Name ?? "?") + ")";
            var rotulos = todos.Select(Rotulo).ToList();

            // which rooms start checked: the selected ones, else the active plan's level, else all
            var pre = new HashSet<long>();
            foreach (ElementId id in UiDoc.Selection.GetElementIds())
            {
                Element el = Doc.GetElement(id);
                if (el is Room rr) pre.Add(rr.Id.Value);
                else if (el is RoomTag tg && tg.Room != null) pre.Add(tg.Room.Id.Value);
            }
            ElementId nivelVista = (Doc.ActiveView as ViewPlan)?.GenLevel?.Id;
            bool Marcado(Room r) => pre.Count > 0 ? pre.Contains(r.Id.Value) : nivelVista == null || r.LevelId == nivelVista;

            var dlg = new OptionsDialog(Title, "RodapeAmbiente");
            dlg.Section("Ambientes");
            var fAmb = dlg.Multi("ambientes", "Ambientes", rotulos, true, true, "Já vêm marcados os selecionados antes do comando ou, sem seleção, os do nível da planta ativa.");
            for (int i = 0; i < todos.Count; i++) fAmb.Checked[i] = Marcado(todos[i]);
            var fSem = dlg.Text("sem", "Sem rodapé nos ambientes cujo nome contém", "banheiro, banho, lavabo, wc, piscina",
                "Separe por vírgula. Rodar de novo apaga o rodapé que esses ambientes tenham.");
            var fAcab = dlg.Check("usarAcab", "Usar o Acabamento de base do ambiente quando ele trouxer material e altura", true,
                "Ex.: “Granito branco h 100 mm” ou “Poliestireno h 10 cm × 15”. O que o próprio JA Famílias escreveu é ignorado (vale o rodapé abaixo).");

            dlg.Section("Rodapé A (padrão)");
            var fAAlt = dlg.Number("aAlt", "Altura (mm)", 70, 20, 400);
            var fAEsp = dlg.Number("aEsp", "Espessura (mm)", 15, 5, 40);
            var fAMat = dlg.Text("aMat", "Material", "Granito cinza andorinha");

            dlg.Section("Rodapé B");
            var fBPal = dlg.Text("bPal", "Ambientes cujo nome contém (vazio = não usa)", "cozinha, serviço, servico, lavanderia, copa");
            var fBAlt = dlg.Number("bAlt", "Altura (mm)", 100, 20, 400);
            var fBEsp = dlg.Number("bEsp", "Espessura (mm)", 10, 5, 40);
            var fBMat = dlg.Text("bMat", "Material", "Porcelanato recortado");

            dlg.Section("Rodapé C");
            var fCPal = dlg.Text("cPal", "Ambientes cujo nome contém (vazio = não usa)", "",
                "Ex.: quarto, dormitório, suíte para um rodapé de poliestireno ou MDF.");
            var fCAlt = dlg.Number("cAlt", "Altura (mm)", 100, 20, 400);
            var fCEsp = dlg.Number("cEsp", "Espessura (mm)", 15, 5, 40);
            var fCMat = dlg.Text("cMat", "Material", "Poliestireno branco");

            CamposRodape fd = DescontosRodape(dlg);
            dlg.Section("Compra");
            var fPerda = dlg.Number("perda", "Perda (%)", 10, 0, 50);
            var fBarra = dlg.Number("barra", "Comprimento da peça (mm; 0 = por metro)", 0, 0, 6000, "Vale para todos os rodapés; a quantidade é arredondada para cima.");
            var fExcel = dlg.Check("excel", "Salvar a planilha dos descontos (Excel)", false);
            dlg.Note("A ordem é: sem rodapé → Acabamento de base → Rodapé B → Rodapé C → Rodapé A. Só nas paredes e pilares.");
            if (!dlg.Show(Owner)) return Result.Cancelled;

            var rooms = fAmb.SelectedIndices.Select(i => todos[i]).ToList();
            if (rooms.Count == 0) return Result.Cancelled;

            ConfigRodape Base(double alt, double esp, string mat)
            {
                var c = new ConfigRodape { Altura = alt, Espessura = esp, Material = string.IsNullOrWhiteSpace(mat) ? "Rodapé" : mat.Trim(), Perda = fPerda.Value, Barra = fBarra.Value };
                fd.Aplicar(c);
                return c;
            }
            var tipoA = Base(fAAlt.Value, fAEsp.Value, fAMat.Value);
            var tipoB = Base(fBAlt.Value, fBEsp.Value, fBMat.Value);
            var tipoC = Base(fCAlt.Value, fCEsp.Value, fCMat.Value);
            string[] palB = Palavras(fBPal.Value), palC = Palavras(fCPal.Value), palSem = Palavras(fSem.Value);

            // what this tool already wrote in each room (so its own Acabamento de base is not read back as the designer's)
            var escritos = new Dictionary<long, HashSet<string>>();
            var antigos = new Dictionary<long, List<ElementId>>();
            foreach (FamilyInstance fi in new FilteredElementCollector(Doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>())
            {
                DadosJa dj = JaDados.Ler(fi);
                if (dj == null || dj.Tipo != "rodape" || dj.Ambiente == 0) continue;
                if (!antigos.TryGetValue(dj.Ambiente, out var l)) antigos[dj.Ambiente] = l = new List<ElementId>();
                l.Add(fi.Id);
                ConfigRodape cr = null;
                try { cr = Json.Deserialize<ConfigRodape>(dj.Config); } catch { }
                if (cr == null) continue;
                if (!escritos.TryGetValue(dj.Ambiente, out var h)) escritos[dj.Ambiente] = h = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                h.Add(TextoAcabamento(cr));
            }

            // one rodapé per room
            var grupos = new Dictionary<string, (string nome, ConfigRodape c, List<Room> rooms)>();
            var semRodape = new List<Room>();
            foreach (Room r in rooms)
            {
                string nomeAmb = NomeDe(r).ToLowerInvariant();
                if (palSem.Any(k => nomeAmb.Contains(k))) { semRodape.Add(r); continue; }
                string nome; ConfigRodape c;
                string acab = r.get_Parameter(BuiltInParameter.ROOM_FINISH_BASE)?.AsString();
                if (fAcab.Value && !string.IsNullOrWhiteSpace(acab)
                    && !(escritos.TryGetValue(r.Id.Value, out var ja) && ja.Contains(acab.Trim()))
                    && LerAcabamento(acab, tipoA.Espessura, out string mat, out double alt, out double esp))
                {
                    c = Base(alt, esp, mat);
                    nome = "do Acabamento de base";
                }
                else if (palB.Any(k => nomeAmb.Contains(k))) { c = tipoB; nome = "B"; }
                else if (palC.Any(k => nomeAmb.Contains(k))) { c = tipoC; nome = "C"; }
                else { c = tipoA; nome = "A"; }
                string chave = Json.Serialize(c);
                if (!grupos.TryGetValue(chave, out var g)) grupos[chave] = g = (nome, c, new List<Room>());
                g.rooms.Add(r);
            }

            if (fd.Previa.Value && grupos.Count > 0 &&
                !ConfirmarRodape(grupos.Values.Select(g => (g.c, g.rooms)).ToList())) return Result.Cancelled;
            var avisos = new List<string>();
            var feitos = new List<(ConfigRodape c, ResultadoRodape r)>();
            var cab = new StringBuilder();
            cab.AppendLine($"{rooms.Count} ambiente(s): {rooms.Count - semRodape.Count} com rodapé em {grupos.Count} tipo(s).");
            // every rodapé family first (generating / loading a family cannot happen inside a transaction
            // group); then all the rooms in one group: one Ctrl+Z undoes the whole run
            foreach (var g in grupos.Values) { try { SimboloRodape(g.c, avisos, out _); } catch { } }
            ToolProgress.Setup("Colocando os rodapés", null);
            var todosAmb = grupos.Values.SelectMany(g => g.rooms).ToList();   // a separation-line corner between two rodapés closes once
            var grupoTodos = new TransactionGroup(Doc, Title);
            try { grupoTodos.Start(); } catch { grupoTodos = null; }
            try
            {
            foreach (var g in grupos.Values)
            {
                ResultadoRodape r0 = AplicarRodapes(g.rooms, g.c, fd.Refazer.Value, avisos, todosAmb);
                if (r0 == null) continue;
                feitos.Add((g.c, r0));
                cab.AppendLine($"• Rodapé {g.nome} = {r0.Codigo} ({g.c.Material}, h {PecaUtil.Mm(g.c.Altura)} mm): {string.Join(", ", g.rooms.Select(Amb).Take(12))}" +
                               (g.rooms.Count > 12 ? $" e mais {g.rooms.Count - 12}" : ""));
            }

            // rooms without rodapé: remove the old one (and the Acabamento de base this tool wrote)
            if (semRodape.Count > 0)
            {
                int apagados = 0;
                if (fd.Refazer.Value)
                {
                    using (var t = new Transaction(Doc, "JA Famílias – sem rodapé"))
                    {
                        t.Start();
                        foreach (Room r in semRodape)
                        {
                            if (!antigos.TryGetValue(r.Id.Value, out var ids)) continue;
                            foreach (ElementId id in ids) { try { Doc.Delete(id); apagados++; } catch { } }
                            Parameter pb = r.get_Parameter(BuiltInParameter.ROOM_FINISH_BASE);
                            string acab = pb?.AsString();
                            if (pb != null && !pb.IsReadOnly && acab != null && escritos.TryGetValue(r.Id.Value, out var ja) && ja.Contains(acab.Trim()))
                                try { pb.Set(""); } catch { }
                        }
                        t.Commit();
                    }
                }
                cab.AppendLine($"• Sem rodapé: {string.Join(", ", semRodape.Select(Amb).Take(15))}" + (semRodape.Count > 15 ? $" e mais {semRodape.Count - 15}" : "") +
                               (apagados > 0 ? $" ({apagados} trecho(s) antigo(s) apagado(s))" : ""));
            }

            }
            finally
            {
                if (grupoTodos != null)
                {
                    try { if (grupoTodos.GetStatus() == TransactionStatus.Started) grupoTodos.Assimilate(); } catch { }
                    grupoTodos.Dispose();
                }
            }

            if (feitos.Count == 0)
            {
                Show(cab.ToString().TrimEnd() + (avisos.Count > 0 ? "\n\nAvisos:\n• " + string.Join("\n• ", avisos.Distinct().Take(15)) : ""),
                     avisos.Count > 0 ? ResultKind.Warning : ResultKind.Success);
                return Result.Succeeded;
            }
            MostrarRodape(feitos, fExcel.Value, avisos, cab.ToString());
            return Result.Succeeded;
        }

        /// <summary>The room's name alone (Room.Name also carries the number).</summary>
        private static string NomeDe(Room r) => r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? r.Name ?? "";
        private static string Amb(Room r) => (r.Number + " " + NomeDe(r)).Trim();

        private static string[] Palavras(string s) =>
            (s ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0).ToArray();

        /// <summary>The Acabamento de base this tool writes for a rodapé (see AplicarRodapes).</summary>
        private static string TextoAcabamento(ConfigRodape c) => c.Material + " h " + PecaUtil.Mm(c.Altura) + " mm";

        // material, then the height — "h 100 mm", "h=10cm", "h.10cm", "altura 7 cm", or just "10 cm" — then maybe the thickness ("× 15", "e=15", "esp 15")
        private static readonly Regex ReAcab = new Regex(
            @"^\s*(?<mat>.+?)\s*[-–,;:]?\s*(?:\b(?:h|alt(?:ura)?)\.?\s*[=:]?\s*(?<h>\d+(?:[.,]\d+)?)\s*(?<u>mm|cm)?|(?<h2>\d+(?:[.,]\d+)?)\s*(?<u2>mm|cm)\b)" +
            @"(?:\s*(?:[x×]|e\s*[=:]?|esp\.?\s*[=:]?)\s*(?<e>\d+(?:[.,]\d+)?)\s*(?<ue>mm|cm)?)?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>"Granito branco h 100 mm", "Poliestireno h 10 cm × 15", "MDF - h=70 e=15", "Rodapé em granito, altura 7 cm",
        /// "Rodapé poliestireno 10 cm": material, height and thickness (mm). Tested in docs/testes/rodape.</summary>
        internal static bool LerAcabamento(string s, double espPadrao, out string material, out double altura, out double espessura)
        {
            material = null; altura = 0; espessura = espPadrao;
            Match m = ReAcab.Match(s ?? "");
            if (!m.Success) return false;
            double Num(System.Text.RegularExpressions.Group g, System.Text.RegularExpressions.Group u)
            {
                double v = double.Parse(g.Value.Replace(',', '.'), CultureInfo.InvariantCulture);
                return u.Success && u.Value.Equals("cm", StringComparison.OrdinalIgnoreCase) ? v * 10 : v;
            }
            material = m.Groups["mat"].Value.Trim(' ', '-', '–', ',', ';', ':');
            altura = m.Groups["h"].Success ? Num(m.Groups["h"], m.Groups["u"]) : Num(m.Groups["h2"], m.Groups["u2"]);
            if (m.Groups["e"].Success) espessura = Num(m.Groups["e"], m.Groups["ue"]);
            if (material.Count(char.IsLetter) < 2 || altura < 20 || altura > 400) return false;
            if (espessura < 5 || espessura > 40) espessura = espPadrao;
            return true;
        }
    }
}
