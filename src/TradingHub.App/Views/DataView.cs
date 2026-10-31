using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Material.Icons;
using TradingHub.App.Controls;
using TradingHub.App.Services;
using TradingHub.App.Windows;
using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.App.Views;

/// <summary>
/// Datos históricos: qué hay guardado en la Mac (grabado en vivo o descargado), descargas de días
/// completos desde los archivos públicos de Binance/Bybit, borrado y replay de días.
/// </summary>
internal sealed class DataView : UserControl, IPage
{
    private readonly AppServices _svc;
    private readonly Action<string> _status;
    private readonly MainWindow _main;
    private readonly ListBox _symbols = new();
    private readonly ListBox _days = new() { SelectionMode = SelectionMode.Multiple };
    private readonly TextBlock _totals = new() { Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _daysTitle = new() { Classes = { "h2" } };
    private readonly TextBlock _daysInfo = new() { Classes = { "muted", "small" } };
    private readonly Control _daysPanel;
    private List<SymbolData> _index = new();
    private SymbolData? _selected;

    // descarga
    private readonly InstrumentPicker _picker;
    private string? _dlProfile;
    private Instrument? _dlInstrument;
    private readonly CalendarDatePicker _from = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CalendarDatePicker _to = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, Height = 6, IsVisible = false };
    private readonly TextBlock _dlStatus = new() { Classes = { "muted", "small" }, TextWrapping = TextWrapping.Wrap };
    private readonly Button _download;
    private readonly Button _cancel;
    private CancellationTokenSource? _cts;

    public DataView(AppServices svc, Action<string> status, MainWindow main)
    {
        _svc = svc;
        _status = status;
        _main = main;
        _picker = new InstrumentPicker(svc) { HorizontalAlignment = HorizontalAlignment.Stretch };
        _picker.Picked += (profileId, inst, _) =>
        {
            _dlProfile = profileId;
            _dlInstrument = inst;
            _picker.SetCurrent(profileId, inst);
            UpdateDownloadHint();
        };
        _download = Ui.Button("Descargar", MaterialIconKind.CloudDownloadOutline, () => _ = DownloadAsync(), "primary");
        _cancel = Ui.Button("Cancelar", MaterialIconKind.Close, () => _cts?.Cancel());
        _cancel.IsVisible = false;
        var today = DateTime.UtcNow.Date;
        _from.SelectedDate = today.AddDays(-7);
        _to.SelectedDate = today.AddDays(-1);
        _daysPanel = BuildDaysPanel();
        Content = Build();
        Refresh();
    }

    public void OnShown()
    {
        Refresh();

        // propone el instrumento del gráfico activo si se puede descargar
        if (_dlInstrument == null && _main.Trading.ActiveTab is { Model: { } m } tab && (PublicArchives.Supports(m.Instrument.FeedId) || DownloaderOf(tab.FeedId) != null))
        {
            _dlProfile = tab.FeedId;
            _dlInstrument = m.Instrument;
            _picker.SetCurrent(tab.FeedId, m.Instrument);
            UpdateDownloadHint();
        }
    }

    public void OnTimer()
    {
    }

