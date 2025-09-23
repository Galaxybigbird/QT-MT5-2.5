using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TradingPlatform.BusinessLayer;

namespace Quantower.MultiStrat.Services
{
    public sealed class SltpRemovalService : IDisposable
    {
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _pendingRemovals = new(StringComparer.OrdinalIgnoreCase);
        private bool _disposed;

        public bool Enabled { get; set; }
        public int RemovalDelaySeconds { get; set; } = 2;

        public void HandleTrade(Trade trade)
        {
            if (!Enabled || trade == null)
            {
                return;
            }

            var core = Core.Instance;
            if (core == null)
            {
                return;
            }

            var order = TryResolveOrder(core, trade.OrderId);
            var position = ResolvePosition(core, trade, order);
            if (!IsEntryTrade(trade, order, position))
            {
                return;
            }

            var key = trade.Id ?? trade.OrderId ?? Guid.NewGuid().ToString("N");
            var cts = new CancellationTokenSource();

            var previous = _pendingRemovals.AddOrUpdate(key, cts, (_, existing) =>
            {
                existing.Cancel();
                existing.Dispose();
                return cts;
            });

            if (!ReferenceEquals(previous, cts))
            {
                previous.Cancel();
                previous.Dispose();
            }

            Task.Run(async () =>
            {
                try
                {
                    var delay = Math.Max(1, RemovalDelaySeconds);
                    await Task.Delay(TimeSpan.FromSeconds(delay), cts.Token).ConfigureAwait(false);
                    RemoveProtectiveOrders(core, trade, order, position);
                }
                catch (TaskCanceledException)
                {
                    // ignored on cancellation
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[QT][SLTP] Removal task failed: {ex.Message}\n{ex}");
                }
                finally
                {
                    _pendingRemovals.TryRemove(key, out _);
                    cts.Dispose();
                }
            }, cts.Token);
        }

        private static bool IsEntryTrade(Trade trade, Order? order, Position? position)
        {
            try
            {
                var impact = trade.PositionImpactType.ToString();
                if (impact.Equals("Open", StringComparison.OrdinalIgnoreCase) ||
                    impact.Equals("Increase", StringComparison.OrdinalIgnoreCase) ||
                    impact.Equals("Start", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (impact.Equals("Close", StringComparison.OrdinalIgnoreCase) ||
                    impact.Equals("Decrease", StringComparison.OrdinalIgnoreCase) ||
                    impact.Equals("End", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            catch
            {
                // Some providers may not populate PositionImpactType; fall back to heuristics.
            }

            var direction = ResolvePositionSide(position);
            if (order != null && direction.HasValue)
            {
                return order.Side == direction.Value;
            }

            if (direction.HasValue)
            {
                var quantitySign = Math.Sign(trade.Quantity);
                if (direction == Side.Buy && quantitySign > 0)
                {
                    return true;
                }

                if (direction == Side.Sell && quantitySign < 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static void RemoveProtectiveOrders(Core core, Trade trade, Order? triggeringOrder, Position? position)
        {
            if (core.Orders == null)
            {
                return;
            }

            var positionId = trade.PositionId;
            var accountKey = triggeringOrder?.Account?.Id ?? triggeringOrder?.Account?.Name ?? position?.Account?.Id ?? position?.Account?.Name;
            var symbolKey = triggeringOrder?.Symbol?.Id ?? triggeringOrder?.Symbol?.Name ?? position?.Symbol?.Id ?? position?.Symbol?.Name;

            var protectiveOrders = new List<Order>();

            foreach (var order in core.Orders)
            {
                if (order == null)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(positionId))
                {
                    if (!string.Equals(order.PositionId, positionId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(accountKey))
                    {
                        var orderAccount = order.Account?.Id ?? order.Account?.Name;
                        if (!string.Equals(orderAccount, accountKey, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(symbolKey))
                    {
                        var orderSymbol = order.Symbol?.Id ?? order.Symbol?.Name;
                        if (!string.Equals(orderSymbol, symbolKey, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                    }
                }

                if (IsProtectiveOrder(order, position))
                {
                    protectiveOrders.Add(order);
                }
            }

            foreach (var order in protectiveOrders)
            {
                try
                {
                    var result = order.Cancel("qt_sltp_cleanup");
                    if (result != null && result.Status != TradingOperationResultStatus.Success)
                    {
                        Console.Error.WriteLine($"[QT][SLTP] Cancel rejected for order {order.Id}: {result?.Message}");
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[QT][SLTP] Exception cancelling order {order.Id}: {ex.Message}\n{ex}");
                }
            }
        }

        private static Order? TryResolveOrder(Core core, string? orderId)
        {
            if (string.IsNullOrWhiteSpace(orderId))
            {
                return null;
            }

            try
            {
                return core.GetOrderById(orderId);
            }
            catch
            {
                return null;
            }
        }

        private static Position? ResolvePosition(Core core, Trade trade, Order? order)
        {
            if (!string.IsNullOrWhiteSpace(trade.PositionId))
            {
                try
                {
                    var position = core.GetPositionById(trade.PositionId);
                    if (position != null)
                    {
                        return position;
                    }
                }
                catch
                {
                    // ignored
                }
            }

            var accountKey = order?.Account?.Id ?? order?.Account?.Name;
            var symbolKey = order?.Symbol?.Id ?? order?.Symbol?.Name;

            var candidates = core.Positions;
            if (candidates == null)
            {
                return null;
            }

            foreach (var candidate in candidates)
            {
                if (candidate == null)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(symbolKey))
                {
                    var candidateSymbol = candidate.Symbol?.Id ?? candidate.Symbol?.Name;
                    if (!string.Equals(candidateSymbol, symbolKey, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                if (!string.IsNullOrWhiteSpace(accountKey))
                {
                    var candidateAccount = candidate.Account?.Id ?? candidate.Account?.Name;
                    if (!string.Equals(candidateAccount, accountKey, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                return candidate;
            }

            return null;
        }

        private static Side? ResolvePositionSide(Position? position)
        {
            if (position == null)
            {
                return null;
            }

            if (position.Quantity > 0)
            {
                return Side.Buy;
            }

            if (position.Quantity < 0)
            {
                return Side.Sell;
            }

            return null;
        }

        private static bool IsProtectiveOrder(Order order, Position? position)
        {
            if (order.Status == OrderStatus.Filled || order.Status == OrderStatus.Cancelled || order.Status == OrderStatus.Refused)
            {
                return false;
            }

            if (order.TrailOffset > 0)
            {
                return false;
            }

            var descriptor = (order.OrderTypeId ?? string.Empty).ToLowerInvariant();
            var name = order.ToString()?.ToLowerInvariant() ?? string.Empty;

            var looksLikeStop = descriptor.Contains("stop") || name.Contains("stop");
            var looksLikeTarget = descriptor.Contains("limit") || name.Contains("take") || name.Contains("target");

            if (!looksLikeStop && !looksLikeTarget)
            {
                return false;
            }

            var positionSide = ResolvePositionSide(position);
            if (!positionSide.HasValue)
            {
                return looksLikeStop || looksLikeTarget;
            }

            if (order.Side != positionSide.Value)
            {
                return true;
            }

            return false;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var entry in _pendingRemovals)
            {
                entry.Value.Cancel();
                entry.Value.Dispose();
            }

            _pendingRemovals.Clear();
        }
    }
}
