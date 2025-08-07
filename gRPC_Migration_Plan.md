# gRPC Migration Plan for Futures Hedging System

## Executive Summary

This document outlines the comprehensive plan to migrate the current HTTP/WebSocket-based communication architecture to gRPC while maintaining fallback capabilities and converting logging to local NinjaScript output.

## Current Architecture Analysis

### Communication Flows Identified

#### 1. **Go Bridge Server (BridgeApp)**
- **HTTP Server Endpoints:**
  - `POST /log_trade` - Receives trades from NinjaTrader
  - `GET /mt5/get_trade` - Sends trades to MT5 EA
  - `GET /health` - Health check (supports `?source=hedgebot` parameter)
  - `POST /notify_hedge_close` - Handles hedge closure notifications from MT5
  - `POST /nt_close_hedge` - Handles hedge closure requests from NT
  - `POST /mt5/trade_result` - Receives trade execution results from MT5
  - `POST /elastic_update` - Handles elastic hedging updates
  - `POST /trailing_stop_update` - Handles trailing stop updates

#### 2. **C# NinjaTrader Addon (MultiStratManagerRepo)**
- **HTTP Client Patterns:**
  - Trade submission via `POST /log_trade`
  - Trade closure via `POST /nt_close_hedge`
  - Health checks via `GET /health?source=addon`
  - Settings API via `GET /api/settings/verbose-mode`
  - System heartbeat via `POST /api/system/heartbeat`
  - Logging API via `POST /api/logs`

- **WebSocket Communication:**
  - Real-time trade data transmission
  - Elastic hedge updates
  - Trailing stop updates
  - Connection heartbeat
  - Bidirectional messaging

- **HTTP Listener (Port 8081):**
  - `/ping_msm` - Ping endpoint
  - `/notify_hedge_closed` - Hedge closure notifications

#### 3. **MT5 EA Communication:**
- **WebRequest Endpoints:**
  - `GET /health?source=hedgebot` - Health ping every 3 seconds
  - `GET /mt5/get_trade` - Trade polling every 200ms
  - `POST /mt5/trade_result` - Trade execution results
  - `POST /notify_hedge_close` - Hedge closure notifications

## gRPC Migration Plan

### Phase 1: Protocol Buffer Definitions

#### 1.1 Core Message Types
```protobuf
// proto/trading.proto
syntax = "proto3";

package trading;
option go_package = "github.com/hedgebot/proto/trading";
option csharp_namespace = "Trading.Proto";

// Core trade message
message Trade {
  string id = 1;
  string base_id = 2;
  int64 timestamp = 3;
  string action = 4;              // "buy", "sell", "CLOSE_HEDGE", etc.
  double quantity = 5;
  double price = 6;
  int32 total_quantity = 7;
  int32 contract_num = 8;
  string order_type = 9;          // "ENTRY", "TP", "SL", "NT_CLOSE"
  int32 measurement_pips = 10;
  double raw_measurement = 11;
  string instrument = 12;
  string account_name = 13;
  
  // Enhanced NT Performance Data
  double nt_balance = 14;
  double nt_daily_pnl = 15;
  string nt_trade_result = 16;    // "win", "loss", "pending"
  int32 nt_session_trades = 17;
}

// Hedge closure notification
message HedgeCloseNotification {
  string event_type = 1;
  string base_id = 2;
  string nt_instrument_symbol = 3;
  string nt_account_name = 4;
  double closed_hedge_quantity = 5;
  string closed_hedge_action = 6;
  string timestamp = 7;
  string closure_reason = 8;
}

// Elastic hedge update
message ElasticHedgeUpdate {
  string event_type = 1;
  string action = 2;
  string base_id = 3;
  double current_profit = 4;
  int32 profit_level = 5;
  string timestamp = 6;
}

// Trailing stop update
message TrailingStopUpdate {
  string event_type = 1;
  string base_id = 2;
  double new_stop_price = 3;
  string trailing_type = 4;
  double current_price = 5;
  string timestamp = 6;
}

// MT5 trade result
message MT5TradeResult {
  string status = 1;
  uint64 ticket = 2;
  double volume = 3;
  bool is_close = 4;
  string id = 5;
}

// Health check request/response
message HealthRequest {
  string source = 1;  // "hedgebot", "addon", etc.
  int32 open_positions = 2;  // Optional for hedgebot
}

message HealthResponse {
  string status = 1;
  int32 queue_size = 2;
  int32 net_position = 3;
  double hedge_size = 4;
}

// Generic response
message GenericResponse {
  string status = 1;
  string message = 2;
  map<string, string> metadata = 3;
}
```

#### 1.2 Service Definitions
```protobuf
// Trading service for main communication
service TradingService {
  // Trade submission from NinjaTrader
  rpc SubmitTrade(Trade) returns (GenericResponse);
  
  // Trade polling for MT5 (streaming)
  rpc GetTrades(stream HealthRequest) returns (stream Trade);
  
  // Trade result from MT5
  rpc SubmitTradeResult(MT5TradeResult) returns (GenericResponse);
  
  // Hedge closure notifications
  rpc NotifyHedgeClose(HedgeCloseNotification) returns (GenericResponse);
  
  // Elastic hedge updates
  rpc SubmitElasticUpdate(ElasticHedgeUpdate) returns (GenericResponse);
  
  // Trailing stop updates
  rpc SubmitTrailingUpdate(TrailingStopUpdate) returns (GenericResponse);
  
  // Health check
  rpc HealthCheck(HealthRequest) returns (HealthResponse);
}

// Real-time streaming service
service StreamingService {
  // Bidirectional streaming for real-time updates
  rpc TradingStream(stream Trade) returns (stream Trade);
  
  // Status updates stream
  rpc StatusStream(stream HealthRequest) returns (stream HealthResponse);
}
```

### Phase 2: Go Bridge Server Implementation

#### 2.1 gRPC Server Setup
```go
// internal/grpc/server.go
type Server struct {
    trading.UnimplementedTradingServiceServer
    trading.UnimplementedStreamingServiceServer
    app *App  // Reference to main app
}

func NewGRPCServer(app *App) *Server {
    return &Server{app: app}
}

func (s *Server) StartGRPCServer() error {
    lis, err := net.Listen("tcp", ":50051")
    if err != nil {
        return err
    }
    
    grpcServer := grpc.NewServer(
        grpc.KeepaliveParams(keepalive.ServerParameters{
            Time:    30 * time.Second,
            Timeout: 5 * time.Second,
        }),
        grpc.KeepaliveEnforcementPolicy(keepalive.EnforcementPolicy{
            MinTime:             5 * time.Second,
            PermitWithoutStream: true,
        }),
    )
    
    trading.RegisterTradingServiceServer(grpcServer, s)
    trading.RegisterStreamingServiceServer(grpcServer, s)
    
    return grpcServer.Serve(lis)
}
```

