#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <string>
#include <memory>
#include <thread>
#include <atomic>
#include <queue>
#include <mutex>
#include <condition_variable>
#include <chrono>
#include <sstream>
#include <iomanip>
#include <optional>
#include <cstdint>
#include <cstring>

#pragma comment(lib, "ws2_32.lib")

// We'll implement a simplified gRPC-like client using HTTP/2 over TCP
// This avoids the complexity of integrating full gRPC C++ libraries
// but provides the streaming functionality we need

// Error codes
#define GRPC_SUCCESS 0
#define GRPC_NOT_IMPLEMENTED -999
#define GRPC_CONNECTION_FAILED -1
#define GRPC_SOCKET_ERROR -2
#define GRPC_TIMEOUT -3
#define GRPC_STREAM_CLOSED -4
#define GRPC_PARSE_ERROR -5

namespace {

bool ReadVarint(const std::string& data, size_t& offset, uint64_t& value) {
    value = 0;
    int shift = 0;
    while (offset < data.size() && shift < 64) {
        uint8_t byte = static_cast<uint8_t>(data[offset++]);
        value |= static_cast<uint64_t>(byte & 0x7F) << shift;
        if ((byte & 0x80) == 0) {
            return true;
        }
        shift += 7;
    }
    return false;
}

bool ReadLengthDelimited(const std::string& data, size_t& offset, std::string& out) {
    uint64_t length = 0;
    if (!ReadVarint(data, offset, length)) {
        return false;
    }
    if (offset + length > data.size()) {
        return false;
    }
    out.assign(data.data() + offset, static_cast<size_t>(length));
    offset += static_cast<size_t>(length);
    return true;
}

bool ReadFixed64Double(const std::string& data, size_t& offset, double& out) {
    if (offset + 8 > data.size()) {
        return false;
    }

    uint64_t raw = 0;
    std::memcpy(&raw, data.data() + offset, 8);
    offset += 8;
    std::memcpy(&out, &raw, 8);
    return true;
}

bool SkipField(int wireType, const std::string& data, size_t& offset) {
    switch (wireType) {
        case 0: { // varint
            uint64_t dummy;
            return ReadVarint(data, offset, dummy);
        }
        case 1: { // 64-bit
            if (offset + 8 > data.size()) return false;
            offset += 8;
            return true;
        }
        case 2: { // length-delimited
            uint64_t length = 0;
            if (!ReadVarint(data, offset, length)) return false;
            if (offset + length > data.size()) return false;
            offset += static_cast<size_t>(length);
            return true;
        }
        case 5: { // 32-bit
            if (offset + 4 > data.size()) return false;
            offset += 4;
            return true;
        }
        default:
            return false;
    }
}

std::string EscapeJson(const std::string& value) {
    std::ostringstream oss;
    for (char ch : value) {
        switch (ch) {
            case '\\': oss << "\\\\"; break;
            case '\"': oss << "\\\""; break;
            case '\b': oss << "\\b"; break;
            case '\f': oss << "\\f"; break;
            case '\n': oss << "\\n"; break;
            case '\r': oss << "\\r"; break;
            case '\t': oss << "\\t"; break;
            default:
                if (static_cast<unsigned char>(ch) < 0x20) {
                    oss << "\\u" << std::hex << std::setw(4) << std::setfill('0')
                        << static_cast<int>(static_cast<unsigned char>(ch));
                } else {
                    oss << ch;
                }
                break;
        }
    }
    return oss.str();
}

bool ParseTradeMessage(const std::string& protoData, std::string& tradeJson) {
    struct TradeFields {
        std::optional<std::string> id;
        std::optional<std::string> base_id;
        std::optional<int64_t> timestamp;
        std::optional<std::string> action;
        std::optional<double> quantity;
        std::optional<double> price;
        std::optional<int32_t> total_quantity;
        std::optional<int32_t> contract_num;
        std::optional<std::string> order_type;
        std::optional<int32_t> measurement_pips;
        std::optional<double> raw_measurement;
        std::optional<std::string> instrument;
        std::optional<std::string> account_name;
        std::optional<double> nt_balance;
        std::optional<double> nt_daily_pnl;
        std::optional<std::string> nt_trade_result;
        std::optional<int32_t> nt_session_trades;
        std::optional<uint64_t> mt5_ticket;
        std::optional<double> nt_points_per_1k_loss;
        std::optional<std::string> event_type;
        std::optional<double> elastic_current_profit;
        std::optional<int32_t> elastic_profit_level;
        std::optional<std::string> qt_trade_id;
        std::optional<std::string> qt_position_id;
        std::optional<std::string> strategy_tag;
        std::optional<std::string> origin_platform;
    } fields;

    size_t offset = 0;
    while (offset < protoData.size()) {
        uint64_t key = 0;
        if (!ReadVarint(protoData, offset, key)) {
            return false;
        }

        uint32_t fieldNumber = static_cast<uint32_t>(key >> 3);
        uint32_t wireType = static_cast<uint32_t>(key & 0x07);

        switch (fieldNumber) {
            case 1: {
                std::string value;
                if (!ReadLengthDelimited(protoData, offset, value)) return false;
                fields.id = value;
                break;
            }
            case 2: {
                std::string value;
                if (!ReadLengthDelimited(protoData, offset, value)) return false;
                fields.base_id = value;
                break;
            }
            case 3: {
                uint64_t value = 0;
                if (!ReadVarint(protoData, offset, value)) return false;
                fields.timestamp = static_cast<int64_t>(value);
                break;
            }
            case 4: {
                std::string value;
                if (!ReadLengthDelimited(protoData, offset, value)) return false;
                fields.action = value;
                break;
            }
            case 5: {
                double value = 0.0;
                if (!ReadFixed64Double(protoData, offset, value)) return false;
                fields.quantity = value;
                break;
            }
            case 6: {
                double value = 0.0;
                if (!ReadFixed64Double(protoData, offset, value)) return false;
                fields.price = value;
                break;
            }
            case 7: {
                uint64_t value = 0;
                if (!ReadVarint(protoData, offset, value)) return false;
                fields.total_quantity = static_cast<int32_t>(value);
                break;
            }
            case 8: {
                uint64_t value = 0;
                if (!ReadVarint(protoData, offset, value)) return false;
                fields.contract_num = static_cast<int32_t>(value);
                break;
            }
            case 9: {
                std::string value;
                if (!ReadLengthDelimited(protoData, offset, value)) return false;
                fields.order_type = value;
                break;
            }
            case 10: {
                uint64_t value = 0;
                if (!ReadVarint(protoData, offset, value)) return false;
                fields.measurement_pips = static_cast<int32_t>(value);
                break;
            }
            case 11: {
                double value = 0.0;
                if (!ReadFixed64Double(protoData, offset, value)) return false;
                fields.raw_measurement = value;
                break;
            }
            case 12: {
                std::string value;
                if (!ReadLengthDelimited(protoData, offset, value)) return false;
                fields.instrument = value;
                break;
            }
            case 13: {
                std::string value;
                if (!ReadLengthDelimited(protoData, offset, value)) return false;
                fields.account_name = value;
                break;
            }
            case 14: {
                double value = 0.0;
                if (!ReadFixed64Double(protoData, offset, value)) return false;
                fields.nt_balance = value;
                break;
            }
            case 15: {
                double value = 0.0;
                if (!ReadFixed64Double(protoData, offset, value)) return false;
                fields.nt_daily_pnl = value;
                break;
            }
            case 16: {
                std::string value;
                if (!ReadLengthDelimited(protoData, offset, value)) return false;
                fields.nt_trade_result = value;
                break;
            }
            case 17: {
                uint64_t value = 0;
                if (!ReadVarint(protoData, offset, value)) return false;
                fields.nt_session_trades = static_cast<int32_t>(value);
                break;
            }
            case 18: {
                uint64_t value = 0;
                if (!ReadVarint(protoData, offset, value)) return false;
                fields.mt5_ticket = value;
                break;
            }
            case 19: {
                double value = 0.0;
                if (!ReadFixed64Double(protoData, offset, value)) return false;
                fields.nt_points_per_1k_loss = value;
                break;
            }
            case 20: {
                std::string value;
                if (!ReadLengthDelimited(protoData, offset, value)) return false;
                fields.event_type = value;
                break;
            }
            case 21: {
                double value = 0.0;
                if (!ReadFixed64Double(protoData, offset, value)) return false;
                fields.elastic_current_profit = value;
                break;
            }
            case 22: {
                uint64_t value = 0;
                if (!ReadVarint(protoData, offset, value)) return false;
                fields.elastic_profit_level = static_cast<int32_t>(value);
                break;
            }
            case 23: {
                std::string value;
                if (!ReadLengthDelimited(protoData, offset, value)) return false;
                fields.qt_trade_id = value;
                break;
            }
            case 24: {
                std::string value;
                if (!ReadLengthDelimited(protoData, offset, value)) return false;
                fields.qt_position_id = value;
                break;
            }
            case 25: {
                std::string value;
                if (!ReadLengthDelimited(protoData, offset, value)) return false;
                fields.strategy_tag = value;
                break;
            }
            case 26: {
                std::string value;
                if (!ReadLengthDelimited(protoData, offset, value)) return false;
                fields.origin_platform = value;
                break;
            }
            default:
                if (!SkipField(wireType, protoData, offset)) {
                    return false;
                }
                break;
        }
    }

    std::ostringstream json;
    json << "{";
    bool first = true;

    auto appendString = [&](const char* key, const std::optional<std::string>& value) {
        if (!value) return;
        if (!first) json << ','; else first = false;
        json << '\"' << key << "\":\"" << EscapeJson(*value) << "\"";
    };

    auto appendDouble = [&](const char* key, const std::optional<double>& value) {
        if (!value) return;
        if (!first) json << ','; else first = false;
        json << '\"' << key << "\":";
        json << std::setprecision(15) << std::defaultfloat << *value;
    };

    auto appendInt = [&](const char* key, const std::optional<int32_t>& value) {
        if (!value) return;
        if (!first) json << ','; else first = false;
        json << '\"' << key << "\":" << *value;
    };

    auto appendInt64 = [&](const char* key, const std::optional<int64_t>& value) {
        if (!value) return;
        if (!first) json << ','; else first = false;
        json << '\"' << key << "\":" << *value;
    };

    auto appendUint64 = [&](const char* key, const std::optional<uint64_t>& value) {
        if (!value) return;
        if (!first) json << ','; else first = false;
        json << '\"' << key << "\":" << *value;
    };

    appendString("id", fields.id);
    appendString("base_id", fields.base_id);
    appendInt64("timestamp", fields.timestamp);
    appendString("action", fields.action);
    appendDouble("quantity", fields.quantity);
    appendDouble("price", fields.price);
    appendInt("total_quantity", fields.total_quantity);
    appendInt("contract_num", fields.contract_num);
    appendString("order_type", fields.order_type);
    appendInt("measurement_pips", fields.measurement_pips);
    appendDouble("raw_measurement", fields.raw_measurement);
    appendString("instrument", fields.instrument);
    appendString("account_name", fields.account_name);
    appendDouble("nt_balance", fields.nt_balance);
    appendDouble("nt_daily_pnl", fields.nt_daily_pnl);
    appendString("nt_trade_result", fields.nt_trade_result);
    appendInt("nt_session_trades", fields.nt_session_trades);
    appendUint64("mt5_ticket", fields.mt5_ticket);
    appendDouble("nt_points_per_1k_loss", fields.nt_points_per_1k_loss);
    appendString("event_type", fields.event_type);
    appendDouble("elastic_current_profit", fields.elastic_current_profit);
    appendInt("elastic_profit_level", fields.elastic_profit_level);
    appendString("qt_trade_id", fields.qt_trade_id);
    appendString("qt_position_id", fields.qt_position_id);
    appendString("strategy_tag", fields.strategy_tag);
    appendString("origin_platform", fields.origin_platform);

    json << "}";
    tradeJson = json.str();
    return true;
}

} // namespace

