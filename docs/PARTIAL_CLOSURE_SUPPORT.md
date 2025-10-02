# Partial Closure Support Implementation

**Date:** 2025-10-02  
**Status:** ✅ Implemented  
**Feature:** Detect and handle partial position closures (closing N of M contracts)

---

## Problem Statement

### Original Issue
When a user has a 3-contract position (3 MT5 hedges) and closes 1 contract:
- **Expected:** Close 1 of 3 hedges on MT5 (2 QT contracts → 2 MT5 hedges)
- **Actual (before fix):** Opens a 4th hedge on MT5 (2 QT contracts → 4 MT5 hedges) ❌

### Root Cause
Quantower fires `TradeAdded` events for BOTH opening and closing trades:
- **Opening trade:** Buy 3 contracts → `TradeAdded` with `Side.Buy`, `Quantity=3`
- **Partial close:** Close 1 contract → `TradeAdded` with `Side.Sell`, `Quantity=1` (opposite side!)

The system treated ALL trades as opening trades, so the opposite-side trade opened a NEW hedge instead of closing an existing one.

---

## Solution Implemented

### Architecture: Trade-Based Partial Closure Detection

**Key Insight:** We can detect closing trades by comparing the trade side with the tracked position side.

**Components:**
1. **Position State Tracking** - Track current quantity and side for each position
2. **Closing Trade Detection** - Identify opposite-side trades as closures
3. **Partial Close Requests** - Send close messages with specific quantities

### Implementation Details

#### 1. Enhanced Position Tracking

**File:** `MultiStratManagerService.cs`

Added three new tracking dictionaries:
```csharp
// Track initial position quantities (for full closures)
private readonly ConcurrentDictionary<string, int> _baseIdToInitialQuantity;

// Track CURRENT position quantities (for partial closures)
private readonly ConcurrentDictionary<string, int> _baseIdToCurrentQuantity;

// Track position side (Buy/Sell) to detect closing trades
private readonly ConcurrentDictionary<string, Side> _baseIdToSide;
```

**When position opens:**
```csharp
_baseIdToInitialQuantity[baseId] = newQuantity;  // e.g., 3
_baseIdToCurrentQuantity[baseId] = newQuantity;  // e.g., 3
_baseIdToSide[baseId] = position.Side;           // e.g., Side.Buy
```

#### 2. Closing Trade Detection

**Method:** `IsClosingTrade(Trade trade, string baseId)`

**Logic:**
```csharp
// 1. Get tracked position side
if (!_baseIdToSide.TryGetValue(baseId, out var positionSide))
    return false;  // Not tracking → treat as opening trade

// 2. Check if trade side is OPPOSITE to position side
bool isOpposite = (positionSide == Side.Buy && trade.Side == Side.Sell) ||
                  (positionSide == Side.Sell && trade.Side == Side.Buy);

if (!isOpposite)
    return false;  // Same side → adding to position

// 3. Opposite side → this is a closing trade
return true;
```

**Example:**
- Position: Long 3 contracts (`Side.Buy`)
- Trade: Sell 1 contract (`Side.Sell`)
- Result: `isOpposite = true` → Closing trade detected ✅

#### 3. Partial Closure Handling

**Method:** `HandlePartialClosure(Trade trade, string baseId)`

**Steps:**
1. Get current quantity from tracking
2. Calculate new quantity: `newQty = currentQty - tradeQty`
3. Update tracked quantity
4. Send partial close request to bridge

**Example:**
```
Current: 3 contracts
Trade: Close 1 contract
New: 3 - 1 = 2 contracts
Action: Send close request for 1 hedge
```

#### 4. Modified Trade Handler

**Method:** `HandleTrade(Trade trade)`

**Flow:**
```csharp
// BEFORE (old code):
// All trades → Send to bridge → Open hedges

// AFTER (new code):
if (IsClosingTrade(trade, baseId))
{
    HandlePartialClosure(trade, baseId);  // Close hedges
    return;  // Don't send as opening trade
}
// Otherwise, send as opening trade (existing logic)
```

---

## How It Works

### Scenario 1: Open 3 Contracts

```
1. User opens 3 contracts
2. Quantower fires: TradeAdded(Side.Buy, Qty=3)
3. IsClosingTrade? NO (not tracking yet)
4. Action: Send to bridge → Split into 3 hedges
5. Track: currentQty=3, side=Buy
```

### Scenario 2: Close 1 of 3 Contracts

```
1. User closes 1 contract
2. Quantower fires: TradeAdded(Side.Sell, Qty=1)  ← Opposite side!
3. IsClosingTrade? YES (tracked side=Buy, trade side=Sell)
4. HandlePartialClosure:
   - Current: 3
   - Closing: 1
   - New: 2
5. Action: Send partial close request (close 1 hedge)
6. Update: currentQty=2
7. Result: 2 QT contracts, 2 MT5 hedges ✅
```

### Scenario 3: Close Remaining 2 Contracts

```
1. User closes 2 contracts
2. Quantower fires: TradeAdded(Side.Sell, Qty=2)
3. IsClosingTrade? YES
4. HandlePartialClosure:
   - Current: 2
   - Closing: 2
   - New: 0
5. Action: Send partial close request (close 2 hedges)
6. Quantower fires: PositionRemoved (full close)
7. Cleanup: Remove all tracking
```

