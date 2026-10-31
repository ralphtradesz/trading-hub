using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using TradingHub.App.Controls;
using TradingHub.App.Services;
using TradingHub.Strategies;

namespace TradingHub.App.Views;

/// <summary>
/// Portafolio de estrategias: combina corridas guardadas de varias estrategias (o instrumentos), mide la
/// correlación de sus resultados diarios y busca combinaciones poco correlacionadas.
/// </summary>
internal sealed partial class StrategiesView
{
    private sealed class PortfolioState
    {
        public decimal Capital { get; set; } = 10_000;
        public string Mode { get; set; } = "equal";
        public List<PortfolioItem> Items { get; set; } = new();
    }

    private sealed class PortfolioItem
    {
        public string StrategyId { get; set; } = "";
        public string RunId { get; set; } = "";
        public decimal Weight { get; set; } = 1;
    }

    private static string PortfolioFile => Path.Combine(StrategyStore.Root, "portafolio.json");
    private static readonly CultureInfo EsAr = TradingHub.Common.UiCulture.Current;

    private PortfolioState _pf = new();
    private readonly List<PortfolioComponent> _pfComponents = new();
    private readonly StackPanel _pfList = new() { Spacing = 8 };
    private readonly WrapPanel _pfKpis = new() { ItemSpacing = 10, LineSpacing = 10 };
    private readonly EquityChart _pfChart = new() { Height = 300, EmptyText = "Agregá al menos dos estrategias para ver la curva combinada" };
    private readonly Grid _pfMatrix = new();
    private readonly TextBlock _pfNote = new() { Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap };
    private readonly NumericUpDown _pfCapital = new() { Minimum = 100, Maximum = 100_000_000, Increment = 1000, FormatString = "N0", Width = 150 };
    private Ui.Segmented<string>? _pfMode;
    private Button? _pfAdd;
    private PortfolioResult? _pfResult;
    private bool _pfLoading;

