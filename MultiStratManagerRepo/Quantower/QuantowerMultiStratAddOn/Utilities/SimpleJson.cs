using System;
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
                            return JsonSerializer.Deserialize<object?[]>(doc.RootElement.GetRawText(), options);
                        }
                    case JsonTokenType.StartObject:
                        {
                            using var doc = JsonDocument.ParseValue(ref reader);
                            return JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, object?>>(doc.RootElement.GetRawText(), options);
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
                        JsonSerializer.Serialize(writer, value, value.GetType(), options);
                        break;
                }
            }
        }
    }
}
