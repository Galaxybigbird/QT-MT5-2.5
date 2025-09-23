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

            var valueObject = (object)item.Value;

            return valueObject switch
            {
                double d when double.IsFinite(d) => d,
                double _ => 0.0,
                decimal dec => (double)dec,
                _ => 0.0
            };
        }
    }
}
