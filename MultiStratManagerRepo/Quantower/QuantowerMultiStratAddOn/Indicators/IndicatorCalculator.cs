using System;
using System.Collections.Generic;
using System.Linq;

namespace Quantower.MultiStrat.Indicators
{
    public class Quote
    {
        public DateTime Date { get; set; }
        public double Open { get; set; }
        public double High { get; set; }
        public double Low { get; set; }
        public double Close { get; set; }
        public long Volume { get; set; }
    }

    public static class IndicatorCalculator
    {
        public static double? CalculateDema(List<Quote> quotes, int period)
        {
            try
            {
                if (period <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(period), period, "DEMA period must be greater than zero.");
                }
                if (quotes == null)
                {
                    return null;
                }

                int minRequired = (2 * period) - 1;
                if (quotes.Count < minRequired)
                {
                    return null;
                }

                var closePrices = quotes.Select(q => q.Close).ToList();
                var ema1Values = CalculateEma(closePrices, period);
                if (ema1Values == null || ema1Values.Count == 0)
                {
                    return null;
                }

                var ema2Values = CalculateEma(ema1Values, period);
                if (ema2Values == null || ema2Values.Count == 0)
                {
                    return null;
                }

                double currentEma1 = ema1Values.Last();
                double currentEma2 = ema2Values.Last();

                return (2.0 * currentEma1) - currentEma2;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[QT][IndicatorCalculator] DEMA calculation failed: {ex.Message}");
                return null;
            }
        }

        public static double? CalculateAtr(List<Quote> quotes, int period)
        {
            try
            {
                if (period <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(period), period, "ATR period must be greater than zero.");
                }
                if (quotes == null || quotes.Count < period + 1)
                {
                    return null;
                }

                var trueRanges = new List<double>();
                for (int i = 1; i < quotes.Count; i++)
                {
                    var current = quotes[i];
                    var previous = quotes[i - 1];

                    double tr1 = current.High - current.Low;
                    double tr2 = Math.Abs(current.High - previous.Close);
                    double tr3 = Math.Abs(current.Low - previous.Close);

                    trueRanges.Add(Math.Max(tr1, Math.Max(tr2, tr3)));
                }

                if (trueRanges.Count == 0)
                {
                    return null;
                }

                if (trueRanges.Count < period)
                {
                    return trueRanges.Average();
                }

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
                Console.WriteLine($"[QT][IndicatorCalculator] ATR calculation failed: {ex.Message}");
                return null;
            }
        }

        public static List<double> CalculateEma(List<double> values, int period)
        {
            try
            {
                if (period <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(period), period, "EMA period must be greater than zero.");
                }
                if (values == null || values.Count < period)
                {
                    return new List<double>();
                }

                var emaValues = new List<double>();
                double multiplier = 2.0 / (period + 1);
                double sma = values.Take(period).Average();
                emaValues.Add(sma);

                for (int i = period; i < values.Count; i++)
                {
                    double ema = ((values[i] - emaValues.Last()) * multiplier) + emaValues.Last();
                    emaValues.Add(ema);
                }

                return emaValues;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[QT][IndicatorCalculator] EMA calculation failed: {ex.Message}");
                return new List<double>();
            }
        }
    }
}
