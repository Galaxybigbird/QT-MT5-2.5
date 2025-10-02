using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using Quantower.Bridge.Client;
using Quantower.MultiStrat.Indicators;
using Quantower.MultiStrat.Persistence;
using Quantower.MultiStrat.Services;
using Quantower.MultiStrat.Utilities;
using TradingPlatform.BusinessLayer;

namespace Quantower.MultiStrat
{
    public sealed class MultiStratManagerService : INotifyPropertyChanged, IDisposable
    {
        private readonly QuantowerBridgeService _bridgeService;
        private readonly ObservableCollection<AccountSubscription> _accounts = new();
        private readonly object _accountsLock = new();
        private readonly ReadOnlyObservableCollection<AccountSubscription> _accountsView;
        private readonly SettingsRepository _settingsRepository = new();
        private readonly RiskConfiguration _riskSettings = new();
        private readonly HashSet<string> _savedAccountIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly Services.TrailingElasticService _trailingService;
        private readonly Services.SltpRemovalService _sltpService = new();
        private readonly Dictionary<string, TrackingState> _trackingStates = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _trackingLock = new();
        private readonly TimeSpan _trackingInterval = TimeSpan.FromSeconds(2);
        private readonly object _riskLock = new();
        private readonly object _settingsSaveLock = new();
        private readonly ConcurrentDictionary<string, bool> _processingPositions = new();
        private readonly ConcurrentDictionary<string, string> _baseIdToPositionId = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, Order> _stopLossOrders = new(StringComparer.OrdinalIgnoreCase);
        // Track initial position quantities for proper hedge closure (n trades = n hedges)
        private readonly ConcurrentDictionary<string, int> _baseIdToInitialQuantity = new(StringComparer.OrdinalIgnoreCase);
        private int _disposed; // 0 = active, 1 = disposed
        private Timer? _riskTimer;
        private bool _coreEventsAttached;
        private volatile bool _isReconnecting;
        public TimeSpan RiskTimerInterval { get; set; } = TimeSpan.FromSeconds(5);

        public MultiStratManagerService()
        {
            _trailingService = new Services.TrailingElasticService
            {
                LogWarning = message => EmitLog(QuantowerBridgeService.BridgeLogLevel.Warn, message),
                LogDebug = message => EmitLog(QuantowerBridgeService.BridgeLogLevel.Debug, message)
            };
            _bridgeService = new QuantowerBridgeService();
            _bridgeService.ConnectionStateChanged += OnBridgeConnectionStateChanged;
            _bridgeService.StreamingStateChanged += OnBridgeStreamingStateChanged;
            _bridgeService.Log += entry => Log?.Invoke(entry);
            _bridgeService.TradeReceived += HandleTrade;
            _bridgeService.PositionAdded += HandlePositionAdded;
            _bridgeService.PositionRemoved += HandlePositionRemoved;
            _bridgeService.StreamEnvelopeReceived += OnBridgeStreamEnvelopeReceived;
            // Wire up callback for getting tracked quantities (n trades = n hedges)
            _bridgeService.GetTrackedQuantity = GetTrackedInitialQuantity;
            _accountsView = new ReadOnlyObservableCollection<AccountSubscription>(_accounts);
            LoadSettings();
            StartRiskTimer();
            SubscribeToCoreEvents();
        }

        public event Action<QuantowerBridgeService.BridgeLogEntry>? Log;
        public event EventHandler? AccountsChanged;
        public event PropertyChangedEventHandler? PropertyChanged;
        public event Action<BridgeGrpcClient.StreamingState, string?>? StreamingStateChanged;

        public ReadOnlyObservableCollection<AccountSubscription> Accounts => _accountsView;

        public bool IsConnected => _bridgeService.IsOnline;
        public bool IsReconnecting => Volatile.Read(ref _isReconnecting);
        public bool IsBridgeRunning => _bridgeService.IsRunning;

        public string? CurrentAddress => _bridgeService.CurrentAddress;

        public async Task<bool> ConnectAsync(string address)
        {
            ThrowIfDisposed();

            var ok = await _bridgeService.StartAsync(address).ConfigureAwait(false);
            if (!ok)
            {
                return false;
            }

            OnPropertyChanged(nameof(IsConnected));
            return true;
        }

        public async Task DisconnectAsync(string reason = "unspecified")
        {
            ThrowIfDisposed();

            var stack = Environment.StackTrace;
            EmitLog(QuantowerBridgeService.BridgeLogLevel.Info, $"Disconnect requested ({reason})", details: stack);

            await _bridgeService.StopAsync().ConfigureAwait(false);
            StopAllTracking();
            OnPropertyChanged(nameof(IsConnected));
            SaveSettings();
        }

        public RiskSnapshot GetRiskSnapshot()
        {
            ThrowIfDisposed();

            lock (_riskLock)
            {
                var accounts = new List<AccountRiskSnapshot>(_riskSettings.Accounts.Count);
                foreach (var kvp in _riskSettings.Accounts)
                {
                    accounts.Add(new AccountRiskSnapshot(
                        kvp.Key,
                        kvp.Value.BalanceBaseline,
                        kvp.Value.LimitTriggered,
                        kvp.Value.LastKnownPnL,
                        kvp.Value.LastTriggerUtc));
                }

                return new RiskSnapshot(
                    _riskSettings.DailyTakeProfit,
                    _riskSettings.DailyLossLimit,
                    _riskSettings.AutoFlatten,
                    _riskSettings.DisableOnLimit,
                    _riskSettings.LastResetDateUtc,
                    accounts.AsReadOnly());
            }
        }

        public void UpdateRiskSettings(RiskSettingsUpdate update)
        {
            ThrowIfDisposed();

            lock (_riskLock)
            {
                _riskSettings.DailyTakeProfit = Math.Max(0, update.DailyTakeProfit);
                _riskSettings.DailyLossLimit = Math.Max(0, update.DailyLossLimit);
                _riskSettings.AutoFlatten = update.AutoFlatten;
                _riskSettings.DisableOnLimit = update.DisableOnLimit;
            }

            SaveSettings();
        }

        public void ResetDailyRisk(string? accountId)
        {
            ThrowIfDisposed();

            var accountLookup = BuildAccountLookup(SnapshotAccounts());

            lock (_riskLock)
            {
                if (string.IsNullOrWhiteSpace(accountId))
                {
                    foreach (var kvp in _riskSettings.Accounts)
                    {
                        if (accountLookup.TryGetValue(kvp.Key, out var account) && account != null)
                        {
                            kvp.Value.BalanceBaseline = account.Balance;
                        }

                        kvp.Value.LimitTriggered = false;
                        kvp.Value.LastKnownPnL = 0;
                        kvp.Value.LastTriggerUtc = DateTime.MinValue;
                    }

                    _riskSettings.LastResetDateUtc = DateTime.UtcNow.Date;
                }
                else
                {
                    accountLookup.TryGetValue(accountId, out var account);
                    var state = GetOrCreateRiskState(accountId, account);
                    state.BalanceBaseline = account?.Balance ?? state.BalanceBaseline;
                    state.LimitTriggered = false;
                    state.LastKnownPnL = 0;
                    state.LastTriggerUtc = DateTime.MinValue;
                }
            }

            SaveSettings();
        }

        public Task<bool> FlattenAccountAsync(string accountId, bool disableAfter, string reason = "manual")
        {
            ThrowIfDisposed();

            AccountSubscription? subscription;
            lock (_accountsLock)
            {
                subscription = _accounts.FirstOrDefault(s => string.Equals(s.AccountId, accountId, StringComparison.OrdinalIgnoreCase));
            }
            if (subscription == null)
            {
                EmitLog(QuantowerBridgeService.BridgeLogLevel.Warn, $"Flatten request ignored – unknown account {accountId}");
                return Task.FromResult(false);
            }

            return FlattenAccountInternalAsync(subscription, reason, disableAfter);
        }

        public Task<bool> FlattenAllAsync(bool disableAfter, string reason = "manual")
        {
            ThrowIfDisposed();

            var tasks = new List<Task<bool>>();
            List<AccountSubscription> snapshot;
            lock (_accountsLock)
            {
                snapshot = _accounts.ToList();
            }

            foreach (var subscription in snapshot)
            {
                if (subscription.Account != null)
                {
                    tasks.Add(FlattenAccountInternalAsync(subscription, reason, disableAfter));
                }
            }

            return Task.WhenAll(tasks).ContinueWith(t => t.Result.All(result => result), TaskScheduler.Default);
        }

