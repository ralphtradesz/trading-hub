using SkiaSharp;
using TradingHub.Data;
using TradingHub.Indicators;
using TradingHub.Market;
using TradingHub.Rendering.Context;
using TradingHub.Trading;
using TradingHub.Rendering;

namespace TradingHub.Charting.Dom;

/// <summary>
/// Libro de órdenes vertical (DOM / escalera de precios) al estilo SmartDOM: órdenes propias,
/// bid, precio, ask y volumen operado en la sesión por precio. Se opera haciendo clic.
/// </summary>
public sealed class DomModel
{
    private readonly Dictionary<long, (decimal Bid, decimal Ask)> _sessionVolume = new();
    private int _processed;
    private DateOnly _sessionDate;

    public DomModel(InstrumentData data, DomSettings? settings = null)
    {
        Data = data;
        Settings = settings ?? new DomSettings();
        AutoCenter = Settings.AutoCenter;
        data.TradesAppended += (_, _) => UpdateVolume();
        data.HistoryLoaded += () =>
        {
            _sessionVolume.Clear();
            _processed = 0;
            UpdateVolume();
        };
        UpdateVolume();
    }

    public InstrumentData Data { get; }
    public Instrument Instrument => Data.Instrument;
    public ITradingAccount? Broker { get; set; }

    /// <summary>Stop/objetivos de la cuenta (se pintan distinto).</summary>
    public BracketManager? Brackets { get; set; }

    /// <summary>Configuración visual y de comportamiento (compartida entre DOMs).</summary>
    public DomSettings Settings { get; set; }

    /// <summary>Ticks del instrumento por fila del DOM.</summary>
    public int Step { get; set; } = 1;

    /// <summary>Precio (en ticks) de la fila central cuando no está centrado automáticamente.</summary>
    public long CenterTicks { get; set; }

    /// <summary>Si sigue al precio (se apaga al desplazar con la rueda).</summary>
    public bool AutoCenter { get; set; }

    public float RowHeight => Math.Clamp(Settings.RowHeight, 10, 60);
    public decimal OrderQuantity { get; set; } = 1;

    /// <summary>Orden que se está arrastrando en el DOM y precio de destino.</summary>
    public Order? DragOrder { get; set; }
    public decimal DragPrice { get; set; }

    public SessionSettings Session { get; set; } = SessionSettings.CryptoUtc;

    /// <summary>Volumen operado en la sesión por fila (bid = vendedores agresivos, ask = compradores agresivos).</summary>
    public IReadOnlyDictionary<long, (decimal Bid, decimal Ask)> SessionVolume => _sessionVolume;

    public long RowOf(long ticks) => CandleSeries.FloorDiv(ticks, Step);

    private void UpdateVolume()
    {
        var store = Data.Trades;
        if (store.Count == 0) return;
        var today = Session.SessionDate(store.LastTime);
        if (today != _sessionDate)
        {
            _sessionDate = today;
            _sessionVolume.Clear();
            _processed = store.LowerBound(Session.SessionStartUtc(today));
        }

        for (int i = _processed; i < store.Count; i++)
        {
            if (store.GetId(i) < 0) continue; // historia en velas: no son operaciones reales
            long row = RowOf(store.GetPriceTicks(i));
            _sessionVolume.TryGetValue(row, out var v);
            var vol = store.GetVolume(i);
            _sessionVolume[row] = store.GetSide(i) == TradeSide.Sell ? (v.Bid + vol, v.Ask) : (v.Bid, v.Ask + vol);
        }

        _processed = store.Count;
    }

    public void ResetVolume()
    {
        _sessionVolume.Clear();
        _processed = 0;
        _sessionDate = default;
        UpdateVolume();
    }
}

