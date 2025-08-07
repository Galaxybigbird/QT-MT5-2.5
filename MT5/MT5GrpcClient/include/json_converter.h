#pragma once

#include <string>
#include "trading.pb.h"

class JsonConverter {
public:
    // Convert JSON to protobuf messages
    static bool JsonToTrade(const std::string& json, trading::Trade& trade);
    static bool JsonToHealthRequest(const std::string& json, trading::HealthRequest& request);
    static bool JsonToMT5TradeResult(const std::string& json, trading::MT5TradeResult& result);
    static bool JsonToHedgeCloseNotification(const std::string& json, trading::HedgeCloseNotification& notification);
    static bool JsonToElasticHedgeUpdate(const std::string& json, trading::ElasticHedgeUpdate& update);
    static bool JsonToTrailingStopUpdate(const std::string& json, trading::TrailingStopUpdate& update);
    static bool JsonToHeartbeatRequest(const std::string& json, trading::HeartbeatRequest& request);

    // Convert protobuf messages to JSON
    static std::string TradeToJson(const trading::Trade& trade);
    static std::string HealthResponseToJson(const trading::HealthResponse& response);
    static std::string GenericResponseToJson(const trading::GenericResponse& response);
    static std::string HeartbeatResponseToJson(const trading::HeartbeatResponse& response);

    // Utility methods
    static std::string GetErrorJson(int error_code, const std::string& message);
    static std::string GetSuccessJson(const std::string& message = "");
    static std::string GetConnectionStatusJson(
        bool connected,
        const std::string& server_address,
        int port,
        const std::string& connection_time,
        int error_code = 0,
        const std::string& error_message = ""
    );
    static std::string GetStreamingStatsJson(
        bool streaming_active,
        int trades_received,
        int requests_sent,
        int queue_size,
        int connection_attempts,
        int streaming_restarts,
        const std::string& last_trade_time = ""
    );

private:
    // Helper methods for JSON parsing
    static bool ValidateJson(const std::string& json);
    static std::string EscapeJsonString(const std::string& input);
    static std::string GetCurrentTimestamp();
};