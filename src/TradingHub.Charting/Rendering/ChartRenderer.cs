using System.Globalization;
using SkiaSharp;
using TradingHub.Charting.Drawings;
using TradingHub.Common.Logging;
using TradingHub.Data;
using TradingHub.Indicators;
using TradingHub.Rendering.Context;
using TradingHub.Trading;
using Rectangle = System.Drawing.Rectangle;
using TradingHub.Rendering;

namespace TradingHub.Charting.Rendering;

/// <summary>Estado de interacción que afecta al dibujo (mouse, objeto en construcción, mensajes).</summary>
public sealed class ChartRenderState
{
    public float? MouseX { get; set; }
    public float? MouseY { get; set; }
    public bool Crosshair { get; set; } = true;

    /// <summary>Cruz de otro gráfico vinculado: instante y (si es el mismo instrumento) precio.</summary>
    public DateTime? LinkedTime { get; set; }
    public double? LinkedPrice { get; set; }
    public ChartDrawing? Preview { get; set; }
    public string? Message { get; set; }

    /// <summary>Orden que se está arrastrando y el precio donde quedaría.</summary>
    public Order? DragOrder { get; set; }
    public decimal DragPrice { get; set; }

    /// <summary>Stop/objetivo que se está arrastrando desde la posición o una orden pendiente.</summary>
    public BracketDrag? BracketDrag { get; set; }

    /// <summary>Zonas que empiezan un arrastre de stop/objetivo (las arma el dibujo de cada cuadro).</summary>
    public List<BracketHandle> Handles { get; } = new();

    /// <summary>Botón para volver a lo último (abajo a la derecha) si el gráfico está corrido hacia atrás.</summary>
    public SKRect? GoLatestButton { get; set; }

    /// <summary>Zonas clicables de la leyenda: ocultar, configurar o quitar indicadores y plegar la lista.</summary>
    public List<LegendHit> Legend { get; } = new();

    /// <summary>La ✕ de la etiqueta de la operación resaltada (saca el resaltado).</summary>
    public SKRect? FocusTradeClose { get; set; }
}

public enum LegendAction
{
    Name,
    ToggleVisible,
    Settings,
    Remove,
    Collapse,
}

/// <summary>Zona clicable de la leyenda del gráfico.</summary>
public sealed record LegendHit(SKRect Rect, IndicatorSlot? Slot, LegendAction Action, StrategyOverlay? Strategy = null);

/// <summary>
/// Arrastre de stop/objetivo. Entry = null: desde la posición abierta. Pending + Index: mueve un stop
/// (−1) u objetivo adjunto a una orden pendiente. Role = None: lo decide el lado donde se suelta.
/// </summary>
public sealed record BracketDrag(Order? Entry, BracketRole Role, PendingBracket? Pending = null, int Index = -1);

/// <summary>Zona del gráfico que inicia un arrastre (botones SL/TP o la línea de la posición).</summary>
public sealed record BracketHandle(SKRect Rect, BracketDrag Drag, bool IsButton);

/// <summary>Dibuja un gráfico completo sobre un SKCanvas (sin depender de la UI).</summary>
public sealed class ChartRenderer
{
    private readonly FootprintRenderer _footprint = new();
    private readonly CandleRenderer _candles = new();
    private readonly SeriesRenderer _series = new();
    private readonly SKPaint _fill = new() { IsAntialias = false, Style = SKPaintStyle.Fill };
    private readonly SKPaint _stroke = new() { IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
    private readonly SKPaint _text = new() { IsAntialias = true };

    public ChartLayout Render(SKCanvas canvas, ChartModel model, float width, float height, ChartRenderState? state = null)
    {
        state ??= new ChartRenderState();
        state.Legend.Clear();
        state.GoLatestButton = null;
        state.FocusTradeClose = null;
        var t = model.Theme;
        var layout = ComputeLayout(model, width, height);
        model.LastLayout = layout;
        model.ChartInfo.Region = new Rectangle(0, 0, (int)width, (int)height);

        // fondo
        using (var bg = new SKPaint())
        {
            bg.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, height), new[] { t.BackgroundTop, t.BackgroundBottom }, SKShaderTileMode.Clamp);
            canvas.DrawRect(0, 0, width, height, bg);
        }

        var timeTicks = ComputeTimeTicks(model, layout);
        var axisLabels = new List<AxisLabel>();

        foreach (var panel in layout.Panels)
        {
            canvas.Save();
            canvas.ClipRect(panel.Rect);
            if (model.Settings.ShowGrid) DrawGrid(canvas, model, layout, panel, timeTicks);
            if (panel.IsPrice) DrawMainPanel(canvas, model, layout, panel, axisLabels);
            else DrawSubPanel(canvas, model, layout, panel, axisLabels);
            canvas.Restore();

            // renders personalizados de indicadores (pueden pintar sobre el eje)
            DrawCustomRenders(canvas, model, layout, panel, width, height, above: true);
        }

        // separadores de paneles
        _stroke.Color = t.PanelSeparator;
        _stroke.StrokeWidth = 1;
        foreach (var p in layout.Panels.Skip(1))
            canvas.DrawLine(0, MathF.Floor(p.Rect.Top) + 0.5f, width, MathF.Floor(p.Rect.Top) + 0.5f, _stroke);

