using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Documents;
using VibeModel.Markdown;
using VibeModel.Markdown.Renderers;
using VibeModel.Services.Chat;
using VibeModel.UI;
using Xunit;
using WpfTable = System.Windows.Documents.Table;

namespace VibeModel.Tests
{
    /// <summary>
    /// Source markers ([bron: …]) in chat answers — workshop 2026-10-12: the werkvoorbereiders
    /// want to read back why a choice was made.
    /// </summary>
    public class CitationParserTests
    {
        private static string P(int n) => CitationParser.Placeholder(n);

        [Fact]
        public void SeveralMarkers_AreNumberedInOrder_AndARepeatedSourceKeepsItsNumber()
        {
            var r = CitationParser.Parse(
                "Nieuw is fase 2 [bron: leidraad 4.2]. De wand is 3000 mm [bron: revit 1543814]. " +
                "Zie ook hoofdstuk 4.2 [bron: Leidraad  4.2].");
            Assert.Equal(2, r.Citations.Count);
            Assert.Equal("leidraad 4.2", r.Citations[0].Source);
            Assert.Equal(1, r.Citations[0].Number);
            Assert.Equal(2, r.Citations[1].Number);
            Assert.Equal("Nieuw is fase 2" + P(1) + ". De wand is 3000 mm" + P(2) + ". Zie ook hoofdstuk 4.2" + P(1) + ".", r.Text);
        }

        [Fact]
        public void SemicolonSeparatedSources_BecomeTwoMarkersAtOneSpot()
        {
            var r = CitationParser.Parse("Fase bestaand [bron: standaard phasing; document memo.pdf p.3]");
            Assert.Equal(2, r.Citations.Count);
            Assert.Equal("Fase bestaand" + P(1) + P(2), r.Text);
        }

        [Theory]
        [InlineData("Geen dubbele punt [bron leidraad 4.2].")]
        [InlineData("Leeg [bron:] en [bron:   ].")]
        [InlineData("Niet gesloten [bron: leidraad 4.2")]
        [InlineData("Over twee regels [bron: leidraad\n4.2]")]
        [InlineData(@"Ontsnapt \[bron: leidraad 4.2]")]
        [InlineData("Gewone link [tekst](http://x) en [1]")]
        public void MalformedMarkers_StayText(string text)
        {
            var r = CitationParser.Parse(text);
            Assert.Empty(r.Citations);
            Assert.Equal(text, r.Text);
        }

        [Fact]
        public void MarkersInCodeSpansAndFences_AreIgnored()
        {
            var text = "Schrijf `[bron: leidraad 1]` zo, of ``a [bron: x] b``.\n" +
                       "```\ncurl [bron: leidraad 2]\n```\n" +
                       "Na het blok wel [bron: leidraad 3].";
            var r = CitationParser.Parse(text);
            Assert.Single(r.Citations);
            Assert.Equal("leidraad 3", r.Citations[0].Source);
            Assert.Contains("`[bron: leidraad 1]`", r.Text);
            Assert.Contains("``a [bron: x] b``", r.Text);
            Assert.Contains("curl [bron: leidraad 2]", r.Text);
            Assert.EndsWith("wel" + P(1) + ".", r.Text);
        }

        [Fact]
        public void LoneBacktick_DoesNotSwallowTheNextParagraphs()
        {
            var r = CitationParser.Parse("Een ` los teken.\n\nFase 2 [bron: leidraad 4.2].\n\nEn ` nog een.");
            Assert.Single(r.Citations);
            Assert.Contains("Fase 2" + P(1) + ".", r.Text);
        }

        [Fact]
        public void MarkerAsAListItem_KeepsTheBulletSpace()
        {
            var r = CitationParser.Parse("Bronnen:\n- [bron: leidraad 4.2]\n1. [bron: standaard phasing]\nTekst [bron: revit-api]");
            Assert.Equal("Bronnen:\n- " + P(1) + "\n1. " + P(2) + "\nTekst" + P(3), r.Text);
        }

