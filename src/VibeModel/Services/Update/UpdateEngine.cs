using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Web.Script.Serialization;
using VibeModel.Loader;

namespace VibeModel.Services.Update
{
    /// <summary>The settings the update service reads and writes (tests swap them).</summary>
    public interface IUpdateSettings
    {
        bool Disabled { get; }
        string Source { get; }
        bool AutoCheck { get; }
        DateTime? LastChecked { get; }
        void RecordCheck(DateTime utc, string result);
    }

    /// <summary>What is running: the add-in's version and path, the loader that started it, the Revit year.</summary>
    public sealed class RunningInfo
    {
        public Version Version { get; set; }
        public string PayloadPath { get; set; }
        /// <summary>Null = started without the loader.</summary>
        public Version LoaderVersion { get; set; }
        public string RevitYear { get; set; }
    }

    /// <summary>Download progress of an install in flight.</summary>
    public sealed class InstallProgress
    {
        public string Phase { get; set; }   // "download" | "unpack"
        public long Bytes { get; set; }
        public long Total { get; set; }
    }

    /// <summary>
    /// The update service's work (plan 19): check the source for a newer version, download and
    /// verify its zip, unpack it into its own version folder, point current.txt at it, roll back.
    /// Never touches the running add-in's files or the loader. Thread-safe; every call is
    /// synchronous and meant for a background thread (<see cref="UpdateService"/> schedules them).
    /// </summary>
    public sealed class UpdateEngine
    {
        public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan AutoCheckInterval = TimeSpan.FromHours(24);
        private const string LoaderFile = "VibeModel.Loader.dll";

        private readonly UpdateLayout _layout;
        private readonly IUpdateTransport _transport;
        private readonly IUpdateSettings _settings;
        private readonly RunningInfo _running;
        private readonly object _lock = new object();
        private int _installing;

        private UpdateStatus _status = new UpdateStatus { State = UpdateState.NotChecked };
        private UpdateManifest _manifest;
        private Uri _manifestUri;
        private InstallProgress _progress;
        // The last failed install (version + reason): a later check of the same version still says
        // why, until an install succeeds.
        private string _installErrorVersion;
        private string _installError;
        private DateTime _lastProgressEventUtc;

        /// <summary>Raised (on a background thread) whenever <see cref="Status"/> or <see cref="Progress"/> changes.</summary>
        public event Action<UpdateStatus> Changed;

        public UpdateEngine(UpdateLayout layout, IUpdateTransport transport, IUpdateSettings settings, RunningInfo running)
        {
            _layout = layout;
            _transport = transport;
            _settings = settings;
            _running = running;
            LoadCachedManifest();
            _status = Decide(null);
        }

        public UpdateLayout Layout => _layout;
        public RunningInfo Running => _running;

        public UpdateStatus Status
        {
            get { lock (_lock) return _status; }
        }

        public InstallProgress Progress
        {
            get { lock (_lock) return _progress; }
        }

        /// <summary>The version folder this add-in runs from (null: in place).</summary>
        public string RunningFolder => _layout.RunningFolder(_running.PayloadPath);

        /// <summary>True when an update could never be started by the loader (no loader, or VibeModel.dll in the add-ins folder).</summary>
        public bool InPlace => _running.LoaderVersion == null || RunningFolder == null;

        /// <summary>current.txt when it names another version than the running one.</summary>
        public string Pending
        {
            get
            {
                if (InPlace) return null;
                // Only a complete folder: for anything else the loader falls back to the newest version.
                var current = _layout.ReadCurrent();
                return current != null && _layout.IsComplete(current) &&
                       !string.Equals(current, RunningFolder, StringComparison.OrdinalIgnoreCase) ? current : null;
            }
        }

        /// <summary>Where "back" goes (Settings' rollback button); null when no older version is installed.</summary>
        public string RollbackTarget => InPlace ? null : _layout.RollbackTarget(RunningFolder, _layout.ReadCurrent());

