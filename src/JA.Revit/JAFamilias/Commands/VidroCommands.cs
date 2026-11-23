using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using JAEsquadrias.Common;
using JAEsquadrias.Core;
using JAEsquadrias.Engine;
using JAEsquadrias.Infrastructure;
using Settings = JAEsquadrias.Core.Settings;

namespace JAEsquadrias.Commands
{
    public abstract partial class EsquadriaCommandBase
    {
        protected const double MmFt = 1 / 304.8;

        /// <summary>Points clicked in a plan view (Esc ends; at most <paramref name="max"/>), on the view's level plane.</summary>
        protected List<XYZ> PegarPontos(string primeiro, string seguinte, int max, bool orto = false) =>
            PegarPontos(n => n == 0 ? primeiro : seguinte + " (Esc termina)", max, orto);

        /// <summary>Clicked points; with orto, each one is squared to the previous (window option, Shift inverts).</summary>
        protected List<XYZ> PegarPontos(Func<int, string> prompt, int max, bool orto = false)
        {
            var pts = new List<XYZ>();
            var snaps = ObjectSnapTypes.Endpoints | ObjectSnapTypes.Intersections | ObjectSnapTypes.Nearest | ObjectSnapTypes.Perpendicular | ObjectSnapTypes.Midpoints;
            while (pts.Count < max)
            {
                try
                {
                    XYZ p = UiDoc.Selection.PickPoint(snaps, prompt(pts.Count));
                    if (orto && pts.Count > 0) p = AplicarOrto(pts[pts.Count - 1], p);
                    pts.Add(p);
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { break; }
            }
            return pts;
        }

        /// <summary>The level of the active plan view, or null (with a message).</summary>
        protected Level NivelDaPlanta()
        {
            if (Doc.ActiveView is ViewPlan vp && vp.GenLevel != null) return vp.GenLevel;
            Show("Abra uma planta baixa (o nível dela é onde a peça fica).", ResultKind.Info);
            return null;
        }

        /// <summary>A point-based JA piece at p, its X axis along dir, its bottom at p.Z. Inside a transaction.</summary>
        protected FamilyInstance ColocarNoPonto(FamilySymbol s, XYZ p, XYZ dir, Level nivel)
        {
            if (!s.IsActive) s.Activate();
            FamilyInstance fi = Doc.Create.NewFamilyInstance(new XYZ(p.X, p.Y, nivel.ProjectElevation), s, nivel, StructuralType.NonStructural);
            Doc.Regenerate();
            foreach (BuiltInParameter bip in new[] { BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM, BuiltInParameter.INSTANCE_ELEVATION_PARAM })
            {
                Parameter q = fi.get_Parameter(bip);
                if (q == null || q.IsReadOnly || q.StorageType != StorageType.Double) continue;
                try { q.Set(p.Z - nivel.ProjectElevation); break; } catch { }
            }
            double ang = Math.Atan2(dir.Y, dir.X);
            if (Math.Abs(ang) > 1e-6)
            {
                XYZ o = new XYZ(p.X, p.Y, nivel.ProjectElevation);
                ElementTransformUtils.RotateElement(Doc, fi.Id, Line.CreateBound(o, o + XYZ.BasisZ), ang);
            }
            Doc.Regenerate();
            return fi;
        }

        protected sealed class ResultadoGuardaCorpo
        {
            public int Guardas, Trechos, Placas;
            public double Metros;
            public readonly SortedSet<double> Larguras = new SortedSet<double>();
        }

        /// <summary>
        /// The guarda-corpo as Revit railings (category Guarda-corpos): the glass plates are a baluster panel
        /// family ("JA Placa de vidro GC01 1150", Balaústres), placed by the railing type's pattern; the
        /// corrimão is the type's top rail, the U profile a rail at the floor. Equal plates per stretch: one
        /// railing per run of stretches with the same plate (one type per plate width); fixed module: one
        /// railing along the whole path. Null when the plate family could not be made a baluster.
        /// Its own transaction for the railings.
        /// </summary>
        protected ResultadoGuardaCorpo GuardaCorpoDoRevit(VidroConfig c, List<XYZ> pts, Level nivel, EntradaCroqui croqui, List<string> avisos)
        {
            var tipos = new FilteredElementCollector(Doc).OfClass(typeof(RailingType)).Cast<RailingType>().ToList();
            if (tipos.Count == 0) { avisos.Add("O projeto não tem nenhum tipo de guarda-corpo para servir de base."); return null; }
            XYZ P(XYZ q) => new XYZ(q.X, q.Y, nivel.ProjectElevation);
            var trechos = new List<(XYZ a, XYZ b, double L)>();
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                XYZ a = P(pts[i]), b = P(pts[i + 1]);
                double L = a.DistanceTo(b);
                if (L > 10 * MmFt) trechos.Add((a, b, L));
            }
            if (trechos.Count == 0) { avisos.Add("Caminho curto demais."); return null; }
            double g = c.FolgaMm * MmFt, mod = c.ModuloMm * MmFt;
            // plate width (ft) of each stretch: equal plates with the gap between them and at the ends
            double Placa(double L)
            {
                if (c.ModuloFixo) return mod - g;
                double util = L - 2 * g;
                if (util <= 0) return 0;
                int k = mod > 0 ? Math.Max(1, (int)Math.Ceiling((util + g) / (mod + g))) : 1;
                return (util - (k - 1) * g) / k;
            }
            var larguras = trechos.Select(t => Math.Round(Placa(t.L) / MmFt)).ToList();
            // runs of consecutive stretches with the same plate → one railing each (one railing in fixed-module mode)
            var grupos = new List<(int ini, int fim, double placaMm)>();
            for (int i = 0; i < trechos.Count; i++)
            {
                if (larguras[i] < 50) { if (larguras[i] > 0) AddUm(avisos, "Trecho curto demais para uma placa (menos de 50 mm de vidro): ficou sem guarda-corpo."); continue; }
                if (grupos.Count > 0 && grupos[grupos.Count - 1].fim == i - 1 && Math.Abs(grupos[grupos.Count - 1].placaMm - larguras[i]) < 0.5)
                    grupos[grupos.Count - 1] = (grupos[grupos.Count - 1].ini, i, larguras[i]);
                else grupos.Add((i, i, larguras[i]));
            }
            if (grupos.Count == 0) { avisos.Add("Os trechos são menores que as folgas: nenhuma placa cabe."); return null; }

            // one plate family per width (Balaústres)
            var placas = new Dictionary<double, FamilySymbol>();
            foreach (double w in grupos.Select(x => x.placaMm).Distinct())
            {
                var pc = (VidroConfig)c.Copiar();
                pc.Balaustre = true; pc.Partes = ParteGuardaCorpo.Vidro; pc.ComprimentoMm = w;
                pc.Normalized();
                string nome = "JA Placa de vidro " + c.Codigo + " " + PecaUtil.Mm(w);
                var existente = new FilteredElementCollector(Doc).OfClass(typeof(Family)).Cast<Family>().FirstOrDefault(f => f.Name == nome);
                GenerationResult gen = PecaGenerator.Generate(UiApp.Application, Doc, pc, Settings.Current, nome, existente != null ? c.Codigo : null);
                avisos.AddRange(gen.Warnings);
                if (gen.Symbol == null || gen.Family?.FamilyCategory?.Id != new ElementId(BuiltInCategory.OST_StairsRailingBaluster)) return null;
                placas[w] = gen.Symbol;
            }

            var res = new ResultadoGuardaCorpo();
            string json = c.ToJson();
            AlturasDoVidroFt(c, out double z0, out double z1);
            using (var t = new Transaction(Doc, "JA Famílias – guarda-corpo"))
            {
                t.Start();
                var o = t.GetFailureHandlingOptions(); o.SetFailuresPreprocessor(new WarningSwallower()); t.SetFailureHandlingOptions(o);
                foreach (var gr in grupos)
                {
                    FamilySymbol placa = placas[gr.placaMm];
                    if (!placa.IsActive) placa.Activate();
                    double p = gr.placaMm * MmFt;
                    string nomeTipo = "JA Guarda-corpo " + c.Codigo + (c.ModuloFixo ? "" : " placa " + PecaUtil.Mm(gr.placaMm));
                    RailingType rt = new FilteredElementCollector(Doc).OfClass(typeof(RailingType)).Cast<RailingType>().FirstOrDefault(x => x.Name == nomeTipo)
                                     ?? (RailingType)tipos[0].Duplicate(nomeTipo);
                    ConfigurarTipo(rt, placa, c, p, g, z0, z1, avisos);
                    var laco = new CurveLoop();
                    double metros = 0;
                    for (int i = gr.ini; i <= gr.fim; i++) { laco.Append(Line.CreateBound(trechos[i].a, trechos[i].b)); metros += trechos[i].L; }
                    Railing r = Railing.Create(Doc, laco, rt.Id, nivel.Id);
                    int nPlacas = 0;
                    for (int i = gr.ini; i <= gr.fim; i++) nPlacas += Math.Max(1, (int)Math.Floor((trechos[i].L - g + 1e-6) / (p + g)));
                    JaDados.Gravar(r, new DadosJa { Tipo = "guardacorpo", Config = json, Metros = metros * 0.3048, AreaM2 = nPlacas * p * (z1 - z0) * 0.09290304, Peca = PecaUtil.Mm(gr.placaMm), Descricao = c.Codigo });
                    res.Guardas++; res.Trechos += gr.fim - gr.ini + 1; res.Placas += nPlacas; res.Metros += metros * 0.3048; res.Larguras.Add(gr.placaMm);
                }
                ApagarCroqui(croqui);
                t.Commit();
            }
            return res;
        }

