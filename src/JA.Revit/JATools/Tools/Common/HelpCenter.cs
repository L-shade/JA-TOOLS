using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Windows.Media;
using RevitDwgUnifier.Infrastructure;
using RevitDwgUnifier.UI;

namespace RevitDwgUnifier.Tools.Common
{
    /// <summary>
    /// "Central de Ajuda": one offline HTML page generated from ToolCatalog (so it never gets out
    /// of date with the buttons), opened in the default browser at the right tool.
    /// Location: %AppData%\RevitDwgUnifier\Ajuda\index.html
    /// </summary>
    public static class HelpCenter
    {
        public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RevitDwgUnifier", "Ajuda");
        public static string IndexPath => Path.Combine(Folder, "index.html");

        /// <summary>Writes the page (cheap; always fresh) and returns its path.</summary>
        public static string Write()
        {
            Directory.CreateDirectory(Folder);
            byte[] mark = RevitDwgUnifier.UI.IconFactory.ResourceBytes("ja_mark.png");
            if (mark != null) { try { File.WriteAllBytes(Path.Combine(Folder, "ja_mark.png"), mark); } catch { } }
            File.WriteAllText(IndexPath, Build(), new UTF8Encoding(false));
            return IndexPath;
        }

        /// <summary>
        /// Didactic ribbon tooltip from the catalog: the short line says what the tool does; the extended
        /// tooltip (hover a little longer) shows the numbered steps and a tip; F1 over the button opens the
        /// Help Center at that tool. Both tabs use it.
        /// </summary>
        public static void Describe(Autodesk.Revit.UI.ButtonData data, ToolInfo info, string fallbackTip = null)
        {
            data.ToolTip = !string.IsNullOrEmpty(info.Description) ? info.Description : fallbackTip;
            var sb = new StringBuilder();
            if (info.Steps.Length > 0)
            {
                sb.Append("Como usar:");
                for (int i = 0; i < info.Steps.Length; i++) sb.Append("\n").Append(i + 1).Append(". ").Append(info.Steps[i]);
            }
            if (info.Tips.Length > 0) sb.Append(sb.Length > 0 ? "\n\n" : "").Append("Dica: ").Append(info.Tips[0]);
            sb.Append(sb.Length > 0 ? "\n\n" : "").Append("F1: abre a Central de Ajuda nesta ferramenta.");
            data.LongDescription = sb.ToString();
            try { data.SetContextualHelp(new Autodesk.Revit.UI.ContextualHelp(Autodesk.Revit.UI.ContextualHelpType.Url, Url(info.Anchor))); }
            catch (Exception ex) { Logger.Warn("Contextual help not set for " + info.Title + ": " + ex.Message); }
        }

        /// <summary>file:// URL of the page at a tool (used by the ribbon F1 help).</summary>
        public static string Url(string anchor = null)
        {
            string url = new Uri(IndexPath).AbsoluteUri;
            return string.IsNullOrEmpty(anchor) ? url : url + "#" + anchor;
        }

        /// <summary>Opens the help in the browser, scrolled to the tool (anchor) if given.</summary>
        public static void Open(string anchor = null)
        {
            try
            {
                Write();
                string target = IndexPath;
                if (!string.IsNullOrEmpty(anchor))
                {
                    // Windows drops "#anchor" when a file URL is opened through the shell: a tiny
                    // redirect page carries it instead.
                    target = Path.Combine(Folder, "abrir.html");
                    File.WriteAllText(target,
                        "<!doctype html><meta charset=\"utf-8\"><meta http-equiv=\"refresh\" content=\"0;url=index.html#" + anchor + "\">" +
                        "<script>location.replace('index.html#" + anchor + "');</script>", new UTF8Encoding(false));
                }
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Logger.Warn("[JA] Central de Ajuda não abriu: " + ex.Message);
            }
        }

