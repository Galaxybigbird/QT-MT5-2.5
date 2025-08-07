using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript; // For LogLevel
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;

namespace NinjaTrader.NinjaScript.AddOns.MultiStratManagerLogic
{
    public class SLTPRemovalLogic
    {
        private class PendingSLTPRemoval
        {
            public Execution EntryFillExecution { get; set; }
            public DispatcherTimer RemovalTimer { get; set; }
        }

        private List<PendingSLTPRemoval> pendingRemovals = new List<PendingSLTPRemoval>();
        private readonly object _pendingRemovalsLock = new object();

        private HashSet<string> processedEntryOrderIds = new HashSet<string>();
        private readonly object _processedEntryOrderIdsLock = new object();

        // Event fired when SLTP cleanup is complete for an entry order
        public static event Action<string> SLTPCleanupCompleted;

        public SLTPRemovalLogic()
        {
            // Constructor no longer needs log action delegate
        }

        private void Log(string message, LogLevel level)
        {
            // Direct NinjaTrader output - no bridge communication
            NinjaTrader.Code.Output.Process($"[SLTP_LOGIC] {message}", PrintTo.OutputTab1);
        }

        public void HandleExecutionUpdate(
            Execution execution,
            bool enableSLTPRemoval,
            int sltpRemovalDelaySeconds,
            Account account // The account associated with the execution
        )
        {
            NinjaTrader.Code.Output.Process("[SLTP_LOGIC] HandleExecutionUpdate: Entered method.", PrintTo.OutputTab1);
            NinjaTrader.Code.Output.Process($"[SLTP_LOGIC] HandleExecutionUpdate: execution.ExecutionId = {execution?.ExecutionId}, order.Id = {execution?.Order?.Id}", PrintTo.OutputTab1);
            if (!enableSLTPRemoval || account == null || execution == null || execution.Order == null)
            {
                if (execution == null || execution.Order == null)
                {
                    Log("HandleExecutionUpdate received null execution or order.", LogLevel.Warning);
                }
                return;
            }

            // Log order details for debugging
            NinjaTrader.Code.Output.Process($"[SLTP_LOGIC] HandleExecutionUpdate: Order details - State: {execution?.Order?.OrderState}, Filled: {execution?.Order?.Filled}, Type: {execution?.Order?.OrderType}, Action: {execution?.Order?.OrderAction}, IsEntry: {execution?.IsEntry}", PrintTo.OutputTab1);
            
            // Check if this is an entry order fill
            bool isEntryOrderFill = IsEntryOrderFill(execution);
            NinjaTrader.Code.Output.Process($"[SLTP_LOGIC] HandleExecutionUpdate: isEntryOrderFill = {isEntryOrderFill}", PrintTo.OutputTab1);
            
            if (isEntryOrderFill && (execution.Order.OrderState == OrderState.Filled || execution.Order.OrderState == OrderState.PartFilled))
            {
                Log($"Entry fill detected for {execution.Instrument.FullName}. Parent Order ID: {execution.Order.Id}. Fill ID: {execution.ExecutionId}. SL/TP removal timer starting.", LogLevel.Information);

                var pendingRemoval = new PendingSLTPRemoval { EntryFillExecution = execution };
                pendingRemoval.RemovalTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(sltpRemovalDelaySeconds), Tag = pendingRemoval };
                pendingRemoval.RemovalTimer.Tick += SLTPRemovalTimer_Tick;

                lock (_pendingRemovalsLock) { pendingRemovals.Add(pendingRemoval); }
                pendingRemoval.RemovalTimer.Start();
            }
        }

