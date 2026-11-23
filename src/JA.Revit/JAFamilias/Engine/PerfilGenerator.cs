using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using JAEsquadrias.Core.Perfis;
using RevitApp = Autodesk.Revit.ApplicationServices.Application;
using Settings = JAEsquadrias.Core.Settings;

namespace JAEsquadrias.Engine
{
    public sealed class PerfilResult
    {
        public Family Family;
        public FamilySymbol Perfil;
        public MullionType Tipo;
        public string RfaPath;
        public bool Novo;
        public List<string> Warnings = new List<string>();
    }

    /// <summary>
    /// Curtain-wall mullions (montantes) from a <see cref="PerfilConfig"/>:
    ///  * profile shapes → a Mullion Profile family (template "Profile-Mullion": detail lines in
    ///    the plan view, exterior BELOW the centre plane, so Y is mirrored) and a mullion type
    ///    duplicated from the project's rectangular mullion with "Profile" = that family;
    ///  * corners (meia esquadria) → a V or L corner mullion type (system family: legs and
    ///    thickness; Revit does not accept profiles on corner mullions).
    /// The configuration is kept in the profile family (JA_Config) to edit it later.
    /// </summary>
    public static class PerfilGenerator
    {
        private const double Mm = 1.0 / 304.8;
        public const string Marca = "JA Perfil";

        public static PerfilResult Generate(RevitApp app, Document project, PerfilConfig cfg, Settings settings, MullionType editing = null)
        {
            cfg = cfg.Copiar();
            var res = new PerfilResult();
            if (cfg.EhCanto)
            {
                using (var t = new Transaction(project, "JA Famílias – montante de canto"))
                {
                    t.Start();
                    res.Tipo = CornerType(project, cfg, editing, res);
                    t.Commit();
                }
                return res;
            }

            string template = TemplateLocator.FindMullionProfile(app, settings, out string searched);
            if (template == null)
                throw new InvalidOperationException("Não encontrei o modelo de família de perfil de montante (“Metric Profile-Mullion.rft” ou “Perfil métrico - Montante.rft”).\n" +
                    "Procurado em: " + (string.IsNullOrEmpty(searched) ? "(nenhuma pasta de modelos)" : searched));

            PerfilGeom geom = cfg.Build();
            res.Warnings.AddRange(geom.Avisos);
            string familyName = FamilyGenerator.SafeName(editing != null ? ProfileFamilyName(project, editing) ?? cfg.NomeFamilia : cfg.NomeFamilia);
            string folder = Path.Combine(settings.PastaFamilias, "Perfis");
            Directory.CreateDirectory(folder);
            res.RfaPath = Path.Combine(folder, familyName + ".rfa");

            Logger.Info($"[JA Famílias] Gerando perfil {familyName} a partir de {template}");
            Document fam = app.NewFamilyDocument(template);
            if (fam == null) throw new InvalidOperationException("O Revit não criou o documento de família a partir de " + template);
            try
            {
                SharedParams.WithDefinitions(app, defs =>
                {
                    using (var t = new Transaction(fam, "JA Famílias"))
                    {
                        t.Start();
                        var opts = t.GetFailureHandlingOptions();
                        opts.SetFailuresPreprocessor(new WarningSwallower());
                        t.SetFailureHandlingOptions(opts);
                        DrawProfile(fam, cfg, geom, res);
                        WriteParams(fam, cfg, defs, res);
                        t.Commit();
                    }
                });
                fam.SaveAs(res.RfaPath, new SaveAsOptions { OverwriteExistingFile = true, MaximumBackups = 1 });
                res.Family = fam.LoadFamily(project, new OverwriteLoadOptions());
            }
            finally
            {
                try { fam.Close(false); } catch { }
            }
            if (res.Family == null)
                res.Family = new FilteredElementCollector(project).OfClass(typeof(Family)).Cast<Family>().FirstOrDefault(f => f.Name == familyName);
            if (res.Family == null) throw new InvalidOperationException("O perfil foi gerado (" + res.RfaPath + ") mas não foi carregado no projeto.");
            res.Perfil = res.Family.GetFamilySymbolIds().Select(id => project.GetElement(id)).OfType<FamilySymbol>()
                .OrderByDescending(s => s.Name == cfg.Codigo).FirstOrDefault();
            if (res.Perfil == null) throw new InvalidOperationException("A família de perfil " + familyName + " não tem tipo.");

            using (var t = new Transaction(project, "JA Famílias – tipo de montante"))
            {
                t.Start();
                res.Tipo = ProfileType(project, cfg, res.Perfil, editing, res);
                t.Commit();
            }
            return res;
        }

