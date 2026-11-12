using System.Windows;
using System.Windows.Media;
using VibeModel.UI;

namespace VibeModel.Markdown
{
    /// <summary>
    /// Programmatic styles for markdown rendering (light or dark, see <see cref="ThemeManager"/>).
    /// Replaces the XAML resource dictionary approach from Markdig.Wpf.
    /// </summary>
    public static class MarkdownStyles
    {
        // Colours come from the shared theme palette (UI.Palette): one brush per role per UI
        // thread, recoloured in place when the theme switches — so a rendered answer follows too.
        public static SolidColorBrush FgPrimary => Palette.FgPrimary;
        public static SolidColorBrush FgSecondary => Palette.FgSecondary;
        public static SolidColorBrush BgCode => Palette.BgCode;
        // Inline code sits on the assistant bubble: a slightly different pill reads as code
        // without the hard block of a code fence.
        public static SolidColorBrush BgCodeInline => Palette.BgCodeInline;
        public static SolidColorBrush FgCodeInline => Palette.FgCodeInline;
        public static SolidColorBrush AccentBlue => Palette.Accent;
        public static SolidColorBrush BorderColor => Palette.BorderColor;
        // Tables: a subtle header fill and row tint on the assistant bubble.
        public static SolidColorBrush BgTableHeader => Palette.BgTableHeader;
        public static SolidColorBrush BgTableRowAlt => Palette.BgTableRowAlt;
        public static SolidColorBrush QuoteBorder => Palette.AccentLine;

        public static readonly FontFamily MonoFont = new FontFamily("Consolas");

        // Font sizes — headings stay modest: the pane is narrow and answers are short.
        public const double FontSizeNormal = 13;
        public const double FontSizeCode = 12;
        public const double FontSizeH1 = 16;
        public const double FontSizeH2 = 15;
        public const double FontSizeH3 = 14;
        public const double FontSizeTable = 12;
        public const double FontSizeSmall = 11;
        // Source markers (¹ ²) and the "Bronnen" footer.
        public const double FontSizeCitation = 11;

        // Comfortable reading rhythm for body text (≈1.5× the font size).
        public const double LineHeight = 19.5;
        public const double ParagraphSpacing = 8;
        public const double LineHeightTable = 17;

        // A code fence longer than this shows its first CodeBlockShownLines lines only; the
        // message's Copy button still copies everything.
        public const int CodeBlockMaxLines = 20;
        public const int CodeBlockShownLines = 12;
    }
}
