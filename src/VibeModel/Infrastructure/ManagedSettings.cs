using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Web.Script.Serialization;

namespace VibeModel.Infrastructure
{
    /// <summary>
    /// Read-only settings that IT pushes to a machine: <c>managed.json</c>, searched at
    /// <c>%ProgramData%\VibeModel\managed.json</c> then <c>%APPDATA%\VibeModel\managed.json</c>
    /// (first one found wins). A key present here overrides the user's settings.json and is shown
    /// as locked ("beheerd door IT") in Settings. Keys: <see cref="Keys"/>. VibeModel never writes
    /// this file. Re-read at most once a minute, and on <see cref="Reload"/>.
    /// <para>
    /// The machine-wide file is only trusted when it really comes from IT: ordinary users may
    /// create folders under %ProgramData% (its DACL gives Users create rights), so a user-made
    /// <c>VibeModel\managed.json</c> there would otherwise lock the add-in to a key or endpoint of
    /// their choosing — and look like IT policy. <see cref="FileTrust.Check"/> requires the file and
    /// its folder to be owned by Administrators or SYSTEM and to give no write access to Users,
    /// Authenticated Users, Everyone or Interactive. An untrusted file is ignored, logged once as a
    /// WARN and named by Diagnose (<see cref="UntrustedFile"/>).
    /// </para>
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
        public const string OpenServer = "OpenServer";

        /// <summary>The keys IT can set (documented in docs/deployment.md).</summary>
        public static readonly string[] Keys =
        {
            PreferredBackend, Model, ApiBaseUrl, AnthropicApiKey, DisableClaudeCode, ClaudeCliPath, Language, OpenServer
        };

        private static readonly object Lock = new object();
        private static string[] _searchPathsOverride;
        private static Func<string, string> _trustCheckOverride;
        private static Dictionary<string, object> _values;
        private static string _path;
        private static string _untrustedPath;
        private static string _untrustedReason;
        private static DateTime _readUtc;
        private static bool _loggedError;
        private static bool _loggedUntrusted;

        /// <summary>Where managed.json is looked for, in order.</summary>
        public static IReadOnlyList<string> SearchPaths
        {
            get
            {
                if (_searchPathsOverride != null) return _searchPathsOverride;
                return new[]
                {
                    Path.Combine(MachineFolder, "VibeModel", "managed.json"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VibeModel", "managed.json"),
                };
            }
        }

        private static string MachineFolder => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        /// <summary>The managed.json in use, or null when there is none.</summary>
        public static string FilePath
        {
            get { Ensure(); return _path; }
        }

        /// <summary>A machine-wide managed.json that was found but not trusted (null when none); see <see cref="UntrustedReason"/>.</summary>
        public static string UntrustedFile
        {
            get { Ensure(); return _untrustedPath; }
        }

        public static string UntrustedReason
        {
            get { Ensure(); return _untrustedReason; }
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

        /// <summary>
        /// Tests: decide trust for EVERY candidate with this (path → null when trusted, else the
        /// reason) instead of the ACL check on the machine-wide file. Null restores the default.
        /// </summary>
        internal static void OverrideTrustCheckForTests(Func<string, string> check)
        {
            lock (Lock)
            {
                _trustCheckOverride = check;
                _loggedUntrusted = false;
                _readUtc = DateTime.MinValue;
            }
        }

        // Null when the candidate may be used, else why not. Only the machine-wide location is
        // checked by default: the %APPDATA% file is the user's own by definition.
        private static string TrustProblem(string candidate)
        {
            if (_trustCheckOverride != null) return _trustCheckOverride(candidate);
            var machine = MachineFolder;
            if (string.IsNullOrEmpty(machine) ||
                !candidate.StartsWith(machine.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                return null;
            return FileTrust.Check(candidate);
        }

        private static void Ensure()
        {
            lock (Lock)
            {
                if (DateTime.UtcNow - _readUtc < TimeSpan.FromMinutes(1)) return;
                _readUtc = DateTime.UtcNow;
                _values = null;
                _path = null;
                _untrustedPath = null;
                _untrustedReason = null;
                foreach (var candidate in SearchPaths)
                {
                    try
                    {
                        if (!File.Exists(candidate)) continue;
                        var problem = TrustProblem(candidate);
                        if (problem != null)
                        {
                            _untrustedPath = candidate;
                            _untrustedReason = problem;
                            if (!_loggedUntrusted)
                                Logger.Warn("Ignoring " + candidate + ": " + problem +
                                            " (IT must create the folder and file as an administrator; see docs/deployment.md)");
                            _loggedUntrusted = true;
                            continue;
                        }
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

    /// <summary>
    /// Is a file under %ProgramData% really IT's? Owner and DACL of the file and of its folder.
    /// </summary>
    internal static class FileTrust
    {
        private static readonly WellKnownSidType[] TrustedOwners =
        {
            WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid
        };

        // Groups an ordinary user is a member of; write access for any of them means anyone can edit.
        private static readonly WellKnownSidType[] Everyone =
        {
            WellKnownSidType.BuiltinUsersSid, WellKnownSidType.AuthenticatedUserSid,
            WellKnownSidType.WorldSid, WellKnownSidType.InteractiveSid
        };

        private const FileSystemRights WriteRights =
            FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteAttributes |
            FileSystemRights.WriteExtendedAttributes | FileSystemRights.Delete |
            FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions |
            FileSystemRights.TakeOwnership;

        /// <summary>Null when trusted, else a one-line reason (also when the ACL cannot be read).</summary>
        internal static string Check(string filePath)
        {
            try
            {
                // FileInfo/DirectoryInfo.GetAccessControl exist on both net48 and net8.0-windows.
                var file = Problem(new FileInfo(filePath).GetAccessControl(), "the file");
                if (file != null) return file;
                var folder = Path.GetDirectoryName(filePath);
                if (folder != null)
                {
                    var dir = Problem(new DirectoryInfo(folder).GetAccessControl(), "its folder");
                    if (dir != null) return dir;
                }
                return null;
            }
            catch (Exception ex)
            {
                return "its permissions could not be read (" + ex.Message + ")";
            }
        }

        /// <summary>The decision on a security descriptor (unit-tested without touching ProgramData).</summary>
        internal static string Problem(FileSystemSecurity security, string what)
        {
            var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (owner == null || !IsAny(owner, TrustedOwners))
                return what + " is not owned by Administrators or SYSTEM (owner: " + Name(owner) + ")";

            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                if ((rule.FileSystemRights & WriteRights) == 0) continue;
                var sid = rule.IdentityReference as SecurityIdentifier;
                if (sid != null && IsAny(sid, Everyone))
                    return what + " is writable by " + Name(sid);
            }
            return null;
        }

        private static bool IsAny(SecurityIdentifier sid, WellKnownSidType[] types)
        {
            foreach (var t in types)
                if (sid.IsWellKnown(t)) return true;
            return false;
        }

        private static string Name(SecurityIdentifier sid)
        {
            if (sid == null) return "unknown";
            try { return sid.Translate(typeof(NTAccount)).Value; }
            catch { return sid.Value; }
        }
    }
}
