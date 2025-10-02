using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingPlatform.BusinessLayer;

namespace Quantower.MultiStrat.Infrastructure
{
    internal static class QuantowerTradeMapper
    {
        public static Action<string, Exception>? LogError { get; set; }

        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        /// <summary>
        /// Computes a composite baseId for a position using Position.Id + OpenTime.Ticks.
        /// This ensures unique baseIds even when Quantower reuses Position.Id across different positions.
        /// </summary>
        public static string ComputeBaseId(Position position)
        {
            if (position == null)
            {
                return string.Empty;
            }

            var positionId = SafeString(position.Id);
            if (string.IsNullOrWhiteSpace(positionId))
            {
                return string.Empty;
            }

            var openTimeTicks = position.OpenTime != default
                ? position.OpenTime.Ticks
                : DateTime.UtcNow.Ticks;

            return $"{positionId}_{openTimeTicks}";
        }

        private static void ReportError(string message, Exception ex)
        {
            if (LogError != null)
            {
                LogError.Invoke(message, ex);
                return;
            }

            Console.Error.WriteLine($"{message}\n{ex}");
        }

        public static bool TryBuildTradeEnvelope(Trade trade, out string json, out string? tradeId)
        {
            json = string.Empty;
            tradeId = null;

            if (trade == null)
            {
                return false;
            }

            try
            {
                var payload = new Dictionary<string, object?>
                {
                    ["origin_platform"] = "quantower"
                };

                var qtTradeId = SafeString(trade.Id);
                var positionId = SafeString(trade.PositionId);
                var orderId = SafeString(trade.OrderId);

                // CRITICAL: base_id MUST be Quantower Position.Id for proper correlation
                if (string.IsNullOrWhiteSpace(positionId))
                {
                    ReportError($"[QT][ERROR] Trade missing required PositionId - cannot process. TradeId: {qtTradeId ?? "unknown"}, OrderId: {orderId ?? "unknown"}", null);
                    json = string.Empty;
                    tradeId = null;
                    return false;
                }

                tradeId = qtTradeId ?? positionId ?? orderId ?? Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

                payload["id"] = tradeId;
                payload["base_id"] = positionId;  // REQUIRED: Only use PositionId as base_id

                if (!string.IsNullOrWhiteSpace(qtTradeId))
                {
                    payload["qt_trade_id"] = qtTradeId;
                }

                payload["qt_position_id"] = positionId;  // Always include for audit trail

                if (!string.IsNullOrWhiteSpace(orderId))
                {
                    payload["order_id"] = orderId;
                }

                AddInstrument(payload, trade.Symbol);
                AddAccount(payload, trade.Account);
                AddActionQuantityPrice(payload, trade.Side, trade.Quantity, trade.Price);
                AddTimestamp(payload, trade.DateTime);
                AddStrategyTag(payload, trade.Comment, trade.AdditionalInfo);

                json = JsonSerializer.Serialize(payload, SerializerOptions);
                return true;
            }
            catch (JsonException ex)
            {
                ReportError($"[QT][ERROR] Failed to serialize Quantower trade payload: {ex.Message}", ex);
            }
            catch (FormatException ex)
            {
                ReportError($"[QT][ERROR] Invalid numeric/date format in Quantower trade: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                ReportError($"[QT][ERROR] Unexpected error while mapping Quantower trade: {ex.Message}", ex);
            }

            json = string.Empty;
            tradeId = null;
            return false;
        }

        public static bool TryBuildPositionSnapshot(Position position, out string json, out string? positionTradeId)
        {
            json = string.Empty;
            positionTradeId = null;

            if (position == null)
            {
                return false;
            }

            try
            {
                var payload = new Dictionary<string, object?>
                {
                    ["origin_platform"] = "quantower"
                };

                var positionId = SafeString(position.Id);

                // CRITICAL: base_id MUST use composite baseId (Position.Id + OpenTime.Ticks)
                // to ensure uniqueness when Quantower reuses Position.Id across different positions
                if (string.IsNullOrWhiteSpace(positionId))
                {
                    ReportError($"[QT][ERROR] Position missing required Position.Id - cannot process snapshot.", null);
                    json = string.Empty;
                    positionTradeId = null;
                    return false;
                }

                // Compute composite baseId for uniqueness
                var baseId = ComputeBaseId(position);
                if (string.IsNullOrWhiteSpace(baseId))
                {
                    ReportError($"[QT][ERROR] Failed to compute baseId for position - cannot process snapshot.", null);
                    json = string.Empty;
                    positionTradeId = null;
                    return false;
                }

                positionTradeId = baseId;  // Use composite baseId as the trade ID

                payload["id"] = positionTradeId;
                payload["base_id"] = baseId;  // CRITICAL: Use composite baseId for uniqueness
                payload["qt_position_id"] = positionId;  // Always include original Position.Id for audit trail

                AddInstrument(payload, position.Symbol);
                AddAccount(payload, position.Account);
                AddActionQuantityPrice(payload, position.Side, position.Quantity, position.OpenPrice);
                AddTimestamp(payload, position.OpenTime);
                AddStrategyTag(payload, position.Comment, position.AdditionalInfo);

                json = JsonSerializer.Serialize(payload, SerializerOptions);
                return true;
            }
            catch (JsonException ex)
            {
                ReportError($"[QT][ERROR] Failed to serialize Quantower position snapshot: {ex.Message}", ex);
            }
            catch (FormatException ex)
            {
                ReportError($"[QT][ERROR] Invalid data when mapping Quantower position snapshot: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                ReportError($"[QT][ERROR] Unexpected error while mapping Quantower position snapshot: {ex.Message}", ex);
            }

            json = string.Empty;
            positionTradeId = null;
            return false;
        }

        public static bool TryBuildPositionClosure(Position position, out string json, out string? positionId)
        {
            json = string.Empty;
            positionId = null;

            if (position == null)
            {
                return false;
            }

            try
            {
                var payload = new Dictionary<string, object?>
                {
                    ["event_type"] = "quantower_position_closed",
                    ["origin_platform"] = "quantower",
                    ["closure_reason"] = "qt_position_removed"
                };

                var resolvedPositionId = SafeString(position.Id);

                // CRITICAL: base_id MUST use composite baseId (Position.Id + OpenTime.Ticks)
                // to match the baseId used when the position was opened
                if (string.IsNullOrWhiteSpace(resolvedPositionId))
                {
                    ReportError($"[QT][ERROR] Position missing required Position.Id - cannot process closure.", null);
                    json = string.Empty;
                    positionId = null;
                    return false;
                }

                // Compute composite baseId to match the opening event
                var baseId = ComputeBaseId(position);
                if (string.IsNullOrWhiteSpace(baseId))
                {
                    ReportError($"[QT][ERROR] Failed to compute baseId for position closure.", null);
                    json = string.Empty;
                    positionId = null;
                    return false;
                }

                payload["id"] = baseId;
                payload["base_id"] = baseId;  // CRITICAL: Use composite baseId to match opening
                payload["qt_position_id"] = resolvedPositionId;  // Always include original Position.Id for audit trail

                positionId = baseId;

                AddStrategyTag(payload, position.Comment, position.AdditionalInfo);
                AddClosureInstrumentAndAccount(payload, position.Symbol, position.Account);
                var normalizedQuantity = Math.Abs(position.Quantity);
                payload["closed_hedge_quantity"] = normalizedQuantity;
                payload["closed_hedge_action"] = ResolveAction(position.Side, normalizedQuantity);

                var closeTime = GetDateTimeValue(position, "CloseTime", "ExecutionTime", "Time", "DateTime", "Timestamp")
                                 ?? DateTime.UtcNow;
                payload["timestamp"] = closeTime.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

                json = JsonSerializer.Serialize(payload, SerializerOptions);
                return true;
            }
            catch (JsonException ex)
            {
                ReportError($"[QT][ERROR] Failed to serialize Quantower position closure: {ex.Message}", ex);
            }
            catch (FormatException ex)
            {
                ReportError($"[QT][ERROR] Invalid data while mapping Quantower position closure: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                ReportError($"[QT][ERROR] Unexpected error while mapping Quantower position closure: {ex.Message}", ex);
            }

            json = string.Empty;
            positionId = null;
            return false;
        }

        private static void AddInstrument(IDictionary<string, object?> payload, Symbol? symbol)
        {
            if (symbol == null)
            {
                return;
            }

            var name = SafeString(symbol.Name) ?? SafeString(symbol.Id) ?? SafeString(symbol.Description);
            if (!string.IsNullOrWhiteSpace(name))
            {
                payload["instrument"] = name;
            }
        }

        private static void AddAccount(IDictionary<string, object?> payload, Account? account)
        {
            if (account == null)
            {
                return;
            }

            var name = SafeString(account.Name) ?? SafeString(account.Id);
            if (!string.IsNullOrWhiteSpace(name))
            {
                payload["account_name"] = name;
            }
        }

        private static void AddClosureInstrumentAndAccount(IDictionary<string, object?> payload, Symbol? symbol, Account? account)
        {
            if (symbol != null)
            {
                var name = SafeString(symbol.Name) ?? SafeString(symbol.Id) ?? SafeString(symbol.Description);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    payload["nt_instrument_symbol"] = name;
                }
            }

            if (account != null)
            {
                var name = SafeString(account.Name) ?? SafeString(account.Id);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    payload["nt_account_name"] = name;
                }
            }
        }

        private static void AddActionQuantityPrice(IDictionary<string, object?> payload, Side side, double quantity, double price)
        {
            payload["quantity"] = Math.Abs(quantity);
            payload["action"] = ResolveAction(side, quantity);
            payload["price"] = price;
        }

        private static void AddTimestamp(IDictionary<string, object?> payload, DateTime timestamp)
        {
            if (timestamp == default)
            {
                payload["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                return;
            }

            var utc = timestamp.ToUniversalTime();
            payload["timestamp"] = new DateTimeOffset(utc).ToUnixTimeSeconds();
        }

        private static void AddStrategyTag(IDictionary<string, object?> payload, string? comment, AdditionalInfoCollection? additionalInfo)
        {
            var strategyTag = SafeString(comment);

            if (string.IsNullOrWhiteSpace(strategyTag) && additionalInfo != null)
            {
                if (additionalInfo.TryGetItem("strategy_tag", out var item) && item?.Value is string s && !string.IsNullOrWhiteSpace(s))
                {
                    strategyTag = s;
                }
            }

            if (!string.IsNullOrWhiteSpace(strategyTag))
            {
                payload["strategy_tag"] = strategyTag;
            }
        }

        private static string ResolveAction(Side side, double quantity)
        {
            return side switch
            {
                Side.Buy => "buy",
                Side.Sell => "sell",
                _ => quantity >= 0 ? "buy" : "sell"
            };
        }

        private static string? SafeString(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private static DateTime? GetDateTimeValue(object target, params string[] propertyNames)
        {
            foreach (var propertyName in propertyNames)
            {
                var value = GetPropertyValue(target, propertyName);
                if (value == null)
                {
                    continue;
                }

                switch (value)
                {
                    case DateTime dt:
                        if (dt.Kind == DateTimeKind.Unspecified)
                        {
                            return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
                        }
                        return dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
                    case DateTimeOffset dto:
                        return dto.UtcDateTime;
                    case long unix:
                        return DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
                    case double unixDouble:
                        return DateTimeOffset.FromUnixTimeSeconds((long)unixDouble).UtcDateTime;
                    case string s:
                        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var dtoParsed))
                        {
                            return dtoParsed.UtcDateTime;
                        }

                        var hasOffset = HasExplicitOffset(s);
                        var styles = DateTimeStyles.AllowWhiteSpaces;
                        if (!hasOffset)
                        {
                            styles |= DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
                        }

                        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, styles, out var parsed))
                        {
                            var utc = hasOffset ? parsed.ToUniversalTime() : parsed;
                            return DateTime.SpecifyKind(utc, DateTimeKind.Utc);
                        }
                        continue;
                }
                continue;
            }

            return null;
        }

        private static object? GetPropertyValue(object target, string propertyName)
        {
            var type = target.GetType();
            var property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property != null)
            {
                return property.GetValue(target);
            }

            var field = type.GetField(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            return field?.GetValue(target);
        }

        private static bool HasExplicitOffset(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (c == 'Z' || c == 'z')
                {
                    return true;
                }

                if ((c == '+' || c == '-') && i > 0)
                {
                    var previous = value[i - 1];
                    if (previous == 'T' || previous == ' ')
                    {
                        return true;
                    }

                    if (i >= value.Length - 5)
                    {
                        var nextIndex = i + 1;
                        if (nextIndex < value.Length && char.IsDigit(value[nextIndex]))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }
    }
}
