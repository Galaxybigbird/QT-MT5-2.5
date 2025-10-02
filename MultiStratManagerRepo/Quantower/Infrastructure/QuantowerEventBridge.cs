using System;
using System.Collections.Generic;
using System.Linq;
using Quantower.Bridge.Client;
using TradingPlatform.BusinessLayer;

namespace Quantower.MultiStrat.Infrastructure
{
    internal sealed class QuantowerEventBridge : IDisposable
    {
        private const string LogComponent = "qt_event_bridge";
        private readonly Core _core;
        private readonly Action<Trade> _tradeHandler;
        private readonly Action<Position>? _positionAddedHandler;
        private readonly Action<Position>? _positionClosedHandler;
        private volatile bool _disposed;

        private QuantowerEventBridge(Core core, Action<Trade> onTrade, Action<Position>? onPositionAdded, Action<Position>? onPositionClosed)
        {
            _core = core;
            _tradeHandler = onTrade;
            _positionAddedHandler = onPositionAdded;
            _positionClosedHandler = onPositionClosed;

            _core.TradeAdded += HandleTradeAdded;

            if (_positionAddedHandler != null)
            {
                _core.PositionAdded += HandlePositionAdded;
            }

            if (_positionClosedHandler != null)
            {
                _core.PositionRemoved += HandlePositionRemoved;
            }
        }

        public static bool TryCreate(Action<Trade> onTradeAdded, Action<Position>? onPositionAdded, Action<Position>? onPositionClosed, out QuantowerEventBridge? bridge)
        {
            if (onTradeAdded == null)
            {
                throw new ArgumentNullException(nameof(onTradeAdded));
            }

            bridge = null;

            try
            {
                var core = Core.Instance;
                if (core == null)
                {
                    LogWarn("Quantower Core.Instance unavailable; skipping native event bridge.");
                    return false;
                }

                bridge = new QuantowerEventBridge(core, onTradeAdded, onPositionAdded, onPositionClosed);
                return true;
            }
            catch (Exception ex)
            {
                LogError($"Failed to attach Quantower event bridge: {ex.Message}", ex);
                bridge = null;
                return false;
            }
        }

        public IEnumerable<Position> SnapshotPositions()
        {
            ThrowIfDisposed();

            var positions = _core.Positions;
            if (positions == null)
            {
                return Array.Empty<Position>();
            }

            // CRITICAL FIX: Filter out historical/closed positions
            // Quantower keeps position IDs in history/memory even after they're closed
            // Only return positions with non-zero quantity (active positions)
            // Use double.Epsilon to filter out effectively zero quantities
            var activePositions = positions.Where(p => p != null && Math.Abs(p.Quantity) > double.Epsilon).ToArray();

            LogInfo($"SnapshotPositions: Found {activePositions.Length} active positions (filtered from {positions.Count()} total)");

            return activePositions;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                try
                {
                    _core.TradeAdded -= HandleTradeAdded;

                    if (_positionAddedHandler != null)
                    {
                        _core.PositionAdded -= HandlePositionAdded;
                    }

                    if (_positionClosedHandler != null)
                    {
                        _core.PositionRemoved -= HandlePositionRemoved;
                    }
                }
                catch (Exception ex)
                {
                    LogWarn($"Failed detaching Quantower event bridge: {ex.Message}");
                }
            }

            _disposed = true;
        }

        private void HandleTradeAdded(Trade trade)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _tradeHandler(trade);
            }
            catch (Exception ex)
            {
                LogError($"TradeAdded handler threw: {ex.Message}", ex);
            }
        }

        private void HandlePositionAdded(Position position)
        {
            if (_positionAddedHandler == null || _disposed)
            {
                return;
            }

            try
            {
                _positionAddedHandler(position);
            }
            catch (Exception ex)
            {
                LogError($"PositionAdded handler threw: {ex.Message}", ex);
            }
        }

        private void HandlePositionRemoved(Position position)
        {
            if (_positionClosedHandler == null)
            {
                return;
            }

            if (_disposed)
            {
                return;
            }

            try
            {
                _positionClosedHandler(position);
            }
            catch (Exception ex)
            {
                LogError($"PositionRemoved handler threw: {ex.Message}", ex);
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(QuantowerEventBridge));
            }
        }

        private static void LogInfo(string message)
        {
            BridgeGrpcClient.LogInfo(LogComponent, message);
        }

        private static void LogWarn(string message)
        {
            BridgeGrpcClient.LogWarn(LogComponent, message);
        }

        private static void LogError(string message, Exception? ex = null)
        {
            var details = ex == null ? message : $"{message}: {ex}";
            BridgeGrpcClient.LogError(LogComponent, details);
        }
    }
}
