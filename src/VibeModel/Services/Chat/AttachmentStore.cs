using System;
using System.IO;
using System.Text;
using System.Windows.Media.Imaging;
using VibeModel.Infrastructure;

namespace VibeModel.Services.Chat
{
    /// <summary>
    /// Persists chat attachments per Revit project so they outlive the chat session:
    /// %LOCALAPPDATA%\VibeModel\attachments\&lt;sanitized project key&gt;\&lt;filename&gt;.
    /// The Claude Code backend reads these paths directly; the API backends re-read
    /// the bytes when building request content. Stored copies are never auto-deleted —
    /// they double as the project's document archive.
    /// </summary>
    public static class AttachmentStore
    {
        public static string RootFolder
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "VibeModel",
                    "attachments");
            }
        }

        /// <summary>
        /// Copies a source file into the project's attachment folder and returns the
        /// stored path. If the source already lives inside the store (e.g. a pasted
        /// clipboard image saved moments earlier), it is returned unchanged. Name
        /// collisions dedupe with " (2)", " (3)", ...
        /// </summary>
        public static string StoreFile(string sourcePath, string projectKey)
        {
            return StoreFile(sourcePath, projectKey, RootFolder);
        }

        internal static string StoreFile(string sourcePath, string projectKey, string root)
        {
            if (sourcePath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return sourcePath;

            var folder = GetProjectFolder(projectKey, root);
            Directory.CreateDirectory(folder);

            var target = DedupePath(folder, Path.GetFileName(sourcePath));
            File.Copy(sourcePath, target);
            return target;
        }

        /// <summary>
        /// Saves a clipboard image as a PNG in the project's attachment folder and
        /// returns its path.
        /// </summary>
        public static string SaveClipboardImage(BitmapSource image, string projectKey)
        {
            var folder = GetProjectFolder(projectKey, RootFolder);
            Directory.CreateDirectory(folder);

            var name = "pasted-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png";
            var target = DedupePath(folder, name);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var stream = File.Create(target))
                encoder.Save(stream);

            Logger.Info("Clipboard image saved: " + target);
            return target;
        }

        internal static string GetProjectFolder(string projectKey, string root)
        {
            return Path.Combine(root, SanitizeName(projectKey));
        }

        /// <summary>
        /// Makes a project title safe for use as a folder name: invalid path chars
        /// become '_', whitespace-only/empty input falls back to "default".
        /// </summary>
        internal static string SanitizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "default";

            var sb = new StringBuilder(name.Length);
            var invalid = Path.GetInvalidFileNameChars();
            foreach (var c in name.Trim())
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);

            // Guard against Windows reserved device names (CON, NUL, PRN, ...)
            var result = sb.ToString().TrimEnd('.', ' ');
            if (result.Length == 0 || IsReservedDeviceName(result))
                return "default";
            if (result.Length > 100)
                result = result.Substring(0, 100);
            return result;
        }

        private static bool IsReservedDeviceName(string name)
        {
            var baseName = name.Contains(".")
                ? name.Substring(0, name.IndexOf('.'))
                : name;
            switch (baseName.ToUpperInvariant())
            {
                case "CON":
                case "PRN":
                case "AUX":
                case "NUL":
                    return true;
                default:
                    if (baseName.Length == 4 &&
                        (baseName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                         baseName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                        char.IsDigit(baseName[3]))
                        return true;
                    return false;
            }
        }

        /// <summary>
        /// Returns a path in <paramref name="folder"/> for <paramref name="fileName"/>
        /// that does not collide with an existing file: "spec.pdf" → "spec (2).pdf" → ...
        /// </summary>
        internal static string DedupePath(string folder, string fileName)
        {
            var candidate = Path.Combine(folder, fileName);
            if (!File.Exists(candidate))
                return candidate;

            var stem = Path.GetFileNameWithoutExtension(fileName);
            var ext = Path.GetExtension(fileName);
            for (int i = 2; ; i++)
            {
                candidate = Path.Combine(folder, stem + " (" + i + ")" + ext);
                if (!File.Exists(candidate))
                    return candidate;
            }
        }
    }
}
