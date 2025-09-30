using System;
#nullable disable

using System.Collections.Generic;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TradingPlatform.PresentationLayer.Plugins;
using Quantower.MultiStrat.Utilities;
using Quantower.Bridge.Client;
using WpfApplication = System.Windows.Application;

namespace Quantower.MultiStrat
{
    public class MultiStratPlugin : Plugin
    {
        // Required by Quantower to make the panel discoverable and creatable from UI
        public static PluginInfo GetInfo()
        {
            SafeFileDebug("GetInfo() called");

            try
            {
                // Simplified configuration - minimal browser panel
                var info = new PluginInfo
                {
                    Name = "MultiStratQuantower",
                    Title = "Multi-Strat Bridge",
                    Group = PluginGroup.Misc,
                    ShortName = "MSB",
                    // Use browser template
                    TemplateName = "layout.html",
                    // Minimal window parameters
                    WindowParameters = new NativeWindowParameters(NativeWindowParameters.Panel)
                    {
                        HeaderVisible = true,
                        BindingBehaviour = BindingBehaviour.Bindable,
                        BrowserUsageType = BrowserUsageType.Default
                    },
                    CustomProperties = new System.Collections.Generic.Dictionary<string, object>
                    {
                        { PluginInfo.Const.ALLOW_MANUAL_CREATION, true }
                    }
                };

                SafeFileDebug($"GetInfo() returning plugin info: Name={info.Name}, Title={info.Title}, Template={info.TemplateName}");
                return info;
            }
            catch (Exception ex)
            {
                SafeFileDebug($"GetInfo() failed: {ex.Message}");
                SafeFileDebug($"GetInfo() stack trace: {ex.StackTrace}");
                throw;
            }
        }

        public override System.Drawing.Size DefaultSize => new System.Drawing.Size(720, 540);

        private readonly ObservableCollection<string> _logBuffer = new();
        private readonly MultiStratManagerService _managerService = new();

        private Dispatcher? _dispatcher;
        private SynchronizationContext? _uiContext;
        private TextBox? _addressTextBox;
        private Button? _connectButton;
        private Button? _disconnectButton;
        private TextBlock? _statusText;
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
        private CheckBox? _enableDemaCheckbox;
        private ComboBox? _trailingActivationCombo;
        private TextBox? _trailingActivationValueInput;
        private ComboBox? _trailingStopCombo;
        private TextBox? _trailingStopValueInput;
        private TextBox? _demaMultiplierInput;
        private TextBox? _atrPeriodInput;
        private TextBox? _demaPeriodInput;
        // Browser bridge fields
        private object? _browser;
        private bool _browserBridgeAttached;
        private bool _serviceHandlersAttached;
        private bool _initialized;
        private string _statusTextValue = "Initializing";
        private bool _isBusy;
        private bool _hasEverConnected;
        private bool _userInitiatedDisconnect;
        private bool _pendingConnect;
        private string? _pendingAddress;
        private BridgeGrpcClient.StreamingState _lastStreamingState = BridgeGrpcClient.StreamingState.Disconnected;





        private static string JsEscape(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\r", string.Empty).Replace("\n", "\\n");
        }