        [Fact]
        public void TildeFence_AndUnclosedBacktick_AreHandled()
        {
            var r = CitationParser.Parse("~~~\n[bron: a]\n~~~\nEen ` los [bron: leidraad 5]");
            Assert.Single(r.Citations);
            Assert.Equal("leidraad 5", r.Citations[0].Source);
        }

        [Fact]
        public void EnglishSourceKeyword_IsAccepted()
        {
            var r = CitationParser.Parse("Phase new [source: standaard phasing].");
            Assert.Single(r.Citations);
            Assert.Equal(CitationKind.Standaard, r.Citations[0].Kind);
        }

        [Fact]
        public void Kinds_AndTheirFields()
        {
            var l = CitationParser.Describe("leidraad §4.2 Fasering");
            Assert.Equal(CitationKind.Leidraad, l.Kind);
            Assert.Equal("4.2", l.Chapter);

            var d = CitationParser.Describe("document PRG1002- DO.pdf p.3");
            Assert.Equal(CitationKind.Document, d.Kind);
            Assert.Equal("PRG1002- DO.pdf", d.Target);
            Assert.Equal(3, d.Page);

            var s = CitationParser.Describe("document bestek.pdf §2.1.4");
            Assert.Equal("bestek.pdf", s.Target);
            Assert.Equal("2.1.4", s.Section);
            Assert.Null(s.Page);

            var b = CitationParser.Describe("document memo.txt, blz. 12");
            Assert.Equal("memo.txt", b.Target);
            Assert.Equal(12, b.Page);

            var plain = CitationParser.Describe("document plan2.pdf");
            Assert.Equal("plan2.pdf", plain.Target);
            Assert.Null(plain.Page);

            var rv = CitationParser.Describe("revit 1543814 Lengte");
            Assert.Equal(CitationKind.Revit, rv.Kind);
            Assert.Equal(1543814L, rv.ElementId);

            var param = CitationParser.Describe("revit Phase Created");
            Assert.Equal(CitationKind.Revit, param.Kind);
            Assert.Null(param.ElementId);

            Assert.Equal(CitationKind.RevitApi, CitationParser.Describe("revit-api").Kind);
            Assert.Equal(CitationKind.RevitApi, CitationParser.Describe("Revit API JoinGeometry").Kind);
            Assert.Equal(CitationKind.Standaard, CitationParser.Describe("standaard phasing").Kind);
            Assert.Equal(CitationKind.Other, CitationParser.Describe("wikipedia").Kind);
            Assert.Equal(CitationKind.Other, CitationParser.Describe("documentatie").Kind);
        }

        [Theory]
        [InlineData("leidraad 4.2", "standards", "leidraad 4.2")]
        [InlineData("leidraad §4.2 Fasering", "standards", "leidraad 4.2")]
        [InlineData("standaard phasing", "standards", "phasing")]
        [InlineData("document memo.pdf p.3", "readdoc", "memo.pdf")]
        [InlineData("revit 1543814", "select", "1543814")]
        public void Lookup_NamesTheVibeModelCommand(string source, string command, string args)
        {
            Assert.True(CitationParser.Describe(source).TryGetLookup(out var c, out var a));
            Assert.Equal(command, c);
            Assert.Equal(args, a);
        }

        [Theory]
        [InlineData("revit-api")]
        [InlineData("revit Phase Created")]
        [InlineData("wikipedia")]
        public void TooltipOnlySources_HaveNoLookup(string source)
        {
            Assert.False(CitationParser.Describe(source).TryGetLookup(out _, out _));
        }

        [Fact]
        public void Superscripts()
        {
            Assert.Equal("¹", CitationParser.ToSuperscript(1));
            Assert.Equal("¹²", CitationParser.ToSuperscript(12));
            Assert.Equal("a³b", CitationParser.PlaceholdersToSuperscript("a" + P(3) + "b"));
        }

