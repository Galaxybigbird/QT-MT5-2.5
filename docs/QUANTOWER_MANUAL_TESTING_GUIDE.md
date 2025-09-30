# Quantower Plugin Manual Testing Guide

This document provides step-by-step manual testing procedures for the Quantower Multi-Strat plugin.

---

## Prerequisites

- [ ] Quantower installed (v1.144.12 or later)
- [ ] .NET 8 SDK installed
- [ ] Bridge application running (`BridgeApp/BridgeApp.exe`)
- [ ] MT5 terminal with ACHedgeMaster_gRPC EA running
- [ ] Demo/sim accounts configured in both Quantower and MT5

---

## Test Environment Setup

### 1. Build the Plugin

```bash
cd MultiStratManagerRepo/Quantower/QuantowerMultiStratAddOn
dotnet build -c Release
```

### 2. Deploy to Quantower

**Manual Deployment:**
```powershell
# Copy DLL
Copy-Item "bin\Release\net8.0-windows\QuantowerMultiStratAddOn.dll" "$env:APPDATA\Quantower\Settings\Scripts\plugins\" -Force

# Copy HTML template
New-Item -ItemType Directory -Force -Path "$env:APPDATA\Quantower\Settings\Scripts\plugins\HTML"
Copy-Item "HTML\layout.html" "$env:APPDATA\Quantower\Settings\Scripts\plugins\HTML\" -Force
```

### 3. Launch Components

1. **Start Bridge:** Run `BridgeApp/BridgeApp.exe`
2. **Start MT5:** Launch MT5 terminal with EA attached to chart
3. **Start Quantower:** Launch Quantower platform
4. **Open Plugin:** In Quantower, go to Misc → Multi-Strat Bridge

---

## CRITICAL TEST 1: Browser Bridge Connectivity

**Purpose:** Verify JavaScript ↔ C# communication works in Quantower's browser host.

### Test Steps:

1. **Open Browser Console:**
   - Right-click in plugin panel → Inspect Element (if available)
   - Or check Quantower logs for browser errors

2. **Test Connect Command:**
   - [ ] Enter bridge URL: `http://localhost:50051`
   - [ ] Click "Connect" button
   - [ ] **VERIFY:** Status pill changes from "Disconnected" to "Connecting" or "Connected"
   - [ ] **VERIFY:** Console shows: `[MSB] Sending command: connect`
   - [ ] **VERIFY:** No JavaScript errors in console

3. **Test Status Updates:**
   - [ ] **VERIFY:** Status updates automatically (check timestamp changes)
   - [ ] **VERIFY:** Account dropdown populates with accounts
   - [ ] **VERIFY:** Balance displays update

4. **Test Account Selection:**
   - [ ] Select an account from dropdown
   - [ ] **VERIFY:** Console shows: `[MSB] Sending command: select_account`
   - [ ] **VERIFY:** Account becomes enabled in backend

5. **Test Disconnect:**
   - [ ] Click "Disconnect" button
   - [ ] **VERIFY:** Status changes to "Disconnected"
   - [ ] **VERIFY:** Account dropdown becomes disabled

### Expected Results:
- ✅ All commands trigger console logs
- ✅ Status updates reflect backend state
- ✅ No JavaScript errors
- ✅ UI responds to all button clicks

### If Test Fails:
- **Symptom:** No console logs when clicking buttons
  - **Cause:** msb:// protocol not working in Quantower's CEF browser
  - **Fix Required:** Refactor to use Quantower's native JS bridge API
  - **Files to modify:** `layout.html` (send function), `MultiStratPlugin.cs` (HandleMsbCommand)

- **Symptom:** Console errors about navigation
  - **Cause:** Iframe navigation blocked by Quantower
  - **Fix Required:** Use alternative bridge mechanism (window.external or postMessage)

---

## CRITICAL TEST 2: Window Controls

**Purpose:** Verify window management buttons work with Quantower.

### Test Steps:

1. **Test Minimize:**
   - [ ] Click minimize button (–)
   - [ ] **VERIFY:** Panel minimizes or appropriate action occurs
   - [ ] **CHECK:** Quantower logs for errors

2. **Test Maximize:**
   - [ ] Click maximize button (▢)
   - [ ] **VERIFY:** Panel maximizes or appropriate action occurs

3. **Test Close:**
   - [ ] Click close button (×)
   - [ ] **VERIFY:** Panel closes gracefully
   - [ ] Reopen panel
   - [ ] **VERIFY:** State is preserved or properly reset

