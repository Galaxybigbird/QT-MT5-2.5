# Quantower→MT5 Bridge Position Tracking Hardening

**Date:** 2025-01-XX  
**Status:** ✅ Completed

## Overview

This document describes the hardening of the Quantower→MT5 bridge to fix trade synchronization issues by properly handling Position IDs without assuming they are reused after closure.

## Problem Statement

The previous implementation assumed that Quantower might reuse Position IDs after a position is closed, leading to:
1. A 5-second cooldown mechanism that blocked legitimate new positions
2. Potential race conditions where new positions were incorrectly rejected
3. Difficulty debugging position lifecycle events due to insufficient logging

## Key Assumptions (Corrected)

**CRITICAL:** Quantower does NOT reuse Position IDs. Each position has a unique `position.Id` that is never reused, even after the position is closed.

## Changes Made

### 1. MultiStratManagerService.cs

#### Removed Cooldown Mechanism
- **Removed:** `_closedPositionCooldowns` dictionary (line 38)
- **Removed:** `COOLDOWN_SECONDS` constant (line 40)
- **Rationale:** Cooldown was based on incorrect assumption that Position IDs are reused

#### Enhanced HandlePositionAdded (lines 1224-1288)
**Changes:**
- Removed cooldown check (lines 1235-1246)
- Added detailed logging with position details (baseId, Position.Id, Symbol, Quantity)
- Enhanced deduplication check with better logging
- Added source indication in comments (snapshot/event/refresh)
- Improved error messages for debugging

**Key Logic:**
```csharp
// 1. Log position details
var positionDetails = $"baseId={baseId}, Position.Id={position.Id}, Symbol={position.Symbol?.Name}, Qty={position.Quantity:F2}";
EmitLog(QuantowerBridgeService.BridgeLogLevel.Debug, $"HandlePositionAdded called: {positionDetails}");

// 2. Prevent concurrent processing
if (!_processingPositions.TryAdd(baseId, true)) { return; }

// 3. Check if account is enabled
if (!IsAccountEnabled(position)) { ... }

// 4. Deduplicate - check if already tracking
lock (_trackingLock)
{
    if (_trackingStates.ContainsKey(baseId))
    {
        EmitLog(..., $"Position {baseId} already being tracked - skipping duplicate add");
        return;
    }
}

// 5. Maintain baseId → Position.Id mapping
_baseIdToPositionId[baseId] = position.Id;

// 6. Start tracking
_trailingService.RegisterPosition(baseId, position);
SendElasticAndTrailing(position, baseId);
StartTracking(position, baseId);
```

#### Enhanced HandlePositionRemoved (lines 1290-1324)
**Changes:**
- Removed cooldown additions (lines 1312, 1325)
- Added detailed logging with position details
- Ensured proper cleanup of both tracking state and trailing service
- Added explicit RemoveTracker call for trailing service

**Key Logic:**
```csharp
// 1. Log position details
var positionDetails = $"Position.Id={position.Id}, Symbol={position.Symbol?.Name}, Qty={position.Quantity:F2}";
EmitLog(..., $"HandlePositionRemoved called: {positionDetails}");

// 2. Resolve tracked baseId
var existingBaseId = TryResolveTrackedBaseId(position);

// 3. Clean up mappings
_baseIdToPositionId.TryRemove(existingBaseId, out _);

// 4. Stop tracking and remove from trailing service
StopTracking(existingBaseId);
_trailingService.RemoveTracker(existingBaseId);
```

### 2. QuantowerEventBridge.cs

#### Enhanced SnapshotPositions (lines 67-86)
**Changes:**
- Updated epsilon check from `> 0.0` to `> double.Epsilon` for precision
- Added logging to indicate snapshot filtering
- Reports count of active vs total positions

**Key Logic:**
```csharp
// Filter out historical/closed positions
// Only return positions with non-zero quantity (active positions)
var activePositions = positions.Where(p => p != null && Math.Abs(p.Quantity) > double.Epsilon).ToArray();

LogInfo($"SnapshotPositions: Found {activePositions.Length} active positions (filtered from {positions.Count()} total)");

return activePositions;
```

#### Added LogInfo Method (lines 190-193)
- Added `LogInfo` helper method to support snapshot logging
- Consistent with existing `LogWarn` and `LogError` methods

### 3. QuantowerBridgeService.cs

#### Enhanced TryPublishPositionSnapshotAsync (lines 470-479)
**Changes:**
- Added `[SNAPSHOT]` prefix to log messages
- Added position details (Symbol, Quantity) to logs
- Clearly indicates source is from startup snapshot

#### Enhanced OnQuantowerPositionAdded (lines 511-556)
**Changes:**
- Added `[CORE EVENT]` prefix to log messages
- Added position details (Symbol, Quantity) to logs
- Clearly indicates source is from Core.PositionAdded event

#### Enhanced OnQuantowerPositionClosed (lines 481-513)
**Changes:**
- Added `[CORE EVENT]` prefix to log messages
- Added position details at entry point
- Improved debugging visibility for position removal events

