using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using VibeModel.Loader;

namespace VibeModel.Services.Update
{
    /// <summary>
    /// <c>latest-Revit&lt;year&gt;.json</c>, published next to the release zip by
    /// scripts/package-release.ps1 (plan 19):
    /// <code>{"version":"1.2.13","revitYear":"2023","zip":"VibeModel-v1.2.13-Revit2023.zip",
    ///  "sha256":"…","size":3456789,"published":"2026-10-20","notes":"https://…","minLoader":"1.0.0"}</code>
    /// Pure: parsing and validation only. Nothing is trusted that does not validate — the zip name
    /// must be exactly the one for this version and Revit year (a plain file name, never a path or
    /// URL), the checksum 64 hex characters, the size positive.
    /// </summary>
    public sealed class UpdateManifest
    {
        public Version Version { get; private set; }
        /// <summary>The version as written ("1.2.13") — also the name of its version folder.</summary>
        public string VersionText { get; private set; }
        public string RevitYear { get; private set; }
        public string Zip { get; private set; }
        /// <summary>Lower-case hex.</summary>
        public string Sha256 { get; private set; }
        public long Size { get; private set; }
        public string Published { get; private set; }
        /// <summary>The release notes page (https only), or null.</summary>
        public string Notes { get; private set; }
        /// <summary>The oldest loader that can start this version (default 1.0.0).</summary>
        public Version MinLoader { get; private set; }

        /// <summary>The manifest's file name for a Revit year.</summary>
        public static string FileName(string revitYear) => "latest-Revit" + revitYear + ".json";

        /// <summary>The release zip's name for a version and Revit year.</summary>
        public static string ZipName(string version, string revitYear) => "VibeModel-v" + version + "-Revit" + revitYear + ".zip";

        /// <summary>1.2.13.0 → "1.2.13"; 1.2.13.4 → "1.2.13.4".</summary>
        public static string ShortVersion(Version v)
        {
            if (v == null) return "?";
            var n = LoaderPaths.Normalize(v);
            return n.Revision == 0 ? n.Major + "." + n.Minor + "." + n.Build : n.ToString();
        }

        private static readonly Regex Hex64 = new Regex("^[0-9a-fA-F]{64}$");

        public static bool TryParse(string json, string expectedRevitYear, out UpdateManifest manifest, out string error)
        {
            manifest = null;
            Dictionary<string, object> d;
            try
            {
                d = new JavaScriptSerializer().DeserializeObject(json ?? "") as Dictionary<string, object>;
            }
            catch (Exception ex)
            {
                error = "the update manifest is not valid JSON (" + ex.Message + ")";
                return false;
            }
            if (d == null)
            {
                error = "the update manifest is empty or not a JSON object";
                return false;
            }

            var versionText = Str(d, "version");
            if (!Version.TryParse(versionText ?? "", out var version))
            {
                error = "the update manifest has no valid version ('" + (versionText ?? "") + "')";
                return false;
            }

            var year = Str(d, "revitYear");
            if (string.IsNullOrEmpty(year))
            {
                error = "the update manifest names no Revit year";
                return false;
            }
            if (!string.IsNullOrEmpty(expectedRevitYear) && year != expectedRevitYear)
            {
                error = "the update manifest is for Revit " + year + ", this is Revit " + expectedRevitYear;
                return false;
            }

            var zip = Str(d, "zip");
            var expectedZip = ZipName(versionText, year);
            if (!string.Equals(zip, expectedZip, StringComparison.OrdinalIgnoreCase))
            {
                error = "the update manifest names the zip '" + (zip ?? "") + "', expected '" + expectedZip + "'";
                return false;
            }

            var sha = Str(d, "sha256");
            if (sha == null || !Hex64.IsMatch(sha))
            {
                error = "the update manifest has no valid sha256 checksum";
                return false;
            }

            if (!TryLong(d, "size", out var size) || size <= 0)
            {
                error = "the update manifest has no valid size";
                return false;
            }

            var minLoader = new Version(1, 0, 0, 0);
            var minLoaderText = Str(d, "minLoader");
            if (!string.IsNullOrEmpty(minLoaderText))
            {
                if (!Version.TryParse(minLoaderText, out var ml))
                {
                    error = "the update manifest has an invalid minLoader ('" + minLoaderText + "')";
                    return false;
                }
                minLoader = LoaderPaths.Normalize(ml);
            }

            var notes = Str(d, "notes");
            if (notes != null && !notes.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) notes = null;

            manifest = new UpdateManifest
            {
                Version = LoaderPaths.Normalize(version),
                VersionText = versionText.Trim(),
                RevitYear = year,
                Zip = expectedZip,
                Sha256 = sha.ToLowerInvariant(),
                Size = size,
                Published = Str(d, "published"),
                Notes = notes,
                MinLoader = minLoader
            };
            error = null;
            return true;
        }

        private static string Str(Dictionary<string, object> d, string key)
        {
            if (!d.TryGetValue(key, out var v) || v == null) return null;
            var s = Convert.ToString(v, CultureInfo.InvariantCulture)?.Trim();
            return string.IsNullOrEmpty(s) ? null : s;
        }

        private static bool TryLong(Dictionary<string, object> d, string key, out long value)
        {
            value = 0;
            if (!d.TryGetValue(key, out var v) || v == null) return false;
            switch (v)
            {
                case int i: value = i; return true;
                case long l: value = l; return true;
                case decimal m when m == Math.Floor(m): value = (long)m; return true;
                case string s: return long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value);
                default: return false;
            }
        }
    }
}
