using System;
using TradingPlatform.BusinessLayer;

namespace Quantower.MultiStrat.Utilities
{
    internal static class PnLUtils
    {
        public static double GetMoney(PnLItem? item)
        {
            if (item == null)
            {
                return 0.0;
            }

            if (double.IsFinite(item.Value))
            {
                return item.Value;
            }

            var valueProp = item.GetType().GetProperty("Value");
            if (valueProp != null)
            {
                var value = valueProp.GetValue(item);
                return value switch
                {
                    double d => d,
                    decimal dec => (double)dec,
                    _ => 0.0
                };
            }

            return 0.0;
        }
    }
}
