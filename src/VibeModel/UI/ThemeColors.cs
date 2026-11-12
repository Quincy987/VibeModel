using System;
using System.Collections.Generic;

namespace VibeModel.UI
{
    /// <summary>
    /// Every colour role the VibeModel UI paints with. <see cref="Palette"/> hands out one shared
    /// brush per role; <see cref="ThemeColors"/> holds the value of each role per theme.
    /// </summary>
    public enum ThemeRole
    {
        // Surfaces
        WindowBg,           // pane / dialog background
        PanelBg,            // header, footers, cards, history rows
        InputBg,            // text boxes, starter chips, attachment pills
        RowHover,           // a hovered list row (history, starter chips)
        ChipOutline,        // the idle outline of a starter / quick-reply chip
        Border,             // outlines, separators, table grid

        // Text
        FgPrimary,
        FgSecondary,        // hints, labels, idle icon buttons
        FgMuted,            // the activity block's finished/idle step text
        FgDone,             // the activity block's ✓

        // Accent
        Accent,             // accent TEXT: role label, links, citations, status line
        AccentLine,         // accent decoration: quote bar, hover outline, focus border
        RunningStepBg,      // the activity block's running row (a tint of the accent)

        // Buttons
        ButtonBg,           // primary button (and the selected Settings tab)
        ButtonHover,
        ButtonFg,           // text/icon on a primary button
        ButtonSecondaryBg,
        ButtonSecondaryFg,

        // Messages
        UserMsgBg,
        AssistantMsgBg,
        SystemMsgBg,
        AssistantMsgBorder, // the light theme draws the white answer bubble with an outline

        // Code and tables (markdown)
        CodeBg,
        CodeInlineBg,
        CodeInlineFg,
        TableHeaderBg,
        TableRowAltBg,

        // States
        FgOk,
        FgError,
        FgFail,             // the activity block's ✕
        FgWarning,          // the amber note text
        WarningBg,          // the amber note bar
        DotLong,            // conversation length ● (long)
        DotVeryLong,        // conversation length ● (very long)

        // Update banner
        BannerBg,
        BannerFg,
        BannerMuted,

        // Settings tabs
        TabBg,              // an unselected tab
        TabFg,              // an unselected tab's text

        // Mode chip (plan 20)
        ModeAuto,           // teal
        ModeAsk,            // amber (also the send button in Vragen)
        ModeAnnotate,       // violet
        ModeReadOnly,       // grey
        ChipBg,
        ChipBorder,
        MenuBg,
        MenuBorder,
        MenuRowHover,
    }

    /// <summary>The two looks. <c>Dark</c> is the original VibeModel look; <c>Light</c> follows Revit's own light UI.</summary>
    public enum ThemeKind { Light, Dark }

    /// <summary>What the user (or Revit-beheer) chose: follow Revit, or a fixed theme.</summary>
    public enum ThemeChoice { Revit, Light, Dark }

    /// <summary>
    /// The colour of every <see cref="ThemeRole"/> in one theme, as 0xAARRGGBB. Pure (no WPF), so the
    /// contrast of every text/background pair is checked by unit tests.
    /// </summary>
    public sealed class ThemeColors
    {
        private readonly uint[] _argb;

        public ThemeKind Kind { get; }

        private ThemeColors(ThemeKind kind, IDictionary<ThemeRole, uint> values)
        {
            Kind = kind;
            var roles = (ThemeRole[])Enum.GetValues(typeof(ThemeRole));
            _argb = new uint[roles.Length];
            foreach (var role in roles)
            {
                if (!values.TryGetValue(role, out var v))
                    throw new InvalidOperationException("Theme " + kind + " has no colour for " + role);
                // A plain 0xRRGGBB is opaque.
                _argb[(int)role] = v <= 0xFFFFFF ? 0xFF000000 | v : v;
            }
        }

        /// <summary>The colour as 0xAARRGGBB.</summary>
        public uint this[ThemeRole role] => _argb[(int)role];

        public static ThemeColors For(ThemeKind kind) => kind == ThemeKind.Dark ? Dark : Light;

