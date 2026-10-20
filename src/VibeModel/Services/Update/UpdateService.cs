using System;
using System.Threading;
using System.Threading.Tasks;
using VibeModel.Infrastructure;

namespace VibeModel.Services.Update
{
    /// <summary>
    /// The in-app update (plan 19), as the rest of the add-in sees it: one <see cref="UpdateEngine"/>
    /// for this Revit session, the startup check (8 s after startup, off the main thread, once per
    /// session), and background check / install / rollback for the banner, Settings and `update`.
    /// </summary>
    public static class UpdateService
    {
        public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(8);

        private static readonly object Lock = new object();
        private static UpdateEngine _engine;
        private static string _revitYear;
        private static int _startupCheckScheduled;

        /// <summary>Raised on a background thread when the status or the install progress changes.</summary>
        public static event Action<UpdateStatus> Changed;

        /// <summary>The Revit year (from Revit at startup; else the year this build targets).</summary>
        public static string RevitYear
        {
            get
            {
                if (!string.IsNullOrEmpty(_revitYear)) return _revitYear;
#if REVIT2022
                return "2022";
#elif REVIT2024
                return "2024";
#elif REVIT2025
                return "2025";
#else
                return "2023";
#endif
            }
            set { _revitYear = value; }
        }

        public static UpdateEngine Engine
        {
            get
            {
                lock (Lock)
                {
                    if (_engine == null)
                    {
                        var asm = typeof(UpdateService).Assembly;
                        _engine = new UpdateEngine(UpdateLayout.Default, new UpdateTransport(), new LiveUpdateSettings(),
                            new RunningInfo
                            {
                                Version = asm.GetName().Version,
                                PayloadPath = asm.Location,
                                LoaderVersion = LoaderInfo.ParsedVersion,
                                RevitYear = RevitYear
                            });
                        _engine.Changed += s => Changed?.Invoke(s);
                    }
                    return _engine;
                }
            }
        }

        public static UpdateStatus Status => Engine.Status;

        /// <summary>Called once from OnStartup: check 8 s later in the background (never delays Revit's start).</summary>
        public static void ScheduleStartupCheck()
        {
            if (Interlocked.Exchange(ref _startupCheckScheduled, 1) != 0) return;
            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(StartupDelay).ConfigureAwait(false);
                    var status = Engine.Check(manual: false);
                    Logger.Info("Update check at startup: " + UpdateEngine.OneLine(status) + " (source " + Engine.Source.Describe() + ")");
                }
                catch (Exception ex)
                {
                    Logger.Warn("Update check at startup failed: " + ex.Message);
                }
            });
        }

        public static Task<UpdateStatus> CheckAsync(bool manual) => Task.Run(() => Engine.Check(manual));

        public static Task<UpdateStatus> InstallAsync() => Task.Run(() => Engine.Install());

        public static Task<UpdateStatus> RollbackAsync() => Task.Run(() => Engine.Rollback());

        /// <summary>Where VibeModel runs from, in words (Settings, Diagnose, `update`).</summary>
        public static string DescribeRunningFrom()
        {
            var e = Engine;
            var folder = e.RunningFolder;
            if (LoaderInfo.Version == null)
                return e.Running.PayloadPath + " (started directly by the .addin, no loader)";
            if (folder == null)
                return e.Running.PayloadPath + " (developer build next to the loader)";
            return e.Running.PayloadPath + " (installed version " + folder + ")";
        }

        private sealed class LiveUpdateSettings : IUpdateSettings
        {
            public bool Disabled => SettingsManager.IsUpdateDisabled();
            public string Source => SettingsManager.GetUpdateSource();
            public bool AutoCheck => SettingsManager.GetUpdateCheck();
            public DateTime? LastChecked => SettingsManager.GetUpdateLastChecked();
            public void RecordCheck(DateTime utc, string result) => SettingsManager.SetUpdateLastCheck(utc, result);
        }
    }
}