// Forward declarations
extern "C" {
    __declspec(dllexport) int __stdcall TestFunction();
    __declspec(dllexport) int __stdcall GrpcInitialize(const wchar_t* server_address, int port);
    __declspec(dllexport) int __stdcall GrpcShutdown();
    __declspec(dllexport) int __stdcall GrpcIsConnected();
    __declspec(dllexport) int __stdcall GrpcReconnect();
    
    __declspec(dllexport) int __stdcall GrpcStartTradeStream();
    __declspec(dllexport) int __stdcall GrpcStopTradeStream();
    __declspec(dllexport) int __stdcall GrpcGetNextTrade(wchar_t* trade_json, int buffer_size);
    __declspec(dllexport) int __stdcall GrpcGetTradeQueueSize();
    
    __declspec(dllexport) int __stdcall GrpcSubmitTradeResult(const wchar_t* result_json);
    __declspec(dllexport) int __stdcall GrpcHealthCheck(const wchar_t* request_json, wchar_t* response_json, int buffer_size);
    __declspec(dllexport) int __stdcall GrpcNotifyHedgeClose(const wchar_t* notification_json);
    __declspec(dllexport) int __stdcall GrpcSubmitElasticUpdate(const wchar_t* update_json);
    __declspec(dllexport) int __stdcall GrpcSubmitTrailingUpdate(const wchar_t* update_json);
    __declspec(dllexport) int __stdcall GrpcGetLastErrorMessage(wchar_t* error_message, int buffer_size);
}