### Expected Results:
- ✅ Window controls work as expected
- OR ✅ Graceful failure with no errors

### If Test Fails:
- **Symptom:** Buttons do nothing
  - **Cause:** Quantower doesn't expose window control APIs to plugins
  - **Fix Required:** Remove window control buttons from UI
  - **Files to modify:** `layout.html` (remove buttons), `MultiStratPlugin.cs` (remove window command handling)

---

## CRITICAL TEST 3: End-to-End Trade Flow

**Purpose:** Verify complete trade lifecycle from Quantower to MT5.

### Prerequisites:
- [ ] Bridge connected and showing "Connected" status
- [ ] Account selected in plugin
- [ ] MT5 EA showing "Connected" status
- [ ] Demo accounts with sufficient margin

### Test 3A: Basic Trade Execution

1. **Place Trade in Quantower:**
   - [ ] Open a position (e.g., Buy 1 lot ES)
   - [ ] Note the Quantower Trade ID and Position ID

2. **Verify Bridge Reception:**
   - [ ] Check Bridge logs: `logs/unified-*.jsonl`
   - [ ] **VERIFY:** Log entry with `source: "qt"` and `origin_platform: "quantower"`
   - [ ] **VERIFY:** Trade ID matches Quantower's Trade.Id

3. **Verify MT5 Hedge:**
   - [ ] Check MT5 terminal
   - [ ] **VERIFY:** Opposite position opened (Sell 1 lot ES)
   - [ ] **VERIFY:** MT5 comment contains Quantower trade reference
   - [ ] **VERIFY:** MT5 EA logs show trade received

4. **Close Position in Quantower:**
   - [ ] Close the Quantower position
   - [ ] **VERIFY:** MT5 hedge closes automatically
   - [ ] **VERIFY:** Bridge logs show closure notification

### Expected Results:
- ✅ Quantower trade → Bridge → MT5 hedge (< 500ms latency)
- ✅ Trade IDs properly tracked
- ✅ Hedge closes when Quantower position closes

### If Test Fails:
- **Check Bridge logs** for errors
- **Check MT5 EA logs** for reception issues
- **Verify gRPC connectivity** between all components

---

## TEST 4: Risk Management Features

### Test 4A: Daily Take Profit Limit

1. **Configure Risk Settings:**
   - [ ] Set Daily Take Profit: `$500.00`
   - [ ] Enable "Auto Flatten on Limit"
   - [ ] Click "Apply Risk Settings"

2. **Trigger Limit:**
   - [ ] Place trades until daily P&L reaches $500
   - [ ] **VERIFY:** All positions automatically flatten
   - [ ] **VERIFY:** Plugin shows "Limit Reached" or similar status

### Test 4B: Daily Loss Limit

1. **Configure Risk Settings:**
   - [ ] Set Daily Loss Limit: `$200.00`
   - [ ] Enable "Disable on Limit"
   - [ ] Click "Apply Risk Settings"

2. **Trigger Limit:**
   - [ ] Place trades until daily P&L reaches -$200
   - [ ] **VERIFY:** All positions flatten
   - [ ] **VERIFY:** Account becomes disabled
   - [ ] **VERIFY:** Cannot place new trades

### Test 4C: Reset Daily Tracking

1. **Reset:**
   - [ ] Click "Reset Daily" button
   - [ ] **VERIFY:** Daily P&L resets to $0
   - [ ] **VERIFY:** Account re-enables if previously disabled

---

## TEST 5: Trailing Stop Features

### Test 5A: Basic Trailing Stop

1. **Configure Trailing:**
   - [ ] Enable "Enable Trailing Stop"
   - [ ] Set Activation: "Dollars" / `$100.00`
   - [ ] Set Stop Distance: "Dollars" / `$50.00`
   - [ ] Click "Apply Trailing Settings"

2. **Test Trailing:**
   - [ ] Open position
   - [ ] Move position into $100+ profit
   - [ ] **VERIFY:** Bridge logs show trailing update sent
   - [ ] **VERIFY:** MT5 stop loss moves with price
   - [ ] Let price retrace
   - [ ] **VERIFY:** Position closes at trailing stop

### Test 5B: DEMA/ATR Trailing

1. **Configure DEMA/ATR:**
   - [ ] Enable "Enable DEMA/ATR Trailing"
   - [ ] Set ATR Period: `14`
   - [ ] Set DEMA Period: `20`
   - [ ] Set Multiplier: `2.0`
   - [ ] Click "Apply Trailing Settings"

