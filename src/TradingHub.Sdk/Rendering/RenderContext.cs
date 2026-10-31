using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using SkiaSharp;
using TradingHub.Rendering.Tools;

namespace TradingHub.Rendering.Context
{
    /// <summary>
    /// Superficie de dibujo que reciben los indicadores en OnRender. API al estilo GDI+
    /// (Rectangle, Point, Color, RenderFont, RenderPen) implementada sobre SkiaSharp.
    /// </summary>
    public class RenderContext
    {
        private static readonly ConcurrentDictionary<(string, bool, bool), SKTypeface> Typefaces = new();
        private static readonly ConcurrentDictionary<(string, float, bool, bool), SKFont> Fonts = new();

        private readonly SKCanvas _canvas;
        private readonly SKPaint _fill = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
        private readonly SKPaint _stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke };
        private readonly SKPaint _text = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
        private readonly Rectangle _fullBounds;
        private int _clipSave = -1;
        private Rectangle _clip;

        public RenderContext(SKCanvas canvas, Rectangle fullBounds)
        {
            _canvas = canvas;
            _fullBounds = fullBounds;
            _clip = fullBounds;
        }

        /// <summary>Canvas de Skia subyacente (para dibujo avanzado).</summary>
        public SKCanvas Canvas => _canvas;

        public Rectangle ClipBounds => _clip;

        public RenderTextRenderingHint TextRenderingHint { get; private set; } = RenderTextRenderingHint.AntiAlias;

        public void SetTextRenderingHint(RenderTextRenderingHint hint) => TextRenderingHint = hint;

        // ------------------------------------------------------------------ fuentes

        public static SKTypeface GetTypeface(string family, bool bold, bool italic)
        {
            return Typefaces.GetOrAdd((family ?? "Arial", bold, italic), k =>
            {
                var tf = SKTypeface.FromFamilyName(k.Item1,
                    k.Item2 ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
                    SKFontStyleWidth.Normal,
                    k.Item3 ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
                return tf ?? SKTypeface.Default;
            });
        }

        public static SKFont GetFont(RenderFont font) => GetFont(font.FontFamily, font.PixelSize, font.Bold, font.Italic);

        public static SKFont GetFont(string family, float pixelSize, bool bold, bool italic)
        {
            pixelSize = MathF.Round(pixelSize * 4) / 4f;
            return Fonts.GetOrAdd((family ?? "Arial", pixelSize, bold, italic), k =>
                new SKFont(GetTypeface(k.Item1, k.Item3, k.Item4), Math.Max(1f, k.Item2))
                {
                    Subpixel = true,
                    Edging = SKFontEdging.Antialias,
                    Hinting = SKFontHinting.Slight,
                });
        }


        private static readonly ConcurrentDictionary<SKFont, SKPaint> MeasurePaints = new();

        /// <summary>Ancho en píxeles de un texto con una fuente.</summary>
        public static float MeasureWidth(SKFont font, string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            // como se dibuja: traducido y, si a la letra le faltan caracteres (chino), con la del sistema
            text = UiText.Tr(text);
            font = UiText.FontFor(text, font) ?? font;
            var paint = MeasurePaints.GetOrAdd(font, f => new SKPaint
            {
                Typeface = f.Typeface,
                TextSize = f.Size,
                SubpixelText = true,
                IsAntialias = true,
            });
            lock (paint) return paint.MeasureText(text);
        }

        // ------------------------------------------------------------------ medición

        public Size MeasureString(string text, RenderFont font)
        {
            if (string.IsNullOrEmpty(text) || font == null) return new Size(0, font == null ? 0 : (int)Math.Ceiling(GetFont(font).Spacing));
            var f = GetFont(font);
            var lines = text.Split('\n');
            float w = 0;
            foreach (var l in lines) w = Math.Max(w, MeasureWidth(f, l.TrimEnd('\r')));
            return new Size((int)Math.Ceiling(w) + 1, (int)Math.Ceiling(f.Spacing * lines.Length));
        }

        public Size MeasureString(string text, RenderFont font, int maxWidth) => MeasureString(text, font);

        public SizeF MeasureStringF(string text, RenderFont font)
        {
            var s = MeasureString(text, font);
            return new SizeF(s.Width, s.Height);
        }

        // ------------------------------------------------------------------ texto

        public void DrawString(string text, RenderFont font, Color color, int x, int y) => DrawStringCore(text, font, color, x, y);

        public void DrawString(string text, RenderFont font, Color color, float x, float y) => DrawStringCore(text, font, color, x, y);

        public void DrawString(string text, RenderFont font, Color color, int x, int y, RenderStringFormat format) => DrawStringCore(text, font, color, x, y);

        public void DrawString(string text, RenderFont font, Color color, Point location) => DrawStringCore(text, font, color, location.X, location.Y);

        public void DrawString(string text, RenderFont font, Color color, Rectangle rect) =>
            DrawString(text, font, color, rect, new RenderStringFormat());

        public void DrawString(string text, RenderFont font, Color color, RectangleF rect, RenderStringFormat format) =>
            DrawString(text, font, color, Rectangle.Round(rect), format);

        public void DrawString(string text, RenderFont font, Color color, Rectangle rect, RenderStringFormat format)
        {
            if (string.IsNullOrEmpty(text) || font == null || color.A == 0) return;
            var f = GetFont(font);
            format ??= new RenderStringFormat();
            var lines = text.Replace("\r", "").Split('\n');
            float lineH = f.Spacing;
            float totalH = lineH * lines.Length;

            bool clip = (format.FormatFlags & StringFormatFlags.NoClip) == 0;
            if (clip)
            {
                _canvas.Save();
                _canvas.ClipRect(new SKRect(rect.Left, rect.Top, rect.Right + 1, rect.Bottom + 1));
            }

            float y0 = format.LineAlignment switch
            {
                StringAlignment.Center => rect.Top + (rect.Height - totalH) / 2f,
                StringAlignment.Far => rect.Bottom - totalH,
                _ => rect.Top,
            };

            _text.Color = color.ToSkColor();
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (format.Trimming is StringTrimming.EllipsisCharacter or StringTrimming.EllipsisWord && MeasureWidth(f, line) > rect.Width)
                    line = Ellipsize(line, f, rect.Width);
                float w = MeasureWidth(f, line);
                float x = format.Alignment switch
                {
                    StringAlignment.Center => rect.Left + (rect.Width - w) / 2f,
                    StringAlignment.Far => rect.Right - w,
                    _ => rect.Left,
                };
                float baseline = y0 + i * lineH - f.Metrics.Ascent;
                _canvas.DrawUiText(line, x, baseline, f, _text);
            }

            if (clip) _canvas.Restore();
        }