        /// <summary>
        /// Revit's light UI (Properties / Project Browser): near-white surfaces, light-grey headers,
        /// black text and Autodesk blue. Every text colour reaches WCAG AA (4.5:1) on the surfaces
        /// it sits on — hence the accent TEXT (0067A6) and the button (0A6EB4) are a shade deeper
        /// than Autodesk blue 0696D7, which stays for lines and outlines.
        /// </summary>
        public static readonly ThemeColors Light = new ThemeColors(ThemeKind.Light, new Dictionary<ThemeRole, uint>
        {
            [ThemeRole.WindowBg] = 0xF5F5F5,
            [ThemeRole.PanelBg] = 0xEBEBEB,
            [ThemeRole.InputBg] = 0xFFFFFF,
            [ThemeRole.RowHover] = 0xE5F1FB,
            [ThemeRole.ChipOutline] = 0xC8C8C8,
            [ThemeRole.Border] = 0xC8C8C8,

            [ThemeRole.FgPrimary] = 0x1B1B1B,
            [ThemeRole.FgSecondary] = 0x5F5F5F,
            [ThemeRole.FgMuted] = 0x6B6B6B,
            [ThemeRole.FgDone] = 0x6B6B6B,

            [ThemeRole.Accent] = 0x0067A6,
            [ThemeRole.AccentLine] = 0x0696D7,
            [ThemeRole.RunningStepBg] = 0x220696D7,

            [ThemeRole.ButtonBg] = 0x0A6EB4,
            [ThemeRole.ButtonHover] = 0x085D99,
            [ThemeRole.ButtonFg] = 0xFFFFFF,
            [ThemeRole.ButtonSecondaryBg] = 0xE1E1E1,
            [ThemeRole.ButtonSecondaryFg] = 0x1B1B1B,

            [ThemeRole.UserMsgBg] = 0xDCEBF7,
            [ThemeRole.AssistantMsgBg] = 0xFFFFFF,
            [ThemeRole.SystemMsgBg] = 0xFFF4D6,
            [ThemeRole.AssistantMsgBorder] = 0xD6D6D6,

            [ThemeRole.CodeBg] = 0xF3F3F3,
            [ThemeRole.CodeInlineBg] = 0xEDEDED,
            [ThemeRole.CodeInlineFg] = 0x333333,
            [ThemeRole.TableHeaderBg] = 0xE6E6E6,
            [ThemeRole.TableRowAltBg] = 0xF7F7F7,

            [ThemeRole.FgOk] = 0x276A2B,
            [ThemeRole.FgError] = 0xC62828,
            [ThemeRole.FgFail] = 0xC62828,
            [ThemeRole.FgWarning] = 0x8A5200,
            [ThemeRole.WarningBg] = 0xFFF4D6,
            [ThemeRole.DotLong] = 0xC77700,
            [ThemeRole.DotVeryLong] = 0xC62828,

            [ThemeRole.BannerBg] = 0xE3F1FA,
            [ThemeRole.BannerFg] = 0x1B1B1B,
            [ThemeRole.BannerMuted] = 0x3D5566,

            [ThemeRole.TabBg] = 0xE1E1E1,
            [ThemeRole.TabFg] = 0x1B1B1B,

            [ThemeRole.ModeAuto] = 0x0E7C66,
            [ThemeRole.ModeAsk] = 0xB26A00,
            [ThemeRole.ModeAnnotate] = 0x7B4FC4,
            [ThemeRole.ModeReadOnly] = 0x6E6E78,
            [ThemeRole.ChipBg] = 0xFFFFFF,
            [ThemeRole.ChipBorder] = 0xC8C8C8,
            [ThemeRole.MenuBg] = 0xFFFFFF,
            [ThemeRole.MenuBorder] = 0xBDBDBD,
            [ThemeRole.MenuRowHover] = 0xE5F1FB,
        });

