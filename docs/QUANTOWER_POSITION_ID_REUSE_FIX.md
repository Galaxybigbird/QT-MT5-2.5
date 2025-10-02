# Quantower Position.Id Reuse Fix - 1:1 Close Correlation

## ?? The Problem

**Quantower REUSES the same Position.Id for multiple different positions!**

### Evidence from Logs

When the user opened and closed multiple positions, the logs showed:

1. **Position 1 opens:** 8bcddeb9626d47f09b3f543e6d197053_638938305650000000 (Position.Id: 8bcddeb9626d47f09b3f543e6d197053)
2. **Position 1 closes:** Uses tracked baseId 
3. **Position 2 opens:** 8bcddeb9626d47f09b3f543e6d197053_638938305670000000 (**SAME Position.Id!**)
4. **Position 2 closes:** Uses tracked baseId 
5. **Position 3 opens:** 8bcddeb9626d47f09b3f543e6d197053_638938305730000003 (**SAME Position.Id!**)
6. **Position 4 opens:** 8bcddeb9626d47f09b3f543e6d197053_638938306020000001 (**SAME Position.Id!**)
7. **Position 5 opens:** 8bcddeb9626d47f09b3f543e6d197053_638938306090000001 (**SAME Position.Id!**)

### The Issue

Our previous fix used a **dictionary** to track Position.Id -> baseId mappings. When multiple positions with the same Position.Id were opened, each new position **OVERWROTE** the previous baseId in the dictionary!

**Result:** When the user tried to close Positions 3 or 4, the system couldn't find their baseIds, so it created NEW MT5 hedge trades instead of closing the existing ones.

##  The Solution

**Use a QUEUE (FIFO) to track ALL baseIds for each Position.Id:**

Changed from: ConcurrentDictionary<string, string> _positionIdToBaseId
Changed to: ConcurrentDictionary<string, ConcurrentQueue<string>> _positionIdToBaseIds

### How It Works

1. **When a position opens:** Enqueue the new baseId to the END of the queue
2. **When a position closes:** Dequeue the OLDEST baseId from the FRONT of the queue (FIFO)
3. **If queue is empty:** Remove it from the dictionary

##  Expected Behavior

**Opening:**
- Open 2 QT positions  2 MT5 hedges open 

**Closing:**
- Close 2 QT positions  2 MT5 hedges close (using OLDEST baseIds first) 
- Close 1 QT position  1 MT5 hedge closes (using OLDEST baseId) 

**No more:**
-  Spurious MT5 hedge trades
-  BaseId mismatches due to Position.Id reuse

##  Testing Instructions

1. **Deploy to Quantower:** Copy MultiStratManagerRepo\Quantower\QuantowerMultiStratAddOn\bin\Release\net8.0-windows\* to your Quantower installation directory

2. **Test Scenario 1: Open 2, Close 2**
   - Open 2 QT positions  Verify 2 MT5 hedges open
   - Close 2 QT positions  Verify 2 MT5 hedges close (no spurious trades)

3. **Test Scenario 2: Open 2, Close 1**
   - Open 2 QT positions  Verify 2 MT5 hedges open
   - Close 1 QT position  Verify 1 MT5 hedge closes (1 remains open)

4. **Check logs for:**
   - "Tracked baseId mapping: ... (queue size: X)" when positions open
   - "Found tracked baseId for position ...: ... (remaining in queue: X)" when positions close
   - No spurious MT5 hedge trades

##  Summary

This fix solves the **Position.Id reuse problem** by tracking ALL baseIds for each Position.Id in a FIFO queue, ensuring that close events use the correct baseId even when Quantower reuses the same Position.Id for multiple positions. This maintains perfect 1:1 correlation between Quantower positions and MT5 hedge trades.
