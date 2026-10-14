using System;
using System.Diagnostics;
using System.IO;

namespace VibeModel.Services.Chat
{
    /// <summary>
    /// The WPF-free parts of connecting the chat (used by the connect wizard and Settings): the
    /// official per-user Claude Code installer call, and what a key test result means in words.
    /// </summary>
    public static class ClaudeSetup
    {
        /// <summary>The official per-user installer (no administrator rights; lands in %USERPROFILE%\.local\bin).</summary>
        public const string InstallCommand = "irm https://claude.ai/install.ps1 | iex";

        /// <summary>A visible PowerShell window that runs the installer and stays open with a closing line.</summary>
        public static ProcessStartInfo InstallStartInfo(string language)
        {
            var done = Strings.IsEnglish(language)
                ? "Done. Go back to Revit and press Check again. You can close this window."
                : "Klaar. Ga terug naar Revit en klik op Opnieuw controleren. Dit venster mag dicht.";
            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            return new ProcessStartInfo
            {
                FileName = powershell,
                Arguments = "-NoProfile -NoExit -ExecutionPolicy Bypass -Command \"& { " + InstallCommand +
                            "; Write-Host ''; Write-Host '" + done + "' -ForegroundColor Green }\"",
                UseShellExecute = true
            };
        }

        /// <summary>The sentence the wizard and Settings show for a key test.</summary>
        public static string DescribeKeyTest(string language, AnthropicHttp.KeyTestResult result, string model)
        {
            switch (result.Outcome)
            {
                case AnthropicHttp.KeyTestOutcome.Ok: return Strings.For(language, Strings.WizardApiOk, model);
                case AnthropicHttp.KeyTestOutcome.InvalidKey: return Strings.For(language, Strings.WizardApiInvalid);
                case AnthropicHttp.KeyTestOutcome.NoAccess: return Strings.For(language, Strings.WizardApiNoAccess);
                case AnthropicHttp.KeyTestOutcome.ModelUnknown: return Strings.For(language, Strings.WizardApiModelUnknown, model);
                case AnthropicHttp.KeyTestOutcome.Network:
                    return Strings.For(language, Strings.WizardApiNetwork, HostOf(AnthropicHttp.MessagesUrl), result.Detail);
                default: return Strings.For(language, Strings.WizardApiOther, result.Status, result.Detail);
            }
        }

        private static string HostOf(string url)
        {
            Uri uri;
            return Uri.TryCreate(url, UriKind.Absolute, out uri) ? uri.Host : url;
        }
    }
}
