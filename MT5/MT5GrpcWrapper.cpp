#include <windows.h>
#include <mscoree.h>
#include <metahost.h>
#include <comdef.h>
#include <string>
#include <iostream>
#include <fstream>
#include <sstream>

#pragma comment(lib, "mscoree.lib")

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
    __declspec(dllexport) int __stdcall GrpcLog(const wchar_t* log_json);
}

// Global variables for .NET runtime
static ICLRMetaHost* g_pMetaHost = nullptr;
static ICLRRuntimeInfo* g_pRuntimeInfo = nullptr;
static ICLRRuntimeHost* g_pRuntimeHost = nullptr;
static HMODULE g_hMscorlib = nullptr;
static HMODULE g_hThisModule = nullptr;
static bool g_runtimeStarted = false;
static DWORD g_lastHr = 0;

// Wrapper-specific error codes (avoid conflict with Windows ERROR_* macros)
const int WRAP_SUCCESS = 0;
const int WRAP_INIT_FAILED = -1;
const int WRAP_NOT_INITIALIZED = -2;
const int WRAP_CONNECTION_FAILED = -3;
const int WRAP_STREAM_FAILED = -4;
const int WRAP_INVALID_PARAMS = -5;
const int WRAP_TIMEOUT = -6;
const int WRAP_SERIALIZATION = -7;
const int WRAP_CLEANUP_FAILED = -8;

// Utility function to convert wchar_t* to std::string
std::string WStringToString(const wchar_t* wstr) {
    if (!wstr) return "";
    int size_needed = WideCharToMultiByte(CP_UTF8, 0, wstr, -1, NULL, 0, NULL, NULL);
    std::string result(size_needed - 1, 0);
    WideCharToMultiByte(CP_UTF8, 0, wstr, -1, &result[0], size_needed - 1, NULL, NULL);
    return result;
}

// Utility function to convert std::string to wchar_t*
void StringToWString(const std::string& str, wchar_t* buffer, int buffer_size) {
    if (!buffer || buffer_size <= 0) return;
    int chars_written = MultiByteToWideChar(CP_UTF8, 0, str.c_str(), -1, buffer, buffer_size - 1);
    if (chars_written > 0) {
        buffer[chars_written] = L'\0';
    } else {
        buffer[0] = L'\0';
    }
}

// Initialize .NET runtime
bool InitializeDotNetRuntime() {
    if (g_runtimeStarted) return true;

    HRESULT hr = CLRCreateInstance(CLSID_CLRMetaHost, IID_ICLRMetaHost, (LPVOID*)&g_pMetaHost);
    if (FAILED(hr)) { g_lastHr = hr; return false; }

    // Use .NET Framework 4.8
    hr = g_pMetaHost->GetRuntime(L"v4.0.30319", IID_ICLRRuntimeInfo, (LPVOID*)&g_pRuntimeInfo);
    if (FAILED(hr)) { g_lastHr = hr; return false; }

    hr = g_pRuntimeInfo->GetInterface(CLSID_CLRRuntimeHost, IID_ICLRRuntimeHost, (LPVOID*)&g_pRuntimeHost);
    if (FAILED(hr)) { g_lastHr = hr; return false; }

    hr = g_pRuntimeHost->Start();
    if (FAILED(hr)) { g_lastHr = hr; return false; }

    g_runtimeStarted = true;
    return true;
}

// Cleanup .NET runtime
void CleanupDotNetRuntime() {
    if (g_pRuntimeHost) {
        g_pRuntimeHost->Stop();
        g_pRuntimeHost->Release();
        g_pRuntimeHost = nullptr;
    }
    
    if (g_pRuntimeInfo) {
        g_pRuntimeInfo->Release();
        g_pRuntimeInfo = nullptr;
    }
    
    if (g_pMetaHost) {
        g_pMetaHost->Release();
        g_pMetaHost = nullptr;
    }
    
    g_runtimeStarted = false;
}

