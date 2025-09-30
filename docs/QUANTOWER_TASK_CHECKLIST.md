# Quantower Plugin Completion Checklist

Quick reference for remaining tasks. See `QUANTOWER_MANUAL_TESTING_GUIDE.md` for detailed testing procedures.

---

## 🔴 PRIORITY 1: Critical for Functionality (7-14 hours)

### Task 1: Verify Browser Bridge Connectivity (2-4 hours)
**Status:** NOT STARTED  
**Complexity:** 6/10  
**Type:** MANUAL TESTING + POTENTIAL CODE FIXES

**What to do:**
1. Deploy plugin to Quantower
2. Open browser console (F12 or Inspect Element)
3. Test all msb:// commands (connect, disconnect, select_account, etc.)
4. Verify console logs appear for each command
5. Verify UI updates reflect backend state

**Files that may need changes:**
- `MultiStratManagerRepo/Quantower/QuantowerMultiStratAddOn/MultiStratPlugin.cs` (HandleMsbCommand method)
- `MultiStratManagerRepo/Quantower/QuantowerMultiStratAddOn/HTML/layout.html` (send function)

**Success criteria:**
- [ ] All button clicks trigger console logs
- [ ] Status updates work automatically
- [ ] No JavaScript errors in console
- [ ] Commands reach C# backend

**If it fails:**
- msb:// protocol may not work in Quantower's CEF browser
- Need to refactor to use Quantower's native JS bridge API
- Check Quantower documentation for `window.external` or similar APIs

---

### Task 2: Test Window Controls (1-2 hours)
**Status:** NOT STARTED  
**Complexity:** 3/10  
**Type:** MANUAL TESTING + POTENTIAL CODE REMOVAL

**What to do:**
1. Click minimize button (–)
2. Click maximize button (▢)
3. Click close button (×)
4. Verify each action works or fails gracefully

**Files that may need changes:**
- `MultiStratManagerRepo/Quantower/QuantowerMultiStratAddOn/HTML/layout.html` (lines 394-396, 716-723)
- `MultiStratManagerRepo/Quantower/QuantowerMultiStratAddOn/MultiStratPlugin.cs` (window command handling)

**Success criteria:**
- [ ] Window controls work as expected
- OR [ ] Buttons removed if Quantower doesn't support them

**If it fails:**
- Remove window control buttons from HTML
- Remove window command handling from C#
- Rely on Quantower's native window chrome

---

### Task 3: End-to-End Integration Testing (4-8 hours)
**Status:** NOT STARTED  
**Complexity:** 7/10  
**Type:** MANUAL TESTING (will reveal bugs to fix)

**What to do:**
1. **Basic Trade Flow:**
   - Place trade in Quantower → Verify hedge on MT5
   - Close trade in Quantower → Verify MT5 hedge closes
   
2. **Trailing Stops:**
   - Enable trailing → Move into profit → Verify stop moves
   
3. **Elastic Hedging:**
   - Enable elastic → Move into profit → Verify hedge adjusts
   
4. **Risk Limits:**
   - Set daily TP/SL → Trigger limit → Verify auto-flatten
   
5. **Reconnection:**
   - Disconnect bridge → Reconnect → Verify state recovery

**See:** `docs/QUANTOWER_MANUAL_TESTING_GUIDE.md` for detailed test procedures

**Success criteria:**
- [ ] Trade flow works: Quantower → Bridge → MT5
- [ ] Trailing stops adjust correctly
- [ ] Elastic hedging increases hedge size
- [ ] Risk limits trigger auto-flatten
- [ ] Reconnection recovers state

**Expected bugs to fix:**
- Timing issues in status updates
- Edge cases in trade mapping
- UI state synchronization issues

---

## 🟡 PRIORITY 2: Important for Production (4-6 hours)

### Task 4: Create Deployment Script (2-3 hours)
**Status:** NOT STARTED  
**Complexity:** 2/10  
**Type:** CODE - NEW FILE

