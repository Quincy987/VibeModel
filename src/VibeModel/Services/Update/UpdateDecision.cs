using System;

namespace VibeModel.Services.Update
{
    /// <summary>What the update check concluded (plan 19).</summary>
    public enum UpdateState
    {
        /// <summary>Not checked yet this session (and nothing pending).</summary>
        NotChecked,
        /// <summary>IT switched updates off (managed.json DisableUpdates).</summary>
        Disabled,
        /// <summary>The running version is the newest published one (or newer).</summary>
        UpToDate,
        /// <summary>A newer version can be downloaded and installed.</summary>
        Available,
        /// <summary>A newer version is downloading / unpacking.</summary>
        Installing,
        /// <summary>current.txt names another version than the running one: Revit starts it next time.</summary>
        ReadyAtRestart,
        /// <summary>VibeModel runs from the Revit add-ins folder (a developer build, or no loader): an update would never be started.</summary>
        InPlaceBuild,
        /// <summary>The new version needs a newer loader than the one installed: run install.cmd from the zip once.</summary>
        LoaderTooOld,
        /// <summary>The check or the install failed (<see cref="UpdateStatus.Reason"/>).</summary>
        Failed
    }

    /// <summary>One snapshot of the update state, for the banner, Settings, Diagnose and `update`.</summary>
    public sealed class UpdateStatus
    {
        public UpdateState State { get; set; }
        /// <summary>The newest published version found (null when not known).</summary>
        public UpdateManifest Latest { get; set; }
        /// <summary>The version Revit starts next time when it is not the running one (current.txt).</summary>
        public string Pending { get; set; }
        /// <summary>
        /// Why the state is what it is — English, for logs and `update`: Failed, InPlaceBuild,
        /// LoaderTooOld, Disabled, and Available after a failed install (the banner then says the
        /// install failed).
        /// </summary>
        public string Reason { get; set; }
        /// <summary>
        /// A check or action that failed WITHOUT changing the state — a check that could not reach the
        /// source while an earlier check's manifest still decides, a refused rollback. Never shown as a
        /// failed install.
        /// </summary>
        public string LastError { get; set; }
        /// <summary>A note that does not change the state ("the loader changed too …").</summary>
        public string Note { get; set; }
        public DateTime? CheckedAt { get; set; }
        /// <summary>The running add-in's version (normalized).</summary>
        public Version Running { get; set; }

        public UpdateStatus With(UpdateState state, string reason = null)
        {
            return new UpdateStatus
            {
                State = state,
                Latest = Latest,
                Pending = Pending,
                Reason = reason ?? Reason,
                LastError = LastError,
                Note = Note,
                CheckedAt = CheckedAt,
                Running = Running
            };
        }
    }

    /// <summary>
    /// The pure decision: running version + where it runs from + loader + pending folder + what the
    /// check found → one <see cref="UpdateState"/>. Order: Disabled, InPlaceBuild, ReadyAtRestart
    /// (a pending version at least as new as the latest, or a rollback), Failed (no manifest), UpToDate,
    /// LoaderTooOld, Available.
    /// </summary>
    public static class UpdateDecision
    {
        public const string InPlaceReason =
            "VibeModel runs from the Revit add-ins folder (a developer build, or an install without the loader), " +
            "so Revit would never start an update. Install the release zip with install.cmd once.";

        /// <summary>True when the pending version is older than the running one (a rollback, not an update).</summary>
        public static bool IsRollback(string pending, Version running) =>
            running != null && Loader.LoaderPaths.TryParseFolderVersion(pending, out var p) && p < Loader.LoaderPaths.Normalize(running);

        public static UpdateStatus Decide(bool disabled, Version running, bool inPlace, Version loader,
            string pending, UpdateManifest latest, string checkError)
        {
            var status = new UpdateStatus { Latest = latest, Pending = pending, Running = Loader.LoaderPaths.Normalize(running) };
            if (disabled) return status.With(UpdateState.Disabled, "updates are switched off by IT (managed.json DisableUpdates)");
            if (inPlace) return status.With(UpdateState.InPlaceBuild, InPlaceReason);

            var runningN = Loader.LoaderPaths.Normalize(running);
            // A pending version at least as new as the latest (an installed update), or older than
            // the running one (a rollback): either way Revit's next start changes the version.
            if (pending != null && Loader.LoaderPaths.TryParseFolderVersion(pending, out var pendingV) &&
                (latest == null || pendingV >= latest.Version || (runningN != null && pendingV < runningN)))
                return status.With(UpdateState.ReadyAtRestart);

            if (latest == null)
                return status.With(checkError == null ? UpdateState.NotChecked : UpdateState.Failed, checkError);

            if (runningN != null && latest.Version <= runningN)
                return status.With(UpdateState.UpToDate);

            if (loader == null || latest.MinLoader > Loader.LoaderPaths.Normalize(loader))
                return status.With(UpdateState.LoaderTooOld,
                    "VibeModel " + latest.VersionText + " needs loader " + UpdateManifest.ShortVersion(latest.MinLoader) +
                    " or newer (installed: " + (loader == null ? "none" : UpdateManifest.ShortVersion(loader)) +
                    "). Download " + latest.Zip + " and run install.cmd from it once.");

            return status.With(UpdateState.Available);
        }

        /// <summary>
        /// The state in plain English with its reason — the one English wording, used by `update`,
        /// Diagnose, the log and settings.json's UpdateLastResult (the pane's own, localized wording
        /// is <c>UiText.UpdateLine</c>).
        /// </summary>
        public static string Describe(UpdateStatus s)
        {
            if (s == null) return "not checked yet";
            var latest = s.Latest?.VersionText;
            switch (s.State)
            {
                case UpdateState.Disabled: return "updates are switched off by IT (managed.json DisableUpdates)";
                case UpdateState.UpToDate: return "up to date (latest " + latest + ")";
                case UpdateState.Available:
                    return "VibeModel " + latest + " is available - `update --install` (or the chat pane's Update button)" +
                           (s.Reason != null ? "; the last install failed: " + s.Reason : "");
                case UpdateState.Installing: return "installing VibeModel " + latest;
                case UpdateState.ReadyAtRestart:
                    return IsRollback(s.Pending, s.Running)
                        ? "rolled back - Revit starts VibeModel " + s.Pending + " next time"
                        : "VibeModel " + s.Pending + " is installed and becomes active after Revit restarts";
                case UpdateState.InPlaceBuild: return "developer build / no loader - " + s.Reason;
                case UpdateState.LoaderTooOld: return "needs install.cmd - " + s.Reason;
                case UpdateState.Failed: return "check failed - " + s.Reason;
                default: return "not checked yet";
            }
        }
    }
}
