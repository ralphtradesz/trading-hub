using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;
using TradingHub.App.Services;
using TradingHub.App.Views;
using TradingHub.Common.Logging;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;

namespace TradingHub.App.Windows;

/// <summary>Página de la ventana principal (Trading, Estrategias, Datos…).</summary>
internal sealed record Page(string Id, string Title, MaterialIconKind Icon, Func<Control> Create, KeyGesture? Gesture);

/// <summary>
/// Ventana principal: barra superior propia (integrada con los botones de macOS, o con los de Windows a la derecha),
/// barra lateral de navegación, páginas y barra de estado.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly AppServices _svc;
    private readonly DispatcherTimer _timer;
    private readonly Panel _host = new();
    private readonly StackPanel _toasts = new();
    private readonly Dictionary<string, Control> _pages = new();
    private readonly Dictionary<string, ToggleButton> _navButtons = new();
    private readonly List<Page> _pageDefs;
    private readonly TextBlock _statusText = new() { VerticalAlignment = VerticalAlignment.Center, Classes = { "muted", "small" }, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _infoText = new() { VerticalAlignment = VerticalAlignment.Center, Classes = { "faint", "small" } };
    private readonly TextBlock _pnlText = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 11.5, FontWeight = FontWeight.SemiBold };
    private readonly Avalonia.Controls.Shapes.Ellipse _connDot = new() { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _connText = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 11.5 };
    private DateTime _lastStatus;
    private DateTime _lastTickAt;
    private string _current = "";

    public MainWindow(AppServices svc)
    {
        _svc = svc;
        Title = TradingHub.Common.Product.Name;
        Width = Math.Max(1000, svc.Settings.WindowWidth);
        Height = Math.Max(640, svc.Settings.WindowHeight);
        MinWidth = 1000;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = AppIcon.Window;
        ExtendClientAreaToDecorationsHint = true;
        // barra de título "gruesa" de macOS (como Finder): los botones de la ventana quedan centrados en la barra,
        // a la misma altura que los de la app
        ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.PreferSystemChrome | Avalonia.Platform.ExtendClientAreaChromeHints.OSXThickTitleBar;
        ExtendClientAreaTitleBarHeightHint = TopBarHeight;

        Trading = new TradingView(svc, SetStatus);
        _pages["trading"] = Trading;
        svc.Live.Trading = () => Trading;
        svc.Live.Notice += text => Dispatcher.UIThread.Post(() => SetStatus(text));
        _pageDefs = new List<Page>
        {
            new("trading", "Trading", MaterialIconKind.ChartBoxOutline, () => Trading, new KeyGesture(Key.D1, Keys.Command)),
            new("accounts", "Cuenta", MaterialIconKind.WalletOutline, () => new AccountsView(svc, SetStatus, this), new KeyGesture(Key.D5, Keys.Command)),
            new("strategies", "Backtesting", MaterialIconKind.FlaskOutline, () => new StrategiesView(svc, SetStatus), new KeyGesture(Key.D2, Keys.Command)),
            new("live", "En vivo", MaterialIconKind.AccessPoint, () => new LiveView(svc, SetStatus, this), new KeyGesture(Key.D7, Keys.Command)),
            new("data", "Datos", MaterialIconKind.DatabaseOutline, () => new DataView(svc, SetStatus, this), new KeyGesture(Key.D3, Keys.Command)),
            new("connections", "Conexiones", MaterialIconKind.LanConnect, () => new ConnectionsView(svc, SetStatus), new KeyGesture(Key.D4, Keys.Command)),
            new("settings", "Ajustes", MaterialIconKind.CogOutline, () => new SettingsView(svc, SetStatus), new KeyGesture(Key.OemComma, Keys.Command)),
        };

        Content = BuildLayout();
        BuildMenu();
        if (Os.IsWindows) SetUpWindows();
        Navigate("trading");

        _svc.Scripts = new UserScripts(_svc.Catalog);
        _svc.Scripts.Compiled += r => SetStatus(r.Success
            ? $"Indicadores propios compilados en {r.Elapsed.TotalMilliseconds:0} ms"
            : $"Error compilando indicadores propios: {r.Diagnostics.FirstOrDefault(d => d.Severity == "error")}");
        _svc.Scripts.CompileAll();

        Log.Entry += e =>
        {
            if (e.Level >= LogLevel.Warning) Dispatcher.UIThread.Post(() => SetStatus("⚠ " + e.Message));
        };

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => Tick());
        _timer.Start();

        Opened += async (_, _) =>
        {
            FitToScreen();
            await Trading.RestoreOrDefaultAsync();
            // y las tuyas en vivo, si así está en Ajustes (unos segundos después: las conexiones ya arrancaron)
            _ = Task.Delay(TimeSpan.FromSeconds(8)).ContinueWith(_ => Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    await _svc.Live.ResumeAtStartAsync();
                }
                catch (Exception ex)
                {
                    CrashLog.Write(ex);
                }
            }), TaskScheduler.Default);
        };

        // atajos de teclado configurables (solo con la sección Trading a la vista)
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (_current != "trading" || e.Handled) return;
            if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && Trading.ActiveTab?.Model?.FocusTrade != null)
            {
                Trading.ClearFocusTrade(Trading.ActiveTab);
                e.Handled = true;
                return;
            }

            if (Hotkeys.Match(_svc.Settings, e, this) is { } action && Trading.ExecuteHotkey(action)) e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Closing += (_, e) =>
        {
            Workspace.Save(Trading);
            if (WindowState == WindowState.Normal)
            {
                _svc.Settings.WindowWidth = Width;
                _svc.Settings.WindowHeight = Height;
            }

            _svc.Settings.Save();
            // la cruz roja no cierra Trading Hub: queda en la barra de menú, con las estrategias y los agentes trabajando
            // (⌘Q o «Salir de Trading Hub» del panel sí la cierran del todo). En Windows, igual con la bandeja y Alt+F4.
            if (e.CloseReason == WindowCloseReason.WindowClosing && _svc.Settings.KeepInMenuBar && MenuBar.Current != null)
            {
                e.Cancel = true;
                Hide();
                return;
            }

            Trading.CloseFloating();
        };
    }

    private int _liveSaveTick;

    /// <summary>La conexión de datos en una línea (la del botón de arriba a la derecha).</summary>
    internal string ConnectionSummary => _connText.Text ?? "";

    internal AppServices Services => _svc;
    internal TradingView Trading { get; }

    /// <summary>Una sección ya abierta (o null).</summary>
    internal Control? Page(string id) => _pages.GetValueOrDefault(id);

    // ===================================================================== layout

    private Control BuildLayout()
    {
        var root = new DockPanel { Background = Ui.Bg0 };

        var top = BuildTopBar();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        var status = BuildStatusBar();
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(status);

        // los avisos flotan arriba a la derecha, sobre la página
        _toasts.HorizontalAlignment = HorizontalAlignment.Right;
        _toasts.VerticalAlignment = VerticalAlignment.Top;
        _toasts.Margin = new Thickness(0, 12, 16, 0);
        _toasts.Width = 430;
        _toasts.Spacing = 10;
        root.Children.Add(new Panel { Children = { _host, _toasts } });
        return root;
    }

    /// <summary>Alto de la barra de arriba: el de la barra de título gruesa de macOS (los botones de la ventana van centrados ahí).</summary>
    /// <summary>
    /// En Windows, una ventana más grande que la pantalla (una notebook de 1366×768, o la escala de Windows al 150 %)
    /// arranca con la barra de arriba afuera y no se puede mover: se achica a lo que entra, centrada, y si ni el tamaño
    /// mínimo entra, arranca maximizada. macOS ya la acomoda solo.
    /// </summary>
    private void FitToScreen()
    {
        if (!Os.IsWindows || WindowState != WindowState.Normal || Screens.ScreenFromWindow(this) is not { } screen) return;
        var area = screen.WorkingArea;
        double scale = screen.Scaling > 0 ? screen.Scaling : 1;
        double w = area.Width / scale, h = area.Height / scale;
        if (Width <= w && Height <= h && Position.Y >= area.Y) return;
        if (w < MinWidth || h < MinHeight)
        {
            WindowState = WindowState.Maximized;
            return;
        }

        Width = Math.Min(Width, w);
        Height = Math.Min(Height, h);
        Position = new PixelPoint(area.X + (int)((area.Width - Width * scale) / 2), area.Y + (int)((area.Height - Height * scale) / 2));
    }

    private const double TopBarHeight = 52;

    /// <summary>
    /// Ancho de los botones de la ventana en Windows: los dibuja Avalonia (tema Fluent) arriba a la derecha, tres de 45
    /// con 2 de separación.
    /// </summary>
    private const double WindowsCaptionButtonsWidth = 3 * 45 + 2 * 2;

    private Control BuildTopBar()
    {

        // navegación: integrada en la barra de título para no ocupar ancho
        var nav = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var labels = new List<TextBlock>();
        foreach (var p in _pageDefs)
        {
            var label = new TextBlock { Text = p.Title, VerticalAlignment = VerticalAlignment.Center };
            labels.Add(label);
            var b = new ToggleButton { Content = Ui.H(7, Ui.Icon(p.Icon, 16), label) }.Classes("nav");
            ToolTip.SetTip(b, p.Gesture != null ? $"{p.Title} ({GestureText(p.Gesture)})" : p.Title);
            b.Click += (_, _) => Navigate(p.Id);
            _navButtons[p.Id] = b;
            nav.Children.Add(b);
        }

        // pegada a la izquierda: deja solo el lugar de los botones de la ventana (en pantalla completa no hay)
        var left = Ui.H(18, nav);
        void PlaceNav() => left.Margin = new Thickness(OperatingSystem.IsMacOS() && WindowState != WindowState.FullScreen ? 82 : 10, 0, 0, 0);
        PlaceNav();
        // con la ventana angosta la barra queda solo con íconos (el nombre sigue en el tooltip)
        SizeChanged += (_, e) =>
        {
            bool compact = e.NewSize.Width < 1360;
            foreach (var l in labels) l.IsVisible = !compact;
        };

        var conn = new Border { Child = Ui.H(8, _connDot, _connText), Classes = { "pill" }, Padding = new Thickness(11, 4), VerticalAlignment = VerticalAlignment.Center, Cursor = new Cursor(StandardCursorType.Hand) };
        var reconnect = new MenuItem { Header = "Reconectar ahora" };
        reconnect.Click += (_, _) =>
        {
            _svc.Hub.ReconnectAll();
            SetStatus("Reconectando… lo que se perdió durante el corte se rellena solo.");
        };
        var toConnections = new MenuItem { Header = "Conexiones…" };
        toConnections.Click += (_, _) => Navigate("connections");
        var connMenu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        connMenu.Items.Add(reconnect);
        connMenu.Items.Add(toConnections);
        conn.PointerPressed += (_, e) =>
        {
            connMenu.ShowAt(conn);
            e.Handled = true;
        };
        ToolTip.SetTip(conn, "Conexión del gráfico activo (clic: reconectar o ir a Conexiones)");
        // el resultado de la simulada lleva a la sección Cuenta
        var pnl = new Border { Child = _pnlText, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(pnl, "Ver la cuenta: saldo, órdenes, operaciones y estadísticas");
        pnl.PointerPressed += (_, _) => Navigate("accounts");
        // modo claro / oscuro
        var themeButton = new Button { Classes = { "icon" }, VerticalAlignment = VerticalAlignment.Center };
        void ThemeIcon()
        {
            themeButton.Content = Ui.Icon(AppTheme.IsLight ? MaterialIconKind.WeatherNight : MaterialIconKind.WhiteBalanceSunny, 16);
            ToolTip.SetTip(themeButton, AppTheme.IsLight ? "Pasar a modo oscuro" : "Pasar a modo claro");
        }

        ThemeIcon();
        themeButton.Click += (_, _) =>
        {
            _svc.Settings.ThemeMode = AppTheme.IsLight ? AppTheme.Dark : AppTheme.Light;
            _svc.Settings.Save();
            AppTheme.Apply(_svc.Settings.ThemeMode);
        };
        AppTheme.Changed += () =>
        {
            ThemeIcon();
            Trading.OnThemeChanged();
            InvalidateVisual();
        };
        var right = Ui.H(12, pnl, themeButton, conn);
        // en Windows los botones de la ventana (minimizar, maximizar y cerrar) van arriba a la derecha: se les deja su lugar
        void PlaceRight() => right.Margin = new Thickness(0, 0, 14 + (Os.IsWindows && WindowState != WindowState.FullScreen ? WindowsCaptionButtonsWidth : 0), 0);
        PlaceRight();
        PropertyChanged += (_, e) =>
        {
            if (e.Property != WindowStateProperty) return;
            PlaceNav();
            PlaceRight();
        };

        var bar = new DockPanel { Height = TopBarHeight, LastChildFill = false };
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(right);
        bar.Children.Add(left);
        var border = new Border { Child = bar, Background = Ui.Bg1, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1) };

        // arrastrar la ventana desde la barra y doble clic para maximizar
        border.PointerPressed += (_, e) =>
        {
            if (e.Handled || e.Source is not (Border or DockPanel or StackPanel) || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            else BeginMoveDrag(e);
        };
        return border;
    }

    private static string GestureText(KeyGesture g)
    {
        string key = g.Key switch
        {
            >= Key.D0 and <= Key.D9 => ((int)(g.Key - Key.D0)).ToString(),
            Key.OemComma => ",",
            _ => g.Key.ToString(),
        };
        return Keys.Format(g.KeyModifiers, key, mac: !Os.IsWindows);
    }

    private Control BuildStatusBar()
    {
        var bar = new DockPanel { Margin = new Thickness(14, 0), Height = 26 };
        DockPanel.SetDock(_infoText, Dock.Right);
        bar.Children.Add(_infoText);
        bar.Children.Add(_statusText);
        return new Border { Child = bar, Background = Ui.Bg1, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 0, 0) };
    }

    // ===================================================================== navegación

    internal void Navigate(string id)
    {
        var def = _pageDefs.FirstOrDefault(p => p.Id == id);
        if (def == null) return;
        if (!_pages.TryGetValue(id, out var page))
        {
            try
            {
                page = def.Create();
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex);
                page = new TextBlock { Text = "No se pudo abrir la página: " + ex.Message, Margin = new Thickness(24) };
            }

            _pages[id] = page;
        }

        if (!_host.Children.Contains(page)) _host.Children.Add(page);
        foreach (var c in _host.Children) c.IsVisible = ReferenceEquals(c, page);
        foreach (var (pid, b) in _navButtons) b.IsChecked = pid == id;
        _current = id;
        Title = id == "trading" ? TradingHub.Common.Product.Name : $"{TradingHub.Common.Product.Name} — {def.Title}";
        (page as IPage)?.OnShown();
    }

    // ===================================================================== menú nativo

    private void BuildMenu()
    {
        var menu = new NativeMenu();

        var file = new NativeMenuItem("Archivo") { Menu = new NativeMenu() };
        file.Menu.Add(Item("Nuevo gráfico", () =>
        {
            Navigate("trading");
            _ = Trading.NewChartAsync();
        }, new KeyGesture(Key.T, Keys.Command)));
        file.Menu.Add(Item("Cerrar gráfico", Trading.CloseActiveTab, new KeyGesture(Key.W, Keys.Command)));
        file.Menu.Add(Item("Buscar instrumento…", () =>
        {
            Navigate("trading");
            Trading.Picker.Open();
        }, new KeyGesture(Key.K, Keys.Command)));
        file.Menu.Add(new NativeMenuItemSeparator());
        file.Menu.Add(Item("Guardar espacio de trabajo", () =>
        {
            Workspace.Save(Trading);
            SetStatus("Espacio de trabajo guardado");
        }, new KeyGesture(Key.S, Keys.Command)));
        file.Menu.Add(Item("Abrir carpeta de datos", () => Shell.OpenFolder(AppPaths.Root)));
        menu.Add(file);

        var chart = new NativeMenuItem("Gráfico") { Menu = new NativeMenu() };
        chart.Menu.Add(Item("Indicadores…", Trading.OpenIndicators, new KeyGesture(Key.I, Keys.Command)));
        chart.Menu.Add(Item("Editar footprint…", Trading.OpenFootprintEditor, new KeyGesture(Key.F, Keys.Command | KeyModifiers.Shift)));
        chart.Menu.Add(Item("Escala automática", () =>
        {
            if (Trading.ActiveTab?.Model is { } m)
            {
                m.Viewport.AutoScale = true;
                m.Invalidate();
            }
        }, new KeyGesture(Key.A, Keys.Command | KeyModifiers.Alt)));
        chart.Menu.Add(Item("Ir al último precio", () =>
        {
            if (Trading.ActiveTab?.Model is { } m)
            {
                m.Viewport.ScrollBars = 0;
                m.Invalidate();
            }
        }, new KeyGesture(Key.End, Keys.Command)));
        chart.Menu.Add(new NativeMenuItemSeparator());
        var layouts = new NativeMenuItem("Diseño") { Menu = new NativeMenu() };
        foreach (var (layout, label, _) in TradingView.Layouts)
            layouts.Menu.Add(Item(label, () =>
            {
                Navigate("trading");
                _ = Trading.SetLayoutAsync(layout);
            }));
        chart.Menu.Add(layouts);
        chart.Menu.Add(Item("Sacar gráfico a una ventana", () =>
        {
            Navigate("trading");
            Trading.DetachActive();
        }, new KeyGesture(Key.D, Keys.Command | KeyModifiers.Shift)));
        chart.Menu.Add(new NativeMenuItemSeparator());
        var panels = new NativeMenuItem("Paneles") { Menu = new NativeMenu() };
        foreach (var (key, label) in new[] { ("side", "Panel derecho"), ("ticket", "Ticket"), ("dom", "DOM"), ("tape", "Cinta de operaciones") })
            panels.Menu.Add(Item($"Mostrar u ocultar: {label}", () => Trading.TogglePanel(key)));
        chart.Menu.Add(panels);
        chart.Menu.Add(Item("Configurar DOM…", () =>
        {
            Navigate("trading");
            Trading.ShowDomSettings();
        }));
        menu.Add(chart);

        var go = new NativeMenuItem("Ir") { Menu = new NativeMenu() };
        foreach (var p in _pageDefs) go.Menu.Add(Item(p.Title, () => Navigate(p.Id), p.Gesture));
        menu.Add(go);

        var code = new NativeMenuItem("Código") { Menu = new NativeMenu() };
        code.Menu.Add(Item("Editor de indicadores…", () => new CodeEditorWindow(_svc, CodeKind.Indicator).Show(this), new KeyGesture(Key.E, Keys.Command | KeyModifiers.Shift)));
        code.Menu.Add(Item("Abrir carpeta de indicadores", () => Shell.OpenFolder(AppPaths.UserIndicators)));
        code.Menu.Add(Item("Recompilar indicadores", () => _svc.Scripts?.CompileAll()));
        code.Menu.Add(new NativeMenuItemSeparator());
        code.Menu.Add(Item("Backtesting de estrategias…", () => Navigate("strategies")));
        code.Menu.Add(Item("Estrategias en vivo…", () => Navigate("live")));
        code.Menu.Add(Item("Abrir carpeta de estrategias", () => Shell.OpenFolder(AppPaths.Strategies)));
        menu.Add(code);

        NativeMenu.SetMenu(this, menu);
    }

    /// <summary>Los atajos del menú (en Windows no hay barra de menú: se registran en la ventana).</summary>
    private readonly List<(KeyGesture Gesture, Action Run)> _menuShortcuts = new();

    private NativeMenuItem Item(string header, Action onClick, KeyGesture? gesture = null)
    {
        var item = new NativeMenuItem(header);
        if (gesture != null)
        {
            item.Gesture = gesture;
            _menuShortcuts.Add((gesture, onClick));
        }

        item.Click += (_, _) => onClick();
        return item;
    }

    // ===================================================================== Windows

    /// <summary>
    /// Windows no muestra el menú de la app (es de la barra de menú de la Mac): sus atajos, con Ctrl, quedan en la ventana.
    /// Alt+F4 sale de Trading Hub del todo, como ⌘Q en la Mac (la cruz solo la manda a la bandeja del sistema, si está
    /// activado en Ajustes). Maximizada, Windows deja el borde de la ventana fuera de la pantalla: el contenido se corre
    /// para que la barra de arriba no quede cortada.
    /// </summary>
    private void SetUpWindows()
    {
        foreach (var (gesture, run) in _menuShortcuts)
            KeyBindings.Add(new KeyBinding { Gesture = gesture, Command = new Relay(run) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.F4, KeyModifiers.Alt), Command = new Relay(MenuBar.Quit) });
        PropertyChanged += (_, e) =>
        {
            if (e.Property == OffScreenMarginProperty && Content is Control root) root.Margin = OffScreenMargin;
        };
    }

    // ===================================================================== estado

    internal void SetStatus(string text)
    {
        _statusText.Text = text;
        ToolTip.SetTip(_statusText, text);
    }

    internal void Tick()
    {
        try
        {
            // si el reloj saltó (la Mac durmió) las conexiones quedaron muertas: se reconecta al toque
            var now = DateTime.UtcNow;
            if (_lastTickAt != default && (now - _lastTickAt).TotalSeconds > 20)
            {
                _svc.Hub.ReconnectAll();
                SetStatus($"Volvió después de {(now - _lastTickAt).TotalMinutes:0} min en pausa: reconectando y rellenando lo que faltó.");
            }

            _lastTickAt = now;
            _svc.Hub.PumpAll();
            // los gráficos en ventanas aparte se actualizan siempre; los de la principal, más lento si no se ven
            Trading.Tick(_current == "trading" && IsVisible);
            if ((DateTime.UtcNow - _lastStatus).TotalMilliseconds > 250)
            {
                _lastStatus = DateTime.UtcNow;
                UpdateStatusBar();
                if (++_liveSaveTick % 12 == 0) _svc.SaveLiveState(); // cada ~3 s
                if (_liveSaveTick % 4 == 0) _svc.Live.Tick();
                if (_liveSaveTick % 20 == 0) // cada ~5 s: modo sin dormir según lo que esté corriendo
                    _svc.Sleep.Update(_svc.Settings.KeepAwake, _svc.Settings.KeepAwakeLidClosed, _svc.Settings.LidClosedOnlyOnPower, _svc.AwakeReason());
                foreach (var p in _pages.Values.OfType<IPage>()) p.OnTimer();
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
    }


    private static Border Toast(Control content) => new()
    {
        Child = content,
        Background = Ui.Bg1,
        BorderBrush = Ui.Warn,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(14, 12),
        BoxShadow = BoxShadows.Parse("0 6 24 0 #40000000"),
    };

    private void UpdateStatusBar()
    {
        var m = Trading.ActiveTab?.Model;
        if (m != null)
        {
            var d = m.Data;
            _connDot.Fill = d.Status switch
            {
                FeedStatus.Connected => Ui.Up,
                FeedStatus.Connecting or FeedStatus.Reconnecting => Ui.Warn,
                _ => Ui.Down,
            };
            string feedName = _svc.Connections.Find(Trading.ActiveTab!.FeedId)?.Name ?? d.Feed.DisplayName;
            string state = StatusName(d.Status);
            var quiet = DateTime.UtcNow - d.LastDataAt;
            string? tip = null;
            bool quietLong = d.LastDataAt.Year < 2000 || quiet.TotalSeconds > 60;
            if (d.Status == FeedStatus.Connected && d.Feed.IsLive && quietLong
                && (d.MarketClosed || (d.Feed is ISessionAwareFeed && FuturesSessions.IsGlobexClosed(DateTime.UtcNow))))
            {
                // futuros fuera de horario: no es un corte, el mercado está cerrado
                var tz = m.Settings.TimeZone;
                var open = FuturesSessions.NextGlobexOpen(DateTime.UtcNow);
                var openLocal = TimeZoneInfo.ConvertTimeFromUtc(open, tz);
                state = open > DateTime.UtcNow ? $"mercado cerrado · abre {Day(openLocal, tz)} {openLocal:HH:mm}" : "mercado cerrado";
                tip = d.ClosedAt is { } last
                    ? $"Última operación: {Day(TimeZoneInfo.ConvertTimeFromUtc(last, tz), tz)} {TimeZoneInfo.ConvertTimeFromUtc(last, tz):HH:mm}. La conexión sigue activa."
                    : "La conexión sigue activa; los datos vuelven solos cuando abre el mercado.";
                _connDot.Fill = Ui.Text2;
            }
            else if (d.Status == FeedStatus.Connected && d.Feed.IsLive && d.LastDataAt.Year > 2000 && quiet.TotalSeconds > 60)
            {
                state = $"sin datos hace {(quiet.TotalMinutes < 60 ? $"{quiet.TotalMinutes:0} min" : $"{quiet.TotalHours:0} h")}";
                _connDot.Fill = Ui.Warn;
            }
            else if (d.Status == FeedStatus.Reconnecting && d.StatusMessage is { } rm
                     && System.Text.RegularExpressions.Regex.Match(rm, @"intento (\d+)") is { Success: true } attempt)
            {
                state = $"reconectando… (intento {attempt.Groups[1].Value})";
            }

            _connText.Text = $"{feedName} · {state}";
            ToolTip.SetTip(_connText, tip ?? d.StatusMessage);
            _infoText.Text = d.IsLoadingHistory || d.IsExtending
                ? $"Cargando historia {d.HistoryProgress * 100:0}%"
                : $"{d.Trades.Count:N0} operaciones · {m.Series.Count:N0} velas · desde {TimeZoneInfo.ConvertTimeFromUtc(d.HistoryFrom, m.Settings.TimeZone):dd/MM HH:mm}";
        }

        // resultado del día y saldo de la cuenta simulada (guardados: no vuelven a cero al cerrar Trading Hub)
        var (day, balance) = _svc.AccountCenter.SimSummary();
        _pnlText.Foreground = day >= 0 ? Ui.Up : Ui.Down;
        _pnlText.Text = $"Simulada  {day:+0.00;-0.00;0.00}  ·  {balance:N2}";
        Trading.UpdatePosition();
    }

    private static string Day(DateTime local, TimeZoneInfo tz)
    {
        var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz).Date;
        if (local.Date == today) return "hoy";
        if (local.Date == today.AddDays(1)) return "mañana";
        return local.ToString("ddd", TradingHub.Common.UiCulture.Current).TrimEnd('.');
    }

    private static string StatusName(FeedStatus s) => s switch
    {
        FeedStatus.Connected => "conectado",
        FeedStatus.Connecting => "conectando…",
        FeedStatus.Reconnecting => "reconectando…",
        FeedStatus.Error => "error",
        _ => "desconectado",
    };
}

