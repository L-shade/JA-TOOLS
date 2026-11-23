using System.Globalization;
using System.Text;

namespace RevitDwgUnifier.Tools.Common
{
    /// <summary>Text helpers shared by every tool (1.17: one copy instead of four).</summary>
    public static class TextUtil
    {
        /// <summary>Lower case without accents: "Elevação" → "elevacao". Searches and name matching.</summary>
        public static string Fold(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder(text.Length);
            foreach (char ch in text.Normalize(NormalizationForm.FormD))
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(ch));
            return sb.ToString();
        }
    }
}
