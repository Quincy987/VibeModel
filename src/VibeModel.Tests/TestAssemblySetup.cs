using System.IO;
using System.Runtime.CompilerServices;
using VibeModel.Infrastructure;

namespace VibeModel.Tests
{
    /// <summary>
    /// Runs once, before any test: warnings and errors the code under test logs (it always writes
    /// those) go to a temp folder, not into the developer's %LOCALAPPDATA%\VibeModel\logs.
    /// </summary>
    internal static class TestAssemblySetup
    {
        [ModuleInitializer]
        internal static void Init() =>
            Logger.RedirectForTests(Path.Combine(Path.GetTempPath(), "VibeModel.Tests", "logs"));
    }
}

namespace System.Runtime.CompilerServices
{
    // net48 does not ship the attribute; the C# compiler only needs one with this name.
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal sealed class ModuleInitializerAttribute : Attribute { }
}