// Global state
static bool g_wsaInitialized = false;
static bool g_grpcConnected = false;
static std::string g_serverAddress = "";
static int g_serverPort = 0;
static std::string g_lastError = "";

// Streaming state
static std::atomic<bool> g_streamActive(false);
static std::thread g_streamThread;
static SOCKET g_streamSocket = INVALID_SOCKET;
static std::queue<std::string> g_tradeQueue;
static std::mutex g_queueMutex;
static std::condition_variable g_queueCondition;

// Helper functions
bool InitializeWinsock() {
    if (g_wsaInitialized) return true;
    
    WSADATA wsaData;
    int result = WSAStartup(MAKEWORD(2, 2), &wsaData);
    if (result != 0) {
        g_lastError = "WSAStartup failed with error: " + std::to_string(result);
        return false;
    }
    g_wsaInitialized = true;
    return true;
}

void CleanupWinsock() {
    if (g_wsaInitialized) {
        WSACleanup();
        g_wsaInitialized = false;
    }
}

// Test TCP connection to gRPC server
bool TestTcpConnection(const std::string& address, int port) {
    if (!InitializeWinsock()) {
        return false;
    }

    SOCKET sock = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (sock == INVALID_SOCKET) {
        g_lastError = "Failed to create socket: " + std::to_string(WSAGetLastError());
        return false;
    }

    // Set timeout
    DWORD timeout = 5000; // 5 seconds
    setsockopt(sock, SOL_SOCKET, SO_RCVTIMEO, (char*)&timeout, sizeof(timeout));
    setsockopt(sock, SOL_SOCKET, SO_SNDTIMEO, (char*)&timeout, sizeof(timeout));

    sockaddr_in serverAddr;
    serverAddr.sin_family = AF_INET;
    serverAddr.sin_port = htons(port);
    
    // Convert address
    if (inet_pton(AF_INET, address.c_str(), &serverAddr.sin_addr) <= 0) {
        g_lastError = "Invalid server address: " + address;
        closesocket(sock);
        return false;
    }

    // Attempt connection
    int result = connect(sock, (sockaddr*)&serverAddr, sizeof(serverAddr));
    bool connected = (result == 0);
    
    if (!connected) {
        g_lastError = "Connection failed: " + std::to_string(WSAGetLastError());
    }
    
    closesocket(sock);
    return connected;
}