        DrawPriceAxis(canvas, model, layout, axisLabels);
        if (model.Strategies.Count > 0) DrawStrategySignals(canvas, model, layout);
        if (model.Settings.ShowTradeMarks) DrawTradeMarks(canvas, model, layout);
        if (model.FocusTrade != null) DrawFocusTrade(canvas, model, layout, state);
        DrawTrading(canvas, model, layout, state);
        DrawTimeAxis(canvas, model, layout, timeTicks);
        DrawPanelTitles(canvas, model, layout, state);
        if (model.Settings.ShowInfoPanel) DrawInfo(canvas, model, layout, state);
        if (state.Crosshair) DrawCrosshair(canvas, model, layout, state);
        DrawOverlay(canvas, model, layout, state);
        DrawGoLatest(canvas, model, layout, state);
        model.ClearDirty();
        return layout;
    }

    // =================================================================== distribución

    public static ChartLayout ComputeLayout(ChartModel model, float width, float height)
    {
        var vp = model.Viewport;
        var series = model.Series;
        float axisW = ChartLayout.PriceAxisWidth, timeH = ChartLayout.TimeAxisHeight;
        var plot = new SKRect(0, 0, Math.Max(50, width - axisW), Math.Max(50, height - timeH));

        // limita el desplazamiento
        // (con barras muy anchas o un gráfico angosto entran menos de 3 y el mínimo pasaba al máximo)
        double visibleBars = plot.Width / vp.BarWidth;
        double maxScroll = Math.Max(0, series.Count + vp.RightMarginBars - 3);
        vp.ScrollBars = Math.Clamp(vp.ScrollBars, Math.Min(-(visibleBars - 3), maxScroll), maxScroll);

        var layout = new ChartLayout
        {
            Bounds = new SKRect(0, 0, width, height),
            Plot = plot,
            PriceAxis = new SKRect(plot.Right, 0, width, plot.Bottom),
            TimeAxis = new SKRect(0, plot.Bottom, plot.Right, height),
            Count = series.Count,
            BarWidth = vp.BarWidth,
            BarSpacing = vp.BarSpacing,
            ScrollBars = vp.ScrollBars,
            RightMarginBars = vp.RightMarginBars,
        };

        layout.FirstVisible = Math.Max(0, layout.BarAt(plot.Left + (float)(vp.BarWidth * 0.35)));
        layout.LastVisible = Math.Min(series.Count - 1, layout.BarAt(plot.Right));
        if (layout.LastVisible < layout.FirstVisible) layout.LastVisible = layout.FirstVisible - 1;

        // alturas de paneles
        double totalRatio = model.SubPanels.Sum(p => p.HeightRatio);
        double scale = totalRatio > 0.65 ? 0.65 / totalRatio : 1;
        float y = plot.Top;
        float mainH = (float)(plot.Height * (1 - totalRatio * scale));
        var main = new PanelLayout(layout, new SKRect(plot.Left, y, plot.Right, y + mainH), true)
        {
            RowSize = series.RowSize,
            RowCenterOf = p => series.RowCenter(series.PriceToRow(p)),
        };
        layout.Panels.Add(main);
        y += mainH;
        foreach (var sp in model.SubPanels)
        {
            float h = (float)(plot.Height * sp.HeightRatio * scale);
            layout.Panels.Add(new PanelLayout(layout, new SKRect(plot.Left, y, plot.Right, y + h), false)
            {
                SubPanel = sp,
                RowSize = series.RowSize,
                RowCenterOf = p => series.RowCenter(series.PriceToRow(p)),
            });
            y += h;
        }

        ComputeMainRange(model, layout, main);
        foreach (var p in layout.Panels.Skip(1)) ComputeSubRange(model, layout, p);
        return layout;
    }

    private static void ComputeMainRange(ChartModel model, ChartLayout layout, PanelLayout main)
    {
        var vp = model.Viewport;
        var series = model.Series;
        bool footprint = model.Settings.Style == ChartStyle.Footprint;
        double row = (double)series.RowSize;

        if (!vp.AutoScale && vp.PriceTop > vp.PriceBottom)
        {
            main.Top = vp.PriceTop;
            main.Bottom = vp.PriceBottom;
            return;
        }

        double min = double.MaxValue, max = double.MinValue;
        for (int i = Math.Max(0, layout.FirstVisible); i <= Math.Min(layout.LastVisible, series.Count - 1); i++)
        {
            var c = series[i];
            if (c.Trades == 0) continue;
            double hi = (double)series.TicksToPrice(c.HighTicks), lo = (double)series.TicksToPrice(c.LowTicks);
            if (footprint)
            {
                hi = (double)series.RowCenter(series.RowOf(c.HighTicks)) + row / 2;
                lo = (double)series.RowCenter(series.RowOf(c.LowTicks)) - row / 2;
            }

            if (hi > max) max = hi;
            if (lo < min) min = lo;
        }

        var overlay = model.Indicators.Where(s => s.Panel == null && s.Visible);
        var (imin, imax) = SeriesRenderer.ValueRange(overlay, layout.FirstVisible, layout.LastVisible, true);
        if (min != double.MaxValue && imin != double.MaxValue)
        {
            // los indicadores amplían la escala solo si no la deforman demasiado
            double span = Math.Max(max - min, row * 4);
            if (imin > min - span * 1.5) min = Math.Min(min, imin);
            if (imax < max + span * 1.5) max = Math.Max(max, imax);
        }

        if (min == double.MaxValue)
        {
            double last = (double)model.Data.LastPrice;
            if (last == 0) last = 100;
            min = last - row * 20;
            max = last + row * 20;
        }

        double rangeV = Math.Max(max - min, row * 6);
        double mid = (max + min) / 2;
        double margin = rangeV * 0.08;
        // el modo combinado escribe el delta arriba y el volumen abajo de cada vela: deja lugar
        if (footprint && model.Settings.Layout.ShowTotals && main.Rect.Height > 100)
            margin += rangeV * 26 / main.Rect.Height;
        main.Top = mid + rangeV / 2 + margin;
        main.Bottom = mid - rangeV / 2 - margin;
        vp.PriceTop = main.Top;
        vp.PriceBottom = main.Bottom;
    }

    private static void ComputeSubRange(ChartModel model, ChartLayout layout, PanelLayout panel)
    {
        var slots = panel.SubPanel!.Slots;
        var (min, max) = SeriesRenderer.ValueRange(slots, layout.FirstVisible, layout.LastVisible, false);
        if (min == double.MaxValue)
        {
            min = 0;
            max = 1;
        }

        if (max - min < 1e-12)
        {
            double d = Math.Abs(max) > 0 ? Math.Abs(max) * 0.1 : 1;
            min -= d;
            max += d;
        }

        double margin = (max - min) * 0.1;
        panel.Top = max + margin;
        panel.Bottom = min - margin;
    }

    // =================================================================== paneles

    private void DrawMainPanel(SKCanvas canvas, ChartModel model, ChartLayout layout, PanelLayout main, List<AxisLabel> axisLabels)
    {
        var slots = model.Indicators.Where(s => s.Panel == null).ToList();
        if (model.Settings.ShowSessionLines) DrawSessionLines(canvas, model, layout, main);

        _series.RenderBands(canvas, layout, main, slots);
        DrawCustomRendersCore(canvas, model, layout, main, slots, above: false);

        var paint = _series.CollectPaintBars(model, layout);
        var style = model.Settings.Style;

        // si un indicador dibuja sus propias velas (Heikin Ashi…), reemplazan a las del gráfico
        if (!SeriesRenderer.ChartReplacedByIndicator(model, layout))
        {
            var hidden = new HashSet<int>(paint.Where(kv => kv.Value.Alpha == 0).Select(kv => kv.Key));
            if (style == ChartStyle.Footprint && _footprint.CanShowClusters(model, main, layout.BarWidth))
                _footprint.Render(canvas, model, layout, main, _candles, hidden);
            else
                _candles.Render(canvas, model, layout, main, style == ChartStyle.Footprint ? ChartStyle.Candles : style, paint);
        }

        _series.RenderSeries(canvas, model, layout, main, slots, axisLabels);
        _series.RenderDrawingObjects(canvas, model, layout, main, slots);

        int decimals = Instrument_Decimals(model);
        foreach (var d in model.Drawings) d.Render(canvas, layout, model.Series, model.Theme, decimals);

        if (model.Settings.ShowLastPriceLine && model.Series.Count > 0)
        {
            var last = model.Series[^1];
            float y = MathF.Floor(main.Y(model.Series.TicksToPrice(last.CloseTicks))) + 0.5f;
            _stroke.Color = model.Theme.LastPriceLine;
            _stroke.PathEffect = SKPathEffect.CreateDash(new[] { 3f, 3f }, 0);
            canvas.DrawLine(main.Rect.Left, y, main.Rect.Right, y, _stroke);
            _stroke.PathEffect = null;
        }
    }

    private static int Instrument_Decimals(ChartModel model) => Market.Instrument.DecimalPlaces(model.Series.RowSize);

    private void DrawSubPanel(SKCanvas canvas, ChartModel model, ChartLayout layout, PanelLayout panel, List<AxisLabel> axisLabels)
    {
        var slots = panel.SubPanel!.Slots;
        _series.RenderBands(canvas, layout, panel, slots);
        DrawCustomRendersCore(canvas, model, layout, panel, slots, above: false);
        _series.RenderSeries(canvas, model, layout, panel, slots, axisLabels);
        _series.RenderDrawingObjects(canvas, model, layout, panel, slots);
    }

    private void DrawCustomRenders(SKCanvas canvas, ChartModel model, ChartLayout layout, PanelLayout panel, float width, float height, bool above)
    {
        var slots = panel.IsPrice ? model.Indicators.Where(s => s.Panel == null).ToList() : panel.SubPanel!.Slots;
        DrawCustomRendersCore(canvas, model, layout, panel, slots, above);
    }

    private void DrawCustomRendersCore(SKCanvas canvas, ChartModel model, ChartLayout layout, PanelLayout panel, IList<IndicatorSlot> slots, bool above)
    {
        foreach (var slot in slots)
        {
            var ind = slot.Indicator;
            if (!slot.Visible || !ind.EnableCustomDrawing || ind.SubscribedLayouts == DrawingLayouts.None) continue;
            // en el panel de precio, los que no piden "encima del precio" van antes de las velas
            bool wantAbove = !panel.IsPrice || ind.DrawAbovePrice;
            if (wantAbove != above) continue;

            canvas.Save();
            canvas.ClipRect(new SKRect(panel.Rect.Left, panel.Rect.Top, layout.PriceAxis.Right, panel.Rect.Bottom));
            var ctx = new RenderContext(canvas, new Rectangle(0, 0, (int)layout.Bounds.Width, (int)layout.Bounds.Height));
            try
            {
                foreach (var l in new[] { DrawingLayouts.Historical, DrawingLayouts.LatestBar, DrawingLayouts.Final })
                {
                    if ((ind.SubscribedLayouts & l) != 0) ind.HostRender(ctx, l);
                }

                ctx.ResetClip();
            }
            catch (Exception ex)
            {
                if (slot.Error == null) this.LogError($"Error dibujando {ind.Name}: {ex.Message}", ex);
                slot.Error = ex.Message;
            }

            canvas.Restore();
        }
    }

    private void DrawSessionLines(SKCanvas canvas, ChartModel model, ChartLayout layout, PanelLayout main)
    {
        var series = model.Series;
        _stroke.Color = model.Theme.NewSession;
        _stroke.PathEffect = SKPathEffect.CreateDash(new[] { 4f, 4f }, 0);
        for (int i = Math.Max(1, layout.FirstVisible); i <= layout.LastVisible; i++)
        {
            if (!series.Session.IsNewSession(series[i - 1].Time, series[i].Time)) continue;
            float x = MathF.Floor(layout.LeftX(i)) + 0.5f;
            canvas.DrawLine(x, layout.Plot.Top, x, layout.Plot.Bottom, _stroke);
        }

        _stroke.PathEffect = null;
    }

    // =================================================================== grilla y ejes

    private static double NiceStep(double raw)
    {
        if (raw <= 0) return 1;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        foreach (var m in new[] { 1, 2, 2.5, 5, 10 })
            if (mag * m >= raw) return mag * m;
        return mag * 10;
    }

    private static double PriceStepFor(PanelLayout panel, double minPx)
    {
        double perPx = panel.Range / Math.Max(1, panel.Rect.Height);
        double raw = perPx * minPx;
        if (!panel.IsPrice) return NiceStep(raw);
        double row = (double)panel.RowSize;
        double step = NiceStep(raw);
        // múltiplo del tamaño de fila
        if (row > 0) step = Math.Max(row, Math.Ceiling(step / row) * row);
        return step;
    }

    private void DrawGrid(SKCanvas canvas, ChartModel model, ChartLayout layout, PanelLayout panel, List<(float X, string Text, bool Major)> timeTicks)
    {
        _stroke.Color = model.Theme.Grid;
        _stroke.StrokeWidth = 1;
        double step = PriceStepFor(panel, 52);
        double start = Math.Ceiling(panel.Bottom / step) * step;
        for (double v = start; v <= panel.Top; v += step)
        {
            float y = MathF.Floor(panel.Y(v)) + 0.5f;
            canvas.DrawLine(panel.Rect.Left, y, panel.Rect.Right, y, _stroke);
        }

        foreach (var (x, _, _) in timeTicks)
        {
            float xx = MathF.Floor(x) + 0.5f;
            canvas.DrawLine(xx, panel.Rect.Top, xx, panel.Rect.Bottom, _stroke);
        }
    }

    private void DrawPriceAxis(SKCanvas canvas, ChartModel model, ChartLayout layout, List<AxisLabel> extraLabels)
    {
        var t = model.Theme;
        var axis = layout.PriceAxis;
        _fill.Color = t.AxisBackground;
        canvas.DrawRect(new SKRect(axis.Left, 0, layout.Bounds.Right, layout.Bounds.Bottom), _fill);
        _stroke.Color = t.AxisLine;
        canvas.DrawLine(MathF.Floor(axis.Left) + 0.5f, 0, MathF.Floor(axis.Left) + 0.5f, layout.Bounds.Bottom, _stroke);

        var font = RenderContext.GetFont(t.FontFamily, 11, false, false);
        _text.Color = t.AxisText;
        foreach (var panel in layout.Panels)
        {
            double step = PriceStepFor(panel, 52);
            double start = Math.Ceiling(panel.Bottom / step) * step;
            int dec = panel.IsPrice ? Market.Instrument.DecimalPlaces(model.Series.RowSize) : DecimalsForStep(step);
            for (double v = start; v <= panel.Top; v += step)
            {
                float y = panel.Y(v);
                if (y < panel.Rect.Top + 6 || y > panel.Rect.Bottom - 6) continue;
                string s = v.ToString("F" + dec, CultureInfo.InvariantCulture);
                if (!panel.IsPrice && Math.Abs(v) >= 100000) s = VolumeFormat.Compact((decimal)v, 1);
                canvas.DrawUiText(s, axis.Left + 6, y - font.Metrics.Ascent / 2 - font.Metrics.Descent / 2, font, _text);
                _stroke.Color = t.AxisLine;
                canvas.DrawLine(axis.Left, MathF.Floor(y) + 0.5f, axis.Left + 3, MathF.Floor(y) + 0.5f, _stroke);
            }
        }

        // etiquetas de valores actuales de indicadores
        foreach (var l in extraLabels) DrawAxisTag(canvas, layout, l.Y, l.Text, l.Background, l.Foreground, font);

        // último precio
        if (model.Series.Count > 0)
        {
            var last = model.Series[^1];
            var main = layout.Main;
            decimal price = model.Series.TicksToPrice(last.CloseTicks);
            float y = main.Y(price);
            if (y >= main.Rect.Top && y <= main.Rect.Bottom)
            {
                bool up = last.CloseTicks >= last.OpenTicks;
                var bg = up ? t.LastPriceUp : t.LastPriceDown;
                DrawAxisTag(canvas, layout, y, model.Instrument.FormatPrice(price), bg, SKColors.Black, RenderContext.GetFont(t.FontFamily, 11, true, false));
            }
        }
    }

    /// <summary>
    /// Operaciones propias: un triángulo en cada ejecución (verde compra ▲, rojo venta ▼, con la cantidad) y una
    /// línea punteada de la entrada a la salida de cada operación cerrada (verde si ganó, roja si perdió).
    /// </summary>
    private void DrawTradeMarks(SKCanvas canvas, ChartModel model, ChartLayout layout)
    {
        var series = model.Series;
        if (series.Count == 0 || (model.TradeMarks.Count == 0 && model.TradeLinks.Count == 0)) return;
        var t = model.Theme;
        var main = layout.Main;
        var plot = main.Rect;
        var firstTime = series[0].Time;
        float X(DateTime time)
        {
            int bar = time < firstTime ? -1 : series.FindBar(time);
            return bar < 0 ? float.NaN : layout.CenterX(bar);
        }

        canvas.Save();
        canvas.ClipRect(plot);
        TradeLinePaint(model.Settings);
        foreach (var link in model.TradeLinks)
        {
            float x0 = X(link.OpenTime), x1 = X(link.CloseTime);
            if (float.IsNaN(x0) || float.IsNaN(x1) || Math.Max(x0, x1) < plot.Left || Math.Min(x0, x1) > plot.Right) continue;
            _stroke.Color = link.Pnl >= 0 ? t.TradeWin : t.TradeLoss;
            canvas.DrawLine(x0, main.Y(link.OpenPrice), x1, main.Y(link.ClosePrice), _stroke);
        }

        _stroke.PathEffect = null;
        _stroke.StrokeCap = SKStrokeCap.Butt;
        _stroke.StrokeWidth = 1;
        var font = RenderContext.GetFont(t.FontFamily, 9.5f, true, false);
        float size = (float)Math.Clamp(layout.BarWidth * 0.35, 5, 8);
        bool labels = layout.BarWidth >= 14;
        using var path = new SKPath();
        foreach (var m in model.TradeMarks)
        {
            float x = X(m.Time);
            if (float.IsNaN(x) || x < plot.Left - size || x > plot.Right + size) continue;
            float y = main.Y(m.Price);
            if (y < plot.Top - size || y > plot.Bottom + size) continue;
            // la punta toca el precio: compra desde abajo, venta desde arriba
            path.Reset();
            if (m.Buy)
            {
                path.MoveTo(x, y);
                path.LineTo(x - size, y + size * 1.6f);
                path.LineTo(x + size, y + size * 1.6f);
            }
            else
            {
                path.MoveTo(x, y);
                path.LineTo(x - size, y - size * 1.6f);
                path.LineTo(x + size, y - size * 1.6f);
            }

            path.Close();
            _fill.Color = m.Buy ? t.PositionLong : t.PositionShort;
            canvas.DrawPath(path, _fill);
            _stroke.Color = new SKColor(0, 0, 0, 160);
            canvas.DrawPath(path, _stroke);
            if (!labels) continue;
            string q = (m.Buy ? "+" : "−") + m.Quantity.ToString("0.####", CultureInfo.InvariantCulture);
            _text.Color = m.Buy ? t.PositionLong : t.PositionShort;
            float ty = m.Buy ? y + size * 1.6f + font.Spacing : y - size * 1.6f - 3;
            canvas.DrawUiText(q, x - RenderContext.MeasureWidth(font, q) / 2, ty, font, _text);
        }

        canvas.Restore();
    }

    /// <summary>
    /// La operación elegida en una lista: recuadro de la entrada a la salida (verde si ganó, rojo si perdió), el peor y el
    /// mejor precio por el que pasó (MAE / MFE, punteados), flecha en la entrada, cruz en la salida y una etiqueta.
    /// </summary>
    private void DrawFocusTrade(SKCanvas canvas, ChartModel model, ChartLayout layout, ChartRenderState state)
    {
        var f = model.FocusTrade!;
        var series = model.Series;
        if (series.Count == 0) return;
        int b0 = f.EntryTime < series[0].Time ? -1 : series.FindBar(f.EntryTime);
        int b1 = series.FindBar(f.ExitTime);
        if (b0 < 0 || b1 < 0) return;
        var t = model.Theme;
        var main = layout.Main;
        var plot = main.Rect;
        float half = (float)Math.Max(2, layout.BarWidth / 2);
        float x0 = layout.CenterX(b0) - half, x1 = layout.CenterX(b1) + half;
        if (x1 < plot.Left || x0 > plot.Right) return;

        // peor y mejor precio dentro de la operación (con las velas)
        decimal lo = Math.Min(f.EntryPrice, f.ExitPrice), hi = Math.Max(f.EntryPrice, f.ExitPrice);
        var inst = model.Instrument;
        for (int i = b0; i <= b1 && i < series.Count; i++)
        {
            var c = series[i];
            if (c.Trades == 0) continue;
            lo = Math.Min(lo, inst.FromTicks(c.LowTicks));
            hi = Math.Max(hi, inst.FromTicks(c.HighTicks));
        }

        var win = f.Pnl >= 0 ? t.TradeWin : t.TradeLoss;
        float yEntry = main.Y(f.EntryPrice), yExit = main.Y(f.ExitPrice);
        canvas.Save();
        canvas.ClipRect(plot);
        _fill.Color = win.WithAlpha(38);
        canvas.DrawRect(new SKRect(x0, Math.Min(yEntry, yExit), x1, Math.Max(yEntry, yExit)), _fill);
        _stroke.Color = win.WithAlpha(200);
        _stroke.StrokeWidth = 1.5f;
        _stroke.PathEffect = null;
        canvas.DrawRect(new SKRect(x0, Math.Min(yEntry, yExit), x1, Math.Max(yEntry, yExit)), _stroke);

        // MAE / MFE: el extremo en contra y el extremo a favor
        decimal adverse = f.IsLong ? lo : hi, favorable = f.IsLong ? hi : lo;
        _stroke.StrokeWidth = 1.2f;
        _stroke.PathEffect = SKPathEffect.CreateDash(new[] { 3f, 3f }, 0);
        var font = RenderContext.GetFont(t.FontFamily, 10f, true, false);
        float yAdverse = main.Y(adverse), yFavorable = main.Y(favorable);
        void Level(float y, float labelY, SKColor color, string label)
        {
            _stroke.Color = color;
            canvas.DrawLine(x0, y, x1, y, _stroke);
            _text.Color = color;
            float w = RenderContext.MeasureWidth(font, label);
            // a la derecha del recuadro si entra; si no (operación pegada al borde), a la izquierda
            float lx = x1 + 4 + w <= plot.Right ? x1 + 4 : Math.Max(plot.Left + 2, x0 - 4 - w);
            canvas.DrawUiText(label, lx, labelY + font.Size / 3, font, _text);
        }

        // si los dos niveles quedan muy cerca, las etiquetas se separan
        float gap = font.Size + 2;
        float la = yAdverse, lf = yFavorable;
        if (Math.Abs(la - lf) < gap)
        {
            float mid = (la + lf) / 2;
            bool adverseBelow = f.IsLong;
            la = adverseBelow ? mid + gap / 2 : mid - gap / 2;
            lf = adverseBelow ? mid - gap / 2 : mid + gap / 2;
        }

        Level(yAdverse, la, t.TradeLoss, "MAE " + model.Instrument.FormatPrice(adverse));
        Level(yFavorable, lf, t.TradeWin, "MFE " + model.Instrument.FormatPrice(favorable));
        _stroke.PathEffect = null;

        // entrada (flecha) y salida (cruz)
        float size = 7;
        using (var path = new SKPath())
        {
            float xe = layout.CenterX(b0);
            float dir = f.IsLong ? 1 : -1;
            path.MoveTo(xe, yEntry + dir * 2);
            path.LineTo(xe - size, yEntry + dir * (size * 1.8f + 2));
            path.LineTo(xe + size, yEntry + dir * (size * 1.8f + 2));
            path.Close();
            _fill.Color = f.IsLong ? t.PositionLong : t.PositionShort;
            canvas.DrawPath(path, _fill);
        }

        float xx = layout.CenterX(b1);
        _stroke.Color = win;
        _stroke.StrokeWidth = 2.2f;
        canvas.DrawLine(xx - 6, yExit - 6, xx + 6, yExit + 6, _stroke);
        canvas.DrawLine(xx - 6, yExit + 6, xx + 6, yExit - 6, _stroke);

        // etiqueta arriba del recuadro, con una ✕ para sacar el resaltado
        var labelFont = RenderContext.GetFont(t.FontFamily, 11f, true, false);
        const float closeW = 20;
        float w = RenderContext.MeasureWidth(labelFont, f.Label) + 14 + closeW;
        float ly = Math.Max(plot.Top + 4, Math.Min(main.Y(hi), Math.Min(yEntry, yExit)) - 26);
        float lx = Math.Clamp(x0, plot.Left + 4, Math.Max(plot.Left + 4, plot.Right - w - 4));
        var box = new SKRoundRect(new SKRect(lx, ly, lx + w, ly + 20), 5);
        _fill.Color = t.BackgroundBottom.WithAlpha(235);
        canvas.DrawRoundRect(box, _fill);
        _stroke.Color = win;
        _stroke.StrokeWidth = 1;
        canvas.DrawRoundRect(box, _stroke);
        _text.Color = win;
        canvas.DrawUiText(f.Label, lx + 7, ly + 14, labelFont, _text);
        float cx = lx + w - closeW / 2 - 2, cy = ly + 10;
        _stroke.Color = win.WithAlpha(90);
        canvas.DrawLine(lx + w - closeW - 2, ly + 4, lx + w - closeW - 2, ly + 16, _stroke);
        bool over = state.MouseX is { } mx && state.MouseY is { } my && mx >= lx + w - closeW - 2 && mx <= lx + w && my >= ly && my <= ly + 20;
        _stroke.Color = over ? t.TradeLoss : win;
        _stroke.StrokeWidth = over ? 2f : 1.6f;
        canvas.DrawLine(cx - 4, cy - 4, cx + 4, cy + 4, _stroke);
        canvas.DrawLine(cx - 4, cy + 4, cx + 4, cy - 4, _stroke);
        state.FocusTradeClose = new SKRect(lx + w - closeW - 4, ly - 2, lx + w + 2, ly + 22);
        _stroke.StrokeWidth = 1;
        canvas.Restore();
    }

    /// <summary>Grosor y trazo de las líneas entrada → salida (configurables en el gráfico).</summary>
    private void TradeLinePaint(ChartSettings s)
    {
        float w = (float)Math.Clamp(s.TradeLineWidth, 0.5, 8);
        _stroke.StrokeWidth = w;
        _stroke.StrokeCap = s.TradeLineStyle == TradeLineStyle.Dotted ? SKStrokeCap.Round : SKStrokeCap.Butt;
        _stroke.PathEffect = s.TradeLineStyle switch
        {
            TradeLineStyle.Dashed => SKPathEffect.CreateDash(new[] { 4f + w * 2, 3f + w * 1.5f }, 0),
            TradeLineStyle.Dotted => SKPathEffect.CreateDash(new[] { 0.1f, w * 2.5f + 1.5f }, 0),
            _ => null,
        };
    }

    /// <summary>
    /// Operaciones de las estrategias puestas en el gráfico: flecha en la entrada (▲ compra abajo, ▼ venta arriba) con su
    /// nombre, una cruz en la salida con el resultado, y una línea punteada entre las dos (verde si ganó, roja si perdió).
    /// </summary>
    private void DrawStrategySignals(SKCanvas canvas, ChartModel model, ChartLayout layout)
    {
        var series = model.Series;
        if (series.Count == 0) return;
        var t = model.Theme;
        var main = layout.Main;
        var plot = main.Rect;
        var firstTime = series[0].Time;
        float X(DateTime time)
        {
            int bar = time < firstTime ? -1 : series.FindBar(time);
            return bar < 0 ? float.NaN : layout.CenterX(bar);
        }

        canvas.Save();
        canvas.ClipRect(plot);
        var font = RenderContext.GetFont(t.FontFamily, 9.5f, true, false);
        float size = (float)Math.Clamp(layout.BarWidth * 0.45, 6, 10);
        bool labels = layout.BarWidth >= 7;
        using var path = new SKPath();
        foreach (var st in model.Strategies)
        {
            if (!st.Visible) continue;
            if (st.Lines.Count > 0 || st.Shapes.Count > 0) DrawStrategyShapes(canvas, model, layout, st);
            TradeLinePaint(model.Settings);
            foreach (var link in st.Links)
            {
                float x0 = X(link.OpenTime), x1 = X(link.CloseTime);
                if (float.IsNaN(x0) || float.IsNaN(x1) || Math.Max(x0, x1) < plot.Left || Math.Min(x0, x1) > plot.Right) continue;
                _stroke.Color = link.Pnl >= 0 ? t.TradeWin : t.TradeLoss;
                canvas.DrawLine(x0, main.Y(link.OpenPrice), x1, main.Y(link.ClosePrice), _stroke);
            }

            _stroke.PathEffect = null;
            _stroke.StrokeCap = SKStrokeCap.Butt;
            foreach (var sg in st.Signals)
            {
                float x = X(sg.Time);
                if (float.IsNaN(x) || x < plot.Left - size || x > plot.Right + size) continue;
                float y = main.Y(sg.Price);
                if (y < plot.Top - 40 || y > plot.Bottom + 40) continue;
                var color = sg.Buy ? t.PositionLong : t.PositionShort;
                float ty;
                if (sg.Entry)
                {
                    // flecha con la punta en el precio de entrada, del color de la estrategia por dentro
                    path.Reset();
                    float dir = sg.Buy ? 1 : -1;
                    path.MoveTo(x, y + dir * 2);
                    path.LineTo(x - size, y + dir * (size * 1.8f + 2));
                    path.LineTo(x - size * 0.35f, y + dir * (size * 1.8f + 2));
                    path.LineTo(x - size * 0.35f, y + dir * (size * 3f + 2));
                    path.LineTo(x + size * 0.35f, y + dir * (size * 3f + 2));
                    path.LineTo(x + size * 0.35f, y + dir * (size * 1.8f + 2));
                    path.LineTo(x + size, y + dir * (size * 1.8f + 2));
                    path.Close();
                    _fill.Color = st.Color.WithAlpha(230);
                    canvas.DrawPath(path, _fill);
                    _stroke.StrokeWidth = 1.3f;
                    _stroke.Color = color;
                    canvas.DrawPath(path, _stroke);
                    ty = sg.Buy ? y + size * 3f + 2 + font.Spacing : y - size * 3f - 5;
                }
                else
                {
                    // salida: una cruz en el precio
                    _stroke.StrokeWidth = 2f;
                    _stroke.Color = st.Color;
                    float c = size * 0.75f;
                    canvas.DrawLine(x - c, y - c, x + c, y + c, _stroke);
                    canvas.DrawLine(x - c, y + c, x + c, y - c, _stroke);
                    ty = sg.Buy ? y + c + font.Spacing : y - c - 4;
                }

                if (!labels || sg.Label.Length == 0) continue;
                _text.Color = sg.Entry ? st.Color : color;
                canvas.DrawUiText(sg.Label, x - RenderContext.MeasureWidth(font, sg.Label) / 2, ty, font, _text);
            }
        }

        _stroke.StrokeWidth = 1;
        canvas.Restore();
    }

    /// <summary>
    /// Lo que dibuja una estrategia: líneas continuas (vela a vela) y dibujos (rangos, líneas, niveles, marcas, textos).
    /// Solo se miran los que caen en la parte visible.
    /// </summary>
    private void DrawStrategyShapes(SKCanvas canvas, ChartModel model, ChartLayout layout, StrategyOverlay st)
    {
        var series = model.Series;
        var main = layout.Main;
        var plot = main.Rect;
        var t = model.Theme;
        int first = Math.Clamp(layout.FirstVisible, 0, series.Count - 1), last = Math.Clamp(layout.LastVisible, 0, series.Count - 1);
        var firstTime = series[0].Time;
        var fromTime = series[first].Time;
        var toTime = last + 1 < series.Count ? series[last + 1].Time : DateTime.MaxValue;
        float half = (float)Math.Max(1, layout.BarWidth / 2);
        float X(DateTime time)
        {
            if (time == DateTime.MinValue) return plot.Left - 2;
            if (time < firstTime) return layout.CenterX(0) - half * 2;
            int bar = series.FindBar(time);
            return layout.CenterX(Math.Max(0, bar));
        }

        void Dash(int dash, float width) => _stroke.PathEffect = dash switch
        {
            1 => SKPathEffect.CreateDash(new[] { 6f * width, 4f * width }, 0),
            2 => SKPathEffect.CreateDash(new[] { 1.5f * width, 3f * width }, 0),
            _ => null,
        };

        // líneas continuas
        using var path = new SKPath();
        foreach (var line in st.Lines)
        {
            var pts = line.Points;
            if (pts.Count == 0) continue;
            int lo = 0, hi = pts.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) >>> 1;
                if (pts[mid].Time < fromTime) lo = mid + 1;
                else hi = mid;
            }

            path.Reset();
            bool open = false;
            for (int i = Math.Max(0, lo - 1); i < pts.Count; i++)
            {
                var (time, value) = pts[i];
                if (value is not { } v)
                {
                    open = false;
                    continue;
                }

                float x = X(time), y = main.Y(v);
                if (!open) path.MoveTo(x, y);
                else path.LineTo(x, y);
                open = true;
                if (time > toTime) break;
            }

            _stroke.Color = line.Color;
            _stroke.StrokeWidth = line.Width;
            Dash(line.Dash, line.Width);
            canvas.DrawPath(path, _stroke);
        }

        _stroke.PathEffect = null;
        var font = RenderContext.GetFont(t.FontFamily, 10f, false, false);
        float size = (float)Math.Clamp(layout.BarWidth * 0.4, 5, 9);
        foreach (var sh in st.Shapes)
        {
            var tMin = sh.Time1 < sh.Time2 ? sh.Time1 : sh.Time2;
            var tMax = sh.Time1 < sh.Time2 ? sh.Time2 : sh.Time1;
            if (tMin > toTime) continue;
            if (!sh.ExtendRight && tMax < fromTime && tMax != DateTime.MinValue) continue;
            _stroke.Color = sh.Color;
            _stroke.StrokeWidth = sh.Width;
            switch (sh.Kind)
            {
                case StrategyShapeKind.Box:
                {
                    float x0 = X(tMin) - half, x1 = X(tMax) + half;
                    float y0 = main.Y(Math.Max(sh.Price1, sh.Price2)), y1 = main.Y(Math.Min(sh.Price1, sh.Price2));
                    var r = new SKRect(x0, y0, x1, y1);
                    if (sh.FillAlpha > 0)
                    {
                        _fill.Color = sh.Color.WithAlpha(sh.FillAlpha);
                        canvas.DrawRect(r, _fill);
                    }

                    Dash(sh.Dash, sh.Width);
                    canvas.DrawRect(r, _stroke);
                    _stroke.PathEffect = null;
                    if (sh.Text.Length > 0)
                    {
                        _text.Color = sh.Color;
                        canvas.DrawUiText(sh.Text, x0 + 4, y0 - 4, font, _text);
                    }

                    break;
                }
                case StrategyShapeKind.Line:
                    Dash(sh.Dash, sh.Width);
                    canvas.DrawLine(X(sh.Time1), main.Y(sh.Price1), X(sh.Time2), main.Y(sh.Price2), _stroke);
                    _stroke.PathEffect = null;
                    break;
                case StrategyShapeKind.HorizontalLine:
                {
                    float x0 = Math.Max(plot.Left, X(sh.Time1)), y = MathF.Floor(main.Y(sh.Price1)) + 0.5f;
                    Dash(sh.Dash, sh.Width);
                    canvas.DrawLine(x0, y, plot.Right, y, _stroke);
                    _stroke.PathEffect = null;
                    if (sh.Text.Length > 0)
                    {
                        _text.Color = sh.Color;
                        float w = RenderContext.MeasureWidth(font, sh.Text);
                        canvas.DrawUiText(sh.Text, Math.Max(x0 + 4, plot.Right - w - 6), y - 4, font, _text);
                    }

                    break;
                }
                case StrategyShapeKind.Marker:
                {
                    float x = X(sh.Time1), y = main.Y(sh.Price1);
                    _fill.Color = sh.Color;
                    float ty = y - size - 4;
                    switch (sh.Marker)
                    {
                        case StrategyMarker.ArrowUp:
                            path.Reset();
                            path.MoveTo(x, y + 2);
                            path.LineTo(x - size, y + size * 1.7f + 2);
                            path.LineTo(x + size, y + size * 1.7f + 2);
                            path.Close();
                            canvas.DrawPath(path, _fill);
                            ty = y + size * 1.7f + 4 + font.Spacing;
                            break;
                        case StrategyMarker.ArrowDown:
                            path.Reset();
                            path.MoveTo(x, y - 2);
                            path.LineTo(x - size, y - size * 1.7f - 2);
                            path.LineTo(x + size, y - size * 1.7f - 2);
                            path.Close();
                            canvas.DrawPath(path, _fill);
                            ty = y - size * 1.7f - 6;
                            break;
                        case StrategyMarker.Square:
                            canvas.DrawRect(new SKRect(x - size * 0.7f, y - size * 0.7f, x + size * 0.7f, y + size * 0.7f), _fill);
                            break;
                        case StrategyMarker.Diamond:
                            path.Reset();
                            path.MoveTo(x, y - size);
                            path.LineTo(x + size * 0.8f, y);
                            path.LineTo(x, y + size);
                            path.LineTo(x - size * 0.8f, y);
                            path.Close();
                            canvas.DrawPath(path, _fill);
                            break;
                        case StrategyMarker.Cross:
                            _stroke.StrokeWidth = 2;
                            canvas.DrawLine(x - size * 0.7f, y - size * 0.7f, x + size * 0.7f, y + size * 0.7f, _stroke);
                            canvas.DrawLine(x - size * 0.7f, y + size * 0.7f, x + size * 0.7f, y - size * 0.7f, _stroke);
                            break;
                        default:
                            canvas.DrawCircle(x, y, size * 0.7f, _fill);
                            break;
                    }

                    if (sh.Text.Length > 0 && layout.BarWidth >= 5)
                    {
                        _text.Color = sh.Color;
                        canvas.DrawUiText(sh.Text, x - RenderContext.MeasureWidth(font, sh.Text) / 2, ty, font, _text);
                    }

                    break;
                }
                case StrategyShapeKind.Text:
                {
                    _text.Color = sh.Color;
                    float x = X(sh.Time1), y = main.Y(sh.Price1);
                    canvas.DrawUiText(sh.Text, x - RenderContext.MeasureWidth(font, sh.Text) / 2, y, font, _text);
                    break;
                }
            }
        }

        _stroke.StrokeWidth = 1;
        _stroke.PathEffect = null;
    }

    /// <summary>Órdenes activas y posición de la cuenta del gráfico (líneas + etiquetas).</summary>
    private static readonly SKColor StopColor = new(240, 82, 90);
    private static readonly SKColor TargetColor = new(38, 194, 129);

    private void DrawTrading(SKCanvas canvas, ChartModel model, ChartLayout layout, ChartRenderState state)
    {
        state.Handles.Clear();
        var acc = model.Account;
        if (acc == null) return;
        var t = model.Theme;
        var main = layout.Main;
        var inst = model.Instrument;
        var br = model.Brackets;
        var font = RenderContext.GetFont(t.FontFamily, 10.5f, true, false);
        var inv = CultureInfo.InvariantCulture;
        canvas.Save();
        canvas.ClipRect(new SKRect(main.Rect.Left, main.Rect.Top, layout.Bounds.Right, main.Rect.Bottom));

        string Qty(decimal q) => q.ToString("0.####", inv);
        string Money(decimal v) => v.ToString("+0.00;-0.00;0.00", inv);

        // dibuja una línea con su etiqueta; devuelve el rectángulo de la etiqueta
        SKRect? Line(decimal price, SKColor color, string label, bool dashed, bool ghost)
        {
            float y = MathF.Floor(main.Y(price)) + 0.5f;
            if (y < main.Rect.Top || y > main.Rect.Bottom) return null;
            var c = ghost ? color.WithAlpha(150) : color;
            _stroke.Color = c;
            _stroke.StrokeWidth = 1;
            _stroke.PathEffect = dashed ? SKPathEffect.CreateDash(new[] { 6f, 4f }, 0) : null;
            canvas.DrawLine(main.Rect.Left, y, main.Rect.Right, y, _stroke);
            _stroke.PathEffect = null;

            float w = RenderContext.MeasureWidth(font, label) + 12;
            float h = font.Spacing + 4;
            var r = new SKRect(main.Rect.Right - w - 8, y - h / 2, main.Rect.Right - 8, y + h / 2);
            _fill.Color = c;
            canvas.DrawRoundRect(r, 3, 3, _fill);
            _text.Color = SKColors.Black;
            canvas.DrawUiText(label, r.Left + 6, y - font.Metrics.Ascent / 2 - font.Metrics.Descent / 2, font, _text);
            DrawAxisTag(canvas, layout, y, inst.FormatPrice(price), c, SKColors.Black, font);
            return r;
        }

        // botones "SL" y "TP" a la izquierda de una etiqueta: se arrastran para poner stop u objetivo
        void Buttons(SKRect label, Order? entry)
        {
            float x = label.Left - 4;
            foreach (var (role, text, color) in new[] { (BracketRole.Target, "TP", TargetColor), (BracketRole.StopLoss, "SL", StopColor) })
            {
                float w = RenderContext.MeasureWidth(font, text) + 10;
                var r = new SKRect(x - w, label.Top, x, label.Bottom);
                _fill.Color = new SKColor(12, 16, 26, 235);
                canvas.DrawRoundRect(r, 3, 3, _fill);
                _stroke.Color = color;
                canvas.DrawRoundRect(r, 3, 3, _stroke);
                _text.Color = color;
                canvas.DrawUiText(text, r.Left + 5, r.MidY - font.Metrics.Ascent / 2 - font.Metrics.Descent / 2, font, _text);
                state.Handles.Add(new BracketHandle(r, new BracketDrag(entry, role), true));
                x = r.Left - 4;
            }
        }

        void Band(decimal price, BracketDrag drag)
        {
            float y = main.Y(price);
            state.Handles.Add(new BracketHandle(new SKRect(main.Rect.Left, y - 4, main.Rect.Right, y + 4), drag, false));
        }

        // cross trading: la posición y las órdenes son de otro instrumento (mirás ES, ejecutás MES) y se aclara cuál
        string other = acc.Instrument.Key != inst.Key ? acc.Instrument.Symbol + " · " : "";
        var pos = acc.Position;
        bool hasPosition = pos != null && pos.Volume != 0;
        if (hasPosition)
        {
            var label = Line(pos!.AveragePrice, pos.Volume > 0 ? t.PositionLong : t.PositionShort,
                $"{other}{(pos.Volume > 0 ? "LARGO" : "CORTO")} {Qty(Math.Abs(pos.Volume))} · {Money(pos.UnrealizedPnL)}", false, false);
            if (label is { } lr && br != null) Buttons(lr, null);
            if (br != null) Band(pos.AveragePrice, new BracketDrag(null, BracketRole.None));
        }

        foreach (var o in acc.ActiveOrders)
        {
            bool dragging = ReferenceEquals(o, state.DragOrder);
            decimal price = o.Type == OrderTypes.Stop ? o.TriggerPrice : o.Price;
            var role = br?.RoleOf(o) ?? BracketRole.None;
            SKColor color;
            string label;
            if (role == BracketRole.StopLoss)
            {
                color = StopColor;
                label = $"SL {Qty(o.Unfilled)} · {Money(br!.PnLAt(dragging && state.DragPrice > 0 ? state.DragPrice : price, o.Unfilled))}";
            }
            else if (role == BracketRole.Target)
            {
                color = TargetColor;
                int n = br!.TargetNumber(o);
                label = $"TP{(n > 0 ? n.ToString(inv) : "")} {Qty(o.Unfilled)} · {Money(br.PnLAt(dragging && state.DragPrice > 0 ? state.DragPrice : price, o.Unfilled))}";
            }
            else
            {
                color = o.Direction == OrderDirections.Buy ? t.BuyOrder : t.SellOrder;
                string kind = o.Type == OrderTypes.Stop ? "STP" : "LMT";
                label = $"{kind} {(o.Direction == OrderDirections.Buy ? "COMPRA" : "VENTA")} {Qty(o.Unfilled)}";
            }

            label = other + label;
            var lr = Line(price, color, label, role == BracketRole.None, dragging);
            if (dragging && state.DragPrice > 0) Line(state.DragPrice, color, label + " →", false, false);

            // orden de entrada: botones para adjuntarle stop/objetivo y sus brackets pendientes
            if (role != BracketRole.None || o.ReduceOnly || br == null) continue;
            bool entryLike = !hasPosition || (o.Direction == OrderDirections.Buy) == (pos!.Volume > 0);
            if (!entryLike) continue;
            if (lr is { } r && !dragging) Buttons(r, o);
            if (br.PendingFor(o) is not { } pb) continue;
            decimal entry = dragging && state.DragPrice > 0 ? state.DragPrice : pb.EntryPrice;
            if (pb.StopTicks is int st)
            {
                decimal sp = entry - pb.Side * st * inst.TickSize;
                Line(sp, StopColor, $"SL al llenar · {st}t · {Money(br.PnLAt(sp, o.Unfilled, entry, pb.Side))}", true, true);
                Band(sp, new BracketDrag(o, BracketRole.StopLoss, pb, -1));
            }

            for (int i = 0; i < pb.Targets.Count; i++)
            {
                var tg = pb.Targets[i];
                decimal tp = entry + pb.Side * tg.Ticks * inst.TickSize;
                string pct = pb.Targets.Count > 1 ? $" {tg.Percent.ToString("0.#", inv)}%" : "";
                Line(tp, TargetColor, $"TP{i + 1}{pct} al llenar · {tg.Ticks}t", true, true);
                Band(tp, new BracketDrag(o, BracketRole.Target, pb, i));
            }
        }

        // stop/objetivo que se está arrastrando
        if (state.BracketDrag is { } d && state.DragPrice > 0 && br != null)
        {
            decimal p = state.DragPrice;
            if (d.Entry == null && hasPosition)
            {
                decimal last = acc.LastPrice > 0 ? acc.LastPrice : pos!.AveragePrice;
                var role = d.Role != BracketRole.None ? d.Role : (p > last) == (pos!.Volume > 0) ? BracketRole.Target : BracketRole.StopLoss;
                bool sl = role == BracketRole.StopLoss;
                Line(p, sl ? StopColor : TargetColor, $"{(sl ? "SL" : "TP")} · {Money(br.PnLAt(p))}", false, false);
            }
            else if (d.Entry != null)
            {
                var pb = d.Pending ?? br.PendingFor(d.Entry);
                decimal entry = pb?.EntryPrice ?? (d.Entry.Type == OrderTypes.Stop ? d.Entry.TriggerPrice : d.Entry.Price);
                int side = d.Entry.Direction == OrderDirections.Buy ? 1 : -1;
                int ticks = (int)Math.Round((p - entry) / inst.TickSize) * side;
                bool sl = d.Role == BracketRole.StopLoss || (d.Role == BracketRole.None && ticks < 0);
                Line(p, sl ? StopColor : TargetColor, $"{(sl ? "SL" : "TP")} al llenar · {Math.Abs(ticks)}t", false, false);
            }
        }

        canvas.Restore();
    }

    private static int DecimalsForStep(double step)
    {
        if (step >= 1) return 0;
        return Math.Min(6, (int)Math.Ceiling(-Math.Log10(step)));
    }

    private void DrawAxisTag(SKCanvas canvas, ChartLayout layout, float y, string text, SKColor bg, SKColor fg, SKFont font)
    {
        var axis = layout.PriceAxis;
        float h = font.Spacing + 4;
        var r = new SKRect(axis.Left + 1, y - h / 2, layout.Bounds.Right - 1, y + h / 2);
        _fill.Color = bg;
        canvas.DrawRect(r, _fill);
        _text.Color = fg;
        canvas.DrawUiText(text, axis.Left + 6, y - font.Metrics.Ascent / 2 - font.Metrics.Descent / 2, font, _text);
    }

    private static readonly (TimeSpan Step, string Format)[] TimeSteps =
    {
        (TimeSpan.FromSeconds(1), "HH:mm:ss"), (TimeSpan.FromSeconds(5), "HH:mm:ss"), (TimeSpan.FromSeconds(15), "HH:mm:ss"),
        (TimeSpan.FromSeconds(30), "HH:mm:ss"), (TimeSpan.FromMinutes(1), "HH:mm"), (TimeSpan.FromMinutes(2), "HH:mm"),
        (TimeSpan.FromMinutes(5), "HH:mm"), (TimeSpan.FromMinutes(10), "HH:mm"), (TimeSpan.FromMinutes(15), "HH:mm"),
        (TimeSpan.FromMinutes(30), "HH:mm"), (TimeSpan.FromHours(1), "HH:mm"), (TimeSpan.FromHours(2), "HH:mm"),
        (TimeSpan.FromHours(4), "HH:mm"), (TimeSpan.FromHours(6), "HH:mm"), (TimeSpan.FromHours(12), "HH:mm"),
        (TimeSpan.FromDays(1), "dd MMM"), (TimeSpan.FromDays(2), "dd MMM"), (TimeSpan.FromDays(7), "dd MMM"),
        (TimeSpan.FromDays(14), "dd MMM"), (TimeSpan.FromDays(30), "MMM yyyy"), (TimeSpan.FromDays(90), "MMM yyyy"),
        (TimeSpan.FromDays(365), "yyyy"),
    };

    private static List<(float X, string Text, bool Major)> ComputeTimeTicks(ChartModel model, ChartLayout layout)
    {
        var res = new List<(float, string, bool)>();
        var series = model.Series;
        if (series.Count == 0 || layout.LastVisible < layout.FirstVisible) return res;
        var tz = model.Settings.TimeZone;
        var es = TradingHub.Common.UiCulture.Current;

        // estimación de tiempo por píxel
        int f = layout.FirstVisible, l = layout.LastVisible;
        double spanSec = Math.Max(1, (series[l].Time - series[f].Time).TotalSeconds);
        double px = Math.Max(1, layout.CenterX(l) - layout.CenterX(f));
        double secPerPx = spanSec / px;
        var chosen = TimeSteps[^1];
        foreach (var ts in TimeSteps)
        {
            if (ts.Step.TotalSeconds / secPerPx >= 85)
            {
                chosen = ts;
                break;
            }
        }

        long stepTicks = chosen.Step.Ticks;
        DateTime? prevLocal = null;
        long prevBucket = long.MinValue;
        float lastX = float.MinValue;
        for (int i = f; i <= l; i++)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(series[i].Time, tz);
            long bucket = chosen.Step.TotalDays >= 28
                ? local.Year * 12 + local.Month
                : chosen.Step.TotalDays >= 1
                    ? (long)(local.Date.Ticks / stepTicks)
                    : (local.Ticks / stepTicks);
            if (bucket != prevBucket)
            {
                float x = layout.CenterX(i);
                bool major = prevLocal != null && local.Date != prevLocal.Value.Date;
                string text = major && chosen.Step < TimeSpan.FromDays(1) ? local.ToString("dd MMM", es) : local.ToString(chosen.Format, es);
                if (x - lastX >= 60 && prevBucket != long.MinValue)
                {
                    res.Add((x, text, major));
                    lastX = x;
                }

                prevBucket = bucket;
            }

            prevLocal = local;
        }

        return res;
    }

    private void DrawTimeAxis(SKCanvas canvas, ChartModel model, ChartLayout layout, List<(float X, string Text, bool Major)> ticks)
    {
        var t = model.Theme;
        var ax = layout.TimeAxis;
        _fill.Color = t.AxisBackground;
        canvas.DrawRect(ax, _fill);
        _stroke.Color = t.AxisLine;
        canvas.DrawLine(0, MathF.Floor(ax.Top) + 0.5f, layout.Bounds.Right, MathF.Floor(ax.Top) + 0.5f, _stroke);
        var font = RenderContext.GetFont(t.FontFamily, 11, false, false);
        var bold = RenderContext.GetFont(t.FontFamily, 11, true, false);
        foreach (var (x, text, major) in ticks)
        {
            var f = major ? bold : font;
            float w = RenderContext.MeasureWidth(f, text);
            _text.Color = major ? t.Text : t.AxisText;
            canvas.DrawUiText(text, x - w / 2, ax.Top + 16, f, _text);
        }
    }

    // =================================================================== títulos, info y cursor

    private void DrawPanelTitles(SKCanvas canvas, ChartModel model, ChartLayout layout, ChartRenderState state)
    {
        var t = model.Theme;
        var font = RenderContext.GetFont(t.FontFamily, 11, false, false);
        float mx = state.MouseX ?? -1, my = state.MouseY ?? -1;
        bool names = model.Settings.ShowIndicatorNames;
        foreach (var panel in layout.Panels.Skip(1))
        {
            float x = panel.Rect.Left + 6, y = panel.Rect.Top + 14;
            foreach (var slot in panel.SubPanel!.Slots)
            {
                string name = slot.Indicator.Name;
                float nameW = RenderContext.MeasureWidth(font, name);
                bool hover = new SKRect(x - 4, y - 12, x + nameW + 66, y + 5).Contains(mx, my);
                if (names || hover)
                {
                    if (hover) DrawLegendRowBack(canvas, t, new SKRect(x - 4, y - 12, x + nameW + 62, y + 5));
                    _text.Color = slot.Error != null ? new SKColor(255, 110, 110) : slot.Visible ? t.TextDim : t.TextDim.WithAlpha(110);
                    canvas.DrawUiText(name, x, y, font, _text);
                    state.Legend.Add(new LegendHit(new SKRect(x - 4, y - 12, x + nameW + 4, y + 5), slot, LegendAction.Name));
                    x += nameW + 8;
                    if (hover || !slot.Visible) x = DrawLegendIcons(canvas, state, slot, x, y, hover, t.TextDim) + 6;
                }

                if (slot.Visible)
                {
                    foreach (var s in slot.Indicator.DataSeries.OfType<ValueDataSeries>())
                    {
                        if (!s.Visible || s.VisualType == VisualMode.Hide || s.IsHidden && s.VisualType == VisualMode.OnlyValueOnAxis) continue;
                        int lastBar = Math.Min(s.Count, model.Series.Count) - 1;
                        if (lastBar < 0) continue;
                        string v = SeriesRenderer.FormatValue(model, panel, s[lastBar]);
                        _text.Color = (s.Colors.Has(lastBar) ? s.Colors[lastBar] : s.Color).ToSk();
                        canvas.DrawUiText(v, x, y, font, _text);
                        x += RenderContext.MeasureWidth(font, v) + 8;
                        if (x > panel.Rect.Right - 80) break;
                    }
                }

                if (slot.Error != null)
                {
                    _text.Color = new SKColor(255, 110, 110);
                    canvas.DrawUiText("⚠ " + slot.Error, x, y, font, _text);
                }

                x += 10;
            }
        }
    }

    private void DrawInfo(SKCanvas canvas, ChartModel model, ChartLayout layout, ChartRenderState state)
    {
        var t = model.Theme;
        var main = layout.Main;
        var series = model.Series;
        var settings = model.Settings;
        var title = RenderContext.GetFont(t.FontFamily, 13, true, false);
        var font = RenderContext.GetFont(t.FontFamily, 11, false, false);
        float x = main.Rect.Left + 8, y = main.Rect.Top + 18;

        if (settings.ShowLegendTitle)
        {
            _text.Color = t.Text;
            string head = $"{model.Instrument.Symbol}  ·  {model.Period.DisplayName}  ·  {model.Instrument.Exchange}";
            canvas.DrawUiText(head, x, y, title, _text);
            y += 17;
        }
        else
        {
            y -= 2;
        }

        if (settings.ShowLegendValues && series.Count > 0)
        {
            int bar = series.Count - 1;
            if (state.MouseX is float mx0 && state.MouseY is float my0 && layout.Plot.Contains(mx0, my0))
                bar = Math.Clamp(layout.BarAt(mx0), 0, series.Count - 1);
            var c = series[bar];
            var inst = model.Instrument;
            int vd = FootprintRenderer.ChooseDecimals(c.Volume, inst.VolumeDecimals);
            var parts = new (string Label, string Value, SKColor Color)[]
            {
                ("Ap", inst.FormatPrice(series.TicksToPrice(c.OpenTicks)), t.Text),
                ("Máx", inst.FormatPrice(series.TicksToPrice(c.HighTicks)), t.Text),
                ("Mín", inst.FormatPrice(series.TicksToPrice(c.LowTicks)), t.Text),
                ("Cie", inst.FormatPrice(series.TicksToPrice(c.CloseTicks)), c.CloseTicks >= c.OpenTicks ? t.UpCandle : t.DownCandle),
                ("Vol", VolumeFormat.Compact(c.Volume, vd), t.Text),
                ("Δ", (c.Delta > 0 ? "+" : "") + VolumeFormat.Compact(c.Delta, vd), c.Delta >= 0 ? t.Ask : t.Bid),
                ("Ops", c.Trades.ToString(CultureInfo.InvariantCulture), t.Text),
            };
            float total = parts.Sum(pt => RenderContext.MeasureWidth(font, pt.Label) + 4 + RenderContext.MeasureWidth(font, pt.Value) + 12);
            using (var back = new SKPaint { IsAntialias = true, Color = t.BackgroundTop.WithAlpha(215) })
                canvas.DrawRoundRect(new SKRect(x - 4, y - 12, x + total - 6, y + 5), 4, 4, back);
            foreach (var (label, value, color) in parts)
            {
                _text.Color = t.TextDim;
                canvas.DrawUiText(label, x, y, font, _text);
                x += RenderContext.MeasureWidth(font, label) + 4;
                _text.Color = color;
                canvas.DrawUiText(value, x, y, font, _text);
                x += RenderContext.MeasureWidth(font, value) + 12;
            }

            y += 16;
        }

        // indicadores superpuestos: uno por línea; al pasar el mouse aparecen ojo, ajustes y quitar (como TradingView)
        var overlay = model.Indicators.Where(s => s.Panel == null).ToList();
        if (overlay.Count == 0 && model.Strategies.Count == 0) return;
        x = main.Rect.Left + 8;
        float mx = state.MouseX ?? -1, my = state.MouseY ?? -1;
        var arrow = new SKRect(x - 2, y - 10, x + 9, y + 2);
        DrawLegendArrow(canvas, arrow.MidX, arrow.MidY, settings.ShowIndicatorNames, t.TextDim);
        state.Legend.Add(new LegendHit(Inflate(arrow, 4), null, LegendAction.Collapse));
        float nx = x + 14;
        if (!settings.ShowIndicatorNames)
        {
            _text.Color = t.TextDim.WithAlpha(150);
            int total = overlay.Count + model.Strategies.Count;
            string n = total == 1 ? "1 indicador" : $"{total} indicadores";
            canvas.DrawUiText(n, nx, y, font, _text);
            state.Legend.Add(new LegendHit(new SKRect(nx - 2, y - 11, nx + RenderContext.MeasureWidth(font, n) + 2, y + 4), null, LegendAction.Collapse));
            return;
        }

        foreach (var slot in overlay)
        {
            if (y > main.Rect.Bottom - 12) break;
            string name = slot.Indicator.Name + (slot.Error != null ? " ⚠" : "");
            float w = RenderContext.MeasureWidth(font, name);
            var row = new SKRect(nx - 4, y - 12, nx + w + 66, y + 5);
            bool hover = row.Contains(mx, my);
            if (hover) DrawLegendRowBack(canvas, t, new SKRect(nx - 4, y - 12, nx + w + 62, y + 5));
            _text.Color = slot.Error != null ? new SKColor(255, 110, 110) : slot.Visible ? t.TextDim : t.TextDim.WithAlpha(110);
            canvas.DrawUiText(name, nx, y, font, _text);
            state.Legend.Add(new LegendHit(new SKRect(nx - 4, y - 12, nx + w + 4, y + 5), slot, LegendAction.Name));
            if (hover || !slot.Visible) DrawLegendIcons(canvas, state, slot, nx + w + 8, y, hover, t.TextDim);
            y += 16;
        }

        // estrategias: como un indicador más, con su estado (visual o en vivo y en qué cuenta)
        foreach (var st in model.Strategies)
        {
            if (y > main.Rect.Bottom - 12) break;
            // (el ▶ no está en la fuente del gráfico: se dibuja el triángulo)
            string name = st.Name + (st.Status.Length > 0 ? " · " + st.Status : "") + (st.Error != null ? " (!)" : "");
            float w = RenderContext.MeasureWidth(font, name) + 12;
            var row = new SKRect(nx - 4, y - 12, nx + w + 66, y + 5);
            bool hover = row.Contains(mx, my);
            if (hover) DrawLegendRowBack(canvas, t, new SKRect(nx - 4, y - 12, nx + w + 62, y + 5));
            _text.Color = st.Error != null ? new SKColor(255, 110, 110) : st.Visible ? (st.Live ? st.Color : t.TextDim) : t.TextDim.WithAlpha(110);
            using (var tri = new SKPath())
            {
                tri.MoveTo(nx, y - 8.5f);
                tri.LineTo(nx + 7, y - 4.5f);
                tri.LineTo(nx, y - 0.5f);
                tri.Close();
                _fill.Color = st.Visible ? st.Color : st.Color.WithAlpha(110);
                canvas.DrawPath(tri, _fill);
            }

            canvas.DrawUiText(name, nx + 12, y, font, _text);
            state.Legend.Add(new LegendHit(new SKRect(nx - 4, y - 12, nx + w + 4, y + 5), null, LegendAction.Name, st));
            if (hover || !st.Visible) DrawLegendIcons(canvas, state, null, nx + w + 8, y, hover, t.TextDim, st);
            y += 16;
        }
    }

    private static SKRect Inflate(SKRect r, float d) => new(r.Left - d, r.Top - d, r.Right + d, r.Bottom + d);

    private static void DrawLegendRowBack(SKCanvas canvas, ChartTheme t, SKRect r)
    {
        using var p = new SKPaint { IsAntialias = true, Color = t.CrosshairLabelBack.WithAlpha(150) };
        canvas.DrawRoundRect(r, 4, 4, p);
    }

    /// <summary>Ojo (ocultar/mostrar) y, con el mouse encima, ajustes y quitar. Devuelve dónde terminan.</summary>
    private static float DrawLegendIcons(SKCanvas canvas, ChartRenderState state, IndicatorSlot? slot, float x, float y, bool hover, SKColor color, StrategyOverlay? strategy = null)
    {
        bool visible = slot?.Visible ?? strategy?.Visible ?? true;
        float cy = y - 4;
        using var p = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.3f, Color = color, StrokeCap = SKStrokeCap.Round };
        // ojo
        float ex = x + 7;
        using (var path = new SKPath())
        {
            path.MoveTo(ex - 6, cy);
            path.QuadTo(ex, cy - 6.5f, ex + 6, cy);
            path.QuadTo(ex, cy + 6.5f, ex - 6, cy);
            canvas.DrawPath(path, p);
        }

        p.Style = SKPaintStyle.Fill;
        canvas.DrawCircle(ex, cy, 1.9f, p);
        p.Style = SKPaintStyle.Stroke;
        if (!visible) canvas.DrawLine(ex - 5.5f, cy + 5, ex + 5.5f, cy - 5, p);
        state.Legend.Add(new LegendHit(new SKRect(x - 2, cy - 8, x + 16, cy + 8), slot, LegendAction.ToggleVisible, strategy));
        if (!hover) return x + 16;

        // ajustes (engranaje)
        float gx = x + 25;
        canvas.DrawCircle(gx, cy, 3.3f, p);
        for (int i = 0; i < 8; i++)
        {
            double a = i * Math.PI / 4;
            canvas.DrawLine(gx + (float)Math.Cos(a) * 4.4f, cy + (float)Math.Sin(a) * 4.4f, gx + (float)Math.Cos(a) * 6.1f, cy + (float)Math.Sin(a) * 6.1f, p);
        }

        state.Legend.Add(new LegendHit(new SKRect(gx - 8, cy - 8, gx + 8, cy + 8), slot, LegendAction.Settings, strategy));

        // quitar
        float cx = x + 43;
        canvas.DrawLine(cx - 4, cy - 4, cx + 4, cy + 4, p);
        canvas.DrawLine(cx - 4, cy + 4, cx + 4, cy - 4, p);
        state.Legend.Add(new LegendHit(new SKRect(cx - 8, cy - 8, cx + 8, cy + 8), slot, LegendAction.Remove, strategy));
        return cx + 8;
    }

    private static void DrawLegendArrow(SKCanvas canvas, float cx, float cy, bool expanded, SKColor color)
    {
        using var p = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color };
        using var path = new SKPath();
        if (expanded)
        {
            path.MoveTo(cx - 4, cy - 2);
            path.LineTo(cx + 4, cy - 2);
            path.LineTo(cx, cy + 3);
        }
        else
        {
            path.MoveTo(cx - 2, cy - 4);
            path.LineTo(cx + 3, cy);
            path.LineTo(cx - 2, cy + 4);
        }

        path.Close();
        canvas.DrawPath(path, p);
    }

    /// <summary>Botón chiquito abajo a la derecha para volver a lo último cuando el gráfico está corrido hacia atrás.</summary>
    private static void DrawGoLatest(SKCanvas canvas, ChartModel model, ChartLayout layout, ChartRenderState state)
    {
        if (model.Viewport.ScrollBars < 2 || model.Series.Count == 0) return;
        var t = model.Theme;
        float bottom = layout.Plot.Bottom;
        var r = new SKRect(layout.Plot.Right - 40, bottom - 34, layout.Plot.Right - 12, bottom - 12);
        bool hover = state.MouseX is float mx && state.MouseY is float my && r.Contains(mx, my);
        using var back = new SKPaint { IsAntialias = true, Color = t.CrosshairLabelBack.WithAlpha(hover ? (byte)255 : (byte)205) };
        canvas.DrawRoundRect(r, 6, 6, back);
        using var p = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.7f, Color = t.CrosshairLabelText, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
        float cx = r.MidX, cy = r.MidY;
        using (var path = new SKPath())
        {
            path.MoveTo(cx - 7, cy - 4);
            path.LineTo(cx - 3, cy);
            path.LineTo(cx - 7, cy + 4);
            path.MoveTo(cx - 2, cy - 4);
            path.LineTo(cx + 2, cy);
            path.LineTo(cx - 2, cy + 4);
            canvas.DrawPath(path, p);
        }

        canvas.DrawLine(cx + 6, cy - 5, cx + 6, cy + 5, p);
        state.GoLatestButton = r;
    }

    private void DrawCrosshair(SKCanvas canvas, ChartModel model, ChartLayout layout, ChartRenderState state)
    {
        if (state.MouseX is not float mx || state.MouseY is not float my)
        {
            DrawLinkedCrosshair(canvas, model, layout, state);
            return;
        }

        if (!layout.Plot.Contains(mx, my)) return;
        var t = model.Theme;
        var panel = layout.PanelAt(my);
        if (panel == null) return;

        int bar = layout.BarAt(mx);
        float cx = bar >= 0 && bar < model.Series.Count ? layout.CenterX(bar) : mx;
        _stroke.Color = t.Crosshair;
        _stroke.PathEffect = SKPathEffect.CreateDash(new[] { 4f, 3f }, 0);
        canvas.DrawLine(MathF.Floor(cx) + 0.5f, layout.Plot.Top, MathF.Floor(cx) + 0.5f, layout.Plot.Bottom, _stroke);
        canvas.DrawLine(panel.Rect.Left, MathF.Floor(my) + 0.5f, panel.Rect.Right, MathF.Floor(my) + 0.5f, _stroke);
        _stroke.PathEffect = null;

        var font = RenderContext.GetFont(t.FontFamily, 11, true, false);
        double v = panel.ValueAt(my);
        string label = panel.IsPrice
            ? model.Instrument.FormatPrice(model.Settings.Style == ChartStyle.Footprint
                ? FootprintRowPrice(model, layout, (decimal)v)
                : model.Instrument.RoundPrice((decimal)v))
            : SeriesRenderer.FormatValue(model, panel, (decimal)v);
        DrawAxisTag(canvas, layout, my, label, t.CrosshairLabelBack, t.CrosshairLabelText, font);

        if (bar >= 0 && bar < model.Series.Count)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(model.Series[bar].Time, model.Settings.TimeZone);
            string ts = local.ToString(model.Period.IsTimeBased && model.Period.ApproxDuration >= TimeSpan.FromDays(1) ? "ddd dd MMM yyyy" : "ddd dd MMM  HH:mm:ss", TradingHub.Common.UiCulture.Current);
            float w = RenderContext.MeasureWidth(font, ts) + 12;
            var r = new SKRect(cx - w / 2, layout.TimeAxis.Top + 1, cx + w / 2, layout.TimeAxis.Bottom - 1);
            _fill.Color = t.CrosshairLabelBack;
            canvas.DrawRect(r, _fill);
            _text.Color = t.CrosshairLabelText;
            canvas.DrawUiText(ts, r.Left + 6, r.Top + 16, font, _text);
        }
    }

    /// <summary>Cruz que viene de otro gráfico del mismo grupo de color (más tenue).</summary>
    private void DrawLinkedCrosshair(SKCanvas canvas, ChartModel model, ChartLayout layout, ChartRenderState state)
    {
        if (state.LinkedTime is not { } time || model.Series.Count == 0) return;
        var t = model.Theme;
        float x = ChartDrawing.XOf(layout, model.Series, time);
        int bar = layout.BarAt(x);
        if (bar >= 0 && bar < model.Series.Count && time <= model.Series[^1].Time.Add(model.Period.ApproxDuration)) x = layout.CenterX(bar);
        var font = RenderContext.GetFont(t.FontFamily, 11, true, false);
        _stroke.Color = t.Crosshair.WithAlpha((byte)(t.Crosshair.Alpha * 0.8));
        _stroke.PathEffect = SKPathEffect.CreateDash(new[] { 2f, 3f }, 0);
        if (x >= layout.Plot.Left && x <= layout.Plot.Right)
            canvas.DrawLine(MathF.Floor(x) + 0.5f, layout.Plot.Top, MathF.Floor(x) + 0.5f, layout.Plot.Bottom, _stroke);
        float? y = null;
        if (state.LinkedPrice is { } price)
        {
            float py = layout.Main.Y(price);
            if (py >= layout.Main.Rect.Top && py <= layout.Main.Rect.Bottom)
            {
                y = py;
                canvas.DrawLine(layout.Main.Rect.Left, MathF.Floor(py) + 0.5f, layout.Main.Rect.Right, MathF.Floor(py) + 0.5f, _stroke);
            }
        }

        _stroke.PathEffect = null;
        if (y is float yy)
        {
            string label = model.Instrument.FormatPrice(model.Instrument.RoundPrice((decimal)state.LinkedPrice!.Value));
            DrawAxisTag(canvas, layout, yy, label, t.CrosshairLabelBack, t.CrosshairLabelText, font);
        }

        if (x >= layout.Plot.Left && x <= layout.Plot.Right)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(time, model.Settings.TimeZone);
            string ts = local.ToString(model.Period.IsTimeBased && model.Period.ApproxDuration >= TimeSpan.FromDays(1) ? "ddd dd MMM yyyy" : "ddd dd MMM  HH:mm", TradingHub.Common.UiCulture.Current);
            float w = RenderContext.MeasureWidth(font, ts) + 12;
            var r = new SKRect(x - w / 2, layout.TimeAxis.Top + 1, x + w / 2, layout.TimeAxis.Bottom - 1);
            _fill.Color = t.CrosshairLabelBack;
            canvas.DrawRect(r, _fill);
            _text.Color = t.CrosshairLabelText;
            canvas.DrawUiText(ts, r.Left + 6, r.Top + 16, font, _text);
        }
    }

    private static decimal FootprintRowPrice(ChartModel model, ChartLayout layout, decimal price)
    {
        long step = (long)model.Series.PriceStep * Math.Max(1, layout.FootprintRowFactor);
        long ticks = model.Instrument.ToTicks(price);
        return Data.CandleSeries.FloorDiv(ticks, step) * step * model.Instrument.TickSize;
    }

    private void DrawOverlay(SKCanvas canvas, ChartModel model, ChartLayout layout, ChartRenderState state)
    {
        string? msg = state.Message;
        if (msg == null)
        {
            if (model.Data.IsLoadingHistory) msg = $"Cargando historia…  {model.Data.HistoryProgress * 100:0}%";
            else if (model.Series.Count == 0) msg = model.Data.HistoryError != null ? "Error: " + model.Data.HistoryError : "Esperando datos…";
        }

        if (msg == null) return;
        var t = model.Theme;
        var font = RenderContext.GetFont(t.FontFamily, 15, true, false);
        float w = RenderContext.MeasureWidth(font, msg) + 36;
        var main = layout.Main.Rect;
        var r = new SKRect(main.MidX - w / 2, main.MidY - 22, main.MidX + w / 2, main.MidY + 22);
        using var p = new SKPaint { IsAntialias = true, Color = new SKColor(8, 14, 26, 235) };
        canvas.DrawRoundRect(r, 8, 8, p);
        using var b = new SKPaint { IsAntialias = true, Color = t.PanelSeparator, Style = SKPaintStyle.Stroke };
        canvas.DrawRoundRect(r, 8, 8, b);
        _text.Color = t.Text;
        canvas.DrawUiText(msg, r.Left + 18, r.MidY + 5, font, _text);
    }
}
