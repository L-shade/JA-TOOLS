using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using Autodesk.Revit.UI;
using JAEsquadrias.Commands;

namespace JAEsquadrias
{
    /// <summary>
    /// IExternalApplication: creates the "JA Famílias" ribbon tab (JA para Revit 2.0: same DLL and same
    /// common base as the "JA Tools" tab).
    ///   Esquadrias       : Nova Janela, Nova Porta, Parede Cortina, Painel de Cortina, Fachada Paginada
    ///   Perfis           : Novo Perfil, Trocar Montantes (curtain-wall mullions)
    ///   Pedras e Nichos  : Nova Bancada, Novo Nicho, Novo Chapim, Bancada no Local, Nicho no Local, Soleiras e Peitoris
    ///   Revestimentos    : Paginação, Rodapé ▾ (ambientes, por ambiente, no local), Forro e Sanca,
    ///                      Impermeabilização, Escada Revestida, Ralo ▾ (novo, no local), Atualizar Revestimentos
    ///   Vidros e Madeira : Guarda-corpo, Box de Banheiro, Painel Ripado, Pergolado
    ///   Gerenciar        : Editar Família (any JA family), Biblioteca, Substituir pelos do JA, Conferir Planta, Trocar Acabamento
    ///   Documentar       : Quadros ▾ (esquadrias, peças), Relatório, Cortes, Iluminação, Orçamento, Listas, Memorial
    ///   JA               : Central JA, Buscar Comando (both tabs), Central de Ajuda, Configurações
    /// Startup must never throw: a failing OnStartup disables the add-in for the session, so
    /// everything is caught and logged.
    /// </summary>
    public class App : IExternalApplication
    {
        public const string TabName = "JA Famílias";

