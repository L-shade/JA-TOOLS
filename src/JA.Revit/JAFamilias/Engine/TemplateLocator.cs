using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using JAEsquadrias.Core;

namespace JAEsquadrias.Engine
{
    /// <summary>
    /// Finds the Revit family template (.rft) for windows or doors in any language install
    /// ("Metric Window.rft", "Janela métrica.rft", "Ventana métrica.rft"...). A template chosen in
    /// Configurações always wins.
    /// </summary>
    public static class TemplateLocator
    {
        private static readonly string[] WindowWords = { "window", "janela", "ventana", "fenetre", "fenster", "finestra" };
        private static readonly string[] DoorWords = { "door", "porta", "puerta", "porte" };
        private static readonly string[] Excluded = { "curtain", "cortina", "skylight", "claraboia", "lucarne", "rideau", "vorhang", "facciata", "fachada", "garage", "overhead", "basculante" };

        private static readonly string[] Metric = { "metric", "metrica", "metrique", "metrisch" };
        private static readonly string[] MetricMasc = { "metric", "metrica", "metrico", "metrique", "metrisch" };

        public static string Find(Application app, TipoEsquadria tipo, Settings s, out string searched)
        {
            string manual = tipo == TipoEsquadria.Porta ? s?.TemplatePorta : s?.TemplateJanela;
            searched = "";
            if (!string.IsNullOrWhiteSpace(manual) && File.Exists(manual)) return manual;
            var roots = Roots(app, out string own);
            searched = string.Join("; ", roots);
            string[] words = tipo == TipoEsquadria.Porta ? DoorWords : WindowWords;
            return Best(roots, own, Metric, name =>
            {
                if (!words.Any(name.Contains) || Excluded.Any(name.Contains)) return null;
                int score = 0;
                if (name.Contains("wall") || name.Contains("parede") || name.Contains("muro")) score -= 3;
                if (name.Contains("2d") || name.Contains("symbol") || name.Contains("simbolo") || name.Contains("tag") || name.Contains("etiqueta")) score -= 30;
                return score;
            }, warn: true);
        }

        private static readonly string[] CurtainWords = { "curtain", "cortina", "rideau", "vorhang", "facciata continua" };

        /// <summary>Door/window template for curtain walls ("Metric Door - Curtain Wall.rft", "Porta métrica - Parede cortina.rft"...).</summary>
        public static string FindCurtain(Application app, TipoEsquadria tipo, Settings s, out string searched)
        {
            var roots = Roots(app, out string own);
            searched = string.Join("; ", roots);
            string[] words = tipo == TipoEsquadria.Porta ? DoorWords : WindowWords;
            return Best(roots, own, Metric, name => words.Any(name.Contains) && CurtainWords.Any(name.Contains) ? 0 : (int?)null);
        }

        private static readonly string[] PanelWords = { "panel", "painel", "panneau", "paneel", "pannello", "panel ", "paneles" };
        private static readonly string[] PanelExcluded = { "pattern", "padrao", "patron", "motif", "muster", "adaptive", "adaptativ", "adaptiv", "baseado em padrao" };

        /// <summary>Curtain Wall Panel template ("Metric Curtain Wall Panel.rft", "Painel de parede cortina métrico.rft"...): painéis que não são porta/janela.</summary>
        public static string FindCurtainPanel(Application app, Settings s, out string searched)
        {
            searched = "";
            if (!string.IsNullOrWhiteSpace(s?.TemplatePainelCortina) && File.Exists(s.TemplatePainelCortina)) return s.TemplatePainelCortina;
            var roots = Roots(app, out string own);
            searched = string.Join("; ", roots);
            return Best(roots, own, MetricMasc, name =>
                !PanelWords.Any(name.Contains) || !CurtainWords.Any(name.Contains) || PanelExcluded.Any(name.Contains) ||
                WindowWords.Any(name.Contains) || DoorWords.Any(name.Contains) ? (int?)null : 0);
        }

        public enum Hospedeiro { Livre, Parede, Linha }

        private static readonly string[] GenericWords = { "generic", "generico", "generique", "generisch" };
        private static readonly string[] WallWords = { "wall", "parede", "muro", "pared", "mur " };
        private static readonly string[] LineWords = { "line", "linha", "linea", "ligne" };
        private static readonly string[] GenericExcluded = { "face", "adaptive", "adaptativ", "adaptiv", "pattern", "padrao", "patron", "floor", "piso", "ceiling", "forro", "techo",
                                                             "roof", "telhado", "cobertura", "two level", "dois niveis", "baluster", "guarda", "annotation", "anotacao", "tag", "etiqueta",
                                                             "profile", "perfil", "detail", "detalhe", "2d" };

        /// <summary>Generic Model template: free (non-hosted), wall-based or line-based.</summary>
        public static string FindGeneric(Application app, Hospedeiro h, Settings s, out string searched)
        {
            string manual = h == Hospedeiro.Parede ? s?.TemplateGenericoParede : h == Hospedeiro.Linha ? s?.TemplateGenericoLinha : s?.TemplateGenerico;
            searched = "";
            if (!string.IsNullOrWhiteSpace(manual) && File.Exists(manual)) return manual;
            var roots = Roots(app, out string own);
            searched = string.Join("; ", roots);
            return Best(roots, own, Metric, name =>
            {
                if (!GenericWords.Any(name.Contains) || GenericExcluded.Any(name.Contains)) return null;
                bool wall = WallWords.Any(name.Contains), line = LineWords.Any(name.Contains);
                if (h == Hospedeiro.Parede ? !wall : h == Hospedeiro.Linha ? !line : wall || line) return null;
                return 0;
            }, padded: true);
        }

