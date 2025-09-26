using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
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

            var valueObject = (object?)item.Value;
            return ConvertToDouble(valueObject, null);
        }

        private static double ConvertToDouble(object? value, HashSet<object>? visited)
        {
            bool addedToVisited = false;
            if (value != null && !value.GetType().IsValueType)
            {
                visited ??= new HashSet<object>(ReferenceObjectComparer.Instance);
                if (!visited.Add(value))
                {
                    return 0.0;
                }
                addedToVisited = true;
            }

            try
            {
                switch (value)
            {
                case null:
                    return 0.0;
                case double d:
                    return double.IsNaN(d) || double.IsInfinity(d) ? 0.0 : d;
                case float f:
                    return float.IsNaN(f) || float.IsInfinity(f) ? 0.0 : f;
                case decimal dec:
                    return (double)dec;
                case string s:
                    if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                    {
                        return double.IsNaN(parsed) || double.IsInfinity(parsed) ? 0.0 : parsed;
                    }
                    return 0.0;
                case IConvertible convertible:
                    try
                    {
                        if (convertible is string convertibleString)
                        {
                            return double.TryParse(convertibleString, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedConvertible) && !double.IsNaN(parsedConvertible) && !double.IsInfinity(parsedConvertible)
                                ? parsedConvertible
                                : 0.0;
                        }

                        var converted = Convert.ToDouble(convertible, CultureInfo.InvariantCulture);
                        return double.IsNaN(converted) || double.IsInfinity(converted) ? 0.0 : converted;
                    }
                    catch
                    {
                        return 0.0;
                    }
                default:
                {
                    var type = value.GetType();
                    var moneyProperty = type.GetProperty("Money");
                    if (moneyProperty != null)
                    {
                        var moneyValue = moneyProperty.GetValue(value);
                        if (moneyValue != null && !ReferenceEquals(moneyValue, value) && moneyValue.GetType() != type)
                        {
                            return ConvertToDouble(moneyValue, visited);
                        }
                    }

                    var valueProperty = type.GetProperty("Value");
                    if (valueProperty != null)
                    {
                        var coreValue = valueProperty.GetValue(value);
                        if (coreValue != null && coreValue.GetType() != type)
                        {
                            return ConvertToDouble(coreValue, visited);
                        }
                    }

                    var amountProperty = type.GetProperty("Amount");
                    if (amountProperty != null)
                    {
                        var amountValue = amountProperty.GetValue(value);
                        if (amountValue != null && amountValue.GetType() != type)
                        {
                            return ConvertToDouble(amountValue, visited);
                        }
                    }

                    if (value is System.Collections.IEnumerable enumerable && !(value is string))
                    {
                        double sum = 0.0;
                        foreach (var element in enumerable)
                        {
                            sum += ConvertToDouble(element, visited);
                        }
                        return sum;
                    }

                    return 0.0;
                }
            }
            finally
            {
                if (addedToVisited && visited != null)
                {
                    visited.Remove(value);
                }
            }
        }

        private sealed class ReferenceObjectComparer : IEqualityComparer<object>
        {
            public static ReferenceObjectComparer Instance { get; } = new ReferenceObjectComparer();

            public bool Equals(object? x, object? y) => ReferenceEquals(x, y);

            public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}
