#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Threading;
using NinjaTrader.Cbi;
using NinjaTrader.Core;
using NinjaTrader.Data;
using System.Threading.Tasks;
using System.Text;
using System.Globalization;
using NTGrpcClient; // Added for gRPC client
#endregion

namespace NinjaTrader.NinjaScript.AddOns
{
    #region Enums for Trailing Configuration
    
    public enum InitialStopPlacementType
    {
        MoveToBreakeven,
        FixedPipsFromEntry,
        FixedTicksFromEntry,
        FixedDollarFromEntry,
        StartTrailingImmediately
    }
    
    public enum ContinuousTrailingType
    {
        None,
        PipTrail,
        TickTrail,
        DollarAmountTrail,
        DEMAAtrTrail,
        StepTrail
    }
    
    #endregion
    
    #region Data Classes
    
    /// <summary>
    /// Internal trailing stop tracking - HARDCODED TO REPLACE BROKEN TRADITIONAL SYSTEM
    /// </summary>
    public class InternalTrailingStop
    {
        public string BaseId { get; set; }
        public double StopLevel { get; set; }
        public double HighWaterMark { get; set; }
        public double LowWaterMark { get; set; }
        public bool IsActive { get; set; }
        public DateTime LastUpdate { get; set; }
        public Position TrackedPosition { get; set; }
        public double EntryPrice { get; set; }
        public double LastSentStopLevel { get; set; }
        
        // Performance tracking for debugging
        public DateTime ActivationTime { get; set; }
        public double ActivationPrice { get; set; }
        public double MaxProfit { get; set; }
        public int StopUpdateCount { get; set; }
        public double InitialStopDistance { get; set; }
    }
    
    /// <summary>
    /// Elastic position tracking class
    /// </summary>
    public class ElasticPositionTracker
    {
        public string BaseId { get; set; }
        public double EntryPrice { get; set; }
        public double LastReportedProfitLevel { get; set; }
        public int ProfitUpdatesSent { get; set; }
        public DateTime LastUpdateTime { get; set; }
        
        // Position identification
        public string InstrumentFullName { get; set; }
        public string InstrumentName { get; set; } // Alias for InstrumentFullName
        public MarketPosition MarketPosition { get; set; }
        
        // Trailing stop state
        public bool IsTrailingActive { get; set; }
        public double CurrentTrailedStopPrice { get; set; }
        public double HighWaterMarkPrice { get; set; }
        public double LowWaterMarkPrice { get; set; }
        public Order ManagedStopOrder { get; set; }
        public bool IsSLTPLogicCompleteForEntry { get; set; }
        
        // Bar data for indicators
        public List<NinjaTrader.NinjaScript.AddOns.Quote> QuoteBuffer { get; set; } = new List<NinjaTrader.NinjaScript.AddOns.Quote>();
        public double CurrentDemaValue { get; set; }
        public double CurrentAtrValue { get; set; }
        public DateTime LastBarTimeProcessed { get; set; }
    }
    
    /// <summary>
    /// Traditional trailing stop tracking - Uses actual broker-side stop orders
    /// </summary>
    public class TraditionalTrailingStop
    {
        public string BaseId { get; set; }
        public Order CurrentStopOrder { get; set; }
        public double LastStopPrice { get; set; }
        public DateTime LastModificationTime { get; set; }
        public int ModificationCount { get; set; }
        public Position TrackedPosition { get; set; }
        public double EntryPrice { get; set; }
        public bool IsActive { get; set; }
        public DateTime ActivationTime { get; set; }
        public double ActivationPrice { get; set; }
        public double MaxProfit { get; set; }
        public double InitialStopDistance { get; set; }
        
        // Tracking for order state
        public OrderState LastKnownOrderState { get; set; }
        public DateTime LastOrderStateUpdate { get; set; }
        public bool IsPendingModification { get; set; }
        public int FailedModificationAttempts { get; set; }
    }
    
    // Using the Quote class from IndicatorCalculator.cs
    
    #endregion
    
    /// <summary>
    /// Manages trailing stops and elastic hedging functionality
    /// </summary>
    public class TrailingAndElasticManager : INotifyPropertyChanged
    {
        #region Private Fields
        
        private readonly MultiStratManager parentManager;
        private Dictionary<string, ElasticPositionTracker> elasticPositions = new Dictionary<string, ElasticPositionTracker>();
        private DispatcherTimer elasticMonitorTimer;
        private Dictionary<string, BarsRequest> barsRequests = new Dictionary<string, BarsRequest>();
        
        // Internal trailing stop tracking
        private Dictionary<string, InternalTrailingStop> internalStops = new Dictionary<string, InternalTrailingStop>();
        private DispatcherTimer internalTrailingTimer;
        
        // Traditional (broker-side) trailing stop tracking
        private Dictionary<string, TraditionalTrailingStop> traditionalTrailingStops = new Dictionary<string, TraditionalTrailingStop>();
        
        #endregion
        
        #region Constructor
        
        public TrailingAndElasticManager(MultiStratManager parent)
        {
            parentManager = parent ?? throw new ArgumentNullException(nameof(parent));
        }
        
        /// <summary>
        /// Wrapper for logging to maintain consistent logging through parent manager
        /// </summary>
        private void LogAndPrint(string message)
        {
            parentManager.LogAndPrint(message);
        }
        
        #endregion
        
        #region Elastic Hedging Settings
        
        // Elastic Hedging Configuration
        public bool EnableElasticHedging { get; set; } = true;
        public double ProfitUpdateThreshold { get; set; } = 50.0; // $ profit per update
        public int ElasticUpdateIntervalSeconds { get; set; } = 1; // Check every second for fast updates
        public double MinProfitToReport { get; set; } = 10.0; // Don't send updates below this
        
        #endregion
        
        #region Trailing Settings
        
        // Main Trailing Settings Control
        public bool EnableTrailing { get; set; } = true; // Master switch for all trailing
        
        // Traditional vs Internal Trailing Selection
        private bool _useTraditionalTrailing = false; // DEFAULT: Use internal trailing (ultra-fast)
        public bool UseTraditionalTrailing 
        { 
            get { return _useTraditionalTrailing; }
            set 
            { 
                if (_useTraditionalTrailing != value)
                {
                    _useTraditionalTrailing = value;
                    OnPropertyChanged(nameof(UseTraditionalTrailing));
                    LogAndPrint($"TRAILING_MODE: Switched to {(value ? "Traditional (Broker-Side)" : "Internal (Ultra-Fast)")} trailing");
                }
            }
        }
        
        // DEMA-ATR Trailing Settings - Matching EA structure
        private bool _useATRTrailing = false; // DEFAULT: Off (Alternative Trailing is default)
        public bool UseATRTrailing 
        { 
            get { return _useATRTrailing; }
            set 
            { 
                if (_useATRTrailing != value)
                {
                    _useATRTrailing = value;
                    OnPropertyChanged(nameof(UseATRTrailing));
                }
            }
        }
        
        public int DEMA_ATR_Period { get; set; } = 14;                // DEMA-ATR Period
        public double DEMA_ATR_Multiplier { get; set; } = 1.5;        // DEMA-ATR Multiplier
        
        // Trailing Activation Settings
        public TrailingActivationType TrailingActivationMode { get; set; } = TrailingActivationType.Percent;
        public double TrailingActivationValue { get; set; } = 1.0;    // Value based on activation mode
        
        // Alternative Trailing Settings
        private bool _useAlternativeTrailing = true; // DEFAULT: Use Alternative Trailing instead of DEMA-ATR
        public bool UseAlternativeTrailing 
        { 
            get { return _useAlternativeTrailing; }
            set 
            { 
                if (_useAlternativeTrailing != value)
                {
                    _useAlternativeTrailing = value;
                    OnPropertyChanged(nameof(UseAlternativeTrailing));
                }
            }
        }
        
        // Trailing TRIGGER settings  
        public TrailingActivationType TrailingTriggerType { get; set; } = TrailingActivationType.Dollars;
        public double TrailingTriggerValue { get; set; } = 100.0; // Changed from 50.0 to 100.0 for less aggressive activation
        
        // Trailing STOP settings
        public TrailingActivationType TrailingStopType { get; set; } = TrailingActivationType.Dollars;  
        public double TrailingStopValue { get; set; } = 50.0; // Changed from 25.0 to 50.0 for wider stop distance
        
        // Trailing INCREMENTS settings
        public TrailingActivationType TrailingIncrementsType { get; set; } = TrailingActivationType.Dollars;
        public double TrailingIncrementsValue { get; set; } = 10.0;
        
        #endregion
        
        #region Legacy Trailing Settings
        
        // Legacy trailing settings (kept for backward compatibility)
        public bool EnableTrailingStop { get; set; } = false;
        public int ActivateTrailAfterPipsProfit { get; set; } = 20;
        public InitialStopPlacementType InitialStopPlacement { get; set; } = InitialStopPlacementType.StartTrailingImmediately;
        public ContinuousTrailingType TrailingType { get; set; } = ContinuousTrailingType.DollarAmountTrail;
        public double DollarTrailDistance { get; set; } = 100.00;
        public int PipTrailDistance { get; set; } = 15;
        public int TickTrailDistance { get; set; } = 30;
        public string StepTrailLevelsCsv { get; set; } = "20:10,40:20,60:30";
        public int AtrPeriod { get; set; } = 14;
        public double AtrMultiplier { get; set; } = 2.5;
        
        #endregion
        
        #region Public Properties for UI Access
        
        /// <summary>
        /// Public property to expose internal stops for UI
        /// </summary>
        public Dictionary<string, InternalTrailingStop> InternalStops
        {
            get { return new Dictionary<string, InternalTrailingStop>(internalStops); }
        }
        
        /// <summary>
        /// Public property to expose elastic positions for UI
        /// </summary>
        public Dictionary<string, ElasticPositionTracker> ElasticPositions
        {
            get { return new Dictionary<string, ElasticPositionTracker>(elasticPositions); }
        }
        
        /// <summary>
        /// Public property to expose traditional trailing stops for UI
        /// </summary>
        public Dictionary<string, TraditionalTrailingStop> TraditionalTrailingStops
        {
            get { return new Dictionary<string, TraditionalTrailingStop>(traditionalTrailingStops); }
        }
        
        #endregion
        
        #region Events
        
        public event PropertyChangedEventHandler PropertyChanged;
        
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        
        #endregion
        
        #region Public Methods
        
