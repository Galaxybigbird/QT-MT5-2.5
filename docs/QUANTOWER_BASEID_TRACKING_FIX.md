# Quantower BaseID Tracking Fix

## 🔴 Problem Identified

The closing logic was broken because **Quantower changes `OpenTime.Ticks` on close events**, causing baseId mismatches between open and close operations.

### Root Cause

1. **Position Opens:**
   - Quantower fires `OnQuantowerPositionAdded` with `Position.Id` and `OpenTime.Ticks`
   - We compute baseId = `Position.Id + OpenTime.Ticks` (e.g., `5cd6825e8d82464ca3499a0c076aae33_638938305990000003`)
   - This baseId is sent to the bridge, which opens an MT5 hedge and tracks it

2. **Position Closes:**
   - Quantower fires `OnQuantowerPositionClosed` with the SAME `Position.Id` but DIFFERENT `OpenTime.Ticks`
   - We recompute baseId using the NEW `OpenTime.Ticks`, creating a DIFFERENT baseId (e.g., `5cd6825e8d82464ca3499a0c076aae33_638938306040000002`)
   - The bridge tries to close this NEW baseId, but it has no MT5 tickets for it because it was never opened

### Evidence from Logs

```
[17:35:21] QT position added ...638938305990000003 -> notifying bridge  ← OPENED
[17:35:23] QT position closed ...638938305990000003 -> notifying bridge  ← CLOSED (correct baseId)
[17:35:29] QT position closed ...638938306040000002 -> notifying bridge  ← WRONG baseId!
[17:35:33] QT position closed ...638938306130000003 -> notifying bridge  ← WRONG baseId!
[17:35:35] QT position closed ...638938306170000003 -> notifying bridge  ← WRONG baseId!
```

Bridge responses:
```
[17:35:31] gRPC: No tracked MT5 tickets remain for BaseID ...638938306040000002
[17:35:35] gRPC: No tracked MT5 tickets remain for BaseID ...638938306130000003
[17:35:37] gRPC: No tracked MT5 tickets remain for BaseID ...638938306170000003
```

**Result:** Only 1 MT5 hedge was opened, but 4 close requests were sent with different baseIds!

## ✅ Solution Implemented

**Track the baseId when positions are added, and use that tracked baseId when closing.**

### Changes Made

#### 1. `QuantowerBridgeService.cs`

**Added baseId tracking dictionary:**
```csharp
private readonly ConcurrentDictionary<string, string> _positionIdToBaseId = new(StringComparer.OrdinalIgnoreCase);
```

**In `OnQuantowerPositionAdded`:** Store the baseId mapping after computing it:
```csharp
// CRITICAL FIX: Track the baseId for this position so we can use it when closing
// Quantower changes OpenTime.Ticks on close events, so we need to remember the original baseId
if (!string.IsNullOrWhiteSpace(rawPositionId))
{
    _positionIdToBaseId[rawPositionId] = positionTradeId;
    EmitLog(BridgeLogLevel.Debug, $"[CORE EVENT] Tracked baseId mapping: {rawPositionId} -> {positionTradeId}");
}
```

**In `OnQuantowerPositionClosed`:** Look up the ORIGINAL baseId and pass it to the mapper:
```csharp
// CRITICAL FIX: Look up the ORIGINAL baseId that was used when the position was opened
// Quantower changes OpenTime.Ticks on close events, so we can't recompute the baseId
string? knownBaseId = null;
if (!string.IsNullOrWhiteSpace(positionId) && _positionIdToBaseId.TryGetValue(positionId, out var trackedBaseId))
{
    knownBaseId = trackedBaseId;
    EmitLog(BridgeLogLevel.Debug, $"[CORE EVENT] Found tracked baseId for position {positionId}: {knownBaseId}");
}
else
{
    EmitLog(BridgeLogLevel.Warn, $"[CORE EVENT] No tracked baseId found for position {positionId} - will compute from current position data (may cause mismatch!)");
}

if (!QuantowerTradeMapper.TryBuildPositionClosure(position, knownBaseId, out var payload, out var closureId))
{
    // ... error handling
}

// Clean up the baseId mapping after closing
if (!string.IsNullOrWhiteSpace(positionId))
{
    _positionIdToBaseId.TryRemove(positionId, out _);
    EmitLog(BridgeLogLevel.Debug, $"[CORE EVENT] Removed baseId mapping for position {positionId}");
}
```

#### 2. `QuantowerTradeMapper.cs`

**Modified `TryBuildPositionClosure` signature:** Added `knownBaseId` parameter:
```csharp
public static bool TryBuildPositionClosure(Position position, string? knownBaseId, out string json, out string? positionId)
```

