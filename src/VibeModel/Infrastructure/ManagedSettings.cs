using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace VibeModel.Infrastructure
{
    /// <summary>
    /// Read-only settings that IT pushes to a machine: <c>managed.json</c>, searched at
    /// <c>%ProgramData%\VibeModel\managed.json</c> then <c>%APPDATA%\VibeModel\managed.json</c>
    /// (first one found wins). A key present here overrides the user's settings.json and is shown
    /// as locked ("beheerd door IT") in Settings. Keys: <see cref="Keys"/>. VibeModel never writes
    /// this file. Re-read at most once a minute, and on <see cref="Reload"/>.
    /// </summary>
    public static class ManagedSettings
    {
        public const string PreferredBackend = "PreferredBackend";
        public const string Model = "Model";
        public const string ApiBaseUrl = "ApiBaseUrl";
        public const string AnthropicApiKey = "AnthropicApiKey";
        public const string DisableClaudeCode = "DisableClaudeCode";
        public const string ClaudeCliPath = "ClaudeCliPath";
        public const string Language = "Language";

        /// <summary>The keys IT can set (documented in docs/deployment.md).</summary>
        public static readonly string[] Keys =
        {
            PreferredBackend, Model, ApiBaseUrl, AnthropicApiKey, DisableClaudeCode, ClaudeCliPath, Language
        };

        private static readonly object Lock = new object();
        private static string[] _searchPathsOverride;
        private static Dictionary<string, object> _values;
        private static string _path;
        private static DateTime _readUtc;
        private static bool _loggedError;

        /// <summary>Where managed.json is looked for, in order.</summary>
        public static IReadOnlyList<string> SearchPaths
        {
            get
            {
                if (_searchPathsOverride != null) return _searchPathsOverride;
                return new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VibeModel", "managed.json"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VibeModel", "managed.json"),
                };
            }
        }

        /// <summary>The managed.json in use, or null when there is none.</summary>
        public static string FilePath
        {
            get { Ensure(); return _path; }
        }

        public static bool TryGet(string key, out object value)
        {
            Ensure();
            value = null;
            lock (Lock)
                return _values != null && _values.TryGetValue(key, out value) && value != null;
        }

        /// <summary>The managed string value of <paramref name="key"/>, or null when IT did not set it.</summary>
        public static string GetString(string key)
        {
            return TryGet(key, out var v) && v is string s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
        }

        /// <summary>True/false when IT set the flag, null otherwise.</summary>
        public static bool? GetBool(string key)
        {
            if (!TryGet(key, out var v)) return null;
            if (v is bool b) return b;
            if (v is string s)
            {
                if (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1") return true;
                if (s.Equals("false", StringComparison.OrdinalIgnoreCase) || s == "0") return false;
            }
            return null;
        }

        /// <summary>True when IT set <paramref name="key"/> (the Settings dialog locks that field).</summary>
        public static bool IsManaged(string key) => TryGet(key, out _);

        public static void Reload()
        {
            lock (Lock) _readUtc = DateTime.MinValue;
        }

        /// <summary>Tests: look only in these files (null restores the real locations).</summary>
        internal static void OverrideSearchPathsForTests(params string[] paths)
        {
            lock (Lock)
            {
                _searchPathsOverride = paths;
                _readUtc = DateTime.MinValue;
            }
        }

        private static void Ensure()
        {
            lock (Lock)
            {
                if (DateTime.UtcNow - _readUtc < TimeSpan.FromMinutes(1)) return;
                _readUtc = DateTime.UtcNow;
                _values = null;
                _path = null;
                foreach (var candidate in SearchPaths)
                {
                    try
                    {
                        if (!File.Exists(candidate)) continue;
                        var parsed = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(candidate));
                        _values = new Dictionary<string, object>(parsed ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
                        _path = candidate;
                        return;
                    }
                    catch (Exception ex)
                    {
                        // A broken managed.json must not break the add-in; it is reported once and by Diagnose.
                        if (!_loggedError) Logger.Error("Could not read " + candidate, ex);
                        _loggedError = true;
                    }
                }
            }
        }
    }
}
