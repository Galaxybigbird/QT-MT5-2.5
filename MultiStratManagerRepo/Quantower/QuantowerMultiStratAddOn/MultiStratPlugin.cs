using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TradingPlatform.PresentationLayer.Plugins;

namespace Quantower.MultiStrat
{
    public class MultiStratPlugin : Plugin
    {
        private readonly ObservableCollection<string> _logBuffer = new();
        private readonly MultiStratManagerService _managerService = new();

        private Dispatcher? _dispatcher;
        private TextBox? _addressTextBox;
        private Button? _connectButton;
        private Button? _disconnectButton;
        private TextBlock? _statusText;
        private ListBox? _logList;
        private StackPanel? _accountsPanel;

        public MultiStratPlugin()
        {
            Title = "Multi-Strat Bridge";
            CreationType = PluginCreationType.Dockable;
            AllowSaveTemplates = false;
            AllowDataExport = false;
            AllowShowNextTimeOnClosing = false;
            UseCloseConfirmation = false;
        }

        public override void Initialize()
        {
            base.Initialize();

            _dispatcher = Dispatcher.CurrentDispatcher;
            _managerService.Log += OnBridgeLog;

            var layout = BuildLayout();
            AttachContent(layout);

            UpdateStatus("Disconnected", Brushes.Gray);
            try
            {
                _managerService.RefreshAccounts();
            }
            catch (Exception ex)
            {
                AddLogEntry("WARN", $"Unable to load accounts: {ex.Message}");
            }
            RenderAccounts();
        }

        public override void Close()
        {
            _managerService.Log -= OnBridgeLog;

            if (_managerService.IsConnected)
            {
                try
                {
                    _managerService.DisconnectAsync().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    AddLogEntry("ERROR", $"Error while stopping bridge: {ex.Message}");
                }
            }

            _managerService.Dispose();

            base.Close();
        }

        private UIElement BuildLayout()
        {
            var root = new Grid
            {
                Margin = new Thickness(16)
            };

            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 12)
            };

            header.Children.Add(new TextBlock
            {
                Text = "Bridge gRPC endpoint:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });

            _addressTextBox = new TextBox
            {
                Width = 280,
                Text = _managerService.CurrentAddress ?? "http://127.0.0.1:50051"
            };
            header.Children.Add(_addressTextBox);

            _connectButton = new Button
            {
                Content = "Connect",
                Width = 90,
                Margin = new Thickness(8, 0, 0, 0)
            };
            _connectButton.Click += async (_, __) => await ConnectAsync().ConfigureAwait(true);
            header.Children.Add(_connectButton);

            _disconnectButton = new Button
            {
                Content = "Disconnect",
                Width = 100,
                Margin = new Thickness(8, 0, 0, 0),
                IsEnabled = false
            };
            _disconnectButton.Click += (_, __) => Disconnect();
            header.Children.Add(_disconnectButton);

            root.Children.Add(header);
            Grid.SetRow(header, 0);

            var statusPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 12)
            };

