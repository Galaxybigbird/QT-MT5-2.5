using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Quantower.Bridge.Client;
using Quantower.MultiStrat.Infrastructure;
using TradingPlatform.BusinessLayer;

namespace Quantower.MultiStrat
{
    public sealed class QuantowerBridgeService : IDisposable
    {
        private const string LogComponent = "qt_addon";

        private readonly ConcurrentDictionary<string, byte> _pendingTrades = new();
        private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

        private QuantowerEventBridge? _eventBridge;
        private string? _grpcAddress;
        private bool _isRunning;

        public event Action<BridgeLogEntry>? Log;
        public event Action<Trade>? TradeReceived;
        public event Action<Position>? PositionAdded;
        public event Action<Position>? PositionRemoved;

        public bool IsRunning => _isRunning;

        public string? CurrentAddress => _grpcAddress;

        public async Task<bool> StartAsync(string grpcAddress)
        {
            if (string.IsNullOrWhiteSpace(grpcAddress))
            {
                throw new ArgumentException("gRPC address must be provided", nameof(grpcAddress));
            }

            await _lifecycleLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_isRunning && string.Equals(_grpcAddress, grpcAddress, StringComparison.OrdinalIgnoreCase))
                {
                    EmitLog(BridgeLogLevel.Info, $"Bridge already running on {grpcAddress}");
                    return true;
                }

                if (_isRunning)
                {
                    EmitLog(BridgeLogLevel.Info, "Restarting bridge with new settings");
                    StopCore();
                }

