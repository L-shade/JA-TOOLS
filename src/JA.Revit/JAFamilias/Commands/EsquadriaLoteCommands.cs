using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using JAEsquadrias.Common;
using JAEsquadrias.Core;
using JAEsquadrias.Engine;
using JAEsquadrias.Infrastructure;
using Settings = JAEsquadrias.Core.Settings;

namespace JAEsquadrias.Commands
{
    /// <summary>Settings of the Iluminação e Ventilação check (kept for Atualizar Revestimentos).</summary>
    [System.Runtime.Serialization.DataContract]
    public sealed class ConfigIluminacao
    {
        [System.Runtime.Serialization.DataMember(Order = 1)] public double IlumP { get; set; }
        [System.Runtime.Serialization.DataMember(Order = 2)] public double VentP { get; set; }
        [System.Runtime.Serialization.DataMember(Order = 3)] public string Transit { get; set; }
        [System.Runtime.Serialization.DataMember(Order = 4)] public double IlumT { get; set; }
        [System.Runtime.Serialization.DataMember(Order = 5)] public double VentT { get; set; }
        [System.Runtime.Serialization.DataMember(Order = 6)] public string Isento { get; set; }
        [System.Runtime.Serialization.DataMember(Order = 7)] public bool Exterior { get; set; }
        [System.Runtime.Serialization.DataMember(Order = 8)] public double OutrasI { get; set; }
        [System.Runtime.Serialization.DataMember(Order = 9)] public double OutrasV { get; set; }
        [System.Runtime.Serialization.DataMember(Order = 10)] public bool Cortina { get; set; }
        [System.Runtime.Serialization.DataMember(Order = 11)] public bool Parametro { get; set; }

        /// <summary>The last settings used; before 1.20, the values remembered by the dialog; else the defaults.</summary>
        public static ConfigIluminacao Salva()
        {
            try { var c = Json.Deserialize<ConfigIluminacao>(ToolPrefs.Get("iluminacao.config")); if (c != null) return c; } catch { }
            return DasPreferencias();
        }

        public static ConfigIluminacao DasPreferencias()
        {
            var c = new ConfigIluminacao();
            double N(string k, double d) => double.TryParse(ToolPrefs.Get("IluminacaoVentilacao." + k), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) && v > 0 ? v : d;
            bool B(string k, bool d) { string v = ToolPrefs.Get("IluminacaoVentilacao." + k); return v == "1" ? true : v == "0" ? false : d; }
            string T(string k, string d) { string v = ToolPrefs.Get("IluminacaoVentilacao." + k); return string.IsNullOrWhiteSpace(v) ? d : v; }
            c.IlumP = N("ilumP", c.IlumP); c.VentP = N("ventP", c.VentP); c.IlumT = N("ilumT", c.IlumT); c.VentT = N("ventT", c.VentT);
            c.OutrasI = N("outrasI", c.OutrasI); c.OutrasV = N("outrasV", c.OutrasV);
            c.Transit = T("transit", c.Transit); c.Isento = T("isento", c.Isento);
            c.Exterior = B("exterior", c.Exterior); c.Cortina = B("cortina", c.Cortina); c.Parametro = B("parametro", c.Parametro);
            return c;
        }

        public ConfigIluminacao() { Padroes(); }

        /// <summary>DataContract does not run the constructor: the defaults for fields missing in older JSON.</summary>
        [System.Runtime.Serialization.OnDeserializing] private void AoLer(System.Runtime.Serialization.StreamingContext c) => Padroes();

        private void Padroes()
        {
            IlumP = 6;
            VentP = 12;
            Transit = "banho, banheiro, lavabo, wc, sanitário, serviço, lavanderia, circulação, corredor, hall, escada, depósito, despensa, closet";
            IlumT = 8;
            VentT = 16;
            Isento = "garagem, shaft, poço, área técnica";
            Exterior = true;
            OutrasI = 80;
            OutrasV = 40;
            Cortina = true;
            Parametro = true;
        }
    }

