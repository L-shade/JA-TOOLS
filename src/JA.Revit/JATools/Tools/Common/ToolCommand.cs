using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitDwgUnifier.Infrastructure;

namespace RevitDwgUnifier.Tools.Common
{
    /// <summary>Thrown by a tool to stop quietly (user pressed Cancel / Esc).</summary>
    public sealed class ToolCancelledException : Exception
    {
        public ToolCancelledException() : base("Cancelado.") { }
    }

    /// <summary>
    /// Base of every button of both tabs, JA Famílias and JA Tools (except the DWG exporter, which has its own flow).
    /// Gives each tool the active document, a uniform error handling (nothing escapes to Revit
    /// as an unhandled exception), logging, and Portuguese message helpers.
    /// Derived classes MUST carry [Transaction(TransactionMode.Manual)].
    /// </summary>
    public abstract class ToolCommand : IExternalCommand
    {
        protected UIApplication UiApp { get; private set; }
        protected UIDocument UiDoc { get; private set; }
        protected Document Doc { get; private set; }

        /// <summary>Name shown in dialogs and in the log, e.g. "Cortes Gerais".</summary>
        public abstract string Title { get; }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
            Executar(commandData.Application);

        /// <summary>Runs the tool from code (Central JA: tasks, bundles, command search).</summary>
        public Result Executar(UIApplication app)
        {
            UiApp = app;
            UiDoc = UiApp.ActiveUIDocument;
            Doc = UiDoc?.Document;
            if (Doc == null) return Result.Cancelled;
            if (Doc.IsFamilyDocument)
            {
                Show("Esta ferramenta funciona apenas em projetos (não no editor de famílias).");
                return Result.Cancelled;
            }

            SessionCache.Clear();
            Telemetria.Rodada rodada = Telemetria.IniciarComando(Title, UiDoc, Automacao.Ativa);
            Result fim = Result.Failed;
            Exception erro = null;
            ToolProgress.Begin(Owner, Title, PicksInView);
            // one Ctrl+Z for the whole run of a tool that works in several transactions
            TransactionGroup grupo = null;
            if (UmDesfazer)
            {
                try { grupo = new TransactionGroup(Doc, Title); grupo.Start(); }
                catch (Exception ex) { Logger.Warn($"[JA] {Title}: sem grupo de desfazer ({ex.Message})."); grupo = null; }
            }
            try
            {
                Logger.Info($"[{ToolCatalog.Marca(Title)}] {Title}: início.");
                Result r = Run();
                fim = r;
                Logger.Info($"[{ToolCatalog.Marca(Title)}] {Title}: {r}.");
                return r;
            }
            catch (ToolCancelledException) { fim = Result.Cancelled; return Result.Cancelled; }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { fim = Result.Cancelled; return Result.Cancelled; }
            catch (Exception ex)
            {
                erro = ex;
                fim = Result.Failed;
                Logger.Error($"[{ToolCatalog.Marca(Title)}] {Title} falhou.", ex);
                Show("Erro: " + ex.Message + "\n\nOs detalhes técnicos foram gravados no registro do JA (Central de Ajuda › registro).", ResultKind.Error);
                return Result.Failed;
            }
            finally
            {
                if (grupo != null)
                {
                    // what was done stays (also after an error, as without the group), in one undo step
                    try { if (grupo.GetStatus() == TransactionStatus.Started) grupo.Assimilate(); } catch { }
                    try { grupo.Dispose(); } catch { }
                }
                ToolProgress.End();
                SessionCache.Clear();
                Telemetria.FimComando(rodada, fim, erro);
                Logger.Flush();
            }
        }

        /// <summary>Binds the tool to Revit when it runs from an external event (keyboard shortcut) instead of the ribbon.</summary>
        internal void Vincular(UIApplication app)
        {
            UiApp = app;
            UiDoc = app?.ActiveUIDocument;
            Doc = UiDoc?.Document;
        }

        /// <summary>True for tools that make the user click in the view after the options, or that
        /// run their own window (with its own progress): no automatic progress box.</summary>
        protected virtual bool PicksInView => false;

        /// <summary>
        /// True for tools that change the model in several transactions (batches, a second pass): the
        /// whole run becomes one Ctrl+Z (a transaction group). Only for tools that do not create or load
        /// families, save, export or open documents while they run (those must not be inside a group).
        /// </summary>
        protected virtual bool UmDesfazer => false;

        protected abstract Result Run();

        /// <summary>Result / message window (styled, kind inferred from the text).</summary>
        protected void Show(string text) => Show(text, ResultKind.Auto);

        protected void Show(string text, ResultKind kind, params ResultAction[] actions)
        {
            ToolProgress.End();
            Telemetria.Mensagem(Title, kind.ToString(), text);
            // inside a task / bundle: the message goes to the status report, no window
            if (Automacao.Ativa) { Automacao.Capturar(Title, text, kind); return; }
            try { ResultDialog.Show(Owner, Title, text, kind, null, actions); }
            catch (Exception ex)
            {
                Logger.Warn("[JA Tools] Janela de resultado falhou: " + ex.Message);
                TaskDialog.Show(ToolCatalog.Marca(Title) + " - " + Title, text);
            }
        }

        /// <summary>"Abrir pasta" button for tools that write files.</summary>
        protected static ResultAction OpenFolderAction(string folder) =>
            new ResultAction("Abrir pasta", () => ResultDialog.OpenPath(folder), true);

        protected IntPtr Owner => UiApp?.MainWindowHandle ?? IntPtr.Zero;

        /// <summary>Opens a view in Revit after the transaction (does nothing if it fails).</summary>
        protected void TryOpen(View v)
        {
            try { if (v != null && v.IsValidObject) UiDoc.ActiveView = v; } catch { }
        }
    }
}
