using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Tasks;
using Quantower.Bridge.Client;
using Quantower.MultiStrat.Infrastructure;
using TradingPlatform.BusinessLayer;

namespace Quantower.MultiStrat
{
    /// <summary>
    /// Minimal scaffold illustrating how the Quantower add-on wires into the gRPC bridge.
    /// Concrete Quantower SDK integration (Core events, UI surfaces) will be layered on later.
    /// </summary>
    public class QuantowerAddon
    {
        private readonly ConcurrentDictionary<string, byte> _pendingTrades = new();
        private QuantowerEventBridge? _eventBridge;
        private readonly string _grpcAddress;

        public QuantowerAddon(string grpcAddress)
        {
            _grpcAddress = grpcAddress;
        }

        public bool Start() => StartAsync().GetAwaiter().GetResult();

        public async Task<bool> StartAsync()
        {
            var ok = await BridgeGrpcClient.Initialize(_grpcAddress, source: "qt", component: "qt_addon").ConfigureAwait(false);
            if (!ok)
            {
                Console.WriteLine($"[QT][ERROR] Failed to initialize bridge client: {BridgeGrpcClient.LastError}");
                return false;
            }

            BridgeGrpcClient.StartTradingStream(OnBridgeTradeReceived);

            if (QuantowerEventBridge.TryCreate(OnQuantowerTrade, OnQuantowerPositionClosed, out var bridge) && bridge != null)
            {
                _eventBridge = bridge;

                foreach (var position in bridge.SnapshotPositions())
                {
                    await TryPublishPositionSnapshotAsync(position).ConfigureAwait(false);
                }

                Console.WriteLine("[QT][INFO] Attached to Quantower Core trade stream.");
            }
            else
            {
                Console.WriteLine("[QT][WARN] Quantower Core instance not detected. Running without native event hooks.");
            }

            return true;
        }

        public async Task<bool> SubmitTradeAsync(string tradeJson)
        {
            string? tradeId = null;
            try
            {
                // Extract a stable unique identifier from the trade JSON
                var doc = JsonDocument.Parse(tradeJson);
                var root = doc.RootElement;

                // Try various ID fields in order of preference
                tradeId = GetStringValue(root, "id") ?? 
                         GetStringValue(root, "trade_id") ?? 
                         GetStringValue(root, "base_id") ?? 
                         GetStringValue(root, "qt_position_id") ?? 
                         GetStringValue(root, "position_id");

                if (string.IsNullOrWhiteSpace(tradeId))
                {
                    Console.WriteLine("[QT][ERROR] Trade JSON missing unique identifier");
                    return false;
                }

                _pendingTrades.TryAdd(tradeId, 0);
                var success = await BridgeGrpcClient.SubmitTradeAsync(tradeJson).ConfigureAwait(false);

                _pendingTrades.TryRemove(tradeId, out _);

                return success;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[QT][ERROR] Exception in SubmitTradeAsync: {ex.Message}");
                if (!string.IsNullOrWhiteSpace(tradeId))
                {
                    _pendingTrades.TryRemove(tradeId, out _);
                }
                return false;
            }
        }

        private static string? GetStringValue(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var value))
                return null;

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.ToString(),
                JsonValueKind.True or JsonValueKind.False => value.GetBoolean().ToString(),
                _ => null
            };
        }

        public void Stop()
        {
            BridgeGrpcClient.StopTradingStream();
            BridgeGrpcClient.Shutdown();
            _eventBridge?.Dispose();
            _eventBridge = null;
        }

        private void OnBridgeTradeReceived(string tradeJson)
        {
            // Placeholder: Quantower-specific logic will ingest MT5 responses here.
            Console.WriteLine($"[QT][STREAM] {tradeJson}");
        }

        private void OnQuantowerTrade(Trade trade)
        {
            if (!QuantowerTradeMapper.TryBuildTradeEnvelope(trade, out var payload, out var tradeId))
            {
                Console.WriteLine("[QT][WARN] Unable to translate Quantower trade event into bridge payload.");
                return;
            }

            if (!string.IsNullOrWhiteSpace(tradeId))
            {
                _pendingTrades.TryRemove(tradeId, out _);
            }

            ObserveAsyncOperation(BridgeGrpcClient.SubmitTradeAsync(payload), "SubmitTrade", tradeId ?? "unknown");
        }

        private async Task TryPublishPositionSnapshotAsync(Position position)
        {
            if (QuantowerTradeMapper.TryBuildPositionSnapshot(position, out var tradePayload, out var positionTradeId))
            {
                Console.WriteLine($"[QT][INFO] Streaming existing position as trade snapshot ({positionTradeId ?? "n/a"}).");
                await DispatchWithLoggingAsync(() => BridgeGrpcClient.SubmitTradeAsync(tradePayload), "SubmitTradeSnapshot", positionTradeId ?? "n/a").ConfigureAwait(false);
            }

            if (QuantowerTradeMapper.TryBuildPositionClosure(position, out var closurePayload, out var closureId))
            {
                Console.WriteLine($"[QT][INFO] Broadcasting position state ({closureId ?? "n/a"}) to bridge for reconciliation.");
                await DispatchWithLoggingAsync(() => BridgeGrpcClient.CloseHedgeAsync(closurePayload), "CloseHedgeSnapshot", closureId ?? "n/a").ConfigureAwait(false);
            }
        }

        private void OnQuantowerPositionClosed(Position position)
        {
            if (!QuantowerTradeMapper.TryBuildPositionClosure(position, out var payload, out var closureId))
            {
                Console.WriteLine("[QT][WARN] Unable to map Quantower position closure to bridge notification.");
                return;
            }

            Console.WriteLine($"[QT][INFO] Quantower position closed ({closureId ?? "n/a"}) -> notifying bridge.");
            ObserveAsyncOperation(BridgeGrpcClient.CloseHedgeAsync(payload), "CloseHedge", closureId ?? "n/a");
        }

        private static async Task DispatchWithLoggingAsync(Func<Task<bool>> operation, string operationName, string identifier)
        {
            try
            {
                var success = await operation().ConfigureAwait(false);
                if (!success)
                {
                    Console.WriteLine($"[QT][WARN] {operationName} failed for {identifier}: {BridgeGrpcClient.LastError}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[QT][ERROR] {operationName} threw for {identifier}: {ex.Message}");
            }
        }

        private static void ObserveAsyncOperation(Task<bool> task, string operationName, string identifier)
        {
            task.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    var root = t.Exception?.GetBaseException();
                    Console.WriteLine($"[QT][ERROR] {operationName} exception for {identifier}: {root?.Message ?? t.Exception?.Message}");
                }
                else if (!t.Result)
                {
                    Console.WriteLine($"[QT][WARN] {operationName} unsuccessful for {identifier}: {BridgeGrpcClient.LastError}");
                }
            }, TaskContinuationOptions.ExecuteSynchronously);
        }
    }
}