        // ================================================================== profile family

        private static void DrawProfile(Document fam, PerfilConfig cfg, PerfilGeom geom, PerfilResult res)
        {
            FamilyManager fm = fam.FamilyManager;
            if (fm.CurrentType == null) fm.NewType(cfg.Codigo);
            else { try { fm.RenameCurrentType(cfg.Codigo); } catch { } }

            // the template's own sample lines (if any) go away
            var old = new FilteredElementCollector(fam).OfClass(typeof(CurveElement)).Cast<CurveElement>()
                .Where(c => c.Category == null || c.Category.Id.Value != (long)BuiltInCategory.OST_ReferenceLines).Select(c => c.Id).ToList();
            try { if (old.Count > 0) fam.Delete(old); } catch { }

            View view = new FilteredElementCollector(fam).OfClass(typeof(ViewPlan)).Cast<View>().FirstOrDefault(v => !v.IsTemplate)
                        ?? new FilteredElementCollector(fam).OfClass(typeof(View)).Cast<View>().FirstOrDefault(v => !v.IsTemplate && v.ViewType != ViewType.ProjectBrowser);
            SketchPlane plane = null;
            double ySign = cfg.InverterLado ? 1 : -1;     // template: exterior below the centre plane
            XYZ X(P p) => new XYZ(p.X * Mm, ySign * p.Y * Mm, 0);

            int made = 0, failed = 0;
            foreach (Loop loop in geom.Loops)
                foreach (Seg s in loop.Segs)
                {
                    Curve c;
                    try
                    {
                        c = s.Arco ? (Curve)Arc.Create(X(s.A), X(s.B), X(s.Mid.Value)) : Line.CreateBound(X(s.A), X(s.B));
                    }
                    catch { failed++; continue; }
                    try
                    {
                        if (view != null) fam.FamilyCreate.NewDetailCurve(view, c);
                        else throw new InvalidOperationException();
                        made++;
                    }
                    catch
                    {
                        try
                        {
                            if (plane == null) plane = SketchPlane.Create(fam, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero));
                            fam.FamilyCreate.NewModelCurve(c, plane);
                            made++;
                        }
                        catch { failed++; }
                    }
                }
            if (made == 0) throw new InvalidOperationException("O Revit não aceitou as linhas do perfil.");
            if (failed > 0) res.Warnings.Add($"{failed} linha(s) do perfil não foram desenhadas (medidas muito pequenas?).");
        }

        private static void WriteParams(Document fam, PerfilConfig cfg, Dictionary<Guid, ExternalDefinition> defs, PerfilResult res)
        {
            FamilyManager fm = fam.FamilyManager;
            void Text(SharedParams.Def d, string value)
            {
                try
                {
                    FamilyParameter p = null;
                    try { p = fm.get_Parameter(d.Guid); } catch { }
                    if (p == null && defs.TryGetValue(d.Guid, out var def)) p = fm.AddParameter(def, GroupTypeId.Data, false);
                    if (p != null) fm.Set(p, value ?? "");
                }
                catch (Exception ex) { Logger.Warn("[JA Famílias] Parâmetro " + d.Name + " no perfil: " + ex.Message); }
            }
            Text(SharedParams.Config, cfg.ToJson());
            Text(SharedParams.Familia, Marca);
            Text(SharedParams.Material, cfg.Material);
            Text(SharedParams.Dimensoes, cfg.Sigla());
            try
            {
                FamilyParameter desc = fm.get_Parameter(BuiltInParameter.ALL_MODEL_DESCRIPTION);
                if (desc != null) fm.Set(desc, cfg.Descricao());
            }
            catch { }
        }

