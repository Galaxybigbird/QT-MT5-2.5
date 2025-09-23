using System;
using System.Collections.Generic;
using System.Globalization;

namespace Quantower.MultiStrat.Utilities
{
    public static class SimpleJson
    {
        public static string SerializeObject(object obj)
        {
            if (obj == null)
            {
                return "null";
            }

            if (obj is Dictionary<string, object?> dict)
            {
                var pairs = new List<string>();
                foreach (var kvp in dict)
                {
                    pairs.Add($"\"{kvp.Key}\":{SerializeValue(kvp.Value)}");
                }

                return "{" + string.Join(",", pairs) + "}";
            }

            var properties = obj.GetType().GetProperties();
            var jsonPairs = new string[properties.Length];

            for (int i = 0; i < properties.Length; i++)
            {
                var property = properties[i];
                var value = property.GetValue(obj);
                jsonPairs[i] = $"\"{property.Name.ToLowerInvariant()}\":{SerializeValue(value)}";
            }

            return "{" + string.Join(",", jsonPairs) + "}";
        }

        public static T DeserializeObject<T>(string json) where T : new()
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return default;
            }

            var type = typeof(T);
            if (type == typeof(Dictionary<string, object?>))
            {
                return (T)(object)ParseDictionary(json);
            }

            return new T();
        }

        private static Dictionary<string, object?> ParseDictionary(string json)
        {
            var dict = new Dictionary<string, object?>();

            json = json.Trim();
            if (!json.StartsWith("{") || !json.EndsWith("}"))
            {
                return dict;
            }

            json = json.Substring(1, json.Length - 2).Trim();
            var pairs = new List<string>();
            int depth = 0;
            int lastSplit = 0;

            for (int i = 0; i < json.Length; i++)
            {
                switch (json[i])
                {
                    case '{':
                    case '[':
                        depth++;
                        break;
                    case '}':
                    case ']':
                        depth--;
                        break;
                    case ',':
                        if (depth == 0)
                        {
                            pairs.Add(json.Substring(lastSplit, i - lastSplit).Trim());
                            lastSplit = i + 1;
                        }
                        break;
                }
            }

            pairs.Add(json.Substring(lastSplit).Trim());

            foreach (var pair in pairs)
            {
                if (string.IsNullOrWhiteSpace(pair))
                {
                    continue;
                }

                var colonIndex = pair.IndexOf(':');
                if (colonIndex <= 0)
                {
                    continue;
                }

                var key = pair.Substring(0, colonIndex).Trim();
                if (key.StartsWith("\"") && key.EndsWith("\""))
                {
                    key = key.Substring(1, key.Length - 2);
                }

                var valueString = pair.Substring(colonIndex + 1).Trim();
                dict[key] = ParseValue(valueString);
            }

            return dict;
        }

        private static object? ParseValue(string value)
        {
            if (value.StartsWith("\"") && value.EndsWith("\""))
            {
                return value.Substring(1, value.Length - 2);
            }

            if (value.Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (value.Equals("false", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (value.Equals("null", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
            {
                return number;
            }

            return value;
        }

        private static string SerializeValue(object? value)
        {
            if (value == null)
            {
                return "null";
            }

            switch (value)
            {
                case string s:
                    return $"\"{s.Replace("\"", "\\\"")}\"";
                case bool b:
                    return b ? "true" : "false";
                case DateTime dt:
                    return $"\"{dt:o}\"";
                case int or long or float or double or decimal:
                    return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "0";
                case Dictionary<string, object?> nestedDict:
                    return SerializeObject(nestedDict);
                case System.Collections.IEnumerable enumerable when value is not string:
                    var elements = new List<string>();
                    foreach (var item in enumerable)
                    {
                        elements.Add(SerializeValue(item));
                    }
                    return "[" + string.Join(",", elements) + "]";
                default:
                    if (value.GetType().IsClass)
                    {
                        return SerializeObject(value);
                    }

                    return $"\"{value.ToString()?.Replace("\"", "\\\"")}\"";
            }
        }
    }
}
