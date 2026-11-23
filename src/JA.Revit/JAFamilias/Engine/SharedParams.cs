using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;

namespace JAEsquadrias.Engine
{
    /// <summary>
    /// The JA Famílias parameters. They are SHARED parameters (fixed GUIDs) so they can be
    /// scheduled and tagged in the project, and so every generated family uses the very same
    /// definitions. JA_Config is invisible: it keeps the full configuration (JSON) inside the
    /// family type, which is what lets "Editar Família" reopen any esquadria.
    /// </summary>
    public static class SharedParams
    {
        public sealed class Def
        {
            public Guid Guid;
            public string Name;
            public ForgeTypeId Spec;
            public bool Visible = true;
            public string Description;
        }

        public static readonly Def Config = new Def { Guid = new Guid("91303496-d9de-47a8-a08d-78cafb760505"), Name = "JA_Config", Spec = SpecTypeId.String.Text, Visible = false, Description = "Configuração do JA Famílias (não editar)." };
        public static readonly Def Linha = new Def { Guid = new Guid("1ddfd4be-7fc4-4e79-b679-f1fd21e2bbce"), Name = "JA_Linha", Spec = SpecTypeId.String.Text, Description = "Linha de perfis da esquadria." };
        public static readonly Def Cor = new Def { Guid = new Guid("bada0cef-9ccf-4c33-a8c4-f716febd4993"), Name = "JA_Cor", Spec = SpecTypeId.String.Text, Description = "Cor/acabamento dos perfis." };
        public static readonly Def Vidro = new Def { Guid = new Guid("414fff62-22c2-457c-a352-a10c8993f152"), Name = "JA_Vidro", Spec = SpecTypeId.String.Text, Description = "Vidro da esquadria." };
        public static readonly Def Ferragens = new Def { Guid = new Guid("64e25cd3-04b5-40cc-9afa-96c48063bc6e"), Name = "JA_Ferragens", Spec = SpecTypeId.String.Text, Description = "Ferragens e acessórios da esquadria (puxador, fechadura, fecho, roldanas)." };
        public static readonly Def Abertura = new Def { Guid = new Guid("6d2252b3-0dc3-432e-aa06-9a7417bac9af"), Name = "JA_Abertura", Spec = SpecTypeId.String.Text, Description = "Tipo de abertura (ex.: correr 2 folhas + basculante)." };
        public static readonly Def Uw = new Def { Guid = new Guid("b19cf036-6dde-4304-9b29-490b6d8b60ac"), Name = "JA_Uw", Spec = SpecTypeId.Number, Description = "Transmitância térmica estimada da esquadria Uw (W/m²K)." };
        public static readonly Def AreaIluminacao = new Def { Guid = new Guid("1f321c5f-9dd9-4ff4-8f75-82789df5bf99"), Name = "JA_AreaIluminacao", Spec = SpecTypeId.Area, Description = "Área envidraçada (iluminação)." };
        public static readonly Def AreaVentilacao = new Def { Guid = new Guid("d03699e3-827e-4129-b4f9-4ed4f6e6a9cf"), Name = "JA_AreaVentilacao", Spec = SpecTypeId.Area, Description = "Área de ventilação estimada (folhas que abrem)." };

        public static readonly Def SoleiraCodigo = new Def { Guid = new Guid("5cca918d-9acc-49d2-9906-b38ffbbc55e8"), Name = "JA_SoleiraCodigo", Spec = SpecTypeId.String.Text, Description = "Código da soleira/filete (portas) ou do peitoril (janelas)." };
        public static readonly Def SoleiraMaterial = new Def { Guid = new Guid("6967518e-b031-45cd-b341-9cb6aacad367"), Name = "JA_SoleiraMaterial", Spec = SpecTypeId.String.Text, Description = "Material da soleira/filete ou do peitoril." };
        public static readonly Def SoleiraDimensoes = new Def { Guid = new Guid("2b148174-0aa0-4d19-a2fd-5a26d58876a6"), Name = "JA_SoleiraDimensoes", Spec = SpecTypeId.String.Text, Description = "Comprimento x largura x espessura da soleira/filete ou do peitoril." };

