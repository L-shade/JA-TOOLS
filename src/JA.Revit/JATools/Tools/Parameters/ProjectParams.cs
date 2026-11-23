using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitDwgUnifier.Tools.Parameters
{
    /// <summary>
    /// Project parameters created by the JA: shared parameters made in a temporary shared-parameter file (the user's own
    /// file is untouched), bound to categories as instance parameters, allowed to vary between group instances.
    /// </summary>
    public static class ProjectParams
    {
        /// <summary>JA_Nivel: the level name of the element (rodapés of the JA; any element through Preencher Parâmetros).</summary>
        public static readonly Guid JaNivel = new Guid("3f6a9c1e-2b74-4d8e-a5c3-7e1d0b9f4a26");
        public const string JaNivelNome = "JA_Nivel";
        public const string JaNivelDescricao = "Nível do elemento (preenchido pelo JA: rodapés e Preencher Parâmetros).";

        /// <summary>The project parameter of that name, if it is bound (instance or type).</summary>
        public static Definition Existente(Document doc, string nome, out ElementBinding binding)
        {
            binding = null;
            DefinitionBindingMapIterator it = doc.ParameterBindings.ForwardIterator();
            while (it.MoveNext())
                if (it.Key is Definition d && string.Equals(d.Name, nome, StringComparison.OrdinalIgnoreCase))
                {
                    binding = it.Current as ElementBinding;
                    return d;
                }
            return null;
        }

        /// <summary>
        /// The instance text parameter bound to the categories (the categories are added when it already exists for
        /// others). Inside a transaction. False with the reason when it cannot.
        /// </summary>
        public static bool GarantirTexto(Document doc, string nome, Guid? guid, IEnumerable<Category> categorias, string descricao, out string erro)
        {
            erro = null;
            var app = doc.Application;
            var alvo = categorias.Where(c => c != null && c.AllowsBoundParameters).GroupBy(c => c.Id.Value).Select(g => g.First()).ToList();
            if (alvo.Count == 0) { erro = "nenhuma das categorias aceita parâmetros de projeto"; return false; }
            try
            {
                Definition existente = Existente(doc, nome, out ElementBinding b);
                if (existente != null)
                {
                    if (!(b is InstanceBinding)) { erro = "“" + nome + "” já existe no projeto como parâmetro de tipo"; return false; }
                    CategorySet cs = app.Create.NewCategorySet();
                    foreach (Category c in b.Categories) cs.Insert(c);
                    bool falta = false;
                    foreach (Category c in alvo) if (!cs.Contains(c)) { cs.Insert(c); falta = true; }
                    if (falta && !doc.ParameterBindings.ReInsert(existente, app.Create.NewInstanceBinding(cs), existente.GetGroupTypeId()))
                    { erro = "o Revit não aceitou acrescentar as categorias a “" + nome + "”"; return false; }
                    return true;
                }

                string original = null;
                try { original = app.SharedParametersFilename; } catch { }
                string tmp = Path.Combine(Path.GetTempPath(), "JA_param_" + Guid.NewGuid().ToString("N") + ".txt");
                try
                {
                    File.Create(tmp).Dispose();
                    app.SharedParametersFilename = tmp;
                    DefinitionFile file = app.OpenSharedParameterFile();
                    if (file == null) { erro = "o Revit não abriu o arquivo temporário de parâmetros compartilhados"; return false; }
                    DefinitionGroup grupo = file.Groups.Create("JA");
                    var opt = new ExternalDefinitionCreationOptions(nome, SpecTypeId.String.Text) { Description = descricao ?? "" };
                    if (guid.HasValue) opt.GUID = guid.Value;
                    var def = (ExternalDefinition)grupo.Definitions.Create(opt);
                    CategorySet cs = app.Create.NewCategorySet();
                    foreach (Category c in alvo) cs.Insert(c);
                    InstanceBinding bind = app.Create.NewInstanceBinding(cs);
                    if (!doc.ParameterBindings.Insert(def, bind, GroupTypeId.IdentityData) &&
                        !doc.ParameterBindings.ReInsert(def, bind, GroupTypeId.IdentityData))
                    { erro = "o Revit não aceitou criar “" + nome + "”"; return false; }
                }
                finally
                {
                    try { app.SharedParametersFilename = original ?? ""; } catch { }
                    try { File.Delete(tmp); } catch { }
                }
                // group members can get their own value
                if (Existente(doc, nome, out _) is InternalDefinition idf)
                    try { idf.SetAllowVaryBetweenGroups(doc, true); } catch { }
                return true;
            }
            catch (Exception ex) { erro = ex.Message; return false; }
        }

        /// <summary>
        /// The level name in JA_Nivel (bound to the element's category if needed) and, when the element has one, in its
        /// schedule level. Inside a transaction.
        /// </summary>
        public static void GravarNivel(Element e, Level nivel)
        {
            if (e == null || nivel == null) return;
            try
            {
                Parameter p = e.get_Parameter(JaNivel);
                if (p == null && e.Category != null && GarantirTexto(e.Document, JaNivelNome, JaNivel, new[] { e.Category }, JaNivelDescricao, out _))
                    p = e.get_Parameter(JaNivel);
                if (p != null && !p.IsReadOnly) p.Set(nivel.Name);
            }
            catch { }
            try
            {
                Parameter sl = e.get_Parameter(BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM);
                if (sl != null && !sl.IsReadOnly && sl.StorageType == StorageType.ElementId) sl.Set(nivel.Id);
            }
            catch { }
        }
    }
}
