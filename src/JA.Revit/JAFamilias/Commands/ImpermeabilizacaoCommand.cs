using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using JAEsquadrias.Core;
using JAEsquadrias.Engine;
using JAEsquadrias.Infrastructure;
using Settings = JAEsquadrias.Core.Settings;

namespace JAEsquadrias.Commands
{
    /// <summary>Settings of the impermeabilização take-off (kept in the preferences for the Lista de Compras).</summary>
    [DataContract]
    public sealed class ConfigImper
    {
        [DataMember(Order = 1)] public string Palavras { get; set; }
        [DataMember(Order = 2)] public double Subida { get; set; }      // rise at the walls (mm)
        [DataMember(Order = 3)] public double SubidaBox { get; set; }  // rise inside the shower (mm)
        [DataMember(Order = 4)] public bool DescontarPortas { get; set; }
        [DataMember(Order = 5)] public int Sistema { get; set; }               // 0 argamassa polimérica, 1 manta líquida, 2 manta asfáltica
        [DataMember(Order = 6)] public double Consumo { get; set; }        // kg/m² (0, 1) or overlap % (2)
        [DataMember(Order = 7)] public double Perda { get; set; }

        public static readonly string[] Sistemas = { "Argamassa polimérica (kg/m²)", "Manta líquida / membrana acrílica (kg/m²)", "Manta asfáltica (m², com sobreposição)" };
        public string NomeSistema => Sistema == 1 ? "Manta líquida" : Sistema == 2 ? "Manta asfáltica" : "Argamassa polimérica";
        public string[] Chaves() => (Palavras ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0).ToArray();

        public static ConfigImper Salva()
        {
            try { return Json.Deserialize<ConfigImper>(ToolPrefs.Get("impermeabilizacao.config")) ?? new ConfigImper(); } catch { return new ConfigImper(); }
        }

        public ConfigImper() { Padroes(); }

        /// <summary>DataContract does not run the constructor: the defaults for fields missing in older JSON.</summary>
        [System.Runtime.Serialization.OnDeserializing] private void AoLer(System.Runtime.Serialization.StreamingContext c) => Padroes();

        private void Padroes()
        {
            Palavras = "banho, banheiro, wc, lavanderia, área de serviço, serviço, varanda, sacada, terraço, cozinha, gourmet, churrasqueira, piscina";
            Subida = 300;
            SubidaBox = 1800;
            DescontarPortas = true;
            Consumo = 4;
            Perda = 10;
        }
    }

    public abstract partial class EsquadriaCommandBase
    {
        protected sealed class ImperAmbiente
        {
            public Room Room;
            public string Nome;
            public double PisoM2, PerimetroM, PortasM, SubidaM2, BoxParedeM, BoxM2;
            public int Boxes;
            public double TotalM2 => PisoM2 + SubidaM2 + BoxM2;
        }