        /// <summary>
        /// Determines if an execution represents an entry order fill by examining order characteristics
        /// instead of relying on the potentially unreliable execution.IsEntry property
        /// </summary>
        private bool IsEntryOrderFill(Execution execution)
        {
            if (execution?.Order == null) return false;

            var order = execution.Order;
            
            // Log the order details for debugging
            Log($"IsEntryOrderFill check - OrderType: {order.OrderType}, OrderAction: {order.OrderAction}, Name: '{order.Name}'", LogLevel.Information);
            
            // Entry orders are typically:
            // 1. Market orders (Buy/Sell/SellShort)
            // 2. Limit orders (Buy/SellShort)
            // 3. StopMarket orders (Buy/SellShort) for breakout entries
            // 4. NOT StopMarket orders that are stop losses (opposite action)
            // 5. NOT Limit orders that are profit targets (opposite action)
            
            bool isEntryOrderType = order.OrderType == OrderType.Market ||
                                   order.OrderType == OrderType.Limit ||
                                   order.OrderType == OrderType.StopMarket;
            
            if (!isEntryOrderType)
            {
                Log($"IsEntryOrderFill: Order {order.Id} rejected - not an entry order type ({order.OrderType})", LogLevel.Information);
                return false;
            }
            
            // Entry orders should be opening positions (Buy, Sell, or SellShort)
            bool isOpeningAction = order.OrderAction == OrderAction.Buy ||
                                   order.OrderAction == OrderAction.Sell ||
                                   order.OrderAction == OrderAction.SellShort;
            
            if (!isOpeningAction)
            {
                Log($"IsEntryOrderFill: Order {order.Id} rejected - not an opening action ({order.OrderAction})", LogLevel.Information);
                return false;
            }
            
            // Additional validation: exclude orders that look like SL/TP based on naming patterns
            string orderName = order.Name?.ToLower() ?? "";
            bool looksLikeSLTP = orderName.Contains("stop") || orderName.Contains("profit") ||
                                orderName.Contains("sl") || orderName.Contains("tp") ||
                                orderName.Contains("target");
            
            if (looksLikeSLTP)
            {
                Log($"IsEntryOrderFill: Order {order.Id} rejected - name suggests SL/TP order ('{order.Name}')", LogLevel.Information);
                return false;
            }
            
            Log($"IsEntryOrderFill: Order {order.Id} accepted as entry order", LogLevel.Information);
            return true;
        }
        
        /// <summary>
        /// Check if an order is a trailing stop order (should not be cancelled by SLTP removal)
        /// </summary>
        /// <param name="order">Order to check</param>
        /// <returns>True if this is a trailing stop order</returns>
        private bool IsTrailingStopOrder(Order order)
        {
            if (order?.Name == null) return false;
            
            // Primary check: Traditional trailing stops use MSM_TRAIL_STOP_ prefix
            if (order.Name.StartsWith("MSM_TRAIL_STOP_"))
            {
                Log($"SLTP_PROTECTION: Detected traditional trailing stop order: {order.Name} - excluding from SLTP removal", LogLevel.Information);
                return true;
            }
            
            // Secondary check: Internal trailing stops may use different patterns
            if (order.Name.Contains("MSM_TRAIL") || order.Name.Contains("InternalTrail"))
            {
                Log($"SLTP_PROTECTION: Detected internal trailing stop order: {order.Name} - excluding from SLTP removal", LogLevel.Information);
                return true;
            }
            
            // Additional safety check for any order containing "TRAIL" or "TRAILING"
            if (order.Name.ToUpper().Contains("TRAIL"))
            {
                Log($"SLTP_PROTECTION: Detected potential trailing order: {order.Name} - excluding from SLTP removal for safety", LogLevel.Warning);
                return true;
            }
            
            Log($"SLTP_PROTECTION: Order {order.Name} is NOT a trailing stop - available for SLTP removal", LogLevel.Information);
            return false;
        }

