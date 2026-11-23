using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;

namespace RevitDwgUnifier.Tools.Common
{
    /// <summary>Didactic description of one JA Tools button: what it does, how to use it, the
    /// verb of its main action and help for its options. One source for the ribbon tooltips,
    /// the dialog headers and the Help Center page.</summary>
    public sealed class ToolInfo
    {
        public string Title;
        public string Group;
        public string Glyph;
        /// <summary>Key of the line icon (IconFactory.Paths); null = lettered tile.</summary>
        public string Icon => ToolCatalog.IconOf(Title);
        public string Description;
        public string[] Steps = new string[0];
        public string Verb = "Executar";
        public string[] Tips = new string[0];
        public Dictionary<string, string> FieldHelp = new Dictionary<string, string>();
        public string Anchor => new string(Title.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        public Color Accent => ToolCatalog.GroupColor(Group);
    }

    /// <summary>
    /// The one catalog of JA para Revit (both tabs): this file has the JA Tools half and joins it with
    /// the JA Famílias half (ToolCatalog.Familias.cs). Ribbon tooltips, option windows, results, the
    /// Central de Ajuda and the Central JA / Buscar Comando all read it.
    /// </summary>
    public static partial class ToolCatalog
    {
        public const string AbaFamilias = "JA Famílias", AbaTools = "JA Tools";

        // Groups = ribbon panels, in the order of the work: model, create views, detail, dimension,
        // assemble sheets, export; then project data and the office settings.
        public const string Exportar = "Exportar", Cotas = "Cotas", Vistas = "Vistas", Modelagem = "Modelagem", Parametros = "Dados";
        public const string Detalhamento = "Detalhamento", Publicacao = "Pranchas", Geral = "Geral";

        private static readonly string[] GroupOrderTools = { Modelagem, Vistas, Detalhamento, Cotas, Publicacao, Exportar, Parametros, Geral };

        /// <summary>Both tabs: the JA Famílias panels, then the JA Tools ones.</summary>
        public static readonly string[] GroupOrder = GroupOrderFamilias().Concat(GroupOrderTools).ToArray();

        /// <summary>Ribbon tab of a group.</summary>
        public static string Aba(string group) => GroupOrderFamilias().Contains(group) ? AbaFamilias : AbaTools;

        /// <summary>"JA Famílias" or "JA Tools": the tab of a tool (window titles, breadcrumbs).</summary>
        public static string Marca(string toolTitle)
        {
            ToolInfo t = string.IsNullOrEmpty(toolTitle) ? null : All.FirstOrDefault(x => string.Equals(x.Title, toolTitle, StringComparison.OrdinalIgnoreCase));
            return t == null || string.IsNullOrEmpty(t.Group) ? "JA" : Aba(t.Group);
        }

        public static Color GroupColor(string group)
        {
            switch (group)
            {
                case Modelagem: return Color.FromRgb(0x6B, 0x4F, 0xA0);
                case Vistas: return Color.FromRgb(0x1F, 0x7A, 0x5A);
                case Detalhamento: return Color.FromRgb(0x0F, 0x55, 0x66);
                case Cotas: return Color.FromRgb(0xB0, 0x53, 0x1A);
                case Publicacao: return Color.FromRgb(0x5C, 0x6B, 0x33);
                case Exportar: return Color.FromRgb(0x2B, 0x5B, 0x9A);
                case Parametros: return Color.FromRgb(0x4F, 0x5D, 0x6E);
                default: return CorFamilias(group) ?? Color.FromRgb(0x18, 0x21, 0x2B);
            }
        }

        public static string GroupIntro(string group)
        {
            switch (group)
            {
                case Exportar: return "Leve o projeto para fora do Revit: DWG único, PDF, imagens e Excel.";
                case Cotas: return "Cotas automáticas na planta ativa, a partir de paredes, pisos, forros e pontos. As paredes são cotadas sempre pela estrutura (osso), nunca pelo revestimento — nem pelas camadas de acabamento da parede, nem por paredes cebola.";
                case Vistas: return "Criação de vistas em lote: plantas, cortes, elevações, 3D e eixos, já configurados.";
                case Modelagem: return "Modelagem por ambiente (pisos, luminárias, tabica), pontos de instalações com cliques, paredes cebola e conversão em camadas. Forro e rodapé: aba JA Famílias.";
                case Parametros: return "Organização dos dados do projeto: numeração, níveis e limpeza de ambientes.";
                case Detalhamento: return "Detalhamento automático: ambientes detalhados em um clique (planta, elevações, 3D, cotas, etiquetas e prancha), planta anotada, fichas por tipo, etiquetas com os identificadores do escritório, hachuras e ajuste de anotações.";
                case Publicacao: return "Montagem e organização das pranchas: vistas arrastadas para o carimbo, pranchas renomeadas, renumeradas e duplicadas em lote e vistas alinhadas entre pranchas.";
                case Geral: return "Configurações do JA para Revit compartilhadas pela equipe (as duas abas).";
                default: return IntroFamilias(group) ?? "";
            }
        }

        private static readonly Dictionary<string, string> Common = Juntar(new Dictionary<string, string>
        {
            ["template"] = "Modelo de vista aplicado às vistas criadas (escala, visibilidade, detalhamento). '(nenhum)' mantém o padrão do tipo de vista.",
            ["scale"] = "Escala das vistas criadas, ex.: 50 para 1:50. Se o modelo de vista controlar a escala, prevalece o modelo.",
            ["dimType"] = "Estilo das cotas criadas (texto, setas, unidades). '(padrão do projeto)' usa o tipo de cota padrão.",
            ["rooms"] = "Marque os ambientes. Dica: selecione ambientes no Revit antes de clicar no botão para que já venham marcados.",
            ["levels"] = "Uma vista será criada para cada nível marcado.",
            ["name"] = "Nome das vistas criadas. Os textos entre chaves são trocados automaticamente; se o nome já existir, é numerado.",
            ["folder"] = "Pasta onde os arquivos serão gravados (criada se não existir).",
            ["pattern"] = "Nome dos arquivos. Variáveis: {numero} {nome} {revisao} {projeto} {codigo} {data}.",
            ["margin"] = "Folga em volta do elemento, em metros.",
            ["offset"] = "Distância da linha de cota, em metros.",
        }, CommonFamilias());