        [Fact]
        public void SourceText_DropsTheStamp_AndFindsPagesAndSections()
        {
            var response = "Hoofdstuk 4.2\nTekst\n-- model #47 | view: {3D} | selected: 0\n";
            Assert.Equal("Hoofdstuk 4.2\nTekst", CitationSourceText.Clean(response));

            var pdf = "file\n--- page 1 ---\naaa\n--- page 2 ---\nbbb\n--- page 12 ---\nccc";
            var page2 = CitationParser.Describe("document x.pdf p.2");
            var at = CitationSourceText.FindLocation(pdf, page2, out var len);
            Assert.Equal(pdf.IndexOf("--- page 2 ---", StringComparison.Ordinal), at);
            Assert.Equal("--- page 2 ---".Length, len);
            Assert.Equal(-1, CitationSourceText.FindLocation(pdf, CitationParser.Describe("document x.pdf p.3"), out _));

            var bestek = "Inhoud 4.2.1 en 4.2\n4.2.1 Sub\n4.2 Fasering\nTekst";
            var sec = CitationSourceText.FindLocation(bestek, CitationParser.Describe("document bestek.pdf §4.2"), out _);
            Assert.Equal(bestek.IndexOf("4.2 Fasering", StringComparison.Ordinal), sec);
        }
    }

    public class TableLayoutTests
    {
        [Theory]
        [InlineData(new[] { "3000", "12 345", "-1,5" }, true)]
        [InlineData(new[] { "3000 mm", "2,5 m²", "45°", "≈ 300 kg" }, true)]
        [InlineData(new[] { "3000", "", "—", "n.v.t." }, true)]
        [InlineData(new[] { "1543814", "1543815" }, true)]
        [InlineData(new[] { "3000", "Wand A" }, false)]
        [InlineData(new[] { "3 wanden" }, false)]
        [InlineData(new[] { "1:50" }, false)]
        [InlineData(new[] { "", "-" }, false)]
        public void NumericColumns(string[] cells, bool expected)
        {
            Assert.Equal(expected, TableLayout.IsNumericColumn(cells));
        }

        [Fact]
        public void NumbersGroupedWithNonBreakingSpaces_AreNumeric()
        {
            // "12 345" as Dutch number formatting writes it: NBSP / narrow NBSP between the groups.
            Assert.True(TableLayout.IsNumericColumn(new[] { "12\u00A0345 mm", "1\u202F000" }));
            Assert.True(VibeModel.Services.Helpers.RevitUnitHelper.IsBareNumber("12\u00A0345"));
        }

        [Fact]
        public void Tsv_HasHeaderAndRows_WithTabsAndNoStrayWhitespace()
        {
            var tsv = TableLayout.ToTsv(new List<string> { "Naam", "Lengte (mm)" },
                new List<IList<string>> { new List<string> { "W1\tx", "3000" }, new List<string> { "W2", "4 500" + CitationParser.Placeholder(1) } });
            Assert.Equal("Naam\tLengte (mm)\r\nW1 x\t3000\r\nW2\t4 500¹\r\n", tsv);
        }

        private static readonly System.Func<string, bool, double> SevenPx = (t, bold) => t.Length * 7.0;

        [Fact]
        public void MeasureColumns_MinIsTheLongestWord_AndNumbersNeverBreak()
        {
            var header = new List<string> { "Wand", "Lengte (mm)", "Opmerking" };
            var rows = new List<IList<string>> { new List<string> { "W1", "12 000", "een heel lange opmerking" } };
            var needs = TableLayout.MeasureColumns(header, rows, 3, new[] { false, true, false }, SevenPx);
            var chrome = TableLayout.CellChrome;
            Assert.Equal(4 * 7 + chrome, needs[0].Min);                  // "Wand"
            Assert.Equal(6 * 7 + chrome, needs[1].Min);                  // "Lengte" / "12 000" whole
            Assert.Equal(11 * 7 + chrome, needs[1].Max);                 // "Lengte (mm)" on one line
            Assert.Equal(9 * 7 + chrome, needs[2].Min);                  // "opmerking"
            Assert.Equal(24 * 7 + chrome, needs[2].Max);
        }

