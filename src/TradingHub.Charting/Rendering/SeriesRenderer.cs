using System.Drawing.Drawing2D;
using System.Globalization;
using SkiaSharp;
using TradingHub.Common;
using TradingHub.Indicators;
using TradingHub.Indicators.Drawing;
using TradingHub.Rendering.Context;
using Color = System.Drawing.Color;
using TradingHub.Rendering;

namespace TradingHub.Charting.Rendering;

internal static class Sk
{
    public static SKColor ToSk(this Color c) => new(c.R, c.G, c.B, c.A);

    public static SKPathEffect? Dash(DashStyle style, float w)
    {
        float u = Math.Max(1f, w);
        return style switch
        {
            DashStyle.Dash => SKPathEffect.CreateDash(new[] { 6 * u, 3 * u }, 0),
            DashStyle.Dot => SKPathEffect.CreateDash(new[] { 1.5f * u, 2 * u }, 0),
            DashStyle.DashDot => SKPathEffect.CreateDash(new[] { 6 * u, 2 * u, 1.5f * u, 2 * u }, 0),
            DashStyle.DashDotDot => SKPathEffect.CreateDash(new[] { 6 * u, 2 * u, 1.5f * u, 2 * u, 1.5f * u, 2 * u }, 0),
            _ => null,
        };
    }

    public static SKPathEffect? Dash(LineDashStyle style, float w) => Dash(style switch
    {
        LineDashStyle.Dash => DashStyle.Dash,
        LineDashStyle.Dot => DashStyle.Dot,
        LineDashStyle.DashDot => DashStyle.DashDot,
        LineDashStyle.DashDotDot => DashStyle.DashDotDot,
        _ => DashStyle.Solid,
    }, w);
}

/// <summary>Una etiqueta de valor para el eje de precios.</summary>
internal readonly record struct AxisLabel(float Y, string Text, SKColor Background, SKColor Foreground);

