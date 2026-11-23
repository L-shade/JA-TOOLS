using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using RevitDwgUnifier.Infrastructure;

namespace RevitDwgUnifier.Tools.Views
{
    /// <summary>One line of the information table under the drawings ("Descrição | Porta de correr").</summary>
    [DataContract]
    public sealed class LegendRow
    {
        [DataMember] public string Label { get; set; }
        /// <summary>A parameter name, or one of the special sources (LegendSources).</summary>
        [DataMember] public string Source { get; set; }

        public LegendRow() { }
        public LegendRow(string label, string source) { Label = label; Source = source; }
        public LegendRow Clone() => new LegendRow(Label, Source);
    }

    /// <summary>Values computed by the tool (not parameters).</summary>
    public static class LegendSources
    {
        public const string Quantity = "[Quantidade no projeto]";
        public const string Location = "[Localização: ambientes]";
        public const string Size = "[Largura × Altura]";
        public const string Family = "[Família]";
        public const string TypeName = "[Nome do tipo]";
        public const string Mark = "[Marca de tipo]";
        public const string Description = "[Descrição]";
        public static readonly string[] All = { Description, Quantity, Location, Size, Mark, Family, TypeName };
    }

    /// <summary>
    /// The look of one legend sheet ("ficha"): where the mark goes, frame, size, which views,
    /// dimensions and the information table. Built-in presets plus the user's own, saved in
    /// %AppData%\RevitDwgUnifier\legend_templates.json.
    /// All sizes are in millimetres on the printed sheet.
    /// </summary>
    [DataContract]
    public sealed class LegendTemplate
    {
        [DataMember] public string Name { get; set; } = "Ficha";
        [DataMember] public bool BuiltIn { get; set; }

        // ---- layout
        /// <summary>true = mark in a column on the left (caderno de esquadrias); false = above the drawings.</summary>
        [DataMember] public bool MarkLeft { get; set; } = true;
        [DataMember] public double LeftColumnMm { get; set; } = 45;
        [DataMember] public bool Frame { get; set; } = true;
        [DataMember] public bool Divider { get; set; } = true;
        /// <summary>Fixed card size (all legends equal, they tile on the sheet). false = fits the content.</summary>
        [DataMember] public bool FixedSize { get; set; } = true;
        [DataMember] public double WidthMm { get; set; } = 180;
        [DataMember] public double HeightMm { get; set; } = 120;
        [DataMember] public double PaddingMm { get; set; } = 5;

        // ---- mark and size
        [DataMember] public bool ShowMark { get; set; } = true;
        [DataMember] public bool MarkUnderline { get; set; } = true;
        [DataMember] public bool ShowSize { get; set; } = true;
        [DataMember] public string SizeFormat { get; set; } = "{L} m x {A} m";
        [DataMember] public string MarkTextType { get; set; }
        [DataMember] public string SizeTextType { get; set; }
        [DataMember] public string BodyTextType { get; set; }

        // ---- drawings
        [DataMember] public List<string> Views { get; set; } = new List<string> { "front", "plan" };
        [DataMember] public int DetailLevel { get; set; } = 3;
        [DataMember] public double ViewGapMm { get; set; } = 10;
        [DataMember] public double HostLengthM { get; set; } = 1.0;
        [DataMember] public bool FloorLine { get; set; } = true;
        /// <summary>Draw every type from model views (sections around an instance), even when legends accept it.</summary>
        [DataMember] public bool ForceModel { get; set; }
        [DataMember] public int Scale { get; set; } = 25;
        /// <summary>Kept for templates saved by older versions (ScaleMode replaces it).</summary>
        [DataMember] public bool AutoScale { get; set; } = true;
        /// <summary>0 = fixed scale, 1 = reduce only when it does not fit, 2 = largest scale that fits.</summary>
        [DataMember] public int ScaleMode { get; set; } = 2;
        /// <summary>With modes 1/2: one scale for every legend (the one that fits all of them).</summary>
        [DataMember] public bool SameScale { get; set; } = true;

