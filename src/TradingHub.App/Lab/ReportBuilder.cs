using System.Globalization;
using System.Text;
using TradingHub.Strategies;

namespace TradingHub.App.Lab;

/// <summary>Qué entra en un informe: corridas, portafolio y el análisis escrito (de Claude o del usuario).</summary>
internal sealed class ReportInput
{
    public string Title { get; set; } = "";
    public string Verdict { get; set; } = "";
    public string? StrategyFile { get; set; }
    public string? StrategyName { get; set; }
    public string? Description { get; set; }
    public StrategyRun? Backtest { get; set; }
    public StrategyRun? Optimization { get; set; }
    public StrategyRun? WalkForward { get; set; }
    public PortfolioResult? Portfolio { get; set; }
    public decimal PortfolioCapital { get; set; } = 10_000;
    public string Summary { get; set; } = "";
    public string Analysis { get; set; } = "";
    public string Risks { get; set; } = "";
    public string NextSteps { get; set; } = "";
    public string Extra { get; set; } = "";
}

/// <summary>
/// Arma un informe profesional en Markdown con todas las métricas, tablas y gráficos a partir de las
/// corridas guardadas. Las imágenes se referencian como {{assets}}/archivo.png (las ubica ReportStore).
/// </summary>
internal static class ReportBuilder
{
    private static readonly CultureInfo Es = TradingHub.Common.UiCulture.Current;

    private static string Money(decimal v, bool sign = false) => (sign && v > 0 ? "+" : "") + v.ToString("N2", Es);
    private static string Pct(decimal v, bool sign = false) => (sign && v > 0 ? "+" : "") + v.ToString("0.00", Es) + " %";
    private static string Pct(double v, bool sign = false) => Pct((decimal)(double.IsFinite(v) ? v : 0), sign);
    private static string Num(double v, int dec = 2) => double.IsFinite(v) ? v.ToString("N" + dec, Es) : "—";
    private static string Num(decimal v, int dec = 2) => v.ToString("N" + dec, Es);
    private static string Date(DateTime t) => t.ToString("dd/MM/yyyy", Es);
    private static string DateTimeText(DateTime t) => t.ToString("dd/MM/yyyy HH:mm", Es);
    private static string Ratio(decimal v) => v >= 999 ? "∞" : Num(v);

    /// <summary>CAGR y Calmar solo tienen sentido con períodos largos (anualizar 5 días exagera todo).</summary>
    private static string Annualized(TradeAnalytics a, double value, bool percent) =>
        a.Days < 90 ? "— (menos de 90 días)" : percent ? Pct(value, true) : Num(value);