        [Fact]
        public void Fit_OneLineWhenItFits_ElseWordsFirst_ElseProportional()
        {
            var needs = new[]
            {
                new TableLayout.ColumnNeed { Min = 100, Max = 150 },   // Beton_Metselwerk_650
                new TableLayout.ColumnNeed { Min = 50, Max = 50 },     // 35,41
                new TableLayout.ColumnNeed { Min = 70, Max = 170 },    // 1. Bestaande situatie
            };
            Assert.Equal(new double[] { 150, 50, 170 }, TableLayout.Fit(needs, 500));   // compact, not stretched
            var mid = TableLayout.Fit(needs, 300);                                         // 80 spare over the words
            Assert.Equal(new double[] { 126, 50, 123 }, mid);
            Assert.True(mid.Sum() <= 300);
            var tight = TableLayout.Fit(needs, 176);                                       // below the words
            Assert.Equal(new double[] { 80, 40, 56 }, tight);                              // no numeric column: proportional

            needs[1].Numeric = true;                                                       // "35,41" stays whole,
            Assert.Equal(new double[] { 74, 50, 51 }, TableLayout.Fit(needs, 176));        // the text columns give way
            Assert.Equal(new double[] { 36, 18, 25 }, TableLayout.Fit(needs, 80));         // numbers > half the room: proportional
        }

        [Fact]
        public void HiddenRows_OnlyBeyondThirty()
        {
            Assert.Equal(0, TableLayout.HiddenRowCount(30));
            Assert.Equal(15, TableLayout.HiddenRowCount(45));
        }
    }

