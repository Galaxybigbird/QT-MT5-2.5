using System;
using System.Collections.Generic;
using System.IO;
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
                return JsonSerializer.Deserialize<Dictionary<string, object?>>(payload) ?? new Dictionary<string, object?>();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[QT][SettingsRepository] Failed to load settings: {ex.Message}");
                return new Dictionary<string, object?>();
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
                Console.WriteLine($"[QT][SettingsRepository] Failed to save settings: {ex.Message}");
            }
        }

        private static string GetDefaultPath()
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Quantower", "MultiStrat");
            return Path.Combine(folder, "settings.json");
        }
    }
}
