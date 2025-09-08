#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Globalization; // For number formatting
using System.Linq;
using System.Windows.Threading;
using NinjaTrader.Cbi; // For Account
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core;
using System.Threading;
using System.Diagnostics;
using NinjaTrader.Data;
using System.Text;
using System.Threading.Tasks;
// HttpListener removed - using gRPC instead
using System.IO; // Required for StreamReader
using System.Collections.Concurrent; // Added for ConcurrentDictionary
using NinjaTrader.NinjaScript.AddOns.MultiStratManagerLogic; // Added for SLTPRemovalLogic
using NTGrpcClient; // Added for gRPC client
#endregion

namespace NinjaTrader.NinjaScript.AddOns
{
    // Enums for trailing configuration
    public enum TrailingActivationType
    {
        Ticks,
        Pips,
        Dollars,
        Percent
    }
    
    /// <summary>
    /// Multi-Strategy Manager for hedging and managing multiple trading strategies
    /// </summary>
    public class MultiStratManager : NinjaTrader.NinjaScript.AddOnBase, INotifyPropertyChanged
    {
    // Type aliases for UI compatibility - expose types from TrailingAndElasticManager
    public class ElasticPositionTracker : NinjaTrader.NinjaScript.AddOns.ElasticPositionTracker { }
        private static UIForManager window;
        private bool isFirstRun = true;
        private bool connectionsStarted = false;
        private System.Windows.Threading.DispatcherTimer autoLaunchTimer;

        // ✅ RECOMPILATION SAFETY: Track if we've already cleaned up to prevent multiple cleanup attempts
        private static bool hasPerformedStaticCleanup = false;

        /// <summary>
        /// Aggressive cleanup of static resources to handle NinjaScript recompilation scenarios
        /// </summary>
        private static void PerformStaticCleanup()
        {
            try
            {
                System.Console.WriteLine("[NT_ADDON][DEBUG] PerformStaticCleanup: Starting aggressive cleanup for recompilation safety");

                // 1. Close and dispose existing UI window
                if (window != null)
                {
                    try
                    {
                        System.Console.WriteLine("[NT_ADDON][DEBUG] PerformStaticCleanup: Closing existing UI window");
                        if (window.Dispatcher.CheckAccess())
                        {
                            window.Close();
                        }
                        else
                        {
                            window.Dispatcher.BeginInvoke(new Action(() => window.Close()));
                        }
                        window = null;
                    }
                    catch (Exception ex)
                    {
                        System.Console.WriteLine($"[NT_ADDON][ERROR] PerformStaticCleanup: Error closing window: {ex.Message}");
                    }
                }

                // 2. Clear monitored strategies list
                if (monitoredStrategies != null)
                {
                    System.Console.WriteLine($"[NT_ADDON][DEBUG] PerformStaticCleanup: Clearing {monitoredStrategies.Count} monitored strategies");
                    monitoredStrategies.Clear();
                }

                // 3. Clean up previous instance
                if (Instance != null)
                {
                    try
                    {
                        System.Console.WriteLine("[NT_ADDON][DEBUG] PerformStaticCleanup: Disposing previous instance");
                        Instance.DisconnectGrpcAndStopAll();
                        Instance = null;
                    }
                    catch (Exception ex)
                    {
                        System.Console.WriteLine($"[NT_ADDON][ERROR] PerformStaticCleanup: Error disposing instance: {ex.Message}");
                    }
                }

                // 4. Force garbage collection to clean up resources
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                hasPerformedStaticCleanup = true;
                System.Console.WriteLine("[NT_ADDON][DEBUG] PerformStaticCleanup: Cleanup completed successfully");
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[NT_ADDON][ERROR] PerformStaticCleanup: Critical error during cleanup: {ex.Message}");
            }
        }

        // Bridge connection monitoring removed - manual connection only
        private bool lastBridgeConnectionStatus = false;
        private DateTime lastBridgeConnectionCheck = DateTime.MinValue;

        public static MultiStratManager Instance { get; private set; }
        public event Action PingReceivedFromBridge;

        private SLTPRemovalLogic sltpRemovalLogic;

        // Properties for SLTP Removal Logic
        public bool EnableSLTPRemoval { get; set; } = true; // Default to true
        public int SLTPRemovalDelaySeconds { get; set; } = 3; // Default to 3 seconds

        #region Trailing and Elastic Properties - Delegated to TrailingAndElasticManager
        
        // All trailing and elastic settings are now handled by TrailingAndElasticManager
        // Expose them through properties for UI compatibility
        public bool EnableElasticHedging 
        { 
            get => trailingAndElasticManager?.EnableElasticHedging ?? true; 
            set { if (trailingAndElasticManager != null) { trailingAndElasticManager.EnableElasticHedging = value; OnPropertyChanged(nameof(EnableElasticHedging)); } } 
        }
        
        public bool EnableTrailing 
        { 
            get => trailingAndElasticManager?.EnableTrailing ?? true; 
            set { if (trailingAndElasticManager != null) { trailingAndElasticManager.EnableTrailing = value; OnPropertyChanged(nameof(EnableTrailing)); } } 
        }
        
        public bool UseAlternativeTrailing 
        { 
            get => trailingAndElasticManager?.UseAlternativeTrailing ?? true; 
            set { if (trailingAndElasticManager != null) { trailingAndElasticManager.UseAlternativeTrailing = value; OnPropertyChanged(nameof(UseAlternativeTrailing)); } } 
        }
        
        public bool UseTraditionalTrailing 
        { 
            get => trailingAndElasticManager?.UseTraditionalTrailing ?? false; 
            set { if (trailingAndElasticManager != null) { trailingAndElasticManager.UseTraditionalTrailing = value; OnPropertyChanged(nameof(UseTraditionalTrailing)); } } 
        }
        
        public TrailingActivationType TrailingTriggerType 
        { 
            get => trailingAndElasticManager?.TrailingTriggerType ?? TrailingActivationType.Dollars; 
            set 
            { 
                if (trailingAndElasticManager != null) 
                { 
                    trailingAndElasticManager.TrailingTriggerType = value; 
                    // Mirror to Elastic trigger for unified control
                    trailingAndElasticManager.ElasticTriggerType = value; 
                    OnPropertyChanged(nameof(TrailingTriggerType)); 
                    OnPropertyChanged(nameof(ElasticTriggerType)); 
                } 
            } 
        }
        
        public double TrailingTriggerValue 
        { 
            get => trailingAndElasticManager?.TrailingTriggerValue ?? 100.0; 
            set 
            { 
                if (trailingAndElasticManager != null) 
                { 
                    trailingAndElasticManager.TrailingTriggerValue = value; 
                    // Mirror to Elastic threshold
                    trailingAndElasticManager.ProfitUpdateThreshold = value; 
                    OnPropertyChanged(nameof(TrailingTriggerValue)); 
                    OnPropertyChanged(nameof(ProfitUpdateThreshold)); 
                } 
            } 
        }
        
        public TrailingActivationType TrailingStopType 
        { 
            get => trailingAndElasticManager?.TrailingStopType ?? TrailingActivationType.Dollars; 
            set { if (trailingAndElasticManager != null) { trailingAndElasticManager.TrailingStopType = value; OnPropertyChanged(nameof(TrailingStopType)); } } 
        }
        
        public double TrailingStopValue 
        { 
            get => trailingAndElasticManager?.TrailingStopValue ?? 50.0; 
            set { if (trailingAndElasticManager != null) { trailingAndElasticManager.TrailingStopValue = value; OnPropertyChanged(nameof(TrailingStopValue)); } } 
        }
        
        public TrailingActivationType TrailingIncrementsType 
        { 
            get => trailingAndElasticManager?.TrailingIncrementsType ?? TrailingActivationType.Dollars; 
            set 
            { 
                if (trailingAndElasticManager != null) 
                { 
                    trailingAndElasticManager.TrailingIncrementsType = value; 
                    // Mirror to Elastic increments units for unified control
                    trailingAndElasticManager.ElasticProfitUnits = value; 
                    OnPropertyChanged(nameof(TrailingIncrementsType)); 
                    OnPropertyChanged(nameof(ElasticProfitUnits)); 
                } 
            } 
        }
        
        public double TrailingIncrementsValue 
        { 
            get => trailingAndElasticManager?.TrailingIncrementsValue ?? 10.0; 
            set 
            { 
                if (trailingAndElasticManager != null) 
                { 
                    trailingAndElasticManager.TrailingIncrementsValue = value; 
                    // Mirror to Elastic increments value for unified control
                    trailingAndElasticManager.ElasticIncrementValue = value; 
                    OnPropertyChanged(nameof(TrailingIncrementsValue)); 
                    OnPropertyChanged(nameof(ElasticIncrementValue)); 
                } 
            } 
        }
        
        // Missing properties that UI expects
        public TrailingActivationType TrailingActivationMode 
        { 
            get => trailingAndElasticManager?.TrailingActivationMode ?? TrailingActivationType.Percent; 
            set { if (trailingAndElasticManager != null) { trailingAndElasticManager.TrailingActivationMode = value; OnPropertyChanged(nameof(TrailingActivationMode)); } } 
        }
        
        public double TrailingActivationValue 
        { 
            get => trailingAndElasticManager?.TrailingActivationValue ?? 1.0; 
            set { if (trailingAndElasticManager != null) { trailingAndElasticManager.TrailingActivationValue = value; OnPropertyChanged(nameof(TrailingActivationValue)); } } 
        }
        
        // Elastic hedging properties (actively used by Elastic monitor and Alternative Trailing activation)
        // Keep these in sync with the unified UI fields as well as the trailing equivalents.
        public TrailingActivationType ElasticTriggerType 
        { 
            get => trailingAndElasticManager?.ElasticTriggerType ?? TrailingActivationType.Dollars; 
            set 
            { 
                if (trailingAndElasticManager != null) 
                { 
                    // Set Elastic trigger and mirror to Trailing trigger to keep the unified semantics
                    trailingAndElasticManager.ElasticTriggerType = value; 
                    trailingAndElasticManager.TrailingTriggerType = value; 
                    OnPropertyChanged(nameof(ElasticTriggerType)); 
                    OnPropertyChanged(nameof(TrailingTriggerType)); 
                } 
            } 
        }
        public double ProfitUpdateThreshold 
        { 
            get => trailingAndElasticManager?.ProfitUpdateThreshold ?? 50.0; 
            set 
            { 
                if (trailingAndElasticManager != null) 
                { 
                    // Set Elastic threshold and mirror to Trailing trigger value
                    trailingAndElasticManager.ProfitUpdateThreshold = value; 
                    trailingAndElasticManager.TrailingTriggerValue = value; 
                    OnPropertyChanged(nameof(ProfitUpdateThreshold)); 
                    OnPropertyChanged(nameof(TrailingTriggerValue)); 
                } 
            } 
        }
        
        // UI compatibility: this value is no longer used at runtime (timer fixed to 100ms),
        // but we keep a local setting so existing UI bindings don't break.
    // Legacy UI-only knob; no longer used (monitor fixed at 100ms). Kept for binding compatibility.
    // private int _elasticUpdateIntervalSecondsCompat = 1;
    // public int ElasticUpdateIntervalSeconds 
    // {
    //     get => _elasticUpdateIntervalSecondsCompat; 
    //     set { _elasticUpdateIntervalSecondsCompat = value; OnPropertyChanged(nameof(ElasticUpdateIntervalSeconds)); } 
    // }
        public TrailingActivationType ElasticProfitUnits
        {
            get => trailingAndElasticManager?.ElasticProfitUnits ?? TrailingActivationType.Dollars;
            set
            {
                if (trailingAndElasticManager != null)
                {
                    // Set Elastic increments units and mirror to Trailing increments units
                    trailingAndElasticManager.ElasticProfitUnits = value;
                    trailingAndElasticManager.TrailingIncrementsType = value;
                    OnPropertyChanged(nameof(ElasticProfitUnits));
                    OnPropertyChanged(nameof(TrailingIncrementsType));
                }
            }
        }
        public double ElasticIncrementValue
        {
            get => trailingAndElasticManager?.ElasticIncrementValue ?? 10.0;
            set
            {
                if (trailingAndElasticManager != null)
                {
                    // Set Elastic increments value and mirror to Trailing increments value
                    trailingAndElasticManager.ElasticIncrementValue = value;
                    trailingAndElasticManager.TrailingIncrementsValue = value;
                    OnPropertyChanged(nameof(ElasticIncrementValue));
                    OnPropertyChanged(nameof(TrailingIncrementsValue));
                }
            }
        }
        
        // Legacy trailing properties that UI still binds to
        public bool EnableTrailingStop 
        { 
            get => trailingAndElasticManager?.EnableTrailingStop ?? false; 
            set { if (trailingAndElasticManager != null) { trailingAndElasticManager.EnableTrailingStop = value; OnPropertyChanged(nameof(EnableTrailingStop)); } } 
        }
        
        public int ActivateTrailAfterPipsProfit 
        { 
            get => trailingAndElasticManager?.ActivateTrailAfterPipsProfit ?? 20; 
            set { if (trailingAndElasticManager != null) { trailingAndElasticManager.ActivateTrailAfterPipsProfit = value; OnPropertyChanged(nameof(ActivateTrailAfterPipsProfit)); } } 
        }
        
        public double DollarTrailDistance 
        { 
            get => trailingAndElasticManager?.DollarTrailDistance ?? 100.0; 
            set { if (trailingAndElasticManager != null) { trailingAndElasticManager.DollarTrailDistance = value; OnPropertyChanged(nameof(DollarTrailDistance)); } } 
        }
        
        public int AtrPeriod 
        { 
            get => trailingAndElasticManager?.AtrPeriod ?? 14; 
            set { if (trailingAndElasticManager != null) { trailingAndElasticManager.AtrPeriod = value; OnPropertyChanged(nameof(AtrPeriod)); } } 
        }
        
        public double AtrMultiplier 
        { 
            get => trailingAndElasticManager?.AtrMultiplier ?? 2.5; 
            set { if (trailingAndElasticManager != null) { trailingAndElasticManager.AtrMultiplier = value; OnPropertyChanged(nameof(AtrMultiplier)); } } 
        }
        
        public bool UseATRTrailing 
        { 
            get => trailingAndElasticManager?.UseATRTrailing ?? false; 
            set { if (trailingAndElasticManager != null) { trailingAndElasticManager.UseATRTrailing = value; OnPropertyChanged(nameof(UseATRTrailing)); } } 
        }
        
        public int DEMA_ATR_Period 
        { 
            get => trailingAndElasticManager?.DEMA_ATR_Period ?? 14; 
            set { if (trailingAndElasticManager != null) { trailingAndElasticManager.DEMA_ATR_Period = value; OnPropertyChanged(nameof(DEMA_ATR_Period)); } } 
        }
        
        public double DEMA_ATR_Multiplier 
        { 
            get => trailingAndElasticManager?.DEMA_ATR_Multiplier ?? 1.5; 
            set { if (trailingAndElasticManager != null) { trailingAndElasticManager.DEMA_ATR_Multiplier = value; OnPropertyChanged(nameof(DEMA_ATR_Multiplier)); } } 
        }

        // Expose unified trailing order semantics to UI (local backing fields; TrailingAndElasticManager does not define these)
        private bool _useLimitOrdersForStops = true; // default hedge-style LIMIT
        public bool UseLimitOrdersForStops
        {
            get => _useLimitOrdersForStops;
            set { if (_useLimitOrdersForStops != value) { _useLimitOrdersForStops = value; OnPropertyChanged(nameof(UseLimitOrdersForStops)); } }
        }

        private bool _useStopMarketOnActivation = true;
        public bool UseStopMarketOnActivation
        {
            get => _useStopMarketOnActivation;
            set { if (_useStopMarketOnActivation != value) { _useStopMarketOnActivation = value; OnPropertyChanged(nameof(UseStopMarketOnActivation)); } }
        }

        private int _placementMinTicksBuffer = 6;
        public int PlacementMinTicksBuffer
        {
            get => _placementMinTicksBuffer;
            set { if (_placementMinTicksBuffer != value) { _placementMinTicksBuffer = value; OnPropertyChanged(nameof(PlacementMinTicksBuffer)); } }
        }

        private int _trailingTimeBufferMs = 15;
        public int TrailingTimeBufferMs
        {
            get => _trailingTimeBufferMs;
            set { if (_trailingTimeBufferMs != value) { _trailingTimeBufferMs = value; OnPropertyChanged(nameof(TrailingTimeBufferMs)); } }
        }
        
    // Internal trailing has been removed. No InternalStops exposed.
        
        // Elastic positions access for UI
        public Dictionary<string, NinjaTrader.NinjaScript.AddOns.ElasticPositionTracker> ElasticPositions
        {
            get { return trailingAndElasticManager?.ElasticPositions ?? new Dictionary<string, NinjaTrader.NinjaScript.AddOns.ElasticPositionTracker>(); }
        }
        
        // Traditional trailing stops access for UI
        public Dictionary<string, NinjaTrader.NinjaScript.AddOns.TraditionalTrailingStop> TraditionalTrailingStops
        {
            get { return trailingAndElasticManager?.TraditionalTrailingStops ?? new Dictionary<string, NinjaTrader.NinjaScript.AddOns.TraditionalTrailingStop>(); }
        }
        
        #endregion
        
        

        #region PnL Properties and INotifyPropertyChanged

        private double _realizedPnL;
        public double RealizedPnL
        {
            get { return _realizedPnL; }
            private set
            {
                if (_realizedPnL != value)
                {
                    _realizedPnL = value;
                    OnPropertyChanged(nameof(RealizedPnL));
                    UpdateTotalPnL();
                }
            }
        }

        private double _unrealizedPnL;
        public double UnrealizedPnL
        {
            get { return _unrealizedPnL; }
            private set
            {
                if (_unrealizedPnL != value)
                {
                    _unrealizedPnL = value;
                    OnPropertyChanged(nameof(UnrealizedPnL));
                    UpdateTotalPnL();
                }
            }
        }

        private double _totalPnL;
        public double TotalPnL
        {
            get { return _totalPnL; }
            private set
            {
                if (_totalPnL != value)
                {
                    _totalPnL = value;
                    OnPropertyChanged(nameof(TotalPnL));
                }
            }
        }

        private void UpdateTotalPnL()
        {
            TotalPnL = RealizedPnL + UnrealizedPnL;
        }

        // NT Performance Tracking for Elastic Hedging
        private double _sessionStartBalance = 0.0;
        private double _dailyStartPnL = 0.0;
        private DateTime _sessionStartTime = DateTime.MinValue;
        private int _sessionTradeCount = 0;
        private string _lastTradeResult = "";
        private double _lastTradePnL = 0.0;

        public double SessionStartBalance
        {
            get { return _sessionStartBalance; }
            private set { _sessionStartBalance = value; }
        }

        public double DailyPnL
        {
            get { return TotalPnL - _dailyStartPnL; }
        }

        public int SessionTradeCount
        {
            get { return _sessionTradeCount; }
            private set { _sessionTradeCount = value; }
        }

        public string LastTradeResult
        {
            get { return _lastTradeResult; }
            private set { _lastTradeResult = value; }
        }

        // Initialize session tracking (call when addon starts or new day begins)
        private void InitializeSessionTracking()
        {
            if (monitoredAccount != null)
            {
                _sessionStartTime = DateTime.UtcNow;
                _dailyStartPnL = TotalPnL;
                _sessionTradeCount = 0;
                _lastTradeResult = "";
                _lastTradePnL = 0.0;

                // Get current account balance
                var balanceItem = monitoredAccount.GetAccountItem(Cbi.AccountItem.CashValue, Currency.UsDollar);
                if (balanceItem != null && balanceItem.Value is double)
                {
                    _sessionStartBalance = (double)balanceItem.Value;
                }

                LogAndPrint($"Session tracking initialized: Balance=${_sessionStartBalance:F2}, StartPnL=${_dailyStartPnL:F2}");
            }
        }