---

## Benefits

✅ **Accurate Hedge Count:** N QT contracts = N MT5 hedges (always)  
✅ **Partial Closures:** Close 1 of 3 contracts → Close 1 of 3 hedges  
✅ **Full Closures:** Still handled by `PositionRemoved` event  
✅ **No Extra Hedges:** Opposite-side trades no longer open new hedges  
✅ **Backward Compatible:** Existing full-closure logic unchanged

---

## Testing Scenarios

### Test Case 1: Partial Close (3 → 2 → 1 → 0)
1. Open 3 contracts
2. **Expected:** 3 hedges created
3. Close 1 contract
4. **Expected:** 1 hedge closed, 2 remain
5. Close 1 contract
6. **Expected:** 1 hedge closed, 1 remains
7. Close 1 contract
8. **Expected:** 1 hedge closed, 0 remain

### Test Case 2: Partial Close Then Add
1. Open 3 contracts → 3 hedges
2. Close 2 contracts → 2 hedges closed, 1 remains
3. Open 2 contracts → 2 hedges added, 3 total
4. **Expected:** 3 QT contracts, 3 MT5 hedges

### Test Case 3: Full Close (No Partial)
1. Open 3 contracts → 3 hedges
2. Close all 3 contracts at once
3. **Expected:** `PositionRemoved` fires, all 3 hedges closed

---

## Technical Notes

### Why Not Use `Position.Updated`?
Quantower's `Core` class does NOT expose a `PositionUpdated` event. We must rely on `TradeAdded` events to detect quantity changes.

### Trade vs Position Events
- **`TradeAdded`**: Fires for EVERY trade (opening AND closing)
- **`PositionAdded`**: Fires when position is first created
- **`PositionRemoved`**: Fires ONLY when position is FULLY closed (quantity reaches 0)

### Quantity Tracking
- **`_baseIdToInitialQuantity`**: Stores quantity when position opens (used for full closures)
- **`_baseIdToCurrentQuantity`**: Updated on every partial close (used for partial closures)

### Cleanup
All tracking dictionaries are cleaned up in three places:
1. `HandlePositionRemoved` - When position fully closes
2. `StopTracking` - When manually stopping tracking
3. Position ID reuse detection - When same ID used for new position

---

## Related Files

### Modified Files
- `MultiStratManagerRepo/Quantower/QuantowerMultiStratAddOn/MultiStratManagerService.cs`
  - Added `_baseIdToCurrentQuantity` and `_baseIdToSide` dictionaries
  - Modified `HandleTrade` to detect closing trades
  - Added `IsClosingTrade` method
  - Added `HandlePartialClosure` method
  - Added `SendPartialCloseRequest` method
  - Updated cleanup in `HandlePositionRemoved` and `StopTracking`

### Unchanged Files
- `QuantowerTradeMapper.cs` - No changes needed (already supports `closedContractCount`)
- `BridgeApp/app.go` - Already supports closing N hedges
- `BridgeApp/internal/grpc/server.go` - Already supports trade splitting

---

## Limitations

### Quantower API Constraints
1. **No `PositionUpdated` Event**: Cannot directly observe quantity changes
2. **`PositionRemoved` Only on Full Close**: Partial closes don't fire this event
3. **Trade Events for Everything**: Must distinguish opening vs closing trades

### Current Implementation
1. **Trade-Based Detection Only**: Relies on `TradeAdded` events
2. **No Direct Position Polling**: Doesn't query position quantity directly
3. **Assumes Sequential Trades**: Doesn't handle simultaneous opposite-side trades

---

## Future Enhancements

### Potential Improvements
1. **Position Polling**: Periodically check actual position quantity
2. **Trade Correlation**: Match closing trades to specific opening trades
3. **Partial Close from MT5**: Handle user closing hedges directly on MT5
4. **Multi-Account Support**: Track quantities per account

### Not Implemented (Out of Scope)
- **Closing from MT5 side**: User manually closes 1 of 3 MT5 hedges
  - Would require MT5 → QT synchronization
  - Complex: Which QT contract to close?
- **Scale-in scenarios**: Adding to position after partial close
  - Currently supported but not explicitly tested

---

## Commit Message

```
feat(quantower): Implement partial closure support for n:n hedge mapping

Quantower fires TradeAdded events for both opening and closing trades.
Closing trades have opposite side (e.g., Sell for Long position).
Previous code treated all trades as opens, creating extra hedges.

Changes:
- Track current position quantity and side
- Detect closing trades by comparing trade side with position side
- Send partial close requests with specific quantities
- Update current quantity on each partial close
- Maintain n:n relationship: N QT contracts = N MT5 hedges

Example:
- Open 3 contracts → 3 hedges
- Close 1 contract → Close 1 hedge (2 remain)
- Close 2 contracts → Close 2 hedges (0 remain)

Fixes: Partial closures creating extra hedges instead of closing existing ones
Implements: Full n:n hedge mapping with partial closure support
```

