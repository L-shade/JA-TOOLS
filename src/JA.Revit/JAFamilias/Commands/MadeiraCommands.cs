using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using JAEsquadrias.Core;
using JAEsquadrias.Engine;
using Settings = JAEsquadrias.Core.Settings;

namespace JAEsquadrias.Commands
{
    public abstract partial class EsquadriaCommandBase
    {
        /// <summary>Woods (name, colour) offered for ripados and pergolados.</summary>
        protected static readonly (string Nome, string Hex)[] Madeiras =
        {
            ("Cumaru", "8A5A3B"), ("Freijó", "B08A5E"), ("Jequitibá", "C9A27A"), ("Tauari", "D8BE96"), ("Itaúba", "6E5238"),
            ("Ipê", "5E4128"), ("Garapeira", "A9814F"), ("Eucalipto tratado", "B5895A"), ("Pinus tratado", "D9BF8C"),
            ("MDF amadeirado carvalho", "B99467"), ("MDF amadeirado nogueira", "6B4A33"), ("MDF branco", "EDEBE6"), ("MDF preto", "2B2B2B"),
            ("Alumínio amadeirado", "9C6E45"),
        };

        protected static string NomeFamiliaMadeira(MadeiraConfig c) =>
            (c.Uso == UsoMadeira.Pergolado ? "JA Pergolado " : "JA Painel ripado ") + c.Codigo + " " + PecaUtil.Mm(c.LarguraMm) + "x" +
            PecaUtil.Mm(c.Uso == UsoMadeira.Pergolado ? c.ProfundidadeMm : c.AlturaMm);

        protected static MadeiraConfig InicioMadeira(UsoMadeira uso)
        {
            var c = PecaUtil.FromJson(TipoPeca.Madeira, ToolPrefs.Get("ultima.peca." + TipoPeca.Madeira + "." + uso)) as MadeiraConfig
                    ?? new MadeiraConfig { Uso = uso, AlturaMm = uso == UsoMadeira.Pergolado ? 2500 : 2600 };
            c.Uso = uso;
            return c.Normalized();
        }

