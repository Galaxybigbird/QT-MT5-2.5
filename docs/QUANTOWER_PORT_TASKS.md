# Quantower Port Task List

This document tracks the migration of the NinjaTrader add-on logic into the Quantower implementation.

## Legacy Components To Port

| Legacy File | Quantower Replacement | Status |
|-------------|-----------------------|--------|
| MultiStratManager.cs | Quantower.MultiStrat.Manager service | ✅ Done |
| UIForManager.cs | MultiStratPlugin UI panel | In Progress |
| TrailingAndElasticManager.cs | Quantower-aware trailing/elastic manager | ✅ Done |
| IndicatorCalculator.cs | Shared indicator helpers (Quantower) | ✅ Done |
| SLTPRemovalLogic.cs | Quantower order cleanup | ✅ Done |
| SQLiteHelper.cs | Persistence helper (JSON-based) | ✅ Done |
| SimpleJson.cs | Lightweight serialization helper | ✅ Done |

## Implementation Steps

1. Build Quantower-oriented service layer that replaces `MultiStratManager` (handles bridge lifecycle, event subscriptions, strategy config state).
2. Move legacy UI functionality into the new WPF plug-in panel (bindings, settings trays, account/symbol selectors).
3. Rework trailing/elastic logic to consume Quantower `Trade`/`Position` objects and submit updates via `QuantowerBridgeService`.
4. Port math/util helper classes with minimal dependencies (indicator calculations, JSON helpers, persistence).
5. Validate integration end-to-end inside Quantower (manual tests once Quantower SDK is available) and remove unused NinjaTrader-specific code paths.

This list will be updated as the port progresses.