        // ================================================================== mullion types

        private static MullionType ProfileType(Document doc, PerfilConfig cfg, FamilySymbol perfil, MullionType editing, PerfilResult res)
        {
            var all = Types(doc);
            MullionType type = editing != null && ProfileParam(editing) != null ? editing
                             : all.FirstOrDefault(t => t.Name == cfg.NomeTipo && ProfileParam(t) != null);
            if (type == null)
            {
                MullionType source = all.Where(t => ProfileParam(t) != null)
                    .OrderByDescending(t => Plain(t.FamilyName).Contains("retang") || Plain(t.FamilyName).Contains("rectang"))
                    .FirstOrDefault();
                if (source == null)
                    throw new InvalidOperationException("O projeto não tem montante retangular (com o parâmetro Perfil) para servir de base. " +
                                                        "Transfira os tipos de montante de um modelo de projeto padrão e tente de novo.");
                type = (MullionType)source.Duplicate(UniqueName(all, cfg.NomeTipo));
                res.Novo = true;
            }
            else Rename(type, cfg.NomeTipo, all);

            Parameter pp = ProfileParam(type);
            if (pp == null || pp.IsReadOnly || !pp.Set(perfil.Id)) res.Warnings.Add("O perfil não foi aplicado no tipo de montante (parâmetro Perfil).");
            SetMaterial(doc, type, cfg, res);
            return type;
        }

        private static MullionType CornerType(Document doc, PerfilConfig cfg, MullionType editing, PerfilResult res)
        {
            var all = Types(doc);
            bool v = cfg.TipoCanto == TipoCanto.V;
            MullionType type = editing != null && IsCorner(editing, v) ? editing
                             : all.FirstOrDefault(t => t.Name == cfg.NomeTipo && IsCorner(t, v));
            if (type == null)
            {
                MullionType source = all.FirstOrDefault(t => IsCorner(t, v));
                if (source == null)
                    throw new InvalidOperationException($"O projeto não tem montante de canto em {(v ? "V" : "L")} para servir de base. " +
                                                        "Transfira os tipos de montante de um modelo de projeto padrão e tente de novo.");
                type = (MullionType)source.Duplicate(UniqueName(all, cfg.NomeTipo));
                res.Novo = true;
            }
            else Rename(type, cfg.NomeTipo, all);

            bool l1 = SetLength(type, cfg.Perna1Mm, new[] { "LV_MULLION_LEG1" }, new[] { "Leg 1", "Perna 1", "Comprimento da perna 1" });
            bool l2 = SetLength(type, cfg.Perna2Mm, new[] { "LV_MULLION_LEG2" }, new[] { "Leg 2", "Perna 2", "Comprimento da perna 2" });
            bool th = SetLength(type, cfg.EspessuraCantoMm, new[] { "LV_MULLION_THICK", "CUST_MULLION_THICK", "RECT_MULLION_THICK", "MULLION_THICK" }, new[] { "Thickness", "Espessura" });
            if (!l1 || !l2 || !th) res.Warnings.Add("Alguma medida do canto não foi aplicada (pernas / espessura): confira as propriedades do tipo.");
            SetMaterial(doc, type, cfg, res);
            return type;
        }

        public static List<MullionType> Types(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(MullionType)).Cast<MullionType>().OrderBy(t => t.FamilyName).ThenBy(t => t.Name).ToList();

