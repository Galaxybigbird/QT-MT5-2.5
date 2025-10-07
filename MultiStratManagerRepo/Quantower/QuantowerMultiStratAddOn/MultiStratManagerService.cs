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
using System.Reflection;
using Quantower.Bridge.Client;
using Quantower.MultiStrat.Infrastructure;
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
        // Track Quantower trade ids we've already applied to quantity bookkeeping to avoid double counting.
        private readonly ConcurrentDictionary<string, byte> _processedTradeIds = new(StringComparer.OrdinalIgnoreCase);
        // Track initial position quantities for proper hedge closure (n trades = n hedges)
        private readonly ConcurrentDictionary<string, int> _baseIdToInitialQuantity = new(StringComparer.OrdinalIgnoreCase);
        // Track current position quantities for partial closure detection
        private readonly ConcurrentDictionary<string, int> _baseIdToCurrentQuantity = new(StringComparer.OrdinalIgnoreCase);
        // Remember the last non-zero quantity we observed so late closure events can still determine
        // how many hedges need to unwind even if Quantower has already flushed its trackers.
        private readonly ConcurrentDictionary<string, int> _baseIdToLastKnownQuantity = new(StringComparer.OrdinalIgnoreCase);
        // Track position side (Buy/Sell) to detect closing trades
        private readonly ConcurrentDictionary<string, Side> _baseIdToSide = new(StringComparer.OrdinalIgnoreCase);
        // Map Quantower trade identifiers to base ids for fallback correlation
        private readonly ConcurrentDictionary<string, string> _tradeIdToBaseId = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> _orderIdToBaseId = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, int> _positionContractCounts = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _positionOpenContracts = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> _pendingContractCloseAcks = new(StringComparer.OrdinalIgnoreCase);
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
            _bridgeService.ResolveBaseId = ResolveBaseIdFromTrade;
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

            var baseId = ResolveBaseIdFromTrade(trade);
            if (string.IsNullOrWhiteSpace(baseId))
            {
                EmitLog(QuantowerBridgeService.BridgeLogLevel.Warn, $"Trade {trade.Id} missing PositionId - skipping hedge synchronization");
                _trailingService.RecordTrade(trade);
                if (_sltpService.Enabled)
                {
                    TryHandleSltpTrade(trade);
                }
                return;
            }

            if (!string.IsNullOrWhiteSpace(trade.Id))
            {
                _tradeIdToBaseId[trade.Id] = baseId;

                if (!_processedTradeIds.TryAdd(trade.Id, 0))
                {
                    _trailingService.RecordTrade(trade);
                    if (_sltpService.Enabled)
                    {
                        TryHandleSltpTrade(trade);
                    }
                    return;
                }
            }

            if (!string.IsNullOrWhiteSpace(trade.OrderId))
            {
                _orderIdToBaseId[trade.OrderId] = baseId;
            }

            var contracts = GetContractsFromTrade(trade);
            if (contracts <= 0)
            {
                contracts = 1;
            }

            switch (trade.PositionImpactType)
            {
                case PositionImpactType.Open:
                    HandleOpeningTradeSimple(baseId, trade, contracts);
                    break;
                case PositionImpactType.Close:
                    HandleClosingTradeSimple(baseId, trade, contracts);
                    break;
                default:
                    if (IsClosingTrade(trade, baseId))
                    {
                        HandleClosingTradeSimple(baseId, trade, contracts);
                    }
                    else
                    {
                        HandleOpeningTradeSimple(baseId, trade, contracts);
                    }
                    break;
            }

            _trailingService.RecordTrade(trade);

            if (_sltpService.Enabled)
            {
                TryHandleSltpTrade(trade);
            }
        }

        private void TryHandleSltpTrade(Trade trade)
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

        private static string GenerateContractBaseId(string? tradeId, string positionId, int index, int totalContracts)
        {
            var token = !string.IsNullOrWhiteSpace(tradeId)
                ? tradeId
                : $"{positionId}-{Guid.NewGuid():N}";

            if (totalContracts <= 1)
            {
                return token;
            }

            return $"{token}-{index + 1}";
        }

        private string? BuildOpenPayload(string positionId, string contractBaseId, Trade trade)
        {
            var instrument = GetInstrumentName(positionId, trade);
            var accountName = GetAccountName(positionId, trade);

            if (string.IsNullOrWhiteSpace(instrument) || string.IsNullOrWhiteSpace(accountName))
            {
                EmitLog(QuantowerBridgeService.BridgeLogLevel.Warn,
                    $"Missing instrument/account for open payload {contractBaseId} (position {positionId})");
            }

            var payload = new Dictionary<string, object?>
            {
                ["origin_platform"] = "quantower",
                ["id"] = contractBaseId,
                ["base_id"] = contractBaseId,
                ["qt_trade_id"] = trade?.Id ?? string.Empty,
                ["qt_position_id"] = positionId,
                ["instrument"] = instrument,
                ["instrument_name"] = instrument,
                ["nt_instrument_symbol"] = instrument,
                ["account_name"] = accountName,
                ["nt_account_name"] = accountName,
                ["quantity"] = 1d,
                ["total_quantity"] = Math.Abs(trade?.Quantity ?? 1d),
                ["action"] = trade?.Side == Side.Buy ? "buy" : "sell",
                ["price"] = trade?.Price ?? 0d,
                ["timestamp"] = (trade?.DateTime ?? DateTime.UtcNow).ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)
            };

            if (!string.IsNullOrWhiteSpace(trade?.OrderId))
            {
                payload["order_id"] = trade.OrderId;
            }

            if (!string.IsNullOrWhiteSpace(trade?.Comment))
            {
                payload["comment"] = trade.Comment;
            }

            return SimpleJson.SerializeObject(payload);
        }

        private void HandleOpeningTradeSimple(string baseId, Trade trade, int contracts)
        {
            _positionContractCounts.AddOrUpdate(baseId, contracts, (_, existing) => existing + contracts);
            _baseIdToSide[baseId] = trade.Side;

            var queue = _positionOpenContracts.GetOrAdd(baseId, _ => new ConcurrentQueue<string>());

            for (var i = 0; i < contracts; i++)
            {
                var contractBaseId = GenerateContractBaseId(trade?.Id, baseId, i, contracts);
                queue.Enqueue(contractBaseId);
                _baseIdToPositionId[contractBaseId] = baseId;

                var payload = BuildOpenPayload(baseId, contractBaseId, trade);
                if (!string.IsNullOrWhiteSpace(payload))
                {
                    _ = _bridgeService.SubmitTradeAsync(payload);
                }
            }
        }

        private void HandleClosingTradeSimple(string baseId, Trade trade, int contracts)
        {
            var queue = _positionOpenContracts.GetOrAdd(baseId, _ => new ConcurrentQueue<string>());
            var consumed = new List<string>(contracts);

            for (var i = 0; i < contracts; i++)
            {
                if (queue.TryDequeue(out var contractBaseId))
                {
                    consumed.Add(contractBaseId);
                }
                else
                {
                    EmitLog(QuantowerBridgeService.BridgeLogLevel.Warn,
                        $"Quantower requested close of {contracts} hedge(s) for {baseId}, but only {consumed.Count} contract(s) remain tracked");
                    break;
                }
            }

            if (consumed.Count == 0)
            {
                var fallbackId = !string.IsNullOrWhiteSpace(trade?.Id)
                    ? $"{trade.Id}-close-{Guid.NewGuid():N}"
                    : $"{baseId}-close-{Guid.NewGuid():N}";

                EmitLog(QuantowerBridgeService.BridgeLogLevel.Warn,
                    $"No tracked contract base IDs remained for {baseId}; synthesizing fallback close id {fallbackId}");

                consumed.Add(fallbackId);
                _baseIdToPositionId[fallbackId] = baseId;
            }

            var remaining = _positionContractCounts.AddOrUpdate(
                baseId,
                _ => 0,
                (_, current) =>
                {
                    var updated = current - consumed.Count;
                    return updated < 0 ? 0 : updated;
                });

            foreach (var contractBaseId in consumed)
            {
                var payload = BuildClosePayload(baseId, contractBaseId, trade);
                if (!string.IsNullOrWhiteSpace(payload))
                {
                    _pendingContractCloseAcks[contractBaseId] = 0;
                    _ = _bridgeService.SubmitCloseHedgeAsync(payload, contractBaseId);
                }
            }

            if (remaining <= 0 || trade.PositionImpactType == PositionImpactType.Close)
            {
                _positionContractCounts.TryRemove(baseId, out _);
                _positionOpenContracts.TryRemove(baseId, out _);
            }
        }

        private string ResolvePositionBaseId(string baseId)
        {
            if (string.IsNullOrWhiteSpace(baseId))
            {
                return baseId;
            }

            if (_baseIdToPositionId.TryGetValue(baseId, out var positionId) && !string.IsNullOrWhiteSpace(positionId))
            {
                return positionId;
            }

            return baseId;
        }

        private int ConsumeContractsFromQueue(string positionBaseId, string? contractBaseId, int requestedCount)
        {
            if (string.IsNullOrWhiteSpace(positionBaseId))
            {
                return 0;
            }

            if (!_positionOpenContracts.TryGetValue(positionBaseId, out var queue) || queue == null)
            {
                return 0;
            }

            var removed = 0;
            var buffer = new List<string>();

            lock (queue)
            {
                while (queue.TryDequeue(out var current))
                {
                    if (!string.IsNullOrWhiteSpace(contractBaseId))
                    {
                        if (string.Equals(current, contractBaseId, StringComparison.OrdinalIgnoreCase) && removed == 0)
                        {
                            removed++;
                            continue;
                        }
                    }
                    else if (removed < requestedCount)
                    {
                        removed++;
                        continue;
                    }

                    buffer.Add(current);
                }

                foreach (var id in buffer)
                {
                    queue.Enqueue(id);
                }
            }

            return removed;
        }

        private int SubtractContractCount(string positionBaseId, int amount)
        {
            if (string.IsNullOrWhiteSpace(positionBaseId) || amount <= 0)
            {
                return _positionContractCounts.TryGetValue(positionBaseId ?? string.Empty, out var existing) ? existing : 0;
            }

            return _positionContractCounts.AddOrUpdate(
                positionBaseId,
                _ => 0,
                (_, current) =>
                {
                    var updated = current - amount;
                    return updated < 0 ? 0 : updated;
                });
        }

        private void RemovePendingCloseEntries(string positionBaseId)
        {
            if (string.IsNullOrWhiteSpace(positionBaseId))
            {
                return;
            }

            foreach (var pair in _pendingContractCloseAcks.ToArray())
            {
                if (string.Equals(pair.Key, positionBaseId, StringComparison.OrdinalIgnoreCase))
                {
                    _pendingContractCloseAcks.TryRemove(pair.Key, out _);
                    continue;
                }

                if (_baseIdToPositionId.TryGetValue(pair.Key, out var ownerBaseId) &&
                    string.Equals(ownerBaseId, positionBaseId, StringComparison.OrdinalIgnoreCase))
                {
                    _pendingContractCloseAcks.TryRemove(pair.Key, out _);
                }
            }
        }

        private void RemoveContractBaseMappings(string positionBaseId)
        {
            if (string.IsNullOrWhiteSpace(positionBaseId))
            {
                return;
            }

            foreach (var pair in _baseIdToPositionId.ToArray())
            {
                if (string.Equals(pair.Key, positionBaseId, StringComparison.OrdinalIgnoreCase))
                {
                    _baseIdToPositionId.TryRemove(pair.Key, out _);
                    continue;
                }

                if (string.Equals(pair.Value, positionBaseId, StringComparison.OrdinalIgnoreCase))
                {
                    _baseIdToPositionId.TryRemove(pair.Key, out _);
                }
            }
        }

        private string? BuildClosePayload(string positionId, string contractBaseId, Trade trade)
        {
            var instrument = GetInstrumentName(positionId, trade);
            var accountName = GetAccountName(positionId, trade);

            if (string.IsNullOrWhiteSpace(instrument) || string.IsNullOrWhiteSpace(accountName))
            {
                EmitLog(QuantowerBridgeService.BridgeLogLevel.Warn,
                    $"Missing instrument/account for close payload {contractBaseId} (position {positionId})");
            }

            var payload = new Dictionary<string, object?>
            {
                ["event_type"] = "quantower_position_closed",
                ["origin_platform"] = "quantower",
                ["closure_reason"] = trade.PositionImpactType == PositionImpactType.Close ? "qt_position_removed" : "qt_partial_close",
                ["id"] = contractBaseId,
                ["base_id"] = contractBaseId,
                ["qt_position_id"] = positionId,
                ["instrument"] = instrument,
                ["instrument_name"] = instrument,
                ["nt_instrument_symbol"] = instrument,
                ["account_name"] = accountName,
                ["nt_account_name"] = accountName,
                ["closed_hedge_quantity"] = 1d,
                ["closed_hedge_action"] = ResolveCloseAction(trade, positionId),
                ["timestamp"] = DateTime.UtcNow.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                ["mt5_ticket"] = 0,
                ["qt_trade_id"] = trade?.Id ?? string.Empty
            };

            return SimpleJson.SerializeObject(payload);
        }

        private string? GetInstrumentName(string baseId, Trade trade)
        {
            var symbolName = trade?.Symbol?.Name ?? trade?.Symbol?.Id ?? trade?.Symbol?.Description;
            if (!string.IsNullOrWhiteSpace(symbolName))
            {
                return symbolName;
            }

            var position = FindPositionByBaseId(baseId);
            if (position?.Symbol != null)
            {
                return position.Symbol.Name ?? position.Symbol.Id ?? position.Symbol.Description;
            }

            return null;
        }

        private string? GetAccountName(string baseId, Trade trade)
        {
            var accountName = trade?.Account?.Name ?? trade?.Account?.Id;
            if (!string.IsNullOrWhiteSpace(accountName))
            {
                return accountName;
            }

            var position = FindPositionByBaseId(baseId);
            if (position?.Account != null)
            {
                return position.Account.Name ?? position.Account.Id;
            }

            return null;
        }

        private void HandleOpeningTrade(Trade trade, string baseId, Position? position)
        {
            var contracts = GetContractsFromTrade(trade);
            EmitLog(QuantowerBridgeService.BridgeLogLevel.Debug, $"Opening trade {trade.Id} for {baseId}: qty={trade.Quantity:F2}, contracts={contracts}, impact={trade.PositionImpactType}");
            if (contracts <= 0)
            {
                return;
            }

            AccumulateOpenQuantity(baseId, trade.Side, contracts);

            if (position != null)
            {
                UpdateTrackingFromPosition(baseId, position, allowDecrease: false);
            }
        }

        private void HandleClosingTrade(Trade trade, string baseId, Position? position)
        {
            var trackedCurrent = _baseIdToCurrentQuantity.TryGetValue(baseId, out var currentQty) ? currentQty : 0;
            var trackedInitial = _baseIdToInitialQuantity.TryGetValue(baseId, out var initialQty) ? initialQty : 0;
            var lastKnown = _baseIdToLastKnownQuantity.TryGetValue(baseId, out var lastQty) ? lastQty : 0;

            var tradeContracts = GetContractsFromTrade(trade);
            var contractsToClose = tradeContracts;

            var remainingContracts = position != null
                ? (int)Math.Round(Math.Abs(position.Quantity))
                : -1;

            if (trackedCurrent > 0)
            {
                if (remainingContracts >= 0)
                {
                    var delta = trackedCurrent - remainingContracts;
                    if (delta > 0)
                    {
                        contractsToClose = delta;
                    }
                }

                if (trade.PositionImpactType == PositionImpactType.Close)
                {
                    contractsToClose = Math.Max(contractsToClose, trackedCurrent);
                }
            }

            if (contractsToClose <= 0)
            {
                contractsToClose = tradeContracts > 0 ? tradeContracts : Math.Max(Math.Max(trackedCurrent, Math.Max(initialQty, lastQty)), 1);
            }

            EmitLog(QuantowerBridgeService.BridgeLogLevel.Debug,
                $"Closing trade {trade.Id} for {baseId}: qty={trade.Quantity:F2}, impact={trade.PositionImpactType}, trackedCurrent={trackedCurrent}, trackedInitial={trackedInitial}, lastKnown={lastKnown}, positionRemaining={remainingContracts}, closing={contractsToClose}");

            if (contractsToClose <= 0)
            {
                return;
            }

            var remaining = ReduceTrackedQuantity(baseId, contractsToClose);
            SendPartialCloseRequest(baseId, trade, position, contractsToClose);

            if (remaining <= 0 || position == null || Math.Abs(position.Quantity) < 0.0001)
            {
                StopTracking(baseId);
            }
            else if (position != null)
            {
                UpdateTrackingFromPosition(baseId, position, allowDecrease: true);
            }
        }

        private void UpdateTrackingFromPosition(string baseId, Position position, bool allowDecrease)
        {
            if (string.IsNullOrWhiteSpace(baseId) || position == null)
            {
                return;
            }

            var quantity = (int)Math.Round(Math.Abs(position.Quantity));
            if (quantity > 0)
            {
                var currentTracked = _baseIdToCurrentQuantity.TryGetValue(baseId, out var existingCurrent) ? existingCurrent : 0;

                // Quantower trade callbacks can arrive before the aggregated Position has updated. When
                // that happens `position.Quantity` may reflect the *previous* size, which would wrongly
                // downshift our hedge count. Unless we explicitly allow decreases (closing path
                // synchronisation), honour the larger of the two so multi-leg opens stay intact.
                var effectiveQuantity = (!allowDecrease && existingCurrent > 0 && quantity < existingCurrent)
                    ? existingCurrent
                    : quantity;

                _baseIdToCurrentQuantity[baseId] = effectiveQuantity;
                RememberQuantity(baseId, effectiveQuantity);
                _baseIdToInitialQuantity.AddOrUpdate(baseId, effectiveQuantity, (_, existing) => Math.Max(existing, effectiveQuantity));
                _baseIdToSide[baseId] = position.Side;
                _baseIdToPositionId[baseId] = position.Id;
            }
            else
            {
                _baseIdToCurrentQuantity.TryRemove(baseId, out _);
                _baseIdToSide.TryRemove(baseId, out _);
            }
        }

        private static int GetContractsFromTrade(Trade? trade)
        {
            if (trade == null)
            {
                return 0;
            }

            var abs = Math.Abs(trade.Quantity);
            if (abs < double.Epsilon)
            {
                return 0;
            }

            var contracts = (int)Math.Round(abs);
            return Math.Max(contracts, 1);
        }

        private void SynchronizeTrackedQuantity(string baseId)
        {
            if (string.IsNullOrWhiteSpace(baseId))
            {
                return;
            }

            var position = FindPositionByBaseId(baseId);
            if (position == null)
            {
                return;
            }
            UpdateTrackingFromPosition(baseId, position, allowDecrease: true);
        }

        private bool IsClosingTrade(Trade trade, string baseId)
        {
            // A trade is a closing trade if:
            // 1. We're tracking this position
            // 2. The trade side is OPPOSITE to the position side
            // 3. The position quantity is decreasing

            switch (trade.PositionImpactType)
            {
                case PositionImpactType.Close:
                    return true;
                case PositionImpactType.Open:
                    return false;
            }

            if (!_baseIdToSide.TryGetValue(baseId, out var positionSide))
            {
                // Not tracking this position - treat as opening trade
                return false;
            }

            if (!_baseIdToCurrentQuantity.TryGetValue(baseId, out var currentQty))
            {
                // No current quantity tracked - treat as opening trade
                return false;
            }

            // Check if trade side is opposite to position side
            bool isOpposite = (positionSide == Side.Buy && trade.Side == Side.Sell) ||
                              (positionSide == Side.Sell && trade.Side == Side.Buy);

            if (!isOpposite)
            {
                // Same side - this is adding to the position
                return false;
            }

            // Opposite side trade - this is a closing trade
            return true;
        }

        private string? ResolveBaseIdFromTrade(Trade trade)
        {
            if (trade == null)
            {
                return null;
            }

            string? candidate = Normalize(trade.PositionId);
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }

            candidate = ResolveFromPositionProperty(trade);
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }

            candidate = ResolveFromAdditionalInfo(trade);
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }

            if (!string.IsNullOrWhiteSpace(trade.Id) && _tradeIdToBaseId.TryGetValue(trade.Id, out var mappedBaseId))
            {
                return mappedBaseId;
            }

            if (!string.IsNullOrWhiteSpace(trade.OrderId) && _orderIdToBaseId.TryGetValue(trade.OrderId, out var mappedOrderBaseId))
            {
                return mappedOrderBaseId;
            }

            var accountId = GetAccountId(trade.Account);
            var symbolName = trade.Symbol?.Name;

            List<string> matches;
            lock (_trackingLock)
            {
                matches = _trackingStates
                    .Where(pair => MatchesTracking(pair.Value, accountId, symbolName))
                    .Select(pair => pair.Key)
                    .ToList();
            }

            if (matches.Count == 1)
            {
                return matches[0];
            }

            if (matches.Count > 1)
            {
                var activeMatches = matches.Where(id => _baseIdToCurrentQuantity.TryGetValue(id, out var qty) && qty > 0).ToList();
                if (activeMatches.Count == 1)
                {
                    return activeMatches[0];
                }

                if (activeMatches.Count > 0)
                {
                    matches = activeMatches;
                }

                var tradeSide = trade.Side;
                if (tradeSide == Side.Buy || tradeSide == Side.Sell)
                {
                    var opposite = matches.Where(id => _baseIdToSide.TryGetValue(id, out var positionSide) && IsOppositeSide(positionSide, tradeSide)).ToList();
                    if (opposite.Count == 1)
                    {
                        return opposite[0];
                    }

                    if (opposite.Count > 0)
                    {
                        matches = opposite;
                    }
                    else
                    {
                        var sameSide = matches.Where(id => _baseIdToSide.TryGetValue(id, out var positionSide) && positionSide == tradeSide).ToList();
                        if (sameSide.Count == 1)
                        {
                            return sameSide[0];
                        }

                        if (sameSide.Count > 0)
                        {
                            matches = sameSide;
                        }
                    }
                }
            }

            return matches.Count > 0 ? matches[0] : null;

            static bool MatchesTracking(TrackingState state, string? accountId, string? symbolName)
            {
                if (!string.IsNullOrWhiteSpace(accountId) && !string.IsNullOrWhiteSpace(state.AccountId) &&
                    !string.Equals(state.AccountId, accountId, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(symbolName) && !string.IsNullOrWhiteSpace(state.SymbolName) &&
                    !string.Equals(state.SymbolName, symbolName, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                return true;
            }

            static bool IsOppositeSide(Side positionSide, Side tradeSide)
            {
                return (positionSide == Side.Buy && tradeSide == Side.Sell) ||
                       (positionSide == Side.Sell && tradeSide == Side.Buy);
            }

            static string? Normalize(string? value)
            {
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }

            string? ResolveFromPositionProperty(Trade sourceTrade)
            {
                try
                {
                    var tradeType = sourceTrade.GetType();
                    var positionProperty = tradeType.GetProperty("Position", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    if (positionProperty == null)
                    {
                        return null;
                    }

                    var positionValue = positionProperty.GetValue(sourceTrade);
                    return ExtractPositionId(positionValue);
                }
                catch
                {
                    return null;
                }
            }

            string? ResolveFromAdditionalInfo(Trade sourceTrade)
            {
                if (sourceTrade.AdditionalInfo == null)
                {
                    return null;
                }

                if (sourceTrade.AdditionalInfo.TryGetItem("base_id", out var item) && item != null)
                {
                    var fromAdditional = item.Value as string ?? item.Value?.ToString();
                    if (!string.IsNullOrWhiteSpace(fromAdditional))
                    {
                        return fromAdditional;
                    }
                }

                if (sourceTrade.AdditionalInfo.TryGetItem("BaseId", out var altItem) && altItem != null)
                {
                    var fromAdditional = altItem.Value as string ?? altItem.Value?.ToString();
                    if (!string.IsNullOrWhiteSpace(fromAdditional))
                    {
                        return fromAdditional;
                    }
                }

                return null;
            }

            static string? ExtractPositionId(object? value)
            {
                if (value == null)
                {
                    return null;
                }

                if (value is Position qtPosition && !string.IsNullOrWhiteSpace(qtPosition.Id))
                {
                    return qtPosition.Id;
                }

                var type = value.GetType();
                var idProperty = type.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase) ??
                                 type.GetProperty("PositionId", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

                var raw = idProperty?.GetValue(value);
                var resolved = raw?.ToString();
                return string.IsNullOrWhiteSpace(resolved) ? null : resolved;
            }
        }

        private void AccumulateOpenQuantity(string baseId, Side tradeSide, int tradeQty)
        {
            if (string.IsNullOrWhiteSpace(baseId) || tradeQty <= 0)
            {
                return;
            }

            var newQuantity = _baseIdToCurrentQuantity.AddOrUpdate(baseId, tradeQty, (_, current) => current + tradeQty);
            RememberQuantity(baseId, newQuantity);

            _baseIdToInitialQuantity.AddOrUpdate(baseId, newQuantity, (_, existing) => Math.Max(existing, newQuantity));
            _baseIdToSide[baseId] = tradeSide;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void RememberQuantity(string? baseId, int quantity)
        {
            if (string.IsNullOrWhiteSpace(baseId) || quantity <= 0)
            {
                return;
            }

            _baseIdToLastKnownQuantity[baseId] = quantity;
        }

        private int ReduceTrackedQuantity(string baseId, int closedContracts)
        {
            if (string.IsNullOrWhiteSpace(baseId) || closedContracts <= 0)
            {
                return -1;
            }

            int currentQuantity;
            lock (_trackingLock)
            {
                if (!_baseIdToCurrentQuantity.TryGetValue(baseId, out currentQuantity) || currentQuantity <= 0)
                {
                    if (!_baseIdToInitialQuantity.TryGetValue(baseId, out currentQuantity) || currentQuantity <= 0)
                    {
                        currentQuantity = 0;
                    }
                }

                RememberQuantity(baseId, currentQuantity);

                var newQuantity = currentQuantity - closedContracts;
                if (newQuantity > 0)
                {
                    _baseIdToCurrentQuantity[baseId] = newQuantity;
                    RememberQuantity(baseId, newQuantity);
                    return newQuantity;
                }

                _baseIdToCurrentQuantity.TryRemove(baseId, out _);
                return 0;
            }
        }

        private void SendPartialCloseRequest(string baseId, Trade? trade, Position? position, int closedQuantity)
        {
            try
            {
                var targetPosition = position ?? FindPositionByBaseId(baseId);
                if (targetPosition == null)
                {
                    if (!TryBuildFallbackClosurePayload(trade, baseId, closedQuantity, out var fallbackJson))
                    {
                        EmitLog(QuantowerBridgeService.BridgeLogLevel.Warn, $"Cannot send partial close for {baseId} - position not found and no tracked metadata available");
                        return;
                    }

                    EmitLog(QuantowerBridgeService.BridgeLogLevel.Info, $"Sending partial close request (fallback): {baseId} closing {closedQuantity} hedge(s)");
                    _pendingContractCloseAcks[baseId] = 0;
                    _ = _bridgeService.SubmitCloseHedgeAsync(fallbackJson, baseId);
                    return;
                }

                // Build closure message with the specific quantity
                if (!Infrastructure.QuantowerTradeMapper.TryBuildPositionClosure(targetPosition, baseId, closedQuantity, out var json, out var positionId))
                {
                    EmitLog(QuantowerBridgeService.BridgeLogLevel.Warn, $"Failed to build partial closure message for {baseId}");
                    return;
                }

                EmitLog(QuantowerBridgeService.BridgeLogLevel.Info, $"Sending partial close request: {baseId} closing {closedQuantity} hedge(s)");
                // Use the CLOSE endpoint so the bridge issues targeted CLOSE_HEDGE trades
                _pendingContractCloseAcks[baseId] = 0;
                _ = _bridgeService.SubmitCloseHedgeAsync(json, baseId);
            }
            catch (Exception ex)
            {
                EmitLog(QuantowerBridgeService.BridgeLogLevel.Error, $"Error sending partial close request for {baseId}: {ex.Message}");
            }
        }

        private bool TryBuildFallbackClosurePayload(Trade? trade, string baseId, int closedQuantity, out string json)
        {
            json = string.Empty;

            if (closedQuantity <= 0)
            {
                return false;
            }

            TrackingState? state;
            lock (_trackingLock)
            {
                _trackingStates.TryGetValue(baseId, out state);
            }

            var accountName = trade?.Account?.Name;
            if (string.IsNullOrWhiteSpace(accountName))
            {
                accountName = trade?.Account?.Id;
            }
            if (string.IsNullOrWhiteSpace(accountName))
            {
                accountName = state?.AccountId;
            }

            var symbolName = trade?.Symbol?.Name;
            if (string.IsNullOrWhiteSpace(symbolName))
            {
                symbolName = state?.SymbolName;
            }

            if (string.IsNullOrWhiteSpace(symbolName) || string.IsNullOrWhiteSpace(accountName))
            {
                return false;
            }

            var action = ResolveCloseAction(trade, baseId);
            var timestamp = DateTime.UtcNow;

            var payload = new Dictionary<string, object?>
            {
                ["event_type"] = "quantower_position_closed",
                ["origin_platform"] = "quantower",
                ["closure_reason"] = "qt_position_removed",
                ["id"] = baseId,
                ["base_id"] = baseId,
                ["qt_position_id"] = state?.PositionId ?? baseId,
                ["nt_instrument_symbol"] = symbolName,
                ["instrument"] = symbolName,
                ["instrument_name"] = symbolName,
                ["nt_account_name"] = accountName,
                ["account_name"] = accountName,
                ["closed_hedge_quantity"] = (double)closedQuantity,
                ["closed_hedge_action"] = action,
                ["timestamp"] = timestamp.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                ["mt5_ticket"] = 0,
                ["qt_trade_id"] = trade?.Id ?? string.Empty
            };

            json = SimpleJson.SerializeObject(payload);
            return true;
        }

        private string ResolveCloseAction(Trade? trade, string baseId)
        {
            if (trade != null && (trade.Side == Side.Buy || trade.Side == Side.Sell))
            {
                return trade.Side == Side.Buy ? "buy" : "sell";
            }

            if (_baseIdToSide.TryGetValue(baseId, out var trackedSide))
            {
                if (trackedSide == Side.Buy)
                {
                    return "buy";
                }

                if (trackedSide == Side.Sell)
                {
                    return "sell";
                }
            }

            return "buy";
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
                    EmitLog(QuantowerBridgeService.BridgeLogLevel.Info, $"Bridge confirmed hedge close for {baseId}");
                    SynchronizeTrackedQuantity(baseId);
                    CleanupIfPositionFlat(baseId);
                    return;
                }

                if (action.Equals("MT5_CLOSE_NOTIFICATION", StringComparison.OrdinalIgnoreCase))
                {
                    HandleMt5CloseNotification(baseId, envelope);
                    return;
                }
            }

            var eventType = envelope.EventType;
            if (!string.IsNullOrWhiteSpace(eventType) && eventType.Equals("quantower_position_closed", StringComparison.OrdinalIgnoreCase))
            {
                EmitLog(QuantowerBridgeService.BridgeLogLevel.Info, $"Bridge acknowledged Quantower position closure for {baseId}");
                StopTracking(baseId);
            }
        }

        private void HandleMt5CloseNotification(string baseId, QuantowerBridgeService.BridgeStreamEnvelope envelope)
        {
            var contractBaseId = baseId;
            var positionBaseId = ResolvePositionBaseId(contractBaseId);

            EmitLog(QuantowerBridgeService.BridgeLogLevel.Info,
                $"MT5 close notification received for contract {contractBaseId} (position {positionBaseId})");

            var (isFullClose, closedQuantity, tradeResult, orderType) = ParseMt5CloseEnvelope(envelope.RawJson);

            if (orderType.Equals("NT_CLOSE_ACK", StringComparison.OrdinalIgnoreCase))
            {
                if (_pendingContractCloseAcks.AddOrUpdate(contractBaseId, _ => (byte)1, (_, __) => (byte)1) == 0)
                {
                    EmitLog(QuantowerBridgeService.BridgeLogLevel.Debug,
                        $"Recorded NT_CLOSE_ACK for {contractBaseId}; awaiting MT5 close confirmation");
                }
                return;
            }

            var wasQtInitiated = _pendingContractCloseAcks.TryRemove(contractBaseId, out _);

            var contractsFromPayload = closedQuantity > 0
                ? Math.Max(1, (int)Math.Round(Math.Abs(closedQuantity)))
                : 0;

            if (contractsFromPayload <= 0)
            {
                if (_baseIdToCurrentQuantity.TryGetValue(positionBaseId, out var currentQty) && currentQty > 0)
                {
                    contractsFromPayload = currentQty;
                }
                else if (_baseIdToInitialQuantity.TryGetValue(positionBaseId, out var initialQty) && initialQty > 0)
                {
                    contractsFromPayload = initialQty;
                }
            }

            var consumed = ConsumeContractsFromQueue(positionBaseId, contractBaseId, contractsFromPayload);
            if (consumed == 0 && contractsFromPayload > 1)
            {
                // Fallback: consume the requested number of contracts in FIFO order if MT5 did not echo the exact contract id
                consumed = ConsumeContractsFromQueue(positionBaseId, null, contractsFromPayload);
            }

            if (!string.Equals(contractBaseId, positionBaseId, StringComparison.OrdinalIgnoreCase))
            {
                _baseIdToPositionId.TryRemove(contractBaseId, out _);
            }

            var contractsClosed = consumed > 0 ? consumed : Math.Max(contractsFromPayload, 1);

            if (contractsClosed > 0)
            {
                SubtractContractCount(positionBaseId, contractsClosed);
                ReduceTrackedQuantity(positionBaseId, contractsClosed);
            }

            var position = FindPositionByBaseId(positionBaseId);

            if (position != null)
            {
                if (!wasQtInitiated)
                {
                    try
                    {
                        if (isFullClose || Math.Abs(position.Quantity) <= contractsClosed)
                        {
                            EmitLog(QuantowerBridgeService.BridgeLogLevel.Info,
                                $"Applying MT5-driven full close for position {position.Id} (contractsClosed={contractsClosed}, reason={tradeResult})");
                            _ = Task.Run(() => position.Close());
                        }
                        else if (contractsClosed > 0)
                        {
                            EmitLog(QuantowerBridgeService.BridgeLogLevel.Info,
                                $"Applying MT5-driven partial close for position {position.Id}: closing {contractsClosed} contract(s)");
                            _ = Task.Run(() => position.Close(contractsClosed));
                        }
                    }
                    catch (Exception ex)
                    {
                        EmitLog(QuantowerBridgeService.BridgeLogLevel.Error,
                            $"Failed to apply MT5 close notification for {contractBaseId}: {ex.Message}");
                    }
                }
                else
                {
                    EmitLog(QuantowerBridgeService.BridgeLogLevel.Debug,
                        $"MT5 close ack for {contractBaseId} matches Quantower-submitted close; skipping additional position.Close()");
                }
            }
            else if (!wasQtInitiated)
            {
                EmitLog(QuantowerBridgeService.BridgeLogLevel.Warn,
                    $"MT5 close notification for {contractBaseId} but Quantower position {positionBaseId} not found (tradeResult={tradeResult}, orderType={orderType})");
            }

            SynchronizeTrackedQuantity(positionBaseId);
            CleanupIfPositionFlat(positionBaseId);
        }

        private (bool isFullClose, double closedQuantity, string tradeResult, string orderType) ParseMt5CloseEnvelope(string? rawJson)
        {
            bool isFullClose = false;
            double closedQuantity = -1;
            string tradeResult = string.Empty;
            string orderType = string.Empty;
            double totalQuantity = double.NaN;

            if (!string.IsNullOrWhiteSpace(rawJson))
            {
                try
                {
                    using var json = System.Text.Json.JsonDocument.Parse(rawJson);
                    var root = json.RootElement;

                    if (root.TryGetProperty("nt_trade_result", out var tradeResultElement))
                    {
                        tradeResult = tradeResultElement.GetString() ?? string.Empty;
                    }

                    if (root.TryGetProperty("order_type", out var orderTypeElement))
                    {
                        orderType = orderTypeElement.GetString() ?? string.Empty;
                    }

                    if (root.TryGetProperty("closed_hedge_quantity", out var closedQtyElement))
                    {
                        closedQuantity = closedQtyElement.GetDouble();
                    }

                    if (root.TryGetProperty("total_quantity", out var totalQtyElement))
                    {
                        totalQuantity = totalQtyElement.GetDouble();
                    }
                }
                catch (Exception ex)
                {
                    EmitLog(QuantowerBridgeService.BridgeLogLevel.Warn, $"Failed to parse MT5 close payload: {ex.Message}");
                }
            }

            if (!string.IsNullOrWhiteSpace(tradeResult))
            {
                if (tradeResult.Contains("partial", StringComparison.OrdinalIgnoreCase))
                {
                    isFullClose = false;
                }
                else if (tradeResult.Contains("position_closed", StringComparison.OrdinalIgnoreCase) ||
                         tradeResult.Contains("already_closed", StringComparison.OrdinalIgnoreCase) ||
                         tradeResult.Contains("mt5_closed", StringComparison.OrdinalIgnoreCase) ||
                         tradeResult.Equals("success", StringComparison.OrdinalIgnoreCase))
                {
                    isFullClose = true;
                }
            }

            if (!isFullClose && !double.IsNaN(totalQuantity) && Math.Abs(totalQuantity) < double.Epsilon)
            {
                isFullClose = true;
            }

            return (isFullClose, closedQuantity, tradeResult, orderType);
        }

        private void CleanupIfPositionFlat(string baseId)
        {
            if (string.IsNullOrWhiteSpace(baseId))
            {
                return;
            }

            // If we still believe contracts remain, do not tear down tracking yet.
            if (_positionContractCounts.TryGetValue(baseId, out var queuedContracts) && queuedContracts > 0)
            {
                return;
            }

            if (_baseIdToCurrentQuantity.TryGetValue(baseId, out var currentQuantity) && currentQuantity > 0)
            {
                return;
            }

            var position = FindPositionByBaseId(baseId);
            if (position != null && Math.Abs(position.Quantity) > double.Epsilon)
            {
                return;
            }

            StopTracking(baseId);
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

            _positionOpenContracts.GetOrAdd(baseId, _ => new ConcurrentQueue<string>());

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
                // CRITICAL: Quantower can reuse Position.Id for different positions (e.g., after closing and reopening)
                // Check if we're already tracking this position, and if so, check if quantity changed
                var newQuantity = (int)Math.Abs(position.Quantity);
                var wasTracked = false;
                var hadInitialQuantity = _baseIdToInitialQuantity.TryGetValue(baseId, out var trackedQty);
                lock (_trackingLock)
                {
                    wasTracked = _trackingStates.ContainsKey(baseId);
                    if (wasTracked)
                    {
                        // Position already tracked - check if quantity changed
                        if (hadInitialQuantity && trackedQty != newQuantity)
                        {
                            // Quantity changed on an existing tracked position (partial fill/scale event).
                            // Update the current quantity but preserve the original initial quantity so the
                            // hedge tracker still knows the full contract count for close syncing.
                            _baseIdToCurrentQuantity[baseId] = newQuantity;
                            RememberQuantity(baseId, newQuantity);
                            EmitLog(QuantowerBridgeService.BridgeLogLevel.Info, $"Position {baseId} quantity changed from {trackedQty} to {newQuantity} - updating current quantity and reprocessing");

                            if (newQuantity > trackedQty)
                            {
                                _baseIdToInitialQuantity[baseId] = newQuantity;
                                RememberQuantity(baseId, newQuantity);
                                EmitLog(QuantowerBridgeService.BridgeLogLevel.Debug, $"Position {baseId} scaled in - bumped initial hedge count from {trackedQty} to {newQuantity}");
                            }
                            // Remove from tracking states to allow downstream processing to refresh trailing/elastic state
                            _trackingStates.Remove(baseId);
                        }
                        else if (!hadInitialQuantity)
                        {
                            _baseIdToInitialQuantity[baseId] = newQuantity;
                            _baseIdToCurrentQuantity[baseId] = newQuantity;
                            EmitLog(QuantowerBridgeService.BridgeLogLevel.Info, $"Position {baseId} quantity changed to {newQuantity} with no prior initial tracking - initializing state");
                            _trackingStates.Remove(baseId);
                        }
                        else
                        {
                            // Same quantity - this is a duplicate event, skip it
                            EmitLog(QuantowerBridgeService.BridgeLogLevel.Debug, $"Position {baseId} already being tracked with same quantity {newQuantity} - skipping duplicate add");
                            return;
                        }
                    }
                }

                // Maintain baseId → Position.Id mapping
                // This allows us to find Quantower positions when MT5 sends closure notifications
                if (!string.IsNullOrWhiteSpace(baseId) && !string.IsNullOrWhiteSpace(position.Id))
                {
                    _baseIdToPositionId[baseId] = position.Id;
                    EmitLog(QuantowerBridgeService.BridgeLogLevel.Debug, $"Mapped baseId {baseId} -> Position.Id {position.Id}");
                }

                // Track initial position quantity for proper hedge closure (n trades = n hedges).
                // Only update the initial quantity for genuinely new tracking scenarios; partial closes or
                // intra-position scale events should preserve the original contract count so we know how many
                // hedges were spawned from the opening leg.
                int existingInitial;
                var hasInitial = _baseIdToInitialQuantity.TryGetValue(baseId, out existingInitial);
                if (!wasTracked || !hasInitial)
                {
                    _baseIdToInitialQuantity[baseId] = newQuantity;
                }
                else if (newQuantity > existingInitial)
                {
                    _baseIdToInitialQuantity[baseId] = newQuantity;
                    EmitLog(QuantowerBridgeService.BridgeLogLevel.Debug, $"Position {baseId} initial hedge count increased from {existingInitial} to {newQuantity}");
                }

                _baseIdToCurrentQuantity[baseId] = newQuantity;
                RememberQuantity(baseId, newQuantity);
                _baseIdToSide[baseId] = position.Side;
                if (newQuantity > 0)
                {
                    _positionContractCounts[baseId] = newQuantity;
                }
                else
                {
                    _positionContractCounts.TryRemove(baseId, out _);
                }
                var initialQty = _baseIdToInitialQuantity.TryGetValue(baseId, out var initQty) ? initQty : newQuantity;
                EmitLog(QuantowerBridgeService.BridgeLogLevel.Debug, $"Stored initial quantity {initialQty}, current quantity {newQuantity}, side {position.Side} for baseId {baseId}");

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

            var positionDetails = $"Position.Id={position.Id}, Symbol={position.Symbol?.Name}, Qty={position.Quantity:F2}";
            EmitLog(QuantowerBridgeService.BridgeLogLevel.Debug, $"HandlePositionRemoved called: {positionDetails}");

            var resolvedBaseId = TryResolveTrackedBaseId(position);
            var fallbackBaseId = GetBaseId(position);
            var baseIdToUse = !string.IsNullOrWhiteSpace(resolvedBaseId) ? resolvedBaseId : fallbackBaseId;

            if (string.IsNullOrWhiteSpace(baseIdToUse))
            {
                EmitLog(QuantowerBridgeService.BridgeLogLevel.Warn,
                    $"Quantower position removed ({position.Id}) but no tracked baseId could be resolved; deferring cleanup");
                return;
            }

            var remainingContracts = (int)Math.Round(Math.Abs(position.Quantity));
            if (remainingContracts > 0)
            {
                EmitLog(QuantowerBridgeService.BridgeLogLevel.Warn,
                    $"Quantower position removal for {baseIdToUse} still reports {remainingContracts} contract(s); retaining hedge tracking until closes settle");
                _baseIdToCurrentQuantity[baseIdToUse] = remainingContracts;
                RememberQuantity(baseIdToUse, remainingContracts);
            }
            else
            {
                EmitLog(QuantowerBridgeService.BridgeLogLevel.Debug,
                    $"Quantower position removal for {baseIdToUse} indicates flat position; waiting for bridge confirmation before cleanup");
                RememberQuantity(baseIdToUse, 0);
                _baseIdToCurrentQuantity.TryRemove(baseIdToUse, out _);
            }

            if (!string.IsNullOrWhiteSpace(position.Id))
            {
                _baseIdToPositionId[baseIdToUse] = position.Id;
            }

            // Do not call StopTracking here; MT5/bridge notifications will trigger CleanupIfPositionFlat once hedge closures finish.
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

            // Clean up quantity tracking (initial and current)
            _baseIdToInitialQuantity.TryRemove(baseId, out _);
            _baseIdToCurrentQuantity.TryRemove(baseId, out _);
            _baseIdToSide.TryRemove(baseId, out _);
            _baseIdToLastKnownQuantity.TryRemove(baseId, out _);
            _positionContractCounts.TryRemove(baseId, out _);
            _positionOpenContracts.TryRemove(baseId, out _);
            RemoveTradeOrderMappings(baseId);
            RemovePendingCloseEntries(baseId);
            RemoveContractBaseMappings(baseId);

            _trailingService.RemoveTracker(baseId);
        }


        private void RemoveTradeOrderMappings(string baseId)
        {
            if (string.IsNullOrWhiteSpace(baseId))
            {
                return;
            }

            foreach (var pair in _tradeIdToBaseId.ToArray())
            {
                if (string.Equals(pair.Value, baseId, StringComparison.OrdinalIgnoreCase))
                {
                    _tradeIdToBaseId.TryRemove(pair.Key, out _);
                    _processedTradeIds.TryRemove(pair.Key, out _);
                }
            }

            foreach (var pair in _orderIdToBaseId.ToArray())
            {
                if (string.Equals(pair.Value, baseId, StringComparison.OrdinalIgnoreCase))
                {
                    _orderIdToBaseId.TryRemove(pair.Key, out _);
                }
            }
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

            // Prefer the actively tracked quantity; this reflects the number of hedges that remain open.
            if (_baseIdToCurrentQuantity.TryGetValue(baseId, out var current) && current > 0)
            {
                return current;
            }

            if (_baseIdToInitialQuantity.TryGetValue(baseId, out var initial))
            {
                return initial;
            }

            if (_baseIdToLastKnownQuantity.TryGetValue(baseId, out var lastKnown) && lastKnown > 0)
            {
                return lastKnown;
            }

            return null;
        }
    }
}
