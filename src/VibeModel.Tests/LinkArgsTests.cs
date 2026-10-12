using System.IO;
using VibeModel.Services.Claude.Commands;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>Flag parsing and the file probe shared by linkcad and linkrvt.</summary>
    public class LinkArgsTests
    {
        [Fact]
        public void Flags_AreTakenAndThePathRemains()
        {
            var s = "\"C:\\proj\\K model.rvt\" --origin --no-pin";
            Assert.True(LinkArgs.TakeFlag(ref s, "--origin"));
            Assert.True(LinkArgs.TakeFlag(ref s, "--no-pin"));
            Assert.False(LinkArgs.TakeFlag(ref s, "--force"));
            Assert.Equal("\"C:\\proj\\K model.rvt\"", s);
        }

        [Fact]
        public void Value_QuotedOrBare()
        {
            var s = "\"a.dgn\" --dgnview \"Model 2\"";
            Assert.Equal("Model 2", LinkArgs.TakeValue(ref s, "--dgnview"));
            Assert.Equal("\"a.dgn\"", s);
            var t = "a.dgn --dgnview=Default";
            Assert.Equal("Default", LinkArgs.TakeValue(ref t, "--dgnview"));
            Assert.Null(LinkArgs.TakeValue(ref t, "--dgnview"));
        }

        [Fact]
        public void SharedPlacementWhy_NamesTheCriterion()
        {
            var rd = new VibeModel.Services.Standards.CoordinateFacts { BaseNorthMm = 487000000, BaseEastMm = 123000000.4, SiteName = "Internal" };
            Assert.Equal("Auto – By Shared Coordinates (coordinates set up: base point N/S 487000000, E/W 123000000 mm ≠ 0)",
                LinkArgs.SharedPlacementWhy(rd));
            var atZero = new VibeModel.Services.Standards.CoordinateFacts { SiteName = "RD" };
            Assert.Equal("Auto – By Shared Coordinates (coordinates set up: shared coordinates acquired (site 'RD'))",
                LinkArgs.SharedPlacementWhy(atZero));
        }

        [Fact]
        public void ProbeFile_MissingIsFileNotFound_ExistingIsNull()
        {
            var missing = Path.Combine(Path.GetTempPath(), "vibemodel-nope-" + System.Guid.NewGuid() + ".rvt");
            var r = LinkArgs.ProbeFile(missing);
            Assert.NotNull(r);
            Assert.Equal("FILE_NOT_FOUND", r.ErrorCode);

            var real = Path.GetTempFileName();
            try { Assert.Null(LinkArgs.ProbeFile(real)); }
            finally { File.Delete(real); }
        }
    }
}
