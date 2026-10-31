using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using TradingHub.Common.Logging;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Binance;
using TradingHub.Indicators;
using TradingHub.Market;
using TradingHub.Trading;
using TradingHub.Trading.Statistics;

namespace TradingHub.App.Services;

/// <summary>Carpetas de la plataforma.</summary>
public static class AppPaths
{
    /// <summary>Datos de la app. TRADINGHUB_DATA_DIR la cambia (las herramientas de prueba usan una carpeta aparte).</summary>
    public static string Root { get; } = Environment.GetEnvironmentVariable("TRADINGHUB_DATA_DIR") is { Length: > 0 } dir
        ? dir
        : TradingHub.Common.Product.AppData;
    public static string Ticks => Path.Combine(Root, "Ticks");
    public static string WorkspaceFile => Path.Combine(Root, "workspace.json");
    public static string Drawings => Path.Combine(Root, "Drawings");
    public static string Backtests => Path.Combine(Root, "Backtests");
    public static string Accounts => Path.Combine(Root, "Accounts");

    /// <summary>
    /// Carpeta visible para los indicadores propios (.cs) del usuario. TRADINGHUB_INDICATORS_DIR la cambia (pruebas); con
    /// TRADINGHUB_DATA_DIR y sin ella, una dentro de esa carpeta: una prueba nunca escribe en la carpeta real.
    /// </summary>
    public static string UserIndicators { get; } = TestFolder("TRADINGHUB_INDICATORS_DIR", "Indicadores") ?? Path.Combine(TradingHub.Common.Product.Documents, "Indicadores");

    /// <summary>Carpeta visible para las estrategias (.cs). TRADINGHUB_STRATEGIES_DIR la cambia (pruebas), igual que los indicadores.</summary>
    public static string Strategies { get; } = TestFolder("TRADINGHUB_STRATEGIES_DIR", "Estrategias") ?? Path.Combine(TradingHub.Common.Product.Documents, "Estrategias");

    private static string? TestFolder(string variable, string name) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } dir ? dir
        : Environment.GetEnvironmentVariable("TRADINGHUB_DATA_DIR") is { Length: > 0 } data ? Path.Combine(data, name)
        : null;


    /// <summary>Reportes del laboratorio (Markdown), dentro de la carpeta de estrategias.</summary>
    public static string Reports => Path.Combine(Strategies, "Reportes");
}

/// <summary>Un indicador disponible para agregar.</summary>
public sealed record IndicatorInfo(Type Type, string Name, string Category, string Source, string Description);

/// <summary>Ventana aparte de una sección del panel derecho (Chart Trader, DOM o cinta).</summary>
public sealed class PanelWindowState
{
    public bool Floating { get; set; }
    public int X { get; set; } = -1;
    public int Y { get; set; } = -1;
    public double Width { get; set; } = 340;
    public double Height { get; set; } = 720;
}

/// <summary>Catálogo de indicadores: los propios de Trading Hub, DLLs extra y scripts del usuario.</summary>
public sealed class IndicatorCatalog
{
    private readonly List<IndicatorInfo> _items = new();
    private readonly HashSet<Assembly> _scanned = new();

    public IReadOnlyList<IndicatorInfo> Items => _items;
    public event Action? Changed;

    public void Scan(Assembly asm, string source)
    {
        if (!_scanned.Add(asm)) return;
        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }

        foreach (var t in types)
        {
            if (t.IsAbstract || !typeof(Indicator).IsAssignableFrom(t) || t.GetConstructor(Type.EmptyTypes) == null) continue;
            if (t.GetCustomAttribute<BrowsableAttribute>() is { Browsable: false }) continue;
            string name = Indicator.DefaultName(t);
            string cat = t.GetCustomAttribute<CategoryAttribute>()?.Category ?? IndicatorCategories.Other;
            string desc = "";
            try { desc = t.GetCustomAttribute<System.ComponentModel.DataAnnotations.DisplayAttribute>()?.GetDescription() ?? t.GetCustomAttribute<DescriptionAttribute>()?.Description ?? ""; }
            catch { /* recursos de localización ausentes */ }
            _items.RemoveAll(i => i.Type.FullName == t.FullName && i.Source == source);
            _items.Add(new IndicatorInfo(t, name, cat, source, desc));
        }