        public TrailingSettingsSnapshot GetTrailingSettings()
        {
            ThrowIfDisposed();

            return new TrailingSettingsSnapshot(
                _trailingService.EnableElasticHedging,
                _trailingService.ElasticTriggerUnits,
                _trailingService.ProfitUpdateThreshold,
                _trailingService.ElasticIncrementUnits,
                _trailingService.ElasticIncrementValue,
                _trailingService.EnableTrailing,
                _trailingService.UseDemaAtrTrailing,
                // REMOVED: TrailingActivationUnits and TrailingActivationValue
                _trailingService.TrailingStopUnits,
                _trailingService.TrailingStopValue,
                _trailingService.DemaAtrMultiplier,
                _trailingService.AtrPeriod,
                _trailingService.DemaPeriod);
        }

        public void UpdateTrailingSettings(TrailingSettingsUpdate update)
        {
            ThrowIfDisposed();

            _trailingService.EnableElasticHedging = update.EnableElastic;
            _trailingService.ElasticTriggerUnits = update.ElasticTriggerUnits;
            _trailingService.ProfitUpdateThreshold = update.ProfitUpdateThreshold;
            _trailingService.ElasticIncrementUnits = update.ElasticIncrementUnits;
            _trailingService.ElasticIncrementValue = update.ElasticIncrementValue;
            _trailingService.EnableTrailing = update.EnableTrailing;
            _trailingService.UseDemaAtrTrailing = update.EnableTrailing && update.UseDemaAtrTrailing;
            // REMOVED: TrailingActivationUnits and TrailingActivationValue
            // Trailing now uses the SAME trigger as elastic
            _trailingService.TrailingStopUnits = update.TrailingStopUnits;
            _trailingService.TrailingStopValue = update.TrailingStopValue;
            _trailingService.DemaAtrMultiplier = update.DemaAtrMultiplier;
            _trailingService.AtrPeriod = update.AtrPeriod;
            _trailingService.DemaPeriod = update.DemaPeriod;
            SaveSettings();
        }

        public void RefreshAccounts()
        {
            ThrowIfDisposed();

            try
            {
                var core = Core.Instance;
                if (core?.Accounts == null)
                {
                    return;
                }

                var accounts = core.Accounts;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var refreshPositions = new List<string>();
                var removedAccounts = new List<AccountSubscription>();
                var stopTrackingIds = new List<string>();
                var changed = false;
                var subscriptionsToAttach = new List<AccountSubscription>();

                lock (_accountsLock)
                {
                    foreach (var account in accounts)
                    {
                        if (account == null)
                        {
                            continue;
                        }

                        var identifier = account.Id ?? account.Name ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(identifier))
                        {
                            continue;
                        }

                        seen.Add(identifier);

                        AccountSubscription? existing = null;
                        foreach (var subscription in _accounts)
                        {
                            if (subscription.Matches(account))
                            {
                                existing = subscription;
                                break;
                            }
                        }

                        if (existing == null)
                        {
                            var enable = _savedAccountIds.Count == 0 || _savedAccountIds.Contains(identifier);
                            var subscription = new AccountSubscription(account, enable);
                            _accounts.Add(subscription);
                            changed = true;
                            subscriptionsToAttach.Add(subscription);

                            if (enable && !string.IsNullOrWhiteSpace(subscription.AccountId))
                            {
                                refreshPositions.Add(subscription.AccountId);
                            }
                        }
                        else
                        {
                            var beforeName = existing.DisplayName;
                            existing.Update(account);
                            subscriptionsToAttach.Add(existing);
                            if (!string.Equals(beforeName, existing.DisplayName, StringComparison.Ordinal))
                            {
                                changed = true;
                            }
                        }
                    }

                    for (var i = _accounts.Count - 1; i >= 0; i--)
                    {
                        var subscription = _accounts[i];
                        var currentAccount = subscription.Account;
                        var candidate = currentAccount?.Id ?? currentAccount?.Name ?? subscription.AccountId;

                        var shouldRemove = currentAccount == null || !seen.Contains(candidate ?? string.Empty);
                        if (!shouldRemove)
                        {
                            continue;
                        }

                        _accounts.RemoveAt(i);
                        removedAccounts.Add(subscription);
                        if (!string.IsNullOrWhiteSpace(subscription.AccountId))
                        {
                            stopTrackingIds.Add(subscription.AccountId);
                        }
                        changed = true;
                    }
                }

                foreach (var subscription in subscriptionsToAttach)
                {
                    AttachSubscription(subscription);
                }

                foreach (var subscription in removedAccounts)
                {
                    DetachSubscription(subscription);
                }

                foreach (var accountId in stopTrackingIds)
                {
                    StopTrackingByAccount(accountId);
                }

                foreach (var accountId in refreshPositions)
                {
                    RefreshAccountPositions(accountId);
                }

                if (changed)
                {
                    RaiseAccountsChanged();
                }
            }
            catch (Exception ex)
            {
                EmitLog(QuantowerBridgeService.BridgeLogLevel.Warn, $"Failed to refresh Quantower accounts: {ex.Message}");
            }
        }

        public bool SelectAccount(string? accountId)
        {
            ThrowIfDisposed();

            List<AccountSubscription> snapshot;
            lock (_accountsLock)
            {
                snapshot = _accounts.ToList();
            }

            var normalized = string.IsNullOrWhiteSpace(accountId) ? null : accountId.Trim();
            var found = false;

            foreach (var subscription in snapshot)
            {
                var shouldEnable = normalized != null && string.Equals(subscription.AccountId, normalized, StringComparison.OrdinalIgnoreCase);
                if (shouldEnable)
                {
                    found = true;
                }

                if (subscription.IsEnabled != shouldEnable)
                {
                    subscription.IsEnabled = shouldEnable;
                }
            }

            if (normalized == null)
            {
                // Disable all accounts when no selection is provided.
                foreach (var subscription in snapshot)
                {
                    if (subscription.IsEnabled)
                    {
                        subscription.IsEnabled = false;
                    }
                }

                return true;
            }

            if (!found)
            {
                EmitLog(QuantowerBridgeService.BridgeLogLevel.Warn, $"SelectAccount ignored – unknown account '{normalized}'");
            }

            return found;
        }

        private void SubscribeToCoreEvents()
        {
            if (_coreEventsAttached)
            {
                return;
            }

            try
            {
                var core = Core.Instance;
                if (core == null)
                {
                    return;
                }

                core.AccountAdded += OnCoreAccountAdded;
                var connections = core.Connections;
                if (connections != null)
                {
                    connections.ConnectionStateChanged += OnCoreConnectionStateChanged;
                }
                _coreEventsAttached = true;
                RefreshAccounts();
            }
            catch (Exception ex)
            {
                EmitLog(QuantowerBridgeService.BridgeLogLevel.Warn, $"Failed to subscribe to Quantower Core events: {ex.Message}");
            }
        }

        private void UnsubscribeFromCoreEvents()
        {
            if (!_coreEventsAttached)
            {
                return;
            }

            try
            {
                var core = Core.Instance;
                if (core != null)
                {
                    core.AccountAdded -= OnCoreAccountAdded;
                    var connections = core.Connections;
                    if (connections != null)
                    {
                        connections.ConnectionStateChanged -= OnCoreConnectionStateChanged;
                    }
                }
            }
            catch
            {
                // ignore during shutdown
            }
            finally
            {
                _coreEventsAttached = false;
            }
        }

        private void OnCoreAccountAdded(Account account)
        {
            RefreshAccounts();
        }

        private void OnCoreConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
        {
            RefreshAccounts();
        }

        private void OnBridgeConnectionStateChanged(bool isOnline)
        {
            OnPropertyChanged(nameof(IsConnected));

            if (isOnline)
            {
                EmitLog(QuantowerBridgeService.BridgeLogLevel.Info, "Bridge connection established");
            }
            else
            {
                EmitLog(QuantowerBridgeService.BridgeLogLevel.Warn, "Bridge connection lost");
            }
        }

