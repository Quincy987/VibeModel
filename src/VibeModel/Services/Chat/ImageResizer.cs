using System;
using System.IO;
using System.Windows.Media.Imaging;
using VibeModel.Infrastructure;

namespace VibeModel.Services.Chat
{
    /// <summary>
    /// Downscales oversized image attachments before they are base64-inlined into an
    /// API request. Images within limits pass through byte-for-byte (original format
    /// preserved); oversized ones are re-encoded as JPEG at progressively smaller
    /// scales until they fit. Decode failures (e.g. WebP, which WPF can't decode)
    /// fall back to the original bytes — the hard size guard upstream still applies.
    /// </summary>
    internal static class ImageResizer
    {
        internal const int MaxPixelEdge = 4000;
        internal const int MaxEncodedBytes = 4500000; // ~4.5MB, safely under the 5MB API image cap

        internal static byte[] LoadImageBytesForApi(string path, string originalMimeType, out string mediaType)
        {
            var original = File.ReadAllBytes(path);
            mediaType = originalMimeType;

            try
            {
                int width, height;
                using (var probe = new MemoryStream(original))
                {
                    var decoder = BitmapDecoder.Create(
                        probe, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    width = decoder.Frames[0].PixelWidth;
                    height = decoder.Frames[0].PixelHeight;
                }

                int longestEdge = Math.Max(width, height);
                if (longestEdge <= MaxPixelEdge && original.Length <= MaxEncodedBytes)
                    return original;

                double scale = Math.Min(1.0, (double)MaxPixelEdge / longestEdge);
                byte[] best = null;
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    var encoded = EncodeJpegAtScale(original, scale);
                    if (best == null || encoded.Length < best.Length)
                        best = encoded;
                    if (encoded.Length <= MaxEncodedBytes)
                        break;
                    scale *= 0.7;
                }

                Logger.Info("Attachment image downscaled: " + Path.GetFileName(path) + " " +
                            original.Length + "B -> " + best.Length + "B");
                mediaType = "image/jpeg";
                return best;
            }
            catch (Exception ex)
            {
                Logger.Warn("Image downscale failed for " + path + " (" + ex.Message +
                            ") — sending original bytes");
                mediaType = originalMimeType;
                return original;
            }
        }

        private static byte[] EncodeJpegAtScale(byte[] original, double scale)
        {
            using (var input = new MemoryStream(original))
            {
                var decoder = BitmapDecoder.Create(
                    input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames[0];

                BitmapSource source = frame;
                if (scale < 1.0)
                    source = new System.Windows.Media.Imaging.TransformedBitmap(
                        frame, new System.Windows.Media.ScaleTransform(scale, scale));

                var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
                encoder.Frames.Add(BitmapFrame.Create(source));
                using (var output = new MemoryStream())
                {
                    encoder.Save(output);
                    return output.ToArray();
                }
            }
        }
    }
}