        /// <summary>
        /// Editar Família on JA guarda-corpos made as Revit railings: the dialog with the stored settings,
        /// then each railing is made again on its own path (the plates are recalculated).
        /// </summary>
        protected Result EditarGuardaCorpos(List<Railing> gcs)
        {
            VidroConfig cfg = null;
            try { cfg = Json.Deserialize<VidroConfig>(JaDados.Ler(gcs[0]).Config)?.Normalized(); } catch { }
            if (cfg == null) { Show("Este guarda-corpo não tem as configurações guardadas.", ResultKind.Warning); return Result.Cancelled; }
            VidroConfig c = DialogoVidro(cfg, true);
            if (c == null) return Result.Cancelled;
            var avisos = new List<string>();
            var caminhos = new List<(List<XYZ> pts, Level nivel)>();
            foreach (Railing r in gcs)
            {
                var curvas = r.GetPath();
                Level nivel = Doc.GetElement(r.LevelId) as Level ?? (Doc.ActiveView as ViewPlan)?.GenLevel;
                if (curvas == null || curvas.Count == 0 || nivel == null) continue;
                var pts = new List<XYZ> { curvas[0].GetEndPoint(0) };
                foreach (Curve cv in curvas) pts.Add(cv.GetEndPoint(1));
                caminhos.Add((pts, nivel));
            }
            using (var tg = new TransactionGroup(Doc, "JA Famílias – editar guarda-corpo"))
            {
                tg.Start();
                using (var t = new Transaction(Doc, "JA Famílias – apagar guarda-corpo antigo"))
                {
                    t.Start();
                    Doc.Delete(gcs.Select(r => r.Id).ToList());
                    t.Commit();
                }
                int feitos = 0;
                foreach (var (pts, nivel) in caminhos)
                {
                    ResultadoGuardaCorpo rg = GuardaCorpoDoRevit(c, pts, nivel, null, avisos);
                    if (rg == null) { tg.RollBack(); Show("Não deu para refazer o guarda-corpo do Revit.\n" + string.Join("\n", avisos.Distinct()), ResultKind.Error); return Result.Failed; }
                    feitos += rg.Guardas;
                }
                tg.Assimilate();
                Show($"Guarda-corpo {c.Codigo} atualizado: {feitos} guarda-corpo(s) do Revit refeito(s).\n{c.Descricao()}" +
                     (avisos.Count > 0 ? "\n\nAvisos:\n• " + string.Join("\n• ", avisos.Distinct().Take(15)) : ""), avisos.Count > 0 ? ResultKind.Warning : ResultKind.Success);
            }
            return Result.Succeeded;
        }

