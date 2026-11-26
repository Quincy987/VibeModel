using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;
using Microsoft.CSharp;
using VibeModel.Services.Chat;
using VibeModel.Services.Claude;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>
    /// Phase 1: exec's compile and run-time errors are failed commands (they read as succeeded
    /// before), and its compile errors say which C# and which Revit year exec speaks.
    /// </summary>
    public class ExecOutcomeTests
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        private const string CompileText =
            "COMPILATION ERRORS:\n\n  Line 21: Ongeldige expressieterm .\n  Line 21: Syntaxisfout, : wordt verwacht\n\n" +
            "Your code was wrapped in a method with: doc, uiDoc, uiApp, sb (StringBuilder)\nUse sb.AppendLine(...) to produce output.\n\n" +
            "Hint: exec compiles C# 5, Revit 2023 API:\n  - no ?. / ?[] — write `x != null ? x.Y : null` or an if";

        [Fact]
        public void CompileFailure_IsAnError_TextStartsWithError_KeepsCompilerOutput()
        {
            var cr = ExecOutcome.FromText(CompileText);
            Assert.False(cr.Success);
            Assert.Equal(ExecOutcome.CompileCode, cr.ErrorCode);
            var text = cr.RenderText();
            Assert.StartsWith("ERROR: exec did not compile, nothing ran (2 errors).", text);
            Assert.Contains("COMPILATION ERRORS:", text);
            Assert.Contains("Line 21: Ongeldige expressieterm .", text);
            Assert.Contains("Hint: exec compiles C# 5", cr.Suggestion);
            Assert.Contains("Line 21: Ongeldige expressieterm .", cr.Message);
        }

        [Fact]
        public void CompileFailure_Json_OkFalseWithCode()
        {
            var obj = (Dictionary<string, object>)Json.DeserializeObject(ExecOutcome.FromText(CompileText).Render(ResponseFormat.Json, Json));
            Assert.Equal(false, obj["ok"]);
            Assert.Equal(ExecOutcome.CompileCode, ((Dictionary<string, object>)obj["error"])["code"]);
        }

        [Fact]
        public void RuntimeError_AfterOutput_IsAnError_OutputKept()
        {
            var text = "wall 1 done\nwall 2 done\nERROR in exec: Object reference not set to an instance of an object.\n";
            var cr = ExecOutcome.FromText(text);
            Assert.False(cr.Success);
            Assert.Equal(ExecOutcome.RuntimeCode, cr.ErrorCode);
            var rendered = cr.RenderText();
            Assert.StartsWith("ERROR: exec failed: Object reference not set to an instance of an object.", rendered);
            Assert.Contains("\nHint: ", rendered);
            Assert.Contains("wall 1 done\nwall 2 done", rendered);
            Assert.Equal("exec failed: Object reference not set to an instance of an object.", cr.Message);
        }

        [Fact]
        public void RuntimeError_WithRunGroupHintAfterIt_StillFound()
        {
            var text = "ERROR in exec: Operation is not permitted\nHint: " + ChangeApprovalPolicy.InRunGroupHint;
            var cr = ExecOutcome.FromText(text, ChangeApprovalPolicy.InRunGroupHint);
            Assert.Equal(ExecOutcome.RuntimeCode, cr.ErrorCode);
            Assert.Contains(ChangeApprovalPolicy.InRunGroupHint, cr.Suggestion);
        }

        [Fact]
        public void OutputThatMentionsAnErrorEarlier_IsStillOutput()
        {
            Assert.True(ExecOutcome.FromText("read: ERROR in exec: old line from a log\nwall 1 done\n").Success);
            Assert.True(ExecOutcome.FromText("12 walls").Success);
            Assert.True(ExecOutcome.FromText("(exec completed, no output — use sb.AppendLine(...) to produce output)").Success);
        }

        [Fact]
        public void CompileErrors_StopAtExecsOwnNote_NotAtHintLines()
        {
            Assert.Equal(new[] { "Line 21: Ongeldige expressieterm .", "Line 21: Syntaxisfout, : wordt verwacht" },
                ExecOutcome.CompileErrors(CompileText).ToArray());
            Assert.Null(ExecOutcome.CompileErrors("COMPILATION ERRORS:\n\nYour code was wrapped in a method"));
            Assert.Null(ExecOutcome.CompileErrors("12 walls"));
        }

        [Fact]
        public void ExistingErrorTexts_KeepTheirMeaning()
        {
            var rollback = ExecOutcome.FromText("ERROR: Revit rolled the change back: Can't keep elements joined.\nHint: x");
            Assert.False(rollback.Success);
            Assert.Equal(FailureReport.RollbackErrorCode, rollback.ErrorCode);
            var usage = ExecOutcome.FromText("ERROR: Usage: exec <csharp_code>");
            Assert.False(usage.Success);
            Assert.Equal("INTERNAL", usage.ErrorCode);
            var runtime = ExecOutcome.FromText("ERROR: Runtime exception: boom");
            Assert.Equal(ExecOutcome.RuntimeCode, runtime.ErrorCode);
            Assert.Equal("ERROR: Runtime exception: boom", runtime.RenderText());
        }

        // ---------------------------------------------------------------- the pane's step check

        [Theory]
        [InlineData("COMPILATION ERRORS:\n\n  Line 5: x\n", "exec did not compile: Line 5: x")]
        [InlineData("half output\nERROR in exec: boom\n-- model #3 | view: L1 | selected: 0", "exec failed: boom")]
        [InlineData(">>> exec x\nERROR in exec: boom\n\n>>> wall 0 0 1 0\nok", null)]
        [InlineData("12 walls\n-- model #3 | view: L1 | selected: 0", null)]
        // An answer or log that merely quotes the markers in the middle is no failed step.
        [InlineData("chat 3: ERROR in exec: boom\nERROR in exec: quoted\nlater line\n-- model #3 | view: L1 | selected: 0", null)]
        [InlineData("log says COMPILATION ERRORS: none here\nall fine", null)]
        public void Pane_FlagsExecErrorsAnywhere_NotInATextBatch(string result, string expected)
        {
            Assert.Equal(expected, ToolCallDescriber.ErrorLine(result));
        }

        // ---------------------------------------------------------------- C# 5 and Revit-year hints

        // Compiled with the same compiler exec uses (CodeDom, C# 5): the error numbers and columns
        // are this machine's, whatever language its messages are in.
        private static List<ApiLookup.CompileError> Compile(string body)
        {
            var source = "using System; using System.Text; public static class S { public static string Run(object o, string s) {\n" +
                         body + "\n return null; } }";
            var lines = source.Split('\n');
            using (var provider = new CSharpCodeProvider())
            {
                var r = provider.CompileAssemblyFromSource(new CompilerParameters { GenerateInMemory = true }, source);
                return r.Errors.Cast<CompilerError>().Where(e => !e.IsWarning).Select(e => new ApiLookup.CompileError
                {
                    Number = e.ErrorNumber, Message = e.ErrorText, Column = e.Column,
                    Line = e.Line >= 1 && e.Line <= lines.Length ? lines[e.Line - 1] : null
                }).ToList();
            }
        }

        [Theory]
        [InlineData("var n = s?.Length;", "?.")]
        [InlineData("if (o is string t) { }", "is T x")]
        [InlineData("var t = $\"n={s}\";", "$\"")]
        [InlineData("int.TryParse(s, out var n);", "out var")]
        [InlineData("var t = nameof(s);", "nameof")]
        public void CSharp6Constructs_AreNamed(string body, string construct)
        {
            var errors = Compile(body);
            Assert.NotEmpty(errors);
            var advice = ExecDialect.CSharp5Advice(errors);
            Assert.Single(advice);
            Assert.Contains(construct, advice[0]);
            var hint = ExecDialect.Hint(errors, "2023", p => false);
            Assert.StartsWith("Hint: exec compiles C# 5, Revit 2023 API:", hint);
        }

        [Fact]
        public void AQuestionMarkDotInsideAString_IsNoConstruct()
        {
            var errors = new List<ApiLookup.CompileError>
            {
                new ApiLookup.CompileError { Number = "CS1002", Line = "sb.AppendLine(\"a?.b $\\\"x\\\"\") x", Column = 30, Message = "; expected" }
            };
            Assert.Empty(ExecDialect.CSharp5Advice(errors));
        }

        [Fact]
        public void ValidCSharp5_GivesNoHint()
        {
            Assert.Null(ExecDialect.Hint(Compile("var n = s != null ? s.Length : 0; var t = string.Format(\"{0}\", n);"), "2023", p => false));
        }

        [Fact]
        public void RevitYearGaps_OnlyWhenTheRunningApiLacksThem()
        {
            var errors = new List<ApiLookup.CompileError>
            {
                new ApiLookup.CompileError { Number = "CS1503", Line = "var id = new ElementId(123L);", Message = "Argument 1: cannot convert from 'long' to 'int'" },
                new ApiLookup.CompileError { Number = "CS1061", Line = "var e = fl.GetSlabShapeEditor();", Message = "'Autodesk.Revit.DB.Floor' does not contain a definition for 'GetSlabShapeEditor'" },
                new ApiLookup.CompileError { Number = "CS1061", Line = "sb.AppendLine(w.Id.Value.ToString());", Message = "'Autodesk.Revit.DB.ElementId' does not contain a definition for 'Value'" },
            };
            var in2023 = ExecDialect.YearAdvice(errors, "2023", p => false);
            Assert.Equal(3, in2023.Count);
            Assert.Contains(in2023, a => a.Contains("new ElementId(long) is Revit 2024+") && a.EndsWith("(this is Revit 2023)"));
            Assert.Contains(in2023, a => a.Contains("SlabShapeEditor"));
            Assert.Contains(in2023, a => a.Contains("IntegerValue"));
            Assert.Empty(ExecDialect.YearAdvice(errors, "2024", p => true));
        }

        [Fact]
        public void TheExecDescription_NamesTheDialect()
        {
            var d = new Services.Claude.Commands.ExecCommand().Description;
            Assert.Contains("C# 5, Revit ", d);
            Assert.Contains("no ?.", d);
        }
    }
}