        public static readonly Def Familia = new Def { Guid = new Guid("209780ee-02ed-4ce2-9278-241e7f920da6"), Name = "JA_Familia", Spec = SpecTypeId.String.Text, Description = "Tipo de família JA (Bancada, Nicho, Chapim; vazio = esquadria)." };
        public static readonly Def Material = new Def { Guid = new Guid("b444874e-59b1-4669-a456-d141fff7e40c"), Name = "JA_Material", Spec = SpecTypeId.String.Text, Description = "Material principal (pedra, revestimento)." };
        public static readonly Def AreaPedra = new Def { Guid = new Guid("89d45bc4-f72a-45f7-97f1-bdcea21a8fc6"), Name = "JA_AreaPedra", Spec = SpecTypeId.Area, Description = "Área de pedra estimada (tampo, saia, frontão...)." };
        public static readonly Def Dimensoes = new Def { Guid = new Guid("a0926488-a3f4-4fa6-800e-1157afd637af"), Name = "JA_Dimensoes", Spec = SpecTypeId.String.Text, Description = "Dimensões da peça." };

        public static readonly Def Comprimento = new Def { Guid = new Guid("5f0c3a7e-8d21-4b6a-9e4f-2c7d1b93a046"), Name = "JA_Comprimento", Spec = SpecTypeId.Length, Description = "Comprimento da peça (chapins: acompanha o muro)." };

        public static readonly Def AreaMolhada = new Def { Guid = new Guid("7c1e4b52-3a9d-4f08-b6e1-9d2a5c7f8e13"), Name = "JA_AreaMolhada", Spec = SpecTypeId.Area, Description = "Bancadas: área do tampo na(s) área(s) molhada(s)." };

        public static readonly Def Iluminacao = new Def { Guid = new Guid("a9515fb5-774f-4c4d-9658-88e24f68db44"), Name = "JA_Iluminacao", Spec = SpecTypeId.String.Text, Description = "Ambientes: resultado da conferência de iluminação e ventilação (JA Famílias)." };
        public static readonly Def[] All = { Config, Linha, Cor, Vidro, Ferragens, Iluminacao, Abertura, Uw, AreaIluminacao, AreaVentilacao, SoleiraCodigo, SoleiraMaterial, SoleiraDimensoes,
                                             Familia, Material, AreaPedra, Dimensoes, Comprimento, AreaMolhada };

        /// <summary>
        /// Creates the definitions in a temporary shared-parameter file, runs <paramref name="use"/>
        /// with them and restores the user's own shared-parameter file afterwards.
        /// </summary>
        public static void WithDefinitions(Application app, Action<Dictionary<Guid, ExternalDefinition>> use)
        {
            string original = null;
            try { original = app.SharedParametersFilename; } catch { }
            string tmp = Path.Combine(Path.GetTempPath(), "JAEsquadrias_" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                File.Create(tmp).Dispose();
                app.SharedParametersFilename = tmp;
                DefinitionFile file = app.OpenSharedParameterFile();
                if (file == null) throw new InvalidOperationException("O Revit não abriu o arquivo de parâmetros compartilhados temporário.");
                DefinitionGroup group = file.Groups.get_Item("JA Esquadrias") ?? file.Groups.Create("JA Esquadrias");
                var defs = new Dictionary<Guid, ExternalDefinition>();
                foreach (var d in All)
                {
                    var opt = new ExternalDefinitionCreationOptions(d.Name, d.Spec)
                    {
                        GUID = d.Guid,
                        Visible = d.Visible,
                        UserModifiable = d.Visible,
                        Description = d.Description ?? ""
                    };
                    if (group.Definitions.get_Item(d.Name) is ExternalDefinition existing) defs[d.Guid] = existing;
                    else defs[d.Guid] = (ExternalDefinition)group.Definitions.Create(opt);
                }
                use(defs);
            }
            finally
            {
                try { app.SharedParametersFilename = original ?? ""; }
                catch (Exception ex) { Logger.Warn("[JA Famílias] Arquivo de parâmetros compartilhados não restaurado: " + ex.Message); }
                try { File.Delete(tmp); } catch { }
            }
        }

        /// <summary>Text value of a JA parameter on an element (type or instance), or null.</summary>
        public static string GetString(Element e, Def d)
        {
            try
            {
                Parameter p = e?.get_Parameter(d.Guid);
                return p != null && p.HasValue ? p.AsString() : null;
            }
            catch { return null; }
        }

        /// <summary>Id of the shared parameter in a project (for schedule fields), or invalid.</summary>
        public static ElementId ProjectId(Document doc, Def d)
        {
            try
            {
                var spe = SharedParameterElement.Lookup(doc, d.Guid);
                return spe?.Id ?? ElementId.InvalidElementId;
            }
            catch { return ElementId.InvalidElementId; }
        }

        public static IEnumerable<Def> Visible => All.Where(d => d.Visible);
    }
}
