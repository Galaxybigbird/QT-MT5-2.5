using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using Trading.Proto;

namespace Quantower.Bridge.Client
{
    internal sealed class TradingClient : ITradingClient
    {
        private readonly GrpcChannel _channel;
        private readonly TradingService.TradingServiceClient _client;
        private readonly StreamingService.StreamingServiceClient _streamingClient;
        private readonly LoggingService.LoggingServiceClient _loggingClient;
        private static readonly ConcurrentDictionary<string, GrpcChannel> ChannelCache = new();


        private readonly string _source;
        private readonly string _component;
        private readonly object _streamSync = new();

        private CancellationTokenSource? _streamCancellation;
        private Task? _streamTask;

        private bool _disposed;

        public bool IsConnected { get; private set; }
        public string LastError { get; private set; } = string.Empty;

        public TradingClient(string serverAddress, string source, string component)
        {
            _source = string.IsNullOrWhiteSpace(source) ? "qt" : source;
            _component = string.IsNullOrWhiteSpace(component) ? "qt_addon" : component;

            var canonical = serverAddress;
            var channel = ChannelCache.GetOrAdd(canonical, key =>
            {
                var handler = new SocketsHttpHandler
                {
                    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                    KeepAlivePingDelay = TimeSpan.FromSeconds(15),
                    KeepAlivePingTimeout = TimeSpan.FromSeconds(5),
                    EnableMultipleHttp2Connections = true
                };

                return GrpcChannel.ForAddress(key, new GrpcChannelOptions
                {
                    HttpHandler = handler,
                    DisposeHttpClient = true
                });
            });

            _channel = channel;
            _client = new TradingService.TradingServiceClient(_channel);
            _streamingClient = new StreamingService.StreamingServiceClient(_channel);
            _loggingClient = new LoggingService.LoggingServiceClient(_channel);
        }

        public void LogFireAndForget(string level, string component, string message, string tradeId = "", string errorCode = "", string baseId = "", string? correlationId = null)
        {
            try
            {
                var nowNs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L;
                var log = new LogEvent
                {
                    TimestampNs = nowNs,
                    Source = _source,
                    Level = string.IsNullOrWhiteSpace(level) ? "INFO" : level,
                    Component = string.IsNullOrWhiteSpace(component) ? _component : component,
                    Message = message ?? string.Empty,
                    TradeId = tradeId ?? string.Empty,
                    ErrorCode = errorCode ?? string.Empty,
                    BaseId = baseId ?? string.Empty
                };

                if (!string.IsNullOrWhiteSpace(correlationId))
                {
                    log.Tags["correlation_id"] = correlationId;
                }

                var call = _loggingClient.LogAsync(log);
                _ = ObserveLoggingAsync(call.ResponseAsync);
            }
            catch
            {
                // Logging failures are non-critical, swallow to avoid cascading issues.
            }
        }

        public async Task<OperationResult> SubmitTradeAsync(string tradeJson)
        {
            try
            {
                var trade = JsonToProtoTrade(tradeJson);
                if (trade == null)
                {
                    return OperationResult.Failure("Invalid trade payload");
                }
                var deadline = DateTime.UtcNow.AddSeconds(10);
                var response = await _client.SubmitTradeAsync(trade, deadline: deadline).ConfigureAwait(false);
                return OperationResult.Ok(JsonSerializer.Serialize(new { status = response.Status, message = response.Message }));
            }
            catch (Exception ex)
            {
                return OperationResult.Failure(ex.Message);
            }
        }

        public async Task<OperationResult> HealthCheckAsync(string source)
        {
            try
            {
                var effectiveSource = string.IsNullOrWhiteSpace(source) ? _source : source;
                var request = new HealthRequest { Source = effectiveSource };
                var deadline = DateTime.UtcNow.AddSeconds(3);
                var response = await _client.HealthCheckAsync(request, deadline: deadline).ConfigureAwait(false);
                IsConnected = response.Status == "healthy";

                var responseJson = JsonSerializer.Serialize(new
                {
                    status = response.Status,
                    queue_size = response.QueueSize,
                    net_position = response.NetPosition,
                    hedge_size = response.HedgeSize
                });

                var result = response.Status == "healthy"
                    ? OperationResult.Ok(responseJson)
                    : OperationResult.Failure($"Server status: {response.Status}");

                if (string.IsNullOrEmpty(result.ResponseJson))
                {
                    result.ResponseJson = responseJson;
                }

                return result;
            }
            catch (Exception ex)
            {
                IsConnected = false;
                return OperationResult.Failure(ex.Message);
            }
        }