**What to do:**
1. Create `scripts/deploy-quantower-plugin.ps1`
2. Script should:
   - Build plugin in Release mode
   - Copy DLL to Quantower plugins directory
   - Copy HTML template
   - Provide instructions to restart Quantower

**File to create:**
```powershell
# scripts/deploy-quantower-plugin.ps1
$QuantowerPluginPath = "$env:APPDATA\Quantower\Settings\Scripts\plugins"
$ProjectPath = "MultiStratManagerRepo\Quantower\QuantowerMultiStratAddOn"

# Build
dotnet build "$ProjectPath\QuantowerMultiStratAddOn.csproj" -c Release

# Deploy
Copy-Item "$ProjectPath\bin\Release\net8.0-windows\*.dll" $QuantowerPluginPath -Force
New-Item -ItemType Directory -Force -Path "$QuantowerPluginPath\HTML"
Copy-Item "$ProjectPath\HTML\layout.html" "$QuantowerPluginPath\HTML\" -Force

Write-Host "✅ Deployed to Quantower. Restart Quantower to load plugin."
```

**Success criteria:**
- [ ] Script builds plugin successfully
- [ ] Script copies files to correct location
- [ ] Plugin loads after Quantower restart

---

### Task 5: Enhance Error Handling & Logging (2-3 hours)
**Status:** NOT STARTED  
**Complexity:** 4/10  
**Type:** CODE - MODIFICATIONS

**What to do:**
1. Add try-catch blocks around all browser invocations
2. Add user-friendly error toasts in UI
3. Improve reconnection logic with exponential backoff
4. Better logging of browser console errors

**Files to modify:**
- `MultiStratManagerRepo/Quantower/QuantowerMultiStratAddOn/MultiStratPlugin.cs`
  - Wrap all `TryBrowserInvokeJs` calls in try-catch
  - Add error logging
  
- `MultiStratManagerRepo/Quantower/QuantowerMultiStratAddOn/HTML/layout.html`
  - Add error toast notification function
  - Display errors to user
  
- `MultiStratManagerRepo/Quantower/QuantowerMultiStratAddOn/QuantowerBridgeService.cs`
  - Enhance reconnection logic

**Success criteria:**
- [ ] No unhandled exceptions
- [ ] User sees friendly error messages
- [ ] Automatic reconnection works reliably
- [ ] Errors are logged for debugging

---

## 🟢 PRIORITY 3: Nice to Have (7-10 hours)

### Task 6: Create Documentation (3-4 hours)
**Status:** NOT STARTED  
**Complexity:** 3/10  
**Type:** DOCUMENTATION

**What to do:**
1. Create `docs/QUANTOWER_SETUP.md` - Installation guide
2. Create `docs/QUANTOWER_TROUBLESHOOTING.md` - Common issues
3. Update `README.md` - Add Quantower section

**Content needed:**
- Prerequisites (Quantower version, .NET 8)
- Installation steps with screenshots
- Configuration walkthrough
- Troubleshooting common issues
- FAQ section

**Success criteria:**
- [ ] New user can install and configure plugin
- [ ] Common issues have documented solutions
- [ ] README reflects Quantower support

---

### Task 7: UI Polish (2-3 hours)
**Status:** NOT STARTED  
**Complexity:** 4/10  
**Type:** CODE - MODIFICATIONS

**What to do:**
1. Add loading spinners for async operations
2. Add confirmation dialogs for destructive actions
3. Improve button disabled states
4. Test responsive layout at different sizes

**File to modify:**
- `MultiStratManagerRepo/Quantower/QuantowerMultiStratAddOn/HTML/layout.html`

**Changes:**
- Add CSS for loading spinner
- Add JavaScript for confirmation dialogs
- Improve button state management
- Test at 720x540, 1024x768, 1920x1080

**Success criteria:**
- [ ] Loading states visible during operations
- [ ] Confirmation before flatten all
- [ ] Buttons clearly show enabled/disabled
- [ ] UI works at all tested sizes

---

