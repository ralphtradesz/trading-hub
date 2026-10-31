using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;
using TradingHub.App.Controls;
using TradingHub.App.Services;
using TradingHub.App.Windows;
using TradingHub.Strategies;
using TradingHub.Trading;

namespace TradingHub.App.Views;

/// <summary>
/// Sección Cuenta: saldo, resultado del día, posiciones, órdenes, ejecuciones, operaciones cerradas y estadísticas
/// de cada cuenta (la simulada y las de Rithmic). Las filas de posiciones y órdenes nunca cambian de lugar:
/// las nuevas se agregan abajo, así un clic no cae en la fila de al lado.
/// </summary>
public sealed class AccountsView : UserControl
{
    private static readonly CultureInfo Es = TradingHub.Common.UiCulture.Current;
    private const string PositionColumns = "1.3*,0.7*,0.7*,1*,1*,1*,92";
    private const string OrderColumns = "64,1.1*,0.6*,0.9*,0.5*,0.9*,0.7*,0.9*,1.6*,92";
    private const string FillColumns = "130,1.2*,0.7*,0.7*,1*,0.8*";
    private const string TradeColumns = "130,1.1*,0.6*,0.6*,1*,1*,0.8*,1*";

    private readonly AppServices _svc;
    private readonly Action<string> _status;
    private readonly MainWindow _window;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Button _accountButton = new() { Padding = new Thickness(12, 5), MinWidth = 320, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly Flyout _accountMenu = new() { Placement = PlacementMode.BottomEdgeAlignedLeft };
    private readonly TextBlock _noRithmic = new() { Text = "Las cuentas de Rithmic aparecen al conectar Rithmic en Conexiones", Classes = { "faint", "small" }, VerticalAlignment = VerticalAlignment.Center };
    private IReadOnlyList<AccountEntry> _accounts = Array.Empty<AccountEntry>();
    private readonly TextBlock _accountStatus = new() { Classes = { "muted", "small" }, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly WrapPanel _kpis = new();
    private readonly List<(TextBlock Title, TextBlock Value, TextBlock Sub)> _cards = new();
    private readonly StackPanel _simBar;
    private readonly NumericUpDown _startBalance = new() { Width = 150, Minimum = 100, Maximum = 100_000_000, Increment = 1000, FormatString = "N0", NumberFormat = CultureInfo.InvariantCulture.NumberFormat };
    private readonly Button _restart = new() { Content = "Reiniciar la cuenta simulada" };
    private readonly TextBlock _restartHint = new() { Classes = { "faint", "small" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly TabControl _tabs = new();
    private readonly StackPanel _positions = new();
    private readonly Dictionary<string, PositionRow> _positionRows = new();
    private readonly TextBlock _positionsEmpty = new() { Text = "Sin posiciones todavía", Classes = { "muted" }, Margin = new Thickness(12, 14) };
    private readonly StackPanel _orders = new();
    private readonly Dictionary<string, OrderRow> _orderRows = new();
    private readonly TextBlock _ordersEmpty = new() { Text = "Todavía no hay órdenes", Classes = { "muted" }, Margin = new Thickness(12, 14) };
    private readonly ListBox _fills = new();
    private readonly ListBox _trades = new();
    private readonly WrapPanel _statKpis = new();
    private readonly EquityChart _equity = new() { Height = 230, EmptyText = "Todavía no hay operaciones cerradas en este período" };
    private readonly Grid _metrics = new() { ColumnDefinitions = new ColumnDefinitions("*,24,*") };
    private readonly StackPanel _days = new() { Spacing = 2 };
    private readonly WrapPanel _periodChips = new();
    private string _period = "all";
    private string _selected = AccountCenter.SimId;
    private string _chipsKey = "", _listsKey = "", _statsKey = "";
    private AccountEntry? _entry;
    private bool _syncing;

    public AccountsView(AppServices svc, Action<string> status, MainWindow window)
    {
        _svc = svc;
        _status = status;
        _window = window;
        // la cuenta del gráfico activo, si es una de las que hay
        if (window.Trading.ActiveTab?.AccountId is { } active && active.StartsWith("rithmic|", StringComparison.Ordinal)) _selected = active;

        _startBalance.Value = svc.Settings.SimStartBalance;
        _startBalance.ValueChanged += (_, _) =>
        {
            if (_syncing || _startBalance.Value is not { } v || v <= 0) return;
            svc.Settings.SimStartBalance = v;
            svc.Portfolio.BeginBalance = v;
            svc.Settings.Save();
            Refresh();
        };
        _restart.Flyout = ConfirmFlyout("La cuenta simulada vuelve al saldo inicial y su historial empieza de cero (el anterior queda guardado aparte).", "Reiniciar", RestartSim);
        _simBar = Ui.H(10, Ui.Muted("Saldo inicial"), _startBalance, _restart, _restartHint);
        _simBar.Margin = new Thickness(0, 0, 0, 12);

        Content = Build();
        _timer.Tick += (_, _) => Refresh();
        AttachedToVisualTree += (_, _) =>
        {
            _timer.Start();
            Refresh();
        };
        DetachedFromVisualTree += (_, _) => _timer.Stop();
    }

    // ===================================================================== armado

    private Control Build()
    {
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        var title = Ui.H(10, Ui.Icon(MaterialIconKind.WalletOutline, 20, Ui.Accent), Ui.H2("Cuenta"));
        DockPanel.SetDock(title, Dock.Left);
        head.Children.Add(title);
        // una sola cuenta a la vista; las demás en el menú (con muchas cuentas los botones sueltos quedaban desordenados)
        _accountButton.Flyout = _accountMenu;
        _accountMenu.Opening += (_, _) => _accountMenu.Content = BuildAccountMenu();
        ToolTip.SetTip(_accountButton, "Elegir la cuenta");
        var selector = Ui.H(12, _accountButton, _noRithmic);
        selector.Margin = new Thickness(18, 0, 0, 0);
        selector.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(selector);

        for (int i = 0; i < 6; i++)
        {
            var t = new TextBlock { Classes = { "h3" } };
            var v = new TextBlock { FontSize = 19, FontWeight = FontWeight.SemiBold };
            var sub = new TextBlock { Classes = { "faint", "small" }, TextTrimming = TextTrimming.CharacterEllipsis };
            var card = Ui.Card(Ui.V(3, t, v, sub), 14);
            card.Width = 196;
            card.Margin = new Thickness(0, 0, 10, 10);
            _kpis.Children.Add(card);
            _cards.Add((t, v, sub));
        }

        _tabs.Items.Add(new TabItem { Header = "Posiciones", Content = Table(PositionColumns, new[] { "Contrato", "Lado", "Cantidad", "Precio prom.", "PnL abierto", "Realizado hoy", "" }, new[] { 2, 3, 4, 5 }, _positions, _positionsEmpty) });
        _tabs.Items.Add(new TabItem { Header = "Órdenes", Content = Table(OrderColumns, new[] { "Hora", "Contrato", "Lado", "Tipo", "Cant.", "Precio", "Ejecutado", "Precio prom.", "Estado", "" }, new[] { 4, 5, 6, 7 }, _orders, _ordersEmpty) });
        _tabs.Items.Add(new TabItem { Header = "Ejecuciones", Content = BuildFills() });
        _tabs.Items.Add(new TabItem { Header = "Operaciones", Content = BuildTrades() });
        _tabs.Items.Add(new TabItem { Header = "Estadísticas", Content = BuildStats() });
        _tabs.SelectionChanged += (_, _) => Refresh();

        var top = Ui.V(0, head, _accountStatus, _kpis, _simBar);
        _accountStatus.Margin = new Thickness(0, 0, 0, 12);
        var root = new DockPanel { Margin = new Thickness(24, 18, 24, 18) };
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(_tabs);
        return root;
    }

    private static Grid HeaderRow(string columns, string[] titles, int[] right)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions(columns), Margin = new Thickness(12, 4, 12, 6) };
        for (int i = 0; i < titles.Length; i++)
        {
            var t = new TextBlock { Text = titles[i], Classes = { "h3" }, TextAlignment = right.Contains(i) ? TextAlignment.Right : TextAlignment.Left, Margin = new Thickness(0, 0, 10, 0) };
            Grid.SetColumn(t, i);
            g.Children.Add(t);
        }

        return g;
    }

    private static Control Table(string columns, string[] titles, int[] right, StackPanel rows, Control empty)
    {
        var dock = new DockPanel();
        var header = HeaderRow(columns, titles, right);
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        dock.Children.Add(new ScrollViewer { Content = Ui.V(0, rows, empty) });
        return new Border { Child = Ui.Card(dock, 8), Margin = new Thickness(0, 10, 0, 0) };
    }

    private static TextBlock Cell(Grid g, int col, bool right = false)
    {
        var tb = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextAlignment = right ? TextAlignment.Right : TextAlignment.Left, Margin = new Thickness(0, 0, 10, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(tb, col);
        g.Children.Add(tb);
        return tb;
    }

    private static Border RowBorder(Grid g) => new() { Child = g, Padding = new Thickness(12, 5), BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1) };

    private Control BuildFills()
    {
        _fills.ItemTemplate = new FuncDataTemplate<AccountFill>((f, _) =>
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions(FillColumns) };
            if (f == null) return g;
            Cell(g, 0).Text = _svc.AccountCenter.ToLocal(f.Time).ToString("dd/MM HH:mm:ss", Es);
            Cell(g, 1).Text = f.Symbol;
            var side = Cell(g, 2);
            side.Text = f.Buy ? "Compra" : "Venta";
            side.Foreground = f.Buy ? Ui.Up : Ui.Down;
            Cell(g, 3, true).Text = Qty(f.Quantity);
            Cell(g, 4, true).Text = Price(f.Price);
            var c = Cell(g, 5, true);
            c.Text = f.Commission != 0 ? Money(-f.Commission) : "—";
            c.Foreground = Ui.Text2;
            return g;
        });
        return ListCard(FillColumns, new[] { "Hora", "Contrato", "Lado", "Cant.", "Precio", "Comisión" }, new[] { 3, 4, 5 }, _fills);
    }

