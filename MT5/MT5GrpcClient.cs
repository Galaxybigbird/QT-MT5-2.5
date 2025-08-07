using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Net.Client;
using Trading.Proto;
using System.Runtime.InteropServices;
using System.IO;

namespace MT5GrpcClient
{
    /// <summary>
    /// MT5 gRPC Client DLL for pure gRPC communication with Bridge Server
    /// Exports unmanaged functions for MQL5 integration
    /// </summary>
    public static class GrpcClientWrapper
    {
        private static GrpcChannel? _channel;
        private static TradingService.TradingServiceClient? _client;
        private static StreamingService.StreamingServiceClient? _streamingClient;
        private static ConcurrentQueue<string> _tradeQueue = new();
        private static CancellationTokenSource? _cancellationTokenSource;
        private static Task? _streamingTask;
        private static bool _isInitialized;
        private static bool _isStreamingActive;
        private static string _serverAddress = "";
        private static DateTime _lastHealthCheck = DateTime.MinValue;
        private static bool _isConnected;
        
        // Global variables for the C++ wrapper to access string data
        private static string _lastTradeJson = "";
        private static string _lastErrorMessage = "";
        private static readonly object _lockObject = new object();
        private static readonly string _tempTradeFile = Path.Combine(Path.GetTempPath(), "mt5_grpc_trade.json");

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

        /// <summary>
        /// Simple test function to verify DLL exports are working
        /// </summary>
        /// <returns>Always returns 42 for testing</returns>
        public static int TestFunction()
        {
            return 42;
        }

        /// <summary>
        /// Initialize gRPC client connection to Bridge Server
        /// </summary>
        /// <param name="serverAddress">Server address (e.g., "127.0.0.1")</param>
        /// <param name="port">Server port (e.g., 50051)</param>
        /// <returns>0 on success, negative error code on failure</returns>
        
