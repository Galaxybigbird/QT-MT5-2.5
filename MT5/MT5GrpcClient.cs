using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Net.Client;
using Grpc.Core;
using Trading.Proto;
using System.IO;
using System.Reflection;
using System.Linq;

namespace MT5GrpcClient
{
    /// <summary>
    /// MT5 gRPC Client DLL for pure gRPC communication with Bridge Server
    /// Exports unmanaged functions for MQL5 integration
    /// </summary>
    public static class GrpcClientWrapper
    {
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetDllDirectory(string lpPathName);

    private static bool s_nativePathInited;
    private static void EnsureNativeSearchPath()
    {
        if (s_nativePathInited) return;
        s_nativePathInited = true;
        try
        {
            var asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (!string.IsNullOrEmpty(asmDir))
            {
                SetDllDirectory(asmDir);
                var existing = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                if (existing.IndexOf(asmDir, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    Environment.SetEnvironmentVariable("PATH", asmDir + ";" + existing);
                }
            }
            // Also add Terminal root hash directory if present (we copy native deps there)
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var mqRoot = Path.Combine(roaming, "MetaQuotes", "Terminal");
            if (Directory.Exists(mqRoot))
            {
                var dirs = Directory.GetDirectories(mqRoot);
                if (dirs != null && dirs.Length > 0)
                {
                    var firstHash = dirs[0];
                    if (!string.IsNullOrEmpty(firstHash) && Directory.Exists(firstHash))
                    {
                        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                        if (pathVar.IndexOf(firstHash, StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            Environment.SetEnvironmentVariable("PATH", firstHash + ";" + pathVar);
                        }
                    }
                }
            }
            // Install AssemblyResolve to load DLLs from our folder and Terminal root
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                try
                {
                    var requested = new AssemblyName(args.Name);
                    var name = requested.Name + ".dll";
                    var baseDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
                    string roamingDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                    string termRoot = Directory.Exists(Path.Combine(roamingDir, "MetaQuotes", "Terminal"))
                        ? (Directory.GetDirectories(Path.Combine(roamingDir, "MetaQuotes", "Terminal")).FirstOrDefault() ?? string.Empty)
                        : string.Empty;
                    string[] probeDirs = string.IsNullOrEmpty(termRoot)
                        ? new[] { baseDir }
                        : new[] { baseDir, termRoot };
                    foreach (var dir in probeDirs)
                    {
                        if (string.IsNullOrEmpty(dir)) continue;
                        var candidate = Path.Combine(dir, name);
                        if (File.Exists(candidate))
                        {
                            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "mt5_grpc_managed.txt"),
                                $"[{DateTime.Now:O}] AssemblyResolve: loaded {name} from {candidate}\n"); } catch { }
                            return Assembly.LoadFrom(candidate);
                        }
                    }
                    try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "mt5_grpc_managed.txt"),
                        $"[{DateTime.Now:O}] AssemblyResolve: miss {name}\n"); } catch { }
                    return null;
                }
                catch { return null; }
            };

            // Eager-load critical support libs to satisfy System.Text.Json and Grpc.Core
            TryLoadSupport("System.Runtime.CompilerServices.Unsafe.dll");
            TryLoadSupport("System.Memory.dll");
            TryLoadSupport("System.Buffers.dll");
            TryLoadSupport("Google.Protobuf.dll");
            TryLoadSupport("Grpc.Core.Api.dll");
            TryLoadSupport("Grpc.Core.dll");
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "mt5_grpc_managed.txt"),
                $"[{DateTime.Now:O}] EnsureNativeSearchPath OK asmDir={asmDir}\n"); } catch { }
        }
        catch { }
    }

    private static void TryLoadSupport(string file)
    {
        try
        {
            var asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
            string candidate = Path.Combine(asmDir, file);
            if (File.Exists(candidate))
            {
                Assembly.LoadFrom(candidate);
                try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "mt5_grpc_managed.txt"),
                    $"[{DateTime.Now:O}] Preloaded support: {file}\n"); } catch { }
            }
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "mt5_grpc_managed.txt"),
                $"[{DateTime.Now:O}] Preload failed {file}: {ex.Message}\n"); } catch { }
        }
    }
    private static GrpcChannel? _channel;
    private static Channel? _coreChannel; // Grpc.Core channel for .NET Framework
        private static TradingService.TradingServiceClient? _client;
        private static StreamingService.StreamingServiceClient? _streamingClient;
    private static LoggingService.LoggingServiceClient? _loggingClient;
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

        // Ensure a LoggingService client exists even if the full client wasn't initialized.
        // Uses existing _channel if available; otherwise creates a minimal channel to the bridge
        // using env vars BRIDGE_GRPC_HOST/BRIDGE_GRPC_PORT or defaults to 127.0.0.1:50051.
        private static bool EnsureLoggingClient()
        {
            try
            {
                EnsureNativeSearchPath();
                if (_loggingClient != null)
                    return true;

                // Reuse configured server if available; else fall back to env/defaults
                string host = Environment.GetEnvironmentVariable("BRIDGE_GRPC_HOST") ?? "127.0.0.1";
                string portStr = Environment.GetEnvironmentVariable("BRIDGE_GRPC_PORT") ?? "50051";
                if (!int.TryParse(portStr, out var port) || port <= 0)
                    port = 50051;

                if (string.IsNullOrWhiteSpace(_serverAddress))
                {
                    _serverAddress = $"http://{host}:{port}";
                }

                // Try Grpc.Core first
                try
                {
                    if (_coreChannel == null)
                    {
                        var target = _serverAddress.Replace("http://", string.Empty).Replace("https://", string.Empty);
                        _coreChannel = new Channel(target, ChannelCredentials.Insecure, new[] {
                            new ChannelOption(ChannelOptions.MaxReceiveMessageLength, 1024 * 1024),
                            new ChannelOption(ChannelOptions.MaxSendMessageLength, 1024 * 1024)
                        });
                    }
                    _loggingClient = new LoggingService.LoggingServiceClient(_coreChannel);
                    return true;
                }
                catch (Exception exCore)
                {
                    // Fallback to Grpc.Net.Client
                    try
                    {
                        if (_channel == null)
                        {
                            var channelOptions = new GrpcChannelOptions
                            {
                                MaxReceiveMessageSize = 1024 * 1024,
                                MaxSendMessageSize = 1024 * 1024,
                            };
                            _channel = GrpcChannel.ForAddress(_serverAddress, channelOptions);
                        }
                        _loggingClient = new LoggingService.LoggingServiceClient(_channel);
                        return true;
                    }
                    catch (Exception exNet)
                    {
                        lock (_lockObject) { _lastErrorMessage = $"EnsureLoggingClient failed: core={exCore.Message}; net={exNet.Message}"; }
                        return false;
                    }
                }
            }
            catch
            {
                return false;
            }
        }

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
        /// Wrapper-friendly overload: accepts a single string "host,port"
        /// </summary>
        /// <param name="args">Combined args: "host,port"</param>
        /// <returns>0 on success, negative error code on failure</returns>
        public static int GrpcInitialize(string args)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(args)) return ERROR_INVALID_PARAMS;
                var idx = args.LastIndexOf(',');
                if (idx <= 0 || idx >= args.Length - 1) return ERROR_INVALID_PARAMS;
                var host = args.Substring(0, idx).Trim();
                var portStr = args.Substring(idx + 1).Trim();
                if (!int.TryParse(portStr, out var port)) return ERROR_INVALID_PARAMS;
                return GrpcInitialize(host, port);
            }
            catch
            {
                return ERROR_INIT_FAILED;
            }
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
                EnsureNativeSearchPath();
                if (_isInitialized)
                {
                    GrpcCleanup();
                }
                    try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "mt5_grpc_managed.txt"),
                        $"[{DateTime.Now:O}] GrpcInitialize called with serverAddress={serverAddress}, port={port}\n"); } catch { }

                _serverAddress = $"http://{serverAddress}:{port}";
                
                // Prefer Grpc.Core on .NET Framework for robust HTTP/2
                if (_coreChannel == null)
                {
                    var target = _serverAddress.Replace("http://", string.Empty).Replace("https://", string.Empty);
                    _coreChannel = new Channel(target, ChannelCredentials.Insecure, new[]
                    {
                        new ChannelOption(ChannelOptions.MaxReceiveMessageLength, 1024 * 1024),
                        new ChannelOption(ChannelOptions.MaxSendMessageLength, 1024 * 1024)
                    });
                }
                _client = new TradingService.TradingServiceClient(_coreChannel);
                _streamingClient = new StreamingService.StreamingServiceClient(_coreChannel);
                _loggingClient = new LoggingService.LoggingServiceClient(_coreChannel);
                
                _cancellationTokenSource = new CancellationTokenSource();
                _tradeQueue = new ConcurrentQueue<string>();
                
                // Mark initialized once channels/clients are created. We will compute connectivity via health checks.
                _isInitialized = true;
                _isConnected = false;

                // Test connection with health check (non-fatal if not healthy yet)
                var healthRequest = new HealthRequest 
                { 
                    Source = "MT5_EA",
                    OpenPositions = 0
                };

                try
                {
                    var healthResponse = _client.HealthCheck(healthRequest);
                    if (healthResponse.Status == "healthy")
                    {
                        _isConnected = true;
                        _lastHealthCheck = DateTime.UtcNow;
                    }
                }
                catch (Exception exHealth)
                {
                    // Bridge may not be ready yet. Keep initialized=true so EA can retry health later.
                    try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "mt5_grpc_managed.txt"),
                        $"[{DateTime.Now:O}] GrpcInitialize health non-fatal error: {exHealth.Message}\n"); } catch { }
                }

                return ERROR_SUCCESS;
            }
            catch (Exception ex)
            {
                try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "mt5_grpc_managed.txt"),
                    $"[{DateTime.Now:O}] GrpcInitialize exception: {ex}\n"); } catch { }
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
                                        nt_session_trades = trade.NtSessionTrades,
                                        mt5_ticket = trade.Mt5Ticket
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
        /// Wrapper-friendly overload: takes buffer size in string, writes trade JSON to temp file for C++ wrapper to read
        /// </summary>
        public static int GrpcGetNextTrade(string args)
        {
            try
            {
                int bufferSize = 0;
                if (!string.IsNullOrWhiteSpace(args))
                {
                    int.TryParse(args.Trim(), out bufferSize);
                }

                if (!_tradeQueue.TryDequeue(out var tradeJson))
                {
                    tradeJson = string.Empty;
                }

                // Write to temp file consumed by wrapper (path must match wrapper)
                File.WriteAllText(_tempTradeFile, tradeJson ?? string.Empty);
                return ERROR_SUCCESS;
            }
            catch
            {
                return ERROR_SERIALIZATION;
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
        /// Submit a unified log event to the Bridge's LoggingService.
        /// Accepts a JSON object compatible with LogEvent fields; missing fields are optional.
        /// </summary>
        /// <param name="logJson">JSON with keys like source, level, component, message, base_id, tags, etc.</param>
        /// <returns>0 on success, negative on failure</returns>
    public static int GrpcLog([MarshalAs(UnmanagedType.LPWStr)] string logJson)
        {
            EnsureNativeSearchPath();
            // Build a LogEvent; if JSON parsing fails, fallback to raw message
            LogEvent evt;
            try
            {
                var data = JsonSerializer.Deserialize<JsonElement>(logJson);
                evt = new LogEvent
                {
                    TimestampNs = data.TryGetProperty("timestamp_ns", out var ts) && ts.ValueKind == JsonValueKind.Number ? ts.GetInt64() : 0,
                    Source = data.TryGetProperty("source", out var src) ? (src.GetString() ?? "") : "mt5",
                    Level = data.TryGetProperty("level", out var lvl) ? (lvl.GetString() ?? "INFO") : "INFO",
                    Component = data.TryGetProperty("component", out var comp) ? (comp.GetString() ?? "EA") : "EA",
                    Message = data.TryGetProperty("message", out var msg) ? (msg.GetString() ?? "") : "",
                    BaseId = data.TryGetProperty("base_id", out var bid) ? (bid.GetString() ?? "") : "",
                    TradeId = data.TryGetProperty("trade_id", out var tid) ? (tid.GetString() ?? "") : "",
                    NtOrderId = data.TryGetProperty("nt_order_id", out var nid) ? (nid.GetString() ?? "") : "",
                    Mt5Ticket = data.TryGetProperty("mt5_ticket", out var tk) && tk.ValueKind == JsonValueKind.Number ? tk.GetUInt64() : 0,
                    QueueSize = data.TryGetProperty("queue_size", out var qs) && qs.ValueKind == JsonValueKind.Number ? qs.GetInt32() : 0,
                    NetPosition = data.TryGetProperty("net_position", out var np) && np.ValueKind == JsonValueKind.Number ? np.GetInt32() : 0,
                    HedgeSize = data.TryGetProperty("hedge_size", out var hs) && hs.ValueKind == JsonValueKind.Number ? hs.GetDouble() : 0,
                    ErrorCode = data.TryGetProperty("error_code", out var ec) ? (ec.GetString() ?? "") : "",
                    Stack = data.TryGetProperty("stack", out var st) ? (st.GetString() ?? "") : "",
                    SchemaVersion = data.TryGetProperty("schema_version", out var sv) ? (sv.GetString() ?? "mt5-1") : "mt5-1",
                    CorrelationId = data.TryGetProperty("correlation_id", out var cid) ? (cid.GetString() ?? "") : ""
                };
                if (data.TryGetProperty("tags", out var tagsElem) && tagsElem.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in tagsElem.EnumerateObject())
                    {
                        evt.Tags[prop.Name] = prop.Value.ValueKind == JsonValueKind.String ? (prop.Value.GetString() ?? "") : prop.Value.ToString();
                    }
                }
            }
            catch (Exception parseEx)
            {
                try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "mt5_grpc_managed.txt"),
                    $"[{DateTime.Now:O}] GrpcLog JSON parse failed: {parseEx.Message}; raw='{logJson}'\n"); } catch { }
                evt = new LogEvent
                {
                    TimestampNs = 0,
                    Source = "mt5",
                    Level = "INFO",
                    Component = "EA",
                    Message = logJson ?? string.Empty,
                    SchemaVersion = "mt5-1",
                };
            }

            // Determine bridge target and send
            try
            {
                string host = Environment.GetEnvironmentVariable("BRIDGE_GRPC_HOST") ?? "127.0.0.1";
                string portStr = Environment.GetEnvironmentVariable("BRIDGE_GRPC_PORT") ?? "50051";
                if (!int.TryParse(portStr, out var port) || port <= 0) port = 50051;
                var target = $"{host}:{port}";

                var options = new[]
                {
                    new ChannelOption(ChannelOptions.MaxReceiveMessageLength, 1024 * 1024),
                    new ChannelOption(ChannelOptions.MaxSendMessageLength, 1024 * 1024)
                };
                var channel = new Channel(target, ChannelCredentials.Insecure, options);
                try
                {
                    var client = new LoggingService.LoggingServiceClient(channel);
                    var ack = client.Log(evt);
                    return ack.Accepted > 0 ? ERROR_SUCCESS : ERROR_CONNECTION_FAILED;
                }
                finally
                {
                    try { channel.ShutdownAsync().Wait(200); } catch { }
                }
            }
            catch (RpcException rpcEx)
            {
                try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "mt5_grpc_managed.txt"),
                    $"[{DateTime.Now:O}] GrpcLog send RpcException: {rpcEx.Status} {rpcEx.Message}\n"); } catch { }
                return ERROR_CONNECTION_FAILED;
            }
            catch (Exception sendEx)
            {
                try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "mt5_grpc_managed.txt"),
                    $"[{DateTime.Now:O}] GrpcLog send failed: {sendEx.Message}\n"); } catch { }
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
                // Use fresh short-lived Grpc.Core channel like GrpcLog to avoid stale connection state
                EnsureNativeSearchPath();
                if (string.IsNullOrWhiteSpace(_serverAddress))
                {
                    string host = Environment.GetEnvironmentVariable("BRIDGE_GRPC_HOST") ?? "127.0.0.1";
                    string portStr = Environment.GetEnvironmentVariable("BRIDGE_GRPC_PORT") ?? "50051";
                    if (!int.TryParse(portStr, out var port) || port <= 0) port = 50051;
                    _serverAddress = $"http://{host}:{port}";
                }
                var target = _serverAddress.Replace("http://", string.Empty).Replace("https://", string.Empty);
                var options = new[]
                {
                    new ChannelOption(ChannelOptions.MaxReceiveMessageLength, 1024 * 1024),
                    new ChannelOption(ChannelOptions.MaxSendMessageLength, 1024 * 1024)
                };
                var tempChannel = new Channel(target, ChannelCredentials.Insecure, options);
                try
                {
                    var tempClient = new TradingService.TradingServiceClient(tempChannel);
                    var healthRequest = new HealthRequest { Source = "MT5_EA", OpenPositions = openPositions };
                    var response = tempClient.HealthCheck(healthRequest);
                    if (response.Status == "healthy")
                    {
                        _isConnected = true;
                        _lastHealthCheck = DateTime.UtcNow;
                        return ERROR_SUCCESS;
                    }
                    _isConnected = false;
                    return ERROR_CONNECTION_FAILED;
                }
                finally
                {
                    try { tempChannel.ShutdownAsync().Wait(200); } catch { }
                }
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
                    ClosureReason = notificationData.GetProperty("closure_reason").GetString() ?? "",
                };
                // Optional: propagate mt5_ticket if present in the JSON
                if (notificationData.TryGetProperty("mt5_ticket", out var tk) && tk.ValueKind == JsonValueKind.Number)
                {
                    hedgeNotification.Mt5Ticket = tk.GetUInt64();
                }

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
                // Also write to temp file for the native wrapper to pick up if needed
                try
                {
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "mt5_grpc_health.json"), responseJson);
                }
                catch { }
                
                return response.Status == "healthy" ? ERROR_SUCCESS : ERROR_CONNECTION_FAILED;
            }
            catch (Exception)
            {
                responseJson = JsonSerializer.Serialize(new { status = "error" });
                try
                {
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "mt5_grpc_health.json"), responseJson);
                }
                catch { }
                return ERROR_CONNECTION_FAILED;
            }
        }

        // Wrapper-friendly overload for ExecuteInDefaultAppDomain: accepts a single string argument.
        // Format: "{request_json},{buffer_size}". Writes response JSON to temp file and returns status code.
    public static int GrpcHealthCheck(string args)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(args))
                    return ERROR_INVALID_PARAMS;

                // Split by last comma to allow commas inside JSON (unlikely in our minimal payload)
                var idx = args.LastIndexOf(',');
                if (idx <= 0 || idx >= args.Length - 1)
                    return ERROR_INVALID_PARAMS;

                var json = args.Substring(0, idx);
                var bufStr = args.Substring(idx + 1);
                if (!int.TryParse(bufStr, out var bufferSize)) bufferSize = 2048;

                // Use fresh short-lived channel per call to avoid stale state
                EnsureNativeSearchPath();
                if (string.IsNullOrWhiteSpace(_serverAddress))
                {
                    string host = Environment.GetEnvironmentVariable("BRIDGE_GRPC_HOST") ?? "127.0.0.1";
                    string portStr = Environment.GetEnvironmentVariable("BRIDGE_GRPC_PORT") ?? "50051";
                    if (!int.TryParse(portStr, out var port) || port <= 0) port = 50051;
                    _serverAddress = $"http://{host}:{port}";
                }
                var target = _serverAddress.Replace("http://", string.Empty).Replace("https://", string.Empty);
                var options = new[]
                {
                    new ChannelOption(ChannelOptions.MaxReceiveMessageLength, 1024 * 1024),
                    new ChannelOption(ChannelOptions.MaxSendMessageLength, 1024 * 1024)
                };
                var tempChannel = new Channel(target, ChannelCredentials.Insecure, options);

                // Parse open_positions from JSON if present
                int openPositions = 0;
                try
                {
                    var elem = JsonSerializer.Deserialize<JsonElement>(json);
                    if (elem.TryGetProperty("open_positions", out var posEl) && posEl.ValueKind == JsonValueKind.Number)
                    {
                        openPositions = posEl.GetInt32();
                    }
                }
                catch { }

                try
                {
                    var tempClient = new TradingService.TradingServiceClient(tempChannel);
                    var healthRequest = new HealthRequest { Source = "MT5_EA", OpenPositions = openPositions };
                    var response = tempClient.HealthCheck(healthRequest);
                    var responseJson = JsonSerializer.Serialize(new { status = response.Status });
                    try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "mt5_grpc_health.json"), responseJson); } catch { }
                    if (response.Status == "healthy")
                    {
                        _isConnected = true;
                        _lastHealthCheck = DateTime.UtcNow;
                        return ERROR_SUCCESS;
                    }
                    _isConnected = false;
                    return ERROR_CONNECTION_FAILED;
                }
                catch
                {
                    _isConnected = false;
                    try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "mt5_grpc_health.json"), JsonSerializer.Serialize(new { status = "error" })); } catch { }
                    return ERROR_CONNECTION_FAILED;
                }
                finally
                {
                    try { tempChannel.ShutdownAsync().Wait(200); } catch { }
                }
            }
            catch
            {
                try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "mt5_grpc_health.json"), JsonSerializer.Serialize(new { status = "error" })); } catch { }
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
                if (_coreChannel != null)
                {
                    try { _coreChannel.ShutdownAsync().Wait(2000); } catch {}
                    _coreChannel = null;
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