        private void SLTPRemovalTimer_Tick(object sender, EventArgs e)
        {
            DispatcherTimer timer = sender as DispatcherTimer;
            PendingSLTPRemoval removalDetails = timer?.Tag as PendingSLTPRemoval;

            timer?.Stop(); // Stop the timer first

            if (removalDetails == null || removalDetails.EntryFillExecution == null || removalDetails.EntryFillExecution.Order == null)
            {
                Log("Error: SLTPRemovalTimer_Tick with invalid details.", LogLevel.Error);
                if (timer != null && removalDetails != null) // Attempt to remove if possible, even if details are bad
                {
                    lock (_pendingRemovalsLock) { pendingRemovals.Remove(removalDetails); }
                }
                return;
            }

            // Remove from list after stopping, before processing
            lock (_pendingRemovalsLock) { pendingRemovals.Remove(removalDetails); }

            string parentEntryOrderId = removalDetails.EntryFillExecution.Order.Id.ToString();
            bool alreadyProcessed;
            lock (_processedEntryOrderIdsLock)
            {
                alreadyProcessed = processedEntryOrderIds.Contains(parentEntryOrderId);
            }

            if (alreadyProcessed)
            {
                Log($"SL/TP removal for parent entry order {parentEntryOrderId} already processed. Skipping for fill {removalDetails.EntryFillExecution.ExecutionId}.", LogLevel.Information);
                return;
            }

            ProcessSLTPRemovalInternal(removalDetails.EntryFillExecution);

            // Mark as processed AFTER attempting removal for the first fill of this parent order.
            lock (_processedEntryOrderIdsLock)
            {
                processedEntryOrderIds.Add(parentEntryOrderId);
            }
        }

