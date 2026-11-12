using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace VibeModel.UI
{
    /// <summary>
    /// The one source of colour for every VibeModel window, the chat pane and the markdown renderer.
    /// Each <see cref="ThemeRole"/> is ONE shared <see cref="SolidColorBrush"/>, deliberately not frozen:
    /// when <see cref="ThemeManager"/> switches theme the brush's <c>Color</c> is set, and every element
    /// holding it repaints in place — no rebuild, the conversation stays.
    /// <para>
    /// An unfrozen brush belongs to the thread that made it, so the set is per UI thread
    /// (<c>[ThreadStatic]</c>): in Revit that is the one main thread; unit tests render on a fresh
    /// STA thread each and get their own. A theme change recolours every live set on its own
    /// dispatcher. Never hand a palette brush to another thread (worker code marshals back first).
    /// </para>
    /// </summary>
    internal static class Palette
    {
        private sealed class BrushSet
        {
            public readonly SolidColorBrush[] Brushes;
            public int Version;
            public Dispatcher Dispatcher => Brushes[0].Dispatcher;

            public BrushSet()
            {
                var count = Enum.GetValues(typeof(ThemeRole)).Length;
                Brushes = new SolidColorBrush[count];
                for (int i = 0; i < count; i++) Brushes[i] = new SolidColorBrush();
            }
        }

        [ThreadStatic] private static BrushSet _local;
        private static readonly object Lock = new object();
        private static readonly List<WeakReference<BrushSet>> Sets = new List<WeakReference<BrushSet>>();

        static Palette()
        {
            ThemeManager.Changed += OnThemeChanged;
        }

        /// <summary>This thread's brush for <paramref name="role"/>, in the current theme.</summary>
        public static SolidColorBrush Get(ThemeRole role)
        {
            var set = _local;
            if (set == null)
            {
                set = _local = new BrushSet();
                lock (Lock)
                {
                    Sets.RemoveAll(IsGone);
                    Sets.Add(new WeakReference<BrushSet>(set));
                }
            }
            if (set.Version != ThemeManager.Version) Apply(set);
            return set.Brushes[(int)role];
        }

        public static Color ColorOf(uint argb) =>
            Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

        private static void Apply(BrushSet set)
        {
            var version = ThemeManager.Version;
            var colors = ThemeManager.Colors;
            for (int i = 0; i < set.Brushes.Length; i++)
                set.Brushes[i].Color = ColorOf(colors[(ThemeRole)i]);
            set.Version = version;
        }

        private static void OnThemeChanged()
        {
            var live = new List<BrushSet>();
            lock (Lock)
            {
                Sets.RemoveAll(IsGone);
                foreach (var w in Sets)
                    if (w.TryGetTarget(out var s)) live.Add(s);
            }
            foreach (var set in live)
            {
                var dispatcher = set.Dispatcher;
                if (dispatcher.CheckAccess()) Apply(set);
                else if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(new Action(() => Apply(set)));
            }
        }

        // A set whose brushes were collected, or whose UI thread has ended (a finished test thread):
        // nothing can show its brushes any more, and a queued recolour would never run.
        private static bool IsGone(WeakReference<BrushSet> w) =>
            !w.TryGetTarget(out var s) || !s.Dispatcher.Thread.IsAlive;

        // --- Roles ---------------------------------------------------------------------------

        public static SolidColorBrush BgWindow => Get(ThemeRole.WindowBg);
        public static SolidColorBrush BgPanel => Get(ThemeRole.PanelBg);
        public static SolidColorBrush BgInput => Get(ThemeRole.InputBg);
        public static SolidColorBrush RowHover => Get(ThemeRole.RowHover);
        public static SolidColorBrush ChipOutline => Get(ThemeRole.ChipOutline);
        public static SolidColorBrush BorderColor => Get(ThemeRole.Border);

        public static SolidColorBrush FgPrimary => Get(ThemeRole.FgPrimary);
        public static SolidColorBrush FgSecondary => Get(ThemeRole.FgSecondary);
        public static SolidColorBrush FgMuted => Get(ThemeRole.FgMuted);
        public static SolidColorBrush FgDone => Get(ThemeRole.FgDone);

        public static SolidColorBrush Accent => Get(ThemeRole.Accent);
        public static SolidColorBrush AccentLine => Get(ThemeRole.AccentLine);
        public static SolidColorBrush RunningStepBg => Get(ThemeRole.RunningStepBg);

        public static SolidColorBrush BgButton => Get(ThemeRole.ButtonBg);
        public static SolidColorBrush BgButtonHover => Get(ThemeRole.ButtonHover);
        public static SolidColorBrush FgButton => Get(ThemeRole.ButtonFg);
        public static SolidColorBrush BgButtonSecondary => Get(ThemeRole.ButtonSecondaryBg);
        public static SolidColorBrush FgButtonSecondary => Get(ThemeRole.ButtonSecondaryFg);

        public static SolidColorBrush BgUserMsg => Get(ThemeRole.UserMsgBg);
        public static SolidColorBrush BgAssistantMsg => Get(ThemeRole.AssistantMsgBg);
        public static SolidColorBrush BgSystemMsg => Get(ThemeRole.SystemMsgBg);
        public static SolidColorBrush AssistantMsgBorder => Get(ThemeRole.AssistantMsgBorder);

        public static SolidColorBrush BgCode => Get(ThemeRole.CodeBg);
        public static SolidColorBrush BgCodeInline => Get(ThemeRole.CodeInlineBg);
        public static SolidColorBrush FgCodeInline => Get(ThemeRole.CodeInlineFg);
        public static SolidColorBrush BgTableHeader => Get(ThemeRole.TableHeaderBg);
        public static SolidColorBrush BgTableRowAlt => Get(ThemeRole.TableRowAltBg);

        public static SolidColorBrush FgOk => Get(ThemeRole.FgOk);
        public static SolidColorBrush FgError => Get(ThemeRole.FgError);
        public static SolidColorBrush FgFail => Get(ThemeRole.FgFail);
        public static SolidColorBrush FgWarning => Get(ThemeRole.FgWarning);
        public static SolidColorBrush BgWarning => Get(ThemeRole.WarningBg);
        public static SolidColorBrush DotLong => Get(ThemeRole.DotLong);
        public static SolidColorBrush DotVeryLong => Get(ThemeRole.DotVeryLong);

        public static SolidColorBrush BannerBg => Get(ThemeRole.BannerBg);
        public static SolidColorBrush BannerFg => Get(ThemeRole.BannerFg);
        public static SolidColorBrush BannerMuted => Get(ThemeRole.BannerMuted);

        public static SolidColorBrush BgTab => Get(ThemeRole.TabBg);

        /// <summary>A flat button with an AutomationId (UI-automation tests find it by that id).</summary>
        public static Button FlatButton(string text, string automationId, bool primary, Thickness padding, double fontSize)
        {
            var button = new Button
            {
                Content = text,
                Foreground = primary ? FgButton : FgButtonSecondary,
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
    }
}