            statusPanel.Children.Add(new TextBlock
            {
                Text = "Status:",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 8, 0)
            });

            _statusText = new TextBlock
            {
                Text = "Disconnected",
                Foreground = Brushes.Gray
            };
            statusPanel.Children.Add(_statusText);

            root.Children.Add(statusPanel);
            Grid.SetRow(statusPanel, 1);

            var accountsSection = BuildAccountsSection();
            root.Children.Add(accountsSection);
            Grid.SetRow(accountsSection, 2);

            _logList = new ListBox
            {
                ItemsSource = _logBuffer,
                Background = new SolidColorBrush(Color.FromRgb(28, 28, 28)),
                Foreground = Brushes.Gainsboro,
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(45, 45, 45))
            };

            root.Children.Add(_logList);
            Grid.SetRow(_logList, 3);

            return root;
        }

        private UIElement BuildAccountsSection()
        {
            var container = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(0, 0, 0, 12)
            };

            var header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left
            };

            header.Children.Add(new TextBlock
            {
                Text = "Accounts:",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 8, 0)
            });

            var refreshButton = new Button
            {
                Content = "Refresh",
                Width = 80,
                Margin = new Thickness(0, 0, 0, 4)
            };
            refreshButton.Click += (_, __) => RefreshAccounts();
            header.Children.Add(refreshButton);

            container.Children.Add(header);

            _accountsPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(8, 4, 0, 0)
            };
            container.Children.Add(_accountsPanel);

            return container;
        }

        private async Task ConnectAsync()
        {
            if (_addressTextBox == null)
            {
                return;
            }

            var address = _addressTextBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(address))
            {
                AddLogEntry("WARN", "Please provide a valid gRPC address.");
                return;
            }

            SetButtonsBusy(true);
            UpdateStatus("Connecting...", Brushes.DarkOrange);

            try
            {
                var ok = await _managerService.ConnectAsync(address).ConfigureAwait(true);
                if (ok)
                {
                    try
                    {
                        _managerService.RefreshAccounts();
                        RenderAccounts();
                    }
                    catch (Exception ex)
                    {
                        AddLogEntry("WARN", $"Failed to refresh accounts: {ex.Message}");
                    }

                    UpdateStatus("Connected", Brushes.LightGreen);
                    SetButtonStates(connected: true);
                }
                else
                {
                    UpdateStatus("Connection failed", Brushes.IndianRed);
                    SetButtonStates(connected: false);
                }
            }
            catch (Exception ex)
            {
                AddLogEntry("ERROR", $"Connection attempt failed: {ex.Message}");
                UpdateStatus("Connection failed", Brushes.IndianRed);
                SetButtonStates(connected: false);
            }
            finally
            {
                SetButtonsBusy(false);
            }
        }

        private void Disconnect()
        {
            try
            {
                _managerService.DisconnectAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                AddLogEntry("WARN", $"Error during disconnection: {ex.Message}");
            }

            UpdateStatus("Disconnected", Brushes.Gray);
            SetButtonStates(connected: false);
        }

        private void OnBridgeLog(QuantowerBridgeService.BridgeLogEntry entry)
        {
            void Append()
            {
                var formatted = $"[{entry.TimestampUtc:HH:mm:ss}] {entry.Level}: {entry.Message}" +
                                 (string.IsNullOrWhiteSpace(entry.Details) ? string.Empty : $" ({entry.Details})");
                _logBuffer.Add(formatted);

                const int MaxEntries = 500;
                if (_logBuffer.Count > MaxEntries)
                {
                    _logBuffer.RemoveAt(0);
                }

                if (_logList != null && _logList.Items.Count > 0)
                {
                    _logList.ScrollIntoView(_logList.Items[_logList.Items.Count - 1]);
                }
            }

            if (_dispatcher == null)
            {
                Append();
            }
            else
            {
                _ = _dispatcher.InvokeAsync(Append, DispatcherPriority.Background);
            }
        }

        private void AddLogEntry(string level, string message)
        {
            var lvl = Enum.TryParse(level, true, out QuantowerBridgeService.BridgeLogLevel parsed) ? parsed : QuantowerBridgeService.BridgeLogLevel.Info;
            OnBridgeLog(new QuantowerBridgeService.BridgeLogEntry(DateTime.UtcNow, lvl, message, null, null, null));
        }

        private void RefreshAccounts()
        {
            try
            {
                _managerService.RefreshAccounts();
                RenderAccounts();
            }
            catch (Exception ex)
            {
                AddLogEntry("WARN", $"Failed to refresh accounts: {ex.Message}");
            }
        }

        private void RenderAccounts()
        {
            void Render()
            {
                if (_accountsPanel == null)
                {
                    return;
                }

                _accountsPanel.Children.Clear();

                foreach (var account in _managerService.Accounts)
                {
                    var subscription = account;
                    var checkbox = new CheckBox
                    {
                        Content = subscription.DisplayName,
                        IsChecked = subscription.IsEnabled,
                        Margin = new Thickness(0, 2, 0, 2)
                    };
                    checkbox.Checked += (_, __) => subscription.IsEnabled = true;
                    checkbox.Unchecked += (_, __) => subscription.IsEnabled = false;
                    _accountsPanel.Children.Add(checkbox);
                }

                _accountsPanel.IsEnabled = _managerService.IsConnected;
            }

            if (_dispatcher == null || _dispatcher.CheckAccess())
            {
                Render();
            }
            else
            {
                _dispatcher.Invoke(Render);
            }
        }

        private void UpdateStatus(string text, Brush color)
        {
            void Update()
            {
                if (_statusText != null)
                {
                    _statusText.Text = text;
                    _statusText.Foreground = color;
                }
            }

            if (_dispatcher == null)
            {
                Update();
            }
            else
            {
                _ = _dispatcher.InvokeAsync(Update, DispatcherPriority.Render);
            }
        }

        private void SetButtonsBusy(bool busy)
        {
            void Update()
            {
                if (_connectButton != null)
                {
                    _connectButton.IsEnabled = !busy && !_managerService.IsConnected;
                }

                if (_disconnectButton != null)
                {
                    _disconnectButton.IsEnabled = !busy && _managerService.IsConnected;
                }

                if (_accountsPanel != null)
                {
                    _accountsPanel.IsEnabled = !busy && _managerService.IsConnected;
                }
            }

            if (_dispatcher == null)
            {
                Update();
            }
            else
            {
                _ = _dispatcher.InvokeAsync(Update, DispatcherPriority.Normal);
            }
        }

        private void SetButtonStates(bool connected)
        {
            void Update()
            {
                if (_connectButton != null)
                {
                    _connectButton.IsEnabled = !connected;
                }

                if (_disconnectButton != null)
                {
                    _disconnectButton.IsEnabled = connected;
                }

                if (_accountsPanel != null)
                {
                    _accountsPanel.IsEnabled = connected;
                }
            }

            if (_dispatcher == null)
            {
                Update();
            }
            else
            {
                _ = _dispatcher.InvokeAsync(Update, DispatcherPriority.Background);
            }

            if (connected)
            {
                RenderAccounts();
            }
        }

        private void AttachContent(UIElement element)
        {
            var windowProperty = GetType().BaseType?.GetProperty("Window") ?? GetType().GetProperty("Window");
            var windowInstance = windowProperty?.GetValue(this);
            var contentProperty = windowInstance?.GetType().GetProperty("Content");
            contentProperty?.SetValue(windowInstance, element);
        }
    }
}
