# Quantower Plugin: Manual vs Automated Tasks

This document clarifies which tasks require manual testing vs automated code changes.

---

## 🧪 MANUAL TESTING REQUIRED (Cannot be automated)

### 1. Browser Bridge Connectivity Testing
**Why manual:** Requires interaction with Quantower's UI and browser console.

**What you'll do:**
- Open plugin in Quantower
- Open browser console (F12)
- Click buttons and verify console logs
- Check that commands reach C# backend
- Verify UI updates reflect backend state

**Time:** 1-2 hours  
**See:** `QUANTOWER_MANUAL_TESTING_GUIDE.md` - CRITICAL TEST 1

---

### 2. Window Controls Testing
**Why manual:** Requires visual verification of window behavior.

**What you'll do:**
- Click minimize/maximize/close buttons
- Verify window responds correctly
- Check for errors in Quantower logs

**Time:** 30 minutes  
**See:** `QUANTOWER_MANUAL_TESTING_GUIDE.md` - CRITICAL TEST 2

---

### 3. End-to-End Trade Flow Testing
**Why manual:** Requires real trading platforms and visual verification.

**What you'll do:**
- Place trades in Quantower
- Verify hedges appear on MT5
- Close positions and verify MT5 closes
- Test trailing stops with live price movement
- Test elastic hedging with profit changes
- Test risk limits by triggering them
- Test reconnection scenarios

**Time:** 4-6 hours  
**See:** `QUANTOWER_MANUAL_TESTING_GUIDE.md` - CRITICAL TEST 3 and TESTS 4-7

---

### 4. UI Responsiveness Testing
**Why manual:** Requires visual verification at different sizes.

**What you'll do:**
- Resize plugin panel to various sizes
- Verify no overlapping elements
- Test with multiple accounts
- Verify all controls remain accessible

**Time:** 30 minutes  
**See:** `QUANTOWER_MANUAL_TESTING_GUIDE.md` - TEST 8

---

### 5. Error Handling Testing
**Why manual:** Requires simulating error conditions.

**What you'll do:**
- Test with invalid bridge URLs
- Simulate network interruptions
- Verify error messages are user-friendly
- Verify no crashes or hangs

**Time:** 1 hour  
**See:** `QUANTOWER_MANUAL_TESTING_GUIDE.md` - TEST 9

---

## 💻 CODE CHANGES REQUIRED (Automated development)

### 1. Browser Bridge Fixes (IF NEEDED)
**When:** Only if manual testing reveals msb:// protocol doesn't work

**What to code:**
- Refactor `layout.html` send() function to use Quantower's native JS bridge
- Update `MultiStratPlugin.cs` HandleMsbCommand to use different communication mechanism
- May need to use `window.external` or postMessage API

**Files:**
- `MultiStratManagerRepo/Quantower/QuantowerMultiStratAddOn/HTML/layout.html`
- `MultiStratManagerRepo/Quantower/QuantowerMultiStratAddOn/MultiStratPlugin.cs`

**Time:** 2-4 hours (only if needed)

---

### 2. Window Controls Removal (IF NEEDED)
**When:** Only if manual testing shows Quantower doesn't support window control APIs

**What to code:**
- Remove window control buttons from HTML
- Remove window command handling from C#

**Files:**
- `MultiStratManagerRepo/Quantower/QuantowerMultiStratAddOn/HTML/layout.html` (lines 394-396, 716-723)
- `MultiStratManagerRepo/Quantower/QuantowerMultiStratAddOn/MultiStratPlugin.cs`

**Time:** 30 minutes (only if needed)

---

### 3. Bug Fixes from E2E Testing
**When:** After manual E2E testing reveals issues

**What to code:**
- Fix any bugs discovered during testing
- Timing issues, edge cases, state synchronization

**Files:** TBD based on bugs found

**Time:** 2-4 hours (estimated)

---

### 4. Deployment Script Creation
**When:** Before extensive testing to make deployment easier

**What to code:**
- Create PowerShell script to build and deploy plugin
- Automate copying DLL and HTML to Quantower directory

**File to create:**
- `scripts/deploy-quantower-plugin.ps1`

**Time:** 1 hour

---

### 5. Error Handling Enhancement
**When:** After initial testing, before production

**What to code:**
- Add try-catch blocks around browser invocations
- Add error toast notifications in UI
- Improve reconnection logic with exponential backoff

**Files:**
- `MultiStratManagerRepo/Quantower/QuantowerMultiStratAddOn/MultiStratPlugin.cs`
- `MultiStratManagerRepo/Quantower/QuantowerMultiStratAddOn/HTML/layout.html`
- `MultiStratManagerRepo/Quantower/QuantowerMultiStratAddOn/QuantowerBridgeService.cs`

**Time:** 2-3 hours

---

### 6. UI Polish
**When:** After core functionality works

**What to code:**
- Add loading spinners
- Add confirmation dialogs
- Improve button states
- Responsive layout fixes

**Files:**
- `MultiStratManagerRepo/Quantower/QuantowerMultiStratAddOn/HTML/layout.html`

**Time:** 2-3 hours

---

### 7. Performance Optimization
**When:** After everything works, before production

