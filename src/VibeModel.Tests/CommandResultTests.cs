using System.Collections.Generic;
using System.Web.Script.Serialization;
using VibeModel.Services.Claude;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>
    /// The JSON/text envelope contract:
    ///   success  => {"ok":true,"data":...}  or  {"ok":true,"text":"..."}
    ///   failure  => {"ok":false,"error":{code,message,suggestion}}
    /// and the byte-identical text rendering the add-in has always produced.
    /// </summary>
    public class CommandResultTests
    {
        private static readonly JavaScriptSerializer Json =
            new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        private static Dictionary<string, object> Deserialize(string s) =>
            Json.Deserialize<Dictionary<string, object>>(s);

        // ---- Text rendering -------------------------------------------------

        [Fact]
        public void Ok_TextMode_ReturnsTheHumanText()
        {
            var r = CommandResult.Ok("WALL CREATED");
            Assert.Equal("WALL CREATED", r.Render(ResponseFormat.Text, Json));
        }

        [Fact]
        public void Error_TextMode_PrefixesErrorAndAppendsHint()
        {
            var r = CommandResult.Error("BAD_ARGS", "Invalid coordinates", "Use numbers in mm.");
            Assert.Equal("ERROR: Invalid coordinates\nHint: Use numbers in mm.",
                r.Render(ResponseFormat.Text, Json));
        }

        [Fact]
        public void Error_TextMode_NoHintWhenSuggestionMissing()
        {
            var r = CommandResult.Error("BAD_ARGS", "Invalid coordinates");
            Assert.Equal("ERROR: Invalid coordinates", r.Render(ResponseFormat.Text, Json));
        }

        [Fact]
        public void Legacy_SuccessText_RenderedVerbatim()
        {
            var r = CommandResult.Legacy("some raw output\nline 2");
            Assert.True(r.Success);
            Assert.Equal("some raw output\nline 2", r.Render(ResponseFormat.Text, Json));
        }

        [Fact]
        public void Legacy_ErrorPrefix_MarksFailureButKeepsTextVerbatim()
        {
            var r = CommandResult.Legacy("ERROR: No basic wall type found");
            Assert.False(r.Success);
            // Text stays byte-identical to the pre-envelope output.
            Assert.Equal("ERROR: No basic wall type found", r.Render(ResponseFormat.Text, Json));
        }

        [Fact]
        public void RenderText_WorksWithoutASerializer()
        {
            var r = CommandResult.Error("X", "boom", "try again");
            Assert.Equal("ERROR: boom\nHint: try again", r.RenderText());
        }

        // ---- JSON rendering -------------------------------------------------

        [Fact]
        public void Ok_WithData_JsonHasOkTrueAndDataAndNullText()
        {
            var data = new Dictionary<string, object> { { "id", 12345 } };
            var r = CommandResult.Ok("ignored when data present", data);

            var obj = Deserialize(r.Render(ResponseFormat.Json, Json));
            Assert.Equal(true, obj["ok"]);
            Assert.NotNull(obj["data"]);
            Assert.True(obj.ContainsKey("text"));
            Assert.Null(obj["text"]);        // text is suppressed when structured data exists
        }

        [Fact]
        public void Ok_WithoutData_JsonCarriesTextInsteadOfData()
        {
            var r = CommandResult.Ok("hello world");
            var obj = Deserialize(r.Render(ResponseFormat.Json, Json));

            Assert.Equal(true, obj["ok"]);
            Assert.Null(obj["data"]);
            Assert.Equal("hello world", obj["text"]);
        }

        [Fact]
        public void Legacy_Success_JsonCarriesRawTextAsText()
        {
            var r = CommandResult.Legacy("legacy body");
            var obj = Deserialize(r.Render(ResponseFormat.Json, Json));

            Assert.Equal(true, obj["ok"]);
            Assert.Equal("legacy body", obj["text"]);
        }

        [Fact]
        public void Error_JsonHasOkFalseAndFullErrorObject()
        {
            var r = CommandResult.Error("NO_DOCUMENT", "No document open",
                "Open a Revit project before running commands.");
            var obj = Deserialize(r.Render(ResponseFormat.Json, Json));

            Assert.Equal(false, obj["ok"]);
            var err = (Dictionary<string, object>)obj["error"];
            Assert.Equal("NO_DOCUMENT", err["code"]);
            Assert.Equal("No document open", err["message"]);
            Assert.Equal("Open a Revit project before running commands.", err["suggestion"]);
            Assert.False(obj.ContainsKey("data"));   // failure envelope has no data key
        }

        // ---- Transaction-error adaptation ----------------------------------

        [Fact]
        public void FromTransactionError_StripsErrorPrefix()
        {
            var r = CommandResult.FromTransactionError("ERROR: Transaction failed to commit");
            Assert.False(r.Success);

            var obj = Deserialize(r.Render(ResponseFormat.Json, Json));
            var err = (Dictionary<string, object>)obj["error"];
            Assert.Equal("TRANSACTION_FAILED", err["code"]);
            Assert.Equal("Transaction failed to commit", err["message"]);   // "ERROR: " stripped
        }

        [Fact]
        public void FromTransactionError_HonorsCustomCodeAndSuggestion()
        {
            var r = CommandResult.FromTransactionError("ERROR: locked", "DOC_LOCKED", "close the dialog");
            var obj = Deserialize(r.Render(ResponseFormat.Json, Json));
            var err = (Dictionary<string, object>)obj["error"];
            Assert.Equal("DOC_LOCKED", err["code"]);
            Assert.Equal("locked", err["message"]);
            Assert.Equal("close the dialog", err["suggestion"]);
        }
    }
}