        /// <summary>
        /// Initialize elastic hedging monitoring timer
        /// </summary>
        public void InitializeElasticMonitoring(Account monitoredAccount)
        {
            // Prevent double initialization
            if (elasticMonitorTimer != null)
            {
                LogAndPrint("ELASTIC_DEBUG: Elastic monitoring already initialized, skipping");
                return;
            }
            
            LogAndPrint("ELASTIC_DEBUG: Initializing elastic hedging monitoring");
            LogAndPrint($"ELASTIC_DEBUG: EnableElasticHedging = {EnableElasticHedging}");
            LogAndPrint($"ELASTIC_DEBUG: ElasticUpdateIntervalSeconds = {ElasticUpdateIntervalSeconds}");
            LogAndPrint($"ELASTIC_DEBUG: ProfitUpdateThreshold = {ProfitUpdateThreshold}");
            LogAndPrint($"ELASTIC_DEBUG: MinProfitToReport = {MinProfitToReport}");
            LogAndPrint($"ELASTIC_DEBUG: MonitoredAccount = {(monitoredAccount?.Name ?? "NULL")}");
            
            elasticMonitorTimer = new DispatcherTimer();
            elasticMonitorTimer.Interval = TimeSpan.FromSeconds(ElasticUpdateIntervalSeconds);
            elasticMonitorTimer.Tick += (sender, e) => MonitorPositionsForElasticHedging(monitoredAccount);
            elasticMonitorTimer.Start();
            
            LogAndPrint($"ELASTIC_DEBUG: Elastic monitoring timer started with {ElasticUpdateIntervalSeconds}s interval for account {monitoredAccount?.Name}");
            
            // Initialize internal trailing stop timer for ultra-fast monitoring (ALWAYS ENABLED)
            internalTrailingTimer = new DispatcherTimer();
            internalTrailingTimer.Interval = TimeSpan.FromMilliseconds(100); // 100ms for nanosecond-fast response
            internalTrailingTimer.Tick += (sender, e) => MonitorInternalTrailingStops(monitoredAccount);
            internalTrailingTimer.Start();
            LogAndPrint($"INTERNAL_TRAILING: Ultra-fast internal trailing timer started with 100ms interval");
        }
        
        /// <summary>
        /// Stop elastic monitoring timer
        /// </summary>
        public void StopElasticMonitoring()
        {
            if (elasticMonitorTimer != null)
            {
                elasticMonitorTimer.Stop();
                elasticMonitorTimer = null;
                LogAndPrint("Elastic monitoring stopped");
            }
            
            if (internalTrailingTimer != null)
            {
                internalTrailingTimer.Stop();
                internalTrailingTimer = null;
                LogAndPrint("Internal trailing monitoring stopped");
            }
        }
        
        /// <summary>
        /// Add position to elastic tracking when trade is filled
        /// </summary>
        public void AddElasticPositionTracking(string baseId, Position position, double entryPrice)
        {
            LogAndPrint($"ELASTIC_ADD_DEBUG: AddElasticPositionTracking called - BaseId: {baseId}, EnableElasticHedging: {EnableElasticHedging}");
            
            if (!EnableElasticHedging) 
            {
                LogAndPrint($"ELASTIC_ADD_DEBUG: Elastic hedging disabled, skipping tracking for {baseId}");
                return;
            }
            
            var tracker = new ElasticPositionTracker
            {
                BaseId = baseId,
                EntryPrice = entryPrice,
                LastReportedProfitLevel = 0,
                ProfitUpdatesSent = 0,
                LastUpdateTime = DateTime.Now,
                InstrumentFullName = position.Instrument.FullName,
                MarketPosition = position.MarketPosition,
                IsTrailingActive = false,
                CurrentTrailedStopPrice = 0,
                HighWaterMarkPrice = entryPrice,
                LowWaterMarkPrice = entryPrice,
                IsSLTPLogicCompleteForEntry = false
            };
            
            elasticPositions[baseId] = tracker;
            LogAndPrint($"ELASTIC_ADD_DEBUG: ✅ Successfully added elastic tracking for {baseId} at {entryPrice:F2}. Total tracked positions: {elasticPositions.Count}");
            double addCurrentPrice = GetCurrentPrice(position.Instrument);
            LogAndPrint($"ELASTIC_ADD_DEBUG: Position details - Instrument: {position.Instrument.FullName}, MarketPosition: {position.MarketPosition}, Current P&L: ${position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, addCurrentPrice):F2}");
        }
        
        /// <summary>
        /// Add position to internal trailing stop tracking
        /// </summary>
        public void AddInternalTrailingStop(string baseId, Position position, double entryPrice)
        {
            if (!EnableTrailing)
            {
                LogAndPrint($"INTERNAL_TRAILING: Trailing disabled, skipping internal stop for {baseId}");
                return;
            }
            
            var internalStop = new InternalTrailingStop
            {
                BaseId = baseId,
                TrackedPosition = position,
                EntryPrice = entryPrice,
                StopLevel = 0, // Will be calculated
                IsActive = false, // Will be activated when profit threshold is met
                HighWaterMark = entryPrice,
                LowWaterMark = entryPrice,
                LastUpdate = DateTime.Now,
                ActivationTime = DateTime.MinValue,
                MaxProfit = 0,
                StopUpdateCount = 0
            };
            
            internalStops[baseId] = internalStop;
            LogAndPrint($"INTERNAL_TRAILING: Added internal trailing stop for {baseId} at entry {entryPrice:F2}");
        }
        
        /// <summary>
        /// Remove position from all tracking
        /// </summary>
        public void RemovePositionTracking(string baseId)
        {
            bool removedElastic = elasticPositions.Remove(baseId);
            bool removedInternal = internalStops.Remove(baseId);
            
            if (removedElastic || removedInternal)
            {
                LogAndPrint($"Removed position tracking for {baseId} - Elastic: {removedElastic}, Internal: {removedInternal}");
            }
        }
        
        /// <summary>
        /// Remove internal trailing stop for a specific baseId
        /// </summary>
        public void RemoveInternalTrailingStop(string baseId)
        {
            bool removed = internalStops.Remove(baseId);
            if (removed)
            {
                LogAndPrint($"Removed internal trailing stop for {baseId}");
            }
        }
        