// Resolve absolute path to MT5GrpcManaged.dll located alongside this wrapper DLL
static std::string GetManagedAssemblyPath() {
    wchar_t pathW[MAX_PATH];
    HMODULE mod = g_hThisModule;
    if (!mod) {
        // Fallback: try to get by known name
        mod = GetModuleHandleW(L"MT5GrpcWrapper.dll");
    }
    if (!mod) {
        // As a last resort, use current process module
        mod = GetModuleHandleW(NULL);
    }
    DWORD len = GetModuleFileNameW(mod, pathW, MAX_PATH);
    if (len == 0 || len >= MAX_PATH) {
        // Fallback to simple filename; CLR may still resolve via probing
        return std::string("MT5GrpcManaged.dll");
    }
    std::wstring full(pathW);
    size_t pos = full.find_last_of(L"\\/");
    std::wstring dir = (pos == std::wstring::npos) ? full : full.substr(0, pos);

    // Prefer MT5GrpcManaged.dll but fall back to MT5GrpcClient.dll if not found
    std::wstring managed1 = dir + L"\\MT5GrpcManaged.dll";
    std::wstring managed2 = dir + L"\\MT5GrpcClient.dll";
    DWORD attr1 = GetFileAttributesW(managed1.c_str());
    if (attr1 != INVALID_FILE_ATTRIBUTES && !(attr1 & FILE_ATTRIBUTE_DIRECTORY)) {
        return std::string(managed1.begin(), managed1.end());
    }
    DWORD attr2 = GetFileAttributesW(managed2.c_str());
    if (attr2 != INVALID_FILE_ATTRIBUTES && !(attr2 & FILE_ATTRIBUTE_DIRECTORY)) {
        return std::string(managed2.begin(), managed2.end());
    }
    // Default to MT5GrpcManaged.dll name
    return std::string(managed1.begin(), managed1.end());
}

// Call managed method helper
template<typename T>
T CallManagedMethod(const std::string& assemblyPath, const std::string& typeName, 
                   const std::string& methodName, const std::string& args = "") {
    if (!g_runtimeStarted) {
        if (!InitializeDotNetRuntime()) {
            return static_cast<T>(WRAP_NOT_INITIALIZED);
        }
    }

    DWORD returnValue = 0;
    std::wstring wAssemblyPath(assemblyPath.begin(), assemblyPath.end());
    std::wstring wTypeName(typeName.begin(), typeName.end());
    std::wstring wMethodName(methodName.begin(), methodName.end());
    std::wstring wArgs(args.begin(), args.end());

    HRESULT hr = g_pRuntimeHost->ExecuteInDefaultAppDomain(
        wAssemblyPath.c_str(),
        wTypeName.c_str(),
        wMethodName.c_str(),
        wArgs.c_str(),
        &returnValue
    );

    if (FAILED(hr)) {
        g_lastHr = hr;
        // Write a minimal diagnostic file to %TEMP%
        wchar_t tempPath[MAX_PATH];
        if (GetTempPathW(MAX_PATH, tempPath) > 0) {
            std::wstring wfile = std::wstring(tempPath) + L"mt5_grpc_wrapper.txt";
            HANDLE hFile = CreateFileW(wfile.c_str(), GENERIC_WRITE, FILE_SHARE_READ, NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
            if (hFile != INVALID_HANDLE_VALUE) {
                SetFilePointer(hFile, 0, NULL, FILE_END);
                SYSTEMTIME st; GetLocalTime(&st);
                char line[1024];
                int n = snprintf(line, sizeof(line),
                    "[%04d-%02d-%02d %02d:%02d:%02d] ExecuteInDefaultAppDomain failed hr=0x%08lX asm=%ls type=%ls method=%ls\r\n",
                    st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond,
                    (unsigned long)hr, wAssemblyPath.c_str(), wTypeName.c_str(), wMethodName.c_str());
                DWORD written = 0;
                if (n > 0) WriteFile(hFile, line, (DWORD)n, &written, NULL);
                CloseHandle(hFile);
            }
        }
        return static_cast<T>(WRAP_CONNECTION_FAILED);
    }

    return static_cast<T>(returnValue);
}

// DLL Entry Point
BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID lpReserved) {
    switch (ul_reason_for_call) {
    case DLL_PROCESS_ATTACH:
    g_hThisModule = hModule;
        break;
    case DLL_THREAD_ATTACH:
        break;
    case DLL_THREAD_DETACH:
        break;
    case DLL_PROCESS_DETACH:
        CleanupDotNetRuntime();
        break;
    }
    return TRUE;
}

