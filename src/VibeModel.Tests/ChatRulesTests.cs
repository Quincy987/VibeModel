using System.Collections.Generic;
using System.Linq;
using VibeModel.Services.Chat;
using VibeModel.Services.Claude;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>
    /// The chat's language setting, its fixed texts in both languages, and the behaviour-rules
    /// block both backends send (workshop 2026-10-12: blocked delete, English replies to Dutch
    /// questions, unasked changes to shared resources, walls of text).
    /// </summary>
    public class ChatStringsTests
    {
        [Theory]
        [InlineData(null, "nl")]
        [InlineData("", "nl")]
        [InlineData("nl", "nl")]
        [InlineData("NL", "nl")]
        [InlineData("de", "nl")]
        [InlineData("en", "en")]
        [InlineData(" EN ", "en")]
        [InlineData("en-GB", "en")]
        [InlineData("English", "en")]
        public void Normalize_DefaultsToDutch(string input, string expected)
        {
            Assert.Equal(expected, Strings.Normalize(input));
        }

        [Fact]
        public void EveryKey_HasADutchText_ThatDiffersFromEnglish()
        {
            foreach (var key in Strings.Keys)
            {
                Assert.True(Strings.HasDutch(key), "No Dutch text for " + key);
                Assert.NotEqual(Strings.For("en", key), Strings.For("nl", key));
            }
        }

        [Fact]
        public void For_PicksTheLanguage_AndFormatsArguments()
        {
            Assert.Equal("Claude Code exited with code 3", Strings.For("en", Strings.ProcessExitCode, 3));
            Assert.Equal("Claude Code is gestopt met code 3", Strings.For("nl", Strings.ProcessExitCode, 3));
            Assert.Contains("18885", Strings.For("nl", Strings.ServerNotResponding, 18885));
            Assert.StartsWith("De chat is nog niet ingesteld", Strings.For("nl", Strings.BackendNotInitialized));
            Assert.StartsWith("Chat backend not initialized", Strings.For("en", Strings.BackendNotInitialized));
        }

        [Fact]
        public void For_UnknownKey_ComesBackAsItself()
        {
            Assert.Equal("NoSuchKey", Strings.For("nl", "NoSuchKey"));
        }

        [Fact]
        public void StatusVerbs_DutchAndEnglish_CoverTheSameCommands()
        {
            var en = new HashSet<string>(StatusVerbs.EnglishCommands);
            var nl = new HashSet<string>(StatusVerbs.DutchCommands);
            Assert.Empty(en.Except(nl));
            Assert.Empty(nl.Except(en));
        }

        [Theory]
        [InlineData("wall", "nl", "Wand maken…")]
        [InlineData("revit_delete", "nl", "Elementen verwijderen…")]
        [InlineData("info", "nl", "Model bekijken…")]
        [InlineData("wall", "en", "Creating wall…")]
        [InlineData("revit_delete", "en", "Deleting elements…")]
        [InlineData("frobnicate", "nl", "Bezig… (frobnicate)")]
        [InlineData("frobnicate", "en", "Working… (frobnicate)")]
        public void StatusVerbs_Describe_InBothLanguages(string tool, string language, string expected)
        {
            Assert.Equal(expected, StatusVerbs.Describe(tool, language));
        }
    }

    public class BehaviourRulesTests
    {
        [Theory]
        [InlineData("nl", "If unclear, use Dutch.")]
        [InlineData("en", "If unclear, use English.")]
        public void Rules_NameTheFallbackLanguage(string language, string expected)
        {
            var rules = ChatBackendBase.BuildBehaviourRules(language);
            Assert.Contains("Reply in the language the user writes in. " + expected, rules);
            Assert.Contains("Keep Revit UI terms (ribbon/command names) in English", rules);
        }

        [Theory]
        [InlineData("nl")]
        [InlineData("en")]
        public void Rules_CarryUndoAskFirstAndCompactBlocks(string language)
        {
            var rules = ChatBackendBase.BuildBehaviourRules(language);
            Assert.Contains("one Revit undo step (Ctrl+Z)", rules);
            Assert.Contains("never refused or redirected to manual steps", rules);
            Assert.Contains("view templates, project materials, families/types, shared parameters, phases, worksets", rules);
            Assert.Contains("ask for confirmation first", rules);
            Assert.Contains("just do it, then report", rules);
            Assert.Contains("at most 5 short lines", rules);
            Assert.Contains("No element ids in running text", rules);
            Assert.Contains("Never repeat the question", rules);
            Assert.Contains("6 words max", rules);
        }

        [Fact]
        public void Rules_DifferOnlyInTheFallbackLanguage()
        {
            var nl = ChatBackendBase.BuildBehaviourRules("nl");
            var en = ChatBackendBase.BuildBehaviourRules("en");
            Assert.Equal(nl.Replace("use Dutch.", "use English."), en);
        }

        [Theory]
        [InlineData("nl", "use Dutch.")]
        [InlineData("en", "use English.")]
        public void ClaudeCode_TurnPrompt_CarriesTheRules(string language, string expected)
        {
            var prompt = ClaudeCodeBackend.BuildTurnPrompt(@"C:\x\system-prompt-18884.txt", 18884, "CURRENT REVIT CONTEXT: x", language);
            Assert.Contains("RULES", prompt);
            Assert.Contains(expected, prompt);
            Assert.Contains("one Revit undo step (Ctrl+Z)", prompt);
            Assert.Contains("CURRENT REVIT CONTEXT: x", prompt);
            Assert.Contains("127.0.0.1:18884", prompt);
            // The old "suggest 2-3 things" nudge fought the 5-line rule.
            Assert.DoesNotContain("suggest 2-3", prompt);
        }

        [Fact]
        public void ClaudeCode_TurnPrompt_WithoutContext_StillCarriesTheRules()
        {
            var prompt = ClaudeCodeBackend.BuildTurnPrompt(@"C:\x\p.txt", 18884, "", "nl");
            Assert.Contains("If unclear, use Dutch.", prompt);
        }

        [Fact]
        public void ClaudeCode_PromptFile_NoLongerAsksToConfirmDeletes()
        {
            var text = ClaudeCodeBackend.BuildSystemPromptText(new ClaudeCommandRegistry().GetCommands(), 18884, null);
            Assert.DoesNotContain("confirm destructive operations", text);
            Assert.DoesNotContain("outline the full sequence up front", text);
            Assert.Contains("BEHAVIOR:", text);
        }

        [Fact]
        public void ClaudeCode_RunsInDontAskMode_WithOnlyBashAndRead()
        {
            Assert.Equal("--permission-mode dontAsk --allowedTools Bash,Read,Grep,Glob", ClaudeCodeBackend.PermissionArgs);
        }

        [Theory]
        [InlineData("error: option '--permission-mode <mode>' argument 'dontAsk' is invalid. Allowed choices are default, plan.", true)]
        [InlineData("No conversation found with session ID: abc", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void ClaudeCode_DetectsACliThatRejectsThePermissionMode(string stderr, bool expected)
        {
            Assert.Equal(expected, ClaudeCodeBackend.IsPermissionModeRejected(stderr));
        }
    }
}
