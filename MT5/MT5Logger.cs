using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Grpc.Core;
using Trading.Proto;

namespace MT5GrpcClient
{
    // Minimal logging-only wrapper to be invoked from the native MT5GrpcWrapper.dll
    // Avoids static fields of gRPC types to prevent type initialization failures in MT5.
    public static class GrpcClientWrapper
    {
        private const int ERROR_SUCCESS = 0;
        private const int ERROR_NOT_INITIALIZED = -2;
        private const int ERROR_CONNECTION_FAILED = -3;
        private const int ERROR_SERIALIZATION = -7;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetDllDirectory(string lpPathName);

        private static bool s_nativeInit;

        private static void EnsureNativeSearchPath()
        {
            if (s_nativeInit) return;
            s_nativeInit = true;
            try
            {
                var asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (!string.IsNullOrEmpty(asmDir))
                {
                    // Help the loader find grpc_csharp_ext.x64.dll next to this managed DLL
                    SetDllDirectory(asmDir);
                    var existing = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                    if (existing.IndexOf(asmDir, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        Environment.SetEnvironmentVariable("PATH", asmDir + ";" + existing);
                    }
                }

                // Also add Terminal root, where we also copy native deps
                var terminalRoot = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (!string.IsNullOrEmpty(terminalRoot))
                {
                    var mqRoot = Path.Combine(terminalRoot, "MetaQuotes", "Terminal");
                    if (Directory.Exists(mqRoot))
                    {
                        // Best-effort: pick the first child directory (actual hash folder)
                        var dirs = Directory.GetDirectories(mqRoot);
                        var firstHash = dirs != null && dirs.Length > 0 ? dirs[0] : null;
                        if (!string.IsNullOrEmpty(firstHash) && Directory.Exists(firstHash))
                        {
                            var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                            if (pathVar.IndexOf(firstHash, StringComparison.OrdinalIgnoreCase) < 0)
                            {
                                Environment.SetEnvironmentVariable("PATH", firstHash + ";" + pathVar);
                            }
                        }
                    }
                }

                // Minimal breadcrumb for troubleshooting
                try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "mt5_grpc_managed.txt"),
                    $"[{DateTime.Now:O}] Native path set: asmDir={asmDir}\n"); } catch { }
            }
            catch { /* ignore path setup errors */ }
        }

    public static int GrpcLog(string logJson)
        {
            try
            {
                EnsureNativeSearchPath();
                // Determine bridge address from env or defaults
                string host = Environment.GetEnvironmentVariable("BRIDGE_GRPC_HOST") ?? "127.0.0.1";
                string portStr = Environment.GetEnvironmentVariable("BRIDGE_GRPC_PORT") ?? "50051";
                if (!int.TryParse(portStr, out var port) || port <= 0)
                    port = 50051;

                var target = $"{host}:{port}"; // Grpc.Core expects host:port

                // Build LogEvent from JSON
                var data = JsonSerializer.Deserialize<JsonElement>(logJson);
                var evt = new LogEvent
                {
                    TimestampNs = data.TryGetProperty("timestamp_ns", out var ts) && ts.ValueKind == JsonValueKind.Number ? ts.GetInt64() : 0,
                    Source = data.TryGetProperty("source", out var src) ? (src.GetString() ?? "") : "mt5",
                    Level = data.TryGetProperty("level", out var lvl) ? (lvl.GetString() ?? "INFO") : "INFO",
                    Component = data.TryGetProperty("component", out var comp) ? (comp.GetString() ?? "EA") : "EA",
                    Message = data.TryGetProperty("message", out var msg) ? (msg.GetString() ?? "") : "",
                    BaseId = data.TryGetProperty("base_id", out var bid) ? (bid.GetString() ?? "") : "",
                    TradeId = data.TryGetProperty("trade_id", out var tid) ? (tid.GetString() ?? "") : "",
                    NtOrderId = data.TryGetProperty("nt_order_id", out var nid) ? (nid.GetString() ?? "") : "",
                    Mt5Ticket = data.TryGetProperty("mt5_ticket", out var tk) && tk.ValueKind == JsonValueKind.Number ? tk.GetUInt64() : 0,
                    QueueSize = data.TryGetProperty("queue_size", out var qs) && qs.ValueKind == JsonValueKind.Number ? qs.GetInt32() : 0,
                    NetPosition = data.TryGetProperty("net_position", out var np) && np.ValueKind == JsonValueKind.Number ? np.GetInt32() : 0,
                    HedgeSize = data.TryGetProperty("hedge_size", out var hs) && hs.ValueKind == JsonValueKind.Number ? hs.GetDouble() : 0,
                    ErrorCode = data.TryGetProperty("error_code", out var ec) ? (ec.GetString() ?? "") : "",
                    Stack = data.TryGetProperty("stack", out var st) ? (st.GetString() ?? "") : "",
                    SchemaVersion = data.TryGetProperty("schema_version", out var sv) ? (sv.GetString() ?? "mt5-1") : "mt5-1",
                    CorrelationId = data.TryGetProperty("correlation_id", out var cid) ? (cid.GetString() ?? "") : ""
                };

                if (data.TryGetProperty("tags", out var tagsElem) && tagsElem.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in tagsElem.EnumerateObject())
                    {
                        evt.Tags[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                            ? (prop.Value.GetString() ?? "")
                            : prop.Value.ToString();
                    }
                }

                // Use Grpc.Core synchronously for maximum compatibility with .NET Framework in MT5
                var channelOptions = new[]
                {
                    new ChannelOption(ChannelOptions.MaxReceiveMessageLength, 1024 * 1024),
                    new ChannelOption(ChannelOptions.MaxSendMessageLength, 1024 * 1024)
                };

                var channel = new Channel(target, ChannelCredentials.Insecure, channelOptions);
                try
                {
                    var client = new LoggingService.LoggingServiceClient(channel);
                    var ack = client.Log(evt);
                    return ack.Accepted > 0 ? ERROR_SUCCESS : ERROR_CONNECTION_FAILED;
                }
                finally
                {
                    try { channel.ShutdownAsync().Wait(500); } catch { /* ignore */ }
                }
            }
            catch (JsonException)
            {
                return ERROR_SERIALIZATION;
            }
            catch (Exception ex)
            {
                try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "mt5_grpc_managed.txt"),
                    $"[{DateTime.Now:O}] Exception: {ex}\n"); } catch { }
                return ERROR_CONNECTION_FAILED;
            }
        }
    }
}
