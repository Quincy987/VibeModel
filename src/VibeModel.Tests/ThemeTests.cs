using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using VibeModel.Markdown;
using VibeModel.UI;
using Xunit;

namespace VibeModel.Tests
{
    /// <summary>
    /// The light ("like Revit") and dark themes: every role has a colour, every text/background
    /// pair the UI really draws reaches WCAG AA, "follow Revit" resolves as documented, and the
    /// shared palette brushes recolour in place when the theme switches.
    /// </summary>
    public class ThemeColorsTests
    {
        // Text on the surface it sits on (4.5:1, WCAG AA for body text).
        private static readonly (ThemeRole Fg, ThemeRole Bg)[] TextPairs =
        {
            (ThemeRole.FgPrimary, ThemeRole.WindowBg),
            (ThemeRole.FgPrimary, ThemeRole.PanelBg),
            (ThemeRole.FgPrimary, ThemeRole.InputBg),
            (ThemeRole.FgPrimary, ThemeRole.UserMsgBg),
            (ThemeRole.FgPrimary, ThemeRole.AssistantMsgBg),
            (ThemeRole.FgPrimary, ThemeRole.SystemMsgBg),
            (ThemeRole.FgPrimary, ThemeRole.CodeBg),
            (ThemeRole.FgPrimary, ThemeRole.TableHeaderBg),
            (ThemeRole.FgPrimary, ThemeRole.TableRowAltBg),
            (ThemeRole.FgPrimary, ThemeRole.RowHover),
            (ThemeRole.FgPrimary, ThemeRole.MenuBg),
            (ThemeRole.FgPrimary, ThemeRole.MenuRowHover),
            (ThemeRole.FgPrimary, ThemeRole.ChipBg),
            (ThemeRole.FgSecondary, ThemeRole.WindowBg),
            (ThemeRole.FgSecondary, ThemeRole.PanelBg),
            (ThemeRole.FgSecondary, ThemeRole.AssistantMsgBg),
            (ThemeRole.FgSecondary, ThemeRole.UserMsgBg),
            (ThemeRole.FgSecondary, ThemeRole.MenuBg),
            (ThemeRole.FgMuted, ThemeRole.WindowBg),
            (ThemeRole.FgDone, ThemeRole.WindowBg),
            (ThemeRole.FgPrimary, ThemeRole.RunningStepBg),
            (ThemeRole.FgSecondary, ThemeRole.RunningStepBg),
            (ThemeRole.Accent, ThemeRole.WindowBg),
            (ThemeRole.Accent, ThemeRole.PanelBg),
            (ThemeRole.Accent, ThemeRole.AssistantMsgBg),
            (ThemeRole.Accent, ThemeRole.UserMsgBg),
            (ThemeRole.Accent, ThemeRole.SystemMsgBg),
            (ThemeRole.CodeInlineFg, ThemeRole.CodeInlineBg),
            (ThemeRole.ButtonFg, ThemeRole.ButtonBg),
            (ThemeRole.ButtonFg, ThemeRole.ButtonHover),
            (ThemeRole.ButtonSecondaryFg, ThemeRole.ButtonSecondaryBg),
            (ThemeRole.TabFg, ThemeRole.TabBg),
            (ThemeRole.FgOk, ThemeRole.WindowBg),
            (ThemeRole.FgOk, ThemeRole.PanelBg),
            (ThemeRole.FgError, ThemeRole.WindowBg),
            (ThemeRole.FgError, ThemeRole.PanelBg),
            (ThemeRole.FgFail, ThemeRole.WindowBg),
            (ThemeRole.FgWarning, ThemeRole.WarningBg),
            (ThemeRole.BannerFg, ThemeRole.BannerBg),
            (ThemeRole.BannerMuted, ThemeRole.BannerBg),
        };

        // Icons, dots and the mode glyphs: non-text, 3:1 (WCAG 1.4.11).
        private static readonly (ThemeRole Fg, ThemeRole Bg)[] GraphicPairs =
        {
            (ThemeRole.ModeAuto, ThemeRole.ChipBg),
            (ThemeRole.ModeAsk, ThemeRole.ChipBg),
            (ThemeRole.ModeAnnotate, ThemeRole.ChipBg),
            (ThemeRole.ModeReadOnly, ThemeRole.ChipBg),
            (ThemeRole.ModeAuto, ThemeRole.MenuBg),
            (ThemeRole.ModeAsk, ThemeRole.MenuBg),
            (ThemeRole.ModeAnnotate, ThemeRole.MenuBg),
            (ThemeRole.ModeReadOnly, ThemeRole.MenuBg),
            (ThemeRole.DotLong, ThemeRole.WindowBg),
            (ThemeRole.DotVeryLong, ThemeRole.WindowBg),
            (ThemeRole.Accent, ThemeRole.WindowBg),
        };