        public UpdateSource Source => UpdateSource.Resolve(_settings.Source, _running.RevitYear);

        /// <summary>The state from what is known now (no network).</summary>
        public UpdateStatus Refresh()
        {
            UpdateStatus s;
            lock (_lock)
            {
                if (_installing != 0) return _status;
                s = Decide(_status.State == UpdateState.Failed ? _status.Reason : null);
                s.CheckedAt = _status.CheckedAt;
                _status = s;
            }
            return s;
        }

        // --- Check -------------------------------------------------------------------------

        /// <summary>
        /// Looks for a newer version. Automatic checks (<paramref name="manual"/> false) are skipped
        /// when Settings says so or the last one was less than 24 h ago — the cached manifest from
        /// that check still decides (the banner shows after every Revit start).
        /// </summary>
        public UpdateStatus Check(bool manual, CancellationToken ct = default(CancellationToken))
        {
            if (Volatile.Read(ref _installing) != 0) return Status;
            return CheckCore(manual, ct);
        }

        private UpdateStatus CheckCore(bool manual, CancellationToken ct)
        {
            if (_settings.Disabled || InPlace) return Publish(Decide(null));

            if (!manual)
            {
                var last = _settings.LastChecked;
                if (!_settings.AutoCheck || (last.HasValue && DateTime.UtcNow - last.Value < AutoCheckInterval))
                    return Refresh();
            }

            var source = Source;
            if (source.Manifest == null) return CheckFailed("update source " + source.Describe());

            UpdateManifest manifest;
            Uri used;
            try
            {
                used = source.Manifest;
                string json;
                try
                {
                    json = _transport.GetText(used, CheckTimeout, ct);
                }
                catch (UpdateNotFoundException) when (UpdateSource.IsGitHubLatest(used))
                {
                    // The latest release has no manifest (an older release, or a non-version release
                    // marked latest): ask the API for the newest "v" release that has one.
                    var picked = UpdateSource.PickFromReleases(
                        _transport.GetText(new Uri(UpdateSource.ReleasesApi), CheckTimeout, ct),
                        UpdateManifest.FileName(_running.RevitYear));
                    if (picked == null)
                        return CheckFailed("no published release has " + UpdateManifest.FileName(_running.RevitYear) +
                                           " yet (releases before in-app updates do not)");
                    used = picked;
                    json = _transport.GetText(used, CheckTimeout, ct);
                }
                if (!UpdateManifest.TryParse(json, _running.RevitYear, out manifest, out var error))
                    return CheckFailed(error);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                return CheckFailed("could not read " + DescribeUri(source.Manifest) + ": " + Plain(ex));
            }

            lock (_lock)
            {
                _manifest = manifest;
                _manifestUri = used;
            }
            SaveCachedManifest();
            var status = Decide(null);
            status.CheckedAt = DateTime.UtcNow;
            _settings.RecordCheck(status.CheckedAt.Value, UpdateDecision.Describe(status));
            return Publish(status);
        }

        // --- Install -----------------------------------------------------------------------

