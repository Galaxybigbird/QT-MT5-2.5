# Quantower Migration PRD

## 1. Purpose & Background

- **Goal**: replace the current NinjaTrader-based multi-strategy manager with a Quantower-native solution while keeping synchronized hedging with MT5 via the Go bridge.
- **Drivers**: Quantower offers deterministic TradeID semantics, native .NET 6 support, and multi-connection flexibility; NinjaTrader’s unreliable trade tracking and legacy .NET 4.8 constraints impede new features.
- **In Scope**: Go bridge (`BridgeApp`), Quantower trading add-on, shared gRPC client, MT5 EA integration, proto schema, automation scripts, logging/observability, documentation.
- **Out of Scope**: Historical NinjaTrader compatibility, HTTP/WebSocket fallbacks, non-gRPC transport layers.

## 2. High-Level Architecture Target

```
Quantower Add-on (.NET 6) ── gRPC ──> BridgeApp (Go) ── gRPC ──> MT5 EA (C++/MQL5)
          ↑                                            ↓
   Quantower Core API                         Unified logging / analytics
```

- Quantower add-on publishes trade life-cycle events with Quantower `Trade.Id` and `Position.Id`.
- BridgeApp stores Quantower identifiers centrally, translating to MT5 ticket management without NinjaTrader cross-references.
- MT5 EA consumes Quantower-originated trades, returns closure notifications tagged by TradeId/Strategy.

## 3. Success Criteria

1. Quantower add-on builds and runs against Quantower SDK (tested with Quantower v1.144.9 stable and v1.144.10 Beta) on net8.0-windows.
2. Bridge dispatches trades keyed by Quantower TradeId without `baseIdCrossRef` / `pendingCloses` heuristics.
3. MT5 EA opens/closes hedges using Quantower identifiers with <100ms additional latency compared to current flow.
4. Unified logging identifies source `qt` and correlates trades end-to-end.
5. `coderabbit review --plain` executed and addressed after each major implementation PR.

## 4. Workstreams & Detailed Tasks

### Minimum Development Requirements
- Windows 10 or Windows 11 (64-bit).
- .NET 8 SDK and runtime installed.
- Visual Studio 2022 v17.8 or newer (with .NET desktop development workload).

### 4.1 Discovery & Tooling
- [ ] Confirm Quantower SDK version and API surface (Core, Accounts, Orders, Trades, Portfolio).
- [ ] Collect example code from Quantower docs (`Core.Instance.TradeAdded`, `PositionAdded`, etc.).
- [ ] Document supported .NET runtime (Quantower stable 1.144.9 and beta 1.144.10 require net8.0-windows).
- [ ] Maintain SDK notes in `docs/QUANTOWER_SDK_NOTES.md`; keep environment setup in `docs/ENVIRONMENT_SETUP.md` up to date.

### 4.2 Proto & gRPC Contract
- [ ] Audit `BridgeApp/proto/trading.proto` for NT-specific fields (`nt_balance`, `NTTradeResult`, etc.).
- [ ] Draft schema vNext removing NinjaTrader-only fields, adding `strategy_tag`, `quantower_trade_id`, or `position_id` as needed.
- [ ] Update Makefile targets (`proto`, `proto-go`, `proto-csharp`, `proto-cpp`).
- [ ] Regenerate stubs in Go/C#/C++ and update all consumers.
- [ ] Maintain backward compatibility toggle or plan a synchronous cutover.

### 4.3 Bridge Refactor (Go)
- [ ] Introduce new `QuantowerApp` aggregate or refactor `App` to:
  - Track trades by Quantower `TradeId` -> MT5 ticket mapping.
  - Remove `baseIdCrossRef`, `pendingCloses`, `ntInitiatedTickets`, and NT sizing caches.
  - Replace `BaseID` semantics with Quantower `StrategyId`/`OrderId` as `base_id` values (document mapping).
  - Normalize queue metrics to floats (Quantower partial lots).
- [ ] Adjust `internal/grpc/server.go` handlers accordingly.
- [ ] Update bridge tests (`app_elastic_extract_test.go`, `app_pending_close_test.go`, etc.).
- [ ] Review logging, ensure new tags (`source=qt`, `quantower_trade_id`).
- [ ] Update CLI tools under `BridgeApp/tools/` for new message shapes.

### 4.4 Shared gRPC Client (C#)
- [ ] Replace the legacy `NTGrpcClient` with a Quantower-first `BridgeGrpcClient` targeting **net8.0-windows** only.
- [ ] Switch from `Grpc.Core` to `Grpc.Net.Client` (Quantower supports modern .NET).
- [ ] Expose Quantower-specific helpers (TradeId-based submission, portfolio snapshots).
- [ ] Update logging metadata (tags: `source=qt`, `component=qt_addon`).

