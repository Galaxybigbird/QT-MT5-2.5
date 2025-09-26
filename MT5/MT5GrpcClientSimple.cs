using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Net.Client;
using Trading.Proto;

namespace MT5GrpcClient
{
    /// <summary>
    /// Simplified MT5 gRPC Client for C++ wrapper integration
    /// Methods designed to be called via .NET runtime hosting
    /// </summary>
    public static class GrpcClientWrapper
    {
        private static GrpcChannel _channel;
        private static TradingService.TradingServiceClient _client;
        private static StreamingService.StreamingServiceClient _streamingClient;
        private static ConcurrentQueue<string> _tradeQueue = new ConcurrentQueue<string>();
        private static volatile CancellationTokenSource _cancellationTokenSource;
        private static readonly object _ctsSync = new object();
        private static Task _streamingTask;
        private static volatile bool _isInitialized;
        private static volatile bool _isStreamingActive;
        private static string _serverAddress = "";
        private static DateTime _lastHealthCheck = DateTime.MinValue;
        private static volatile bool _isConnected;

        // Error codes for MT5
        private const int ERROR_SUCCESS = 0;
        private const int ERROR_INIT_FAILED = -1;
        private const int ERROR_NOT_INITIALIZED = -2;
        private const int ERROR_CONNECTION_FAILED = -3;
        private const int ERROR_STREAM_FAILED = -4;
        private const int ERROR_INVALID_PARAMS = -5;
        private const int ERROR_TIMEOUT = -6;
        private const int ERROR_SERIALIZATION = -7;
        private const int ERROR_CLEANUP_FAILED = -8;

