using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using VibeModel.Services.Chat;
using VibeModel.Services.Claude;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>
    /// Phase 2, damage control (docs/plans/22-damage-control.md): exec is one undo step, the undo
    /// command's ledger never takes back the user's work, one overview line per action names what
    /// went (and why) and what changed on sheets, templates and filters, a region edge in the same
    /// place brings its dimension back, a trial is a preview of the real script, and the stored
    /// chat keeps what VibeModel answered.
    /// </summary>
    [Collection("UndoLedger")]
    public class DamageControlTests : IDisposable
    {
        // The edit tracker is shared with other test classes running in parallel: own doc keys, no reset.
        public DamageControlTests() => UndoLedger.ResetForTests();

        public void Dispose() => UndoLedger.ResetForTests();

        // ---------------------------------------------------------------- 1. exec in a group

        [Fact]
        public void Exec_RunsInAGroupUnderAuto_UnlessItSavesOrCloses()
        {
            Assert.True(ChangeApprovalPolicy.NeedsRunGroup(ChangeApprovalMode.Auto, true, true, false, false, "exec", "Level.Create(doc, 1);"));
            Assert.True(ChangeApprovalPolicy.NeedsRunGroup(ChangeApprovalMode.Auto, true, true, false, false, "EXEC", "x"));
            Assert.False(ChangeApprovalPolicy.NeedsRunGroup(ChangeApprovalMode.Auto, true, true, false, false, "exec", "trans.Commit(); doc.Save();"));
            Assert.False(ChangeApprovalPolicy.NeedsRunGroup(ChangeApprovalMode.Auto, true, true, false, false, "exec", "doc.SynchronizeWithCentral(o, s);"));
            // Other commands under auto: exactly as before (their own transaction is one undo step).
            Assert.False(ChangeApprovalPolicy.NeedsRunGroup(ChangeApprovalMode.Auto, true, true, false, false, "delete", "123"));
            Assert.False(ChangeApprovalPolicy.NeedsRunGroup(ChangeApprovalMode.Auto, false, true, false, false, "wall", "0 0 1 0"));
            // A command that changes nothing never needs one.
            Assert.False(ChangeApprovalPolicy.NeedsRunGroup(ChangeApprovalMode.Auto, true, false, false, false, "exec", "x"));
            // Under auto the group is simply kept: Decide says Keep, no prompt.
            Assert.Equal(RunDecision.Keep, ChangeApprovalPolicy.Decide(ChangeApprovalMode.Auto, true, false, false, changesEmpty: false));
            Assert.Equal(RunDecision.Nothing, ChangeApprovalPolicy.Decide(ChangeApprovalMode.Auto, true, false, false, changesEmpty: true));
        }

        // ---------------------------------------------------------------- 2. exec source checks

        [Theory]
        [InlineData("uiApp.PostCommand(RevitCommandId.LookupPostableCommandId(PostableCommand.Undo));", true)]
        [InlineData("var id = RevitCommandId.LookupPostableCommandId(PostableCommand . Redo);", true)]
        [InlineData("// PostableCommand.Undo would be wrong here\nsb.AppendLine(\"x\");", false)]
        [InlineData("sb.AppendLine(\"PostableCommand.Undo\");", false)]
        [InlineData("var id = RevitCommandId.LookupPostableCommandId(PostableCommand.UndoLast);", false)]
        public void PostsUndo_IsReadFromTheCodeOnly(string code, bool expected)
            => Assert.Equal(expected, ExecSourceChecks.PostsUndo(code));

        [Theory]
        [InlineData("bool CREATE = false;\nif (CREATE) doc.Delete(ids);", "CREATE = false")]
        [InlineData("const bool DryRun = true;", "DryRun = true")]
        [InlineData("var apply = false;", "apply = false")]
        [InlineData("bool CREATE = true;", null)]
        [InlineData("bool dryRun = false;", null)]
        [InlineData("// bool CREATE = false;\nsb.AppendLine(\"x\");", null)]
        [InlineData("bool found = false;", null)]
        [InlineData("bool run = false;", null)]
        public void DryRunSwitch_IsNamed(string code, string expected)
            => Assert.Equal(expected, ExecSourceChecks.DryRunSwitch(code));

        // ---------------------------------------------------------------- 3. the undo ledger

        private const string Doc = @"C:\p\model.rvt";
        private static readonly DateTime Now = new DateTime(2026, 11, 27, 10, 0, 0, DateTimeKind.Utc);

        private static UndoLedger.Run Kept(string name, long before, long after, bool undoable = true)
        {
            UndoLedger.BeginRun();
            UndoLedger.NoteCommit(new[] { "VibeModel: Exec" });
            return UndoLedger.Record(Doc, "model", name, "exec … — added 1 (Levels 1)", before, after, Now, "t1", undoable, "saves");
        }

        [Fact]
        public void Undo_TakesBackTheLastRun_OnlyWhileNothingElseHappened()
        {
            Kept("VibeModel: exec a", 10, 12);
            var ok = UndoLedger.Decide(Doc, "model", 12, Now);
            Assert.True(ok.Ok);
            Assert.Equal("VibeModel: exec a", ok.Run.UndoName);

            // The user (or anything else) edited since: refused, never their work.
            var refused = UndoLedger.Decide(Doc, "model", 13, Now);
            Assert.False(refused.Ok);
            Assert.Contains("1 edit since", refused.Reason);
            Assert.Contains("Never undo the user's own work", refused.Hint);
        }

        [Fact]
        public void Undo_RefusesWithoutARun_InAnotherDocument_AndForSeveralSteps()
        {
            Assert.Contains("no change in this document", UndoLedger.Decide(Doc, "model", 0, Now).Reason);
            UndoLedger.Record(@"C:\p\other.rvt", "other", "VibeModel: wall", "wall", 0, 1, Now, null, true);
            Assert.Contains("another document ('other')", UndoLedger.Decide(Doc, "model", 0, Now).Reason);
            Kept("VibeModel: Exec", 1, 4, undoable: false);
            Assert.Contains("saves", UndoLedger.Decide(Doc, "model", 4, Now).Reason);
        }

        [Fact]
        public void PostedUndo_IsConfirmedByDocumentChanged_AndTheRunBelowBecomesTheTop()
        {
            Kept("VibeModel: exec a", 10, 12);
            var b = Kept("VibeModel: exec b", 12, 14);
            UndoLedger.MarkPosted(b, 14, Now);
            Assert.True(UndoLedger.IsPending(Doc));
            // A second undo while the first is on its way: refused.
            Assert.Contains("has not happened yet", UndoLedger.Decide(Doc, "model", 14, Now).Reason);

            UndoLedger.ObserveUndoRedo(Doc, new[] { "VibeModel: exec b" }, 15, redo: false);
            Assert.False(UndoLedger.IsPending(Doc));
            Assert.Equal("undone: VibeModel: exec b", UndoLedger.TakeReport(Doc, Now));
            Assert.Null(UndoLedger.TakeReport(Doc, Now));                       // shown once

            // The model now stands where 'a' left it, plus the undo: 'a' can be taken back next.
            var next = UndoLedger.Decide(Doc, "model", 15, Now);
            Assert.True(next.Ok);
            Assert.Equal("VibeModel: exec a", next.Run.UndoName);
        }

        [Fact]
        public void PostedUndo_ThatUndidSomethingElse_OrNeverRan_IsSaidSo()
        {
            var a = Kept("VibeModel: exec a", 10, 12);
            UndoLedger.MarkPosted(a, 12, Now);
            // Something committed between the post and Revit running it (counter 13), then the undo (14): it took that back.
            UndoLedger.ObserveUndoRedo(Doc, new[] { "Move" }, 14, redo: false);
            var report = UndoLedger.TakeReport(Doc, Now);
            Assert.StartsWith("the posted undo took back 'Move', not 'VibeModel: exec a'", report);
            Assert.Single(UndoLedger.List(Doc));                                  // still recorded

            UndoLedger.MarkPosted(a, 14, Now);
            UndoLedger.ObserveUndoRedo(Doc, new[] { "VibeModel: exec a" }, 15, redo: true);
            Assert.Contains("did not happen (Revit redid", UndoLedger.TakeReport(Doc, Now));

            UndoLedger.MarkPosted(a, 15, Now);
            Assert.Null(UndoLedger.TakeReport(Doc, Now.AddSeconds(2)));           // still on its way
            Assert.Contains("did not happen", UndoLedger.TakeReport(Doc, Now + UndoLedger.PendingTimeout + TimeSpan.FromSeconds(1)));
            Assert.False(UndoLedger.IsPending(Doc));
        }

        [Fact]
        public void TheUsersOwnUndoOfTheLastRun_DropsIt_AndTheRunBelowCanBeTakenBack()
        {
            Kept("VibeModel: exec a", 10, 12);
            Kept("VibeModel: 2× level", 12, 14);
            // Measured: Revit's Undo button took back VibeModel's batch (one entry, its group name).
            UndoLedger.ObserveUndoRedo(Doc, new[] { "VibeModel: 2× level" }, 15, redo: false);
            Assert.Null(UndoLedger.TakeReport(Doc, Now));        // not ours to report
            var next = UndoLedger.Decide(Doc, "model", 15, Now);
            Assert.True(next.Ok);
            Assert.Equal("VibeModel: exec a", next.Run.UndoName);
            // An undo of something else (the user's own move) changes nothing in the ledger: refused as before.
            UndoLedger.ObserveUndoRedo(Doc, new[] { "Move" }, 16, redo: false);
            Assert.False(UndoLedger.Decide(Doc, "model", 16, Now).Ok);
        }

        [Fact]
        public void TheUsersUndoOfSeveralStepsAtOnce_LeavesTheRunBelowRefused()
        {
            Kept("VibeModel: exec a", 10, 12);
            Kept("VibeModel: exec b", 12, 14);
            // One undo event that also took back a step that was not run b's: run a may be gone too.
            UndoLedger.ObserveUndoRedo(Doc, new[] { "VibeModel: exec b", "Move" }, 15, redo: false);
            Assert.Single(UndoLedger.List(Doc));
            Assert.False(UndoLedger.Decide(Doc, "model", 15, Now).Ok);
        }

        [Fact]
        public void OurOwnUndo_IsNoUserEdit_ButStillMovesTheCounter()
        {
            var key = "untitled:phase2-" + Guid.NewGuid().ToString("N");
            ModelChangeTracker.RecordUndoRedo(key, postedByVibeModel: true);
            var stamp = ModelChangeTracker.TakeStamp(key, "L1", 0, null, "undone: VibeModel: exec a");
            Assert.Equal(1, stamp.TotalEdits);
            Assert.Equal(0, stamp.UserEdits);
            Assert.Equal("-- model #1 | view: L1 | selected: 0 | undone: VibeModel: exec a", stamp.ToTextLine());
            Assert.Equal("undone: VibeModel: exec a", stamp.ToMeta()["undone"]);
            ModelChangeTracker.RecordUndoRedo(key);
            Assert.Equal(1, ModelChangeTracker.TakeStamp(key, "L1", 0).UserEdits);
        }

        [Fact]
        public void Summary_IsOneShortLine_WithTheChangeLine()
        {
            var s = UndoLedger.SummaryOf("exec", "// Doel: maak een peil\nLevel.Create(doc, 1);", "-- changed: added 1 (Levels 1), modified 0, deleted 0");
            Assert.Equal("exec // Doel: maak een peil Level.Create(doc, 1); — added 1 (Levels 1), modified 0, deleted 0", s);
            Assert.True(UndoLedger.SummaryOf("exec", new string('x', 300), null).Length <= 80);
        }

        [Fact]
        public void UndoCommand_IsNoModification_AndHasAStepLabelInBothLanguages()
        {
            var cmd = new Services.Claude.Commands.UndoCommand();
            Assert.False(cmd is IModificationCommand);
            Assert.True(cmd is IJournaledOutcome);
            var posted = CommandResult.Ok("x", new Dictionary<string, object> { { "posted", true }, { "undoName", "VibeModel: exec a" } });
            Assert.Equal("took back 'VibeModel: exec a'", cmd.JournalOutcome(posted));
            Assert.Null(cmd.JournalOutcome(CommandResult.Ok("x", new Dictionary<string, object> { { "posted", false }, { "undoName", "a" } })));
        }

        // ---------------------------------------------------------------- 4. one overview per action

        private static AttentionReport.Snapshot Snap()
        {
            var s = new AttentionReport.Snapshot();
            s.Sheets[1] = new AttentionReport.SheetFact { Number = "0001", Name = "Situatie" };
            s.Views[10] = new AttentionReport.ViewFact { Name = "Werk", IsTemplate = true };
            s.Views[11] = new AttentionReport.ViewFact { Name = "L1", TemplateId = 10 };
            s.Views[12] = new AttentionReport.ViewFact { Name = "L2", TemplateId = 10 };
            s.Views[13] = new AttentionReport.ViewFact { Name = "Doorsnede", FilterIds = new List<long> { 50 } };
            s.Filters[50] = "Damwand";
            s.Filters[51] = "Palen";
            return s;
        }

        [Fact]
        public void Attention_NamesSheetTemplateAndFilterChanges_InOneLine()
        {
            var before = Snap();
            var after = Snap();
            after.Sheets[1].Name = "Situatie nieuw";
            after.Views[11].TemplateId = 0;
            after.Views[12].TemplateId = 0;
            after.Views[13].FilterIds = new List<long> { 50, 51 };
            var items = AttentionReport.Compare(before, after, new long[] { 50 });
            var line = AttentionReport.Line(items);
            Assert.Equal("-- attention: sheet 0001 renamed 'Situatie' → 'Situatie nieuw'; view template 'Werk' removed from 2 views ('L1', 'L2'); " +
                         "filter 'Palen' added to 1 view ('Doorsnede'); filter 'Damwand' changed", line);
            var json = AttentionReport.Json(items);
            Assert.Equal(4, json.Count);
            Assert.Equal("sheet", ((Dictionary<string, object>)json[0])["kind"]);
            Assert.StartsWith("-- preview: attention: ", AttentionReport.Line(items, preview: true));
        }

        [Fact]
        public void Attention_SaysNothing_WhenNothingChanged_AndNamesWhatItCouldNotRead()
        {
            Assert.Empty(AttentionReport.Compare(Snap(), Snap(), new long[0]));
            Assert.Null(AttentionReport.Line(new List<AttentionReport.Item>()));
            Assert.Equal("-- attention: 2 changed views not checked for template/filter changes (more than 3000 views in the model)",
                AttentionReport.Line(new List<AttentionReport.Item>(), notRead: 2));
            var deleted = Snap();
            deleted.Sheets.Clear();
            Assert.Contains("sheet 0001 'Situatie' deleted", AttentionReport.Line(AttentionReport.Compare(Snap(), deleted, null)));
        }

        [Fact]
        public void Attention_ReadsAgainOnlyWhenTheRunTouchedASheetViewOrFilter()
        {
            var s = Snap();
            s.UnreadViews.Add(900);
            Assert.False(s.Touches(new long[] { 4711, 4712 }));        // a wall and its join: nothing to compare
            Assert.True(s.Touches(new long[] { 4711, 1 }));            // the sheet
            Assert.True(s.Touches(new long[] { 50 }));                 // a filter's rules
            Assert.True(s.Touches(new long[] { 900 }));                // a view beyond the cap
            Assert.Equal(1, s.UnreadTouched(new long[] { 900, 900, 11 }));
        }

        [Fact]
        public void RemovedAnnotations_SayWhy_StillOneLine()
        {
            var o = new AnnotationOutcome();
            for (int i = 0; i < 256; i++)
                o.Removed.Add(new RemovedAnnotation { Id = 1000 + i, Kind = AnnotationKind.Dimension, Referenced = new List<long> { 50 + i % 10 }, View = "Doorsnede " + (i % 10), Reason = RemovalReason.NothingInPlace });
            o.Removed.Add(new RemovedAnnotation { Id = 5000, Kind = AnnotationKind.Dimension, Referenced = new List<long> { 60 }, View = "Doorsnede 1", Reason = RemovalReason.CouldNotAttach });
            var line = o.ToTextLine();
            Assert.StartsWith("-- annotations: removed 256 dimensions that referenced ", line);
            Assert.Contains("view Doorsnede 9) — reference gone, nothing came back in its place, 1 dimension that referenced 60", line);
            Assert.EndsWith("1 dimension that referenced 60 (view Doorsnede 1) — could not be re-attached to the replacement", line);
            Assert.DoesNotContain("\n", line);
            var json = new JavaScriptSerializer().Serialize(o.ToJson());
            Assert.Contains("\"reason\":\"nothingInPlace\"", json);
            Assert.Contains("\"reason\":\"couldNotAttach\"", json);
        }

        // ---------------------------------------------------------------- 5. a region edge in the same place

        [Fact]
        public void SameEdge_OnlyOnTheOldReferenceLine_AndOverlapping()
        {
            var dim = new V3(1, 0, 0);                         // a horizontal dimension
            var a = new V3(1000, 0, 0); var b = new V3(1000, 2000, 0);   // the old vertical edge at x = 1000
            Assert.Equal(0, AnnotationMatch.SameEdge(a, b, new V3(1000, 0, 0), new V3(1000, 2000, 0), dim), 6);
            Assert.True(AnnotationMatch.SameEdge(a, b, new V3(1000.5, 500, 0), new V3(1000.5, 3000, 0), dim) > 0);   // shorter/longer, same line
            Assert.True(AnnotationMatch.SameEdge(a, b, new V3(1000, 2040, 0), new V3(1000, 4000, 0), dim) > 0);      // 40 mm gap: still the same place
            Assert.True(AnnotationMatch.SameEdge(a, b, new V3(1002, 0, 0), new V3(1002, 2000, 0), dim) < 0);         // 2 mm off
            Assert.True(AnnotationMatch.SameEdge(a, b, new V3(1000, 3000, 0), new V3(1000, 5000, 0), dim) < 0);      // 1 m further along: elsewhere
            Assert.True(AnnotationMatch.SameEdge(a, b, new V3(0, 0, 0), new V3(2000, 0, 0), dim) < 0);               // runs the other way
        }

        // ---------------------------------------------------------------- 6. what VibeModel answered, stored

        [Fact]
        public void StatusLines_KeepTheHeadAndEveryStatusLine()
        {
            var text = "ERROR: exec failed: boom\nHint: fix it\n\npartial out\nERROR in exec: boom\n-- annotations: removed 2 dimensions that referenced 7 — reference gone, nothing came back in its place\n" +
                       "-- changed: added 0, modified 0, deleted 12 (Detail Items 10, Dimensions 2)\n-- attention: sheet 0001 renamed 'A' → 'B'\n-- model #3 | view: L1 | selected: 0";
            var status = ToolCallDescriber.StatusLines(text);
            Assert.StartsWith("ERROR: exec failed: boom\nHint: fix it\nERROR in exec: boom\n-- annotations: removed 2 dimensions", status);
            Assert.Contains("-- attention: sheet 0001 renamed", status);
            Assert.EndsWith("-- model #3 | view: L1 | selected: 0", status);
            Assert.DoesNotContain("partial out", status);
            Assert.Null(ToolCallDescriber.StatusLines("12 walls"));
            Assert.Contains("[atomic] rolled back", ToolCallDescriber.StatusLines(">>> wall 0 0 1 0\nok\n\n[atomic] rolled back — a command failed.\n-- model #4 | view: L1 | selected: 0"));
        }

        [Fact]
        public void StatusLines_FromAJsonEnvelope_AndCapped()
        {
            var json = "{\"ok\":false,\"error\":{\"code\":\"UNDO_REFUSED\",\"message\":\"Nothing was undone: x\",\"suggestion\":\"ask\"},\"meta\":{\"edits\":3,\"undone\":\"undone: VibeModel: exec a\"}}";
            var status = ToolCallDescriber.StatusLines(json);
            Assert.StartsWith("ERROR: Nothing was undone: x\nHint: ask\n-- meta: ", status);
            Assert.Contains("undone: VibeModel: exec a", status);
            var big = string.Join("\n", Enumerable.Range(0, 200).Select(i => "-- changed: added " + i));
            Assert.Contains("… (+", ToolCallDescriber.StatusLines(big));
        }

        [Fact]
        public void Store_And_Export_KeepTheStatus()
        {
            var dir = Path.Combine(Path.GetTempPath(), "VibeModelPhase2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var t0 = new DateTime(2026, 11, 27, 9, 0, 0, DateTimeKind.Utc);
                var s = new ChatSession { Id = "20261127-090000-abcdef12", Backend = "claude-cli", CreatedUtc = t0, UpdatedUtc = t0 };
                s.Messages.Add(new ChatSessionMessage { Role = ChatSession.RoleUser, Content = "vervang de arceringen", TimestampUtc = t0 });
                s.Messages.Add(new ChatSessionMessage
                {
                    Role = ChatSession.RoleAssistant, Content = "Klaar.", TimestampUtc = t0.AddSeconds(5),
                    Steps = new List<ChatSessionStep>
                    {
                        new ChatSessionStep { Command = "exec", Args = "x", Ok = true, Status = "-- annotations: removed 256 dimensions\n-- changed: added 2" },
                        new ChatSessionStep { Command = "info", Args = "", Ok = true }
                    }
                });
                var store = new ChatSessionStore(dir);
                store.SaveSession(s);
                var loaded = store.LoadSession(s.Id).Messages[1].Steps;
                Assert.Equal("-- annotations: removed 256 dimensions\n-- changed: added 2", loaded[0].Status);
                Assert.Null(loaded[1].Status);
                Assert.DoesNotContain("\"status\"", File.ReadAllText(Path.Combine(dir, s.Id + ".json")).Replace("\"status\":\"-- annotations", "X"));

                var nl = ChatExportDocument.Render(store.LoadSession(s.Id), null, Strings.Dutch, out _);
                Assert.Contains("Wat VibeModel antwoordde bij stap 1 (exec):", nl);
                Assert.Contains("-- annotations: removed 256 dimensions", nl);
                Assert.DoesNotContain("stap 2 (info)", nl);
                Assert.Contains("What VibeModel answered at step 1 (exec):", ChatExportDocument.Render(store.LoadSession(s.Id), null, Strings.English, out _));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void TurnActivity_CarriesTheStatusToTheStoredStep()
        {
            var turn = new TurnActivity(DateTime.UtcNow);
            turn.Apply(ChatActivityEvent.Started("t1", new ToolCallInfo("exec", "x", "x")), DateTime.UtcNow);
            turn.Apply(ChatActivityEvent.Finished("t1", null, "-- changed: added 1"), DateTime.UtcNow);
            Assert.Equal("-- changed: added 1", ChatSessionStep.FromTurn(turn)[0].Status);
        }

        // ---------------------------------------------------------------- the rules

        [Fact]
        public void Rules_PassOnSideEffects_TrialIsAPreview_FailuresAreCounted()
        {
            var rules = ChatBackendBase.BuildBehaviourRules("nl");
            Assert.Contains("- " + ChatBackendBase.SideEffectsRule, rules);
            Assert.Contains("- " + ChatBackendBase.DryRunRule, rules);
            Assert.Contains("- " + ChatBackendBase.ExecFailuresRule, rules);
            Assert.Contains("offer `undo`", ChatBackendBase.SideEffectsRule);
            Assert.Contains("`?preview=1`", ChatBackendBase.DryRunRule);
            Assert.Contains(ChatBackendBase.UndoCommandRule, ChatBackendBase.BuildBehaviourRules("nl", ChangeApprovalMode.Annotate));
        }
    }
}