### 4.5 Quantower Add-on Implementation
- [ ] Create new project (e.g., `QuantowerMultiStratAddOn.csproj`) under `MultiStratManagerRepo/Quantower/` targeting `net8.0-windows` (Quantower-bridge-MT5EA stack only — NinjaTrader retired).
- [ ] Implement lifecycle hooks:
  - Connect to Core events (`Core.Instance.TradeAdded`, `PositionAdded`, `PositionClosed`).
  - Manage configuration (accounts, symbols, hedging ratios) via Quantower panels.
  - Publish trades/elastic/trailing updates through `BridgeGrpcClient`.
- [ ] Port reusable logic (elastic trailing, SQLite if required) into platform-agnostic services.
- [ ] Implement UI using Quantower panel framework (WPF or WinForms host).
- [ ] Provide deployment instructions for Quantower (copy to `%AppData%\Quantower\Settings\Scripts\plugins`).

### 4.6 MT5 EA & Wrapper
- [ ] Update proto structures in `MT5/Generated/` after schema change.
- [ ] Modify EA (`ACHedgeMaster_gRPC.mq5`) to interpret Quantower `base_id`/`trade_id` semantics.
- [ ] Remove NinjaTrader-specific messaging (`NTTradeResult`, etc.).
- [ ] Validate closure acknowledgement flow using Quantower strategy tags.
- [ ] Rebuild C++ wrapper / DLLs and update deployment scripts.

### 4.7 Scripts, CI/CD, and Docs
- [ ] Adjust `scripts/*.ps1` to reference Quantower deployment steps.
- [ ] Update `README.md` overview, replacing NinjaTrader references with Quantower.
- [ ] Create migration guide `docs/QUANTOWER_MIGRATION_STATUS.md` capturing phase checkpoints.
- [ ] Update `docs/WORKFLOW_COORDINATION.md` to reflect new coordination steps.
- [ ] Ensure `OfficialFuturesHedgebotv2.5QT.sln` includes new projects.

### 4.8 Testing & Verification
- **Unit Tests**: Go `go test ./...`, C# unit tests for bridging logic (if feasible).
- **Integration**: Simulated Quantower trade stream -> Bridge -> MT5 stub; use `BridgeApp/tools/smoke` with updated payloads.
- **End-to-End**: Manual Quantower + MT5 demo account validation (document scenarios: new trade, partial close, elastic event, trailing update).
- **Regression**: Ensure MT5 EA still handles historical logs; verify `logs/unified-*.jsonl` format.
- **Automation**: Consider adding GitHub workflow for Go lint/test and .NET build (Quantower add-on).

### 4.9 Rollout Plan
1. **Phase 0** – Documentation & environment setup (this PRD, SDK validation).
2. **Phase 1** – Proto + shared client update (behind feature flag).
3. **Phase 2** – Quantower add-on prototype streaming to existing bridge (dual-mode support).
4. **Phase 3** – Bridge refactor toggling to TradeId storage.
5. **Phase 4** – MT5 EA adjustments and parallel testing.
6. **Phase 5** – Remove NinjaTrader artifacts, clean scripts, update docs.
7. **Phase 6** – Production rollout & monitoring (quantify metrics, fallback plan).

## 5. Dependencies & Risks

- **Dependencies**: Quantower SDK availability, gRPC .NET compatibility, MT5 API stability, dev environment licensing.
- **Risks**:
  - Trade synchronization regression if Quantower events misinterpreted.
  - Proto breaking change requires synchronized updates across all components.
  - Lack of automated Quantower testing harness; manual validation required.
  - Bridge queue semantics changing to float volumes may expose hidden assumptions in MT5 EA.
- **Mitigations**: phased rollout, dual-mode bridging (feature flags), thorough logging, snapshot backups of configs.

## 6. Code Review & Quality Process

1. For each major implementation branch, run `coderabbit review --plain` before requesting human review.
2. Capture the `coderabbit` report in the PR description or attach to issue tracking notes.
3. Address all actionable feedback; rerun the command if substantial changes follow.
4. Add checklist items to PR template referencing this PRD and the relevant phase.

## 7. Tracking & Reporting

- Maintain a status table in `docs/QUANTOWER_MIGRATION_STATUS.md` with columns: Phase, Owner, Start, End, Status, Notes.
- Use issue tracker tags `quantower` and `migration-phase-x` to group work.
- Weekly sync: summarize progress, blockers, upcoming tests.
- Update this PRD when scope or sequencing changes (record date and change summary in an appendix section).

## 8. Appendix

- **References**:
  - [Quantower API](https://api.quantower.com/)
  - [Quantower Portfolio Access](https://help.quantower.com/quantower/quantower-algo/access-to-trading-portfolio)
- **Open Questions**:
  - Exact Quantower event sequence for partial fills? (Confirm via SDK docs/testing.)
  - Preferred deployment mechanism for Quantower add-ons (manual vs. package feed)?
  - Do we retain SQLite storage or migrate to a cross-platform persistent store?
