using System;
using System.Collections.Generic;
using System.Linq;
using RevitDwgUnifier.Contracts;

namespace RevitDwgUnifier.AcadMerge.Cleanup
{
    /// <summary>
    /// Building-services families (fixtures, pipes, luminaires, devices, ducts...) and furniture
    /// (mobiliário, marcenaria, equipamentos) stay blocks: no cleanup step explodes them. A block
    /// is one of them when its insert is on a layer of those Revit categories (export setup), on
    /// a "JA - INST_..." layer, or when its name / layer carries a discipline ("Coluna AF",
    /// "Ralo", "Luminária"...) or a piece of furniture ("Mesa", "Cadeira", "Armário"...).
    /// </summary>
    internal sealed class InstallationGuard
    {
        private readonly List<string> _layers;
        private readonly List<InstallationLayer> _table;
        private readonly Dictionary<string, bool> _layerCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> _nameCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Words of furniture in a block / layer name (same rule as the disciplines).</summary>
        private static readonly List<InstallationLayer> Furniture = new List<InstallationLayer>
        {
            new InstallationLayer
            {
                Name = "MOBILIARIO",
                Keywords = new List<string> { "MOBILI", "MOVEL", "MOVEIS", "FURNITURE", "CASEWORK", "MARCENAR", "CADEIRA", "POLTRONA",
                                              "SOFA", "MESA", "ARMARIO", "ESTANTE", "BANQUETA", "BALCAO", "CRIADO MUDO", "CABECEIRA",
                                              "GUARDA ROUPA", "APARADOR", "PUFF", "COLCHAO", "BELICHE", "ESCRIVANINHA", "GAVETEIRO" }
            }
        };

        public InstallationGuard(MergeOptions o)
        {
            _layers = (o?.InstallationCategoryLayers ?? new List<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _table = o?.InstallationLayerTable != null && o.InstallationLayerTable.Count > 0 ? o.InstallationLayerTable : InstallationRules.Defaults();
        }

        /// <summary>Layer of a building-services category, or a layer named after a discipline.</summary>
        public bool Layer(string layer)
        {
            if (string.IsNullOrEmpty(layer) || layer == "0") return false;
            if (_layerCache.TryGetValue(layer, out bool known)) return known;
            bool r = _layers.Any(l => InstallationMover.SameLayer(layer, l)) ||
                     _table.Any(t => string.Equals(t?.Name?.Trim(), layer, StringComparison.OrdinalIgnoreCase)) ||
                     InstallationRules.Classify(_table, layer) != null ||
                     InstallationRules.Classify(Furniture, layer) != null;
            _layerCache[layer] = r;
            return r;
        }

        /// <summary>Block name carrying a discipline (anonymous "*U..." names say nothing).</summary>
        public bool Name(string blockName)
        {
            if (string.IsNullOrEmpty(blockName) || blockName.StartsWith("*", StringComparison.Ordinal)) return false;
            if (_nameCache.TryGetValue(blockName, out bool known)) return known;
            bool r = InstallationRules.Classify(_table, blockName) != null || InstallationRules.Classify(Furniture, blockName) != null;
            _nameCache[blockName] = r;
            return r;
        }

        public bool Block(string insertLayer, string blockName) => Layer(insertLayer) || Name(blockName);
    }
}
