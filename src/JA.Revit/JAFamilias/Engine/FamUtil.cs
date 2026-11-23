using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using JAEsquadrias.Core;

namespace JAEsquadrias.Engine
{
    /// <summary>Family-editing helpers shared by the generators (esquadria, peça, painel).</summary>
    internal static class FamUtil
    {
        /// <summary>The first built-in parameter that exists, else the first family parameter with one of the names.</summary>
        public static FamilyParameter Find(FamilyManager fm, BuiltInParameter[] bips, string[] names)
        {
            foreach (var bip in bips)
            {
                try { var p = fm.get_Parameter(bip); if (p != null) return p; } catch { }
            }
            foreach (FamilyParameter p in fm.Parameters)
            {
                string n = p.Definition?.Name ?? "";
                if (names.Any(x => string.Equals(n, x, StringComparison.OrdinalIgnoreCase))) return p;
            }
            return null;
        }

        /// <summary>Material type parameter (created if missing) set to the material, if any.</summary>
        public static FamilyParameter MaterialParam(FamilyManager fm, string name, ElementId material, Action<string> warn)
        {
            try
            {
                FamilyParameter fp = fm.get_Parameter(name) ?? fm.AddParameter(name, GroupTypeId.Materials, SpecTypeId.Reference.Material, false);
                if (material != null && material != ElementId.InvalidElementId) fm.Set(fp, material);
                return fp;
            }
            catch (Exception ex)
            {
                warn($"Parâmetro '{name}' não criado: {ex.Message}");
                return null;
            }
        }

        /// <summary>The shared parameter in the family (added from the definitions if missing).</summary>
        public static FamilyParameter Shared(FamilyManager fm, Dictionary<Guid, ExternalDefinition> defs, SharedParams.Def d)
        {
            FamilyParameter fp = null;
            try { fp = fm.get_Parameter(d.Guid); } catch { }
            if (fp != null) return fp;
            if (!defs.TryGetValue(d.Guid, out ExternalDefinition def)) return null;
            return fm.AddParameter(def, GroupTypeId.Data, false);
        }

        /// <summary>"RRGGBB" → colour (light grey when not valid).</summary>
        public static Color ToColor(string hex)
        {
            hex = (hex ?? "").Trim().TrimStart('#');
            if (hex.Length != 6) return new Color(200, 200, 200);
            try { return new Color(Convert.ToByte(hex.Substring(0, 2), 16), Convert.ToByte(hex.Substring(2, 2), 16), Convert.ToByte(hex.Substring(4, 2), 16)); }
            catch { return new Color(200, 200, 200); }
        }

        /// <summary>Removes points closer than 1 mm to the previous one (Revit rejects tiny edges).</summary>
        public static List<P2> Clean(List<P2> loop)
        {
            var r = new List<P2>();
            foreach (var p in loop)
                if (r.Count == 0 || Math.Abs(p.X - r[r.Count - 1].X) + Math.Abs(p.Y - r[r.Count - 1].Y) > 1.0) r.Add(p);
            while (r.Count > 2 && Math.Abs(r[0].X - r[r.Count - 1].X) + Math.Abs(r[0].Y - r[r.Count - 1].Y) <= 1.0) r.RemoveAt(r.Count - 1);
            return r;
        }
    }
}
