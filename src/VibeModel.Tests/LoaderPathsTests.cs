using System;
using System.IO;
using VibeModel.Loader;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>Plan 19: which VibeModel.dll the loader starts (the source is shared with the add-in).</summary>
    public class LoaderPathsTests : IDisposable
    {
        private readonly string _root;
        private readonly string _addins;
        private readonly string _versions;

        public LoaderPathsTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "vm-loader-" + Guid.NewGuid().ToString("N"));
            _addins = Path.Combine(_root, "Addins", "2023");
            _versions = Path.Combine(_root, "versions");
            Directory.CreateDirectory(_addins);
            Directory.CreateDirectory(_versions);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { }
        }

        private void AddVersion(string name, bool withDll = true)
        {
            var dir = Path.Combine(_versions, name);
            Directory.CreateDirectory(dir);
            if (withDll) File.WriteAllText(Path.Combine(dir, "VibeModel.dll"), "x");
        }

        private void Current(string text) => File.WriteAllText(Path.Combine(_versions, "current.txt"), text);

        [Fact]
        public void InPlaceBuild_NextToTheLoader_Wins()
        {
            File.WriteAllText(Path.Combine(_addins, "VibeModel.dll"), "dev");
            AddVersion("1.2.13");
            Current("1.2.13");
            var c = LoaderPaths.Choose(_addins, _versions);
            Assert.Equal(PayloadRule.InPlace, c.Rule);
            Assert.Equal(Path.Combine(_addins, "VibeModel.dll"), c.Path);
            Assert.Null(c.VersionFolder);
        }

        [Fact]
        public void CurrentTxt_NamesTheVersion()
        {
            AddVersion("1.2.12");
            AddVersion("1.2.13");
            Current("1.2.12\r\n");
            var c = LoaderPaths.Choose(_addins, _versions);
            Assert.Equal(PayloadRule.Current, c.Rule);
            Assert.Equal("1.2.12", c.VersionFolder);
            Assert.Equal(Path.Combine(_versions, "1.2.12", "VibeModel.dll"), c.Path);
        }

        [Fact]
        public void Newest_IsByVersion_NotByName()
        {
            AddVersion("1.9.0");
            AddVersion("1.10.0");
            var c = LoaderPaths.Choose(_addins, _versions);
            Assert.Equal(PayloadRule.Newest, c.Rule);
            Assert.Equal("1.10.0", c.VersionFolder);
        }

        [Fact]
        public void CurrentTxt_NamingAnIncompleteFolder_FallsBackToNewest()
        {
            AddVersion("1.2.12");
            AddVersion("1.2.13", withDll: false);
            Current("1.2.13");
            var c = LoaderPaths.Choose(_addins, _versions);
            Assert.Equal(PayloadRule.Newest, c.Rule);
            Assert.Equal("1.2.12", c.VersionFolder);
            Assert.Contains(c.LookedIn, l => l.Contains("(missing)"));
        }

        [Fact]
        public void Nothing_ReturnsNoPath_AndListsWhereItLooked()
        {
            var c = LoaderPaths.Choose(_addins, _versions);
            Assert.Null(c.Path);
            Assert.Equal(PayloadRule.None, c.Rule);
            Assert.Equal(3, c.LookedIn.Count);
            Assert.Contains(c.LookedIn, l => l.StartsWith(Path.Combine(_addins, "VibeModel.dll")));
        }

        [Fact]
        public void CurrentTxt_WithAPath_IsIgnored()
        {
            AddVersion("1.2.12");
            Current("..\\..\\evil");
            Assert.Null(LoaderPaths.ReadCurrent(_versions));
            Assert.Equal(PayloadRule.Newest, LoaderPaths.Choose(_addins, _versions).Rule);
            Current("C:\\x");
            Assert.Null(LoaderPaths.ReadCurrent(_versions));
        }

        [Fact]
        public void TmpAndOtherFolders_AreNotVersions()
        {
            AddVersion("1.2.13.tmp");
            AddVersion("1.2.14.tmp.4711");
            AddVersion("_old");
            AddVersion("1.2.12");
            Assert.Equal("1.2.12", LoaderPaths.NewestInstalled(_versions));
            Assert.Single(LoaderPaths.Installed(_versions));
        }

        [Fact]
        public void Normalize_MakesThreeAndFourPartVersionsEqual()
        {
            Assert.Equal(LoaderPaths.Normalize(new Version(1, 2, 12)), LoaderPaths.Normalize(new Version(1, 2, 12, 0)));
            Assert.True(LoaderPaths.Normalize(new Version(1, 2, 13)) > LoaderPaths.Normalize(new Version(1, 2, 12, 0)));
        }
    }
}