        _items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        Changed?.Invoke();
    }

    public void RemoveSource(string source)
    {
        _items.RemoveAll(i => i.Source == source);
        _scanned.RemoveWhere(a => a.IsCollectible);
        Changed?.Invoke();
    }

    public void ScanFolder(string folder, string source)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var dll in Directory.EnumerateFiles(folder, "*.dll"))
        {
            try { Scan(Assembly.LoadFrom(dll), source); }
            catch (Exception ex) { this.LogWarn($"No se pudo cargar {Path.GetFileName(dll)}: {ex.Message}"); }
        }
    }

    public IndicatorInfo? Find(string typeName) => _items.FirstOrDefault(i => i.Type.FullName == typeName) ?? _items.FirstOrDefault(i => i.Type.Name == typeName);

    public Indicator? Create(string typeName)
    {
        var info = Find(typeName);
        return info == null ? null : (Indicator?)Activator.CreateInstance(info.Type);
    }
}

/// <summary>Preferencias generales.</summary>
public sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(AppPaths.Root, "settings.json");

    public string? TimeZoneId { get; set; }
    public bool ConfirmRealOrders { get; set; } = true;

    /// <summary>Estrategias favoritas (por nombre de archivo): van arriba de todo en la lista de Backtesting.</summary>
    public List<string> FavoriteStrategies { get; set; } = new();

    /// <summary>Apariencia de la plataforma: "oscuro" (negro) o "claro" (blanco).</summary>
    public string ThemeMode { get; set; } = "oscuro";

    /// <summary>Plantilla de gráfico con la que arrancan las pestañas nuevas (null = copian el gráfico activo).</summary>
    public string? DefaultChartTemplate { get; set; }

    /// <summary>Plantilla con la que arranca cada indicador nuevo (tipo del indicador → nombre de la plantilla).</summary>
    public Dictionary<string, string> DefaultIndicatorTemplates { get; set; } = new();

    /// <summary>Saldo con el que arranca la cuenta simulada (se puede cambiar en la sección Cuenta).</summary>
    public decimal SimStartBalance { get; set; } = 10_000m;
    public bool SoundAlerts { get; set; } = true;

    public bool RecordLiveData { get; set; } = true;

    /// <summary>Modo sin dormir: la Mac no se duerme mientras haya estrategias en vivo o agentes trabajando.</summary>
    public bool KeepAwake { get; set; } = true;

    /// <summary>Seguir con la tapa cerrada (pide tu contraseña de administrador) y, si se quiere, solo con el cargador.</summary>
    public bool KeepAwakeLidClosed { get; set; }
    public bool LidClosedOnlyOnPower { get; set; } = true;
    /// <summary>Idioma de la interfaz (es, en, zh-CN). null = todavía no se eligió: se toma el del sistema al arrancar.</summary>
    public string? Language { get; set; }

    /// <summary>Si ya había ajustes guardados (alguien que ya usaba la app antes de que hubiera idiomas).</summary>
    public static bool Saved => File.Exists(FilePath);

    public double WindowWidth { get; set; } = 1600;
    public double WindowHeight { get; set; } = 960;
    public List<string> Favorites { get; set; } = new() { "binance-futures:BTCUSDT", "binance-futures:ETHUSDT", "sim:SIM-BTC" };

    /// <summary>Períodos favoritos: los que se ven en la barra del gráfico (el resto, en su selector).</summary>
    public List<string> FavoritePeriods { get; set; } = new() { "M1", "M5", "M15", "H1", "H4", "Daily" };

    // panel derecho de Trading
    public bool ShowSidePanel { get; set; } = true;
    public bool ShowTicket { get; set; } = true;
    public bool ShowDom { get; set; } = true;
    public bool ShowTape { get; set; } = true;
    public double SidePanelWidth { get; set; } = 340;

    /// <summary>Ya se mostró el Chart Trader por gráfico (la primera vez se oculta el ticket general).</summary>
    public bool ChartTraderIntroduced { get; set; }

    /// <summary>Alto relativo de cada sección del panel derecho (ticket, dom, tape) cuando están juntas.</summary>
    public Dictionary<string, double> SideWeights { get; set; } = new();

    /// <summary>Chart Trader, DOM o cinta en una ventana aparte: si estaba afuera, dónde y de qué tamaño.</summary>
    public Dictionary<string, PanelWindowState> PanelWindows { get; set; } = new();

    /// <summary>Cerrar la ventana deja a Trading Hub corriendo en la barra de menú (con su panel); ⌘Q la cierra del todo.</summary>
    public bool KeepInMenuBar { get; set; } = true;

    /// <summary>
    /// Contrato inteligente: si el tamaño de una estrategia pide fracciones de un mini (1 NQ × 0,5), ejecuta en el micro con la
    /// misma exposición (5 MNQ). Con minis enteros, en el mini.
    /// </summary>
    public bool SmartContracts { get; set; } = true;

    /// <summary>Al abrir Trading Hub, las estrategias en vivo que corrían cuando se cerró vuelven a arrancar solas.</summary>
    public bool AutoResumeLive { get; set; } = true;

    /// <summary>…también las de cuentas reales (si no, esas esperan que las reanudes vos).</summary>
    public bool AutoResumeLiveReal { get; set; }

    /// <summary>Botones de ejecución debajo del DOM (mercado, límites, cancelar, cerrar…).</summary>
    public bool ShowDomButtons { get; set; } = true;

    /// <summary>DOM en una ventana aparte (versión anterior: ahora va en <see cref="PanelWindows"/>).</summary>
    public bool DomFloating { get; set; }
    public int DomWindowX { get; set; } = -1;
    public int DomWindowY { get; set; } = -1;
    public double DomWindowWidth { get; set; } = 360;
    public double DomWindowHeight { get; set; } = 720;

    /// <summary>Chat de Claude en una ventana aparte: posición y tamaño.</summary>
    public int ClaudeWindowX { get; set; } = -1;
    public int ClaudeWindowY { get; set; } = -1;
    public double ClaudeWindowWidth { get; set; } = 460;
    public double ClaudeWindowHeight { get; set; } = 760;

    /// <summary>Gráficos vinculados por color: qué se sincroniza dentro de cada grupo.</summary>
    public bool LinkSymbol { get; set; } = true;
    public bool LinkPeriod { get; set; } = true;
    public bool LinkCrosshair { get; set; } = true;

    /// <summary>Estilo por defecto de cada tipo de dibujo (lo guarda "usar como predeterminado").</summary>
    public Dictionary<string, TradingHub.Charting.Drawings.ChartDrawing> DrawingDefaults { get; set; } = new();

    /// <summary>Atajos de teclado: acción → atajo ("" = sin asignar; si falta, el de fábrica).</summary>
    public Dictionary<string, string> Hotkeys { get; set; } = new();

    /// <summary>Configuración del DOM (columnas, tamaños, colores).</summary>
    public TradingHub.Charting.Dom.DomSettings Dom { get; set; } = new();

    /// <summary>Stop y objetivos automáticos por instrumento (clave del instrumento).</summary>
    public Dictionary<string, BracketTemplate> Brackets { get; set; } = new();

    public BracketTemplate BracketTemplateFor(string instrumentKey)
    {
        if (!Brackets.TryGetValue(instrumentKey, out var t)) Brackets[instrumentKey] = t = new BracketTemplate();
        return t;
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new TradingHub.Charting.Dom.ColorJsonConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
                s.Dom ??= new TradingHub.Charting.Dom.DomSettings();
                s.Dom.Normalize();
                s.Brackets ??= new();
                s.Hotkeys ??= new();
                s.DrawingDefaults ??= new();
                return s;
            }
        }
        catch
        {
            // archivo dañado: valores por defecto
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
    }
}

