using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using VibeModel.Infrastructure;
using VibeModel.Services.Chat;
using VibeModel.Services.Claude;
using VibeModel.UI;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>
    /// The Settings window in tabs: five pages, Save in a fixed footer, no change-approval section,
    /// and every text in the chosen language (built on an STA thread with a scratch settings.json).
    /// </summary>
    [Collection("ProcessSettings")]
    public class SettingsDialogTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _env;

        public SettingsDialogTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "vm-settingsdlg-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _env = Environment.GetEnvironmentVariable(ChangeApprovalPolicy.EnvVar);
            Environment.SetEnvironmentVariable(ChangeApprovalPolicy.EnvVar, null);
            SettingsManager.OverridePathForTests(Path.Combine(_dir, "settings.json"));
            ManagedSettings.OverrideSearchPathsForTests(Path.Combine(_dir, "managed.json"));
            ManagedSettings.OverrideTrustCheckForTests(_ => null);
            // No `claude --version` spawned by the Claude Code tab.
            SettingsManager.SetValueForTests("ClaudeCliPath", "none");
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(ChangeApprovalPolicy.EnvVar, _env);
            SettingsManager.OverridePathForTests(null);
            ManagedSettings.OverrideSearchPathsForTests(null);
            ManagedSettings.OverrideTrustCheckForTests(null);
            try { Directory.Delete(_dir, true); } catch { }
        }

        private static IEnumerable<DependencyObject> Logical(DependencyObject root)
        {
            var stack = new Stack<DependencyObject>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var o = stack.Pop();
                yield return o;
                foreach (var child in LogicalTreeHelper.GetChildren(o).OfType<DependencyObject>())
                    stack.Push(child);
            }
        }

        private static List<string> Texts(Window w) =>
            Logical(w).Select(o =>
                    o is TextBlock t ? t.Text :
                    o is ContentControl c && c.Content is string s ? s : null)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();

        private static string Id(DependencyObject o) => AutomationProperties.GetAutomationId(o);

        [Theory]
        [InlineData("nl")]
        [InlineData("en")]
        public void FiveTabs_SaveInTheFooter_NoApprovalSection(string language)
        {
            SettingsManager.SetLanguage(language);
            MarkdownTableRenderingTests.Sta(() =>
            {
                var dialog = new SettingsDialog("Demo.rvt", Path.Combine(_dir, "Demo.rvt"));
                var all = Logical(dialog).ToList();
                var ids = all.Select(Id).Where(i => !string.IsNullOrEmpty(i)).ToList();
                foreach (var tab in new[] { "SettingsTabGeneral", "SettingsTabConnection", "SettingsTabProject", "SettingsTabStandards", "SettingsTabAbout" })
                    Assert.Contains(tab, ids);
                Assert.DoesNotContain(ids, i => i.StartsWith("SettingApproval") || i.StartsWith("SettingsExportChats"));

                // Save never scrolls away: no ScrollViewer above it.
                var save = all.OfType<Button>().Single(b => Id(b) == "SettingsSave");
                for (DependencyObject p = save; p != null; p = LogicalTreeHelper.GetParent(p))
                    Assert.False(p is ScrollViewer, "Save sits inside a scrolling page");

                // Exactly one page is shown: General, the first time.
                var pages = all.OfType<ScrollViewer>().ToList();
                Assert.Equal(5, pages.Count);
                Assert.Single(pages, p => p.Visibility == Visibility.Visible);

                var texts = Texts(dialog);
                Assert.DoesNotContain(texts, t => t.Contains("Wijzigingen door de AI") || t.Contains("Changes by the AI"));
                Assert.Contains(Strings.For(language, Strings.SettingsSave), texts);
                Assert.Contains(Strings.For(language, Strings.SettingsProjectTitle, "Demo.rvt"), texts);
                Assert.Contains(Strings.For(language, Strings.SettingsStandardsTitle), texts);

                // The other language's texts never leak in (the old hard-coded English included).
                var other = language == "nl" ? "en" : "nl";
                foreach (var key in new[] { Strings.SettingsTabGeneral, Strings.SettingsTabConnection, Strings.SettingsTabProject,
                                            Strings.SettingsTabStandards, Strings.SettingsTabAbout, Strings.SettingsBrowse,
                                            Strings.SettingsSave, Strings.SettingsLocalTab, Strings.SettingsProjectFolder })
                    Assert.DoesNotContain(Strings.For(other, key), texts);
                if (language == "nl")
                    foreach (var old in new[] { "AI Backend", "Project folder", "Office standards", "Local LLM Server", "Browse…", "Save", "Test Connection" })
                        Assert.DoesNotContain(old, texts);
                dialog.Close();
            });
        }

        [Fact]
        public void NotConnected_OpensOnTheConnectionTab_AndTheLastTabIsRemembered()
        {
            MarkdownTableRenderingTests.Sta(() =>
            {
                var dialog = new SettingsDialog(null, null, connectionNeeded: true);
                var shown = Logical(dialog).OfType<ScrollViewer>().Single(p => p.Visibility == Visibility.Visible);
                Assert.Contains(Logical(shown), o => Id(o) == "SettingsBackendApi");
                // No model open: the project page says so and its buttons are off.
                Assert.Contains(Strings.Get(Strings.SettingsProjectNoModel), Texts(dialog));
                dialog.Close();

                // Opened normally afterwards: the tab last shown (the connection tab) again.
                var again = new SettingsDialog(null, null);
                var page = Logical(again).OfType<ScrollViewer>().Single(p => p.Visibility == Visibility.Visible);
                Assert.Contains(Logical(page), o => Id(o) == "SettingsBackendApi");
                again.Close();
            });
        }
    }
}
