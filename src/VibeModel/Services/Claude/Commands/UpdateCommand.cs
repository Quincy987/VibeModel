using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using VibeModel.Infrastructure;
using VibeModel.Services.Chat;
using VibeModel.Services.Update;

namespace VibeModel.Services.Claude.Commands
{
    /// <summary>
    /// In-app updates (plan 19): `update` checks now and reports, `update --install` starts the
    /// download + unpack in the background (the new version is active after Revit restarts),
    /// `update --rollback` points the loader back at the previous installed version. Not a model
    /// change: no transaction, no undo step, no journal, no approval. IT's DisableUpdates is decided
    /// by the engine like everything else (state "disabled").
    /// </summary>
    public class UpdateCommand : IClaudeCommand, IStructuredCommand
    {
        // The command runs on Revit's main thread: a check normally answers in well under a second,
        // and a source that does not is given up on (the check goes on in the background).
        private static readonly TimeSpan CommandDeadline = TimeSpan.FromSeconds(12);

        public string Name => "update";
        public string Description =>
            "VibeModel updates: check (installed vs latest; up to date / available / ready at restart / developer build / needs install.cmd), " +
            "--install (download in the background, active after Revit restarts), --rollback (previous version next start)";
        public string Usage => "update [--install | --rollback]";

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var a = (args ?? "").Trim().ToLowerInvariant();
            var engine = UpdateService.Engine;
            string action = null;
            UpdateStatus status;

            if (a == "--install" || a == "install")
            {
                if (engine.Status.State == UpdateState.Installing)
                {
                    action = "An install is already running.";
                    status = engine.Status;
                }
                else
                {
                    // Check first (the install needs the manifest), then download in the background:
                    // the zip can take a minute and Revit's main thread must not wait for it.
                    status = Wait(UpdateService.CheckAsync(manual: true), engine, "the update source did not answer");
                    if (status.State == UpdateState.Available)
                    {
                        UpdateService.InstallAsync();
                        status = status.With(UpdateState.Installing);
                        action = "Install of VibeModel " + status.Latest.VersionText + " started in the background; run `update` for progress. " +
                                 "It becomes active after Revit restarts.";
                    }
                    else
                    {
                        action = "Nothing to install.";
                    }
                }
            }
            else if (a == "--rollback" || a == "rollback")
            {
                var target = engine.RollbackTarget;
                if (engine.InPlace || target == null)
                {
                    status = engine.Refresh();
                    action = "Rollback not possible: " + (engine.InPlace ? UpdateDecision.InPlaceReason : "no earlier version is installed.");
                }
                else
                {
                    status = Wait(UpdateService.RollbackAsync(), engine, "the rollback did not finish");
                    action = status.LastError == null
                        ? "Rollback: Revit starts VibeModel " + target + " next time."
                        : "Rollback failed: " + status.LastError;
                }
            }
            else if (a.Length == 0 || a == "--check" || a == "check")
            {
                status = engine.Status.State == UpdateState.Installing
                    ? engine.Status
                    : Wait(UpdateService.CheckAsync(manual: true), engine, "the update source did not answer");
            }
            else
            {
                return CommandResult.Error("BAD_ARGS", "Unknown option '" + args.Trim() + "'", "Usage: " + Usage);
            }

            return Report(engine, status, action);
        }

        private static UpdateStatus Wait(Task<UpdateStatus> task, UpdateEngine engine, string timeoutText)
        {
            try
            {
                if (task.Wait(CommandDeadline)) return task.Result;
            }
            catch (AggregateException ex)
            {
                return engine.Status.With(UpdateState.Failed, ex.InnerException?.Message ?? ex.Message);
            }
            return engine.Status.With(UpdateState.Failed, timeoutText + " within " + CommandDeadline.TotalSeconds + " s");
        }

        internal static CommandResult Report(UpdateEngine engine, UpdateStatus status, string action)
        {
            var source = engine.Source;
            var installed = UpdateManifest.ShortVersion(engine.Running.Version);
            var sb = new StringBuilder();
            if (action != null) sb.AppendLine(action);
            sb.AppendLine("State: " + UpdateDecision.Describe(status));
            if (status.LastError != null) sb.AppendLine("Last check/action failed: " + status.LastError);
            sb.AppendLine("Installed: VibeModel " + installed + " · loader " + LoaderInfo.Describe());
            sb.AppendLine("Running from: " + UpdateService.DescribeRunningFrom());
            sb.AppendLine("Source: " + source.Describe() + (ManagedSettings.IsManaged(ManagedSettings.UpdateSource) ? " (managed by IT)" : ""));
            if (status.Latest != null)
                sb.AppendLine("Latest: " + status.Latest.VersionText + " (" + ChatAttachment.FormatSize(status.Latest.Size) +
                              (status.Latest.Published != null ? ", published " + status.Latest.Published : "") + ")" +
                              (status.Latest.Notes != null ? " - " + status.Latest.Notes : ""));
            if (status.Pending != null) sb.AppendLine("Next Revit start: VibeModel " + status.Pending);
            var progress = engine.Progress;
            if (status.State == UpdateState.Installing && progress != null)
                sb.AppendLine("Progress: " + progress.Phase + " " + ChatAttachment.FormatSize(progress.Bytes) + " / " +
                              ChatAttachment.FormatSize(progress.Total));
            if (status.Note != null) sb.AppendLine("Note: " + status.Note);
            var rollback = engine.RollbackTarget;
            if (rollback != null) sb.AppendLine("Rollback possible to: " + rollback + " (update --rollback)");
            if (status.CheckedAt.HasValue)
                sb.AppendLine("Checked: " + status.CheckedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));

            var data = new Dictionary<string, object>
            {
                { "state", StateName(status.State) },
                { "installed", installed },
                { "loader", LoaderInfo.Version },
                { "runningFrom", engine.Running.PayloadPath },
                { "inPlace", engine.InPlace },
                { "source", source.Describe() },
                { "latest", status.Latest?.VersionText },
                { "pending", status.Pending },
                { "reason", status.Reason },
                { "lastError", status.LastError },
                { "note", status.Note },
                { "rollbackTo", rollback },
                { "checkedAt", status.CheckedAt?.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) }
            };
            if (status.State == UpdateState.Installing && progress != null)
                data["progress"] = new Dictionary<string, object> { { "phase", progress.Phase }, { "bytes", progress.Bytes }, { "total", progress.Total } };
            return CommandResult.Ok(sb.ToString().TrimEnd(), data);
        }

        /// <summary>The JSON name of a state: the enum name in camelCase ("readyAtRestart").</summary>
        internal static string StateName(UpdateState state)
        {
            var n = state.ToString();
            return char.ToLowerInvariant(n[0]) + n.Substring(1);
        }
    }
}
