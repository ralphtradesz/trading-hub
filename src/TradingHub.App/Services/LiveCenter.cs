using System.Globalization;
using Avalonia.Threading;
using TradingHub.App.Controls;
using TradingHub.App.Views;
using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Strategies;

namespace TradingHub.App.Services;

/// <summary>Una estrategia en vivo vista desde un solo lugar, la haya lanzado quien la haya lanzado.</summary>
internal sealed class LiveEntry
{
    public required string Key { get; init; }

    /// <summary>panel (sección En vivo), chart (un gráfico) u otro origen.</summary>
    public required string Source { get; init; }

    public required LiveRunnerInfo Info { get; set; }
    public LiveStrategyRunner? Runner { get; init; }
    public ChartTab? Chart { get; init; }
    public ChartStrategy? ChartStrategy { get; init; }

    /// <summary>Su contabilidad (en memoria si corre; si no, la guardada).</summary>
    public LiveLedger Ledger { get; set; } = null!;

    public bool Running => Runner?.IsRunning == true;

    /// <summary>Bloqueada: el riesgo y los parámetros no se cambian desde En vivo.</summary>
    public bool Locked => IsManaged(Source);

    /// <summary>La gestiona otro y no se toca desde En vivo (en esta versión, ninguna).</summary>
    public static bool IsManaged(string source) => false;

    /// <summary>En un gráfico, esperando que la reanudes (se volvió a abrir Trading Hub).</summary>
    public bool WaitingResume => ChartStrategy is { Live: true, Paused: true } && Runner is not { IsRunning: true };

    public bool Paused => Runner?.Paused ?? Info.Paused;

    public string SourceLabel => Source == "chart" ? "Gráfico" : "En vivo";

    public string StateLabel =>
        Running ? Runner!.DailyLimitActive ? "Frenada por hoy" : Paused ? "Pausada" : "Corriendo"
        : WaitingResume ? "Esperando que la reanudes"
        : Runner?.IsClosing == true ? "Cerrando la posición"
        : "Detenida";

    public decimal OpenPnL => Running ? Ledger.OpenPnL(Runner!.Data.LastPrice) : 0;
    public decimal Net => Ledger.Realized + OpenPnL;
}

/// <summary>La próxima orden de una estrategia: cuántos contratos, de cuál y si es el micro (con su equivalente en el mini).</summary>
internal readonly record struct OrderPlan(decimal Units, string Symbol, string SignalSymbol, bool Micro, bool AtMinimum, decimal Equivalent)
{
    private static readonly System.Globalization.CultureInfo Es = TradingHub.Common.UiCulture.Current;

    /// <summary>«5 MNQ», «1 NQ» o «en cero».</summary>
    public string Text => Units <= 0 ? "en cero" : $"{Units.ToString("0.####", Es)} {Symbol}";

    /// <summary>Aclaración: «micro · equivale a 0,5 NQ», «mínimo: no llega a 1 contrato» o vacío.</summary>
    public string Detail =>
        Units <= 0 ? "no abre operaciones nuevas"
        : Micro ? $"micro · equivale a {Equivalent.ToString("0.####", Es)} {SignalSymbol}"
        : AtMinimum ? "mínimo: el tamaño no llega a 1 contrato"
        : "";
}

/// <summary>Lo necesario para lanzar una estrategia en vivo desde la sección En vivo.</summary>
internal sealed class LiveLaunch
{
    public required StrategyFile File { get; init; }
    public required string ProfileId { get; init; }
    public required Instrument Instrument { get; init; }
    public required string AccountId { get; init; }
    public required ChartPeriod Period { get; init; }
    public Dictionary<string, object?> Parameters { get; init; } = new();
    public string? TimeZoneId { get; init; }
    public TimeSpan SessionStart { get; init; }
    public decimal Size { get; init; } = 1;
    public decimal MaxDailyLoss { get; init; }
    public decimal MaxDrawdown { get; init; }
    public bool CloseOnLimit { get; init; } = true;
    public string? PortfolioId { get; init; }
    public bool Paused { get; init; }
}

/// <summary>Un portafolio con sus estrategias ya resueltas.</summary>
internal sealed class LivePortfolioView
{
    public required LivePortfolioDef Def { get; init; }
    public required List<LiveEntry> Members { get; init; }
    public bool HasReal => Members.Any(m => m.Info.IsReal);
}

