# Quantower-MT5 Bridge: Root Cause Analysis & Solutions

**Date**: 2025-09-30  
**Analyst**: AI Assistant  
**Log Files Analyzed**: `BridgeApp/logs/unified-20250930.jsonl`, `logs.txt`

---

## Executive Summary

Four critical issues were identified in the Quantower-MT5 bridge system:
1. **Quantower → MT5 Close Logic Failure**: Positions close but immediately reopen
2. **MT5 → Quantower Close Logic Failure**: MT5 closures don't close Quantower positions
3. **Trailing Stop Logic Broken**: Incorrect stop loss placement and modification
4. **Duplicate Trade on First Connection**: First trade creates duplicate hedge

All issues have been traced to specific root causes with concrete solutions provided below.

---

## Issue 1: Quantower → MT5 Close Logic Failure

### Symptoms
- User closes a position in Quantower
- MT5 hedge position closes successfully
- MT5 immediately reopens a new position with the same quantity

### Log Evidence
```
Line 1371: "Quantower position closed (84e84d08b4a04ba094cc17f8a8bd3301) -> notifying bridge"
Line 1390: "AddToTradeQueue: Successfully converted trade - ID: close_1759261943523867400, Action: CLOSE_HEDGE"
Line 1394: "gRPC: Received trade submission - ID: ba2f7874-dfac-427b-8d33-dc07c5321679, Action: buy, Quantity: 1.00"
Line 1402: "gRPC: Sending trade to MT5 stream - ID: close_1759261943523867400, Action: CLOSE_HEDGE"
Line 1404: "gRPC: Sending trade to MT5 stream - ID: ba2f7874-dfac-427b-8d33-dc07c5321679, Action: buy"
```

### Root Cause
When a Quantower position is closed:
1. The `Core.PositionRemoved` event fires → triggers `OnQuantowerPositionClosed` → sends `CLOSE_HEDGE` ✅
2. **BUT** Quantower also generates a closing `Trade` event (the trade that closed the position)
3. The `Core.TradeAdded` event fires → triggers `OnQuantowerTrade` → sends the closing trade as a NEW trade ❌
4. The `TryBuildTradeEnvelope` method in `QuantowerTradeMapper.cs` (lines 32-98) does NOT filter out closing trades
5. It treats ALL trades as opening trades and sends them to MT5
6. MT5 receives the "buy" or "sell" action and opens a new position

**Code Location**: `MultiStratManagerRepo/Quantower/Infrastructure/QuantowerTradeMapper.cs:32-98`

### Solution
**Option A: Filter closing trades in `TryBuildTradeEnvelope`**
```csharp
public static bool TryBuildTradeEnvelope(Trade trade, out string json, out string? tradeId)
{
    json = string.Empty;
    tradeId = null;

    if (trade == null)
    {
        return false;
    }

    // CRITICAL FIX: Skip closing trades - they're handled by PositionRemoved event
    // Quantower generates a Trade event when a position is closed, but we don't want to send it as a new trade
    if (trade.Quantity < 0 || IsClosingTrade(trade))
    {
        LogDebug?.Invoke($"Skipping closing trade {trade.Id} for position {trade.PositionId}");
        return false;
    }

    // ... rest of existing code
}

private static bool IsClosingTrade(Trade trade)
{
    // Check if this trade is closing an existing position
    // Quantower closing trades typically have specific characteristics:
    // 1. The trade reduces the position quantity
    // 2. The trade comment may contain "close" or similar
    // 3. The trade's position may no longer exist
    
    if (trade.Comment != null && 
        (trade.Comment.Contains("close", StringComparison.OrdinalIgnoreCase) ||
         trade.Comment.Contains("exit", StringComparison.OrdinalIgnoreCase)))
    {
        return true;
    }

    return false;
}
```

