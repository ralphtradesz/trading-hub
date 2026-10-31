using SkiaSharp;
using TradingHub.Data;
using TradingHub.Indicators;
using TradingHub.Rendering.Context;
using TradingHub.Rendering;

namespace TradingHub.Charting.Rendering;

/// <summary>
/// Dibuja el footprint componiendo, dentro de cada vela, las columnas que eligió el usuario
/// (escaleras, perfiles, celdas, vela finita, espacios). Ver <see cref="FootprintLayout"/>.
/// </summary>
internal sealed class FootprintRenderer
{
    private readonly SKPaint _fill = new() { IsAntialias = false, Style = SKPaintStyle.Fill };
    private readonly SKPaint _stroke = new() { IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
    private readonly SKPaint _text = new() { IsAntialias = true, Style = SKPaintStyle.Fill };

    private const float ColumnGap = 2;

    /// <summary>Máximos usados para escalar colores y barras.</summary>
    private struct Stats
    {
        public decimal MaxVol, MaxAbsDelta, MaxSide, MaxTrades;

        public void Take(in LevelData d)
        {
            if (d.Volume > MaxVol) MaxVol = d.Volume;
            var ad = Math.Abs(d.Delta);
            if (ad > MaxAbsDelta) MaxAbsDelta = ad;
            if (d.Bid > MaxSide) MaxSide = d.Bid;
            if (d.Ask > MaxSide) MaxSide = d.Ask;
            if (d.Trades > MaxTrades) MaxTrades = d.Trades;
        }
    }

    /// <summary>Contexto de dibujo de una vela.</summary>
    private sealed class Ctx
    {
        public ChartModel Model = null!;
        public PanelLayout Main = null!;
        public CandleSeries Series = null!;
        public FootprintLayout L = null!;
        public ChartTheme T = null!;
        public TradingHub.Data.Candle C = null!;
        public Stats S;
        public double RowH;
        public long MinRow, MaxRow;

        // filas agregadas (factor K de filas base por fila visible)
        public int K = 1;
        public LevelData[] Agg = new LevelData[64];
        public long AggMin, AggMax;
        public long PocRow;
        public decimal PocVolume;

        public LevelData Get(long r) => r < AggMin || r > AggMax ? default : Agg[r - AggMin];

        /// <summary>Fila agregada de un precio en ticks.</summary>
        public long RowOfTicks(long ticks) => CandleSeries.FloorDiv(ticks, (long)Series.PriceStep * K);

        /// <summary>Centro visual de una fila agregada.</summary>
        public decimal Center(long r)
        {
            long step = (long)Series.PriceStep * K;
            return (r * step + (step - 1) / 2m) * Series.Instrument.TickSize;
        }

        /// <summary>Agrupa los niveles de la vela en filas de K filas base.</summary>
        public void Aggregate(TradingHub.Data.Candle c)
        {
            var lv = c.Levels;
            PocRow = 0;
            PocVolume = 0;
            if (lv.IsEmpty)
            {
                AggMin = 0;
                AggMax = -1;
                return;
            }

            AggMin = CandleSeries.FloorDiv(lv.MinRow, K);
            AggMax = CandleSeries.FloorDiv(lv.MaxRow, K);
            int n = (int)(AggMax - AggMin + 1);
            if (Agg.Length < n) Agg = new LevelData[Math.Max(n, Agg.Length * 2)];
            Array.Clear(Agg, 0, n);
            foreach (var (row, d) in lv.Enumerate())
            {
                ref var a = ref Agg[CandleSeries.FloorDiv(row, K) - AggMin];
                a.Bid += d.Bid;
                a.Ask += d.Ask;
                a.Between += d.Between;
                a.Trades += d.Trades;
                if (d.LastTimeTicks > a.LastTimeTicks) a.LastTimeTicks = d.LastTimeTicks;
            }

            for (int i = 0; i < n; i++)
            {
                var v = Agg[i].Volume;
                if (v > PocVolume)
                {
                    PocVolume = v;
                    PocRow = AggMin + i;
                }
            }
        }
        public int Decimals;
        public SKFont Font = null!, Bold = null!, Small = null!;
        public float BaseOff;
        public float GapY;
        public decimal VolumeStep;
    }

    public bool CanShowClusters(ChartModel model, PanelLayout main, double barWidth) => barWidth >= 6 && main.RowHeight > 0;

    public void Render(SKCanvas canvas, ChartModel model, ChartLayout layout, PanelLayout main, CandleRenderer candles, HashSet<int>? hiddenBars = null)
    {
        var series = model.Series;
        if (series.Count == 0) return;
        var L = model.Settings.Layout;
        if (L.Columns.Count == 0) L = FootprintLayout.Presets[0];
        var t = model.Theme;

        double baseRowH = main.RowHeight;
        float slotW = (float)(layout.BarWidth - layout.BarSpacing);

        // cuántas filas base se agrupan en cada fila visible
        int k;
        int userStep = model.Settings.PriceStep;
        if (userStep > 0) k = Math.Max(1, userStep / Math.Max(1, series.PriceStep));
        else
        {
            const double targetRowPx = 14;
            k = CandleSeries.NiceStep((long)Math.Ceiling(targetRowPx / Math.Max(0.01, baseRowH)));
        }

        layout.FootprintRowFactor = k;
        double rowH = baseRowH * k;

        float fontPx = (float)Math.Clamp(rowH * 0.70, 7, 13);
        var ctx = new Ctx
        {
            Model = model,
            Main = main,
            Series = series,
            L = L,
            T = t,
            RowH = rowH,
            Font = RenderContext.GetFont(t.FontFamily, fontPx, false, false),
            Bold = RenderContext.GetFont(t.FontFamily, fontPx, true, false),
            Small = RenderContext.GetFont(t.FontFamily, Math.Max(7, fontPx - 1.5f), false, false),
            GapY = rowH > 7 ? 1 : 0,
            VolumeStep = model.Instrument.VolumeStep,
            K = k,
        };

        // máximos sobre lo visible (si los colores no son por vela) — sobre las filas agregadas
        var visible = new Stats();
        decimal visMaxVol = 0;
        for (int i = Math.Max(0, layout.FirstVisible); i <= Math.Min(layout.LastVisible, series.Count - 1); i++)
        {
            ctx.Aggregate(series[i]);
            for (long r = ctx.AggMin; r <= ctx.AggMax; r++) visible.Take(ctx.Agg[r - ctx.AggMin]);
            visMaxVol = Math.Max(visMaxVol, ctx.PocVolume);
        }

        ctx.Decimals = ChooseDecimals(visMaxVol, model.Instrument.VolumeDecimals);
        ctx.BaseOff = -ctx.Font.Metrics.Ascent / 2f - ctx.Font.Metrics.Descent / 2f;
        int totDecimals = ChooseDecimals(visMaxVol * 20, model.Instrument.VolumeDecimals);
        var totalsFont = RenderContext.GetFont(t.FontFamily, Math.Clamp(fontPx + 1.5f, 9, 13), true, false);

        long topRow = ctx.RowOfTicks(model.Instrument.ToTicks((decimal)main.Top)) + 1;
        long botRow = ctx.RowOfTicks(model.Instrument.ToTicks((decimal)main.Bottom)) - 1;
        var rects = new (float Left, float Right)[L.Columns.Count];

        for (int i = Math.Max(0, layout.FirstVisible); i <= Math.Min(layout.LastVisible, series.Count - 1); i++)
        {
            var c = series[i];
            if (c.Trades == 0 || (hiddenBars != null && hiddenBars.Contains(i))) continue;
            float slotLeft = layout.LeftX(i) + layout.BarSpacing / 2f;
            ComputeColumns(L, slotLeft, slotW, rects);

            ctx.C = c;
            if (c.IsSynthetic)
            {
                // historia en velas (sin ticks): la misma vela finita del footprint, sin clusters inventados
                bool drawn = false;
                for (int ci = 0; ci < L.Columns.Count; ci++)
                {
                    if (L.Columns[ci].Kind != FootprintColumnKind.Candle) continue;
                    DrawCandleColumn(canvas, ctx, rects[ci].Left, rects[ci].Right - rects[ci].Left);
                    drawn = true;
                }

                if (!drawn)
                {
                    float cw = Math.Clamp(slotW * 0.06f, 3, 7);
                    DrawCandleColumn(canvas, ctx, slotLeft + slotW / 2 - cw / 2, cw);
                }

                DrawTotals(canvas, ctx, c, slotLeft, slotW, totalsFont, totDecimals, rowH);
                continue;
            }

            ctx.Aggregate(c);
            ctx.MinRow = Math.Max(ctx.AggMin, botRow);
            ctx.MaxRow = Math.Min(ctx.AggMax, topRow);
            if (L.ProportionPerCandle)
            {
                var st = new Stats();
                for (long r = ctx.AggMin; r <= ctx.AggMax; r++) st.Take(ctx.Agg[r - ctx.AggMin]);
                ctx.S = st;
            }
            else ctx.S = visible;

            for (int ci = 0; ci < L.Columns.Count; ci++)
            {
                var col = L.Columns[ci];
                var (left, right) = rects[ci];
                if (right - left < 1) continue;
                switch (col.Kind)
                {
                    case FootprintColumnKind.Candle:
                        DrawCandleColumn(canvas, ctx, left, right - left);
                        break;
                    case FootprintColumnKind.Cells:
                        DrawCells(canvas, ctx, col, left, right);
                        break;
                    case FootprintColumnKind.Ladder:
                        DrawLadder(canvas, ctx, col, left, right);
                        break;
                    case FootprintColumnKind.Profile:
                        DrawProfile(canvas, ctx, col, left, right);
                        break;
                }
            }

            if (model.Settings.ShowValueArea) DrawValueArea(canvas, ctx, slotLeft + slotW - 3, slotLeft + slotW);

            DrawTotals(canvas, ctx, c, slotLeft, slotW, totalsFont, totDecimals, rowH);
        }
    }

    /// <summary>Delta arriba y volumen abajo de la vela.</summary>
    private void DrawTotals(SKCanvas canvas, Ctx ctx, TradingHub.Data.Candle c, float slotLeft, float slotW, SKFont totalsFont, int totDecimals, double rowH)
    {
        if (!ctx.L.ShowTotals || slotW < 36) return;
        var main = ctx.Main;
        var t = ctx.T;
        float yHigh = (float)(main.Y(ctx.Center(ctx.RowOfTicks(c.HighTicks))) - rowH / 2);
        float yLow = (float)(main.Y(ctx.Center(ctx.RowOfTicks(c.LowTicks))) + rowH / 2);
        string dTxt = (c.Delta > 0 ? "+" : "") + VolumeFormat.Compact(c.Delta, totDecimals);
        _text.Color = c.Delta >= 0 ? t.DeltaPositiveText : t.DeltaNegativeText;
        float w = RenderContext.MeasureWidth(totalsFont, dTxt);
        canvas.DrawUiText(dTxt, slotLeft + slotW / 2 - w / 2, yHigh - 6, totalsFont, _text);
        string vTxt = VolumeFormat.Compact(c.Volume, totDecimals);
        _text.Color = t.Text;
        w = RenderContext.MeasureWidth(totalsFont, vTxt);
        canvas.DrawUiText(vTxt, slotLeft + slotW / 2 - w / 2, yLow + 6 - totalsFont.Metrics.Ascent, totalsFont, _text);
    }

    /// <summary>Reparte el ancho de la vela entre las columnas.</summary>
    private static void ComputeColumns(FootprintLayout L, float slotLeft, float slotW, (float, float)[] rects)
    {
        int n = L.Columns.Count;
        float candleW = Math.Clamp(slotW * 0.06f, 3, 7);
        float fixedW = 0;
        double weights = 0;
        int gaps = 0;
        foreach (var c in L.Columns)
        {
            if (c.Kind == FootprintColumnKind.Candle) fixedW += candleW;
            else weights += Math.Max(0.05, c.Width);
        }

        gaps = Math.Max(0, n - 1);
        float flexW = Math.Max(0, slotW - fixedW - gaps * ColumnGap);
        float x = slotLeft;
        for (int k = 0; k < n; k++)
        {
            var c = L.Columns[k];
            float w = c.Kind == FootprintColumnKind.Candle ? candleW : (float)(flexW * Math.Max(0.05, c.Width) / Math.Max(0.0001, weights));
            rects[k] = (x, x + w);
            x += w + ColumnGap;
        }
    }

    // ===================================================================== columnas

    private void DrawCandleColumn(SKCanvas canvas, Ctx x, float left, float w)
    {
        var c = x.C;
        var main = x.Main;
        double rowH = x.RowH;
        float yHigh = (float)(main.Y(x.Center(x.RowOfTicks(c.HighTicks))) - rowH / 2);
        float yLow = (float)(main.Y(x.Center(x.RowOfTicks(c.LowTicks))) + rowH / 2);
        float yO = main.Y(x.Center(x.RowOfTicks(c.OpenTicks))), yC = main.Y(x.Center(x.RowOfTicks(c.CloseTicks)));
        bool up = c.CloseTicks >= c.OpenTicks;
        float cx = MathF.Floor(left + w / 2) + 0.5f;
        _stroke.Color = x.T.Wick;
        _stroke.StrokeWidth = 1;
        canvas.DrawLine(cx, yHigh, cx, yLow, _stroke);
        _fill.Color = up ? x.T.UpCandle : x.T.DownCandle;
        canvas.DrawRect(new SKRect(left, (float)(Math.Min(yO, yC) - rowH / 2), left + w, (float)(Math.Max(yO, yC) + rowH / 2)), _fill);
    }

    private void DrawCells(SKCanvas canvas, Ctx x, FootprintColumn col, float left, float right)
    {
        var t = x.T;
                float w = right - left, mid = left + w / 2;
        bool showText = x.RowH >= 9.5 && w >= MinTextWidth(col.Value) * 0.75f;
        for (long r = x.MinRow; r <= x.MaxRow; r++)
        {
            var d = x.Get(r);
            if (d.IsEmpty) continue;
            float yc = x.Main.Y(x.Center(r));
            var cell = new SKRect(left, (float)(yc - x.RowH / 2) + x.GapY / 2, right, (float)(yc + x.RowH / 2) - x.GapY / 2);
            CellBackground(canvas, x, col, cell, d);
            if (!showText) continue;
            var (askImb, bidImb) = Imbalance(x, r, d);
            bool isPoc = r == x.PocRow;
            DrawValueText(canvas, x, col, d, cell, yc + x.BaseOff, askImb && x.L.ShowImbalances, bidImb && x.L.ShowImbalances, isPoc, ColumnTextAlign.Center, false);
        }

        if (x.MaxRow < x.MinRow) return;
        float yTop = (float)(x.Main.Y(x.Center(x.MaxRow)) - x.RowH / 2);
        float yBot = (float)(x.Main.Y(x.Center(x.MinRow)) + x.RowH / 2);
        if (x.RowH >= 4 && w >= 10)
        {
            _stroke.Color = t.CellBorder;
            _stroke.StrokeWidth = 1;
            canvas.DrawRect(new SKRect(left + 0.5f, yTop + 0.5f, right - 0.5f, yBot - 0.5f), _stroke);
        }

        if (x.L.ShowPoc && x.PocRow >= x.MinRow && x.PocRow <= x.MaxRow && x.PocVolume > 0)
        {
            float yc = x.Main.Y(x.Center(x.PocRow));
            _stroke.Color = t.Poc;
            _stroke.StrokeWidth = x.RowH >= 8 ? 1.5f : 1f;
            canvas.DrawRect(new SKRect(left + 0.75f, (float)(yc - x.RowH / 2) + 0.75f, right - 0.75f, (float)(yc + x.RowH / 2) - 0.75f), _stroke);
        }
    }

    private void DrawLadder(SKCanvas canvas, Ctx x, FootprintColumn col, float left, float right)
    {
        var t = x.T;
                float w = right - left;
        bool showText = x.RowH >= 7.5 && w >= MinTextWidth(col.Value) * 0.6f;
        var pos = ColorOr(col.PositiveColor, t.LadderPositive);
        var neg = ColorOr(col.NegativeColor, t.LadderNegative);
        var neu = ColorOr(col.NeutralColor, t.ClusterBase);
        for (long r = x.MinRow; r <= x.MaxRow; r++)
        {
            var d = x.Get(r);
            if (d.IsEmpty) continue;
            float yc = x.Main.Y(x.Center(r));
            int hl = Highlight(x, col.Highlight, r, d, col.Value);
            if (hl != 0)
            {
                _fill.Color = hl == 1 ? pos : hl == -1 ? neg : neu;
                canvas.DrawRect(new SKRect(left, (float)(yc - x.RowH / 2) + x.GapY / 2, right, (float)(yc + x.RowH / 2) - x.GapY / 2), _fill);
            }

            if (!showText) continue;
            var (askImb, bidImb) = col.Value == FootprintValue.BidAsk ? Imbalance(x, r, d) : (false, false);
            var cell = new SKRect(left, (float)(yc - x.RowH / 2), right, (float)(yc + x.RowH / 2));
            DrawValueText(canvas, x, col, d, cell, yc + x.BaseOff, askImb && x.L.ShowImbalances, bidImb && x.L.ShowImbalances, r == x.PocRow, col.Align, hl != 0);
        }
    }

    private void DrawProfile(SKCanvas canvas, Ctx x, FootprintColumn col, float left, float right)
    {
        var t = x.T;
                var st = x.S;
        float w = right - left, mid = left + w / 2;
        var bidCol = ColorOr(col.NegativeColor, t.Bid).WithAlpha(190);
        var askCol = ColorOr(col.PositiveColor, t.Ask).WithAlpha(190);
        var neutral = ColorOr(col.NeutralColor, t.ProfileBar);
        var pos = ColorOr(col.PositiveColor, t.LadderPositive);
        var neg = ColorOr(col.NegativeColor, t.LadderNegative);
        bool numbers = col.ShowNumbers && x.RowH >= 7.5 && w >= 26;

        for (long r = x.MinRow; r <= x.MaxRow; r++)
        {
            var d = x.Get(r);
            if (d.IsEmpty) continue;
            float yc = x.Main.Y(x.Center(r));
            float top = (float)(yc - x.RowH / 2) + x.GapY / 2, bottom = (float)(yc + x.RowH / 2) - x.GapY / 2;
            int hl = Highlight(x, col.Highlight, r, d, col.Value);

            void Bar(float frac, SKColor color, bool fromRight)
            {
                float len = Math.Max(1, w * Math.Clamp(frac, 0, 1));
                _fill.Color = color;
                canvas.DrawRect(fromRight ? new SKRect(right - len, top, right, bottom) : new SKRect(left, top, left + len, bottom), _fill);
            }

            bool fromRightSide = col.Direction == ProfileDirection.RightToLeft;
            switch (col.Value)
            {
                case FootprintValue.Delta:
                {
                    float frac = Frac(Math.Abs(d.Delta), st.MaxAbsDelta);
                    var color = d.Delta >= 0 ? askCol : bidCol;
                    if (col.Direction == ProfileDirection.Centered)
                    {
                        float len = Math.Max(1, w / 2 * frac);
                        _fill.Color = color;
                        canvas.DrawRect(d.Delta >= 0 ? new SKRect(mid, top, mid + len, bottom) : new SKRect(mid - len, top, mid, bottom), _fill);
                    }
                    else Bar(frac, color, fromRightSide);

                    break;
                }
                case FootprintValue.Bid:
                    Bar(Frac(d.Bid, st.MaxSide), hl != 0 ? HighlightColor(hl, pos, neg, neutral) : bidCol, fromRightSide);
                    break;
                case FootprintValue.Ask:
                    Bar(Frac(d.Ask, st.MaxSide), hl != 0 ? HighlightColor(hl, pos, neg, neutral) : askCol, fromRightSide);
                    break;
                case FootprintValue.BidAsk:
                {
                    if (col.Direction == ProfileDirection.Centered)
                    {
                        float fb = Frac(d.Bid, st.MaxSide), fa = Frac(d.Ask, st.MaxSide);
                        _fill.Color = bidCol;
                        canvas.DrawRect(new SKRect(mid - w / 2 * fb, top, mid, bottom), _fill);
                        _fill.Color = askCol;
                        canvas.DrawRect(new SKRect(mid, top, mid + w / 2 * fa, bottom), _fill);
                    }
                    else
                    {
                        float total = w * Frac(d.Volume, st.MaxVol);
                        float bw = d.Volume > 0 ? total * (float)(d.Bid / d.Volume) : 0;
                        if (fromRightSide)
                        {
                            _fill.Color = askCol;
                            canvas.DrawRect(new SKRect(right - total, top, right - total + (total - bw), bottom), _fill);
                            _fill.Color = bidCol;
                            canvas.DrawRect(new SKRect(right - bw, top, right, bottom), _fill);
                        }
                        else
                        {
                            _fill.Color = bidCol;
                            canvas.DrawRect(new SKRect(left, top, left + bw, bottom), _fill);
                            _fill.Color = askCol;
                            canvas.DrawRect(new SKRect(left + bw, top, left + total, bottom), _fill);
                        }
                    }

                    break;
                }
                case FootprintValue.Trades:
                    Bar(Frac(d.Trades, st.MaxTrades), hl != 0 ? HighlightColor(hl, pos, neg, neutral) : neutral, fromRightSide);
                    break;
                default:
                {
                    var color = hl == 1 ? Mix(neutral, pos, 0.75f)
                        : hl == -1 ? Mix(neutral, neg, 0.75f)
                        : hl == 2 ? t.ProfileBarStrong
                        : r == x.PocRow ? t.ProfileBarStrong : neutral;
                    Bar(Frac(d.Volume, st.MaxVol), color, fromRightSide);
                    break;
                }
            }

            if (numbers)
            {
                var shown = col.Value == FootprintValue.BidAsk ? FootprintValue.Volume : col.Value;
                string txt = ValueString(shown, d, x.Decimals);
                uint? own = shown switch
                {
                    FootprintValue.Delta => (d.Delta >= 0 ? col.PositiveTextColor : col.NegativeTextColor) ?? col.TextColor,
                    FootprintValue.Bid => col.BidTextColor ?? col.TextColor,
                    FootprintValue.Ask => col.AskTextColor ?? col.TextColor,
                    _ => col.TextColor,
                };
                _text.Color = ColorOr(own, SKColors.White.WithAlpha(220));
                float tw = RenderContext.MeasureWidth(x.Small, txt);
                float tx = col.Direction == ProfileDirection.RightToLeft ? right - 2 - tw : col.Direction == ProfileDirection.Centered ? mid - tw / 2 : left + 2;
                canvas.DrawUiText(txt, tx, yc + x.BaseOff, x.Small, _text);
            }
        }
    }

    private void DrawValueArea(SKCanvas canvas, Ctx x, float left, float right)
    {
        var (hi, lo) = ValueAreaRows(x);
        _fill.Color = x.T.ValueArea.WithAlpha(120);
        for (long r = Math.Max(lo, x.MinRow); r <= Math.Min(hi, x.MaxRow); r++)
        {
            float yc = x.Main.Y(x.Center(r));
            canvas.DrawRect(new SKRect(left, (float)(yc - x.RowH / 2), right, (float)(yc + x.RowH / 2)), _fill);
        }
    }

    // ===================================================================== texto y colores

    private void DrawValueText(SKCanvas canvas, Ctx x, FootprintColumn col, in LevelData d, SKRect cell, float baseline,
        bool askImb, bool bidImb, bool isPoc, ColumnTextAlign align, bool highlighted)
    {
        var t = x.T;
        // color del texto: el propio de la columna (si lo elegiste) o el del tema; en niveles resaltados, blanco o el elegido
        var themeText = isPoc && col.Kind == FootprintColumnKind.Cells ? t.ClusterTextStrong : (col.Kind == FootprintColumnKind.Ladder ? t.LadderText : t.ClusterText);
        var normal = highlighted ? ColorOr(col.HighlightTextColor, SKColors.White) : ColorOr(col.TextColor, themeText);
        var dim = normal.WithAlpha(110);
        var f = isPoc || highlighted ? x.Bold : x.Font;

        if (col.Value == FootprintValue.BidAsk)
        {
            string b = VolumeFormat.Compact(d.Bid, x.Decimals), a = VolumeFormat.Compact(d.Ask, x.Decimals);
            float mid = align switch
            {
                ColumnTextAlign.Left => cell.Left + 4 + RenderContext.MeasureWidth(x.Font, b) + 4,
                ColumnTextAlign.Right => cell.Right - 4 - RenderContext.MeasureWidth(x.Font, a) - 4,
                _ => cell.MidX,
            };
            var fb = bidImb ? x.Bold : f;
            var fa = askImb ? x.Bold : f;
            var bidText = highlighted ? normal : ColorOr(col.BidTextColor, normal);
            var askText = highlighted ? normal : ColorOr(col.AskTextColor, normal);
            _text.Color = bidImb ? t.ImbalanceBid : (d.Bid == 0 ? bidText.WithAlpha(110) : bidText);
            canvas.DrawUiText(b, mid - 4 - RenderContext.MeasureWidth(fb, b), baseline, fb, _text);
            _text.Color = askImb ? t.ImbalanceAsk : (d.Ask == 0 ? askText.WithAlpha(110) : askText);
            canvas.DrawUiText(a, mid + 4, baseline, fa, _text);
            if (cell.Width >= 70)
            {
                _text.Color = normal.WithAlpha(70);
                canvas.DrawUiText("x", mid - RenderContext.MeasureWidth(x.Font, "x") / 2, baseline, x.Font, _text);
            }

            return;
        }

        string txt = ValueString(col.Value, d, x.Decimals);
        if (!highlighted)
        {
            bool ladder = col.Kind == FootprintColumnKind.Ladder, own = col.TextColor.HasValue;
            _text.Color = col.Value switch
            {
                FootprintValue.Delta => d.Delta >= 0 ? ColorOr(col.PositiveTextColor, own ? normal : t.DeltaPositiveText) : ColorOr(col.NegativeTextColor, own ? normal : t.DeltaNegativeText),
                FootprintValue.Bid => ColorOr(col.BidTextColor, ladder && !own ? Mix(normal, t.Bid, 0.45f) : normal),
                FootprintValue.Ask => ColorOr(col.AskTextColor, ladder && !own ? Mix(normal, t.Ask, 0.45f) : normal),
                _ => normal,
            };
            // celdas coloreadas por delta: el fondo ya dice el lado (salvo que hayas elegido colores para el texto)
            if (col.Kind == FootprintColumnKind.Cells && col.Value == FootprintValue.Delta && col.ColorScheme == ClusterColorScheme.Delta &&
                col.PositiveTextColor == null && col.NegativeTextColor == null)
                _text.Color = normal;
        }
        else _text.Color = normal;

        float w = RenderContext.MeasureWidth(f, txt);
        float tx = align switch
        {
            ColumnTextAlign.Left => cell.Left + 3,
            ColumnTextAlign.Right => cell.Right - 3 - w,
            _ => cell.MidX - w / 2,
        };
        canvas.DrawUiText(txt, tx, baseline, f, _text);
    }

    private static string ValueString(FootprintValue v, in LevelData d, int decimals) => v switch
    {
        FootprintValue.Delta => (d.Delta > 0 ? "+" : "") + VolumeFormat.Compact(d.Delta, decimals),
        FootprintValue.Bid => VolumeFormat.Compact(d.Bid, decimals),
        FootprintValue.Ask => VolumeFormat.Compact(d.Ask, decimals),
        FootprintValue.Trades => d.Trades.ToString(),
        _ => VolumeFormat.Compact(d.Volume, decimals),
    };

    private void CellBackground(SKCanvas canvas, Ctx x, FootprintColumn col, SKRect cell, in LevelData d)
    {
        var t = x.T;
        var st = x.S;
        float mid = cell.MidX;
        switch (col.ColorScheme)
        {
            case ClusterColorScheme.None:
                return;
            case ClusterColorScheme.Solid:
                _fill.Color = ColorOr(col.NeutralColor, t.ClusterBase).WithAlpha(110);
                canvas.DrawRect(cell, _fill);
                return;
            case ClusterColorScheme.Delta:
            {
                float frac = Frac(Math.Abs(d.Delta), st.MaxAbsDelta);
                var c = d.Delta >= 0 ? ColorOr(col.PositiveColor, t.Ask) : ColorOr(col.NegativeColor, t.Bid);
                _fill.Color = c.WithAlpha((byte)(25 + 185 * frac));
                canvas.DrawRect(cell, _fill);
                return;
            }
            case ClusterColorScheme.BidAsk:
            {
                float fb = Frac(d.Bid, st.MaxSide), fa = Frac(d.Ask, st.MaxSide);
                _fill.Color = ColorOr(col.NegativeColor, t.Bid).WithAlpha((byte)(20 + 170 * fb));
                canvas.DrawRect(new SKRect(cell.Left, cell.Top, mid, cell.Bottom), _fill);
                _fill.Color = ColorOr(col.PositiveColor, t.Ask).WithAlpha((byte)(20 + 170 * fa));
                canvas.DrawRect(new SKRect(mid, cell.Top, cell.Right, cell.Bottom), _fill);
                return;
            }
            case ClusterColorScheme.Heatmap:
                _fill.Color = Heat(Frac(d.Volume, st.MaxVol));
                canvas.DrawRect(cell, _fill);
                return;
            default:
            {
                bool trades = col.Value == FootprintValue.Trades;
                float frac = Frac(trades ? d.Trades : d.Volume, trades ? st.MaxTrades : st.MaxVol);
                var hi = col.NeutralColor.HasValue ? new SKColor(col.NeutralColor.Value) : new SKColor(52, 110, 175);
                _fill.Color = Mix(new SKColor(14, 26, 44), hi, frac);
                canvas.DrawRect(cell, _fill);
                return;
            }
        }
    }

    /// <summary>+1 = compra, -1 = venta, 2 = resaltado neutro, 0 = nada.</summary>
    private static int Highlight(Ctx x, HighlightRule rule, long r, in LevelData d, FootprintValue value)
    {
        var L = x.L;
        switch (rule)
        {
            case HighlightRule.StrongDelta:
                return d.Volume > 0 && d.Volume >= x.PocVolume * L.HighlightMinVolumeFraction && Math.Abs(d.Delta) >= L.StrongDeltaRatio * d.Volume
                    ? Math.Sign(d.Delta)
                    : 0;
            case HighlightRule.Imbalance:
            {
                var (askImb, bidImb) = Imbalance(x, r, d);
                // en una escalera de bid solo interesan los desequilibrios vendedores, y viceversa
                if (value == FootprintValue.Bid) return bidImb ? -1 : 0;
                if (value == FootprintValue.Ask) return askImb ? 1 : 0;
                return askImb && !bidImb ? 1 : bidImb && !askImb ? -1 : 0;
            }
            case HighlightRule.Poc:
                return r == x.PocRow ? 2 : 0;
            case HighlightRule.HighVolume:
                return x.PocVolume > 0 && d.Volume >= x.PocVolume * Math.Max(0.5m, L.HighlightMinVolumeFraction * 2) ? 2 : 0;
            default:
                return 0;
        }
    }

    /// <summary>Desequilibrio diagonal: ask del nivel contra bid del nivel de abajo, y bid contra ask del de arriba.</summary>
    private static (bool AskImb, bool BidImb) Imbalance(Ctx x, long r, in LevelData d)
    {
        var L = x.L;
        decimal step = x.VolumeStep;
        decimal minV = Math.Max(L.ImbalanceMinVolume, step);
        bool askImb = r > x.AggMin && d.Ask >= minV && d.Ask >= L.ImbalanceRatio * Math.Max(x.Get(r - 1).Bid, step);
        bool bidImb = r < x.AggMax && d.Bid >= minV && d.Bid >= L.ImbalanceRatio * Math.Max(x.Get(r + 1).Ask, step);
        return (askImb, bidImb);
    }

    private static SKColor HighlightColor(int hl, SKColor pos, SKColor neg, SKColor neutral) => hl == 1 ? pos : hl == -1 ? neg : neutral;

    private static SKColor ColorOr(uint? argb, SKColor fallback) => argb.HasValue ? new SKColor(argb.Value) : fallback;

    private static float MinTextWidth(FootprintValue v) => v == FootprintValue.BidAsk ? 52 : 26;

    private static (long High, long Low) ValueAreaRows(Ctx x)
    {
        decimal total = 0;
        for (long r = x.AggMin; r <= x.AggMax; r++) total += x.Get(r).Volume;
        decimal target = total * 0.7m;
        long up = x.PocRow, dn = x.PocRow;
        decimal acc = x.Get(x.PocRow).Volume;
        while (acc < target && (up < x.AggMax || dn > x.AggMin))
        {
            decimal vu = up < x.AggMax ? x.Get(up + 1).Volume : -1;
            decimal vd = dn > x.AggMin ? x.Get(dn - 1).Volume : -1;
            if (vu >= vd) { up++; acc += Math.Max(0, vu); }
            else { dn--; acc += Math.Max(0, vd); }
        }

        return (up, dn);
    }

    internal static int ChooseDecimals(decimal maxVol, int instrumentDecimals)
    {
        if (instrumentDecimals <= 0) return 0;
        if (maxVol >= 100) return 0;
        if (maxVol >= 10) return Math.Min(1, instrumentDecimals);
        if (maxVol >= 1) return Math.Min(2, instrumentDecimals);
        return Math.Min(3, instrumentDecimals);
    }

    private static float Frac(decimal v, decimal max) => max <= 0 ? 0 : (float)Math.Clamp(v / max, 0, 1);

    private static SKColor Mix(SKColor a, SKColor b, float k)
    {
        k = Math.Clamp(k, 0, 1);
        return new SKColor(
            (byte)(a.Red + (b.Red - a.Red) * k),
            (byte)(a.Green + (b.Green - a.Green) * k),
            (byte)(a.Blue + (b.Blue - a.Blue) * k),
            (byte)(a.Alpha + (b.Alpha - a.Alpha) * k));
    }

    private static SKColor Heat(float k)
    {
        if (k < 0.33f) return Mix(new SKColor(10, 20, 60), new SKColor(0, 150, 200), k / 0.33f);
        if (k < 0.66f) return Mix(new SKColor(0, 150, 200), new SKColor(240, 210, 40), (k - 0.33f) / 0.33f);
        return Mix(new SKColor(240, 210, 40), new SKColor(230, 50, 40), (k - 0.66f) / 0.34f);
    }
}
