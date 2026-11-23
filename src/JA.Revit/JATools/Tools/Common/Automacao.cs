using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using RevitDwgUnifier.Contracts;
using RevitDwgUnifier.Infrastructure;

namespace RevitDwgUnifier.Tools.Common
{
    // ============================================================================ model (Glyph-like)

    /// <summary>A tool with its options filled in, saved to run again with one click.</summary>
    [DataContract]
    public sealed class Tarefa
    {
        [DataMember(Order = 1)] public string Id = Guid.NewGuid().ToString("N");
        [DataMember(Order = 2)] public string Nome = "";
        /// <summary>ToolCatalog title of the tool ("Plantas por Nível").</summary>
        [DataMember(Order = 3)] public string Ferramenta = "";
        /// <summary>Preference keys of the tool ("PlantasPorNivel.escala") and their values.</summary>
        [DataMember(Order = 4)] public Dictionary<string, string> Valores = new Dictionary<string, string>();
        /// <summary>The tool has its own window: it opens filled in and waits for the user.</summary>
        [DataMember(Order = 5)] public bool Interativa;
        [DataMember(Order = 6)] public string Descricao = "";
        [DataMember(Order = 7)] public string Criada = "";
        /// <summary>Read-only: came from the office folder.</summary>
        public bool DoEscritorio;
    }

    /// <summary>Several tasks (or plain tools) run in order with one click.</summary>
    [DataContract]
    public sealed class Pacote
    {
        [DataMember(Order = 1)] public string Id = Guid.NewGuid().ToString("N");
        [DataMember(Order = 2)] public string Nome = "";
        /// <summary>Task ids, or "ferramenta:Title" for a tool run as it is.</summary>
        [DataMember(Order = 3)] public List<string> Passos = new List<string>();
        [DataMember(Order = 4)] public bool PararNoErro = true;
        [DataMember(Order = 5)] public string Descricao = "";
        public bool DoEscritorio;
    }

    /// <summary>Unified selection: where the tools work.</summary>
    [DataContract]
    public sealed class Escopo
    {
        public const int Nenhum = 0, VistaAtiva = 1, Selecionados = 2, Filtrar = 3;
        [DataMember(Order = 1)] public int Modo;
        [DataMember(Order = 2)] public List<string> Categorias = new List<string>();
        [DataMember(Order = 3)] public List<string> Niveis = new List<string>();
        [DataMember(Order = 4)] public string ScopeBox = "";
        [DataMember(Order = 5)] public string Filtro = "";
        [DataMember(Order = 6)] public List<long> Excluidos = new List<long>();
    }

    [DataContract]
    public sealed class Biblioteca
    {
        [DataMember(Order = 1)] public List<Tarefa> Tarefas = new List<Tarefa>();
        [DataMember(Order = 2)] public List<Pacote> Pacotes = new List<Pacote>();
        [DataMember(Order = 3)] public List<string> Favoritos = new List<string>();
        [DataMember(Order = 4)] public Escopo Escopo = new Escopo();
    }

    /// <summary>One step of a run in the status report.</summary>
    public sealed class StatusPasso
    {
        public const string Ok = "ok", Aviso = "aviso", Erro = "erro", Cancelado = "cancelado";
        public string Nome = "", Ferramenta = "", Estado = Ok, Texto = "";
        public List<ElementId> Criados = new List<ElementId>();
        public List<ElementId> VistasCriadas = new List<ElementId>();
        public double Segundos;
    }

    public sealed class StatusExecucao
    {
        public string Titulo = "";
        public DateTime Quando = DateTime.Now;
        public List<StatusPasso> Passos = new List<StatusPasso>();
        public string Resumo => $"{Passos.Count(p => p.Estado == StatusPasso.Ok)} ok · {Passos.Count(p => p.Estado == StatusPasso.Aviso)} aviso · " +
                                $"{Passos.Count(p => p.Estado == StatusPasso.Erro)} erro · {Passos.Count(p => p.Estado == StatusPasso.Cancelado)} cancelado";
    }

