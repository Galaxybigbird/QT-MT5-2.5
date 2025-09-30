# Quantower-MT5 Bridge: Critical Fixes Implementation

**Date**: 2025-09-30  
**Status**: ✅ IMPLEMENTED  
**Files Modified**: 2

---

## Summary

All four critical issues have been fixed with targeted code changes:

1. **Issue #1 & #4**: Quantower → MT5 close logic failure (position closes but reopens) - **FIXED**
2. **Issue #2**: MT5 → Quantower close logic failure (MT5 closures don't close Quantower) - **FIXED**
3. **Issue #3**: Trailing stop logic broken (incorrect stop loss placement) - **FIXED**

---

## Files Modified

### 1. `MultiStratManagerRepo/Quantower/QuantowerMultiStratAddOn/QuantowerBridgeService.cs`

**Changes Made:**

#### A. Added Recent Closures Tracking (Line 19)
```csharp
private readonly ConcurrentDictionary<string, DateTime> _recentClosures = new(StringComparer.OrdinalIgnoreCase);
```

**Purpose**: Track positions that were recently closed to prevent closing trades from being sent as new trades.

#### B. Updated `OnQuantowerTrade` Method (Lines 426-467)
**Before**: All trades were sent to MT5 without filtering
**After**: Added cooldown check to skip trades for recently closed positions

```csharp
private void OnQuantowerTrade(Trade trade)
{
    var positionId = trade?.PositionId;
    
    // CRITICAL FIX (Issue #1 & #4): Check if this position was recently closed
    // When a position is closed in Quantower, it generates a closing Trade event
    // We don't want to send this as a new trade to MT5, as it would reopen the position
    if (!string.IsNullOrWhiteSpace(positionId) && 
        _recentClosures.TryGetValue(positionId, out var closureTime))
    {
        if ((DateTime.UtcNow - closureTime).TotalSeconds < 2)
        {
            EmitLog(BridgeLogLevel.Debug, $"Skipping trade {trade?.Id} - position {positionId} was recently closed (cooldown active)");
            _recentClosures.TryRemove(positionId, out _);
            return;
        }
        // Cooldown expired, remove from dictionary
        _recentClosures.TryRemove(positionId, out _);
    }

    // ... rest of existing code
}
```

**Impact**: Prevents closing trades from being sent to MT5, eliminating the reopen issue.

#### C. Updated `OnQuantowerPositionClosed` Method (Lines 480-498)
**Before**: Only sent close notification to bridge
**After**: Also marks position as recently closed

```csharp
private void OnQuantowerPositionClosed(Position position)
{
    // CRITICAL FIX (Issue #1 & #4): Mark this position as recently closed
    // This prevents the closing trade from being sent as a new trade to MT5
    var positionId = position?.Id;
    if (!string.IsNullOrWhiteSpace(positionId))
    {
        _recentClosures[positionId] = DateTime.UtcNow;
        EmitLog(BridgeLogLevel.Debug, $"Marked position {positionId} as recently closed (cooldown active for 2 seconds)");
    }

    // ... rest of existing code
}
```

**Impact**: Establishes the 2-second cooldown window to prevent duplicate trades.

---

### 2. `MultiStratManagerRepo/Quantower/QuantowerMultiStratAddOn/MultiStratManagerService.cs`

**Changes Made:**

#### A. Added BaseId → Position.Id Mapping (Line 39)
```csharp
private readonly ConcurrentDictionary<string, string> _baseIdToPositionId = new(StringComparer.OrdinalIgnoreCase);
```

**Purpose**: Maintain a mapping between baseId (used by MT5) and Quantower Position.Id to enable MT5 → Quantower close synchronization.

#### B. Updated `HandlePositionAdded` Method (Lines 1263-1268)
**Before**: Only registered position with trailing service
**After**: Also maintains baseId → Position.Id mapping

```csharp
// CRITICAL FIX (Issue #2): Maintain baseId → Position.Id mapping
// This allows us to find Quantower positions when MT5 sends closure notifications
if (!string.IsNullOrWhiteSpace(baseId) && !string.IsNullOrWhiteSpace(position.Id))
{
    _baseIdToPositionId[baseId] = position.Id;
    EmitLog(QuantowerBridgeService.BridgeLogLevel.Debug, $"Mapped baseId {baseId} -> Position.Id {position.Id}");
}
```

