using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Quantower.MultiStrat.Indicators;
using Quantower.MultiStrat.Utilities;
using TradingPlatform.BusinessLayer;
using IndicatorQuote = Quantower.MultiStrat.Indicators.Quote;

namespace Quantower.MultiStrat.Services
{
    public sealed class TrailingElasticService
    {
        public enum ProfitUnitType
        {
            Dollars,
            Pips,
            Ticks,
            Percent
        }

        private sealed class ElasticTracker
        {
            public string BaseId = string.Empty;
            public string? SymbolKey;
            public string? AccountId;
            public double EntryPrice;
            public double Quantity;
            public Side Side;
            public bool Triggered;
            public double TriggerUnitsAtActivation;
            public int LastReportedLevel;
            public double LastReportedProfit;
            public DateTime LastPublishUtc;
            public double HighWaterMark;
            public double LowWaterMark;
            public double? LastTrailingStop;
            public double LastSourcePrice;
        }

        private readonly ConcurrentDictionary<string, ElasticTracker> _trackers = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, List<IndicatorQuote>> _quoteHistory = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, object> _trackerLocks = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, object> _historyLocks = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<Type, PropertyInfo?> TickSizePropertyCache = new();
        private readonly ConcurrentDictionary<string, double> _tickSizeCache = new(StringComparer.OrdinalIgnoreCase);

        public Action<string>? LogWarning { get; set; }

        public bool EnableElasticHedging { get; set; } = true;
        public bool EnableTrailing { get; set; } = true;
        public bool UseDemaAtrTrailing { get; set; } = true;
        public ProfitUnitType ElasticTriggerUnits { get; set; } = ProfitUnitType.Dollars;
        public double ProfitUpdateThreshold { get; set; } = 100.0;
        public ProfitUnitType ElasticIncrementUnits { get; set; } = ProfitUnitType.Dollars;
        public double ElasticIncrementValue { get; set; } = 10.0;
        public ProfitUnitType TrailingActivationUnits { get; set; } = ProfitUnitType.Percent;
        public double TrailingActivationValue { get; set; } = 1.0;
        public ProfitUnitType TrailingStopUnits { get; set; } = ProfitUnitType.Dollars;
        public double TrailingStopValue { get; set; } = 50.0;
        public double DemaAtrMultiplier { get; set; } = 1.5;
        public int AtrPeriod { get; set; } = 14;
        public int DemaPeriod { get; set; } = 21;

        public void RegisterPosition(string baseId, Position position)
        {
            if (string.IsNullOrWhiteSpace(baseId) || position == null)
            {
                return;
            }

            var tracker = _trackers.GetOrAdd(baseId, _ => new ElasticTracker());
            var trackerLock = GetTrackerLock(baseId);
            lock (trackerLock)
            {
                var wasInitialized = tracker.EntryPrice > 0 || tracker.LastPublishUtc != DateTime.MinValue || tracker.Triggered;

                tracker.BaseId = baseId;
                tracker.SymbolKey = GetSymbolKey(position.Symbol);
                tracker.AccountId = GetAccountId(position.Account);
                tracker.Quantity = Math.Abs(position.Quantity);
                tracker.Side = ResolveSide(position);

                if (wasInitialized)
                {
                    if (position.OpenPrice > 0 && !double.IsNaN(position.OpenPrice) && !double.IsInfinity(position.OpenPrice))
                    {
                        tracker.EntryPrice = position.OpenPrice;
                    }

                    var refreshedPrice = ResolveCurrentPrice(position);
                    if (refreshedPrice > 0 && !double.IsNaN(refreshedPrice) && !double.IsInfinity(refreshedPrice))
                    {
                        tracker.LastSourcePrice = refreshedPrice;
                        UpdateWaterMarks(tracker, refreshedPrice);
                    }

                    return;
                }

                tracker.EntryPrice = position.OpenPrice;
                tracker.Triggered = false;
                tracker.TriggerUnitsAtActivation = 0.0;
                tracker.LastReportedLevel = 0;
                tracker.LastReportedProfit = 0.0;
                tracker.LastPublishUtc = DateTime.MinValue;
                tracker.LastTrailingStop = position.StopLoss?.Price;

                var current = ResolveCurrentPrice(position);
                if (current <= 0)
                {
                    current = tracker.EntryPrice;
                }

                tracker.LastSourcePrice = current;
                tracker.HighWaterMark = current;
                tracker.LowWaterMark = current;
            }
        }