        // ---- dimensions
        [DataMember] public bool DimWidth { get; set; } = true;
        [DataMember] public bool DimHeight { get; set; } = true;
        [DataMember] public bool DimSill { get; set; } = true;
        /// <summary>Depth: under the side elevation, or beside the plan (cabinets, furniture).</summary>
        [DataMember] public bool DimDepth { get; set; }
        [DataMember] public string DimType { get; set; }

        // ---- style
        /// <summary>0 = ficha (caderno de esquadrias); 1 = relação (bancadas / granito): table on top
        /// (especificação, compartimento, quantidade), drawing with the typical details on the right,
        /// "TOTAL" line at the bottom. Cards are stacked on the sheet.</summary>
        [DataMember] public int Layout { get; set; }
        [DataMember] public string HeadSpec { get; set; } = "ESPECIFICAÇÃO";
        [DataMember] public string HeadComp { get; set; } = "COMPARTIMENTO";
        [DataMember] public string HeadQty { get; set; } = "QUANTIDADE";
        /// <summary>Especificação: a parameter or a calculated value (LegendSources).</summary>
        [DataMember] public string SpecSource { get; set; } = LegendSources.Description;
        /// <summary>Compartimento: [Localização: ambientes] or a parameter of the instance (then of the type).</summary>
        [DataMember] public string CompSource { get; set; } = LegendSources.Location;
        /// <summary>One compartment row per distinct compartment, each with its own quantity.</summary>
        [DataMember] public bool CompPerRoom { get; set; }
        [DataMember] public string UnitName { get; set; } = "UNIDADE";
        [DataMember] public string UnitPlural { get; set; } = "UNIDADES";
        /// <summary>"1 x 6 PAV. = 6 BANCAS" when every level has the same number of pieces.</summary>
        [DataMember] public bool QtyByFloor { get; set; } = true;
        [DataMember] public string FloorWord { get; set; } = "PAV.";
        [DataMember] public bool ShowTotal { get; set; } = true;
        [DataMember] public double MarkColMm { get; set; } = 18;
        [DataMember] public double SpecColPct { get; set; } = 50;
        [DataMember] public double CompColPct { get; set; } = 34;
        [DataMember] public double HeadRowMm { get; set; } = 6;
        /// <summary>Share of the drawing area (right side) for the typical details.</summary>
        [DataMember] public double DetailsPct { get; set; } = 45;
        /// <summary>Typical details used when a type has none of its own ("Família : Tipo" or "Família").</summary>
        [DataMember] public List<string> Details { get; set; } = new List<string>();

        // ---- table
        [DataMember] public List<LegendRow> Rows { get; set; } = new List<LegendRow>();
        [DataMember] public bool TableLines { get; set; } = true;
        [DataMember] public bool ShowLabels { get; set; } = true;
        [DataMember] public double LabelColumnPct { get; set; } = 25;
        [DataMember] public double ValueColumnPct { get; set; } = 48;
        [DataMember] public double RowHeightMm { get; set; } = 6;

        public LegendTemplate Clone()
        {
            var t = (LegendTemplate)MemberwiseClone();
            t.Views = new List<string>(Views ?? new List<string>());
            t.Rows = (Rows ?? new List<LegendRow>()).Select(r => r.Clone()).ToList();
            t.Details = new List<string>(Details ?? new List<string>());
            t.BuiltIn = false;
            return t;
        }

