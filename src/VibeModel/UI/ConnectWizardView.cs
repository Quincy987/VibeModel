using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VibeModel.Infrastructure;
using VibeModel.Services.Chat;

namespace VibeModel.UI
{
    /// <summary>
    /// "Verbind met Claude": the welcome card while no chat backend works. No terminal, no
    /// administrator rights: (1) paste an API key — tested against the API (free, no tokens) and
    /// stored DPAPI-encrypted — or (2) Claude Code: shows whether it is installed, installs it with
    /// the official per-user installer in a visible PowerShell window, opens its login, and
    /// switches the chat to it. Options IT switched off (managed.json) are not shown. Every control
    /// has an AutomationId for UI-automation tests (WizardApiKeyBox, WizardApiTest, …).
    /// </summary>
    public sealed class ConnectWizardView : StackPanel
    {
        /// <summary>The user picked a backend: "anthropic-api" or "claude-cli". The pane recreates it.</summary>
        public event Action<string> BackendChosen;

        private static readonly SolidColorBrush FgPrimary = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
        private static readonly SolidColorBrush FgSecondary = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
        private static readonly SolidColorBrush FgOk = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0));
        private static readonly SolidColorBrush FgError = new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36));
        private static readonly SolidColorBrush BgInput = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        private static readonly SolidColorBrush BgButton = new SolidColorBrush(Color.FromRgb(0x0E, 0x63, 0x9C));
        private static readonly SolidColorBrush BgButtonSecondary = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));
        private static readonly SolidColorBrush BorderColor = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));

        private readonly string _language;
        private PasswordBox _keyBox;
        private TextBlock _apiStatus;
        private Button _testButton;
        private Button _saveButton;
        private TextBlock _cliStatus;
        private Button _installButton;
        private Button _loginButton;
        private Button _useButton;
        private Button _recheckButton;
        private string _cliPath;

        public ConnectWizardView(string language)
        {
            _language = language;
            Children.Add(Text(T(Strings.WizardTitle), FgPrimary, 13, FontWeights.SemiBold, "WizardTitle"));
            Children.Add(Text(T(Strings.WizardIntro), FgSecondary, 11, FontWeights.Normal, null, new Thickness(0, 2, 0, 8)));

            if (!ManagedSettings.IsManaged(ManagedSettings.AnthropicApiKey))
                BuildApiSection();
            if (!SettingsManager.IsClaudeCodeDisabled())
                BuildCliSection();

            Children.Add(Text("• " + T(Strings.WizardLocal), FgSecondary, 11, FontWeights.Normal, null, new Thickness(0, 6, 0, 0)));
        }

        private string T(string key, params object[] args) => Strings.For(_language, key, args);

        // --- 1. API key --------------------------------------------------------------------

        private void BuildApiSection()
        {
            Children.Add(Text(T(Strings.WizardApiHeading), FgPrimary, 12, FontWeights.SemiBold, null));
            Children.Add(Text(T(Strings.WizardApiHint), FgSecondary, 11, FontWeights.Normal, null, new Thickness(0, 1, 0, 4)));

            _keyBox = new PasswordBox
            {
                Background = BgInput,
                Foreground = FgPrimary,
                BorderBrush = BorderColor,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(4, 3, 4, 3),
                FontSize = 12,
                CaretBrush = FgPrimary,
                Margin = new Thickness(0, 0, 0, 4)
            };
            AutomationProperties.SetAutomationId(_keyBox, "WizardApiKeyBox");
            AutomationProperties.SetName(_keyBox, T(Strings.WizardApiHeading));
            _keyBox.KeyDown += (s, e) => { if (e.Key == Key.Enter) RunKeyCheck(save: true); };
            Children.Add(_keyBox);

            var row = new WrapPanel();
            _testButton = MakeButton(T(Strings.WizardApiTest), "WizardApiTest", secondary: true);
            _testButton.Click += (s, e) => RunKeyCheck(save: false);
            _saveButton = MakeButton(T(Strings.WizardApiSave), "WizardApiSave", secondary: false);
            _saveButton.Click += (s, e) => RunKeyCheck(save: true);
            row.Children.Add(_testButton);
            row.Children.Add(_saveButton);
            Children.Add(row);

            _apiStatus = Text("", FgSecondary, 11, FontWeights.Normal, "WizardApiStatus", new Thickness(0, 2, 0, 8));
            Children.Add(_apiStatus);
        }

        // Test = check only. Save = check, and keep the key only when the API accepted it — a key
        // that cannot be checked (network, proxy) is not stored: the chat would fail the same way.
        private void RunKeyCheck(bool save)
        {
            var key = (_keyBox.Password ?? "").Trim();
            if (key.Length == 0)
            {
                Show(_apiStatus, T(Strings.WizardApiEmpty), FgError);
                return;
            }

            var model = SettingsManager.GetModel();
            Show(_apiStatus, T(Strings.WizardApiTesting), FgSecondary);
            _testButton.IsEnabled = _saveButton.IsEnabled = false;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                var result = AnthropicHttp.TestKey(key, model, TimeSpan.FromSeconds(15));
                Logger.Info("Connect wizard: key test " + result.Outcome + " (HTTP " + result.Status + ") for " + SettingsManager.MaskKey(key));
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _testButton.IsEnabled = _saveButton.IsEnabled = true;
                    Show(_apiStatus, Describe(result, model), result.Ok ? FgOk : FgError);
                    if (!save || !result.Ok) return;

                    SettingsManager.SetApiKey(key);
                    if (!ManagedSettings.IsManaged(ManagedSettings.PreferredBackend))
                        SettingsManager.SetPreferredBackend("anthropic-api");
                    Logger.Info("Connect wizard: API key saved (" + SettingsManager.MaskKey(key) + "), switching to the API backend");
                    BackendChosen?.Invoke("anthropic-api");
                }));
            });
        }

        private string Describe(AnthropicHttp.KeyTestResult result, string model) => ClaudeSetup.DescribeKeyTest(_language, result, model);

        // --- 2. Claude Code ----------------------------------------------------------------

        private void BuildCliSection()
        {
            Children.Add(Text(T(Strings.WizardCliHeading), FgPrimary, 12, FontWeights.SemiBold, null, new Thickness(0, 4, 0, 0)));
            _cliStatus = Text(T(Strings.WizardCliChecking), FgSecondary, 11, FontWeights.Normal, "WizardCliStatus", new Thickness(0, 1, 0, 4));
            Children.Add(_cliStatus);

            var row = new WrapPanel();
            _installButton = MakeButton(T(Strings.WizardCliInstall), "WizardCliInstall", secondary: false);
            _installButton.Click += (s, e) => StartInstall();
            _loginButton = MakeButton(T(Strings.WizardCliLogin), "WizardCliLogin", secondary: true);
            _loginButton.Click += (s, e) => StartLogin();
            _loginButton.ToolTip = T(Strings.WizardCliLoginHint);
            _useButton = MakeButton(T(Strings.WizardCliUse), "WizardCliUse", secondary: false);
            _useButton.Click += (s, e) =>
            {
                if (!ManagedSettings.IsManaged(ManagedSettings.PreferredBackend))
                    SettingsManager.SetPreferredBackend("claude-cli");
                Logger.Info("Connect wizard: switching to Claude Code (" + _cliPath + ")");
                BackendChosen?.Invoke("claude-cli");
            };
            _recheckButton = MakeButton(T(Strings.WizardRecheck), "WizardRecheck", secondary: true);
            _recheckButton.Click += (s, e) => CheckCli();
            foreach (var b in new[] { _installButton, _loginButton, _useButton, _recheckButton })
            {
                b.Visibility = Visibility.Collapsed;
                row.Children.Add(b);
            }
            Children.Add(row);
            CheckCli();
        }

        // `claude --version` spawns a process: off the UI thread.
        private void CheckCli()
        {
            Show(_cliStatus, T(Strings.WizardCliChecking), FgSecondary);
            _recheckButton.IsEnabled = false;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var path = ClaudeCodeBackend.LocateCli(out var version);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _cliPath = path;
                    var found = path != null;
                    Show(_cliStatus, found ? T(Strings.WizardCliFound, version) : T(Strings.WizardCliNotFound), found ? FgOk : FgSecondary);
                    if (found) _cliStatus.ToolTip = path;
                    _installButton.Visibility = found ? Visibility.Collapsed : Visibility.Visible;
                    _loginButton.Visibility = found ? Visibility.Visible : Visibility.Collapsed;
                    _useButton.Visibility = found ? Visibility.Visible : Visibility.Collapsed;
                    _recheckButton.Visibility = Visibility.Visible;
                    _recheckButton.IsEnabled = true;
                }));
            });
        }

        private void StartInstall()
        {
            var psi = ClaudeSetup.InstallStartInfo(_language);
            // Verification hook: show what would run without installing (VIBEMODEL_DRY_RUN_INSTALL=1).
            if (Environment.GetEnvironmentVariable("VIBEMODEL_DRY_RUN_INSTALL") == "1")
            {
                Logger.Warn("Connect wizard (dry run): would start " + psi.FileName + " " + psi.Arguments);
                Show(_cliStatus, "(dry run) " + psi.FileName + " " + psi.Arguments, FgSecondary);
                return;
            }
            try
            {
                Process.Start(psi);
                Logger.Info("Connect wizard: Claude Code installer started");
                Show(_cliStatus, T(Strings.WizardCliInstallStarted), FgSecondary);
                _recheckButton.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                Logger.Error("Connect wizard: could not start the installer", ex);
                Show(_cliStatus, T(Strings.WizardFailedToStart, ex.Message), FgError);
            }
        }

        // The login is interactive (a browser opens for the Claude account): it needs a window of
        // its own. Started in VibeModel's folder so Claude Code does not ask to trust a project.
        private void StartLogin()
        {
            if (_cliPath == null) return;
            try
            {
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VibeModel");
                Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo { FileName = _cliPath, WorkingDirectory = folder, UseShellExecute = true });
                Show(_cliStatus, T(Strings.WizardCliLoginHint), FgSecondary);
            }
            catch (Exception ex)
            {
                Logger.Error("Connect wizard: could not start Claude Code for the login", ex);
                Show(_cliStatus, T(Strings.WizardFailedToStart, ex.Message), FgError);
            }
        }

        // --- Little helpers ----------------------------------------------------------------

        private static void Show(TextBlock block, string text, Brush color)
        {
            block.Text = text;
            block.Foreground = color;
            block.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        private static TextBlock Text(string text, Brush fg, double size, FontWeight weight, string automationId, Thickness? margin = null)
        {
            var block = new TextBlock
            {
                Text = text,
                Foreground = fg,
                FontSize = size,
                FontWeight = weight,
                TextWrapping = TextWrapping.Wrap,
                Margin = margin ?? new Thickness(0)
            };
            if (automationId != null) AutomationProperties.SetAutomationId(block, automationId);
            if (string.IsNullOrEmpty(text)) block.Visibility = Visibility.Collapsed;
            return block;
        }

        private static Button MakeButton(string text, string automationId, bool secondary)
        {
            var button = new Button
            {
                Content = text,
                Foreground = FgPrimary,
                Background = secondary ? BgButtonSecondary : BgButton,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(0, 0, 6, 4),
                FontSize = 11,
                Cursor = Cursors.Hand
            };
            AutomationProperties.SetAutomationId(button, automationId);
            return button;
        }
    }
}
