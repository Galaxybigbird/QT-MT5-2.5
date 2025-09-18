using System;
using System.Collections.Generic;
using System.Linq;

namespace NinjaTrader.NinjaScript.AddOns
{
    /// <summary>
    /// Quote data structure for indicator calculations
    /// </summary>
    public class Quote
    {
        public DateTime Date { get; set; }
        public double Open { get; set; }
        public double High { get; set; }
        public double Low { get; set; }
        public double Close { get; set; }
        public long Volume { get; set; }
    }

    /// <summary>
    /// Provides technical indicator calculations for trailing stop logic
    /// Implements DEMA and ATR calculations using Quote data
    /// </summary>
    public static class IndicatorCalculator
    {
        /// <summary>
        /// Helper method to log messages directly to NinjaTrader output
        /// </summary>
        private static void LogToBridge(string level, string category, string message)
        {
            try
            {
                var mgr = NinjaTrader.NinjaScript.AddOns.MultiStratManager.Instance;
                if (mgr != null)
                {
                    switch ((level ?? "").ToUpperInvariant())
                    {
                        case "DEBUG": mgr.LogDebug(category, message); break;
                        case "WARN": mgr.LogWarn(category, message); break;
                        case "ERROR": mgr.LogError(category, message); break;
                        default: mgr.LogInfo(category, message); break;
                    }
                }
                else
                {
                    NinjaTrader.Code.Output.Process($"[INDICATOR_{level}][{category}] {message}", PrintTo.OutputTab1);
                }
            }
            catch (Exception ex)
            {
                // Last resort fallback
                NinjaTrader.Code.Output.Process($"[INDICATOR_ERROR] Logging failed: {ex.Message} | Original: [{level}][{category}] {message}", PrintTo.OutputTab1);
            }
        }