        private static void AlturasDoVidroFt(VidroConfig c, out double z0, out double z1)
        {
            c.AlturasDoVidro(out double b, out double tp);
            z0 = b * MmFt; z1 = tp * MmFt;
        }

        /// <summary>The railing type of the JA guarda-corpo: height, top rail (corrimão) or none, no handrails and no posts,
        /// the U profile as a rail, and the pattern = one glass plate every (plate + gap), starting half a plate + gap in.</summary>
        private void ConfigurarTipo(RailingType rt, FamilySymbol placa, VidroConfig c, double p, double g, double z0, double z1, List<string> avisos)
        {
            void Tenta(string oQue, Action a) { try { a(); } catch (Exception ex) { AddUm(avisos, "Tipo de guarda-corpo – " + oQue + ": " + ex.Message); } }
            Tenta("altura", () => rt.TopRailHeight = c.AlturaMm * MmFt);
            Tenta("corrimão", () =>
            {
                if (!c.Corrimao) { rt.TopRailType = ElementId.InvalidElementId; return; }
                var tops = new FilteredElementCollector(Doc).OfClass(typeof(TopRailType)).Cast<TopRailType>().ToList();
                TopRailType tr = tops.FirstOrDefault(x => x.Name.IndexOf("ret", StringComparison.OrdinalIgnoreCase) >= 0 || x.Name.IndexOf("rect", StringComparison.OrdinalIgnoreCase) >= 0) ?? tops.FirstOrDefault();
                if (tr != null) rt.TopRailType = tr.Id;
            });
            Tenta("corrimãos laterais", () => { rt.PrimaryHandrailType = ElementId.InvalidElementId; rt.SecondaryHandrailType = ElementId.InvalidElementId; });
            Tenta("travessas", () =>
            {
                NonContinuousRailStructure rs = rt.RailStructure;
                for (int i = rs.GetNonContinuousRailCount() - 1; i >= 0; i--) rs.RemoveNonContinuousRail(i);
                if (c.Fixacao == FixacaoGuardaCorpo.PerfilU) rs.AddNonContinuousRail("JA Perfil U", 100 * MmFt, 0);
            });
            Tenta("pilaretes", () =>
            {
                PostPattern pp = rt.BalusterPlacement.PostPattern;
                foreach (BalusterInfo bi in new[] { pp.StartPost, pp.CornerPost, pp.EndPost })
                {
                    try { bi.BalusterFamilyId = ElementId.InvalidElementId; } catch { }
                }
            });
            BalusterPattern pat = rt.BalusterPlacement.BalusterPattern;
            while (pat.GetBalusterCount() > 1) pat.RemoveBaluster(pat.GetBalusterCount() - 1);
            BalusterInfo b = pat.GetBaluster(0);
            b.BalusterFamilyId = placa.Id;          // throws when the plate is not a baluster: the caller falls back
            Tenta("base da placa", () => { b.BaseReferenceName = BalusterInfo.GetReferenceNameForHost(); b.BaseOffset = 0; });
            Tenta("topo da placa", () => { b.TopReferenceName = BalusterInfo.GetReferenceNameForHost(); b.TopOffset = z1; });
            Tenta("posição", () => { b.Offset = 0; });
            Tenta("padrão", () =>
            {
                // the pattern length is read-only: it is the distance of the plate from the pattern start plus the
                // space after it (EndSpace) — together one plate + one gap (p + g)
                double dist = c.ModuloFixo ? (p + g) / 2 : g + p / 2;
                b.DistanceFromPreviousOrSpace = dist;
                pat.EndSpace = Math.Max(0, p + g - dist);
                pat.DistributionJustification = c.ModuloFixo ? PatternJustification.Center : PatternJustification.Beginning;
                pat.BreakPattern = BreakPatternCondition.EachSegmentEnd;
                pat.ExcessLengthFillBalusterId = ElementId.InvalidElementId;
            });
        }

