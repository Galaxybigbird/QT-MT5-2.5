# Quantower Plugin - Critical Fixes Applied (2025-09-29)

## 🔴 **CRITICAL ISSUES IDENTIFIED & FIXED**

---

## **ISSUE 1: Trailing Activation Threshold Too High** ❌ NOT FIXED (USER ACTION REQUIRED)

### **Problem:**
From `logs.txt` lines 81-305:
```
[Trailing] 5247c0ada94d4975adf037a6fb3cb018: activationUnits=0.00, threshold=1.00, profit=$0.00
[Trailing] 5247c0ada94d4975adf037a6fb3cb018: activation threshold not met (0.00 < 1.00), skipping
```

**Root Cause:**
- Trailing activation is set to **1.00 in PERCENT mode**
- This means the position needs to be **1% in profit** before trailing activates
- With profits of $10-$105, this threshold is NEVER met on a typical position
- **Example**: On a $100,000 position, 1% = $1,000. Your $105 profit is only 0.105%!

### **Solution:**
**USER MUST CHANGE SETTINGS IN UI:**

1. **Option A: Switch to DOLLARS mode**
   - Set "Trailing Activation Units" to **Dollars**
   - Set "Trailing Activation Value" to **10** (activates at $10 profit)

2. **Option B: Lower the PERCENT threshold**
   - Keep "Trailing Activation Units" as **Percent**
   - Set "Trailing Activation Value" to **0.01** (activates at 0.01% = $10 on $100k position)

### **Why This Happened:**
The default settings were likely copied from NinjaTrader where position sizes and profit scales are different.

---

## **ISSUE 2: MT5 Partial Closes Closing Entire Quantower Position** ✅ FIXED

### **Problem:**
From `logs.txt` line 306-309:
```
[21:49:27] Debug: Bridge stream payload received: {"action":"MT5_CLOSE_NOTIFICATION","nt_trade_result":"elastic_partial_close",...}
[21:49:27] Info: MT5 closed position for 5247c0ada94d4975adf037a6fb3cb018 - closing corresponding Quantower position
[21:49:27] Info: Closing Quantower position 5247c0ada94d4975adf037a6fb3cb018 (base_id=5247c0ada94d4975adf037a6fb3cb018) due to MT5 closure
[21:49:27] Info: Quantower position closed (5247c0ada94d4975adf037a6fb3cb018) -> notifying bridge
```

**Root Cause:**
- MT5 sends `MT5_CLOSE_NOTIFICATION` for BOTH full closes AND partial closes (elastic updates)
- My previous fix closed the Quantower position for ANY `MT5_CLOSE_NOTIFICATION`
- When MT5 did an elastic partial close (reducing hedge size), it closed the ENTIRE Quantower position!

### **Solution Applied:**
**File:** `MultiStratManagerRepo/Quantower/QuantowerMultiStratAddOn/MultiStratManagerService.cs`

Added logic to parse the `RawJson` and check:
1. **`nt_trade_result`** field:
   - `"elastic_partial_close"` → Ignore (keep Quantower position open)
   - `"MT5_position_closed"` → Close Quantower position
   - Contains `"position_closed"` → Close Quantower position

2. **`total_quantity`** field:
   - `0` → Full close (close Quantower position)
   - `> 0` → Partial close (ignore)

**New Logic:**
```csharp
// Determine if this is a full close
if (totalQuantity == 0 || 
    tradeResult.Contains("position_closed", StringComparison.OrdinalIgnoreCase) ||
    tradeResult.Contains("MT5_position_closed", StringComparison.OrdinalIgnoreCase))
{
    isFullClose = true;
}

if (tradeResult.Contains("partial", StringComparison.OrdinalIgnoreCase) && totalQuantity > 0)
{
    isFullClose = false;
}

if (isFullClose)
{
    // Close Quantower position
}
else
{
    EmitLog(BridgeLogLevel.Info, $"MT5 partial close for {baseId} - ignoring (Quantower position remains open)");
}
```

### **Expected Result:**
✅ Elastic partial closes on MT5 will NO LONGER close the Quantower position
✅ Only full MT5 closures will close the corresponding Quantower position

---

## **ISSUE 3: Duplicate Initial Trades** ❌ NOT FIXED (NEEDS INVESTIGATION)

### **Problem:**
From `logs.txt` lines 327-339:
```
[21:49:41] Info: MT5 closed position for a45e1e5b-9d42-4757-8b23-6e8ca19735f0 - closing corresponding Quantower position
[21:49:41] Warn: MT5 closure notification for a45e1e5b-9d42-4757-8b23-6e8ca19735f0 but no matching Quantower position found
[21:49:42] Info: MT5 closed position for 4c5440b1-c26f-4780-8c59-e4320857ff9c - closing corresponding Quantower position
[21:49:42] Warn: MT5 closure notification for 4c5440b1-c26f-4780-8c59-e4320857ff9c but no matching Quantower position found
[21:49:43] Info: MT5 closed position for 995e006f-75db-4f4c-85a9-2dc85f7d32a9 - closing corresponding Quantower position
[21:49:43] Warn: MT5 closure notification for 995e006f-75db-4f4c-85a9-2dc85f7d32a9 but no matching Quantower position found
```