### Task 8: Performance Optimization (2-3 hours)
**Status:** NOT STARTED  
**Complexity:** 5/10  
**Type:** CODE - MODIFICATIONS

**What to do:**
1. Reduce status polling frequency (currently 2s)
2. Debounce trailing/elastic setting updates
3. Optimize account list rendering
4. Reduce unnecessary PushStatusToBrowser calls

**Files to modify:**
- `MultiStratManagerRepo/Quantower/QuantowerMultiStratAddOn/HTML/layout.html`
  - Change polling interval from 2s to 5s
  - Add debounce to setting inputs
  
- `MultiStratManagerRepo/Quantower/QuantowerMultiStratAddOn/MultiStratPlugin.cs`
  - Only call PushStatusToBrowser when state actually changes
  - Cache status payload to avoid redundant serialization

**Success criteria:**
- [ ] Reduced CPU usage
- [ ] Fewer network calls
- [ ] UI still feels responsive
- [ ] No noticeable lag

---

## 📊 Progress Tracking

### Overall Status
- **Total Tasks:** 8
- **Completed:** 0
- **In Progress:** 0
- **Not Started:** 8
- **Estimated Total Effort:** 18-30 hours

### By Priority
- **Priority 1 (Critical):** 0/3 complete
- **Priority 2 (Important):** 0/2 complete
- **Priority 3 (Nice to Have):** 0/3 complete

---

## 🎯 Recommended Order

1. **Task 1** - Browser Bridge (MUST DO FIRST)
2. **Task 2** - Window Controls (Quick win)
3. **Task 4** - Deployment Script (Makes testing easier)
4. **Task 3** - E2E Testing (Will reveal bugs)
5. **Task 5** - Error Handling (Fix bugs found in Task 3)
6. **Task 6** - Documentation (While fresh in mind)
7. **Task 7** - UI Polish (Final touches)
8. **Task 8** - Performance (Last optimization)

---

## ✅ Definition of Done

**For each task:**
- [ ] Code changes committed
- [ ] Manual testing completed
- [ ] No regressions introduced
- [ ] Documentation updated (if applicable)

**For overall project:**
- [ ] All Priority 1 tasks complete
- [ ] All Priority 2 tasks complete
- [ ] At least 2/3 Priority 3 tasks complete
- [ ] All manual tests pass (see QUANTOWER_MANUAL_TESTING_GUIDE.md)
- [ ] Plugin deployed and working on clean machine
- [ ] Documentation complete

---

## 🚫 What Does NOT Need to Be Done

### MT5 EA / C++ DLL
- ❌ No changes to `MT5/ACHedgeMaster_gRPC.mq5`
- ❌ No changes to `MT5/cpp-grpc-client/` C++ DLL
- ❌ No proto schema changes
- ❌ No bridge changes for Quantower support

**Why:** The MT5 side is platform-agnostic. It receives trade messages via gRPC and doesn't care whether they come from NinjaTrader or Quantower. The bridge handles all platform-specific translation.

---

## 📝 Notes

- **Browser bridge is the critical path** - If Task 1 fails, it may require significant refactoring
- **Most tasks are testing and polish** - The core functionality is already implemented
- **No major technical blockers identified** - All remaining work is achievable
- **MT5 compatibility is confirmed** - No changes needed on MT5 side

---

## 🆘 If You Get Stuck

1. **Browser bridge not working?**
   - Check Quantower documentation for plugin JS bridge APIs
   - Look for `window.external` or similar mechanisms
   - Consider using postMessage instead of iframe navigation

2. **Can't find Quantower plugin directory?**
   - Check `%APPDATA%\Quantower\`
   - Look in Quantower settings for plugin paths
   - Check Quantower documentation

3. **Tests failing?**
   - Check all three log sources: Quantower, Bridge, MT5
   - Verify gRPC connectivity with `telnet localhost 50051`
   - Enable verbose logging in all components

4. **Need help?**
   - Review `docs/QUANTOWER_MANUAL_TESTING_GUIDE.md`
   - Check existing NinjaTrader implementation for reference
   - Review Quantower API documentation

