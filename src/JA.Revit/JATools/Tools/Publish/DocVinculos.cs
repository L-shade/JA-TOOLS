using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using RevitDwgUnifier.Infrastructure;
using RevitDwgUnifier.Tools.Common;

namespace RevitDwgUnifier.Tools.Publish
{
    /// <summary>Kinds of linked document.</summary>
    public static class DocTipo
    {
        public const string Tabela = "tabela", Pdf = "pdf", Word = "word", ExcelImagem = "excelimg";

        public static string Nome(string t)
        {
            switch (t)
            {
                case Tabela: return "Excel (tabela)";
                case ExcelImagem: return "Excel (imagem)";
                case Word: return "Word";
                default: return "PDF";
            }
        }

        public static bool EhImagem(string t) => t != Tabela;

        /// <summary>Kinds a file can become (first = default).</summary>
        public static string[] Possiveis(string caminho)
        {
            switch ((Path.GetExtension(caminho) ?? "").ToLowerInvariant())
            {
                case ".xlsx": case ".xlsm": case ".xls": case ".xlsb": return new[] { Tabela, ExcelImagem };
                case ".csv": case ".txt": return new[] { Tabela };
                case ".pdf": return new[] { Pdf };
                case ".docx": case ".doc": case ".rtf": case ".odt": return new[] { Word };
                default: return new string[0];
            }
        }
    }

    /// <summary>One linked document (Excel/CSV as a native table, PDF/Word/Excel as images).</summary>
    [DataContract]
    public sealed class DocVinculo
    {
        [DataMember(Order = 1)] public string Id = Guid.NewGuid().ToString("N");
        [DataMember(Order = 2)] public string Nome = "";
        [DataMember(Order = 3)] public string Tipo = DocTipo.Tabela;
        [DataMember(Order = 4)] public string Caminho = "";
        [DataMember(Order = 5)] public string Relativo = "";
        [DataMember(Order = 6)] public string Aba = "";
        [DataMember(Order = 7)] public string Intervalo = "";
        [DataMember(Order = 8)] public double Escala = 100;
        [DataMember(Order = 9)] public string Paginas = "";
        [DataMember(Order = 10)] public double LarguraMm;
        [DataMember(Order = 11)] public bool PretoBranco;
        [DataMember(Order = 12)] public bool FonteExcel = true;
        /// <summary>Source file stamp (UTC ticks of last write) and size when last read.</summary>
        [DataMember(Order = 13)] public long Carimbo;
        [DataMember(Order = 14)] public long Tamanho;
        /// <summary>PDF made from Word/Excel (images point to it).</summary>
        [DataMember(Order = 15)] public string Pdf = "";
        /// <summary>UniqueIds: the schedule (table) or the image types (one per page, in order).</summary>
        [DataMember(Order = 16)] public List<string> Elementos = new List<string>();
        [DataMember(Order = 17)] public List<int> PaginasLigadas = new List<int>();
        [DataMember(Order = 18)] public string Atualizado = "";
        [DataMember(Order = 19)] public string IntervaloLido = "";
    }

    [DataContract]
    public sealed class DocRegistro
    {
        [DataMember] public List<DocVinculo> Itens = new List<DocVinculo>();
    }

    /// <summary>Registry of the linked documents, stored in the model (ExtensibleStorage, JSON).</summary>
    public static class DocArmazem
    {
        private static readonly Guid SchemaId = new Guid("8e3f1b6a-2c47-4d19-a5e0-7b9c3d2f4a61");

        private static Schema Esquema()
        {
            Schema s = Schema.Lookup(SchemaId);
            if (s != null) return s;
            var b = new SchemaBuilder(SchemaId);
            b.SetSchemaName("JATools_DocumentosVinculados");
            b.SetReadAccessLevel(AccessLevel.Public);
            b.SetWriteAccessLevel(AccessLevel.Public);
            b.AddSimpleField("Json", typeof(string));
            return b.Finish();
        }

        private static DataStorage Armazem(Document doc, Schema s) =>
            new FilteredElementCollector(doc).OfClass(typeof(DataStorage)).Cast<DataStorage>()
                .FirstOrDefault(d => { try { return d.GetEntity(s).IsValid(); } catch { return false; } });

        public static DocRegistro Ler(Document doc)
        {
            try
            {
                Schema s = Schema.Lookup(SchemaId);
                if (s == null || doc == null || doc.IsFamilyDocument) return new DocRegistro();
                DataStorage ds = Armazem(doc, s);
                string json = ds?.GetEntity(s).Get<string>("Json");
                if (string.IsNullOrWhiteSpace(json)) return new DocRegistro();
                using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                    return (DocRegistro)new DataContractJsonSerializer(typeof(DocRegistro)).ReadObject(ms) ?? new DocRegistro();
            }
            catch (Exception ex) { Logger.Warn("[JA Tools] Documentos vinculados ilegíveis: " + ex.Message); return new DocRegistro(); }
        }

