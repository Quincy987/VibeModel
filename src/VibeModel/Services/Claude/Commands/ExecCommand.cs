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
    public class ExecCommand : IClaudeCommand, IModificationCommand
    {
        public string Name => "exec";
        public string Description => "Execute C# code at runtime (use 'doc', 'uiDoc', 'uiApp')";
        public string Usage => "exec <csharp_code>";

        public string Execute(string args, UIApplication uiApp)
        {
            if (string.IsNullOrWhiteSpace(args))
                return "ERROR: Usage: exec <csharp_code>\n\nAvailable variables: doc, uiDoc, uiApp, sb (StringBuilder for output)\n\nExample: exec sb.AppendLine(doc.Title);";

            var uiDoc = uiApp.ActiveUIDocument;
            var doc = uiDoc?.Document;

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
    public static string Run(UIApplication uiApp)
    {
        var uiDoc = uiApp.ActiveUIDocument;
        var doc = uiDoc != null ? uiDoc.Document : null;
        var sb = new StringBuilder();

        using (var trans = new Transaction(doc, ""VibeModel: Exec""))
        {
            // Suppress warnings to prevent modal dialogs blocking the main thread
            var failOpts = trans.GetFailureHandlingOptions();
            failOpts.SetFailuresPreprocessor(new VibeModel.Services.Helpers.WarningSwallower());
            failOpts.SetClearAfterRollback(true);
            trans.SetFailureHandlingOptions(failOpts);

            trans.Start();
            try
            {
                " + args + @"

                if (trans.HasStarted())
                    trans.Commit();
            }
            catch (Exception ex)
            {
                if (trans.HasStarted())
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
                compilerParams.ReferencedAssemblies.Add(typeof(VibeModel.Services.Helpers.WarningSwallower).Assembly.Location); // VibeModel

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
                foreach (CompilerError error in results.Errors)
                {
                    if (!error.IsWarning)
                        sb.AppendLine("  Line " + error.Line + ": " + error.ErrorText);
                }
                sb.AppendLine();
                sb.AppendLine("Your code was wrapped in a method with: doc, uiDoc, uiApp, sb (StringBuilder)");
                sb.AppendLine("Use sb.AppendLine(...) to produce output.");
                return sb.ToString();
            }

            // Execute
            try
            {
                var assembly = results.CompiledAssembly;
                var type = assembly.GetType("DynamicCommand");
                var method = type.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
                var output = (string)method.Invoke(null, new object[] { uiApp });

                if (string.IsNullOrEmpty(output))
                    return "(exec completed, no output — use sb.AppendLine(...) to produce output)";

                return output;
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
    }
}
