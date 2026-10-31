using System.Globalization;
using SkiaSharp;
using TradingHub.Strategies;
using TradingHub.Rendering;

namespace TradingHub.App.Lab;

/// <summary>Gráficos de los informes (PNG, estilo documento: fondo blanco, apto para imprimir).</summary>
internal static class ReportCharts
{
    private static readonly SKColor Bg = SKColors.White;
    private static readonly SKColor GridC = new(232, 235, 241);
    private static readonly SKColor AxisC = new(118, 124, 138);
    private static readonly SKColor TextC = new(34, 38, 46);
    private static readonly SKColor UpC = new(22, 163, 107);
    private static readonly SKColor DownC = new(220, 53, 69);
    private static readonly SKColor LineC = new(37, 99, 235);
    private static readonly SKColor[] Palette =
    {
        new(37, 99, 235), new(234, 140, 0), new(22, 163, 107), new(168, 85, 247), new(220, 53, 69), new(14, 165, 233), new(132, 94, 54), new(236, 72, 153),
    };

    private static readonly CultureInfo Es = TradingHub.Common.UiCulture.Current;
    private static SKTypeface Face(bool bold = false) => SKTypeface.FromFamilyName(TradingHub.Common.PlatformFonts.SansSkia, bold ? SKFontStyle.Bold : SKFontStyle.Normal) ?? SKTypeface.Default;

    private static string Compact(double v)
    {
        double a = Math.Abs(v);
        string s = a >= 1_000_000 ? (v / 1_000_000).ToString("0.#", Es) + "M" : a >= 10_000 ? (v / 1000).ToString("0.#", Es) + "K" : v.ToString(a >= 100 ? "N0" : "N2", Es);
        return s;
    }

    private static double[] Ticks(double min, double max, int n)
    {
        if (max <= min) max = min + 1;
        double raw = (max - min) / n;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double step = new[] { 1, 2, 2.5, 5, 10 }.Select(k => k * mag).First(s => s >= raw);
        var list = new List<double>();
        for (double v = Math.Ceiling(min / step) * step; v <= max + step * 1e-6; v += step) list.Add(v);
        return list.ToArray();
    }

    private static float Tw(SKFont f, string s) => TradingHub.Rendering.Context.RenderContext.MeasureWidth(f, s);

    private static byte[] Encode(SKSurface surface)
    {
        using var img = surface.Snapshot();
        using var data = img.Encode(SKEncodedImageFormat.Png, 92);
        return data.ToArray();
    }

    private static void Title(SKCanvas c, string title, float x, float y)
    {
        using var font = new SKFont(Face(true), 22);
        using var p = new SKPaint { Color = TextC, IsAntialias = true };
        c.DrawUiText(title, x, y, font, p);
    }