    public abstract partial class EsquadriaCommandBase
    {
        protected sealed class ResultadoIluminacao
        {
            public int Ambientes, Ok, Isentos, Pintados;
            public bool Gravou;
            public string Csv;
            public readonly List<ElementId> Falhas = new List<ElementId>();
            public readonly List<string> Linhas = new List<string>();
        }

        /// <summary>
        /// The check itself: areas per room, the CSV text, the result written on the rooms and painted
        /// on the plans given (old JA regions replaced). Its own transaction when it writes.
        /// </summary>
        protected ResultadoIluminacao ConferirIluminacao(List<Room> rooms, ConfigIluminacao c, IEnumerable<ViewPlan> pintar)
        {
            var res = new ResultadoIluminacao { Ambientes = rooms.Count };
            string[] Lista(string s) => (s ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0).ToArray();
            string[] transit = Lista(c.Transit), isento = Lista(c.Isento);

            var aberturas = new FilteredElementCollector(Doc).WherePasses(new LogicalOrFilter(new ElementCategoryFilter(BuiltInCategory.OST_Windows), new ElementCategoryFilter(BuiltInCategory.OST_Doors)))
                .WhereElementIsNotElementType().OfType<FamilyInstance>().ToList();
            var cache = new Dictionary<long, (double il, double ve)>();
            var porAmbiente = new Dictionary<long, (double il, double ve, List<string> quais)>();
            foreach (FamilyInstance fi in aberturas)
            {
                Room a = null, b = null;
                try { a = fi.FromRoom; b = fi.ToRoom; } catch { }
                // in a curtain wall (or without phase data): the rooms on each side of it
                if (a == null && b == null) LadosAbertura(fi, out a, out b);
                if (a == null && b == null) continue;
                if (c.Exterior && a != null && b != null && a.Id != b.Id) continue;
                if (!cache.TryGetValue(fi.Symbol.Id.Value, out var ar))
                {
                    EsquadriaConfig cfg = ProjectEsquadrias.ConfigOf(fi, out _);
                    if (cfg != null)
                    {
                        try { var lay = Layout.Build(cfg); ar = (lay.AreaIluminacao, lay.AreaVentilacao); }
                        catch { ar = (0, 0); }
                    }
                    else
                    {
                        double vao = TamanhoPainel(fi, out double l, out double h) ? l * h / 1e6 : 0;
                        ar = (vao * c.OutrasI / 100, vao * c.OutrasV / 100);
                    }
                    cache[fi.Symbol.Id.Value] = ar;
                }
                string marca = fi.Symbol.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_MARK)?.AsString();
                foreach (Room r in new[] { a, b }.Where(x => x != null).GroupBy(x => x.Id.Value).Select(g => g.First()))
                {
                    (double il, double ve, List<string> quais) cur = porAmbiente.TryGetValue(r.Id.Value, out var c0) ? c0 : (0.0, 0.0, new List<string>());
                    cur.quais.Add(string.IsNullOrWhiteSpace(marca) ? fi.Symbol.Name : marca);
                    porAmbiente[r.Id.Value] = (cur.il + ar.il, cur.ve + ar.ve, cur.quais);
                }
            }

            // glazed curtain wall panels: lighting only
            if (c.Cortina)
                foreach (Panel pn in new FilteredElementCollector(Doc).OfClass(typeof(Panel)).Cast<Panel>())
                {
                    var tp = Doc.GetElement(pn.GetTypeId()) as ElementType;
                    string tn = (tp?.Name ?? "") + " " + (tp?.FamilyName ?? "");
                    string tl = tn.ToLowerInvariant();
                    if (!(tl.Contains("vidro") || tl.Contains("glass") || tl.Contains("envidra") || tl.Contains("glazed"))) continue;
                    double l = pn.get_Parameter(BuiltInParameter.CURTAIN_WALL_PANELS_WIDTH)?.AsDouble() ?? 0, h = pn.get_Parameter(BuiltInParameter.CURTAIN_WALL_PANELS_HEIGHT)?.AsDouble() ?? 0;
                    double area = l * h * 0.3048 * 0.3048;
                    if (area <= 0) continue;
                    LadosAbertura(pn, out Room a, out Room b);
                    if (c.Exterior && a != null && b != null && a.Id != b.Id) continue;
                    foreach (Room r in new[] { a, b }.Where(x => x != null).GroupBy(x => x.Id.Value).Select(g => g.First()))
                    {
                        (double il, double ve, List<string> quais) cur = porAmbiente.TryGetValue(r.Id.Value, out var c0) ? c0 : (0.0, 0.0, new List<string>());
                        cur.quais.Add("painel de vidro");
                        porAmbiente[r.Id.Value] = (cur.il + area, cur.ve, cur.quais);
                    }
                }

