using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Threading.Tasks;
using Trading.Proto;

namespace Quantower.Bridge.Client
{
    /// <summary>
    /// gRPC helper facade used by the Quantower add-on to talk to the Go bridge.
    /// </summary>
    public static class BridgeGrpcClient
    {
        private static ITradingClient? _client;
        private static readonly object InitLock = new();
        private static volatile bool _initialized;
        private static readonly ConcurrentDictionary<string, string> CorrelationByBaseId = new();

        private static TimeSpan _healthCheckTimeout = TimeSpan.FromSeconds(2);
        public static TimeSpan HealthCheckTimeout
        {
            get => _healthCheckTimeout;
            set => _healthCheckTimeout = value > TimeSpan.Zero ? value : TimeSpan.FromSeconds(2);
        }

        private static string GetOrCreateCorrelation(string baseId)
        {
            if (string.IsNullOrWhiteSpace(baseId))
            {
                return Guid.NewGuid().ToString("N");
            }

            return CorrelationByBaseId.GetOrAdd(baseId, _ => Guid.NewGuid().ToString("N"));
        }

        public static void ReleaseCorrelation(string baseId)
        {
            if (!string.IsNullOrWhiteSpace(baseId))
            {
                CorrelationByBaseId.TryRemove(baseId, out _);
            }
        }

        public static string LastError { get; private set; } = string.Empty;

