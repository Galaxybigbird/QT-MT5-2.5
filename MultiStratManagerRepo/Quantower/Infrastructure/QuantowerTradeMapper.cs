using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quantower.MultiStrat.Infrastructure
{
    internal static class QuantowerTradeMapper
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        public static bool TryBuildTradeEnvelope(object trade, out string json, out string? tradeId)
        {
            json = string.Empty;
            tradeId = null;

            if (trade == null)
            {
                return false;
            }

            try
            {
                var payload = new Dictionary<string, object?>();

                var qtTradeId = GetString(trade, "TradeId", "Id", "ExecutionId");
                var positionId = GetString(trade, "PositionId", "StrategyId", "BaseId");
                var orderId = GetString(trade, "OrderId");

                tradeId = qtTradeId ?? positionId ?? orderId ?? Guid.NewGuid().ToString("N");

                payload["id"] = tradeId;
                if (!string.IsNullOrWhiteSpace(qtTradeId))
                {
                    payload["qt_trade_id"] = qtTradeId;
                }
                if (!string.IsNullOrWhiteSpace(positionId))
                {
                    payload["qt_position_id"] = positionId;
                    payload["base_id"] = positionId;
                }

                payload["order_id"] = orderId;

                payload["instrument"] = ExtractInstrumentName(trade);
                payload["account_name"] = ExtractAccountName(trade);

                var qty = GetDouble(trade, "Quantity", "Volume", "Amount");
                payload["quantity"] = Math.Abs(qty);

                payload["action"] = DetermineAction(trade, qty);
                payload["price"] = GetDouble(trade, "Price", "ExecutionPrice", "FillPrice");

                var time = GetDateTime(trade, "ExecutionTime", "Time", "DateTime", "Timestamp");
                if (time.HasValue)
                {
                    payload["timestamp"] = new DateTimeOffset(time.Value).ToUnixTimeSeconds();
                }

                payload["origin_platform"] = "quantower";

                json = JsonSerializer.Serialize(payload, SerializerOptions);
                return true;
            }
            catch
            {
                json = string.Empty;
                tradeId = null;
                return false;
            }
        }

        private static string DetermineAction(object trade, double quantity)
        {
            var direction = GetString(trade, "Direction", "Side", "TradeSide", "OrderSide");
            if (!string.IsNullOrWhiteSpace(direction))
            {
                direction = direction.Trim();
                if (direction.Equals("buy", StringComparison.OrdinalIgnoreCase) || direction.Equals("long", StringComparison.OrdinalIgnoreCase))
                {
                    return "buy";
                }
                if (direction.Equals("sell", StringComparison.OrdinalIgnoreCase) || direction.Equals("short", StringComparison.OrdinalIgnoreCase))
                {
                    return "sell";
                }
            }

            return quantity >= 0 ? "buy" : "sell";
        }

        private static string? ExtractInstrumentName(object trade)
        {
            var instrument = GetObject(trade, "Instrument", "Symbol");
            if (instrument == null)
            {
                return null;
            }

            return GetString(instrument, "FullName", "Name", "Id", "Symbol");
        }

        private static string? ExtractAccountName(object trade)
        {
            var account = GetObject(trade, "Account");
            if (account == null)
            {
                return null;
            }

            return GetString(account, "Name", "Id", "AccountId");
        }

        private static string? GetString(object target, params string[] propertyNames)
        {
            foreach (var propertyName in propertyNames)
            {
                var value = GetPropertyValue(target, propertyName);
                if (value == null)
                {
                    continue;
                }

                return value switch
                {
                    string s => string.IsNullOrWhiteSpace(s) ? null : s,
                    Enum e => e.ToString(),
                    IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                    _ => value.ToString()
                };
            }

            return null;
        }

        private static double GetDouble(object target, params string[] propertyNames)
        {
            foreach (var propertyName in propertyNames)
            {
                var value = GetPropertyValue(target, propertyName);
                if (value == null)
                {
                    continue;
                }

                if (value is double d)
                {
                    return d;
                }
                if (value is float f)
                {
                    return f;
                }
                if (value is decimal dec)
                {
                    return (double)dec;
                }
                if (value is int i)
                {
                    return i;
                }
                if (value is long l)
                {
                    return l;
                }
                if (value is IFormattable formattable && double.TryParse(formattable.ToString(null, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }
            }

            return 0d;
        }

        private static DateTime? GetDateTime(object target, params string[] propertyNames)
        {
            foreach (var propertyName in propertyNames)
            {
                var value = GetPropertyValue(target, propertyName);
                if (value == null)
                {
                    continue;
                }

                if (value is DateTime dt)
                {
                    return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
                }
                if (value is DateTimeOffset dto)
                {
                    return dto.UtcDateTime;
                }
                if (value is long ticks)
                {
                    return DateTimeOffset.FromUnixTimeSeconds(ticks).UtcDateTime;
                }
                if (value is IFormattable formattable)
                {
                    if (DateTime.TryParse(formattable.ToString(null, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                    {
                        return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
                    }
                }
            }

            return null;
        }

        private static object? GetObject(object target, params string[] propertyNames)
        {
            foreach (var propertyName in propertyNames)
            {
                var value = GetPropertyValue(target, propertyName);
                if (value != null)
                {
                    return value;
                }
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
    }
}