        // Internal processing method, can be public if direct invocation is needed
        private void ProcessSLTPRemovalInternal(Execution entryFillExecution)
        {
            Order parentEntryOrder = entryFillExecution.Order;
            if (parentEntryOrder == null)
            {
                Log($"Error: Parent entry order is null for fill {entryFillExecution.ExecutionId}.", LogLevel.Error);
                return;
            }
            Log($"Processing SL/TP removal for parent entry: {parentEntryOrder.Id} on {entryFillExecution.Instrument.FullName}", LogLevel.Information);

            Account account = entryFillExecution.Account;
            if (account == null)
            {
                Log($"Error: Account is null for fill {entryFillExecution.ExecutionId}. Cannot process SL/TP removal.", LogLevel.Error);
                return;
            }

            List<Order> ordersToCancel = new List<Order>();

            string expectedFillId = entryFillExecution.ExecutionId; // Use Fill ID as the expected OCO tag

            lock (account.Orders) // Lock the account's order collection during iteration
            {
                // Iterate a copy in case Cancel() modifies the collection, though lock should also help.
                foreach (Order order in account.Orders.ToList())
                {
                    // Updated detailed logging to reflect matching against Fill ID (disabled to prevent log spam)
                    // Log($"[SLTPRemovalLogic-DEBUG] Checking Order: Name='{order.Name}', Oco='{order.Oco}', State='{order.OrderState}', Type='{order.OrderType}', Instrument='{order.Instrument?.FullName ?? "null"}'. Expecting Oco (to match Fill ID)='{expectedFillId}', ParentInstrument='{parentEntryOrder.Instrument?.FullName ?? "null"}' (Parent Fill ID: {entryFillExecution.ExecutionId}).", LogLevel.Information);

                    if (order.Instrument == null || parentEntryOrder.Instrument == null || order.Account == null) continue; // Use parentEntryOrder.Instrument
                    if (order.Instrument != parentEntryOrder.Instrument || order.Account.Name != account.Name) continue; // Direct instrument comparison
                    
                    // Check if order is in a cancelable state
                    if (order.OrderState != OrderState.Working && order.OrderState != OrderState.Accepted && order.OrderState != OrderState.Submitted) continue;
                    
                    if (order.Id == parentEntryOrder.Id) continue; // Don't cancel the entry order itself

                    // Identification logic:
                    // Identification logic:
                    // SL/TP order's OCO tag must match the parent entry order's Fill ID (Execution ID).
                    bool fillIdToOcoMatch = !string.IsNullOrEmpty(expectedFillId) && // expectedFillId is entryFillExecution.ExecutionId
                                            !string.IsNullOrEmpty(order.Oco) &&
                                            order.Oco == expectedFillId;

                    // The old nameMatch (parentEntryOrder.Name == order.Name) is generally not reliable.
                    // The old signalNameToOcoMatch (parentEntryOrder.Name == order.Oco) is being replaced.
                    
                    // New (or restored) alternative match condition
                    bool parentOcoToOrderOcoMatch = parentEntryOrder != null &&
                                                    !string.IsNullOrEmpty(parentEntryOrder.Oco) &&
                                                    !string.IsNullOrEmpty(order.Oco) &&
                                                    order.Oco == parentEntryOrder.Oco;

                    bool isPotentialSL = order.OrderType == OrderType.StopMarket && order.OrderAction != parentEntryOrder.OrderAction;
                    bool isPotentialTP = order.OrderType == OrderType.Limit && order.OrderAction != parentEntryOrder.OrderAction;
                    
                    // Check if this is a trailing stop order (should be excluded from cancellation)
                    bool isTrailingStop = IsTrailingStopOrder(order);
                    
                    // Match SL/TP quantity against the PARENT entry order's total quantity
                    bool quantityMatch = order.Quantity == parentEntryOrder.Quantity;
                    
                    if ((isPotentialSL || isPotentialTP) && quantityMatch && !isTrailingStop)
                    {
                        string matchReason = string.Empty;
                        bool matched = false; // Flag to indicate if a match was found

                        if (fillIdToOcoMatch)
                        {
                            matchReason = $"Entry Fill ID ('{expectedFillId}') to Order OCO ('{order.Oco}')";
                            matched = true;
                        }
                        else if (parentOcoToOrderOcoMatch)
                        {
                            matchReason = $"Parent Entry OCO ('{parentEntryOrder.Oco}') to Order OCO ('{order.Oco}')";
                            matched = true;
                        }

                        if (matched)
                        {
                            Log($"Found potential SL/TP by {matchReason}: Order '{order.Name}' ({order.Id}), Type: {order.OrderType}, Action: {order.OrderAction}, Qty: {order.Quantity}", LogLevel.Information);
                            ordersToCancel.Add(order);
                            // Log the match reason for clarity
                            Log($"SLTPRemovalLogic: Added order {order.Name} ({order.Id}) to cancellation list due to {matchReason}.", LogLevel.Information);
                        }
                        // Optional: Add an else if you want to log orders that are potential SL/TP but don't match either condition
                        // else
                        // {
                        //     Log($"SLTPRemovalLogic: Order {order.Name} ({order.Id}) is potential SL/TP but did not match OCO conditions.", LogLevel.Debug);
                        // }
                    }
                }
            }

            if (ordersToCancel.Count == 0)
            {
                Log($"No SL/TP orders found matching criteria for parent entry {parentEntryOrder.Id}.", LogLevel.Warning);
                return;
            }

            foreach (Order orderToCancel in ordersToCancel.Distinct()) // Use Distinct() in case of duplicates
            {
                Log($"Attempting to cancel order: {orderToCancel.Name} ({orderToCancel.Id}) for parent entry {parentEntryOrder.Id}", LogLevel.Information);
                try
                {
                    account.Cancel(new[] { orderToCancel }); // Cancel expects an IEnumerable<Order>
                    Log($"Cancellation request sent for order {orderToCancel.Id}.", LogLevel.Information);
                }
                catch (Exception ex) 
                { 
                    Log($"Error cancelling order {orderToCancel.Id}: {ex.Message}", LogLevel.Error); 
                }
            }
            
            // Fire completion event to notify that SLTP cleanup is complete for this entry
            try
            {
                SLTPCleanupCompleted?.Invoke(parentEntryOrder.Id.ToString());
                Log($"SLTP cleanup completed for entry order {parentEntryOrder.Id}", LogLevel.Information);
            }
            catch (Exception ex)
            {
                Log($"Error firing SLTPCleanupCompleted event: {ex.Message}", LogLevel.Error);
            }
        }

        public void Cleanup()
        {
            Log("Cleaning up SLTPRemovalLogic resources (timers and processed IDs).", LogLevel.Information);
            lock (_pendingRemovalsLock)
            {
                foreach (var pending in pendingRemovals)
                {
                    pending.RemovalTimer?.Stop();
                }
                pendingRemovals.Clear();
            }
            lock (_processedEntryOrderIdsLock)
            {
                processedEntryOrderIds.Clear();
            }
        }
    }
}