        public static async Task<bool> Initialize(string serverAddress, string source = "qt", string? component = null)
        {
            var address = NormalizeAddress(serverAddress);
            var comp = string.IsNullOrWhiteSpace(component)
                ? (!string.IsNullOrWhiteSpace(source) ? $"{source.ToLowerInvariant()}_addon" : "addon")
                : component;

            ITradingClient? newClient = null;

            try
            {
                newClient = new TradingClient(address, source, comp);
                var healthTask = newClient.HealthCheckAsync("addon");
                var completed = await Task.WhenAny(healthTask, Task.Delay(_healthCheckTimeout)).ConfigureAwait(false);

                if (completed != healthTask)
                {
                    LastError = "HealthCheck timeout";
                    return false;
                }

                var health = await healthTask.ConfigureAwait(false);
                if (!health.Success)
                {
                    LastError = string.IsNullOrWhiteSpace(health.ErrorMessage)
                        ? "HealthCheck failed"
                        : health.ErrorMessage;
                    return false;
                }

                lock (InitLock)
                {
                    ShutdownInternal();
                    _client = newClient;
                    _initialized = true;
                    // Transfer ownership safely so finally won't dispose the assigned instance
                    newClient = null;
                }

                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
            finally
            {
                newClient?.Dispose();
            }
        }

        public static async Task<bool> SubmitTradeAsync(string tradeJson)
        {
            if (!TryGetClient(out var client))
            {
                return false;
            }

            try
            {
                var result = await client!.SubmitTradeAsync(tradeJson).ConfigureAwait(false);
                if (!result.Success)
                {
                    LastError = string.IsNullOrEmpty(result.ErrorMessage) ? "SubmitTrade failed" : result.ErrorMessage;
                }
                return result.Success;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }

        public static async Task<OperationResult> HealthCheckAsync(string source)
        {
            if (!TryGetClient(out var client))
            {
                return OperationResult.Failure("gRPC client not initialized");
            }

            try
            {
                var result = await client!.HealthCheckAsync(source).ConfigureAwait(false);
                if (!result.Success && !string.IsNullOrEmpty(result.ErrorMessage))
                {
                    LastError = result.ErrorMessage;
                }
                return result;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return OperationResult.Failure(ex.Message);
            }
        }

        public static async Task<bool> SubmitElasticUpdateAsync(string updateJson)
        {
            if (!TryGetClient(out var client))
            {
                return false;
            }

            try
            {
                var result = await client!.SubmitElasticUpdateAsync(updateJson).ConfigureAwait(false);
                if (!result.Success)
                {
                    LastError = string.IsNullOrEmpty(result.ErrorMessage) ? "SubmitElasticUpdate failed" : result.ErrorMessage;
                }
                return result.Success;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }

        public static async Task<bool> SubmitTrailingUpdateAsync(string updateJson)
        {
            if (!TryGetClient(out var client))
            {
                return false;
            }

            try
            {
                var result = await client!.SubmitTrailingUpdateAsync(updateJson).ConfigureAwait(false);
                if (!result.Success)
                {
                    LastError = string.IsNullOrEmpty(result.ErrorMessage) ? "SubmitTrailingUpdate failed" : result.ErrorMessage;
                }
                return result.Success;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }

        public static async Task<bool> NotifyHedgeCloseAsync(string notificationJson)
        {
            if (!TryGetClient(out var client))
            {
                return false;
            }

            try
            {
                var result = await client!.NotifyHedgeCloseAsync(notificationJson).ConfigureAwait(false);
                if (!result.Success)
                {
                    LastError = string.IsNullOrEmpty(result.ErrorMessage) ? "NotifyHedgeClose failed" : result.ErrorMessage;
                }
                return result.Success;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }

        public static async Task<bool> CloseHedgeAsync(string notificationJson)
        {
            if (!TryGetClient(out var client))
            {
                return false;
            }

            try
            {
                var result = await client!.SubmitCloseHedgeAsync(notificationJson).ConfigureAwait(false);
                if (!result.Success)
                {
                    LastError = string.IsNullOrEmpty(result.ErrorMessage) ? "CloseHedge failed" : result.ErrorMessage;
                }
                return result.Success;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }

        public static void StartTradingStream(Action<string>? onTradeReceived)
        {
            if (!TryGetClient(out var client, setError: false))
            {
                return;
            }

            client!.StartTradingStream(payload =>
            {
                if (payload == null)
                {
                    return;
                }

                onTradeReceived?.Invoke(payload);
            });
        }

        public static void StopTradingStream()
        {
            if (TryGetClient(out var client, setError: false))
            {
                client!.StopTradingStream();
            }
        }

        public static void Shutdown()
        {
            lock (InitLock)
            {
                ShutdownInternal();
            }
        }

        public static void Log(string level, string component, string message, string tradeId = "", string errorCode = "", string baseId = "")
        {
            if (!TryGetClient(out var client, setError: false))
            {
                return;
            }

            var correlation = GetOrCreateCorrelation(baseId);
            client!.LogFireAndForget(level, component, message, tradeId, errorCode, baseId, correlation);
        }

        public static void LogDebug(string component, string message, string tradeId = "", string baseId = "") => Log("DEBUG", component, message, tradeId, "", baseId);
        public static void LogInfo(string component, string message, string tradeId = "", string baseId = "") => Log("INFO", component, message, tradeId, "", baseId);
        public static void LogWarn(string component, string message, string tradeId = "", string baseId = "") => Log("WARN", component, message, tradeId, "", baseId);
        public static void LogError(string component, string message, string tradeId = "", string errorCode = "", string baseId = "") => Log("ERROR", component, message, tradeId, errorCode, baseId);

        private static bool TryGetClient(out ITradingClient? client, bool setError = true)
        {
            lock (InitLock)
            {
                if (_initialized && _client != null)
                {
                    client = _client;
                    return true;
                }
            }

            if (setError)
            {
                LastError = "gRPC client not initialized";
            }

            client = null;
            return false;
        }

        private static void ShutdownInternal()
        {
            if (_client != null)
            {
                try
                {
                    _client.Dispose();
                }
                catch
                {
                    // ignore
                }
                finally
                {
                    _client = null;
                }
            }

            _initialized = false;
        }

        private static string NormalizeAddress(string serverAddress)
        {
            if (string.IsNullOrWhiteSpace(serverAddress))
            {
                throw new ArgumentException("Server address cannot be empty", nameof(serverAddress));
            }

            var input = serverAddress.Trim();

            // Try absolute first
            if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
            {
                // Prepend default scheme and retry
                if (!Uri.TryCreate($"http://{input}", UriKind.Absolute, out uri))
                {
                    throw new ArgumentException($"Invalid server address: {serverAddress}", nameof(serverAddress));
                }
            }

            var scheme = uri.Scheme.ToLowerInvariant();
            if (scheme != "http" && scheme != "https")
            {
                throw new ArgumentException($"Unsupported URI scheme '{uri.Scheme}'. Use http or https.", nameof(serverAddress));
            }

            var host = uri.Host.ToLowerInvariant();
            var normalizedHost = host.Contains(':') ? $"[{host}]" : host;
            int port = uri.IsDefaultPort ? -1 : uri.Port;

            // Normalize default ports
            if ((scheme == "http" && port == 80) || (scheme == "https" && port == 443))
            {
                port = -1;
            }

            return port > 0 ? $"{scheme}://{normalizedHost}:{port}" : $"{scheme}://{normalizedHost}";
        }
    }

    public class OperationResult
    {
        public bool Success { get; set; }
        public string ResponseJson { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;

        public static OperationResult Ok(string? payload = null) => new() { Success = true, ResponseJson = payload ?? string.Empty };
        public static OperationResult Failure(string? error = null) => new() { Success = false, ErrorMessage = error ?? string.Empty };
    }
}
