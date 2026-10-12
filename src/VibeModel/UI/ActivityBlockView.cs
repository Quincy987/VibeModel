using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using VibeModel.Markdown;
using VibeModel.Services.Chat;

namespace VibeModel.UI
{
    /// <summary>
    /// The per-turn activity block: while the turn runs, an animated indicator, an mm:ss counter
    /// and the live step list (last 8 steps, intermediate text muted); when it ends, one clickable
    /// summary line ("7 stappen · 48 s") that expands to the full list. Draws a
    /// <see cref="TurnActivity"/>; holds no state of its own beyond expanded/collapsed.
    /// </summary>
    internal sealed class ActivityBlockView : Border
    {
        private static readonly SolidColorBrush FgPrimary = MarkdownStyles.FgPrimary;
        private static readonly SolidColorBrush FgSecondary = MarkdownStyles.FgSecondary;
        private static readonly SolidColorBrush Accent = MarkdownStyles.AccentBlue;
        private static readonly SolidColorBrush FgMuted = Frozen(Color.FromRgb(0x80, 0x80, 0x80));
        private static readonly SolidColorBrush FgDone = Frozen(Color.FromRgb(0x8A, 0x8A, 0x8A));
        private static readonly SolidColorBrush FgFail = Frozen(Color.FromRgb(0xE5, 0x73, 0x73));
        private static readonly SolidColorBrush BgRunning = Frozen(Color.FromArgb(0x22, 0x4E, 0xC9, 0xB0));
        private static readonly SolidColorBrush RailBrush = MarkdownStyles.BorderColor;

        private readonly TurnActivity _model;
        private readonly StackPanel _root = new StackPanel();
        private readonly Grid _header = new Grid();
        private readonly StackPanel _dots = new StackPanel { Orientation = Orientation.Horizontal };
        private readonly TextBlock _status = new TextBlock();
        private readonly TextBlock _clock = new TextBlock();
        private readonly StackPanel _rows = new StackPanel();
        private readonly TextBlock _hint = new TextBlock();
        private TextBlock _summary;
        private Button _toggle;
        private bool _expanded;

        public ActivityBlockView(TurnActivity model)
        {
            _model = model;

            BorderBrush = RailBrush;
            BorderThickness = new Thickness(2, 0, 0, 0);
            Padding = new Thickness(10, 4, 4, 4);
            Margin = new Thickness(2, 6, 0, 4);
            Child = _root;

            _header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _dots.VerticalAlignment = VerticalAlignment.Center;
            _dots.Margin = new Thickness(0, 0, 8, 0);
            for (var i = 0; i < 3; i++)
            {
                _dots.Children.Add(new Ellipse
                {
                    Width = 5,
                    Height = 5,
                    Fill = Accent,
                    Margin = new Thickness(0, 0, 3, 0),
                    Opacity = 0.25
                });
            }
            Grid.SetColumn(_dots, 0);
            _header.Children.Add(_dots);

            _status.Foreground = FgSecondary;
            _status.FontSize = 12;
            _status.VerticalAlignment = VerticalAlignment.Center;
            _status.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetColumn(_status, 1);
            _header.Children.Add(_status);

            _clock.Foreground = FgMuted;
            _clock.FontSize = 11;
            _clock.FontFamily = MarkdownStyles.MonoFont;
            _clock.VerticalAlignment = VerticalAlignment.Center;
            _clock.Margin = new Thickness(8, 0, 2, 0);
            Grid.SetColumn(_clock, 2);
            _header.Children.Add(_clock);

            _rows.Margin = new Thickness(0, 4, 0, 0);

            _hint.Foreground = FgMuted;
            _hint.FontSize = 11;
            _hint.FontStyle = FontStyles.Italic;
            _hint.Margin = new Thickness(0, 4, 0, 0);
            _hint.TextWrapping = TextWrapping.Wrap;
            _hint.Text = UiText.StallHint;
            _hint.Visibility = Visibility.Collapsed;

            _root.Children.Add(_header);
            _root.Children.Add(_rows);
            _root.Children.Add(_hint);

            StartAnimation();
            Refresh(DateTime.UtcNow);
        }

        /// <summary>Redraw everything (after a step started/finished or text moved into a note).</summary>
        public void Refresh(DateTime nowUtc)
        {
            if (_model.IsFinished) return;

            if (_model.HasRunningStep) _status.Text = UiText.Working;
            else if (_model.IsThinking) _status.Text = UiText.Thinking;
            else _status.Text = UiText.WritingAnswer;

            int hidden;
            var items = _model.VisibleItems(false, out hidden);
            BuildRows(items, hidden);
            Tick(nowUtc);
        }

