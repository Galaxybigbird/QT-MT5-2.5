# Bridge Server Configuration

## Environment Variables

The Bridge Server supports the following environment variables for configuration:

### Protocol Configuration

- **BRIDGE_USE_GRPC** (default: `true`)
  - Values: `true`, `false`, `1`, `0`, `yes`, `no`, `on`, `off`
  - Controls whether gRPC server is enabled by default

- **BRIDGE_GRPC_PORT** (default: `"50051"`)
  - The port number for the gRPC server

- **BRIDGE_HTTP_FALLBACK** (default: `true`)
  - Values: `true`, `false`, `1`, `0`, `yes`, `no`, `on`, `off`
  - Controls whether HTTP fallback server is enabled

## Configuration Examples

### gRPC Only Mode
```bash
export BRIDGE_USE_GRPC=true
export BRIDGE_HTTP_FALLBACK=false
./BridgeApp
```

### HTTP Only Mode (Fallback)
```bash
export BRIDGE_USE_GRPC=false
export BRIDGE_HTTP_FALLBACK=true
./BridgeApp
```

### Dual Protocol Mode (Default)
```bash
export BRIDGE_USE_GRPC=true
export BRIDGE_HTTP_FALLBACK=true
./BridgeApp
```

### Custom gRPC Port
```bash
export BRIDGE_GRPC_PORT=9090
./BridgeApp
```

## HTTP Fallback Features

### Automatic Fallback
- If gRPC server fails to start, the system automatically falls back to HTTP
- All HTTP endpoints remain functional and available

### Runtime Protocol Switching
The application supports runtime switching between protocols via API methods:

- `SwitchToGRPC(disableHTTP bool)` - Switch to gRPC protocol
- `SwitchToHTTP(disableGRPC bool)` - Switch to HTTP protocol  
- `EnableDualProtocol()` - Enable both gRPC and HTTP
- `GetProtocolStatus()` - Get current protocol status

### Preserved HTTP Endpoints
All original HTTP endpoints are preserved for fallback:

- `POST /log_trade` - Trade submission from NinjaTrader
- `GET /mt5/get_trade` - Trade polling for MT5
- `GET /health` - Health check endpoint
- `POST /notify_hedge_close` - Hedge closure notifications from MT5
- `POST /nt_close_hedge` - Hedge closure requests from NT
- `POST /mt5/trade_result` - Trade execution results from MT5
- `POST /elastic_update` - Elastic hedging updates
- `POST /trailing_stop_update` - Trailing stop updates

## Server Ports

- **gRPC Server**: Port 50051 (configurable via BRIDGE_GRPC_PORT)
- **HTTP Server**: Port 5000 (127.0.0.1:5000)

## Dual Protocol Architecture

```
┌─────────────────┐    gRPC (Port 50051)     ┌─────────────────┐
│  NinjaTrader    │ ────────────────────────→ │                 │
│     Addon       │                           │  Bridge Server  │
│                 │ ←──── HTTP (Port 5000) ── │   (Go + gRPC)   │
└─────────────────┘    (Fallback)             └─────────────────┘
                                                       │
┌─────────────────┐    gRPC Streaming         ┌─────────────────┐
│      MT5 EA     │ ←────────────────────────→ │  gRPC Services  │
│                 │                           │                 │
└─────────────────┘    HTTP Polling           └─────────────────┘
                       (Fallback)
```

## Benefits of HTTP Fallback

1. **Reliability**: If gRPC fails, system continues with HTTP
2. **Backward Compatibility**: Existing HTTP clients continue to work
3. **Gradual Migration**: Can migrate clients one at a time
4. **Development Flexibility**: Easier debugging with familiar HTTP tools
5. **Emergency Recovery**: Can switch back to HTTP in production if needed

## Monitoring Protocol Status

Check current protocol status via the `GetProtocolStatus()` method which returns:

```json
{
  "grpc_enabled": true,
  "grpc_port": "50051", 
  "http_fallback": true,
  "bridge_active": true,
  "protocols_active": ["gRPC", "HTTP"]
}
```