#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <string>

#pragma comment(lib, "ws2_32.lib")

// Simple test exports to verify the C++ wrapper concept
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

// Error codes
#define GRPC_SUCCESS 0
#define GRPC_NOT_IMPLEMENTED -999
#define GRPC_CONNECTION_FAILED -1
#define GRPC_SOCKET_ERROR -2
#define GRPC_TIMEOUT -3

// Global state
static bool g_wsaInitialized = false;
static bool g_grpcConnected = false;
static std::string g_serverAddress = "";
static int g_serverPort = 0;

// Helper functions
bool InitializeWinsock() {
    if (g_wsaInitialized) return true;
    
    WSADATA wsaData;
    int result = WSAStartup(MAKEWORD(2, 2), &wsaData);
    if (result != 0) {
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

    addrinfo hints{};
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = SOCK_STREAM;
    hints.ai_protocol = IPPROTO_TCP;

    addrinfo* resultList = nullptr;
    std::string portString = std::to_string(port);
    int status = getaddrinfo(address.c_str(), portString.c_str(), &hints, &resultList);
    if (status != 0 || resultList == nullptr) {
        if (resultList) {
            freeaddrinfo(resultList);
        }
        return false;
    }

    bool connected = false;
    for (addrinfo* ptr = resultList; ptr != nullptr; ptr = ptr->ai_next) {
        SOCKET sock = socket(ptr->ai_family, ptr->ai_socktype, ptr->ai_protocol);
        if (sock == INVALID_SOCKET) {
            continue;
        }

        DWORD timeout = 5000; // 5 seconds
        setsockopt(sock, SOL_SOCKET, SO_RCVTIMEO, reinterpret_cast<char*>(&timeout), sizeof(timeout));
        setsockopt(sock, SOL_SOCKET, SO_SNDTIMEO, reinterpret_cast<char*>(&timeout), sizeof(timeout));

        if (connect(sock, ptr->ai_addr, static_cast<int>(ptr->ai_addrlen)) == 0) {
            connected = true;
            closesocket(sock);
            break;
        }

        closesocket(sock);
    }

    freeaddrinfo(resultList);
    return connected;
}

// Convert wchar_t to string
std::string WCharToString(const wchar_t* wstr) {
    if (!wstr) return "";
    int size = WideCharToMultiByte(CP_UTF8, 0, wstr, -1, nullptr, 0, nullptr, nullptr);
    if (size <= 0) return "";
    if (size == 1) return ""; // empty string (only null terminator)

    std::string result(size, '\0');
    int written = WideCharToMultiByte(CP_UTF8, 0, wstr, -1, result.data(), size, nullptr, nullptr);
    if (written <= 0) {
        return "";
    }
    // written includes the terminating null
    result.resize(static_cast<size_t>(written - 1));
    return result;
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
        CleanupWinsock();
        break;
    }
    return TRUE;
}

// Simple implementations for testing
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
            return GRPC_SUCCESS;
        } else {
            g_grpcConnected = false;
            return GRPC_CONNECTION_FAILED;
        }
    }
    catch (...) {
        g_grpcConnected = false;
        return GRPC_SOCKET_ERROR;
    }
}

__declspec(dllexport) int __stdcall GrpcShutdown() {
    return GRPC_NOT_IMPLEMENTED;
}

__declspec(dllexport) int __stdcall GrpcIsConnected() {
    return g_grpcConnected ? 1 : 0;
}

__declspec(dllexport) int __stdcall GrpcReconnect() {
    return GRPC_NOT_IMPLEMENTED;
}

__declspec(dllexport) int __stdcall GrpcStartTradeStream() {
    return GRPC_NOT_IMPLEMENTED;
}

__declspec(dllexport) int __stdcall GrpcStopTradeStream() {
    return GRPC_NOT_IMPLEMENTED;
}

__declspec(dllexport) int __stdcall GrpcGetNextTrade(wchar_t* trade_json, int buffer_size) {
    if (trade_json && buffer_size > 0) {
        trade_json[0] = L'\0';  // Return empty string
    }
    return GRPC_NOT_IMPLEMENTED;
}

__declspec(dllexport) int __stdcall GrpcGetTradeQueueSize() {
    return 0;  // No trades in queue
}

__declspec(dllexport) int __stdcall GrpcSubmitTradeResult(const wchar_t* result_json) {
    return GRPC_NOT_IMPLEMENTED;
}

__declspec(dllexport) int __stdcall GrpcHealthCheck(const wchar_t* request_json, wchar_t* response_json, int buffer_size) {
    if (response_json && buffer_size > 0) {
        response_json[0] = L'\0';  // Return empty string
    }
    return GRPC_NOT_IMPLEMENTED;
}

__declspec(dllexport) int __stdcall GrpcNotifyHedgeClose(const wchar_t* notification_json) {
    return GRPC_NOT_IMPLEMENTED;
}

__declspec(dllexport) int __stdcall GrpcSubmitElasticUpdate(const wchar_t* update_json) {
    return GRPC_NOT_IMPLEMENTED;
}

__declspec(dllexport) int __stdcall GrpcSubmitTrailingUpdate(const wchar_t* update_json) {
    return GRPC_NOT_IMPLEMENTED;
}

__declspec(dllexport) int __stdcall GrpcGetLastErrorMessage(wchar_t* error_message, int buffer_size) {
    if (error_message && buffer_size > 0) {
        const wchar_t* msg = L"TCP connection test implementation";
        wcsncpy_s(error_message, buffer_size, msg, _TRUNCATE);
    }
    return GRPC_SUCCESS;
}

} // extern "C"