        private static readonly List<ToolInfo> All = FerramentasFamilias().Concat(new List<ToolInfo>
        {
            // ------------------------------------------------------------------ Exportar
            new ToolInfo { Title = "Exportar Pranchas", Group = Exportar, Glyph = "EXP", Verb = "Exportar",
                Description = "Central de exportação: PDF, um DWG único com um layout por folha (via AutoCAD), DWG por prancha e PNG, de pranchas e de vistas avulsas, tudo de uma vez e com o nome no padrão do escritório.",
                Steps = new[] { "Marque as pranchas e/ou vistas na lista (filtro Folhas / Vistas).", "Escolha os formatos: PDF, DWG único, DWG por prancha e/ou PNG.", "Confira a pasta e o nome dos arquivos e clique em Exportar." },
                Tips = new[] { "Os padrões já funcionam para a maioria dos projetos. Pasta e nomes ficam sempre à vista em Destino; as opções mostram só o que os formatos marcados usam (PDF, PNG, DWG, DWG único, Avançado).",
                               "PNG (antes Exportar Vistas): imagem de cada folha ou vista marcada; com fundo transparente o branco vira transparente, ideal para pranchas de apresentação.",
                               "Selecione pranchas ou vistas no Navegador de Projeto antes de abrir: elas já vêm marcadas.",
                               "Se um arquivo estiver aberto no AutoCAD, um novo nome numerado é usado.",
                               "Notas de instalações vão para as layers JA - INST_ (aba DWG) quando o tipo de texto tem a disciplina no nome: ex. \"INST_GAS 2,5mm\" vai para JA - INST_GAS." } },
            new ToolInfo { Title = "Gestão de Pranchas", Group = Publicacao, Glyph = "GP",
                Description = "Monte pranchas arrastando vistas para elas. As vistas se organizam sozinhas dentro do carimbo.",
                Steps = new[] { "Selecione vistas na lista da esquerda (Ctrl/Shift para várias).", "Arraste até uma prancha da direita (ou use Colocar).", "Ajuste margens e use Organizar se quiser reorganizar." } },
            new ToolInfo { Title = "Documentos Vinculados", Group = Publicacao, Glyph = "DV", Verb = "Vincular",
                Description = "Como o Ideate Sticky: vincula Excel/CSV como tabela nativa do Revit (fontes, cores, bordas, mesclas) e PDF, Word ou Excel como imagem; avisa quando o arquivo muda e atualiza com um clique.",
                Steps = new[] { "Abra a folha onde o documento vai ficar.", "Escolha o arquivo, a aba/intervalo ou as páginas e clique em Vincular e colocar.", "Clique na folha onde fica o canto superior esquerdo." },
                Tips = new[] { "Ao abrir o projeto, os documentos que mudaram são avisados; “Atualizar agora” lê todos de novo.",
                               "Tabela: vazio em Intervalo usa a área de impressão da aba (ou a área usada). Linhas e colunas ocultas não entram.",
                               "Word e “Excel como imagem” usam o Office instalado e geram um PDF na pasta “_JA Vínculos” ao lado do arquivo.",
                               "O caminho é guardado também relativo ao projeto: mover a pasta do projeto inteira não quebra o vínculo." } },
            new ToolInfo { Title = "Mapeador de Folhas", Group = Publicacao, Glyph = "MF", Verb = "Criar pranchas",
                Description = "Como o Sheet Mapper do Glyph: pega as vistas que ainda não estão em pranchas e cria as pranchas de uma vez — uma por vista, N por prancha ou o que couber — com carimbo, numeração e nomes, já arrumadas dentro do carimbo.",
                Steps = new[] { "Filtre e marque as vistas sem prancha (as selecionadas no navegador já vêm marcadas).", "Escolha como agrupar, o carimbo, o nº inicial e o nome.", "Confira o plano na grade (dá para editar número e nome) e clique em Criar pranchas." },
                Tips = new[] { "“Encaixar” usa o mesmo arranjo da Gestão de Pranchas: linhas a partir do canto superior esquerdo, as pequenas preenchendo os vãos.",
                               "Cada vista mantém a sua escala. Vista maior que a área útil vai sozinha e é avisada.",
                               "Números já usados no projeto são pulados. Campos do nome: {vista}, {vistas}, {nivel}, {tipo}, {n}." } },
            new ToolInfo { Title = "Etiquetar e Cotar em Lote", Group = Cotas, Glyph = "EC", Verb = "Aplicar",
                Description = "Em todas as plantas escolhidas: etiqueta as portas, janelas e ambientes que ainda não têm etiqueta (identificadores do escritório, posição automática) e cota as aberturas das paredes externas — uma corrente por parede, do lado de fora.",
                Steps = new[] { "Marque as plantas.", "Escolha o que etiquetar e se quer as cotas (só paredes externas, afastamento, tipo de cota).", "Aplicar: uma transação por planta." },
                Tips = new[] { "O que já está etiquetado ou cotado é pulado: dá para rodar de novo.", "Paredes externas = Função Exterior no tipo da parede.", "Cotas internas: como em Cotas por Ambiente, para cada ambiente da planta." } },
            new ToolInfo { Title = "Quadro de Acabamentos", Group = Parametros, Glyph = "QA", Verb = "Preencher",
                Description = "Preenche o acabamento de piso, parede e forro de cada ambiente pelos materiais modelados (camada de cima do piso, camada da parede voltada para o ambiente, camada de baixo do forro) e cria a tabela “JA - Quadro de Acabamentos”.",
                Steps = new[] { "Selecione ambientes (opcional).", "Escolha o que preencher e se sobrescreve o que foi digitado.", "Preencher: a tabela abre no fim." },
                Tips = new[] { "Paredes com várias camadas: vale a camada do lado do ambiente.", "Pintura aplicada com a ferramenta Pintar conta (a da face voltada para o ambiente).", "O rodapé fica como você digitar (coluna Rodapé)." } },
            new ToolInfo { Title = "Quadro de Áreas", Group = Parametros, Glyph = "QR", Verb = "Gerar",
                Description = "Quadro de áreas para a prefeitura: área útil e construída por pavimento, não computáveis, área permeável e os índices sobre o terreno (taxa de ocupação, coeficiente de aproveitamento, taxa de permeabilidade). Sai em Excel e numa vista de desenho para a prancha.",
                Steps = new[] { "Área do terreno (lida da linha de propriedade, se houver).", "Construída pelos pisos ou por um esquema de áreas; pavimento da projeção.", "Palavras dos ambientes não computáveis e permeáveis." },
                Tips = new[] { "As regras mudam por município: confira o que é computável na lei de uso do solo.", "Para a área construída oficial, desenhe um esquema de áreas “Bruta” e escolha-o." } },
            new ToolInfo { Title = "Conferir Acessibilidade", Group = Parametros, Glyph = "AC", Verb = "Conferir",
                Description = "Primeira conferência da NBR 9050: vão livre das portas, círculo de giro de 1,50 m nos sanitários acessíveis (louças e móveis como obstáculo) e largura das circulações pela extensão. Excel e os elementos que não atendem selecionados.",
                Steps = new[] { "Confira as palavras que identificam sanitários acessíveis e circulações.", "Ajuste o vão livre e o desconto da folha.", "Conferir: leia o resumo; o que não atende fica selecionado." },
                Tips = new[] { "Nomeie o sanitário com PCD ou Acessível para ele entrar.", "Barras, alturas de louças e sinalização não entram: é uma conferência geométrica." } },
            new ToolInfo { Title = "Conferir Escadas", Group = Parametros, Glyph = "ES", Verb = "Conferir",
                Description = "Confere todas as escadas do projeto: espelho e piso, fórmula de Blondel (2e + p), largura dos lances, desnível sem patamar e corrimãos. Limites da NBR 9050 ou de uso privativo, editáveis.",
                Steps = new[] { "Escolha a referência (rota acessível ou uso privativo).", "Ajuste os limites se o código de obras pedir outros.", "Conferir: as escadas que não atendem ficam selecionadas." },
                Tips = new[] { "Corrimão conta pelos guarda-corpos associados à escada (Revit: Guarda-corpo › Colocar na escada)." } },
            new ToolInfo { Title = "Carimbo em Lote", Group = Parametros, Glyph = "CB", Verb = "Aplicar",
                Description = "Preenche de uma vez as Informações do projeto e os campos das pranchas (desenhado, verificado, aprovado, data), mais qualquer parâmetro como “Nome = valor”; depois lista o que ainda está vazio em cada prancha.",
                Steps = new[] { "Preencha só o que quer mudar (vazio não mexe).", "Marque as pranchas (ou selecione-as antes).", "Aplicar e confira a planilha de campos vazios." },
                Tips = new[] { "Em “Conferir vazios”, ponha os nomes dos parâmetros que o seu carimbo mostra." } },
            new ToolInfo { Title = "Comparar Versões", Group = Parametros, Glyph = "CV", Verb = "Comparar",
                Description = "Compara o modelo aberto com outra versão (um arquivo .rvt anterior, aberto em segundo plano sem salvar): ambientes, portas e janelas por tipo, pranchas, níveis, metros de parede por tipo e quantidade por categoria. Sai em Excel.",
                Steps = new[] { "Salve uma cópia da emissão anterior (ou use o backup .0001.rvt).", "Clique em Comparar Versões e escolha esse arquivo.", "Leia o resumo e a planilha de diferenças." },
                Tips = new[] { "Ambientes são comparados pelo elemento: renomear aparece como alteração, não como novo." } },
            new ToolInfo { Title = "Quantitativo de Pintura", Group = Parametros, Glyph = "PT", Verb = "Calcular",
                Description = "Área de pintura por ambiente — paredes (perímetro × altura, menos portas, janelas e rodapé) e teto — e os litros de tinta e selador em latas de 18 L e galões de 3,6 L. Ambientes com parede revestida ficam fora das paredes.",
                Steps = new[] { "Marque os ambientes.", "Pé-direito (0 = altura do ambiente), rodapé e revestimentos.", "Demãos e rendimento da tinta; Calcular." },
                Tips = new[] { "Rode o Quadro de Acabamentos antes: o acabamento de parede diz quais ambientes são azulejados.",
                               "Ambientes com o Rodapé da aba JA Famílias descontam a altura real dele; o valor da janela vale para os outros." } },
            new ToolInfo { Title = "Verificar Modelo", Group = Exportar, Glyph = "VM", Verb = "Verificar",
                Description = "Confere o modelo contra o padrão do escritório: avisos do Revit, vistas fora de pranchas, pranchas vazias, nomes com “Cópia”, CAD importado, famílias sem uso e ambientes soltos. Depois do relatório, a janela Corrigir apaga o que você marcar (famílias sem uso, pranchas vazias, vistas “Cópia” fora de pranchas, ambientes não colocados), com a lista como prévia.",
                Steps = new[] { "Clique em Verificar Modelo e leia o relatório.", "Na janela Corrigir, ligue “Apagar” nos grupos que quiser e desmarque o que deve ficar.", "Ctrl+Z desfaz a correção inteira." },
                Tips = new[] { "Rode antes de cada entrega.", "Feche a janela Corrigir para só conferir." } },
            new ToolInfo { Title = "Planilha de Parâmetros", Group = Exportar, Glyph = "⇄", Verb = "Continuar",
                Description = "Ida e volta Revit → Excel → Revit: gera um Excel com uma aba por tabela escolhida, uma linha por elemento (com o ID) e depois importa a planilha editada, alterando só os valores que mudaram — com uma prévia do que vai mudar.",
                Steps = new[] { "Gerar a planilha a partir de uma tabela do Revit.", "Editar os valores no Excel e salvar (.csv ou .xlsx).", "Importar a planilha editada e conferir a prévia." },
                Tips = new[] { "Não altere a coluna ID nem os títulos das colunas.", "Parâmetros de tipo só mudam se a opção estiver ligada (valem para todos os elementos do tipo).", "Colunas calculadas, materiais, níveis e parâmetros só de leitura são ignorados." } },
            new ToolInfo { Title = "Exportar Tabelas", Group = Exportar, Glyph = "XLS", Verb = "Exportar para Excel",
                Description = "Exporta tabelas do Revit para Excel (.xlsx) com as cores do escritório — com os valores como dados: áreas, comprimentos, quantidades, % e R$ viram números somáveis, com a unidade no formato da célula.",
                Steps = new[] { "Marque as tabelas.", "Defina as cores (ficam salvas).", "Exportar para Excel." },
                Tips = new[] { "Os números são lidos com o separador decimal e de milhar das Unidades do projeto (Gerenciar › Unidades do projeto).",
                               "Códigos (J01, 007, 101A), datas e medidas em pés e polegadas continuam como texto.",
                               "Desmarque “Números como dados” para exportar tudo como texto, igual ao que o Revit mostra." },
                FieldHelp = { ["headerColor"] = "Cor hexadecimal, ex.: 1F7A5A.", ["mode"] = "Uma planilha com uma aba por tabela, ou um arquivo para cada tabela." } },

            // ------------------------------------------------------------------ Cotas
            // ------------------------------------------------------------------ Publicação
            new ToolInfo { Title = "Renomear Pranchas", Group = Publicacao, Glyph = "RP", Verb = "Renomear",
                Description = "Muda várias pranchas de uma vez: procurar e substituir no nome ou no número, prefixo, sufixo, maiúsculas e renumeração em sequência.",
                Steps = new[] { "Marque as pranchas (as selecionadas no Navegador já vêm marcadas).", "Preencha o que mudar.", "Renomear." },
                Tips = new[] { "A renumeração troca números entre pranchas sem conflito (usa números temporários).", "Números usados por pranchas fora da lista são pulados." } },
            new ToolInfo { Title = "Duplicar Pranchas", Group = Publicacao, Glyph = "DP", Verb = "Duplicar",
                Description = "Cria cópias de pranchas com o mesmo carimbo e campos, anotações da folha, legendas, tabelas e as vistas (duplicadas, com detalhamento ou dependentes) na mesma posição.",
                Steps = new[] { "Marque as pranchas.", "Escolha quantas cópias e o que fazer com as vistas.", "Duplicar." } },
            new ToolInfo { Title = "Alinhar Vistas", Group = Publicacao, Glyph = "AV", Verb = "Alinhar",
                Description = "Alinha as vistas de várias pranchas pela prancha de referência: plantas de pavimentos diferentes ficam sobrepostas no papel, ou cada vista vai para a mesma posição.",
                Steps = new[] { "Escolha a prancha de referência.", "Marque as pranchas a alinhar.", "Alinhar." } },
            new ToolInfo { Title = "Central JA", Group = Geral, Glyph = "CJ", Verb = "Abrir",
                Description = "Como o Glyph, numa barra lateral leve (ou janela flutuante) que não trava o Revit: uma busca para as ferramentas das duas abas (JA Tools e JA Famílias), tarefas e pacotes, favoritas e recentes, tarefas (ferramenta + opções salvas), pacotes com 1 clique, escopo e status.",
                Steps = new[] { "Clique em Central JA: abre na barra lateral (de novo = esconde). ⧉ passa para janela flutuante; ⇥ volta para a lateral.", "Digite na busca e tecle Enter para rodar o primeiro resultado; ☆ numa ferramenta salva as opções como tarefa.", "Pacotes: junte tarefas e rode com ▶. O chip “Escopo” define onde trabalham; o resultado aparece em Status." },
                Tips = new[] { "Toda janela de opções do JA (nas duas abas) tem “☆ Tarefa” no rodapé: salva as opções atuais como tarefa sem rodar.",
                               "Tarefas de ferramentas com janela própria (ex.: Exportar Pranchas) abrem a janela já com as opções salvas e esperam o seu clique.",
                               "Exporte tarefas e pacotes (.jatarefas) e ponha numa pasta compartilhada (“Pasta do escritório”): a equipe toda vê, somente leitura.",
                               "O escopo filtra as listas das ferramentas (ambientes, níveis, vistas, folhas) pelo nome — ex.: só os ambientes do 2º pavimento." } },
            new ToolInfo { Title = "Buscar Comando", Group = Geral, Glyph = "BC", Verb = "Rodar",
                Description = "Paleta de comandos: digite parte do nome de uma ferramenta, tarefa ou pacote e tecle Enter.",
                Steps = new[] { "Abra a busca (dica: atribua um atalho em Arquivo › Opções › Interface do usuário › Atalhos de teclado, procurando “Buscar Comando”).", "Digite — sem acentos, em qualquer ordem, ou as iniciais (ex.: “dp” para Duplicar Pranchas).", "↑/↓ escolhe, Enter roda. “t:” busca só tarefas, “p:” só pacotes." },
                Tips = new[] { "Os mais usados aparecem primeiro.", "Tarefas e pacotes rodados pela busca mostram o resultado na Central JA (Status)." } },
            new ToolInfo { Title = "Estudar Vista", Group = Geral, Glyph = "EV", Verb = "Estudar",
                Description = "Grava num arquivo (JSON) tudo o que compõe a vista ativa — configurações, modelo de vista, faixa de vista, recorte, filtros, cada etiqueta, cota, cota de nível, texto, símbolo e item de detalhe com família, tipo, parâmetros e posição — e uma imagem PNG da vista. Serve para o Claude estudar uma vista feita à mão e ensinar as ferramentas a reproduzi-la. Não muda nada no modelo.",
                Steps = new[] { "Abra a vista modelo.", "Clique em Estudar Vista.", "Envie o .json e o .png (pasta Documentos › JA Tools › Estudo de vistas)." },
                Tips = new[] { "Não altera o projeto.", "Posições em mm a partir do canto inferior esquerdo do recorte da vista." } },
            new ToolInfo { Title = "Uso e Telemetria", Group = Geral, Glyph = "UT", Verb = "Continuar",
                Description = "Relatório do uso do JA no escritório (Excel): ferramentas mais e menos usadas, desistências na janela de opções, Ctrl+Z logo depois, erros e avisos, opções mais escolhidas, sequências de ferramentas, usuários e versões, uso por semana — e uma aba de sugestões de melhoria tiradas desses números. Também liga/desliga a coleta, deixa anônima e escolhe a pasta central.",
                Steps = new[] { "Escolha Relatório de uso e o período.", "Continuar: a planilha abre com Resumo, Sugestões e as abas de detalhe.", "Configurar a coleta: ligar/desligar, anônimo e pasta central (padrão: pasta de atualizações\\Telemetria)." },
                Tips = new[] { "Cada computador grava em %LOCALAPPDATA%\\RevitDwgUnifier\\Telemetria e copia para a pasta central ao abrir e fechar o Revit: o relatório junta todos.",
                               "Nada do conteúdo do projeto é gravado: o projeto vira um código; mensagens com números viram # (12 peças = # peças).",
                               "“Desfeitas” = Ctrl+Z na transação da ferramenta em até 2 minutos: sinal de resultado inesperado.",
                               "Avise a equipe que a coleta existe (LGPD); quem preferir pode deixá-la anônima ou desligada." } },
            new ToolInfo { Title = "Exportar Relatório de Uso", Group = Geral, Glyph = "RU", Verb = "Exportar",
                Description = "Exporta o relatório de uso do JA para onde você escolher: a planilha do Excel (Resumo, Sugestões de melhoria, Ferramentas, Opções, Erros, Avisos, Usuários, Sem uso, Sequências, Por semana, Contexto) e, se quiser, os dados brutos em CSV (um evento por linha) para Power BI ou tabela dinâmica.",
                Steps = new[] { "Escolha o período, de onde vêm os dados (pasta central e/ou este computador) e o formato.", "Exportar: escolha a pasta e o nome do arquivo.", "O arquivo abre em seguida (opção)." },
                Tips = new[] { "Esconder os nomes troca os usuários por Usuário 1, Usuário 2… para mostrar à equipe.",
                               "Ligar/desligar a coleta e a pasta central: Uso e Telemetria › Configurar." } },
            new ToolInfo { Title = "Padrões do Escritório", Group = Geral, Glyph = "PD", Verb = "Continuar",
                Description = "Exporta e importa num arquivo .jatools todas as configurações do JA — as duas abas: opções das ferramentas, modelos de detalhe, exportação e as configurações do JA Famílias (esquadrias, perfis, materiais) —, para toda a equipe trabalhar igual.",
                Steps = new[] { "Exportar: salve o arquivo numa pasta da rede.", "Na máquina de cada pessoa: Importar o mesmo arquivo." } },

            // ------------------------------------------------------------------ Detalhamento
            new ToolInfo { Title = "Detalhar Ambientes", Group = Detalhamento, Glyph = "DA", Verb = "Detalhar",
                Description = "Detalha ambientes em um clique: planta recortada com cotas em todas as direções, etiquetas posicionadas como no desenho à mão, região de planta nas janelas altas, quatro elevações com cotas gerais e das bancadas, isométrico e a prancha montada — com pré-visualização para arrastar as vistas.",
                Steps = new[] { "Marque os ambientes (os selecionados já vêm marcados).", "Escolha vistas, escala, tipos de etiqueta, cota e texto, e a prancha (nova ou existente).", "Detalhar: confira a pré-visualização da prancha, arraste as vistas se quiser e clique em Aplicar layout." },
                Tips = new[] { "Portas e janelas recebem a etiqueta em frente, no sentido da parede; peças compridas (tento, soleira) sobre a peça; louças ao lado, no espaço livre da bancada.",
                               "Região de planta: o Revit não deixa criar do zero. Desenhe uma vez uma região de planta modelo (com o corte acima das janelas altas e JA em Comentários) e o JA Tools a copia para cada janela.",
                               "Ajuste as margens da prancha para a área livre do seu carimbo." },
                FieldHelp = { ["elevDims"] = "Largura de face a face das paredes laterais (abaixo do piso) e altura do piso ao forro — ou à laje, sem forro — à esquerda.",
                              ["counterDims"] = "Altura do piso ao tampo de cada bancada encostada na parede vista (marcenaria, ou nome com bancada / tampo / granito...).",
                              ["ceilNote"] = "Quando há forro no ambiente, a elevação o corta: o texto vai logo abaixo da linha do forro.",
                              ["preview"] = "Mostra a prancha com a área útil e cada vista no tamanho real antes de colocar: arraste para montar o layout, ou Organizar automaticamente.",
                              ["dest"] = "Nova: uma prancha por ambiente com o carimbo escolhido. Existente: as vistas entram na prancha escolhida, desviando do que já está nela." } },
            new ToolInfo { Title = "Anotar Vista", Group = Detalhamento, Glyph = "AN", Verb = "Anotar",
                Description = "Coloca numa vista que já existe toda a anotação do Detalhar Ambientes, sem criar vistas nem pranchas: plantas recebem cotas, etiquetas de ambiente e dos elementos, pontos hidrossanitários cotados e etiquetados, ajuste de sobreposições e regiões de planta; elevações recebem cotas gerais, altura das bancadas e o texto do forro.",
                Steps = new[] { "Abra a planta ou a elevação — ou a prancha (anota todas as vistas dela), ou selecione várias vistas no navegador de projeto.", "Marque o que colocar (plantas, pontos hidrossanitários, elevações) e os tipos de cota, etiqueta e texto.", "Anotar: uma vista por vez, com o andamento na tela." },
                Tips = new[] { "1.17: junta o Anotar Planta e a Anotação Completa — os pontos hidrossanitários ficam no cartão próprio.",
                               "Ponto hidráulico ou de esgoto é reconhecido pelos conectores de tubulação da família (água fria/quente, esgoto) ou pelas palavras do nome (editáveis). Pias e lavatórios recebem as duas etiquetas.",
                               "Etiquetas não duplicam ao rodar de novo; as cotas, sim (rode uma vez por vista). Pontos sem Marca nem Marca de tipo não recebem etiqueta.",
                               "Combina com o modo Rápido do Detalhar Ambientes: crie todas as vistas e pranchas primeiro e anote depois, prancha por prancha." },
                FieldHelp = { ["adjust"] = "Etiquetas, textos e etiquetas de ambiente sobrepostos (entre si ou sobre o texto de uma cota) vão para o lugar livre mais próximo; longe do elemento, ganham linha de chamada. Cotas não são movidas.",
                              ["tagOff"] = "Onde a etiqueta nasce, na frente do ponto (medido no papel).",
                              ["onlySel"] = "Selecione ambientes e pontos na planta antes de abrir para anotar só eles." } },
            new ToolInfo { Title = "Etiquetar Elementos", Group = Detalhamento, Glyph = "EE", Verb = "Etiquetar",
                Description = "Etiqueta as categorias escolhidas na vista ativa com os identificadores do escritório (carregados automaticamente), na posição automática (em frente a portas, ao lado das louças, ao longo do tento) ou fixa, pulando os já etiquetados e os sem marca.",
                Steps = new[] { "Abra a vista.", "Marque as categorias e a posição da etiqueta.", "Etiquetar." } },
            new ToolInfo { Title = "Organizar Anotações", Group = Detalhamento, Glyph = "OA", Verb = "Organizar",
                Description = "Arruma as anotações da vista: afasta as sobrepostas para o lugar livre mais próximo, ou alinha / distribui com espaços iguais as etiquetas e textos selecionados.",
                Steps = new[] { "Abra a vista (para alinhar: selecione as etiquetas/textos).", "Escolha Sobreposições, Alinhar ou Distribuir.", "Organizar." },
                Tips = new[] { "1.17: junta o Ajustar Anotações e o Alinhar Anotações. Com duas ou mais anotações selecionadas abre em “Alinhar”.",
                               "Sobreposições não move cotas; longe do elemento, a etiqueta ganha linha de chamada." } },
            new ToolInfo { Title = "Hachura Única", Group = Detalhamento, Glyph = "HU", Verb = "Criar hachura",
                Description = "Num corte ou planta, cria UMA hachura (região preenchida) cobrindo o corte de vários elementos separados — laje, vigas, paredes e pilares —, como uma peça só de concreto. Com hachuras selecionadas, une as hachuras numa só.",
                Steps = new[] { "Abra o corte (ou a planta) — ou selecione as hachuras a unir.", "Escolha o que hachurar: selecionados, clicar, todos os cortados das categorias marcadas, ou unir hachuras.", "Escolha o tipo de região e criar." },
                Tips = new[] { "Elementos que se encostam viram uma forma só, sem as emendas; os separados entram na mesma hachura como ilhas.",
                               "1.17: o Unir Hachuras entrou aqui — com hachuras selecionadas a primeira opção é “Unir”; numa vista sem elementos cortados a ferramenta pede para clicar nas hachuras.",
                               "O contorno vem do corte real dos elementos no plano da vista (também com furos e vigas invertidas).",
                               "Sem contorno (linhas invisíveis), as linhas de corte dos elementos continuam desenhando as bordas.",
                               "Se o modelo mudar, apague a hachura e rode de novo: ela não acompanha os elementos.",
                               "“Unir partes separadas por até (cm)”: frestas menores que essa distância são fechadas e as partes viram uma peça só (as bordas retas voltam ao lugar exato); os furos das hachuras originais continuam furos." } },
            new ToolInfo { Title = "Varinha Mágica", Group = Detalhamento, Glyph = "VM", Verb = "Começar",
                Description = "Como a varinha mágica do ArchiCAD: um clique dentro de uma área fechada cria uma hachura, linhas de detalhe, um piso ou um forro seguindo o contorno dela. Atalho: Espaço (com nada selecionado). Cada clique cria um; Esc termina.",
                Steps = new[] { "Abra a planta, o corte ou a vista de desenho e aperte Espaço (ou clique no botão).", "No painel do canto, escolha o que criar e o tipo.", "Clique dentro das áreas fechadas; Esc ou Espaço termina." },
                Tips = new[] { "O atalho funciona com nada selecionado e fora de campos de texto; troque a tecla (ou desligue) em “alterar”, no rodapé do painel.",
                               "As opções do painel valem no clique seguinte: dá para trocar de hachura para piso sem sair da varinha. Arraste o painel pelo título para outro canto; o lugar fica guardado.",
                               "Em planta, “pelo ambiente” segue as paredes como um ambiente do Revit: fecha nas portas e os pilares viram furos.",
                               "“Pelas linhas e cortes” usa as linhas da vista, o contorno das hachuras e o corte dos elementos: funciona em cortes e vistas de desenho e acha a menor área fechada em volta do clique.",
                               "Clique fora de uma área fechada não cria nada (o painel conta esses cliques).",
                               "Pelas linhas, ilhas soltas dentro da área (um pilar sem ligação com o contorno) não viram furo." } },

            new ToolInfo { Title = "Cotas por Ambiente", Group = Cotas, Glyph = "CA", Verb = "Cotar ambientes",
                Description = "Cota as medidas internas de cada ambiente, de face a face das paredes, em todas as direções das paredes — paredes anguladas (45°, 30°, 27°, 11°...) também.",
                Steps = new[] { "Abra a planta onde as cotas devem aparecer.", "Escolha todos os ambientes da vista ou só os selecionados.", "Cotar ambientes." },
                FieldHelp = { ["offset"] = "Afasta as cotas do centro do ambiente para não sobrepor a etiqueta." },
                Tips = new[] { "Paredes: sempre a estrutura (osso). Camadas de acabamento da parede e paredes cebola (revestimento modelado como parede — pelas camadas, pelo nome ou fina encostada numa parede mais grossa) nunca são cotadas; selecionar um revestimento cota a parede dele." } },
            new ToolInfo { Title = "Cotas de Piso e Forro", Group = Cotas, Glyph = "CP", Verb = "Cotar",
                Description = "Cota pisos ou forros pelas suas arestas, em todas as direções, com cota total; nos forros, a tabica (folga até a parede) entra na mesma corrente.",
                Steps = new[] { "Abra a planta (de forro, para forros) — ou selecione os pisos/forros.", "Escolha Pisos ou Forros e os elementos (todos ou selecionados).", "Cotar." },
                Tips = new[] { "1.17: junta o Cotas por Piso e o Cotas por Forro. A seleção (ou uma planta de forro) já escolhe o tipo." } },
            new ToolInfo { Title = "Cotas por Seleção", Group = Cotas, Glyph = "CS", Verb = "Começar",
                Description = "Cota paredes selecionadas: em corrente (paralelas, na posição clicada) ou como fachada (aberturas, paredes que chegam e total, do lado de fora).",
                Steps = new[] { "Escolha Corrente ou Fachada e clique em Começar.", "Selecione as paredes e clique em Concluir na barra de opções (corrente: clique onde a cota passa).", "Repita ou tecle Esc." },
                Tips = new[] { "Paredes: sempre a estrutura (osso). Camadas de acabamento da parede e paredes cebola (revestimento modelado como parede — pelas camadas, pelo nome ou fina encostada numa parede mais grossa) nunca são cotadas; selecionar um revestimento cota a parede dele.", "1.17: o Cotas totais por Seleção virou o modo Fachada desta ferramenta." } },
            new ToolInfo { Title = "Cotas por Pontos", Group = Cotas, Glyph = "PT", Verb = "Começar",
                Description = "Desenhe uma linha: tudo o que ela cruzar é cotado.",
                Steps = new[] { "Clique em Começar.", "Clique o início e o fim da linha.", "Repita ou tecle Esc." },
                Tips = new[] { "Paredes: sempre a estrutura (osso). Camadas de acabamento da parede e paredes cebola (revestimento modelado como parede — pelas camadas, pelo nome ou fina encostada numa parede mais grossa) nunca são cotadas; selecionar um revestimento cota a parede dele." } },
            new ToolInfo { Title = "Cotas por Parede", Group = Cotas, Glyph = "CW", Verb = "Começar",
                Description = "Um clique numa parede encontra e cota as demais paredes paralelas naquela direção.",
                Steps = new[] { "Clique em Começar.", "Clique numa parede, no ponto por onde a cota deve passar.", "Repita ou tecle Esc." },
                Tips = new[] { "Paredes: sempre a estrutura (osso). Camadas de acabamento da parede e paredes cebola (revestimento modelado como parede — pelas camadas, pelo nome ou fina encostada numa parede mais grossa) nunca são cotadas; selecionar um revestimento cota a parede dele." } },
            new ToolInfo { Title = "Cotas de Pontos", Group = Cotas, Glyph = "PT", Verb = "Cotar pontos",
                Description = "Cota luminárias, pontos elétricos ou pontos hidrossanitários a partir das paredes; pontos alinhados formam uma fila com uma corrente só.",
                Steps = new[] { "Abra a planta (de forro, para luminárias).", "Escolha Luminárias, Elétricos ou Hidrossanitários e marque as categorias.", "Cotar pontos." },
                Tips = new[] { "1.17: junta Cotas de Luminárias, de Pontos Elétricos e de Pontos Hidrossanitários; cada escolha guarda as suas categorias.",
                               "A família precisa ter os planos de referência centrais definidos como referência." } },

            // ------------------------------------------------------------------ Vistas
            new ToolInfo { Title = "Cortes Gerais", Group = Vistas, Glyph = "CG", Verb = "Começar a desenhar",
                Description = "Cria cortes cotados a partir de cliques na planta, com a altura toda do edifício.",
                Steps = new[] { "Abra uma planta.", "Configure tipo, escala e profundidade.", "Clique início, fim e o lado do corte. Repita ou tecle Esc." } },
            new ToolInfo { Title = "Fachada Planificada", Group = Vistas, Glyph = "FP", Verb = "Criar fachada",
                Description = "Fachada com vários ângulos vista de frente: cada trecho em verdadeira grandeza, desdobrados lado a lado numa prancha.",
                Steps = new[] { "Selecione as paredes da fachada — ou abra uma planta sem nada selecionado.", "Configure nome, escala e recorte.", "Sem paredes: clique os cantos (Esc termina) e o lado de onde a fachada é vista." },
                Tips = new[] { "As vistas são cortes vivos (um por trecho): mudou o modelo, a fachada atualiza.",
                               "Paredes curvas viram trechos de até 15°.",
                               "“Afastamento” põe o plano de corte à frente da fachada: aumente para mostrar varandas e marquises.",
                               "Na prancha, cada trecho começa onde o anterior termina, com as bases alinhadas; a linha tracejada marca as dobras.",
                               "Um contorno fechado (todas as paredes do prédio) gera a volta inteira." } },
            new ToolInfo { Title = "Gerar Detalhes", Group = Detalhamento, Glyph = "GD", Verb = "Gerar detalhes",
                Description = "Um detalhe (ficha) por tipo, como o id:legend: portas, janelas, gabinetes, louças, equipamentos — qualquer família. Vistas frontal, laterais, posterior, planta e corte, cotas, marca e tabela, com modelos personalizáveis e montagem nas pranchas.",
                Steps = new[] { "Marque as categorias (só elas são carregadas) e os tipos.", "Escolha um modelo de ficha e ajuste vistas, layout, textos e tabela.", "Opcional: aba Pranchas — carimbo e quantas fichas por prancha.", "Gerar detalhes e acompanhar o progresso." },
                Tips = new[] { "Categorias que o Revit não aceita em legendas (louças, componentes de gabinete, conexões, equipamentos...) são desenhadas com vistas do modelo, a partir de uma instância colocada.",
                               "“Salvar como…” guarda o seu modelo de ficha para usar em outros projetos.",
                               "Com “Ajustar à prancha”, o tamanho da ficha sai do carimbo: 2 × 1 numa A4 em pé dá o caderno de esquadrias.",
                               "Estilo Relação: modelos prontos com a tabela Especificação | Compartimento | Quantidade, a planta cotada e os detalhes típicos (aba Detalhes típicos, que aparece no estilo Relação), três por prancha com a linha de OBS.",
                               "Com pranchas, a legenda leva só os desenhos e as cotas; moldura, linhas de separação e textos (marca, medidas, descrição, quantidade, localização) são criados na própria prancha e podem ser editados lá.",
                               "Atualizar fichas (rodapé): conta de novo os elementos e reescreve quantidades e localização das fichas já feitas — rode antes de imprimir o caderno (antes era um botão à parte).",
                               "“Fase dos elementos” (acima da lista de tipos): quantidades, ambientes e a instância desenhada do modelo saem só dos elementos existentes nessa fase." } },
            new ToolInfo { Title = "Ocultar em Vista", Group = Vistas, Glyph = "OV", Verb = "Aplicar",
                Description = "Em cortes, elevações e plantas, trata o que fica em vista além do plano de corte: oculta (ou afina) os elementos inteiros em vista das categorias escolhidas e, nos elementos cortados, oculta as peças além do corte e afina as linhas em vista — a parte cortada continua forte.",
                Steps = new[] { "Abra o corte ou a elevação (ou selecione vistas / pranchas no Navegador, ou os próprios elementos na vista).", "Marque as categorias e escolha ocultar, linha fina, cinza ou meio-tom.", "Aplicar." },
                Tips = new[] { "Vale só nessas vistas (Ocultar na vista e Sobrepor gráficos por elemento): nada muda no modelo nem nas outras vistas.",
                               "Num guarda-corpo cortado, o perfil cortado fica com a espessura de corte e os painéis/montantes vistos atrás ficam com a linha fina.",
                               "Peças de famílias aninhadas compartilhadas podem ser ocultadas sozinhas; as não compartilhadas ficam com a linha fina.",
                               "Selecione elementos na vista antes de abrir para tratar só eles, de qualquer categoria.",
                               "\"Só mudar a espessura de linha\" troca de uma vez a espessura (em vista e/ou cortada) de todos os elementos das categorias marcadas, cortados ou não, inclusive os modelados depois.",
                               "Rode de novo com \"Desfazer\" para voltar exatamente como estava; o que você ocultou ou sobrepôs à mão não é mexido.",
                               "Selecionando uma prancha, a ferramenta vale para todas as vistas dela." } },
            new ToolInfo { Title = "Plantas por Nível", Group = Vistas, Glyph = "PN", Verb = "Criar plantas",
                Description = "Cria plantas por nível — baixa (ambientes, portas e janelas etiquetados), de forro (forros) ou luminotécnica (luminárias) — já com modelo, escala, nome e etiquetas.",
                Steps = new[] { "Marque os níveis.", "Escolha a planta (Baixa, Forro, Luminotécnica), o modelo e a escala.", "Criar plantas." },
                Tips = new[] { "1.17: junta Planta Baixa, Plantas de Forro e Plantas Luminotécnicas; cada tipo guarda o seu modelo, nome e etiquetas." } },
            new ToolInfo { Title = "Vistas por Ambiente", Group = Vistas, Glyph = "VA", Verb = "Criar vistas",
                Description = "Planta, elevações internas e isométrico de cada ambiente — o que você marcar —, recortados no ambiente, numa passada só; opcionalmente, uma prancha por ambiente com as vistas arrumadas.",
                Steps = new[] { "Marque os ambientes (busca, nível, só os sem vistas).", "Marque o que criar (planta, elevações, isométrico) e ajuste cada cartão.", "Criar vistas." },
                Tips = new[] { "1.17: junta Vistas, Elevações e Isométrico por Ambiente. Ambientes selecionados no Revit já vêm marcados; Shift+clique marca um intervalo.",
                               "Elevações paralelas às paredes em qualquer ângulo: uma por direção de parede (A = de cima da planta, sentido horário; paredes inclinadas a mais viram E, F…), recortada na largura da parede. A planta e o isométrico giram com o ambiente.",
                               "“Só as que faltam” completa as vistas que ainda não existem; “Substituir” refaz as vistas com os mesmos nomes.",
                               "Nomes: {ambiente}, {numero}, {nome}, {nivel} e, nas elevações, {letra}." } },
            new ToolInfo { Title = "Vistas de Eixos", Group = Vistas, Glyph = "VE", Verb = "Criar plantas",
                Description = "Cria plantas de eixos para vários níveis, com os eixos cotados.",
                Steps = new[] { "Marque os níveis.", "Escolha modelo e escala.", "Criar plantas." } },
            new ToolInfo { Title = "Eixos de Paredes", Group = Vistas, Glyph = "EP", Verb = "Criar eixos",
                Description = "Cria eixos sobre o eixo das paredes, numerados e cotados.",
                Steps = new[] { "Abra a planta (opcional: selecione paredes).", "Escolha a numeração.", "Criar eixos." } },
            new ToolInfo { Title = "Recalcular Eixos", Group = Vistas, Glyph = "RE", Verb = "Recalcular",
                Description = "Depois de editar eixos, renomeia todos em ordem e refaz as cotas na vista ativa.",
                Steps = new[] { "Abra a planta de eixos.", "Escolha a numeração.", "Recalcular." } },

            // ------------------------------------------------------------------ Modelagem
            new ToolInfo { Title = "Pisos por Ambiente", Group = Modelagem, Glyph = "PI", Verb = "Criar pisos",
                Description = "Modela o piso de cada ambiente, com recortes e rebaixo nas áreas molhadas.",
                Steps = new[] { "Marque os ambientes.", "Escolha o tipo de piso e o rebaixo.", "Criar pisos." },
                Tips = new[] { "Depois, a Paginação de Piso e Parede (aba JA Famílias › Revestimentos) desenha as peças e calcula a compra nos mesmos ambientes." },
                FieldHelp = { ["wet"] = "Ambientes cujo nome contém uma destas palavras recebem o rebaixo. Separe por ponto e vírgula." } },
            new ToolInfo { Title = "Luminárias por Ambiente", Group = Modelagem, Glyph = "LA", Verb = "Calcular distribuição",
                Description = "Distribui luminárias uniformemente em cada ambiente, com prévia antes de alterar o modelo.",
                Steps = new[] { "Marque os ambientes e a luminária.", "Defina o espaçamento.", "Confira a prévia e confirme." } },
            new ToolInfo { Title = "Tabica por Ambiente", Group = Modelagem, Glyph = "TB", Verb = "Criar",
                Description = "Modela a tabica entre o forro e a parede no perímetro de cada ambiente, na altura do forro que já existe (forros feitos de outro jeito). Rodapé: use o Rodapé da aba JA Famílias; forro novo com tabica e sanca: Forro e Sanca.",
                Steps = new[] { "Crie um tipo de parede fino para a tabica (uma vez).", "Marque os ambientes e escolha o tipo e a altura.", "Criar." },
                Tips = new[] { "Ambientes sem forro são ignorados.", "2.0: o rodapé saiu daqui — o Rodapé do JA Famílias desconta portas, janelas e vãos, acerta os cantos e fica preso na parede." } },
            new ToolInfo { Title = "Varinha de Pontos", Group = Modelagem, Glyph = "VP", Verb = "Começar a clicar",
                Description = "Coloca as famílias de instalações do escritório com cliques, no estilo da Varinha Mágica: tomadas, interruptores, pontos de água e esgoto, ralos, dados, luminárias, quadros… Na planta, clique perto da parede, do lado do ambiente: a peça vai para a face mais próxima, de frente para o ambiente, na altura escolhida.",
                Steps = new[] { "Abra a planta do pavimento (ou um corte, elevação ou 3D).", "Escolha a família e a altura (0,30 tomada baixa, 1,10 interruptor, 2,20 tomada alta…).", "Clique perto de cada parede onde vai um ponto; Esc termina e abre a escolha da próxima família." },
                Tips = new[] { "Funciona com família de face (vai na face da parede), hospedada em parede, solta no nível (girada para o ambiente) e de anotação (símbolo na planta).",
                               "Longe das paredes (ralos, caixas de esgoto, pontos no piso) a peça vai no ponto clicado; família de face vai sobre o piso que estiver ali.",
                               "Em corte, elevação e 3D clique na própria face da parede: a altura é a do clique.",
                               "“Copiar a família de um elemento já colocado” pega o mesmo tipo de uma peça do projeto.",
                               "Cada família colocada se desfaz com um Ctrl+Z." },
                FieldHelp = { ["raio"] = "Clique a até esta distância da face da parede, do lado do ambiente.", ["afastamento"] = "Para famílias soltas no nível ou de anotação: distância do ponto até a face." } },
            new ToolInfo { Title = "Suspender Grupos", Group = Modelagem, Glyph = "SG", Verb = "Suspender",
                Description = "Como “Suspender grupos” do ArchiCAD: desfaz os grupos temporariamente para editar cada elemento sozinho; um clique depois junta tudo de novo.",
                Steps = new[] { "Clique em Suspender Grupos e escolha quais (selecionados, da vista ou do projeto).", "Edite, mova ou apague os elementos à vontade.", "Clique de novo — o botão agora diz Restaurar Grupos (n)." },
                Tips = new[] { "O estado fica gravado no projeto: dá para salvar, fechar e restaurar depois (ou outro usuário restaurar).",
                               "Grupo não alterado volta ao mesmo tipo (as cópias continuam cópias). Grupo alterado vira um tipo novo “… (editado)”, ou mantém o nome quando era a única cópia.",
                               "Grupos com grupo de detalhe anexado não são suspensos (o Revit apagaria os detalhes).",
                               "“Esquecer” deixa os elementos soltos de vez." } },
            new ToolInfo { Title = "Unir Elementos", Group = Modelagem, Glyph = "UE", Verb = "Unir",
                Description = "Une a geometria no projeto todo: paredes com seus revestimentos (paredes cebola — portas e janelas passam a cortar todas as camadas), paredes × pilares, paredes × pisos, pisos × pilares e vigas, com a ordem de corte certa — inclusive dentro de grupos, sem desagrupar nem quebrar nenhum.",
                Steps = new[] { "Escolha onde: selecionados, vista ativa ou o projeto todo.", "Marque o que unir (paredes cebola, pilares, pisos, vigas).", "Unir: o resumo mostra quantas uniões, ordens corrigidas e grupos atualizados." },
                Tips = new[] { "Ordem de corte: pilar corta viga, que corta piso, que corta parede; entre paredes, a mais grossa corta o revestimento.",
                               "Paredes cebola = paredes paralelas encostadas (até a folga indicada); depois de unidas, cada porta e janela da parede corta também o revestimento.",
                               "Grupos: o que o Revit aceitar é unido direto; grupos com várias cópias têm o tipo refeito com as uniões (mesmo nome, todas as cópias atualizadas e no mesmo lugar). Cópias com membros excluídos ou com grupo de detalhe anexado são puladas e listadas.",
                               "Nenhuma união que exigiria desagrupar é feita: ela é desfeita na hora e aparece no resumo.",
                               "Tudo fica num desfazer só por etapa (Ctrl+Z)." } },
            new ToolInfo { Title = "Ajustar Paredes Cebola", Group = Modelagem, Glyph = "AC", Verb = "Ajustar",
                Description = "Acerta as paredes de revestimento (paredes cebola) que estão um pouco afastadas da parede, entrando nela ou tortas: endireita e encosta cada uma na face da parede (camada sobre camada, de dentro para fora), fecha os cantos e as emendas entre revestimentos, une a geometria com tudo em que o revestimento encosta e prende o revestimento na parede (alinhamento com cadeado) — movendo a parede, o revestimento vai junto.",
                Steps = new[] { "Escolha onde: selecionados (a parede ou o revestimento), vista ativa ou o projeto todo.", "Diga o que é revestimento (espessura máxima ou palavras no nome do tipo) e até quantos mm corrigir.", "Confira a prévia (os maiores ajustes aparecem) e clique em Ajustar; os revestimentos ajustados ficam selecionados." },
                Tips = new[] { "Cerâmica sobre reboco: o reboco vai para a parede e a cerâmica para o reboco; duas camadas no mesmo lugar: a mais grossa fica por dentro.",
                               "Cantos: com a união das pontas ligada, as duas vão até o cruzamento (o Revit limpa o canto); com a união desligada, fica topo a topo — o mais comprido passa e o outro encosta.",
                               "Emendas: peças da mesma camada com fresta ou sobreposição pequena se encontram no meio (só se tiverem quase a mesma altura).",
                               "Uma parede fina sozinha (divisória) não é revestimento; o revestimento dela vai para ela.",
                               "O cadeado é um alinhamento travado entre as faces: aparece ao selecionar o revestimento numa planta do nível. Para soltar, clique no cadeado.",
                               "Paredes em grupos ou fixadas (pin) e paredes curvas ficam de fora e aparecem no resumo." } },
            new ToolInfo { Title = "Paredes nas Lajes", Group = Modelagem, Glyph = "PL", Verb = "Ajustar",
                Description = "Verifica paredes que atravessam a laje de cima (ou não chegam até ela) e ajusta a altura para o topo ficar rente à face de baixo da laje; opcionalmente sobe a base que começa dentro da laje de baixo. Funciona com lajes de qualquer espessura e nível, e dentro de grupos sem quebrá-los.",
                Steps = new[] { "Escolha onde: selecionados, vista ativa ou o projeto todo.", "Marque o que ajustar (paredes que entram na laje, que não chegam nela, base dentro da laje).", "Ajustar (ou “Só verificar”): o resumo mostra o que mudou e seleciona as paredes." },
                Tips = new[] { "A laje é medida em 7 pontos ao longo de cada parede: vale para lajes rebaixadas, de espessuras diferentes e em qualquer nível.",
                               "Parede com topo preso a um nível: muda o deslocamento superior; parede sem restrição: muda a altura.",
                               "Laje inclinada: use “Anexar o topo à laje”; em altura exata ela é só listada.",
                               "Não mexe em paredes de mais de um andar, com perfil editado, já anexadas, ou com laje só sobre parte dela — essas aparecem no resumo.",
                               "Grupos: o que o Revit aceitar é ajustado direto; grupos com várias cópias têm o tipo refeito (mesmo nome) quando todas as cópias precisam do mesmo ajuste." } },
            new ToolInfo { Title = "Converter em Camadas", Group = Modelagem, Glyph = "CC", Verb = "Converter",
                Description = "Converte o que estiver selecionado: paredes compostas em uma parede por camada (cantos em meia-esquadria, portas e janelas na camada principal) e pisos em camadas empilhadas com o contorno original.",
                Steps = new[] { "Selecione paredes e/ou pisos (ou clique neles ao abrir).", "Confira as opções.", "Converter." },
                Tips = new[] { "1.17: junta Parede em Camadas e Piso em Camadas.", "Os originais são excluídos; pisos inclinados são ignorados." } },

            // ------------------------------------------------------------------ Parâmetros
            new ToolInfo { Title = "Padronizar Nomes", Group = Parametros, Glyph = "PN", Verb = "Renomear",
                Description = "Dá nomes técnicos e claros às famílias e tipos do padrão de planta do escritório: etiquetas (JA - Etiqueta - Porta : Código + Soleira…), cotas de nível (JA - Nível - Piso Acabado), cotas, textos, tipo e modelo de vista. Só o nome muda; o desenho fica igual.",
                Steps = new[] { "Clique em Padronizar Nomes.", "Confira a lista “antigo → novo” e desmarque o que não quiser.", "Renomear." },
                Tips = new[] { "Os nomes antigos são achados sem acento e sem o prefixo LB.", "Detalhar Ambientes e as etiquetas JA reconhecem os nomes novos e os antigos." } },
            new ToolInfo { Title = "Gestor de Estilos", Group = Parametros, Glyph = "GE", Verb = "Aplicar",
                Description = "Como o Ideate StyleManager: todos os estilos do projeto com os usos — textos, cotas, etiquetas, linhas, padrões, modelos de vista, filtros, materiais, tipos e famílias. Veja onde estão, una, limpe e renomeie em lote.",
                Steps = new[] { "Escolha a categoria à esquerda (ex.: Tipos de texto).", "Marque estilos na tabela (Espaço marca as linhas selecionadas).", "À direita: Onde (abre a vista e seleciona), Unir, Limpar ou Renomear." },
                Tips = new[] { "≡ marca estilos idênticos com nomes diferentes; “Unir duplicados” deixa só o mais usado de cada grupo.",
                               "Unir passa todos os usos para o estilo escolhido e só apaga o antigo se ele ficar sem nenhum uso.",
                               "Cada ação é um Ctrl+Z. Estilos do Revit (🔒) não são apagados nem renomeados.",
                               "Estilos de linha são recriados com o novo nome (o Revit não permite renomear); as linhas passam para o novo." } },
            new ToolInfo { Title = "Renumerar Elementos", Group = Parametros, Glyph = "RN", Verb = "Renumerar",
                Description = "Renumera portas, janelas ou paredes em ordem (P01, J01...).",
                Steps = new[] { "Escolha a categoria e a ordem.", "Defina prefixo e número inicial.", "Renumerar." } },
            new ToolInfo { Title = "Preencher Parâmetros", Group = Parametros, Glyph = "PP", Verb = "Preencher",
                Description = "Preenche um parâmetro em massa — das instâncias ou dos tipos — nos elementos selecionados, ou nas categorias escolhidas da vista ou do projeto, com uma informação de cada elemento: nível, ambiente (portas pelo lado interno), faixas de elevação (pavimentos), tabela de/para, modelo de texto, fórmula (contas, condições e textos, como as expressões do Archicad), numeração {Seq}, texto fixo ou substituir um trecho do valor atual. Mostra a prévia antes de gravar e cria o parâmetro de texto se ele não existir.",
                Steps = new[] { "Selecione os elementos (opcional; o Filtrar Elementos seleciona por regras), escolha onde e as categorias.", "Escolha o parâmetro (ou um novo) e de onde vem o valor.", "Confira a prévia e grave." },
                Tips = new[] { "Pavimentos pela elevação: Com base em › Faixas de elevação, ex.: “7-10 = Térreo; 10-13 = Primeiro pavimento; 13-16 = Segundo pavimento”. A faixa inclui o início e não o fim.",
                               "Fórmula: ARRED({Comprimento} * {Altura}; 2) · SE({Altura} > 2,1; \"Alta\"; \"Normal\") · {Nível} & \" - \" & {Ambiente} · ESCOLHER({Nível}; \"Térreo\"; \"T\"; \"P\"). Comprimentos em m, áreas em m², volumes em m³; resultado numérico vai direto para parâmetros de comprimento/área.",
                               "Numeração: use {Seq} no modelo ou na fórmula, ex.: “{Nível:Nome}-{Seq}” ou CONCATENAR(\"P\"; {Seq}); escolha a ordem (nível e posição, valor atual ou a do Revit), onde recomeça (ex.: {Nível}), o início e os dígitos.",
                               "Campos especiais: " + string.Join(", ", RevitDwgUnifier.Tools.Parameters.ValoresElemento.Especiais) + "; e qualquer parâmetro do elemento ou do tipo. De outro elemento: {Ambiente:Acabamento piso}, {Tipo:Descrição}, {Hospedeiro:Tipo}, {Nível:Elevação}.",
                               RevitDwgUnifier.Tools.Parameters.Formula.Ajuda,
                               "Um valor só num trecho do prédio: marque “Só elementos entre as elevações” (De/Até) e use Texto fixo.",
                               "Ambiente: o ambiente onde o elemento está; portas e janelas pelo lado interno (para onde abrem), ou o lado que não é circulação, ou o lado externo.",
                               "Tabela de/para: compara um campo ({Tipo}, {Família}, {Ambiente}…) e grava o valor da linha, ex.: “Porta 80x210 = P1; Porta 90x210 = P2; * = Outra”.",
                               "Substituir: troca um trecho do que já está escrito (ex.: “Pav.” por “Pavimento”) sem mexer no resto.",
                               "Parâmetros [tipo]: o mesmo valor vai para todas as instâncias do tipo; tipos cujas instâncias pediriam valores diferentes são pulados.",
                               "Os rodapés do JA já saem com JA_Nivel preenchido; com o nome JA_Nivel é o mesmo parâmetro.",
                               "Números em texto aceitam as unidades do projeto (“2,50”, “250 cm”); Sim/Não aceitam Sim, Não, 1 ou 0; Letras passa o valor para MAIÚSCULAS, minúsculas ou primeira maiúscula." } },
            new ToolInfo { Title = "Filtrar Elementos", Group = Parametros, Glyph = "FE", Verb = "Filtrar",
                Description = "Como o Encontrar e Selecionar do Archicad: encontra os elementos que atendem a até cinco regras — nível, ambiente, elevação, comprimento, área, volume, altura, espessura, material, fase, grupo, subprojeto, hospedeiro… ou qualquer parâmetro (do elemento, do tipo, do ambiente, do nível, do hospedeiro), com 16 condições (igual, contém, começa, vazio, maior, entre, é um de, parece com * ?), ou uma fórmula verdadeira — combinadas com E / OU, na seleção, na vista ou no projeto. Seleciona (somar, tirar, inverter, mostrar), isola ou oculta na vista, destaca com cor por um filtro de vista ou exporta a lista para o Excel.",
                Steps = new[] { "Escolha onde procurar e as categorias.", "Monte as regras (E / OU) e o que fazer com o resultado.", "Filtrar." },
                Tips = new[] { "Ex.: Elevação (m) entre “10 a 13” → Selecionar; depois Preencher Parâmetros › Selecionados com Texto fixo “Primeiro pavimento”.",
                               "Ex.: Portas com Ambiente contém “banho” E Largura (m) menor que 0,8 → Destacar na vista em vermelho.",
                               "Fórmula (verdadeira): qualquer condição, ex.: E({Altura} > 2,1; CONTÉM({Tipo}; \"porta\"); NÃO(VAZIO({Marca}))).",
                               "É um de: “Térreo; Mezanino”. Parece com: * é qualquer trecho e ? uma letra (ex.: “P?0*”).",
                               "Campos com (m), (m²), (m³) são medidos pelo JA; os parâmetros comparam o número como o Revit mostra (2,50; 80 cm).",
                               "Selecionar os que NÃO atendem inverte o resultado. Selecionar e mostrar enquadra os elementos na vista.",
                               "“Salvar como conjunto de seleção” guarda o resultado no projeto (Gerenciar › Seleção › Carregar); Destacar já cria o conjunto e o aplica como filtro da vista.",
                               "Exportar a lista grava no Excel categoria, família, tipo, id, nível, ambiente, marca e os campos das regras.",
                               "Isolar/Ocultar são temporários: o óculos na barra de baixo da vista desfaz. Toda janela tem ☆ Tarefa para guardar as regras e repetir." } },
            new ToolInfo { Title = "Mudar Nível", Group = Parametros, Glyph = "NV", Verb = "Mudar nível",
                Description = "Muda o nível dos elementos selecionados — ou de categorias inteiras, na vista ativa ou no projeto todo — sem tirá-los do lugar: o deslocamento é corrigido pela diferença entre os níveis. Pode mudar só quem está num nível de origem e, se quiser, também o topo.",
                Steps = new[] { "Selecione os elementos (opcional).", "Escolha onde (seleção, vista ou projeto) e marque as categorias.", "Escolha o nível de origem (ou qualquer) e o de destino." },
                Tips = new[] { "Com “Qualquer nível”, só a base muda; marque “Também a restrição superior” para levar o topo junto.",
                               "Portas e janelas seguem a parede onde estão; elementos em grupos são pulados.",
                               "Para mover tudo de um nível para outro e apagar o antigo, use Editar Níveis › Revincular." } },
            new ToolInfo { Title = "Editar Níveis", Group = Parametros, Glyph = "NV", Verb = "Aplicar",
                Description = "Edita todos os níveis numa tabela, em metros: nome, elevação ou altura até o nível de cima. Revincula todos os elementos de um nível para outro sem tirá-los do lugar.",
                Steps = new[] { "Edite nomes, elevações ou alturas (a altura move o nível de cima).", "Opcional: revincule os elementos de um nível para outro.", "Adicione ou marque níveis para excluir.", "Aplicar." },
                Tips = new[] { "Revincular corrige o deslocamento de cada elemento: paredes, pisos e pilares ficam no mesmo lugar, e as cotas continuam presas a eles.",
                               "Excluir um nível apaga as plantas dele (com as cotas dessas plantas). Revincule antes; a exclusão é feita por último." } },
            new ToolInfo { Title = "Remover Ambientes", Group = Parametros, Glyph = "RA", Verb = "Remover",
                Description = "Remove ambientes não colocados, não delimitados ou redundantes que poluem as tabelas.",
                Steps = new[] { "Confira as quantidades.", "Marque o que remover.", "Remover." } },
        }).ToList();