    private Control BuildTrades()
    {
        _trades.ItemTemplate = new FuncDataTemplate<AccountTrade>((t, _) =>
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions(TradeColumns) };
            if (t == null) return g;
            Cell(g, 0).Text = _svc.AccountCenter.ToLocal(t.CloseTime).ToString("dd/MM HH:mm:ss", Es);
            Cell(g, 1).Text = t.Symbol;
            var side = Cell(g, 2);
            side.Text = t.IsLong ? "Largo" : "Corto";
            side.Foreground = t.IsLong ? Ui.Up : Ui.Down;
            Cell(g, 3, true).Text = Qty(t.Quantity);
            Cell(g, 4, true).Text = Price(t.OpenPrice);
            Cell(g, 5, true).Text = Price(t.ClosePrice);
            var d = Cell(g, 6, true);
            d.Text = Duration(t.Duration);
            d.Foreground = Ui.Text2;
            var r = Cell(g, 7, true);
            r.Text = Money(t.NetPnl);
            r.Foreground = t.NetPnl >= 0 ? Ui.Up : Ui.Down;
            return g;
        });
        return ListCard(TradeColumns, new[] { "Cierre", "Contrato", "Lado", "Cant.", "Entrada", "Salida", "Duración", "Resultado" }, new[] { 3, 4, 5, 6, 7 }, _trades);
    }

    private static Control ListCard(string columns, string[] titles, int[] right, ListBox list)
    {
        var dock = new DockPanel();
        var header = HeaderRow(columns, titles, right);
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        dock.Children.Add(list);
        return new Border { Child = Ui.Card(dock, 8), Margin = new Thickness(0, 10, 0, 0) };
    }

    private Control BuildStats()
    {
        foreach (var (id, text) in new[] { ("today", "Hoy"), ("7", "7 días"), ("30", "30 días"), ("all", "Todo") })
        {
            var b = new ToggleButton { Content = text, Tag = id, Margin = new Thickness(0, 0, 6, 0), IsChecked = id == _period };
            b.Click += (_, _) =>
            {
                _period = id;
                foreach (var c in _periodChips.Children.OfType<ToggleButton>()) c.IsChecked = (string)c.Tag! == id;
                _statsKey = "";
                Refresh();
            };
            _periodChips.Children.Add(b);
        }

        _metrics.Margin = new Thickness(0, 4, 0, 0);
        var left = Ui.V(12, _periodChips, _statKpis, Ui.Card(_equity, 10), Ui.Card(_metrics, 12));
        var right = Ui.Card(Ui.V(8, new TextBlock { Text = "POR DÍA", Classes = { "h3" } }, _days), 12);
        right.Width = 300;
        right.Margin = new Thickness(14, 0, 0, 0);
        right.VerticalAlignment = VerticalAlignment.Top;
        var dock = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(right);
        dock.Children.Add(left);
        return new ScrollViewer { Content = dock };
    }

    // ===================================================================== acciones

    private static Flyout ConfirmFlyout(string question, string confirm, Action action) => Ui.ConfirmFlyout(question, confirm, action);

    private void RestartSim()
    {
        _svc.Journal.Restart(AccountCenter.SimId);
        _svc.Portfolio.RealizedPnL = 0;
        _svc.Portfolio.Commission = 0;
        foreach (var b in _svc.SimBrokers)
        {
            b.Position!.RealizedPnL = 0;
            b.Position.Commission = 0;
        }

        _svc.Statistics.HistoryMyTrades.Clear();
        _listsKey = _statsKey = "";
        _status($"Cuenta simulada reiniciada: saldo {_svc.Settings.SimStartBalance:N2}.");
        Refresh();
    }

    private async void Run(Func<Task> action, string doing)
    {
        try
        {
            _status(doing);
            await action();
        }
        catch (Exception ex)
        {
            _status("No se pudo: " + ex.Message);
        }

        Refresh();
    }

    private Button ActionButton(string text, string question, string confirm, Func<Task> action, string doing, bool ask)
    {
        var b = new Button { Content = text, Padding = new Thickness(8, 2), MinHeight = 24, FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        if (ask) b.Flyout = ConfirmFlyout(question, confirm, () => Run(action, doing));
        else b.Click += (_, _) => Run(action, doing);
        return b;
    }

    // ===================================================================== actualización

    private void Refresh()
    {
        if (!IsEffectivelyVisible) return;
        try
        {
            var accounts = _svc.AccountCenter.Accounts();
            var entry = accounts.FirstOrDefault(a => a.Id == _selected) ?? accounts[0];
            UpdateChips(accounts, entry.Id);
            if (_entry?.Id != entry.Id)
            {
                // otra cuenta: filas nuevas
                _positionRows.Clear();
                _positions.Children.Clear();
                _orderRows.Clear();
                _orders.Children.Clear();
                _listsKey = _statsKey = "";
            }

            _entry = entry;
            _selected = entry.Id;
            var snap = _svc.AccountCenter.Snapshot(entry);
            UpdateHeader(snap);
            UpdatePositions(snap);
            UpdateOrders(snap);
            UpdateLists(snap);
            if (_tabs.SelectedIndex == 4) UpdateStats(snap);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
    }

    private static Control AccountLabel(AccountEntry a, string text, double size = 13)
    {
        // (un Foreground nulo deja el texto invisible: solo se pone en las reales)
        var name = new TextBlock { Text = text, FontSize = size, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        if (a.IsReal) name.Foreground = Brushes.OrangeRed;
        return Ui.H(8, Ui.Icon(a.Kind == AccountKind.Sim ? MaterialIconKind.FlaskOutline : MaterialIconKind.Bank, 15, a.IsReal ? Brushes.OrangeRed : Ui.Accent), name);
    }

    private void UpdateChips(IReadOnlyList<AccountEntry> accounts, string selected)
    {
        _accounts = accounts;
        _noRithmic.IsVisible = !_svc.Connections.Enabled.Any(p => p.IsRithmic);
        string key = string.Join("|", accounts.Select(a => a.Id + a.Name)) + "#" + selected;
        if (key == _chipsKey) return;
        _chipsKey = key;
        var current = accounts.First(a => a.Id == selected);
        var row = new DockPanel();
        var chevron = Ui.Icon(MaterialIconKind.ChevronDown, 16, Ui.Text2);
        chevron.Margin = new Thickness(10, 0, 0, 0);
        DockPanel.SetDock(chevron, Dock.Right);
        row.Children.Add(chevron);
        if (accounts.Count > 1)
        {
            var count = new TextBlock { Text = $"{accounts.Count} cuentas", Classes = { "faint", "small" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            DockPanel.SetDock(count, Dock.Right);
            row.Children.Add(count);
        }

        row.Children.Add(AccountLabel(current, current.Name));
        _accountButton.Content = row;
    }

    /// <summary>Menú de cuentas, agrupadas por conexión, con la elegida marcada.</summary>
    private Control BuildAccountMenu()
    {
        var list = new StackPanel { Spacing = 1, MinWidth = 320 };
        string? group = null;
        foreach (var a in _accounts)
        {
            string g = a.Kind == AccountKind.Sim ? "TRADING HUB" : $"{(a.Profile?.Name ?? "Rithmic").ToUpperInvariant()} · {(a.IsReal ? "REAL" : a.Kind == AccountKind.Tradovate ? "DEMO" : "PRÁCTICA")}";
            if (g != group)
            {
                group = g;
                list.Children.Add(new TextBlock { Text = g, Classes = { "h3" }, Margin = new Thickness(10, list.Children.Count == 0 ? 2 : 10, 10, 4) });
            }

            bool selected = a.Id == _selected;
            var row = new DockPanel();
            if (selected)
            {
                var check = Ui.Icon(MaterialIconKind.Check, 16, Ui.Accent);
                DockPanel.SetDock(check, Dock.Right);
                row.Children.Add(check);
            }

            row.Children.Add(AccountLabel(a, a.Kind == AccountKind.Sim ? a.Name : a.RithmicAccount, 12.5));
            var item = new Button
            {
                Content = row, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = selected ? Ui.Brush("Bg3Brush") : Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(10, 7),
            };
            string id = a.Id;
            item.Click += (_, _) =>
            {
                _accountMenu.Hide();
                _selected = id;
                _chipsKey = "";
                Refresh();
            };
            list.Children.Add(item);
        }

        return new ScrollViewer { Content = list, MaxHeight = 440 };
    }

    private void Card(int i, string title, string value, string sub, IBrush? color = null)
    {
        var (t, v, s) = _cards[i];
        t.Text = title.ToUpperInvariant();
        v.Text = value;
        v.Foreground = color ?? Ui.Text1;
        s.Text = sub;
        ToolTip.SetTip(s, sub);
    }

    private static IBrush Signed(decimal v) => v > 0 ? Ui.Up : v < 0 ? Ui.Down : Ui.Text1;

    private void UpdateHeader(AccountSnapshot s)
    {
        var a = s.Account;
        _accountStatus.Text = s.Status + (a.IsReal ? " · CUENTA REAL" + (a.CanTrade ? "" : " (solo lectura: las órdenes reales están apagadas en Conexiones)") : "");
        _accountStatus.Foreground = !s.Connected ? Ui.Warn : a.IsReal ? Brushes.OrangeRed : Ui.Text2;
        int open = s.Positions.Count(p => p.Net != 0);
        int active = s.Orders.Count(o => o.Active);
        int filledToday = s.Orders.Count(o => o.Filled > 0 && _svc.AccountCenter.IsToday(o.Time));

        Card(0, "Saldo", s.Balance is { } bal ? Money(bal, false) : "—",
            a.Kind == AccountKind.Sim ? $"arrancó con {Money(s.StartBalance ?? 0, false)}" : s.Balance == null ? $"esperando a {(a.Kind == AccountKind.Tradovate ? "Tradovate" : "Rithmic")}…" : $"según {(a.Kind == AccountKind.Tradovate ? "Tradovate" : "Rithmic")}");
        Card(1, "Resultado del día", Money(s.DayPnl), $"realizado {Money(s.RealizedToday)} · abierto {Money(s.OpenPnl)}", Signed(s.DayPnl));
        Card(2, "PnL abierto", Money(s.OpenPnl), open == 0 ? "sin posiciones abiertas" : open == 1 ? "1 posición abierta" : $"{open} posiciones abiertas", Signed(s.OpenPnl));
        Card(3, "Realizado hoy", Money(s.RealizedToday), s.TradesToday == 1 ? "1 operación cerrada hoy" : $"{s.TradesToday} operaciones cerradas hoy", Signed(s.RealizedToday));
        if (a.Kind == AccountKind.Sim)
            Card(4, "Resultado total", Money(s.Analysis.RealizedNet), $"{s.Analysis.Trades.Count} operaciones desde el inicio", Signed(s.Analysis.RealizedNet));
        else
            Card(4, "Disponible para operar", s.Available is { } av ? Money(av, false) : "—", s.MarginBalance is { } mb ? $"margen {Money(mb, false)}" : "poder de compra según Rithmic");
        Card(5, "Órdenes activas", active.ToString(), $"{filledToday} ejecutada{(filledToday == 1 ? "" : "s")} hoy");

        _simBar.IsVisible = a.Kind == AccountKind.Sim;
        if (a.Kind == AccountKind.Sim)
        {
            bool busy = open > 0 || active > 0;
            _restart.IsEnabled = !busy;
            _restartHint.Text = busy ? "Para reiniciar, primero cerrá las posiciones y cancelá las órdenes" : "";
            if (!_startBalance.IsKeyboardFocusWithin && _startBalance.Value != _svc.Settings.SimStartBalance)
            {
                _syncing = true;
                _startBalance.Value = _svc.Settings.SimStartBalance;
                _syncing = false;
            }
        }
    }

    private sealed class PositionRow
    {
        public required Border Root;
        public required TextBlock Symbol, Side, Qty, Avg, Open, Closed;
        public required Button Close;
        public PositionLine? Line;
    }

    private void UpdatePositions(AccountSnapshot s)
    {
        var entry = s.Account;
        foreach (var p in s.Positions)
        {
            if (!_positionRows.TryGetValue(p.Key, out var row))
            {
                var g = new Grid { ColumnDefinitions = new ColumnDefinitions(PositionColumns) };
                PositionRow? self = null;
                var close = ActionButton("Cerrar", "", "Cerrar a mercado", () => _svc.AccountCenter.CloseAsync(entry, self!.Line!), "Cerrando la posición a mercado…", entry.IsReal);
                if (entry.IsReal && close.Flyout is Flyout f)
                    f.Opening += (_, _) => ((TextBlock)((StackPanel)f.Content!).Children[0]).Text = $"¿Cerrar {Qty(Math.Abs(self!.Line!.Net))} {self.Line.Symbol} a mercado en la cuenta REAL?";
                Grid.SetColumn(close, 6);
                g.Children.Add(close);
                row = self = new PositionRow { Root = RowBorder(g), Symbol = Cell(g, 0), Side = Cell(g, 1), Qty = Cell(g, 2, true), Avg = Cell(g, 3, true), Open = Cell(g, 4, true), Closed = Cell(g, 5, true), Close = close };
                _positionRows[p.Key] = row;
                _positions.Children.Add(row.Root);
            }

            row.Line = p;
            row.Symbol.Text = p.Symbol;
            row.Side.Text = p.Net > 0 ? "Largo" : p.Net < 0 ? "Corto" : "Plana";
            row.Side.Foreground = p.Net > 0 ? Ui.Up : p.Net < 0 ? Ui.Down : Ui.Text2;
            row.Qty.Text = p.Net == 0 ? "0" : Qty(Math.Abs(p.Net));
            row.Avg.Text = p.Net == 0 ? "—" : Price(p.AvgPrice);
            row.Open.Text = p.Net == 0 ? "—" : Money(p.OpenPnl);
            row.Open.Foreground = p.Net == 0 ? Ui.Text2 : Signed(p.OpenPnl);
            row.Closed.Text = Money(p.ClosedPnl);
            row.Closed.Foreground = Signed(p.ClosedPnl);
            row.Close.IsVisible = p.Net != 0 && entry.CanTrade;
        }

        _positionsEmpty.IsVisible = _positionRows.Count == 0;
    }

    private sealed class OrderRow
    {
        public required Border Root;
        public required TextBlock Time, Symbol, Side, Type, Qty, Price, Filled, Avg, Status;
        public required Button Cancel;
        public OrderLine? Line;
    }

    private void UpdateOrders(AccountSnapshot s)
    {
        var entry = s.Account;
        foreach (var o in s.Orders)
        {
            if (!_orderRows.TryGetValue(o.Key, out var row))
            {
                var g = new Grid { ColumnDefinitions = new ColumnDefinitions(OrderColumns) };
                OrderRow? self = null;
                var cancel = ActionButton("Cancelar", "¿Cancelar la orden en la cuenta REAL?", "Cancelar la orden", () => _svc.AccountCenter.CancelAsync(entry, self!.Line!), "Cancelando la orden…", entry.IsReal);
                Grid.SetColumn(cancel, 9);
                g.Children.Add(cancel);
                row = self = new OrderRow
                {
                    Root = RowBorder(g), Time = Cell(g, 0), Symbol = Cell(g, 1), Side = Cell(g, 2), Type = Cell(g, 3), Qty = Cell(g, 4, true),
                    Price = Cell(g, 5, true), Filled = Cell(g, 6, true), Avg = Cell(g, 7, true), Status = Cell(g, 8), Cancel = cancel,
                };
                _orderRows[o.Key] = row;
                _orders.Children.Add(row.Root);
            }

            row.Line = o;
            row.Time.Text = _svc.AccountCenter.ToLocal(o.Time).ToString("HH:mm:ss", Es);
            row.Time.Foreground = Ui.Text2;
            row.Symbol.Text = o.Symbol;
            row.Side.Text = o.Buy ? "Compra" : "Venta";
            row.Side.Foreground = o.Buy ? Ui.Up : Ui.Down;
            row.Type.Text = o.Type;
            row.Qty.Text = Qty(o.Quantity);
            row.Price.Text = o.Type == "Mercado" || o.Price == 0 ? "—" : Price(o.Price);
            row.Filled.Text = Qty(o.Filled);
            row.Avg.Text = o.AvgFill > 0 ? Price(o.AvgFill) : "—";
            row.Status.Text = o.Status;
            ToolTip.SetTip(row.Status, o.Status);
            row.Status.Foreground = o.Active ? Ui.Accent : o.Status.StartsWith("Rechazada", StringComparison.Ordinal) ? Ui.Down : o.Status == "Ejecutada" ? Ui.Text1 : Ui.Text2;
            row.Cancel.IsVisible = o.Active && entry.CanTrade;
        }

        _ordersEmpty.IsVisible = _orderRows.Count == 0;
    }

    private void UpdateLists(AccountSnapshot s)
    {
        string key = s.Account.Id + "#" + s.Fills.Count;
        if (key == _listsKey) return;
        _listsKey = key;
        _fills.ItemsSource = s.Fills.Reverse().ToList();
        _trades.ItemsSource = s.Analysis.Trades.AsEnumerable().Reverse().ToList();
    }

    private void UpdateStats(AccountSnapshot s)
    {
        string key = s.Account.Id + "#" + s.Fills.Count + "#" + _period + "#" + _svc.AccountCenter.ToLocal(DateTime.UtcNow).Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        if (key == _statsKey) return;
        _statsKey = key;

        var center = _svc.AccountCenter;
        var today = center.ToLocal(DateTime.UtcNow).Date;
        var trades = s.Analysis.Trades.Where(t => _period switch
        {
            "today" => center.ToLocal(t.CloseTime).Date == today,
            "7" => center.ToLocal(t.CloseTime).Date > today.AddDays(-7),
            "30" => center.ToLocal(t.CloseTime).Date > today.AddDays(-30),
            _ => true,
        }).ToList();

        // la curva arranca en el saldo que había al principio del período
        decimal periodNet = trades.Sum(t => t.NetPnl);
        decimal capital = s.Account.Kind == AccountKind.Sim
            ? (s.StartBalance ?? 0) + s.Analysis.Trades.Where(t => !trades.Contains(t)).Where(t => t.CloseTime < (trades.FirstOrDefault()?.CloseTime ?? DateTime.MaxValue)).Sum(t => t.NetPnl)
            : Math.Max(0, (s.Balance ?? 0) - periodNet);
        var converted = trades.Select((t, i) => new ClosedTrade(i + 1, t.IsLong, t.OpenTime, t.OpenPrice, t.CloseTime, t.ClosePrice, t.Quantity, t.Pnl, t.Commission, 0, 0, "", "", 0)).ToList();
        var equity = new List<EquityPoint>();
        decimal eq = capital, peak = capital;
        foreach (var t in trades)
        {
            eq += t.NetPnl;
            peak = Math.Max(peak, eq);
            equity.Add(new EquityPoint(t.CloseTime, eq, peak - eq));
        }

        var m = PerformanceMetrics.Compute(converted, equity, capital, false, 0, 0);
        _equity.SetData(equity, capital);

        _statKpis.Children.Clear();
        _statKpis.Children.Add(Kpi("Resultado neto", Money(m.NetProfit), $"{m.TotalTrades} operaciones", Signed(m.NetProfit)));
        _statKpis.Children.Add(Kpi("% ganadoras", m.TotalTrades > 0 ? $"{m.WinRate:0.#}%" : "—", $"{m.Winners} ganadoras · {m.Losers} perdedoras"));
        _statKpis.Children.Add(Kpi("Profit factor", m.TotalTrades > 0 ? (m.ProfitFactor >= 999 ? "∞" : m.ProfitFactor.ToString("0.00", Es)) : "—", "ganancia bruta / pérdida bruta"));
        _statKpis.Children.Add(Kpi("Drawdown máximo", Money(-m.MaxDrawdown), capital > 0 ? $"{m.MaxDrawdownPercent:0.##}% del saldo" : "desde el pico", m.MaxDrawdown > 0 ? Ui.Down : null));
        _statKpis.Children.Add(Kpi("Expectativa", Money(m.Expectancy), "resultado esperado por operación", Signed(m.Expectancy)));

        var avgDuration = trades.Count > 0 ? TimeSpan.FromTicks((long)trades.Average(t => t.Duration.Ticks)) : TimeSpan.Zero;
        var rows = new (string, string)[]
        {
            ("Resultado neto", Money(m.NetProfit)), ("Ganancia bruta", Money(m.GrossProfit)), ("Pérdida bruta", Money(-m.GrossLoss)), ("Comisiones", Money(-m.Commission)),
            ("Operaciones", m.TotalTrades.ToString(Es)), ("Ganadoras / perdedoras", $"{m.Winners} / {m.Losers}"), ("% ganadoras", $"{m.WinRate:0.##}%"), ("Profit factor", m.ProfitFactor >= 999 ? "∞" : m.ProfitFactor.ToString("0.00", Es)),
            ("Promedio por operación", Money(m.AverageTrade)), ("Ganancia promedio", Money(m.AverageWin)), ("Pérdida promedio", Money(m.AverageLoss)), ("Relación ganancia/pérdida", m.PayoffRatio.ToString("0.00", Es)),
            ("Mayor ganancia", Money(m.LargestWin)), ("Mayor pérdida", Money(m.LargestLoss)), ("Racha ganadora máx.", m.MaxConsecutiveWins.ToString(Es)), ("Racha perdedora máx.", m.MaxConsecutiveLosses.ToString(Es)),
            ("Drawdown máximo", $"{Money(-m.MaxDrawdown)} ({m.MaxDrawdownPercent:0.##}%)"), ("Factor de recuperación", m.RecoveryFactor >= 999 ? "∞" : m.RecoveryFactor.ToString("0.00", Es)),
            ("Resultado largos", $"{Money(m.LongNetProfit)} ({trades.Count(t => t.IsLong)})"), ("Resultado cortos", $"{Money(m.ShortNetProfit)} ({trades.Count(t => !t.IsLong)})"),
            ("Duración promedio", trades.Count > 0 ? Duration(avgDuration) : "—"), ("Días operados", trades.Select(t => center.ToLocal(t.CloseTime).Date).Distinct().Count().ToString(Es)),
        };
        _metrics.Children.Clear();
        _metrics.RowDefinitions.Clear();
        int half = (rows.Length + 1) / 2;
        for (int i = 0; i < half; i++) _metrics.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (int i = 0; i < rows.Length; i++)
        {
            var (label, value) = rows[i];
            var row = new DockPanel { Margin = new Thickness(4, 6) };
            var v = new TextBlock { Text = value, FontWeight = FontWeight.Medium };
            DockPanel.SetDock(v, Dock.Right);
            row.Children.Add(v);
            row.Children.Add(new TextBlock { Text = label, Classes = { "muted" } });
            var cell = new Border { Child = row, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1) };
            Grid.SetRow(cell, i % half);
            Grid.SetColumn(cell, i < half ? 0 : 2);
            _metrics.Children.Add(cell);
        }

        // resultado por día, con una barra proporcional
        _days.Children.Clear();
        var days = trades.GroupBy(t => center.ToLocal(t.CloseTime).Date).Select(g => (Day: g.Key, Count: g.Count(), Net: g.Sum(t => t.NetPnl))).OrderByDescending(d => d.Day).Take(60).ToList();
        decimal max = days.Count > 0 ? days.Max(d => Math.Abs(d.Net)) : 0;
        foreach (var d in days)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("78,*,92"), Margin = new Thickness(0, 2) };
            var date = new TextBlock { Text = d.Day.ToString("ddd dd/MM", Es), FontSize = 12, Foreground = Ui.Text2, VerticalAlignment = VerticalAlignment.Center };
            var bar = new Border { Height = 8, CornerRadius = new CornerRadius(4), Background = d.Net >= 0 ? Ui.Up : Ui.Down, HorizontalAlignment = HorizontalAlignment.Left, Width = max > 0 ? Math.Max(3, (double)(Math.Abs(d.Net) / max) * 110) : 3, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(bar, d.Count == 1 ? "1 operación" : $"{d.Count} operaciones");
            var net = new TextBlock { Text = Money(d.Net), FontSize = 12, TextAlignment = TextAlignment.Right, Foreground = Signed(d.Net), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(bar, 1);
            Grid.SetColumn(net, 2);
            g.Children.Add(date);
            g.Children.Add(bar);
            g.Children.Add(net);
            _days.Children.Add(g);
        }

        if (days.Count == 0) _days.Children.Add(new TextBlock { Text = "Sin operaciones cerradas en este período", Classes = { "muted", "small" } });
    }

    private static Border Kpi(string title, string value, string sub, IBrush? color = null)
    {
        var v = new TextBlock { Text = value, FontSize = 19, FontWeight = FontWeight.SemiBold };
        if (color != null) v.Foreground = color;
        var card = Ui.Card(Ui.V(3, new TextBlock { Text = title.ToUpperInvariant(), Classes = { "h3" } }, v, new TextBlock { Text = sub, Classes = { "faint", "small" }, TextTrimming = TextTrimming.CharacterEllipsis }), 14);
        card.Width = 178;
        card.Margin = new Thickness(0, 0, 10, 0);
        return card;
    }

    // ===================================================================== formatos

    private static string Money(decimal v, bool signed = true) => signed ? v.ToString("+#,##0.00;-#,##0.00;0.00", Es) : v.ToString("#,##0.00", Es);
    private static string Qty(decimal v) => v.ToString("0.####", Es);
    private static string Price(decimal v) => v.ToString("#,##0.#####", Es);

    private static string Duration(TimeSpan d) =>
        d.TotalSeconds < 60 ? $"{d.TotalSeconds:0} s"
        : d.TotalMinutes < 60 ? $"{(int)d.TotalMinutes} min {d.Seconds:00} s"
        : d.TotalHours < 24 ? $"{(int)d.TotalHours} h {d.Minutes:00} min"
        : $"{(int)d.TotalDays} d {d.Hours} h";
}
