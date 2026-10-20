namespace VibeModel.Services.Update
{
    /// <summary>
    /// Which loader started this add-in (plan 19). Set by VibeModel.Loader through reflection
    /// before OnStartup — there is no reference either way, so the names here are a contract:
    /// <c>Version</c>, <c>Path</c>, <c>Rule</c> (public static, settable). All null when VibeModel.dll
    /// was started directly by an older .addin (no loader).
    /// </summary>
    public static class LoaderInfo
    {
        /// <summary>The loader's file version ("1.0.0.0"); null = no loader.</summary>
        public static string Version { get; set; }

        /// <summary>Full path of VibeModel.Loader.dll.</summary>
        public static string Path { get; set; }

        /// <summary>How the loader found this add-in: InPlace, Current or Newest (<c>PayloadRule</c>).</summary>
        public static string Rule { get; set; }

        /// <summary>The loader version parsed and normalized; null when unknown.</summary>
        public static System.Version ParsedVersion =>
            System.Version.TryParse(Version ?? "", out var v) ? Loader.LoaderPaths.Normalize(v) : null;

        /// <summary>"1.0.0 (C:\…\VibeModel.Loader.dll)" or "none - VibeModel.dll started directly".</summary>
        public static string Describe()
        {
            if (string.IsNullOrEmpty(Version)) return "none - VibeModel.dll was started directly by the .addin";
            return UpdateManifest.ShortVersion(ParsedVersion) + " (" + Path + ")";
        }
    }
}
