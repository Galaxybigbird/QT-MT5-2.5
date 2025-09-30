# Quantower-MT5 Bridge: Critical Fixes Implementation (V2)

**Date:** 2025-09-30  
**Status:** New fixes based on actual log evidence

## Overview

This document provides the correct implementation fixes for all four critical issues, based on analysis of actual log files showing where and why the previous fixes failed.

---

## Fix #1 & #4: Prevent Position Reopening After Close

### Problem
When closing a position in Quantower, the closing trade is being sent to MT5 as a new trade, causing the position to reopen.

### Root Cause
The cooldown check in `OnQuantowerTradeFilled` is keyed by `Position.Id`, but the closing trade has a different `Trade.Id`. The check fails to match the closing trade to the recently closed position.

### Solution

**File:** `MultiStratManagerRepo/Quantower/QuantowerMultiStratAddOn/QuantowerBridgeService.cs`

**Current Code (lines 429-444):**
```csharp
private void OnQuantowerTradeFilled(Trade trade)
{
    var positionId = trade?.PositionId;
    if (!string.IsNullOrWhiteSpace(positionId) &&
        _recentClosures.TryGetValue(positionId, out var closureTime))
    {
        if ((DateTime.UtcNow - closureTime).TotalSeconds < 2)
        {
            EmitLog(BridgeLogLevel.Debug, $"Skipping trade {trade?.Id} - position {positionId} was recently closed");
            _recentClosures.TryRemove(positionId, out _);
            return;
        }
        _recentClosures.TryRemove(positionId, out _);
    }
    
    // ... rest of method
}
```

**Fixed Code:**
```csharp
private void OnQuantowerTradeFilled(Trade trade)
{
    // CRITICAL FIX: Check BOTH Trade.PositionId AND the baseId extracted from the trade
    // Closing trades may have different IDs than the position they're closing
    
    var positionId = trade?.PositionId;
    var baseId = ExtractBaseIdFromTrade(trade);  // Extract baseId from trade properties
    
    // Check if this position was recently closed (by Position.Id)
    if (!string.IsNullOrWhiteSpace(positionId) &&
        _recentClosures.TryGetValue(positionId, out var closureTime))
    {
        if ((DateTime.UtcNow - closureTime).TotalSeconds < 2)
        {
            EmitLog(BridgeLogLevel.Debug, $"Skipping trade {trade?.Id} - position {positionId} was recently closed (matched by PositionId)");
            _recentClosures.TryRemove(positionId, out _);
            return;
        }
        _recentClosures.TryRemove(positionId, out _);
    }
    
    // ALSO check by baseId (in case PositionId doesn't match)
    if (!string.IsNullOrWhiteSpace(baseId) &&
        _recentClosures.TryGetValue(baseId, out closureTime))
    {
        if ((DateTime.UtcNow - closureTime).TotalSeconds < 2)
        {
            EmitLog(BridgeLogLevel.Debug, $"Skipping trade {trade?.Id} - position {baseId} was recently closed (matched by baseId)");
            _recentClosures.TryRemove(baseId, out _);
            return;
        }
        _recentClosures.TryRemove(baseId, out _);
    }
    
    // ... rest of method
}

// Helper method to extract baseId from trade
private string? ExtractBaseIdFromTrade(Trade? trade)
{
    if (trade == null) return null;
    
    // Try to get baseId from trade properties
    // Quantower trades may store baseId in Comment, AdditionalInfo, or other fields
    try
    {
        // Check Comment field first
        if (!string.IsNullOrWhiteSpace(trade.Comment))
        {
            // If comment contains a GUID-like string, it might be the baseId
            if (Guid.TryParse(trade.Comment, out _))
                return trade.Comment;
        }
        
        // Check AdditionalInfo
        if (trade.AdditionalInfo != null && trade.AdditionalInfo.Count > 0)
        {
            if (trade.AdditionalInfo.TryGetValue("base_id", out var baseIdObj) && baseIdObj != null)
                return baseIdObj.ToString();
        }
        
        // Fallback: use PositionId if it looks like a GUID
        if (!string.IsNullOrWhiteSpace(trade.PositionId) && Guid.TryParse(trade.PositionId, out _))
            return trade.PositionId;
    }
    catch
    {
        // Ignore errors
    }
    
    return null;
}
```

