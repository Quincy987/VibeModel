using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace VibeModel.Infrastructure
{
    /// <summary>Where the API key in use came from (shown masked in Settings and Diagnose).</summary>
    public enum ApiKeySource { None, Managed, Environment, Settings }

    public static class SettingsManager
    {
        private static string SettingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VibeModel");

        private static string SettingsPath = Path.Combine(SettingsDir, "settings.json");

        private static readonly object Lock = new object();
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        private static Dictionary<string, object> _cache;

        /// <summary>The model the direct API backend uses when nobody chose one.</summary>
        public const string DefaultModel = "claude-sonnet-5-5";

        /// <summary>The Anthropic API; a gateway that speaks the Messages API can replace it (ApiBaseUrl).</summary>
        public const string DefaultApiBaseUrl = "https://api.anthropic.com";

        /// <summary>The user's settings.json (Diagnose names it).</summary>
        public static string FilePath => SettingsPath;

        // --- API key: DPAPI at rest, never plain text on disk ---------------------------------

        // Older builds (and the old installer) wrote the key in plain text under this name.
        private const string LegacyKeyName = "AnthropicApiKey";
        private const string ProtectedKeyName = "AnthropicApiKeyProtected";
        private static readonly byte[] KeyEntropy = Encoding.UTF8.GetBytes("VibeModel.AnthropicApiKey.v1");

        public static string GetApiKey() => GetApiKey(out _);

        /// <summary>
        /// The key to use, first match wins: IT's managed.json, the ANTHROPIC_API_KEY environment
        /// variable, then the user's own key (DPAPI-encrypted for this Windows user). A plain-text key
        /// left by an older build is encrypted on first read and removed from settings.json.
        /// </summary>
        public static string GetApiKey(out ApiKeySource source)
        {
            var managed = ManagedSettings.GetString(ManagedSettings.AnthropicApiKey);
            if (managed != null) { source = ApiKeySource.Managed; return managed; }

            var env = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
            if (!string.IsNullOrWhiteSpace(env)) { source = ApiKeySource.Environment; return env.Trim(); }

            var settings = Load();
            object val;
            if (settings.TryGetValue(LegacyKeyName, out val) && val is string legacy && !string.IsNullOrWhiteSpace(legacy))
            {
                SetApiKey(legacy.Trim());
                Logger.Info("API key moved from plain text to DPAPI-encrypted storage (" + MaskKey(legacy) + ")");
                source = ApiKeySource.Settings;
                return legacy.Trim();
            }

            if (settings.TryGetValue(ProtectedKeyName, out val) && val is string blob && !string.IsNullOrWhiteSpace(blob))
            {
                var key = Unprotect(blob);
                if (key != null) { source = ApiKeySource.Settings; return key; }
            }

            source = ApiKeySource.None;
            return null;
        }

        /// <summary>Stores the user's key encrypted (DPAPI, this Windows user only); empty clears it.</summary>
        public static void SetApiKey(string key)
        {
            var settings = Load();
            settings.Remove(LegacyKeyName);
            if (string.IsNullOrWhiteSpace(key)) settings.Remove(ProtectedKeyName);
            else settings[ProtectedKeyName] = Protect(key.Trim());
            Save(settings);
        }

        internal static string Protect(string plain)
        {
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), KeyEntropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(bytes);
        }

        private static bool _loggedUnprotectFailure;

        internal static string Unprotect(string blob)
        {
            try
            {
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(blob), KeyEntropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (Exception ex)
            {
                // Another Windows user's (or another machine's) blob: unreadable here, so no key.
                // Logged once: the pane asks for the key on every availability check.
                if (!_loggedUnprotectFailure)
                    Logger.Warn("Stored API key could not be decrypted for this Windows user: " + ex.Message);
                _loggedUnprotectFailure = true;
                return null;
            }
        }

        /// <summary>"sk-ant-…1234" — enough to recognise a key, never enough to use it.</summary>
        public static string MaskKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return "";
            var k = key.Trim();
            if (k.Length <= 12) return new string('*', k.Length);
            var prefixEnd = k.StartsWith("sk-ant-", StringComparison.Ordinal) ? 7 : 3;
            return k.Substring(0, prefixEnd) + "…" + k.Substring(k.Length - 4);
        }

        // --- Model, endpoint, backend (managed.json overrides settings.json) ------------------

        /// <summary>IT's value for <paramref name="key"/>, else the user's non-empty one, else the fallback.</summary>
        private static string ManagedOrSetting(string key, string fallback)
        {
            var managed = ManagedSettings.GetString(key);
            if (managed != null) return managed;
            var configured = GetString(key, "");
            return string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim();
        }

        public static string GetModel() => ManagedOrSetting(ManagedSettings.Model, DefaultModel);

        /// <summary>The model the user chose; empty = the default (<see cref="DefaultModel"/>).</summary>
        public static string GetModelSetting() => GetString("Model", "");

        public static void SetModel(string model)
        {
            var settings = Load();
            if (string.IsNullOrWhiteSpace(model)) settings.Remove("Model");
            else settings["Model"] = model.Trim();
            Save(settings);
        }

        /// <summary>The Messages API host: managed.json, settings.json, else api.anthropic.com.</summary>
        public static string GetApiBaseUrl() => ManagedOrSetting(ManagedSettings.ApiBaseUrl, DefaultApiBaseUrl);

        public static void SetApiBaseUrl(string url)
        {
            var settings = Load();
            if (string.IsNullOrWhiteSpace(url) || url.Trim().TrimEnd('/') == DefaultApiBaseUrl) settings.Remove("ApiBaseUrl");
            else settings["ApiBaseUrl"] = url.Trim();
            Save(settings);
        }

        /// <summary>
        /// The full Messages URL for a base: "https://api.anthropic.com" → ".../v1/messages"; a base
        /// that already ends in /v1 or /v1/messages is completed, not doubled.
        /// </summary>
        public static string MessagesUrl(string baseUrl)
        {
            var b = (string.IsNullOrWhiteSpace(baseUrl) ? DefaultApiBaseUrl : baseUrl.Trim()).TrimEnd('/');
            if (b.EndsWith("/v1/messages", StringComparison.OrdinalIgnoreCase)) return b;
            if (b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) return b + "/messages";
            return b + "/v1/messages";
        }

        /// <summary>IT can switch the Claude Code backend off entirely (managed DisableClaudeCode).</summary>
        public static bool IsClaudeCodeDisabled() => ManagedSettings.GetBool(ManagedSettings.DisableClaudeCode) == true;

        /// <summary>
        /// OpenServer: run the local HTTP server without a token, as before 1.2.11 — for people who
        /// script against it. managed.json decides when IT set it; else settings.json; default false.
        /// An explicit VIBEMODEL_TOKEN still wins over this (see ServerAuth).
        /// </summary>
        public static bool GetOpenServer()
        {
            var managed = ManagedSettings.GetBool(ManagedSettings.OpenServer);
            if (managed.HasValue) return managed.Value;
            var settings = Load();
            object val;
            if (settings.TryGetValue(ManagedSettings.OpenServer, out val))
            {
                if (val is bool b) return b;
                if (val is string s) return s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1";
            }
            return false;
        }

        /// <summary>
        /// Where the Claude Code CLI lives when set explicitly: managed.json, the
        /// VIBEMODEL_CLAUDE_CLI environment variable, then settings.json. "none" switches discovery
        /// off (simulates a laptop without Claude Code). Null = discover it.
        /// </summary>
        public static string GetClaudeCliPath()
        {
            var managed = ManagedSettings.GetString(ManagedSettings.ClaudeCliPath);
            if (managed != null) return managed;
            var env = Environment.GetEnvironmentVariable("VIBEMODEL_CLAUDE_CLI");
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
            var configured = GetString("ClaudeCliPath", "");
            return string.IsNullOrWhiteSpace(configured) ? null : configured.Trim();
        }

        public static string GetPreferredBackend() => ManagedOrSetting(ManagedSettings.PreferredBackend, "auto");

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

        // --- Chat language ------------------------------------------------------------------

        /// <summary>
        /// Chat language: "nl" (default) or "en". Picks the pane's fixed texts and the reply
        /// language the model falls back to when the user's own language is unclear.
        /// </summary>
        public static string GetLanguage() => Services.Chat.Strings.Normalize(
            ManagedOrSetting(ManagedSettings.Language, Services.Chat.Strings.Dutch));

        public static void SetLanguage(string language) => SetValue("Language", Services.Chat.Strings.Normalize(language));

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

        /// <summary>
        /// Project memory + journal (plan 08, project part). Default on; off = no PROJECT MEMORY
        /// block, no memory/journal commands offered, nothing written — the chat as before.
        /// </summary>
        public static bool GetProjectMemoryEnabled()
        {
            var settings = Load();
            object val;
            if (settings.TryGetValue("ProjectMemoryEnabled", out val))
            {
                if (val is bool b) return b;
                if (val is string s) return !s.Equals("false", StringComparison.OrdinalIgnoreCase);
            }
            return true;
        }

        public static void SetProjectMemoryEnabled(bool enabled) => SetValue("ProjectMemoryEnabled", enabled);

        // --- Project folder context (plan 10) ----------------------------------------------

        private const string ProjectFoldersKey = "ProjectContextFolders";

        /// <summary>
        /// The folder the user chose for this project (key = <c>ProjectDocs.ProjectKey</c>), or
        /// null when none is set or the stored folder no longer exists — callers then fall back
        /// to the model's own folder instead of erroring every turn. A folder that does not
        /// answer in time (network drive offline) is returned as-is; listing it reports that.
        /// </summary>
        public static string GetProjectContextFolder(string projectKey)
            => LiveFolderOrNull(GetProjectContextFolderSetting(projectKey));

        internal static string LiveFolderOrNull(string stored)
        {
            if (string.IsNullOrWhiteSpace(stored)) return null;
            return Services.Standards.PathProbe.DirectoryExists(stored) == false ? null : stored;
        }

        /// <summary>The stored entry without the existence check (the Settings dialog shows a stale one).</summary>
        public static string GetProjectContextFolderSetting(string projectKey)
        {
            if (string.IsNullOrWhiteSpace(projectKey)) return null;
            var settings = Load();
            object val;
            if (settings.TryGetValue(ProjectFoldersKey, out val) && val is Dictionary<string, object> map)
            {
                foreach (var kv in map)
                    if (string.Equals(kv.Key, projectKey, StringComparison.OrdinalIgnoreCase) &&
                        kv.Value is string path && !string.IsNullOrWhiteSpace(path))
                        return path.Trim();
            }
            return null;
        }

        /// <summary>Sets (or, with an empty path, clears) the folder for one project only.</summary>
        public static void SetProjectContextFolder(string projectKey, string path)
        {
            if (string.IsNullOrWhiteSpace(projectKey)) return;
            var settings = Load();
            var map = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            object val;
            if (settings.TryGetValue(ProjectFoldersKey, out val) && val is Dictionary<string, object> existing)
                foreach (var kv in existing) map[kv.Key] = kv.Value;

            if (string.IsNullOrWhiteSpace(path)) map.Remove(projectKey);
            else map[projectKey] = path.Trim();

            if (map.Count == 0) settings.Remove(ProjectFoldersKey);
            else settings[ProjectFoldersKey] = map;
            Save(settings);
        }

        public static void ClearProjectContextFolder(string projectKey) => SetProjectContextFolder(projectKey, null);

        private static string GetString(string key, string fallback)
        {
            var settings = Load();
            object val;
            if (settings.TryGetValue(key, out val) && val is string s)
                return s;
            return fallback;
        }

        /// <summary>Settings-side OpenServer (managed.json wins when IT set it); applies at the next Revit start.</summary>
        public static void SetOpenServer(bool open) => SetValue(ManagedSettings.OpenServer, open);

        internal static void SetValueForTests(string key, object value) => SetValue(key, value);

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
            ManagedSettings.Reload();
        }

        /// <summary>Tests: read and write this settings file instead of the user's (null restores it).</summary>
        internal static void OverridePathForTests(string settingsPath)
        {
            lock (Lock)
            {
                SettingsPath = settingsPath ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VibeModel", "settings.json");
                SettingsDir = Path.GetDirectoryName(SettingsPath);
                _cache = null;
            }
        }
    }
}