        private static string Ellipsize(string s, SKFont f, float maxW)
        {
            const string ell = "…";
            for (int n = s.Length - 1; n > 0; n--)
            {
                var t = s[..n] + ell;
                if (MeasureWidth(f, t) <= maxW) return t;
            }

            return ell;
        }

        private void DrawStringCore(string text, RenderFont font, Color color, float x, float y)
        {
            if (string.IsNullOrEmpty(text) || font == null || color.A == 0) return;
            var f = GetFont(font);
            _text.Color = color.ToSkColor();
            if (text.IndexOf('\n') < 0)
            {
                _canvas.DrawUiText(text, x, y - f.Metrics.Ascent, f, _text);
                return;
            }

            var lines = text.Replace("\r", "").Split('\n');
            for (int i = 0; i < lines.Length; i++)
                _canvas.DrawUiText(lines[i], x, y + i * f.Spacing - f.Metrics.Ascent, f, _text);
        }

        // ------------------------------------------------------------------ líneas

        private SKPaint Stroke(RenderPen pen)
        {
            _stroke.Color = pen.Color.ToSkColor();
            float w = Math.Max(1f, pen.Width);
            _stroke.StrokeWidth = w;
            _stroke.PathEffect = DashEffect(pen.DashStyle, w, pen.DashPattern);
            _stroke.StrokeCap = SKStrokeCap.Butt;
            return _stroke;
        }

        private static SKPathEffect? DashEffect(DashStyle style, float w, float[]? pattern)
        {
            float u = Math.Max(1f, w);
            return style switch
            {
                DashStyle.Dash => SKPathEffect.CreateDash(new[] { 6 * u, 3 * u }, 0),
                DashStyle.Dot => SKPathEffect.CreateDash(new[] { 1.5f * u, 2 * u }, 0),
                DashStyle.DashDot => SKPathEffect.CreateDash(new[] { 6 * u, 2 * u, 1.5f * u, 2 * u }, 0),
                DashStyle.DashDotDot => SKPathEffect.CreateDash(new[] { 6 * u, 2 * u, 1.5f * u, 2 * u, 1.5f * u, 2 * u }, 0),
                DashStyle.Custom when pattern is { Length: >= 2 } => SKPathEffect.CreateDash(pattern.Select(p => p * u).ToArray(), 0),
                _ => null,
            };
        }

