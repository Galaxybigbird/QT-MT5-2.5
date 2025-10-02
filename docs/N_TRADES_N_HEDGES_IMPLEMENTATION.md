# Implementation: n Quantower Trades → n MT5 Hedges

**Date:** 2025-01-XX  
**Status:** ✅ Implemented  
**Goal:** Restore 1:1 relationship between Quantower trades and MT5 hedges

---

## Problem Statement

The system was enforcing a **"1 Quantower position = 1 MT5 hedge"** policy. When opening 3 trades that rolled up into a single Quantower position, only **one** hedge was created on MT5 with quantity=3, instead of three separate hedges with quantity=1 each.

### Root Cause

1. **Trade splitting disabled** in `BridgeApp/internal/grpc/server.go` (line 223-239)
   - Multi-contract trades were sent as-is without splitting
   - Comment explicitly stated: "Do NOT split multi-quantity trades"

2. **Closure logic hardcoded** in `BridgeApp/app.go` (line 1233-1235)
   - Always closed exactly 1 ticket regardless of `closed_hedge_quantity`
   - Comment: "Always close exactly 1 ticket per close request"

3. **Closure quantity incorrect** in `QuantowerTradeMapper.cs` (line 261-262)
   - Used total position quantity instead of number of contracts being closed

---

## Solution Overview

Implemented a 5-phase solution to restore n:n relationship while maintaining stable `base_id` (Position.Id) for correlation:

### Phase 1: Re-enable Trade Splitting (Go Bridge)
**File:** `BridgeApp/internal/grpc/server.go`

- Restored splitting logic in `enqueueTradeWithSplit` function
- When `quantity > 1`, split into individual trades:
  - Each split trade: `quantity=1`, same `base_id`, unique `id`
  - Set `total_quantity` and `contract_num` for tracking
  - Example: Trade with qty=3 → 3 trades with IDs `trade-1`, `trade-2`, `trade-3`

```go
// Multi-contract trade - split into individual hedges
for i := 1; i <= quantity; i++ {
    split := *base
    split.ID = fmt.Sprintf("%s-%d", base.ID, i)
    split.Quantity = 1.0
    split.TotalQuantity = quantity
    split.ContractNum = i
    // Enqueue each split trade...
}
```

### Phase 2: Fix Closure Logic (Go Bridge)
**File:** `BridgeApp/app.go`

- Removed hardcoded `qty := 1`
- Read `closed_hedge_quantity` from request
- Close exactly that many tickets from the pool

```go
// Read closed_hedge_quantity from request
closedQty := getQuantityFromRequest(request)
qty := int(closedQty)
if qty < 1 {
    qty = 1 // Safety fallback
}
```

### Phase 3: Track Position Quantities (C# Addon)
**File:** `MultiStratManagerRepo/Quantower/QuantowerMultiStratAddOn/MultiStratManagerService.cs`

- Added `_baseIdToInitialQuantity` dictionary to track initial position quantities
- Store quantity when position is added (line 1283-1285)
- Clean up when position is removed or tracking stops

```csharp
// Track initial position quantity for proper hedge closure
var initialQuantity = (int)Math.Abs(position.Quantity);
_baseIdToInitialQuantity[baseId] = initialQuantity;
```

### Phase 4: Update Closure Mapper (C# Infrastructure)
**File:** `MultiStratManagerRepo/Quantower/Infrastructure/QuantowerTradeMapper.cs`

- Modified `TryBuildPositionClosure` signature to accept `int? closedContractCount`
- Use provided count for `closed_hedge_quantity` instead of position quantity
- Fallback to position quantity for backward compatibility

```csharp
public static bool TryBuildPositionClosure(
    Position position, 
    string? knownBaseId, 
    int? closedContractCount,  // NEW PARAMETER
    out string json, 
    out string? positionId)
{
    // Use closedContractCount if provided
    double closedQuantity;
    if (closedContractCount.HasValue && closedContractCount.Value > 0)
    {
        closedQuantity = closedContractCount.Value;
    }
    else
    {
        closedQuantity = Math.Abs(position.Quantity);
    }
    payload["closed_hedge_quantity"] = closedQuantity;
}
```

### Phase 5: Wire Up Closure Quantity (C# Bridge Service)
**File:** `MultiStratManagerRepo/Quantower/QuantowerMultiStratAddOn/QuantowerBridgeService.cs`

- Added `GetTrackedQuantity` callback property
- Retrieve tracked quantity in `OnQuantowerPositionClosed`
- Pass to `TryBuildPositionClosure`

