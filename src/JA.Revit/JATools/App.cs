using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.UI;
using RevitDwgUnifier.Commands;
using RevitDwgUnifier.Infrastructure;
using RevitDwgUnifier.Tools.Common;
using RevitDwgUnifier.Tools.Dimensions;
using RevitDwgUnifier.Tools.Exporting;
using RevitDwgUnifier.Tools.Parameters;
using RevitDwgUnifier.Tools.Modeling;
using RevitDwgUnifier.Tools.Views;
using RevitDwgUnifier.UI;

namespace RevitDwgUnifier
{
    /// <summary>
    /// IExternalApplication: creates the "JA Tools" ribbon tab, in the order of the work:
    ///   Modelagem    : Por Ambiente ▾ (pisos, forro, rodapé e tabica, luminárias), Converter em Camadas, Suspender Grupos
    ///   Vistas       : Cortes Gerais, Fachada Planificada, Plantas por Nível, Vistas por Ambiente, Eixos ▾, Ocultar em Vista
    ///   Detalhamento : Detalhar Ambientes, Anotar Vista, Anotações ▾, Gerar Detalhes, Hachuras ▾
    ///   Cotas        : Cotas de Ambiente ▾, Cotas de Parede ▾, Cotas de Pontos
    ///   Pranchas     : Gestão de Pranchas, Renomear, Duplicar, Alinhar Vistas
    ///   Exportar     : Exportar Pranchas (PDF, DWG, PNG), Exportar Tabelas
    /// 1.17: tools that did the same job were merged (one button, a choice inside).
    ///   Dados        : Renumerar Elementos, Editar Níveis, Remover Ambientes
    ///   JA           : Central de Ajuda, Padrões do Escritório
    /// The ▾ buttons are split buttons: the top half repeats the last tool used from that menu.
    /// Startup must never throw: a failing OnStartup disables the add-in for the session and
    /// shows a generic Revit error, so everything is caught and logged.
    /// </summary>
    public class App : IExternalApplication
    {
        public const string TabName = "JA Tools";

