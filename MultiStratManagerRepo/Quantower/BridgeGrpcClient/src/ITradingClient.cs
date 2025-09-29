using System;
using System.Threading;
using System.Threading.Tasks;

namespace Quantower.Bridge.Client
{
    internal interface ITradingClient : IDisposable
    {
        bool IsConnected { get; }
        Task<OperationResult> SubmitTradeAsync(string tradeJson, CancellationToken cancellationToken = default);
        Task<OperationResult> HealthCheckAsync(string source);
        Task<OperationResult> SubmitElasticUpdateAsync(string updateJson);
        Task<OperationResult> SubmitTrailingUpdateAsync(string updateJson);
        Task<OperationResult> NotifyHedgeCloseAsync(string notificationJson);
        Task<OperationResult> SubmitCloseHedgeAsync(string notificationJson);
        void StartTradingStream(Action<string>? onTradeReceived, Action<BridgeGrpcClient.StreamingState, string?>? onStreamStateChanged = null);
        void StopTradingStream();
        void LogFireAndForget(string level, string component, string message, string tradeId = "", string errorCode = "", string baseId = "", string? correlationId = null);
    }
}