/// <summary>Dibuja el DOM y resuelve los clics.</summary>
public sealed class DomRenderer
{
    private readonly SKPaint _fill = new() { IsAntialias = false };
    private readonly SKPaint _stroke = new() { IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
    private readonly SKPaint _text = new() { IsAntialias = true };
    private readonly List<(DomColumn Col, float X, float W)> _cols = new();

    private float _width, _height, _headerH, _rowH = 18;
    private long _topRow;
    private int _rows;

    private ChartTheme? _theme;

    /// <summary>Colores (por defecto, la base del modo oscuro/claro de la app).</summary>
    public ChartTheme Theme
    {
        get => _theme ?? ChartTheme.Base;
        set => _theme = value;
    }

    /// <summary>Alto del encabezado en el último dibujo.</summary>
    public float HeaderHeight => _headerH;

    /// <summary>Fila de arriba en el último dibujo (si cambia, el DOM se movió).</summary>
    public long TopRow => _topRow;

    /// <summary>Rótulos de cada columna, del más largo al más corto.</summary>
    public static string[] HeaderLabels(DomColumn c) => c switch
    {
        DomColumn.BuyOrders or DomColumn.SellOrders => new[] { "Órdenes", "Órd.", "Ó" },
        DomColumn.Bid => new[] { "Bid", "B" },
        DomColumn.Price => new[] { "Precio", "Prec.", "$" },
        DomColumn.Ask => new[] { "Ask", "A" },
        DomColumn.Volume => new[] { "Volumen", "Vol.", "V" },
        DomColumn.Delta => new[] { "Delta", "Δ" },
        DomColumn.TradedBid => new[] { "Vol. bid", "V.bid", "Vb" },
        DomColumn.TradedAsk => new[] { "Vol. ask", "V.ask", "Va" },
        _ => new[] { c.ToString() },
    };

    /// <summary>El rótulo más largo que entra en el ancho, o null si no entra ninguno.</summary>
    public static string? FitLabel(DomColumn c, float width, Func<string, float> measure)
    {
        foreach (var s in HeaderLabels(c))
            if (measure(s) <= width - 6) return s;
        return null;
    }

    /// <summary>Reparte el ancho; la columna de precio nunca queda más angosta que el precio (si no, se corta).</summary>
    private void ComputeColumns(DomSettings s, float width, float priceMin = 0)
    {
        _cols.Clear();
        var visible = s.Columns.Where(c => c.Visible).ToList();
        if (visible.Count == 0) visible.Add(new DomColumnSetting { Column = DomColumn.Price });
        double total = visible.Sum(c => Math.Max(0.1, c.Width));
        var price = visible.FirstOrDefault(c => c.Column == DomColumn.Price);
        float priceW = price != null ? (float)(width * Math.Max(0.1, price.Width) / total) : 0;
        bool reserve = price != null && visible.Count > 1 && priceW < priceMin && width > priceMin * 1.3f;
        double others = total - (price != null ? Math.Max(0.1, price.Width) : 0);
        float x = 0;
        foreach (var c in visible)
        {
            float w = !reserve
                ? (float)(width * Math.Max(0.1, c.Width) / total)
                : c.Column == DomColumn.Price ? priceMin : (float)((width - priceMin) * Math.Max(0.1, c.Width) / Math.Max(0.1, others));
            _cols.Add((c.Column, x, w));
            x += w;
        }
    }

    private bool TryColumn(DomColumn c, out float x, out float w)
    {
        foreach (var (col, cx, cw) in _cols)
        {
            if (col != c) continue;
            x = cx;
            w = cw;
            return true;
        }

        x = w = 0;
        return false;
    }

    private bool Has(DomColumn c) => _cols.Any(x => x.Col == c);

    /// <summary>Columna donde se dibujan las órdenes propias de un lado (si su columna está oculta, usa otra).</summary>
    public DomColumn OrdersColumn(OrderDirections dir)
    {
        var order = dir == OrderDirections.Buy
            ? new[] { DomColumn.BuyOrders, DomColumn.Bid, DomColumn.Price }
            : new[] { DomColumn.SellOrders, DomColumn.Ask, DomColumn.Price };
        foreach (var c in order)
            if (Has(c)) return c;
        return _cols.Count > 0 ? _cols[0].Col : DomColumn.Price;
    }

    private static SKColor C(System.Drawing.Color c) => new(c.R, c.G, c.B, c.A);

    public void Render(SKCanvas canvas, DomModel dom, float width, float height, float? mouseY = null)
    {
        var s = dom.Settings;
        _width = width;
        _height = height;
        _rowH = dom.RowHeight;
        var t = Theme;
        var inst = dom.Instrument;
        var book = dom.Data.Depth;

        // la letra nunca más alta que la fila
        float size = Math.Min(Math.Clamp(s.FontSize, 7, 24), _rowH - 3);
        var font = RenderContext.GetFont(t.FontFamily, size, false, false);
        var bold = RenderContext.GetFont(t.FontFamily, size, true, false);
        decimal sample = dom.Data.LastPrice > 0 ? dom.Data.LastPrice : inst.TickSize * 100_000;
        ComputeColumns(s, width, RenderContext.MeasureWidth(bold, inst.FormatPrice(sample)) + 10);
        var small = RenderContext.GetFont(t.FontFamily, Math.Max(7, size - 1.5f), false, false);
        var header = RenderContext.GetFont(t.FontFamily, Math.Clamp(s.FontSize - 1.5f, 8, 14), false, false);
        _headerH = s.ShowHeader ? MathF.Ceiling(header.Spacing + 9) : 0;

        _fill.Color = t.BackgroundTop;
        canvas.DrawRect(0, 0, width, height, _fill);

        _rows = Math.Max(1, (int)((height - _headerH) / _rowH));
        int drawRows = _rows + 1; // fila parcial abajo
        long lastTicks = dom.Data.Trades.Count > 0 ? dom.Data.Trades.GetPriceTicks(dom.Data.Trades.Count - 1) : (book.HasBid ? book.BestBidTicks : 0);
        long lastRow = dom.RowOf(lastTicks);
        long centerRow = dom.RowOf(dom.CenterTicks);
        // recentra si el precio se va del tercio central
        if (dom.CenterTicks == 0 || (dom.AutoCenter && Math.Abs(lastRow - centerRow) > _rows / 3)) dom.CenterTicks = lastRow * dom.Step;

        long center = dom.RowOf(dom.CenterTicks);
        _topRow = center + _rows / 2;

        // libro por fila
        var bids = new Dictionary<long, decimal>();
        var asks = new Dictionary<long, decimal>();
        foreach (var l in book.Bids) bids[dom.RowOf(l.PriceTicks)] = bids.GetValueOrDefault(dom.RowOf(l.PriceTicks)) + l.Size;
        foreach (var l in book.Asks) asks[dom.RowOf(l.PriceTicks)] = asks.GetValueOrDefault(dom.RowOf(l.PriceTicks)) + l.Size;
        long bestBidRow = book.HasBid ? dom.RowOf(book.BestBidTicks) : long.MinValue;
        long bestAskRow = book.HasAsk ? dom.RowOf(book.BestAskTicks) : long.MaxValue;

        decimal maxBook = 0, maxVol = 0, maxDelta = 0, maxSide = 0;
        for (int i = 0; i < drawRows; i++)
        {
            long r = _topRow - i;
            maxBook = Math.Max(maxBook, Math.Max(bids.GetValueOrDefault(r), asks.GetValueOrDefault(r)));
            if (!dom.SessionVolume.TryGetValue(r, out var sv)) continue;
            maxVol = Math.Max(maxVol, sv.Bid + sv.Ask);
            maxDelta = Math.Max(maxDelta, Math.Abs(sv.Ask - sv.Bid));
            maxSide = Math.Max(maxSide, Math.Max(sv.Bid, sv.Ask));
        }

        int bookDec = FootprintRendererShim.Decimals(maxBook, inst.VolumeDecimals);
        int volDec = FootprintRendererShim.Decimals(maxVol, inst.VolumeDecimals);
        int hoverRow = mouseY is float my && my > _headerH ? (int)((my - _headerH) / _rowH) : -1;

        // fondos de fila
        var bidBg = C(s.BidBackground);
        var askBg = C(s.AskBackground);
        var spreadBg = C(s.SpreadBackground);
        for (int i = 0; i < drawRows; i++)
        {
            long r = _topRow - i;
            float y = _headerH + i * _rowH;
            _fill.Color = r >= bestAskRow ? askBg : r <= bestBidRow ? bidBg : spreadBg;
            canvas.DrawRect(0, y, width, _rowH, _fill);
            if (i != hoverRow) continue;
            _fill.Color = new SKColor(255, 255, 255, 18);
            canvas.DrawRect(0, y, width, _rowH, _fill);
        }

        var bidColor = C(s.BidColor);
        var askColor = C(s.AskColor);
        var buyVol = C(s.BuyVolumeColor);
        var sellVol = C(s.SellVolumeColor);
        var lastColor = C(s.LastPriceColor);
        var priceText = C(s.PriceText);
        var large = lastColor;
        var pos = dom.Broker?.Position;
        long posRow = pos != null && pos.Volume != 0 ? dom.RowOf(inst.ToTicks(pos.AveragePrice)) : long.MinValue;

        foreach (var (col, x, w) in _cols)
        {
            canvas.Save();
            canvas.ClipRect(new SKRect(x, _headerH, x + w, height));
            for (int i = 0; i < drawRows; i++)
            {
                long r = _topRow - i;
                float y = _headerH + i * _rowH;
                switch (col)
                {
                    case DomColumn.Bid when r <= bestBidRow && bids.TryGetValue(r, out var bsz) && bsz > 0:
                        BookCell(canvas, x, y, w, bsz, maxBook, bidColor, r == bestBidRow, rightAligned: true, s, bookDec, font, bold, large);
                        break;
                    case DomColumn.Ask when r >= bestAskRow && asks.TryGetValue(r, out var asz) && asz > 0:
                        BookCell(canvas, x, y, w, asz, maxBook, askColor, r == bestAskRow, rightAligned: false, s, bookDec, font, bold, large);
                        break;
                    case DomColumn.Price:
                    {
                        decimal price = r * dom.Step * inst.TickSize;
                        bool isLast = r == lastRow;
                        if (isLast)
                        {
                            _fill.Color = lastColor;
                            canvas.DrawRect(x + 1, y + 1, w - 2, _rowH - 2, _fill);
                        }

                        bool round = price % (inst.TickSize * dom.Step * 10) == 0;
                        Text(canvas, inst.FormatPrice(price), x, y, w, isLast ? bold : font, isLast ? SKColors.Black : round ? t.Text : priceText, 0);
                        break;
                    }
                    case DomColumn.Volume when dom.SessionVolume.TryGetValue(r, out var sv) && maxVol > 0:
                    {
                        decimal tot = sv.Bid + sv.Ask;
                        float vw = (float)(tot / maxVol) * (w - 4);
                        float bidW = tot > 0 ? vw * (float)(sv.Bid / tot) : 0;
                        _fill.Color = sellVol;
                        canvas.DrawRect(x + 2, y + 2, bidW, _rowH - 4, _fill);
                        _fill.Color = buyVol;
                        canvas.DrawRect(x + 2 + bidW, y + 2, vw - bidW, _rowH - 4, _fill);
                        Text(canvas, VolumeFormat.Compact(tot, volDec), x, y, w, small, t.Text, -1);
                        break;
                    }
                    case DomColumn.Delta when dom.SessionVolume.TryGetValue(r, out var sv) && maxDelta > 0:
                    {
                        decimal d = sv.Ask - sv.Bid;
                        if (d == 0) break;
                        float dw = (float)(Math.Abs(d) / maxDelta) * (w - 4);
                        _fill.Color = d > 0 ? buyVol : sellVol;
                        canvas.DrawRect(x + 2, y + 2, dw, _rowH - 4, _fill);
                        Text(canvas, (d > 0 ? "+" : "") + VolumeFormat.Compact(d, volDec), x, y, w, small, t.Text, -1);
                        break;
                    }
                    case DomColumn.TradedBid when dom.SessionVolume.TryGetValue(r, out var sv) && sv.Bid > 0 && maxSide > 0:
                    {
                        float bw = (float)(sv.Bid / maxSide) * (w - 4);
                        _fill.Color = sellVol;
                        canvas.DrawRect(x + w - 2 - bw, y + 2, bw, _rowH - 4, _fill);
                        Text(canvas, VolumeFormat.Compact(sv.Bid, volDec), x, y, w, small, t.Text, 1);
                        break;
                    }
                    case DomColumn.TradedAsk when dom.SessionVolume.TryGetValue(r, out var sv) && sv.Ask > 0 && maxSide > 0:
                    {
                        float aw = (float)(sv.Ask / maxSide) * (w - 4);
                        _fill.Color = buyVol;
                        canvas.DrawRect(x + 2, y + 2, aw, _rowH - 4, _fill);
                        Text(canvas, VolumeFormat.Compact(sv.Ask, volDec), x, y, w, small, t.Text, -1);
                        break;
                    }
                }
            }

            canvas.Restore();
        }

        // posición: recuadro en el precio promedio
        if (posRow != long.MinValue)
        {
            int i = (int)(_topRow - posRow);
            if (i >= 0 && i < drawRows)
            {
                float y = _headerH + i * _rowH;
                float px = 0, pw = width;
                if (TryColumn(DomColumn.Price, out var cx, out var cw))
                {
                    px = cx;
                    pw = cw;
                }

                _stroke.Color = pos!.Volume > 0 ? t.PositionLong : t.PositionShort;
                _stroke.StrokeWidth = 2;
                canvas.DrawRect(px + 1.5f, y + 1.5f, pw - 3, _rowH - 3, _stroke);
                _stroke.StrokeWidth = 1;
            }
        }

        // órdenes propias
        var orders = dom.Broker?.ActiveOrders.ToList() ?? new List<Order>();
        foreach (var o in orders)
        {
            bool dragging = ReferenceEquals(o, dom.DragOrder);
            long orow = dom.RowOf(inst.ToTicks(dragging ? dom.DragPrice : o.Type == OrderTypes.Stop ? o.TriggerPrice : o.Price));
            int i = (int)(_topRow - orow);
            if (i < 0 || i >= drawRows) continue;
            float y = _headerH + i * _rowH;
            if (!TryColumn(OrdersColumn(o.Direction), out var x, out var w)) continue;
            canvas.Save();
            canvas.ClipRect(new SKRect(x, _headerH, x + w, height));
            var role = dom.Brackets?.RoleOf(o) ?? BracketRole.None;
            var color = role switch
            {
                BracketRole.StopLoss => new SKColor(240, 82, 90),
                BracketRole.Target => new SKColor(38, 194, 129),
                _ => o.Direction == OrderDirections.Buy ? t.BuyOrder : t.SellOrder,
            };
            _fill.Color = color.WithAlpha(dragging ? (byte)150 : (byte)255);
            canvas.DrawRoundRect(new SKRect(x + 2, y + 2, x + w - 2, y + _rowH - 2), 3, 3, _fill);
            string prefix = role switch { BracketRole.StopLoss => "SL ", BracketRole.Target => "TP ", _ => o.Type == OrderTypes.Stop ? "S " : "L " };
            string label = prefix + VolumeFormat.Compact(o.Unfilled, inst.VolumeDecimals);
            Text(canvas, label, x, y, w, small, SKColors.Black, 0);
            canvas.Restore();
        }

        // líneas de columnas
        _stroke.Color = new SKColor(40, 46, 60);
        foreach (var (_, x, _) in _cols.Skip(1))
        {
            float xx = MathF.Floor(x) + 0.5f;
            canvas.DrawLine(xx, 0, xx, height, _stroke);
        }

        if (s.ShowHeader) DrawHeader(canvas, header);
    }

    private void BookCell(SKCanvas canvas, float x, float y, float w, decimal size, decimal max, SKColor color, bool best, bool rightAligned,
        DomSettings s, int decimals, SKFont font, SKFont bold, SKColor large)
    {
        if (s.BookBars)
        {
            _fill.Color = color.WithAlpha(best ? (byte)230 : (byte)150);
            float bw = max > 0 ? (float)(size / max) * (w - 2) : 0;
            canvas.DrawRect(rightAligned ? x + w - 1 - bw : x + 1, y + 1, bw, _rowH - 2, _fill);
        }
        else if (best)
        {
            _fill.Color = color.WithAlpha(120);
            canvas.DrawRect(x + 1, y + 1, w - 2, _rowH - 2, _fill);
        }

        bool isLarge = s.LargeSize > 0 && size >= s.LargeSize;
        if (isLarge)
        {
            _stroke.Color = large;
            _stroke.StrokeWidth = 1.5f;
            canvas.DrawRect(x + 1.5f, y + 1.5f, w - 3, _rowH - 3, _stroke);
            _stroke.StrokeWidth = 1;
        }

        Text(canvas, VolumeFormat.Compact(size, decimals), x, y, w, best || isLarge ? bold : font, SKColors.White, rightAligned ? 1 : -1);
    }

    private void Text(SKCanvas canvas, string s, float x, float y, float w, SKFont f, SKColor color, int align)
    {
        float tw = RenderContext.MeasureWidth(f, s);
        float tx = align < 0 ? x + 4 : align > 0 ? x + w - 4 - tw : x + (w - tw) / 2;
        float ty = y + _rowH / 2 - (f.Metrics.Ascent + f.Metrics.Descent) / 2;
        _text.Color = color;
        canvas.DrawUiText(s, tx, ty, f, _text);
    }

    private void DrawHeader(SKCanvas canvas, SKFont font)
    {
        var t = Theme;
        _fill.Color = t.AxisBackground;
        canvas.DrawRect(0, 0, _width, _headerH, _fill);
        float baseline = _headerH / 2 - (font.Metrics.Ascent + font.Metrics.Descent) / 2;
        foreach (var (col, x, w) in _cols)
        {
            // el rótulo se abrevia según el ancho y nunca se sale de su columna
            var label = FitLabel(col, w, s => RenderContext.MeasureWidth(font, s));
            if (label == null) continue;
            canvas.Save();
            canvas.ClipRect(new SKRect(x, 0, x + w, _headerH));
            _text.Color = t.TextDim;
            canvas.DrawUiText(label, x + w / 2 - RenderContext.MeasureWidth(font, label) / 2, baseline, font, _text);
            canvas.Restore();
        }

        _stroke.Color = new SKColor(40, 46, 60);
        canvas.DrawLine(0, _headerH - 0.5f, _width, _headerH - 0.5f, _stroke);
    }

    /// <summary>Qué hay bajo el mouse: columna y precio.</summary>
    public (DomColumn Column, decimal Price)? HitTest(DomModel dom, float x, float y)
    {
        if (y < _headerH || _width <= 0) return null;
        int i = (int)((y - _headerH) / _rowH);
        long r = _topRow - i;
        decimal price = r * dom.Step * dom.Instrument.TickSize;
        foreach (var (c, cx, cw) in _cols)
            if (x >= cx && x < cx + cw) return (c, price);
        return null;
    }

    /// <summary>Orden propia dibujada en la celda (columna de órdenes de su lado, mismo precio).</summary>
    public Order? OrderAt(DomModel dom, float x, float y)
    {
        var hit = HitTest(dom, x, y);
        if (hit == null || dom.Broker == null) return null;
        long row = dom.RowOf(dom.Instrument.ToTicks(hit.Value.Price));
        foreach (var dir in new[] { OrderDirections.Buy, OrderDirections.Sell })
        {
            if (OrdersColumn(dir) != hit.Value.Column) continue;
            var o = dom.Broker.ActiveOrders.FirstOrDefault(o => o.Direction == dir && dom.RowOf(dom.Instrument.ToTicks(o.Type == OrderTypes.Stop ? o.TriggerPrice : o.Price)) == row);
            if (o != null) return o;
        }

        return null;
    }

    /// <summary>Clic en el DOM: opera directo en el broker (sin confirmación).</summary>
    public string? Click(DomModel dom, float x, float y, bool right, bool cancelModifier)
    {
        var hit = HitTest(dom, x, y);
        var broker = dom.Broker;
        if (hit == null || broker == null) return null;
        var (col, price) = hit.Value;
        var book = dom.Data.Depth;
        decimal bestBid = book.HasBid ? dom.Instrument.FromTicks(book.BestBidTicks) : dom.Data.LastPrice;
        decimal bestAsk = book.HasAsk ? dom.Instrument.FromTicks(book.BestAskTicks) : dom.Data.LastPrice;
        decimal qty = dom.OrderQuantity;

        if (right || cancelModifier)
        {
            broker.CancelAt(price);
            return $"Órdenes canceladas en {dom.Instrument.FormatPrice(price)}";
        }

        switch (col)
        {
            case DomColumn.Bid:
            case DomColumn.BuyOrders:
                if (price < bestAsk) broker.Limit(OrderDirections.Buy, qty, price);
                else broker.Stop(OrderDirections.Buy, qty, price);
                return $"Compra {(price < bestAsk ? "límite" : "stop")} {qty} @ {dom.Instrument.FormatPrice(price)}";
            case DomColumn.Ask:
            case DomColumn.SellOrders:
                if (price > bestBid) broker.Limit(OrderDirections.Sell, qty, price);
                else broker.Stop(OrderDirections.Sell, qty, price);
                return $"Venta {(price > bestBid ? "límite" : "stop")} {qty} @ {dom.Instrument.FormatPrice(price)}";
        }

        return null;
    }

    public void Scroll(DomModel dom, int rows)
    {
        dom.AutoCenter = false;
        long center = dom.RowOf(dom.CenterTicks) + rows;
        dom.CenterTicks = center * dom.Step;
    }
}

internal static class FootprintRendererShim
{
    public static int Decimals(decimal max, int instrumentDecimals) => Rendering.FootprintRenderer.ChooseDecimals(max, instrumentDecimals);
}