        public Result OnStartup(UIControlledApplication application)
        {
            try { Logger.Initialize(); } catch { }
            var problems = new List<string>();
            try
            {
                try { AddinLocation.RevitVersion = application.ControlledApplication.VersionNumber; } catch { }
                Logger.Info($"JA Tools starting in Revit {AddinLocation.RevitVersion} ({System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}), " +
                            $"DLL: {AddinLocation.DllPath}");
                try { HelpCenter.Write(); } catch (Exception ex) { Logger.Warn("Help page not written: " + ex.Message); }
                try { application.CreateRibbonTab(TabName); }
                catch (Autodesk.Revit.Exceptions.ArgumentException) { /* tab already exists */ }

                // each panel on its own: one failing panel never hides the others
                var panels = new List<KeyValuePair<string, Action<UIControlledApplication>>>
                {
                    new KeyValuePair<string, Action<UIControlledApplication>>("Modelagem", AddModelingPanel),
                    new KeyValuePair<string, Action<UIControlledApplication>>("Vistas", AddViewsPanel),
                    new KeyValuePair<string, Action<UIControlledApplication>>("Detalhamento", AddDetailPanel),
                    new KeyValuePair<string, Action<UIControlledApplication>>("Cotas", AddDimensionsPanel),
                    new KeyValuePair<string, Action<UIControlledApplication>>("Pranchas", AddSheetsPanel),
                    new KeyValuePair<string, Action<UIControlledApplication>>("Exportar", AddExportPanel),
                    new KeyValuePair<string, Action<UIControlledApplication>>("Dados", AddDataPanel),
                    new KeyValuePair<string, Action<UIControlledApplication>>("JA", AddHelpPanel)
                };
                foreach (var kv in panels)
                {
                    try { kv.Value(application); }
                    catch (Exception ex)
                    {
                        Logger.Error("Painel '" + kv.Key + "' não criado.", ex);
                        problems.Add(kv.Key + ": " + ex.GetType().Name + " – " + ex.Message);
                    }
                }

                // Suspender Grupos: the button label follows the active project
                try
                {
                    application.ViewActivated += (s, e) => Faixa.Atualizar(e.Document);
                    application.ControlledApplication.DocumentChanged += (s, e) => { try { Faixa.Atualizar(e.GetDocument()); } catch { } };
                }
                catch (Exception ex) { Logger.Warn("Botão Suspender Grupos: " + ex.Message); }

                // Documentos Vinculados: warn when linked Excel/Word/PDF files changed
                try { RevitDwgUnifier.Tools.Publish.DocAviso.Iniciar(application); }
                catch (Exception ex) { Logger.Warn("Aviso de documentos vinculados: " + ex.Message); }

                // Central JA as a Revit side panel (dockable panes can only be registered at startup)
                try { RevitDwgUnifier.Tools.Central.CentralHost.Iniciar(application); }
                catch (Exception ex) { Logger.Warn("Central JA (barra lateral): " + ex.Message); }

                // keyboard shortcut of the Varinha Mágica (Space by default)
                try { RevitDwgUnifier.Tools.Detail.VarinhaAtalho.Iniciar(application); }
                catch (Exception ex) { Logger.Warn("Atalho da Varinha Mágica: " + ex.Message); }

                // telemetria: tool use, options, times, errors (Uso e Telemetria)
                try { Telemetria.Iniciar(application); } catch (Exception ex) { Logger.Warn("Telemetria: " + ex.Message); }
                AvisoVersao.Iniciar(application, "JA");   // one notice for both tabs (versoes.txt: "JA=2.0.0")
                Logger.Info("JA Tools loaded (Revit " + application.ControlledApplication.VersionNumber + ")" +
                            (problems.Count > 0 ? $" with {problems.Count} panel error(s)." : "."));
            }
            catch (Exception ex)
            {
                Logger.Error("Ribbon initialisation failed.", ex);
                problems.Add(ex.GetType().Name + " – " + ex.Message);
            }

            if (problems.Count > 0)
            {
                // say it instead of failing silently (the log has the details)
                try
                {
                    TaskDialog.Show("JA Tools", "O JA Tools não carregou por completo neste Revit:\n\n• " + string.Join("\n• ", problems) +
                                    "\n\nDetalhes no registro: " + Logger.DailyLogPath);
                }
                catch { }
            }
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            try { RevitDwgUnifier.Tools.Detail.VarinhaAtalho.Parar(); } catch { }
            try { Telemetria.Encerrar(); } catch { }
            return Result.Succeeded;
        }

        private static RibbonPanel Panel(UIControlledApplication app, string name) =>
            app.GetRibbonPanels(TabName).FirstOrDefault(p => p.Name == name) ?? app.CreateRibbonPanel(TabName, name);

        private static string AssemblyPath => AddinLocation.DllPath;

        // ================================================================== panels
        private static void AddModelingPanel(UIControlledApplication app)
        {
            RibbonPanel panel = Panel(app, "Modelagem");
            AddSplit(panel, "MenuPorAmbiente", "Por Ambiente",
                Button<PisosPorAmbienteCommand>("PisosPorAmbiente", "Pisos por\nAmbiente"),
                Button<RodapeTabicaCommand>("RodapeTabica", "Tabica por\nAmbiente"),
                Button<LuminariasPorAmbienteCommand>("LuminariasPorAmbiente", "Luminárias\npor Ambiente"));
            panel.AddItem(Button<VarinhaPontosCommand>("VarinhaPontos", "Varinha de\nPontos"));
            panel.AddItem(Button<ConverterEmCamadasCommand>("ConverterEmCamadas", "Converter\nem Camadas"));
            panel.AddItem(Button<UnirElementosCommand>("UnirElementos", "Unir\nElementos"));
            panel.AddItem(Button<AjustarParedesCebolaCommand>("AjustarCebola", "Ajustar\nCebola"));
            panel.AddItem(Button<ParedesNasLajesCommand>("ParedesNasLajes", "Paredes\nnas Lajes"));
            // one button that says what it will do: "Suspender Grupos" / "Restaurar Grupos (n)"
            Faixa.Botao = panel.AddItem(Button<SuspenderGruposCommand>("SuspenderGrupos", "Suspender\nGrupos")) as PushButton;
        }