        // The dark theme is the original VibeModel look, kept value for value; these of its pairs
        // were below 4.5:1 already (still ≥ 3:1) and are not changed by this plan.
        private static readonly HashSet<(ThemeRole, ThemeRole)> DarkAsBefore = new HashSet<(ThemeRole, ThemeRole)>
        {
            (ThemeRole.FgMuted, ThemeRole.WindowBg),         // 4.2
            (ThemeRole.FgSecondary, ThemeRole.UserMsgBg),    // 3.1
            (ThemeRole.Accent, ThemeRole.UserMsgBg),         // 4.3
            (ThemeRole.FgError, ThemeRole.PanelBg),          // 4.2
        };

        // A translucent surface (the running step's tint) is seen over the window background.
        private static double Ratio(ThemeColors t, ThemeRole fg, ThemeRole bg)
        {
            var surface = (t[bg] >> 24) == 0xFF ? t[bg] : ThemeColors.Over(t[bg], t[ThemeRole.WindowBg]);
            return ThemeColors.Contrast(ThemeColors.Over(t[fg], surface), surface);
        }

        [Fact]
        public void LightTheme_EveryTextPair_ReachesAA()
        {
            var failures = TextPairs
                .Select(p => (p, r: Ratio(ThemeColors.Light, p.Fg, p.Bg)))
                .Where(x => x.r < 4.5)
                .Select(x => x.p.Fg + " on " + x.p.Bg + ": " + x.r.ToString("0.00"))
                .ToList();
            Assert.True(failures.Count == 0, string.Join("; ", failures));
        }

        [Fact]
        public void DarkTheme_EveryTextPair_ReachesAA_ExceptTheOriginalLook()
        {
            var failures = new List<string>();
            foreach (var p in TextPairs)
            {
                var r = Ratio(ThemeColors.Dark, p.Fg, p.Bg);
                var min = DarkAsBefore.Contains((p.Fg, p.Bg)) ? 3.0 : 4.5;
                if (r < min) failures.Add(p.Fg + " on " + p.Bg + ": " + r.ToString("0.00"));
            }
            Assert.True(failures.Count == 0, string.Join("; ", failures));
        }

        [Theory]
        [InlineData(ThemeKind.Light)]
        [InlineData(ThemeKind.Dark)]
        public void BothThemes_IconsAndDots_ReachThreeToOne(ThemeKind kind)
        {
            var t = ThemeColors.For(kind);
            var failures = GraphicPairs.Where(p => Ratio(t, p.Fg, p.Bg) < 3.0)
                .Select(p => p.Fg + " on " + p.Bg).ToList();
            Assert.True(failures.Count == 0, string.Join("; ", failures));
        }

        [Fact]
        public void LightTheme_SendArrowOnAmber_StaysReadable()
        {
            // The send button turns amber in Vragen; its white arrow must still show.
            Assert.True(Ratio(ThemeColors.Light, ThemeRole.ButtonFg, ThemeRole.ModeAsk) >= 3.0);
        }

        [Fact]
        public void LightTheme_LooksLikeRevit_DarkKeepsTheOriginalValues()
        {
            Assert.Equal(0xFFF5F5F5u, ThemeColors.Light[ThemeRole.WindowBg]);
            Assert.Equal(0xFFFFFFFFu, ThemeColors.Light[ThemeRole.InputBg]);
            Assert.Equal(0xFF0696D7u, ThemeColors.Light[ThemeRole.AccentLine]);  // Autodesk blue
            Assert.Equal(0xFF1E1E1Eu, ThemeColors.Dark[ThemeRole.WindowBg]);
            Assert.Equal(0xFFE0E0E0u, ThemeColors.Dark[ThemeRole.FgPrimary]);
            Assert.Equal(0xFF4EC9B0u, ThemeColors.Dark[ThemeRole.Accent]);
            Assert.Equal(0xFF0E639Cu, ThemeColors.Dark[ThemeRole.ButtonBg]);
            Assert.Equal(0x224EC9B0u, ThemeColors.Dark[ThemeRole.RunningStepBg]);
        }