        public static int GrpcInitialize([MarshalAs(UnmanagedType.LPStr)] string serverAddress, int port)
        {
            try
            {
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
                
                _cancellationTokenSource = new CancellationTokenSource();
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
        /// <returns>0 on success, negative error code on failure</returns>
        
        public static int GrpcStartTradeStream()
        {
            try
            {
                if (!_isInitialized || _client == null || _streamingClient == null)
                    return ERROR_NOT_INITIALIZED;

                if (_isStreamingActive)
                    return ERROR_SUCCESS; // Already streaming

                _streamingTask = Task.Run(async () =>
                {
                    try
                    {
                        while (!_cancellationTokenSource?.Token.IsCancellationRequested ?? false)
                        {
                            try
                            {
                                // Use bidirectional streaming as defined in protobuf
                                var call = _client.GetTrades(cancellationToken: _cancellationTokenSource.Token);
                                
                                // Send periodic health requests to keep stream alive
                                _ = Task.Run(async () =>
                                {
                                    try
                                    {
                                        while (!_cancellationTokenSource.Token.IsCancellationRequested)
                                        {
                                            await call.RequestStream.WriteAsync(new HealthRequest
                                            {
                                                Source = "MT5_EA",
                                                OpenPositions = 0
                                            });
                                            
                                            await Task.Delay(1000, _cancellationTokenSource.Token); // Send heartbeat every second
                                        }
                                    }
                                    catch (Exception)
                                    {
                                        // Connection lost or stream closed
                                    }
                                    finally
                                    {
                                        await call.RequestStream.CompleteAsync();
                                    }
                                }, _cancellationTokenSource.Token);
                                
                                // Read incoming trades from the response stream
                                while (await call.ResponseStream.MoveNext(_cancellationTokenSource.Token))
                                {
                                    var trade = call.ResponseStream.Current;
                                    // Convert trade to JSON and add to queue
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
                                        nt_session_trades = trade.NtSessionTrades
                                    });

                                    _tradeQueue.Enqueue(tradeJson);
                                }
                            }
                            catch (Exception ex)
                            {
                                // Connection lost, try to reconnect after delay
                                await Task.Delay(5000, _cancellationTokenSource.Token);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected when cancellation is requested
                    }
                });

                _isStreamingActive = true;
                return ERROR_SUCCESS;
            }
            catch (Exception)
            {
                return ERROR_STREAM_FAILED;
            }
        }

        /// <summary>
        /// Get next trade from the streaming queue (writes result to temp file for C++ access)
        /// </summary>
        /// <param name="bufferSize">Size of the output buffer</param>
        /// <returns>0 on success, negative error code on failure</returns>
        
        public static int GrpcGetNextTrade(int bufferSize)
        {
            try
            {
                if (!_isInitialized)
                    return ERROR_NOT_INITIALIZED;

                lock (_lockObject)
                {
                    if (_tradeQueue.TryDequeue(out var trade))
                    {
                        if (trade.Length > bufferSize - 1)
                            return ERROR_INVALID_PARAMS; // Buffer too small

                        _lastTradeJson = trade;
                        
                        // Write trade JSON to temp file for C++ wrapper to read
                        try
                        {
                            File.WriteAllText(_tempTradeFile, trade);
                        }
                        catch
                        {
                            // If file write fails, still store in memory
                        }
                        
                        return ERROR_SUCCESS;
                    }

                    _lastTradeJson = ""; // No trades available
                    
                    // Write empty string to indicate no trades
                    try
                    {
                        File.WriteAllText(_tempTradeFile, "");
                    }
                    catch
                    {
                        // Ignore file write errors
                    }
                    
                    return ERROR_SUCCESS; // No trades available, not an error
                }
            }
            catch (Exception ex)
            {
                lock (_lockObject)
                {
                    _lastErrorMessage = ex.Message;
                }
                return ERROR_SERIALIZATION;
            }
        }
        
        /// <summary>
        /// Get the last trade JSON stored by GrpcGetNextTrade
        /// </summary>
        /// <returns>Last trade JSON or empty string</returns>
        public static string GrpcGetLastTradeJson()
        {
            lock (_lockObject)
            {
                return _lastTradeJson;
            }
        }

        /// <summary>
        /// Submit trade execution result to Bridge Server
        /// </summary>
        /// <param name="resultJson">Trade result in JSON format</param>
        /// <returns>0 on success, negative error code on failure</returns>
        
        public static int GrpcSubmitTradeResult([MarshalAs(UnmanagedType.LPWStr)] string resultJson)
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
        /// <param name="openPositions">Number of open positions</param>
        /// <returns>0 if healthy, negative error code on failure</returns>
        
        public static int GrpcHealthCheck(int openPositions)
        {
            try
            {
                if (!_isInitialized || _client == null)
                    return ERROR_NOT_INITIALIZED;

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
        /// <param name="notificationJson">Hedge close notification in JSON format</param>
        /// <returns>0 on success, negative error code on failure</returns>
        
        public static int GrpcNotifyHedgeClose([MarshalAs(UnmanagedType.LPWStr)] string notificationJson)
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
        /// <returns>1 if connected, 0 if disconnected</returns>
        
        public static int GrpcIsConnected()
        {
            return _isConnected ? 1 : 0;
        }

        /// <summary>
        /// Get number of trades in queue
        /// </summary>
        /// <returns>Number of pending trades</returns>
        
        public static int GrpcGetQueueSize()
        {
            return _tradeQueue.Count;
        }

        /// <summary>
        /// Get last error message
        /// </summary>
        /// <param name="errorMessage">Output buffer for error message</param>
        /// <param name="bufferSize">Size of the output buffer</param>
        /// <returns>0 on success</returns>
        
        public static int GrpcGetLastError([MarshalAs(UnmanagedType.LPWStr)] out string errorMessage, int bufferSize)
        {
            errorMessage = "No error"; // Simplified for now
            return ERROR_SUCCESS;
        }

        /// <summary>
        /// Stop gRPC trade streaming
        /// </summary>
        /// <returns>0 on success, negative error code on failure</returns>
        
        public static int GrpcStopTradeStream()
        {
            try
            {
                _isStreamingActive = false;
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
        /// <returns>0 on success, negative error code on failure</returns>
        
        public static int GrpcShutdown()
        {
            return GrpcCleanup();
        }

        /// <summary>
        /// Reconnect gRPC client
        /// </summary>
        /// <returns>0 on success, negative error code on failure</returns>
        
        public static int GrpcReconnect()
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
        /// Get trade queue size
        /// </summary>
        /// <returns>Number of pending trades</returns>
        
        public static int GrpcGetTradeQueueSize()
        {
            return GrpcGetQueueSize();
        }

        /// <summary>
        /// Health check with JSON request/response
        /// </summary>
        /// <param name="requestJson">Health check request JSON</param>
        /// <param name="responseJson">Output health check response JSON</param>
        /// <param name="bufferSize">Size of response buffer</param>
        /// <returns>0 on success, negative error code on failure</returns>
        
        public static int GrpcHealthCheck([MarshalAs(UnmanagedType.LPWStr)] string requestJson, 
                                         [MarshalAs(UnmanagedType.LPWStr)] out string responseJson, 
                                         int bufferSize)
        {
            responseJson = "";
            try
            {
                if (!_isInitialized || _client == null)
                    return ERROR_NOT_INITIALIZED;

                var requestData = JsonSerializer.Deserialize<JsonElement>(requestJson);
                int openPositions = 0;
                if (requestData.TryGetProperty("open_positions", out var posElement))
                {
                    openPositions = posElement.GetInt32();
                }

                var healthRequest = new HealthRequest
                {
                    Source = "MT5_EA",
                    OpenPositions = openPositions
                };

                var response = _client.HealthCheck(healthRequest);
                
                responseJson = JsonSerializer.Serialize(new { status = response.Status });
                
                return response.Status == "healthy" ? ERROR_SUCCESS : ERROR_CONNECTION_FAILED;
            }
            catch (Exception)
            {
                responseJson = JsonSerializer.Serialize(new { status = "error" });
                return ERROR_CONNECTION_FAILED;
            }
        }

        /// <summary>
        /// Submit elastic update to Bridge Server
        /// </summary>
        /// <param name="updateJson">Elastic update in JSON format</param>
        /// <returns>0 on success, negative error code on failure</returns>
        
        public static int GrpcSubmitElasticUpdate([MarshalAs(UnmanagedType.LPWStr)] string updateJson)
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
        /// <param name="updateJson">Trailing update in JSON format</param>
        /// <returns>0 on success, negative error code on failure</returns>
        
        public static int GrpcSubmitTrailingUpdate([MarshalAs(UnmanagedType.LPWStr)] string updateJson)
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
        /// Get connection status in JSON format
        /// </summary>
        /// <param name="statusJson">Output connection status JSON</param>
        /// <param name="bufferSize">Size of output buffer</param>
        /// <returns>0 on success</returns>
        
        public static int GrpcGetConnectionStatus([MarshalAs(UnmanagedType.LPWStr)] out string statusJson, int bufferSize)
        {
            statusJson = JsonSerializer.Serialize(new
            {
                connected = _isConnected,
                streaming = _isStreamingActive,
                server_address = _serverAddress,
                queue_size = _tradeQueue.Count,
                last_health_check = _lastHealthCheck.ToString("yyyy-MM-dd HH:mm:ss")
            });
            return ERROR_SUCCESS;
        }

        /// <summary>
        /// Get streaming statistics in JSON format
        /// </summary>
        /// <param name="statsJson">Output streaming stats JSON</param>
        /// <param name="bufferSize">Size of output buffer</param>
        /// <returns>0 on success</returns>
        
        public static int GrpcGetStreamingStats([MarshalAs(UnmanagedType.LPWStr)] out string statsJson, int bufferSize)
        {
            statsJson = JsonSerializer.Serialize(new
            {
                streaming_active = _isStreamingActive,
                trades_in_queue = _tradeQueue.Count,
                connection_established = _isConnected
            });
            return ERROR_SUCCESS;
        }

        /// <summary>
        /// Set connection timeout
        /// </summary>
        /// <param name="timeoutMs">Timeout in milliseconds</param>
        /// <returns>0 on success</returns>
        
        public static int GrpcSetConnectionTimeout(int timeoutMs)
        {
            return ERROR_SUCCESS;
        }

        /// <summary>
        /// Set streaming timeout
        /// </summary>
        /// <param name="timeoutMs">Timeout in milliseconds</param>
        /// <returns>0 on success</returns>
        
        public static int GrpcSetStreamingTimeout(int timeoutMs)
        {
            return ERROR_SUCCESS;
        }

        /// <summary>
        /// Set maximum retries
        /// </summary>
        /// <param name="maxRetries">Maximum number of retries</param>
        /// <returns>0 on success</returns>
        
        public static int GrpcSetMaxRetries(int maxRetries)
        {
            return ERROR_SUCCESS;
        }

        /// <summary>
        /// Get last error code
        /// </summary>
        /// <returns>Last error code</returns>
        
        public static int GrpcGetLastError()
        {
            return ERROR_SUCCESS;
        }

        /// <summary>
        /// Get last error message
        /// </summary>
        /// <param name="errorMessage">Output error message</param>
        /// <param name="bufferSize">Size of output buffer</param>
        /// <returns>0 on success</returns>
        
        public static int GrpcGetLastErrorMessage([MarshalAs(UnmanagedType.LPWStr)] out string errorMessage, int bufferSize)
        {
            errorMessage = "No error";
            return ERROR_SUCCESS;
        }

        /// <summary>
        /// Cleanup gRPC client and release resources
        /// </summary>
        /// <returns>0 on success, negative error code on failure</returns>
        
        public static int GrpcCleanup()
        {
            try
            {
                _isStreamingActive = false;
                
                if (_cancellationTokenSource != null)
                {
                    _cancellationTokenSource.Cancel();
                    _cancellationTokenSource.Dispose();
                    _cancellationTokenSource = null;
                }

                if (_streamingTask != null)
                {
                    _streamingTask.Wait(5000); // Wait up to 5 seconds
                    _streamingTask.Dispose();
                    _streamingTask = null;
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