        /// <summary>Painel ripado / pergolado: the configuration dialog. Sizes from the clicks when creating (&gt; 0).</summary>
        protected MadeiraConfig DialogoMadeira(MadeiraConfig c, bool editando, double largura = 0, double profundidade = 0)
        {
            bool pg = c.Uso == UsoMadeira.Pergolado;
            var dlg = new OptionsDialog(pg ? "Pergolado" : "Painel Ripado", pg ? "MadeiraPG" : "MadeiraRP", editando ? "Atualizar" : null);
            var fCod = dlg.Text("codigo", "Código", c.Codigo); fCod.Value = c.Codigo;
            var fNome = dlg.Text("nome", "Descrição (opcional)", c.Nome); fNome.Value = c.Nome;
            dlg.Section("Medidas");
            OptionsDialog.NumberField fLarg = null, fProf = null;
            if (editando || largura <= 0) { fLarg = dlg.Number("largura", pg ? "Comprimento (mm)" : "Largura do painel (mm)", c.LarguraMm, 100, 30000); fLarg.Value = c.LarguraMm; }
            if (pg && (editando || profundidade <= 0)) { fProf = dlg.Number("prof", "Profundidade (mm)", c.ProfundidadeMm, 300, 20000); fProf.Value = c.ProfundidadeMm; }
            var fAlt = dlg.Number("altura", pg ? "Vão livre sob as vigas (mm)" : "Altura do painel (mm)", c.AlturaMm, 100, 8000); fAlt.Value = c.AlturaMm;
            OptionsDialog.NumberField fRL = null, fRE = null, fEsp = null, fFE = null, fElev = null, fPil = null, fVL = null, fVA = null, fCL = null, fCA = null, fCE = null, fBei = null, fVao = null;
            OptionsDialog.BoolField fHor = null, fFundo = null, fParede = null;
            if (!pg)
            {
                fElev = dlg.Number("elev", "Começa a (mm do piso)", c.ElevacaoMm, 0, 6000, "0 = do piso. Ex.: 100 para deixar o rodapé à mostra."); fElev.Value = c.ElevacaoMm;
                dlg.Section("Ripas");
                fHor = dlg.Check("horizontal", "Ripas na horizontal", c.Horizontal); fHor.Value = c.Horizontal;
                fRL = dlg.Number("ripaL", "Largura da ripa (face aparente, mm)", c.RipaLarguraMm, 5, 500); fRL.Value = c.RipaLarguraMm;
                fRE = dlg.Number("ripaE", "Espessura da ripa (mm)", c.RipaEspessuraMm, 3, 200); fRE.Value = c.RipaEspessuraMm;
                fEsp = dlg.Number("vao", "Vão entre ripas (mm)", c.EspacamentoMm, 0, 1000, "As ripas são centralizadas na largura: as sobras ficam iguais nas pontas."); fEsp.Value = c.EspacamentoMm;
                fFundo = dlg.Check("fundo", "Painel de fundo (MDF atrás das ripas)", c.Fundo); fFundo.Value = c.Fundo;
                fFE = dlg.Number("fundoE", "Espessura do fundo (mm)", c.FundoEspessuraMm, 3, 60); fFE.Value = c.FundoEspessuraMm;
                fFE.EnabledWhen(fFundo);
            }
            else
            {
                dlg.Section("Estrutura");
                fPil = dlg.Number("pilar", "Pilar (seção quadrada, mm)", c.PilarMm, 40, 600); fPil.Value = c.PilarMm;
                fVao = dlg.Number("vaoMax", "Vão máximo entre pilares (mm)", c.VaoMaximoMm, 500, 10000); fVao.Value = c.VaoMaximoMm;
                fVL = dlg.Number("vigaL", "Viga: largura (mm)", c.VigaLarguraMm, 20, 400); fVL.Value = c.VigaLarguraMm;
                fVA = dlg.Number("vigaA", "Viga: altura (mm)", c.VigaAlturaMm, 30, 800); fVA.Value = c.VigaAlturaMm;
                fCL = dlg.Number("caibroL", "Caibro: largura (mm)", c.CaibroLarguraMm, 10, 300); fCL.Value = c.CaibroLarguraMm;
                fCA = dlg.Number("caibroA", "Caibro: altura (mm)", c.CaibroAlturaMm, 10, 600); fCA.Value = c.CaibroAlturaMm;
                fCE = dlg.Number("caibroE", "Caibros a cada (mm, entre eixos)", c.EspacamentoCaibroMm, 20, 3000); fCE.Value = c.EspacamentoCaibroMm;
                fBei = dlg.Number("beiral", "Beiral (balanço das vigas e caibros, mm)", c.BeiralMm, 0, 1500); fBei.Value = c.BeiralMm;
                fParede = dlg.Check("parede", "O primeiro lado encosta numa parede (sem pilares desse lado)", c.ApoiadoNaParede); fParede.Value = c.ApoiadoNaParede;
            }
            dlg.Section("Material");
            var nomes = Madeiras.Select(m => m.Nome).ToList();
            int im = Math.Max(0, Array.FindIndex(Madeiras, m => c.MaterialMadeira.IndexOf(m.Nome, StringComparison.OrdinalIgnoreCase) >= 0));
            var fMad = dlg.Choice("madeira", pg ? "Madeira" : "Ripas", nomes, im); fMad.Index = im;
            OptionsDialog.ChoiceField fCorF = null;
            if (!pg)
            {
                int iff = Math.Max(0, Array.FindIndex(Madeiras, m => c.MaterialFundo.IndexOf(m.Nome, StringComparison.OrdinalIgnoreCase) >= 0));
                fCorF = dlg.Choice("fundoMat", "Fundo", nomes, iff); fCorF.Index = iff;
                fCorF.EnabledWhen(fFundo);
            }
            if (!editando) dlg.Note(pg ? "Depois do OK não há mais cliques: o pergolado vai no retângulo que você marcou." : "O painel vai na face da parede que você clicou, entre os dois pontos.");
            if (!dlg.Show(Owner)) return null;

            var r = (MadeiraConfig)c.Copiar();
            r.Codigo = string.IsNullOrWhiteSpace(fCod.Value) ? c.Codigo : fCod.Value.Trim();
            r.Nome = fNome.Value ?? "";
            r.LarguraMm = fLarg != null ? fLarg.Value : Math.Round(largura);
            if (pg) r.ProfundidadeMm = fProf != null ? fProf.Value : Math.Round(profundidade);
            r.AlturaMm = fAlt.Value;
            if (!pg)
            {
                r.ElevacaoMm = fElev.Value; r.Horizontal = fHor.Value; r.RipaLarguraMm = fRL.Value; r.RipaEspessuraMm = fRE.Value; r.EspacamentoMm = fEsp.Value;
                r.Fundo = fFundo.Value; r.FundoEspessuraMm = fFE.Value;
                r.MaterialFundo = Madeiras[Math.Max(0, fCorF.Index)].Nome; r.CorFundo = Madeiras[Math.Max(0, fCorF.Index)].Hex;
            }
            else
            {
                r.PilarMm = fPil.Value; r.VaoMaximoMm = fVao.Value; r.VigaLarguraMm = fVL.Value; r.VigaAlturaMm = fVA.Value;
                r.CaibroLarguraMm = fCL.Value; r.CaibroAlturaMm = fCA.Value; r.EspacamentoCaibroMm = fCE.Value; r.BeiralMm = fBei.Value; r.ApoiadoNaParede = fParede.Value;
            }
            var mad = Madeiras[Math.Max(0, fMad.Index)];
            r.MaterialMadeira = mad.Nome.StartsWith("MDF") || mad.Nome.StartsWith("Alumínio") ? mad.Nome : "Madeira " + mad.Nome.ToLowerInvariant();
            r.CorMadeira = mad.Hex;
            r.Normalized();
            ToolPrefs.Set("ultima.peca." + TipoPeca.Madeira + "." + r.Uso, r.ToJson());
            ToolPrefs.Save();
            return r;
        }