        private static void AddViewsPanel(UIControlledApplication app)
        {
            RibbonPanel panel = Panel(app, "Vistas");
            panel.AddItem(Button<CortesGeraisCommand>("CortesGerais", "Cortes\nGerais"));
            panel.AddItem(Button<FachadaPlanificadaCommand>("FachadaPlanificada", "Fachada\nPlanificada"));
            panel.AddItem(Button<PlantasPorNivelCommand>("PlantasPorNivel", "Plantas\npor Nível"));
            panel.AddItem(Button<VistasPorAmbienteCommand>("VistasPorAmbiente", "Vistas por\nAmbiente"));
            AddStack(panel,
                Menu("MenuEixos", "Eixos",
                    Button<VistasEixosCommand>("VistasEixos", "Vistas de Eixos"),
                    Button<EixosDeParedesCommand>("EixosDeParedes", "Eixos de Paredes"),
                    Button<RecalcularEixosCommand>("RecalcularEixos", "Recalcular Eixos")),
                Button<OcultarEmVistaCommand>("OcultarEmVista", "Ocultar em Vista"));
        }

        private static void AddDetailPanel(UIControlledApplication app)
        {
            RibbonPanel panel = Panel(app, "Detalhamento");
            panel.AddItem(Button<RevitDwgUnifier.Tools.Detail.DetalharAmbientesCommand>("DetalharAmbientes", "Detalhar\nAmbientes"));
            panel.AddItem(Button<RevitDwgUnifier.Tools.Detail.AnotarVistaCommand>("AnotarVista", "Anotar\nVista"));
            AddStack(panel,
                Menu("MenuAnotacoes", "Anotações",
                    Button<RevitDwgUnifier.Tools.Detail.EtiquetarElementosCommand>("EtiquetarElementos", "Etiquetar Elementos"),
                    Button<RevitDwgUnifier.Tools.Detail.OrganizarAnotacoesCommand>("OrganizarAnotacoes", "Organizar Anotações")),
                Button<GerarDetalhesCommand>("GerarDetalhes", "Gerar Detalhes"),
                Menu("MenuHachuras", "Hachuras",
                    Button<RevitDwgUnifier.Tools.Detail.HachuraUnicaCommand>("HachuraUnica", "Hachura Única"),
                    Button<RevitDwgUnifier.Tools.Detail.VarinhaMagicaCommand>("VarinhaMagica", "Varinha Mágica")));
        }

        private static void AddDimensionsPanel(UIControlledApplication app)
        {
            RibbonPanel panel = Panel(app, "Cotas");
            AddSplit(panel, "MenuCotasAmbiente", "Cotas de Ambiente",
                Button<CotasPorAmbienteCommand>("CotasPorAmbiente", "Cotas por\nAmbiente"),
                Button<CotasPisoForroCommand>("CotasPisoForro", "Cotas de\nPiso e Forro"));
            AddSplit(panel, "MenuCotasParede", "Cotas de Parede",
                Button<CotasPorParedeCommand>("CotasPorParede", "Cotas por\nParede"),
                Button<CotasPorSelecaoCommand>("CotasPorSelecao", "Cotas por\nSeleção"),
                Button<CotasPorPontosCommand>("CotasPorPontos", "Cotas por\nPontos"));
            panel.AddItem(Button<CotasDePontosCommand>("CotasDePontos", "Cotas de\nPontos"));
            panel.AddItem(Button<EtiquetarCotarLoteCommand>("EtiquetarCotarLote", "Etiquetar e\nCotar em Lote", "Etiquetar e Cotar em Lote"));
        }

