# Quantower Migration Status

| Phase | Scope | Owner | Status | Started | Target Completion | Notes |
|-------|-------|-------|--------|---------|--------------------|-------|
| 0 | Documentation, SDK discovery | TBD | ✅ Completed | 2025-09-21 | 2025-09-22 | PRD drafted (`docs/QUANTOWER_MIGRATION_PRD.md`). |
| 1 | Proto & shared gRPC client foundation | TBD | 🔄 In Progress | 2025-09-22 | 2025-09-24 | Proto extended with Quantower IDs; Go/C#/C++ stubs regenerated; net6 BridgeGrpcClient scaffold created. |
| 2 | Quantower add-on prototype + net6 gRPC client | TBD | 🔄 In Progress | 2025-09-24 | 2025-09-30 | Quantower add-on stub + streaming hook in place; pending real SDK wiring & UI. |
| 3 | Bridge refactor to Quantower identifiers | TBD | ⏳ Planned | 2025-09-26 | 2025-10-03 | Remove NT mapping logic, adopt TradeID-first flow. |
| 4 | MT5 EA synchronization updates | TBD | ⏳ Planned | 2025-09-28 | 2025-10-05 | Align EA with Quantower identifiers, rebuild wrapper. |
| 5 | Cleanup & rollout | TBD | ⏳ Planned | 2025-10-05 | 2025-10-10 | Retire NT assets, update docs, verify telemetry. |

_Last updated: 2025-09-22._