// Create persistent HTTP/2 connection for streaming
SOCKET CreateStreamConnection() {
    if (!InitializeWinsock()) {
        return INVALID_SOCKET;
    }

    SOCKET sock = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (sock == INVALID_SOCKET) {
        g_lastError = "Failed to create stream socket: " + std::to_string(WSAGetLastError());
        return INVALID_SOCKET;
    }

    // Set socket options for streaming
    DWORD timeout = 30000; // 30 seconds
    setsockopt(sock, SOL_SOCKET, SO_RCVTIMEO, (char*)&timeout, sizeof(timeout));
    setsockopt(sock, SOL_SOCKET, SO_SNDTIMEO, (char*)&timeout, sizeof(timeout));

    // Enable keep-alive
    BOOL keepAlive = TRUE;
    setsockopt(sock, SOL_SOCKET, SO_KEEPALIVE, (char*)&keepAlive, sizeof(keepAlive));

    sockaddr_in serverAddr;
    serverAddr.sin_family = AF_INET;
    serverAddr.sin_port = htons(g_serverPort);
    inet_pton(AF_INET, g_serverAddress.c_str(), &serverAddr.sin_addr);

    if (connect(sock, (sockaddr*)&serverAddr, sizeof(serverAddr)) != 0) {
        g_lastError = "Stream connection failed: " + std::to_string(WSAGetLastError());
        closesocket(sock);
        return INVALID_SOCKET;
    }

    return sock;
}

// Create gRPC message frame with proper protobuf encoding
std::string CreateGrpcMessage(const std::string& data, bool compressed = false) {
    std::string frame;
    // gRPC frame format: [compression flag: 1 byte][length: 4 bytes big-endian][message: N bytes]
    frame += compressed ? '\x01' : '\x00';
    
    uint32_t length = static_cast<uint32_t>(data.length());
    frame += static_cast<char>((length >> 24) & 0xFF);
    frame += static_cast<char>((length >> 16) & 0xFF);
    frame += static_cast<char>((length >> 8) & 0xFF);
    frame += static_cast<char>(length & 0xFF);
    
    frame += data;
    return frame;
}

// Create protobuf-encoded GetTradesRequest message
std::string CreateGetTradesRequestProto(const std::string& source = "hedgebot", int openPositions = 0) {
    std::string proto;
    
    // Field 1 (source): tag=0x0A (field 1, wire type 2=length-delimited)
    if (!source.empty()) {
        proto += '\x0A'; // tag for field 1
        proto += static_cast<char>(source.length()); // length
        proto += source; // value
    }
    
    // Field 2 (open_positions): tag=0x10 (field 2, wire type 0=varint)
    if (openPositions > 0) {
        proto += '\x10'; // tag for field 2
        // Simple varint encoding for small numbers
        if (openPositions < 128) {
            proto += static_cast<char>(openPositions);
        } else {
            proto += static_cast<char>((openPositions & 0x7F) | 0x80);
            proto += static_cast<char>((openPositions >> 7) & 0x7F);
        }
    }
    
    return proto;
}