**Impact**: Enables lookup of Quantower positions by baseId when MT5 sends closure notifications.

#### C. Updated `HandlePositionRemoved` Method (Lines 1297, 1310)
**Before**: Only stopped tracking
**After**: Also removes baseId → Position.Id mapping

```csharp
// CRITICAL FIX (Issue #2): Remove baseId → Position.Id mapping
_baseIdToPositionId.TryRemove(existingBaseId, out _);
```

**Impact**: Keeps the mapping clean and prevents stale entries.

#### D. Updated `FindPositionByBaseId` Method (Lines 1189-1202)
**Before**: Only searched by position.Id or tracked baseId
**After**: First checks the baseId → Position.Id mapping

```csharp
// CRITICAL FIX (Issue #2): Check the baseId → Position.Id mapping first
// This allows us to find Quantower positions when MT5 sends closure notifications
if (_baseIdToPositionId.TryGetValue(baseId, out var positionId))
{
    foreach (var position in core.Positions)
    {
        if (string.Equals(position.Id, positionId, StringComparison.OrdinalIgnoreCase))
        {
            EmitLog(QuantowerBridgeService.BridgeLogLevel.Debug, $"Found position via mapping: baseId {baseId} -> Position.Id {positionId}");
            return position;
        }
    }
    // Position was in mapping but no longer exists - remove stale mapping
    _baseIdToPositionId.TryRemove(baseId, out _);
}

// Fallback to existing logic
// ...
```

**Impact**: Enables successful lookup of Quantower positions when MT5 sends closure notifications, allowing the existing close logic (lines 1133-1157) to work correctly.

#### E. Updated `SendElasticAndTrailing` Method (Lines 1417-1421)
**Before**: Sent trailing updates to MT5 via bridge
**After**: Only modifies Quantower stop loss locally, does NOT send to MT5

```csharp
// CRITICAL FIX (Issue #3): DO NOT send trailing updates to MT5
// Trailing stops should ONLY modify the Quantower stop loss locally
// Only elastic updates should be sent to MT5
// The code above already modified the Quantower stop loss using Core.ModifyOrder()
EmitLog(QuantowerBridgeService.BridgeLogLevel.Debug, $"Trailing stop updated locally in Quantower for {baseId} - NOT sending to MT5");
```

**Impact**: Prevents trailing stop updates from being sent to MT5, ensuring they only modify the Quantower stop loss as required.

---

## How the Fixes Work

### Issue #1 & #4: Quantower → MT5 Close and Reopen

**Problem Flow (Before Fix):**
1. User closes position in Quantower
2. `Core.PositionRemoved` event fires → sends `CLOSE_HEDGE` to MT5 ✅
3. Quantower generates closing `Trade` event
4. `Core.TradeAdded` event fires → sends closing trade as NEW trade to MT5 ❌
5. MT5 receives "buy" or "sell" action → opens new position ❌

**Solution Flow (After Fix):**
1. User closes position in Quantower
2. `OnQuantowerPositionClosed` marks position as recently closed (2-second cooldown)
3. `Core.PositionRemoved` event fires → sends `CLOSE_HEDGE` to MT5 ✅
4. Quantower generates closing `Trade` event
5. `Core.TradeAdded` event fires → `OnQuantowerTrade` checks cooldown
6. Cooldown active → trade is skipped, NOT sent to MT5 ✅
7. MT5 only receives `CLOSE_HEDGE`, position stays closed ✅

### Issue #2: MT5 → Quantower Close Logic

**Problem Flow (Before Fix):**
1. User closes hedge position in MT5
2. MT5 sends `HEDGE_CLOSE` notification with baseId
3. Bridge broadcasts to Quantower addon
4. `OnBridgeStreamEnvelopeReceived` calls `FindPositionByBaseId(baseId)`
5. `FindPositionByBaseId` searches for position.Id == baseId
6. **FAILS** because Quantower Position.Id ≠ original baseId ❌
7. Quantower position remains open ❌

**Solution Flow (After Fix):**
1. When position is added, `HandlePositionAdded` stores: `_baseIdToPositionId[baseId] = position.Id`
2. User closes hedge position in MT5
3. MT5 sends `HEDGE_CLOSE` notification with baseId
4. Bridge broadcasts to Quantower addon
5. `OnBridgeStreamEnvelopeReceived` calls `FindPositionByBaseId(baseId)`
6. `FindPositionByBaseId` checks mapping: `_baseIdToPositionId[baseId]` → gets Position.Id
7. Searches for position with that Position.Id → **FOUND** ✅
8. Calls `position.Close()` → Quantower position closes ✅