        public Result OnStartup(UIControlledApplication application)
        {
            try { Logger.Initialize(); } catch { }
            var problems = new List<string>();
            try
            {
                try { AddinLocation.RevitVersion = application.ControlledApplication.VersionNumber; } catch { }
                Logger.Info($"JA Famílias starting in Revit {AddinLocation.RevitVersion} ({System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}), DLL: {AddinLocation.DllPath}");
                try { HelpCenter.Write(); } catch (Exception ex) { Logger.Warn("Help page not written: " + ex.Message); }
                try { application.CreateRibbonTab(TabName); }
                catch (Autodesk.Revit.Exceptions.ArgumentException) { /* tab already exists */ }

                var panels = new List<KeyValuePair<string, Action<UIControlledApplication>>>
                {
                    new KeyValuePair<string, Action<UIControlledApplication>>("Esquadrias", AddCreatePanel),
                    new KeyValuePair<string, Action<UIControlledApplication>>("Perfis", AddPerfisPanel),
                    new KeyValuePair<string, Action<UIControlledApplication>>("Pedras e Nichos", AddPecasPanel),
                    new KeyValuePair<string, Action<UIControlledApplication>>("Revestimentos", AddRevestimentosPanel),
                    new KeyValuePair<string, Action<UIControlledApplication>>("Vidros e Madeira", AddVidrosPanel),
                    new KeyValuePair<string, Action<UIControlledApplication>>("Gerenciar", AddEditPanel),
                    new KeyValuePair<string, Action<UIControlledApplication>>("Documentar", AddDocPanel),
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
                // the notice of a new version is given once, by the JA Tools tab (one installer for both)
                Logger.Info("JA Famílias loaded" + (problems.Count > 0 ? $" with {problems.Count} panel error(s)." : "."));
            }
            catch (Exception ex)
            {
                Logger.Error("Ribbon initialisation failed.", ex);
                problems.Add(ex.GetType().Name + " – " + ex.Message);
            }

            if (problems.Count > 0)
            {
                try
                {
                    TaskDialog.Show("JA Famílias", "O JA Famílias não carregou por completo neste Revit:\n\n• " + string.Join("\n• ", problems) +
                                    "\n\nDetalhes no registro: " + Logger.DailyLogPath);
                }
                catch { }
            }
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;

        private static RibbonPanel Panel(UIControlledApplication app, string name) =>
            app.GetRibbonPanels(TabName).FirstOrDefault(p => p.Name == name) ?? app.CreateRibbonPanel(TabName, name);

        private static string AssemblyPath => AddinLocation.DllPath;

        private static PushButtonData Button<T>(string id, string text, string title, bool always = false)
        {
            var info = ToolCatalog.Get(title);
            Color c = info.Accent;
            var data = new PushButtonData("JAEsquadrias_" + id, text, AssemblyPath, typeof(T).FullName)
            {
                AvailabilityClassName = always ? typeof(AlwaysAvailable).FullName : typeof(CommandAvailability).FullName,
                LargeImage = IconFactory.Tool(32, info.Icon, info.Glyph, c),
                Image = IconFactory.Tool(16, info.Icon, info.Glyph, c)
            };
            HelpCenter.Describe(data, info);
            return data;
        }

        private static void AddCreatePanel(UIControlledApplication app)
        {
            RibbonPanel panel = Panel(app, "Esquadrias");
            panel.AddItem(Button<NovaJanelaCommand>("NovaJanela", "Nova\nJanela", "Nova Janela"));
            panel.AddItem(Button<NovaPortaCommand>("NovaPorta", "Nova\nPorta", "Nova Porta"));
            panel.AddItem(Button<PortaElevadorCommand>("PortaElevador", "Porta de\nElevador", "Porta de Elevador"));
            panel.AddItem(Button<EsquadriaParedeCortinaCommand>("ParedeCortina", "Parede\nCortina", "Parede Cortina"));
            panel.AddItem(Button<NovoPainelCommand>("PainelCortina", "Painel de\nCortina", "Painel de Cortina"));
            panel.AddItem(Button<FachadaPaginadaCommand>("FachadaPaginada", "Fachada\nPaginada", "Fachada Paginada"));
        }

        private static void AddPerfisPanel(UIControlledApplication app)
        {
            RibbonPanel panel = Panel(app, "Perfis");
            panel.AddItem(Button<NovoPerfilCommand>("NovoPerfil", "Novo\nPerfil", "Novo Perfil"));
            panel.AddItem(Button<TrocarMontantesCommand>("TrocarMontantes", "Trocar\nMontantes", "Trocar Montantes"));
        }

        private static void AddPecasPanel(UIControlledApplication app)
        {
            RibbonPanel panel = Panel(app, "Pedras e Nichos");
            panel.AddStackedItems(
                Button<NovaBancadaCommand>("NovaBancada", "Nova Bancada", "Nova Bancada"),
                Button<NovoNichoCommand>("NovoNicho", "Novo Nicho", "Novo Nicho"),
                Button<NovoChapimCommand>("NovoChapim", "Novo Chapim", "Novo Chapim"));
            panel.AddItem(Button<BancadaLocalCommand>("BancadaLocal", "Bancada\nno Local", "Bancada no Local"));
            panel.AddItem(Button<NichoNoLocalCommand>("NichoLocal", "Nicho\nno Local", "Nicho no Local"));
            panel.AddItem(Button<SoleirasPeitorisCommand>("SoleirasPeitoris", "Soleiras e\nPeitoris", "Soleiras e Peitoris"));
        }

        /// <summary>A menu of tools as a split button: the top half repeats the last one used.</summary>
        private static void Split(RibbonPanel panel, string id, string text, params PushButtonData[] items)
        {
            if (panel.AddItem(new SplitButtonData("JAEsquadrias_Menu" + id, text)) is SplitButton sb)
            {
                sb.IsSynchronizedWithCurrentItem = true;
                foreach (PushButtonData b in items) sb.AddPushButton(b);
            }
        }

        private static void AddRevestimentosPanel(UIControlledApplication app)
        {
            RibbonPanel panel = Panel(app, "Revestimentos");
            panel.AddItem(Button<PaginacaoCommand>("Paginacao", "Paginação", "Paginação de Piso e Parede"));
            Split(panel, "Rodape", "Rodapé",
                Button<RodapeCommand>("Rodape", "Rodapé", "Rodapé"),
                Button<RodapePorAmbienteCommand>("RodapeAmbiente", "Rodapé por\nAmbiente", "Rodapé por Ambiente"),
                Button<RodapeNoLocalCommand>("RodapeLocal", "Rodapé\nno Local", "Rodapé no Local"));
            panel.AddStackedItems(
                Button<ForroCommand>("Forro", "Forro e Sanca", "Forro e Sanca"),
                Button<ImpermeabilizacaoCommand>("Impermeabilizacao", "Impermeabilização", "Impermeabilização"),
                Button<EscadaRevestidaCommand>("EscadaRevestida", "Escada Revestida", "Escada Revestida"));
            Split(panel, "Ralo", "Ralo",
                Button<NovoRaloCommand>("NovoRalo", "Novo\nRalo", "Novo Ralo"),
                Button<RaloCroquiCommand>("RaloCroqui", "Ralo no\nLocal", "Ralo no Local"));
            panel.AddItem(Button<AtualizarRevestimentosCommand>("AtualizarRevestimentos", "Atualizar\nRevestimentos", "Atualizar Revestimentos"));
        }

        private static void AddVidrosPanel(UIControlledApplication app)
        {
            RibbonPanel panel = Panel(app, "Vidros e Madeira");
            panel.AddStackedItems(
                Button<GuardaCorpoCommand>("GuardaCorpo", "Guarda-corpo", "Guarda-corpo de Vidro"),
                Button<BoxBanheiroCommand>("BoxBanheiro", "Box de Banheiro", "Box de Banheiro"));
            panel.AddStackedItems(
                Button<PainelRipadoCommand>("PainelRipado", "Painel Ripado", "Painel Ripado"),
                Button<PergoladoCommand>("Pergolado", "Pergolado", "Pergolado"));
        }

        private static void AddEditPanel(UIControlledApplication app)
        {
            RibbonPanel panel = Panel(app, "Gerenciar");
            panel.AddItem(Button<EditarEsquadriaCommand>("EditarEsquadria", "Editar\nFamília", "Editar Família"));
            panel.AddItem(Button<BibliotecaCommand>("Biblioteca", "Biblioteca", "Biblioteca"));
            panel.AddItem(Button<SubstituirPorJaCommand>("SubstituirPorJa", "Substituir\npelos do JA", "Substituir pelos do JA"));
            panel.AddItem(Button<ConferirPlantaCommand>("ConferirPlanta", "Conferir\nPlanta", "Conferir Planta"));
            panel.AddItem(Button<TrocarAcabamentoCommand>("TrocarAcabamento", "Trocar\nAcabamento", "Trocar Acabamento"));
        }

        private static void AddDocPanel(UIControlledApplication app)
        {
            RibbonPanel panel = Panel(app, "Documentar");
            // split button: the top half repeats the last schedule created
            if (panel.AddItem(new SplitButtonData("JAEsquadrias_MenuQuadros", "Quadros")) is SplitButton quadros)
            {
                quadros.IsSynchronizedWithCurrentItem = true;
                quadros.AddPushButton(Button<QuadroEsquadriasCommand>("Quadro", "Quadro de\nEsquadrias", "Quadro de Esquadrias"));
                quadros.AddPushButton(Button<QuadroPecasCommand>("QuadroPecas", "Quadro de\nPeças", "Quadro de Peças"));
            }
            panel.AddItem(Button<RelatorioEsquadriasCommand>("Relatorio", "Relatório", "Relatório de Esquadrias"));
            panel.AddItem(Button<CortesEsquadriasCommand>("CortesEsquadrias", "Cortes das\nEsquadrias", "Cortes das Esquadrias"));
            panel.AddItem(Button<IluminacaoVentilacaoCommand>("IluminacaoVentilacao", "Iluminação e\nVentilação", "Iluminação e Ventilação"));
            panel.AddItem(Button<OrcamentoCommand>("Orcamento", "Orçamento", "Orçamento"));
            panel.AddItem(Button<ListaMarmorariaCommand>("ListaMarmoraria", "Lista para a\nMarmoraria", "Lista para a Marmoraria"));
            panel.AddStackedItems(
                Button<ListaComprasCommand>("ListaCompras", "Lista de Compras", "Lista de Compras"),
                Button<MemorialCommand>("Memorial", "Memorial Descritivo", "Memorial Descritivo"));
        }

        private static void AddHelpPanel(UIControlledApplication app)
        {
            RibbonPanel panel = Panel(app, "JA");
            // the Central JA and the command search cover the tools of both tabs (same DLL)
            panel.AddItem(Button<RevitDwgUnifier.Tools.Central.CentralJaCommand>("CentralJA", "Central\nJA", "Central JA"));
            panel.AddItem(Button<RevitDwgUnifier.Tools.Central.BuscarComandoCommand>("BuscarComando", "Buscar\nComando", "Buscar Comando"));
            var help = new PushButtonData("JAEsquadrias_CentralAjuda", "Central de\nAjuda", AssemblyPath, typeof(HelpCenterCommand).FullName)
            {
                ToolTip = "Todas as ferramentas das abas JA Famílias e JA Tools explicadas passo a passo, com busca. Funciona sem internet.",
                LongDescription = "Dica: no configurador, F1 abre a ajuda, Ctrl+Enter gera e Esc cancela.",
                AvailabilityClassName = typeof(AlwaysAvailable).FullName,
                LargeImage = IconFactory.Mark(32),
                Image = IconFactory.Mark(16)
            };
            help.SetContextualHelp(new ContextualHelp(ContextualHelpType.Url, HelpCenter.Url()));
            panel.AddItem(help);
            panel.AddItem(Button<ConfiguracoesCommand>("Configuracoes", "Configurações", ConfiguracoesCommand.Title, always: true));
        }
    }
}
