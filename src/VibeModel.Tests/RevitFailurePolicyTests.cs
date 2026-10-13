using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;
using VibeModel.Services.Claude;
using VibeModel.Services.Claude.Commands;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>
    /// What VibeModel does with a Revit failure during one of its commands (so Revit never shows
    /// its modal failure dialog) and how that is reported back to the caller.
    /// </summary>
    public class RevitFailurePolicyTests
    {
        private static readonly JavaScriptSerializer Json =
            new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        private static FailingElementInfo Spot(long id) => new FailingElementInfo
            { Id = id, Category = "Spot Elevations", BuiltInCategory = -2000263, ViewSpecific = true };

        private static FailingElementInfo Wall(long id) => new FailingElementInfo
            { Id = id, Category = "Walls", BuiltInCategory = -2000011, ViewSpecific = false };

        private static FailureInfo Error(bool canDelete, params FailingElementInfo[] elements) => new FailureInfo
        {
            Severity = RevitFailureSeverity.Error,
            Description = "One or more Spot Dimension references are no longer valid.",
            FailingElements = elements,
            HasDeleteElementsResolution = canDelete
        };

        // ---- Decide ---------------------------------------------------------

        [Fact]
        public void Warning_IsDismissed()
        {
            var f = new FailureInfo { Severity = RevitFailureSeverity.Warning, Description = "Highlighted walls overlap.",
                FailingElements = new[] { Wall(1), Wall(2) } };
            Assert.Equal(RevitFailureAction.DeleteWarning, RevitFailurePolicy.Decide(f, 1));
        }

        [Fact]
        public void Error_OnlySpotDimensions_WithDeleteResolution_DeletesThem()
        {
            Assert.Equal(RevitFailureAction.ResolveByDeletingAnnotations,
                RevitFailurePolicy.Decide(Error(true, Spot(777), Spot(778)), 1));
        }

        [Fact]
        public void Error_WithAModelElementAmongTheFailing_RollsBack()
        {
            Assert.Equal(RevitFailureAction.RollBack, RevitFailurePolicy.Decide(Error(true, Spot(777), Wall(5)), 1));
        }

        [Fact]
        public void Error_WithoutDeleteResolution_RollsBack()
        {
            Assert.Equal(RevitFailureAction.RollBack, RevitFailurePolicy.Decide(Error(false, Spot(777)), 1));
        }

        [Fact]
        public void Error_WithoutFailingElements_RollsBack()
        {
            Assert.Equal(RevitFailureAction.RollBack, RevitFailurePolicy.Decide(Error(true), 1));
        }

        [Fact]
        public void DocumentCorruption_RollsBack()
        {
            var f = Error(true, Spot(777));
            f.Severity = RevitFailureSeverity.DocumentCorruption;
            Assert.Equal(RevitFailureAction.RollBack, RevitFailurePolicy.Decide(f, 1));
        }

        [Fact]
        public void LoopingPasses_RollBackEvenAWarning()
        {
            var f = new FailureInfo { Severity = RevitFailureSeverity.Warning, Description = "w" };
            Assert.Equal(RevitFailureAction.DeleteWarning, RevitFailurePolicy.Decide(f, RevitFailurePolicy.MaxPasses));
            Assert.Equal(RevitFailureAction.RollBack, RevitFailurePolicy.Decide(f, RevitFailurePolicy.MaxPasses + 1));
        }

        [Fact]
        public void NullFailure_RollsBack()
        {
            Assert.Equal(RevitFailureAction.RollBack, RevitFailurePolicy.Decide(null, 1));
        }

        // ---- IsDeletableAnnotation (the whitelist) --------------------------

        [Theory]
        [InlineData(-2000260)] // dimensions
        [InlineData(-2000263)] // spot elevations
        [InlineData(-2000264)] // spot coordinates
        [InlineData(-2000265)] // spot slopes
        [InlineData(-2000970)] // multi-reference annotations
        public void HostedAnnotationCategories_AreDeletable(int bic)
        {
            Assert.True(RevitFailurePolicy.IsDeletableAnnotation(
                new FailingElementInfo { Id = 1, BuiltInCategory = bic, ViewSpecific = true }));
        }

        [Fact]
        public void Tags_AreDeletable_ByFlagOrName()
        {
            Assert.True(RevitFailurePolicy.IsDeletableAnnotation(
                new FailingElementInfo { Id = 1, Category = "Whatever", BuiltInCategory = -1, ViewSpecific = true, IsTag = true }));
            Assert.True(RevitFailurePolicy.IsDeletableAnnotation(
                new FailingElementInfo { Id = 1, Category = "Structural Foundation Tags", BuiltInCategory = -1, ViewSpecific = true }));
        }

        [Fact]
        public void TextNote_IsNotDeletable()
        {
            Assert.False(RevitFailurePolicy.IsDeletableAnnotation(
                new FailingElementInfo { Id = 1, Category = "Text Notes", BuiltInCategory = -2000300, ViewSpecific = true }));
        }

        [Fact]
        public void DimensionCategory_ThatIsNotViewSpecific_IsNotDeletable()
        {
            Assert.False(RevitFailurePolicy.IsDeletableAnnotation(
                new FailingElementInfo { Id = 1, Category = "Dimensions", BuiltInCategory = -2000260, ViewSpecific = false }));
        }

        [Fact]
        public void ModelElement_AndUnknownElement_AreNotDeletable()
        {
            Assert.False(RevitFailurePolicy.IsDeletableAnnotation(Wall(5)));
            Assert.False(RevitFailurePolicy.IsDeletableAnnotation(new FailingElementInfo { Id = 9 }));
            Assert.False(RevitFailurePolicy.IsDeletableAnnotation(null));
        }

        // ---- FailureReport --------------------------------------------------

        [Fact]
        public void EmptyReport_HasNoLines()
        {
            var r = new FailureReport();
            Assert.True(r.IsEmpty);
            Assert.False(r.RolledBack);
            Assert.Null(r.RollbackReason);
            Assert.Empty(r.ToTextLines());
        }

        [Fact]
        public void IdenticalWarnings_AreCountedOnOneLine()
        {
            var r = new FailureReport();
            r.AddWarning("Highlighted walls overlap.");
            r.AddWarning("Highlighted walls overlap.");
            r.AddWarning("Other warning");
            Assert.Equal(new[]
            {
                "-- revit warning: Highlighted walls overlap. (2×)",
                "-- revit warning: Other warning"
            }, r.ToTextLines().ToArray());
        }

        [Fact]
        public void MultiLineDescription_StaysOneLine()
        {
            var r = new FailureReport();
            r.AddWarning("Line one\r\n  line two");
            Assert.Equal("-- revit warning: Line one line two", r.ToTextLines().Single());
        }

        [Fact]
        public void ResolvedLine_NamesIdsAndCategory()
        {
            var r = new FailureReport();
            r.AddResolved("One or more Spot Dimension references are no longer valid.", new[] { Spot(777) });
            Assert.Equal("-- revit resolved: One or more Spot Dimension references are no longer valid. → deleted 1 annotation: 777 (Spot Elevations)",
                r.ToTextLines().Single());
            Assert.Equal(new long[] { 777 }, r.DeletedIds.ToArray());
        }

        [Fact]
        public void ResolvedLine_ListsTenIdsThenMore()
        {
            var r = new FailureReport();
            r.AddResolved("x", Enumerable.Range(1, 13).Select(i => Spot(i)));
            var line = r.ToTextLines().Single();
            Assert.Contains("13 annotations: 1 (Spot Elevations)", line);
            Assert.Contains("10 (Spot Elevations) +3 more", line);
            Assert.DoesNotContain("11 (", line);
        }

        [Fact]
        public void ResolvedTwice_SameMessage_DoesNotDuplicateIds()
        {
            var r = new FailureReport();
            r.AddResolved("x", new[] { Spot(1) });
            r.AddResolved("x", new[] { Spot(1), Spot(2) });
            Assert.Single(r.Resolved);
            Assert.Equal(new long[] { 1, 2 }, r.DeletedIds.ToArray());
        }

        [Fact]
        public void Rollback_KeepsFirstReasonAndAllDistinctOnes()
        {
            var r = new FailureReport();
            r.AddRollback("Can't keep elements joined.");
            r.AddRollback("Can't keep elements joined.");
            r.AddRollback("Second");
            Assert.True(r.RolledBack);
            Assert.Equal("Can't keep elements joined.", r.RollbackReason);
            Assert.Equal(2, r.RollbackReasons.Count);
            Assert.Equal("-- revit rolled back: Second", r.ToTextLines().Last());
        }

        [Fact]
        public void Merge_AddsCountsIdsAndReasons()
        {
            var a = new FailureReport();
            a.AddWarning("w");
            var b = new FailureReport();
            b.AddWarning("w");
            b.AddResolved("x", new[] { Spot(5) });
            b.AddRollback("r");
            a.Merge(b);
            Assert.Equal(2, a.Warnings.Single().Count);
            Assert.Equal(new long[] { 5 }, a.DeletedIds.ToArray());
            Assert.Equal("r", a.RollbackReason);
        }

        [Fact]
        public void ToJson_HasWarningsResolvedAndRolledBack()
        {
            var r = new FailureReport();
            r.AddWarning("Highlighted walls overlap.");
            r.AddResolved("x", new[] { Spot(777) });
            var d = Json.Deserialize<Dictionary<string, object>>(Json.Serialize(r.ToJson()));

            var warnings = (System.Collections.ArrayList)d["warnings"];
            var w = (Dictionary<string, object>)warnings[0];
            Assert.Equal("Highlighted walls overlap.", w["message"]);
            Assert.Equal(1, w["count"]);

            var resolved = (Dictionary<string, object>)((System.Collections.ArrayList)d["resolved"])[0];
            var deleted = (Dictionary<string, object>)((System.Collections.ArrayList)resolved["deleted"])[0];
            Assert.Equal(777, deleted["id"]);
            Assert.Equal("Spot Elevations", deleted["category"]);

            Assert.True(d.ContainsKey("rolledBack"));
            Assert.Null(d["rolledBack"]);
        }

        [Fact]
        public void RollbackErrorMessage_FallsBackWithoutReason()
        {
            Assert.Equal("Revit rolled the change back: X", FailureReport.RollbackErrorMessage("X"));
            Assert.Equal("Revit rolled the change back: Revit refused the change", FailureReport.RollbackErrorMessage(null));
            Assert.True(FailureReport.IsRollbackError("ERROR: Revit rolled the change back: X"));
            Assert.True(FailureReport.IsRollbackError("Revit rolled the change back: X"));
            Assert.False(FailureReport.IsRollbackError("ERROR: Wall too short"));
            Assert.False(FailureReport.IsRollbackError(null));
        }

        // ---- Rollbacks become structured errors -----------------------------

        [Fact]
        public void FromTransactionError_Rollback_GetsCodeAndHint()
        {
            var r = CommandResult.FromTransactionError("ERROR: " + FailureReport.RollbackErrorMessage("Can't keep elements joined."));
            Assert.False(r.Success);
            Assert.Equal(FailureReport.RollbackErrorCode, r.ErrorCode);
            Assert.Equal("ERROR: Revit rolled the change back: Can't keep elements joined.\nHint: " + FailureReport.RollbackHint,
                r.RenderText());
        }

        [Fact]
        public void FromTransactionError_Rollback_ReplacesCallersExceptionHint()
        {
            // e.g. TextCommand's "A text note must lie in the view's plane" would mislead here.
            var r = CommandResult.FromTransactionError("ERROR: Revit rolled the change back: x", "REVIT_ERROR", "Mine.");
            Assert.Equal(FailureReport.RollbackErrorCode, r.ErrorCode);
            Assert.Equal(FailureReport.RollbackHint, r.Suggestion);
        }

        [Fact]
        public void Legacy_RawHelperRollback_GetsTheHintLine()
        {
            // Legacy commands (wall, floor, place, delete, set, color) return the helper's string as is.
            var r = CommandResult.Legacy("ERROR: Revit rolled the change back: Can't keep elements joined.");
            Assert.False(r.Success);
            Assert.Equal("ERROR: Revit rolled the change back: Can't keep elements joined.\nHint: " + FailureReport.RollbackHint,
                r.RenderText());
            Assert.Equal("Revit rolled the change back: Can't keep elements joined.", r.Message);
            Assert.Equal(FailureReport.RollbackHint, r.Suggestion);
        }

        // ---- Loop guard -----------------------------------------------------

        private static FailureInfo SpotError(long id) => Error(true, Spot(id));

        [Fact]
        public void LoopGuard_FreshFailures_StartAtPassOne()
        {
            var g = new FailureLoopGuard();
            Assert.Equal(1, g.NextPass(new[] { SpotError(1) }));
        }

        [Fact]
        public void LoopGuard_SameFailureBackAfterResolving_CountsUpToRollback()
        {
            var g = new FailureLoopGuard();
            int pass = 0;
            for (int i = 0; i <= RevitFailurePolicy.MaxPasses; i++)
            {
                pass = g.NextPass(new[] { SpotError(1) });
                if (RevitFailurePolicy.Decide(SpotError(1), pass) == RevitFailureAction.RollBack) break;
                g.Resolved(pass, new[] { SpotError(1) });
            }
            Assert.Equal(RevitFailurePolicy.MaxPasses + 1, pass);
        }

        [Fact]
        public void LoopGuard_ManyTransactionsResolvingDifferentFailures_NeverLoop()
        {
            // An exec snippet with 20 transactions that each resolve one spot of its own.
            var g = new FailureLoopGuard();
            for (long id = 1; id <= 20; id++)
            {
                int pass = g.NextPass(new[] { SpotError(id) });
                Assert.Equal(1, pass);
                g.Resolved(pass, new[] { SpotError(id) });
            }
        }

        [Fact]
        public void LoopGuard_Reset_StartsOver()
        {
            var g = new FailureLoopGuard();
            g.Resolved(5, new[] { SpotError(1) });
            Assert.Equal(6, g.NextPass(new[] { SpotError(1) }));
            g.Reset();
            Assert.Equal(1, g.NextPass(new[] { SpotError(1) }));
        }

        [Fact]
        public void FromTransactionError_PlainError_IsUnchanged()
        {
            var r = CommandResult.FromTransactionError("ERROR: Wall too short");
            Assert.Equal("TRANSACTION_FAILED", r.ErrorCode);
            Assert.Null(r.Suggestion);
            Assert.Equal("ERROR: Wall too short", r.RenderText());
        }

        [Fact]
        public void Legacy_RollbackText_IsVerbatimWithStructuredFields()
        {
            var raw = ExecCommand.RollbackError("Highlighted walls overlap.", "created 1 wall");
            var r = CommandResult.Legacy(raw);
            Assert.False(r.Success);
            Assert.Equal(raw, r.RenderText());
            Assert.Equal(FailureReport.RollbackErrorCode, r.ErrorCode);
            Assert.Equal("Revit rolled the change back: Highlighted walls overlap.", r.Message);
            Assert.Equal(ExecCommand.RollbackHint, r.Suggestion);
            Assert.StartsWith("ERROR: Revit rolled the change back: Highlighted walls overlap.\nHint: ", raw);
            Assert.EndsWith("\n\ncreated 1 wall", raw);
        }

        [Fact]
        public void Legacy_PlainError_IsUnchanged()
        {
            var r = CommandResult.Legacy("ERROR: Runtime exception: boom");
            Assert.Equal("INTERNAL", r.ErrorCode);
            Assert.Equal("Runtime exception: boom", r.Message);
            Assert.Null(r.Suggestion);
        }
    }
}