                _grpcAddress = grpcAddress.Trim();
                return await StartCoreAsync().ConfigureAwait(false);
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        public async Task StopAsync()
        {
            await _lifecycleLock.WaitAsync().ConfigureAwait(false);
            try
            {
                StopCore();
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        public void Stop()
        {
            StopAsync().GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            if (_isRunning)
            {
                StopCore();
            }

            _lifecycleLock.Dispose();
        }

        private async Task<bool> StartCoreAsync()
        {
            if (string.IsNullOrEmpty(_grpcAddress))
            {
                EmitLog(BridgeLogLevel.Error, "Cannot start bridge without gRPC address");
                return false;
            }

            EmitLog(BridgeLogLevel.Info, $"Connecting to {_grpcAddress}");
            var ok = await BridgeGrpcClient.Initialize(_grpcAddress, source: "qt", component: LogComponent).ConfigureAwait(false);
            if (!ok)
            {
                EmitLog(BridgeLogLevel.Error, $"Bridge connection failed: {BridgeGrpcClient.LastError}");
                return false;
            }

            BridgeGrpcClient.StartTradingStream(OnBridgeTradeReceived);

            if (QuantowerEventBridge.TryCreate(OnQuantowerTrade, OnQuantowerPositionAdded, OnQuantowerPositionClosed, out var bridge) && bridge != null)
            {
                _eventBridge = bridge;

                foreach (var position in bridge.SnapshotPositions())
                {
                    await TryPublishPositionSnapshotAsync(position).ConfigureAwait(false);
                }

                EmitLog(BridgeLogLevel.Info, "Attached to Quantower Core trade stream");
            }
            else
            {
                EmitLog(BridgeLogLevel.Warn, "Quantower Core instance not detected. Running without native event hooks");
            }

            _pendingTrades.Clear();
            _isRunning = true;
            EmitLog(BridgeLogLevel.Info, "Bridge ready");
            return true;
        }

        private void StopCore()
        {
            if (!_isRunning)
            {
                return;
            }

            try
            {
                BridgeGrpcClient.StopTradingStream();
                BridgeGrpcClient.Shutdown();
            }
            catch (Exception ex)
            {
                EmitLog(BridgeLogLevel.Warn, "Bridge shutdown encountered an error", details: ex.Message);
            }
            finally
            {
                try
                {
                    _eventBridge?.Dispose();
                }
                catch (Exception ex)
                {
                    EmitLog(BridgeLogLevel.Warn, "Event bridge disposal encountered an error", details: ex.Message);
                }

                _eventBridge = null;
                _pendingTrades.Clear();
                _isRunning = false;
                EmitLog(BridgeLogLevel.Info, "Bridge stopped");
            }
        }

        public async Task<bool> SubmitTradeAsync(string tradeJson)
        {
            string? tradeId = null;
            try
            {
                using var doc = JsonDocument.Parse(tradeJson);
                var root = doc.RootElement;

                tradeId = GetStringValue(root, "id") ??
                          GetStringValue(root, "trade_id") ??
                          GetStringValue(root, "base_id") ??
                          GetStringValue(root, "qt_position_id") ??
                          GetStringValue(root, "position_id");

                if (string.IsNullOrWhiteSpace(tradeId))
                {
                    EmitLog(BridgeLogLevel.Error, "Trade JSON missing unique identifier");
                    return false;
                }

                _pendingTrades.TryAdd(tradeId, 0);
                var success = await BridgeGrpcClient.SubmitTradeAsync(tradeJson).ConfigureAwait(false);
                _pendingTrades.TryRemove(tradeId, out _);

                return success;
            }
            catch (Exception ex)
            {
                EmitLog(BridgeLogLevel.Error, "Exception in SubmitTradeAsync", tradeId, tradeId, ex.Message);
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
            {
                return null;
            }

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.ToString(),
                JsonValueKind.True or JsonValueKind.False => value.GetBoolean().ToString(),
                _ => null
            };
        }

        private void OnBridgeTradeReceived(string tradeJson)
        {
            EmitLog(BridgeLogLevel.Debug, $"Bridge stream payload received: {tradeJson}");
        }

        private void OnQuantowerTrade(Trade trade)
        {
            if (!QuantowerTradeMapper.TryBuildTradeEnvelope(trade, out var payload, out var tradeId))
            {
                EmitLog(BridgeLogLevel.Warn, "Unable to translate Quantower trade event into bridge payload", trade?.Id, trade?.PositionId);
                return;
            }

            if (!string.IsNullOrWhiteSpace(tradeId))
            {
                _pendingTrades.TryRemove(tradeId, out _);
            }

            ObserveAsyncOperation(BridgeGrpcClient.SubmitTradeAsync(payload), "SubmitTrade", tradeId ?? "unknown");

            try
            {
                TradeReceived?.Invoke(trade);
            }
            catch (Exception ex)
            {
                EmitLog(BridgeLogLevel.Warn, "Trade listener threw", trade.Id, trade.PositionId, ex.Message);
            }
        }

        private async Task TryPublishPositionSnapshotAsync(Position position)
        {
            if (QuantowerTradeMapper.TryBuildPositionSnapshot(position, out var tradePayload, out var positionTradeId))
            {
                EmitLog(BridgeLogLevel.Info, $"Streaming existing position as trade snapshot ({positionTradeId ?? "n/a"})", positionTradeId, positionTradeId);
                await DispatchWithLoggingAsync(() => BridgeGrpcClient.SubmitTradeAsync(tradePayload), "SubmitTradeSnapshot", positionTradeId ?? "n/a").ConfigureAwait(false);
            }

            if (QuantowerTradeMapper.TryBuildPositionClosure(position, out var closurePayload, out var closureId))
            {
                EmitLog(BridgeLogLevel.Info, $"Broadcasting position state ({closureId ?? "n/a"}) to bridge for reconciliation", closureId, closureId);
                await DispatchWithLoggingAsync(() => BridgeGrpcClient.CloseHedgeAsync(closurePayload), "CloseHedgeSnapshot", closureId ?? "n/a").ConfigureAwait(false);
            }

            RaisePositionAdded(position);
        }

        private void OnQuantowerPositionClosed(Position position)
        {
            if (!QuantowerTradeMapper.TryBuildPositionClosure(position, out var payload, out var closureId))
            {
                EmitLog(BridgeLogLevel.Warn, "Unable to map Quantower position closure to bridge notification", closureId, closureId);
                return;
            }

            EmitLog(BridgeLogLevel.Info, $"Quantower position closed ({closureId ?? "n/a"}) -> notifying bridge", closureId, closureId);
            ObserveAsyncOperation(BridgeGrpcClient.CloseHedgeAsync(payload), "CloseHedge", closureId ?? "n/a");

            try
            {
                PositionRemoved?.Invoke(position);
            }
            catch (Exception ex)
            {
                EmitLog(BridgeLogLevel.Warn, "PositionRemoved listener threw", position.Id, position.UniqueId, ex.Message);
            }
        }

        private void OnQuantowerPositionAdded(Position position)
        {
            if (QuantowerTradeMapper.TryBuildPositionSnapshot(position, out var tradePayload, out var positionTradeId))
            {
                EmitLog(BridgeLogLevel.Info, $"Quantower position added ({positionTradeId ?? "n/a"}) -> notifying bridge", positionTradeId, positionTradeId);
                ObserveAsyncOperation(BridgeGrpcClient.SubmitTradeAsync(tradePayload), "SubmitTradeSnapshot", positionTradeId ?? "n/a");
            }

            if (QuantowerTradeMapper.TryBuildPositionClosure(position, out var closurePayload, out var closureId))
            {
                EmitLog(BridgeLogLevel.Info, $"Quantower position state broadcast ({closureId ?? "n/a"}) after addition", closureId, closureId);
                ObserveAsyncOperation(BridgeGrpcClient.CloseHedgeAsync(closurePayload), "CloseHedgeSnapshot", closureId ?? "n/a");
            }

            try
            {
                PositionAdded?.Invoke(position);
            }
            catch (Exception ex)
            {
                EmitLog(BridgeLogLevel.Warn, "PositionAdded listener threw", position.Id, position.UniqueId, ex.Message);
            }
        }

        public Task<bool> SubmitElasticUpdateAsync(string payload, string? baseId)
        {
            return DispatchWithLoggingAsync(() => BridgeGrpcClient.SubmitElasticUpdateAsync(payload), "SubmitElasticUpdate", baseId ?? string.Empty);
        }

        public Task<bool> SubmitTrailingUpdateAsync(string payload, string? baseId)
        {
            return DispatchWithLoggingAsync(() => BridgeGrpcClient.SubmitTrailingUpdateAsync(payload), "SubmitTrailingUpdate", baseId ?? string.Empty);
        }

        public Task<bool> NotifyHedgeCloseAsync(string payload, string? baseId)
        {
            return DispatchWithLoggingAsync(() => BridgeGrpcClient.NotifyHedgeCloseAsync(payload), "NotifyHedgeClose", baseId ?? string.Empty);
        }

        private async Task<bool> DispatchWithLoggingAsync(Func<Task<bool>> operation, string operationName, string identifier)
        {
            try
            {
                var success = await operation().ConfigureAwait(false);
                if (!success)
                {
                    EmitLog(BridgeLogLevel.Warn, $"{operationName} failed", identifier, identifier, BridgeGrpcClient.LastError);
                }
                return success;
            }
            catch (Exception ex)
            {
                EmitLog(BridgeLogLevel.Error, $"{operationName} threw", identifier, identifier, ex.Message);
                return false;
            }
        }

        private void RaisePositionAdded(Position position)
        {
            try
            {
                PositionAdded?.Invoke(position);
            }
            catch (Exception ex)
            {
                EmitLog(BridgeLogLevel.Warn, "PositionAdded listener threw", position.Id, position.UniqueId, ex.Message);
            }
        }

        private void RaisePositionRemoved(Position position)
        {
            try
            {
                PositionRemoved?.Invoke(position);
            }
            catch (Exception ex)
            {
                EmitLog(BridgeLogLevel.Warn, "PositionRemoved listener threw", position.Id, position.UniqueId, ex.Message);
            }
        }

        private void RaiseTradeReceived(Trade trade)
        {
            try
            {
                TradeReceived?.Invoke(trade);
            }
            catch (Exception ex)
            {
                EmitLog(BridgeLogLevel.Warn, "Trade listener threw", trade.Id, trade.PositionId, ex.Message);
            }
        }

        private void ObserveAsyncOperation(Task<bool> task, string operationName, string identifier)
        {
            task.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    var root = t.Exception?.GetBaseException();
                    EmitLog(BridgeLogLevel.Error, $"{operationName} exception", identifier, identifier, root?.Message ?? t.Exception?.Message ?? "unknown error");
                }
                else if (!t.Result)
                {
                    EmitLog(BridgeLogLevel.Warn, $"{operationName} unsuccessful", identifier, identifier, BridgeGrpcClient.LastError);
                }
            }, TaskContinuationOptions.ExecuteSynchronously);
        }