        private static void AddSheetsPanel(UIControlledApplication app)
        {
            RibbonPanel panel = Panel(app, "Pranchas");
            panel.AddItem(Button<GestaoPranchasCommand>("GestaoPranchas", "Gestão de\nPranchas"));
            AddStack(panel,
                Button<RevitDwgUnifier.Tools.Publish.RenomearPranchasCommand>("RenomearPranchas", "Renomear Pranchas"),
                Button<RevitDwgUnifier.Tools.Publish.DuplicarPranchasCommand>("DuplicarPranchas", "Duplicar Pranchas"),
                Button<RevitDwgUnifier.Tools.Publish.AlinharVistasCommand>("AlinharVistas", "Alinhar Vistas"));
            panel.AddItem(Button<RevitDwgUnifier.Tools.Publish.DocumentosVinculadosCommand>("DocumentosVinculados", "Documentos\nVinculados"));
            panel.AddItem(Button<RevitDwgUnifier.Tools.Publish.MapeadorFolhasCommand>("MapeadorFolhas", "Mapeador\nde Folhas"));
        }

        private static void AddExportPanel(UIControlledApplication app)
        {
            RibbonPanel panel = Panel(app, "Exportar");
            panel.AddItem(Button<UnifiedDwgExportCommand>("ExportarPranchas", "Exportar\nPranchas", "Exportar Pranchas"));
            panel.AddItem(Button<ExportarTabelasCommand>("ExportarTabelas", "Exportar\nTabelas"));
            panel.AddItem(Button<PlanilhaParametrosCommand>("PlanilhaParametros", "Planilha de\nParâmetros", "Planilha de Parâmetros"));
            panel.AddItem(Button<VerificarModeloCommand>("VerificarModelo", "Verificar\nModelo", "Verificar Modelo"));
        }

        private static void AddDataPanel(UIControlledApplication app)
        {
            RibbonPanel panel = Panel(app, "Dados");
            panel.AddItem(Button<GestorEstilosCommand>("GestorEstilos", "Gestor de\nEstilos"));
            panel.AddItem(Button<RevitDwgUnifier.Tools.Parameters.PadronizarNomesCommand>("PadronizarNomes", "Padronizar\nNomes"));
            AddStack(panel,
                Button<RenumerarElementosCommand>("RenumerarElementos", "Renumerar Elementos"),
                Button<EditarNiveisCommand>("EditarNiveis", "Editar Níveis"),
                Button<RemoverAmbientesCommand>("RemoverAmbientes", "Remover Ambientes"));
            panel.AddItem(Button<MudarNivelCommand>("MudarNivel", "Mudar\nNível", "Mudar Nível"));
            panel.AddItem(Button<PreencherParametrosCommand>("PreencherParametros", "Preencher\nParâmetros", "Preencher Parâmetros"));
            panel.AddItem(Button<FiltrarElementosCommand>("FiltrarElementos", "Filtrar\nElementos", "Filtrar Elementos"));
            AddStack(panel,
                Button<QuadroAcabamentosCommand>("QuadroAcabamentos", "Quadro de Acabamentos", "Quadro de Acabamentos"),
                Button<QuadroAreasCommand>("QuadroAreas", "Quadro de Áreas", "Quadro de Áreas"),
                Button<QuantitativoPinturaCommand>("QuantitativoPintura", "Quantitativo de Pintura", "Quantitativo de Pintura"));
            AddStack(panel,
                Menu("MenuConferencias", "Conferências",
                    Button<ConferirAcessibilidadeCommand>("ConferirAcessibilidade", "Conferir Acessibilidade", "Conferir Acessibilidade"),
                    Button<ConferirEscadasCommand>("ConferirEscadas", "Conferir Escadas", "Conferir Escadas")),
                Button<CarimboLoteCommand>("CarimboLote", "Carimbo em Lote", "Carimbo em Lote"),
                Button<CompararVersoesCommand>("CompararVersoes", "Comparar Versões", "Comparar Versões"));
        }