        /// <summary>Inside a transaction.</summary>
        public static void Gravar(Document doc, DocRegistro reg)
        {
            Schema s = Esquema();
            DataStorage ds = Armazem(doc, s);
            if (reg == null || reg.Itens.Count == 0)
            {
                if (ds != null) { try { doc.Delete(ds.Id); } catch { } }
                return;
            }
            if (ds == null) ds = DataStorage.Create(doc);
            string json;
            using (var ms = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(DocRegistro)).WriteObject(ms, reg);
                json = Encoding.UTF8.GetString(ms.ToArray());
            }
            var ent = new Entity(s);
            ent.Set("Json", json);
            ds.SetEntity(ent);
        }
    }

    /// <summary>Word/Excel → PDF with the Office installed on the computer (late-bound COM, no references).</summary>
    public static class OfficePdf
    {
        public static bool Tem(string progId) { try { return Type.GetTypeFromProgID(progId) != null; } catch { return false; } }
        public static bool TemWord => Tem("Word.Application");
        public static bool TemExcel => Tem("Excel.Application");

        private static object Call(object o, string m, params object[] a) => o.GetType().InvokeMember(m, BindingFlags.InvokeMethod, null, o, a);
        private static object Get(object o, string p, params object[] a) => o.GetType().InvokeMember(p, BindingFlags.GetProperty, null, o, a);
        private static void Set(object o, string p, object v) => o.GetType().InvokeMember(p, BindingFlags.SetProperty, null, o, new[] { v });
        private static void Soltar(object o) { try { if (o != null && Marshal.IsComObject(o)) Marshal.FinalReleaseComObject(o); } catch { } }

        /// <summary>Copies the source first: a file open in Word/Excel (or in OneDrive) is still read.</summary>
        private static string Copia(string origem)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "JA_" + Guid.NewGuid().ToString("N").Substring(0, 8) + Path.GetExtension(origem));
            using (var i = new FileStream(origem, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var o = File.Create(tmp)) i.CopyTo(o);
            return tmp;
        }

        public static void Word(string origem, string pdf)
        {
            if (!TemWord) throw new InvalidOperationException("O Microsoft Word não está instalado neste computador.");
            string tmp = Copia(origem);
            object app = null, docs = null, d = null;
            try
            {
                app = Activator.CreateInstance(Type.GetTypeFromProgID("Word.Application"));
                Set(app, "Visible", false);
                try { Set(app, "DisplayAlerts", 0); } catch { }
                docs = Get(app, "Documents");
                d = Call(docs, "Open", tmp, false, true); // FileName, ConfirmConversions, ReadOnly
                if (File.Exists(pdf)) File.Delete(pdf);
                Call(d, "ExportAsFixedFormat", pdf, 17); // wdExportFormatPDF
                Call(d, "Close", false);
            }
            finally
            {
                Soltar(d); Soltar(docs);
                if (app != null) { try { Call(app, "Quit", false); } catch { } Soltar(app); }
                try { File.Delete(tmp); } catch { }
            }
        }

        /// <summary>One worksheet (and range, if given) to PDF as Excel prints it.</summary>
        public static void Excel(string origem, string aba, string intervalo, string pdf)
        {
            if (!TemExcel) throw new InvalidOperationException("O Microsoft Excel não está instalado neste computador.");
            ComExcel(origem, (wb, ws) =>
            {
                if (!string.IsNullOrWhiteSpace(intervalo))
                {
                    object ps = Get(ws, "PageSetup");
                    try { Set(ps, "PrintArea", intervalo.Trim()); } finally { Soltar(ps); }
                }
                if (File.Exists(pdf)) File.Delete(pdf);
                Call(ws, "ExportAsFixedFormat", 0, pdf); // xlTypePDF
            }, aba);
        }

        /// <summary>.xls/.xlsb → .xlsx (temporary), so the table reader can read it.</summary>
        public static string ParaXlsx(string origem)
        {
            if (!TemExcel) throw new InvalidOperationException("Arquivos " + Path.GetExtension(origem) + " precisam do Excel instalado (ou salve como .xlsx).");
            string dest = Path.Combine(Path.GetTempPath(), "JA_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".xlsx");
            ComExcel(origem, (wb, ws) => Call(wb, "SaveAs", dest, 51), null);
            return dest;
        }

        private static void ComExcel(string origem, Action<object, object> acao, string aba)
        {
            string tmp = Copia(origem);
            object app = null, wbs = null, wb = null, sheets = null, ws = null;
            try
            {
                app = Activator.CreateInstance(Type.GetTypeFromProgID("Excel.Application"));
                Set(app, "Visible", false);
                Set(app, "DisplayAlerts", false);
                wbs = Get(app, "Workbooks");
                wb = Call(wbs, "Open", tmp, 0, true); // Filename, UpdateLinks, ReadOnly
                sheets = Get(wb, "Worksheets");
                if (!string.IsNullOrWhiteSpace(aba)) { try { ws = Get(sheets, "Item", aba); } catch { ws = null; } }
                if (ws == null) ws = Get(wb, "ActiveSheet");
                acao(wb, ws);
                Call(wb, "Close", false);
            }
            finally
            {
                Soltar(ws); Soltar(sheets); Soltar(wb); Soltar(wbs);
                if (app != null) { try { Call(app, "Quit"); } catch { } Soltar(app); }
                try { File.Delete(tmp); } catch { }
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
    }

    /// <summary>Creates, updates and checks the linked documents. Every method that changes the model runs inside a transaction.</summary>
    public static class DocMotor
    {
        public const string EstadoOk = "Atualizado", EstadoMudou = "Mudou", EstadoSemArquivo = "Arquivo não encontrado";

        // ------------------------------------------------------------------ files

        /// <summary>The file to read: the saved path, or the relative one next to the project.</summary>
        public static string Arquivo(Document doc, DocVinculo v)
        {
            if (!string.IsNullOrEmpty(v.Caminho) && File.Exists(v.Caminho)) return v.Caminho;
            string pasta = PastaProjeto(doc);
            if (pasta != null && !string.IsNullOrEmpty(v.Relativo))
            {
                try
                {
                    string p = Path.GetFullPath(Path.Combine(pasta, v.Relativo));
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
            return null;
        }

        private static string PastaProjeto(Document doc)
        {
            try { return string.IsNullOrEmpty(doc.PathName) || doc.IsModelInCloud ? null : Path.GetDirectoryName(doc.PathName); }
            catch { return null; }
        }

        public static void DefinirCaminho(Document doc, DocVinculo v, string caminho)
        {
            v.Caminho = Path.GetFullPath(caminho);
            v.Relativo = "";
            string pasta = PastaProjeto(doc);
            if (pasta == null) return;
            try
            {
                var a = new Uri(pasta.TrimEnd('\\', '/') + Path.DirectorySeparatorChar);
                var b = new Uri(v.Caminho);
                if (a.Scheme == b.Scheme && string.Equals(Path.GetPathRoot(pasta), Path.GetPathRoot(v.Caminho), StringComparison.OrdinalIgnoreCase))
                    v.Relativo = Uri.UnescapeDataString(a.MakeRelativeUri(b).ToString()).Replace('/', Path.DirectorySeparatorChar);
            }
            catch { }
        }

        public static string Estado(Document doc, DocVinculo v)
        {
            string f = Arquivo(doc, v);
            if (f == null) return EstadoSemArquivo;
            try
            {
                var fi = new FileInfo(f);
                return fi.LastWriteTimeUtc.Ticks == v.Carimbo && fi.Length == v.Tamanho ? EstadoOk : EstadoMudou;
            }
            catch { return EstadoSemArquivo; }
        }

        public static bool ElementosExistem(Document doc, DocVinculo v) => v.Elementos.Any(u => doc.GetElement(u) != null);

        private static void Carimbar(string f, DocVinculo v)
        {
            var fi = new FileInfo(f);
            v.Carimbo = fi.LastWriteTimeUtc.Ticks;
            v.Tamanho = fi.Length;
            v.Atualizado = DateTime.Now.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
        }

        /// <summary>Reads the worksheet (converting .xls with Excel when needed).</summary>
        public static PlTabela LerTabela(string arquivo, DocVinculo v)
        {
            string ext = Path.GetExtension(arquivo).ToLowerInvariant();
            if (ext == ".xls" || ext == ".xlsb")
            {
                string tmp = OfficePdf.ParaXlsx(arquivo);
                try { return PlanilhaLeitor.Ler(tmp, v.Aba, v.Intervalo); }
                finally { try { File.Delete(tmp); } catch { } }
            }
            return PlanilhaLeitor.Ler(arquivo, v.Aba, v.Intervalo);
        }

        /// <summary>PDF to link: the file itself, or one made by Word/Excel in "_JA Vínculos" next to it.</summary>
        private static string PrepararPdf(string arquivo, DocVinculo v)
        {
            if (v.Tipo == DocTipo.Pdf) return arquivo;
            string nome = Path.GetFileNameWithoutExtension(arquivo) + (v.Tipo == DocTipo.ExcelImagem && !string.IsNullOrWhiteSpace(v.Aba) ? " - " + v.Aba : "") + ".pdf";
            nome = string.Concat(nome.Split(Path.GetInvalidFileNameChars()));
            string pasta = Path.Combine(Path.GetDirectoryName(arquivo), "_JA Vínculos");
            try { Directory.CreateDirectory(pasta); File.WriteAllText(Path.Combine(pasta, ".teste"), ""); File.Delete(Path.Combine(pasta, ".teste")); }
            catch
            {
                pasta = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JA Tools", "Vinculos", v.Id);
                Directory.CreateDirectory(pasta);
            }
            string pdf = Path.Combine(pasta, nome);
            if (v.Tipo == DocTipo.Word) OfficePdf.Word(arquivo, pdf);
            else OfficePdf.Excel(arquivo, v.Aba, v.Intervalo, pdf);
            v.Pdf = pdf;
            return pdf;
        }

        /// <summary>"1-3, 5" → pages; empty = all (null).</summary>
        public static List<int> Paginas(string texto)
        {
            if (string.IsNullOrWhiteSpace(texto)) return null;
            var set = new SortedSet<int>();
            foreach (string parte in texto.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] ab = parte.Split('-');
                if (ab.Length == 2 && int.TryParse(ab[0], out int a) && int.TryParse(ab[1], out int b))
                { for (int i = Math.Min(a, b); i <= Math.Max(a, b) && i <= Math.Min(a, b) + 500; i++) if (i > 0) set.Add(i); }
                else if (int.TryParse(parte, out int p) && p > 0) set.Add(p);
            }
            return set.Count == 0 ? null : set.ToList();
        }

        // ------------------------------------------------------------------ create

        /// <summary>
        /// Creates the Revit elements of a new link and stores it. Tables go on the sheet at <paramref name="ponto"/>
        /// (or stay in the browser if the view is not a sheet); images go in <paramref name="vista"/>.
        /// Returns the view to open (or null).
        /// </summary>
        public static View Criar(Document doc, DocVinculo v, View vista, XYZ ponto, List<string> problemas)
        {
            string arquivo = Arquivo(doc, v) ?? throw new FileNotFoundException("Arquivo não encontrado: " + v.Caminho);
            if (string.IsNullOrWhiteSpace(v.Nome)) v.Nome = Path.GetFileNameWithoutExtension(arquivo) + (string.IsNullOrWhiteSpace(v.Aba) || v.Tipo == DocTipo.Pdf || v.Tipo == DocTipo.Word ? "" : " - " + v.Aba);
            View abrir = null;
            if (v.Tipo == DocTipo.Tabela)
            {
                PlTabela tab = LerTabela(arquivo, v);
                ViewSchedule vs = NovaTabela(doc, v.Nome);
                Preencher(doc, vs, tab, v);
                v.IntervaloLido = tab.Intervalo;
                v.Elementos = new List<string> { vs.UniqueId };
                if (vista is ViewSheet folha) ScheduleSheetInstance.Create(doc, folha.Id, vs.Id, ponto ?? XYZ.Zero);
                else abrir = vs;
            }
            else
            {
                string pdf = PrepararPdf(arquivo, v);
                if (!PodeImagem(vista)) { vista = NovaVistaDesenho(doc, v.Nome); ponto = XYZ.Zero; abrir = vista; }
                List<int> pags = Paginas(v.Paginas);
                var tipos = new List<ImageType>();
                var paginas = new List<int>();
                int limite = pags == null ? 200 : pags.Count;
                for (int k = 0; k < limite; k++)
                {
                    int p = pags == null ? k + 1 : pags[k];
                    ImageType it;
                    try { it = ImageType.Create(doc, Opcoes(pdf, p)); }
                    catch (Exception ex)
                    {
                        if (pags != null) problemas.Add($"Página {p}: {ex.Message}");
                        if (pags == null) break; // past the last page
                        continue;
                    }
                    tipos.Add(it);
                    paginas.Add(p);
                }
                if (tipos.Count == 0) throw new InvalidOperationException("Nenhuma página pôde ser lida do PDF.");
                Colocar(doc, vista, ponto ?? XYZ.Zero, tipos, v.LarguraMm);
                v.Elementos = tipos.Select(t => t.UniqueId).ToList();
                v.PaginasLigadas = paginas;
                foreach (var t in tipos) { try { t.Name = Unico(doc, typeof(ImageType), $"JA Doc - {v.Nome} - p{paginas[tipos.IndexOf(t)]}"); } catch { } }
            }
            Carimbar(arquivo, v);
            DocRegistro reg = DocArmazem.Ler(doc);
            reg.Itens.RemoveAll(x => x.Id == v.Id);
            reg.Itens.Add(v);
            DocArmazem.Gravar(doc, reg);
            return abrir;
        }

        private static bool PodeImagem(View v) =>
            v != null && !v.IsTemplate && (v is ViewSheet || v.ViewType == ViewType.DraftingView || v.ViewType == ViewType.Legend ||
                                           v is ViewPlan || v.ViewType == ViewType.Section || v.ViewType == ViewType.Elevation || v.ViewType == ViewType.Detail);

        private static ViewDrafting NovaVistaDesenho(Document doc, string nome)
        {
            ViewFamilyType vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(t => t.ViewFamily == ViewFamily.Drafting);
            ViewDrafting vd = ViewDrafting.Create(doc, vft.Id);
            try { vd.Name = Unico(doc, typeof(View), "JA Doc - " + nome); } catch { }
            try { vd.Scale = 1; } catch { }
            return vd;
        }

        private static ImageTypeOptions Opcoes(string pdf, int pagina)
        {
            var o = new ImageTypeOptions(pdf, false, ImageTypeSource.Link);
            if (string.Equals(Path.GetExtension(pdf), ".pdf", StringComparison.OrdinalIgnoreCase)) o.PageNumber = pagina;
            try { o.Resolution = 150; } catch { }
            return o;
        }

        /// <summary>Pages side by side from the top-left point, 10 mm apart.</summary>
        private static void Colocar(Document doc, View vista, XYZ ponto, List<ImageType> tipos, double larguraMm)
        {
            double x = ponto.X;
            double gap = 10 / 304.8 * (vista is ViewSheet ? 1 : Math.Max(1, vista.Scale));
            foreach (ImageType t in tipos)
            {
                ImageInstance ii = ImageInstance.Create(doc, vista, t.Id, new ImagePlacementOptions(new XYZ(x, ponto.Y, ponto.Z), BoxPlacement.TopLeft));
                if (larguraMm > 0) { try { ii.Width = larguraMm / 304.8 * (vista is ViewSheet ? 1 : Math.Max(1, vista.Scale)); } catch { } }
                x += ii.Width + gap;
            }
        }

        private static string Unico(Document doc, Type classe, string nome)
        {
            nome = RevitUtil.Sanitize(nome);
            var nomes = new HashSet<string>(new FilteredElementCollector(doc).OfClass(classe).Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
            string n = nome;
            for (int i = 2; nomes.Contains(n); i++) n = $"{nome} ({i})";
            return n;
        }

        // ------------------------------------------------------------------ native table (schedule header)

        private const string FiltroVazio = "§JA-DOC-SEM-LINHAS§";

        /// <summary>Multi-category schedule whose body never has rows; the table lives in the header.</summary>
        private static ViewSchedule NovaTabela(Document doc, string nome)
        {
            ViewSchedule vs = ViewSchedule.CreateSchedule(doc, ElementId.InvalidElementId);
            ScheduleDefinition def = vs.Definition;
            SchedulableField sf = def.GetSchedulableFields().FirstOrDefault(f => f.ParameterId == new ElementId(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS))
                                  ?? def.GetSchedulableFields().FirstOrDefault(f => f.FieldType == ScheduleFieldType.Instance);
            if (sf != null)
            {
                ScheduleField fld = def.AddField(sf);
                try { def.AddFilter(new ScheduleFilter(fld.FieldId, ScheduleFilterType.Equal, FiltroVazio)); } catch { }
                fld.IsHidden = true;
            }
            def.ShowHeaders = false;
            def.ShowTitle = true;
            try { def.ShowGridLines = false; } catch { }
            try { vs.Name = Unico(doc, typeof(View), "JA Doc - " + nome); } catch { }
            return vs;
        }

        private static ElementId[] _linhas;
        private static Document _linhasDoc;

        /// <summary>Line styles for cell borders: [0] invisible, [1] thin, [2] medium, [3] thick (created once: "JA Tabela - …").</summary>
        private static ElementId[] EstilosBorda(Document doc)
        {
            if (_linhas != null && _linhasDoc == doc && _linhas.All(id => doc.GetElement(id) != null)) return _linhas;
            Category linhas = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
            ElementId Estilo(string nome, int peso)
            {
                Category c = linhas.SubCategories.Cast<Category>().FirstOrDefault(x => x.Name == nome);
                if (c == null)
                {
                    c = doc.Settings.Categories.NewSubcategory(linhas, nome);
                    c.SetLineWeight(peso, GraphicsStyleType.Projection);
                    c.LineColor = new Color(0, 0, 0);
                    try { c.SetLinePatternId(LinePatternElement.GetSolidPatternId(), GraphicsStyleType.Projection); } catch { }
                    doc.Regenerate();
                }
                return c.GetGraphicsStyle(GraphicsStyleType.Projection).Id;
            }
            Category inv = linhas.SubCategories.Cast<Category>().FirstOrDefault(x => x.Id.Value == (long)BuiltInCategory.OST_InvisibleLines);
            _linhas = new[]
            {
                inv?.GetGraphicsStyle(GraphicsStyleType.Projection)?.Id ?? ElementId.InvalidElementId,
                Estilo("JA Tabela - fina", 1), Estilo("JA Tabela - média", 3), Estilo("JA Tabela - grossa", 5)
            };
            _linhasDoc = doc;
            return _linhas;
        }

        /// <summary>Rebuilds the header of <paramref name="vs"/> with the worksheet: sizes, texts, styles, merges.</summary>
        public static void Preencher(Document doc, ViewSchedule vs, PlTabela t, DocVinculo v)
        {
            ElementId[] bordas = EstilosBorda(doc);
            double k = Math.Max(0.1, v.Escala / 100.0);
            TableSectionData h = vs.GetTableData().GetSectionData(SectionType.Header);
            if (h.NumberOfColumns < 1)
            {
                // some versions tie the header to the visible body columns: show the (empty) field
                ScheduleDefinition def = vs.Definition;
                for (int i = 0; i < def.GetFieldCount(); i++) { try { def.GetField(i).IsHidden = false; } catch { } }
                vs.RefreshData();
                h = vs.GetTableData().GetSectionData(SectionType.Header);
            }

            // reset to 1 × 1, then grow
            while (h.NumberOfRows > 1) h.RemoveRow(h.LastRowNumber);
            while (h.NumberOfColumns > 1) h.RemoveColumn(h.LastColumnNumber);
            for (int j = 1; j < t.Colunas; j++) h.InsertColumn(h.FirstColumnNumber);
            for (int i = 1; i < t.Linhas; i++) h.InsertRow(h.LastRowNumber + 1);
            vs.RefreshData();
            int r0 = h.FirstRowNumber, c0 = h.FirstColumnNumber;

            // clear merges left by the title row
            for (int i = 0; i < t.Linhas; i++)
                for (int j = 0; j < t.Colunas; j++)
                {
                    try
                    {
                        TableMergedCell m = h.GetMergedCell(r0 + i, c0 + j);
                        if (m.Top != m.Bottom || m.Left != m.Right) h.SetMergedCell(r0 + i, c0 + j, new TableMergedCell(r0 + i, c0 + j, r0 + i, c0 + j));
                    }
                    catch { }
                }

            for (int j = 0; j < t.Colunas; j++) h.SetColumnWidth(c0 + j, Math.Max(0.5, t.LargurasMm[j] * k) / 304.8);
            for (int i = 0; i < t.Linhas; i++) { try { h.SetRowHeight(r0 + i, Math.Max(0.5, t.AlturasMm[i] * k) / 304.8); } catch { } }

            var dentroMescla = new HashSet<(int, int)>();
            foreach (PlMescla m in t.Mesclas)
                for (int i = m.Linha1; i <= m.Linha2; i++)
                    for (int j = m.Coluna1; j <= m.Coluna2; j++)
                        if (i != m.Linha1 || j != m.Coluna1) dentroMescla.Add((i, j));

            PropertyInfo fundoFolha = typeof(TableCellStyle).GetProperty("SheetBackgroundColor");
            for (int i = 0; i < t.Linhas; i++)
                for (int j = 0; j < t.Colunas; j++)
                {
                    int r = r0 + i, c = c0 + j;
                    PlCelula cel = t.Celulas[i, j];
                    try { h.SetCellText(r, c, dentroMescla.Contains((i, j)) ? "" : cel.Texto ?? ""); } catch { }
                    if (!h.AllowOverrideCellStyle(r, c)) continue;
                    var st = new TableCellStyle();
                    var op = new TableCellStyleOverrideOptions
                    {
                        Bold = true, Italics = true, Underline = true, FontSize = true, HorizontalAlignment = true, VerticalAlignment = true,
                        BorderTopLineStyle = true, BorderBottomLineStyle = true, BorderLeftLineStyle = true, BorderRightLineStyle = true,
                        Font = v.FonteExcel, FontColor = !v.PretoBranco && cel.Cor >= 0, BackgroundColor = !v.PretoBranco && cel.Fundo >= 0,
                        TextOrientation = cel.Rotacao != 0
                    };
                    st.SetCellStyleOverrideOptions(op);
                    st.IsFontBold = cel.Negrito;
                    st.IsFontItalic = cel.Italico;
                    st.IsFontUnderline = cel.Sublinhado;
                    // Excel points (em height) → Revit text size (≈ 0.7 em), in the unit TextSize uses (1/96 in)
                    double mm = cel.FontePt * 25.4 / 72 * 0.7 * k;
                    st.TextSize = mm * 96 / 25.4;
                    if (v.FonteExcel && !string.IsNullOrEmpty(cel.Fonte)) st.FontName = cel.Fonte;
                    st.FontHorizontalAlignment = cel.AlinhH == 1 ? HorizontalAlignmentStyle.Center : cel.AlinhH == 2 ? HorizontalAlignmentStyle.Right : HorizontalAlignmentStyle.Left;
                    st.FontVerticalAlignment = cel.AlinhV == 0 ? VerticalAlignmentStyle.Top : cel.AlinhV == 1 ? VerticalAlignmentStyle.Middle : VerticalAlignmentStyle.Bottom;
                    if (op.FontColor) st.TextColor = Cor(cel.Cor);
                    if (op.BackgroundColor)
                    {
                        st.BackgroundColor = Cor(cel.Fundo);
                        try { fundoFolha?.SetValue(st, Cor(cel.Fundo)); } catch { }
                    }
                    if (cel.Rotacao != 0) st.TextOrientation = cel.Rotacao * 10;
                    st.BorderLeftLineStyle = bordas[Math.Max(0, Math.Min(3, cel.BordaE))];
                    st.BorderRightLineStyle = bordas[Math.Max(0, Math.Min(3, cel.BordaD))];
                    st.BorderTopLineStyle = bordas[Math.Max(0, Math.Min(3, cel.BordaC))];
                    st.BorderBottomLineStyle = bordas[Math.Max(0, Math.Min(3, cel.BordaB))];
                    try { h.SetCellStyle(r, c, st); } catch { }
                }

            foreach (PlMescla m in t.Mesclas)
            {
                try { h.MergeCells(new TableMergedCell(r0 + m.Linha1, c0 + m.Coluna1, r0 + m.Linha2, c0 + m.Coluna2)); }
                catch (Exception ex) { Logger.Warn("[Documentos Vinculados] mesclar: " + ex.Message); }
            }
            vs.RefreshData();
        }

        private static Color Cor(int rgb) => new Color((byte)((rgb >> 16) & 255), (byte)((rgb >> 8) & 255), (byte)(rgb & 255));

        // ------------------------------------------------------------------ update

        /// <summary>Reads the source again and refreshes the Revit elements (inside a transaction).</summary>
        public static bool Atualizar(Document doc, DocVinculo v, List<string> problemas)
        {
            string arquivo = Arquivo(doc, v);
            if (arquivo == null) { problemas.Add($"{v.Nome}: arquivo não encontrado ({v.Caminho})."); return false; }
            try
            {
                if (v.Tipo == DocTipo.Tabela)
                {
                    ViewSchedule vs = v.Elementos.Select(u => doc.GetElement(u)).OfType<ViewSchedule>().FirstOrDefault();
                    if (vs == null) { problemas.Add($"{v.Nome}: a tabela foi apagada do projeto."); return false; }
                    PlTabela tab = LerTabela(arquivo, v);
                    Preencher(doc, vs, tab, v);
                    v.IntervaloLido = tab.Intervalo;
                }
                else
                {
                    string pdf = PrepararPdf(arquivo, v);
                    var tipos = v.Elementos.Select(u => doc.GetElement(u) as ImageType).ToList();
                    if (tipos.All(x => x == null)) { problemas.Add($"{v.Nome}: as imagens foram apagadas do projeto."); return false; }
                    for (int i = 0; i < tipos.Count; i++)
                    {
                        if (tipos[i] == null) continue;
                        int p = i < v.PaginasLigadas.Count ? v.PaginasLigadas[i] : i + 1;
                        try { tipos[i].ReloadFrom(Opcoes(pdf, p)); }
                        catch (Exception ex) { problemas.Add($"{v.Nome}, página {p}: {ex.Message}"); }
                    }
                    // pages added at the end of the document (when linked as "all")
                    if (string.IsNullOrWhiteSpace(v.Paginas))
                    {
                        ImageInstance ultima = Instancias(doc, v).LastOrDefault();
                        var novos = new List<ImageType>();
                        for (int p = (v.PaginasLigadas.Count == 0 ? 0 : v.PaginasLigadas.Max()) + 1; p < 200; p++)
                        {
                            ImageType it;
                            try { it = ImageType.Create(doc, Opcoes(pdf, p)); } catch { break; }
                            novos.Add(it);
                            v.PaginasLigadas.Add(p);
                            v.Elementos.Add(it.UniqueId);
                            try { it.Name = Unico(doc, typeof(ImageType), $"JA Doc - {v.Nome} - p{p}"); } catch { }
                        }
                        if (novos.Count > 0 && ultima != null && doc.GetElement(ultima.OwnerViewId) is View vw)
                        {
                            BoundingBoxXYZ bb = ultima.get_BoundingBox(vw);
                            XYZ pt = bb != null ? new XYZ(bb.Max.X + 10 / 304.8, bb.Max.Y, 0) : XYZ.Zero;
                            Colocar(doc, vw, pt, novos, v.LarguraMm);
                            problemas.Add($"{v.Nome}: {novos.Count} página(s) nova(s) colocada(s) ao lado.");
                        }
                    }
                }
                Carimbar(arquivo, v);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn("[Documentos Vinculados] " + v.Nome + ": " + ex);
                problemas.Add($"{v.Nome}: {ex.Message}");
                return false;
            }
        }

        /// <summary>Updates the given links in one transaction; returns how many succeeded.</summary>
        public static int AtualizarVarios(Document doc, IEnumerable<string> ids, List<string> problemas)
        {
            int n = 0;
            using (var t = new Transaction(doc, "JA Tools - Atualizar documentos vinculados"))
            {
                t.Start();
                RevitDwgUnifier.Export.FailureSwallower.Attach(t);
                DocRegistro reg = DocArmazem.Ler(doc);
                var alvo = new HashSet<string>(ids);
                foreach (DocVinculo v in reg.Itens.Where(x => alvo.Contains(x.Id)))
                {
                    using (var st = new SubTransaction(doc))
                    {
                        st.Start();
                        if (Atualizar(doc, v, problemas)) { st.Commit(); n++; }
                        else st.RollBack();
                    }
                }
                DocArmazem.Gravar(doc, reg);
                t.Commit();
            }
            return n;
        }

        public static List<ImageInstance> Instancias(Document doc, DocVinculo v)
        {
            var tipos = new HashSet<ElementId>(v.Elementos.Select(u => doc.GetElement(u)?.Id).Where(id => id != null));
            return new FilteredElementCollector(doc).OfClass(typeof(ImageInstance)).Cast<ImageInstance>().Where(i => tipos.Contains(i.GetTypeId())).ToList();
        }

        /// <summary>Sheets / views where the link appears.</summary>
        public static List<View> Onde(Document doc, DocVinculo v)
        {
            var vistas = new List<View>();
            if (v.Tipo == DocTipo.Tabela)
            {
                var ids = new HashSet<ElementId>(v.Elementos.Select(u => doc.GetElement(u)?.Id).Where(id => id != null));
                foreach (ScheduleSheetInstance s in new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>())
                    if (ids.Contains(s.ScheduleId) && doc.GetElement(s.OwnerViewId) is View vw) vistas.Add(vw);
            }
            else
                foreach (ImageInstance i in Instancias(doc, v))
                    if (doc.GetElement(i.OwnerViewId) is View vw) vistas.Add(vw);
            return vistas.GroupBy(x => x.Id).Select(g => g.First()).ToList();
        }

        /// <summary>Deletes the Revit elements of the link and forgets it (inside a transaction).</summary>
        public static void Remover(Document doc, string id)
        {
            DocRegistro reg = DocArmazem.Ler(doc);
            DocVinculo v = reg.Itens.FirstOrDefault(x => x.Id == id);
            if (v == null) return;
            foreach (string u in v.Elementos)
            {
                Element e = doc.GetElement(u);
                if (e != null) { try { doc.Delete(e.Id); } catch { } }
            }
            reg.Itens.Remove(v);
            DocArmazem.Gravar(doc, reg);
        }

        /// <summary>Saves edited settings of an existing link and refreshes it (inside a transaction).</summary>
        public static bool Salvar(Document doc, DocVinculo editado, List<string> problemas)
        {
            DocRegistro reg = DocArmazem.Ler(doc);
            int i = reg.Itens.FindIndex(x => x.Id == editado.Id);
            if (i < 0) return false;
            DocVinculo antigo = reg.Itens[i];
            editado.Elementos = antigo.Elementos;
            editado.PaginasLigadas = antigo.PaginasLigadas;
            if (editado.Tipo != antigo.Tipo) { problemas.Add("Para trocar o tipo (tabela × imagem), crie um vínculo novo."); editado.Tipo = antigo.Tipo; }
            bool ok = Atualizar(doc, editado, problemas);
            reg.Itens[i] = editado;
            DocArmazem.Gravar(doc, reg);
            return ok;
        }

        /// <summary>Links whose source changed (or is missing), for the check when a project opens.</summary>
        public static List<DocVinculo> Mudados(Document doc) =>
            DocArmazem.Ler(doc).Itens.Where(v => ElementosExistem(doc, v) && Estado(doc, v) == EstadoMudou).ToList();
    }

    /// <summary>When a project opens: if linked documents changed, ask once to update them (one click).</summary>
    public static class DocAviso
    {
        private static readonly Queue<Document> Fila = new Queue<Document>();
        private static readonly HashSet<string> JaAvisados = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static void Iniciar(UIControlledApplication app)
        {
            app.ControlledApplication.DocumentOpened += (s, e) =>
            {
                try { if (e.Document != null && !e.Document.IsFamilyDocument && !e.Document.IsLinked) Fila.Enqueue(e.Document); } catch { }
            };
            app.Idling += (s, e) =>
            {
                if (Fila.Count == 0) return;
                Document doc = Fila.Dequeue();
                try { Verificar(s as UIApplication, doc); }
                catch (Exception ex) { Logger.Warn("[Documentos Vinculados] verificação: " + ex.Message); }
            };
        }

        private static void Verificar(UIApplication uiapp, Document doc)
        {
            if (doc == null || !doc.IsValidObject || doc.IsReadOnly) return;
            string chave = doc.PathName ?? doc.Title;
            if (JaAvisados.Contains(chave)) return;
            List<DocVinculo> mudados = DocMotor.Mudados(doc);
            if (mudados.Count == 0) return;
            JaAvisados.Add(chave);
            var td = new TaskDialog("JA Tools — Documentos Vinculados")
            {
                MainInstruction = mudados.Count == 1 ? "Um documento vinculado mudou" : $"{mudados.Count} documentos vinculados mudaram",
                MainContent = string.Join("\n", mudados.Take(8).Select(v => "• " + v.Nome + "  (" + Path.GetFileName(v.Caminho) + ")")) + (mudados.Count > 8 ? $"\n… e mais {mudados.Count - 8}" : ""),
                CommonButtons = TaskDialogCommonButtons.Close
            };
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Atualizar agora", "Lê os arquivos de novo e atualiza as tabelas e imagens no projeto.");
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Depois", "Fica marcado como “Mudou” em Pranchas › Documentos Vinculados.");
            if (td.Show() != TaskDialogResult.CommandLink1) return;
            var problemas = new List<string>();
            int n = DocMotor.AtualizarVarios(doc, mudados.Select(v => v.Id), problemas);
            if (problemas.Count > 0)
                TaskDialog.Show("JA Tools — Documentos Vinculados", $"{n} de {mudados.Count} atualizado(s).\n\n" + string.Join("\n", problemas.Take(10)));
        }
    }

    /// <summary>
    /// Documentos Vinculados (like Ideate Sticky): Excel/CSV as native Revit tables, PDF/Word/Excel as
    /// linked images; one place to see, update, re-point and remove them.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class DocumentosVinculadosCommand : ToolCommand
    {
        public override string Title => "Documentos Vinculados";
        protected override bool PicksInView => true;

        protected override Result Run()
        {
            var window = new DocVinculosWindow(Doc, UiDoc.ActiveView);
            if (Owner != IntPtr.Zero) new System.Windows.Interop.WindowInteropHelper(window).Owner = Owner;
            window.ShowDialog();

            if (window.IrPara != null) { TryOpen(window.IrPara); return Result.Succeeded; }
            DocVinculo novo = window.Novo;
            if (novo == null) return Result.Succeeded;

            View vista = UiDoc.ActiveView;
            XYZ ponto = null;
            bool naFolha = vista is ViewSheet;
            bool imagemAqui = DocTipo.EhImagem(novo.Tipo) && !(vista is View3D) && !(vista is ViewSchedule);
            if (naFolha || imagemAqui)
            {
                try { ponto = RevitUtil.PickPoint(UiDoc, "Clique onde ficará o canto superior esquerdo (Esc = canto da folha)"); }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { ponto = null; }
            }
            if (ponto == null && naFolha) ponto = CantoFolha((ViewSheet)vista);

            var problemas = new List<string>();
            View abrir;
            System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            try
            {
                using (var t = new Transaction(Doc, "JA Tools - " + Title))
                {
                    t.Start();
                    RevitDwgUnifier.Export.FailureSwallower.Attach(t);
                    abrir = DocMotor.Criar(Doc, novo, vista, ponto, problemas);
                    t.Commit();
                }
            }
            finally { System.Windows.Input.Mouse.OverrideCursor = null; }
            if (abrir != null) TryOpen(abrir);
            string msg = $"“{novo.Nome}” vinculado ({DocTipo.Nome(novo.Tipo)}" +
                         (novo.Tipo == DocTipo.Tabela ? $", {novo.IntervaloLido}" : $", {novo.PaginasLigadas.Count} página(s)") + ").";
            if (novo.Tipo == DocTipo.Tabela && !naFolha) msg += "\n\nA tabela está no navegador de projeto (Tabelas): arraste-a para uma folha.";
            if (problemas.Count > 0) msg += "\n\n" + string.Join("\n", problemas.Take(10));
            if (problemas.Count > 0 || abrir != null) Show(msg);
            return Result.Succeeded;
        }

        private static XYZ CantoFolha(ViewSheet folha)
        {
            try
            {
                BoundingBoxUV o = folha.Outline;
                return new XYZ(o.Min.U + 20 / 304.8, o.Max.V - 20 / 304.8, 0);
            }
            catch { return XYZ.Zero; }
        }
    }
}
