using VibeModel.Services.Claude;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>
    /// The raw HTTP query-string transforms in <see cref="RevitHttpServer"/>:
    ///   - args=hello+world  =>  "hello world"  (plus-decoding + percent-unescaping)
    ///   - format=json       =>  JSON response opt-in (case-insensitive, curl's */* stays text)
    /// </summary>
    public class QueryParsingTests
    {
        // ---- ParseQueryArgs -------------------------------------------------

        [Fact]
        public void Args_PlusSignsBecomeSpaces()
        {
            Assert.Equal("hello world", RevitHttpServer.ParseQueryArgs("args=hello+world"));
        }

        [Fact]
        public void Args_PercentEncodingIsUnescaped()
        {
            // "%2C" is a comma — coordinate lists arrive percent-encoded.
            Assert.Equal("0,0 5000,0", RevitHttpServer.ParseQueryArgs("args=0%2C0+5000%2C0"));
        }

        [Fact]
        public void Args_MissingArgsParam_ReturnsEmpty()
        {
            Assert.Equal("", RevitHttpServer.ParseQueryArgs("format=json"));
        }

        [Fact]
        public void Args_EmptyQuery_ReturnsEmpty()
        {
            Assert.Equal("", RevitHttpServer.ParseQueryArgs(""));
        }

        [Fact]
        public void Args_PicksArgsAmongOtherParams()
        {
            Assert.Equal("height", RevitHttpServer.ParseQueryArgs("format=json&args=height"));
        }

        [Fact]
        public void Args_ValueContainingEquals_IsPreserved()
        {
            // Split on first '=' only, so "a=b" survives as the value.
            Assert.Equal("a=b", RevitHttpServer.ParseQueryArgs("args=a=b"));
        }

        // ---- QueryHasFormatJson --------------------------------------------

        [Fact]
        public void FormatJson_IsDetected()
        {
            Assert.True(RevitHttpServer.QueryHasFormatJson("format=json"));
        }

        [Fact]
        public void FormatJson_IsCaseInsensitive()
        {
            Assert.True(RevitHttpServer.QueryHasFormatJson("Format=JSON"));
        }

        [Fact]
        public void FormatJson_DetectedAmongOtherParams()
        {
            Assert.True(RevitHttpServer.QueryHasFormatJson("args=x&format=json"));
        }

        [Fact]
        public void FormatText_IsNotJson()
        {
            Assert.False(RevitHttpServer.QueryHasFormatJson("format=text"));
        }

        [Fact]
        public void NoFormatParam_IsNotJson()
        {
            Assert.False(RevitHttpServer.QueryHasFormatJson("args=height"));
        }

        [Fact]
        public void EmptyQuery_IsNotJson()
        {
            Assert.False(RevitHttpServer.QueryHasFormatJson(""));
        }
    }
}
