using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDwgUnifier.Infrastructure;
using RevitDwgUnifier.Tools.Common;

namespace RevitDwgUnifier.Tools.Modeling
{
    /// <summary>
    /// Paredes nas Lajes: finds walls whose top goes into (or through) the slab above, walls that stop
    /// short of it, and (optionally) walls whose base starts inside the slab below; sets the top flush
    /// with the underside of the slab (height/offset, or "attach top" for sloped slabs) and the base on
    /// the top of the slab. The slab is measured at points along the wall (vertical line × slab solid),
    /// so it works with any thickness and level. Groups are kept whole (see <see cref="GruposSeguros"/>).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class ParedesNasLajesCommand : ToolCommand
    {
        public override string Title => "Paredes nas Lajes";

        private const string SAtravessa = "atravessa a laje", SCurta = "não chega na laje", SBase = "base dentro da laje";

        private sealed class Ajuste
        {
            public Wall Parede;
            public double DeltaTopo, DeltaBase;
            public bool Anexar;
            public ElementId Laje = ElementId.InvalidElementId;
            public readonly List<string> Situacoes = new List<string>();
            public string Assinatura => string.Join(",", Situacoes) + "|" + Math.Round(DeltaTopo * 304.8) + "|" + Math.Round(DeltaBase * 304.8) + "|" + Anexar;
        }

        private double _tol, _folga, _multiAndar;
        private bool _atravessa, _curtas, _base, _anexar, _telhados;
        private readonly Dictionary<ElementId, List<Solid>> _solidos = new Dictionary<ElementId, List<Solid>>();
        private readonly Dictionary<ElementId, (Ajuste aj, string atencao)> _analise = new Dictionary<ElementId, (Ajuste, string)>();

        protected override Result Run()
        {
            var sel = UiDoc.Selection.GetElementIds().Select(id => Doc.GetElement(id)).Where(e => e != null).ToList();
            var dlg = new OptionsDialog(Title, "ParedesNasLajes", "Ajustar");
            var fScope = dlg.Choice("scope", "Onde", new[] { $"Selecionados ({sel.Count})", "Vista ativa", "Todo o projeto" }, 2, "Selecionar um grupo inclui as paredes dele.");
            if (sel.Count > 0) fScope.Index = 0;
            dlg.Section("O que ajustar");
            var fAtravessa = dlg.Check("atravessa", "Paredes que entram na laje de cima: topo rente à face de baixo", true);
            var fCurtas = dlg.Check("curtas", "Paredes que não chegam até a laje: subir até ela", true, "Só quando a folga for pequena (parede baixa de propósito fica como está).");
            var fFolga = dlg.Number("folga", "… se faltar até (mm)", 150, 1, 1000);
            fFolga.EnabledWhen(fCurtas);
            var fBase = dlg.Check("base", "Base dentro da laje de baixo: subir a base até o topo da laje", false);
            var fTelhados = dlg.Check("telhados", "Considerar também telhados", false);
            var fMulti = dlg.Number("multi", "Não mexer em paredes que passam do topo da laje mais de (mm)", 300, 0, 50000,
                "Paredes de mais de um andar (fachada, poço, platibanda) ficam como estão.");
            dlg.Section("Como");
            var fModo = dlg.Choice("modo", "Ajuste do topo", new[] { "Altura exata (topo na face de baixo da laje)", "Anexar o topo à laje (segue laje inclinada e mudanças futuras)" }, 0);
            var fGrupos = dlg.Choice("grupos", "Paredes em grupos", new[] { "Ajustar também (os grupos continuam inteiros)", "Ignorar paredes em grupos" }, 0,
                "Grupo com várias cópias: o tipo é refeito com as paredes ajustadas, desde que todas as cópias precisem do mesmo ajuste.");
            var fFim = dlg.Choice("fim", "No fim, selecionar", new[] { "As paredes ajustadas", "As que precisam de atenção", "Nada" }, 0);
            var fSo = dlg.Check("so", "Só verificar (não mudar nada)", false);
            if (!dlg.Show(Owner)) return Result.Cancelled;

            _tol = 2 / 304.8;
            _folga = fFolga.Value / 304.8;
            _multiAndar = fMulti.Value / 304.8;
            _atravessa = fAtravessa.Value; _curtas = fCurtas.Value; _base = fBase.Value; _telhados = fTelhados.Value;
            _anexar = fModo.Index == 1;
            bool grupos = fGrupos.Index == 0;

            // ---- walls in scope
            IEnumerable<Element> escopo;
            if (fScope.Index == 0)
            {
                var ids = new HashSet<ElementId>();
                void Add(Element e)
                {
                    if (e is Group g) { foreach (ElementId m in g.GetMemberIds()) { Element me = Doc.GetElement(m); if (me != null) Add(me); } }
                    else ids.Add(e.Id);
                }
                foreach (Element e in sel) Add(e);
                escopo = ids.Select(id => Doc.GetElement(id));
            }
            else escopo = (fScope.Index == 1 ? new FilteredElementCollector(Doc, Doc.ActiveView.Id) : new FilteredElementCollector(Doc)).OfClass(typeof(Wall)).ToElements();
            var paredes = escopo.OfType<Wall>().Where(Basica).Where(w => grupos || w.GroupId == ElementId.InvalidElementId).ToList();
            if (paredes.Count == 0) { Show("Nenhuma parede no escopo.", ResultKind.Info); return Result.Cancelled; }

            ToolProgress.Setup("Paredes nas lajes", new[] { "Medir paredes e lajes", "Ajustar", "Concluir" }, cancellable: true);
            ToolProgress.Stage(0, "Medindo…");
            var ajustes = new List<Ajuste>();
            var atencao = new List<(Wall w, string motivo)>();
            for (int i = 0; i < paredes.Count; i++)
            {
                if (ToolProgress.Cancelled) { ToolProgress.End(); return Result.Cancelled; }
                if (i % 20 == 0) ToolProgress.Report(i, paredes.Count, "Parede " + paredes[i].Id.Value);
                var (aj, at) = Analisar(paredes[i]);
                if (aj != null) ajustes.Add(aj);
                else if (at != null) atencao.Add((paredes[i], at));
            }

            var feitos = new List<Ajuste>();
            var problemas = new List<string>();
            int recusados = 0, gruposRefeitos = 0;
            if (!fSo.Value && ajustes.Count > 0)
            {
                ToolProgress.Stage(1, $"Ajustando {ajustes.Count} parede(s)…");
                var soltos = ajustes.Where(a => a.Parede.GroupId == ElementId.InvalidElementId).ToList();
                Lote(soltos, feitos, ref recusados);

                foreach (var porTipo in ajustes.Where(a => a.Parede.GroupId != ElementId.InvalidElementId)
                                               .GroupBy(a => (Doc.GetElement(a.Parede.GroupId) as Group)?.GroupType?.Id ?? ElementId.InvalidElementId))
                {
                    if (ToolProgress.Cancelled) break;
                    GroupType tipo = Doc.GetElement(porTipo.Key) as GroupType;
                    string nome = tipo?.Name ?? "grupo";
                    var lista = porTipo.ToList();
                    // grouped walls never get attached to a slab outside the group
                    foreach (var a in lista.Where(a => a.Anexar).ToList())
                    {
                        lista.Remove(a);
                        atencao.Add((a.Parede, "em grupo e sob laje inclinada: ajuste em Editar grupo"));
                    }
                    if (lista.Count == 0) continue;
                    // 1) directly (Revit accepts it for a group with one copy)
                    if (Tentar(lista, feitos)) continue;
                    // 2) several copies: rebuild the type, if every copy needs the same changes
                    if (tipo == null) { recusados += lista.Count; continue; }
                    List<Group> copias = GruposSeguros.Copias(Doc, tipo);
                    var porCopia = copias.Select(g => (g, aj: g.GetMemberIds().Select(id => Doc.GetElement(id)).OfType<Wall>().Where(Basica)
                                                                 .Select(w => Analisar(w).aj).Where(x => x != null && !x.Anexar).ToList())).ToList();
                    var assinaturas = porCopia.Select(x => string.Join(";", x.aj.Select(a => a.Assinatura).OrderBy(z => z, StringComparer.Ordinal))).Distinct().ToList();
                    if (assinaturas.Count != 1)
                    {
                        problemas.Add($"Grupo “{nome}”: as cópias precisam de ajustes diferentes (estão sob lajes diferentes) — ajuste em Editar grupo. Nada foi desagrupado.");
                        recusados += lista.Count;
                        continue;
                    }
                    var (g0, aj0) = porCopia.OrderByDescending(x => x.aj.Count).First();
                    var feitosG = new List<Ajuste>();
                    string motivo = GruposSeguros.RefazerTipo(Doc, tipo, g0, membros => { foreach (Ajuste a in aj0) if (Aplicar(a)) feitosG.Add(a); }, out _);
                    if (motivo == null)
                    {
                        gruposRefeitos++;
                        feitos.AddRange(feitosG);
                        problemas.Add($"Grupo “{nome}”: tipo refeito com as paredes ajustadas — {copias.Count} cópia(s), todas agrupadas.");
                    }
                    else
                    {
                        problemas.Add($"Grupo “{nome}”: não ajustado ({motivo}). Nada foi desagrupado.");
                        recusados += lista.Count;
                    }
                }
            }
            ToolProgress.Stage(2, "Resumo…");
            ToolProgress.End();

            // ---- summary
            var linhas = new List<string>();
            int nAtr = ajustes.Count(a => a.Situacoes.Contains(SAtravessa)), nCur = ajustes.Count(a => a.Situacoes.Contains(SCurta)), nBase = ajustes.Count(a => a.Situacoes.Contains(SBase));
            linhas.Add($"{paredes.Count} parede(s) verificada(s): {nAtr} entrando na laje, {nCur} sem chegar nela, {nBase} com a base dentro da laje.");
            if (fSo.Value) linhas.Add("Só verificação: nada foi mudado.");
            else
            {
                linhas.Add($"{feitos.Count} parede(s) ajustada(s)" + (feitos.Count(a => a.Anexar) > 0 ? $" ({feitos.Count(a => a.Anexar)} anexadas à laje)" : "") + ".");
                if (gruposRefeitos > 0) linhas.Add($"{gruposRefeitos} tipo(s) de grupo refeito(s) com o mesmo nome — todas as cópias continuam agrupadas.");
                if (recusados > 0) problemas.Insert(0, $"{recusados} parede(s) não ajustadas para não quebrar grupos (ou recusadas pelo Revit).");
            }
            if (atencao.Count > 0)
            {
                problemas.Add($"{atencao.Count} parede(s) precisam de atenção:");
                foreach (var g in atencao.GroupBy(a => a.motivo)) problemas.Add($"   {g.Count()} — {g.Key}");
            }
            // select what the user asked for
            try
            {
                List<ElementId> ids = fFim.Index == 0 ? (fSo.Value ? ajustes : feitos).Select(a => a.Parede).Where(w => w.IsValidObject).Select(w => w.Id).ToList()
                                    : fFim.Index == 1 ? atencao.Select(a => a.w).Where(w => w.IsValidObject).Select(w => w.Id).ToList() : null;
                if (ids != null && ids.Count > 0) { UiDoc.Selection.SetElementIds(ids); linhas.Add($"{ids.Count} parede(s) selecionada(s)."); }
            }
            catch { }
            Show(string.Join("\n", linhas) + (problemas.Count > 0 ? "\n\nAtenção:\n• " + string.Join("\n• ", problemas.Take(30)) : ""),
                 problemas.Count > 0 ? ResultKind.Warning : ResultKind.Success);
            return Result.Succeeded;
        }

        private static bool Basica(Wall w)
        {
            try { return w.WallType != null && w.WallType.Kind == WallKind.Basic && w.Location is LocationCurve; } catch { return false; }
        }

        // ================================================================== measuring

        /// <summary>What a wall needs (or why it needs a human), cached per wall.</summary>
        private (Ajuste aj, string atencao) Analisar(Wall w)
        {
            if (_analise.TryGetValue(w.Id, out var pronto)) return pronto;
            var r = Medir(w);
            _analise[w.Id] = r;
            return r;
        }

        private (Ajuste, string) Medir(Wall w)
        {
            try { if (w.SketchId != ElementId.InvalidElementId) return (null, "perfil editado (ajuste à mão)"); } catch { }
            BoundingBoxXYZ bb = w.get_BoundingBox(null);
            if (bb == null) return (null, null);
            double z0 = bb.Min.Z, z1 = bb.Max.Z;
            Curve eixo = ((LocationCurve)w.Location).Curve;
            bool topoAnexado = (w.get_Parameter(BuiltInParameter.WALL_TOP_IS_ATTACHED)?.AsInteger() ?? 0) == 1;
            bool baseAnexada = (w.get_Parameter(BuiltInParameter.WALL_BOTTOM_IS_ATTACHED)?.AsInteger() ?? 0) == 1;

            // slabs around the wall
            double cima = Math.Max(_folga, 0) + 0.5;
            var cats = new List<BuiltInCategory> { BuiltInCategory.OST_Floors };
            if (_telhados) cats.Add(BuiltInCategory.OST_Roofs);
            var lajes = new FilteredElementCollector(Doc).WherePasses(new ElementMulticategoryFilter(cats)).WhereElementIsNotElementType()
                .WherePasses(new BoundingBoxIntersectsFilter(new Outline(new XYZ(bb.Min.X, bb.Min.Y, z0 - 3), new XYZ(bb.Max.X, bb.Max.Y, z1 + cima)))).ToElements();
            if (lajes.Count == 0) return (null, null);

            // vertical line × slab solids at 7 points along the wall
            const int N = 7;
            var acima = new List<(double zb, double zt, ElementId id)>();
            var abaixo = new List<double>();
            // a slab joined to the wall may have a hole where the wall is: also look just beside both faces
            double lado = w.Width / 2 + 30 / 304.8;
            for (int i = 0; i < N; i++)
            {
                double u = 0.05 + 0.9 * i / (N - 1);
                XYZ p = eixo.Evaluate(u, true);
                XYZ n = XYZ.Zero;
                try { XYZ d = eixo.ComputeDerivatives(u, true).BasisX; n = new XYZ(-d.Y, d.X, 0); n = n.GetLength() > 1e-9 ? n.Normalize() : XYZ.Zero; } catch { }
                var segs = new List<(double zb, double zt, ElementId id)>();
                foreach (XYZ q in n.IsZeroLength() ? new[] { p } : new[] { p, p + n * lado, p - n * lado })
                {
                    Line v = Line.CreateBound(new XYZ(q.X, q.Y, z0 - 3), new XYZ(q.X, q.Y, z1 + cima + 1));
                    foreach (Element laje in lajes)
                        foreach (Solid s in Solidos(laje))
                        {
                            try
                            {
                                SolidCurveIntersection sci = s.IntersectWithCurve(v, new SolidCurveIntersectionOptions { ResultType = SolidCurveIntersectionMode.CurveSegmentsInside });
                                for (int k = 0; k < sci.SegmentCount; k++)
                                {
                                    Curve c = sci.GetCurveSegment(k);
                                    double a = c.GetEndPoint(0).Z, b = c.GetEndPoint(1).Z;
                                    if (Math.Abs(a - b) > 1e-4) segs.Add((Math.Min(a, b), Math.Max(a, b), laje.Id));
                                }
                            }
                            catch { }
                        }
                }
                // the slab above: the lowest bottom higher than 5 cm over the wall base, within reach of the top
                var sobre = segs.Where(x => x.zb > z0 + 0.164 && x.zb <= z1 + _folga + _tol).OrderBy(x => x.zb).FirstOrDefault();
                if (sobre.id != null) acima.Add(sobre);
                // the slab under: the wall base is inside it
                var sob = segs.Where(x => x.zb < z0 - _tol && x.zt > z0 + _tol).OrderByDescending(x => x.zt).FirstOrDefault();
                if (sob.id != null) abaixo.Add(sob.zt);
            }

            var aj = new Ajuste { Parede = w };
            string atencao = null;
            // ---- top
            if (!topoAnexado && acima.Count > 0)
            {
                bool entra = acima.Any(x => z1 > x.zb + _tol);
                if (acima.Count < N - 2)
                {
                    if (entra && _atravessa) atencao = "a laje cobre só parte da parede";
                }
                else if (z1 > acima.Max(x => x.zt) + _multiAndar && _multiAndar > 0)
                {
                    // multi-storey wall: leave it
                }
                else
                {
                    double zmin = acima.Min(x => x.zb), zmax = acima.Max(x => x.zb);
                    ElementId laje = acima.GroupBy(x => x.id).OrderByDescending(g => g.Count()).First().Key;
                    bool inclinada = zmax - zmin > 5 / 304.8;
                    double d = zmin - z1;
                    bool precisa = (d < -_tol && _atravessa) || (d > _tol && _curtas && d <= _folga + _tol) || (inclinada && (_atravessa || _curtas));
                    if (precisa)
                    {
                        if (_anexar || inclinada)
                        {
                            if (_anexar) { aj.Anexar = true; aj.Laje = laje; }
                            else atencao = "laje inclinada ou com espessura variável sobre a parede (use “Anexar o topo à laje”)";
                        }
                        else aj.DeltaTopo = d;
                        if (aj.Anexar || Math.Abs(aj.DeltaTopo) > 0) aj.Situacoes.Add(d < 0 || inclinada ? SAtravessa : SCurta);
                    }
                }
            }
            // ---- base
            if (_base && !baseAnexada && abaixo.Count >= N - 2)
            {
                double zt = abaixo.Min(), ztM = abaixo.Max();
                if (ztM - zt > 5 / 304.8) atencao = atencao ?? "laje de baixo inclinada sob a parede";
                else if (zt - z0 > _tol)
                {
                    aj.DeltaBase = zt - z0;
                    aj.Situacoes.Add(SBase);
                }
            }
            return aj.Situacoes.Count > 0 ? (aj, null) : (null, atencao);
        }

        private List<Solid> Solidos(Element e)
        {
            if (_solidos.TryGetValue(e.Id, out var l)) return l;
            l = new List<Solid>();
            try
            {
                GeometryElement geo = e.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine });
                if (geo != null)
                    foreach (GeometryObject go in geo)
                    {
                        if (go is Solid s && s.Volume > 1e-9) l.Add(s);
                        else if (go is GeometryInstance gi)
                            foreach (GeometryObject g2 in gi.GetInstanceGeometry())
                                if (g2 is Solid s2 && s2.Volume > 1e-9) l.Add(s2);
                    }
            }
            catch (Exception ex) { Logger.Info("[Paredes nas Lajes] geometria de " + e.Id.Value + ": " + ex.Message); }
            _solidos[e.Id] = l;
            return l;
        }

        // ================================================================== changing

        private static MethodInfo _addAttachment;
        private static object _topo;
        private static bool _lido;

        /// <summary>Wall.AddAttachment(target, AttachmentLocation.Top) (Revit 2023+), by reflection.</summary>
        private static bool AnexarTopo(Wall w, ElementId laje)
        {
            if (!_lido)
            {
                _lido = true;
                try
                {
                    Type loc = typeof(Wall).Assembly.GetType("Autodesk.Revit.DB.AttachmentLocation");
                    if (loc != null)
                    {
                        _addAttachment = typeof(Wall).GetMethod("AddAttachment", new[] { typeof(ElementId), loc });
                        _topo = Enum.Parse(loc, "Top");
                    }
                }
                catch { }
            }
            if (_addAttachment == null) return false;
            _addAttachment.Invoke(w, new[] { (object)laje, _topo });
            return true;
        }

        private bool Aplicar(Ajuste a)
        {
            Wall w = a.Parede;
            if (!w.IsValidObject) return false;
            ElementId topo = w.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.AsElementId() ?? ElementId.InvalidElementId;
            bool solta = topo == ElementId.InvalidElementId;
            if (Math.Abs(a.DeltaBase) > 0)
            {
                Parameter pb = w.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET);
                if (pb == null || pb.IsReadOnly) return false;
                pb.Set(pb.AsDouble() + a.DeltaBase);
                if (solta)
                {
                    // keep the top where it was (the height is measured from the base)
                    Parameter ph = w.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM);
                    if (ph != null && !ph.IsReadOnly) ph.Set(ph.AsDouble() - a.DeltaBase);
                }
            }
            if (a.Anexar)
            {
                if (!AnexarTopo(w, a.Laje)) throw new InvalidOperationException("esta versão do Revit não anexa paredes pela API");
                return true;
            }
            if (Math.Abs(a.DeltaTopo) > 0)
            {
                Parameter pt = solta ? w.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM) : w.get_Parameter(BuiltInParameter.WALL_TOP_OFFSET);
                if (pt == null || pt.IsReadOnly) return false;
                double novo = pt.AsDouble() + a.DeltaTopo;
                if (solta && novo < 0.1) return false;
                pt.Set(novo);
            }
            return true;
        }

        /// <summary>All in one transaction; false (nothing changed) if Revit raised an error.</summary>
        private bool Tentar(List<Ajuste> lista, List<Ajuste> feitos)
        {
            var ok = new List<Ajuste>();
            var guarda = new GuardaGrupos();
            try
            {
                using (var t = new Transaction(Doc, "JA Tools - " + Title))
                {
                    t.Start();
                    guarda.Anexar(t);
                    foreach (Ajuste a in lista)
                    {
                        try { if (Aplicar(a)) ok.Add(a); }
                        catch (Exception ex) { Logger.Info("[Paredes nas Lajes] " + a.Parede.Id.Value + ": " + ex.Message); }
                    }
                    if (t.Commit() != TransactionStatus.Committed || guarda.Desfez) return false;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("[Paredes nas Lajes] " + ex.Message);
                return false;
            }
            feitos.AddRange(ok);
            return true;
        }

        /// <summary>Batches; a batch Revit refuses is split in halves, so only the refused walls stay out.</summary>
        private void Lote(List<Ajuste> lista, List<Ajuste> feitos, ref int recusados)
        {
            const int Tamanho = 200;
            for (int i = 0; i < lista.Count; i += Tamanho)
            {
                if (ToolProgress.Cancelled) return;
                ToolProgress.Report(i, lista.Count, "Ajustando…");
                Dividir(lista.Skip(i).Take(Tamanho).ToList(), feitos, ref recusados);
            }
        }

        private void Dividir(List<Ajuste> lista, List<Ajuste> feitos, ref int recusados)
        {
            if (lista.Count == 0 || Tentar(lista, feitos)) return;
            if (lista.Count == 1) { recusados++; return; }
            int meio = lista.Count / 2;
            Dividir(lista.Take(meio).ToList(), feitos, ref recusados);
            Dividir(lista.Skip(meio).ToList(), feitos, ref recusados);
        }
    }
}
