using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Autodesk.Revit.Attributes;
using RevitDwgUnifier.Tools.Common;
using RevitDwgUnifier.UI;
using RUI = Autodesk.Revit.UI;

namespace RevitDwgUnifier.Tools.Central
{
    /// <summary>
    /// Buscar Comando (reference: Glyph's command search): a small palette — type part of a name,
    /// arrow keys to choose, Enter to run. Finds tools, saved tasks, bundles and the Central tabs,
    /// ignoring accents and word order; recently used items come first.
    /// </summary>
    public sealed class BuscaComandosWindow : Window
    {
        private sealed class Item
        {
            public string Tipo, Titulo, Detalhe, Chave;
            public ImageSource Icone;
            public PedidoCentral Pedido;
        }

        private const string PrefRecentes = "busca.recentes";
        private readonly List<Item> _todos = new List<Item>();
        private readonly TextBox _caixa = new TextBox { FontSize = 16, BorderThickness = new Thickness(0), Padding = new Thickness(4, 6, 4, 6) };
        private readonly ListBox _lista = new ListBox { BorderThickness = new Thickness(0), Background = Brushes.Transparent, MaxHeight = 420 };
        public PedidoCentral Pedido { get; private set; }

        public BuscaComandosWindow()
        {
            Title = "JA Tools — Buscar comando";
            Width = 620; SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true;
            Background = Brushes.Transparent; ShowInTaskbar = false; Topmost = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            JaTheme.Apply(this, Color.FromRgb(0x2B, 0x57, 0x9A));
            Montar();

            var casca = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(12), Margin = new Thickness(14) };
            casca.SetResourceReference(Border.BackgroundProperty, "Ja.Surface");
            casca.SetResourceReference(Border.BorderBrushProperty, "Ja.Line");
            casca.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.25 };
            var sp = new StackPanel();
            var topo = new DockPanel();
            var marca = new Image { Source = IconFactory.Mark(64), Width = 22, Height = 22, Margin = new Thickness(2, 0, 8, 0) };
            DockPanel.SetDock(marca, Dock.Left);
            topo.Children.Add(marca);
            topo.Children.Add(_caixa);
            sp.Children.Add(topo);
            var linha = new Border { Height = 1, Margin = new Thickness(0, 8, 0, 6) };
            linha.SetResourceReference(Border.BackgroundProperty, "Ja.Line");
            sp.Children.Add(linha);
            sp.Children.Add(_lista);
            var dica = ToolChrome.Muted("↑ ↓ escolher · Enter rodar · Esc fechar · “t:” só tarefas · “p:” só pacotes", 10.5);
            dica.Margin = new Thickness(4, 6, 0, 0);
            sp.Children.Add(dica);
            casca.Child = sp;
            Content = casca;

