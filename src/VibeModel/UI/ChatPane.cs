using System;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Autodesk.Revit.UI;
using VibeModel.Infrastructure;
using VibeModel.Markdown;
using VibeModel.Services.Chat;
using TextBox = System.Windows.Controls.TextBox;

namespace VibeModel.UI
{
    public class ChatPane : Page, IDockablePaneProvider
    {
        public static readonly DockablePaneId PaneId =
            new DockablePaneId(new Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567890"));

        private const int MaxMessages = 200;

        public event EventHandler BackendChangeRequested;

        private StackPanel _messagePanel;
        private ScrollViewer _scrollViewer;
        private TextBox _inputBox;
        private Button _sendButton;
        private Button _cancelButton;
        private Button _newChatButton;
        private Button _historyButton;
        private Button _settingsButton;
        private TextBlock _statusText;
        private Border _infoBanner;

        // Streaming state — direct references avoid O(n) visual tree search
        private TextBlock _streamingTextBlock;
        private StackPanel _streamingContentPanel;
        private Border _streamingBorder;
        private ChatMessage _streamingMessage;
        private StringBuilder _streamingContent;
        // Throttled render: tokens accrue into _streamingContent; a timer flushes to the
        // TextBlock at a fixed cadence so render cost is bounded by wall-clock, not token
        // count (avoids an O(n²) full-string rebuild on every token for long responses).
        private System.Windows.Threading.DispatcherTimer _streamingRenderTimer;
        private bool _streamingDirty;

        private IChatBackend _backend;
        private CancellationTokenSource _cts;
        private bool _isGenerating;
        private bool _userScrolledUp;

        // Current persisted session. Created lazily on the first user message
        // (never on pane open) so empty chats leave no files behind.
        private ChatSession _session;

        // Dark theme colors
        private static readonly SolidColorBrush BgDark = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
        private static readonly SolidColorBrush BgPanel = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25));
        private static readonly SolidColorBrush BgInput = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        private static readonly SolidColorBrush BgUserMsg = new SolidColorBrush(Color.FromRgb(0x2D, 0x4A, 0x7A));
        private static readonly SolidColorBrush BgAssistantMsg = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30));
        private static readonly SolidColorBrush BgSystemMsg = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x2D));
        private static readonly SolidColorBrush BgCode = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
        private static readonly SolidColorBrush FgPrimary = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
        private static readonly SolidColorBrush FgSecondary = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
        private static readonly SolidColorBrush AccentBlue = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0));
        private static readonly SolidColorBrush BgButton = new SolidColorBrush(Color.FromRgb(0x0E, 0x63, 0x9C));
        private static readonly SolidColorBrush BgButtonHover = new SolidColorBrush(Color.FromRgb(0x1E, 0x73, 0xAC));
        private static readonly SolidColorBrush BorderColor = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));
        private static readonly SolidColorBrush FgError = new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36));
        private static readonly FontFamily MonoFont = new FontFamily("Consolas");

        public ChatPane()
        {
            Background = BgDark;

            var mainGrid = new Grid();
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });

            var header = CreateHeader();
            Grid.SetRow(header, 0);
            mainGrid.Children.Add(header);

            _messagePanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(0)
            };

            _scrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Background = BgDark,
                Padding = new Thickness(8)
            };
            _scrollViewer.Content = _messagePanel;
            _scrollViewer.ScrollChanged += OnScrollChanged;

            Grid.SetRow(_scrollViewer, 1);
            mainGrid.Children.Add(_scrollViewer);

            var inputArea = CreateInputArea();
            Grid.SetRow(inputArea, 2);
            mainGrid.Children.Add(inputArea);

            Content = mainGrid;
        }

        private Border CreateHeader()
        {
            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });

            var title = new TextBlock
            {
                Text = "VibeModel Chat",
                Foreground = FgPrimary,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            Grid.SetColumn(title, 0);
            headerGrid.Children.Add(title);

            _statusText = new TextBlock
            {
                Text = "",
                Foreground = FgSecondary,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(_statusText, 1);
            headerGrid.Children.Add(_statusText);

            _newChatButton = CreateHeaderButton("New Chat");
            _newChatButton.Click += OnNewChatClick;
            Grid.SetColumn(_newChatButton, 2);
            headerGrid.Children.Add(_newChatButton);

            _historyButton = CreateHeaderButton("History");
            _historyButton.Click += OnHistoryClick;
            Grid.SetColumn(_historyButton, 3);
            headerGrid.Children.Add(_historyButton);

            _settingsButton = CreateHeaderButton("Settings");
            _settingsButton.Click += OnSettingsClick;
            Grid.SetColumn(_settingsButton, 4);
            headerGrid.Children.Add(_settingsButton);

            return new Border
            {
                Background = BgPanel,
                BorderBrush = BorderColor,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(4, 6, 4, 6),
                Child = headerGrid
            };
        }

        private Button CreateHeaderButton(string text)
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
                Margin = new Thickness(2, 0, 4, 0)
            };
            btn.MouseEnter += (s, e) => btn.Foreground = FgPrimary;
            btn.MouseLeave += (s, e) => btn.Foreground = FgSecondary;
            return btn;
        }

        private Border CreateInputArea()
        {
            var inputGrid = new Grid();
            inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });

            _inputBox = new TextBox
            {
                Background = BgInput,
                Foreground = FgPrimary,
                CaretBrush = FgPrimary,
                BorderBrush = BorderColor,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 6, 8, 6),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = false,
                MaxHeight = 120,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            _inputBox.PreviewKeyDown += OnInputPreviewKeyDown;
            Grid.SetColumn(_inputBox, 0);
            inputGrid.Children.Add(_inputBox);

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(4, 0, 0, 0)
            };

            _sendButton = new Button
            {
                Content = "Send",
                Background = BgButton,
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(12, 6, 12, 6),
                FontSize = 12,
                Cursor = Cursors.Hand
            };
            _sendButton.Click += OnSendClick;
            _sendButton.MouseEnter += (s, e) => _sendButton.Background = BgButtonHover;
            _sendButton.MouseLeave += (s, e) => _sendButton.Background = BgButton;
            buttonPanel.Children.Add(_sendButton);

            _cancelButton = new Button
            {
                Content = "Cancel",
                Background = Brushes.Transparent,
                Foreground = FgSecondary,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(12, 4, 12, 4),
                FontSize = 11,
                Cursor = Cursors.Hand,
                Visibility = Visibility.Collapsed
            };
            _cancelButton.Click += OnCancelClick;
            buttonPanel.Children.Add(_cancelButton);

            Grid.SetColumn(buttonPanel, 1);
            inputGrid.Children.Add(buttonPanel);

            return new Border
            {
                Background = BgPanel,
                BorderBrush = BorderColor,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(8),
                Child = inputGrid
            };
        }

        // --- Backend integration ---

        public void InitializeBackend(IChatBackend backend)
        {
            // Backend switch ends the current session — persist it first.
            ChatSessionStore.Default.SaveSession(_session);
            _session = null;

            _backend = backend;
            _messagePanel.Children.Clear();
            ClearStreamingState();
            ChatHistory.Clear();
            UpdateStatus();
            ShowInfoBanner();
            ShowWelcomeMessage();
        }

        // Stable backend label persisted with each session; drives resume behavior on load.
        private string BackendLabel()
        {
            if (_backend is ClaudeCodeBackend) return "claude-cli";
            if (_backend is LocalLlmBackend) return "local";
            return "anthropic";
        }

        private void ShowWelcomeMessage()
        {
            if (_backend == null) return;

            string statusLine;
            if (_backend.IsAvailable)
            {
                if (_backend is LocalLlmBackend)
                    statusLine = "Connected to local LLM. You can now chat directly inside Revit.\n\n";
                else
                    statusLine = "Connected to Claude. You can now talk to Claude directly inside Revit.\n\n";
            }
            else
            {
                statusLine = "To get started, either:\n" +
                    "- **Recommended:** Install Claude Code (`npm install -g @anthropic-ai/claude-code`) and set your `ANTHROPIC_API_KEY` environment variable\n" +
                    "- **Lightweight:** Click **Settings** to enter your Anthropic API key directly\n" +
                    "- **Local:** Connect a local LLM server (llama.cpp, Ollama, LM Studio) via **Settings**\n\n";
            }

            var welcome = new ChatMessage(ChatRole.System,
                "Welcome to VibeModel Chat!\n\n" +
                statusLine +
                "Examples:\n" +
                "  - \"What document is open?\"\n" +
                "  - \"List all walls\"\n" +
                "  - \"Create 4 walls forming a 5x5m room\"\n" +
                "  - \"Select element 12345 and change its color to red\"");
            AddMessage(welcome);
        }

        private void ShowInfoBanner()
        {
            // Remove previous banner if any
            if (_infoBanner != null)
            {
                _messagePanel.Children.Remove(_infoBanner);
                _infoBanner = null;
            }

            // Show chat-only banner for local LLM without tool use
            if (_backend is LocalLlmBackend && !SettingsManager.GetLocalLlmToolUse())
            {
                var bannerGrid = new Grid();
                bannerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                bannerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });

                bannerGrid.Children.Add(new TextBlock
                {
                    Text = "Chat only — Revit commands require enabling tool use in Settings.",
                    Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB7, 0x4D)),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center
                });

                var dismissBtn = new Button
                {
                    Content = "X",
                    Foreground = FgSecondary,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                    FontSize = 10,
                    Padding = new Thickness(4, 0, 4, 0)
                };
                dismissBtn.Click += (s, e) =>
                {
                    _messagePanel.Children.Remove(_infoBanner);
                    _infoBanner = null;
                };
                Grid.SetColumn(dismissBtn, 1);
                bannerGrid.Children.Add(dismissBtn);

                _infoBanner = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x33, 0x1E)),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(10, 6, 10, 6),
                    Margin = new Thickness(0, 4, 0, 4),
                    Child = bannerGrid
                };

                _messagePanel.Children.Insert(0, _infoBanner);
            }
        }

        private void UpdateStatus()
        {
            if (_backend == null) return;

            if (_backend.IsAvailable)
            {
                string label;
                if (_backend is LocalLlmBackend)
                    label = "Local LLM";
                else if (_backend is ClaudeCodeBackend)
                    label = "Claude CLI";
                else
                    label = "Anthropic API";

                _statusText.Text = label;
                _statusText.Foreground = AccentBlue;
            }
            else
            {
                _statusText.Text = "Not Connected";
                _statusText.Foreground = FgError;
            }
        }

        // --- Input handling ---

        private void OnSendClick(object sender, RoutedEventArgs e)
        {
            SendCurrentMessage();
        }

        private void OnInputPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (Keyboard.Modifiers == ModifierKeys.Shift)
                {
                    var caretIndex = _inputBox.CaretIndex;
                    _inputBox.Text = _inputBox.Text.Insert(caretIndex, "\n");
                    _inputBox.CaretIndex = caretIndex + 1;
                    e.Handled = true;
                }
                else
                {
                    e.Handled = true;
                    SendCurrentMessage();
                }
            }
            else if (e.Key == Key.Escape && _isGenerating)
            {
                e.Handled = true;
                CancelGeneration();
            }
        }

        private void SendCurrentMessage()
        {
            var text = _inputBox.Text?.Trim();
            if (string.IsNullOrEmpty(text) || _isGenerating)
                return;

            // Re-check availability (API key may have been configured via Settings)
            if (_backend != null && _backend.IsAvailable)
                UpdateStatus();

            if (_backend == null || !_backend.IsAvailable)
            {
                var msg = _backend?.StatusMessage ?? "Chat backend not initialized. Click Settings to configure your API key.";
                AddMessage(new ChatMessage(ChatRole.System, msg));
                ChatHistory.Add("System", msg);
                if (_session != null)
                {
                    _session.Append(ChatSession.RoleSystem, msg);
                    ChatSessionStore.Default.SaveSession(_session);
                }
                return;
            }

            _inputBox.Text = "";

            // User message
            var userMsg = new ChatMessage(ChatRole.User, text);
            AddMessage(userMsg);
            ChatHistory.Add("You", text);

            // Lazily start the persisted session on the first user message.
            if (_session == null)
                _session = ChatSession.Start(ActiveDocumentTracker.Title, BackendLabel());
            _session.Append(ChatSession.RoleUser, text);

            // Capture the session that owns this turn. The completion/error callbacks
            // arrive via the Dispatcher and may fire AFTER the user switched to a new
            // chat or loaded another session — recording against the captured session
            // keeps a late reply/error in the conversation it belongs to.
            var sessionForTurn = _session;

            // Streaming placeholder — plain TextBlock, replaced with rendered markdown on complete
            _streamingMessage = new ChatMessage(ChatRole.Assistant, "");
            _streamingContent = new StringBuilder();

            StackPanel contentPanel;
            _streamingBorder = CreateMessageChrome(_streamingMessage, out contentPanel);
            _streamingContentPanel = contentPanel;
            _streamingTextBlock = new TextBlock
            {
                // Immediate feedback the instant the user sends — replaced by the first
                // streamed token (onToken overwrites this with the real content).
                Text = "Looking at the model…",
                Foreground = FgPrimary,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap
            };
            contentPanel.Children.Add(_streamingTextBlock);
            _messagePanel.Children.Add(_streamingBorder);
            ScrollToBottomIfNeeded();

            SetGenerating(true);
            StartStreamingRenderTimer();

            _cts = new CancellationTokenSource();

            _backend.SendMessage(
                text,
                onToken: delta =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_streamingTextBlock == null) return;

                        // Cheap: just accrue. The render timer flushes to the UI on a
                        // fixed cadence, so per-token work stays O(1).
                        _streamingContent.Append(delta);
                        _streamingDirty = true;
                    }));
                },
                onComplete: fullText =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_streamingContentPanel != null)
                        {
                            var content = _streamingContent.Length > 0
                                ? _streamingContent.ToString()
                                : fullText;
                            _streamingMessage.Content = content;

                            // Replace plain TextBlock with rendered markdown
                            _streamingContentPanel.Children.Remove(_streamingTextBlock);
                            AddRenderedContent(_streamingContentPanel, content);

                            ChatHistory.Add("Claude", content);
                            sessionForTurn.Append(ChatSession.RoleAssistant, content);
                            ChatSessionStore.Default.SaveSession(sessionForTurn);
                            ClearStreamingState();
                        }
                        SetGenerating(false);
                        ScrollToBottomIfNeeded();
                    }));
                },
                onError: error =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_streamingBorder != null)
                        {
                            _messagePanel.Children.Remove(_streamingBorder);
                            ClearStreamingState();
                        }
                        var errorMsg = "Error: " + error;
                        AddMessage(new ChatMessage(ChatRole.System, errorMsg));
                        ChatHistory.Add("System", errorMsg);
                        sessionForTurn.Append(ChatSession.RoleSystem, errorMsg);
                        ChatSessionStore.Default.SaveSession(sessionForTurn);
                        SetGenerating(false);
                        ScrollToBottomIfNeeded();
                    }));
                },
                cancellationToken: _cts.Token);
        }

        // Flushes accrued stream text to the TextBlock at a fixed cadence (~25fps) rather
        // than on every token, so a long response renders in O(length) total, not O(length²).
        private void StartStreamingRenderTimer()
        {
            _streamingDirty = false;
            if (_streamingRenderTimer == null)
            {
                _streamingRenderTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(40)
                };
                _streamingRenderTimer.Tick += (s, e) =>
                {
                    if (!_streamingDirty || _streamingTextBlock == null) return;
                    _streamingDirty = false;
                    _streamingTextBlock.Text = _streamingContent.ToString() + " ...";
                    ScrollToBottomIfNeeded();
                };
            }
            _streamingRenderTimer.Start();
        }

        private void ClearStreamingState()
        {
            _streamingRenderTimer?.Stop();
            _streamingDirty = false;
            _streamingTextBlock = null;
            _streamingContentPanel = null;
            _streamingBorder = null;
            _streamingMessage = null;
            _streamingContent = null;
        }

        private void CancelGeneration()
        {
            _cts?.Cancel();
            _backend?.Cancel();
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            CancelGeneration();
        }

        private void OnNewChatClick(object sender, RoutedEventArgs e)
        {
            if (_isGenerating)
                CancelGeneration();

            // Persist the conversation we're leaving before wiping the pane.
            ChatSessionStore.Default.SaveSession(_session);
            _session = null;

            _messagePanel.Children.Clear();
            ClearStreamingState();
            _infoBanner = null;
            _backend?.ResetSession();
            ChatHistory.Clear();
            ShowInfoBanner();
            ShowWelcomeMessage();
        }

        private void OnHistoryClick(object sender, RoutedEventArgs e)
        {
            var dialog = new ChatHistoryDialog();
            var result = dialog.ShowDialog();
            if (result == true && !string.IsNullOrEmpty(dialog.SelectedSessionId))
                LoadSession(dialog.SelectedSessionId);
        }

        /// <summary>
        /// Replace the pane contents with a saved session and rebuild backend context
        /// so the user can continue where they left off.
        /// </summary>
        private void LoadSession(string id)
        {
            var loaded = ChatSessionStore.Default.LoadSession(id);
            if (loaded == null)
            {
                AddMessage(new ChatMessage(ChatRole.System,
                    "Could not load that chat - the file may have been deleted."));
                return;
            }

            if (_isGenerating)
                CancelGeneration();

            // Persist the conversation we're leaving.
            ChatSessionStore.Default.SaveSession(_session);

            _messagePanel.Children.Clear();
            ClearStreamingState();
            _infoBanner = null;
            ChatHistory.Clear();

            // The loaded session becomes current: new turns append to the same file.
            _session = loaded;
            _session.Backend = BackendLabel();

            foreach (var m in loaded.Messages)
            {
                ChatRole role;
                string logRole;
                if (m.Role == ChatSession.RoleUser) { role = ChatRole.User; logRole = "You"; }
                else if (m.Role == ChatSession.RoleAssistant) { role = ChatRole.Assistant; logRole = "Claude"; }
                else { role = ChatRole.System; logRole = "System"; }

                AddMessage(new ChatMessage(role, m.Content));
                ChatHistory.Add(logRole, m.Content);
            }

            _backend?.RestoreHistory(loaded.Messages);
            if (_backend is ClaudeCodeBackend)
            {
                AddMessage(new ChatMessage(ChatRole.System,
                    "Restored transcript - the assistant will see this as a new session " +
                    "(previous CLI context can't be resumed)."));
            }

            ShowInfoBanner();
            UpdateStatus();
        }

        private void OnSettingsClick(object sender, RoutedEventArgs e)
        {
            var dialog = new SettingsDialog();
            var result = dialog.ShowDialog();

            if (result == true && dialog.BackendChanged)
            {
                // Backend was changed — request parent to recreate and reinject
                BackendChangeRequested?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                // Settings may have changed (e.g. API key) without switching backend
                UpdateStatus();
            }
        }

        private void SetGenerating(bool generating)
        {
            _isGenerating = generating;
            _inputBox.IsEnabled = !generating;
            _sendButton.Visibility = generating ? Visibility.Collapsed : Visibility.Visible;
            _cancelButton.Visibility = generating ? Visibility.Visible : Visibility.Collapsed;

            if (!generating)
            {
                _inputBox.Focus();
                _cts?.Dispose();
                _cts = null;
            }
        }

        // --- Message rendering ---

        private void AddMessage(ChatMessage message)
        {
            while (_messagePanel.Children.Count >= MaxMessages)
                _messagePanel.Children.RemoveAt(0);

            StackPanel contentPanel;
            var border = CreateMessageChrome(message, out contentPanel);
            AddRenderedContent(contentPanel, message.Content);
            _messagePanel.Children.Add(border);
            ScrollToBottomIfNeeded();
        }

        private Border CreateMessageChrome(ChatMessage message, out StackPanel contentPanel)
        {
            SolidColorBrush bgBrush;
            string roleLabel;
            switch (message.Role)
            {
                case ChatRole.User:
                    bgBrush = BgUserMsg;
                    roleLabel = "You";
                    break;
                case ChatRole.Assistant:
                    bgBrush = BgAssistantMsg;
                    roleLabel = "Claude";
                    break;
                default:
                    bgBrush = BgSystemMsg;
                    roleLabel = "System";
                    break;
            }

            contentPanel = new StackPanel();

            // Header row: role label + copy button
            var headerRow = new Grid();
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });

            headerRow.Children.Add(new TextBlock
            {
                Text = roleLabel,
                Foreground = AccentBlue,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4)
            });

            // Copy button — reads from message object so it works after re-rendering
            var capturedMessage = message;
            var copyBtn = new Button
            {
                Content = "Copy",
                Foreground = FgSecondary,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                FontSize = 10,
                Padding = new Thickness(4, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Top
            };
            copyBtn.Click += (s, e) =>
            {
                try
                {
                    Clipboard.SetText(capturedMessage.Content);
                    copyBtn.Content = "Copied!";
                    var timer = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromSeconds(1.5)
                    };
                    timer.Tick += (t, args) =>
                    {
                        copyBtn.Content = "Copy";
                        timer.Stop();
                    };
                    timer.Start();
                }
                catch { }
            };
            copyBtn.MouseEnter += (s, e) => copyBtn.Foreground = FgPrimary;
            copyBtn.MouseLeave += (s, e) => copyBtn.Foreground = FgSecondary;
            Grid.SetColumn(copyBtn, 1);
            headerRow.Children.Add(copyBtn);

            contentPanel.Children.Add(headerRow);

            return new Border
            {
                Background = bgBrush,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 4, 0, 4),
                Child = contentPanel
            };
        }

        // --- Markdown rendering ---

        private void AddRenderedContent(StackPanel panel, string content)
        {
            if (string.IsNullOrEmpty(content))
                return;

            var rtb = CreateMarkdownRichTextBox(content);
            panel.Children.Add(rtb);
        }

        private RichTextBox CreateMarkdownRichTextBox(string markdown)
        {
            var doc = MarkdownHelper.ToFlowDocument(markdown);

            var rtb = new RichTextBox
            {
                Document = doc,
                IsReadOnly = true,
                IsDocumentEnabled = true,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = FgPrimary,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 0, -4), // compensate FlowDocument paragraph spacing
            };

            // Disable built-in context menu
            rtb.ContextMenu = null;

            return rtb;
        }

        // --- Scrolling ---

        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            var atBottom = _scrollViewer.VerticalOffset >= _scrollViewer.ScrollableHeight - 20;
            _userScrolledUp = !atBottom;
        }

        private void ScrollToBottomIfNeeded()
        {
            if (!_userScrolledUp)
                _scrollViewer.ScrollToEnd();
        }

        // --- Dockable pane ---

        public void SetupDockablePane(DockablePaneProviderData data)
        {
            data.FrameworkElement = this;
            data.InitialState = new DockablePaneState
            {
                DockPosition = DockPosition.Right
            };
            data.VisibleByDefault = false;
        }

        public void Cleanup()
        {
            _cts?.Cancel();
            _backend?.Cancel();
            // Persist whatever conversation is open when Revit shuts down.
            ChatSessionStore.Default.SaveSession(_session);
        }
    }
}
