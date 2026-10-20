using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VibeModel.Infrastructure;
using VibeModel.Services.Chat;
using VibeModel.Services.Update;

namespace VibeModel.UI
{
    /// <summary>
    /// The slim row under the chat pane's header (plan 19): "VibeModel 1.2.13 beschikbaar (3,4 MB)"
    /// with Bijwerken / Wat is nieuw / Later; the download progress; "klaar - actief na het opnieuw
    /// starten van Revit"; or why the install failed. Shown only while there is something to do or
    /// to know (Available, Installing, ReadyAtRestart, LoaderTooOld) — never for a failed automatic
    /// check, never mid-message (it sits above the scroll area, so messages never shift). Never
    /// restarts Revit. AutomationIds: UpdateBanner, UpdateText, UpdateInstall, UpdateNotes, UpdateLater.
    /// </summary>
    public sealed class UpdateBanner : Border
    {
        private static readonly SolidColorBrush Bg = new SolidColorBrush(Color.FromRgb(0x1F, 0x3A, 0x4D));
        private static readonly SolidColorBrush Fg = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
        private static readonly SolidColorBrush FgMuted = new SolidColorBrush(Color.FromRgb(0xB0, 0xC4, 0xD0));
        private static readonly SolidColorBrush BtnBg = new SolidColorBrush(Color.FromRgb(0x0E, 0x63, 0x9C));
        private static readonly SolidColorBrush Line = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));

        // "Later" / "×" hide it for the rest of this Revit session (until an install starts).
        private static bool _dismissedThisSession;

        private readonly TextBlock _text;
        private readonly Button _install;
        private readonly Button _notes;
        private readonly Button _later;
        private UpdateStatus _shown;

        public UpdateBanner()
        {
            Background = Bg;
            BorderBrush = Line;
            BorderThickness = new Thickness(0, 0, 0, 1);
            Padding = new Thickness(8, 5, 6, 5);
            Visibility = Visibility.Collapsed;
            AutomationProperties.SetAutomationId(this, "UpdateBanner");

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _text = new TextBlock
            {
                Foreground = Fg,
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            AutomationProperties.SetAutomationId(_text, "UpdateText");
            Grid.SetColumn(_text, 0);
            grid.Children.Add(_text);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _install = MakeButton("UpdateInstall", primary: true);
            _install.Click += (s, e) => OnInstall();
            _notes = MakeButton("UpdateNotes", primary: false);
            _notes.Click += (s, e) => OpenNotes();
            _later = MakeButton("UpdateLater", primary: false);
            _later.Click += (s, e) =>
            {
                _dismissedThisSession = true;
                Visibility = Visibility.Collapsed;
            };
            buttons.Children.Add(_install);
            buttons.Children.Add(_notes);
            buttons.Children.Add(_later);
            Grid.SetColumn(buttons, 1);
            grid.Children.Add(buttons);
            Child = grid;

            UpdateService.Changed += OnChanged;
            Render(UpdateService.Status);
        }

        /// <summary>The pane is going away (Revit shutting down).</summary>
        public void Detach() => UpdateService.Changed -= OnChanged;

        private void OnChanged(UpdateStatus status)
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(() => Render(status)));
            }
            catch
            {
                // The pane is gone.
            }
        }

        private void Render(UpdateStatus s)
        {
            _shown = s;
            var language = Strings.Current;
            var state = s?.State ?? UpdateState.NotChecked;
            bool show;
            switch (state)
            {
                case UpdateState.Available:
                case UpdateState.LoaderTooOld:
                    show = !_dismissedThisSession;
                    break;
                case UpdateState.Installing:
                    // An install started anywhere (Settings, `update --install`) brings the banner back.
                    _dismissedThisSession = false;
                    show = true;
                    break;
                case UpdateState.ReadyAtRestart:
                    show = !_dismissedThisSession;
                    break;
                default:
                    show = false;
                    break;
            }
            Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show) return;

            _text.Text = UiText.UpdateLine(s, UpdateService.Engine.Progress, language);
            _text.ToolTip = s.Note;

            _install.Visibility = Visibility.Collapsed;
            _notes.Visibility = s.Latest?.Notes != null ? Visibility.Visible : Visibility.Collapsed;
            _notes.Content = Strings.For(language, Strings.UpdateNotes);
            _later.Visibility = Visibility.Visible;
            _later.Content = Strings.For(language, Strings.UpdateLater);

            switch (state)
            {
                case UpdateState.Available:
                    _install.Visibility = Visibility.Visible;
                    _install.IsEnabled = true;
                    _install.Content = Strings.For(language, s.Reason != null ? Strings.UpdateRetry : Strings.UpdateInstall);
                    _install.ToolTip = Strings.For(language, Strings.UpdateInstallTip);
                    break;
                case UpdateState.LoaderTooOld:
                    // The zip has to be installed once by hand: the button opens the release page.
                    _notes.Content = Strings.For(language, Strings.UpdateDownloadZip);
                    break;
                case UpdateState.Installing:
                    _notes.Visibility = Visibility.Collapsed;
                    _later.Visibility = Visibility.Collapsed;
                    break;
                case UpdateState.ReadyAtRestart:
                    _notes.Visibility = Visibility.Collapsed;
                    _later.Content = "×";
                    break;
            }
        }

        private void OnInstall()
        {
            _install.IsEnabled = false;
            _text.Text = UiText.UpdateLine(new UpdateStatus { State = UpdateState.Installing, Latest = _shown?.Latest }, null, Strings.Current);
            UpdateService.InstallAsync();
        }

        private void OpenNotes()
        {
            var url = _shown?.Latest?.Notes;
            if (url == null || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Logger.Warn("Could not open " + url + ": " + ex.Message);
            }
        }

        private static Button MakeButton(string automationId, bool primary)
        {
            var b = new Button
            {
                Foreground = primary ? Brushes.White : FgMuted,
                Background = primary ? BtnBg : Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                FontSize = 11,
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(3, 0, 0, 0)
            };
            AutomationProperties.SetAutomationId(b, automationId);
            return b;
        }
    }
}