        public void RemoveTracker(string baseId)
        {
            if (string.IsNullOrWhiteSpace(baseId))
            {
                return;
            }

            _trackers.TryRemove(baseId, out _);
            _trackerLocks.TryRemove(baseId, out _);
        }

        public void TrackQuote(Symbol symbol, IndicatorQuote quote)
        {
            if (symbol == null || quote == null)
            {
                return;
            }

            var key = GetSymbolKey(symbol);
            if (string.IsNullOrEmpty(key))
            {
                return;
            }

            var history = _quoteHistory.GetOrAdd(key, _ => new List<IndicatorQuote>());
            var historyLock = GetHistoryLock(key);
            lock (historyLock)
            {
                history.Add(quote);
                if (history.Count > 600)
                {
                    history.RemoveRange(0, history.Count - 600);
                }
            }

            foreach (var trackerEntry in _trackers)
            {
                var tracker = trackerEntry.Value;
                if (!string.Equals(tracker.SymbolKey, key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var trackerLock = GetTrackerLock(trackerEntry.Key);
                lock (trackerLock)
                {
                    UpdateWaterMarks(tracker, quote.Close);
                }
            }
        }

        public void RecordTrade(Trade trade)
        {
            if (trade?.Symbol == null)
            {
                return;
            }

            var quote = new IndicatorQuote
            {
                Date = trade.DateTime,
                Open = trade.Price,
                High = trade.Price,
                Low = trade.Price,
                Close = trade.Price,
                Volume = (long)Math.Abs(trade.Quantity)
            };

            TrackQuote(trade.Symbol, quote);
        }

        public Dictionary<string, object?>? TryBuildElasticUpdate(string baseId, Position position)
        {
            if (!EnableElasticHedging || position == null)
            {
                return null;
            }

            var tracker = GetTracker(baseId, position);
            if (tracker == null)
            {
                return null;
            }

            var trackerLock = GetTrackerLock(baseId);
            lock (trackerLock)
            {
                var currentPrice = ResolveCurrentPrice(position);
                if (currentPrice <= 0)
                {
                    currentPrice = tracker.EntryPrice;
                }

                UpdateWaterMarks(tracker, currentPrice);

                var profitDollars = PnLUtils.GetMoney(position.GrossPnL);
                var triggerUnits = ConvertUnits(ElasticTriggerUnits, position, tracker, currentPrice, profitDollars);

                if (!tracker.Triggered && triggerUnits >= ProfitUpdateThreshold)
                {
                    tracker.Triggered = true;
                    tracker.TriggerUnitsAtActivation = ConvertUnits(ElasticIncrementUnits, position, tracker, currentPrice, profitDollars);
                    tracker.LastReportedLevel = 0;
                }

                if (!tracker.Triggered)
                {
                    return null;
                }

                var incrementUnits = ConvertUnits(ElasticIncrementUnits, position, tracker, currentPrice, profitDollars);
                var deltaUnits = Math.Max(0.0, incrementUnits - tracker.TriggerUnitsAtActivation);
                var increments = 1 + (int)Math.Floor(deltaUnits / Math.Max(ElasticIncrementValue, 1e-6));
                if (increments <= tracker.LastReportedLevel)
                {
                    return null;
                }

                tracker.LastReportedLevel = increments;
                tracker.LastReportedProfit = profitDollars;
                tracker.LastPublishUtc = DateTime.UtcNow;
                tracker.LastSourcePrice = currentPrice;

                var atr = GetAtr(position.Symbol, AtrPeriod);
                var dema = GetDema(position.Symbol, DemaPeriod);

                var payload = new Dictionary<string, object?>
                {
                    ["event_type"] = "elastic_hedge_update",
                    ["action"] = "ELASTIC_UPDATE",
                    ["origin_platform"] = "quantower",
                    ["base_id"] = tracker.BaseId,
                    ["qt_position_id"] = tracker.BaseId,
                    ["current_profit"] = profitDollars,
                    ["profit_level"] = increments,
                    ["trigger_units"] = triggerUnits,
                    ["increment_units"] = incrementUnits,
                    ["entry_price"] = tracker.EntryPrice,
                    ["current_price"] = currentPrice,
                    ["quantity"] = tracker.Quantity,
                    ["market_position"] = tracker.Side == Side.Sell ? "Short" : "Long",
                    ["trend_high"] = tracker.HighWaterMark,
                    ["trend_low"] = tracker.LowWaterMark,
                    ["timestamp"] = tracker.LastPublishUtc.ToString("o", CultureInfo.InvariantCulture)
                };

                if (!string.IsNullOrWhiteSpace(tracker.AccountId))
                {
                    payload["account_name"] = tracker.AccountId;
                }

                if (position.Symbol != null)
                {
                    payload["symbol"] = position.Symbol.Name ?? position.Symbol.Id;
                    payload["symbol_full_name"] = position.Symbol.Description ?? position.Symbol.Name;
                }

                if (atr.HasValue)
                {
                    payload["atr_value"] = atr.Value;
                }

                if (dema.HasValue)
                {
                    payload["dema_value"] = dema.Value;
                }

                return payload;
            }
        }

        public Dictionary<string, object?>? TryBuildTrailingUpdate(string baseId, Position position)
        {
            if (!EnableTrailing || position == null)
            {
                return null;
            }

            var tracker = GetTracker(baseId, position);
            if (tracker == null)
            {
                return null;
            }

            var trackerLock = GetTrackerLock(baseId);
            lock (trackerLock)
            {
                var currentPrice = ResolveCurrentPrice(position);
                if (currentPrice <= 0)
                {
                    currentPrice = tracker.LastSourcePrice <= 0 ? tracker.EntryPrice : tracker.LastSourcePrice;
                }

                var activationUnits = ConvertUnits(TrailingActivationUnits, position, tracker, currentPrice, PnLUtils.GetMoney(position.GrossPnL));
                if (activationUnits < TrailingActivationValue)
                {
                    return null;
                }

                var offset = ComputeTrailingOffset(position, tracker, currentPrice);
                if (offset <= 0)
                {
                    return null;
                }

                var newStop = tracker.Side == Side.Sell ? currentPrice + offset : currentPrice - offset;
                if (!IsImprovedStop(tracker, newStop))
                {
                    return null;
                }

                tracker.LastTrailingStop = newStop;
                tracker.LastSourcePrice = currentPrice;

                var atr = GetAtr(position.Symbol, AtrPeriod);
                var dema = GetDema(position.Symbol, DemaPeriod);

                var payload = new Dictionary<string, object?>
                {
                    ["event_type"] = "trailing_update",
                    ["action"] = "TRAILING_STOP_UPDATE",
                    ["origin_platform"] = "quantower",
                    ["base_id"] = tracker.BaseId,
                    ["qt_position_id"] = tracker.BaseId,
                    ["new_stop_price"] = newStop,
                    ["current_price"] = currentPrice,
                    ["market_position"] = tracker.Side == Side.Sell ? "Short" : "Long",
                    ["timestamp"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    ["trailing_type"] = DetermineTrailingType(atr, dema)
                };

                if (!string.IsNullOrWhiteSpace(tracker.AccountId))
                {
                    payload["account_name"] = tracker.AccountId;
                }

                if (position.Symbol != null)
                {
                    payload["symbol"] = position.Symbol.Name ?? position.Symbol.Id;
                    payload["symbol_full_name"] = position.Symbol.Description ?? position.Symbol.Name;
                }

                if (atr.HasValue)
                {
                    payload["atr_value"] = atr.Value;
                }

                if (dema.HasValue)
                {
                    payload["dema_value"] = dema.Value;
                }

                return payload;
            }
        }

        private ElasticTracker? GetTracker(string baseId, Position position)
        {
            if (string.IsNullOrWhiteSpace(baseId))
            {
                return null;
            }

            if (!_trackers.TryGetValue(baseId, out var tracker))
            {
                RegisterPosition(baseId, position);
                _trackers.TryGetValue(baseId, out tracker);
            }

            return tracker;
        }

        private object GetTrackerLock(string baseId)
        {
            return _trackerLocks.GetOrAdd(baseId, _ => new object());
        }

        private object GetHistoryLock(string key)
        {
            return _historyLocks.GetOrAdd(key, _ => new object());
        }

        private static string? GetAccountId(Account? account)
        {
            return account?.Id ?? account?.Name;
        }

        private static Side ResolveSide(Position position)
        {
            try
            {
                return position.Side;
            }
            catch
            {
                return position.Quantity < 0 ? Side.Sell : Side.Buy;
            }
        }

        private void UpdateWaterMarks(ElasticTracker tracker, double price)
        {
            if (price <= 0 || double.IsNaN(price) || double.IsInfinity(price))
            {
                var reason = price <= 0 ? "non-positive" : (double.IsNaN(price) ? "NaN" : "Infinity");
                var symbol = tracker.SymbolKey ?? "unknown";
                var message = $"[TrailingElastic] Ignoring invalid price {price} ({reason}) for baseId={tracker.BaseId}, symbol={symbol}";
                if (LogWarning != null)
                {
                    LogWarning.Invoke(message);
                }
                else
                {
                    Console.WriteLine(message);
                }
                return;
            }

            if (tracker.HighWaterMark <= 0)
            {
                tracker.HighWaterMark = price;
            }

            if (tracker.LowWaterMark <= 0)
            {
                tracker.LowWaterMark = price;
            }

            tracker.HighWaterMark = Math.Max(tracker.HighWaterMark, price);
            tracker.LowWaterMark = Math.Min(tracker.LowWaterMark, price);
        }

        private double ComputeTrailingOffset(Position position, ElasticTracker tracker, double currentPrice)
        {
            if (UseDemaAtrTrailing)
            {
                var atr = GetAtr(position.Symbol, AtrPeriod);
                if (atr.HasValue && atr.Value > 0)
                {
                    return atr.Value * Math.Max(0.1, DemaAtrMultiplier);
                }

                var dema = GetDema(position.Symbol, DemaPeriod);
                if (dema.HasValue && dema.Value > 0)
                {
                    var delta = Math.Abs(currentPrice - dema.Value);
                    if (delta > 0)
                    {
                        return delta * Math.Max(1.0, DemaAtrMultiplier);
                    }
                }
            }

            return TrailingStopUnits switch
            {
                ProfitUnitType.Dollars => Math.Max(0.0, TrailingStopValue / Math.Max(1.0, tracker.Quantity)),
                ProfitUnitType.Pips => TrailingStopValue * GetPipSize(position.Symbol),
                ProfitUnitType.Ticks => TrailingStopValue * GetTickSize(position.Symbol),
                ProfitUnitType.Percent => Math.Abs(currentPrice) * (TrailingStopValue / 100.0),
                _ => Math.Abs(currentPrice - tracker.EntryPrice) * 0.5
            };
        }

        private static bool IsImprovedStop(ElasticTracker tracker, double candidate)
        {
            if (double.IsNaN(candidate) || double.IsInfinity(candidate) || candidate <= 0)
            {
                return false;
            }

            if (tracker.LastTrailingStop == null)
            {
                return true;
            }

            var previous = tracker.LastTrailingStop.Value;
            if (tracker.Side == Side.Sell)
            {
                return candidate < previous - 1e-6;
            }

            return candidate > previous + 1e-6;
        }

    private double ConvertUnits(ProfitUnitType units, Position position, ElasticTracker tracker, double currentPrice, double profitDollars)
    {
        var priceDelta = currentPrice - tracker.EntryPrice;
        var signedDelta = tracker.Side == Side.Sell ? -priceDelta : priceDelta;
        var effectiveDelta = Math.Max(0.0, signedDelta);

        return units switch
        {
            ProfitUnitType.Dollars => profitDollars,
            ProfitUnitType.Percent => tracker.EntryPrice == 0
                ? 0.0
                : (effectiveDelta / tracker.EntryPrice) * 100.0,
            ProfitUnitType.Pips => ConvertDistance(effectiveDelta, GetPipSize(position.Symbol)),
            ProfitUnitType.Ticks => ConvertDistance(effectiveDelta, GetTickSize(position.Symbol)),
            _ => profitDollars
        };

        static double ConvertDistance(double delta, double stepSize) =>
            stepSize > 0 ? delta / stepSize : 0.0;
    }

        public double? GetAtr(Symbol? symbol, int period)
        {
            if (symbol == null)
            {
                return null;
            }

            var key = GetSymbolKey(symbol);
            if (string.IsNullOrEmpty(key))
            {
                return null;
            }

            if (_quoteHistory.TryGetValue(key, out var history))
            {
                var historyLock = GetHistoryLock(key);
                lock (historyLock)
                {
                    return IndicatorCalculator.CalculateAtr(history.ToList(), period);
                }
            }

            return null;
        }

        public double? GetDema(Symbol? symbol, int period)
        {
            if (symbol == null)
            {
                return null;
            }

            var key = GetSymbolKey(symbol);
            if (string.IsNullOrEmpty(key))
            {
                return null;
            }

            if (_quoteHistory.TryGetValue(key, out var history))
            {
                var historyLock = GetHistoryLock(key);
                lock (historyLock)
                {
                    return IndicatorCalculator.CalculateDema(history.ToList(), period);
                }
            }

            return null;
        }

        private static double ResolveCurrentPrice(Position position)
        {
            if (position == null)
            {
                return 0.0;
            }

            try
            {
                if (position.Symbol != null)
                {
                    if (position.Symbol.Last > 0)
                    {
                        return position.Symbol.Last;
                    }

                    if (position.Symbol.Bid > 0 && position.Symbol.Ask > 0)
                    {
                        return (position.Symbol.Bid + position.Symbol.Ask) / 2.0;
                    }
                }

                if (position.CurrentPrice > 0)
                {
                    return position.CurrentPrice;
                }
            }
            catch
            {
                // ignore runtime-only issues from certain connectivity providers
            }

            return position.OpenPrice;
        }

        private string DetermineTrailingType(double? atr, double? dema)
        {
            if (!UseDemaAtrTrailing)
            {
                return "static";
            }

            if (atr.HasValue && dema.HasValue)
            {
                return "atr_dema";
            }

            if (atr.HasValue)
            {
                return "atr";
            }

            if (dema.HasValue)
            {
                return "dema";
            }

            return "static";
        }

        private static string GetSymbolKey(Symbol? symbol)
        {
            if (symbol == null)
            {
                return string.Empty;
            }

            return !string.IsNullOrWhiteSpace(symbol.Id)
                ? symbol.Id
                : symbol.Name ?? symbol.Description ?? string.Empty;
        }

        private double GetTickSize(Symbol? symbol)
        {
            if (symbol == null)
            {
                return 0.0;
            }

            var cacheKey = GetSymbolKey(symbol);
            if (!string.IsNullOrEmpty(cacheKey) && _tickSizeCache.TryGetValue(cacheKey, out var cached) && cached > 0)
            {
                return cached;
            }

            try
            {
                if (symbol.TickSize > 0)
                {
                    if (!string.IsNullOrEmpty(cacheKey))
                    {
                        _tickSizeCache[cacheKey] = symbol.TickSize;
                    }

                    return symbol.TickSize;
                }
            }
            catch
            {
                // fall through to reflection-based lookup
            }

            var type = symbol.GetType();
            var tickProp = TickSizePropertyCache.GetOrAdd(type, t => t.GetProperty("TickSize"));
            if (tickProp != null)
            {
                try
                {
                    var value = tickProp.GetValue(symbol);
                    double tickSize = value switch
                    {
                        double d when d > 0 => d,
                        decimal dec when dec > 0 => (double)dec,
                        _ => 0.0
                    };

                    if (tickSize > 0 && !string.IsNullOrEmpty(cacheKey))
                    {
                        _tickSizeCache[cacheKey] = tickSize;
                    }

                    if (tickSize > 0)
                    {
                        return tickSize;
                    }
                }
                catch
                {
                    // ignore and fall through to default
                }
            }

            return 0.0;
        }

        private double GetPipSize(Symbol? symbol)
        {
            if (symbol == null)
            {
                return 0.0;
            }

            var pipSizeProp = symbol.GetType().GetProperty("PipSize");
            if (pipSizeProp != null)
            {
                var value = pipSizeProp.GetValue(symbol);
                if (value is double d && d > 0)
                {
                    return d;
                }

                if (value is decimal dec && dec > 0)
                {
                    return (double)dec;
                }
            }

            var pointProp = symbol.GetType().GetProperty("Point");
            if (pointProp != null)
            {
                var value = pointProp.GetValue(symbol);
                if (value is double d && d > 0)
                {
                    return d;
                }

                if (value is decimal dec && dec > 0)
                {
                    return (double)dec;
                }
            }

            var tickSize = GetTickSize(symbol);
            return tickSize > 0 ? tickSize : 0.0;
        }
    }
}
