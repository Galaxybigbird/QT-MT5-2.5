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
        private const string LogComponent = "qt_addon";

        public QuantowerAddon(string grpcAddress)
        {
            _grpcAddress = grpcAddress;
        }

        public bool Start() => StartAsync().GetAwaiter().GetResult();

        public async Task<bool> StartAsync()
        {
            var ok = await BridgeGrpcClient.Initialize(_grpcAddress, source: "qt", component: LogComponent).ConfigureAwait(false);
            if (!ok)
            {
                LogError($"Failed to initialize bridge client: {BridgeGrpcClient.LastError}");
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

                LogInfo("Attached to Quantower Core trade stream");
            }
            else
            {
                LogWarn("Quantower Core instance not detected. Running without native event hooks");
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
                    LogError("Trade JSON missing unique identifier");
                    return false;
                }

                _pendingTrades.TryAdd(tradeId, 0);
                var success = await BridgeGrpcClient.SubmitTradeAsync(tradeJson).ConfigureAwait(false);

                _pendingTrades.TryRemove(tradeId, out _);

                return success;
            }
            catch (Exception ex)
            {
                LogError($"Exception in SubmitTradeAsync: {ex.Message}", tradeId);
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
            LogInfo("Quantower add-on stopped");
        }

        private void OnBridgeTradeReceived(string tradeJson)
        {
            // Placeholder: Quantower-specific logic will ingest MT5 responses here.
            LogDebug($"Bridge stream payload received: {tradeJson}");
        }

        private void OnQuantowerTrade(Trade trade)
        {
            if (!QuantowerTradeMapper.TryBuildTradeEnvelope(trade, out var payload, out var tradeId))
            {
                LogWarn("Unable to translate Quantower trade event into bridge payload", trade?.Id, trade?.PositionId);
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
                LogInfo($"Streaming existing position as trade snapshot ({positionTradeId ?? "n/a"})", positionTradeId, positionTradeId);
                await DispatchWithLoggingAsync(() => BridgeGrpcClient.SubmitTradeAsync(tradePayload), "SubmitTradeSnapshot", positionTradeId ?? "n/a").ConfigureAwait(false);
            }

            if (QuantowerTradeMapper.TryBuildPositionClosure(position, out var closurePayload, out var closureId))
            {
                LogInfo($"Broadcasting position state ({closureId ?? "n/a"}) to bridge for reconciliation", closureId, closureId);
                await DispatchWithLoggingAsync(() => BridgeGrpcClient.CloseHedgeAsync(closurePayload), "CloseHedgeSnapshot", closureId ?? "n/a").ConfigureAwait(false);
            }
        }

        private void OnQuantowerPositionClosed(Position position)
        {
            if (!QuantowerTradeMapper.TryBuildPositionClosure(position, out var payload, out var closureId))
            {
                LogWarn("Unable to map Quantower position closure to bridge notification", closureId, closureId);
                return;
            }

            LogInfo($"Quantower position closed ({closureId ?? "n/a"}) -> notifying bridge", closureId, closureId);
            ObserveAsyncOperation(BridgeGrpcClient.CloseHedgeAsync(payload), "CloseHedge", closureId ?? "n/a");
        }

        private static async Task DispatchWithLoggingAsync(Func<Task<bool>> operation, string operationName, string identifier)
        {
            try
            {
                var success = await operation().ConfigureAwait(false);
                if (!success)
                {
                    LogWarnStatic($"{operationName} failed", identifier, BridgeGrpcClient.LastError);
                }
            }
            catch (Exception ex)
            {
                LogErrorStatic($"{operationName} threw", identifier, ex.Message);
            }
        }

        private static void ObserveAsyncOperation(Task<bool> task, string operationName, string identifier)
        {
            task.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    var root = t.Exception?.GetBaseException();
                    LogErrorStatic($"{operationName} exception", identifier, root?.Message ?? t.Exception?.Message ?? "unknown error");
                }
                else if (!t.Result)
                {
                    LogWarnStatic($"{operationName} unsuccessful", identifier, BridgeGrpcClient.LastError);
                }
            }, TaskContinuationOptions.ExecuteSynchronously);
        }

        private static void LogDebug(string message, string? tradeId = null, string? baseId = null)
        {
            BridgeGrpcClient.LogDebug(LogComponent, message, tradeId ?? string.Empty, baseId ?? string.Empty);
            Console.WriteLine($"[QT][DEBUG] {message}");
        }

        private static void LogInfo(string message, string? tradeId = null, string? baseId = null)
        {
            BridgeGrpcClient.LogInfo(LogComponent, message, tradeId ?? string.Empty, baseId ?? string.Empty);
            Console.WriteLine($"[QT][INFO] {message}");
        }

        private static void LogWarn(string message, string? tradeId = null, string? baseId = null)
        {
            BridgeGrpcClient.LogWarn(LogComponent, message, tradeId ?? string.Empty, baseId ?? string.Empty);
            Console.WriteLine($"[QT][WARN] {message}");
        }

        private static void LogError(string message, string? tradeId = null, string? baseId = null)
        {
            BridgeGrpcClient.LogError(LogComponent, message, tradeId ?? string.Empty, errorCode: string.Empty, baseId: baseId ?? string.Empty);
            Console.WriteLine($"[QT][ERROR] {message}");
        }

        private static void LogWarnStatic(string message, string identifier, string details)
        {
            var composed = string.IsNullOrWhiteSpace(details)
                ? $"{message} for {identifier}"
                : $"{message} for {identifier}: {details}";
            BridgeGrpcClient.LogWarn(LogComponent, composed, identifier ?? string.Empty, identifier ?? string.Empty);
            Console.WriteLine($"[QT][WARN] {composed}");
        }

        private static void LogErrorStatic(string message, string identifier, string details)
        {
            var composed = string.IsNullOrWhiteSpace(details)
                ? $"{message} for {identifier}"
                : $"{message} for {identifier}: {details}";
            BridgeGrpcClient.LogError(LogComponent, composed, identifier ?? string.Empty, errorCode: string.Empty, baseId: identifier ?? string.Empty);
            Console.WriteLine($"[QT][ERROR] {composed}");
        }
    }
}