        /// <summary>Baluster panel template ("Metric Baluster Panel.rft", "Balaústre métrico - Painel.rft"…); else any baluster template.</summary>
        public static string FindBalusterPanel(Application app, out string searched)
        {
            var roots = Roots(app, out string own);
            searched = string.Join("; ", roots);
            return Best(roots, own, Metric, name =>
            {
                if (!(name.Contains("baluster") || name.Contains("balaustre") || name.Contains("balaustra"))) return null;
                int score = 0;
                if (name.Contains("panel") || name.Contains("painel") || name.Contains("panneau") || name.Contains("pannello")) score += 40;
                if (name.Contains("post") || name.Contains("poste") || name.Contains("montante")) score -= 30;
                return score;
            }, padded: true);
        }

        private static readonly string[] DetailWords = { "detail", "detalhe", "detalle" };
        private static readonly string[] DetailExcluded = { "line", "linha", "linea", "baseado", "based", "tag", "etiqueta", "profile", "perfil", "annotation", "anotacao", "title", "carimbo" };

        /// <summary>Detail Item template ("Metric Detail Item.rft", "Item de detalhe métrico.rft"...): plan groups nested in esquadrias.</summary>
        public static string FindDetail(Application app, Settings s, out string searched)
        {
            var roots = Roots(app, out string own);
            searched = string.Join("; ", roots);
            return Best(roots, own, Metric, name =>
                !DetailWords.Any(name.Contains) || DetailExcluded.Any(name.Contains) ? (int?)null
                : name.Contains("item") || name.Contains("componente") ? 15 : 0, padded: true);
        }

        private static readonly string[] ProfileWords = { "profile", "perfil", "profil", "profilo" };
        private static readonly string[] MullionWords = { "mullion", "montante", "montant", "pfosten", "parteluz" };

        /// <summary>Mullion profile template ("Metric Profile-Mullion.rft", "Perfil métrico - Montante.rft"...).</summary>
        public static string FindMullionProfile(Application app, Settings s, out string searched)
        {
            searched = "";
            if (!string.IsNullOrWhiteSpace(s?.TemplatePerfilMontante) && File.Exists(s.TemplatePerfilMontante)) return s.TemplatePerfilMontante;
            var roots = Roots(app, out string own);
            string version = "";
            try { version = app.VersionNumber; } catch { }
            string lib = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Autodesk", "RVT " + version, "Libraries");
            if (Directory.Exists(lib)) roots.Add(lib);
            searched = string.Join("; ", roots);
            return Best(roots, own, Metric, name => ProfileWords.Any(name.Contains) && MullionWords.Any(name.Contains) ? 0 : (int?)null);
        }

        /// <summary>
        /// The best .rft under the roots. rank(name) gives the bonus of a file (name: lower case, no accents,
        /// with a trailing space if padded) or null to skip it; then + 20 metric, + 10 in the Revit's own
        /// template folder, shorter names first.
        /// </summary>
        private static string Best(List<string> roots, string own, string[] metric, Func<string, int?> rank, bool padded = false, bool warn = false)
        {
            string best = null;
            int bestScore = int.MinValue;
            foreach (string root in roots)
            {
                List<string> files;
                try { files = Directory.EnumerateFiles(root, "*.rft", SearchOption.AllDirectories).ToList(); }
                catch (Exception ex)
                {
                    if (warn) Logger.Warn("[JA Famílias] Pasta de modelos ilegível " + root + ": " + ex.Message);
                    continue;
                }
                foreach (string f in files)
                {
                    string name = Plain(Path.GetFileNameWithoutExtension(f)) + (padded ? " " : "");
                    int? r = rank(name);
                    if (r == null) continue;
                    int score = r.Value;
                    if (metric.Any(name.Contains)) score += 20;
                    if (!string.IsNullOrEmpty(own) && f.StartsWith(own, StringComparison.OrdinalIgnoreCase)) score += 10;
                    score -= name.Length / 4;
                    if (score > bestScore) { bestScore = score; best = f; }
                }
            }
            return best;
        }

        private static List<string> Roots(Application app, out string own)
        {
            var roots = new List<string>();
            own = null;
            try { own = app.FamilyTemplatePath; } catch { }
            if (!string.IsNullOrWhiteSpace(own)) roots.Add(own);
            string version = "";
            try { version = app.VersionNumber; } catch { }
            string pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            roots.Add(Path.Combine(pd, "Autodesk", "RVT " + version, "Family Templates"));
            if (!string.IsNullOrWhiteSpace(own))
            {
                try { var parent = Directory.GetParent(own.TrimEnd('\\', '/')); if (parent != null) roots.Add(parent.FullName); } catch { }
            }
            return roots.Where(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Lower case, accents removed.</summary>
        private static string Plain(string s) => TextUtil.Fold(s);
    }
}
