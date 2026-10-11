using VibeModel.Services.Claude;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>`view` office flags: --template, --no-template, --name (multi-word, unquoted).</summary>
    public class ViewArgsTests
    {
        [Fact]
        public void NoFlags_Unchanged()
        {
            var r = ViewArgs.Parse("plan Level 2");
            Assert.Equal("plan Level 2", r.Remaining);
            Assert.Null(r.Template);
            Assert.Null(r.Name);
            Assert.False(r.NoTemplate);
        }

        [Fact]
        public void Null_IsEmpty() => Assert.Equal("", ViewArgs.Parse(null).Remaining);

        [Theory]
        [InlineData("plan N.A.P. --name Werkview bouwfase N.A.P.", "plan N.A.P.", "Werkview bouwfase N.A.P.", null, false)]
        [InlineData("plan N.A.P. --name \"Werk view\" --template EX_W_C_Plan", "plan N.A.P.", "Werk view", "EX_W_C_Plan", false)]
        [InlineData("3d --template EX_W_C_3D Sunny --name My 3D", "3d", "My 3D", "EX_W_C_3D Sunny", false)]
        [InlineData("plan Level 1 --no-template --name Plan A", "plan Level 1", "Plan A", null, true)]
        [InlineData("--no-template 3d", "3d", null, null, true)]
        [InlineData("plan --name=Test", "plan", "Test", null, false)]
        [InlineData("plan N.A.P. --activate --name Werkview", "plan N.A.P.", "Werkview", null, false)]
        public void Flags_AreExtracted(string args, string remaining, string name, string template, bool noTemplate)
        {
            var r = ViewArgs.Parse(args);
            Assert.Equal(remaining, r.Remaining);
            Assert.Equal(name, r.Name);
            Assert.Equal(template, r.Template);
            Assert.Equal(noTemplate, r.NoTemplate);
        }

        [Fact]
        public void Activate_IsParsed()
        {
            Assert.True(ViewArgs.Parse("3d --activate").Activate);
            Assert.Equal("3d", ViewArgs.Parse("3d --activate").Remaining);
            Assert.False(ViewArgs.Parse("3d --name activate me").Activate);
        }

        [Fact]
        public void NoTemplate_IsNotConfusedWithAName()
        {
            var r = ViewArgs.Parse("plan --name no-template-view");
            Assert.Equal("no-template-view", r.Name);
            Assert.False(r.NoTemplate);
        }
    }
}