// Send proper gRPC request using HTTP/2 format that Go gRPC server expects
bool SendGrpcStreamInit(SOCKET sock, const std::string& method) {
    // HTTP/2 Connection Preface
    const char* preface = "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n";
    if (send(sock, preface, 24, 0) == SOCKET_ERROR) {
        g_lastError = "Failed to send HTTP/2 preface";
        return false;
    }
    
    // HTTP/2 SETTINGS frame
    unsigned char settings[] = {
        0x00, 0x00, 0x12,  // Length: 18 bytes (3 settings * 6 bytes each)
        0x04,              // Type: SETTINGS
        0x00,              // Flags: 0
        0x00, 0x00, 0x00, 0x00, // Stream ID: 0
        
        // SETTINGS_HEADER_TABLE_SIZE = 4096
        0x00, 0x01,        // Setting ID: HEADER_TABLE_SIZE
        0x00, 0x00, 0x10, 0x00, // Value: 4096
        
        // SETTINGS_ENABLE_PUSH = 0 (disabled)
        0x00, 0x02,        // Setting ID: ENABLE_PUSH
        0x00, 0x00, 0x00, 0x00, // Value: 0
        
        // SETTINGS_MAX_FRAME_SIZE = 16384
        0x00, 0x05,        // Setting ID: MAX_FRAME_SIZE
        0x00, 0x00, 0x40, 0x00  // Value: 16384
    };
    if (send(sock, reinterpret_cast<char*>(settings), sizeof(settings), 0) == SOCKET_ERROR) {
        g_lastError = "Failed to send SETTINGS frame";
        return false;
    }
    
    // Send SETTINGS ACK frame (required by HTTP/2)
    unsigned char settingsAck[] = {
        0x00, 0x00, 0x00,  // Length: 0 bytes
        0x04,              // Type: SETTINGS
        0x01,              // Flags: ACK
        0x00, 0x00, 0x00, 0x00  // Stream ID: 0
    };
    if (send(sock, reinterpret_cast<char*>(settingsAck), sizeof(settingsAck), 0) == SOCKET_ERROR) {
        g_lastError = "Failed to send SETTINGS ACK frame";
        return false;
    }
    
    // HTTP/2 HEADERS frame for gRPC GetTrades method
    std::string path = "/trading.TradingService/" + method;
    std::string authority = g_serverAddress + ":" + std::to_string(g_serverPort);
    
    // Build proper gRPC headers using HPACK encoding (simplified)
    std::string headers;
    
    // :method: POST (HPACK static table index 3)
    headers += '\x83'; // Index 3 with indexing
    
    // :path: /trading.TradingService/GetTrades
    headers += '\x04'; // :path literal without indexing
    headers += static_cast<char>(path.length());
    headers += path;
    
    // :scheme: http (HPACK static table index 6)
    headers += '\x86'; // Index 6 with indexing
    
    // :authority: 127.0.0.1:50051
    headers += '\x01'; // :authority literal without indexing
    headers += static_cast<char>(authority.length());
    headers += authority;
    
    // content-type: application/grpc
    headers += '\x40'; // Literal with incremental indexing
    headers += '\x0c'; // Length of "content-type"
    headers += "content-type";
    headers += '\x10'; // Length of "application/grpc"
    headers += "application/grpc";
    
    // grpc-encoding: identity
    headers += '\x40'; // Literal with incremental indexing
    headers += '\x0d'; // Length of "grpc-encoding"
    headers += "grpc-encoding";
    headers += '\x08'; // Length of "identity"
    headers += "identity";
    
    // te: trailers (required for gRPC)
    headers += '\x40'; // Literal with incremental indexing
    headers += '\x02'; // Length of "te"
    headers += "te";
    headers += '\x08'; // Length of "trailers"
    headers += "trailers";
    
    // user-agent: mt5-grpc-client
    headers += '\x40'; // Literal with incremental indexing
    headers += '\x0a'; // Length of "user-agent"
    headers += "user-agent";
    headers += '\x0f'; // Length of "mt5-grpc-client"
    headers += "mt5-grpc-client";
    
    // HTTP/2 HEADERS frame
    uint32_t headerLen = static_cast<uint32_t>(headers.length());
    unsigned char headerFrame[9]; // Frame header
    
    // Length (24 bits)
    headerFrame[0] = (headerLen >> 16) & 0xFF;
    headerFrame[1] = (headerLen >> 8) & 0xFF;
    headerFrame[2] = headerLen & 0xFF;
    
    headerFrame[3] = 0x01; // Type: HEADERS
    headerFrame[4] = 0x04; // Flags: END_HEADERS
    
    // Stream ID: 1 (31 bits)
    headerFrame[5] = 0x00;
    headerFrame[6] = 0x00;
    headerFrame[7] = 0x00;
    headerFrame[8] = 0x01;
    
    if (send(sock, reinterpret_cast<char*>(headerFrame), 9, 0) == SOCKET_ERROR) {
        g_lastError = "Failed to send HEADERS frame header";
        return false;
    }
    
    if (send(sock, headers.c_str(), headerLen, 0) == SOCKET_ERROR) {
        g_lastError = "Failed to send HEADERS frame payload";
        return false;
    }
    
    return true;
}

// Send gRPC message as HTTP/2 DATA frame
bool SendGrpcMessage(SOCKET sock, const std::string& grpcMessage) {
    // HTTP/2 DATA frame header (9 bytes) + payload
    uint32_t dataLen = static_cast<uint32_t>(grpcMessage.length());
    unsigned char dataFrameHeader[9];
    
    // Length (24 bits)
    dataFrameHeader[0] = (dataLen >> 16) & 0xFF;
    dataFrameHeader[1] = (dataLen >> 8) & 0xFF;
    dataFrameHeader[2] = dataLen & 0xFF;
    
    dataFrameHeader[3] = 0x00; // Type: DATA
    dataFrameHeader[4] = 0x00; // Flags: 0 (not end of stream)
    
    // Stream ID: 1 (31 bits)
    dataFrameHeader[5] = 0x00;
    dataFrameHeader[6] = 0x00;
    dataFrameHeader[7] = 0x00;
    dataFrameHeader[8] = 0x01;
    
    // Send frame header
    if (send(sock, reinterpret_cast<char*>(dataFrameHeader), 9, 0) == SOCKET_ERROR) {
        g_lastError = "Failed to send DATA frame header";
        return false;
    }
    
    // Send frame payload (gRPC message)
    if (send(sock, grpcMessage.c_str(), dataLen, 0) == SOCKET_ERROR) {
        g_lastError = "Failed to send DATA frame payload";
        return false;
    }
    
    return true;
}

// Legacy function for backward compatibility
bool SendGrpcRequest(SOCKET sock, const std::string& method, const std::string& data = "") {
    // For simple unary calls, use HTTP/1.1 (kept for compatibility)
    std::string request = 
        "POST /trading.TradingService/" + method + " HTTP/1.1\r\n"
        "Host: " + g_serverAddress + ":" + std::to_string(g_serverPort) + "\r\n"
        "Content-Type: application/grpc+proto\r\n"
        "Te: trailers\r\n"
        "User-Agent: mt5-grpc-client/1.0\r\n";
    
    if (!data.empty()) {
        request += "Content-Length: " + std::to_string(data.length()) + "\r\n";
    }
    
    request += "\r\n";
    
    if (!data.empty()) {
        request += data;
    }

    int bytesSent = send(sock, request.c_str(), request.length(), 0);
    if (bytesSent == SOCKET_ERROR) {
        g_lastError = "Failed to send gRPC request: " + std::to_string(WSAGetLastError());
        return false;
    }

    return true;
}

