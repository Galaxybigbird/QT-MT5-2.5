using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;

namespace Quantower.MultiStrat.Infrastructure
{
    internal sealed class QuantowerEventBridge : IDisposable
    {
        private readonly object _coreInstance;
        private readonly List<(EventInfo Event, Delegate Handler)> _subscriptions;
        private readonly Func<IEnumerable<object>> _positionsProvider;

        private QuantowerEventBridge(object coreInstance, List<(EventInfo, Delegate)> subscriptions, Func<IEnumerable<object>> positionsProvider)
        {
            _coreInstance = coreInstance;
            _subscriptions = subscriptions;
            _positionsProvider = positionsProvider;
        }

        public static bool TryCreate(Action<object> onTradeAdded, Action<object>? onPositionClosed, out QuantowerEventBridge? bridge)
        {
            bridge = null;

            try
            {
                var coreType = Type.GetType("TradingPlatform.BusinessLayer.Core, TradingPlatform.BusinessLayer", throwOnError: false);
                if (coreType == null)
                {
                    return false;
                }

                var instanceProp = coreType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                var instance = instanceProp?.GetValue(null);
                if (instance == null)
                {
                    return false;
                }

                var subscriptions = new List<(EventInfo, Delegate)>();

                SubscribeIfPossible(instance, coreType, "TradeAdded", onTradeAdded, subscriptions);
                SubscribeIfPossible(instance, coreType, "TradeUpdated", onTradeAdded, subscriptions);

                if (onPositionClosed != null)
                {
                    SubscribeIfPossible(instance, coreType, "PositionRemoved", onPositionClosed, subscriptions);
                }

                var positionsProvider = BuildPositionsProvider(instance, coreType);

                bridge = new QuantowerEventBridge(instance, subscriptions, positionsProvider);
                return subscriptions.Count > 0;
            }
            catch
            {
                bridge = null;
                return false;
            }
        }

        public IEnumerable<object> SnapshotPositions() => _positionsProvider();

        public void Dispose()
        {
            foreach (var (eventInfo, handler) in _subscriptions)
            {
                try
                {
                    eventInfo.RemoveEventHandler(_coreInstance, handler);
                }
                catch
                {
                    // ignore detach failures
                }
            }
            _subscriptions.Clear();
        }

        private static void SubscribeIfPossible(object instance, Type coreType, string eventName, Action<object> callback, List<(EventInfo, Delegate)> subscriptions)
        {
            var eventInfo = coreType.GetEvent(eventName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (eventInfo == null)
            {
                return;
            }

            var handler = CreateEventHandler(eventInfo, callback);
            if (handler == null)
            {
                return;
            }

            try
            {
                eventInfo.AddEventHandler(instance, handler);
                subscriptions.Add((eventInfo, handler));
            }
            catch
            {
                // failed to subscribe; remove handler
            }
        }

        private static Delegate? CreateEventHandler(EventInfo eventInfo, Action<object> callback)
        {
            var handlerType = eventInfo.EventHandlerType;
            if (handlerType == null)
            {
                return null;
            }

            var invokeMethod = handlerType.GetMethod("Invoke");
            if (invokeMethod == null)
            {
                return null;
            }

            var parameters = invokeMethod.GetParameters();
            var parameterExpressions = new ParameterExpression[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                parameterExpressions[i] = Expression.Parameter(parameters[i].ParameterType, parameters[i].Name);
            }

            Expression payloadExpression;
            if (parameterExpressions.Length == 0)
            {
                payloadExpression = Expression.Constant(null, typeof(object));
            }
            else if (parameterExpressions.Length == 1)
            {
                payloadExpression = Expression.Convert(parameterExpressions[0], typeof(object));
            }
            else
            {
                payloadExpression = Expression.Convert(parameterExpressions[1], typeof(object));
            }

            var callbackExpression = Expression.Constant(callback);
            var body = Expression.Invoke(callbackExpression, payloadExpression);
            var lambda = Expression.Lambda(handlerType, body, parameterExpressions);
            return lambda.Compile();
        }

        private static Func<IEnumerable<object>> BuildPositionsProvider(object instance, Type coreType)
        {
            var positionsProp = coreType.GetProperty("Positions", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (positionsProp == null)
            {
                return () => Array.Empty<object>();
            }

            return () => EnumerateObjects(positionsProp.GetValue(instance));
        }

        private static IEnumerable<object> EnumerateObjects(object? source)
        {
            if (source is System.Collections.IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    if (item != null)
                    {
                        yield return item;
                    }
                }
            }
        }
    }
}
