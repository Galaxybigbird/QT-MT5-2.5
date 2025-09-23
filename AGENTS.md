# Repository Guidelines

## Project Structure & Module Organization
`BridgeApp/` hosts the Go-based bridge and Wails UI; `main.go` boots the gRPC server while `frontend/` contains the GUI. Protocol assets live under `BridgeApp/proto/` and generated code is written to `BridgeApp/internal/`. The NinjaTrader addon sources sit in `MultiStratManagerRepo/` with `MultiStratManager.csproj` targeting .NET Framework; gRPC stubs populate `External/Proto/`. MT5 expert adviser code is under `MT5/` (`ACHedgeMaster.mq5` plus generated proto glue). Reference material and operational notes are in `docs/`, while automation helpers live in `scripts/` (PowerShell wrappers for deployment and Wails dev logging).

## Build, Test, and Development Commands
From `BridgeApp/`, run `go test ./...` for unit tests and `wails build` (or `make build`) to compile the desktop bridge. Use `make proto` after editing `proto/trading.proto` to regenerate Go, C#, and C++ bindings. The NinjaTrader addon builds through the solution `OfficialFuturesHedgebotv2.5QT.sln` in Visual Studio (Release x64 preferred). Compile the MT5 EA inside MetaEditor (`F7`) and deploy the resulting `.ex5` to your MT5 terminal. PowerShell scripts in `scripts/` streamline copying NinjaTrader binaries and launching Wails in dev mode.

## Coding Style & Naming Conventions
Go code must stay `gofmt`-clean; keep package-level symbols in `camelCase` and exported APIs in `PascalCase`. Favor dependency injection over globals, and co-locate bridge handlers in `internal/grpc`. For C#, follow NinjaTrader conventions: four-space indentation, `PascalCase` for types, and `camelCase` for locals/fields prefixed with `_` only when private state persists across callbacks. MQL5 logic mirrors the same naming, with hedge states grouped in structs to match proto contract versions. Document configuration toggles with succinct XML comments.

## Testing Guidelines
Aim to cover Go message flows with table-driven tests under `BridgeApp/*.go`; add fixtures in `bridge-test/` when reproducing regression traffic. After proto changes, rerun serializer tests (`go test ./internal/grpc/...`). NinjaTrader and MT5 components rely on scenario testing: validate fills via the NT Playback Connection and confirm hedge mirroring on MT5 using demo accounts. Capture logs from `logs/unified-*.jsonl` when filing issues.

## Commit & Pull Request Guidelines
Match the existing history (`type(scope): summary`), e.g., `fix(trailing/elastic): sync DEMA and ATR updates`. Keep subject lines under 80 characters and explain behavioral impact in the body bullets. Pull requests should link the relevant issue, describe NT/MT5 scenarios exercised, and attach updated screenshots for UI tweaks (`Screenshots/`). When proto or config schemas move, call it out in the PR checklist and remind reviewers to regenerate stubs.
