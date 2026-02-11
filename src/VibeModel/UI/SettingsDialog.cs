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
        private static readonly SolidColorBrush FgPrimary = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
        private static readonly SolidColorBrush FgSecondary = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
        private static readonly SolidColorBrush AccentBlue = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0));
        private static readonly SolidColorBrush BgButton = new SolidColorBrush(Color.FromRgb(0x0E, 0x63, 0x9C));
        private static readonly SolidColorBrush BgButtonHover = new SolidColorBrush(Color.FromRgb(0x1E, 0x73, 0xAC));
        private static readonly SolidColorBrush BorderColor = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));
        private static readonly SolidColorBrush FgError = new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36));
        private static readonly SolidColorBrush FgSuccess = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0));

        private PasswordBox _apiKeyBox;
        private TextBlock _statusText;
        private Button _validateButton;
        private Button _saveButton;

        public SettingsDialog()
        {
            Title = "VibeModel Settings";
            Width = 480;
            Height = 280;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;
            Background = BgDark;

            var mainPanel = new StackPanel
            {
                Margin = new Thickness(24, 20, 24, 20)
            };

            // Title
            mainPanel.Children.Add(new TextBlock
            {
                Text = "Anthropic API Key",
                Foreground = FgPrimary,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4)
            });

            mainPanel.Children.Add(new TextBlock
            {
                Text = "Get your key from console.anthropic.com",
                Foreground = FgSecondary,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 12)
            });

            // API key input
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

            // Load existing key
            var existingKey = SettingsManager.GetApiKey();
            if (!string.IsNullOrEmpty(existingKey))
                _apiKeyBox.Password = existingKey;

            mainPanel.Children.Add(_apiKeyBox);

            // Status text
            _statusText = new TextBlock
            {
                Text = string.IsNullOrEmpty(existingKey) ? "No API key configured" : "API key loaded from settings",
                Foreground = string.IsNullOrEmpty(existingKey) ? FgSecondary : FgSuccess,
                FontSize = 12,
                Margin = new Thickness(0, 8, 0, 16)
            };
            mainPanel.Children.Add(_statusText);

            // Button row
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            _validateButton = CreateButton("Validate");
            _validateButton.Click += OnValidateClick;
            _validateButton.Background = Brushes.Transparent;
            _validateButton.Foreground = FgSecondary;
            _validateButton.BorderBrush = BorderColor;
            _validateButton.BorderThickness = new Thickness(1);
            buttonPanel.Children.Add(_validateButton);

            _saveButton = CreateButton("Save");
            _saveButton.Click += OnSaveClick;
            _saveButton.Margin = new Thickness(8, 0, 0, 0);
            buttonPanel.Children.Add(_saveButton);

            mainPanel.Children.Add(buttonPanel);

            Content = mainPanel;
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

        private void OnValidateClick(object sender, RoutedEventArgs e)
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
            _validateButton.IsEnabled = false;

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
                    message = "Connection error: " + ex.InnerException?.Message ?? ex.Message;
                }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _statusText.Text = message;
                    _statusText.Foreground = valid ? FgSuccess : FgError;
                    _validateButton.IsEnabled = true;
                }));
            });
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            var key = _apiKeyBox.Password?.Trim();
            SettingsManager.SetApiKey(key ?? "");
            SettingsManager.ClearCache();

            _statusText.Text = string.IsNullOrEmpty(key) ? "API key cleared" : "API key saved!";
            _statusText.Foreground = FgSuccess;

            // Close after brief delay
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
