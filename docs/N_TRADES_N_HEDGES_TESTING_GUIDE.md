# Testing Guide: n Trades → n Hedges Implementation

## Quick Start

### 1. Build the Projects

**Go Bridge:**
```bash
cd BridgeApp
go build
```

**C# Quantower Addon:**
```bash
cd MultiStratManagerRepo/Quantower
dotnet build --configuration Release
```

### 2. Deploy

**Bridge:**
- Run `BridgeApp.exe` (or use Wails dev mode)
- Verify gRPC server starts on port 50051

**Quantower Addon:**
- Copy DLLs from `bin/Release` to Quantower's `Vendor` folder
- Restart Quantower
- Enable the addon in Quantower settings

---

## Test Scenarios

### ✅ Test 1: Single Contract Trade

**Steps:**
1. Open 1 contract on Quantower (e.g., NQ)
2. Check MT5 - should see 1 hedge position

**Expected Logs:**

**Bridge (Go):**
```
gRPC: Received trade submission - ID: trade-123, Action: buy, Quantity: 1.00
gRPC: Trade processed successfully - ID: trade-123
```

**Quantower Addon (C#):**
```
[DEBUG] Stored initial quantity 1 for baseId position-abc
[INFO] Starting tracking for position position-abc
```

**MT5 EA:**
```
ACHM: Opening hedge for base_id position-abc, volume 1.0
```

**Closure:**
1. Close the position in Quantower
2. Check MT5 - hedge should close

**Expected Logs:**
```
[INFO] Quantower position closed (position-abc) -> notifying bridge (closing 1 hedge(s))
gRPC: Closing 1 MT5 ticket(s) for BaseID position-abc
```

---

### ✅ Test 2: Multi-Contract Trade (3 Contracts)

**Steps:**
1. Open 3 contracts on Quantower in a single order
2. Check MT5 - should see **3 separate hedge positions** with same base_id

**Expected Logs:**

**Bridge (Go):**
```
gRPC: Received trade submission - ID: trade-456, Action: buy, Quantity: 3.00
gRPC: Splitting trade trade-456 (base_id=position-xyz) into 3 individual hedges
gRPC: Enqueued split trade trade-456-1 (contract 1/3) for base_id=position-xyz
gRPC: Enqueued split trade trade-456-2 (contract 2/3) for base_id=position-xyz
gRPC: Enqueued split trade trade-456-3 (contract 3/3) for base_id=position-xyz
gRPC: Successfully split and enqueued 3 hedges for trade trade-456
```

**Quantower Addon (C#):**
```
[DEBUG] Stored initial quantity 3 for baseId position-xyz
[INFO] Starting tracking for position position-xyz
```

**MT5 EA:**
```
ACHM: Opening hedge for base_id position-xyz, volume 1.0 (ticket 12345)
ACHM: Opening hedge for base_id position-xyz, volume 1.0 (ticket 12346)
ACHM: Opening hedge for base_id position-xyz, volume 1.0 (ticket 12347)
```

**Verify in MT5:**
- Open "Trade" tab
- Should see 3 positions with same comment (base_id)
- Each position: 1.0 lots

**Closure:**
1. Close the position in Quantower (all 3 contracts)
2. Check MT5 - all 3 hedges should close

**Expected Logs:**
```
[DEBUG] Retrieved tracked quantity 3 for position position-xyz
[INFO] Quantower position closed (position-xyz) -> notifying bridge (closing 3 hedge(s))
gRPC: Closing 3 MT5 ticket(s) for BaseID position-xyz
gRPC: Enqueued CLOSE_HEDGE for BaseID position-xyz using 3 ticket(s)
```

**MT5 EA:**
```
ACHM_CLOSURE: Successfully closed hedge position #12345 for base_id: position-xyz
ACHM_CLOSURE: Successfully closed hedge position #12346 for base_id: position-xyz
ACHM_CLOSURE: Successfully closed hedge position #12347 for base_id: position-xyz
```

---

### ✅ Test 3: Multiple Separate Positions

**Steps:**
1. Open 1 contract on NQ → Position A
2. Open 1 contract on NQ → Position B (separate order)
3. Check MT5 - should see 2 hedges with **different** base_ids

**Expected:**
- Position A: `base_id=position-aaa`, 1 hedge
- Position B: `base_id=position-bbb`, 1 hedge

**Closure:**
1. Close Position A → Only hedge A closes
2. Close Position B → Only hedge B closes

---

### ✅ Test 4: Mixed Scenario

**Steps:**
1. Open 2 contracts on NQ → Position A (2 hedges)
2. Open 3 contracts on ES → Position B (3 hedges)
3. Open 1 contract on NQ → Position C (1 hedge)

**Expected MT5 State:**
- 6 total hedge positions
- Position A: 2 hedges (same base_id)
- Position B: 3 hedges (same base_id)
- Position C: 1 hedge (unique base_id)

**Closure:**
1. Close Position A → 2 hedges close
2. Close Position B → 3 hedges close
3. Close Position C → 1 hedge closes

---

## Log Monitoring

### Key Log Messages to Watch

**✅ Splitting Enabled:**
```
gRPC: Splitting trade {id} (base_id={base_id}) into {N} individual hedges
```

**✅ Quantity Tracked:**
```
[DEBUG] Stored initial quantity {N} for baseId {base_id}
```

**✅ Closure with Correct Count:**
```
[INFO] Quantower position closed ({base_id}) -> notifying bridge (closing {N} hedge(s))
gRPC: Closing {N} MT5 ticket(s) for BaseID {base_id}
```

### Log Locations

**Bridge (Go):**
- Console output
- `logs/unified-*.jsonl` (if logging enabled)

**Quantower Addon (C#):**
- Quantower's log viewer
- `%APPDATA%/Quantower/Logs/`

**MT5 EA:**
- MT5 "Experts" tab
- MT5 log files in `MQL5/Logs/`

---

## Troubleshooting

### Issue: Only 1 hedge created for multi-contract trade

**Check:**
1. Bridge logs - is splitting happening?
   - Look for "Splitting trade" message
   - If missing, check if `enqueueTradeWithSplit` was modified correctly

2. Verify quantity in trade message:
   ```
   gRPC: Received trade submission - ID: X, Action: buy, Quantity: 3.00
   ```
   - If Quantity is 1.00, Quantower may be sending separate trade events

### Issue: Wrong number of hedges closed

**Check:**
1. Addon logs - is quantity tracked?
   ```
   [DEBUG] Stored initial quantity {N} for baseId {base_id}
   ```

2. Closure logs - is correct quantity retrieved?
   ```
   [DEBUG] Retrieved tracked quantity {N} for position {base_id}
   ```

3. Bridge logs - is correct quantity used?
   ```
   gRPC: Closing {N} MT5 ticket(s) for BaseID {base_id}
   ```

### Issue: Hedges not closing at all

**Check:**
1. Verify `closed_hedge_quantity` in closure message
2. Check if tickets are in the pool:
   ```
   gRPC: No tracked MT5 tickets remain for BaseID {base_id}
   ```
3. Verify MT5 EA is receiving CLOSE_HEDGE messages

---

## Success Criteria

✅ **Single contract trades work as before**  
✅ **Multi-contract trades create N separate hedges**  
✅ **All hedges share the same base_id**  
✅ **Closures close the correct number of hedges**  
✅ **No memory leaks (tracking cleaned up)**  
✅ **Logs show correct splitting and closure counts**

---

## Performance Notes

- **Splitting overhead:** Minimal - just creates N copies of the trade struct
- **Memory:** Each position tracks one integer (initial quantity)
- **Cleanup:** Automatic when position closes or tracking stops
- **Network:** N separate gRPC messages for N contracts (expected)

---

## Rollback

If issues occur, revert to previous behavior:

1. **Quick fix:** Set quantity to 1 in bridge:
   ```go
   // In enqueueTradeWithSplit
   quantity := 1  // Force single hedge
   ```

2. **Full rollback:** Revert commits in reverse order (see main doc)

---

## Next Steps After Testing

1. **Monitor production** for 24-48 hours
2. **Collect metrics:**
   - Average contracts per trade
   - Split trade frequency
   - Closure success rate
3. **Consider enhancements:**
   - Partial closure support
   - Configurable splitting (on/off per account)
   - Performance optimizations for large quantities

---

## Support

If you encounter issues:
1. Collect logs from all 3 components (Bridge, Addon, EA)
2. Note the exact scenario (contracts, symbols, timing)
3. Check if it's reproducible
4. Review the implementation doc for architecture details