**What to code:**
- Reduce polling frequency
- Debounce setting updates
- Optimize rendering
- Cache status payloads

**Files:**
- `MultiStratManagerRepo/Quantower/QuantowerMultiStratAddOn/HTML/layout.html`
- `MultiStratManagerRepo/Quantower/QuantowerMultiStratAddOn/MultiStratPlugin.cs`

**Time:** 2-3 hours

---

## 📝 DOCUMENTATION WRITING (Manual writing)

### 1. Setup Guide
**What to write:**
- Installation prerequisites
- Step-by-step setup instructions
- Configuration walkthrough
- Screenshots

**File to create:**
- `docs/QUANTOWER_SETUP.md`

**Time:** 2 hours

---

### 2. Troubleshooting Guide
**What to write:**
- Common issues and solutions
- Error message explanations
- FAQ section

**File to create:**
- `docs/QUANTOWER_TROUBLESHOOTING.md`

**Time:** 1-2 hours

---

### 3. README Update
**What to write:**
- Add Quantower section
- Update architecture diagram
- Update feature list

**File to update:**
- `README.md`

**Time:** 30 minutes

---

## 🚫 NO WORK REQUIRED (Already done or not needed)

### MT5 EA
- ❌ No code changes needed
- ❌ No testing needed (already works with bridge)
- ❌ No documentation changes needed

### C++ DLL
- ❌ No code changes needed
- ❌ No rebuild needed
- ❌ No testing needed

### Proto Definitions
- ❌ No schema changes needed
- ❌ No regeneration needed

### Bridge (Go)
- ❌ No code changes needed (already supports Quantower)
- ❌ No testing needed beyond E2E

### Backend Services (C#)
- ✅ Already complete
- ❌ No changes needed (unless bugs found in testing)

---

## 📊 Time Breakdown

### Manual Testing: 7-10 hours
- Browser bridge testing: 1-2 hours
- Window controls testing: 0.5 hours
- E2E trade flow testing: 4-6 hours
- UI responsiveness testing: 0.5 hours
- Error handling testing: 1 hour

### Code Changes: 8-14 hours
- Browser bridge fixes (if needed): 2-4 hours
- Window controls removal (if needed): 0.5 hours
- Bug fixes from testing: 2-4 hours
- Deployment script: 1 hour
- Error handling enhancement: 2-3 hours
- UI polish: 2-3 hours
- Performance optimization: 2-3 hours

### Documentation: 3-4 hours
- Setup guide: 2 hours
- Troubleshooting guide: 1-2 hours
- README update: 0.5 hours

### **Total: 18-28 hours**

---

## 🎯 Critical Path

The **critical path** is the sequence of tasks that must be done in order:

1. **Manual: Browser Bridge Testing** (1-2 hours)
   - ↓ If fails ↓
2. **Code: Browser Bridge Fixes** (2-4 hours)
   - ↓ Retest ↓
3. **Manual: Window Controls Testing** (0.5 hours)
   - ↓ If fails ↓
4. **Code: Window Controls Removal** (0.5 hours)
5. **Code: Deployment Script** (1 hour) - Makes next steps easier
6. **Manual: E2E Testing** (4-6 hours) - Will reveal bugs
7. **Code: Bug Fixes** (2-4 hours)
8. **Code: Error Handling** (2-3 hours)
9. **Manual: Final Testing** (1-2 hours)
10. **Documentation** (3-4 hours)

**Minimum Critical Path:** 14-22 hours  
**With Optional Polish:** 18-28 hours

---

## 🔄 Iterative Process

The work follows this pattern:

```
Manual Test → Find Issues → Code Fixes → Manual Retest → Repeat
```

**Example:**
1. Manual test browser bridge
2. Discover msb:// doesn't work
3. Code fix to use window.external
4. Manual retest browser bridge
5. Discover new issue with status updates
6. Code fix for status updates
7. Manual retest
8. Success! Move to next test

---

## 💡 Key Insights

### What's Already Done (85%)
- All backend C# services
- All UI controls and layout
- All business logic (risk, trailing, elastic)
- All gRPC communication
- All trade mapping

### What Remains (15%)
- **Testing** to verify it works in Quantower
- **Bug fixes** from testing
- **Polish** for production quality
- **Documentation** for users

### Why No MT5 Work?
The MT5 EA and C++ DLL are **platform-agnostic**. They receive trade messages via gRPC and don't care about the source. The bridge handles all platform-specific translation, so Quantower trades look identical to NinjaTrader trades from MT5's perspective.

---

## 📋 Quick Reference

**Need to test manually:**
- Browser bridge connectivity
- Window controls
- End-to-end trade flow
- UI responsiveness
- Error handling

**Need to code:**
- Deployment script (always)
- Browser bridge fixes (if test fails)
- Window controls removal (if test fails)
- Bug fixes (based on testing)
- Error handling enhancement
- UI polish (optional)
- Performance optimization (optional)

**Need to write:**
- Setup guide
- Troubleshooting guide
- README updates

**Don't need to touch:**
- MT5 EA
- C++ DLL
- Proto definitions
- Bridge (Go)
- Backend services (unless bugs found)

