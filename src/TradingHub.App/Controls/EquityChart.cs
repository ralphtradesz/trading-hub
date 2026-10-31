using System.Globalization;
using Avalonia.Input;
using SkiaSharp;
using TradingHub.Rendering.Context;
using TradingHub.Strategies;
using TradingHub.Rendering;

namespace TradingHub.App.Controls;

/// <summary>Curva de capital con el drawdown debajo (y curvas extra, por ejemplo de Monte Carlo).</summary>
public sealed class EquityChart : SkiaView
{
    private IReadOnlyList<EquityPoint> _points = Array.Empty<EquityPoint>();
    private IReadOnlyList<IReadOnlyList<decimal>> _extra = Array.Empty<IReadOnlyList<decimal>>();
    private IReadOnlyList<(DateTime Time, string Label)> _marks = Array.Empty<(DateTime, string)>();
    private decimal _capital;
    private float? _mouseX;

    // colores del tema (claro u oscuro): se leen en cada dibujo
    private SKColor Bg, Grid, Text, Up, Down, Accent, Strong, Box;

    public EquityChart()
    {
        CornerRadius = 7;
        AttachedToVisualTree += (_, _) => Services.AppTheme.Changed += OnTheme;
        DetachedFromVisualTree += (_, _) => Services.AppTheme.Changed -= OnTheme;
    }

    private void OnTheme() => InvalidateVisual();

    private static SKColor Sk(string key)
    {
        var c = Ui.Color(key);
        return new SKColor(c.R, c.G, c.B, c.A);
    }

    private void LoadColors()
    {
        bool light = Services.AppTheme.IsLight;
        Bg = light ? Sk("Bg2") : new SKColor(0x0F, 0x14, 0x1D);
        Grid = light ? Sk("Line") : new SKColor(0x1C, 0x24, 0x32);
        Text = light ? Sk("Text2") : new SKColor(0x6E, 0x78, 0x89);
        Up = Sk("Up");
        Down = Sk("Down");
        Accent = Sk("Accent");
        Strong = Sk("Text0");
        Box = light ? new SKColor(0x11, 0x11, 0x11, 235) : new SKColor(0x1F, 0x28, 0x38);
    }

    public string EmptyText { get; set; } = "Corré un backtest para ver la curva de capital";

    public void SetData(IReadOnlyList<EquityPoint> points, decimal capital, IReadOnlyList<IReadOnlyList<decimal>>? extra = null, IReadOnlyList<(DateTime, string)>? marks = null)
    {
        _points = points;
        _capital = capital;
        _extra = extra ?? Array.Empty<IReadOnlyList<decimal>>();
        _marks = marks ?? Array.Empty<(DateTime, string)>();
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _mouseX = (float)e.GetPosition(this).X;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _mouseX = null;
        InvalidateVisual();
    }

    protected override void Draw(SKCanvas canvas, float width, float height)
    {
        LoadColors();
        canvas.Clear(Bg);
        var font = RenderContext.GetFont("Inter", 11, false, false);
        using var text = new SKPaint { IsAntialias = true, Color = Text };
        if (_points.Count < 2 && _extra.Count == 0)
        {
            float tw = RenderContext.MeasureWidth(font, EmptyText);
            canvas.DrawUiText(EmptyText, width / 2 - tw / 2, height / 2, font, text);
            return;
        }

        const float left = 12, right = 74, top = 14, bottom = 22;
        float plotW = width - left - right;
        float eqH = (height - top - bottom) * 0.72f;
        float ddTop = top + eqH + 10, ddH = height - bottom - ddTop;

        // escalas
        decimal min = _capital, max = _capital, maxDd = 0;
        foreach (var p in _points)
        {
            min = Math.Min(min, p.Equity);
            max = Math.Max(max, p.Equity);
            maxDd = Math.Max(maxDd, p.Drawdown);
        }

        foreach (var c in _extra)
        foreach (var v in c)
        {
            min = Math.Min(min, v);
            max = Math.Max(max, v);
        }

        if (max == min) max = min + 1;
        decimal pad = (max - min) * 0.06m;
        min -= pad;
        max += pad;
        float Y(decimal v) => top + (float)((max - v) / (max - min)) * eqH;

        using var grid = new SKPaint { Color = Grid, StrokeWidth = 1, IsAntialias = false };
        bool fine = max - min < 2000;
        for (int i = 0; i <= 4; i++)
        {
            decimal v = min + (max - min) * i / 4;
            float y = MathF.Floor(Y(v)) + 0.5f;
            canvas.DrawLine(left, y, left + plotW, y, grid);
            canvas.DrawUiText(fine ? v.ToString("#,##0.##", TradingHub.Common.UiCulture.Current) : Format(v), left + plotW + 8, y + 4, font, text);
        }

        // capital inicial
        using (var cap = new SKPaint { Color = Text.WithAlpha(140), StrokeWidth = 1, PathEffect = SKPathEffect.CreateDash(new[] { 4f, 4f }, 0) })
        {
            float y = MathF.Floor(Y(_capital)) + 0.5f;
            canvas.DrawLine(left, y, left + plotW, y, cap);
        }

        // curvas extra (Monte Carlo)
        using (var ex = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = new SKColor(0x3D, 0x8B, 0xFD, 50) })
        {
            foreach (var c in _extra)
            {
                if (c.Count < 2) continue;
                using var path = new SKPath();
                for (int i = 0; i < c.Count; i++)
                {
                    float x = left + plotW * i / (c.Count - 1);
                    if (i == 0) path.MoveTo(x, Y(c[i]));
                    else path.LineTo(x, Y(c[i]));
                }

                canvas.DrawPath(path, ex);
            }
        }

