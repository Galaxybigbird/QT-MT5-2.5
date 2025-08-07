#define MT5GRPCCLIENT_EXPORTS
#include "mt5_grpc_client.h"
#include "grpc_client_impl.h"
#include <memory>
#include <cstring>

// Global instance of the gRPC client
static std::unique_ptr<GrpcClientImpl> g_grpc_client = nullptr;

// Thread-safe access to the global client
static GrpcClientImpl* GetClient() {
    static std::mutex client_mutex;
    std::lock_guard<std::mutex> lock(client_mutex);
    return g_grpc_client.get();
}

// Helper function to safely copy string to buffer
static int CopyStringToBuffer(const std::string& str, char* buffer, int buffer_size) {
    if (!buffer || buffer_size <= 0) {
        return GRPC_ERROR_INVALID_PARAMS;
    }
    
    size_t copy_size = std::min(static_cast<size_t>(buffer_size - 1), str.size());
    std::memcpy(buffer, str.c_str(), copy_size);
    buffer[copy_size] = '\0';
    
    return GRPC_SUCCESS;
}

// Connection and initialization functions
extern "C" {

MT5GRPC_API int MT5GRPC_CALL GrpcInitialize(const char* server_address, int port) {
    if (!server_address || port <= 0) {
        return GRPC_ERROR_INVALID_PARAMS;
    }
    
    try {
        static std::mutex init_mutex;
        std::lock_guard<std::mutex> lock(init_mutex);
        
        // Shutdown existing client if any
        if (g_grpc_client) {
            g_grpc_client->Shutdown();
        }
        
        // Create new client
        g_grpc_client = std::make_unique<GrpcClientImpl>();
        
        // Initialize with server address
        bool success = g_grpc_client->Initialize(std::string(server_address), port);
        
        if (!success) {
            g_grpc_client.reset();
            return GRPC_ERROR_CONNECTION;
        }
        
        return GRPC_SUCCESS;
    }
    catch (...) {
        g_grpc_client.reset();
        return GRPC_ERROR_CONNECTION;
    }
}

MT5GRPC_API int MT5GRPC_CALL GrpcShutdown() {
    try {
        static std::mutex shutdown_mutex;
        std::lock_guard<std::mutex> lock(shutdown_mutex);
        
        if (g_grpc_client) {
            g_grpc_client->Shutdown();
            g_grpc_client.reset();
        }
        
        return GRPC_SUCCESS;
    }
    catch (...) {
        return GRPC_ERROR_CONNECTION;
    }
}

MT5GRPC_API int MT5GRPC_CALL GrpcIsConnected() {
    GrpcClientImpl* client = GetClient();
    if (!client) {
        return 0;
    }
    
    return client->IsConnected() ? 1 : 0;
}

MT5GRPC_API int MT5GRPC_CALL GrpcReconnect() {
    GrpcClientImpl* client = GetClient();
    if (!client) {
        return GRPC_ERROR_NOT_INITIALIZED;
    }
    
    return client->Reconnect() ? GRPC_SUCCESS : GRPC_ERROR_CONNECTION;
}

// Trade streaming functions
MT5GRPC_API int MT5GRPC_CALL GrpcStartTradeStream() {
    GrpcClientImpl* client = GetClient();
    if (!client) {
        return GRPC_ERROR_NOT_INITIALIZED;
    }
    
    return client->StartTradeStream() ? GRPC_SUCCESS : GRPC_ERROR_STREAMING_FAILED;
}

MT5GRPC_API int MT5GRPC_CALL GrpcStopTradeStream() {
    GrpcClientImpl* client = GetClient();
    if (!client) {
        return GRPC_ERROR_NOT_INITIALIZED;
    }
    
    client->StopTradeStream();
    return GRPC_SUCCESS;
}

MT5GRPC_API int MT5GRPC_CALL GrpcGetNextTrade(char* trade_json, int buffer_size) {
    GrpcClientImpl* client = GetClient();
    if (!client) {
        return GRPC_ERROR_NOT_INITIALIZED;
    }
    
    if (!trade_json || buffer_size <= 0) {
        return GRPC_ERROR_INVALID_PARAMS;
    }
    
    std::string trade_str;
    if (!client->GetNextTrade(trade_str)) {
        // No trade available - return empty string
        trade_json[0] = '\0';
        return GRPC_SUCCESS;
    }
    
    return CopyStringToBuffer(trade_str, trade_json, buffer_size);
}

MT5GRPC_API int MT5GRPC_CALL GrpcGetTradeQueueSize() {
    GrpcClientImpl* client = GetClient();
    if (!client) {
        return -1;
    }
    
    return client->GetTradeQueueSize();
}

// Trade result submission
MT5GRPC_API int MT5GRPC_CALL GrpcSubmitTradeResult(const char* result_json) {
    GrpcClientImpl* client = GetClient();
    if (!client) {
        return GRPC_ERROR_NOT_INITIALIZED;
    }
    
    if (!result_json) {
        return GRPC_ERROR_INVALID_PARAMS;
    }
    
    return client->SubmitTradeResult(std::string(result_json)) ? GRPC_SUCCESS : GRPC_ERROR_CONNECTION;
}

// Health check functions
MT5GRPC_API int MT5GRPC_CALL GrpcHealthCheck(const char* request_json, char* response_json, int buffer_size) {
    GrpcClientImpl* client = GetClient();
    if (!client) {
        return GRPC_ERROR_NOT_INITIALIZED;
    }
    
    if (!request_json || !response_json || buffer_size <= 0) {
        return GRPC_ERROR_INVALID_PARAMS;
    }
    
    std::string response_str;
    if (!client->HealthCheck(std::string(request_json), response_str)) {
        return GRPC_ERROR_CONNECTION;
    }
    
    return CopyStringToBuffer(response_str, response_json, buffer_size);
}

// Hedge closure notifications
MT5GRPC_API int MT5GRPC_CALL GrpcNotifyHedgeClose(const char* notification_json) {
    GrpcClientImpl* client = GetClient();
    if (!client) {
        return GRPC_ERROR_NOT_INITIALIZED;
    }
    
    if (!notification_json) {
        return GRPC_ERROR_INVALID_PARAMS;
    }
    
    return client->NotifyHedgeClose(std::string(notification_json)) ? GRPC_SUCCESS : GRPC_ERROR_CONNECTION;
}

// Elastic hedge updates
MT5GRPC_API int MT5GRPC_CALL GrpcSubmitElasticUpdate(const char* update_json) {
    GrpcClientImpl* client = GetClient();
    if (!client) {
        return GRPC_ERROR_NOT_INITIALIZED;
    }
    
    if (!update_json) {
        return GRPC_ERROR_INVALID_PARAMS;
    }
    
    return client->SubmitElasticUpdate(std::string(update_json)) ? GRPC_SUCCESS : GRPC_ERROR_CONNECTION;
}

// Trailing stop updates
MT5GRPC_API int MT5GRPC_CALL GrpcSubmitTrailingUpdate(const char* update_json) {
    GrpcClientImpl* client = GetClient();
    if (!client) {
        return GRPC_ERROR_NOT_INITIALIZED;
    }
    
    if (!update_json) {
        return GRPC_ERROR_INVALID_PARAMS;
    }
    
    return client->SubmitTrailingUpdate(std::string(update_json)) ? GRPC_SUCCESS : GRPC_ERROR_CONNECTION;
}

// System heartbeat
MT5GRPC_API int MT5GRPC_CALL GrpcSystemHeartbeat(const char* heartbeat_json, char* response_json, int buffer_size) {
    GrpcClientImpl* client = GetClient();
    if (!client) {
        return GRPC_ERROR_NOT_INITIALIZED;
    }
    
    if (!heartbeat_json || !response_json || buffer_size <= 0) {
        return GRPC_ERROR_INVALID_PARAMS;
    }
    
    std::string response_str;
    if (!client->SystemHeartbeat(std::string(heartbeat_json), response_str)) {
        return GRPC_ERROR_CONNECTION;
    }
    
    return CopyStringToBuffer(response_str, response_json, buffer_size);
}

// Error handling and logging
MT5GRPC_API int MT5GRPC_CALL GrpcGetLastError() {
    GrpcClientImpl* client = GetClient();
    if (!client) {
        return GRPC_ERROR_NOT_INITIALIZED;
    }
    
    return client->GetLastError();
}

MT5GRPC_API int MT5GRPC_CALL GrpcGetLastErrorMessage(char* error_message, int buffer_size) {
    GrpcClientImpl* client = GetClient();
    if (!client) {
        return GRPC_ERROR_NOT_INITIALIZED;
    }
    
    if (!error_message || buffer_size <= 0) {
        return GRPC_ERROR_INVALID_PARAMS;
    }
    
    std::string error_str = client->GetLastErrorMessage();
    return CopyStringToBuffer(error_str, error_message, buffer_size);
}

// Connection status and statistics
MT5GRPC_API int MT5GRPC_CALL GrpcGetConnectionStatus(char* status_json, int buffer_size) {
    GrpcClientImpl* client = GetClient();
    if (!client) {
        return GRPC_ERROR_NOT_INITIALIZED;
    }
    
    if (!status_json || buffer_size <= 0) {
        return GRPC_ERROR_INVALID_PARAMS;
    }
    
    std::string status_str = client->GetConnectionStatus();
    return CopyStringToBuffer(status_str, status_json, buffer_size);
}

MT5GRPC_API int MT5GRPC_CALL GrpcGetStreamingStats(char* stats_json, int buffer_size) {
    GrpcClientImpl* client = GetClient();
    if (!client) {
        return GRPC_ERROR_NOT_INITIALIZED;
    }
    
    if (!stats_json || buffer_size <= 0) {
        return GRPC_ERROR_INVALID_PARAMS;
    }
    
    std::string stats_str = client->GetStreamingStats();
    return CopyStringToBuffer(stats_str, stats_json, buffer_size);
}

// Configuration functions
MT5GRPC_API int MT5GRPC_CALL GrpcSetConnectionTimeout(int timeout_ms) {
    GrpcClientImpl* client = GetClient();
    if (!client) {
        return GRPC_ERROR_NOT_INITIALIZED;
    }
    
    if (timeout_ms <= 0) {
        return GRPC_ERROR_INVALID_PARAMS;
    }
    
    client->SetConnectionTimeout(timeout_ms);
    return GRPC_SUCCESS;
}

MT5GRPC_API int MT5GRPC_CALL GrpcSetStreamingTimeout(int timeout_ms) {
    GrpcClientImpl* client = GetClient();
    if (!client) {
        return GRPC_ERROR_NOT_INITIALIZED;
    }
    
    if (timeout_ms <= 0) {
        return GRPC_ERROR_INVALID_PARAMS;
    }
    
    client->SetStreamingTimeout(timeout_ms);
    return GRPC_SUCCESS;
}

MT5GRPC_API int MT5GRPC_CALL GrpcSetMaxRetries(int max_retries) {
    GrpcClientImpl* client = GetClient();
    if (!client) {
        return GRPC_ERROR_NOT_INITIALIZED;
    }
    
    if (max_retries < 0) {
        return GRPC_ERROR_INVALID_PARAMS;
    }
    
    client->SetMaxRetries(max_retries);
    return GRPC_SUCCESS;
}

} // extern "C"