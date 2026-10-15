using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using VibeModel.Services.Standards;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>
    /// Plan 11 — the standards pack model, fingerprint activation, discovery order and the
    /// prompt-block renderers. Runs against the committed sample pack (standards/example-office)
    /// plus throwaway packs in temp folders; no Revit.
    /// </summary>
    public class StandardsPackTests : IDisposable
    {
        private readonly string _temp = Path.Combine(Path.GetTempPath(), "vm-standards-" + Guid.NewGuid().ToString("N"));

        public StandardsPackTests() => Directory.CreateDirectory(_temp);

        public void Dispose()
        {
            try { Directory.Delete(_temp, true); } catch { }
        }

        internal static string ExamplePackFolder()
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "standards", "example-office");
                if (File.Exists(Path.Combine(candidate, "pack.json"))) return candidate;
                dir = dir.Parent;
            }
            throw new InvalidOperationException("standards/example-office not found above " + AppDomain.CurrentDomain.BaseDirectory);
        }

        private static StandardsPack Example()
        {
            var pack = StandardsPack.TryLoad(ExamplePackFolder(), out var error);
            Assert.True(pack != null, error);
            return pack;
        }

        private string WritePack(string folderName, string json, Dictionary<string, string> files = null)
        {
            var folder = Path.Combine(_temp, folderName);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "pack.json"), json, Encoding.UTF8);
            if (files != null)
                foreach (var kv in files)
                {
                    var p = Path.Combine(folder, kv.Key);
                    Directory.CreateDirectory(Path.GetDirectoryName(p));
                    File.WriteAllText(p, kv.Value, Encoding.UTF8);
                }
            return folder;
        }

        private static TemplateFacts ExampleFacts() => new TemplateFacts
        {
            Title = "2_Cofferdam North",
            Phases = { "1. Existing", "2. New construction", "3. Final" },
            ScopeBoxes = { "Project_extent_WCS", "Project_extent_UCS" },
            ProjectParameters = { "EX_ObjectCode", "EX_ObjectDescription", "Comments" }
        };

        // --- Loading ------------------------------------------------------------------------

        [Fact]
        public void ExamplePack_Parses()
        {
            var pack = Example();
            Assert.Equal("example-office", pack.Id);
            Assert.Equal("2026-10-11.1", pack.Version);
            Assert.Contains("phasing", pack.TopicNames);
            Assert.Equal(10, pack.Topics.Count);
            Assert.Equal(3, pack.Fingerprint.Phases.Count);
            Assert.Equal("checks.json", pack.ChecksFile);
            Assert.Equal("EX_TITLEBLOCK : A1", pack.Sheets.DefaultTitleblock);
            Assert.Equal(2, pack.LibraryPaths.Count);
            Assert.Contains("EX_", pack.FamilyPrefixes);
            Assert.Equal("EX_W_C_Plan", pack.ViewDefaults["plan"].Template);
            Assert.Equal("01_Work views", pack.ViewDefaults["plan"].Parameters["EX_BrowserFolder"]);
            Assert.False(pack.LinkCad.CorrectOffAxisLines);
            Assert.True(pack.LinkCad.PinAfterLink);
            Assert.Equal(1000, pack.CoordinateRoundingMm);
        }

        [Fact]
        public void ExamplePack_TopicsAndCore_Read()
        {
            var pack = Example();
            Assert.Contains("Table 1", pack.GetTopicText("phasing"));
            Assert.Contains("Table 1", pack.GetTopicText("PHASING")); // case-insensitive
            Assert.Null(pack.GetTopicText("nonexistent"));
            Assert.Contains("core rules", pack.GetCoreText());
        }

        [Fact]
        public void PhaseTable_ResolvesKeysAliasesAndDefault()
        {
            var t = Example().PhaseTable;
            Assert.NotNull(t);
            Assert.Equal("new", t.DefaultKey);
            Assert.Equal("2. New construction", t.Default.Created);
            Assert.Null(t.Default.Demolished);

            var temp = t.Resolve("temporary"); // alias
            Assert.Equal("temp", temp.Key);
            Assert.Equal("2. New construction", temp.Demolished);

            Assert.Equal("1. Existing", t.Resolve("DEMOLISH").Created);
            Assert.Null(t.Resolve("bogus"));
            Assert.Equal("1. Existing", t.ExistingPhase);
            Assert.Equal(new[] { "existing", "new", "temp", "demolish", "future" }, t.Keys.ToArray());
        }

        [Fact]
        public void MissingTopicFile_IsGracefulNull()
        {
            var folder = WritePack("p", "{\"id\":\"p\",\"topics\":{\"ghost\":\"topics/ghost.md\"}}");
            var pack = StandardsPack.TryLoad(folder, out var error);
            Assert.Null(error);
            Assert.True(pack.HasTopic("ghost"));
            Assert.Null(pack.GetTopicText("ghost"));
            Assert.Equal("", pack.GetCoreText()); // core.md missing too
        }

        [Fact]
        public void NoPackJson_ReturnsNullWithReason()
        {
            Assert.Null(StandardsPack.TryLoad(_temp, out var error));
            Assert.Contains("pack.json", error);
        }

        [Fact]
        public void BrokenJson_ReturnsNullWithReason()
        {
            var folder = WritePack("bad", "{ this is not json");
            Assert.Null(StandardsPack.TryLoad(folder, out var error));
            Assert.False(string.IsNullOrEmpty(error));
        }

        [Fact]
        public void MissingId_FallsBackToFolderName()
        {
            var folder = WritePack("my-office", "{}");
            Assert.Equal("my-office", StandardsPack.TryLoad(folder, out _).Id);
        }

        [Fact]
        public void LibraryPaths_FallBackToPathKeys()
        {
            var folder = WritePack("lib", "{\"paths\":{\"library\":\"X:\\\\lib\",\"templateFamilies\":\"X:\\\\fam\"}}");
            var pack = StandardsPack.TryLoad(folder, out _);
            Assert.Equal(new[] { "X:\\lib", "X:\\fam" }, pack.LibraryPaths.ToArray());
        }

        [Fact]
        public void Additions_AreAppendedToCore()
        {
            var folder = WritePack("add", "{\"id\":\"add\"}", new Dictionary<string, string>
            {
                { "core.md", "- rule one" },
                { "aanvullingen.md", "- rule from the working group" }
            });
            var pack = StandardsPack.TryLoad(folder, out _);
            pack.AdditionsPaths = new List<string> { Path.Combine(folder, "aanvullingen.md"), Path.Combine(folder, "absent.md") };
            var core = pack.GetCoreText();
            Assert.Contains("rule one", core);
            Assert.Contains("working group", core);
        }

        [Fact]
        public void GeneratedCore_UsedWhenRequested_FallsBackToCoreMd()
        {
            var folder = WritePack("gen", "{\"id\":\"gen\",\"core\":\"generated\"}", new Dictionary<string, string>
            {
                { "core.md", "- hand written" }
            });
            var pack = StandardsPack.TryLoad(folder, out _);
            pack.GeneratedCorePath = Path.Combine(folder, "core.generated.md");
            Assert.Contains("hand written", pack.GetCoreText());
            File.WriteAllText(pack.GeneratedCorePath, "- distilled");
            Assert.Contains("distilled", pack.GetCoreText());
            Assert.DoesNotContain("hand written", pack.GetCoreText());
        }

        // --- Fingerprint --------------------------------------------------------------------

        [Fact]
        public void Fingerprint_MatchesMeasuredNames()
        {
            var r = FingerprintMatcher.Match(Example().Fingerprint, ExampleFacts());
            Assert.True(r.Matched);
            Assert.Equal(2, r.Score);
            Assert.Equal(2, r.Max);
            Assert.Contains("scope box", r.Reasons);
        }

        [Fact]
        public void Fingerprint_ForeignPhases_DoNotMatch()
        {
            var facts = ExampleFacts();
            facts.Phases = new List<string> { "Existing", "New Construction" };
            var r = FingerprintMatcher.Match(Example().Fingerprint, facts);
            Assert.False(r.Matched);
            Assert.Contains(r.Missing, m => m.Contains("1. Existing"));
        }

        [Fact]
        public void Fingerprint_ScopeBoxes_AreEvidenceOnly_WithAliasSpelling()
        {
            var facts = ExampleFacts();
            facts.ScopeBoxes = new List<string> { "Project_Extent_WCS" }; // declared alias
            Assert.Contains("scope box", FingerprintMatcher.Match(Example().Fingerprint, facts).Reasons);

            facts.ScopeBoxes = new List<string> { "project_extent_wcs" }; // case-insensitive
            Assert.Contains("scope box", FingerprintMatcher.Match(Example().Fingerprint, facts).Reasons);

            // A real project may have deleted the template's scope boxes: still the office's model.
            facts.ScopeBoxes = new List<string>();
            var r = FingerprintMatcher.Match(Example().Fingerprint, facts);
            Assert.True(r.Matched);
            Assert.DoesNotContain("scope box", r.Reasons);
        }

        [Fact]
        public void Fingerprint_RequiresASharedParameter()
        {
            var facts = ExampleFacts();
            facts.ProjectParameters = new List<string> { "Comments" };
            var r = FingerprintMatcher.Match(Example().Fingerprint, facts);
            Assert.False(r.Matched);
            Assert.Equal(1, r.Score);
        }

        [Fact]
        public void Fingerprint_Empty_AlwaysMatches()
        {
            var r = FingerprintMatcher.Match(new PackFingerprint(), new TemplateFacts());
            Assert.True(r.Matched);
            Assert.Equal(0, r.Max);
        }

        [Fact]
        public void PickActive_PrefersMostSpecificMatch()
        {
            var generic = StandardsPack.TryLoad(WritePack("generic", "{\"id\":\"generic\"}"), out _);
            var office = Example();
            var picked = StandardsPackRegistry.PickActive(new[] { generic, office }, ExampleFacts(), out var match);
            Assert.Same(office, picked);
            Assert.Equal(2, match.Max);

            var foreign = new TemplateFacts { Phases = { "Existing", "New Construction" } };
            var rejections = new List<string>();
            picked = StandardsPackRegistry.PickActive(new[] { generic, office }, foreign, out _, rejections);
            Assert.Same(generic, picked);
            Assert.Contains(rejections, r => r.StartsWith("example-office"));
        }

        [Fact]
        public void PickActive_NoMatch_ReturnsNull()
        {
            var picked = StandardsPackRegistry.PickActive(new[] { Example() }, new TemplateFacts(), out var match);
            Assert.Null(picked);
            Assert.Null(match);
        }

        // --- Discovery ----------------------------------------------------------------------

        [Fact]
        public void Discover_FollowsSearchOrder_AndReportsShadowing()
        {
            var setting = Path.Combine(_temp, "setting");
            var local = Path.Combine(_temp, "local");
            var addin = Path.Combine(_temp, "addin");
            WritePack(Path.Combine("setting", "a"), "{\"id\":\"a\",\"version\":\"from-setting\"}");
            WritePack(Path.Combine("local", "a"), "{\"id\":\"a\",\"version\":\"from-local\"}");
            WritePack(Path.Combine("local", "b"), "{\"id\":\"b\"}");
            WritePack(Path.Combine("addin", "c"), "{\"id\":\"c\"}");
            Directory.CreateDirectory(Path.Combine(local, "not-a-pack"));

            var found = StandardsPackRegistry.Discover(StandardsPackRegistry.ResolveSearchRoots(setting, local, addin));
            var packs = found.Where(d => d.Pack != null).Select(d => d.Pack).ToList();

            Assert.Equal(new[] { "a", "b", "c" }, packs.Select(p => p.Id).ToArray());
            Assert.Equal("from-setting", packs[0].Version);
            Assert.Contains(found, d => d.Pack == null && d.Error.Contains("shadowed"));
        }

        [Fact]
        public void Discover_SettingMayPointDirectlyAtAPack()
        {
            var folder = WritePack("direct", "{\"id\":\"direct\"}");
            var found = StandardsPackRegistry.Discover(StandardsPackRegistry.ResolveSearchRoots(folder, null, null));
            Assert.Equal("direct", found.Single().Pack.Id);
        }

        [Fact]
        public void Discover_MissingSettingFolder_IsReported_DefaultsAreSilent()
        {
            var found = StandardsPackRegistry.Discover(StandardsPackRegistry.ResolveSearchRoots(
                Path.Combine(_temp, "nope"), Path.Combine(_temp, "absent-local"), null));
            var d = Assert.Single(found);
            Assert.Contains("not found", d.Error);
            Assert.Equal("setting StandardsPackPath", d.Origin);
        }

        // --- Network share mirror ------------------------------------------------------------

        [Fact]
        public void SyncShare_MirrorsPackFolders_AndRecordsTheSource()
        {
            var share = Path.Combine(_temp, "share");
            WritePack(Path.Combine("share", "office"), "{\"id\":\"office\"}", new Dictionary<string, string>
            {
                { "core.md", "- rule" }, { Path.Combine("topics", "phasing.md"), "# phasing" }
            });
            Directory.CreateDirectory(Path.Combine(share, "not-a-pack"));
            var mirror = Path.Combine(_temp, "local", "_mirror");

            var status = StandardsPackRegistry.SyncShare(share, mirror);
            Assert.StartsWith("synced 1 pack(s), 3 file(s)", status);
            Assert.True(File.Exists(Path.Combine(mirror, "office", "topics", "phasing.md")));
            Assert.False(Directory.Exists(Path.Combine(mirror, "not-a-pack")));
            Assert.Equal(share, File.ReadAllText(Path.Combine(mirror, StandardsPackRegistry.MirrorSourceFile)));
            Assert.False(Directory.Exists(mirror + ".staging"));

            // The mirror is a normal search root afterwards.
            var found = StandardsPackRegistry.Discover(StandardsPackRegistry.ResolveSearchRoots(mirror, null, null));
            Assert.Equal("office", found.Single().Pack.Id);
        }

        [Fact]
        public void SyncShare_UnreachableShare_KeepsThePreviousMirror()
        {
            var share = Path.Combine(_temp, "share");
            WritePack(Path.Combine("share", "office"), "{\"id\":\"office\"}");
            var mirror = Path.Combine(_temp, "_mirror");
            StandardsPackRegistry.SyncShare(share, mirror);

            var status = StandardsPackRegistry.SyncShare(Path.Combine(_temp, "gone"), mirror);
            Assert.StartsWith("share unreachable", status);
            Assert.True(File.Exists(Path.Combine(mirror, "office", "pack.json")));
        }

        [Theory]
        [InlineData(@"\\server\revit\standards", true)]
        [InlineData("//server/revit", true)]
        [InlineData(@"C:\Users\x\standards", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsNetworkPath(string path, bool expected) => Assert.Equal(expected, PathProbe.IsNetworkPath(path));

        [Fact]
        public void ShareRoot_IsTheReachabilityUnit()
        {
            Assert.Equal(@"\\server\share", PathProbe.ShareRoot(@"\\server\share\revit\a.pdf"));
            Assert.Equal(@"K:\", PathProbe.ShareRoot(@"K:\lib\a.pdf"));
        }

        [Fact]
        public void PathProbe_Run_ReturnsFallbackOnTimeoutAndOnError()
        {
            Assert.Equal("slow", PathProbe.Run(() => { System.Threading.Thread.Sleep(2000); return "done"; }, TimeSpan.FromMilliseconds(50), "slow"));
            Assert.Equal("err", PathProbe.Run<string>(() => throw new IOException("x"), TimeSpan.FromSeconds(1), "err"));
            Assert.Equal("ok", PathProbe.Run(() => "ok", TimeSpan.FromSeconds(1), "x"));
        }

        [Fact]
        public void AdditionsFile_ConfigurableAndSafe()
        {
            Assert.Equal("additions.md", StandardsPack.TryLoad(WritePack("a1", "{}"), out _).AdditionsFile);
            Assert.Equal("aanvullingen.md", StandardsPack.TryLoad(WritePack("a2", "{\"additionsFile\":\"aanvullingen.md\"}"), out _).AdditionsFile);
            Assert.Equal("additions.md", StandardsPack.TryLoad(WritePack("a3", "{\"additionsFile\":\"..\\\\x|y.md\"}"), out _).AdditionsFile);
        }

        [Fact]
        public void Fingerprint_ViewsOnly_IsSupportingEvidence_PackAppliesEverywhere()
        {
            var fp = new PackFingerprint { Views = new List<string> { "Some view" } };
            Assert.True(fp.IsEmpty);
            Assert.True(FingerprintMatcher.Match(fp, new TemplateFacts()).Matched);
        }

        [Fact]
        public void SurveyPoint_JudgedOnSharedCoordinates_NotOnDistanceFromInternalOrigin()
        {
            // Georeferenced project: survey point far from the internal origin, but it IS shared 0,0,0.
            var c = new CoordinateFacts { Available = true, SurveyOffsetFromOriginMm = 150000000 };
            Assert.True(c.SurveyAtSharedOrigin);
            Assert.Contains("at shared 0,0,0", StandardsRender.DescribeCoordinates(new TemplateFacts { Coordinates = c }));
            c.SurveyNorthMm = 25;
            Assert.False(c.SurveyAtSharedOrigin);
        }

        // --- Rendering ----------------------------------------------------------------------

        [Fact]
        public void RenderCore_HasHeaderRulesTopicsAndPhaseHint_WithinCap()
        {
            var pack = Example();
            var block = StandardsRender.RenderCore(pack);
            Assert.StartsWith("STANDARDS: office standard 'example-office'", block);
            Assert.Contains("Never leave a model element without a phase", block);
            Assert.Contains("standards <topic>", block);
            Assert.Contains("--phase existing|new|temp|demolish|future", block);
            Assert.True(StandardsRender.Bytes(block) <= pack.CoreMaxBytes);
        }

        [Fact]
        public void RenderCore_RespectsSmallCap()
        {
            var block = StandardsRender.RenderCore(Example(), 600);
            Assert.True(StandardsRender.Bytes(block) <= 600, "got " + StandardsRender.Bytes(block));
            Assert.StartsWith("STANDARDS:", block);
            Assert.Contains("pass --phase", block); // the phase hint survives; the core is cut instead
            Assert.Contains("truncated", block);
        }

        [Fact]
        public void RenderTemplateSummary_StaysUnderOneKilobyte()
        {
            var facts = ExampleFacts();
            facts.IsFull = true;
            for (int i = 0; i < 200; i++)
            {
                facts.ViewTemplates.Add("EX_W_C_Template_number_" + i);
                facts.Families.Add(new FamilyFact { Name = (i % 2 == 0 ? "EX_" : "Other_") + "Family_with_a_long_name_" + i });
                facts.Sheets.Add(i.ToString("0000") + " Sheet");
            }
            facts.Titleblocks.Add("EX_TITLEBLOCK");
            facts.Coordinates = new CoordinateFacts { Available = true, BaseNorthMm = 487000000, BaseEastMm = 123000000, BasePinned = true };
            var av = new ActiveViewFacts { Name = "_WCS_DATUM", Phase = "1. Existing", PhaseFilter = "1.1 Existing" };
            var pack = Example();
            var match = FingerprintMatcher.Match(pack.Fingerprint, facts);

            var block = StandardsRender.RenderTemplateSummary(facts, av, pack, match);
            Assert.True(StandardsRender.Bytes(block) <= StandardsRender.TemplateMaxBytes, "got " + StandardsRender.Bytes(block));
            Assert.StartsWith("TEMPLATE:", block);
            Assert.Contains("example-office", block);
            Assert.Contains("EXISTING phase", block); // active view on the existing phase is flagged
            Assert.Contains("Run `template`", block);
        }

        [Fact]
        public void RenderTemplateSummary_NoPack_SaysGeneric()
        {
            var block = StandardsRender.RenderTemplateSummary(new TemplateFacts { Title = "x" }, null, null, null);
            Assert.Contains("none matched", block);
        }

        [Fact]
        public void TruncateAtLine_CountsUtf8Bytes_AndEndsOnALine()
        {
            var text = string.Join("\n", Enumerable.Range(0, 100).Select(i => "regel " + i + " — één"));
            var cut = StandardsRender.TruncateAtLine(text, 300);
            Assert.True(StandardsRender.Bytes(cut) <= 300);
            Assert.EndsWith("truncated; see the topic sheets]", cut);
            Assert.Equal("short", StandardsRender.TruncateAtLine("short", 300));
        }

        [Fact]
        public void PrefixHelpers_GroupAndClassify()
        {
            Assert.Equal("EX_W_C", StandardsRender.PrefixKey("EX_W_C_Plan"));
            Assert.Equal("EX", StandardsRender.PrefixKey("EX_Schedules"));
            var counts = StandardsRender.ClassifyPrefixes(new[] { "EX_a", "NLRS_b", "NLRS_c", "foo" }, new[] { "EX_", "NLRS_" });
            Assert.Equal(1, counts["EX_"]);
            Assert.Equal(2, counts["NLRS_"]);
            Assert.Equal(1, counts["other"]);
        }

        [Theory]
        [InlineData(487000000, 1000, true)]
        [InlineData(487000000.3, 1000, true)]
        [InlineData(487000123, 1000, false)]
        [InlineData(-123000000, 1000, true)]
        [InlineData(12345, 0, true)]
        public void IsRound(double mm, double step, bool expected)
        {
            Assert.Equal(expected, StandardsRender.IsRound(mm, step));
        }

        [Fact]
        public void StandardsPrompt_CombinesBlocks()
        {
            StandardsPrompt.Publish(null, null);
            Assert.Equal("", StandardsPrompt.Combined);
            StandardsPrompt.Publish("TEMPLATE: t", "STANDARDS: s");
            var c = StandardsPrompt.Combined;
            Assert.True(c.IndexOf("TEMPLATE:") < c.IndexOf("STANDARDS:"));
            StandardsPrompt.Publish(null, null);
        }
    }
}
