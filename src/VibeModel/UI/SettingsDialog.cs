using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VibeModel.Infrastructure;
using VibeModel.Services.Chat;
using VibeModel.Services.Claude;

namespace VibeModel.UI
{
    /// <summary>
    /// The chat pane's Settings window: five tabs (General, AI connection, This project, Standards,
    /// About), each scrolling on its own, with the status line and Save in a fixed footer. Every
    /// text comes from <see cref="Strings"/> (nl/en) except product names. The change-approval mode
    /// is not here: the chat's mode chip chooses it and remembers it (<see cref="ModeChip"/>).
    /// </summary>
    public class SettingsDialog : Window
    {
        private static SolidColorBrush BgWindow => Palette.BgWindow;
        private static SolidColorBrush BgInput => Palette.BgInput;
        private static SolidColorBrush FgPrimary => Palette.FgPrimary;
        private static SolidColorBrush FgSecondary => Palette.FgSecondary;
        private static SolidColorBrush BgButton => Palette.BgButton;
        private static SolidColorBrush BgButtonHover => Palette.BgButtonHover;
        private static SolidColorBrush BorderColor => Palette.BorderColor;
        private static SolidColorBrush FgError => Palette.FgError;
        private static SolidColorBrush FgSuccess => Palette.FgOk;
        private static SolidColorBrush BgSelectedTab => Palette.BgButton;
        private static SolidColorBrush BgUnselectedTab => Palette.BgTab;

        // Page tabs (keys) — the last one shown is remembered for the rest of the Revit session.
        private const string TabGeneral = "general";
        private const string TabConnection = "connection";
        private const string TabProject = "project";
        private const string TabStandards = "standards";
        private const string TabAbout = "about";
        private static string _lastTab = TabGeneral;

        private readonly List<Tuple<string, Button, ScrollViewer>> _pages = new List<Tuple<string, Button, ScrollViewer>>();

        // Backend selector (sub-tabs inside "AI connection")
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

        // Footer
        private TextBlock _statusText;
        private Button _saveButton;

        // Chat language ("nl" / "en") and theme
        private ComboBox _languageCombo;
        private ComboBox _themeCombo;

        // Updates (plan 19)
        private CheckBox _updateCheckBox;
        private TextBlock _updateStatusText;
        private TextBlock _updatePendingText;
        private Button _updateCheckNowButton;
        private Button _updateRollbackButton;

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

        private static string S(string key, params object[] args) => Strings.Get(key, args);

        private static void SetId(DependencyObject element, string id) =>
            System.Windows.Automation.AutomationProperties.SetAutomationId(element, id);

        /// <param name="documentTitle">Active document title (the project key); null when unknown.</param>
        /// <param name="documentPath">Its Document.PathName — the default project folder is its directory.</param>
        /// <param name="connectionNeeded">No working chat backend yet: open on the AI connection tab.</param>
        public SettingsDialog(string documentTitle = null, string documentPath = null, bool connectionNeeded = false)
        {
            _projectKey = string.IsNullOrWhiteSpace(documentTitle) ? null : Services.Chat.ProjectDocs.ProjectKey(documentTitle);
            _documentPath = documentPath ?? "";
            _projectFolderOverride = _projectKey != null ? SettingsManager.GetProjectContextFolderSetting(_projectKey) ?? "" : "";

            Title = S(Strings.SettingsWindowTitle);
            Width = 560;
            // A fixed height for every tab (the window does not jump when switching); never taller
            // than the screen — each tab scrolls on its own.
            Height = Math.Min(640, SystemParameters.WorkArea.Height - 40);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;
            Background = BgWindow;

            _selectedBackend = SettingsManager.GetPreferredBackend();
            _originalBackend = _selectedBackend == "direct" ? "anthropic-api" : _selectedBackend;
            // Normalize legacy/default values to a valid tab key
            if (_selectedBackend == "direct" || _selectedBackend == "auto")
                _selectedBackend = "anthropic-api";

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // The footer first: the backend sub-tabs and the test buttons write to its status line.
            var footer = CreateFooter();
            Grid.SetRow(footer, 2);

            var tabBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24, 16, 24, 0) };
            var tabBorder = new Border
            {
                BorderBrush = BorderColor,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Child = tabBar,
                Padding = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(tabBorder, 0);

            var host = new Grid();
            Grid.SetRow(host, 1);

            AddPage(tabBar, host, TabGeneral, S(Strings.SettingsTabGeneral), "SettingsTabGeneral", CreateGeneralPage());
            AddPage(tabBar, host, TabConnection, S(Strings.SettingsTabConnection), "SettingsTabConnection", CreateConnectionPage());
            AddPage(tabBar, host, TabProject, S(Strings.SettingsTabProject), "SettingsTabProject", CreateProjectPage(documentTitle));
            AddPage(tabBar, host, TabStandards, S(Strings.SettingsTabStandards), "SettingsTabStandards", CreateStandardsPanel());
            AddPage(tabBar, host, TabAbout, S(Strings.SettingsTabAbout), "SettingsTabAbout", CreateUpdatesPanel());

            root.Children.Add(tabBorder);
            root.Children.Add(host);
            root.Children.Add(footer);
            Content = root;

            UpdateTabVisuals();
            ShowPage(connectionNeeded ? TabConnection : _lastTab);
        }

