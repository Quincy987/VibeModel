using System;
using System.CodeDom.Compiler;
using System.Linq;
using System.Reflection;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.CSharp;

namespace VibeModel.Services.Claude.Commands
{
    public class ExecCommand : IClaudeCommand, IModificationCommand, IStructuredCommand
    {
        public string Name => "exec";
        public string Description => "Execute C# code at runtime (use 'doc', 'uiDoc', 'uiApp'); loops with { } bodies stop after 25 s (?timeout=<s>, max 300)";
        public string Usage => "exec <csharp_code>";

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        /// <summary>
        /// Plan 17: the snippet's braced loops get a deadline check (<see cref="ExecDeadline"/>);
        /// when it fires the run is a structured EXEC_TIMEOUT error. Everything else is the legacy
        /// text, verbatim.
        /// </summary>
        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var clock = new ExecClock(ExecDeadline.BudgetSeconds(RunContext.Current?.TimeoutSeconds));
            int recordedBefore = Standards.CreatedElementsRecorder.RecordedCount;
            string text;
            try { text = Run(args, uiApp, clock); }
            finally { clock.Stop(); }
            // Plan 15: inside a run group (preview / read-only / approval) Revit refuses to save,
            // close or synchronize — say why instead of leaving Revit's bare message. Decided on the
            // snippet's own calls, not on the message text, which Revit words per UI language.
            if (RunContext.InRunGroup && text != null && text.IndexOf("ERROR", StringComparison.Ordinal) >= 0 &&
                ChangeApprovalPolicy.SavesOrCloses(args))
                text = text.TrimEnd() + "\nHint: " + ChangeApprovalPolicy.InRunGroupHint;
            if (!clock.Fired) return CommandResult.Legacy(text);
            // Transactions the snippet committed itself before the loop check fired are kept.
            bool kept = Standards.CreatedElementsRecorder.RecordedCount > recordedBefore;
            return CommandResult.Error(ExecDeadline.TimeoutCode, ExecDeadline.StoppedMessage(clock.Seconds, kept), ExecDeadline.StoppedHint);
        }