        /// <summary>
        /// Downloads the latest version, verifies size + sha256, unpacks it into versions\&lt;version&gt;,
        /// points current.txt at it and prunes old folders. The running add-in and the loader are
        /// never touched; the new version starts with the next Revit start.
        /// </summary>
        public UpdateStatus Install(CancellationToken ct = default(CancellationToken))
        {
            if (Interlocked.CompareExchange(ref _installing, 1, 0) != 0) return Status;
            string partial = null;
            string zip = null;
            string tmp = null;
            try
            {
                var decided = Decide(null);
                if (decided.State == UpdateState.NotChecked || decided.State == UpdateState.Failed)
                    decided = CheckCore(true, ct);
                if (decided.State != UpdateState.Available)
                    return Publish(decided);

                UpdateManifest m;
                Uri manifestUri;
                lock (_lock)
                {
                    m = _manifest;
                    manifestUri = _manifestUri;
                }
                var folder = m.VersionText;
                if (!LoaderPaths.IsPlainFolderName(folder) || !LoaderPaths.TryParseFolderVersion(folder, out _))
                    return ActionFailed("the manifest's version '" + folder + "' cannot be a folder name");

                if (!_layout.IsComplete(folder))
                {
                    var zipUri = UpdateSource.ZipUri(manifestUri ?? Source.Manifest, m);
                    Directory.CreateDirectory(_layout.DownloadDir);
                    partial = Path.Combine(_layout.DownloadDir, m.Zip + ".partial");
                    zip = Path.Combine(_layout.DownloadDir, m.Zip);
                    TryDelete(partial);

                    SetProgress(new InstallProgress { Phase = "download", Bytes = 0, Total = m.Size }, force: true);
                    using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        deadline.CancelAfter(UpdateTransport.DownloadTimeout);
                        try
                        {
                            _transport.Download(zipUri, partial,
                                (bytes, total) => SetProgress(new InstallProgress { Phase = "download", Bytes = bytes, Total = total > 0 ? total : m.Size }, force: false),
                                deadline.Token);
                        }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            return InstallFailed(m.VersionText, "the download did not finish within " + UpdateTransport.DownloadTimeout.TotalMinutes + " minutes");
                        }
                        catch (Exception ex) when (!(ex is OperationCanceledException))
                        {
                            return InstallFailed(m.VersionText, "the download of " + DescribeUri(zipUri) + " failed: " + Plain(ex));
                        }
                    }

                    var length = new FileInfo(partial).Length;
                    var hash = Sha256Of(partial);
                    if (length != m.Size || !string.Equals(hash, m.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        TryDelete(partial);
                        return InstallFailed(m.VersionText, "the download did not match the published checksum (got " + length + " bytes, sha256 " +
                                    hash.Substring(0, 12) + "...; expected " + m.Size + " bytes, " + m.Sha256.Substring(0, 12) + "...) - nothing was installed");
                    }
                    TryDelete(zip);
                    File.Move(partial, zip);
                    partial = null;

                    SetProgress(new InstallProgress { Phase = "unpack", Bytes = m.Size, Total = m.Size }, force: true);
                    tmp = _layout.FolderPath(folder + ".tmp");
                    if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
                    ZipSafety.ExtractTo(zip, tmp);
                    if (!File.Exists(Path.Combine(tmp, LoaderPaths.PayloadFile)))
                        return InstallFailed(m.VersionText, "the update zip has no " + LoaderPaths.PayloadFile + " at its top level - nothing was installed");

                    var final = _layout.FolderPath(folder);
                    if (Directory.Exists(final)) Directory.Delete(final, true);   // incomplete leftover (no VibeModel.dll)
                    Directory.Move(tmp, final);
                    tmp = null;
                }

                var note = LoaderNote(_layout.FolderPath(folder), m);
                lock (_lock) _installError = _installErrorVersion = null;
                _layout.WriteCurrent(folder);
                var removed = _layout.Prune(RunningFolder, folder);

                var status = Decide(null);
                status.Note = note;
                status.CheckedAt = Status.CheckedAt;
                Infrastructure.Logger.Info("Update: VibeModel " + folder + " installed in " + _layout.FolderPath(folder) +
                                           "; active after Revit restarts" + (removed.Count > 0 ? "; removed " + string.Join(", ", removed) : "") +
                                           (note != null ? "; " + note : ""));
                lock (_lock) _progress = null;
                return Publish(status);
            }
            catch (OperationCanceledException)
            {
                return ActionFailed("the install was cancelled");
            }
            catch (Exception ex)
            {
                return InstallFailed(Status.Latest?.VersionText, "the install failed: " + Plain(ex));
            }
            finally
            {
                // Nothing half-done stays behind; the next attempt downloads again.
                TryDelete(partial);
                TryDelete(zip);
                if (tmp != null) TryDeleteDir(tmp);
                lock (_lock) _progress = null;
                Interlocked.Exchange(ref _installing, 0);
            }
        }

