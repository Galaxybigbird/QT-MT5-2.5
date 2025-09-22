using System;
using System.Collections.Generic;
using TradingPlatform.BusinessLayer;

namespace Quantower.MultiStrat.Infrastructure
{
    internal sealed class QuantowerEventBridge : IDisposable
    {
        private readonly Core _core;
        private readonly Action<Trade> _tradeHandler;
        private readonly Action<Position>? _positionClosedHandler;
        private bool _disposed;

        private QuantowerEventBridge(Core core, Action<Trade> onTrade, Action<Position>? onPositionClosed)
        {
            _core = core;
            _tradeHandler = onTrade;
            _positionClosedHandler = onPositionClosed;

            _core.TradeAdded += HandleTradeAdded;

            if (_positionClosedHandler != null)
            {
                _core.PositionRemoved += HandlePositionRemoved;
            }
        }

        public static bool TryCreate(Action<Trade> onTradeAdded, Action<Position>? onPositionClosed, out QuantowerEventBridge? bridge)
        {
            bridge = null;

            try
            {
                var core = Core.Instance;
                if (core == null)
                {
                    Console.Error.WriteLine("[QT][WARN] Quantower Core.Instance unavailable; skipping native event bridge.");
                    return false;
                }

                bridge = new QuantowerEventBridge(core, onTradeAdded, onPositionClosed);
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[QT][ERROR] Failed to attach Quantower event bridge: {ex.Message}\n{ex}");
                bridge = null;
                return false;
            }
        }

        public IEnumerable<Position> SnapshotPositions()
        {
            var positions = _core.Positions;
            return positions ?? Array.Empty<Position>();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _core.TradeAdded -= HandleTradeAdded;

                if (_positionClosedHandler != null)
                {
                    _core.PositionRemoved -= HandlePositionRemoved;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[QT][WARN] Failed detaching Quantower event bridge: {ex.Message}");
            }

            _disposed = true;
        }

        private void HandleTradeAdded(Trade trade)
        {
            try
            {
                _tradeHandler(trade);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[QT][ERROR] TradeAdded handler threw: {ex.Message}\n{ex}");
            }
        }

        private void HandlePositionRemoved(Position position)
        {
            if (_positionClosedHandler == null)
            {
                return;
            }

            try
            {
                _positionClosedHandler(position);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[QT][ERROR] PositionRemoved handler threw: {ex.Message}\n{ex}");
            }
        }
    }
}
