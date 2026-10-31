using System.Globalization;
using SkiaSharp;
using TradingHub.Data;
using TradingHub.Rendering.Context;
using TradingHub.Rendering;

namespace TradingHub.Charting.Drawings;

public enum DrawingKind
{
    HorizontalLine,
    HorizontalRay,
    VerticalLine,
    TrendLine,
    Ray,
    ExtendedLine,
    Rectangle,
    FibRetracement,
    Text,
    Ruler,
    LongPosition,
    ShortPosition,
}

public enum LineStyleKind
{
    Solid,
    Dashed,
    Dotted,
}

/// <summary>Punto anclado en el tiempo y el precio (sobrevive a cambios de período).</summary>
public struct ChartPoint
{
    public DateTime Time { get; set; }
    public double Price { get; set; }

    public ChartPoint(DateTime time, double price)
    {
        Time = time;
        Price = price;
    }
}

/// <summary>Nivel de un retroceso de Fibonacci (valor, si se ve y su color; 0 = el del dibujo).</summary>
public sealed class FibLevel
{
    public double Value { get; set; }
    public bool Visible { get; set; } = true;
    public uint ColorArgb { get; set; }

    public FibLevel Clone() => new() { Value = Value, Visible = Visible, ColorArgb = ColorArgb };
}