## Position Lifecycle Flow

### Position Added Entry Points
1. **Snapshot (Startup):**
   ```
   QuantowerBridgeService.StartCoreAsync()
   → bridge.SnapshotPositions() [filters by Quantity > double.Epsilon]
   → TryPublishPositionSnapshotAsync() [logs with [SNAPSHOT]]
   → RaisePositionAdded()
   → MultiStratManagerService.HandlePositionAdded()
   ```

2. **Core Event (Runtime):**
   ```
   Core.PositionAdded event
   → QuantowerEventBridge.HandlePositionAdded()
   → OnQuantowerPositionAdded() [logs with [CORE EVENT]]
   → RaisePositionAdded()
   → MultiStratManagerService.HandlePositionAdded()
   ```

3. **Account Refresh:**
   ```
   MultiStratManagerService.RefreshAccountPositions()
   → HandlePositionAdded() [when account is enabled]
   ```

### Position Removed Entry Points
1. **Core Event:**
   ```
   Core.PositionRemoved event
   → QuantowerEventBridge.HandlePositionRemoved()
   → OnQuantowerPositionClosed() [logs with [CORE EVENT]]
   → RaisePositionRemoved()
   → MultiStratManagerService.HandlePositionRemoved()
   ```

## Deduplication Strategy

The implementation uses multiple layers of deduplication:

1. **Snapshot Filtering:** Only positions with `Math.Abs(Quantity) > double.Epsilon` are included
2. **Processing Set:** `_processingPositions` prevents concurrent processing of same baseId
3. **Tracking State Check:** `_trackingStates.ContainsKey(baseId)` prevents duplicate tracking
4. **Bridge-Level Deduplication:** `_recentPositionSubmissions` prevents duplicate gRPC submissions within 1 second

## Mapping Management

### baseId → Position.Id Mapping
- **Purpose:** Allows finding Quantower positions when MT5 sends closure notifications
- **Created:** In `HandlePositionAdded` when position is first tracked
- **Removed:** In `HandlePositionRemoved` when position is closed
- **Storage:** `_baseIdToPositionId` ConcurrentDictionary

### Base ID Resolution
- **Priority 1:** Use `position.Id` if available
- **Priority 2:** Generate fallback ID: `{accountId}:{symbolName}:{seed}`
- **Seed:** Uses `position.OpenTime.Ticks` or object hash code

## Logging Enhancements

### Log Prefixes
- `[SNAPSHOT]` - Position from startup snapshot
- `[CORE EVENT]` - Position from Quantower Core event

### Position Details Format
```
baseId={baseId}, Position.Id={position.Id}, Symbol={symbol}, Qty={quantity:F2}
```

### Key Log Points
1. Entry to HandlePositionAdded/Removed
2. Duplicate detection (already processing, already tracking)
3. Account enabled/disabled checks
4. Mapping creation/removal
5. Tracking start/stop

## Testing Recommendations

### Unit Tests
1. Verify snapshot filtering excludes zero-quantity positions
2. Verify deduplication prevents duplicate tracking
3. Verify mapping lifecycle (create on add, remove on close)
4. Verify concurrent processing prevention

### Integration Tests
1. **Startup Snapshot:**
   - Open positions in Quantower before connecting bridge
   - Connect bridge and verify positions are published once
   - Check logs for `[SNAPSHOT]` prefix

2. **Runtime Position Add:**
   - Open new position while bridge is connected
   - Verify position is tracked once
   - Check logs for `[CORE EVENT]` prefix

3. **Position Close:**
   - Close position in Quantower
   - Verify tracking stops and mappings are cleaned up
   - Verify position does NOT reopen

4. **Account Enable/Disable:**
   - Disable account with open positions
   - Verify positions stop tracking
   - Re-enable account
   - Verify positions are refreshed correctly

5. **Rapid Position Changes:**
   - Open and close positions rapidly
   - Verify no duplicate tracking
   - Verify no stale mappings remain

## Rollback Plan

If issues arise, revert these commits:
1. Remove `LogInfo` method from QuantowerEventBridge.cs
2. Restore `_closedPositionCooldowns` dictionary and `COOLDOWN_SECONDS` constant
3. Restore cooldown checks in HandlePositionAdded
4. Restore cooldown additions in HandlePositionRemoved
5. Remove `[SNAPSHOT]` and `[CORE EVENT]` log prefixes

## Success Criteria

✅ No cooldown-based blocking of legitimate positions  
✅ Proper deduplication via tracking state check  
✅ Clean mapping lifecycle (create on add, remove on close)  
✅ Comprehensive logging with source indication  
✅ Snapshot filtering excludes closed positions  
✅ No position reopen loops after closure  

## Related Documents

- `QUANTOWER_FINAL_FIXES_20250929.md` - Previous fixes
- `QUANTOWER_POSITION_TRACKING_ANALYSIS.md` - Original analysis
- `QUANTOWER_CRITICAL_FIXES_IMPLEMENTATION.md` - Critical fixes implementation

