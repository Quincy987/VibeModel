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

        /// <summary>True if the user changed the backend selection (caller should reset chat).</summary>
        public bool BackendChanged { get; private set; }

        public SettingsDialog()
        {
            Title = "VibeModel Settings";
            Width = 500;
            Height = 480;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;
            Background = BgDark;

            _selectedBackend = SettingsManager.GetPreferredBackend();
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

            // Config panels (stacked, visibility toggled)
            _cliPanel = CreateCliPanel();
            _apiPanel = CreateApiPanel();
            _localPanel = CreateLocalPanel();

            mainPanel.Children.Add(_cliPanel);
            mainPanel.Children.Add(_apiPanel);
            mainPanel.Children.Add(_localPanel);

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

            Content = mainPanel;
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
                Text = "Uses the Claude Code CLI for full agentic capabilities.\n" +
                       "Install with: npm install -g @anthropic-ai/claude-code\n" +
                       "Set ANTHROPIC_API_KEY environment variable.",
                Foreground = FgSecondary,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            });

            // Detection status
            var cliStatus = "Not detected";
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "claude",
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using (var proc = System.Diagnostics.Process.Start(psi))
                {
                    var ver = proc.StandardOutput.ReadToEnd().Trim();
                    proc.WaitForExit(2000);
                    if (proc.ExitCode == 0)
                        cliStatus = "Detected: Claude Code " + ver;
                }
            }
            catch { }

            var detected = cliStatus.StartsWith("Detected");
            panel.Children.Add(new TextBlock
            {
                Text = cliStatus,
                Foreground = detected ? FgSuccess : FgError,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 4)
            });

            return panel;
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
                Text = "Get your key from console.anthropic.com",
                Foreground = FgSecondary,
                FontSize = 12,
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

            var existingKey = SettingsManager.GetApiKey();
            if (!string.IsNullOrEmpty(existingKey))
                _apiKeyBox.Password = existingKey;

            panel.Children.Add(_apiKeyBox);

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

            return panel;
        }

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
                Text = "Only enable if your model supports function calling (Qwen 2.5 7B+, Mistral 7B+).",
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

            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool valid = false;
                string message = "";

                try
                {
                    using (var client = new HttpClient())
                    {
                        client.Timeout = TimeSpan.FromSeconds(15);

                        var body = new JavaScriptSerializer().Serialize(new Dictionary<string, object>
                        {
                            { "model", SettingsManager.GetModel() },
                            { "max_tokens", 1 },
                            { "messages", new[] { new Dictionary<string, object>
                                {
                                    { "role", "user" },
                                    { "content", "Hi" }
                                }
                            }}
                        });

                        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
                        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                        request.Headers.Add("x-api-key", key);
                        request.Headers.Add("anthropic-version", "2023-06-01");

                        var response = client.SendAsync(request).Result;
                        if (response.IsSuccessStatusCode)
                        {
                            valid = true;
                            message = "API key is valid!";
                        }
                        else
                        {
                            var responseBody = response.Content.ReadAsStringAsync().Result;
                            try
                            {
                                var errorObj = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(responseBody);
                                var error = errorObj.ContainsKey("error") ? errorObj["error"] as Dictionary<string, object> : null;
                                message = error != null && error.ContainsKey("message")
                                    ? (string)error["message"]
                                    : "Invalid key (HTTP " + (int)response.StatusCode + ")";
                            }
                            catch
                            {
                                message = "Invalid key (HTTP " + (int)response.StatusCode + ")";
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    message = "Connection error: " + (ex.InnerException?.Message ?? ex.Message);
                }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _statusText.Text = message;
                    _statusText.Foreground = valid ? FgSuccess : FgError;
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
                                        var data = obj.ContainsKey("data") ? obj["data"] as object[] : null;
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

            // Save backend selection
            SettingsManager.SetPreferredBackend(_selectedBackend);

            // Always save all backend settings so nothing is lost when switching tabs
            var key = _apiKeyBox.Password?.Trim();
            SettingsManager.SetApiKey(key ?? "");

            SettingsManager.SetLocalLlmEndpoint(_endpointBox.Text?.Trim() ?? "http://localhost:8080");
            SettingsManager.SetLocalLlmModel(_modelBox.Text?.Trim() ?? "");
            SettingsManager.SetLocalLlmToolUse(_toolUseCheck.IsChecked == true);
            SettingsManager.SetLocalLlmTimeout((int)_timeoutSlider.Value);

            SettingsManager.ClearCache();

            BackendChanged = _selectedBackend != previousBackend;

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
