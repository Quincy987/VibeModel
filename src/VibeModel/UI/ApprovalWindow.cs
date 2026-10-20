using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using VibeModel.Services.Chat;
using VibeModel.Services.Claude;
using static VibeModel.UI.Palette;

namespace VibeModel.UI
{
    /// <summary>What the approval window shows (plan 15).</summary>
    internal sealed class ApprovalRequest
    {
        /// <summary>The chat question that led to the change; null outside a chat turn.</summary>
        public string Question { get; set; }
        /// <summary>The command or batch as sent ("delete 1730959 1730980").</summary>
        public string CommandText { get; set; }
        public ChangeSetSummary Summary { get; set; }
        /// <summary>The run's "-- revit …" and "-- annotations …" lines.</summary>
        public IList<string> ContextLines { get; set; }
        /// <summary>Elements isolated (and selected) in the active view.</summary>
        public int IsolatedCount { get; set; }
        /// <summary>Added + modified elements that still exist (what Zoom shows).</summary>
        public int ShowableCount { get; set; }
        /// <summary>The command reported an error yet changed something (an exec that failed half-way).</summary>
        public bool CommandFailed { get; set; }
        /// <summary>Zoom to the changed elements (null when there are none to show).</summary>
        public Action Zoom { get; set; }
    }

    internal sealed class ApprovalAnswer
    {
        public ApprovalDecision Decision { get; set; } = ApprovalDecision.Discarded;
        public bool DontAskAgain { get; set; }
    }

    /// <summary>
    /// "VibeModel wil deze wijziging behouden": the change set of a delete/edit run with Keep and
    /// Discard. Modal, owned by Revit's main window (Revit's UI is disabled while it is up, so the
    /// user cannot edit while the run's TransactionGroup is open). It is a WPF window, not a Revit
    /// dialog, so VibeModel's DialogBoxShowing handler never sees it. Keep = Enter, Discard = Esc
    /// and closing the window. AutomationIds: ApprovalWindow, ApprovalSummary, ApprovalZoom,
    /// ApprovalDontAsk, ApprovalDiscard, ApprovalKeep.
    /// </summary>
    internal sealed class ApprovalWindow : Window
    {
        private readonly ApprovalAnswer _answer = new ApprovalAnswer();
        private readonly CheckBox _dontAsk;

        /// <summary>Shows the window modally over Revit (restored first when minimized) and returns the answer.</summary>
        public static ApprovalAnswer Ask(IntPtr revitWindow, ApprovalRequest request)
        {
            var window = new ApprovalWindow(request);
            if (revitWindow != IntPtr.Zero)
            {
                // An owned window is hidden while its owner is minimized: bring Revit back first
                // (the user has to see the isolated change anyway).
                if (IsIconic(revitWindow)) ShowWindow(revitWindow, SwRestore);
                new WindowInteropHelper(window).Owner = revitWindow;
            }
            window.ShowDialog();
            return window._answer;
        }

        private ApprovalWindow(ApprovalRequest request)
        {
            Title = "VibeModel";
            Width = 480;
            SizeToContent = SizeToContent.Height;
            MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 80);
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Background = BgDark;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            // In front once, even when another application has the focus; then a normal window.
            Topmost = true;
            ContentRendered += (s, e) => { Topmost = false; Activate(); };
            AutomationProperties.SetAutomationId(this, "ApprovalWindow");

            var root = new StackPanel { Margin = new Thickness(16, 14, 16, 12) };

            root.Children.Add(new TextBlock
            {
                Text = Strings.Get(Strings.ApprovalTitle),
                Foreground = FgPrimary,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            });

            if (!string.IsNullOrWhiteSpace(request.Question))
                root.Children.Add(Line(request.Question.Trim(), FgPrimary, 13, italic: true));
            if (!string.IsNullOrWhiteSpace(request.CommandText))
            {
                var command = Line(Shorten(request.CommandText.Trim(), 300), FgSecondary, 12);
                command.FontFamily = new System.Windows.Media.FontFamily("Consolas");
                root.Children.Add(command);
            }

            var summary = new TextBlock
            {
                Text = SummaryText(request.Summary),
                Foreground = FgPrimary,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 2)
            };
            AutomationProperties.SetAutomationId(summary, "ApprovalSummary");
            root.Children.Add(summary);
            if (request.Summary != null && request.Summary.Modified.Count > 0)
                root.Children.Add(Line(Strings.Get(Strings.ApprovalModifiedNote), FgSecondary, 11));
            if (request.CommandFailed)
                root.Children.Add(Line(Strings.Get(Strings.ApprovalCommandFailed), FgError, 12));