    private Control Build()
    {
        var openFolder = Ui.Button("Abrir carpeta", MaterialIconKind.FolderOpenOutline, () => Shell.OpenFolder(AppPaths.Ticks));
        var refresh = Ui.IconButton(MaterialIconKind.Refresh, "Actualizar", Refresh);
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 18) };
        var actions = Ui.H(8, _totals, refresh, openFolder);
        DockPanel.SetDock(actions, Dock.Right);
        header.Children.Add(actions);
        header.Children.Add(Ui.V(4, Ui.H1("Datos"), Ui.Muted($"Historia guardada en tu {Os.Device}. Se usa al abrir gráficos, en el replay y en los backtests.")));

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("340,16,*,16,360") };

        // ---- instrumentos guardados
        var symCard = new DockPanel();
        var symHeader = Ui.H3("Guardado");
        symHeader.Margin = new Thickness(4, 0, 0, 10);
        DockPanel.SetDock(symHeader, Dock.Top);
        symCard.Children.Add(symHeader);
        _symbols.SelectionChanged += (_, _) =>
        {
            _selected = (_symbols.SelectedItem as ListBoxItem)?.Tag as SymbolData;
            FillDays();
        };
        _symbols.Background = Brushes.Transparent;
        symCard.Children.Add(_symbols);
        grid.Children.Add(Ui.Card(symCard, 12));

        var daysCard = Ui.Card(_daysPanel, 16);
        Grid.SetColumn(daysCard, 2);
        grid.Children.Add(daysCard);

        // a la derecha: bajar (exchanges y Rithmic) e importar archivos (CFDs, forex…)
        var right = new ScrollViewer { Content = Ui.V(16, Ui.Card(BuildDownloadPanel(), 16), Ui.Card(BuildDatabentoPanel(), 16), Ui.Card(BuildImportPanel(), 16)) };
        Grid.SetColumn(right, 4);
        grid.Children.Add(right);

        var root = new DockPanel { Margin = new Thickness(28, 24) };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(grid);
        return root;
    }

    private Control BuildDaysPanel()
    {
        var replay = Ui.Button("Reproducir", MaterialIconKind.PlayCircleOutline, () => _ = ReplaySelectedAsync());
        ToolTip.SetTip(replay, "Abrir el día seleccionado como replay en Trading (operás con la cuenta simulada)");
        var delete = Ui.Button("Borrar", MaterialIconKind.DeleteOutline, () => _ = DeleteSelectedAsync(), "ghost");
        var actions = Ui.H(8, delete, replay);
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(actions, Dock.Right);
        head.Children.Add(actions);
        head.Children.Add(Ui.V(3, _daysTitle, _daysInfo));
        var panel = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        panel.Children.Add(head);
        _days.Background = Brushes.Transparent;
        _days.DoubleTapped += (_, _) => _ = ReplaySelectedAsync();
        panel.Children.Add(_days);
        return panel;
    }

    private Control BuildDownloadPanel()
    {
        (DateTime? From, DateTime? To) range = (_from.SelectedDate, _to.SelectedDate);
        // cuánto bajar: atajos o cualquier rango con Desde / Hasta
        var presets = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
        var chips = new List<Avalonia.Controls.Primitives.ToggleButton>();
        foreach (var (days, label) in new[] { (1, "Ayer"), (7, "7 días"), (30, "30 días"), (90, "90 días"), (182, "6 meses"), (365, "1 año"), (730, "2 años") })
        {
            var b = new Avalonia.Controls.Primitives.ToggleButton { Content = label, Padding = new Thickness(10, 3), MinHeight = 26, FontSize = 12, IsChecked = days == 7 };
            b.Click += (_, _) =>
            {
                var today = DateTime.UtcNow.Date;
                _from.SelectedDate = today.AddDays(-days);
                _to.SelectedDate = today.AddDays(-1);
                range = (_from.SelectedDate, _to.SelectedDate);
                foreach (var c in chips) c.IsChecked = ReferenceEquals(c, b);
                UpdateDownloadHint();
            };
            chips.Add(b);
            presets.Children.Add(b);
        }

        // si se cambia una fecha a mano, ningún atajo queda marcado (el selector avisa también sin cambios: se ignora)
        void Custom()
        {
            if ((_from.SelectedDate, _to.SelectedDate) == range) return;
            range = (_from.SelectedDate, _to.SelectedDate);
            foreach (var c in chips) c.IsChecked = false;
            if (_cts == null || _cts.IsCancellationRequested) UpdateDownloadHint();
        }

        _from.SelectedDateChanged += (_, _) => Custom();
        _to.SelectedDateChanged += (_, _) => Custom();

        var buttons = Ui.Row(10, _cancel, _download);
        _cancel.IsVisible = true;
        _cancel.IsEnabled = false;
        return Ui.V(14,
            Ui.H2("Descargar historia"),
            new TextBlock { Text = "Días completos: de los archivos públicos (Binance, Bybit) o por la API de tus conexiones (Rithmic, Tradovate, cTrader, OANDA, Alpaca). En futuros, con el contrato que operaba cada día.", Classes = { "muted" }, TextWrapping = TextWrapping.Wrap },
            Ui.Field("Instrumento", _picker),
            Ui.Field("Cuánto (días UTC)", presets),
            Ui.Row(10, Ui.Field("Desde", _from), Ui.Field("Hasta", _to)),
            buttons,
            _progress,
            _dlStatus,
            Ui.Divider(),
            Note(MaterialIconKind.RecordCircleOutline, "Todo lo que mirás en vivo se graba solo, también Rithmic. Los días ya bajados no se vuelven a bajar: si cancelás, la próxima vez sigue donde quedó."));
    }

    // ===================================================================== importar

    private readonly TextBox _importSymbol = new() { Watermark = "Se deduce del nombre del archivo (US100, US500…)" };
    private readonly ComboBox _importZone = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ProgressBar _importProgress = new() { Minimum = 0, Maximum = 1, Height = 6, IsVisible = false };
    private readonly TextBlock _importStatus = new() { Classes = { "muted", "small" }, TextWrapping = TextWrapping.Wrap };
    private Button? _importButton;

    private static readonly (string Label, double Hours)[] Zones =
    {
        ("UTC (Dukascopy, TradingView)", 0),
        ("Nueva York fija, −5 (HistData)", -5),
        ("UTC+2 (MetaTrader en invierno)", 2),
        ("UTC+3 (MetaTrader en verano)", 3),
    };

    private Control BuildImportPanel()
    {
        foreach (var (label, _) in Zones) _importZone.Items.Add(new ComboBoxItem { Content = label });
        _importZone.SelectedIndex = 0;
        _importButton = Ui.Button("Importar archivos…", MaterialIconKind.FileImportOutline, () => _ = ImportAsync(), "primary");
        var duka = Ui.Button("Dukascopy", MaterialIconKind.OpenInNew, () => Shell.Open("https://www.dukascopy.com/swiss/english/marketwatch/historical/"));
        ToolTip.SetTip(duka, "Historia gratis (con cuenta gratis): US100 = USA Tech, US500, US30, DAX, oro, forex… Elegí ticks o velas de 1 minuto y exportá en CSV");
        var histdata = Ui.Button("HistData", MaterialIconKind.OpenInNew, () => Shell.Open("https://www.histdata.com/download-free-forex-data/"));
        ToolTip.SetTip(histdata, "Forex e índices (NSXUSD = Nasdaq 100, SPXUSD = S&P 500) en velas de 1 minuto o ticks, gratis");
        return Ui.V(14,
            Ui.H2("Importar archivos"),
            new TextBlock { Text = "Para CFDs (US100, US500, DAX), forex, oro… Bajá el CSV gratis de Dukascopy o HistData, o exportalo de MetaTrader o TradingView, y Trading Hub lo convierte en historia para gráficos, replay y backtests. El formato se reconoce solo.", Classes = { "muted" }, TextWrapping = TextWrapping.Wrap },
            Ui.H(8, duka, histdata),
            Ui.Field("Nombre del instrumento", _importSymbol),
            Ui.Field("Hora del archivo", _importZone),
            _importButton,
            _importProgress,
            _importStatus,
            Note(MaterialIconKind.InformationOutline, "Los CFDs no tienen volumen real: cada cotización cuenta como una operación (volumen de ticks) y el lado sale de si el precio subió o bajó. Sirve para precio, velas y backtests de precio; el footprint y el delta son aproximados."));
    }

    // ===================================================================== Databento (futuros de CME, historia larga)

    private readonly ComboBox _dbRoot = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CalendarDatePicker _dbFrom = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CalendarDatePicker _dbTo = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _dbStatus = new() { Classes = { "muted", "small" }, TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _dbProgress = new() { Minimum = 0, Maximum = 1, Height = 6, IsVisible = false };
    private readonly CheckBox _dbAlsoRithmic = new() { Content = "Bajar también los días que ya tiene Rithmic" };
    private readonly TextBlock _dbHave = new() { Classes = { "small" }, TextWrapping = TextWrapping.Wrap };
    private Button? _dbCost, _dbDownload, _dbCancel;
    private decimal? _dbQuote;
    private string _dbQuoteKey = "";
    private CancellationTokenSource? _dbCts;

    private ConnectionProfile? DatabentoProfile => _svc.Connections.Enabled.FirstOrDefault(p => p.Kind == ConnectionKind.Databento && !string.IsNullOrEmpty(_svc.Connections.GetSecret(p)));

    private Control BuildDatabentoPanel()
    {
        foreach (var root in new[] { "NQ", "ES", "MNQ", "MES", "YM", "RTY", "CL", "GC", "MCL", "MGC", "ZN", "6E" })
            if (TradingHub.Feeds.Rithmic.RithmicFeed.SpecFor(root) is { } spec)
                _dbRoot.Items.Add(new ComboBoxItem { Content = $"{root} · {spec.Name}", Tag = root });
        _dbRoot.SelectedIndex = 0;
        var today = DateTime.UtcNow.Date;
        _dbFrom.SelectedDate = today.AddMonths(-3);
        _dbTo.SelectedDate = today.AddDays(-1);
        _dbCost = Ui.Button("Calcular costo", MaterialIconKind.Calculator, () => _ = DatabentoQuoteAsync());
        _dbDownload = Ui.Button("Descargar", MaterialIconKind.CloudDownloadOutline, () => _ = DatabentoDownloadAsync(), "primary");
        _dbCancel = Ui.Button("Cancelar", MaterialIconKind.Close, () => _dbCts?.Cancel());
        _dbDownload.IsEnabled = false;
        _dbCancel.IsVisible = false;
        void Reset()
        {
            _dbQuote = null;
            if (_dbDownload != null) _dbDownload.IsEnabled = false;
            _dbStatus.Text = DatabentoProfile == null ? "Primero agregá la conexión «Databento» (con tu API key) en Conexiones." : "Calculá el costo del rango antes de bajar.";
        }

        _dbRoot.SelectionChanged += (_, _) => Reset();
        _dbFrom.SelectedDateChanged += (_, _) => Reset();
        _dbTo.SelectedDateChanged += (_, _) => Reset();
        _dbAlsoRithmic.IsCheckedChanged += (_, _) => Reset();
        ToolTip.SetTip(_dbAlsoRithmic, "Sin tildar, Databento solo completa lo que le falta a Rithmic (más barato). Tildado, baja todo el rango para tener la historia de Databento entera.");
        var configure = Ui.Button("Conexiones", MaterialIconKind.LanConnect, () => _main.Navigate("connections"), "ghost");
        Reset();
        return Ui.V(14,
            Ui.H2("Futuros de CME con Databento"),
            new TextBlock { Text = "Años de historia tick a tick (desde 2010) con el lado agresor real de cada operación: lo que Rithmic no guarda. Es pago por uso (las cuentas nuevas traen créditos gratis): Trading Hub calcula el costo antes y solo baja si confirmás.", Classes = { "muted" }, TextWrapping = TextWrapping.Wrap },
            _dbHave,
            Ui.Field("Contrato continuo", _dbRoot),
            Ui.Row(10, Ui.Field("Desde", _dbFrom), Ui.Field("Hasta", _dbTo)),
            _dbAlsoRithmic,
            new WrapPanel { ItemSpacing = 8, LineSpacing = 8, Children = { _dbCost, _dbDownload, _dbCancel, configure } },
            _dbProgress,
            _dbStatus,
            Note(MaterialIconKind.InformationOutline, "Se guarda aparte, como «Databento» en la lista de la izquierda. En los backtests elegís solo Databento, solo Rithmic o las dos combinadas (Rithmic primero y Databento completa lo que le falta). Los días que ya tenés completos no se vuelven a bajar ni se cobran."));
    }

    private string DbRoot => (_dbRoot.SelectedItem as ComboBoxItem)?.Tag as string ?? "NQ";

    private static Instrument CmeInstrument(string root)
    {
        var spec = TradingHub.Feeds.Rithmic.RithmicFeed.SpecFor(root);
        return new Instrument { Symbol = root, FeedId = HistorySources.Databento, Exchange = "CME", TickSize = spec?.Tick ?? 0.25m, PointValue = spec?.PointValue ?? 1, VolumeStep = 1 };
    }

    /// <summary>
    /// Días que faltan en el rango (sin sábados): los que ya bajó Databento no se vuelven a bajar, y los que Rithmic tiene
    /// completos tampoco, salvo que se pida la historia de Databento entera.
    /// </summary>
    private List<DateOnly> DatabentoDays()
    {
        var from = DateOnly.FromDateTime(_dbFrom.SelectedDate ?? DateTime.UtcNow.Date.AddMonths(-3));
        var to = DateOnly.FromDateTime(_dbTo.SelectedDate ?? DateTime.UtcNow.Date.AddDays(-1));
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
        if (to > yesterday) to = yesterday;
        var days = new List<DateOnly>();
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            if (d.DayOfWeek == DayOfWeek.Saturday) continue;
            var dayEnd = d.ToDateTime(TimeOnly.MinValue).AddDays(1.2);
            var own = TickFile.DayPath(AppPaths.Ticks, HistorySources.Databento, DbRoot, d);
            if (File.Exists(own) && File.GetLastWriteTimeUtc(own) > dayEnd) continue;
            var rithmic = TickFile.DayPath(AppPaths.Ticks, TradingHub.Feeds.Rithmic.RithmicFeed.FeedId, DbRoot, d);
            bool rithmicComplete = File.Exists(rithmic) && File.GetLastWriteTimeUtc(rithmic) > dayEnd && new FileInfo(rithmic).Length > 200_000;
            if (rithmicComplete && _dbAlsoRithmic.IsChecked != true) continue;
            days.Add(d);
        }

        return days;
    }

    /// <summary>Tramos seguidos de días (para pedir el costo en pocos pedidos).</summary>
    private static List<(DateOnly From, DateOnly To)> Runs(List<DateOnly> days)
    {
        var runs = new List<(DateOnly, DateOnly)>();
        foreach (var d in days)
        {
            if (runs.Count > 0 && runs[^1].Item2.AddDays(d.DayOfWeek == DayOfWeek.Sunday ? 2 : 1) >= d) runs[^1] = (runs[^1].Item1, d);
            else runs.Add((d, d));
        }

        return runs;
    }

    private async Task DatabentoQuoteAsync()
    {
        if (DatabentoProfile is not { } profile)
        {
            _dbStatus.Text = "Primero agregá la conexión «Databento» (con tu API key) en Conexiones.";
            return;
        }

        var days = DatabentoDays();
        if (days.Count == 0)
        {
            _dbStatus.Text = "Ya tenés completos todos los días de ese rango.";
            return;
        }

        _dbCost!.IsEnabled = false;
        _dbStatus.Text = "Calculando el costo…";
        try
        {
            using var client = _svc.Connections.DatabentoOf(profile);
            decimal total = 0;
            foreach (var (from, to) in Runs(days)) total += await client.CostAsync(DbRoot, from, to, CancellationToken.None);
            _dbQuote = total;
            _dbQuoteKey = $"{DbRoot}|{days.First()}|{days.Last()}|{days.Count}";
            _dbDownload!.IsEnabled = true;
            _dbStatus.Text = $"{DbRoot}: {days.Count} día(s) por bajar ({days.First():dd/MM/yyyy} → {days.Last():dd/MM/yyyy}) · costo ≈ US$ {total.ToString("0.00", CultureInfo.InvariantCulture)} de tus créditos de Databento.";
        }
        catch (Exception ex)
        {
            _dbStatus.Text = "No se pudo calcular: " + ex.Message;
        }
        finally
        {
            _dbCost.IsEnabled = true;
        }
    }

    private async Task DatabentoDownloadAsync()
    {
        if (DatabentoProfile is not { } profile || _dbQuote is not { } quote) return;
        var days = DatabentoDays();
        if (days.Count == 0 || $"{DbRoot}|{days.First()}|{days.Last()}|{days.Count}" != _dbQuoteKey)
        {
            _dbStatus.Text = "El rango cambió: volvé a calcular el costo.";
            _dbDownload!.IsEnabled = false;
            return;
        }

        string cost = quote.ToString("0.00", CultureInfo.InvariantCulture);
        if (!await ConfirmDialog.ShowAsync(_main, "Bajar historia de Databento",
                $"Se bajan {days.Count} día(s) de {DbRoot} tick a tick. Databento cobra ≈ US$ {cost} de tus créditos (si no te alcanzan, a tu medio de pago en Databento).",
                $"Bajar (≈ US$ {cost})")) return;

        var inst = CmeInstrument(DbRoot);
        _dbCts = new CancellationTokenSource();
        var ct = _dbCts.Token;
        using var job = BackgroundWork.Begin($"bajando historia de {DbRoot} de Databento");
        _dbDownload!.IsEnabled = false;
        _dbCost!.IsEnabled = false;
        _dbCancel!.IsVisible = true;
        _dbProgress.IsVisible = true;
        int done = 0, empty = 0;
        long trades = 0;
        try
        {
            using var client = _svc.Connections.DatabentoOf(profile);
            for (int i = 0; i < days.Count; i++)
            {
                var day = days[i];
                int index = i;
                _dbStatus.Text = $"{DbRoot} {day:dd/MM/yyyy}: descargando… ({i + 1}/{days.Count})";
                var progress = new Progress<double>(p => _dbProgress.Value = (index + p) / days.Count);
                var list = await client.DownloadDayAsync(inst, day, progress, ct);
                if (list == null)
                {
                    empty++;
                    continue;
                }

                _dbStatus.Text = $"{DbRoot} {day:dd/MM/yyyy}: guardando {list.Count:N0} operaciones…";
                await Task.Run(() => HistoricalData.WriteDay(AppPaths.Ticks, inst, day, list, replace: true), ct);
                trades += list.Count;
                done++;
            }

            _dbStatus.Text = $"Listo: {done} día(s) de {DbRoot} ({trades:N0} operaciones con su lado agresor)" + (empty > 0 ? $", {empty} sin operaciones (feriados)" : "") + ".";
            _status(_dbStatus.Text);
        }
        catch (OperationCanceledException)
        {
            _dbStatus.Text = $"Cancelado. Se guardaron {done} día(s).";
        }
        catch (Exception ex)
        {
            _dbStatus.Text = "Error: " + ex.Message;
            CrashLog.Write(ex);
        }
        finally
        {
            _dbQuote = null;
            _dbCost.IsEnabled = true;
            _dbCancel.IsVisible = false;
            _dbProgress.IsVisible = false;
            _dbCts = null;
            Dispatcher.UIThread.Post(Refresh);
        }
    }

    /// <summary>Nombre del instrumento según el archivo (USATECHIDXUSD_Ticks… → US100).</summary>
    private static string GuessSymbol(string file)
    {
        string n = Path.GetFileNameWithoutExtension(file).ToUpperInvariant();
        if (n.Contains("USATECH") || n.Contains("NAS100") || n.Contains("NSX") || n.Contains("US100") || n.Contains("USTEC") || n.Contains("NDX")) return "US100";
        if (n.Contains("USA500") || n.Contains("SPX") || n.Contains("US500")) return "US500";
        if (n.Contains("USA30") || n.Contains("US30") || n.Contains("DJI") || n.Contains("WS30")) return "US30";
        if (n.Contains("DEU") || n.Contains("GER") || n.Contains("DAX")) return "GER40";
        if (n.Contains("XAU") || n.Contains("GOLD")) return "XAUUSD";
        foreach (var fx in new[] { "EURUSD", "GBPUSD", "USDJPY", "AUDUSD", "USDCAD", "USDCHF", "NZDUSD", "XAGUSD", "BTCUSD" })
            if (n.Replace("/", "").Replace("_", "").Contains(fx)) return fx;
        var first = n.Split('_', '.', ' ', '-')[0];
        return first.Length > 0 ? first : "IMPORTADO";
    }

    private async Task ImportAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Elegí los archivos de historia (CSV o TXT)",
            AllowMultiple = true,
            FileTypeFilter = new[] { new Avalonia.Platform.Storage.FilePickerFileType("Historia (CSV, TXT)") { Patterns = new[] { "*.csv", "*.txt" } } },
        });
        if (files.Count == 0) return;
        var offset = TimeSpan.FromHours(Zones[Math.Max(0, _importZone.SelectedIndex)].Hours);
        string typed = (_importSymbol.Text ?? "").Trim().ToUpperInvariant();
        _importButton!.IsEnabled = false;
        _importProgress.IsVisible = true;
        var lines = new List<string>();
        try
        {
            for (int i = 0; i < files.Count; i++)
            {
                var path = files[i].TryGetLocalPath();
                if (path == null) continue;
                string symbol = typed.Length > 0 ? typed : GuessSymbol(path);
                _importStatus.Text = $"{Path.GetFileName(path)} → {symbol}: leyendo… ({i + 1}/{files.Count})";
                int index = i;
                var progress = new Progress<double>(p => _importProgress.Value = (index + p) / files.Count);
                try
                {
                    var r = await Task.Run(() => CsvImport.Import(path, symbol, AppPaths.Ticks, offset, null, progress, CancellationToken.None));
                    lines.Add($"{symbol}: {r.Kind}, {r.Trades:N0} operaciones en {r.Days} día(s)" +
                              (r.From is { } f && r.To is { } t ? $" ({f:dd/MM/yyyy} → {t:dd/MM/yyyy})" : "") +
                              (r.BadLines > 0 ? $", {r.BadLines:N0} filas que no se pudieron leer" : "") + $" · paso de precio {r.TickSize.ToString("0.########", CultureInfo.InvariantCulture)}");
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    lines.Add($"{Path.GetFileName(path)}: {ex.Message}");
                }
            }

            _importStatus.Text = "Listo. " + string.Join(" · ", lines) + ". Aparece en la lista de la izquierda (conexión «importado») y en los backtests.";
            _status(_importStatus.Text);
        }
        finally
        {
            _importButton.IsEnabled = true;
            _importProgress.IsVisible = false;
            Refresh();
        }
    }

    private static Control Note(MaterialIconKind icon, string text)
    {
        var d = new DockPanel();
        var i = Ui.Icon(icon, 16, Ui.Text2);
        i.Margin = new Thickness(0, 1, 8, 0);
        i.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(i, Dock.Left);
        d.Children.Add(i);
        d.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Classes = { "faint", "small" } });
        return d;
    }

    private bool DownloadsFromRithmic => _dlProfile != null && _svc.Connections.Find(_dlProfile)?.IsRithmic == true;

    /// <summary>La conexión baja días completos por su API (Rithmic, Tradovate, cTrader, OANDA, Alpaca); null si no.</summary>
    private TradingHub.Feeds.IDayDownloader? DownloaderOf(string? profileId)
    {
        if (profileId == null || _svc.Connections.Find(profileId) is not { Enabled: true, HasMarketData: true } p) return null;
        if (PublicArchives.Supports(p.MarketDataId)) return null; // los archivos públicos son más rápidos
        try
        {
            return _svc.Connections.GetFeed(p.Id) as TradingHub.Feeds.IDayDownloader;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private TradingHub.Feeds.IDayDownloader? Downloader => DownloaderOf(_dlProfile);

    private bool CanDownload => _dlInstrument != null && (PublicArchives.Supports(_dlInstrument.FeedId) || Downloader != null);

    /// <summary>Días a bajar del rango elegido (en Rithmic se saltean los sábados: no hay mercado).</summary>
    private List<DateOnly> RangeDays()
    {
        var from = DateOnly.FromDateTime(_from.SelectedDate ?? DateTime.UtcNow.Date.AddDays(-7));
        var to = DateOnly.FromDateTime(_to.SelectedDate ?? DateTime.UtcNow.Date.AddDays(-1));
        var todayUtc = DateOnly.FromDateTime(DateTime.UtcNow);
        if (to >= todayUtc) to = todayUtc.AddDays(-1);
        var days = new List<DateOnly>();
        bool skipSaturdays = Downloader?.ClosedOnSaturdays == true;
        for (var d = from; d <= to; d = d.AddDays(1))
            if (!(skipSaturdays && d.DayOfWeek == DayOfWeek.Saturday)) days.Add(d);
        return days;
    }

    private void UpdateDownloadHint()
    {
        if (_dlInstrument == null)
        {
            _dlStatus.Text = "Elegí un instrumento de Binance, Bybit, Rithmic, Tradovate, cTrader, OANDA o Alpaca.";
            return;
        }

        _download.IsEnabled = CanDownload;
        if (!CanDownload)
        {
            _dlStatus.Text = "Esta conexión no da historia para bajar: sus datos se graban cuando los mirás en vivo.";
            return;
        }

        var days = RangeDays();
        if (DownloadsFromRithmic)
        {
            // qué contratos entran en el rango (ESU6, ESZ6…)
            var contracts = days.Select(d => TradingHub.Feeds.Rithmic.RithmicFeed.ContractNameOn(_dlInstrument, d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))).Distinct().ToList();
            var since = TradingHub.Feeds.Rithmic.RithmicFeed.CurrentContractSince(_dlInstrument);
            bool older = since is { } sd && days.Count > 0 && days[0].ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) < sd;
            _dlStatus.Text = $"{_dlInstrument.Symbol} · {days.Count} días tick a tick desde Rithmic · contrato{(contracts.Count > 1 ? "s" : "")} {string.Join(", ", contracts)}" +
                             (older ? $". Ojo: Rithmic no guarda la historia de los contratos vencidos; lo seguro es desde el {since:dd/MM} (contrato {contracts[^1]}). Lo anterior se intenta igual." : "") +
                             (days.Count > 60 ? " Un índice como el ES tiene más de un millón de operaciones por día: un rango largo tarda (se puede cancelar y seguir después)." : "");
        }
        else if (Downloader is { } dl)
        {
            _dlStatus.Text = $"{_dlInstrument.Symbol} · {days.Count} días · {dl.HistoryNote}" +
                             (days.Count > 30 ? ". Un rango largo tarda: se puede cancelar y seguir después (lo bajado queda)." : "");
        }
        else
        {
            _dlStatus.Text = $"{_dlInstrument.Symbol} · {days.Count} días · archivos diarios de {_dlInstrument.Exchange}";
        }
    }

    // ===================================================================== índice

    private static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        _ => $"{Math.Max(1, bytes / 1024)} KB",
    };

    private string FeedName(string feedId) => _svc.Connections.Find(feedId)?.Name ?? (feedId == HistorySources.Databento ? "Databento" : feedId);

    /// <summary>Lo que ya se bajó de Databento (para que se vea en su tarjeta).</summary>
    private void RefreshDatabentoSummary()
    {
        var have = _index.Where(s => s.FeedId == HistorySources.Databento).OrderBy(s => s.Symbol).ToList();
        _dbHave.Text = have.Count == 0
            ? "Todavía no bajaste nada de Databento."
            : "Ya tenés de Databento: " + string.Join(" · ", have.Select(s => $"{s.Symbol} {s.Days.Count} día{(s.Days.Count == 1 ? "" : "s")} ({s.First:dd/MM/yyyy} → {s.Last:dd/MM/yyyy})"));
    }

    private void Refresh()
    {
        _index = HistoricalData.Scan(AppPaths.Ticks);
        _totals.Text = $"{_index.Count} instrumentos · {Size(_index.Sum(s => s.Bytes))}";
        RefreshDatabentoSummary();
        var keep = _selected;
        _symbols.Items.Clear();
        foreach (var s in _index)
        {
            var text = Ui.V(2,
                new TextBlock { Text = s.Symbol, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = $"{FeedName(s.FeedId)} · {s.Days.Count} día{(s.Days.Count == 1 ? "" : "s")}", Classes = { "muted", "small" } });
            var size = new TextBlock { Text = Size(s.Bytes), Classes = { "faint", "small" }, VerticalAlignment = VerticalAlignment.Center };
            var row = new DockPanel { Margin = new Thickness(2, 4) };
            DockPanel.SetDock(size, Dock.Right);
            row.Children.Add(size);
            row.Children.Add(text);
            _symbols.Items.Add(new ListBoxItem { Content = row, Tag = s });
        }

        if (_index.Count == 0)
            _symbols.Items.Add(new ListBoxItem { Content = new TextBlock { Text = "Todavía no hay datos guardados. Abrí un gráfico o descargá días.", TextWrapping = TextWrapping.Wrap, Classes = { "muted", "small" } }, IsEnabled = false });

        _symbols.SelectedItem = _symbols.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag is SymbolData sd && keep != null && sd.FeedId == keep.FeedId && sd.Symbol == keep.Symbol)
                                ?? _symbols.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag is SymbolData);
        FillDays();
    }

    private void FillDays()
    {
        _days.Items.Clear();
        _daysPanel.IsVisible = true;
        if (_selected == null)
        {
            _daysTitle.Text = "Días";
            _daysInfo.Text = "Elegí un instrumento";
            return;
        }

        _daysTitle.Text = $"{_selected.Symbol} · {FeedName(_selected.FeedId)}";
        _daysInfo.Text = $"{_selected.First:dd/MM/yyyy} → {_selected.Last:dd/MM/yyyy} · {Size(_selected.Bytes)}";
        long max = _selected.Days.Max(d => d.Bytes);
        foreach (var d in _selected.Days.OrderByDescending(d => d.Day))
        {
            var date = new TextBlock { Text = d.Day.ToDateTime(TimeOnly.MinValue).ToString("ddd dd/MM/yyyy", TradingHub.Common.UiCulture.Current), Width = 130, VerticalAlignment = VerticalAlignment.Center };
            var bar = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = Ui.Accent, Opacity = 0.75, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Width = Math.Max(4, 220.0 * d.Bytes / Math.Max(1, max)) };
            ToolTip.SetTip(bar, "Tamaño relativo (≈ cantidad de operaciones)");
            var size = new TextBlock { Text = Size(d.Bytes), Classes = { "muted", "small" }, Width = 70, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
            var row = new DockPanel { Margin = new Thickness(2, 3) };
            DockPanel.SetDock(date, Dock.Left);
            DockPanel.SetDock(size, Dock.Right);
            row.Children.Add(date);
            row.Children.Add(size);
            row.Children.Add(bar);
            _days.Items.Add(new ListBoxItem { Content = row, Tag = d });
        }
    }

    private List<DayData> SelectedDays() => _days.SelectedItems?.OfType<ListBoxItem>().Select(i => (DayData)i.Tag!).ToList() ?? new();

    private async Task DeleteSelectedAsync()
    {
        var days = SelectedDays();
        if (_selected == null || days.Count == 0)
        {
            _status(Os.IsWindows ? "Elegí uno o más días para borrar (Ctrl o Shift para seleccionar varios)." : "Elegí uno o más días para borrar (⌘ o ⇧ para seleccionar varios).");
            return;
        }

        if (!await ConfirmDialog.ShowAsync(_main, "Borrar datos", $"Se borran {days.Count} día(s) de {_selected.Symbol} ({Size(days.Sum(d => d.Bytes))}). No se puede deshacer.", "Borrar", danger: true)) return;
        HistoricalData.DeleteDays(AppPaths.Ticks, _selected.FeedId, _selected.Symbol, days.Select(d => d.Day));
        _status($"Borrados {days.Count} día(s) de {_selected.Symbol}");
        Refresh();
    }

    private async Task ReplaySelectedAsync()
    {
        var days = SelectedDays();
        if (_selected == null || days.Count == 0)
        {
            _status("Elegí el día que querés reproducir.");
            return;
        }

        var day = days.Min(d => d.Day);
        var lastDay = days.Max(d => d.Day);
        Instrument? inst = null;
        if (_selected.FeedId == HistorySources.Databento) inst = MarketInfo.FuturesInstrument(_selected.FeedId, _selected.Symbol);
        else
        {
            try
            {
                var instruments = await InstrumentPicker.InstrumentsAsync(_svc, _selected.FeedId);
                inst = instruments.FirstOrDefault(i => i.Symbol == _selected.Symbol);
            }
            catch (Exception)
            {
                // sin la conexión: los futuros igual se arman con el catálogo de CME
            }

            if (inst == null && MarketInfo.IsFutures(_selected.FeedId)) inst = MarketInfo.FuturesInstrument(_selected.FeedId, _selected.Symbol);
        }

        if (inst == null)
        {
            _status($"No pude obtener la ficha de {_selected.Symbol} (¿la conexión está activa?).");
            return;
        }

        // arranca con el primer dato del día
        var start = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var first = HistoricalData.Read(AppPaths.Ticks, _selected.FeedId, _selected.Symbol, start, start.AddDays(1)).FirstOrDefault();
        if (first.Time > start) start = first.Time;
        var end = lastDay.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddDays(1);
        _main.Navigate("trading");
        await _main.Trading.OpenReplayAsync(inst, start, end, _main.Trading.ActiveTab?.Model?.Period ?? ChartPeriod.M5);
    }

    // ===================================================================== descargas

    private async Task DownloadAsync()
    {
        if (_dlInstrument == null || _dlProfile == null)
        {
            _picker.Open();
            return;
        }

        if (!CanDownload)
        {
            UpdateDownloadHint();
            return;
        }

        var days = RangeDays();
        if (days.Count == 0)
        {
            _dlStatus.Text = "El rango no tiene días completos (el de hoy todavía no terminó).";
            return;
        }

        var rithmic = DownloadsFromRithmic ? _svc.Connections.GetFeed(_dlProfile!) as TradingHub.Feeds.Rithmic.RithmicFeed : null;
        rithmic?.ClearMissingContracts();
        var downloader = Downloader;
        var started = DateTime.UtcNow;
        int fetched = 0;
        string? lastProblem = null;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _download.IsEnabled = false;
        _cancel.IsEnabled = true;
        _progress.IsVisible = true;
        _progress.Value = 0;
        int done = 0, skipped = 0, missing = 0;
        long trades = 0;
        var inst = _dlInstrument;
        using var job = BackgroundWork.Begin($"bajando historia de {inst.Symbol}");
        try
        {
            for (int i = 0; i < days.Count; i++)
            {
                var day = days[i];
                int index = i;
                // un día ya completo (descargado antes) no se vuelve a bajar
                var path = TickFile.DayPath(AppPaths.Ticks, inst.FeedId, inst.Symbol, day);
                if (File.Exists(path) && File.GetLastWriteTimeUtc(path) > day.ToDateTime(TimeOnly.MinValue).AddDays(1.2) && new FileInfo(path).Length > 200_000)
                {
                    skipped++;
                    continue;
                }

                // tiempo que falta, con lo que tardaron los días ya bajados
                string eta = "";
                if (fetched > 0)
                {
                    var left = TimeSpan.FromTicks((DateTime.UtcNow - started).Ticks / fetched * (days.Count - i));
                    eta = left.TotalMinutes < 1 ? " · falta menos de un minuto" : left.TotalHours < 1 ? $" · faltan ~{left.TotalMinutes:0} min" : $" · faltan ~{left.TotalHours:0.#} h";
                }

                string contract = rithmic != null ? $" ({TradingHub.Feeds.Rithmic.RithmicFeed.ContractNameOn(inst, day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))})" : "";
                _dlStatus.Text = $"{inst.Symbol} {day:dd/MM/yyyy}{contract}: descargando… ({i + 1}/{days.Count}){eta}";
                var progress = new Progress<double>(p => _progress.Value = (index + p) / days.Count);
                var list = downloader != null
                    ? await downloader.DownloadDayAsync(inst, day, progress, ct)
                    : await PublicArchives.DownloadDayAsync(inst, day, progress, ct);
                fetched++;
                if (list == null)
                {
                    missing++;
                    if (rithmic?.LastHistoryError is { } err) lastProblem = $"{day:dd/MM}{contract}: Rithmic respondió {err}";
                    continue;
                }

                _dlStatus.Text = $"{inst.Symbol} {day:dd/MM/yyyy}{contract}: guardando {list.Count:N0} operaciones…";
                bool replace = downloader != null; // el día bajado por la API viene completo: reemplaza lo grabado en vivo
                await Task.Run(() => HistoricalData.WriteDay(AppPaths.Ticks, inst, day, list, replace), ct);
                trades += list.Count;
                done++;
                _progress.Value = (i + 1.0) / days.Count;
            }

            _dlStatus.Text = $"Listo: {done} día(s) descargados ({trades:N0} operaciones)" +
                             (skipped > 0 ? $", {skipped} ya estaban" : "") +
                             (missing > 0 ? rithmic != null ? $", {missing} sin operaciones en Rithmic (feriados o fuera de lo que guarda)" : downloader != null ? $", {missing} sin datos (feriados o fin de semana)" : $", {missing} todavía no publicados" : "") + "." +
                             (lastProblem != null ? " " + lastProblem : "");
            if (rithmic?.MissingContracts is { Count: > 0 } gone)
                _dlStatus.Text += $" Rithmic no devolvió nada de {string.Join(", ", gone)}: no guarda la historia de los contratos vencidos, así que con Rithmic se puede bajar desde el último vencimiento. Lo que bajes o grabes queda guardado en tu {Os.Device}, así que la historia se va juntando.";
            _status(_dlStatus.Text);
        }
        catch (OperationCanceledException)
        {
            _dlStatus.Text = $"Cancelado. Se guardaron {done} día(s).";
        }
        catch (Exception ex)
        {
            _dlStatus.Text = "Error: " + ex.Message;
            CrashLog.Write(ex);
        }
        finally
        {
            _download.IsEnabled = true;
            _cancel.IsEnabled = false;
            _progress.IsVisible = false;
            _cts = null;
            Dispatcher.UIThread.Post(Refresh);
        }
    }
}