        // The loader is never replaced by an update (Revit holds it, and it is the file Revit asked
        // about). Say so when the zip carries a newer one.
        private string LoaderNote(string folder, UpdateManifest m)
        {
            try
            {
                var shipped = Path.Combine(folder, LoaderFile);
                if (!File.Exists(shipped)) return null;
                if (!Version.TryParse(FileVersionInfo.GetVersionInfo(shipped).FileVersion ?? "", out var v)) return null;
                var installed = _running.LoaderVersion;
                if (installed != null && LoaderPaths.Normalize(v) <= LoaderPaths.Normalize(installed)) return null;
                return "the loader changed too (" + UpdateManifest.ShortVersion(v) + ", installed " +
                       (installed == null ? "none" : UpdateManifest.ShortVersion(installed)) +
                       ") - run install.cmd from " + folder + " once to get it" +
                       (m.Notes != null ? " (" + m.Notes + ")" : "");
            }
            catch
            {
                return null;
            }
        }

        // --- Rollback ----------------------------------------------------------------------

        /// <summary>Points current.txt at the previous installed version (or cancels a pending update).</summary>
        public UpdateStatus Rollback()
        {
            if (Volatile.Read(ref _installing) != 0) return Status;
            if (InPlace) return Publish(Decide(null));
            var target = RollbackTarget;
            if (target == null) return ActionFailed("no earlier version is installed in " + _layout.VersionsRoot);
            try
            {
                _layout.WriteCurrent(target);
            }
            catch (Exception ex)
            {
                return ActionFailed("could not write " + _layout.CurrentFile + ": " + Plain(ex));
            }
            Infrastructure.Logger.Info("Update: rolled back - Revit starts VibeModel " + target + " next time");
            var status = Decide(null);
            status.CheckedAt = Status.CheckedAt;
            return Publish(status);
        }

        // --- helpers -----------------------------------------------------------------------

        private UpdateStatus Decide(string checkError)
        {
            UpdateManifest m;
            string failedVersion, failure;
            lock (_lock)
            {
                m = _manifest;
                failedVersion = _installErrorVersion;
                failure = _installError;
            }
            var status = UpdateDecision.Decide(_settings.Disabled, _running.Version, InPlace, _running.LoaderVersion,
                Pending, m, checkError);
            if (status.State == UpdateState.Available && status.Reason == null && failure != null &&
                string.Equals(failedVersion, m?.VersionText, StringComparison.OrdinalIgnoreCase))
                status.Reason = failure;
            return status;
        }

        // Three kinds of failure, one place that words them:
        // - a check: without a known manifest the state is Failed; with one (cached) it still decides,
        //   and the failure is the status' LastError — never shown as a failed install;
        // - an install: the update stays Available with the failure as its Reason (the banner says the
        //   install failed), also after a later check of the same version;
        // - another action (rollback, cancel): LastError, the state as it is.
        private UpdateStatus CheckFailed(string reason) => Failure(reason, check: true);

        private UpdateStatus InstallFailed(string version, string reason)
        {
            lock (_lock)
            {
                _installErrorVersion = version;
                _installError = reason;
            }
            return Failure(reason, check: false);
        }

        private UpdateStatus ActionFailed(string reason) => Failure(reason, check: false);

        private UpdateStatus Failure(string reason, bool check)
        {
            Infrastructure.Logger.Warn("Update: " + reason);
            var status = Decide(check ? reason : null);
            if (status.State == UpdateState.NotChecked)
                status = status.With(UpdateState.Failed, reason);
            else if (status.Reason != reason)
                status.LastError = reason;
            status.CheckedAt = check ? DateTime.UtcNow : Status.CheckedAt;
            if (check) _settings.RecordCheck(status.CheckedAt.Value, "failed: " + reason);
            return Publish(status);
        }