    /// <summary>Markdown → FlowDocument structure (WPF, so each test runs on an STA thread).</summary>
    public class MarkdownTableRenderingTests
    {
        internal static void Sta(Action action)
        {
            Exception error = null;
            var t = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { error = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();
            if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
        }

        private static readonly MarkdownRenderOptions Nl = new MarkdownRenderOptions { Language = "nl" };

        internal static IEnumerable<TextElement> All(FlowDocument doc)
        {
            var stack = new Stack<object>(doc.Blocks.Reverse());
            while (stack.Count > 0)
            {
                var o = stack.Pop();
                if (!(o is TextElement e)) continue;
                yield return e;
                IEnumerable<object> children =
                    e is Section s ? s.Blocks.Cast<object>() :
                    e is List l ? l.ListItems.Cast<object>() :
                    e is ListItem li ? li.Blocks.Cast<object>() :
                    e is WpfTable t ? t.RowGroups.Cast<object>() :
                    e is TableRowGroup g ? g.Rows.Cast<object>() :
                    e is TableRow r ? r.Cells.Cast<object>() :
                    e is TableCell c ? c.Blocks.Cast<object>() :
                    e is Paragraph p ? p.Inlines.Cast<object>() :
                    e is Span sp ? sp.Inlines.Cast<object>() :
                    Enumerable.Empty<object>();
                foreach (var child in children.Reverse()) stack.Push(child);
            }
        }

        internal static string Text(TextElement e) => new TextRange(e.ContentStart, e.ContentEnd).Text;

        private static Hyperlink Link(FlowDocument doc, string tag) =>
            All(doc).OfType<Hyperlink>().First(h => (h.Tag as string) == tag);

        [Fact]
        public void PipeTable_BecomesATable_WithHeaderRow_AndNumericColumnsRightAligned()
        {
            Sta(() =>
            {
                var md = "De langste wanden:\n\n| Wand | Lengte (mm) | Fase |\n|---|---|---|\n" +
                         "| W1 | 12 000 | Nieuw |\n| W2 | 8 500 | Bestaand |\n| W3 | 3 000 | Nieuw |\n";
                var doc = MarkdownHelper.ToFlowDocument(md, Nl);
                var table = All(doc).OfType<WpfTable>().Single();

                Assert.Equal(3, table.Columns.Count);
                // Short and numeric columns are sized to fit (no mid-number wrap).
                Assert.True(table.Columns.All(c => c.Width.IsAbsolute));
                Assert.Equal(2, table.RowGroups.Count);

                var header = table.RowGroups[0].Rows.Single();
                Assert.Equal(MarkdownStyles.BgTableHeader, header.Background);
                Assert.Equal(FontWeights.SemiBold, header.Cells[0].FontWeight);
                Assert.Equal("Lengte (mm)", Text(header.Cells[1]).Trim());

                var body = table.RowGroups[1].Rows;
                Assert.Equal(3, body.Count);
                Assert.Null(body[0].Background);
                Assert.Equal(MarkdownStyles.BgTableRowAlt, body[1].Background);
                Assert.Equal(TextAlignment.Left, body[0].Cells[0].TextAlignment);
                Assert.Equal(TextAlignment.Right, body[0].Cells[1].TextAlignment);
                Assert.Equal(TextAlignment.Left, body[0].Cells[2].TextAlignment);
                Assert.Equal("8 500", Text(body[1].Cells[1]).Trim());

                // The footer: Kopiëren only (nothing folded away).
                var footer = All(doc).OfType<Paragraph>().Single(p => (p.Tag as string) == TableRenderer.TableFooterTag);
                Assert.Equal("Kopiëren", Text(footer).Trim());
                Assert.DoesNotContain(All(doc).OfType<Hyperlink>(), h => (h.Tag as string) == TableRenderer.MoreRowsTag);
            });
        }

        [Fact]
        public void Columns_RefitToThePaneWidth_KeepingNumbersWhole()
        {
            Sta(() =>
            {
                var md = "| Wand (type) | Lengte (m) | Fase gemaakt |\n|---|---|---|\n" +
                         "| Beton_Metselwerk_650 | 35,41 | 1. Bestaande situatie |\n";
                var doc = MarkdownHelper.ToFlowDocument(md, Nl);
                var table = All(doc).OfType<WpfTable>().Single();
                var needs = (TableLayout.ColumnNeed[])table.Tag;

                TableRenderer.FitTables(doc, 1000);
                Assert.Equal(needs.Select(n => System.Math.Floor(n.Max)), table.Columns.Select(c => c.Width.Value));

                var narrow = needs.Sum(n => n.Min) + 20;
                TableRenderer.FitTables(doc, narrow);
                Assert.True(table.Columns[1].Width.Value >= needs[1].Min - 1);   // "35,41" stays whole
                Assert.True(table.Columns.Sum(c => c.Width.Value) <= narrow);
            });
        }

        [Fact]
        public void Adjacent_Markers_AreSeparated()
        {
            Sta(() =>
            {
                var doc = MarkdownHelper.ToFlowDocument("Vijf wanden [bron: revit 11; revit 12; revit-api].", Nl);
                Assert.StartsWith("Vijf wanden¹,²,³.", new TextRange(doc.Blocks.FirstBlock.ContentStart, doc.Blocks.FirstBlock.ContentEnd).Text);
            });
        }

        [Fact]
        public void ExplicitAlignment_Wins_OverNumericDetection()
        {
            Sta(() =>
            {
                var doc = MarkdownHelper.ToFlowDocument("| Id | Naam |\n|:--|--:|\n| 12 | a |\n", Nl);
                var row = All(doc).OfType<WpfTable>().Single().RowGroups[1].Rows[0];
                Assert.Equal(TextAlignment.Left, row.Cells[0].TextAlignment);
                Assert.Equal(TextAlignment.Right, row.Cells[1].TextAlignment);
            });
        }

        [Fact]
        public void LongTable_ShowsThirtyRows_AndUnfoldsOnClick()
        {
            Sta(() =>
            {
                var md = "| Wand | Lengte (mm) |\n|---|---|\n" +
                         string.Concat(Enumerable.Range(1, 45).Select(i => "| W" + i + " | " + (i * 100) + " |\n"));
                var doc = MarkdownHelper.ToFlowDocument(md, Nl);
                var body = All(doc).OfType<WpfTable>().Single().RowGroups[1];
                Assert.Equal(30, body.Rows.Count);

                var more = Link(doc, TableRenderer.MoreRowsTag);
                Assert.Equal("… nog 15 rijen", Text(more));
                more.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));

                Assert.Equal(45, body.Rows.Count);
                Assert.Equal("W45", Text(body.Rows[44].Cells[0]).Trim());
                Assert.DoesNotContain(All(doc).OfType<Hyperlink>(), h => (h.Tag as string) == TableRenderer.MoreRowsTag);
            });
        }

