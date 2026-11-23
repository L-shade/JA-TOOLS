using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitDwgUnifier.Tools.Common
{
    /// <summary>
    /// Technical names of the office standard (JA): "JA - [what] - [variant]", category first, no
    /// "LB"/"EX"/"TAG"/"IND" leftovers. One row per family or type: old names (compared without accents
    /// and "LB") → new name. Used by Padronizar Nomes (renames in the project) and by every tool that
    /// looks for these types (it accepts the new and the old names).
    /// </summary>
    public static class PadraoNomes
    {
        public enum Tipo { Familia, TipoDeFamilia, TipoDeSistema, TipoDeVista, ModeloDeVista }

        public sealed class Regra
        {
            public Tipo Tipo;
            public string Grupo;            // shown in the list
            public string Familia;          // for TipoDeFamilia: the family's (old or new) name
            public string[] Antigos;
            public string Novo;
        }

        private static Regra F(string grupo, string novo, params string[] antigos) => new Regra { Tipo = Tipo.Familia, Grupo = grupo, Novo = novo, Antigos = antigos };
        private static Regra T(string grupo, string familiaNova, string novo, params string[] antigos) => new Regra { Tipo = Tipo.TipoDeFamilia, Grupo = grupo, Familia = familiaNova, Novo = novo, Antigos = antigos };
        private static Regra S(string grupo, string novo, params string[] antigos) => new Regra { Tipo = Tipo.TipoDeSistema, Grupo = grupo, Novo = novo, Antigos = antigos };

        public static readonly Regra[] Regras =
        {
            // views
            new Regra { Tipo = Tipo.TipoDeVista, Grupo = "Tipo de vista", Novo = "JA - Planta - Nível Acabado", Antigos = new[] { "Planta. Nivel Acabado", "Planta Nivel Acabado" } },
            new Regra { Tipo = Tipo.ModeloDeVista, Grupo = "Modelo de vista", Novo = "JA - PE - Planta Baixa Civil", Antigos = new[] { "PE - Plantas. Civil", "PE - Plantas Civil" } },

            // tags (families, then their types)
            F("Etiqueta de ambiente", "JA - Etiqueta - Ambiente", "IND NOME AMBIENTE"),
            T("Etiqueta de ambiente", "JA - Etiqueta - Ambiente", "Nome + Área", "NOME E AREA"),
            T("Etiqueta de ambiente", "JA - Etiqueta - Ambiente", "Nome + Área + Coluna", "NOME, AREA E COLUNA"),
            T("Etiqueta de ambiente", "JA - Etiqueta - Ambiente", "Nome + Área (sem PD)", "NOME E AREA S/ PD"),
            T("Etiqueta de ambiente", "JA - Etiqueta - Ambiente", "Nome", "NOME"),
            F("Etiqueta de porta", "JA - Etiqueta - Porta", "Tag Porta EX"),
            T("Etiqueta de porta", "JA - Etiqueta - Porta", "Código + Soleira", "Com Soleira"),
            T("Etiqueta de porta", "JA - Etiqueta - Porta", "Código", "Sem Soleira"),
            F("Etiqueta de janela", "JA - Etiqueta - Janela", "Tag Janela EX"),
            T("Etiqueta de janela", "JA - Etiqueta - Janela", "Código + Peitoril", "Com Peitoril"),
            T("Etiqueta de janela", "JA - Etiqueta - Janela", "Código", "Sem Peitoril"),
            F("Etiqueta de guarda-corpo", "JA - Etiqueta - Guarda-corpo", "TAG - Identificador de Guarda corpo", "Identificador de Guarda corpo"),
            T("Etiqueta de guarda-corpo", "JA - Etiqueta - Guarda-corpo", "Código", "Sem Soleira"),
            F("Etiqueta de equipamento hidráulico", "JA - Etiqueta - Equipamento Hidráulico", "JA - Identificador de Equipamentos Hidráulicos"),
            F("Etiqueta de equipamento mecânico", "JA - Etiqueta - Equipamento Mecânico", "JA - Identificador de Equipamentos Dispositivos Mecânicos"),
            F("Etiqueta de acessório de tubo", "JA - Etiqueta - Acessório de Tubo", "Identificador de acessórios Tubulaçoes", "Identificador de acessórios Tubulações"),
            F("Etiqueta de tubo", "JA - Etiqueta - Tubo", "Identificador de Tubos"),
            F("Etiqueta de conexão de tubo", "JA - Etiqueta - Conexão de Tubo", "TAG DESCRIÇÃO PARA DETALHE - CONEXÕES DE TUBO"),
            F("Etiqueta de circuito", "JA - Etiqueta - Circuito de Tomada", "Tag N° do circuito em Tomada", "Tag Nº do circuito em Tomada"),
            T("Etiqueta de circuito", "JA - Etiqueta - Circuito de Tomada", "Nº do Circuito", "Tag N° do circuito em Tomada", "Tag Nº do circuito em Tomada"),
            F("Etiqueta de gás", "JA - Etiqueta - Gás", "Identificador de Gás"),

            // spot elevations
            S("Cota de nível", "JA - Nível - Piso Acabado (P.A./P.O.)", "JA - Nível (Relativo)", "Nível (Relativo)"),
            S("Cota de nível", "JA - Nível - Soleira", "Soleira/Filete - Relativo a Nível"),
            // dimensions
            S("Cota", "JA - Cota Linear - Executivo 1,5 mm", "EXECUTIVO - 1,5mm_RomansD"),
            S("Cota", "JA - Cota Linear - Detalhe 1,5 mm", "EXECUTIVO - Detalhes - 1,5mm_Simplex 2"),
            S("Cota", "JA - Cota Angular - 1,5 mm Vermelha", "1,5mm_vermelho"),
            // texts
            S("Texto", "JA - Texto 1,0 mm - RomansD", "RomansD 1.0 mm preto"),
            S("Texto", "JA - Texto 1,0 mm - Arial", "Arial_1.0 mm preto"),
            S("Texto", "JA - Texto 1,2 mm - Arial", "Arial_1.20mm"),
            S("Texto", "JA - Texto 1,5 mm - Arial", "Arial_1.5 mm preto"),
            S("Texto", "JA - Texto 1,5 mm - Arial com Borda", "Arial_1.5 mm preto C/ Borda"),
            S("Texto", "JA - Texto 1,8 mm - Arial", "Arial_1.8 mm preto"),
            S("Texto", "JA - Título 2,5 mm - Arial Narrow Negrito", "M. ArialN. 2.5mm. Preto. Negrito"),
        };

        /// <summary>Every name (new first, then old) a rule of this kind answers to.</summary>
        public static IEnumerable<string> Nomes(Tipo tipo, string qualquer)
        {
            string n = Norm(qualquer);
            foreach (Regra r in Regras.Where(x => x.Tipo == tipo))
                if (Norm(r.Novo) == n || r.Antigos.Any(a => Norm(a) == n))
                    return new[] { r.Novo }.Concat(r.Antigos);
            return new[] { qualquer };
        }

        /// <summary>The new and old names of a list of wanted names (new first).</summary>
        public static string[] ComNovos(Tipo tipo, IEnumerable<string> nomes) => nomes.SelectMany(n => Nomes(tipo, n)).Distinct().ToArray();

        /// <summary>No accents, lower case, no "LB" prefix/suffix, single spaces (same as PadraoPlanta.Norm).</summary>
        public static string Norm(string s)
        {
            string f = TextUtil.Fold(s ?? "").Trim();
            f = System.Text.RegularExpressions.Regex.Replace(f, @"^lb\s*[\.\-_:]*\s*", "");
            f = System.Text.RegularExpressions.Regex.Replace(f, @"[\s\.\-_]*lb$", "");
            f = System.Text.RegularExpressions.Regex.Replace(f, @"(?<=[\s_\-\.])lb(?=[\s_\-\.])", " ");
            f = System.Text.RegularExpressions.Regex.Replace(f, @"\s+", " ");
            return f.Trim(' ', '.', '-', '_');
        }
    }
}
