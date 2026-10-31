using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using TradingHub.App.Controls;
using TradingHub.App.Services;
using TradingHub.App.Views;
using TradingHub.Strategies;

namespace TradingHub.App.Windows;

/// <summary>
/// Gestión de una estrategia en vivo (la haya lanzado el panel o un gráfico): estado, pausa, cerrar
/// posición, detener, reanudar, borrar, su riesgo (tamaño y límites), sus parámetros, su portafolio y su actividad. Las
/// bloqueadas tienen el riesgo y los parámetros fijos.
/// </summary>
internal sealed class LiveStrategyWindow : Window
{
    private static readonly CultureInfo Es = TradingHub.Common.UiCulture.Current;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly AppServices _svc;
    private readonly Action<string> _status;
    private string _key;
    private LiveEntry _entry;
    private readonly TextBlock _title = new() { Classes = { "h2" }, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _subtitle = new() { Classes = { "muted", "small" }, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _state = new() { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly Border _lockNote = new() { IsVisible = false };
    private readonly WrapPanel _kpis = new() { ItemSpacing = 8, LineSpacing = 8 };
    private readonly WrapPanel _actions = new() { ItemSpacing = 8, LineSpacing = 8 };
    private readonly NumericUpDown _size = Num(0.01m, 0, 100, "0.##");
    private readonly NumericUpDown _dailyLoss = Num(10, 0, 100_000_000, "0.##");
    private readonly NumericUpDown _maxDd = Num(10, 0, 100_000_000, "0.##");
    private readonly CheckBox _closeOnLimit = new() { Content = "Al tocar un límite, cerrar también su posición" };
    private readonly TextBlock _riskNote = new() { Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap };
    private Button _applyRisk = null!, _applyParams = null!, _applyConditions = null!;
    private readonly ParameterEditor _params = new();
    private readonly MarketConditionPicker _conditions = new();

    /// <summary>Los parámetros del filtro de contexto: se editan en «Condición de mercado», no en la lista de parámetros.</summary>
    internal static readonly string[] ContextParameters = { nameof(Strategy.RegimeFilter), nameof(Strategy.MinVolatilityRank), nameof(Strategy.MaxVolatilityRank) };
    private readonly ComboBox _portfolio = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ListBox _log = new() { Background = Brushes.Transparent, MaxHeight = 260 };
    private readonly Avalonia.Threading.DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _logCount = -1;
    private string _actionsKey = "";
    private bool _fillingPortfolio;

    public LiveStrategyWindow(AppServices svc, Action<string> status, LiveEntry entry)
    {
        _svc = svc;
        _status = status;
        _entry = entry;
        _key = entry.Key;
        Title = $"Estrategia en vivo · {entry.Info.Strategy}";
        Width = 620;
        Height = 820;
        MinWidth = 520;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Ui.Bg1;

        _applyRisk = Ui.Button("Aplicar riesgo", MaterialIconKind.Check, () => _ = ApplyRiskAsync(), "primary");
        _applyParams = Ui.Button("Aplicar y reiniciar", MaterialIconKind.Restart, () => _ = ApplyParametersAsync(), "primary");
        _applyConditions = Ui.Button("Aplicar", MaterialIconKind.Check, ApplyConditions, "primary");
        _conditions.Changed += () => _applyConditions.IsEnabled = !_entry.Locked && _conditions.IsValid;
        _portfolio.SelectionChanged += (_, _) =>
        {
            if (_fillingPortfolio || _portfolio.SelectedItem is not ComboBoxItem item) return;
            _svc.Live.Assign(_key, item.Tag as string);
            _status($"{_entry.Info.Strategy}: {(item.Tag == null ? "sin portafolio (va al automático)" : "asignada a «" + item.Content + "»")}");
        };

        var lockText = new TextBlock
        {
            Text = "Bloqueada: el riesgo y los parámetros no se cambian desde acá. Podés verla, detenerla o reanudarla.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
        };
        var lockIcon = Ui.Icon(MaterialIconKind.ShieldLockOutline, 18, Ui.Warn);
        lockIcon.Margin = new Thickness(0, 0, 10, 0);
        lockIcon.VerticalAlignment = VerticalAlignment.Top;
        var lockRow = new DockPanel();
        DockPanel.SetDock(lockIcon, Dock.Left);
        lockRow.Children.Add(lockIcon);
        lockRow.Children.Add(lockText);
        _lockNote.Child = Ui.Card(lockRow, 12);

        var riskGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*,12,*") };
        void Put(Control c, int col)
        {
            Grid.SetColumn(c, col);
            riskGrid.Children.Add(c);
        }

        Put(Ui.Field("Tamaño (×)", _size, "1 = como dice la estrategia"), 0);
        Put(Ui.Field("Pérdida máx. del día", _dailyLoss, "0 = sin límite"), 2);
        Put(Ui.Field("Drawdown máximo", _maxDd, "0 = sin límite"), 4);

        var body = new StackPanel { Spacing = 18, Margin = new Thickness(22, 18, 22, 18) };
        body.Children.Add(Ui.V(4, _title, _subtitle, _state));
        body.Children.Add(_lockNote);
        body.Children.Add(_kpis);
        body.Children.Add(Section("Acciones", _actions));
        body.Children.Add(Section("Riesgo", riskGrid, _closeOnLimit, _riskNote, Right(_applyRisk)));
        body.Children.Add(Section("Condición de mercado", _conditions,
            Note("Solo abre operaciones cuando el mercado está en lo que elijas (lo mide con las velas ya cerradas: tendencia de las últimas 24 horas y volatilidad del último mes). Se aplica ya, sin reiniciar; las salidas siempre pasan."),
            Right(_applyConditions)));
        body.Children.Add(Section("Parámetros", _params, Right(_applyParams)));
        body.Children.Add(Section("Portafolio", _portfolio, Note("Una estrategia está en un solo portafolio tuyo; las que no tienen van al automático (todas las que corren en vivo).")));
        body.Children.Add(Section("Actividad", Ui.Card(_log, 6)));
        Content = new ScrollViewer { Content = body };

        LoadForm();
        Refresh();
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape) Close();
        };
    }

    private static NumericUpDown Num(decimal increment, decimal min, decimal max, string format) => new()
    {
        Increment = increment,
        Minimum = min,
        Maximum = max,
        FormatString = format,
        NumberFormat = CultureInfo.InvariantCulture.NumberFormat,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    private static TextBlock Note(string text) => new() { Text = text, Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap };

    private static Control Right(Control c)
    {
        c.HorizontalAlignment = HorizontalAlignment.Right;
        return c;
    }

    private static Control Section(string title, params Control[] rows)
    {
        var v = new StackPanel { Spacing = 9 };
        v.Children.Add(Ui.H3(title));
        foreach (var r in rows) v.Children.Add(r);
        return v;
    }

    private static string Money(decimal v) => v.ToString("+#,##0.00;-#,##0.00;0.00", Es);

    /// <summary>Formulario con lo actual (riesgo, parámetros y portafolio).</summary>
    private void LoadForm()
    {
        var e = _entry;
        var r = e.Runner;
        _size.Value = r?.SizeMultiplier ?? e.Info.Size;
        _dailyLoss.Value = r?.MaxDailyLoss ?? e.Info.MaxDailyLoss;
        _maxDd.Value = r?.MaxDrawdown ?? e.Info.MaxDrawdown;
        _closeOnLimit.IsChecked = r?.CloseOnLimit ?? e.Info.CloseOnLimit;
        bool locked = e.Locked;
        _lockNote.IsVisible = locked;
        foreach (var c in new Control[] { _size, _dailyLoss, _maxDd, _closeOnLimit, _applyRisk, _applyParams }) c.IsEnabled = !locked;

        var catalog = _svc.Strategies;
        if (catalog.Files.Count == 0) catalog.Refresh();
        var file = catalog.Files.FirstOrDefault(f => f.Id == e.Info.StrategyId);
        var type = r?.StrategyInstance.GetType() ?? file?.Type;
        var values = _svc.Live.ParametersOf(e);
        _params.Load(type, values, readOnly: locked, hidden: ContextParameters);
        var probe = type != null ? (Strategy)Activator.CreateInstance(type)! : null;
        probe?.SetParameters(values);
        _conditions.Load(probe?.RegimeFilter ?? RegimeFilter.All, probe?.MinVolatilityRank ?? 0, probe?.MaxVolatilityRank ?? 100);
        _conditions.Editable = !locked;
        _applyConditions.IsEnabled = !locked && _conditions.IsValid;

        _fillingPortfolio = true;
        _portfolio.Items.Clear();
        _portfolio.Items.Add(new ComboBoxItem { Content = "Ninguno (va al automático)", Tag = null });
        var defs = LiveStore.LoadPortfolios().Where(d => !d.Auto).ToList();
        foreach (var d in defs) _portfolio.Items.Add(new ComboBoxItem { Content = d.Name, Tag = d.Id });
        string? current = defs.FirstOrDefault(d => d.Members.Contains(_key))?.Id;
        _portfolio.SelectedItem = _portfolio.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (i.Tag as string) == current) ?? _portfolio.Items[0];
        _fillingPortfolio = false;
    }

    private static bool MarketInfoHasMicro(TradingHub.Strategies.LiveRunnerInfo info) =>
        MarketInfo.IsFutures(info.FeedId) && MarketInfo.MicroOf(info.Symbol) != null;

    /// <summary>En qué contrato van las órdenes y por qué (contrato inteligente).</summary>
    private string ContractNote(TradingHub.Strategies.LiveRunnerInfo info, TradingHub.Strategies.LiveStrategyRunner? r)
    {
        decimal qty = LiveCenter.QuantityOf(info.Parameters);
        decimal size = r?.SizeMultiplier ?? info.Size;
        var (symbol, ratio) = _svc.Live.PreferredContract(info.FeedId, info.Symbol, qty, size);
        string q(decimal v) => v.ToString("0.####", Es);
        if (r is { CrossExecution: true })
            return $"\nContrato: {q(qty)} {info.Symbol} × {q(size)} = {q(qty * size * r.ExecRatio)} {r.ExecInstrument.Symbol} (la misma exposición en micros).";
        if (!symbol.Equals(info.Symbol, StringComparison.OrdinalIgnoreCase))
            return $"\nContrato: con este tamaño conviene {symbol} ({q(qty * size * ratio)} {symbol}); cambia solo cuando quede plana.";
        if (r != null && r.SizeBelowMinimum(qty))
            return $"\nContrato: {q(qty)} {info.Symbol} × {q(size)} no llega a 1 contrato: se manda 1.";
        return "";
    }

    private void Refresh()
    {
        var fresh = _svc.Live.Entries().FirstOrDefault(x => x.Key == _key);
        if (fresh != null) _entry = fresh;
        var e = _entry;
        var info = e.Info;
        var r = e.Runner;
        _title.Text = info.Strategy;
        _subtitle.Text = $"{info.Symbol} · {info.Period} · {info.Account}{(info.IsReal ? " — REAL" : "")} · lanzada desde {e.SourceLabel}" +
                         (r is { CrossExecution: true } ? $" · ejecuta en {r.ExecInstrument.Symbol}" : "");
        _subtitle.Foreground = info.IsReal ? Ui.Warn : Ui.Text1;
        // cómo está el mercado para la estrategia (lo que decide si su filtro la deja entrar)
        if (r is { IsRunning: true }) _conditions.SetContext(r.StrategyInstance.Regime, r.StrategyInstance.MarketDirection, r.StrategyInstance.VolatilityRank);
        string why = r?.StopReason ?? info.StopReason ?? "";
        _state.Text = "Estado: " + e.StateLabel + (why.Length > 0 && !e.Running ? $" · {why}" : r is { DailyLimitActive: true } ? $" · {why}" : "");
        _state.Foreground = e.Running ? (e.Paused || r!.DailyLimitActive ? Ui.Warn : Ui.Up) : Ui.Text1;

        var stats = LiveStore.StatsOf(info, e.Ledger, 25_000m);
        _kpis.Children.Clear();
        decimal pos = e.Ledger.Position;
        var plan = _svc.Live.PlanOf(e);
        string posSymbol = e.Ledger.OpenSymbol ?? plan.Symbol;
        _kpis.Children.Add(Kpi("Posición", pos == 0 ? "Plana" : $"{(pos > 0 ? "Largo" : "Corto")} {Math.Abs(pos).ToString("0.####", Es)} {posSymbol}", pos == 0 ? "" : "@ " + e.Ledger.AveragePrice.ToString("0.#####", Inv), pos > 0 ? Ui.Up : pos < 0 ? Ui.Down : null));
        // cuántos contratos y de cuál manda cada orden: que nunca haya dudas entre el mini y el micro
        _kpis.Children.Add(Kpi("Cada orden", plan.Text, plan.Detail.Length > 0 ? plan.Detail : MarketInfoHasMicro(info) ? "contrato mini" : "", plan.Micro ? Ui.Accent : null));
        _kpis.Children.Add(Kpi("Abierto", Money(e.OpenPnL), r != null ? $"hoy {Money(r.TodayPnL)}" : "", e.OpenPnL >= 0 ? Ui.Up : Ui.Down));
        _kpis.Children.Add(Kpi("Neto", Money(e.Net), $"{stats.Metrics.TotalTrades} operaciones", e.Net >= 0 ? Ui.Up : Ui.Down));
        _kpis.Children.Add(Kpi("Profit factor", stats.Metrics.TotalTrades > 0 ? (stats.Metrics.ProfitFactor >= 999 ? "∞" : stats.Metrics.ProfitFactor.ToString("0.00", Es)) : "—", stats.Metrics.TotalTrades > 0 ? $"{stats.Metrics.WinRate:0} % ganadoras" : ""));
        _kpis.Children.Add(Kpi("Drawdown", (-(r?.CurrentDrawdown ?? stats.CurrentDrawdown)).ToString("#,##0.00", Es), $"peor {(-stats.Metrics.MaxDrawdown).ToString("#,##0.00", Es)}"));
        _riskNote.Text =
            $"El tamaño multiplica las órdenes nuevas (las salidas siempre cierran justo lo abierto). Pérdida del día: frena hasta la próxima sesión. Drawdown: la detiene. Drawdown actual {(r?.CurrentDrawdown ?? stats.CurrentDrawdown).ToString("#,##0.00", Es)}." +
              ContractNote(info, r);

        RefreshActions();
        var lines = r?.LogLines;
        int count = lines?.Count ?? 0;
        if (count != _logCount)
        {
            _logCount = count;
            _log.Items.Clear();
            if (lines == null || lines.Count == 0)
                _log.Items.Add(new ListBoxItem { Content = new TextBlock { Text = e.Running ? "Sin actividad todavía." : "La actividad se ve mientras corre en esta sesión de Trading Hub.", Classes = { "muted", "small" } }, IsEnabled = false });
            else
                foreach (var line in lines.Reverse().Take(200))
                    _log.Items.Add(new ListBoxItem { Content = new TextBlock { Text = line, Classes = { "mono", "small" }, TextWrapping = TextWrapping.Wrap }, Padding = new Thickness(8, 2) });
        }
    }

    private static Control Kpi(string label, string value, string sub, IBrush? color = null) => Ui.Card(Ui.V(2,
        new TextBlock { Text = label, Classes = { "faint", "small" } },
        new TextBlock { Text = value, FontSize = 17, FontWeight = FontWeight.Bold, Foreground = color ?? Ui.Text0 },
        new TextBlock { Text = sub, Classes = { "faint", "small" } }), 10).Also(b => b.MinWidth = 104);

    /// <summary>Los botones según el estado (se rehacen solo si cambió).</summary>
    private void RefreshActions()
    {
        var e = _entry;
        bool running = e.Running, locked = e.Locked;
        string key = $"{running}|{e.Paused}|{locked}|{e.WaitingResume}|{e.Ledger.Position != 0}|{e.Ledger.Trades.Count > 0}";
        if (key == _actionsKey) return;
        _actionsKey = key;
        _actions.Children.Clear();
        void Add(Button b) => _actions.Children.Add(b);
        if (running && !locked)
        {
            Add(e.Paused
                ? Ui.Button("Reanudar", MaterialIconKind.PlayCircleOutline, () => SetPaused(false), "primary")
                : Ui.Button("Pausar", MaterialIconKind.PauseCircleOutline, () => SetPaused(true)));
            if (e.Ledger.Position != 0) Add(Ui.Button("Cerrar posición", MaterialIconKind.CloseCircleOutline, () => _ = ClosePositionAsync(), "danger"));
        }

        if (running)
        {
            Add(Ui.Button("Detener", MaterialIconKind.Stop, () => _ = StopAsync(false)));
            if (!locked && e.Ledger.Position != 0) Add(Ui.Button("Detener y cerrar", MaterialIconKind.StopCircleOutline, () => _ = StopAsync(true), "danger"));
        }
        else Add(Ui.Button(e.WaitingResume ? "Reanudar en el gráfico" : "Volver a arrancar", MaterialIconKind.Play, () => _ = ResumeAsync(), "primary"));

        Add(Ui.Button("Ver en el gráfico", MaterialIconKind.ChartBoxOutline, () => _ = ShowOnChartAsync()));
        if (e.Ledger.Trades.Count > 0) Add(Ui.Button("Ver operaciones", MaterialIconKind.FormatListBulleted, OpenTrades));
        if (!locked && e.Ledger.Fills.Count > 0 && e.Ledger.Position == 0)
            Add(Ui.Button("Empezar de cero", MaterialIconKind.Restart, () => _ = ResetHistoryAsync()));
        if (!locked) Add(Ui.Button("Borrar", MaterialIconKind.DeleteOutline, () => _ = DeleteAsync(), "danger"));
    }

    private async Task ResetHistoryAsync()
    {
        if (!await ConfirmDialog.ShowAsync(this, "Empezar de cero",
                $"Se borran las {_entry.Ledger.Trades.Count} operación(es) registradas de «{_entry.Info.Strategy}» (resultado, estadísticas y curva). La estrategia sigue como está. No se puede deshacer.",
                "Borrar su historial", danger: true))
            return;
        var err = _svc.Live.ResetHistory(_entry);
        _status(err ?? $"{_entry.Info.Strategy}: historial borrado, empieza de cero");
        _actionsKey = "";
        _logCount = -1;
        Refresh();
    }

    private async Task<bool> ConfirmRealAsync(string title, string message, string ok)
    {
        if (!_entry.Info.IsReal) return true;
        return await ConfirmDialog.ShowAsync(this, title, message, ok, danger: true);
    }

    private void SetPaused(bool paused)
    {
        _svc.Live.SetPaused(_entry, paused);
        _status(paused ? $"{_entry.Info.Strategy} pausada: no abre operaciones nuevas" : $"{_entry.Info.Strategy} reanudada");
        _actionsKey = "";
        Refresh();
    }

    private async Task ClosePositionAsync()
    {
        if (!await ConfirmDialog.ShowAsync(this, "Cerrar la posición", $"Se cierra a mercado la posición de «{_entry.Info.Strategy}» ({_entry.Ledger.Position.ToString("0.####", Inv)}). La estrategia sigue corriendo y puede volver a entrar (pausala si no querés).", "Cerrar posición", danger: true))
            return;
        _svc.Live.ClosePosition(_entry);
        _status($"{_entry.Info.Strategy}: posición cerrada a mercado");
        _actionsKey = "";
        Refresh();
    }

    private async Task StopAsync(bool close)
    {
        string what =
            close ? "Se detiene y se cierra su posición a mercado." : "Se detiene; si tiene una posición abierta, queda como está.";
        if (_entry.ChartStrategy != null && !_entry.Locked) what += " En el gráfico queda en modo visual.";
        if (!await ConfirmDialog.ShowAsync(this, "Detener la estrategia", what, close ? "Detener y cerrar" : "Detener", danger: close)) return;
        await _svc.Live.StopAsync(_entry, close);
        _status($"{_entry.Info.Strategy} detenida{(close ? " y posición cerrada" : "")}");
        _actionsKey = "";
        Refresh();
    }

    private async Task ResumeAsync()
    {
        if (!await ConfirmRealAsync("Ejecutar en cuenta REAL", $"«{_entry.Info.Strategy}» va a operar {_entry.Info.Symbol} en {_entry.Info.Account} con órdenes reales y sin confirmación individual.", "Ejecutar con dinero real"))
            return;
        var err = await _svc.Live.ResumeAsync(_entry);
        _status(err ?? $"{_entry.Info.Strategy} corriendo de nuevo");
        var fresh = _svc.Live.Entries().FirstOrDefault(x => x.Key == _key);
        if (fresh != null) _entry = fresh;
        _actionsKey = "";
        LoadForm();
        Refresh();
    }

    private async Task ShowOnChartAsync()
    {
        var last = _entry.Ledger.Trades.LastOrDefault();
        var err = await _svc.Live.ShowTradeAsync(_entry, last);
        _status(err ?? (last != null ? $"Última operación de {_entry.Info.Strategy} en el gráfico" : $"{_entry.Info.Symbol} en el gráfico"));
    }

    private void OpenTrades() => new LiveTradesWindow(_svc, _status, _key, $"Operaciones · {_entry.Info.Strategy} · {_entry.Info.Symbol}").Show(this);

    private async Task DeleteAsync()
    {
        bool ok = await ConfirmDialog.ShowAsync(this, "Borrar de En vivo",
            $"«{_entry.Info.Strategy}» ({_entry.Info.Symbol} · {_entry.Info.Account}) se detiene{(_entry.ChartStrategy != null ? ", se quita del gráfico" : "")} y deja de aparecer en En vivo. " +
            "Si tiene una posición abierta, queda como está (cerrala antes si querés).",
            "Borrar", danger: true);
        if (!ok) return;
        bool history = _entry.Ledger.Fills.Count > 0 && await ConfirmDialog.ShowAsync(this, "¿Borrar también su historial?",
            $"Tiene {_entry.Ledger.Trades.Count} operación(es) registradas. Si lo borrás, no se puede recuperar; si lo conservás y la volvés a lanzar igual, sigue con su historia.",
            "Borrar el historial", danger: true);
        var err = await _svc.Live.DeleteAsync(_entry, history);
        _status(err ?? $"{_entry.Info.Strategy} borrada de En vivo");
        if (err == null) Close();
    }

    private async Task ApplyRiskAsync()
    {
        decimal size = _size.Value ?? 1, dl = _dailyLoss.Value ?? 0, dd = _maxDd.Value ?? 0;
        if (!await ConfirmRealAsync("Cambiar el riesgo en cuenta REAL", $"{_entry.Info.Strategy}: tamaño ×{size.ToString("0.##", Inv)}, pérdida máx. del día {dl.ToString("0.##", Inv)}, drawdown máx. {dd.ToString("0.##", Inv)}.", "Aplicar"))
            return;
        if (_svc.Live.ApplyRisk(_entry, size, dl, dd, _closeOnLimit.IsChecked == true))
            _status($"Riesgo de {_entry.Info.Strategy}: tamaño ×{size.ToString("0.##", Inv)}{(dl > 0 ? $" · pérdida del día {dl.ToString("0.##", Inv)}" : "")}{(dd > 0 ? $" · drawdown {dd.ToString("0.##", Inv)}" : "")}");
        Refresh();
    }

    /// <summary>La condición de mercado elegida: ya, sin reiniciar.</summary>
    private void ApplyConditions()
    {
        if (!_conditions.IsValid) return;
        var (filter, min, max) = _conditions.Value;
        var err = _svc.Live.ApplyContextFilter(_entry, filter, min, max);
        _status(err ?? $"«{_entry.Info.Strategy}» opera {_conditions.Describe()}");
    }

    private async Task ApplyParametersAsync()
    {
        var values = _params.Read(_svc.Live.ParametersOf(_entry));
        string text = _entry.Running
            ? "Se reinicia con los parámetros nuevos (vuelve a calentar con la historia). Si tiene una posición abierta, la sigue gestionando con su stop."
            : "Se guardan los parámetros nuevos y arranca con ellos.";
        if (!await ConfirmDialog.ShowAsync(this, "Aplicar parámetros", text, "Aplicar y reiniciar")) return;
        if (!await ConfirmRealAsync("Reiniciar en cuenta REAL", $"«{_entry.Info.Strategy}» vuelve a operar {_entry.Info.Symbol} en {_entry.Info.Account} con órdenes reales.", "Reiniciar con dinero real")) return;
        var err = await _svc.Live.RestartWithParametersAsync(_entry, values);
        _status(err ?? $"{_entry.Info.Strategy} reiniciada con los parámetros nuevos");
        var fresh = _svc.Live.Entries().FirstOrDefault(x => x.Key == _key);
        if (fresh != null) _entry = fresh;
        _actionsKey = "";
        LoadForm();
        Refresh();
    }
}