        private static void AddHelpPanel(UIControlledApplication app)
        {
            RibbonPanel panel = Panel(app, "JA");
            panel.AddItem(Button<RevitDwgUnifier.Tools.Central.CentralJaCommand>("CentralJA", "Central\nJA"));
            panel.AddItem(Button<RevitDwgUnifier.Tools.Central.BuscarComandoCommand>("BuscarComando", "Buscar\nComando"));
            panel.AddItem(Button<RevitDwgUnifier.Tools.Views.EstudarVistaCommand>("EstudarVista", "Estudar\nVista"));
            var help = new PushButtonData("JATools_CentralAjuda", "Central de\nAjuda", AssemblyPath, typeof(HelpCenterCommand).FullName)
            {
                ToolTip = "Todas as ferramentas do JA Tools explicadas passo a passo, com busca. Funciona sem internet.",
                LongDescription = "Dica: em qualquer janela do JA Tools, F1 abre a ajuda da ferramenta, Ctrl+Enter executa e Esc cancela. " +
                                  "Passe o mouse sobre um botão da faixa e espere um pouco para ver os passos.",
                AvailabilityClassName = typeof(AlwaysAvailable).FullName,
                LargeImage = IconFactory.Mark(32),
                Image = IconFactory.Mark(16)
            };
            help.SetContextualHelp(new ContextualHelp(ContextualHelpType.Url, HelpCenter.Url()));
            panel.AddItem(help);
            panel.AddItem(Button<RevitDwgUnifier.Tools.Publish.PadroesEscritorioCommand>("PadroesEscritorio", "Padrões do\nEscritório"));
            panel.AddItem(Button<RevitDwgUnifier.Tools.Common.UsoTelemetriaCommand>("UsoTelemetria", "Uso e\nTelemetria"));
            panel.AddItem(Button<RevitDwgUnifier.Tools.Common.ExportarRelatorioUsoCommand>("ExportarRelatorioUso", "Exportar\nRelatório de Uso"));
        }

        // ================================================================== helpers
        /// <param name="title">ToolCatalog title; default: the command's own Title.</param>
        private static PushButtonData Button<T>(string id, string text, string title = null)
        {
            ToolInfo info = ToolCatalog.Get(title ?? TitleOf(typeof(T)) ?? "");
            var data = new PushButtonData("JATools_" + id, text, AssemblyPath, typeof(T).FullName)
            {
                AvailabilityClassName = typeof(CommandAvailability).FullName,
                LargeImage = IconFactory.Tool(32, info.Icon, info.Glyph, info.Accent),
                Image = IconFactory.Tool(16, info.Icon, info.Glyph, info.Accent)
            };
            HelpCenter.Describe(data, info);
            return data;
        }

        /// <summary>The command's own Title (same key as ToolCatalog), read once at startup.</summary>
        private static string TitleOf(Type commandType)
        {
            try { return (Activator.CreateInstance(commandType) as ToolCommand)?.Title; }
            catch { return null; }
        }

        /// <summary>A menu of tools for a stack: becomes a split button (last tool used on top).</summary>
        private sealed class MenuData
        {
            public string Id, Text;
            public PushButtonData[] Items;
        }

        private static MenuData Menu(string id, string text, params PushButtonData[] items) =>
            new MenuData { Id = id, Text = text, Items = items };

        /// <summary>Large split button: the top half runs the last tool used, the arrow lists all.</summary>
        private static void AddSplit(RibbonPanel panel, string id, string text, params PushButtonData[] items)
        {
            if (panel.AddItem(new SplitButtonData("JATools_" + id, text)) is SplitButton split) Fill(split, items);
        }

        private static void Fill(SplitButton split, PushButtonData[] items)
        {
            split.IsSynchronizedWithCurrentItem = true;
            foreach (var b in items)
            {
                try { split.AddPushButton(b); }
                catch (Exception ex) { Logger.Warn("Botão " + b.Name + " não entrou no menu: " + ex.Message); }
            }
        }

        /// <summary>Two or three small items one above the other (buttons or menus).</summary>
        private static void AddStack(RibbonPanel panel, params object[] items)
        {
            var data = items.Select(o => o is MenuData m ? (RibbonItemData)new SplitButtonData("JATools_" + m.Id, m.Text) : (RibbonItemData)o).ToArray();
            IList<RibbonItem> made;
            if (data.Length == 3) made = panel.AddStackedItems(data[0], data[1], data[2]);
            else if (data.Length == 2) made = panel.AddStackedItems(data[0], data[1]);
            else
            {
                made = new List<RibbonItem>();
                foreach (var d in data) made.Add(panel.AddItem(d));
            }
            for (int i = 0; i < items.Length && i < made.Count; i++)
                if (items[i] is MenuData m && made[i] is SplitButton split) Fill(split, m.Items);
        }
    }
}
