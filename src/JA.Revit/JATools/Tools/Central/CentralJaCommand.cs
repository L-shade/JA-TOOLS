using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDwgUnifier.Infrastructure;
using RevitDwgUnifier.Tools.Common;

namespace RevitDwgUnifier.Tools.Central
{
    /// <summary>What the Central or the command search asks for.</summary>
    public sealed class PedidoCentral
    {
        public const string Ferramenta = "ferramenta", Tarefa = "tarefa", Pacote = "pacote", Configurar = "configurar",
                            NovaTarefa = "novaTarefa", Selecionar = "selecionar", Abrir = "abrir", Central = "central";
        public string Acao, Alvo, Nome;
        public List<ElementId> Ids = new List<ElementId>();
        public ElementId Vista;
    }

    /// <summary>Runs what the Central or the command search asked for. Returns the Central tab to show, or null.</summary>
    internal static class CentralExecutor
    {
        public static string Executar(UIApplication app, PedidoCentral p)
        {
            if (p == null) return null;
            UIDocument uidoc = app.ActiveUIDocument;
            switch (p.Acao)
            {
                case PedidoCentral.Ferramenta:
                    if (uidoc == null) return null;
                    Automacao.RodarFerramenta(app, p.Alvo);
                    return null;

                case PedidoCentral.Tarefa:
                {
                    Tarefa t = Automacao.TodasTarefas().FirstOrDefault(x => x.Id == p.Alvo);
                    if (t == null || uidoc == null) return CentralPainel.AbaTarefas;
                    var escopo = Automacao.Resolver(uidoc, Automacao.Bib.Escopo);
                    var st = new StatusExecucao { Titulo = "Tarefa: " + t.Nome };
                    st.Passos.Add(Automacao.RodarTarefa(app, t, escopo));
                    Automacao.Registrar(st);
                    return CentralPainel.AbaStatus;
                }

                case PedidoCentral.Pacote:
                {
                    Pacote pk = Automacao.TodosPacotes().FirstOrDefault(x => x.Id == p.Alvo);
                    if (pk == null || uidoc == null) return CentralPainel.AbaPacotes;
                    Automacao.RodarPacote(app, pk, Automacao.Resolver(uidoc, Automacao.Bib.Escopo));
                    return CentralPainel.AbaStatus;
                }

                case PedidoCentral.Configurar:
                {
                    Tarefa t = Automacao.Bib.Tarefas.FirstOrDefault(x => x.Id == p.Alvo);
                    if (t != null && uidoc != null && Automacao.Configurar(app, t)) Automacao.Salvar();
                    return CentralPainel.AbaTarefas;
                }

                case PedidoCentral.NovaTarefa:
                {
                    if (uidoc == null) return CentralPainel.AbaTarefas;
                    var t = new Tarefa { Nome = p.Nome, Ferramenta = p.Alvo, Criada = DateTime.Now.ToString("dd/MM/yyyy") };
                    if (Automacao.Configurar(app, t))
                    {
                        Automacao.Bib.Tarefas.Add(t);
                        Automacao.Salvar();
                    }
                    return CentralPainel.AbaTarefas;
                }

                case PedidoCentral.Selecionar:
                    try
                    {
                        var ids = p.Ids.Where(id => uidoc.Document.GetElement(id) != null).ToList();
                        uidoc.Selection.SetElementIds(ids);
                        if (ids.Count > 0) uidoc.ShowElements(ids);
                    }
                    catch (Exception ex) { Logger.Warn("[Central JA] Selecionar: " + ex.Message); }
                    return null;

                case PedidoCentral.Abrir:
                    try { if (uidoc.Document.GetElement(p.Vista) is View v) uidoc.ActiveView = v; }
                    catch (Exception ex) { Logger.Warn("[Central JA] Abrir vista: " + ex.Message); }
                    return null;

                case PedidoCentral.Central:
                    return string.IsNullOrEmpty(p.Alvo) ? CentralPainel.AbaFerramentas : p.Alvo;
            }
            return null;
        }
    }

    /// <summary>
    /// Hosts the Central JA without blocking Revit: as a dockable side panel (registered at startup)
    /// or as a floating window, both live (modeless). The panels never call the Revit API directly:
    /// they queue work here and it runs through one ExternalEvent, in Revit's API context.
    /// </summary>
    internal static class CentralHost
    {
        public static readonly DockablePaneId PaneId = new DockablePaneId(new Guid("6C1E4B7A-2F3D-4E8B-9A51-3D7C0E9B5F21"));
        private const string PrefModo = "central.modo";
        private static readonly ConcurrentQueue<Action<UIApplication>> _fila = new ConcurrentQueue<Action<UIApplication>>();
        private static ExternalEvent _evento;
        private static IntPtr _revit;
        private static bool _registrado;
        internal static CentralPainel Lateral;
        internal static CentralJanela Janela;

        /// <summary>App.OnStartup: the only moment Revit accepts a dockable pane.</summary>
        public static void Iniciar(UIControlledApplication app)
        {
            _revit = app.MainWindowHandle;
            _evento = ExternalEvent.Create(new Fila());
            Lateral = new CentralPainel(true);
            app.RegisterDockablePane(PaneId, "Central JA", new Provedor());
            _registrado = true;
            // the scope chip ("vista ativa", selection) and the status line follow the project
            app.ViewActivated += (s, e) => Cabecalhos();
        }

