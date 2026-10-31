using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;
using TradingHub.App.Controls;
using TradingHub.App.Services;
using TradingHub.App.Windows;
using TradingHub.Charting;
using TradingHub.Charting.Drawings;
using TradingHub.Charting.Rendering;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;
using TradingHub.Trading;

namespace TradingHub.App.Views;

/// <summary>Un gráfico: vive en una pestaña de la ventana principal o en una ventana aparte.</summary>
public sealed class ChartTab
{
    public required ChartView View { get; init; }

    /// <summary>Marco del gráfico (encabezado + gráfico); es lo que se mueve entre pestañas y ventanas.</summary>
    public required Border Frame { get; init; }

    /// <summary>Encabezado del marco (se ve en los diseños de varios gráficos).</summary>
    public required Control Header { get; init; }

    public required TextBlock Title { get; init; }
    public ChartModel? Model { get; set; }
    public string FeedId { get; set; } = SimulatedFeed.FeedId;
    public string Symbol { get; set; } = "SIM-BTC";
    public string AccountId { get; set; } = "sim";
    public double HistoryHours { get; set; }

    /// <summary>Grupo de vinculación por color (0 = sin vincular).</summary>
    public int LinkGroup { get; set; }

    internal LinkButton? LinkButton { get; set; }

    /// <summary>Cantidad para operar desde este gráfico (Chart Trader, DOM y atajos cuando es el activo).</summary>
    public decimal Quantity { get; set; }

    /// <summary>Cross trading: instrumento donde se ejecutan las órdenes de este gráfico (null = el del gráfico).</summary>
    public string? CrossSymbol { get; set; }

    internal TradingHub.Data.InstrumentData? CrossData { get; set; }

    /// <summary>Si el Chart Trader de este gráfico está desplegado.</summary>
    public bool TraderVisible { get; set; } = true;

    internal ChartTraderPanel? Trader { get; set; }

    /// <summary>Barra de dibujos propia del gráfico (a la izquierda).</summary>
    internal DrawingToolStrip? Tools { get; set; }

    internal ChartPage? Page { get; set; }
    internal ChartWindow? Window { get; set; }

    /// <summary>Botón de agrandar / achicar de la barra del marco.</summary>
    internal Avalonia.Controls.Button? MaximizeButton { get; set; }

    /// <summary>Se llama al cerrar el gráfico (por ejemplo, para cerrar el feed de un replay).</summary>
    internal Action? OnClosed { get; set; }

    public bool IsReplay => Model?.Data.Feed is ReplayFeed;

    public string Caption => Model == null ? Symbol : $"{(IsReplay ? "▶ " : "")}{Symbol} · {Model.Period.DisplayName}";

    /// <summary>Estrategias puestas en este gráfico (como indicadores: visuales o ejecutando en una cuenta).</summary>
    public List<ChartStrategy> Strategies { get; } = new();

    /// <summary>Qué ejecuciones están marcadas en el gráfico (cuenta, instrumento y cantidad) para no rehacerlas.</summary>
    public string MarksKey { get; set; } = "";
}

/// <summary>Cómo se reparten los gráficos dentro de una pestaña.</summary>
public enum PaneLayout
{
    One,
    TwoColumns,
    TwoRows,
    ThreeColumns,
    OneLeftTwoRight,
    OneTopTwoBottom,
    Four,
    Six,
}

/// <summary>Una pestaña de la ventana principal: uno o varios gráficos con un diseño.</summary>
internal sealed class ChartPage
{
    public required TabItem Item { get; init; }
    public required TextBlock HeaderText { get; init; }
    public Grid Host { get; } = new() { Background = Ui.Bg0, Margin = new Thickness(6) };
    public PaneLayout Layout { get; set; } = PaneLayout.One;
    public List<ChartTab> Charts { get; } = new();
    public ChartTab? Active { get; set; }

    /// <summary>Gráfico agrandado: ocupa toda la pestaña hasta que lo achicás (los otros siguen ahí).</summary>
    public ChartTab? Maximized { get; set; }
    public int VisibleCount => Maximized != null ? 1 : Math.Min(Charts.Count, TradingView.PaneCount(Layout));
}

/// <summary>Espacio de trabajo de trading: pestañas con uno o varios gráficos, DOM, ticket y cinta.</summary>
public sealed partial class TradingView : UserControl
{
    private readonly AppServices _svc;
    private readonly Action<string> _status;
    private readonly List<ChartPage> _pages = new();
    private readonly List<ChartWindow> _floating = new();
    private readonly TabControl _tabControl = new();
    private readonly ChartToolbar _toolbar;
    private readonly ChoiceButton<PaneLayout> _layoutChoice;
    private ChartPage? _page;
    private ChartTab? _active;
    private bool _switching;

    internal static readonly (PaneLayout Layout, string Label, string Short)[] Layouts =
    {
        (PaneLayout.One, "Un gráfico", "1"),
        (PaneLayout.TwoColumns, "Dos, uno al lado del otro", "2 ‖"),
        (PaneLayout.TwoRows, "Dos, uno arriba del otro", "2 ═"),
        (PaneLayout.ThreeColumns, "Tres en columnas", "3 ‖"),
        (PaneLayout.OneLeftTwoRight, "Uno grande y dos a la derecha", "1+2"),
        (PaneLayout.OneTopTwoBottom, "Uno grande arriba y dos abajo", "1+2 ═"),
        (PaneLayout.Four, "Cuatro (2 × 2)", "4"),
        (PaneLayout.Six, "Seis (3 × 2)", "6"),
    };

    public TradingView(AppServices svc, Action<string> status)
    {
        _svc = svc;
        _status = status;
        _layoutChoice = new ChoiceButton<PaneLayout>(MaterialIconKind.ViewGridOutline, Layouts.Select(l => (l.Layout, l.Label)),
            "Diseño: cuántos gráficos ver a la vez en esta pestaña", l => Layouts.First(x => x.Layout == l).Short);
        _layoutChoice.Changed += l => _ = SetLayoutAsync(l);
        var popOut = Ui.IconButton(MaterialIconKind.OpenInNew, $"Sacar el gráfico a una ventana aparte ({Keys.Label("D", shift: true)})", () => DetachActive());
        var newTab = Ui.IconButton(MaterialIconKind.Plus, $"Nueva pestaña ({Keys.Label("T")})", () => _ = NewChartAsync());
        _toolbar = new ChartToolbar(this, false, Ui.Divider(true), _layoutChoice, BuildPanelsButton(), popOut, newTab);
        _svc.BracketsCreated += HookBrackets;
        Content = BuildLayout();
    }

    internal IEnumerable<ChartTab> AllCharts => _pages.SelectMany(p => p.Charts).Concat(_floating.Select(w => w.Chart));

    /// <summary>Todos los gráficos abiertos (pestañas y ventanas aparte).</summary>
    internal IReadOnlyList<ChartTab> Tabs => AllCharts.ToList();

    internal IReadOnlyList<ChartPage> Pages => _pages;
    internal IReadOnlyList<ChartWindow> FloatingWindows => _floating;
    internal ChartTab? ActiveTab => _active;
    internal ChartPage? ActivePage => _page;
    internal AppServices Services => _svc;
    internal InstrumentPicker Picker => _toolbar.Picker;
    internal ChartToolbar Toolbar => _toolbar;

    internal void SetStatus(string text) => _status(text);

    // ===================================================================== layout

    private readonly Grid _body = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,340") };

