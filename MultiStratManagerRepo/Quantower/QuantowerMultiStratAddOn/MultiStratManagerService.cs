using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
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
        private readonly HashSet<string> _savedAccountIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly Services.TrailingElasticService _trailingService = new();
        private readonly Services.SltpRemovalService _sltpService = new();
        private readonly Dictionary<string, TrackingState> _trackingStates = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _trackingLock = new();
        private readonly TimeSpan _trackingInterval = TimeSpan.FromSeconds(2);
        private bool _disposed;

        public MultiStratManagerService()
        {
            _bridgeService = new QuantowerBridgeService();
            _bridgeService.Log += entry => Log?.Invoke(entry);
            _bridgeService.TradeReceived += HandleTrade;
            _bridgeService.PositionAdded += HandlePositionAdded;
            _bridgeService.PositionRemoved += HandlePositionRemoved;
            _accountsView = new ReadOnlyObservableCollection<AccountSubscription>(_accounts);
            LoadSettings();
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
            if (_disposed)
            {
                return;
            }

            _disposed = true;
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
            }
            catch (Exception ex)
            {
                EmitLog(QuantowerBridgeService.BridgeLogLevel.Warn, $"Failed to load manager settings: {ex.Message}");
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
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(MultiStratManagerService));
            }
        }
    }
}
