using System;
using System.ComponentModel;
using TradingPlatform.BusinessLayer;

namespace Quantower.MultiStrat
{
    public sealed class AccountSubscription : INotifyPropertyChanged
    {
        private Account? _account;
        private bool _isEnabled;

        public AccountSubscription(Account account, bool isEnabled = true)
        {
            _account = account ?? throw new ArgumentNullException(nameof(account));
            AccountId = SafeAccountIdentifier(account);
            DisplayName = ResolveDisplayName(account);
            _isEnabled = isEnabled;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string AccountId { get; }

        public string DisplayName { get; private set; }

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled != value)
                {
                    _isEnabled = value;
                    OnPropertyChanged(nameof(IsEnabled));
                }
            }
        }

        public Account? Account => _account;

        public bool Matches(Account? candidate)
        {
            if (candidate == null)
            {
                return false;
            }

            var candidateKey = SafeAccountIdentifier(candidate);
            if (!string.IsNullOrEmpty(candidateKey) && !string.IsNullOrEmpty(AccountId))
            {
                return string.Equals(candidateKey, AccountId, StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(candidate?.Name, _account?.Name, StringComparison.OrdinalIgnoreCase);
        }

        public void Update(Account account)
        {
            _account = account;
            DisplayName = ResolveDisplayName(account);
            OnPropertyChanged(nameof(DisplayName));
        }

        private static string SafeAccountIdentifier(Account account)
        {
            if (!string.IsNullOrWhiteSpace(account.Id))
            {
                return account.Id;
            }

            return account.Name ?? string.Empty;
        }

        private static string ResolveDisplayName(Account account)
        {
            if (!string.IsNullOrWhiteSpace(account.Name))
            {
                return account.Name;
            }

            if (!string.IsNullOrWhiteSpace(account.Id))
            {
                return account.Id;
            }

            return "Account";
        }

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