        /// <summary>The same piece (all settings but the code) already in the project, or a new family.</summary>
        protected FamilySymbol GerarMadeira(MadeiraConfig c, List<string> avisos)
        {
            string Chave(MadeiraConfig m) { var k = (MadeiraConfig)m.Copiar(); k.Codigo = ""; k.Nome = ""; k.ModeloId = ""; return k.ToJson(); }
            string minha = Chave(c);
            var igual = ProjectPecas.All(Doc, TipoPeca.Madeira).FirstOrDefault(p => p.Config is MadeiraConfig m && Chave(m) == minha);
            if (igual != null) { c.Codigo = igual.Codigo; return igual.Symbol; }
            GenerationResult gen = PecaGenerator.Generate(UiApp.Application, Doc, c, Settings.Current, NomeFamiliaMadeira(c), null);
            avisos.AddRange(gen.Warnings);
            return gen.Symbol;
        }

        /// <summary>Editar Família on a painel ripado or pergolado: the dialog, then the family is regenerated (every copy follows).</summary>
        protected Result AbrirMadeira(MadeiraConfig cfg, FamilySymbol editing)
        {
            MadeiraConfig c = DialogoMadeira(cfg, true);
            if (c == null) return Result.Cancelled;
            GenerationResult gen = PecaGenerator.Generate(UiApp.Application, Doc, c, Settings.Current, editing.Family?.Name, editing.Name);
            Show(c.NomeUso + " " + c.Codigo + " atualizado em todo o modelo.\n" + c.Descricao() +
                 (gen.Warnings.Count > 0 ? "\n\nAvisos:\n• " + string.Join("\n• ", gen.Warnings.Distinct()) : ""),
                 gen.Warnings.Count > 0 ? ResultKind.Warning : ResultKind.Success);
            return Result.Succeeded;
        }

        /// <summary>Wall picking filter for every JA tool: which walls (null = any) and whether faces/edges are allowed.</summary>
        protected sealed class FiltroDeParede : ISelectionFilter
        {
            private readonly Func<Wall, bool> _ok;
            private readonly bool _referencias;
            public FiltroDeParede(Func<Wall, bool> ok = null, bool referencias = false) { _ok = ok; _referencias = referencias; }
            public bool AllowElement(Element e) => e is Wall w && (_ok == null || _ok(w));
            public bool AllowReference(Reference r, XYZ p) => _referencias;
            public static bool Reta(Wall w) => (w.Location as LocationCurve)?.Curve is Line;
        }

