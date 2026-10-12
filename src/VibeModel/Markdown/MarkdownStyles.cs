using System.Windows;
using System.Windows.Media;

namespace VibeModel.Markdown
{
    /// <summary>
    /// Programmatic dark-theme styles for markdown rendering.
    /// Replaces the XAML resource dictionary approach from Markdig.Wpf.
    /// </summary>
    public static class MarkdownStyles
    {
        // Colors matching ChatPane dark theme
        public static readonly SolidColorBrush FgPrimary = Freeze(new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)));
        public static readonly SolidColorBrush FgSecondary = Freeze(new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)));
        public static readonly SolidColorBrush BgCode = Freeze(new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)));
        // Inline code sits on the assistant bubble (#2D2D30): a slightly lighter pill reads as code
        // without the hard black block of a code fence.
        public static readonly SolidColorBrush BgCodeInline = Freeze(new SolidColorBrush(Color.FromRgb(0x3E, 0x3E, 0x44)));
        public static readonly SolidColorBrush FgCodeInline = Freeze(new SolidColorBrush(Color.FromRgb(0xD8, 0xD8, 0xD8)));
        public static readonly SolidColorBrush AccentBlue = Freeze(new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0)));
        public static readonly SolidColorBrush BorderColor = Freeze(new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46)));
        public static readonly SolidColorBrush QuoteBorder = Freeze(new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0)));

        public static readonly FontFamily MonoFont = new FontFamily("Consolas");

        // Font sizes — headings stay modest: the pane is narrow and answers are short.
        public const double FontSizeNormal = 13;
        public const double FontSizeCode = 12;
        public const double FontSizeH1 = 16;
        public const double FontSizeH2 = 15;
        public const double FontSizeH3 = 14;

        // Comfortable reading rhythm for body text (≈1.5× the font size).
        public const double LineHeight = 19.5;
        public const double ParagraphSpacing = 8;

        // A code fence longer than this shows its first CodeBlockShownLines lines only; the
        // message's Copy button still copies everything.
        public const int CodeBlockMaxLines = 20;
        public const int CodeBlockShownLines = 12;

        private static SolidColorBrush Freeze(SolidColorBrush brush)
        {
            brush.Freeze();
            return brush;
        }
    }
}