/// <summary>Cuenta disponible para operar un instrumento.</summary>
public sealed record AccountOption(string Id, string Name, bool IsReal);

/// <summary>Servicios compartidos de la aplicación.</summary>
public sealed class AppServices
{
    private readonly Dictionary<string, SimBroker> _simBrokers = new();
    private readonly Dictionary<string, ITradingAccount> _realBrokers = new();
    private readonly Dictionary<string, TradingHub.Feeds.Rithmic.RithmicTradingSession> _rithmicSessions = new();
    private readonly Dictionary<string, TradingHub.Feeds.Tradovate.TradovateTradingSession> _tradovateSessions = new();

    /// <summary>Cuentas de Tradovate de una conexión (sincroniza órdenes, posiciones y saldos en segundo plano).</summary>
    public TradingHub.Feeds.Tradovate.TradovateTradingSession TradovateSessionFor(ConnectionProfile p)
    {
        if (_tradovateSessions.TryGetValue(p.Id, out var s)) return s;
        var feed = (TradingHub.Feeds.Tradovate.TradovateFeed)Connections.GetFeed(p.Id);
        s = new TradingHub.Feeds.Tradovate.TradovateTradingSession(feed.Connection);
        _tradovateSessions[p.Id] = s;
        // cada ejecución queda en el historial de la cuenta (saldo, operaciones y estadísticas)
        s.FillReceived += f => Journal.Add(TradovateAccountId(p, f.AccountId),
            new AccountFill("tv-" + f.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), f.Time, f.Symbol, f.Buy, f.Quantity, f.Price,
                AccountCenter.PointValueOf(f.Symbol), 0, f.OrderId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        s.Start();
        return s;
    }

    /// <summary>Id de una cuenta de Tradovate en Trading Hub: tradovate|conexión|cuenta.</summary>
    public static string TradovateAccountId(ConnectionProfile p, long account) => $"tradovate|{p.Id}|{account}";

    /// <summary>Futuro de CME (sirve para operarlo con una cuenta de Tradovate aunque el gráfico sea de otra conexión).</summary>
    private static bool IsCmeFuture(Instrument inst) =>
        (inst.FeedId is TradingHub.Feeds.Rithmic.RithmicFeed.FeedId or TradingHub.Feeds.Tradovate.TradovateFeed.FeedId) && TradingHub.Feeds.Rithmic.RithmicFeed.SpecFor(inst.Symbol.Split(' ', ':')[0]) != null;

    /// <summary>Sesión de órdenes de Rithmic de una conexión (se conecta en segundo plano la primera vez).</summary>
    public TradingHub.Feeds.Rithmic.RithmicTradingSession RithmicSessionFor(ConnectionProfile p)
    {
        if (_rithmicSessions.TryGetValue(p.Id, out var s)) return s;
        s = new TradingHub.Feeds.Rithmic.RithmicTradingSession(Connections.RithmicSettingsOf(p));
        _rithmicSessions[p.Id] = s;
        // cada ejecución queda en el historial de la cuenta (saldo, operaciones y estadísticas)
        s.FillReceived += f => Journal.Add(RithmicAccountId(p, f.Account),
            new AccountFill(f.Id, f.Time, f.Symbol, f.IsBuy, f.Quantity, f.Price, AccountCenter.PointValueOf(f.Symbol), 0, f.BasketId));
        s.Start();
        return s;
    }

    /// <summary>Estado de la sesión de órdenes de Rithmic para un instrumento (null si no es de Rithmic).</summary>
    public string? RithmicStatusFor(InstrumentData data)
    {
        var p = Connections.Enabled.FirstOrDefault(x => x.IsRithmic && x.MarketDataId == data.Instrument.FeedId && !string.IsNullOrWhiteSpace(x.RithmicUser));
        if (p == null) return null;
        var s = RithmicSessionFor(p);
        return s.IsConnected ? null : "Rithmic: " + s.StatusText;
    }

    /// <summary>Id de una cuenta de Rithmic en Trading Hub: rithmic|conexión|cuenta.</summary>
    public static string RithmicAccountId(ConnectionProfile p, string account) => $"rithmic|{p.Id}|{account}";

    public AppServices()
    {
        Portfolio.BeginBalance = Portfolio.Balance = Settings.SimStartBalance;
        Markets = new MarketConditions(this);
        // cada estrategia que arranca en vivo arma su contexto de mercado con el último mes (no solo con el calentamiento)
        TradingHub.Strategies.LiveStrategyRunner.Started += OnRunnerStarted;
        // la sesión de órdenes de Rithmic sigue la configuración nueva sin abrir otra (una segunda cortaría la primera)
        Connections.Reconfigured += p =>
        {
            if (p.IsRithmic && _rithmicSessions.TryGetValue(p.Id, out var session)) session.Reconfigure(Connections.RithmicSettingsOf(p));
            // Tradovate: la sesión de órdenes usa la conexión del feed, que se rehace con los datos nuevos
            if (p.Kind == ConnectionKind.Tradovate)
            {
                if (_tradovateSessions.Remove(p.Id, out var tv)) tv.Dispose();
                foreach (var key in _realBrokers.Keys.Where(k => k.StartsWith($"tradovate|{p.Id}|", StringComparison.Ordinal)).ToList())
                {
                    (_realBrokers[key] as IDisposable)?.Dispose();
                    _realBrokers.Remove(key);
                }
            }
        };
        Directory.CreateDirectory(AppPaths.Root);
        Directory.CreateDirectory(AppPaths.UserIndicators);
        Directory.CreateDirectory(AppPaths.Strategies);
        Hub.RecordingRoot = AppPaths.Ticks;
        KlineHistory.CacheRoot = Path.Combine(AppPaths.Root, "Klines");
        // TRADINGHUB_LIVE_DIR: cartera alternativa (pruebas)
        TradingHub.Strategies.LiveStore.Root = Environment.GetEnvironmentVariable("TRADINGHUB_LIVE_DIR") is { Length: > 0 } live ? live : Path.Combine(AppPaths.Root, "Live");
        // las que corrían al cerrar Trading Hub (enseguida el guardado periódico las marca detenidas): para reanudarlas al abrir
        RunningAtLastClose = TradingHub.Strategies.LiveStore.LoadRunners().Where(i => i.Running && !LiveEntry.IsManaged(i.Source)).ToList();
        Hub.RecordLive = Settings.RecordLiveData;
        StatsProvider.SetSource(Statistics);
        Catalog.Scan(typeof(TradingHub.Indicators.Native.BigTrades).Assembly, "Trading Hub");
        // los indicadores técnicos, si vienen en esta versión (la de código abierto no los trae)
        try
        {
            Catalog.Scan(System.Reflection.Assembly.Load("TradingHub.Indicators.Technical"), "Técnicos");
        }
        catch (Exception ex)
        {
            this.LogInfo($"Sin indicadores técnicos extra: {ex.Message}");
        }
        Catalog.ScanFolder(Path.Combine(AppContext.BaseDirectory, "Indicators"), "Extra");
        Catalog.ScanFolder(Path.Combine(AppPaths.Root, "Indicators"), "Extra");
    }

    public AppSettings Settings { get; } = AppSettings.Load();
    public ConnectionManager Connections { get; } = new();
    public MarketDataHub Hub { get; } = new();

    /// <summary>Condición de cada mercado (rango o tendencia y volatilidad), la misma medida que usan los filtros de contexto.</summary>
    internal MarketConditions Markets { get; }

    private void OnRunnerStarted(TradingHub.Strategies.LiveStrategyRunner runner) => _ = Markets.SeedAsync(runner);
    public Portfolio Portfolio { get; } = new() { AccountID = "SIM-1", Name = "Cuenta simulada", BeginBalance = 10_000m, Balance = 10_000m };

    /// <summary>Ejecuciones guardadas de cada cuenta (sobreviven al cerrar Trading Hub).</summary>
    public AccountJournal Journal { get; } = new(AppPaths.Accounts);

    private AccountCenter? _accountCenter;
    public AccountCenter AccountCenter => _accountCenter ??= new AccountCenter(this);
    public TradingStatistics Statistics { get; } = new();
    public TradingStatisticsProvider StatsProvider { get; } = new();
    public IndicatorCatalog Catalog { get; } = new();
    public UserScripts? Scripts { get; set; }

    private StrategyCatalog? _strategies;

    /// <summary>Estrategias del usuario (se crea al entrar a la sección).</summary>
    public StrategyCatalog Strategies => _strategies ??= new StrategyCatalog();

    /// <summary>Estrategias corriendo en vivo.</summary>
    public List<TradingHub.Strategies.LiveStrategyRunner> Runners { get; } = new();


    /// <summary>Modo sin dormir (aserción de energía y, si lo activás, seguir con la tapa cerrada).</summary>
    internal SleepGuard Sleep { get; } = new();

    /// <summary>Por qué la Mac tiene que seguir despierta ahora (o null si no hay nada corriendo).</summary>
    internal string? AwakeReason()
    {
        int live = Runners.Count(r => r.IsRunning);
        bool agents = _live?.RiskAgentBusy ?? false;
        bool waiting = false;
        if (live == 0 && !agents && !waiting) return null;
        var parts = new List<string>();
        if (live > 0) parts.Add($"{live} estrategia(s) en vivo");
        if (agents) parts.Add("agentes trabajando");
        return string.Join(" y ", parts);
    }

    private LiveCenter? _live;

    /// <summary>Todas las estrategias en vivo en un solo lugar (panel y gráficos), sus portafolios y el agente de riesgo.</summary>
    internal LiveCenter Live => _live ??= new LiveCenter(this);

    /// <summary>Guarda el estado de la cartera en vivo (lo lee el laboratorio de Claude). Solo escribe si cambió algo.</summary>
    /// <summary>Estrategias en vivo (del panel y de los gráficos) que corrían cuando se cerró Trading Hub.</summary>
    internal List<TradingHub.Strategies.LiveRunnerInfo> RunningAtLastClose { get; set; } = new();

    private bool _closingDown;

    public void SaveLiveState()
    {
        // al cerrar se guarda una vez, con lo que estaba corriendo: después se detienen las estrategias y el guardado
        // periódico (que sigue unos instantes) las marcaría detenidas, y al abrir no se reanudarían solas
        if (_closingDown) return;
        var infos = Runners.Where(r => r.Persist).Select(r => r.Info()).ToList();
        TradingHub.Strategies.LiveStore.SaveRunners(infos.GroupBy(i => i.Key).Select(g => g.OrderByDescending(i => i.Running).First()));
    }

    /// <summary>Aplica un tamaño aprobado: a la estrategia si está corriendo y a lo guardado para la próxima vez.</summary>
    public void ApplySize(string key, decimal size) => Live.ApplySize(key, size);

    public TimeSpan HistoryFor(string profileId) =>
        TimeSpan.FromHours(Math.Clamp(Connections.Find(profileId)?.HistoryHours ?? 1.5, 0.1, 24 * 60));

    /// <summary>Abre (o reutiliza) los datos de un instrumento de una conexión.</summary>
    public InstrumentData Open(string profileId, Instrument instrument) =>
        Hub.GetOrStart(Connections.GetFeed(profileId), instrument, HistoryFor(profileId));

    /// <summary>Abre un replay de datos guardados (no se graba ni se guarda en el espacio de trabajo).</summary>
    public InstrumentData OpenReplay(Instrument source, DateTime startUtc, DateTime endUtc, TimeSpan preload)
    {
        var feed = new ReplayFeed(AppPaths.Ticks, source, startUtc, endUtc);
        return Hub.GetOrStart(feed, feed.Instrument, preload);
    }

    public SimBroker SimBrokerFor(InstrumentData data)
    {
        if (!_simBrokers.TryGetValue(data.Instrument.Key, out var b))
        {
            b = new SimBroker(data, Portfolio, Statistics);
            _simBrokers[data.Instrument.Key] = b;
            var inst = data.Instrument;
            b.NewMyTrade += t => Journal.Add(AccountCenter.SimId, new AccountFill(Guid.NewGuid().ToString("N"), t.Time, inst.Symbol,
                t.OrderDirection == OrderDirections.Buy, t.Volume, t.Price, inst.PointValue > 0 ? inst.PointValue : 1, t.Commission, t.Order?.Id.ToString() ?? ""));
        }

        return b;
    }

    public IEnumerable<SimBroker> SimBrokers => _simBrokers.Values;

    /// <summary>Todas las cuentas abiertas en esta sesión (simuladas y reales): para saber si queda algo abierto antes de reiniciar.</summary>
    internal IEnumerable<ITradingAccount> OpenAccounts() => _simBrokers.Values.Cast<ITradingAccount>().Concat(_realBrokers.Values);

    private readonly Dictionary<ITradingAccount, BracketManager> _brackets = new(ReferenceEqualityComparer.Instance);

    /// <summary>Stop/objetivos de una cuenta (uno por cuenta e instrumento), con la plantilla del instrumento.</summary>
    public BracketManager BracketsFor(ITradingAccount account)
    {
        if (!_brackets.TryGetValue(account, out var m))
        {
            m = new BracketManager(account);
            _brackets[account] = m;
            BracketsCreated?.Invoke(m);
        }

        m.Template = Settings.BracketTemplateFor(account.Instrument.Key);
        return m;
    }

    /// <summary>Se creó el manejador de stop/objetivos de una cuenta (para escuchar sus avisos).</summary>
    public event Action<BracketManager>? BracketsCreated;

    /// <summary>Cuentas con las que se puede operar este instrumento.</summary>
    public IEnumerable<AccountOption> AccountsFor(InstrumentData data)
    {
        yield return new AccountOption("sim", "Cuenta simulada", false);
        foreach (var p in Connections.Enabled.Where(p => p.SupportsTrading && p.MarketDataId == data.Instrument.FeedId && !string.IsNullOrWhiteSpace(p.ApiKey)))
        {
            if (p.Testnet) yield return new AccountOption(p.Id, $"{p.Name} (testnet)", false);
            else if (p.AllowRealOrders) yield return new AccountOption(p.Id, $"{p.Name} — REAL", true);
        }

        // cuentas de Tradovate: operan cualquier futuro de CME, también desde un gráfico de Rithmic
        if (IsCmeFuture(data.Instrument))
            foreach (var p in Connections.Enabled.Where(p => p.Kind == ConnectionKind.Tradovate && !string.IsNullOrWhiteSpace(p.TradovateUser)))
            {
                var session = TradovateSessionFor(p);
                foreach (var a in session.Accounts)
                {
                    if (session.IsDemo) yield return new AccountOption(TradovateAccountId(p, a.Id), $"{a.Name} · Tradovate (demo)", false);
                    else if (p.AllowRealOrders) yield return new AccountOption(TradovateAccountId(p, a.Id), $"{a.Name} · Tradovate — REAL", true);
                }
            }

        // cuentas de Rithmic (aparecen cuando la sesión de órdenes termina de conectarse)
        foreach (var p in Connections.Enabled.Where(p => p.IsRithmic && p.MarketDataId == data.Instrument.FeedId && !string.IsNullOrWhiteSpace(p.RithmicUser)))
        {
            var session = RithmicSessionFor(p);
            foreach (var a in session.Accounts)
            {
                if (session.IsPractice) yield return new AccountOption(RithmicAccountId(p, a.AccountId), $"{a.AccountId} · Rithmic (práctica)", false);
                else if (p.AllowRealOrders) yield return new AccountOption(RithmicAccountId(p, a.AccountId), $"{a.AccountId} · Rithmic — REAL", true);
            }
        }
    }

    public ITradingAccount AccountFor(InstrumentData data, string accountId)
    {
        if (accountId == "sim") return SimBrokerFor(data);
        string key = accountId + "|" + data.Instrument.Key;
        if (_realBrokers.TryGetValue(key, out var b)) return b;
        if (accountId.StartsWith("tradovate|", StringComparison.Ordinal))
        {
            var parts = accountId.Split('|');
            var tp = Connections.Find(parts[1]) ?? throw new KeyNotFoundException(accountId);
            var session = TradovateSessionFor(tp);
            long id = long.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
            var acc = session.Accounts.FirstOrDefault(a => a.Id == id) ?? new TradingHub.Feeds.Tradovate.TradovateAccount(id, parts[2], true);
            var tb = new TradingHub.Feeds.Tradovate.TradovateBroker(data, session, acc, $"{acc.Name} · Tradovate{(session.IsDemo ? " (demo)" : " — REAL")}");
            _realBrokers[key] = tb;
            return tb;
        }

        if (accountId.StartsWith("rithmic|", StringComparison.Ordinal))
        {
            var parts = accountId.Split('|');
            var rp = Connections.Find(parts[1]) ?? throw new KeyNotFoundException(accountId);
            var session = RithmicSessionFor(rp);
            var feed = (TradingHub.Feeds.Rithmic.RithmicFeed)Connections.GetFeed(rp.Id);
            var rb = new TradingHub.Feeds.Rithmic.RithmicBroker(data, session, parts[2], $"{parts[2]} · Rithmic{(session.IsPractice ? " (práctica)" : " — REAL")}",
                ct => feed.ContractAsync(data.Instrument, ct));
            _realBrokers[key] = rb;
            return rb;
        }

        var p = Connections.Find(accountId) ?? throw new KeyNotFoundException(accountId);
        b = new BinanceBroker(data, Connections.BinanceSettingsOf(p), p.Testnet ? $"{p.Name} (testnet)" : $"{p.Name} — REAL");
        _realBrokers[key] = b;
        return b;
    }

    public void Shutdown()
    {
        try
        {
            SaveLiveState();
            _closingDown = true;
            TradingHub.Strategies.LiveStrategyRunner.Started -= OnRunnerStarted;
            Sleep.Dispose();
            foreach (var r in Runners) r.Stop(flatten: false);
            foreach (var b in _realBrokers.Values) (b as IDisposable)?.Dispose();
            foreach (var rs in _rithmicSessions.Values) rs.Dispose();
            foreach (var ts in _tradovateSessions.Values) ts.Dispose();
            Hub.Dispose();
            Connections.DisposeAll();
        }
        catch (Exception ex)
        {
            this.LogError("Error al cerrar", ex);
        }
    }
}