// Parse gRPC response from HTTP/2 DATA frame
bool ParseGrpcResponse(const std::string& data, std::string& tradeJson) {
    // Look for HTTP/2 DATA frames containing gRPC messages
    size_t pos = 0;
    while (pos < data.length()) {
        // Skip HTTP/2 frame headers (9 bytes minimum)
        if (pos + 9 >= data.length()) break;
        
        // Check if this looks like a DATA frame (type 0x00)
        if (data[pos + 3] == 0x00) {
            // Extract frame length (3 bytes, big-endian)
            uint32_t frameLen = 
                (static_cast<uint8_t>(data[pos]) << 16) |
                (static_cast<uint8_t>(data[pos + 1]) << 8) |
                static_cast<uint8_t>(data[pos + 2]);
                
            if (pos + 9 + frameLen <= data.length()) {
                std::string frameData = data.substr(pos + 9, frameLen);
                
                // Check for gRPC message (starts with compression flag + length)
                if (frameData.length() >= 5) {
                    uint8_t compressionFlag = static_cast<uint8_t>(frameData[0]);
                    uint32_t msgLen = 
                        (static_cast<uint8_t>(frameData[1]) << 24) |
                        (static_cast<uint8_t>(frameData[2]) << 16) |
                        (static_cast<uint8_t>(frameData[3]) << 8) |
                        static_cast<uint8_t>(frameData[4]);
                        
                    if (frameData.length() >= 5 + msgLen) {
                        std::string protoData = frameData.substr(5, msgLen);
                        
                        // Decode protobuf Trade payload into JSON using a minimal decoder
                        if (!protoData.empty()) {
                            if (ParseTradeMessage(protoData, tradeJson)) {
                                return true;
                            }
                        }
                    }
                }
            }
            pos += 9 + frameLen;
        } else {
            pos++;
        }
    }
    
    return false;
}

// Stream polling thread function with proper gRPC bidirectional streaming
void StreamPollingThread() {
    while (g_streamActive) {
        try {
            // Create persistent connection for streaming
            SOCKET streamSock = CreateStreamConnection();
            if (streamSock == INVALID_SOCKET) {
                std::this_thread::sleep_for(std::chrono::seconds(3));
                continue;
            }

            // Initialize HTTP/2 gRPC stream for GetTrades
            if (!SendGrpcStreamInit(streamSock, "GetTrades")) {
                closesocket(streamSock);
                std::this_thread::sleep_for(std::chrono::seconds(3));
                continue;
            }

            // Send initial GetTradesRequest to establish the stream
            std::string healthProto = CreateGetTradesRequestProto("hedgebot", 0);
            std::string healthMessage = CreateGrpcMessage(healthProto);
            
            if (!SendGrpcMessage(streamSock, healthMessage)) {
                closesocket(streamSock);
                std::this_thread::sleep_for(std::chrono::seconds(3));
                continue;
            }

            // Main streaming loop - send periodic health requests and listen for trades
            auto lastHealthCheck = std::chrono::steady_clock::now();
            char buffer[8192];
            
            while (g_streamActive) {
                // Send periodic health requests to keep stream alive (every 5 seconds)
                auto now = std::chrono::steady_clock::now();
                if (std::chrono::duration_cast<std::chrono::seconds>(now - lastHealthCheck).count() >= 5) {
                    std::string healthProto = CreateGetTradesRequestProto("hedgebot", 0);
                    std::string healthMessage = CreateGrpcMessage(healthProto);
                    
                    if (!SendGrpcMessage(streamSock, healthMessage)) {
                        break; // Connection lost
                    }
                    lastHealthCheck = now;
                }

                // Listen for incoming trades (non-blocking with timeout)
                fd_set readSet;
                FD_ZERO(&readSet);
                FD_SET(streamSock, &readSet);
                
                timeval timeout;
                timeout.tv_sec = 1; // 1 second timeout
                timeout.tv_usec = 0;
                
                int selectResult = select(0, &readSet, NULL, NULL, &timeout);
                
                if (selectResult > 0 && FD_ISSET(streamSock, &readSet)) {
                    int bytesReceived = recv(streamSock, buffer, sizeof(buffer) - 1, 0);
                    
                    if (bytesReceived > 0) {
                        buffer[bytesReceived] = '\0';
                        std::string responseData(buffer, bytesReceived);
                        
                        // Parse gRPC response for Trade messages
                        std::string tradeJson;
                        if (ParseGrpcResponse(responseData, tradeJson)) {
                            std::lock_guard<std::mutex> lock(g_queueMutex);
                            g_tradeQueue.push(tradeJson);
                            g_queueCondition.notify_one();
                        }
                        
                    } else if (bytesReceived == 0) {
                        // Connection closed by server
                        break;
                    } else {
                        // Socket error
                        int error = WSAGetLastError();
                        if (error != WSAEWOULDBLOCK && error != WSAETIMEDOUT) {
                            break;
                        }
                    }
                }
                
                // Small delay to prevent busy loop
                std::this_thread::sleep_for(std::chrono::milliseconds(100));
            }

            closesocket(streamSock);
            
        } catch (...) {
            // Continue streaming on any exception
            std::this_thread::sleep_for(std::chrono::seconds(3));
        }
    }
}

