using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using VibeModel.Services.Chat;

namespace VibeModel.Services.Update
{
    /// <summary>The source has no such file (HTTP 404 / file missing) — the GitHub API fallback applies.</summary>
    public sealed class UpdateNotFoundException : Exception
    {
        public UpdateNotFoundException(string message) : base(message) { }
    }

    /// <summary>How the update service reads a manifest and downloads a zip (tests swap it).</summary>
    public interface IUpdateTransport
    {
        /// <summary>The text at <paramref name="uri"/>; throws <see cref="UpdateNotFoundException"/> when it does not exist.</summary>
        string GetText(Uri uri, TimeSpan timeout, CancellationToken ct);

        /// <summary>Downloads <paramref name="uri"/> to <paramref name="targetFile"/>, reporting (bytes, total or -1).</summary>
        void Download(Uri uri, string targetFile, Action<long, long> progress, CancellationToken ct);
    }

    /// <summary>
    /// https through the same handler as every Anthropic call (<see cref="AnthropicHttp.CreateHandler"/>:
    /// Windows proxy with the user's credentials, the Windows certificate store, validation never
    /// relaxed); file:// (a share or a local folder) with a deadline on reading the manifest.
    /// </summary>
    public sealed class UpdateTransport : IUpdateTransport
    {
        public static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

        private static string UserAgent =>
            "VibeModel/" + UpdateManifest.ShortVersion(typeof(UpdateTransport).Assembly.GetName().Version);

        public string GetText(Uri uri, TimeSpan timeout, CancellationToken ct)
        {
            if (uri.IsFile) return ReadFile(uri.LocalPath, timeout);

            using (var client = AnthropicHttp.CreateClient(timeout))
            using (var request = new HttpRequestMessage(HttpMethod.Get, uri))
            {
                request.Headers.UserAgent.ParseAdd(UserAgent);
                request.Headers.Accept.ParseAdd("application/json");
                HttpResponseMessage response;
                try
                {
                    response = client.SendAsync(request, ct).GetAwaiter().GetResult();
                }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException(uri.Host + " did not answer within " + timeout.TotalSeconds + " s");
                }
                using (response)
                {
                    if (response.StatusCode == HttpStatusCode.NotFound)
                        throw new UpdateNotFoundException(uri.AbsoluteUri + " was not found (HTTP 404)");
                    if (!response.IsSuccessStatusCode)
                        throw new IOException(uri.Host + " answered HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase);
                    return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                }
            }
        }

        private static string ReadFile(string path, TimeSpan timeout)
        {
            // A share can hang for minutes when the VPN is down: read with a deadline.
            var task = Task.Run(() => File.Exists(path) ? File.ReadAllText(path) : null);
            if (!task.Wait(timeout))
                throw new TimeoutException(path + " did not answer within " + timeout.TotalSeconds + " s (network folder offline?)");
            if (task.Result == null) throw new UpdateNotFoundException(path + " does not exist");
            return task.Result;
        }

        public void Download(Uri uri, string targetFile, Action<long, long> progress, CancellationToken ct)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile));
            if (uri.IsFile)
            {
                var source = uri.LocalPath;
                if (!File.Exists(source)) throw new UpdateNotFoundException(source + " does not exist");
                using (var input = File.OpenRead(source))
                using (var output = File.Create(targetFile))
                    Copy(input, output, input.Length, progress, ct);
                return;
            }

            using (var client = AnthropicHttp.CreateClient(DownloadTimeout))
            using (var request = new HttpRequestMessage(HttpMethod.Get, uri))
            {
                request.Headers.UserAgent.ParseAdd(UserAgent);
                using (var response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).GetAwaiter().GetResult())
                {
                    if (response.StatusCode == HttpStatusCode.NotFound)
                        throw new UpdateNotFoundException(uri.AbsoluteUri + " was not found (HTTP 404)");
                    if (!response.IsSuccessStatusCode)
                        throw new IOException(uri.Host + " answered HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase);
                    var total = response.Content.Headers.ContentLength ?? -1;
                    using (var input = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                    using (var output = File.Create(targetFile))
                        Copy(input, output, total, progress, ct);
                }
            }
        }

        private static void Copy(Stream input, Stream output, long total, Action<long, long> progress, CancellationToken ct)
        {
            var buffer = new byte[81920];
            long done = 0;
            int read;
            // A stalled connection never returns from Read: cancelling (the caller's deadline) closes it.
            using (ct.Register(() => { try { input.Dispose(); } catch { } }))
            {
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, read);
                    done += read;
                    progress?.Invoke(done, total);
                }
            }
            ct.ThrowIfCancellationRequested();
        }
    }
}
