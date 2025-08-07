#pragma once

#include <grpcpp/grpcpp.h>
#include <memory>
#include <string>
#include <queue>
#include <mutex>
#include <condition_variable>
#include <thread>
#include <atomic>
#include <chrono>

#include "trading.grpc.pb.h"

class GrpcClientImpl {
public:
    GrpcClientImpl();
    ~GrpcClientImpl();

    // Connection management
    bool Initialize(const std::string& server_address, int port);
    void Shutdown();
    bool IsConnected() const;
    bool Reconnect();

    // Streaming operations
    bool StartTradeStream();
    void StopTradeStream();
    bool GetNextTrade(std::string& trade_json);
    int GetTradeQueueSize() const;

    // Service calls
    bool SubmitTradeResult(const std::string& result_json);
    bool HealthCheck(const std::string& request_json, std::string& response_json);
    bool NotifyHedgeClose(const std::string& notification_json);
    bool SubmitElasticUpdate(const std::string& update_json);
    bool SubmitTrailingUpdate(const std::string& update_json);
    bool SystemHeartbeat(const std::string& heartbeat_json, std::string& response_json);

    // Configuration
    void SetConnectionTimeout(int timeout_ms);
    void SetStreamingTimeout(int timeout_ms);
    void SetMaxRetries(int max_retries);

    // Error handling
    int GetLastError() const;
    std::string GetLastErrorMessage() const;

    // Status and statistics
    std::string GetConnectionStatus() const;
    std::string GetStreamingStats() const;

private:
    // gRPC components
    std::unique_ptr<grpc::Channel> channel_;
    std::unique_ptr<trading::TradingService::Stub> trading_stub_;
    std::unique_ptr<trading::StreamingService::Stub> streaming_stub_;

    // Streaming management
    std::unique_ptr<grpc::ClientReaderWriter<trading::HealthRequest, trading::Trade>> trade_stream_;
    std::thread streaming_thread_;
    std::atomic<bool> streaming_active_;
    std::atomic<bool> should_stop_streaming_;

    // Trade queue
    std::queue<std::string> trade_queue_;
    mutable std::mutex trade_queue_mutex_;
    std::condition_variable trade_queue_condition_;

    // Connection state
    std::atomic<bool> connected_;
    std::string server_address_;
    int port_;
    
    // Configuration
    int connection_timeout_ms_;
    int streaming_timeout_ms_;
    int max_retries_;

    // Error handling
    mutable std::mutex error_mutex_;
    int last_error_;
    std::string last_error_message_;

    // Statistics
    mutable std::mutex stats_mutex_;
    std::chrono::steady_clock::time_point connection_start_time_;
    std::atomic<int> total_trades_received_;
    std::atomic<int> total_requests_sent_;
    std::atomic<int> connection_attempts_;
    std::atomic<int> streaming_restarts_;

    // Private methods
    bool CreateChannel();
    void StreamingWorker();
    void SetError(int error_code, const std::string& message);
    bool RetryOperation(std::function<bool()> operation, int max_attempts = -1);
    std::string GetCurrentTimestamp() const;
    
    // Channel state monitoring
    bool WaitForChannelReady(int timeout_ms = 5000);
    grpc_connectivity_state GetChannelState() const;
};