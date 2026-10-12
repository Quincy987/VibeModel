using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Autodesk.Revit.UI;
using VibeModel.Infrastructure;
using VibeModel.Markdown;
using VibeModel.Services.Chat;
using VibeModel.Services.Chat.QuickReplies;
using VibeModel.Services.Claude;
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
        private Button _examplesButton;
        private Border _infoBanner;
        // The compact card at the top of an empty chat (backend, model, project folder, chips).
        private Border _welcomeCard;
        // The one in-pane popup (a source, or the examples list).
        private Popup _popup;

        // Quick replies: the chip row above the input, made by a cheap model call after an answer.
        private Border _suggestionBar;
        private WrapPanel _suggestionPanel;
        private IReadOnlyList<QuickReply> _quickReplies;      // the set on show (null: none)
        private QuickReplyRequest _quickReplyRequest;         // the answer the next set must belong to
        private System.Windows.Threading.DispatcherTimer _typingTimer;
        // "Klaar om te versturen" under the input after a chip that changes the model filled it.
        private TextBlock _readyHint;
        private string _readyHintText;
        // Starter chips: documents in the project folder, listed off the UI thread and cached.
        private string _docCountFolder;
        private int _docCount;
        private DateTime _docCountAtUtc;
        private bool _docCountPending;

        // The turn in flight. Direct references avoid O(n) visual tree search.
        private sealed class TurnUi
        {
            // Every streamed token, exactly as before — this (not the display) is what gets
            // stored in the chat history JSON.
            public StringBuilder Raw = new StringBuilder();
            // What the pane draws: steps, muted intermediate notes, and the answer.
            public TurnActivity Model;
            public ActivityBlockView Block;
            // The answer bubble below the block — created when answer text first appears.
            public ChatMessage AnswerMessage;
            public Border AnswerBorder;
            public StackPanel AnswerPanel;
            public TextBlock AnswerText;
            public bool TextDirty;
            public bool ActivityDirty;
        }

        private TurnUi _turn;
        // Throttled render: tokens and activity events update the model at once; a timer
        // flushes to the UI at a fixed cadence so render cost is bounded by wall-clock, not
        // token count (avoids an O(n²) full-string rebuild on every token for long responses).
        private System.Windows.Threading.DispatcherTimer _streamingRenderTimer;
        // Once a second while a turn runs: the mm:ss counter and the stall hint.
        private System.Windows.Threading.DispatcherTimer _clockTimer;
        // User messages shown so far — a thin separator goes in front of every one after the first.
        private int _userTurnCount;

        private IChatBackend _backend;
        private CancellationTokenSource _cts;
        private bool _isGenerating;
        private bool _userScrolledUp;

        // Current persisted session. Created lazily on the first user message
        // (never on pane open) so empty chats leave no files behind.
        private ChatSession _session;

        // Pending attachments (chips above the input box, not yet sent). Pasted
        // clipboard images are saved into the AttachmentStore immediately (the
        // clipboard is transient); picked/dropped files are copied at send time.
        private class PendingFile
        {
            public string SourcePath;
            public bool AlreadyStored;
        }

        private readonly List<PendingFile> _pendingAttachments = new List<PendingFile>();
        private Button _attachButton;
        private Border _attachmentBar;
        private WrapPanel _attachmentChipPanel;

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
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
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

            _suggestionBar = CreateSuggestionBar();
            Grid.SetRow(_suggestionBar, 2);
            mainGrid.Children.Add(_suggestionBar);

            _attachmentBar = CreateAttachmentBar();
            Grid.SetRow(_attachmentBar, 3);
            mainGrid.Children.Add(_attachmentBar);

            var inputArea = CreateInputArea();
            Grid.SetRow(inputArea, 4);
            mainGrid.Children.Add(inputArea);

            Content = mainGrid;

            // Drag-and-drop files anywhere onto the pane. Preview events intercept
            // before the TextBox (which would otherwise refuse file drops).
            AllowDrop = true;
            PreviewDragOver += OnPreviewDragOver;
            PreviewDrop += OnPreviewDrop;

            // Esc closes the popup wherever the focus is in the pane.
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape && _popup != null && _popup.IsOpen)
                {
                    ClosePopup();
                    e.Handled = true;
                }
            };
            // A model opened or switched: the welcome card names it.
            ActiveDocumentTracker.Changed += OnActiveDocumentChanged;
        }

        private Border CreateHeader()
        {
            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
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
                TextTrimming = TextTrimming.CharacterEllipsis,
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

            // "?" - the longer example list (the welcome card only has four chips).
            _examplesButton = CreateHeaderButton("?");
            _examplesButton.FontWeight = FontWeights.SemiBold;
            _examplesButton.Padding = new Thickness(5, 2, 5, 2);
            _examplesButton.Margin = new Thickness(0, 0, 2, 0);
            _examplesButton.ToolTip = Strings.Get(Strings.ExamplesTitle);
            _examplesButton.Click += (s, e) => ShowExamplesPopup();
            Grid.SetColumn(_examplesButton, 2);
            headerGrid.Children.Add(_examplesButton);

            _newChatButton = CreateHeaderButton("New Chat");
            _newChatButton.Click += OnNewChatClick;
            Grid.SetColumn(_newChatButton, 3);
            headerGrid.Children.Add(_newChatButton);

            _historyButton = CreateHeaderButton("History");
            _historyButton.Click += OnHistoryClick;
            Grid.SetColumn(_historyButton, 4);
            headerGrid.Children.Add(_historyButton);

            _settingsButton = CreateHeaderButton("Settings");
            _settingsButton.Click += OnSettingsClick;
            Grid.SetColumn(_settingsButton, 5);
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

        // Chip row above the input: one removable chip per pending attachment.
        // Collapsed while empty so the layout is untouched for text-only use.
        private Border CreateAttachmentBar()
        {
            _attachmentChipPanel = new WrapPanel { Orientation = Orientation.Horizontal };

            return new Border
            {
                Background = BgPanel,
                BorderBrush = BorderColor,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(8, 6, 8, 2),
                Visibility = Visibility.Collapsed,
                Child = _attachmentChipPanel
            };
        }

        private Border CreateInputArea()
        {
            var inputGrid = new Grid();
            inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
            inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });

            _attachButton = new Button
            {
                Content = "📎",
                Background = Brushes.Transparent,
                Foreground = FgSecondary,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6, 6, 6, 6),
                Margin = new Thickness(0, 0, 4, 0),
                FontSize = 14,
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Bottom,
                ToolTip = "Attach documents or images (or paste / drag-and-drop)"
            };
            _attachButton.Click += OnAttachClick;
            _attachButton.MouseEnter += (s, e) => _attachButton.Foreground = FgPrimary;
            _attachButton.MouseLeave += (s, e) => _attachButton.Foreground = FgSecondary;
            Grid.SetColumn(_attachButton, 0);
            inputGrid.Children.Add(_attachButton);

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
            _inputBox.TextChanged += OnInputTextChanged;
            // Intercept paste: files / clipboard images become attachments; text pastes normally.
            DataObject.AddPastingHandler(_inputBox, OnInputPasting);
            Grid.SetColumn(_inputBox, 1);
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

            Grid.SetColumn(buttonPanel, 2);
            inputGrid.Children.Add(buttonPanel);

            _readyHint = new TextBlock
            {
                Foreground = AccentBlue,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(34, 4, 0, 0),
                Visibility = Visibility.Collapsed
            };
            var inputStack = new StackPanel();
            inputStack.Children.Add(inputGrid);
            inputStack.Children.Add(_readyHint);

            return new Border
            {
                Background = BgPanel,
                BorderBrush = BorderColor,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(8),
                Child = inputStack
            };
        }

        // --- Backend integration ---

        public void InitializeBackend(IChatBackend backend)
        {
            // Backend switch ends the current session — persist it first.
            ChatSessionStore.Default.SaveSession(_session);
            _session = null;

            _backend = backend;
            ClosePopup();
            ClearQuickReplies();
            _readyHint.Visibility = Visibility.Collapsed;
            _messagePanel.Children.Clear();
            _userTurnCount = 0;
            ClearStreamingState();
            _pendingAttachments.Clear();
            RebuildAttachmentChips();
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
            _welcomeCard = BuildWelcomeCard();
            _messagePanel.Children.Add(_welcomeCard);
        }

        // Rebuilds the card in place (language switch, model opened) while it is still shown.
        private void RefreshWelcomeCard()
        {
            if (_welcomeCard == null || _backend == null) return;
            var index = _messagePanel.Children.IndexOf(_welcomeCard);
            if (index < 0) { _welcomeCard = null; return; }
            _messagePanel.Children.RemoveAt(index);
            _welcomeCard = BuildWelcomeCard();
            _messagePanel.Children.Insert(index, _welcomeCard);
        }

        private void OnActiveDocumentChanged(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(RefreshWelcomeCard));
        }

        /// <summary>
        /// The welcome card: backend + model, open model + project folder, four example chips
        /// that fill the input box. Not configured: the three ways to set a backend up.
        /// Texts from <see cref="UiText"/> in the Settings language.
        /// </summary>
        private Border BuildWelcomeCard()
        {
            var language = Strings.Current;
            var panel = new StackPanel();
            var chatBackend = _backend as ChatBackendBase;

            if (_backend.IsAvailable)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = UiText.WelcomeBackendLine(chatBackend?.DisplayName ?? "Chat", chatBackend?.ModelName, language),
                    Foreground = AccentBlue,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });

                var title = ActiveDocumentTracker.Title;
                var folder = string.IsNullOrWhiteSpace(title)
                    ? null
                    : ProjectDocs.ResolveForDocument(title, ActiveDocumentTracker.PathName).Path;
                var modelLine = UiText.WelcomeModelLine(title, folder, language);
                panel.Children.Add(new TextBlock
                {
                    Text = modelLine,
                    ToolTip = modelLine,
                    Foreground = FgSecondary,
                    FontSize = 11,
                    Margin = new Thickness(0, 2, 0, 6),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });

                // Starters follow the live context (selection, sheet, office pack, project documents).
                var chips = new WrapPanel();
                foreach (var example in Starters.Pick(BuildStarterContext(), language))
                    chips.Children.Add(CreateExampleChip(example, Strings.For(language, Strings.WelcomeChipTip)));
                panel.Children.Add(chips);
            }
            else
            {
                var lines = UiText.WelcomeSetupLines(language);
                panel.Children.Add(new TextBlock
                {
                    Text = lines[0],
                    Foreground = FgPrimary,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 4)
                });
                for (int i = 1; i < lines.Length; i++)
                {
                    // Selectable, so the npm line can be copied.
                    panel.Children.Add(new TextBox
                    {
                        Text = "• " + lines[i],
                        IsReadOnly = true,
                        BorderThickness = new Thickness(0),
                        Background = Brushes.Transparent,
                        Foreground = FgSecondary,
                        FontSize = 11,
                        TextWrapping = TextWrapping.Wrap,
                        Padding = new Thickness(0),
                        Margin = new Thickness(0, 1, 0, 1)
                    });
                }
            }

            return new Border
            {
                Background = BgPanel,
                BorderBrush = BorderColor,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 10, 6),
                Margin = new Thickness(0, 4, 0, 4),
                Child = panel
            };
        }

        // A rounded chip; a click puts its text in the input box (the "…" selected, to type over).
        private Button CreateExampleChip(string text, string tooltip)
        {
            var chip = CreateChip(text, tooltip);
            chip.Click += (s, e) => UseExample(text);
            System.Windows.Automation.AutomationProperties.SetAutomationId(chip, "StarterChip");
            return chip;
        }

        private static ControlTemplate _chipTemplate;

        // A chip is a Button (keyboard and UI Automation can invoke it) drawn as a rounded pill.
        private Button CreateChip(string text, string tooltip)
        {
            if (_chipTemplate == null)
            {
                var border = new FrameworkElementFactory(typeof(Border));
                border.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
                border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
                border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
                border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
                border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
                border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
                _chipTemplate = new ControlTemplate(typeof(Button)) { VisualTree = border };
            }

            var chip = new Button
            {
                Template = _chipTemplate,
                Background = BgInput,
                BorderBrush = BgInput,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 2, 8, 3),
                Margin = new Thickness(0, 0, 6, 4),
                Cursor = Cursors.Hand,
                ToolTip = tooltip,
                MaxWidth = 280,
                Content = new TextBlock
                {
                    Text = text,
                    Foreground = FgPrimary,
                    FontSize = 11,
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
            };
            System.Windows.Automation.AutomationProperties.SetName(chip, text);
            chip.MouseEnter += (s, e) => chip.BorderBrush = AccentBlue;
            chip.MouseLeave += (s, e) => chip.BorderBrush = BgInput;
            return chip;
        }

        // What the starter chips are picked from; the project-folder count comes from a cached,
        // off-thread listing (a network folder must never stall the pane).
        private StarterContext BuildStarterContext()
        {
            var ctx = new StarterContext
            {
                SelectedCount = ActiveDocumentTracker.SelectedCount,
                ActiveViewIsSheet = ActiveDocumentTracker.ActiveViewIsSheet,
                PackActive = !string.IsNullOrWhiteSpace(Services.Standards.StandardsPrompt.StandardsBlock)
            };

            var title = ActiveDocumentTracker.Title;
            var folder = string.IsNullOrWhiteSpace(title)
                ? null
                : ProjectDocs.ResolveForDocument(title, ActiveDocumentTracker.PathName).Path;
            if (string.IsNullOrWhiteSpace(folder)) return ctx;

            var fresh = string.Equals(folder, _docCountFolder, StringComparison.OrdinalIgnoreCase) &&
                        DateTime.UtcNow - _docCountAtUtc < TimeSpan.FromMinutes(2);
            if (string.Equals(folder, _docCountFolder, StringComparison.OrdinalIgnoreCase))
                ctx.ProjectDocCount = _docCount;
            if (!fresh && !_docCountPending)
            {
                _docCountPending = true;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    var count = 0;
                    try { count = ProjectDocs.TryList(folder, null, out string listError)?.TotalFiles ?? 0; }
                    catch (Exception ex) { Logger.Info("Starter chips: project folder not listed - " + ex.Message); }
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        var changed = count != _docCount ||
                                      !string.Equals(folder, _docCountFolder, StringComparison.OrdinalIgnoreCase);
                        _docCountFolder = folder;
                        _docCount = count;
                        _docCountAtUtc = DateTime.UtcNow;
                        _docCountPending = false;
                        if (changed) RefreshWelcomeCard();
                    }));
                });
            }
            return ctx;
        }

        private void UseExample(string text)
        {
            if (_isGenerating) return;
            ClosePopup();
            _inputBox.Text = text;
            _inputBox.Focus();
            var gap = text.IndexOf('…');
            if (gap >= 0) _inputBox.Select(gap, 1);
            else _inputBox.CaretIndex = text.Length;
        }

        private void ShowExamplesPopup()
        {
            var language = Strings.Current;
            var list = new StackPanel();
            foreach (var example in UiText.ExampleList(language))
            {
                var line = new TextBlock
                {
                    Text = "• " + example,
                    Foreground = FgPrimary,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 2),
                    Cursor = Cursors.Hand
                };
                var captured = example;
                line.MouseEnter += (s, e) => line.Foreground = AccentBlue;
                line.MouseLeave += (s, e) => line.Foreground = FgPrimary;
                line.MouseLeftButtonUp += (s, e) => UseExample(captured);
                list.Children.Add(line);
            }
            list.Children.Add(new TextBlock
            {
                Text = Strings.For(language, Strings.ExamplesTip),
                Foreground = FgSecondary,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0)
            });
            ShowPopup(Strings.For(language, Strings.ExamplesTitle),
                new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list });
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

        // --- Attachments -----------------------------------------------------------

        private void OnAttachClick(object sender, RoutedEventArgs e)
        {
            if (_isGenerating) return;

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Multiselect = true,
                Filter = "Documents & images|*.pdf;*.png;*.jpg;*.jpeg;*.gif;*.webp;*.txt;*.md;*.csv;*.json;*.xml|All files|*.*",
                Title = "Attach files to chat"
            };
            if (dialog.ShowDialog() == true)
                AttachFiles(dialog.FileNames);
        }

        private void OnInputPasting(object sender, DataObjectPastingEventArgs e)
        {
            try
            {
                if (_isGenerating) return;

                if (e.DataObject.GetDataPresent(DataFormats.FileDrop))
                {
                    var files = e.DataObject.GetData(DataFormats.FileDrop) as string[];
                    if (files != null && files.Length > 0)
                    {
                        AttachFiles(files);
                        e.CancelCommand();
                    }
                }
                else if (e.DataObject.GetDataPresent(DataFormats.Bitmap))
                {
                    // Clipboard images (e.g. a screenshotted spec page) are transient —
                    // persist to the store immediately, then treat like any attachment.
                    var image = Clipboard.GetImage();
                    if (image != null)
                    {
                        var path = AttachmentStore.SaveClipboardImage(image, GetProjectKey());
                        _pendingAttachments.Add(new PendingFile { SourcePath = path, AlreadyStored = true });
                        RebuildAttachmentChips();
                        e.CancelCommand();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Paste attachment failed", ex);
            }
        }

        private void OnPreviewDragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = _isGenerating ? DragDropEffects.None : DragDropEffects.Copy;
                e.Handled = true;
            }
        }

        private void OnPreviewDrop(object sender, DragEventArgs e)
        {
            if (_isGenerating) return;
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null && files.Length > 0)
            {
                AttachFiles(files);
                e.Handled = true;
            }
        }

        private void AttachFiles(IEnumerable<string> paths)
        {
            if (_isGenerating) return;

            foreach (var path in paths)
            {
                try
                {
                    if (!File.Exists(path))
                        continue; // skips folders and vanished files
                    if (_pendingAttachments.Any(p =>
                            string.Equals(p.SourcePath, path, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    _pendingAttachments.Add(new PendingFile { SourcePath = path, AlreadyStored = false });
                }
                catch (Exception ex)
                {
                    Logger.Error("Failed to add attachment: " + path, ex);
                }
            }
            RebuildAttachmentChips();
        }

        private void RebuildAttachmentChips()
        {
            _attachmentChipPanel.Children.Clear();

            foreach (var pending in _pendingAttachments)
            {
                var captured = pending;
                long size = 0;
                try { size = new FileInfo(pending.SourcePath).Length; } catch { }

                var chipPanel = new StackPanel { Orientation = Orientation.Horizontal };
                chipPanel.Children.Add(new TextBlock
                {
                    Text = "📎 " + Path.GetFileName(pending.SourcePath) +
                           " (" + ChatAttachment.FormatSize(size) + ")",
                    Foreground = FgPrimary,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center
                });

                var removeBtn = new Button
                {
                    Content = "×",
                    Foreground = FgSecondary,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                    FontSize = 12,
                    Padding = new Thickness(4, 0, 2, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                removeBtn.Click += (s, e) =>
                {
                    _pendingAttachments.Remove(captured);
                    RebuildAttachmentChips();
                };
                removeBtn.MouseEnter += (s, e) => removeBtn.Foreground = FgError;
                removeBtn.MouseLeave += (s, e) => removeBtn.Foreground = FgSecondary;
                chipPanel.Children.Add(removeBtn);

                _attachmentChipPanel.Children.Add(new Border
                {
                    Background = BgInput,
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(6, 2, 4, 2),
                    Margin = new Thickness(0, 0, 4, 4),
                    Child = chipPanel
                });
            }

            _attachmentBar.Visibility = _pendingAttachments.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        // Last known document title — fallback once the document is closed.
        private string _lastProjectKey;

        /// <summary>
        /// The current Revit document title as the per-project key (attachment folder,
        /// project folder setting). Falls back to the last known title, then "default".
        /// </summary>
        private string GetProjectKey() => GetProjectIdentity(out _);

        /// <summary>
        /// <see cref="GetProjectKey"/> plus the document's path (the project folder defaults
        /// to the model's own folder; "" when unsaved or unknown). Read from the tracker, not
        /// over HTTP: the pane runs on Revit's UI thread, and a request to our own server from
        /// here can never be served (its ExternalEvent needs this very thread) — it only ever
        /// ran into its timeout. The tracker is updated on that thread (view activation,
        /// every command).
        /// </summary>
        private string GetProjectIdentity(out string documentPath)
        {
            var title = ActiveDocumentTracker.Title;
            if (!string.IsNullOrWhiteSpace(title))
            {
                _lastProjectKey = title;
                documentPath = ActiveDocumentTracker.PathName;
                return title;
            }
            documentPath = "";
            return _lastProjectKey ?? "default";
        }

        private void SendCurrentMessage()
        {
            var text = _inputBox.Text?.Trim();
            bool hasAttachments = _pendingAttachments.Count > 0;
            if ((string.IsNullOrEmpty(text) && !hasAttachments) || _isGenerating)
                return;

            // Re-check availability (API key may have been configured via Settings)
            if (_backend != null && _backend.IsAvailable)
                UpdateStatus();

            if (_backend == null || !_backend.IsAvailable)
            {
                var msg = _backend?.StatusMessage ?? Strings.Get(Strings.BackendNotInitialized);
                AddMessage(new ChatMessage(ChatRole.System, msg));
                ChatHistory.Add("System", msg);
                if (_session != null)
                {
                    _session.Append(ChatSession.RoleSystem, msg);
                    ChatSessionStore.Default.SaveSession(_session);
                }
                return;
            }

            ClearQuickReplies();
            _readyHint.Visibility = Visibility.Collapsed;
            _inputBox.Text = "";
            if (text == null) text = "";

            // Persist pending attachments into the per-project store and build the
            // list handed to the backend. A file that fails to copy is reported and
            // skipped rather than blocking the message.
            List<ChatAttachment> attachments = null;
            if (hasAttachments)
            {
                attachments = new List<ChatAttachment>();
                var projectKey = GetProjectKey();
                foreach (var pending in _pendingAttachments)
                {
                    try
                    {
                        var att = ChatAttachment.FromFile(pending.SourcePath);
                        att.StoredPath = pending.AlreadyStored
                            ? pending.SourcePath
                            : AttachmentStore.StoreFile(pending.SourcePath, projectKey);
                        attachments.Add(att);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("Failed to store attachment: " + pending.SourcePath, ex);
                        AddMessage(new ChatMessage(ChatRole.System,
                            "Could not attach " + Path.GetFileName(pending.SourcePath) + ": " + ex.Message));
                    }
                }
                _pendingAttachments.Clear();
                RebuildAttachmentChips();
                if (attachments.Count == 0)
                {
                    attachments = null;
                    if (string.IsNullOrEmpty(text))
                        return; // nothing left to send
                }
            }

            // User message
            var userMsg = new ChatMessage(ChatRole.User, text) { Attachments = attachments };
            AddMessage(userMsg);
            var historyText = attachments == null
                ? text
                : (string.IsNullOrEmpty(text) ? "" : text + "\n") +
                  "[attached: " + string.Join(", ", attachments.Select(a => a.FileName)) + "]";
            ChatHistory.Add("You", historyText);

            // Lazily start the persisted session on the first user message.
            if (_session == null)
                _session = ChatSession.Start(ActiveDocumentTracker.Title, BackendLabel());
            _session.Append(ChatSession.RoleUser, text);

            // Capture the session that owns this turn. The completion/error callbacks
            // arrive via the Dispatcher and may fire AFTER the user switched to a new
            // chat or loaded another session — recording against the captured session
            // keeps a late reply/error in the conversation it belongs to.
            var sessionForTurn = _session;

            // The activity block shows at once ("Aan het denken…" + counter) — the gap before
            // the first token is the most confusing moment of a turn.
            var turn = new TurnUi { Model = new TurnActivity(DateTime.UtcNow) };
            turn.Block = new ActivityBlockView(turn.Model);
            _turn = turn;
            _messagePanel.Children.Add(turn.Block);
            ScrollToBottomIfNeeded();

            SetGenerating(true);
            StartStreamingRenderTimer();

            _cts = new CancellationTokenSource();

            // Tool steps arrive on a side channel (UI-only); the token stream stays as it was.
            var activitySource = _backend as IChatActivitySource;
            if (activitySource != null)
            {
                activitySource.ActivitySink = evt =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_turn != turn) return;
                        turn.Model.Apply(evt, DateTime.UtcNow);
                        turn.ActivityDirty = true;
                    }));
                };
            }

            _backend.SendMessage(
                text,
                attachments,
                onToken: delta =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_turn != turn) return;

                        // Cheap: just accrue. The render timer flushes to the UI on a
                        // fixed cadence, so per-token work stays O(1).
                        turn.Raw.Append(delta);
                        turn.Model.AppendText(delta, DateTime.UtcNow);
                        turn.TextDirty = true;
                    }));
                },
                onComplete: fullText =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_turn == turn)
                        {
                            // Stored exactly as before: the full raw stream (status lines,
                            // intermediate text and all). Only the display is split up.
                            var content = turn.Raw.Length > 0
                                ? turn.Raw.ToString()
                                : fullText;
                            var cancelled = fullText == "[Cancelled]";

                            turn.Model.Finish(DateTime.UtcNow, cancelled, content);
                            var answer = turn.Model.Answer;
                            if (answer.Length == 0 && cancelled)
                                answer = fullText;
                            EndTurnDisplay(turn, answer);

                            ChatHistory.Add("Claude", content);
                            sessionForTurn.Append(ChatSession.RoleAssistant, content);
                            ChatSessionStore.Default.SaveSession(sessionForTurn);
                            // Quick replies: a separate cheap call, never on the token stream.
                            if (ReferenceEquals(sessionForTurn, _session))
                                RequestQuickReplies(sessionForTurn, text, answer, cancelled, turn.Model.ToolNames);
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
                        if (_turn == turn)
                        {
                            turn.Model.Finish(DateTime.UtcNow, cancelled: true);
                            EndTurnDisplay(turn, null);
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

        // The turn ended: collapse the activity block to its summary line (or drop it when the
        // turn ran no tools) and render the answer as normal assistant markdown below it.
        // A null answer (error) removes the answer bubble.
        private void EndTurnDisplay(TurnUi turn, string answer)
        {
            if (!turn.Block.Collapse())
                _messagePanel.Children.Remove(turn.Block);

            if (string.IsNullOrEmpty(answer))
            {
                if (turn.AnswerBorder != null)
                    _messagePanel.Children.Remove(turn.AnswerBorder);
                return;
            }

            EnsureAnswerBubble(turn);
            turn.AnswerMessage.Content = answer; // Copy copies the answer, not the step chatter
            turn.AnswerPanel.Children.Remove(turn.AnswerText);
            AddRenderedContent(turn.AnswerPanel, answer);
            turn.AnswerBorder.Visibility = Visibility.Visible;
        }

        private void EnsureAnswerBubble(TurnUi turn)
        {
            if (turn.AnswerBorder != null) return;

            turn.AnswerMessage = new ChatMessage(ChatRole.Assistant, "");
            StackPanel contentPanel;
            turn.AnswerBorder = CreateMessageChrome(turn.AnswerMessage, out contentPanel);
            turn.AnswerPanel = contentPanel;
            turn.AnswerText = new TextBlock
            {
                Foreground = FgPrimary,
                FontSize = 13,
                LineHeight = 20,
                TextWrapping = TextWrapping.Wrap
            };
            contentPanel.Children.Add(turn.AnswerText);
            _messagePanel.Children.Add(turn.AnswerBorder);
        }

        // Flushes the turn to the UI at a fixed cadence (~25fps) rather than on every token,
        // so a long response renders in O(length) total, not O(length²).
        private void StartStreamingRenderTimer()
        {
            if (_streamingRenderTimer == null)
            {
                _streamingRenderTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(40)
                };
                _streamingRenderTimer.Tick += (s, e) => FlushTurn();
            }
            if (_clockTimer == null)
            {
                _clockTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(1)
                };
                _clockTimer.Tick += (s, e) => _turn?.Block.Tick(DateTime.UtcNow);
            }
            _streamingRenderTimer.Start();
            _clockTimer.Start();
        }

        private void FlushTurn()
        {
            var turn = _turn;
            if (turn == null || (!turn.TextDirty && !turn.ActivityDirty)) return;

            // Text since the last tool call streams in the answer bubble; when a tool call
            // follows, the model moves it into the block as a muted note and the bubble empties.
            var live = turn.Model.LiveText;
            if (live.Length > 0)
            {
                EnsureAnswerBubble(turn);
                turn.AnswerText.Text = live + " …";
                turn.AnswerBorder.Visibility = Visibility.Visible;
            }
            else if (turn.AnswerBorder != null)
            {
                turn.AnswerBorder.Visibility = Visibility.Collapsed;
            }

            // Header state ("Antwoord schrijven…") depends on the text too.
            turn.Block.Refresh(DateTime.UtcNow);
            turn.TextDirty = false;
            turn.ActivityDirty = false;
            ScrollToBottomIfNeeded();
        }

        private void ClearStreamingState()
        {
            _streamingRenderTimer?.Stop();
            _clockTimer?.Stop();
            if (_turn != null)
            {
                _turn.Block.StopAnimation();
                _turn = null;
            }
            var activitySource = _backend as IChatActivitySource;
            if (activitySource != null)
                activitySource.ActivitySink = null;
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

            ClosePopup();
            ClearQuickReplies();
            _readyHint.Visibility = Visibility.Collapsed;
            _messagePanel.Children.Clear();
            _userTurnCount = 0;
            ClearStreamingState();
            _infoBanner = null;
            // Pending chips clear; stored copies stay on disk as the project archive.
            _pendingAttachments.Clear();
            RebuildAttachmentChips();
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
            if (_isGenerating)
                CancelGeneration();

            // Persist the conversation we're leaving BEFORE reading the target file.
            // Order matters: the user may re-open the chat that is already current,
            // and reading first would resurrect a stale copy (dropping any message
            // not yet flushed, e.g. the in-flight user turn) that the next save
            // would then write back over the file.
            ChatSessionStore.Default.SaveSession(_session);

            var loaded = ChatSessionStore.Default.LoadSession(id);
            if (loaded == null)
            {
                AddMessage(new ChatMessage(ChatRole.System,
                    "Could not load that chat - the file may have been deleted."));
                return;
            }

            ClosePopup();
            ClearQuickReplies();
            _readyHint.Visibility = Visibility.Collapsed;
            _messagePanel.Children.Clear();
            _userTurnCount = 0;
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
            var title = GetProjectIdentity(out var documentPath);
            // No open document: the project-folder row stays read-only.
            var hasDocument = !string.IsNullOrWhiteSpace(ActiveDocumentTracker.Title);
            var dialog = new SettingsDialog(hasDocument ? title : null, documentPath);
            var result = dialog.ShowDialog();
            // Language or project folder may have changed.
            RefreshWelcomeCard();

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
            _attachButton.IsEnabled = !generating;
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

            // A thin rule between turns, so a long conversation reads as separate exchanges.
            if (message.Role == ChatRole.User)
            {
                if (_userTurnCount > 0)
                {
                    _messagePanel.Children.Add(new Border
                    {
                        Height = 1,
                        Background = BorderColor,
                        Margin = new Thickness(24, 10, 24, 6),
                        SnapsToDevicePixels = true
                    });
                }
                _userTurnCount++;
            }

            StackPanel contentPanel;
            var border = CreateMessageChrome(message, out contentPanel);
            AddRenderedContent(contentPanel, message.Content);

            // Attachment chip line under the message text
            if (message.Attachments != null && message.Attachments.Count > 0)
            {
                foreach (var att in message.Attachments)
                {
                    contentPanel.Children.Add(new TextBlock
                    {
                        Text = "📎 " + att.FileName + " (" + att.FormatSize() + ")",
                        Foreground = FgSecondary,
                        FontSize = 11,
                        Margin = new Thickness(0, 4, 0, 0),
                        TextWrapping = TextWrapping.Wrap
                    });
                }
            }

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

        // A RichTextBox lays its document out a few px narrower than itself.
        private const double RichTextBoxInset = 12;

        private RichTextBox CreateMarkdownRichTextBox(string markdown)
        {
            var doc = MarkdownHelper.ToFlowDocument(markdown, new MarkdownRenderOptions { OnCitationClick = OpenCitation });

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

            // Tables size their columns to the width the answer really gets.
            rtb.SizeChanged += (s, e) =>
            {
                if (e.WidthChanged)
                    Markdown.Renderers.TableRenderer.FitTables(doc, rtb.ActualWidth - RichTextBoxInset);
            };

            return rtb;
        }

        // --- Quick replies (the chip row above the input) ---

        private Border CreateSuggestionBar()
        {
            _suggestionPanel = new WrapPanel { Orientation = Orientation.Horizontal };
            _typingTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _typingTimer.Tick += (s, e) =>
            {
                // One second after typing started (not restarted per key): the row steps aside.
                _typingTimer.Stop();
                if (!string.IsNullOrEmpty(_inputBox.Text)) _suggestionBar.Visibility = Visibility.Collapsed;
            };
            return new Border
            {
                Background = BgDark,
                Padding = new Thickness(8, 4, 8, 0),
                Visibility = Visibility.Collapsed,
                RenderTransform = new TranslateTransform(),
                Child = _suggestionPanel
            };
        }

        // After a completed answer: ask the cheap model (off the UI thread) what the user was
        // about to type. Every rule of the row lives in QuickReplyService; this only wires it.
        private void RequestQuickReplies(ChatSession session, string question, string answer, bool cancelled,
            IReadOnlyList<string> toolsCalled)
        {
            ClearQuickReplies();
            if (session == null || !QuickReplyService.ShouldOffer(answer, cancelled, false)) return;
            var model = SuggestionModels.For(_backend);
            if (model == null) return;

            var context = new QuickReplyContext
            {
                ActiveView = ActiveDocumentTracker.ActiveViewName,
                Phase = ActiveViewPhase(),
                Selection = ActiveDocumentTracker.SelectedCount == 0
                    ? "nothing selected"
                    : ActiveDocumentTracker.SelectedCount + " element(s) selected",
                ToolsCalled = toolsCalled
            };
            var request = new QuickReplyRequest
            {
                SessionId = session.Id,
                MessageIndex = session.Messages.Count,
                Language = Strings.Current,
                SystemPrompt = QuickReplyService.BuildSystemPrompt(context),
                UserInput = QuickReplyService.BuildUserInput(session.Messages.ToList(), question, answer)
            };
            _quickReplyRequest = request;
            QuickReplyService.GenerateAsync(model, request,
                (r, replies) => Dispatcher.BeginInvoke(new Action(() => ApplyQuickReplies(r, replies))));
        }

        // The active view's phase as the TEMPLATE block states it ("… on phase '2. Bouwfase'").
        private static string ActiveViewPhase()
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                Services.Standards.StandardsPrompt.TemplateBlock ?? "", @"- active view: .*? on phase '([^']+)'");
            return m.Success ? m.Groups[1].Value : null;
        }

        // A set that arrives late is dropped: a newer answer, another chat, a turn running, or
        // the user already typing.
        private void ApplyQuickReplies(QuickReplyRequest request, IReadOnlyList<QuickReply> replies)
        {
            if (!ReferenceEquals(request, _quickReplyRequest)) return;
            _quickReplyRequest = null;
            if (_session == null || _session.Id != request.SessionId || _session.Messages.Count != request.MessageIndex ||
                _isGenerating)
                return;
            if (!string.IsNullOrEmpty(_inputBox.Text))
            {
                Logger.Info("Quick replies: dropped - the user is already typing");
                return;
            }
            if (replies == null || replies.Count == 0) return;
            _quickReplies = replies;
            RenderQuickReplies();
        }

        private void RenderQuickReplies()
        {
            _suggestionPanel.Children.Clear();
            var language = Strings.Current;
            foreach (var reply in _quickReplies)
            {
                var captured = reply;
                // A chip that changes the model carries a pencil: it fills the box, you press Send.
                var chip = CreateChip((reply.Commits ? "✎ " : "") + reply.Label,
                    Strings.For(language, reply.Commits ? Strings.QuickReplyFillsTip : Strings.QuickReplySendsTip, reply.Prompt));
                chip.Click += (s, e) => OnQuickReplyChosen(captured);
                System.Windows.Automation.AutomationProperties.SetAutomationId(chip, reply.Commits ? "QuickReplyChipCommit" : "QuickReplyChip");
                _suggestionPanel.Children.Add(chip);
            }
            ShowSuggestionBar();
        }

        private void ShowSuggestionBar()
        {
            if (_quickReplies == null || _quickReplies.Count == 0 || _isGenerating) return;
            var wasVisible = _suggestionBar.Visibility == Visibility.Visible;
            _suggestionBar.Visibility = Visibility.Visible;
            if (wasVisible) return;
            // Slide in: a short fade with a small upward move.
            var duration = TimeSpan.FromMilliseconds(120);
            _suggestionBar.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, duration));
            ((TranslateTransform)_suggestionBar.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
                new System.Windows.Media.Animation.DoubleAnimation(6, 0, duration));
            ScrollToBottomIfNeeded();
        }

        private void ClearQuickReplies()
        {
            _quickReplies = null;
            _quickReplyRequest = null;
            _typingTimer?.Stop();
            _suggestionPanel?.Children.Clear();
            if (_suggestionBar != null) _suggestionBar.Visibility = Visibility.Collapsed;
        }

        // Tap: a plain reply is sent at once; one that changes the model is typed into the box
        // for the user to check and send.
        private void OnQuickReplyChosen(QuickReply reply)
        {
            if (_isGenerating) return;
            ClearQuickReplies();
            if (reply.Commits)
            {
                _inputBox.Text = reply.Prompt;
                _inputBox.Focus();
                _inputBox.CaretIndex = _inputBox.Text.Length;
                _readyHintText = reply.Prompt;
                _readyHint.Text = Strings.Get(Strings.QuickReplyReadyHint);
                _readyHint.Visibility = Visibility.Visible;
                return;
            }
            _inputBox.Text = reply.Prompt;
            SendCurrentMessage();
        }

        private void OnInputTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_readyHint.Visibility == Visibility.Visible && _inputBox.Text != _readyHintText)
                _readyHint.Visibility = Visibility.Collapsed;

            if (_quickReplies == null) return;
            if (string.IsNullOrEmpty(_inputBox.Text))
            {
                // The box is empty again: the row comes back.
                _typingTimer.Stop();
                ShowSuggestionBar();
            }
            else if (_suggestionBar.Visibility == Visibility.Visible && !_typingTimer.IsEnabled)
            {
                _typingTimer.Start();
            }
        }

        // --- In-pane popup (a source, the examples list) ---

        // One popup at a time over the message area: at most ~60% of the pane high, scrollable
        // body; Esc, the × or a click outside closes it.
        private void ShowPopup(string title, FrameworkElement body)
        {
            ClosePopup();
            var maxHeight = Math.Max(160, ActualHeight * 0.6);

            var header = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(new TextBlock
            {
                Text = title,
                ToolTip = title,
                Foreground = AccentBlue,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            });
            var close = CreateHeaderButton("×");
            close.FontSize = 14;
            close.Padding = new Thickness(6, 0, 2, 0);
            close.Margin = new Thickness(4, 0, 0, 0);
            close.ToolTip = Strings.Get(Strings.SourceClose);
            close.Click += (s, e) => ClosePopup();
            Grid.SetColumn(close, 1);
            header.Children.Add(close);

            var dock = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(header, Dock.Top);
            dock.Children.Add(header);
            body.MaxHeight = maxHeight - 40;
            dock.Children.Add(body);

            var border = new Border
            {
                Background = BgPanel,
                BorderBrush = AccentBlue,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6, 10, 8),
                Width = Math.Max(220, _scrollViewer.ActualWidth - 16),
                MaxHeight = maxHeight,
                Focusable = true,
                Child = dock
            };
            border.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) { ClosePopup(); e.Handled = true; }
            };

            var popup = new Popup
            {
                Child = border,
                PlacementTarget = _scrollViewer,
                Placement = PlacementMode.Relative,
                HorizontalOffset = 8,
                VerticalOffset = 8,
                StaysOpen = false,
                AllowsTransparency = true
            };
            popup.Closed += (s, e) => { if (_popup == popup) _popup = null; };
            _popup = popup;
            popup.IsOpen = true;
            border.Focus();
        }

        private void ClosePopup()
        {
            var popup = _popup;
            _popup = null;
            if (popup != null) popup.IsOpen = false;
        }

        // A [bron: …] marker was clicked: leidraad/standaard → the standards text, document →
        // readdoc (scrolled to the page/section), revit <id> → select + zoom in Revit.
        // The HTTP calls run off the UI thread: the server's ExternalEvent needs this thread.
        private void OpenCitation(Citation citation)
        {
            if (!citation.TryGetLookup(out var command, out var args)) return;
            var language = Strings.Current;
            var port = (_backend as ChatBackendBase)?.HttpPort ?? 0;

            var text = new TextBox
            {
                Text = Strings.For(language, Strings.SourceLoading),
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                Background = Brushes.Transparent,
                Foreground = FgPrimary,
                CaretBrush = FgPrimary,
                BorderThickness = new Thickness(0),
                FontSize = 12,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            ShowPopup(citation.Source, text);
            var popup = _popup;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                string shown;
                try
                {
                    if (port == 0) throw new InvalidOperationException(Strings.For(language, Strings.BackendNotInitialized));
                    var response = CitationSourceText.Clean(GetFromRevit(port, command, args));
                    if (citation.Kind == CitationKind.Revit && !response.StartsWith("ERROR", StringComparison.Ordinal))
                    {
                        GetFromRevit(port, "zoomto", args);
                        shown = Strings.For(language, Strings.SourceSelected, args);
                    }
                    else
                    {
                        shown = response;
                    }
                }
                catch (Exception ex)
                {
                    shown = Strings.For(language, Strings.SourceFailed, ex.Message);
                }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_popup != popup) return;
                    var index = CitationSourceText.FindLocation(shown, citation, out var length);
                    var note = index < 0 && (citation.Page != null || citation.Section != null)
                        ? Strings.For(language, Strings.SourceMatchNotFound,
                            citation.Page != null ? "p." + citation.Page : "§" + citation.Section) + "\n\n"
                        : "";
                    text.Text = note + shown;
                    if (index < 0) return;
                    text.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
                    {
                        // To the end first, then back: the matched line lands at the top.
                        text.ScrollToEnd();
                        text.ScrollToLine(Math.Max(0, text.GetLineIndexFromCharacterIndex(index)));
                        text.Select(index, length);
                    }));
                }));
            });
        }

        // GET /<command>?args=… on the local VibeModel server (text output), with the token header.
        private static string GetFromRevit(int port, string command, string args)
        {
            using (var client = new System.Net.WebClient { Encoding = Encoding.UTF8 })
            {
                var token = Environment.GetEnvironmentVariable(RevitHttpServer.TokenEnvVar);
                if (!string.IsNullOrWhiteSpace(token))
                    client.Headers[RevitHttpServer.TokenHeader] = token.Trim();
                var url = RevitHttpServer.BaseUrl(port) + "/" + command +
                          (string.IsNullOrEmpty(args) ? "" : "?args=" + Uri.EscapeDataString(args));
                return client.DownloadString(url);
            }
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
            ActiveDocumentTracker.Changed -= OnActiveDocumentChanged;
            _cts?.Cancel();
            _backend?.Cancel();
            // Persist whatever conversation is open when Revit shuts down.
            ChatSessionStore.Default.SaveSession(_session);
        }
    }
}
