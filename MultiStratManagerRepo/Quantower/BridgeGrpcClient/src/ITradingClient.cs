using System;
using System.Threading.Tasks;

namespace Quantower.Bridge.Client
{
    internal interface ITradingClient : IDisposable
    {
        bool IsConnected { get; }
        Task<OperationResult> SubmitTradeAsync(string tradeJson);
        Task<OperationResult> HealthCheckAsync(string source);
        Task<OperationResult> SubmitElasticUpdateAsync(string updateJson);
        Task<OperationResult> SubmitTrailingUpdateAsync(string updateJson);
        Task<OperationResult> NotifyHedgeCloseAsync(string notificationJson);
        Task<OperationResult> SubmitCloseHedgeAsync(string notificationJson);
        void StartTradingStream(Action<string>? onTradeReceived);
        void StopTradingStream();
        void LogFireAndForget(string level, string component, string message, string tradeId = "", string errorCode = "", string baseId = "", string? correlationId = null);
    }
}
