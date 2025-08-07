#include "grpc_client_impl.h"
#include "json_converter.h"
#include <grpcpp/create_channel.h>
#include <grpcpp/security/credentials.h>
#include <functional>
#include <thread>

GrpcClientImpl::GrpcClientImpl()
    : connected_(false)
    , port_(0)
    , connection_timeout_ms_(5000)
    , streaming_timeout_ms_(30000)
    , max_retries_(3)
    , last_error_(0)
    , streaming_active_(false)
    , should_stop_streaming_(false)
    , total_trades_received_(0)
    , total_requests_sent_(0)
    , connection_attempts_(0)
    , streaming_restarts_(0) {
}

GrpcClientImpl::~GrpcClientImpl() {
    Shutdown();
}

bool GrpcClientImpl::Initialize(const std::string& server_address, int port) {
    std::lock_guard<std::mutex> lock(error_mutex_);
    
    server_address_ = server_address;
    port_ = port;
    connection_attempts_++;
    
    if (!CreateChannel()) {
        SetError(-1, "Failed to create gRPC channel");
        return false;
    }
    
    // Test the connection with a simple health check
    if (!WaitForChannelReady(connection_timeout_ms_)) {
        SetError(-2, "Failed to establish connection within timeout");
        return false;
    }
    
    connected_ = true;
    connection_start_time_ = std::chrono::steady_clock::now();
    SetError(0, "");
    
    return true;
}

bool GrpcClientImpl::CreateChannel() {
    try {
        std::string target = server_address_ + ":" + std::to_string(port_);
        
        // Create insecure channel for local communication
        grpc::ChannelArguments args;
        args.SetMaxReceiveMessageSize(64 * 1024 * 1024); // 64MB
        args.SetMaxSendMessageSize(64 * 1024 * 1024);    // 64MB
        args.SetKeepAliveTime(30000);                      // 30 seconds
        args.SetKeepAliveTimeout(5000);                    // 5 seconds
        args.SetKeepAlivePermitWithoutCalls(true);
        args.SetInt(GRPC_ARG_KEEPALIVE_PERMIT_WITHOUT_CALLS, 1);
        
        channel_ = grpc::CreateCustomChannel(
            target, 
            grpc::InsecureChannelCredentials(),
            args
        );
        
        if (!channel_) {
            return false;
        }
        
        // Create service stubs
        trading_stub_ = trading::TradingService::NewStub(channel_);
        streaming_stub_ = trading::StreamingService::NewStub(channel_);
        
        return trading_stub_ && streaming_stub_;
    }
    catch (...) {
        return false;
    }
}

bool GrpcClientImpl::WaitForChannelReady(int timeout_ms) {
    if (!channel_) {
        return false;
    }
    
    auto deadline = std::chrono::system_clock::now() + std::chrono::milliseconds(timeout_ms);
    return channel_->WaitForConnected(deadline);
}

grpc_connectivity_state GrpcClientImpl::GetChannelState() const {
    if (!channel_) {
        return GRPC_CHANNEL_SHUTDOWN;
    }
    return channel_->GetState(false);
}

void GrpcClientImpl::Shutdown() {
    StopTradeStream();
    
    std::lock_guard<std::mutex> lock(error_mutex_);
    connected_ = false;
    
    if (channel_) {
        // Gracefully shutdown the channel
        channel_.reset();
    }
    
    trading_stub_.reset();
    streaming_stub_.reset();
}

bool GrpcClientImpl::IsConnected() const {
    if (!connected_ || !channel_) {
        return false;
    }
    
    grpc_connectivity_state state = GetChannelState();
    return state == GRPC_CHANNEL_READY || state == GRPC_CHANNEL_IDLE;
}

bool GrpcClientImpl::Reconnect() {
    Shutdown();
    std::this_thread::sleep_for(std::chrono::milliseconds(1000)); // Wait 1 second before reconnect
    return Initialize(server_address_, port_);
}

bool GrpcClientImpl::StartTradeStream() {
    if (streaming_active_) {
        return true; // Already streaming
    }
    
    if (!IsConnected()) {
        SetError(-3, "Not connected to server");
        return false;
    }
    
    should_stop_streaming_ = false;
    streaming_active_ = true;
    
    // Start streaming thread
    streaming_thread_ = std::thread(&GrpcClientImpl::StreamingWorker, this);
    
    return true;
}