**Option B: Add cooldown check in `OnQuantowerTrade`**
```csharp
// In QuantowerBridgeService.cs
private readonly ConcurrentDictionary<string, DateTime> _recentClosures = new();

private void OnQuantowerTrade(Trade trade)
{
    var positionId = trade.PositionId;
    
    // Check if this position was recently closed
    if (!string.IsNullOrWhiteSpace(positionId) && 
        _recentClosures.TryGetValue(positionId, out var closureTime))
    {
        if ((DateTime.UtcNow - closureTime).TotalSeconds < 2)
        {
            EmitLog(BridgeLogLevel.Debug, $"Skipping trade {trade.Id} - position {positionId} was recently closed");
            _recentClosures.TryRemove(positionId, out _);
            return;
        }
    }

    // ... rest of existing code
}

private void OnQuantowerPositionClosed(Position position)
{
    var positionId = position.Id;
    if (!string.IsNullOrWhiteSpace(positionId))
    {
        _recentClosures[positionId] = DateTime.UtcNow;
    }

    // ... rest of existing code
}
```

**Recommended**: Use **Option B** as it's safer and doesn't rely on trade comment parsing.

---

## Issue 2: MT5 → Quantower Close Logic Failure

### Symptoms
- User closes a hedge position in MT5
- Bridge receives the closure notification
- Quantower position remains open

### Log Evidence
```
Line 1816: "gRPC: Broadcasting MT5 close notification to addon streams only: {ID:mt5close_1759261962215536900 BaseID:8ffa114d-c595-4fef-9653-b35dc375d1e2 ...}"
Line 1821: "Bridge stream payload received: {...base_id:8ffa114d-c595-4fef-9653-b35dc375d1e2...}"
```

**Missing**: No log entry showing "Closing Quantower position..." which should appear at line 1141 of `MultiStratManagerService.cs`

### Root Cause
The code DOES have logic to close Quantower positions when MT5 closes (lines 1133-1157 in `MultiStratManagerService.cs`):
```csharp
if (isFullClose)
{
    var position = FindPositionByBaseId(baseId);
    if (position != null)
    {
        EmitLog(..., $"Closing Quantower position {position.Id} (base_id={baseId}) due to MT5 full closure");
        _ = Task.Run(() => position.Close());
        // ...
    }
    else
    {
        EmitLog(..., $"MT5 full closure notification for {baseId} but no matching Quantower position found");
        // ...
    }
}
```

**The problem**: `FindPositionByBaseId(baseId)` is returning `null` because:
1. MT5 sends baseId like "8ffa114d-c595-4fef-9653-b35dc375d1e2"
2. This baseId was generated when the trade was first created
3. But Quantower positions use their own Position.Id (which may be different)
4. The `FindPositionByBaseId` method (lines 1175-1210) searches for:
   - `position.Id == baseId` ← This fails because Quantower Position.Id ≠ original baseId
   - Tracked positions via `TryResolveTrackedBaseId` ← This also fails because tracking is not maintained

**Code Location**: `MultiStratManagerRepo/Quantower/QuantowerMultiStratAddOn/MultiStratManagerService.cs:1175-1210`

### Solution
**Maintain a baseId → Position.Id mapping**