/// <summary>Objeto dibujado por el usuario sobre el gráfico.</summary>
public sealed class ChartDrawing
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DrawingKind Kind { get; set; }
    public List<ChartPoint> Points { get; set; } = new();
    public uint ColorArgb { get; set; } = 0xFF1E90FF;
    public float Width { get; set; } = 1.5f;

    /// <summary>Formato viejo (antes de <see cref="LineStyle"/>): se sigue leyendo.</summary>
    public bool Dashed { get; set; }

    public LineStyleKind LineStyle { get; set; }

    /// <summary>Texto del dibujo (en líneas y zonas se muestra como etiqueta).</summary>
    public string Text { get; set; } = "";

    public bool Locked { get; set; }

    /// <summary>Relleno de zonas (0 = el color de la línea, transparente).</summary>
    public uint FillArgb { get; set; }

    /// <summary>Extender hacia la izquierda / derecha (líneas de tendencia y zonas).</summary>
    public bool ExtendLeft { get; set; }
    public bool ExtendRight { get; set; }

    /// <summary>Mostrar el precio en las líneas y en los niveles.</summary>
    public bool ShowPriceLabel { get; set; } = true;

    public float FontSize { get; set; } = 13;
    public bool Bold { get; set; }

    /// <summary>Color del texto (0 = el de la línea).</summary>
    public uint TextArgb { get; set; }

    /// <summary>Oculto (sigue guardado; se vuelve a mostrar desde su panel o la lista de dibujos).</summary>
    public bool Hidden { get; set; }

    /// <summary>Si se ve solo en un período (código, p. ej. "M5"); null = en todos.</summary>
    public string? OnlyPeriod { get; set; }

    /// <summary>Alerta de precio en líneas horizontales (avisa cuando el precio la cruza).</summary>
    public bool Alert { get; set; }

    /// <summary>Niveles de Fibonacci propios (null = los de siempre).</summary>
    public List<FibLevel>? Levels { get; set; }

    /// <summary>Posición larga/corta: cantidad para calcular ganancia y riesgo (0 = sin montos).</summary>
    public decimal Quantity { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool Selected { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public SKColor Color
    {
        get => new(ColorArgb);
        set => ColorArgb = (uint)value;
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public LineStyleKind EffectiveLineStyle => LineStyle != LineStyleKind.Solid ? LineStyle : Dashed ? LineStyleKind.Dashed : LineStyleKind.Solid;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsPosition => Kind is DrawingKind.LongPosition or DrawingKind.ShortPosition;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsAlert => Alert || Text == "alerta";

    public int RequiredPoints => Kind switch
    {
        DrawingKind.HorizontalLine or DrawingKind.HorizontalRay or DrawingKind.VerticalLine or DrawingKind.Text => 1,
        DrawingKind.LongPosition or DrawingKind.ShortPosition => 1,
        _ => 2,
    };

    public static readonly double[] FibLevels = { 0, 0.236, 0.382, 0.5, 0.618, 0.786, 1, 1.272, 1.618 };

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<FibLevel> EffectiveLevels => Levels ?? FibLevels.Select(v => new FibLevel { Value = v }).ToList();

    public static string KindName(DrawingKind k) => k switch
    {
        DrawingKind.HorizontalLine => "Línea horizontal",
        DrawingKind.HorizontalRay => "Rayo horizontal",
        DrawingKind.VerticalLine => "Línea vertical",
        DrawingKind.TrendLine => "Línea de tendencia",
        DrawingKind.Ray => "Rayo",
        DrawingKind.ExtendedLine => "Línea extendida",
        DrawingKind.Rectangle => "Rectángulo / zona",
        DrawingKind.FibRetracement => "Retroceso de Fibonacci",
        DrawingKind.Text => "Texto",
        DrawingKind.Ruler => "Regla",
        DrawingKind.LongPosition => "Posición larga",
        DrawingKind.ShortPosition => "Posición corta",
        _ => k.ToString(),
    };

    /// <summary>Si se dibuja en un gráfico de ese período.</summary>
    public bool IsVisibleOn(ChartPeriod period) => !Hidden && (OnlyPeriod == null || OnlyPeriod == period.Code);

    /// <summary>Copia independiente (otro Id).</summary>
    public ChartDrawing Clone()
    {
        var c = (ChartDrawing)MemberwiseClone();
        c.Id = Guid.NewGuid();
        c.Points = Points.ToList();
        c.Levels = Levels?.Select(l => l.Clone()).ToList();
        c.Selected = false;
        return c;
    }

    /// <summary>Toma el estilo (no la posición) de otro dibujo: lo usa "predeterminado" y "copiar estilo".</summary>
    public void CopyStyleFrom(ChartDrawing src)
    {
        ColorArgb = src.ColorArgb;
        Width = src.Width;
        LineStyle = src.EffectiveLineStyle;
        Dashed = false;
        FillArgb = src.FillArgb;
        ExtendLeft = src.ExtendLeft;
        ExtendRight = src.ExtendRight;
        ShowPriceLabel = src.ShowPriceLabel;
        FontSize = src.FontSize;
        Bold = src.Bold;
        TextArgb = src.TextArgb;
        Levels = src.Levels?.Select(l => l.Clone()).ToList();
        Quantity = src.Quantity;
    }

    // ------------------------------------------------------------------ posición larga/corta

    /// <summary>Arma una posición con stop y objetivo por defecto (1:2) desde la entrada (Points[0]).</summary>
    public void InitPosition(double risk, DateTime end)
    {
        var e = Points[0];
        bool isLong = Kind == DrawingKind.LongPosition;
        double target = isLong ? e.Price + risk * 2 : e.Price - risk * 2;
        double stop = isLong ? e.Price - risk : e.Price + risk;
        Points = new List<ChartPoint> { e, new(end, target), new(end, stop) };
    }

    /// <summary>
    /// Arrastre de un manejador. En la posición: 0 = entrada, 1 = objetivo, 2 = stop (solo precio) y 3 = largo
    /// (hasta cuándo). En el resto, el punto correspondiente.
    /// </summary>
    public void MoveHandle(int handle, ChartPoint p)
    {
        if (IsPosition && Points.Count >= 3)
        {
            switch (handle)
            {
                case 0: Points[0] = p; break;
                case 1: Points[1] = new ChartPoint(Points[1].Time, p.Price); break;
                case 2: Points[2] = new ChartPoint(Points[2].Time, p.Price); break;
                case 3:
                    Points[1] = new ChartPoint(p.Time, Points[1].Price);
                    Points[2] = new ChartPoint(p.Time, Points[2].Price);
                    break;
            }

            return;
        }

        if (handle >= 0 && handle < Points.Count) Points[handle] = p;
    }

    /// <summary>Resultado de la posición sobre las velas: si tocó el objetivo o el stop primero.</summary>
    public (string Status, int Outcome) PositionOutcome(CandleSeries series)
    {
        if (!IsPosition || Points.Count < 3 || series.Count == 0) return ("", 0);
        bool isLong = Kind == DrawingKind.LongPosition;
        double target = Points[1].Price, stop = Points[2].Price;
        var end = Points[1].Time > Points[0].Time ? Points[1].Time : DateTime.MaxValue;
        // desde la vela siguiente a la entrada (la vela de la entrada ya tenía su rango antes de entrar)
        int first = series.FindBar(Points[0].Time) + 1;
        if (first < 1) first = 1;
        for (int i = first; i < series.Count && series[i].Time <= end; i++)
        {
            var c = series[i];
            double hi = (double)series.TicksToPrice(c.HighTicks), lo = (double)series.TicksToPrice(c.LowTicks);
            bool hitStop = isLong ? lo <= stop : hi >= stop;
            bool hitTarget = isLong ? hi >= target : lo <= target;
            if (hitStop) return ("Stop tocado", -1); // si toca los dos en la misma vela, se toma el peor caso
            if (hitTarget) return ("Objetivo tocado", 1);
        }

        return ("", 0);
    }

    // ------------------------------------------------------------------ coordenadas

    /// <summary>X de un instante (interpola entre velas y extrapola al futuro).</summary>
    public static float XOf(ChartLayout layout, CandleSeries series, DateTime time)
    {
        if (series.Count == 0) return layout.Plot.Right;
        var lastTime = series[^1].Time;
        if (time > lastTime)
        {
            var dur = series.Period.ApproxDuration;
            double bars = dur > TimeSpan.Zero ? (time - lastTime).Ticks / (double)dur.Ticks : 1;
            return layout.CenterX(series.Count - 1 + bars);
        }

        int bar = series.FindBar(time);
        if (bar < 0) return layout.CenterX(0);
        double frac = 0;
        if (bar + 1 < series.Count)
        {
            var t0 = series[bar].Time;
            var t1 = series[bar + 1].Time;
            if (t1 > t0) frac = Math.Clamp((time - t0).Ticks / (double)(t1 - t0).Ticks, 0, 0.999);
        }

        return layout.CenterX(bar + frac);
    }

    /// <summary>Instante en una coordenada X.</summary>
    public static DateTime TimeAt(ChartLayout layout, CandleSeries series, float x)
    {
        if (series.Count == 0) return DateTime.UtcNow;
        double b = layout.BarAtExact(x);
        int bar = (int)Math.Floor(b);
        double frac = b - bar;
        if (bar >= series.Count - 1)
        {
            var dur = series.Period.ApproxDuration;
            var last = series[^1].Time;
            double extra = b - (series.Count - 1);
            return dur > TimeSpan.Zero ? last + TimeSpan.FromTicks((long)(dur.Ticks * extra)) : last;
        }

        if (bar < 0) return series[0].Time;
        var t0 = series[bar].Time;
        var t1 = series[bar + 1].Time;
        return t0 + TimeSpan.FromTicks((long)((t1 - t0).Ticks * frac));
    }

    // ------------------------------------------------------------------ dibujo

    private SKPathEffect? Effect(float scale = 1) => EffectiveLineStyle switch
    {
        LineStyleKind.Dashed => SKPathEffect.CreateDash(new[] { 6f * scale, 4f * scale }, 0),
        LineStyleKind.Dotted => SKPathEffect.CreateDash(new[] { 1.5f, 3f }, 0),
        _ => null,
    };

    public void Render(SKCanvas canvas, ChartLayout layout, CandleSeries series, ChartTheme theme, int decimals)
    {
        if (Points.Count == 0 || !IsVisibleOn(series.Period)) return;
        var main = layout.Main;
        var plot = main.Rect;
        using var stroke = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Selected ? Width + 1 : Width,
            Color = Color,
            PathEffect = Effect(),
        };
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = FillArgb != 0 ? new SKColor(FillArgb) : Color.WithAlpha(40) };
        using var text = new SKPaint { IsAntialias = true, Color = TextArgb != 0 ? new SKColor(TextArgb) : Color };
        var font = RenderContext.GetFont(theme.FontFamily, 11, false, false);
        var labelFont = RenderContext.GetFont(theme.FontFamily, Math.Clamp(FontSize, 8, 40), Bold, false);

        var p0 = Points[0];
        float x0 = XOf(layout, series, p0.Time), y0 = main.Y(p0.Price);
        float x1 = x0, y1 = y0;
        if (Points.Count > 1)
        {
            x1 = XOf(layout, series, Points[1].Time);
            y1 = main.Y(Points[1].Price);
        }

        string label = Text == "alerta" ? "" : Text;
        switch (Kind)
        {
            case DrawingKind.HorizontalLine:
                canvas.DrawLine(plot.Left, y0, plot.Right, y0, stroke);
                if (ShowPriceLabel)
                {
                    string pl = Format(p0.Price, decimals);
                    canvas.DrawUiText(pl, plot.Right - 4 - RenderContext.MeasureWidth(font, pl), y0 - 3, font, text);
                }

                if (label.Length > 0) canvas.DrawUiText(label, plot.Left + 6, y0 - 4, labelFont, text);
                if (IsAlert) DrawBell(canvas, plot.Right - (ShowPriceLabel ? 70 : 14), y0 - 8, Color);
                break;
            case DrawingKind.HorizontalRay:
                canvas.DrawLine(ExtendLeft ? plot.Left : x0, y0, plot.Right, y0, stroke);
                if (ShowPriceLabel)
                {
                    string pl = Format(p0.Price, decimals);
                    canvas.DrawUiText(pl, plot.Right - 4 - RenderContext.MeasureWidth(font, pl), y0 - 3, font, text);
                }

                if (label.Length > 0) canvas.DrawUiText(label, x0 + 6, y0 - 4, labelFont, text);
                if (IsAlert) DrawBell(canvas, x0 + 8, y0 - 8, Color);
                break;
            case DrawingKind.VerticalLine:
                canvas.DrawLine(x0, plot.Top, x0, layout.Panels[^1].Rect.Bottom, stroke);
                if (label.Length > 0) canvas.DrawUiText(label, x0 + 5, plot.Top + labelFont.Size + 4, labelFont, text);
                break;
            case DrawingKind.TrendLine:
            case DrawingKind.Ray:
            case DrawingKind.ExtendedLine:
            {
                bool left = Kind == DrawingKind.ExtendedLine || (Kind == DrawingKind.TrendLine && ExtendLeft);
                bool right = Kind is DrawingKind.Ray or DrawingKind.ExtendedLine || (Kind == DrawingKind.TrendLine && ExtendRight);
                var (ax, ay, bx, by) = Extend(x0, y0, x1, y1, plot, left, right);
                canvas.DrawLine(ax, ay, bx, by, stroke);
                if (label.Length > 0)
                {
                    float w = RenderContext.MeasureWidth(labelFont, label);
                    canvas.DrawUiText(label, (x0 + x1) / 2 - w / 2, (y0 + y1) / 2 - 6, labelFont, text);
                }

                break;
            }
            case DrawingKind.Rectangle:
            {
                var r = new SKRect(Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1));
                if (ExtendRight) r.Right = plot.Right;
                if (ExtendLeft) r.Left = plot.Left;
                canvas.DrawRect(r, fill);
                canvas.DrawRect(r, stroke);
                if (label.Length > 0) canvas.DrawUiText(label, r.Left + 5, r.Top + labelFont.Size + 3, labelFont, text);
                break;
            }
            case DrawingKind.FibRetracement:
            {
                double pHigh = Points[0].Price, pLow = Points.Count > 1 ? Points[1].Price : pHigh;
                float left = ExtendLeft ? plot.Left : Math.Min(x0, x1);
                float right = ExtendRight || Points.Count < 2 ? plot.Right : Math.Max(Math.Max(x0, x1), plot.Right);
                foreach (var lvl in EffectiveLevels)
                {
                    if (!lvl.Visible) continue;
                    double price = pLow + (pHigh - pLow) * (1 - lvl.Value);
                    float y = main.Y(price);
                    stroke.Color = lvl.ColorArgb != 0 ? new SKColor(lvl.ColorArgb) : Color;
                    text.Color = stroke.Color;
                    stroke.PathEffect = lvl.Value is 0 or 1 ? Effect() : SKPathEffect.CreateDash(new[] { 4f, 3f }, 0);
                    canvas.DrawLine(left, y, right, y, stroke);
                    string l = lvl.Value.ToString("0.###", CultureInfo.InvariantCulture) + (ShowPriceLabel ? $"  ({Format(price, decimals)})" : "");
                    canvas.DrawUiText(l, left + 3, y - 3, font, text);
                }

                break;
            }
            case DrawingKind.Text:
                canvas.DrawUiText(string.IsNullOrEmpty(Text) ? "Texto" : Text, x0, y0, labelFont, text);
                break;
            case DrawingKind.Ruler:
            {
                var r = new SKRect(Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1));
                bool up = Points.Count > 1 && Points[1].Price >= Points[0].Price;
                var col = up ? theme.UpCandle : theme.DownCandle;
                fill.Color = col.WithAlpha(45);
                stroke.Color = col;
                canvas.DrawRect(r, fill);
                canvas.DrawLine(x0, y0, x1, y1, stroke);
                if (Points.Count > 1)
                {
                    double diff = Points[1].Price - Points[0].Price;
                    double pct = Points[0].Price != 0 ? diff / Points[0].Price * 100 : 0;
                    long ticks = series.Instrument.TickSize > 0 ? (long)Math.Round(diff / (double)series.Instrument.TickSize) : 0;
                    int bars = Math.Abs(layout.BarAt(x1) - layout.BarAt(x0));
                    var span = Points[1].Time - Points[0].Time;
                    string l1 = $"{(diff >= 0 ? "+" : "")}{Format(diff, decimals)} ({pct:+0.00;-0.00}%)  {ticks} ticks";
                    string l2 = $"{bars} barras · {FormatSpan(span)}";
                    DrawBox(canvas, font, (x0 + x1) / 2, Math.Max(y0, y1) + 6, SKColors.White, l1, l2);
                }

                break;
            }
            case DrawingKind.LongPosition:
            case DrawingKind.ShortPosition:
                RenderPosition(canvas, layout, series, theme, decimals, font);
                break;
        }

        if (Selected)
        {
            using var h = new SKPaint { IsAntialias = true, Color = SKColors.White, Style = SKPaintStyle.Fill };
            using var hb = new SKPaint { IsAntialias = true, Color = theme.Selection, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
            foreach (var (hx, hy) in HandlePositions(layout, series))
            {
                canvas.DrawCircle(hx, hy, 4.5f, h);
                canvas.DrawCircle(hx, hy, 4.5f, hb);
            }
        }

        if (Locked && Selected)
        {
            var (lx, ly) = HandlePositions(layout, series).FirstOrDefault();
            DrawLock(canvas, lx + 9, ly - 14, theme.Selection);
        }
    }

    private void RenderPosition(SKCanvas canvas, ChartLayout layout, CandleSeries series, ChartTheme theme, int decimals, SKFont font)
    {
        if (Points.Count < 3) return;
        var main = layout.Main;
        bool isLong = Kind == DrawingKind.LongPosition;
        double entry = Points[0].Price, target = Points[1].Price, stop = Points[2].Price;
        float x0 = XOf(layout, series, Points[0].Time), x1 = XOf(layout, series, Points[1].Time);
        if (x1 < x0 + 8) x1 = x0 + 8;
        float yE = main.Y(entry), yT = main.Y(target), yS = main.Y(stop);
        var profit = new SKRect(x0, Math.Min(yE, yT), x1, Math.Max(yE, yT));
        var risk = new SKRect(x0, Math.Min(yE, yS), x1, Math.Max(yE, yS));
        var (status, outcome) = PositionOutcome(series);
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        fill.Color = theme.UpCandle.WithAlpha(outcome == 1 ? (byte)70 : (byte)38);
        canvas.DrawRect(profit, fill);
        fill.Color = theme.DownCandle.WithAlpha(outcome == -1 ? (byte)70 : (byte)38);
        canvas.DrawRect(risk, fill);
        using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Selected ? 1.8f : 1.2f };
        line.Color = theme.UpCandle;
        canvas.DrawLine(x0, yT, x1, yT, line);
        line.Color = theme.DownCandle;
        canvas.DrawLine(x0, yS, x1, yS, line);
        line.Color = new SKColor(200, 205, 215);
        canvas.DrawLine(x0, yE, x1, yE, line);

        var inst = series.Instrument;
        double tick = inst.TickSize > 0 ? (double)inst.TickSize : 0;
        double reward = Math.Abs(target - entry), riskAmt = Math.Abs(entry - stop);
        double rr = riskAmt > 0 ? reward / riskAmt : 0;
        var ic = CultureInfo.InvariantCulture;
        string Ticks(double d) => tick > 0 ? $" · {Math.Round(d / tick).ToString("0", ic)} ticks" : "";
        string Pct(double d) => entry != 0 ? $" · {(d / entry * 100).ToString("0.00", ic)}%" : "";
        string Money(double d) => Quantity > 0 ? $" · {((double)Quantity * d).ToString("0.00", ic)}" : "";
        // las etiquetas se centran en la parte visible de la caja
        var plotRect = main.Rect;
        float vx0 = Math.Max(x0, plotRect.Left), vx1 = Math.Min(x1, plotRect.Right);
        float cx = vx1 > vx0 ? (vx0 + vx1) / 2 : (x0 + x1) / 2;
        string tl = $"Objetivo {Format(target, decimals)}  +{Format(reward, decimals)}{Pct(reward)}{Ticks(reward)}{Money(reward)}";
        string sl = $"Stop {Format(stop, decimals)}  −{Format(riskAmt, decimals)}{Pct(riskAmt)}{Ticks(riskAmt)}{Money(riskAmt)}";
        float boxH = font.Spacing + 8;
        // la etiqueta del objetivo va del lado de afuera (arriba en la larga, abajo en la corta)
        DrawBox(canvas, font, cx, isLong ? yT - boxH - 4 : yT + 4, theme.UpCandle, tl);
        DrawBox(canvas, font, cx, isLong ? yS + 4 : yS - boxH - 4, theme.DownCandle, sl);
        string mid = $"{(isLong ? "Larga" : "Corta")} {Format(entry, decimals)} · R:R {rr.ToString("0.00", ic)}" + (Quantity > 0 ? $" · Cant {Quantity.ToString(ic)}" : "") + (status.Length > 0 ? $" · {status}" : "");
        var midCol = outcome == 1 ? theme.UpCandle : outcome == -1 ? theme.DownCandle : SKColors.White;
        DrawBox(canvas, font, cx, yE - boxH / 2, midCol, mid);
    }

    /// <summary>Cajita oscura con una o más líneas de texto, centrada en <paramref name="cx"/>.</summary>
    private static void DrawBox(SKCanvas canvas, SKFont font, float cx, float top, SKColor color, params string[] lines)
    {
        float w = lines.Max(l => RenderContext.MeasureWidth(font, l)) + 12;
        float h = font.Spacing * lines.Length + 8;
        float bx = cx - w / 2;
        using var box = new SKPaint { Color = new SKColor(10, 14, 24, 225), IsAntialias = true };
        canvas.DrawRoundRect(new SKRect(bx, top, bx + w, top + h), 4, 4, box);
        using var text = new SKPaint { IsAntialias = true, Color = color };
        for (int i = 0; i < lines.Length; i++)
            canvas.DrawUiText(lines[i], bx + 6, top + 4 + font.Spacing * i - font.Metrics.Ascent, font, text);
    }

    private static void DrawBell(SKCanvas canvas, float x, float y, SKColor color)
    {
        using var p = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.3f, Color = color };
        using var path = new SKPath();
        path.MoveTo(x - 4, y + 3);
        path.LineTo(x - 4, y - 1);
        path.QuadTo(x - 4, y - 5, x, y - 5);
        path.QuadTo(x + 4, y - 5, x + 4, y - 1);
        path.LineTo(x + 4, y + 3);
        path.Close();
        canvas.DrawPath(path, p);
        canvas.DrawLine(x - 5.5f, y + 3, x + 5.5f, y + 3, p);
        canvas.DrawCircle(x, y + 5, 1, p);
    }

    private static void DrawLock(SKCanvas canvas, float x, float y, SKColor color)
    {
        using var p = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f, Color = color };
        canvas.DrawRoundRect(new SKRect(x - 4, y - 1, x + 4, y + 5), 1.5f, 1.5f, p);
        canvas.DrawArc(new SKRect(x - 2.6f, y - 5, x + 2.6f, y + 1), 180, 180, false, p);
    }

    /// <summary>Extiende un segmento hasta los bordes pedidos del rectángulo.</summary>
    private static (float, float, float, float) Extend(float x0, float y0, float x1, float y1, SKRect r, bool left, bool right)
    {
        if (Math.Abs(x1 - x0) < 0.01f)
        {
            if (!left && !right) return (x0, y0, x1, y1);
            return (x0, left || y1 < y0 ? r.Top : y0, x1, right || y1 > y0 ? r.Bottom : y1);
        }

        float slope = (y1 - y0) / (x1 - x0);
        float ax = x0, ay = y0, bx = x1, by = y1;
        bool forward = x1 >= x0;
        if (right)
        {
            bx = forward ? r.Right : r.Left;
            by = y0 + slope * (bx - x0);
        }

        if (left)
        {
            ax = forward ? r.Left : r.Right;
            ay = y0 + slope * (ax - x0);
        }

        return (ax, ay, bx, by);
    }

    public IEnumerable<(float X, float Y)> HandlePositions(ChartLayout layout, CandleSeries series)
    {
        var main = layout.Main;
        if (IsPosition && Points.Count >= 3)
        {
            float x0 = XOf(layout, series, Points[0].Time), x1 = XOf(layout, series, Points[1].Time);
            if (x1 < x0 + 8) x1 = x0 + 8;
            yield return (x0, main.Y(Points[0].Price));
            yield return (x0, main.Y(Points[1].Price));
            yield return (x0, main.Y(Points[2].Price));
            yield return (x1, main.Y(Points[0].Price));
            yield break;
        }

        for (int i = 0; i < Points.Count; i++)
        {
            float x = Kind == DrawingKind.HorizontalLine ? (main.Rect.Left + main.Rect.Right) / 2 : XOf(layout, series, Points[i].Time);
            yield return (x, main.Y(Points[i].Price));
        }
    }

    /// <summary>Prueba si un punto toca el objeto. Devuelve el índice del manejador (-1 = el cuerpo).</summary>
    public bool HitTest(ChartLayout layout, CandleSeries series, float x, float y, out int handle)
    {
        handle = -1;
        if (Points.Count == 0 || !IsVisibleOn(series.Period)) return false;
        const float tol = 6;
        int k = 0;
        foreach (var (hx, hy) in HandlePositions(layout, series))
        {
            if (Math.Abs(hx - x) <= tol && Math.Abs(hy - y) <= tol)
            {
                handle = k;
                return true;
            }

            k++;
        }

        var main = layout.Main;
        var plot = main.Rect;
        float x0 = XOf(layout, series, Points[0].Time), y0 = main.Y(Points[0].Price);
        float x1 = x0, y1 = y0;
        if (Points.Count > 1)
        {
            x1 = XOf(layout, series, Points[1].Time);
            y1 = main.Y(Points[1].Price);
        }

        switch (Kind)
        {
            case DrawingKind.HorizontalLine:
                return Math.Abs(y - y0) <= tol;
            case DrawingKind.HorizontalRay:
                return Math.Abs(y - y0) <= tol && (ExtendLeft || x >= x0 - tol);
            case DrawingKind.VerticalLine:
                return Math.Abs(x - x0) <= tol;
            case DrawingKind.Rectangle:
            case DrawingKind.Ruler:
            case DrawingKind.FibRetracement:
            {
                var r = new SKRect(Math.Min(x0, x1) - tol, Math.Min(y0, y1) - tol, Math.Max(x0, x1) + tol, Math.Max(y0, y1) + tol);
                if (Kind == DrawingKind.FibRetracement || ExtendRight) r.Right = plot.Right;
                if (ExtendLeft) r.Left = plot.Left;
                return r.Contains(x, y);
            }
            case DrawingKind.LongPosition:
            case DrawingKind.ShortPosition:
            {
                if (Points.Count < 3) return false;
                float xe = Math.Max(x1, x0 + 8);
                float top = Math.Min(main.Y(Points[1].Price), main.Y(Points[2].Price));
                float bottom = Math.Max(main.Y(Points[1].Price), main.Y(Points[2].Price));
                return new SKRect(x0 - tol, top - tol, xe + tol, bottom + tol).Contains(x, y);
            }
            case DrawingKind.Text:
                return Math.Abs(x - x0 - 20) <= 30 && Math.Abs(y - y0 + 6) <= 10 + FontSize / 2;
            default:
            {
                bool left = Kind == DrawingKind.ExtendedLine || (Kind == DrawingKind.TrendLine && ExtendLeft);
                bool right = Kind is DrawingKind.Ray or DrawingKind.ExtendedLine || (Kind == DrawingKind.TrendLine && ExtendRight);
                var (ax, ay, bx, by) = Extend(x0, y0, x1, y1, plot, left, right);
                return DistanceToSegment(x, y, ax, ay, bx, by, false) <= tol;
            }
        }
    }

    private static float DistanceToSegment(float px, float py, float x0, float y0, float x1, float y1, bool infinite)
    {
        float dx = x1 - x0, dy = y1 - y0;
        float len2 = dx * dx + dy * dy;
        if (len2 < 0.0001f) return MathF.Sqrt((px - x0) * (px - x0) + (py - y0) * (py - y0));
        float t = ((px - x0) * dx + (py - y0) * dy) / len2;
        if (!infinite) t = Math.Clamp(t, 0, 1);
        float cx = x0 + t * dx, cy = y0 + t * dy;
        return MathF.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
    }

    private static string Format(double v, int decimals) => v.ToString("F" + decimals, CultureInfo.InvariantCulture);

    private static string FormatSpan(TimeSpan s)
    {
        s = s.Duration();
        if (s.TotalDays >= 1) return $"{(int)s.TotalDays}d {s.Hours}h";
        if (s.TotalHours >= 1) return $"{(int)s.TotalHours}h {s.Minutes}m";
        if (s.TotalMinutes >= 1) return $"{(int)s.TotalMinutes}m {s.Seconds}s";
        return $"{s.Seconds}s";
    }
}
