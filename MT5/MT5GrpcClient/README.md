# MT5GrpcClient DLL

This C++ DLL provides gRPC communication capabilities for MetaTrader 5 Expert Advisors.

## Prerequisites

1. **vcpkg** - C++ package manager
   - Download from: https://github.com/Microsoft/vcpkg
   - Install and add to PATH environment variable
   - Set VCPKG_ROOT environment variable

2. **Visual Studio 2019/2022** with C++ development tools

## Building

### Option 1: Using the batch script (Recommended)
```bash
build.bat
```

### Option 2: Manual build
```bash
# Install dependencies via vcpkg
vcpkg install grpc:x64-windows protobuf:x64-windows jsoncpp:x64-windows

# Build with CMake
mkdir build && cd build
cmake .. -DCMAKE_TOOLCHAIN_FILE=%VCPKG_ROOT%/scripts/buildsystems/vcpkg.cmake -DVCPKG_TARGET_TRIPLET=x64-windows -A x64
cmake --build . --config Release
```

## Usage in MT5

Import the DLL functions in your MQL5 Expert Advisor:

```mql5
#import "MT5GrpcClient.dll"
   int GrpcInitialize(string server_address, int port);
   int GrpcStartTradeStream();
   int GrpcGetNextTrade(string &trade_json, int buffer_size);
   int GrpcSubmitTradeResult(string result_json);
   int GrpcHealthCheck(string request_json, string &response_json, int buffer_size);
   int GrpcShutdown();
#import
```

## Functions

### Connection Management
- `GrpcInitialize()` - Initialize connection to gRPC server
- `GrpcShutdown()` - Clean shutdown
- `GrpcIsConnected()` - Check connection status
- `GrpcReconnect()` - Reconnect to server

### Trade Streaming
- `GrpcStartTradeStream()` - Start real-time trade streaming
- `GrpcStopTradeStream()` - Stop streaming
- `GrpcGetNextTrade()` - Get next trade from queue
- `GrpcGetTradeQueueSize()` - Get queue size

### API Calls
- `GrpcSubmitTradeResult()` - Submit trade execution results
- `GrpcHealthCheck()` - Health check with server
- `GrpcNotifyHedgeClose()` - Notify hedge closure
- `GrpcSubmitElasticUpdate()` - Submit elastic hedge updates
- `GrpcSubmitTrailingUpdate()` - Submit trailing stop updates

### Error Handling
- `GrpcGetLastError()` - Get last error code
- `GrpcGetLastErrorMessage()` - Get last error message
- `GrpcGetConnectionStatus()` - Get connection status JSON
- `GrpcGetStreamingStats()` - Get streaming statistics JSON

## Error Codes

- `0` - Success
- `-1` - Connection error
- `-2` - Invalid parameters  
- `-3` - Timeout
- `-4` - Not initialized
- `-5` - Streaming failed
- `-6` - JSON parse error
- `-7` - Protobuf conversion error