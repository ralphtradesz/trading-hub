using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using TradingHub.App.Services;
using TradingHub.App.Views;
using TradingHub.Strategies;

namespace TradingHub.App.Windows;

/// <summary>
/// Configuración de una estrategia puesta en un gráfico: solo visual o ejecutando en una cuenta, sus parámetros y
/// cómo le fue con la historia del gráfico.
/// </summary>
internal sealed class StrategyChartWindow : Window
{
    private static readonly CultureInfo Es = TradingHub.Common.UiCulture.Current;
    private readonly TradingView _host;
    private readonly ChartTab _chart;
    private readonly ChartStrategy _cs;
    private readonly RadioButton _visual = new() { Content = "Solo visual: marca dónde entra y dónde sale, no opera", GroupName = "modo" };
    private readonly RadioButton _live = new() { Content = "Ejecutar en vivo con una cuenta", GroupName = "modo" };
    private readonly ComboBox _account = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _accountNote = new() { Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _params = new() { Spacing = 8 };
    // cómo le fue con la historia del gráfico: el resultado real, la curva y todas las estadísticas
    private readonly TextBlock _net = new() { FontSize = 26, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _netCaption = new() { Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap };
    private readonly Controls.EquityChart _curve = new() { Height = 130, EmptyText = "Sin operaciones todavía" };
    private readonly Grid _stats = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto,22,*,Auto") };
    private readonly TextBlock _liveText = new() { Classes = { "small" }, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _costsNote = new() { Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _results = new() { Spacing = 10 };
    private readonly TextBlock _emptyResults = new() { TextWrapping = TextWrapping.Wrap, Classes = { "muted" } };
    private PerformanceMetrics? _shown;
    private readonly TextBlock _state = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    private readonly List<(PropertyInfo Prop, Func<object?> Read)> _editors = new();
    private readonly Avalonia.Threading.DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private StrategyFile? _file;

    public StrategyChartWindow(TradingView host, ChartTab chart, ChartStrategy cs)
    {
        _host = host;
        _chart = chart;
        _cs = cs;
        Title = $"Estrategia en el gráfico · {cs.Overlay.Name}";
        Width = 520;
        Height = 860;
        MinWidth = 420;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Ui.Bg1;
        _file = host.StrategyCatalog.Files.FirstOrDefault(f => f.Id == cs.StrategyId) ?? host.StrategyCatalog.Files.FirstOrDefault(f => f.FileName == cs.StrategyId);

        _visual.IsChecked = !cs.Live;
        _live.IsChecked = cs.Live;
        _visual.IsCheckedChanged += (_, _) => UpdateMode();
        var accounts = chart.Model != null ? host.AccountOptions(chart, chart.Model.Data) : new List<AccountOption>();
        Ui.SyncAccounts(_account, accounts, cs.AccountId);
        if (_account.SelectedItem == null && _account.Items.Count > 0) _account.SelectedIndex = 0;
        BuildParameters();

        var body = new StackPanel { Spacing = 18, Margin = new Thickness(20, 18, 20, 18) };
        body.Children.Add(Ui.V(4, Ui.H2(cs.Overlay.Name), Note($"{chart.Symbol} · {chart.Model?.Period.DisplayName}. Marca sus entradas (flecha) y salidas (cruz con el resultado) en la historia del gráfico y en las nuevas."), _state));
        body.Children.Add(Section("Cómo corre", _visual, _live, Ui.Field("Cuenta", _account), _accountNote));
        body.Children.Add(Section("Parámetros", _params));
        var curve = new Border { Child = _curve, Background = Ui.Bg0, CornerRadius = new CornerRadius(10), Padding = new Thickness(6, 8), ClipToBounds = true };
        _results.Children.Add(Ui.V(2, _net, _netCaption));
        _results.Children.Add(curve);
        _results.Children.Add(_stats);
        _results.Children.Add(_liveText);
        _results.Children.Add(_costsNote);
        body.Children.Add(Section("En la historia del gráfico", _emptyResults, _results));

        var apply = Ui.Button("Aplicar", MaterialIconKind.Check, Apply, "primary");
        var remove = Ui.Button("Quitar del gráfico", MaterialIconKind.DeleteOutline, () =>
        {
            host.RemoveStrategy(chart, cs);
            Close();
        }, "danger");
        var close = Ui.Button("Cerrar", MaterialIconKind.Close, Close);
        var actions = new WrapPanel { ItemSpacing = 8, LineSpacing = 8, Margin = new Thickness(20, 10, 20, 16) };
        foreach (var b in new Control[] { apply, remove, close }) actions.Children.Add(b);

        var root = new DockPanel();
        var actionsBorder = new Border { Child = actions, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 0, 0) };
        DockPanel.SetDock(actionsBorder, Dock.Bottom);
        root.Children.Add(actionsBorder);
        root.Children.Add(new ScrollViewer { Content = body });
        Content = root;
        UpdateMode();
        Refresh();
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape) Close();
        };
    }

    private static TextBlock Note(string text) => new() { Text = text, Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap };

    private static Control Section(string title, params Control[] rows)
    {
        var v = new StackPanel { Spacing = 9 };
        v.Children.Add(Ui.H3(title));
        foreach (var r in rows) v.Children.Add(r);
        return v;
    }

    private void UpdateMode()
    {
        bool live = _live.IsChecked == true;
        _account.IsEnabled = live;
        _accountNote.Text = live
            ? "Manda órdenes de verdad a esa cuenta (si es real, te pide confirmar). La detenés volviendo a «Solo visual» o quitándola; la posición abierta queda como está."
            : "Corre con una cuenta simulada propia: no toca tu cuenta simulada ni tu historial.";
    }

    private void BuildParameters()
    {
        _params.Children.Clear();
        _editors.Clear();
        if (_file?.Type == null)
        {
            _params.Children.Add(Note("La estrategia no está compilada: abrila en Estrategias."));
            return;
        }

        var current = _file.Create();
        current.SetParameters(_cs.Parameters);
        foreach (var p in Strategy.Parameters(_file.Type))
        {
            object? value = p.GetValue(current);
            Control editor;
            Func<object?> read;
            var t = p.PropertyType;
            if (t == typeof(bool))
            {
                var cb = new CheckBox { IsChecked = value is true };
                editor = cb;
                read = () => cb.IsChecked == true;
            }
            else if (t.IsEnum)
            {
                var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
                foreach (var v in Enum.GetValues(t)) combo.Items.Add(new ComboBoxItem { Content = Controls.PropertyGrid.EnumDisplay(v), Tag = v });
                var selected = value is string str && Enum.TryParse(t, str, out var parsed) ? parsed : value;
                combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, selected));
                editor = combo;
                read = () => combo.SelectedItem is ComboBoxItem { Tag: { } tag } ? tag : value;
            }
            else if (t == typeof(int) || t == typeof(long) || t == typeof(double) || t == typeof(decimal) || t == typeof(float))
            {
                bool whole = t == typeof(int) || t == typeof(long);
                var num = new NumericUpDown
                {
                    Value = Convert.ToDecimal(value ?? 0, CultureInfo.InvariantCulture),
                    Increment = whole ? 1 : 0.1m,
                    FormatString = whole ? "0" : "0.####",
                    NumberFormat = CultureInfo.InvariantCulture.NumberFormat,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Minimum = decimal.MinValue,
                    Maximum = decimal.MaxValue,
                };
                editor = num;
                read = () => Convert.ChangeType(num.Value ?? 0, t, CultureInfo.InvariantCulture);
            }
            else
            {
                var tb = new TextBox { Text = value?.ToString() ?? "" };
                editor = tb;
                read = () => tb.Text;
            }

            var attr = p.GetCustomAttribute<ParameterAttribute>();
            if (!string.IsNullOrWhiteSpace(attr?.Description)) ToolTip.SetTip(editor, attr.Description);
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*") };
            g.Children.Add(new TextBlock { Text = Strategy.ParameterName(p), VerticalAlignment = VerticalAlignment.Center, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap });
            Grid.SetColumn(editor, 1);
            g.Children.Add(editor);
            _params.Children.Add(g);
            _editors.Add((p, read));
        }

