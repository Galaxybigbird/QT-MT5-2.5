using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Text.Json;

namespace Quantower.MultiStrat.Persistence
{
    public sealed class SettingsRepository
    {
        private readonly string _settingsPath;

        public SettingsRepository(string? customPath = null)
        {
            _settingsPath = customPath ?? GetDefaultPath();
        }

        public Dictionary<string, object?> Load()
        {
            try
            {
                if (!File.Exists(_settingsPath))
                {
                    return new Dictionary<string, object?>();
                }

                var payload = File.ReadAllText(_settingsPath);
                using var document = JsonDocument.Parse(payload);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException($"Settings file '{_settingsPath}' must contain a JSON object at the root.");
                }

                var result = new Dictionary<string, object?>();
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    result[property.Name] = ConvertElement(property.Value);
                }

                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[QT][SettingsRepository] Failed to load settings: {ex}");
                throw;
            }
        }

        public void Save(Dictionary<string, object?> settings)
        {
            try
            {
                var directory = Path.GetDirectoryName(_settingsPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var payload = JsonSerializer.Serialize(settings, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                File.WriteAllText(_settingsPath, payload);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[QT][SettingsRepository] Failed to save settings: {ex}");
                throw;
            }
        }

        private static string GetDefaultPath()
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Quantower", "MultiStrat");
            return Path.Combine(folder, "settings.json");
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
                    if (element.TryGetInt32(out var i32))
                    {
                        return i32;
                    }

                    if (element.TryGetInt64(out var i64))
                    {
                        return i64;
                    }

                    if (element.TryGetDecimal(out var dec))
                    {
                        return dec;
                    }

                    if (element.TryGetDouble(out var dbl))
                    {
                        return dbl;
                    }

                    return element.Clone();
                case JsonValueKind.String:
                {
                    var str = element.GetString();
                    if (string.IsNullOrEmpty(str))
                    {
                        return str;
                    }

                    if (DateTimeOffset.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var dto))
                    {
                        return dto;
                    }

                    if (DateTime.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dt))
                    {
                        if (dt.Kind == DateTimeKind.Unspecified)
                        {
                            dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
                        }
                        return dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
                    }

                    if (Guid.TryParse(str, out var guid))
                    {
                        return guid;
                    }

                    return str;
                }
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
                    var dict = new Dictionary<string, object?>();
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
