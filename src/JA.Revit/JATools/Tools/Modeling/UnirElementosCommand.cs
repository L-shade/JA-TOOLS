using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDwgUnifier.Infrastructure;
using RevitDwgUnifier.Tools.Common;

namespace RevitDwgUnifier.Tools.Modeling
{
    /// <summary>
    /// Unir Elementos: joins geometry across the project — walls with their finish walls (paredes
    /// cebola: parallel walls touching, so doors and windows cut every layer), walls × columns,
    /// walls × floors, floors × columns, beams × floors/walls/columns — with a fixed cutting order
    /// (column › beam › floor › wall; the thicker wall cuts its finish).
    /// Groups are never broken: every batch runs in a transaction that is rolled back if Revit
    /// raises an error (e.g. "changes to groups only in edit mode"); joins inside a group type with
    /// several copies are made by rebuilding the type (ungroup one copy, join, group again, swap
    /// every copy to the new type, keep the name) — all copies stay grouped and get the joins.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class UnirElementosCommand : ToolCommand
    {
        public override string Title => "Unir Elementos";

        /// <summary>Joins in batches: one Ctrl+Z undoes them all.</summary>
        protected override bool UmDesfazer => true;

        private enum Papel { Parede = 1, Piso = 2, Viga = 3, Pilar = 4 }

        private sealed class Par
        {
            public Element Corta, Cortado;
            public string Tipo;
            public string Descricao => $"{Nome(Corta)} × {Nome(Cortado)}";
        }

        private sealed class Contagem
        {
            public int Novas, Ordens, JaUnidas, Falhas, Recusados, Disjuntos;
            public readonly Dictionary<string, int> PorTipo = new Dictionary<string, int>();
            public readonly List<string> Motivos = new List<string>();
            public void Somar(Contagem o)
            {
                Novas += o.Novas; Ordens += o.Ordens; JaUnidas += o.JaUnidas; Falhas += o.Falhas; Recusados += o.Recusados; Disjuntos += o.Disjuntos;
                foreach (var kv in o.PorTipo) PorTipo[kv.Key] = (PorTipo.TryGetValue(kv.Key, out int n) ? n : 0) + kv.Value;
                foreach (string m in o.Motivos) if (Motivos.Count < 40) Motivos.Add(m);
            }
        }

        private const string TCebola = "paredes com revestimento", TPilar = "paredes × pilares", TPiso = "paredes × pisos",
                             TPisoPilar = "pisos × pilares", TViga = "vigas";

        private double _tol;
        private bool _corrigirOrdem;

        protected override Result Run()
        {
            var sel = UiDoc.Selection.GetElementIds().Select(id => Doc.GetElement(id)).Where(e => e != null).ToList();
            var dlg = new OptionsDialog(Title, "UnirElementos", "Unir");
            var fScope = dlg.Choice("scope", "Onde", new[] { $"Selecionados ({sel.Count})", "Vista ativa", "Todo o projeto" }, 2,
                "Selecionar um grupo inclui os elementos dele. O outro elemento do par pode estar em qualquer lugar do projeto.");
            if (sel.Count > 0) fScope.Index = 0;
            dlg.Section("O que unir");
            var fCebola = dlg.Check("cebola", "Paredes com revestimentos (paredes cebola)", true,
                "Paredes paralelas encostadas (alvenaria + revestimento): portas e janelas passam a cortar todas as camadas.");
            var fPilar = dlg.Check("pilar", "Paredes × pilares", true, "Pilares arquitetônicos e estruturais.");
            var fPiso = dlg.Check("piso", "Paredes × pisos e lajes", false, "Une as paredes às lajes que elas atravessam ou em que encostam.");
            var fPisoPilar = dlg.Check("pisoPilar", "Pisos × pilares", true);
            var fViga = dlg.Check("viga", "Vigas × pisos, paredes e pilares", true);
            dlg.Section("Ordem de corte");
            var fOrdem = dlg.Check("ordem", "Corrigir a ordem de corte também nos já unidos", true,
                "Pilar corta viga, que corta piso, que corta parede; entre paredes, a mais grossa corta o revestimento.");
            var fTol = dlg.Number("tol", "Folga para contato (mm)", 5, 0, 50, "Paredes de revestimento a até esta distância contam como encostadas.");
            dlg.Section("Grupos");
            var fGrupos = dlg.Choice("grupos", "Elementos em grupos", new[] { "Unir também (os grupos continuam inteiros)", "Ignorar elementos em grupos" }, 0);
            var fRefazer = dlg.Check("refazer", "Grupos com várias cópias: refazer o tipo com as uniões", true,
                "O Revit só aceita mudar grupos com várias cópias em “Editar grupo”. Aqui o tipo é refeito: uma cópia é desagrupada, unida e agrupada de novo, e todas as cópias passam para o tipo novo, com o mesmo nome — nenhuma fica desagrupada.");
            fRefazer.VisibleWhen(fGrupos, 0);
            dlg.Section("Conferir");
            var fPrevia = dlg.Check("previa", "Mostrar o que será unido antes de aplicar", true,
                "Depois de procurar os pares, mostra a quantidade por tipo e pede a confirmação. Nada muda antes disso.");
            dlg.Note("Nada é desagrupado: se o Revit recusar uma união por causa de um grupo, ela é desfeita e listada no resumo.");
            if (!dlg.Show(Owner)) return Result.Cancelled;

            _tol = Math.Max(0, fTol.Value) / 304.8;
            _corrigirOrdem = fOrdem.Value;
            bool grupos = fGrupos.Index == 0;

            ToolProgress.Setup("Unindo elementos", new[] { "Procurar pares", "Unir soltos", "Unir dentro de grupos", "Unir entre grupos", "Concluir" }, cancellable: true);
            ToolProgress.Stage(0, "Lendo o projeto…");

            // ---- every wall, floor, column and beam of the project, read ONCE (role + box), instead of
            //      one collector per element; neighbours come from an in-memory grid
            var todos = new Dictionary<ElementId, Item>();
            foreach (Element e in new FilteredElementCollector(Doc).WherePasses(FiltroEstrutural()).WhereElementIsNotElementType())
            {
                Papel? pp = PapelDe(e);
                if (!pp.HasValue) continue;
                if (!grupos && e.GroupId != ElementId.InvalidElementId) continue;
                BoundingBoxXYZ bb = e.get_BoundingBox(null);
                if (bb == null) continue;
                todos[e.Id] = new Item { E = e, P = pp.Value, Min = bb.Min, Max = bb.Max };
            }

            // ---- the elements in scope (groups selected = their members)
            List<Item> noEscopo;
            if (fScope.Index == 0)
            {
                var ids = new HashSet<ElementId>();
                void Add(Element e)
                {
                    if (e is Group g) { foreach (ElementId m in g.GetMemberIds()) { Element me = Doc.GetElement(m); if (me != null) Add(me); } }
                    else ids.Add(e.Id);
                }
                foreach (Element e in sel) Add(e);
                noEscopo = ids.Where(todos.ContainsKey).Select(id => todos[id]).ToList();
            }
            else if (fScope.Index == 1)
                noEscopo = new FilteredElementCollector(Doc, Doc.ActiveView.Id).WherePasses(FiltroEstrutural()).WhereElementIsNotElementType()
                    .ToElementIds().Where(todos.ContainsKey).Select(id => todos[id]).ToList();
            else noEscopo = todos.Values.ToList();
            if (noEscopo.Count == 0) { ToolProgress.End(); Show("Nenhuma parede, piso, pilar ou viga no escopo.", ResultKind.Info); return Result.Cancelled; }

            var grade = new Grade(todos.Values, _tol + 1e-3);

            // ---- pairs
            var pares = new List<Par>();
            var vistos = new HashSet<(long, long)>();
            int k = 0, jaCertas = 0;
            var tolV = _tol + 1e-3;
            foreach (Item a in noEscopo)
            {
                if (ToolProgress.Cancelled) { ToolProgress.End(); return Result.Cancelled; }
                if (k++ % 50 == 0) ToolProgress.Report(k, noEscopo.Count, Nome(a.E));
                var testarSolido = new List<(Item b, string tipo)>();
                foreach (Item b in grade.Vizinhos(a))
                {
                    if (b.E.Id == a.E.Id) continue;
                    if (b.Min.X > a.Max.X + tolV || b.Max.X < a.Min.X - tolV || b.Min.Y > a.Max.Y + tolV || b.Max.Y < a.Min.Y - tolV ||
                        b.Min.Z > a.Max.Z + tolV || b.Max.Z < a.Min.Z - tolV) continue;
                    string tipo = TipoDoPar(a.P, b.P, fCebola.Value, fPilar.Value, fPiso.Value, fPisoPilar.Value, fViga.Value);
                    if (tipo == null) continue;
                    long ia = a.E.Id.Value, ib = b.E.Id.Value;
                    if (!vistos.Add((Math.Min(ia, ib), Math.Max(ia, ib)))) continue;
                    // already joined: no geometry test; only the order may need fixing
                    bool unidas = false;
                    try { unidas = JoinGeometryUtils.AreElementsJoined(Doc, a.E, b.E); } catch { }
                    if (unidas)
                    {
                        Par pj = Ordem(a, b, tipo);
                        bool certa = true;
                        try { certa = JoinGeometryUtils.IsCuttingElementInJoin(Doc, pj.Corta, pj.Cortado); } catch { }
                        if (certa || !_corrigirOrdem) { jaCertas++; continue; }
                        pares.Add(pj);
                        continue;
                    }
                    if (tipo == TCebola)
                    {
                        if (Encostadas((Wall)a.E, (Wall)b.E)) pares.Add(Ordem(a, b, tipo));
                        continue;
                    }
                    testarSolido.Add((b, tipo));
                }
                if (testarSolido.Count == 0) continue;
                // one native pass for all the neighbours of a (the filter keeps a's geometry)
                HashSet<ElementId> tocam;
                try
                {
                    tocam = new HashSet<ElementId>(new FilteredElementCollector(Doc, testarSolido.Select(x => x.b.E.Id).ToList())
                        .WherePasses(new ElementIntersectsElementFilter(a.E)).ToElementIds());
                }
                catch { tocam = new HashSet<ElementId>(); }
                foreach (var (b, tipo) in testarSolido)
                {
                    bool toca = tocam.Contains(b.E.Id);
                    if (!toca && (tipo == TPiso || tipo == TPisoPilar || (tipo == TViga && (a.P == Papel.Piso || b.P == Papel.Piso))))
                        toca = ContatoVertical(a, b);
                    if (toca) pares.Add(Ordem(a, b, tipo));
                }
            }
            if (pares.Count == 0)
            {
                ToolProgress.End();
                Show(jaCertas > 0 ? $"Nada a fazer: {jaCertas} par(es) já estavam unidos na ordem certa." : "Nenhum par de elementos encostados ou cruzados com essas opções.", ResultKind.Info);
                return Result.Succeeded;
            }

            if (fPrevia.Value)
            {
                ToolProgress.Hide();
                var conf = new OptionsDialog(Title, "UnirElementosConfirmar", "Unir");
                conf.Section($"{pares.Count} par(es) para unir ou corrigir");
                foreach (var g in pares.GroupBy(p => p.Tipo).OrderByDescending(g => g.Count()))
                    conf.Note($"{g.Key}: {g.Count()} par(es) — por exemplo {string.Join("; ", g.Take(3).Select(p => p.Descricao))}");
                int emGrupos = pares.Count(p => p.Corta.GroupId != ElementId.InvalidElementId || p.Cortado.GroupId != ElementId.InvalidElementId);
                if (emGrupos > 0) conf.Note($"{emGrupos} par(es) envolvem elementos em grupos.");
                if (jaCertas > 0) conf.Note($"{jaCertas} par(es) já estão unidos na ordem certa (não mudam).");
                if (!conf.Show(Owner)) { ToolProgress.End(); return Result.Cancelled; }
            }

            // ---- by group situation
            var soltos = pares.Where(p => p.Corta.GroupId == ElementId.InvalidElementId && p.Cortado.GroupId == ElementId.InvalidElementId).ToList();
            var internos = pares.Where(p => p.Corta.GroupId != ElementId.InvalidElementId && p.Corta.GroupId == p.Cortado.GroupId).ToList();
            var mistos = pares.Except(soltos).Except(internos).ToList();

            var total = new Contagem { JaUnidas = jaCertas };
            var problemas = new List<string>();
            var refeitos = new List<string>();
            int gruposOk = 0;

            ToolProgress.Stage(1, $"{soltos.Count} par(es) fora de grupos…");
            Lote(soltos, "Unir elementos", total, 1000);

            ToolProgress.Stage(2, "Grupos…");
            var porTipo = internos.GroupBy(p => (Doc.GetElement(p.Corta.GroupId) as Group)?.GroupType?.Id ?? ElementId.InvalidElementId).ToList();
            int gi = 0;
            foreach (var tg in porTipo)
            {
                if (ToolProgress.Cancelled) break;
                ToolProgress.Report(gi++, porTipo.Count, "Grupos");
                var lista = tg.ToList();
                GroupType tipo = Doc.GetElement(tg.Key) as GroupType;
                string nomeTipo = tipo?.Name ?? "grupo";
                // 1) directly (Revit accepts it for a group with one copy)
                var c = new Contagem();
                if (TentarTudo(lista, "Unir no grupo " + nomeTipo, c)) { total.Somar(c); gruposOk++; continue; }
                // 2) rebuild the type: all copies stay grouped and get the joins
                if (fRefazer.Value && grupos && tipo != null)
                {
                    Group g0 = lista.GroupBy(p => p.Corta.GroupId).OrderByDescending(g => g.Count()).Select(g => Doc.GetElement(g.Key) as Group).FirstOrDefault(g => g != null);
                    var deG0 = lista.Where(p => g0 != null && p.Corta.GroupId == g0.Id).ToList();
                    var c2 = new Contagem();
                    int copias = 0;
                    try { copias = tipo.Groups?.Size ?? 0; } catch { }
                    string motivo = g0 == null ? "grupo não encontrado" : RefazerGrupo(tipo, g0, deG0, c2);
                    if (motivo == null) { total.Somar(c2); refeitos.Add(nomeTipo + " (" + copias + " cópias)"); gruposOk++; continue; }
                    problemas.Add($"Grupo “{nomeTipo}”: não unido ({motivo}). Nada foi desagrupado.");
                }
                else problemas.Add($"Grupo “{nomeTipo}”: tem várias cópias; o Revit só aceita a união em “Editar grupo”. Nada foi desagrupado.");
                total.Recusados += lista.Count;
            }

            ToolProgress.Stage(3, $"{mistos.Count} par(es) entre grupo e fora…");
            var mistosValidos = mistos.Where(p => p.Corta.IsValidObject && p.Cortado.IsValidObject).ToList();
            if (mistosValidos.Count < mistos.Count)
                problemas.Add($"{mistos.Count - mistosValidos.Count} par(es) entre um grupo refeito e elementos fora dele: rode a ferramenta de novo para uni-los.");
            Lote(mistosValidos, "Unir elementos com grupos", total, 200);

            ToolProgress.Stage(4, "Resumo…");
            ToolProgress.End();

            var linhas = new List<string>();
            linhas.Add($"{total.Novas} união(ões) nova(s)" + (total.PorTipo.Count > 0 ? ": " + string.Join(", ", total.PorTipo.Select(kv => kv.Value + " " + kv.Key)) : "") + ".");
            if (total.Ordens > 0) linhas.Add($"{total.Ordens} ordem(ns) de corte corrigida(s).");
            if (total.JaUnidas > 0) linhas.Add($"{total.JaUnidas} par(es) já estavam unidos.");
            if (gruposOk > 0) linhas.Add($"{gruposOk} tipo(s) de grupo com uniões internas — todos continuam agrupados.");
            if (refeitos.Count > 0) linhas.Add("Tipos de grupo refeitos (mesmo nome, todas as cópias atualizadas): " + string.Join(", ", refeitos.Take(15)) + (refeitos.Count > 15 ? "…" : "") + ".");
            if (total.Disjuntos > 0) linhas.Add($"{total.Disjuntos} par(es) só se tocavam na caixa, não de verdade: desfeitos pelo Revit.");
            if (total.Recusados > 0) problemas.Insert(0, $"{total.Recusados} par(es) não unidos para não quebrar grupos (ou recusados pelo Revit).");
            if (total.Falhas > 0) problemas.Add($"{total.Falhas} par(es) que o Revit não sabe unir (geometria) — ex.: " + string.Join("; ", total.Motivos.Take(5)));
            string texto = string.Join("\n", linhas) + (problemas.Count > 0 ? "\n\nAtenção:\n• " + string.Join("\n• ", problemas.Take(25)) : "");
            Show(texto, problemas.Count > 0 ? ResultKind.Warning : ResultKind.Success);
            return Result.Succeeded;
        }

        // ================================================================== roles and pairs

        private static Papel? PapelDe(Element e)
        {
            switch (e)
            {
                case Wall w:
                    try { return w.WallType != null && w.WallType.Kind == WallKind.Basic && w.Location is LocationCurve ? Papel.Parede : (Papel?)null; } catch { return null; }
                case Floor _:
                    return Papel.Piso;
                case FamilyInstance fi:
                {
                    long c = fi.Category?.Id.Value ?? 0;
                    if (c == (long)BuiltInCategory.OST_Columns || c == (long)BuiltInCategory.OST_StructuralColumns) return Papel.Pilar;
                    if (c == (long)BuiltInCategory.OST_StructuralFraming) return Papel.Viga;
                    return null;
                }
                default: return null;
            }
        }

        private static ElementFilter FiltroEstrutural() => new ElementMulticategoryFilter(new List<BuiltInCategory>
        {
            BuiltInCategory.OST_Walls, BuiltInCategory.OST_Floors, BuiltInCategory.OST_Columns, BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_StructuralFraming
        });

        private static string TipoDoPar(Papel a, Papel b, bool cebola, bool pilar, bool piso, bool pisoPilar, bool viga)
        {
            if (a == Papel.Parede && b == Papel.Parede) return cebola ? TCebola : null;
            var s = new HashSet<Papel> { a, b };
            if (s.SetEquals(new[] { Papel.Parede, Papel.Pilar })) return pilar ? TPilar : null;
            if (s.SetEquals(new[] { Papel.Parede, Papel.Piso })) return piso ? TPiso : null;
            if (s.SetEquals(new[] { Papel.Piso, Papel.Pilar })) return pisoPilar ? TPisoPilar : null;
            if (s.Contains(Papel.Viga) && s.Count == 2) return viga ? TViga : null;
            return null;
        }

        private sealed class Item
        {
            public Element E;
            public Papel P;
            public XYZ Min, Max;
        }

        /// <summary>Who cuts whom: column › beam › floor › wall; between walls the thicker one.</summary>
        private static Par Ordem(Item a, Item b, string tipo)
        {
            bool aCorta = tipo == TCebola ? ((Wall)a.E).Width >= ((Wall)b.E).Width : (int)a.P > (int)b.P;
            return new Par { Corta = aCorta ? a.E : b.E, Cortado = aCorta ? b.E : a.E, Tipo = tipo };
        }

        /// <summary>One sits on (or hangs from) the other: horizontal faces at the same height, plans overlapping.</summary>
        private bool ContatoVertical(Item x, Item y)
        {
            bool alturas = Math.Abs(x.Min.Z - y.Max.Z) <= _tol + 1e-3 || Math.Abs(y.Min.Z - x.Max.Z) <= _tol + 1e-3;
            double ox = Math.Min(x.Max.X, y.Max.X) - Math.Max(x.Min.X, y.Min.X);
            double oy = Math.Min(x.Max.Y, y.Max.Y) - Math.Max(x.Min.Y, y.Min.Y);
            return alturas && ox > 0.03 && oy > 0.03;
        }

        /// <summary>Plan grid of element boxes (cells of 2 m): the neighbours of an element are read from
        /// the cells it covers; very large elements (big slabs) go to a list checked against everyone.</summary>
        private sealed class Grade
        {
            private const double Celula = 2 / 0.3048;
            private readonly Dictionary<(int, int), List<Item>> _c = new Dictionary<(int, int), List<Item>>();
            private readonly List<Item> _grandes = new List<Item>();
            private readonly double _f;

            public Grade(IEnumerable<Item> itens, double folga)
            {
                _f = folga;
                foreach (Item it in itens)
                {
                    var (x0, y0, x1, y1) = Faixa(it);
                    if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > 400) { _grandes.Add(it); continue; }
                    for (int x = x0; x <= x1; x++)
                        for (int y = y0; y <= y1; y++)
                        {
                            if (!_c.TryGetValue((x, y), out var l)) _c[(x, y)] = l = new List<Item>();
                            l.Add(it);
                        }
                }
            }

            private (int, int, int, int) Faixa(Item it) =>
                ((int)Math.Floor((it.Min.X - _f) / Celula), (int)Math.Floor((it.Min.Y - _f) / Celula),
                 (int)Math.Floor((it.Max.X + _f) / Celula), (int)Math.Floor((it.Max.Y + _f) / Celula));

            public IEnumerable<Item> Vizinhos(Item a)
            {
                var vistos = new HashSet<ElementId>();
                var (x0, y0, x1, y1) = Faixa(a);
                if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > 400)
                {
                    // a big element: every element (the box test outside filters them)
                    foreach (var l in _c.Values) foreach (Item it in l) if (vistos.Add(it.E.Id)) yield return it;
                }
                else
                    for (int x = x0; x <= x1; x++)
                        for (int y = y0; y <= y1; y++)
                            if (_c.TryGetValue((x, y), out var l))
                                foreach (Item it in l) if (vistos.Add(it.E.Id)) yield return it;
                foreach (Item it in _grandes) if (vistos.Add(it.E.Id)) yield return it;
            }
        }

        /// <summary>Parallel walls side by side (a wall and its finish): straight and parallel, or arcs
        /// with the same centre, axes (w1 + w2)/2 apart (+ tolerance), overlapping along and in height.</summary>
        private bool Encostadas(Wall a, Wall b)
        {
            if (!(a.Location is LocationCurve la) || !(b.Location is LocationCurve lb)) return false;
            double alvo = (a.Width + b.Width) / 2 + _tol + 1e-3;
            BoundingBoxXYZ x = a.get_BoundingBox(null), y = b.get_BoundingBox(null);
            if (x == null || y == null || Math.Min(x.Max.Z, y.Max.Z) - Math.Max(x.Min.Z, y.Min.Z) < 0.03) return false;
            if (la.Curve is Line l1 && lb.Curve is Line l2)
            {
                XYZ d = Plano(l1.Direction), d2 = Plano(l2.Direction);
                if (Math.Abs(d.X * d2.Y - d.Y * d2.X) > 0.02) return false;   // > ~1°
                XYZ n = new XYZ(-d.Y, d.X, 0);
                XYZ p1 = Plano(l1.GetEndPoint(0)), q0 = Plano(l2.GetEndPoint(0)), q1 = Plano(l2.GetEndPoint(1));
                double dist = Math.Abs((q0 - p1).DotProduct(n));
                if (dist > alvo) return false;
                double s0 = Plano(l1.GetEndPoint(0)).DotProduct(d), s1 = Plano(l1.GetEndPoint(1)).DotProduct(d);
                double t0 = q0.DotProduct(d), t1 = q1.DotProduct(d);
                double sobre = Math.Min(Math.Max(s0, s1), Math.Max(t0, t1)) - Math.Max(Math.Min(s0, s1), Math.Min(t0, t1));
                return sobre > 0.03;
            }
            if (la.Curve is Arc a1 && lb.Curve is Arc a2)
            {
                if (Plano(a1.Center).DistanceTo(Plano(a2.Center)) > _tol + 0.01) return false;
                return Math.Abs(a1.Radius - a2.Radius) <= alvo;
            }
            return false;
        }

        private static XYZ Plano(XYZ v) => new XYZ(v.X, v.Y, 0);

        private static string Nome(Element e)
        {
            try { return (e.Category?.Name ?? "Elemento") + " " + e.Id.Value; } catch { return "elemento"; }
        }

        // ================================================================== joining

        /// <summary>One join, in the right order (or only the order fixed, when already joined).</summary>
        private void Unir(Par p, Contagem c)
        {
            try
            {
                if (JoinGeometryUtils.AreElementsJoined(Doc, p.Corta, p.Cortado))
                {
                    c.JaUnidas++;
                    if (_corrigirOrdem && !JoinGeometryUtils.IsCuttingElementInJoin(Doc, p.Corta, p.Cortado))
                    {
                        JoinGeometryUtils.SwitchJoinOrder(Doc, p.Corta, p.Cortado);
                        c.Ordens++;
                    }
                    return;
                }
                JoinGeometryUtils.JoinGeometry(Doc, p.Corta, p.Cortado);
                if (!JoinGeometryUtils.IsCuttingElementInJoin(Doc, p.Corta, p.Cortado))
                    JoinGeometryUtils.SwitchJoinOrder(Doc, p.Corta, p.Cortado);
                c.Novas++;
                c.PorTipo[p.Tipo] = (c.PorTipo.TryGetValue(p.Tipo, out int n) ? n : 0) + 1;
            }
            catch (Exception ex)
            {
                c.Falhas++;
                if (c.Motivos.Count < 40) c.Motivos.Add(p.Descricao + ": " + ex.Message);
            }
        }

        /// <summary>All the pairs in one transaction; false (and nothing changed) if Revit raised an error.</summary>
        private bool TentarTudo(List<Par> pares, string nome, Contagem c)
        {
            var guarda = new GuardaGrupos();
            var parcial = new Contagem();
            try
            {
                using (var t = new Transaction(Doc, "JA Tools - " + nome))
                {
                    t.Start();
                    guarda.Anexar(t);
                    foreach (Par p in pares) Unir(p, parcial);
                    TransactionStatus st = t.Commit();
                    if (st != TransactionStatus.Committed || guarda.Desfez) return false;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("[Unir Elementos] " + nome + ": " + ex.Message);
                return false;
            }
            parcial.Disjuntos += guarda.Disjuntos;
            c.Somar(parcial);
            return true;
        }

        /// <summary>Batches of pairs; a batch Revit refuses is split in halves, so only the refused pairs stay out.</summary>
        private void Lote(List<Par> pares, string nome, Contagem total, int Tamanho)
        {
            for (int i = 0; i < pares.Count; i += Tamanho)
            {
                if (ToolProgress.Cancelled) return;
                ToolProgress.Report(i, pares.Count, nome);
                Dividir(pares.Skip(i).Take(Tamanho).ToList(), nome, total);
            }
        }

        private void Dividir(List<Par> pares, string nome, Contagem total)
        {
            if (pares.Count == 0) return;
            if (TentarTudo(pares, nome, total)) return;
            if (pares.Count == 1)
            {
                total.Recusados++;
                if (total.Motivos.Count < 40) total.Motivos.Add(pares[0].Descricao + ": o Revit recusou (grupo)");
                return;
            }
            int meio = pares.Count / 2;
            Dividir(pares.Take(meio).ToList(), nome, total);
            Dividir(pares.Skip(meio).ToList(), nome, total);
        }

        // ================================================================== groups with several copies

        /// <summary>Rebuilds the group type with the joins (see <see cref="GruposSeguros.RefazerTipo"/>). Null = done.</summary>
        private string RefazerGrupo(GroupType tipo, Group g0, List<Par> pares, Contagem c)
        {
            var parcial = new Contagem();
            string motivo = GruposSeguros.RefazerTipo(Doc, tipo, g0, membros => { foreach (Par p in pares) Unir(p, parcial); }, out int disj);
            if (motivo != null) return motivo;
            parcial.Disjuntos += disj;
            c.Somar(parcial);
            return null;
        }

    }
}