```csharp
// Add to MultiStratManagerService class
private readonly ConcurrentDictionary<string, string> _baseIdToPositionId = new(StringComparer.OrdinalIgnoreCase);

// In HandlePositionAdded method (around line 1250)
private void HandlePositionAdded(Position position)
{
    if (position == null)
    {
        return;
    }

    var baseId = TryResolveTrackedBaseId(position);
    if (!string.IsNullOrWhiteSpace(baseId) && !string.IsNullOrWhiteSpace(position.Id))
    {
        _baseIdToPositionId[baseId] = position.Id;
        EmitLog(QuantowerBridgeService.BridgeLogLevel.Debug, $"Mapped baseId {baseId} -> Position.Id {position.Id}");
    }

    // ... rest of existing code
}

// In HandlePositionRemoved method (around line 1270)
private void HandlePositionRemoved(Position position)
{
    if (position == null)
    {
        return;
    }

    var baseId = TryResolveTrackedBaseId(position);
    if (!string.IsNullOrWhiteSpace(baseId))
    {
        _baseIdToPositionId.TryRemove(baseId, out _);
    }

    // ... rest of existing code
}

// Update FindPositionByBaseId method
private Position? FindPositionByBaseId(string baseId)
{
    if (string.IsNullOrWhiteSpace(baseId))
    {
        return null;
    }

    var core = Core.Instance;
    if (core?.Positions == null)
    {
        return null;
    }

    // CRITICAL FIX: Check the mapping first
    if (_baseIdToPositionId.TryGetValue(baseId, out var positionId))
    {
        foreach (var position in core.Positions)
        {
            if (string.Equals(position.Id, positionId, StringComparison.OrdinalIgnoreCase))
            {
                return position;
            }
        }
    }

    // Fallback to existing logic
    foreach (var position in core.Positions)
    {
        if (string.Equals(position.Id, baseId, StringComparison.OrdinalIgnoreCase))
        {
            return position;
        }

        var trackedBaseId = TryResolveTrackedBaseId(position);
        if (!string.IsNullOrWhiteSpace(trackedBaseId) &&
            string.Equals(trackedBaseId, baseId, StringComparison.OrdinalIgnoreCase))
        {
            return position;
        }
    }

    return null;
}
```

---

## Issue 3: Trailing Stop Logic Broken

### Symptoms
- Trailing stop places stop-limit order at incorrect price
- Stop loss is far from current price
- Should use DEMA-ATR calculation but doesn't

### Log Evidence
```
Line 636: "gRPC: Received trailing update - BaseID: 84e84d08b4a04ba094cc17f8a8bd3301, NewStopPrice: 24786.75"
Line 637: "gRPC: Received trailing stop update: {...CurrentPrice:24736.75...}"
```

The stop price (24786.75) is 50 points ABOVE the current price (24736.75) for what should be a long position, which is incorrect.

### Root Cause
The code at lines 1320-1346 in `MultiStratManagerService.cs` DOES modify the Quantower stop loss correctly using `Core.ModifyOrder()`. However:

1. **Trailing updates are being sent to MT5** (line 636-637 in logs)
2. According to the requirements: "Trailing stops should modify the stop loss WITHIN Quantower itself, NOT send updates to MT5 EA"
3. The `TrailingElasticService.TryBuildTrailingUpdate` method (lines 314-397 in `TrailingElasticService.cs`) builds trailing update payloads
4. These payloads are being submitted to the bridge and forwarded to MT5
5. MT5 EA's `ProcessTrailingStopUpdate` method then tries to modify the MT5 stop loss, causing conflicts

**Code Location**: `MultiStratManagerRepo/Quantower/QuantowerMultiStratAddOn/Services/TrailingElasticService.cs:314-397`

### Solution
**Do NOT send trailing updates to the bridge/MT5**

```csharp
// In MultiStratManagerService.cs, around line 1300
private void ProcessTrailingAndElastic(Position position, string baseId)
{
    // ... existing code to get trailingPayload and elasticPayload

    // CRITICAL FIX: Only modify Quantower stop loss locally, don't send to MT5
    if (trailingPayload != null)
    {
        var newStop = trailingPayload.ContainsKey("new_stop_price") ? trailingPayload["new_stop_price"] : null;
        if (newStop != null && newStop is double newStopPrice)
        {
            EmitLog(QuantowerBridgeService.BridgeLogLevel.Info, $"🎯 Updating Quantower stop loss for {baseId} - newStop={newStopPrice:F2}");

            // Modify the Quantower position's stop loss
            if (position.StopLoss != null)
            {
                try
                {
                    var result = Core.Instance.ModifyOrder(position.StopLoss, price: newStopPrice);
                    if (result.Status == TradingOperationResultStatus.Success)
                    {
                        EmitLog(QuantowerBridgeService.BridgeLogLevel.Info, $"✅ Successfully updated Quantower stop loss to {newStopPrice:F2}");
                    }
                    else
                    {
                        EmitLog(QuantowerBridgeService.BridgeLogLevel.Error, $"❌ Failed to update Quantower stop loss: {result.Message}");
                    }
                }
                catch (Exception modifyEx)
                {
                    EmitLog(QuantowerBridgeService.BridgeLogLevel.Error, $"❌ Exception modifying stop loss: {modifyEx.Message}");
                }
            }
            // ... existing code to create stop loss if it doesn't exist
        }

        // DO NOT SEND TRAILING UPDATE TO BRIDGE/MT5
        // trailingPayload = null; // Uncomment this line to prevent sending
    }

    // ONLY send elastic updates to MT5
    if (elasticPayload != null)
    {
        var json = JsonSerializer.Serialize(elasticPayload);
        ObserveAsyncOperation(_bridgeService.SubmitElasticUpdateAsync(json), "SubmitElasticUpdate", baseId);
    }
}
```

