using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using VibeModel.UI;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using WpfTable = System.Windows.Documents.Table;
using WpfTableCell = System.Windows.Documents.TableCell;
using WpfTableRow = System.Windows.Documents.TableRow;

namespace VibeModel.Markdown.Renderers
{
    /// <summary>
    /// A markdown pipe/grid table as a real FlowDocument <see cref="WpfTable"/>: bold header row
    /// on a light fill, alternating row tint, padded cells, numeric columns right-aligned, star
    /// column widths from the content (capped, so long text wraps and the pane never scrolls
    /// sideways). Under the table one muted line: "… nog N rijen" (when rows were folded away)
    /// and "Kopiëren" (TSV to the clipboard). Layout decisions live in <see cref="TableLayout"/>.
    /// </summary>
    public class TableRenderer : WpfObjectRenderer<MdTable>
    {
        protected override void Write(WpfRenderer renderer, MdTable table)
        {
            var rows = table.OfType<MdTableRow>().ToList();
            var columns = rows.Count == 0 ? 0 : rows.Max(r => r.OfType<MdTableCell>().Sum(c => Math.Max(1, c.ColumnSpan)));
            if (columns == 0) return;

            var headerRows = rows.Where(r => r.IsHeader).ToList();
            var bodyRows = rows.Where(r => !r.IsHeader).ToList();
            var headerText = headerRows.Count > 0 ? CellTexts(headerRows[0]) : new List<string>();
            var bodyText = bodyRows.Select(r => (IList<string>)CellTexts(r)).ToList();

            var alignments = new TextAlignment[columns];
            for (int c = 0; c < columns; c++)
            {
                var explicitAlign = c < table.ColumnDefinitions.Count ? table.ColumnDefinitions[c].Alignment : null;
                if (explicitAlign == TableColumnAlign.Right) alignments[c] = TextAlignment.Right;
                else if (explicitAlign == TableColumnAlign.Center) alignments[c] = TextAlignment.Center;
                else if (explicitAlign == TableColumnAlign.Left) alignments[c] = TextAlignment.Left;
                else
                {
                    var col = c;
                    alignments[c] = TableLayout.IsNumericColumn(bodyText.Select(r => col < r.Count ? r[col] : ""))
                        ? TextAlignment.Right
                        : TextAlignment.Left;
                }
            }

            var weights = TableLayout.ColumnWeights(headerText, bodyText, columns);
            var wpfTable = new WpfTable
            {
                CellSpacing = 0,
                FontSize = MarkdownStyles.FontSizeTable,
                BorderBrush = MarkdownStyles.BorderColor,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 2, 0, 0)
            };
            foreach (var w in weights)
                wpfTable.Columns.Add(new TableColumn { Width = new GridLength(w, GridUnitType.Star) });

            renderer.Push(wpfTable);
            if (headerRows.Count > 0)
            {
                renderer.Push(new TableRowGroup());
                foreach (var row in headerRows)
                    WriteRow(renderer, row, alignments, header: true, alternate: false);
                renderer.Pop();
            }

            var body = new TableRowGroup();
            renderer.Push(body);
            for (int i = 0; i < bodyRows.Count; i++)
                WriteRow(renderer, bodyRows[i], alignments, header: false, alternate: i % 2 == 1);
            renderer.Pop();
            renderer.Pop();

            // Long tables: the first ShownRows rows show; the rest wait behind a link.
            var hidden = new List<WpfTableRow>();
            for (var n = TableLayout.HiddenRowCount(body.Rows.Count); n > 0; n--)
            {
                var last = body.Rows[body.Rows.Count - 1];
                body.Rows.Remove(last);
                hidden.Insert(0, last);
            }

            renderer.WriteBlock(BuildFooter(renderer, body, hidden, TableLayout.ToTsv(headerText, bodyText)));
        }