    // ============================================================================ runtime state + runner

    /// <summary>
    /// Central JA (Glyph-like): tasks, bundles, unified selection and status report. The tools stay
    /// the same; while a task runs, the options dialog does not open (the task's values are used),
    /// result windows go to the status report and the lists follow the scope.
    /// </summary>
    public static class Automacao
    {
        // ------------------------------------------------------------------ state read by OptionsDialog / ToolCommand
        public static bool Ativa;
        public static bool SemJanela;
        public static Tarefa EditandoTarefa;
        public static bool TarefaSalva;
        public static bool DialogoVisto;
        public static HashSet<string> NomesEscopo;
        private static readonly List<(string titulo, string texto, ResultKind kind)> _mensagens = new List<(string, string, ResultKind)>();
        public static readonly List<StatusExecucao> Historico = new List<StatusExecucao>();

        public static void Capturar(string titulo, string texto, ResultKind kind) => _mensagens.Add((titulo, texto ?? "", kind));

        /// <summary>A list item (room, level, view, sheet...) is in the scope when one of its names appears in it as a whole word.</summary>
        public static bool CorrespondeEscopo(string item)
        {
            if (NomesEscopo == null || string.IsNullOrWhiteSpace(item)) return false;
            string f = TextUtil.Fold(item).Trim();
            foreach (string n in NomesEscopo)
            {
                if (n.Length == 0) continue;
                if (f == n) return true;
                if (n.Length >= 2 && Regex.IsMatch(f, @"(^|[^\p{L}\p{N}])" + Regex.Escape(n) + @"($|[^\p{L}\p{N}])")) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ library
        private static string Arquivo => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RevitDwgUnifier", "central_ja.json");
        private static Biblioteca _bib;

        public static Biblioteca Bib
        {
            get
            {
                if (_bib != null) return _bib;
                try { if (File.Exists(Arquivo)) _bib = ContractJson.Read<Biblioteca>(Arquivo); }
                catch (Exception ex) { Logger.Warn("Central JA: biblioteca ilegível: " + ex.Message); }
                _bib = _bib ?? new Biblioteca();
                if (_bib.Tarefas == null) _bib.Tarefas = new List<Tarefa>();
                if (_bib.Pacotes == null) _bib.Pacotes = new List<Pacote>();
                if (_bib.Favoritos == null) _bib.Favoritos = new List<string>();
                if (_bib.Escopo == null) _bib.Escopo = new Escopo();
                foreach (var t in _bib.Tarefas) if (t.Valores == null) t.Valores = new Dictionary<string, string>();
                foreach (var p in _bib.Pacotes) if (p.Passos == null) p.Passos = new List<string>();
                return _bib;
            }
        }

        public static void Salvar()
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(Arquivo)); ContractJson.Write(Arquivo, Bib); }
            catch (Exception ex) { Logger.Warn("Central JA: biblioteca não salva: " + ex.Message); }
        }

        /// <summary>Office folder with shared .jatarefas files (read-only in the Central).</summary>
        public static string PastaEscritorio
        {
            get => ToolPrefs.Get("central.pastaEscritorio") ?? "";
            set { ToolPrefs.Set("central.pastaEscritorio", value ?? ""); ToolPrefs.Save(); }
        }

