using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
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
        private TextBox? _takeProfitInput;
        private TextBox? _lossLimitInput;
        private CheckBox? _autoFlattenCheckbox;
        private CheckBox? _disableOnLimitCheckbox;
        private TextBlock? _riskResetInfo;
        private CheckBox? _enableElasticCheckbox;
        private ComboBox? _elasticTriggerCombo;
        private TextBox? _profitThresholdInput;
        private ComboBox? _elasticIncrementCombo;
        private TextBox? _elasticIncrementValueInput;
        private CheckBox? _enableTrailingCheckbox;
        private ComboBox? _trailingActivationCombo;
        private TextBox? _trailingActivationValueInput;
        private ComboBox? _trailingStopCombo;
        private TextBox? _trailingStopValueInput;
        private TextBox? _demaMultiplierInput;
        private TextBox? _atrPeriodInput;
        private TextBox? _demaPeriodInput;

        public MultiStratPlugin()
        {
            Title = "Multi-Strat Bridge";
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
            PopulateRiskUi();
            PopulateTrailingUi();
        }

        public override void Dispose()
        {
            _managerService.Log -= OnBridgeLog;

            try
            {
                if (_managerService.IsConnected)
                {
                    try
                    {
                        _managerService.DisconnectAsync().ConfigureAwait(false).GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        AddLogEntry("ERROR", $"Error while stopping bridge: {ex.Message}");
                    }
                }

                _managerService.Dispose();
            }
            finally
            {
                base.Dispose();
            }
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
            _disconnectButton.Click += async (_, __) => await DisconnectAsync().ConfigureAwait(true);
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

            var riskSection = BuildRiskSection();
            root.Children.Add(riskSection);
            Grid.SetRow(riskSection, 2);

            var trailingSection = BuildTrailingSection();
            root.Children.Add(trailingSection);
            Grid.SetRow(trailingSection, 3);

            var accountsSection = BuildAccountsSection();
            root.Children.Add(accountsSection);
            Grid.SetRow(accountsSection, 4);

            _logList = new ListBox
            {
                ItemsSource = _logBuffer,
                Background = new SolidColorBrush(Color.FromRgb(28, 28, 28)),
                Foreground = Brushes.Gainsboro,
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(45, 45, 45))
            };

            root.Children.Add(_logList);
            Grid.SetRow(_logList, 5);

            return root;
        }

        private UIElement BuildRiskSection()
        {
            var group = new GroupBox
            {
                Header = "Risk Controls",
                Margin = new Thickness(0, 0, 0, 12)
            };

            var panel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(8, 4, 8, 4)
            };

            var thresholdRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 6)
            };

            thresholdRow.Children.Add(new TextBlock
            {
                Text = "Daily Take Profit",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            });

            _takeProfitInput = new TextBox
            {
                Width = 90,
                Margin = new Thickness(0, 0, 12, 0)
            };
            thresholdRow.Children.Add(_takeProfitInput);

            thresholdRow.Children.Add(new TextBlock
            {
                Text = "Daily Loss Limit",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            });

            _lossLimitInput = new TextBox
            {
                Width = 90
            };
            thresholdRow.Children.Add(_lossLimitInput);

            panel.Children.Add(thresholdRow);

            var flagsRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 6)
            };

            _autoFlattenCheckbox = new CheckBox
            {
                Content = "Flatten positions when limit hits",
                Margin = new Thickness(0, 0, 12, 0)
            };
            flagsRow.Children.Add(_autoFlattenCheckbox);

            _disableOnLimitCheckbox = new CheckBox
            {
                Content = "Disable tracking when limit hits"
            };
            flagsRow.Children.Add(_disableOnLimitCheckbox);

            panel.Children.Add(flagsRow);

            var buttonsRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 4)
            };

            var applyButton = new Button
            {
                Content = "Apply Risk",
                Width = 110,
                Margin = new Thickness(0, 0, 8, 0)
            };
            applyButton.Click += (_, __) => ApplyRiskSettings();
            buttonsRow.Children.Add(applyButton);

            var resetDailyButton = new Button
            {
                Content = "Reset Daily (All)",
                Width = 140,
                Margin = new Thickness(0, 0, 8, 0)
            };
            resetDailyButton.Click += (_, __) => ResetDailyRisk(null);
            buttonsRow.Children.Add(resetDailyButton);

            var flattenAllButton = new Button
            {
                Content = "Flatten All Accounts",
                Width = 160
            };
            flattenAllButton.Click += (_, __) => FlattenAllAccounts();
            buttonsRow.Children.Add(flattenAllButton);

            panel.Children.Add(buttonsRow);

            _riskResetInfo = new TextBlock
            {
                Margin = new Thickness(0, 2, 0, 0),
                Foreground = Brushes.Gray
            };
            panel.Children.Add(_riskResetInfo);

            group.Content = panel;
            return group;
        }

        private UIElement BuildTrailingSection()
        {
            var group = new GroupBox
            {
                Header = "Elastic / Trailing Settings",
                Margin = new Thickness(0, 0, 0, 12)
            };

            var grid = new Grid
            {
                Margin = new Thickness(8, 4, 8, 4)
            };

            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            int row = 0;

            UIElement AddRow(string label, UIElement editor)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var text = new TextBlock
                {
                    Text = label,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 2, 8, 2)
                };

                Grid.SetRow(text, row);
                Grid.SetColumn(text, 0);
                grid.Children.Add(text);

                Grid.SetRow(editor, row);
                Grid.SetColumn(editor, 1);
                grid.Children.Add(editor);

                row++;
                return editor;
            }

            _enableElasticCheckbox = (CheckBox)AddRow("Enable Elastic Hedging", new CheckBox());

            _elasticTriggerCombo = (ComboBox)AddRow("Elastic Trigger Units", new ComboBox { ItemsSource = Enum.GetValues(typeof(Services.TrailingElasticService.ProfitUnitType)) });
            _profitThresholdInput = (TextBox)AddRow("Profit Update Threshold", new TextBox { Width = 100 });

            _elasticIncrementCombo = (ComboBox)AddRow("Elastic Increment Units", new ComboBox { ItemsSource = Enum.GetValues(typeof(Services.TrailingElasticService.ProfitUnitType)) });
            _elasticIncrementValueInput = (TextBox)AddRow("Elastic Increment Value", new TextBox { Width = 100 });

            _enableTrailingCheckbox = (CheckBox)AddRow("Enable Trailing Updates", new CheckBox());

            _trailingActivationCombo = (ComboBox)AddRow("Trailing Activation Units", new ComboBox { ItemsSource = Enum.GetValues(typeof(Services.TrailingElasticService.ProfitUnitType)) });
            _trailingActivationValueInput = (TextBox)AddRow("Trailing Activation Value", new TextBox { Width = 100 });

            _trailingStopCombo = (ComboBox)AddRow("Trailing Stop Units", new ComboBox { ItemsSource = Enum.GetValues(typeof(Services.TrailingElasticService.ProfitUnitType)) });
            _trailingStopValueInput = (TextBox)AddRow("Trailing Stop Value", new TextBox { Width = 100 });

            _demaMultiplierInput = (TextBox)AddRow("DEMA/ATR Multiplier", new TextBox { Width = 100 });
            _atrPeriodInput = (TextBox)AddRow("ATR Period", new TextBox { Width = 100 });
            _demaPeriodInput = (TextBox)AddRow("DEMA Period", new TextBox { Width = 100 });

            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var applyButton = new Button
            {
                Content = "Apply Elastic/Trailing",
                Width = 180,
                Margin = new Thickness(0, 6, 0, 0)
            };
            applyButton.Click += (_, __) => ApplyTrailingSettings();
            Grid.SetRow(applyButton, row);
            Grid.SetColumn(applyButton, 1);
            grid.Children.Add(applyButton);

            group.Content = grid;
            return group;
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

        private void ApplyRiskSettings()
        {
            if (_takeProfitInput == null || _lossLimitInput == null)
            {
                return;
            }

            var culture = CultureInfo.CurrentCulture;

            MultiStratManagerService.RiskSnapshot? snapshot = null;
            try
            {
                snapshot = _managerService.GetRiskSnapshot();
            }
            catch (Exception ex)
            {
                AddLogEntry("WARN", $"Failed to read existing risk settings: {ex.Message}");
            }

            var takeProfit = ParseDouble(_takeProfitInput.Text, culture, snapshot?.DailyTakeProfit ?? 0);
            var lossLimit = ParseDouble(_lossLimitInput.Text, culture, snapshot?.DailyLossLimit ?? 0);
            var autoFlatten = _autoFlattenCheckbox?.IsChecked == true;
            var disableOnLimit = _disableOnLimitCheckbox?.IsChecked == true;

            try
            {
                _managerService.UpdateRiskSettings(new MultiStratManagerService.RiskSettingsUpdate(takeProfit, lossLimit, autoFlatten, disableOnLimit));
                AddLogEntry("INFO", $"Updated risk settings (TP={takeProfit:F2}, SL={lossLimit:F2})");
            }
            catch (Exception ex)
            {
                AddLogEntry("ERROR", $"Failed to update risk settings: {ex.Message}");
            }

            PopulateRiskUi();
        }

        private void ResetDailyRisk(string? accountId)
        {
            try
            {
                _managerService.ResetDailyRisk(accountId);
                AddLogEntry("INFO", accountId == null ? "Reset daily baselines for all accounts" : $"Reset daily baseline for {accountId}");
            }
            catch (Exception ex)
            {
                AddLogEntry("ERROR", $"Failed to reset daily baseline: {ex.Message}");
            }

            PopulateRiskUi();
            RenderAccounts();
        }

        private async void FlattenAllAccounts()
        {
            try
            {
                var success = await _managerService.FlattenAllAsync(disableAfter: _disableOnLimitCheckbox?.IsChecked == true, reason: "manual");
                AddLogEntry(success ? "INFO" : "WARN", success ? "Flattened all accounts" : "One or more accounts failed to flatten");
            }
            catch (Exception ex)
            {
                AddLogEntry("ERROR", $"Failed to flatten accounts: {ex.Message}");
            }

            PopulateRiskUi();
            RenderAccounts();
        }

        private async void FlattenAccount(AccountSubscription subscription)
        {
            try
            {
                var success = await _managerService.FlattenAccountAsync(subscription.AccountId, disableAfter: _disableOnLimitCheckbox?.IsChecked == true, reason: "manual");
                AddLogEntry(success ? "INFO" : "WARN", success ? $"Flattened account {subscription.DisplayName}" : $"Flatten for {subscription.DisplayName} reported issues");
            }
            catch (Exception ex)
            {
                AddLogEntry("ERROR", $"Error flattening account {subscription.DisplayName}: {ex.Message}");
            }

            PopulateRiskUi();
            RenderAccounts();
        }

        private void ApplyTrailingSettings()
        {
            try
            {
                var snapshot = _managerService.GetTrailingSettings();
                var culture = CultureInfo.CurrentCulture;

                var enableElastic = _enableElasticCheckbox?.IsChecked ?? snapshot.EnableElastic;
                var elasticTriggerUnits = _elasticTriggerCombo?.SelectedItem is Services.TrailingElasticService.ProfitUnitType etu ? etu : snapshot.ElasticTriggerUnits;
                var profitThreshold = ParseDouble(_profitThresholdInput?.Text, culture, snapshot.ProfitUpdateThreshold);
                var elasticIncrementUnits = _elasticIncrementCombo?.SelectedItem is Services.TrailingElasticService.ProfitUnitType eiu ? eiu : snapshot.ElasticIncrementUnits;
                var elasticIncrementValue = ParseDouble(_elasticIncrementValueInput?.Text, culture, snapshot.ElasticIncrementValue);
                var enableTrailing = _enableTrailingCheckbox?.IsChecked ?? snapshot.EnableTrailing;
                var trailingActivationUnits = _trailingActivationCombo?.SelectedItem is Services.TrailingElasticService.ProfitUnitType tau ? tau : snapshot.TrailingActivationUnits;
                var trailingActivationValue = ParseDouble(_trailingActivationValueInput?.Text, culture, snapshot.TrailingActivationValue);
                var trailingStopUnits = _trailingStopCombo?.SelectedItem is Services.TrailingElasticService.ProfitUnitType tsu ? tsu : snapshot.TrailingStopUnits;
                var trailingStopValue = ParseDouble(_trailingStopValueInput?.Text, culture, snapshot.TrailingStopValue);
                var demaMultiplier = ParseDouble(_demaMultiplierInput?.Text, culture, snapshot.DemaAtrMultiplier);
                var atrPeriod = ParseInt(_atrPeriodInput?.Text, snapshot.AtrPeriod);
                var demaPeriod = ParseInt(_demaPeriodInput?.Text, snapshot.DemaPeriod);

                var update = new MultiStratManagerService.TrailingSettingsUpdate(
                    enableElastic,
                    elasticTriggerUnits,
                    profitThreshold,
                    elasticIncrementUnits,
                    elasticIncrementValue,
                    enableTrailing,
                    trailingActivationUnits,
                    trailingActivationValue,
                    trailingStopUnits,
                    trailingStopValue,
                    demaMultiplier,
                    atrPeriod,
                    demaPeriod);

                _managerService.UpdateTrailingSettings(update);
                AddLogEntry("INFO", "Elastic/trailing settings updated");
            }
            catch (Exception ex)
            {
                AddLogEntry("ERROR", $"Failed to update elastic/trailing settings: {ex.Message}");
            }

            PopulateTrailingUi();
        }

        private static double ParseDouble(string? text, CultureInfo culture, double fallback = 0)
        {
            if (double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, culture, out var value))
            {
                return value;
            }

            return fallback;
        }

        private static int ParseInt(string? text, int fallback)
        {
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
        }

        private void PopulateRiskUi()
        {
            if (_takeProfitInput == null || _lossLimitInput == null)
            {
                return;
            }

            try
            {
                var snapshot = _managerService.GetRiskSnapshot();
                _takeProfitInput.Text = snapshot.DailyTakeProfit.ToString("F2", CultureInfo.CurrentCulture);
                _lossLimitInput.Text = snapshot.DailyLossLimit.ToString("F2", CultureInfo.CurrentCulture);

                if (_autoFlattenCheckbox != null)
                {
                    _autoFlattenCheckbox.IsChecked = snapshot.AutoFlatten;
                }

                if (_disableOnLimitCheckbox != null)
                {
                    _disableOnLimitCheckbox.IsChecked = snapshot.DisableOnLimit;
                }

                if (_riskResetInfo != null)
                {
                    _riskResetInfo.Text = $"Last reset: {snapshot.LastResetDateUtc:yyyy-MM-dd}";
                }
            }
            catch (Exception ex)
            {
                AddLogEntry("WARN", $"Unable to load risk snapshot: {ex.Message}");
            }
        }

        private void PopulateTrailingUi()
        {
            try
            {
                var snapshot = _managerService.GetTrailingSettings();

                if (_enableElasticCheckbox != null)
                {
                    _enableElasticCheckbox.IsChecked = snapshot.EnableElastic;
                }

                if (_elasticTriggerCombo != null)
                {
                    _elasticTriggerCombo.SelectedItem = snapshot.ElasticTriggerUnits;
                }

                if (_profitThresholdInput != null)
                {
                    _profitThresholdInput.Text = snapshot.ProfitUpdateThreshold.ToString("F2", CultureInfo.CurrentCulture);
                }

                if (_elasticIncrementCombo != null)
                {
                    _elasticIncrementCombo.SelectedItem = snapshot.ElasticIncrementUnits;
                }

                if (_elasticIncrementValueInput != null)
                {
                    _elasticIncrementValueInput.Text = snapshot.ElasticIncrementValue.ToString("F2", CultureInfo.CurrentCulture);
                }

                if (_enableTrailingCheckbox != null)
                {
                    _enableTrailingCheckbox.IsChecked = snapshot.EnableTrailing;
                }

                if (_trailingActivationCombo != null)
                {
                    _trailingActivationCombo.SelectedItem = snapshot.TrailingActivationUnits;
                }

                if (_trailingActivationValueInput != null)
                {
                    _trailingActivationValueInput.Text = snapshot.TrailingActivationValue.ToString("F2", CultureInfo.CurrentCulture);
                }

                if (_trailingStopCombo != null)
                {
                    _trailingStopCombo.SelectedItem = snapshot.TrailingStopUnits;
                }

                if (_trailingStopValueInput != null)
                {
                    _trailingStopValueInput.Text = snapshot.TrailingStopValue.ToString("F2", CultureInfo.CurrentCulture);
                }

                if (_demaMultiplierInput != null)
                {
                    _demaMultiplierInput.Text = snapshot.DemaAtrMultiplier.ToString("F2", CultureInfo.CurrentCulture);
                }

                if (_atrPeriodInput != null)
                {
                    _atrPeriodInput.Text = snapshot.AtrPeriod.ToString(CultureInfo.CurrentCulture);
                }

                if (_demaPeriodInput != null)
                {
                    _demaPeriodInput.Text = snapshot.DemaPeriod.ToString(CultureInfo.CurrentCulture);
                }
            }
            catch (Exception ex)
            {
                AddLogEntry("WARN", $"Unable to load trailing settings: {ex.Message}");
            }
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
                        PopulateRiskUi();
                        PopulateTrailingUi();
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

        private async Task DisconnectAsync()
        {
            try
            {
                await _managerService.DisconnectAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                AddLogEntry("WARN", $"Error during disconnection: {ex.Message}");
            }

            UpdateStatus("Disconnected", Brushes.Gray);
            SetButtonStates(connected: false);
            PopulateRiskUi();
            PopulateTrailingUi();
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

                if (entry.Message.IndexOf("Risk limit", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    PopulateRiskUi();
                    RenderAccounts();
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
                PopulateRiskUi();
                PopulateTrailingUi();
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

                MultiStratManagerService.RiskSnapshot? riskSnapshot = null;
                try
                {
                    riskSnapshot = _managerService.GetRiskSnapshot();
                }
                catch (Exception ex)
                {
                    AddLogEntry("WARN", $"Failed to load risk snapshot for accounts: {ex.Message}");
                }

                var riskLookup = riskSnapshot?.Accounts.ToDictionary(a => a.AccountId, a => a);

                foreach (var account in _managerService.Accounts)
                {
                    var subscription = account;
                    var row = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Margin = new Thickness(0, 2, 0, 2)
                    };

                    var checkbox = new CheckBox
                    {
                        Content = subscription.DisplayName,
                        IsChecked = subscription.IsEnabled,
                        VerticalAlignment = VerticalAlignment.Center,
                        MinWidth = 180
                    };
                    checkbox.Checked += (_, __) => subscription.IsEnabled = true;
                    checkbox.Unchecked += (_, __) => subscription.IsEnabled = false;
                    row.Children.Add(checkbox);

                    var flattenButton = new Button
                    {
                        Content = "Flatten",
                        Width = 80,
                        Margin = new Thickness(8, 0, 0, 0),
                        Tag = subscription
                    };
                    flattenButton.Click += (s, _) =>
                    {
                        if (s is Button btn && btn.Tag is AccountSubscription sub)
                        {
                            FlattenAccount(sub);
                        }
                    };
                    row.Children.Add(flattenButton);

                    var resetButton = new Button
                    {
                        Content = "Reset",
                        Width = 70,
                        Margin = new Thickness(4, 0, 0, 0),
                        Tag = subscription
                    };
                    resetButton.Click += (s, _) =>
                    {
                        if (s is Button btn && btn.Tag is AccountSubscription sub)
                        {
                            ResetDailyRisk(sub.AccountId);
                        }
                    };
                    row.Children.Add(resetButton);

                    var infoText = new TextBlock
                    {
                        Margin = new Thickness(8, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = Brushes.Gray
                    };

                    if (riskLookup != null && riskLookup.TryGetValue(subscription.AccountId, out var riskState))
                    {
                        infoText.Text = $"PnL: {riskState.LastKnownPnL:F2}";
                        if (riskState.LimitTriggered)
                        {
                            infoText.Text += " (Limit Hit)";
                            infoText.Foreground = Brushes.OrangeRed;
                        }
                    }

                    row.Children.Add(infoText);

                    _accountsPanel.Children.Add(row);
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
