namespace VibeModel.Services.Claude
{
    /// <summary>
    /// Interface for all commands executable via the Claude-Revit bridge.
    /// Transport-agnostic: works over HTTP, file-based polling, or direct in-process calls.
    /// </summary>
    public interface IClaudeCommand
    {
        /// <summary>Command name used in routing (e.g. "floor", "selected").</summary>
        string Name { get; }

        /// <summary>Short description shown in help output.</summary>
        string Description { get; }

        /// <summary>Usage pattern (e.g. "wall &lt;x1&gt; &lt;y1&gt; &lt;x2&gt; &lt;y2&gt; [height]").</summary>
        string Usage { get; }

        /// <summary>
        /// Execute the command and return plain-text output.
        /// </summary>
        /// <param name="args">Arguments string (everything after the command name).</param>
        /// <param name="uiApp">Revit UIApplication — guaranteed to be on the main thread.</param>
        /// <returns>Plain-text result string.</returns>
        string Execute(string args, Autodesk.Revit.UI.UIApplication uiApp);
    }

    /// <summary>
    /// Marker interface for commands that modify the document.
    /// Used by HelpCommand to auto-categorize query vs modification commands.
    /// </summary>
    public interface IModificationCommand { }

    /// <summary>
    /// Optional richer contract a command can implement IN ADDITION TO IClaudeCommand to
    /// return a structured result (machine data + self-correcting errors). Commands that don't
    /// implement it are auto-wrapped by the registry, so migration is per-command, not big-bang.
    /// The legacy Execute(string) should delegate to ExecuteStructured(...).Text so there is one
    /// code path producing the text form.
    /// </summary>
    public interface IStructuredCommand
    {
        CommandResult ExecuteStructured(string args, Autodesk.Revit.UI.UIApplication uiApp);
    }
}
