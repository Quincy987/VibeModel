using System;
using VibeModel.Infrastructure;

namespace VibeModel.UI
{
    /// <summary>
    /// Which theme the UI paints right now. Inputs: the Settings / managed.json choice
    /// (<see cref="SettingsManager.GetTheme"/>) and, on Revit 2024+, Revit's own UI theme
    /// (<see cref="SetRevitIsDark"/>, fed from <c>UIThemeManager.CurrentTheme</c> at startup and on
    /// <c>ThemeChanged</c>). A change bumps <see cref="Version"/> and raises <see cref="Changed"/>;
    /// <see cref="Palette"/> then recolours its shared brushes, so open windows repaint in place.
    /// </summary>
    public static class ThemeManager
    {
        private static readonly object Lock = new object();
        private static volatile bool _loaded;
        private static ThemeChoice _choice = ThemeChoice.Revit;
        private static bool _revitIsDark;
        private static ThemeKind _current = ThemeKind.Light;
        private static volatile int _version = 1;

        /// <summary>Raised (on the thread that made the change) after the painted theme changed.</summary>
        public static event Action Changed;

        /// <summary>Bumped on every change of <see cref="Current"/>; brush sets compare against it.</summary>
        public static int Version { get { EnsureLoaded(); return _version; } }

        public static ThemeChoice Choice { get { EnsureLoaded(); return _choice; } }

        public static ThemeKind Current { get { EnsureLoaded(); return _current; } }

        public static ThemeColors Colors => ThemeColors.For(Current);

        /// <summary>Re-read the choice from settings / managed.json (Settings → Save, startup).</summary>
        public static void Refresh()
        {
            ThemeChoice choice;
            try { choice = SettingsManager.GetTheme(); }
            catch (Exception ex) { Logger.Warn("Theme: settings unreadable, following Revit: " + ex.Message); choice = ThemeChoice.Revit; }
            Update(choice, null);
        }

        /// <summary>Set the choice directly (unit tests; the app goes through settings + <see cref="Refresh"/>).</summary>
        internal static void SetChoice(ThemeChoice choice)
        {
            EnsureLoaded();
            Update(choice, null);
        }

        /// <summary>Revit's own UI theme (Revit 2024+): dark or not.</summary>
        public static void SetRevitIsDark(bool dark)
        {
            EnsureLoaded();
            Update(null, dark);
        }

        /// <summary>One line for Diagnose: <c>revit (follows Revit: light)</c>.</summary>
        public static string Describe()
        {
            EnsureLoaded();
            lock (Lock)
            {
                var name = ThemeColors.ChoiceName(_choice);
                return _choice == ThemeChoice.Revit
                    ? name + " (follows Revit: " + (_current == ThemeKind.Dark ? "dark" : "light") + ")"
                    : name;
            }
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (Lock) { if (_loaded) return; }
            Refresh();
        }

        private static void Update(ThemeChoice? choice, bool? revitIsDark)
        {
            bool changed;
            lock (Lock)
            {
                if (choice.HasValue) _choice = choice.Value;
                if (revitIsDark.HasValue) _revitIsDark = revitIsDark.Value;
                var kind = ThemeColors.Resolve(_choice, _revitIsDark);
                changed = kind != _current;
                _current = kind;
                if (changed) _version++;
                if (!_loaded)
                {
                    // The first read sets the theme; nothing was painted yet, so no event.
                    _loaded = true;
                    changed = false;
                }
            }
            if (!changed) return;
            Logger.Info("Theme: now " + Describe());
            try { Changed?.Invoke(); }
            catch (Exception ex) { Logger.Warn("Theme change handler failed: " + ex.Message); }
        }
    }
}