        /// <summary>
        /// Initialize BarsRequest subscriptions for trailing stop calculations
        /// </summary>
        public void InitializeBarsRequests()
        {
            try
            {
                LogAndPrint("Initializing BarsRequest subscriptions for trailing stops");
                
                // Subscribe to positions from monitored account
                if (parentManager.MonitoredAccount != null && parentManager.MonitoredAccount.Positions != null)
                {
                    foreach (var position in parentManager.MonitoredAccount.Positions)
                    {
                        if (position.Quantity != 0)
                        {
                            SubscribeToInstrumentBars(position.Instrument);
                        }
                    }
                }
                
                LogAndPrint($"BarsRequest initialization complete. Active subscriptions: {barsRequests.Count}");
            }
            catch (Exception ex)
            {
                LogAndPrint($"Error initializing BarsRequest subscriptions: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Subscribe to bars for a specific instrument
        /// </summary>
        private void SubscribeToInstrumentBars(Instrument instrument)
        {
            try
            {
                string instrumentKey = instrument.FullName;
                
                // Check if already subscribed
                if (barsRequests.ContainsKey(instrumentKey))
                {
                    return;
                }
                
                // Create BarsRequest for 1-minute bars with sufficient history for ATR calculation
                BarsRequest barsRequest = new BarsRequest(instrument, 200) // 200 bars for ATR calculation
                {
                    BarsPeriod = new BarsPeriod
                    {
                        BarsPeriodType = BarsPeriodType.Minute,
                        Value = 1
                    }
                };
                
                // Subscribe to events
                barsRequest.Update += OnBarsUpdate;
                
                // Store the request
                barsRequests[instrumentKey] = barsRequest;
                
                LogAndPrint($"Subscribed to bars for {instrumentKey}");
            }
            catch (Exception ex)
            {
                LogAndPrint($"Error subscribing to bars for {instrument.FullName}: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Clean up BarsRequest subscriptions
        /// </summary>
        public void CleanupBarsRequests()
        {
            try
            {
                LogAndPrint($"Cleaning up {barsRequests.Count} BarsRequest subscriptions");
                
                foreach (var kvp in barsRequests)
                {
                    try
                    {
                        if (kvp.Value != null)
                        {
                            kvp.Value.Update -= OnBarsUpdate;
                            kvp.Value.Dispose();
                        }
                    }
                    catch (Exception ex)
                    {
                        LogAndPrint($"Error disposing BarsRequest for {kvp.Key}: {ex.Message}");
                    }
                }
                
                barsRequests.Clear();
                LogAndPrint("BarsRequest cleanup complete");
            }
            catch (Exception ex)
            {
                LogAndPrint($"Error in CleanupBarsRequests: {ex.Message}");
            }
        }
        
        #endregion
        
        #region Private Methods - Elastic Hedging
        
        /// <summary>
        /// Monitor positions for elastic hedging profit updates
        /// </summary>
        private void MonitorPositionsForElasticHedging(Account monitoredAccount)
        {
            if (monitoredAccount == null || !EnableElasticHedging) return;
            
            try
            {
                // Monitor tracked positions (removed spammy log - logs only when positions change or actions are taken)
                
                // Add periodic position scanning to catch positions not added via OnExecutionUpdate
                ScanForMissingPositionTrackers(monitoredAccount);
                
                foreach (var tracker in elasticPositions.Values.ToList())
                {
                    // Check tracker for position (removed spammy log - only logs when actions are taken)
                    
                    // Match position by instrument and market position
                    var position = monitoredAccount.Positions.FirstOrDefault(p => 
                        p.Instrument.FullName == tracker.InstrumentFullName && 
                        p.MarketPosition == tracker.MarketPosition);
                        
                    if (position != null)
                    {
                        // Get current market price for the instrument
                        double currentPrice = GetCurrentPrice(position.Instrument);
                        double currentProfit = position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, currentPrice);
                        double profitThreshold = ProfitUpdateThreshold;
                        
                        LogAndPrint($"ELASTIC_DEBUG: Found position {tracker.BaseId} - Current profit: ${currentProfit:F2}, Threshold: ${profitThreshold:F2}, MinReport: ${MinProfitToReport:F2}");
                        
                        // Check if we've crossed a new threshold
                        int currentLevel = (int)(currentProfit / profitThreshold);
                        int lastLevel = (int)(tracker.LastReportedProfitLevel / profitThreshold);
                        
                        LogAndPrint($"ELASTIC_DEBUG: Levels - Current: {currentLevel}, Last: {lastLevel}, Should send: {currentLevel > lastLevel && currentProfit >= MinProfitToReport}");
                        
                        if (currentLevel > lastLevel && currentProfit >= MinProfitToReport)
                        {
                            // Send profit update to bridge
                            Task.Run(() => SendElasticHedgeUpdate(tracker.BaseId, currentProfit, currentLevel));
                            tracker.LastReportedProfitLevel = currentLevel * profitThreshold;
                            tracker.ProfitUpdatesSent++;
                            tracker.LastUpdateTime = DateTime.Now;
                            
                            LogAndPrint($"Elastic update sent for {tracker.BaseId}: Profit=${currentProfit:F2}, Level={currentLevel}");
                        }
                        
                        // Also check trailing stop updates (internal system handles this automatically)
                        if (EnableTrailing && ShouldUpdateTrailingStop(tracker, position))
                        {
                            UpdateTrailingStop(tracker, position);
                            tracker.LastUpdateTime = DateTime.Now; // Update the last trailing update time
                        }
                    }
                    else
                    {
                        LogAndPrint($"ELASTIC_DEBUG: Position not found for {tracker.BaseId} - Available positions:");
                        foreach (var pos in monitoredAccount.Positions)
                        {
                            double posCurrentPrice = GetCurrentPrice(pos.Instrument);
                            double posPnL = pos.GetUnrealizedProfitLoss(PerformanceUnit.Currency, posCurrentPrice);
                            LogAndPrint($"ELASTIC_DEBUG: - {pos.Instrument.FullName}, {pos.MarketPosition}, P&L: ${posPnL:F2}");
                        }
                        
                        // Position closed, remove from tracking
                        elasticPositions.Remove(tracker.BaseId);
                        LogAndPrint($"Position {tracker.BaseId} closed, removing from elastic tracking");
                    }
                }
            }
            catch (Exception ex)
            {
                LogAndPrint($"ERROR in elastic monitoring: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Manually scan account positions to add missing elastic trackers
        /// </summary>
        private void ScanForMissingPositionTrackers(Account monitoredAccount)
        {
            if (monitoredAccount == null) return;
            
            try
            {
                // THROTTLED: Only log position scanning when there are actual positions or changes
                var positions = monitoredAccount.Positions.Where(p => p.MarketPosition != MarketPosition.Flat).ToList();
                if (positions.Count > 0 || elasticPositions.Count > 0)
                {
                    LogAndPrint($"POSITION_SCAN: Scanning account {monitoredAccount.Name} - {positions.Count} positions, {elasticPositions.Count} trackers");
                }
                
                foreach (var position in positions)
                {
                    LogAndPrint($"POSITION_SCAN_DEBUG: Position - Instrument: {position.Instrument.FullName}, MarketPosition: {position.MarketPosition}, Quantity: {position.Quantity}");
                    
                    // Check if we're already tracking this position
                    bool alreadyTracked = elasticPositions.Values.Any(tracker => 
                        tracker.InstrumentFullName == position.Instrument.FullName && 
                        tracker.MarketPosition == position.MarketPosition);
                    
                    if (!alreadyTracked)
                    {
                        // Create a tracker for this position
                        string syntheticBaseId = $"MANUAL_{position.Instrument.FullName}_{position.MarketPosition}_{DateTime.Now:HHmmss}";
                        double currentPrice = GetCurrentPrice(position.Instrument);

                        var tracker = new ElasticPositionTracker
                        {
                            BaseId = syntheticBaseId,
                            InstrumentFullName = position.Instrument.FullName,
                            MarketPosition = position.MarketPosition,
                            EntryPrice = currentPrice, // Use current price as approximation
                            LastReportedProfitLevel = 0,
                            LastUpdateTime = DateTime.Now
                        };

                        if (!elasticPositions.ContainsKey(syntheticBaseId))
                        {
                            elasticPositions[syntheticBaseId] = tracker;
                            LogAndPrint($"POSITION_SCAN: Created manual tracker {syntheticBaseId} for untracked position");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogAndPrint($"ERROR in ScanForMissingPositionTrackers: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Send elastic hedge update to bridge via WebSocket for low-latency
        /// </summary>
        private async Task SendElasticHedgeUpdate(string baseId, double currentProfit, int profitLevel)
        {
            var updateData = new Dictionary<string, object>
            {
                { "action", "ELASTIC_UPDATE" },
                { "base_id", baseId },
                { "price", currentProfit },  // EA expects profit in 'price' field
                { "volume", profitLevel },   // EA expects level in 'volume' field
                { "id", Guid.NewGuid().ToString() },  // Unique ID to prevent duplicates
                { "timestamp", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") }
            };

            // WebSocket and HTTP removed - using gRPC communication
            LogAndPrint($"Sending elastic update for {baseId}: Profit=${currentProfit:F2}, Level={profitLevel}");

            // Send via gRPC
            try
            {
                var json = SimpleJson.SerializeObject(updateData);
                bool success = TradingGrpcClient.SubmitElasticUpdate(json);

                if (success)
                {
                    LogAndPrint($"Elastic update sent via gRPC for {baseId}");
                }
                else
                {
                    string error = TradingGrpcClient.LastError;
                    LogAndPrint($"Failed to send elastic update via gRPC: {error}");
                }
            }
            catch (Exception grpcEx)
            {
                LogAndPrint($"ERROR sending elastic update via gRPC: {grpcEx.Message}");
            }
        }
        
        #endregion
        
        #region Private Methods - Internal Trailing Stops
        
        /// <summary>
        /// Monitor internal trailing stops for ultra-fast execution
        /// </summary>
        private void MonitorInternalTrailingStops(Account monitoredAccount)
        {
            try
            {
                if (monitoredAccount == null || !EnableTrailing) return;
                
                // Route to appropriate trailing method based on user selection
                if (UseTraditionalTrailing)
                {
                    MonitorTraditionalTrailingStops(monitoredAccount);
                    return;
                }
                
                var stopsToCheck = internalStops.Values.ToList();
                
                // THROTTLED: Only log when there are active stops to monitor
                if (stopsToCheck.Count > 0)
                {
                    LogAndPrint($"INTERNAL_TRAILING: Monitoring {stopsToCheck.Count} active internal stops");
                }
                
                foreach (var stop in stopsToCheck)
                {
                    // Skip inactive stops
                    if (!stop.IsActive && stop.MaxProfit <= 0) continue;
                    
                    // Get fresh position from account instead of using stale reference
                    var position = monitoredAccount.Positions.FirstOrDefault(p => 
                        p.Instrument.FullName == stop.TrackedPosition.Instrument.FullName && 
                        p.MarketPosition != MarketPosition.Flat);
                        
                    if (position == null)
                    {
                        // Position closed, remove stop
                        internalStops.Remove(stop.BaseId);
                        LogAndPrint($"INTERNAL_TRAILING: Position {stop.BaseId} closed, removing internal stop");
                        continue;
                    }
                    
                    // Validate position still has quantity
                    if (position.Quantity == 0)
                    {
                        internalStops.Remove(stop.BaseId);
                        LogAndPrint($"INTERNAL_TRAILING: Position {stop.BaseId} has zero quantity, removing internal stop");
                        continue;
                    }
                    
                    double currentPrice = GetCurrentPrice(position.Instrument);
                    if (currentPrice == 0) continue;
                    
                    // Calculate current P&L and position details
                    double unrealizedPnL = position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, currentPrice);
                    double pointsFromEntry = Math.Abs(currentPrice - stop.EntryPrice);
                    double percentFromEntry = (pointsFromEntry / stop.EntryPrice) * 100;
                    
                    // Update max profit tracking
                    if (unrealizedPnL > stop.MaxProfit)
                    {
                        stop.MaxProfit = unrealizedPnL;
                    }
                    
                    // Check if trailing should be activated (if not already active)
                    if (!stop.IsActive)
                    {
                        bool shouldActivate = false;
                        
                        switch (TrailingTriggerType)
                        {
                            case TrailingActivationType.Dollars:
                                shouldActivate = unrealizedPnL >= TrailingTriggerValue;
                                break;
                            case TrailingActivationType.Percent:
                                shouldActivate = percentFromEntry >= TrailingTriggerValue;
                                break;
                            case TrailingActivationType.Ticks:
                                double ticks = pointsFromEntry / position.Instrument.MasterInstrument.TickSize;
                                shouldActivate = ticks >= TrailingTriggerValue;
                                break;
                        }
                        
                        if (shouldActivate)
                        {
                            stop.IsActive = true;
                            stop.ActivationTime = DateTime.Now;
                            stop.ActivationPrice = currentPrice;
                            LogAndPrint($"INTERNAL_TRAILING: Activated trailing for {stop.BaseId} at ${unrealizedPnL:F2} profit");
                        }
                    }
                    
                    // If active, calculate and update stop level
                    if (stop.IsActive)
                    {
                        double newStopLevel = CalculateInternalStopLevel(stop, position, currentPrice);
                        
                        // Only update if stop level changed significantly
                        bool shouldUpdateStop = Math.Abs(newStopLevel - stop.StopLevel) >= position.Instrument.MasterInstrument.TickSize;
                        
                        if (shouldUpdateStop)
                        {
                            stop.StopLevel = newStopLevel;
                            stop.StopUpdateCount++;
                            LogAndPrint($"INTERNAL_TRAILING: Updated stop for {stop.BaseId} to {newStopLevel:F2}");
                        }
                        
                        // Check if current price hit the stop
                        bool stopHit = false;
                        if (position.MarketPosition == MarketPosition.Long && currentPrice <= stop.StopLevel)
                        {
                            stopHit = true;
                        }
                        else if (position.MarketPosition == MarketPosition.Short && currentPrice >= stop.StopLevel)
                        {
                            stopHit = true;
                        }
                        
                        if (stopHit)
                        {
                            LogAndPrint($"INTERNAL_TRAILING: Stop hit for {stop.BaseId}! Price: {currentPrice:F2}, Stop: {stop.StopLevel:F2}");
                            ExecuteInternalTrailingStop(stop, position, monitoredAccount);
                        }
                        else
                        {
                            // Send update to bridge for MT5 synchronization if stop changed
                            if (shouldUpdateStop && internalStops.ContainsKey(stop.BaseId))
                            {
                                // CRITICAL FIX: Only send updates when stop level changes significantly and is valid
                                double previousStopLevel = stop.LastSentStopLevel;
                                double currentStopLevel = stop.StopLevel;
                                
                                bool stopLevelValid = currentStopLevel > 0;
                                bool stopLevelChanged = Math.Abs(currentStopLevel - previousStopLevel) >= 0.25; // 1 tick
                                bool enoughTimePassed = (DateTime.Now - stop.LastUpdate).TotalMilliseconds >= 1000; // 1 second minimum
                                
                                if (stopLevelValid && (stopLevelChanged || enoughTimePassed))
                                {
                                    stop.LastUpdate = DateTime.Now;
                                    stop.LastSentStopLevel = currentStopLevel;
                                    Task.Run(() => SendTrailingStopUpdate(stop.BaseId, currentStopLevel, currentPrice));
                                    LogAndPrint($"INTERNAL_TRAILING: Sent stop update for {stop.BaseId} - Stop: {currentStopLevel}, Price: {currentPrice}");
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogAndPrint($"ERROR in internal trailing monitor: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Calculate internal stop level based on PROFIT-BASED trailing logic (not price-based)
        /// </summary>
        private double CalculateInternalStopLevel(InternalTrailingStop stop, Position position, double currentPrice)
        {
            double tickSize = position.Instrument.MasterInstrument.TickSize;
            double pointValue = position.Instrument.MasterInstrument.PointValue;
            double entryPrice = stop.EntryPrice;
            double currentPnL = position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, currentPrice);
            
            LogAndPrint($"INTERNAL_TRAILING_CALC: [{stop.BaseId}] PROFIT-BASED CALCULATION - Entry: {entryPrice:F2}, Current: {currentPrice:F2}, P&L: ${currentPnL:F2}");
            
            double newStopLevel = stop.StopLevel; // Default to current stop
            
            // Use PROFIT-BASED trailing settings (same logic as CalculateAlternativeTrailingStopPrice)
            switch (TrailingStopType)
            {
                case TrailingActivationType.Dollars:
                    double initialLockedProfit = TrailingStopValue; // $50 initially locked
                    double trailingIncrement = TrailingIncrementsValue; // $10 steps
                    double triggerLevel = TrailingTriggerValue; // $100 activation level
                    
                    // Calculate how many $10 increments of profit we have beyond the trigger level
                    double extraProfit = Math.Max(0, currentPnL - triggerLevel);
                    double incrementsEarned = Math.Floor(extraProfit / trailingIncrement);
                    double totalLockedProfit = initialLockedProfit + (incrementsEarned * trailingIncrement);
                    
                    // Convert locked profit to price points
                    double lockedProfitInPoints = totalLockedProfit / (position.Quantity * pointValue);
                    
                    if (position.MarketPosition == MarketPosition.Long)
                    {
                        // Long: Stop ABOVE entry (favorable) = Entry + LockedProfit
                        newStopLevel = entryPrice + lockedProfitInPoints;
                    }
                    else
                    {
                        // Short: Stop BELOW entry (favorable) = Entry - LockedProfit  
                        newStopLevel = entryPrice - lockedProfitInPoints;
                    }
                    
                    LogAndPrint($"INTERNAL_TRAILING_CALC: [{stop.BaseId}] PROFIT-LOCKING - CurrentP&L: ${currentPnL:F2}, ExtraProfit: ${extraProfit:F2}, Increments: {incrementsEarned}, LockedProfit: ${totalLockedProfit:F2}, NEW Stop: {newStopLevel:F2}");
                    break;
                    
                case TrailingActivationType.Pips:
                    double pipValue = GetPipValueForInstrument(position.Instrument);
                    double profitBufferPips = TrailingStopValue + 10.0;
                    double trailingIncrementPips = TrailingIncrementsValue;
                    
                    double currentPips = Math.Abs(currentPrice - entryPrice) / pipValue;
                    double extraPips = Math.Max(0, currentPips - profitBufferPips);
                    double incrementsEarnedPips = Math.Floor(extraPips / trailingIncrementPips);
                    double totalPipProtection = profitBufferPips + (incrementsEarnedPips * trailingIncrementPips);
                    
                    if (position.MarketPosition == MarketPosition.Long)
                    {
                        newStopLevel = entryPrice + (totalPipProtection * pipValue);
                    }
                    else
                    {
                        newStopLevel = entryPrice - (totalPipProtection * pipValue);
                    }
                    
                    LogAndPrint($"INTERNAL_TRAILING_CALC: [{stop.BaseId}] PROFIT-BASED PIPS - Current: {currentPips:F2}, Protection: {totalPipProtection:F2}, NEW Stop: {newStopLevel:F2}");
                    break;
                    
                case TrailingActivationType.Ticks:
                    double profitBufferTicks = TrailingStopValue + 10.0;
                    double trailingIncrementTicks = TrailingIncrementsValue;
                    
                    double currentTicks = Math.Abs(currentPrice - entryPrice) / tickSize;
                    double extraTicks = Math.Max(0, currentTicks - profitBufferTicks);
                    double incrementsEarnedTicks = Math.Floor(extraTicks / trailingIncrementTicks);
                    double totalTickProtection = profitBufferTicks + (incrementsEarnedTicks * trailingIncrementTicks);
                    
                    if (position.MarketPosition == MarketPosition.Long)
                    {
                        newStopLevel = entryPrice + (totalTickProtection * tickSize);
                    }
                    else
                    {
                        newStopLevel = entryPrice - (totalTickProtection * tickSize);
                    }
                    
                    LogAndPrint($"INTERNAL_TRAILING_CALC: [{stop.BaseId}] PROFIT-BASED TICKS - Current: {currentTicks:F2}, Protection: {totalTickProtection:F2}, NEW Stop: {newStopLevel:F2}");
                    break;
                    
                case TrailingActivationType.Percent:
                    double profitBufferPercent = TrailingStopValue + 1.0;
                    double trailingIncrementPercent = TrailingIncrementsValue;
                    
                    double currentPercent = Math.Abs((currentPrice - entryPrice) / entryPrice) * 100;
                    double extraPercent = Math.Max(0, currentPercent - profitBufferPercent);
                    double incrementsEarnedPercent = Math.Floor(extraPercent / trailingIncrementPercent);
                    double totalPercentProtection = profitBufferPercent + (incrementsEarnedPercent * trailingIncrementPercent);
                    
                    double percentDistance = (totalPercentProtection / 100.0) * entryPrice;
                    
                    if (position.MarketPosition == MarketPosition.Long)
                    {
                        newStopLevel = entryPrice + percentDistance;
                    }
                    else
                    {
                        newStopLevel = entryPrice - percentDistance;
                    }
                    
                    LogAndPrint($"INTERNAL_TRAILING_CALC: [{stop.BaseId}] PROFIT-BASED PERCENT - Current: {currentPercent:F2}%, Protection: {totalPercentProtection:F2}%, NEW Stop: {newStopLevel:F2}");
                    break;
            }
            
            // Round to tick size
            double roundedStopLevel = Math.Round(newStopLevel / tickSize) * tickSize;
            if (roundedStopLevel != newStopLevel)
            {
                LogAndPrint($"INTERNAL_TRAILING_CALC: [{stop.BaseId}] Rounded stop from {newStopLevel:F6} to {roundedStopLevel:F6}");
            }
            
            return roundedStopLevel;
        }
        
        /// <summary>
        /// Execute immediate market order for internal trailing stop
        /// </summary>
        private void ExecuteInternalTrailingStop(InternalTrailingStop stop, Position position, Account monitoredAccount)
        {
            try
            {
                LogAndPrint($"INTERNAL_TRAILING: Executing market order to close {stop.BaseId}");
                
                // Submit market order to close position
                Order closeOrder = monitoredAccount.CreateOrder(
                    position.Instrument,
                    position.MarketPosition == MarketPosition.Long ? OrderAction.Sell : OrderAction.Buy,
                    OrderType.Market,
                    OrderEntry.Manual,
                    TimeInForce.Day,
                    position.Quantity,
                    0, // Limit price (not used for market orders)
                    0, // Stop price (not used for market orders)
                    string.Empty, // OCO ID
                    $"InternalTrail_{stop.BaseId}_{DateTime.Now:HHmmss}",
                    default(DateTime), // Good till date
                    null // Custom order
                );
                
                monitoredAccount.Submit(new[] { closeOrder });
                
                // Remove from internal tracking - let normal OnExecutionUpdate handle bridge notification
                internalStops.Remove(stop.BaseId);
                
                LogAndPrint($"INTERNAL_TRAILING: Market order submitted for {stop.BaseId}");
            }
            catch (Exception ex)
            {
                LogAndPrint($"ERROR executing internal trailing stop: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Send trailing stop update to bridge via WebSocket for low-latency
        /// </summary>
        private async Task SendTrailingStopUpdate(string baseId, double newStopPrice, double currentPrice)
        {
            var updateData = new Dictionary<string, object>
            {
                { "action", "TRAILING_STOP_UPDATE" },
                { "base_id", baseId },
                { "volume", newStopPrice },  // EA expects stop price in 'volume' field
                { "price", currentPrice },    // EA expects current price in 'price' field
                { "id", Guid.NewGuid().ToString() },  // Unique ID to prevent duplicates
                { "timestamp", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") }
            };

            // WebSocket and HTTP removed - using gRPC communication
            LogAndPrint($"Sending trailing stop update for {baseId}: Stop=${newStopPrice:F2}, Current=${currentPrice:F2}");

            // Send via gRPC
            try
            {
                var json = SimpleJson.SerializeObject(updateData);
                bool success = TradingGrpcClient.SubmitTrailingUpdate(json);

                if (success)
                {
                    LogAndPrint($"Trailing stop update sent via gRPC for {baseId}");
                }
                else
                {
                    string error = TradingGrpcClient.LastError;
                    LogAndPrint($"Failed to send trailing stop update via gRPC: {error}");
                }
            }
            catch (Exception grpcEx)
            {
                LogAndPrint($"ERROR sending trailing stop update via gRPC: {grpcEx.Message}");
            }
        }
        
        #endregion
        
        #region Private Methods - Alternative Trailing and Helper Methods
        
        /// <summary>
        /// Update trailing stop for a position
        /// </summary>
        private void UpdateTrailingStop(ElasticPositionTracker tracker, Position position)
        {
            try
            {
                double unrealizedPips = CalculateUnrealizedPips(position);
                double trailCurrentPrice = GetCurrentPrice(position.Instrument);
                double currentPnL = position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, trailCurrentPrice);
                
                LogAndPrint($"TRAILING_DEBUG: Checking trailing for {tracker.BaseId} - EnableTrailing: {EnableTrailing}, UnrealizedPips: {unrealizedPips:F2}, PnL: ${currentPnL:F2}");
                
                // Determine which trailing system to use
                TrailingActivationType activeMode = UseAlternativeTrailing ? TrailingTriggerType : TrailingActivationMode;
                double activeThreshold = UseAlternativeTrailing ? TrailingTriggerValue : TrailingActivationValue;
                
                LogAndPrint($"TRAILING_DEBUG: UseAlternativeTrailing: {UseAlternativeTrailing}, Active Mode: {activeMode}, Active Threshold: {activeThreshold}, IsTrailingActive: {tracker.IsTrailingActive}");
                
                // Use the UI-configured trailing activation settings
                bool shouldActivate = false;
                double currentValue = 0;
                
                switch (activeMode)
                {
                    case TrailingActivationType.Ticks:
                        currentValue = unrealizedPips / GetPipValueForInstrument(position.Instrument) * position.Instrument.MasterInstrument.TickSize;
                        shouldActivate = currentValue >= activeThreshold;
                        LogAndPrint($"TRAILING_DEBUG: Ticks mode - CurrentValue: {currentValue:F2} ticks, Threshold: {activeThreshold}, ShouldActivate: {shouldActivate}");
                        break;
                        
                    case TrailingActivationType.Pips:
                        currentValue = unrealizedPips;
                        shouldActivate = currentValue >= activeThreshold;
                        LogAndPrint($"TRAILING_DEBUG: Pips mode - CurrentValue: {currentValue:F2} pips, Threshold: {activeThreshold}, ShouldActivate: {shouldActivate}");
                        break;
                        
                    case TrailingActivationType.Dollars:
                        currentValue = currentPnL;
                        shouldActivate = currentValue >= activeThreshold;
                        LogAndPrint($"TRAILING_DEBUG: Dollars mode - CurrentValue: ${currentValue:F2}, Threshold: ${activeThreshold}, ShouldActivate: {shouldActivate}");
                        break;
                        
                    case TrailingActivationType.Percent:
                        currentValue = Math.Abs((trailCurrentPrice - tracker.EntryPrice) / tracker.EntryPrice) * 100;
                        shouldActivate = currentValue >= activeThreshold;
                        LogAndPrint($"TRAILING_DEBUG: Percent mode - CurrentValue: {currentValue:F2}%, Threshold: {activeThreshold}%, ShouldActivate: {shouldActivate}");
                        break;
                }
                
                // Activate trailing if threshold is met and not already active
                if (!tracker.IsTrailingActive && shouldActivate)
                {
                    tracker.IsTrailingActive = true;
                    tracker.HighWaterMarkPrice = trailCurrentPrice;
                    tracker.LowWaterMarkPrice = trailCurrentPrice;
                    LogAndPrint($"TRAILING_ACTIVATED: Trailing activated for {tracker.BaseId} at {activeMode}={currentValue:F2} (threshold: {activeThreshold})");
                }
                
                // If trailing is active, calculate and potentially update the stop
                if (tracker.IsTrailingActive)
                {
                    // Update water marks
                    if (position.MarketPosition == MarketPosition.Long && trailCurrentPrice > tracker.HighWaterMarkPrice)
                    {
                        tracker.HighWaterMarkPrice = trailCurrentPrice;
                        LogAndPrint($"TRAILING_DEBUG: Updated high water mark for {tracker.BaseId} to {trailCurrentPrice:F2}");
                    }
                    else if (position.MarketPosition == MarketPosition.Short && (tracker.LowWaterMarkPrice == 0 || trailCurrentPrice < tracker.LowWaterMarkPrice))
                    {
                        tracker.LowWaterMarkPrice = trailCurrentPrice;
                        LogAndPrint($"TRAILING_DEBUG: Updated low water mark for {tracker.BaseId} to {trailCurrentPrice:F2}");
                    }
                    
                    // Calculate new stop price
                    double newStopPrice = UseAlternativeTrailing ? 
                        CalculateAlternativeTrailingStopPrice(tracker, position) :
                        CalculateTrailingStopPrice(tracker, position);
                    
                    // Check if we should update the stop
                    bool shouldUpdate = ShouldUpdateTrailingStop(tracker, position);
                    bool significantChange = Math.Abs(newStopPrice - tracker.CurrentTrailedStopPrice) >= position.Instrument.MasterInstrument.TickSize;
                    
                    LogAndPrint($"TRAILING_DEBUG: Stop calculation - Current: {tracker.CurrentTrailedStopPrice:F2}, New: {newStopPrice:F2}, ShouldUpdate: {shouldUpdate}, SignificantChange: {significantChange}");
                    
                    if (shouldUpdate && significantChange)
                    {
                        // For alternative trailing, use internal stops tracking
                        if (UseAlternativeTrailing)
                        {
                            if (internalStops.ContainsKey(tracker.BaseId))
                            {
                                internalStops[tracker.BaseId].StopLevel = newStopPrice;
                                internalStops[tracker.BaseId].LastUpdate = DateTime.Now;
                                LogAndPrint($"INTERNAL_TRAILING: Updated stop level for {tracker.BaseId} to {newStopPrice:F2}");
                            }
                            
                            // Send update to bridge for MT5 synchronization
                            Task.Run(() => SendTrailingStopUpdate(tracker.BaseId, newStopPrice, trailCurrentPrice));
                            tracker.CurrentTrailedStopPrice = newStopPrice;
                            LogAndPrint($"Trailing stop updated for {tracker.BaseId}: {newStopPrice:F2}");
                            LogAndPrint($"Trailing stop update sent successfully for {tracker.BaseId}");
                        }
                        else
                        {
                            // Traditional system - submit/update stop orders in NT
                            if (tracker.ManagedStopOrder == null)
                            {
                                SubmitTrailingStopOrder(tracker, position);
                            }
                            else
                            {
                                UpdateTrailingStopOrder(tracker, newStopPrice);
                            }
                            tracker.CurrentTrailedStopPrice = newStopPrice;
                        }
                    }
                    else
                    {
                        LogAndPrint($"TRAILING_UPDATE_SKIP: Not updating stop for {tracker.BaseId} - ShouldUpdate: {shouldUpdate}, SignificantChange: {significantChange}");
                    }
                }
            }
            catch (Exception ex)
            {
                LogAndPrint($"ERROR updating trailing stop for {tracker.BaseId}: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Calculate trailing stop price using alternative trailing settings
        /// </summary>
        private double CalculateAlternativeTrailingStopPrice(ElasticPositionTracker tracker, Position position)
        {
            try
            {
                double currentPrice = GetCurrentPrice(position.Instrument);
                if (currentPrice == 0) return tracker.CurrentTrailedStopPrice;
                
                double entryPrice = position.AveragePrice;
                double currentPnL = position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, currentPrice);
                double pointValue = position.Instrument.MasterInstrument.PointValue;
                
                LogAndPrint($"TRAILING_DEBUG: PROFIT-BASED TRAILING - Entry: {entryPrice:F2}, Current: {currentPrice:F2}, P&L: ${currentPnL:F2}, StopValue: ${TrailingStopValue}, IncrementsValue: ${TrailingIncrementsValue}");
                
                double stopPrice = tracker.CurrentTrailedStopPrice;
                
                switch (TrailingStopType)
                {
                    case TrailingActivationType.Dollars:
                        // CORRECT LOGIC: Stop locks in profit levels, moves favorably from entry
                        // Initial stop locks in TrailingStopValue ($50) profit
                        // Then increases locked profit by TrailingIncrementsValue ($10) for each $10 additional P&L
                        
                        double initialLockedProfit = TrailingStopValue; // $50 initially locked
                        double trailingIncrement = TrailingIncrementsValue; // $10 steps
                        double triggerLevel = TrailingTriggerValue; // $100 activation level
                        
                        // Calculate how many $10 increments of profit we have beyond the trigger level
                        double extraProfit = Math.Max(0, currentPnL - triggerLevel);
                        double incrementsEarned = Math.Floor(extraProfit / trailingIncrement);
                        double totalLockedProfit = initialLockedProfit + (incrementsEarned * trailingIncrement);
                        
                        // Convert locked profit to price points
                        double lockedProfitInPoints = totalLockedProfit / (position.Quantity * pointValue);
                        
                        if (position.MarketPosition == MarketPosition.Long)
                        {
                            // Long: Stop ABOVE entry (favorable) = Entry + LockedProfit
                            stopPrice = entryPrice + lockedProfitInPoints;
                        }
                        else
                        {
                            // Short: Stop BELOW entry (favorable) = Entry - LockedProfit  
                            stopPrice = entryPrice - lockedProfitInPoints;
                        }
                        
                        LogAndPrint($"TRAILING_DEBUG: PROFIT-LOCKING CALCULATION - CurrentP&L: ${currentPnL:F2}, ExtraProfit: ${extraProfit:F2}, Increments: {incrementsEarned}, LockedProfit: ${totalLockedProfit:F2}, NEW Stop: {stopPrice:F2}");
                        break;
                        
                    case TrailingActivationType.Pips:
                        // PROFIT-BASED PIP TRAILING: Similar logic but using pips instead of dollars
                        double pipValue = GetPipValueForInstrument(position.Instrument);
                        double profitBufferPips = TrailingStopValue + 10.0; // Initial pip protection
                        double trailingIncrementPips = TrailingIncrementsValue; // Pip increments
                        
                        double currentPips = Math.Abs(currentPrice - entryPrice) / pipValue;
                        double extraPips = Math.Max(0, currentPips - profitBufferPips);
                        double incrementsEarnedPips = Math.Floor(extraPips / trailingIncrementPips);
                        double totalPipProtection = profitBufferPips + (incrementsEarnedPips * trailingIncrementPips);
                        
                        if (position.MarketPosition == MarketPosition.Long)
                        {
                            stopPrice = entryPrice + (totalPipProtection * pipValue);
                        }
                        else
                        {
                            stopPrice = entryPrice - (totalPipProtection * pipValue);
                        }
                        
                        LogAndPrint($"TRAILING_DEBUG: PROFIT-BASED PIPS - CurrentPips: {currentPips:F2}, Protection: {totalPipProtection:F2}, NEW Stop: {stopPrice:F2}");
                        break;
                        
                    case TrailingActivationType.Ticks:
                        // PROFIT-BASED TICK TRAILING: Similar logic but using ticks
                        double tickSizeForTicks = position.Instrument.MasterInstrument.TickSize;
                        double profitBufferTicks = TrailingStopValue + 10.0; // Initial tick protection
                        double trailingIncrementTicks = TrailingIncrementsValue; // Tick increments
                        
                        double currentTicks = Math.Abs(currentPrice - entryPrice) / tickSizeForTicks;
                        double extraTicks = Math.Max(0, currentTicks - profitBufferTicks);
                        double incrementsEarnedTicks = Math.Floor(extraTicks / trailingIncrementTicks);
                        double totalTickProtection = profitBufferTicks + (incrementsEarnedTicks * trailingIncrementTicks);
                        
                        if (position.MarketPosition == MarketPosition.Long)
                        {
                            stopPrice = entryPrice + (totalTickProtection * tickSizeForTicks);
                        }
                        else
                        {
                            stopPrice = entryPrice - (totalTickProtection * tickSizeForTicks);
                        }
                        
                        LogAndPrint($"TRAILING_DEBUG: PROFIT-BASED TICKS - CurrentTicks: {currentTicks:F2}, Protection: {totalTickProtection:F2}, NEW Stop: {stopPrice:F2}");
                        break;
                        
                    case TrailingActivationType.Percent:
                        // PROFIT-BASED PERCENT TRAILING: Calculate based on percentage of entry price
                        double profitBufferPercent = TrailingStopValue + 1.0; // Initial percent protection
                        double trailingIncrementPercent = TrailingIncrementsValue; // Percent increments
                        
                        double currentPercent = Math.Abs((currentPrice - entryPrice) / entryPrice) * 100;
                        double extraPercent = Math.Max(0, currentPercent - profitBufferPercent);
                        double incrementsEarnedPercent = Math.Floor(extraPercent / trailingIncrementPercent);
                        double totalPercentProtection = profitBufferPercent + (incrementsEarnedPercent * trailingIncrementPercent);
                        
                        double percentDistance = (totalPercentProtection / 100.0) * entryPrice;
                        
                        if (position.MarketPosition == MarketPosition.Long)
                        {
                            stopPrice = entryPrice + percentDistance;
                        }
                        else
                        {
                            stopPrice = entryPrice - percentDistance;
                        }
                        
                        LogAndPrint($"TRAILING_DEBUG: PROFIT-BASED PERCENT - CurrentPercent: {currentPercent:F2}%, Protection: {totalPercentProtection:F2}%, NEW Stop: {stopPrice:F2}");
                        break;
                }
                
                // Round to valid tick size
                double finalTickSize = position.Instrument.MasterInstrument.TickSize;
                stopPrice = Math.Round(stopPrice / finalTickSize) * finalTickSize;
                
                LogAndPrint($"TRAILING_DEBUG: Alternative trailing stop calculated - Final Stop: {stopPrice:F2} (rounded to tick size {finalTickSize})");
                
                return stopPrice;
            }
            catch (Exception ex)
            {
                LogAndPrint($"ERROR calculating alternative trailing stop price: {ex.Message}");
                return tracker.CurrentTrailedStopPrice;
            }
        }
        
        /// <summary>
        /// Calculate trailing stop price based on configured type
        /// </summary>
        private double CalculateTrailingStopPrice(ElasticPositionTracker tracker, Position position)
        {
            double stopPrice = tracker.CurrentTrailedStopPrice;
            double pointValue = position.Instrument.MasterInstrument.PointValue;
            double tickSize = position.Instrument.MasterInstrument.TickSize;
            bool isLong = position.MarketPosition == MarketPosition.Long;
            
            try
            {
                switch (TrailingType)
                {
                    case ContinuousTrailingType.DollarAmountTrail:
                        double dollarOffset = DollarTrailDistance / pointValue;
                        stopPrice = isLong
                            ? tracker.HighWaterMarkPrice - dollarOffset
                            : tracker.LowWaterMarkPrice + dollarOffset;
                        break;
                        
                    case ContinuousTrailingType.PipTrail:
                        double pipValue = GetPipValueForInstrument(position.Instrument);
                        double pipOffset = PipTrailDistance * pipValue;
                        stopPrice = isLong
                            ? tracker.HighWaterMarkPrice - pipOffset
                            : tracker.LowWaterMarkPrice + pipOffset;
                        break;
                        
                    case ContinuousTrailingType.TickTrail:
                        double tickOffset = TickTrailDistance * tickSize;
                        stopPrice = isLong
                            ? tracker.HighWaterMarkPrice - tickOffset
                            : tracker.LowWaterMarkPrice + tickOffset;
                        break;
                        
                    case ContinuousTrailingType.DEMAAtrTrail:
                        if (tracker.CurrentAtrValue > 0 && tracker.CurrentDemaValue > 0)
                        {
                            double atrOffset = tracker.CurrentAtrValue * AtrMultiplier;
                            stopPrice = isLong
                                ? tracker.CurrentDemaValue - atrOffset
                                : tracker.CurrentDemaValue + atrOffset;
                        }
                        break;
                        
                    case ContinuousTrailingType.StepTrail:
                        stopPrice = CalculateStepTrailPrice(tracker, position);
                        break;
                        
                    case ContinuousTrailingType.None:
                    default:
                        // No trailing, keep current stop
                        break;
                }
                
                // Ensure stop doesn't move against us
                if (isLong && stopPrice < tracker.CurrentTrailedStopPrice)
                    stopPrice = tracker.CurrentTrailedStopPrice;
                else if (!isLong && stopPrice > tracker.CurrentTrailedStopPrice)
                    stopPrice = tracker.CurrentTrailedStopPrice;
                
                // Round to valid tick size
                return Math.Round(stopPrice / tickSize) * tickSize;
            }
            catch (Exception ex)
            {
                LogAndPrint($"Error calculating trailing stop price: {ex.Message}");
                return tracker.CurrentTrailedStopPrice;
            }
        }
        
        /// <summary>
        /// Check if enough time has passed to update trailing stop based on frequency settings
        /// </summary>
        private bool ShouldUpdateTrailingStop(ElasticPositionTracker tracker, Position position)
        {
            try
            {
                // Determine which frequency settings to use
                TrailingActivationType frequencyType = UseAlternativeTrailing ? TrailingIncrementsType : TrailingActivationType.Dollars;
                double frequencyValue = UseAlternativeTrailing ? TrailingIncrementsValue : 1.0; // Default to 1 second for main system
                
                DateTime now = DateTime.Now;
                TimeSpan timeSinceLastUpdate = now - tracker.LastUpdateTime;
                
                double requiredInterval = 0;
                
                if (UseAlternativeTrailing)
                {
                    switch (frequencyType)
                    {
                        case TrailingActivationType.Dollars:
                            // For dollars, use it as profit change requirement
                            double freqCurrentPrice = GetCurrentPrice(position.Instrument);
                            double currentPnL = position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, freqCurrentPrice);
                            double pnlChange = Math.Abs(currentPnL - tracker.LastReportedProfitLevel);
                            bool shouldUpdate = pnlChange >= frequencyValue;
                            LogAndPrint($"TRAILING_INCREMENTS_DEBUG: Dollar-based increments - PnL change: ${pnlChange:F2}, Required: ${frequencyValue}, Should update: {shouldUpdate}");
                            return shouldUpdate;
                            
                        case TrailingActivationType.Ticks:
                            // For ticks, use it as price change requirement
                            double currentPrice = GetCurrentPrice(position.Instrument);
                            double priceChange = Math.Abs(currentPrice - tracker.EntryPrice) / position.Instrument.MasterInstrument.TickSize;
                            shouldUpdate = priceChange >= frequencyValue;
                            LogAndPrint($"TRAILING_INCREMENTS_DEBUG: Tick-based increments - Tick change: {priceChange:F1}, Required: {frequencyValue}, Should update: {shouldUpdate}");
                            return shouldUpdate;
                            
                        case TrailingActivationType.Pips:
                            // For pips, use it as pip movement requirement
                            currentPrice = GetCurrentPrice(position.Instrument);
                            double pipValue = GetPipValueForInstrument(position.Instrument);
                            double pipChange = Math.Abs(currentPrice - tracker.EntryPrice) / pipValue;
                            shouldUpdate = pipChange >= frequencyValue;
                            LogAndPrint($"TRAILING_INCREMENTS_DEBUG: Pip-based increments - Pip change: {pipChange:F2}, Required: {frequencyValue}, Should update: {shouldUpdate}");
                            return shouldUpdate;
                            
                        case TrailingActivationType.Percent:
                            // For percent, use it as seconds interval
                            requiredInterval = frequencyValue; // Treat percent value as seconds
                            break;
                    }
                }
                else
                {
                    // Main system - update every second
                    requiredInterval = 1.0;
                }
                
                bool timeElapsed = timeSinceLastUpdate.TotalSeconds >= requiredInterval;
                LogAndPrint($"TRAILING_INCREMENTS_DEBUG: Time-based increments - Elapsed: {timeSinceLastUpdate.TotalSeconds:F1}s, Required: {requiredInterval}s, Should update: {timeElapsed}");
                return timeElapsed;
            }
            catch (Exception ex)
            {
                LogAndPrint($"ERROR checking trailing frequency: {ex.Message}");
                return true; // Default to allowing update on error
            }
        }
        
        #endregion
        
        #region Helper Methods
        
        /// <summary>
        /// Calculate step trail stop price based on profit levels
        /// </summary>
        private double CalculateStepTrailPrice(ElasticPositionTracker tracker, Position position)
        {
            try
            {
                // Parse step trail levels from CSV (format: "20:10,40:20,60:30" = profit:stopDistance)
                var stepLevels = ParseStepTrailLevels(StepTrailLevelsCsv);
                if (stepLevels.Count == 0)
                    return tracker.CurrentTrailedStopPrice;
                
                // Calculate current profit in pips
                double currentProfit = CalculateUnrealizedPips(position);
                
                // Find applicable step level
                double stopDistance = 0;
                foreach (var level in stepLevels.OrderByDescending(x => x.Key))
                {
                    if (currentProfit >= level.Key)
                    {
                        stopDistance = level.Value;
                        break;
                    }
                }
                
                if (stopDistance == 0)
                    return tracker.CurrentTrailedStopPrice;
                
                // Calculate stop price based on step distance
                double pipValue = GetPipValueForInstrument(position.Instrument);
                double stopOffset = stopDistance * pipValue;
                
                bool isLong = position.MarketPosition == MarketPosition.Long;
                return isLong
                    ? tracker.HighWaterMarkPrice - stopOffset
                    : tracker.LowWaterMarkPrice + stopOffset;
            }
            catch (Exception ex)
            {
                LogAndPrint($"Error calculating step trail price: {ex.Message}");
                return tracker.CurrentTrailedStopPrice;
            }
        }
        
        /// <summary>
        /// Parse step trail levels from CSV string
        /// </summary>
        private Dictionary<double, double> ParseStepTrailLevels(string csvLevels)
        {
            var levels = new Dictionary<double, double>();
            
            try
            {
                if (string.IsNullOrEmpty(csvLevels))
                    return levels;
                
                var pairs = csvLevels.Split(',');
                foreach (var pair in pairs)
                {
                    var parts = pair.Split(':');
                    if (parts.Length == 2 && 
                        double.TryParse(parts[0].Trim(), out double profit) &&
                        double.TryParse(parts[1].Trim(), out double stopDistance))
                    {
                        levels[profit] = stopDistance;
                    }
                }
            }
            catch (Exception ex)
            {
                LogAndPrint($"Error parsing step trail levels: {ex.Message}");
            }
            
            return levels;
        }
        
        /// <summary>
        /// Get pip value for instrument (handles Forex vs other instruments)
        /// </summary>
        private double GetPipValueForInstrument(Instrument instrument)
        {
            try
            {
                // For Forex, a pip is typically 0.0001 (or 0.01 for JPY pairs)
                if (instrument.MasterInstrument.InstrumentType == InstrumentType.Forex)
                {
                    // Check if this is a JPY pair (pips are 0.01)
                    if (instrument.FullName.Contains("JPY"))
                        return 0.01;
                    else
                        return 0.0001;
                }
                
                // For futures and other instruments, use tick size
                return instrument.MasterInstrument.TickSize;
            }
            catch (Exception ex)
            {
                LogAndPrint($"Error getting pip value for {instrument.FullName}: {ex.Message}");
                return instrument.MasterInstrument.TickSize;
            }
        }
        
        /// <summary>
        /// Calculate unrealized pips for a position
        /// </summary>
        private double CalculateUnrealizedPips(Position position)
        {
            try
            {
                if (position.Quantity == 0)
                    return 0;
                
                double currentPrice = GetCurrentMarketPrice(position.Instrument);
                double entryPrice = position.AveragePrice;
                double pipValue = GetPipValueForInstrument(position.Instrument);
                
                bool isLong = position.MarketPosition == MarketPosition.Long;
                double priceDiff = isLong ? currentPrice - entryPrice : entryPrice - currentPrice;
                
                return priceDiff / pipValue;
            }
            catch (Exception ex)
            {
                LogAndPrint($"Error calculating unrealized pips: {ex.Message}");
                return 0;
            }
        }
        
        /// <summary>
        /// Get current market price for instrument
        /// </summary>
        private double GetCurrentMarketPrice(Instrument instrument)
        {
            try
            {
                // Use last price from market data
                return instrument.MarketData.Last?.Price ?? instrument.MarketData.Ask?.Price ?? 0;
            }
            catch (Exception ex)
            {
                LogAndPrint($"Error getting market price for {instrument.FullName}: {ex.Message}");
                return 0;
            }
        }
        
        /// <summary>
        /// Get current price for position calculations
        /// </summary>
        private double GetCurrentPrice(Instrument instrument)
        {
            return GetCurrentMarketPrice(instrument);
        }
        
        /// <summary>
        /// Submit trailing stop order to NinjaTrader
        /// </summary>
        private void SubmitTrailingStopOrder(ElasticPositionTracker tracker, Position position)
        {
            try
            {
                // This would need access to the monitored account
                // For now, just log - this functionality may need to be handled in MultiStratManager
                LogAndPrint($"SubmitTrailingStopOrder called for {tracker.BaseId} - would submit stop order here");
            }
            catch (Exception ex)
            {
                LogAndPrint($"Error submitting trailing stop order for {tracker.BaseId}: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Update existing trailing stop order
        /// </summary>
        private void UpdateTrailingStopOrder(ElasticPositionTracker tracker, double newStopPrice)
        {
            try
            {
                // This would need access to the monitored account
                // For now, just log - this functionality may need to be handled in MultiStratManager
                LogAndPrint($"UpdateTrailingStopOrder called for {tracker.BaseId} with new price {newStopPrice:F2} - would update stop order here");
            }
            catch (Exception ex)
            {
                LogAndPrint($"Error updating trailing stop order for {tracker.BaseId}: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Handle bars update event for indicators
        /// </summary>
        private void OnBarsUpdate(object sender, BarsUpdateEventArgs e)
        {
            try
            {
                BarsRequest barsRequest = sender as BarsRequest;
                if (barsRequest == null)
                    return;
                
                string instrumentKey = barsRequest.Bars.Instrument.FullName;
                
                // Find position tracker for this instrument
                var positionTracker = elasticPositions.Values.FirstOrDefault(p => 
                    p.InstrumentName == instrumentKey);
                
                if (positionTracker == null)
                    return;
                
                // Update quote buffer for ATR calculations
                UpdateQuoteBuffer(barsRequest, positionTracker);
                
                // Calculate ATR and DEMA values
                CalculateIndicators(positionTracker);
                
                // Update last processed time
                positionTracker.LastBarTimeProcessed = DateTime.Now;
            }
            catch (Exception ex)
            {
                LogAndPrint($"Error in OnBarsUpdate: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Update quote buffer for indicator calculations
        /// </summary>
        private void UpdateQuoteBuffer(BarsRequest barsRequest, ElasticPositionTracker tracker)
        {
            try
            {
                var bars = barsRequest.Bars;
                int count = bars.Count;
                
                // Clear existing buffer
                tracker.QuoteBuffer.Clear();
                
                // Add historical bars to quote buffer (limit to reasonable size)
                int startIndex = Math.Max(0, count - (AtrPeriod + DEMA_ATR_Period + 20));
                
                for (int i = startIndex; i < count; i++)
                {
                    tracker.QuoteBuffer.Add(new NinjaTrader.NinjaScript.AddOns.Quote
                    {
                        Date = bars.GetTime(i),
                        Open = bars.GetOpen(i),
                        High = bars.GetHigh(i),
                        Low = bars.GetLow(i),
                        Close = bars.GetClose(i),
                        Volume = bars.GetVolume(i)
                    });
                }
            }
            catch (Exception ex)
            {
                LogAndPrint($"Error updating quote buffer: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Calculate DEMA and ATR indicators using IndicatorCalculator
        /// </summary>
        private void CalculateIndicators(ElasticPositionTracker tracker)
        {
            try
            {
                // QuoteBuffer now contains the correct Quote type
                if (!IndicatorCalculator.ValidateQuoteData(tracker.QuoteBuffer, Math.Max(AtrPeriod, DEMA_ATR_Period)))
                    return;
                
                // Calculate ATR using IndicatorCalculator
                double? atrValue = IndicatorCalculator.CalculateAtr(tracker.QuoteBuffer, AtrPeriod);
                if (atrValue.HasValue)
                {
                    tracker.CurrentAtrValue = atrValue.Value;
                }
                
                // Calculate DEMA using IndicatorCalculator
                double? demaValue = IndicatorCalculator.CalculateDema(tracker.QuoteBuffer, DEMA_ATR_Period);
                if (demaValue.HasValue)
                {
                    tracker.CurrentDemaValue = demaValue.Value;
                }
                
                LogAndPrint($"Indicators updated for {tracker.InstrumentName}: ATR={tracker.CurrentAtrValue:F4}, DEMA={tracker.CurrentDemaValue:F4}");
            }
            catch (Exception ex)
            {
                LogAndPrint($"Error calculating indicators: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Handle trailing stop order updates
        /// </summary>
        public void HandleTrailingStopOrderUpdate(Order order)
        {
            try
            {
                // Find the tracker for this trailing stop order
                var tracker = elasticPositions.Values.FirstOrDefault(t => 
                    t.ManagedStopOrder != null && t.ManagedStopOrder.OrderId == order.OrderId);
                
                if (tracker != null)
                {
                    OnTrailingStopOrderUpdate(order, tracker);
                }
            }
            catch (Exception ex)
            {
                LogAndPrint($"Error handling trailing stop order update: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Handle order updates for trailing stop orders
        /// </summary>
        private void OnTrailingStopOrderUpdate(Order order, ElasticPositionTracker tracker)
        {
            try
            {
                if (order == null || tracker == null)
                    return;
                
                LogAndPrint($"Trailing stop order update for {tracker.BaseId}: {order.OrderState}");
                
                switch (order.OrderState)
                {
                    case OrderState.Filled:
                        LogAndPrint($"Trailing stop order filled for {tracker.BaseId} at {order.AverageFillPrice:F2}");
                        // Position was stopped out - deactivate trailing
                        tracker.IsTrailingActive = false;
                        tracker.ManagedStopOrder = null;
                        break;
                        
                    case OrderState.Cancelled:
                    case OrderState.Rejected:
                        LogAndPrint($"Trailing stop order {order.OrderState} for {tracker.BaseId}");
                        tracker.ManagedStopOrder = null;
                        break;
                        
                    case OrderState.CancelPending:
                    case OrderState.ChangePending:
                        // Order is being modified - no action needed
                        break;
                        
                    case OrderState.Working:
                    case OrderState.Accepted:
                        LogAndPrint($"Trailing stop order active for {tracker.BaseId} at {order.StopPrice:F2}");
                        break;
                }
            }
            catch (Exception ex)
            {
                LogAndPrint($"Error handling trailing stop order update: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Add position to elastic tracking when trade is filled but no position found
        /// </summary>
        public void AddElasticPositionTrackingFromExecution(string baseId, Execution execution)
        {
            LogAndPrint($"ELASTIC_ADD_DEBUG: AddElasticPositionTrackingFromExecution called - BaseId: {baseId}, EnableElasticHedging: {EnableElasticHedging}");
            
            if (!EnableElasticHedging) 
            {
                LogAndPrint($"ELASTIC_ADD_DEBUG: Elastic hedging disabled, skipping tracking for {baseId}");
                return;
            }
            
            var tracker = new ElasticPositionTracker
            {
                BaseId = baseId,
                EntryPrice = execution.Price,
                LastReportedProfitLevel = 0,
                ProfitUpdatesSent = 0,
                LastUpdateTime = DateTime.Now,
                InstrumentFullName = execution.Instrument.FullName,
                MarketPosition = execution.MarketPosition,
                IsTrailingActive = false,
                CurrentTrailedStopPrice = 0,
                HighWaterMarkPrice = execution.Price,
                LowWaterMarkPrice = execution.Price,
                IsSLTPLogicCompleteForEntry = false
            };
            
            elasticPositions[baseId] = tracker;
            LogAndPrint($"ELASTIC_ADD_DEBUG: ✅ Added elastic tracking for {baseId} using execution data. Total tracked: {elasticPositions.Count}");
        }
        
        #endregion
        
        #region Traditional (Broker-Side) Trailing Stop Methods
        
        /// <summary>
        /// Monitor traditional trailing stops using actual broker-side stop orders
        /// </summary>
        private void MonitorTraditionalTrailingStops(Account monitoredAccount)
        {
            try
            {
                if (monitoredAccount == null) return;
                
                LogAndPrint($"TRADITIONAL_TRAILING_DEBUG: Monitoring {traditionalTrailingStops.Count} traditional stops");
                
                // First, check existing positions for new trailing candidates
                foreach (var position in monitoredAccount.Positions.Where(p => p.MarketPosition != MarketPosition.Flat))
                {
                    // Generate baseId from position (you may need to adjust this based on your baseId generation logic)
                    string baseId = GetBaseIdFromPosition(position);
                    
                    if (string.IsNullOrEmpty(baseId))
                    {
                        LogAndPrint($"TRADITIONAL_TRAILING_DEBUG: No baseId found for position {position.Instrument.FullName}");
                        continue;
                    }
                    
                    // Check if we already have a traditional stop for this position
                    if (!traditionalTrailingStops.ContainsKey(baseId))
                    {
                        CheckAndInitializeTraditionalTrailing(baseId, position, monitoredAccount);
                    }
                }
                
                // Then, update existing traditional stops
                var stopsToUpdate = traditionalTrailingStops.Values.ToList();
                foreach (var stop in stopsToUpdate)
                {
                    UpdateTraditionalTrailingStop(stop, monitoredAccount);
                }
                
                // Clean up stops for closed positions
                CleanupClosedPositionStops(monitoredAccount);
            }
            catch (Exception ex)
            {
                LogAndPrint($"ERROR in MonitorTraditionalTrailingStops: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Check if a position should have traditional trailing and initialize it
        /// </summary>
        private void CheckAndInitializeTraditionalTrailing(string baseId, Position position, Account account)
        {
            try
            {
                double currentPrice = GetCurrentPrice(position.Instrument);
                if (currentPrice == 0) return;
                
                double unrealizedPnL = position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, currentPrice);
                double pointsFromEntry = Math.Abs(currentPrice - position.AveragePrice);
                double percentFromEntry = (pointsFromEntry / position.AveragePrice) * 100;
                
                // Check if trailing should be activated
                bool shouldActivate = false;
                
                switch (TrailingTriggerType)
                {
                    case TrailingActivationType.Dollars:
                        shouldActivate = unrealizedPnL >= TrailingTriggerValue;
                        break;
                    case TrailingActivationType.Percent:
                        shouldActivate = percentFromEntry >= TrailingTriggerValue;
                        break;
                    case TrailingActivationType.Ticks:
                        double ticks = pointsFromEntry / position.Instrument.MasterInstrument.TickSize;
                        shouldActivate = ticks >= TrailingTriggerValue;
                        break;
                    case TrailingActivationType.Pips:
                        double pips = pointsFromEntry / GetPipSize(position.Instrument);
                        shouldActivate = pips >= TrailingTriggerValue;
                        break;
                }
                
                if (shouldActivate)
                {
                    LogAndPrint($"TRADITIONAL_TRAILING: Activating for {baseId} at ${unrealizedPnL:F2} profit");
                    SubmitInitialTraditionalStop(baseId, position, account, currentPrice);
                }
            }
            catch (Exception ex)
            {
                LogAndPrint($"ERROR in CheckAndInitializeTraditionalTrailing: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Submit initial traditional stop order
        /// </summary>
        private void SubmitInitialTraditionalStop(string baseId, Position position, Account account, double currentPrice)
        {
            try
            {
                // Calculate initial stop level using existing logic
                double stopPrice = CalculateTraditionalStopPrice(position, currentPrice, true);
                
                if (stopPrice <= 0)
                {
                    LogAndPrint($"TRADITIONAL_TRAILING: Invalid stop price calculated for {baseId}");
                    return;
                }
                
                // Determine order action (opposite of position)
                OrderAction orderAction = position.MarketPosition == MarketPosition.Long ? 
                    OrderAction.Sell : OrderAction.Buy;
                
                // Create stop order with specific naming convention
                string orderName = $"MSM_TRAIL_STOP_{baseId}";
                
                LogAndPrint($"TRADITIONAL_TRAILING: Submitting initial stop for {baseId} at {stopPrice:F2}");
                
                Order stopOrder = account.CreateOrder(
                    position.Instrument,
                    orderAction,
                    OrderType.StopMarket,
                    OrderEntry.Manual,
                    TimeInForce.Day,
                    position.Quantity,
                    0,                    // Limit price (not used for stop market)
                    stopPrice,            // Stop price
                    string.Empty,         // OCO
                    orderName,
                    default(DateTime),
                    null
                );
                
                account.Submit(new[] { stopOrder });
                
                // Create tracking object
                var tracker = new TraditionalTrailingStop
                {
                    BaseId = baseId,
                    CurrentStopOrder = stopOrder,
                    LastStopPrice = stopPrice,
                    LastModificationTime = DateTime.Now,
                    ModificationCount = 0,
                    TrackedPosition = position,
                    EntryPrice = position.AveragePrice,
                    IsActive = true,
                    ActivationTime = DateTime.Now,
                    ActivationPrice = currentPrice,
                    MaxProfit = position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, currentPrice),
                    InitialStopDistance = Math.Abs(stopPrice - position.AveragePrice),
                    LastKnownOrderState = OrderState.Submitted,
                    LastOrderStateUpdate = DateTime.Now,
                    IsPendingModification = false,
                    FailedModificationAttempts = 0
                };
                
                traditionalTrailingStops[baseId] = tracker;
                LogAndPrint($"TRADITIONAL_TRAILING: Initial stop submitted for {baseId}");
            }
            catch (Exception ex)
            {
                LogAndPrint($"ERROR in SubmitInitialTraditionalStop: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Update traditional trailing stop if needed
        /// </summary>
        private void UpdateTraditionalTrailingStop(TraditionalTrailingStop stop, Account account)
        {
            try
            {
                if (!stop.IsActive || stop.IsPendingModification) return;
                
                // Get current position
                var position = account.Positions.FirstOrDefault(p => 
                    p.Instrument.FullName == stop.TrackedPosition.Instrument.FullName && 
                    p.MarketPosition != MarketPosition.Flat);
                
                if (position == null)
                {
                    // Position closed - remove tracking
                    LogAndPrint($"TRADITIONAL_TRAILING: Position closed for {stop.BaseId}");
                    traditionalTrailingStops.Remove(stop.BaseId);
                    return;
                }
                
                // Check order state
                if (stop.CurrentStopOrder != null)
                {
                    if (stop.CurrentStopOrder.OrderState == OrderState.Filled)
                    {
                        LogAndPrint($"TRADITIONAL_TRAILING: Stop filled for {stop.BaseId}");
                        traditionalTrailingStops.Remove(stop.BaseId);
                        return;
                    }
                    
                    if (stop.CurrentStopOrder.OrderState == OrderState.Cancelled ||
                        stop.CurrentStopOrder.OrderState == OrderState.Rejected)
                    {
                        LogAndPrint($"TRADITIONAL_TRAILING: Stop order {stop.CurrentStopOrder.OrderState} for {stop.BaseId}");
                        stop.CurrentStopOrder = null;
                        stop.FailedModificationAttempts++;
                        
                        if (stop.FailedModificationAttempts < 3)
                        {
                            // Retry submission
                            double retryCurrentPrice = GetCurrentPrice(position.Instrument);
                            SubmitInitialTraditionalStop(stop.BaseId, position, account, retryCurrentPrice);
                        }
                        else
                        {
                            LogAndPrint($"TRADITIONAL_TRAILING: Max retries reached for {stop.BaseId}, removing");
                            traditionalTrailingStops.Remove(stop.BaseId);
                        }
                        return;
                    }
                }
                
                // Calculate new stop price
                double currentPrice = GetCurrentPrice(position.Instrument);
                if (currentPrice == 0) return;
                
                double newStopPrice = CalculateTraditionalStopPrice(position, currentPrice, false);
                
                // Update max profit
                double currentProfit = position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, currentPrice);
                if (currentProfit > stop.MaxProfit)
                {
                    stop.MaxProfit = currentProfit;
                }
                
                // Check if stop needs to be updated (must move favorably)
                bool shouldUpdate = false;
                if (position.MarketPosition == MarketPosition.Long)
                {
                    shouldUpdate = newStopPrice > stop.LastStopPrice && 
                                   Math.Abs(newStopPrice - stop.LastStopPrice) >= position.Instrument.MasterInstrument.TickSize;
                }
                else if (position.MarketPosition == MarketPosition.Short)
                {
                    shouldUpdate = newStopPrice < stop.LastStopPrice && 
                                   Math.Abs(newStopPrice - stop.LastStopPrice) >= position.Instrument.MasterInstrument.TickSize;
                }
                
                // Time throttling - don't update too frequently
                TimeSpan timeSinceLastUpdate = DateTime.Now - stop.LastModificationTime;
                if (shouldUpdate && timeSinceLastUpdate.TotalSeconds >= 1)
                {
                    LogAndPrint($"TRADITIONAL_TRAILING: Updating stop for {stop.BaseId} from {stop.LastStopPrice:F2} to {newStopPrice:F2}");
                    ModifyTraditionalStop(stop, newStopPrice, account);
                }
            }
            catch (Exception ex)
            {
                LogAndPrint($"ERROR in UpdateTraditionalTrailingStop: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Modify existing traditional stop order
        /// </summary>
        private void ModifyTraditionalStop(TraditionalTrailingStop stop, double newStopPrice, Account account)
        {
            try
            {
                if (stop.CurrentStopOrder == null) return;
                
                stop.IsPendingModification = true;
                
                // Cancel existing order
                account.Cancel(new[] { stop.CurrentStopOrder });
                
                // Wait a brief moment for cancellation
                System.Threading.Thread.Sleep(100);
                
                // Submit new order
                OrderAction orderAction = stop.TrackedPosition.MarketPosition == MarketPosition.Long ? 
                    OrderAction.Sell : OrderAction.Buy;
                
                string orderName = $"MSM_TRAIL_STOP_{stop.BaseId}";
                
                Order newStopOrder = account.CreateOrder(
                    stop.TrackedPosition.Instrument,
                    orderAction,
                    OrderType.StopMarket,
                    OrderEntry.Manual,
                    TimeInForce.Day,
                    stop.TrackedPosition.Quantity,
                    0,
                    newStopPrice,
                    string.Empty,
                    orderName,
                    default(DateTime),
                    null
                );
                
                account.Submit(new[] { newStopOrder });
                
                // Update tracking
                stop.CurrentStopOrder = newStopOrder;
                stop.LastStopPrice = newStopPrice;
                stop.LastModificationTime = DateTime.Now;
                stop.ModificationCount++;
                stop.IsPendingModification = false;
                stop.LastKnownOrderState = OrderState.Submitted;
                
                LogAndPrint($"TRADITIONAL_TRAILING: Stop modified for {stop.BaseId} - Count: {stop.ModificationCount}");
                
                // Send update to bridge
                Task.Run(() => SendTrailingStopUpdate(stop.BaseId, newStopPrice, GetCurrentPrice(stop.TrackedPosition.Instrument)));
            }
            catch (Exception ex)
            {
                LogAndPrint($"ERROR in ModifyTraditionalStop: {ex.Message}");
                stop.IsPendingModification = false;
                stop.FailedModificationAttempts++;
            }
        }
        
        /// <summary>
        /// Calculate traditional stop price using profit-locking logic
        /// </summary>
        private double CalculateTraditionalStopPrice(Position position, double currentPrice, bool isInitial)
        {
            try
            {
                double unrealizedPnL = position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, currentPrice);
                double entryPrice = position.AveragePrice;
                double pointValue = position.Instrument.MasterInstrument.PointValue * position.Quantity;
                
                // Calculate locked profit based on current profit level
                double lockedProfit = 0;
                
                switch (TrailingStopType)
                {
                    case TrailingActivationType.Dollars:
                        // Initial locked profit
                        double initialLockedProfit = TrailingStopValue;
                        double trailingIncrement = TrailingIncrementsValue;
                        double triggerLevel = TrailingTriggerValue;
                        
                        // Calculate increments earned
                        double extraProfit = Math.Max(0, unrealizedPnL - triggerLevel);
                        double incrementsEarned = Math.Floor(extraProfit / trailingIncrement);
                        lockedProfit = initialLockedProfit + (incrementsEarned * trailingIncrement);
                        break;
                        
                    case TrailingActivationType.Percent:
                        // Similar logic but using percentages
                        double percentProfit = (unrealizedPnL / (entryPrice * position.Quantity * pointValue)) * 100;
                        double percentTrigger = TrailingTriggerValue;
                        double percentInitial = TrailingStopValue;
                        double percentIncrement = TrailingIncrementsValue;
                        
                        double percentExtra = Math.Max(0, percentProfit - percentTrigger);
                        double percentIncrements = Math.Floor(percentExtra / percentIncrement);
                        double totalPercent = percentInitial + (percentIncrements * percentIncrement);
                        
                        lockedProfit = (totalPercent / 100) * (entryPrice * position.Quantity * pointValue);
                        break;
                        
                    // Add other types as needed
                }
                
                // Convert locked profit to price points
                double lockedProfitInPoints = lockedProfit / pointValue;
                
                // Calculate stop price
                double stopPrice;
                if (position.MarketPosition == MarketPosition.Long)
                {
                    stopPrice = entryPrice + lockedProfitInPoints;
                }
                else
                {
                    stopPrice = entryPrice - lockedProfitInPoints;
                }
                
                // Ensure stop is at least at breakeven on initial placement
                if (isInitial)
                {
                    if (position.MarketPosition == MarketPosition.Long)
                    {
                        stopPrice = Math.Max(stopPrice, entryPrice);
                    }
                    else
                    {
                        stopPrice = Math.Min(stopPrice, entryPrice);
                    }
                }
                
                return stopPrice;
            }
            catch (Exception ex)
            {
                LogAndPrint($"ERROR in CalculateTraditionalStopPrice: {ex.Message}");
                return 0;
            }
        }
        
        /// <summary>
        /// Clean up traditional stops for closed positions
        /// </summary>
        private void CleanupClosedPositionStops(Account account)
        {
            try
            {
                var keysToRemove = new List<string>();
                
                foreach (var kvp in traditionalTrailingStops)
                {
                    var position = account.Positions.FirstOrDefault(p => 
                        p.Instrument.FullName == kvp.Value.TrackedPosition.Instrument.FullName && 
                        p.MarketPosition != MarketPosition.Flat);
                    
                    if (position == null)
                    {
                        // Cancel any remaining stop order
                        if (kvp.Value.CurrentStopOrder != null && 
                            (kvp.Value.CurrentStopOrder.OrderState == OrderState.Working ||
                             kvp.Value.CurrentStopOrder.OrderState == OrderState.Accepted))
                        {
                            try
                            {
                                account.Cancel(new[] { kvp.Value.CurrentStopOrder });
                                LogAndPrint($"TRADITIONAL_TRAILING: Cancelled orphaned stop for {kvp.Key}");
                            }
                            catch (Exception ex)
                            {
                                LogAndPrint($"ERROR cancelling orphaned stop: {ex.Message}");
                            }
                        }
                        
                        keysToRemove.Add(kvp.Key);
                    }
                }
                
                foreach (var key in keysToRemove)
                {
                    traditionalTrailingStops.Remove(key);
                    LogAndPrint($"TRADITIONAL_TRAILING: Cleaned up tracking for {key}");
                }
            }
            catch (Exception ex)
            {
                LogAndPrint($"ERROR in CleanupClosedPositionStops: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Get base ID from position (helper method - you may need to adjust based on your logic)
        /// </summary>
        private string GetBaseIdFromPosition(Position position)
        {
            // This is a placeholder - you need to implement proper baseId retrieval
            // based on how baseIds are associated with positions in your system
            
            // Check if we have an elastic position with this instrument
            var elasticPosition = elasticPositions.Values.FirstOrDefault(ep => 
                ep.InstrumentFullName == position.Instrument.FullName &&
                ep.MarketPosition == position.MarketPosition);
                
            if (elasticPosition != null)
            {
                return elasticPosition.BaseId;
            }
            
            // Check internal stops
            var internalStop = internalStops.Values.FirstOrDefault(ist => 
                ist.TrackedPosition?.Instrument.FullName == position.Instrument.FullName &&
                ist.TrackedPosition?.MarketPosition == position.MarketPosition);
                
            if (internalStop != null)
            {
                return internalStop.BaseId;
            }
            
            return null;
        }
        
        /// <summary>
        /// Get pip size for forex instruments
        /// </summary>
        private double GetPipSize(Instrument instrument)
        {
            try
            {
                // For most forex pairs, a pip is 0.0001 (4th decimal place)
                // For JPY pairs, a pip is 0.01 (2nd decimal place)
                
                string instrumentName = instrument.FullName.ToUpper();
                
                // Check if this is a JPY pair
                if (instrumentName.Contains("JPY"))
                {
                    return 0.01; // JPY pairs use 2 decimal places for pips
                }
                
                // Default pip size for most forex pairs
                return 0.0001; // 4 decimal places
            }
            catch (Exception ex)
            {
                LogAndPrint($"ERROR in GetPipSize: {ex.Message}");
                return 0.0001; // Safe default
            }
        }
        
        #endregion
    }
}