        /// <summary>The straight wall a line was drawn along (parallel, within 50 cm of a face, overlapping it).</summary>
        protected Wall ParedeAoLongo(XYZ p0, XYZ p1)
        {
            XYZ a = new XYZ(p0.X, p0.Y, 0), b = new XYZ(p1.X, p1.Y, 0);
            if (a.DistanceTo(b) < 1e-6) return null;
            XYZ d = (b - a).Normalize(), m = (a + b) / 2;
            Wall melhor = null;
            double best = double.MaxValue;
            foreach (Wall w in new FilteredElementCollector(Doc).OfClass(typeof(Wall)).Cast<Wall>())
            {
                if (!((w.Location as LocationCurve)?.Curve is Line ln)) continue;
                XYZ s = new XYZ(ln.GetEndPoint(0).X, ln.GetEndPoint(0).Y, 0), e = new XYZ(ln.GetEndPoint(1).X, ln.GetEndPoint(1).Y, 0);
                double L = s.DistanceTo(e);
                if (L < 1e-6) continue;
                XYZ wd = (e - s) / L;
                if (Math.Abs(wd.DotProduct(d)) < 0.996) continue;
                double t = (m - s).DotProduct(wd);
                if (t < -0.3 || t > L + 0.3) continue;
                XYZ ori = new XYZ(w.Orientation.X, w.Orientation.Y, 0).Normalize();
                double off = 0;
                try { off = CortesEsquadriasCommand.WallOffset(w); } catch { }
                double dist = Math.Abs((m - (s + ori * off)).DotProduct(ori));   // from the wall's centre line
                double aoFace = Math.Abs(dist - w.Width / 2);
                if (aoFace < 0.5 / 0.3048 && aoFace < best) { best = aoFace; melhor = w; }
            }
            return melhor;
        }

        /// <summary>
        /// Three points (two corners of the first side, one of the opposite side) from a croqui: a closed
        /// rectangle (the side against a wall first, else the longest) or two lines at a right angle.
        /// </summary>
        protected List<XYZ> RetanguloDoCroqui(List<Curve> curvas, Level nivel, out string erro)
        {
            erro = null;
            const double tol = 2 / 304.8;
            List<XYZ> v = null;
            List<XYZ> poli = FecharPoligono(curvas.Where(c => c is Line), tol);
            if (poli != null && curvas.All(c => c is Line)) v = SemColineares(poli);
            else
            {
                // two straight lines at a right angle: the first side, then the depth
                List<XYZ> aberto = curvas.All(c => c is Line) ? RaloCroquiCommand.EncadearCaminho(curvas, 100, out _) : null;
                if (aberto != null && aberto.Count == 3 && Math.Abs((aberto[1] - aberto[0]).Normalize().DotProduct((aberto[2] - aberto[1]).Normalize())) < 0.01)
                    return aberto.Select(p => new XYZ(p.X, p.Y, 0)).ToList();
                erro = "Desenhe o retângulo do pergolado (4 lados retos) ou duas linhas em ângulo reto (primeiro lado e profundidade).";
                return null;
            }
            if (v.Count != 4) { erro = "O contorno precisa ser um retângulo (4 lados); este tem " + v.Count + "."; return null; }
            for (int k = 0; k < 4; k++)
            {
                XYZ e0 = (v[(k + 1) % 4] - v[k]).Normalize(), e1 = (v[(k + 2) % 4] - v[(k + 1) % 4]).Normalize();
                if (Math.Abs(e0.DotProduct(e1)) > 0.01) { erro = "O contorno precisa ter cantos em ângulo reto."; return null; }
            }
            // the first side: the one along a wall, else the longest
            int primeiro = -1;
            for (int k = 0; k < 4 && primeiro < 0; k++)
                if (ParedeAoLongo(v[k], v[(k + 1) % 4]) != null) primeiro = k;
            if (primeiro < 0) primeiro = v[0].DistanceTo(v[1]) >= v[1].DistanceTo(v[2]) ? 0 : 1;
            return new List<XYZ> { v[primeiro], v[(primeiro + 1) % 4], v[(primeiro + 2) % 4] };
        }

        protected static string CutList(MadeiraConfig c) =>
            string.Join("\n", c.ListaDeCorte().Select(p => "• " + p.Quantidade + " × " + p.Nome.ToLowerInvariant() + " " + p.Secao + " com " + PecaUtil.Mm(p.ComprimentoMm) + " mm (" + PecaUtil.M(p.MetrosLineares) + ")"));
    }