**Also update `OnQuantowerPositionClosed` to store BOTH Position.Id AND baseId:**
```csharp
private void OnQuantowerPositionClosed(Position position)
{
    // Mark BOTH Position.Id AND baseId as recently closed
    var positionId = position?.Id;
    if (!string.IsNullOrWhiteSpace(positionId))
    {
        _recentClosures[positionId] = DateTime.UtcNow;
        EmitLog(BridgeLogLevel.Debug, $"Marked position {positionId} as recently closed (by Position.Id)");
    }
    
    // ALSO mark by baseId
    var baseId = ExtractBaseIdFromPosition(position);
    if (!string.IsNullOrWhiteSpace(baseId) && baseId != positionId)
    {
        _recentClosures[baseId] = DateTime.UtcNow;
        EmitLog(BridgeLogLevel.Debug, $"Marked position {baseId} as recently closed (by baseId)");
    }
    
    // ... rest of method
}

private string? ExtractBaseIdFromPosition(Position? position)
{
    if (position == null) return null;
    
    try
    {
        // Check Comment field
        if (!string.IsNullOrWhiteSpace(position.Comment) && Guid.TryParse(position.Comment, out _))
            return position.Comment;
        
        // Check AdditionalInfo
        if (position.AdditionalInfo != null && position.AdditionalInfo.Count > 0)
        {
            if (position.AdditionalInfo.TryGetValue("base_id", out var baseIdObj) && baseIdObj != null)
                return baseIdObj.ToString();
        }
        
        // Fallback: use Position.Id
        return position.Id;
    }
    catch
    {
        return position.Id;
    }
}
```

---

## Fix #2: MT5 → Quantower Close Logic

### Problem
When MT5 closes a hedge position, the corresponding Quantower position does not close because `FindPositionByBaseId` cannot find the position.

### Root Cause
`FindPositionByBaseId` searches for positions by matching baseId directly against `Position.Id`, but Quantower positions have their own IDs. The `_baseIdToPositionId` mapping exists but is not being used.

### Solution

**File:** `MultiStratManagerRepo/Quantower/QuantowerMultiStratAddOn/MultiStratManagerService.cs`

**Current Code (lines 1176-1223):**
```csharp
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

    foreach (var pos in core.Positions)
    {
        if (string.Equals(GetBaseId(pos), baseId, StringComparison.OrdinalIgnoreCase))
        {
            return pos;
        }
    }

    return null;
}
```

**Fixed Code:**
```csharp
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

    // CRITICAL FIX: First try to find position using the baseId → Position.Id mapping
    if (_baseIdToPositionId.TryGetValue(baseId, out var quantowerPositionId))
    {
        EmitLog(QuantowerBridgeService.BridgeLogLevel.Debug, $"Found Position.Id {quantowerPositionId} for baseId {baseId} via mapping");
        
        // Search for position by Quantower Position.Id
        foreach (var pos in core.Positions)
        {
            if (string.Equals(pos.Id, quantowerPositionId, StringComparison.OrdinalIgnoreCase))
            {
                EmitLog(QuantowerBridgeService.BridgeLogLevel.Info, $"✅ Found Quantower position {pos.Id} for baseId {baseId}");
                return pos;
            }
        }
        
        EmitLog(QuantowerBridgeService.BridgeLogLevel.Warn, $"Position.Id {quantowerPositionId} found in mapping but position not in Core.Positions");
    }

    // Fallback: Try direct baseId match (for backward compatibility)
    foreach (var pos in core.Positions)
    {
        if (string.Equals(GetBaseId(pos), baseId, StringComparison.OrdinalIgnoreCase))
        {
            EmitLog(QuantowerBridgeService.BridgeLogLevel.Info, $"✅ Found Quantower position {pos.Id} for baseId {baseId} (direct match)");
            return pos;
        }
    }

    EmitLog(QuantowerBridgeService.BridgeLogLevel.Warn, $"❌ Could not find Quantower position for baseId {baseId}");
    return null;
}
```