        private static async Task StopStreamingAsync()
        {
            CancellationTokenSource cts = null;
            Task streamingTask = null;

            lock (_ctsSync)
            {
                cts = _cancellationTokenSource;
                _cancellationTokenSource = null;
                streamingTask = _streamingTask;
                _streamingTask = null;
                _isStreamingActive = false;
            }

            if (cts != null)
            {
                try { cts.Cancel(); } catch { }
                cts.Dispose();
            }

            if (streamingTask != null)
            {
                try { await streamingTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch { }
            }
        }

        private static CancellationToken SetCancellationTokenSource(CancellationTokenSource cts)
        {
            CancellationTokenSource previous = null;
            lock (_ctsSync)
            {
                previous = _cancellationTokenSource;
                _cancellationTokenSource = cts;
            }

            if (previous != null)
            {
                try { previous.Cancel(); } catch { }
                previous.Dispose();
            }

            return cts.Token;
        }

        private static void ClearCancellationTokenSource()
        {
            CancellationTokenSource existing = null;
            lock (_ctsSync)
            {
                existing = _cancellationTokenSource;
                _cancellationTokenSource = null;
            }

            if (existing != null)
            {
                try { existing.Cancel(); } catch { }
                existing.Dispose();
            }
        }

        /// <summary>
        /// Initialize gRPC client connection to Bridge Server
        /// Called via C++ wrapper: GrpcInitialize(serverAddress, port)
        /// </summary>
        public static int GrpcInitialize(string args)
        {
            try
            {
                // Parse arguments: "serverAddress,port"
                var parts = args.Split(',');
                if (parts.Length != 2) return ERROR_INVALID_PARAMS;
                
                string serverAddress = parts[0];
                if (!int.TryParse(parts[1], out int port)) return ERROR_INVALID_PARAMS;

                if (_isInitialized)
                {
                    GrpcCleanup();
                }

                _serverAddress = $"http://{serverAddress}:{port}";
                
                // Create gRPC channel with connection options
                var channelOptions = new GrpcChannelOptions
                {
                    MaxReceiveMessageSize = 1024 * 1024, // 1MB
                    MaxSendMessageSize = 1024 * 1024,    // 1MB
                };

                _channel = GrpcChannel.ForAddress(_serverAddress, channelOptions);
                _client = new TradingService.TradingServiceClient(_channel);
                _streamingClient = new StreamingService.StreamingServiceClient(_channel);
                
                SetCancellationTokenSource(new CancellationTokenSource());
                _tradeQueue = new ConcurrentQueue<string>();
                
                // Test connection with health check
                var healthRequest = new HealthRequest 
                { 
                    Source = "MT5_EA",
                    OpenPositions = 0
                };

                var healthResponse = _client.HealthCheck(healthRequest);
                if (healthResponse.Status == "healthy")
                {
                    _isInitialized = true;
                    _isConnected = true;
                    _lastHealthCheck = DateTime.UtcNow;
                    return ERROR_SUCCESS;
                }

                return ERROR_CONNECTION_FAILED;
            }
            catch (Exception)
            {
                return ERROR_INIT_FAILED;
            }
        }

        /// <summary>
        /// Start gRPC streaming for real-time trade updates
        /// </summary>
        public static int GrpcStartTradeStream(string args = "")
        {
            try
            {
                if (!_isInitialized || _client == null || _streamingClient == null)
                    return ERROR_NOT_INITIALIZED;

                if (_isStreamingActive)
                    return ERROR_SUCCESS; // Already streaming

                StopStreamingAsync().GetAwaiter().GetResult();

                var token = SetCancellationTokenSource(new CancellationTokenSource());
                lock (_ctsSync)
                {
                    _isStreamingActive = true;
                }
                var task = Task.Run(async () =>
                {
                    try
                    {
                        while (_isStreamingActive && !token.IsCancellationRequested)
                        {
                            try
                            {
                                using var call = _client.GetTrades(
                                    deadline: DateTime.UtcNow.AddSeconds(30),
                                    cancellationToken: token);
                                var heartbeatTask = Task.Run(async () =>
                                {
                                    try
                                    {
                                        while (_isStreamingActive && !token.IsCancellationRequested)
                                        {
                                            await call.RequestStream.WriteAsync(new GetTradesRequest
                                            {
                                                Source = "MT5_EA",
                                                OpenPositions = 0
                                            }).ConfigureAwait(false);

                                            await Task.Delay(1000, token).ConfigureAwait(false);
                                        }
                                    }
                                    catch (OperationCanceledException)
                                    {
                                        // expected on shutdown
                                    }
                                    catch
                                    {
                                        // swallow other transient errors; the outer loop will reconnect
                                    }
                                    finally
                                    {
                                        try { await call.RequestStream.CompleteAsync().ConfigureAwait(false); } catch { }
                                    }
                                }, token);

                                while (_isStreamingActive && !token.IsCancellationRequested &&
                                       await call.ResponseStream.MoveNext(token).ConfigureAwait(false))
                                {
                                    var trade = call.ResponseStream.Current;
                                    var tradeJson = JsonSerializer.Serialize(new
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
                                        account_name = trade.AccountName,
                                        nt_balance = trade.NtBalance,
                                        nt_daily_pnl = trade.NtDailyPnl,
                                        nt_trade_result = trade.NtTradeResult,
                                        nt_session_trades = trade.NtSessionTrades,
                                        mt5_ticket = trade.Mt5Ticket
                                    });

                                    _tradeQueue.Enqueue(tradeJson);
                                }

                                try { await heartbeatTask.ConfigureAwait(false); } catch { }
                            }
                            catch (OperationCanceledException)
                            {
                                break;
                            }
                            catch
                            {
                                if (!_isStreamingActive || token.IsCancellationRequested)
                                {
                                    break;
                                }

                                await Task.Delay(5000, token).ConfigureAwait(false);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected when cancellation is requested
                    }
                });

                lock (_ctsSync)
                {
                    _streamingTask = task;
                }

                return ERROR_SUCCESS;
            }
            catch (Exception)
            {
                StopStreamingAsync().GetAwaiter().GetResult();
                return ERROR_STREAM_FAILED;
            }
        }

        /// <summary>
        /// Get next trade from the streaming queue
        /// Note: For C++ wrapper, return value only (JSON returned via different mechanism)
        /// </summary>
        public static int GrpcGetNextTrade(string args)
        {
            try
            {
                if (!_isInitialized)
                    return ERROR_NOT_INITIALIZED;

                if (_tradeQueue.TryDequeue(out var trade))
                {
                    // In real implementation, this would be passed back to C++
                    // For now, just return success
                    return ERROR_SUCCESS;
                }

                return ERROR_TIMEOUT; // No trades available
            }
            catch (Exception)
            {
                return ERROR_SERIALIZATION;
            }
        }
        /// <summary>
        /// Submit trade execution result to Bridge Server
        /// </summary>
        public static int GrpcSubmitTradeResult(string resultJson)
        {
            try
            {
                if (!_isInitialized || _client == null)
                    return ERROR_NOT_INITIALIZED;

                // Parse JSON to extract trade result fields
                var resultData = JsonSerializer.Deserialize<JsonElement>(resultJson);
                
                var tradeResult = new MT5TradeResult
                {
                    Status = resultData.GetProperty("status").GetString() ?? "",
                    Ticket = resultData.GetProperty("ticket").GetUInt64(),
                    Volume = resultData.GetProperty("volume").GetDouble(),
                    IsClose = resultData.GetProperty("is_close").GetBoolean(),
                    Id = resultData.GetProperty("id").GetString() ?? ""
                };

                var response = _client.SubmitTradeResult(tradeResult);
                
                if (response.Status == "success")
                {
                    return ERROR_SUCCESS;
                }

                return ERROR_CONNECTION_FAILED;
            }
            catch (Exception)
            {
                return ERROR_SERIALIZATION;
            }
        }

        /// <summary>
        /// Perform health check with Bridge Server
        /// </summary>
        public static int GrpcHealthCheck(string args)
        {
            try
            {
                if (!_isInitialized || _client == null)
                    return ERROR_NOT_INITIALIZED;

                // Parse arguments: "requestJson,bufferSize"
                var parts = args.Split(',');
                string requestJson = parts.Length > 0 ? parts[0] : "{}";

                int openPositions = 0;
                try
                {
                    var requestData = JsonSerializer.Deserialize<JsonElement>(requestJson);
                    if (requestData.TryGetProperty("open_positions", out var posElement))
                    {
                        openPositions = posElement.GetInt32();
                    }
                }
                catch
                {
                    // Use default value if parsing fails
                }

                var healthRequest = new HealthRequest
                {
                    Source = "MT5_EA",
                    OpenPositions = openPositions
                };

                var response = _client.HealthCheck(healthRequest);
                
                if (response.Status == "healthy")
                {
                    _isConnected = true;
                    _lastHealthCheck = DateTime.UtcNow;
                    return ERROR_SUCCESS;
                }

                _isConnected = false;
                return ERROR_CONNECTION_FAILED;
            }
            catch (Exception)
            {
                _isConnected = false;
                return ERROR_CONNECTION_FAILED;
            }
        }

        /// <summary>
        /// Submit hedge close notification to Bridge Server
        /// </summary>
        public static int GrpcNotifyHedgeClose(string notificationJson)
        {
            try
            {
                if (!_isInitialized || _client == null)
                    return ERROR_NOT_INITIALIZED;

                var notificationData = JsonSerializer.Deserialize<JsonElement>(notificationJson);
                
                var hedgeNotification = new HedgeCloseNotification
                {
                    EventType = notificationData.GetProperty("event_type").GetString() ?? "",
                    BaseId = notificationData.GetProperty("base_id").GetString() ?? "",
                    NtInstrumentSymbol = notificationData.GetProperty("nt_instrument_symbol").GetString() ?? "",
                    NtAccountName = notificationData.GetProperty("nt_account_name").GetString() ?? "",
                    ClosedHedgeQuantity = notificationData.GetProperty("closed_hedge_quantity").GetDouble(),
                    ClosedHedgeAction = notificationData.GetProperty("closed_hedge_action").GetString() ?? "",
                    Timestamp = notificationData.GetProperty("timestamp").GetString() ?? "",
                    ClosureReason = notificationData.GetProperty("closure_reason").GetString() ?? ""
                };

                var response = _client.NotifyHedgeClose(hedgeNotification);
                
                return response.Status == "success" ? ERROR_SUCCESS : ERROR_CONNECTION_FAILED;
            }
            catch (Exception)
            {
                return ERROR_SERIALIZATION;
            }
        }

        /// <summary>
        /// Get connection status
        /// </summary>
        public static int GrpcIsConnected(string args = "")
        {
            return _isConnected ? 1 : 0;
        }

        /// <summary>
        /// Get number of trades in queue
        /// </summary>
        public static int GrpcGetTradeQueueSize(string args = "")
        {
            return _tradeQueue.Count;
        }

        /// <summary>
        /// Stop gRPC trade streaming
        /// </summary>
        public static int GrpcStopTradeStream(string args = "")
        {
            try
            {
                StopStreamingAsync().GetAwaiter().GetResult();
                return ERROR_SUCCESS;
            }
            catch (Exception)
            {
                return ERROR_STREAM_FAILED;
            }
        }

        /// <summary>
        /// Shutdown gRPC client connection
        /// </summary>
        public static int GrpcShutdown(string args = "")
        {
            return GrpcCleanup();
        }

        /// <summary>
        /// Reconnect gRPC client
        /// </summary>
        public static int GrpcReconnect(string args = "")
        {
            try
            {
                GrpcCleanup();
                _isConnected = false;
                return ERROR_SUCCESS;
            }
            catch (Exception)
            {
                return ERROR_CONNECTION_FAILED;
            }
        }

        /// <summary>
        /// Submit elastic update to Bridge Server
        /// </summary>
        public static int GrpcSubmitElasticUpdate(string updateJson)
        {
            try
            {
                if (!_isInitialized || _client == null)
                    return ERROR_NOT_INITIALIZED;

                var updateData = JsonSerializer.Deserialize<JsonElement>(updateJson);
                
                var elasticUpdate = new ElasticHedgeUpdate
                {
                    EventType = updateData.GetProperty("event_type").GetString() ?? "elastic_update",
                    Action = updateData.GetProperty("action").GetString() ?? "",
                    BaseId = updateData.GetProperty("base_id").GetString() ?? "",
                    CurrentProfit = updateData.GetProperty("current_profit").GetDouble(),
                    ProfitLevel = updateData.GetProperty("profit_level").GetInt32(),
                    Timestamp = updateData.GetProperty("timestamp").GetString() ?? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                };

                var response = _client.SubmitElasticUpdate(elasticUpdate);
                
                return response.Status == "success" ? ERROR_SUCCESS : ERROR_CONNECTION_FAILED;
            }
            catch (Exception)
            {
                return ERROR_SERIALIZATION;
            }
        }

        /// <summary>
        /// Submit trailing stop update to Bridge Server
        /// </summary>
        public static int GrpcSubmitTrailingUpdate(string updateJson)
        {
            try
            {
                if (!_isInitialized || _client == null)
                    return ERROR_NOT_INITIALIZED;

                var updateData = JsonSerializer.Deserialize<JsonElement>(updateJson);
                
                var trailingUpdate = new TrailingStopUpdate
                {
                    EventType = updateData.GetProperty("event_type").GetString() ?? "trailing_update",
                    BaseId = updateData.GetProperty("base_id").GetString() ?? "",
                    NewStopPrice = updateData.GetProperty("new_stop_price").GetDouble(),
                    TrailingType = updateData.GetProperty("trailing_type").GetString() ?? "",
                    CurrentPrice = updateData.GetProperty("current_price").GetDouble(),
                    Timestamp = updateData.GetProperty("timestamp").GetString() ?? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                };

                var response = _client.SubmitTrailingUpdate(trailingUpdate);
                
                return response.Status == "success" ? ERROR_SUCCESS : ERROR_CONNECTION_FAILED;
            }
            catch (Exception)
            {
                return ERROR_SERIALIZATION;
            }
        }

        /// <summary>
        /// Cleanup gRPC client and release resources
        /// </summary>
        private static readonly TimeSpan StreamingShutdownTimeout = TimeSpan.FromSeconds(5);

        private static void ObserveTask(Task task)
        {
            _ = task.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    // Surface exception for diagnostic purposes without throwing on the cleanup path.
                    System.Diagnostics.Trace.TraceWarning($"MT5 streaming task faulted: {t.Exception}");
                }

                t.Dispose();
            }, TaskScheduler.Default);
        }

        private static int GrpcCleanup()
        {
            try
            {
                _isStreamingActive = false;

                ClearCancellationTokenSource();

                if (_streamingTask != null)
                {
                    var task = _streamingTask;
                    _streamingTask = null;

                    try
                    {
                        var completion = Task.WhenAny(task, Task.Delay(StreamingShutdownTimeout));
                        Task.Run(async () => await completion.ConfigureAwait(false)).Wait(StreamingShutdownTimeout);
                        if (task.IsCompleted)
                        {
                            task.ConfigureAwait(false).GetAwaiter().GetResult();
                            task.Dispose();
                        }
                        else
                        {
                            ObserveTask(task);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Trace.TraceWarning($"MT5 streaming shutdown encountered an error: {ex}");
                        ObserveTask(task);
                    }
                }

                if (_channel != null)
                {
                    _channel.Dispose();
                    _channel = null;
                }

                _client = null;
                _streamingClient = null;
                _tradeQueue = new ConcurrentQueue<string>();
                _isInitialized = false;
                _isConnected = false;

                return ERROR_SUCCESS;
            }
            catch (Exception)
            {
                return ERROR_CLEANUP_FAILED;
            }
        }
    }
}