    /// <summary>
    /// Painel Ripado: click the wall (on the room side), then the start and the end of the panel;
    /// the panel (ripas, optional backing board) is generated in that width and placed on the face.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class PainelRipadoCommand : EsquadriaCommandBase
    {
        public override string Title => "Painel Ripado";

        protected override Result Run()
        {
            Level nivel = NivelDaPlanta();
            if (nivel == null) return Result.Cancelled;
            XYZ Plano(XYZ q) => new XYZ(q.X, q.Y, 0);
            EntradaCroqui ent = EntradaDoCroqui("ripado", Doc.ActiveView,
                "Desenhe uma linha ao longo da face da parede, do início ao fim do painel, do lado da parede onde ele vai: a parede é achada pela linha. Clicar: a parede e depois o início e o fim.");
            if (ent == null) return Result.Cancelled;
            if (ent.Iniciado) return Result.Succeeded;
            Wall w;
            XYZ ladoRef;
            List<XYZ> cl;
            if (ent.Curvas != null)
            {
                List<XYZ> cadeia = RaloCroquiCommand.EncadearCaminho(ent.Curvas, 100, out string erro);
                if (cadeia == null) { Show(erro, ResultKind.Warning); return Result.Cancelled; }
                cl = new List<XYZ> { cadeia[0], cadeia[cadeia.Count - 1] };
                w = ParedeAoLongo(cl[0], cl[1]);
                if (w == null) { Show("Não achei uma parede reta paralela a essa linha, a até 50 cm da face dela. Desenhe a linha junto da face da parede.", ResultKind.Warning); return Result.Cancelled; }
                ladoRef = (cl[0] + cl[1]) / 2;
            }
            else
            {
                Reference rw;
                try
                {
                    rw = UiDoc.Selection.PickObject(ObjectType.Element, new FiltroDeParede(FiltroDeParede.Reta), "Painel ripado: clique a parede (reta), do lado onde vai o painel");
                    w = Doc.GetElement(rw) as Wall;
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
                cl = PegarPontos("Clique o início do painel", "Clique o fim do painel", 2, true);
                if (cl.Count < 2 || w == null) return Result.Cancelled;
                ladoRef = rw.GlobalPoint ?? cl[0];
            }

            Line linha = (Line)((LocationCurve)w.Location).Curve;
            XYZ dir = Plano(linha.Direction).Normalize(), ori = Plano(w.Orientation).Normalize();
            double off = 0;
            try { off = CortesEsquadriasCommand.WallOffset(w); } catch { }
            XYZ centro = Plano(linha.GetEndPoint(0)) + ori * off;
            double lado = Math.Sign((Plano(ladoRef) - centro).DotProduct(ori));
            if (lado == 0) lado = Math.Sign((Plano(cl[0]) - centro).DotProduct(ori));
            if (lado == 0) lado = 1;
            XYZ n = ori * lado, face = centro + n * (w.Width / 2);
            XYZ Proj(XYZ q) { double t = (Plano(q) - face).DotProduct(dir); return face + dir * t; }
            XYZ a = Proj(cl[0]), b = Proj(cl[1]);
            double largura = a.DistanceTo(b) / MmFt;
            if (largura < 100) { Show("Painel estreito demais (mínimo 100 mm).", ResultKind.Warning); return Result.Cancelled; }

            MadeiraConfig c = InicioMadeira(UsoMadeira.PainelRipado);
            c.Codigo = ProjectPecas.NextCode(ProjectPecas.UsedCodes(Doc, TipoPeca.Madeira), "RP");
            c = DialogoMadeira(c, false, largura);
            if (c == null) return Result.Cancelled;
            var avisos = new List<string>();
            FamilySymbol s = GerarMadeira(c, avisos);
            if (s == null) { Show("A família não foi gerada.\n" + string.Join("\n", avisos), ResultKind.Error); return Result.Failed; }
            FamilyInstance fi;
            using (var t = new Transaction(Doc, "JA Famílias – " + Title))
            {
                t.Start();
                var o = t.GetFailureHandlingOptions(); o.SetFailuresPreprocessor(new WarningSwallower()); t.SetFailureHandlingOptions(o);
                XYZ meio = (a + b) / 2;
                fi = ColocarNoPonto(s, new XYZ(meio.X, meio.Y, nivel.ProjectElevation), n.CrossProduct(XYZ.BasisZ), nivel);
                ApagarCroqui(ent);
                t.Commit();
            }
            try { UiDoc.Selection.SetElementIds(new List<ElementId> { fi.Id }); } catch { }
            Show($"Painel ripado {c.Codigo} colocado na face da parede.\n{c.Descricao()}\n\nCorte:\n{CutList(c)}\n\nEditar Família no painel muda todas as cópias; a Lista de Compras soma as ripas." +
                 (avisos.Count > 0 ? "\n\nAvisos:\n• " + string.Join("\n• ", avisos.Distinct()) : ""), avisos.Count > 0 ? ResultKind.Warning : ResultKind.Success);
            return Result.Succeeded;
        }
    }

    /// <summary>Pergolado: click two corners of the first side and a point of the opposite side; generated and placed.</summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class PergoladoCommand : EsquadriaCommandBase
    {
        public override string Title => "Pergolado";

        protected override Result Run()
        {
            Level nivel = NivelDaPlanta();
            if (nivel == null) return Result.Cancelled;
            EntradaCroqui ent = EntradaDoCroqui("pergolado", Doc.ActiveView,
                "Desenhe o retângulo do pergolado (face externa dos pilares). O lado encostado numa parede, se houver, é achado sozinho; sem parede, o primeiro lado é o mais comprido. Clicar: dois cantos do primeiro lado e um ponto do lado oposto.");
            if (ent == null) return Result.Cancelled;
            if (ent.Iniciado) return Result.Succeeded;
            List<XYZ> pts;
            if (ent.Curvas != null)
            {
                pts = RetanguloDoCroqui(ent.Curvas, nivel, out string erro);
                if (pts == null) { Show(erro, ResultKind.Warning); return Result.Cancelled; }
            }
            else
                pts = PegarPontos(k => k == 0 ? "Pergolado: clique o primeiro canto (face externa dos pilares; do lado da parede, se encostar numa)"
                                    : k == 1 ? "Clique o segundo canto do mesmo lado (Shift inverte o orto)"
                                    : "Clique um ponto do lado oposto (dá a profundidade)", 3, true);
            if (pts.Count < 3) return Result.Cancelled;
            XYZ Plano(XYZ q) => new XYZ(q.X, q.Y, 0);
            XYZ p1 = Plano(pts[0]), p2 = Plano(pts[1]), p3 = Plano(pts[2]);
            double comp = p1.DistanceTo(p2) / MmFt;
            if (comp < 500) { Show("O primeiro lado ficou curto demais (mínimo 500 mm).", ResultKind.Warning); return Result.Cancelled; }
            XYZ x = (p2 - p1).Normalize();
            XYZ y = XYZ.BasisZ.CrossProduct(x);
            double prof = (p3 - p1).DotProduct(y) / MmFt;
            XYZ origem = p1;
            if (prof < 0) { origem = p2; x = -x; prof = -prof; }   // +Y of the family must point to the third click
            if (prof < 300) { Show("A profundidade ficou curta demais (mínimo 300 mm).", ResultKind.Warning); return Result.Cancelled; }

            MadeiraConfig c = InicioMadeira(UsoMadeira.Pergolado);
            c.Codigo = ProjectPecas.NextCode(ProjectPecas.UsedCodes(Doc, TipoPeca.Madeira), "PG");
            c = DialogoMadeira(c, false, comp, prof);
            if (c == null) return Result.Cancelled;
            var avisos = new List<string>();
            FamilySymbol s = GerarMadeira(c, avisos);
            if (s == null) { Show("A família não foi gerada.\n" + string.Join("\n", avisos), ResultKind.Error); return Result.Failed; }
            FamilyInstance fi;
            using (var t = new Transaction(Doc, "JA Famílias – " + Title))
            {
                t.Start();
                var o = t.GetFailureHandlingOptions(); o.SetFailuresPreprocessor(new WarningSwallower()); t.SetFailureHandlingOptions(o);
                fi = ColocarNoPonto(s, new XYZ(origem.X, origem.Y, nivel.ProjectElevation), x, nivel);
                ApagarCroqui(ent);
                t.Commit();
            }
            try { UiDoc.Selection.SetElementIds(new List<ElementId> { fi.Id }); } catch { }
            double vol = c.ListaDeCorte().Sum(p => p.VolumeM3);
            Show($"Pergolado {c.Codigo} colocado ({PecaUtil.Mm(c.LarguraMm)} × {PecaUtil.Mm(c.ProfundidadeMm)} mm, {(c.LarguraMm * c.ProfundidadeMm / 1e6).ToString("0.00", PecaUtil.Pt)} m² coberto).\n{c.Descricao()}\n\n" +
                 $"Corte ({vol.ToString("0.00", PecaUtil.Pt)} m³ de madeira):\n{CutList(c)}\n\nAs seções são de pré-dimensionamento: confirme com o cálculo estrutural." +
                 (avisos.Count > 0 ? "\n\nAvisos:\n• " + string.Join("\n• ", avisos.Distinct()) : ""), avisos.Count > 0 ? ResultKind.Warning : ResultKind.Success);
            return Result.Succeeded;
        }
    }
}
