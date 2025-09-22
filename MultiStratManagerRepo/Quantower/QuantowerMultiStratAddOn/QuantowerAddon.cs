using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Tasks;
using Quantower.Bridge.Client;

namespace Quantower.MultiStrat
{
    /// <summary>
    /// Minimal scaffold illustrating how the Quantower add-on wires into the gRPC bridge.
    /// Concrete Quantower SDK integration (Core events, UI surfaces) will be layered on later.
    /// </summary>
    public class QuantowerAddon
    {
        private readonly ConcurrentDictionary<string, byte> _pendingTrades = new();
        private readonly string _grpcAddress;

        public QuantowerAddon(string grpcAddress)
        {
            _grpcAddress = grpcAddress;
        }

        public bool Start()
        {
            var ok = BridgeGrpcClient.Initialize(_grpcAddress, source: "qt", component: "qt_addon");
            if (!ok)
            {
                Console.WriteLine($"[QT][ERROR] Failed to initialize bridge client: {BridgeGrpcClient.LastError}");
                return false;
            }

            BridgeGrpcClient.StartTradingStream(OnBridgeTradeReceived);
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
        }

        private void OnBridgeTradeReceived(string tradeJson)
        {
            // Placeholder: Quantower-specific logic will ingest MT5 responses here.
            Console.WriteLine($"[QT][STREAM] {tradeJson}");
        }
    }
}
