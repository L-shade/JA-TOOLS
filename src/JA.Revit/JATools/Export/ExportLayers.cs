using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using RevitDwgUnifier.Infrastructure;

namespace RevitDwgUnifier.Export
{
    /// <summary>
    /// Layer names the DWG export setup gives to some categories (projection and cut layers of the
    /// category and of all its subcategories). The AutoCAD side uses them to recognise the
    /// columns (pilares) in the exported DWG for the Padrão JA.
    /// </summary>
    public static class ExportLayers
    {
        public static List<string> Columns(Document doc, DWGExportOptions options) =>
            For(doc, options, BuiltInCategory.OST_Columns, BuiltInCategory.OST_StructuralColumns);

        /// <summary>Families that stay blocks in the DWG (never exploded by the cleanup steps):
        /// building services and furniture (mobiliário, marcenaria, equipamentos).</summary>
        public static List<string> KeepAsBlocks(Document doc, DWGExportOptions options)
        {
            var cats = new List<BuiltInCategory>();
            foreach (string n in InstallationCategories.Concat(FurnitureCategories))
                if (Enum.TryParse(n, out BuiltInCategory c)) cats.Add(c);
            return For(doc, options, cats.ToArray());
        }

        private static readonly string[] FurnitureCategories =
        {
            "OST_Furniture", "OST_FurnitureSystems", "OST_Casework", "OST_SpecialityEquipment"
        };

        private static readonly string[] InstallationCategories =
        {
            "OST_PlumbingFixtures", "OST_PlumbingEquipment", "OST_PipeCurves", "OST_PipeFitting", "OST_PipeAccessory", "OST_FlexPipeCurves",
            "OST_MechanicalEquipment", "OST_DuctCurves", "OST_DuctFitting", "OST_DuctAccessory", "OST_DuctTerminal", "OST_FlexDuctCurves",
            "OST_ElectricalFixtures", "OST_ElectricalEquipment", "OST_LightingFixtures", "OST_LightingDevices", "OST_CommunicationDevices",
            "OST_DataDevices", "OST_SecurityDevices", "OST_TelephoneDevices", "OST_NurseCallDevices", "OST_FireAlarmDevices", "OST_Sprinklers",
            "OST_Conduit", "OST_ConduitFitting", "OST_CableTray", "OST_CableTrayFitting", "OST_Wire", "OST_FireProtection",
            "OST_MechanicalControlDevices"
        };

        /// <summary>The export layer table as {category, layer, cut layer} rows, read once per options
        /// object: one export asks for it many times (columns, families kept as blocks, every tag category).</summary>
        private static List<string[]> Table(DWGExportOptions options)
        {
            if (options == null) return null;
            lock (TableSync)
            {
                if (ReferenceEquals(options, _tableOf) && _table != null) return _table;
            }
            ExportLayerTable table = options.GetExportLayerTable();
            if (table == null) return null;
            var rows = new List<string[]>();
            foreach (KeyValuePair<ExportLayerKey, ExportLayerInfo> kv in table)
            {
                if (kv.Key == null || kv.Value == null) continue;
                var row = new List<string> { kv.Key.CategoryName ?? "" };
                foreach (string layer in new[] { kv.Value.LayerName, kv.Value.CutLayerName })
                    if (!string.IsNullOrWhiteSpace(layer)) row.Add(layer.Trim());
                rows.Add(row.ToArray());
            }
            lock (TableSync) { _tableOf = options; _table = rows; }
            return rows;
        }

        private static readonly object TableSync = new object();
        private static DWGExportOptions _tableOf;
        private static List<string[]> _table;

        public static List<string> For(Document doc, DWGExportOptions options, params BuiltInCategory[] categories)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (BuiltInCategory bic in categories)
                {
                    try
                    {
                        Category c = Category.GetCategory(doc, bic);
                        if (c != null) names.Add(c.Name);
                    }
                    catch { }
                }
                List<string[]> table = Table(options);
                if (table == null) return result.ToList();
                // a layer shared with other categories (e.g. "ESTRUTURA" for walls and columns) is not
                // a column layer: it would drag wall hatches along
                var shared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string[] row in table)
                {
                    bool mine = names.Contains(row[0]);
                    for (int i = 1; i < row.Length; i++)
                    {
                        if (mine) result.Add(row[i]); else shared.Add(row[i]);
                    }
                }
                result.ExceptWith(shared);
                result.Remove("0");
            }
            catch (Exception ex) { Logger.Warn("Export layer table not read: " + ex.Message); }
            return result.ToList();
        }
    }
}
