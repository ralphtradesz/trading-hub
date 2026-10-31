using System.Globalization;
using SkiaSharp;
using TradingHub.Data;
using TradingHub.Indicators;
using TradingHub.Market;
using TradingHub.Rendering.Context;
using TradingHub.Rendering;

namespace TradingHub.Charting.Dom;

/// <summary>Cinta de operaciones (Time &amp; Sales) con agregación por orden y filtro de tamaño.</summary>
public sealed class TapeModel
{
    public TapeModel(InstrumentData data) => Data = data;

    public InstrumentData Data { get; }

    /// <summary>Sumar las ejecuciones de una misma orden agresiva (mismo milisegundo y lado).</summary>
    public bool Aggregate { get; set; } = true;

    /// <summary>Volumen mínimo para mostrar.</summary>
    public decimal MinVolume { get; set; }

    /// <summary>A partir de este volumen la fila se resalta.</summary>
    public decimal HighlightVolume { get; set; }

    public TimeZoneInfo TimeZone { get; set; } = TimeZoneInfo.Local;

    /// <summary>
    /// Desplazada hacia atrás: índice de la operación más nueva que se muestra (null = en vivo, arriba la última).
    /// Mientras está desplazada no se mueve aunque lleguen operaciones nuevas.
    /// </summary>
    public int? Anchor { get; set; }

    /// <summary>Operaciones nuevas desde que se desplazó (para el aviso de arriba).</summary>
    public int NewSinceAnchor => Anchor is { } a ? Math.Max(0, Data.Trades.Count - 1 - a) : 0;
}

public sealed class TapeRenderer
{
    private readonly SKPaint _fill = new() { IsAntialias = false };
    private readonly SKPaint _text = new() { IsAntialias = true };
    private const float RowH = 16, HeaderH = 20;

    private ChartTheme? _theme;

    /// <summary>Colores (por defecto, la base del modo oscuro/claro de la app).</summary>
    public ChartTheme Theme
    {
        get => _theme ?? ChartTheme.Base;
        set => _theme = value;
    }

    private readonly record struct Row(DateTime Time, decimal Price, decimal Volume, TradeSide Side, int Count);

