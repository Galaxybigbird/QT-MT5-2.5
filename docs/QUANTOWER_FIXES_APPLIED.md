# Quantower Plugin Fixes Applied

## Date: 2025-09-29

## Summary

This document details all fixes applied to resolve critical issues with the Quantower plugin integration.

---

## 🔴 **CRITICAL FIX 1: MT5 Close Notification Handler**

### **Issue:**
When MT5 closes a position (e.g., elastic partial close, manual close), it sends `MT5_CLOSE_NOTIFICATION` to the bridge. The bridge forwards it to Quantower, but Quantower was NOT closing the corresponding position. This caused positions to remain open in Quantower even though they were closed on MT5.

### **Root Cause:**
`MultiStratManagerService.OnBridgeStreamEnvelopeReceived()` had no handler for `MT5_CLOSE_NOTIFICATION` action.

### **Fix Applied:**
**File:** `MultiStratManagerRepo/Quantower/QuantowerMultiStratAddOn/MultiStratManagerService.cs`

Added handler in `OnBridgeStreamEnvelopeReceived()` method:
```csharp
// Handle MT5 closure notifications - close corresponding Quantower position
if (action.Equals("MT5_CLOSE_NOTIFICATION", StringComparison.OrdinalIgnoreCase))
{
    EmitLog(BridgeLogLevel.Info, $"MT5 closed position for {baseId} - closing corresponding Quantower position");
    
    var position = FindPositionByBaseId(baseId);
    if (position != null)
    {
        EmitLog(BridgeLogLevel.Info, $"Closing Quantower position {position.Id} (base_id={baseId}) due to MT5 closure");
        _ = Task.Run(() => position.Close());
        StopTracking(baseId);
        _trailingService.RemoveTracker(baseId);
    }
    else
    {
        EmitLog(BridgeLogLevel.Warn, $"MT5 closure notification for {baseId} but no matching Quantower position found");
        StopTracking(baseId);
        _trailingService.RemoveTracker(baseId);
    }
    return;
}
```

Added helper method `FindPositionByBaseId()` to locate Quantower positions by base_id.

### **Expected Result:**
✅ When MT5 closes a position, Quantower will now automatically close the corresponding position.

---

## 🔴 **CRITICAL FIX 2: DEMA-ATR Trailing Debug Logging**

### **Issue:**
DEMA-ATR trailing stops were not working. No trailing happened when trigger was hit, even though elastic updates were working.

### **Root Cause:**
`TryBuildTrailingUpdate()` was returning `null` for unknown reasons. No logging existed to diagnose why.

### **Fix Applied:**
**File:** `MultiStratManagerRepo/Quantower/QuantowerMultiStratAddOn/Services/TrailingElasticService.cs`

1. Added `LogDebug` action property to the service
2. Added comprehensive debug logging at every decision point in `TryBuildTrailingUpdate()`:
   - EnableTrailing check
   - Position null check
   - Tracker not found check
   - Activation threshold check (with values)
   - Offset calculation check (with values)
   - Stop improvement check (with values)

**File:** `MultiStratManagerRepo/Quantower/QuantowerMultiStratAddOn/MultiStratManagerService.cs`

Wired up `LogDebug` action in constructor:
```csharp
_trailingService = new Services.TrailingElasticService
{
    LogWarning = message => EmitLog(BridgeLogLevel.Warn, message),
    LogDebug = message => EmitLog(BridgeLogLevel.Debug, message)
};
```

### **Expected Result:**
✅ Logs will now show exactly why trailing updates are not being generated:
- `[Trailing] {baseId}: EnableTrailing=false, skipping`
- `[Trailing] {baseId}: activation threshold not met (X < Y), skipping`
- `[Trailing] {baseId}: offset <= 0, skipping`
- `[Trailing] {baseId}: stop not improved, skipping`
- `[Trailing] {baseId}: ✅ Building trailing update - newStop=X`

---

## ⚠️ **FIX 3: Remove Window Control Buttons**

### **Issue:**
Minimize, maximize, and close buttons were not working. This is expected because Quantower doesn't expose window control APIs to plugins.

### **Fix Applied:**
**File:** `MultiStratManagerRepo/Quantower/QuantowerMultiStratAddOn/HTML/layout.html`

1. Removed window control CSS styles (lines 48-66)
2. Removed window control buttons from HTML (lines 374-378)
3. Removed window control event listeners from JavaScript (lines 692-700)

### **Expected Result:**
✅ UI is cleaner and doesn't show non-functional buttons. Users rely on Quantower's native window chrome.

---

## 🔧 **FIX 4: Enhanced Flatten Button Logging**

### **Issue:**
Flatten button might not be working, but no logging existed to confirm if the command was reaching the backend.

### **Fix Applied:**
**File:** `MultiStratManagerRepo/Quantower/QuantowerMultiStratAddOn/MultiStratPlugin.cs`