    // el divisor va encima del borde del panel (6 px para agarrarlo con el mouse, sin dejar un hueco entre el gráfico y el panel)
    private readonly GridSplitter _sideSplitter = new()
    {
        ResizeDirection = GridResizeDirection.Columns,
        ResizeBehavior = GridResizeBehavior.PreviousAndCurrent,
        Width = 6,
        HorizontalAlignment = HorizontalAlignment.Left,
        Background = Brushes.Transparent,
        ZIndex = 10,
        Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeWestEast),
    };

    private Control BuildLayout()
    {
        var root = new DockPanel();
        DockPanel.SetDock(_toolbar, Dock.Top);
        root.Children.Add(_toolbar);

        _tabControl.SelectionChanged += (_, _) => OnPageChanged();
        _tabControl.Margin = new Thickness(0);
        // la tira de pestañas solo aparece cuando hay más de una pestaña
        _tabControl.Classes.Add("single");
        ((System.Collections.Specialized.INotifyCollectionChanged)_tabControl.Items).CollectionChanged += (_, _) =>
            _tabControl.Classes.Set("single", _tabControl.ItemCount <= 1);
        var chartColumn = new DockPanel();
        var replayBar = BuildReplayBar();
        DockPanel.SetDock(replayBar, Dock.Bottom);
        chartColumn.Children.Add(replayBar);
        chartColumn.Children.Add(_tabControl);
        Grid.SetColumn(chartColumn, 1);
        _body.Children.Add(chartColumn);

        var right = BuildRightPanel();
        Grid.SetColumn(right, 2);
        _body.Children.Add(right);
        Grid.SetColumn(_sideSplitter, 2);
        _body.Children.Add(_sideSplitter);
        root.Children.Add(_body);
        ApplyPanelVisibility();
        return root;
    }

    // ===================================================================== diseños de varios gráficos

    internal static int PaneCount(PaneLayout l) => l switch
    {
        PaneLayout.One => 1,
        PaneLayout.TwoColumns or PaneLayout.TwoRows => 2,
        PaneLayout.ThreeColumns or PaneLayout.OneLeftTwoRight or PaneLayout.OneTopTwoBottom => 3,
        PaneLayout.Four => 4,
        _ => 6,
    };

    /// <summary>Columnas, filas y celdas (columna, fila, ancho, alto) de cada diseño.</summary>
    private static (int Cols, int Rows, (int C, int R, int CS, int RS)[] Cells) LayoutGrid(PaneLayout l) => l switch
    {
        PaneLayout.One => (1, 1, new[] { (0, 0, 1, 1) }),
        PaneLayout.TwoColumns => (2, 1, new[] { (0, 0, 1, 1), (1, 0, 1, 1) }),
        PaneLayout.TwoRows => (1, 2, new[] { (0, 0, 1, 1), (0, 1, 1, 1) }),
        PaneLayout.ThreeColumns => (3, 1, new[] { (0, 0, 1, 1), (1, 0, 1, 1), (2, 0, 1, 1) }),
        PaneLayout.OneLeftTwoRight => (2, 2, new[] { (0, 0, 1, 2), (1, 0, 1, 1), (1, 1, 1, 1) }),
        PaneLayout.OneTopTwoBottom => (2, 2, new[] { (0, 0, 2, 1), (0, 1, 1, 1), (1, 1, 1, 1) }),
        PaneLayout.Four => (2, 2, new[] { (0, 0, 1, 1), (1, 0, 1, 1), (0, 1, 1, 1), (1, 1, 1, 1) }),
        _ => (3, 2, new[] { (0, 0, 1, 1), (1, 0, 1, 1), (2, 0, 1, 1), (0, 1, 1, 1), (1, 1, 1, 1), (2, 1, 1, 1) }),
    };

    /// <summary>Acomoda los gráficos de una pestaña según su diseño (con divisores para cambiar tamaños).</summary>
    private void Arrange(ChartPage page)
    {
        var host = page.Host;
        host.Children.Clear();
        host.ColumnDefinitions.Clear();
        host.RowDefinitions.Clear();
        if (page.Maximized is { } big && !page.Charts.Contains(big)) page.Maximized = null;
        if (page.Maximized is { } max)
        {
            // agrandado: solo ese gráfico, con su barra (para volver a achicarlo)
            host.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            host.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            max.Header.IsVisible = true;
            if (max.Frame.Parent is Panel prev && !ReferenceEquals(prev, host)) prev.Children.Remove(max.Frame);
            host.Children.Add(max.Frame);
            foreach (var c in page.Charts) UpdateMaximizeButton(c);
            UpdateFrames(page);
            UpdatePageHeader(page);
            return;
        }

        var (cols, rows, cells) = LayoutGrid(page.Layout);
        for (int c = 0; c < cols; c++)
        {
            if (c > 0) host.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(5)));
            host.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star) { MinWidth = 160 });
        }

        for (int r = 0; r < rows; r++)
        {
            if (r > 0) host.RowDefinitions.Add(new RowDefinition(new GridLength(5)));
            host.RowDefinitions.Add(new RowDefinition(GridLength.Star) { MinHeight = 120 });
        }

        bool multi = cells.Length > 1;
        for (int i = 0; i < cells.Length; i++)
        {
            var (c, r, cs, rs) = cells[i];
            Control cell;
            if (i < page.Charts.Count)
            {
                var chart = page.Charts[i];
                chart.Header.IsVisible = multi;
                cell = chart.Frame;
                if (cell.Parent is Panel old && !ReferenceEquals(old, host)) old.Children.Remove(cell);
            }
            else
            {
                cell = EmptyPane(page);
            }

            Avalonia.Controls.Grid.SetColumn(cell, c * 2);
            Avalonia.Controls.Grid.SetRow(cell, r * 2);
            Avalonia.Controls.Grid.SetColumnSpan(cell, cs * 2 - 1);
            Avalonia.Controls.Grid.SetRowSpan(cell, rs * 2 - 1);
            host.Children.Add(cell);
        }

        // divisores: entre columnas (en las filas que corresponda) y entre filas
        void VSplit(int col, int row, int rowSpan)
        {
            var s = new GridSplitter { ResizeDirection = GridResizeDirection.Columns, Background = Ui.Bg0 };
            Avalonia.Controls.Grid.SetColumn(s, col);
            Avalonia.Controls.Grid.SetRow(s, row);
            Avalonia.Controls.Grid.SetRowSpan(s, rowSpan);
            host.Children.Add(s);
        }

        void HSplit(int row, int col, int colSpan)
        {
            var s = new GridSplitter { ResizeDirection = GridResizeDirection.Rows, Background = Ui.Bg0 };
            Avalonia.Controls.Grid.SetRow(s, row);
            Avalonia.Controls.Grid.SetColumn(s, col);
            Avalonia.Controls.Grid.SetColumnSpan(s, colSpan);
            host.Children.Add(s);
        }

        switch (page.Layout)
        {
            case PaneLayout.OneLeftTwoRight:
                VSplit(1, 0, 3);
                HSplit(1, 2, 1);
                break;
            case PaneLayout.OneTopTwoBottom:
                HSplit(1, 0, 3);
                VSplit(1, 2, 1);
                break;
            default:
                for (int c = 1; c < cols; c++) VSplit(c * 2 - 1, 0, rows * 2 - 1);
                for (int r = 1; r < rows; r++) HSplit(r * 2 - 1, 0, cols * 2 - 1);
                break;
        }

        foreach (var c in page.Charts) UpdateMaximizeButton(c);
        UpdateFrames(page);
        UpdatePageHeader(page);
    }

    /// <summary>Agranda un gráfico para que ocupe toda la pestaña (o lo vuelve a su lugar).</summary>
    internal void ToggleMaximize(ChartTab chart)
    {
        if (chart.Page is not { } page) return;
        page.Maximized = ReferenceEquals(page.Maximized, chart) ? null : chart;
        Arrange(page);
        Activate(chart);
    }

    private static void UpdateMaximizeButton(ChartTab chart)
    {
        if (chart.MaximizeButton is not { } b) return;
        bool big = chart.Page?.Maximized != null && ReferenceEquals(chart.Page.Maximized, chart);
        b.Content = Ui.Icon(big ? MaterialIconKind.ArrowCollapse : MaterialIconKind.ArrowExpand, 14);
        ToolTip.SetTip(b, big ? "Volver a mostrar todos los gráficos" : "Agrandar este gráfico (ocupa toda la pestaña)");
    }

    /// <summary>Diseño justo para la cantidad de gráficos que quedan (al cerrar o sacar uno no quedan lugares vacíos).</summary>
    private static PaneLayout FitLayout(PaneLayout current, int count) => count switch
    {
        <= 1 => PaneLayout.One,
        2 => current is PaneLayout.TwoRows or PaneLayout.OneTopTwoBottom ? PaneLayout.TwoRows : PaneLayout.TwoColumns,
        3 => current is PaneLayout.OneLeftTwoRight or PaneLayout.OneTopTwoBottom or PaneLayout.ThreeColumns ? current : PaneLayout.OneLeftTwoRight,
        4 => PaneLayout.Four,
        _ => current,
    };

    /// <summary>Si quedaron lugares vacíos en la pestaña, el diseño se achica a lo que hay.</summary>
    private void ShrinkLayout(ChartPage page)
    {
        if (PaneCount(page.Layout) <= page.Charts.Count) return;
        page.Layout = FitLayout(page.Layout, page.Charts.Count);
        if (ReferenceEquals(page, _page)) _layoutChoice.Select(page.Layout);
    }

    private Control EmptyPane(ChartPage page)
    {
        var add = Ui.Button("Agregar gráfico", MaterialIconKind.Plus, () => _ = AddClonedChartAsync(page));
        var box = Ui.V(10, Ui.Icon(MaterialIconKind.ChartBoxOutline, 34, Ui.Text2), add);
        box.HorizontalAlignment = HorizontalAlignment.Center;
        box.VerticalAlignment = VerticalAlignment.Center;
        return new Border { Child = box, Background = Ui.Bg1, BorderBrush = Ui.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8) };
    }

    /// <summary>
    /// Resalta el gráfico activo cuando hay varios: borde fino con el acento suave y el título encendido (los otros con
    /// el borde de siempre y el título apagado). Antes era un borde ámbar fuerte que recargaba la pantalla.
    /// </summary>
    private void UpdateFrames(ChartPage page)
    {
        bool multi = PaneCount(page.Layout) > 1;
        // un solo gráfico: ocupa todo, de borde a borde (sin margen, sin marco ni esquinas redondeadas)
        bool alone = page.Maximized != null || !multi;
        // con el panel derecho a la vista, los gráficos de la última columna quedan pegados a él: sin hueco, sin esquinas
        // redondeadas de ese lado y con una sola línea entre los dos (la del panel)
        page.Host.Margin = alone ? new Thickness(0) : _sideVisible ? new Thickness(6, 6, 0, 6) : new Thickness(6);
        int cols = page.Host.ColumnDefinitions.Count;
        foreach (var c in page.Charts)
        {
            bool on = multi && ReferenceEquals(c, page.Active);
            bool flush = _sideVisible && ReferenceEquals(c.Frame.Parent, page.Host) &&
                         Avalonia.Controls.Grid.GetColumn(c.Frame) + Avalonia.Controls.Grid.GetColumnSpan(c.Frame) >= cols;
            c.Frame.BorderBrush = on ? Ui.Brush("ActiveFrameBrush") : Ui.Line;
            c.Frame.BorderThickness = alone ? new Thickness(0) : flush ? new Thickness(1, 1, 0, 1) : new Thickness(1);
            c.Frame.CornerRadius = alone ? new CornerRadius(0) : flush ? new CornerRadius(8, 0, 0, 8) : new CornerRadius(8);
            if (c.Frame.Child is Border inner) inner.CornerRadius = alone ? new CornerRadius(0) : flush ? new CornerRadius(7, 0, 0, 7) : new CornerRadius(7);
            c.Title.Foreground = !multi || on ? Ui.Text0 : Ui.Text2;
            if (c.Header is Border hb) hb.Background = on ? Ui.Brush("ActiveHeaderBrush") : Ui.Bg1;
        }
    }

    private void UpdatePageHeader(ChartPage page)
    {
        var main = page.Active ?? page.Charts.FirstOrDefault();
        int n = page.VisibleCount;
        page.HeaderText.Text = main == null ? "Vacía" : n > 1 ? $"{main.Caption}  +{n - 1}" : main.Caption;
    }

    /// <summary>Actualiza los títulos donde aparece un gráfico (marco, pestaña y ventana).</summary>
    internal void UpdateTitles(ChartTab chart)
    {
        chart.Title.Text = chart.Caption;
        if (chart.Page != null) UpdatePageHeader(chart.Page);
        chart.Window?.UpdateTitle();
    }

    internal async Task SetLayoutAsync(PaneLayout layout)
    {
        var page = _page;
        if (page == null) return;
        page.Layout = layout;
        page.Maximized = null; // elegir un diseño vuelve a mostrar todos
        _layoutChoice.Select(layout);
        int need = PaneCount(layout);
        // el gráfico que estabas usando sigue a la vista aunque el diseño tenga menos lugares
        if (page.Active is { } act && page.Charts.IndexOf(act) is var ai && ai >= need)
        {
            page.Charts.RemoveAt(ai);
            page.Charts.Insert(0, act);
        }

        // los gráficos que sobran quedan guardados en la pestaña (vuelven al elegir un diseño más grande)
        while (page.Charts.Count < need)
            if (await AddClonedChartAsync(page, arrange: false) == null) break;
        Arrange(page);
        if (page.Active == null || page.Charts.IndexOf(page.Active) >= need) Activate(page.Charts.FirstOrDefault());
        _status(need == 1 ? "Un gráfico por pestaña" : $"{need} gráficos en la pestaña: hacé clic en uno para elegirlo y cambiarle el instrumento o el período");
    }

    /// <summary>Agrega a la pestaña una copia del gráfico activo con otro período.</summary>
    private async Task<ChartTab?> AddClonedChartAsync(ChartPage page, bool arrange = true)
    {
        var src = page.Active ?? page.Charts.FirstOrDefault() ?? _active;
        var used = page.Charts.Select(c => c.Model?.Period).Where(p => p != null).ToHashSet();
        var period = new[] { ChartPeriod.M1, ChartPeriod.M5, ChartPeriod.M15, ChartPeriod.H1, ChartPeriod.M30, ChartPeriod.H4, ChartPeriod.Parse("Daily"), ChartPeriod.Parse("S15") }
            .Where(p => !used.Contains(p)).DefaultIfEmpty(ChartPeriod.M5).First();
        var chart = CreateChart(src?.FeedId ?? SimulatedFeed.FeedId, src?.Symbol ?? "SIM-BTC", src?.AccountId ?? "sim", src?.HistoryHours ?? 0);
        page.Charts.Add(chart);
        chart.Page = page;
        await OpenInTabAsync(chart, chart.FeedId, chart.Symbol, period, src?.Model?.Settings.Clone(), copyIndicatorsFrom: src);
        if (arrange) Arrange(page);
        return chart;
    }

    // ===================================================================== gráficos y pestañas

    /// <summary>Crea un gráfico vacío (marco, encabezado y eventos). Después se abre con OpenInTabAsync.</summary>
    private ChartTab CreateChart(string feedId, string symbol, string accountId, double historyHours)
    {
        var view = new ChartView();
        var title = new TextBlock { Text = symbol, FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var headerRight = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        var header = new DockPanel { Height = 26, Margin = new Thickness(8, 0, 4, 0) };
        DockPanel.SetDock(headerRight, Dock.Right);
        header.Children.Add(headerRight);
        header.Children.Add(title);
        var headerBorder = new Border { Child = header, Background = Ui.Bg1, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1) };
        var body = new DockPanel();
        DockPanel.SetDock(headerBorder, Dock.Top);
        body.Children.Add(headerBorder);
        body.Children.Add(view);
        // el contenido se recorta con el radio de adentro del borde: el gráfico acompaña la curva de la esquina (sin puntas)
        var inner = new Border { Child = body, CornerRadius = new CornerRadius(7), ClipToBounds = true };
        var frame = new Border { Child = inner, ClipToBounds = true, CornerRadius = new CornerRadius(8), Background = Ui.Bg0, BorderBrush = Ui.Line, BorderThickness = new Thickness(1) };
        var chart = new ChartTab { View = view, Frame = frame, Header = headerBorder, Title = title, FeedId = feedId, Symbol = symbol, AccountId = accountId, HistoryHours = historyHours };

        // cada gráfico con su barra de dibujos (izquierda) y su Chart Trader (derecha), como en ATAS
        var tools = new DrawingToolStrip();
        chart.Tools = tools;
        DockPanel.SetDock(tools, Dock.Left);
        body.Children.Insert(body.Children.IndexOf(view), tools);
        tools.ToolSelected += kind =>
        {
            Activate(chart);
            if (chart.View.Controller is { } ctl)
            {
                ctl.ActiveTool = kind;
                chart.View.Focus();
            }
        };
        tools.ClearRequested += () =>
        {
            if (chart.Model == null) return;
            chart.Model.Drawings.Clear();
            chart.Model.Invalidate();
            DrawingsTouched(chart);
        };
        var trader = new ChartTraderPanel(this, chart);
        chart.Trader = trader;
        DockPanel.SetDock(trader, Dock.Right);
        body.Children.Insert(body.Children.IndexOf(view), trader);

        // color de vinculación en la esquina de la barrita del gráfico
        var link = new LinkButton(this, () => chart) { Margin = new Thickness(0, 0, 6, 0) };
        chart.LinkButton = link;
        DockPanel.SetDock(link, Dock.Left);
        header.Children.Insert(1, link);
        view.CrosshairMoved += (time, price) => OnCrosshair(chart, time, price);

        Button Small(MaterialIconKind icon, string tip, Action a)
        {
            var b = new Button { Content = Ui.Icon(icon, 14), Width = 22, Height = 22, MinHeight = 0, Padding = new Thickness(0) }.Classes("icon");
            ToolTip.SetTip(b, tip);
            b.Click += (_, e) =>
            {
                a();
                e.Handled = true;
            };
            return b;
        }

        chart.MaximizeButton = Small(MaterialIconKind.ArrowExpand, "Agrandar este gráfico (ocupa toda la pestaña)", () => ToggleMaximize(chart));
        headerRight.Children.Add(chart.MaximizeButton);
        headerRight.Children.Add(Small(MaterialIconKind.OpenInNew, "Sacar a una ventana aparte", () => Detach(chart)));
        headerRight.Children.Add(Small(MaterialIconKind.Close, "Cerrar este gráfico", () => CloseChart(chart)));

        // clic en cualquier parte del gráfico: pasa a ser el activo
        frame.AddHandler(PointerPressedEvent, (_, _) => Activate(chart), RoutingStrategies.Tunnel, handledEventsToo: true);
        view.ToolFinished += () => chart.Tools?.Check(null);
        view.DrawingsChanged += () => DrawingsTouched(chart);
        view.DrawingDefaults = k => _svc.Settings.DrawingDefaults.TryGetValue(k.ToString(), out var d) ? d : null;
        view.MenuRequested += (_, p) => ShowChartMenu(chart, p);
        view.IndicatorSettingsRequested += slot => OpenIndicators(chart, slot);
        view.StrategySettingsRequested += ov =>
        {
            if (ov.Tag is ChartStrategy cs) OpenStrategySettings(chart, cs);
        };
        view.StrategyRemoveRequested += ov =>
        {
            if (ov.Tag is ChartStrategy cs) RemoveStrategy(chart, cs);
        };
        view.IndicatorsChanged += () => Workspace.Save(this);
        view.DrawingDoubleClicked += d => EditDrawing(chart, d);
        view.OrderDragged += (o, price) => _ = MoveOrderAsync(chart, o, price);
        view.BracketDropped += (d, price) => _ = OnBracketDroppedAsync(chart, d, price);
        return chart;
    }

    private ChartPage CreatePage()
    {
        var headerText = new TextBlock { Text = "…", VerticalAlignment = VerticalAlignment.Center };
        var close = new Button { Content = Ui.Icon(MaterialIconKind.Close, 13), Width = 20, Height = 20, MinHeight = 0, Padding = new Thickness(0) }.Classes("icon");
        ToolTip.SetTip(close, "Cerrar la pestaña");
        var item = new TabItem { Header = Ui.H(8, headerText, close) };
        var page = new ChartPage { Item = item, HeaderText = headerText };
        item.Content = page.Host;
        close.Click += (_, e) =>
        {
            ClosePage(page);
            e.Handled = true;
        };
        return page;
    }

    /// <summary>Pestaña nueva con un gráfico.</summary>
    internal async Task<ChartTab> NewTabAsync(string feedId, string symbol, ChartPeriod period, ChartSettings? settings = null, bool activate = true, string accountId = "sim", double historyHours = 0, PaneLayout layout = PaneLayout.One)
    {
        var page = CreatePage();
        page.Layout = layout;
        var chart = CreateChart(feedId, symbol, accountId, historyHours);
        page.Charts.Add(chart);
        page.Active = chart;
        chart.Page = page;
        _pages.Add(page);
        Arrange(page);
        _tabControl.Items.Add(page.Item);
        if (activate || _page == null) _tabControl.SelectedItem = page.Item;
        await OpenInTabAsync(chart, feedId, symbol, period, settings, copyIndicatorsFrom: null);
        return chart;
    }

    /// <summary>Agrega un gráfico a una pestaña existente (lo usa la restauración del espacio de trabajo).</summary>
    internal async Task<ChartTab> AddToPageAsync(ChartPage page, string feedId, string symbol, ChartPeriod period, ChartSettings? settings, string accountId, double historyHours)
    {
        var chart = CreateChart(feedId, symbol, accountId, historyHours);
        page.Charts.Add(chart);
        chart.Page = page;
        Arrange(page);
        await OpenInTabAsync(chart, feedId, symbol, period, settings, copyIndicatorsFrom: null);
        return chart;
    }

    internal async Task OpenInTabAsync(ChartTab tab, string feedId, string symbol, ChartPeriod period, ChartSettings? settings) =>
        await OpenInTabAsync(tab, feedId, symbol, period, settings, copyIndicatorsFrom: tab);

    /// <summary>Carga (o cambia) el instrumento de un gráfico. Copia los indicadores del gráfico indicado.</summary>
    private async Task OpenInTabAsync(ChartTab tab, string feedId, string symbol, ChartPeriod period, ChartSettings? settings, ChartTab? copyIndicatorsFrom)
    {
        try
        {
            var profile = _svc.Connections.Find(feedId);
            if (profile == null)
            {
                _status($"La conexión {feedId} no existe; revisá Conexiones.");
                return;
            }

            var instruments = await InstrumentPicker.InstrumentsAsync(_svc, profile.Id);
            var inst = instruments.FirstOrDefault(i => i.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));
            if (inst == null)
            {
                _status($"No encontré {symbol} en {profile.Name}");
                return;
            }

            bool instrumentChanged = tab.Model != null && (tab.FeedId != profile.Id || !tab.Symbol.Equals(inst.Symbol, StringComparison.OrdinalIgnoreCase));
            var oldIndicators = copyIndicatorsFrom?.Model?.Indicators.Select(s => s.Indicator).ToList();
            tab.Model?.Dispose();
            var data = _svc.Open(profile.Id, inst);
            var model = new ChartModel(data, period, settings ?? new ChartSettings())
            {
                Trading = _svc.SimBrokerFor(data),
                Statistics = _svc.StatsProvider,
            };
            model.Settings.TimeZoneId = _svc.Settings.TimeZoneId;
            model.Viewport.BarWidth = model.Settings.Style == ChartStyle.Footprint ? 110 : 8;
            model.AlertRaised += a => Dispatcher.UIThread.Post(() => ShowAlert(a));
            tab.Model = model;
            tab.FeedId = profile.Id;
            tab.Symbol = inst.Symbol;
            tab.View.Model = model;
            // una cuenta de Rithmic se mantiene aunque su sesión todavía se esté conectando
            if (!tab.AccountId.StartsWith("rithmic|", StringComparison.Ordinal) && !_svc.AccountsFor(data).Any(a => a.Id == tab.AccountId)) tab.AccountId = "sim";
            if (instrumentChanged && tab.CrossSymbol != null)
            {
                tab.CrossSymbol = null;
                tab.CrossData = null;
            }

            model.Account = AccountOf(tab) ?? _svc.AccountFor(data, tab.AccountId);
            model.Brackets = _svc.BracketsFor(model.Account);
            tab.Trader?.Rebind();
            Workspace.LoadDrawings(tab);
            if (tab.HistoryHours != 0) _ = LoadHistoryAsync(tab, tab.HistoryHours, quiet: true);

            if (oldIndicators != null)
            {
                foreach (var ind in oldIndicators)
                {
                    var copy = Workspace.CloneIndicator(_svc, ind);
                    if (copy != null) model.AddIndicator(copy, applyDefaultColors: false);
                }
            }

            UpdateTitles(tab);
            if (tab.Strategies.Count > 0) RestartStrategies(tab, instrumentChanged);
            if (ReferenceEquals(tab, _active)) BindSidePanel(tab, force: true);
            if (ReferenceEquals(tab, _active)) _toolbar.Chart = tab;
            tab.Window?.Toolbar.Sync();
            if (!_linkSync) _ = PropagateSymbolAsync(tab);
            _status($"Abriendo {inst.Symbol} en {profile.Name}…");
            void Ready()
            {
                data.HistoryLoaded -= Ready;
                if (tab.Model?.Data != data) return;
                var from = TimeZoneInfo.ConvertTimeFromUtc(data.Trades.Count > 0 ? data.Trades.FirstTime : DateTime.UtcNow, model.Settings.TimeZone);
                _status(data.HistoryError != null
                    ? $"{inst.Symbol}: no se pudo traer toda la historia ({data.HistoryError})"
                    : $"{inst.Symbol} listo · {data.Trades.Count:N0} operaciones desde {from:dd/MM HH:mm}");
            }

            if (data.IsLoadingHistory) data.HistoryLoaded += Ready;
        }
        catch (Exception ex)
        {
            _status("Error al abrir: " + ex.Message);
            CrashLog.Write(ex);
        }
    }

    /// <summary>Hace activo un gráfico de la ventana principal (barra, DOM y ticket pasan a él).</summary>
    internal void Activate(ChartTab? chart)
    {
        if (chart == null || chart.Window != null) return;
        var page = chart.Page;
        if (page == null) return;
        bool changed = !ReferenceEquals(chart, _active);
        page.Active = chart;
        if (!ReferenceEquals(page, _page))
        {
            _switching = true;
            _tabControl.SelectedItem = page.Item;
            _switching = false;
            _page = page;
        }

        _active = chart;
        UpdateFrames(page);
        UpdatePageHeader(page);
        if (!changed) return;
        _toolbar.Chart = chart;
        _layoutChoice.Select(page.Layout);
        BindSidePanel(chart, force: false);
        UpdateReplayBar();
    }

    private void OnPageChanged()
    {
        if (_switching) return;
        _page = _pages.FirstOrDefault(p => ReferenceEquals(p.Item, _tabControl.SelectedItem));
        if (_page == null) return;
        var chart = _page.Active ?? _page.Charts.FirstOrDefault();
        _active = null; // fuerza a reenganchar barra y panel
        Activate(chart);
        _layoutChoice.Select(_page.Layout);
    }

    private void DisposeChart(ChartTab chart)
    {
        // las estrategias del gráfico se detienen (las posiciones abiertas quedan como están)
        foreach (var cs in chart.Strategies) StopStrategy(cs);
        chart.Model?.Dispose();
        chart.OnClosed?.Invoke();
        chart.Page = null;
        chart.Window = null;
    }

    /// <summary>Cierra un gráfico (de una pestaña o de una ventana aparte).</summary>
    internal void CloseChart(ChartTab chart)
    {
        if (chart.Window is { } w)
        {
            w.Close();
            return;
        }

        var page = chart.Page;
        if (page == null) return;
        if (page.Charts.Count == 1)
        {
            ClosePage(page);
            return;
        }

        page.Charts.Remove(chart);
        DisposeChart(chart);
        if (ReferenceEquals(page.Active, chart)) page.Active = page.Charts[0];
        if (ReferenceEquals(page.Maximized, chart)) page.Maximized = null;
        ShrinkLayout(page);
        Arrange(page);
        if (ReferenceEquals(_active, chart))
        {
            _active = null;
            Activate(page.Active);
        }
    }

    private void ClosePage(ChartPage page)
    {
        if (_pages.Count <= 1)
        {
            _status("Tiene que quedar al menos una pestaña abierta.");
            return;
        }

        foreach (var c in page.Charts.ToList()) DisposeChart(c);
        page.Charts.Clear();
        _pages.Remove(page);
        _tabControl.Items.Remove(page.Item);
    }

    /// <summary>Cierra el gráfico activo (⌘W).</summary>
    internal void CloseActiveTab()
    {
        if (_active != null) CloseChart(_active);
    }

    /// <summary>Saca el gráfico de su pestaña y lo deja solo en una pestaña nueva.</summary>
    internal async Task MoveToNewPageAsync(ChartTab chart)
    {
        var from = chart.Page;
        if (from == null || from.Charts.Count == 1) return;
        from.Charts.Remove(chart);
        if (ReferenceEquals(from.Active, chart)) from.Active = from.Charts[0];
        if (ReferenceEquals(from.Maximized, chart)) from.Maximized = null;
        ShrinkLayout(from);
        Arrange(from);
        var page = CreatePage();
        page.Charts.Add(chart);
        page.Active = chart;
        chart.Page = page;
        _pages.Add(page);
        Arrange(page);
        _tabControl.Items.Add(page.Item);
        _active = null;
        Activate(chart);
        await Task.CompletedTask;
    }

    // ===================================================================== ventanas aparte

    internal void DetachActive()
    {
        if (_active != null) Detach(_active);
    }

    /// <summary>Saca un gráfico a su propia ventana (se puede llevar a otro monitor).</summary>
    internal ChartWindow? Detach(ChartTab chart, PixelPoint? position = null, Size? size = null)
    {
        var page = chart.Page;
        if (page == null) return null;
        bool lastChart = _pages.Count == 1 && page.Charts.Count == 1;
        page.Charts.Remove(chart);
        if (ReferenceEquals(page.Active, chart)) page.Active = page.Charts.FirstOrDefault();
        chart.Page = null;
        // en su ventana vuelve a tener las cuatro esquinas redondeadas (pegado al panel derecho no las tenía)
        chart.Frame.BorderThickness = new Thickness(1);
        chart.Frame.CornerRadius = new CornerRadius(8);
        if (chart.Frame.Child is Border innerFrame) innerFrame.CornerRadius = new CornerRadius(7);

        var w = new ChartWindow(this, chart);
        // solo vuelve a la posición guardada si ese lugar sigue en alguna pantalla
        if (position is { } p && w.Screens.All.Any(sc => sc.WorkingArea.Contains(p))) w.Position = p;
        if (size is { } s)
        {
            w.Width = s.Width;
            w.Height = s.Height;
        }

        _floating.Add(w);
        w.Closed += (_, _) => OnFloatingClosed(w);
        w.Show();

        if (page.Charts.Count == 0)
        {
            if (lastChart)
            {
                // la ventana principal nunca queda sin gráficos: deja una copia
                _pages.Remove(page);
                _tabControl.Items.Remove(page.Item);
                _ = NewTabAsync(chart.FeedId, chart.Symbol, chart.Model?.Period ?? ChartPeriod.M5, chart.Model?.Settings.Clone(), accountId: chart.AccountId);
            }
            else
            {
                _pages.Remove(page);
                _tabControl.Items.Remove(page.Item);
            }
        }
        else
        {
            Arrange(page);
            if (ReferenceEquals(_active, chart))
            {
                _active = null;
                Activate(page.Active);
            }
        }

        _status($"{chart.Caption} en una ventana aparte: movela a otro monitor si querés. Para volver, usá «Volver a la ventana principal».");
        return w;
    }

    /// <summary>Devuelve un gráfico de una ventana aparte a la pestaña activa (o a una nueva si está llena).</summary>
    internal void DockBack(ChartWindow w)
    {
        var chart = w.Chart;
        w.ReleaseChart();
        _floating.Remove(w);
        chart.Window = null;
        chart.Frame.Margin = new Thickness(0); // en la pestaña el espacio lo da la grilla
        var page = _page;
        if (page != null && page.Charts.Count < PaneCount(page.Layout))
        {
            page.Charts.Add(chart);
            chart.Page = page;
            Arrange(page);
        }
        else
        {
            page = CreatePage();
            page.Charts.Add(chart);
            chart.Page = page;
            _pages.Add(page);
            Arrange(page);
            _tabControl.Items.Add(page.Item);
        }

        _active = null;
        Activate(chart);
        w.Close();
    }

    private void OnFloatingClosed(ChartWindow w)
    {
        if (!_floating.Remove(w)) return;
        if (!w.Released) DisposeChart(w.Chart);
    }

    /// <summary>Cierra las ventanas aparte (al salir de la app).</summary>
    internal void CloseFloating()
    {
        foreach (var w in _floating.ToList()) w.Close();
        ClosePanelWindowsForShutdown();
    }

    internal void RefreshLayoutBoxes()
    {
        _toolbar.RefreshLayoutBox();
        foreach (var w in _floating) w.Toolbar.RefreshLayoutBox();
    }

    // ===================================================================== replay

    private readonly Border _replayBar = new() { IsVisible = false };
    private readonly ToggleButton _replayPlay = new() { Width = 34, Height = 30, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _replayClock = new() { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, MinWidth = 150 };
    private readonly ProgressBar _replayProgress = new() { Minimum = 0, Maximum = 1, Height = 4, MinWidth = 160, VerticalAlignment = VerticalAlignment.Center };
    private Ui.Segmented<double>? _replaySpeed;

    private ReplayFeed? ActiveReplay => _active?.Model?.Data.Feed as ReplayFeed;

    private Control BuildReplayBar()
    {
        _replayPlay.Click += (_, _) =>
        {
            if (ActiveReplay is { } r) r.Paused = _replayPlay.IsChecked != true;
            UpdateReplayBar();
        };
        _replaySpeed = new Ui.Segmented<double>(new[] { 1.0, 2, 5, 10, 30, 60, 300 }.Select(v => (v, $"{v:0}×", (string?)null)));
        _replaySpeed.SelectionChanged += v =>
        {
            if (ActiveReplay is { } r) r.Speed = v;
        };
        Button Jump(string text, TimeSpan by) => Ui.Button(text, null, () =>
        {
            if (ActiveReplay is { } r) r.SeekForward(r.Clock + by);
        });

        var badge = new Border { Child = Ui.H(6, Ui.Icon(MaterialIconKind.PlayCircleOutline, 15, Ui.Accent), new TextBlock { Text = "REPLAY", FontSize = 11, FontWeight = FontWeight.Bold }), Classes = { "pill" } };
        var row = Ui.H(12, badge, _replayPlay, _replaySpeed, _replayClock, _replayProgress, Ui.H(6, Jump("+5 min", TimeSpan.FromMinutes(5)), Jump("+30 min", TimeSpan.FromMinutes(30)), Jump("+1 h", TimeSpan.FromHours(1))));
        row.Margin = new Thickness(12, 8);
        _replayBar.Child = row;
        _replayBar.Background = Ui.Bg1;
        _replayBar.BorderBrush = Ui.Line;
        _replayBar.BorderThickness = new Thickness(0, 1, 0, 0);
        return _replayBar;
    }

    private void UpdateReplayBar()
    {
        var r = ActiveReplay;
        _replayBar.IsVisible = r != null;
        if (r == null || _active?.Model == null) return;
        _replayPlay.IsChecked = !r.Paused && !r.IsFinished;
        _replayPlay.Content = Ui.Icon(r.Paused || r.IsFinished ? MaterialIconKind.Play : MaterialIconKind.Pause, 18);
        _replaySpeed?.Select(r.Speed);
        var local = TimeZoneInfo.ConvertTimeFromUtc(r.Clock, _active.Model.Settings.TimeZone);
        _replayClock.Text = r.IsFinished ? "Terminado" : local.ToString("ddd dd/MM HH:mm:ss", TradingHub.Common.UiCulture.Current);
        _replayProgress.Value = r.Progress;
    }

    /// <summary>Abre un replay en una pestaña nueva.</summary>
    internal async Task OpenReplayAsync(Instrument source, DateTime startUtc, DateTime endUtc, ChartPeriod period)
    {
        var data = _svc.OpenReplay(source, startUtc, endUtc, TimeSpan.FromHours(3));
        var page = CreatePage();
        var chart = CreateChart(data.Instrument.FeedId, source.Symbol, "sim", 0);
        chart.OnClosed = () => data.Feed.Dispose();
        page.Charts.Add(chart);
        page.Active = chart;
        chart.Page = page;
        var settings = _active?.Model?.Settings.Clone() ?? new ChartSettings();
        var model = new ChartModel(data, period, settings) { Trading = _svc.SimBrokerFor(data), Statistics = _svc.StatsProvider };
        model.Settings.TimeZoneId = _svc.Settings.TimeZoneId;
        model.Viewport.BarWidth = model.Settings.Style == ChartStyle.Footprint ? 110 : 8;
        model.Account = _svc.SimBrokerFor(data);
        model.Brackets = _svc.BracketsFor(model.Account);
        if (_active?.Model != null)
        {
            foreach (var ind in _active.Model.Indicators.Select(s => s.Indicator))
            {
                var copy = Workspace.CloneIndicator(_svc, ind);
                if (copy != null) model.AddIndicator(copy, applyDefaultColors: false);
            }
        }

        chart.Model = model;
        chart.View.Model = model;
        _pages.Add(page);
        Arrange(page);
        _tabControl.Items.Add(page.Item);
        _tabControl.SelectedItem = page.Item;
        UpdateTitles(chart);
        _status($"Replay de {source.Symbol} desde {TimeZoneInfo.ConvertTimeFromUtc(startUtc, model.Settings.TimeZone):dd/MM HH:mm}: operá con la cuenta simulada como si fuera en vivo.");
        await Task.CompletedTask;
    }

    // ===================================================================== historia

    /// <summary>
    /// Carga historia hasta <paramref name="hours"/> atrás (o todo el historial). Si todavía se está cargando
    /// algo, espera y sigue. Lo que no está en disco se trae del exchange: lo reciente en ticks y lo viejo en velas.
    /// </summary>
    internal async Task LoadHistoryAsync(ChartTab chart, double hours, bool quiet = false)
    {
        var data = chart.Model?.Data;
        if (data == null) return;
        chart.HistoryHours = hours;
        string what = ChartToolbar.SpanText(hours);
        DateTime from;
        try
        {
            if (hours == ChartToolbar.AllHistory)
            {
                if (!quiet) _status($"Buscando dónde empieza el historial de {chart.Symbol}…");
                from = data.Feed is IKlineHistory k ? await k.FirstAvailableAsync(data.Instrument, CancellationToken.None) ?? data.Feed.Now.AddYears(-5) : data.Feed.Now.AddYears(-5);
            }
            else from = data.Feed.Now.AddHours(-hours);
        }
        catch (Exception ex)
        {
            _status($"No se pudo consultar el historial de {chart.Symbol}: {ex.Message}");
            return;
        }

        // espera a que termine lo que se esté cargando (por ejemplo, la historia inicial)
        for (int i = 0; i < 2400 && (data.IsLoadingHistory || data.IsExtending); i++) await Task.Delay(250);
        if (!ReferenceEquals(chart.Model?.Data, data)) return;
        if (!data.ExtendHistory(from))
        {
            if (!quiet) _status($"{chart.Symbol}: la historia pedida ({what}) ya está cargada.");
            return;
        }

        if (!quiet || hours > 24) _status($"Cargando {what} de {chart.Symbol}: lo que no está guardado se trae del exchange…");
        for (int i = 0; i < 24000 && data.IsExtending; i++) await Task.Delay(250);
        if (!ReferenceEquals(chart.Model?.Data, data)) return;
        var tz = chart.Model!.Settings.TimeZone;
        string fromText = TimeZoneInfo.ConvertTimeFromUtc(data.Trades.Count > 0 ? data.Trades.FirstTime : from, tz).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
        string ticks = data.RealTicksFrom < DateTime.MaxValue
            ? $" · footprint (ticks) desde {TimeZoneInfo.ConvertTimeFromUtc(data.RealTicksFrom, tz):dd/MM HH:mm}; antes, velas de 1 minuto del exchange (para más footprint, bajá días en Datos)"
            : "";
        _status(data.HistoryError != null
            ? $"{chart.Symbol}: no se pudo traer toda la historia ({data.HistoryError})"
            : $"{chart.Symbol}: historia desde {fromText} · {data.Trades.Count:N0} operaciones{ticks}");
    }

    // ===================================================================== herramientas y ventanas auxiliares

    internal void OpenIndicators() => OpenIndicators(_active);

    internal void OpenIndicators(ChartTab? chart, IndicatorSlot? slot = null)
    {
        if (chart?.Model == null) return;
        var owner = chart.Window ?? TopLevel.GetTopLevel(this) as Window;
        if (owner == null) return;
        var w = new IndicatorsWindow(_svc, chart.Model);
        w.Show(owner);
        if (slot != null) w.Select(slot);
    }

    /// <summary>Cambió un dibujo: se guarda y se redibujan todos los gráficos del mismo instrumento.</summary>
    internal void DrawingsTouched(ChartTab chart)
    {
        if (!chart.IsReplay) Workspace.SaveDrawings(chart);
        foreach (var c in AllCharts)
        {
            if (ReferenceEquals(c.Model?.Drawings, chart.Model?.Drawings)) c.Model?.Invalidate();
        }
    }

    /// <summary>
    /// Marca en el gráfico las ejecuciones de su cuenta en el instrumento donde opera (con cross trading, las del otro):
    /// salen del historial guardado, así se ven también las de días anteriores.
    /// </summary>
    private void UpdateTradeMarks(ChartTab c)
    {
        if (c.Model is not { } m || c.IsReplay) return;
        var inst = (c.CrossData ?? m.Data).Instrument;
        string key = m.Settings.ShowTradeMarks ? $"{c.AccountId}|{inst.Key}|{_svc.Journal.CountOf(c.AccountId)}" : "off";
        if (key == c.MarksKey) return;
        c.MarksKey = key;
        if (!m.Settings.ShowTradeMarks)
        {
            m.TradeMarks = Array.Empty<TradeMark>();
            m.TradeLinks = Array.Empty<TradeLink>();
            m.Invalidate();
            return;
        }

        // ESZ6 (Rithmic) es ES; BTCUSDT es BTCUSDT
        string root = System.Text.RegularExpressions.Regex.Replace(inst.Symbol, "[FGHJKMNQUVXZ][0-9]{1,2}$", "");
        bool Same(string symbol) =>
            symbol.Equals(inst.Symbol, StringComparison.OrdinalIgnoreCase)
            || System.Text.RegularExpressions.Regex.Replace(symbol, "[FGHJKMNQUVXZ][0-9]{1,2}$", "").Equals(root, StringComparison.OrdinalIgnoreCase);
        var fills = _svc.Journal.FillsOf(c.AccountId).Where(f => Same(f.Symbol)).ToList();
        m.TradeMarks = fills.Select(f => new TradeMark(f.Time, f.Price, f.Buy, f.Quantity)).ToList();
        m.TradeLinks = RoundTrips.Analyze(fills).Trades.Select(t => new TradeLink(t.OpenTime, t.OpenPrice, t.CloseTime, t.ClosePrice, t.IsLong, t.NetPnl)).ToList();
        m.Invalidate();
    }

    /// <summary>Cambió el modo claro/oscuro: cada gráfico rearma sus colores sobre la base nueva y se redibuja todo.</summary>
    internal void OnThemeChanged()
    {
        foreach (var c in AllCharts) c.Model?.ApplyColors();
        _dom.InvalidateVisual();
        _tape.InvalidateVisual();
        InvalidateVisual();
    }

    /// <summary>Pestaña nueva (botón + o ⌘T): copia el gráfico activo, o usa la plantilla para pestañas nuevas si hay una.</summary>
    internal async Task NewChartAsync()
    {
        var tab = await NewTabAsync(_active?.FeedId ?? SimulatedFeed.FeedId, _active?.Symbol ?? "SIM-BTC", _active?.Model?.Period ?? ChartPeriod.M5, _active?.Model?.Settings.Clone());
        if (_svc.Settings.DefaultChartTemplate is { } t && Workspace.ApplyChartTemplate(this, tab, t)) AfterTemplate(tab);
    }

    // ===================================================================== plantillas de gráfico

    private void AfterTemplate(ChartTab tab)
    {
        if (ReferenceEquals(tab, _active)) _toolbar.Sync();
        tab.Window?.Toolbar.Sync();
        tab.Trader?.Rebind();
    }

    private async Task SaveChartTemplateAsync(ChartTab tab)
    {
        if (tab.Model == null || TopLevel.GetTopLevel(tab.Window ?? (Control)this) is not Window owner) return;
        string suggested = $"{tab.Symbol} {(tab.Model.Settings.Style == ChartStyle.Footprint ? "footprint" : tab.Model.Settings.Style.ToString().ToLowerInvariant())}";
        var name = await PromptDialog.ShowAsync(owner, "Guardar plantilla de gráfico",
            "Nombre de la plantilla. Guarda el estilo, el footprint, los colores, lo que se muestra y los indicadores (no el instrumento ni el período).", suggested, "Guardar");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            Workspace.SaveChartTemplate(tab, name);
            _status($"Plantilla de gráfico «{name.Trim()}» guardada · aplicala a cualquier gráfico desde Plantillas");
        }
        catch (Exception ex)
        {
            _status("No se pudo guardar la plantilla: " + ex.Message);
        }
    }

    /// <summary>Opciones de plantillas de un gráfico (para el botón de la barra y el menú del clic derecho).</summary>
    internal List<Control> ChartTemplateItems(ChartTab tab)
    {
        var items = new List<Control>();
        var save = new MenuItem { Header = "Guardar este gráfico como plantilla…", Icon = Ui.Icon(MaterialIconKind.ContentSaveOutline, 16) };
        save.Click += (_, _) => _ = SaveChartTemplateAsync(tab);
        items.Add(save);
        var names = Workspace.ChartTemplateNames();
        if (names.Count == 0)
        {
            items.Add(new MenuItem { Header = "Todavía no hay plantillas guardadas", IsEnabled = false });
            return items;
        }

        items.Add(new Separator());
        string? def = _svc.Settings.DefaultChartTemplate;
        foreach (var n in names)
        {
            var apply = new MenuItem { Header = n == def ? $"{n}  · pestañas nuevas" : n, Icon = Ui.Icon(MaterialIconKind.BookmarkOutline, 16) };
            ToolTip.SetTip(apply, $"Aplicar «{n}» a este gráfico (reemplaza su estilo, colores e indicadores)");
            apply.Click += (_, _) =>
            {
                if (!Workspace.ApplyChartTemplate(this, tab, n)) return;
                AfterTemplate(tab);
                _status($"Plantilla «{n}» aplicada a {tab.Symbol}");
            };
            items.Add(apply);
        }

        items.Add(new Separator());
        var forNew = new MenuItem { Header = "Para las pestañas nuevas", Icon = Ui.Icon(MaterialIconKind.StarOutline, 16) };
        MenuItem Choice(string header, string? value)
        {
            var mi = new MenuItem { Header = header, Icon = value == def ? Ui.Icon(MaterialIconKind.Check, 15, Ui.Accent) : null };
            mi.Click += (_, _) =>
            {
                _svc.Settings.DefaultChartTemplate = value;
                _svc.Settings.Save();
                _status(value == null ? "Las pestañas nuevas copian el gráfico activo" : $"Las pestañas nuevas arrancan con «{value}»");
            };
            return mi;
        }

        forNew.Items.Add(Choice("Copiar el gráfico activo (sin plantilla)", null));
        foreach (var n in names) forNew.Items.Add(Choice(n, n));
        items.Add(forNew);

        var delete = new MenuItem { Header = "Borrar una plantilla", Icon = Ui.Icon(MaterialIconKind.DeleteOutline, 16) };
        foreach (var n in names)
        {
            var mi = new MenuItem { Header = n };
            mi.Click += (_, _) =>
            {
                Workspace.DeleteChartTemplate(n);
                if (_svc.Settings.DefaultChartTemplate == n)
                {
                    _svc.Settings.DefaultChartTemplate = null;
                    _svc.Settings.Save();
                }

                _status($"Plantilla «{n}» borrada");
            };
            delete.Items.Add(mi);
        }

        items.Add(delete);
        return items;
    }

    internal void ShowChartTemplates(ChartTab? tab, Control anchor)
    {
        if (tab?.Model == null) return;
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        foreach (var i in ChartTemplateItems(tab)) flyout.Items.Add(i);
        flyout.ShowAt(anchor);
    }

    private readonly Dictionary<ChartTab, Window> _settingsWindows = new();

    /// <summary>Configuración del gráfico (colores, fondo, velas…). Si ya está abierta, la trae al frente.</summary>
    internal void OpenChartSettings(ChartTab? chart)
    {
        if (chart?.Model == null) return;
        if (_settingsWindows.TryGetValue(chart, out var open))
        {
            open.Activate();
            return;
        }

        if ((chart.Window ?? TopLevel.GetTopLevel(this) as Window) is not { } owner) return;
        var w = new ChartSettingsWindow(this, chart);
        _settingsWindows[chart] = w;
        w.Closed += (_, _) => _settingsWindows.Remove(chart);
        w.Show(owner);
    }

    /// <summary>Copia los colores y lo que se muestra de un gráfico a todos los demás. Devuelve cuántos cambió.</summary>
    internal int ApplyChartLook(ChartTab source)
    {
        if (source.Model is not { } src) return 0;
        int n = 0;
        foreach (var c in AllCharts)
        {
            if (ReferenceEquals(c, source) || c.Model is not { } m) continue;
            m.Settings.Colors = src.Settings.Colors == null ? null : new Dictionary<string, uint>(src.Settings.Colors);
            m.Settings.ShowGrid = src.Settings.ShowGrid;
            m.Settings.ShowSessionLines = src.Settings.ShowSessionLines;
            m.Settings.ShowLastPriceLine = src.Settings.ShowLastPriceLine;
            m.Settings.ShowInfoPanel = src.Settings.ShowInfoPanel;
            m.Settings.ShowTradeMarks = src.Settings.ShowTradeMarks;
            m.ApplyColors();
            n++;
        }

        Workspace.Save(this);
        return n;
    }

    private readonly Dictionary<Guid, Window> _drawingWindows = new();

    /// <summary>Panel completo de un dibujo (doble clic o menú). Si ya está abierto, lo trae al frente.</summary>
    internal void EditDrawing(ChartTab chart, ChartDrawing d)
    {
        if (chart.Model == null) return;
        if (_drawingWindows.TryGetValue(d.Id, out var open))
        {
            open.Activate();
            return;
        }

        var owner = chart.Window ?? TopLevel.GetTopLevel(this) as Window;
        if (owner == null) return;
        var model = chart.Model;
        void Changed()
        {
            model.Invalidate();
            DrawingsTouched(chart);
        }

        var w = new DrawingSettingsWindow(_svc, d, model.Instrument, model.Period, Changed,
            delete: () =>
            {
                model.Drawings.Remove(d);
                Changed();
            },
            duplicate: () =>
            {
                var copy = d.Clone();
                double shift = model.LastLayout is { } l ? Math.Abs(l.Main.Top - l.Main.Bottom) * 0.04 : 0;
                copy.Points = copy.Points.Select(p => new ChartPoint(p.Time, p.Price + shift)).ToList();
                d.Selected = false;
                copy.Selected = true;
                model.Drawings.Add(copy);
                Changed();
            });
        _drawingWindows[d.Id] = w;
        w.Closed += (_, _) => _drawingWindows.Remove(d.Id);
        w.Show(owner);
    }

    internal void OpenFootprintEditor() => OpenFootprintEditor(_active);

    internal void OpenFootprintEditor(ChartTab? chart)
    {
        if (chart?.Model == null) return;
        var owner = chart.Window ?? TopLevel.GetTopLevel(this) as Window;
        if (owner == null) return;
        new FootprintEditorWindow(chart.Model, this).Show(owner);
    }

    private void ShowAlert(AlertInfo a)
    {
        _status($"🔔 {a.Source}: {a.Message}");
        Sound.Play();
    }

    internal async Task RestoreOrDefaultAsync()
    {
        try
        {
            if (await Workspace.RestoreAsync(this))
            {
                RestorePanelWindows();
                return;
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }

        var tab = await NewTabAsync(SimulatedFeed.FeedId, "SIM-BTC", ChartPeriod.M5, new ChartSettings { Layout = FootprintLayout.Presets[1].Clone() });
        tab.Model?.AddIndicator(new TradingHub.Indicators.Native.BigTrades());
        tab.Model?.AddIndicator(new TradingHub.Indicators.Native.DeltaBars());
        RestorePanelWindows();
    }

    // ===================================================================== ciclo (lo llama la ventana a 60 Hz)

    private readonly Dictionary<ChartDrawing, decimal> _alertLast = new();
    private int _frame;

    /// <summary>Actualiza los gráficos. Con la sección oculta, los de la ventana principal van más lento.</summary>
    internal void Tick(bool mainVisible = true)
    {
        _frame++;
        bool slowFrame = _frame % 15 == 0;
        var visible = new HashSet<ChartTab>(ReferenceEqualityComparer.Instance);
        if (mainVisible && _page != null)
            foreach (var c in _page.Charts.Take(PaneCount(_page.Layout))) visible.Add(c);

        foreach (var page in _pages)
        {
            if (!mainVisible && !slowFrame) break;
            foreach (var t in page.Charts)
            {
                if (t.Model == null) continue;
                t.Model.Update();
                CheckAlerts(t);
                if (t.Model.IsDirty && visible.Contains(t)) t.View.InvalidateVisual();
            }
        }

        foreach (var w in _floating)
        {
            var t = w.Chart;
            if (t.Model == null) continue;
            t.Model.Update();
            CheckAlerts(t);
            if (t.Model.IsDirty) t.View.InvalidateVisual();
            if (slowFrame) w.Toolbar.UpdateQuote();
        }

        if (slowFrame)
        {
            foreach (var c in AllCharts)
            {
                if (c.Trader != null && (c.Window != null || mainVisible)) c.Trader.Refresh();
                UpdateTradeMarks(c);
                UpdateStrategySignals(c);
            }

            if ((mainVisible || _panelWindows.ContainsKey("ticket")) && _dockedTrader is { IsEffectivelyVisible: true } docked) docked.Refresh();
        }

        if (!mainVisible && !slowFrame && !AnyPanelFloating) return;
        if (++_replayTick % 10 == 0 && _replayBar.IsVisible | ActiveReplay != null) UpdateReplayBar();

        var data = _dom.Dom?.Data;
        if (data != null && (data.Trades.Count != _lastTradeCount || !ReferenceEquals(data.Depth, _lastDepth)))
        {
            _lastTradeCount = data.Trades.Count;
            _lastDepth = data.Depth;
            _dom.InvalidateVisual();
            _tape.InvalidateVisual();
            UpdateQuote(data);
        }
    }

    private int _replayTick;

    private void CheckAlerts(ChartTab t)
    {
        var m = t.Model!;
        decimal last = m.Data.LastPrice;
        if (last == 0) return;
        foreach (var d in m.Drawings.Where(d => d.IsAlert && d.Points.Count > 0).ToList())
        {
            decimal level = (decimal)d.Points[0].Price;
            if (_alertLast.TryGetValue(d, out var prev) && prev != 0 && Math.Sign(prev - level) != Math.Sign(last - level))
            {
                d.Alert = false;
                if (d.Text == "alerta") d.Text = "";
                d.Dashed = false;
                d.LineStyle = LineStyleKind.Solid;
                DrawingsTouched(t);
                _status($"🔔 {t.Symbol} tocó {m.Instrument.FormatPrice(level)}");
                Sound.Play();
                m.Invalidate();
            }

            _alertLast[d] = last;
        }
    }
}