        /// <summary>The "Profile" parameter of rectangular / circular mullion types (null for corners).</summary>
        public static Parameter ProfileParam(Element t)
        {
            if (t == null) return null;
            if (Enum.TryParse("MULLION_PROFILE", out BuiltInParameter bip))
            {
                try { var p = t.get_Parameter(bip); if (p != null && p.StorageType == StorageType.ElementId) return p; } catch { }
            }
            foreach (string n in new[] { "Profile", "Perfil" })
            {
                var p = t.LookupParameter(n);
                if (p != null && p.StorageType == StorageType.ElementId) return p;
            }
            return null;
        }

        public static bool IsCorner(MullionType t, bool v)
        {
            string f = " " + Plain(t.FamilyName).Replace("-", " ").Replace("_", " ") + " ";
            if (!(f.Contains("corner") || f.Contains("canto") || f.Contains("esquina") || f.Contains("angle"))) return false;
            return v ? f.Contains(" v ") : f.Contains(" l ");
        }

        /// <summary>The configuration stored in a JA profile type (null = not a JA type).</summary>
        public static PerfilConfig ConfigOf(Document doc, MullionType t)
        {
            Parameter pp = ProfileParam(t);
            if (pp != null && doc.GetElement(pp.AsElementId()) is FamilySymbol s)
            {
                var c = PerfilConfig.FromJson(SharedParams.GetString(s, SharedParams.Config));
                if (c != null) return c;
            }
            // corner types made here: settings remembered locally
            return PerfilConfig.FromJson(ToolPrefs.Get("perfil.canto." + t.Name));
        }

        private static string ProfileFamilyName(Document doc, MullionType t)
        {
            Parameter pp = ProfileParam(t);
            return pp != null && doc.GetElement(pp.AsElementId()) is FamilySymbol s && SharedParams.GetString(s, SharedParams.Familia) == Marca ? s.FamilyName : null;
        }

        private static bool SetLength(Element e, double mm, string[] bips, string[] names)
        {
            foreach (string b in bips)
                if (Enum.TryParse(b, out BuiltInParameter bip))
                {
                    try { var p = e.get_Parameter(bip); if (p != null && !p.IsReadOnly && p.StorageType == StorageType.Double && p.Set(mm * Mm)) return true; } catch { }
                }
            foreach (string n in names)
            {
                var p = e.LookupParameter(n);
                try { if (p != null && !p.IsReadOnly && p.StorageType == StorageType.Double && p.Set(mm * Mm)) return true; } catch { }
            }
            return false;
        }

        private static void SetMaterial(Document doc, MullionType type, PerfilConfig cfg, PerfilResult res)
        {
            try
            {
                Material m = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>()
                    .FirstOrDefault(x => string.Equals(x.Name, cfg.Material, StringComparison.OrdinalIgnoreCase));
                if (m == null)
                {
                    m = doc.GetElement(Material.Create(doc, cfg.Material)) as Material;
                    if (m != null && TryColor(cfg.CorHex, out Color c)) { m.Color = c; m.Shininess = 64; }
                }
                Parameter p = type.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
                if (m != null && p != null && !p.IsReadOnly) p.Set(m.Id);
            }
            catch (Exception ex) { res.Warnings.Add("Material não aplicado: " + ex.Message); }
        }

        private static bool TryColor(string hex, out Color c)
        {
            c = null;
            hex = (hex ?? "").Trim().TrimStart('#');
            if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v)) return false;
            c = new Color((byte)(v >> 16), (byte)((v >> 8) & 255), (byte)(v & 255));
            return true;
        }

        private static void Rename(MullionType t, string wanted, List<MullionType> all)
        {
            if (t.Name == wanted) return;
            if (all.Any(o => o.Id != t.Id && o.Name == wanted)) return;
            try { t.Name = wanted; } catch { }
        }

        private static string UniqueName(List<MullionType> all, string wanted)
        {
            var names = new HashSet<string>(all.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
            string n = wanted;
            for (int i = 2; names.Contains(n); i++) n = wanted + " (" + i + ")";
            return n;
        }

        private static string Plain(string s) => TextUtil.Fold(s);
    }
}
