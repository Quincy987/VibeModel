using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;
using VibeModel.Services.Chat;
using VibeModel.Services.Chat.QuickReplies;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>
    /// Quick replies (chips after an answer) — transferred from DegenAI's quick replies: the
    /// free pre-filter, the input, the prompt and, above all, the sanitizer that never trusts
    /// the model. The five model-behaviour cases of DegenAI's eval are replayed here as
    /// sanitizer cases on realistic model output.
    /// </summary>
    public class QuickReplySanitizerTests
    {
        private static string Json(bool offer, params (string label, string prompt, bool commits)[] replies) =>
            new JavaScriptSerializer().Serialize(new Dictionary<string, object>
            {
                { "offer", offer },
                { "replies", replies.Select(r => new Dictionary<string, object>
                    { { "label", r.label }, { "prompt", r.prompt }, { "commits", r.commits } }).ToArray() }
            });

        [Fact]
        public void YesNoQuestion_TheNoNeverSurvives()
        {
            // "Zal ik ze selecteren?" — Haiku really returned this on 2026-10-12.
            var replies = QuickReplyService.ParseAndSanitize(Json(true,
                ("Ja, selecteer ze", "Ja, selecteer de 14 deuren zonder Fire Rating.", false),
                ("Toon eerst de lijst", "Laat me eerst de 14 deuren zien.", false),
                ("Nee, laat maar", "Nee, selecteer ze niet.", false)));
            Assert.Equal(new[] { "Ja, selecteer ze", "Toon eerst de lijst" }, replies.Select(r => r.Label));
        }

        [Fact]
        public void EnglishDecline_IsDroppedToo()
        {
            var replies = QuickReplyService.ParseAndSanitize(Json(true,
                ("Start it now", "Start the section now.", true),
                ("Only on Level 2", "Only make sections on Level 2.", true),
                ("No thanks", "No thanks.", false)));
            Assert.Equal(2, replies.Count);
            Assert.DoesNotContain(replies, r => r.Label == "No thanks");
        }

        [Fact]
        public void UndoOffer_KeepIsNotAChoice()
        {
            // "Zal ik de oude doorsnede verwijderen?" — the decline sounds like a choice; it is not.
            var replies = QuickReplyService.ParseAndSanitize(Json(true,
                ("Verwijder hem", "Verwijder de oude doorsnede.", true),
                ("Nee, houd hem", "Houd de oude doorsnede.", false)));
            Assert.Empty(replies); // one survivor is no row
        }

        [Fact]
        public void EitherOr_ExactlyTheTwoActionsInOrder()
        {
            var replies = QuickReplyService.ParseAndSanitize(Json(true,
                ("Zet ze op fase 2", "Zet de 5 wanden op fase 2. Bouwfase.", true),
                ("Toon eerst de lijst", "Toon eerst de lijst van de 5 wanden.", false)));
            Assert.Equal(new[] { "Zet ze op fase 2", "Toon eerst de lijst" }, replies.Select(r => r.Label));
            Assert.True(replies[0].Commits);
            Assert.False(replies[1].Commits);
        }

        [Fact]
        public void FactualAnswer_OfferFalse_NoRow_EvenWithReplies()
        {
            Assert.Empty(QuickReplyService.ParseAndSanitize(Json(false,
                ("Toon alle parameters", "Toon alle parameters van de wand.", false),
                ("Maak een doorsnede", "Maak een doorsnede.", true))));
        }

        [Fact]
        public void LengthLimits_DropNeverCut_EmptyPromptFallsBack_DuplicatesGo()
        {
            var replies = QuickReplyService.ParseAndSanitize(Json(true,
                (new string('x', 45), "te lang label", false),
                ("Te lange prompt", new string('y', 241), false),
                ("Selecteer ze", "", false),
                ("Selecteer ze!", "Selecteer ze nog een keer.", false),
                ("", "lege label", false),
                ("Kleur ze rood", "Kleur ze rood in deze view.", true)));
            Assert.Equal(new[] { "Selecteer ze", "Kleur ze rood" }, replies.Select(r => r.Label));
            Assert.Equal("Selecteer ze", replies[0].Prompt); // empty prompt → the label
            Assert.Equal(44, QuickReplyService.MaxLabelLength);
            Assert.Single(QuickReplyService.ParseAndSanitize(Json(true, (new string('x', 44), "ok", false), ("b", "b", false))),
                r => r.Label.Length == 44);
        }

        [Fact]
        public void AtMostFour_AndGarbageIsNoRow()
        {
            var five = QuickReplyService.ParseAndSanitize(Json(true,
                ("a1", "a", false), ("a2", "b", false), ("a3", "c", false), ("a4", "d", false), ("a5", "e", false)));
            Assert.Equal(4, five.Count);
            Assert.Empty(QuickReplyService.ParseAndSanitize("not json"));
            Assert.Empty(QuickReplyService.ParseAndSanitize(""));
            Assert.Empty(QuickReplyService.ParseAndSanitize("{\"offer\":true}"));
            Assert.Empty(QuickReplyService.ParseAndSanitize("[1,2]"));
        }

        [Theory]
        [InlineData("Nee, bedankt", true)]
        [InlineData("nee bedankt!", true)]
        [InlineData("Laat maar", true)]
        [InlineData("Nu niet", true)]
        [InlineData("Laat het zo", true)]
        [InlineData("No thanks", true)]
        [InlineData("Not now", true)]
        [InlineData("Keep it as is", true)]
        [InlineData("Nu niet, eerst de lijst", false)]
        [InlineData("Not now, show me the 4h first", false)]
        [InlineData("Selecteer ze", false)]
        public void DeclineLabels_CompareTheWholeNormalizedLabel(string label, bool decline)
        {
            Assert.Equal(decline, DeclineLabels.IsDecline(label));
        }

        [Fact]
        public void Normalize_LettersAndDigitsLowercase()
        {
            Assert.Equal("neebedankt", DeclineLabels.Normalize("Nee, bedankt!"));
            Assert.Equal("fase2", DeclineLabels.Normalize(" Fase-2 "));
        }
    }

    public class QuickReplyInputTests
    {
        [Theory]
        [InlineData("Er staan 63 wanden in het model; 5 daarvan zijn langer dan 20 m. Zal ik ze selecteren?", false, false, true)]
        [InlineData("3000 mm.", false, false, false)]                     // too short
        [InlineData("Er staan 63 wanden in het model; 5 daarvan zijn langer dan 20 m.", true, false, false)] // cancelled
        [InlineData("Er staan 63 wanden in het model; 5 daarvan zijn langer dan 20 m.", false, true, false)] // error
        [InlineData("[Cancelled] Er staan 63 wanden in het model; 5 daarvan zijn langer.", false, false, false)]
        [InlineData("Error: VibeModel's HTTP server isn't responding on port 18884. Restart Revit.", false, false, false)]
        [InlineData("[Stopped: task went idle for 3 min. Say \"continue\" to resume.]", false, false, false)]
        public void ShouldOffer_TheFreePrefilter(string answer, bool cancelled, bool error, bool expected)
        {
            Assert.Equal(expected, QuickReplyService.ShouldOffer(answer, cancelled, error));
        }

        private static ChatSessionMessage M(string role, string content) =>
            new ChatSessionMessage { Role = role, Content = content };

        [Fact]
        public void UserInput_LastSixStoredMessages_CurrentTurnExcluded_EachCut()
        {
            var stored = new List<ChatSessionMessage>();
            for (int i = 1; i <= 8; i++)
            {
                stored.Add(M(ChatSession.RoleUser, "vraag " + i));
                // A stored answer: step notes first, the answer the user read last.
                stored.Add(M(ChatSession.RoleAssistant, "stap-notitie " + new string('z', 600) + " antwoord " + i));
            }
            stored.Add(M(ChatSession.RoleSystem, "Error: something"));
            stored.Add(M(ChatSession.RoleUser, "huidige vraag"));
            stored.Add(M(ChatSession.RoleAssistant, "huidig antwoord met tool-chatter"));

            var input = QuickReplyService.BuildUserInput(stored, "huidige vraag", "het getoonde antwoord");

            Assert.StartsWith("EARLIER IN THIS CONVERSATION:", input);
            Assert.DoesNotContain("vraag 5", input);              // only the last 6 earlier messages
            Assert.Contains("User: vraag 6", input);
            Assert.Contains("VibeModel: …", input);
            Assert.Contains(" antwoord 8\r\n", input);              // the answer's end survives the cut
            Assert.DoesNotContain("Error: something", input);     // system messages are not turns
            Assert.DoesNotContain("tool-chatter", input);         // the current turn comes separately
            Assert.Equal(1, Count(input, "huidige vraag"));
            Assert.DoesNotContain(new string('z', 501), input);   // 500 chars per earlier message
            Assert.DoesNotContain("stap-notitie", input);         // ...and of an answer, its END
            Assert.Contains("THE USER JUST ASKED:\r\nhuidige vraag", input);
            Assert.Contains("VIBEMODEL ANSWERED:\r\nhet getoonde antwoord", input);
            Assert.EndsWith("What should the user be offered as one-tap answers?", input);
        }

        [Fact]
        public void UserInput_FirstTurn_HasNoEarlierSection_AndLongTextsAreCut()
        {
            var input = QuickReplyService.BuildUserInput(
                new List<ChatSessionMessage> { M(ChatSession.RoleUser, "q") }, "q", new string('a', 5000));
            Assert.StartsWith("THE USER JUST ASKED:", input);
            Assert.Contains(new string('a', QuickReplyService.AnswerChars) + "…", input);
            Assert.DoesNotContain(new string('a', QuickReplyService.AnswerChars + 1), input);
        }

        private static int Count(string text, string part)
        {
            int n = 0, i = 0;
            while ((i = text.IndexOf(part, i, System.StringComparison.Ordinal)) >= 0) { n++; i += part.Length; }
            return n;
        }

        [Fact]
        public void SystemPrompt_CarriesTheRules_AndFillsEveryPlaceholder()
        {
            var prompt = QuickReplyService.BuildSystemPrompt(new QuickReplyContext
            {
                ActiveView = "Doorsnede 1-1",
                Phase = "2. Bouwfase",
                Selection = "2 element(s) selected",
                ToolsCalled = new[] { "list", "select", "list" }
            });
            Assert.DoesNotContain("{{", prompt);
            Assert.Contains("VibeModel, the chat pane inside Autodesk Revit", prompt);
            Assert.Contains("There are either none of them or two to four.", prompt);
            // The no-decline rule and the language rule.
            Assert.Contains("the \"no\" is NEVER a button", prompt);
            Assert.Contains("Every button must make something happen.", prompt);
            Assert.Contains("Write labels and prompts in the language VibeModel answered in", prompt);
            // The capability list and what commits means in Revit.
            Assert.Contains("Only propose things VibeModel can actually do: inspect, select, zoom to, colour, isolate or hide elements", prompt);
            Assert.Contains("commits -- true when acting on this would change the Revit model", prompt);
            // The context, filled.
            Assert.Contains("Active view: Doorsnede 1-1", prompt);
            Assert.Contains("Phase: 2. Bouwfase", prompt);
            Assert.Contains("Selection: 2 element(s) selected", prompt);
            Assert.Contains("Tools VibeModel used to produce this answer: list, select", prompt);

            var empty = QuickReplyService.BuildSystemPrompt(null);
            Assert.Contains("Tools VibeModel used to produce this answer: none", empty);
            Assert.Contains("Selection: nothing selected", empty);
        }

        [Fact]
        public void Schema_IsTheTransferredOne()
        {
            var schema = new JavaScriptSerializer().DeserializeObject(QuickReplyService.SchemaJson) as Dictionary<string, object>;
            Assert.NotNull(schema);
            Assert.Contains("\"maxItems\":4", QuickReplyService.SchemaJson);
            Assert.Contains("\"maxLength\":44", QuickReplyService.SchemaJson);
            Assert.Contains("\"maxLength\":240", QuickReplyService.SchemaJson);
            Assert.Contains("\"required\":[\"offer\",\"replies\"]", QuickReplyService.SchemaJson);
        }
    }

    public class QuickReplyTransportTests
    {
        // A real `claude -p --output-format json --json-schema …` envelope (trimmed), 2026-10-12.
        private const string CliEnvelope =
            "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false," +
            "\"result\":\"{\\\"offer\\\":true,\\\"replies\\\":[]}\"," +
            "\"structured_output\":{\"offer\":true,\"replies\":[{\"label\":\"Ja, selecteer ze\",\"prompt\":\"Ja, selecteer de 14 deuren.\",\"commits\":false}," +
            "{\"label\":\"Toon eerst de lijst\",\"prompt\":\"Toon eerst de lijst.\",\"commits\":false}]}}";

        [Fact]
        public void CliOutput_StructuredOutputWins()
        {
            var json = QuickReplyService.ParseCliOutput(CliEnvelope);
            Assert.Contains("Ja, selecteer ze", json);
            Assert.Equal(2, QuickReplyService.ParseAndSanitize(json).Count);
        }

        [Fact]
        public void CliOutput_JsonInResultText_WhenNoStructuredField()
        {
            var stdout = "warning: something\n{\"type\":\"result\",\"is_error\":false,\"result\":\"Here you go:\\n```json\\n{\\\"offer\\\":false,\\\"replies\\\":[]}\\n```\"}";
            Assert.Equal("{\"offer\":false,\"replies\":[]}", QuickReplyService.ParseCliOutput(stdout));
        }

        [Fact]
        public void CliOutput_PrettyPrintedEnvelope()
        {
            var stdout = "{\n  \"is_error\": false,\n  \"structured_output\": {\"offer\": false, \"replies\": []}\n}";
            Assert.Equal("{\"offer\":false,\"replies\":[]}", QuickReplyService.ParseCliOutput(stdout));
        }

        [Theory]
        [InlineData("error: unknown option '--effort'", "--effort")]
        [InlineData("error: unknown option '--json-schema=x'", "--json-schema")]
        [InlineData("Error: Unknown option '--strict-mcp-config'\n(Did you mean --effort?)", "--strict-mcp-config")]
        [InlineData("API Error: 529 overloaded", null)]
        [InlineData(null, null)]
        public void CliRejectedOption_IsReadExactly(string stderr, string expected)
        {
            Assert.Equal(expected, ClaudeCliSuggestionModel.RejectedOption(stderr));
        }

        [Theory]
        [InlineData("{\"choices\":[{\"message\":{\"content\":\"{\\\"offer\\\":false}\"}}]}", "{\"offer\":false}")]
        [InlineData("{\"choices\":[{\"text\":\"x\"}]}", null)]
        [InlineData("{\"choices\":[]}", null)]
        [InlineData("{}", null)]
        public void LocalResponse_MessageContent(string response, string expected)
        {
            Assert.Equal(expected, LocalSuggestionModel.MessageContent(response, new JavaScriptSerializer()));
        }

        [Theory]
        [InlineData("{\"type\":\"result\",\"is_error\":true,\"result\":\"API Error: 529\"}")]
        [InlineData("")]
        [InlineData("not json at all")]
        public void CliOutput_ErrorsAndGarbage_AreNull(string stdout)
        {
            Assert.Null(QuickReplyService.ParseCliOutput(stdout));
        }

        [Fact]
        public void CliArguments_StructuredHaiku_NoTools_NoSession()
        {
            var args = ClaudeCliSuggestionModel.BuildArguments("SYSTEM \"quoted\"", new[] { "--effort low", "--strict-mcp-config" });
            Assert.StartsWith("-p --model haiku --output-format json --no-session-persistence --json-schema ", args);
            Assert.Contains(" --tools \"\" --effort low --strict-mcp-config --system-prompt ", args);
            Assert.EndsWith("\"SYSTEM \\\"quoted\\\"\"", args);
            Assert.Contains("\\\"maxItems\\\":4", args); // the schema, escaped for the command line
        }

        [Fact]
        public void AnthropicRequest_ForcesTheTool_OnHaiku()
        {
            var json = new JavaScriptSerializer();
            var body = AnthropicSuggestionModel.BuildRequest("sys", "input", json);
            Assert.Equal("claude-haiku-5-5", body["model"]);
            Assert.Equal(1024, body["max_tokens"]);
            var choice = (Dictionary<string, object>)body["tool_choice"];
            Assert.Equal("tool", choice["type"]);
            Assert.Equal("quick_replies", choice["name"]);
            var serialized = json.Serialize(body);
            Assert.Contains("\"input_schema\":{\"type\":\"object\"", serialized);
        }

        [Fact]
        public void AnthropicResponse_ToolInputIsTheJson()
        {
            var response = "{\"content\":[{\"type\":\"text\",\"text\":\"x\"},{\"type\":\"tool_use\",\"name\":\"quick_replies\",\"input\":{\"offer\":false,\"replies\":[]}}]}";
            Assert.Equal("{\"offer\":false,\"replies\":[]}", AnthropicSuggestionModel.ToolInput(response, new JavaScriptSerializer()));
            Assert.Null(AnthropicSuggestionModel.ToolInput("{\"content\":[]}", new JavaScriptSerializer()));
        }

        [Fact]
        public void LocalRequest_JsonMode_AndSchemaInThePrompt()
        {
            var body = LocalSuggestionModel.BuildRequest("sys", "input", true);
            Assert.Equal(false, body["stream"]);
            Assert.True(body.ContainsKey("response_format"));
            var serialized = new JavaScriptSerializer().Serialize(body);
            Assert.Contains("Reply with ONLY a JSON object matching this schema", serialized);
            Assert.False(LocalSuggestionModel.BuildRequest("sys", "input", false).ContainsKey("response_format"));
        }

        [Theory]
        [InlineData("```json\n{\"offer\":false,\"replies\":[]}\n```", "{\"offer\":false,\"replies\":[]}")]
        [InlineData("Sure! {\"offer\":true} done", "{\"offer\":true}")]
        [InlineData("no braces", null)]
        public void ExtractJsonObject_FromProse(string text, string expected)
        {
            Assert.Equal(expected, QuickReplyService.ExtractJsonObject(text));
        }
    }

    public class StarterTests
    {
        [Fact]
        public void NothingKnown_TheFourDefaults()
        {
            Assert.Equal(new[]
            {
                "Wat zit er in dit model?",
                "Waarom zie ik element … niet?",
                "Maak een doorsnede dwars op deze wand",
                "Zet deze views passend op een A0"
            }, Starters.Pick(new StarterContext(), "nl"));
        }

        [Fact]
        public void Selection_ComesFirst_ThenDefaults_MaxFour()
        {
            var chips = Starters.Pick(new StarterContext { SelectedCount = 2 }, "nl");
            Assert.Equal(4, chips.Count);
            Assert.Equal("Wat heb ik geselecteerd?", chips[0]);
            Assert.Equal("Wat zit er in dit model?", chips[1]);
        }

        [Fact]
        public void EveryRule_InPriorityOrder_NoDuplicates()
        {
            var chips = Starters.Pick(new StarterContext
            {
                SelectedCount = 1,
                ActiveViewIsSheet = true,
                PackActive = true,
                ProjectDocCount = 3
            }, "nl");
            Assert.Equal(new[]
            {
                "Wat heb ik geselecteerd?",
                "Zet deze views passend op dit blad",
                "Controleer dit model tegen de kantoorstandaard",
                "Welke documenten zitten in de projectmap?"
            }, chips);
            Assert.Equal(chips.Count, chips.Distinct().Count());
        }

        [Fact]
        public void Sheet_ReplacesTheA0Example()
        {
            var chips = Starters.Pick(new StarterContext { ActiveViewIsSheet = true }, "nl");
            Assert.Equal("Zet deze views passend op dit blad", chips[0]);
            Assert.DoesNotContain("Zet deze views passend op een A0", chips);
            Assert.Equal(4, chips.Count);
        }

        [Fact]
        public void Defaults_AreTheWelcomeExamples()
        {
            Assert.Equal(VibeModel.UI.UiText.WelcomeExamples("nl"), Starters.Defaults("nl"));
            Assert.Equal(VibeModel.UI.UiText.WelcomeExamples("en"), Starters.Defaults("en"));
        }

        [Fact]
        public void English()
        {
            var chips = Starters.Pick(new StarterContext { SelectedCount = 3, PackActive = true }, "en");
            Assert.Equal("What have I selected?", chips[0]);
            Assert.Equal("Check this model against the office standard", chips[1]);
            Assert.Equal("What is in this model?", chips[2]);
            Assert.Equal(4, chips.Count);
        }
    }
}
