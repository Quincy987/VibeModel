using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using VibeModel.Infrastructure;

namespace VibeModel.Services.Chat
{
    /// <summary>
    /// Where the Claude Code CLI may live. In order: an explicit path (managed.json ClaudeCliPath,
    /// the VIBEMODEL_CLAUDE_CLI environment variable, settings.json) — "none" turns discovery off;
    /// otherwise the native installer's <c>%USERPROFILE%\.local\bin\claude.exe</c>, every folder on
    /// the PATH as Windows has it NOW (read from the registry — Revit's own PATH is the one from
    /// when Revit started, so a CLI installed since then would stay invisible until a restart of
    /// Windows Explorer), then the old npm and installer locations. Pure apart from
    /// <see cref="FreshPathDirectories"/>; the candidates are probed with --version by the backend.
    /// </summary>
    public static class ClaudeCliLocator
    {
        public const string DisabledValue = "none";

        /// <summary>True when the explicit setting says "none" (a laptop without Claude Code, or a test of one).</summary>
        public static bool IsDisabled(string explicitPath) =>
            string.Equals((explicitPath ?? "").Trim(), DisabledValue, StringComparison.OrdinalIgnoreCase);

        /// <summary>The paths to probe, best first, without duplicates. Empty when discovery is off.</summary>
        public static IReadOnlyList<string> Candidates(string explicitPath, string userProfile, string appData,
            string localAppData, IEnumerable<string> pathDirectories)
        {
            if (IsDisabled(explicitPath)) return new string[0];
            if (!string.IsNullOrWhiteSpace(explicitPath)) return new[] { explicitPath.Trim().Trim('"') };

            var list = new List<string>();
            if (!string.IsNullOrEmpty(userProfile))
                list.Add(Path.Combine(userProfile, ".local", "bin", "claude.exe"));
            foreach (var dir in pathDirectories ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                list.Add(Path.Combine(dir.Trim().Trim('"'), "claude.exe"));
                list.Add(Path.Combine(dir.Trim().Trim('"'), "claude.cmd"));
            }
            if (!string.IsNullOrEmpty(appData))
                list.Add(Path.Combine(appData, "npm", "claude.cmd"));
            if (!string.IsNullOrEmpty(localAppData))
                list.Add(Path.Combine(localAppData, "Programs", "claude", "claude.exe"));
            if (!string.IsNullOrEmpty(userProfile))
                list.Add(Path.Combine(userProfile, ".claude", "local", "claude.exe"));

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return list.Where(p => seen.Add(p)).ToList();
        }

        /// <summary>The candidates for this user and machine.</summary>
        public static IReadOnlyList<string> CandidatesForThisMachine()
        {
            return Candidates(
                SettingsManager.GetClaudeCliPath(),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                FreshPathDirectories());
        }

        /// <summary>
        /// The user PATH and the machine PATH as stored in the registry now (environment variables
        /// in them expanded), then this process's PATH — first occurrence wins.
        /// </summary>
        public static IReadOnlyList<string> FreshPathDirectories()
        {
            var raw = new List<string>
            {
                ReadRegistryPath(Registry.CurrentUser, @"Environment"),
                ReadRegistryPath(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment"),
                Environment.GetEnvironmentVariable("PATH")
            };
            return SplitPath(raw);
        }

        internal static IReadOnlyList<string> SplitPath(IEnumerable<string> pathValues)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dirs = new List<string>();
            foreach (var value in pathValues)
            {
                if (string.IsNullOrWhiteSpace(value)) continue;
                foreach (var part in value.Split(';'))
                {
                    var dir = Environment.ExpandEnvironmentVariables(part.Trim().Trim('"'));
                    if (dir.Length > 0 && seen.Add(dir.TrimEnd('\\'))) dirs.Add(dir);
                }
            }
            return dirs;
        }

        private static string ReadRegistryPath(RegistryKey root, string subKey)
        {
            try
            {
                using (var key = root.OpenSubKey(subKey))
                    return key?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            }
            catch
            {
                return null;
            }
        }
    }
}
