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
        public static readonly SolidColorBrush AccentBlue = Freeze(new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0)));
        public static readonly SolidColorBrush BorderColor = Freeze(new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46)));
        public static readonly SolidColorBrush QuoteBorder = Freeze(new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0)));

        public static readonly FontFamily MonoFont = new FontFamily("Consolas");

        // Font sizes
        public const double FontSizeNormal = 13;
        public const double FontSizeCode = 12;
        public const double FontSizeH1 = 20;
        public const double FontSizeH2 = 17;
        public const double FontSizeH3 = 15;

        private static SolidColorBrush Freeze(SolidColorBrush brush)
        {
            brush.Freeze();
            return brush;
        }
    }
}