2. **Test Dynamic Trailing:**
   - [ ] Open position
   - [ ] Move into profit
   - [ ] **VERIFY:** Stop distance adjusts based on ATR
   - [ ] **VERIFY:** Stop follows DEMA line

---

## TEST 6: Elastic Hedging Features

### Test 6A: Basic Elastic Hedging

1. **Configure Elastic:**
   - [ ] Enable "Enable Elastic Hedging"
   - [ ] Set Trigger: "Dollars" / `$200.00`
   - [ ] Set Increment: "Dollars" / `$100.00`
   - [ ] Click "Apply Trailing Settings"

2. **Test Elastic:**
   - [ ] Open position
   - [ ] Move position into $200+ profit
   - [ ] **VERIFY:** Bridge logs show elastic update
   - [ ] **VERIFY:** MT5 hedge size increases
   - [ ] Continue into $300+ profit
   - [ ] **VERIFY:** Hedge increases again

---

## TEST 7: Reconnection & Recovery

### Test 7A: Bridge Reconnection

1. **Disconnect Bridge:**
   - [ ] Stop BridgeApp
   - [ ] **VERIFY:** Plugin shows "Disconnected" status
   - [ ] **VERIFY:** UI shows reconnection attempts

2. **Reconnect:**
   - [ ] Restart BridgeApp
   - [ ] **VERIFY:** Plugin automatically reconnects
   - [ ] **VERIFY:** Account state recovers
   - [ ] **VERIFY:** Existing positions still tracked

### Test 7B: Quantower Restart

1. **Restart Quantower:**
   - [ ] Close Quantower (with positions open)
   - [ ] Restart Quantower
   - [ ] Reopen plugin
   - [ ] **VERIFY:** Plugin reconnects to bridge
   - [ ] **VERIFY:** Existing positions are tracked
   - [ ] **VERIFY:** Settings are preserved

---

## TEST 8: UI Responsiveness

### Test 8A: Window Resizing

1. **Resize Panel:**
   - [ ] Resize panel to minimum width
   - [ ] **VERIFY:** UI remains usable (no overlapping elements)
   - [ ] Resize to maximum width
   - [ ] **VERIFY:** UI scales appropriately

### Test 8B: Multiple Accounts

1. **Test with Multiple Accounts:**
   - [ ] Connect with 3+ accounts
   - [ ] **VERIFY:** All accounts appear in dropdown
   - [ ] Switch between accounts
   - [ ] **VERIFY:** Balance updates correctly
   - [ ] **VERIFY:** Position tracking switches

---

## TEST 9: Error Handling

### Test 9A: Invalid Bridge URL

1. **Test Invalid URL:**
   - [ ] Enter invalid URL: `http://invalid:99999`
   - [ ] Click "Connect"
   - [ ] **VERIFY:** User-friendly error message displayed
   - [ ] **VERIFY:** No crash or hang

### Test 9B: Network Interruption

1. **Simulate Network Issue:**
   - [ ] Connect successfully
   - [ ] Disable network adapter
   - [ ] **VERIFY:** Plugin detects disconnection
   - [ ] Re-enable network
   - [ ] **VERIFY:** Plugin reconnects automatically

---

## Test Results Template

Copy this template for each test session:

```
## Test Session: [DATE]
**Tester:** [NAME]
**Quantower Version:** [VERSION]
**Bridge Version:** [COMMIT HASH]
**MT5 Build:** [BUILD NUMBER]

### Test Results:
- [ ] CRITICAL TEST 1: Browser Bridge Connectivity - PASS/FAIL
- [ ] CRITICAL TEST 2: Window Controls - PASS/FAIL
- [ ] CRITICAL TEST 3: End-to-End Trade Flow - PASS/FAIL
- [ ] TEST 4: Risk Management - PASS/FAIL
- [ ] TEST 5: Trailing Stops - PASS/FAIL
- [ ] TEST 6: Elastic Hedging - PASS/FAIL
- [ ] TEST 7: Reconnection - PASS/FAIL
- [ ] TEST 8: UI Responsiveness - PASS/FAIL
- [ ] TEST 9: Error Handling - PASS/FAIL

### Issues Found:
1. [Description]
2. [Description]

### Notes:
[Any additional observations]
```

---

## Success Criteria

**Minimum for Production:**
- ✅ All CRITICAL tests pass
- ✅ At least 80% of other tests pass
- ✅ No data loss or corruption
- ✅ No crashes or hangs

**Ideal for Production:**
- ✅ All tests pass
- ✅ Performance meets expectations (< 500ms trade latency)
- ✅ UI is responsive and intuitive
- ✅ Error messages are clear and actionable

