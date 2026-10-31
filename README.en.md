[Español](README.md) · **English** · [中文](README.zh-CN.md)

<div align="center">

# Trading Hub

**The free, open-source trading platform that runs 100% on your computer: Mac and Windows.**
Footprint charts, DOM trading, professional anti-overfitting backtesting and AI strategies,
with your data and your keys always on your computer.

Created by [**@ralphtradesz**](https://x.com/ralphtradesz)

Using Windows? **[Download the installer](https://github.com/ralphtradesz/trading-hub/releases/latest/download/TradingHub-Setup-x64.exe)** ([how to install it](#using-windows)).

[![License MIT + Commons Clause](https://img.shields.io/badge/license-MIT%20%2B%20Commons%20Clause-amber.svg)](LICENSE)
![macOS Apple Silicon](https://img.shields.io/badge/macOS-Apple%20Silicon-black.svg)
![Windows 10 and 11](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6.svg)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4.svg)
![Avalonia](https://img.shields.io/badge/UI-Avalonia-8B44AC.svg)

![Trading Hub](docs/img/trading.png)

</div>

---

## What it is

Trading Hub is a free, open-source desktop trading platform. Everything runs locally: the app, the data you record or
download, your strategies, the backtests and your broker keys (which go to the macOS Keychain, never to files). There
are no servers of its own, no accounts to create and no telemetry.

It works with **CME futures** (Rithmic, Tradovate and Databento history), **crypto** (Binance and Bybit, with free data
and no keys), **forex and CFDs** (OANDA, cTrader or CSV files) and **US stocks** (Alpaca).

The AI comes from **Claude Code with your own subscription** (no API keys): an assistant that writes C# strategies,
compiles them, tests them and puts together reports, and an agent that allocates risk among your live strategies.

## Features

### Charts
- Time, tick, volume or range bars; candles, OHLC bars, line or Heikin Ashi.
- **Footprint** configurable column by column (bid/ask, delta, volume, POC, profile per candle) with its own editor.
- DOM, Time & Sales, drawings and templates.
- Built-in indicators: moving averages, VWAP with deviation bands, Bollinger, RSI, MACD, volume, delta, cumulative delta
  (CVD), session volume profile and big trades. Add your own in C# (they recompile when you save), compatible with the
  ATAS indicator API, plus the ATAS ones or others from public source code: see
  [More indicators](#more-indicators).
- **Replay** any saved day, at whatever speed you want.

### Trading
- **Chart Trader** and DOM with market, limit and stop orders, brackets (stop and targets) and OCO.
- Simulated accounts included, plus the real accounts from your connections; history and statistics per account.
- On the simulated account nothing gets in the way of your click; on real accounts you can ask for a confirmation before
  each order (it can be turned off in Settings). Safety comes from risk limits.

### Data
- Everything you watch is recorded automatically, tick by tick, with the aggressor side.
- Downloads: public files from Binance and Bybit, history from Rithmic and from your connections.
- **Databento**: years of tick-by-tick CME futures (pay as you go, it shows you the cost first). It's stored separately,
  and in backtests you choose **Rithmic only, Databento only or both combined** (Rithmic first, and Databento fills in
  what's missing).
- CSV import from Dukascopy, HistData, MetaTrader or TradingView.

### Strategies and backtesting
- Strategies in **C#** with a built-in editor that compiles in milliseconds (Roslyn, with errors marked in the code)
  and a simple API: `EnterLong()`, `StopLossTicks`, indicators, time-of-day and market-context filters.
- Backtests on every real trade recorded, with commissions, slippage and realistic limit order fills.
- **Bayesian optimization** (TPE) and **smart walk-forward**: it picks stable plateaus instead of peaks, compresses
  ticks to go faster and measures overfitting with **PBO (CSCV)** and **CPCV with embargo**. The library also includes
  the **deflated Sharpe ratio** and a full validation you can run from code.
- The **timeframe** is optimized like any other parameter (M1 to D1), and your **favorite** strategies stay at the top
  of the list.
- **Monte Carlo**, results **by market condition** (range, mixed or trend; low, normal or high volatility) and
  robustness metrics so you don't mistake luck for an edge.

### Live
- Run strategies on your accounts from the backtester or from a chart, with a maximum size, daily loss and drawdown.
  On the chart you see how it did over its history: the actual result with costs, a mini curve and all the statistics.
- Portfolios, a **Claude risk agent** that proposes the size of each strategy (you approve it on real accounts), each
  market's condition in real time and a panel in the Mac menu bar.
- Automatic resume on launch and a no-sleep mode (even with the lid closed, if you want).

### Claude
- A chat that writes and fixes strategies, and a **lab** (an MCP server in the app itself) where Claude runs
  backtests, optimizations and walk-forwards and leaves you a professional report with all the metrics.

## Screenshots

| | |
|---|---|
| ![Footprint](docs/img/footprint.png) | ![Trading from the chart](docs/img/operar.png) |
| Footprint with delta, POC and big trades | Chart Trader, DOM and Time & Sales |
| ![Strategies with Claude](docs/img/claude.png) | ![Backtesting](docs/img/backtest.png) |
| C# strategies with the Claude assistant | Backtesting with results by market condition |
| ![Data and Databento](docs/img/datos.png) | ![Light theme](docs/img/claro.png) |
| Tick-by-tick history and Databento | Light or dark theme |

## Installation

### The easiest way: with Claude Code
If you have [Claude Code](https://claude.com/claude-code), open a Terminal in this folder, run `claude` and ask it:

```
Install Trading Hub on my Mac following CLAUDE.md.
```

It checks your Mac, installs whatever is missing (no administrator password), builds the app and puts it in
Applications. The step-by-step guide is in [INSTALAR_CON_CLAUDE_CODE.md](INSTALAR_CON_CLAUDE_CODE.md).

### Manually
You need a Mac with Apple Silicon and macOS 12 or newer (for Windows, see [Using Windows?](#using-windows)).

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
bash /tmp/dotnet-install.sh --channel 10.0
git clone https://github.com/ralphtradesz/trading-hub.git
cd trading-hub
./build-app.sh --install
```

The first build takes a few minutes. The app ends up in `/Applications/Trading Hub.app` and starts with a simulated
account: nothing trades real money until you enable it in Connections. More details in
[docs/instalacion.md](docs/instalacion.md).

## Using Windows?

Trading Hub runs on Windows 10 and 11 with a one-click installer: there's no need to build anything or install .NET.

1. Download the installer:
   - **[PC with Intel or AMD (most PCs)](https://github.com/ralphtradesz/trading-hub/releases/latest/download/TradingHub-Setup-x64.exe)**
   - [PC with Windows on ARM (Snapdragon, Surface Pro X)](https://github.com/ralphtradesz/trading-hub/releases/latest/download/TradingHub-Setup-arm64.exe)
2. Open it. Since the installer isn't signed with a paid certificate yet, Windows may show "Windows protected your
   PC": click **More info** and then **Run anyway**.
3. Click **Install**. It doesn't ask for administrator rights: it installs for your user only, with shortcuts in the
   Start menu and on the desktop. When it's done, **Open Trading Hub**.

It starts with a simulated account, same as on Mac. Your data stays in `%APPDATA%\Trading Hub`, your strategies and
indicators in `Documents\Trading Hub` and your keys in the **Windows Credential Manager**. To update, install the new
version over the old one (your data isn't touched); to uninstall, **Settings → Apps → Trading Hub → Uninstall**.

To build it yourself on Windows: [Git](https://git-scm.com), the [.NET 10 SDK](https://dotnet.microsoft.com/download)
and [NSIS](https://nsis.sourceforge.io) (`winget install NSIS.NSIS`), then `.\build-windows.ps1` in the repository
folder (it leaves the installers in `dist`). From a Mac: `brew install makensis` and `./build-windows.sh`.
What depends on each operating system is in [PORTAR_A_WINDOWS.md](PORTAR_A_WINDOWS.md).

## More indicators

Trading Hub comes with its own indicators (moving averages, VWAP, Bollinger, RSI, MACD, volume, delta, CVD, volume
profile and big trades). Those from other platforms aren't in the repository because their code doesn't have a license
that allows redistributing it, but **you can add them yourself** from their public source code:

**All the ATAS ones at once** (about 250, from their public repository
[AtasPlatform/Indicators](https://github.com/AtasPlatform/Indicators)):

```bash
git clone https://github.com/AtasPlatform/Indicators.git third_party/ATAS-Indicators
python3 tools/port_atas.py
./build-app.sh --install
```

The script adapts the ATAS namespaces to Trading Hub's without touching the logic (and fixes a couple of details so
they compile), and they show up in the indicator library with the others. To update them when ATAS publishes changes:
`git -C third_party/ATAS-Indicators pull` and repeat the last two steps. The two folders it creates are in
`.gitignore`, so they don't get pushed if you fork.

**A single one, from ATAS or any other public C# source code:** copy the `.cs` file into
`~/Documents/Trading Hub/Indicadores`. Trading Hub compiles it on its own, adapts the ATAS namespaces and adds it to
the library. If it uses another indicator (for example, the ATAS CCI uses its SMA), copy that one too.

Those indicators belong to their authors: use them according to each source's license. More details in
[docs/indicadores.md](docs/indicadores.md).

## Documentation (in Spanish)

| | |
|---|---|
| [Installation](docs/instalacion.md) | Requirements, building, updating and uninstalling |
| [Getting started](docs/primeros-pasos.md) | A tour of the app |
| [Connections](docs/conexiones.md) | Brokers, exchanges and data; real and simulated accounts |
| [Charts](docs/graficos.md) | Timeframes, footprint, DOM, drawings, indicators and replay |
| [Trading](docs/operar.md) | Chart Trader, DOM, orders, brackets and accounts |
| [Data](docs/datos.md) | Recording, downloads, Databento and CSV import |
| [Strategies](docs/estrategias.md) | The C# API with examples |
| [Backtesting](docs/backtesting.md) | Backtest, optimization, walk-forward, PBO, CPCV and Monte Carlo |
| [Live](docs/en-vivo.md) | Strategies on your accounts, portfolios and the risk agent |
| [Claude](docs/asistente-claude.md) | The strategy assistant and the MCP lab |
| [Indicators](docs/indicadores.md) | Built-in, your own and compatible with the ATAS API |
| [Settings](docs/ajustes.md) | All the options |
| [Privacy](docs/privacidad.md) | What stays on your Mac and what connects to the internet |
| [Development](docs/desarrollo.md) | Code structure, tests and scenes |
| [Windows](PORTAR_A_WINDOWS.md) | What depends on each operating system and how the Windows version was built |

## Support the project

Trading Hub is free and it will stay free. If it's useful to you and you want to support its development, you can
donate here:

| Currency | Network | Address |
|---|---|---|
| USDT | BEP20 (BNB Smart Chain) | `0xebedab9a1f6820914b0869e4126d5c54aa028a2a` |
| USDT | TRC20 (Tron) | `THAULimtnWpbQ8opZWxK8863nZEkyek4E5` |
| BTC | BEP20 (BNB Smart Chain) | `0xebedab9a1f6820914b0869e4126d5c54aa028a2a` |

Send each currency **only over the network listed**: anything sent over another network is lost. Thank you!

## Disclaimer

Trading in the markets involves the risk of losing money. Trading Hub is a tool: it doesn't give investment advice, and
backtest results don't guarantee future results. Always try things on the simulated account first. The software is
provided "as is", without warranty (see the [license](LICENSE)).

## License

[MIT with the Commons Clause](LICENSE): you can use Trading Hub for free (including to trade your own money), modify
it and share it, always with the license notice. What you can't do is **sell it**: not the app, not a modified
version, not a paid service whose value comes from Trading Hub (for example, charging to install it, host it or
support it). Because of that condition it isn't an "open source" license in the strict OSI sense: the code is public
and free, but it can't be commercialized.

Indicators ported from other platforms that don't have an open license aren't part of this repository, but you can
add them yourself from their public source code (see [More indicators](#more-indicators)).

---

<div align="center">

Made with care by [**@ralphtradesz**](https://x.com/ralphtradesz)

</div>