/// <summary>
/// Todas las estrategias en vivo en un solo lugar: las del panel En vivo y las que ejecutan desde un gráfico. Desde acá se gestionan (tamaño, límites, parámetros, pausa,
/// detener, borrar), se arman los portafolios en vivo y corre el agente de riesgo de cada portafolio.
/// </summary>
internal sealed partial class LiveCenter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly AppServices _svc;
    private readonly HashSet<LiveStrategyRunner> _watched = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, (long Length, LiveLedger Ledger)> _ledgers = new();
    private readonly Dictionary<string, (decimal Mae, decimal Mfe, bool Exact)> _excursions = new();
    private DateTime _lastTick;
    private DateTime _proposalsStamp;

    public LiveCenter(AppServices svc)
    {
        _svc = svc;
        RiskAgent = new LiveRiskAgent(svc, this);
    }

    /// <summary>La sección Trading (para las estrategias de los gráficos y para mostrar operaciones).</summary>
    public Func<TradingView?>? Trading { get; set; }

    /// <summary>El agente de riesgo de los portafolios.</summary>
    public LiveRiskAgent RiskAgent { get; }

    /// <summary>El agente de riesgo está revisando algún portafolio.</summary>
    public bool RiskAgentBusy => RiskAgent.AnyRunning;

    /// <summary>Avisos importantes (límite tocado, propuestas del agente) para la barra de estado.</summary>
    public event Action<string>? Notice;

    /// <summary>Últimos avisos (los muestra la sección En vivo).</summary>
    public List<(DateTime Time, string Text)> Notices { get; } = new();

    internal void Notify(string text)
    {
        Notices.Add((DateTime.Now, text));
        if (Notices.Count > 50) Notices.RemoveAt(0);
        Notice?.Invoke(text);
    }

    // ===================================================================== registro

    /// <summary>Le pone a una estrategia que vuelve a correr lo que tenía guardado (tamaño, límites, pausa y portafolio).</summary>
    public static void Restore(LiveStrategyRunner r)
    {
        var saved = LiveStore.InfoFor(r.Key);
        if (saved == null) return;
        r.SizeMultiplier = saved.Size;
        r.MaxDailyLoss = saved.MaxDailyLoss;
        r.MaxDrawdown = saved.MaxDrawdown;
        r.CloseOnLimit = saved.CloseOnLimit;
        r.PortfolioId ??= saved.PortfolioId;
        if (saved.Paused) r.SetPaused(true);
    }

    /// <summary>Se suscribe a los avisos de una estrategia (una vez): límites tocados y órdenes ejecutadas.</summary>
    public void Watch(LiveStrategyRunner r)
    {
        if (!_watched.Add(r)) return;
        r.LimitHit += (_, msg) => Dispatcher.UIThread.Post(() =>
        {
            Notify("⚠ " + msg);
            _svc.SaveLiveState();
        });
        r.Executed += (_, msg) => Dispatcher.UIThread.Post(() => Notify(msg));
    }

    /// <summary>La que está corriendo con esa clave (de cualquier origen).</summary>
    public LiveStrategyRunner? Running(string key) =>
        _svc.Runners.FirstOrDefault(r => r.Key == key && r.IsRunning)
        ;

    private LiveLedger LedgerOf(string key, decimal pointValue)
    {
        long length = 0;
        try
        {
            var f = new FileInfo(LiveStore.LedgerFile(key));
            length = f.Exists ? f.Length : 0;
        }
        catch
        {
            // sin archivo
        }

        if (_ledgers.TryGetValue(key, out var c) && c.Length == length) return c.Ledger;
        var l = LiveStore.LoadLedger(key, pointValue);
        _ledgers[key] = (length, l);
        return l;
    }

    /// <summary>Todas: las que corren (panel y gráficos) y las guardadas que están detenidas.</summary>
    public List<LiveEntry> Entries(bool includeStopped = true)
    {
        var list = new List<LiveEntry>();
        var seen = new HashSet<string>();
        var stored = LiveStore.LoadRunners();

        // 1) gráficos: las que ejecutan y las que esperan que las reanudes
        if (Trading?.Invoke() is { } tv)
        {
            foreach (var chart in tv.AllCharts)
            {
                foreach (var cs in chart.Strategies.Where(c => c.Live))
                {
                    if (cs.Runner is { Persist: true } r && seen.Add(r.Key))
                    {
                        list.Add(new LiveEntry { Key = r.Key, Source = "chart", Info = r.Info(), Runner = r, Chart = chart, ChartStrategy = cs, Ledger = r.Ledger });
                        continue;
                    }

                    if (cs.Runner is { IsRunning: true }) continue;
                    var inst = chart.Model?.Instrument;
                    var info = stored.FirstOrDefault(i => i.StrategyId == cs.StrategyId && inst != null && i.FeedId == inst.FeedId && i.Symbol == inst.Symbol &&
                                                         (i.AccountId == cs.AccountId || (i.AccountId.Length == 0 && !LiveEntry.IsManaged(i.Source))));
                    string key = info?.Key ?? $"chart|{cs.StrategyId}|{inst?.FeedId}:{chart.Symbol}|{cs.AccountId}";
                    if (!seen.Add(key)) continue;
                    info ??= new LiveRunnerInfo
                    {
                        Key = key,
                        Strategy = cs.Overlay.Name,
                        StrategyId = cs.StrategyId,
                        FeedId = inst?.FeedId ?? chart.FeedId,
                        Symbol = chart.Symbol,
                        Period = chart.Model?.Period.Code ?? "",
                        Account = cs.AccountId,
                        AccountId = cs.AccountId,
                        Source = "chart",
                        PointValue = inst?.PointValue ?? 1,
                    };
                    list.Add(new LiveEntry { Key = key, Source = "chart", Info = info, Runner = cs.Runner, Chart = chart, ChartStrategy = cs, Ledger = LedgerOf(key, info.PointValue) });
                }
            }
        }

        // 2) las del panel (y las de gráficos que ya no están en un gráfico)
        foreach (var r in _svc.Runners.Where(r => r.Persist))
        {
            if (!r.IsRunning && !r.IsClosing && !includeStopped) continue;
            if (!seen.Add(r.Key)) continue;
            list.Add(new LiveEntry { Key = r.Key, Source = r.Source == "chart" ? "panel" : r.Source, Info = r.Info(), Runner = r, Ledger = r.Ledger });
        }


        // 4) guardadas y detenidas
        if (includeStopped)
        {
            foreach (var info in stored)
            {
                if (seen.Contains(info.Key)) continue;
                seen.Add(info.Key);
                info.Running = false;
                list.Add(new LiveEntry { Key = info.Key, Source = info.Source == "chart" ? "chart" : "panel", Info = info, Ledger = LedgerOf(info.Key, info.PointValue) });
            }
        }

        return list;
    }

    public LiveEntry? Find(string key) => Entries().FirstOrDefault(e => e.Key == key);

    // ===================================================================== gestión

    /// <summary>Cambia tamaño y límites (de una que corre o de lo guardado). Las bloqueadas no se tocan.</summary>
    public bool ApplyRisk(LiveEntry e, decimal size, decimal maxDailyLoss, decimal maxDrawdown, bool closeOnLimit)
    {
        if (e.Locked) return false;
        size = Math.Max(0, size);
        var r = e.Runner ?? Running(e.Key);
        if (r != null)
        {
            r.SizeMultiplier = size;
            r.MaxDailyLoss = Math.Max(0, maxDailyLoss);
            r.MaxDrawdown = Math.Max(0, maxDrawdown);
            r.CloseOnLimit = closeOnLimit;
            r.Log($"Riesgo cambiado: tamaño ×{size.ToString("0.##", Inv)} · pérdida máx. del día {(maxDailyLoss > 0 ? maxDailyLoss.ToString("0.##", Inv) : "sin límite")} · drawdown máx. {(maxDrawdown > 0 ? maxDrawdown.ToString("0.##", Inv) : "sin límite")}");
            _svc.SaveLiveState();
        }

        LiveStore.UpdateRunner(e.Key, i =>
        {
            i.Size = size;
            i.MaxDailyLoss = Math.Max(0, maxDailyLoss);
            i.MaxDrawdown = Math.Max(0, maxDrawdown);
            i.CloseOnLimit = closeOnLimit;
        });
        return true;
    }

    /// <summary>Aplica un tamaño aprobado (de una propuesta o de los botones de la tabla).</summary>
    public void ApplySize(string key, decimal size)
    {
        var e = Entries().FirstOrDefault(x => x.Key == key);
        if (e == null)
        {
            LiveStore.UpdateRunner(key, i => i.Size = size);
            return;
        }

        if (e.Locked) return;
        ApplyRisk(e, size, e.Runner?.MaxDailyLoss ?? e.Info.MaxDailyLoss, e.Runner?.MaxDrawdown ?? e.Info.MaxDrawdown, e.Runner?.CloseOnLimit ?? e.Info.CloseOnLimit);
    }

    /// <summary>Pausa o reanuda (sigue recibiendo el mercado; en pausa no abre operaciones nuevas).</summary>
    public void SetPaused(LiveEntry e, bool paused)
    {
        if (e.Locked) return;
        if (e.Runner is { IsRunning: true } r) r.SetPaused(paused);
        LiveStore.UpdateRunner(e.Key, i => i.Paused = paused);
        _svc.SaveLiveState();
    }

    /// <summary>Cierra la posición de la estrategia a mercado (sigue corriendo, salvo que esté pausada no vuelve a entrar sola).</summary>
    public void ClosePosition(LiveEntry e)
    {
        var r = e.Runner ?? Running(e.Key);
        r?.ClosePosition("Cierre manual");
        _svc.SaveLiveState();
    }

    /// <summary>Detiene (opcionalmente cerrando su posición). La de un gráfico vuelve a modo visual.</summary>
    public async Task StopAsync(LiveEntry e, bool close)
    {

        var r = e.Runner;
        if (r is { IsRunning: true })
        {
            r.StopReason = "La detuviste";
            r.Stop(close);
        }

        if (e.ChartStrategy is { } cs && e.Chart is { } chart && Trading?.Invoke() is { } tv)
        {
            cs.Live = false;
            cs.Paused = false;
            await tv.RunStrategyAsync(chart, cs);
        }

        _svc.SaveLiveState();
    }

    /// <summary>Vuelve a correr: la del gráfico se reanuda en su gráfico; la del panel, con lo guardado.</summary>
    public async Task<string?> ResumeAsync(LiveEntry e)
    {

        if (e.ChartStrategy is { } cs && e.Chart is { } chart && Trading?.Invoke() is { } tv)
        {
            cs.Live = true;
            cs.Paused = false;
            await tv.RunStrategyAsync(chart, cs);
            return cs.Overlay.Error;
        }

        var launch = await LaunchFromInfoAsync(e.Info);
        if (launch.Error != null) return launch.Error;
        var (runner, message) = await LaunchAsync(launch.Spec!);
        return runner == null ? message : null;
    }

    /// <summary>
    /// Al abrir Trading Hub: vuelve a arrancar las estrategias que corrían cuando se cerró (si está activado en Ajustes; las de
    /// cuentas reales solo si también lo activaste), con sus parámetros, tamaño, límites y pausa.
    /// </summary>
    public async Task ResumeAtStartAsync()
    {
        var s = _svc.Settings;
        var was = _svc.RunningAtLastClose;
        _svc.RunningAtLastClose = new(); // una sola vez por apertura
        if (!s.AutoResumeLive || was.Count == 0) return;
        var byKey = was.GroupBy(i => i.Key).ToDictionary(g => g.Key, g => g.First());
        var resumed = new List<string>();
        var waiting = new List<string>();
        var failed = new List<string>();
        foreach (var e in Entries().Where(e => byKey.ContainsKey(e.Key) && !e.Running && !e.Locked).ToList())
        {
            var info = byKey[e.Key];
            string name = $"«{info.Strategy}» {info.Symbol}";
            if ((info.IsReal || e.Info.IsReal) && !s.AutoResumeLiveReal)
            {
                waiting.Add(name);
                continue;
            }

            try
            {
                var error = await ResumeAsync(e);
                if (error != null)
                {
                    failed.Add($"{name}: {error}");
                    continue;
                }

                if (info.Paused && Running(e.Key) is { } r) r.SetPaused(true);
                resumed.Add(name);
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex);
                failed.Add($"{name}: {ex.Message}");
            }
        }

        var parts = new List<string>();
        if (resumed.Count > 0) parts.Add($"Reanudé {resumed.Count} estrategia(s) en vivo que corrían al cerrar Trading Hub: {string.Join(", ", resumed)}");
        if (waiting.Count > 0) parts.Add($"Esperan que las reanudes (cuenta real): {string.Join(", ", waiting)}");
        if (failed.Count > 0) parts.Add($"No pude reanudar {string.Join("; ", failed)}");
        if (parts.Count > 0) Notify(string.Join(" · ", parts));
        _svc.SaveLiveState();
    }

    /// <summary>Reconstruye cómo se lanzó una estrategia guardada (null si falta algo, con el motivo).</summary>
    public async Task<(LiveLaunch? Spec, string? Error)> LaunchFromInfoAsync(LiveRunnerInfo info)
    {
        var catalog = _svc.Strategies;
        if (catalog.Files.Count == 0) catalog.Refresh();
        var file = catalog.Files.FirstOrDefault(f => f.Id == info.StrategyId) ?? catalog.Files.FirstOrDefault(f => f.FileName == info.StrategyId.Split('.').Last());
        if (file == null) return (null, $"No encuentro la estrategia «{info.Strategy}» en Backtesting.");
        if (file.Type == null) await Task.Run(() => catalog.Compile(file));
        if (file.Type == null) return (null, $"«{info.Strategy}» tiene errores de compilación.");
        string profileId = info.ProfileId.Length > 0 ? info.ProfileId : _svc.Connections.Enabled.FirstOrDefault(p => p.MarketDataId == info.FeedId)?.Id ?? "";
        if (profileId.Length == 0) return (null, $"No hay una conexión activa para {info.FeedId}.");
        // las guardadas por una versión anterior no tienen la cuenta: la simulada se reconoce por el nombre
        string accountId = info.AccountId.Length > 0 ? info.AccountId : info.Account == "Cuenta simulada" ? "sim" : "";
        if (accountId.Length == 0) return (null, "No sé en qué cuenta operaba (es de una versión anterior): lanzala de nuevo desde «Nueva estrategia en vivo».");
        var instruments = await InstrumentPicker.InstrumentsAsync(_svc, profileId);
        var inst = instruments.FirstOrDefault(i => i.Symbol.Equals(info.Symbol, StringComparison.OrdinalIgnoreCase));
        if (inst == null) return (null, $"{info.Symbol} no está en la conexión.");
        if (!ChartPeriod.TryParse(info.Period, out var period)) period = ChartPeriod.M5;
        var parameters = info.Parameters.ToDictionary(kv => kv.Key, kv => kv.Value);
        string? timeZone = info.TimeZoneId;
        var session = TimeSpan.FromHours(info.SessionStartHours);
        if (parameters.Count == 0)
        {
            // guardada por una versión anterior (sin parámetros): los de su Backtest, que son con los que se había lanzado
            var saved = SavedBacktest(file, _svc.Settings.TimeZoneId);
            parameters = saved.Parameters;
            if (info.AccountId.Length == 0)
            {
                timeZone = saved.TimeZoneId;
                session = saved.SessionStart;
            }
        }

        return (new LiveLaunch
        {
            File = file,
            ProfileId = profileId,
            Instrument = inst,
            AccountId = accountId,
            Period = period,
            Parameters = parameters,
            TimeZoneId = timeZone,
            SessionStart = session,
            Size = info.Size,
            MaxDailyLoss = info.MaxDailyLoss,
            MaxDrawdown = info.MaxDrawdown,
            CloseOnLimit = info.CloseOnLimit,
            PortfolioId = info.PortfolioId,
            Paused = info.Paused,
        }, null);
    }

    private sealed class SavedUi
    {
        public BacktestSettings? Settings { get; set; }
        public Dictionary<string, System.Text.Json.JsonElement> Parameters { get; set; } = new();
    }

    /// <summary>
    /// Lo último que se configuró en el Backtest de una estrategia: parámetros (los que falten, los de la clase), velas y
    /// sesión. Es lo que usa «Nueva estrategia en vivo» y lo que se toma de una guardada por una versión anterior.
    /// </summary>
    public static (Dictionary<string, object?> Parameters, string? Period, string? TimeZoneId, TimeSpan SessionStart) SavedBacktest(StrategyFile file, string? defaultTimeZone)
    {
        var values = file.Type != null ? file.Create().GetParameters() : new Dictionary<string, object?>();
        string? period = null, timeZone = defaultTimeZone;
        var session = TimeSpan.Zero;
        try
        {
            var path = Path.Combine(AppPaths.Backtests, string.Concat(file.Id.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)), "ui.json");
            if (File.Exists(path))
            {
                var opts = new System.Text.Json.JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
                var saved = System.Text.Json.JsonSerializer.Deserialize<SavedUi>(File.ReadAllText(path), opts);
                if (saved != null)
                {
                    foreach (var (k, v) in saved.Parameters)
                        if (values.ContainsKey(k)) values[k] = v.Clone();
                    if (saved.Settings is { } st)
                    {
                        if (!string.IsNullOrWhiteSpace(st.Period)) period = st.Period;
                        timeZone = st.TimeZoneId;
                        session = st.SessionStart;
                    }
                }
            }
        }
        catch (Exception)
        {
            // sin estado guardado: los de la clase
        }

        return (values, period, timeZone, session);
    }

    /// <summary>Los parámetros con los que corre (o correría) una estrategia de En vivo.</summary>
    /// <summary>
    /// En qué condiciones de mercado abre operaciones (el filtro de contexto de la estrategia): se aplica ya, sin reiniciar,
    /// y queda guardado para cuando se reanude. Las bloqueadas no. null si salió bien.
    /// </summary>
    public string? ApplyContextFilter(LiveEntry e, RegimeFilter filter, int minRank, int maxRank)
    {
        if (e.Locked) return "Está bloqueada: su condición de mercado no se cambia desde acá.";
        if (e.Runner is { IsRunning: true } r) r.SetContextFilter(filter, minRank, maxRank);
        void Put(IDictionary<string, object?> p)
        {
            p["RegimeFilter"] = (int)filter;
            p["MinVolatilityRank"] = Math.Clamp(minRank, 0, 100);
            p["MaxVolatilityRank"] = Math.Clamp(Math.Max(maxRank, minRank), 0, 100);
        }

        // lo que se guarda para reanudarla (y, si es de un gráfico, su configuración en el gráfico)
        Put(e.Info.Parameters);
        if (e.ChartStrategy is { } cs) Put(cs.Parameters);
        if (e.Runner is not { IsRunning: true })
        {
            var infos = LiveStore.LoadRunners().Where(i => i.Key != e.Info.Key).Append(e.Info).ToList();
            LiveStore.SaveRunners(infos);
        }

        _svc.SaveLiveState();
        string what = Robustness.DescribeFilter(filter, minRank, maxRank);
        Notice?.Invoke($"«{e.Info.Strategy}» opera {(what.Length > 0 ? what : "en cualquier condición de mercado")}");
        return null;
    }

    public Dictionary<string, object?> ParametersOf(LiveEntry e)
    {
        if (e.Runner is { } r) return r.StrategyInstance.GetParameters();
        if (e.ChartStrategy is { } cs) return new Dictionary<string, object?>(cs.Parameters);
        if (e.Info.Parameters.Count > 0) return e.Info.Parameters.ToDictionary(kv => kv.Key, kv => kv.Value);
        var catalog = _svc.Strategies;
        if (catalog.Files.Count == 0) catalog.Refresh();
        var file = catalog.Files.FirstOrDefault(f => f.Id == e.Info.StrategyId);
        return file != null ? SavedBacktest(file, _svc.Settings.TimeZoneId).Parameters : new Dictionary<string, object?>();
    }

    /// <summary>Lanza una estrategia en vivo (la confirmación de cuenta real la pide la ventana antes).</summary>
    public async Task<(LiveStrategyRunner? Runner, string Message)> LaunchAsync(LiveLaunch spec)
    {
        if (spec.File.Type == null) await Task.Run(() => _svc.Strategies.Compile(spec.File));
        if (spec.File.Type == null) return (null, $"«{spec.File.FileName}» tiene errores: arreglala en Backtesting.");
        var data = _svc.Open(spec.ProfileId, spec.Instrument);
        for (int i = 0; i < 240 && data.IsLoadingHistory; i++) await Task.Delay(500);
        var strategy = spec.File.Create();
        strategy.SetParameters(spec.Parameters);
        var (execInst, ratio) = await ExecutionContractAsync(spec, strategy, data);
        var execData = ReferenceEquals(execInst, spec.Instrument) ? data : _svc.Open(spec.ProfileId, execInst);
        var account = _svc.AccountFor(execData, spec.AccountId);
        var runner = new LiveStrategyRunner(strategy, data, account, spec.Period, spec.TimeZoneId, spec.SessionStart)
        {
            ExecInstrument = execInst,
            ExecRatio = ratio,
            Source = "panel",
            AccountId = spec.AccountId,
            ProfileId = spec.ProfileId,
            SizeMultiplier = spec.Size,
            MaxDailyLoss = spec.MaxDailyLoss,
            MaxDrawdown = spec.MaxDrawdown,
            CloseOnLimit = spec.CloseOnLimit,
            PortfolioId = spec.PortfolioId,
            ProtectOnStart = true,
        };
        if (Running(runner.Key) != null) return (null, $"«{runner.StrategyName}» ya está corriendo en {spec.Instrument.Symbol} con {account.Name}.");
        try
        {
            runner.Start();
        }
        catch (Exception ex)
        {
            return (null, "No se pudo iniciar: " + ex.Message);
        }

        if (spec.Paused) runner.SetPaused(true);
        _svc.Runners.RemoveAll(r => r.Key == runner.Key && !r.IsRunning && !r.IsClosing);
        _svc.Runners.Add(runner);
        Watch(runner);
        _svc.SaveLiveState();
        if (MarketInfo.IsFutures(spec.Instrument.FeedId))
        {
            decimal units = runner.UnitsFor(QuantityOf(spec.Parameters));
            Notify(runner.CrossExecution
                ? $"«{runner.StrategyName}» opera en micros: cada orden va con {units:0.####} {execInst.Symbol} (equivale a {units / ratio:0.####} {spec.Instrument.Symbol})"
                : $"«{runner.StrategyName}» opera {units:0.####} {execInst.Symbol} por orden");
        }

        string where = runner.CrossExecution ? $"{spec.Instrument.Symbol}, ejecuta en {execInst.Symbol}" : spec.Instrument.Symbol;
        return (runner, $"{runner.StrategyName} corriendo en vivo en {account.Name} ({where})");
    }

    // ===================================================================== contrato inteligente

    /// <summary>
    /// Cómo sale la próxima orden de una estrategia: cuántos contratos y de cuál (la cantidad de la estrategia × el tamaño,
    /// en contratos enteros, en el micro si el contrato inteligente lo eligió). Es lo que se muestra en todos lados para
    /// que nunca haya dudas de si opera 5 NQ o 5 MNQ.
    /// </summary>
    internal OrderPlan PlanOf(LiveEntry e)
    {
        if (e.Runner is { } r)
        {
            decimal q = QuantityOf(r.StrategyInstance.GetParameters());
            return new OrderPlan(r.UnitsFor(q), r.ExecInstrument.Symbol, r.Instrument.Symbol, r.CrossExecution, r.SizeMultiplier > 0 && r.SizeBelowMinimum(q),
                r.CrossExecution && r.ExecRatio > 0 ? r.UnitsFor(q) / r.ExecRatio : 0);
        }

        // detenida: lo que mandaría al volver a correr (con una posición abierta, sigue en el contrato en que está)
        var info = e.Info;
        decimal qty = QuantityOf(info.Parameters), size = info.Size;
        var (symbol, ratio) = PreferredContract(info.FeedId, info.Symbol, qty, size);
        if (e.Ledger.Position != 0 && e.Ledger.OpenSymbol is { } open && !open.Equals(symbol, StringComparison.OrdinalIgnoreCase))
        {
            symbol = open;
            ratio = open.Equals(info.Symbol, StringComparison.OrdinalIgnoreCase) ? 1 : RatioOf(info.Symbol, open);
        }

        decimal step = MarketInfo.VolumeStep(info.FeedId, symbol) is var st && st > 0 ? st : 1;
        decimal exact = Math.Abs(qty) * size * ratio;
        decimal units = size <= 0 ? 0 : Math.Max(1, Math.Floor(exact / step + 0.0000001m)) * step;
        bool micro = !symbol.Equals(info.Symbol, StringComparison.OrdinalIgnoreCase);
        return new OrderPlan(units, symbol, info.Symbol, micro, size > 0 && exact < step, micro && ratio > 0 ? units / ratio : 0);
    }

    /// <summary>Cantidad por orden que pide la estrategia (su parámetro Quantity, o 1).</summary>
    internal static decimal QuantityOf(IReadOnlyDictionary<string, object?> parameters) =>
        parameters.TryGetValue("Quantity", out var q) && Robustness.ToDouble(q) is { } d && d > 0 ? (decimal)d : 1;

    /// <summary>
    /// Contrato inteligente: en un futuro con micro, si el tamaño pide fracciones del mini (1 NQ × 0,5) se ejecuta en el
    /// micro con la misma exposición (5 MNQ); con minis enteros, en el mini. Devuelve el contrato y cuántos de ese contrato
    /// vale uno del que mira la estrategia.
    /// </summary>
    internal (string Symbol, decimal Ratio) PreferredContract(string feed, string symbol, decimal strategyQuantity, decimal size)
    {
        if (!_svc.Settings.SmartContracts || !MarketInfo.IsFutures(feed) || MarketInfo.MicroOf(symbol) is not { } micro) return (symbol, 1);
        decimal exposure = Math.Abs(strategyQuantity) * size;
        if (size <= 0 || (exposure >= 1 && exposure == Math.Floor(exposure))) return (symbol, 1);
        return (micro, RatioOf(symbol, micro));
    }

    private static decimal RatioOf(string symbol, string exec)
    {
        decimal a = FuturesCatalog.Find(symbol)?.PointValue ?? 0, b = FuturesCatalog.Find(exec)?.PointValue ?? 0;
        return a > 0 && b > 0 ? a / b : 1;
    }

    /// <summary>
    /// En qué contrato ejecuta al lanzarse: el preferido para su tamaño, salvo que tenga una posición abierta (la sigue en
    /// el contrato en que está, y cambia recién cuando queda plana).
    /// </summary>
    private Task<(Instrument Instrument, decimal Ratio)> ExecutionContractAsync(LiveLaunch spec, Strategy strategy, InstrumentData data) =>
        ExecutionContractForAsync(spec.ProfileId, spec.Instrument, spec.Parameters, spec.Size, strategy, _svc.AccountFor(data, spec.AccountId).Name);

    /// <summary>
    /// En qué contrato ejecuta una estrategia (del panel o de un gráfico) con ese tamaño: el preferido (el micro si el tamaño
    /// pide fracciones del mini), salvo que tenga una posición abierta, que sigue en el contrato en que está.
    /// </summary>
    internal async Task<(Instrument Instrument, decimal Ratio)> ExecutionContractForAsync(string profileId, Instrument signal, IReadOnlyDictionary<string, object?> parameters,
        decimal size, Strategy strategy, string accountName)
    {
        var (symbol, ratio) = PreferredContract(signal.FeedId, signal.Symbol, QuantityOf(parameters), size);
        string key = $"{strategy.GetType().FullName}|{signal.FeedId}:{signal.Symbol}|{accountName}";
        var ledger = LiveStore.LoadLedger(key, signal.PointValue);
        if (ledger.Position != 0)
        {
            symbol = ledger.OpenSymbol ?? signal.Symbol;
            ratio = symbol.Equals(signal.Symbol, StringComparison.OrdinalIgnoreCase) ? 1 : RatioOf(signal.Symbol, symbol);
        }

        if (symbol.Equals(signal.Symbol, StringComparison.OrdinalIgnoreCase)) return (signal, 1);
        try
        {
            var list = await InstrumentPicker.InstrumentsAsync(_svc, profileId);
            if (list.FirstOrDefault(i => i.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase)) is { } found) return (found, ratio);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }

        return (signal, 1); // la conexión no lo ofrece: en el mismo contrato
    }

    private readonly Dictionary<string, DateTime> _contractSwitchTried = new();

    /// <summary>
    /// Cambió el tamaño y ahora conviene otro contrato (por ejemplo de ×0,5 a ×1: del micro al mini): si la estrategia está
    /// plana y sin órdenes, se reinicia en el contrato nuevo (con la misma contabilidad).
    /// </summary>
    private void CheckContracts()
    {
        if (!_svc.Settings.SmartContracts) return;
        foreach (var r in _svc.Runners.Where(r => r is { IsRunning: true, Persist: true } && r.Source is "panel" or "chart").ToList())
        {
            if (r.Ledger.Position != 0 || r.HasWorkingOrders || r.IsClosing) continue;
            var (symbol, _) = PreferredContract(r.Instrument.FeedId, r.Instrument.Symbol, QuantityOf(r.StrategyInstance.GetParameters()), r.SizeMultiplier);
            if (symbol.Equals(r.ExecInstrument.Symbol, StringComparison.OrdinalIgnoreCase)) continue;
            if (_contractSwitchTried.TryGetValue(r.Key, out var last) && (DateTime.UtcNow - last).TotalMinutes < 5) continue;
            _contractSwitchTried[r.Key] = DateTime.UtcNow;
            if (Find(r.Key) is not { } e) continue;
            Notify($"«{r.StrategyName}» con tamaño ×{r.SizeMultiplier:0.##}: ahora ejecuta en {symbol} (estaba plana, se reinició)");
            _ = RestartWithParametersAsync(e, r.StrategyInstance.GetParameters());
        }
    }

    /// <summary>Reinicia con otros parámetros (sigue la misma contabilidad y, si tenía posición, la sigue gestionando).</summary>
    public async Task<string?> RestartWithParametersAsync(LiveEntry e, Dictionary<string, object?> parameters)
    {
        if (e.Locked) return "Está bloqueada: sus parámetros no se cambian desde acá.";
        if (e.ChartStrategy is { } cs && e.Chart is { } chart && Trading?.Invoke() is { } tv)
        {
            cs.Parameters = new Dictionary<string, object?>(parameters);
            cs.Paused = false;
            await tv.RunStrategyAsync(chart, cs);
            return cs.Overlay.Error;
        }

        var launch = await LaunchFromInfoAsync(e.Info);
        if (launch.Error != null) return launch.Error;
        var old = e.Runner;
        if (old is { IsRunning: true })
        {
            old.StopReason = "Reiniciada con parámetros nuevos";
            old.Stop(flatten: false);
        }

        var spec = launch.Spec!;
        var (runner, message) = await LaunchAsync(new LiveLaunch
        {
            File = spec.File,
            ProfileId = spec.ProfileId,
            Instrument = spec.Instrument,
            AccountId = spec.AccountId,
            Period = spec.Period,
            Parameters = parameters,
            TimeZoneId = spec.TimeZoneId,
            SessionStart = spec.SessionStart,
            Size = old?.SizeMultiplier ?? spec.Size,
            MaxDailyLoss = old?.MaxDailyLoss ?? spec.MaxDailyLoss,
            MaxDrawdown = old?.MaxDrawdown ?? spec.MaxDrawdown,
            CloseOnLimit = old?.CloseOnLimit ?? spec.CloseOnLimit,
            PortfolioId = old?.PortfolioId ?? spec.PortfolioId,
            Paused = old?.Paused ?? spec.Paused,
        });
        return runner == null ? message : null;
    }

    /// <summary>Borra el historial de operaciones de una estrategia y empieza de cero (solo sin posición abierta).</summary>
    public string? ResetHistory(LiveEntry e)
    {
        if (e.Locked) return "Está bloqueada: su historial no se borra desde acá.";
        if (e.Runner is { } r && (r.IsRunning || r.IsClosing))
        {
            if (!r.ResetLedger()) return "Tiene una posición abierta: cerrala primero.";
        }
        else LiveStore.DeleteFills(e.Key); // detenida: se borra aunque su contabilidad diga que tenía posición (la cuenta manda)

        _ledgers.Remove(e.Key);
        LiveStore.UpdateRunner(e.Key, i =>
        {
            i.Position = 0;
            i.OpenPnL = 0;
            i.Realized = 0;
            i.Trades = 0;
        });
        _svc.SaveLiveState();
        return null;
    }

    /// <summary>Borra una estrategia de En vivo (la detiene si corría; con <paramref name="history"/>, también su historial).</summary>
    public async Task<string?> DeleteAsync(LiveEntry e, bool history)
    {
        if (e.Locked) return "Está bloqueada: no se borra desde acá.";
        if (e.Runner is { IsRunning: true } r)
        {
            r.StopReason = "Borrada";
            r.Stop(flatten: false);
        }

        if (e.ChartStrategy is { } cs && e.Chart is { } chart && Trading?.Invoke() is { } tv) tv.RemoveStrategy(chart, cs);
        _svc.Runners.RemoveAll(x => x.Key == e.Key && !x.IsClosing);
        LiveStore.RemoveRunner(e.Key, history);
        _ledgers.Remove(e.Key);
        // también de los portafolios
        var defs = LiveStore.LoadPortfolios();
        foreach (var d in defs) d.Members.Remove(e.Key);
        LiveStore.SavePortfolios(defs);
        await Task.CompletedTask;
        return null;
    }

    // ===================================================================== kill switch

    /// <summary>
    /// Frenar todo: todas las estrategias en vivo (panel y gráficos), cancelando sus órdenes y cerrando sus posiciones.
    /// </summary>
    public async Task<int> KillAllAsync(string reason)
    {
        int stopped = 0;

        foreach (var e in Entries(includeStopped: false).Where(x => x.Running && !x.Locked))
        {
            await StopAsync(e, close: true);
            stopped++;
        }

        Notify($"⛔ Frenaste todo ({stopped} estrategia(s)): {reason}");
        _svc.SaveLiveState();
        return stopped;
    }

    // ===================================================================== operaciones

    /// <summary>Identificador estable de una operación cerrada (para el caché de excursiones).</summary>
    public static string TradeId(string key, ClosedTrade t) => $"{key}#{t.Number}#{t.EntryTime.Ticks}";

    /// <summary>
    /// MAE y MFE de operaciones en vivo: con los precios en memoria si el instrumento está abierto, o con los guardados
    /// en disco. Sin datos de ese momento queda sin calcular (Exact = false).
    /// </summary>
    public async Task<Dictionary<string, (decimal Mae, decimal Mfe, bool Exact)>> ExcursionsAsync(IReadOnlyList<(LiveEntry Entry, ClosedTrade Trade)> items)
    {
        var result = new Dictionary<string, (decimal, decimal, bool)>();
        var pending = new List<(string Id, string Feed, string Symbol, decimal PointValue, ClosedTrade Trade)>();
        foreach (var (e, t) in items)
        {
            string id = TradeId(e.Key, t);
            if (_excursions.TryGetValue(id, out var cached))
            {
                result[id] = cached;
                continue;
            }

            // en memoria si el instrumento está abierto (las horas de la contabilidad son de la Mac y las del mercado
            // pueden venir unos segundos atrasadas: si la última operación del mercado no llega a la salida, no se guarda)
            var data = _svc.Hub.Items.FirstOrDefault(d => d.Instrument.FeedId == e.Info.FeedId && d.Instrument.Symbol == e.Info.Symbol);
            if (data is { Trades.Count: > 0 } && data.Trades.FirstTime <= t.EntryTime.AddSeconds(5) && data.Trades.LastTime >= t.ExitTime.AddSeconds(-60))
            {
                var store = data.Trades;
                int i0 = store.LowerBound(t.EntryTime.AddSeconds(-2)), i1 = store.LowerBound(t.ExitTime.AddSeconds(2));
                var ex = TradeExcursion.Compute(t, Prices(store, i0, i1), e.Info.PointValue);
                result[id] = (ex.Mae, ex.Mfe, true);
                if (store.LastTime >= t.ExitTime.AddSeconds(2)) _excursions[id] = (ex.Mae, ex.Mfe, true);
            }
            else pending.Add((id, e.Info.FeedId, e.Info.Symbol, e.Info.PointValue, t));
        }

        if (pending.Count == 0) return result;
        var root = AppPaths.Ticks;
        var computed = await Task.Run(() =>
        {
            var res = new List<(string Id, decimal Mae, decimal Mfe, bool Exact)>();
            foreach (var p in pending)
            {
                try
                {
                    var prices = HistoricalData.Read(root, p.Feed, p.Symbol, p.Trade.EntryTime, p.Trade.ExitTime.AddSeconds(1)).Select(x => x.Price).ToList();
                    if (prices.Count == 0)
                    {
                        res.Add((p.Id, 0, 0, false));
                        continue;
                    }

                    var ex = TradeExcursion.Compute(p.Trade, prices, p.PointValue);
                    res.Add((p.Id, ex.Mae, ex.Mfe, true));
                }
                catch
                {
                    res.Add((p.Id, 0, 0, false));
                }
            }

            return res;
        });
        foreach (var c in computed)
        {
            result[c.Id] = (c.Mae, c.Mfe, c.Exact);
            if (c.Exact) _excursions[c.Id] = (c.Mae, c.Mfe, true);
        }

        return result;
    }

    private static IEnumerable<decimal> Prices(TradeStore store, int from, int to)
    {
        for (int i = from; i < to && i < store.Count; i++) yield return store.GetPrice(i);
    }

    /// <summary>Muestra una operación en un gráfico (abre uno del instrumento si no hay).</summary>
    public async Task<string?> ShowTradeAsync(LiveEntry e, ClosedTrade? t)
    {
        if (Trading?.Invoke() is not { } tv) return "Abrí la sección Trading primero.";
        string profileId = e.Info.ProfileId.Length > 0 ? e.Info.ProfileId : _svc.Connections.Enabled.FirstOrDefault(p => p.MarketDataId == e.Info.FeedId)?.Id ?? e.Info.FeedId;
        if (!ChartPeriod.TryParse(e.Info.Period, out var period)) period = ChartPeriod.M5;
        return await tv.ShowTradeAsync(profileId, e.Info.Symbol, period, e.Chart, t, $"{e.Info.Strategy}");
    }

    // ===================================================================== reloj

    /// <summary>Desde el reloj de la app (unas veces por segundo): agente de riesgo de cada portafolio.</summary>
    public void Tick()
    {
        var now = DateTime.UtcNow;
        // Claude (el laboratorio de Backtesting) dejó propuestas de tamaño: se avisa una vez
        var stamp = LiveStore.ProposalsStamp;
        if (stamp != _proposalsStamp)
        {
            bool first = _proposalsStamp == default;
            _proposalsStamp = stamp;
            if (!first && LiveStore.LoadProposals().Any(p => p.Source == "claude" && p.IsPending && p.CreatedUtc.ToUniversalTime() > now.AddMinutes(-10)))
                Notify("Claude propuso cambios de tamaño: revisalos en En vivo → Propuestas");
        }

        if ((now - _lastTick).TotalSeconds < 20) return;
        _lastTick = now;
        try
        {
            CheckContracts();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }

        try
        {
            RiskAgent.Tick();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
    }
}