        private static string H(string s) => WebUtility.HtmlEncode(s ?? "");
        private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        private static string Build()
        {
            var sb = new StringBuilder();
            sb.Append(@"<!doctype html>
<html lang=""pt-BR""><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>JA para Revit — Central de Ajuda</title>
<style>
:root{--ink:#18212B;--text:#2E3742;--muted:#66707C;--line:#E3E6E1;--surface:#F3F4F1;--card:#fff}
*{box-sizing:border-box}
body{margin:0;font:14px/1.55 'Segoe UI',system-ui,sans-serif;color:var(--text);background:var(--surface)}
header{background:var(--ink);color:#fff;padding:28px 32px 22px}
header h1{margin:0;font-size:24px;font-weight:600}
header .brand{display:flex;align-items:center;gap:14px}
header .brand img{width:48px;height:48px;border-radius:6px;background:#fff}
header p{margin:6px 0 16px;color:#C9D3DF;max-width:760px}
#q{width:100%;max-width:520px;padding:10px 14px;border-radius:8px;border:0;font:inherit;outline:none}
.wrap{display:flex;gap:28px;max-width:1180px;margin:0 auto;padding:24px 32px 60px}
nav{position:sticky;top:16px;align-self:flex-start;width:220px;flex:none}
nav a{display:block;padding:5px 10px;border-radius:6px;color:var(--text);text-decoration:none;font-size:13px}
nav a:hover{background:#E9EDF2}
nav .g{margin:14px 0 4px;font-size:11px;font-weight:700;letter-spacing:.06em;text-transform:uppercase}
main{flex:1;min-width:0}
section.group>h2{font-size:18px;margin:28px 0 2px;display:flex;align-items:center;gap:10px}
section.group>h2 .bar{width:6px;height:22px;border-radius:3px}
section.group>p{margin:0 0 14px;color:var(--muted)}
.card{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:16px 18px;margin-bottom:12px;scroll-margin-top:16px}
.card:target{box-shadow:0 0 0 3px var(--accent)}
.head{display:flex;gap:14px;align-items:flex-start}
.glyph{flex:none;width:40px;height:40px;border-radius:10px;color:#fff;font-weight:700;font-size:13px;display:flex;align-items:center;justify-content:center;letter-spacing:-.02em}
.card h3{margin:0;font-size:16px;color:var(--ink)}
.card .desc{margin:2px 0 0;color:var(--muted)}
ol{margin:12px 0 0;padding:0;list-style:none;counter-reset:s}
ol li{counter-increment:s;position:relative;padding:2px 0 4px 32px}
ol li::before{content:counter(s);position:absolute;left:0;top:2px;width:21px;height:21px;border-radius:50%;background:var(--accent);color:#fff;font-size:11px;font-weight:700;display:flex;align-items:center;justify-content:center}
.tips{margin:10px 0 0;padding:9px 12px;border-radius:8px;background:var(--soft);font-size:13px}
.tips b{color:var(--accent)}
.verb{display:inline-block;margin-top:10px;font-size:12px;color:var(--muted)}
.verb span{background:var(--accent);color:#fff;border-radius:5px;padding:1px 8px;font-weight:600}
.intro{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:18px 20px}
.intro h2{margin:0 0 8px;font-size:18px}
.keys{display:grid;grid-template-columns:repeat(auto-fill,minmax(250px,1fr));gap:8px 18px;margin-top:10px}
kbd{display:inline-block;min-width:22px;padding:1px 6px;border:1px solid var(--line);border-bottom-width:2px;border-radius:5px;background:#fff;font:12px 'Segoe UI';text-align:center;margin-right:6px}
.hidden{display:none}
#none{display:none;color:var(--muted);padding:20px 0}
@media (max-width:820px){nav{display:none}.wrap{padding:16px}}
</style></head><body>
<header><div class=""brand""><img src=""ja_mark.png"" alt=""JA""><h1>JA para Revit — Central de Ajuda</h1></div>
<p>Tudo o que cada botão das abas <b>JA Famílias</b> e <b>JA Tools</b> faz e como usar, passo a passo. Esta página é gerada pelo próprio plugin e funciona sem internet.</p>
<input id=""q"" type=""search"" placeholder=""Buscar ferramenta (ex.: cotas, planta, pdf)…"" autofocus></header>
<div class=""wrap""><nav>");

            string abaNav = null;
            foreach (string g in ToolCatalog.GroupOrder)
            {
                if (ToolCatalog.Aba(g) != abaNav)
                {
                    abaNav = ToolCatalog.Aba(g);
                    sb.Append($"<div class=\"g\" style=\"color:#18212B;font-size:13px;margin-top:14px\">ABA {H(abaNav.ToUpperInvariant())}</div>");
                }
                sb.Append($"<div class=\"g\" style=\"color:{Hex(ToolCatalog.GroupColor(g))}\">{H(g)}</div>");
                foreach (var t in ToolCatalog.Tools.Where(t => t.Group == g))
                    sb.Append($"<a href=\"#{t.Anchor}\">{H(t.Title)}</a>");
            }
            sb.Append("</nav><main>");

            sb.Append(@"<div class=""intro""><h2>Como as janelas funcionam</h2>
<p>Todas as ferramentas seguem o mesmo padrão: o topo explica o que a ferramenta faz e os passos; as opções ficam em cartões;
a barra de dica embaixo explica a opção sob o mouse; o botão colorido executa. Tudo o que você escolhe é lembrado para a próxima vez.</p>
<div class=""keys"">
<div><kbd>Enter</kbd>executa a ação principal</div>
<div><kbd>Esc</kbd>cancela sem alterar nada</div>
<div><kbd>F1</kbd>abre esta ajuda na ferramenta</div>
<div><kbd>↑</kbd><kbd>↓</kbd>ajustam números (Shift = passo maior)</div>
<div><kbd>Shift</kbd>+ clique marca um intervalo na lista</div>
<div><kbd>Ctrl</kbd><kbd>Z</kbd>no Revit desfaz tudo o que a ferramenta fez, de uma vez</div>
</div>
<p style=""margin:12px 0 0;color:var(--muted)"">Dica: selecione ambientes, paredes ou vistas no Revit <i>antes</i> de clicar no botão — eles já chegam marcados.
“Restaurar padrões” volta as opções aos valores de fábrica.</p></div>");

            foreach (string g in ToolCatalog.GroupOrder)
            {
                Color c = ToolCatalog.GroupColor(g);
                string accent = Hex(c);
                string soft = $"rgba({c.R},{c.G},{c.B},.09)";
                sb.Append($"<section class=\"group\" style=\"--accent:{accent};--soft:{soft}\">");
                sb.Append($"<h2><span class=\"bar\" style=\"background:{accent}\"></span>{H(g)}</h2><p>{H(ToolCatalog.GroupIntro(g))}</p>");
                foreach (var t in ToolCatalog.Tools.Where(t => t.Group == g))
                {
                    string search = H((t.Title + " " + t.Description + " " + string.Join(" ", t.Steps) + " " + g).ToLowerInvariant());
                    sb.Append($"<div class=\"card\" id=\"{t.Anchor}\" data-s=\"{search}\">");
                    if (t.Icon != null && IconFactory.Paths.TryGetValue(t.Icon, out string path))
                        sb.Append($"<div class=\"head\"><div class=\"glyph\" style=\"background:{Hex(IconFactory.Soft(c, 0.13))}\"><svg viewBox=\"0 0 24 24\" width=\"24\" height=\"24\" fill=\"none\" stroke=\"{accent}\" stroke-width=\"1.8\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\"><path d=\"{path}\"/></svg></div><div>");
                    else
                        sb.Append($"<div class=\"head\"><div class=\"glyph\" style=\"background:{accent}\">{H(t.Glyph)}</div><div>");
                    sb.Append($"<h3>{H(t.Title)}</h3><p class=\"desc\">{H(t.Description)}</p></div></div>");
                    if (t.Steps.Length > 0)
                    {
                        sb.Append("<ol>");
                        foreach (string s in t.Steps) sb.Append($"<li>{H(s)}</li>");
                        sb.Append("</ol>");
                    }
                    if (t.Tips.Length > 0)
                    {
                        sb.Append("<div class=\"tips\"><b>Dicas</b>");
                        foreach (string tip in t.Tips) sb.Append($"<div>• {H(tip)}</div>");
                        sb.Append("</div>");
                    }
                    sb.Append($"<div class=\"verb\">Botão de ação: <span>{H(t.Verb)}</span></div>");
                    sb.Append("</div>");
                }
                sb.Append("</section>");
            }

            sb.Append(@"<p id=""none"">Nenhuma ferramenta encontrada. Tente outra palavra.</p></main></div>
<script>
var q=document.getElementById('q');
q.addEventListener('input',function(){
  var v=q.value.trim().toLowerCase(),any=false;
  document.querySelectorAll('.card').forEach(function(c){var ok=!v||c.dataset.s.indexOf(v)>=0;c.classList.toggle('hidden',!ok);if(ok)any=true;});
  document.querySelectorAll('section.group').forEach(function(s){s.classList.toggle('hidden',!s.querySelector('.card:not(.hidden)'));});
  document.getElementById('none').style.display=any?'none':'block';
});
</script></body></html>");
            return sb.ToString();
        }
    }
}