        if (_editors.Count == 0) _params.Children.Add(Note("Esta estrategia no tiene parámetros."));
    }

    private void Refresh()
    {
        _state.Text = (_cs.Overlay.Error != null ? "⚠ " + _cs.Overlay.Error + " · " : "") + "Estado: " + _cs.Overlay.Status;
        _state.Foreground = _cs.Overlay.Error != null ? Ui.Down : _cs.Overlay.Live ? Ui.Accent : Ui.Text1;
        var m = _cs.HistoryMetrics;
        bool has = m is { TotalTrades: > 0 };
        _emptyResults.IsVisible = !has;
        _results.IsVisible = has;
        _emptyResults.Text = _cs.Busy ? "Calculando…" : "Sin operaciones en la historia cargada (cargá más días desde el botón de historia del gráfico).";

        // lo que corre en vivo (cambia cada segundo); lo de la historia se arma de nuevo solo cuando se recalcula
        var ledger = _cs.Runner?.Ledger;
        int live = ledger?.Trades.Count ?? 0;
        _liveText.IsVisible = live > 0;
        if (live > 0) _liveText.Text = $"Desde que corre en vivo: {live} operación(es) · resultado {Money(ledger!.Realized)}";
        if (!has || ReferenceEquals(m, _shown)) return;
        _shown = m;

        _net.Text = Money(m!.NetProfit);
        _net.Foreground = m.NetProfit >= 0 ? Ui.Up : Ui.Down;
        var from = _cs.HistoryEquity.Count > 0 ? _cs.HistoryEquity[0].Time.ToLocalTime() : (DateTime?)null;
        _netCaption.Text = $"Resultado real, con comisiones y deslizamiento · {m.TotalTrades} operaciones" + (from is { } f ? $" desde el {f:dd/MM/yyyy}" : "") +
                           $" · {m.ReturnPercent.ToString("+0.##;-0.##", Es)}% del capital";
        _curve.SetData(_cs.HistoryEquity, _cs.HistoryCapital);

        var items = new (string Label, string Value, IBrush? Color)[]
        {
            ("Operaciones", $"{m.TotalTrades} ({m.Winners} G · {m.Losers} P)", null),
            ("% ganadoras", $"{m.WinRate:0.#}%", null),
            ("Profit factor", m.ProfitFactor >= 999 ? "∞" : m.ProfitFactor.ToString("0.00", Es), m.ProfitFactor >= 1 ? Ui.Up : Ui.Down),
            ("Drawdown máx.", $"{(-m.MaxDrawdown).ToString("#,##0.00", Es)} ({m.MaxDrawdownPercent:0.#}%)", Ui.Down),
            ("Promedio por op.", Money(m.AverageTrade), m.AverageTrade >= 0 ? Ui.Up : Ui.Down),
            ("Ganancia / pérdida", m.PayoffRatio.ToString("0.00", Es), null),
            ("Ganancia media", Money(m.AverageWin), Ui.Up),
            ("Pérdida media", Money(m.AverageLoss), Ui.Down),
            ("Mayor ganancia", Money(m.LargestWin), Ui.Up),
            ("Mayor pérdida", Money(m.LargestLoss), Ui.Down),
            ("Racha ganadora", $"{m.MaxConsecutiveWins}", null),
            ("Racha perdedora", $"{m.MaxConsecutiveLosses}", null),
            ("Sharpe", m.SharpeRatio.ToString("0.00", Es), null),
            ("Sortino", m.SortinoRatio.ToString("0.00", Es), null),
            ("Recuperación", m.RecoveryFactor.ToString("0.00", Es), null),
            ("Exposición", $"{m.ExposurePercent:0.#}%", null),
            ("Largos", Money(m.LongNetProfit), m.LongNetProfit >= 0 ? Ui.Up : Ui.Down),
            ("Cortos", Money(m.ShortNetProfit), m.ShortNetProfit >= 0 ? Ui.Up : Ui.Down),
            ("Comisiones", (-m.Commission).ToString("#,##0.00", Es), Ui.Text1),
            ("Velas por op.", m.AverageBarsInTrade.ToString("0.#", Es), null),
        };
        _stats.Children.Clear();
        _stats.RowDefinitions.Clear();
        for (int i = 0; i < items.Length; i++)
        {
            int row = i / 2, col = i % 2 * 3;
            if (col == 0) _stats.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = new TextBlock
            {
                Text = items[i].Label, Classes = { "muted", "small" }, Margin = new Thickness(0, 3, 8, 3), VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            ToolTip.SetTip(label, items[i].Label);
            var value = new TextBlock { Text = items[i].Value, FontSize = 12.5, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 3), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            if (items[i].Color is { } c) value.Foreground = c;
            Grid.SetRow(label, row);
            Grid.SetColumn(label, col);
            Grid.SetRow(value, row);
            Grid.SetColumn(value, col + 1);
            _stats.Children.Add(label);
            _stats.Children.Add(value);
        }

        var costs = _cs.HistoryCosts;
        _costsNote.Text = costs == null ? "" :
            "Calculado con " + (costs.CommissionPerUnit > 0 ? $"{costs.CommissionPerUnit.ToString("0.##", Es)} de comisión por lado y contrato" :
                costs.CommissionPercent > 0 ? $"{costs.CommissionPercent.ToString("0.###", Es)}% de comisión por lado" : "sin comisión") +
            $" y {costs.SlippageTicks} tick(s) de deslizamiento por orden, como el backtest. Para el detalle de cada operación, el backtest en Estrategias.";
    }

    private static string Money(decimal v) => v.ToString("+#,##0.00;-#,##0.00", Es);

    private void Apply()
    {
        if (_file?.Type != null)
        {
            var s = _file.Create();
            s.SetParameters(_cs.Parameters);
            foreach (var (prop, read) in _editors)
            {
                try
                {
                    prop.SetValue(s, read());
                }
                catch (Exception)
                {
                    // valor inválido: queda el anterior
                }
            }

            _cs.Parameters = s.GetParameters();
        }

        _cs.Live = _live.IsChecked == true;
        if (_cs.Live && _account.SelectedItem is ComboBoxItem { Tag: AccountOption a }) _cs.AccountId = a.Id;
        _cs.Paused = false;
        _ = _host.RunStrategyAsync(_chart, _cs);
        Refresh();
    }
}