        public async Task<OperationResult> SubmitElasticUpdateAsync(string updateJson)
        {
            try
            {
                var update = JsonToProtoElasticUpdate(updateJson);
                var deadline = DateTime.UtcNow.AddSeconds(5);
                var response = await _client.SubmitElasticUpdateAsync(update, deadline: deadline).ConfigureAwait(false);
                return response.Status == "success" ? OperationResult.Ok() : OperationResult.Failure(response.Message);
            }
            catch (Exception ex)
            {
                return OperationResult.Failure(ex.Message);
            }
        }

        public async Task<OperationResult> SubmitTrailingUpdateAsync(string updateJson)
        {
            try
            {
                var update = JsonToProtoTrailingUpdate(updateJson);
                var deadline = DateTime.UtcNow.AddSeconds(30);
                var response = await _client.SubmitTrailingUpdateAsync(update, deadline: deadline).ConfigureAwait(false);
                return response.Status == "success" ? OperationResult.Ok() : OperationResult.Failure(response.Message);
            }
            catch (Exception ex)
            {
                return OperationResult.Failure(ex.Message);
            }
        }

        public async Task<OperationResult> NotifyHedgeCloseAsync(string notificationJson)
        {
            try
            {
                var request = JsonToProtoHedgeClose(notificationJson);
                var deadline = DateTime.UtcNow.AddSeconds(5);
                var response = await _client.NotifyHedgeCloseAsync(request, deadline: deadline).ConfigureAwait(false);
                return response.Status == "success" ? OperationResult.Ok() : OperationResult.Failure(response.Message);
            }
            catch (Exception ex)
            {
                return OperationResult.Failure(ex.Message);
            }
        }

        public async Task<OperationResult> SubmitCloseHedgeAsync(string notificationJson)
        {
            try
            {
                var request = JsonToProtoHedgeClose(notificationJson);
                var response = await CloseHedgeInternalAsync(request).ConfigureAwait(false);
                return response.Status == "success" ? OperationResult.Ok() : OperationResult.Failure(response.Message);
            }
            catch (Exception ex)
            {
                return OperationResult.Failure(ex.Message);
            }
        }

        private async Task<GenericResponse> CloseHedgeInternalAsync(HedgeCloseNotification request)
        {
            var call = _client.SubmitCloseHedgeAsync(request, deadline: DateTime.UtcNow.AddSeconds(10));
            return await call.ResponseAsync.ConfigureAwait(false);
        }