        private static float Crisp(float v, float width) => ((int)Math.Round(width) % 2 == 1) ? MathF.Floor(v) + 0.5f : MathF.Round(v);

        public void DrawLine(RenderPen pen, int x1, int y1, int x2, int y2) => DrawLine(pen, (float)x1, y1, x2, y2);

        public void DrawLine(RenderPen pen, float x1, float y1, float x2, float y2)
        {
            if (pen == null || pen.Color.A == 0) return;
            var p = Stroke(pen);
            if (x1 == x2)
            {
                x1 = x2 = Crisp(x1, p.StrokeWidth);
                p.IsAntialias = false;
            }
            else if (y1 == y2)
            {
                y1 = y2 = Crisp(y1, p.StrokeWidth);
                p.IsAntialias = false;
            }

            _canvas.DrawLine(x1, y1, x2, y2, p);
            p.IsAntialias = true;
        }

        public void DrawLine(RenderPen pen, Point p1, Point p2) => DrawLine(pen, p1.X, p1.Y, p2.X, p2.Y);

        public void DrawLines(RenderPen pen, Point[] points)
        {
            if (pen == null || points == null || points.Length < 2) return;
            using var path = new SKPath();
            path.MoveTo(points[0].X, points[0].Y);
            for (int i = 1; i < points.Length; i++) path.LineTo(points[i].X, points[i].Y);
            _canvas.DrawPath(path, Stroke(pen));
        }

        public void DrawLines(RenderPen pen, PointF[] points)
        {
            if (pen == null || points == null || points.Length < 2) return;
            using var path = new SKPath();
            path.MoveTo(points[0].X, points[0].Y);
            for (int i = 1; i < points.Length; i++) path.LineTo(points[i].X, points[i].Y);
            _canvas.DrawPath(path, Stroke(pen));
        }

        // ------------------------------------------------------------------ rectángulos

        public void FillRectangle(Color color, Rectangle rect)
        {
            if (color.A == 0 || rect.Width <= 0 || rect.Height <= 0) return;
            _fill.Color = color.ToSkColor();
            _fill.IsAntialias = false;
            _canvas.DrawRect(rect.X, rect.Y, rect.Width, rect.Height, _fill);
            _fill.IsAntialias = true;
        }

        public void FillRectangle(Color color, RectangleF rect) => FillRectangle(color, Rectangle.Round(rect));

        public void FillRectangle(Color color, Rectangle rect, int radius)
        {
            if (radius <= 0)
            {
                FillRectangle(color, rect);
                return;
            }

            if (color.A == 0 || rect.Width <= 0 || rect.Height <= 0) return;
            _fill.Color = color.ToSkColor();
            _canvas.DrawRoundRect(new SKRect(rect.Left, rect.Top, rect.Right, rect.Bottom), radius, radius, _fill);
        }

        public void FillRectangle(Color startColor, Color endColor, Rectangle rect, bool isVertical = true)
        {
            if (rect.Width <= 0 || rect.Height <= 0) return;
            using var shader = SKShader.CreateLinearGradient(
                new SKPoint(rect.Left, rect.Top),
                isVertical ? new SKPoint(rect.Left, rect.Bottom) : new SKPoint(rect.Right, rect.Top),
                new[] { startColor.ToSkColor(), endColor.ToSkColor() }, SKShaderTileMode.Clamp);
            using var paint = new SKPaint { Shader = shader, IsAntialias = false };
            _canvas.DrawRect(rect.X, rect.Y, rect.Width, rect.Height, paint);
        }

        public void DrawRectangle(RenderPen pen, Rectangle rect)
        {
            if (pen == null || pen.Color.A == 0) return;
            var p = Stroke(pen);
            float l = Crisp(rect.Left, p.StrokeWidth), t = Crisp(rect.Top, p.StrokeWidth);
            float r = Crisp(rect.Right, p.StrokeWidth), b = Crisp(rect.Bottom, p.StrokeWidth);
            p.IsAntialias = false;
            _canvas.DrawRect(new SKRect(l, t, r, b), p);
            p.IsAntialias = true;
        }

