using VibeModel.Services.Claude;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>
    /// Reflection-based command discovery and routing in <see cref="ClaudeCommandRegistry"/>.
    ///
    /// These run headless: constructing the registry reflects over the VibeModel assembly.
    /// The Revit reference assemblies are absent at test time, so the registry's
    /// ReflectionTypeLoadException fallback kicks in — Revit-derived types (external
    /// application / event handlers) are skipped, while the pure command types (which only
    /// implement in-assembly interfaces and have trivial constructors) still load and register.
    ///
    /// Note: the actual command-execution path (ExecuteCore/Execute) takes a Revit
    /// UIApplication in its signature, so it cannot be called from a headless test without
    /// referencing (and, at runtime, loading) RevitAPIUI. We therefore assert on the pure,
    /// Revit-free surface: which names resolve, and the query-vs-modification classification.
    /// The unknown-command *routing decision* is verified via GetCommands() lookup.
    /// </summary>
    public class CommandRegistryTests
    {
        // One discovery pass shared across the assertions.
        private static readonly ClaudeCommandRegistry Registry = new ClaudeCommandRegistry();

        [Fact]
        public void Discovery_FindsAHealthyNumberOfCommands()
        {
            Assert.True(Registry.GetCommands().Count >= 20,
                "Expected the reflection scan to register the full command set; got " +
                Registry.GetCommands().Count);
        }

        [Theory]
        [InlineData("wall")]
        [InlineData("floor")]
        [InlineData("info")]
        [InlineData("selected")]
        [InlineData("list")]
        [InlineData("help")]
        [InlineData("color")]
        [InlineData("delete")]
        public void Discovery_RegistersExpectedCommandNames(string name)
        {
            Assert.True(Registry.GetCommands().ContainsKey(name),
                "Command '" + name + "' was not discovered.");
        }

        [Fact]
        public void Lookup_IsCaseInsensitive()
        {
            Assert.True(Registry.GetCommands().ContainsKey("WALL"));
        }

        [Theory]
        [InlineData("wall")]
        [InlineData("floor")]
        [InlineData("color")]
        [InlineData("delete")]
        [InlineData("set")]
        public void IsModification_TrueForDocumentMutatingCommands(string name)
        {
            Assert.True(Registry.IsModification(name), name + " should be a modification command.");
        }

        [Theory]
        [InlineData("info")]
        [InlineData("selected")]
        [InlineData("list")]
        [InlineData("levels")]
        public void IsModification_FalseForReadOnlyCommands(string name)
        {
            Assert.False(Registry.IsModification(name), name + " should be read-only.");
        }

        [Fact]
        public void IsModification_UnknownCommand_IsFalse()
        {
            Assert.False(Registry.IsModification("does-not-exist"));
        }

        [Fact]
        public void UnknownCommand_DoesNotResolve()
        {
            // The routing decision: an unrecognized name is absent from the table, which is
            // what drives ExecuteCore's UNKNOWN_COMMAND error at the Revit-coupled boundary.
            Assert.False(Registry.GetCommands().ContainsKey("nonsense-command"));
        }
    }
}
