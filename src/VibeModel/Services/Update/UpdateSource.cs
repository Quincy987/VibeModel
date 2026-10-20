using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace VibeModel.Services.Update
{
    /// <summary>
    /// Where updates come from (plan 19). Default: GitHub's "latest release" asset URL —
    /// <c>https://github.com/Quincy987/VibeModel/releases/latest/download/latest-Revit&lt;year&gt;.json</c>
    /// (GitHub redirects it to the newest published, non-prerelease release; drafts are invisible).
    /// IT can set <c>UpdateSource</c> in managed.json: an https URL (of the manifest itself or of the
    /// folder holding it) or a UNC/local folder (or manifest file) — a mirror of the release assets.
    /// Pure: no I/O.
    /// </summary>
    public sealed class UpdateSource
    {
        public const string Repository = "Quincy987/VibeModel";
        public const string DefaultBase = "https://github.com/" + Repository + "/releases/latest/download/";
        public const string ReleasesApi = "https://api.github.com/repos/" + Repository + "/releases?per_page=10";
        private const string LatestSegment = "/releases/latest/download/";

        /// <summary>What was configured (empty = the default).</summary>
        public string Configured { get; private set; }
        public bool IsDefault { get; private set; }
        /// <summary>The manifest: an https URL, or a file:// URI for a share or a local folder.</summary>
        public Uri Manifest { get; private set; }
        public bool IsFile => Manifest != null && Manifest.IsFile;
        /// <summary>Why the configured value cannot be used (then <see cref="Manifest"/> is null).</summary>
        public string Error { get; private set; }

        /// <summary>"GitHub releases (default)" / the URL / the folder — for Settings, Diagnose and `update`.</summary>
        public string Describe()
        {
            if (Error != null) return "invalid (" + Error + ")";
            if (IsDefault) return "GitHub releases (" + Repository + ")";
            return IsFile ? Manifest.LocalPath : Manifest.AbsoluteUri;
        }

        public static UpdateSource Resolve(string configured, string revitYear)
        {
            var source = new UpdateSource { Configured = (configured ?? "").Trim() };
            var file = UpdateManifest.FileName(revitYear);
            var c = source.Configured;

            if (c.Length == 0)
            {
                source.IsDefault = true;
                source.Manifest = new Uri(DefaultBase + file);
                return source;
            }

            if (c.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                source.Error = "only https:// is accepted for an update source";
                return source;
            }

            if (c.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(c, UriKind.Absolute, out var uri))
                {
                    source.Error = "'" + c + "' is not a valid URL";
                    return source;
                }
                // A folder URL gets the file name appended; its query (an access token) stays on the end.
                source.Manifest = uri.AbsolutePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    ? uri
                    : new UriBuilder(new Uri(new Uri(uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/"), file))
                        { Query = uri.Query.TrimStart('?') }.Uri;
                return source;
            }

            // A folder (UNC or local) or the manifest file in it: \\server\share\... or X:\... Relative
            // paths, "\folder" (the current drive) and "X:folder" (that drive's current folder) mean
            // nothing here.
            bool unc = c.StartsWith(@"\\", StringComparison.Ordinal);
            bool driveAbsolute = c.Length >= 3 && char.IsLetter(c[0]) && c[1] == ':' && (c[2] == '\\' || c[2] == '/');
            if (!unc && !driveAbsolute)
            {
                source.Error = "'" + c + "' is neither an https URL nor a full folder path (\\\\server\\share\\… or C:\\…)";
                return source;
            }
            try
            {
                var path = c.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? c : Path.Combine(c, file);
                source.Manifest = new Uri(Path.GetFullPath(path));
            }
            catch (Exception ex)
            {
                source.Error = "'" + c + "' is not a usable path (" + ex.Message + ")";
            }
            return source;
        }

        /// <summary>
        /// Where the zip a manifest names lives: next to the manifest. For GitHub's moving
        /// "latest/download" address the zip is taken from the manifest's own release tag
        /// (<c>/releases/download/v&lt;version&gt;/</c>), so a release published between the two downloads
        /// cannot hand over a different zip than the one the checksum belongs to. An https mirror's
        /// query (a container-wide access token, e.g. an Azure SAS) is kept on the zip's address.
        /// </summary>
        public static Uri ZipUri(Uri manifest, UpdateManifest m)
        {
            if (manifest == null || m == null) return null;
            if (manifest.IsFile) return new Uri(manifest, Uri.EscapeDataString(m.Zip));

            var text = manifest.GetLeftPart(UriPartial.Path);
            var at = text.IndexOf(LatestSegment, StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
                return new Uri(text.Substring(0, at) + "/releases/download/v" + m.VersionText + "/" + Uri.EscapeDataString(m.Zip));
            return new UriBuilder(new Uri(manifest, Uri.EscapeDataString(m.Zip))) { Query = manifest.Query.TrimStart('?') }.Uri;
        }

        /// <summary>True for a URL on GitHub's own release download path (the API fallback applies).</summary>
        public static bool IsGitHubLatest(Uri manifest) =>
            manifest != null && !manifest.IsFile &&
            manifest.AbsoluteUri.IndexOf(LatestSegment, StringComparison.OrdinalIgnoreCase) >= 0 &&
            manifest.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The GitHub API fallback (the "latest" release has no manifest — an older release, or a
        /// non-version release marked latest): the manifest asset of the first published,
        /// non-prerelease release whose tag starts with "v". Null when none has one.
        /// </summary>
        public static Uri PickFromReleases(string releasesJson, string manifestName)
        {
            object[] releases;
            try
            {
                releases = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(releasesJson ?? "") as object[];
            }
            catch
            {
                return null;
            }
            if (releases == null) return null;
            foreach (var r in releases)
            {
                if (!(r is Dictionary<string, object> release)) continue;
                if (IsTrue(release, "draft") || IsTrue(release, "prerelease")) continue;
                var tag = release.TryGetValue("tag_name", out var t) ? t as string : null;
                if (tag == null || !tag.StartsWith("v", StringComparison.OrdinalIgnoreCase)) continue;
                if (!release.TryGetValue("assets", out var a) || !(a is object[] assets)) continue;
                foreach (var asset in assets)
                {
                    if (!(asset is Dictionary<string, object> ad)) continue;
                    var name = ad.TryGetValue("name", out var n) ? n as string : null;
                    var url = ad.TryGetValue("browser_download_url", out var u) ? u as string : null;
                    if (string.Equals(name, manifestName, StringComparison.OrdinalIgnoreCase) &&
                        url != null && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                        Uri.TryCreate(url, UriKind.Absolute, out var uri))
                        return uri;
                }
            }
            return null;
        }

        private static bool IsTrue(Dictionary<string, object> d, string key) =>
            d.TryGetValue(key, out var v) && v is bool b && b;
    }
}
