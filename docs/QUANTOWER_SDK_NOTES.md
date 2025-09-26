# Quantower SDK / API Notes

_Last reviewed: 2025-09-22_

## Supported Platform & Runtime
- **Quantower Versions:** Stable `1.144.12` (17 Sep 2025), Beta `1.144.12` (17 Sep 2025). Monitor release notes for API changes.
- **OS:** Windows 10/11 (64-bit). Quantower is Windows-only.
- **.NET Runtime:** `net8.0-windows` (Quantower Algo ships with .NET 8 assemblies). Install .NET 8 SDK + desktop runtime.
- **IDE:** Visual Studio 2022 v17.8+ (Community acceptable) with ".NET desktop development" workload. Quantower Algo VS extension scaffolds projects automatically.

## Project Setup
- **Assemblies:** Reference `TradingPlatform.BusinessLayer.dll`, `TradingPlatform.Common.dll`, and any UI/plugin DLLs from the Quantower installation (e.g. `<Quantower>\TradingPlatform\v1.144.12\bin`).
- **Project Targets:** Build for `net8.0-windows`, prefer `x64`. Add post-build step to copy DLLs into Quantower settings.
- **Deployment Paths:**
  - Strategies/indicators: `%UserProfile%\Documents\Quantower\Settings\Scripts\Strategies` (or `...\Scripts\Indicators`).
  - UI/Plugin DLLs: `%UserProfile%\Documents\Quantower\Settings\Scripts\plugins\<PluginName>` (or the equivalent folder under a portable installation).
- **Versioning:** Maintain per-version copies of Quantower assemblies; update project references when upgrading the platform.

## Lifecycle & Entry Points
- **Strategy Base Class:** `Strategy` with overrides `OnCreated`, `OnRun`, `OnStop`, `OnRemove`, `OnGetMetrics`.
- **Indicators:** Inherit `Indicator`; implement `IWatchlistIndicator` for watchlist support.
- **Plugins/UI:** Implement `IAddOn`, `IPanel`, etc., depending on component type.
- **Core Access:** `Core.Instance` exposes `Connections`, `Accounts`, `Symbols`, `Orders`, `Positions`, `Trades`. Use it as central API hub.
- **Events:** Subscribe to `TradeAdded/Updated/Removed`, `OrderAdded/Updated/Removed`, `PositionAdded/Removed` to react to portfolio changes.

## Order & Trade Management
- **Placing Orders:** `Core.Instance.PlaceOrder(PlaceOrderRequestParameters)` with params for account, symbol, side, order type, quantity, price, etc.
- **Modifying Orders:** `Core.Instance.ModifyOrder` or `order.Modify` helpers.
- **Cancelling Orders:** `Core.Instance.CancelOrder` or `order.Cancel()`.
- **Closing Positions:** `Core.Instance.ClosePosition(position, quantity)`.
- **Identifiers:** `Order.Id`, `Order.PositionId`, `Trade.TradeId`, `Trade.PositionId`. Use `PositionId` to link hedging legs and positions.

## Logging & Diagnostics
- **Strategy Logging:** `Log(message, StrategyLoggingLevel)` writes to the strategy log panel.
- **Central Logging:** Use `Core.Instance.Loggers` or the Quantower Event Log panel for aggregated output.
- **Debugging:** Enable "Allow connection from Visual Studio" in Quantower, attach VS debugger, run strategy from Backtest & Optimize panel.

## Distribution
- **Packaging:** No signing required. Ship compiled DLL in zip with deployment instructions.
- **Installation:** Users copy DLL (and resources) into the appropriate `Settings\Scripts` subdirectory (Strategies, Indicators, or `plugins`). One plugin per subfolder recommended.
- **Compatibility Testing:** Validate against both stable and beta builds; update references when platform upgrades.

## Caveats
- **Multi-connection Awareness:** Always specify the correct `ConnectionId` when working with accounts or symbols in multi-broker environments.
- **Threading:** Quantower APIs are generally not thread-safe; marshal updates back to the UI thread as needed.
- **Error Handling:** Wrap operations in try/catch; unhandled exceptions propagate into the Quantower host process.
- **Performance:** Avoid blocking calls on Quantower's main thread—offload heavy work to background tasks, but perform order submissions on the main thread.

## References
- Quantower API docs: <https://api.quantower.com/>
- Quantower Algo examples: <https://help.quantower.com/quantower/quantower-algo>
- Debugging guide: <https://help.quantower.com/quantower/quantower-algo/debugging-in-vs-2022>