        /// <summary>Tasks and bundles of the office folder (each file a Biblioteca).</summary>
        public static Biblioteca Escritorio()
        {
            var b = new Biblioteca();
            string pasta = PastaEscritorio;
            if (string.IsNullOrWhiteSpace(pasta) || !Directory.Exists(pasta)) return b;
            foreach (string f in Directory.GetFiles(pasta, "*.jatarefas"))
            {
                try
                {
                    var x = ContractJson.Read<Biblioteca>(f);
                    foreach (var t in x.Tarefas ?? new List<Tarefa>()) { t.DoEscritorio = true; if (t.Valores == null) t.Valores = new Dictionary<string, string>(); b.Tarefas.Add(t); }
                    foreach (var p in x.Pacotes ?? new List<Pacote>()) { p.DoEscritorio = true; if (p.Passos == null) p.Passos = new List<string>(); b.Pacotes.Add(p); }
                }
                catch (Exception ex) { Logger.Warn("Central JA: " + Path.GetFileName(f) + " ilegível: " + ex.Message); }
            }
            return b;
        }

        public static List<Tarefa> TodasTarefas() => Bib.Tarefas.Concat(Escritorio().Tarefas.Where(t => Bib.Tarefas.All(m => m.Id != t.Id))).ToList();
        public static List<Pacote> TodosPacotes() => Bib.Pacotes.Concat(Escritorio().Pacotes.Where(p => Bib.Pacotes.All(m => m.Id != p.Id))).ToList();

