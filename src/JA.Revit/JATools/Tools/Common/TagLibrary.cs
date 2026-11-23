using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using RevitDwgUnifier.Infrastructure;

namespace RevitDwgUnifier.Tools.Common
{
    /// <summary>
    /// The office's tag families ("Identificadores", installed with JA Tools in
    /// ...\RevitDwgUnifier\Identificadores). Every JA Tools command that tags elements uses them:
    /// when a category is tagged and the project has no JA identifier for it yet, the right .rfa is
    /// loaded on the spot (inside the command's transaction) and its type is used.
    /// A family saved in a newer Revit than the one running cannot be loaded: the tag then falls
    /// back to the category's default tag and the command reports it once.
    /// </summary>
    public static class TagLibrary
    {
        /// <summary>element category -> tag category -> family files, preferred first (names as BuiltInCategory
        /// strings, parsed at run time: some categories only exist in newer Revit versions).</summary>
        private static readonly Tuple<string, string, string[]>[] Map =
        {
            Tuple.Create("OST_StructuralColumns", "OST_StructuralColumnTags", new[] { "Identificador de Pilares Estruturais" }),
            Tuple.Create("OST_Columns", "OST_ColumnTags", new[] { "Identificador de Colunas IFC", "Identificador de Colunas IFC 2" }),
            Tuple.Create("OST_ElectricalEquipment", "OST_ElectricalEquipmentTags", new[] { "Identificador de Equipamento Elétrico" }),
            Tuple.Create("OST_ElectricalFixtures", "OST_ElectricalFixtureTags", new[] { "Identificador de Dispositivos Elétricos", "Identificador de Dispositivo Elétrico" }),
            Tuple.Create("OST_LightingDevices", "OST_LightingDeviceTags", new[] { "Identificador de Dispositivos de Iluminação" }),
            Tuple.Create("OST_LightingFixtures", "OST_LightingFixtureTags", new[] { "Identificador de Luminárias" }),
            Tuple.Create("OST_CommunicationDevices", "OST_CommunicationDeviceTags", new[] { "Identificador de Comunicação" }),
            Tuple.Create("OST_DataDevices", "OST_DataDeviceTags", new[] { "Identificador de Dados" }),
            Tuple.Create("OST_PlumbingEquipment", "OST_PlumbingEquipmentTags", new[] { "JA - Identificador de Equipamentos Hidráulicos", "Identificador de Equipamento Hidráulico - Tag Circular" }),
            Tuple.Create("OST_PipeFitting", "OST_PipeFittingTags", new[] { "Identificador de Ralos", "Identificador de Tubos" }),
            Tuple.Create("OST_PipeAccessory", "OST_PipeAccessoryTags", new[] { "Identificador de acessórios Tubulaçoes" }),
            Tuple.Create("OST_Parking", "OST_ParkingTags", new[] { "Identificador de Estacionamento" })
        };

        /// <summary>Folder with the identifier families (first one that exists).</summary>
        public static string Folder
        {
            get
            {
                var candidates = new List<string>
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RevitDwgUnifier", "Identificadores"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RevitDwgUnifier", "Identificadores")
                };
                try { if (!string.IsNullOrEmpty(AddinLocation.Folder)) candidates.Add(Path.Combine(AddinLocation.Folder, "Identificadores")); } catch { }
                return candidates.FirstOrDefault(Directory.Exists);
            }
        }

        private static BuiltInCategory? Parse(string name) =>
            Enum.TryParse(name, out BuiltInCategory bic) ? bic : (BuiltInCategory?)null;

        /// <summary>Tag category of an element category, if JA Tools knows one.</summary>
        public static BuiltInCategory? TagCategoryOf(Category elementCategory)
        {
            if (elementCategory == null) return null;
            foreach (var m in Map)
            {
                BuiltInCategory? e = Parse(m.Item1);
                if (e.HasValue && elementCategory.Id.Value == (long)e.Value) return Parse(m.Item2);
            }
            return null;
        }

        // documents / categories already tried in this session (a failed load is not retried)
        private static readonly HashSet<string> Tried = new HashSet<string>();

        /// <summary>
        /// The JA identifier type for an element category: one already in the project, or loaded now
        /// from the identifier folder. Must run inside an open transaction. null = none available.
        /// </summary>
        public static ElementId Ensure(Document doc, Category elementCategory, List<string> problems)
        {
            if (doc == null || elementCategory == null) return null;
            var entry = Map.FirstOrDefault(m => Parse(m.Item1) is BuiltInCategory b && elementCategory.Id.Value == (long)b);
            if (entry == null) return null;
            BuiltInCategory? tagCat = Parse(entry.Item2);
            if (!tagCat.HasValue) return null;

            FamilySymbol existing = FindSymbol(doc, tagCat.Value, entry.Item3);
            if (existing != null) return existing.Id;

            string key = doc.GetHashCode() + "|" + entry.Item1;
            if (Tried.Contains(key)) return null;
            Tried.Add(key);

            string folder = Folder;
            if (folder == null) return null;
            foreach (string name in entry.Item3)
            {
                string path = Path.Combine(folder, name + ".rfa");
                if (!File.Exists(path)) continue;
                try
                {
                    doc.LoadFamily(path, new KeepValues(), out Family fam);
                    FamilySymbol s = FindSymbol(doc, tagCat.Value, entry.Item3);
                    if (s != null)
                    {
                        Logger.Info($"[JA Tools] Identificador carregado: {name}");
                        return s.Id;
                    }
                }
                catch (Exception ex)
                {
                    problems?.Add($"Identificador \"{name}\" não carregado ({ex.Message}). Famílias salvas numa versão mais nova do Revit não abrem nesta; usada a etiqueta padrão.");
                    Logger.Warn($"[JA Tools] Identificador {name}: {ex.Message}");
                }
            }
            return null;
        }

        private static FamilySymbol FindSymbol(Document doc, BuiltInCategory tagCat, string[] names)
        {
            var symbols = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(tagCat).Cast<FamilySymbol>().ToList();
            // also the names given by Padronizar Nomes ("JA - Etiqueta - …")
            foreach (string n in PadraoNomes.ComNovos(PadraoNomes.Tipo.Familia, names))
            {
                FamilySymbol s = symbols.FirstOrDefault(x => string.Equals(x.FamilyName, n, StringComparison.CurrentCultureIgnoreCase));
                if (s != null) return s;
            }
            return null;
        }

        /// <summary>Loading a family that is already in the project keeps the parameter values there.</summary>
        internal sealed class KeepValues : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                overwriteParameterValues = false;
                return true;
            }

            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            {
                source = FamilySource.Family;
                overwriteParameterValues = false;
                return true;
            }
        }
    }
}
