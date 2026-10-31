// Abre la ventana principal de Trading Hub sin pantalla (Avalonia headless + Skia) y guarda capturas PNG.
// Uso: dotnet run --project tools/TradingHub.UiSnapshot -- carpeta [segundos] [página...] [--open conexión:SÍMBOLO] [--size 1600x950]
// Páginas: trading, strategies, data, connections, settings (por defecto: trading).
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TradingHub.App;
using TradingHub.App.Windows;

string outDir = args.Length > 0 ? args[0] : "ui-snapshots";
double seconds = args.Length > 1 && double.TryParse(args[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 6;
var pages = new List<string>();
string? open = null;
double width = 1600, height = 950;
for (int i = 2; i < args.Length; i++)
{
    if (args[i] == "--open" && i + 1 < args.Length) open = args[++i];
    else if (args[i] == "--size" && i + 1 < args.Length)
    {
        var parts = args[++i].Split('x');
        width = double.Parse(parts[0]);
        height = double.Parse(parts[1]);
    }
    else pages.Add(args[i]);
}

if (pages.Count == 0) pages.Add("trading");
Directory.CreateDirectory(outDir);

// una escena a la vez por carpeta temporal: todas usan las mismas copias (tradinghub-snap-*, tradinghub-fake-claude…) y otra
// corrida a la par las borraría a mitad de camino. Se libera sola al terminar el proceso (aunque se cuelgue o falle).
FileStream? snapLock = null;
for (var until = DateTime.UtcNow.AddMinutes(20); snapLock == null;)
{
    try
    {
        snapLock = new FileStream(Path.Combine(Path.GetTempPath(), "tradinghub-snap.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    catch (IOException)
    {
        if (DateTime.UtcNow > until)
        {
            Console.WriteLine("FALLA  hay otra escena corriendo con la misma carpeta temporal desde hace 20 minutos");
            return 1;
        }

        Console.WriteLine("Esperando a que termine otra escena…");
        Thread.Sleep(5000);
    }
}

// nunca tocar la energía de la Mac ni pedir permisos de administrador desde una herramienta
Environment.SetEnvironmentVariable("TRADINGHUB_SLEEP_GUARD", "off");

// nunca el llavero real desde una herramienta: ni la contraseña de Rithmic ni las claves de los exchanges
// (además, entrar con el usuario real desde acá puede desconectar la sesión del Trading Hub abierto)
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TRADINGHUB_KEYCHAIN_FILE")))
    Environment.SetEnvironmentVariable("TRADINGHUB_KEYCHAIN_FILE", Path.Combine(Path.GetTempPath(), "tradinghub-snap-keychain.json"));
TradingHub.Tests.FakeRithmicServer? fakeRithmic = null;

// las estrategias también en una copia: una herramienta nunca escribe en la carpeta real del usuario
// (hay escenas que crean estrategias y las mueven de carpeta)
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TRADINGHUB_STRATEGIES_DIR")))
{
    var realStrategies = Path.Combine(TradingHub.Common.Product.Documents, "Estrategias");
    var snapStrategies = Path.Combine(Path.GetTempPath(), "tradinghub-snap-estrategias");
    if (Directory.Exists(snapStrategies)) Directory.Delete(snapStrategies, true);
    Directory.CreateDirectory(snapStrategies);
    if (Directory.Exists(realStrategies))
        foreach (var f in Directory.EnumerateFiles(realStrategies, "*.cs"))
            File.Copy(f, Path.Combine(snapStrategies, Path.GetFileName(f)));
    Environment.SetEnvironmentVariable("TRADINGHUB_STRATEGIES_DIR", snapStrategies);
}

// y los indicadores propios: hay escenas que escriben indicadores de prueba y el Trading Hub abierto vigila la carpeta real
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TRADINGHUB_INDICATORS_DIR")))
{
    var realIndicators = Path.Combine(TradingHub.Common.Product.Documents, "Indicadores");
    var snapIndicators = Path.Combine(Path.GetTempPath(), "tradinghub-snap-indicadores");
    if (Directory.Exists(snapIndicators)) Directory.Delete(snapIndicators, true);
    Directory.CreateDirectory(snapIndicators);
    if (Directory.Exists(realIndicators))
        foreach (var f in Directory.EnumerateFiles(realIndicators, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(realIndicators, f);
            var unixRel = rel.Replace(Path.DirectorySeparatorChar, '/'); // en Windows las carpetas van con «\»
            if (unixRel.Contains("/bin/") || unixRel.Contains("/obj/")) continue;
            var dst = Path.Combine(snapIndicators, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(f, dst);
        }

    Environment.SetEnvironmentVariable("TRADINGHUB_INDICATORS_DIR", snapIndicators);
}

// nunca sobre los datos reales: un Trading Hub abierto graba en esa carpeta y dos procesos escribiendo los mismos
// archivos los dañan. Se copia solo la configuración y el espacio de trabajo a una carpeta temporal.
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TRADINGHUB_DATA_DIR")))
{
    var realRoot = TradingHub.Common.Product.AppData;
    var snapRoot = Path.Combine(Path.GetTempPath(), "tradinghub-snap-data");
    Directory.CreateDirectory(snapRoot);
    foreach (var f in new[] { "settings.json", "workspace.json", "connections.json" })
    {
        var src = Path.Combine(realRoot, f);
        if (File.Exists(src)) File.Copy(src, Path.Combine(snapRoot, f), overwrite: true);
    }

    // copias (nunca los originales) de lo ya bajado, para no volver a pedir años de datos al exchange
    foreach (var dir in new[] { "Klines", "Ticks", "Drawings" })
    {
        var src = Path.Combine(realRoot, dir);
        if (!Directory.Exists(src)) continue;
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(f).StartsWith('.')) continue; // candados de grabación
            // Rithmic va al servidor falso; Databento tampoco se copia (pesa cientos de MB y cambiaría lo que prueban las escenas)
            if (Path.GetRelativePath(src, f) is var rel && (rel.StartsWith("rithmic", StringComparison.Ordinal) || rel.StartsWith("databento", StringComparison.Ordinal))) continue;
            var dst = Path.Combine(snapRoot, dir, Path.GetRelativePath(src, f));
            if (File.Exists(dst) && File.GetLastWriteTimeUtc(dst) >= File.GetLastWriteTimeUtc(f)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(f, dst, overwrite: true);
        }
    }

    // el historial de las cuentas de prueba empieza de cero en cada corrida
    if (Directory.Exists(Path.Combine(snapRoot, "Accounts"))) Directory.Delete(Path.Combine(snapRoot, "Accounts"), true);

    // Rithmic contra un servidor de mentira (usuario demo, cuenta ACC1) y sin claves de exchanges
    fakeRithmic = FakeMarket();
    SanitizeConnections(Path.Combine(snapRoot, "connections.json"), fakeRithmic.Uri, Environment.GetEnvironmentVariable("TRADINGHUB_KEYCHAIN_FILE")!);
    Environment.SetEnvironmentVariable("TRADINGHUB_DATA_DIR", snapRoot);
}

static TradingHub.Tests.FakeRithmicServer FakeMarket()
{
    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var server = new TradingHub.Tests.FakeRithmicServer { PageSize = 5000, LastTradeSsboe = now - 5, MarketPrice = 5000 };
    var rnd = new Random(7);
    double price = 5000;
    // velas de 1 minuto de los últimos 3 días y operaciones de las últimas 2 horas
    for (long t = now - 3 * 86400; t < now - 7200; t += 60)
    {
        double o = price;
        price = Math.Round((price + (rnd.NextDouble() - 0.5) * 6) * 4) / 4;
        server.Bars.Add((t + 60, o, Math.Max(o, price) + 0.5, Math.Min(o, price) - 0.5, price, 400 + rnd.Next(800), 200 + rnd.Next(400)));
    }

    for (long t = now - 7200; t < now - 5; t += 2)
    {
        price = Math.Round((price + (rnd.NextDouble() - 0.5) * 1.5) * 4) / 4;
        server.Ticks.Add((t, rnd.Next(1_000_000), price, 1 + rnd.Next(6), rnd.Next(2) == 0));
    }

    server.MarketPrice = price;
    return server;
}

static void SanitizeConnections(string path, string fakeUri, string keychainFile)
{
    var secrets = new Dictionary<string, string>();
    if (File.Exists(path))
    {
        var list = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsArray();
        foreach (var node in list)
        {
            if (node is not System.Text.Json.Nodes.JsonObject o) continue;
            o["ApiKey"] = "";
            o["AllowRealOrders"] = false;
            if (Enum.TryParse<TradingHub.App.Services.ConnectionKind>(o["Kind"]?.ToString(), out var kind)
                && kind is TradingHub.App.Services.ConnectionKind.Rithmic or TradingHub.App.Services.ConnectionKind.AmpFutures)
            {
                o["RithmicUri"] = fakeUri;
                o["RithmicSystem"] = "Rithmic Paper Trading";
                o["RithmicUser"] = "demo";
                o["RithmicAggregatedQuotes"] = false;
                secrets[$"{o["Id"]}:secret"] = "demo";
            }
        }

        File.WriteAllText(path, list.ToJsonString());
    }

    File.WriteAllText(keychainFile, System.Text.Json.JsonSerializer.Serialize(secrets));
}

// TRADINGHUB_LANG=en o zh-CN: las escenas en otro idioma, y por cada página la lista de lo que quedó en español
var sceneLang = Environment.GetEnvironmentVariable("TRADINGHUB_LANG");
if (!string.IsNullOrEmpty(sceneLang)) TradingHub.App.Localization.Lang.Load(sceneLang);
AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();
TradingHub.App.Localization.Lang.Install();

// textos a la vista que siguen en español (con tildes o palabras muy del español): para completar las traducciones
void ReportUntranslated(Avalonia.Controls.Window w, string page)
{
    if (string.IsNullOrEmpty(sceneLang)) return;
    var spanish = new System.Text.RegularExpressions.Regex(@"[áéíóúñ¿¡]|\b(de|del|la|las|los|el|que|con|para|una|sin|por|más|cuando|desde|hasta)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    var left = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(w).OfType<Avalonia.Controls.TextBlock>()
        .Where(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text) && spanish.IsMatch(t.Text!))
        .Select(t => t.Text!.Replace("\n", " ⏎ ")).Distinct().ToList();
    File.WriteAllLines(Path.Combine(outDir, $"sin-traducir-{page.Replace('+', '_')}.txt"), left);
    Console.WriteLine($"Sin traducir en {page}: {left.Count}");
}

MainWindow? window = null;

void Pump(double secs)
{
    var end = DateTime.UtcNow.AddSeconds(secs);
    do
    {
        // en modo sin pantalla los DispatcherTimer no siempre disparan: el ciclo se llama a mano
        window?.Tick();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Thread.Sleep(16);
    }
    while (DateTime.UtcNow < end);
}

window = new MainWindow(App.Services) { Width = width, Height = height };
window.Show();
for (int i = 0; i < 150 && window.Trading.ActiveTab?.Model == null; i++) Pump(0.02);

if (open != null && open.Split(':') is [var feedId, var symbol] && window.Trading.ActiveTab is { } tab)
{
    var task = window.Trading.OpenInTabAsync(tab, feedId, symbol, TradingHub.Data.ChartPeriod.M1, tab.Model?.Settings.Clone());
    while (!task.IsCompleted) Pump(0.02);
}

if (Environment.GetEnvironmentVariable("TRADINGHUB_SNAP_INDICATORS") is { Length: > 0 } wanted && window.Trading.ActiveTab?.Model is { } chart)
{
    // agrega indicadores al gráfico activo (por nombre visible)
    foreach (var slot in chart.Indicators.ToList()) chart.RemoveIndicator(slot);
    foreach (var name in wanted.Split('|'))
    {
        var info = App.Services.Catalog.Items.FirstOrDefault(i => i.Name == name);
        if (info == null) { Console.WriteLine("No encontré " + name); continue; }
        chart.AddIndicator((TradingHub.Indicators.Indicator)Activator.CreateInstance(info.Type)!);
        Console.WriteLine("Agregado " + name);
    }
}

Pump(seconds);
if (Environment.GetEnvironmentVariable("TRADINGHUB_SNAP_INDICATORS") is { Length: > 0 } && window.Trading.ActiveTab?.Model is { } chart2)
    foreach (var slot in chart2.Indicators) Console.WriteLine($"  {slot.Indicator.Name}: {(slot.Error ?? "OK")}");

if (Environment.GetEnvironmentVariable("TRADINGHUB_SCRIPTS_DEBUG") == "1")
{
    var res = App.Services.Scripts?.LastResult;
    Console.WriteLine($"Indicadores propios: {(res == null ? "sin compilar" : res.Success ? "OK" : "con errores")} ({res?.Diagnostics.Count(d => d.Severity == "error")} errores)");
    foreach (var g in (res?.Diagnostics ?? Array.Empty<TradingHub.Scripting.ScriptDiagnostic>()).Where(d => d.Severity == "error").GroupBy(d => d.Id + " " + System.Text.RegularExpressions.Regex.Replace(d.Message, "'[^']*'", m => m.Value)).Take(120))
        Console.WriteLine($"  {g.Count(),3}x {Path.GetFileName(g.First().File)}:{g.First().Line} {g.Key}");
    var names = App.Services.Catalog.Items.Where(i => i.Source == TradingHub.App.Services.UserScripts.Source).Select(i => i.Name);
    Console.WriteLine("En el catálogo: " + string.Join(", ", names));
}

if (Environment.GetEnvironmentVariable("TRADINGHUB_SNAP_DEBUG") == "1")
{
    foreach (var f in new[] { "_connText", "_infoText", "_statusText" })
    {
        var tb = (Avalonia.Controls.TextBlock?)typeof(MainWindow).GetField(f, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(window);
        Console.WriteLine($"{f} = '{tb?.Text}'");
    }

    var d = window.Trading.ActiveTab?.Model?.Data;
    Console.WriteLine($"data: {d?.Instrument.Symbol} status={d?.Status} trades={d?.Trades.Count} loading={d?.IsLoadingHistory} progress={d?.HistoryProgress}");
}

int exitCode = 0;
foreach (var page in pages)
{

    if (page == "conexiones")
    {
        exitCode += ConnectionsScene.Run(window, Pump, outDir);
        continue;
    }


    if (page == "panel")
    {
        exitCode += PanelScene.Run(window, Pump, outDir);
        continue;
    }

    if (page == "footprint")
    {
        exitCode += FootprintScene.Run(window, Pump, outDir);
        continue;
    }

    if (page == "esquinas")
    {
        exitCode += CornersScene.Run(window, Pump, outDir);
        continue;
    }


    if (page == "mercado")
    {
        exitCode += BacktestMarketScene.Run(window, Pump, outDir);
        continue;
    }

    if (page == "choice")
    {
        // abre el botón de historia de la barra y muestra qué pasa
        window.Navigate("trading");
        var hb = typeof(TradingHub.App.Views.ChartToolbar).GetField(Environment.GetEnvironmentVariable("TRADINGHUB_SNAP_CHOICE") ?? "_historyBox", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(window.Trading.Toolbar)!;
        var btn = (Avalonia.Controls.Button)hb;
        Console.WriteLine($"Botón: visible={btn.IsVisible} habilitado={btn.IsEffectivelyEnabled} tamaño={btn.Bounds} plantilla={(btn.GetType().GetProperty("Template")?.GetValue(btn) != null)} flyout={btn.Flyout?.GetType().Name}");
        btn.GetType().GetMethod("OnClick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(btn, null);
        Pump(1);
        var mf = (Avalonia.Controls.MenuFlyout)btn.Flyout!;
        Console.WriteLine($"Menú abierto={mf.IsOpen} ítems={mf.Items.Count}");
        var presenter = mf.GetType().GetProperty("Popup", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(mf) as Avalonia.Controls.Primitives.Popup;
        var child = presenter?.Child as Avalonia.Controls.Control;
        Console.WriteLine($"Presentador: {child?.GetType().Name} tamaño={child?.Bounds}");
        if (child is Avalonia.Controls.ItemsControl ic) Console.WriteLine($"Ítems en el presentador: {ic.ItemCount}");
        var shot = child != null ? Avalonia.Controls.TopLevel.GetTopLevel(child)?.CaptureRenderedFrame() : null;
        shot?.Save(Path.Combine(outDir, "choice.png"));
        continue;
    }

    if (page == "picker")
    {
        // abre el selector de instrumentos y captura su ventana emergente
        window.Navigate("trading");
        var picker = window.Trading.Picker;
        picker.GetType().GetMethod("OnClick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(picker, null);
        Pump(4);
        var flyout = (Avalonia.Controls.Flyout)picker.GetType().GetField("_flyout", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(picker)!;
        var content = (Avalonia.Controls.Control)flyout.Content!;
        var top = Avalonia.Controls.TopLevel.GetTopLevel(content);
        var shot = top?.CaptureRenderedFrame();
        var pp = Path.Combine(outDir, "picker.png");
        shot?.Save(pp);
        Console.WriteLine(shot != null ? $"Captura: {pp}" : "No se pudo capturar el selector");
        flyout.Hide();
        continue;
    }

    var parts = page.Split('+');
    if (page == "data+rithmic")
    {
        // Datos con un instrumento de Rithmic: 1 año (contratos de cada tramo) y una descarga corta contra el servidor falso
        window.Navigate("data");
        Pump(1);
        var view = ((System.Collections.Generic.Dictionary<string, Avalonia.Controls.Control>)typeof(MainWindow).GetField("_pages", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(window)!)["data"];
        var vt = view.GetType();
        var fl = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var es = (await App.Services.Connections.GetFeed("rithmic").GetInstrumentsAsync(CancellationToken.None)).First(i => i.Symbol == "ES");
        vt.GetField("_dlProfile", fl)!.SetValue(view, "rithmic");
        vt.GetField("_dlInstrument", fl)!.SetValue(view, es);
        var chip = view.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().First(b => (b.Content as string) == "1 año");
        chip.IsChecked = true;
        chip.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Pump(0.5);
        var status = (Avalonia.Controls.TextBlock)vt.GetField("_dlStatus", fl)!.GetValue(view)!;
        Console.WriteLine("1 año: " + status.Text);
        window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "data_rithmic_year.png"));
        // descarga de ayer (el servidor falso tiene operaciones de las últimas 2 horas: hoy no se baja, ayer da vacío)
        var y = view.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().First(b => (b.Content as string) == "Ayer");
        y.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Console.WriteLine($"antes: perfil={vt.GetField("_dlProfile", fl)!.GetValue(view)} inst={vt.GetField("_dlInstrument", fl)!.GetValue(view)} puede={vt.GetProperty("CanDownload", fl)!.GetValue(view)}");
        var dl = (Task)vt.GetMethod("DownloadAsync", fl)!.Invoke(view, null)!;
        string last = "";
        while (!dl.IsCompleted)
        {
            Pump(0.1);
            if (status.Text != last) Console.WriteLine("  · " + (last = status.Text ?? ""));
        }
        Console.WriteLine("Descarga: " + status.Text + (dl.Exception != null ? " · ERROR " + dl.Exception.GetBaseException() : ""));
        continue;
    }

    if (page == "theme")
    {
        // oscuro y claro, con dos gráficos (bordes) y la configuración del gráfico por secciones
        window.Navigate("trading");
        var tv = window.Trading;
        var lay = tv.SetLayoutAsync(TradingHub.App.Views.PaneLayout.TwoColumns);
        while (!lay.IsCompleted) Pump(0.05);
        foreach (var c in tv.Tabs.Where(t => t.Model != null)) { c.Model!.Settings.Colors = null; c.Model.ApplyColors(); }
        Pump(3);
        window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "theme_dark.png"));
        TradingHub.App.Services.AppTheme.Apply(TradingHub.App.Services.AppTheme.Light);
        Pump(2);
        window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "theme_light.png"));
        tv.OpenChartSettings(tv.ActiveTab);
        Pump(1.5);
        var sw = (typeof(TradingHub.App.Views.TradingView).GetField("_settingsWindows", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(tv) as System.Collections.IDictionary)?.Values.Cast<Avalonia.Controls.Window>().FirstOrDefault();
        if (sw != null)
        {
            var tabsCtl = sw.GetVisualDescendants().OfType<Avalonia.Controls.TabControl>().First();
            tabsCtl.SelectedIndex = tabsCtl.ItemCount - 1;
            Pump(1);
            sw.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "theme_chart_settings_light.png"));
            sw.Close();
        }

        window.Navigate("settings");
        Pump(1.5);
        window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "theme_settings_light.png"));
        TradingHub.App.Services.AppTheme.Apply(TradingHub.App.Services.AppTheme.Dark);
        window.Navigate("accounts");
        Pump(1.5);
        window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "theme_accounts_dark.png"));
        Console.WriteLine("Capturas del tema listas");
        continue;
    }

    if (page == "data+import")
    {
        // importar un CSV de US100 (como el de Dukascopy) y ver que aparece en Datos y en los backtests
        var csv = Path.Combine(Path.GetTempPath(), "USATECHIDXUSD_Ticks_muestra.csv");
        var sb = new System.Text.StringBuilder("Gmt time,Ask,Bid,AskVolume,BidVolume\n");
        var t = new DateTime(2025, 9, 1, 13, 30, 0, DateTimeKind.Utc);
        var rnd = new Random(3);
        double px = 23450;
        for (int i = 0; i < 20000; i++)
        {
            px += (rnd.NextDouble() - 0.5) * 2;
            t = t.AddMilliseconds(250 + rnd.Next(1500));
            sb.Append(t.ToString("dd.MM.yyyy HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
              .Append((px + 1.2).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
              .Append(px.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)).Append(",0.0015,0.0012\n");
        }

        File.WriteAllText(csv, sb.ToString());
        var r = TradingHub.Data.CsvImport.Import(csv, "US100", TradingHub.App.Services.AppPaths.Ticks, TimeSpan.Zero, null, null, CancellationToken.None);
        Console.WriteLine($"Importado: {r.Kind}, {r.Trades} operaciones, {r.Days} días, paso {r.TickSize}");
        window.Navigate("data");
        Pump(1.5);
        var found = TradingHub.Data.HistoricalData.Scan(TradingHub.App.Services.AppPaths.Ticks).FirstOrDefault(x => x.FeedId == "importado" && x.Symbol == "US100");
        Console.WriteLine($"En Datos: {(found == null ? "no" : $"US100 · importado, {found.Days.Count} día(s)")}");
        window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "data_import.png"));
        continue;
    }

    if (page == "accounts+demo")
    {
        // sección Cuenta con movimiento: órdenes en la simulada (SIM-BTC) y en Rithmic (servidor falso, cuenta ACC1)
        var tv = window.Trading;
        var simOpen = tv.OpenInTabAsync(tv.ActiveTab!, "sim", "SIM-BTC", TradingHub.Data.ChartPeriod.M1, tv.ActiveTab!.Model?.Settings.Clone());
        while (!simOpen.IsCompleted) Pump(0.05);
        Pump(2);
        var simData = tv.ActiveTab!.Model!.Data;
        var sim = App.Services.SimBrokerFor(simData);
        sim.Market(TradingHub.Trading.OrderDirections.Buy, 0.5m);
        Pump(1.5);
        sim.Market(TradingHub.Trading.OrderDirections.Buy, 0.5m);
        Pump(1.5);
        sim.Market(TradingHub.Trading.OrderDirections.Sell, 1m);
        Pump(1.5);
        sim.Market(TradingHub.Trading.OrderDirections.Sell, 0.3m);
        Pump(1.5);
        sim.Limit(TradingHub.Trading.OrderDirections.Buy, 0.2m, Math.Round(simData.LastPrice * 0.98m, 1));
        Pump(1);
        Console.WriteLine($"Simulada: posición {sim.Position!.Volume} · órdenes {sim.Orders.Count} · ejecuciones guardadas {App.Services.Journal.FillsOf("sim").Count}");

        var rp = App.Services.Connections.Find("rithmic")!;
        var session = App.Services.RithmicSessionFor(rp);
        for (int i = 0; i < 100 && !session.IsConnected; i++) Pump(0.1);
        fakeRithmic!.MarketPrice = 5000;
        var o1 = session.PlaceAsync("ACC1", "ESZ6", "CME", true, 2, 2, 0, 0, "demo1", CancellationToken.None);
        while (!o1.IsCompleted) Pump(0.05);
        fakeRithmic.MarketPrice = 5006.25;
        var o2 = session.PlaceAsync("ACC1", "ESZ6", "CME", false, 2, 1, 0, 0, "demo2", CancellationToken.None);
        while (!o2.IsCompleted) Pump(0.05);
        var o3 = session.PlaceAsync("ACC1", "ESZ6", "CME", true, 1, 1, 4990, 4990, "demo3", CancellationToken.None);
        while (!o3.IsCompleted) Pump(0.05);
        Pump(1);
        Console.WriteLine($"Rithmic: saldo {session.SummaryFor("ACC1")?.Balance} · ejecuciones {session.FillsOf("ACC1").Count} · guardadas {App.Services.Journal.FillsOf("rithmic|rithmic|ACC1").Count}");

        window.Navigate("accounts");
        Pump(1.5);
        var view = ((System.Collections.Generic.Dictionary<string, Avalonia.Controls.Control>)typeof(MainWindow).GetField("_pages", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(window)!)["accounts"];
        var vt = view.GetType();
        var tabs = (Avalonia.Controls.TabControl)vt.GetField("_tabs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(view)!;
        foreach (var acc in new[] { "rithmic|rithmic|ACC1", "sim" })
        {
            vt.GetField("_selected", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(view, acc);
            for (int t = 0; t < 5; t++)
            {
                tabs.SelectedIndex = t;
                Pump(1.3);
                string name = $"accounts_{(acc == "sim" ? "sim" : "rithmic")}_{t}";
                window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, name + ".png"));
                Console.WriteLine($"Captura: {name}");
            }
        }

        continue;
    }

    window.Navigate(parts[0]);
    Pump(1.2);

    if (parts.Length > 1 && parts[0] == "connections")
    {
        // elige el perfil de Rithmic y espera la lista de servidores
        var cv = ((System.Collections.Generic.Dictionary<string, Avalonia.Controls.Control>)typeof(MainWindow).GetField("_pages", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(window)!)["connections"];
        var prof = App.Services.Connections.Profiles.FirstOrDefault(p => p.IsRithmic && (parts[1] != "amp" || p.Kind == TradingHub.App.Services.ConnectionKind.AmpFutures));
        if (prof != null) cv.GetType().GetMethod("Select", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(cv, new object[] { prof });
        Pump(9);
        window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, $"connections_{parts[1]}.png"));
        Console.WriteLine($"Perfil: {prof?.Name} ({prof?.Kind}) · {prof?.RithmicSystem} · {prof?.RithmicUri}");
        continue;
    }

    // acciones de trading: trading+layout4, trading+float, trading+brackets, trading+domsettings, trading+narrow
    if (parts.Length > 1 && parts[0] == "trading")
    {
        var tv = window.Trading;
        string action = parts[1];
        string shotName = page.Replace('+', '_') + ".png";
        if (action.StartsWith("layout"))
        {
            var layout = action switch
            {
                "layout2" => TradingHub.App.Views.PaneLayout.TwoColumns,
                "layout2r" => TradingHub.App.Views.PaneLayout.TwoRows,
                "layout3" => TradingHub.App.Views.PaneLayout.OneLeftTwoRight,
                "layout6" => TradingHub.App.Views.PaneLayout.Six,
                _ => TradingHub.App.Views.PaneLayout.Four,
            };
            var t1 = tv.SetLayoutAsync(layout);
            while (!t1.IsCompleted) Pump(0.05);
            Pump(seconds);
            Console.WriteLine($"Diseño {layout}: {tv.ActivePage?.Charts.Count} gráficos en la pestaña, activo {tv.ActiveTab?.Caption}");
        }
        else if (action.StartsWith("history"))
        {
            // trading+history8760[+H4]: carga esa cantidad de horas (o "historyall") y cambia el período
            double hours = action == "historyall" ? -1 : double.Parse(action["history".Length..], System.Globalization.CultureInfo.InvariantCulture);
            var hc = tv.ActiveTab!;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var load = tv.LoadHistoryAsync(hc, hours);
            while (!load.IsCompleted) Pump(0.1);
            Console.WriteLine($"Historia {hours} h cargada en {sw.Elapsed.TotalSeconds:0.0} s · {hc.Model?.Data.Trades.Count:N0} operaciones desde {hc.Model?.Data.Trades.FirstTime:yyyy-MM-dd HH:mm} · ticks reales desde {hc.Model?.Data.RealTicksFrom:yyyy-MM-dd HH:mm}");
            if (parts.Length > 2) tv.Toolbar.SetPeriod(TradingHub.Data.ChartPeriod.Parse(parts[2]));
            Pump(seconds);
            Console.WriteLine($"Velas: {hc.Model?.Series.Count:N0} · sintéticas: {hc.Model?.Series.Candles.Count(c => c.IsSynthetic):N0}");
        }
        else if (action is "jitter" or "jitterold")
        {
            if (action == "jitterold")
            {
                // como estaba antes: sin anchos fijos en el precio y la variación
                foreach (var f in new[] { "_price", "_change" })
                    ((Avalonia.Controls.TextBlock)typeof(TradingHub.App.Controls.InstrumentPicker).GetField(f, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(tv.Picker)!).MinWidth = 0;
            }

            // la barra no debe cambiar de alto cuando la variación del día pasa por 0,00 %
            var inst = tv.ActiveTab!.Model!.Instrument;
            var buy = (Avalonia.Controls.Button)typeof(TradingHub.App.Views.TradingView).GetField("_buyButton", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(tv)!;
            if (Environment.GetEnvironmentVariable("TRADINGHUB_SNAP_SCAN") == "1")
            {
                // recorre anchos de ventana y busca dónde la barra cambia de alto según la variación del día
                int jumps = 0;
                for (int w = 1100; w <= 1700; w += 5)
                {
                    window.Width = w;
                    Pump(0.05);
                    tv.Picker.SetQuote(84710.6m, 0.25m, inst);
                    Pump(0.05);
                    double h1 = tv.Toolbar.Bounds.Height;
                    var y1 = buy.TranslatePoint(new Avalonia.Point(0, 0), window)?.Y;
                    tv.Picker.SetQuote(84710.6m, 0m, inst);
                    Pump(0.05);
                    double h2 = tv.Toolbar.Bounds.Height;
                    var y2 = buy.TranslatePoint(new Avalonia.Point(0, 0), window)?.Y;
                    if (h1 != h2 || y1 != y2)
                    {
                        jumps++;
                        Console.WriteLine($"ancho {w}: barra {h1:0}→{h2:0} px · COMPRAR y={y1:0}→{y2:0} (se mueve {y1 - y2:0} px)");
                    }
                }

                Console.WriteLine(jumps == 0 ? "Ningún ancho hace saltar la barra." : $"{jumps} anchos con salto");
                continue;
            }

            foreach (var (pct, label) in new[] { (0.25m, "con variación"), (0m, "variación 0,00 %"), (-12.5m, "variación −12,50 %"), (0m, "variación 0,00 %") })
            {
                tv.Picker.SetQuote(84710.6m, pct, inst);
                Pump(0.3);
                var pos = buy.TranslatePoint(new Avalonia.Point(0, 0), window);
                Console.WriteLine($"{label,-20} barra {tv.Toolbar.Bounds.Height:0} px · botón COMPRAR en y={pos?.Y:0}");
            }

            continue;
        }
        else if (action == "float")
        {
            var w = tv.Detach(tv.ActiveTab!);
            Pump(seconds);
            var fshot = w?.CaptureRenderedFrame();
            fshot?.Save(Path.Combine(outDir, "trading_float_window.png"));
            Console.WriteLine($"Ventana aparte: {w?.Title} · ventanas={tv.FloatingWindows.Count} · pestañas={tv.Pages.Count}");
            var main = window.CaptureRenderedFrame();
            main?.Save(Path.Combine(outDir, shotName));
            if (w != null) tv.DockBack(w);
            Pump(1);
            Console.WriteLine($"De vuelta: ventanas={tv.FloatingWindows.Count} · gráficos en la pestaña={tv.ActivePage?.Charts.Count}");
            continue;
        }
        else if (action == "brackets" && tv.ActiveTab?.Model is { } m && m.Account is { } acc && m.Brackets is { } br)
        {
            for (int i = 0; i < 200 && m.Data.LastPrice == 0; i++) Pump(0.05);
            decimal last = m.Data.LastPrice;
            decimal tick = m.Instrument.TickSize;
            decimal q = Math.Max(m.Instrument.VolumeStep, 1m * m.Instrument.VolumeStep * 10);
            acc.Market(TradingHub.Trading.OrderDirections.Buy, q * 4);
            Pump(0.3);
            Console.WriteLine(br.DropFromPosition(last - 60 * tick));
            Console.WriteLine(br.DropFromPosition(last + 80 * tick));
            Console.WriteLine(br.AddTarget(last + 160 * tick));
            var entry = acc.Limit(TradingHub.Trading.OrderDirections.Buy, q, last - 120 * tick);
            Console.WriteLine(br.AttachToEntry(entry, last - 170 * tick));
            Console.WriteLine(br.AttachToEntry(entry, last - 40 * tick));
            m.Viewport.AutoScale = false;
            m.Viewport.PriceTop = (double)(last + 220 * tick);
            m.Viewport.PriceBottom = (double)(last - 220 * tick);
            m.Invalidate();
            Pump(seconds);
            tv.UpdatePosition();
            Pump(0.5);
            var bshot = window.CaptureRenderedFrame();
            bshot?.Save(Path.Combine(outDir, shotName));
            Console.WriteLine($"Captura: {Path.Combine(outDir, shotName)}");
            Console.WriteLine(br.ClosePartial(0.5m));
            Pump(0.5);
            Console.WriteLine($"Después del parcial: posición={acc.Position.Volume} stop={br.StopLoss?.Unfilled} objetivos={string.Join(",", br.Targets.Select(t => t.Unfilled))}");
            acc.Flatten();
            Pump(0.5);
            Console.WriteLine($"Plana: órdenes activas={acc.ActiveOrders.Count()}");
            m.Viewport.AutoScale = true;
            continue;
        }
        else if (action is "drawings" or "drawsettings")
        {
            // dibujos nuevos: posición larga y corta, Fibonacci, zona con texto, tendencia extendida y alerta
            var cm = tv.ActiveTab!.Model!;
            var ser = cm.Series;
            for (int i = 0; i < 100 && ser.Count < 40; i++) Pump(0.1);
            double last = (double)ser.TicksToPrice(ser[^1].CloseTicks);
            double hi = Enumerable.Range(1, 30).Max(k => (double)ser.TicksToPrice(ser[^k].HighTicks));
            double lo = Enumerable.Range(1, 30).Min(k => (double)ser.TicksToPrice(ser[^k].LowTicks));
            double r = Math.Max((hi - lo) / 4, (double)cm.Instrument.TickSize * 10);
            DateTime T(int back) => ser[^back].Time;
            var dl = new TradingHub.Charting.Drawings.ChartDrawing { Kind = TradingHub.Charting.Drawings.DrawingKind.LongPosition, Points = { new(T(30), lo + r) }, Quantity = 0.5m };
            dl.InitPosition(r, T(12));
            var ds = new TradingHub.Charting.Drawings.ChartDrawing { Kind = TradingHub.Charting.Drawings.DrawingKind.ShortPosition, Points = { new(T(10), hi - r / 2) } };
            ds.InitPosition(r / 2, T(1));
            cm.Drawings.Add(dl);
            cm.Drawings.Add(ds);
            cm.Drawings.Add(new TradingHub.Charting.Drawings.ChartDrawing { Kind = TradingHub.Charting.Drawings.DrawingKind.Rectangle, Points = { new(T(28), hi), new(T(18), hi - r) }, Text = "Zona de oferta", ExtendRight = true, ColorArgb = 0xFFF2B53A });
            cm.Drawings.Add(new TradingHub.Charting.Drawings.ChartDrawing { Kind = TradingHub.Charting.Drawings.DrawingKind.TrendLine, Points = { new(T(35), lo), new(T(20), lo + r) }, ExtendRight = true, Text = "Soporte", LineStyle = TradingHub.Charting.Drawings.LineStyleKind.Dashed });
            cm.Drawings.Add(new TradingHub.Charting.Drawings.ChartDrawing { Kind = TradingHub.Charting.Drawings.DrawingKind.HorizontalLine, Points = { new(T(5), last + r * 1.5) }, Alert = true, ColorArgb = 0xFFF2B53A });
            cm.Invalidate();
            Pump(0.6);
            if (action == "drawsettings")
            {
                var win = new TradingHub.App.Windows.DrawingSettingsWindow(App.Services, dl, cm.Instrument, cm.Period, () => cm.Invalidate(), () => { }, () => { });
                win.Show(window);
                Pump(1.2);
                win.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "trading_drawsettings.png"));
                var fib = new TradingHub.Charting.Drawings.ChartDrawing { Kind = TradingHub.Charting.Drawings.DrawingKind.FibRetracement, Points = { new(T(25), hi), new(T(5), lo) } };
                var win2 = new TradingHub.App.Windows.DrawingSettingsWindow(App.Services, fib, cm.Instrument, cm.Period, () => { }, () => { }, () => { });
                win2.Show(window);
                Pump(1.2);
                win2.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "trading_drawsettings_fib.png"));
                Console.WriteLine("Capturas: trading_drawsettings.png, trading_drawsettings_fib.png");
                win.Close();
                win2.Close();
                continue;
            }

            // leyenda con el mouse encima del primer indicador y gráfico corrido hacia atrás (aparece el botón)
            var st = (TradingHub.Charting.Rendering.ChartRenderState)typeof(TradingHub.App.Controls.ChartView).GetField("_state", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(tv.ActiveTab.View)!;
            var nameHit = st.Legend.FirstOrDefault(h => h.Action == TradingHub.Charting.Rendering.LegendAction.Name);
            if (nameHit != null)
            {
                st.MouseX = nameHit.Rect.MidX;
                st.MouseY = nameHit.Rect.MidY;
            }

            cm.Invalidate();
            tv.ActiveTab.View.InvalidateVisual();
            Pump(0.6);
            Console.WriteLine($"Leyenda: {st.Legend.Count} zonas ({string.Join(", ", st.Legend.Select(h => h.Action).Distinct())}) · botón de ir a lo último: {(st.GoLatestButton != null ? "sí" : "no")}");
            window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "trading_drawings.png"));
            cm.Viewport.ScrollBars = 25;
            st.MouseX = null;
            st.MouseY = null;
            cm.Invalidate();
            Pump(0.6);
            Console.WriteLine($"Corrido 25 velas: botón de ir a lo último {(st.GoLatestButton != null ? "visible" : "NO visible")}");
            window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "trading_drawings_scrolled.png"));
            continue;
        }
        else if (action == "links")
        {
            // dos gráficos vinculados en rojo: el segundo toma el instrumento del primero
            var setLayout = tv.SetLayoutAsync(TradingHub.App.Views.PaneLayout.TwoColumns);
            while (!setLayout.IsCompleted) Pump(0.05);
            Pump(1.5);
            var charts = tv.ActivePage!.Charts.Take(2).ToList();
            foreach (var c in charts) tv.SetLinkGroup(c, 1);
            Pump(2);
            var pt = charts[0].Model!.Period;
            tv.Toolbar.Chart = charts[0];
            tv.Activate(charts[0]);
            tv.Toolbar.SetPeriod(pt == TradingHub.Data.ChartPeriod.M5 ? TradingHub.Data.ChartPeriod.M15 : TradingHub.Data.ChartPeriod.M5);
            Pump(1.5);
            Console.WriteLine($"Vinculados: {string.Join(" | ", charts.Select(c => $"{c.Symbol} {c.Model?.Period.Code} grupo {c.LinkGroup}"))}");
            // cruz del cursor en el primero → aparece en el segundo
            var view0 = charts[0].View;
            var ev = typeof(TradingHub.App.Controls.ChartView).GetMethod("RaiseCrosshair", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            ev.Invoke(view0, new object[] { new Avalonia.Point(view0.Bounds.Width * 0.6, view0.Bounds.Height * 0.4) });
            Pump(0.6);
            var st2 = (TradingHub.Charting.Rendering.ChartRenderState)typeof(TradingHub.App.Controls.ChartView).GetField("_state", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(charts[1].View)!;
            Console.WriteLine($"Cruz vinculada en el segundo: {(st2.LinkedTime != null ? st2.LinkedTime.Value.ToString("HH:mm") : "no")} precio={(st2.LinkedPrice != null ? st2.LinkedPrice.Value.ToString("0.0") : "-")}");
            window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "trading_links.png"));
            continue;
        }
        else if (action == "toolbar")
        {
            // mide la barra: alto (una o dos líneas) y ancho de cada parte
            Pump(1);
            var bar = tv.Toolbar;
            void Walk(Avalonia.Controls.Control c, int depth)
            {
                if (depth > 4) return;
                if (c is Avalonia.Controls.WrapPanel wp)
                {
                    Console.WriteLine($"WrapPanel ancho={wp.Bounds.Width:0} alto={wp.Bounds.Height:0}");
                    foreach (var ch in wp.Children) Console.WriteLine($"   {ch.GetType().Name,-22} x={ch.Bounds.X:0} y={ch.Bounds.Y:0} w={ch.Bounds.Width:0}");
                    return;
                }

                if (c is Avalonia.Controls.StackPanel sp && depth > 1) Console.WriteLine($"Panel derecho ancho={sp.Bounds.Width:0}");
                foreach (var ch in Avalonia.VisualTree.VisualExtensions.GetVisualChildren(c).OfType<Avalonia.Controls.Control>()) Walk(ch, depth + 1);
            }

            Walk(bar, 0);
            Console.WriteLine($"Barra alto={bar.Bounds.Height:0} ventana={window.Bounds.Width:0}");
            continue;
        }
        else if (action == "trader")
        {
            // Chart Trader propio y cross trading: desde BTCUSDT se compra BTCUSDC
            var trChart = tv.ActiveTab!;
            Pump(1);
            window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "trading_trader.png"));
            var cross = tv.SetCrossTradingAsync(trChart, trChart.Symbol == "BTCUSDT" ? "BTCUSDC" : "BTCUSDT");
            while (!cross.IsCompleted) Pump(0.05);
            for (int i = 0; i < 80 && (trChart.CrossData?.LastPrice ?? 0) == 0; i++) Pump(0.1);
            var own = App.Services.AccountFor(trChart.Model!.Data, "sim");
            var target = tv.AccountOf(trChart)!;
            Console.WriteLine($"Cross: mira {trChart.Symbol}, ejecuta en {trChart.CrossSymbol} · cuenta destino {target.Instrument.Symbol} · precio destino {trChart.CrossData?.LastPrice}");
            var order = tv.TradeAsync(trChart, TradingHub.Trading.OrderTypes.Market, TradingHub.Trading.OrderDirections.Buy, 0, 0.002m);
            while (!order.IsCompleted) Pump(0.05);
            Pump(1.5);
            Console.WriteLine($"Después de comprar: {target.Instrument.Symbol} posición {target.Position.Volume} · {own.Instrument.Symbol} posición {own.Position.Volume}");
            trChart.Trader?.Refresh();
            Pump(0.5);
            window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "trading_trader_cross.png"));
            var flat = tv.AccountActionAsync(trChart, a => a.Flatten(), "cerrada");
            while (!flat.IsCompleted) Pump(0.05);
            Pump(1);
            Console.WriteLine($"Cerrada: {target.Instrument.Symbol} posición {target.Position.Volume}");
            var off = tv.SetCrossTradingAsync(trChart, null);
            while (!off.IsCompleted) Pump(0.05);
            continue;
        }
        else if (action == "accounts")
        {
            // selector de cuentas del Chart Trader con una cuenta de Rithmic (servidor falso): se ven todas y se cambia ida y vuelta
            var ch = tv.ActiveTab!;
            if (ch.FeedId != "rithmic")
            {
                var op = tv.OpenInTabAsync(ch, "rithmic", "ES", TradingHub.Data.ChartPeriod.M1, ch.Model?.Settings.Clone());
                while (!op.IsCompleted) Pump(0.05);
            }

            tv.SetTraderVisible(ch, true);
            for (int i = 0; i < 200 && !App.Services.AccountsFor(ch.Model!.Data).Any(a => a.Id.StartsWith("rithmic|")); i++) Pump(0.1);
            Console.WriteLine("Cuentas: " + string.Join(" | ", App.Services.AccountsFor(ch.Model!.Data).Select(a => $"{a.Id} = {a.Name}")));
            ch.Trader!.Refresh();
            Pump(0.5);
            var box = (Avalonia.Controls.ComboBox)typeof(TradingHub.App.Views.ChartTraderPanel).GetField("_account", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(ch.Trader)!;

            void Click(Avalonia.Controls.Control c)
            {
                var top = Avalonia.Controls.TopLevel.GetTopLevel(c)!;
                var pt = c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), top)!.Value;
                top.MouseMove(pt);
                top.MouseDown(pt, Avalonia.Input.MouseButton.Left);
                top.MouseUp(pt, Avalonia.Input.MouseButton.Left);
                Pump(0.6);
            }

            void Shot(string name)
            {
                window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, name + ".png"));
                // si la lista abierta es una ventana aparte, también se captura
                var popupTop = box.Items.OfType<Avalonia.Controls.ComboBoxItem>().Select(Avalonia.Controls.TopLevel.GetTopLevel).FirstOrDefault(t => t != null && t != window);
                popupTop?.CaptureRenderedFrame()?.Save(Path.Combine(outDir, name + "_lista.png"));
            }

            string State() => $"abierta={box.IsDropDownOpen} · elegida='{(box.SelectedItem as Avalonia.Controls.ComboBoxItem)?.Content}' · cuenta del gráfico={ch.AccountId} · opera en={tv.AccountOf(ch)?.Name}";

            Avalonia.Controls.ComboBoxItem Item(string idPrefix) =>
                box.Items.OfType<Avalonia.Controls.ComboBoxItem>().First(i => i.Tag is TradingHub.App.Services.AccountOption o && o.Id.StartsWith(idPrefix, StringComparison.Ordinal));

            for (int round = 1; round <= 2; round++)
            {
                Click(box);
                Console.WriteLine($"[{round}] abrir: {State()}");
                foreach (var it in box.Items.OfType<Avalonia.Controls.ComboBoxItem>())
                {
                    var cp = it.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().FirstOrDefault();
                    Console.WriteLine($"     · '{it.Content}' visible={it.IsEffectivelyVisible} alto={it.Bounds.Height:0} color={(cp?.Foreground as Avalonia.Media.ISolidColorBrush)?.Color.ToString() ?? "NINGUNO"}");
                }

                if (round == 1) Shot("trading_accounts_open");
                Click(Item("rithmic|"));
                Console.WriteLine($"[{round}] elegir Rithmic: {State()}");
                if (round == 1) Shot("trading_accounts_rithmic");
                Click(box);
                Console.WriteLine($"[{round}] abrir de nuevo: {State()}");
                Click(Item("sim"));
                Console.WriteLine($"[{round}] elegir simulada: {State()}");
            }

            Shot("trading_accounts_back");
            continue;
        }
        else if (action == "crossdemo")
        {
            // mirás ES y ejecutás MES (cross trading): la posición de MES se ve en el gráfico de ES; panel de la derecha y colores
            var ch = tv.ActiveTab!;
            if (ch.FeedId != "rithmic" || ch.Symbol != "ES")
            {
                var op = tv.OpenInTabAsync(ch, "rithmic", "ES", TradingHub.Data.ChartPeriod.M1, ch.Model?.Settings.Clone());
                while (!op.IsCompleted) Pump(0.05);
            }

            double px = fakeRithmic!.MarketPrice;
            void PumpPush(double secs)
            {
                var end = DateTime.UtcNow.AddSeconds(secs);
                while (DateTime.UtcNow < end)
                {
                    _ = fakeRithmic.PushTrade("ESZ6", px, 1, true);
                    _ = fakeRithmic.PushTrade("MESZ6", px, 1, false);
                    Pump(0.2);
                }
            }

            tv.SetChartAccount(ch, "sim");
            var cross = tv.SetCrossTradingAsync(ch, "MES");
            while (!cross.IsCompleted) Pump(0.05);
            PumpPush(3);
            Console.WriteLine($"Cross: mira {ch.Symbol} ({ch.Model!.Data.Status}, {ch.Model.Data.LastPrice}) · ejecuta en {ch.CrossSymbol} ({ch.CrossData?.Status}, {ch.CrossData?.LastPrice})");
            var buy = tv.TradeAsync(ch, TradingHub.Trading.OrderTypes.Market, TradingHub.Trading.OrderDirections.Buy, 0, 1);
            while (!buy.IsCompleted) Pump(0.05);
            px += 1.25;
            PumpPush(2);
            var crossAcc = tv.AccountOf(ch)!;
            Console.WriteLine($"Cuenta del gráfico: {ch.Model.Account?.Instrument.Symbol} · posición {crossAcc.Instrument.Symbol} {crossAcc.Position.Volume} @ {crossAcc.Position.AveragePrice} · mismo objeto={ReferenceEquals(ch.Model.Account, crossAcc)}");
            ch.Model.Viewport.ScrollBars = 0;
            ch.Model.Viewport.AutoScale = true;
            PumpPush(1);
            window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "trading_cross_projection.png"));

            // panel de la derecha = el mismo Chart Trader del gráfico activo
            tv.SetPanel("ticket", true);
            PumpPush(1.5);
            window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "trading_side_trader.png"));
            var docked = typeof(TradingHub.App.Views.TradingView).GetField("_dockedTrader", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(tv) as TradingHub.App.Views.ChartTraderPanel;
            Console.WriteLine($"Panel de la derecha: {(docked == null ? "no" : $"Chart Trader de {docked.Chart.Symbol} (mismo gráfico={ReferenceEquals(docked.Chart, ch)})")}");

            // colores propios del gráfico
            tv.OpenChartSettings(ch);
            Pump(1);
            var sw = (typeof(TradingHub.App.Views.TradingView).GetField("_settingsWindows", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(tv) as System.Collections.IDictionary)?.Values.Cast<Avalonia.Controls.Window>().FirstOrDefault();
            sw?.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "chart_settings_window.png"));
            ch.Model.Settings.Colors = new() { ["BackgroundTop"] = 0xFF131722, ["BackgroundBottom"] = 0xFF131722, ["UpCandle"] = 0xFF26A69A, ["DownCandle"] = 0xFFF23645, ["UpCandleBorder"] = 0xFF26A69A, ["DownCandleBorder"] = 0xFFF23645 };
            ch.Model.ApplyColors();
            sw?.Close();
            PumpPush(1);
            window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "trading_custom_colors.png"));
            var others = tv.Tabs.Where(t => !ReferenceEquals(t, ch) && t.Model != null).Select(t => t.Model!.Theme.BackgroundTop.ToString()).Distinct();
            Console.WriteLine($"Colores: este gráfico fondo {ch.Model.Theme.BackgroundTop} · los otros {string.Join(", ", others)}");

            var flat = tv.AccountActionAsync(ch, a => a.Flatten(), "cerrada");
            while (!flat.IsCompleted) Pump(0.05);
            PumpPush(1.5);
            Console.WriteLine($"Cerrada: {crossAcc.Instrument.Symbol} posición {crossAcc.Position.Volume}");
            PumpPush(2);
            Console.WriteLine($"Marcas en {ch.Symbol}: {ch.Model.TradeMarks.Count} ejecuciones ({string.Join(", ", ch.Model.TradeMarks.Select(m => (m.Buy ? "compra " : "venta ") + m.Quantity + " @ " + m.Price))}) · {ch.Model.TradeLinks.Count} operación(es) cerrada(s)");
            ch.Model.Settings.Colors = null;
            ch.Model.ApplyColors();
            ch.Model.Viewport.ScrollBars = 0;
            ch.Model.Viewport.BarWidth = 22;
            PumpPush(1);
            window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "trading_trade_marks.png"));

            // plantillas: se guarda este gráfico, se le cambia todo y se aplica la plantilla
            var model = ch.Model;
            model.Settings.Colors = new() { ["BackgroundTop"] = 0xFF131722 };
            model.ApplyColors();
            var vwapInfo = App.Services.Catalog.Items.FirstOrDefault(i => i.Name.Contains("VWAP", StringComparison.OrdinalIgnoreCase)) ?? App.Services.Catalog.Items.First();
            foreach (var slot in model.Indicators.ToList()) model.RemoveIndicator(slot);
            model.AddIndicator((TradingHub.Indicators.Indicator)Activator.CreateInstance(vwapInfo.Type)!);
            var styleBefore = model.Settings.Style;
            TradingHub.App.Services.Workspace.SaveChartTemplate(ch, "Prueba plantilla");
            tv.Toolbar.SetStyle(styleBefore == TradingHub.Charting.ChartStyle.Footprint ? TradingHub.Charting.ChartStyle.Candles : TradingHub.Charting.ChartStyle.Footprint);
            model.Settings.Colors = null;
            model.ApplyColors();
            foreach (var slot in model.Indicators.ToList()) model.RemoveIndicator(slot);
            Pump(0.5);
            Console.WriteLine($"Plantillas guardadas: {string.Join(", ", TradingHub.App.Services.Workspace.ChartTemplateNames())} · antes de aplicar: estilo {model.Settings.Style}, {model.Indicators.Count} indicadores, fondo {model.Theme.BackgroundTop}");
            bool applied = TradingHub.App.Services.Workspace.ApplyChartTemplate(tv, ch, "Prueba plantilla");
            Pump(0.5);
            Console.WriteLine($"Aplicada={applied}: estilo {model.Settings.Style} (era {styleBefore}), indicadores [{string.Join(", ", model.Indicators.Select(i => i.Indicator.Name))}], fondo {model.Theme.BackgroundTop}");

            // plantilla de indicador: ajustes guardados, aplicados a uno nuevo y como predeterminada
            var ind = model.Indicators.First().Indicator;
            var numeric = ind.GetType().GetProperties().FirstOrDefault(p => p.CanWrite && p.PropertyType == typeof(int) && p.GetCustomAttributes(typeof(System.ComponentModel.BrowsableAttribute), true).Length == 0 && p.DeclaringType != typeof(TradingHub.Indicators.Indicator));
            if (numeric != null) numeric.SetValue(ind, (int)numeric.GetValue(ind)! + 7);
            TradingHub.App.Services.Workspace.SaveIndicatorTemplate(ind, "Mi " + ind.Name);
            var fresh = (TradingHub.Indicators.Indicator)Activator.CreateInstance(ind.GetType())!;
            bool indApplied = TradingHub.App.Services.Workspace.ApplyIndicatorTemplate(fresh, "Mi " + ind.Name);
            var a1 = TradingHub.App.Services.Workspace.SaveIndicator(ind, out _).ToString();
            var a2 = TradingHub.App.Services.Workspace.SaveIndicator(fresh, out _).ToString();
            App.Services.Settings.DefaultIndicatorTemplates[ind.GetType().FullName!] = "Mi " + ind.Name;
            var fresh2 = (TradingHub.Indicators.Indicator)Activator.CreateInstance(ind.GetType())!;
            bool byDefault = TradingHub.App.Services.Workspace.ApplyDefaultIndicatorTemplate(App.Services, fresh2);
            Console.WriteLine($"Plantilla de indicador: {string.Join(", ", TradingHub.App.Services.Workspace.IndicatorTemplateNames(ind.GetType()))} · cambiado {numeric?.Name}={numeric?.GetValue(ind)} · aplicada={indApplied} iguales={a1 == a2} · predeterminada={byDefault} ({numeric?.Name}={numeric?.GetValue(fresh2)})");
            App.Services.Settings.DefaultIndicatorTemplates.Clear();
            continue;
        }
        else if (action == "strategy")
        {
            // estrategia en el gráfico: historia + vivo (visual), configuración, pasar a ejecutar con la simulada y quitar
            var ch = tv.ActiveTab!;
            var open1 = tv.OpenInTabAsync(ch, "sim", "SIM-BTC", TradingHub.Data.ChartPeriod.M1, ch.Model?.Settings.Clone());
            while (!open1.IsCompleted) Pump(0.05);
            for (int i = 0; i < 200 && ch.Model!.Data.IsLoadingHistory; i++) Pump(0.1);
            // el cruce de medias tal como viene de ejemplo: la copia del del usuario puede haber cambiado y no operar en
            // esta historia corta (solo se escribe en la carpeta de prueba, nunca en la real)
            var strategiesDir = Path.GetFullPath(TradingHub.App.Services.AppPaths.Strategies);
            if (strategiesDir != Path.GetFullPath(Path.Combine(TradingHub.Common.Product.Documents, "Estrategias")))
            {
                var example = TradingHub.App.Services.StrategyTemplates.All.First(t => t.FileName == "CruceDeMedias");
                File.WriteAllText(Path.Combine(strategiesDir, "CruceDeMedias.cs"), example.Code("CruceDeMedias").Replace("__NOMBRE__", "Cruce de medias"));
            }

            App.Services.Strategies.Refresh();
            var file = App.Services.Strategies.Files.First(f => f.FileName == "CruceDeMedias");
            // el gráfico sin las estrategias que trae el espacio de trabajo copiado: se mide solo la de la prueba
            foreach (var old in ch.Strategies.ToList()) tv.RemoveStrategy(ch, old);
            var add = tv.AddStrategyAsync(ch, file, openSettings: false);
            while (!add.IsCompleted) Pump(0.1);
            var cs = ch.Strategies[^1];
            // si el gráfico carga más historia la estrategia se recalcula: se espera a que termine la última corrida
            bool Calculating() => cs.Busy || cs.Overlay.Status is "calculando…" or "esperando la historia…";
            for (int round = 0; round < 3; round++)
            {
                for (int i = 0; i < 600 && Calculating(); i++) Pump(0.1);
                Pump(2);
                if (!Calculating()) break;
            }
            Console.WriteLine($"Visual: estado «{cs.Overlay.Status}» {cs.Overlay.Error} · marcas {cs.Overlay.Signals.Count} · líneas {cs.Overlay.Links.Count} · runner={(cs.Runner?.IsRunning == true ? "corriendo" : "no")} en {cs.Runner?.Account.Name}");
            ch.Model!.Viewport.ScrollBars = 0;
            ch.Model.Viewport.BarWidth = 9;
            Pump(1);
            window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "strategy_visual.png"));

            tv.OpenStrategySettings(ch, cs);
            Pump(1.5);
            var sw = Avalonia.Application.Current!.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime lt
                ? lt.Windows.OfType<TradingHub.App.Windows.StrategyChartWindow>().FirstOrDefault() : null;
            sw ??= window.OwnedWindows.OfType<TradingHub.App.Windows.StrategyChartWindow>().FirstOrDefault();
            sw?.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "strategy_settings.png"));
            // y abajo, cómo le fue en la historia del gráfico (resultado real, curva y estadísticas)
            if (sw?.GetVisualDescendants().OfType<Avalonia.Controls.ScrollViewer>().FirstOrDefault() is { } scroll)
            {
                scroll.ScrollToEnd();
                Pump(0.6);
                scroll.ScrollToEnd();
                Pump(0.4);
                sw.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "strategy_settings_resultados.png"));
            }

            sw?.Close();

            // ejecutar con la cuenta simulada
            cs.Live = true;
            cs.AccountId = "sim";
            var run = tv.RunStrategyAsync(ch, cs);
            while (!run.IsCompleted) Pump(0.1);
            Pump(2);
            Console.WriteLine($"En vivo: estado «{cs.Overlay.Status}» · en la lista de Estrategias={App.Services.Runners.Contains(cs.Runner!)} · cuenta {cs.Runner?.Account.Name}");
            TradingHub.App.Services.Workspace.Save(tv);
            var ws = File.ReadAllText(TradingHub.App.Services.AppPaths.WorkspaceFile);
            Console.WriteLine($"Guardada en el espacio de trabajo: {ws.Contains("CruceDeMedias") || ws.Contains("Cruce")} (Live={ws.Contains("\"Live\": true")})");
            var runner = cs.Runner!;
            tv.RemoveStrategy(ch, cs);
            Pump(0.5);
            Console.WriteLine($"Quitada: runner corriendo={runner.IsRunning} · en la lista={App.Services.Runners.Contains(runner)} · leyenda {ch.Model.Strategies.Count}");
            continue;
        }
        else if (action == "domfloat")
        {
            tv.DetachDom();
            Pump(1.5);
            var dw = tv.PanelWindow("dom");
            dw?.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "trading_domfloat_window.png"));
            window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "trading_domfloat_main.png"));
            Console.WriteLine($"DOM afuera: {(dw != null ? dw.Title : "no")} · flotante={tv.DomIsFloating}");
            tv.DockDom();
            Pump(1);
            Console.WriteLine($"DOM de vuelta: flotante={tv.DomIsFloating}");
            continue;
        }
        else if (action == "domsettings")
        {
            var settingsWin = tv.ShowDomSettings();
            Pump(1.5);
            var dshot = settingsWin?.CaptureRenderedFrame();
            dshot?.Save(Path.Combine(outDir, shotName));
            Console.WriteLine(dshot != null ? $"Captura: {Path.Combine(outDir, shotName)}" : "No encontré la ventana del DOM");
            settingsWin?.Close();
            continue;
        }

        Pump(0.5);
        var tshot = window.CaptureRenderedFrame();
        tshot?.Save(Path.Combine(outDir, shotName));
        Console.WriteLine($"Captura: {Path.Combine(outDir, shotName)}");
        continue;
    }

    // acciones opcionales: strategies+backtest, strategies+optimize, strategies+code…
    if (parts.Length > 1 && parts[0] == "strategies")
    {
        var pagesField = typeof(MainWindow).GetField("_pages", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var view = ((System.Collections.Generic.Dictionary<string, Avalonia.Controls.Control>)pagesField.GetValue(window)!)["strategies"];
        var t = view.GetType();
        for (int i = 0; i < 200 && t.GetField("_current", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(view) is { } cur && cur.GetType().GetProperty("Type")!.GetValue(cur) == null; i++) Pump(0.05);
        if (parts.Length > 2)
        {
            // elige la estrategia por nombre de archivo
            var catalog = t.GetField("_catalog", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(view)!;
            var files = (System.Collections.IEnumerable)catalog.GetType().GetProperty("Files")!.GetValue(catalog)!;
            var file = files.Cast<object>().First(f => (string)f.GetType().GetProperty("FileName")!.GetValue(f)! == parts[2]);
            var sel = (Task)t.GetMethod("SelectAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(view, new[] { file })!;
            while (!sel.IsCompleted) Pump(0.05);
        }

        if (parts[1] == "claudefloat")
        {
            // conversación de ejemplo en el historial (carpeta de datos aparte) y chat en una ventana
            var conv = new TradingHub.App.Services.ChatConversation { Title = "Creá una estrategia de ruptura del rango de 30 minutos", SessionId = "prueba", Strategy = "Ruptura.cs" };
            conv.Messages.Add(new TradingHub.App.Services.ChatMessage { Role = "user", Text = conv.Title });
            conv.Messages.Add(new TradingHub.App.Services.ChatMessage { Role = "assistant", Text = "Listo: creé Ruptura.cs con la ruptura del rango de los primeros 30 minutos, stop por ATR y salida al cierre." });
            TradingHub.App.Services.ChatHistory.Save(conv);
            t.GetMethod("ShowTab", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(view, new object[] { "code" });
            Pump(0.5);
            t.GetMethod("LoadConversation", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(view, new object[] { conv });
            t.GetMethod("DetachClaude", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(view, null);
            Pump(1.5);
            var cw = (Avalonia.Controls.Window?)t.GetField("_claudeWindow", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(view);
            cw?.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "strategies_claudefloat_window.png"));
            window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "strategies_claudefloat_main.png"));
            Console.WriteLine($"Chat afuera: {(cw != null ? cw.Title : "no")} · conversaciones en el historial: {TradingHub.App.Services.ChatHistory.List().Count}");
            t.GetMethod("DockClaude", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(view, null);
            Pump(0.8);
            Console.WriteLine($"Chat de vuelta: {(t.GetField("_claudeWindow", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(view) == null ? "sí" : "no")}");
            continue;
        }

        if (parts[1] is "claude" or "claudelab")
        {
            t.GetMethod("ShowTab", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(view, new object[] { "code" });
            if (parts[1] == "claudelab")
                ((Avalonia.Controls.Primitives.ToggleButton)t.GetField("_labToggle", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(view)!).IsChecked = true;
            string ask = parts[1] == "claudelab" ? "Investigá y optimizá la estrategia abierta" : "Creá una estrategia que opere la ruptura del rango de los primeros 30 minutos";
            var send = (Task)t.GetMethod("SendToClaudeAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(view, new object[] { ask })!;
            var until = DateTime.UtcNow.AddSeconds(40);
            while (!send.IsCompleted && DateTime.UtcNow < until) Pump(0.05);
            Pump(2.5);
            var frame2 = window.CaptureRenderedFrame();
            var path2 = Path.Combine(outDir, $"strategies_{parts[1]}.png");
            frame2?.Save(path2);
            Console.WriteLine($"Captura: {path2}");
            continue;
        }

        string method = parts[1] switch { "optimize" => "RunOptimizationAsync", "wfo" => "RunWalkForwardAsync", _ => "RunBacktestAsync" };
        if (parts[1] is "code" or "history" or "reports" or "portfolio" or "cartera")
        {
            t.GetMethod("ShowTab", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(view, new object[] { parts[1] });
        }
        else
        {
            var tabId = parts[1] switch { "optimize" => "optimize", "wfo" => "wfo", _ => "backtest" };
            t.GetMethod("ShowTab", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(view, new object[] { tabId });
            var task = (Task)t.GetMethod(method, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(view, null)!;
            var limit = DateTime.UtcNow.AddMinutes(4);
            while (!task.IsCompleted && DateTime.UtcNow < limit) Pump(0.05);
        }

        Pump(0.8);
    }

    var frame = window.CaptureRenderedFrame();
    var path = Path.Combine(outDir, page.Replace('+', '_') + ".png");
    frame?.Save(path);
    Console.WriteLine(frame != null ? $"Captura: {path}" : $"No se pudo capturar {page}");
    ReportUntranslated(window, page);
}

App.Services.Shutdown();
GC.KeepAlive(snapLock); // el candado vale hasta el final
return exitCode;