        [Fact]
        public void TableTexts_FollowTheLanguage()
        {
            Sta(() =>
            {
                var md = "| a | b |\n|---|---|\n" + string.Concat(Enumerable.Range(1, 31).Select(i => "| x | 1 |\n"));
                var doc = MarkdownHelper.ToFlowDocument(md, new MarkdownRenderOptions { Language = "en" });
                Assert.Equal("… 1 more row", Text(Link(doc, TableRenderer.MoreRowsTag)));
                Assert.Equal("Copy", Text(Link(doc, TableRenderer.CopyTag)));
            });
        }

        [Fact]
        public void TextWithPipes_ButNoDelimiterRow_StaysAParagraph()
        {
            Sta(() =>
            {
                var doc = MarkdownHelper.ToFlowDocument("a | b\nc | d", Nl);
                Assert.Empty(All(doc).OfType<WpfTable>());
            });
        }

        [Fact]
        public void Markers_RenderAsSuperscriptLinks_WithACollapsedSourcesFooter()
        {
            Sta(() =>
            {
                Citation clicked = null;
                var options = new MarkdownRenderOptions { Language = "nl", OnCitationClick = c => clicked = c };
                var doc = MarkdownHelper.ToFlowDocument(
                    "Een nieuwe doorsnede krijgt fase *Nieuw* [bron: leidraad 4.2]. API-grens [bron: revit-api].\n\n`[bron: leidraad 9]`",
                    options);

                var marker = All(doc).OfType<Hyperlink>().Single(h => Text(h) == "¹");
                Assert.Contains("leidraad 4.2", marker.ToolTip as string);
                marker.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
                Assert.Equal("leidraad 4.2", clicked?.Source);

                // revit-api: tooltip only (a Run, not a link).
                var apiMarker = All(doc).OfType<Run>().Single(r => r.Text == "²");
                Assert.Equal("revit-api", apiMarker.ToolTip as string);
                Assert.False(apiMarker.Parent is Hyperlink);

                // The code span keeps the raw marker.
                Assert.Contains("[bron: leidraad 9]", Text(doc.Blocks.ElementAt(1)));
                Assert.DoesNotContain("[bron: leidraad 4.2]", new TextRange(doc.ContentStart, doc.ContentEnd).Text);

                var footer = doc.Blocks.LastBlock;
                Assert.Equal(CitationInlines.FooterTag, footer.Tag);
                Assert.Equal("Bronnen (2) ▸", Text(footer).Trim());
                Assert.DoesNotContain(doc.Blocks, b => (b.Tag as string) == CitationInlines.ListTag);

                var toggle = All(doc).OfType<Hyperlink>().Single(h => Text(h).StartsWith("Bronnen", StringComparison.Ordinal));
                toggle.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
                var list = doc.Blocks.OfType<Section>().Single(b => (b.Tag as string) == CitationInlines.ListTag);
                Assert.Equal(2, list.Blocks.Count);
                Assert.Equal("¹ leidraad 4.2", Text(list.Blocks.FirstBlock).Trim());
                Assert.Equal("Bronnen (2) ▾", Text(footer).Trim());

                toggle.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
                Assert.DoesNotContain(doc.Blocks, b => (b.Tag as string) == CitationInlines.ListTag);
            });
        }