    /// <summary>Curva de capital arriba y drawdown ("bajo el agua") abajo. Series extra en colores (componentes).</summary>
    public static byte[] Equity(IReadOnlyList<(DateTime Time, decimal Equity)> points, decimal capital, string title,
        IReadOnlyList<(string Label, IReadOnlyList<(DateTime Time, decimal Equity)> Points)>? extra = null)
    {
        const int W = 1600, H = 820;
        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        var c = surface.Canvas;
        c.Clear(Bg);
        Title(c, title, 40, 46);
        if (points.Count < 2) return Encode(surface);

        float left = 110, right = W - 40, top = 80, eqBottom = 560, ddTop = 600, ddBottom = H - 70;
        var t0 = points[0].Time;
        var t1 = points[^1].Time;
        double span = Math.Max(1, (t1 - t0).TotalSeconds);
        float X(DateTime t) => left + (float)((t - t0).TotalSeconds / span) * (right - left);

        var all = points.Select(p => (double)p.Equity).ToList();
        if (extra != null) all.AddRange(extra.SelectMany(e => e.Points.Select(p => (double)p.Equity)));
        double min = Math.Min(all.Min(), (double)capital), max = Math.Max(all.Max(), (double)capital);
        double pad = (max - min) * 0.06 + 1e-9;
        min -= pad;
        max += pad;
        float Y(double v) => eqBottom - (float)((v - min) / (max - min)) * (eqBottom - top);

        using var grid = new SKPaint { Color = GridC, StrokeWidth = 1, IsAntialias = false };
        using var axisText = new SKPaint { Color = AxisC, IsAntialias = true };
        using var font = new SKFont(Face(), 17);
        foreach (var v in Ticks(min, max, 6))
        {
            float y = MathF.Round(Y(v)) + 0.5f;
            c.DrawLine(left, y, right, y, grid);
            var s = Compact(v);
            c.DrawUiText(s, left - 12 - Tw(font, s), y + 6, font, axisText);
        }

        // capital inicial
        using (var cap = new SKPaint { Color = AxisC, StrokeWidth = 1.5f, PathEffect = SKPathEffect.CreateDash(new[] { 8f, 6f }, 0), IsAntialias = true })
            c.DrawLine(left, Y((double)capital), right, Y((double)capital), cap);

        if (extra != null)
        {
            for (int k = 0; k < extra.Count; k++)
            {
                using var path = new SKPath();
                var pts = extra[k].Points;
                for (int i = 0; i < pts.Count; i++)
                {
                    float x = X(pts[i].Time), y = Y((double)pts[i].Equity);
                    if (i == 0) path.MoveTo(x, y);
                    else path.LineTo(x, y);
                }

                using var pe = new SKPaint { Color = Palette[(k + 1) % Palette.Length].WithAlpha(150), StrokeWidth = 2, Style = SKPaintStyle.Stroke, IsAntialias = true };
                c.DrawPath(path, pe);
            }
        }

        using (var area = new SKPath())
        using (var line = new SKPath())
        {
            for (int i = 0; i < points.Count; i++)
            {
                float x = X(points[i].Time), y = Y((double)points[i].Equity);
                if (i == 0)
                {
                    line.MoveTo(x, y);
                    area.MoveTo(x, eqBottom);
                }
                else line.LineTo(x, y);

                area.LineTo(x, y);
            }

            area.LineTo(X(points[^1].Time), eqBottom);
            area.Close();
            using var fill = new SKPaint { IsAntialias = true, Shader = SKShader.CreateLinearGradient(new SKPoint(0, top), new SKPoint(0, eqBottom), new[] { LineC.WithAlpha(70), LineC.WithAlpha(8) }, SKShaderTileMode.Clamp) };
            c.DrawPath(area, fill);
            using var stroke = new SKPaint { Color = LineC, StrokeWidth = 3, Style = SKPaintStyle.Stroke, IsAntialias = true };
            c.DrawPath(line, stroke);
        }

        // drawdown
        decimal peak = capital;
        var dd = points.Select(p =>
        {
            peak = Math.Max(peak, p.Equity);
            return (p.Time, Pct: peak > 0 ? (double)((p.Equity - peak) / peak * 100) : 0);
        }).ToList();
        double ddMin = Math.Min(-0.5, dd.Min(d => d.Pct) * 1.1);
        float YD(double v) => ddTop + (float)(v / ddMin) * (ddBottom - ddTop);
        foreach (var v in Ticks(ddMin, 0, 3))
        {
            float y = MathF.Round(YD(v)) + 0.5f;
            c.DrawLine(left, y, right, y, grid);
            var s = v.ToString("0.#", Es) + "%";
            c.DrawUiText(s, left - 12 - Tw(font, s), y + 6, font, axisText);
        }

        using (var ddPath = new SKPath())
        {
            ddPath.MoveTo(X(dd[0].Time), YD(0));
            foreach (var d in dd) ddPath.LineTo(X(d.Time), YD(d.Pct));
            ddPath.LineTo(X(dd[^1].Time), YD(0));
            ddPath.Close();
            using var ddFill = new SKPaint { Color = DownC.WithAlpha(90), IsAntialias = true };
            c.DrawPath(ddPath, ddFill);
        }

        using (var label = new SKFont(Face(true), 16))
        using (var lp = new SKPaint { Color = AxisC, IsAntialias = true })
        {
            c.DrawUiText("CAPITAL", left, top - 10, label, lp);
            c.DrawUiText("DRAWDOWN", left, ddTop - 12, label, lp);
        }

        // fechas
        int nDates = 6;
        for (int i = 0; i <= nDates; i++)
        {
            var t = t0.AddSeconds(span * i / nDates);
            var s = t.ToString(span > 86400 * 200 ? "MMM yyyy" : span > 86400 * 3 ? "dd MMM" : "dd/MM HH:mm", Es);
            float x = X(t);
            c.DrawUiText(s, Math.Clamp(x - Tw(font, s) / 2, left, right - Tw(font, s)), H - 34, font, axisText);
        }

        // leyenda de componentes
        if (extra is { Count: > 0 })
        {
            float lx = left + 10, ly = top + 22;
            using var lf = new SKFont(Face(), 16);
            using var lt = new SKPaint { Color = TextC, IsAntialias = true };
            void Item(string text, SKColor col)
            {
                using var sw = new SKPaint { Color = col, IsAntialias = true };
                c.DrawRoundRect(new SKRect(lx, ly - 11, lx + 22, ly - 3), 2, 2, sw);
                c.DrawUiText(text, lx + 30, ly, lf, lt);
                ly += 24;
            }

            Item("Portafolio", LineC);
            for (int k = 0; k < extra.Count; k++) Item(extra[k].Label, Palette[(k + 1) % Palette.Length]);
        }

        return Encode(surface);
    }