        /// <summary>
        /// Calculate Double Exponential Moving Average (DEMA) using Mulloy's formula
        /// </summary>
        /// <param name="quotes">List of Quote data</param>
        /// <param name="period">Period for DEMA calculation</param>
        /// <returns>DEMA value or null if insufficient data</returns>
        public static double? CalculateDema(List<Quote> quotes, int period)
        {
            try
            {
                // Allow faster warmup by using roughly half-period of data (minimum 5 quotes)
                int minRequired = Math.Max(period / 2 + 3, Math.Min(period, 10));
                if (quotes == null || quotes.Count < minRequired)
                {
                    LogToBridge("WARN", "INDICATOR", $"[IndicatorCalculator] DEMA requires at least {minRequired} quotes for period {period}, but only {quotes?.Count} available");
                    return null;
                }

                var closePrices = quotes.Select(q => q.Close).ToList();

                var ema1Values = CalculateEma(closePrices, period);
                if (ema1Values == null || ema1Values.Count == 0)
                    return null;

                var ema2Values = CalculateEma(ema1Values, period);
                if (ema2Values == null || ema2Values.Count == 0)
                    return null;

                double currentEma1 = ema1Values.Last();
                double currentEma2 = ema2Values.Last();

                double demaValue = (2.0 * currentEma1) - currentEma2;

                LogToBridge("DEBUG", "INDICATOR", $"[IndicatorCalculator] DEMA calculated: EMA1={currentEma1:F4}, EMA2={currentEma2:F4}, DEMA={demaValue:F4}");

                return demaValue;
            }
            catch (Exception ex)
            {
                LogToBridge("ERROR", "INDICATOR", $"[IndicatorCalculator] Error calculating DEMA: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Calculate Average True Range (ATR) using Wilder's smoothing method
        /// </summary>
        /// <param name="quotes">List of Quote data</param>
        /// <param name="period">Period for ATR calculation</param>
        /// <returns>ATR value or null if insufficient data</returns>
        public static double? CalculateAtr(List<Quote> quotes, int period)
        {
            try
            {
                // Allow ATR to stabilize with roughly half-period warmup while still requiring multiple bars
                int minRequired = Math.Max(period / 2 + 3, Math.Min(period + 1, 10));
                if (quotes == null || quotes.Count < minRequired)
                    return null;

                var trueRanges = new List<double>();

                // Calculate True Range for each bar (starting from second bar)
                for (int i = 1; i < quotes.Count; i++)
                {
                    var current = quotes[i];
                    var previous = quotes[i - 1];

                    double tr1 = current.High - current.Low;
                    double tr2 = Math.Abs(current.High - previous.Close);
                    double tr3 = Math.Abs(current.Low - previous.Close);

                    double trueRange = Math.Max(tr1, Math.Max(tr2, tr3));
                    trueRanges.Add(trueRange);
                }

                if (trueRanges.Count == 0)
                    return null;

                if (trueRanges.Count < period)
                    return trueRanges.Average();

                double firstAtr = trueRanges.Take(period).Average();
                double currentAtr = firstAtr;

                for (int i = period; i < trueRanges.Count; i++)
                {
                    currentAtr = ((currentAtr * (period - 1)) + trueRanges[i]) / period;
                }

                return currentAtr;
            }
            catch (Exception ex)
            {
                LogToBridge("ERROR", "INDICATOR", $"[IndicatorCalculator] Error calculating ATR: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Calculate Exponential Moving Average (EMA)
        /// </summary>
        /// <param name="values">List of values to calculate EMA for</param>
        /// <param name="period">Period for EMA calculation</param>
        /// <returns>List of EMA values or null if insufficient data</returns>
        private static List<double> CalculateEma(List<double> values, int period)
        {
            try
            {
                if (values == null || values.Count == 0)
                    return null;

                var emaValues = new List<double>();

                double multiplier = 2.0 / (period + 1);

                if (values.Count >= period)
                {
                    double sma = values.Take(period).Average();
                    emaValues.Add(sma);

                    for (int i = period; i < values.Count; i++)
                    {
                        double ema = (values[i] * multiplier) + (emaValues.Last() * (1 - multiplier));
                        emaValues.Add(ema);
                    }
                }
                else
                {
                    double ema = values[0];
                    emaValues.Add(ema);

                    for (int i = 1; i < values.Count; i++)
                    {
                        ema = (values[i] * multiplier) + (ema * (1 - multiplier));
                        emaValues.Add(ema);
                    }
                }

                return emaValues;
            }
            catch (Exception ex)
            {
                LogToBridge("ERROR", "INDICATOR", $"[IndicatorCalculator] Error calculating EMA: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Calculate Simple Moving Average (SMA)
        /// </summary>
        /// <param name="values">List of values to calculate SMA for</param>
        /// <param name="period">Period for SMA calculation</param>
        /// <returns>SMA value or null if insufficient data</returns>
        public static double? CalculateSma(List<double> values, int period)
        {
            try
            {
                if (values == null || values.Count < period)
                    return null;

                return values.Skip(Math.Max(0, values.Count - period)).Average();
            }
            catch (Exception ex)
            {
                LogToBridge("ERROR", "INDICATOR", $"[IndicatorCalculator] Error calculating SMA: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Calculate standard deviation
        /// </summary>
        /// <param name="values">List of values</param>
        /// <param name="period">Period for calculation</param>
        /// <returns>Standard deviation or null if insufficient data</returns>
        public static double? CalculateStandardDeviation(List<double> values, int period)
        {
            try
            {
                if (values == null || values.Count < period)
                    return null;

                var recentValues = values.Skip(Math.Max(0, values.Count - period)).ToList();
                double mean = recentValues.Average();
                
                double sumOfSquares = recentValues.Sum(x => Math.Pow(x - mean, 2));
                return Math.Sqrt(sumOfSquares / period);
            }
            catch (Exception ex)
            {
                LogToBridge("ERROR", "INDICATOR", $"[IndicatorCalculator] Error calculating standard deviation: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Calculate Bollinger Bands
        /// </summary>
        /// <param name="quotes">List of Quote data</param>
        /// <param name="period">Period for calculation</param>
        /// <param name="standardDeviations">Number of standard deviations for bands</param>
        /// <returns>Tuple of (upper band, middle band, lower band) or null if insufficient data</returns>
        public static (double? Upper, double? Middle, double? Lower) CalculateBollingerBands(
            List<Quote> quotes, int period, double standardDeviations = 2.0)
        {
            try
            {
                if (quotes == null || quotes.Count < period)
                    return (null, null, null);

                var closes = quotes.Select(q => q.Close).ToList();
                double? sma = CalculateSma(closes, period);
                double? stdDev = CalculateStandardDeviation(closes, period);

                if (!sma.HasValue || !stdDev.HasValue)
                    return (null, null, null);

                double offset = stdDev.Value * standardDeviations;
                return (sma + offset, sma, sma - offset);
            }
            catch (Exception ex)
            {
                LogToBridge("ERROR", "INDICATOR", $"[IndicatorCalculator] Error calculating Bollinger Bands: {ex.Message}");
                return (null, null, null);
            }
        }

        /// <summary>
        /// Calculate price momentum
        /// </summary>
        /// <param name="quotes">List of Quote data</param>
        /// <param name="period">Period for momentum calculation</param>
        /// <returns>Momentum value or null if insufficient data</returns>
        public static double? CalculateMomentum(List<Quote> quotes, int period)
        {
            try
            {
                if (quotes == null || quotes.Count < period + 1)
                    return null;

                double currentPrice = quotes.Last().Close;
                double periodAgoPrice = quotes[quotes.Count - period - 1].Close;

                return ((currentPrice - periodAgoPrice) / periodAgoPrice) * 100;
            }
            catch (Exception ex)
            {
                LogToBridge("ERROR", "INDICATOR", $"[IndicatorCalculator] Error calculating momentum: {ex.Message}");
                return null;
            }
        }
        
        /// <summary>
        /// Calculate ATR Percentage (normalized ATR)
        /// </summary>
        /// <param name="quotes">List of Quote data</param>
        /// <param name="period">Period for ATR calculation</param>
        /// <returns>ATR as percentage of price or null if insufficient data</returns>
        public static double? CalculateAtrPercentage(List<Quote> quotes, int period)
        {
            try
            {
                double? atr = CalculateAtr(quotes, period);
                if (!atr.HasValue || quotes == null || quotes.Count == 0)
                    return null;
                
                double currentPrice = quotes.Last().Close;
                if (currentPrice <= 0)
                    return null;
                
                // ATRp = (ATR / Price) × 100
                return (atr.Value / currentPrice) * 100;
            }
            catch (Exception ex)
            {
                LogToBridge("ERROR", "INDICATOR", $"[IndicatorCalculator] Error calculating ATR percentage: {ex.Message}");
                return null;
            }
        }
        
        /// <summary>
        /// Calculate True Range values for the entire series
        /// </summary>
        /// <param name="quotes">List of Quote data</param>
        /// <returns>List of True Range values or null if insufficient data</returns>
        public static List<double> CalculateTrueRanges(List<Quote> quotes)
        {
            try
            {
                if (quotes == null || quotes.Count < 2)
                    return null;
                
                var trueRanges = new List<double>();
                
                // First bar has no previous close, so TR = High - Low
                trueRanges.Add(quotes[0].High - quotes[0].Low);
                
                // Calculate True Range for remaining bars
                for (int i = 1; i < quotes.Count; i++)
                {
                    var current = quotes[i];
                    var previous = quotes[i - 1];
                    
                    double tr1 = current.High - current.Low;
                    double tr2 = Math.Abs(current.High - previous.Close);
                    double tr3 = Math.Abs(current.Low - previous.Close);
                    
                    trueRanges.Add(Math.Max(tr1, Math.Max(tr2, tr3)));
                }
                
                return trueRanges;
            }
            catch (Exception ex)
            {
                LogToBridge("ERROR", "INDICATOR", $"[IndicatorCalculator] Error calculating True Ranges: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Validate quote data for indicator calculations
        /// </summary>
        /// <param name="quotes">List of Quote data to validate</param>
        /// <param name="minBars">Minimum required bars</param>
        /// <returns>True if data is valid for calculations</returns>
        public static bool ValidateQuoteData(List<Quote> quotes, int minBars)
        {
            try
            {
                if (quotes == null || quotes.Count < minBars)
                    return false;

                // Check for null or invalid data
                foreach (var quote in quotes.Skip(Math.Max(0, quotes.Count - minBars)))
                {
                    if (quote.High <= 0 || quote.Low <= 0 || quote.Close <= 0 ||
                        quote.High < quote.Low || quote.Close < 0)
                    {
                        return false;
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}