**Also update `OnBridgeStreamEnvelopeReceived` to actually close the position:**
```csharp
private void OnBridgeStreamEnvelopeReceived(QuantowerBridgeService.BridgeStreamEnvelope envelope)
{
    // ... existing code ...
    
    // Handle MT5 close notifications
    if (envelope.Action == "MT5_CLOSE_NOTIFICATION")
    {
        var closureReason = envelope.ClosureReason ?? "unknown";
        
        // Skip elastic partial closes (these don't close the Quantower position)
        if (closureReason.Contains("elastic_partial_close", StringComparison.OrdinalIgnoreCase))
        {
            EmitLog(QuantowerBridgeService.BridgeLogLevel.Info, $"MT5 partial close for {baseId} - ignoring (Quantower position remains open)");
            return;
        }
        
        // CRITICAL FIX: Find and close the Quantower position
        var position = FindPositionByBaseId(baseId);
        if (position != null)
        {
            EmitLog(QuantowerBridgeService.BridgeLogLevel.Info, $"🔴 Closing Quantower position {position.Id} due to MT5 closure (reason: {closureReason})");
            
            try
            {
                // Close the position in Quantower
                position.Close();
                
                EmitLog(QuantowerBridgeService.BridgeLogLevel.Info, $"✅ Successfully closed Quantower position {position.Id}");
                
                // Stop tracking
                StopTracking(baseId);
                _trailingService.RemoveTracker(baseId);
            }
            catch (Exception ex)
            {
                EmitLog(QuantowerBridgeService.BridgeLogLevel.Error, $"❌ Failed to close Quantower position {position.Id}: {ex.Message}");
            }
        }
        else
        {
            EmitLog(QuantowerBridgeService.BridgeLogLevel.Warn, $"⚠️ MT5 close notification for {baseId} but Quantower position not found");
        }
        
        return;
    }
    
    // ... rest of method ...
}
```

---

## Fix #3: Trailing Stop Logic

### Problem
Trailing stops never activate (threshold too high) and when they do, the DEMA-ATR calculation produces incorrect offsets.

### Root Cause
1. Trailing uses the same $100 threshold as elastic hedging, but profit never reaches $100
2. DEMA-ATR calculation can produce huge offsets (e.g., ATR * 1.5 = 150 points)
3. Stop loss modification code is never reached

### Solution

**File:** `MultiStratManagerRepo/Quantower/QuantowerMultiStratAddOn/Services/TrailingElasticService.cs`

**Add separate trailing activation threshold:**
```csharp
public class TrailingElasticService
{
    // ... existing properties ...
    
    // NEW: Separate trailing activation threshold (lower than elastic)
    public ProfitUnitType TrailingActivationUnits { get; set; } = ProfitUnitType.Dollars;
    public double TrailingActivationThreshold { get; set; } = 20.0;  // Activate at $20 profit instead of $100
    
    // ... rest of class ...
}
```

**Update `TryBuildTrailingUpdate` to use separate threshold:**
```csharp
public Dictionary<string, object?>? TryBuildTrailingUpdate(string baseId, Position position)
{
    // ... existing validation code ...
    
    var profitDollars = PnLUtils.GetMoney(position.GrossPnL);
    
    // CRITICAL FIX: Use SEPARATE activation threshold for trailing (not the same as elastic)
    var activationUnits = ConvertUnits(TrailingActivationUnits, position, tracker, currentPrice, profitDollars);
    
    LogDebug?.Invoke($"[Trailing] {baseId}: activationUnits={activationUnits:F2}, threshold={TrailingActivationThreshold:F2}, profit=${profitDollars:F2}");
    
    if (activationUnits < TrailingActivationThreshold)
    {
        LogDebug?.Invoke($"[Trailing] {baseId}: activation threshold not met ({activationUnits:F2} < {TrailingActivationThreshold:F2})");
        return null;
    }
    
    // ... rest of method ...
}
```