// Exported functions implementation
extern "C" {

__declspec(dllexport) int __stdcall TestFunction() {
    return 42;  // Simple test function
}

__declspec(dllexport) int __stdcall GrpcInitialize(const wchar_t* server_address, int port) {
    try {
        if (!InitializeDotNetRuntime()) {
            return WRAP_INIT_FAILED;
        }

    std::string assemblyPath = GetManagedAssemblyPath();
    std::string typeName = "MT5GrpcClient.GrpcClientWrapper";
        std::string methodName = "GrpcInitialize";
        
        std::string serverAddr = WStringToString(server_address);
        std::string args = serverAddr + "," + std::to_string(port);

        return CallManagedMethod<int>(assemblyPath, typeName, methodName, args);
    }
    catch (...) {
    return WRAP_INIT_FAILED;
    }
}

__declspec(dllexport) int __stdcall GrpcShutdown() {
    try {
    std::string assemblyPath = GetManagedAssemblyPath();
    std::string typeName = "MT5GrpcClient.GrpcClientWrapper";
        std::string methodName = "GrpcShutdown";

        int result = CallManagedMethod<int>(assemblyPath, typeName, methodName);
        CleanupDotNetRuntime();
        return result;
    }
    catch (...) {
    return WRAP_CLEANUP_FAILED;
    }
}

__declspec(dllexport) int __stdcall GrpcIsConnected() {
    try {
    std::string assemblyPath = GetManagedAssemblyPath();
    std::string typeName = "MT5GrpcClient.GrpcClientWrapper";
        std::string methodName = "GrpcIsConnected";

        return CallManagedMethod<int>(assemblyPath, typeName, methodName);
    }
    catch (...) {
    return WRAP_CONNECTION_FAILED;
    }
}

__declspec(dllexport) int __stdcall GrpcReconnect() {
    try {
    std::string assemblyPath = GetManagedAssemblyPath();
    std::string typeName = "MT5GrpcClient.GrpcClientWrapper";
        std::string methodName = "GrpcReconnect";

        return CallManagedMethod<int>(assemblyPath, typeName, methodName);
    }
    catch (...) {
    return WRAP_CONNECTION_FAILED;
    }
}

__declspec(dllexport) int __stdcall GrpcStartTradeStream() {
    try {
    std::string assemblyPath = GetManagedAssemblyPath();
    std::string typeName = "MT5GrpcClient.GrpcClientWrapper";
        std::string methodName = "GrpcStartTradeStream";

        return CallManagedMethod<int>(assemblyPath, typeName, methodName);
    }
    catch (...) {
    return WRAP_STREAM_FAILED;
    }
}

__declspec(dllexport) int __stdcall GrpcStopTradeStream() {
    try {
    std::string assemblyPath = GetManagedAssemblyPath();
        std::string typeName = "MT5GrpcClient.GrpcClientWrapper";
        std::string methodName = "GrpcStopTradeStream";

        return CallManagedMethod<int>(assemblyPath, typeName, methodName);
    }
    catch (...) {
    return WRAP_STREAM_FAILED;
    }
}

__declspec(dllexport) int __stdcall GrpcLog(const wchar_t* log_json) {
    try {
        if (!InitializeDotNetRuntime()) {
            return WRAP_NOT_INITIALIZED;
        }
    std::string assemblyPath = GetManagedAssemblyPath();
        std::string typeName = "MT5GrpcClient.GrpcClientWrapper";
        std::string methodName = "GrpcLog";
        std::string args = WStringToString(log_json);
        return CallManagedMethod<int>(assemblyPath, typeName, methodName, args);
    }
    catch (...) {
    return WRAP_SERIALIZATION;
    }
}

__declspec(dllexport) int __stdcall GrpcGetNextTrade(wchar_t* trade_json, int buffer_size) {
    try {
    std::string assemblyPath = GetManagedAssemblyPath();
        std::string typeName = "MT5GrpcClient.GrpcClientWrapper";
        
        // Initialize output buffer
        if (trade_json && buffer_size > 0) {
            trade_json[0] = L'\0';
        }
        
        // Call GrpcGetNextTrade to check for trades and write to temp file
        std::string methodName = "GrpcGetNextTrade";
        std::string args = std::to_string(buffer_size);
        int result = CallManagedMethod<int>(assemblyPath, typeName, methodName, args);
        
    if (result == WRAP_SUCCESS && trade_json && buffer_size > 0) {
            // Read trade JSON from temp file
            char tempPath[MAX_PATH];
            GetTempPathA(MAX_PATH, tempPath);
            std::string tempFile = std::string(tempPath) + "mt5_grpc_trade.json";
            
            std::ifstream file(tempFile);
            if (file.is_open()) {
                std::string tradeJsonStr((std::istreambuf_iterator<char>(file)),
                                       std::istreambuf_iterator<char>());
                file.close();
                
                if (!tradeJsonStr.empty() && tradeJsonStr.length() < buffer_size - 1) {
                    // Convert UTF-8 string to wide string
                    int wideLength = MultiByteToWideChar(CP_UTF8, 0, tradeJsonStr.c_str(), -1, nullptr, 0);
                    if (wideLength > 0 && wideLength <= buffer_size) {
                        MultiByteToWideChar(CP_UTF8, 0, tradeJsonStr.c_str(), -1, trade_json, wideLength);
                    }
                }
            }
        }
        
        return result;
    }
    catch (...) {
    return WRAP_SERIALIZATION;
    }
}

__declspec(dllexport) int __stdcall GrpcGetTradeQueueSize() {
    try {
    std::string assemblyPath = GetManagedAssemblyPath();
        std::string typeName = "MT5GrpcClient.GrpcClientWrapper";
        std::string methodName = "GrpcGetTradeQueueSize";

        return CallManagedMethod<int>(assemblyPath, typeName, methodName);
    }
    catch (...) {
    return WRAP_CONNECTION_FAILED;
    }
}

__declspec(dllexport) int __stdcall GrpcSubmitTradeResult(const wchar_t* result_json) {
    try {
    std::string assemblyPath = GetManagedAssemblyPath();
        std::string typeName = "MT5GrpcClient.GrpcClientWrapper";
        std::string methodName = "GrpcSubmitTradeResult";
        std::string args = WStringToString(result_json);

        return CallManagedMethod<int>(assemblyPath, typeName, methodName, args);
    }
    catch (...) {
    return WRAP_SERIALIZATION;
    }
}

__declspec(dllexport) int __stdcall GrpcHealthCheck(const wchar_t* request_json, wchar_t* response_json, int buffer_size) {
    try {
    std::string assemblyPath = GetManagedAssemblyPath();
        std::string typeName = "MT5GrpcClient.GrpcClientWrapper";
        std::string methodName = "GrpcHealthCheck";
        std::string args = WStringToString(request_json) + "," + std::to_string(buffer_size);

        if (response_json && buffer_size > 0) {
            response_json[0] = L'\0';  // Initialize to empty string
        }

        // Call managed method (overload taking a single string arg)
        int rc = CallManagedMethod<int>(assemblyPath, typeName, methodName, args);

        // Try to read the response JSON written by managed into temp file and copy into buffer
        try {
            char tempPath[MAX_PATH];
            if (GetTempPathA(MAX_PATH, tempPath) > 0) {
                std::string tempFile = std::string(tempPath) + "mt5_grpc_health.json";
                std::ifstream file(tempFile);
                if (file.is_open()) {
                    std::string resp((std::istreambuf_iterator<char>(file)), std::istreambuf_iterator<char>());
                    file.close();
                    if (!resp.empty() && response_json && buffer_size > 0) {
                        // Convert UTF-8 to wide and copy
                        int wideLength = MultiByteToWideChar(CP_UTF8, 0, resp.c_str(), -1, nullptr, 0);
                        if (wideLength > 0) {
                            if (wideLength > buffer_size) wideLength = buffer_size;
                            MultiByteToWideChar(CP_UTF8, 0, resp.c_str(), -1, response_json, wideLength);
                        }
                    }
                }
            }
        } catch(...) { }

        return rc;
    }
    catch (...) {
    return WRAP_CONNECTION_FAILED;
    }
}

__declspec(dllexport) int __stdcall GrpcNotifyHedgeClose(const wchar_t* notification_json) {
    try {
    std::string assemblyPath = GetManagedAssemblyPath();
        std::string typeName = "MT5GrpcClient.GrpcClientWrapper";
        std::string methodName = "GrpcNotifyHedgeClose";
        std::string args = WStringToString(notification_json);

        return CallManagedMethod<int>(assemblyPath, typeName, methodName, args);
    }
    catch (...) {
    return WRAP_SERIALIZATION;
    }
}

__declspec(dllexport) int __stdcall GrpcSubmitElasticUpdate(const wchar_t* update_json) {
    try {
    std::string assemblyPath = GetManagedAssemblyPath();
        std::string typeName = "MT5GrpcClient.GrpcClientWrapper";
        std::string methodName = "GrpcSubmitElasticUpdate";
        std::string args = WStringToString(update_json);

        return CallManagedMethod<int>(assemblyPath, typeName, methodName, args);
    }
    catch (...) {
    return WRAP_SERIALIZATION;
    }
}

__declspec(dllexport) int __stdcall GrpcSubmitTrailingUpdate(const wchar_t* update_json) {
    try {
    std::string assemblyPath = GetManagedAssemblyPath();
        std::string typeName = "MT5GrpcClient.GrpcClientWrapper";
        std::string methodName = "GrpcSubmitTrailingUpdate";
        std::string args = WStringToString(update_json);

        return CallManagedMethod<int>(assemblyPath, typeName, methodName, args);
    }
    catch (...) {
    return WRAP_SERIALIZATION;
    }
}

} // extern "C"