        [Fact]
        public void EveryRole_HasAColourInBothThemes()
        {
            foreach (ThemeRole role in Enum.GetValues(typeof(ThemeRole)))
            {
                Assert.NotEqual(0u, ThemeColors.Light[role] >> 24);
                Assert.NotEqual(0u, ThemeColors.Dark[role] >> 24);
            }
        }

        [Theory]
        [InlineData(ThemeChoice.Revit, false, ThemeKind.Light)]   // Revit 2023, or Revit 2024+ in light
        [InlineData(ThemeChoice.Revit, true, ThemeKind.Dark)]     // Revit 2024+ in its dark theme
        [InlineData(ThemeChoice.Light, true, ThemeKind.Light)]
        [InlineData(ThemeChoice.Dark, false, ThemeKind.Dark)]
        public void Resolve_FollowsRevitOnlyWhenAskedTo(ThemeChoice choice, bool revitDark, ThemeKind expected) =>
            Assert.Equal(expected, ThemeColors.Resolve(choice, revitDark));

        [Theory]
        [InlineData(null, ThemeChoice.Revit)]
        [InlineData("", ThemeChoice.Revit)]
        [InlineData("revit", ThemeChoice.Revit)]
        [InlineData("whatever", ThemeChoice.Revit)]
        [InlineData("light", ThemeChoice.Light)]
        [InlineData(" Licht ", ThemeChoice.Light)]
        [InlineData("dark", ThemeChoice.Dark)]
        [InlineData("DONKER", ThemeChoice.Dark)]
        public void ParseChoice_DefaultsToFollowingRevit(string value, ThemeChoice expected) =>
            Assert.Equal(expected, ThemeColors.ParseChoice(value));

        [Fact]
        public void ChoiceName_RoundTrips()
        {
            foreach (ThemeChoice c in Enum.GetValues(typeof(ThemeChoice)))
                Assert.Equal(c, ThemeColors.ParseChoice(ThemeColors.ChoiceName(c)));
        }
    }

    /// <summary>The shared brushes: one per role per UI thread, recoloured in place on a switch.</summary>
    [Collection("Theme")]
    public class PaletteLiveSwitchTests
    {
        private static Color ColorOf(ThemeKind kind, ThemeRole role) => Palette.ColorOf(ThemeColors.For(kind)[role]);

        [Fact]
        public void ThemeSwitch_RecoloursTheSameBrush_SoOpenWindowsRepaint()
        {
            var before = ThemeManager.Choice;
            try
            {
                MarkdownTableRenderingTests.Sta(() =>
                {
                    ThemeManager.SetChoice(ThemeChoice.Light);
                    var bg = Palette.BgWindow;
                    var text = MarkdownStyles.FgPrimary;   // the markdown renderer shares the palette
                    Assert.Same(Palette.FgPrimary, text);
                    Assert.False(bg.IsFrozen);
                    Assert.Equal(ColorOf(ThemeKind.Light, ThemeRole.WindowBg), bg.Color);

                    ThemeManager.SetChoice(ThemeChoice.Dark);
                    Assert.Same(bg, Palette.BgWindow);
                    Assert.Equal(ColorOf(ThemeKind.Dark, ThemeRole.WindowBg), bg.Color);
                    Assert.Equal(ColorOf(ThemeKind.Dark, ThemeRole.FgPrimary), text.Color);

                    ThemeManager.SetChoice(ThemeChoice.Light);
                    Assert.Equal(ColorOf(ThemeKind.Light, ThemeRole.WindowBg), bg.Color);
                });
            }
            finally { ThemeManager.SetChoice(before); }
        }

        [Fact]
        public void EachUiThread_GetsItsOwnBrushes_InTheCurrentTheme()
        {
            var before = ThemeManager.Choice;
            try
            {
                SolidColorBrush first = null, second = null;
                MarkdownTableRenderingTests.Sta(() => { ThemeManager.SetChoice(ThemeChoice.Dark); first = Palette.Accent; });
                MarkdownTableRenderingTests.Sta(() =>
                {
                    second = Palette.Accent;
                    Assert.Equal(ColorOf(ThemeKind.Dark, ThemeRole.Accent), second.Color);
                });
                Assert.NotSame(first, second);
            }
            finally { ThemeManager.SetChoice(before); }
        }
    }
}