    /// <summary>Rendimiento mensual (% sobre el capital al empezar el mes) como mapa de calor.</summary>
    public static byte[] Monthly(IReadOnlyList<YearReturns> years)
    {
        int W = 1600, rowH = 52, H = 130 + Math.Max(1, years.Count) * rowH;
        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        var c = surface.Canvas;
        c.Clear(Bg);
        Title(c, "Rendimiento mensual", 40, 46);
        string[] months = { "Ene", "Feb", "Mar", "Abr", "May", "Jun", "Jul", "Ago", "Sep", "Oct", "Nov", "Dic", "Año" };
        float left = 110, top = 96, cellW = (W - left - 40) / 13f;
        using var font = new SKFont(Face(), 17);
        using var bold = new SKFont(Face(true), 17);
        using var tp = new SKPaint { Color = AxisC, IsAntialias = true };
        for (int m = 0; m < 13; m++)
            c.DrawUiText(months[m], left + m * cellW + cellW / 2 - Tw(font, months[m]) / 2, top - 12, font, tp);
        double maxAbs = years.SelectMany(y => y.MonthPercent).Where(v => v.HasValue).Select(v => Math.Abs((double)v!.Value)).DefaultIfEmpty(1).Max();
        maxAbs = Math.Max(maxAbs, 0.5);
        for (int r = 0; r < years.Count; r++)
        {
            var y = years[r];
            float yy = top + r * rowH;
            using var yp = new SKPaint { Color = TextC, IsAntialias = true };
            c.DrawUiText(y.Year.ToString(CultureInfo.InvariantCulture), 40, yy + rowH / 2f + 6, bold, yp);
            for (int m = 0; m < 13; m++)
            {
                decimal? v = m < 12 ? y.MonthPercent[m] : y.YearPercent;
                var rect = new SKRect(left + m * cellW + 3, yy + 3, left + (m + 1) * cellW - 3, yy + rowH - 3);
                SKColor bg = GridC.WithAlpha(110);
                if (v.HasValue)
                {
                    double k = Math.Min(1, Math.Abs((double)v.Value) / maxAbs);
                    var col = v.Value >= 0 ? UpC : DownC;
                    bg = col.WithAlpha((byte)(40 + 170 * k));
                }

                using var fill = new SKPaint { Color = bg, IsAntialias = true };
                c.DrawRoundRect(rect, 6, 6, fill);
                if (!v.HasValue) continue;
                var s = (v.Value >= 0 ? "+" : "") + v.Value.ToString("0.0", Es) + "%";
                using var txt = new SKPaint { Color = Math.Abs((double)v.Value) / maxAbs > 0.6 ? SKColors.White : TextC, IsAntialias = true };
                c.DrawUiText(s, rect.MidX - Tw(m == 12 ? bold : font, s) / 2, rect.MidY + 6, m == 12 ? bold : font, txt);
            }
        }

        return Encode(surface);
    }

    /// <summary>Distribución del resultado de las operaciones.</summary>
    public static byte[] Histogram(IReadOnlyList<(decimal From, decimal To, int Count)> buckets)
    {
        const int W = 1600, H = 620;
        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        var c = surface.Canvas;
        c.Clear(Bg);
        Title(c, "Distribución de resultados por operación", 40, 46);
        if (buckets.Count == 0) return Encode(surface);
        float left = 90, right = W - 40, top = 90, bottom = H - 90;
        int max = Math.Max(1, buckets.Max(b => b.Count));
        float bw = (right - left) / buckets.Count;
        using var font = new SKFont(Face(), 16);
        using var tp = new SKPaint { Color = AxisC, IsAntialias = true };
        using var grid = new SKPaint { Color = GridC, StrokeWidth = 1 };
        foreach (var v in Ticks(0, max, 4))
        {
            float y = bottom - (float)(v / max) * (bottom - top);
            c.DrawLine(left, y, right, y, grid);
            var s = v.ToString("0", Es);
            c.DrawUiText(s, left - 12 - Tw(font, s), y + 6, font, tp);
        }

        for (int i = 0; i < buckets.Count; i++)
        {
            var b = buckets[i];
            float h = (float)b.Count / max * (bottom - top);
            var rect = new SKRect(left + i * bw + 6, bottom - h, left + (i + 1) * bw - 6, bottom);
            decimal mid = (b.From + b.To) / 2;
            using var fill = new SKPaint { Color = (mid >= 0 ? UpC : DownC).WithAlpha(200), IsAntialias = true };
            c.DrawRoundRect(rect, 4, 4, fill);
            if (b.Count > 0)
            {
                var n = b.Count.ToString(CultureInfo.InvariantCulture);
                using var np = new SKPaint { Color = TextC, IsAntialias = true };
                c.DrawUiText(n, rect.MidX - Tw(font, n) / 2, rect.Top - 8, font, np);
            }

            var lbl = Compact((double)mid);
            c.DrawUiText(lbl, rect.MidX - Tw(font, lbl) / 2, bottom + 28, font, tp);
        }

        return Encode(surface);
    }