        /// <summary>
        /// Guarda-corpo / box: the configuration dialog (create or edit). Null = cancelled. Box width
        /// comes from the clicks when creating (largura &gt; 0), from the field when editing.
        /// </summary>
        protected VidroConfig DialogoVidro(VidroConfig c, bool editando, double largura = 0)
        {
            bool box = c.Uso == UsoVidro.Box;
            var dlg = new OptionsDialog(box ? "Box de Banheiro" : "Guarda-corpo de Vidro", box ? "VidroBox" : "VidroGC", editando ? "Atualizar" : null);
            var fCod = dlg.Text("codigo", "Código", c.Codigo);
            var fNome = dlg.Text("nome", "Descrição (opcional)", c.Nome);
            fCod.Value = c.Codigo; fNome.Value = c.Nome;
            dlg.Section("Medidas");
            OptionsDialog.NumberField fLarg = null;
            if (box && (editando || largura <= 0)) { fLarg = dlg.Number("largura", c.Canto ? "Lado da porta (mm)" : "Largura do vão (mm)", c.ComprimentoMm, 300, 4000); fLarg.Value = c.ComprimentoMm; }
            OptionsDialog.NumberField fLado = null;
            if (box && c.Canto && editando) { fLado = dlg.Number("ladoFixo", "Lado fixo (mm)", c.LadoFixoMm, 150, 4000); fLado.Value = c.LadoFixoMm; }
            var fAlt = dlg.Number("altura", box ? "Altura do box (mm)" : "Altura total, até o topo do corrimão (mm)", c.AlturaMm, 300, 3000,
                box ? "1900 mm usual." : "NBR 14718: 1100 mm em edificações (pelo menos).");
            fAlt.Value = c.AlturaMm;
            var fEsp = dlg.Number("espessura", "Espessura do vidro (mm)", c.EspessuraVidroMm, 4, 40, box ? "8 mm temperado usual." : "Guarda-corpo: laminado temperado (ex.: 10+10 = 21 mm) conforme o cálculo.");
            fEsp.Value = c.EspessuraVidroMm;
            OptionsDialog.ChoiceField fAbr = null, fFix = null;
            OptionsDialog.BoolField fPux = null, fCorr = null, fFixo = null;
            OptionsDialog.NumberField fCL = null, fCA = null, fMod = null, fFolga = null;
            if (box)
            {
                fAbr = dlg.Choice("abertura", "Abertura", new[] { "De correr (1 fixa + 1 de correr)", "De abrir (porta pivotante)", "Fixo (só vidro fixo)" }, (int)c.Abertura);
                fAbr.Index = (int)c.Abertura;
                fPux = dlg.Check("puxador", "Puxador", c.Puxador); fPux.Value = c.Puxador;
                fPux.EnabledWhen(fAbr, 0, 1);
            }
            else
            {
                fFix = dlg.Choice("fixacao", "Fixação", new[] { "Perfil U no piso", "Lateral (botões / spider na borda da laje)" }, (int)c.Fixacao);
                fFix.Index = (int)c.Fixacao;
                fCorr = dlg.Check("corrimao", "Corrimão no topo do vidro", c.Corrimao); fCorr.Value = c.Corrimao;
                fCL = dlg.Number("corrL", "Largura do corrimão (mm)", c.CorrimaoLarguraMm, 15, 150); fCL.Value = c.CorrimaoLarguraMm;
                fCA = dlg.Number("corrA", "Altura do corrimão (mm)", c.CorrimaoAlturaMm, 10, 150); fCA.Value = c.CorrimaoAlturaMm;
                fCL.EnabledWhen(fCorr); fCA.EnabledWhen(fCorr);
                if (!editando)
                {
                    fMod = dlg.Number("modulo", "Placas de vidro de até (mm; 0 = vidro contínuo)", c.ModuloMm, 0, 6000,
                        "Cada trecho é dividido em placas iguais, com a folga entre elas e nas pontas; os perfis e o corrimão seguem contínuos.");
                    fMod.Value = c.ModuloMm;
                    fFolga = dlg.Number("folga", "Folga entre placas e nas pontas (mm)", c.FolgaMm, 0, 100); fFolga.Value = c.FolgaMm;
                    fFixo = dlg.Check("moduloFixo", "Placas todas do mesmo tamanho, num guarda-corpo só (corrimão contínuo nos cantos)", c.ModuloFixo,
                        "Desmarcado: placas iguais dentro de cada trecho, sem sobra (um guarda-corpo do Revit por trecho de placas iguais). Marcado: o Revit distribui as placas do módulo e a sobra fica nas pontas de cada trecho.");
                    fFixo.Value = c.ModuloFixo;
                    fFixo.EnabledWhen(() => fMod.Value > 0);
                }
            }
            dlg.Section("Materiais");
            var vidros = Catalog.Vidros.Select(v => v.Nome).ToList();
            int iv = Math.Max(0, Catalog.Vidros.ToList().FindIndex(v => c.MaterialVidro.IndexOf(v.Nome, StringComparison.OrdinalIgnoreCase) >= 0));
            var fVidro = dlg.Choice("vidro", "Vidro", vidros, iv); fVidro.Index = iv;
            var cores = Catalog.Cores.Select(k => k.Nome).ToList();
            int ic = Math.Max(0, Catalog.Cores.ToList().FindIndex(k => string.Equals(k.Hex, c.CorMetal, StringComparison.OrdinalIgnoreCase)));
            var fCor = dlg.Choice("cor", "Cor dos perfis", cores, ic); fCor.Index = ic;
            if (!editando && !box) dlg.Note("Depois do OK: o guarda-corpo segue o croqui escolhido (desenhado, linhas, bordas ou cliques).");
            if (!dlg.Show(Owner)) return null;

            var r = (VidroConfig)c.Copiar();
            r.Codigo = string.IsNullOrWhiteSpace(fCod.Value) ? c.Codigo : fCod.Value.Trim();
            r.Nome = fNome.Value ?? "";
            if (fLarg != null) r.ComprimentoMm = fLarg.Value; else if (largura > 0) r.ComprimentoMm = Math.Round(largura);
            r.AlturaMm = fAlt.Value;
            r.EspessuraVidroMm = fEsp.Value;
            if (box) { r.Abertura = (AberturaBox)fAbr.Index; r.Puxador = fPux.Value; if (fLado != null) r.LadoFixoMm = fLado.Value; }
            else
            {
                r.Fixacao = (FixacaoGuardaCorpo)fFix.Index; r.Corrimao = fCorr.Value; r.CorrimaoLarguraMm = fCL.Value; r.CorrimaoAlturaMm = fCA.Value;
                if (fMod != null && !editando) r.ModuloMm = fMod.Value;
                if (fFolga != null) r.FolgaMm = fFolga.Value;
                if (fFixo != null) r.ModuloFixo = fFixo.Value && r.ModuloMm > 0;
            }
            VidroInfo vi = Catalog.Vidros[Math.Max(0, fVidro.Index)];
            r.MaterialVidro = "Vidro " + vi.Nome.ToLowerInvariant();
            r.CorVidro = vi.CorHex;
            CorInfo ci = Catalog.Cores[Math.Max(0, fCor.Index)];
            r.MaterialMetal = "Alumínio " + ci.Nome.ToLowerInvariant();
            r.CorMetal = ci.Hex;
            r.Normalized();
            ToolPrefs.Set("ultima.peca." + TipoPeca.Vidro + "." + r.Uso, r.ToJson());
            ToolPrefs.Save();
            return r;
        }