    private Control BuildPortfolioPage()
    {
        _pfMode = new Ui.Segmented<string>(new[]
        {
            ("equal", "Igual", (string?)"Todas con peso 1 (como en sus backtests)"),
            ("risk", "Riesgo parejo", (string?)"Cada una pesa según la inversa de su volatilidad diaria"),
            ("manual", "Manual", (string?)"Elegís el peso de cada una"),
        });
        _pfMode.SelectionChanged += m =>
        {
            _pf.Mode = m;
            ApplyPortfolioMode();
            RecomputePortfolio();
        };
        _pfCapital.ValueChanged += (_, _) =>
        {
            if (_pfLoading) return;
            _pf.Capital = _pfCapital.Value ?? 10_000;
            RecomputePortfolio();
        };

        _pfAdd = Ui.Button("Agregar", MaterialIconKind.Plus, () => { }, "primary");
        _pfAdd.Flyout = new MenuFlyout();
        _pfAdd.Click += (_, _) => FillPortfolioMenu();
        var suggest = Ui.Button("Sugerir combinación", MaterialIconKind.ScaleBalance, SuggestPortfolio);
        ToolTip.SetTip(suggest, "Busca entre los últimos backtests de todas tus estrategias la combinación poco correlacionada (< 0,5) que más mejora el Sharpe");
        var report = Ui.Button("Informe", MaterialIconKind.FileChartOutline, PortfolioReport);
        ToolTip.SetTip(report, "Informe profesional del portafolio (se abre en Reportes)");

        _pfCapital.HorizontalAlignment = HorizontalAlignment.Left;
        var intro = Ui.Faint("Combiná corridas guardadas de varias estrategias o instrumentos. Las que no están correlacionadas bajan el drawdown del conjunto.");
        intro.TextWrapping = TextWrapping.Wrap;
        var left = Ui.V(14,
            Ui.H2("Portafolio"),
            intro,
            Ui.H(8, _pfAdd, suggest),
            Ui.Field("Pesos", _pfMode),
            Ui.Field("Capital", _pfCapital),
            Ui.H3("Estrategias"),
            _pfList,
            report);
        left.Margin = new Thickness(18, 16);

        var right = Ui.V(16,
            _pfKpis,
            Ui.Card(_pfChart, 8),
            Ui.H3("Correlación de los resultados diarios"),
            Ui.Faint("1 = se mueven igual · 0 = independientes · −1 = opuestas. Para diversificar conviene que estén por debajo de 0,5.").Also(t => t.TextWrapping = TextWrapping.Wrap),
            _pfMatrix,
            _pfNote);
        right.Margin = new Thickness(20, 16, 24, 24);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("390,1,*") };
        grid.Children.Add(new Border { Child = new ScrollViewer { Content = left }, Background = Ui.Bg1 });
        var split = new GridSplitter { ResizeDirection = GridResizeDirection.Columns, Background = Ui.Line };
        Grid.SetColumn(split, 1);
        grid.Children.Add(split);
        var scroll = new ScrollViewer { Content = right };
        Grid.SetColumn(scroll, 2);
        grid.Children.Add(scroll);
        return grid;
    }

    /// <summary>Todos los backtests guardados de todas las estrategias.</summary>
    private List<(StrategyFile File, StrategyRunSummary Run)> AvailableRuns()
    {
        var res = new List<(StrategyFile, StrategyRunSummary)>();
        foreach (var f in _catalog.Files)
        {
            if (f.Type == null && _catalog.Compile(f).Success == false) continue;
            foreach (var r in StrategyStore.List(f.Id).Where(r => r.Kind == RunKind.Backtest && r.Trades > 0).Take(30)) res.Add((f, r));
        }

        return res;
    }

    private void FillPortfolioMenu()
    {
        if (_pfAdd == null) return;
        var menu = new MenuFlyout();
        var runs = AvailableRuns();
        if (runs.Count == 0) menu.Items.Add(new MenuItem { Header = "No hay backtests guardados todavía", IsEnabled = false });
        foreach (var g in runs.GroupBy(r => r.File))
        {
            var parent = new MenuItem { Header = g.Key.DisplayName, Icon = Ui.Icon(MaterialIconKind.RobotOutline, 15) };
            foreach (var (file, r) in g)
            {
                bool inside = _pf.Items.Any(i => i.RunId == r.Id);
                var item = new MenuItem
                {
                    Header = $"{r.Symbol} {r.Period} · {r.FromUtc:dd/MM/yy}–{r.ToUtc:dd/MM/yy} · neto {r.NetProfit.ToString("N0", EsAr)} · Sharpe {r.Sharpe:0.00}",
                    IsEnabled = !inside,
                    Icon = inside ? Ui.Icon(MaterialIconKind.Check, 15, Ui.Accent) : null,
                };
                var f = file;
                var run = r;
                item.Click += (_, _) =>
                {
                    _pf.Items.Add(new PortfolioItem { StrategyId = f.Id, RunId = run.Id });
                    LoadPortfolioComponents();
                };
                parent.Items.Add(item);
            }

            menu.Items.Add(parent);
        }

        _pfAdd.Flyout = menu;
        menu.ShowAt(_pfAdd);
    }

    private void LoadPortfolio()
    {
        try
        {
            if (File.Exists(PortfolioFile)) _pf = JsonSerializer.Deserialize<PortfolioState>(File.ReadAllText(PortfolioFile)) ?? new PortfolioState();
        }
        catch
        {
            _pf = new PortfolioState();
        }

        _pfLoading = true;
        _pfCapital.Value = _pf.Capital;
        _pfMode?.Select(_pf.Mode);
        _pfLoading = false;
        LoadPortfolioComponents();
    }

    private void SavePortfolio()
    {
        try
        {
            Directory.CreateDirectory(StrategyStore.Root);
            File.WriteAllText(PortfolioFile, JsonSerializer.Serialize(_pf, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // no es grave
        }
    }

    private void LoadPortfolioComponents()
    {
        _pfComponents.Clear();
        foreach (var item in _pf.Items.ToList())
        {
            var run = StrategyStore.Load(item.StrategyId, item.RunId);
            if (run?.Trades == null)
            {
                _pf.Items.Remove(item); // se borró la corrida
                continue;
            }

            _pfComponents.Add(PortfolioComponent.FromRun(run, item.Weight));
        }

        ApplyPortfolioMode();
        RecomputePortfolio();
    }

    private void ApplyPortfolioMode()
    {
        if (_pfComponents.Count == 0) return;
        if (_pf.Mode == "equal")
            foreach (var c in _pfComponents) c.Weight = 1;
        else if (_pf.Mode == "risk")
        {
            var w = PortfolioAnalyzer.InverseVolatilityWeights(_pfComponents, _pf.Capital);
            for (int i = 0; i < _pfComponents.Count; i++) _pfComponents[i].Weight = w[i];
        }

        for (int i = 0; i < _pfComponents.Count && i < _pf.Items.Count; i++) _pf.Items[i].Weight = _pfComponents[i].Weight;
    }

    private void RecomputePortfolio()
    {
        SavePortfolio();
        _pfList.Children.Clear();
        _pfKpis.Children.Clear();
        _pfMatrix.Children.Clear();
        _pfMatrix.RowDefinitions.Clear();
        _pfMatrix.ColumnDefinitions.Clear();
        _pfResult = _pfComponents.Count > 0 ? PortfolioAnalyzer.Analyze(_pfComponents, _pf.Capital) : null;

        for (int i = 0; i < _pfComponents.Count; i++)
        {
            int index = i;
            var c = _pfComponents[i];
            var m = _pfResult?.ComponentMetrics[i] ?? c.Metrics;
            var weight = new NumericUpDown { Minimum = 0, Maximum = 100, Increment = 0.25m, FormatString = "0.##", Value = c.Weight, Width = 150, IsEnabled = _pf.Mode == "manual" };
            weight.ValueChanged += (_, _) =>
            {
                if (_pf.Mode != "manual") return;
                c.Weight = weight.Value ?? 1;
                _pf.Items[index].Weight = c.Weight;
                RecomputePortfolio();
            };
            var remove = Ui.IconButton(MaterialIconKind.Close, "Sacar del portafolio", () =>
            {
                _pf.Items.RemoveAt(index);
                _pfComponents.RemoveAt(index);
                ApplyPortfolioMode();
                RecomputePortfolio();
            }, 15);
            var head = new DockPanel();
            DockPanel.SetDock(remove, Dock.Right);
            head.Children.Add(remove);
            head.Children.Add(new TextBlock { Text = $"{i + 1}. {c.StrategyName}", FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var info = new TextBlock
            {
                Text = $"{c.Settings.Symbol} {c.Settings.Period} · {c.Settings.FromUtc:dd/MM/yy}–{c.Settings.ToUtc:dd/MM/yy}\nNeto {m.NetProfit.ToString("N0", EsAr)} · Sharpe {m.SharpeRatio:0.00} · DD {m.MaxDrawdown.ToString("N0", EsAr)} · {m.TotalTrades} ops",
                Classes = { "faint", "small" },
                TextWrapping = TextWrapping.Wrap,
            };
            var wrow = Ui.H(8, new TextBlock { Text = "Peso", Classes = { "muted", "small" }, VerticalAlignment = VerticalAlignment.Center }, weight);
            _pfList.Children.Add(Ui.Card(Ui.V(6, head, info, wrow), 12));
        }

        if (_pfComponents.Count == 0)
            _pfList.Children.Add(new TextBlock { Text = "Todavía no agregaste estrategias. Usá «Agregar» o «Sugerir combinación».", Classes = { "muted", "small" }, TextWrapping = TextWrapping.Wrap });

        var r = _pfResult;
        if (r == null || _pfComponents.Count == 0)
        {
            _pfChart.SetData(Array.Empty<EquityPoint>(), _pf.Capital);
            _pfNote.Text = "";
            return;
        }

        Control Kpi(string label, string value, string sub, IBrush? color = null) => Ui.Card(Ui.V(3,
            new TextBlock { Text = label, Classes = { "faint", "small" } },
            new TextBlock { Text = value, FontSize = 20, FontWeight = FontWeight.Bold, Foreground = color ?? Ui.Text0 },
            new TextBlock { Text = sub, Classes = { "faint", "small" } }), 12).Also(b => b.MinWidth = 170);
        var pm = r.Metrics;
        _pfKpis.Children.Add(Kpi("Neto del portafolio", pm.NetProfit.ToString("N2", EsAr), $"{pm.ReturnPercent:0.0} % · {pm.TotalTrades} ops", pm.NetProfit >= 0 ? Ui.Up : Ui.Down));
        _pfKpis.Children.Add(Kpi("Sharpe", pm.SharpeRatio.ToString("0.00", EsAr), $"promedio individual {r.AverageComponentSharpe:0.00}"));
        _pfKpis.Children.Add(Kpi("Drawdown máximo", pm.MaxDrawdown.ToString("N0", EsAr), $"suma individual {r.SumOfComponentDrawdowns.ToString("N0", EsAr)}"));
        _pfKpis.Children.Add(Kpi("Reducción de drawdown", $"{r.DrawdownReductionPercent:0} %", "por diversificar", r.DrawdownReductionPercent > 0 ? Ui.Up : Ui.Text1));
        _pfKpis.Children.Add(Kpi("Correlación promedio", r.AverageCorrelation.ToString("0.00", EsAr), $"máxima {r.MaxCorrelation:0.00}", r.MaxCorrelation > 0.5 ? Ui.Warn : Ui.Up));

        // curva combinada y la de cada estrategia
        var extra = new List<IReadOnlyList<decimal>>();
        for (int i = 0; i < r.Components.Count; i++)
        {
            decimal eq = _pf.Capital;
            var curve = new List<decimal> { eq };
            foreach (var d in r.DailyPnl[i])
            {
                eq += d;
                curve.Add(eq);
            }

            extra.Add(curve);
        }

        _pfChart.SetData(r.Equity, _pf.Capital, extra);

        // matriz de correlación
        int n = r.Components.Count;
        _pfMatrix.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(260)));
        for (int j = 0; j < n; j++) _pfMatrix.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(74)));
        for (int i = 0; i <= n; i++) _pfMatrix.RowDefinitions.Add(new RowDefinition(new GridLength(i == 0 ? 28 : 46)));
        for (int j = 0; j < n; j++)
        {
            var h = new TextBlock { Text = (j + 1).ToString(CultureInfo.InvariantCulture), HorizontalAlignment = HorizontalAlignment.Center, Classes = { "muted" } };
            Grid.SetColumn(h, j + 1);
            _pfMatrix.Children.Add(h);
        }

        for (int i = 0; i < n; i++)
        {
            var label = new TextBlock { Text = $"{i + 1}. {r.Components[i].Label}", VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 10, 0) };
            Grid.SetRow(label, i + 1);
            _pfMatrix.Children.Add(label);
            for (int j = 0; j < n; j++)
            {
                double v = r.Correlation[i, j];
                byte alpha = (byte)(40 + 190 * Math.Min(1, Math.Abs(v)));
                var bg = i == j ? Ui.Bg3 : new SolidColorBrush(v >= 0 ? Color.FromArgb(alpha, 220, 53, 69) : Color.FromArgb(alpha, 37, 99, 235));
                var cell = new Border
                {
                    Background = bg,
                    CornerRadius = new CornerRadius(6),
                    Margin = new Thickness(3),
                    Child = new TextBlock { Text = v.ToString("0.00", EsAr), FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.White },
                };
                ToolTip.SetTip(cell, $"{r.Components[i].Label} vs {r.Components[j].Label}: {v:0.00}");
                Grid.SetRow(cell, i + 1);
                Grid.SetColumn(cell, j + 1);
                _pfMatrix.Children.Add(cell);
            }
        }

        _pfNote.Text = r.NoOverlap
            ? "⚠ Estas corridas no comparten fechas: se sumó la unión de los períodos y la correlación no es confiable. Corré los backtests en el mismo rango."
            : $"Período común: {r.From:dd/MM/yyyy} – {r.To:dd/MM/yyyy} ({r.Days.Count} días). Los resultados se suman día por día con el peso de cada estrategia.";
    }

    private void SuggestPortfolio()
    {
        var candidates = new List<PortfolioComponent>();
        var ids = new List<(string StrategyId, string RunId)>();
        foreach (var f in _catalog.Files)
        {
            if (f.Type == null && _catalog.Compile(f).Success == false) continue;
            foreach (var g in StrategyStore.List(f.Id).Where(r => r.Kind == RunKind.Backtest).GroupBy(r => (r.Symbol, r.Period)))
            {
                var latest = g.OrderByDescending(r => r.CreatedUtc).First();
                var run = StrategyStore.Load(f.Id, latest.Id);
                if (run?.Trades is not { Count: >= 5 }) continue;
                candidates.Add(PortfolioComponent.FromRun(run));
                ids.Add((f.Id, run.Id));
            }
        }

        if (candidates.Count < 2)
        {
            _status("Para sugerir un portafolio hacen falta backtests de al menos dos estrategias o instrumentos (con 5 operaciones o más).");
            return;
        }

        var chosen = PortfolioAnalyzer.SuggestUncorrelated(candidates, _pf.Capital);
        if (chosen.Count == 0)
        {
            _status("Ninguna estrategia tiene resultado positivo: no hay qué combinar todavía.");
            return;
        }

        _pf.Items = chosen.Select(i => new PortfolioItem { StrategyId = ids[i].StrategyId, RunId = ids[i].RunId }).ToList();
        LoadPortfolioComponents();
        _status(chosen.Count == 1
            ? "Solo una estrategia mejora el conjunto: las demás están muy correlacionadas o pierden."
            : $"Portafolio sugerido: {chosen.Count} estrategias poco correlacionadas entre {candidates.Count} candidatas.");
    }

    private void PortfolioReport()
    {
        if (_pfResult == null || _pfComponents.Count == 0)
        {
            _status("Agregá estrategias al portafolio primero.");
            return;
        }

        try
        {
            var input = new Lab.ReportInput
            {
                Title = $"Portafolio de {_pfComponents.Count} estrategias",
                Portfolio = _pfResult,
                PortfolioCapital = _pf.Capital,
            };
            var (markdown, images) = Lab.ReportBuilder.Build(input);
            var saved = Lab.ReportStore.Save(input.Title, markdown, null, "", images);
            RefreshReports();
            OpenReport(saved.Path);
        }
        catch (Exception ex)
        {
            _status("No se pudo armar el informe: " + ex.Message);
            CrashLog.Write(ex);
        }
    }
}
