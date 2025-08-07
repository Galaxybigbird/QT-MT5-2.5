#include "json_converter.h"
#include <json/json.h>
#include <sstream>
#include <iomanip>
#include <chrono>

bool JsonConverter::ValidateJson(const std::string& json) {
    Json::Value root;
    Json::CharReaderBuilder builder;
    std::string errors;
    
    std::istringstream stream(json);
    return Json::parseFromStream(builder, stream, &root, &errors);
}

std::string JsonConverter::EscapeJsonString(const std::string& input) {
    std::ostringstream oss;
    for (char c : input) {
        switch (c) {
            case '"': oss << "\\\""; break;
            case '\\': oss << "\\\\"; break;
            case '\b': oss << "\\b"; break;
            case '\f': oss << "\\f"; break;
            case '\n': oss << "\\n"; break;
            case '\r': oss << "\\r"; break;
            case '\t': oss << "\\t"; break;
            default:
                if (c >= 0 && c < 32) {
                    oss << "\\u" << std::hex << std::setw(4) << std::setfill('0') << static_cast<int>(c);
                } else {
                    oss << c;
                }
                break;
        }
    }
    return oss.str();
}

std::string JsonConverter::GetCurrentTimestamp() {
    auto now = std::chrono::system_clock::now();
    auto time_t = std::chrono::system_clock::to_time_t(now);
    auto ms = std::chrono::duration_cast<std::chrono::milliseconds>(now.time_since_epoch()) % 1000;
    
    std::ostringstream oss;
    oss << std::put_time(std::gmtime(&time_t), "%Y-%m-%dT%H:%M:%S");
    oss << '.' << std::setfill('0') << std::setw(3) << ms.count() << "Z";
    return oss.str();
}

// Convert JSON to protobuf messages
bool JsonConverter::JsonToTrade(const std::string& json, trading::Trade& trade) {
    if (!ValidateJson(json)) {
        return false;
    }
    
    Json::Value root;
    Json::CharReaderBuilder builder;
    std::string errors;
    std::istringstream stream(json);
    
    if (!Json::parseFromStream(builder, stream, &root, &errors)) {
        return false;
    }
    
    try {
        if (root.isMember("id")) trade.set_id(root["id"].asString());
        if (root.isMember("base_id")) trade.set_base_id(root["base_id"].asString());
        if (root.isMember("timestamp")) trade.set_timestamp(root["timestamp"].asInt64());
        if (root.isMember("action")) trade.set_action(root["action"].asString());
        if (root.isMember("quantity")) trade.set_quantity(root["quantity"].asDouble());
        if (root.isMember("price")) trade.set_price(root["price"].asDouble());
        if (root.isMember("total_quantity")) trade.set_total_quantity(root["total_quantity"].asInt());
        if (root.isMember("contract_num")) trade.set_contract_num(root["contract_num"].asInt());
        if (root.isMember("order_type")) trade.set_order_type(root["order_type"].asString());
        if (root.isMember("measurement_pips")) trade.set_measurement_pips(root["measurement_pips"].asInt());
        if (root.isMember("raw_measurement")) trade.set_raw_measurement(root["raw_measurement"].asDouble());
        if (root.isMember("instrument")) trade.set_instrument(root["instrument"].asString());
        if (root.isMember("account_name")) trade.set_account_name(root["account_name"].asString());
        if (root.isMember("nt_balance")) trade.set_nt_balance(root["nt_balance"].asDouble());
        if (root.isMember("nt_daily_pnl")) trade.set_nt_daily_pnl(root["nt_daily_pnl"].asDouble());
        if (root.isMember("nt_trade_result")) trade.set_nt_trade_result(root["nt_trade_result"].asString());
        if (root.isMember("nt_session_trades")) trade.set_nt_session_trades(root["nt_session_trades"].asInt());
        
        return true;
    }
    catch (...) {
        return false;
    }
}

bool JsonConverter::JsonToHealthRequest(const std::string& json, trading::HealthRequest& request) {
    if (!ValidateJson(json)) {
        return false;
    }
    
    Json::Value root;
    Json::CharReaderBuilder builder;
    std::string errors;
    std::istringstream stream(json);
    
    if (!Json::parseFromStream(builder, stream, &root, &errors)) {
        return false;
    }
    
    try {
        if (root.isMember("source")) request.set_source(root["source"].asString());
        if (root.isMember("open_positions")) request.set_open_positions(root["open_positions"].asInt());
        
        return true;
    }
    catch (...) {
        return false;
    }
}