        protected static string NomeFamiliaVidro(VidroConfig c) =>
            c.Uso == UsoVidro.Box ? "JA Box " + c.Codigo + " " + PecaUtil.Mm(c.ComprimentoMm) + (c.Canto ? "+" + PecaUtil.Mm(c.LadoFixoMm) : "")
            : "JA Guarda-corpo " + c.Codigo + (c.Partes == ParteGuardaCorpo.Perfis ? " perfis" : c.Partes == ParteGuardaCorpo.Vidro ? " vidro" : "");

        protected static VidroConfig InicioVidro(UsoVidro uso)
        {
            var last = PecaUtil.FromJson(TipoPeca.Vidro, ToolPrefs.Get("ultima.peca." + TipoPeca.Vidro + "." + uso)) as VidroConfig;
            var c = last ?? new VidroConfig { Uso = uso, AlturaMm = uso == UsoVidro.Box ? 1900 : 1100, EspessuraVidroMm = uso == UsoVidro.Box ? 8 : 10 };
            c.Uso = uso;
            return c.Normalized();
        }

        /// <summary>Editar Família on a guarda-corpo or box: the dialog, then the family is regenerated (every copy follows).</summary>
        protected Result AbrirVidro(VidroConfig cfg, FamilySymbol editing)
        {
            VidroConfig c = DialogoVidro(cfg, true);
            if (c == null) return Result.Cancelled;
            GenerationResult gen = PecaGenerator.Generate(UiApp.Application, Doc, c, Settings.Current, editing.Family?.Name, editing.Name);
            Show(c.NomeUso + " " + c.Codigo + " atualizado em todo o modelo.\n" + c.Descricao() +
                 (gen.Warnings.Count > 0 ? "\n\nAvisos:\n• " + string.Join("\n• ", gen.Warnings.Distinct()) : ""),
                 gen.Warnings.Count > 0 ? ResultKind.Warning : ResultKind.Success);
            return Result.Succeeded;
        }
    }

