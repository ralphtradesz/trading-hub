using System.Collections.Concurrent;
using SkiaSharp;

namespace TradingHub.Rendering
{
    /// <summary>
    /// Texto de la interfaz dibujado directo con Skia (gráficos, DOM, cinta, leyendas): se traduce al idioma elegido y, si
    /// la fuente no tiene sus letras (chino), se dibuja con una del sistema que sí las tiene (PingFang en Mac, YaHei en
    /// Windows). La app conecta <see cref="Translate"/> al arrancar; sin eso el texto queda como está.
    /// </summary>
    public static class UiText
    {
        /// <summary>Traducción de un texto en español (null = sin traducción). La pone la app según el idioma.</summary>
        public static Func<string, string?>? Translate { get; set; }

        private static readonly ConcurrentDictionary<(string Family, SKFontStyleWeight Weight, int Char), SKTypeface?> Fallbacks = new();
        private static readonly ConcurrentDictionary<(SKTypeface Typeface, float Size, bool Embolden, SKFontEdging Edging), SKFont> Fonts = new();

        /// <summary>El texto traducido, o el mismo.</summary>
        public static string Tr(string text) => Translate is { } t && !string.IsNullOrEmpty(text) && t(text) is { } r ? r : text;

        /// <summary>Dibuja <paramref name="text"/> traducido, con una fuente del sistema si la del gráfico no tiene sus letras.</summary>
        public static void DrawUiText(this SKCanvas canvas, string? text, float x, float y, SKFont font, SKPaint paint)
        {
            if (string.IsNullOrEmpty(text)) return;
            text = Tr(text);
            canvas.DrawText(text, x, y, FontFor(text, font) ?? font, paint);
        }

        /// <summary>Ancho de <paramref name="text"/> como lo dibuja <see cref="DrawUiText"/> (traducido y con la fuente que corresponda).</summary>
        public static float MeasureUiText(this SKFont font, string? text) =>
            string.IsNullOrEmpty(text) ? 0 : Context.RenderContext.MeasureWidth(font, text);

        /// <summary>
        /// La fuente con otra letra si a la original le faltan caracteres del texto (queda guardada: no se libera); null si
        /// no hace falta.
        /// </summary>
        public static SKFont? FontFor(string text, SKFont font)
        {
            var typeface = font.Typeface ?? SKTypeface.Default;
            int missing = FirstMissing(text, typeface);
            if (missing < 0) return null;
            var key = (typeface.FamilyName ?? "", (SKFontStyleWeight)typeface.FontWeight, missing);
            var other = Fallbacks.GetOrAdd(key, k => SKFontManager.Default.MatchCharacter(k.Family, k.Weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright, null, k.Char));
            if (other == null) return null;
            return Fonts.GetOrAdd((other, font.Size, font.Embolden, font.Edging), k =>
                new SKFont(k.Typeface, k.Size) { Edging = k.Edging, Subpixel = font.Subpixel, Hinting = font.Hinting, Embolden = k.Embolden });
        }

        // el primer carácter que la letra no tiene (-1 si tiene todos); solo mira lo que no es ASCII
        private static int FirstMissing(string text, SKTypeface typeface)
        {
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c < 0x2E80) continue; // latín, acentos, símbolos comunes: los tienen todas
                int cp = char.IsHighSurrogate(c) && i + 1 < text.Length ? char.ConvertToUtf32(c, text[++i]) : c;
                if (typeface.GetGlyph(cp) == 0) return cp;
            }

            return -1;
        }
    }
}