        private UpdateStatus Publish(UpdateStatus status)
        {
            lock (_lock) _status = status;
            RaiseChanged(status);
            return status;
        }

        private void SetProgress(InstallProgress progress, bool force)
        {
            UpdateStatus status;
            lock (_lock)
            {
                _progress = progress;
                var now = DateTime.UtcNow;
                if (!force && now - _lastProgressEventUtc < TimeSpan.FromMilliseconds(250)) return;
                _lastProgressEventUtc = now;
                if (_status.State != UpdateState.Installing) _status = _status.With(UpdateState.Installing);
                status = _status;
            }
            RaiseChanged(status);
        }

        private void RaiseChanged(UpdateStatus status)
        {
            try
            {
                Changed?.Invoke(status);
            }
            catch (Exception ex)
            {
                Infrastructure.Logger.Warn("Update: a status listener failed: " + ex.Message);
            }
        }

        private static string Sha256Of(string file)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(file))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        private static string DescribeUri(Uri uri) => uri == null ? "?" : uri.IsFile ? uri.LocalPath : uri.AbsoluteUri;

        private static string Plain(Exception ex)
        {
            while (ex.InnerException != null && (ex is AggregateException || ex is System.Net.Http.HttpRequestException)) ex = ex.InnerException;
            return ex.Message;
        }

        private static void TryDelete(string file)
        {
            try { if (file != null && File.Exists(file)) File.Delete(file); } catch { }
        }

        private static void TryDeleteDir(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }

        // --- the manifest of the last successful check, so a restart within 24 h still knows ---

        private string CacheFile => Path.Combine(_layout.DownloadDir, "manifest-Revit" + _running.RevitYear + ".json");

        private void SaveCachedManifest()
        {
            try
            {
                UpdateManifest m;
                Uri uri;
                lock (_lock)
                {
                    m = _manifest;
                    uri = _manifestUri;
                }
                if (m == null || uri == null) return;
                Directory.CreateDirectory(_layout.DownloadDir);
                var data = new Dictionary<string, object>
                {
                    { "source", _settings.Source ?? "" },
                    { "manifestUri", uri.AbsoluteUri },
                    { "manifest", new Dictionary<string, object>
                        {
                            { "version", m.VersionText }, { "revitYear", m.RevitYear }, { "zip", m.Zip },
                            { "sha256", m.Sha256 }, { "size", m.Size }, { "published", m.Published ?? "" },
                            { "notes", m.Notes ?? "" }, { "minLoader", UpdateManifest.ShortVersion(m.MinLoader) }
                        }
                    }
                };
                File.WriteAllText(CacheFile, new JavaScriptSerializer().Serialize(data));
            }
            catch (Exception ex)
            {
                Infrastructure.Logger.Warn("Update: could not cache the manifest: " + ex.Message);
            }
        }

        private void LoadCachedManifest()
        {
            try
            {
                if (!File.Exists(CacheFile)) return;
                var serializer = new JavaScriptSerializer();
                var data = serializer.DeserializeObject(File.ReadAllText(CacheFile)) as Dictionary<string, object>;
                if (data == null) return;
                // A cache from another source (IT changed UpdateSource) means nothing now.
                if (!string.Equals(data.TryGetValue("source", out var s) ? s as string : null, _settings.Source ?? "", StringComparison.OrdinalIgnoreCase))
                    return;
                if (!data.TryGetValue("manifestUri", out var u) || !Uri.TryCreate(u as string, UriKind.Absolute, out var uri)) return;
                if (!data.TryGetValue("manifest", out var md)) return;
                if (!UpdateManifest.TryParse(serializer.Serialize(md), _running.RevitYear, out var m, out _)) return;
                _manifest = m;
                _manifestUri = uri;
            }
            catch
            {
                // A broken cache is ignored; the next check rewrites it.
            }
        }
    }
}