        static MultiStratPlugin()
        {
            SafeFileDebug("MultiStratPlugin static ctor loaded.");

            try
            {
                AppDomain.CurrentDomain.FirstChanceException += (_, args) =>
                {
                    try
                    {
                        var ex = args.Exception;
                        if (ex == null)
                        {
                            return;
                        }

                        if (ex is System.BadImageFormatException bad && !string.IsNullOrEmpty(bad.FileName))
                        {
                            var file = bad.FileName;
                            if (!string.IsNullOrEmpty(file) && file.IndexOf("Native\\\\Windows", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                return;
                            }
                        }

                        if (ex is System.IO.FileNotFoundException missing && !string.IsNullOrEmpty(missing.FileName))
                        {
                            if (missing.FileName.IndexOf("CefSharp.Wpf.resources.dll", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                return;
                            }
                        }

                        if (ex is System.OperationCanceledException || ex is System.Threading.Tasks.TaskCanceledException)
                        {
                            return;
                        }

                        if (ex is System.IO.IOException io && !string.IsNullOrEmpty(io.Message) && io.Message.IndexOf("msb-plugin.log", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return;
                        }

                        SafeFileDebug($"FirstChanceException: {ex.GetType().FullName}: {ex.Message}");
                    }
                    catch
                    {
                        // ignore logging failures
                    }
                };

                AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                {
                    try
                    {
                        if (args.ExceptionObject is Exception ex)
                        {
                            SafeFileDebug($"UnhandledException: {ex.GetType().FullName}: {ex.Message}");
                        }
                        else
                        {
                            SafeFileDebug($"UnhandledException: {args.ExceptionObject}");
                        }
                    }
                    catch { }
                };
            }
            catch (Exception ex)
            {
                SafeFileDebug($"Failed to hook AppDomain diagnostics: {ex.Message}");
            }
        }












        private void SetWindowAction(string action)
        {
            try
            {
                var w = this.Window;
                if (w == null) return;
                action = action.ToLowerInvariant();

                if (action == "close")
                {
                    var m = w.GetType().GetMethod("Close");
                    m?.Invoke(w, null);
                    return;
                }
                // try common property patterns
                var stateProp = w.GetType().GetProperty("WindowState") ?? w.GetType().GetProperty("State");
                if (stateProp != null && stateProp.PropertyType.IsEnum)
                {
                    var names = Enum.GetNames(stateProp.PropertyType);
                    object? val = null;
                    if (action == "minimize") val = Enum.Parse(stateProp.PropertyType, names.FirstOrDefault(n=>n.Equals("Minimized", StringComparison.OrdinalIgnoreCase)) ?? names.First());
                    if (action == "maximize") val = Enum.Parse(stateProp.PropertyType, names.FirstOrDefault(n=>n.Equals("Maximized", StringComparison.OrdinalIgnoreCase)) ?? names.First());
                    if (val != null) stateProp.SetValue(w, val);
                    return;
                }
                // fallback methods
                var min = w.GetType().GetMethod("Minimize");
                var max = w.GetType().GetMethod("Maximize");
                if (action == "minimize") min?.Invoke(w, null);
                if (action == "maximize") max?.Invoke(w, null);
            }
            catch { /* ignore */ }
        }

        public MultiStratPlugin()
        {
            try
            {
                SafeFileDebug("MultiStratPlugin instance ctor started.");
                // Don't set Title in constructor - it causes null reference in Quantower framework
                // Title will be set from PluginInfo.Title instead
                SafeFileDebug("MultiStratPlugin instance ctor completed successfully.");
            }
            catch (Exception ex)
            {
                SafeFileDebug($"MultiStratPlugin constructor failed: {ex.Message}");
                SafeFileDebug($"MultiStratPlugin constructor stack trace: {ex.StackTrace}");
                throw;
            }
        }

        public override void Initialize()
        {
            SafeFileDebug("=== Initialize() CALLED ===");

            if (_initialized)
            {
                SafeFileDebug("Initialize() invoked more than once; returning early.");
                return;
            }

            try
            {
                base.Initialize();
                SafeFileDebug("base.Initialize() completed");

                try
                {
                    Title = "Multi-Strat Bridge";
                    SafeFileDebug("Title set successfully in Initialize()");
                }
                catch (Exception ex)
                {
                    SafeFileDebug($"Failed to set title in Initialize(): {ex.Message}");
                }

                _uiContext = SynchronizationContext.Current;
                SafeFileDebug($"Initialize entered. Thread={Thread.CurrentThread.ManagedThreadId}");
            }
            catch (Exception ex)
            {
                SafeFileDebug($"Initialize() failed: {ex.Message}");
                SafeFileDebug($"Initialize() stack trace: {ex.StackTrace}");
                throw;
            }

            _dispatcher = WpfApplication.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

            EnsureServiceHandlers();

            var browserReady = TryInitializeBrowserUi();
            if (!browserReady)
            {
                AttachWpfFallbackUi();
            }

            StartAutoConnectIfNeeded();

            SafeFileDebug(browserReady
                ? "Initialize completed (browser host)."
                : "Initialize completed (WPF fallback).");

            _initialized = true;
        }

        public override void Dispose()
        {
            _managerService.Log -= OnBridgeLog;
            _managerService.AccountsChanged -= OnAccountsChanged;
            _managerService.PropertyChanged -= OnManagerPropertyChanged;
            _managerService.StreamingStateChanged -= OnBridgeStreamingStateChanged;

            try
            {
                if (_managerService.IsConnected)
                {
                    var disconnectTask = _managerService.DisconnectAsync("plugin dispose");

                    if (!disconnectTask.IsCompleted)
                    {
                        _ = disconnectTask.ContinueWith(t =>
                        {
                            if (t.IsFaulted)
                            {
                                AddLogEntry("ERROR", $"Error while stopping bridge: {t.Exception?.GetBaseException().Message}");
                            }
                        }, TaskScheduler.Default);
                    }
                    else if (disconnectTask.IsFaulted)
                    {
                        AddLogEntry("ERROR", $"Error while stopping bridge: {disconnectTask.Exception?.GetBaseException().Message}");
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
            _connectButton.Click += async (_, __) =>
            {
                try
                {
                    await ConnectAsync().ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    AddLogEntry("ERROR", $"Connect handler failed: {ex.Message}");
                }
            };
            header.Children.Add(_connectButton);

            _disconnectButton = new Button
            {
                Content = "Disconnect",
                Width = 100,
                Margin = new Thickness(8, 0, 0, 0),
                IsEnabled = false
            };
            _disconnectButton.Click += async (_, __) =>
            {
                try
                {
                    await DisconnectAsync().ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    AddLogEntry("ERROR", $"Disconnect handler failed: {ex.Message}");
                }
            };
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
            flattenAllButton.Click += async (_, __) =>
            {
                try
                {
                    await FlattenAllAccountsAsync().ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    AddLogEntry("ERROR", $"Flatten all handler failed: {ex.Message}");
                }
            };
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

            var enableElasticCheckbox = AddRow("Enable Elastic Hedging", new CheckBox()) as CheckBox;
            if (enableElasticCheckbox == null) { AddLogEntry("ERROR", "Failed to create Enable Elastic Hedging checkbox"); throw new InvalidOperationException("Elastic checkbox not created"); }
            _enableElasticCheckbox = enableElasticCheckbox;

            var elasticTriggerCombo = AddRow("Elastic Trigger Units", new ComboBox { ItemsSource = Enum.GetValues(typeof(Services.TrailingElasticService.ProfitUnitType)) }) as ComboBox;
            if (elasticTriggerCombo == null) { AddLogEntry("ERROR", "Failed to create Elastic Trigger Units combo"); throw new InvalidOperationException("Elastic trigger combo not created"); }
            _elasticTriggerCombo = elasticTriggerCombo;

            var profitThresholdInput = AddRow("Profit Update Threshold", new TextBox { Width = 100 }) as TextBox;
            if (profitThresholdInput == null) { AddLogEntry("ERROR", "Failed to create Profit Update Threshold input"); throw new InvalidOperationException("Profit threshold input not created"); }
            _profitThresholdInput = profitThresholdInput;

            var elasticIncrementCombo = AddRow("Elastic Increment Units", new ComboBox { ItemsSource = Enum.GetValues(typeof(Services.TrailingElasticService.ProfitUnitType)) }) as ComboBox;
            if (elasticIncrementCombo == null) { AddLogEntry("ERROR", "Failed to create Elastic Increment Units combo"); throw new InvalidOperationException("Elastic increment combo not created"); }
            _elasticIncrementCombo = elasticIncrementCombo;

            var elasticIncrementValueInput = AddRow("Elastic Increment Value", new TextBox { Width = 100 }) as TextBox;
            if (elasticIncrementValueInput == null) { AddLogEntry("ERROR", "Failed to create Elastic Increment Value input"); throw new InvalidOperationException("Elastic increment value input not created"); }
            _elasticIncrementValueInput = elasticIncrementValueInput;

            var enableTrailingCheckbox = AddRow("Enable Trailing Updates", new CheckBox()) as CheckBox;
            if (enableTrailingCheckbox == null) { AddLogEntry("ERROR", "Failed to create Enable Trailing Updates checkbox"); throw new InvalidOperationException("Trailing checkbox not created"); }
            _enableTrailingCheckbox = enableTrailingCheckbox;

            var enableDemaCheckbox = AddRow("Enable DEMA/ATR Trailing", new CheckBox()) as CheckBox;
            if (enableDemaCheckbox == null) { AddLogEntry("ERROR", "Failed to create Enable DEMA/ATR checkbox"); throw new InvalidOperationException("DEMA checkbox not created"); }
            _enableDemaCheckbox = enableDemaCheckbox;

            var trailingActivationCombo = AddRow("Trailing Activation Units", new ComboBox { ItemsSource = Enum.GetValues(typeof(Services.TrailingElasticService.ProfitUnitType)) }) as ComboBox;
            if (trailingActivationCombo == null) { AddLogEntry("ERROR", "Failed to create Trailing Activation Units combo"); throw new InvalidOperationException("Trailing activation combo not created"); }
            _trailingActivationCombo = trailingActivationCombo;

            var trailingActivationValueInput = AddRow("Trailing Activation Value", new TextBox { Width = 100 }) as TextBox;
            if (trailingActivationValueInput == null) { AddLogEntry("ERROR", "Failed to create Trailing Activation Value input"); throw new InvalidOperationException("Trailing activation value input not created"); }
            _trailingActivationValueInput = trailingActivationValueInput;

            var trailingStopCombo = AddRow("Trailing Stop Units", new ComboBox { ItemsSource = Enum.GetValues(typeof(Services.TrailingElasticService.ProfitUnitType)) }) as ComboBox;
            if (trailingStopCombo == null) { AddLogEntry("ERROR", "Failed to create Trailing Stop Units combo"); throw new InvalidOperationException("Trailing stop combo not created"); }
            _trailingStopCombo = trailingStopCombo;

            var trailingStopValueInput = AddRow("Trailing Stop Value", new TextBox { Width = 100 }) as TextBox;
            if (trailingStopValueInput == null) { AddLogEntry("ERROR", "Failed to create Trailing Stop Value input"); throw new InvalidOperationException("Trailing stop value input not created"); }
            _trailingStopValueInput = trailingStopValueInput;

            var demaMultiplierInput = AddRow("DEMA/ATR Multiplier", new TextBox { Width = 100 }) as TextBox;
            if (demaMultiplierInput == null) { AddLogEntry("ERROR", "Failed to create DEMA/ATR Multiplier input"); throw new InvalidOperationException("DEMA multiplier input not created"); }
            _demaMultiplierInput = demaMultiplierInput;

            var atrPeriodInput = AddRow("ATR Period", new TextBox { Width = 100 }) as TextBox;
            if (atrPeriodInput == null) { AddLogEntry("ERROR", "Failed to create ATR Period input"); throw new InvalidOperationException("ATR period input not created"); }
            _atrPeriodInput = atrPeriodInput;

            var demaPeriodInput = AddRow("DEMA Period", new TextBox { Width = 100 }) as TextBox;
            if (demaPeriodInput == null) { AddLogEntry("ERROR", "Failed to create DEMA Period input"); throw new InvalidOperationException("DEMA period input not created"); }
            _demaPeriodInput = demaPeriodInput;

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

        private async Task FlattenAllAccountsAsync()
        {
            try
            {
                var success = await _managerService.FlattenAllAsync(disableAfter: _disableOnLimitCheckbox?.IsChecked == true, reason: "manual").ConfigureAwait(true);
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
                var useDemaAtr = _enableDemaCheckbox?.IsChecked ?? snapshot.UseDemaAtrTrailing;
                // REMOVED: trailingActivationUnits and trailingActivationValue
                // Trailing now uses the SAME trigger as elastic
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
                    useDemaAtr,
                    // REMOVED: trailingActivationUnits and trailingActivationValue
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

                if (_enableDemaCheckbox != null)
                {
                    _enableDemaCheckbox.IsChecked = snapshot.UseDemaAtrTrailing;
                }

                // REMOVED: _trailingActivationCombo and _trailingActivationValueInput
                // Trailing now uses the SAME trigger as elastic

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
            _pendingConnect = true;
            _pendingAddress = address;
            _userInitiatedDisconnect = false;

            try
            {
                var ok = await _managerService.ConnectAsync(address).ConfigureAwait(true);
                if (ok)
                {
                    var fullyOnline = _managerService.IsConnected;
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

                    if (fullyOnline)
                    {
                        UpdateStatus("Connected", Brushes.LightGreen);
                        SetButtonStates(connected: true);
                        _pendingConnect = false;
                        _pendingAddress = null;
                    }
                    else
                    {
                        var awaitingLabel = string.IsNullOrWhiteSpace(address)
                            ? "Awaiting trading stream"
                            : $"Awaiting trading stream on {address}";
                        UpdateStatus(awaitingLabel, Brushes.DarkOrange);
                        SetButtonStates(connected: false);
                        _pendingConnect = true;
                        _pendingAddress = address;
                    }
                }
                else
                {
                    UpdateStatus("Connection failed", Brushes.IndianRed);
                    SetButtonStates(connected: false);
                    _pendingConnect = false;
                    _pendingAddress = null;
                }
            }
            catch (Exception ex)
            {
                AddLogEntry("ERROR", $"Connection attempt failed: {ex.Message}");
                UpdateStatus("Connection failed", Brushes.IndianRed);
                SetButtonStates(connected: false);
                _pendingConnect = false;
                _pendingAddress = null;
            }
            finally
            {
                SetButtonsBusy(false);
            }
        }

        private async Task DisconnectAsync()
        {
            _userInitiatedDisconnect = true;
            _pendingConnect = false;
            _pendingAddress = null;

            try
            {
                await _managerService.DisconnectAsync("ui command").ConfigureAwait(true);
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

                SafeFileDebug(formatted, alsoLogToBridge: false);

                // Push to Browser UI if present
                TryBrowserInvokeJs($"window.MSB && MSB.appendLog('{JsEscape(formatted)}')");

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

        private void AddLogEntry(string level, string message, string? details = null)
        {
            var lvl = Enum.TryParse(level, true, out QuantowerBridgeService.BridgeLogLevel parsed) ? parsed : QuantowerBridgeService.BridgeLogLevel.Info;
            EmitPluginLog(lvl, message, details);
        }

        private void EmitPluginLog(QuantowerBridgeService.BridgeLogLevel level, string message, string? details = null)
        {
            TryLogToBridge(level, message, details);
            OnBridgeLog(new QuantowerBridgeService.BridgeLogEntry(DateTime.UtcNow, level, message, null, null, details));
        }

        private static void TryLogToBridge(QuantowerBridgeService.BridgeLogLevel level, string message, string? details)
        {
            var payload = string.IsNullOrWhiteSpace(details) ? message : $"{message} ({details})";

            try
            {
                switch (level)
                {
                    case QuantowerBridgeService.BridgeLogLevel.Debug:
                        BridgeGrpcClient.LogDebug("qt_addon_ui", payload);
                        break;
                    case QuantowerBridgeService.BridgeLogLevel.Info:
                        BridgeGrpcClient.LogInfo("qt_addon_ui", payload);
                        break;
                    case QuantowerBridgeService.BridgeLogLevel.Warn:
                        BridgeGrpcClient.LogWarn("qt_addon_ui", payload);
                        break;
                    case QuantowerBridgeService.BridgeLogLevel.Error:
                        BridgeGrpcClient.LogError("qt_addon_ui", payload, errorCode: "ui_log");
                        break;
                }
            }
            catch
            {
                // Bridge client may not be initialized yet; ignore and rely on local log sink.
            }
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

        private void OnAccountsChanged(object? sender, EventArgs e)
        {
            void UpdateUi()
            {
                RenderAccounts();
                PopulateRiskUi();
                PushStatusToBrowser();
            }

            var dispatcher = _dispatcher;
            if (dispatcher != null)
            {
                _ = dispatcher.BeginInvoke((Action)UpdateUi, DispatcherPriority.Background);
            }
            else
            {
                UpdateUi();
            }
        }

        private void OnManagerPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (!string.Equals(e.PropertyName, nameof(MultiStratManagerService.IsConnected), StringComparison.Ordinal))
            {
                return;
            }

            void UpdateUi()
            {
                var connected = _managerService.IsConnected;

                if (connected)
                {
                    _hasEverConnected = true;
                    _userInitiatedDisconnect = false;
                    _pendingConnect = false;
                    if (!string.IsNullOrWhiteSpace(_managerService.CurrentAddress))
                    {
                        _pendingAddress = _managerService.CurrentAddress;
                    }

                    var address = _managerService.CurrentAddress;
                    var label = string.IsNullOrWhiteSpace(address)
                        ? "Connected"
                        : $"Connected to {address}";

                    UpdateStatus(label, Brushes.LightGreen);
                    SetButtonStates(true);
                    SetButtonsBusy(false);

                    EmitPluginLog(QuantowerBridgeService.BridgeLogLevel.Info, label);
                    PushStatusToBrowser();
                }
                else
                {
                    if (_managerService.IsBridgeRunning &&
                        (_lastStreamingState == BridgeGrpcClient.StreamingState.Connected ||
                         _lastStreamingState == BridgeGrpcClient.StreamingState.Connecting))
                    {
                        // Streaming layer reports connected/connecting; keep current status and wait for health probe.
                        return;
                    }

                    if (_pendingConnect)
                    {
                        var label = string.IsNullOrWhiteSpace(_pendingAddress)
                            ? "Awaiting trading stream"
                            : $"Awaiting trading stream on {_pendingAddress}";

                        UpdateStatus(label, Brushes.DarkOrange);
                        SetButtonStates(false);
                        // keep busy indicator untouched to avoid fighting concurrent updates
                        EmitPluginLog(QuantowerBridgeService.BridgeLogLevel.Info, label);
                        PushStatusToBrowser();
                        return;
                    }

                    var wasManual = _userInitiatedDisconnect || !_hasEverConnected;
                    var message = wasManual ? "Disconnected" : "Disconnected (connection lost)";
                    var brush = wasManual ? Brushes.Gray : Brushes.IndianRed;

                    UpdateStatus(message, brush);
                    SetButtonStates(false);
                    SetButtonsBusy(false);

                    if (!wasManual)
                    {
                        EmitPluginLog(QuantowerBridgeService.BridgeLogLevel.Warn, "Bridge connection lost", "Reconnect when ready");
                    }
                    else
                    {
                        EmitPluginLog(QuantowerBridgeService.BridgeLogLevel.Info, message);
                    }

                    PushStatusToBrowser();
                }
            }

            if (_dispatcher != null && !_dispatcher.CheckAccess())
            {
                _dispatcher.BeginInvoke((Action)UpdateUi, DispatcherPriority.Background);
            }
            else
            {
                UpdateUi();
            }
        }

        private void OnBridgeStreamingStateChanged(BridgeGrpcClient.StreamingState state, string? details)
        {
            void UpdateUi()
            {
                if (_userInitiatedDisconnect)
                {
                    return;
                }

                _lastStreamingState = state;

                switch (state)
                {
                    case BridgeGrpcClient.StreamingState.Connected:
                    {
                        _pendingConnect = false;
                        var address = _managerService.CurrentAddress;
                        var statusText = string.IsNullOrWhiteSpace(address)
                            ? "Connected"
                            : $"Connected to {address}";
                        UpdateStatus(statusText, Brushes.LightGreen);
                        SetButtonStates(true);
                        SetButtonsBusy(false);
                        break;
                    }

                    case BridgeGrpcClient.StreamingState.Connecting:
                    {
                        _pendingConnect = true;
                        if (string.IsNullOrWhiteSpace(_pendingAddress))
                        {
                            _pendingAddress = _managerService.CurrentAddress;
                        }

                        var pendingAddress = _pendingAddress;
                        var statusText = string.IsNullOrWhiteSpace(pendingAddress)
                            ? "Reconnecting…"
                            : $"Reconnecting to {pendingAddress}";

                        UpdateStatus(statusText, Brushes.DarkOrange);
                        SetButtonStates(false);
                        break;
                    }

                    case BridgeGrpcClient.StreamingState.Disconnected:
                    {
                        if (!_managerService.IsBridgeRunning)
                        {
                            UpdateStatus("Disconnected (retrying)", Brushes.IndianRed);
                            SetButtonStates(false);
                            if (string.IsNullOrWhiteSpace(_pendingAddress))
                            {
                                _pendingAddress = _managerService.CurrentAddress ?? _pendingAddress;
                            }
                            _pendingConnect = true;
                            if (!_userInitiatedDisconnect)
                            {
                                QueueAutoReconnect();
                            }
                            break;
                        }

                        _pendingConnect = true;
                        if (string.IsNullOrWhiteSpace(_pendingAddress))
                        {
                            _pendingAddress = _managerService.CurrentAddress;
                        }

                        var pendingAddress = _pendingAddress;
                        var statusText = string.IsNullOrWhiteSpace(pendingAddress)
                            ? "Reconnecting…"
                            : $"Reconnecting to {pendingAddress}";

                        UpdateStatus(statusText, Brushes.DarkOrange);
                        SetButtonStates(false);
                        break;
                    }
                }

                PushStatusToBrowser();
            }

            var dispatcher = _dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke((Action)UpdateUi, DispatcherPriority.Background);
            }
            else
            {
                UpdateUi();
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
            _statusTextValue = text ?? string.Empty;

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

            PushStatusToBrowser();
        }

        private void SetButtonsBusy(bool busy)
        {
            _isBusy = busy;

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

            PushStatusToBrowser();
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

            PushStatusToBrowser();
        }

        private void EnsureServiceHandlers()
        {
            if (_serviceHandlersAttached)
            {
                return;
            }

            _managerService.Log += OnBridgeLog;
            _managerService.AccountsChanged += OnAccountsChanged;
            _managerService.PropertyChanged += OnManagerPropertyChanged;
            _managerService.StreamingStateChanged += OnBridgeStreamingStateChanged;
            _serviceHandlersAttached = true;
        }

        private bool TryInitializeBrowserUi()
        {
            try
            {
                var window = this.Window;
                var wndType = window?.GetType().FullName ?? "<null>";
                SafeFileDebug($"Window type: {wndType}");

                var browser = window?.Browser;
                SafeFileDebug($"Browser host present: {browser != null}");
                if (browser == null)
                {
                    return false;
                }

                _dispatcher = WpfApplication.Current?.Dispatcher ?? _dispatcher ?? Dispatcher.CurrentDispatcher;

                _browser = browser;
                _browserBridgeAttached = false;

                SafeFileDebug("Setting browser HTML from layout.html");
                TryBrowserSetHtmlFromFile("layout.html");

                TryBrowserInvokeJs("console.log('Multi-Strat Bridge loaded')");

                try
                {
                    var trailing = _managerService.GetTrailingSettings();
                    var risk = _managerService.GetRiskSnapshot();
                    var trailingJson = SimpleJson.SerializeObject(new System.Collections.Generic.Dictionary<string, object?>
                    {
                        ["enable_elastic"] = trailing.EnableElastic,
                        ["elastic_trigger_units"] = trailing.ElasticTriggerUnits.ToString(),
                        ["profit_update_threshold"] = trailing.ProfitUpdateThreshold,
                        ["elastic_increment_units"] = trailing.ElasticIncrementUnits.ToString(),
                        ["elastic_increment_value"] = trailing.ElasticIncrementValue,
                        ["enable_trailing"] = trailing.EnableTrailing,
                        ["enable_dema_atr_trailing"] = trailing.UseDemaAtrTrailing,
                        // REMOVED: trailing_activation_units and trailing_activation_value
                        // Trailing now uses the SAME trigger as elastic
                        ["trailing_stop_units"] = trailing.TrailingStopUnits.ToString(),
                        ["trailing_stop_value"] = trailing.TrailingStopValue,
                        ["dema_atr_multiplier"] = trailing.DemaAtrMultiplier,
                        ["atr_period"] = trailing.AtrPeriod,
                        ["dema_period"] = trailing.DemaPeriod,
                    });
                    var riskJson = SimpleJson.SerializeObject(new System.Collections.Generic.Dictionary<string, object?>
                    {
                        ["disable_on_limit"] = risk.DisableOnLimit,
                    });
                    TryBrowserInvokeJs($"window.MSB && MSB.prime({trailingJson}, {riskJson})");
                }
                catch
                {
                    // Priming is best-effort only.
                }

                AttachBrowserCommandBridge();
                PushStatusToBrowser();

                SafeFileDebug("Using Browser host path. Returning to let template render.");
                return true;
            }
            catch (Exception ex)
            {
                SafeFileDebug($"Browser host initialization failed: {ex.Message}");
                SafeFileDebug($"Browser host stack trace: {ex.StackTrace}");
                AddLogEntry("ERROR", $"Browser host initialization failed: {ex.Message}");
                return false;
            }
        }

        private void AttachWpfFallbackUi()
        {
            SafeFileDebug("Browser host unavailable; falling back to WPF panel.");

            var app = WpfApplication.Current;
            if (app == null)
            {
                SafeFileDebug("No WPF Application.Current detected; creating a new instance.");
                app = new WpfApplication
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown
                };
            }

            var root = BuildLayout();
            _dispatcher = root.Dispatcher ?? app.Dispatcher ?? _dispatcher ?? Dispatcher.CurrentDispatcher;

            AttachContent(root);

            try
            {
                PopulateRiskUi();
                PopulateTrailingUi();
                RenderAccounts();
            }
            catch (Exception ex)
            {
                SafeFileDebug($"Failed to populate initial WPF UI: {ex.Message}");
                AddLogEntry("WARN", $"Failed to populate initial WPF UI: {ex.Message}");
            }

            UpdateStatus(_managerService.IsConnected ? "Connected" : "Disconnected",
                _managerService.IsConnected ? Brushes.LightGreen : Brushes.Gray);
            SetButtonStates(_managerService.IsConnected);
        }

        private void StartAutoConnectIfNeeded()
        {
            if (_managerService.IsConnected)
            {
                PushStatusToBrowser();
                return;
            }

            var address = _managerService.CurrentAddress ?? "127.0.0.1:50051";
            BeginConnect(address, "Auto-connect");
        }

        private void QueueAutoReconnect()
        {
            if (_userInitiatedDisconnect || _pendingConnect)
            {
                return;
            }

            if (_managerService.IsConnected)
            {
                return;
            }

            var address = _pendingAddress ?? _managerService.CurrentAddress ?? _addressTextBox?.Text ?? "127.0.0.1:50051";
            if (string.IsNullOrWhiteSpace(address))
            {
                return;
            }

            BeginConnect(address, "Auto-reconnect");
        }

        private void BeginConnect(string address, string origin)
        {
            _userInitiatedDisconnect = false;

            if (string.IsNullOrWhiteSpace(address))
            {
                AddLogEntry("WARN", $"{origin} request ignored: address was empty");
                return;
            }

            var trimmed = address.Trim();

            if (_managerService.IsConnected && string.Equals(_managerService.CurrentAddress, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                AddLogEntry("INFO", $"{origin}: already connected to {trimmed}");
                UpdateStatus($"Connected to {trimmed}", Brushes.LightGreen);
                PushStatusToBrowser();
                return;
            }

            SafeFileDebug($"{origin} attempting to reach {trimmed}");
            UpdateStatus("Connecting...", Brushes.DarkOrange);
            SetButtonsBusy(true);
            PushStatusToBrowser();

            _pendingConnect = true;
            _pendingAddress = trimmed;

            _ = Task.Run(async () =>
            {
                Exception? failure = null;
                var connected = false;

                try
                {
                    connected = await _managerService.ConnectAsync(trimmed).ConfigureAwait(false);
                    if (connected)
                    {
                        _managerService.RefreshAccounts();
                    }
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                void UpdateUi()
                {
                    SetButtonsBusy(false);
                    SetButtonStates(_managerService.IsConnected);

                    if (!connected || failure != null)
                    {
                        var message = failure?.GetBaseException().Message ?? "Bridge unavailable";
                        SafeFileDebug($"{origin} failed for {trimmed}: {message}");
                        AddLogEntry("ERROR", $"{origin} failed for {trimmed}: {message}");
                        UpdateStatus($"Connection failed: {message}", Brushes.IndianRed);
                        _pendingConnect = false;
                        _pendingAddress = null;
                        return;
                    }

                    var fullyOnline = _managerService.IsConnected;

                    if (fullyOnline)
                    {
                        SafeFileDebug($"{origin} connected to {trimmed}");
                        AddLogEntry("INFO", $"{origin} connected to {trimmed}");
                        UpdateStatus($"Connected to {trimmed}", Brushes.LightGreen);
                        _pendingConnect = false;
                        _pendingAddress = null;
                    }
                    else
                    {
                        SafeFileDebug($"{origin} established control channel to {trimmed}; awaiting trading stream");
                        AddLogEntry("INFO", $"{origin} established control channel to {trimmed}; awaiting trading stream");
                        UpdateStatus($"Awaiting trading stream on {trimmed}", Brushes.DarkOrange);
                        _pendingConnect = true;
                        _pendingAddress = trimmed;
                    }

                    try
                    {
                        PopulateRiskUi();
                        PopulateTrailingUi();
                        RenderAccounts();
                    }
                    catch (Exception ex)
                    {
                        AddLogEntry("WARN", $"Post-connect UI refresh failed: {ex.Message}");
                    }
                }

                var dispatcher = _dispatcher;
                if (dispatcher != null && !dispatcher.CheckAccess())
                {
                    dispatcher.BeginInvoke((Action)UpdateUi, DispatcherPriority.Background);
                }
                else if (_uiContext != null)
                {
                    _uiContext.Post(_ => UpdateUi(), null);
                }
                else
                {
                    UpdateUi();
                }
            });
        }

        private void AttachContent(UIElement element)
        {
            // Prefer the official API first
            try
            {
                var wnd = this.Window;
                var contentProp = wnd?.GetType().GetProperty("Content");
                if (wnd != null && contentProp != null)
                {
                    contentProp.SetValue(wnd, element);
                    SafeFileDebug("Attached content via Window.Content");
                    return;
                }
            }
            catch (Exception ex)
            {
                SafeFileDebug($"AttachContent via Window failed: {ex.Message}");
                AddLogEntry("ERROR", $"AttachContent via Window failed: {ex.Message}");
            }

            // Fallback: reflection against base type
            var windowProperty = GetType().BaseType?.GetProperty("Window") ?? GetType().GetProperty("Window");
            if (windowProperty == null)
            {
                SafeFileDebug("Unable to locate 'Window' property via reflection");
                AddLogEntry("ERROR", "Unable to locate plugin window property; UI may not render correctly.");
                return;
            }

            var windowInstance = windowProperty.GetValue(this);
            if (windowInstance == null)
            {
                SafeFileDebug("Window instance is null");
                AddLogEntry("ERROR", "Plugin window instance was null; UI cannot mount.");
                return;
            }

            var contentProperty = windowInstance.GetType().GetProperty("Content");
            if (contentProperty == null)
            {
                SafeFileDebug("Window does not expose a Content property");
                AddLogEntry("ERROR", "Plugin window does not expose a Content property; UI cannot mount.");
                return;
            }

            try
            {
                contentProperty.SetValue(windowInstance, element);
                SafeFileDebug("Attached content via reflection fallback");
            }
            catch (Exception ex)
            {
                SafeFileDebug($"Failed to attach content (fallback): {ex.Message}");
                AddLogEntry("ERROR", $"Failed to attach plugin content (fallback): {ex.Message}");
            }
        }

        private static void SafeFileDebug(string message, bool alsoLogToBridge = true)
        {
            try
            {
                var line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";

                // 1) Write next to plugin assembly (portable install / or Documents path)
                try
                {
                    var asmDir = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".";
                    var path1 = System.IO.Path.Combine(asmDir, "msb-plugin.log");
                    System.IO.File.AppendAllText(path1, line);
                }
                catch { }

                // 2) Also write to user TEMP so it’s easy to find regardless of install
                try
                {
                    var temp = System.IO.Path.GetTempPath();
                    var path2 = System.IO.Path.Combine(temp, "msb-plugin.log");
                    System.IO.File.AppendAllText(path2, line);
                }
                catch { }

                if (alsoLogToBridge)
                {
                    try
                    {
                        BridgeGrpcClient.LogDebug("qt_addon_ui", message);
                    }
                    catch
                    {
                        // Ignore when bridge logging is unavailable (e.g. before initialization)
                    }
                }
            }
            catch { /* never throw from diagnostics */ }
        }
        private void AttachBrowserCommandBridge()
        {
            try
            {
                var browser = _browser;
                if (browser == null || _browserBridgeAttached)
                {
                    return;
                }

                foreach (var ev in browser.GetType().GetEvents())
                {
                    var name = ev.Name ?? string.Empty;
                    bool looksLikeNav = name.IndexOf("Navig", StringComparison.OrdinalIgnoreCase) >= 0
                                     || name.IndexOf("NewWindow", StringComparison.OrdinalIgnoreCase) >= 0
                                     || name.IndexOf("Address", StringComparison.OrdinalIgnoreCase) >= 0
                                     || name.IndexOf("Url", StringComparison.OrdinalIgnoreCase) >= 0
                                     || name.IndexOf("Location", StringComparison.OrdinalIgnoreCase) >= 0
                                     || name.IndexOf("Start", StringComparison.OrdinalIgnoreCase) >= 0
                                     || name.IndexOf("Load", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!looksLikeNav)
                    {
                        continue;
                    }

                    var handlerType = ev.EventHandlerType;
                    if (handlerType == typeof(EventHandler))
                    {
                        var method = GetType().GetMethod(nameof(OnBrowserNavEvent), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (method != null)
                        {
                            var del = Delegate.CreateDelegate(handlerType, this, method);
                            ev.AddEventHandler(browser, del);
                            AddLogEntry("INFO", $"Attached browser handler: {ev.Name}");
                            continue;
                        }
                    }
                    else if (handlerType != null && handlerType.IsGenericType && handlerType.GetGenericTypeDefinition() == typeof(EventHandler<>))
                    {
                        var argType = handlerType.GetGenericArguments()[0];
                        var methodGeneric = GetType().GetMethod(nameof(OnBrowserNavGeneric), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (methodGeneric != null)
                        {
                            var method = methodGeneric.MakeGenericMethod(argType);
                            var del = Delegate.CreateDelegate(handlerType, this, method);
                            ev.AddEventHandler(browser, del);
                            AddLogEntry("INFO", $"Attached browser handler: {ev.Name}<{argType.Name}>");
                            continue;
                        }
                    }
                }

                _browserBridgeAttached = true;
            }
            catch (Exception ex)
            {
                AddLogEntry("WARN", $"Failed to attach Browser command bridge: {ex.Message}");
            }
        }

        private static object? TryGetValue(IDictionary<string, object?> payload, string key)
        {
            if (payload == null)
            {
                return null;
            }

            payload.TryGetValue(key, out var value);
            return value;
        }

        private void HandleMsbCommand(string url)
        {
            try
            {
                var uri = new Uri(url);
                var cmd = uri.Host?.Trim().ToLowerInvariant() ?? string.Empty;

                string? data = null;
                var query = uri.Query;
                if (!string.IsNullOrEmpty(query))
                {
                    var raw = query.StartsWith("?") ? query.Substring(1) : query;
                    foreach (var part in raw.Split('&', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var separator = part.IndexOf('=');
                        if (separator <= 0)
                        {
                            continue;
                        }

                        if (!string.Equals(part.Substring(0, separator), "d", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        data = Uri.UnescapeDataString(part.Substring(separator + 1));
                        break;
                    }
                }

                var payload = new Dictionary<string, object?>();
                if (!string.IsNullOrEmpty(data))
                {
                    try
                    {
                        var json = Encoding.UTF8.GetString(Convert.FromBase64String(data));
                        payload = SimpleJson.DeserializeObject<Dictionary<string, object?>>(json) ?? new Dictionary<string, object?>();
                    }
                    catch (Exception decodeEx)
                    {
                        AddLogEntry("WARN", $"Failed to decode command payload: {decodeEx.Message}");
                    }
                }

                switch (cmd)
                {
                    case "connect":
                    {
                        var addr = TryGetValue(payload, "address") as string;
                        if (string.IsNullOrWhiteSpace(addr))
                        {
                            AddLogEntry("WARN", "Manual connect ignored: address missing");
                            break;
                        }

                        BeginConnect(addr, "Manual connect");
                        break;
                    }
                    case "flatten_all":
                    {
                        var disable = TryGetValue(payload, "disableAfter") is bool b && b;
                        AddLogEntry("INFO", $"Flatten all command received (disableAfter={disable})");
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                AddLogEntry("INFO", "Executing flatten all...");
                                var ok = await _managerService.FlattenAllAsync(disable, "Quantower UI flatten").ConfigureAwait(false);
                                AddLogEntry(ok ? "INFO" : "WARN", ok ? "Flattened all accounts successfully" : "Flatten all reported issues");
                            }
                            catch (Exception ex)
                            {
                                AddLogEntry("ERROR", $"Flatten all failed: {ex.Message}");
                            }
                        });
                        break;
                    }
                    case "reset_daily":
                    {
                        var target = TryGetValue(payload, "id") as string ?? TryGetValue(payload, "account_id") as string;
                        _ = Task.Run(() =>
                        {
                            try
                            {
                                _managerService.ResetDailyRisk(string.IsNullOrWhiteSpace(target) ? null : target);
                                var message = string.IsNullOrWhiteSpace(target)
                                    ? "Reset daily risk for all accounts"
                                    : $"Reset daily risk for {target}";
                                AddLogEntry("INFO", message);

                                void RefreshUi()
                                {
                                    PopulateRiskUi();
                                    RenderAccounts();
                                    PushStatusToBrowser();
                                }

                                var dispatcher = _dispatcher;
                                if (dispatcher != null && !dispatcher.CheckAccess())
                                {
                                    dispatcher.BeginInvoke((Action)RefreshUi, DispatcherPriority.Background);
                                }
                                else
                                {
                                    RefreshUi();
                                }
                            }
                            catch (Exception ex)
                            {
                                AddLogEntry("ERROR", $"Reset daily failed: {ex.Message}");
                            }
                        });
                        break;
                    }
                    case "update_trailing":
                    {
                        Services.TrailingElasticService.ProfitUnitType ParseUnit(object? value)
                        {
                            if (value is string s && Enum.TryParse<Services.TrailingElasticService.ProfitUnitType>(s, true, out var parsed))
                            {
                                return parsed;
                            }

                            if (value is int i && Enum.IsDefined(typeof(Services.TrailingElasticService.ProfitUnitType), i))
                            {
                                return (Services.TrailingElasticService.ProfitUnitType)i;
                            }

                            return Services.TrailingElasticService.ProfitUnitType.Dollars;
                        }

                        double ReadDouble(object? value) => value is IConvertible convertible
                            ? Convert.ToDouble(convertible, CultureInfo.InvariantCulture)
                            : 0d;

                        int ReadInt(object? value) => value is IConvertible convertible
                            ? Convert.ToInt32(convertible, CultureInfo.InvariantCulture)
                            : 0;

                        try
                        {
                            var update = new MultiStratManagerService.TrailingSettingsUpdate(
                                EnableElastic: TryGetValue(payload, "enable_elastic") is bool be && be,
                                ElasticTriggerUnits: ParseUnit(TryGetValue(payload, "elastic_trigger_units")),
                                ProfitUpdateThreshold: ReadDouble(TryGetValue(payload, "profit_update_threshold")),
                                ElasticIncrementUnits: ParseUnit(TryGetValue(payload, "elastic_increment_units")),
                                ElasticIncrementValue: ReadDouble(TryGetValue(payload, "elastic_increment_value")),
                                EnableTrailing: TryGetValue(payload, "enable_trailing") is bool bt && bt,
                                UseDemaAtrTrailing: TryGetValue(payload, "enable_dema_atr_trailing") is bool bda && bda,
                                // REMOVED: TrailingActivationUnits and TrailingActivationValue
                                // Trailing now uses the SAME trigger as elastic
                                TrailingStopUnits: ParseUnit(TryGetValue(payload, "trailing_stop_units")),
                                TrailingStopValue: ReadDouble(TryGetValue(payload, "trailing_stop_value")),
                                DemaAtrMultiplier: ReadDouble(TryGetValue(payload, "dema_atr_multiplier")),
                                AtrPeriod: Math.Max(1, ReadInt(TryGetValue(payload, "atr_period"))),
                                DemaPeriod: Math.Max(1, ReadInt(TryGetValue(payload, "dema_period"))));

                            _managerService.UpdateTrailingSettings(update);
                            AddLogEntry("INFO", "Updated trailing settings");
                        }
                        catch (Exception ex)
                        {
                            AddLogEntry("ERROR", $"Failed to update trailing settings: {ex.Message}");
                        }
                        break;
                    }
                    case "select_account":
                    {
                        var account = TryGetValue(payload, "id") as string ?? TryGetValue(payload, "account_id") as string;
                        var normalized = string.IsNullOrWhiteSpace(account) ? null : account.Trim();
                        var success = _managerService.SelectAccount(normalized);
                        if (!success && normalized != null)
                        {
                            AddLogEntry("WARN", $"Select account ignored: {normalized}");
                        }
                        else
                        {
                            var message = normalized == null ? "Disabled all accounts" : $"Selected account {normalized}";
                            AddLogEntry("INFO", message);
                        }

                        RenderAccounts();
                        PushStatusToBrowser();
                        break;
                    }
                    case "window":
                    {
                        if (TryGetValue(payload, "action") is string action && !string.IsNullOrWhiteSpace(action))
                        {
                            SetWindowAction(action);
                        }

                        break;
                    }
                    case "ui_status_debug":
                    {
                        var connected = TryGetValue(payload, "connected") is bool cb && cb;
                        var reconnecting = TryGetValue(payload, "reconnecting") is bool rb && rb;
                        var streamState = TryGetValue(payload, "stream_state") as string ?? "<unknown>";
                        var busy = TryGetValue(payload, "busy") is bool bb && bb;
                        var label = TryGetValue(payload, "label") as string ?? string.Empty;
                        SafeFileDebug($"UI status callback: connected={connected}, reconnecting={reconnecting}, stream_state={streamState}, busy={busy}, label={label}");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                AddLogEntry("WARN", $"Failed to handle msb command: {ex.Message}");
            }
        }

        private void HandleBrowserNavArgs(object? args)
        {
            try
            {
                if (args == null)
                {
                    return;
                }

                var url = TryGetUrlFromArgs(args);
                if (string.IsNullOrWhiteSpace(url))
                {
                    return;
                }

                if (!url.StartsWith("msb://", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                TryCancelNavigation(args);
                HandleMsbCommand(url);
            }
            catch (Exception ex)
            {
                AddLogEntry("WARN", $"Browser navigation handling failed: {ex.Message}");
            }
        }

        private static string? TryGetUrlFromArgs(object e)
        {
            var type = e.GetType();

            string? Read(string name)
            {
                var property = type.GetProperty(name);
                if (property != null)
                {
                    var value = property.GetValue(e);
                    return value?.ToString();
                }

                return null;
            }

            return Read("Url") ??
                   Read("URI") ??
                   Read("Uri") ??
                   Read("Address") ??
                   Read("TargetUrl") ??
                   Read("Link") ??
                   Read("Location");
        }

        private static void TryCancelNavigation(object e)
        {
            var type = e.GetType();
            var cancelProperty = type.GetProperty("Cancel");
            if (cancelProperty != null && cancelProperty.PropertyType == typeof(bool))
            {
                cancelProperty.SetValue(e, true);
                return;
            }

            var handledProperty = type.GetProperty("Handled");
            if (handledProperty != null && handledProperty.PropertyType == typeof(bool))
            {
                handledProperty.SetValue(e, true);
                return;
            }

            var cancelMethod = type.GetMethod("Cancel");
            if (cancelMethod != null && cancelMethod.GetParameters().Length == 0)
            {
                cancelMethod.Invoke(e, null);
            }
        }

        private void PushStatusToBrowser()
        {
            try
            {
                var dispatcher = _dispatcher;
                if (dispatcher != null && !dispatcher.CheckAccess())
                {
                    dispatcher.BeginInvoke((Action)PushStatusToBrowserCore, DispatcherPriority.Background);
                    return;
                }

                PushStatusToBrowserCore();
            }
            catch
            {
                // ignore
            }
        }

        private void PushStatusToBrowserCore()
        {
            if (_browser == null)
            {
                return;
            }

            var accounts = new List<Dictionary<string, object?>>();
            foreach (var account in _managerService.Accounts)
            {
                accounts.Add(new Dictionary<string, object?>
                {
                    ["id"] = account.AccountId,
                    ["name"] = account.DisplayName,
                    ["enabled"] = account.IsEnabled
                });
            }

            var streamState = _lastStreamingState;
            var uiConnected = _managerService.IsConnected || streamState == BridgeGrpcClient.StreamingState.Connected;
            var uiReconnecting = _managerService.IsReconnecting ||
                                  streamState == BridgeGrpcClient.StreamingState.Connecting ||
                                  _pendingConnect;

            var payload = new Dictionary<string, object?>
            {
                ["connected"] = uiConnected,
                ["bridge_running"] = _managerService.IsBridgeRunning,
                ["stream_state"] = streamState.ToString(),
                ["address"] = _managerService.CurrentAddress ?? string.Empty,
                ["status_text"] = _statusTextValue,
                ["busy"] = _isBusy,
                ["reconnecting"] = uiReconnecting,
                ["accounts"] = accounts
            };

            var json = SimpleJson.SerializeObject(payload);
            SafeFileDebug($"PushStatus payload: {json}");
            TryBrowserInvokeJs($"window.MSB && MSB.setStatus({json})");
        }

        private void TryBrowserInvokeJs(string script)
        {
            try
            {
                if (_browser == null || string.IsNullOrWhiteSpace(script))
                {
                    return;
                }

                var method = _browser.GetType().GetMethod("UpdateHtml", new[] { typeof(string), typeof(HtmlAction), typeof(string) });
                method?.Invoke(_browser, new object[] { string.Empty, HtmlAction.InvokeJs, script });
            }
            catch
            {
                // ignore browser invocation failures
            }
        }

        private void TryBrowserSetHtml(string html)
        {
            try
            {
                if (_browser == null || string.IsNullOrEmpty(html)) return;
                var js = "(function(){document.open();document.write('" + JsEscape(html) + "');document.close();})();";
                TryBrowserInvokeJs(js);
            }
            catch
            {
                // ignore
            }
        }

        private void TryBrowserSetHtmlFromFile(string fileName)
        {
            try
            {
                var baseDir = System.IO.Path.GetDirectoryName(GetType().Assembly.Location);
                if (!string.IsNullOrEmpty(baseDir))
                {
                    var path = System.IO.Path.Combine(baseDir, fileName);
                    if (!System.IO.File.Exists(path))
                    {
                        var sdkGuess = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(baseDir) ?? baseDir, "HTML", fileName);
                        if (System.IO.File.Exists(sdkGuess))
                        {
                            path = sdkGuess;
                        }
                        else
                        {
                            path = null;
                        }
                    }

                    if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                    {
                        var html = System.IO.File.ReadAllText(path);
                        TryBrowserSetHtml(html);
                        return;
                    }
                }

                // Fallback to embedded resource lookup (Quantower samples embed layout.html)
                var asm = GetType().Assembly;
                var resourceName = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
                if (resourceName == null)
                {
                    return;
                }

                using var stream = asm.GetManifestResourceStream(resourceName);
                if (stream == null)
                {
                    return;
                }

                using var reader = new System.IO.StreamReader(stream);
                var embeddedHtml = reader.ReadToEnd();
                TryBrowserSetHtml(embeddedHtml);
            }
            catch
            {
                // ignore
            }
        }


        private void OnBrowserNavEvent(object? sender, EventArgs e) => HandleBrowserNavArgs(e);

        private void OnBrowserNavGeneric<T>(object? sender, T e) => HandleBrowserNavArgs(e);
    }
}