        /// <summary>Rooms whose name has one of the words (wet areas).</summary>
        protected List<Room> AmbientesMolhados(ConfigImper c) =>
            new FilteredElementCollector(Doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().OfType<Room>()
                .Where(r => r.Area > 0 && c.Chaves().Any(k => (r.Name ?? "").ToLowerInvariant().Contains(k)))
                .OrderBy(r => r.Level?.ProjectElevation ?? 0).ThenBy(r => r.Number).ToList();

        /// <summary>
        /// Impermeabilização take-off of each room: the floor, the rise along the walls (less the doors)
        /// and, at each JA box, the shower walls up to the box height.
        /// </summary>
        protected List<ImperAmbiente> CalcularImper(List<Room> rooms, ConfigImper c)
        {
            var res = new List<ImperAmbiente>();
            var portas = new FilteredElementCollector(Doc).OfCategory(BuiltInCategory.OST_Doors).WhereElementIsNotElementType().OfType<FamilyInstance>().ToList();
            var boxes = new FilteredElementCollector(Doc).OfCategory(BuiltInCategory.OST_GenericModel).WhereElementIsNotElementType().OfType<FamilyInstance>()
                .Select(fi => new { Fi = fi, C = ProjectPecas.ConfigOf(fi, out _) as VidroConfig }).Where(x => x.C != null && x.C.Uso == UsoVidro.Box).ToList();
            double sub = c.Subida / 1000.0, subBox = Math.Max(0, c.SubidaBox - c.Subida) / 1000.0;
            foreach (Room r in rooms)
            {
                var a = new ImperAmbiente { Room = r, Nome = (r.Number + " " + r.Name).Trim(), PisoM2 = r.Area * 0.09290304 };
                var lacos = Contornos(r);
                a.PerimetroM = lacos.Sum(l => l.Sum(s => new XYZ(s.a.X - s.b.X, s.a.Y - s.b.Y, 0).GetLength())) * 0.3048;
                if (c.DescontarPortas)
                    foreach (FamilyInstance d in portas)
                    {
                        bool daqui = false;
                        try { daqui = d.FromRoom?.Id == r.Id || d.ToRoom?.Id == r.Id; } catch { }
                        if (!daqui) continue;
                        a.PortasM += LarguraPorta(d);
                    }
                a.SubidaM2 = Math.Max(0, a.PerimetroM - a.PortasM) * sub;
                double z = (r.Level?.ProjectElevation ?? 0) + r.BaseOffset + 1;
                foreach (var b in boxes)
                {
                    Transform T = b.Fi.GetTransform();
                    XYZ y = T.BasisY.Normalize(), x = T.BasisX.Normalize(), o = T.Origin;
                    double W = b.C.ComprimentoMm / 304.8;
                    XYZ dentro = b.C.Canto ? o + x * (W / 2) + y * (b.C.LadoSinal * 0.5) : o + y * 0.5;
                    if (!r.IsPointInRoom(new XYZ(dentro.X, dentro.Y, z))) continue;
                    a.Boxes++;
                    double paredes;
                    if (b.C.Canto) paredes = (b.C.ComprimentoMm + b.C.LadoFixoMm) / 1000.0;   // the two walls behind the glass legs
                    else
                    {
                        // depth of the shower: from the glass to the wall in front of it (+Y)
                        double prof = Raio(lacos, new XYZ(o.X, o.Y, 0), new XYZ(y.X, y.Y, 0));
                        if (prof <= 0 || prof > 10) prof = 0.9 / 0.3048;
                        paredes = (b.C.ComprimentoMm / 1000.0) + 2 * prof * 0.3048;
                    }
                    a.BoxParedeM += paredes;
                }
                a.BoxM2 = a.BoxParedeM * subBox;
                res.Add(a);
            }
            return res;
        }

        /// <summary>Door width (m), as the rodapé reads it (JA or not); 0,80 m when unknown.</summary>
        protected static double LarguraPorta(FamilyInstance d) => TamanhoPainel(d, out double lmm, out _) ? lmm / 1000 : 0.8;

        /// <summary>Distance (ft) from p along d to the first boundary stretch of the room (0 = none).</summary>
        protected static double Raio(List<List<(XYZ a, XYZ b, ElementId el)>> lacos, XYZ p, XYZ d)
        {
            double best = double.MaxValue;
            foreach (var l in lacos)
                foreach (var s in l)
                {
                    XYZ e = new XYZ(s.b.X - s.a.X, s.b.Y - s.a.Y, 0), w = new XYZ(s.a.X - p.X, s.a.Y - p.Y, 0);
                    double den = d.X * e.Y - d.Y * e.X;
                    if (Math.Abs(den) < 1e-9) continue;
                    double t = (w.X * e.Y - w.Y * e.X) / den, u = (w.X * d.Y - w.Y * d.X) / den;
                    if (t > 0.05 && u >= -1e-6 && u <= 1 + 1e-6 && t < best) best = t;
                }
            return best == double.MaxValue ? 0 : best;
        }

        /// <summary>Product of the system for an area (kg or m²), with the waste.</summary>
        protected static double ProdutoImper(ConfigImper c, double m2) =>
            c.Sistema == 2 ? m2 * (1 + c.Consumo / 100) * (1 + c.Perda / 100) : m2 * c.Consumo * (1 + c.Perda / 100);
    }