            var br = PecaUtil.Pt;
            var resultado = new Dictionary<long, string>();
            var csv = new StringBuilder("Número;Ambiente;Nível;Classe;Área (m²);Iluminação (m²);Mínimo (m²);Ventilação (m²);Mínimo (m²);Situação;Esquadrias\n");
            var falhas = res.Falhas;
            var linhas = res.Linhas;
            int ok = 0, isentos = 0;
            foreach (Room r in rooms)
            {
                string nome = r.Name ?? "", nl = nome.ToLowerInvariant();
                string classe;
                double di, dv;
                if (isento.Any(k => nl.Contains(k))) { classe = "Isento"; di = dv = 0; isentos++; }
                else if (transit.Any(k => nl.Contains(k))) { classe = "Transitória"; di = c.IlumT; dv = c.VentT; }
                else { classe = "Prolongada"; di = c.IlumP; dv = c.VentP; }
                double area = UnitUtils.ConvertFromInternalUnits(r.Area, UnitTypeId.SquareMeters);
                var tem = porAmbiente.TryGetValue(r.Id.Value, out var v) ? v : (0.0, 0.0, new List<string>());
                double minI = di > 0 ? area / di : 0, minV = dv > 0 ? area / dv : 0;
                string sit;
                if (classe == "Isento") sit = "—";
                else
                {
                    var faltas = new List<string>();
                    if (tem.Item1 + 1e-6 < minI) faltas.Add($"iluminação {(tem.Item1).ToString("0.00", br)} < {minI.ToString("0.00", br)} m²");
                    if (tem.Item2 + 1e-6 < minV) faltas.Add($"ventilação {(tem.Item2).ToString("0.00", br)} < {minV.ToString("0.00", br)} m²");
                    sit = faltas.Count == 0 ? "OK" : "Não atende: " + string.Join("; ", faltas);
                    resultado[r.Id.Value] = faltas.Count == 0 ? "Atende" : "Não atende";
                    if (faltas.Count == 0) ok++;
                    else { falhas.Add(r.Id); linhas.Add($"• {r.Number} {nome} ({area.ToString("0.00", br)} m²): {string.Join("; ", faltas)}"); }
                }
                string Q(string x) => (x ?? "").IndexOfAny(new[] { ';', '"' }) >= 0 ? "\"" + x.Replace("\"", "\"\"") + "\"" : x ?? "";
                csv.AppendLine(string.Join(";", new[] { Q(r.Number), Q(nome), Q(r.Level?.Name), classe, area.ToString("0.00", br), tem.Item1.ToString("0.00", br), minI.ToString("0.00", br),
                    tem.Item2.ToString("0.00", br), minV.ToString("0.00", br), Q(sit), Q(string.Join(", ", tem.Item3.Distinct())) }));
            }
            res.Ok = ok; res.Isentos = isentos; res.Csv = csv.ToString();
            var vistas = (pintar ?? Enumerable.Empty<ViewPlan>()).ToList();
            if (c.Parametro || vistas.Count > 0)
            {
                using (var t = new Transaction(Doc, "JA Famílias – iluminação e ventilação"))
                {
                    t.Start();
                    var op = t.GetFailureHandlingOptions(); op.SetFailuresPreprocessor(new WarningSwallower()); t.SetFailureHandlingOptions(op);
                    if (c.Parametro) res.Gravou = GravarIluminacao(rooms, resultado);
                    foreach (ViewPlan vp in vistas) res.Pintados += PintarIluminacao(vp, rooms, resultado);
                    t.Commit();
                }
            }
            return res;
        }

