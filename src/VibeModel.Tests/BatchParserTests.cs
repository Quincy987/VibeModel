using System.Linq;
using VibeModel.Services.Claude;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>
    /// The POST /batch parsing rules: line splitting, blank-line skipping,
    /// '#atomic' directive, '?atomic=1' query, '#'-comment skipping, and the
    /// command/args split on the first space.
    /// </summary>
    public class BatchParserTests
    {
        [Fact]
        public void NullBody_IsRejectedAsEmpty()
        {
            var r = BatchParser.Parse(null, "");
            Assert.False(r.IsValid);
            Assert.Equal("ERROR: Empty batch body", r.Error);
        }

        [Fact]
        public void EmptyBody_IsRejectedAsEmpty()
        {
            var r = BatchParser.Parse("", "");
            Assert.False(r.IsValid);
            Assert.Equal("ERROR: Empty batch body", r.Error);
        }

        [Fact]
        public void WhitespaceOnlyBody_HasNoCommands()
        {
            var r = BatchParser.Parse("   \n\n  \r\n", "");
            Assert.False(r.IsValid);
            Assert.Equal("ERROR: No commands in batch", r.Error);
        }

        [Fact]
        public void OnlyCommentLines_HasNoCommands()
        {
            var r = BatchParser.Parse("# just a comment\n# another", "");
            Assert.False(r.IsValid);
            Assert.Equal("ERROR: No commands in batch", r.Error);
        }

        [Fact]
        public void SingleCommand_NoArgs_ParsesWithEmptyArgs()
        {
            var r = BatchParser.Parse("selected", "");
            Assert.True(r.IsValid);
            var c = Assert.Single(r.Commands);
            Assert.Equal("selected", c.Command);
            Assert.Equal("", c.Args);
        }

        [Fact]
        public void CommandAndArgs_SplitOnFirstSpaceOnly()
        {
            var r = BatchParser.Parse("wall 0 0 5000 0 3000", "");
            var c = Assert.Single(r.Commands);
            Assert.Equal("wall", c.Command);
            Assert.Equal("0 0 5000 0 3000", c.Args);   // everything after the first space, intact
        }

        [Fact]
        public void MultipleLines_EachBecomeACommand_BlankLinesSkipped()
        {
            var body = "wall 0 0 5000 0\n\nwall 5000 0 5000 5000\n   \nfloor 0,0 5000,0";
            var r = BatchParser.Parse(body, "");

            Assert.True(r.IsValid);
            Assert.Equal(3, r.Commands.Count);
            Assert.Equal(new[] { "wall", "wall", "floor" }, r.Commands.Select(c => c.Command));
        }

        [Fact]
        public void CrlfAndBareCr_AreBothTreatedAsLineBreaks()
        {
            // \r, \n and \r\n are all delimiters; empty entries removed.
            var r = BatchParser.Parse("wall 0 0 1 1\r\nwall 1 1 2 2\rwall 2 2 3 3", "");
            Assert.Equal(3, r.Commands.Count);
        }

        [Fact]
        public void ExtraSpacesBetweenCommandAndArgs_AreCollapsed()
        {
            var r = BatchParser.Parse("wall    0 0 1 1", "");
            var c = Assert.Single(r.Commands);
            Assert.Equal("wall", c.Command);
            Assert.Equal("0 0 1 1", c.Args);
        }

        [Fact]
        public void Default_IsNotAtomic()
        {
            var r = BatchParser.Parse("wall 0 0 1 1", "");
            Assert.False(r.Atomic);
        }

        [Fact]
        public void AtomicQueryParam_EnablesAtomic()
        {
            var r = BatchParser.Parse("wall 0 0 1 1", "atomic=1");
            Assert.True(r.Atomic);
        }

        [Fact]
        public void AtomicQueryParam_AmongOtherParams_StillEnablesAtomic()
        {
            var r = BatchParser.Parse("wall 0 0 1 1", "format=json&atomic=1");
            Assert.True(r.Atomic);
        }

        [Fact]
        public void LeadingAtomicDirective_EnablesAtomic_AndIsNotACommand()
        {
            var r = BatchParser.Parse("#atomic\nwall 0 0 1 1\nwall 1 1 2 2", "");
            Assert.True(r.Atomic);
            Assert.Equal(2, r.Commands.Count);   // #atomic line is consumed, not routed
        }

        [Fact]
        public void AtomicDirective_IsCaseInsensitive()
        {
            var r = BatchParser.Parse("#ATOMIC\nwall 0 0 1 1", "");
            Assert.True(r.Atomic);
        }

        [Fact]
        public void PlainCommentLines_AreSkippedWithoutEnablingAtomic()
        {
            var r = BatchParser.Parse("# make two walls\nwall 0 0 1 1\n# second\nwall 1 1 2 2", "");
            Assert.False(r.Atomic);
            Assert.Equal(2, r.Commands.Count);
        }

        [Fact]
        public void UnrelatedQuery_DoesNotEnableAtomic()
        {
            var r = BatchParser.Parse("wall 0 0 1 1", "format=json");
            Assert.False(r.Atomic);
        }
    }
}