            _caixa.TextChanged += (s, e) => Filtrar();
            PreviewKeyDown += Teclas;
            _lista.MouseDoubleClick += (s, e) => Rodar();
            Deactivated += (s, e) => { if (IsVisible && DialogResult == null) try { Close(); } catch { } };
            Loaded += (s, e) => { Filtrar(); _caixa.Focus(); };
        }

        private void Montar()
        {
            foreach (ToolInfo t in Automacao.FerramentasDoCatalogo())
                _todos.Add(new Item
                {
                    Tipo = "Ferramenta", Titulo = t.Title, Detalhe = t.Group + " · " + t.Description, Chave = "f:" + t.Title,
                    Icone = IconFactory.Tool(64, t.Icon, t.Glyph, t.Accent),
                    Pedido = new PedidoCentral { Acao = PedidoCentral.Ferramenta, Alvo = t.Title }
                });
            foreach (Tarefa t in Automacao.TodasTarefas())
            {
                ToolInfo info = ToolCatalog.Get(t.Ferramenta);
                _todos.Add(new Item
                {
                    Tipo = "Tarefa", Titulo = t.Nome, Detalhe = "Tarefa · " + t.Ferramenta + (string.IsNullOrWhiteSpace(t.Descricao) ? "" : " · " + t.Descricao), Chave = "t:" + t.Id,
                    Icone = IconFactory.Tool(64, info.Icon, info.Glyph, info.Accent),
                    Pedido = new PedidoCentral { Acao = PedidoCentral.Tarefa, Alvo = t.Id }
                });
            }
            ImageSource central = IconFactory.Tool(64, "central", "C", Color.FromRgb(0x18, 0x21, 0x2B));
            foreach (Pacote p in Automacao.TodosPacotes())
                _todos.Add(new Item
                {
                    Tipo = "Pacote", Titulo = p.Nome, Detalhe = "Pacote · " + p.Passos.Count + " passo(s): " + string.Join(", ", p.Passos.Take(4).Select(Automacao.NomePasso)), Chave = "p:" + p.Id,
                    Icone = central, Pedido = new PedidoCentral { Acao = PedidoCentral.Pacote, Alvo = p.Id }
                });
            foreach (string aba in new[] { CentralPainel.AbaFerramentas, CentralPainel.AbaTarefas, CentralPainel.AbaPacotes, CentralPainel.AbaEscopo, CentralPainel.AbaStatus })
                _todos.Add(new Item
                {
                    Tipo = "Central", Titulo = "Central JA: " + aba, Detalhe = "Abrir a Central JA em " + aba, Chave = "c:" + aba,
                    Icone = central, Pedido = new PedidoCentral { Acao = PedidoCentral.Central, Alvo = aba }
                });
        }

        private static List<string> Recentes()
        {
            string s = ToolPrefs.Get(PrefRecentes) ?? "";
            return s.Split(new[] { '\u001f' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        /// <summary>Score: all words must appear; prefix and word-start matches rank higher; recent items get a bonus.</summary>
        private static int Pontuar(Item it, string[] palavras, List<string> recentes)
        {
            string titulo = TextUtil.Fold(it.Titulo);
            string tudo = titulo + " " + TextUtil.Fold(it.Detalhe);
            int pts = 0;
            foreach (string w in palavras)
            {
                int i = titulo.IndexOf(w, StringComparison.Ordinal);
                if (i == 0) pts += 40;
                else if (i > 0 && !char.IsLetterOrDigit(titulo[i - 1])) pts += 25;
                else if (i > 0) pts += 12;
                else if (Iniciais(titulo).StartsWith(w, StringComparison.Ordinal)) pts += 20;
                else if (tudo.Contains(w)) pts += 4;
                else return -1;
            }
            int r = recentes.IndexOf(it.Chave);
            if (r >= 0) pts += 30 - r * 3;
            if (it.Tipo == "Central") pts -= 5;
            return pts;
        }

        private static string Iniciais(string s) =>
            new string(s.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x[0]).ToArray());

        private void Filtrar()
        {
            string q = TextUtil.Fold(_caixa.Text ?? "").Trim();
            string tipo = null;
            if (q.StartsWith("t:")) { tipo = "Tarefa"; q = q.Substring(2); }
            else if (q.StartsWith("p:")) { tipo = "Pacote"; q = q.Substring(2); }
            else if (q.StartsWith("f:")) { tipo = "Ferramenta"; q = q.Substring(2); }
            string[] palavras = q.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var recentes = Recentes();
            IEnumerable<Item> fonte = _todos.Where(i => tipo == null || i.Tipo == tipo);
            List<Item> res;
            if (palavras.Length == 0)
                res = fonte.Where(i => recentes.Contains(i.Chave)).OrderBy(i => recentes.IndexOf(i.Chave))
                           .Concat(fonte.Where(i => !recentes.Contains(i.Chave) && i.Tipo != "Central")).Take(12).ToList();
            else
                res = fonte.Select(i => (i, p: Pontuar(i, palavras, recentes))).Where(x => x.p >= 0)
                           .OrderByDescending(x => x.p).ThenBy(x => x.i.Titulo).Select(x => x.i).Take(12).ToList();
            _lista.Items.Clear();
            foreach (Item it in res) _lista.Items.Add(new ListBoxItem { Content = Linha(it), Tag = it, Padding = new Thickness(6, 5, 6, 5) });
            if (_lista.Items.Count > 0) _lista.SelectedIndex = 0;
        }

        private static UIElement Linha(Item it)
        {
            var dp = new DockPanel();
            var img = new Image { Source = it.Icone, Width = 24, Height = 24, Margin = new Thickness(0, 0, 10, 0) };
            DockPanel.SetDock(img, Dock.Left);
            dp.Children.Add(img);
            var tipo = ToolChrome.Muted(it.Tipo, 10.5);
            tipo.VerticalAlignment = VerticalAlignment.Center;
            tipo.Margin = new Thickness(10, 0, 4, 0);
            DockPanel.SetDock(tipo, Dock.Right);
            dp.Children.Add(tipo);
            var sp = new StackPanel();
            var t = new TextBlock { Text = it.Titulo, FontWeight = FontWeights.SemiBold, FontSize = 13 };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Ink");
            sp.Children.Add(t);
            var d = ToolChrome.Muted(it.Detalhe, 11);
            d.TextTrimming = TextTrimming.CharacterEllipsis;
            d.TextWrapping = TextWrapping.NoWrap;
            sp.Children.Add(d);
            dp.Children.Add(sp);
            return dp;
        }

        private void Teclas(object sender, KeyEventArgs e)
        {
            int n = _lista.Items.Count;
            switch (e.Key)
            {
                case Key.Escape: DialogResult = false; e.Handled = true; break;
                case Key.Enter: Rodar(); e.Handled = true; break;
                case Key.Down: if (n > 0) { _lista.SelectedIndex = (_lista.SelectedIndex + 1) % n; _lista.ScrollIntoView(_lista.SelectedItem); } e.Handled = true; break;
                case Key.Up: if (n > 0) { _lista.SelectedIndex = (_lista.SelectedIndex - 1 + n) % n; _lista.ScrollIntoView(_lista.SelectedItem); } e.Handled = true; break;
            }
        }

        private void Rodar()
        {
            if (!((_lista.SelectedItem as ListBoxItem)?.Tag is Item it)) return;
            var rec = Recentes();
            rec.Remove(it.Chave);
            rec.Insert(0, it.Chave);
            ToolPrefs.Set(PrefRecentes, string.Join("\u001f", rec.Take(10)));
            ToolPrefs.Save();
            Pedido = it.Pedido;
            DialogResult = true;
        }
    }

    /// <summary>Buscar Comando: the palette; Enter runs the tool, task or bundle (results go to the Central's Status).</summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class BuscarComandoCommand : ToolCommand
    {
        public override string Title => "Buscar Comando";
        protected override bool PicksInView => true;

        protected override RUI.Result Run()
        {
            ToolProgress.End();
            var w = new BuscaComandosWindow();
            if (Owner != IntPtr.Zero) new System.Windows.Interop.WindowInteropHelper(w).Owner = Owner;
            if (w.ShowDialog() != true || w.Pedido == null) return RUI.Result.Cancelled;
            string aba = CentralExecutor.Executar(UiApp, w.Pedido);
            // tasks and bundles show their report in the Central; "Central: …" items open it
            if (aba != null && (w.Pedido.Acao == PedidoCentral.Tarefa || w.Pedido.Acao == PedidoCentral.Pacote || w.Pedido.Acao == PedidoCentral.Central))
                CentralHost.Mostrar(UiApp, aba, false);
            return RUI.Result.Succeeded;
        }
    }
}