        public void StartTradingStream(Action<string>? onTradeReceived)
        {
            StopTradingStream();

            var cts = new CancellationTokenSource();
            var token = cts.Token;

            var streamTask = Task.Run(async () =>
            {
                var rand = new Random();
                var attempt = 0;

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        using var stream = _streamingClient.TradingStream(cancellationToken: token);
                        await stream.RequestStream.WriteAsync(new Trade { Id = "init_stream" }).ConfigureAwait(false);
                        attempt = 0;

                        while (await stream.ResponseStream.MoveNext(token).ConfigureAwait(false))
                        {
                            var trade = stream.ResponseStream.Current;
                            var tradeJson = ProtoTradeToJson(trade);
                            onTradeReceived?.Invoke(tradeJson);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch
                    {
                        // transient errors – fall through to backoff and retry
                    }

                    attempt++;
                    var delayMs = Math.Min(5000, (int)(Math.Pow(2, Math.Min(6, attempt)) * 100) + rand.Next(0, 250));
                    try
                    {
                        await Task.Delay(delayMs, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }, token);

            lock (_streamSync)
            {
                _streamCancellation = cts;
                _streamTask = streamTask;
            }
        }

        public void StopTradingStream()
        {
            CancellationTokenSource? toCancel;
            Task? toAwait;

            lock (_streamSync)
            {
                toCancel = _streamCancellation;
                _streamCancellation = null;
                toAwait = _streamTask;
                _streamTask = null;
            }

            if (toCancel != null)
            {
                try
                {
                    toCancel.Cancel();
                }
                catch (Exception ex)
                {
                    LogFireAndForget("WARN", _component, $"Failed to cancel trading stream: {ex.Message}");
                }
            }

            if (toAwait != null)
            {
                try
                {
                    toAwait.Wait(2000);
                }
                catch (Exception ex)
                {
                    LogFireAndForget("WARN", _component, $"Failed to stop trading stream task: {ex.Message}");
                }
            }

            if (toCancel != null)
            {
                try
                {
                    toCancel.Dispose();
                }
                catch (Exception ex)
                {
                    LogFireAndForget("DEBUG", _component, $"Error disposing cancellation source: {ex.Message}");
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            StopTradingStream();
            // Channel is shared via cache; do not dispose here to allow reuse
            GC.SuppressFinalize(this);
        }

        private static async Task ObserveLoggingAsync(Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
                // Suppress logging exceptions to keep the fire-and-forget semantics.
            }
        }

        #region JSON ↔ Proto helpers

        private Trade? JsonToProtoTrade(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                LogFireAndForget("ERROR", _component, "Trade payload is empty");
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var action = GetStringValue(root, "action");
                if (string.IsNullOrWhiteSpace(action))
                {
                    LogFireAndForget("ERROR", _component, "Trade JSON missing action field");
                    return null;
                }

                return new Trade
                {
                    Id = GetStringValue(root, "id", "trade_id"),
                    BaseId = GetStringValue(root, "base_id", "qt_position_id", "position_id"),
                    Timestamp = GetInt64Value(root, "timestamp"),
                    Action = action,
                    Quantity = GetDoubleValue(root, "quantity"),
                    Price = GetDoubleValue(root, "price"),
                    TotalQuantity = GetInt32Value(root, "total_quantity"),
                    ContractNum = GetInt32Value(root, "contract_num"),
                    OrderType = GetStringValue(root, "order_type"),
                    MeasurementPips = GetInt32Value(root, "measurement_pips", "measurement", "pips"),
                    RawMeasurement = GetDoubleValue(root, "raw_measurement"),
                    Instrument = GetStringValue(root, "instrument", "instrument_name", "symbol"),
                    AccountName = GetStringValue(root, "account_name", "account"),
                    NtBalance = GetDoubleValue(root, "nt_balance"),
                    NtDailyPnl = GetDoubleValue(root, "nt_daily_pnl"),
                    NtTradeResult = GetStringValue(root, "nt_trade_result"),
                    NtSessionTrades = GetInt32Value(root, "nt_session_trades"),
                    NtPointsPer1KLoss = GetDoubleValue(root, "nt_points_per_1k_loss"),
                    QtTradeId = GetStringValue(root, "qt_trade_id", "quantower_trade_id"),
                    QtPositionId = GetStringValue(root, "qt_position_id", "quantower_position_id", "position_id"),
                    StrategyTag = GetStringValue(root, "strategy_tag", "strategy"),
                    OriginPlatform = GetStringValue(root, "origin_platform", "source_platform", "platform", "origin")
                };
            }
            catch (JsonException ex)
            {
                LogFireAndForget("ERROR", _component, $"Failed to parse trade JSON: {ex.Message}");
                return null;
            }
            catch (Exception ex)
            {
                LogFireAndForget("ERROR", _component, $"Unexpected error parsing trade JSON: {ex.Message}");
                return null;
            }
        }

        private string ProtoTradeToJson(Trade trade)
        {
            var data = new
            {
                id = trade.Id,
                base_id = trade.BaseId,
                timestamp = trade.Timestamp,
                action = trade.Action,
                quantity = trade.Quantity,
                price = trade.Price,
                total_quantity = trade.TotalQuantity,
                contract_num = trade.ContractNum,
                order_type = trade.OrderType,
                measurement_pips = trade.MeasurementPips,
                raw_measurement = trade.RawMeasurement,
                instrument = trade.Instrument,
                instrument_name = trade.Instrument,
                account_name = trade.AccountName,
                nt_balance = trade.NtBalance,
                nt_daily_pnl = trade.NtDailyPnl,
                nt_trade_result = trade.NtTradeResult,
                nt_session_trades = trade.NtSessionTrades,
                mt5_ticket = trade.Mt5Ticket,
                nt_points_per_1k_loss = trade.NtPointsPer1KLoss,
                qt_trade_id = trade.QtTradeId,
                qt_position_id = trade.QtPositionId,
                strategy_tag = trade.StrategyTag,
                origin_platform = trade.OriginPlatform
            };

            return JsonSerializer.Serialize(data);
        }

        private ElasticHedgeUpdate JsonToProtoElasticUpdate(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            double currentProfit = GetDoubleValue(root, "current_profit", "elastic_current_profit", "price");
            int profitLevel = GetInt32Value(root, "profit_level", "elastic_profit_level", "level", "volume");

            return new ElasticHedgeUpdate
            {
                EventType = GetStringValue(root, "event_type"),
                Action = GetStringValue(root, "action"),
                BaseId = GetStringValue(root, "base_id", "qt_position_id", "position_id"),
                CurrentProfit = currentProfit,
                ProfitLevel = profitLevel,
                Timestamp = GetStringValue(root, "timestamp"),
                Mt5Ticket = GetUInt64Value(root, "mt5_ticket")
            };
        }

        private TrailingStopUpdate JsonToProtoTrailingUpdate(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            return new TrailingStopUpdate
            {
                EventType = GetStringValue(root, "event_type"),
                BaseId = GetStringValue(root, "base_id", "qt_position_id", "position_id"),
                NewStopPrice = GetDoubleValue(root, "new_stop_price"),
                TrailingType = GetStringValue(root, "trailing_type"),
                CurrentPrice = GetDoubleValue(root, "current_price"),
                Timestamp = GetStringValue(root, "timestamp"),
                Mt5Ticket = GetUInt64Value(root, "mt5_ticket")
            };
        }

        private HedgeCloseNotification JsonToProtoHedgeClose(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            return new HedgeCloseNotification
            {
                EventType = GetStringValue(root, "event_type"),
                BaseId = GetStringValue(root, "base_id", "qt_position_id", "position_id"),
                NtInstrumentSymbol = GetStringValue(root, "nt_instrument_symbol", "instrument_symbol", "instrument", "symbol"),
                NtAccountName = GetStringValue(root, "nt_account_name", "account_name", "account"),
                ClosedHedgeQuantity = GetDoubleValue(root, "closed_hedge_quantity", "quantity"),
                ClosedHedgeAction = GetStringValue(root, "closed_hedge_action", "action"),
                Timestamp = GetStringValue(root, "timestamp"),
                ClosureReason = GetStringValue(root, "closure_reason", "reason"),
                Mt5Ticket = GetUInt64Value(root, "mt5_ticket"),
                QtPositionId = GetStringValue(root, "qt_position_id", "position_id"),
                QtTradeId = GetStringValue(root, "qt_trade_id", "trade_id")
            };
        }

        private static string GetStringValue(JsonElement element, params string[] propertyNames)
        {
            foreach (var name in propertyNames)
            {
                if (!element.TryGetProperty(name, out var value))
                {
                    continue;
                }

                return value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString() ?? string.Empty,
                    JsonValueKind.Number => value.ToString(),
                    JsonValueKind.True => bool.TrueString,
                    JsonValueKind.False => bool.FalseString,
                    _ => string.Empty
                };
            }
            return string.Empty;
        }

        private static double GetDoubleValue(JsonElement element, params string[] propertyNames)
        {
            foreach (var name in propertyNames)
            {
                if (!element.TryGetProperty(name, out var value))
                {
                    continue;
                }

                if (value.ValueKind == JsonValueKind.Number)
                {
                    return value.GetDouble();
                }

                if (value.ValueKind == JsonValueKind.String && double.TryParse(
                        value.GetString(),
                        NumberStyles.Float | NumberStyles.AllowThousands,
                        CultureInfo.InvariantCulture,
                        out var parsed))
                {
                    return parsed;
                }
            }
            return 0;
        }

        private static int GetInt32Value(JsonElement element, params string[] propertyNames)
        {
            foreach (var name in propertyNames)
            {
                if (!element.TryGetProperty(name, out var value))
                {
                    continue;
                }

                if (value.ValueKind == JsonValueKind.Number)
                {
                    return value.GetInt32();
                }

                if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed))
                {
                    return parsed;
                }
            }
            return 0;
        }

        private static long GetInt64Value(JsonElement element, params string[] propertyNames)
        {
            foreach (var name in propertyNames)
            {
                if (!element.TryGetProperty(name, out var value))
                {
                    continue;
                }

                if (value.ValueKind == JsonValueKind.Number)
                {
                    return value.GetInt64();
                }

                if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out var parsed))
                {
                    return parsed;
                }
            }
            return 0;
        }

        private static ulong GetUInt64Value(JsonElement element, params string[] propertyNames)
        {
            foreach (var name in propertyNames)
            {
                if (!element.TryGetProperty(name, out var value))
                {
                    continue;
                }

                if (value.ValueKind == JsonValueKind.Number)
                {
                    return value.GetUInt64();
                }

                if (value.ValueKind == JsonValueKind.String && ulong.TryParse(value.GetString(), out var parsed))
                {
                    return parsed;
                }
            }
            return 0;
        }

        #endregion
    }
}
