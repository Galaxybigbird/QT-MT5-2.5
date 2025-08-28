#pragma once

#include <string>

// Export macros for DLL
#ifdef MT5_GRPC_EXPORTS
#define MT5_GRPC_API __declspec(dllexport)
#else
#define MT5_GRPC_API __declspec(dllimport)
#endif

// Error codes matching C# implementation
const int ERROR_SUCCESS = 0;
const int ERROR_INIT_FAILED = -1;
const int ERROR_NOT_INITIALIZED = -2;
const int ERROR_CONNECTION_FAILED = -3;
const int ERROR_STREAM_FAILED = -4;
const int ERROR_INVALID_PARAMS = -5;
const int ERROR_TIMEOUT = -6;
const int ERROR_SERIALIZATION = -7;
const int ERROR_CLEANUP_FAILED = -8;

extern "C" {
    // Core initialization and connection
    MT5_GRPC_API int __stdcall TestFunction();
    MT5_GRPC_API int __stdcall GrpcInitialize(const wchar_t* server_address, int port);
    MT5_GRPC_API int __stdcall GrpcShutdown();
    MT5_GRPC_API int __stdcall GrpcIsConnected();
    MT5_GRPC_API int __stdcall GrpcReconnect();
    
    // Trade streaming functions
    MT5_GRPC_API int __stdcall GrpcStartTradeStream();
    MT5_GRPC_API int __stdcall GrpcStopTradeStream();
    MT5_GRPC_API int __stdcall GrpcGetNextTrade(wchar_t* trade_json, int buffer_size);
    MT5_GRPC_API int __stdcall GrpcGetTradeQueueSize();
    
    // Trade result submission
    MT5_GRPC_API int __stdcall GrpcSubmitTradeResult(const wchar_t* result_json);
    
    // Health check and status
    MT5_GRPC_API int __stdcall GrpcHealthCheck(const wchar_t* request_json, wchar_t* response_json, int buffer_size);
    
    // Notification functions
    MT5_GRPC_API int __stdcall GrpcNotifyHedgeClose(const wchar_t* notification_json);
    MT5_GRPC_API int __stdcall GrpcSubmitElasticUpdate(const wchar_t* update_json);
    MT5_GRPC_API int __stdcall GrpcSubmitTrailingUpdate(const wchar_t* update_json);
    
    // Status and diagnostics
    MT5_GRPC_API int __stdcall GrpcGetConnectionStatus(wchar_t* status_json, int buffer_size);
    MT5_GRPC_API int __stdcall GrpcGetStreamingStats(wchar_t* stats_json, int buffer_size);
    MT5_GRPC_API int __stdcall GrpcGetLastError(wchar_t* error_message, int buffer_size);
    
    // Configuration functions
    MT5_GRPC_API int __stdcall GrpcSetConnectionTimeout(int timeout_ms);
    MT5_GRPC_API int __stdcall GrpcSetStreamingTimeout(int timeout_ms);
    MT5_GRPC_API int __stdcall GrpcSetMaxRetries(int max_retries);
    MT5_GRPC_API int __stdcall GrpcLog(const wchar_t* log_json);
}