        protected List<Room> AmbientesColocados() =>
            new FilteredElementCollector(Doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
                .OfType<Room>().Where(r => r.Area > 0 && r.Location != null).OrderBy(r => r.Number, StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>The rooms just inside and outside an opening / panel (half a metre each way along its facing).</summary>
        protected void LadosAbertura(FamilyInstance fi, out Room a, out Room b)
        {
            a = b = null;
            try
            {
                BoundingBoxXYZ bb = fi.get_BoundingBox(null);
                if (bb == null) return;
                XYZ c = (bb.Min + bb.Max) / 2, n = fi.FacingOrientation;
                n = new XYZ(n.X, n.Y, 0);
                if (n.GetLength() < 1e-6) return;
                n = n.Normalize() * (500 / 304.8);
                double z = bb.Min.Z + 1;
                a = Doc.GetRoomAtPoint(new XYZ(c.X + n.X, c.Y + n.Y, z));
                b = Doc.GetRoomAtPoint(new XYZ(c.X - n.X, c.Y - n.Y, z));
            }
            catch { }
        }

        /// <summary>JA_Iluminacao bound to rooms (once) and filled. Inside a transaction.</summary>
        protected bool GravarIluminacao(List<Room> rooms, Dictionary<long, string> resultado)
        {
            try
            {
                if (rooms.Count == 0) return false;
                if (rooms[0].get_Parameter(SharedParams.Iluminacao.Guid) == null)
                {
                    var app = UiApp.Application;
                    SharedParams.WithDefinitions(app, defs =>
                    {
                        CategorySet cs = app.Create.NewCategorySet();
                        cs.Insert(Category.GetCategory(Doc, BuiltInCategory.OST_Rooms));
                        InstanceBinding bind = app.Create.NewInstanceBinding(cs);
                        if (!Doc.ParameterBindings.Insert(defs[SharedParams.Iluminacao.Guid], bind, GroupTypeId.IdentityData))
                            Doc.ParameterBindings.ReInsert(defs[SharedParams.Iluminacao.Guid], bind, GroupTypeId.IdentityData);
                    });
                }
                foreach (Room r in rooms)
                {
                    Parameter p = r.get_Parameter(SharedParams.Iluminacao.Guid);
                    if (p != null && !p.IsReadOnly) p.Set(resultado.TryGetValue(r.Id.Value, out string v) ? v : "Sem exigência");
                }
                return true;
            }
            catch (Exception ex) { Logger.Warn("[JA Famílias] JA_Iluminacao: " + ex.Message); return false; }
        }

        /// <summary>Pale filled regions over the rooms of the plan (old JA ones replaced). Inside a transaction.</summary>
        protected int PintarIluminacao(ViewPlan v, List<Room> rooms, Dictionary<long, string> resultado)
        {
            var velhas = new FilteredElementCollector(Doc, v.Id).OfClass(typeof(FilledRegion)).Where(e => JaDados.Ler(e)?.Tipo == "iluminacao").Select(e => e.Id).ToList();
            if (velhas.Count > 0) Doc.Delete(velhas);
            FillPatternElement solido = new FilteredElementCollector(Doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                .FirstOrDefault(f => f.GetFillPattern().IsSolidFill && f.GetFillPattern().Target == FillPatternTarget.Drafting);
            FilledRegionType Tipo(string nome, Color cor)
            {
                var todos = new FilteredElementCollector(Doc).OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>().ToList();
                FilledRegionType t = todos.FirstOrDefault(x => x.Name == nome);
                if (t == null)
                {
                    if (todos.Count == 0) return null;
                    t = todos[0].Duplicate(nome) as FilledRegionType;
                }
                if (t == null) return null;
                if (solido != null) t.ForegroundPatternId = solido.Id;
                t.ForegroundPatternColor = cor;
                t.BackgroundPatternId = ElementId.InvalidElementId;
                t.IsMasking = false;
                return t;
            }
            FilledRegionType ok = Tipo("JA Atende", new Color(214, 238, 218)), nao = Tipo("JA Não atende", new Color(247, 214, 210));
            if (ok == null || nao == null) return 0;
            var ids = new List<ElementId>();
            foreach (Room r in rooms.Where(x => x.LevelId == v.GenLevel?.Id))
            {
                if (!resultado.TryGetValue(r.Id.Value, out string res)) continue;
                var lacos = new List<CurveLoop>();
                foreach (var laco in Contornos(r))
                {
                    var cl = new CurveLoop();
                    try
                    {
                        foreach (var (a, b, _) in laco)
                            if (a.DistanceTo(b) > 1 / 304.8) cl.Append(Line.CreateBound(a, b));
                        lacos.Add(cl);
                    }
                    catch { }
                }
                if (lacos.Count == 0) continue;
                try
                {
                    FilledRegion fr = FilledRegion.Create(Doc, (res == "Atende" ? ok : nao).Id, v.Id, lacos);
                    JaDados.Gravar(fr, new DadosJa { Tipo = "iluminacao", Ambiente = r.Id.Value, Vista = v.Id.Value });
                    ids.Add(fr.Id);
                }
                catch (Exception ex) { Logger.Warn("[JA Famílias] Região do ambiente " + r.Number + ": " + ex.Message); }
            }
            if (ids.Count > 0) { try { DetailElementOrderUtils.SendToBack(Doc, v, ids); } catch { } }
            return ids.Count;
        }
    }

    /// <summary>
    /// Iluminação e Ventilação: for every placed room, the lighting and ventilation areas of the
    /// windows and doors that open to it (JA families: the areas the configurator computes; other
    /// families: width × height × the factors typed), compared with the minimum fractions of the
    /// floor area of the building code (long-stay and short-stay rooms by name). CSV + the rooms
    /// that fail selected.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class IluminacaoVentilacaoCommand : EsquadriaCommandBase
    {
        public override string Title => "Iluminação e Ventilação";

        protected override Result Run()
        {
            var rooms = AmbientesColocados();
            if (rooms.Count == 0) { Show("Nenhum ambiente colocado no projeto.", ResultKind.Info); return Result.Cancelled; }

            var dlg = new OptionsDialog(Title, "IluminacaoVentilacao", "Conferir");
            dlg.Section("Permanência prolongada (salas, quartos, escritórios…)");
            var fIp = dlg.Number("ilumP", "Iluminação: 1 / … da área do piso", 6, 1, 30, "Ex.: 6 = a área de iluminação precisa ser pelo menos 1/6 da área do ambiente. Confira o código de obras do município.");
            var fVp = dlg.Number("ventP", "Ventilação: 1 / … da área do piso", 12, 1, 60, "Muitos códigos pedem metade da iluminação (1/12).");
            dlg.Section("Permanência transitória");
            var fT = dlg.Text("transit", "Ambientes (palavras no nome)", "banho, banheiro, lavabo, wc, sanitário, serviço, lavanderia, circulação, corredor, hall, escada, depósito, despensa, closet");
            var fIt = dlg.Number("ilumT", "Iluminação: 1 / …", 8, 1, 30);
            var fVt = dlg.Number("ventT", "Ventilação: 1 / …", 16, 1, 60);
            dlg.Section("Sem exigência");
            var fIsento = dlg.Text("isento", "Ambientes que não entram (palavras no nome)", "garagem, shaft, poço, área técnica");
            dlg.Section("Esquadrias");
            var fExt = dlg.Check("exterior", "Contar só as que dão para fora (um lado sem ambiente)", true, "Portas e janelas entre dois ambientes não iluminam nem ventilam (a não ser em código que aceite).");
            var fOutI = dlg.Number("outrasI", "Outras famílias: iluminação = vão × (%)", 80, 0, 100, "Para portas e janelas que não são JA (área do vão = largura × altura).");
            var fOutV = dlg.Number("outrasV", "Outras famílias: ventilação = vão × (%)", 40, 0, 100);
            var fCortina = dlg.Check("cortina", "Painéis de vidro de parede cortina contam como iluminação", true,
                "Painéis cujo tipo tem “vidro”, “glass” ou “envidraçado” no nome; não contam para ventilação.");
            dlg.Section("Resultado");
            var fParam = dlg.Check("parametro", "Gravar o resultado no ambiente (parâmetro JA_Iluminacao)", true, "Dá para pôr numa tabela de ambientes ou num esquema de cores.");
            var fCores = dlg.Check("cores", "Pintar os ambientes na planta ativa (verde atende, vermelho não atende)", Doc.ActiveView is ViewPlan,
                "Regiões preenchidas claras no contorno de cada ambiente; rodar de novo troca as antigas. Apague-as quando não precisar mais.");
            fCores.EnabledWhen(() => Doc.ActiveView is ViewPlan);
            if (!dlg.Show(Owner)) return Result.Cancelled;
            var c = new ConfigIluminacao
            {
                IlumP = fIp.Value, VentP = fVp.Value, Transit = fT.Value, IlumT = fIt.Value, VentT = fVt.Value, Isento = fIsento.Value,
                Exterior = fExt.Value, OutrasI = fOutI.Value, OutrasV = fOutV.Value, Cortina = fCortina.Value, Parametro = fParam.Value
            };
            ToolPrefs.Set("iluminacao.config", Json.Serialize(c));
            ToolPrefs.Save();
            var pintar = fCores.Value && Doc.ActiveView is ViewPlan vp0 ? new[] { vp0 } : new ViewPlan[0];
            ResultadoIluminacao res = ConferirIluminacao(rooms, c, pintar);

            string pasta = Settings.Current.PastaRelatorios;
            Directory.CreateDirectory(pasta);
            string path = Path.Combine(pasta, "Iluminação e ventilação - " + FamilyGenerator.SafeName(string.IsNullOrWhiteSpace(Doc.Title) ? "projeto" : Doc.Title) + ".csv");
            File.WriteAllText(path, res.Csv, new UTF8Encoding(true));
            var falhas = res.Falhas; var linhas = res.Linhas;
            if (falhas.Count > 0) { try { UiDoc.Selection.SetElementIds(falhas); } catch { } }

            Show($"{rooms.Count} ambiente(s): {res.Ok} atendem, {falhas.Count} não atendem, {res.Isentos} sem exigência." +
                 (linhas.Count > 0 ? "\n\n" + string.Join("\n", linhas.Take(25)) + (linhas.Count > 25 ? $"\n… e mais {linhas.Count - 25} (no CSV)." : "") + "\n\nOs ambientes que não atendem ficaram selecionados." : "") +
                 (res.Gravou ? "\nResultado gravado em JA_Iluminacao de cada ambiente." : "") +
                 (res.Pintados > 0 ? $"\n{res.Pintados} ambiente(s) pintado(s) na planta (regiões “JA Atende” / “JA Não atende”); Atualizar Revestimentos repinta depois das mudanças." : "") +
                 "\n\n" + path + "\nFrações conforme digitadas: confira o código de obras do município.",
                 falhas.Count > 0 ? ResultKind.Warning : ResultKind.Success, OpenFolderAction(pasta));
            return Result.Succeeded;
        }
    }

    /// <summary>
    /// Trocar Acabamento das Esquadrias: profile line, colour, glass and/or hardware text of many JA
    /// window and door types at once (the selected ones, or the ticked ones); each family is regenerated.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class TrocarAcabamentoCommand : EsquadriaCommandBase
    {
        public override string Title => "Trocar Acabamento";

        protected override Result Run()
        {
            var tipos = ProjectEsquadrias.All(Doc).Where(x => x.Config != null && x.Symbol != null).ToList();
            if (tipos.Count == 0) { Show("Nenhuma esquadria do JA Famílias neste projeto.", ResultKind.Info); return Result.Cancelled; }
            var sel = new HashSet<long>(UiDoc.Selection.GetElementIds().Select(id => Doc.GetElement(id)).OfType<FamilyInstance>().Select(f => f.Symbol.Id.Value));

            var dlg = new OptionsDialog(Title, "TrocarAcabamento", "Trocar");
            var fTipos = dlg.Multi("tipos", "Esquadrias", tipos.Select(t => $"{t.Codigo} — {Catalog.Perfil(t.Config.PerfilId).Nome}, {Catalog.Cor(t.Config.CorId)?.Nome ?? t.Config.CorHex}, {Catalog.Vidro(t.Config.VidroId).Nome} ({t.Instancias} un.)").ToList(), sel.Count == 0);
            if (sel.Count > 0) for (int i = 0; i < tipos.Count; i++) fTipos.Checked[i] = sel.Contains(tipos[i].Symbol.Id.Value);
            dlg.Section("Trocar para");
            const string manter = "(manter)";
            var fPerfil = dlg.Choice("perfil", "Linha / perfil", new[] { manter }.Concat(Catalog.Perfis.Select(p => p.Nome)).ToList(), 0);
            var fCor = dlg.Choice("cor", "Cor", new[] { manter }.Concat(Catalog.Cores.Select(c => c.Nome)).ToList(), 0);
            var fVidro = dlg.Choice("vidro", "Vidro", new[] { manter }.Concat(Catalog.Vidros.Select(v => v.Nome)).ToList(), 0);
            var fFerr = dlg.Text("ferragens", "Ferragens (vazio = manter)", "");
            fPerfil.Index = 0; fCor.Index = 0; fVidro.Index = 0; fFerr.Value = "";
            dlg.Note("Cada tipo marcado é regerado: todas as cópias dele no projeto mudam juntas. Pode demorar alguns segundos por tipo.");
            if (!dlg.Show(Owner)) return Result.Cancelled;
            var escolhidos = fTipos.SelectedIndices.Select(i => tipos[i]).ToList();
            if (escolhidos.Count == 0 || (fPerfil.Index == 0 && fCor.Index == 0 && fVidro.Index == 0 && string.IsNullOrWhiteSpace(fFerr.Value))) return Result.Cancelled;

            var avisos = new List<string>();
            int feitos = 0, iguais = 0;
            var settings = Settings.Current;
            foreach (var t in escolhidos)
            {
                EsquadriaConfig c = t.Config.Clone();
                if (fPerfil.Index > 0) Catalog.ApplyPerfil(c, Catalog.Perfis[fPerfil.Index - 1]);
                if (fCor.Index > 0) { CorInfo ci = Catalog.Cores[fCor.Index - 1]; c.CorId = ci.Id; c.CorHex = ci.Hex; }
                if (fVidro.Index > 0) c.VidroId = Catalog.Vidros[fVidro.Index - 1].Id;
                if (!string.IsNullOrWhiteSpace(fFerr.Value)) c.Ferragens = fFerr.Value.Trim();
                c = c.Normalized();
                if (c.ToJson() == t.Config.Clone().Normalized().ToJson()) { iguais++; continue; }
                try
                {
                    GenerationResult gen = FamilyGenerator.Generate(UiApp.Application, Doc, c, settings, t.Symbol.Family?.Name, t.Symbol.Name);
                    avisos.AddRange(gen.Warnings.Select(w => t.Codigo + ": " + w));
                    feitos++;
                }
                catch (Exception ex) { avisos.Add($"{t.Codigo}: não atualizada ({ex.Message})."); }
            }
            Show($"{feitos} tipo(s) atualizado(s)" + (iguais > 0 ? $", {iguais} já estavam assim" : "") + "." +
                 (avisos.Count > 0 ? "\n\nAvisos:\n• " + string.Join("\n• ", avisos.Distinct().Take(20)) : ""), avisos.Count > 0 ? ResultKind.Warning : ResultKind.Success);
            return Result.Succeeded;
        }
    }
}