        public void DrawRectangle(RenderPen pen, Rectangle rect, int radius)
        {
            if (radius <= 0)
            {
                DrawRectangle(pen, rect);
                return;
            }

            if (pen == null || pen.Color.A == 0) return;
            _canvas.DrawRoundRect(new SKRect(rect.Left + 0.5f, rect.Top + 0.5f, rect.Right - 0.5f, rect.Bottom - 0.5f), radius, radius, Stroke(pen));
        }

        public void DrawFillRectangle(RenderPen pen, Color fill, Rectangle rect)
        {
            FillRectangle(fill, rect);
            DrawRectangle(pen, rect);
        }

        public void DrawFillRectangle(RenderPen pen, Color fill, Rectangle rect, int radius)
        {
            FillRectangle(fill, rect, radius);
            DrawRectangle(pen, rect, radius);
        }

        // ------------------------------------------------------------------ elipses y polígonos

        public void FillEllipse(Color color, Rectangle rect)
        {
            if (color.A == 0) return;
            _fill.Color = color.ToSkColor();
            _canvas.DrawOval(new SKRect(rect.Left, rect.Top, rect.Right, rect.Bottom), _fill);
        }

        public void DrawEllipse(RenderPen pen, Rectangle rect)
        {
            if (pen == null || pen.Color.A == 0) return;
            _canvas.DrawOval(new SKRect(rect.Left, rect.Top, rect.Right, rect.Bottom), Stroke(pen));
        }

        public void FillEllipse(Color color, RectangleF rect) => FillEllipse(color, Rectangle.Round(rect));
        public void DrawEllipse(RenderPen pen, RectangleF rect) => DrawEllipse(pen, Rectangle.Round(rect));

        private static SKPath Poly(IReadOnlyList<Point> points)
        {
            var path = new SKPath();
            path.MoveTo(points[0].X, points[0].Y);
            for (int i = 1; i < points.Count; i++) path.LineTo(points[i].X, points[i].Y);
            path.Close();
            return path;
        }

        public void FillPolygon(Color color, Point[] points)
        {
            if (points == null || points.Length < 3 || color.A == 0) return;
            using var path = Poly(points);
            _fill.Color = color.ToSkColor();
            _canvas.DrawPath(path, _fill);
        }

        public void FillPolygon(Color startColor, Color endColor, Point[] points, bool isVertical)
        {
            if (points == null || points.Length < 3) return;
            using var path = Poly(points);
            var b = path.Bounds;
            using var shader = SKShader.CreateLinearGradient(
                new SKPoint(b.Left, b.Top),
                isVertical ? new SKPoint(b.Left, b.Bottom) : new SKPoint(b.Right, b.Top),
                new[] { startColor.ToSkColor(), endColor.ToSkColor() }, SKShaderTileMode.Clamp);
            using var paint = new SKPaint { Shader = shader, IsAntialias = true };
            _canvas.DrawPath(path, paint);
        }

        public void DrawPolygon(RenderPen pen, Point[] points)
        {
            if (pen == null || points == null || points.Length < 2) return;
            using var path = Poly(points);
            _canvas.DrawPath(path, Stroke(pen));
        }

        public void DrawArc(RenderPen pen, Rectangle rect, float startAngle, float sweepAngle)
        {
            if (pen == null) return;
            using var path = new SKPath();
            path.AddArc(new SKRect(rect.Left, rect.Top, rect.Right, rect.Bottom), startAngle, sweepAngle);
            _canvas.DrawPath(path, Stroke(pen));
        }

        // ------------------------------------------------------------------ recorte

        public void SetClip(Rectangle rect)
        {
            ResetClip();
            _clipSave = _canvas.Save();
            _canvas.ClipRect(new SKRect(rect.Left, rect.Top, rect.Right, rect.Bottom));
            _clip = rect;
        }

        public void ResetClip()
        {
            if (_clipSave >= 0)
            {
                _canvas.RestoreToCount(_clipSave);
                _clipSave = -1;
            }

            _clip = _fullBounds;
        }

        public void DrawStaticImage(object image, Rectangle rect)
        {
            // Las imágenes estáticas (logos) no se soportan todavía.
        }

        public void Flush()
        {
        }
    }

    internal static class ColorExt
    {
        public static SKColor ToSkColor(this Color c) => new(c.R, c.G, c.B, c.A);
    }
}