        public static IReadOnlyList<ToolInfo> Tools => All;

        private static readonly Dictionary<string, string> Icons = Juntar(new Dictionary<string, string>
        {
            ["Exportar Pranchas"] = "exportar",
            ["Gestão de Pranchas"] = "pranchas",
            ["Documentos Vinculados"] = "vinculo",
            ["Mapeador de Folhas"] = "mapeador",
            ["Central JA"] = "central",
            ["Buscar Comando"] = "busca",
            ["Estudar Vista"] = "estudar",
            ["Exportar Tabelas"] = "tabela",
            ["Planilha de Parâmetros"] = "tabela",
            ["Renomear Pranchas"] = "renomear",
            ["Duplicar Pranchas"] = "duplicar",
            ["Alinhar Vistas"] = "alinhar",
            ["Padrões do Escritório"] = "padroes",
            ["Uso e Telemetria"] = "telemetria",
            ["Exportar Relatório de Uso"] = "exportar",
            ["Detalhar Ambientes"] = "detalhar",
            ["Anotar Vista"] = "anotar",
            ["Etiquetar Elementos"] = "etiqueta",
            ["Hachura Única"] = "hachura",
            ["Varinha Mágica"] = "varinha",
            ["Organizar Anotações"] = "alinhar",
            ["Cotas por Ambiente"] = "cota",
            ["Cotas de Piso e Forro"] = "cotaPiso",
            ["Cotas por Seleção"] = "cotaSelecao",
            ["Cotas por Pontos"] = "cotaPontos",
            ["Cotas por Parede"] = "paredes",
            ["Etiquetar e Cotar em Lote"] = "etiqueta",
            ["Quadro de Acabamentos"] = "tabela",
            ["Quadro de Áreas"] = "tabela",
            ["Cotas de Pontos"] = "gota",
            ["Cortes Gerais"] = "corte",
            ["Fachada Planificada"] = "fachada",
            ["Gerar Detalhes"] = "ficha",
            ["Ocultar em Vista"] = "ocultar",
            ["Plantas por Nível"] = "plantas",
            ["Vistas por Ambiente"] = "elevacao",
            ["Vistas de Eixos"] = "eixos",
            ["Eixos de Paredes"] = "eixosParede",
            ["Recalcular Eixos"] = "atualizar",
            ["Pisos por Ambiente"] = "piso",
            ["Luminárias por Ambiente"] = "lampada",
            ["Tabica por Ambiente"] = "forro",
            ["Unir Elementos"] = "unir",
            ["Ajustar Paredes Cebola"] = "cebola",
            ["Paredes nas Lajes"] = "paredesLajes",
            ["Converter em Camadas"] = "camadas",
            ["Suspender Grupos"] = "grupos",
            ["Varinha de Pontos"] = "pontos",
            ["Gestor de Estilos"] = "estilos",
            ["Padronizar Nomes"] = "padronizar",
            ["Renumerar Elementos"] = "numeros",
            ["Editar Níveis"] = "niveis",
            ["Mudar Nível"] = "niveis",
            ["Preencher Parâmetros"] = "preencher",
            ["Filtrar Elementos"] = "filtrar",
            ["Remover Ambientes"] = "lixeira",
            ["Conferir Acessibilidade"] = "acessibilidade",
            ["Conferir Escadas"] = "escada",
            ["Carimbo em Lote"] = "carimbo",
            ["Comparar Versões"] = "comparar",
            ["Quantitativo de Pintura"] = "pintura",
            ["Central de Ajuda"] = "ajuda",
        }, IconesFamilias());

        /// <summary>Line icon key of a tool (see IconFactory.Paths); null when it has none.</summary>
        /// <summary>The JA Tools entries win when both halves name the same key.</summary>
        private static Dictionary<string, string> Juntar(Dictionary<string, string> a, Dictionary<string, string> b)
        {
            foreach (var kv in b) if (!a.ContainsKey(kv.Key)) a[kv.Key] = kv.Value;
            return a;
        }

        public static string IconOf(string title) =>
            title != null && Icons.TryGetValue(title, out string k) ? k : null;

        public static ToolInfo Get(string title) =>
            All.FirstOrDefault(t => string.Equals(t.Title, title, StringComparison.OrdinalIgnoreCase))
            ?? new ToolInfo { Title = title, Group = "", Glyph = "JA", Description = "" };

        /// <summary>Help text of an option: the tool's own text first, then the common one.</summary>
        public static string FieldHelp(ToolInfo t, string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (t != null && t.FieldHelp.TryGetValue(key, out string s)) return s;
            return Common.TryGetValue(key, out string c) ? c : null;
        }
    }
}
