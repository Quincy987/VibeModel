using System;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using VibeModel.Infrastructure;
using VibeModel.Services.Chat;
using VibeModel.Services.Diagnostics;
using static VibeModel.UI.Palette;

namespace VibeModel.UI
{
    /// <summary>
    /// The Diagnose dialog: the <see cref="DiagnoseReport"/> text (built off the UI thread — its
    /// network checks take seconds), with Copy and Save, and the sentence that says where to send it.
    /// </summary>
    public sealed class DiagnoseWindow : Window
    {
        private readonly TextBox _report;
        private readonly TextBlock _status;
        private readonly Button _copy;
        private readonly Button _save;

        public static void ShowFor(FrameworkElement owner)
        {
            var window = new DiagnoseWindow();
            var parent = owner == null ? null : GetWindow(owner);
            if (parent != null) window.Owner = parent;
            window.Show();
        }

        private DiagnoseWindow()
        {
            Title = Strings.Get(Strings.DiagnoseTitle);
            Width = 760;
            Height = 620;
            Background = BgDark;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            AutomationProperties.SetAutomationId(this, "DiagnoseWindow");

            var root = new DockPanel { Margin = new Thickness(10) };

            var hint = new TextBlock
            {
                Text = Strings.Get(Strings.DiagnoseSendHint),
                Foreground = FgPrimary,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            DockPanel.SetDock(hint, Dock.Top);
            root.Children.Add(hint);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            _copy = MakeButton(Strings.Get(Strings.DiagnoseCopy), "DiagnoseCopy", primary: true);
            _copy.Click += (s, e) =>
            {
                try
                {
                    Clipboard.SetText(_report.Text);
                    _status.Text = Strings.Get(Strings.DiagnoseCopied);
                }
                catch (Exception ex)
                {
                    _status.Text = ex.Message;
                }
            };
            _save = MakeButton(Strings.Get(Strings.DiagnoseSave), "DiagnoseSave", primary: false);
            _save.Click += (s, e) =>
            {
                try
                {
                    var path = DiagnoseReport.Save(_report.Text);
                    _status.Text = Strings.Get(Strings.DiagnoseSaved, path);
                    _status.ToolTip = path;
                }
                catch (Exception ex)
                {
                    _status.Text = ex.Message;
                }
            };
            var close = MakeButton(Strings.Get(Strings.DiagnoseClose), "DiagnoseClose", primary: false);
            close.Click += (s, e) => Close();
            _copy.IsEnabled = _save.IsEnabled = false;
            buttons.Children.Add(_copy);
            buttons.Children.Add(_save);
            buttons.Children.Add(close);
            _status = new TextBlock { Foreground = FgSecondary, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            AutomationProperties.SetAutomationId(_status, "DiagnoseStatus");
            buttons.Children.Add(_status);
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);

            _report = new TextBox
            {
                Text = Strings.Get(Strings.DiagnoseRunning),
                IsReadOnly = true,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                Background = BgPanel,
                Foreground = FgPrimary,
                BorderThickness = new Thickness(0),
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(6)
            };
            AutomationProperties.SetAutomationId(_report, "DiagnoseReport");
            root.Children.Add(_report);
            Content = root;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                string text;
                try { text = DiagnoseReport.Build(); }
                catch (Exception ex)
                {
                    Logger.Error("Diagnose report failed", ex);
                    text = "Diagnose failed: " + ex;
                }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _report.Text = text;
                    _copy.IsEnabled = _save.IsEnabled = true;
                }));
            });
        }

        private static Button MakeButton(string text, string automationId, bool primary) =>
            FlatButton(text, automationId, primary, new Thickness(12, 4, 12, 4), 12);
    }
}