**Use tracked baseId if provided:**
```csharp
string baseId;
if (!string.IsNullOrWhiteSpace(knownBaseId))
{
    // Use the tracked baseId from when the position was opened
    baseId = knownBaseId;
}
else
{
    // Fall back to computing baseId (may cause mismatch if OpenTime.Ticks changed)
    baseId = ComputeBaseId(position);
    if (string.IsNullOrWhiteSpace(baseId))
    {
        ReportError($"[QT][ERROR] Failed to compute baseId for position closure.", null);
        json = string.Empty;
        positionId = null;
        return false;
    }
}
```

## 🎯 Expected Behavior

### Opening Positions
1. User opens 2 QT positions
2. System computes baseIds using `Position.Id + OpenTime.Ticks`
3. System stores mappings: `Position.Id -> baseId`
4. System sends open requests to bridge → 2 MT5 hedges open ✓

### Closing Positions
1. User closes 2 QT positions
2. Quantower fires close events with DIFFERENT `OpenTime.Ticks`
3. System looks up ORIGINAL baseIds using `Position.Id`
4. System sends close requests with ORIGINAL baseIds → Bridge finds MT5 tickets and closes them ✓
5. System cleans up baseId mappings ✓

### 1:1 Correlation Maintained
- **Open 2 QT → 2 MT5 hedges open** ✓
- **Close 2 QT → 2 MT5 hedges close** ✓
- **Open 2 QT, close 1 QT → 1 MT5 hedge closes** ✓

## 🧪 Testing Instructions

1. **Build the solution:**
   ```powershell
   & "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" "OfficialFuturesHedgebotv2.5QT.sln" /t:Build /p:Configuration=Release /p:Platform="Any CPU" /m /v:minimal
   ```

2. **Deploy to Quantower:**
   - Copy `MultiStratManagerRepo\Quantower\QuantowerMultiStratAddOn\bin\Release\net8.0-windows\*` to your Quantower installation

3. **Test Scenarios:**

   **Scenario 1: Open 2, Close 2**
   - Open 2 QT positions → Verify 2 MT5 hedges open
   - Close 2 QT positions → Verify 2 MT5 hedges close (not 3 or 4!)

   **Scenario 2: Open 2, Close 1**
   - Open 2 QT positions → Verify 2 MT5 hedges open
   - Close 1 QT position → Verify 1 MT5 hedge closes (not 2!)
   - Verify 1 MT5 hedge remains open

   **Scenario 3: Rapid Open/Close**
   - Open 1 QT position → Verify 1 MT5 hedge opens
   - Close 1 QT position immediately → Verify 1 MT5 hedge closes
   - No spurious MT5 trades should be created

4. **Check Logs:**
   - Look for "Tracked baseId mapping" messages when positions open
   - Look for "Found tracked baseId" messages when positions close
   - Verify no "No tracked MT5 tickets remain" errors
   - Verify clean 1:1 correlation in all scenarios

## 📝 Log Messages

**When position opens:**
```
[CORE EVENT] Quantower position added (5cd6825e8d82464ca3499a0c076aae33_638938305990000003) - Symbol=NQZ5, Qty=1.00 -> notifying bridge
[CORE EVENT] Tracked baseId mapping: 5cd6825e8d82464ca3499a0c076aae33 -> 5cd6825e8d82464ca3499a0c076aae33_638938305990000003
```

**When position closes:**
```
[CORE EVENT] OnQuantowerPositionClosed called: Position.Id=5cd6825e8d82464ca3499a0c076aae33, Symbol=NQZ5, Qty=1.00
[CORE EVENT] Found tracked baseId for position 5cd6825e8d82464ca3499a0c076aae33: 5cd6825e8d82464ca3499a0c076aae33_638938305990000003
[CORE EVENT] Quantower position closed (5cd6825e8d82464ca3499a0c076aae33_638938305990000003) -> notifying bridge
[CORE EVENT] Removed baseId mapping for position 5cd6825e8d82464ca3499a0c076aae33
```

**Bridge response:**
```
gRPC: Close hedge request - BaseID: 5cd6825e8d82464ca3499a0c076aae33_638938305990000003
gRPC: Closing 1 MT5 ticket for BaseID 5cd6825e8d82464ca3499a0c076aae33_638938305990000003 (1 QT position = 1 MT5 hedge)
gRPC: Enqueued targeted CLOSE_HEDGE for ticket 55259610
```

## 🔑 Key Insights

1. **Quantower's Quirk:** `OpenTime.Ticks` changes between position added and position closed events for the same position
2. **The Fix:** Track the baseId when positions are added, don't recompute it when closing
3. **Backward Compatibility:** Falls back to computing baseId if no tracked baseId is found
4. **Clean Architecture:** Minimal changes, focused on the exact problem

This fix ensures that the baseId used for closing ALWAYS matches the baseId used for opening, maintaining perfect 1:1 correlation between Quantower positions and MT5 hedge trades! 🚀