/// <summary>Ícono de la app (Assets/icon.png).</summary>
internal static class AppIcon
{
    private static Avalonia.Media.Imaging.Bitmap? _bitmap;

    public static Avalonia.Media.Imaging.Bitmap? Bitmap
    {
        get
        {
            if (_bitmap != null) return _bitmap;
            try
            {
                using var s = Avalonia.Platform.AssetLoader.Open(new Uri("avares://TradingHub/Assets/icon.png"));
                _bitmap = new Avalonia.Media.Imaging.Bitmap(s);
            }
            catch
            {
                // sin ícono
            }

            return _bitmap;
        }
    }

    public static WindowIcon? Window => Bitmap is { } b ? new WindowIcon(b) : null;
}

/// <summary>Una página que quiere enterarse cuando se muestra o refrescarse periódicamente.</summary>
internal interface IPage
{
    void OnShown();
    void OnTimer();
}

/// <summary>
/// Utilidades para abrir carpetas y archivos con el sistema: con «open» en la Mac y, en Windows, con el programa que
/// tenga asociado (UseShellExecute) o con el Explorador de archivos.
/// </summary>
internal static class Shell
{
    public static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            if (OperatingSystem.IsWindows()) StartWithShell(path);
            else System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("open", $"\"{path}\"") { UseShellExecute = false });
        }
        catch
        {
            // ignorar
        }
    }

    /// <summary>Abre una dirección web en el navegador.</summary>
    public static void OpenUrl(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                StartWithShell(url);
                return;
            }

            var psi = new System.Diagnostics.ProcessStartInfo("open") { UseShellExecute = false };
            psi.ArgumentList.Add(url);
            System.Diagnostics.Process.Start(psi);
        }
        catch
        {
            // ignorar
        }
    }

    /// <summary>Muestra el archivo seleccionado en el Finder (en Windows, en el Explorador de archivos).</summary>
    public static void Reveal(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", RevealArguments(Path.GetFullPath(path))) { UseShellExecute = false })?.Dispose();
                return;
            }

            var psi = new System.Diagnostics.ProcessStartInfo("open") { UseShellExecute = false };
            psi.ArgumentList.Add("-R");
            psi.ArgumentList.Add(path);
            System.Diagnostics.Process.Start(psi);
        }
        catch
        {
            // ignorar
        }
    }

    public static void Open(string pathOrUrl)
    {
        try
        {
            if (OperatingSystem.IsWindows()) StartWithShell(pathOrUrl);
            else System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("open", $"\"{pathOrUrl}\"") { UseShellExecute = false });
        }
        catch
        {
            // ignorar
        }
    }

    /// <summary>
    /// Lo que entiende explorer.exe para abrir la carpeta con el archivo marcado: «/select,"C:\ruta\archivo"» (pegado a la
    /// coma y entre comillas; en Windows una ruta no puede tener comillas).
    /// </summary>
    internal static string RevealArguments(string fullPath) => $"/select,\"{fullPath}\"";

    /// <summary>Windows: lo abre con el programa asociado (carpetas en el Explorador, direcciones en el navegador).</summary>
    private static void StartWithShell(string target) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();

    /// <summary>Windows: las opciones de energía donde se elige qué hace la PC al cerrar la tapa.</summary>
    public static void OpenWindowsPowerOptions()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("control.exe", "/name Microsoft.PowerOptions /page pageGlobalSettings") { UseShellExecute = true })?.Dispose();
        }
        catch
        {
            // ignorar
        }
    }
}