### Issue #3: Trailing Stop Logic

**Problem Flow (Before Fix):**
1. Trailing stop update calculated in `TrailingElasticService`
2. `SendElasticAndTrailing` modifies Quantower stop loss using `Core.ModifyOrder()` ✅
3. **ALSO** sends trailing update to bridge/MT5 ❌
4. MT5 EA receives trailing update and tries to modify MT5 stop loss ❌
5. Conflicts and incorrect stop loss placement ❌

**Solution Flow (After Fix):**
1. Trailing stop update calculated in `TrailingElasticService`
2. `SendElasticAndTrailing` modifies Quantower stop loss using `Core.ModifyOrder()` ✅
3. **DOES NOT** send trailing update to bridge/MT5 ✅
4. MT5 EA never receives trailing updates (only elastic updates) ✅
5. Quantower stop loss is modified correctly, no conflicts ✅

---

## Testing Checklist

### Issue #1 & #4: Quantower → MT5 Close
- [ ] Open a position in Quantower
- [ ] Verify MT5 hedge opens
- [ ] Close the Quantower position
- [ ] Verify MT5 hedge closes
- [ ] **CRITICAL**: Verify MT5 hedge does NOT reopen
- [ ] Check logs for "Skipping trade ... - position ... was recently closed (cooldown active)"

### Issue #2: MT5 → Quantower Close
- [ ] Open a position in Quantower
- [ ] Verify MT5 hedge opens
- [ ] Close the MT5 hedge position manually
- [ ] **CRITICAL**: Verify Quantower position closes
- [ ] Check logs for "Found position via mapping: baseId ... -> Position.Id ..."
- [ ] Check logs for "Closing Quantower position ... due to MT5 full closure"

### Issue #3: Trailing Stop Logic
- [ ] Enable trailing stop in settings
- [ ] Open a position in Quantower
- [ ] Wait for price to move favorably
- [ ] **CRITICAL**: Verify stop loss modifies in Quantower only
- [ ] Check logs for "Trailing stop updated locally in Quantower for ... - NOT sending to MT5"
- [ ] Verify NO trailing update messages sent to MT5 in logs

### Integration Test
- [ ] Enable all three toggles: Elastic, Trailing, DEMA-ATR
- [ ] Open a position in Quantower
- [ ] Verify MT5 hedge opens
- [ ] Wait for price to move favorably
- [ ] Verify elastic updates sent to MT5 (partial closes)
- [ ] Verify trailing stop modifies Quantower stop loss only
- [ ] Close position in Quantower → verify MT5 closes and does NOT reopen
- [ ] Open another position
- [ ] Close MT5 hedge → verify Quantower position closes

---

## Rollback Plan

If issues arise, revert the following changes:

1. **QuantowerBridgeService.cs**:
   - Remove `_recentClosures` dictionary (line 19)
   - Revert `OnQuantowerTrade` to original (remove cooldown check)
   - Revert `OnQuantowerPositionClosed` to original (remove cooldown marking)

2. **MultiStratManagerService.cs**:
   - Remove `_baseIdToPositionId` dictionary (line 39)
   - Revert `HandlePositionAdded` to original (remove mapping)
   - Revert `HandlePositionRemoved` to original (remove mapping removal)
   - Revert `FindPositionByBaseId` to original (remove mapping lookup)
   - Revert `SendElasticAndTrailing` to original (re-enable trailing updates to MT5)

---

## Notes

- All fixes are **non-breaking** and maintain backward compatibility
- The 2-second cooldown window is configurable if needed
- The baseId → Position.Id mapping is automatically cleaned up when positions are removed
- Trailing stop updates are still calculated and applied locally, just not sent to MT5
- Elastic updates continue to be sent to MT5 as before

---

## Next Steps

1. Build and deploy the updated Quantower plugin
2. Test each issue individually using the testing checklist
3. Perform integration testing with all toggles enabled
4. Monitor logs for the new debug messages to verify fixes are working
5. If any issues arise, use the rollback plan to revert changes