        private void OnBridgeStreamingStateChanged(BridgeGrpcClient.StreamingState state, string? details)
        {
            var reconnecting = state switch
            {
                BridgeGrpcClient.StreamingState.Connected => false,
                BridgeGrpcClient.StreamingState.Disconnected => _bridgeService.IsRunning,
                BridgeGrpcClient.StreamingState.Connecting => _bridgeService.IsRunning,
                _ => Volatile.Read(ref _isReconnecting)
            };

            var previous = Volatile.Read(ref _isReconnecting);
            if (previous != reconnecting)
            {
                Volatile.Write(ref _isReconnecting, reconnecting);
                OnPropertyChanged(nameof(IsReconnecting));
            }

            try
            {
                StreamingStateChanged?.Invoke(state, details);
            }
            catch
            {
                // Suppress listener failures so stream recovery isn't impacted.
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            SaveSettings(immediate: true);
            UnsubscribeFromCoreEvents();
            try
            {
                StopAllTracking();
                List<AccountSubscription> snapshot;
                lock (_accountsLock)
                {
                    snapshot = _accounts.ToList();
                }

                foreach (var subscription in snapshot)
                {
                    DetachSubscription(subscription);
                }

                _bridgeService.ConnectionStateChanged -= OnBridgeConnectionStateChanged;
                _bridgeService.StreamingStateChanged -= OnBridgeStreamingStateChanged;
                _bridgeService.StreamEnvelopeReceived -= OnBridgeStreamEnvelopeReceived;
                _bridgeService.TradeReceived -= HandleTrade;
                _bridgeService.PositionAdded -= HandlePositionAdded;
                _bridgeService.PositionRemoved -= HandlePositionRemoved;
                _bridgeService.Dispose();
                _sltpService.Dispose();
                _riskTimer?.Dispose();
                _riskTimer = null;
            }
            catch
            {
                // ignore during dispose
            }
        }

        private void EmitLog(QuantowerBridgeService.BridgeLogLevel level, string message, string? details = null)
        {
            Log?.Invoke(new QuantowerBridgeService.BridgeLogEntry(DateTime.UtcNow, level, message, null, null, details));
        }

        private void RaiseAccountsChanged()
        {
            AccountsChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private void LoadSettings()
        {
            try
            {
                var data = _settingsRepository.Load();
                if (data.TryGetValue("enabled_accounts", out var stored) && stored is IEnumerable<object?> ids)
                {
                    lock (_accountsLock)
                    {
                        _savedAccountIds.Clear();
                        foreach (var id in ids)
                        {
                            if (id is string key && !string.IsNullOrWhiteSpace(key))
                            {
                                _savedAccountIds.Add(key);
                            }
                        }
                    }
                }

                if (data.TryGetValue("risk", out var riskValue))
                {
                    ReadRiskConfiguration(riskValue);
                }

                if (data.TryGetValue("trailing", out var trailingValue))
                {
                    ReadTrailingConfiguration(trailingValue);
                }
            }
            catch (Exception ex)
            {
                EmitLog(QuantowerBridgeService.BridgeLogLevel.Warn, $"Failed to load manager settings: {ex.Message}");
            }
        }

        private void ReadRiskConfiguration(object? raw)
        {
            try
            {
                if (raw is JsonElement json)
                {
                    if (json.TryGetProperty("daily_take_profit", out var dtp) && dtp.TryGetDouble(out var takeProfit))
                    {
                        _riskSettings.DailyTakeProfit = Math.Max(0, takeProfit);
                    }

                    if (json.TryGetProperty("daily_loss_limit", out var dll) && dll.TryGetDouble(out var lossLimit))
                    {
                        _riskSettings.DailyLossLimit = Math.Max(0, lossLimit);
                    }

                    if (json.TryGetProperty("auto_flatten", out var autoFlatten) && autoFlatten.ValueKind != JsonValueKind.Undefined)
                    {
                        _riskSettings.AutoFlatten = autoFlatten.GetBoolean();
                    }

                    if (json.TryGetProperty("disable_on_limit", out var disable) && disable.ValueKind != JsonValueKind.Undefined)
                    {
                        _riskSettings.DisableOnLimit = disable.GetBoolean();
                    }

                    if (json.TryGetProperty("last_reset_date", out var last) && last.ValueKind == JsonValueKind.String && DateTime.TryParse(last.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var reset))
                    {
                        _riskSettings.LastResetDateUtc = reset.ToUniversalTime();
                    }

                    if (json.TryGetProperty("baselines", out var baselines) && baselines.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var property in baselines.EnumerateObject())
                        {
                            if (property.Value.TryGetDouble(out var baseline))
                            {
                                var state = _riskSettings.Accounts.GetOrAdd(property.Name, _ => new AccountRiskState());
                                state.BalanceBaseline = baseline;
                            }
                        }
                    }

                    if (json.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var property in limits.EnumerateObject())
                        {
                            var state = _riskSettings.Accounts.GetOrAdd(property.Name, _ => new AccountRiskState());
                            state.LimitTriggered = property.Value.ValueKind == JsonValueKind.True;
                        }
                    }

                    if (json.TryGetProperty("last_known_pnl", out var pnl) && pnl.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var property in pnl.EnumerateObject())
                        {
                            if (property.Value.TryGetDouble(out var lastPnl))
                            {
                                var state = _riskSettings.Accounts.GetOrAdd(property.Name, _ => new AccountRiskState());
                                state.LastKnownPnL = lastPnl;
                            }
                        }
                    }

                    if (json.TryGetProperty("last_trigger", out var trigger) && trigger.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var property in trigger.EnumerateObject())
                        {
                            if (property.Value.ValueKind == JsonValueKind.String && DateTime.TryParse(property.Value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var triggerTime))
                            {
                                var state = _riskSettings.Accounts.GetOrAdd(property.Name, _ => new AccountRiskState());
                                state.LastTriggerUtc = triggerTime.ToUniversalTime();
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                EmitLog(QuantowerBridgeService.BridgeLogLevel.Warn, $"Failed to parse risk settings: {ex.Message}");
            }
        }

        private void ReadTrailingConfiguration(object? raw)
        {
            try
            {
                if (raw is JsonElement json)
                {
                    if (json.TryGetProperty("enable_elastic", out var enableElastic) && enableElastic.ValueKind != JsonValueKind.Undefined)
                    {
                        _trailingService.EnableElasticHedging = enableElastic.GetBoolean();
                    }

                    if (json.TryGetProperty("elastic_trigger_units", out var triggerUnits) && triggerUnits.ValueKind == JsonValueKind.String && Enum.TryParse(triggerUnits.GetString(), true, out Services.TrailingElasticService.ProfitUnitType triggerType))
                    {
                        _trailingService.ElasticTriggerUnits = triggerType;
                    }

                    if (json.TryGetProperty("profit_update_threshold", out var threshold) && threshold.TryGetDouble(out var profThreshold))
                    {
                        _trailingService.ProfitUpdateThreshold = profThreshold;
                    }

                    if (json.TryGetProperty("elastic_increment_units", out var incUnits) && incUnits.ValueKind == JsonValueKind.String && Enum.TryParse(incUnits.GetString(), true, out Services.TrailingElasticService.ProfitUnitType incType))
                    {
                        _trailingService.ElasticIncrementUnits = incType;
                    }

                    if (json.TryGetProperty("elastic_increment_value", out var incValue) && incValue.TryGetDouble(out var incVal))
                    {
                        _trailingService.ElasticIncrementValue = incVal;
                    }

                    if (json.TryGetProperty("enable_trailing", out var enableTrailing) && enableTrailing.ValueKind != JsonValueKind.Undefined)
                    {
                        _trailingService.EnableTrailing = enableTrailing.GetBoolean();
                    }

                    if (json.TryGetProperty("enable_dema_atr_trailing", out var enableDema) && enableDema.ValueKind != JsonValueKind.Undefined)
                    {
                        _trailingService.UseDemaAtrTrailing = enableDema.GetBoolean();
                    }

                    // REMOVED: trailing_activation_units and trailing_activation_value
                    // Trailing now uses the SAME trigger as elastic

                    if (json.TryGetProperty("trailing_stop_units", out var stopUnits) && stopUnits.ValueKind == JsonValueKind.String && Enum.TryParse(stopUnits.GetString(), true, out Services.TrailingElasticService.ProfitUnitType stopType))
                    {
                        _trailingService.TrailingStopUnits = stopType;
                    }

                    if (json.TryGetProperty("trailing_stop_value", out var stopValue) && stopValue.TryGetDouble(out var stVal))
                    {
                        _trailingService.TrailingStopValue = stVal;
                    }

                    if (json.TryGetProperty("dema_atr_multiplier", out var multiplier) && multiplier.TryGetDouble(out var multVal))
                    {
                        _trailingService.DemaAtrMultiplier = multVal;
                    }

                    if (json.TryGetProperty("atr_period", out var atr) && atr.TryGetInt32(out var atrPeriod))
                    {
                        _trailingService.AtrPeriod = Math.Max(1, atrPeriod);
                    }

                    if (json.TryGetProperty("dema_period", out var dema) && dema.TryGetInt32(out var demaPeriod))
                    {
                        _trailingService.DemaPeriod = Math.Max(1, demaPeriod);
                    }
                }
            }
            catch (Exception ex)
            {
                EmitLog(QuantowerBridgeService.BridgeLogLevel.Warn, $"Failed to parse trailing settings: {ex.Message}");
            }
        }

        private void SaveSettings(bool immediate = false)
        {
            Dictionary<string, object?> snapshot;
            try
            {
                snapshot = BuildSettingsPayload();
            }
            catch (Exception ex)
            {
                EmitLog(QuantowerBridgeService.BridgeLogLevel.Warn, $"Failed to prepare manager settings for save: {ex.Message}");
                return;
            }

            if (immediate)
            {
                PersistSettings(snapshot);
            }
            else
            {
                _ = Task.Run(() => PersistSettings(snapshot));
            }
        }

        private Dictionary<string, object?> BuildSettingsPayload()
        {
            List<AccountSubscription> accountsSnapshot;
            lock (_accountsLock)
            {
                accountsSnapshot = _accounts.ToList();
            }

            var enabled = new List<string>();
            foreach (var account in accountsSnapshot)
            {
                if (account.IsEnabled && !string.IsNullOrWhiteSpace(account.AccountId))
                {
                    enabled.Add(account.AccountId);
                }
            }

            lock (_accountsLock)
            {
                _savedAccountIds.Clear();
                foreach (var id in enabled)
                {
                    _savedAccountIds.Add(id);
                }
            }

            var payload = new Dictionary<string, object?>
            {
                ["enabled_accounts"] = enabled.ToArray()
            };

            Dictionary<string, object?> riskPayload;
            lock (_riskLock)
            {
                var baselines = new Dictionary<string, object?>();
                var triggers = new Dictionary<string, object?>();
                var lastPnl = new Dictionary<string, object?>();

                foreach (var kvp in _riskSettings.Accounts)
                {
                    baselines[kvp.Key] = kvp.Value.BalanceBaseline;
                    if (kvp.Value.LimitTriggered)
                    {
                        triggers[kvp.Key] = true;
                    }

                    if (Math.Abs(kvp.Value.LastKnownPnL) > double.Epsilon)
                    {
                        lastPnl[kvp.Key] = kvp.Value.LastKnownPnL;
                    }
                }

                riskPayload = new Dictionary<string, object?>
                {
                    ["daily_take_profit"] = _riskSettings.DailyTakeProfit,
                    ["daily_loss_limit"] = _riskSettings.DailyLossLimit,
                    ["auto_flatten"] = _riskSettings.AutoFlatten,
                    ["disable_on_limit"] = _riskSettings.DisableOnLimit,
                    ["last_reset_date"] = _riskSettings.LastResetDateUtc.ToString("o", CultureInfo.InvariantCulture),
                    ["baselines"] = baselines,
                    ["limits"] = triggers,
                    ["last_known_pnl"] = lastPnl,
                    ["last_trigger"] = _riskSettings.Accounts.ToDictionary(k => k.Key, v => (object)v.Value.LastTriggerUtc.ToString("o", CultureInfo.InvariantCulture))
                };
            }

            payload["risk"] = riskPayload;

            payload["trailing"] = new Dictionary<string, object?>
            {
                ["enable_elastic"] = _trailingService.EnableElasticHedging,
                ["elastic_trigger_units"] = _trailingService.ElasticTriggerUnits.ToString(),
                ["profit_update_threshold"] = _trailingService.ProfitUpdateThreshold,
                ["elastic_increment_units"] = _trailingService.ElasticIncrementUnits.ToString(),
                ["elastic_increment_value"] = _trailingService.ElasticIncrementValue,
                ["enable_trailing"] = _trailingService.EnableTrailing,
                ["enable_dema_atr_trailing"] = _trailingService.UseDemaAtrTrailing,
                // REMOVED: trailing_activation_units and trailing_activation_value
                // Trailing now uses the SAME trigger as elastic
                ["trailing_stop_units"] = _trailingService.TrailingStopUnits.ToString(),
                ["trailing_stop_value"] = _trailingService.TrailingStopValue,
                ["dema_atr_multiplier"] = _trailingService.DemaAtrMultiplier,
                ["atr_period"] = _trailingService.AtrPeriod,
                ["dema_period"] = _trailingService.DemaPeriod
            };

            return payload;
        }

        private void PersistSettings(Dictionary<string, object?> payload)
        {
            try
            {
                lock (_settingsSaveLock)
                {
                    _settingsRepository.Save(payload);
                }
            }
            catch (Exception ex)
            {
                EmitLog(QuantowerBridgeService.BridgeLogLevel.Warn, $"Failed to save manager settings: {ex.Message}");
            }
        }

        private sealed class TrackingState
        {
            public string BaseId { get; init; } = string.Empty;
            public string? PositionId { get; set; }
            public string? AccountId { get; set; }
            public string? SymbolName { get; set; }
            public Timer Timer { get; set; } = null!;
        }

        private sealed class RiskConfiguration
        {
            public double DailyTakeProfit { get; set; }
            public double DailyLossLimit { get; set; }
            public bool AutoFlatten { get; set; }
            public bool DisableOnLimit { get; set; }
            public DateTime LastResetDateUtc { get; set; } = DateTime.UtcNow.Date;
            public ConcurrentDictionary<string, AccountRiskState> Accounts { get; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class AccountRiskState
        {
            public double BalanceBaseline { get; set; }
            public bool LimitTriggered { get; set; }
            public DateTime LastTriggerUtc { get; set; }
            public double LastKnownPnL { get; set; }
        }

        public readonly record struct RiskSnapshot(
            double DailyTakeProfit,
            double DailyLossLimit,
            bool AutoFlatten,
            bool DisableOnLimit,
            DateTime LastResetDateUtc,
            IReadOnlyList<AccountRiskSnapshot> Accounts);

        public readonly record struct AccountRiskSnapshot(
            string AccountId,
            double BalanceBaseline,
            bool LimitTriggered,
            double LastKnownPnL,
            DateTime LastTriggerUtc);

        public readonly record struct RiskSettingsUpdate(
            double DailyTakeProfit,
            double DailyLossLimit,
            bool AutoFlatten,
            bool DisableOnLimit);

        public readonly record struct TrailingSettingsSnapshot(
            bool EnableElastic,
            Services.TrailingElasticService.ProfitUnitType ElasticTriggerUnits,
            double ProfitUpdateThreshold,
            Services.TrailingElasticService.ProfitUnitType ElasticIncrementUnits,
            double ElasticIncrementValue,
            bool EnableTrailing,
            bool UseDemaAtrTrailing,
            // REMOVED: TrailingActivationUnits and TrailingActivationValue
            // Trailing now uses the SAME trigger as elastic
            Services.TrailingElasticService.ProfitUnitType TrailingStopUnits,
            double TrailingStopValue,
            double DemaAtrMultiplier,
            int AtrPeriod,
            int DemaPeriod);

        public readonly record struct TrailingSettingsUpdate(
            bool EnableElastic,
            Services.TrailingElasticService.ProfitUnitType ElasticTriggerUnits,
            double ProfitUpdateThreshold,
            Services.TrailingElasticService.ProfitUnitType ElasticIncrementUnits,
            double ElasticIncrementValue,
            bool EnableTrailing,
            bool UseDemaAtrTrailing,
            // REMOVED: TrailingActivationUnits and TrailingActivationValue
            // Trailing now uses the SAME trigger as elastic
            Services.TrailingElasticService.ProfitUnitType TrailingStopUnits,
            double TrailingStopValue,
            double DemaAtrMultiplier,
            int AtrPeriod,
            int DemaPeriod);

        private void HandleTrade(Trade trade)
        {
            if (trade?.Symbol == null)
            {
                return;
            }

            var accountId = GetAccountId(trade.Account);
            if (!IsAccountEnabled(accountId))
            {
                return;
            }

            _trailingService.RecordTrade(trade);

            if (_sltpService.Enabled)
            {
                try
                {
                    _sltpService.HandleTrade(trade);
                }
                catch (Exception ex)
                {
                    EmitLog(QuantowerBridgeService.BridgeLogLevel.Warn, $"SLTP handler error: {ex.Message}");
                }
            }
        }

        private void OnBridgeStreamEnvelopeReceived(QuantowerBridgeService.BridgeStreamEnvelope envelope)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            var baseId = envelope.BaseId;
            if (string.IsNullOrWhiteSpace(baseId))
            {
                return;
            }

            var action = envelope.Action;
            if (!string.IsNullOrWhiteSpace(action))
            {
                if (action.Equals("HEDGE_CLOSED", StringComparison.OrdinalIgnoreCase) ||
                    action.Equals("NT_CLOSE_ACK", StringComparison.OrdinalIgnoreCase) ||
                    action.Equals("CLOSE_HEDGE", StringComparison.OrdinalIgnoreCase))
                {
                    EmitLog(QuantowerBridgeService.BridgeLogLevel.Info, $"Bridge confirmed hedge close for {baseId}. Stopping local trackers.");
                    StopTracking(baseId);
                    _trailingService.RemoveTracker(baseId);
                    return;
                }

                // Handle MT5 closure notifications - close corresponding Quantower position
                if (action.Equals("MT5_CLOSE_NOTIFICATION", StringComparison.OrdinalIgnoreCase))
                {
                    // Parse the raw JSON to determine if this is a full close or partial close
                    bool isFullClose = false;
                    string tradeResult = string.Empty;
                    double totalQuantity = -1;

                    if (!string.IsNullOrWhiteSpace(envelope.RawJson))
                    {
                        try
                        {
                            var json = System.Text.Json.JsonDocument.Parse(envelope.RawJson);
                            if (json.RootElement.TryGetProperty("nt_trade_result", out var tradeResultElement))
                            {
                                tradeResult = tradeResultElement.GetString() ?? string.Empty;
                            }
                            if (json.RootElement.TryGetProperty("total_quantity", out var totalQtyElement))
                            {
                                totalQuantity = totalQtyElement.GetDouble();
                            }
                        }
                        catch (Exception ex)
                        {
                            EmitLog(QuantowerBridgeService.BridgeLogLevel.Warn, $"Failed to parse MT5_CLOSE_NOTIFICATION JSON: {ex.Message}");
                        }
                    }

                    // Determine if this is a full close
                    // PRIORITY 1: Check nt_trade_result first (most reliable indicator)
                    // Full close indicators:
                    // - "MT5_position_closed" - MT5 closed the position
                    // - "already_closed" - Position was already closed when close was attempted
                    // - "mt5_closed" - Generic MT5 closure
                    // - "success" - Close operation succeeded
                    // - Contains "position_closed" - Any variant of position closed
                    //
                    // Partial close indicators:
                    // - "elastic_partial_close" - Elastic hedge partial close
                    // - Contains "partial" - Any partial close variant

                    // Check for partial close first (explicit indicator)
                    if (tradeResult.Contains("partial", StringComparison.OrdinalIgnoreCase))
                    {
                        isFullClose = false;
                        EmitLog(QuantowerBridgeService.BridgeLogLevel.Info, $"MT5 close notification for {baseId}: tradeResult='{tradeResult}' indicates PARTIAL close");
                    }
                    // Check for full close indicators
                    else if (tradeResult.Contains("position_closed", StringComparison.OrdinalIgnoreCase) ||
                             tradeResult.Contains("already_closed", StringComparison.OrdinalIgnoreCase) ||
                             tradeResult.Contains("mt5_closed", StringComparison.OrdinalIgnoreCase) ||
                             tradeResult.Equals("success", StringComparison.OrdinalIgnoreCase))
                    {
                        isFullClose = true;
                        EmitLog(QuantowerBridgeService.BridgeLogLevel.Info, $"MT5 close notification for {baseId}: tradeResult='{tradeResult}' indicates FULL close");
                    }
                    // PRIORITY 2: If nt_trade_result is ambiguous, check total_quantity
                    else if (totalQuantity == 0)
                    {
                        isFullClose = true;
                        EmitLog(QuantowerBridgeService.BridgeLogLevel.Info, $"MT5 close notification for {baseId}: totalQty={totalQuantity} indicates FULL close");
                    }
                    else
                    {
                        // Default to partial close if unclear
                        isFullClose = false;
                        EmitLog(QuantowerBridgeService.BridgeLogLevel.Warn, $"MT5 close notification for {baseId}: tradeResult='{tradeResult}', totalQty={totalQuantity} - UNCLEAR, defaulting to partial close");
                    }

                    if (isFullClose)
                    {
                        // Find and close the Quantower position
                        var position = FindPositionByBaseId(baseId);
                        if (position != null)
                        {
                            try
                            {
                                EmitLog(QuantowerBridgeService.BridgeLogLevel.Info, $"Closing Quantower position {position.Id} (base_id={baseId}) due to MT5 full closure");
                                _ = Task.Run(() => position.Close());
                                StopTracking(baseId);
                                _trailingService.RemoveTracker(baseId);
                            }
                            catch (Exception ex)
                            {
                                EmitLog(QuantowerBridgeService.BridgeLogLevel.Error, $"Failed to close Quantower position for {baseId}: {ex.Message}");
                            }
                        }
                        else
                        {
                            EmitLog(QuantowerBridgeService.BridgeLogLevel.Warn, $"MT5 full closure notification for {baseId} but no matching Quantower position found");
                            StopTracking(baseId);
                            _trailingService.RemoveTracker(baseId);
                        }
                    }
                    else
                    {
                        EmitLog(QuantowerBridgeService.BridgeLogLevel.Info, $"MT5 partial close for {baseId} - ignoring (Quantower position remains open)");
                    }
                    return;
                }
            }

            var eventType = envelope.EventType;
            if (!string.IsNullOrWhiteSpace(eventType) && eventType.Equals("quantower_position_closed", StringComparison.OrdinalIgnoreCase))
            {
                EmitLog(QuantowerBridgeService.BridgeLogLevel.Info, $"Bridge acknowledged Quantower position closure for {baseId}");
                StopTracking(baseId);
                _trailingService.RemoveTracker(baseId);
            }
        }

        private Position? FindPositionByBaseId(string baseId)
        {
            if (string.IsNullOrWhiteSpace(baseId))
            {
                return null;
            }

            var core = Core.Instance;
            if (core?.Positions == null)
            {
                return null;
            }

            // CRITICAL FIX (Issue #2): Check the baseId → Position.Id mapping first
            // This allows us to find Quantower positions when MT5 sends closure notifications
            if (_baseIdToPositionId.TryGetValue(baseId, out var positionId))
            {
                foreach (var position in core.Positions)
                {
                    if (string.Equals(position.Id, positionId, StringComparison.OrdinalIgnoreCase))
                    {
                        EmitLog(QuantowerBridgeService.BridgeLogLevel.Debug, $"Found position via mapping: baseId {baseId} -> Position.Id {positionId}");
                        return position;
                    }
                }
                // Position was in mapping but no longer exists - remove stale mapping
                _baseIdToPositionId.TryRemove(baseId, out _);
            }

            // Fallback to existing logic
            foreach (var position in core.Positions)
            {
                // Check if position ID matches base_id
                if (string.Equals(position.Id, baseId, StringComparison.OrdinalIgnoreCase))
                {
                    return position;
                }

                // Also check tracked positions
                var trackedBaseId = TryResolveTrackedBaseId(position);
                if (!string.IsNullOrWhiteSpace(trackedBaseId) &&
                    string.Equals(trackedBaseId, baseId, StringComparison.OrdinalIgnoreCase))
                {
                    return position;
                }
            }

            return null;
        }

        private void HandlePositionAdded(Position position)
        {
            if (position == null)
            {
                return;
            }

            var baseId = GetBaseId(position);

            // Log position details for debugging
            var positionDetails = $"baseId={baseId}, Position.Id={position.Id}, Symbol={position.Symbol?.Name}, Qty={position.Quantity:F2}";
            EmitLog(QuantowerBridgeService.BridgeLogLevel.Debug, $"HandlePositionAdded called: {positionDetails}");

            // Prevent concurrent processing of the same position
            // Try to add to processing set - if already processing, skip
            if (!_processingPositions.TryAdd(baseId, true))
            {
                EmitLog(QuantowerBridgeService.BridgeLogLevel.Info, $"Position {baseId} already being processed - skipping duplicate event");
                return;
            }

            try
            {
                if (!IsAccountEnabled(position))
                {
                    EmitLog(QuantowerBridgeService.BridgeLogLevel.Debug, $"Position {baseId} account not enabled - skipping");
                    StopTracking(baseId);
                    _trailingService.RemoveTracker(baseId);
                    return;
                }

                // Deduplicate position additions
                // Positions can be added multiple times:
                // 1. From SnapshotPositions() at startup (via TryPublishPositionSnapshotAsync)
                // 2. From Core.PositionAdded event (via OnQuantowerPositionAdded)
                // 3. From RefreshAccountPositions() when account is enabled
                // Check if we're already tracking this position to avoid duplicates
                lock (_trackingLock)
                {
                    if (_trackingStates.ContainsKey(baseId))
                    {
                        EmitLog(QuantowerBridgeService.BridgeLogLevel.Debug, $"Position {baseId} already being tracked - skipping duplicate add");
                        return;
                    }
                }

                // Maintain baseId → Position.Id mapping
                // This allows us to find Quantower positions when MT5 sends closure notifications
                if (!string.IsNullOrWhiteSpace(baseId) && !string.IsNullOrWhiteSpace(position.Id))
                {
                    _baseIdToPositionId[baseId] = position.Id;
                    EmitLog(QuantowerBridgeService.BridgeLogLevel.Debug, $"Mapped baseId {baseId} -> Position.Id {position.Id}");
                }

                // Track initial position quantity for proper hedge closure (n trades = n hedges)
                // Store the absolute quantity (number of contracts) when position is first opened
                var initialQuantity = (int)Math.Abs(position.Quantity);
                _baseIdToInitialQuantity[baseId] = initialQuantity;
                EmitLog(QuantowerBridgeService.BridgeLogLevel.Debug, $"Stored initial quantity {initialQuantity} for baseId {baseId}");

                EmitLog(QuantowerBridgeService.BridgeLogLevel.Info, $"Starting tracking for position {baseId}");
                _trailingService.RegisterPosition(baseId, position);
                SendElasticAndTrailing(position, baseId);
                StartTracking(position, baseId);
            }
            finally
            {
                // Remove from processing set
                _processingPositions.TryRemove(baseId, out _);
            }
        }

        private void HandlePositionRemoved(Position position)
        {
            if (position == null)
            {
                return;
            }

            // Log position details for debugging
            var positionDetails = $"Position.Id={position.Id}, Symbol={position.Symbol?.Name}, Qty={position.Quantity:F2}";
            EmitLog(QuantowerBridgeService.BridgeLogLevel.Debug, $"HandlePositionRemoved called: {positionDetails}");

            var existingBaseId = TryResolveTrackedBaseId(position);
            if (!string.IsNullOrWhiteSpace(existingBaseId))
            {
                EmitLog(QuantowerBridgeService.BridgeLogLevel.Info, $"Quantower position removed ({existingBaseId}) - stopping tracking and cleaning up mappings");

                // Remove baseId → Position.Id mapping
                _baseIdToPositionId.TryRemove(existingBaseId, out _);

                // Remove initial quantity tracking
                _baseIdToInitialQuantity.TryRemove(existingBaseId, out _);

                // Remove stop loss order tracking
                if (_stopLossOrders.TryRemove(existingBaseId, out _))
                {
                    EmitLog(QuantowerBridgeService.BridgeLogLevel.Debug, $"🗑️ Removed stop loss order tracking for {existingBaseId}");
                }

                // Stop tracking and remove from trailing service
                StopTracking(existingBaseId);
                _trailingService.RemoveTracker(existingBaseId);
                return;
            }

            var baseId = GetBaseId(position);
            EmitLog(QuantowerBridgeService.BridgeLogLevel.Info, $"Quantower position removed ({baseId}) - stopping tracking and cleaning up mappings");

            // Remove baseId → Position.Id mapping
            _baseIdToPositionId.TryRemove(baseId, out _);

            // Remove stop loss order tracking
            if (_stopLossOrders.TryRemove(baseId, out _))
            {
                EmitLog(QuantowerBridgeService.BridgeLogLevel.Debug, $"🗑️ Removed stop loss order tracking for {baseId}");
            }

            // Stop tracking and remove from trailing service
            StopTracking(baseId);
            _trailingService.RemoveTracker(baseId);
        }

        private void SendElasticAndTrailing(Position position, string? cachedBaseId = null)
        {
            var baseId = cachedBaseId ?? GetBaseId(position);
            if (string.IsNullOrWhiteSpace(baseId))
            {
                return;
            }

            try
            {
                var elasticPayload = _trailingService.TryBuildElasticUpdate(baseId, position);
                if (elasticPayload != null)
                {
                    var elasticJson = SimpleJson.SerializeObject(elasticPayload);
                    EmitLog(QuantowerBridgeService.BridgeLogLevel.Info, $"📤 Sending elastic update for {baseId}");
                    _ = _bridgeService.SubmitElasticUpdateAsync(elasticJson, baseId);
                }

                var trailingPayload = _trailingService.TryBuildTrailingUpdate(baseId, position);
                if (trailingPayload != null)
                {
                    var newStop = trailingPayload.ContainsKey("new_stop_price") ? trailingPayload["new_stop_price"] : null;
                    if (newStop != null && newStop is double newStopPrice)
                    {
                        EmitLog(QuantowerBridgeService.BridgeLogLevel.Info, $"🎯 Updating Quantower stop loss for {baseId} - newStop={newStopPrice:F2}");

                        // CRITICAL FIX: Track stop loss orders ourselves and modify them instead of creating new ones
                        // Quantower doesn't automatically link stop loss orders to positions via position.StopLoss
                        // So we maintain our own dictionary of stop loss orders keyed by baseId

                        if (_stopLossOrders.TryGetValue(baseId, out var existingOrder))
                        {
                            // Stop loss order exists in our tracking - modify it
                            try
                            {
                                EmitLog(QuantowerBridgeService.BridgeLogLevel.Debug, $"📝 Modifying existing stop loss order for {baseId} to {newStopPrice:F2}");
                                var result = Core.Instance.ModifyOrder(existingOrder, price: newStopPrice);
                                if (result.Status == TradingOperationResultStatus.Success)
                                {
                                    EmitLog(QuantowerBridgeService.BridgeLogLevel.Info, $"✅ Successfully modified Quantower stop loss to {newStopPrice:F2}");
                                }
                                else
                                {
                                    EmitLog(QuantowerBridgeService.BridgeLogLevel.Error, $"❌ Failed to modify Quantower stop loss: {result.Message}");
                                }
                            }
                            catch (Exception modifyEx)
                            {
                                EmitLog(QuantowerBridgeService.BridgeLogLevel.Error, $"❌ Exception modifying stop loss: {modifyEx.Message}");
                            }
                        }
                        else
                        {
                            // Stop loss order doesn't exist in our tracking - create it using PlaceOrder
                            EmitLog(QuantowerBridgeService.BridgeLogLevel.Info, $"📝 Creating new stop loss order for {baseId} at {newStopPrice:F2}");

                            try
                            {
                                // Determine the side for the stop loss order (opposite of position side)
                                var stopSide = position.Side == Side.Buy ? Side.Sell : Side.Buy;

                                // Place a stop loss order
                                var result = Core.Instance.PlaceOrder(new PlaceOrderRequestParameters
                                {
                                    Symbol = position.Symbol,
                                    Account = position.Account,
                                    Side = stopSide,
                                    OrderTypeId = OrderType.Stop,
                                    TriggerPrice = newStopPrice,
                                    Quantity = Math.Abs(position.Quantity),
                                    TimeInForce = TimeInForce.GTC
                                });

                                if (result.Status == TradingOperationResultStatus.Success)
                                {
                                    EmitLog(QuantowerBridgeService.BridgeLogLevel.Info, $"✅ Successfully created Quantower stop loss at {newStopPrice:F2}");

                                    // Store the order in our tracking dictionary so we can modify it next time
                                    // The order should now be linked to the position via position.StopLoss
                                    // Wait a moment for Quantower to link the order to the position
                                    System.Threading.Thread.Sleep(100);

                                    // Try to get the stop loss order from the position
                                    if (position.StopLoss != null)
                                    {
                                        _stopLossOrders[baseId] = position.StopLoss;
                                        EmitLog(QuantowerBridgeService.BridgeLogLevel.Debug, $"📌 Stored stop loss order for {baseId} in tracking dictionary");
                                    }
                                    else
                                    {
                                        EmitLog(QuantowerBridgeService.BridgeLogLevel.Warn, $"⚠️ Stop loss order created but not yet linked to position {baseId}");
                                    }
                                }
                                else
                                {
                                    EmitLog(QuantowerBridgeService.BridgeLogLevel.Error, $"❌ Failed to create Quantower stop loss: {result.Message}");
                                }
                            }
                            catch (Exception createEx)
                            {
                                EmitLog(QuantowerBridgeService.BridgeLogLevel.Error, $"❌ Exception creating stop loss: {createEx.Message}");
                            }
                        }

                        // CRITICAL FIX (Issue #3): DO NOT send trailing updates to MT5
                        // Trailing stops should ONLY modify the Quantower stop loss locally
                        // Only elastic updates should be sent to MT5
                        // The code above already modified the Quantower stop loss using Core.ModifyOrder()
                        EmitLog(QuantowerBridgeService.BridgeLogLevel.Debug, $"Trailing stop updated locally in Quantower for {baseId} - NOT sending to MT5");
                    }
                }
            }
            catch (Exception ex)
            {
                EmitLog(QuantowerBridgeService.BridgeLogLevel.Warn, $"Failed to process trailing/elastic update: {ex.Message}");
            }
        }

        private void StartTracking(Position position, string? cachedBaseId = null)
        {
            var baseId = cachedBaseId ?? GetBaseId(position);
            if (string.IsNullOrWhiteSpace(baseId))
            {
                return;
            }

            if (!IsAccountEnabled(position))
            {
                return;
            }

            _trailingService.RegisterPosition(baseId, position);

            lock (_trackingLock)
            {
                if (_trackingStates.TryGetValue(baseId, out var existing))
                {
                    existing.PositionId = position.Id ?? existing.PositionId;
                    existing.AccountId = GetAccountId(position.Account);
                    existing.SymbolName = position.Symbol?.Name;
                    return;
                }

                var state = new TrackingState
                {
                    BaseId = baseId,
                    PositionId = position.Id,
                    AccountId = GetAccountId(position.Account),
                    SymbolName = position.Symbol?.Name
                };

                // Avoid creating timers when disposal has started
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }

                state.Timer = new Timer(OnTrackingTimer, state, _trackingInterval, _trackingInterval);
                _trackingStates[baseId] = state;
            }
        }

        private void StopTracking(string baseId)
        {
            TrackingState? state = null;

            lock (_trackingLock)
            {
                if (_trackingStates.TryGetValue(baseId, out var existing))
                {
                    state = existing;
                    _trackingStates.Remove(baseId);
                }
            }

            if (state?.Timer != null)
            {
                try
                {
                    state.Timer.Change(Timeout.Infinite, Timeout.Infinite);
                    state.Timer.Dispose();
                }
                catch
                {
                    // ignore disposal errors
                }
            }

            // Clean up quantity tracking
            _baseIdToInitialQuantity.TryRemove(baseId, out _);

            _trailingService.RemoveTracker(baseId);
        }

        private void StopTrackingByAccount(string? accountId)
        {
            if (string.IsNullOrWhiteSpace(accountId))
            {
                return;
            }

            List<string> targets;
            lock (_trackingLock)
            {
                targets = new List<string>();
                foreach (var pair in _trackingStates)
                {
                    if (string.Equals(pair.Value.AccountId, accountId, StringComparison.OrdinalIgnoreCase))
                    {
                        targets.Add(pair.Key);
                    }
                }
            }

            foreach (var baseId in targets)
            {
                StopTracking(baseId);
            }
        }

        private void StopAllTracking()
        {
            List<string> keys;
            lock (_trackingLock)
            {
                keys = new List<string>(_trackingStates.Keys);
            }

            foreach (var baseId in keys)
            {
                StopTracking(baseId);
            }
        }

        private void StartRiskTimer()
        {
            var interval = RiskTimerInterval <= TimeSpan.Zero ? TimeSpan.FromSeconds(5) : RiskTimerInterval;
            _riskTimer ??= new Timer(OnRiskTimer, null, interval, interval);
        }

        private void OnRiskTimer(object? state)
        {
            if (Volatile.Read(ref _disposed) != 0 || !IsConnected)
            {
                return;
            }

            try
            {
                var accountsSnapshot = SnapshotAccounts();
                var accountLookup = BuildAccountLookup(accountsSnapshot);

                EvaluateDailyReset(accountLookup);

                foreach (var subscription in accountsSnapshot)
                {
                    if (subscription.IsEnabled)
                    {
                        EvaluateRisk(subscription);
                    }
                }
            }
            catch (Exception ex)
            {
                EmitLog(QuantowerBridgeService.BridgeLogLevel.Warn, $"Risk timer error: {ex.Message}");
            }
        }

        private void EvaluateDailyReset(IReadOnlyDictionary<string, Account?> accountLookup)
        {
            var today = DateTime.UtcNow.Date;
            var resetPerformed = false;

            lock (_riskLock)
            {
                if (today <= _riskSettings.LastResetDateUtc.Date)
                {
                    return;
                }

                foreach (var kvp in _riskSettings.Accounts)
                {
                    if (accountLookup.TryGetValue(kvp.Key, out var account) && account != null)
                    {
                        kvp.Value.BalanceBaseline = account.Balance;
                    }

                    kvp.Value.LimitTriggered = false;
                    kvp.Value.LastKnownPnL = 0;
                    kvp.Value.LastTriggerUtc = DateTime.MinValue;
                }

                _riskSettings.LastResetDateUtc = today;
                resetPerformed = true;
            }

            if (resetPerformed)
            {
                SaveSettings();
                EmitLog(QuantowerBridgeService.BridgeLogLevel.Info, "Daily risk baselines reset");
            }
        }

        private void EvaluateRisk(AccountSubscription subscription)
        {
            var account = subscription.Account;
            if (account == null)
            {
                return;
            }

            AccountRiskState state;
            double pnl;

            lock (_riskLock)
            {
                state = GetOrCreateRiskState(subscription.AccountId, account);
                pnl = CalculateAccountPnl(account, subscription.AccountId, state);
            }

            if (_riskSettings.DailyTakeProfit <= 0 && _riskSettings.DailyLossLimit <= 0)
            {
                return;
            }

            bool limitHit = false;
            string reason = string.Empty;

            if (_riskSettings.DailyTakeProfit > 0 && pnl >= _riskSettings.DailyTakeProfit)
            {
                limitHit = true;
                reason = "take_profit";
            }
            else if (_riskSettings.DailyLossLimit > 0 && pnl <= -Math.Abs(_riskSettings.DailyLossLimit))
            {
                limitHit = true;
                reason = "loss_limit";
            }

            if (!limitHit)
            {
                return;
            }

            lock (_riskLock)
            {
                if (state.LimitTriggered)
                {
                    return;
                }

                state.LimitTriggered = true;
                state.LastTriggerUtc = DateTime.UtcNow;
            }

            EmitLog(QuantowerBridgeService.BridgeLogLevel.Warn, $"Risk limit {reason} triggered for account {subscription.DisplayName}: pnl={pnl:F2}");

            if (_riskSettings.AutoFlatten)
            {
                _ = FlattenAccountInternalAsync(subscription, reason, _riskSettings.DisableOnLimit);
            }
            else if (_riskSettings.DisableOnLimit)
            {
                subscription.IsEnabled = false;
                StopTrackingByAccount(subscription.AccountId);
            }

            SaveSettings();
            RaiseAccountsChanged();
        }

        private double CalculateAccountPnl(Account account, string accountId, AccountRiskState state)
        {
            var balance = account.Balance;
            if (Math.Abs(state.BalanceBaseline) < double.Epsilon)
            {
                state.BalanceBaseline = balance;
            }

            var balanceDelta = balance - state.BalanceBaseline;
            double unrealized = 0.0;

            foreach (var position in EnumeratePositions(accountId))
            {
                var pnlItem = position.NetPnL ?? position.GrossPnL;
                unrealized += PnLUtils.GetMoney(pnlItem);
            }

            var total = balanceDelta + unrealized;
            state.LastKnownPnL = total;
            return total;
        }

        private AccountRiskState GetOrCreateRiskState(string accountId, Account? account = null)
        {
            var key = string.IsNullOrWhiteSpace(accountId) ? string.Empty : accountId;
            var state = _riskSettings.Accounts.GetOrAdd(key, _ => new AccountRiskState());

            if (Math.Abs(state.BalanceBaseline) < double.Epsilon && account != null)
            {
                state.BalanceBaseline = account.Balance;
            }

            return state;
        }

        private static IEnumerable<Position> EnumeratePositions(string accountId)
        {
            var core = Core.Instance;
            if (core?.Positions == null)
            {
                yield break;
            }

            foreach (var position in core.Positions)
            {
                if (string.Equals(GetAccountId(position.Account), accountId, StringComparison.OrdinalIgnoreCase))
                {
                    yield return position;
                }
            }
        }

        private async Task<bool> FlattenAccountInternalAsync(AccountSubscription subscription, string reason, bool disableAfter)
        {
            var accountId = subscription.AccountId;
            var positions = EnumeratePositions(accountId).ToList();
            var success = true;

            if (positions.Count == 0)
            {
                EmitLog(QuantowerBridgeService.BridgeLogLevel.Info, $"No open positions to flatten for account {subscription.DisplayName}");
                if (disableAfter)
                {
                    subscription.IsEnabled = false;
                    StopTrackingByAccount(subscription.AccountId);
                }
                return true;
            }

            foreach (var position in positions)
            {
                try
                {
                    await Task.Run(() => position.Close()).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    success = false;
                    EmitLog(QuantowerBridgeService.BridgeLogLevel.Warn, $"Failed to close position {position.Symbol?.Name}: {ex.Message}");
                }
            }

            if (disableAfter)
            {
                subscription.IsEnabled = false;
                StopTrackingByAccount(subscription.AccountId);
            }

            EmitLog(QuantowerBridgeService.BridgeLogLevel.Info, $"Flatten operation completed for account {subscription.DisplayName} (reason={reason}, success={success})");
            return success;
        }

        private void OnTrackingTimer(object? state)
        {
            if (state is TrackingState trackingState)
            {
                try
                {
                    UpdateTracking(trackingState);
                }
                catch (Exception ex)
                {
                    EmitLog(QuantowerBridgeService.BridgeLogLevel.Warn, $"Tracking timer error: {ex.Message}");
                }
            }
        }

        private void UpdateTracking(TrackingState state)
        {
            if (!IsAccountEnabled(state.AccountId))
            {
                StopTracking(state.BaseId);
                return;
            }

            var position = FindPosition(state);
            if (position == null || Math.Abs(position.Quantity) <= double.Epsilon)
            {
                StopTracking(state.BaseId);
                return;
            }

            SendElasticAndTrailing(position, state.BaseId);
        }

        private Position? FindPosition(TrackingState state)
        {
            var core = Core.Instance;
            if (core?.Positions == null)
            {
                return null;
            }

            foreach (var position in core.Positions)
            {
                if (!string.IsNullOrEmpty(state.PositionId) &&
                    string.Equals(position.Id, state.PositionId, StringComparison.OrdinalIgnoreCase))
                {
                    return position;
                }

                if (!string.IsNullOrEmpty(state.SymbolName) &&
                    string.Equals(position.Symbol?.Name, state.SymbolName, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrEmpty(state.AccountId) &&
                    string.Equals(GetAccountId(position.Account), state.AccountId, StringComparison.OrdinalIgnoreCase))
                {
                    return position;
                }
            }

            return null;
        }

        private void RefreshAccountPositions(string? accountId)
        {
            if (string.IsNullOrWhiteSpace(accountId))
            {
                return;
            }

            var core = Core.Instance;
            if (core?.Positions == null)
            {
                return;
            }

            foreach (var position in core.Positions)
            {
                if (string.Equals(GetAccountId(position.Account), accountId, StringComparison.OrdinalIgnoreCase))
                {
                    HandlePositionAdded(position);
                }
            }
        }

        private List<AccountSubscription> SnapshotAccounts()
        {
            lock (_accountsLock)
            {
                return _accounts.ToList();
            }
        }

        private static Dictionary<string, Account?> BuildAccountLookup(IEnumerable<AccountSubscription> subscriptions)
        {
            var map = new Dictionary<string, Account?>(StringComparer.OrdinalIgnoreCase);
            foreach (var subscription in subscriptions)
            {
                var key = subscription.AccountId;
                if (string.IsNullOrWhiteSpace(key))
                {
                    key = string.Empty;
                }

                map[key] = subscription.Account;
            }

            return map;
        }

        private bool IsAccountEnabled(Position position)
        {
            var accountId = GetAccountId(position.Account);
            return IsAccountEnabled(accountId);
        }

        private bool IsAccountEnabled(string? accountId)
        {
            if (string.IsNullOrWhiteSpace(accountId))
            {
                return true;
            }

            lock (_accountsLock)
            {
                foreach (var subscription in _accounts)
                {
                    if (string.Equals(subscription.AccountId, accountId, StringComparison.OrdinalIgnoreCase))
                    {
                        return subscription.IsEnabled;
                    }
                }
            }

            return true;
        }

        private static string? GetAccountId(Account? account)
        {
            return account?.Id ?? account?.Name;
        }

        private void AttachSubscription(AccountSubscription subscription)
        {
            subscription.PropertyChanged -= OnAccountSubscriptionChanged;
            subscription.PropertyChanged += OnAccountSubscriptionChanged;

            if (subscription.Account != null)
            {
                lock (_riskLock)
                {
                    GetOrCreateRiskState(subscription.AccountId, subscription.Account);
                }
            }
        }

        private void DetachSubscription(AccountSubscription subscription)
        {
            subscription.PropertyChanged -= OnAccountSubscriptionChanged;
        }

        private void OnAccountSubscriptionChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not AccountSubscription subscription || e.PropertyName != nameof(AccountSubscription.IsEnabled))
            {
                return;
            }

            if (subscription.IsEnabled)
            {
                if (subscription.Account != null)
                {
                    lock (_riskLock)
                    {
                        GetOrCreateRiskState(subscription.AccountId, subscription.Account);
                    }
                }

                RefreshAccountPositions(subscription.AccountId);
            }
            else
            {
                StopTrackingByAccount(subscription.AccountId);
            }

            SaveSettings();
        }

        private string GetBaseId(Position position)
        {
            // CRITICAL: Use Position.Id alone as baseId (no OpenTime concatenation)
            // Quantower does NOT reuse Position.Id, so it's stable across the position's lifecycle.
            //
            // This MUST match the logic in QuantowerTradeMapper.ComputeBaseId() to ensure
            // that the baseId used when sending positions to the bridge matches the baseId
            // used for tracking and elastic/trailing updates.
            //
            // The Position.Id remains constant from open to close, ensuring proper 1:1 correlation
            // between Quantower positions and MT5 hedge trades.

            var positionId = position.Id;

            if (!string.IsNullOrWhiteSpace(positionId))
            {
                // Use Position.Id directly as baseId (stable across lifecycle)
                // This matches QuantowerTradeMapper.ComputeBaseId() logic
                return positionId;
            }

            // Fallback: If Position.Id is null (should never happen), log error
            EmitLog(QuantowerBridgeService.BridgeLogLevel.Error,
                $"Position.Id is null for position on {position.Symbol?.Name} - this should never happen!");

            // Generate a fallback ID (but this indicates a serious problem)
            var accountId = GetAccountId(position.Account) ?? "account";
            var symbolName = position.Symbol?.Name ?? "symbol";
            return $"{accountId}:{symbolName}:{DateTime.UtcNow.Ticks}";
        }

        private string? TryResolveTrackedBaseId(Position position)
        {
            var baseIdCandidate = GetBaseId(position);
            var positionId = position.Id;
            var accountId = GetAccountId(position.Account);
            var symbolName = position.Symbol?.Name;

            lock (_trackingLock)
            {
                if (!string.IsNullOrWhiteSpace(baseIdCandidate) && _trackingStates.ContainsKey(baseIdCandidate))
                {
                    return baseIdCandidate;
                }

                foreach (var pair in _trackingStates)
                {
                    var state = pair.Value;

                    if (!string.IsNullOrWhiteSpace(positionId) && string.Equals(state.PositionId, positionId, StringComparison.OrdinalIgnoreCase))
                    {
                        return pair.Key;
                    }

                    if (!string.IsNullOrWhiteSpace(accountId)
                        && !string.IsNullOrWhiteSpace(symbolName)
                        && string.Equals(state.AccountId, accountId, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(state.SymbolName, symbolName, StringComparison.OrdinalIgnoreCase))
                    {
                        return pair.Key;
                    }
                }
            }

            return null;
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(MultiStratManagerService));
            }
        }

        /// <summary>
        /// Gets the tracked initial quantity for a position by its baseId.
        /// Returns null if no quantity was tracked for this baseId.
        /// This is used to determine how many MT5 hedges to close (n trades = n hedges).
        /// </summary>
        public int? GetTrackedInitialQuantity(string baseId)
        {
            if (string.IsNullOrWhiteSpace(baseId))
            {
                return null;
            }

            if (_baseIdToInitialQuantity.TryGetValue(baseId, out var quantity))
            {
                return quantity;
            }

            return null;
        }
    }
}
