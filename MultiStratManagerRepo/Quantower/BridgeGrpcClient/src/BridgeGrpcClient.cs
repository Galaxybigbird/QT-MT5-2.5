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
        private static readonly ConcurrentDictionary<string, string> CorrelationByBaseId = new();

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

        private static bool _initialized;
        public static string LastError { get; private set; } = string.Empty;

        public static async Task<bool> Initialize(string serverAddress, string source = "qt", string? component = null)
        {
            Shutdown();

            try
            {
                var address = NormalizeAddress(serverAddress);
                var comp = string.IsNullOrWhiteSpace(component)
                    ? (!string.IsNullOrWhiteSpace(source) ? $"{source.ToLowerInvariant()}_addon" : "addon")
                    : component;

                _client = new TradingClient(address, source, comp);
                var healthTask = _client.HealthCheckAsync("addon");
                var completed = await Task.WhenAny(healthTask, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);

                if (completed != healthTask)
                {
                    LastError = "HealthCheck timeout";
                    _initialized = false;
                    return false;
                }

                var health = await healthTask.ConfigureAwait(false);
                if (!health.Success)
                {
                    LastError = string.IsNullOrWhiteSpace(health.ErrorMessage)
                        ? "HealthCheck failed"
                        : health.ErrorMessage;
                    _initialized = false;
                    return false;
                }

                _initialized = true;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _initialized = false;
                return false;
            }
        }

        public static async Task<bool> SubmitTradeAsync(string tradeJson)
        {
            if (!EnsureInitialized())
            {
                return false;
            }

            try
            {
                var result = await _client!.SubmitTradeAsync(tradeJson).ConfigureAwait(false);
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
            if (!EnsureInitialized())
            {
                return OperationResult.Failure("gRPC client not initialized");
            }

            try
            {
                var result = await _client!.HealthCheckAsync(source).ConfigureAwait(false);
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
            if (!EnsureInitialized())
            {
                return false;
            }

            try
            {
                var result = await _client!.SubmitElasticUpdateAsync(updateJson).ConfigureAwait(false);
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
            if (!EnsureInitialized())
            {
                return false;
            }

            try
            {
                var result = await _client!.SubmitTrailingUpdateAsync(updateJson).ConfigureAwait(false);
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
            if (!EnsureInitialized())
            {
                return false;
            }

            try
            {
                var result = await _client!.NotifyHedgeCloseAsync(notificationJson).ConfigureAwait(false);
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
            if (!EnsureInitialized())
            {
                return false;
            }

            try
            {
                var result = await _client!.CloseHedgeAsync(notificationJson).ConfigureAwait(false);
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
            if (!EnsureInitialized())
            {
                return;
            }

            _client!.StartTradingStream(payload =>
            {
                if (payload == null)
                {
                    return;
                }

                onTradeReceived?.Invoke(payload);
            });
        }

        public static void StopTradingStream() => _client?.StopTradingStream();

        public static void Shutdown()
        {
            try
            {
                _client?.Dispose();
            }
            catch
            {
                // ignore
            }
            finally
            {
                _client = null;
                _initialized = false;
            }
        }

        public static void Log(string level, string component, string message, string tradeId = "", string errorCode = "", string baseId = "")
        {
            if (!_initialized || _client is not TradingClient impl)
            {
                return;
            }

            var correlation = GetOrCreateCorrelation(baseId);
            impl.LogFireAndForget(level, component, message, tradeId, errorCode, baseId, correlation);
        }

        public static void LogDebug(string component, string message, string tradeId = "", string baseId = "") => Log("DEBUG", component, message, tradeId, "", baseId);
        public static void LogInfo(string component, string message, string tradeId = "", string baseId = "") => Log("INFO", component, message, tradeId, "", baseId);
        public static void LogWarn(string component, string message, string tradeId = "", string baseId = "") => Log("WARN", component, message, tradeId, "", baseId);
        public static void LogError(string component, string message, string tradeId = "", string errorCode = "", string baseId = "") => Log("ERROR", component, message, tradeId, errorCode, baseId);

        private static bool EnsureInitialized()
        {
            if (_initialized && _client != null)
            {
                return true;
            }

            LastError = "gRPC client not initialized";
            return false;
        }

        private static string NormalizeAddress(string serverAddress)
        {
            if (string.IsNullOrWhiteSpace(serverAddress))
            {
                throw new ArgumentException("Server address cannot be empty", nameof(serverAddress));
            }

            serverAddress = serverAddress.Trim();
            if (!serverAddress.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !serverAddress.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                serverAddress = $"http://{serverAddress}";
            }

            return serverAddress;
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
