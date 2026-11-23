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
    /// Ajustar Paredes Cebola: finish walls (thin walls, or a type name with a keyword) that are a little
    /// away from their wall, a little inside it or a little turned are put onto its face; layers on layers
    /// (tile on plaster) inside out; corners between finish walls closed (centre-line crossing when Revit
    /// joins the ends, a butt joint otherwise); small gaps / overlaps between pieces of the same layer
    /// closed; optionally base and top as the wall's; then a locked alignment of the touching faces (moving
    /// the wall moves the finish) and the join with every wall it touches (the wall cuts its finish).
    /// Walls in groups or pinned are left alone and listed. Straight walls only.
    /// The plan geometry was tested case by case in a prototype (gaps, overlaps, flipped walls, location
    /// lines, asymmetric cores, layers, equal layers, thin partitions, two walls, L / oblique / T corners,
    /// butt joints, seams, turned finishes).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class AjustarParedesCebolaCommand : ToolCommand
    {
        public override string Title => "Ajustar Paredes Cebola";

        /// <summary>Four passes in batches: one Ctrl+Z undoes them all.</summary>
        protected override bool UmDesfazer => true;

        private const double Mm = 1 / 304.8;
        private const double Empilha = 150 * Mm;   // layers on layers: how far from the wall a finish may still belong to it

        /// <summary>A straight basic wall read once: centre line in plan (from end 0 to end 1), Orientation, width, heights.</summary>
        private sealed class Info
        {
            public Wall W;
            public XYZ A, B, D, N;
            public double L, Larg, Z0, Z1;
            public bool Revest;
            public XYZ Meio => (A + B) / 2;
        }

        /// <summary>A finish wall, the wall (core) whose side it is on, and the wall or layer it sits on.</summary>
        private sealed class Par
        {
            public Info Rev, Core, Base;
            public int Lado;             // the finish is on the +Orientation (1) or −Orientation (−1) side of the core
            public double E;             // its inner face from the core face (ft): + away, − inside
            public double F;             // where that face must be (0 = on the core, else on the layer below)
            public double Fc;            // where it ends up (F when fixed, E when out of reach)
            public int Prof = 1;
            public double Folga => E - F;
            public string Nome => $"revestimento {Rev.W.Id.Value} (parede {Base.W.Id.Value})";
        }

        private sealed class Ponta
        {
            public Wall W1, W2;
            public int F1, F2;
            public XYZ X1, X2;           // where each centre line must end (plan)
        }

        protected override Result Run()
        {
            var sel = UiDoc.Selection.GetElementIds().Select(id => Doc.GetElement(id)).Where(e => e != null).ToList();
            var dlg = new OptionsDialog(Title, "AjustarCebola", "Ajustar");
            var fScope = dlg.Choice("scope", "Onde", new[] { $"Selecionados ({sel.Count})", "Vista ativa", "Todo o projeto" }, 1,
                "Selecionar a parede inclui os revestimentos dela; selecionar um grupo inclui os membros.");
            fScope.Index = sel.Count > 0 ? 0 : fScope.Index == 0 ? 1 : fScope.Index;
            dlg.Section("O que é revestimento");
            var fEsp = dlg.Number("esp", "Paredes com espessura até (mm)", 60, 3, 300, "Reboco, cerâmica, porcelanato, pedra, gesso… Uma parede fina sem nada ao lado (divisória) não é tratada como revestimento.");
            var fPal = dlg.Text("palavras", "…ou cujo tipo tem no nome", "revest, reboco, acabamento, cerâmica, ceramica, porcelanato, pastilha, cebola",
                "Separe por vírgula. Vazio = só pela espessura.");
            var fTol = dlg.Number("tol", "Corrigir os que estão afastados ou entrando na parede até (mm)", 30, 1, 200,
                "Mais que isso não é tratado como revestimento daquela parede.");
            dlg.Section("O que fazer");
            var fCantos = dlg.Check("cantos", "Fechar os cantos e as emendas entre revestimentos", true,
                "Cantos: as pontas vão até o cruzamento (ou topo a topo, se a união das pontas estiver desligada). Emendas: frestas ou sobreposições pequenas entre peças da mesma camada.");
            var fTolCanto = dlg.Number("tolCanto", "Pontas a até (mm)", 100, 5, 500);
            fTolCanto.EnabledWhen(fCantos);
            var fAlturas = dlg.Check("alturas", "Igualar a base e o topo do revestimento aos da parede", false, "Desmarcado: alturas como estão (ex.: revestimento até 1,20 m).");
            var fTravar = dlg.Check("travar", "Prender na parede (alinhar e cadear as faces)", true,
                "Mover a parede leva o revestimento junto. O cadeado aparece ao selecionar o revestimento numa planta do nível.");
            var fUnir = dlg.Check("unir", "Unir a geometria (a parede corta o revestimento)", true, "Une com todas as paredes em que o revestimento encosta; portas e janelas passam a cortar o revestimento.");
            dlg.Section("Conferir");
            var fPrevia = dlg.Check("previa", "Mostrar o que será ajustado antes de aplicar", true);
            var fSel = dlg.Check("selecionar", "Deixar selecionados os revestimentos ajustados", true);
            dlg.Note("Paredes em grupos ou fixadas (pin) não são mexidas. Paredes curvas ficam de fora.");
            if (!dlg.Show(Owner)) return Result.Cancelled;

            double tol = fTol.Value * Mm, espMax = fEsp.Value * Mm, tolCanto = fTolCanto.Value * Mm;
            string[] palavras = (fPal.Value ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0).ToArray();

            ToolProgress.Setup("Ajustando paredes cebola", new[] { "Procurar", "Encostar", "Cantos e alturas", "Prender e unir", "Concluir" }, cancellable: true);
            ToolProgress.Stage(0, "Lendo as paredes…");

            // ---- every straight basic wall, read once (the layers need the whole project)
            var todas = new List<Info>();
            int curvos = 0;
            foreach (Wall w in new FilteredElementCollector(Doc).OfClass(typeof(Wall)).Cast<Wall>())
            {
                Info i = Ler(w, espMax, palavras);
                if (i != null) todas.Add(i);
                else if (EhCurvoFino(w, espMax)) curvos++;
            }

            // ---- the scope: selected (groups = members), visible in the view, or all
            HashSet<ElementId> escopo;
            if (fScope.Index == 0)
            {
                escopo = new HashSet<ElementId>();
                foreach (Element e in sel)
                {
                    if (e is Group g) foreach (ElementId m in g.GetMemberIds()) escopo.Add(m);
                    else escopo.Add(e.Id);
                }
            }
            else if (fScope.Index == 1) escopo = new HashSet<ElementId>(new FilteredElementCollector(Doc, Doc.ActiveView.Id).OfClass(typeof(Wall)).ToElementIds());
            else escopo = null;

            ToolProgress.Pulse("Procurando a parede de cada revestimento…");
            List<Par> pares = Emparelhar(todas, tol);
            if (ToolProgress.Cancelled) { ToolProgress.End(); return Result.Cancelled; }
            bool NoEscopo(Par p) => escopo == null || escopo.Contains(p.Rev.W.Id) || escopo.Contains(p.Base.W.Id) || escopo.Contains(p.Core.W.Id);
            int revsNoEscopo = todas.Count(i => i.Revest && (escopo == null || escopo.Contains(i.W.Id)));

            var problemas = new List<string>();
            bool Livre(Wall w)
            {
                if (w.GroupId != ElementId.InvalidElementId) { Anotar(problemas, $"Revestimento {w.Id.Value} está num grupo: não mexido."); return false; }
                if (w.Pinned) { Anotar(problemas, $"Revestimento {w.Id.Value} está fixado (pin): não mexido."); return false; }
                return true;
            }
            var lista = pares.Where(NoEscopo).Where(p => Livre(p.Rev.W)).OrderBy(p => p.Prof).ToList();
            var mover = lista.Where(p => Math.Abs(p.Folga) > 0.2 * Mm || Angulo(p.Rev, p.Core) > 1e-5).ToList();
            int semParede = Math.Max(0, revsNoEscopo - pares.Count(p => escopo == null || escopo.Contains(p.Rev.W.Id)));

            if (lista.Count == 0)
            {
                ToolProgress.End();
                Show($"Nenhum revestimento junto de uma parede no escopo (espessura até {fEsp.Value:0} mm ou palavras do tipo; até {fTol.Value:0} mm da face)." +
                     (problemas.Count > 0 ? "\n\n• " + string.Join("\n• ", problemas.Take(15)) : ""), ResultKind.Info);
                return Result.Succeeded;
            }
            if (fPrevia.Value)
            {
                ToolProgress.Hide();
                var conf = new OptionsDialog(Title, "AjustarCebolaConfirmar", "Ajustar");
                conf.Section($"{lista.Count} revestimento(s) com a sua parede");
                int fora = mover.Count(p => p.Folga > 0.2 * Mm), dentro = mover.Count(p => p.Folga < -0.2 * Mm), tortos = mover.Count(p => Angulo(p.Rev, p.Core) > 1e-5);
                conf.Note($"Encostar: {mover.Count} — {fora} afastado(s), {dentro} entrando na parede" + (tortos > 0 ? $", {tortos} torto(s)" : "") +
                          (mover.Count > 0 ? $"; o maior ajuste é de {mover.Max(p => Math.Abs(p.Folga)) / Mm:0.#} mm." : "."));
                foreach (Par p in mover.OrderByDescending(p => Math.Abs(p.Folga)).Take(5))
                    conf.Note($"   • {p.Nome}: {Math.Abs(p.Folga) / Mm:0.#} mm {(p.Folga > 0 ? "afastado" : "para dentro")}" + (p.Prof > 1 ? " (sobre outra camada)" : ""));
                if (lista.Count - mover.Count > 0) conf.Note($"{lista.Count - mover.Count} já encostado(s).");
                int camadas = lista.Count(p => p.Prof > 1);
                if (camadas > 0) conf.Note($"{camadas} sobre outro revestimento (ex.: cerâmica sobre reboco): ajustados de dentro para fora.");
                if (semParede > 0) conf.Note($"{semParede} revestimento(s) sem parede ao lado dentro da tolerância: ficam como estão.");
                conf.Note("Depois: " + string.Join(", ", new[] { fCantos.Value ? "fechar cantos e emendas" : null, fAlturas.Value ? "igualar alturas" : null,
                                                                  fTravar.Value ? "prender na parede" : null, fUnir.Value ? "unir" : null }.Where(x => x != null)) + ".");
                if (problemas.Count > 0) conf.Note(problemas.Count + " revestimento(s) em grupo ou fixado(s) ficam de fora.");
                if (!conf.Show(Owner)) { ToolProgress.End(); return Result.Cancelled; }
            }

            var mudados = new HashSet<ElementId>();

            // ---- 1. parallel and onto the face (F was worked out for every layer, so the order only matters for the record)
            ToolProgress.Stage(1, $"{mover.Count} revestimento(s)…");
            int movidos = 0, girados = 0;
            double maior = 0;
            Lote(mover, "encostar revestimentos", p =>
            {
                Info c = Ler(p.Core.W, espMax, palavras) ?? p.Core, r = Ler(p.Rev.W, espMax, palavras);
                if (r == null) return "!a parede mudou";
                double ang = AnguloAssinado(r, c);
                bool girou = false;
                if (Math.Abs(ang) > 1e-5)
                {
                    XYZ m = new XYZ(r.Meio.X, r.Meio.Y, r.Z0);
                    ElementTransformUtils.RotateElement(Doc, r.W.Id, Line.CreateBound(m, m + XYZ.BasisZ), ang);
                    r = Ler(p.Rev.W, espMax, palavras);
                    if (r == null) return "!a parede mudou";
                    girou = true;
                }
                double sd = (r.Meio - c.A).DotProduct(c.N);
                double e = Math.Abs(sd) - (c.Larg + r.Larg) / 2;
                double delta = p.F - e;
                if (Math.Abs(delta) >= 0.05 * Mm) ElementTransformUtils.MoveElement(Doc, r.W.Id, c.N * (p.Lado * delta));
                else if (!girou) return null;
                return girou ? "girou" : "ok";
            }, (p, res) =>
            {
                if (res == "ok" || res == "girou")
                {
                    movidos++; if (res == "girou") girados++;
                    maior = Math.Max(maior, Math.Abs(p.Folga));
                    mudados.Add(p.Rev.W.Id);
                }
                else if (res != null) Anotar(problemas, p.Nome + ": não movido (" + res.TrimStart('!') + ")");
            });

            // ---- 2. corners, seams and heights
            ToolProgress.Stage(2, "Cantos, emendas e alturas…");
            int cantos = 0, emendas = 0, alturas = 0;
            if (fCantos.Value)
            {
                var livres = lista.Where(p => p.Rev.W.IsValidObject).ToList();
                List<Ponta> pontas = Cantos(livres.Select(p => p.Rev.W).Distinct().ToList(), tolCanto, espMax, palavras);
                Lote(pontas, "fechar cantos", c => (Esticar(c.W1, c.F1, c.X1) | Esticar(c.W2, c.F2, c.X2)) ? "ok" : null, (c, res) =>
                {
                    if (res == "ok") { cantos++; mudados.Add(c.W1.Id); mudados.Add(c.W2.Id); }
                    else if (res != null) Anotar(problemas, $"Canto dos revestimentos {c.W1.Id.Value} e {c.W2.Id.Value}: {res.TrimStart('!')}");
                });
                List<Ponta> juntas = Emendas(livres, tolCanto, espMax, palavras);
                Lote(juntas, "fechar emendas", c => (Esticar(c.W1, c.F1, c.X1) | Esticar(c.W2, c.F2, c.X2)) ? "ok" : null, (c, res) =>
                {
                    if (res == "ok") { emendas++; mudados.Add(c.W1.Id); mudados.Add(c.W2.Id); }
                    else if (res != null) Anotar(problemas, $"Emenda dos revestimentos {c.W1.Id.Value} e {c.W2.Id.Value}: {res.TrimStart('!')}");
                });
            }
            if (fAlturas.Value)
                Lote(lista, "igualar alturas", p => IgualarAlturas(p.Core.W, p.Rev.W) ? "ok" : null,
                     (p, res) => { if (res == "ok") { alturas++; mudados.Add(p.Rev.W.Id); } else if (res != null) Anotar(problemas, p.Nome + ": alturas (" + res.TrimStart('!') + ")"); });

            // ---- 3. locked alignment of the touching faces, then the join with every wall touched
            ToolProgress.Stage(3, "Prendendo e unindo…");
            int presos = 0, jaPresos = 0, unidos = 0, jaUnidos = 0;
            if (fTravar.Value)
            {
                var travados = PresosExistentes();
                var vistas = new Dictionary<ElementId, List<View>>();
                Lote(lista, "prender revestimentos", p =>
                {
                    long a = p.Base.W.Id.Value, b = p.Rev.W.Id.Value;
                    if (travados.Contains((Math.Min(a, b), Math.Max(a, b)))) return "ja";
                    Info bi = Ler(p.Base.W, espMax, palavras), ri = Ler(p.Rev.W, espMax, palavras);
                    if (bi == null || ri == null) return "!parede não é mais reta";
                    XYZ n = p.Core.N * p.Lado;                                    // from the wall to the finish
                    if (!FacesDeContato(bi, ri, n, out Reference fb, out Reference fr)) return "!face de contato não encontrada";
                    List<View> vs = VistasDoNivel(p.Rev.W.LevelId, vistas);
                    if (vs.Count == 0) return "!sem planta do nível para criar o cadeado";
                    string erro = null;
                    foreach (View v in vs)
                    {
                        try { Doc.Create.NewAlignment(v, fb, fr); return "ok"; }
                        catch (Exception ex) { erro = ex.Message; }
                    }
                    return "!" + erro;
                }, (p, res) =>
                {
                    if (res == "ok") { presos++; mudados.Add(p.Rev.W.Id); }
                    else if (res == "ja") jaPresos++;
                    else if (res != null) Anotar(problemas, p.Nome + ": não preso (" + res.TrimStart('!') + ")");
                });
            }
            if (fUnir.Value)
            {
                var uniao = Contatos(lista, todas, espMax, palavras);
                Lote(uniao, "unir revestimentos", u =>
                {
                    Wall b = u.corta, r = u.cortado;
                    if (JoinGeometryUtils.AreElementsJoined(Doc, b, r))
                    {
                        if (!JoinGeometryUtils.IsCuttingElementInJoin(Doc, b, r)) JoinGeometryUtils.SwitchJoinOrder(Doc, b, r);
                        return "ja";
                    }
                    JoinGeometryUtils.JoinGeometry(Doc, b, r);
                    if (!JoinGeometryUtils.IsCuttingElementInJoin(Doc, b, r)) JoinGeometryUtils.SwitchJoinOrder(Doc, b, r);
                    return "ok";
                }, (u, res) =>
                {
                    if (res == "ok") { unidos++; mudados.Add(u.cortado.Id); }
                    else if (res == "ja") jaUnidos++;
                    else if (res != null) Anotar(problemas, $"Revestimento {u.cortado.Id.Value} × parede {u.corta.Id.Value}: não unidos ({res.TrimStart('!')})");
                });
            }

            ToolProgress.Stage(4, "Resumo…");
            ToolProgress.End();
            if (fSel.Value && mudados.Count > 0)
                try { UiDoc.Selection.SetElementIds(mudados.Where(id => Doc.GetElement(id) != null).ToList()); } catch { }

            var linhas = new List<string>
            {
                $"{lista.Count} revestimento(s) com a sua parede" + (lista.Any(p => p.Prof > 1) ? $" ({lista.Count(p => p.Prof > 1)} sobre outra camada)" : "") + ".",
                $"Encostados na face: {movidos}" + (girados > 0 ? $" ({girados} endireitado(s))" : "") + (movidos > 0 ? $"; maior ajuste {maior / Mm:0.#} mm" : "") +
                $"; {lista.Count - mover.Count} já estavam encostados."
            };
            if (fCantos.Value) linhas.Add($"Cantos fechados: {cantos}; emendas fechadas: {emendas}.");
            if (fAlturas.Value) linhas.Add($"Alturas igualadas: {alturas}.");
            if (fTravar.Value) linhas.Add($"Presos na parede (alinhamento com cadeado): {presos}" + (jaPresos > 0 ? $"; {jaPresos} já estavam presos" : "") + ".");
            if (fUnir.Value) linhas.Add($"Uniões novas: {unidos}" + (jaUnidos > 0 ? $"; {jaUnidos} já existiam" : "") + ".");
            if (semParede > 0) linhas.Add($"{semParede} revestimento(s) sem parede ao lado (até {fTol.Value:0} mm): ficaram como estavam.");
            if (curvos > 0) linhas.Add($"{curvos} parede(s) curva(s) fina(s) no projeto não são tratadas.");
            if (fSel.Value && mudados.Count > 0) linhas.Add("Os revestimentos ajustados ficaram selecionados.");
            string texto = string.Join("\n", linhas) + (problemas.Count > 0 ? "\n\nAtenção:\n• " + string.Join("\n• ", problemas.Take(25)) + (problemas.Count > 25 ? "\n• …" : "") : "");
            Show(texto, problemas.Count > 0 ? ResultKind.Warning : ResultKind.Success);
            return Result.Succeeded;
        }

        // ================================================================== reading

        private static Info Ler(Wall w, double espMax, string[] palavras)
        {
            try
            {
                if (w == null || !w.IsValidObject || w.WallType == null || w.WallType.Kind != WallKind.Basic) return null;
                if (!((w.Location as LocationCurve)?.Curve is Line ln)) return null;
                XYZ p0 = Plano(ln.GetEndPoint(0)), p1 = Plano(ln.GetEndPoint(1));
                double L = p0.DistanceTo(p1);
                if (L < 1e-6) return null;
                XYZ ori = Plano(w.Orientation);
                if (ori.GetLength() < 1e-9) return null;
                ori = ori.Normalize();
                XYZ desl = ori * DesvioDoCentro(w);
                BoundingBoxXYZ bb = w.get_BoundingBox(null);
                string nome = (w.WallType.Name ?? "").ToLowerInvariant();
                return new Info
                {
                    W = w, A = p0 + desl, B = p1 + desl, D = (p1 - p0) / L, N = ori, L = L, Larg = w.Width,
                    Z0 = bb?.Min.Z ?? ln.GetEndPoint(0).Z, Z1 = bb?.Max.Z ?? ln.GetEndPoint(0).Z + 1,
                    Revest = w.Width <= espMax + 1e-9 || palavras.Any(nome.Contains)
                };
            }
            catch { return null; }
        }

        private static bool EhCurvoFino(Wall w, double espMax)
        {
            try { return w.WallType?.Kind == WallKind.Basic && (w.Location as LocationCurve)?.Curve is Arc && w.Width <= espMax + 1e-9; } catch { return false; }
        }

        /// <summary>From the location line to the wall's centre, along Orientation (exterior side positive).</summary>
        private static double DesvioDoCentro(Wall w) => ParedesCebola.DesvioDoCentro(w);

        private static XYZ Plano(XYZ v) => new XYZ(v.X, v.Y, 0);
        private static double Cruz(XYZ a, XYZ b) => a.X * b.Y - a.Y * b.X;
        private static bool Paralelas(Info a, Info b, double lim = 0.0175) => Math.Abs(Cruz(a.D, b.D)) <= lim;
        private static double SobreAltura(Info a, Info b) => Math.Min(a.Z1, b.Z1) - Math.Max(a.Z0, b.Z0);

        /// <summary>How much of r runs along b (ft).</summary>
        private static double SobreAoLongo(Info b, Info r)
        {
            double t0 = (r.A - b.A).DotProduct(b.D), t1 = (r.B - b.A).DotProduct(b.D);
            return Math.Min(Math.Max(t0, t1), b.L) - Math.Max(Math.Min(t0, t1), 0);
        }

        /// <summary>The turn that makes r parallel to c (radians, counter-clockwise; drawn either way).</summary>
        private static double AnguloAssinado(Info r, Info c)
        {
            double ang = Math.Atan2(Cruz(r.D, c.D), r.D.DotProduct(c.D));
            if (Math.Abs(ang) > Math.PI / 2) ang -= Math.Sign(ang) * Math.PI;
            return ang;
        }
        private static double Angulo(Info r, Info c) => Math.Abs(AnguloAssinado(r, c));

        private static XYZ Cruzar(XYZ p, XYZ d, XYZ q, XYZ e)
        {
            double c = Cruz(d, e);
            if (Math.Abs(c) < 1e-9) return null;
            double t = Cruz(q - p, e) / c;
            return p + d * t;
        }

        private static void Anotar(List<string> l, string s) { if (!l.Contains(s)) l.Add(s); }

        // ================================================================== pairing

        /// <summary>
        /// The core of every finish (the nearest wall that is not a finish, on its side, faces up to tol inside or
        /// tol + 15 cm away); thin walls with nothing to sit on and no thicker finish next to them are walls
        /// (partitions); then, per core side, the layers inside out: each one onto the core or onto the outer
        /// face of the nearest layer below it.
        /// </summary>
        private static List<Par> Emparelhar(List<Info> todas, double tol)
        {
            Dictionary<ElementId, Par> Achar(List<Info> revs, List<Info> nucleos)
            {
                var achados = new Dictionary<ElementId, Par>();
                foreach (Info r in revs)
                {
                    Par melhor = null;
                    foreach (Info b in nucleos)
                    {
                        if (b.W.Id == r.W.Id || !Paralelas(b, r) || SobreAltura(b, r) < 30 * Mm) continue;
                        double sd = (r.Meio - b.A).DotProduct(b.N);
                        if (Math.Abs(sd) < 1 * Mm) continue;
                        double e = Math.Abs(sd) - (b.Larg + r.Larg) / 2;
                        if (e < -tol || e > tol + Empilha) continue;
                        if (SobreAoLongo(b, r) < 50 * Mm) continue;
                        bool ganha = melhor == null || Math.Abs(e) < Math.Abs(melhor.E) - 1 * Mm
                                     || (Math.Abs(Math.Abs(e) - Math.Abs(melhor.E)) <= 1 * Mm && SobreAoLongo(b, r) > SobreAoLongo(melhor.Core, r));
                        if (ganha) melhor = new Par { Rev = r, Core = b, Base = b, Lado = sd > 0 ? 1 : -1, E = e };
                    }
                    if (melhor != null) achados[r.W.Id] = melhor;
                }
                return achados;
            }
            var nucleos0 = todas.Where(i => !i.Revest).ToList();
            var revs0 = todas.Where(i => i.Revest).ToList();
            var achou = Achar(revs0, nucleos0);
            var sem = revs0.Where(r => !achou.ContainsKey(r.W.Id)).ToList();
            bool VizinhoMaisGrosso(Info r) => sem.Any(o => o.W.Id != r.W.Id && o.Larg > r.Larg && Paralelas(o, r) && SobreAoLongo(o, r) >= 50 * Mm && SobreAltura(o, r) >= 30 * Mm
                                                         && Math.Abs(Math.Abs((r.Meio - o.A).DotProduct(o.N)) - (o.Larg + r.Larg) / 2) <= tol);
            var promovidos = sem.Where(r => !VizinhoMaisGrosso(r)).ToList();
            if (promovidos.Count > 0)
            {
                foreach (Info r in promovidos) r.Revest = false;
                achou = Achar(todas.Where(i => i.Revest).ToList(), todas.Where(i => !i.Revest).ToList());
            }

            var res = new List<Par>();
            foreach (var grupo in achou.Values.GroupBy(p => (p.Core.W.Id.Value, p.Lado)))
            {
                // inside out; two layers at (almost) the same place: the thicker one first (plaster before tile)
                var l = grupo.OrderBy(p => p.E).ThenByDescending(p => p.Rev.Larg).ThenBy(p => p.Rev.W.Id.Value).ToList();
                for (int i = 0; i + 1 < l.Count; i++)
                    if (Math.Abs(l[i].E - l[i + 1].E) < 1 * Mm && l[i].Rev.Larg < l[i + 1].Rev.Larg) { Par x = l[i]; l[i] = l[i + 1]; l[i + 1] = x; }
                var postos = new List<Par>();
                foreach (Par p in l)
                {
                    double F = 0; Info baseW = p.Core; int prof = 1;
                    foreach (Par q in postos)
                    {
                        if (SobreAoLongo(q.Rev, p.Rev) < 50 * Mm || SobreAltura(q.Rev, p.Rev) < 30 * Mm) continue;
                        double fq = q.Fc + q.Rev.Larg;
                        if (Math.Abs(p.E - fq) < Math.Abs(p.E - F)) { F = fq; baseW = q.Rev; prof = q.Prof + 1; }
                    }
                    p.F = F;
                    if (Math.Abs(p.E - F) <= tol) { p.Base = baseW; p.Prof = prof; p.Fc = F; res.Add(p); }
                    else p.Fc = p.E;          // out of reach: stays, may still carry the next layer
                    postos.Add(p);
                }
            }
            return res;
        }

        // ================================================================== corners and seams

        /// <summary>
        /// Ends of two finish walls (not parallel, overlapping in height) that almost meet. Both ends allow
        /// joins: both centre lines to their crossing (Revit cleans the corner). Otherwise a butt joint: the
        /// longer one to the far face of the other, the other to its near face.
        /// </summary>
        private static List<Ponta> Cantos(List<Wall> paredes, double tol, double espMax, string[] palavras)
        {
            var infos = paredes.Select(w => Ler(w, espMax, palavras)).Where(i => i != null).ToList();
            var res = new List<Ponta>();
            var usados = new HashSet<(long, int)>();
            for (int i = 0; i < infos.Count; i++)
                for (int j = i + 1; j < infos.Count; j++)
                {
                    Info a = infos[i], b = infos[j];
                    if (Math.Abs(Cruz(a.D, b.D)) < Math.Sin(10 * Math.PI / 180) || SobreAltura(a, b) < 30 * Mm) continue;
                    XYZ x = Cruzar(a.A, a.D, b.A, b.D);
                    if (x == null) continue;
                    double ta = (x - a.A).DotProduct(a.D), tb = (x - b.A).DotProduct(b.D);
                    int fa = Math.Abs(ta) < Math.Abs(ta - a.L) ? 0 : 1, fb = Math.Abs(tb) < Math.Abs(tb - b.L) ? 0 : 1;
                    XYZ ea = fa == 0 ? a.A : a.B, eb = fb == 0 ? b.A : b.B;
                    if (ea.DistanceTo(x) > tol || eb.DistanceTo(x) > tol) continue;
                    if ((fa == 0 ? a.L - ta : ta) < a.L / 2 || (fb == 0 ? b.L - tb : tb) < b.L / 2) continue;   // never more than half the wall
                    if (usados.Contains((a.W.Id.Value, fa)) || usados.Contains((b.W.Id.Value, fb))) continue;
                    XYZ xa = x, xb = x;
                    bool juntam = false;
                    try { juntam = WallUtils.IsWallJoinAllowedAtEnd(a.W, fa) && WallUtils.IsWallJoinAllowedAtEnd(b.W, fb); } catch { }
                    if (!juntam)
                    {
                        bool aPassa = a.L >= b.L;
                        Info T = aPassa ? a : b, U = aPassa ? b : a;
                        int fT = aPassa ? fa : fb, fU = aPassa ? fb : fa;
                        XYZ corpoT = (fT == 0 ? T.B : T.A) - x, corpoU = (fU == 0 ? U.B : U.A) - x;
                        XYZ nU = new XYZ(-U.D.Y, U.D.X, 0), nT = new XYZ(-T.D.Y, T.D.X, 0);
                        double sU = nU.DotProduct(corpoT) < 0 ? 1 : -1;           // the face of U away from T's body
                        double sT = nT.DotProduct(corpoU) > 0 ? 1 : -1;           // the face of T towards U's body
                        XYZ alvoT = Cruzar(T.A, T.D, U.A + nU * (sU * U.Larg / 2), U.D);
                        XYZ alvoU = Cruzar(U.A, U.D, T.A + nT * (sT * T.Larg / 2), T.D);
                        if (alvoT == null || alvoU == null) continue;
                        xa = aPassa ? alvoT : alvoU; xb = aPassa ? alvoU : alvoT;
                    }
                    if (ea.DistanceTo(xa) < 0.1 * Mm && eb.DistanceTo(xb) < 0.1 * Mm) continue;   // already closed
                    usados.Add((a.W.Id.Value, fa)); usados.Add((b.W.Id.Value, fb));
                    res.Add(new Ponta { W1 = a.W, F1 = fa, X1 = xa, W2 = b.W, F2 = fb, X2 = xb });
                }
            return res;
        }

        /// <summary>Pieces of the same layer one after the other (same core side, same face): a small gap or overlap closed at the middle.</summary>
        private static List<Ponta> Emendas(List<Par> pares, double tol, double espMax, string[] palavras)
        {
            var res = new List<Ponta>();
            foreach (var g in pares.GroupBy(p => (p.Core.W.Id.Value, p.Lado, Math.Round(p.F / Mm * 2))))
            {
                if (g.Count() < 2) continue;
                Info core = g.First().Core;
                var itens = new List<(double t0, double t1, Info r, int fimIni)>();
                foreach (Par p in g)
                {
                    Info r = Ler(p.Rev.W, espMax, palavras);
                    if (r == null) continue;
                    double a0 = (r.A - core.A).DotProduct(core.D), a1 = (r.B - core.A).DotProduct(core.D);
                    itens.Add((Math.Min(a0, a1), Math.Max(a0, a1), r, a0 < a1 ? 0 : 1));
                }
                itens.Sort((x, y) => x.t0.CompareTo(y.t0));
                var usados = new HashSet<(long, int)>();
                for (int i = 0; i + 1 < itens.Count; i++)
                {
                    var a = itens[i]; var b = itens[i + 1];
                    double gap = b.t0 - a.t1;
                    if (Math.Abs(gap) <= 0.1 * Mm || Math.Abs(gap) > tol || b.t1 <= a.t1) continue;
                    double alt = SobreAltura(a.r, b.r) / Math.Max(1e-9, Math.Max(a.r.Z1 - a.r.Z0, b.r.Z1 - b.r.Z0));
                    if (alt < 0.9) continue;                                    // a tile band next to a full-height piece: leave
                    int fimA = 1 - a.fimIni, fimB = b.fimIni;
                    if (!usados.Add((a.r.W.Id.Value, fimA)) || !usados.Add((b.r.W.Id.Value, fimB))) continue;
                    XYZ alvo = core.A + core.D * ((a.t1 + b.t0) / 2);
                    XYZ xa = Cruzar(a.r.A, a.r.D, alvo, core.N), xb = Cruzar(b.r.A, b.r.D, alvo, core.N);
                    if (xa == null || xb == null) continue;
                    res.Add(new Ponta { W1 = a.r.W, F1 = fimA, X1 = xa, W2 = b.r.W, F2 = fimB, X2 = xb });
                }
            }
            return res;
        }

        /// <summary>Moves one end of the wall along its own line so that its centre line ends at x (plan).</summary>
        private static bool Esticar(Wall w, int fim, XYZ x)
        {
            if (!(w.Location is LocationCurve lc) || !(lc.Curve is Line ln)) return false;
            XYZ p0 = ln.GetEndPoint(0), p1 = ln.GetEndPoint(1);
            XYZ d = Plano(p1 - p0).Normalize();
            XYZ fimP = fim == 0 ? p0 : p1;
            // the centre line is parallel to the location line: the same shift along d
            XYZ desl = Plano(w.Orientation).Normalize() * DesvioDoCentro(w);
            double t = (Plano(x) - (Plano(fimP) + desl)).DotProduct(d);
            if (Math.Abs(t) < 0.05 * Mm) return false;
            XYZ novo = fimP + d * t;
            if (fim == 0) p0 = novo; else p1 = novo;
            if (p0.DistanceTo(p1) < 10 * Mm) return false;
            lc.Curve = Line.CreateBound(p0, p1);
            return true;
        }

        // ================================================================== heights

        private static bool IgualarAlturas(Wall b, Wall r)
        {
            bool mudou = false;
            void Copiar(BuiltInParameter bip)
            {
                Parameter pb = b.get_Parameter(bip), pr = r.get_Parameter(bip);
                if (pb == null || pr == null || pr.IsReadOnly) return;
                switch (pb.StorageType)
                {
                    case StorageType.ElementId: if (pr.AsElementId() != pb.AsElementId()) { pr.Set(pb.AsElementId()); mudou = true; } break;
                    case StorageType.Double: if (Math.Abs(pr.AsDouble() - pb.AsDouble()) > 1e-6) { pr.Set(pb.AsDouble()); mudou = true; } break;
                    case StorageType.Integer: if (pr.AsInteger() != pb.AsInteger()) { pr.Set(pb.AsInteger()); mudou = true; } break;
                }
            }
            Copiar(BuiltInParameter.WALL_BASE_CONSTRAINT);
            Copiar(BuiltInParameter.WALL_BASE_OFFSET);
            Copiar(BuiltInParameter.WALL_HEIGHT_TYPE);
            Copiar(BuiltInParameter.WALL_TOP_OFFSET);
            if (b.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE)?.AsElementId() == ElementId.InvalidElementId)
                Copiar(BuiltInParameter.WALL_USER_HEIGHT_PARAM);
            return mudou;
        }

        // ================================================================== lock

        /// <summary>Pairs of elements that already have a locked dimension / alignment between them.</summary>
        private HashSet<(long, long)> PresosExistentes()
        {
            var res = new HashSet<(long, long)>();
            foreach (Dimension d in new FilteredElementCollector(Doc).OfClass(typeof(Dimension)).Cast<Dimension>())
            {
                try
                {
                    if (!d.IsLocked) continue;
                    var ids = new List<long>();
                    foreach (Reference r in d.References) ids.Add(r.ElementId.Value);
                    ids = ids.Distinct().ToList();
                    for (int i = 0; i < ids.Count; i++)
                        for (int j = i + 1; j < ids.Count; j++)
                            res.Add((Math.Min(ids[i], ids[j]), Math.Max(ids[i], ids[j])));
                }
                catch { }
            }
            return res;
        }

        /// <summary>Plans of the level to make the alignment in (the active one first; floor plans before ceiling plans).</summary>
        private List<View> VistasDoNivel(ElementId nivel, Dictionary<ElementId, List<View>> cache)
        {
            if (cache.TryGetValue(nivel, out List<View> l)) return l;
            l = new List<View>();
            if (Doc.ActiveView is ViewPlan ap && !ap.IsTemplate && ap.GenLevel?.Id == nivel) l.Add(ap);
            l.AddRange(new FilteredElementCollector(Doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(x => !x.IsTemplate && x.GenLevel?.Id == nivel && !l.Contains(x))
                .OrderBy(x => x.ViewType == ViewType.FloorPlan ? 0 : 1).Take(4));
            cache[nivel] = l;
            return l;
        }

        /// <summary>
        /// The touching side faces of the wall (normal n, towards the finish) and of the finish (−n): the first
        /// point of the contact (along the overlap, at mid height) that lies on both faces — not in a door hole.
        /// </summary>
        private static bool FacesDeContato(Info b, Info r, XYZ n, out Reference fb, out Reference fr)
        {
            fb = fr = null;
            double t0 = (r.A - b.A).DotProduct(b.D), t1 = (r.B - b.A).DotProduct(b.D);
            double s0 = Math.Max(Math.Min(t0, t1), 0), s1 = Math.Min(Math.Max(t0, t1), b.L);
            double z0 = Math.Max(b.Z0, r.Z0), z1 = Math.Min(b.Z1, r.Z1);
            if (s1 <= s0 || z1 <= z0) return false;
            var facesB = FacesLaterais(b.W, n);
            var facesR = FacesLaterais(r.W, -n);
            if (facesB.Count == 0 || facesR.Count == 0) return false;
            foreach (double fz in new[] { 0.5, 0.25, 0.75 })
                foreach (double ft in new[] { 0.5, 0.3, 0.7, 0.15, 0.85, 0.05, 0.95 })
                {
                    XYZ q = b.A + b.D * (s0 + (s1 - s0) * ft) + n * (b.Larg / 2);
                    q = new XYZ(q.X, q.Y, z0 + (z1 - z0) * fz);
                    Reference rb = facesB.FirstOrDefault(f => Contem(f.face, q)).r, rr = facesR.FirstOrDefault(f => Contem(f.face, q)).r;
                    if (rb != null && rr != null) { fb = rb; fr = rr; return true; }
                }
            fb = facesB[0].r; fr = facesR[0].r;   // no common point found: the first faces with the right normals
            return true;
        }

        private static bool Contem(PlanarFace f, XYZ q)
        {
            try { IntersectionResult ir = f.Project(q); return ir != null && ir.Distance < 2 * Mm; } catch { return false; }
        }

        private static List<(Reference r, PlanarFace face)> FacesLaterais(Wall w, XYZ normal)
        {
            var l = new List<(Reference, PlanarFace)>();
            foreach (ShellLayerType lado in new[] { ShellLayerType.Exterior, ShellLayerType.Interior })
            {
                IList<Reference> refs;
                try { refs = HostObjectUtils.GetSideFaces(w, lado); } catch { continue; }
                foreach (Reference r in refs)
                {
                    PlanarFace pf = null;
                    try { pf = w.GetGeometryObjectFromReference(r) as PlanarFace; } catch { }
                    if (pf != null && Plano(pf.FaceNormal).DotProduct(normal) > 0.999) l.Add((r, pf));
                }
            }
            return l;
        }

        // ================================================================== join

        /// <summary>Every wall each finish touches on its inner side (its wall, the next one of a run, the layer below): that wall cuts.</summary>
        private static List<(Wall corta, Wall cortado)> Contatos(List<Par> pares, List<Info> todas, double espMax, string[] palavras)
        {
            var atuais = todas.Select(i => Ler(i.W, espMax, palavras)).Where(i => i != null).ToList();
            var res = new List<(Wall, Wall)>();
            var vistos = new HashSet<(long, long)>();
            foreach (Par p in pares)
            {
                Info r = Ler(p.Rev.W, espMax, palavras);
                if (r == null) continue;
                XYZ dentro = p.Core.N * -p.Lado;                  // towards the core
                void Add(Wall b)
                {
                    if (b.Id == r.W.Id) return;
                    if (vistos.Add((b.Id.Value, r.W.Id.Value))) res.Add((b, r.W));
                }
                if (p.Base.W.IsValidObject) Add(p.Base.W);
                foreach (Info b in atuais)
                {
                    if (b.W.Id == r.W.Id || !Paralelas(b, r) || SobreAltura(b, r) < 30 * Mm || SobreAoLongo(b, r) < 10 * Mm) continue;
                    double sd = (r.Meio - b.A).DotProduct(b.N);
                    if (Math.Abs(Math.Abs(sd) - (b.Larg + r.Larg) / 2) > 1 * Mm) continue;
                    if ((b.Meio - r.Meio).DotProduct(dentro) <= 0) continue;    // only what is behind the finish
                    Add(b.W);
                }
            }
            return res;
        }

        // ================================================================== transactions

        /// <summary>
        /// The items in one transaction; one that Revit refuses (error at commit) is split in halves until only
        /// the refused items stay out. acao: null = nothing to do, "ok"/"ja"/… = done, "!…" = failure message.
        /// </summary>
        private void Lote<T>(List<T> itens, string nome, Func<T, string> acao, Action<T, string> registrar)
        {
            if (itens.Count == 0 || ToolProgress.Cancelled) return;
            var parcial = new List<(T x, string r)>();
            bool ok = false;
            var guarda = new GuardaGrupos();
            try
            {
                using (var t = new Transaction(Doc, "JA Tools - " + nome))
                {
                    t.Start();
                    guarda.Anexar(t);
                    foreach (T x in itens)
                    {
                        string r;
                        try { r = acao(x); } catch (Exception ex) { r = "!" + ex.Message; }
                        parcial.Add((x, r));
                    }
                    ok = t.Commit() == TransactionStatus.Committed && !guarda.Desfez;
                }
            }
            catch (Exception ex) { Logger.Warn("[Ajustar Paredes Cebola] " + nome + ": " + ex.Message); ok = false; }
            if (ok) { foreach (var (x, r) in parcial) registrar(x, r); return; }
            if (itens.Count == 1) { registrar(itens[0], "!o Revit recusou"); return; }
            int meio = itens.Count / 2;
            Lote(itens.Take(meio).ToList(), nome, acao, registrar);
            Lote(itens.Skip(meio).ToList(), nome, acao, registrar);
        }
    }
}