#### 2.2 Service Implementation
```go
// Implement trading service methods
func (s *Server) SubmitTrade(ctx context.Context, req *trading.Trade) (*trading.GenericResponse, error) {
    // Convert proto to internal trade struct
    trade := convertProtoToTrade(req)
    
    // Existing logic from logTradeHandler
    select {
    case s.app.tradeQueue <- trade:
        return &trading.GenericResponse{
            Status: "success",
        }, nil
    default:
        return nil, status.Error(codes.ResourceExhausted, "queue full")
    }
}

func (s *Server) GetTrades(stream trading.TradingService_GetTradesServer) error {
    // Streaming implementation for MT5 polling
    for {
        select {
        case trade := <-s.app.tradeQueue:
            protoTrade := convertTradeToProto(trade)
            if err := stream.Send(protoTrade); err != nil {
                return err
            }
        case <-stream.Context().Done():
            return nil
        }
    }
}
```

#### 2.3 HTTP Fallback Implementation
```go
// Comment out existing HTTP handlers but keep them for fallback
func (a *App) startHTTPServer() {
    // FALLBACK: Commented out but kept for rollback capability
    /*
    mux := http.NewServeMux()
    mux.HandleFunc("/log_trade", a.logTradeHandler)
    mux.HandleFunc("/mt5/get_trade", a.getTradeHandler)
    // ... other handlers
    */
}

// Feature flag for HTTP fallback
var useHTTPFallback = false

func (a *App) startServer() {
    // Start gRPC server
    go a.startGRPCServer()
    
    // Start HTTP server if fallback enabled
    if useHTTPFallback {
        go a.startHTTPServer()
    }
}
```

### Phase 3: C# NinjaTrader Addon Implementation with Custom gRPC DLL

#### 3.1 Custom gRPC DLL Architecture

**The solution is to create a custom .NET DLL that handles all gRPC complexity:**

```
┌─────────────────┐    C# Interop    ┌──────────────────┐    gRPC      ┌─────────────┐
│  NinjaTrader    │ ──────────────→  │  NTGrpcClient    │ ──────────→  │ Bridge      │
│     Addon       │                  │      .dll        │              │ Server      │
│   (C# 5.0)      │ ←──────────────  │  (.NET Std 2.0)  │ ←────────   │  (Go)      │
└─────────────────┘    Simple APIs   └──────────────────┘    gRPC      └─────────────┘
```

#### 3.2 NTGrpcClient.dll Implementation

**File Structure:**
```
MultiStratManagerRepo/
├── External/
│   ├── NTGrpcClient/
│   │   ├── src/
│   │   │   ├── NTGrpcClient.cs
│   │   │   ├── TradingClient.cs
│   │   │   └── Models/
│   │   ├── proto/
│   │   │   └── trading.proto
│   │   ├── NTGrpcClient.csproj
│   │   └── build.ps1
│   └── NTGrpcClient.dll (compiled output)
```

**NTGrpcClient.csproj:**
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <PlatformTarget>x64</PlatformTarget>
    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
    <AssemblyTitle>NTGrpcClient</AssemblyTitle>
    <AssemblyVersion>1.0.0.0</AssemblyVersion>
  </PropertyGroup>
  
  <ItemGroup>
    <PackageReference Include="Grpc.Net.Client" Version="2.71.0" />
    <PackageReference Include="Google.Protobuf" Version="3.25.1" />
    <PackageReference Include="Grpc.Tools" Version="2.71.0" PrivateAssets="All" />
    <PackageReference Include="System.Text.Json" Version="6.0.0" />
  </ItemGroup>
  
  <ItemGroup>
    <Protobuf Include="proto\trading.proto" GrpcServices="Client" />
  </ItemGroup>
</Project>
```

**Simple API Interface (NTGrpcClient.cs):**
```csharp
using System;
using System.Threading.Tasks;
using Grpc.Net.Client;
using Trading.Proto;

namespace NTGrpcClient
{
    /// <summary>
    /// Simple gRPC client interface for NinjaTrader addon
    /// Handles all gRPC complexity internally
    /// </summary>
    public static class TradingGrpcClient
    {
        private static ITradingClient _client;
        private static bool _initialized = false;
        