void GrpcClientImpl::StopTradeStream() {
    if (!streaming_active_) {
        return;
    }
    
    should_stop_streaming_ = true;
    
    // Wait for streaming thread to finish
    if (streaming_thread_.joinable()) {
        streaming_thread_.join();
    }
    
    streaming_active_ = false;
    
    // Clear any pending trades
    std::lock_guard<std::mutex> lock(trade_queue_mutex_);
    while (!trade_queue_.empty()) {
        trade_queue_.pop();
    }
}

void GrpcClientImpl::StreamingWorker() {
    try {
        while (!should_stop_streaming_) {
            if (!IsConnected()) {
                SetError(-4, "Connection lost during streaming");
                std::this_thread::sleep_for(std::chrono::milliseconds(1000));
                
                // Attempt reconnection
                if (Reconnect()) {
                    streaming_restarts_++;
                    continue;
                } else {
                    break;
                }
            }
            
            // Create streaming context with timeout
            grpc::ClientContext context;
            auto deadline = std::chrono::system_clock::now() + std::chrono::milliseconds(streaming_timeout_ms_);
            context.set_deadline(deadline);
            
            // Start the GetTrades stream
            trade_stream_ = trading_stub_->GetTrades(&context);
            
            if (!trade_stream_) {
                SetError(-5, "Failed to create trade stream");
                std::this_thread::sleep_for(std::chrono::milliseconds(1000));
                continue;
            }
            
            // Send initial health request to start receiving trades
            trading::HealthRequest health_req;
            health_req.set_source("hedgebot");
            health_req.set_open_positions(0);
            
            if (!trade_stream_->Write(health_req)) {
                SetError(-6, "Failed to write health request to stream");
                trade_stream_->WritesDone();
                grpc::Status status = trade_stream_->Finish();
                std::this_thread::sleep_for(std::chrono::milliseconds(1000));
                continue;
            }
            
            // Read trades from the stream
            trading::Trade trade;
            while (!should_stop_streaming_ && trade_stream_->Read(&trade)) {
                // Convert trade to JSON and add to queue
                std::string trade_json = JsonConverter::TradeToJson(trade);
                
                {
                    std::lock_guard<std::mutex> lock(trade_queue_mutex_);
                    trade_queue_.push(trade_json);
                    total_trades_received_++;
                }
                
                // Notify waiting threads
                trade_queue_condition_.notify_one();
            }
            
            // Clean up the stream
            trade_stream_->WritesDone();
            grpc::Status status = trade_stream_->Finish();
            
            if (!status.ok() && !should_stop_streaming_) {
                SetError(-7, "Stream finished with error: " + status.error_message());
                std::this_thread::sleep_for(std::chrono::milliseconds(1000));
            }
            
            trade_stream_.reset();
        }
    }
    catch (const std::exception& e) {
        SetError(-8, "Exception in streaming worker: " + std::string(e.what()));
    }
    catch (...) {
        SetError(-9, "Unknown exception in streaming worker");
    }
    
    streaming_active_ = false;
}

bool GrpcClientImpl::GetNextTrade(std::string& trade_json) {
    std::unique_lock<std::mutex> lock(trade_queue_mutex_);
    
    if (trade_queue_.empty()) {
        return false; // No trades available
    }
    
    trade_json = trade_queue_.front();
    trade_queue_.pop();
    return true;
}

int GrpcClientImpl::GetTradeQueueSize() const {
    std::lock_guard<std::mutex> lock(trade_queue_mutex_);
    return static_cast<int>(trade_queue_.size());
}

bool GrpcClientImpl::SubmitTradeResult(const std::string& result_json) {
    if (!IsConnected()) {
        SetError(-10, "Not connected to server");
        return false;
    }
    
    trading::MT5TradeResult result;
    if (!JsonConverter::JsonToMT5TradeResult(result_json, result)) {
        SetError(-11, "Failed to parse trade result JSON");
        return false;
    }
    
    auto operation = [&]() -> bool {
        try {
            grpc::ClientContext context;
            auto deadline = std::chrono::system_clock::now() + std::chrono::milliseconds(connection_timeout_ms_);
            context.set_deadline(deadline);
            
            trading::GenericResponse response;
            grpc::Status status = trading_stub_->SubmitTradeResult(&context, result, &response);
            
            total_requests_sent_++;
            
            if (!status.ok()) {
                SetError(-12, "gRPC call failed: " + status.error_message());
                return false;
            }
            
            if (response.status() != "success") {
                SetError(-13, "Server rejected trade result: " + response.message());
                return false;
            }
            
            return true;
        }
        catch (...) {
            SetError(-14, "Exception during trade result submission");
            return false;
        }
    };
    
    return RetryOperation(operation);
}