        [Fact]
        public void Markers_InsideATableCell_Render_AndNoFooterWithoutMarkers()
        {
            Sta(() =>
            {
                var doc = MarkdownHelper.ToFlowDocument("| Wand | Fase |\n|---|---|\n| W1 | Nieuw [bron: standaard phasing] |\n", Nl);
                var cell = All(doc).OfType<WpfTable>().Single().RowGroups[1].Rows[0].Cells[1];
                Assert.Equal("Nieuw¹", Text(cell).Trim());

                var plain = MarkdownHelper.ToFlowDocument("Geen bronnen hier.", Nl);
                Assert.DoesNotContain(plain.Blocks, b => (b.Tag as string) == CitationInlines.FooterTag);
            });
        }
    }

    public class ChatPromptRulesTests
    {
        [Theory]
        [InlineData("nl")]
        [InlineData("en")]
        public void Rules_CarryTablesUnitsAndSources(string language)
        {
            var rules = ChatBackendBase.BuildBehaviourRules(language);
            Assert.Contains("When you present more than 3 items with 2+ attributes, use a markdown table; first column = name/id, units in the header cell (`Lengte (mm)`), never in every cell.", rules);
            Assert.Contains("Always name the unit with every quantity (mm, m, m², m³, kg, kN, °, s); never give a bare number.", rules);
            Assert.Contains("VibeModel commands return mm unless stated; convert to m / m² / m³ when values exceed 10 000 mm and say so once.", rules);
            Assert.Contains("with one [bron: …] marker directly after the sentence; do not cite general Revit knowledge; never invent a source", rules);
            Assert.Contains("[bron: leidraad <chapter>]", rules);
            Assert.Contains("[bron: document <file> p.N]", rules);
            Assert.Contains("[bron: revit-api]", rules);
        }

        [Fact]
        public void TheRulesOwnExamples_AreNotWrappedInCode()
        {
            // A marker inside backticks is ignored by the pane — the prompt must not model that.
            Assert.DoesNotContain("`[bron:", ChatBackendBase.BuildBehaviourRules("nl"));
        }

        [Fact]
        public void ClaudeCode_TurnPrompt_CarriesTheNewRules()
        {
            var prompt = ClaudeCodeBackend.BuildTurnPrompt(@"C:\x\p.txt", 18884, "", "nl");
            Assert.Contains("never give a bare number", prompt);
            Assert.Contains("[bron: standaard <topic>]", prompt);
        }
    }

    public class WelcomeCardTextTests
    {
        [Fact]
        public void Dutch_IsTheDefault_AndCompact()
        {
            Assert.Equal("Claude Code · claude-opus-4-1", UiText.WelcomeBackendLine("Claude Code", "claude-opus-4-1", "nl"));
            Assert.Equal("Claude Code · standaardmodel", UiText.WelcomeBackendLine("Claude Code", null, "nl"));
            Assert.Equal("prg1002.rvt · Projectmap: C:\\proj", UiText.WelcomeModelLine("prg1002.rvt", "C:\\proj", "nl"));
            Assert.Equal("prg1002.rvt · geen projectmap", UiText.WelcomeModelLine("prg1002.rvt", null, "nl"));
            Assert.Equal("Geen model open", UiText.WelcomeModelLine("", null, "nl"));
            Assert.Equal(new[]
            {
                "Wat zit er in dit model?",
                "Waarom zie ik element … niet?",
                "Maak een doorsnede dwars op deze wand",
                "Zet deze views passend op een A0"
            }, UiText.WelcomeExamples("nl"));
        }

        [Fact]
        public void English_Variant()
        {
            Assert.Equal("Anthropic API · default model", UiText.WelcomeBackendLine("Anthropic API", " ", "en"));
            Assert.Equal("m.rvt · Project folder: D:\\x", UiText.WelcomeModelLine("m.rvt", "D:\\x", "en"));
            Assert.Equal("No model open", UiText.WelcomeModelLine(null, null, "en"));
            Assert.Equal("What is in this model?", UiText.WelcomeExamples("en")[0]);
            Assert.Contains("…", UiText.WelcomeExamples("en")[1]);
        }

        [Theory]
        [InlineData("nl", "De chat is nog niet ingesteld")]
        [InlineData("en", "No chat backend is set up yet")]
        public void NotConfigured_KeepsTheInstallHints_Localized(string language, string heading)
        {
            var lines = UiText.WelcomeSetupLines(language);
            Assert.Equal(4, lines.Length);
            Assert.StartsWith(heading, lines[0]);
            Assert.Contains("npm install -g @anthropic-ai/claude-code", lines[1]);
            Assert.Contains("Settings", lines[2]);
            Assert.Contains("Ollama", lines[3]);
        }

        [Theory]
        [InlineData("nl")]
        [InlineData("en")]
        public void ExamplesPopup_KeepsTheLongerList(string language)
        {
            var list = UiText.ExampleList(language);
            Assert.True(list.Length >= 8);
            Assert.Equal(UiText.WelcomeExamples(language)[0], list[0]);
            Assert.All(list, e => Assert.False(string.IsNullOrWhiteSpace(e)));
        }
    }
}