        /// <summary>
        /// Initialize the gRPC client
        /// </summary>
        /// <param name="serverAddress">gRPC server address (e.g., "http://localhost:50051")</param>
        /// <returns>True if successful</returns>
        public static bool Initialize(string serverAddress)
        {
            try
            {
                _client = new TradingClient(serverAddress);
                _initialized = true;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }
        
        /// <summary>
        /// Submit a trade to the bridge server
        /// </summary>
        /// <param name="tradeJson">JSON representation of trade</param>
        /// <returns>Success status</returns>
        public static bool SubmitTrade(string tradeJson)
        {
            if (!_initialized) return false;
            
            try
            {
                var result = _client.SubmitTradeAsync(tradeJson).GetAwaiter().GetResult();
                return result.Success;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }
        
        /// <summary>
        /// Perform health check
        /// </summary>
        /// <param name="source">Source identifier</param>
        /// <param name="responseJson">JSON response from server</param>
        /// <returns>True if healthy</returns>
        public static bool HealthCheck(string source, out string responseJson)
        {
            responseJson = "";
            if (!_initialized) return false;
            
            try
            {
                var result = _client.HealthCheckAsync(source).GetAwaiter().GetResult();
                responseJson = result.ResponseJson;
                return result.Success;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }
        
        /// <summary>
        /// Submit elastic hedge update
        /// </summary>
        /// <param name="updateJson">JSON representation of elastic update</param>
        /// <returns>Success status</returns>
        public static bool SubmitElasticUpdate(string updateJson)
        {
            if (!_initialized) return false;
            
            try
            {
                var result = _client.SubmitElasticUpdateAsync(updateJson).GetAwaiter().GetResult();
                return result.Success;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }
        
        /// <summary>
        /// Submit trailing stop update
        /// </summary>
        /// <param name="updateJson">JSON representation of trailing update</param>
        /// <returns>Success status</returns>
        public static bool SubmitTrailingUpdate(string updateJson)
        {
            if (!_initialized) return false;
            
            try
            {
                var result = _client.SubmitTrailingUpdateAsync(updateJson).GetAwaiter().GetResult();
                return result.Success;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }
        
        /// <summary>
        /// Notify hedge closure
        /// </summary>
        /// <param name="notificationJson">JSON representation of hedge closure</param>
        /// <returns>Success status</returns>
        public static bool NotifyHedgeClose(string notificationJson)
        {
            if (!_initialized) return false;
            
            try
            {
                var result = _client.NotifyHedgeCloseAsync(notificationJson).GetAwaiter().GetResult();
                return result.Success;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }
        
        /// <summary>
        /// Start streaming connection for real-time updates
        /// </summary>
        /// <param name="onTradeReceived">Callback for received trades (JSON)</param>
        /// <returns>True if stream started</returns>
        public static bool StartTradingStream(Action<string> onTradeReceived)
        {
            if (!_initialized) return false;
            
            try
            {
                _client.StartTradingStream(onTradeReceived);
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }
        
        /// <summary>
        /// Stop trading stream
        /// </summary>
        public static void StopTradingStream()
        {
            _client?.StopTradingStream();
        }
        
        /// <summary>
        /// Check if client is connected
        /// </summary>
        public static bool IsConnected => _initialized && _client?.IsConnected == true;
        
        /// <summary>
        /// Last error message
        /// </summary>
        public static string LastError { get; private set; } = "";
        
        /// <summary>
        /// Cleanup and dispose
        /// </summary>
        public static void Dispose()
        {
            _client?.Dispose();
            _initialized = false;
        }
    }
    
    /// <summary>
    /// Result wrapper for operations
    /// </summary>
    public class OperationResult
    {
        public bool Success { get; set; }
        public string ResponseJson { get; set; } = "";
        public string ErrorMessage { get; set; } = "";
    }
}
```

**Internal Implementation (TradingClient.cs):**
```csharp
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Net.Client;
using Trading.Proto;

namespace NTGrpcClient
{
    internal interface ITradingClient : IDisposable
    {
        bool IsConnected { get; }
        Task<OperationResult> SubmitTradeAsync(string tradeJson);
        Task<OperationResult> HealthCheckAsync(string source);
        Task<OperationResult> SubmitElasticUpdateAsync(string updateJson);
        Task<OperationResult> SubmitTrailingUpdateAsync(string updateJson);
        Task<OperationResult> NotifyHedgeCloseAsync(string notificationJson);
        void StartTradingStream(Action<string> onTradeReceived);
        void StopTradingStream();
    }
    
    internal class TradingClient : ITradingClient
    {
        private readonly GrpcChannel _channel;
        private readonly TradingService.TradingServiceClient _client;
        private readonly StreamingService.StreamingServiceClient _streamingClient;
        private CancellationTokenSource _streamCancellation;
        private bool _disposed = false;
        
        public bool IsConnected { get; private set; }
        
        public TradingClient(string serverAddress)
        {
            var httpHandler = new HttpClientHandler();
            
            _channel = GrpcChannel.ForAddress(serverAddress, new GrpcChannelOptions
            {
                HttpHandler = httpHandler,
                KeepAliveInterval = TimeSpan.FromSeconds(30),
                KeepAliveTimeout = TimeSpan.FromSeconds(5),
                MaxRetryAttempts = 3
            });
            
            _client = new TradingService.TradingServiceClient(_channel);
            _streamingClient = new StreamingService.StreamingServiceClient(_channel);
            
            // Test connection
            TestConnection();
        }
        
        private async void TestConnection()
        {
            try
            {
                var request = new HealthRequest { Source = "nt_addon_init" };
                var response = await _client.HealthCheckAsync(request);
                IsConnected = response.Status == "healthy";
            }
            catch
            {
                IsConnected = false;
            }
        }
        
        public async Task<OperationResult> SubmitTradeAsync(string tradeJson)
        {
            try
            {
                var trade = JsonToProtoTrade(tradeJson);
                var response = await _client.SubmitTradeAsync(trade);
                
                return new OperationResult
                {
                    Success = response.Status == "success",
                    ResponseJson = JsonSerializer.Serialize(new { status = response.Status, message = response.Message })
                };
            }
            catch (Exception ex)
            {
                return new OperationResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }
        
        public async Task<OperationResult> HealthCheckAsync(string source)
        {
            try
            {
                var request = new HealthRequest { Source = source };
                var response = await _client.HealthCheckAsync(request);
                
                var responseJson = JsonSerializer.Serialize(new
                {
                    status = response.Status,
                    queue_size = response.QueueSize,
                    net_position = response.NetPosition,
                    hedge_size = response.HedgeSize
                });
                
                return new OperationResult
                {
                    Success = response.Status == "healthy",
                    ResponseJson = responseJson
                };
            }
            catch (Exception ex)
            {
                return new OperationResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }
        
        public async Task<OperationResult> SubmitElasticUpdateAsync(string updateJson)
        {
            try
            {
                var update = JsonToProtoElasticUpdate(updateJson);
                var response = await _client.SubmitElasticUpdateAsync(update);
                
                return new OperationResult
                {
                    Success = response.Status == "success",
                    ResponseJson = JsonSerializer.Serialize(new { status = response.Status })
                };
            }
            catch (Exception ex)
            {
                return new OperationResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }
        
        public async Task<OperationResult> SubmitTrailingUpdateAsync(string updateJson)
        {
            try
            {
                var update = JsonToProtoTrailingUpdate(updateJson);
                var response = await _client.SubmitTrailingUpdateAsync(update);
                
                return new OperationResult
                {
                    Success = response.Status == "success",
                    ResponseJson = JsonSerializer.Serialize(new { status = response.Status })
                };
            }
            catch (Exception ex)
            {
                return new OperationResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }
        
        public async Task<OperationResult> NotifyHedgeCloseAsync(string notificationJson)
        {
            try
            {
                var notification = JsonToProtoHedgeClose(notificationJson);
                var response = await _client.NotifyHedgeCloseAsync(notification);
                
                return new OperationResult
                {
                    Success = response.Status == "success",
                    ResponseJson = JsonSerializer.Serialize(new { status = response.Status })
                };
            }
            catch (Exception ex)
            {
                return new OperationResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }
        
        public void StartTradingStream(Action<string> onTradeReceived)
        {
            _streamCancellation = new CancellationTokenSource();
            
            Task.Run(async () =>
            {
                try
                {
                    using var stream = _streamingClient.TradingStream();
                    
                    // Send initial request
                    await stream.RequestStream.WriteAsync(new Trade { Id = "init_stream" });
                    
                    // Read responses
                    await foreach (var trade in stream.ResponseStream.ReadAllAsync(_streamCancellation.Token))
                    {
                        var tradeJson = ProtoTradeToJson(trade);
                        onTradeReceived?.Invoke(tradeJson);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Stream was cancelled - normal
                }
                catch (Exception)
                {
                    // Stream error - could try to reconnect
                }
            });
        }
        
        public void StopTradingStream()
        {
            _streamCancellation?.Cancel();
        }
        
        // JSON conversion methods
        private Trade JsonToProtoTrade(string json)
        {
            var data = JsonSerializer.Deserialize<JsonElement>(json);
            
            return new Trade
            {
                Id = data.GetProperty("id").GetString() ?? "",
                BaseId = data.GetProperty("base_id").GetString() ?? "",
                Timestamp = data.GetProperty("timestamp").GetInt64(),
                Action = data.GetProperty("action").GetString() ?? "",
                Quantity = data.GetProperty("quantity").GetDouble(),
                Price = data.GetProperty("price").GetDouble(),
                TotalQuantity = data.GetProperty("total_quantity").GetInt32(),
                ContractNum = data.GetProperty("contract_num").GetInt32(),
                OrderType = data.GetProperty("order_type").GetString() ?? "",
                MeasurementPips = data.GetProperty("measurement_pips").GetInt32(),
                RawMeasurement = data.GetProperty("raw_measurement").GetDouble(),
                Instrument = data.GetProperty("instrument").GetString() ?? "",
                AccountName = data.GetProperty("account_name").GetString() ?? "",
                NtBalance = data.GetProperty("nt_balance").GetDouble(),
                NtDailyPnl = data.GetProperty("nt_daily_pnl").GetDouble(),
                NtTradeResult = data.GetProperty("nt_trade_result").GetString() ?? "",
                NtSessionTrades = data.GetProperty("nt_session_trades").GetInt32()
            };
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
                account_name = trade.AccountName,
                nt_balance = trade.NtBalance,
                nt_daily_pnl = trade.NtDailyPnl,
                nt_trade_result = trade.NtTradeResult,
                nt_session_trades = trade.NtSessionTrades
            };
            
            return JsonSerializer.Serialize(data);
        }
        
        // Additional conversion methods for other message types...
        
        public void Dispose()
        {
            if (!_disposed)
            {
                _streamCancellation?.Cancel();
                _channel?.Dispose();
                _disposed = true;
            }
        }
    }
}
```

#### 3.3 NinjaTrader Addon Integration

**Update MultiStratManager.csproj:**
```xml
<ItemGroup>
  <Reference Include="NTGrpcClient">
    <HintPath>External\NTGrpcClient.dll</HintPath>
    <Private>True</Private>
  </Reference>
</ItemGroup>
```

**Replace HTTP calls in MultiStratManager.cs:**
```csharp
using NTGrpcClient;

public class MultiStratManager : NinjaTrader.NinjaScript.AddOnBase
{
    private bool useGrpc = true; // Feature flag
    private bool grpcInitialized = false;
    
    protected override void OnStateChange()
    {
        if (State == State.SetDefaults)
        {
            // Initialize gRPC client
            if (useGrpc)
            {
                grpcInitialized = TradingGrpcClient.Initialize("http://localhost:50051");
                if (!grpcInitialized)
                {
                    LogError("GRPC", $"Failed to initialize gRPC: {TradingGrpcClient.LastError}");
                    LogInfo("FALLBACK", "Falling back to HTTP communication");
                }
            }
        }
        else if (State == State.Terminated)
        {
            if (grpcInitialized)
            {
                TradingGrpcClient.StopTradingStream();
                TradingGrpcClient.Dispose();
            }
        }
    }
    
    // Replace existing HTTP trade submission
    private async Task<bool> SubmitTradeAsync(Dictionary<string, object> tradeData)
    {
        if (grpcInitialized)
        {
            try
            {
                string tradeJson = SimpleJson.SerializeObject(tradeData);
                bool success = TradingGrpcClient.SubmitTrade(tradeJson);
                
                if (success)
                {
                    LogInfo("GRPC", "Trade submitted successfully via gRPC");
                    return true;
                }
                else
                {
                    LogError("GRPC", $"gRPC trade submission failed: {TradingGrpcClient.LastError}");
                }
            }
            catch (Exception ex)
            {
                LogError("GRPC", $"gRPC error: {ex.Message}");
            }
        }
        
        // Fallback to HTTP
        LogInfo("FALLBACK", "Using HTTP fallback for trade submission");
        return await SubmitTradeViaHttpAsync(tradeData);
    }
    
    // Replace health check
    private async Task<bool> PerformHealthCheckAsync()
    {
        if (grpcInitialized)
        {
            string responseJson;
            bool healthy = TradingGrpcClient.HealthCheck("addon", out responseJson);
            
            if (healthy)
            {
                // Process response JSON...
                return true;
            }
            else
            {
                LogError("GRPC", $"gRPC health check failed: {TradingGrpcClient.LastError}");
            }
        }
        
        // Fallback to HTTP
        return await PerformHttpHealthCheckAsync();
    }
    
    // Start streaming for real-time updates
    private void StartRealtimeUpdates()
    {
        if (grpcInitialized)
        {
            bool streamStarted = TradingGrpcClient.StartTradingStream(OnTradeReceived);
            if (streamStarted)
            {
                LogInfo("GRPC", "Started gRPC trading stream");
                return;
            }
        }
        
        // Fallback to WebSocket/HTTP polling
        StartWebSocketConnection();
    }
    
    private void OnTradeReceived(string tradeJson)
    {
        try
        {
            var tradeData = SimpleJson.DeserializeObject<Dictionary<string, object>>(tradeJson);
            ProcessIncomingTrade(tradeData);
        }
        catch (Exception ex)
        {
            LogError("GRPC", $"Error processing received trade: {ex.Message}");
        }
    }
}

### Phase 4: MT5 EA Implementation Strategy

#### 4.1 Recommended Approach: Custom gRPC DLL for MT5

Creating a custom C++ DLL that implements gRPC functionality for MT5 is the best long-term solution, providing:
- **Native gRPC performance** with streaming capabilities
- **Type safety** through protocol buffers
- **Unified architecture** across all components
- **Future-proof** integration

##### 4.1.1 DLL Architecture Overview
```
MT5 EA (MQL5) ←→ MT5GrpcClient.dll (C++) ←→ Go Bridge Server (gRPC)
```

##### 4.1.2 C++ DLL Implementation

**File Structure:**
```
MT5/dll/
├── MT5GrpcClient/
│   ├── src/
│   │   ├── mt5_grpc_client.cpp
│   │   ├── mt5_grpc_client.h
│   │   └── grpc_wrapper.cpp
│   ├── proto/
│   │   └── trading.proto (same as main proto)
│   ├── CMakeLists.txt
│   └── vcpkg.json (for dependencies)
```

**Header File (mt5_grpc_client.h):**
```cpp
#pragma once

#include <windows.h>
#include <string>
#include <memory>
#include <grpcpp/grpcpp.h>
#include "trading.grpc.pb.h"

// Export macros for MQL5
#define MT5_EXPORT extern "C" __declspec(dllexport)

// Connection management
MT5_EXPORT int MT5_InitializeGrpcClient(const wchar_t* server_address);
MT5_EXPORT int MT5_DisconnectGrpcClient();
MT5_EXPORT int MT5_IsConnected();

// Health check
MT5_EXPORT int MT5_HealthCheck(const wchar_t* source, int open_positions, wchar_t* response, int response_size);

// Trade operations
MT5_EXPORT int MT5_GetNextTrade(wchar_t* trade_json, int buffer_size);
MT5_EXPORT int MT5_SubmitTradeResult(const wchar_t* result_json);
MT5_EXPORT int MT5_NotifyHedgeClose(const wchar_t* notification_json);

// Streaming operations
MT5_EXPORT int MT5_StartTradeStream();
MT5_EXPORT int MT5_StopTradeStream();
MT5_EXPORT int MT5_HasPendingTrade();

// Error handling
MT5_EXPORT int MT5_GetLastError(wchar_t* error_msg, int buffer_size);
MT5_EXPORT void MT5_ClearLastError();

// Connection status callback
typedef void (*ConnectionStatusCallback)(int is_connected);
MT5_EXPORT int MT5_SetConnectionCallback(ConnectionStatusCallback callback);
```

**Implementation (mt5_grpc_client.cpp):**
```cpp
#include "mt5_grpc_client.h"
#include <queue>
#include <mutex>
#include <thread>
#include <condition_variable>
#include <json/json.h>

class MT5GrpcClient {
private:
    std::unique_ptr<grpc::Channel> channel_;
    std::unique_ptr<trading::TradingService::Stub> stub_;
    std::unique_ptr<trading::StreamingService::Stub> streaming_stub_;
    
    // Streaming management
    std::unique_ptr<grpc::ClientReaderWriter<trading::HealthRequest, trading::Trade>> trade_stream_;
    std::thread stream_thread_;
    std::atomic<bool> stream_active_{false};
    
    // Trade queue
    std::queue<trading::Trade> pending_trades_;
    std::mutex trades_mutex_;
    std::condition_variable trades_cv_;
    
    // Connection status
    std::atomic<bool> connected_{false};
    ConnectionStatusCallback status_callback_ = nullptr;
    
    // Error handling
    std::string last_error_;
    std::mutex error_mutex_;

public:
    bool Initialize(const std::string& server_address) {
        try {
            // Create channel with keepalive settings
            grpc::ChannelArguments args;
            args.SetInt(GRPC_ARG_KEEPALIVE_TIME_MS, 30000);
            args.SetInt(GRPC_ARG_KEEPALIVE_TIMEOUT_MS, 5000);
            args.SetInt(GRPC_ARG_KEEPALIVE_PERMIT_WITHOUT_CALLS, 1);
            args.SetInt(GRPC_ARG_HTTP2_MAX_PINGS_WITHOUT_DATA, 0);
            args.SetInt(GRPC_ARG_HTTP2_MIN_RECV_PING_INTERVAL_WITHOUT_DATA_MS, 300000);
            
            channel_ = grpc::CreateCustomChannel(server_address, grpc::InsecureChannelCredentials(), args);
            stub_ = trading::TradingService::NewStub(channel_);
            streaming_stub_ = trading::StreamingService::NewStub(channel_);
            
            // Test connection
            if (TestConnection()) {
                connected_ = true;
                NotifyConnectionStatus(true);
                return true;
            }
            return false;
        }
        catch (const std::exception& e) {
            SetLastError("Failed to initialize gRPC client: " + std::string(e.what()));
            return false;
        }
    }
    
    bool StartTradeStream() {
        if (!connected_) return false;
        
        stream_active_ = true;
        stream_thread_ = std::thread([this]() {
            try {
                grpc::ClientContext context;
                trade_stream_ = streaming_stub_->TradingStream(&context);
                
                // Send initial health request
                trading::HealthRequest health_req;
                health_req.set_source("hedgebot");
                trade_stream_->Write(health_req);
                
                // Read incoming trades
                trading::Trade incoming_trade;
                while (stream_active_ && trade_stream_->Read(&incoming_trade)) {
                    {
                        std::lock_guard<std::mutex> lock(trades_mutex_);
                        pending_trades_.push(incoming_trade);
                    }
                    trades_cv_.notify_one();
                }
                
                trade_stream_->WritesDone();
                auto status = trade_stream_->Finish();
                if (!status.ok()) {
                    SetLastError("Stream finished with error: " + status.error_message());
                }
            }
            catch (const std::exception& e) {
                SetLastError("Stream error: " + std::string(e.what()));
            }
        });
        
        return true;
    }
    
    bool GetNextTrade(std::string& trade_json) {
        std::unique_lock<std::mutex> lock(trades_mutex_);
        
        // Wait for trade with timeout
        if (trades_cv_.wait_for(lock, std::chrono::milliseconds(500), [this] { return !pending_trades_.empty(); })) {
            auto trade = pending_trades_.front();
            pending_trades_.pop();
            lock.unlock();
            
            // Convert protobuf to JSON
            trade_json = ConvertTradeToJson(trade);
            return true;
        }
        
        return false; // No trade available
    }
    
    bool SubmitTradeResult(const std::string& result_json) {
        if (!connected_) return false;
        
        try {
            // Parse JSON to protobuf
            auto trade_result = ParseTradeResultFromJson(result_json);
            
            grpc::ClientContext context;
            context.set_deadline(std::chrono::system_clock::now() + std::chrono::seconds(5));
            
            trading::GenericResponse response;
            auto status = stub_->SubmitTradeResult(&context, trade_result, &response);
            
            if (status.ok()) {
                return response.status() == "success";
            } else {
                SetLastError("SubmitTradeResult failed: " + status.error_message());
                return false;
            }
        }
        catch (const std::exception& e) {
            SetLastError("Error submitting trade result: " + std::string(e.what()));
            return false;
        }
    }
    
    bool HealthCheck(const std::string& source, int open_positions, std::string& response) {
        if (!connected_) return false;
        
        try {
            trading::HealthRequest request;
            request.set_source(source);
            request.set_open_positions(open_positions);
            
            trading::HealthResponse health_response;
            grpc::ClientContext context;
            context.set_deadline(std::chrono::system_clock::now() + std::chrono::seconds(3));
            
            auto status = stub_->HealthCheck(&context, request, &health_response);
            
            if (status.ok()) {
                // Convert response to JSON
                Json::Value json_response;
                json_response["status"] = health_response.status();
                json_response["queue_size"] = health_response.queue_size();
                json_response["net_position"] = health_response.net_position();
                json_response["hedge_size"] = health_response.hedge_size();
                
                Json::StreamWriterBuilder builder;
                response = Json::writeString(builder, json_response);
                return true;
            } else {
                SetLastError("Health check failed: " + status.error_message());
                return false;
            }
        }
        catch (const std::exception& e) {
            SetLastError("Health check error: " + std::string(e.what()));
            return false;
        }
    }

private:
    std::string ConvertTradeToJson(const trading::Trade& trade) {
        Json::Value json_trade;
        json_trade["id"] = trade.id();
        json_trade["base_id"] = trade.base_id();
        json_trade["timestamp"] = static_cast<int64_t>(trade.timestamp());
        json_trade["action"] = trade.action();
        json_trade["quantity"] = trade.quantity();
        json_trade["price"] = trade.price();
        json_trade["total_quantity"] = trade.total_quantity();
        json_trade["contract_num"] = trade.contract_num();
        json_trade["order_type"] = trade.order_type();
        json_trade["measurement_pips"] = trade.measurement_pips();
        json_trade["raw_measurement"] = trade.raw_measurement();
        json_trade["instrument"] = trade.instrument();
        json_trade["account_name"] = trade.account_name();
        json_trade["nt_balance"] = trade.nt_balance();
        json_trade["nt_daily_pnl"] = trade.nt_daily_pnl();
        json_trade["nt_trade_result"] = trade.nt_trade_result();
        json_trade["nt_session_trades"] = trade.nt_session_trades();
        
        Json::StreamWriterBuilder builder;
        return Json::writeString(builder, json_trade);
    }
    
    trading::MT5TradeResult ParseTradeResultFromJson(const std::string& json_str) {
        Json::CharReaderBuilder builder;
        Json::Value root;
        std::string errors;
        
        std::unique_ptr<Json::CharReader> reader(builder.newCharReader());
        if (!reader->parse(json_str.c_str(), json_str.c_str() + json_str.length(), &root, &errors)) {
            throw std::runtime_error("Failed to parse JSON: " + errors);
        }
        
        trading::MT5TradeResult result;
        result.set_status(root["status"].asString());
        result.set_ticket(root["ticket"].asUInt64());
        result.set_volume(root["volume"].asDouble());
        result.set_is_close(root["is_close"].asBool());
        result.set_id(root["id"].asString());
        
        return result;
    }
};

// Global instance
static std::unique_ptr<MT5GrpcClient> g_client;
static std::mutex g_client_mutex;

// DLL exports implementation
MT5_EXPORT int MT5_InitializeGrpcClient(const wchar_t* server_address) {
    std::lock_guard<std::mutex> lock(g_client_mutex);
    
    try {
        // Convert wide string to string
        int len = WideCharToMultiByte(CP_UTF8, 0, server_address, -1, NULL, 0, NULL, NULL);
        std::string address(len, 0);
        WideCharToMultiByte(CP_UTF8, 0, server_address, -1, &address[0], len, NULL, NULL);
        address.resize(len - 1); // Remove null terminator
        
        g_client = std::make_unique<MT5GrpcClient>();
        return g_client->Initialize(address) ? 1 : 0;
    }
    catch (...) {
        return 0;
    }
}

MT5_EXPORT int MT5_GetNextTrade(wchar_t* trade_json, int buffer_size) {
    std::lock_guard<std::mutex> lock(g_client_mutex);
    
    if (!g_client) return 0;
    
    std::string json_str;
    if (g_client->GetNextTrade(json_str)) {
        // Convert to wide string
        int len = MultiByteToWideChar(CP_UTF8, 0, json_str.c_str(), -1, NULL, 0);
        if (len <= buffer_size) {
            MultiByteToWideChar(CP_UTF8, 0, json_str.c_str(), -1, trade_json, buffer_size);
            return 1;
        }
    }
    return 0;
}

MT5_EXPORT int MT5_StartTradeStream() {
    std::lock_guard<std::mutex> lock(g_client_mutex);
    return (g_client && g_client->StartTradeStream()) ? 1 : 0;
}

// ... implement other exports
```

##### 4.1.3 MQL5 Integration

**Updated ACHedgeMaster.mq5:**
```mql5
#property version   "3.00"
#property description "Hedge Receiver EA with gRPC support via DLL"

// Import DLL functions
#import "MT5GrpcClient.dll"
   int MT5_InitializeGrpcClient(string server_address);
   int MT5_DisconnectGrpcClient();
   int MT5_IsConnected();
   int MT5_HealthCheck(string source, int open_positions, string& response);
   int MT5_GetNextTrade(string& trade_json);
   int MT5_SubmitTradeResult(string result_json);
   int MT5_NotifyHedgeClose(string notification_json);
   int MT5_StartTradeStream();
   int MT5_StopTradeStream();
   int MT5_HasPendingTrade();
   int MT5_GetLastError(string& error_msg);
   void MT5_ClearLastError();
#import

// gRPC connection settings
input group "===== gRPC Connection Settings =====";
input string GrpcServerAddress = "localhost:50051";  // gRPC Server Address
input bool   UseGrpcStreaming = true;                // Use gRPC streaming instead of polling
input bool   FallbackToHttp = true;                  // Fallback to HTTP if gRPC fails

// Global variables
bool g_grpcConnected = false;
bool g_grpcStreamingActive = false;
bool g_httpFallbackActive = false;

//+------------------------------------------------------------------+
//| Expert initialization function                                   |
//+------------------------------------------------------------------+
int OnInit()
{
    Print("=== Initializing ACHedgeMaster with gRPC support ===");
    
    // Initialize gRPC client
    if (MT5_InitializeGrpcClient(GrpcServerAddress) == 1) {
        g_grpcConnected = true;
        Print("gRPC client initialized successfully: ", GrpcServerAddress);
        
        // Start streaming if enabled
        if (UseGrpcStreaming) {
            if (MT5_StartTradeStream() == 1) {
                g_grpcStreamingActive = true;
                Print("gRPC streaming started successfully");
                // Set faster timer for stream processing
                EventSetMillisecondTimer(100);
            } else {
                Print("Failed to start gRPC streaming, falling back to polling");
                EventSetMillisecondTimer(200);
            }
        } else {
            Print("gRPC polling mode enabled");
            EventSetMillisecondTimer(200);
        }
    } else {
        Print("Failed to initialize gRPC client");
        
        if (FallbackToHttp) {
            Print("Falling back to HTTP communication");
            g_httpFallbackActive = true;
            EventSetMillisecondTimer(200);
        } else {
            Print("gRPC connection required but failed. EA stopping.");
            return INIT_FAILED;
        }
    }
    
    // Initialize other components...
    return INIT_SUCCEEDED;
}

//+------------------------------------------------------------------+
//| Expert deinitialization function                                |
//+------------------------------------------------------------------+
void OnDeinit(const int reason)
{
    if (g_grpcStreamingActive) {
        MT5_StopTradeStream();
    }
    
    if (g_grpcConnected) {
        MT5_DisconnectGrpcClient();
    }
    
    Print("ACHedgeMaster deinitialized");
}

//+------------------------------------------------------------------+
//| Timer function - optimized for gRPC                            |
//+------------------------------------------------------------------+
void OnTimer()
{
    g_timerCounter++;
    
    if (g_grpcConnected && !g_httpFallbackActive) {
        // gRPC mode
        if (g_grpcStreamingActive) {
            ProcessGrpcStreamingTrades();
        } else {
            ProcessGrpcPollingTrades();
        }
        
        // Health check every 30 seconds (150 * 200ms)
        if (g_timerCounter % 150 == 0) {
            PerformGrpcHealthCheck();
        }
    } else if (g_httpFallbackActive) {
        // HTTP fallback mode
        ProcessHttpTrades();
        
        // Try to reconnect to gRPC occasionally
        if (g_timerCounter % 300 == 0) { // Every 60 seconds
            AttemptGrpcReconnection();
        }
    }
}

//+------------------------------------------------------------------+
//| Process trades via gRPC streaming                               |
//+------------------------------------------------------------------+
void ProcessGrpcStreamingTrades()
{
    string trade_json;
    
    // Check for pending trades (non-blocking)
    while (MT5_GetNextTrade(trade_json) == 1) {
        if (trade_json != "") {
            ProcessTradeFromJson(trade_json);
        }
    }
}

//+------------------------------------------------------------------+
//| Process trades via gRPC polling                                 |
//+------------------------------------------------------------------+
void ProcessGrpcPollingTrades()
{
    string trade_json;
    
    if (MT5_GetNextTrade(trade_json) == 1 && trade_json != "") {
        ProcessTradeFromJson(trade_json);
    }
}

//+------------------------------------------------------------------+
//| Process trade from JSON (common function)                       |
//+------------------------------------------------------------------+
void ProcessTradeFromJson(string trade_json)
{
    // Parse JSON and execute trade (existing logic)
    Print("Received trade via gRPC: ", trade_json);
    
    // Extract trade data
    string baseIdFromJson = GetJSONStringValue(trade_json, "base_id");
    string incomingNtAction = GetJSONStringValue(trade_json, "action");
    double incomingNtQuantity = GetJSONDouble(trade_json, "quantity");
    
    // Process trade (existing logic)
    if (baseIdFromJson != "" && incomingNtAction != "") {
        ExecuteTradeLogic(baseIdFromJson, incomingNtAction, incomingNtQuantity, trade_json);
    }
}

//+------------------------------------------------------------------+
//| Perform gRPC health check                                       |
//+------------------------------------------------------------------+
void PerformGrpcHealthCheck()
{
    string response;
    int open_pos = CountManagedPositions();
    
    if (MT5_HealthCheck("hedgebot", open_pos, response) == 1) {
        if (!g_bridgeConnected) {
            Print("gRPC connection restored");
            g_bridgeConnected = true;
            UpdateStatusIndicator("Connected (gRPC)", clrGreen);
        }
    } else {
        if (g_bridgeConnected) {
            Print("gRPC health check failed");
            g_bridgeConnected = false;
            UpdateStatusIndicator("Disconnected", clrRed);
            
            // Consider HTTP fallback
            if (FallbackToHttp && !g_httpFallbackActive) {
                Print("Activating HTTP fallback");
                g_httpFallbackActive = true;
            }
        }
    }
}

//+------------------------------------------------------------------+
//| Submit trade result via gRPC                                    |
//+------------------------------------------------------------------+
void SubmitTradeResultGrpc(ulong ticket, double volume, bool is_close, string trade_id, string status)
{
    if (!g_grpcConnected || g_httpFallbackActive) {
        SubmitTradeResultHttp(ticket, volume, is_close, trade_id, status);
        return;
    }
    
    // Create JSON for trade result
    string result_json = StringFormat(
        "{\"status\":\"%s\",\"ticket\":%I64u,\"volume\":%.2f,\"is_close\":%s,\"id\":\"%s\"}",
        status, ticket, volume, is_close ? "true" : "false", trade_id
    );
    
    if (MT5_SubmitTradeResult(result_json) == 1) {
        Print("Trade result submitted successfully via gRPC");
    } else {
        Print("Failed to submit trade result via gRPC, trying HTTP fallback");
        SubmitTradeResultHttp(ticket, volume, is_close, trade_id, status);
    }
}

//+------------------------------------------------------------------+
//| Attempt gRPC reconnection                                       |
//+------------------------------------------------------------------+
void AttemptGrpcReconnection()
{
    if (g_grpcConnected) return;
    
    Print("Attempting gRPC reconnection...");
    
    if (MT5_InitializeGrpcClient(GrpcServerAddress) == 1) {
        g_grpcConnected = true;
        g_httpFallbackActive = false;
        
        if (UseGrpcStreaming) {
            if (MT5_StartTradeStream() == 1) {
                g_grpcStreamingActive = true;
            }
        }
        
        Print("gRPC reconnection successful");
        UpdateStatusIndicator("Connected (gRPC)", clrGreen);
    }
}
```

##### 4.1.4 Build System

**CMakeLists.txt:**
```cmake
cmake_minimum_required(VERSION 3.20)
project(MT5GrpcClient)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Find packages
find_package(Protobuf REQUIRED)
find_package(gRPC REQUIRED)
find_package(PkgConfig REQUIRED)
pkg_check_modules(JSONCPP jsoncpp)

# Proto files
set(PROTO_FILES "${CMAKE_CURRENT_SOURCE_DIR}/proto/trading.proto")
set(PROTO_SRC_DIR "${CMAKE_CURRENT_BINARY_DIR}/proto-src")
file(MAKE_DIRECTORY ${PROTO_SRC_DIR})

# Generate protobuf and gRPC files
protobuf_generate_cpp(PROTO_SRCS PROTO_HDRS ${PROTO_FILES})
set(GRPC_SRCS "${PROTO_SRC_DIR}/trading.grpc.pb.cc")
set(GRPC_HDRS "${PROTO_SRC_DIR}/trading.grpc.pb.h")

add_custom_command(
    OUTPUT "${GRPC_SRCS}" "${GRPC_HDRS}"
    COMMAND protobuf::protoc
    ARGS --grpc_out "${PROTO_SRC_DIR}"
         --cpp_out "${PROTO_SRC_DIR}"
         --plugin=protoc-gen-grpc="$<TARGET_FILE:gRPC::grpc_cpp_plugin>"
         "${PROTO_FILES}"
    DEPENDS "${PROTO_FILES}"
)

# Create DLL
add_library(MT5GrpcClient SHARED
    src/mt5_grpc_client.cpp
    src/grpc_wrapper.cpp
    ${PROTO_SRCS}
    ${GRPC_SRCS}
)

target_include_directories(MT5GrpcClient PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/src
    ${PROTO_SRC_DIR}
    ${JSONCPP_INCLUDE_DIRS}
)

target_link_libraries(MT5GrpcClient
    gRPC::grpc++
    protobuf::libprotobuf
    ${JSONCPP_LIBRARIES}
)

# Set DLL properties
set_target_properties(MT5GrpcClient PROPERTIES
    WINDOWS_EXPORT_ALL_SYMBOLS ON
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}/../"
)
```

##### 4.1.5 Advantages of DLL Approach

1. **Performance**: Native gRPC streaming eliminates polling overhead
2. **Type Safety**: Full protocol buffer validation
3. **Unified Architecture**: All components use gRPC
4. **Future-Proof**: Easy to extend with new gRPC features
5. **Error Handling**: Native gRPC status codes and retry mechanisms
6. **Connection Management**: Built-in keepalive and reconnection

##### 4.1.6 Fallback Strategy

The DLL includes HTTP fallback capability:
- If gRPC connection fails, automatically switch to HTTP
- Periodic reconnection attempts to gRPC
- Seamless transition between protocols
- Full compatibility with existing HTTP endpoints

### Phase 5: Logging Migration to Local NinjaScript Output

#### 5.1 Current Logging Patterns to Replace
```csharp
// Current patterns that need to be replaced:
LogInfo("CATEGORY", message);           // Replace with local output
LogError("CATEGORY", message);          // Replace with local output
LogDebug("CATEGORY", message);          // Replace with local output
LogWarn("CATEGORY", message);           // Replace with local output

// HTTP logging to bridge server
var response = await httpClient.PostAsync($"{bridgeServerUrl}/api/logs", content);

// Asynchronous log batching
private static readonly ConcurrentQueue<Dictionary<string, object>> logQueue;
```

#### 5.2 New Local Logging Implementation
```csharp
// New local-only logging implementation
public static class LocalLogger
{
    private static readonly object _lock = new object();
    private static readonly StringBuilder _logBuffer = new StringBuilder();
    
    public static void LogInfo(string category, string message)
    {
        WriteToNinjaOutput("INFO", category, message);
    }
    
    public static void LogError(string category, string message)
    {
        WriteToNinjaOutput("ERROR", category, message);
    }
    
    public static void LogDebug(string category, string message)
    {
        WriteToNinjaOutput("DEBUG", category, message);
    }
    
    public static void LogWarn(string category, string message)
    {
        WriteToNinjaOutput("WARN", category, message);
    }
    
    private static void WriteToNinjaOutput(string level, string category, string message)
    {
        lock (_lock)
        {
            try
            {
                string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                string logEntry = $"[{timestamp}][{level}][{category}] {message}";
                
                // Write to NinjaTrader output
                NinjaTrader.Code.Output.Process(logEntry, PrintTo.OutputTab1);
                
                // Also write to local buffer for debugging
                _logBuffer.AppendLine(logEntry);
                
                // Trim buffer if it gets too large
                if (_logBuffer.Length > 100000)
                {
                    var lines = _logBuffer.ToString().Split('\n');
                    _logBuffer.Clear();
                    for (int i = lines.Length / 2; i < lines.Length; i++)
                    {
                        _logBuffer.AppendLine(lines[i]);
                    }
                }
            }
            catch (Exception ex)
            {
                // Fallback to basic output if anything fails
                NinjaTrader.Code.Output.Process($"[LOG ERROR] {message}", PrintTo.OutputTab1);
            }
        }
    }
}
```

#### 5.3 Replace All Logging Calls
```csharp
// Replace throughout codebase:
// OLD: LogInfo("TRADING", "Trade submitted");
// NEW: LocalLogger.LogInfo("TRADING", "Trade submitted");

// OLD: LogError("CONNECTION", "Failed to connect");
// NEW: LocalLogger.LogError("CONNECTION", "Failed to connect");

// Remove all HTTP logging infrastructure:
// - Remove logQueue
// - Remove logFlushTimer
// - Remove /api/logs endpoint calls
// - Remove async log batching
```

### Phase 6: Implementation Steps

#### 6.1 Preparation Phase
1. **Create Protocol Buffer Definitions**
   - Define all message types in `.proto` files
   - Generate Go and C# code
   - Create shared package for common types

2. **Set Up Development Environment**
   - Install gRPC tools for Go and C#
   - Set up code generation pipelines
   - Create testing infrastructure

#### 6.2 Implementation Phase
1. **Go Bridge Server**
   - Implement gRPC server alongside existing HTTP
   - Add feature flags for gradual migration
   - Implement bidirectional streaming
   - Add comprehensive error handling

2. **C# NinjaTrader Addon**
   - Add gRPC client libraries
   - Implement gRPC communication methods
   - Replace HTTP calls with gRPC calls
   - Add fallback mechanisms
   - Convert all logging to local NinjaScript output

3. **MT5 Integration**
   - Implement HTTP-to-gRPC proxy if needed
   - Or maintain HTTP endpoints for MT5
   - Test integration thoroughly

#### 6.3 Testing Phase
1. **Unit Testing**
   - Test all gRPC service methods
   - Test message serialization/deserialization
   - Test error handling scenarios

2. **Integration Testing**
   - Test complete communication flows
   - Test fallback mechanisms
   - Test performance under load
   - Test network failure scenarios

3. **Performance Testing**
   - Compare gRPC vs HTTP performance
   - Test streaming performance
   - Measure latency improvements

#### 6.4 Rollout Phase
1. **Gradual Migration**
   - Enable gRPC alongside HTTP
   - Migrate one component at a time
   - Monitor performance and stability

2. **Fallback Preparation**
   - Keep HTTP code commented but available
   - Create rollback procedures
   - Monitor error rates

3. **Final Migration**
   - Remove HTTP code after successful migration
   - Update documentation
   - Update CLAUDE.md with new architecture

### Phase 7: Rollback Strategy

#### 7.1 Feature Flags
```go
// Environment variables for rollback
var (
    useGRPC = os.Getenv("USE_GRPC") == "true"
    useHTTPFallback = os.Getenv("USE_HTTP_FALLBACK") == "true"
)
```

#### 7.2 Commented HTTP Code
Keep all existing HTTP code commented but intact:
```go
// FALLBACK: Original HTTP implementation
/*
func (a *App) logTradeHandler(w http.ResponseWriter, r *http.Request) {
    // Original implementation kept for rollback
}
*/
```

#### 7.3 Quick Rollback Procedure
1. Set environment variable `USE_HTTP_FALLBACK=true`
2. Restart bridge server
3. Update NinjaTrader addon to use HTTP
4. Uncomment HTTP code if needed

## Success Metrics

### Performance Improvements Expected
- **Reduced Latency**: gRPC streaming should reduce polling overhead
- **Better Error Handling**: Native gRPC status codes and retry policies
- **Type Safety**: Protocol buffer schema validation
- **Reduced Bandwidth**: Binary serialization vs JSON

### Monitoring Points
- **Connection Stability**: Monitor gRPC connection health
- **Message Throughput**: Compare before/after message rates
- **Error Rates**: Track gRPC vs HTTP error rates
- **Resource Usage**: Monitor CPU and memory usage

## Conclusion

This migration plan provides a comprehensive approach to modernizing the communication architecture while maintaining system stability and rollback capabilities. The phased approach ensures minimal disruption while achieving the benefits of gRPC's performance, type safety, and streaming capabilities.

The hybrid approach for MT5 (keeping HTTP) balances modernization with practical constraints, while the logging migration ensures all output stays local to NinjaScript for better debugging and monitoring.