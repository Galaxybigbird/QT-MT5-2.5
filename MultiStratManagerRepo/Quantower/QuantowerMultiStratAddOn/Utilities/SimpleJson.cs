using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quantower.MultiStrat.Utilities
{
    public static class SimpleJson
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
            Converters =
            {
                new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true),
                new ObjectToInferredTypesConverter()
            }
        };

        public static string SerializeObject(object obj)
        {
            if (obj == null)
            {
                return "null";
            }

            return JsonSerializer.Serialize(obj, obj.GetType(), Options);
        }

        public static T? DeserializeObject<T>(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return default;
            }

            return JsonSerializer.Deserialize<T>(json, Options);
        }

        private static JsonSerializerOptions WithoutInferredTypesConverter(JsonSerializerOptions options)
        {
            var clone = new JsonSerializerOptions(options);
            for (int i = clone.Converters.Count - 1; i >= 0; i--)
            {
                if (clone.Converters[i] is ObjectToInferredTypesConverter)
                {
                    clone.Converters.RemoveAt(i);
                }
            }

            return clone;
        }

        private sealed class ObjectToInferredTypesConverter : JsonConverter<object?>
        {
            public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.Null:
                        return null;
                    case JsonTokenType.True:
                        return true;
                    case JsonTokenType.False:
                        return false;
                    case JsonTokenType.Number:
                        if (reader.TryGetInt64(out var l))
                        {
                            return l;
                        }

                        if (reader.TryGetDecimal(out var decimalValue))
                        {
                            return decimalValue;
                        }

                        return reader.GetDouble();
                    case JsonTokenType.String:
                        if (reader.TryGetDateTime(out var datetime))
                        {
                            return datetime;
                        }

                        if (reader.TryGetDateTimeOffset(out var dto))
                        {
                            return dto;
                        }

                        return reader.GetString();
                    case JsonTokenType.StartArray:
                    {
                        using var doc = JsonDocument.ParseValue(ref reader);
                        return ConvertElement(doc.RootElement);
                    }
                    case JsonTokenType.StartObject:
                    {
                        using var doc = JsonDocument.ParseValue(ref reader);
                        return ConvertElement(doc.RootElement);
                    }
                    default:
                        throw new JsonException($"Unsupported token type {reader.TokenType}");
                }
            }

            public override void Write(Utf8JsonWriter writer, object? value, JsonSerializerOptions options)
            {
                switch (value)
                {
                    case null:
                        writer.WriteNullValue();
                        break;
                    case bool b:
                        writer.WriteBooleanValue(b);
                        break;
                    case string s:
                        writer.WriteStringValue(s);
                        break;
                    case short s16:
                        writer.WriteNumberValue(s16);
                        break;
                    case ushort u16:
                        writer.WriteNumberValue(u16);
                        break;
                    case uint u32:
                        writer.WriteNumberValue(u32);
                        break;
                    case ulong u64:
                        writer.WriteNumberValue(u64);
                        break;
                    case byte b8:
                        writer.WriteNumberValue(b8);
                        break;
                    case sbyte sb8:
                        writer.WriteNumberValue(sb8);
                        break;
                    case DateTime dt:
                        writer.WriteStringValue(dt);
                        break;
                    case DateTimeOffset dto:
                        writer.WriteStringValue(dto);
                        break;
                    case Guid guid:
                        writer.WriteStringValue(guid);
                        break;
                    case int i:
                        writer.WriteNumberValue(i);
                        break;
                    case long l:
                        writer.WriteNumberValue(l);
                        break;
                    case double d:
                        writer.WriteNumberValue(d);
                        break;
                    case float f:
                        writer.WriteNumberValue(f);
                        break;
                    case decimal dec:
                        writer.WriteNumberValue(dec);
                        break;
                    case JsonElement element:
                        element.WriteTo(writer);
                        break;
                    default:
                        var clean = WithoutInferredTypesConverter(options);
                        JsonSerializer.Serialize(writer, value, value.GetType(), clean);
                        break;
                }
            }

            private static object? ConvertElement(JsonElement element)
            {
                switch (element.ValueKind)
                {
                    case JsonValueKind.Null:
                        return null;
                    case JsonValueKind.True:
                        return true;
                    case JsonValueKind.False:
                        return false;
                    case JsonValueKind.Number:
                        if (element.TryGetInt64(out var intValue))
                        {
                            return intValue;
                        }

                        if (element.TryGetDecimal(out var decimalValue))
                        {
                            return decimalValue;
                        }

                        if (element.TryGetDouble(out var doubleValue))
                        {
                            return doubleValue;
                        }

                        return null;
                    case JsonValueKind.String:
                        if (element.TryGetDateTime(out var dateTime))
                        {
                            return dateTime;
                        }

                        if (element.TryGetDateTimeOffset(out var dto))
                        {
                            return dto;
                        }

                        return element.GetString();
                    case JsonValueKind.Array:
                    {
                        var list = new List<object?>(element.GetArrayLength());
                        foreach (var item in element.EnumerateArray())
                        {
                            list.Add(ConvertElement(item));
                        }

                        return list;
                    }
                    case JsonValueKind.Object:
                    {
                        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
                        foreach (var property in element.EnumerateObject())
                        {
                            dict[property.Name] = ConvertElement(property.Value);
                        }

                        return dict;
                    }
                    default:
                        return null;
                }
            }
        }
    }
}