    /// <summary>Guarda-corpo de Vidro: configure, then click the points in plan; one line-based copy per stretch.</summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class GuardaCorpoCommand : EsquadriaCommandBase
    {
        public override string Title => "Guarda-corpo de Vidro";

        protected override Result Run()
        {
            Level nivel = NivelDaPlanta();
            if (nivel == null) return Result.Cancelled;
            // the path: the common croqui window (Revit tools, lines, edges or clicks with orto)
            EntradaCroqui ent = EntradaDoCroqui("guardacorpo", Doc.ActiveView,
                "Desenhe o caminho do guarda-corpo como uma linha contínua (retas e curvas; aberta ou fechada não importa: fechada vira um caminho que volta ao início).");
            if (ent == null) return Result.Cancelled;
            if (ent.Iniciado) return Result.Succeeded;
            CroquiAtual = ent;
            VidroConfig c = InicioVidro(UsoVidro.GuardaCorpo);
            c.Partes = ParteGuardaCorpo.Tudo;
            var usados = ProjectPecas.UsedCodes(Doc, TipoPeca.Vidro);
            if (usados.Contains(c.Codigo)) c.Codigo = ProjectPecas.NextCode(usados, "GC");
            c = DialogoVidro(c, false);
            if (c == null) return Result.Cancelled;
            List<XYZ> pts;
            if (ent.Curvas != null)
            {
                pts = RaloCroquiCommand.EncadearCaminho(ent.Curvas, c.ModuloMm > 0 ? c.ModuloMm : 600, out string erro);
                if (pts == null)
                {
                    // a closed outline (e.g. a rectangle around a void): walk it from a corner back to it
                    List<XYZ> anel = FecharPoligono(ent.Curvas.Where(x => x is Line), 2 * MmFt);
                    if (anel != null && ent.Curvas.All(x => x is Line)) { pts = anel; pts.Add(anel[0]); }
                    else { Show(erro, ResultKind.Warning); return Result.Cancelled; }
                }
            }
            else pts = PegarPontos("Guarda-corpo: clique o primeiro ponto", "próximo ponto (Shift inverte o orto)", 200, true);
            if (pts.Count < 2) return Result.Cancelled;

            var avisos = new List<string>();
            // always a Revit railing (category Guarda-corpos); the old JA families only if the railing cannot be made
            string falha = null;
            try
            {
                ResultadoGuardaCorpo rg = GuardaCorpoDoRevit(c, pts, nivel, ent, avisos);
                if (rg != null)
                {
                    Show($"Guarda-corpo {c.Codigo} criado como Guarda-corpo do Revit: {rg.Guardas} guarda-corpo(s), {rg.Trechos} trecho(s), {rg.Metros.ToString("0.00", PecaUtil.Pt)} m" +
                         (rg.Placas > 0 ? $", {rg.Placas} placa(s) de vidro ({string.Join(", ", rg.Larguras.Select(x => PecaUtil.Mm(x)))} mm)" : "") + ".\n" + c.Descricao() +
                         "\n\nTipos “JA Guarda-corpo " + c.Codigo + " …” (placas = família de Balaústres “JA Placa de vidro …”). O vidro fica centrado na linha." +
                         (avisos.Count > 0 ? "\n\nAvisos:\n• " + string.Join("\n• ", avisos.Distinct().Take(15)) : ""), avisos.Count > 0 ? ResultKind.Warning : ResultKind.Success);
                    return Result.Succeeded;
                }
            }
            catch (Exception ex) { falha = ex.Message; }
            avisos.Insert(0, "Não deu para criar como Guarda-corpo do Revit" + (falha != null ? " (" + falha + ")" : "") + ": feito com as famílias do JA (Modelo genérico).");
            // continuous: one family; modulated: the profiles (continuous) + the glass plate (one copy per plate)
            FamilySymbol Gerar(VidroConfig v)
            {
                var existente = ProjectPecas.All(Doc, TipoPeca.Vidro).FirstOrDefault(p => p.Config is VidroConfig pv && string.Equals(pv.Codigo, v.Codigo, StringComparison.OrdinalIgnoreCase) && pv.Partes == v.Partes);
                GenerationResult gen = PecaGenerator.Generate(UiApp.Application, Doc, v, Settings.Current, existente?.Symbol.Family?.Name ?? NomeFamiliaVidro(v), existente?.Symbol.Name);
                avisos.AddRange(gen.Warnings);
                return gen.Symbol;
            }
            bool modulado = c.ModuloMm > 0;
            FamilySymbol simbPerfis = null, simbVidro = null, simbTudo = null;
            if (modulado)
            {
                var pp = (VidroConfig)c.Copiar(); pp.Partes = ParteGuardaCorpo.Perfis;
                var pv = (VidroConfig)c.Copiar(); pv.Partes = ParteGuardaCorpo.Vidro;
                simbVidro = Gerar(pv);
                if (c.Corrimao || c.Fixacao == FixacaoGuardaCorpo.PerfilU) simbPerfis = Gerar(pp);
                if (simbVidro == null) { Show("A família não foi gerada.\n" + string.Join("\n", avisos), ResultKind.Error); return Result.Failed; }
            }
            else
            {
                simbTudo = Gerar(c);
                if (simbTudo == null) { Show("A família não foi gerada.\n" + string.Join("\n", avisos), ResultKind.Error); return Result.Failed; }
            }
            int n = 0, placas = 0;
            double folga = c.FolgaMm * MmFt;
            using (var t = new Transaction(Doc, "JA Famílias – " + Title))
            {
                t.Start();
                var o = t.GetFailureHandlingOptions(); o.SetFailuresPreprocessor(new WarningSwallower()); t.SetFailureHandlingOptions(o);
                for (int i = 0; i + 1 < pts.Count; i++)
                {
                    XYZ a = pts[i], b = pts[i + 1];
                    try
                    {
                        if (!modulado) { if (ChapimPlacer.PlaceLine(Doc, simbTudo, a, b, nivel, nivel.ProjectElevation, avisos) != null) n++; continue; }
                        if (simbPerfis != null && ChapimPlacer.PlaceLine(Doc, simbPerfis, a, b, nivel, nivel.ProjectElevation, avisos) != null) n++;
                        XYZ ab = new XYZ(b.X - a.X, b.Y - a.Y, 0);
                        double L = ab.GetLength();
                        XYZ d = ab.Normalize();
                        double util = L - 2 * folga;
                        if (util <= 0) continue;
                        int k = Math.Max(1, (int)Math.Ceiling((util + folga) / (c.ModuloMm * MmFt + folga)));
                        double placa = (util - (k - 1) * folga) / k;
                        for (int j = 0; j < k; j++)
                        {
                            XYZ p0 = a + d * (folga + j * (placa + folga)), p1 = p0 + d * placa;
                            if (ChapimPlacer.PlaceLine(Doc, simbVidro, p0, p1, nivel, nivel.ProjectElevation, avisos) != null) placas++;
                        }
                    }
                    catch (Exception ex) { avisos.Add($"Trecho {i + 1}: {ex.Message}"); }
                }
                if (!modulado) placas = n;
                ApagarCroqui(ent);
                t.Commit();
            }
            double total = 0;
            for (int i = 0; i + 1 < pts.Count; i++) total += new XYZ(pts[i].X - pts[i + 1].X, pts[i].Y - pts[i + 1].Y, 0).GetLength();
            Show($"Guarda-corpo {c.Codigo}: {pts.Count - 1} trecho(s), {(total * 0.3048).ToString("0.00", PecaUtil.Pt)} m" +
                 (modulado ? $", {placas} placa(s) de vidro de até {PecaUtil.Mm(c.ModuloMm)} mm." : ".") + $"\n{c.Descricao()}\n\nO vidro fica centrado na linha; JA_Comprimento soma os metros no Quadro de Peças." +
                 (avisos.Count > 0 ? "\n\nAvisos:\n• " + string.Join("\n• ", avisos.Distinct().Take(15)) : ""), avisos.Count > 0 ? ResultKind.Warning : ResultKind.Success);
            return Result.Succeeded;
        }
    }

