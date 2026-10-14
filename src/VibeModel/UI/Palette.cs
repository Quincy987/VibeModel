using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace VibeModel.UI
{
    /// <summary>
    /// The dark theme's colours and the flat button, shared by the connect wizard and the Diagnose
    /// window (the chat pane and Settings still carry their own copies of the same values).
    /// </summary>
    internal static class Palette
    {
        public static readonly SolidColorBrush BgDark = Frozen(0x1E, 0x1E, 0x1E);
        public static readonly SolidColorBrush BgPanel = Frozen(0x25, 0x25, 0x25);
        public static readonly SolidColorBrush BgInput = Frozen(0x33, 0x33, 0x33);
        public static readonly SolidColorBrush FgPrimary = Frozen(0xE0, 0xE0, 0xE0);
        public static readonly SolidColorBrush FgSecondary = Frozen(0x99, 0x99, 0x99);
        public static readonly SolidColorBrush FgOk = Frozen(0x4E, 0xC9, 0xB0);
        public static readonly SolidColorBrush FgError = Frozen(0xF4, 0x43, 0x36);
        public static readonly SolidColorBrush BgButton = Frozen(0x0E, 0x63, 0x9C);
        public static readonly SolidColorBrush BgButtonSecondary = Frozen(0x3F, 0x3F, 0x46);
        public static readonly SolidColorBrush Outline = Frozen(0x3F, 0x3F, 0x46);

        /// <summary>A flat button with an AutomationId (UI-automation tests find it by that id).</summary>
        public static Button FlatButton(string text, string automationId, bool primary, Thickness padding, double fontSize)
        {
            var button = new Button
            {
                Content = text,
                Foreground = FgPrimary,
                Background = primary ? BgButton : BgButtonSecondary,
                BorderThickness = new Thickness(0),
                Padding = padding,
                Margin = new Thickness(0, 0, 6, 4),
                FontSize = fontSize,
                Cursor = Cursors.Hand
            };
            AutomationProperties.SetAutomationId(button, automationId);
            return button;
        }

        // Frozen: shareable across windows and threads, and cheaper to render.
        private static SolidColorBrush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}