        // Update trade result based on execution
        private void UpdateTradeResult(ExecutionEventArgs e)
        {
            if (e.Execution.Order.OrderAction == OrderAction.Buy || e.Execution.Order.OrderAction == OrderAction.Sell)
            {
                _sessionTradeCount++;

                // Calculate P&L for this trade (simplified - actual P&L calculation may be more complex)
                double tradePnL = 0.0;

                // For closing trades, we can estimate P&L
                if (e.Execution.Order.OrderAction == OrderAction.BuyToCover || e.Execution.Order.OrderAction == OrderAction.SellShort)
                {
                    // This is a closing trade - determine if win/loss
                    // Note: This is a simplified approach. Real P&L tracking would require position tracking
                    double currentPnL = TotalPnL;
                    tradePnL = currentPnL - _lastTradePnL;
                    _lastTradePnL = currentPnL;

                    _lastTradeResult = tradePnL > 0 ? "win" : "loss";
                    LogAndPrint($"Trade result updated: {_lastTradeResult} (P&L: ${tradePnL:F2})");
                }
                else
                {
                    // Opening trade - set baseline
                    _lastTradePnL = TotalPnL;
                    _lastTradeResult = "pending";
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion
        
        // New menu items for Control Center integration
        private NTMenuItem multiStratMenuItem;
        private MenuItem existingMenuItemInControlCenter; // Changed type from NTMenuItem

        private static List<StrategyBase> monitoredStrategies = new List<StrategyBase>();

        // HTTP completely removed - using gRPC only
        
        // gRPC Configuration
        private bool grpcInitialized = false;
        private bool grpcInitializing = false; // Prevent concurrent initialization
        private string grpcServerAddress = "http://localhost:50051"; // gRPC server address

        // WebSocket removed - using gRPC only


        // Heartbeat tracking
        private DateTime lastHeartbeatSent = DateTime.MinValue;
        private readonly TimeSpan heartbeatInterval = TimeSpan.FromSeconds(20); // Send heartbeat every 20 seconds (faster than bridge 35s timeout)
        private int heartbeatFailureCount = 0;
        private DateTime lastHeartbeatFailure = DateTime.MinValue;
    private readonly TimeSpan heartbeatBackoffDuration = TimeSpan.FromSeconds(20); // Short backoff (~one tick) after repeated failures
    private System.Windows.Threading.DispatcherTimer heartbeatTimer;
    // Keep a reference to the heartbeat tick handler so we can properly unsubscribe on stop
    private EventHandler heartbeatTickHandler;

        // Logging infrastructure
        // Auto-logging queue removed - using local NinjaScript output only
        // Auto-logging timer removed
        private static readonly object logTimerLock = new object();
        private Account monitoredAccount = null; // To keep track of the account being monitored
        
        // Trailing and Elastic Manager
        private TrailingAndElasticManager trailingAndElasticManager;
    // Disconnect lifecycle guard
    private readonly object grpcDisconnectLock = new object();
    private bool grpcDisconnectInProgress = false;
        
        // Public properties to expose needed resources to TrailingAndElasticManager
        public Account MonitoredAccount => monitoredAccount;

        // HTTP completely removed - using gRPC streaming instead

        // Class to store original NT trade details
        public class OriginalTradeDetails // Renamed from OriginalNtTradeInfo
        {
            public string BaseId { get; set; }
            public MarketPosition MarketPosition { get; set; } // Renamed from OriginalMarketPosition
            public int Quantity { get; set; } // Renamed from OriginalQuantity
            public double Price { get; set; }
            public string NtInstrumentSymbol { get; set; }
            public string NtAccountName { get; set; }
            public OrderAction OriginalOrderAction { get; set; } // Kept this field
            public DateTime Timestamp { get; set; }

            // MULTI_TRADE_GROUP_FIX: Track total and remaining quantity for this BaseID
            public int TotalQuantity { get; set; } = 0; // Total quantity for this BaseID
            public int RemainingQuantity { get; set; } = 0; // Remaining quantity not yet closed
            
            // CLOSURE_TRACKING_FIX: Track closure state to prevent race conditions
            public bool IsClosed { get; set; } = false; // Whether this trade has been closed
            public DateTime? ClosedTimestamp { get; set; } = null; // When it was closed
        }

        // Dictionary to store active NT trades by their base_id (simple TRADE_XXX format)
        private static ConcurrentDictionary<string, OriginalTradeDetails> activeNtTrades = new ConcurrentDictionary<string, OriginalTradeDetails>(); // Updated type
        private readonly object _activeNtTradesLock = new object(); // Added lock object
        
        // Execution tracking to prevent duplicate trade submissions
        private static readonly HashSet<string> processedExecutionIds = new HashSet<string>();
        private static readonly object executionTrackingLock = new object();
        
        // MT5 close notification deduplication
        private static readonly HashSet<string> processedCloseNotifications = new HashSet<string>();
        private static readonly object closeNotificationLock = new object();

        // BaseID generation - now uses timestamp-based approach for guaranteed uniqueness

        // Mapping between simple baseIDs and original NT OrderIds for closure detection
        private static ConcurrentDictionary<string, string> baseIdToOrderIdMap = new ConcurrentDictionary<string, string>();
        private static ConcurrentDictionary<string, string> orderIdToBaseIdMap = new ConcurrentDictionary<string, string>();
        
        // MT5 ticket mappings for reliable position closure
        private static ConcurrentDictionary<string, ulong> baseIdToMT5Ticket = new ConcurrentDictionary<string, ulong>();
        private static ConcurrentDictionary<ulong, string> mt5TicketToBaseId = new ConcurrentDictionary<ulong, string>();

        /// <summary>
        /// Generates a short random base_id (<= 32 chars) suitable for EA comment limits.
        /// Example: TRD_3f1a9c6b72d84e15 (20 chars). Hex-only for simplicity and safety.
        /// </summary>
        private static string GenerateSimpleBaseId()
        {
            // 32-char hex GUID, take first 16 to keep it short while collision-resistant
            var g = Guid.NewGuid().ToString("N");
            var shortHex = g.Substring(0, 16);
            return $"TRD_{shortHex}"; // total length = 4 + 1 + 16 = 21
        }

        // Contract tracking for multi-contract orders
        private static ConcurrentDictionary<string, int> orderContractCounts = new ConcurrentDictionary<string, int>();
        
        /// <summary>
        /// Gets the contract number for this execution within its order.
        /// For multi-contract orders, this ensures proper numbering (1, 2, 3, 4...)
        /// </summary>
        private int GetContractNumberForExecution(string orderId, double totalQuantity)
        {
            if (string.IsNullOrEmpty(orderId) || totalQuantity <= 1)
            {
                return 1; // Single contract orders always use contract_num = 1
            }

            // For multi-contract orders, increment and return the contract number
            int contractNum = orderContractCounts.AddOrUpdate(orderId, 1, (key, current) => current + 1);
            
            LogAndPrint($"CONTRACT_TRACKING: OrderId {orderId} execution #{contractNum} of {totalQuantity} total contracts");
            
            return contractNum;
        }

        // Class to represent the JSON payload for hedge close notifications
        public class HedgeCloseNotification
        {
            public string event_type { get; set; }
            public string base_id { get; set; }
            public string nt_instrument_symbol { get; set; }
            public string nt_account_name { get; set; }
            public double closed_hedge_quantity { get; set; }
            public string closed_hedge_action { get; set; } // "Buy" or "Sell"
            public string timestamp { get; set; }
            public string ClosureReason { get; set; } // Added for MT5 EA closure reason
        }
private HashSet<string> trackedHedgeClosingOrderIds;

        public void LogAndPrint(string message)
        {
            // Direct NinjaTrader output
            NinjaTrader.Code.Output.Process($"[NT_ADDON] {message}", PrintTo.OutputTab1);
            // Also forward to Bridge so JSONL contains UNIFIED_/TRAILING_/ELASTIC_ diagnostics
            try { TryBridgeLog("INFO", "nt_addon", message); } catch { /* best-effort logging */ }
        }
        
        /// <summary>
        /// Extract a value from JSON string
        /// </summary>
        /// <param name="json">JSON string</param>
        /// <param name="key">Key to extract</param>
        /// <returns>Value or empty string if not found</returns>
        private string ExtractJsonValue(string json, string key)
        {
            try
            {
                string searchPattern = $"\"{key}\":\""; // For string values
                int startIndex = json.IndexOf(searchPattern);
                if (startIndex >= 0)
                {
                    startIndex += searchPattern.Length;
                    int endIndex = json.IndexOf('"', startIndex);
                    if (endIndex > startIndex)
                    {
                        return json.Substring(startIndex, endIndex - startIndex);
                    }
                }
                
                // Try numeric values
                searchPattern = $"\"{key}\":";
                startIndex = json.IndexOf(searchPattern);
                if (startIndex >= 0)
                {
                    startIndex += searchPattern.Length;
                    int endIndex = json.IndexOfAny(new char[] { ',', '}' }, startIndex);
                    if (endIndex > startIndex)
                    {
                        return json.Substring(startIndex, endIndex - startIndex).Trim();
                    }
                }
                
                return string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
        
        /// <summary>
        /// Start streaming to receive MT5 trade results
        /// </summary>
        private void StartMT5TradeResultStream()
        {
            try
            {
                LogInfo("GRPC", "Starting MT5 trade result streaming...");
                
                // Start the trading stream to receive trade results from MT5
                bool streamStarted = TradingGrpcClient.StartTradingStream(OnMT5TradeResultReceived);
                
                if (streamStarted)
                {
                    LogInfo("GRPC", "MT5 trade result streaming started successfully");
                }
                else
                {
                    LogError("GRPC", $"Failed to start MT5 trade result streaming: {TradingGrpcClient.LastError}");
                }
            }
            catch (Exception ex)
            {
                LogError("GRPC", $"Exception starting MT5 trade result streaming: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Handle incoming MT5 trade results
        /// </summary>
        /// <param name="tradeResultJson">JSON trade result from MT5</param>
        private void OnMT5TradeResultReceived(string tradeResultJson)
        {
            try
            {
                LogInfo("GRPC", $"Received MT5 trade result: {tradeResultJson}");
                LogInfo("GRPC", $"DEBUG: Full JSON received: {tradeResultJson}");
                
                // Extract fields from JSON - fix base_id extraction
                string baseId = ExtractJsonValue(tradeResultJson, "base_id");  // Use "base_id" for closure notifications
                string action = ExtractJsonValue(tradeResultJson, "action");
                string ticketStr = ExtractJsonValue(tradeResultJson, "ticket");  // MT5 sends "ticket" not "mt5_ticket"
                string status = ExtractJsonValue(tradeResultJson, "status");
                string messageId = ExtractJsonValue(tradeResultJson, "id"); // Extract message ID for deduplication
                string orderType = ExtractJsonValue(tradeResultJson, "order_type"); // May be NT_CLOSE_ACK or MT5_CLOSE
                
                if (!string.IsNullOrEmpty(baseId) && !string.IsNullOrEmpty(ticketStr))
                {
                    if (ulong.TryParse(ticketStr, out ulong mt5Ticket) && mt5Ticket > 0)
                    {
                        // Store the MT5 ticket mapping
                        baseIdToMT5Ticket.TryAdd(baseId, mt5Ticket);
                        mt5TicketToBaseId.TryAdd(mt5Ticket, baseId);
                        
                        LogInfo("GRPC", $"Stored MT5 ticket mapping - BaseID: {baseId} <-> Ticket: {mt5Ticket}");
                    }
                }
                
                // Handle different types of results
                if (!string.IsNullOrEmpty(action))
                {
                    LogInfo("GRPC", $"DEBUG: Processing action '{action}' with baseId '{baseId}'");
                    
                    if (action == "HEDGE_OPENED")
                    {
                        LogInfo("GRPC", $"MT5 hedge opened for BaseID: {baseId}");
                    }
                    else if (action == "HEDGE_CLOSED")
                    {
                        LogInfo("GRPC", $"MT5 hedge closed for BaseID: {baseId}");
                        // Handle MT5-initiated hedge closure - close corresponding NT position
                        // Use the same handler as MT5_CLOSE_NOTIFICATION since both indicate MT5 closed a position
                        HandleMT5InitiatedClosure(tradeResultJson, baseId);
                        LogInfo("GRPC", $"Triggered NT position closure for hedge close event - BaseID: {baseId}");
                    }
                    else if (action == "MT5_CLOSE_NOTIFICATION")
                    {
                        // MT5 initiated a position closure - check for duplicates first
                        bool shouldProcess = false;
                        // Include mt5_ticket in dedup key when available so sequential closes aren’t dropped
                        string ticketForDedup = ExtractJsonValue(tradeResultJson, "mt5_ticket");
                        string dedupulationSuffix = !string.IsNullOrEmpty(ticketForDedup) ? ticketForDedup : messageId;
                        string deduplicationKey = $"{action}_{baseId}_{dedupulationSuffix}"; // action + baseId + (ticket or messageId)
                        
                        lock (closeNotificationLock)
                        {
                            if (!processedCloseNotifications.Contains(deduplicationKey))
                            {
                                processedCloseNotifications.Add(deduplicationKey);
                                shouldProcess = true;
                                LogInfo("GRPC", $"MT5_CLOSE_DEDUP: First occurrence of notification {deduplicationKey} - processing");
                            }
                            else
                            {
                                LogInfo("GRPC", $"MT5_CLOSE_DEDUP: Duplicate notification {deduplicationKey} - skipping to prevent multiple close orders");
                            }
                        }
                        
                        if (shouldProcess)
                        {
                            // If this is an acknowledgement for an NT-initiated close, do not initiate another NT close
                            if (!string.IsNullOrEmpty(orderType) && orderType == "NT_CLOSE_ACK")
                            {
                                LogInfo("GRPC", $"MT5_CLOSE_NOTIFICATION is NT_CLOSE_ACK for BaseID {baseId}; acknowledging without submitting NT close order.");
                                return;
                            }

                            // POLICY: Do NOT close NT when MT5 reports an elastic partial close
                            string closureReason = ExtractJsonValue(tradeResultJson, "closure_reason");
                            if (string.IsNullOrEmpty(closureReason))
                                closureReason = ExtractJsonValue(tradeResultJson, "nt_trade_result");

                            if (!string.IsNullOrEmpty(closureReason) && closureReason.Equals("elastic_partial_close", StringComparison.OrdinalIgnoreCase))
                            {
                                LogInfo("GRPC", $"[ELASTIC_PARTIAL_SKIP] Skipping NT close for BaseID {baseId} due to MT5 elastic_partial_close.");
                                return;
                            }
                            // Process MT5 close notification and close corresponding NT position
                            HandleMT5InitiatedClosure(tradeResultJson, baseId);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogError("GRPC", $"Error processing MT5 trade result: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Handle MT5-initiated position closures by closing corresponding NT positions
        /// </summary>
        /// <param name="notificationJson">JSON notification from MT5</param>
        /// <param name="baseId">BaseID of the closed position</param>
        private void HandleMT5InitiatedClosure(string notificationJson, string baseId)
        {
            try
            {
                LogInfo("GRPC", $"Processing MT5-initiated closure for BaseID: {baseId}");
                LogInfo("GRPC", $"DEBUG: Full JSON received: {notificationJson}");
                
                // Extract closure details from JSON
                // Prefer explicit closure_reason when present; fallback to nt_trade_result (compat)
                string closureReason = ExtractJsonValue(notificationJson, "closure_reason");
                if (string.IsNullOrEmpty(closureReason))
                    closureReason = ExtractJsonValue(notificationJson, "nt_trade_result");
                string mt5TicketStr = ExtractJsonValue(notificationJson, "id"); // Use the closure ID as reference
                string instrument = ExtractJsonValue(notificationJson, "instrument_name");
                
                LogInfo("GRPC", $"MT5 closure details - Reason: {closureReason}, Ticket: {mt5TicketStr}, Instrument: {instrument}");
                
                // Find the corresponding NT position by BaseID
                // Look for an account that has positions, not just the first account
                var account = Account.All.FirstOrDefault(a => a.Positions.Count > 0);
                if (account == null)
                {
                    // Fallback to any account if no account has positions
                    account = Account.All.FirstOrDefault();
                }
                
                if (account == null)
                {
                    LogError("GRPC", "No accounts available for MT5 closure handling");
                    return;
                }
                
                LogInfo("GRPC", $"Using account '{account.Name}' for MT5 closure handling");
                LogInfo("GRPC", $"DEBUG: Available accounts: {string.Join(", ", Account.All.Select(a => $"{a.Name}({a.Positions.Count} pos)"))}");
                
                // HEDGING SYSTEM: Find and close ONLY the specific position matching this base_id
                var positionsToClose = new List<NinjaTrader.Cbi.Position>();
                LogInfo("GRPC", $"DEBUG: Looking for positions to close. Account has {account.Positions.Count} positions and {account.Orders.Count} orders");
                
                // Look up the original trade details for this base_id
                LogInfo("GRPC", $"DEBUG: Looking up base_id '{baseId}' in activeNtTrades");
                if (activeNtTrades.TryGetValue(baseId, out var originalTradeDetails))
                {
                    // CLOSURE_RACE_FIX: Skip only when nothing remains to be closed
                    if (originalTradeDetails.RemainingQuantity <= 0)
                    {
                        LogInfo("GRPC", $"ALREADY_FULLY_CLOSED: Trade {baseId} has RemainingQuantity=0. Skipping MT5 closure handling.");
                        return; // Nothing left to close
                    }
                    
                    LogInfo("GRPC", $"DEBUG: Found original trade details for base_id '{baseId}': Instrument={originalTradeDetails.NtInstrumentSymbol}, Position={originalTradeDetails.MarketPosition}, Qty={originalTradeDetails.Quantity}");
                    
                    // Find the specific position that matches this original trade
                    // IMPORTANT: Do not require current net position quantity to be >= original trade quantity.
                    // After the first sequential close, net qty drops and must still be eligible for subsequent closes.
                    var targetPosition = account.Positions.FirstOrDefault(p =>
                        p.Instrument.FullName == originalTradeDetails.NtInstrumentSymbol &&
                        p.MarketPosition == originalTradeDetails.MarketPosition &&
                        Math.Abs(p.Quantity) > 0);
                    
                    if (targetPosition != null)
                    {
                        LogInfo("GRPC", $"DEBUG: Found matching position to close: {targetPosition.Instrument.MasterInstrument.Name} (Quantity: {targetPosition.Quantity})");
                        LogInfo("GRPC", $"SEQUENTIAL_CLOSE_OK: Proceeding even if current qty < original trade qty {originalTradeDetails.Quantity}");
                        positionsToClose.Add(targetPosition);
                        // Do NOT mark IsClosed here. We'll decrement RemainingQuantity when the NT close order actually fills
                        // in the execution/closure tracking path. This preserves sequential MT5 closes for multi-quantity trades.
                    }
                    else
                    {
                        LogInfo("GRPC", $"DEBUG: No matching position found for base_id '{baseId}' - checking all positions:");
                        foreach (var pos in account.Positions)
                        {
                            LogInfo("GRPC", $"DEBUG: Available position: {pos.Instrument.FullName}, Position: {pos.MarketPosition}, Quantity: {pos.Quantity}");
                        }
                    }
                }
                else
                {
                    LogInfo("GRPC", $"BASE_ID_MISMATCH: '{baseId}' not found in activeNtTrades. Available base_ids: {string.Join(", ", activeNtTrades.Keys.Take(5))}");
                    
                    // ENHANCED INTELLIGENT MATCHING: Try to find position using closure details
                    LogInfo("GRPC", $"INTELLIGENT_MATCHING: Attempting to match position using closure notification details...");
                    
                    // Extract closure details for intelligent matching
                    string quantityStr = ExtractJsonValue(notificationJson, "quantity");
                    
                    if (double.TryParse(quantityStr, out double closedQuantity) && closedQuantity > 0)
                    {
                        LogInfo("GRPC", $"MATCHING_CRITERIA: Looking for positions in instrument '{instrument}' with quantity >= {closedQuantity}");
                        
                        // Find candidate positions based on instrument
                        // Map MT5 instrument (NAS100.s) to NT instrument pattern (NQ)
                        string ntInstrumentPattern = "";
                        if (instrument.Contains("NAS100") || instrument.Contains("NQ"))
                        {
                            ntInstrumentPattern = "NQ";
                        }
                        else if (instrument.Contains("ES") || instrument.Contains("SPX"))
                        {
                            ntInstrumentPattern = "ES";
                        }
                        // Add more mappings as needed
                        
                        var candidatePositions = account.Positions.Where(p => 
                            p.Quantity != 0 && 
                            !string.IsNullOrEmpty(ntInstrumentPattern) &&
                            p.Instrument.FullName.Contains(ntInstrumentPattern)
                        ).ToList();
                        
                        // SAFETY_FILTER: Remove positions that are still actively tracked by other base_ids
                        var safePositions = candidatePositions.Where(candidate =>
                        {
                            // Check if this position is still actively tracked by another base_id
                            var trackingEntries = activeNtTrades.Values.Where(trade => 
                                !trade.IsClosed && 
                                trade.NtInstrumentSymbol == candidate.Instrument.FullName &&
                                trade.MarketPosition == candidate.MarketPosition
                            ).ToList();
                            
                            // If no active tracking entries, it's safe to close
                            return trackingEntries.Count == 0;
                        }).ToList();
                        
                        LogInfo("GRPC", $"INTELLIGENT_MATCH_RESULT: Found {candidatePositions.Count} candidate positions, {safePositions.Count} safe to close for pattern '{ntInstrumentPattern}'");
                        
                        if (safePositions.Count == 1)
                        {
                            LogInfo("GRPC", $"SAFE_MATCH: Found exactly one safe candidate position - safe to close: {safePositions[0].Instrument.FullName} (Qty: {safePositions[0].Quantity})");
                            positionsToClose.Add(safePositions[0]);
                        }
                        else if (safePositions.Count == 0)
                        {
                            LogError("GRPC", $"NO_SAFE_MATCH: No safe positions found matching instrument pattern '{ntInstrumentPattern}' - all may be tracked by other active trades");
                            if (candidatePositions.Count > 0)
                            {
                                LogError("GRPC", $"UNSAFE_CANDIDATES: Found {candidatePositions.Count} positions but they may belong to other active trades - refusing to close to prevent errors");
                            }
                        }
                        else
                        {
                            LogError("GRPC", $"AMBIGUOUS_SAFE_MATCH: Found {safePositions.Count} safe candidate positions - cannot safely determine which to close:");
                            foreach (var pos in safePositions)
                            {
                                LogError("GRPC", $"  - {pos.Instrument.FullName}: {pos.MarketPosition} {pos.Quantity}");
                            }
                            LogError("GRPC", $"SAFETY_ABORT: Refusing to close any position due to ambiguity - base_id mismatch needs investigation");
                        }
                    }
                    else
                    {
                        LogError("GRPC", $"INVALID_QUANTITY: Cannot parse quantity '{quantityStr}' from closure notification - cannot attempt intelligent matching");
                    }
                }
                
                LogInfo("GRPC", $"DEBUG: Found {positionsToClose.Count} positions to close");
                
                if (positionsToClose.Count == 0)
                {
                    Log($"GRPC WARNING: No NT positions found to close for BaseID: {baseId}", LogLevel.Warning);
                    return;
                }
                
                // Close the found positions
                foreach (var position in positionsToClose)
                {
                    try
                    {
                        LogInfo("GRPC", $"Closing NT position: {position.Instrument.MasterInstrument.Name}, Quantity: {position.Quantity}");
                        
                        // CRITICAL FIX: Validate position before creating close order
                        if (Math.Abs(position.Quantity) < 0.01) // Position essentially already closed
                        {
                            LogInfo("GRPC", $"Position {position.Instrument.MasterInstrument.Name} already closed (Quantity: {position.Quantity}) - skipping close order");
                            continue;
                        }
                        
                        // CRITICAL FIX: Use the ORIGINAL TRADE QUANTITY, not the total position quantity
                        // This prevents closing all positions when only one specific trade should close
                        // Always close exactly 1 contract per MT5 hedge close notification to maintain 1:1 mapping
                        int quantityToClose = 1;
                        LogInfo("GRPC", $"QUANTITY_FIX: For MT5-initiated closure, forcing quantity {quantityToClose} to avoid mass closing NT positions (position qty {Math.Abs(position.Quantity)})");
                        OrderAction closeAction = position.MarketPosition == MarketPosition.Long ? OrderAction.Sell : OrderAction.Buy;
                        
                        LogInfo("GRPC", $"Creating close order: Action={closeAction}, Quantity={quantityToClose}");
                        
                        // Create market order to close the position
                        string closeName = $"MT5_CLOSE_{baseId}_{DateTime.UtcNow:HHmmss}";
                        var closeOrder = account.CreateOrder(
                            position.Instrument,
                            closeAction, // Fixed: Use calculated close action
                            OrderType.Market,
                            TimeInForce.Day,
                            quantityToClose, // Fixed: Use absolute quantity
                            0, // limit price (not used for market orders)
                            0, // stop price (not used for market orders)
                            string.Empty, // oco string
                            closeName,
                            null // custom order
                        );
                        
                        if (closeOrder != null)
                        {
                            // ENHANCED: Track close order state and add to tracking
                            LogInfo("GRPC", $"Submitting close order: {closeName}");
                            LogInfo("GRPC", $"Order details BEFORE submit - ID: {closeOrder.OrderId}, Action: {closeOrder.OrderAction}, Type: {closeOrder.OrderType}, Quantity: {closeOrder.Quantity}, Instrument: {closeOrder.Instrument.FullName}");
                            LogInfo("GRPC", $"Order state BEFORE submit: {closeOrder.OrderState}");
                            LogInfo("GRPC", $"Position details - Quantity: {position.Quantity}, MarketPosition: {position.MarketPosition}, AvgPrice: {position.AveragePrice}");
                            
                            // Add to hedge closing order tracking to prevent circular trades
                            if (trackedHedgeClosingOrderIds != null)
                            {
                                trackedHedgeClosingOrderIds.Add(closeOrder.Id.ToString());
                                LogInfo("GRPC", $"Added order {closeOrder.Id} to hedge closing tracking");
                            }
                            
                            account.Submit(new[] { closeOrder });
                            LogInfo("GRPC", $"Submitted NT close order for MT5-initiated closure: {closeName}");
                            LogInfo("GRPC", $"Order state AFTER submit: {closeOrder.OrderState}");

                            // Decrement remaining quantity for this base_id to allow further sequential closes
                            try
                            {
                                lock (_activeNtTradesLock)
                                {
                                    if (activeNtTrades.TryGetValue(baseId, out var od))
                                    {
                                        if (od.RemainingQuantity > 0)
                                        {
                                            od.RemainingQuantity -= 1;
                                            LogInfo("GRPC", $"SEQ_TRACK: RemainingQuantity for {baseId} decremented to {od.RemainingQuantity}");
                                            if (od.RemainingQuantity <= 0)
                                            {
                                                od.IsClosed = true;
                                                od.ClosedTimestamp = DateTime.UtcNow;
                                                LogInfo("GRPC", $"SEQ_TRACK: BaseID {baseId} fully closed");
                                            }
                                            activeNtTrades[baseId] = od;
                                        }
                                    }
                                }
                            }
                            catch (Exception rqEx)
                            {
                                LogError("GRPC", $"Error updating RemainingQuantity for {baseId}: {rqEx.Message}");
                            }
                            
                            // Add comprehensive tracking for order execution
                            Task.Run(async () => 
                            {
                                // Wait a bit and check order status
                                await Task.Delay(1000);
                                LogInfo("GRPC", $"Close order {closeName} status check: State={closeOrder.OrderState}, Filled={closeOrder.Filled}, AvgFillPrice={closeOrder.AverageFillPrice}");
                                
                                // Check again after more time
                                await Task.Delay(4000);
                                LogInfo("GRPC", $"Close order {closeName} final status: State={closeOrder.OrderState}, Filled={closeOrder.Filled}, AvgFillPrice={closeOrder.AverageFillPrice}");
                                
                                if (closeOrder.OrderState == OrderState.Rejected)
                                {
                                    LogError("GRPC", $"Close order {closeName} was REJECTED. This explains why positions aren't closing!");
                                }
                                else if (closeOrder.OrderState != OrderState.Filled && closeOrder.OrderState != OrderState.PartFilled)
                                {
                                    LogError("GRPC", $"Close order {closeName} did not execute. State: {closeOrder.OrderState}");
                                }
                            });
                        }
                        else
                        {
                            LogError("GRPC", $"Failed to create close order for position {position.Instrument.MasterInstrument.Name}");
                        }
                    }
                    catch (Exception orderEx)
                    {
                        LogError("GRPC", $"Error creating close order for position: {orderEx.Message}");
                        LogError("GRPC", $"OrderEx StackTrace: {orderEx.StackTrace}");
                    }
                }
                
                LogInfo("GRPC", $"Completed MT5-initiated closure handling for BaseID: {baseId}, closed {positionsToClose.Count} positions");
            }
            catch (Exception ex)
            {
                LogError("GRPC", $"Error handling MT5-initiated closure for BaseID {baseId}: {ex.Message}");
            }
        }



        /// <summary>
        /// Start keepalive heartbeat system to maintain bridge connection
        /// </summary>
        private void StartHeartbeatSystem()
        {
            if (heartbeatTimer == null)
            {
                heartbeatTimer = new System.Windows.Threading.DispatcherTimer();
                heartbeatTimer.Interval = heartbeatInterval;
                // store handler so we can unsubscribe exactly later
                heartbeatTickHandler = async (sender, e) => await SendHeartbeatAsync();
                heartbeatTimer.Tick += heartbeatTickHandler;
                heartbeatTimer.Start();
                LogInfo("GRPC", "Keepalive heartbeat system started");
            }
        }

        /// <summary>
        /// Stop keepalive heartbeat system
        /// </summary>
        private void StopHeartbeatSystem()
        {
            if (heartbeatTimer != null)
            {
                heartbeatTimer.Stop();
                if (heartbeatTickHandler != null)
                {
                    heartbeatTimer.Tick -= heartbeatTickHandler;
                    heartbeatTickHandler = null;
                }
                heartbeatTimer = null;
                LogInfo("GRPC", "Keepalive heartbeat system stopped");
            }
        }

        /// <summary>
        /// Public method to stop heartbeat system (for UI cleanup)
        /// </summary>
        public void StopHeartbeatSystemPublic()
        {
            StopHeartbeatSystem();
        }

        /// <summary>
        /// Send heartbeat to bridge via gRPC with retry logic and circuit breaker
        /// </summary>
        private async Task SendHeartbeatAsync()
        {
            try
            {
                // Skip if UI is not open (manual connection mode)
                if (!IsUiOpen)
                {
                    return;
                }
                // Circuit breaker: Skip if we've had recent failures
                if (heartbeatFailureCount >= 3 && DateTime.UtcNow - lastHeartbeatFailure < heartbeatBackoffDuration)
                {
                    return; // Skip heartbeat during backoff period
                }

                // Skip if not initialized
                if (!grpcInitialized)
                {
                    return;
                }

                // Use gRPC health check (silent - no logging to prevent spam)
                var healthResult = await Task.Run(() => {
                    string responseJson;
                    bool isHealthy = TradingGrpcClient.HealthCheck("NT_ADDON_KEEPALIVE", out responseJson);
                    return new { IsHealthy = isHealthy, ResponseJson = responseJson };
                });
                bool isHealthy = healthResult.IsHealthy;

                if (isHealthy)
                {
                    lastHeartbeatSent = DateTime.UtcNow;
                    heartbeatFailureCount = 0; // Reset failure count on success
                    // No logging for successful heartbeats to avoid spam
                }
                else
                {
                    heartbeatFailureCount++;
                    lastHeartbeatFailure = DateTime.UtcNow;

                    // Only log error once every 3 failures to reduce spam
                    if (heartbeatFailureCount == 1 || heartbeatFailureCount % 3 == 0)
                    {
                        string error = TradingGrpcClient.LastError;
                        LogWarn("SYSTEM", $"Heartbeat failed ({heartbeatFailureCount} failures): {error}");
                    }

                    // Circuit breaker: after sustained failures, disconnect fully to release resources
                    if (heartbeatFailureCount >= 6)
                    {
                        LogWarn("GRPC", $"Heartbeat has failed {heartbeatFailureCount} times — disconnecting gRPC and stopping timers to avoid hangs");
                        DisconnectGrpcAndStopAll();
                    }
                }
            }
            catch (Exception ex)
            {
                heartbeatFailureCount++;
                lastHeartbeatFailure = DateTime.UtcNow;
                
                // Only log exception once every 3 failures to reduce spam
                if (heartbeatFailureCount == 1 || heartbeatFailureCount % 3 == 0)
                {
                    LogWarn("SYSTEM", $"Heartbeat exception ({heartbeatFailureCount} failures): {ex.GetType().Name} - {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Initialize gRPC client connection
        /// </summary>
        private async Task InitializeGrpcClient()
        {
            try
            {
                // Prevent concurrent initialization
                if (grpcInitialized || grpcInitializing) return;
                
                grpcInitializing = true;

                LogInfo("GRPC", "Initializing gRPC client connection...");
                
                // Initialize the gRPC client with server address (remove http:// prefix for gRPC)
                string grpcAddress = grpcServerAddress.Replace("http://", "").Replace("https://", "");
                LogDebug("GRPC", $"Calling TradingGrpcClient.Initialize with: {grpcAddress} (converted from {grpcServerAddress})");
                bool initialized = TradingGrpcClient.Initialize(grpcAddress);
                LogDebug("GRPC", $"TradingGrpcClient.Initialize returned: {initialized}");
                
                if (initialized)
                {
                    // Wait for actual connection establishment with faster polling
                    LogDebug("GRPC", "Waiting for actual gRPC connection establishment...");
                    bool actuallyConnected = false;
                    for (int i = 0; i < 50; i++) // Wait up to 2.5 seconds (50 * 50ms)
                    {
                        if (TradingGrpcClient.IsConnected)
                        {
                            actuallyConnected = true;
                            LogInfo("GRPC", $"gRPC client connected after {i * 50}ms");
                            break;
                        }
                        await Task.Delay(50); // Wait 50ms before checking again (faster polling)
                    }
                    
                    if (actuallyConnected)
                    {
                        grpcInitialized = true;
                        grpcInitializing = false;
                        LogInfo("GRPC", "gRPC client fully initialized and connected");
                        
                        // Start trading stream to receive MT5 trade results
                        StartMT5TradeResultStream();
                        
                        // Start keepalive heartbeat system to maintain connection
                        LogDebug("GRPC", "Starting keepalive heartbeat system...");
                        StartHeartbeatSystem();
                    }
                    else
                    {
                        grpcInitializing = false;
                        LogWarn("GRPC", "gRPC client Initialize() returned true but actual connection failed");
                    }
                }
                else
                {
                    string error = TradingGrpcClient.LastError;
                    grpcInitializing = false; // Reset on init failure
                    LogError("GRPC", $"Failed to initialize gRPC client: {error}");
                    
                    // Initialization failed - log only (no blocking popup)
                }
            }
            catch (Exception ex)
            {
                grpcInitializing = false; // Reset on exception
                LogError("GRPC", $"Exception during gRPC initialization: {ex.Message}");
                
                // Show exception popup
                string popupMessage = $"MultiStratManager - gRPC Exception\n\nStatus: EXCEPTION\nServer: {grpcServerAddress}\nError: {ex.Message}\n\nPlease check the bridge server configuration.";
                MessageBox.Show(popupMessage, "gRPC Initialization Exception", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Check if a log should be sent based on verbose mode and log characteristics
        /// </summary>
        private bool ShouldSendLog(string logLevel, string category, string message)
        {
            // Always send ERROR and CRITICAL logs
            if (logLevel == "ERROR" || logLevel == "CRITICAL")
                return true;

            // Always send WARN logs
            if (logLevel == "WARN")
                return true;

            // Send all non-DEBUG logs (INFO, WARN, ERROR, CRITICAL)

            // In non-verbose mode, filter out noisy logs
            if (logLevel == "DEBUG")
                return false;

            // Filter out noisy message patterns - enhanced patterns for better filtering
            var noisyPatterns = new[]
            {
                "ping", "heartbeat", "poll", "status check", "connection alive",
                "timer tick", "balance display updated", "account item update received",
                "strategy state polling", "updating trailing stops display",
                "found \\d+ internal stops", "processing stop", "current price:",
                "added trailing stop display", "final count in ui:",
                "INTERNAL_TRAILING_DEBUG", "POSITION_SCAN_DEBUG", "ELASTIC_DEBUG",
                "monitoring \\d+ active", "monitoring \\d+ tracked", "scanning account",
                "found \\d+ non-flat positions", "current elastic trackers"
            };

            foreach (var pattern in noisyPatterns)
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(message, pattern,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        private void LogForSLTP(string message, LogLevel level)
        {
            // Convert LogLevel to our logging system
            string logLevelStr = level.ToString().ToUpper();
            switch (logLevelStr)
            {
                case "DEBUG":
                    LogDebug("SLTP", message);
                    break;
                case "INFO":
                    LogInfo("SLTP", message);
                    break;
                case "WARN":
                    LogWarn("SLTP", message);
                    break;
                case "ERROR":
                    LogError("SLTP", message);
                    break;
                default:
                    LogInfo("SLTP", message);
                    break;
            }
        }

        /// <summary>
        /// Event handler for when SLTP cleanup is complete for an entry order
        /// </summary>
        private void OnSLTPCleanupComplete(string entryOrderId)
        {
            LogAndPrint($"SLTP cleanup completed for entry order: {entryOrderId}");
        }
 
        /// <summary>
        /// Standard constructor - required for NinjaTrader add-on registration
        /// </summary>
        public MultiStratManager()
        {
            Print("[NT_ADDON][INFO][INIT] MultiStratManager constructor called");
            Print($"[NT_ADDON][DEBUG] Current State: {State}");
            
            // FORCE RESET: If state is Finalized, reset to initial state
            if (State == State.Finalized)
            {
                Print("[NT_ADDON][DEBUG] State is Finalized - forcing reset to SetDefaults");
                State = State.SetDefaults;
                Print($"[NT_ADDON][DEBUG] State after reset: {State}");
                
                // Manually trigger OnStateChange
                Print("[NT_ADDON][DEBUG] About to call OnStateChange()");
                try
                {
                    OnStateChange();
                    Print("[NT_ADDON][DEBUG] OnStateChange() call completed");
                }
                catch (Exception ex)
                {
                    Print($"[NT_ADDON][ERROR] OnStateChange() crashed: {ex.Message}");
                    Print($"[NT_ADDON][ERROR] Stack trace: {ex.StackTrace}");
                }
            }
            
            Print("[NT_ADDON][DEBUG] Constructor completed successfully");
        }

        /// <summary>
        /// Constructor with command parameter - called when the menu item is clicked
        /// </summary>
        /// <param name="command">Command to execute</param>
        public MultiStratManager(string command)
        {
            LogToSystem($"MultiStratManager constructor with command '{command}' called", "INIT");
            if (command == "ShowWindow")
            {
                LogToSystem("ShowWindow command received", "UI");
                ShowWindow();
            }
        }

        // Helper: whether the UI window is currently open
        public bool IsUiOpen => window != null && window.IsVisible;

        // Centralized cleanup for gRPC and timers
        public void DisconnectGrpcAndStopAll()
        {
            // Idempotent guard to prevent re-entrant/disposed access during disconnect
            lock (grpcDisconnectLock)
            {
                if (grpcDisconnectInProgress)
                {
                    LogInfo("GRPC", "Disconnect already in progress; skipping duplicate call");
                    return;
                }
                grpcDisconnectInProgress = true;
            }

            try
            {
                // Stop all strategy-local timers/monitors first to avoid races during gRPC teardown
                try
                {
                    // First cancel any working managed stops to avoid orphan orders closing future trades
                    // Note: stable TrailingAndElasticManager does not expose CancelAllManagedStops; proceed with available cleanup
                    trailingAndElasticManager?.StopElasticMonitoring();
                    trailingAndElasticManager?.CleanupBarsRequests();
                    LogInfo("GRPC", "Cancelled managed stops and stopped trailing/elastic monitors prior to gRPC disconnect");
                }
                catch (Exception ex)
                {
                    LogWarn("GRPC", $"Error stopping trailing monitors: {ex.Message}");
                }

                // Stop heartbeat first to avoid reconnect attempts
                StopHeartbeatSystem();

                // Stop MT5 trade stream if running (with timeout)
                try
                {
                    var stopTask = Task.Run(() => TradingGrpcClient.StopTradingStream());
                    if (!stopTask.Wait(2000)) // 2 second timeout
                    {
                        LogWarn("GRPC", "StopTradingStream timed out");
                    }
                }
                catch (Exception ex)
                {
                    LogWarn("GRPC", $"Error stopping trading stream: {ex.Message}");
                }

                // Dispose gRPC client (with timeout)
                try
                {
                    var disposeTask = Task.Run(() => TradingGrpcClient.Dispose());
                    if (!disposeTask.Wait(2000)) // 2 second timeout
                    {
                        LogWarn("GRPC", "gRPC client dispose timed out");
                    }
                }
                catch (Exception ex)
                {
                    LogWarn("GRPC", $"Error disposing gRPC client: {ex.Message}");
                }

                grpcInitialized = false;
                grpcInitializing = false;
                LogInfo("GRPC", "Disconnected gRPC and stopped timers");
            }
            catch (Exception ex)
            {
                LogWarn("GRPC", $"DisconnectGrpcAndStopAll encountered: {ex.Message}");
            }
            finally
            {
                lock (grpcDisconnectLock)
                {
                    grpcDisconnectInProgress = false;
                }
            }
        }

        /// <summary>
        /// Handles state changes in the add-on lifecycle
        /// </summary>
        protected override void OnStateChange()
        {
            Print($"[NT_ADDON][DEBUG] OnStateChange called - State: {State}");
            
            if (State == State.SetDefaults)
            {
                Print("[NT_ADDON][DEBUG] Setting defaults...");

                // ✅ RECOMPILATION SAFETY: Aggressive cleanup of static resources before initialization
                PerformStaticCleanup();

                try
                {
                    Description = "Multi-Strategy Manager for hedging";
                    Name = "Multi-Strategy Manager";
                    Instance = this;
                    trackedHedgeClosingOrderIds = new HashSet<string>();
                    Print("[NT_ADDON][DEBUG] SetDefaults completed - progressing to Active");
                    State = State.Active;
                }
                catch (Exception ex)
                {
                    Print($"[NT_ADDON][ERROR] Error in SetDefaults: {ex.Message}");
                }
                sltpRemovalLogic = new SLTPRemovalLogic();
                
                // Initialize TrailingAndElasticManager
                trailingAndElasticManager = new TrailingAndElasticManager(this);
                
                // Subscribe to SLTP cleanup completion event
                SLTPRemovalLogic.SLTPCleanupCompleted += OnSLTPCleanupComplete;
                
                // Setup assembly resolver for gRPC dependencies
                AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
                // Defer gRPC initialization until the UI is opened or user explicitly connects
                grpcInitialized = false;
                LogInfo("GRPC", "Deferred gRPC initialization until UI opens or user connects");
                // Other default settings can be initialized here
            }
            else if (State == State.Configure)
            {
                // No auto-startup - all connections will be started when UI window is opened
                NinjaTrader.Code.Output.Process("[NT_ADDON] MultiStratManager configured - connections will start when UI is opened", PrintTo.OutputTab1);
            }
            else if (State == State.Active)
            {
                Print("[NT_ADDON][DEBUG] State.Active reached");
                if (isFirstRun)
                {
                    Print("[NT_ADDON][DEBUG] First run - auto launch disabled (manual only)");
                    isFirstRun = false;
                    // Auto-launch disabled per user request; window opens only via menu
                }
                Print("[NT_ADDON][DEBUG] OnStateChange Active completed");
            }
            else if (State == State.Terminated)
            {
                LogInfo("SYSTEM", "MultiStratManager Terminated - performing aggressive cleanup");

                try
                {
                    // ✅ AGGRESSIVE CLEANUP: Stop all timers first
                    StopAutoLaunchTimer();
                    StopBridgeConnectionMonitoring();

                    // ✅ AGGRESSIVE CLEANUP: Stop all managers and monitoring
                    trailingAndElasticManager?.StopElasticMonitoring();
                    trailingAndElasticManager?.CleanupBarsRequests();

                    // ✅ AGGRESSIVE CLEANUP: Unsubscribe from all events
                    try { SLTPRemovalLogic.SLTPCleanupCompleted -= OnSLTPCleanupComplete; } catch { }
                    try { AppDomain.CurrentDomain.AssemblyResolve -= OnAssemblyResolve; } catch { }

                    // ✅ AGGRESSIVE CLEANUP: Dispose all resources
                    sltpRemovalLogic?.Cleanup();
                    SetMonitoredAccount(null);
                    DisconnectGrpcAndStopAll();

                    // ✅ AGGRESSIVE CLEANUP: Force close UI window
                    if (window != null)
                    {
                        try
                        {
                            if (window.Dispatcher.CheckAccess())
                            {
                                window.Close();
                            }
                            else
                            {
                                window.Dispatcher.BeginInvoke(new Action(() => window.Close()));
                            }
                            window = null;
                        }
                        catch (Exception ex)
                        {
                            Print($"[NT_ADDON][ERROR] Error closing window during termination: {ex.Message}");
                        }
                    }

                    // ✅ AGGRESSIVE CLEANUP: Clear static resources
                    monitoredStrategies?.Clear();
                    Instance = null;

                    // ✅ AGGRESSIVE CLEANUP: Force garbage collection
                    GC.Collect();
                    GC.WaitForPendingFinalizers();

                    LogInfo("SYSTEM", "MultiStratManager termination cleanup completed");
                }
                catch (Exception ex)
                {
                    Print($"[NT_ADDON][ERROR] Error during termination cleanup: {ex.Message}");
                }
            }
            // State.Terminated already handles StopHttpListener.
            // The State enum does not have a 'Disabled' member in this context.
            // If runtime disabling requires specific cleanup beyond what State.Inactive or State.Terminated provide,
            // a different approach would be needed. For now, removing this erroneous check.
        }
        
        /// <summary>
        /// Shows the Multi-Strategy Manager window
        /// </summary>
        public void ShowWindow()
        {
            try
            {
                LogDebug("UI", "ShowWindow called");
                
                // We need to ensure we create and show the window on the UI thread
                // Using Application.Current.Dispatcher ensures we're on the main UI thread
                Application.Current.Dispatcher.Invoke(new Action(delegate()
                {
                    try
                    {
                        if (window == null)
                        {
                            LogDebug("UI", "Creating new window");
                            window = new UIForManager();
                            
                            // Handle window closed event - ensure full cleanup so addon can be reopened/edited without restarting NT
                            window.Closed += new EventHandler(delegate(object o, EventArgs e)
                            {
                                LogDebug("UI", "Window closed - performing cleanup");
                                try
                                {
                                    DisconnectGrpcAndStopAll();
                                    SetMonitoredAccount(null);
                                }
                                catch (Exception ex)
                                {
                                    LogWarn("UI", $"Cleanup on window close encountered: {ex.Message}");
                                }
                                window = null;
                                // Allow services to start again when reopened
                                connectionsStarted = false;
                            });
                            
                            // Handle window loaded event to ensure content is visible
                            window.Loaded += new RoutedEventHandler(delegate(object o, RoutedEventArgs e)
                            {
                                LogDebug("UI", "Window loaded");
                                // Force layout update after window is loaded
                                window.UpdateLayout();
                            });
                        }

                        // Ensure the window is visible
                        if (!window.IsVisible)
                        {
                            LogDebug("UI", "Showing window");
                            window.Show();
                            window.Activate();
                            window.Focus();

                            // Force layout update
                            window.UpdateLayout();
                        }
                        else
                        {
                            LogDebug("UI", "Window already visible, bringing to front");
                            window.WindowState = WindowState.Normal;
                            window.Activate();
                            window.Focus();

                            // Force layout update
                            window.UpdateLayout();
                        }

                        // Start connections only once when window is first opened
                        if (!connectionsStarted)
                        {
                            connectionsStarted = true;
                            NinjaTrader.Code.Output.Process("[NT_ADDON] Starting bridge connections since window is now open", PrintTo.OutputTab1);
                            
                            // Start essential services
                            // Initialize gRPC connection (run on background thread to avoid UI blocking)
                            Task.Run(async () => await InitializeGrpcClient());
                            
                            // Auto-logging timer removed - using direct NinjaScript output only
                            
                            // Removed one-time heartbeat - using scheduled heartbeat system instead

                            // Start bridge connection monitoring now that UI is open
                            try
                            {
                                // bridgeConnectionTimer = new System.Windows.Threading.DispatcherTimer(); // REMOVED
                                // bridgeConnectionTimer.Interval = TimeSpan.FromSeconds(bridgeConnectionCheckIntervalSeconds); // REMOVED
                                // bridgeConnectionTimer.Tick += new EventHandler(OnBridgeConnectionTimerTick); // REMOVED
                                // bridgeConnectionTimer.Start(); // REMOVED
                                NinjaTrader.Code.Output.Process("[NT_ADDON] Manual bridge connection mode enabled", PrintTo.OutputTab1);
                            }
                            catch (Exception ex)
                            {
                                LogError("SYSTEM", $"ERROR in bridge initialization: {ex.Message}");
                            }

                            // Note: WebSocket removed - using gRPC only
                            // Note: Elastic monitoring will be initialized when monitored account is set
                            LogInfo("SYSTEM", "Elastic monitoring will be initialized when account is set");
                            
                            // Initialize bars requests for trailing stop calculations
                            if (EnableTrailing)
                            {
                                trailingAndElasticManager?.InitializeBarsRequests();
                            }
                        }
                    }
                    catch (Exception innerEx)
                    {
                        LogError("UI", $"ERROR in ShowWindow UI thread: {innerEx.Message}\n{innerEx.StackTrace}");
                    }
                }));
            }
            catch (Exception ex)
            {
                LogError("UI", $"ERROR in ShowWindow: {ex.Message}\n{ex.StackTrace}");
            }
        }
        
        private void StartAutoLaunchTimer()
        {
            try
            {
                // Auto-launch disabled
                LogDebug("SYSTEM", "Auto launch timer not started (manual mode)");
            }
            catch (Exception ex)
            {
                LogError("SYSTEM", $"ERROR starting auto launch timer: {ex.Message}");
            }
        }

        private void StopAutoLaunchTimer()
        {
            try
            {
                if (autoLaunchTimer != null)
                {
                    autoLaunchTimer.Stop();
                    autoLaunchTimer.Tick -= OnAutoLaunchTimerTick;
                    autoLaunchTimer = null;
                    LogDebug("SYSTEM", "Auto launch timer stopped");
                }
            }
            catch (Exception ex)
            {
                LogError("SYSTEM", $"ERROR stopping auto launch timer: {ex.Message}");
            }
        }

        private void StartBridgeConnectionMonitoring()
        {
            // Manual connection mode - no automatic monitoring
            NinjaTrader.Code.Output.Process("[NT_ADDON] Manual bridge connection mode - connect via UI button only", PrintTo.OutputTab1);
        }

        private void StopBridgeConnectionMonitoring()
        {
            // Manual connection mode - no timer cleanup needed
            NinjaTrader.Code.Output.Process("[NT_ADDON] Manual bridge connection mode - no monitoring to stop", PrintTo.OutputTab1);
        }

        // ===== WebSocket removed - using gRPC only =====
        public async Task FlushAllLogsToBridge()
        {
            try
            {
                LogInfo("SYSTEM", "Flushing all pending logs to bridge");

                // Log flushing removed - using direct NinjaScript output only

                // Send a test log to verify connectivity
                LogInfo("SYSTEM", "Log flush completed - bridge connectivity verified");

                // Wait a moment for the flush to complete
                await Task.Delay(500);

                NinjaTrader.Code.Output.Process("[NT_ADDON] All logs flushed to bridge", PrintTo.OutputTab1);
            }
            catch (Exception ex)
            {
                LogError("SYSTEM", $"Error during log flush: {ex.Message}");
                throw;
            }
        }
        
        private void OnAutoLaunchTimerTick(object sender, EventArgs e)
        {
            try
            {
                // Auto-launch disabled; just stop timer if somehow invoked
                StopAutoLaunchTimer();
            }
            catch (Exception ex)
            {
                LogError("SYSTEM", $"ERROR in auto launch timer tick: {ex.Message}");
                StopAutoLaunchTimer();
            }
        }


        // Add new method to handle window creation
        /// <summary>
        /// Called when a NinjaTrader window is created. Used here to add menu items to the Control Center.
        /// </summary>
        /// <param name="window">The window that was created.</param>
        protected override void OnWindowCreated(Window window)
        {
            // Quiet noisy per-window logging; only act when ControlCenter is detected
            
            try
            {
                // We want to place our AddOn in the Control Center's menus
                ControlCenter cc = window as ControlCenter;
                if (cc == null)
                {
                    return;
                }

                Print("[NT_ADDON][UI] ControlCenter window detected - registering menu item");

                // Find the "New" menu item
                if (cc.MainMenu == null)
                {
                    LogError("UI", "ERROR: MainMenu not found in Control Center");
                    return;
                }
                
                // Look for the "New" menu item
                existingMenuItemInControlCenter = null;
                // Replace this line:
                // Replace this line:
                // Iterate through the top-level items in the MainMenu
                foreach (object item in cc.MainMenu) // Iterate directly over the Menu control
                        {
                            MenuItem menuItem = item as MenuItem;
                            if (menuItem != null && menuItem.Header != null && menuItem.Header.ToString() == "New")
                            {
                                existingMenuItemInControlCenter = menuItem; // Removed incorrect cast
                                break;
                            }
                        }
                
                if (existingMenuItemInControlCenter == null)
                {
                    LogError("UI", "ERROR: Could not find 'New' menu item in Control Center");
                    return;
                }

                // Check if our menu item already exists to avoid duplicates
                // Renamed inner loop variable from 'item' to 'subItem' to resolve CS0136
                foreach (object subItem in existingMenuItemInControlCenter.ItemsSource ?? existingMenuItemInControlCenter.Items)
                {
                    // Use the renamed variable 'subItem'
                    MenuItem subMenuItem = subItem as MenuItem;
                    if (subMenuItem != null && subMenuItem.Header != null && subMenuItem.Header.ToString() == "Multi-Strategy Manager")
                    {
                        // Our menu item already exists, no need to add it again
                        LogDebug("UI", "Menu item already exists, not adding again");
                        return;
                    }
                }

                // 'Header' sets the name of our AddOn seen in the menu structure
                multiStratMenuItem = new NTMenuItem();
                multiStratMenuItem.Header = "Multi-Strategy Manager";
                multiStratMenuItem.Style = Application.Current.TryFindResource("MainMenuItem") as Style;

                // Add our AddOn into the "New" menu
                existingMenuItemInControlCenter.Items.Add(multiStratMenuItem);

                // Subscribe to the event for when the user presses our AddOn's menu item
                multiStratMenuItem.Click += new RoutedEventHandler(OnMenuItemClick);

                Print("[NT_ADDON][UI] Added Multi-Strategy Manager to Control Center menu");
            }
            catch (Exception ex)
            {
                Print($"[NT_ADDON][ERROR] ERROR in OnWindowCreated: {ex.Message}\n{ex.StackTrace}");
            }
        }
        
        // Helper method to find a visual child of a specific type
        private T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            try
            {
                // Check if the parent is null
                if (parent == null)
                    return null;
                
                // Check if the parent is of the requested type
                if (parent is T)
                    return parent as T;
                
                // Get the number of children
                int childCount = VisualTreeHelper.GetChildrenCount(parent);
                
                // Search through all children
                for (int i = 0; i < childCount; i++)
                {
                    DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                    
                    // Recursively search this child
                    T result = FindVisualChild<T>(child);
                    
                    // If we found the child, return it
                    if (result != null)
                        return result;
                }
                
                return null;
            }
            catch
            {
                // Ignore errors and return null
                return null;
            }
        }
        
        // Add new method to clean up when window is destroyed
        /// <summary>
        /// Called when a NinjaTrader window is destroyed. Used here to clean up menu items from the Control Center.
        /// </summary>
        /// <param name="window">The window that was destroyed.</param>
        protected override void OnWindowDestroyed(Window window)
        {
            if (multiStratMenuItem != null && window is ControlCenter)
            {
                LogDebug("UI", "ControlCenter window destroyed");

                if (existingMenuItemInControlCenter != null && existingMenuItemInControlCenter.Items.Contains(multiStratMenuItem))
                    existingMenuItemInControlCenter.Items.Remove(multiStratMenuItem);

                multiStratMenuItem.Click -= OnMenuItemClick;
                multiStratMenuItem = null;
                existingMenuItemInControlCenter = null;
            }
        }

        // Add new method to handle menu item click
        private void OnMenuItemClick(object sender, RoutedEventArgs e)
        {
            LogDebug("UI", "Menu item clicked");
            // Use Application.Current.Dispatcher instead of RandomDispatcher
            Application.Current.Dispatcher.BeginInvoke(new Action(delegate() { ShowWindow(); }));
        }


        /// <summary>
        /// Set gRPC server address for the bridge connection
        /// </summary>
        public void SetGrpcAddress(string address)
        {
            if (!string.IsNullOrEmpty(address))
            {
                grpcServerAddress = address;
                LogInfo("GRPC", $"gRPC Server Address set to: {grpcServerAddress}");

                // Print initial gRPC address to NT terminal
                NinjaTrader.Code.Output.Process($"[NT_ADDON] gRPC Address configured: {grpcServerAddress}", PrintTo.OutputTab1);

                // Manual connection mode - address set but no automatic connection
                NinjaTrader.Code.Output.Process("[NT_ADDON] Manual connection mode - use UI button to connect", PrintTo.OutputTab1);
            }
        }

        private async Task SendToBridge(Dictionary<string, object> data)
        {
            try
            {
                string jsonPayload = SimpleJson.SerializeObject(data);
                LogDebug("CONNECTION", $"Sending data to bridge via gRPC: {jsonPayload}");

                // Initialize gRPC if needed
                if (!grpcInitialized)
                {
                    await InitializeGrpcClient();
                }

                // Send via gRPC only - no HTTP fallback
                bool success = TradingGrpcClient.SubmitTrade(jsonPayload);
                if (success)
                {
                    LogDebug("GRPC", "Trade data successfully sent via gRPC");
                }
                else
                {
                    string error = TradingGrpcClient.LastError;
                    LogError("GRPC", $"gRPC trade submission failed: {error}");
                    throw new Exception($"Failed to send trade via gRPC: {error}");
                }
            }
            catch (Exception ex)
            {
                LogError("CONNECTION", $"Exception sending trade data to bridge via gRPC: {ex.Message}");
                throw; // Re-throw to allow caller to handle
            }
        }

        // HTTP removed - using gRPC only

        private async Task SendClosureToBridge(string baseId, int quantity)
        {
            try
            {
                var closureRequest = new
                {
                    base_id = baseId,
                    closed_hedge_quantity = quantity
                };

                string jsonPayload = SimpleJson.SerializeObject(closureRequest);
                LogAndPrint($"CLOSURE_REQUEST: Sending closure to bridge via gRPC: {jsonPayload}");

                // Initialize gRPC if needed
                if (!grpcInitialized)
                {
                    await InitializeGrpcClient();
                }

                // Send via gRPC only - no HTTP fallback
                bool success = TradingGrpcClient.NTCloseHedge(jsonPayload);
                if (success)
                {
                    LogAndPrint($"CLOSURE_SUCCESS: Closure request sent successfully via gRPC for baseId {baseId}");
                }
                else
                {
                    string error = TradingGrpcClient.LastError;
                    LogError("GRPC", $"gRPC closure request failed: {error}");
                    throw new Exception($"Failed to send closure via gRPC: {error}");
                }
            }
            catch (Exception ex)
            {
                LogError("CLOSURE", $"Exception sending closure request to bridge via gRPC: {ex.Message}");
                throw; // Re-throw to allow caller to handle
            }
        }

        // HTTP removed - using gRPC only

        //+------------------------------------------------------------------+
        //| Structured Logging Methods                                      |
        //+------------------------------------------------------------------+
        
        // Initialize logging timer
        private void InitializeLogging()
        {
            // Logging timer disabled until UI is opened - no auto HTTP log flushing
            LogDebug("LOGGING", "Logging timer disabled - will start when UI is opened");
        }

        // Cleanup logging timer
        // Auto-logging cleanup removed - using direct NinjaScript output only

        // Log DEBUG level message
        public void LogDebug(string category, string message, string tradeId = "", string baseId = "")
        {
            NinjaTrader.Code.Output.Process($"[NT_ADDON][DEBUG][{category}] {message}", PrintTo.OutputTab1);
            try { System.Console.WriteLine($"[NT_ADDON][DEBUG][{category}] {message}"); } catch { }
            TryBridgeLog("DEBUG", category, message, tradeId, baseId: baseId);
        }

        // Log INFO level message  
        public void LogInfo(string category, string message, string tradeId = "", string baseId = "")
        {
            NinjaTrader.Code.Output.Process($"[NT_ADDON][INFO][{category}] {message}", PrintTo.OutputTab1);
            try { System.Console.WriteLine($"[NT_ADDON][INFO][{category}] {message}"); } catch { }
            TryBridgeLog("INFO", category, message, tradeId, baseId: baseId);
        }

        // Log WARN level message
        public void LogWarn(string category, string message, string tradeId = "", string baseId = "")
        {
            NinjaTrader.Code.Output.Process($"[NT_ADDON][WARN][{category}] {message}", PrintTo.OutputTab1);
            try { System.Console.WriteLine($"[NT_ADDON][WARN][{category}] {message}"); } catch { }
            TryBridgeLog("WARN", category, message, tradeId, baseId: baseId);
        }

        // Log ERROR level message
        public void LogError(string category, string message, int errorCode = 0, string tradeId = "", string baseId = "")
        {
            NinjaTrader.Code.Output.Process($"[NT_ADDON][ERROR][{category}] {message}", PrintTo.OutputTab1);
            try { System.Console.WriteLine($"[NT_ADDON][ERROR][{category}] {message}"); } catch { }
            TryBridgeLog("ERROR", category, message, tradeId, errorCode.ToString(), baseId: baseId);
        }

        // Log CRITICAL level message
        public void LogCritical(string category, string message, int errorCode = 0, string tradeId = "", string context = "", string baseId = "")
        {
            NinjaTrader.Code.Output.Process($"[NT_ADDON][CRITICAL][{category}] {message}", PrintTo.OutputTab1);
            try { System.Console.WriteLine($"[NT_ADDON][CRITICAL][{category}] {message}"); } catch { }
            TryBridgeLog("CRITICAL", category, message, tradeId, errorCode.ToString(), baseId: baseId);
        }

        // Invoke NTGrpcClient logging via reflection to avoid hard dependency on specific method names
    private void TryBridgeLog(string level, string category, string message, string tradeId = "", string errorCode = "", string baseId = "")
        {
            try
            {
                // Find NTGrpcClient.TradingGrpcClient type in loaded assemblies
                var assemblies = global::System.AppDomain.CurrentDomain.GetAssemblies();
                global::System.Type targetType = null;
                foreach (var asm in assemblies)
                {
                    try
                    {
                        var t = asm.GetType("NTGrpcClient.TradingGrpcClient", throwOnError: false);
                        if (t != null) { targetType = t; break; }
                    }
                    catch { /* ignore */ }
                }
                if (targetType == null) return;

                var flags = global::System.Reflection.BindingFlags.Public | global::System.Reflection.BindingFlags.Static;

                // Prefer the generic Log(level, component, message, tradeId, errorCode)
                var genericLog = targetType.GetMethod("Log", flags);
                if (genericLog != null)
                {
                    var args = new object[] { level ?? "INFO", category ?? "nt_addon", message ?? string.Empty, tradeId ?? string.Empty, errorCode ?? string.Empty, baseId ?? string.Empty };
                    try { genericLog.Invoke(null, args); } catch { }
                    return;
                }

                // Fallback to specific method names if present
                string methodName;
                var lvl = (level ?? "").ToUpperInvariant();
                if (lvl == "ERROR") methodName = "LogError";
                else if (lvl == "WARN" || lvl == "WARNING") methodName = "LogWarn";
                else if (lvl == "INFO") methodName = "LogInfo";
                else methodName = "LogDebug";

                var specific = targetType.GetMethod(methodName, flags);
                if (specific != null)
                {
                    var ps = specific.GetParameters();
                    try
                    {
                        if (ps.Length >= 5)
                        {
                            specific.Invoke(null, new object[] { category ?? "nt_addon", message ?? string.Empty, tradeId ?? string.Empty, errorCode ?? string.Empty, baseId ?? string.Empty });
                        }
                        else if (ps.Length == 4)
                        {
                            specific.Invoke(null, new object[] { category ?? "nt_addon", message ?? string.Empty, tradeId ?? string.Empty, errorCode ?? string.Empty });
                        }
                        else if (ps.Length == 3)
                        {
                            specific.Invoke(null, new object[] { category ?? "nt_addon", message ?? string.Empty, tradeId ?? string.Empty });
                        }
                        else if (ps.Length == 2)
                        {
                            specific.Invoke(null, new object[] { category ?? "nt_addon", message ?? string.Empty });
                        }
                        else
                        {
                            // Unknown signature - skip
                        }
                    }
                    catch { }
                }
            }
            catch { /* swallow logging errors */ }
        }

        // Helper method to convert NT output calls to centralized logging
        private void LogToSystem(string message, string category = "SYSTEM")
        {
            // Determine log level from message content
            string logLevel = "INFO";
            if (message.Contains("ERROR") || message.Contains("Exception") || message.Contains("Failed") ||
                message.Contains("Error") || message.Contains("error"))
            {
                logLevel = "ERROR";
            }
            else if (message.Contains("WARNING") || message.Contains("Warning") || message.Contains("WARN") ||
                     message.Contains("warning") || message.Contains("warn"))
            {
                logLevel = "WARN";
            }
            else if (message.Contains("DEBUG") || message.Contains("Debug") || message.Contains("debug"))
            {
                logLevel = "DEBUG";
            }

            NinjaTrader.Code.Output.Process($"[NT_ADDON][{logLevel}][{category}] {message}", PrintTo.OutputTab1);
        }

        // Helper method for quick conversion of existing NT output calls
        private void LogNT(string message)
        {
            // Extract category from message prefix if available
            string category = "SYSTEM";
            if (message.StartsWith("[MultiStratManager]")) category = "ADDON";
            else if (message.Contains("EXECUTION")) category = "EXECUTION";
            else if (message.Contains("TRADING")) category = "TRADING";
            else if (message.Contains("UI") || message.Contains("Window")) category = "UI";
            else if (message.Contains("HTTP") || message.Contains("Bridge")) category = "CONNECTION";
            else if (message.Contains("SLTP")) category = "SLTP";
            else if (message.Contains("ELASTIC")) category = "ELASTIC";
            else if (message.Contains("TRAILING")) category = "TRAILING";

            LogToSystem(message, category);
        }

        // Queue log message for batched sending
        // ===== Auto-logging methods removed - using direct NinjaScript output only =====
        
        public void SetMonitoredAccount(Account account)
        {
        // Unsubscribe from previous account if necessary
        if (monitoredAccount != null)
        {
            monitoredAccount.ExecutionUpdate -= OnExecutionUpdate;
            monitoredAccount.OrderUpdate    -= Account_OrderUpdate;
            monitoredAccount.AccountItemUpdate -= OnAccountItemUpdate; // Unsubscribe AccountItemUpdate
            
            // Stop elastic monitoring timer if running
            trailingAndElasticManager?.StopElasticMonitoring();
            
            LogInfo("SYSTEM", $"[MultiStratManager] Unsubscribed from events for account {monitoredAccount.Name}");
        }

        monitoredAccount = account;

        // Subscribe to new account if not null
        if (monitoredAccount != null)
        {
            // CRITICAL: Force log account details for debugging
            LogAndPrint($"ACCOUNT_SET: Setting monitored account to '{monitoredAccount.Name}' (DisplayName: '{monitoredAccount.DisplayName}')");
            LogAndPrint($"ACCOUNT_SET: Account connection state: {monitoredAccount.ConnectionStatus}");

            monitoredAccount.ExecutionUpdate += OnExecutionUpdate;
            monitoredAccount.OrderUpdate    += Account_OrderUpdate;
            monitoredAccount.AccountItemUpdate += OnAccountItemUpdate; // Subscribe AccountItemUpdate

            LogAndPrint($"ACCOUNT_SET: Successfully subscribed to ExecutionUpdate events for account '{monitoredAccount.Name}'");
            LogInfo("SYSTEM", $"[MultiStratManager] Subscribed to events for account {monitoredAccount.Name}");

            // Initialize PnL values.
            var realizedItemArgs = monitoredAccount.GetAccountItem(Cbi.AccountItem.RealizedProfitLoss, Currency.UsDollar);
            if (realizedItemArgs != null && realizedItemArgs.Value is double) // Assuming GetAccountItem returns AccountItemEventArgs here based on CS0029
                RealizedPnL = (double)realizedItemArgs.Value;

            var unrealizedItemArgs = monitoredAccount.GetAccountItem(Cbi.AccountItem.UnrealizedProfitLoss, Currency.UsDollar);
            if (unrealizedItemArgs != null && unrealizedItemArgs.Value is double) // Assuming GetAccountItem returns AccountItemEventArgs here based on CS0029
                UnrealizedPnL = (double)unrealizedItemArgs.Value;
            // TotalPnL is updated automatically via setters of RealizedPnL/UnrealizedPnL

            // Initialize session tracking for elastic hedging
            InitializeSessionTracking();
            
            // Initialize elastic hedging monitor now that we have a monitored account
            if (trailingAndElasticManager != null)
            {
                trailingAndElasticManager.InitializeElasticMonitoring(monitoredAccount);
            }
        }
        else
        {
            LogInfo("SYSTEM", $"[MultiStratManager] Monitored account set to null. PnL tracking stopped.");
            // Reset PnL values
            RealizedPnL = 0;
            UnrealizedPnL = 0;
            // TotalPnL is updated automatically
        }
    }

    private void OnAccountItemUpdate(object sender, AccountItemEventArgs e)
    {
        _ = sender; // Suppress unused parameter warning
        if (e.Account == null || monitoredAccount == null || e.Account.Name != monitoredAccount.Name)
            return;

        bool pnlChanged = false;

        if (e.AccountItem == Cbi.AccountItem.RealizedProfitLoss)
        {
            if (e.Value is double realizedValue)
            {
                if (RealizedPnL != realizedValue)
                {
                    RealizedPnL = realizedValue;
                    pnlChanged = true;
                }
            }
        }
        else if (e.AccountItem == Cbi.AccountItem.UnrealizedProfitLoss)
        {
            if (e.Value is double unrealizedValue)
            {
                if (UnrealizedPnL != unrealizedValue)
                {
                    UnrealizedPnL = unrealizedValue;
                    pnlChanged = true;
                }
            }
        }

        if (pnlChanged)
        {
            // Assuming RealizedPnL and UnrealizedPnL setters call OnPropertyChanged for themselves.
            // TotalPnL is updated here, and OnPropertyChanged is called for it.
            TotalPnL = RealizedPnL + UnrealizedPnL;
            OnPropertyChanged(nameof(TotalPnL));
        }
    }

    private void OnExecutionUpdate(object sender, ExecutionEventArgs e)
    {
        try
        {
            // BUY_DEBUG: Log ALL executions to debug buy trade processing
            if (e?.Execution?.Order != null)
            {
                LogAndPrint($"BUY_DEBUG: EXECUTION RECEIVED - OrderAction: {e.Execution.Order.OrderAction}, Account: {e.Execution.Account?.Name ?? "null"}, Instrument: {e.Execution.Instrument?.FullName ?? "null"}, Quantity: {e.Execution.Quantity}");
            }

            // Only log significant events, not every execution

            // Ensure the execution is for the monitored account
            if (monitoredAccount == null)
            {
                LogError("EXECUTION", "monitoredAccount is null, skipping execution");
                return;
            }

            if (e.Execution.Account.Name != monitoredAccount.Name)
            {
                // BUY_DEBUG: Log account mismatches for debugging
                LogAndPrint($"BUY_DEBUG: ACCOUNT_MISMATCH - Execution Account: '{e.Execution.Account.Name}', Monitored Account: '{monitoredAccount.Name}', OrderAction: {e.Execution.Order?.OrderAction}");
                // Account mismatch - silent skip
                return;
            }
            
            // Processing execution for monitored account

            // EXECUTION DEDUPLICATION: Check if this execution has already been processed
            string executionId = e.Execution.ExecutionId;
            if (!string.IsNullOrEmpty(executionId))
            {
                lock (executionTrackingLock)
                {
                    if (processedExecutionIds.Contains(executionId))
                    {
                        LogAndPrint($"EXECUTION_DEDUP: Skipping duplicate execution {executionId} - already processed");
                        return;
                    }
                    
                    // Add to processed set to prevent future duplicates
                    processedExecutionIds.Add(executionId);
                    LogAndPrint($"EXECUTION_DEDUP: Processing execution {executionId} (first time seen)");
                }
            }

            // BIDIRECTIONAL_HEDGE_FIX: Skip processing executions of hedge closing orders
            // These orders are responses to MT5 notifications and should NOT trigger additional CLOSE_HEDGE commands
            if (e.Execution?.Order != null && trackedHedgeClosingOrderIds != null &&
                trackedHedgeClosingOrderIds.Contains(e.Execution.Order.Id.ToString()))
            {
                LogAndPrint($"HEDGE_CLOSURE_SKIP: Skipping execution processing for hedge closing order {e.Execution.Order.Id} ('{e.Execution.Order.Name}'). This prevents incorrect FIFO-based BaseID selection.");
                return;
            }

            // OFFICIAL NINJATRADER BEST PRACTICE: Comprehensive Trade Classification
            bool isClosingExecution = DetectTradeClosureByExecution(e);
            bool isNewEntryTrade = IsNewEntryTrade(e);

            LogAndPrint($"TRADE_CLASSIFICATION: Closure={isClosingExecution}, Entry={isNewEntryTrade}, OrderAction={e.Execution.Order.OrderAction}, OrderName={e.Execution.Order.Name ?? "null"}");

            if (isClosingExecution)
            {
                LogAndPrint($"PROCESSING_CLOSURE: Handling trade closure execution via HandleTradeClosureExecution");
                HandleTradeClosureExecution(e);
                return; // Don't process as new trade
            }

            if (!isNewEntryTrade)
            {
                // Not a closure and not a new entry - ignoring
                return; // Don't process executions that are neither closures nor new entries
            }

            LogAndPrint($"PROCESSING_NEW_ENTRY: Handling new entry trade execution");

// Call SLTP Removal Logic
            LogForSLTP($"OnExecutionUpdate: EnableSLTPRemoval is {{EnableSLTPRemoval}}.", LogLevel.Information);
            if (sltpRemovalLogic == null)
            {
                LogForSLTP("OnExecutionUpdate: sltpRemovalLogic is null. SLTP removal will be skipped.", LogLevel.Warning);
            }
            if (sltpRemovalLogic != null && e.Execution != null && e.Execution.Account != null)
            {
                // Calling SLTP removal logic
                sltpRemovalLogic.HandleExecutionUpdate(
                    e.Execution,
                    this.EnableSLTPRemoval,
                    this.SLTPRemovalDelaySeconds,
                    e.Execution.Account
                );
            }
            // Log only fills
            if (e.Execution != null && e.Execution.Quantity > 0)
            {
                // Ensure Order is not null before accessing its properties for logging
                if (e.Execution != null && e.Execution.Order != null)
                {
                    LogDebug("EXECUTION", String.Format("[MultiStratManager] DEBUG Addon: Execution Details - ID: {0}, Order.Filled: {1}, Execution.Quantity: {2}, Order.Quantity: {3}, MarketPosition: {4}",
                        e.Execution.ExecutionId, // Using ExecutionId as OrderId might be the base order id for multiple fills
                        e.Execution.Order.Filled,
                        e.Execution.Quantity,
                        e.Execution.Order.Quantity,
                        e.Execution.MarketPosition
                    ));
                    
                    // Track MT5_CLOSE order executions specifically
                    if (!string.IsNullOrEmpty(e.Execution.Order.Name) && e.Execution.Order.Name.StartsWith("MT5_CLOSE_"))
                    {
                        LogInfo("GRPC", $"MT5_CLOSE_EXECUTION: Order '{e.Execution.Order.Name}' EXECUTED - Quantity: {e.Execution.Quantity}, Price: {e.Execution.Price}, MarketPosition: {e.Execution.MarketPosition}");
                        LogInfo("GRPC", $"MT5_CLOSE_EXECUTION: Order State: {e.Execution.Order.OrderState}, Order Action: {e.Execution.Order.OrderAction}");
                    }
                }
                else
                {
                    LogDebug("EXECUTION", $"[MultiStratManager] Received Execution Fill (Order details partially unavailable): {e.Execution}");
                }
                
                // CONSISTENT_BASEID_FIX: Instead of generating a new baseId for every execution,
                // check if this is part of an existing trade group using the same Order
                string baseId = null;
                string originalOrderId = e.OrderId;
                
                // First, check if we already have a baseId mapping for this OrderId
                if (orderIdToBaseIdMap.TryGetValue(originalOrderId, out string existingBaseId))
                {
                    baseId = existingBaseId;
                    LogAndPrint($"BASEID_REUSE: Using existing BaseID {baseId} for OrderID {originalOrderId}");
                }
                else
                {
                    // Generate a new baseId only if this is truly a new order
                    baseId = GenerateSimpleBaseId();
                    
                    // Store mapping between simple baseID and original NT OrderId for closure detection
                    baseIdToOrderIdMap.TryAdd(baseId, originalOrderId);
                    orderIdToBaseIdMap.TryAdd(originalOrderId, baseId);
                    LogAndPrint($"BASEID_NEW: Created new mapping - BaseID: {baseId} <-> OrderID: {originalOrderId}");
                }

                // MULTI_TRADE_GROUP_FIX: Store original trade info and handle multiple trades with same BaseID
                if (e.Execution.Order != null && e.Execution.Order.OrderState == OrderState.Filled)
                {
                    if (!string.IsNullOrEmpty(baseId))
                    {
                        lock (_activeNtTradesLock)
                        {
                            if (activeNtTrades.ContainsKey(baseId))
                            {
                                // MULTI_TRADE_GROUP_FIX: BaseID already exists, increment quantities
                                var existingTrade = activeNtTrades[baseId];
                                existingTrade.TotalQuantity += (int)e.Execution.Order.Quantity;
                                existingTrade.RemainingQuantity += (int)e.Execution.Order.Quantity;
                                LogAndPrint($"MULTI_TRADE_GROUP: Updated existing BaseID {baseId}. Total: {existingTrade.TotalQuantity}, Remaining: {existingTrade.RemainingQuantity}");
                            }
                            else
                            {
                                // MULTI_TRADE_GROUP_FIX: New BaseID, create new entry
                                var tradeInfo = new OriginalTradeDetails
                                {
                                    MarketPosition = e.Execution.MarketPosition,
                                    Quantity = (int)e.Execution.Order.Quantity,
                                    NtInstrumentSymbol = e.Execution.Instrument.FullName,
                                    NtAccountName = e.Execution.Account.Name,
                                    OriginalOrderAction = e.Execution.Order.OrderAction,
                                    TotalQuantity = (int)e.Execution.Order.Quantity,
                                    RemainingQuantity = (int)e.Execution.Order.Quantity
                                };

                                if (activeNtTrades.TryAdd(baseId, tradeInfo))
                                {
                                    LogDebug("TRADING", $"[MultiStratManager] Stored original trade info for base_id: {baseId}, Position: {tradeInfo.MarketPosition}, Qty: {tradeInfo.Quantity}, Action: {tradeInfo.OriginalOrderAction}");
                                    LogAndPrint($"ACTIVE_TRADES_ADD: Added base_id {baseId} to activeNtTrades. Total entries: {activeNtTrades.Count}");
                                }
                                else
                                {
                                    LogAndPrint($"ACTIVE_TRADES_ADD_FAILED: Failed to add base_id {baseId} to activeNtTrades (race condition)");
                                }
                            }
                        }
                    }
                }

                // Update trade result tracking for elastic hedging
                UpdateTradeResult(e);

                // --- OPTIMIZED CLOSURE DETECTION: Call FindOriginalTradeBaseId only once ---
                string originalBaseId = FindOriginalTradeBaseId(e);
                bool isClosingTrade = !string.IsNullOrEmpty(originalBaseId);

                LogAndPrint($"FIFO_CLOSURE_CHECK: FindOriginalTradeBaseId returned '{originalBaseId}', isClosingTrade={isClosingTrade}");

                if (isClosingTrade)
                {
                    // CRITICAL FIX: Only send closure notification if this trade actually has an active hedge
                    // Check if this base_id was ever sent to MT5 and potentially has an active hedge
                    bool hasActiveHedge = activeNtTrades.ContainsKey(originalBaseId);

                    if (hasActiveHedge)
                    {
                        // This is a closing trade with an active hedge - send hedge closure notification
                        LogAndPrint($"NT_CLOSURE_DETECTED: Execution {e.Execution.ExecutionId} is closing trade for BaseID={originalBaseId} which has an active hedge. Sending hedge closure notification to MT5.");

                        try
                        {
                            // Send hedge closure notification to MT5 via bridge
                            // MT5 EA expects a trade message with action="CLOSE_HEDGE" for processing
                            // Look up MT5 ticket for reliable closure
                            ulong mt5Ticket = 0;
                            // Retry logic for stress testing race conditions
            for (int retry = 0; retry < 3; retry++)
            {
                if (baseIdToMT5Ticket.TryGetValue(originalBaseId, out mt5Ticket) && mt5Ticket > 0)
                    break;
                if (retry < 2) Thread.Sleep(5); // Brief delay
            }
            
            if (mt5Ticket > 0)
                            {
                                LogAndPrint($"CLOSURE_TICKET: Found MT5 ticket {mt5Ticket} for BaseID {originalBaseId}");
                            }
                            else
                            {
                                LogAndPrint($"CLOSURE_TICKET: No MT5 ticket found for BaseID {originalBaseId}, will use comment matching");
                            }
                            
                            var closureData = new Dictionary<string, object>
                            {
                                { "action", "CLOSE_HEDGE" },  // MT5 EA looks for this specific action
                                { "base_id", originalBaseId },
                                { "quantity", (float)e.Execution.Quantity },
                                // gRPC hedge-close fields expected by JsonToProtoHedgeClose
                                { "nt_instrument_symbol", e.Execution.Instrument.FullName },
                                { "nt_account_name", e.Execution.Account.Name },
                                { "closed_hedge_quantity", (double) e.Execution.Quantity },
                                { "closed_hedge_action", "CLOSE_HEDGE" },
                                { "timestamp", e.Execution.Time.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture) },
                                { "price", 0.0 },  // Not critical for closure
                                { "total_quantity", (float)e.Execution.Quantity },
                                { "contract_num", 1 },
                                { "instrument_name", e.Execution.Instrument.FullName },
                                { "account_name", e.Execution.Account.Name },
                                { "time", e.Execution.Time.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture) },
                                { "nt_balance", 0 },  // Not critical for closure
                                { "nt_daily_pnl", 0 },  // Not critical for closure
                                { "nt_trade_result", "closed" },
                                { "nt_session_trades", 0 },
                                { "closure_reason", "NT_ORIGINAL_TRADE_CLOSED" }, // This will NOT trigger whack-a-mole
                                { "mt5_ticket", mt5Ticket } // Include MT5 ticket for reliable closure
                            };

                            string closureJson = SimpleJson.SerializeObject(closureData);
                            LogAndPrint($"NT_CLOSURE: Sending hedge closure notification: {closureJson}");

                            // Send to bridge's hedge closure endpoint
                            Task.Run(() => SendHedgeClosureNotification(closureData));

                            // CRITICAL FIX: Don't immediately remove from activeNtTrades - mark as closed instead
                            // This prevents race conditions where MT5 closure notifications can't find the base_id
                            if (activeNtTrades.TryGetValue(originalBaseId, out var tradeDetails))
                            {
                                // Mark as closed but keep in tracking for MT5 closure coordination
                                tradeDetails.IsClosed = true;
                                tradeDetails.ClosedTimestamp = DateTime.UtcNow;
                                LogAndPrint($"NT_CLOSURE: Marked trade {originalBaseId} as closed in activeNtTrades tracking. Will cleanup later. Total entries: {activeNtTrades.Count}");
                                
                                // Schedule cleanup after delay to allow MT5 notifications to process
                                Task.Run(async () =>
                                {
                                    await Task.Delay(5000); // Wait 5 seconds for any pending MT5 notifications
                                    if (activeNtTrades.TryRemove(originalBaseId, out _))
                                    {
                                        LogAndPrint($"DELAYED_CLEANUP: Removed closed trade {originalBaseId} from activeNtTrades tracking. Remaining entries: {activeNtTrades.Count}");
                                    }
                                });
                            }
                            else
                            {
                                LogAndPrint($"NT_CLOSURE: Trade {originalBaseId} not found in activeNtTrades (may have been removed by hedge closure)");
                            }
                        }
                        catch (Exception ex_closure)
                        {
                            LogAndPrint($"ERROR: Exception sending hedge closure notification: {ex_closure.Message}");
                        }
                    }
                    else
                    {
                        // This is a closing trade but no active hedge exists - skip closure notification
                        LogAndPrint($"NT_CLOSURE_SKIPPED: Execution {e.Execution.ExecutionId} is closing trade for BaseID={originalBaseId} but no active hedge exists. Skipping closure notification to prevent noise.");
                        LogAndPrint($"NT_CLOSURE_SKIPPED: This trade was likely from a previous session or never had a hedge opened. No action needed.");
                    }

                    return; // Don't process as new trade regardless
                }
                else
                {
                    // This is an entry trade - send trade data
                    // FIXED: Send ONLY ONE message per execution to prevent duplicate trades
                    string jsonData = null; // To store serialized tradeData for logging in case of bridge error

                    try
                    {
                        int executionQuantity = e.Execution.Quantity;
                        int totalOrderQuantity = (e.Execution.Order != null) ? (int)e.Execution.Order.Quantity : executionQuantity;
                        
                        LogAndPrint($"SINGLE_MESSAGE_FIX: Processing execution - ExecutionQuantity={executionQuantity}, TotalOrderQuantity={totalOrderQuantity}");
                        
                        // CRITICAL FIX: Send ONLY ONE message per execution with full quantity
                        // This prevents the "whack-a-mole" duplication issue
                        string executionIdForSend = e.Execution.ExecutionId;
                        bool alreadySent = false;
                        
                        lock (executionTrackingLock)
                        {
                            string sentKey = $"SENT_{executionIdForSend}";
                            if (processedExecutionIds.Contains(sentKey))
                            {
                                alreadySent = true;
                                LogAndPrint($"TRADE_DEDUP: ExecutionId {executionIdForSend} already sent to bridge - skipping duplicate submission");
                            }
                            else
                            {
                                processedExecutionIds.Add(sentKey);
                                LogAndPrint($"TRADE_DEDUP: Sending ExecutionId {executionIdForSend} to bridge (first time) - Quantity={executionQuantity}");
                            }
                        }
                        
                        if (!alreadySent)
                        {
                            // Get the correct contract number for this execution
                            int contractNumber = GetContractNumberForExecution(e.Execution.Order?.OrderId ?? "", totalOrderQuantity);
                            
                            var tradeData = new Dictionary<string, object>
                            {
                                { "id", $"{e.Execution.ExecutionId}_C1" }, // Single message ID
                                { "base_id", baseId },
                                { "time", e.Execution.Time.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture) },
                                { "action", e.Execution.Order.OrderAction.ToString() },
                                { "quantity", (float)executionQuantity }, // Send actual execution quantity
                                { "price", (float)e.Execution.Price },
                                { "total_quantity", totalOrderQuantity }, // Total order quantity
                                { "contract_num", contractNumber }, // Sequential contract number
                                { "instrument_name", e.Execution.Instrument.FullName },
                                { "account_name", e.Execution.Account.Name },

                                // Enhanced NT Performance Data for Elastic Hedging
                                { "nt_balance", (float)_sessionStartBalance },
                                { "nt_daily_pnl", (float)DailyPnL },
                                { "nt_trade_result", _lastTradeResult },
                                { "nt_session_trades", _sessionTradeCount }
                            };

                            // Inject nt_points_per_1k_loss hint for MT5 elastic sizing.
                            // Assumption: 1 NT point corresponds to MasterInstrument.PointValue dollars per contract.
                            // Therefore, NT points for $1000 loss = 1000 / pointValue.
                            try
                            {
                                double pointValue = e.Execution.Instrument?.MasterInstrument?.PointValue ?? 0.0;
                                if (pointValue > 0)
                                {
                                    double ntPointsPer1k = 1000.0 / pointValue;
                                    tradeData["nt_points_per_1k_loss"] = ntPointsPer1k;
                                    LogAndPrint($"ELASTIC_HINT_EMIT: nt_points_per_1k_loss={ntPointsPer1k:F2} (pointValue={pointValue:F2}) for {e.Execution.Instrument?.FullName}");
                                }
                                else
                                {
                                    LogAndPrint($"ELASTIC_HINT_EMIT: Skipped nt_points_per_1k_loss (pointValue invalid for {e.Execution.Instrument?.FullName})");
                                }
                            }
                            catch (Exception ehEx)
                            {
                                LogAndPrint($"ELASTIC_HINT_EMIT_ERROR: {ehEx.Message}");
                            }

                            if (e.Execution.Order != null && !string.IsNullOrEmpty(e.Execution.Order.Name))
                            {
                                if (e.Execution.Order.Name.Contains("TP")) tradeData["order_type"] = "TP";
                                else if (e.Execution.Order.Name.Contains("SL")) tradeData["order_type"] = "SL";
                            }

                            jsonData = SimpleJson.SerializeObject(tradeData);
                            LogDebug("CONNECTION", String.Format("[MultiStratManager] DEBUG Addon: JSON to Bridge: {0}", jsonData));

                            // Explicit emission log to confirm new entry submission path and action
                            var actionStr = tradeData.ContainsKey("action") ? (tradeData["action"]?.ToString() ?? "") : "";
                            var qtyVal = tradeData.ContainsKey("quantity") ? (float)tradeData["quantity"] : 0f;
                            var priceVal = tradeData.ContainsKey("price") ? (float)tradeData["price"] : 0f;
                            LogAndPrint($"ENTRY_EMIT: Submitting new entry to Bridge - Action={actionStr}, BaseID={baseId}, Instrument={e.Execution.Instrument.FullName}, Account={e.Execution.Account.Name}, Qty={qtyVal}, Price={priceVal}");

                            // Send trade data to bridge via gRPC
                            Task.Run(async () => await SendToBridge(tradeData));
                            
                            LogAndPrint($"SINGLE_MESSAGE_FIX: Successfully sent ONE message for execution {e.Execution.ExecutionId} with quantity {executionQuantity}");
                        }
                        else
                        {
                            LogAndPrint($"SINGLE_MESSAGE_FIX: Skipped duplicate execution {e.Execution.ExecutionId}");
                        }

                        // WebSocket removed - using gRPC only for real-time processing
                        
                        // Add elastic position tracking for new entry
                        LogAndPrint($"ELASTIC_DEBUG: Checking elastic tracking - EnableElasticHedging: {EnableElasticHedging}, Order: {e.Execution.Order != null}, Execution.Quantity: {e.Execution.Quantity}");
                        
                        if (EnableElasticHedging && e.Execution.Order != null && e.Execution.Quantity > 0)
                        {
                            LogAndPrint($"ELASTIC_DEBUG: Attempting to add elastic tracking for baseId: {baseId}");
                            
                            // Find the position for this instrument
                            LogAndPrint($"ELASTIC_DEBUG: Looking for position with instrument: {e.Execution.Instrument.FullName}");
                            LogAndPrint($"ELASTIC_DEBUG: Available positions in account:");
                            foreach (var pos in monitoredAccount.Positions)
                            {
                                LogAndPrint($"ELASTIC_DEBUG: - Position: {pos.Instrument.FullName}, MarketPosition: {pos.MarketPosition}, Quantity: {pos.Quantity}");
                            }
                            
                            var position = monitoredAccount.Positions.FirstOrDefault(p => 
                                p.Instrument.FullName == e.Execution.Instrument.FullName);
                            
                            if (position != null)
                            {
                                double execCurrentPrice = GetCurrentPrice(position.Instrument);
                                LogAndPrint($"ELASTIC_DEBUG: Found position for tracking - Instrument: {position.Instrument.FullName}, MarketPosition: {position.MarketPosition}, Current P&L: ${position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, execCurrentPrice):F2}");
                                trailingAndElasticManager?.AddElasticPositionTracking(baseId, position, e.Execution.Price);
                            }
                            else
                            {
                                LogAndPrint($"ELASTIC_DEBUG: No position found for instrument {e.Execution.Instrument.FullName}");
                                LogAndPrint($"ELASTIC_DEBUG: Creating tracking anyway with execution data");
                                
                                // Delegate manual tracker creation to TrailingAndElasticManager
                                trailingAndElasticManager?.AddElasticPositionTrackingFromExecution(baseId, e.Execution);
                            }
                        }
                        else
                        {
                            LogAndPrint($"ELASTIC_DEBUG: NOT adding elastic tracking - conditions not met");
                        }
                    }
                    catch (Exception ex_bridge)
                    {
                         LogError("CONNECTION", $"ERROR: [MultiStratManager] Exception sending data to bridge: {ex_bridge.Message} | URL: {this.grpcServerAddress} | Data: {jsonData} | StackTrace: {ex_bridge.StackTrace}");
                         // Do not re-throw, allow ExecutionUpdate to complete
                    }
                }
            }
        }
        catch (Exception ex) // Outer catch for the entire handler
        {
            LogAndPrint($"ERROR: [MultiStratManager] Unhandled exception in ExecutionUpdate handler: {ex.Message} | StackTrace: {ex.StackTrace} | InnerException: {ex.InnerException?.Message}");
        }
    }

    // This method replaces the problematic override that caused CS0115.
    // This is the handler for monitoredAccount.OrderUpdate.
    // Ensure SetMonitoredAccount (lines 483-506) correctly subscribes Account_OrderUpdate.
    private void Account_OrderUpdate(object sender, NinjaTrader.Cbi.OrderEventArgs e)
{
    LogAndPrint($"ORDER_UPDATE_TRACE: OrderId={e.Order?.OrderId}, State={e.OrderState}, FilledThisUpdate={e.Filled}, TotalOrderFilled={e.Order?.Filled}, AvgFillPriceThisUpdate={e.AverageFillPrice}, TotalOrderAvgFillPrice={e.Order?.AverageFillPrice}, QtyThisUpdate={e.Quantity}, LimitPrice={e.LimitPrice}, StopPrice={e.StopPrice}, Time={e.Time}");

    if (e.Order == null)
    {
        LogAndPrint("ORDER_UPDATE_ERROR: e.Order is null, exiting.");
        return;
    }

    Order order = e.Order;
    long unfilledQuantity = order.Quantity - order.Filled; // Correctly calculate unfilled quantity

    // Log with corrected unfilled quantity
    LogAndPrint($"ORDER_UPDATE_DETAILS: {order.Name} (ID: {order.Id}): State={e.OrderState}, FilledThisUpdate={e.Filled}, TotalOrderFilled={order.Filled}, Unfilled={unfilledQuantity}");

    // HEDGE CLOSE ORDER TRACKING: Check if this is a hedge close order and update tracking
    if (order.Name != null && order.Name.StartsWith("HEDGE_CLOSE_") && (e.OrderState == OrderState.Submitted || e.OrderState == OrderState.Accepted))
    {
        lock (trackedHedgeClosingOrderIds)
        {
            string orderIdStr = order.Id.ToString();
            // Remove any old tracking IDs that match this base_id pattern
            var oldTrackingIds = trackedHedgeClosingOrderIds.Where(id => id.StartsWith($"HEDGE_CLOSE_") && id.Contains(order.Name.Substring(12))).ToList();
            foreach (var oldId in oldTrackingIds)
            {
                trackedHedgeClosingOrderIds.Remove(oldId);
                LogAndPrint($"[HEDGE_CLOSE_TRACKING] Removed old tracking ID {oldId}");
            }
            // Add the real order ID for tracking
            trackedHedgeClosingOrderIds.Add(orderIdStr);
            LogAndPrint($"[HEDGE_CLOSE_TRACKING] Added real order ID {orderIdStr} for hedge close order {order.Name}");
        }
    }

    // CLOSURE DETECTION: Check if this filled order represents a trade closure
    if (e.OrderState == OrderState.Filled || e.OrderState == OrderState.PartFilled)
    {
        LogAndPrint($"ORDER_FILLED_DETECTED: Order {order.Id} ({order.Name}) filled - checking if this is a closure");
        
        // Check if this order ID maps to any active trade base IDs using our mapping
        string orderIdStr = order.Id.ToString();
        bool isKnownTradeId = false;
        string mappedBaseId = null;

        // First check if this OrderId maps to a baseId
        if (orderIdToBaseIdMap.TryGetValue(orderIdStr, out mappedBaseId))
        {
            // Check if the mapped baseId is still active
            lock (_activeNtTradesLock)
            {
                isKnownTradeId = activeNtTrades.ContainsKey(mappedBaseId);
                LogAndPrint($"CLOSURE_DEBUG: Order {orderIdStr} maps to BaseID {mappedBaseId}, isActive: {isKnownTradeId}");
            }
        }
        else
        {
            LogAndPrint($"CLOSURE_DEBUG: Order {orderIdStr} has no mapping to any BaseID");
        }
        
        if (isKnownTradeId)
        {
            // This filled order is closing a trade with the mapped baseID
            LogAndPrint($"CLOSURE_DETECTED_BY_MAPPING: Order {orderIdStr} is closing trade with BaseID {mappedBaseId}");
            string closedTradeBaseId = mappedBaseId;
            LogAndPrint($"CLOSURE_DETECTED_VIA_ORDER: Order {order.Id} closes trade {closedTradeBaseId} - sending closure request");
            
            // Send closure request to bridge
            int closureQuantity = (int)e.Filled;
            Task.Run(() => SendClosureToBridge(closedTradeBaseId, closureQuantity));
            
            // Update active trades tracking
            lock (_activeNtTradesLock)
            {
                if (activeNtTrades.TryGetValue(closedTradeBaseId, out var tradeDetails))
                {
                    tradeDetails.RemainingQuantity -= closureQuantity;
                    LogAndPrint($"CLOSURE_TRACKING: Reduced remaining quantity for {closedTradeBaseId} by {closureQuantity}. Remaining: {tradeDetails.RemainingQuantity}");
                    
                    if (tradeDetails.RemainingQuantity <= 0)
                    {
                        activeNtTrades.TryRemove(closedTradeBaseId, out _);

                        // Clean up baseID mappings
                        if (baseIdToOrderIdMap.TryRemove(closedTradeBaseId, out string removedOrderId))
                        {
                            orderIdToBaseIdMap.TryRemove(removedOrderId, out _);
                            LogAndPrint($"CLOSURE_COMPLETE: Removed mapping for BaseID {closedTradeBaseId} <-> OrderID {removedOrderId}");
                        }

                        LogAndPrint($"CLOSURE_COMPLETE: All trades closed for {closedTradeBaseId}. Removed from tracking.");
                    }
                }
            }
        }
        else
        {
            // Check using the standard closure detection logic
            string closedTradeBaseId = FindTradeBeingClosedByOrder(order);
            if (!string.IsNullOrEmpty(closedTradeBaseId))
            {
                LogAndPrint($"CLOSURE_DETECTED_VIA_ORDER: Order {order.Id} closes trade {closedTradeBaseId} - sending closure request");
                
                // Send closure request to bridge
                int closureQuantity = (int)e.Filled;
                Task.Run(() => SendClosureToBridge(closedTradeBaseId, closureQuantity));
                
                // Update active trades tracking
                lock (_activeNtTradesLock)
                {
                    if (activeNtTrades.TryGetValue(closedTradeBaseId, out var tradeDetails))
                    {
                        tradeDetails.RemainingQuantity -= closureQuantity;
                        LogAndPrint($"CLOSURE_TRACKING: Reduced remaining quantity for {closedTradeBaseId} by {closureQuantity}. Remaining: {tradeDetails.RemainingQuantity}");
                        
                        if (tradeDetails.RemainingQuantity <= 0)
                        {
                            activeNtTrades.TryRemove(closedTradeBaseId, out _);

                            // Clean up baseID mappings
                            if (baseIdToOrderIdMap.TryRemove(closedTradeBaseId, out string removedOrderId))
                            {
                                orderIdToBaseIdMap.TryRemove(removedOrderId, out _);
                                LogAndPrint($"CLOSURE_COMPLETE: Removed mapping for BaseID {closedTradeBaseId} <-> OrderID {removedOrderId}");
                            }

                            LogAndPrint($"CLOSURE_COMPLETE: All trades closed for {closedTradeBaseId}. Removed from tracking.");
                        }
                    }
                }
            }
        }
    }

    if (trackedHedgeClosingOrderIds.Contains(order.OrderId.ToString())) // Convert to string for HashSet
    {
        LogAndPrint($"Account_OrderUpdate: Tracking hedge closing order {order.OrderId}, Current State via e.OrderState: {e.OrderState}, via order.OrderState: {order.OrderState}");

        // Detailed logging for CancelPending or terminal states for HedgeClose_ orders
        if (e.Order.Name.StartsWith("HedgeClose_") &&
            (e.OrderState == OrderState.CancelPending || e.OrderState == OrderState.Filled || e.OrderState == OrderState.PartFilled || e.OrderState == OrderState.Cancelled || e.OrderState == OrderState.Rejected))
        {
            LogDebug("TRADING", $"[MultiStratManager DEBUG] HedgeClose_ Order Update: Name='{e.Order.Name}', ID(int)={e.Order.Id}, State='{e.OrderState}', Filled={e.Order.Filled}/{e.Order.Quantity}, ErrorCode='{e.Error}'");
        }

        if (e.OrderState == OrderState.Filled || e.OrderState == OrderState.PartFilled)
        {
            LogAndPrint($"Account_OrderUpdate: Hedge closing order {order.OrderId} received fill update. Filled this update: {e.Filled}, Total Order Filled: {order.Filled}");
            // If the order is terminally filled (fully filled), remove it from tracking.
            if ((order.OrderState == OrderState.Filled || order.OrderState == OrderState.Cancelled || order.OrderState == OrderState.Rejected) && order.Filled == order.Quantity)
            {
                trackedHedgeClosingOrderIds.Remove(order.OrderId);
                LogAndPrint($"Account_OrderUpdate: Fully filled hedge closing order {order.OrderId} removed from tracking.");
            }
        }
        else if ((order.OrderState == OrderState.Filled || order.OrderState == OrderState.Cancelled || order.OrderState == OrderState.Rejected)) // e.g. Cancelled, Rejected.
        {
            LogAndPrint($"Account_OrderUpdate: Hedge closing order {order.OrderId} is now terminal (State via e.OrderState: {e.OrderState}, via order.OrderState: {order.OrderState}). Removing from tracking.");
            trackedHedgeClosingOrderIds.Remove(order.OrderId);
        }
    }
    
    // MULTI_CONTRACT_FIX: Clean up contract tracking when orders reach terminal states
    if (order.OrderState == OrderState.Filled || order.OrderState == OrderState.Cancelled || order.OrderState == OrderState.Rejected)
    {
        string orderId = order.Id.ToString();
        if (orderContractCounts.ContainsKey(orderId))
        {
            orderContractCounts.TryRemove(orderId, out _);
            LogAndPrint($"CONTRACT_TRACKING_CLEANUP: Removed contract tracking for completed order {orderId} (State: {order.OrderState})");
        }
    }
    
    // Handle trailing stop orders (canonical prefix)
    if (order.Name != null && order.Name.StartsWith("MSM_TRAIL_STOP_"))
    {
        // Delegate trailing stop order handling to TrailingAndElasticManager
        trailingAndElasticManager?.HandleTrailingStopOrderUpdate(order);
    }
}

    // HTTP Listener removed - using gRPC streaming instead

    // HTTP listener methods removed - using gRPC streaming instead
    
    /// <summary>
    /// Handle hedge close notifications via gRPC stream (replaces HTTP HandleNotifyHedgeClosedRequest)
    /// </summary>
    private async Task HandleHedgeCloseNotificationAsync(string notification)
    {
        try
        {
            LogDebug("GRPC", "Received hedge close notification via gRPC stream");

            if (string.IsNullOrWhiteSpace(notification))
            {
                LogError("GRPC", "Received empty hedge close notification via gRPC");
                return;
            }

            LogDebug("GRPC", $"Hedge close notification: {notification}");

            // Parse the notification
            HedgeCloseNotification hedgeNotification = null;
            try
            {
                hedgeNotification = SimpleJson.DeserializeObject<HedgeCloseNotification>(notification);
            }
            catch (Exception ex)
            {
                LogError("GRPC", $"Failed to parse hedge close notification: {ex.Message}");
                return;
            }

            if (hedgeNotification == null || string.IsNullOrEmpty(hedgeNotification.base_id))
            {
                LogError("GRPC", "Invalid hedge close notification: missing base_id");
                return;
            }

            LogDebug("GRPC", $"[HEDGE_CLOSE_NOTIFICATION] Processing closure for BaseID: {hedgeNotification.base_id}");

            // Process the hedge close notification (same logic as before, but without HTTP response)
            await ProcessHedgeCloseNotificationInternal(hedgeNotification);
        }
        catch (Exception ex)
        {
            LogError("GRPC", $"Exception processing hedge close notification via gRPC: {ex.Message}");
        }
    }

    /// <summary>
    /// Internal processing of hedge close notifications (extracted from HTTP handler)
    /// </summary>
    private async Task ProcessHedgeCloseNotificationInternal(HedgeCloseNotification notification)
    {
        // CRITICAL: Verify this BaseID exists in our active trades before attempting to close
        lock (_activeNtTradesLock)
        {
            LogAndPrint($"[HEDGE_CLOSE_VERIFICATION] Checking if BaseID {notification.base_id} exists in active trades...");
            LogAndPrint($"[HEDGE_CLOSE_VERIFICATION] Current active trades: {string.Join(", ", activeNtTrades.Keys)}");
            
            if (!activeNtTrades.ContainsKey(notification.base_id))
            {
                LogAndPrint($"[HEDGE_CLOSE_REJECTION] BaseID {notification.base_id} not found in active trades. Ignoring hedge close notification.");
                return;
            }

            // Get trade details for verification
            var tradeDetails = activeNtTrades[notification.base_id];
            LogAndPrint($"[HEDGE_CLOSE_MATCH] Found matching trade - Symbol: {tradeDetails.NtInstrumentSymbol}, Account: {tradeDetails.NtAccountName}, Position: {tradeDetails.MarketPosition}, OriginalAction: {tradeDetails.OriginalOrderAction}");
        }

        // Process the hedge close notification
        string account = notification.nt_account_name;
        string symbol = notification.nt_instrument_symbol;

        if (string.IsNullOrEmpty(account) || string.IsNullOrEmpty(symbol))
        {
            LogAndPrint($"[HEDGE_CLOSE_ERROR] Missing required fields in notification. Account: {account}, Symbol: {symbol}");
            return;
        }

        LogAndPrint($"[HEDGE_CLOSE_PROCESSING] Processing closure for {symbol} on account {account}");

        // Find the account by name
        Account ntAccount = null;
        foreach (Account acc in Account.All)
        {
            if (acc.Name == account)
            {
                ntAccount = acc;
                break;
            }
        }

        if (ntAccount == null)
        {
            LogAndPrint($"[HEDGE_CLOSE_ERROR] Account '{account}' not found in NinjaTrader");
            return;
        }

    // Process position closure (do not remove tracking up-front; let execution updates reconcile)
    await ProcessPositionClosureForHedge(notification, ntAccount);
        
        LogAndPrint($"[HEDGE_CLOSE_COMPLETE] Hedge close notification processed for BaseID {notification.base_id} via gRPC");
    }

    /// <summary>
    /// Process actual position closure for hedge notifications
    /// </summary>
    private async Task ProcessPositionClosureForHedge(HedgeCloseNotification notification, Account ntAccount)
    {
        // Resolve the specific trade details for base_id
        OriginalTradeDetails tradeDetails = null;
        lock (_activeNtTradesLock)
        {
            if (!activeNtTrades.TryGetValue(notification.base_id, out tradeDetails))
            {
                LogAndPrint($"[HEDGE_CLOSE_MISSING] BaseID {notification.base_id} not in active trades when attempting close – may already be closed.");
            }
        }

        // Determine live NT position and cap close quantity accordingly
        var position = ntAccount.Positions.FirstOrDefault(p => p.Instrument.FullName == notification.nt_instrument_symbol);
        if (position == null || position.Quantity == 0)
        {
            LogAndPrint($"[HEDGE_CLOSE_NO_POSITION] No open position for {notification.nt_instrument_symbol} on {notification.nt_account_name} – nothing to close.");
            return;
        }

        // Special-cases for elastic-managed closures
        try
        {
            var reason = notification.ClosureReason ?? ExtractJsonValue(SimpleJson.SerializeObject(notification), "closure_reason");
            bool isElasticCompletion = !string.IsNullOrEmpty(reason) && reason.Equals("elastic_completion", StringComparison.OrdinalIgnoreCase);
            bool isElasticPartial = !string.IsNullOrEmpty(reason) && reason.Equals("elastic_partial_close", StringComparison.OrdinalIgnoreCase);
            // 1) elastic_partial_close: NEVER close the NT position; MT5 is reducing hedge size only
            if (isElasticPartial)
            {
                double lastPrice = GetCurrentPrice(position.Instrument);
                double unrealized = position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, lastPrice);
                LogAndPrint($"[ELASTIC_PARTIAL_SKIP] Skipping NT close for {notification.base_id} on partial hedge close (reason={reason}). NT Unrealized PnL=${unrealized:F2}");
                return;
            }

            // 2) elastic_completion: If trailing remains active and NT is in profit, keep NT open (skip close)
            if (isElasticCompletion)
            {
                // Check profit and trailing state via TrailingAndElasticManager
                double lastPrice = GetCurrentPrice(position.Instrument);
                double unrealized = position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, lastPrice);
                bool isTrailingActive = false;
                try
                {
                    var tracker = trailingAndElasticManager?.ElasticPositions?.ContainsKey(notification.base_id) == true
                        ? trailingAndElasticManager?.ElasticPositions[notification.base_id]
                        : null;
                    isTrailingActive = tracker?.IsTrailingActive == true;
                }
                catch { /* ignore */ }

                if (isTrailingActive && unrealized > 0)
                {
                    LogAndPrint($"[ELASTIC_COMPLETION_SKIP] Skipping NT close for {notification.base_id} because trailing is active and PnL=${unrealized:F2} > 0 (reason={reason})");
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            LogAndPrint($"[ELASTIC_COMPLETION_CHECK_ERROR] {ex.Message}");
        }

        // Compute desired close quantity based on tracked trade (fallback to 1 contract if unknown)
        int desiredQty = 0;
        OrderAction action = position.Quantity > 0 ? OrderAction.Sell : OrderAction.BuyToCover;
        if (tradeDetails != null)
        {
            // Use remaining quantity if tracked; else original quantity
            var remaining = tradeDetails.RemainingQuantity > 0 ? tradeDetails.RemainingQuantity : tradeDetails.Quantity;
            desiredQty = Math.Max(1, Math.Abs(remaining));

            // Ensure action matches the original trade direction when available
            action = tradeDetails.MarketPosition == MarketPosition.Long ? OrderAction.Sell : OrderAction.BuyToCover;
        }
        else
        {
            desiredQty = Math.Max(1, Math.Abs(position.Quantity));
        }

        // Cap by live position quantity to avoid over-close
        int closeQty = Math.Min(desiredQty, Math.Abs(position.Quantity));

        // Add to tracked closing orders to identify these as hedge-initiated closes
        string trackingId = $"HEDGE_CLOSE_{notification.base_id}_{DateTime.UtcNow.Ticks}";
        lock (trackedHedgeClosingOrderIds)
        {
            trackedHedgeClosingOrderIds.Add(trackingId);
            LogAndPrint($"[HEDGE_CLOSE_TRACKING] Added tracking ID {trackingId} for hedge-initiated closure of {notification.base_id}");
        }

        try
        {
            var instr = position.Instrument;
            var closingOrder = ntAccount.CreateOrder(
                instr,
                action,
                OrderType.Market,
                OrderEntry.Manual,
                TimeInForce.Day,
                closeQty,
                0,
                0,
                string.Empty,
                $"HEDGE_CLOSE_{notification.base_id}",
                default(DateTime),
                null
            );

            if (closingOrder != null)
            {
                LogAndPrint($"[HEDGE_CLOSE_ORDER] Created closing order for {instr.FullName}: {closingOrder.OrderAction} {closingOrder.Quantity} (desired {desiredQty}, capped by live {Math.Abs(position.Quantity)})");
                ntAccount.Submit(new[] { closingOrder });
                LogAndPrint($"[HEDGE_CLOSE_SUBMITTED] Submitted hedge closure order for BaseID {notification.base_id}");
            }
            else
            {
                LogAndPrint($"[HEDGE_CLOSE_ERROR] Failed to create closing order for {notification.nt_instrument_symbol}");
            }
        }
        catch (Exception ex)
        {
            LogAndPrint($"[HEDGE_CLOSE_EXCEPTION] Exception creating/submitting closure order: {ex.Message}");
        }
    }

    // HTTP ping handler removed - using gRPC health checks instead

    // HTTP hedge close handler removed - using gRPC stream instead

    /// <summary>
    /// Determines whether a closing order should be created based on the hedge closure reason.
    /// This prevents the whack-a-mole effect where EA-managed closures trigger unnecessary re-trading.
    /// </summary>
    /// <param name="closureReason">The closure reason from the MT5 EA</param>
    /// <returns>True if a closing order should be created, false otherwise</returns>
    private bool ShouldCreateClosingOrderForReason(string closureReason)
    {
        if (string.IsNullOrEmpty(closureReason))
        {
            // If no closure reason is provided, default to creating closing order for backward compatibility
            LogAndPrint("WARNING: No closure reason provided. Defaulting to creating closing order for backward compatibility.");
            return true;
        }

        // Define closure reasons that should NOT trigger re-trading (EA-managed closures)
        // These are internal EA operations that don't require NinjaTrader position closure
        // WHACK-A-MOLE FIX: Most EA closures should NOT trigger NT position closure
        var eaManagedClosureReasons = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Internal EA adjustment and rebalancing operations
            "EA_ADJUSTMENT_CLOSE",              // EA adjustment closure (internal rebalancing)
            "EA_INTERNAL_REBALANCE",            // EA internal rebalancing operations

            // Standard EA hedge management operations - these should NOT trigger NT closure
            "EA_PARALLEL_ARRAY_CLOSE",          // Standard EA closure due to parallel array management
            "EA_COMMENT_BASED_CLOSE",           // EA closure based on comment parsing
            "EA_RECONCILED_AND_CLOSED",         // EA closure when trade group is fully reconciled
            "EA_PARALLEL_ARRAY_ORPHAN_CLOSE",   // EA closure from parallel arrays but no group
            "EA_COMMENT_ORPHAN_CLOSE",          // EA closure from comment but no group
            "EA_OLD_MAP_FALLBACK_CLOSE",        // EA closure using old map fallback

            // EA automatic closure operations - these should NOT trigger NT closure
            "EA_GLOBALFUTURES_ZERO_CLOSE",      // EA closes hedge when globalFutures reaches zero (internal balancing)
            "EA_TRAILING_STOP_CLOSE",           // EA trailing stop triggered closure (EA-managed)
        };

        // Define closure reasons that SHOULD trigger re-trading (legitimate user-initiated closures)
        // ONLY when the user or external systems close MT5 hedges should NT positions also close
        var legitimateClosureReasons = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // User-initiated closures that should close NT positions
            "MANUAL_MT5_CLOSE",                 // Manual closure in MT5 platform by user
            "EA_MANUAL_CLOSE",                  // Manual closure through EA interface by user

            // User-set stop loss/take profit closures (not EA-managed)
            "USER_STOP_LOSS_CLOSE",             // User-set stop loss triggered
            "USER_TAKE_PROFIT_CLOSE",           // User-set take profit triggered

            // External system closures that should close NT positions
            "NT_ORIGINAL_TRADE_CLOSED",         // Original NinjaTrader trade was closed
            "BROKER_MARGIN_CALL",               // Broker-initiated closure
            "BROKER_STOP_OUT",                  // Broker stop-out

            // Legacy/unknown closures - default to closing for safety
            "UNKNOWN_MT5_CLOSE",                // Unknown MT5 closure (default EA reason) - safer to close NT position
            "EA_STOP_LOSS_CLOSE",               // Legacy - MT5 hedge closed by stop loss
            "EA_TAKE_PROFIT_CLOSE",             // Legacy - MT5 hedge closed by take profit
        };

        bool isEaManaged = eaManagedClosureReasons.Contains(closureReason);
        bool isLegitimate = legitimateClosureReasons.Contains(closureReason);

        if (isEaManaged)
        {
            // BIDIRECTIONAL HEDGING FIX: For bidirectional hedging, when MT5 closes a hedge,
            // we WANT to close the corresponding NT trade to maintain synchronization
            LogAndPrint($"CLOSURE_LOGIC: Reason '{closureReason}' is EA-managed. WILL create closing order for bidirectional hedging.");
            return true;  // Changed from false to true for bidirectional hedging
        }
        else if (isLegitimate)
        {
            LogAndPrint($"CLOSURE_LOGIC: Reason '{closureReason}' is legitimate. Will create closing order.");
            return true;
        }
        else
        {
            // Unknown closure reason - log warning and default to creating closing order for safety
            LogAndPrint($"WARNING: Unknown closure reason '{closureReason}'. Defaulting to creating closing order for safety.");
            return true;
        }
    }

    // HTTP response methods removed - using gRPC instead

    // HTTP error response method removed - using gRPC instead

    // HTTP listener removed - using gRPC streaming instead

    public async Task ForceGrpcReinitialization()
    {
        LogAndPrint("[NT_ADDON][INFO][GRPC] Forcing gRPC client re-initialization...");
        grpcInitialized = false;
        
        grpcInitializing = false; // Reset initializing flag
        
        // Stop heartbeat system before disconnection
        StopHeartbeatSystem();
        
        // Shutdown existing gRPC client if it exists
        try
        {
            TradingGrpcClient.Dispose();
        }
        catch (Exception ex)
        {
            LogAndPrint($"[NT_ADDON][DEBUG][GRPC] Error during gRPC disposal: {ex.Message}");
        }

        // Reinitialize on background thread to avoid UI blocking
        await InitializeGrpcClient();
    }

    public async Task<Tuple<bool, string>> PingBridgeAsync(string bridgeBaseUrl)
    {
        try
        {
            // Initialize gRPC if needed (on background thread)
            if (!grpcInitialized)
            {
                // Only initialize on explicit user action (UI open/ping button)
                if (IsUiOpen)
                    await Task.Run(() => InitializeGrpcClient());
                else
                    return Tuple.Create(false, "UI not open; connection is manual. Open Multi-Strategy Manager to connect.");
            }

            // Ping bridge via gRPC health check (removed spammy log - only logs when status changes)

            // Use gRPC health check (on background thread with timeout to avoid UI blocking)
            var healthResult = await Task.Run(() => {
                string responseJson;
                bool isHealthy = TradingGrpcClient.HealthCheck("NT_ADDON", out responseJson);
                return new { IsHealthy = isHealthy, ResponseJson = responseJson };
            });
            bool isHealthy = healthResult.IsHealthy;
            string responseJson = healthResult.ResponseJson;
            
            if (isHealthy)
            {
                // gRPC ping successful (removed spammy log - success is reported via status change only)
                return Tuple.Create(true, $"Bridge is healthy via gRPC: {responseJson}");
            }
            else
            {
                string error = TradingGrpcClient.LastError;
                LogError("CONNECTION", $"[MultiStratManager] gRPC ping failed: {error}");
                return Tuple.Create(false, $"gRPC ping failed: {error}");
            }
        }
        catch (Exception ex)
        {
            LogError("CONNECTION", $"[MultiStratManager] gRPC ping failed (Error): {ex.Message}");
            return Tuple.Create(false, $"gRPC ping failed: {ex.Message}");
        }
    }
    /// <summary>
    /// Registers a strategy for state monitoring.
    /// </summary>
    /// <param name="strategy">The strategy to monitor.</param>
    public static void RegisterStrategyForMonitoring(StrategyBase strategy)
    {
        if (strategy != null && !monitoredStrategies.Contains(strategy))
        {
            monitoredStrategies.Add(strategy);
            Instance?.LogInfo("SYSTEM", $"[MultiStratManager] Registered {strategy.Name} for state monitoring. Current state: {strategy.State}");
            // Optionally, immediately notify of current state
            // OnStrategyExternalStateChange?.Invoke(strategy, strategy.State);
        }
    }

    /// <summary>
    /// Unregisters a strategy from state monitoring.
    /// </summary>
    /// <param name="strategy">The strategy to unmonitor.</param>
    public static void UnregisterStrategyForMonitoring(StrategyBase strategy)
    {
        if (strategy != null && monitoredStrategies.Contains(strategy))
        {
            monitoredStrategies.Remove(strategy);
            Instance?.LogInfo("SYSTEM", $"[MultiStratManager] Unregistered {strategy.Name} from state monitoring.");
        }
    }

    /// <summary>
    /// Requests a state change for the specified strategy.
    /// This method handles enabling and disabling strategies by setting their state
    /// to Active or Terminated respectively.
    /// </summary>
    /// <param name="strategy">The strategy instance to modify.</param>
    /// <param name="newState">The desired state (State.Active to enable, State.Terminated to disable).</param>
    public static void RequestStrategyStateChange(NinjaTrader.NinjaScript.StrategyBase strategy, NinjaTrader.NinjaScript.State newState)
    {
        if (strategy == null)
        {
            Instance?.LogError("SYSTEM", "[MultiStratManager] RequestStrategyStateChange called with null strategy.");
            return;
        }

        // Validate that the requested state is one we expect for enabling/disabling
        if (newState != State.Active && newState != State.Terminated)
        {
            Instance?.LogError("SYSTEM", $"[MultiStratManager] RequestStrategyStateChange called with unexpected state: {newState}. Expected State.Active or State.Terminated.");
            return;
        }

        Instance?.LogInfo("SYSTEM", $"[MultiStratManager] Requesting state change for {strategy.Name} to {newState}");

        try
        {
            // ADDED CHECK: Prevent trying to set Terminated/Finalized to Active
            if (newState == State.Active && (strategy.State == State.Terminated || strategy.State == State.Finalized))
            {
                Instance?.LogWarn("SYSTEM", $"[MultiStratManager] Attempt to set strategy '{strategy.Name}' to Active from {strategy.State} state. This is not allowed by the API. Operation aborted.");
                return; // Abort the state change
            }

            // Check if the state change is actually needed
            if (strategy.State != newState)
            {
                // Execute the state change on the UI thread to ensure compatibility with NT core components
                Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        // Log before the state change
                        Instance?.LogDebug("SYSTEM", $"[MultiStratManager] Calling SetState({newState}) for {strategy.Name}. Current state: {strategy.State}");

                        // Perform the state change
                        strategy.SetState(newState);

                        // Log after the state change
                        Instance?.LogInfo("SYSTEM", $"[MultiStratManager] SetState({newState}) called successfully for {strategy.Name}. New state: {strategy.State}");

                        // Attempt to notify the Control Center to refresh its strategy display
                        // This is a best effort - the actual refresh mechanism depends on NinjaTrader's internal implementation
                        try
                        {
                            // Force a property changed notification on the strategy
                            // This might help trigger UI updates in the Control Center
                            if (strategy is INotifyPropertyChanged notifyPropertyChanged)
                            {
                                var propertyInfo = strategy.GetType().GetProperty("State");
                                if (propertyInfo != null)
                                {
                                    Instance?.LogDebug("SYSTEM", $"[MultiStratManager] Attempting to trigger PropertyChanged for State property");

                                    // Use reflection to invoke the OnPropertyChanged method if it exists
                                    var methodInfo = strategy.GetType().GetMethod("OnPropertyChanged",
                                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                                    if (methodInfo != null)
                                    {
                                        methodInfo.Invoke(strategy, new object[] { "State" });
                                        Instance?.LogDebug("SYSTEM", $"[MultiStratManager] Successfully triggered PropertyChanged for State");
                                    }
                                }
                            }
                        }
                        catch (Exception refreshEx)
                        {
                            // Non-critical error - log but continue
                            Instance?.LogWarn("SYSTEM", $"[MultiStratManager] Non-critical error while attempting to refresh UI: {refreshEx.Message}");
                        }
                    }
                    catch (Exception changeStateException)
                    {
                        Instance?.LogError("SYSTEM", $"[MultiStratManager] Error calling SetState({newState}) for {strategy.Name}: {changeStateException.Message}\nStackTrace: {changeStateException.StackTrace}");
                    }
                });
            }
            else
            {
                Instance?.LogDebug("SYSTEM", $"[MultiStratManager] State for {strategy.Name} is already {newState}. No action taken.");
                // Even if no action is taken, it might be useful to notify if the current state is what UI expects
                // For example, if the UI tried to enable an already enabled strategy,
                // we might still want to confirm the state back to the UI.
                // This part can be expanded based on UI interaction needs.
            }
        }
        catch (Exception ex)
        {
            Instance?.LogError("SYSTEM", $"[MultiStratManager] Error in RequestStrategyStateChange for {strategy.Name}: {ex.Message}\nStackTrace: {ex.StackTrace}");
        }
    }

    /// <summary>
    /// SIMPLIFIED FIFO CLOSURE DETECTION: Finds the original opening trade's base_id that corresponds to a closing execution
    /// Uses the same logic as IsPositionClosingExecution() to ensure consistency
    /// </summary>
    /// <param name="e">The closing execution event args</param>
    /// <returns>The base_id of the original opening trade, or null if not found</returns>
    private string FindOriginalTradeBaseId(ExecutionEventArgs e)
    {
        if (e?.Execution?.Order == null) return null;

        string orderName = e.Execution.Order.Name?.ToUpper() ?? "";
        var orderAction = e.Execution.Order.OrderAction;
        var instrument = e.Execution.Instrument.FullName;
        var account = e.Execution.Account.Name;

        LogAndPrint($"CLOSURE_SEARCH_DEBUG: Searching for closure match. Order: '{e.Execution.Order.Name}', Action: {orderAction}, Instrument: {instrument}, Account: {account}");
        LogAndPrint($"CLOSURE_SEARCH_DEBUG: activeNtTrades contains {activeNtTrades.Count} entries");

        // SIMPLIFIED LOGIC: Use the same position analysis that IsPositionClosingExecution() uses
        // This ensures consistency between closure detection and closure matching
        if (!IsPositionClosingExecution(e))
        {
            LogAndPrint($"CLOSURE_POSITION_ANALYSIS: Position analysis indicates this is NOT a closure - treating as entry trade");
            return null;
        }

        // If we get here, IsPositionClosingExecution() confirmed this is a closure
        // Now find which specific trade to close using FIFO
        LogAndPrint($"CLOSURE_CONFIRMED: Position analysis confirmed closure - finding specific trade to close using FIFO");

        return FindClosureByPositionAnalysis(orderAction, instrument, account);
    }

    /// <summary>
    /// Enhanced position-based closure detection that analyzes order actions and existing positions
    /// </summary>
    private string FindClosureByPositionAnalysis(OrderAction orderAction, string instrument, string account)
    {
        lock (_activeNtTradesLock)
        {
            LogAndPrint($"CLOSURE_POSITION_ANALYSIS: Analyzing {activeNtTrades.Count} active trades for potential closure");

            // Find trades that could be closed by this order action
            var potentialClosures = new List<KeyValuePair<string, OriginalTradeDetails>>();

            foreach (var kvp in activeNtTrades)
            {
                var storedTrade = kvp.Value;

                // Must be same instrument and account
                if (storedTrade.NtInstrumentSymbol != instrument || storedTrade.NtAccountName != account)
                {
                    LogAndPrint($"CLOSURE_POSITION_ANALYSIS: Skipping BaseID: {kvp.Key} - Different instrument/account. Stored: {storedTrade.NtInstrumentSymbol}/{storedTrade.NtAccountName}, Current: {instrument}/{account}");
                    continue;
                }

                bool isOppositeAction = false;

                // ENHANCED: Check if this order action is opposite to the stored position
                LogAndPrint($"CLOSURE_POSITION_ANALYSIS: Checking BaseID: {kvp.Key} - StoredPosition: {storedTrade.MarketPosition}, StoredAction: {storedTrade.OriginalOrderAction}, CurrentAction: {orderAction}");

                // Case 1: Stored trade was a Long position (Buy action) and current is Sell action
                if (storedTrade.MarketPosition == MarketPosition.Long &&
                    (orderAction == OrderAction.Sell || orderAction == OrderAction.SellShort))
                {
                    isOppositeAction = true;
                    LogAndPrint($"CLOSURE_POSITION_ANALYSIS: Found potential long closure - BaseID: {kvp.Key}, Original: Long, Current: {orderAction}");
                }
                // Case 2: Stored trade was a Short position (Sell action) and current is Buy action
                else if (storedTrade.MarketPosition == MarketPosition.Short &&
                        (orderAction == OrderAction.BuyToCover || orderAction == OrderAction.Buy))
                {
                    isOppositeAction = true;
                    LogAndPrint($"CLOSURE_POSITION_ANALYSIS: Found potential short closure - BaseID: {kvp.Key}, Original: Short, Current: {orderAction}");
                }
                else
                {
                    LogAndPrint($"CLOSURE_POSITION_ANALYSIS: No closure match for BaseID: {kvp.Key} - Position: {storedTrade.MarketPosition}, OriginalAction: {storedTrade.OriginalOrderAction}, CurrentAction: {orderAction}");
                }

                if (isOppositeAction)
                {
                    potentialClosures.Add(kvp);
                }
            }

            if (potentialClosures.Count == 0)
            {
                LogAndPrint($"CLOSURE_POSITION_ANALYSIS: No potential closures found for {orderAction} on {instrument}");
                return null;
            }
            else if (potentialClosures.Count == 1)
            {
                // Single match - most likely scenario
                string baseId = potentialClosures[0].Key;
                LogAndPrint($"CLOSURE_POSITION_ANALYSIS: Single closure match found - BaseID: {baseId}");
                return baseId;
            }
            else
            {
                // FIFO: Multiple matches found - use First In, First Out (oldest trade first)
                LogAndPrint($"CLOSURE_POSITION_ANALYSIS: Multiple potential closures found ({potentialClosures.Count}). Using FIFO (oldest trade first) to resolve ambiguity.");
                LogAndPrint($"CLOSURE_POSITION_ANALYSIS: Potential matches were:");
                foreach (var closure in potentialClosures)
                {
                    LogAndPrint($"CLOSURE_POSITION_ANALYSIS:   - BaseID: {closure.Key}, Position: {closure.Value.MarketPosition}, Action: {closure.Value.OriginalOrderAction}, Timestamp: {closure.Value.Timestamp}");
                }

                // Use FIFO - select the oldest trade (earliest timestamp)
                var oldestTrade = potentialClosures.OrderBy(kvp => kvp.Value.Timestamp).First();
                string baseId = oldestTrade.Key;
                LogAndPrint($"CLOSURE_POSITION_ANALYSIS: FIFO selection - BaseID: {baseId} (oldest trade)");
                return baseId;
            }
        }
    }

    /// <summary>
    /// OFFICIAL NINJATRADER BEST PRACTICE: Comprehensive Entry vs Closure Detection
    /// Based on official NinjaTrader documentation for OnExecutionUpdate and OnPositionUpdate
    /// </summary>
    private bool DetectTradeClosureByExecution(ExecutionEventArgs e)
    {
        if (e?.Execution == null || e.Execution.Order == null) return false;

        string orderName = e.Execution.Order.Name ?? "";
        OrderAction orderAction = e.Execution.Order.OrderAction;
        string instrumentName = e.Execution.Instrument?.FullName ?? "Unknown";
        string accountName = e.Execution.Account?.Name ?? "Unknown";
        MarketPosition executionMarketPosition = e.Execution.MarketPosition;

        LogAndPrint($"ENTRY_VS_CLOSURE: Analyzing execution - OrderName: '{orderName}', Action: {orderAction}, MarketPosition: {executionMarketPosition}, Instrument: {instrumentName}");

        // METHOD 1: Order Name Analysis (Most Reliable for Manual Trades)
        if (IsClosingOrderByName(orderName))
        {
            LogAndPrint($"CLOSURE_DETECTED: Order '{orderName}' identified as closing order by name");
            return true;
        }

        // METHOD 2: OrderAction Analysis (Official NinjaTrader Pattern)
        if (IsExitOrderAction(orderAction))
        {
            LogAndPrint($"CLOSURE_DETECTED: OrderAction {orderAction} is an exit action");
            return true;
        }

        // METHOD 3: Position State Analysis (Most Reliable for Automated Detection)
        if (IsPositionClosingExecution(e))
        {
            LogAndPrint($"CLOSURE_DETECTED: Execution reduces position toward flat");
            return true;
        }

        LogAndPrint($"ENTRY_DETECTED: Execution identified as NEW ENTRY TRADE");
        return false;
    }

    /// <summary>
    /// OFFICIAL NINJATRADER: Detects if OrderAction represents an exit/closure
    /// Based on official OrderAction documentation
    /// IMPORTANT: OrderAction.Sell can be either entry (SellShort) or exit (Sell to close long)
    /// We need position context to determine the intent
    /// </summary>
    private bool IsExitOrderAction(OrderAction orderAction)
    {
        // From Official NinjaTrader Documentation:
        // ONLY BuyToCover is explicitly an exit action
        // OrderAction.Sell can be either entry (SellShort) or exit (Sell to close long)
        // OrderAction.Buy can be either entry (Buy to open long) or exit (Buy to cover short)

        // We should NOT classify Sell/Buy as exits without position context
        // Only BuyToCover is explicitly an exit action
        return orderAction == OrderAction.BuyToCover;
    }

    /// <summary>
    /// OFFICIAL NINJATRADER: Detects if execution represents a new entry
    /// Based on official OrderAction documentation
    /// IMPORTANT: OrderAction.Buy and OrderAction.Sell can be either entry or exit
    /// We need position context to determine the intent
    /// </summary>
    private bool IsEntryOrderAction(OrderAction orderAction)
    {
        // From Official NinjaTrader Documentation:
        // ONLY SellShort is explicitly an entry action
        // OrderAction.Buy can be either entry (Buy to open long) or exit (Buy to cover short)
        // OrderAction.Sell can be either entry (SellShort) or exit (Sell to close long)

        // For now, we'll consider all actions as potential entries
        // and rely on position analysis to determine if it's actually a closure
        return orderAction == OrderAction.Buy ||
               orderAction == OrderAction.Sell ||
               orderAction == OrderAction.SellShort ||
               orderAction == OrderAction.BuyToCover;
    }

    /// <summary>
    /// OFFICIAL NINJATRADER: Position State Analysis for Closure Detection
    /// Based on official OnPositionUpdate documentation and best practices
    /// </summary>
    private bool IsPositionClosingExecution(ExecutionEventArgs e)
    {
        if (e?.Execution == null || e.Execution.Order == null) return false;

        string instrumentName = e.Execution.Instrument?.FullName ?? "Unknown";
        string accountName = e.Execution.Account?.Name ?? "Unknown";
        OrderAction orderAction = e.Execution.Order.OrderAction;
        int executionQuantity = e.Execution.Quantity;
        
        // 1) Primary source of truth: actual NT account position (most reliable)
        try
        {
            if (monitoredAccount != null)
            {
                var livePos = monitoredAccount.Positions
                    .FirstOrDefault(p => p.Instrument?.FullName == instrumentName);

                if (livePos == null || livePos.MarketPosition == MarketPosition.Flat || livePos.Quantity == 0)
                {
                    LogAndPrint($"POSITION_ANALYSIS: Account reports FLAT for {instrumentName} on {accountName} - treat as NEW ENTRY");
                    return false; // flat => cannot be a closure
                }

                // Positive for long, negative for short
                int netFromAccount = livePos.MarketPosition == MarketPosition.Long ? livePos.Quantity : -livePos.Quantity;
                LogAndPrint($"POSITION_ANALYSIS: Account position - MarketPosition: {livePos.MarketPosition}, Qty: {livePos.Quantity}, Net: {netFromAccount}");

                if (netFromAccount > 0)
                {
                    // Currently net long → Sell reduces, Buy increases
                    bool isClosing = (orderAction == OrderAction.Sell || orderAction == OrderAction.SellShort);
                    LogAndPrint(isClosing
                        ? $"POSITION_ANALYSIS: {orderAction} reduces LONG from account - CLOSURE"
                        : $"POSITION_ANALYSIS: {orderAction} increases LONG from account - NEW ENTRY");
                    return isClosing;
                }
                else if (netFromAccount < 0)
                {
                    // Currently net short → Buy reduces, Sell increases
                    bool isClosing = (orderAction == OrderAction.Buy || orderAction == OrderAction.BuyToCover);
                    LogAndPrint(isClosing
                        ? $"POSITION_ANALYSIS: {orderAction} reduces SHORT from account - CLOSURE"
                        : $"POSITION_ANALYSIS: {orderAction} increases SHORT from account - NEW ENTRY");
                    return isClosing;
                }
            }
        }
        catch (Exception exPos)
        {
            LogAndPrint($"POSITION_ANALYSIS_ERROR: Failed to read account positions: {exPos.Message}. Falling back to internal tracking.");
        }

        // 2) Fallback to internal activeNtTrades tracking if account positions unavailable
        lock (_activeNtTradesLock)
        {
            var existingPositions = activeNtTrades.Values
                .Where(trade => trade.NtInstrumentSymbol == instrumentName &&
                               trade.NtAccountName == accountName)
                .ToList();

            if (existingPositions.Count == 0)
            {
                LogAndPrint($"POSITION_ANALYSIS: No existing tracked positions - treat as NEW ENTRY");
                return false;
            }

            int currentLongQuantity = existingPositions
                .Where(p => p.MarketPosition == MarketPosition.Long)
                .Sum(p => p.Quantity);

            int currentShortQuantity = existingPositions
                .Where(p => p.MarketPosition == MarketPosition.Short)
                .Sum(p => p.Quantity);

            int netPosition = currentLongQuantity - currentShortQuantity;
            LogAndPrint($"POSITION_ANALYSIS: Tracked position - Long: {currentLongQuantity}, Short: {currentShortQuantity}, Net: {netPosition}");

            if (netPosition > 0)
            {
                bool isClosing = (orderAction == OrderAction.Sell || orderAction == OrderAction.SellShort);
                LogAndPrint(isClosing
                    ? $"POSITION_ANALYSIS: {orderAction} reduces tracked LONG - CLOSURE"
                    : $"POSITION_ANALYSIS: {orderAction} increases tracked LONG - NEW ENTRY");
                return isClosing;
            }
            else if (netPosition < 0)
            {
                bool isClosing = (orderAction == OrderAction.Buy || orderAction == OrderAction.BuyToCover);
                LogAndPrint(isClosing
                    ? $"POSITION_ANALYSIS: {orderAction} reduces tracked SHORT - CLOSURE"
                    : $"POSITION_ANALYSIS: {orderAction} increases tracked SHORT - NEW ENTRY");
                return isClosing;
            }
            else
            {
                LogAndPrint($"POSITION_ANALYSIS: Tracked FLAT - treat as NEW ENTRY");
                return false;
            }
        }
    }

    /// <summary>
    /// OFFICIAL NINJATRADER: Comprehensive New Entry Detection
    /// Based on official documentation best practices - ensures no new entries are missed
    /// </summary>
    private bool IsNewEntryTrade(ExecutionEventArgs e)
    {
        if (e?.Execution == null || e.Execution.Order == null) return false;

        string orderName = e.Execution.Order.Name ?? "";
        OrderAction orderAction = e.Execution.Order.OrderAction;
        string instrumentName = e.Execution.Instrument?.FullName ?? "Unknown";
        string accountName = e.Execution.Account?.Name ?? "Unknown";

        // Do not treat tracked hedge closing orders as new entries
        if (!string.IsNullOrEmpty(e.Execution?.Order?.Id.ToString()) && trackedHedgeClosingOrderIds.Contains(e.Execution.Order.Id.ToString()))
        {
            LogAndPrint($"ENTRY_DETECTION_GUARD: Order {e.Execution.Order.Id} is a tracked hedge close order - NOT a new entry");
            return false;
        }

        LogAndPrint($"ENTRY_DETECTION: Analyzing execution - OrderName: '{orderName}', Action: {orderAction}, Instrument: {instrumentName}");

        // METHOD 1: OrderAction Analysis (Primary Method)
        if (IsEntryOrderAction(orderAction))
        {
            LogAndPrint($"ENTRY_DETECTION: OrderAction {orderAction} is an entry action");

            // METHOD 2: Confirm it's not actually a closure by checking position state
            if (!IsPositionClosingExecution(e))
            {
                LogAndPrint($"ENTRY_CONFIRMED: Position analysis confirms this is a NEW ENTRY");
                return true;
            }
            else
            {
                LogAndPrint($"ENTRY_OVERRIDE: OrderAction suggests entry but position analysis indicates closure");
                return false;
            }
        }

        // METHOD 3: Order Name Analysis for Entry Detection
        if (IsEntryOrderByName(orderName))
        {
            LogAndPrint($"ENTRY_DETECTION: Order '{orderName}' identified as entry order by name");
            return true;
        }

        LogAndPrint($"ENTRY_DETECTION: Execution is NOT a new entry");
        return false;
    }

    /// <summary>
    /// Checks if order name indicates it's an entry order
    /// Based on common NinjaTrader naming conventions
    /// </summary>
    private bool IsEntryOrderByName(string orderName)
    {
        if (string.IsNullOrEmpty(orderName)) return false;

        string upperName = orderName.ToUpper();

        // Common entry order names in NinjaTrader
        return upperName.Contains("ENTRY") ||
               upperName.Contains("ENTER") ||
               upperName.Contains("LONG") ||
               upperName.Contains("SHORT") ||
               upperName.Contains("BUY") ||
               upperName.Contains("SELL") ||
               upperName == "ENTRY";           // Exact match for simple entry
    }

    /// <summary>
    /// Checks if order name indicates it's a closing order
    /// Based on common NinjaTrader naming conventions
    /// </summary>
    private bool IsClosingOrderByName(string orderName)
    {
        if (string.IsNullOrEmpty(orderName)) return false;

        string upperName = orderName.ToUpper();

        // Common closing order names in NinjaTrader
        return upperName.Contains("CLOSE") ||
               upperName.Contains("EXIT") ||
               upperName.Contains("STOP") ||
               upperName.Contains("TARGET") ||
               upperName.Contains("TP") ||     // Take Profit
               upperName.Contains("SL") ||     // Stop Loss
               upperName == "CLOSE";           // Exact match for manual close
    }

    /// <summary>
    /// Checks if this execution will close an existing position
    /// This is the most reliable method according to NinjaTrader docs
    /// </summary>
    private bool WillExecutionClosePosition(ExecutionEventArgs e)
    {
        if (e?.Execution == null || e.Execution.Order == null) return false;

        string instrumentName = e.Execution.Instrument?.FullName ?? "Unknown";
        string accountName = e.Execution.Account?.Name ?? "Unknown";
        OrderAction orderAction = e.Execution.Order.OrderAction;
        int executionQuantity = e.Execution.Quantity;

        lock (_activeNtTradesLock)
        {
            // Find existing positions that could be closed by this execution
            var matchingPositions = activeNtTrades.Values
                .Where(trade => trade.NtInstrumentSymbol == instrumentName &&
                               trade.NtAccountName == accountName)
                .ToList();

            if (matchingPositions.Count == 0)
            {
                LogAndPrint($"CLOSURE_DETECTION: No existing positions found for {instrumentName} - this is a NEW TRADE");
                return false;
            }

            // Check if this execution would close any existing positions
            foreach (var position in matchingPositions)
            {
                bool isOppositeDirection = IsOppositeDirection(position.MarketPosition, orderAction);

                if (isOppositeDirection)
                {
                    LogAndPrint($"CLOSURE_DETECTION: Found opposite position - Position: {position.MarketPosition}, Action: {orderAction} - this is a CLOSURE");
                    return true;
                }
            }

            LogAndPrint($"CLOSURE_DETECTION: No opposite positions found - this is a NEW TRADE");
            return false;
        }
    }

    /// <summary>
    /// Determines if an order action is opposite to a market position
    /// </summary>
    private bool IsOppositeDirection(MarketPosition position, OrderAction action)
    {
        return (position == MarketPosition.Long && (action == OrderAction.Sell || action == OrderAction.SellShort)) ||
               (position == MarketPosition.Short && (action == OrderAction.Buy || action == OrderAction.BuyToCover));
    }

    /// <summary>
    /// Handles a confirmed trade closure execution
    /// BIDIRECTIONAL_HEDGE_FIX: This function should only process user-initiated closures,
    /// NOT hedge closing orders (which are responses to MT5 notifications).
    /// Hedge closing orders are now filtered out in OnExecutionUpdate to prevent FIFO-based BaseID mismatches.
    /// </summary>
    private void HandleTradeClosureExecution(ExecutionEventArgs e)
    {
        string executionId = e.Execution.ExecutionId;
        string instrumentName = e.Execution.Instrument?.FullName ?? "Unknown";
        string accountName = e.Execution.Account?.Name ?? "Unknown";
        OrderAction orderAction = e.Execution.Order.OrderAction;
        int quantity = e.Execution.Quantity;

        LogAndPrint($"CLOSURE_CONFIRMED: Processing closure execution {executionId}");

        // FIXED: Use FIFO logic since closing orders have different OrderIds than opening orders
        // OrderId mapping only works for the same order, but closures create new orders
        string closedTradeBaseId = FindClosureByPositionAnalysis(orderAction, instrumentName, accountName);

        if (!string.IsNullOrEmpty(closedTradeBaseId))
        {
            LogAndPrint($"CLOSURE_SUCCESS: Found trade being closed - BaseID: {closedTradeBaseId}");

            // Look up MT5 ticket for reliable closure
            ulong mt5Ticket = 0;
            // Retry logic for stress testing race conditions
            for (int retry = 0; retry < 3; retry++)
            {
                if (baseIdToMT5Ticket.TryGetValue(closedTradeBaseId, out mt5Ticket) && mt5Ticket > 0)
                    break;
                if (retry < 2) Thread.Sleep(5); // Brief delay
            }
            
            if (mt5Ticket > 0)
            {
                LogAndPrint($"CLOSURE_TICKET: Found MT5 ticket {mt5Ticket} for BaseID {closedTradeBaseId}");
            }
            else
            {
                LogAndPrint($"CLOSURE_TICKET: No MT5 ticket found for BaseID {closedTradeBaseId}, will use comment matching");
            }
            
            // Send closure notification to MT5
            // MT5 EA expects a trade message with action="CLOSE_HEDGE" for processing
            var closureNotification = new
            {
                action = "CLOSE_HEDGE",  // MT5 EA looks for this specific action
                base_id = closedTradeBaseId,
                // gRPC hedge-close fields expected by JsonToProtoHedgeClose
                nt_instrument_symbol = instrumentName,
                nt_account_name = accountName,
                closed_hedge_quantity = (double) quantity,
                closed_hedge_action = "CLOSE_HEDGE",
                timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                // Legacy/compat fields retained for MT5 JSON consumers
                price = 0.0,  // Not critical for closure
                total_quantity = quantity,
                contract_num = 1,
                instrument_name = instrumentName,
                account_name = accountName,
                time = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                nt_balance = 0,  // Not critical for closure
                nt_daily_pnl = 0,  // Not critical for closure
                nt_trade_result = "closed",
                nt_session_trades = 0,
                closure_reason = "NT_ORIGINAL_TRADE_CLOSED",
                mt5_ticket = mt5Ticket // Include MT5 ticket for reliable closure
            };

            string jsonMessage = SimpleJson.SerializeObject(closureNotification);
            LogAndPrint($"CLOSURE_NOTIFICATION: Sending to MT5: {jsonMessage}");

            // Send CLOSE_HEDGE request to Bridge Server via gRPC (using background task)
            Task.Run(() =>
            {
                try
                {
                    bool success = TradingGrpcClient.NTCloseHedge(jsonMessage);
                    if (success)
                    {
                        LogAndPrint($"CLOSURE_NOTIFICATION: Successfully sent CLOSE_HEDGE via gRPC for BaseID: {closedTradeBaseId}");
                    }
                    else
                    {
                        LogAndPrint($"ERROR: Failed to send CLOSE_HEDGE via gRPC for BaseID: {closedTradeBaseId}. Error: {TradingGrpcClient.LastError}");
                    }
                }
                catch (Exception ex)
                {
                    LogAndPrint($"ERROR: Exception sending CLOSE_HEDGE via gRPC for BaseID: {closedTradeBaseId}. Exception: {ex.Message}");
                }
            });

            // MULTI_TRADE_GROUP_FIX: Only remove BaseID when all trades with that BaseID are closed
            lock (_activeNtTradesLock)
            {
                if (activeNtTrades.TryGetValue(closedTradeBaseId, out var tradeDetails))
                {
                    tradeDetails.RemainingQuantity -= quantity;
                    LogAndPrint($"MULTI_TRADE_GROUP_CLOSURE: Reduced remaining quantity for BaseID {closedTradeBaseId} by {quantity}. Remaining: {tradeDetails.RemainingQuantity}/{tradeDetails.TotalQuantity}");

                    if (tradeDetails.RemainingQuantity <= 0)
                    {
                        // RemainingQuantity-based completion. Avoid setting IsClosed here; cleanup will remove tracking entry shortly.
                        tradeDetails.ClosedTimestamp = DateTime.UtcNow;
                        
                        // Internal trailing removed: no per-BaseID cleanup required
                        
                        LogAndPrint($"CLOSURE_CLEANUP: All trades closed for BaseID {closedTradeBaseId}. Will cleanup later. Remaining entries: {activeNtTrades.Count}");
                        
                        // Schedule delayed cleanup to allow MT5 notifications to process
                        Task.Run(async () =>
                        {
                            await Task.Delay(5000); // Wait 5 seconds for any pending MT5 notifications
                            if (activeNtTrades.TryRemove(closedTradeBaseId, out _))
                            {
                                LogAndPrint($"DELAYED_CLEANUP: Removed fully closed trade {closedTradeBaseId} from activeNtTrades tracking. Remaining entries: {activeNtTrades.Count}");
                            }
                        });
                    }
                    else
                    {
                        LogAndPrint($"CLOSURE_PARTIAL: BaseID {closedTradeBaseId} still has {tradeDetails.RemainingQuantity} trades remaining. Keeping in tracking.");
                    }
                }
                else
                {
                    LogAndPrint($"CLOSURE_ERROR: BaseID {closedTradeBaseId} not found in activeNtTrades during closure cleanup.");
                }
            }

            // WebSocket removed - using gRPC only for closure requests
        }
        else
        {
            LogAndPrint($"CLOSURE_ERROR: Could not find matching trade to close for {orderAction} on {instrumentName}");
        }
    }

    /// <summary>
    /// Finds the specific trade being closed by this order
    /// </summary>
    private string FindTradeBeingClosedByOrder(Order order)
    {
        if (order == null || order.Instrument == null || order.Account == null) return null;
        
        string instrument = order.Instrument.FullName;
        string account = order.Account.Name;
        OrderAction orderAction = order.OrderAction;
        int quantity = (int)order.Filled;
        
        LogAndPrint($"CLOSURE_SEARCH_ORDER: Looking for trade closed by Order {order.Id}, Action: {orderAction}, Instrument: {instrument}, Quantity: {quantity}");

        // FIXED: Use exact OrderId mapping instead of FIFO logic
        string orderId = order.Id.ToString();
        return FindTradeBeingClosedByOrderId(orderId);
    }

    /// <summary>
    /// Finds the specific trade being closed by this execution using OrderId mapping
    /// FIXED: Replaced FIFO logic with exact OrderId-to-BaseID mapping for accurate closure detection
    /// </summary>
    private string FindTradeBeingClosed(OrderAction orderAction, string instrument, string account, int quantity)
    {
        // This method is now deprecated in favor of direct OrderId mapping
        // It's kept for backward compatibility but should not be used for new closures
        LogAndPrint($"CLOSURE_SEARCH_DEPRECATED: FindTradeBeingClosed called - this method uses FIFO logic and may be inaccurate");

        lock (_activeNtTradesLock)
        {
            var matchingTrades = activeNtTrades
                .Where(kvp => kvp.Value.NtInstrumentSymbol == instrument &&
                             kvp.Value.NtAccountName == account &&
                             IsOppositeDirection(kvp.Value.MarketPosition, orderAction))
                .ToList();

            if (matchingTrades.Count == 0)
            {
                LogAndPrint($"CLOSURE_SEARCH: No matching trades found for {orderAction} on {instrument}");
                return null;
            }

            if (matchingTrades.Count == 1)
            {
                LogAndPrint($"CLOSURE_SEARCH: Single matching trade found - BaseID: {matchingTrades[0].Key}");
                return matchingTrades[0].Key;
            }

            // DEPRECATED: Multiple matches - use FIFO (first in, first out)
            // This is the problematic logic that should be replaced with OrderId mapping
            var oldestTrade = matchingTrades.OrderBy(kvp => kvp.Value.Timestamp).First();
            LogAndPrint($"CLOSURE_SEARCH_FIFO_WARNING: Multiple matches found, using FIFO - BaseID: {oldestTrade.Key} (THIS MAY BE INCORRECT!)");
            return oldestTrade.Key;
        }
    }

    /// <summary>
    /// NEW: Finds the specific trade being closed using exact OrderId-to-BaseID mapping
    /// This replaces the problematic FIFO logic with accurate closure detection
    /// </summary>
    private string FindTradeBeingClosedByOrderId(string orderId)
    {
        if (string.IsNullOrEmpty(orderId))
        {
            LogAndPrint($"CLOSURE_SEARCH_ORDERID: OrderId is null or empty");
            return null;
        }

        // Use our OrderId-to-BaseID mapping for exact matching
        if (orderIdToBaseIdMap.TryGetValue(orderId, out string baseId))
        {
            // Verify the baseId is still active
            lock (_activeNtTradesLock)
            {
                if (activeNtTrades.ContainsKey(baseId))
                {
                    LogAndPrint($"CLOSURE_SEARCH_ORDERID: Found exact match - OrderId {orderId} maps to BaseID {baseId}");
                    return baseId;
                }
                else
                {
                    LogAndPrint($"CLOSURE_SEARCH_ORDERID: OrderId {orderId} maps to BaseID {baseId} but trade is no longer active");
                    return null;
                }
            }
        }
        else
        {
            LogAndPrint($"CLOSURE_SEARCH_ORDERID: No mapping found for OrderId {orderId}");
            return null;
        }
    }

    /// <summary>
    /// Determines if an execution represents a closing trade (exit from position)
    /// ENHANCED: More aggressive closure detection for manual closures
    /// </summary>
    /// <param name="e">The execution event args</param>
    /// <returns>True if this execution closes a position, false if it opens/adds to a position</returns>
    private bool IsClosingExecution(ExecutionEventArgs e)
    {
        if (e?.Execution?.Order == null) return false;

        // MOST RELIABLE: Check if order name explicitly indicates it's a closing order
        if (!string.IsNullOrEmpty(e.Execution.Order.Name))
        {
            string orderName = e.Execution.Order.Name.ToUpper();
            if (orderName.Contains("CLOSE") || orderName.Contains("EXIT") ||
                orderName.Contains("TP") || orderName.Contains("SL"))
            {
                LogAndPrint($"CLOSURE_BY_NAME: Order '{e.Execution.Order.Name}' identified as closing order by name");
                return true;
            }
        }

        // Check for closure matches using the improved FindOriginalTradeBaseId function
        // which now only matches when there are explicit closure indicators
        string originalBaseId = FindOriginalTradeBaseId(e);
        if (!string.IsNullOrEmpty(originalBaseId))
        {
            LogAndPrint($"CLOSURE_BY_EXPLICIT_MATCH: Order identified as closing order for base_id {originalBaseId}");
            return true;
        }

        // CONSERVATIVE APPROACH: Only treat as closure if we have explicit evidence
        // This prevents new entry trades from being incorrectly treated as closures
        LogAndPrint($"ENTRY_TRADE: Order '{e.Execution.Order.Name ?? "unnamed"}' identified as entry trade (Action: {e.Execution.Order.OrderAction}, Position: {e.Execution.MarketPosition})");
        return false;
    }

    /// <summary>
    /// Sends a hedge closure notification to the bridge via gRPC
    /// </summary>
    /// <param name="closureData">The closure notification data</param>
    private void SendHedgeClosureNotification(Dictionary<string, object> closureData)
    {
        try
        {
            string json = SimpleJson.SerializeObject(closureData);
            LogAndPrint($"NT_CLOSURE: Sending hedge closure notification via gRPC");

            bool success = TradingGrpcClient.NotifyHedgeClose(json);

            if (success)
            {
                LogAndPrint($"NT_CLOSURE: Successfully sent hedge closure notification via gRPC");
            }
            else
            {
                string error = TradingGrpcClient.LastError;
                LogAndPrint($"ERROR: Failed to send hedge closure notification via gRPC: {error}");
            }
        }
        catch (Exception ex)
        {
            LogAndPrint($"ERROR: Exception sending hedge closure notification via gRPC: {ex.Message}");
        }
    }
    
    
    /// <summary>
    /// Get current market price for an instrument - kept locally as it's used for other purposes too
    /// </summary>
    private double GetCurrentPrice(Instrument instrument)
    {
        if (instrument == null) return 0;
        
        // Get the current bid/ask prices
        double bid = instrument.MarketData?.Bid?.Price ?? 0;
        double ask = instrument.MarketData?.Ask?.Price ?? 0;
        
        // Return mid-point or last traded price
        if (bid > 0 && ask > 0)
            return (bid + ask) / 2;
        else if (instrument.MarketData?.Last != null)
            return instrument.MarketData.Last.Price;
        else
            return 0;
    }

    /// <summary>
    /// Assembly resolver to load gRPC dependencies from addon directory
    /// </summary>
    private System.Reflection.Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
    {
        try
        {
            string assemblyName = new System.Reflection.AssemblyName(args.Name).Name;
            
            // Map of gRPC assemblies we need to resolve
            var grpcAssemblies = new Dictionary<string, string>
            {
                {"Grpc.Net.Client", "Grpc.Net.Client.dll"},
                {"Grpc.Core.Api", "Grpc.Core.Api.dll"},
                {"Grpc.Net.Common", "Grpc.Net.Common.dll"},
                {"Google.Protobuf", "Google.Protobuf.dll"},
                {"System.Runtime.CompilerServices.Unsafe", "System.Runtime.CompilerServices.Unsafe.dll"},
                {"System.Text.Json", "System.Text.Json.dll"}
            };
            
            if (grpcAssemblies.ContainsKey(assemblyName))
            {
                // Try to load from addon directory first
                string addonPath = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                string dllPath = System.IO.Path.Combine(addonPath, grpcAssemblies[assemblyName]);
                
                if (System.IO.File.Exists(dllPath))
                {
                    LogDebug("ASSEMBLY", $"Loading {assemblyName} from: {dllPath}");
                    return System.Reflection.Assembly.LoadFrom(dllPath);
                }
                
                // Fallback: try External folder in development
                string externalPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(addonPath), "External", grpcAssemblies[assemblyName]);
                if (System.IO.File.Exists(externalPath))
                {
                    LogDebug("ASSEMBLY", $"Loading {assemblyName} from External: {externalPath}");
                    return System.Reflection.Assembly.LoadFrom(externalPath);
                }
                
                LogError("ASSEMBLY", $"Could not find {assemblyName} at {dllPath} or {externalPath}");
            }
        }
        catch (Exception ex)
        {
            LogError("ASSEMBLY", $"Error resolving assembly {args.Name}: {ex.Message}");
        }
        
        return null;
    }
    
    }
}
