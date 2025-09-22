using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingPlatform.BusinessLayer;

namespace Quantower.MultiStrat.Infrastructure
{
    internal static class QuantowerTradeMapper
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

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

                var qtTradeId = SafeString(trade.Id) ?? SafeString(trade.UniqueId);
                var positionId = SafeString(trade.PositionId);
                var orderId = SafeString(trade.OrderId);

                tradeId = qtTradeId ?? positionId ?? orderId ?? Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

                payload["id"] = tradeId;
                payload["base_id"] = positionId ?? tradeId;

                if (!string.IsNullOrWhiteSpace(qtTradeId))
                {
                    payload["qt_trade_id"] = qtTradeId;
                }

                if (!string.IsNullOrWhiteSpace(positionId))
                {
                    payload["qt_position_id"] = positionId;
                }

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
                Console.Error.WriteLine($"[QT][ERROR] Failed to serialize Quantower trade payload: {ex.Message}\n{ex}");
            }
            catch (FormatException ex)
            {
                Console.Error.WriteLine($"[QT][ERROR] Invalid numeric/date format in Quantower trade: {ex.Message}\n{ex}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[QT][ERROR] Unexpected error while mapping Quantower trade: {ex.Message}\n{ex}");
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

                var positionId = SafeString(position.Id) ?? SafeString(position.UniqueId);
                positionTradeId = positionId ?? Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

                payload["id"] = positionTradeId;
                payload["base_id"] = positionId ?? positionTradeId;
                payload["qt_position_id"] = positionId;

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
                Console.Error.WriteLine($"[QT][ERROR] Failed to serialize Quantower position snapshot: {ex.Message}\n{ex}");
            }
            catch (FormatException ex)
            {
                Console.Error.WriteLine($"[QT][ERROR] Invalid data when mapping Quantower position snapshot: {ex.Message}\n{ex}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[QT][ERROR] Unexpected error while mapping Quantower position snapshot: {ex.Message}\n{ex}");
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

                positionId = SafeString(position.Id) ?? SafeString(position.UniqueId);

                if (!string.IsNullOrWhiteSpace(positionId))
                {
                    payload["base_id"] = positionId;
                    payload["qt_position_id"] = positionId;
                }

                AddStrategyTag(payload, position.Comment, position.AdditionalInfo);
                AddClosureInstrumentAndAccount(payload, position.Symbol, position.Account);
                payload["closed_hedge_quantity"] = Math.Abs(position.Quantity);
                payload["closed_hedge_action"] = ResolveAction(position.Side, position.Quantity);
                payload["timestamp"] = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);

                json = JsonSerializer.Serialize(payload, SerializerOptions);
                return true;
            }
            catch (JsonException ex)
            {
                Console.Error.WriteLine($"[QT][ERROR] Failed to serialize Quantower position closure: {ex.Message}\n{ex}");
            }
            catch (FormatException ex)
            {
                Console.Error.WriteLine($"[QT][ERROR] Invalid data while mapping Quantower position closure: {ex.Message}\n{ex}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[QT][ERROR] Unexpected error while mapping Quantower position closure: {ex.Message}\n{ex}");
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

            var normalized = DateTime.SpecifyKind(timestamp, DateTimeKind.Utc);
            payload["timestamp"] = new DateTimeOffset(normalized).ToUnixTimeSeconds();
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
    }
}