        [OnDeserializing]
        private void Defaults(StreamingContext c)
        {
            // fields added in later versions keep a sensible value in old files
            Views = new List<string> { "front", "plan" };
            Rows = new List<LegendRow>();
            LeftColumnMm = 45; WidthMm = 180; HeightMm = 120; PaddingMm = 5; SizeFormat = "{L} m x {A} m";
            DetailLevel = 3; ViewGapMm = 10; HostLengthM = 1; Scale = 25; AutoScale = true; ScaleMode = 2; SameScale = true;
            LabelColumnPct = 25; ValueColumnPct = 48; RowHeightMm = 6; MarkLeft = true; Frame = true; Divider = true;
            FixedSize = true; ShowMark = true; MarkUnderline = true; ShowSize = true; FloorLine = true;
            DimWidth = true; DimHeight = true; DimSill = true; TableLines = true; ShowLabels = true;
            Layout = 0; HeadSpec = "ESPECIFICAÇÃO"; HeadComp = "COMPARTIMENTO"; HeadQty = "QUANTIDADE";
            SpecSource = LegendSources.Description; CompSource = LegendSources.Location; UnitName = "UNIDADE"; UnitPlural = "UNIDADES";
            QtyByFloor = true; FloorWord = "PAV."; ShowTotal = true; MarkColMm = 18; SpecColPct = 50; CompColPct = 34; HeadRowMm = 6;
            DetailsPct = 45; Details = new List<string>();
        }

        // ================================================================== presets
        public static List<LegendTemplate> Presets()
        {
            var ficha = new LegendTemplate
            {
                Name = "Ficha – marca à esquerda, com moldura", BuiltIn = true,
                Rows = new List<LegendRow>
                {
                    new LegendRow("Descrição", LegendSources.Description),
                    new LegendRow("Quantidade", LegendSources.Quantity),
                    new LegendRow("Localização", LegendSources.Location)
                }
            };
            var semMoldura = ficha.Clone();
            semMoldura.Name = "Ficha – marca à esquerda, sem moldura";
            semMoldura.BuiltIn = true;
            semMoldura.Frame = false;

            var compacto = new LegendTemplate
            {
                Name = "Ficha compacta – marca acima, sem tabela", BuiltIn = true,
                MarkLeft = false, Frame = false, Divider = false, FixedSize = false, MarkUnderline = false,
                FloorLine = false, DimSill = false, TableLines = false, ShowLabels = false, Scale = 50, AutoScale = false, ScaleMode = 0,
                Rows = new List<LegendRow>
                {
                    new LegendRow("", LegendSources.Size),
                    new LegendRow("Quantidade", LegendSources.Quantity)
                },
                ShowSize = false
            };
            compacto.ShowLabels = true;

            var gabinete = ficha.Clone();
            gabinete.Name = "Ficha – três vistas (frente, lateral e planta)";
            gabinete.BuiltIn = true;
            gabinete.Views = new List<string> { "front", "right", "plan" };
            gabinete.DimDepth = true;
            gabinete.SizeFormat = "{L} x {P} x {A} m";
            gabinete.Rows = new List<LegendRow>
            {
                new LegendRow("Descrição", LegendSources.Description),
                new LegendRow("Dimensões (L x P x A)", LegendSources.Size),
                new LegendRow("Quantidade", LegendSources.Quantity),
                new LegendRow("Localização", LegendSources.Location)
            };
            // relação de bancadas: one card per bancada, three per A4 sheet, plan + typical details
            var bancadas = new LegendTemplate
            {
                Name = "Relação – tabela, desenho e detalhes típicos", BuiltIn = true, Layout = 1,
                ShowMark = false, MarkUnderline = false, ShowSize = false, Frame = true, Divider = false,
                Views = new List<string> { "plan" }, DimWidth = true, DimHeight = false, DimSill = false, DimDepth = true,
                FloorLine = false, Scale = 50, ScaleMode = 1, SameScale = true, PaddingMm = 4, DetailLevel = 3,
                SpecSource = LegendSources.Description, CompSource = LegendSources.Location,
                UnitName = "UNIDADE", UnitPlural = "UNIDADES", QtyByFloor = true,
                Details = new List<string> { "Det.Frontispício", "Det.Saia" }
            };
            // relação de granito: tento, chapim, peitoril, soleira, filete (mark column, one row per compartment)
            var granito = bancadas.Clone();
            granito.Name = "Relação – com marca e uma linha por compartimento";
            granito.BuiltIn = true;
            granito.ShowMark = true;
            granito.CompPerRoom = true;
            granito.UnitName = "UNIDADE";
            granito.UnitPlural = "UNIDADES";
            granito.Details = new List<string> { "Det.Tento Simples" };
            granito.SpecColPct = 45;
            granito.CompColPct = 37;
            return new List<LegendTemplate> { ficha, semMoldura, gabinete, compacto, bancadas, granito };
        }

