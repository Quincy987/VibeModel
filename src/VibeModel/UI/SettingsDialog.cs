using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VibeModel.Infrastructure;
using VibeModel.Services.Chat;

namespace VibeModel.UI
{
    public class SettingsDialog : Window
    {
        private static readonly SolidColorBrush BgDark = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
        private static readonly SolidColorBrush BgInput = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        private static readonly SolidColorBrush BgPanel = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25));
        private static readonly SolidColorBrush FgPrimary = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
        private static readonly SolidColorBrush FgSecondary = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
        private static readonly SolidColorBrush AccentBlue = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0));
        private static readonly SolidColorBrush BgButton = new SolidColorBrush(Color.FromRgb(0x0E, 0x63, 0x9C));
        private static readonly SolidColorBrush BgButtonHover = new SolidColorBrush(Color.FromRgb(0x1E, 0x73, 0xAC));
        private static readonly SolidColorBrush BorderColor = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));
        private static readonly SolidColorBrush FgError = new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36));
        private static readonly SolidColorBrush FgSuccess = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0));
        private static readonly SolidColorBrush BgSelectedTab = new SolidColorBrush(Color.FromRgb(0x0E, 0x63, 0x9C));
        private static readonly SolidColorBrush BgUnselectedTab = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30));

        // Backend selector
        private Button _tabCli;
        private Button _tabApi;
        private Button _tabLocal;
        private string _selectedBackend;

        // Config panels
        private StackPanel _cliPanel;
        private StackPanel _apiPanel;
        private StackPanel _localPanel;

        // Anthropic API fields
        private PasswordBox _apiKeyBox;
        private string _initialApiKey;
        private TextBox _apiModelBox;
        private TextBox _apiBaseUrlBox;
        private string _originalBackend;
        private bool _tabClicked;
        private Button _validateApiButton;

        // Local LLM fields
        private TextBox _endpointBox;
        private TextBox _modelBox;
        private CheckBox _toolUseCheck;
        private Slider _timeoutSlider;
        private TextBlock _timeoutLabel;
        private Button _testConnectionButton;

        // Shared
        private TextBlock _statusText;
        private Button _saveButton;

        // Chat language ("nl" / "en")
        private ComboBox _languageCombo;

        // Office standards
        private CheckBox _standardsEnabledCheck;
        private CheckBox _projectMemoryCheck;
        private TextBox _packPathBox;
        private TextBox _standardsFolderBox;

        // Project folder (plan 10) — edits the entry of the current project only.
        private readonly string _projectKey;
        private readonly string _documentPath;
        private string _projectFolderOverride;   // pending value; "" = cleared
        private TextBox _projectFolderBox;
        private TextBlock _projectFolderSource;

        /// <summary>True if the user changed the backend selection (caller should reset chat).</summary>
        public bool BackendChanged { get; private set; }

        /// <param name="documentTitle">Active document title (the project key); null when unknown.</param>
        /// <param name="documentPath">Its Document.PathName — the default project folder is its directory.</param>
        public SettingsDialog(string documentTitle = null, string documentPath = null)
        {
            _projectKey = string.IsNullOrWhiteSpace(documentTitle) ? null : Services.Chat.ProjectDocs.ProjectKey(documentTitle);
            _documentPath = documentPath ?? "";
            _projectFolderOverride = _projectKey != null ? SettingsManager.GetProjectContextFolderSetting(_projectKey) ?? "" : "";

            Title = "VibeModel Settings";
            Width = 500;
            // Tall enough for the office-standards section, never taller than the screen (the
            // content scrolls instead).
            Height = Math.Min(700, SystemParameters.WorkArea.Height - 40);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;
            Background = BgDark;

            _selectedBackend = SettingsManager.GetPreferredBackend();
            _originalBackend = _selectedBackend == "direct" ? "anthropic-api" : _selectedBackend;
            // Normalize legacy/default values to a valid tab key
            if (_selectedBackend == "direct" || _selectedBackend == "auto")
                _selectedBackend = "anthropic-api";

            var mainPanel = new StackPanel { Margin = new Thickness(24, 16, 24, 16) };

            // Title
            mainPanel.Children.Add(new TextBlock
            {
                Text = "AI Backend",
                Foreground = FgPrimary,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 12)
            });

            // Tab bar
            mainPanel.Children.Add(CreateTabBar());
            if (ManagedSettings.IsManaged(ManagedSettings.PreferredBackend))
                mainPanel.Children.Add(CreateLabel("Backend: " + SettingsManager.GetPreferredBackend() + " - " + Strings.Get(Strings.SettingManaged)));

            // Config panels (stacked, visibility toggled)
            _cliPanel = CreateCliPanel();
            _apiPanel = CreateApiPanel();
            _localPanel = CreateLocalPanel();

            mainPanel.Children.Add(_cliPanel);
            mainPanel.Children.Add(_apiPanel);
            mainPanel.Children.Add(_localPanel);

            // Chat language — independent of the backend tabs.
            mainPanel.Children.Add(CreateLanguagePanel());

            // Project folder (plan 10) — this model's reference documents.
            mainPanel.Children.Add(CreateProjectFolderPanel(documentTitle));

            // Office standards (plan 11) — independent of the backend tabs.
            mainPanel.Children.Add(CreateStandardsPanel());

            // Status text
            _statusText = new TextBlock
            {
                Text = "",
                Foreground = FgSecondary,
                FontSize = 12,
                Margin = new Thickness(0, 12, 0, 8),
                TextWrapping = TextWrapping.Wrap
            };
            mainPanel.Children.Add(_statusText);

            // Save button
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            _saveButton = CreateButton("Save");
            _saveButton.Click += OnSaveClick;
            buttonPanel.Children.Add(_saveButton);
            mainPanel.Children.Add(buttonPanel);

            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = mainPanel
            };
            UpdateTabVisuals();
        }

        // --- Tab bar ---

        private StackPanel CreateTabBar()
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 16)
            };

            _tabCli = CreateTabButton("Claude CLI", "claude-cli");
            _tabApi = CreateTabButton("Anthropic API", "anthropic-api");
            _tabLocal = CreateTabButton("Local LLM", "local-llm");

            panel.Children.Add(_tabCli);
            panel.Children.Add(_tabApi);
            panel.Children.Add(_tabLocal);

            return panel;
        }

        private Button CreateTabButton(string text, string backendKey)
        {
            var btn = new Button
            {
                Content = text,
                Foreground = Brushes.White,
                Background = BgUnselectedTab,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(14, 6, 14, 6),
                FontSize = 12,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 4, 0),
                Tag = backendKey
            };
            btn.Click += (s, e) =>
            {
                _selectedBackend = backendKey;
                _tabClicked = true;
                UpdateTabVisuals();
            };
            return btn;
        }

        private void UpdateTabVisuals()
        {
            _tabCli.Background = _selectedBackend == "claude-cli" ? BgSelectedTab : BgUnselectedTab;
            _tabApi.Background = _selectedBackend == "anthropic-api" ? BgSelectedTab : BgUnselectedTab;
            _tabLocal.Background = _selectedBackend == "local-llm" ? BgSelectedTab : BgUnselectedTab;

            _cliPanel.Visibility = _selectedBackend == "claude-cli" ? Visibility.Visible : Visibility.Collapsed;
            _apiPanel.Visibility = _selectedBackend == "anthropic-api" ? Visibility.Visible : Visibility.Collapsed;
            _localPanel.Visibility = _selectedBackend == "local-llm" ? Visibility.Visible : Visibility.Collapsed;

            _statusText.Text = "";
        }

        // --- Claude CLI panel ---

        private StackPanel CreateCliPanel()
        {
            var panel = new StackPanel();

            panel.Children.Add(new TextBlock
            {
                Text = "Claude Code CLI",
                Foreground = FgPrimary,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8)
            });

            panel.Children.Add(new TextBlock
            {
                Text = "Uses the Claude Code CLI (your own Claude subscription) for full agentic capabilities.\n" +
                       "Install it without administrator rights: the chat's Connect to Claude card, or in PowerShell:\n" +
                       ClaudeSetup.InstallCommand,
                Foreground = FgSecondary,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            });

            // Detection status: the same search the backend uses (.local\bin, a fresh PATH, the npm
            // folders). It spawns `claude --version`, so it runs off the UI thread.
            var status = new TextBlock
            {
                Text = "Looking for Claude Code…",
                Foreground = FgSecondary,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4)
            };
            panel.Children.Add(status);
            if (SettingsManager.IsClaudeCodeDisabled())
            {
                status.Text = "Switched off on this computer (" + Strings.Get(Strings.SettingManaged) + ")";
                status.Foreground = FgError;
                return panel;
            }
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                var path = ClaudeCodeBackend.LocateCli(out var version);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    status.Text = path != null ? "Detected: Claude Code " + version + "\n" + path : "Not detected";
                    status.Foreground = path != null ? FgSuccess : FgError;
                }));
            });

            return panel;
        }

        // --- Language panel ---

        private StackPanel CreateLanguagePanel()
        {
            var panel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
            panel.Children.Add(new TextBlock
            {
                Text = "Taal / Language",
                Foreground = FgPrimary,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4)
            });
            panel.Children.Add(CreateLabel(
                "The chat replies in the language you write in; this is the fallback, and the language of the pane's own messages."));

            _languageCombo = new ComboBox
            {
                FontSize = 13,
                Width = 200,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 4, 0, 4)
            };
            _languageCombo.Items.Add(new ComboBoxItem { Content = "Nederlands", Tag = Services.Chat.Strings.Dutch });
            _languageCombo.Items.Add(new ComboBoxItem { Content = "English", Tag = Services.Chat.Strings.English });
            _languageCombo.SelectedIndex = Services.Chat.Strings.IsEnglish(SettingsManager.GetLanguage()) ? 1 : 0;
            panel.Children.Add(_languageCombo);
            if (ManagedSettings.IsManaged(ManagedSettings.Language))
            {
                _languageCombo.IsEnabled = false;
                panel.Children.Add(CreateLabel(Strings.Get(Strings.SettingManaged)));
            }
            return panel;
        }

        // --- Project folder panel (plan 10) ---

        private StackPanel CreateProjectFolderPanel(string documentTitle)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
            panel.Children.Add(new TextBlock
            {
                Text = "Project folder",
                Foreground = FgPrimary,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4)
            });
            panel.Children.Add(CreateLabel(
                "Reference documents the chat can list and read (bestek, memo's, DO-tekening, reports). " +
                "Default: the folder the model is in. " +
                (_projectKey != null ? "Applies to: " + documentTitle : "Open a model to set one.")));

            _projectFolderBox = CreateTextBox("");
            _projectFolderBox.IsReadOnly = true;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(_projectFolderBox, 0);
            grid.Children.Add(_projectFolderBox);

            var browse = CreateButton("Browse…");
            browse.MinWidth = 0;
            browse.Margin = new Thickness(6, 0, 0, 4);
            browse.IsEnabled = _projectKey != null;
            browse.Click += (s, e) =>
            {
                // A file dialog that accepts a folder: navigate into it and press Open.
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    CheckFileExists = false,
                    CheckPathExists = true,
                    ValidateNames = false,
                    FileName = "Select this folder",
                    Title = "Open the project folder, then click Open"
                };
                var current = _projectFolderBox.Text;
                if (!string.IsNullOrWhiteSpace(current) && System.IO.Directory.Exists(current))
                    dlg.InitialDirectory = current;
                if (dlg.ShowDialog(this) == true)
                {
                    var picked = System.IO.Directory.Exists(dlg.FileName)
                        ? dlg.FileName
                        : System.IO.Path.GetDirectoryName(dlg.FileName);
                    _projectFolderOverride = picked ?? "";
                    UpdateProjectFolderRow();
                }
            };
            Grid.SetColumn(browse, 1);
            grid.Children.Add(browse);

            var clear = CreateButton("Clear");
            clear.MinWidth = 0;
            clear.Margin = new Thickness(6, 0, 0, 4);
            clear.IsEnabled = _projectKey != null;
            clear.Click += (s, e) =>
            {
                _projectFolderOverride = "";
                UpdateProjectFolderRow();
            };
            Grid.SetColumn(clear, 2);
            grid.Children.Add(clear);
            panel.Children.Add(grid);

            _projectFolderSource = new TextBlock
            {
                Foreground = FgSecondary,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4)
            };
            panel.Children.Add(_projectFolderSource);
            UpdateProjectFolderRow();

            // Project memory + journal (plan 08) live in that folder's VibeModel subfolder.
            _projectMemoryCheck = new CheckBox
            {
                Content = Services.Chat.Strings.Get(Services.Chat.Strings.SettingProjectMemory),
                IsChecked = SettingsManager.GetProjectMemoryEnabled(),
                Foreground = FgPrimary,
                FontSize = 12,
                Margin = new Thickness(0, 6, 0, 2)
            };
            panel.Children.Add(_projectMemoryCheck);
            panel.Children.Add(CreateLabel(Services.Chat.Strings.Get(Services.Chat.Strings.SettingProjectMemoryHint)));
            return panel;
        }

        // Shows the folder that will be in force (pending choice + model default) and its source.
        private void UpdateProjectFolderRow()
        {
            var live = SettingsManager.LiveFolderOrNull(_projectFolderOverride);
            var folder = Services.Chat.ProjectDocs.Resolve(live, _documentPath);
            _projectFolderBox.Text = folder.IsSet ? folder.Path : "";
            var text = folder.IsSet
                ? folder.SourceLabel
                : "Not set — this model has no folder on disk (not saved yet, or a cloud model). Browse to pick one.";
            if (!string.IsNullOrWhiteSpace(_projectFolderOverride) && live == null)
                text = "Configured folder not found: " + _projectFolderOverride + " — " +
                       (folder.IsSet ? "using " + folder.SourceLabel : "no folder") + ". Browse or Clear.";
            _projectFolderSource.Text = text;
        }

        // --- Office standards panel ---

        private StackPanel CreateStandardsPanel()
        {
            var panel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
            panel.Children.Add(new TextBlock
            {
                Text = "Office standards",
                Foreground = FgPrimary,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4)
            });

            _standardsEnabledCheck = new CheckBox
            {
                Content = "Apply office standards (rules in chat, default phase, checks)",
                IsChecked = SettingsManager.GetStandardsEnabled(),
                Foreground = FgPrimary,
                FontSize = 12,
                Margin = new Thickness(0, 4, 0, 6)
            };
            panel.Children.Add(_standardsEnabledCheck);

            panel.Children.Add(CreateLabel("Standards pack folder (pack.json, or a folder of packs). Empty = the local copy in %LOCALAPPDATA%\\VibeModel\\standards. A network folder is mirrored locally in the background."));
            _packPathBox = CreateTextBox(SettingsManager.GetStandardsPackPath());
            panel.Children.Add(CreateFolderRow(_packPathBox, "pack.json|pack.json|All files|*.*"));

            panel.Children.Add(CreateLabel("Office source documents folder (the modelling manual PDFs). Empty = the pack's default."));
            _standardsFolderBox = CreateTextBox(SettingsManager.GetStandardsFolder());
            panel.Children.Add(CreateFolderRow(_standardsFolderBox, "Documents|*.pdf;*.txt;*.md|All files|*.*"));
            return panel;
        }

        // Text box + Browse (pick any file in the folder) + Clear. A file dialog avoids a
        // WinForms dependency just for a folder picker.
        private Grid CreateFolderRow(TextBox box, string filter)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(box, 0);
            grid.Children.Add(box);

            var browse = CreateButton("Browse…");
            browse.MinWidth = 0;
            browse.Margin = new Thickness(6, 0, 0, 4);
            browse.Click += (s, e) =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = filter,
                    CheckFileExists = true,
                    Title = "Pick a file inside the folder"
                };
                if (dlg.ShowDialog(this) == true)
                    box.Text = System.IO.Path.GetDirectoryName(dlg.FileName);
            };
            Grid.SetColumn(browse, 1);
            grid.Children.Add(browse);

            var clear = CreateButton("Clear");
            clear.MinWidth = 0;
            clear.Margin = new Thickness(6, 0, 0, 4);
            clear.Click += (s, e) => box.Text = "";
            Grid.SetColumn(clear, 2);
            grid.Children.Add(clear);
            return grid;
        }

        // --- Anthropic API panel ---

        private StackPanel CreateApiPanel()
        {
            var panel = new StackPanel();

            panel.Children.Add(new TextBlock
            {
                Text = "Anthropic API Key",
                Foreground = FgPrimary,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4)
            });

            panel.Children.Add(new TextBlock
            {
                Text = "Get your key from console.anthropic.com (or from your administrator). It is stored encrypted for your Windows account.",
                Foreground = FgSecondary,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            });

            _apiKeyBox = new PasswordBox
            {
                Background = BgInput,
                Foreground = FgPrimary,
                BorderBrush = BorderColor,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 6, 8, 6),
                FontSize = 13,
                MaxLength = 200
            };

            // Only the user's own key goes in the box. A key from IT (managed.json) or from the
            // ANTHROPIC_API_KEY variable is named, masked, and never copied into settings.json.
            var existingKey = SettingsManager.GetApiKey(out var keySource);
            if (keySource == ApiKeySource.Settings && !string.IsNullOrEmpty(existingKey))
                _apiKeyBox.Password = existingKey;
            _initialApiKey = _apiKeyBox.Password;
            if (keySource == ApiKeySource.Managed)
                _apiKeyBox.IsEnabled = false;

            panel.Children.Add(_apiKeyBox);
            if (keySource == ApiKeySource.Managed || keySource == ApiKeySource.Environment)
                panel.Children.Add(new TextBlock
                {
                    Text = "In use: " + SettingsManager.MaskKey(existingKey) + " - " +
                           (keySource == ApiKeySource.Managed ? Strings.Get(Strings.SettingManaged) + " (managed.json)" : "from the ANTHROPIC_API_KEY variable"),
                    Foreground = FgSecondary,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 0)
                });

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 8, 0, 0)
            };
            _validateApiButton = CreateButton("Validate");
            _validateApiButton.Click += OnValidateApiClick;
            _validateApiButton.Background = Brushes.Transparent;
            _validateApiButton.Foreground = FgSecondary;
            _validateApiButton.BorderBrush = BorderColor;
            _validateApiButton.BorderThickness = new Thickness(1);
            btnRow.Children.Add(_validateApiButton);
            panel.Children.Add(btnRow);

            // Model: empty = the default. Endpoint: a gateway that speaks the Messages API.
            panel.Children.Add(CreateLabel("Model (empty = " + SettingsManager.DefaultModel + ")" + ManagedNote(ManagedSettings.Model)));
            _apiModelBox = CreateTextBox(ManagedSettings.IsManaged(ManagedSettings.Model) ? SettingsManager.GetModel() : SettingsManager.GetModelSetting());
            _apiModelBox.IsEnabled = !ManagedSettings.IsManaged(ManagedSettings.Model);
            panel.Children.Add(_apiModelBox);

            panel.Children.Add(CreateLabel("API endpoint (advanced; empty = " + SettingsManager.DefaultApiBaseUrl + ")" + ManagedNote(ManagedSettings.ApiBaseUrl)));
            var baseUrl = SettingsManager.GetApiBaseUrl();
            _apiBaseUrlBox = CreateTextBox(baseUrl == SettingsManager.DefaultApiBaseUrl ? "" : baseUrl);
            _apiBaseUrlBox.IsEnabled = !ManagedSettings.IsManaged(ManagedSettings.ApiBaseUrl);
            panel.Children.Add(_apiBaseUrlBox);

            return panel;
        }

        private static string ManagedNote(string key) =>
            ManagedSettings.IsManaged(key) ? " - " + Strings.Get(Strings.SettingManaged) : "";

        // --- Local LLM panel ---

        private StackPanel CreateLocalPanel()
        {
            var panel = new StackPanel();

            panel.Children.Add(new TextBlock
            {
                Text = "Local LLM Server",
                Foreground = FgPrimary,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4)
            });

            panel.Children.Add(new TextBlock
            {
                Text = "Connect to a local OpenAI-compatible server (llama.cpp, Ollama, LM Studio).",
                Foreground = FgSecondary,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            });

            // Endpoint
            panel.Children.Add(CreateLabel("Server Endpoint"));
            _endpointBox = CreateTextBox(SettingsManager.GetLocalLlmEndpoint());
            panel.Children.Add(_endpointBox);

            // Model name
            panel.Children.Add(CreateLabel("Model Name (optional — leave blank for server default)"));
            _modelBox = CreateTextBox(SettingsManager.GetLocalLlmModel());
            panel.Children.Add(_modelBox);

            // Tool use toggle
            _toolUseCheck = new CheckBox
            {
                Content = "Enable tool use (Revit commands)",
                Foreground = FgPrimary,
                FontSize = 12,
                IsChecked = SettingsManager.GetLocalLlmToolUse(),
                Margin = new Thickness(0, 8, 0, 0)
            };
            panel.Children.Add(_toolUseCheck);

            panel.Children.Add(new TextBlock
            {
                Text = "Only enable if your model supports function calling (Qwen 3.5 9B+, Mistral 7B+).",
                Foreground = FgSecondary,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(18, 2, 0, 8)
            });

            // Timeout slider
            var timeoutRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            timeoutRow.Children.Add(new TextBlock
            {
                Text = "Response timeout: ",
                Foreground = FgSecondary,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            });
            _timeoutLabel = new TextBlock
            {
                Text = SettingsManager.GetLocalLlmTimeout() + "s",
                Foreground = FgPrimary,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 32
            };
            timeoutRow.Children.Add(_timeoutLabel);
            panel.Children.Add(timeoutRow);

            _timeoutSlider = new Slider
            {
                Minimum = 30,
                Maximum = 300,
                Value = SettingsManager.GetLocalLlmTimeout(),
                TickFrequency = 30,
                IsSnapToTickEnabled = true,
                Margin = new Thickness(0, 2, 0, 4),
                Width = 300,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            _timeoutSlider.ValueChanged += (s, e) =>
            {
                _timeoutLabel.Text = (int)_timeoutSlider.Value + "s";
            };
            panel.Children.Add(_timeoutSlider);

            // Test Connection
            _testConnectionButton = CreateButton("Test Connection");
            _testConnectionButton.Click += OnTestConnectionClick;
            _testConnectionButton.Background = Brushes.Transparent;
            _testConnectionButton.Foreground = FgSecondary;
            _testConnectionButton.BorderBrush = BorderColor;
            _testConnectionButton.BorderThickness = new Thickness(1);
            _testConnectionButton.Margin = new Thickness(0, 4, 0, 0);
            _testConnectionButton.HorizontalAlignment = HorizontalAlignment.Left;
            panel.Children.Add(_testConnectionButton);

            return panel;
        }

        // --- Helpers ---

        private TextBlock CreateLabel(string text)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = FgSecondary,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 4)
            };
        }

        private TextBox CreateTextBox(string value)
        {
            return new TextBox
            {
                Text = value ?? "",
                Background = BgInput,
                Foreground = FgPrimary,
                CaretBrush = FgPrimary,
                BorderBrush = BorderColor,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 5, 8, 5),
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 4)
            };
        }

        private Button CreateButton(string text)
        {
            var btn = new Button
            {
                Content = text,
                Background = BgButton,
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(16, 6, 16, 6),
                FontSize = 13,
                Cursor = Cursors.Hand,
                MinWidth = 80
            };
            btn.MouseEnter += (s, e) =>
            {
                if (btn.Background != Brushes.Transparent)
                    btn.Background = BgButtonHover;
                else
                    btn.Foreground = FgPrimary;
            };
            btn.MouseLeave += (s, e) =>
            {
                if (btn.Background == BgButtonHover)
                    btn.Background = BgButton;
                else
                    btn.Foreground = FgSecondary;
            };
            return btn;
        }

        // --- Actions ---

        private void OnValidateApiClick(object sender, RoutedEventArgs e)
        {
            var key = _apiKeyBox.Password?.Trim();
            if (string.IsNullOrEmpty(key))
            {
                _statusText.Text = "Please enter an API key";
                _statusText.Foreground = FgError;
                return;
            }

            _statusText.Text = "Validating...";
            _statusText.Foreground = FgSecondary;
            _validateApiButton.IsEnabled = false;

            var model = string.IsNullOrWhiteSpace(_apiModelBox.Text) ? SettingsManager.DefaultModel : _apiModelBox.Text.Trim();
            var language = Strings.Current;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                // Same check as the connect wizard: GET /v1/models/{model} through the Windows
                // proxy; costs no tokens. (It uses the endpoint already saved.)
                var result = AnthropicHttp.TestKey(key, model, TimeSpan.FromSeconds(15));
                var message = ClaudeSetup.DescribeKeyTest(language, result, model);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _statusText.Text = message;
                    _statusText.Foreground = result.Ok ? FgSuccess : FgError;
                    _validateApiButton.IsEnabled = true;
                }));
            });
        }

        private void OnTestConnectionClick(object sender, RoutedEventArgs e)
        {
            var endpoint = _endpointBox.Text?.Trim();
            if (string.IsNullOrEmpty(endpoint))
            {
                _statusText.Text = "Please enter an endpoint URL";
                _statusText.Foreground = FgError;
                return;
            }

            _statusText.Text = "Testing connection...";
            _statusText.Foreground = FgSecondary;
            _testConnectionButton.IsEnabled = false;

            // Capture UI values on UI thread before switching to background
            var timeoutSec = (int)_timeoutSlider.Value;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool connected = false;
                string message = "";

                try
                {
                    using (var client = new HttpClient())
                    {
                        client.Timeout = TimeSpan.FromSeconds(Math.Min(timeoutSec, 10));
                        var baseUri = endpoint.TrimEnd('/');

                        // Try /health (llama.cpp)
                        try
                        {
                            var r = client.GetAsync(baseUri + "/health").Result;
                            if (r.IsSuccessStatusCode)
                            {
                                connected = true;
                                message = "Connected! Server is healthy.";
                            }
                        }
                        catch { }

                        // Try /v1/models
                        if (!connected)
                        {
                            try
                            {
                                var r = client.GetAsync(baseUri + "/v1/models").Result;
                                if (r.IsSuccessStatusCode)
                                {
                                    connected = true;
                                    var body = r.Content.ReadAsStringAsync().Result;
                                    try
                                    {
                                        var obj = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(body);
                                        var dataRaw = obj.ContainsKey("data") ? obj["data"] : null;
                                        var data = dataRaw as object[]
                                            ?? (dataRaw as System.Collections.ArrayList)?.ToArray();
                                        if (data != null && data.Length > 0)
                                        {
                                            var first = data[0] as Dictionary<string, object>;
                                            var id = first != null && first.ContainsKey("id") ? first["id"] as string : null;
                                            message = "Connected! Model: " + (id ?? "unknown");
                                        }
                                        else
                                        {
                                            message = "Connected! (no models listed)";
                                        }
                                    }
                                    catch
                                    {
                                        message = "Connected!";
                                    }
                                }
                            }
                            catch { }
                        }

                        if (!connected)
                            message = "Could not reach server at " + endpoint + ". Is it running?";
                    }
                }
                catch (Exception ex)
                {
                    message = "Connection error: " + (ex.InnerException?.Message ?? ex.Message);
                }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _statusText.Text = message;
                    _statusText.Foreground = connected ? FgSuccess : FgError;
                    _testConnectionButton.IsEnabled = true;
                }));
            });
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            var previousBackend = SettingsManager.GetPreferredBackend();
            // Normalize legacy/default to match tab keys (same as constructor)
            if (previousBackend == "direct" || previousBackend == "auto")
                previousBackend = "anthropic-api";

            // Save backend selection ("auto" stays automatic unless a tab was picked)
            var savedBackend = _originalBackend == "auto" && !_tabClicked ? "auto" : _selectedBackend;
            SettingsManager.SetPreferredBackend(savedBackend);

            // Always save all backend settings so nothing is lost when switching tabs. The key only
            // when the user edited it: a key from IT or the environment never lands in settings.json.
            var key = _apiKeyBox.Password?.Trim() ?? "";
            var keyChanged = _apiKeyBox.IsEnabled && key != (_initialApiKey ?? "").Trim();
            if (keyChanged)
                SettingsManager.SetApiKey(key);
            if (_apiModelBox.IsEnabled) SettingsManager.SetModel(_apiModelBox.Text);
            if (_apiBaseUrlBox.IsEnabled) SettingsManager.SetApiBaseUrl(_apiBaseUrlBox.Text);

            SettingsManager.SetLocalLlmEndpoint(_endpointBox.Text?.Trim() ?? "http://localhost:8080");
            SettingsManager.SetLocalLlmModel(_modelBox.Text?.Trim() ?? "");
            SettingsManager.SetLocalLlmToolUse(_toolUseCheck.IsChecked == true);
            SettingsManager.SetLocalLlmTimeout((int)_timeoutSlider.Value);

            if (_languageCombo.IsEnabled)
                SettingsManager.SetLanguage((_languageCombo.SelectedItem as ComboBoxItem)?.Tag as string);

            SettingsManager.SetStandardsEnabled(_standardsEnabledCheck.IsChecked == true);
            SettingsManager.SetProjectMemoryEnabled(_projectMemoryCheck.IsChecked == true);
            SettingsManager.SetStandardsPackPath(_packPathBox.Text);
            SettingsManager.SetStandardsFolder(_standardsFolderBox.Text);

            if (_projectKey != null)
                SettingsManager.SetProjectContextFolder(_projectKey, _projectFolderOverride);

            SettingsManager.ClearCache();
            // Pick up a new pack location / switch immediately (no Revit API involved).
            Services.Standards.StandardsContext.Reset();

            // A new or removed key changes what "auto" picks (a key wins), so the backend is rebuilt too.
            BackendChanged = _selectedBackend != previousBackend || savedBackend != _originalBackend ||
                             (keyChanged && savedBackend == "auto");

            _statusText.Text = "Settings saved!";
            _statusText.Foreground = FgSuccess;

            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(600)
            };
            timer.Tick += (t, args) =>
            {
                timer.Stop();
                DialogResult = true;
                Close();
            };
            timer.Start();
        }
    }
}