    private static string Duration(TimeSpan d)
    {
        if (d.TotalMinutes < 1) return $"{d.TotalSeconds:0} s";
        if (d.TotalHours < 1) return $"{d.TotalMinutes:0} min";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours} h {d.Minutes} min";
        return $"{(int)d.TotalDays} d {d.Hours} h";
    }

    private static string Cell(object? v) => (v switch
    {
        null => "",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    }).Replace("|", "/");

    private static void Table(StringBuilder sb, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        sb.AppendLine("| " + string.Join(" | ", headers) + " |");
        sb.AppendLine("|" + string.Concat(headers.Select(_ => "---|")));
        foreach (var r in rows) sb.AppendLine("| " + string.Join(" | ", r.Select(x => x.Replace("|", "/"))) + " |");
        sb.AppendLine();
    }

    private static void KeyValues(StringBuilder sb, IEnumerable<(string Key, string Value)> rows) =>
        Table(sb, new[] { "Concepto", "Valor" }, rows.Select(r => (IReadOnlyList<string>)new[] { r.Key, r.Value }));

    private static TimeZoneInfo Zone(BacktestSettings s)
    {
        if (string.IsNullOrEmpty(s.TimeZoneId)) return TimeZoneInfo.Utc;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(s.TimeZoneId);
        }
        catch
        {
            return TimeZoneInfo.Utc;
        }
    }

    private static string VerdictText(string v) => v switch
    {
        "ganadora" => "GANADORA",
        "prometedora" => "PROMETEDORA",
        "perdedora" => "PERDEDORA",
        "descartada" => "DESCARTADA",
        _ => "",
    };

    public static (string Markdown, List<(string Name, byte[] Png)> Images) Build(ReportInput x)
    {
        var sb = new StringBuilder();
        var images = new List<(string, byte[])>();
        var main = x.Backtest;
        var s = main?.Settings ?? x.WalkForward?.Settings ?? x.Optimization?.Settings;

        // ----------------------------------------------------------------- encabezado
        sb.AppendLine($"# {x.Title}");
        sb.AppendLine();
        var meta = new List<string>();
        if (x.StrategyName != null) meta.Add($"**Estrategia:** {x.StrategyName}{(x.StrategyFile != null ? $" ({x.StrategyFile}.cs)" : "")}");
        if (s != null)
        {
            meta.Add($"**Instrumento:** {s.Symbol} ({s.FeedId})");
            meta.Add($"**Velas:** {s.Period}");
            meta.Add($"**Período:** {Date(s.FromUtc)} – {Date(s.ToUtc.AddSeconds(-1))}");
        }
        else if (x.Portfolio != null)
        {
            meta.Add($"**Portafolio:** {x.Portfolio.Components.Count} estrategias");
            meta.Add($"**Período:** {Date(x.Portfolio.From)} – {Date(x.Portfolio.To)}");
        }

        meta.Add($"**Generado:** {DateTimeText(DateTime.Now)} con Trading Hub");
        sb.AppendLine("> " + string.Join(" · ", meta));
        sb.AppendLine();
        if (VerdictText(x.Verdict) is { Length: > 0 } vt)
        {
            sb.AppendLine($"> **Veredicto: {vt}**{(x.Summary.Length > 0 ? " — " + x.Summary.Trim().Replace("\n", " ") : "")}");
            sb.AppendLine();
        }

        // sujeto principal de las estadísticas: el backtest o el portafolio
        List<ClosedTrade>? trades = main?.Trades;
        List<EquityPoint>? equity = main?.Equity;
        PerformanceMetrics? m = main?.Metrics;
        decimal capital = s?.InitialCapital ?? x.PortfolioCapital;
        if (main == null && x.Portfolio != null)
        {
            trades = x.Portfolio.Trades;
            equity = x.Portfolio.Equity;
            m = x.Portfolio.Metrics;
            capital = x.PortfolioCapital;
        }

        TradeAnalytics? a = null;
        if (trades != null && m != null)
            a = TradeAnalytics.Compute(trades, equity ?? new List<EquityPoint>(), capital, m, s != null ? Zone(s) : TimeZoneInfo.Utc,
                s?.FromUtc ?? x.Portfolio?.From, s?.ToUtc ?? x.Portfolio?.To);

        int section = 0;
        string H2(string title) => $"## {++section}. {title}";

        // ----------------------------------------------------------------- 1. resumen ejecutivo
        sb.AppendLine(H2("Resumen ejecutivo"));
        sb.AppendLine();
        // el resumen va en el recuadro del veredicto; si no hay veredicto, acá
        if (x.Summary.Length > 0 && VerdictText(x.Verdict).Length == 0)
        {
            sb.AppendLine(x.Summary.Trim());
            sb.AppendLine();
        }

        if (m != null && a != null)
        {
            var wf = x.WalkForward?.WalkForward;
            var mc = main?.MonteCarlo;
            var pairs = new List<(string, string)>
            {
                ("Ganancia neta", Money(m.NetProfit, true)),
                ("Retorno total", Pct(m.ReturnPercent, true)),
                ("CAGR (anualizado)", Annualized(a, a.CagrPercent, true)),
                ("Profit factor", Ratio(m.ProfitFactor)),
                ("Sharpe", Num(m.SharpeRatio)),
                ("Sortino", Num(m.SortinoRatio)),
                ("Drawdown máximo", $"{Money(m.MaxDrawdown)} ({Pct(m.MaxDrawdownPercent)})"),
                ("Calmar", Annualized(a, a.Calmar, false)),
                ("Operaciones", m.TotalTrades.ToString(Es)),
                ("Acierto", Pct(m.WinRate)),
                ("Expectativa por operación", Money(m.Expectancy, true)),
                ("SQN", Num(a.Sqn)),
            };
            if (wf != null)
            {
                pairs.Add(("Eficiencia walk-forward", Num(wf.WalkForwardEfficiency)));
                pairs.Add(("Ventanas ganadoras (WF)", Pct(wf.ProfitableWindowsPercent)));
            }

            if (mc != null)
            {
                pairs.Add(("Drawdown p95 (Monte Carlo)", Money(mc.DrawdownP95)));
                pairs.Add(("Probabilidad de ruina", Pct(mc.RuinProbabilityPercent)));
            }

            var rows = new List<IReadOnlyList<string>>();
            for (int i = 0; i < pairs.Count; i += 2)
                rows.Add(new[] { pairs[i].Item1, pairs[i].Item2, i + 1 < pairs.Count ? pairs[i + 1].Item1 : "", i + 1 < pairs.Count ? pairs[i + 1].Item2 : "" });
            Table(sb, new[] { "Indicador", "Valor", "Indicador", "Valor" }, rows);

            if (equity is { Count: > 1 })
            {
                var extra = x.Portfolio != null && main == null ? PortfolioCurves(x.Portfolio, capital) : null;
                images.Add(("capital.png", ReportCharts.Equity(equity.Select(p => (p.Time, p.Equity)).ToList(), capital, main == null ? "Curva de capital del portafolio" : "Curva de capital y drawdown", extra)));
                sb.AppendLine("![Curva de capital y drawdown]({{assets}}/capital.png)");
                sb.AppendLine();
            }
        }

        // ----------------------------------------------------------------- 2. estrategia
        if (main != null || x.StrategyName != null)
        {
            sb.AppendLine(H2("Estrategia"));
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(x.Description))
            {
                sb.AppendLine(x.Description.Trim());
                sb.AppendLine();
            }

            var parms = main?.Parameters ?? x.WalkForward?.Parameters;
            if (parms is { Count: > 0 })
            {
                sb.AppendLine("**Parámetros usados**");
                sb.AppendLine();
                Table(sb, new[] { "Parámetro", "Valor" }, parms.Select(kv => (IReadOnlyList<string>)new[] { kv.Key, Cell(kv.Value) }));
            }

            var hash = main?.CodeHash ?? x.WalkForward?.CodeHash;
            if (!string.IsNullOrEmpty(hash)) sb.AppendLine($"Versión del código: `{hash[..Math.Min(12, hash.Length)]}` (queda guardada en el historial de la estrategia).\n");
        }

        // ----------------------------------------------------------------- 3. datos y supuestos
        if (main != null && s != null)
        {
            sb.AppendLine(H2("Datos y supuestos"));
            sb.AppendLine();
            KeyValues(sb, new[]
            {
                ("Instrumento", $"{s.Symbol} ({s.FeedId})"),
                ("Velas", s.Period),
                ("Desde", DateTimeText(s.FromUtc) + " UTC"),
                ("Hasta", DateTimeText(s.ToUtc) + " UTC"),
                ("Días calendario", Num((s.ToUtc - s.FromUtc).TotalDays, 1)),
                ("Ticks procesados", main.Ticks.ToString("N0", Es)),
                ("Velas procesadas", main.Bars.ToString("N0", Es)),
                ("Capital inicial", Money(s.InitialCapital)),
                ("Comisión", s.CommissionPercent > 0 ? $"{s.CommissionPercent.ToString("0.####", Es)} % del nominal por lado" : $"{Money(s.CommissionPerUnit)} por unidad"),
                ("Deslizamiento", $"{s.SlippageTicks} tick(s) en órdenes a mercado y stops"),
                ("Órdenes límite", s.LimitFill == LimitFillMode.TradeThrough ? "se llenan solo si el precio las atraviesa" : "se llenan al tocar el precio"),
                ("Zona horaria de la sesión", string.IsNullOrEmpty(s.TimeZoneId) ? "UTC" : s.TimeZoneId),
                ("Inicio de sesión", s.SessionStart.ToString(@"hh\:mm", Es)),
                ("Posición al final", s.CloseAtEnd ? "se cierra al terminar el período" : "queda abierta"),
                ("Tiempo de cálculo", $"{Num(main.Seconds, 1)} s"),
            });
        }

        // ----------------------------------------------------------------- 4. rendimiento
        if (m != null && a != null)
        {
            sb.AppendLine(H2("Rendimiento"));
            sb.AppendLine();
            sb.AppendLine("### Rentabilidad");
            sb.AppendLine();
            KeyValues(sb, new[]
            {
                ("Ganancia neta", Money(m.NetProfit, true)),
                ("Ganancia bruta", Money(m.GrossProfit)),
                ("Pérdida bruta", Money(-m.GrossLoss)),
                ("Comisiones pagadas", Money(m.Commission)),
                ("Capital final", Money(a.FinalEquity)),
                ("Retorno total", Pct(m.ReturnPercent, true)),
                ("CAGR (anualizado)", Annualized(a, a.CagrPercent, true)),
                ("Profit factor", Ratio(m.ProfitFactor)),
                ("Expectativa por operación", Money(m.Expectancy, true)),
                ("Días con operaciones", a.TradingDays.ToString(Es)),
                ("Resultado promedio por día operado", Money(a.AverageDailyPnl, true)),
                ("Mejor día", Money(a.BestDay, true)),
                ("Peor día", Money(a.WorstDay, true)),
                ("Días ganadores", Pct(a.ProfitableDaysPercent)),
            });

            sb.AppendLine("### Operaciones");
            sb.AppendLine();
            KeyValues(sb, new[]
            {
                ("Operaciones", m.TotalTrades.ToString(Es)),
                ("Ganadoras / perdedoras", $"{m.Winners} / {m.Losers}"),
                ("Acierto", Pct(m.WinRate)),
                ("Resultado promedio", Money(m.AverageTrade, true)),
                ("Promedio de las ganadoras", Money(m.AverageWin, true)),
                ("Promedio de las perdedoras", Money(m.AverageLoss, true)),
                ("Relación ganancia / pérdida promedio", Num(m.PayoffRatio)),
                ("Mayor ganadora", Money(m.LargestWin, true)),
                ("Mayor perdedora", Money(m.LargestLoss, true)),
                ("Racha ganadora máxima", m.MaxConsecutiveWins.ToString(Es)),
                ("Racha perdedora máxima", m.MaxConsecutiveLosses.ToString(Es)),
                ("Desvío estándar por operación", Money(a.TradeStdDev)),
                ("Operaciones por día", Num(a.TradesPerDay)),
                ("Duración promedio / mediana / máxima", $"{Duration(a.AverageDuration)} / {Duration(a.MedianDuration)} / {Duration(a.MaxDuration)}"),
                ("Duración promedio ganadoras / perdedoras", $"{Duration(a.AverageWinDuration)} / {Duration(a.AverageLossDuration)}"),
                ("Velas promedio ganadoras / perdedoras", $"{Num(a.AverageBarsWin, 1)} / {Num(a.AverageBarsLoss, 1)}"),
                ("Tiempo en el mercado", m.ExposurePercent > 0 ? Pct(m.ExposurePercent) : "—"),
            });

            sb.AppendLine("### Riesgo");
            sb.AppendLine();
            KeyValues(sb, new[]
            {
                ("Drawdown máximo", $"{Money(m.MaxDrawdown)} ({Pct(m.MaxDrawdownPercent)})"),
                ("Drawdown más largo (cierre de operaciones)", $"{Num(a.MaxDrawdownDays, 1)} días"),
                ("Período más largo sin nuevos máximos", $"{Num(a.LongestFlatDays, 1)} días"),
                ("Recovery factor", Ratio(m.RecoveryFactor)),
                ("Sharpe (anualizado)", Num(m.SharpeRatio)),
                ("Sortino (anualizado)", Num(m.SortinoRatio)),
                ("Calmar", Annualized(a, a.Calmar, false)),
                ("Ulcer index", Num(a.UlcerIndex)),
                ("SQN (calidad del sistema)", Num(a.Sqn)),
                ("Kelly", Pct(a.KellyPercent)),
                ("MAE promedio (excursión en contra)", Money(a.AverageMae)),
                ("MFE promedio (excursión a favor)", Money(a.AverageMfe)),
                ("Edge ratio (MFE / MAE)", Num(a.EdgeRatio)),
            });

            sb.AppendLine("### Largos y cortos");
            sb.AppendLine();
            var all = new BucketStat { Label = "Total" };
            foreach (var t in trades!) all.Add(t);
            var cols = new[] { a.Long, a.Short, all };
            Table(sb, new[] { "", "Largos", "Cortos", "Total" }, new IReadOnlyList<string>[]
            {
                new[] { "Operaciones" }.Concat(cols.Select(c => c.Trades.ToString(Es))).ToArray(),
                new[] { "Acierto" }.Concat(cols.Select(c => Pct(c.WinRate))).ToArray(),
                new[] { "Resultado neto" }.Concat(cols.Select(c => Money(c.Net, true))).ToArray(),
                new[] { "Profit factor" }.Concat(cols.Select(c => Ratio(c.ProfitFactor))).ToArray(),
                new[] { "Promedio" }.Concat(cols.Select(c => Money(c.Average, true))).ToArray(),
                new[] { "Mayor ganadora" }.Concat(cols.Select(c => Money(c.LargestWin, true))).ToArray(),
                new[] { "Mayor perdedora" }.Concat(cols.Select(c => Money(c.LargestLoss, true))).ToArray(),
            });

            // ----------------------------------------------------------------- 5. mensual
            if (a.Monthly.Count > 0)
            {
                sb.AppendLine(H2("Rendimiento mensual"));
                sb.AppendLine();
                images.Add(("mensual.png", ReportCharts.Monthly(a.Monthly)));
                sb.AppendLine("![Rendimiento mensual]({{assets}}/mensual.png)");
                sb.AppendLine();
                string[] months = { "Ene", "Feb", "Mar", "Abr", "May", "Jun", "Jul", "Ago", "Sep", "Oct", "Nov", "Dic" };
                Table(sb, new[] { "Año" }.Concat(months).Append("Año").ToArray(),
                    a.Monthly.Select(y => (IReadOnlyList<string>)new[] { y.Year.ToString(CultureInfo.InvariantCulture) }
                        .Concat(y.MonthPercent.Select(v => v.HasValue ? Pct(v.Value, true) : ""))
                        .Append(Pct(y.YearPercent, true)).ToArray()));
            }

            // ----------------------------------------------------------------- 6. operaciones
            sb.AppendLine(H2("Análisis de operaciones"));
            sb.AppendLine();
            if (a.Histogram.Count > 0)
            {
                images.Add(("distribucion.png", ReportCharts.Histogram(a.Histogram)));
                sb.AppendLine("![Distribución de resultados]({{assets}}/distribucion.png)");
                sb.AppendLine();
            }

            if (a.ByHour.Count > 0)
            {
                sb.AppendLine($"### Por hora de entrada ({(s == null || string.IsNullOrEmpty(s.TimeZoneId) ? "UTC" : s.TimeZoneId)})");
                sb.AppendLine();
                Table(sb, new[] { "Hora", "Operaciones", "Acierto", "Neto", "Profit factor", "Promedio" },
                    a.ByHour.Select(b => (IReadOnlyList<string>)new[] { b.Label, b.Trades.ToString(Es), Pct(b.WinRate), Money(b.Net, true), Ratio(b.ProfitFactor), Money(b.Average, true) }));
            }

            if (a.ByWeekday.Count > 0)
            {
                sb.AppendLine("### Por día de la semana");
                sb.AppendLine();
                Table(sb, new[] { "Día", "Operaciones", "Acierto", "Neto", "Profit factor", "Promedio" },
                    a.ByWeekday.Select(b => (IReadOnlyList<string>)new[] { b.Label, b.Trades.ToString(Es), Pct(b.WinRate), Money(b.Net, true), Ratio(b.ProfitFactor), Money(b.Average, true) }));
            }

            IReadOnlyList<string> TradeRow(ClosedTrade t) => new[]
            {
                t.Number.ToString(Es), t.IsLong ? "Largo" : "Corto", DateTimeText(t.EntryTime), DateTimeText(t.ExitTime),
                t.EntryPrice.ToString("0.########", Es), t.ExitPrice.ToString("0.########", Es), Money(t.NetPnl, true), Money(t.Mae), Money(t.Mfe), t.Bars.ToString(Es),
            };
            string[] tradeHeaders = { "#", "Lado", "Entrada", "Salida", "Precio entrada", "Precio salida", "Resultado", "MAE", "MFE", "Velas" };
            if (a.Best.Count > 0)
            {
                sb.AppendLine("### Mejores operaciones");
                sb.AppendLine();
                Table(sb, tradeHeaders, a.Best.Select(TradeRow));
                sb.AppendLine("### Peores operaciones");
                sb.AppendLine();
                Table(sb, tradeHeaders, a.Worst.Select(TradeRow));
            }

            // ----------------------------------------------------------------- 7. drawdowns
            if (a.Drawdowns.Count > 0)
            {
                sb.AppendLine(H2("Drawdowns principales"));
                sb.AppendLine();
                Table(sb, new[] { "#", "Inicio", "Mínimo", "Recuperación", "Profundidad", "%", "Días" },
                    a.Drawdowns.Select((d, i) => (IReadOnlyList<string>)new[]
                    {
                        (i + 1).ToString(Es), DateTimeText(d.Start), DateTimeText(d.Trough), d.Recovery is { } r ? DateTimeText(r) : "sin recuperar",
                        Money(-d.Depth), Pct(-d.DepthPercent), Num(d.Days, 1),
                    }));
            }
        }

        // ----------------------------------------------------------------- 8. optimización
        if (x.Optimization?.Optimization is { } opt && opt.Rows.Count > 0)
        {
            sb.AppendLine(H2("Optimización"));
            sb.AppendLine();
            var enabled = opt.Ranges.Where(r => r.Enabled).ToList();
            KeyValues(sb, new[]
            {
                ("Métrica optimizada", opt.Metric.ToString()),
                ("Combinaciones probadas", opt.Combinations.ToString("N0", Es)),
                ("Combinaciones rentables", Pct(100.0 * opt.Rows.Count(r => r.Metrics.NetProfit > 0) / opt.Rows.Count)),
                ("Tiempo", $"{Num(opt.Seconds, 1)} s"),
            }.Concat(enabled.Select(r => ($"Rango de {r.Label}", $"{Num(r.Min, 4)} a {Num(r.Max, 4)} de a {Num(r.Step, 4)}"))));

            sb.AppendLine("### Mejores combinaciones");
            sb.AppendLine();
            var names = enabled.Select(r => r.Name).ToList();
            Table(sb, new[] { "#" }.Concat(enabled.Select(r => r.Label)).Concat(new[] { "Neto", "PF", "Sharpe", "DD máx", "Ops", "Acierto" }).ToArray(),
                opt.Rows.Take(10).Select((r, i) => (IReadOnlyList<string>)new[] { (i + 1).ToString(Es) }
                    .Concat(names.Select(n => Cell(r.Parameters.GetValueOrDefault(n))))
                    .Concat(new[] { Money(r.Metrics.NetProfit, true), Ratio(r.Metrics.ProfitFactor), Num(r.Metrics.SharpeRatio), Money(r.Metrics.MaxDrawdown), r.Metrics.TotalTrades.ToString(Es), Pct(r.Metrics.WinRate) })
                    .ToArray()));

            sb.AppendLine("### Sensibilidad por parámetro");
            sb.AppendLine();
            sb.AppendLine("Promedio de todas las combinaciones probadas con cada valor: una meseta pareja es señal de robustez; un pico aislado, de sobreajuste.");
            sb.AppendLine();
            foreach (var r in enabled)
            {
                sb.AppendLine($"**{r.Label}**");
                sb.AppendLine();
                var groups = opt.Rows.GroupBy(row => Cell(row.Parameters.GetValueOrDefault(r.Name)))
                    .OrderBy(g => double.TryParse(g.Key, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0);
                Table(sb, new[] { "Valor", "Combinaciones", "Rentables", "Neto promedio", "Sharpe promedio" },
                    groups.Select(g => (IReadOnlyList<string>)new[]
                    {
                        g.Key, g.Count().ToString(Es), Pct(100.0 * g.Count(z => z.Metrics.NetProfit > 0) / g.Count()),
                        Money(g.Average(z => z.Metrics.NetProfit), true), Num(g.Average(z => z.Metrics.SharpeRatio)),
                    }));
            }
        }

        // ----------------------------------------------------------------- 9. walk-forward
        if (x.WalkForward?.WalkForward is { } w && w.Windows.Count > 0)
        {
            sb.AppendLine(H2("Walk-forward (validación fuera de muestra)"));
            sb.AppendLine();
            var om = w.OutSampleMetrics;
            KeyValues(sb, new[]
            {
                ("Ventanas", $"{w.Windows.Count} · {w.InSampleDays} días de optimización y {w.OutSampleDays} de prueba ({(w.Anchored ? "ancladas" : "móviles")})"),
                ("Métrica", w.Metric.ToString()),
                ("Eficiencia walk-forward", Num(w.WalkForwardEfficiency)),
                ("Ventanas ganadoras", Pct(w.ProfitableWindowsPercent)),
                ("Neto fuera de muestra", Money(om.NetProfit, true)),
                ("Profit factor fuera de muestra", Ratio(om.ProfitFactor)),
                ("Sharpe fuera de muestra", Num(om.SharpeRatio)),
                ("Drawdown máximo fuera de muestra", $"{Money(om.MaxDrawdown)} ({Pct(om.MaxDrawdownPercent)})"),
                ("Operaciones fuera de muestra", om.TotalTrades.ToString(Es)),
                ("Acierto fuera de muestra", Pct(om.WinRate)),
            });
            Table(sb, new[] { "Ventana", "Optimización", "Prueba", "Parámetros elegidos", "Neto (optim.)", "Neto (prueba)", "Ops prueba", "Eficiencia" },
                w.Windows.Select(win => (IReadOnlyList<string>)new[]
                {
                    win.Number.ToString(Es), $"{Date(win.InSampleFrom)} – {Date(win.InSampleTo)}", $"{Date(win.OutSampleFrom)} – {Date(win.OutSampleTo)}",
                    string.Join(", ", win.BestParameters.Select(kv => $"{kv.Key}={Cell(kv.Value)}")),
                    Money(win.InSample.NetProfit, true), Money(win.OutSample.NetProfit, true), win.OutSample.TotalTrades.ToString(Es), Num(win.Efficiency),
                }));
            if (w.OutSampleEquity.Count > 1)
            {
                images.Add(("walkforward.png", ReportCharts.Equity(w.OutSampleEquity.Select(p => (p.Time, p.Equity)).ToList(), w.OutSampleEquity[0].Equity, "Capital fuera de muestra (encadenado)")));
                sb.AppendLine("![Capital fuera de muestra]({{assets}}/walkforward.png)");
                sb.AppendLine();
            }

            // qué tan estables fueron los parámetros entre ventanas
            var paramNames = w.Windows.SelectMany(win => win.BestParameters.Keys).Distinct().ToList();
            var varying = paramNames.Where(p => w.Windows.Select(win => Cell(win.BestParameters.GetValueOrDefault(p))).Distinct().Count() > 1).ToList();
            if (varying.Count > 0)
            {
                sb.AppendLine("### Estabilidad de los parámetros");
                sb.AppendLine();
                Table(sb, new[] { "Parámetro", "Valores elegidos (veces)" }, varying.Select(p => (IReadOnlyList<string>)new[]
                {
                    p, string.Join(" · ", w.Windows.GroupBy(win => Cell(win.BestParameters.GetValueOrDefault(p))).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} ({g.Count()})")),
                }));
            }
        }

        // ----------------------------------------------------------------- 10. Monte Carlo
        if (main?.MonteCarlo is { } mcr && mcr.Runs > 0)
        {
            sb.AppendLine(H2("Monte Carlo"));
            sb.AppendLine();
            sb.AppendLine($"Las {main.Trades?.Count ?? 0} operaciones se reordenaron al azar {mcr.Runs:N0} veces para ver qué drawdowns son posibles con la misma ventaja.");
            sb.AppendLine();
            KeyValues(sb, new[]
            {
                ("Simulaciones", mcr.Runs.ToString("N0", Es)),
                ("Drawdown mediano", Money(mcr.DrawdownP50)),
                ("Drawdown p95", Money(mcr.DrawdownP95)),
                ("Drawdown p99", Money(mcr.DrawdownP99)),
                ("Peor drawdown simulado", Money(mcr.WorstDrawdown)),
                ("Probabilidad de ruina (drawdown ≥ 50 % del capital)", Pct(mcr.RuinProbabilityPercent)),
                ("Capital sugerido (2 × drawdown p99)", Money(mcr.DrawdownP99 * 2)),
            });
            if (mcr.SampleCurves.Count > 0)
            {
                images.Add(("montecarlo.png", ReportCharts.Curves(mcr.SampleCurves.Select(cv => (IReadOnlyList<decimal>)cv).ToList(), capital, "Monte Carlo: curvas con las operaciones en otro orden")));
                sb.AppendLine("![Monte Carlo]({{assets}}/montecarlo.png)");
                sb.AppendLine();
            }
        }

        // ----------------------------------------------------------------- 11. portafolio
        if (x.Portfolio is { } pf && pf.Components.Count > 0)
        {
            sb.AppendLine(H2("Portafolio"));
            sb.AppendLine();
            if (pf.NoOverlap) sb.AppendLine("> Las estrategias no comparten fechas: se combinó la unión de los períodos (la correlación no es confiable).\n");
            Table(sb, new[] { "#", "Estrategia", "Instrumento", "Peso", "Neto", "Sharpe", "DD máx", "Ops" },
                pf.Components.Select((c, i) => (IReadOnlyList<string>)new[]
                {
                    (i + 1).ToString(Es), c.StrategyName, $"{c.Settings.Symbol} {c.Settings.Period}", Num(c.Weight),
                    Money(pf.ComponentMetrics[i].NetProfit, true), Num(pf.ComponentMetrics[i].SharpeRatio), Money(pf.ComponentMetrics[i].MaxDrawdown), pf.ComponentMetrics[i].TotalTrades.ToString(Es),
                }));
            KeyValues(sb, new[]
            {
                ("Período común", $"{Date(pf.From)} – {Date(pf.To)}"),
                ("Neto del portafolio", Money(pf.Metrics.NetProfit, true)),
                ("Sharpe del portafolio / promedio de las estrategias", $"{Num(pf.Metrics.SharpeRatio)} / {Num(pf.AverageComponentSharpe)}"),
                ("Drawdown del portafolio / suma de los drawdowns", $"{Money(pf.Metrics.MaxDrawdown)} / {Money(pf.SumOfComponentDrawdowns)}"),
                ("Reducción de drawdown por diversificar", Pct(pf.DrawdownReductionPercent)),
                ("Correlación promedio / máxima", $"{Num(pf.AverageCorrelation)} / {Num(pf.MaxCorrelation)}"),
            });
            var labels = pf.Components.Select(c => c.Label).ToList();
            images.Add(("correlacion.png", ReportCharts.Correlation(labels, pf.Correlation)));
            sb.AppendLine("![Correlación]({{assets}}/correlacion.png)");
            sb.AppendLine();
            if (main != null)
            {
                images.Add(("portafolio.png", ReportCharts.Equity(pf.Equity.Select(p => (p.Time, p.Equity)).ToList(), x.PortfolioCapital, "Curva del portafolio", PortfolioCurves(pf, x.PortfolioCapital))));
                sb.AppendLine("![Curva del portafolio]({{assets}}/portafolio.png)");
                sb.AppendLine();
            }
        }

        // ----------------------------------------------------------------- análisis escrito
        void Text(string title, string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return;
            sb.AppendLine(H2(title));
            sb.AppendLine();
            sb.AppendLine(body.Trim());
            sb.AppendLine();
        }

        Text("Análisis", x.Analysis);
        Text("Riesgos", x.Risks);
        Text("Próximos pasos", x.NextSteps);
        if (!string.IsNullOrWhiteSpace(x.Extra))
        {
            sb.AppendLine(x.Extra.Trim());
            sb.AppendLine();
        }

        // ----------------------------------------------------------------- glosario
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("### Glosario");
        sb.AppendLine();
        foreach (var (k, v) in new[]
                 {
                     ("Profit factor", "ganancia bruta dividida por pérdida bruta. Más de 1,5 es bueno; más de 2, muy bueno."),
                     ("Sharpe / Sortino", "retorno diario promedio sobre su volatilidad (Sortino solo penaliza las bajas), anualizados."),
                     ("CAGR", "crecimiento anual compuesto equivalente."),
                     ("Calmar", "CAGR dividido por el drawdown máximo en %."),
                     ("Drawdown", "caída desde el máximo anterior de la curva de capital."),
                     ("SQN", "System Quality Number (Van Tharp): resultado promedio sobre su desvío, por la raíz de la cantidad de operaciones (máx. 100). Más de 2 es bueno."),
                     ("Ulcer index", "profundidad y duración de los drawdowns (más bajo es mejor)."),
                     ("Kelly", "fracción del capital que maximiza el crecimiento según acierto y relación ganancia/pérdida (en la práctica se usa una fracción)."),
                     ("MAE / MFE", "lo máximo que cada operación fue en contra / a favor antes de cerrarse."),
                     ("Eficiencia walk-forward", "rendimiento fuera de muestra sobre el de la optimización (por día). Más de 0,5 sugiere que no está sobreajustada."),
                     ("Monte Carlo", "reordena las operaciones al azar para estimar drawdowns posibles con la misma ventaja."),
                 })
            sb.AppendLine($"- **{k}:** {v}");
        sb.AppendLine();
        sb.AppendLine("> Resultados simulados sobre datos históricos, con comisiones y deslizamiento. No garantizan resultados futuros.");
        return (sb.ToString(), images);
    }

    private static List<(string Label, IReadOnlyList<(DateTime Time, decimal Equity)> Points)> PortfolioCurves(PortfolioResult pf, decimal capital)
    {
        var res = new List<(string, IReadOnlyList<(DateTime, decimal)>)>();
        for (int i = 0; i < pf.Components.Count; i++)
        {
            decimal eq = capital;
            var pts = new List<(DateTime, decimal)> { (pf.From, capital) };
            for (int k = 0; k < pf.Days.Count; k++)
            {
                eq += pf.DailyPnl[i][k];
                pts.Add((pf.Days[k].ToDateTime(new TimeOnly(23, 59)), eq));
            }

            res.Add((pf.Components[i].Label, pts));
        }

        return res;
    }
}
