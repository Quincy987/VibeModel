using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VibeModel.Services.Chat;
using TextBox = System.Windows.Controls.TextBox;

namespace VibeModel.UI
{
    /// <summary>
    /// Modal browser for saved chat sessions: grouped by project (most recent group
    /// first, newest session first within a group), with search and per-row
    /// open/delete. Double-click or the Open button loads a session.
    /// </summary>
    public class ChatHistoryDialog : Window
    {
        // Same dark palette as SettingsDialog / ChatPane
        private static readonly SolidColorBrush BgDark = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
        private static readonly SolidColorBrush BgPanel = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25));
        private static readonly SolidColorBrush BgInput = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        private static readonly SolidColorBrush BgRowHover = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30));
        private static readonly SolidColorBrush FgPrimary = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
        private static readonly SolidColorBrush FgSecondary = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
        private static readonly SolidColorBrush AccentBlue = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0));
        private static readonly SolidColorBrush BorderColor = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));
        private static readonly SolidColorBrush FgError = new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36));

        /// <summary>Id of the session the user chose to open (null if the dialog was dismissed).</summary>
        public string SelectedSessionId { get; private set; }

        private TextBox _searchBox;
        private TextBlock _searchHint;
        private StackPanel _listPanel;
        private readonly List<ChatSessionMeta> _sessions;

        public ChatHistoryDialog()
        {
            Title = "Chat History";
            Width = 560;
            Height = 540;
            MinWidth = 420;
            MinHeight = 320;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = BgDark;

            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var titleText = new TextBlock
            {
                Text = "Chat History",
                Foreground = FgPrimary,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(titleText, 0);
            root.Children.Add(titleText);

            var searchArea = CreateSearchBox();
            Grid.SetRow(searchArea, 1);
            root.Children.Add(searchArea);

            _listPanel = new StackPanel();
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Background = BgDark,
                Content = _listPanel,
                Margin = new Thickness(0, 8, 0, 0)
            };
            Grid.SetRow(scroll, 2);
            root.Children.Add(scroll);

            Content = root;

            _sessions = ChatSessionStore.Default.ListSessions();
            RebuildList();
        }

        private Grid CreateSearchBox()
        {
            var grid = new Grid();

            _searchBox = new TextBox
            {
                Background = BgInput,
                Foreground = FgPrimary,
                CaretBrush = FgPrimary,
                BorderBrush = BorderColor,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 5, 8, 5),
                FontSize = 13
            };
            _searchBox.TextChanged += (s, e) =>
            {
                _searchHint.Visibility = string.IsNullOrEmpty(_searchBox.Text)
                    ? Visibility.Visible : Visibility.Collapsed;
                RebuildList();
            };
            grid.Children.Add(_searchBox);

            _searchHint = new TextBlock
            {
                Text = "Search by title or project...",
                Foreground = FgSecondary,
                FontSize = 13,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            grid.Children.Add(_searchHint);

            return grid;
        }

        private void RebuildList()
        {
            _listPanel.Children.Clear();

            var filter = (_searchBox.Text ?? "").Trim();
            var visible = _sessions.Where(s =>
                filter.Length == 0
                || (s.Title ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                || (s.Project ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            if (visible.Count == 0)
            {
                _listPanel.Children.Add(new TextBlock
                {
                    Text = _sessions.Count == 0 ? "No past chats yet." : "No chats match your search.",
                    Foreground = FgSecondary,
                    FontSize = 13,
                    Margin = new Thickness(4, 12, 4, 0)
                });
                return;
            }

            var groups = visible
                .GroupBy(s => string.IsNullOrEmpty(s.Project) ? "(no project)" : s.Project)
                .OrderByDescending(g => g.Max(s => s.UpdatedUtc));

            foreach (var group in groups)
            {
                _listPanel.Children.Add(new TextBlock
                {
                    Text = group.Key,
                    Foreground = AccentBlue,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(2, 10, 0, 4)
                });

                foreach (var session in group.OrderByDescending(s => s.UpdatedUtc))
                    _listPanel.Children.Add(CreateRow(session));
            }
        }

        private Border CreateRow(ChatSessionMeta session)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var textPanel = new StackPanel();
            textPanel.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(session.Title) ? "Untitled chat" : session.Title,
                Foreground = FgPrimary,
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            textPanel.Children.Add(new TextBlock
            {
                Text = FormatDate(session.UpdatedUtc) + "  -  " + session.MessageCount +
                       (session.MessageCount == 1 ? " message" : " messages"),
                Foreground = FgSecondary,
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 0)
            });
            Grid.SetColumn(textPanel, 0);
            grid.Children.Add(textPanel);

            var openBtn = CreateRowButton("Open");
            openBtn.Click += (s, e) => OpenSession(session.Id);
            Grid.SetColumn(openBtn, 1);
            grid.Children.Add(openBtn);

            var deleteBtn = CreateRowButton("X");
            deleteBtn.MouseEnter += (s, e) => deleteBtn.Foreground = FgError;
            deleteBtn.MouseLeave += (s, e) => deleteBtn.Foreground = FgSecondary;
            deleteBtn.Click += (s, e) =>
            {
                e.Handled = true;
                DeleteSession(session);
            };
            Grid.SetColumn(deleteBtn, 2);
            grid.Children.Add(deleteBtn);

            var row = new Border
            {
                Background = BgPanel,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 6, 6, 6),
                Margin = new Thickness(0, 2, 0, 2),
                Cursor = Cursors.Hand,
                Child = grid
            };
            row.MouseEnter += (s, e) => row.Background = BgRowHover;
            row.MouseLeave += (s, e) => row.Background = BgPanel;
            row.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2)
                    OpenSession(session.Id);
            };
            return row;
        }

        private Button CreateRowButton(string text)
        {
            var btn = new Button
            {
                Content = text,
                Foreground = FgSecondary,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                FontSize = 11,
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(2, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            btn.MouseEnter += (s, e) => btn.Foreground = FgPrimary;
            btn.MouseLeave += (s, e) => btn.Foreground = FgSecondary;
            return btn;
        }

        private void OpenSession(string id)
        {
            SelectedSessionId = id;
            DialogResult = true;
            Close();
        }

        private void DeleteSession(ChatSessionMeta session)
        {
            ChatSessionStore.Default.DeleteSession(session.Id);
            _sessions.Remove(session);
            RebuildList();
        }

        private static string FormatDate(DateTime utc)
        {
            var local = utc.Kind == DateTimeKind.Utc
                ? utc.ToLocalTime()
                : DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
            var today = DateTime.Now.Date;

            if (local.Date == today)
                return "today " + local.ToString("HH:mm");
            if (local.Date == today.AddDays(-1))
                return "yesterday " + local.ToString("HH:mm");
            if (local.Year == today.Year)
                return local.ToString("d MMM");
            return local.ToString("d MMM yyyy");
        }
    }
}