        // --- Pages ---

        private void AddPage(Panel tabBar, Grid host, string key, string label, string automationId, Panel content)
        {
            var tab = CreateTabButton(label, () => ShowPage(key));
            tab.FontSize = 13;
            tab.Padding = new Thickness(12, 6, 12, 6);
            SetId(tab, automationId);
            tabBar.Children.Add(tab);

            content.Margin = new Thickness(24, 12, 24, 16);
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = content,
                Visibility = Visibility.Collapsed
            };
            host.Children.Add(scroll);
            _pages.Add(Tuple.Create(key, tab, scroll));
        }

        private void ShowPage(string key)
        {
            if (!_pages.Exists(p => p.Item1 == key)) key = TabGeneral;
            _lastTab = key;
            foreach (var page in _pages)
            {
                bool on = page.Item1 == key;
                page.Item3.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                page.Item2.Background = on ? BgSelectedTab : BgUnselectedTab;
                page.Item2.Foreground = on ? Palette.FgButton : Palette.Get(ThemeRole.TabFg);
            }
        }

        private Border CreateFooter()
        {
            var panel = new StackPanel { Margin = new Thickness(24, 8, 24, 12) };
            _statusText = new TextBlock
            {
                Text = "",
                Foreground = FgSecondary,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 6),
                TextWrapping = TextWrapping.Wrap
            };
            SetId(_statusText, "SettingsStatus");
            panel.Children.Add(_statusText);

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var hint = CreateLabel(S(Strings.SettingsSaveHint));
            hint.FontSize = 11;
            hint.VerticalAlignment = VerticalAlignment.Center;
            hint.Margin = new Thickness(0, 0, 12, 0);
            Grid.SetColumn(hint, 0);
            row.Children.Add(hint);

            _saveButton = CreateButton(S(Strings.SettingsSave));
            _saveButton.Click += OnSaveClick;
            SetId(_saveButton, "SettingsSave");
            Grid.SetColumn(_saveButton, 1);
            row.Children.Add(_saveButton);
            panel.Children.Add(row);

            return new Border
            {
                BorderBrush = BorderColor,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Child = panel
            };
        }

        private StackPanel CreateGeneralPage()
        {
            var page = new StackPanel();
            page.Children.Add(CreateLanguagePanel());
            var theme = CreateThemePanel();
            theme.Margin = new Thickness(0, 20, 0, 0);
            page.Children.Add(theme);
            return page;
        }

        private StackPanel CreateConnectionPage()
        {
            var page = new StackPanel();
            page.Children.Add(CreateHeading(S(Strings.SettingsConnectionTitle)));
            page.Children.Add(CreateLabel(S(Strings.SettingsConnectionHint)));
            page.Children.Add(CreateBackendTabBar());
            if (ManagedSettings.IsManaged(ManagedSettings.PreferredBackend))
                page.Children.Add(CreateLabel(S(Strings.SettingsBackendManaged, SettingsManager.GetPreferredBackend(), S(Strings.SettingManaged))));

            // Config panels (stacked, visibility toggled)
            _cliPanel = CreateCliPanel();
            _apiPanel = CreateApiPanel();
            _localPanel = CreateLocalPanel();
            page.Children.Add(_cliPanel);
            page.Children.Add(_apiPanel);
            page.Children.Add(_localPanel);
            return page;
        }

        private StackPanel CreateProjectPage(string documentTitle)
        {
            var page = new StackPanel();
            page.Children.Add(CreateHeading(_projectKey != null
                ? S(Strings.SettingsProjectTitle, documentTitle)
                : S(Strings.SettingsTabProject)));
            if (_projectKey == null)
                page.Children.Add(CreateLabel(S(Strings.SettingsProjectNoModel)));
            page.Children.Add(CreateProjectFolderPanel());
            return page;
        }

        // --- Backend sub-tabs ---

        private StackPanel CreateBackendTabBar()
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 6, 0, 14)
            };

            // Product names stay as they are in both languages.
            _tabCli = CreateBackendTab("Claude Code", "claude-cli", "SettingsBackendCli");
            _tabApi = CreateBackendTab("Anthropic API", "anthropic-api", "SettingsBackendApi");
            _tabLocal = CreateBackendTab(S(Strings.SettingsLocalTab), "local-llm", "SettingsBackendLocal");

            panel.Children.Add(_tabCli);
            panel.Children.Add(_tabApi);
            panel.Children.Add(_tabLocal);
            return panel;
        }

        private Button CreateBackendTab(string text, string backendKey, string automationId)
        {
            var btn = CreateTabButton(text, () =>
            {
                _selectedBackend = backendKey;
                _tabClicked = true;
                UpdateTabVisuals();
            });
            SetId(btn, automationId);
            return btn;
        }

        private Button CreateTabButton(string text, Action onClick)
        {
            var btn = new Button
            {
                Content = text,
                Foreground = Palette.Get(ThemeRole.TabFg),
                Background = BgUnselectedTab,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(14, 6, 14, 6),
                FontSize = 12,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 4, 0)
            };
            btn.Click += (s, e) => onClick();
            return btn;
        }

        private void UpdateTabVisuals()
        {
            _tabCli.Background = _selectedBackend == "claude-cli" ? BgSelectedTab : BgUnselectedTab;
            _tabApi.Background = _selectedBackend == "anthropic-api" ? BgSelectedTab : BgUnselectedTab;
            _tabLocal.Background = _selectedBackend == "local-llm" ? BgSelectedTab : BgUnselectedTab;
            foreach (var tab in new[] { _tabCli, _tabApi, _tabLocal })
                tab.Foreground = tab.Background == BgSelectedTab ? Palette.FgButton : Palette.Get(ThemeRole.TabFg);

            _cliPanel.Visibility = _selectedBackend == "claude-cli" ? Visibility.Visible : Visibility.Collapsed;
            _apiPanel.Visibility = _selectedBackend == "anthropic-api" ? Visibility.Visible : Visibility.Collapsed;
            _localPanel.Visibility = _selectedBackend == "local-llm" ? Visibility.Visible : Visibility.Collapsed;

            _statusText.Text = "";
        }

        // --- Claude Code panel ---

        private StackPanel CreateCliPanel()
        {
            var panel = new StackPanel();
            panel.Children.Add(CreateSubHeading("Claude Code"));
            panel.Children.Add(CreateLabel(S(Strings.SettingsCliHint) + "\n" + ClaudeSetup.InstallCommand));

            // Detection status: the same search the backend uses (.local\bin, a fresh PATH, the npm
            // folders). It spawns `claude --version`, so it runs off the UI thread.
            var status = CreateLabel(S(Strings.SettingsCliLooking));
            status.Margin = new Thickness(0, 8, 0, 4);
            panel.Children.Add(status);
            if (SettingsManager.IsClaudeCodeDisabled())
            {
                status.Text = S(Strings.SettingsCliDisabled, S(Strings.SettingManaged));
                status.Foreground = FgError;
                return panel;
            }
            var found = Strings.SettingsCliFound;
            var notFound = S(Strings.SettingsCliNotFound);
            var language = Strings.Current;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var path = ClaudeCodeBackend.LocateCli(out var version);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    status.Text = path != null ? Strings.For(language, found, version, path) : notFound;
                    status.Foreground = path != null ? FgSuccess : FgError;
                }));
            });

            return panel;
        }

        // --- Language panel ---

        private StackPanel CreateLanguagePanel()
        {
            var panel = new StackPanel();
            // Both languages on purpose: whoever cannot read the current one still finds it.
            panel.Children.Add(CreateHeading("Taal / Language"));
            panel.Children.Add(CreateLabel(S(Strings.SettingsLanguageHint)));

            _languageCombo = new ComboBox
            {
                FontSize = 13,
                Width = 200,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 4, 0, 4)
            };
            SetId(_languageCombo, "SettingsLanguage");
            _languageCombo.Items.Add(new ComboBoxItem { Content = "Nederlands", Tag = Services.Chat.Strings.Dutch });
            _languageCombo.Items.Add(new ComboBoxItem { Content = "English", Tag = Services.Chat.Strings.English });
            _languageCombo.SelectedIndex = Services.Chat.Strings.IsEnglish(SettingsManager.GetLanguage()) ? 1 : 0;
            panel.Children.Add(_languageCombo);
            if (ManagedSettings.IsManaged(ManagedSettings.Language))
            {
                _languageCombo.IsEnabled = false;
                panel.Children.Add(CreateLabel(S(Strings.SettingManaged)));
            }
            return panel;
        }

        // --- Theme panel ---

        private StackPanel CreateThemePanel()
        {
            var panel = new StackPanel();
            panel.Children.Add(CreateHeading(S(Strings.SettingThemeTitle)));
            panel.Children.Add(CreateLabel(S(Strings.SettingThemeHint)));

            _themeCombo = new ComboBox
            {
                FontSize = 13,
                Width = 200,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 4, 0, 4)
            };
            SetId(_themeCombo, "SettingsTheme");
            _themeCombo.Items.Add(new ComboBoxItem { Content = S(Strings.ThemeFollowRevit), Tag = ThemeChoice.Revit });
            _themeCombo.Items.Add(new ComboBoxItem { Content = S(Strings.ThemeLight), Tag = ThemeChoice.Light });
            _themeCombo.Items.Add(new ComboBoxItem { Content = S(Strings.ThemeDark), Tag = ThemeChoice.Dark });
            var current = SettingsManager.GetTheme();
            _themeCombo.SelectedIndex = current == ThemeChoice.Light ? 1 : current == ThemeChoice.Dark ? 2 : 0;
            panel.Children.Add(_themeCombo);
            if (ManagedSettings.IsManaged(ManagedSettings.Theme))
            {
                _themeCombo.IsEnabled = false;
                panel.Children.Add(CreateLabel(S(Strings.SettingManaged)));
            }
            return panel;
        }

        // --- Project folder panel (plan 10) ---

        private StackPanel CreateProjectFolderPanel()
        {
            var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            panel.Children.Add(CreateSubHeading(S(Strings.SettingsProjectFolder)));
            panel.Children.Add(CreateLabel(S(Strings.SettingsProjectFolderHint)));

            _projectFolderBox = CreateTextBox("");
            _projectFolderBox.IsReadOnly = true;
            SetId(_projectFolderBox, "SettingsProjectFolder");

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(_projectFolderBox, 0);
            grid.Children.Add(_projectFolderBox);

            var browse = CreateButton(S(Strings.SettingsBrowse));
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
                    FileName = S(Strings.SettingsProjectPickName),
                    Title = S(Strings.SettingsProjectPickTitle)
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

            var clear = CreateButton(S(Strings.SettingsClear));
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

            _projectFolderSource = CreateLabel("");
            _projectFolderSource.Margin = new Thickness(0, 0, 0, 4);
            panel.Children.Add(_projectFolderSource);
            UpdateProjectFolderRow();

            // Project memory + journal (plan 08) live in that folder's VibeModel subfolder.
            _projectMemoryCheck = new CheckBox
            {
                Content = S(Strings.SettingProjectMemory),
                IsChecked = SettingsManager.GetProjectMemoryEnabled(),
                Foreground = FgPrimary,
                FontSize = 12,
                Margin = new Thickness(0, 14, 0, 2)
            };
            SetId(_projectMemoryCheck, "SettingsProjectMemory");
            panel.Children.Add(_projectMemoryCheck);
            // A global switch on a per-project page: say so.
            var memoryHint = CreateLabel(S(Strings.SettingProjectMemoryHint) + " " + S(Strings.SettingsProjectMemoryAll));
            memoryHint.Margin = new Thickness(20, 0, 0, 4);
            panel.Children.Add(memoryHint);
            return panel;
        }

        private static string SourceText(ProjectFolderSource source) =>
            source == ProjectFolderSource.Configured ? S(Strings.SettingsProjectSourceConfigured) : S(Strings.SettingsProjectSourceModel);

        // Shows the folder that will be in force (pending choice + model default) and its source.
        private void UpdateProjectFolderRow()
        {
            var live = SettingsManager.LiveFolderOrNull(_projectFolderOverride);
            var folder = Services.Chat.ProjectDocs.Resolve(live, _documentPath);
            _projectFolderBox.Text = folder.IsSet ? folder.Path : "";
            var text = folder.IsSet ? SourceText(folder.Source) : S(Strings.SettingsProjectNotSet);
            if (!string.IsNullOrWhiteSpace(_projectFolderOverride) && live == null)
                text = S(Strings.SettingsProjectMissing, _projectFolderOverride,
                    folder.IsSet ? S(Strings.SettingsProjectUsing, SourceText(folder.Source)) : S(Strings.SettingsProjectNoFolder));
            _projectFolderSource.Text = text;
        }

        // --- Office standards panel ---

        private StackPanel CreateStandardsPanel()
        {
            var panel = new StackPanel();
            panel.Children.Add(CreateHeading(S(Strings.SettingsStandardsTitle)));
            panel.Children.Add(CreateLabel(S(Strings.SettingsStandardsHint)));

            _standardsEnabledCheck = new CheckBox
            {
                Content = S(Strings.SettingsStandardsEnabled),
                IsChecked = SettingsManager.GetStandardsEnabled(),
                Foreground = FgPrimary,
                FontSize = 12,
                Margin = new Thickness(0, 8, 0, 12)
            };
            SetId(_standardsEnabledCheck, "SettingsStandardsEnabled");
            panel.Children.Add(_standardsEnabledCheck);

            var localCopy = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VibeModel", "standards");
            var packLabel = CreateLabel(S(Strings.SettingsStandardsPack));
            packLabel.ToolTip = S(Strings.SettingsStandardsPackTip, localCopy);
            panel.Children.Add(packLabel);
            _packPathBox = CreateTextBox(SettingsManager.GetStandardsPackPath());
            _packPathBox.ToolTip = packLabel.ToolTip;
            SetId(_packPathBox, "SettingsStandardsPack");
            panel.Children.Add(CreateFolderRow(_packPathBox, "pack.json|pack.json|" + S(Strings.SettingsAllFiles) + "|*.*"));

            var docsLabel = CreateLabel(S(Strings.SettingsStandardsDocs));
            docsLabel.Margin = new Thickness(0, 12, 0, 4);
            panel.Children.Add(docsLabel);
            _standardsFolderBox = CreateTextBox(SettingsManager.GetStandardsFolder());
            SetId(_standardsFolderBox, "SettingsStandardsDocs");
            panel.Children.Add(CreateFolderRow(_standardsFolderBox,
                S(Strings.SettingsDocumentsFilter) + "|*.pdf;*.txt;*.md|" + S(Strings.SettingsAllFiles) + "|*.*"));
            return panel;
        }

        // --- Updates panel (plan 19) ---

        private StackPanel CreateUpdatesPanel()
        {
            var engine = Services.Update.UpdateService.Engine;
            var panel = new StackPanel();
            panel.Children.Add(CreateHeading(S(Strings.SettingsUpdatesTitle)));

            panel.Children.Add(CreateLabel(S(Strings.SettingsUpdateInstalled,
                Services.Update.UpdateManifest.ShortVersion(engine.Running.Version),
                Services.Update.LoaderInfo.Version == null ? "-" : Services.Update.UpdateManifest.ShortVersion(Services.Update.LoaderInfo.ParsedVersion))));
            panel.Children.Add(CreateLabel(S(Strings.SettingsUpdateRunsFrom,
                engine.InPlace ? S(Strings.UpdateDevBuild) : System.IO.Path.GetDirectoryName(engine.Running.PayloadPath))));
            panel.Children.Add(CreateLabel(S(Strings.SettingsUpdateSource, engine.Source.Describe()) +
                                           ManagedNote(ManagedSettings.UpdateSource)));

            bool disabled = SettingsManager.IsUpdateDisabled();
            _updateCheckBox = new CheckBox
            {
                Content = S(Strings.SettingsUpdateCheck),
                IsChecked = SettingsManager.GetUpdateCheck() && !disabled,
                IsEnabled = !disabled,
                Foreground = FgPrimary,
                FontSize = 12,
                Margin = new Thickness(0, 10, 0, 4)
            };
            SetId(_updateCheckBox, "SettingsUpdateCheck");
            panel.Children.Add(_updateCheckBox);
            if (disabled)
                panel.Children.Add(CreateLabel(S(Strings.UpdateDisabled) + " (" + S(Strings.SettingManaged) + ")"));

            _updateStatusText = CreateLabel("");
            SetId(_updateStatusText, "SettingsUpdateStatus");
            panel.Children.Add(_updateStatusText);
            _updatePendingText = CreateLabel("");
            panel.Children.Add(_updatePendingText);

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
            _updateCheckNowButton = CreateButton(S(Strings.SettingsUpdateCheckNow));
            _updateCheckNowButton.IsEnabled = !disabled && !engine.InPlace;
            SetId(_updateCheckNowButton, "SettingsUpdateCheckNow");
            _updateCheckNowButton.Click += async (s, e) =>
            {
                _updateCheckNowButton.IsEnabled = false;
                _updateStatusText.Text = S(Strings.UpdateChecking);
                var status = await Services.Update.UpdateService.CheckAsync(manual: true);
                _updateCheckNowButton.IsEnabled = true;
                ShowUpdateState(status);
            };
            row.Children.Add(_updateCheckNowButton);

            _updateRollbackButton = CreateSecondaryButton("");
            _updateRollbackButton.Margin = new Thickness(8, 0, 0, 0);
            SetId(_updateRollbackButton, "SettingsUpdateRollback");
            _updateRollbackButton.Click += async (s, e) =>
            {
                _updateRollbackButton.IsEnabled = false;
                var status = await Services.Update.UpdateService.RollbackAsync();
                _updateRollbackButton.IsEnabled = true;
                ShowUpdateState(status);
            };
            row.Children.Add(_updateRollbackButton);
            panel.Children.Add(row);

            ShowUpdateState(engine.Refresh());
            return panel;
        }

        private void ShowUpdateState(Services.Update.UpdateStatus status)
        {
            var engine = Services.Update.UpdateService.Engine;
            _updateStatusText.Text = UiText.UpdateLine(status, engine.Progress, Strings.Current) +
                                     (status.LastError != null ? "\n" + S(Strings.UpdateLastError, status.LastError) : "");
            _updatePendingText.Text = status.Pending != null ? S(Strings.SettingsUpdatePending, status.Pending) : "";
            _updatePendingText.Visibility = status.Pending != null ? Visibility.Visible : Visibility.Collapsed;
            var target = engine.RollbackTarget;
            _updateRollbackButton.Visibility = target != null ? Visibility.Visible : Visibility.Collapsed;
            if (target != null) _updateRollbackButton.Content = S(Strings.SettingsUpdateRollback, target);
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

            var browse = CreateButton(S(Strings.SettingsBrowse));
            browse.MinWidth = 0;
            browse.Margin = new Thickness(6, 0, 0, 4);
            browse.Click += (s, e) =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = filter,
                    CheckFileExists = true,
                    Title = S(Strings.SettingsPickFileInFolder)
                };
                if (dlg.ShowDialog(this) == true)
                    box.Text = System.IO.Path.GetDirectoryName(dlg.FileName);
            };
            Grid.SetColumn(browse, 1);
            grid.Children.Add(browse);

            var clear = CreateButton(S(Strings.SettingsClear));
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
            panel.Children.Add(CreateSubHeading(S(Strings.SettingsApiTitle)));
            var hint = CreateLabel(S(Strings.SettingsApiHint));
            hint.Margin = new Thickness(0, 0, 0, 8);
            panel.Children.Add(hint);

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
            SetId(_apiKeyBox, "SettingsApiKey");

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
            {
                var inUse = CreateLabel(S(Strings.SettingsApiInUse, SettingsManager.MaskKey(existingKey),
                    keySource == ApiKeySource.Managed ? S(Strings.SettingManaged) + " (managed.json)" : S(Strings.SettingsApiFromEnv)));
                inUse.FontSize = 11;
                panel.Children.Add(inUse);
            }

            _validateApiButton = CreateSecondaryButton(S(Strings.SettingsApiValidate));
            _validateApiButton.Click += OnValidateApiClick;
            _validateApiButton.Margin = new Thickness(0, 8, 0, 0);
            _validateApiButton.HorizontalAlignment = HorizontalAlignment.Left;
            SetId(_validateApiButton, "SettingsApiValidate");
            panel.Children.Add(_validateApiButton);

            // Advanced: model (empty = the default) and endpoint (a gateway that speaks the Messages API).
            var advanced = new StackPanel();
            advanced.Children.Add(CreateLabel(S(Strings.SettingsApiModel, SettingsManager.DefaultModel) + ManagedNote(ManagedSettings.Model)));
            _apiModelBox = CreateTextBox(ManagedSettings.IsManaged(ManagedSettings.Model) ? SettingsManager.GetModel() : SettingsManager.GetModelSetting());
            _apiModelBox.IsEnabled = !ManagedSettings.IsManaged(ManagedSettings.Model);
            advanced.Children.Add(_apiModelBox);

            advanced.Children.Add(CreateLabel(S(Strings.SettingsApiEndpoint, SettingsManager.DefaultApiBaseUrl) + ManagedNote(ManagedSettings.ApiBaseUrl)));
            var baseUrl = SettingsManager.GetApiBaseUrl();
            _apiBaseUrlBox = CreateTextBox(baseUrl == SettingsManager.DefaultApiBaseUrl ? "" : baseUrl);
            _apiBaseUrlBox.IsEnabled = !ManagedSettings.IsManaged(ManagedSettings.ApiBaseUrl);
            advanced.Children.Add(_apiBaseUrlBox);

            // Open from the start when something in it is set or locked, so it is never hidden.
            bool open = !string.IsNullOrWhiteSpace(_apiModelBox.Text) || !string.IsNullOrWhiteSpace(_apiBaseUrlBox.Text) ||
                        ManagedSettings.IsManaged(ManagedSettings.Model) || ManagedSettings.IsManaged(ManagedSettings.ApiBaseUrl);
            AddAdvanced(panel, advanced, open, "SettingsApiAdvanced");
            return panel;
        }

        private static string ManagedNote(string key) =>
            ManagedSettings.IsManaged(key) ? " - " + S(Strings.SettingManaged) : "";

        // --- Local LLM panel ---

        private StackPanel CreateLocalPanel()
        {
            var panel = new StackPanel();
            panel.Children.Add(CreateSubHeading(S(Strings.SettingsLocalTab)));
            var hint = CreateLabel(S(Strings.SettingsLocalHint));
            hint.Margin = new Thickness(0, 0, 0, 8);
            panel.Children.Add(hint);

            panel.Children.Add(CreateLabel(S(Strings.SettingsLocalEndpoint)));
            _endpointBox = CreateTextBox(SettingsManager.GetLocalLlmEndpoint());
            SetId(_endpointBox, "SettingsLocalEndpoint");
            panel.Children.Add(_endpointBox);

            _testConnectionButton = CreateSecondaryButton(S(Strings.SettingsLocalTest));
            _testConnectionButton.Click += OnTestConnectionClick;
            _testConnectionButton.Margin = new Thickness(0, 4, 0, 0);
            _testConnectionButton.HorizontalAlignment = HorizontalAlignment.Left;
            SetId(_testConnectionButton, "SettingsLocalTest");
            panel.Children.Add(_testConnectionButton);

            var advanced = new StackPanel();
            advanced.Children.Add(CreateLabel(S(Strings.SettingsLocalModel)));
            _modelBox = CreateTextBox(SettingsManager.GetLocalLlmModel());
            advanced.Children.Add(_modelBox);

            _toolUseCheck = new CheckBox
            {
                Content = S(Strings.SettingsLocalTools),
                Foreground = FgPrimary,
                FontSize = 12,
                IsChecked = SettingsManager.GetLocalLlmToolUse(),
                Margin = new Thickness(0, 8, 0, 0)
            };
            advanced.Children.Add(_toolUseCheck);
            var toolHint = CreateLabel(S(Strings.SettingsLocalToolsHint));
            toolHint.FontSize = 11;
            toolHint.Margin = new Thickness(20, 2, 0, 8);
            advanced.Children.Add(toolHint);

            var timeoutRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            var timeoutText = CreateLabel(S(Strings.SettingsLocalTimeout));
            timeoutText.Margin = new Thickness(0);
            timeoutText.VerticalAlignment = VerticalAlignment.Center;
            timeoutRow.Children.Add(timeoutText);
            _timeoutLabel = new TextBlock
            {
                Text = SettingsManager.GetLocalLlmTimeout() + " s",
                Foreground = FgPrimary,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 32
            };
            timeoutRow.Children.Add(_timeoutLabel);
            advanced.Children.Add(timeoutRow);

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
                _timeoutLabel.Text = (int)_timeoutSlider.Value + " s";
            };
            advanced.Children.Add(_timeoutSlider);

            AddAdvanced(panel, advanced, !string.IsNullOrWhiteSpace(_modelBox.Text), "SettingsLocalAdvanced");
            return panel;
        }

        // A plain "▸ Advanced" toggle (WPF's Expander ignores the theme palette) that shows the
        // panel below it.
        private void AddAdvanced(Panel parent, StackPanel content, bool open, string automationId)
        {
            var label = S(Strings.SettingsAdvanced);
            var toggle = new Button
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = Palette.Accent,
                FontSize = 12,
                Padding = new Thickness(0, 2, 0, 2),
                Margin = new Thickness(0, 14, 0, 2),
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            SetId(toggle, automationId);
            System.Windows.Automation.AutomationProperties.SetName(toggle, label);
            content.Margin = new Thickness(14, 0, 0, 0);
            Action apply = () =>
            {
                toggle.Content = (content.Visibility == Visibility.Visible ? "▾ " : "▸ ") + label;
            };
            content.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            apply();
            toggle.Click += (s, e) =>
            {
                content.Visibility = content.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                apply();
            };
            parent.Children.Add(toggle);
            parent.Children.Add(content);
        }

        // --- Helpers ---

        private TextBlock CreateHeading(string text)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = FgPrimary,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4)
            };
        }

        private TextBlock CreateSubHeading(string text)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = FgPrimary,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4)
            };
        }

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
                Foreground = Palette.FgButton,
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

        // An outlined button for actions that run at once (test a key, a connection, roll back).
        private Button CreateSecondaryButton(string text)
        {
            var btn = CreateButton(text);
            btn.Background = Brushes.Transparent;
            btn.Foreground = FgSecondary;
            btn.BorderBrush = BorderColor;
            btn.BorderThickness = new Thickness(1);
            return btn;
        }

        // --- Actions ---

        private void OnValidateApiClick(object sender, RoutedEventArgs e)
        {
            var key = _apiKeyBox.Password?.Trim();
            // Box empty (or locked) while IT's or the environment's key is in use: test that one.
            if (string.IsNullOrEmpty(key))
            {
                var inUse = SettingsManager.GetApiKey(out var source);
                if (source == ApiKeySource.Managed || source == ApiKeySource.Environment) key = inUse;
            }
            if (string.IsNullOrEmpty(key))
            {
                _statusText.Text = S(Strings.SettingsApiKeyEmpty);
                _statusText.Foreground = FgError;
                return;
            }

            _statusText.Text = S(Strings.SettingsApiChecking);
            _statusText.Foreground = FgSecondary;
            _validateApiButton.IsEnabled = false;

            var model = string.IsNullOrWhiteSpace(_apiModelBox.Text) ? SettingsManager.DefaultModel : _apiModelBox.Text.Trim();
            // What is typed now, not what was saved before (an empty box = the default endpoint).
            var baseUrl = _apiBaseUrlBox.IsEnabled ? _apiBaseUrlBox.Text : SettingsManager.GetApiBaseUrl();
            var language = Strings.Current;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                // Same check as the connect wizard: GET /v1/models/{model} through the Windows
                // proxy; costs no tokens.
                var result = AnthropicHttp.TestKey(key, model, TimeSpan.FromSeconds(15), baseUrl);
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
                _statusText.Text = S(Strings.SettingsLocalNoEndpoint);
                _statusText.Foreground = FgError;
                return;
            }

            _statusText.Text = S(Strings.SettingsLocalTesting);
            _statusText.Foreground = FgSecondary;
            _testConnectionButton.IsEnabled = false;

            // Capture UI values on UI thread before switching to background
            var timeoutSec = (int)_timeoutSlider.Value;
            var language = Strings.Current;

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
                                message = Strings.For(language, Strings.SettingsLocalHealthy);
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
                                            message = Strings.For(language, Strings.SettingsLocalModelFound,
                                                id ?? Strings.For(language, Strings.SettingsLocalUnknownModel));
                                        }
                                        else
                                        {
                                            message = Strings.For(language, Strings.SettingsLocalNoModels);
                                        }
                                    }
                                    catch
                                    {
                                        message = Strings.For(language, Strings.SettingsLocalConnected);
                                    }
                                }
                            }
                            catch { }
                        }

                        if (!connected)
                            message = Strings.For(language, Strings.SettingsLocalUnreachable, endpoint);
                    }
                }
                catch (Exception ex)
                {
                    message = Strings.For(language, Strings.SettingsLocalError, ex.InnerException?.Message ?? ex.Message);
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
            if (_themeCombo.IsEnabled && (_themeCombo.SelectedItem as ComboBoxItem)?.Tag is ThemeChoice theme)
                SettingsManager.SetTheme(theme);

            if (_updateCheckBox.IsEnabled)
                SettingsManager.SetUpdateCheck(_updateCheckBox.IsChecked == true);

            SettingsManager.SetStandardsEnabled(_standardsEnabledCheck.IsChecked == true);
            SettingsManager.SetProjectMemoryEnabled(_projectMemoryCheck.IsChecked == true);
            SettingsManager.SetStandardsPackPath(_packPathBox.Text);
            SettingsManager.SetStandardsFolder(_standardsFolderBox.Text);

            if (_projectKey != null)
                SettingsManager.SetProjectContextFolder(_projectKey, _projectFolderOverride);

            SettingsManager.ClearCache();
            // A new theme repaints the pane (and this dialog) at once: the shared brushes recolour.
            ThemeManager.Refresh();
            // Pick up a new pack location / switch immediately (no Revit API involved).
            Services.Standards.StandardsContext.Reset();

            // A new or removed key changes what "auto" picks (a key wins), so the backend is rebuilt too.
            BackendChanged = _selectedBackend != previousBackend || savedBackend != _originalBackend ||
                             (keyChanged && savedBackend == "auto");

            _statusText.Text = S(Strings.SettingsSaved);
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
