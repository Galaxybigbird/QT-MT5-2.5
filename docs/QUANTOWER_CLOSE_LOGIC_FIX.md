# Quantower 1:1 Close Logic Fix

## Problem Statement

When closing multiple Quantower positions, the system was not maintaining 1:1 correlation with MT5 hedge trades. Specifically:

**Observed Behavior:**
- User opens 2 QT positions → 2 MT5 hedge trades open ✓
- User closes 2 QT positions → System opens a 3rd MT5 hedge trade, immediately closes it, and leaves the original 2 MT5 hedges open ✗

**Expected Behavior:**
- User opens 2 QT positions → 2 MT5 hedge trades open ✓
- User closes 2 QT positions → 2 MT5 hedge trades close ✓
- User closes 1 QT position (out of 2) → 1 corresponding MT5 hedge trade closes ✓

## Root Cause Analysis

### Issue: Spurious Position Added Events During Close Operations

When a Quantower position is being closed, Quantower sometimes fires a `PositionAdded` event for the same `Position.Id`. This causes the system to:

1. Interpret the event as a new position opening
2. Send a new trade to MT5
3. Open a new MT5 hedge trade
4. Immediately close it when the actual close event arrives

**Example Sequence (from logs):**
```
[16:47:00] QT position added ...638938305990000000 (1 contract) → Opens MT5 ticket 55247520 ✓
[16:47:01] QT position added ...638938305990000001 (2 contracts) → Opens MT5 ticket 55247521 ✓
[16:47:03] QT position added ...638938306020000001 (1 contract) → Opens MT5 ticket 55247529 ✗ SPURIOUS!
[16:47:04] QT position closed ...638938306020000001 (1 contract) → Closes MT5 ticket 55247529 ✗
```

The baseId `...638938306020000001` was never intentionally opened by the user - it was a spurious event generated during the close process.

### Why Existing Safeguards Didn't Work

The system already had two safeguards:

1. **`_recentClosures` dictionary** - Tracks recently closed positions by `Position.Id` to prevent closing Trade events from being sent as new trades
   - **Limitation:** Only checked in `OnQuantowerTrade()`, not in `OnQuantowerPositionAdded()`

2. **`_recentPositionSubmissions` dictionary** - Prevents duplicate position submissions within 1 second
   - **Limitation:** Only prevents exact duplicates (same baseId), not spurious new positions with different `OpenTime.Ticks`

## Solution

### Fix: Check `_recentClosures` in `OnQuantowerPositionAdded()`

Add a check at the beginning of `OnQuantowerPositionAdded()` to filter out position added events for positions that were recently closed:

```csharp
private void OnQuantowerPositionAdded(Position position)
{
    // CRITICAL FIX: Prevent spurious position additions during close operations
    // When a position is being closed, Quantower sometimes fires a PositionAdded event
    // for the same Position.Id. We need to filter these out to prevent opening new MT5 hedges.
    var rawPositionId = position?.Id;
    if (!string.IsNullOrWhiteSpace(rawPositionId) &&
        _recentClosures.TryGetValue(rawPositionId, out var closureTime))
    {
        var elapsed = DateTime.UtcNow - closureTime;
        if (elapsed.TotalSeconds < 2.0)
        {
            EmitLog(BridgeLogLevel.Debug, $"[CORE EVENT] Skipping position added event for {rawPositionId} - position was recently closed {elapsed.TotalMilliseconds:F0}ms ago (cooldown active)", rawPositionId, rawPositionId);
            return;
        }
        // Cooldown expired, remove from dictionary
        _recentClosures.TryRemove(rawPositionId, out _);
    }

    // ... rest of existing logic ...
}
```

### How It Works

1. When a position is closed, `OnQuantowerPositionClosed()` marks the `Position.Id` in `_recentClosures` with a timestamp
2. If Quantower fires a spurious `PositionAdded` event for the same `Position.Id` within 2 seconds, it's filtered out
3. After 2 seconds, the cooldown expires and the `Position.Id` can be used for new positions again

### Why This Works

- **Reuses existing infrastructure:** Leverages the `_recentClosures` dictionary that was already being used for Trade events
- **Consistent cooldown period:** Uses the same 2-second cooldown as the Trade event filter
- **Handles ID reuse:** After the cooldown expires, Quantower can reuse the same `Position.Id` for a genuinely new position
- **Minimal performance impact:** Simple dictionary lookup with automatic cleanup

## Testing Scenarios

### Scenario 1: Close 2 Positions (2:2 → 0:0)
1. Open 2 QT positions → 2 MT5 hedges open ✓
2. Close 2 QT positions → 2 MT5 hedges close ✓
3. No spurious MT5 trades opened ✓

### Scenario 2: Close 1 Position (2:2 → 1:1)
1. Open 2 QT positions → 2 MT5 hedges open ✓
2. Close 1 QT position → 1 MT5 hedge closes ✓
3. 1 QT position and 1 MT5 hedge remain open ✓

### Scenario 3: Rapid Open/Close
1. Open 1 QT position → 1 MT5 hedge opens ✓
2. Immediately close it → 1 MT5 hedge closes ✓
3. No spurious MT5 trades opened ✓

### Scenario 4: ID Reuse After Cooldown
1. Open 1 QT position (Position.Id = "abc123") → 1 MT5 hedge opens ✓
2. Close it → 1 MT5 hedge closes ✓
3. Wait 3 seconds (cooldown expires)
4. Open new QT position (Position.Id = "abc123" reused) → 1 MT5 hedge opens ✓

## Files Modified

1. **MultiStratManagerRepo/Quantower/QuantowerMultiStratAddOn/QuantowerBridgeService.cs**
   - Added `_recentClosures` check in `OnQuantowerPositionAdded()` (lines 515-531)

## Related Issues

- **Opening Logic:** Fixed in `QUANTOWER_1TO1_HEDGE_FIX.md` - ensures unique baseIds using `Position.Id + OpenTime.Ticks`
- **Trailing Stops:** Fixed in `QUANTOWER_1TO1_HEDGE_FIX.md` - modifies existing stop loss orders instead of creating new ones

## Verification

After applying this fix:
1. Compile the Quantower plugin
2. Copy the DLL to Quantower installation
3. Test the scenarios above
4. Check logs for "Skipping position added event" messages when spurious events are filtered
5. Verify that MT5 hedge count matches QT position count at all times

## Log Messages

**When spurious event is filtered:**
```
[CORE EVENT] Skipping position added event for 79a8ace8e1ec4169a472181074cf8c76 - position was recently closed 150ms ago (cooldown active)
```

**When legitimate position is added:**
```
[CORE EVENT] Quantower position added (79a8ace8e1ec4169a472181074cf8c76_638938305990000000) - Symbol=NQZ5, Qty=1.00 -> notifying bridge
```