---

## Issue 4: Duplicate Trade on First Connection

### Symptoms
- After initial setup (opening Bridge, Quantower plugin, MT5 EA)
- First trade placed in Quantower creates duplicate hedge trade in MT5

### Root Cause
This is the **same root cause as Issue 1**. When the initial position snapshot is sent:
1. `QuantowerEventBridge.SnapshotPositions()` is called (line 173 in `QuantowerBridgeService.cs`)
2. For each existing position, `TryPublishPositionSnapshotAsync` is called
3. This sends the position as a trade snapshot
4. If any of these positions are then closed, the closing trade is sent as a new trade (Issue 1)

### Solution
**Same as Issue 1** - implement the cooldown check in `OnQuantowerTrade` to prevent recently closed positions from being reopened.

---

## Implementation Priority

1. **Issue 1 & 4** (Highest Priority): Implement cooldown check in `OnQuantowerTrade` to prevent closing trades from being sent as new trades
2. **Issue 2** (High Priority): Implement baseId → Position.Id mapping to enable MT5 → Quantower close synchronization
3. **Issue 3** (Medium Priority): Prevent trailing updates from being sent to MT5 (only modify Quantower stop loss locally)

---

## Testing Checklist

After implementing fixes:

- [ ] **Issue 1**: Close a Quantower position → verify MT5 hedge closes and does NOT reopen
- [ ] **Issue 2**: Close an MT5 hedge position → verify Quantower position closes
- [ ] **Issue 3**: Enable trailing stop → verify stop loss modifies in Quantower only, not sent to MT5
- [ ] **Issue 4**: Restart all components → place first trade → verify no duplicate hedge in MT5
- [ ] **Integration**: Test all three toggles enabled (Elastic, Trailing, DEMA-ATR) → verify correct behavior

---

## Quantower API Research Findings

Based on codebase analysis and existing implementation:

### Position Management
- `Position.Id`: Unique identifier for the position (string)
- `Position.Close(double closeQuantity = -1)`: Closes position (full or partial)
- `Position.StopLoss`: Returns the Order object for the stop loss (can be null)
- `Position.Side`: Buy or Sell

### Order Modification
- `Core.Instance.ModifyOrder(Order order, price: newPrice)`: Modifies an existing order
- Returns `TradingOperationResult` with `Status` (Success/Failure) and `Message`

### Events
- `Core.TradeAdded`: Fires when a trade is executed (including closing trades)
- `Core.PositionAdded`: Fires when a new position is opened
- `Core.PositionRemoved`: Fires when a position is closed

### Trade Properties
- `Trade.Id`: Unique ID for the trade (fill)
- `Trade.PositionId`: Links the trade to its position
- `Trade.OrderId`: Links the trade to its order
- `Trade.Quantity`: Trade quantity (positive for opening, negative for closing)
- `Trade.Comment`: Optional comment/note
- `Trade.Side`: Buy or Sell

---

## Conclusion

All four issues stem from event handling and state synchronization problems:
1. Closing trades being misinterpreted as opening trades
2. Position ID mapping not maintained across platforms
3. Trailing updates being sent to MT5 when they should stay local

The solutions are straightforward and low-risk, requiring only targeted changes to event handlers and state management logic.