**Root Cause:**
MT5 is sending close notifications for positions that don't exist in Quantower. This suggests:
1. When the plugin starts, existing MT5 positions are being sent as NEW trades to Quantower
2. These create duplicate hedges on MT5 (with different base_ids)
3. When MT5 closes these duplicates, Quantower doesn't know about them

**Likely Source:**
- `SnapshotPositions()` in `QuantowerBridgeService` or `QuantowerTradeMapper`
- Position snapshots are being sent as new trades instead of being marked as snapshots

### **Next Steps:**
1. Need to investigate `TryBuildPositionSnapshot()` in `QuantowerTradeMapper.cs`
2. Check if snapshots are marked with a special flag
3. Verify bridge/MT5 can distinguish snapshots from new trades
4. Consider skipping snapshots entirely on plugin startup

---

## **ISSUE 4: User Misunderstanding of Settings** ✅ CLARIFIED

### **User's Statement:**
> "when elastic, trailing, and dema-atr trailing is on, the stoploss limit placement and the increment trailing should use the settings set in DEMA-ATR Trailing BUT the elastic updates should still use what i set in the default trailing settings"

### **Clarification:**
**The user is CORRECT!** The current implementation already works this way:

1. **"Default Trailing Settings" (Elastic Hedging Settings section)**
   - Controls: `ElasticTriggerUnits`, `ElasticIncrementUnits`, `ElasticIncrementValue`
   - Used by: `TryBuildElasticUpdate()` → Elastic hedge updates
   - Purpose: Determines when to increase hedge size and by how much

2. **"DEMA-ATR Trailing" section**
   - Controls: `TrailingActivationUnits`, `TrailingActivationValue`, `UseDemaAtrTrailing`, `AtrPeriod`, `DemaPeriod`
   - Used by: `TryBuildTrailingUpdate()` → Trailing stop placement
   - Purpose: Determines when to move trailing stops and where to place them

**The architecture is correct!** The issue is just that the trailing activation threshold is too high (see Issue 1).

---

## 🧪 **TESTING CHECKLIST**

After rebuilding and deploying:

### **Test 1: Trailing Activation**
- [ ] Change trailing activation to Dollars mode with value 10
- [ ] Place a trade in Quantower
- [ ] Wait for profit to reach $10
- [ ] Check logs for: `[Trailing] {baseId}: ✅ Building trailing update - newStop=X`
- [ ] Verify trailing stop is placed on MT5

### **Test 2: Elastic Partial Closes**
- [ ] Place a trade in Quantower
- [ ] Wait for elastic update to trigger
- [ ] Check logs for: `MT5 partial close for {baseId} - ignoring (Quantower position remains open)`
- [ ] Verify Quantower position stays OPEN
- [ ] Verify MT5 hedge size increases

### **Test 3: Full Position Close**
- [ ] Manually close a position on MT5
- [ ] Check logs for: `MT5 full closure notification for {baseId}`
- [ ] Verify Quantower position closes automatically

### **Test 4: Duplicate Trades**
- [ ] Close all positions in Quantower and MT5
- [ ] Restart Quantower plugin
- [ ] Check if duplicate trades are created
- [ ] Document findings

---

## 📝 **BUILD & DEPLOY**

```powershell
cd C:\Documents\Dev\OfficialFuturesHedgebotv2.5QT\MultiStratManagerRepo\Quantower
dotnet build -c Release

# Copy to Quantower
Copy-Item "bin\Release\net8.0-windows\QuantowerMultiStratAddOn.dll" "C:\Quantower\Settings\Scripts\plug-ins\MultiStratQuantower\"
Copy-Item "HTML\layout.html" "C:\Quantower\Settings\Scripts\plug-ins\MultiStratQuantower\"

# Restart Quantower
# Monitor logs
cd C:\Documents\Dev\OfficialFuturesHedgebotv2.5QT
.\scripts\monitor-plugin-loading.ps1
```

---

## 🎯 **SUMMARY**

| Issue | Status | Action Required |
|-------|--------|-----------------|
| Trailing activation threshold too high | ❌ NOT FIXED | **USER: Change UI settings to Dollars/10 or Percent/0.01** |
| MT5 partial closes closing Quantower position | ✅ FIXED | Code updated to ignore partial closes |
| Duplicate initial trades | ❌ NOT FIXED | Needs investigation of SnapshotPositions |
| User settings misunderstanding | ✅ CLARIFIED | Architecture is correct |

---

**Next Immediate Action:** Change trailing activation settings in the UI and test!