bool GrpcClientImpl::HealthCheck(const std::string& request_json, std::string& response_json) {
    if (!IsConnected()) {
        SetError(-15, "Not connected to server");
        return false;
    }
    
    trading::HealthRequest request;
    if (!JsonConverter::JsonToHealthRequest(request_json, request)) {
        SetError(-16, "Failed to parse health request JSON");
        return false;
    }
    
    auto operation = [&]() -> bool {
        try {
            grpc::ClientContext context;
            auto deadline = std::chrono::system_clock::now() + std::chrono::milliseconds(connection_timeout_ms_);
            context.set_deadline(deadline);
            
            trading::HealthResponse response;
            grpc::Status status = trading_stub_->HealthCheck(&context, request, &response);
            
            total_requests_sent_++;
            
            if (!status.ok()) {
                SetError(-17, "Health check gRPC call failed: " + status.error_message());
                return false;
            }
            
            response_json = JsonConverter::HealthResponseToJson(response);
            return true;
        }
        catch (...) {
            SetError(-18, "Exception during health check");
            return false;
        }
    };
    
    return RetryOperation(operation);
}

bool GrpcClientImpl::NotifyHedgeClose(const std::string& notification_json) {
    if (!IsConnected()) {
        SetError(-19, "Not connected to server");
        return false;
    }
    
    trading::HedgeCloseNotification notification;
    if (!JsonConverter::JsonToHedgeCloseNotification(notification_json, notification)) {
        SetError(-20, "Failed to parse hedge close notification JSON");
        return false;
    }
    
    auto operation = [&]() -> bool {
        try {
            grpc::ClientContext context;
            auto deadline = std::chrono::system_clock::now() + std::chrono::milliseconds(connection_timeout_ms_);
            context.set_deadline(deadline);
            
            trading::GenericResponse response;
            grpc::Status status = trading_stub_->NotifyHedgeClose(&context, notification, &response);
            
            total_requests_sent_++;
            
            if (!status.ok()) {
                SetError(-21, "Hedge close notification gRPC call failed: " + status.error_message());
                return false;
            }
            
            return true;
        }
        catch (...) {
            SetError(-22, "Exception during hedge close notification");
            return false;
        }
    };
    
    return RetryOperation(operation);
}

bool GrpcClientImpl::SubmitElasticUpdate(const std::string& update_json) {
    if (!IsConnected()) {
        SetError(-23, "Not connected to server");
        return false;
    }
    
    trading::ElasticHedgeUpdate update;
    if (!JsonConverter::JsonToElasticHedgeUpdate(update_json, update)) {
        SetError(-24, "Failed to parse elastic update JSON");
        return false;
    }
    
    auto operation = [&]() -> bool {
        try {
            grpc::ClientContext context;
            auto deadline = std::chrono::system_clock::now() + std::chrono::milliseconds(connection_timeout_ms_);
            context.set_deadline(deadline);
            
            trading::GenericResponse response;
            grpc::Status status = trading_stub_->SubmitElasticUpdate(&context, update, &response);
            
            total_requests_sent_++;
            
            if (!status.ok()) {
                SetError(-25, "Elastic update gRPC call failed: " + status.error_message());
                return false;
            }
            
            return true;
        }
        catch (...) {
            SetError(-26, "Exception during elastic update submission");
            return false;
        }
    };
    
    return RetryOperation(operation);
}

bool GrpcClientImpl::SubmitTrailingUpdate(const std::string& update_json) {
    if (!IsConnected()) {
        SetError(-27, "Not connected to server");
        return false;
    }
    
    trading::TrailingStopUpdate update;
    if (!JsonConverter::JsonToTrailingStopUpdate(update_json, update)) {
        SetError(-28, "Failed to parse trailing update JSON");
        return false;
    }
    
    auto operation = [&]() -> bool {
        try {
            grpc::ClientContext context;
            auto deadline = std::chrono::system_clock::now() + std::chrono::milliseconds(connection_timeout_ms_);
            context.set_deadline(deadline);
            
            trading::GenericResponse response;
            grpc::Status status = trading_stub_->SubmitTrailingUpdate(&context, update, &response);
            
            total_requests_sent_++;
            
            if (!status.ok()) {
                SetError(-29, "Trailing update gRPC call failed: " + status.error_message());
                return false;
            }
            
            return true;
        }
        catch (...) {
            SetError(-30, "Exception during trailing update submission");
            return false;
        }
    };
    
    return RetryOperation(operation);
}