        public static bool EmJanela
        {
            get => ToolPrefs.Get(PrefModo) == "janela" || !_registrado;
            set { ToolPrefs.Set(PrefModo, value ? "janela" : "lateral"); ToolPrefs.Save(); }
        }

        /// <summary>Queues work for Revit's API context (from the panels, on the UI thread).</summary>
        public static void Pedir(Action<UIApplication> acao)
        {
            if (acao == null) return;
            _fila.Enqueue(acao);
            try
            {
                ExternalEventRequest r = _evento?.Raise() ?? ExternalEventRequest.Denied;
                if (r == ExternalEventRequest.Denied || r == ExternalEventRequest.TimedOut)
                    Logger.Warn("[Central JA] o Revit recusou o pedido agora (" + r + "); ele roda no próximo.");
            }
            catch (Exception ex) { Logger.Warn("[Central JA] " + ex.Message); }
        }

        /// <summary>Runs a request and refreshes the panels (Status after tasks and bundles).</summary>
        public static void Executar(PedidoCentral p) => Pedir(app => Atualizar(CentralExecutor.Executar(app, p)));

        public static void Atualizar(string aba)
        {
            foreach (CentralPainel painel in Paineis())
            {
                CentralPainel pp = painel;
                try { pp.Dispatcher.BeginInvoke(new Action(() => pp.Recarregar(aba))); } catch { }
            }
        }

        /// <summary>Light refresh (scope chip, last run): on view change, never rebuilds the lists.</summary>
        public static void Cabecalhos()
        {
            foreach (CentralPainel painel in Paineis())
            {
                CentralPainel pp = painel;
                try { pp.Dispatcher.BeginInvoke(new Action(pp.AtualizarCabecalho)); } catch { }
            }
        }

        private static IEnumerable<CentralPainel> Paineis()
        {
            if (Lateral != null) yield return Lateral;
            if (Janela?.Painel != null) yield return Janela.Painel;
        }

        /// <summary>Ribbon button / command search: shows the Central the way the user left it; with
        /// <paramref name="alternar"/> a second click hides it.</summary>
        public static void Mostrar(UIApplication app, string aba, bool alternar)
        {
            if (EmJanela) MostrarJanela(aba, alternar);
            else MostrarLateral(app, aba, alternar);
        }

        private static void MostrarLateral(UIApplication app, string aba, bool alternar)
        {
            DockablePane pane = app.GetDockablePane(PaneId);
            if (alternar && aba == null && pane.IsShown()) { pane.Hide(); return; }
            FecharJanela();
            pane.Show();
            Lateral?.Recarregar(aba);
        }

        private static void MostrarJanela(string aba, bool alternar)
        {
            if (Janela != null && Janela.IsVisible)
            {
                if (alternar && aba == null) { Janela.Close(); return; }
                Janela.Activate();
            }
            else
            {
                Janela = new CentralJanela(_revit);
                Janela.Closed += (s, e) => Janela = null;
                Janela.Show();
            }
            Janela?.Painel.Recarregar(aba);
        }

        private static void FecharJanela()
        {
            try { Janela?.Close(); } catch { }
            Janela = null;
        }

        /// <summary>"Abrir como janela" (from the side panel).</summary>
        public static void ParaJanela()
        {
            EmJanela = true;
            Pedir(app => { try { app.GetDockablePane(PaneId).Hide(); } catch { } });
            MostrarJanela(null, false);
        }

        /// <summary>"Fixar na lateral" (from the window).</summary>
        public static void ParaLateral()
        {
            if (!_registrado) return;
            EmJanela = false;
            FecharJanela();
            Pedir(app => MostrarLateral(app, null, false));
        }

        private sealed class Fila : IExternalEventHandler
        {
            public void Execute(UIApplication app)
            {
                while (_fila.TryDequeue(out Action<UIApplication> acao))
                {
                    try { acao(app); }
                    catch (Exception ex) { Logger.Warn("[Central JA] " + ex); }
                }
            }

            public string GetName() => "JA Tools - Central JA";
        }

        private sealed class Provedor : IDockablePaneProvider
        {
            public void SetupDockablePane(DockablePaneProviderData data)
            {
                data.FrameworkElement = Lateral;
                data.InitialState = new DockablePaneState { DockPosition = DockPosition.Right };
                // keep the keyboard in the panel while typing in the search box
                try { data.EditorInteraction = new EditorInteraction(EditorInteractionType.KeepAlive); } catch { }
                try { typeof(DockablePaneProviderData).GetProperty("VisibleByDefault")?.SetValue(data, false); } catch { }
            }
        }
    }

    /// <summary>Central JA: shows or hides the Central (side panel or window, as last used).</summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class CentralJaCommand : ToolCommand
    {
        public override string Title => "Central JA";
        protected override bool PicksInView => true;

        protected override Result Run()
        {
            ToolProgress.End();
            CentralHost.Mostrar(UiApp, null, true);
            return Result.Succeeded;
        }
    }
}