Added logging to `flatten_all` command handler:
```csharp
case "flatten_all":
{
    var disable = TryGetValue(payload, "disableAfter") is bool b && b;
    AddLogEntry("INFO", $"Flatten all command received (disableAfter={disable})");
    _ = Task.Run(async () =>
    {
        try
        {
            AddLogEntry("INFO", "Executing flatten all...");
            var ok = await _managerService.FlattenAllAsync(disable, "Quantower UI flatten").ConfigureAwait(false);
            AddLogEntry(ok ? "INFO" : "WARN", ok ? "Flattened all accounts successfully" : "Flatten all reported issues");
        }
        catch (Exception ex)
        {
            AddLogEntry("ERROR", $"Flatten all failed: {ex.Message}");
        }
    });
    break;
}
```

### **Expected Result:**
✅ Logs will show:
- When flatten command is received
- When execution starts
- Success or failure with details

---

## 🔧 **FIX 5: Monitor Script File Output**

### **Issue:**
`monitor-plugin-loading.ps1` only displayed logs in console. User wanted logs written to file and cleared on each run (like `run-wails-dev-with-logs.ps1`).

### **Fix Applied:**
**File:** `scripts/monitor-plugin-loading.ps1`

1. Added `$LogFile` variable pointing to `C:\Documents\Dev\OfficialFuturesHedgebotv2.5QT\logs.txt`
2. Clear log file on each run
3. Write all output to both console AND file
4. Added timestamps and better formatting

### **Expected Result:**
✅ All plugin loading logs are now saved to `logs.txt` and cleared on each script run.

---

## 📋 **REMAINING ISSUES TO INVESTIGATE**

### **1. Duplicate Initial Trade**
**Status:** Not fixed yet - needs investigation

**Symptom:** First trade opens a duplicate hedge, then subsequent trades sync correctly 1:1.

**Likely Cause:** `SnapshotPositions()` is sending existing positions as new trades when the plugin starts.

**Next Steps:**
1. Check if `TryBuildPositionSnapshot()` is marking positions differently from `TryBuildTradeEnvelope()`
2. Verify bridge/MT5 can distinguish snapshots from new trades
3. Consider skipping snapshots entirely or adding a flag

### **2. Toggle Buttons**
**Status:** ✅ Actually working correctly

**Finding:** The toggle buttons ARE wired correctly:
- JS sends `update_trailing` command
- C# handles it in `HandleMsbCommand`
- Settings are updated in `TrailingElasticService`

**User's Issue:** Might be confusion about what the toggles do, or the trailing logic itself not working (see Fix 2).

---

## 🧪 **TESTING CHECKLIST**

After rebuilding and deploying:

- [ ] **MT5 Close Sync:** Close a position on MT5 manually → Verify Quantower position closes automatically
- [ ] **Quantower Close Sync:** Close a position in Quantower → Verify MT5 hedge closes automatically
- [ ] **DEMA-ATR Trailing:** Enable trailing, place trade, check logs for trailing update messages
- [ ] **Flatten Button:** Click flatten → Check logs for "Flatten all command received" and "Executing flatten all..."
- [ ] **Window Controls:** Verify buttons are removed from UI
- [ ] **Monitor Script:** Run script → Verify logs.txt is created and populated
- [ ] **Duplicate Trade:** Restart plugin with existing positions → Check if duplicates are created

---

## 📝 **BUILD & DEPLOY INSTRUCTIONS**

1. **Build the plugin:**
   ```powershell
   cd C:\Documents\Dev\OfficialFuturesHedgebotv2.5QT\MultiStratManagerRepo\Quantower
   dotnet build -c Release
   ```

2. **Deploy to Quantower:**
   ```powershell
   # Copy DLL and HTML to Quantower plugins folder
   Copy-Item "bin\Release\net8.0-windows\QuantowerMultiStratAddOn.dll" "C:\Quantower\Settings\Scripts\plug-ins\MultiStratQuantower\"
   Copy-Item "HTML\layout.html" "C:\Quantower\Settings\Scripts\plug-ins\MultiStratQuantower\"
   ```

3. **Restart Quantower**

4. **Monitor logs:**
   ```powershell
   cd C:\Documents\Dev\OfficialFuturesHedgebotv2.5QT
   .\scripts\monitor-plugin-loading.ps1
   ```

---

## 🎯 **EXPECTED OUTCOMES**

After these fixes:

1. ✅ **Closure Sync Works:** Positions close in sync between Quantower and MT5
2. ✅ **Trailing Diagnostics:** Logs show exactly why trailing is/isn't working
3. ✅ **Cleaner UI:** No non-functional window control buttons
4. ✅ **Better Logging:** Flatten button and all operations have detailed logs
5. ✅ **Persistent Logs:** All logs saved to file for analysis

---

## 📞 **NEXT STEPS**

1. **Test all fixes** using the checklist above
2. **Review trailing logs** to see why DEMA-ATR isn't activating
3. **Investigate duplicate initial trade** issue
4. **Report findings** and any remaining issues

---

**End of Document**

