using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace VibeModel.Infrastructure
{
    public static class SettingsManager
    {
        private static readonly string SettingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VibeModel");

        private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

        private static readonly object Lock = new object();
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        private static Dictionary<string, object> _cache;

        public static string GetApiKey()
        {
            var settings = Load();
            object val;
            if (settings.TryGetValue("AnthropicApiKey", out val) && val is string key && !string.IsNullOrWhiteSpace(key))
                return key;
            return null;
        }

        public static void SetApiKey(string key)
        {
            var settings = Load();
            settings["AnthropicApiKey"] = key ?? "";
            Save(settings);
        }

        public static string GetModel()
        {
            var settings = Load();
            object val;
            if (settings.TryGetValue("Model", out val) && val is string model && !string.IsNullOrWhiteSpace(model))
                return model;
            return "claude-sonnet-4-5-20250929";
        }

        public static string GetPreferredBackend()
        {
            var settings = Load();
            object val;
            if (settings.TryGetValue("PreferredBackend", out val) && val is string backend)
                return backend;
            return "auto";
        }

        public static void SetPreferredBackend(string backend)
        {
            var settings = Load();
            settings["PreferredBackend"] = backend ?? "auto";
            Save(settings);
        }

        public static string GetLocalLlmEndpoint()
        {
            var settings = Load();
            object val;
            if (settings.TryGetValue("LocalLlmEndpoint", out val) && val is string ep && !string.IsNullOrWhiteSpace(ep))
                return ep;
            return "http://localhost:8080";
        }

        public static void SetLocalLlmEndpoint(string endpoint)
        {
            var settings = Load();
            settings["LocalLlmEndpoint"] = endpoint ?? "";
            Save(settings);
        }

        public static string GetLocalLlmModel()
        {
            var settings = Load();
            object val;
            if (settings.TryGetValue("LocalLlmModel", out val) && val is string model)
                return model;
            return "";
        }

        public static void SetLocalLlmModel(string model)
        {
            var settings = Load();
            settings["LocalLlmModel"] = model ?? "";
            Save(settings);
        }

        public static bool GetLocalLlmToolUse()
        {
            var settings = Load();
            object val;
            if (settings.TryGetValue("LocalLlmToolUse", out val))
            {
                if (val is bool b) return b;
                if (val is string s) return s.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        public static void SetLocalLlmToolUse(bool enabled)
        {
            var settings = Load();
            settings["LocalLlmToolUse"] = enabled;
            Save(settings);
        }

        public static int GetLocalLlmTimeout()
        {
            var settings = Load();
            object val;
            if (settings.TryGetValue("LocalLlmTimeout", out val))
            {
                if (val is int i) return Math.Max(30, Math.Min(300, i));
                if (val is string s && int.TryParse(s, out int parsed))
                    return Math.Max(30, Math.Min(300, parsed));
            }
            return 120;
        }

        public static void SetLocalLlmTimeout(int seconds)
        {
            var settings = Load();
            settings["LocalLlmTimeout"] = Math.Max(30, Math.Min(300, seconds));
            Save(settings);
        }

        // --- Office standards (plan 11) --------------------------------------------------

        /// <summary>
        /// Folder holding a standards pack (pack.json) or several pack folders. Empty = only the
        /// default locations (%LOCALAPPDATA%\VibeModel\standards, add-in folder) are searched.
        /// Point it at the office share to make every modeller use the same pack.
        /// </summary>
        public static string GetStandardsPackPath() => GetString("StandardsPackPath", "");

        public static void SetStandardsPackPath(string path) => SetValue("StandardsPackPath", path?.Trim() ?? "");

        /// <summary>Folder with the office's source documents (leidraad PDFs, checklists, .txt).</summary>
        public static string GetStandardsFolder() => GetString("StandardsFolder", "");

        public static void SetStandardsFolder(string path) => SetValue("StandardsFolder", path?.Trim() ?? "");

        /// <summary>Master switch: when off no pack is ever active (no rules, no defaults, no checks).</summary>
        public static bool GetStandardsEnabled()
        {
            var settings = Load();
            object val;
            if (settings.TryGetValue("StandardsEnabled", out val))
            {
                if (val is bool b) return b;
                if (val is string s) return !s.Equals("false", StringComparison.OrdinalIgnoreCase);
            }
            return true;
        }

        public static void SetStandardsEnabled(bool enabled) => SetValue("StandardsEnabled", enabled);

        private static string GetString(string key, string fallback)
        {
            var settings = Load();
            object val;
            if (settings.TryGetValue(key, out val) && val is string s)
                return s;
            return fallback;
        }

        private static void SetValue(string key, object value)
        {
            var settings = Load();
            settings[key] = value;
            Save(settings);
        }

        public static Dictionary<string, object> Load()
        {
            lock (Lock)
            {
                if (_cache != null)
                    return new Dictionary<string, object>(_cache, StringComparer.OrdinalIgnoreCase);

                try
                {
                    if (File.Exists(SettingsPath))
                    {
                        var json = File.ReadAllText(SettingsPath);
                        var result = Json.Deserialize<Dictionary<string, object>>(json);
                        if (result != null)
                        {
                            _cache = result;
                            return new Dictionary<string, object>(result, StringComparer.OrdinalIgnoreCase);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("Failed to load settings", ex);
                }

                _cache = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            }
        }

        public static void Save(Dictionary<string, object> settings)
        {
            lock (Lock)
            {
                try
                {
                    Directory.CreateDirectory(SettingsDir);
                    var json = Json.Serialize(settings);
                    File.WriteAllText(SettingsPath, json);
                    _cache = new Dictionary<string, object>(settings, StringComparer.OrdinalIgnoreCase);
                }
                catch (Exception ex)
                {
                    Logger.Error("Failed to save settings", ex);
                }
            }
        }

        public static void ClearCache()
        {
            lock (Lock)
            {
                _cache = null;
            }
        }
    }
}
