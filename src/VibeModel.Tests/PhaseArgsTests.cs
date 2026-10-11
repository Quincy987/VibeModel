using VibeModel.Services.Claude;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>--phase parsing for creating commands: absent flag = untouched args.</summary>
    public class PhaseArgsTests
    {
        [Fact]
        public void NoFlag_ReturnsSameArgs()
        {
            const string args = "0 0 5000 0 3000";
            var r = PhaseArgs.Parse(args);
            Assert.Same(args, r.Remaining);
            Assert.False(r.Explicit);
            Assert.Null(r.Error);
        }

        [Fact]
        public void NullAndEmpty_AreSafe()
        {
            Assert.Null(PhaseArgs.Parse(null).Remaining);
            Assert.Equal("", PhaseArgs.Parse("").Remaining);
        }

        [Theory]
        [InlineData("0 0 5000 0 --phase temp", "0 0 5000 0", "temp")]
        [InlineData("--phase new 0,0 5000,0 5000,5000", "0,0 5000,0 5000,5000", "new")]
        [InlineData("Door Single --phase=existing 100 200", "Door Single 100 200", "existing")]
        [InlineData("0 0 5000 0 --PHASE Demolish", "0 0 5000 0", "Demolish")]
        [InlineData("2500 2500 Office  101 --phase future", "2500 2500 Office  101", "future")]
        public void Flag_IsStripped(string args, string remaining, string key)
        {
            var r = PhaseArgs.Parse(args);
            Assert.Null(r.Error);
            Assert.Equal(remaining, r.Remaining);
            Assert.Equal(key, r.Key);
            Assert.True(r.Explicit);
        }

        [Fact]
        public void MissingValue_IsAnError()
        {
            Assert.NotNull(PhaseArgs.Parse("0 0 5000 0 --phase").Error);
            Assert.NotNull(PhaseArgs.Parse("0 0 --phase --other").Error);
        }

        [Fact]
        public void Twice_IsAnError()
        {
            Assert.NotNull(PhaseArgs.Parse("--phase new 0 0 --phase temp").Error);
        }

        [Fact]
        public void PhaseInsideAnotherWord_IsNotAFlag()
        {
            const string args = "Door my--phase-door 0 0";
            var r = PhaseArgs.Parse(args);
            Assert.Same(args, r.Remaining);
            Assert.False(r.Explicit);
        }

        [Fact]
        public void KeyList_JoinsWithPipes()
        {
            Assert.Equal("a|b", PhaseArgs.KeyList(new[] { "a", "b" }));
        }
    }
}
