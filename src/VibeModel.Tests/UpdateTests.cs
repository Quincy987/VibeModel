using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using VibeModel.Services.Chat;
using VibeModel.Services.Claude.Commands;
using VibeModel.Services.Update;
using VibeModel.UI;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>Plan 19, the pure parts: manifest, decision, source, zip safety, wording.</summary>
    public class UpdatePureTests
    {
        private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private static string Json(string version = "1.2.13", string year = "2023", string zip = null, string sha = Sha,
            string size = "3456789", string minLoader = null, string notes = "https://github.com/Quincy987/VibeModel/releases/tag/v1.2.13")
        {
            zip = zip ?? "VibeModel-v" + version + "-Revit" + year + ".zip";
            return "{\"version\":\"" + version + "\",\"revitYear\":\"" + year + "\",\"zip\":\"" + zip + "\",\"sha256\":\"" + sha +
                   "\",\"size\":" + size + ",\"published\":\"2026-10-20\",\"notes\":\"" + notes + "\"" +
                   (minLoader != null ? ",\"minLoader\":\"" + minLoader + "\"" : "") + "}";
        }

        private static UpdateManifest Manifest(string version = "1.2.13", string minLoader = null)
        {
            Assert.True(UpdateManifest.TryParse(Json(version, minLoader: minLoader), "2023", out var m, out var error), error);
            return m;
        }

        [Fact]
        public void Manifest_Valid()
        {
            var m = Manifest();
            Assert.Equal(new Version(1, 2, 13, 0), m.Version);
            Assert.Equal("1.2.13", m.VersionText);
            Assert.Equal("VibeModel-v1.2.13-Revit2023.zip", m.Zip);
            Assert.Equal(3456789, m.Size);
            Assert.Equal(new Version(1, 0, 0, 0), m.MinLoader);
            Assert.StartsWith("https://", m.Notes);
        }

        [Theory]
        [InlineData("{}", "no valid version")]
        [InlineData("not json", "not valid JSON")]
        public void Manifest_Broken(string json, string expected)
        {
            Assert.False(UpdateManifest.TryParse(json, "2023", out _, out var error));
            Assert.Contains(expected, error);
        }

        [Fact]
        public void Manifest_RefusesWhatDoesNotValidate()
        {
            Assert.False(UpdateManifest.TryParse(Json(sha: "abc"), "2023", out _, out var e1));
            Assert.Contains("sha256", e1);
            Assert.False(UpdateManifest.TryParse(Json(size: "0"), "2023", out _, out var e2));
            Assert.Contains("size", e2);
            Assert.False(UpdateManifest.TryParse(Json(zip: "..\\\\evil.zip"), "2023", out _, out var e3));
            Assert.Contains("expected 'VibeModel-v1.2.13-Revit2023.zip'", e3);
            Assert.False(UpdateManifest.TryParse(Json(zip: "https://evil/VibeModel-v1.2.13-Revit2023.zip"), "2023", out _, out _));
            Assert.False(UpdateManifest.TryParse(Json(year: "2024"), "2023", out _, out var e4));
            Assert.Contains("Revit 2024", e4);
            Assert.False(UpdateManifest.TryParse(Json(minLoader: "x"), "2023", out _, out _));
            // A notes link that is not https is dropped, the rest stays valid.
            Assert.True(UpdateManifest.TryParse(Json(notes: "file://c:/x"), "2023", out var m, out _));
            Assert.Null(m.Notes);
        }

        [Fact]
        public void Decide_ComparesVersionsWithThreeOrFourParts()
        {
            var s = UpdateDecision.Decide(false, new Version(1, 2, 13, 0), false, new Version(1, 0, 0), null, Manifest(), null);
            Assert.Equal(UpdateState.UpToDate, s.State);
            s = UpdateDecision.Decide(false, new Version(1, 2, 12, 0), false, new Version(1, 0, 0), null, Manifest(), null);
            Assert.Equal(UpdateState.Available, s.State);
            // Installed newer than published (a test build): up to date, never a downgrade.
            s = UpdateDecision.Decide(false, new Version(1, 3, 0), false, new Version(1, 0, 0), null, Manifest(), null);
            Assert.Equal(UpdateState.UpToDate, s.State);
        }

        [Fact]
        public void Decide_Order()
        {
            var running = new Version(1, 2, 12);
            var loader = new Version(1, 0, 0);
            Assert.Equal(UpdateState.Disabled, UpdateDecision.Decide(true, running, true, null, "1.2.13", Manifest(), "x").State);
            var inPlace = UpdateDecision.Decide(false, running, true, loader, null, Manifest(), null);
            Assert.Equal(UpdateState.InPlaceBuild, inPlace.State);
            Assert.Contains("install.cmd", inPlace.Reason);
            Assert.Equal(UpdateState.ReadyAtRestart, UpdateDecision.Decide(false, running, false, loader, "1.2.13", Manifest(), null).State);
            Assert.Equal(UpdateState.ReadyAtRestart, UpdateDecision.Decide(false, running, false, loader, "1.2.13", null, null).State);
            // A rollback (pending older than running) is also "at the next start".
            var rb = UpdateDecision.Decide(false, new Version(1, 2, 13), false, loader, "1.2.12", Manifest(), null);
            Assert.Equal(UpdateState.ReadyAtRestart, rb.State);
            Assert.True(UpdateDecision.IsRollback(rb.Pending, rb.Running));
            Assert.Equal(UpdateState.NotChecked, UpdateDecision.Decide(false, running, false, loader, null, null, null).State);
            var failed = UpdateDecision.Decide(false, running, false, loader, null, null, "offline");
            Assert.Equal(UpdateState.Failed, failed.State);
            Assert.Equal("offline", failed.Reason);
            var old = UpdateDecision.Decide(false, running, false, loader, null, Manifest(minLoader: "1.1.0"), null);
            Assert.Equal(UpdateState.LoaderTooOld, old.State);
            Assert.Contains("1.1.0", old.Reason);
        }

        [Fact]
        public void Source_Default_IsGitHubLatestPerRevitYear()
        {
            var s = UpdateSource.Resolve("", "2024");
            Assert.True(s.IsDefault);
            Assert.Equal("https://github.com/Quincy987/VibeModel/releases/latest/download/latest-Revit2024.json", s.Manifest.AbsoluteUri);
            Assert.True(UpdateSource.IsGitHubLatest(s.Manifest));
        }

        [Fact]
        public void Source_Configured()
        {
            Assert.Equal("https://mirror.example/vm/latest-Revit2023.json",
                UpdateSource.Resolve("https://mirror.example/vm", "2023").Manifest.AbsoluteUri);
            Assert.Equal("https://mirror.example/vm/custom.json",
                UpdateSource.Resolve("https://mirror.example/vm/custom.json", "2023").Manifest.AbsoluteUri);
            Assert.NotNull(UpdateSource.Resolve("http://mirror.example/vm", "2023").Error);
            Assert.NotNull(UpdateSource.Resolve("relative\\folder", "2023").Error);

            var unc = UpdateSource.Resolve("\\\\server\\share\\VibeModel", "2023");
            Assert.True(unc.IsFile);
            Assert.Equal("\\\\server\\share\\VibeModel\\latest-Revit2023.json", unc.Manifest.LocalPath);
            var local = UpdateSource.Resolve("C:\\Mirror\\VibeModel updates", "2023");
            Assert.Equal("C:\\Mirror\\VibeModel updates\\latest-Revit2023.json", local.Manifest.LocalPath);
        }

        [Fact]
        public void ZipUri_LatestUsesTheManifestsOwnTag_FolderUsesTheSibling()
        {
            var m = Manifest();
            var latest = UpdateSource.Resolve("", "2023").Manifest;
            Assert.Equal("https://github.com/Quincy987/VibeModel/releases/download/v1.2.13/VibeModel-v1.2.13-Revit2023.zip",
                UpdateSource.ZipUri(latest, m).AbsoluteUri);
            var share = UpdateSource.Resolve("\\\\server\\share\\VibeModel", "2023").Manifest;
            Assert.Equal("\\\\server\\share\\VibeModel\\VibeModel-v1.2.13-Revit2023.zip", UpdateSource.ZipUri(share, m).LocalPath);
            var mirror = UpdateSource.Resolve("https://mirror.example/vm", "2023").Manifest;
            Assert.Equal("https://mirror.example/vm/VibeModel-v1.2.13-Revit2023.zip", UpdateSource.ZipUri(mirror, m).AbsoluteUri);
        }

        [Fact]
        public void PickFromReleases_FirstPublishedVReleaseWithTheManifest()
        {
            var json = "[" +
                       "{\"tag_name\":\"v1.3.0\",\"draft\":true,\"prerelease\":false,\"assets\":[{\"name\":\"latest-Revit2023.json\",\"browser_download_url\":\"https://x/draft.json\"}]}," +
                       "{\"tag_name\":\"v1.2.14-rc\",\"draft\":false,\"prerelease\":true,\"assets\":[{\"name\":\"latest-Revit2023.json\",\"browser_download_url\":\"https://x/rc.json\"}]}," +
                       "{\"tag_name\":\"demo-videos\",\"draft\":false,\"prerelease\":false,\"assets\":[{\"name\":\"latest-Revit2023.json\",\"browser_download_url\":\"https://x/demo.json\"}]}," +
                       "{\"tag_name\":\"v1.2.13\",\"draft\":false,\"prerelease\":false,\"assets\":[{\"name\":\"latest-Revit2024.json\",\"browser_download_url\":\"https://x/2024.json\"}," +
                       "{\"name\":\"latest-Revit2023.json\",\"browser_download_url\":\"https://github.com/Quincy987/VibeModel/releases/download/v1.2.13/latest-Revit2023.json\"}]}," +
                       "{\"tag_name\":\"v1.2.12\",\"draft\":false,\"prerelease\":false,\"assets\":[]}" +
                       "]";
            Assert.Equal("https://github.com/Quincy987/VibeModel/releases/download/v1.2.13/latest-Revit2023.json",
                UpdateSource.PickFromReleases(json, "latest-Revit2023.json").AbsoluteUri);
            Assert.Null(UpdateSource.PickFromReleases("[{\"tag_name\":\"v1.2.10\",\"assets\":[]}]", "latest-Revit2023.json"));
            Assert.Null(UpdateSource.PickFromReleases("{\"message\":\"rate limited\"}", "latest-Revit2023.json"));
        }

        [Theory]
        [InlineData("../evil.dll")]
        [InlineData("a/../../evil.dll")]
        [InlineData("/abs/evil.dll")]
        [InlineData("\\abs\\evil.dll")]
        [InlineData("C:evil.dll")]
        [InlineData("C:\\Windows\\evil.dll")]
        public void ZipSafety_RejectsEscapes(string entry)
        {
            Assert.Null(ZipSafety.SafePath("C:\\target\\1.2.13.tmp", entry));
        }

        [Fact]
        public void ZipSafety_AcceptsNormalEntries()
        {
            Assert.Equal("C:\\target\\x\\VibeModel.dll", ZipSafety.SafePath("C:\\target\\x", "VibeModel.dll"));
            Assert.Equal("C:\\target\\x\\VibeModel.PdfText\\a.exe", ZipSafety.SafePath("C:\\target\\x", "VibeModel.PdfText/a.exe"));
            Assert.Equal("C:\\target\\x\\VibeModel.PdfText\\a.exe", ZipSafety.SafePath("C:\\target\\x", "VibeModel.PdfText\\a.exe"));
        }

        [Fact]
        public void Wording_EveryStateHasDutchAndEnglish()
        {
            var m = Manifest();
            foreach (UpdateState state in Enum.GetValues(typeof(UpdateState)))
            {
                var s = new UpdateStatus { State = state, Latest = m, Pending = "1.2.13", Reason = state == UpdateState.Available ? null : "why", Running = new Version(1, 2, 12, 0) };
                var nl = UiText.UpdateLine(s, null, "nl");
                var en = UiText.UpdateLine(s, null, "en");
                Assert.False(string.IsNullOrWhiteSpace(nl), state + " nl");
                Assert.False(string.IsNullOrWhiteSpace(en), state + " en");
                Assert.NotEqual(nl, en);
                Assert.DoesNotContain(Strings.Keys, k => k == nl || k == en);   // never a raw key
                Assert.False(string.IsNullOrWhiteSpace(UpdateCommand.StateText(s)), state + " command");
            }
            Assert.Equal("VibeModel 1.2.13 beschikbaar (3,3 MB)",
                UiText.UpdateLine(new UpdateStatus { State = UpdateState.Available, Latest = m }, null, "nl"));
            Assert.Equal("VibeModel 1.2.13 is available (3.3 MB)",
                UiText.UpdateLine(new UpdateStatus { State = UpdateState.Available, Latest = m }, null, "en"));
            Assert.Equal("Downloaden… 1,0 MB / 3,3 MB",
                UiText.UpdateLine(new UpdateStatus { State = UpdateState.Installing, Latest = m },
                    new InstallProgress { Phase = "download", Bytes = 1048576, Total = m.Size }, "nl"));
            Assert.Equal("Revit start de volgende keer VibeModel 1.2.11.",
                UiText.UpdateLine(new UpdateStatus { State = UpdateState.ReadyAtRestart, Pending = "1.2.11", Running = new Version(1, 2, 12) }, null, "nl"));
        }

        [Fact]
        public void Strings_NewKeysHaveDutch()
        {
            foreach (var key in Strings.Keys.Where(k => k.StartsWith("Update") || k.StartsWith("SettingsUpdate")))
                Assert.True(Strings.HasDutch(key), key);
        }
    }

    /// <summary>Plan 19, the engine: check, download, verify, unpack, current.txt, prune, rollback — in a temp folder.</summary>
    public class UpdateEngineTests : IDisposable
    {
        private readonly string _root;
        private readonly string _versions;
        private readonly string _downloads;
        private readonly string _mirror;
        private readonly FakeSettings _settings = new FakeSettings();

        public UpdateEngineTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "vm-update-" + Guid.NewGuid().ToString("N"));
            _versions = Path.Combine(_root, "versions");
            _downloads = Path.Combine(_root, "updates");
            _mirror = Path.Combine(_root, "mirror");
            Directory.CreateDirectory(_mirror);
            InstallFolder("1.2.12");
            File.WriteAllText(Path.Combine(_versions, "current.txt"), "1.2.12");
            _settings.Source = _mirror;
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        private sealed class FakeSettings : IUpdateSettings
        {
            public bool Disabled { get; set; }
            public string Source { get; set; }
            public bool AutoCheck { get; set; } = true;
            public DateTime? LastChecked { get; set; }
            public string LastResult;
            public void RecordCheck(DateTime utc, string result)
            {
                LastChecked = utc;
                LastResult = result;
            }
        }

        private void InstallFolder(string version)
        {
            var dir = Path.Combine(_versions, version);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "VibeModel.dll"), "payload " + version);
        }

        private UpdateEngine Engine(string running = "1.2.12", IUpdateTransport transport = null, bool inPlace = false, Version loader = null)
        {
            return new UpdateEngine(new UpdateLayout(_versions, _downloads), transport ?? new UpdateTransport(), _settings,
                new RunningInfo
                {
                    Version = new Version(running + ".0"),
                    PayloadPath = inPlace ? Path.Combine(_root, "Addins", "VibeModel.dll") : Path.Combine(_versions, running, "VibeModel.dll"),
                    LoaderVersion = loader ?? new Version(1, 0, 0, 0),
                    RevitYear = "2023"
                });
        }

        // A release in the mirror folder: the zip (flat, like package-release.ps1 makes it) + its manifest.
        private string Publish(string version, string extraFile = null, Action<string> tamper = null)
        {
            var zipName = "VibeModel-v" + version + "-Revit2023.zip";
            var zip = Path.Combine(_mirror, zipName);
            if (File.Exists(zip)) File.Delete(zip);
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                Add(archive, "VibeModel.dll", "payload " + version);
                Add(archive, "Markdig.dll", "markdig");
                Add(archive, "VibeModel.PdfText/VibeModel.PdfText.exe", "pdf");
                Add(archive, "install.cmd", "@echo off");
                if (extraFile != null) archive.CreateEntryFromFile(extraFile, "VibeModel.Loader.dll");
            }
            string sha;
            using (var s = SHA256.Create())
            using (var f = File.OpenRead(zip))
                sha = BitConverter.ToString(s.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
            var size = new FileInfo(zip).Length;
            File.WriteAllText(Path.Combine(_mirror, "latest-Revit2023.json"),
                "{\"version\":\"" + version + "\",\"revitYear\":\"2023\",\"zip\":\"" + zipName + "\",\"sha256\":\"" + sha +
                "\",\"size\":" + size + ",\"published\":\"2026-10-20\",\"notes\":\"https://example.com/notes\",\"minLoader\":\"1.0.0\"}");
            tamper?.Invoke(zip);
            return zip;
        }

        private static void Add(ZipArchive archive, string name, string content)
        {
            using (var w = new StreamWriter(archive.CreateEntry(name).Open()))
                w.Write(content);
        }

        [Fact]
        public void Check_FindsANewerVersion_InAFolderSource()
        {
            Publish("1.2.13");
            var s = Engine().Check(manual: true);
            Assert.Equal(UpdateState.Available, s.State);
            Assert.Equal("1.2.13", s.Latest.VersionText);
            Assert.Equal("1.2.13 available", _settings.LastResult);
            Assert.NotNull(_settings.LastChecked);
        }

        [Fact]
        public void Install_UnpacksIntoItsOwnFolder_AndPointsCurrentAtIt()
        {
            Publish("1.2.13");
            var engine = Engine();
            var events = new List<UpdateState>();
            engine.Changed += s => events.Add(s.State);
            var result = engine.Install();

            Assert.Equal(UpdateState.ReadyAtRestart, result.State);
            Assert.Equal("1.2.13", result.Pending);
            Assert.Equal("1.2.13", File.ReadAllText(Path.Combine(_versions, "current.txt")).Trim());
            Assert.True(File.Exists(Path.Combine(_versions, "1.2.13", "VibeModel.dll")));
            Assert.True(File.Exists(Path.Combine(_versions, "1.2.13", "VibeModel.PdfText", "VibeModel.PdfText.exe")));
            Assert.Equal("payload 1.2.12", File.ReadAllText(Path.Combine(_versions, "1.2.12", "VibeModel.dll")));   // the running one untouched
            Assert.False(Directory.Exists(Path.Combine(_versions, "1.2.13.tmp")));
            Assert.Empty(Directory.GetFiles(_downloads, "*.zip*"));
            Assert.Contains(UpdateState.Installing, events);
            Assert.Equal("1.2.12", engine.RollbackTarget);
        }

        [Fact]
        public void Install_TamperedZip_IsRefused_AndLeavesNothing()
        {
            Publish("1.2.13", tamper: zip =>
            {
                var bytes = File.ReadAllBytes(zip);
                bytes[bytes.Length / 2] ^= 0xFF;
                File.WriteAllBytes(zip, bytes);
            });
            var result = Engine().Install();
            Assert.Equal(UpdateState.Available, result.State);   // still available; the reason says why it failed
            Assert.Contains("checksum", result.Reason);
            Assert.False(Directory.Exists(Path.Combine(_versions, "1.2.13")));
            Assert.Equal("1.2.12", File.ReadAllText(Path.Combine(_versions, "current.txt")).Trim());
            Assert.Empty(Directory.GetFiles(_downloads, "*.partial"));
        }

        [Fact]
        public void FailedInstall_StaysNamed_AfterANewCheck()
        {
            Publish("1.2.13", tamper: zip =>
            {
                var bytes = File.ReadAllBytes(zip);
                bytes[bytes.Length / 2] ^= 0xFF;
                File.WriteAllBytes(zip, bytes);
            });
            var engine = Engine();
            engine.Install();
            var s = engine.Check(manual: true);
            Assert.Equal(UpdateState.Available, s.State);
            Assert.Contains("checksum", s.Reason);
            Assert.Contains("the last attempt failed", UpdateCommand.StateText(s));
        }

        [Fact]
        public void Rollback_IsWordedAsARollback()
        {
            InstallFolder("1.2.11");
            var s = Engine().Rollback();
            Assert.StartsWith("rolled back", UpdateCommand.StateText(s));
            Assert.Equal("Revit start de volgende keer VibeModel 1.2.11.", UiText.UpdateLine(s, null, "nl"));
        }


        [Fact]
        public void Install_AlreadyInstalledVersion_NeedsNoDownload()
        {
            var zip = Publish("1.2.13");
            InstallFolder("1.2.13");
            File.Delete(zip);   // the download would fail
            var result = Engine().Install();
            Assert.Equal(UpdateState.ReadyAtRestart, result.State);
            Assert.Equal("1.2.13", File.ReadAllText(Path.Combine(_versions, "current.txt")).Trim());
        }

        [Fact]
        public void Install_ZipWithANewerLoader_SaysSo()
        {
            // Any DLL with a file version above 1.0 stands in for a newer loader.
            Publish("1.2.13", extraFile: typeof(Assert).Assembly.Location);
            var result = Engine().Install();
            Assert.Equal(UpdateState.ReadyAtRestart, result.State);
            Assert.Contains("the loader changed too", result.Note);
            Assert.Contains("install.cmd", result.Note);
        }

        [Fact]
        public void Prune_KeepsRunningCurrentAndOnePrevious()
        {
            InstallFolder("1.2.9");
            InstallFolder("1.2.10");
            InstallFolder("1.2.11");
            Directory.CreateDirectory(Path.Combine(_versions, "notaversion"));
            var removed = new UpdateLayout(_versions, _downloads).Prune("1.2.12", "1.2.13");
            Assert.Equal(new[] { "1.2.10", "1.2.9" }, removed.OrderBy(x => x, StringComparer.Ordinal).ToArray());
            Assert.True(Directory.Exists(Path.Combine(_versions, "1.2.11")));
            Assert.True(Directory.Exists(Path.Combine(_versions, "1.2.12")));
            Assert.True(Directory.Exists(Path.Combine(_versions, "notaversion")));
        }

        [Fact]
        public void Rollback_PointsCurrentAtThePreviousVersion()
        {
            InstallFolder("1.2.11");
            var engine = Engine();
            Assert.Equal("1.2.11", engine.RollbackTarget);
            var s = engine.Rollback();
            Assert.Equal(UpdateState.ReadyAtRestart, s.State);
            Assert.Equal("1.2.11", s.Pending);
            Assert.Equal("1.2.11", File.ReadAllText(Path.Combine(_versions, "current.txt")).Trim());
        }

        [Fact]
        public void Rollback_WithoutAnOlderVersion_Fails()
        {
            var s = Engine().Rollback();
            Assert.Contains("no earlier version", s.Reason);
            Assert.Equal("1.2.12", File.ReadAllText(Path.Combine(_versions, "current.txt")).Trim());
        }

        [Fact]
        public void Disabled_EvenOnAManualCheck()
        {
            Publish("1.2.13");
            _settings.Disabled = true;
            Assert.Equal(UpdateState.Disabled, Engine().Check(manual: true).State);
            Assert.Equal(UpdateState.Disabled, Engine().Install().State);
            Assert.False(Directory.Exists(Path.Combine(_versions, "1.2.13")));
        }

        [Fact]
        public void InPlaceBuild_NeverChecksOrInstalls()
        {
            Publish("1.2.13");
            Assert.Equal(UpdateState.InPlaceBuild, Engine(inPlace: true).Check(manual: true).State);
            Assert.Equal(UpdateState.InPlaceBuild, Engine(inPlace: true).Install().State);
            Assert.Null(_settings.LastChecked);
        }

        [Fact]
        public void AutomaticCheck_Within24Hours_UsesTheCachedManifest()
        {
            Publish("1.2.13");
            Assert.Equal(UpdateState.Available, Engine().Check(manual: true).State);
            Directory.Delete(_mirror, true);   // the source is gone: only the cache can know
            var s = Engine().Check(manual: false);
            Assert.Equal(UpdateState.Available, s.State);
            Assert.Equal("1.2.13", s.Latest.VersionText);
        }

        [Fact]
        public void AutomaticCheck_Off_DoesNotReachTheSource()
        {
            Publish("1.2.13");
            _settings.AutoCheck = false;
            Assert.Equal(UpdateState.NotChecked, Engine().Check(manual: false).State);
            Assert.Null(_settings.LastChecked);
            Assert.Equal(UpdateState.Available, Engine().Check(manual: true).State);
        }

        [Fact]
        public void MissingSource_FailsWithAReason()
        {
            var s = Engine().Check(manual: true);
            Assert.Equal(UpdateState.Failed, s.State);
            Assert.Contains("does not exist", s.Reason);
            Assert.StartsWith("failed:", _settings.LastResult);
        }

        [Fact]
        public void GitHubLatestWithoutManifest_FallsBackToTheReleasesApi()
        {
            _settings.Source = "";
            var manifestUrl = "https://github.com/Quincy987/VibeModel/releases/download/v1.2.13/latest-Revit2023.json";
            var transport = new FakeTransport();
            transport.Texts[UpdateSource.ReleasesApi] =
                "[{\"tag_name\":\"v1.2.13\",\"draft\":false,\"prerelease\":false,\"assets\":[{\"name\":\"latest-Revit2023.json\",\"browser_download_url\":\"" + manifestUrl + "\"}]}]";
            transport.Texts[manifestUrl] = "{\"version\":\"1.2.13\",\"revitYear\":\"2023\",\"zip\":\"VibeModel-v1.2.13-Revit2023.zip\",\"sha256\":\"" +
                                           new string('a', 64) + "\",\"size\":10}";
            var s = Engine(transport: transport).Check(manual: true);
            Assert.Equal(UpdateState.Available, s.State);
            Assert.Equal(new[] { "https://github.com/Quincy987/VibeModel/releases/latest/download/latest-Revit2023.json", UpdateSource.ReleasesApi, manifestUrl },
                transport.Asked.ToArray());
        }

        private sealed class FakeTransport : IUpdateTransport
        {
            public readonly Dictionary<string, string> Texts = new Dictionary<string, string>();
            public readonly List<string> Asked = new List<string>();

            public string GetText(Uri uri, TimeSpan timeout, CancellationToken ct)
            {
                Asked.Add(uri.AbsoluteUri);
                if (Texts.TryGetValue(uri.AbsoluteUri, out var text)) return text;
                throw new UpdateNotFoundException(uri + " (404)");
            }

            public void Download(Uri uri, string targetFile, Action<long, long> progress, CancellationToken ct) =>
                throw new UpdateNotFoundException(uri + " (404)");
        }
    }
}