        // ================================================================== user templates
        private static string StorePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                                        "RevitDwgUnifier", "legend_templates.json");

        [DataContract]
        private sealed class Store
        {
            [DataMember] public List<LegendTemplate> Templates { get; set; } = new List<LegendTemplate>();
            [DataMember] public string Last { get; set; }
        }

        /// <summary>Presets first, then the user's own templates.</summary>
        public static List<LegendTemplate> LoadAll(out string last)
        {
            var list = Presets();
            last = null;
            try
            {
                if (File.Exists(StorePath))
                {
                    using (var fs = File.OpenRead(StorePath))
                    {
                        var s = (Store)new DataContractJsonSerializer(typeof(Store)).ReadObject(fs);
                        foreach (var t in s.Templates ?? new List<LegendTemplate>())
                        {
                            t.BuiltIn = false;
                            list.RemoveAll(x => !x.BuiltIn && x.Name == t.Name);
                            list.Add(t);
                        }
                        last = Renamed(s.Last);
                    }
                }
            }
            catch (Exception ex) { Logger.Warn("[JA Tools] Modelos de legenda não lidos: " + ex.Message); }
            return list;
        }

        /// <summary>Built-in templates renamed in 1.1 (the "last used" saved by older versions still opens).</summary>
        private static string Renamed(string name)
        {
            switch (name)
            {
                case "Ficha com moldura (caderno de esquadrias)": return "Ficha – marca à esquerda, com moldura";
                case "Ficha sem moldura (marca à esquerda)": return "Ficha – marca à esquerda, sem moldura";
                case "Gabinetes e mobiliário (frente, lateral e planta)": return "Ficha – três vistas (frente, lateral e planta)";
                case "Compacto (marca acima, sem tabela)": return "Ficha compacta – marca acima, sem tabela";
                case "Relação de bancadas (granito)": return "Relação – tabela, desenho e detalhes típicos";
                case "Relação de granito (tento, chapim, peitoril, soleira)": return "Relação – com marca e uma linha por compartimento";
                default: return name;
            }
        }

        public static void SaveAll(IEnumerable<LegendTemplate> all, string last)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StorePath));
                var s = new Store { Templates = all.Where(t => !t.BuiltIn).ToList(), Last = last };
                using (var ms = new MemoryStream())
                {
                    new DataContractJsonSerializer(typeof(Store)).WriteObject(ms, s);
                    File.WriteAllText(StorePath, Encoding.UTF8.GetString(ms.ToArray()), new UTF8Encoding(false));
                }
            }
            catch (Exception ex) { Logger.Warn("[JA Tools] Modelos de legenda não gravados: " + ex.Message); }
        }
    }

    /// <summary>Put the legends on sheets (one cell per legend, like a caderno de esquadrias).</summary>
    public sealed class SheetPlacement
    {
        public bool Enabled;
        public Autodesk.Revit.DB.ElementId TitleBlock = Autodesk.Revit.DB.ElementId.InvalidElementId;
        public int Rows = 2, Columns = 1;
        public double MarginLeftMm = 10, MarginTopMm = 10, MarginRightMm = 10, MarginBottomMm = 25;
        /// <summary>Card size computed from the sheet (rows × columns fill the usable area).</summary>
        public bool FitCards = true;
        public string FirstNumber = "A101";
        public string Name = "CADERNO DE ESQUADRIA";
        public bool HideViewportTitle = true;
        /// <summary>Framed note row above the title block on every sheet ("OBS.: CONFERIR MEDIDAS NO LOCAL...").</summary>
        public string Note = "";
        public double NoteRowMm = 8;
        public double NoteHeightMm => string.IsNullOrWhiteSpace(Note) ? 0 : NoteRowMm;
    }
}