    /// <summary>Box de Banheiro: configure, click the two wall faces of the opening; the box is generated in that width and placed.</summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class BoxBanheiroCommand : EsquadriaCommandBase
    {
        public override string Title => "Box de Banheiro";

        protected override Result Run()
        {
            Level nivel = NivelDaPlanta();
            if (nivel == null) return Result.Cancelled;
            EntradaCroqui ent = EntradaDoCroqui("box", Doc.ActiveView,
                "Desenhe a linha do vidro de uma face de parede à outra, da esquerda para a direita vendo de fora (box reto); box em L: do lado fixo ao canto e do canto ao lado da porta (2 linhas).");
            if (ent == null) return Result.Cancelled;
            if (ent.Iniciado) return Result.Succeeded;
            CroquiAtual = ent;
            List<XYZ> pts;
            if (ent.Curvas != null)
            {
                pts = ent.Curvas.All(x => x is Line) ? RaloCroquiCommand.EncadearCaminho(ent.Curvas, 100, out string erro) : null;
                if (pts == null || pts.Count < 2 || pts.Count > 3)
                {
                    Show("O box precisa de uma linha reta (box reto) ou de duas linhas retas ligadas (box em L).", ResultKind.Warning);
                    return Result.Cancelled;
                }
            }
            else
                pts = PegarPontos(n => n == 0 ? "Box: clique a face da parede da esquerda, vendo de fora (box em L: a parede do lado fixo)"
                                            : n == 1 ? "Clique a face da parede da direita (box em L: o canto do box)"
                                            : "Box em L: clique a parede do lado da porta (Esc = box reto)", 3, true);
            if (pts.Count < 2) return Result.Cancelled;
            bool emL = pts.Count == 3;
            XYZ Pl(XYZ q) => new XYZ(q.X, q.Y, 0);
            // straight: a → b is the opening; L: the corner is pts[1], the door leg goes to pts[2], the fixed leg to pts[0]
            XYZ a = emL ? Pl(pts[1]) : Pl(pts[0]), b = emL ? Pl(pts[2]) : Pl(pts[1]);
            double largura = a.DistanceTo(b) / MmFt;
            if (largura < 300) { Show("Vão pequeno demais (mínimo 300 mm).", ResultKind.Warning); return Result.Cancelled; }
            double ladoFixo = 0; int sinal = 1;
            if (emL)
            {
                XYZ lat = Pl(pts[0]) - a;
                ladoFixo = lat.GetLength() / MmFt;
                if (ladoFixo < 150) { Show("O lado fixo do box em L ficou curto demais (mínimo 150 mm).", ResultKind.Warning); return Result.Cancelled; }
                XYZ x = (b - a).Normalize();
                sinal = XYZ.BasisZ.CrossProduct(x).DotProduct(lat) >= 0 ? 1 : -1;
                if (Math.Abs(x.DotProduct(lat.Normalize())) > 0.1) Show("Os dois lados do box em L não estão a 90°: o box é desenhado em esquadro a partir do lado da porta.", ResultKind.Info);
            }

            VidroConfig c = InicioVidro(UsoVidro.Box);
            c.Canto = emL; c.LadoFixoMm = Math.Round(ladoFixo); c.LadoSinal = sinal;
            var usados = ProjectPecas.UsedCodes(Doc, TipoPeca.Vidro);
            c.Codigo = ProjectPecas.NextCode(usados, "BX");
            c = DialogoVidro(c, false, largura);
            if (c == null) return Result.Cancelled;

            GenerationResult gen = PecaGenerator.Generate(UiApp.Application, Doc, c, Settings.Current, NomeFamiliaVidro(c), null);
            if (gen.Symbol == null) { Show("A família não foi gerada.\n" + string.Join("\n", gen.Warnings), ResultKind.Error); return Result.Failed; }
            using (var t = new Transaction(Doc, "JA Famílias – " + Title))
            {
                t.Start();
                var o = t.GetFailureHandlingOptions(); o.SetFailuresPreprocessor(new WarningSwallower()); t.SetFailureHandlingOptions(o);
                XYZ onde = emL ? a : (a + b) / 2;
                ColocarNoPonto(gen.Symbol, new XYZ(onde.X, onde.Y, nivel.ProjectElevation), (b - a).Normalize(), nivel);
                ApagarCroqui(CroquiAtual);
                t.Commit();
            }
            Show($"Box {c.Codigo} ({c.NomeAbertura}) " + (emL ? $"em L: porta {PecaUtil.Mm(c.ComprimentoMm)} mm + lado fixo {PecaUtil.Mm(c.LadoFixoMm)} mm" : $"com {PecaUtil.Mm(c.ComprimentoMm)} mm") + $" colocado.\n{c.Descricao()}" +
                 (gen.Warnings.Count > 0 ? "\n\nAvisos:\n• " + string.Join("\n• ", gen.Warnings.Distinct()) : ""),
                 gen.Warnings.Count > 0 ? ResultKind.Warning : ResultKind.Success);
            return Result.Succeeded;
        }
    }
}