/// <summary>Dibuja las series y objetos de los indicadores.</summary>
internal sealed class SeriesRenderer
{
    private readonly SKPaint _stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round };
    private readonly SKPaint _fill = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _text = new() { IsAntialias = true };

    /// <summary>Colores de vela pedidos por indicadores (PaintbarsDataSeries).</summary>
    public Dictionary<int, SKColor> CollectPaintBars(ChartModel model, ChartLayout layout)
    {
        var res = new Dictionary<int, SKColor>();
        foreach (var slot in model.Indicators)
        {
            if (!slot.Visible || slot.Panel != null) continue;
            foreach (var s in slot.Indicator.DataSeries)
            {
                if (s is not PaintbarsDataSeries pb || !pb.Visible) continue;
                for (int i = layout.FirstVisible; i <= layout.LastVisible && i < pb.Count; i++)
                {
                    // transparente = el indicador oculta esa vela del gráfico (así lo hace Heikin Ashi de ATAS)
                    var c = pb[i];
                    if (c.HasValue) res[i] = c.Value.ToSk();
                }
            }
        }

        return res;
    }

    /// <summary>
    /// Si algún indicador visible del panel de precio reemplaza las velas del gráfico: pide ocultarlas
    /// (PaintbarsDataSeries.HideChart) o dibuja sus propias velas sobre el precio (Heikin Ashi, etc.).
    /// </summary>
    public static bool ChartReplacedByIndicator(ChartModel model, ChartLayout layout)
    {
        foreach (var slot in model.Indicators)
        {
            if (!slot.Visible || slot.Panel != null) continue;
            foreach (var s in slot.Indicator.DataSeries)
            {
                if (s is PaintbarsDataSeries { HideChart: true }) return true;
                if (s is CandleDataSeries cds && cds.Visible && !cds.IsHidden)
                {
                    for (int i = layout.LastVisible; i >= layout.FirstVisible && i >= layout.LastVisible - 20; i--)
                        if (i < cds.Count && !cds[i].IsEmpty) return true;
                }
            }
        }

        return false;
    }

    /// <summary>Rellenos de bandas (van debajo de las velas).</summary>
    public void RenderBands(SKCanvas canvas, ChartLayout layout, PanelLayout panel, IEnumerable<IndicatorSlot> slots)
    {
        foreach (var slot in slots)
        {
            if (!slot.Visible) continue;
            foreach (var s in slot.Indicator.DataSeries)
            {
                if (s is not RangeDataSeries rs || !rs.Visible) continue;
                RenderRange(canvas, layout, panel, rs, panel.IsPrice);
            }
        }
    }

    /// <summary>Líneas, histogramas, marcadores y velas de los indicadores de un panel.</summary>
    public void RenderSeries(SKCanvas canvas, ChartModel model, ChartLayout layout, PanelLayout panel, IEnumerable<IndicatorSlot> slots, List<AxisLabel> axisLabels)
    {
        foreach (var slot in slots)
        {
            if (!slot.Visible) continue;
            var ind = slot.Indicator;
            foreach (var s in ind.DataSeries)
            {
                if (!s.Visible) continue;
                switch (s)
                {
                    case ValueDataSeries vs:
                        RenderValueSeries(canvas, model, layout, panel, vs, axisLabels);
                        break;
                    case CandleDataSeries cs:
                        RenderCandleSeries(canvas, layout, panel, cs);
                        break;
                    case PriceSelectionDataSeries ps:
                        RenderPriceSelections(canvas, layout, panel, ps);
                        break;
                }
            }

            foreach (var ls in ind.LineSeries)
            {
                if (ls.IsHidden || !ls.Visible) continue;
                float y = panel.Y(ls.Value);
                if (y < panel.Rect.Top - 1 || y > panel.Rect.Bottom + 1) continue;
                _stroke.Color = ls.Color.ToSk();
                _stroke.StrokeWidth = ls.Width;
                _stroke.PathEffect = Sk.Dash(ls.LineDashStyle, ls.Width);
                y = MathF.Floor(y) + 0.5f;
                canvas.DrawLine(panel.Rect.Left, y, panel.Rect.Right, y, _stroke);
                _stroke.PathEffect = null;
            }
        }
    }

    private static bool IsEmpty(decimal v, ValueDataSeries vs, bool isPrice) => v == 0 && (isPrice || !vs.ShowZeroValue);

    /// <summary>Y de un precio de nivel (centro de su fila) o de un precio cualquiera.</summary>
    private static float YLevel(PanelLayout panel, decimal price) =>
        panel.IsPrice && panel.RowCenterOf != null && panel.RowSize > 0 && price % panel.RowSize == 0
            ? panel.Y(panel.RowCenterOf(price))
            : panel.Y(price);

    private void RenderValueSeries(SKCanvas canvas, ChartModel model, ChartLayout layout, PanelLayout panel, ValueDataSeries vs, List<AxisLabel> axisLabels)
    {
        if (vs.VisualType == VisualMode.Hide) return;
        int first = Math.Max(0, layout.FirstVisible - 1), last = Math.Min(vs.Count - 1, layout.LastVisible + 1);
        var baseColor = vs.Color.ToSk();
        float w = Math.Max(1, vs.Width);

        switch (vs.VisualType)
        {
            case VisualMode.Line:
            case VisualMode.Steps:
            {
                _stroke.StrokeWidth = w;
                _stroke.PathEffect = Sk.Dash(vs.LineDashStyle, w);
                SKPath? path = null;
                SKColor pathColor = baseColor;
                float px = 0, py = 0;
                bool hasPrev = false;
                for (int i = first; i <= last; i++)
                {
                    var v = vs[i];
                    if (IsEmpty(v, vs, panel.IsPrice))
                    {
                        hasPrev = false;
                        continue;
                    }

                    float x = layout.CenterX(i), y = panel.Y(v);
                    var col = vs.Colors.Has(i) ? vs.Colors[i].ToSk() : baseColor;
                    if (hasPrev && !vs.IsThisPointOfEndLine(i - 1))
                    {
                        if (path == null || col != pathColor)
                        {
                            Flush(canvas, ref path, pathColor);
                            path = new SKPath();
                            path.MoveTo(px, py);
                            pathColor = col;
                        }

                        if (vs.VisualType == VisualMode.Steps) path.LineTo(x, py);
                        path.LineTo(x, y);
                    }
                    else
                    {
                        Flush(canvas, ref path, pathColor);
                    }

                    px = x;
                    py = y;
                    hasPrev = true;
                }

                Flush(canvas, ref path, pathColor);
                _stroke.PathEffect = null;
                break;
            }

            case VisualMode.Histogram:
            {
                float bw = (float)Math.Max(1, (layout.BarWidth - layout.BarSpacing) * 0.8);
                float y0 = panel.Y(vs.ZeroValue);
                for (int i = first; i <= last; i++)
                {
                    var v = vs[i];
                    if (v == vs.ZeroValue && !vs.ShowZeroValue) continue;
                    if (panel.IsPrice && v == 0) continue;
                    float x = layout.CenterX(i), y = panel.Y(v);
                    _fill.Color = vs.Colors.Has(i) ? vs.Colors[i].ToSk() : baseColor;
                    _fill.IsAntialias = false;
                    float top = Math.Min(y, y0), bot = Math.Max(y, y0);
                    if (bot - top < 1) bot = top + 1;
                    canvas.DrawRect(new SKRect(MathF.Round(x - bw / 2), top, MathF.Round(x - bw / 2) + Math.Max(1, MathF.Round(bw)), bot), _fill);
                    _fill.IsAntialias = true;
                }

                break;
            }

            case VisualMode.Levels:
            case VisualMode.Hash:
            {
                float half = (float)Math.Max(2, layout.BarWidth / 2);
                _stroke.StrokeWidth = w;
                for (int i = first; i <= last; i++)
                {
                    var v = vs[i];
                    if (IsEmpty(v, vs, panel.IsPrice)) continue;
                    float x = layout.CenterX(i), y = panel.Y(v);
                    _stroke.Color = vs.Colors.Has(i) ? vs.Colors[i].ToSk() : baseColor;
                    canvas.DrawLine(x - half, y, x + half, y, _stroke);
                }

                break;
            }

            case VisualMode.OnlyValueOnAxis:
                break;

            default:
                RenderMarkers(canvas, layout, panel, vs, first, last, baseColor, w);
                break;
        }

        if (vs.ShowCurrentValue && vs.VisualType != VisualMode.Hide && vs.Count > 0)
        {
            int lastBar = Math.Min(vs.Count, model.Series.Count) - 1;
            if (lastBar >= 0)
            {
                var v = vs[lastBar];
                if (!(panel.IsPrice && v == 0))
                {
                    float y = panel.Y(v);
                    if (y >= panel.Rect.Top && y <= panel.Rect.Bottom)
                    {
                        var bg = vs.Colors.Has(lastBar) ? vs.Colors[lastBar].ToSk() : baseColor;
                        axisLabels.Add(new AxisLabel(y, FormatValue(model, panel, v), bg.WithAlpha(255), Contrast(bg)));
                    }
                }
            }
        }
    }

    private void Flush(SKCanvas canvas, ref SKPath? path, SKColor color)
    {
        if (path == null) return;
        _stroke.Color = color;
        canvas.DrawPath(path, _stroke);
        path.Dispose();
        path = null;
    }

    private void RenderMarkers(SKCanvas canvas, ChartLayout layout, PanelLayout panel, ValueDataSeries vs, int first, int last, SKColor baseColor, float w)
    {
        float size = (float)Math.Clamp(layout.BarWidth * 0.35, 3, 9) + w;
        for (int i = first; i <= last; i++)
        {
            var v = vs[i];
            if (IsEmpty(v, vs, panel.IsPrice)) continue;
            float x = layout.CenterX(i), y = panel.Y(v);
            var col = vs.Colors.Has(i) ? vs.Colors[i].ToSk() : baseColor;
            _fill.Color = col;
            _stroke.Color = col;
            _stroke.StrokeWidth = Math.Max(1, w);
            switch (vs.VisualType)
            {
                case VisualMode.Dots:
                case VisualMode.Points:
                    canvas.DrawCircle(x, y, Math.Max(1.5f, size / 3), _fill);
                    break;
                case VisualMode.Square:
                case VisualMode.Block:
                    canvas.DrawRect(new SKRect(x - size / 3, y - size / 3, x + size / 3, y + size / 3), _fill);
                    break;
                case VisualMode.Cross:
                    canvas.DrawLine(x - size / 2, y, x + size / 2, y, _stroke);
                    canvas.DrawLine(x, y - size / 2, x, y + size / 2, _stroke);
                    break;
                case VisualMode.UpArrow:
                {
                    using var p = new SKPath();
                    p.MoveTo(x, y);
                    p.LineTo(x - size / 2, y + size * 0.8f);
                    p.LineTo(x + size / 2, y + size * 0.8f);
                    p.Close();
                    canvas.DrawPath(p, _fill);
                    break;
                }
                case VisualMode.DownArrow:
                {
                    using var p = new SKPath();
                    p.MoveTo(x, y);
                    p.LineTo(x - size / 2, y - size * 0.8f);
                    p.LineTo(x + size / 2, y - size * 0.8f);
                    p.Close();
                    canvas.DrawPath(p, _fill);
                    break;
                }
                default:
                    canvas.DrawCircle(x, y, 2, _fill);
                    break;
            }
        }
    }

    private void RenderRange(SKCanvas canvas, ChartLayout layout, PanelLayout panel, RangeDataSeries rs, bool isPrice)
    {
        int first = Math.Max(0, layout.FirstVisible - 1), last = Math.Min(rs.Count - 1, layout.LastVisible + 1);
        _fill.Color = rs.RangeColor.ToSk();
        var upper = new List<SKPoint>();
        var lower = new List<SKPoint>();

        void FlushPoly()
        {
            if (upper.Count >= 2)
            {
                using var path = new SKPath();
                path.MoveTo(upper[0]);
                for (int k = 1; k < upper.Count; k++) path.LineTo(upper[k]);
                for (int k = lower.Count - 1; k >= 0; k--) path.LineTo(lower[k]);
                path.Close();
                canvas.DrawPath(path, _fill);
            }

            upper.Clear();
            lower.Clear();
        }

        for (int i = first; i <= last; i++)
        {
            var v = rs[i];
            if (v == null || (v.Upper == 0 && v.Lower == 0))
            {
                FlushPoly();
                continue;
            }

            float x = layout.CenterX(i);
            upper.Add(new SKPoint(x, panel.Y(v.Upper)));
            lower.Add(new SKPoint(x, panel.Y(v.Lower)));
        }

        FlushPoly();
    }

    private void RenderCandleSeries(SKCanvas canvas, ChartLayout layout, PanelLayout panel, CandleDataSeries cs)
    {
        float bw = (float)Math.Max(1, layout.BarWidth - layout.BarSpacing);
        int first = layout.FirstVisible, last = Math.Min(cs.Count - 1, layout.LastVisible);
        for (int i = first; i <= last; i++)
        {
            var c = cs[i];
            if (c == null || c.IsEmpty) continue;
            float x = layout.CenterX(i);
            bool up = c.Close >= c.Open;
            var col = c.Color?.ToSk() ?? (cs.Colors.Has(i) ? cs.Colors[i].ToSk() : (up ? cs.UpCandleColor : cs.DownCandleColor).ToSk());
            float yH = panel.Y(c.High), yL = panel.Y(c.Low), yO = panel.Y(c.Open), yC = panel.Y(c.Close);
            _stroke.Color = cs.DrawCandleBorder ? cs.BorderColor.ToSk() : col;
            _stroke.StrokeWidth = 1;
            _stroke.IsAntialias = false;
            float xl = MathF.Floor(x) + 0.5f;
            canvas.DrawLine(xl, yH, xl, yL, _stroke);
            _fill.Color = col;
            _fill.IsAntialias = false;
            float top = Math.Min(yO, yC), bot = Math.Max(yO, yC);
            if (bot - top < 1) bot = top + 1;
            var r = new SKRect(MathF.Round(x - bw / 2), top, MathF.Round(x - bw / 2) + Math.Max(1, MathF.Round(bw)), bot);
            canvas.DrawRect(r, _fill);
            if (cs.DrawCandleBorder && bw >= 4) canvas.DrawRect(r, _stroke);
            _stroke.IsAntialias = true;
            _fill.IsAntialias = true;
        }
    }

    private void RenderPriceSelections(SKCanvas canvas, ChartLayout layout, PanelLayout panel, PriceSelectionDataSeries ps)
    {
        float slotW = (float)Math.Max(1, layout.BarWidth - layout.BarSpacing);
        double rowH = layout.Main.RowHeight;
        int first = layout.FirstVisible, last = Math.Min(ps.Count - 1, layout.LastVisible);
        for (int i = first; i <= last; i++)
        {
            var list = ps[i];
            if (list == null || list.Count == 0) continue;
            float cx = layout.CenterX(i);
            foreach (var sel in list.ToArray())
            {
                float yTop = (float)(YLevel(panel, sel.MaximumPrice) - rowH / 2);
                float yBot = (float)(YLevel(panel, sel.MinimumPrice) + rowH / 2);
                float yMid = (yTop + yBot) / 2;
                int size = Math.Clamp(sel.Size, Math.Max(1, sel.MinimumSize), Math.Max(sel.MinimumSize, sel.MaximumSize));
                var objCol = sel.ObjectColor.ToSk();
                if (sel.ObjectsTransparency > 0) objCol = objCol.WithAlpha((byte)(255 * (100 - Math.Clamp(sel.ObjectsTransparency, 0, 100)) / 100));
                if (sel.PriceSelectionColor.A > 0 && sel.VisualObject != ObjectType.OnlyCluster)
                {
                    _fill.Color = sel.PriceSelectionColor.ToSk();
                    canvas.DrawRect(new SKRect(cx - slotW / 2, yTop, cx + slotW / 2, yBot), _fill);
                }

                _fill.Color = objCol;
                _stroke.Color = sel.ObjectBorderColor.A > 0 ? sel.ObjectBorderColor.ToSk() : objCol.WithAlpha(255);
                _stroke.StrokeWidth = 1;
                switch (sel.VisualObject)
                {
                    case ObjectType.Ellipse:
                        canvas.DrawCircle(cx, yMid, size / 2f, _fill);
                        if (sel.IsBorderEnabled || sel.ObjectBorderColor.A > 0) canvas.DrawCircle(cx, yMid, size / 2f, _stroke);
                        break;
                    case ObjectType.Rectangle:
                    case ObjectType.Square:
                        canvas.DrawRect(new SKRect(cx - size / 2f, yMid - size / 2f, cx + size / 2f, yMid + size / 2f), _fill);
                        break;
                    case ObjectType.Triangle:
                    {
                        using var p = new SKPath();
                        p.MoveTo(cx, yMid - size / 2f);
                        p.LineTo(cx + size / 2f, yMid + size / 2f);
                        p.LineTo(cx - size / 2f, yMid + size / 2f);
                        p.Close();
                        canvas.DrawPath(p, _fill);
                        break;
                    }
                    case ObjectType.Diamond:
                    {
                        using var p = new SKPath();
                        p.MoveTo(cx, yMid - size / 2f);
                        p.LineTo(cx + size / 2f, yMid);
                        p.LineTo(cx, yMid + size / 2f);
                        p.LineTo(cx - size / 2f, yMid);
                        p.Close();
                        canvas.DrawPath(p, _fill);
                        break;
                    }
                    case ObjectType.OnlyCluster:
                        _fill.Color = sel.PriceSelectionColor.ToSk();
                        canvas.DrawRect(new SKRect(cx - slotW / 2, yTop, cx + slotW / 2, yBot), _fill);
                        break;
                    case ObjectType.Line:
                        canvas.DrawLine(cx - slotW / 2, yMid, cx + slotW / 2, yMid, _stroke);
                        break;
                    default:
                        canvas.DrawCircle(cx, yMid, size / 2f, _fill);
                        break;
                }
            }
        }
    }

    /// <summary>Objetos de dibujo de los indicadores: líneas de tendencia, líneas hasta toque, rectángulos y textos.</summary>
    public void RenderDrawingObjects(SKCanvas canvas, ChartModel model, ChartLayout layout, PanelLayout panel, IEnumerable<IndicatorSlot> slots)
    {
        foreach (var slot in slots)
        {
            if (!slot.Visible) continue;
            var ind = slot.Indicator;

            foreach (var r in ind.Rectangles)
            {
                float x1 = layout.CenterX(r.FirstBar), x2 = layout.CenterX(r.SecondBar);
                float y1 = panel.Y(r.FirstPrice), y2 = panel.Y(r.SecondPrice);
                var rect = new SKRect(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));
                if (r.Brush != null && r.Brush.Color.A > 0)
                {
                    _fill.Color = r.Brush.Color.ToSk();
                    canvas.DrawRect(rect, _fill);
                }

                if (r.Pen != null && r.Pen.Color.A > 0)
                {
                    ApplyPen(r.Pen);
                    canvas.DrawRect(rect, _stroke);
                }
            }

            foreach (var tl in ind.TrendLines)
            {
                if (tl.Pen == null) continue;
                float x1 = layout.CenterX(tl.FirstBar), x2 = layout.CenterX(tl.SecondBar);
                float y1 = panel.Y(tl.FirstPrice), y2 = panel.Y(tl.SecondPrice);
                if (tl.IsRay && x2 != x1)
                {
                    float xr = layout.Plot.Right;
                    y2 = y1 + (y2 - y1) * (xr - x1) / (x2 - x1);
                    x2 = xr;
                }

                ApplyPen(tl.Pen);
                canvas.DrawLine(x1, y1, x2, y2, _stroke);
            }

            foreach (var line in ind.HorizontalLinesTillTouch)
            {
                if (line.Pen == null) continue;
                float x1 = layout.CenterX(line.FirstBar);
                float x2 = line.Finished ? layout.CenterX(line.SecondBar) : (line.IsRay ? layout.Plot.Right : layout.CenterX(Math.Max(line.SecondBar, line.FirstBar)));
                if (x2 < layout.Plot.Left || x1 > layout.Plot.Right) continue;
                float y = MathF.Floor(YLevel(panel, line.FirstPrice)) + 0.5f;
                ApplyPen(line.Pen);
                canvas.DrawLine(x1, y, x2, y, _stroke);
            }

            foreach (var lbl in ind.Labels.Values)
            {
                if (!lbl.Visible || string.IsNullOrEmpty(lbl.Text)) continue;
                if (lbl.Bar < layout.FirstVisible - 2 || lbl.Bar > layout.LastVisible + 2) continue;
                var font = RenderContext.GetFont(model.Theme.FontFamily, lbl.FontSize * 96f / 72f, false, false);
                float w = RenderContext.MeasureWidth(font, lbl.Text);
                float h = font.Spacing;
                float x = layout.CenterX(lbl.Bar) + lbl.XOffset;
                float y = panel.Y(lbl.TextPrice);
                float top = lbl.IsAbovePrice ? y - h - 3 + lbl.YOffset : y + 3 + lbl.YOffset;
                float left = lbl.Align switch
                {
                    DrawingText.TextAlign.Left => x - w,
                    DrawingText.TextAlign.Right => x,
                    _ => x - w / 2,
                };
                var rect = new SKRect(left - 2, top, left + w + 2, top + h);
                if (lbl.FillColor.A > 0)
                {
                    _fill.Color = lbl.FillColor.ToSk();
                    canvas.DrawRect(rect, _fill);
                }

                if (lbl.BorderColor.A > 0)
                {
                    _stroke.Color = lbl.BorderColor.ToSk();
                    _stroke.StrokeWidth = 1;
                    _stroke.PathEffect = null;
                    canvas.DrawRect(rect, _stroke);
                }

                _text.Color = lbl.Textcolor.ToSk();
                canvas.DrawUiText(lbl.Text, left, top - font.Metrics.Ascent, font, _text);
            }
        }
    }

    private void ApplyPen(UniversalPen pen)
    {
        _stroke.Color = pen.Color.ToSk();
        _stroke.StrokeWidth = Math.Max(1, pen.Width);
        _stroke.PathEffect = Sk.Dash(pen.DashStyle, pen.Width);
    }

    /// <summary>Rango de valores de los indicadores de un panel en las barras visibles (para la autoescala).</summary>
    public static (double Min, double Max) ValueRange(IEnumerable<IndicatorSlot> slots, int first, int last, bool isPrice)
    {
        double min = double.MaxValue, max = double.MinValue;
        void Take(decimal v)
        {
            double d = (double)v;
            if (d < min) min = d;
            if (d > max) max = d;
        }

        foreach (var slot in slots)
        {
            if (!slot.Visible) continue;
            foreach (var s in slot.Indicator.DataSeries)
            {
                if (!s.Visible || !s.ScaleIt) continue;
                switch (s)
                {
                    case ValueDataSeries vs when vs.VisualType is not (VisualMode.Hide or VisualMode.OnlyValueOnAxis):
                        for (int i = first; i <= last && i < vs.Count; i++)
                        {
                            var v = vs[i];
                            if (v == 0 && (isPrice || !vs.ShowZeroValue)) continue;
                            Take(v);
                        }

                        if (vs.VisualType == VisualMode.Histogram && !isPrice) Take(vs.ZeroValue);
                        break;
                    case CandleDataSeries cs:
                        for (int i = first; i <= last && i < cs.Count; i++)
                        {
                            var c = cs[i];
                            if (c == null || c.IsEmpty) continue;
                            Take(c.High);
                            Take(c.Low);
                        }

                        break;
                    case RangeDataSeries rs:
                        for (int i = first; i <= last && i < rs.Count; i++)
                        {
                            var v = rs[i];
                            if (v == null || (v.Upper == 0 && v.Lower == 0)) continue;
                            Take(v.Upper);
                            Take(v.Lower);
                        }

                        break;
                }
            }

            if (!isPrice)
            {
                foreach (var ls in slot.Indicator.LineSeries)
                    if (ls.UseScale && !ls.IsHidden) Take(ls.Value);
            }
        }

        return (min, max);
    }

    internal static string FormatValue(ChartModel model, PanelLayout panel, decimal v)
    {
        if (panel.IsPrice) return model.Instrument.FormatPrice(Math.Round(v, model.Instrument.PriceDecimals));
        var a = Math.Abs(v);
        string fmt = a >= 100000 ? "0" : a >= 1000 ? "0.#" : a >= 10 ? "0.##" : a >= 1 ? "0.###" : "0.####";
        if (a >= 1_000_000) return VolumeFormat.Compact(v, 1);
        return v.ToString(fmt, CultureInfo.InvariantCulture);
    }

    internal static SKColor Contrast(SKColor bg)
    {
        double lum = (0.299 * bg.Red + 0.587 * bg.Green + 0.114 * bg.Blue) / 255;
        return lum > 0.6 ? SKColors.Black : SKColors.White;
    }
}
