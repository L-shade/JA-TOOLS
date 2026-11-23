using System;
using System.IO;
using System.Runtime.Serialization;
using JAEsquadrias.Infrastructure;

namespace JAEsquadrias.Core
{
    /// <summary>
    /// User settings of JA Famílias (per Windows user):
    ///   %AppData%\JAEsquadrias\settings.json
    /// Generated families and the model library live in Documentos\JA Esquadrias by default.
    /// </summary>
    [DataContract]
    public sealed class Settings
    {
        [DataMember] public string PastaFamilias { get; set; }
        [DataMember] public string PastaBiblioteca { get; set; }
        [DataMember] public string PastaRelatorios { get; set; }
        /// <summary>Optional: a specific window family template (.rft). Empty = found automatically.</summary>
        [DataMember] public string TemplateJanela { get; set; }
        [DataMember] public string TemplatePorta { get; set; }
        /// <summary>Optional Generic Model templates (bancadas / nichos / chapins).</summary>
        [DataMember] public string TemplateGenerico { get; set; }
        [DataMember] public string TemplateGenericoParede { get; set; }
        [DataMember] public string TemplateGenericoLinha { get; set; }
        /// <summary>Optional mullion profile template ("Metric Profile-Mullion.rft"). Empty = found automatically.</summary>
        [DataMember] public string TemplatePerfilMontante { get; set; }
        /// <summary>Optional curtain wall panel template ("Metric Curtain Wall Panel.rft"). Empty = found automatically.</summary>
        [DataMember] public string TemplatePainelCortina { get; set; }
        [DataMember] public string PrefixoFamilia { get; set; }
        [DataMember] public string Fabricante { get; set; }
        [DataMember] public string PerfilPadrao { get; set; }
        [DataMember] public string CorPadrao { get; set; }
        [DataMember] public string VidroPadrao { get; set; }
        [DataMember] public double ParedePadraoMm { get; set; }
        [DataMember] public PosicaoParede PosicaoPadrao { get; set; }

        public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JAEsquadrias");
        private static string FilePath => Path.Combine(Folder, "settings.json");
        private static string Documents => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "JA Esquadrias");

        private static Settings _current;

        /// <summary>Reads the file again (after Padrões do Escritório imported the office settings).</summary>
        public static void Recarregar() => _current = null;

        public static Settings Current
        {
            get
            {
                if (_current != null) return _current;
                try { if (File.Exists(FilePath)) _current = Json.Read<Settings>(FilePath); }
                catch (Exception ex) { Logger.Warn("[JA Famílias] Configurações ilegíveis, usando padrões: " + ex.Message); }
                _current = (_current ?? new Settings()).Fill();
                return _current;
            }
        }

        public Settings Fill()
        {
            if (string.IsNullOrWhiteSpace(PastaFamilias)) PastaFamilias = Path.Combine(Documents, "Familias");
            if (string.IsNullOrWhiteSpace(PastaBiblioteca)) PastaBiblioteca = Path.Combine(Documents, "Biblioteca");
            if (string.IsNullOrWhiteSpace(PastaRelatorios)) PastaRelatorios = Path.Combine(Documents, "Relatorios");
            if (string.IsNullOrWhiteSpace(PrefixoFamilia)) PrefixoFamilia = "JA Esquadria";
            if (string.IsNullOrWhiteSpace(Fabricante)) Fabricante = "JA Famílias";
            if (string.IsNullOrWhiteSpace(PerfilPadrao)) PerfilPadrao = "AL30";
            if (string.IsNullOrWhiteSpace(CorPadrao)) CorPadrao = "branco";
            if (string.IsNullOrWhiteSpace(VidroPadrao)) VidroPadrao = "INC6";
            if (ParedePadraoMm <= 0) ParedePadraoMm = 150;
            if (TemplateJanela == null) TemplateJanela = "";
            if (TemplatePorta == null) TemplatePorta = "";
            if (TemplateGenerico == null) TemplateGenerico = "";
            if (TemplateGenericoParede == null) TemplateGenericoParede = "";
            if (TemplateGenericoLinha == null) TemplateGenericoLinha = "";
            if (TemplatePainelCortina == null) TemplatePainelCortina = "";
            return this;
        }

        public void Save()
        {
            try { Json.Write(FilePath, this); _current = this; }
            catch (Exception ex) { Logger.Warn("[JA Famílias] Configurações não salvas: " + ex.Message); }
        }
    }
}