bool GrpcClientImpl::SystemHeartbeat(const std::string& heartbeat_json, std::string& response_json) {
    if (!IsConnected()) {
        SetError(-31, "Not connected to server");
        return false;
    }
    
    trading::HeartbeatRequest request;
    if (!JsonConverter::JsonToHeartbeatRequest(heartbeat_json, request)) {
        SetError(-32, "Failed to parse heartbeat request JSON");
        return false;
    }
    
    auto operation = [&]() -> bool {
        try {
            grpc::ClientContext context;
            auto deadline = std::chrono::system_clock::now() + std::chrono::milliseconds(connection_timeout_ms_);
            context.set_deadline(deadline);
            
            trading::HeartbeatResponse response;
            grpc::Status status = trading_stub_->SystemHeartbeat(&context, request, &response);
            
            total_requests_sent_++;
            
            if (!status.ok()) {
                SetError(-33, "System heartbeat gRPC call failed: " + status.error_message());
                return false;
            }
            
            response_json = JsonConverter::HeartbeatResponseToJson(response);
            return true;
        }
        catch (...) {
            SetError(-34, "Exception during system heartbeat");
            return false;
        }
    };
    
    return RetryOperation(operation);
}

void GrpcClientImpl::SetConnectionTimeout(int timeout_ms) {
    connection_timeout_ms_ = timeout_ms;
}

void GrpcClientImpl::SetStreamingTimeout(int timeout_ms) {
    streaming_timeout_ms_ = timeout_ms;
}

void GrpcClientImpl::SetMaxRetries(int max_retries) {
    max_retries_ = max_retries;
}

int GrpcClientImpl::GetLastError() const {
    std::lock_guard<std::mutex> lock(error_mutex_);
    return last_error_;
}

std::string GrpcClientImpl::GetLastErrorMessage() const {
    std::lock_guard<std::mutex> lock(error_mutex_);
    return last_error_message_;
}

std::string GrpcClientImpl::GetConnectionStatus() const {
    std::lock_guard<std::mutex> lock(error_mutex_);
    
    auto now = std::chrono::steady_clock::now();
    auto duration = std::chrono::duration_cast<std::chrono::milliseconds>(now - connection_start_time_);
    
    return JsonConverter::GetConnectionStatusJson(
        connected_,
        server_address_,
        port_,
        std::to_string(duration.count()) + "ms",
        last_error_,
        last_error_message_
    );
}

std::string GrpcClientImpl::GetStreamingStats() const {
    std::lock_guard<std::mutex> lock(stats_mutex_);
    
    return JsonConverter::GetStreamingStatsJson(
        streaming_active_,
        total_trades_received_,
        total_requests_sent_,
        GetTradeQueueSize(),
        connection_attempts_,
        streaming_restarts_
    );
}

void GrpcClientImpl::SetError(int error_code, const std::string& message) {
    // Note: error_mutex_ should already be locked by caller
    last_error_ = error_code;
    last_error_message_ = message;
}

bool GrpcClientImpl::RetryOperation(std::function<bool()> operation, int max_attempts) {
    int attempts = (max_attempts == -1) ? max_retries_ : max_attempts;
    
    for (int i = 0; i <= attempts; ++i) {
        if (operation()) {
            return true;
        }
        
        // Don't wait after the last attempt
        if (i < attempts) {
            std::this_thread::sleep_for(std::chrono::milliseconds(500 * (i + 1))); // Exponential backoff
        }
    }
    
    return false;
}

std::string GrpcClientImpl::GetCurrentTimestamp() const {
    auto now = std::chrono::system_clock::now();
    auto time_t = std::chrono::system_clock::to_time_t(now);
    auto ms = std::chrono::duration_cast<std::chrono::milliseconds>(now.time_since_epoch()) % 1000;
    
    std::ostringstream oss;
    oss << std::put_time(std::gmtime(&time_t), "%Y-%m-%dT%H:%M:%S");
    oss << '.' << std::setfill('0') << std::setw(3) << ms.count() << "Z";
    return oss.str();
}