using System.Globalization;
using System.Text.Json;

namespace TradingHub.Strategies;

/// <summary>
/// Pruebas contra la sobreoptimización: Sharpe probabilístico y deflactado (Bailey y López de Prado), estabilidad de
/// los parámetros elegidos en el walk-forward y meseta alrededor de la mejor combinación de la optimización.
/// </summary>
public static class Robustness
{
    private const double EulerGamma = 0.5772156649015329;

    /// <summary>Distribución normal estándar acumulada.</summary>
    public static double NormalCdf(double x)
    {
        // erfc con la aproximación de Numerical Recipes (error < 1.2e-7)
        double z = Math.Abs(x) / Math.Sqrt(2);
        double t = 1 / (1 + 0.5 * z);
        double r = t * Math.Exp(-z * z - 1.26551223 + t * (1.00002368 + t * (0.37409196 + t * (0.09678418 + t * (-0.18628806 + t * (0.27886807 +
            t * (-1.13520398 + t * (1.48851587 + t * (-0.82215223 + t * 0.17087277)))))))));
        return x >= 0 ? 1 - r / 2 : r / 2;
    }

    /// <summary>Inversa de la normal estándar (algoritmo de Acklam, error relativo &lt; 1.2e-9).</summary>
    public static double NormalInv(double p)
    {
        if (p <= 0) return double.NegativeInfinity;
        if (p >= 1) return double.PositiveInfinity;
        double[] a = { -3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00 };
        double[] b = { -5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01 };
        double[] c = { -7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00 };
        double[] d = { 7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00 };
        const double low = 0.02425, high = 1 - low;
        if (p < low)
        {
            double q = Math.Sqrt(-2 * Math.Log(p));
            return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }

        if (p > high)
        {
            double q = Math.Sqrt(-2 * Math.Log(1 - p));
            return -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }

        double u = p - 0.5, r = u * u;
        return (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * u / (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1);
    }

    /// <summary>Máximo esperado de <paramref name="n"/> normales estándar independientes (lo que da la suerte sola al probar n cosas).</summary>
    public static double ExpectedMaxZ(int n)
    {
        if (n <= 1) return 0;
        return (1 - EulerGamma) * NormalInv(1 - 1.0 / n) + EulerGamma * NormalInv(1 - 1.0 / (n * Math.E));
    }

    /// <summary>Sharpe por operación (media / desvío del resultado neto), asimetría, curtosis (normal = 3) y cantidad.</summary>
    public static (double Sharpe, double Skew, double Kurtosis, int Count) TradeSharpe(IReadOnlyList<ClosedTrade> trades)
    {
        int n = trades.Count;
        if (n < 2) return (0, 0, 3, n);
        double mean = trades.Average(t => (double)t.NetPnl);
        double m2 = 0, m3 = 0, m4 = 0;
        foreach (var t in trades)
        {
            double d = (double)t.NetPnl - mean;
            m2 += d * d;
            m3 += d * d * d;
            m4 += d * d * d * d;
        }

        m2 /= n;
        m3 /= n;
        m4 /= n;
        if (m2 <= 0) return (0, 0, 3, n);
        double sd = Math.Sqrt(m2);
        return (mean / sd, m3 / (sd * sd * sd), m4 / (m2 * m2), n);
    }

    /// <summary>Probabilidad de que el Sharpe verdadero supere <paramref name="benchmark"/> dado lo observado (PSR).</summary>
    public static double ProbabilisticSharpe(double sharpe, double benchmark, int count, double skew, double kurtosis)
    {
        if (count < 2) return 0;
        double denom = 1 - skew * sharpe + (kurtosis - 1) / 4 * sharpe * sharpe;
        if (denom <= 1e-9) denom = 1e-9;
        return NormalCdf((sharpe - benchmark) * Math.Sqrt(count - 1) / Math.Sqrt(denom));
    }

    /// <summary>
    /// Sharpe deflactado: compara contra el mejor Sharpe que se esperaría por pura suerte después de probar
    /// <paramref name="trials"/> configuraciones cuyos Sharpe tienen varianza <paramref name="trialVariance"/>.
    /// La varianza nunca se toma menor que la del ruido de estimar un Sharpe con esa muestra (1/n).
    /// </summary>
    public static double DeflatedSharpe(double sharpe, int count, double skew, double kurtosis, int trials, double trialVariance)
    {
        if (count < 2) return 0;
        return ProbabilisticSharpe(sharpe, DeflatedBenchmark(count, trials, trialVariance), count, skew, kurtosis);
    }

    /// <summary>
    /// La vara del Sharpe deflactado: el Sharpe por operación que mostraría por puro azar la mejor de
    /// <paramref name="trials"/> estrategias sin ninguna ventaja, con <paramref name="count"/> operaciones cada una.
    /// </summary>
    public static double DeflatedBenchmark(int count, int trials, double trialVariance) =>
        count < 2 ? 0 : Math.Sqrt(Math.Max(trialVariance, 1.0 / count)) * ExpectedMaxZ(Math.Max(1, trials));

    /// <summary>
    /// Desgaste de la ventaja: ¿rinde igual al final que al principio? Ganancia media por operación del primer y del último
    /// tercio (por orden de salida) y la t de la pendiente del resultado por operación en el tiempo (negativa = se apaga).
    /// </summary>
    public static (decimal Early, decimal Late, double T, int Count) EdgeDecay(IReadOnlyList<ClosedTrade> trades)
    {
        var list = trades.OrderBy(t => t.ExitTime).ToList();
        int n = list.Count;
        if (n < 6) return (0, 0, 0, n);
        int third = n / 3;
        decimal early = list.Take(third).Average(t => t.NetPnl);
        decimal late = list.Skip(n - third).Average(t => t.NetPnl);
        double mx = (n - 1) / 2.0, my = list.Average(t => (double)t.NetPnl);
        double sxx = 0, sxy = 0;
        for (int i = 0; i < n; i++)
        {
            sxx += (i - mx) * (i - mx);
            sxy += (i - mx) * ((double)list[i].NetPnl - my);
        }

        double slope = sxx > 0 ? sxy / sxx : 0, sse = 0;
        for (int i = 0; i < n; i++)
        {
            double e = (double)list[i].NetPnl - (my + slope * (i - mx));
            sse += e * e;
        }

        double se = sxx > 0 ? Math.Sqrt(sse / (n - 2) / sxx) : 0;
        return (early, late, se > 0 ? slope / se : 0, n);
    }

    /// <summary>Eficiencia de la tendencia hasta la que se considera rango, y desde la que se considera tendencia.</summary>
    public const double RangeTrend = MarketContextTracker.RangeLimit, TrendingTrend = MarketContextTracker.TrendLimit;

    /// <summary>Percentiles de volatilidad (del último mes) que se consideran baja y alta.</summary>
    public const double LowVolatilityRank = MarketContextTracker.LowVolatility, HighVolatilityRank = MarketContextTracker.HighVolatility;

    /// <summary>
    /// Resultado de las operaciones según el mercado en que entraron: en rango, mixto o en tendencia (eficiencia de la
    /// tendencia de las últimas 24 horas; la tendencia, además, alcista o bajista) y con volatilidad baja o alta (el tercio de
    /// menos y el de más volatilidad). Vacío si las operaciones no traen el régimen (corridas de antes de que existiera).
    /// </summary>
    public static List<RegimeStats> Regimes(IReadOnlyList<ClosedTrade> trades)
    {
        var known = trades.Where(t => t.EntryTrend != null).ToList();
        var list = new List<RegimeStats>();
        if (known.Count == 0) return list;
        static RegimeStats Of(string name, IEnumerable<ClosedTrade> group, bool detail = false)
        {
            var g = group.ToList();
            decimal win = g.Where(t => t.NetPnl > 0).Sum(t => t.NetPnl), loss = -g.Where(t => t.NetPnl < 0).Sum(t => t.NetPnl);
            return new RegimeStats(name, g.Count, g.Sum(t => t.NetPnl), loss > 0 ? Math.Round(win / loss, 2) : win > 0 ? 999 : 0, detail);
        }

        list.Add(Of("rango", known.Where(t => t.EntryTrend <= RangeTrend)));
        list.Add(Of("mixto", known.Where(t => t.EntryTrend > RangeTrend && t.EntryTrend < TrendingTrend)));
        var trending = known.Where(t => t.EntryTrend >= TrendingTrend).ToList();
        list.Add(Of("tendencia", trending));
        // la tendencia, separada en alcista y bajista (detalle: no cuenta aparte para la regla de regímenes)
        if (trending.Count > 0 && trending.Count(t => t.EntryMarketDirection != null) >= trending.Count * 0.8)
        {
            list.Add(Of("tendencia alcista", trending.Where(t => t.EntryMarketDirection > 0), detail: true));
            list.Add(Of("tendencia bajista", trending.Where(t => t.EntryMarketDirection < 0), detail: true));
        }
        // volatilidad: con el percentil del último mes (el mismo que usa el filtro de contexto); las corridas de antes,
        // por tercios de las propias operaciones
        if (known.Count(t => t.EntryVolatilityRank != null) >= known.Count * 0.8)
        {
            list.Add(Of("volatilidad baja", known.Where(t => t.EntryVolatilityRank <= LowVolatilityRank)));
            list.Add(Of("volatilidad alta", known.Where(t => t.EntryVolatilityRank >= HighVolatilityRank)));
        }
        else
        {
            var vols = known.Where(t => t.EntryVolatility != null).Select(t => t.EntryVolatility!.Value).OrderBy(v => v).ToList();
            if (vols.Count >= 9)
            {
                double low = vols[vols.Count / 3], high = vols[vols.Count * 2 / 3];
                list.Add(Of("volatilidad baja", known.Where(t => t.EntryVolatility < low)));
                list.Add(Of("volatilidad alta", known.Where(t => t.EntryVolatility >= high)));
            }
        }

        return list;
    }

    /// <summary>
    /// ¿Gana solo en ciertos contextos? Prueba los filtros de régimen (solo rango, sin tendencia, solo tendencia, sin rango,
    /// solo tendencia alcista o bajista) y de volatilidad (percentil del último mes) sobre las operaciones fuera de muestra y se queda con el que más mejora:
    /// lo que deja tiene que ganar con PF ≥ 1,5 y al menos 20 operaciones (y el 30 % de todas), y lo que saca tiene que
    /// perder. null si no hay un filtro así (o las operaciones no traen el contexto).
    /// </summary>
    public static ContextFilterSuggestion? SuggestContextFilter(IReadOnlyList<ClosedTrade> trades)
    {
        var known = trades.Where(t => t.EntryTrend != null).ToList();
        if (known.Count < 30) return null;
        bool ranks = known.Count(t => t.EntryVolatilityRank != null) >= known.Count * 0.8;
        bool directions = known.Count(t => t.EntryMarketDirection != null) >= known.Count * 0.8;
        var regimes = new List<RegimeFilter> { RegimeFilter.All, RegimeFilter.RangeOnly, RegimeFilter.NoTrend, RegimeFilter.TrendOnly, RegimeFilter.NoRange };
        if (directions) regimes.AddRange(new[] { RegimeFilter.UpTrendOnly, RegimeFilter.DownTrendOnly });
        var vols = ranks ? new[] { (0, 100), (0, 33), (0, 66), (34, 100), (67, 100) } : new[] { (0, 100) };

        static (decimal Net, decimal Pf, double T) Stats(List<ClosedTrade> g)
        {
            decimal win = g.Where(t => t.NetPnl > 0).Sum(t => t.NetPnl), loss = -g.Where(t => t.NetPnl < 0).Sum(t => t.NetPnl);
            double mean = g.Count > 0 ? g.Average(t => (double)t.NetPnl) : 0;
            double sd = g.Count > 1 ? Math.Sqrt(g.Sum(t => Math.Pow((double)t.NetPnl - mean, 2)) / (g.Count - 1)) : 0;
            return (g.Sum(t => t.NetPnl), loss > 0 ? win / loss : win > 0 ? 999 : 0, sd > 0 ? mean / sd * Math.Sqrt(g.Count) : 0);
        }

        var all = Stats(known);
        int minKept = Math.Max(20, (int)Math.Ceiling(known.Count * 0.3));
        ContextFilterSuggestion? best = null;
        double bestT = all.T;
        foreach (var regime in regimes)
        foreach (var (lo, hi) in vols)
        {
            if (regime == RegimeFilter.All && lo == 0 && hi == 100) continue;
            var kept = known.Where(t => InRegime(t, regime) && (!ranks || (t.EntryVolatilityRank is { } r && r >= lo && r <= hi))).ToList();
            if (kept.Count < minKept || kept.Count == known.Count) continue;
            var k = Stats(kept);
            var dropped = known.Except(kept).ToList();
            decimal droppedNet = dropped.Sum(t => t.NetPnl);
            if (k.Net <= 0 || k.Pf < 1.5m || droppedNet >= 0 || k.Pf < Math.Min(all.Pf, 99) + 0.3m || k.T <= bestT) continue;
            bestT = k.T;
            best = new ContextFilterSuggestion(regime, lo, hi, DescribeFilter(regime, lo, hi), kept.Count, k.Net, Math.Round(Math.Min(k.Pf, 999), 2), dropped.Count, droppedNet);
        }

        return best;
    }

    /// <summary>¿Entró en la condición de mercado que pide el filtro de régimen? (con el contexto guardado en la operación)</summary>
    private static bool InRegime(ClosedTrade t, RegimeFilter f) =>
        f == RegimeFilter.All || (MarketContextTracker.SetOf(f) & MarketContextTracker.SetOf(t.EntryTrend!.Value, t.EntryMarketDirection)) != 0;

    /// <summary>
    /// Qué deja y qué saca un filtro de contexto de unas operaciones (las que traen el contexto guardado): cuántas y con
    /// qué neto. La volatilidad solo se mira si las operaciones traen su percentil.
    /// </summary>
    public static (int Kept, decimal KeptNet, int Dropped, decimal DroppedNet) ApplyFilter(IReadOnlyList<ClosedTrade> trades, RegimeFilter regime, int minRank, int maxRank)
    {
        var known = trades.Where(t => t.EntryTrend != null).ToList();
        bool ranks = known.Count > 0 && known.Count(t => t.EntryVolatilityRank != null) >= known.Count * 0.8;
        var kept = known.Where(t => InRegime(t, regime) && (!ranks || (t.EntryVolatilityRank is { } r && r >= minRank && r <= maxRank))).ToList();
        var dropped = known.Except(kept).ToList();
        return (kept.Count, kept.Sum(t => t.NetPnl), dropped.Count, dropped.Sum(t => t.NetPnl));
    }

    /// <summary>
    /// En palabras, las condiciones en que opera: «solo en rango», «rango o tendencia alcista», «todo menos tendencia
    /// bajista»… null si son todas (sin filtro).
    /// </summary>
    public static string? DescribeRegimes(RegimeSet set)
    {
        if (set is RegimeSet.All or RegimeSet.None) return null;
        if (set == (RegimeSet.UpTrend | RegimeSet.DownTrend)) return "solo en tendencia";
        if (set == (RegimeSet.Range | RegimeSet.Mixed)) return "sin tendencia (rango o mixto)";
        if (set == (RegimeSet.Mixed | RegimeSet.UpTrend | RegimeSet.DownTrend)) return "sin rango (mixto o tendencia)";
        static string Name(RegimeSet one) => one switch
        {
            RegimeSet.Range => "rango",
            RegimeSet.Mixed => "mixto",
            RegimeSet.UpTrend => "tendencia alcista",
            _ => "tendencia bajista",
        };
        var on = new[] { RegimeSet.Range, RegimeSet.Mixed, RegimeSet.UpTrend, RegimeSet.DownTrend }.Where(x => set.HasFlag(x)).ToList();
        if (on.Count == 1) return "solo en " + Name(on[0]);
        if (on.Count == 3) return "todo menos " + Name(RegimeSet.All & ~set);
        return string.Join(" o ", on.Select(Name));
    }

    /// <summary>«solo en rango, volatilidad hasta el percentil 66».</summary>
    public static string DescribeFilter(RegimeFilter regime, int minRank, int maxRank)
    {
        var parts = new List<string>();
        if (DescribeRegimes(MarketContextTracker.SetOf(regime)) is { } r) parts.Add(r);
        if (minRank > 0 && maxRank < 100) parts.Add($"volatilidad entre los percentiles {minRank} y {maxRank}");
        else if (maxRank < 100) parts.Add(maxRank <= 33 ? "volatilidad baja" : $"volatilidad hasta el percentil {maxRank}");
        else if (minRank > 0) parts.Add(minRank >= 67 ? "volatilidad alta" : $"volatilidad desde el percentil {minRank}");
        return string.Join(", ", parts);
    }

    /// <summary>Número de un valor de parámetro (los cargados de JSON vienen como JsonElement).</summary>
    public static double? ToDouble(object? v) => v switch
    {
        null => null,
        JsonElement { ValueKind: JsonValueKind.Number } je => je.GetDouble(),
        JsonElement { ValueKind: JsonValueKind.String } je when double.TryParse(je.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        JsonElement => null,
        bool => null,
        IConvertible c when double.TryParse(Convert.ToString(c, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => null,
    };

    /// <summary>
    /// Inestabilidad de los parámetros que eligió cada ventana del walk-forward: desvío de los valores elegidos sobre el
    /// ancho del rango probado, promediado entre parámetros (0 = siempre el mismo; más de ~0.35 = saltan por todo el rango).
    /// </summary>
    public static double? ParameterInstability(IReadOnlyList<WalkForwardWindow> windows, IReadOnlyList<ParameterRange>? ranges)
    {
        if (windows.Count < 2) return null;
        var names = ranges is { Count: > 0 }
            ? ranges.Where(r => r.Enabled && r.Max > r.Min).Select(r => r.Name).ToList()
            : windows.SelectMany(w => w.BestParameters.Keys).Distinct().ToList();
        var scores = new List<double>();
        foreach (var name in names)
        {
            var range = ranges?.FirstOrDefault(r => r.Name == name);
            var values = windows.Select(w => w.BestParameters.TryGetValue(name, out var v) ? range?.Numeric(v) ?? ToDouble(v) : null).Where(v => v != null).Select(v => v!.Value).ToList();
            if (values.Count < 2) continue;
            double mean = values.Average();
            double sd = Math.Sqrt(values.Sum(x => (x - mean) * (x - mean)) / values.Count);
            double width = range != null ? range.Max - range.Min : 0;
            if (width > 0) scores.Add(sd / width);
            else if (Math.Abs(mean) > 1e-12) scores.Add(Math.Min(1, sd / Math.Abs(mean)));
        }

        return scores.Count == 0 ? 0 : scores.Average();
    }

    /// <summary>
    /// Meseta alrededor de la mejor combinación: de las vecinas (un paso de distancia en cada parámetro), qué parte gana
    /// plata. Un pico aislado (las vecinas pierden) suele ser sobreajuste.
    /// </summary>
    public static (double ProfitableShare, int Neighbors)? Plateau(OptimizationResult opt)
    {
        if (opt.Rows.Count < 3 || opt.Best is not { } best) return null;
        var ranges = opt.Ranges.Where(r => r.Enabled && r.Step > 0 && r.Max > r.Min).ToList();
        if (ranges.Count == 0) return null;
        int neighbors = 0, profitable = 0;
        foreach (var row in opt.Rows.Skip(1))
        {
            bool near = true, same = true;
            foreach (var r in ranges)
            {
                double? a = best.Parameters.TryGetValue(r.Name, out var av) ? r.Numeric(av) : null;
                double? b = row.Parameters.TryGetValue(r.Name, out var bv) ? r.Numeric(bv) : null;
                if (a == null || b == null) continue;
                double diff = Math.Abs(a.Value - b.Value);
                if (diff > r.Step * 1.0001) near = false;
                if (diff > 1e-9) same = false;
            }

            if (!near || same) continue;
            neighbors++;
            if (row.Metrics.NetProfit > 0) profitable++;
        }

        return neighbors == 0 ? null : ((double)profitable / neighbors, neighbors);
    }

    /// <summary>Parte de las combinaciones probadas que ganan plata (robustez general de la idea).</summary>
    public static double? ProfitableShare(OptimizationResult opt) =>
        opt.Rows.Count == 0 ? null : (double)opt.Rows.Count(r => r.Metrics.NetProfit > 0) / opt.Rows.Count;
}

/// <summary>Resultado de una prueba de la validación.</summary>
public enum GateStatus
{
    Pass,
    Warn,
    Fail,
}

/// <summary>Una prueba de la validación con su explicación.</summary>
public sealed record GateCheck(string Name, GateStatus Status, string Text);

/// <summary>Lo que se le pide a una estrategia validada con walk-forward.</summary>
public sealed class GateInput
{
    public required WalkForwardResult WalkForward { get; init; }
    public OptimizationResult? Optimization { get; init; }

    /// <summary>Configuraciones (estrategia × mercado) que ya se probaron, contando esta.</summary>
    public int Trials { get; init; } = 1;

    /// <summary>Varianza de los Sharpe por operación de esas configuraciones.</summary>
    public double TrialVariance { get; init; }

    public int MinTrades { get; init; } = 30;
}

/// <summary>Resultado de las operaciones que entraron en un régimen de mercado.</summary>
/// <summary>Resultado en una condición de mercado. Detail: subgrupo de otra (tendencia alcista dentro de tendencia).</summary>
public sealed record RegimeStats(string Name, int Trades, decimal Net, decimal ProfitFactor, bool Detail = false);

/// <summary>
/// Filtro de contexto que convendría: en ese contexto la estrategia gana con claridad y fuera de él pierde. Son los
/// valores para sus parámetros RegimeFilter, MinVolatilityRank y MaxVolatilityRank.
/// </summary>
public sealed record ContextFilterSuggestion(RegimeFilter Regime, int MinVolatilityRank, int MaxVolatilityRank, string Description,
    int Trades, decimal Net, decimal ProfitFactor, int DroppedTrades, decimal DroppedNet);

/// <summary>Veredicto de la validación con todas las pruebas.</summary>
public sealed class GateResult
{
    public GateStatus Status { get; init; }
    public List<GateCheck> Checks { get; init; } = new();
    public double TradeSharpe { get; init; }
    public double Deflated { get; init; }
    public double? Instability { get; init; }
    public (double ProfitableShare, int Neighbors)? Plateau { get; init; }
    public double? ProfitableCombos { get; init; }

    /// <summary>
    /// No pasa por falta de muestra, no por falta de ventaja: gana fuera de muestra y lo único que falla depende de cuántas
    /// operaciones hay (la muestra, el Sharpe deflactado y, con pocas operaciones, la consistencia y la eficiencia). Con la
    /// misma ventaja por operación pasaría con más operaciones: antes de descartarla hay que juntar más historia.
    /// </summary>
    public bool NeedsMoreSample { get; init; }

    /// <summary>Cuántas veces más operaciones fuera de muestra harían falta para concluir (con la misma ventaja por operación).</summary>
    public double SampleFactor { get; init; } = 1;

    /// <summary>Resultado fuera de muestra por régimen de mercado (rango, mixto, tendencia, volatilidad baja y alta).</summary>
    public List<RegimeStats> Regimes { get; init; } = new();

    /// <summary>Desgaste: ganancia media por operación del primer y último tercio, y la t de la pendiente en el tiempo.</summary>
    public decimal DecayEarly { get; init; }
    public decimal DecayLate { get; init; }
    public double DecayT { get; init; }

    public string StatusText => Status switch
    {
        GateStatus.Pass => "pasa",
        GateStatus.Warn => "dudosa",
        _ when NeedsMoreSample => "falta muestra",
        _ => "no pasa",
    };
}

/// <summary>
/// Reglas fijas contra la sobreoptimización (no las decide un agente): se juzga solo lo de fuera de muestra, con muestra
/// suficiente, eficiencia del walk-forward, consistencia entre ventanas, estabilidad de parámetros, meseta y Sharpe
/// deflactado por la cantidad de cosas que se probaron.
/// </summary>
public static class ValidationGate
{
    public const double MinProfitFactor = 1.2, FailProfitFactor = 1.0;
    public const double MinEfficiency = 0.5, FailEfficiency = 0.3;
    public const double MinWindowsPercent = 60, FailWindowsPercent = 40;
    public const int MinWindows = 4;
    public const double MaxInstability = 0.35;
    public const double MinPlateau = 0.5, MinProfitableCombos = 0.25;
    public const double MinDeflated = 0.9, FailDeflated = 0.5;

    /// <summary>Cómo empieza la línea del Sharpe deflactado (las de antes decían «Probabilidad de que no sea suerte»).</summary>
    public const string LuckPassPrefix = "Probabilidad de que la ventaja sea real y no suerte:", LuckFailPrefix = "Puede ser suerte:";

    /// <summary>¿Es la línea del Sharpe deflactado? (la de ahora o la de las pruebas guardadas antes)</summary>
    public static bool IsLuckLine(string text) =>
        text.StartsWith(LuckFailPrefix, StringComparison.Ordinal) || text.StartsWith(LuckPassPrefix, StringComparison.Ordinal) ||
        text.StartsWith("Probabilidad de que no sea suerte", StringComparison.Ordinal);

    /// <summary>Hasta cuántas veces más operaciones se considera ir a buscar para que el Sharpe deflactado pase.</summary>
    public const double MaxSampleFactor = 8;

    /// <summary>Pruebas que dependen del tamaño de la muestra (con pocas operaciones, y con suficientes).</summary>
    private static readonly HashSet<string> SmallSampleChecks = new() { "Muestra", "Sharpe deflactado", "Consistencia", "Eficiencia", "Ventanas", "Sobreajuste", "Caminos" };

    /// <summary>Sobreajuste (PBO): la elegida pierde fuera de muestra en menos del 20 % de las particiones (pasa) o en la mitad o más (no pasa).</summary>
    public const double MaxPboLoss = 0.2, FailPboLoss = 0.5;

    /// <summary>Caminos de la CPCV que tienen que ganar (y la mediana tiene que ser positiva).</summary>
    public const double MinProfitablePaths = 0.7;
    private static readonly HashSet<string> LargeSampleChecks = new() { "Sharpe deflactado", "Ventanas" };

    public static GateResult Evaluate(GateInput input)
    {
        var wf = input.WalkForward;
        var m = wf.OutSampleMetrics;
        var checks = new List<GateCheck>();
        void Add(string name, GateStatus st, string text) => checks.Add(new GateCheck(name, st, text));
        var es = TradingHub.Common.UiCulture.Current;

        int trades = m.TotalTrades;
        Add("Muestra", trades >= input.MinTrades ? GateStatus.Pass : GateStatus.Fail,
            trades >= input.MinTrades ? $"{trades} operaciones fuera de muestra"
            : trades == 0 ? "No operó fuera de muestra (0 operaciones): sus condiciones no se dieron en el período probado"
            : $"Solo {trades} operaciones fuera de muestra (mínimo {input.MinTrades}): no alcanza para concluir nada");
        Add("Resultado", m.NetProfit > 0 ? GateStatus.Pass : GateStatus.Fail,
            m.NetProfit > 0 ? $"Gana {m.NetProfit.ToString("N2", es)} fuera de muestra" : $"Pierde fuera de muestra ({m.NetProfit.ToString("N2", es)})");
        decimal pf = m.ProfitFactor;
        Add("Profit factor", pf >= (decimal)MinProfitFactor ? GateStatus.Pass : pf >= (decimal)FailProfitFactor ? GateStatus.Warn : GateStatus.Fail,
            $"PF fuera de muestra {(pf >= 999 ? "∞" : pf.ToString("0.00", es))} (pide ≥ {MinProfitFactor.ToString("0.0", es)})");
        double wfe = wf.WalkForwardEfficiency;
        Add("Eficiencia", wfe >= MinEfficiency ? GateStatus.Pass : wfe >= FailEfficiency ? GateStatus.Warn : GateStatus.Fail,
            wfe >= MinEfficiency ? $"Eficiencia walk-forward {wfe.ToString("0.00", es)}: rinde fuera de muestra parecido a lo optimizado"
            : wfe >= FailEfficiency ? $"Eficiencia walk-forward {wfe.ToString("0.00", es)}: pierde bastante fuera de muestra (pide ≥ {MinEfficiency.ToString("0.0", es)})"
            : $"Eficiencia walk-forward {wfe.ToString("0.00", es)}: sobreoptimizada (lo optimizado no se repite fuera de muestra)");
        double win = wf.ProfitableWindowsPercent;
        Add("Consistencia", win >= MinWindowsPercent ? GateStatus.Pass : win >= FailWindowsPercent ? GateStatus.Warn : GateStatus.Fail,
            $"{win.ToString("0", es)}% de las ventanas fuera de muestra ganan (pide ≥ {MinWindowsPercent}%)");
        Add("Ventanas", wf.Windows.Count >= MinWindows ? GateStatus.Pass : GateStatus.Warn,
            wf.Windows.Count >= MinWindows ? $"{wf.Windows.Count} ventanas de walk-forward" : $"Solo {wf.Windows.Count} ventanas: poca evidencia de que se repita en el tiempo");

        double? instability = Robustness.ParameterInstability(wf.Windows, wf.Ranges);
        if (instability is { } inst)
            Add("Estabilidad", inst <= MaxInstability ? GateStatus.Pass : GateStatus.Warn,
                inst <= MaxInstability ? $"Los parámetros elegidos son estables entre ventanas (dispersión {inst.ToString("0.00", es)})"
                : $"Los parámetros elegidos saltan por todo el rango entre ventanas (dispersión {inst.ToString("0.00", es)}): la ventaja depende de afinarlos");

        (double, int)? plateau = null;
        double? profitableCombos = null;
        if (input.Optimization is { } opt)
        {
            plateau = Robustness.Plateau(opt);
            if (plateau is { } p)
                Add("Meseta", p.Item1 >= MinPlateau ? GateStatus.Pass : GateStatus.Warn,
                    p.Item1 >= MinPlateau ? $"{p.Item1 * 100:0}% de las combinaciones vecinas a la mejor también ganan (meseta)"
                    : $"Solo {p.Item1 * 100:0}% de las vecinas a la mejor combinación ganan: pico aislado, típico de sobreajuste");
            profitableCombos = Robustness.ProfitableShare(opt);
            if (profitableCombos is { } pc)
                Add("Robustez", pc >= MinProfitableCombos ? GateStatus.Pass : GateStatus.Warn,
                    $"{pc * 100:0}% de todas las combinaciones probadas ganan plata");
        }

        // sobreajuste (Bailey, Borwein, López de Prado y Zhu): con la mitad de la historia se elige la mejor combinación y se
        // mira la otra mitad, de todas las formas posibles. Lo que decide es si la elegida pierde plata en la otra mitad; la
        // PBO (si queda en la mitad de abajo de las demás) se informa: alta con todas ganando es una meseta, no un problema
        if (wf.Pbo is { } pbo && wf.PboLoss is { } pboLoss && wf.PboSplits > 0)
            Add("Sobreajuste", pboLoss < MaxPboLoss ? GateStatus.Pass : pboLoss < FailPboLoss ? GateStatus.Warn : GateStatus.Fail,
                (pboLoss < MaxPboLoss
                    ? $"La optimización no elige suerte: la combinación que mejor le fue con una mitad de la historia ganó plata en la otra mitad en el {(1 - pboLoss) * 100:0}% de las {wf.PboSplits} particiones"
                    : pboLoss < FailPboLoss
                        ? $"Algo de sobreajuste: la combinación que mejor le fue con una mitad de la historia perdió plata en la otra mitad en el {pboLoss * 100:0}% de las {wf.PboSplits} particiones (pide menos del {MaxPboLoss * 100:0}%)"
                        : $"Sobreoptimizada: la combinación que mejor le fue con una mitad de la historia perdió plata en la otra mitad en el {pboLoss * 100:0}% de las {wf.PboSplits} particiones; elegir la mejor es elegir suerte") +
                $" (entre las {wf.Evaluated} combinaciones probadas, quedó en la mitad de abajo en el {pbo * 100:0}%: PBO)");

        // caminos fuera de muestra de la validación cruzada combinatoria purgada: no un solo camino como el walk-forward
        if (wf.CpcvPaths.Count >= 3)
        {
            var paths = wf.CpcvPaths.OrderBy(p => p.Net).ToList();
            int good = paths.Count(p => p.Net > 0);
            decimal median = paths[paths.Count / 2].Net, worst = paths[0].Net;
            double share = (double)good / paths.Count;
            Add("Caminos", median <= 0 ? GateStatus.Fail : share >= MinProfitablePaths ? GateStatus.Pass : GateStatus.Warn,
                $"{good} de {paths.Count} caminos fuera de muestra ganan (validación cruzada con {wf.CpcvGroups} bloques y {wf.EmbargoDays} día(s) de embargo): " +
                $"el del medio {median.ToString("N0", es)}, el peor {worst.ToString("N0", es)}" +
                (median <= 0 ? ". Con otra forma de partir la historia, pierde" : share >= MinProfitablePaths ? "" : $" (pide que ganen al menos el {MinProfitablePaths * 100:0}%)"));
        }

        var (sr, skew, kurt, n) = Robustness.TradeSharpe(wf.OutSampleTrades);
        double dsr = Robustness.DeflatedSharpe(sr, n, skew, kurt, input.Trials, input.TrialVariance);
        double bar = Robustness.DeflatedBenchmark(n, input.Trials, input.TrialVariance);
        // en plata, para que se entienda: cuánto gana por operación, cuánto varía cada una y cuánto mostraría por pura
        // suerte la mejor de las estrategias sin ventaja que se probaron, con esa misma variación
        var pnls = wf.OutSampleTrades.Select(t => (double)t.NetPnl).ToList();
        double mean = pnls.Count > 0 ? pnls.Average() : 0;
        double sd = pnls.Count > 1 ? Math.Sqrt(pnls.Sum(x => (x - mean) * (x - mean)) / (pnls.Count - 1)) : 0;
        string money = n >= 2 && sd > 0
            ? $". Gana en promedio {mean.ToString("N0", es)} por operación, pero cada operación varía unos ±{sd.ToString("N0", es)}: entre {input.Trials} estrategias sin ninguna ventaja " +
              $"(las que se probaron), la de mejor suerte suele mostrar ~{(bar * sd).ToString("N0", es)} por operación con esa variación"
            : "";
        Add("Sharpe deflactado", dsr >= MinDeflated ? GateStatus.Pass : dsr >= FailDeflated ? GateStatus.Warn : GateStatus.Fail,
            dsr >= MinDeflated
                ? $"{LuckPassPrefix} {dsr * 100:0}% (pide ≥ {MinDeflated * 100:0}%, descontando las {input.Trials} estrategias que se probaron){money}"
                : $"{LuckFailPrefix} solo {dsr * 100:0}% de probabilidad de que la ventaja sea real (pide ≥ {MinDeflated * 100:0}%){money}" +
                  (dsr < FailDeflated ? ". Con esto no se distingue del azar" : ""));

        // desgaste de la ventaja: con 30 operaciones o más, ¿el último tercio rinde como el primero?
        var (early, late, tDecay, _) = Robustness.EdgeDecay(wf.OutSampleTrades);
        if (trades >= 30)
        {
            bool dying = early > 0 && late < 0 && tDecay <= -2;
            bool fading = (early > 0 && late < early * 0.5m) || tDecay <= -1.5;
            Add("Desgaste", dying ? GateStatus.Fail : fading ? GateStatus.Warn : GateStatus.Pass,
                dying ? $"La ventaja se está apagando: el último tercio de las operaciones pierde ({late.ToString("N2", es)} por operación) cuando el primero ganaba {early.ToString("N2", es)} (tendencia en contra, t {tDecay.ToString("0.0", es)})"
                : fading ? $"La ventaja se achica con el tiempo: el último tercio de las operaciones rinde {late.ToString("N2", es)} por operación contra {early.ToString("N2", es)} del primero"
                : $"La ventaja se sostiene en el tiempo: el último tercio de las operaciones rinde {late.ToString("N2", es)} por operación (el primero {early.ToString("N2", es)})");
        }

        // regímenes de mercado: cómo le va en rango, en tendencia y con volatilidad baja o alta (grupos con muestra)
        var regimes = Robustness.Regimes(wf.OutSampleTrades);
        int minGroup = Math.Max(8, trades * 15 / 100);
        var judged = regimes.Where(r => !r.Detail && r.Trades >= minGroup).ToList();
        if (judged.Count >= 2)
        {
            string Line(RegimeStats r) => $"{r.Name} ({r.Trades} op, PF {(r.ProfitFactor >= 999 ? "∞" : r.ProfitFactor.ToString("0.00", es))})";
            var losing = judged.Where(r => r.Net < 0).ToList();
            var winning = judged.Except(losing).ToList();
            Add("Regímenes", losing.Count == 0 ? GateStatus.Pass : GateStatus.Warn,
                losing.Count == 0 ? $"Gana en todas las condiciones de mercado con muestra: {string.Join(", ", judged.Select(Line))}"
                : winning.Count == 0 ? $"Pierde en todas las condiciones de mercado: {string.Join(", ", losing.Select(Line))}"
                : $"Depende del régimen: pierde en {string.Join(", ", losing.Select(Line))} y gana en {string.Join(", ", winning.Select(Line))}. Conviene un filtro de régimen");
        }

        var status = checks.Any(c => c.Status == GateStatus.Fail) ? GateStatus.Fail : checks.Any(c => c.Status == GateStatus.Warn) ? GateStatus.Warn : GateStatus.Pass;
        var (needs, factor) = status == GateStatus.Pass ? (false, 1.0)
            : SampleNeed(checks.Select(c => (c.Name, c.Status)), m.NetProfit, trades, input.MinTrades, sr, skew, kurt, input.Trials, input.TrialVariance, dsr);
        return new GateResult
        {
            Status = status,
            Checks = checks,
            TradeSharpe = sr,
            Deflated = dsr,
            Instability = instability,
            Plateau = plateau,
            ProfitableCombos = profitableCombos,
            NeedsMoreSample = needs,
            SampleFactor = factor,
            Regimes = regimes,
            DecayEarly = early,
            DecayLate = late,
            DecayT = tDecay,
        };
    }

    /// <summary>
    /// ¿Le falta muestra o le falta ventaja? Solo si gana fuera de muestra y todo lo que falla depende del tamaño de la
    /// muestra. Con 10 operaciones o más se proyecta el Sharpe deflactado con la misma ventaja por operación: si ni con
    /// <see cref="MaxSampleFactor"/> veces más operaciones pasaría, no es la muestra, es que no se distingue del azar.
    /// Con menos de 10 el Sharpe no dice nada todavía: alcanza con que gane.
    /// </summary>
    public static (bool Needs, double Factor) SampleNeed(IEnumerable<(string Name, GateStatus Status)> checks, decimal net, int trades, int minTrades,
                                                         double sr, double skew, double kurt, int trials, double trialVariance, double dsr)
    {
        if (net <= 0 || trades <= 0 || (trades >= minTrades && dsr >= MinDeflated)) return (false, 1);
        var bound = trades < minTrades ? SmallSampleChecks : LargeSampleChecks;
        if (checks.Any(c => c.Status == GateStatus.Fail && !bound.Contains(c.Name))) return (false, 1);
        double factor = Math.Max(1, (double)minTrades / trades);
        if (trades < 10 || sr <= 0) return (true, Math.Round(factor, 2));
        double f = factor;
        while (Robustness.DeflatedSharpe(sr, (int)Math.Ceiling(trades * f), skew, kurt, trials, trialVariance) < MinDeflated)
        {
            f *= 1.15;
            if (f > MaxSampleFactor * factor) return (false, 1);
        }

        return (true, Math.Round(f, 2));
    }
}