bool JsonConverter::JsonToMT5TradeResult(const std::string& json, trading::MT5TradeResult& result) {
    if (!ValidateJson(json)) {
        return false;
    }
    
    Json::Value root;
    Json::CharReaderBuilder builder;
    std::string errors;
    std::istringstream stream(json);
    
    if (!Json::parseFromStream(builder, stream, &root, &errors)) {
        return false;
    }
    
    try {
        if (root.isMember("status")) result.set_status(root["status"].asString());
        if (root.isMember("ticket")) result.set_ticket(root["ticket"].asUInt64());
        if (root.isMember("volume")) result.set_volume(root["volume"].asDouble());
        if (root.isMember("is_close")) result.set_is_close(root["is_close"].asBool());
        if (root.isMember("id")) result.set_id(root["id"].asString());
        
        return true;
    }
    catch (...) {
        return false;
    }
}

bool JsonConverter::JsonToHedgeCloseNotification(const std::string& json, trading::HedgeCloseNotification& notification) {
    if (!ValidateJson(json)) {
        return false;
    }
    
    Json::Value root;
    Json::CharReaderBuilder builder;
    std::string errors;
    std::istringstream stream(json);
    
    if (!Json::parseFromStream(builder, stream, &root, &errors)) {
        return false;
    }
    
    try {
        if (root.isMember("event_type")) notification.set_event_type(root["event_type"].asString());
        if (root.isMember("base_id")) notification.set_base_id(root["base_id"].asString());
        if (root.isMember("nt_instrument_symbol")) notification.set_nt_instrument_symbol(root["nt_instrument_symbol"].asString());
        if (root.isMember("nt_account_name")) notification.set_nt_account_name(root["nt_account_name"].asString());
        if (root.isMember("closed_hedge_quantity")) notification.set_closed_hedge_quantity(root["closed_hedge_quantity"].asDouble());
        if (root.isMember("closed_hedge_action")) notification.set_closed_hedge_action(root["closed_hedge_action"].asString());
        if (root.isMember("timestamp")) notification.set_timestamp(root["timestamp"].asString());
        if (root.isMember("closure_reason")) notification.set_closure_reason(root["closure_reason"].asString());
        
        return true;
    }
    catch (...) {
        return false;
    }
}

bool JsonConverter::JsonToElasticHedgeUpdate(const std::string& json, trading::ElasticHedgeUpdate& update) {
    if (!ValidateJson(json)) {
        return false;
    }
    
    Json::Value root;
    Json::CharReaderBuilder builder;
    std::string errors;
    std::istringstream stream(json);
    
    if (!Json::parseFromStream(builder, stream, &root, &errors)) {
        return false;
    }
    
    try {
        if (root.isMember("event_type")) update.set_event_type(root["event_type"].asString());
        if (root.isMember("action")) update.set_action(root["action"].asString());
        if (root.isMember("base_id")) update.set_base_id(root["base_id"].asString());
        if (root.isMember("current_profit")) update.set_current_profit(root["current_profit"].asDouble());
        if (root.isMember("profit_level")) update.set_profit_level(root["profit_level"].asInt());
        if (root.isMember("timestamp")) update.set_timestamp(root["timestamp"].asString());
        
        return true;
    }
    catch (...) {
        return false;
    }
}

bool JsonConverter::JsonToTrailingStopUpdate(const std::string& json, trading::TrailingStopUpdate& update) {
    if (!ValidateJson(json)) {
        return false;
    }
    
    Json::Value root;
    Json::CharReaderBuilder builder;
    std::string errors;
    std::istringstream stream(json);
    
    if (!Json::parseFromStream(builder, stream, &root, &errors)) {
        return false;
    }
    
    try {
        if (root.isMember("event_type")) update.set_event_type(root["event_type"].asString());
        if (root.isMember("base_id")) update.set_base_id(root["base_id"].asString());
        if (root.isMember("new_stop_price")) update.set_new_stop_price(root["new_stop_price"].asDouble());
        if (root.isMember("trailing_type")) update.set_trailing_type(root["trailing_type"].asString());
        if (root.isMember("current_price")) update.set_current_price(root["current_price"].asDouble());
        if (root.isMember("timestamp")) update.set_timestamp(root["timestamp"].asString());
        
        return true;
    }
    catch (...) {
        return false;
    }
}

bool JsonConverter::JsonToHeartbeatRequest(const std::string& json, trading::HeartbeatRequest& request) {
    if (!ValidateJson(json)) {
        return false;
    }
    
    Json::Value root;
    Json::CharReaderBuilder builder;
    std::string errors;
    std::istringstream stream(json);
    
    if (!Json::parseFromStream(builder, stream, &root, &errors)) {
        return false;
    }
    
    try {
        if (root.isMember("component")) request.set_component(root["component"].asString());
        if (root.isMember("status")) request.set_status(root["status"].asString());
        if (root.isMember("version")) request.set_version(root["version"].asString());
        if (root.isMember("timestamp")) request.set_timestamp(root["timestamp"].asInt64());
        
        return true;
    }
    catch (...) {
        return false;
    }
}