```csharp
// Get tracked quantity
int? closedContractCount = null;
if (!string.IsNullOrWhiteSpace(positionId) && GetTrackedQuantity != null)
{
    closedContractCount = GetTrackedQuantity(positionId);
}

// Pass to mapper
QuantowerTradeMapper.TryBuildPositionClosure(
    position, positionId, closedContractCount, out var payload, out var closureId);
```

**File:** `MultiStratManagerRepo/Quantower/QuantowerMultiStratAddOn/MultiStratManagerService.cs`

- Wired up callback in constructor
- Added public `GetTrackedInitialQuantity` method

```csharp
// Wire up callback
_bridgeService.GetTrackedQuantity = GetTrackedInitialQuantity;

// Public method to retrieve tracked quantity
public int? GetTrackedInitialQuantity(string baseId)
{
    if (_baseIdToInitialQuantity.TryGetValue(baseId, out var quantity))
    {
        return quantity;
    }
    return null;
}
```

---

## Key Benefits

✅ **Stable `base_id`**: Continue using `Position.Id` for correlation  
✅ **n:n relationship**: 3 QT trades → 3 MT5 hedges  
✅ **Proper closures**: Close the correct number of hedges  
✅ **Backward compatible**: Falls back to current behavior if quantity tracking fails  
✅ **Clean architecture**: Minimal changes, leverages existing infrastructure

---

## Testing Scenarios

### Scenario 1: Single Contract Trade
- **Input:** Open 1 contract on Quantower
- **Expected:** 1 MT5 hedge created
- **Closure:** Close position → 1 MT5 hedge closed

### Scenario 2: Multi-Contract Trade
- **Input:** Open 3 contracts on Quantower (single position)
- **Expected:** 3 MT5 hedges created (same `base_id`, different IDs)
- **Closure:** Close position → 3 MT5 hedges closed

### Scenario 3: Multiple Separate Positions
- **Input:** Open 2 separate 1-contract positions
- **Expected:** 2 MT5 hedges with different `base_id`s
- **Closure:** Close each position → corresponding hedge closed

---

## Important Notes

### Partial Closures
Quantower only fires `PositionRemoved` when the ENTIRE position is closed. For partial closures (closing 2 out of 3 contracts), we would need to:
- Subscribe to position update events
- Track closing trades separately
- Calculate quantity deltas

**Current Implementation:** Only handles full position closures. Partial closures are an optional future enhancement.

### Correlation Keys
- **`base_id`**: Always `Position.Id` (stable across lifecycle)
- **`id`**: Unique per split trade (`trade-1`, `trade-2`, etc.)
- **`qt_trade_id`**: Original Quantower trade ID
- **`qt_position_id`**: Quantower position ID (audit trail)

### Cleanup
All tracking dictionaries are properly cleaned up:
- When position is removed (`HandlePositionRemoved`)
- When tracking stops (`StopTracking`)
- Prevents memory leaks

---

## Files Modified

### Go (Bridge)
1. `BridgeApp/internal/grpc/server.go` - Re-enabled splitting
2. `BridgeApp/app.go` - Fixed closure logic

### C# (Quantower Addon)
1. `MultiStratManagerRepo/Quantower/QuantowerMultiStratAddOn/MultiStratManagerService.cs` - Quantity tracking
2. `MultiStratManagerRepo/Quantower/QuantowerMultiStratAddOn/QuantowerBridgeService.cs` - Callback wiring
3. `MultiStratManagerRepo/Quantower/Infrastructure/QuantowerTradeMapper.cs` - Closure mapper update

---

## Next Steps

1. **Build and Test:**
   ```bash
   # Build Go bridge
   cd BridgeApp
   go build
   
   # Build C# addon
   cd MultiStratManagerRepo/Quantower
   dotnet build
   ```

2. **Test Scenarios:**
   - Single contract trades
   - Multi-contract trades (2, 3, 5 contracts)
   - Multiple separate positions
   - Full position closures

3. **Monitor Logs:**
   - Look for "Splitting trade" messages in bridge logs
   - Verify "Stored initial quantity" in addon logs
   - Confirm "closing N hedge(s)" in closure logs

4. **Optional Enhancements:**
   - Implement partial closure support
   - Add metrics/telemetry for split trades
   - Create automated integration tests

---

## Rollback Plan

If issues arise, revert these commits in reverse order:
1. Phase 5: Remove callback wiring
2. Phase 4: Revert mapper signature change
3. Phase 3: Remove quantity tracking
4. Phase 2: Restore hardcoded `qty := 1`
5. Phase 1: Disable splitting again

Each phase is independent and can be rolled back separately.