    /// <summary>
    /// Impermeabilização por Ambiente: wet rooms (by name, or the selected ones) → floor, rise at the
    /// walls (less the doors) and the shower walls at the JA boxes, with the product of the chosen
    /// system, as an Excel file. The settings are used by the Lista de Compras too.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class ImpermeabilizacaoCommand : EsquadriaCommandBase
    {
        public override string Title => "Impermeabilização";

        protected override Result Run()
        {
            var c0 = ConfigImper.Salva();
            var selecionados = UiDoc.Selection.GetElementIds().Select(id => Doc.GetElement(id)).Select(e => e is RoomTag t ? t.Room : e as Room).Where(r => r != null && r.Area > 0).Distinct().ToList();
            var dlg = new OptionsDialog(Title, "Impermeabilizacao", "Calcular");
            dlg.Section("Ambientes");
            OptionsDialog.TextField fPal = null;
            if (selecionados.Count > 0) dlg.Note(selecionados.Count + " ambiente(s) selecionado(s): só eles entram.");
            else fPal = dlg.Text("palavras", "Áreas molhadas: nomes que contêm (separe por vírgula)", c0.Palavras);
            dlg.Section("Alturas");
            var fSub = dlg.Number("subida", "Subida nas paredes (mm)", c0.Subida, 0, 3000, "NBR 9575 / 9574: pelo menos 30 cm acima do piso acabado nas áreas molhadas.");
            var fBox = dlg.Number("subidaBox", "Subida dentro do box (mm)", c0.SubidaBox, 0, 3000, "Paredes do chuveiro (boxes do JA Famílias): usual 1,80 m ou até o teto.");
            var fPortas = dlg.Check("portas", "Descontar a largura das portas na subida", c0.DescontarPortas);
            dlg.Section("Produto");
            var fSis = dlg.Choice("sistema", "Sistema", ConfigImper.Sistemas, c0.Sistema);
            var fCons = dlg.Number("consumo", "Consumo (kg/m²) ou sobreposição da manta (%)", c0.Consumo, 0, 50,
                "Argamassa polimérica: cerca de 4 kg/m² (3 a 4 demãos). Manta líquida: 1,5 a 2,5 kg/m². Manta asfáltica: 10 a 15% de sobreposição.");
            var fPerda = dlg.Number("perda", "Perda (%)", c0.Perda, 0, 50);
            if (!dlg.Show(Owner)) return Result.Cancelled;
            var c = new ConfigImper
            {
                Palavras = fPal?.Value ?? c0.Palavras, Subida = fSub.Value, SubidaBox = fBox.Value, DescontarPortas = fPortas.Value,
                Sistema = fSis.Index, Consumo = fCons.Value, Perda = fPerda.Value
            };
            ToolPrefs.Set("impermeabilizacao.config", Json.Serialize(c));
            ToolPrefs.Save();

            var rooms = selecionados.Count > 0 ? selecionados : AmbientesMolhados(c);
            if (rooms.Count == 0) { Show("Nenhum ambiente com esses nomes. Selecione os ambientes antes, ou mude as palavras.", ResultKind.Info); return Result.Cancelled; }
            var lista = CalcularImper(rooms, c);

            var br = PecaUtil.Pt;
            string N(double v, string f = "0.00") => v.ToString(f, br);
            string unidade = c.Sistema == 2 ? "m²" : "kg";
            var tab = new XlsxWriter.Table { Name = "Impermeabilização", Title = "Impermeabilização — " + (string.IsNullOrWhiteSpace(Doc.Title) ? "projeto" : Doc.Title) + " · " + c.NomeSistema };
            tab.Rows.Add(new[] { "Ambiente", "Nível", "Piso (m²)", "Perímetro (m)", "Portas (m)", "Subida " + N(c.Subida, "0") + " mm (m²)", "Boxes", "Paredes do box (m)", "Box até " + N(c.SubidaBox, "0") + " mm (m²)", "Total (m²)", c.NomeSistema + " (" + unidade + ")" });
            foreach (var a in lista)
                tab.Rows.Add(new[] { a.Nome, a.Room.Level?.Name ?? "", N(a.PisoM2), N(a.PerimetroM), N(a.PortasM), N(a.SubidaM2), a.Boxes.ToString(), N(a.BoxParedeM), N(a.BoxM2), N(a.TotalM2), N(ProdutoImper(c, a.TotalM2), "0.0") });
            double tot = lista.Sum(a => a.TotalM2);
            tab.Rows.Add(new[] { "Total", "", N(lista.Sum(a => a.PisoM2)), N(lista.Sum(a => a.PerimetroM)), N(lista.Sum(a => a.PortasM)), N(lista.Sum(a => a.SubidaM2)), lista.Sum(a => a.Boxes).ToString(),
                                 N(lista.Sum(a => a.BoxParedeM)), N(lista.Sum(a => a.BoxM2)), N(tot), N(ProdutoImper(c, tot), "0.0") });
            string folder = Settings.Current.PastaRelatorios;
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "Impermeabilização - " + FamilyGenerator.SafeName(string.IsNullOrWhiteSpace(Doc.Title) ? "projeto" : Doc.Title) + ".xlsx");
            var xw = new XlsxWriter();
            xw.Add(tab);
            try { xw.Save(path); }
            catch (IOException) { path = Path.Combine(folder, Path.GetFileNameWithoutExtension(path) + " " + DateTime.Now.ToString("HHmmss") + ".xlsx"); xw.Save(path); }

            var sb = new StringBuilder();
            sb.AppendLine($"{lista.Count} área(s) molhada(s): {N(tot)} m² a impermeabilizar → {c.NomeSistema}: {N(ProdutoImper(c, tot), "0.0")} {unidade} (com {N(c.Perda, "0")}% de perda).");
            sb.AppendLine();
            foreach (var a in lista)
                sb.AppendLine($"• {a.Nome}: piso {N(a.PisoM2)} + subida {N(a.SubidaM2)}" + (a.Boxes > 0 ? $" + box {N(a.BoxM2)}" : "") + $" = {N(a.TotalM2)} m²");
            int semBox = lista.Count(a => a.Boxes == 0 && a.Nome.ToLowerInvariant().Contains("banh"));
            if (semBox > 0) sb.AppendLine($"\n{semBox} banheiro(s) sem box do JA Famílias: as paredes do chuveiro não entraram (só a subida de {N(c.Subida, "0")} mm).");
            sb.AppendLine();
            sb.AppendLine(path);
            sb.AppendLine("Estimativa de quantidade: o sistema e as camadas (regularização, proteção mecânica, reforços nos ralos e cantos) vêm do projeto de impermeabilização.");
            Show(sb.ToString().TrimEnd(), ResultKind.Success, OpenFolderAction(folder));
            return Result.Succeeded;
        }
    }
}
