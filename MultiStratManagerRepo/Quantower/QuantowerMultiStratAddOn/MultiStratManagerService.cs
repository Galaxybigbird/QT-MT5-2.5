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
        private readonly ReadOnlyObservableCollection<AccountSubscription> _accountsView;
        private readonly SettingsRepository _settingsRepository = new();
        private readonly RiskConfiguration _riskSettings = new();
        private readonly HashSet<string> _savedAccountIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly Services.TrailingElasticService _trailingService = new();
        private readonly Services.SltpRemovalService _sltpService = new();
        private readonly Dictionary<string, TrackingState> _trackingStates = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _trackingLock = new();
        private readonly TimeSpan _trackingInterval = TimeSpan.FromSeconds(2);
        private readonly object _riskLock = new();
        private int _disposed; // 0 = active, 1 = disposed
        private Timer? _riskTimer;
        public TimeSpan RiskTimerInterval { get; set; } = TimeSpan.FromSeconds(5);

        public MultiStratManagerService()
        {
            _bridgeService = new QuantowerBridgeService();
            _bridgeService.Log += entry => Log?.Invoke(entry);
            _bridgeService.TradeReceived += HandleTrade;
            _bridgeService.PositionAdded += HandlePositionAdded;
            _bridgeService.PositionRemoved += HandlePositionRemoved;
            _accountsView = new ReadOnlyObservableCollection<AccountSubscription>(_accounts);
            LoadSettings();
            StartRiskTimer();
        }

        public event Action<QuantowerBridgeService.BridgeLogEntry>? Log;
        public event PropertyChangedEventHandler? PropertyChanged;

        public ReadOnlyObservableCollection<AccountSubscription> Accounts => _accountsView;

        public bool IsConnected => _bridgeService.IsRunning;

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

        public async Task DisconnectAsync()
        {
            ThrowIfDisposed();

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
                SaveSettings();
            }
        }

        public void ResetDailyRisk(string? accountId)
        {
            ThrowIfDisposed();

            lock (_riskLock)
            {
                if (string.IsNullOrWhiteSpace(accountId))
                {
                    foreach (var kvp in _riskSettings.Accounts)
                    {
                        var account = FindAccountById(kvp.Key);
                        if (account != null)
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
                    var account = FindAccountById(accountId);
                    var state = GetOrCreateRiskState(accountId, account);
                    state.BalanceBaseline = account?.Balance ?? state.BalanceBaseline;
                    state.LimitTriggered = false;
                    state.LastKnownPnL = 0;
                    state.LastTriggerUtc = DateTime.MinValue;
                }

                SaveSettings();
            }
        }

        public Task<bool> FlattenAccountAsync(string accountId, bool disableAfter, string reason = "manual")
        {
            ThrowIfDisposed();

            var subscription = _accounts.FirstOrDefault(s => string.Equals(s.AccountId, accountId, StringComparison.OrdinalIgnoreCase));
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
            foreach (var subscription in _accounts)
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
                _trailingService.TrailingActivationUnits,
                _trailingService.TrailingActivationValue,
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
            _trailingService.TrailingActivationUnits = update.TrailingActivationUnits;
            _trailingService.TrailingActivationValue = update.TrailingActivationValue;
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

                    var existing = _accounts.FirstOrDefault(a => a.Matches(account));
                    if (existing == null)
                    {
                        var enable = _savedAccountIds.Count == 0 || _savedAccountIds.Contains(identifier);
                        var subscription = new AccountSubscription(account, enable);
                        AttachSubscription(subscription);
                        _accounts.Add(subscription);

                        if (enable)
                        {
                            RefreshAccountPositions(subscription.AccountId);
                        }
                    }
                    else
                    {
                        existing.Update(account);
                        AttachSubscription(existing);
                    }
                }

                for (var i = _accounts.Count - 1; i >= 0; i--)
                {
                    var account = _accounts[i];
                    if (account.Account == null)
                    {
                        DetachSubscription(account);
                        StopTrackingByAccount(account.AccountId);
                        _accounts.RemoveAt(i);
                        continue;
                    }

                    var candidate = account.Account.Id ?? account.Account.Name ?? string.Empty;
                    if (!seen.Contains(candidate))
                    {
                        DetachSubscription(account);
                        StopTrackingByAccount(account.AccountId);
                        _accounts.RemoveAt(i);
                    }
                }
            }
            catch (Exception ex)
            {
                EmitLog(QuantowerBridgeService.BridgeLogLevel.Warn, $"Failed to refresh Quantower accounts: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            SaveSettings();
            try
            {
                StopAllTracking();
                foreach (var subscription in _accounts)
                {
                    DetachSubscription(subscription);
                }

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

        private void EmitLog(QuantowerBridgeService.BridgeLogLevel level, string message)
        {
            Log?.Invoke(new QuantowerBridgeService.BridgeLogEntry(DateTime.UtcNow, level, message, null, null, null));
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
                    _savedAccountIds.Clear();
                    foreach (var id in ids)
                    {
                        if (id is string key && !string.IsNullOrWhiteSpace(key))
                        {
                            _savedAccountIds.Add(key);
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

                    if (json.TryGetProperty("trailing_activation_units", out var activationUnits) && activationUnits.ValueKind == JsonValueKind.String && Enum.TryParse(activationUnits.GetString(), true, out Services.TrailingElasticService.ProfitUnitType activationType))
                    {
                        _trailingService.TrailingActivationUnits = activationType;
                    }

                    if (json.TryGetProperty("trailing_activation_value", out var activationValue) && activationValue.TryGetDouble(out var actVal))
                    {
                        _trailingService.TrailingActivationValue = actVal;
                    }

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

        private void SaveSettings()
        {
            try
            {
                var enabled = new List<string>();
                foreach (var account in _accounts)
                {
                    if (account.IsEnabled && !string.IsNullOrEmpty(account.AccountId))
                    {
                        enabled.Add(account.AccountId);
                    }
                }

                _savedAccountIds.Clear();
                foreach (var id in enabled)
                {
                    _savedAccountIds.Add(id);
                }

                var payload = new Dictionary<string, object?>
                {
                    ["enabled_accounts"] = enabled.ToArray()
                };

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

                    payload["risk"] = new Dictionary<string, object?>
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

                payload["trailing"] = new Dictionary<string, object?>
                {
                    ["enable_elastic"] = _trailingService.EnableElasticHedging,
                    ["elastic_trigger_units"] = _trailingService.ElasticTriggerUnits.ToString(),
                    ["profit_update_threshold"] = _trailingService.ProfitUpdateThreshold,
                    ["elastic_increment_units"] = _trailingService.ElasticIncrementUnits.ToString(),
                    ["elastic_increment_value"] = _trailingService.ElasticIncrementValue,
                    ["enable_trailing"] = _trailingService.EnableTrailing,
                    ["trailing_activation_units"] = _trailingService.TrailingActivationUnits.ToString(),
                    ["trailing_activation_value"] = _trailingService.TrailingActivationValue,
                    ["trailing_stop_units"] = _trailingService.TrailingStopUnits.ToString(),
                    ["trailing_stop_value"] = _trailingService.TrailingStopValue,
                    ["dema_atr_multiplier"] = _trailingService.DemaAtrMultiplier,
                    ["atr_period"] = _trailingService.AtrPeriod,
                    ["dema_period"] = _trailingService.DemaPeriod
                };

                _settingsRepository.Save(payload);
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
            public string? UniqueId { get; set; }
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
            Services.TrailingElasticService.ProfitUnitType TrailingActivationUnits,
            double TrailingActivationValue,
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
            Services.TrailingElasticService.ProfitUnitType TrailingActivationUnits,
            double TrailingActivationValue,
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

        private void HandlePositionAdded(Position position)
        {
            if (position == null)
            {
                return;
            }

            var baseId = GetBaseId(position);

            if (!IsAccountEnabled(position))
            {
                StopTracking(baseId);
                _trailingService.RemoveTracker(baseId);
                return;
            }

            _trailingService.RegisterPosition(baseId, position);
            SendElasticAndTrailing(position, baseId);
            StartTracking(position, baseId);
        }

        private void HandlePositionRemoved(Position position)
        {
            if (position == null)
            {
                return;
            }

            StopTracking(GetBaseId(position));
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
                    _ = _bridgeService.SubmitElasticUpdateAsync(elasticJson, baseId);
                }

                var trailingPayload = _trailingService.TryBuildTrailingUpdate(baseId, position);
                if (trailingPayload != null)
                {
                    var trailingJson = SimpleJson.SerializeObject(trailingPayload);
                    _ = _bridgeService.SubmitTrailingUpdateAsync(trailingJson, baseId);
                }
            }
            catch (Exception ex)
            {
                EmitLog(QuantowerBridgeService.BridgeLogLevel.Warn, $"Failed to push trailing update: {ex.Message}");
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
                    existing.UniqueId = position.UniqueId ?? existing.UniqueId;
                    existing.AccountId = GetAccountId(position.Account);
                    existing.SymbolName = position.Symbol?.Name;
                    return;
                }

                var state = new TrackingState
                {
                    BaseId = baseId,
                    PositionId = position.Id,
                    UniqueId = position.UniqueId,
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
                EvaluateDailyReset();

                foreach (var subscription in _accounts)
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

        private void EvaluateDailyReset()
        {
            lock (_riskLock)
            {
                var today = DateTime.UtcNow.Date;
                if (today <= _riskSettings.LastResetDateUtc.Date)
                {
                    return;
                }

                foreach (var kvp in _riskSettings.Accounts)
                {
                    var account = FindAccountById(kvp.Key);
                    if (account != null)
                    {
                        kvp.Value.BalanceBaseline = account.Balance;
                    }

                    kvp.Value.LimitTriggered = false;
                    kvp.Value.LastKnownPnL = 0;
                    kvp.Value.LastTriggerUtc = DateTime.MinValue;
                }

                _riskSettings.LastResetDateUtc = today;
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

        private bool FlattenAccountInternal(AccountSubscription subscription, string reason, bool disableAfter)
        {
            var accountId = subscription.AccountId;
            var positions = EnumeratePositions(accountId).ToList();
            var success = true;

            if (positions.Count == 0)
            {
                EmitLog(QuantowerBridgeService.BridgeLogLevel.Info, $"No open positions to flatten for account {subscription.DisplayName}");
            }

            foreach (var position in positions)
            {
                try
                {
                    position.Close();
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

                if (!string.IsNullOrEmpty(state.UniqueId) &&
                    string.Equals(position.UniqueId, state.UniqueId, StringComparison.OrdinalIgnoreCase))
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

        private Account? FindAccountById(string accountId)
        {
            foreach (var subscription in _accounts)
            {
                if (string.Equals(subscription.AccountId, accountId, StringComparison.OrdinalIgnoreCase))
                {
                    return subscription.Account;
                }
            }

            return null;
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

            foreach (var subscription in _accounts)
            {
                if (string.Equals(subscription.AccountId, accountId, StringComparison.OrdinalIgnoreCase))
                {
                    return subscription.IsEnabled;
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
            if (!string.IsNullOrWhiteSpace(position.Id))
            {
                return position.Id;
            }

            if (!string.IsNullOrWhiteSpace(position.UniqueId))
            {
                return position.UniqueId;
            }

            var accountId = GetAccountId(position.Account) ?? "account";
            var symbolName = position.Symbol?.Name ?? "symbol";
            var openTicks = position.OpenTime == default ? DateTime.UtcNow.Ticks : position.OpenTime.Ticks;
            return $"{accountId}:{symbolName}:{openTicks}";
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(MultiStratManagerService));
            }
        }
    }
}