    public void Render(SKCanvas canvas, TapeModel tape, float width, float height)
    {
        var t = Theme;
        var inst = tape.Data.Instrument;
        _fill.Color = t.BackgroundTop;
        canvas.DrawRect(0, 0, width, height, _fill);
        _fill.Color = t.AxisBackground;
        canvas.DrawRect(0, 0, width, HeaderH, _fill);
        var font = RenderContext.GetFont(t.MonoFontFamily, 11, false, false);
        var bold = RenderContext.GetFont(t.MonoFontFamily, 11, true, false);
        var head = RenderContext.GetFont(t.FontFamily, 10, false, false);
        float c1 = 6, c2 = width * 0.36f, c3 = width - 6;
        _text.Color = t.TextDim;
        canvas.DrawUiText("Hora", c1, 14, head, _text);
        canvas.DrawUiText("Precio", c2, 14, head, _text);
        canvas.DrawUiText("Volumen", c3 - RenderContext.MeasureWidth(head, "Volumen"), 14, head, _text);

        int maxRows = (int)((height - HeaderH) / RowH) + 1;
        if (tape.Anchor is { } anchor && anchor >= tape.Data.Trades.Count - 1) tape.Anchor = null;
        var rows = Collect(tape, maxRows);
        decimal hl = tape.HighlightVolume > 0 ? tape.HighlightVolume : AutoHighlight(rows);
        int dec = Rendering.FootprintRenderer.ChooseDecimals(rows.Count > 0 ? rows.Max(r => r.Volume) : 0, inst.VolumeDecimals);
        float baseOff = RowH / 2 - font.Metrics.Ascent / 2 - font.Metrics.Descent / 2;

        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            float y = HeaderH + i * RowH;
            bool buy = r.Side == TradeSide.Buy;
            bool big = r.Volume >= hl && hl > 0;
            var col = buy ? t.Ask : t.Bid;
            _fill.Color = big ? col.WithAlpha(150) : col.WithAlpha(i % 2 == 0 ? (byte)22 : (byte)14);
            canvas.DrawRect(0, y, width, RowH, _fill);
            var f = big ? bold : font;
            _text.Color = big ? SKColors.White : col;
            var local = TimeZoneInfo.ConvertTimeFromUtc(r.Time, tape.TimeZone);
            canvas.DrawUiText(local.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture), c1, y + baseOff, f, _text);
            canvas.DrawUiText(inst.FormatPrice(r.Price), c2, y + baseOff, f, _text);
            string v = VolumeFormat.Compact(r.Volume, dec);
            canvas.DrawUiText(v, c3 - RenderContext.MeasureWidth(f, v), y + baseOff, f, _text);
        }

        // desplazada: el encabezado avisa y un clic ahí vuelve a lo último
        if (tape.Anchor != null)
        {
            _fill.Color = t.AxisBackground;
            canvas.DrawRect(0, 0, width, HeaderH, _fill);
            string msg = tape.NewSinceAnchor > 0 ? $"▲ {tape.NewSinceAnchor:N0} nuevas · clic para volver" : "▲ clic para volver a lo último";
            _text.Color = t.LastPriceUp;
            canvas.DrawUiText(msg, width / 2 - RenderContext.MeasureWidth(head, msg) / 2, 14, head, _text);
        }
    }

    /// <summary>Alto del encabezado (un clic ahí vuelve a lo último).</summary>
    public static float HeaderHeight => HeaderH;

    /// <summary>Desplaza la cinta: filas positivas = hacia atrás (más viejas), negativas = hacia lo último.</summary>
    public static void Scroll(TapeModel tape, int rows)
    {
        var s = tape.Data.Trades;
        if (s.Count == 0 || rows == 0) return;
        int i = tape.Anchor ?? s.Count - 1;
        if (rows > 0)
        {
            for (int k = 0; k < rows && i > 0; k++)
            {
                int j = RowStart(tape, i) - 1; // la operación más nueva de la fila siguiente (más vieja)
                if (j < 0) break;
                i = j;
            }

            tape.Anchor = i;
        }
        else
        {
            for (int k = 0; k < -rows && i < s.Count - 1; k++) i = RowEndForward(tape, i + 1);
            tape.Anchor = i >= s.Count - 1 ? null : i;
        }
    }

    /// <summary>Primera operación (la más vieja) de la fila cuya operación más nueva es <paramref name="i"/>.</summary>
    private static int RowStart(TapeModel tape, int i)
    {
        if (!tape.Aggregate) return i;
        var s = tape.Data.Trades;
        long ms = s.GetTimeTicks(i) / TimeSpan.TicksPerMillisecond;
        var side = s.GetSide(i);
        int j = i;
        while (j > 0 && s.GetSide(j - 1) == side && s.GetTimeTicks(j - 1) / TimeSpan.TicksPerMillisecond == ms) j--;
        return j;
    }

    /// <summary>Operación más nueva de la fila que empieza en <paramref name="i"/> (yendo hacia lo último).</summary>
    private static int RowEndForward(TapeModel tape, int i)
    {
        var s = tape.Data.Trades;
        if (!tape.Aggregate) return i;
        long ms = s.GetTimeTicks(i) / TimeSpan.TicksPerMillisecond;
        var side = s.GetSide(i);
        int j = i;
        while (j + 1 < s.Count && s.GetSide(j + 1) == side && s.GetTimeTicks(j + 1) / TimeSpan.TicksPerMillisecond == ms) j++;
        return j;
    }

    private static decimal AutoHighlight(List<Row> rows)
    {
        if (rows.Count < 20) return 0;
        var vols = rows.Select(r => r.Volume).OrderBy(v => v).ToArray();
        return vols[(int)(vols.Length * 0.95)];
    }

    private static List<Row> Collect(TapeModel tape, int max)
    {
        var res = new List<Row>(max);
        var s = tape.Data.Trades;
        int i = Math.Min(tape.Anchor ?? s.Count - 1, s.Count - 1);
        int scanned = 0;
        while (i >= 0 && res.Count < max && scanned < 200_000)
        {
            long t = s.GetTimeTicks(i);
            var side = s.GetSide(i);
            decimal vol = s.GetVolume(i);
            decimal price = s.GetPrice(i);
            int count = 1;
            int j = i - 1;
            if (tape.Aggregate)
            {
                long ms = t / TimeSpan.TicksPerMillisecond;
                while (j >= 0 && s.GetSide(j) == side && s.GetTimeTicks(j) / TimeSpan.TicksPerMillisecond == ms)
                {
                    vol += s.GetVolume(j);
                    count++;
                    j--;
                }
            }

            scanned += count;
            if (vol >= tape.MinVolume) res.Add(new Row(new DateTime(t, DateTimeKind.Utc), price, vol, side, count));
            i = j;
        }

        return res;
    }
}