// Convert wchar_t to string
std::string WCharToString(const wchar_t* wstr) {
    if (!wstr) return "";
    int size = WideCharToMultiByte(CP_UTF8, 0, wstr, -1, nullptr, 0, nullptr, nullptr);
    if (size <= 0) return "";
    
    std::string result(size - 1, 0);
    WideCharToMultiByte(CP_UTF8, 0, wstr, -1, &result[0], size - 1, nullptr, nullptr);
    return result;
}

// Convert string to wchar_t
void StringToWChar(const std::string& str, wchar_t* buffer, int bufferSize) {
    if (!buffer || bufferSize <= 0) return;
    
    int size = MultiByteToWideChar(CP_UTF8, 0, str.c_str(), -1, buffer, bufferSize - 1);
    if (size <= 0) {
        buffer[0] = L'\0';
    } else {
        buffer[size] = L'\0';
    }
}

// DLL Entry Point
BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID lpReserved) {
    switch (ul_reason_for_call) {
    case DLL_PROCESS_ATTACH:
        break;
    case DLL_THREAD_ATTACH:
        break;
    case DLL_THREAD_DETACH:
        break;
    case DLL_PROCESS_DETACH:
        // Cleanup on DLL unload
        if (g_streamActive) {
            g_streamActive = false;
            if (g_streamThread.joinable()) {
                g_streamThread.join();
            }
        }
        CleanupWinsock();
        break;
    }
    return TRUE;
}

