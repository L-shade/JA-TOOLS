using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Autodesk.Revit.DB;
using RevitDwgUnifier.Infrastructure;
using RevitDwgUnifier.Tools.Common;
using Binding = System.Windows.Data.Binding;
using Grid = System.Windows.Controls.Grid;
using WM = System.Windows.Media;

namespace RevitDwgUnifier.Tools.Publish
{
    /// <summary>One row of the links table.</summary>
    public sealed class VinculoLinha : INotifyPropertyChanged
    {
        public DocVinculo V { get; }
        public VinculoLinha(DocVinculo v) { V = v; }

        private bool _marcado;
        public bool Marcado { get => _marcado; set { _marcado = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Marcado))); } }
        public string Nome => V.Nome;
        public string Tipo => DocTipo.Nome(V.Tipo);
        public string Arquivo => Path.GetFileName(V.Caminho);
        public string Detalhe => V.Tipo == DocTipo.Tabela ? (string.IsNullOrWhiteSpace(V.Aba) ? "" : V.Aba + " · ") + (V.IntervaloLido ?? "")
                                : V.PaginasLigadas.Count == 1 ? "1 página" : V.PaginasLigadas.Count + " páginas";
        public string Onde { get; set; }
        public string Estado { get; set; }
        public string Atualizado => V.Atualizado;
        public WM.Brush CorEstado => Estado == DocMotor.EstadoOk ? WM.Brushes.SeaGreen : Estado == DocMotor.EstadoMudou ? WM.Brushes.DarkOrange : WM.Brushes.Firebrick;

        public event PropertyChangedEventHandler PropertyChanged;
    }

    /// <summary>
    /// Documentos Vinculados window: the links of the project (state, where they are) at the left,
    /// the form of a new or selected link at the right. Updates and edits run here (one undo each);
    /// a new link closes the window so the command can pick the point.
    /// </summary>
    public sealed class DocVinculosWindow : Window
    {
        private const string Titulo = "Documentos Vinculados";
        private readonly Document _doc;
        private readonly View _ativa;
        private readonly ObservableCollection<VinculoLinha> _linhas = new ObservableCollection<VinculoLinha>();

        public DocVinculo Novo { get; private set; }
        public View IrPara { get; private set; }

        private readonly DataGrid _grid = new DataGrid();
        private Button _btMudados, _btMarcados, _btRemover, _btIr, _btAbrir, _btAcao;
        private readonly TextBlock _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };

        // form
        private DocVinculo _editando;
        private readonly TextBlock _formTitulo = new TextBlock { FontSize = 13.5, FontWeight = FontWeights.SemiBold };
        private readonly TextBox _arquivo = new TextBox();
        private readonly TextBox _nome = new TextBox();
        private readonly StackPanel _comoPainel = new StackPanel { Orientation = Orientation.Horizontal };
        private readonly ComboBox _aba = new ComboBox { IsEditable = true };
        private readonly TextBox _intervalo = new TextBox(), _paginas = new TextBox(), _escala = new TextBox { Text = "100" }, _largura = new TextBox();
        private readonly CheckBox _fonteExcel = new CheckBox { Content = "Usar as fontes da planilha", IsChecked = true };
        private readonly CheckBox _pb = new CheckBox { Content = "Preto e branco" };
        private readonly TextBlock _previa = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) };
        private readonly TextBlock _destino = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 0, 0, 10) };
        private FrameworkElement _fComo, _fAba, _fIntervalo, _fPaginas, _fEscala, _fLargura, _fFonte;
        private string _tipo;
        private bool _ok;

        public DocVinculosWindow(Document doc, View ativa)
        {
            _doc = doc;
            _ativa = ativa;
            Width = Math.Min(1180, SystemParameters.WorkArea.Width - 80);
            Height = Math.Min(760, SystemParameters.WorkArea.Height - 40);
            MinWidth = 940; MinHeight = 520;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });
            var esq = ToolChrome.Card(MontarLista(), "No projeto", MontarBotoesLista());
            esq.Margin = new Thickness(0, 0, 10, 12);
            body.Children.Add(esq);
            var dir = ToolChrome.Card(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = MontarForm() });
            dir.Margin = new Thickness(0, 0, 0, 12);
            Grid.SetColumn(dir, 1);
            body.Children.Add(dir);

            var footer = new DockPanel();
            var fechar = ToolChrome.Secondary("Fechar", (s, e) => Close());
            fechar.IsCancel = true;
            DockPanel.SetDock(fechar, Dock.Right);
            footer.Children.Add(fechar);
            _status.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Muted");
            footer.Children.Add(_status);
            ToolChrome.DressCompact(this, Titulo, body, footer);

            Recarregar();
            LimparForm();
        }

        // ==================================================================== list

        private UIElement MontarBotoesLista()
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(ToolChrome.Ghost("+ Novo", "Limpa o formulário para vincular outro arquivo.", (s, e) => { _grid.SelectedItem = null; LimparForm(); }));
            return sp;
        }

        private UIElement MontarLista()
        {
            var dock = new DockPanel();
            var barra = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            _btMudados = ToolChrome.Primary("Atualizar os que mudaram", (s, e) => Atualizar(_linhas.Where(l => l.Estado == DocMotor.EstadoMudou).ToList()));
            _btMarcados = ToolChrome.Secondary("Atualizar marcados", (s, e) => Atualizar(_linhas.Where(l => l.Marcado).ToList()), "Lê de novo os arquivos marcados, mesmo que não tenham mudado.");
            _btIr = ToolChrome.Secondary("Ir para", (s, e) => Ir(), "Abre a folha ou vista onde o selecionado está.");
            _btAbrir = ToolChrome.Secondary("Abrir arquivo", (s, e) => AbrirArquivo(), "Abre o arquivo de origem (Excel, Word, PDF) para editar.");
            _btRemover = ToolChrome.Ghost("Remover", "Apaga a tabela/as imagens do projeto e o vínculo.", (s, e) => Remover());
            foreach (Button b in new[] { _btMudados, _btMarcados, _btIr, _btAbrir, _btRemover }) { b.Margin = new Thickness(0, 0, 6, 4); barra.Children.Add(b); }
            DockPanel.SetDock(barra, Dock.Top);
            dock.Children.Add(barra);

            _grid.ItemsSource = _linhas;
            _grid.AutoGenerateColumns = false;
            _grid.CanUserAddRows = false;
            _grid.CanUserDeleteRows = false;
            _grid.IsReadOnly = false;
            _grid.HeadersVisibility = DataGridHeadersVisibility.Column;
            _grid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
            _grid.RowHeight = 28;
            _grid.BorderThickness = new Thickness(0);
            _grid.Background = WM.Brushes.White;
            _grid.SelectionMode = DataGridSelectionMode.Single;
            _grid.SetResourceReference(DataGrid.HorizontalGridLinesBrushProperty, "Ja.Line");
            var check = new FrameworkElementFactory(typeof(CheckBox));
            check.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding(nameof(VinculoLinha.Marcado)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            check.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            check.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            _grid.Columns.Add(new DataGridTemplateColumn { Header = "", Width = new DataGridLength(30), CellTemplate = new DataTemplate { VisualTree = check } });
            _grid.Columns.Add(Col("Nome", nameof(VinculoLinha.Nome), new DataGridLength(1.6, DataGridLengthUnitType.Star)));
            _grid.Columns.Add(Col("Tipo", nameof(VinculoLinha.Tipo), new DataGridLength(104)));
            _grid.Columns.Add(Col("Arquivo", nameof(VinculoLinha.Arquivo), new DataGridLength(1.2, DataGridLengthUnitType.Star)));
            _grid.Columns.Add(Col("Conteúdo", nameof(VinculoLinha.Detalhe), new DataGridLength(110)));
            _grid.Columns.Add(Col("Onde", nameof(VinculoLinha.Onde), new DataGridLength(1, DataGridLengthUnitType.Star)));
            var est = Col("Estado", nameof(VinculoLinha.Estado), new DataGridLength(118));
            var st = new Style(typeof(TextBlock));
            st.Setters.Add(new Setter(TextBlock.ForegroundProperty, new Binding(nameof(VinculoLinha.CorEstado))));
            st.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold));
            st.Setters.Add(new Setter(VerticalAlignmentProperty, VerticalAlignment.Center));
            est.ElementStyle = st;
            _grid.Columns.Add(est);
            _grid.Columns.Add(Col("Lido em", nameof(VinculoLinha.Atualizado), new DataGridLength(108)));
            _grid.SelectionChanged += (s, e) =>
            {
                if (_grid.SelectedItem is VinculoLinha l) Editar(l.V);
                Botoes();
            };
            _grid.MouseDoubleClick += (s, e) => { if (_grid.SelectedItem != null) Ir(); };

            var vazio = ToolChrome.Muted("Nenhum documento vinculado ainda. Escolha um arquivo à direita: Excel ou CSV viram uma tabela do Revit; PDF, Word e Excel também podem entrar como imagem.");
            vazio.Margin = new Thickness(4, 12, 4, 0);
            var g = new Grid();
            g.Children.Add(_grid);
            g.Children.Add(vazio);
            _linhas.CollectionChanged += (s, e) => vazio.Visibility = _linhas.Count == 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            dock.Children.Add(g);
            return dock;
        }

        private static DataGridTextColumn Col(string h, string p, DataGridLength w)
        {
            var st = new Style(typeof(TextBlock));
            st.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            st.Setters.Add(new Setter(VerticalAlignmentProperty, VerticalAlignment.Center));
            return new DataGridTextColumn { Header = h, Binding = new Binding(p), Width = w, IsReadOnly = true, ElementStyle = st };
        }

        private void Recarregar()
        {
            string sel = (_grid.SelectedItem as VinculoLinha)?.V.Id;
            var marcados = new HashSet<string>(_linhas.Where(l => l.Marcado).Select(l => l.V.Id));
            _linhas.Clear();
            foreach (DocVinculo v in DocArmazem.Ler(_doc).Itens.OrderBy(x => x.Nome, StringComparer.CurrentCultureIgnoreCase))
            {
                var vistas = DocMotor.Onde(_doc, v);
                string onde = !DocMotor.ElementosExistem(_doc, v) ? "(apagado do projeto)"
                    : vistas.Count == 0 ? (v.Tipo == DocTipo.Tabela ? "só no navegador" : "—")
                    : string.Join(", ", vistas.Take(3).Select(x => x is ViewSheet f ? f.SheetNumber : x.Name)) + (vistas.Count > 3 ? $" +{vistas.Count - 3}" : "");
                _linhas.Add(new VinculoLinha(v) { Onde = onde, Estado = DocMotor.Estado(_doc, v), Marcado = marcados.Contains(v.Id) });
            }
            int mudou = _linhas.Count(l => l.Estado == DocMotor.EstadoMudou), falta = _linhas.Count(l => l.Estado == DocMotor.EstadoSemArquivo);
            _btMudados.Content = mudou > 0 ? $"Atualizar os que mudaram ({mudou})" : "Atualizar os que mudaram";
            _status.Text = $"{_linhas.Count} vínculo(s)" + (mudou > 0 ? $" · {mudou} mudaram" : "") + (falta > 0 ? $" · {falta} sem arquivo" : "") +
                           " · ao abrir o projeto, os que mudaram são avisados.";
            if (sel != null) _grid.SelectedItem = _linhas.FirstOrDefault(l => l.V.Id == sel);
            Botoes();
        }

        private void Botoes()
        {
            var sel = _grid.SelectedItem as VinculoLinha;
            _btMudados.IsEnabled = _linhas.Any(l => l.Estado == DocMotor.EstadoMudou);
            _btMarcados.IsEnabled = _linhas.Count > 0;
            _btIr.IsEnabled = sel != null && DocMotor.ElementosExistem(_doc, sel.V);
            _btAbrir.IsEnabled = sel != null && DocMotor.Arquivo(_doc, sel.V) != null;
            _btRemover.IsEnabled = sel != null || _linhas.Any(l => l.Marcado);
        }

        // ==================================================================== form

        private UIElement MontarForm()
        {
            var sp = new StackPanel();
            _formTitulo.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Ink");
            _formTitulo.Margin = new Thickness(0, 0, 0, 10);
            sp.Children.Add(_formTitulo);

            var linhaArq = new DockPanel();
            var procurar = ToolChrome.Secondary("Procurar…", (s, e) => Procurar());
            procurar.Margin = new Thickness(6, 0, 0, 0);
            DockPanel.SetDock(procurar, Dock.Right);
            linhaArq.Children.Add(procurar);
            linhaArq.Children.Add(_arquivo);
            _arquivo.LostFocus += (s, e) => ArquivoMudou();
            _arquivo.KeyDown += (s, e) => { if (e.Key == Key.Enter) ArquivoMudou(); };
            sp.Children.Add(ToolChrome.Labeled("Arquivo (Excel, CSV, PDF ou Word)", linhaArq));
            sp.Children.Add(ToolChrome.Labeled("Nome no projeto", _nome, "Nome da tabela/imagens no navegador (prefixo “JA Doc - ”)."));
            sp.Children.Add(_fComo = ToolChrome.Labeled("Entrar como", ToolChrome.Track(_comoPainel)));

            _aba.SelectionChanged += (s, e) => Dispatcher.BeginInvoke(new Action(Previa));
            _aba.LostFocus += (s, e) => Previa();
            sp.Children.Add(_fAba = ToolChrome.Labeled("Aba", _aba));
            _intervalo.LostFocus += (s, e) => Previa();
            _intervalo.KeyDown += (s, e) => { if (e.Key == Key.Enter) Previa(); };
            sp.Children.Add(_fIntervalo = ToolChrome.Labeled("Intervalo (ex.: A1:H30)", _intervalo, "Vazio = área de impressão da aba; sem ela, toda a área usada."));
            sp.Children.Add(_fPaginas = ToolChrome.Labeled("Páginas (ex.: 1-3, 5)", _paginas, "Vazio = todas; páginas novas no fim do documento entram ao atualizar."));

            var tam = new Grid();
            tam.ColumnDefinitions.Add(new ColumnDefinition());
            tam.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            tam.ColumnDefinitions.Add(new ColumnDefinition());
            _fEscala = ToolChrome.Labeled("Tamanho (%)", _escala, "100% = mesmas larguras, alturas e tamanhos de fonte da planilha impressa.");
            _fLargura = ToolChrome.Labeled("Largura na folha (mm)", _largura, "Vazio = tamanho da página.");
            Grid.SetColumn(_fLargura, 2);
            tam.Children.Add(_fEscala);
            tam.Children.Add(_fLargura);
            sp.Children.Add(tam);

            var opts = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            _fonteExcel.SetResourceReference(StyleProperty, "Ja.Toggle");
            _pb.SetResourceReference(StyleProperty, "Ja.Toggle");
            _fonteExcel.Margin = new Thickness(0, 0, 10, 4);
            _fonteExcel.ToolTip = "Desligado: usa a fonte do tipo de texto da tabela no Revit.";
            _pb.ToolTip = "Ignora cores de texto e de fundo.";
            opts.Children.Add(_fonteExcel);
            opts.Children.Add(_pb);
            _fFonte = opts;
            sp.Children.Add(opts);

            _previa.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Accent");
            sp.Children.Add(_previa);
            _destino.SetResourceReference(TextBlock.ForegroundProperty, "Ja.Muted");
            sp.Children.Add(_destino);
            _btAcao = ToolChrome.Primary("Vincular e colocar", (s, e) => Acao());
            _btAcao.HorizontalAlignment = HorizontalAlignment.Left;
            sp.Children.Add(_btAcao);
            return sp;
        }

        private void LimparForm()
        {
            _editando = null;
            _formTitulo.Text = "Novo vínculo";
            _arquivo.Text = ""; _nome.Text = ""; _intervalo.Text = ""; _paginas.Text = ""; _escala.Text = "100"; _largura.Text = "";
            _fonteExcel.IsChecked = (ToolPrefs.Get("docvinc.fonte") ?? "1") == "1";
            _pb.IsChecked = ToolPrefs.Get("docvinc.pb") == "1";
            _aba.ItemsSource = null; _aba.Text = "";
            _tipo = null;
            _arquivo.IsReadOnly = false;
            MontarComo(new string[0]);
            Atualizar();
        }

        private void Editar(DocVinculo v)
        {
            _editando = v;
            _formTitulo.Text = "Editar: " + v.Nome;
            _arquivo.Text = DocMotor.Arquivo(_doc, v) ?? v.Caminho;
            _nome.Text = v.Nome;
            _intervalo.Text = v.Intervalo;
            _paginas.Text = v.Paginas;
            _escala.Text = v.Escala.ToString("0.#", CultureInfo.CurrentCulture);
            _largura.Text = v.LarguraMm > 0 ? v.LarguraMm.ToString("0.#", CultureInfo.CurrentCulture) : "";
            _fonteExcel.IsChecked = v.FonteExcel;
            _pb.IsChecked = v.PretoBranco;
            _tipo = v.Tipo;
            MontarComo(new[] { v.Tipo });
            CarregarAbas(v.Aba);
            Atualizar();
        }

        private void Procurar()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Documentos (Excel, CSV, PDF, Word)|*.xlsx;*.xlsm;*.xls;*.xlsb;*.csv;*.pdf;*.docx;*.doc;*.rtf|Excel|*.xlsx;*.xlsm;*.xls;*.xlsb|CSV|*.csv;*.txt|PDF|*.pdf|Word|*.docx;*.doc;*.rtf",
                InitialDirectory = ToolPrefs.Get("docvinc.pasta") ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };
            try { if (File.Exists(_arquivo.Text)) dlg.InitialDirectory = Path.GetDirectoryName(_arquivo.Text); } catch { }
            if (dlg.ShowDialog(this) != true) return;
            _arquivo.Text = dlg.FileName;
            ToolPrefs.Set("docvinc.pasta", Path.GetDirectoryName(dlg.FileName));
            ArquivoMudou();
        }

        private string _ultimoArquivo;

        private void ArquivoMudou()
        {
            string f = _arquivo.Text.Trim().Trim('"');
            if (f == _ultimoArquivo) return;
            _ultimoArquivo = f;
            if (_editando == null) _nome.Text = File.Exists(f) ? Path.GetFileNameWithoutExtension(f) : "";
            string[] tipos = DocTipo.Possiveis(f);
            if (_editando != null)
            {
                // an existing link keeps its kind; only files of a compatible kind are accepted
                if (!tipos.Contains(_editando.Tipo)) _previa.Text = "Este arquivo não serve para um vínculo do tipo " + DocTipo.Nome(_editando.Tipo) + ".";
                tipos = new[] { _editando.Tipo };
            }
            _tipo = tipos.FirstOrDefault();
            MontarComo(tipos);
            CarregarAbas(_editando?.Aba);
            Atualizar();
        }

        private void MontarComo(string[] tipos)
        {
            _comoPainel.Children.Clear();
            foreach (string t in tipos)
            {
                var rb = new RadioButton
                {
                    Content = t == DocTipo.Tabela ? "Tabela do Revit" : "Imagem",
                    GroupName = "dv-como", IsChecked = t == _tipo, IsEnabled = _editando == null,
                    ToolTip = t == DocTipo.Tabela ? "Tabela nativa (cabeçalho de tabela de quantitativos): textos, fontes, cores, bordas e células mescladas; nítida em qualquer escala."
                                                  : "Imagem da página impressa pelo Excel: fiel ao visual (quebras de linha, gráficos). Precisa do Excel instalado."
                };
                rb.SetResourceReference(StyleProperty, "Ja.Segment");
                string tt = t;
                rb.Checked += (s, e) => { _tipo = tt; Atualizar(); };
                _comoPainel.Children.Add(rb);
            }
        }

        private void CarregarAbas(string escolher)
        {
            string f = _arquivo.Text.Trim().Trim('"');
            _aba.ItemsSource = null;
            if (!File.Exists(f)) return;
            string ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext != ".xlsx" && ext != ".xlsm") { _aba.Text = escolher ?? ""; return; }
            try
            {
                var abas = PlanilhaLeitor.Abas(f);
                _aba.ItemsSource = abas;
                _aba.SelectedItem = abas.FirstOrDefault(a => string.Equals(a, escolher, StringComparison.OrdinalIgnoreCase)) ?? abas.FirstOrDefault();
            }
            catch (Exception ex) { _previa.Text = ex.Message; }
        }

        /// <summary>Shows/hides fields for the chosen kind and refreshes preview, destination and button.</summary>
        private void Atualizar()
        {
            string f = _arquivo.Text.Trim().Trim('"');
            bool tem = File.Exists(f);
            bool tabela = _tipo == DocTipo.Tabela, excel = _tipo == DocTipo.Tabela || _tipo == DocTipo.ExcelImagem;
            bool csv = tem && PlanilhaLeitor.EhCsv(f);
            Vis(_fComo, _comoPainel.Children.Count > 1 || (_editando != null && _tipo != null));
            Vis(_fAba, excel && !csv);
            Vis(_fIntervalo, excel);
            Vis(_fPaginas, _tipo == DocTipo.Pdf || _tipo == DocTipo.Word || _tipo == DocTipo.ExcelImagem);
            Vis(_fEscala, tabela);
            Vis(_fLargura, _tipo != null && !tabela);
            Vis(_fFonte, tabela);
            _ok = tem && _tipo != null;
            if (!tem) _previa.Text = string.IsNullOrWhiteSpace(f) ? "" : "Arquivo não encontrado.";
            else if (_tipo == null) { _previa.Text = "Tipo de arquivo não suportado."; _ok = false; }
            else if (_tipo == DocTipo.Word && !OfficePdf.TemWord) { _previa.Text = "Word como imagem precisa do Microsoft Word instalado neste computador."; _ok = false; }
            else if (_tipo == DocTipo.ExcelImagem && !OfficePdf.TemExcel) { _previa.Text = "Excel como imagem precisa do Microsoft Excel instalado neste computador."; _ok = false; }
            else Previa();

            if (_editando != null)
            {
                _btAcao.Content = "Salvar e atualizar";
                _destino.Text = "Lê o arquivo de novo com estas opções; a posição na folha não muda.";
            }
            else
            {
                _btAcao.Content = "Vincular e colocar";
                bool folha = _ativa is ViewSheet;
                _destino.Text = tabela
                    ? (folha ? $"Depois de clicar, indique na folha {((ViewSheet)_ativa).SheetNumber} onde fica o canto superior esquerdo."
                             : "A vista ativa não é uma folha: a tabela fica no navegador (Tabelas) para você arrastar para uma folha.")
                    : (_ativa is View3D || _ativa is ViewSchedule || _ativa == null
                        ? "A vista ativa não aceita imagens: será criada uma vista de desenho com as páginas."
                        : "Depois de clicar, indique na vista ativa onde fica o canto superior esquerdo; as páginas ficam lado a lado.")
                    + (_tipo == DocTipo.Word || _tipo == DocTipo.ExcelImagem ? " O PDF gerado fica em “_JA Vínculos”, ao lado do arquivo." : "");
            }
            _btAcao.IsEnabled = _ok;
        }

        private static void Vis(FrameworkElement e, bool v) { if (e != null) e.Visibility = v ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed; }

        private void Previa()
        {
            string f = _arquivo.Text.Trim().Trim('"');
            if (_tipo != DocTipo.Tabela || !File.Exists(f)) { if (_tipo == DocTipo.Pdf) _previa.Text = "PDF: " + Path.GetFileName(f); return; }
            string ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext == ".xls" || ext == ".xlsb") { _previa.Text = "Arquivo antigo do Excel: será convertido pelo Excel instalado ao vincular."; return; }
            try
            {
                Cursor = Cursors.Wait;
                PlTabela t = PlanilhaLeitor.Ler(f, _aba.Text, _intervalo.Text);
                double larg = t.LargurasMm.Sum() * Num(_escala.Text, 100) / 100, alt = t.AlturasMm.Sum() * Num(_escala.Text, 100) / 100;
                _previa.Text = $"{t.Intervalo} · {t.Linhas} linhas × {t.Colunas} colunas · ≈ {larg:0} × {alt:0} mm" + (t.Mesclas.Count > 0 ? $" · {t.Mesclas.Count} mesclas" : "");
                _ok = true;
            }
            catch (Exception ex) { _previa.Text = ex.Message; _ok = false; }
            finally { Cursor = null; _btAcao.IsEnabled = _ok; }
        }

        private static double Num(string s, double padrao)
        {
            s = (s ?? "").Trim().Replace(',', '.');
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 0 ? v : padrao;
        }

        private DocVinculo DoForm(DocVinculo baseV)
        {
            DocVinculo v = baseV == null ? new DocVinculo() : Clone(baseV);
            DocMotor.DefinirCaminho(_doc, v, _arquivo.Text.Trim().Trim('"'));
            v.Tipo = _tipo;
            v.Nome = string.IsNullOrWhiteSpace(_nome.Text) ? Path.GetFileNameWithoutExtension(v.Caminho) : _nome.Text.Trim();
            v.Aba = _fAba.Visibility == System.Windows.Visibility.Visible ? (_aba.Text ?? "").Trim() : "";
            v.Intervalo = _fIntervalo.Visibility == System.Windows.Visibility.Visible ? _intervalo.Text.Trim() : "";
            v.Paginas = _fPaginas.Visibility == System.Windows.Visibility.Visible ? _paginas.Text.Trim() : "";
            v.Escala = Num(_escala.Text, 100);
            v.LarguraMm = string.IsNullOrWhiteSpace(_largura.Text) ? 0 : Num(_largura.Text, 0);
            v.FonteExcel = _fonteExcel.IsChecked == true;
            v.PretoBranco = _pb.IsChecked == true;
            return v;
        }

        private static DocVinculo Clone(DocVinculo v) => new DocVinculo
        {
            Id = v.Id, Nome = v.Nome, Tipo = v.Tipo, Caminho = v.Caminho, Relativo = v.Relativo, Aba = v.Aba, Intervalo = v.Intervalo,
            Escala = v.Escala, Paginas = v.Paginas, LarguraMm = v.LarguraMm, PretoBranco = v.PretoBranco, FonteExcel = v.FonteExcel,
            Carimbo = v.Carimbo, Tamanho = v.Tamanho, Pdf = v.Pdf, Elementos = v.Elementos.ToList(), PaginasLigadas = v.PaginasLigadas.ToList(),
            Atualizado = v.Atualizado, IntervaloLido = v.IntervaloLido
        };

        private void Acao()
        {
            if (!_ok) return;
            ToolPrefs.Set("docvinc.fonte", _fonteExcel.IsChecked == true ? "1" : "0");
            ToolPrefs.Set("docvinc.pb", _pb.IsChecked == true ? "1" : "0");
            if (_editando == null)
            {
                Novo = DoForm(null);
                DialogResult = true;
                return;
            }
            DocVinculo v = DoForm(_editando);
            var problemas = new List<string>();
            Executar("Editar vínculo", () => DocMotor.Salvar(_doc, v, problemas));
            Recarregar();
            if (problemas.Count > 0) Aviso(string.Join("\n", problemas.Take(12)));
        }

        // ==================================================================== actions

        private void Executar(string nome, Action acao)
        {
            Cursor = Cursors.Wait;
            try
            {
                using (var t = new Transaction(_doc, "JA Tools - " + Titulo + ": " + nome))
                {
                    t.Start();
                    RevitDwgUnifier.Export.FailureSwallower.Attach(t);
                    acao();
                    t.Commit();
                }
            }
            catch (Exception ex) { Logger.Error("[Documentos Vinculados] " + nome, ex); Aviso(ex.Message); }
            finally { Cursor = null; }
        }

        private void Atualizar(List<VinculoLinha> alvo)
        {
            if (alvo.Count == 0) return;
            var problemas = new List<string>();
            int n = 0;
            Cursor = Cursors.Wait;
            try { n = DocMotor.AtualizarVarios(_doc, alvo.Select(l => l.V.Id), problemas); }
            catch (Exception ex) { problemas.Add(ex.Message); }
            finally { Cursor = null; }
            Recarregar();
            _status.Text = $"{n} de {alvo.Count} atualizado(s).";
            if (problemas.Count > 0) Aviso($"{n} de {alvo.Count} atualizado(s).\n\n" + string.Join("\n", problemas.Take(12)));
        }

        private void Remover()
        {
            var alvo = _linhas.Where(l => l.Marcado).ToList();
            if (alvo.Count == 0 && _grid.SelectedItem is VinculoLinha sel) alvo.Add(sel);
            if (alvo.Count == 0) return;
            if (MessageBox.Show(this, $"Apagar {alvo.Count} vínculo(s) e suas tabelas/imagens do projeto?\n\n" + string.Join("\n", alvo.Take(10).Select(l => "• " + l.Nome)) +
                                "\n\nOs arquivos de origem não são apagados.", "JA Tools — " + Titulo, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            Executar("Remover", () => { foreach (var l in alvo) DocMotor.Remover(_doc, l.V.Id); });
            LimparForm();
            Recarregar();
        }

        private void Ir()
        {
            if (!(_grid.SelectedItem is VinculoLinha l)) return;
            View v = DocMotor.Onde(_doc, l.V).FirstOrDefault()
                     ?? l.V.Elementos.Select(u => _doc.GetElement(u)).OfType<ViewSchedule>().FirstOrDefault();
            if (v == null) return;
            IrPara = v;
            DialogResult = true;
        }

        private void AbrirArquivo()
        {
            if (!(_grid.SelectedItem is VinculoLinha l)) return;
            string f = DocMotor.Arquivo(_doc, l.V);
            if (f == null) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(f) { UseShellExecute = true }); }
            catch (Exception ex) { Aviso(ex.Message); }
        }

        private void Aviso(string texto) =>
            MessageBox.Show(this, texto, "JA Tools — " + Titulo, MessageBoxButton.OK, MessageBoxImage.Information);

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            try { ToolPrefs.Save(); } catch { }
        }
    }
}
