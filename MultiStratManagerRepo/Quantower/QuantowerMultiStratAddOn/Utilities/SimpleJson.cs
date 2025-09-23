using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

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

            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            return SerializeValue(obj, visited);
        }

        public static T DeserializeObject<T>(string json) where T : new()
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return default;
            }

            if (typeof(T) == typeof(Dictionary<string, object?>))
            {
                return (T)(object)ParseDictionary(json);
            }

            throw new NotSupportedException($"SimpleJson only supports deserializing to Dictionary<string, object?>. Requested type: {typeof(T).FullName}");
        }

        private static string SerializeValue(object? value, HashSet<object> visited)
        {
            if (value == null)
            {
                return "null";
            }

            switch (value)
            {
                case string s:
                    return SerializeString(s);
                case bool b:
                    return b ? "true" : "false";
                case DateTime dt:
                    return SerializeString(dt.ToString("o", CultureInfo.InvariantCulture));
                case DateTimeOffset dto:
                    return SerializeString(dto.ToString("o", CultureInfo.InvariantCulture));
                case Enum e:
                    return SerializeString(Convert.ToString(e, CultureInfo.InvariantCulture) ?? e.ToString());
                case short or ushort or int or uint or long or ulong or float or double or decimal or byte or sbyte:
                    return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "0";
                case IDictionary dictionary:
                    return SerializeDictionary(dictionary, visited);
                case IEnumerable enumerable when value is not string:
                    return SerializeEnumerable(enumerable, visited);
                default:
                    return SerializeComplexObject(value, visited);
            }
        }

        private static string SerializeDictionary(IDictionary dictionary, HashSet<object> visited)
        {
            if (!visited.Add(dictionary))
            {
                return SerializeString("[Circular]");
            }

            try
            {
                var pairs = new List<string>();
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Key is not string key)
                    {
                        continue;
                    }

                    pairs.Add($"{SerializeString(key)}:{SerializeValue(entry.Value, visited)}");
                }

                return "{" + string.Join(",", pairs) + "}";
            }
            finally
            {
                visited.Remove(dictionary);
            }
        }

        private static string SerializeEnumerable(IEnumerable enumerable, HashSet<object> visited)
        {
            if (!visited.Add(enumerable))
            {
                return SerializeString("[Circular]");
            }

            try
            {
                var elements = new List<string>();
                foreach (var item in enumerable)
                {
                    elements.Add(SerializeValue(item, visited));
                }

                return "[" + string.Join(",", elements) + "]";
            }
            finally
            {
                visited.Remove(enumerable);
            }
        }

        private static string SerializeComplexObject(object value, HashSet<object> visited)
        {
            var type = value.GetType();
            var isReferenceType = !type.IsValueType;

            if (isReferenceType && !visited.Add(value))
            {
                return SerializeString("[Circular]");
            }

            try
            {
                var pairs = new List<string>();
                foreach (var property in type.GetProperties())
                {
                    if (!property.CanRead || property.GetIndexParameters().Length > 0)
                    {
                        continue;
                    }

                    var propertyName = property.Name.ToLowerInvariant();
                    var propertyValue = property.GetValue(value);
                    pairs.Add($"{SerializeString(propertyName)}:{SerializeValue(propertyValue, visited)}");
                }

                return "{" + string.Join(",", pairs) + "}";
            }
            finally
            {
                if (isReferenceType)
                {
                    visited.Remove(value);
                }
            }
        }

        private static string SerializeString(string value)
        {
            return "\"" + EscapeJsonString(value ?? string.Empty) + "\"";
        }

        private static string EscapeJsonString(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value ?? string.Empty;
            }

            var builder = new StringBuilder(value.Length + 8);
            foreach (var ch in value)
            {
                switch (ch)
                {
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\"':
                        builder.Append("\\\"");
                        break;
                    case '\b':
                        builder.Append("\\b");
                        break;
                    case '\f':
                        builder.Append("\\f");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (char.IsControl(ch))
                        {
                            builder.Append("\\u");
                            builder.Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(ch);
                        }

                        break;
                }
            }

            return builder.ToString();
        }

        private static Dictionary<string, object?> ParseDictionary(string json)
        {
            var dict = new Dictionary<string, object?>();

            json = json.Trim();
            if (json.Length < 2 || json[0] != '{' || json[^1] != '}')
            {
                return dict;
            }

            var inner = json.Substring(1, json.Length - 2);
            var pairs = SplitTopLevel(inner);

            foreach (var pair in pairs)
            {
                if (string.IsNullOrWhiteSpace(pair))
                {
                    continue;
                }

                var colonIndex = FindFirstUnquotedChar(pair, ':');
                if (colonIndex <= 0)
                {
                    continue;
                }

                var key = pair.Substring(0, colonIndex).Trim();
                if (key.Length >= 2 && key[0] == '"' && key[^1] == '"')
                {
                    key = key.Substring(1, key.Length - 2);
                }

                var valueString = pair.Substring(colonIndex + 1).Trim();
                dict[key] = ParseValue(valueString);
            }

            return dict;
        }

        private static List<string> SplitTopLevel(string json)
        {
            var segments = new List<string>();
            var depth = 0;
            var lastSplit = 0;
            var inString = false;
            var isEscaped = false;

            for (int i = 0; i < json.Length; i++)
            {
                var ch = json[i];

                if (inString)
                {
                    if (isEscaped)
                    {
                        isEscaped = false;
                    }
                    else if (ch == '\\')
                    {
                        isEscaped = true;
                    }
                    else if (ch == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                switch (ch)
                {
                    case '"':
                        inString = true;
                        break;
                    case '{':
                    case '[':
                        depth++;
                        break;
                    case '}':
                    case ']':
                        depth = Math.Max(0, depth - 1);
                        break;
                    case ',':
                        if (depth == 0)
                        {
                            var segment = json.Substring(lastSplit, i - lastSplit).Trim();
                            if (segment.Length > 0)
                            {
                                segments.Add(segment);
                            }

                            lastSplit = i + 1;
                        }
                        break;
                }
            }

            if (lastSplit < json.Length)
            {
                var tail = json.Substring(lastSplit).Trim();
                if (tail.Length > 0)
                {
                    segments.Add(tail);
                }
            }

            return segments;
        }

        private static int FindFirstUnquotedChar(string input, char target)
        {
            var inString = false;
            var isEscaped = false;

            for (int i = 0; i < input.Length; i++)
            {
                var ch = input[i];

                if (inString)
                {
                    if (isEscaped)
                    {
                        isEscaped = false;
                        continue;
                    }

                    if (ch == '\\')
                    {
                        isEscaped = true;
                        continue;
                    }

                    if (ch == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (ch == '"')
                {
                    inString = true;
                    continue;
                }

                if (ch == target)
                {
                    return i;
                }
            }

            return -1;
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

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static ReferenceEqualityComparer Instance { get; } = new();

            public new bool Equals(object x, object y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(object obj)
            {
                return RuntimeHelpers.GetHashCode(obj);
            }
        }
    }
}