        /// <summary>Once a second: the counter and the stall hint.</summary>
        public void Tick(DateTime nowUtc)
        {
            if (_model.IsFinished) return;
            _clock.Text = UiText.Clock(_model.Elapsed(nowUtc));
            _hint.Visibility = _model.IsStalled(nowUtc) ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// The turn ended (model already finished): stop the animation and collapse to the summary
        /// line. Returns false when there were no steps — the caller removes the block entirely.
        /// </summary>
        public bool Collapse()
        {
            StopAnimation();
            if (!_model.HasSteps) return false;

            _root.Children.Clear();
            _rows.Children.Clear();
            _rows.Visibility = Visibility.Collapsed;

            _summary = new TextBlock { Foreground = FgMuted, FontSize = 11 };

            // A flat button rather than a clickable TextBlock: keyboard- and UI-Automation-
            // invokable (screen readers, test drivers) while it still reads as a plain text line.
            var template = new ControlTemplate(typeof(Button));
            var chrome = new FrameworkElementFactory(typeof(Border));
            chrome.SetValue(Border.BackgroundProperty, Brushes.Transparent); // hit-testable line
            chrome.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
            template.VisualTree = chrome;
            var toggle = new Button
            {
                Content = _summary,
                Template = template,
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Left,
                Focusable = true
            };
            toggle.Click += (s, e) => { _expanded = !_expanded; UpdateCollapsed(); };
            toggle.MouseEnter += (s, e) => _summary.Foreground = FgSecondary;
            toggle.MouseLeave += (s, e) => _summary.Foreground = FgMuted;
            _toggle = toggle;

            Padding = new Thickness(10, 2, 4, 2);
            _root.Children.Add(toggle);
            _root.Children.Add(_rows);
            UpdateCollapsed();
            return true;
        }

        private void UpdateCollapsed()
        {
            var text = UiText.StepSummary(_model.StepCount, _model.FailedCount,
                _model.Elapsed(_model.FinishedUtc ?? DateTime.UtcNow));
            _summary.Text = (_expanded ? "▾ " : "▸ ") + text;
            System.Windows.Automation.AutomationProperties.SetName(_toggle, text);

            if (_expanded)
            {
                int hidden;
                BuildRows(_model.VisibleItems(true, out hidden), 0);
                _rows.Visibility = Visibility.Visible;
            }
            else
            {
                _rows.Children.Clear();
                _rows.Visibility = Visibility.Collapsed;
            }
        }

        private void BuildRows(System.Collections.Generic.IReadOnlyList<TurnActivity.Item> items, int hiddenSteps)
        {
            _rows.Children.Clear();
            if (hiddenSteps > 0)
            {
                _rows.Children.Add(new TextBlock
                {
                    Text = UiText.EarlierSteps(hiddenSteps),
                    Foreground = FgMuted,
                    FontSize = 11,
                    Margin = new Thickness(0, 0, 0, 2)
                });
            }

            foreach (var item in items)
                _rows.Children.Add(item.IsStep ? StepRow(item.Step) : NoteRow(item.Note));
            _rows.Visibility = _rows.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // Intermediate assistant text: muted, smaller, capped to a few lines.
        private static UIElement NoteRow(string note)
        {
            return new TextBlock
            {
                Text = note,
                Foreground = FgMuted,
                FontSize = 11.5,
                FontStyle = FontStyles.Italic,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.WordEllipsis,
                MaxHeight = 64,
                LineHeight = 16,
                Margin = new Thickness(0, 2, 0, 3)
            };
        }

        private static UIElement StepRow(TurnActivity.Step step)
        {
            string glyph;
            Brush glyphBrush, labelBrush;
            switch (step.State)
            {
                case TurnActivity.StepState.Running:
                    glyph = "●"; glyphBrush = Accent; labelBrush = FgPrimary; break;
                case TurnActivity.StepState.Failed:
                    glyph = "✕"; glyphBrush = FgFail; labelBrush = FgSecondary; break;
                case TurnActivity.StepState.Cancelled:
                    glyph = "–"; glyphBrush = FgMuted; labelBrush = FgMuted; break;
                default:
                    glyph = "✓"; glyphBrush = FgDone; labelBrush = FgDone; break;
            }

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var glyphText = new TextBlock
            {
                Text = glyph,
                Foreground = glyphBrush,
                FontSize = step.State == TurnActivity.StepState.Running ? 9 : 11,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            grid.Children.Add(glyphText);

            var line = new TextBlock
            {
                Foreground = labelBrush,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            line.Inlines.Add(new System.Windows.Documents.Run(UiText.StepLabel(step.Command)));
            if (!string.IsNullOrEmpty(step.Detail))
            {
                line.Inlines.Add(new System.Windows.Documents.Run("  " + step.Detail)
                {
                    FontFamily = MarkdownStyles.MonoFont,
                    FontSize = 11,
                    Foreground = step.State == TurnActivity.StepState.Running ? FgSecondary : FgMuted
                });
            }
            line.ToolTip = UiText.StepLabel(step.Command) + (string.IsNullOrEmpty(step.Detail) ? "" : " " + step.Detail);
            Grid.SetColumn(line, 1);
            grid.Children.Add(line);

            if (step.State == TurnActivity.StepState.Failed && !string.IsNullOrEmpty(step.ErrorLine))
            {
                var error = new TextBlock
                {
                    Text = step.ErrorLine,
                    Foreground = FgFail,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 1, 0, 1)
                };
                Grid.SetColumn(error, 1);
                Grid.SetRow(error, 1);
                grid.Children.Add(error);
            }

            return new Border
            {
                Background = step.State == TurnActivity.StepState.Running ? BgRunning : Brushes.Transparent,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 2, 4, 2),
                Margin = new Thickness(-4, 0, 0, 1),
                Child = grid
            };
        }

        // --- Indicator: three dots pulsing in a wave. Throttled to a low frame rate (no CPU burn)
        // and removed outright when the turn ends.

        private void StartAnimation()
        {
            for (var i = 0; i < _dots.Children.Count; i++)
            {
                var animation = new DoubleAnimation(0.25, 1.0, TimeSpan.FromMilliseconds(450))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    BeginTime = TimeSpan.FromMilliseconds(150 * i),
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };
                Timeline.SetDesiredFrameRate(animation, 20);
                ((UIElement)_dots.Children[i]).BeginAnimation(OpacityProperty, animation);
            }
        }

        public void StopAnimation()
        {
            foreach (UIElement dot in _dots.Children)
                dot.BeginAnimation(OpacityProperty, null);
            _dots.Visibility = Visibility.Collapsed;
        }

        private static SolidColorBrush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
