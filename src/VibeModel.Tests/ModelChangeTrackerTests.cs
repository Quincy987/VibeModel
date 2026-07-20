using System.Collections.Generic;
using VibeModel.Services.Claude;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>
    /// The model-change tracking contract behind the model-state stamp:
    ///   - VibeModel-prefixed transactions raise only the total counter;
    ///   - anything else (foreign names, undo/redo) also counts as a user edit;
    ///   - taking a stamp reports the counters and resets the user-edit counter;
    ///   - stamp rendering: trailing text line and additive JSON meta object.
    /// Tracker state is static, so every test uses its own doc key and the
    /// constructor wipes state to keep tests order-independent.
    /// </summary>
    public class ModelChangeTrackerTests
    {
        public ModelChangeTrackerTests()
        {
            ModelChangeTracker.ResetAll();
        }

        private static readonly string[] Ours = { "VibeModel: Create Wall" };
        private static readonly string[] Foreign = { "Move" };

        // ---- Transaction classification ------------------------------------

        [Fact]
        public void VibeModelPrefixedNames_AreOurs()
        {
            Assert.True(ModelChangeTracker.IsVibeModelTransaction(
                new[] { "VibeModel: Create Wall", "VibeModel: Set Parameter" }));
        }

        [Fact]
        public void ForeignName_IsUserEdit()
        {
            Assert.False(ModelChangeTracker.IsVibeModelTransaction(new[] { "Move" }));
        }

        [Fact]
        public void MixedNames_CountAsUserEdit()
        {
            Assert.False(ModelChangeTracker.IsVibeModelTransaction(
                new[] { "VibeModel: Create Wall", "Manual Edit" }));
        }

        [Fact]
        public void EmptyOrNullNames_CountAsUserEdit()
        {
            Assert.False(ModelChangeTracker.IsVibeModelTransaction(new string[0]));
            Assert.False(ModelChangeTracker.IsVibeModelTransaction(null));
            Assert.False(ModelChangeTracker.IsVibeModelTransaction(new string[] { null }));
        }

        [Fact]
        public void PrefixMatch_IsCaseSensitive()
        {
            Assert.False(ModelChangeTracker.IsVibeModelTransaction(new[] { "vibemodel: create wall" }));
        }

        // ---- Counter behavior ----------------------------------------------

        [Fact]
        public void OurCommit_RaisesTotalButNotUserCount()
        {
            ModelChangeTracker.RecordCommit("doc-ours", Ours);

            var stamp = ModelChangeTracker.TakeStamp("doc-ours", "Level 1", 0);
            Assert.Equal(1, stamp.TotalEdits);
            Assert.Equal(0, stamp.UserEdits);
        }

        [Fact]
        public void ForeignCommit_RaisesBothCounters()
        {
            ModelChangeTracker.RecordCommit("doc-foreign", Foreign);

            var stamp = ModelChangeTracker.TakeStamp("doc-foreign", "Level 1", 0);
            Assert.Equal(1, stamp.TotalEdits);
            Assert.Equal(1, stamp.UserEdits);
        }

        [Fact]
        public void UndoRedo_CountsAsUserEdit()
        {
            ModelChangeTracker.RecordUndoRedo("doc-undo");

            var stamp = ModelChangeTracker.TakeStamp("doc-undo", "Level 1", 0);
            Assert.Equal(1, stamp.TotalEdits);
            Assert.Equal(1, stamp.UserEdits);
        }

        [Fact]
        public void TakeStamp_ResetsUserCountButNotTotal()
        {
            ModelChangeTracker.RecordCommit("doc-reset", Foreign);
            ModelChangeTracker.RecordCommit("doc-reset", Foreign);

            var first = ModelChangeTracker.TakeStamp("doc-reset", "Level 1", 0);
            Assert.Equal(2, first.TotalEdits);
            Assert.Equal(2, first.UserEdits);

            var second = ModelChangeTracker.TakeStamp("doc-reset", "Level 1", 0);
            Assert.Equal(2, second.TotalEdits);
            Assert.Equal(0, second.UserEdits); // reset means "the client has been told"
        }

        [Fact]
        public void Documents_AreTrackedIndependently()
        {
            ModelChangeTracker.RecordCommit("doc-a", Foreign);

            Assert.Equal(1, ModelChangeTracker.TakeStamp("doc-a", null, 0).UserEdits);
            Assert.Equal(0, ModelChangeTracker.TakeStamp("doc-b", null, 0).TotalEdits);
        }

        // ---- Doc key -------------------------------------------------------

        [Fact]
        public void DocKey_PrefersPathName_FallsBackToTitle()
        {
            Assert.Equal(@"C:\proj\house.rvt", ModelChangeTracker.DocKey(@"C:\proj\house.rvt", "house"));
            Assert.Equal("untitled:Project1", ModelChangeTracker.DocKey("", "Project1"));
            Assert.Equal("untitled:Project1", ModelChangeTracker.DocKey(null, "Project1"));
        }

        // ---- Stamp rendering: text -----------------------------------------

        [Fact]
        public void TextLine_NoUserEdits_OmitsWarning()
        {
            var stamp = new ModelStamp(47, 0, "{3D} Main", 0);
            Assert.Equal("-- model #47 | view: {3D} Main | selected: 0", stamp.ToTextLine());
        }

        [Fact]
        public void TextLine_OneUserEdit_SingularWarning()
        {
            var stamp = new ModelStamp(48, 1, "Level 1", 2);
            Assert.Equal(
                "-- model #48 (1 USER edit since last command - model changed outside VibeModel)" +
                " | view: Level 1 | selected: 2",
                stamp.ToTextLine());
        }

        [Fact]
        public void TextLine_ManyUserEdits_PluralWarning()
        {
            var stamp = new ModelStamp(50, 3, "Level 1", 0);
            Assert.Equal(
                "-- model #50 (3 USER edits since last command - model changed outside VibeModel)" +
                " | view: Level 1 | selected: 0",
                stamp.ToTextLine());
        }

        [Fact]
        public void TextLine_UnknownView_RendersPlaceholder()
        {
            var stamp = new ModelStamp(1, 0, null, 0);
            Assert.Equal("-- model #1 | view: unknown | selected: 0", stamp.ToTextLine());
        }

        [Fact]
        public void AppendToText_AddsExactlyOneSeparatorNewline()
        {
            var stamp = new ModelStamp(5, 0, "Level 1", 0);

            // Body without a trailing newline gains one before the stamp.
            Assert.Equal("WALL CREATED\n" + stamp.ToTextLine(), stamp.AppendToText("WALL CREATED"));
            // Body already ending in a newline is preserved verbatim.
            Assert.Equal("WALL CREATED\n" + stamp.ToTextLine(), stamp.AppendToText("WALL CREATED\n"));
        }

        [Fact]
        public void AppendToText_EmptyBody_IsJustTheStamp()
        {
            var stamp = new ModelStamp(5, 0, "Level 1", 0);
            Assert.Equal(stamp.ToTextLine(), stamp.AppendToText(""));
        }

        // ---- Stamp rendering: JSON meta ------------------------------------

        [Fact]
        public void Meta_CarriesAllFourFields()
        {
            var meta = new ModelStamp(47, 3, "{3D} Main", 2).ToMeta();

            Assert.Equal(47L, meta["edits"]);
            Assert.Equal(3L, meta["userEditsSinceLastCommand"]);
            Assert.Equal("{3D} Main", meta["activeView"]);
            Assert.Equal(2, meta["selectedCount"]);
        }

        [Fact]
        public void Meta_NullView_StaysNull()
        {
            var meta = new ModelStamp(0, 0, null, 0).ToMeta();
            Assert.Null(meta["activeView"]);
        }

        // ---- End-to-end counting scenario ----------------------------------

        [Fact]
        public void Scenario_MixedActivity_CountsAndResetsCorrectly()
        {
            const string doc = "doc-scenario";

            // Our wall, then the user moves something and hits undo once.
            ModelChangeTracker.RecordCommit(doc, new[] { "VibeModel: Create Wall" });
            ModelChangeTracker.RecordCommit(doc, new[] { "Move" });
            ModelChangeTracker.RecordUndoRedo(doc);

            var stamp = ModelChangeTracker.TakeStamp(doc, "Level 1", 1);
            Assert.Equal(3, stamp.TotalEdits);
            Assert.Equal(2, stamp.UserEdits);

            // Next command: nothing happened in between.
            var next = ModelChangeTracker.TakeStamp(doc, "Level 1", 1);
            Assert.Equal(3, next.TotalEdits);
            Assert.Equal(0, next.UserEdits);
        }
    }
}