/// <summary>
/// Sonido de alerta del sistema: Glass de macOS (un .aiff, con afplay) o, en Windows, el sonido de notificación que tenga
/// elegido el usuario (PlaySound de winmm con el evento del sistema; con un .wav propio, ese archivo).
/// </summary>
internal static class Sound
{
    private const string MacAlert = "/System/Library/Sounds/Glass.aiff";

    public static void Play(string? file = null)
    {
        if (App.Services?.Settings.SoundAlerts == false) return;
        try
        {
            if (OperatingSystem.IsWindows()) PlayWindows(file);
            else System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("afplay", file ?? MacAlert) { UseShellExecute = false });
        }
        catch
        {
            // sin sonido
        }
    }

    private const uint SndAsync = 0x0001;
    private const uint SndNoDefault = 0x0002;
    private const uint SndAlias = 0x00010000;
    private const uint SndFilename = 0x00020000;

    /// <summary>El evento de sonido de Windows para las notificaciones («Notificación» en la configuración de sonido).</summary>
    internal const string WindowsAlertEvent = "Notification.Default";

    [System.Runtime.InteropServices.DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool PlaySound(string? sound, IntPtr module, uint flags);

    /// <summary>Sin esperar a que termine. Si el evento no tiene sonido, Windows toca el suyo por defecto.</summary>
    private static void PlayWindows(string? file)
    {
        if (file != null && file.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) && File.Exists(file)) PlaySound(file, IntPtr.Zero, SndFilename | SndAsync | SndNoDefault);
        else PlaySound(WindowsAlertEvent, IntPtr.Zero, SndAlias | SndAsync);
    }

    /// <summary>Para las pruebas en Windows: pide un evento que no existe y sin sonido por defecto (no suena nada).</summary>
    internal static bool ProbeWindows() => PlaySound("TradingHub.Prueba.NoExiste", IntPtr.Zero, SndAlias | SndAsync | SndNoDefault);
}