        private static void WriteRow(WpfRenderer renderer, MdTableRow row, TextAlignment[] alignments, bool header, bool alternate)
        {
            var wpfRow = new WpfTableRow();
            if (header) wpfRow.Background = MarkdownStyles.BgTableHeader;
            else if (alternate) wpfRow.Background = MarkdownStyles.BgTableRowAlt;
            renderer.Push(wpfRow);

            int column = 0;
            foreach (var cell in row.OfType<MdTableCell>())
            {
                var span = Math.Max(1, cell.ColumnSpan);
                var wpfCell = new WpfTableCell
                {
                    Padding = new Thickness(6, 3, 6, 3),
                    TextAlignment = column < alignments.Length ? alignments[column] : TextAlignment.Left,
                    ColumnSpan = span
                };
                if (header)
                {
                    wpfCell.FontWeight = FontWeights.SemiBold;
                    wpfCell.BorderBrush = MarkdownStyles.BorderColor;
                    wpfCell.BorderThickness = new Thickness(0, 0, 0, 1);
                }
                renderer.Push(wpfCell);
                renderer.WriteChildren(cell);
                renderer.Pop();
                column += span;
            }
            renderer.Pop();
        }

        private static Paragraph BuildFooter(WpfRenderer renderer, TableRowGroup body, List<WpfTableRow> hidden, string tsv)
        {
            var language = renderer.Options.Language;
            var footer = new Paragraph
            {
                FontSize = MarkdownStyles.FontSizeSmall,
                Foreground = MarkdownStyles.FgSecondary,
                Margin = new Thickness(0, 2, 0, MarkdownStyles.ParagraphSpacing),
                Tag = TableFooterTag
            };

            if (hidden.Count > 0)
            {
                var more = MutedLink(UiText.MoreTableRows(hidden.Count, language));
                more.Tag = MoreRowsTag;
                var separator = new Run("  ·  ");
                more.Click += (s, e) =>
                {
                    foreach (var row in hidden) body.Rows.Add(row);
                    hidden.Clear();
                    footer.Inlines.Remove(more);
                    footer.Inlines.Remove(separator);
                };
                footer.Inlines.Add(more);
                footer.Inlines.Add(separator);
            }

            var copyLabel = UiText.CopyTable(language);
            var copyRun = new Run(copyLabel);
            var copy = MutedLink(null);
            copy.Inlines.Add(copyRun);
            copy.Tag = CopyTag;
            copy.ToolTip = UiText.CopyTableTip(language);
            copy.Click += (s, e) =>
            {
                try
                {
                    Clipboard.SetText(tsv);
                    copyRun.Text = UiText.CopiedTable(language);
                    var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                    timer.Tick += (t, a) => { copyRun.Text = copyLabel; timer.Stop(); };
                    timer.Start();
                }
                catch { }
            };
            footer.Inlines.Add(copy);
            return footer;
        }

        // Tags so tests (and nothing else) can find the footer parts.
        internal const string TableFooterTag = "vm-table-footer";
        internal const string MoreRowsTag = "vm-table-more";
        internal const string CopyTag = "vm-table-copy";

        internal static Hyperlink MutedLink(string text)
        {
            var link = text == null ? new Hyperlink() : new Hyperlink(new Run(text));
            link.Foreground = MarkdownStyles.AccentBlue;
            link.TextDecorations = null;
            link.Cursor = System.Windows.Input.Cursors.Hand;
            return link;
        }

        // --- Plain text of a cell (for numeric detection, widths and TSV) ---------------

        private static List<string> CellTexts(MdTableRow row)
        {
            var texts = new List<string>();
            foreach (var cell in row.OfType<MdTableCell>())
            {
                texts.Add(PlainText(cell));
                for (int i = 1; i < cell.ColumnSpan; i++) texts.Add("");
            }
            return texts;
        }

        internal static string PlainText(ContainerBlock container)
        {
            var sb = new StringBuilder();
            foreach (var block in container)
            {
                if (sb.Length > 0) sb.Append(' ');
                if (block is LeafBlock leaf && leaf.Inline != null) AppendInlines(sb, leaf.Inline);
                else if (block is ContainerBlock inner) sb.Append(PlainText(inner));
            }
            return sb.ToString().Trim();
        }

        private static void AppendInlines(StringBuilder sb, ContainerInline container)
        {
            foreach (var inline in container)
            {
                switch (inline)
                {
                    case LiteralInline lit: sb.Append(lit.Content.ToString()); break;
                    case CodeInline code: sb.Append(code.Content); break;
                    case LineBreakInline _: sb.Append(' '); break;
                    case HtmlEntityInline entity: sb.Append(entity.Transcoded.ToString()); break;
                    case AutolinkInline auto: sb.Append(auto.Url); break;
                    case ContainerInline inner: AppendInlines(sb, inner); break;
                }
            }
        }
    }
}