    /// <summary>Curvas de Monte Carlo (operaciones en otro orden).</summary>
    public static byte[] Curves(IReadOnlyList<IReadOnlyList<decimal>> curves, decimal capital, string title)
    {
        const int W = 1600, H = 620;
        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        var c = surface.Canvas;
        c.Clear(Bg);
        Title(c, title, 40, 46);
        if (curves.Count == 0 || curves[0].Count < 2) return Encode(surface);
        float left = 110, right = W - 40, top = 80, bottom = H - 60;
        double min = curves.Min(x => (double)x.Min()), max = curves.Max(x => (double)x.Max());
        double pad = (max - min) * 0.05 + 1e-9;
        min -= pad;
        max += pad;
        int n = curves.Max(x => x.Count);
        float X(int i) => left + (float)i / (n - 1) * (right - left);
        float Y(double v) => bottom - (float)((v - min) / (max - min)) * (bottom - top);
        using var font = new SKFont(Face(), 17);
        using var tp = new SKPaint { Color = AxisC, IsAntialias = true };
        using var grid = new SKPaint { Color = GridC, StrokeWidth = 1 };
        foreach (var v in Ticks(min, max, 5))
        {
            float y = Y(v);
            c.DrawLine(left, y, right, y, grid);
            var s = Compact(v);
            c.DrawUiText(s, left - 12 - Tw(font, s), y + 6, font, tp);
        }

        foreach (var curve in curves)
        {
            using var path = new SKPath();
            for (int i = 0; i < curve.Count; i++)
            {
                if (i == 0) path.MoveTo(X(i), Y((double)curve[i]));
                else path.LineTo(X(i), Y((double)curve[i]));
            }

            using var p = new SKPaint { Color = LineC.WithAlpha(45), StrokeWidth = 1.6f, Style = SKPaintStyle.Stroke, IsAntialias = true };
            c.DrawPath(path, p);
        }

        using (var cap = new SKPaint { Color = AxisC, StrokeWidth = 1.5f, PathEffect = SKPathEffect.CreateDash(new[] { 8f, 6f }, 0), IsAntialias = true })
            c.DrawLine(left, Y((double)capital), right, Y((double)capital), cap);
        var lbl = "operaciones →";
        c.DrawUiText(lbl, right - Tw(font, lbl), H - 22, font, tp);
        return Encode(surface);
    }

    /// <summary>Matriz de correlación (−1 a 1) entre las estrategias de un portafolio.</summary>
    public static byte[] Correlation(IReadOnlyList<string> labels, double[,] corr)
    {
        int n = labels.Count;
        int cell = Math.Clamp(900 / Math.Max(1, n), 70, 160);
        int labelW = 420;
        int W = Math.Max(900, labelW + n * cell + 60), H = 140 + n * cell + 40;
        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        var c = surface.Canvas;
        c.Clear(Bg);
        Title(c, "Correlación de los resultados diarios", 40, 46);
        float left = labelW, top = 110;
        using var font = new SKFont(Face(), 17);
        using var bold = new SKFont(Face(true), 19);
        using var tp = new SKPaint { Color = TextC, IsAntialias = true };
        for (int i = 0; i < n; i++)
        {
            string l = $"{i + 1}. {labels[i]}";
            while (Tw(font, l) > labelW - 50 && l.Length > 4) l = l[..^2];
            c.DrawUiText(l, 40, top + i * cell + cell / 2f + 6, font, tp);
            var num = (i + 1).ToString(CultureInfo.InvariantCulture);
            c.DrawUiText(num, left + i * cell + cell / 2f - Tw(font, num) / 2, top - 14, font, tp);
            for (int j = 0; j < n; j++)
            {
                double v = corr[i, j];
                var rect = new SKRect(left + j * cell + 3, top + i * cell + 3, left + (j + 1) * cell - 3, top + (i + 1) * cell - 3);
                var col = i == j ? GridC : v >= 0 ? DownC.WithAlpha((byte)(30 + 200 * Math.Min(1, Math.Abs(v)))) : LineC.WithAlpha((byte)(30 + 200 * Math.Min(1, Math.Abs(v))));
                using var fill = new SKPaint { Color = col, IsAntialias = true };
                c.DrawRoundRect(rect, 6, 6, fill);
                var s = v.ToString("0.00", Es);
                using var txt = new SKPaint { Color = Math.Abs(v) > 0.6 && i != j ? SKColors.White : TextC, IsAntialias = true };
                c.DrawUiText(s, rect.MidX - Tw(bold, s) / 2, rect.MidY + 7, bold, txt);
            }
        }

        return Encode(surface);
    }
}