        private static string Run(string args, UIApplication uiApp, ExecClock clock)
        {
            if (string.IsNullOrWhiteSpace(args))
                return "ERROR: Usage: exec <csharp_code>\n\nAvailable variables: doc, uiDoc, uiApp, sb (StringBuilder for output)\n\nExample: exec sb.AppendLine(doc.Title);";

            var uiDoc = uiApp.ActiveUIDocument;
            var doc = uiDoc?.Document;

            // Plan 17: every loop body written with { } starts with __vm.Tick() (same line, so
            // compiler line numbers do not move).
            var code = ExecDeadline.Instrument(args, out _, out _);

            // Wrap user code in a class with standard boilerplate
            var fullSource = @"
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

public static class DynamicCommand
{
    public static string Run(UIApplication uiApp, VibeModel.Services.Claude.ExecClock __vm)
    {
        var uiDoc = uiApp.ActiveUIDocument;
        var doc = uiDoc != null ? uiDoc.Document : null;
        var sb = new StringBuilder();

        using (var trans = new Transaction(doc, ""VibeModel: Exec""))
        {
            // Revit failures are handled by VibeModel, so no modal failure dialog blocks the main thread
            var __vmFailures = VibeModel.Services.Helpers.TransactionHelper.Attach(trans);

            trans.Start();
            try
            {
                " + code + @"

                // A loop check that fired is never committed, even when the snippet caught it.
                if (__vm.Fired) throw new VibeModel.Services.Claude.ExecDeadlineException(__vm.Seconds);

                // A rollback the command handler collected is reported by ExecCommand itself.
                var __vmCommitError = VibeModel.Services.Helpers.TransactionHelper.CommitOrReport(trans, __vmFailures);
                var __vmCollected = VibeModel.Services.Helpers.FailureCollector.Current;
                if (__vmCommitError != null && (__vmCollected == null || !__vmCollected.RolledBack))
                    sb.AppendLine(""ERROR in exec: "" + __vmCommitError);
            }
            catch (Exception ex)
            {
                if (trans.HasStarted() && !trans.HasEnded())
                    trans.RollBack();
                sb.AppendLine(""ERROR in exec: "" + ex.Message);
            }
        }

        return sb.ToString();
    }
}";

            // Compile
            CompilerResults results;
            using (var provider = new CSharpCodeProvider())
            {
                var compilerParams = new CompilerParameters
                {
                    GenerateInMemory = true,
                    GenerateExecutable = false
                };

                // Add references
                compilerParams.ReferencedAssemblies.Add("System.dll");
                compilerParams.ReferencedAssemblies.Add("System.Core.dll");
                compilerParams.ReferencedAssemblies.Add(typeof(UIApplication).Assembly.Location);  // RevitAPIUI
                compilerParams.ReferencedAssemblies.Add(typeof(Document).Assembly.Location);        // RevitAPI
                compilerParams.ReferencedAssemblies.Add(typeof(VibeModel.Services.Helpers.TransactionHelper).Assembly.Location); // VibeModel

                try
                {
                    results = provider.CompileAssemblyFromSource(compilerParams, fullSource);
                }
                catch (Exception ex)
                {
                    return "ERROR: Compilation failed: " + ex.Message;
                }
            }

            if (results.Errors.HasErrors)
            {
                var sb = new StringBuilder();
                sb.AppendLine("COMPILATION ERRORS:");
                sb.AppendLine();
                var errors = new System.Collections.Generic.List<ApiLookup.CompileError>();
                var sourceLines = fullSource.Replace("\r\n", "\n").Split('\n');
                foreach (CompilerError error in results.Errors)
                {
                    if (!error.IsWarning)
                    {
                        sb.AppendLine("  Line " + error.Line + ": " + error.ErrorText);
                        errors.Add(new ApiLookup.CompileError
                        {
                            Number = error.ErrorNumber,
                            Message = error.ErrorText,
                            Line = error.Line >= 1 && error.Line <= sourceLines.Length ? sourceLines[error.Line - 1] : null,
                            Column = error.Column
                        });
                    }
                }
                sb.AppendLine();
                sb.AppendLine("Your code was wrapped in a method with: doc, uiDoc, uiApp, sb (StringBuilder)");
                sb.AppendLine("Use sb.AppendLine(...) to produce output.");
                // Plan 16: what the API really has for the member/type the compiler could not find.
                var hint = ApiLookupCommand.CompileErrorHint(errors);
                if (hint != null)
                {
                    sb.AppendLine();
                    sb.AppendLine(hint);
                }
                return sb.ToString();
            }

            // Execute
            try
            {
                var assembly = results.CompiledAssembly;
                var type = assembly.GetType("DynamicCommand");
                var method = type.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
                var output = (string)method.Invoke(null, new object[] { uiApp, clock });

                // Revit rolled back the wrapper transaction or one the snippet opened itself:
                // the caller must see an error with Revit's reason, not the snippet's output alone.
                var failures = VibeModel.Services.Helpers.FailureCollector.Current;
                if (failures != null && failures.RolledBack)
                    return RollbackError(failures.RollbackReason, output);

                if (string.IsNullOrEmpty(output))
                    return "(exec completed, no output — use sb.AppendLine(...) to produce output)";

                return output;
            }
            catch (TargetInvocationException tie) when (tie.InnerException is ExecDeadlineException)
            {
                // Thrown outside the wrapper's try (a loop in a lambda run later): the clock says so.
                return "ERROR: " + tie.InnerException.Message;
            }
            catch (TargetInvocationException tie)
            {
                return "ERROR: Runtime exception: " + (tie.InnerException != null ? tie.InnerException.Message : tie.Message);
            }
            catch (Exception ex)
            {
                return "ERROR: Execution failed: " + ex.Message;
            }
        }


        // A snippet can commit several transactions; only the one Revit refused is gone.
        internal const string RollbackHint = "Revit rolled back the transaction that failed; anything the snippet committed in other transactions was kept. Fix the cause (or delete/re-host the dependent annotation first) and retry.";

        internal static string RollbackError(string reason, string output)
        {
            var text = "ERROR: " + FailureReport.RollbackErrorMessage(reason) + "\nHint: " + RollbackHint;
            output = output?.TrimEnd();
            return string.IsNullOrEmpty(output) ? text : text + "\n\n" + output;
        }
    }
}
