using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RevitDwgUnifier.UI
{
    /// <summary>
    /// Draws the ribbon icons of both tabs at runtime (line icons on soft tiles, lettered tiles, the
    /// DWG exporter's stacked sheets), so they stay crisp at 16 px and 32 px; the JA logo comes from
    /// the embedded PNGs.
    /// </summary>
    public static class IconFactory
    {
        private static readonly Dictionary<string, byte[]> Resources = new Dictionary<string, byte[]>();

        /// <summary>Bytes of an embedded resource ("ja_mark.png", "ja_logo.png"); null when missing.</summary>
        public static byte[] ResourceBytes(string name)
        {
            lock (Resources)
            {
                if (Resources.TryGetValue(name, out byte[] cached)) return cached;
                byte[] data = null;
                try
                {
                    using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("JATools." + name))
                    {
                        if (s != null)
                        {
                            using (var ms = new MemoryStream()) { s.CopyTo(ms); data = ms.ToArray(); }
                        }
                    }
                }
                catch { data = null; }
                Resources[name] = data;
                return data;
            }
        }

        /// <summary>An embedded picture decoded at the given pixel width (high-quality downscale).</summary>
        public static ImageSource Picture(string name, int width)
        {
            byte[] data = ResourceBytes(name);
            if (data == null) return null;
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.StreamSource = new MemoryStream(data);
                bi.DecodePixelWidth = width;
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
            catch { return null; }
        }

        /// <summary>The JA monogram (square), for the ribbon and window headers.</summary>
        public static ImageSource Mark(int size) =>
            Picture("ja_mark.png", size) ?? Glyph(size, "JA", Color.FromRgb(0x0C, 0x34, 0x42));

        public static ImageSource Create(int size)
        {
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                double s = size;
                var stroke = new Pen(new SolidColorBrush(Color.FromRgb(0x2B, 0x57, 0x9A)), s / 16.0);
                var back = new SolidColorBrush(Color.FromRgb(0xE8, 0xEE, 0xF8));
                var front = Brushes.White;

                // Three stacked "sheets" -> many views into one file.
                dc.DrawRectangle(back, stroke, new Rect(s * 0.30, s * 0.05, s * 0.62, s * 0.55));
                dc.DrawRectangle(back, stroke, new Rect(s * 0.18, s * 0.17, s * 0.62, s * 0.55));
                dc.DrawRectangle(front, stroke, new Rect(s * 0.06, s * 0.29, s * 0.62, s * 0.55));

                if (size >= 24)
                {
                    var text = new FormattedText("DWG", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                        s * 0.24, stroke.Brush, 1.0);
                    dc.DrawText(text, new Point(s * 0.12, s * 0.44));
                }
            }

            var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze();
            return bmp;
        }

        /// <summary>
        /// JA Tools button icon: rounded square in the group colour with a short code
        /// ("CG" = Cortes Gerais, "PB" = Planta Baixa...). Drawn at runtime, no image files.
        /// </summary>
        private static readonly Dictionary<string, ImageSource> GlyphCache = new Dictionary<string, ImageSource>();

        /// <summary>Drawn once per size / code / colour (the ribbon and every dialog header reuse it).</summary>
        public static ImageSource Glyph(int size, string code, Color color)
        {
            string key = size + "|" + code + "|" + color;
            lock (GlyphCache)
            {
                if (GlyphCache.TryGetValue(key, out ImageSource cached)) return cached;
            }
            ImageSource made = DrawGlyph(size, code ?? "", color);
            lock (GlyphCache) GlyphCache[key] = made;
            return made;
        }

        private static ImageSource DrawGlyph(int size, string code, Color color)
        {
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                double s = size;
                var fill = new SolidColorBrush(color);
                var border = new Pen(new SolidColorBrush(Color.FromRgb((byte)(color.R * 0.7), (byte)(color.G * 0.7), (byte)(color.B * 0.7))), s / 24.0);
                dc.DrawRoundedRectangle(fill, border, new Rect(s * 0.04, s * 0.04, s * 0.92, s * 0.92), s * 0.18, s * 0.18);

                string text = size < 24 && code.Length > 2 ? code.Substring(0, 2) : code;
                double em = text.Length <= 2 ? s * 0.42 : s * 0.32;
                var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Condensed),
                    em, Brushes.White, 1.0);
                dc.DrawText(ft, new Point((s - ft.Width) / 2, (s - ft.Height) / 2));
            }
            var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze();
            return bmp;
        }

        // ------------------------------------------------------------------ line icons
        /// <summary>
        /// Line pictograms (24 x 24 grid, stroke drawing) used on the ribbon, in the window headers
        /// and in the Help Center: the group colour draws the stroke over a soft tile of the same
        /// colour. Path syntax shared by WPF and SVG (every number separated by a space).
        /// </summary>
        public static readonly Dictionary<string, string> Paths = new Dictionary<string, string>
        {
            ["ambientes"] = "M 3 4 h 18 v 16 H 3 z M 3 12 h 9 M 12 4 v 16 M 12 15 h 9",
            ["mapeador"] = "M 3 4 h 7 v 5 h -7 Z M 3 14 h 7 v 6 h -7 Z M 14 4 h 7 v 16 h -7 Z M 10 6.5 h 4 M 10 17 h 4 M 16 8 h 3 M 16 11 h 3",
            ["central"] = "M 4 4 h 6 v 6 h -6 Z M 14 4 h 6 v 6 h -6 Z M 4 14 h 6 v 6 h -6 Z M 17 13.5 v 7 M 13.5 17 h 7",
            ["busca"] = "M 10.5 4 a 6.5 6.5 0 1 1 0 13 a 6.5 6.5 0 1 1 0 -13 Z M 15.2 15.2 L 20 20 M 7.5 10.5 h 6",
            ["estudar"] = "M 3 4 h 13 v 13 H 3 Z M 6 8 h 7 M 6 11 h 5 M 15 15 a 3.5 3.5 0 1 1 0.01 0 M 17.5 17.5 L 21 21",
            ["vinculo"] = "M 4 3 h 10 l 4 4 v 6 M 14 3 v 4 h 4 M 4 3 v 18 h 7 M 7 9 h 6 M 7 13 h 4 M 15 17 a 2.5 2.5 0 0 1 0 -5 h 2 M 19 12 a 2.5 2.5 0 0 1 0 5 h -2 M 15.5 14.5 h 3",
            ["estilos"] = "M 3 4 h 8 M 3 8 h 8 M 3 12 h 5 M 14 4 h 7 v 7 h -7 z M 3 16 h 18 M 3 20 h 18 M 16.5 14 v 8",
            ["padronizar"] = "M 3 5 h 9 M 3 9 h 6 M 3 13 h 8 M 14 7 h 7 M 18 4 l 3 3 -3 3 M 3 18 h 18",
            ["grupos"] = "M 3 3 h 7 v 7 H 3 z M 14 3 h 7 v 7 h -7 z M 3 14 h 7 v 7 H 3 z M 14 14 h 7 v 7 h -7 z M 1 1 h 2 M 21 1 h 2 M 1 23 h 2 M 21 23 h 2",
            ["camadas"] = "M 12 3 3 7.5 l 9 4.5 9 -4.5 z M 3 12 l 9 4.5 9 -4.5 M 3 16.5 12 21 l 9 -4.5",
            ["camadasPiso"] = "M 3 7 h 18 M 3 11 h 18 M 3 15 h 18 M 3 19 h 18 M 3 7 v 12 M 21 7 v 12",
            ["piso"] = "M 2 14 l 10 5 10 -5 -10 -5 z M 2 10 l 10 5 10 -5",
            ["forro"] = "M 3 5 h 18 M 5 5 v 4 M 19 5 v 4 M 3 9 h 18 M 8 13 l 4 4 4 -4 M 12 9 v 8",
            ["rodape"] = "M 4 3 v 17 h 17 M 4 16 h 17 M 8 16 v 4 M 13 16 v 4 M 18 16 v 4",
            ["tabica"] = "M 3 20 V 4 h 18 M 3 8 h 18 M 7 4 v 4 M 12 4 v 4 M 17 4 v 4",
            ["corte"] = "M 3 12 h 18 M 6 12 V 6 H 3 M 18 12 v 6 h 3",
            ["fachada"] = "M 3 7 L 8 3 L 13 7 L 21 4 M 3 11 H 21 V 20 H 3 Z M 9 11 V 20 M 15 11 V 20",
            ["plantas"] = "M 4 4 h 16 v 16 H 4 z M 4 10 h 7 v 10 M 15 4 v 6 h 5",
            ["plantaForro"] = "M 4 4 h 16 v 16 H 4 z M 4 9 h 16 M 9 4 v 5 M 15 4 v 5 M 9 14 h 6",
            ["olho"] = "M 2 12 s 4 -7 10 -7 10 7 10 7 -4 7 -10 7 S 2 12 2 12 z M 12 9 a 3 3 0 1 0 0 6 3 3 0 1 0 0 -6",
            ["cubo"] = "M 12 3 4 7.5 v 9 L 12 21 l 8 -4.5 v -9 z M 4 7.5 12 12 l 8 -4.5 M 12 12 v 9",
            ["elevacao"] = "M 3 20 h 18 M 5 20 V 9 l 7 -5 7 5 v 11 M 10 20 v -6 h 4 v 6",
            ["eixos"] = "M 7 3 v 18 M 17 3 v 18 M 3 8 h 18 M 3 16 h 18",
            ["eixosParede"] = "M 4 4 h 4 v 16 H 4 z M 16 4 h 4 v 16 h -4 z M 6 2 v 20 M 18 2 v 20",
            ["atualizar"] = "M 20 12 a 8 8 0 1 1 -2.3 -5.7 M 20 4 v 4 h -4",
            ["ocultar"] = "M 2 12 s 4 -7 10 -7 10 7 10 7 -4 7 -10 7 S 2 12 2 12 z M 4 4 l 16 16",
            ["detalhar"] = "M 4 4 h 11 l 5 5 v 11 H 4 z M 15 4 v 5 h 5 M 8 13 h 8 M 8 17 h 5",
            ["anotar"] = "M 4 7 h 16 v 13 H 4 z M 4 3 h 16 M 4 2 v 2 M 20 2 v 2 M 8 12 h 8 M 8 16 h 5",
            ["etiqueta"] = "M 3 12 V 4 h 8 l 10 10 -8 8 z M 7.5 8.5 h .01",
            ["ficha"] = "M 5 3 h 14 v 18 H 5 z M 9 7 h 6 M 9 11 h 6 M 9 15 h 4",
            ["hachura"] = "M 4 4 h 16 v 16 H 4 z M 4 12 l 8 -8 M 4 20 20 4 M 12 20 l 8 -8",
            ["unir"] = "M 3 3 h 10 v 10 H 3 z M 11 11 h 10 v 10 H 11 z",
            ["cebola"] = "M 3 3 h 9 v 18 H 3 z M 14 3 h 3 v 18 h -3 z M 21 12 h -3 M 19.5 10.5 L 18 12 l 1.5 1.5",
            ["paredesLajes"] = "M 2 3 h 20 v 4 H 2 z M 6 7 v 14 h 5 V 7 M 2 21 h 20",
            ["varinha"] = "M 4 20 15 9 M 14 3 v 3 M 20 9 h -3 M 18.5 4.5 17 6 M 10 5.5 11.5 7 M 18.5 13.5 17 12",
            ["anotacoes"] = "M 4 6 h 16 M 4 10 h 10 M 4 14 h 16 M 4 18 h 10",
            ["ajustar"] = "M 4 12 h 5 M 15 12 h 5 M 7 9 l -3 3 3 3 M 17 9 l 3 3 -3 3 M 12 5 v 14",
            ["alinhar"] = "M 4 3 v 18 M 8 7 h 12 v 4 H 8 z M 8 14 h 7 v 4 H 8 z",
            ["cota"] = "M 3 7 v 10 M 21 7 v 10 M 3 12 h 18 M 6 10 l -3 2 3 2 M 18 10 l 3 2 -3 2",
            ["cotaPiso"] = "M 4 9 h 16 v 11 H 4 z M 4 5 h 16 M 4 4 v 2 M 20 4 v 2",
            ["cotaForro"] = "M 4 4 h 16 v 9 H 4 z M 4 17 h 16 M 4 16 v 2 M 20 16 v 2",
            ["paredes"] = "M 4 4 h 4 v 16 H 4 z M 16 4 h 4 v 16 h -4 z M 8 12 h 8",
            ["cotaSelecao"] = "M 4 9 h 4 v 11 H 4 z M 16 9 h 4 v 11 h -4 z M 4 5 h 16 M 4 4 v 2 M 20 4 v 2 M 12 4 v 2",
            ["cotaTotal"] = "M 3 5 h 18 M 3 4 v 2 M 21 4 v 2 M 3 12 h 8 M 3 11 v 2 M 11 11 v 2 M 3 19 h 18 M 3 18 v 2 M 21 18 v 2",
            ["cotaPontos"] = "M 3 12 h 18 M 7 6 v 12 M 12 6 v 12 M 17 6 v 12",
            ["lampada"] = "M 9 18 h 6 M 10 21 h 4 M 12 3 a 6 6 0 0 0 -4 10.5 c 0.7 0.7 1 1.5 1 2.5 h 6 c 0 -1 0.3 -1.8 1 -2.5 A 6 6 0 0 0 12 3 z",
            ["tomada"] = "M 12 3 a 9 9 0 1 0 0 18 9 9 0 0 0 0 -18 z M 9 9 v 3 M 15 9 v 3 M 9 16 h 6",
            ["gota"] = "M 12 3 s 6 6.5 6 11 a 6 6 0 0 1 -12 0 c 0 -4.5 6 -11 6 -11 z",
            ["pranchas"] = "M 3 5 h 12 v 14 H 3 z M 7 9 h 4 M 7 13 h 4 M 18 8 h 3 v 12 h -9 v -1",
            ["renomear"] = "M 4 20 h 4 L 19 9 l -4 -4 L 4 16 z M 14 6 l 4 4",
            ["duplicar"] = "M 8 8 h 12 v 12 H 8 z M 4 16 V 4 h 12",
            ["exportar"] = "M 12 3 v 12 M 7 10 l 5 5 5 -5 M 4 19 h 16",
            ["imagem"] = "M 4 5 h 16 v 14 H 4 z M 4 15 l 5 -5 4 4 3 -3 4 4",
            ["tabela"] = "M 4 5 h 16 v 14 H 4 z M 4 10 h 16 M 10 5 v 14",
            ["numeros"] = "M 4 6 h 3 v 12 M 10 6 h 10 M 10 12 h 10 M 10 18 h 10",
            ["niveis"] = "M 3 7 h 12 M 3 12 h 18 M 3 17 h 12 M 18 5 l 3 2 -3 2",
            ["pontos"] = "M 3 5 h 12 v 12 H 3 z M 7 9 v 3 M 11 9 v 3 M 9 14 v .5 M 15 15 l 6 6 M 19 4 v 3 M 17.5 5.5 h 3",
            ["telemetria"] = "M 3 20 h 18 M 6 20 v -6 M 10.5 20 v -10 M 15 20 v -7 M 19.5 20 v -13 M 4 11 l 5 -5 l 4 3 l 7 -6",
            ["filtrar"] = "M 3 4 h 18 l -7 8 v 6 l -4 2 v -8 z",
            ["preencher"] = "M 3 6 h 10 M 3 11 h 8 M 3 16 h 5 M 12 20 l 1 -4 7 -7 3 3 -7 7 z",
            ["lixeira"] = "M 4 7 h 16 M 9 7 V 4 h 6 v 3 M 6 7 l 1 13 h 10 l 1 -13",
            ["ajuda"] = "M 12 21 a 9 9 0 1 0 0 -18 9 9 0 0 0 0 18 z M 9.5 9 a 2.5 2.5 0 1 1 3.5 2.3 c -0.7 0.3 -1 0.9 -1 1.7 M 12 17 h .01",
            ["padroes"] = "M 4 7 h 10 M 18 7 h 2 M 4 17 h 4 M 12 17 h 8 M 16 5 v 4 M 10 15 v 4",
            ["acessibilidade"] = "M 12 3 a 1.5 1.5 0 1 1 0 3 a 1.5 1.5 0 1 1 0 -3 Z M 12 7 v 6 h 5 l 2 5 M 12 9 h 5 M 9 10 a 6 6 0 1 0 7 8",
            ["escada"] = "M 3 20 h 4 v -4 h 4 v -4 h 4 v -4 h 4 v -4 h 2 M 3 20 V 4",
            ["carimbo"] = "M 4 15 h 16 v 5 H 4 z M 9 15 v -3 a 3 3 0 1 1 6 0 v 3 M 8 18 h 8",
            ["comparar"] = "M 3 4 h 7 v 16 H 3 z M 14 4 h 7 v 16 h -7 z M 5 8 h 3 M 5 12 h 3 M 16 8 h 3 M 16 12 h 3 M 16 16 h 3",
            ["pintura"] = "M 4 4 h 13 v 5 H 4 z M 17 6.5 h 3 v 6 h -8 v 3 M 11 15.5 h 2 v 5 h -2 z",
            // ---- JA Famílias tab
            ["janela"] = "M 4 3 h 16 v 18 H 4 z M 12 3 v 18 M 4 12 h 16",
            ["porta"] = "M 6 3 h 12 v 18 H 6 z M 15 12 h .01 M 3 21 h 18",
            ["bancada"] = "M 2 9 h 20 v 3 H 2 z M 4 12 v 8 M 20 12 v 8 M 8 9 V 6 h 4 v 3",
            ["nicho"] = "M 4 3 h 16 v 18 H 4 z M 8 8 h 8 v 8 H 8 z",
            ["bancadalocal"] = "M 3 4 h 8 v 3 h 4 V 4 h 6 v 8 h -8 v 8 H 3 z M 13 12 v -5 M 16 17 l 5 4 M 16 17 v 5 M 16 17 h 5",
            ["chapim"] = "M 2 8 h 20 v 3 H 2 z M 6 11 h 12 v 10 H 6 z",
            ["ralo"] = "M 2 7 h 20 v 4 H 2 z M 5 8.2 v 1.6 M 8 8.2 v 1.6 M 11 8.2 v 1.6 M 14 8.2 v 1.6 M 17 8.2 v 1.6 M 20 8.2 v 1.6 M 4 11 h 16 v 4 H 4 z M 10 15 h 4 v 7 h -4 z",
            ["editar"] = "M 4 20 h 4 L 19 9 l -4 -4 L 4 16 z M 14 6 l 4 4",
            ["biblioteca"] = "M 4 4 h 4 v 16 H 4 z M 10 4 h 4 v 16 h -4 z M 16 5 l 3 -1 3 15 -3 1 z",
            ["relatorio"] = "M 6 3 h 9 l 4 4 v 14 H 6 z M 14 3 v 5 h 5 M 9 13 h 6 M 9 17 h 6",
            ["config"] = "M 12 15 a 3 3 0 1 0 0 -6 3 3 0 0 0 0 6 z M 19 12 h 2 M 3 12 h 2 M 12 3 v 2 M 12 19 v 2 M 5.6 5.6 l 1.4 1.4 M 17 17 l 1.4 1.4 M 5.6 18.4 7 17 M 17 7 l 1.4 -1.4",
            ["perfil"] = "M 4 4 h 16 v 4 h -6 v 8 h 6 v 4 H 4 v -4 h 6 V 8 H 4 z",
            ["soleira"] = "M 4 3 h 16 v 13 H 4 z M 12 3 v 13 M 2 16 h 20 v 3 H 2 z",
            ["substituir"] = "M 3 4 h 7 v 9 H 3 z M 14 11 h 7 v 9 h -7 z M 13 6 h 5 v 3 M 16 7 l 2 2 2 -2 M 11 18 H 6 v -3 M 8 17 l -2 -2 -2 2",
            ["cortina"] = "M 3 3 h 18 v 18 H 3 z M 9 3 v 18 M 15 3 v 18 M 3 9 h 18 M 3 15 h 18",
            ["painel"] = "M 3 3 h 18 v 18 H 3 z M 6 6 l 12 2.5 M 6 10.5 l 12 2.5 M 6 15 l 12 2.5",
            ["paginacao"] = "M 3 3 h 18 v 18 H 3 z M 9 3 v 18 M 15 3 v 18 M 3 9 h 18 M 3 15 h 18",
            ["rodapePedra"] = "M 4 3 v 15 h 17 M 4 18 v 3 h 17 v -3",
            ["escadaRevestida"] = "M 3 21 h 5 v -5 h 5 v -5 h 5 v -5 h 3",
            ["guardacorpo"] = "M 3 20 h 18 M 3 8 h 18 M 5 8 v 12 M 12 8 v 12 M 19 8 v 12",
            ["box"] = "M 4 3 v 18 M 20 3 v 18 M 4 4 h 16 M 6 6 h 7 v 14 H 6 z M 11 7 h 7 v 13 h -7",
            ["sol"] = "M 12 8 a 4 4 0 1 0 0.01 0 M 12 2 v 3 M 12 19 v 3 M 2 12 h 3 M 19 12 h 3 M 5 5 l 2 2 M 17 17 l 2 2 M 5 19 l 2 -2 M 17 7 l 2 -2",
            ["trocar"] = "M 4 8 h 14 l -3 -3 M 20 16 H 6 l 3 3",
            ["corteEsquadria"] = "M 4 3 h 16 v 18 H 4 z M 9 3 v 18 M 15 3 v 18 M 2 12 h 4 M 18 12 h 4 M 6 12 l -2 -2 M 6 12 l -2 2",
            ["fachadaPaginada"] = "M 3 3 h 18 v 18 H 3 z M 9 3 v 18 M 15 3 v 18 M 3 11 h 18 M 3 16 h 18",
            ["conferir"] = "M 2 7 h 20 v 6 H 2 z M 7 7 v 6 M 17 7 v 6 M 9 10 h 6 M 12 19 l 2.5 2.5 L 20 16",
            ["forroSanca"] = "M 3 5 h 18 M 3 5 v 4 h 4 v 3 h 10 v -3 h 4 V 5 M 9 16 l 3 3 3 -3",
            ["ripado"] = "M 4 3 v 18 M 8 3 v 18 M 12 3 v 18 M 16 3 v 18 M 20 3 v 18 M 2 21 h 20",
            ["pergolado"] = "M 2 7 h 20 M 2 10 h 20 M 5 4 v 6 M 10 4 v 6 M 14 4 v 6 M 19 4 v 6 M 5 10 v 11 M 19 10 v 11 M 2 21 h 20",
            ["gotaImperm"] = "M 12 3 s 6 6.5 6 11 a 6 6 0 0 1 -12 0 c 0 -4.5 6 -11 6 -11 z M 3 21 h 18",
            ["compras"] = "M 3 4 h 3 l 2.5 11 h 10 L 21 8 H 7 M 10 19 a 1.2 1.2 0 1 0 0.01 0 M 17 19 a 1.2 1.2 0 1 0 0.01 0",
        };

        private static readonly Dictionary<string, ImageSource> IconCache = new Dictionary<string, ImageSource>();

        /// <summary>Line icon on a soft tile; null when the key is unknown (callers fall back to <see cref="Glyph"/>).</summary>
        public static ImageSource Icon(int size, string key, Color color)
        {
            if (string.IsNullOrEmpty(key) || !Paths.TryGetValue(key, out string data)) return null;
            string cacheKey = size + "|" + key + "|" + color;
            lock (IconCache)
            {
                if (IconCache.TryGetValue(cacheKey, out ImageSource cached)) return cached;
            }
            ImageSource made = null;
            try { made = DrawIcon(size, data, color); }
            catch { made = null; }
            if (made == null) return null;
            lock (IconCache) IconCache[cacheKey] = made;
            return made;
        }

        /// <summary>Icon for a tool: its line icon, or the old lettered tile when it has none.</summary>
        public static ImageSource Tool(int size, string iconKey, string glyph, Color color) =>
            Icon(size, iconKey, color) ?? Glyph(size, glyph ?? "JA", color);

        /// <summary>Soft version of a colour (mixed with white), for tiles and highlighted boxes.</summary>
        public static Color Soft(Color c, double amount = 0.12) =>
            Color.FromRgb((byte)Math.Round(255 - (255 - c.R) * amount), (byte)Math.Round(255 - (255 - c.G) * amount), (byte)Math.Round(255 - (255 - c.B) * amount));

        private static ImageSource DrawIcon(int size, string data, Color color)
        {
            Geometry g = Geometry.Parse(data);
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                double s = size;
                var tile = new SolidColorBrush(Soft(color, size <= 16 ? 0.16 : 0.13));
                tile.Freeze();
                dc.DrawRoundedRectangle(tile, null, new Rect(0, 0, s, s), s * 0.22, s * 0.22);

                double inner = s * (size <= 16 ? 0.78 : 0.64);
                double k = inner / 24.0;
                double off = (s - inner) / 2;
                double px = size <= 16 ? 1.3 : size <= 32 ? 1.8 : 2.2;   // stroke on screen, in pixels
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                var pen = new Pen(brush, px / k) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                pen.Freeze();
                dc.PushTransform(new TranslateTransform(off, off));
                dc.PushTransform(new ScaleTransform(k, k));
                dc.DrawGeometry(null, pen, g);
                dc.Pop();
                dc.Pop();
            }
            var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze();
            return bmp;
        }
    }
}
