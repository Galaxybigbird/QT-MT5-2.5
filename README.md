# NT-MT5 gRPC

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![Build Status](https://img.shields.io/badge/build-passing-brightgreen.svg)]()
[![Version](https://img.shields.io/badge/version-2.0.0-blue.svg)]()

## 🚀 Overview

> **Note**: This project is actively under development with new fixes and features being added regularly.

This project is a 3-part system designed to synchronize trading actions between NinjaTrader (NT) and MetaTrader 5 (MT5), primarily for hedging and copy trading purposes. The system consists of a NinjaTrader Addon, an MT5 Expert Advisor (EA), and a Go-based Bridge application. When trades occur on NT, corresponding hedge positions are managed on MT5. Conversely, when MT5 hedge positions are closed, the original NT trades are signaled for closure.

## Core Features

*   **Bidirectional/Unidirectional Trade Synchronization:**
    *   New NT trades (entries/fills) trigger hedge (or parallel) position opening/adjustment on MT5.
    *   Closure of MT5 hedge/parallel positions triggers closure of corresponding original NT trades.
*   **Flexible Stop Loss / Take Profit (SL/TP) Management:**
    *   Option for NinjaTrader to remove its native SL/TP orders for new entries if `EnableSLTPRemoval` is active in the NT Addon.
    *   MT5 EA can manage SL/TP for its hedge/parallel positions, including ATR-based trailing stops.
*   **Centralized Communication Bridge:** A Go-based application facilitates message passing between NT and MT5.
*   **Unified Structured Logging:** All NT/MT5/Bridge logs flow into a JSONL file with daily rotation (BRIDGE_LOG_DIR) plus optional Sentry forwarding (ERROR+ by default).
*   **Extensible Observability:** Support for correlation & schema versioning in log events (`schema_version`, `correlation_id`).
*   **UI for NinjaTrader Addon:** Provides an interface for managing strategies and settings within NinjaTrader.

## System Architecture & Components

The system is composed of three main parts:

### 1. NinjaTrader Addon
*   **Role:** Manages strategies on NinjaTrader, sends new trade data to the bridge, receives closure signals from the bridge, and handles NT-side SL/TP removal.
*   **Key Files:**
    *   [`MultiStratManager_Source_Code/MultiStratManager.cs`](MultiStratManager_Source_Code/MultiStratManager.cs)
    *   [`MultiStratManager_Source_Code/SLTPRemovalLogic.cs`](MultiStratManager_Source_Code/SLTPRemovalLogic.cs)
    *   [`MultiStratManager_Source_Code/UIForManager.cs`](MultiStratManager_Source_Code/UIForManager.cs)

### 2. MT5 Expert Advisor (EA)
*   **Role:** Receives trade data from the bridge, opens/adjusts/manages MT5 hedge positions (including trailing stops), and sends closure notifications for its hedges back to the bridge.
*   **Key File:** [`ACHedgeMaster.mq5`](ACHedgeMaster.mq5)

### 3. Bridge Application
*   **Role:** A Go-based application acting as the central communication hub, relaying messages between the NT Addon and the MT5 EA. It uses Wails for a potential frontend.
*   **Key Files:**
    *   [`BridgeApp/main.go`](BridgeApp/main.go)
    *   (Frontend: [`BridgeApp/frontend/`](BridgeApp/frontend/))

## Setup & Installation (High-Level Guide)

### Prerequisites
*   NinjaTrader 8
*   MetaTrader 5
*   Go environment (for the bridge application)

### NinjaTrader Addon
1.  Compile the C# project files (e.g., [`MultiStratManager.cs`](MultiStratManager_Source_Code/MultiStratManager.cs), [`SLTPRemovalLogic.cs`](MultiStratManager_Source_Code/SLTPRemovalLogic.cs), [`UIForManager.cs`](MultiStratManager_Source_Code/UIForManager.cs)) and install the resulting assembly as a NinjaTrader Addon.
2.  Within NinjaTrader, enable the Addon.
3.  Use the Addon's UI ([`UIForManager`](MultiStratManager_Source_Code/UIForManager.cs)) to configure:
    *   Account(s) to monitor.
    *   Bridge application URL (e.g., `http://127.0.0.1:5000`).
    *   The `EnableSLTPRemoval` setting per strategy or globally.

### MT5 Expert Advisor (EA)
1.  Open [`ACHedgeMaster.mq5`](ACHedgeMaster.mq5) in MetaEditor (part of MT5).
2.  Compile the EA.
3.  In MT5, attach the compiled `ACHedgeMaster` EA to the chart(s) corresponding to the instruments you intend to hedge.
4.  Configure EA input parameters:
    *   Bridge application URL.
    *   Symbol mapping (if NT and MT5 symbols differ).
    *   Hedging logic parameters (e.g., hedge ratio).
    *   Trailing stop parameters (ATR period, multiplier, profit trigger for activation).

### Bridge Application
1.  Navigate to the [`BridgeApp`](BridgeApp/) directory.
2.  Build the Go application (e.g., `go build main.go`).
3.  Run the compiled executable (e.g., `./main` or `main.exe`) or just type the command 'wails dev' within the (BridgeApp/) directory from the terminal.
4.  Ensure the bridge is listening on the configured port (e.g., 5000).

### Quantower Plugin (in progress)
1. Navigate to [`MultiStratManagerRepo/Quantower/QuantowerMultiStratAddOn`](MultiStratManagerRepo/Quantower/QuantowerMultiStratAddOn/).
2. Build/publish the plug-in (e.g. `dotnet publish -c Release -o ./publish --no-restore /p:EnableWindowsTargeting=true`).
3. Copy the output into Quantower's plug-in directory, typically `%UserProfile%\Documents\Quantower\Settings\Scripts\plugins\MultiStratQuantower` (use [`scripts/deploy-quantower-plugin.ps1`](scripts/deploy-quantower-plugin.ps1) to automate this step).
4. Restart Quantower and launch the "Multi-Strat Bridge" panel from the Sidebar to configure the gRPC endpoint and monitor bridge logs.

### Network Configuration
*   Verify that the NT Addon, MT5 EA, and Bridge application can communicate over the network (typically all on `localhost` using the configured port and/or the UI). Firewall exceptions might be needed.

## Workflow / How It Works

### A. NinjaTrader Trade to MT5 Hedge
1.  An NT strategy places an entry order, which gets filled.
2.  The NT Addon ([`MultiStratManager`](MultiStratManager_Source_Code/MultiStratManager.cs)) detects this fill via `OnExecutionUpdate`.
3.  If `EnableSLTPRemoval` is true for the strategy, [`SLTPRemovalLogic`](MultiStratManager_Source_Code/SLTPRemovalLogic.cs) cancels any associated SL/TP orders on NT.
4.  The NT Addon formats a JSON message with trade details (action, quantity, price, instrument, unique `base_id` from the NT order, etc.).
5.  This message is sent to the Bridge application's appropriate endpoint (e.g., `/trade`).
6.  The Bridge receives the message and forwards it to the MT5 EA (via a socket connection or HTTP request, as per its design).
7.  The MT5 EA ([`ACHedgeMaster.mq5`](ACHedgeMaster.mq5)) receives the message in its `OnBridgeMessage` (or equivalent) handler.
8.  The EA updates its internal state (e.g., `globalFutures` representing the net position on NT).
9.  Based on the new `globalFutures` and hedging logic, the EA calculates the required MT5 hedge position adjustment.
10. The EA opens new hedge orders or modifies existing ones on MT5 to match the target hedge.

### B. MT5 Hedge Closure to NT Original Trade Closure
1.  An MT5 hedge position is closed (e.g., by its SL/TP, trailing stop, or manual intervention).
2.  The MT5 EA detects this closure (e.g., in `OnTradeTransaction` or `OnTimer`).
3.  The EA identifies the `base_id` of the original NT trade associated with the closed MT5 hedge (from its internal tracking arrays like `g_open_mt5_base_ids[]`).
4.  The EA constructs a JSON message indicating a closure request for this `base_id`.
5.  This message is sent to the Bridge application.
6.  The Bridge relays this closure message to the NinjaTrader Addon.
7.  The NT Addon receives the message, finds the original NT entry order/position associated with the `base_id`.
8.  The NT Addon then programmatically closes this original NT position.

## Key Configuration Points to Highlight

*   **NT Addon UI:** `EnableSLTPRemoval` checkbox, Bridge URL.
*   **MT5 EA Inputs:** Bridge URL, Symbol Mapping, Hedging Ratio, Trailing Stop Activation Profit (%), ATR Period, ATR Multiplier.
*   **Bridge:** Ensure it's running and accessible by both NT and MT5 on the configured host/port.

## Troubleshooting Notes/Small Bugs To Keep In Mind (Optional)

*   For debugging, refer to the Experts tab in MT5 for the EA, the terminal logs for the bridge, and the Ninjascript Output tab/logs for the NT Multi-Strategy Manager Addon

*   Bug: The UI component for hedgebot that switches the status to (active) takes about 15ish seconds to update to active if you loaded the ea first before starting up the bridge. 

*   Bug: Reset bridge state is currently broken, a fix is coming soon with explanation of the feature

## 🔍 Unified Logging & Observability

The Bridge acts as a central collector for log events from all components (NinjaTrader Addon, MT5 EA, Bridge internals) via gRPC `LoggingService.Log`.

### JSONL Log Files
* Directory resolution order:
    1. `BRIDGE_LOG_DIR` (if set)
    2. `logs` folder next to the Bridge executable
    3. `./logs` (CWD fallback)
* Rotated daily: `unified-YYYYMMDD.jsonl`
* Buffered writer (64KB) with periodic flush (250ms) and graceful flush on shutdown.

### Log Event Schema (selected fields)
| Field | Description |
|-------|-------------|
| timestamp_ns | Event time (Unix ns). Filled at ingest if 0. |
| source | bridge | nt | mt5 |
| level | INFO | WARN | ERROR (others passthrough) |
| component | Logical area (queue, stream, trade_exec, etc.) |
| message | Human-readable description |
| base_id / trade_id | Trade correlation identifiers |
| nt_order_id / mt5_ticket | Platform-specific identifiers |
| queue_size / net_position / hedge_size | Snapshot (auto-added for WARN/ERROR internal events) |
| error_code | Optional domain error code |
| stack | Optional stack / trace text |
| tags | Simple string map for ad-hoc labels |
| extra | Arbitrary structured enrichment |
| schema_version | Added 2025-08 (currently "1.0") |
| correlation_id | End-to-end trace / grouping ID (proto extended 2025-08) |

### Sentry Integration (Phase 1)
Sentry forwarding is Bridge-only (clients still send plain LogEvents). Qualifying events (>= threshold) are forwarded asynchronously after local file write.

#### Environment Variables
| Variable | Purpose | Default |
|----------|---------|---------|
| SENTRY_DSN | Enables Sentry when non-empty | (disabled) |
| SENTRY_ENV | Deployment environment (prod/staging/dev) | unset |
| SENTRY_RELEASE | Release identifier (e.g. bridge@2.0.0) | unset |
| SENTRY_MIN_EVENT_LEVEL | Promotion threshold (INFO/WARN/ERROR) | WARN (configured) |
| SENTRY_DEBUG | Verbose SDK logging (true/false) | false |

#### Behavior
* Non-blocking: File write never waits for network.
* Tags: `source, component, base_id, trade_id, nt_order_id, mt5_ticket, error_code, schema_version, correlation_id` plus any user `tags`.
* Contexts: queue/state snapshots, `extra` map, `stack` (if provided).
* Flush: On graceful shutdown (GUI & headless) a 2s Sentry flush window is attempted.

### Correlation IDs
`correlation_id` added to the protocol for future cross-system tracing. Until regenerated protobuf stubs are deployed:
* Proto file already contains the field (index 17).
* Go server currently ignores it pending `protoc` regeneration (see below).
* After regeneration, server will map it directly into the unified log pipeline & Sentry tags.

### Regenerating Protobuf After Schema Change
Install `protoc` (if not already):
1. Download latest release zip for Windows from: https://github.com/protocolbuffers/protobuf/releases
2. Extract `protoc.exe` into a folder, e.g. `C:\tools\protoc\`.
3. Add that folder to your PATH (System Environment Variables > Path).

Then regenerate (from `BridgeApp/`):
```powershell
go install google.golang.org/protobuf/cmd/protoc-gen-go@latest
go install google.golang.org/grpc/cmd/protoc-gen-go-grpc@latest
protoc --go_out=internal/grpc --go_opt=paths=source_relative `
             --go-grpc_out=internal/grpc --go-grpc_opt=paths=source_relative `
             proto/trading.proto
```

For C# (NinjaTrader Addon) and C++ (MT5 DLL), also run the corresponding generation steps (see `Makefile` targets or manually add `--csharp_out` / `--cpp_out`). Copy the regenerated C# files into the Addon’s `External/Proto` folder and rebuild inside NinjaTrader.

### Deployment Notes After Proto Update
1. Regenerate Go/C#/C++ stubs.
2. Rebuild Bridge (GUI & headless if used).
3. Deploy updated C# proto classes + recompile NT Addon.
4. Rebuild MT5 integration if/when it consumes new fields.
5. Confirm logs still flowing; then begin populating `correlation_id` from clients if desired.

---
Future Enhancements (Planned):
* Client-originated correlation propagation (NT Addon & MT5 EA).
* Optional sampling & rate limiting for Sentry.
* Redaction / PII filtering hook prior to external export.