        public static Tarefa NovaTarefa(string nome, string ferramenta, Dictionary<string, string> valores, bool interativa)
        {
            var t = new Tarefa
            {
                Nome = nome, Ferramenta = ferramenta, Valores = valores ?? new Dictionary<string, string>(), Interativa = interativa,
                Criada = DateTime.Now.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)
            };
            Bib.Tarefas.Add(t);
            Salvar();
            return t;
        }

        /// <summary>Writes tasks and bundles (and the tasks the bundles use) to a .jatarefas file.</summary>
        public static void Exportar(string caminho, IEnumerable<Tarefa> tarefas, IEnumerable<Pacote> pacotes)
        {
            var b = new Biblioteca { Tarefas = tarefas.ToList(), Pacotes = pacotes.ToList() };
            var todas = TodasTarefas();
            foreach (var p in b.Pacotes)
                foreach (string id in p.Passos)
                {
                    var t = todas.FirstOrDefault(x => x.Id == id);
                    if (t != null && !b.Tarefas.Contains(t)) b.Tarefas.Add(t);
                }
            ContractJson.Write(caminho, b);
        }

        /// <summary>Adds the tasks/bundles of a file (same id = replaced). Returns how many.</summary>
        public static int Importar(string caminho)
        {
            var x = ContractJson.Read<Biblioteca>(caminho);
            int n = 0;
            foreach (var t in x.Tarefas ?? new List<Tarefa>())
            {
                if (t.Valores == null) t.Valores = new Dictionary<string, string>();
                Bib.Tarefas.RemoveAll(m => m.Id == t.Id);
                Bib.Tarefas.Add(t); n++;
            }
            foreach (var p in x.Pacotes ?? new List<Pacote>())
            {
                if (p.Passos == null) p.Passos = new List<string>();
                Bib.Pacotes.RemoveAll(m => m.Id == p.Id);
                Bib.Pacotes.Add(p); n++;
            }
            Salvar();
            return n;
        }

        // ------------------------------------------------------------------ tool registry
        private static Dictionary<string, Type> _tipos;

        /// <summary>Every runnable JA Tools command by its catalog title (ToolCommands + the DWG exporter).</summary>
        public static Dictionary<string, Type> Ferramentas
        {
            get
            {
                if (_tipos != null) return _tipos;
                _tipos = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
                Type[] tipos;
                try { tipos = Assembly.GetExecutingAssembly().GetTypes(); }
                catch (ReflectionTypeLoadException ex) { tipos = ex.Types.Where(t => t != null).ToArray(); }
                foreach (Type t in tipos)
                {
                    if (t.IsAbstract || !typeof(ToolCommand).IsAssignableFrom(t) || t.GetConstructor(Type.EmptyTypes) == null) continue;
                    try
                    {
                        string titulo = ((ToolCommand)Activator.CreateInstance(t)).Title;
                        if (!string.IsNullOrEmpty(titulo) && !_tipos.ContainsKey(titulo)) _tipos[titulo] = t;
                    }
                    catch { }
                }
                Type exp = tipos.FirstOrDefault(t => t.Name == "UnifiedDwgExportCommand");
                if (exp != null) _tipos["Exportar Pranchas"] = exp;
                return _tipos;
            }
        }

        /// <summary>Tools shown in the Central (catalog order; the Central and the search themselves left out).</summary>
        public static List<ToolInfo> FerramentasDoCatalogo() =>
            ToolCatalog.Tools.Where(t => Ferramentas.ContainsKey(t.Title) && t.Title != "Central JA" && t.Title != "Buscar Comando").ToList();

        // ------------------------------------------------------------------ scope (unified selection)
        public sealed class EscopoResolvido
        {
            public List<ElementId> Elementos = new List<ElementId>();
            public HashSet<string> Nomes = new HashSet<string>();
            public View Vista;
            public string Descricao = "";
        }

        public static readonly string[] CategoriasEscopo =
            { "Ambientes", "Portas", "Janelas", "Paredes", "Pisos", "Forros", "Mobiliário", "Marcenaria", "Equipamentos", "Peças hidrossanitárias",
              "Luminárias", "Pilares", "Vistas", "Folhas", "Níveis" };

        private static BuiltInCategory[] Bic(string nome)
        {
            switch (nome)
            {
                case "Ambientes": return new[] { BuiltInCategory.OST_Rooms };
                case "Portas": return new[] { BuiltInCategory.OST_Doors };
                case "Janelas": return new[] { BuiltInCategory.OST_Windows };
                case "Paredes": return new[] { BuiltInCategory.OST_Walls };
                case "Pisos": return new[] { BuiltInCategory.OST_Floors };
                case "Forros": return new[] { BuiltInCategory.OST_Ceilings };
                case "Mobiliário": return new[] { BuiltInCategory.OST_Furniture, BuiltInCategory.OST_FurnitureSystems };
                case "Marcenaria": return new[] { BuiltInCategory.OST_Casework };
                case "Equipamentos": return new[] { BuiltInCategory.OST_SpecialityEquipment, BuiltInCategory.OST_MechanicalEquipment, BuiltInCategory.OST_ElectricalEquipment };
                case "Peças hidrossanitárias": return new[] { BuiltInCategory.OST_PlumbingFixtures };
                case "Luminárias": return new[] { BuiltInCategory.OST_LightingFixtures };
                case "Pilares": return new[] { BuiltInCategory.OST_Columns, BuiltInCategory.OST_StructuralColumns };
                case "Vistas": return new[] { BuiltInCategory.OST_Views };
                case "Folhas": return new[] { BuiltInCategory.OST_Sheets };
                case "Níveis": return new[] { BuiltInCategory.OST_Levels };
                default: return new BuiltInCategory[0];
            }
        }

        /// <summary>Resolves the scope in the open project (elements to select, names for the lists, view to activate).</summary>
        public static EscopoResolvido Resolver(UIDocument uidoc, Escopo e)
        {
            var r = new EscopoResolvido();
            if (uidoc == null || e == null || e.Modo == Escopo.Nenhum) return r;
            Document doc = uidoc.Document;
            switch (e.Modo)
            {
                case Escopo.VistaAtiva:
                    r.Vista = uidoc.ActiveView;
                    Nomear(r, r.Vista);
                    r.Descricao = "Vista ativa: " + (r.Vista is ViewSheet vs ? vs.SheetNumber + " - " + vs.Name : r.Vista?.Name);
                    return r;
                case Escopo.Selecionados:
                    foreach (ElementId id in uidoc.Selection.GetElementIds())
                    {
                        Element el = doc.GetElement(id);
                        if (el == null) continue;
                        r.Elementos.Add(id);
                        Nomear(r, el);
                    }
                    r.Descricao = r.Elementos.Count + " elemento(s) selecionado(s) no Revit";
                    return r;
            }
            // filter: categories (default rooms), levels, scope box, text
            var cats = (e.Categorias.Count == 0 ? new List<string> { "Ambientes" } : e.Categorias).SelectMany(Bic).Distinct().ToList();
            var niveis = new HashSet<string>(e.Niveis ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            BoundingBoxXYZ caixa = null;
            if (!string.IsNullOrWhiteSpace(e.ScopeBox))
                caixa = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_VolumeOfInterest).WhereElementIsNotElementType()
                    .FirstOrDefault(x => string.Equals(x.Name, e.ScopeBox, StringComparison.OrdinalIgnoreCase))?.get_BoundingBox(null);
            string filtro = TextUtil.Fold(e.Filtro ?? "").Trim();
            var excl = new HashSet<long>(e.Excluidos ?? new List<long>());
            foreach (Element el in Candidatos(doc, cats))
            {
                if (excl.Contains(el.Id.Value)) continue;
                if (niveis.Count > 0)
                {
                    string nv = NivelDe(doc, el);
                    if (nv == null || !niveis.Contains(nv)) continue;
                }
                if (caixa != null)
                {
                    XYZ c = Centro(el);
                    if (c == null || c.X < caixa.Min.X || c.X > caixa.Max.X || c.Y < caixa.Min.Y || c.Y > caixa.Max.Y) continue;
                }
                if (filtro.Length > 0 && !TextUtil.Fold(Rotulo(el)).Contains(filtro)) continue;
                r.Elementos.Add(el.Id);
                Nomear(r, el);
            }
            foreach (string n in niveis) r.Nomes.Add(TextUtil.Fold(n).Trim());
            r.Descricao = $"{r.Elementos.Count} elemento(s): {string.Join(", ", e.Categorias.Count == 0 ? new List<string> { "Ambientes" } : e.Categorias)}" +
                          (niveis.Count > 0 ? " · " + string.Join(", ", niveis) : "") + (caixa != null ? " · " + e.ScopeBox : "") +
                          (filtro.Length > 0 ? " · “" + e.Filtro + "”" : "");
            return r;
        }

        /// <summary>Candidates of the filter mode, for the scope preview (hand-picking).</summary>
        public static IEnumerable<Element> Candidatos(Document doc, List<BuiltInCategory> cats)
        {
            if (cats.Count == 0) yield break;
            foreach (Element el in new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(cats)).WhereElementIsNotElementType())
            {
                if (el is View v && v.IsTemplate) continue;
                if (el is Room room && room.Area <= 0) continue;
                yield return el;
            }
        }

        public static string Rotulo(Element el)
        {
            switch (el)
            {
                case Room r: return (r.Number + " - " + (r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? "")).Trim(' ', '-');
                case ViewSheet s: return s.SheetNumber + " - " + s.Name;
                default:
                    string mark = el.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();
                    return string.IsNullOrEmpty(mark) ? el.Name : el.Name + " (" + mark + ")";
            }
        }

        private static void Nomear(EscopoResolvido r, Element el)
        {
            if (el == null) return;
            void Add(string s) { if (!string.IsNullOrWhiteSpace(s)) r.Nomes.Add(TextUtil.Fold(s).Trim()); }
            switch (el)
            {
                case Room room:
                    Add(room.Number);
                    Add(room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString());
                    Add(room.Number + " - " + room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString());
                    break;
                case ViewSheet s: Add(s.SheetNumber); Add(s.Name); Add(s.SheetNumber + " - " + s.Name); break;
                default: Add(el.Name); break;
            }
            string nv = NivelDe(el.Document, el);
            if (nv != null && el is View) Add(nv);
        }

        private static string NivelDe(Document doc, Element el)
        {
            try
            {
                if (el is Level l) return l.Name;
                if (el is View v) return v.GenLevel?.Name;
                ElementId id = el.LevelId;
                if (id == null || id == ElementId.InvalidElementId)
                {
                    Parameter p = el.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM) ?? el.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM);
                    id = p?.AsElementId();
                }
                return id != null && id != ElementId.InvalidElementId ? doc.GetElement(id)?.Name : null;
            }
            catch { return null; }
        }

        private static XYZ Centro(Element el)
        {
            if (el.Location is LocationPoint lp) return lp.Point;
            if (el.Location is LocationCurve lc) return lc.Curve.Evaluate(0.5, true);
            BoundingBoxXYZ b = el.get_BoundingBox(null);
            return b == null ? null : (b.Min + b.Max) / 2;
        }

        /// <summary>Selects the scope's elements in Revit / opens its view (outside any transaction).</summary>
        public static void AplicarNoRevit(UIDocument uidoc, EscopoResolvido r)
        {
            if (r == null) return;
            try { if (r.Vista != null && uidoc.ActiveView.Id != r.Vista.Id) uidoc.ActiveView = r.Vista; } catch { }
            try { if (r.Elementos.Count > 0) uidoc.Selection.SetElementIds(r.Elementos); } catch { }
        }

        // ------------------------------------------------------------------ running
        /// <summary>Runs a tool as it is (its own dialog), from the Central or the command search.</summary>
        public static Result RodarFerramenta(UIApplication app, string titulo)
        {
            if (!Ferramentas.TryGetValue(titulo, out Type tipo)) return Result.Failed;
            return Executar(app, tipo);
        }

        private static Result Executar(UIApplication app, Type tipo)
        {
            object cmd = Activator.CreateInstance(tipo);
            if (cmd is ToolCommand tc) return tc.Executar(app);
            if (cmd is IExternalCommand ec)
            {
                // the exporter is a plain IExternalCommand: build its ExternalCommandData by hand
                var data = (ExternalCommandData)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(ExternalCommandData));
                typeof(ExternalCommandData).GetProperty("Application")?.SetValue(data, app);
                string msg = "";
                return ec.Execute(data, ref msg, new ElementSet());
            }
            return Result.Failed;
        }

        /// <summary>Runs one task (no options window unless it is interactive) and reports what happened.</summary>
        public static StatusPasso RodarTarefa(UIApplication app, Tarefa t, EscopoResolvido escopo)
        {
            var passo = new StatusPasso { Nome = t.Nome, Ferramenta = t.Ferramenta };
            if (!Ferramentas.TryGetValue(t.Ferramenta, out Type tipo))
            {
                passo.Estado = StatusPasso.Erro;
                passo.Texto = "Ferramenta não encontrada nesta versão do JA Tools: " + t.Ferramenta;
                return passo;
            }
            return Rodar(app, tipo, t.Valores, t.Interativa, escopo, passo);
        }

        /// <summary>A tool without a task (bundle step "ferramenta:…"): its own window, as it is.</summary>
        public static StatusPasso RodarFerramentaComoPasso(UIApplication app, string titulo, EscopoResolvido escopo)
        {
            var passo = new StatusPasso { Nome = titulo, Ferramenta = titulo };
            if (!Ferramentas.TryGetValue(titulo, out Type tipo)) { passo.Estado = StatusPasso.Erro; passo.Texto = "Ferramenta não encontrada: " + titulo; return passo; }
            return Rodar(app, tipo, new Dictionary<string, string>(), true, escopo, passo);
        }

        private static StatusPasso Rodar(UIApplication app, Type tipo, Dictionary<string, string> valores, bool interativa,
                                         EscopoResolvido escopo, StatusPasso passo)
        {
            var antes = new Dictionary<string, string>();
            foreach (var kv in valores) { antes[kv.Key] = ToolPrefs.Get(kv.Key); ToolPrefs.Set(kv.Key, kv.Value); }
            var criados = new List<ElementId>();
            Document doc = app.ActiveUIDocument?.Document;
            EventHandler<Autodesk.Revit.DB.Events.DocumentChangedEventArgs> h = (s, e) =>
            {
                try { if (e.GetDocument().Equals(doc)) criados.AddRange(e.GetAddedElementIds()); } catch { }
            };
            var relogio = Stopwatch.StartNew();
            _mensagens.Clear();
            Ativa = true; SemJanela = !interativa; DialogoVisto = false;
            NomesEscopo = escopo != null && escopo.Nomes.Count > 0 ? escopo.Nomes : null;
            Result r;
            try
            {
                if (escopo != null) AplicarNoRevit(app.ActiveUIDocument, escopo);
                app.Application.DocumentChanged += h;
                r = Executar(app, tipo);
            }
            catch (Exception ex) { r = Result.Failed; Capturar(passo.Ferramenta, ex.Message, ResultKind.Error); }
            finally
            {
                try { app.Application.DocumentChanged -= h; } catch { }
                Ativa = false; SemJanela = false; NomesEscopo = null;
                foreach (var kv in antes) { if (kv.Value == null) ToolPrefs.Set(kv.Key, ""); else ToolPrefs.Set(kv.Key, kv.Value); }
                ToolPrefs.Save();
            }
            passo.Segundos = relogio.Elapsed.TotalSeconds;
            Document d = app.ActiveUIDocument?.Document;
            passo.Criados = criados.Distinct().Where(id => d?.GetElement(id) != null).ToList();
            passo.VistasCriadas = passo.Criados.Where(id => d.GetElement(id) is View v && !v.IsTemplate).ToList();
            passo.Texto = string.Join("\n", _mensagens.Select(m => m.texto).Where(x => !string.IsNullOrWhiteSpace(x)));
            bool erro = r == Result.Failed || _mensagens.Any(m => m.kind == ResultKind.Error);
            bool aviso = _mensagens.Any(m => m.kind == ResultKind.Warning) || Regex.IsMatch(passo.Texto, @"\b(erro|falh|não foi|nao foi|problema)", RegexOptions.IgnoreCase);
            passo.Estado = erro ? StatusPasso.Erro : r == Result.Cancelled ? StatusPasso.Cancelado : aviso ? StatusPasso.Aviso : StatusPasso.Ok;
            if (string.IsNullOrWhiteSpace(passo.Texto))
                passo.Texto = passo.Estado == StatusPasso.Cancelado ? "Cancelado." : passo.Criados.Count > 0 ? $"{passo.Criados.Count} elemento(s) criados." : "Concluído.";
            _mensagens.Clear();
            return passo;
        }

        public static StatusExecucao RodarPacote(UIApplication app, Pacote p, EscopoResolvido escopo)
        {
            var st = new StatusExecucao { Titulo = "Pacote: " + p.Nome };
            var tarefas = TodasTarefas();
            foreach (string passo in p.Passos)
            {
                StatusPasso sp;
                if (passo.StartsWith("ferramenta:")) sp = RodarFerramentaComoPasso(app, passo.Substring(11), escopo);
                else
                {
                    Tarefa t = tarefas.FirstOrDefault(x => x.Id == passo);
                    sp = t != null ? RodarTarefa(app, t, escopo)
                                   : new StatusPasso { Nome = "(tarefa apagada)", Estado = StatusPasso.Erro, Texto = "A tarefa deste passo não existe mais." };
                }
                st.Passos.Add(sp);
                if (p.PararNoErro && (sp.Estado == StatusPasso.Erro || sp.Estado == StatusPasso.Cancelado))
                {
                    int faltam = p.Passos.Count - st.Passos.Count;
                    if (faltam > 0) st.Passos.Add(new StatusPasso { Nome = $"{faltam} passo(s) seguintes", Estado = StatusPasso.Cancelado, Texto = "Não rodaram: o pacote para no primeiro erro (opção do pacote)." });
                    break;
                }
            }
            Registrar(st);
            return st;
        }

        public static void Registrar(StatusExecucao st)
        {
            Historico.Insert(0, st);
            while (Historico.Count > 15) Historico.RemoveAt(Historico.Count - 1);
        }

        public static string NomePasso(string passo)
        {
            if (passo.StartsWith("ferramenta:")) return passo.Substring(11) + " (janela própria)";
            return TodasTarefas().FirstOrDefault(t => t.Id == passo)?.Nome ?? "(tarefa apagada)";
        }

        /// <summary>
        /// "Configurar tarefa": opens the tool with the task's values. Option dialogs get a "Salvar tarefa"
        /// button (the tool does not run); tools with their own window run once and the options used are
        /// recorded (the task becomes interactive). Returns false when nothing was saved.
        /// </summary>
        public static bool Configurar(UIApplication app, Tarefa t)
        {
            if (!Ferramentas.TryGetValue(t.Ferramenta, out Type tipo)) return false;
            var antes = new Dictionary<string, string>();
            foreach (var kv in t.Valores) { antes[kv.Key] = ToolPrefs.Get(kv.Key); ToolPrefs.Set(kv.Key, kv.Value); }
            ToolPrefs.Gravando = new HashSet<string>();
            EditandoTarefa = t; TarefaSalva = false; DialogoVisto = false;
            bool ok = false;
            try
            {
                Executar(app, tipo);
                if (TarefaSalva) ok = true;
                else if (!DialogoVisto)
                {
                    var chaves = ToolPrefs.Gravando.Where(k => !Regex.IsMatch(k, @"\.__\w+$")).ToList();
                    if (chaves.Count > 0)
                    {
                        t.Valores = chaves.ToDictionary(k => k, k => ToolPrefs.Get(k) ?? "");
                        t.Interativa = true;
                        ok = true;
                    }
                }
            }
            finally
            {
                var gravadas = ToolPrefs.Gravando;
                ToolPrefs.Gravando = null;
                EditandoTarefa = null;
                foreach (var kv in antes) if (gravadas == null || !gravadas.Contains(kv.Key) || TarefaSalva) ToolPrefs.Set(kv.Key, kv.Value ?? "");
                ToolPrefs.Save();
            }
            return ok;
        }

        // ------------------------------------------------------------------ small UI helper
        /// <summary>Asks for a name (small JA window). Null = cancelled.</summary>
        public static string PerguntarNome(Window owner, string titulo, string pergunta, string inicial)
        {
            var w = new Window
            {
                Title = ToolCatalog.Marca(titulo) + " — " + titulo, Width = 420, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen, ShowInTaskbar = false, Owner = owner
            };
            RevitDwgUnifier.UI.JaTheme.Apply(w, System.Windows.Media.Color.FromRgb(0x2B, 0x57, 0x9A));
            var sp = new StackPanel { Margin = new Thickness(18) };
            sp.SetResourceReference(System.Windows.Controls.Panel.BackgroundProperty, "Ja.Surface");
            var tb = new System.Windows.Controls.TextBox { Text = inicial ?? "", Margin = new Thickness(0, 6, 0, 12) };
            sp.Children.Add(new TextBlock { Text = pergunta, TextWrapping = TextWrapping.Wrap });
            sp.Children.Add(tb);
            var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
            var cancel = ToolChrome.Ghost("Cancelar"); cancel.IsCancel = true; cancel.Margin = new Thickness(0, 0, 8, 0);
            var ok = ToolChrome.Primary("OK", (s, e) => w.DialogResult = true); ok.IsDefault = true;
            bar.Children.Add(cancel); bar.Children.Add(ok);
            sp.Children.Add(bar);
            w.Content = sp;
            w.Loaded += (s, e) => { tb.Focus(); tb.SelectAll(); };
            return w.ShowDialog() == true ? tb.Text : null;
        }
    }
}
