using System;
using System.Collections.Generic;
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
        private readonly List<string> _pendingTrades = new();
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
            _pendingTrades.Add(tradeJson);
            var success = await BridgeGrpcClient.SubmitTradeAsync(tradeJson).ConfigureAwait(false);
            if (success)
            {
                _pendingTrades.Remove(tradeJson);
            }
            return success;
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