// Convert protobuf messages to JSON
std::string JsonConverter::TradeToJson(const trading::Trade& trade) {
    Json::Value root;
    
    root["id"] = trade.id();
    root["base_id"] = trade.base_id();
    root["timestamp"] = static_cast<Json::Int64>(trade.timestamp());
    root["action"] = trade.action();
    root["quantity"] = trade.quantity();
    root["price"] = trade.price();
    root["total_quantity"] = trade.total_quantity();
    root["contract_num"] = trade.contract_num();
    root["order_type"] = trade.order_type();
    root["measurement_pips"] = trade.measurement_pips();
    root["raw_measurement"] = trade.raw_measurement();
    root["instrument"] = trade.instrument();
    root["account_name"] = trade.account_name();
    root["nt_balance"] = trade.nt_balance();
    root["nt_daily_pnl"] = trade.nt_daily_pnl();
    root["nt_trade_result"] = trade.nt_trade_result();
    root["nt_session_trades"] = trade.nt_session_trades();
    
    Json::StreamWriterBuilder builder;
    builder["indentation"] = "";
    return Json::writeString(builder, root);
}

std::string JsonConverter::HealthResponseToJson(const trading::HealthResponse& response) {
    Json::Value root;
    
    root["status"] = response.status();
    root["queue_size"] = response.queue_size();
    root["net_position"] = response.net_position();
    root["hedge_size"] = response.hedge_size();
    
    Json::StreamWriterBuilder builder;
    builder["indentation"] = "";
    return Json::writeString(builder, root);
}

std::string JsonConverter::GenericResponseToJson(const trading::GenericResponse& response) {
    Json::Value root;
    
    root["status"] = response.status();
    root["message"] = response.message();
    
    Json::Value metadata;
    for (const auto& pair : response.metadata()) {
        metadata[pair.first] = pair.second;
    }
    root["metadata"] = metadata;
    
    Json::StreamWriterBuilder builder;
    builder["indentation"] = "";
    return Json::writeString(builder, root);
}

std::string JsonConverter::HeartbeatResponseToJson(const trading::HeartbeatResponse& response) {
    Json::Value root;
    
    root["status"] = response.status();
    root["message"] = response.message();
    
    Json::StreamWriterBuilder builder;
    builder["indentation"] = "";
    return Json::writeString(builder, root);
}

// Utility methods
std::string JsonConverter::GetErrorJson(int error_code, const std::string& message) {
    Json::Value root;
    root["error"] = true;
    root["error_code"] = error_code;
    root["error_message"] = message;
    root["timestamp"] = GetCurrentTimestamp();
    
    Json::StreamWriterBuilder builder;
    builder["indentation"] = "";
    return Json::writeString(builder, root);
}

std::string JsonConverter::GetSuccessJson(const std::string& message) {
    Json::Value root;
    root["success"] = true;
    root["message"] = message.empty() ? "Operation completed successfully" : message;
    root["timestamp"] = GetCurrentTimestamp();
    
    Json::StreamWriterBuilder builder;
    builder["indentation"] = "";
    return Json::writeString(builder, root);
}

std::string JsonConverter::GetConnectionStatusJson(
    bool connected,
    const std::string& server_address,
    int port,
    const std::string& connection_time,
    int error_code,
    const std::string& error_message) {
    
    Json::Value root;
    root["connected"] = connected;
    root["server_address"] = server_address;
    root["port"] = port;
    root["connection_time"] = connection_time;
    root["timestamp"] = GetCurrentTimestamp();
    
    if (error_code != 0) {
        root["error_code"] = error_code;
        root["error_message"] = error_message;
    }
    
    Json::StreamWriterBuilder builder;
    builder["indentation"] = "";
    return Json::writeString(builder, root);
}

std::string JsonConverter::GetStreamingStatsJson(
    bool streaming_active,
    int trades_received,
    int requests_sent,
    int queue_size,
    int connection_attempts,
    int streaming_restarts,
    const std::string& last_trade_time) {
    
    Json::Value root;
    root["streaming_active"] = streaming_active;
    root["trades_received"] = trades_received;
    root["requests_sent"] = requests_sent;
    root["queue_size"] = queue_size;
    root["connection_attempts"] = connection_attempts;
    root["streaming_restarts"] = streaming_restarts;
    root["timestamp"] = GetCurrentTimestamp();
    
    if (!last_trade_time.empty()) {
        root["last_trade_time"] = last_trade_time;
    }
    
    Json::StreamWriterBuilder builder;
    builder["indentation"] = "";
    return Json::writeString(builder, root);
}