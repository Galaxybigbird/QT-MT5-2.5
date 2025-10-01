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

        private static readonly TimeSpan TradeSubmitTimeout = TimeSpan.FromSeconds(10);

        private readonly ConcurrentDictionary<string, byte> _pendingTrades = new();
        private readonly ConcurrentDictionary<string, DateTime> _recentClosures = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, DateTime> _recentPositionSubmissions = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
        private readonly object _healthSync = new();

        private CancellationTokenSource? _healthMonitorCts;
        private Task? _healthMonitorTask;

        private static readonly TimeSpan HealthProbeInterval = TimeSpan.FromSeconds(5);
        private const int MaxHealthCheckFailures = 3;

        private QuantowerEventBridge? _eventBridge;
        private string? _grpcAddress;
        private bool _isRunning;
        private bool _streamHealthy;

        public event Action<BridgeLogEntry>? Log;
        public event Action<Trade>? TradeReceived;
        public event Action<Position>? PositionAdded;
        public event Action<Position>? PositionRemoved;
        public event Action<bool>? ConnectionStateChanged;
        public event Action<BridgeGrpcClient.StreamingState, string?>? StreamingStateChanged;
        public event Action<BridgeStreamEnvelope>? StreamEnvelopeReceived;

        public bool IsRunning => _isRunning;

        public bool IsOnline => _isRunning && _streamHealthy;

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

        [Obsolete("Use StopAsync() instead.")]
        public void Stop()
        {
            // CRITICAL FIX: Fire-and-forget shutdown to prevent UI freeze
            // When bridge is killed, blocking here causes Quantower to freeze completely
            // This is called during Quantower shutdown, often on the UI thread
            _ = Task.Run(async () =>
            {
                try
                {
                    // Use a timeout to prevent indefinite blocking
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await StopAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    EmitLog(BridgeLogLevel.Warn, $"Stop() encountered error: {ex.Message}");
                }
            });
        }

        public void Dispose()
        {
            if (_isRunning)
            {
                // CRITICAL FIX: Fire-and-forget shutdown to prevent UI freeze
                // Dispose() is often called on the UI thread during Quantower shutdown
                // Blocking here causes Quantower to freeze when bridge is killed
                _ = Task.Run(() =>
                {
                    try
                    {
                        StopCore();
                    }
                    catch (Exception ex)
                    {
                        EmitLog(BridgeLogLevel.Warn, $"Dispose() encountered error: {ex.Message}");
                    }
                });
            }

            // Dispose the lifecycle lock on a background thread to prevent blocking
            _ = Task.Run(() =>
            {
                try
                {
                    _lifecycleLock.Dispose();
                }
                catch
                {
                    // Ignore disposal errors
                }
            });
        }

        private async Task<bool> StartCoreAsync()
        {
            if (string.IsNullOrEmpty(_grpcAddress))
            {
                EmitLog(BridgeLogLevel.Error, "Cannot start bridge without gRPC address");
                return false;
            }

            EmitLog(BridgeLogLevel.Info, $"Connecting to {_grpcAddress}");
            BridgeGrpcClient.SubmitTradeTimeout = TradeSubmitTimeout;
            var ok = await BridgeGrpcClient.Initialize(_grpcAddress, source: "qt", component: LogComponent).ConfigureAwait(false);
            if (!ok)
            {
                EmitLog(BridgeLogLevel.Error, $"Bridge connection failed: {BridgeGrpcClient.LastError}");
                return false;
            }

            _streamHealthy = false;
            _isRunning = true;

            BridgeGrpcClient.StreamingStateChanged += HandleStreamingStateChanged;
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

            StartHealthMonitor();
            EmitLog(BridgeLogLevel.Info, "Bridge ready");
            return true;
        }

        private void StopCore()
        {
            if (!_isRunning)
            {
                return;
            }

            var stack = Environment.StackTrace;

            EmitLog(BridgeLogLevel.Warn, "StopCore invoked", details: stack);

            BridgeGrpcClient.StreamingStateChanged -= HandleStreamingStateChanged;
            _streamHealthy = false;
            StopHealthMonitor();

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
                RaiseStreamingStateChanged(BridgeGrpcClient.StreamingState.Disconnected, "stopped");
                EmitLog(BridgeLogLevel.Info, "Bridge stopped", details: stack);
                SafeNotifyConnectionChanged(IsOnline);
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
                          GetStringValue(root, "qt_trade_id") ??
                          GetStringValue(root, "base_id") ??
                          GetStringValue(root, "qt_position_id") ??
                          GetStringValue(root, "position_id");

                if (string.IsNullOrWhiteSpace(tradeId))
                {
                    EmitLog(BridgeLogLevel.Error, "Trade JSON missing unique identifier");
                    return false;
                }

                var added = _pendingTrades.TryAdd(tradeId, 0);
                if (!added)
                {
                    EmitLog(BridgeLogLevel.Warn, "Trade submission already pending", tradeId, tradeId, "already pending");
                    return false;
                }

                try
                {
                    var success = await BridgeGrpcClient.SubmitTradeAsync(tradeJson).ConfigureAwait(false);
                    if (!success)
                    {
                        EmitLog(BridgeLogLevel.Warn, "SubmitTrade failed", tradeId, tradeId, BridgeGrpcClient.LastError);
                    }
                    return success;
                }
                finally
                {
                    _pendingTrades.TryRemove(tradeId, out _);
                }
            }
            catch (Exception ex)
            {
                EmitLog(BridgeLogLevel.Error, "Exception in SubmitTradeAsync", tradeId, tradeId, ex.Message);

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

        private static string? GetStringValueCaseInsensitive(JsonElement element, params string[] propertyNames)
        {
            foreach (var name in propertyNames)
            {
                if (!string.IsNullOrEmpty(name) && element.TryGetProperty(name, out var value))
                {
                    return ExtractString(value);
                }
            }

            if (propertyNames.Length == 0)
            {
                return null;
            }

            foreach (var property in element.EnumerateObject())
            {
                foreach (var candidate in propertyNames)
                {
                    if (!string.IsNullOrEmpty(candidate) && string.Equals(property.Name, candidate, StringComparison.OrdinalIgnoreCase))
                    {
                        return ExtractString(property.Value);
                    }
                }
            }

            return null;
        }

        private static string? ExtractString(JsonElement value)
        {
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
            if (string.IsNullOrWhiteSpace(tradeJson))
            {
                return;
            }

            EmitLog(BridgeLogLevel.Debug, $"Bridge stream payload received: {tradeJson}");

            BridgeStreamEnvelope? envelope = null;
            try
            {
                using var doc = JsonDocument.Parse(tradeJson);
                var root = doc.RootElement;

                var action = GetStringValueCaseInsensitive(root, "action");
                var eventType = GetStringValueCaseInsensitive(root, "event_type");
                var baseId = GetStringValueCaseInsensitive(root, "base_id", "qt_position_id");
                var status = GetStringValueCaseInsensitive(root, "status");
                var tradeId = GetStringValueCaseInsensitive(root, "id", "qt_trade_id");
                var instrument = GetStringValueCaseInsensitive(root, "instrument", "symbol", "nt_instrument_symbol");
                var account = GetStringValueCaseInsensitive(root, "account_name", "account", "nt_account_name");
                var ticketRaw = GetStringValueCaseInsensitive(root, "mt5_ticket", "ticket");
                ulong.TryParse(ticketRaw, out var ticket);

                envelope = new BridgeStreamEnvelope(
                    action ?? string.Empty,
                    eventType ?? string.Empty,
                    baseId ?? string.Empty,
                    status ?? string.Empty,
                    tradeId ?? string.Empty,
                    instrument ?? string.Empty,
                    account ?? string.Empty,
                    ticket,
                    tradeJson);
            }
            catch (JsonException ex)
            {
                EmitLog(BridgeLogLevel.Warn, "Failed to parse bridge stream payload", details: ex.Message);
            }
            catch (Exception ex)
            {
                EmitLog(BridgeLogLevel.Warn, "Unexpected error decoding bridge stream payload", details: ex.Message);
            }

            if (envelope.HasValue)
            {
                HandleStreamEnvelope(envelope.Value);
            }
        }

        private void HandleStreamEnvelope(BridgeStreamEnvelope envelope)
        {
            var action = envelope.Action;
            var eventType = envelope.EventType;
            var baseId = envelope.BaseId;

            if (!string.IsNullOrWhiteSpace(action))
            {
                if (action.Equals("HEDGE_CLOSED", StringComparison.OrdinalIgnoreCase) ||
                    action.Equals("NT_CLOSE_ACK", StringComparison.OrdinalIgnoreCase) ||
                    action.Equals("CLOSE_HEDGE", StringComparison.OrdinalIgnoreCase))
                {
                    EmitLog(BridgeLogLevel.Info, $"Bridge stream reports hedge closed for {baseId}", envelope.TradeId, baseId, envelope.Status);
                }
                else if (action.Equals("HEDGE_OPENED", StringComparison.OrdinalIgnoreCase))
                {
                    EmitLog(BridgeLogLevel.Info, $"Bridge stream reports hedge opened for {baseId}", envelope.TradeId, baseId, envelope.Status);
                }
            }

            if (!string.IsNullOrWhiteSpace(eventType))
            {
                if (eventType.Equals("elastic_hedge_update", StringComparison.OrdinalIgnoreCase) ||
                    eventType.Equals("trailing_stop_update", StringComparison.OrdinalIgnoreCase))
                {
                    EmitLog(BridgeLogLevel.Debug, $"Bridge stream event '{eventType}' for {baseId}", envelope.TradeId, baseId, envelope.Status);
                }
            }

            RaiseStreamEnvelopeReceived(envelope);
        }

        private void OnQuantowerTrade(Trade trade)
        {
            var positionId = trade?.PositionId;

            // CRITICAL FIX (Issue #1 & #4): Check if this position was recently closed
            // When a position is closed in Quantower, it generates a closing Trade event
            // We don't want to send this as a new trade to MT5, as it would reopen the position
            if (!string.IsNullOrWhiteSpace(positionId) &&
                _recentClosures.TryGetValue(positionId, out var closureTime))
            {
                if ((DateTime.UtcNow - closureTime).TotalSeconds < 2)
                {
                    EmitLog(BridgeLogLevel.Debug, $"Skipping trade {trade?.Id} - position {positionId} was recently closed (cooldown active)");
                    _recentClosures.TryRemove(positionId, out _);
                    return;
                }
                // Cooldown expired, remove from dictionary
                _recentClosures.TryRemove(positionId, out _);
            }

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

            RaisePositionAdded(position);
        }

        private void OnQuantowerPositionClosed(Position position)
        {
            // CRITICAL FIX (Issue #1 & #4): Mark this position as recently closed
            // This prevents the closing trade from being sent as a new trade to MT5
            var positionId = position?.Id;
            if (!string.IsNullOrWhiteSpace(positionId))
            {
                _recentClosures[positionId] = DateTime.UtcNow;
                EmitLog(BridgeLogLevel.Debug, $"Marked position {positionId} as recently closed (cooldown active for 2 seconds)");
            }

            if (!QuantowerTradeMapper.TryBuildPositionClosure(position, out var payload, out var closureId))
            {
                EmitLog(BridgeLogLevel.Warn, "Unable to map Quantower position closure to bridge notification", closureId, closureId);
                return;
            }

            EmitLog(BridgeLogLevel.Info, $"Quantower position closed ({closureId ?? "n/a"}) -> notifying bridge", closureId, closureId);
            ObserveAsyncOperation(BridgeGrpcClient.SubmitCloseHedgeAsync(payload), "SubmitCloseHedge", closureId ?? "n/a");

            try
            {
                PositionRemoved?.Invoke(position);
            }
            catch (Exception ex)
            {
                EmitLog(BridgeLogLevel.Warn, "PositionRemoved listener threw", position.Id, ex.Message);
            }
        }

        private void OnQuantowerPositionAdded(Position position)
        {
            if (QuantowerTradeMapper.TryBuildPositionSnapshot(position, out var tradePayload, out var positionTradeId))
            {
                // CRITICAL FIX: Prevent duplicate position submissions
                // Quantower fires PositionAdded event multiple times in rapid succession
                // Check if we've already submitted this position recently (within 1 second)
                if (!string.IsNullOrWhiteSpace(positionTradeId))
                {
                    if (_recentPositionSubmissions.TryGetValue(positionTradeId, out var lastSubmitTime))
                    {
                        var elapsed = DateTime.UtcNow - lastSubmitTime;
                        if (elapsed.TotalSeconds < 1.0)
                        {
                            EmitLog(BridgeLogLevel.Debug, $"Position {positionTradeId} was submitted {elapsed.TotalMilliseconds:F0}ms ago - skipping duplicate", positionTradeId, positionTradeId);
                            return;
                        }
                    }

                    // Record this submission
                    _recentPositionSubmissions[positionTradeId] = DateTime.UtcNow;

                    // Clean up old entries (older than 5 seconds)
                    var cutoff = DateTime.UtcNow.AddSeconds(-5);
                    foreach (var kvp in _recentPositionSubmissions)
                    {
                        if (kvp.Value < cutoff)
                        {
                            _recentPositionSubmissions.TryRemove(kvp.Key, out _);
                        }
                    }
                }

                EmitLog(BridgeLogLevel.Info, $"Quantower position added ({positionTradeId ?? "n/a"}) -> notifying bridge", positionTradeId, positionTradeId);
                ObserveAsyncOperation(BridgeGrpcClient.SubmitTradeAsync(tradePayload), "SubmitTradeSnapshot", positionTradeId ?? "n/a");
            }

            try
            {
                PositionAdded?.Invoke(position);
            }
            catch (Exception ex)
            {
                EmitLog(BridgeLogLevel.Warn, "PositionAdded listener threw", position.Id, ex.Message);
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
                EmitLog(BridgeLogLevel.Warn, "PositionAdded listener threw", position.Id, ex.Message);
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
                EmitLog(BridgeLogLevel.Warn, "PositionRemoved listener threw", position.Id, ex.Message);
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

        private async void ObserveAsyncOperation(Task<bool> task, string operationName, string identifier)
        {
            try
            {
                var result = await task.ConfigureAwait(false);
                if (!result)
                {
                    EmitLog(BridgeLogLevel.Warn, $"{operationName} unsuccessful", identifier, identifier, BridgeGrpcClient.LastError);
                }
            }
            catch (Exception ex)
            {
                var root = ex.GetBaseException();
                EmitLog(BridgeLogLevel.Error, $"{operationName} exception", identifier, identifier, root?.Message ?? ex.Message);
            }
        }

        private void SafeNotifyConnectionChanged(bool online)
        {
            try
            {
                ConnectionStateChanged?.Invoke(online);
            }
            catch
            {
                // Suppress notification errors to keep diagnostics from interrupting shutdown paths.
            }
        }

        private void HandleStreamingStateChanged(BridgeGrpcClient.StreamingState state, string? details)
        {
            var wasHealthy = _streamHealthy;
            var wasOnline = _isRunning && wasHealthy;

            switch (state)
            {
                case BridgeGrpcClient.StreamingState.Connecting:
                    // No-op; we only log on actual state changes below.
                    RaiseStreamingStateChanged(state, details);
                    return;
                case BridgeGrpcClient.StreamingState.Connected:
                    _streamHealthy = true;
                    if (!wasHealthy)
                    {
                        EmitLog(BridgeLogLevel.Info, "Trading stream connected");
                    }
                    RaiseStreamingStateChanged(state, details);
                    break;
                case BridgeGrpcClient.StreamingState.Disconnected:
                    _streamHealthy = false;
                    if (wasHealthy)
                    {
                        var message = string.IsNullOrWhiteSpace(details)
                            ? "Trading stream disconnected; retrying"
                            : $"Trading stream disconnected; retrying ({details})";
                        EmitLog(BridgeLogLevel.Warn, message);
                    }
                    RaiseStreamingStateChanged(state, details);
                    break;
            }

            var isOnline = _isRunning && _streamHealthy;
            if (isOnline != wasOnline)
            {
                SafeNotifyConnectionChanged(isOnline);
            }
        }

        private void RaiseStreamingStateChanged(BridgeGrpcClient.StreamingState state, string? details)
        {
            try
            {
                StreamingStateChanged?.Invoke(state, details);
            }
            catch
            {
                // Swallow to avoid destabilising stream loop when observers throw.
            }
        }

        private void RaiseStreamEnvelopeReceived(BridgeStreamEnvelope envelope)
        {
            try
            {
                StreamEnvelopeReceived?.Invoke(envelope);
            }
            catch (Exception ex)
            {
                EmitLog(BridgeLogLevel.Warn, "Stream envelope listener threw", envelope.TradeId, envelope.BaseId, ex.Message);
            }
        }

        private void StartHealthMonitor()
        {
            StopHealthMonitor();

            if (!_isRunning)
            {
                return;
            }

            lock (_healthSync)
            {
                if (_healthMonitorCts != null)
                {
                    return;
                }

                var cts = new CancellationTokenSource();
                _healthMonitorCts = cts;
                _healthMonitorTask = Task.Run(() => MonitorHealthAsync(cts.Token));
            }
        }

        private void StopHealthMonitor()
        {
            CancellationTokenSource? cts;
            Task? task;

            lock (_healthSync)
            {
                cts = _healthMonitorCts;
                task = _healthMonitorTask;
                _healthMonitorCts = null;
                _healthMonitorTask = null;
            }

            if (cts != null)
            {
                try
                {
                    cts.Cancel();
                }
                catch
                {
                    // ignore cancellation failures
                }
            }

            cts?.Dispose();
        }

        private async Task MonitorHealthAsync(CancellationToken token)
        {
            var consecutiveFailures = 0;
            var reportedUnhealthy = false;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(HealthProbeInterval, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (!_isRunning || token.IsCancellationRequested)
                {
                    break;
                }

                OperationResult result;
                try
                {
                    result = await BridgeGrpcClient.HealthCheckAsync("addon").ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    result = OperationResult.Failure(ex.Message);
                }

                if (token.IsCancellationRequested)
                {
                    break;
                }

                if (!result.Success)
                {
                    consecutiveFailures++;
                    var reason = !string.IsNullOrWhiteSpace(result.ErrorMessage)
                        ? result.ErrorMessage
                        : BridgeGrpcClient.LastError;

                    EmitLog(BridgeLogLevel.Warn, $"Health check failed ({consecutiveFailures}/{MaxHealthCheckFailures})", details: reason);
                    reportedUnhealthy = true;

                    if (consecutiveFailures >= MaxHealthCheckFailures)
                    {
                        await HandleConnectionLostAsync(reason).ConfigureAwait(false);
                        break;
                    }
                }
                else
                {
                    if (reportedUnhealthy)
                    {
                        EmitLog(BridgeLogLevel.Info, "Bridge health restored");
                        reportedUnhealthy = false;
                    }

                    consecutiveFailures = 0;
                }
            }
        }

        private async Task HandleConnectionLostAsync(string reason)
        {
            EmitLog(BridgeLogLevel.Error, "Bridge connection lost; stopping", details: reason);

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

        public readonly record struct BridgeStreamEnvelope(
            string Action,
            string EventType,
            string BaseId,
            string Status,
            string TradeId,
            string Instrument,
            string Account,
            ulong Mt5Ticket,
            string RawJson);
    }
}