            foreach (var line in request.ContextLines ?? new List<string>())
                if (!string.IsNullOrWhiteSpace(line))
                    root.Children.Add(Line(Shorten(line.Trim(), 400), FgSecondary, 11));

            // What the view shows, and Zoom.
            var viewRow = new DockPanel { Margin = new Thickness(0, 8, 0, 4), LastChildFill = true };
            if (request.Zoom != null)
            {
                var zoom = FlatButton(Strings.Get(Strings.ApprovalZoom), "ApprovalZoom", false, new Thickness(10, 3, 10, 3), 12);
                zoom.Margin = new Thickness(8, 0, 0, 0);
                zoom.Click += (s, e) => request.Zoom();
                DockPanel.SetDock(zoom, Dock.Right);
                viewRow.Children.Add(zoom);
            }
            viewRow.Children.Add(new TextBlock
            {
                Text = ViewText(request),
                Foreground = FgSecondary,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            });
            root.Children.Add(viewRow);

            _dontAsk = new CheckBox
            {
                Content = Strings.Get(Strings.ApprovalDontAsk),
                Foreground = FgSecondary,
                FontSize = 12,
                Margin = new Thickness(0, 6, 0, 10)
            };
            AutomationProperties.SetAutomationId(_dontAsk, "ApprovalDontAsk");
            root.Children.Add(_dontAsk);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var discard = FlatButton(Strings.Get(Strings.ApprovalDiscard), "ApprovalDiscard", false, new Thickness(14, 5, 14, 5), 13);
            discard.IsCancel = true;
            discard.Click += (s, e) => Finish(ApprovalDecision.Discarded);
            var keep = FlatButton(Strings.Get(Strings.ApprovalKeep), "ApprovalKeep", true, new Thickness(14, 5, 14, 5), 13);
            keep.IsDefault = true;
            keep.Click += (s, e) => Finish(ApprovalDecision.Kept);
            buttons.Children.Add(discard);
            buttons.Children.Add(keep);
            root.Children.Add(buttons);

            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root };
            Loaded += (s, e) => keep.Focus();
        }

        private void Finish(ApprovalDecision decision)
        {
            _answer.Decision = decision;
            // The checkbox is about the NEXT runs: honoured with either answer.
            _answer.DontAskAgain = _dontAsk.IsChecked == true;
            Close();
        }

        /// <summary>"Toegevoegd 0 · Gewijzigd 1 (Walls 1) · Verwijderd 2 (Structural Foundations 2)".</summary>
        internal static string SummaryText(ChangeSetSummary s)
        {
            if (s == null) return "";
            return Part(Strings.ApprovalAdded, s.Added) + "\n" + Part(Strings.ApprovalModified, s.Modified) + "\n" +
                   Part(Strings.ApprovalDeleted, s.Deleted);
        }

        private static string Part(string key, IReadOnlyList<ChangedElement> elements)
        {
            var text = Strings.Get(key) + " " + elements.Count;
            return elements.Count == 0 ? text : text + " (" + ChangeSetSummary.ByCategory(elements, Strings.Get(Strings.ApprovalOther)) + ")";
        }

        private static string ViewText(ApprovalRequest r)
        {
            if (r.IsolatedCount > 0) return Strings.Get(Strings.ApprovalIsolated, r.IsolatedCount);
            if (r.ShowableCount > 0) return Strings.Get(Strings.ApprovalNotVisible);
            // Nothing to isolate: only deletions, or only views/types changed (a type's parameter).
            bool onlyDeleted = r.Summary != null && r.Summary.Deleted.Count > 0 && r.Summary.Added.Count == 0;
            return Strings.Get(onlyDeleted ? Strings.ApprovalOnlyDeleted : Strings.ApprovalNothingToShow);
        }

        private static TextBlock Line(string text, System.Windows.Media.Brush brush, double size, bool italic = false)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = brush,
                FontSize = size,
                FontStyle = italic ? FontStyles.Italic : FontStyles.Normal,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 2)
            };
        }

        private static string Shorten(string text, int max) => text.Length <= max ? text : text.Substring(0, max - 1) + "…";

        private const int SwRestore = 9;

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    }
}