        private void EmitLog(BridgeLogLevel level, string message, string? tradeId = null, string? baseId = null, string? details = null)
        {
            var entry = new BridgeLogEntry(DateTime.UtcNow, level, message, tradeId, baseId, details);

            try
            {
                Log?.Invoke(entry);
            }
            catch
            {
                // Suppress consumer logging failures
            }

            switch (level)
            {
                case BridgeLogLevel.Debug:
                    BridgeGrpcClient.LogDebug(LogComponent, message, tradeId ?? string.Empty, baseId ?? string.Empty);
                    break;
                case BridgeLogLevel.Info:
                    BridgeGrpcClient.LogInfo(LogComponent, message, tradeId ?? string.Empty, baseId ?? string.Empty);
                    break;
                case BridgeLogLevel.Warn:
                    BridgeGrpcClient.LogWarn(LogComponent, message, tradeId ?? string.Empty, baseId ?? string.Empty);
                    break;
                case BridgeLogLevel.Error:
                    BridgeGrpcClient.LogError(LogComponent, message, tradeId ?? string.Empty, errorCode: string.Empty, baseId: baseId ?? string.Empty);
                    break;
            }

            Console.WriteLine($"[QT][{level}] {message}");
        }

        public enum BridgeLogLevel
        {
            Debug,
            Info,
            Warn,
            Error
        }

        public sealed record BridgeLogEntry(DateTime TimestampUtc, BridgeLogLevel Level, string Message, string? TradeId, string? BaseId, string? Details);
    }
}