**Fix DEMA-ATR calculation to cap the offset:**
```csharp
private double ComputeTrailingOffset(Position position, ElasticTracker tracker, double currentPrice)
{
    if (UseDemaAtrTrailing)
    {
        var atr = GetAtr(position.Symbol, AtrPeriod);
        if (atr.HasValue && atr.Value > 0)
        {
            // CRITICAL FIX: Cap ATR-based offset to reasonable percentage of current price
            var atrOffset = atr.Value * Math.Max(0.1, DemaAtrMultiplier);
            var maxOffset = currentPrice * 0.02;  // Max 2% of current price
            var cappedOffset = Math.Min(atrOffset, maxOffset);
            
            LogDebug?.Invoke($"[Trailing] ATR offset: raw={atrOffset:F5}, capped={cappedOffset:F5}, maxAllowed={maxOffset:F5}");
            
            return cappedOffset;
        }
        
        var dema = GetDema(position.Symbol, DemaPeriod);
        if (dema.HasValue && dema.Value > 0)
        {
            var delta = Math.Abs(currentPrice - dema.Value);
            var demaOffset = delta * Math.Max(1.0, DemaAtrMultiplier);
            var maxOffset = currentPrice * 0.02;  // Max 2% of current price
            var cappedOffset = Math.Min(demaOffset, maxOffset);
            
            LogDebug?.Invoke($"[Trailing] DEMA offset: raw={demaOffset:F5}, capped={cappedOffset:F5}, maxAllowed={maxOffset:F5}");
            
            return cappedOffset;
        }
    }
    
    // Fallback to static offset
    return TrailingStopUnits switch
    {
        ProfitUnitType.Dollars => Math.Max(0.0, TrailingStopValue / Math.Max(1.0, tracker.Quantity)),
        ProfitUnitType.Pips => TrailingStopValue * GetPipSize(position.Symbol),
        ProfitUnitType.Ticks => TrailingStopValue * GetTickSize(position.Symbol),
        ProfitUnitType.Percent => Math.Abs(currentPrice) * (TrailingStopValue / 100.0),
        _ => Math.Abs(currentPrice - tracker.EntryPrice) * 0.5
    };
}
```

---

## Testing Checklist

### Issue #1 & #4 Testing
- [ ] Open a position in Quantower
- [ ] Close the position in Quantower
- [ ] Verify MT5 hedge closes
- [ ] Verify position does NOT reopen
- [ ] Check logs for "Skipping trade ... was recently closed" messages

### Issue #2 Testing
- [ ] Open a position in Quantower (creates MT5 hedge)
- [ ] Manually close the MT5 hedge position
- [ ] Verify Quantower position closes automatically
- [ ] Check logs for "Found Position.Id ... for baseId ... via mapping"
- [ ] Check logs for "Closing Quantower position ... due to MT5 closure"

### Issue #3 Testing
- [ ] Open a position in Quantower
- [ ] Wait for profit to reach $20 (new threshold)
- [ ] Verify trailing stop activates (check logs for "[Trailing] ... Building trailing update")
- [ ] Verify stop loss is updated in Quantower (check logs for "🎯 Updating Quantower stop loss")
- [ ] Verify stop loss price is reasonable (within 2% of current price)
- [ ] Verify NO trailing updates are sent to MT5 EA

---

## Rollback Plan

If issues persist:
1. Revert changes to `QuantowerBridgeService.cs`
2. Revert changes to `MultiStratManagerService.cs`
3. Revert changes to `TrailingElasticService.cs`
4. Restore from git: `git checkout HEAD -- MultiStratManagerRepo/Quantower/`