        /// <summary>The original VibeModel look, value for value (it was spread over the UI files).</summary>
        public static readonly ThemeColors Dark = new ThemeColors(ThemeKind.Dark, new Dictionary<ThemeRole, uint>
        {
            [ThemeRole.WindowBg] = 0x1E1E1E,
            [ThemeRole.PanelBg] = 0x252525,
            [ThemeRole.InputBg] = 0x333333,
            [ThemeRole.RowHover] = 0x2D2D30,
            [ThemeRole.ChipOutline] = 0x333333,   // = the chip's own fill: no visible outline
            [ThemeRole.Border] = 0x3F3F46,

            [ThemeRole.FgPrimary] = 0xE0E0E0,
            [ThemeRole.FgSecondary] = 0x999999,
            [ThemeRole.FgMuted] = 0x808080,
            [ThemeRole.FgDone] = 0x8A8A8A,

            [ThemeRole.Accent] = 0x4EC9B0,
            [ThemeRole.AccentLine] = 0x4EC9B0,
            [ThemeRole.RunningStepBg] = 0x224EC9B0,

            [ThemeRole.ButtonBg] = 0x0E639C,
            [ThemeRole.ButtonHover] = 0x1E73AC,
            [ThemeRole.ButtonFg] = 0xFFFFFF,
            [ThemeRole.ButtonSecondaryBg] = 0x3F3F46,
            [ThemeRole.ButtonSecondaryFg] = 0xE0E0E0,

            [ThemeRole.UserMsgBg] = 0x2D4A7A,
            [ThemeRole.AssistantMsgBg] = 0x2D2D30,
            [ThemeRole.SystemMsgBg] = 0x3A3A2D,
            [ThemeRole.AssistantMsgBorder] = 0x2D2D30,   // same as the bubble: no visible outline

            [ThemeRole.CodeBg] = 0x1A1A1A,
            [ThemeRole.CodeInlineBg] = 0x3E3E44,
            [ThemeRole.CodeInlineFg] = 0xD8D8D8,
            [ThemeRole.TableHeaderBg] = 0x3C3C42,
            [ThemeRole.TableRowAltBg] = 0x333337,

            [ThemeRole.FgOk] = 0x4EC9B0,
            [ThemeRole.FgError] = 0xF44336,
            [ThemeRole.FgFail] = 0xE57373,
            [ThemeRole.FgWarning] = 0xFFB74D,
            [ThemeRole.WarningBg] = 0x3A331E,
            [ThemeRole.DotLong] = 0xE09A2E,
            [ThemeRole.DotVeryLong] = 0xE05545,

            [ThemeRole.BannerBg] = 0x1F3A4D,
            [ThemeRole.BannerFg] = 0xE0E0E0,
            [ThemeRole.BannerMuted] = 0xB0C4D0,

            [ThemeRole.TabBg] = 0x2D2D30,
            [ThemeRole.TabFg] = 0xFFFFFF,

            [ThemeRole.ModeAuto] = 0x4EC9B0,
            [ThemeRole.ModeAsk] = 0xE5A53A,
            [ThemeRole.ModeAnnotate] = 0xB18CE8,
            [ThemeRole.ModeReadOnly] = 0x9A9AA5,
            [ThemeRole.ChipBg] = 0x3A3A40,
            [ThemeRole.ChipBorder] = 0x505058,
            [ThemeRole.MenuBg] = 0x2D2D30,
            [ThemeRole.MenuBorder] = 0x55555C,
            [ThemeRole.MenuRowHover] = 0x3A3A40,
        });

        // --- Choice ------------------------------------------------------------------------

        public const string ChoiceRevit = "revit";
        public const string ChoiceLight = "light";
        public const string ChoiceDark = "dark";

        /// <summary>settings.json / managed.json value → choice; anything unknown follows Revit.</summary>
        public static ThemeChoice ParseChoice(string value)
        {
            switch ((value ?? "").Trim().ToLowerInvariant())
            {
                case ChoiceLight: case "licht": case "day": case "dag": return ThemeChoice.Light;
                case ChoiceDark: case "donker": case "night": return ThemeChoice.Dark;
                default: return ThemeChoice.Revit;
            }
        }

        public static string ChoiceName(ThemeChoice choice) =>
            choice == ThemeChoice.Light ? ChoiceLight : choice == ThemeChoice.Dark ? ChoiceDark : ChoiceRevit;

        /// <summary>
        /// The theme to paint: a fixed choice wins; "follow Revit" is dark only when Revit itself
        /// runs its dark UI (Revit 2024+; Revit 2023 and older are always light).
        /// </summary>
        public static ThemeKind Resolve(ThemeChoice choice, bool revitIsDark) =>
            choice == ThemeChoice.Dark ? ThemeKind.Dark :
            choice == ThemeChoice.Light ? ThemeKind.Light :
            revitIsDark ? ThemeKind.Dark : ThemeKind.Light;

        // --- Contrast (WCAG 2.x) ------------------------------------------------------------

        /// <summary>Contrast ratio of two opaque colours (1..21).</summary>
        public static double Contrast(uint a, uint b)
        {
            double la = Luminance(a), lb = Luminance(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }

        /// <summary>A translucent colour laid over an opaque background.</summary>
        public static uint Over(uint top, uint background)
        {
            double alpha = ((top >> 24) & 0xFF) / 255.0;
            uint Mix(int shift) =>
                (uint)Math.Round(((top >> shift) & 0xFF) * alpha + ((background >> shift) & 0xFF) * (1 - alpha));
            return 0xFF000000 | (Mix(16) << 16) | (Mix(8) << 8) | Mix(0);
        }

        private static double Luminance(uint argb)
        {
            double Channel(int shift)
            {
                double c = ((argb >> shift) & 0xFF) / 255.0;
                return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Channel(16) + 0.7152 * Channel(8) + 0.0722 * Channel(0);
        }
    }
}
