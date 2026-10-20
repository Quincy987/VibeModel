using System;
using System.Collections.Generic;
using System.IO;

namespace VibeModel.Loader
{
    /// <summary>Which rule picked the add-in the loader starts (plan 19).</summary>
    internal enum PayloadRule
    {
        /// <summary>Nothing found: the loader shows one dialog and Revit runs without VibeModel.</summary>
        None,
        /// <summary>(a) VibeModel.dll next to the loader — a developer build (build.ps1 deploys it there).</summary>
        InPlace,
        /// <summary>(b) the version folder versions\current.txt names.</summary>
        Current,
        /// <summary>(c) the newest version folder (current.txt missing or naming an incomplete folder).</summary>
        Newest
    }

    /// <summary>The add-in the loader starts, and how it was found.</summary>
    internal sealed class PayloadChoice
    {
        /// <summary>Full path of VibeModel.dll; null when nothing was found.</summary>
        public string Path { get; set; }
        public PayloadRule Rule { get; set; }
        /// <summary>The version folder's name ("1.2.13"); null for an in-place build.</summary>
        public string VersionFolder { get; set; }
        /// <summary>Every place looked at, in order (the "not installed" dialog lists them).</summary>
        public List<string> LookedIn { get; } = new List<string>();

        public string RuleText
        {
            get
            {
                switch (Rule)
                {
                    case PayloadRule.InPlace: return "in place (next to the loader)";
                    case PayloadRule.Current: return "current.txt";
                    case PayloadRule.Newest: return "newest version folder";
                    default: return "none";
                }
            }
        }
    }

    /// <summary>
    /// Pure path rules shared by the loader (which picks the add-in to start) and the add-in itself
    /// (which reports and changes what the loader will pick next time). No Revit types. The source
    /// file is compiled into VibeModel.Loader.dll and, linked, into VibeModel.dll.
    /// </summary>
    internal static class LoaderPaths
    {
        public const string PayloadFile = "VibeModel.dll";
        public const string CurrentFile = "current.txt";

        /// <summary>%LOCALAPPDATA%\VibeModel — settings, logs, versions.</summary>
        public static string DataRoot => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VibeModel");

        /// <summary>%LOCALAPPDATA%\VibeModel\versions — one folder per installed version.</summary>
        public static string DefaultVersionsRoot => System.IO.Path.Combine(DataRoot, "versions");

        /// <summary>
        /// The add-in to start: (a) VibeModel.dll next to the loader, (b) the folder current.txt
        /// names, (c) the newest version folder holding VibeModel.dll, else none.
        /// </summary>
        public static PayloadChoice Choose(string loaderFolder, string versionsRoot)
        {
            var choice = new PayloadChoice();

            if (!string.IsNullOrEmpty(loaderFolder))
            {
                var inPlace = System.IO.Path.Combine(loaderFolder, PayloadFile);
                choice.LookedIn.Add(inPlace);
                if (File.Exists(inPlace))
                {
                    choice.Path = inPlace;
                    choice.Rule = PayloadRule.InPlace;
                    return choice;
                }
            }

            if (string.IsNullOrEmpty(versionsRoot)) return choice;

            var current = ReadCurrent(versionsRoot);
            choice.LookedIn.Add(System.IO.Path.Combine(versionsRoot, CurrentFile) +
                                (current == null ? " (none)" : " → " + current));
            if (current != null)
            {
                var named = System.IO.Path.Combine(versionsRoot, current, PayloadFile);
                if (File.Exists(named))
                {
                    choice.Path = named;
                    choice.Rule = PayloadRule.Current;
                    choice.VersionFolder = current;
                    return choice;
                }
                choice.LookedIn.Add(named + " (missing)");
            }

            var newest = NewestInstalled(versionsRoot);
            choice.LookedIn.Add(System.IO.Path.Combine(versionsRoot, "{version}") + "\\" + PayloadFile +
                                (newest == null ? " (no version folder)" : ""));
            if (newest != null)
            {
                choice.Path = System.IO.Path.Combine(versionsRoot, newest, PayloadFile);
                choice.Rule = PayloadRule.Newest;
                choice.VersionFolder = newest;
            }
            return choice;
        }

        /// <summary>
        /// The folder name in versions\current.txt (first non-empty line), or null when the file is
        /// missing, unreadable or names something that is not a plain folder name.
        /// </summary>
        public static string ReadCurrent(string versionsRoot)
        {
            try
            {
                var file = System.IO.Path.Combine(versionsRoot, CurrentFile);
                if (!File.Exists(file)) return null;
                foreach (var line in File.ReadAllLines(file))
                {
                    var name = line.Trim();
                    if (name.Length == 0) continue;
                    return IsPlainFolderName(name) ? name : null;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return null;
        }

        /// <summary>A single folder name: no separators, no "." / "..", no invalid characters.</summary>
        public static bool IsPlainFolderName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name == "." || name == "..") return false;
            if (name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) return false;
            return name.IndexOf('/') < 0 && name.IndexOf('\\') < 0 && name.IndexOf(':') < 0;
        }

        /// <summary>"1.2.13" → 1.2.13; anything that is not a version (".tmp" folders, "_old") → false.</summary>
        public static bool TryParseFolderVersion(string name, out Version version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(name)) return false;
            if (!Version.TryParse(name.Trim(), out var v)) return false;
            version = Normalize(v);
            return true;
        }

        /// <summary>1.2.13 and 1.2.13.0 compare equal: missing parts become 0.</summary>
        public static Version Normalize(Version v)
        {
            if (v == null) return null;
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
        }

        /// <summary>Version folders that hold VibeModel.dll, newest first (by version, not by name).</summary>
        public static List<KeyValuePair<string, Version>> Installed(string versionsRoot)
        {
            var list = new List<KeyValuePair<string, Version>>();
            try
            {
                if (!Directory.Exists(versionsRoot)) return list;
                foreach (var dir in Directory.GetDirectories(versionsRoot))
                {
                    var name = System.IO.Path.GetFileName(dir);
                    if (!TryParseFolderVersion(name, out var v)) continue;
                    if (!File.Exists(System.IO.Path.Combine(dir, PayloadFile))) continue;
                    list.Add(new KeyValuePair<string, Version>(name, v));
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            return list;
        }

        /// <summary>The newest complete version folder's name, or null.</summary>
        public static string NewestInstalled(string versionsRoot)
        {
            var list = Installed(versionsRoot);
            return list.Count == 0 ? null : list[0].Key;
        }
    }
}