// Exported functions implementation
extern "C" {

__declspec(dllexport) int __stdcall TestFunction() {
    return 42;  // Simple test function returns 42
}

__declspec(dllexport) int __stdcall GrpcInitialize(const wchar_t* server_address, int port) {
    try {
        // Convert server address
        g_serverAddress = WCharToString(server_address);
        g_serverPort = port;
        
        // Test TCP connection to gRPC server
        if (TestTcpConnection(g_serverAddress, g_serverPort)) {
            g_grpcConnected = true;
            g_lastError = "Connection successful";
            return GRPC_SUCCESS;
        } else {
            g_grpcConnected = false;
            return GRPC_CONNECTION_FAILED;
        }
    }
    catch (...) {
        g_grpcConnected = false;
        g_lastError = "Exception during initialization";
        return GRPC_SOCKET_ERROR;
    }
}

__declspec(dllexport) int __stdcall GrpcShutdown() {
    g_grpcConnected = false;
    
    // Stop streaming
    if (g_streamActive) {
        g_streamActive = false;
        if (g_streamThread.joinable()) {
            g_streamThread.join();
        }
    }
    
    // Clear trade queue
    {
        std::lock_guard<std::mutex> lock(g_queueMutex);
        while (!g_tradeQueue.empty()) {
            g_tradeQueue.pop();
        }
    }
    
    CleanupWinsock();
    return GRPC_SUCCESS;
}

__declspec(dllexport) int __stdcall GrpcIsConnected() {
    return g_grpcConnected ? 1 : 0;
}

__declspec(dllexport) int __stdcall GrpcReconnect() {
    GrpcShutdown();
    return GrpcInitialize(std::wstring(g_serverAddress.begin(), g_serverAddress.end()).c_str(), g_serverPort);
}

__declspec(dllexport) int __stdcall GrpcStartTradeStream() {
    if (!g_grpcConnected) {
        g_lastError = "Not connected to gRPC server";
        return GRPC_CONNECTION_FAILED;
    }
    
    if (g_streamActive) {
        return GRPC_SUCCESS; // Already running
    }
    
    try {
        g_streamActive = true;
        g_streamThread = std::thread(StreamPollingThread);
        g_lastError = "Trade streaming started";
        return GRPC_SUCCESS;
    } catch (...) {
        g_streamActive = false;
        g_lastError = "Failed to start streaming thread";
        return GRPC_SOCKET_ERROR;
    }
}

__declspec(dllexport) int __stdcall GrpcStopTradeStream() {
    if (g_streamActive) {
        g_streamActive = false;
        if (g_streamThread.joinable()) {
            g_streamThread.join();
        }
    }
    return GRPC_SUCCESS;
}

__declspec(dllexport) int __stdcall GrpcGetNextTrade(wchar_t* trade_json, int buffer_size) {
    if (!trade_json || buffer_size <= 0) {
        return GRPC_PARSE_ERROR;
    }

    std::lock_guard<std::mutex> lock(g_queueMutex);
    if (g_tradeQueue.empty()) {
        trade_json[0] = L'\0';
        return 0; // No trades available
    }

    std::string trade = g_tradeQueue.front();
    g_tradeQueue.pop();
    
    StringToWChar(trade, trade_json, buffer_size);
    return 1; // Trade retrieved
}

__declspec(dllexport) int __stdcall GrpcGetTradeQueueSize() {
    std::lock_guard<std::mutex> lock(g_queueMutex);
    return static_cast<int>(g_tradeQueue.size());
}

__declspec(dllexport) int __stdcall GrpcSubmitTradeResult(const wchar_t* result_json) {
    if (!g_grpcConnected) {
        return GRPC_CONNECTION_FAILED;
    }
    
    try {
        SOCKET sock = CreateStreamConnection();
        if (sock == INVALID_SOCKET) {
            return GRPC_CONNECTION_FAILED;
        }
        
        std::string jsonData = WCharToString(result_json);
        bool success = SendGrpcRequest(sock, "SubmitTradeResult", jsonData);
        
        closesocket(sock);
        return success ? GRPC_SUCCESS : GRPC_SOCKET_ERROR;
    } catch (...) {
        return GRPC_SOCKET_ERROR;
    }
}

__declspec(dllexport) int __stdcall GrpcHealthCheck(const wchar_t* request_json, wchar_t* response_json, int buffer_size) {
    if (!g_grpcConnected) {
        if (response_json && buffer_size > 0) {
            StringToWChar("{\"status\":\"disconnected\",\"error\":\"Not connected to gRPC server\"}", response_json, buffer_size);
        }
        return GRPC_CONNECTION_FAILED;
    }
    
    try {
        SOCKET sock = CreateStreamConnection();
        if (sock == INVALID_SOCKET) {
            if (response_json && buffer_size > 0) {
                StringToWChar("{\"status\":\"error\",\"error\":\"Connection failed\"}", response_json, buffer_size);
            }
            return GRPC_CONNECTION_FAILED;
        }
        
        std::string requestData = WCharToString(request_json);
        if (!SendGrpcRequest(sock, "HealthCheck", requestData)) {
            closesocket(sock);
            if (response_json && buffer_size > 0) {
                StringToWChar("{\"status\":\"error\",\"error\":\"Send failed\"}", response_json, buffer_size);
            }
            return GRPC_SOCKET_ERROR;
        }
        
        // Read response
        char buffer[2048];
        int bytesReceived = recv(sock, buffer, sizeof(buffer) - 1, 0);
        closesocket(sock);
        
        if (bytesReceived > 0) {
            buffer[bytesReceived] = '\0';
            std::string responseStr(buffer);
            
            // Extract JSON from HTTP response
            size_t jsonStart = responseStr.find("{");
            if (jsonStart != std::string::npos) {
                std::string jsonResponse = responseStr.substr(jsonStart);
                if (response_json && buffer_size > 0) {
                    StringToWChar(jsonResponse, response_json, buffer_size);
                }
                return GRPC_SUCCESS;
            }
        }
        
        if (response_json && buffer_size > 0) {
            StringToWChar("{\"status\":\"connected\",\"queue_size\":0}", response_json, buffer_size);
        }
        return GRPC_SUCCESS;
        
    } catch (...) {
        if (response_json && buffer_size > 0) {
            StringToWChar("{\"status\":\"error\",\"error\":\"Exception occurred\"}", response_json, buffer_size);
        }
        return GRPC_SOCKET_ERROR;
    }
}

__declspec(dllexport) int __stdcall GrpcNotifyHedgeClose(const wchar_t* notification_json) {
    if (!g_grpcConnected) {
        return GRPC_CONNECTION_FAILED;
    }
    
    try {
        SOCKET sock = CreateStreamConnection();
        if (sock == INVALID_SOCKET) {
            return GRPC_CONNECTION_FAILED;
        }
        
        std::string jsonData = WCharToString(notification_json);
        bool success = SendGrpcRequest(sock, "NotifyHedgeClose", jsonData);
        
        closesocket(sock);
        return success ? GRPC_SUCCESS : GRPC_SOCKET_ERROR;
    } catch (...) {
        return GRPC_SOCKET_ERROR;
    }
}

__declspec(dllexport) int __stdcall GrpcSubmitElasticUpdate(const wchar_t* update_json) {
    if (!g_grpcConnected) {
        return GRPC_CONNECTION_FAILED;
    }
    
    try {
        SOCKET sock = CreateStreamConnection();
        if (sock == INVALID_SOCKET) {
            return GRPC_CONNECTION_FAILED;
        }
        
        std::string jsonData = WCharToString(update_json);
        bool success = SendGrpcRequest(sock, "SubmitElasticUpdate", jsonData);
        
        closesocket(sock);
        return success ? GRPC_SUCCESS : GRPC_SOCKET_ERROR;
    } catch (...) {
        return GRPC_SOCKET_ERROR;
    }
}

__declspec(dllexport) int __stdcall GrpcSubmitTrailingUpdate(const wchar_t* update_json) {
    if (!g_grpcConnected) {
        return GRPC_CONNECTION_FAILED;
    }
    
    try {
        SOCKET sock = CreateStreamConnection();
        if (sock == INVALID_SOCKET) {
            return GRPC_CONNECTION_FAILED;
        }
        
        std::string jsonData = WCharToString(update_json);
        bool success = SendGrpcRequest(sock, "SubmitTrailingUpdate", jsonData);
        
        closesocket(sock);
        return success ? GRPC_SUCCESS : GRPC_SOCKET_ERROR;
    } catch (...) {
        return GRPC_SOCKET_ERROR;
    }
}

__declspec(dllexport) int __stdcall GrpcGetLastErrorMessage(wchar_t* error_message, int buffer_size) {
    if (error_message && buffer_size > 0) {
        StringToWChar(g_lastError, error_message, buffer_size);
    }
    return GRPC_SUCCESS;
}

} // extern "C"