        if (_points.Count >= 2)
        {
            long t0 = _points[0].Time.Ticks, t1 = _points[^1].Time.Ticks;
            if (t1 == t0) t1 = t0 + 1;
            float X(DateTime t) => left + plotW * (float)((t.Ticks - t0) / (double)(t1 - t0));

            // área bajo la curva
            using var line = new SKPath();
            using var area = new SKPath();
            area.MoveTo(X(_points[0].Time), Y(min));
            for (int i = 0; i < _points.Count; i++)
            {
                float x = X(_points[i].Time), y = Y(_points[i].Equity);
                if (i == 0) line.MoveTo(x, y);
                else line.LineTo(x, y);
                area.LineTo(x, y);
            }

            area.LineTo(X(_points[^1].Time), Y(min));
            area.Close();
            bool positive = _points[^1].Equity >= _capital;
            var color = positive ? Up : Down;
            using (var fill = new SKPaint { IsAntialias = true })
            {
                fill.Shader = SKShader.CreateLinearGradient(new SKPoint(0, top), new SKPoint(0, top + eqH), new[] { color.WithAlpha(60), color.WithAlpha(0) }, SKShaderTileMode.Clamp);
                canvas.DrawPath(area, fill);
            }

            using (var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.6f, Color = color })
                canvas.DrawPath(line, stroke);

            // separadores de ventanas (walk-forward)
            using (var mark = new SKPaint { Color = Accent.WithAlpha(110), StrokeWidth = 1, PathEffect = SKPathEffect.CreateDash(new[] { 3f, 3f }, 0) })
            {
                foreach (var (t, label) in _marks)
                {
                    float x = MathF.Floor(X(t)) + 0.5f;
                    canvas.DrawLine(x, top, x, top + eqH, mark);
                    canvas.DrawUiText(label, x + 3, top + 10, font, text);
                }
            }

            // drawdown
            if (maxDd > 0 && ddH > 10)
            {
                using var dd = new SKPath();
                dd.MoveTo(X(_points[0].Time), ddTop);
                foreach (var p in _points) dd.LineTo(X(p.Time), ddTop + (float)(p.Drawdown / maxDd) * ddH);
                dd.LineTo(X(_points[^1].Time), ddTop);
                dd.Close();
                using var ddFill = new SKPaint { IsAntialias = true, Color = Down.WithAlpha(90) };
                canvas.DrawPath(dd, ddFill);
                canvas.DrawUiText("DD " + Format(-maxDd), left + plotW + 8, ddTop + ddH, font, text);
                canvas.DrawLine(left, ddTop - 0.5f, left + plotW, ddTop - 0.5f, grid);
            }

            // fechas
            for (int i = 0; i <= 4; i++)
            {
                var t = new DateTime(t0 + (t1 - t0) * i / 4, DateTimeKind.Utc).ToLocalTime();
                long span = t1 - t0;
                string s = t.ToString(span > TimeSpan.TicksPerDay * 3 ? "dd/MM" : span > TimeSpan.TicksPerDay ? "dd/MM HH:mm" : span > TimeSpan.TicksPerHour * 2 ? "HH:mm" : "HH:mm:ss", CultureInfo.InvariantCulture);
                float x = left + plotW * i / 4;
                float w = RenderContext.MeasureWidth(font, s);
                canvas.DrawUiText(s, Math.Clamp(x - w / 2, left, left + plotW - w), height - 6, font, text);
            }

            // cursor
            if (_mouseX is float mx && mx >= left && mx <= left + plotW)
            {
                var target = t0 + (long)((mx - left) / plotW * (t1 - t0));
                int lo = 0, hi = _points.Count - 1;
                while (lo < hi)
                {
                    int mid = (lo + hi) / 2;
                    if (_points[mid].Time.Ticks < target) lo = mid + 1;
                    else hi = mid;
                }

                var p = _points[lo];
                float x = X(p.Time), y = Y(p.Equity);
                using var cross = new SKPaint { Color = Strong.WithAlpha(60), StrokeWidth = 1 };
                canvas.DrawLine(x, top, x, height - bottom, cross);
                using var dot = new SKPaint { IsAntialias = true, Color = Accent };
                canvas.DrawCircle(x, y, 3.5f, dot);
                string label = $"{p.Time.ToLocalTime():dd/MM HH:mm}  ·  {Format(p.Equity)}  ·  DD {Format(-p.Drawdown)}";
                float lw = RenderContext.MeasureWidth(font, label) + 14;
                float lx = Math.Clamp(x - lw / 2, left, left + plotW - lw);
                using var box = new SKPaint { IsAntialias = true, Color = Box };
                canvas.DrawRoundRect(new SKRect(lx, top, lx + lw, top + 20), 5, 5, box);
                using var white = new SKPaint { IsAntialias = true, Color = new SKColor(0xE8, 0xEB, 0xF1) };
                canvas.DrawUiText(label, lx + 7, top + 14, font, white);
            }
        }
    }

    private static string Format(decimal v)
    {
        decimal a = Math.Abs(v);
        string s = a >= 1_000_000 ? (v / 1_000_000).ToString("0.##", CultureInfo.InvariantCulture) + "M"
            : a >= 10_000 ? (v / 1000).ToString("0.#", CultureInfo.InvariantCulture) + "k"
            : v.ToString("0.##", CultureInfo.InvariantCulture);
        return s;
    }
}
