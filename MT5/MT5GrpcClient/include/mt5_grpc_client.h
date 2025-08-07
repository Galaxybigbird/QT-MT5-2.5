#pragma once

#ifdef _WIN32
    #ifdef MT5GRPCCLIENT_EXPORTS
        #define MT5GRPC_API __declspec(dllexport)
    #else
        #define MT5GRPC_API __declspec(dllimport)
    #endif
    #define MT5GRPC_CALL __stdcall
#else
    #define MT5GRPC_API
    #define MT5GRPC_CALL
#endif

// Error codes for gRPC operations
#define GRPC_SUCCESS 0
#define GRPC_ERROR_CONNECTION -1
#define GRPC_ERROR_INVALID_PARAMS -2
#define GRPC_ERROR_TIMEOUT -3
#define GRPC_ERROR_NOT_INITIALIZED -4
#define GRPC_ERROR_STREAMING_FAILED -5
#define GRPC_ERROR_JSON_PARSE -6
#define GRPC_ERROR_PROTOBUF_CONVERT -7

#ifdef __cplusplus
extern "C" {
#endif

// Connection and initialization functions
MT5GRPC_API int MT5GRPC_CALL GrpcInitialize(const char* server_address, int port);
MT5GRPC_API int MT5GRPC_CALL GrpcShutdown();
MT5GRPC_API int MT5GRPC_CALL GrpcIsConnected();
MT5GRPC_API int MT5GRPC_CALL GrpcReconnect();

// Trade streaming functions
MT5GRPC_API int MT5GRPC_CALL GrpcStartTradeStream();
MT5GRPC_API int MT5GRPC_CALL GrpcStopTradeStream();
MT5GRPC_API int MT5GRPC_CALL GrpcGetNextTrade(char* trade_json, int buffer_size);
MT5GRPC_API int MT5GRPC_CALL GrpcGetTradeQueueSize();

// Trade result submission
MT5GRPC_API int MT5GRPC_CALL GrpcSubmitTradeResult(const char* result_json);

// Health check functions
MT5GRPC_API int MT5GRPC_CALL GrpcHealthCheck(const char* request_json, char* response_json, int buffer_size);

// Hedge closure notifications
MT5GRPC_API int MT5GRPC_CALL GrpcNotifyHedgeClose(const char* notification_json);

// Elastic hedge updates
MT5GRPC_API int MT5GRPC_CALL GrpcSubmitElasticUpdate(const char* update_json);

// Trailing stop updates
MT5GRPC_API int MT5GRPC_CALL GrpcSubmitTrailingUpdate(const char* update_json);

// System heartbeat
MT5GRPC_API int MT5GRPC_CALL GrpcSystemHeartbeat(const char* heartbeat_json, char* response_json, int buffer_size);

// Error handling and logging
MT5GRPC_API int MT5GRPC_CALL GrpcGetLastError();
MT5GRPC_API int MT5GRPC_CALL GrpcGetLastErrorMessage(char* error_message, int buffer_size);

// Connection status and statistics
MT5GRPC_API int MT5GRPC_CALL GrpcGetConnectionStatus(char* status_json, int buffer_size);
MT5GRPC_API int MT5GRPC_CALL GrpcGetStreamingStats(char* stats_json, int buffer_size);

// Configuration functions
MT5GRPC_API int MT5GRPC_CALL GrpcSetConnectionTimeout(int timeout_ms);
MT5GRPC_API int MT5GRPC_CALL GrpcSetStreamingTimeout(int timeout_ms);
MT5GRPC_API int MT5GRPC_CALL GrpcSetMaxRetries(int max_retries);

#ifdef __cplusplus
}
#endif