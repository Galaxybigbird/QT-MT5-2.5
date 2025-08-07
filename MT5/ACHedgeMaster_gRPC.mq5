#property link      ""
#property version   "3.10"
#property strict
#property description "gRPC Hedge Receiver EA for Go bridge server with Asymmetrical Compounding"

//+------------------------------------------------------------------+
//| gRPC Connection Settings                                         |
//+------------------------------------------------------------------+
input group "===== gRPC Connection Settings =====";
input string BridgeServerAddress = "127.0.0.1";  // gRPC Server Address
input int    BridgeServerPort = 50051;            // gRPC Server Port

//+------------------------------------------------------------------+
//| Trading Settings                                                |
//+------------------------------------------------------------------+
input group "===== Trading Settings =====";
enum LOT_MODE { Asymmetric_Compounding = 0, Fixed_Lot_Size = 1, Elastic_Hedging = 2 };
input LOT_MODE LotSizingMode = Asymmetric_Compounding;    // Lot Sizing Method

input bool   EnableHedging = true;   // Enable hedging? (false = copy direction)
input double DefaultLot = 1.0;       // Default lot size if not specified
input int    Slippage = 200;         // Slippage
input int    MagicNumber = 12345;    // MagicNumber for trades

input group "===== Elastic Hedging Settings =====";
input bool   ElasticHedging_Enabled = true;              // Enable Elastic Hedging
input double ElasticHedging_NTPointsToMT5 = 100.0;       // NT to MT5 point conversion ratio

input group "--- Tier 1: First Loss Recovery ---";
input double ElasticHedging_Tier1_TargetProfit = 70.0;   // Target profit for first $1K NT loss ($)
input double ElasticHedging_Tier1_LotReduction = 0.05;   // Lots to close per profit update
input double ElasticHedging_Tier1_MaxReduction = 0.80;   // Max % of position to close (80%)

input group "--- Tier 2: Second Loss Recovery ---";
input double ElasticHedging_Tier2_TargetProfit = 200.0;  // Target profit for second $1K NT loss ($)
input double ElasticHedging_Tier2_LotReduction = 0.02;   // More aggressive lot reduction per update
input double ElasticHedging_Tier2_MaxReduction = 0.90;   // More aggressive max reduction (90%)
input double ElasticHedging_Tier2_Threshold = -1000.0;   // NT PnL threshold to trigger Tier 2

input group "--- General Settings ---";
input int ElasticHedging_MinUpdateInterval = 1;          // Min seconds between reductions

// Include the asymmetrical compounding functionality
#include <gRPC/ACFunctions_gRPC.mqh>
#include <gRPC/ATRtrailing_gRPC.mqh>

input group "=====On-Chart Element Positions=====";
input int TrailingButtonXPos_EA = 120; // X distance for trailing button position
input int TrailingButtonYPos_EA = 20;  // Y distance for trailing button position
input int StatusLabelXPos_EA    = 200; // X distance for status label position
input int StatusLabelYPos_EA    = 50;  // Y distance for status label position

#include <gRPC/StatusIndicator_gRPC.mqh>
#include <gRPC/StatusOverlay_gRPC.mqh>
#include <Trade/Trade.mqh>
#include <Generic/HashMap.mqh>
#include <Strings/String.mqh>
#include <Trade/DealInfo.mqh>
#include <Trade/PositionInfo.mqh>

CTrade trade;

// Error code constant for hedging-related errors
#define ERR_TRADE_NOT_ALLOWED           4756  // Trading is prohibited

// Function declarations for functions not in include files
void InitATRTrailing();
void CleanupATRTrailing();
void HandleATRTrailingForPosition(ulong ticket, double entryPrice, double currentPrice, string orderType, double volume);
double CalculateACLotSize(double ntQuantity);
double CalculateElasticLotSize(double ntQuantity);
void AddElasticPosition(string baseId, ulong positionTicket, double lots);

// AC Risk Management variables already declared in ACFunctions_gRPC.mqh

bool      UseACRiskManagement = false; // Effective AC Risk Management state, derived from LotSizingMode
const string    CommentPrefix = "NT_Hedge_";  // Prefix for hedge order comments
const string    EA_COMMENT_PREFIX_BUY = CommentPrefix + "BUY_"; // Specific prefix for EA BUY hedges
const string    EA_COMMENT_PREFIX_SELL = CommentPrefix + "SELL_"; // Specific prefix for EA SELL hedges

//+------------------------------------------------------------------+
//| Pure C++ gRPC Client DLL Import                                 |
//+------------------------------------------------------------------+
#import "MT5GrpcClient.dll"
   int TestFunction();
   int GrpcInitialize(string server_address, int port);
   int GrpcShutdown();
   int GrpcIsConnected();
   int GrpcReconnect();
   
   int GrpcStartTradeStream();
   int GrpcStopTradeStream();
   int GrpcGetNextTrade(string &trade_json, int buffer_size);
   int GrpcGetTradeQueueSize();
   
   int GrpcSubmitTradeResult(string result_json);
   int GrpcHealthCheck(string request_json, string &response_json, int buffer_size);
   int GrpcNotifyHedgeClose(string notification_json);
   int GrpcSubmitElasticUpdate(string update_json);
   int GrpcSubmitTrailingUpdate(string update_json);
   
   int GrpcGetConnectionStatus(string &status_json, int buffer_size);
   int GrpcGetStreamingStats(string &stats_json, int buffer_size);
   int GrpcGetLastError(string &error_message, int buffer_size);
   
   // Configuration functions
   int GrpcSetConnectionTimeout(int timeout_ms);
   int GrpcSetStreamingTimeout(int timeout_ms);
   int GrpcSetMaxRetries(int max_retries);
#import

//+------------------------------------------------------------------+
//| Risk Management - Asymmetrical Compounding                       |
//+------------------------------------------------------------------+
// Global variable to track the aggregated net futures position from NT trades.
double globalFutures = 0.0;
string lastTradeTime = "";  // Track the last processed trade time
string lastTradeId = "";  // Track the last processed trade ID

// Add new struct for TP/SL measurements
struct TPSLMeasurement {
    string baseTradeId;
    string orderType;  // "TP" or "SL"
    int pips;
    double rawMeasurement;
};

// Add global variables for measurements
TPSLMeasurement lastTPSL;

// Dynamic‑hedge state
double g_highWaterEOD = 0.0;  // highest *settled* balance
const  double CUSHION_BAND = 90.0;    // Trailing drawdown cushion (30% of $300 account)
double g_lastOHF      = 0.05; // last over‑hedge factor
double g_lastCushion  = 0.0;  // last calculated cushion for debugging

// Progressive hedging state for combine scenarios
double g_ntCumulativeLoss = 0.0;  // Track cumulative NT losses for progressive scaling
int g_ntLossStreak = 0;           // Count consecutive losing days
double g_lastNTBalance = 0.0;     // Track NT balance changes
double g_ntDailyPnL = 0.0;        // Current day's NT P&L
string g_lastNTTradeResult = "";  // Last trade result: "win" or "loss"
int g_ntSessionTrades = 0;        // Number of trades in current session
datetime g_lastNTUpdateTime = 0;  // Last time NT data was updated
bool g_ntDataAvailable = false;   // Flag to indicate if NT data is available

// WHACK-A-MOLE FIX: State change tracking for overlay calculations
static datetime g_lastNTDataUpdate = 0;
static double g_lastNTBalanceForCalc = 0.0;
static double g_lastNTDailyPnLForCalc = 0.0;
static string g_lastNTResultForCalc = "";
static int g_lastNTSessionTradesForCalc = 0;

// Broker specification cache
struct BrokerSpecs {
    double tickSize;        // Minimum price change
    double tickValue;       // Dollar value per tick
    double pointValue;      // Dollar value per point
    double contractSize;    // Contract size
    double minLot;          // Minimum lot size
    double maxLot;          // Maximum lot size
    double lotStep;         // Lot step increment
    double marginRequired;  // Margin per lot
    bool   isValid;         // Whether specs have been loaded
} g_brokerSpecs;

// Race condition fix: Flag to indicate if broker specs are loaded and valid.
bool g_broker_specs_ready = false;


// gRPC connection state
bool grpc_connected = false;
bool grpc_streaming = false;
datetime grpc_last_connection_attempt = 0;
int grpc_connection_retry_interval = 5; // seconds
int grpc_max_retries = 3;

// Instead of struct array, use separate arrays for each field
string g_baseIds[];           // Array of base trade IDs
int g_totalQuantities[];      // Array of total quantities
int g_processedQuantities[];  // Array of processed quantities
string g_actions[];           // Array of trade actions
bool g_isComplete[];          // Array of completion flags
string g_ntInstrumentSymbols[]; // Array of NT instrument symbols
string g_ntAccountNames[];    // Array of NT account names
int g_mt5HedgesOpenedCount[]; // Count of MT5 hedges opened for this group
int g_mt5HedgesClosedCount[]; // Count of MT5 hedges closed for this group
bool g_isMT5Opened[];         // Flag if MT5 hedge has been opened for this group
bool g_isMT5Closed[];         // Flag if all MT5 hedges for this group are closed

CHashMap<long, CString*> *g_map_position_id_to_base_id = NULL; // Map PositionID (long) to original base_id (CString*)

// New parallel arrays for MT5 position details
long g_open_mt5_pos_ids[];       // Stores MT5 Position IDs
string g_open_mt5_base_ids[];    // Stores corresponding NT Base IDs
string g_open_mt5_nt_symbols[];  // Stores corresponding NT Instrument Symbols
string g_open_mt5_nt_accounts[]; // Stores corresponding NT Account Names
string g_open_mt5_actions[];     // Stores the MT5 position type ("buy" or "sell") for open positions
string g_open_mt5_original_nt_actions[];    // Stores original NT action for rehydrated open MT5 positions
double g_open_mt5_original_nt_quantities[]; // Stores original NT quantity for rehydrated open MT5 positions

// DUPLICATE NOTIFICATION PREVENTION: Track positions closed by NT to prevent duplicate notifications
long g_nt_closed_position_ids[];  // Stores position IDs that were closed by NT (to prevent duplicate notifications)
datetime g_nt_closed_timestamps[]; // Stores timestamps when positions were closed by NT (for cleanup)

// TRAILING STOP IGNORE: Track base IDs that have been closed to ignore subsequent trailing stop updates
string g_closed_base_ids[];       // Stores base IDs that have been closed (to ignore trailing stop updates)
datetime g_closed_base_timestamps[]; // Stores timestamps when base IDs were closed (for cleanup)

// ELASTIC HEDGING: Track elastic positions and their reduction history
struct ElasticHedgePosition
{
    string baseId;
    ulong positionTicket;
    double initialLots;
    double remainingLots;
    int profitLevelsReceived;
    double totalLotsReduced;
    datetime lastReductionTime;
};

ElasticHedgePosition g_elasticPositions[];  // Array of elastic hedge positions

// COMPREHENSIVE DUPLICATE PREVENTION: Track all notifications sent per base_id to prevent multiple notifications
string g_notified_base_ids[];     // Stores base_ids that have already been notified
datetime g_notified_timestamps[]; // Stores timestamps when notifications were sent (for cleanup)

// Mutex-like mechanism to prevent concurrent array modifications
bool g_array_modification_in_progress = false;
datetime g_last_array_modification_time = 0;
const int ARRAY_MODIFICATION_TIMEOUT_SECONDS = 30; // Maximum time to wait for array modification to complete

//──────────────────────────────────────────────────────────────────────────────
// Query and cache broker specifications for current symbol
//──────────────────────────────────────────────────────────────────────────────
bool QueryBrokerSpecs()
{
    g_brokerSpecs.tickSize = SymbolInfoDouble(_Symbol, SYMBOL_TRADE_TICK_SIZE);
    g_brokerSpecs.tickValue = SymbolInfoDouble(_Symbol, SYMBOL_TRADE_TICK_VALUE);
    g_brokerSpecs.contractSize = SymbolInfoDouble(_Symbol, SYMBOL_TRADE_CONTRACT_SIZE);
    g_brokerSpecs.minLot = SymbolInfoDouble(_Symbol, SYMBOL_VOLUME_MIN);
    g_brokerSpecs.maxLot = SymbolInfoDouble(_Symbol, SYMBOL_VOLUME_MAX);
    g_brokerSpecs.lotStep = SymbolInfoDouble(_Symbol, SYMBOL_VOLUME_STEP);
    g_brokerSpecs.marginRequired = SymbolInfoDouble(_Symbol, SYMBOL_MARGIN_INITIAL);

    // Calculate point value (dollar value per point movement)
    double point = SymbolInfoDouble(_Symbol, SYMBOL_POINT);
    if(point > 0 && g_brokerSpecs.tickSize > 0)
    {
        // Point value = (tick value / tick size) * point size
        g_brokerSpecs.pointValue = (g_brokerSpecs.tickValue / g_brokerSpecs.tickSize) * point;
    }
    else
    {
        g_brokerSpecs.pointValue = 0.0;
    }

    // CRITICAL FIX: Handle zero margin requirement with realistic fallback
    if(g_brokerSpecs.marginRequired <= 0)
    {
        // Calculate realistic margin based on current price and leverage
        double currentPrice = SymbolInfoDouble(_Symbol, SYMBOL_BID);
        long leverageLong = AccountInfoInteger(ACCOUNT_LEVERAGE);
        double leverage = (double)leverageLong; // Explicit cast to avoid warning

        if(currentPrice > 0 && leverage > 0)
        {
            // For NAS100: Contract size * Current price / Leverage
            g_brokerSpecs.marginRequired = (g_brokerSpecs.contractSize * currentPrice) / leverage;
            Print("BROKER_SPECS_FIX: Calculated margin requirement: $", g_brokerSpecs.marginRequired,
                  " per lot (Price: ", currentPrice, ", Leverage: 1:", leverageLong, ")");
        }
        else
        {
            // Ultimate fallback for $300 account safety
            g_brokerSpecs.marginRequired = 50.0; // Conservative $50 per lot
            Print("BROKER_SPECS_FALLBACK: Using conservative margin requirement: $", g_brokerSpecs.marginRequired, " per lot");
        }
    }

    // Validate that we got reasonable values
    g_brokerSpecs.isValid = (g_brokerSpecs.tickSize > 0 &&
                            g_brokerSpecs.tickValue > 0 &&
                            g_brokerSpecs.contractSize > 0 &&
                            g_brokerSpecs.minLot > 0 &&
                            g_brokerSpecs.maxLot > 0 &&
                            g_brokerSpecs.lotStep > 0 &&
                            g_brokerSpecs.marginRequired > 0); // Added margin validation

    if(g_brokerSpecs.isValid)
    {
        g_broker_specs_ready = true; // Specs are valid
        Print("BROKER_SPECS: Successfully queried specifications for ", _Symbol);
        Print("  Tick Size: ", g_brokerSpecs.tickSize);
        Print("  Tick Value: $", g_brokerSpecs.tickValue);
        Print("  Point Value: $", g_brokerSpecs.pointValue, " per point per lot");
        Print("  Contract Size: ", g_brokerSpecs.contractSize);
        Print("  Min Lot: ", g_brokerSpecs.minLot);
        Print("  Max Lot: ", g_brokerSpecs.maxLot);
        Print("  Lot Step: ", g_brokerSpecs.lotStep);
        Print("  Margin Required: $", g_brokerSpecs.marginRequired, " per lot");

        // Additional safety check for $300 account
        double accountBalance = AccountInfoDouble(ACCOUNT_BALANCE);
        double maxSafeLots = (accountBalance * 0.50) / g_brokerSpecs.marginRequired; // 50% max usage
        Print("  SAFETY: For $", accountBalance, " account, max safe lots: ", maxSafeLots, " (50% margin usage)");
    }
    else
    {
        g_broker_specs_ready = false; // Specs are invalid
        Print("BROKER_SPECS_ERROR: Failed to query valid specifications for ", _Symbol);
        Print("  Tick Size: ", g_brokerSpecs.tickSize);
        Print("  Tick Value: ", g_brokerSpecs.tickValue);
        Print("  Contract Size: ", g_brokerSpecs.contractSize);
        Print("  Min/Max/Step Lot: ", g_brokerSpecs.minLot, "/", g_brokerSpecs.maxLot, "/", g_brokerSpecs.lotStep);
        Print("  Margin Required: ", g_brokerSpecs.marginRequired);
    }

    return g_brokerSpecs.isValid;
}

//──────────────────────────────────────────────────────────────────────────────
// Helper functions for parsing NT performance data from JSON
//──────────────────────────────────────────────────────────────────────────────
bool ParseJSONDouble(string json_str, string key, double &value)
{
    value = GetJSONDouble(json_str, key);
    return (value != 0.0 || StringFind(json_str, "\"" + key + "\":0") >= 0);
}

bool ParseJSONString(string json_str, string key, string &value)
{
    value = GetJSONStringValue(json_str, "\"" + key + "\"");
    return (value != "");
}

bool ParseJSONInt(string json_str, string key, int &value)
{
    value = GetJSONIntValue(json_str, key, -999999); // Use unlikely default
    return (value != -999999);
}

//──────────────────────────────────────────────────────────────────────────────
// Parse NT performance data from enhanced JSON messages
//──────────────────────────────────────────────────────────────────────────────
bool ParseNTPerformanceData(string json_str, double &nt_balance, double &nt_daily_pnl,
                           string &nt_trade_result, int &nt_session_trades)
{
    // Parse nt_balance
    if(!ParseJSONDouble(json_str, "nt_balance", nt_balance)) {
        Print("NT_PARSE_WARNING: nt_balance not found in JSON, using default");
        nt_balance = 0.0;
    }

    // Parse nt_daily_pnl
    if(!ParseJSONDouble(json_str, "nt_daily_pnl", nt_daily_pnl)) {
        Print("NT_PARSE_WARNING: nt_daily_pnl not found in JSON, using default");
        nt_daily_pnl = 0.0;
    }

    // Parse nt_trade_result
    if(!ParseJSONString(json_str, "nt_trade_result", nt_trade_result)) {
        Print("NT_PARSE_WARNING: nt_trade_result not found in JSON, using default");
        nt_trade_result = "unknown";
    }

    // Parse nt_session_trades
    if(!ParseJSONInt(json_str, "nt_session_trades", nt_session_trades)) {
        Print("NT_PARSE_WARNING: nt_session_trades not found in JSON, using default");
        nt_session_trades = 0;
    }

    return true;
}

//──────────────────────────────────────────────────────────────────────────────
// Update NT performance tracking variables
//──────────────────────────────────────────────────────────────────────────────
void UpdateNTPerformanceTracking(double nt_balance, double nt_daily_pnl,
                                string nt_trade_result, int nt_session_trades)
{
    // WHACK-A-MOLE FIX: Check if NT data has actually changed
    bool nt_data_changed = false;

    if(MathAbs(nt_balance - g_lastNTBalanceForCalc) > 0.01 ||
       MathAbs(nt_daily_pnl - g_lastNTDailyPnLForCalc) > 0.01 ||
       nt_trade_result != g_lastNTResultForCalc ||
       nt_session_trades != g_lastNTSessionTradesForCalc ||
       !g_ntDataAvailable) // First time data becomes available
    {
        nt_data_changed = true;
        g_lastNTBalanceForCalc = nt_balance;
        g_lastNTDailyPnLForCalc = nt_daily_pnl;
        g_lastNTResultForCalc = nt_trade_result;
        g_lastNTSessionTradesForCalc = nt_session_trades;
        g_lastNTDataUpdate = TimeCurrent();
    }

    // Update global tracking variables
    double previous_balance = g_lastNTBalance;
    double previous_pnl = g_ntDailyPnL;
    g_lastNTBalance = nt_balance;
    g_ntDailyPnL = nt_daily_pnl;
    g_lastNTTradeResult = nt_trade_result;
    g_ntSessionTrades = nt_session_trades;
    g_lastNTUpdateTime = TimeCurrent();
    g_ntDataAvailable = true;
    
    // Log tier transition
    bool wasTier2 = (previous_pnl <= ElasticHedging_Tier2_Threshold);
    bool isTier2 = (g_ntDailyPnL <= ElasticHedging_Tier2_Threshold);
    if (wasTier2 != isTier2) {
        if (isTier2) {
            Print("ELASTIC_HEDGE: *** TIER TRANSITION *** Entering Tier 2 (High Risk) - NT PnL: $", g_ntDailyPnL);
        } else {
            Print("ELASTIC_HEDGE: *** TIER TRANSITION *** Returning to Tier 1 (Standard) - NT PnL: $", g_ntDailyPnL);
        }
    }

    // Update loss streak tracking
    if(nt_trade_result == "loss") {
        g_ntLossStreak++;
        if(nt_daily_pnl < 0) {
            g_ntCumulativeLoss += MathAbs(nt_daily_pnl);
        }
    } else if(nt_trade_result == "win") {
        g_ntLossStreak = 0; // Reset loss streak on win
    }

    // Only print and force recalculation if data actually changed
    if(nt_data_changed) {
        Print("NT_PERFORMANCE_UPDATE: Balance: $", nt_balance,
              ", Daily P&L: $", nt_daily_pnl,
              ", Trade Result: ", nt_trade_result,
              ", Session Trades: ", nt_session_trades,
              ", Loss Streak: ", g_ntLossStreak,
              ", Cumulative Loss: $", g_ntCumulativeLoss);

        // WHACK-A-MOLE FIX: Update overlay directly when NT data actually changes
        UpdateStatusOverlay();
    }
}

//──────────────────────────────────────────────────────────────────────────────
// Calculate progressive hedging target based on NT performance scenarios
//──────────────────────────────────────────────────────────────────────────────
double CalculateProgressiveHedgingTarget()
{
    // Default conservative target if no NT data available
    if(!g_ntDataAvailable) {
        Print("PROGRESSIVE_HEDGING: No NT data available, using default $60 target");
        return 60.0;
    }

    double targetProfit = 60.0;  // Base target for first loss

    // Progressive hedging logic based on NT performance:
    if(g_ntLossStreak == 0) {
        // No current loss streak - use minimal hedging
        targetProfit = 30.0;
        Print("PROGRESSIVE_HEDGING: No loss streak - Minimal hedging target: $", targetProfit);
    }
    else if(g_ntLossStreak == 1) {
        // First loss - Day 1 scenario: Target $50-70 to break even
        targetProfit = 60.0;
        Print("PROGRESSIVE_HEDGING: First loss (Day 1) - Standard target: $", targetProfit);
    }
    else if(g_ntLossStreak >= 2) {
        // Multiple losses - Day 2+ scenario: Scale up to cover multiple combines
        if(g_lastNTTradeResult == "loss") {
            // Day 2+ Loss: Target $200+ to cover both combines
            targetProfit = 200.0 + (g_ntLossStreak - 2) * 50.0; // Scale up for additional losses
            Print("PROGRESSIVE_HEDGING: Multiple losses (Day ", g_ntLossStreak, ") - Scaled target: $", targetProfit);
        } else {
            // Day 2+ Win after losses: Reduce target to minimize MT5 loss
            targetProfit = 80.0; // Reduced target when NT wins after losses
            Print("PROGRESSIVE_HEDGING: Win after losses - Reduced target: $", targetProfit);
        }
    }

    // Additional scaling based on cumulative losses
    if(g_ntCumulativeLoss > 500.0) {
        targetProfit *= 1.5; // Increase target by 50% for significant cumulative losses
        Print("PROGRESSIVE_HEDGING: High cumulative loss ($", g_ntCumulativeLoss, ") - Adjusted target: $", targetProfit);
    }

    Print("PROGRESSIVE_HEDGING: Final target: $", targetProfit,
          " (Loss Streak: ", g_ntLossStreak,
          ", Last Result: ", g_lastNTTradeResult,
          ", Daily P&L: $", g_ntDailyPnL, ")");

    return targetProfit;
}

//──────────────────────────────────────────────────────────────────────────────
// Calculate lot size needed to achieve target profit in USD
//──────────────────────────────────────────────────────────────────────────────
double CalculateLotForTargetProfit(double targetProfitUSD, double expectedPointMove)
{
    if(!g_brokerSpecs.isValid)
    {
        Print("ELASTIC_ERROR: Broker specs not loaded. Cannot calculate lot for target profit.");
        return g_brokerSpecs.minLot;
    }

    if(g_brokerSpecs.pointValue <= 0 || expectedPointMove <= 0)
    {
        Print("ELASTIC_ERROR: Invalid point value ($", g_brokerSpecs.pointValue,
              ") or expected move (", expectedPointMove, " points)");
        return g_brokerSpecs.minLot;
    }

    // Required lot = Target Profit / (Point Value * Expected Point Move)
    double requiredLot = targetProfitUSD / (g_brokerSpecs.pointValue * expectedPointMove);

    // Apply broker constraints
    requiredLot = MathMax(requiredLot, g_brokerSpecs.minLot);
    requiredLot = MathMin(requiredLot, g_brokerSpecs.maxLot);
    requiredLot = MathFloor(requiredLot / g_brokerSpecs.lotStep) * g_brokerSpecs.lotStep;

    Print("ELASTIC_CALC: Target profit $", targetProfitUSD,
          ", Expected move ", expectedPointMove, " points",
          ", Point value $", g_brokerSpecs.pointValue, "/point/lot",
          " -> Required lot: ", requiredLot);

    return requiredLot;
}

// Function to find or create trade group
int FindOrCreateTradeGroup(string baseId, int totalQty, string action)
{
    // First try to find an existing group with this base ID
    // Handle both full match (legacy) and partial match (new format due to MT5 comment length limit)
    int arraySize = ArraySize(g_baseIds);
    for(int i = 0; i < arraySize; i++)
    {
        bool isMatch = false;
        if(g_baseIds[i] == baseId && !g_isComplete[i]) {
            // Full match (legacy format)
            isMatch = true;
        } else if(StringLen(g_baseIds[i]) >= 16 && StringLen(baseId) >= 16 && !g_isComplete[i]) {
            // Partial match - compare first 16 characters (new format)
            string shortStoredBaseId = StringSubstr(g_baseIds[i], 0, 16);
            string shortBaseId = StringSubstr(baseId, 0, 16);
            if(shortStoredBaseId == shortBaseId) {
                isMatch = true;
                Print("DEBUG: FindOrCreateTradeGroup - Matched using partial base_id. Stored: '", shortStoredBaseId, "' (from full: '", g_baseIds[i], "'), Input: '", shortBaseId, "' (from full: '", baseId, "')");
            }
        }

        if(isMatch) {
            // Found existing group - don't update global futures position again
            Print("DEBUG: Found existing trade group at index ", i, " for base ID: ", baseId);
            return i;
        }
    }
    
    // Create new group if not found
    int newIndex = arraySize;
    ArrayResize(g_baseIds, newIndex + 1);
    ArrayResize(g_totalQuantities, newIndex + 1);
    ArrayResize(g_processedQuantities, newIndex + 1);
    ArrayResize(g_actions, newIndex + 1);
    ArrayResize(g_isComplete, newIndex + 1);
    
    g_baseIds[newIndex] = baseId;
    g_totalQuantities[newIndex] = totalQty;  // Use the total quantity from the message
    g_processedQuantities[newIndex] = 0;
    g_actions[newIndex] = action;
    g_isComplete[newIndex] = false;
    
    // Update global futures position based on total quantity
    if(action == "Buy" || action == "BuyToCover")
        globalFutures += 1;  // Add one contract at a time
    else if(action == "Sell" || action == "SellShort")
        globalFutures -= 1;  // Subtract one contract at a time
        
    Print("DEBUG: New trade group created. Base ID: ", baseId, 
          ", Total Qty: ", totalQty,
          ", Action: ", action,
          ", Updated Global Futures: ", globalFutures);
    
    return newIndex;
}

//+------------------------------------------------------------------+
//| gRPC Connection Management                                       |
//+------------------------------------------------------------------+
bool InitializeGrpcConnection()
{
    Print("Initializing gRPC connection to ", BridgeServerAddress, ":", BridgeServerPort);
    Print("DEBUG: Starting DLL connection test...");
    Print("DEBUG: Testing DLL export...");
    
    // Test if DLL exports are working at all
    int testResult = TestFunction();
    Print("DEBUG: TestFunction returned: ", testResult, " (should be 42)");
    
    if(testResult != 42) {
        Print("ERROR: DLL exports not working correctly!");
        return false;
    }
    
    Print("DEBUG: DLL exports working! Setting up timeout configuration...");
    
    // Configure timeouts to prevent hanging - CRITICAL FOR STABILITY
    int timeout_result = GrpcSetConnectionTimeout(5000);  // 5 second connection timeout
    if(timeout_result != 0) {
        Print("WARNING: Failed to set connection timeout, using defaults");
    }
    
    timeout_result = GrpcSetStreamingTimeout(10000);  // 10 second streaming timeout
    if(timeout_result != 0) {
        Print("WARNING: Failed to set streaming timeout, using defaults");
    }
    
    timeout_result = GrpcSetMaxRetries(2);  // Limit retries to prevent infinite loops
    if(timeout_result != 0) {
        Print("WARNING: Failed to set max retries, using defaults");
    }
    
    Print("DEBUG: Timeouts configured, starting gRPC initialization...");
    
    // Initialize the gRPC client with timeout protection
    Print("DEBUG: About to call GrpcInitialize with: ", BridgeServerAddress, ":", BridgeServerPort);
    int result = GrpcInitialize(BridgeServerAddress, BridgeServerPort);
    Print("DEBUG: GrpcInitialize returned: ", result);
    
    if(result != 0) {
        string error_msg;
        GrpcGetLastError(error_msg, 1024);
        Print("gRPC initialization failed. Error: ", result, " - ", error_msg);
        Print("NOTE: This is normal if bridge server is not running yet");
        return false;
    }
    
    // Verify connection with health check (with timeout protection)
    string health_request = "{\"source\":\"MT5_EA\",\"open_positions\":0}";
    string health_response;
    StringReserve(health_response, 2048); // Pre-allocate buffer for C++ DLL
    
    result = GrpcHealthCheck(health_request, health_response, 2048);
    
    if(result != 0) {
        string error_msg;
        GrpcGetLastError(error_msg, 1024);
        Print("gRPC health check failed. Error: ", result, " - ", error_msg);
        Print("NOTE: Bridge server may not be ready yet, will retry later");
        return false;
    }
    
    Print("gRPC health check successful. Response: ", health_response);
    grpc_last_connection_attempt = TimeCurrent();
    
    return true;
}

bool StartGrpcTradeStreaming()
{
    Print("Starting gRPC trade streaming with timeout protection...");
    
    // Check if we're still connected before attempting to start streaming
    if(!grpc_connected) {
        Print("Cannot start streaming: gRPC not connected");
        return false;
    }
    
    int result = GrpcStartTradeStream();
    
    if(result != 0) {
        string error_msg;
        GrpcGetLastError(error_msg, 1024);
        Print("Failed to start gRPC trade streaming. Error: ", result, " - ", error_msg);
        Print("Streaming will be retried automatically");
        return false;
    }
    
    grpc_streaming = true;
    Print("gRPC trade streaming started successfully");
    
    return true;
}

bool ReconnectGrpc()
{
    Print("Attempting gRPC reconnection...");
    
    // Stop current streaming
    if(grpc_streaming) {
        GrpcStopTradeStream();
        grpc_streaming = false;
    }
    
    // Attempt reconnection
    int result = GrpcReconnect();
    
    if(result != 0) {
        string error_msg;
        GrpcGetLastError(error_msg, 1024);
        Print("gRPC reconnection failed. Error: ", result, " - ", error_msg);
        grpc_connected = false;
        UpdateStatusIndicator("gRPC Disconnected", clrRed);
        return false;
    }
    
    // Restart streaming
    if(StartGrpcTradeStreaming()) {
        grpc_connected = true;
        UpdateStatusIndicator("gRPC Connected", clrGreen);
        Print("gRPC reconnection successful");
        return true;
    } else {
        grpc_connected = false;
        UpdateStatusIndicator("gRPC Streaming Failed", clrOrange);
        return false;
    }
}

void CheckGrpcConnection()
{
    if(!grpc_connected) {
        // Attempt reconnection if enough time has passed
        if(TimeCurrent() - grpc_last_connection_attempt >= grpc_connection_retry_interval) {
            grpc_last_connection_attempt = TimeCurrent();
            ReconnectGrpc();
        }
        return;
    }
    
    // Check if connection is still active
    int connected = GrpcIsConnected();
    if(connected == 0) {
        Print("gRPC connection lost. Will attempt reconnection.");
        grpc_connected = false;
        grpc_streaming = false;
        UpdateStatusIndicator("gRPC Disconnected", clrRed);
    } else {
        // Connection is good, perform health check
        string health_request = "{\"source\":\"hedgebot\",\"open_positions\":" + IntegerToString(PositionsTotal()) + "}";
        string health_response;
        
        int result = GrpcHealthCheck(health_request, health_response, 2048);
        if(result != 0) {
            Print("gRPC health check failed during periodic check");
            grpc_connected = false;
            UpdateStatusIndicator("gRPC Health Failed", clrOrange);
        }
    }
}

void ProcessGrpcTrades()
{
    if(!grpc_connected || !grpc_streaming) {
        static int debug_counter = 0;
        debug_counter++;
        if(debug_counter >= 1000) { // Print every 1000 skips
            debug_counter = 0;
            Print("DEBUG: Skipping trade processing - grpc_connected: ", grpc_connected, ", grpc_streaming: ", grpc_streaming);
        }
        return;
    }
    
    // Check how many trades are queued
    int queue_size = GrpcGetTradeQueueSize();
    if(queue_size <= 0) {
        return; // No trades to process
    }
    
    // Process up to 10 trades per timer cycle to avoid overload
    int processed = 0;
    const int MAX_TRADES_PER_CYCLE = 10;
    
    while(processed < MAX_TRADES_PER_CYCLE && processed < queue_size) {
        string trade_json;
        StringReserve(trade_json, 8192); // Pre-allocate buffer for C++ DLL
        int result = GrpcGetNextTrade(trade_json, 8192);
        
        if(result != 0) {
            string error_msg;
            GrpcGetLastError(error_msg, 1024);
            Print("Error getting next trade: ", result, " - ", error_msg);
            break;
        }
        
        if(trade_json == "") {
            break; // No more trades
        }
        
        // Process the trade
        ProcessTradeFromJson(trade_json);
        processed++;
    }
    
    if(processed > 0) {
        Print("Processed ", processed, " trades from gRPC stream (", (queue_size - processed), " remaining)");
    }
}

//+------------------------------------------------------------------+
//| Process gRPC trades without verbose logging (for timer events)  |
//+------------------------------------------------------------------+
void ProcessGrpcTradesQuiet()
{
    if(!grpc_connected || !grpc_streaming) {
        return; // Silent return - no logging
    }
    
    // Check how many trades are queued
    int queue_size = GrpcGetTradeQueueSize();
    if(queue_size <= 0) {
        return; // No trades to process - silent return
    }
    
    // Process up to 10 trades per timer cycle to avoid overload
    int processed = 0;
    const int MAX_TRADES_PER_CYCLE = 10;
    
    while(processed < MAX_TRADES_PER_CYCLE && processed < queue_size) {
        string trade_json;
        StringReserve(trade_json, 8192); // Pre-allocate buffer for C++ DLL
        int result = GrpcGetNextTrade(trade_json, 8192);
        
        if(result != 0) {
            // Only log errors - these are important
            string error_msg;
            GrpcGetLastError(error_msg, 1024);
            Print("Error getting next trade: ", result, " - ", error_msg);
            break;
        }
        
        if(trade_json == "") {
            break; // No more trades
        }
        
        // Process the trade - this will log important events like trade execution
        ProcessTradeFromJson(trade_json);
        processed++;
    }
    
    // Only log if we actually processed trades (event happened)
    if(processed > 0) {
        Print("Processed ", processed, " trades from gRPC stream (", (queue_size - processed), " remaining)");
    }
}

//+------------------------------------------------------------------+
//| Expert initialization function                                   |
//+------------------------------------------------------------------+
int OnInit()
{
    Print("===== ACHedgeMaster gRPC v3.08 Initializing =====");
    
    // Initialize CTrade object
    trade.SetExpertMagicNumber(MagicNumber);
    trade.SetDeviationInPoints(Slippage);
    trade.SetTypeFilling(ORDER_FILLING_IOC);
    
    // Adjust UseACRiskManagement based on LotSizingMode
    if (LotSizingMode == Asymmetric_Compounding) {
        UseACRiskManagement = true;
        Print("LotSizingMode is Asymmetric_Compounding, UseACRiskManagement set to true");
    } else {
        UseACRiskManagement = false;
        Print("LotSizingMode is ", EnumToString(LotSizingMode), ", UseACRiskManagement set to false");
    }
    
    // Reset trade groups on startup
    ResetTradeGroups();
    
    // Initialize position tracking map
    if(g_map_position_id_to_base_id == NULL) {
        g_map_position_id_to_base_id = new CHashMap<long, CString*>();
        if(CheckPointer(g_map_position_id_to_base_id) == POINTER_INVALID) {
            Print("FATAL ERROR: Failed to initialize position tracking map!");
            return(INIT_FAILED);
        }
        Print("Position tracking map initialized");
    }
    
    // Initialize arrays
    ArrayResize(g_ntInstrumentSymbols, 0);
    ArrayResize(g_ntAccountNames, 0);
    ArrayResize(g_open_mt5_original_nt_actions, 0);
    ArrayResize(g_open_mt5_original_nt_quantities, 0);
    
    // Initialize asymmetrical compounding risk management
    InitializeACRiskManagement(true);
    
    // Verify automated trading is enabled
    if(!TerminalInfoInteger(TERMINAL_TRADE_ALLOWED)) {
        MessageBox("Please enable automated trading in MT5 settings!", "Error", MB_OK|MB_ICONERROR);
        return INIT_FAILED;
    }
    
    // Check account type
    ENUM_ACCOUNT_MARGIN_MODE margin_mode = (ENUM_ACCOUNT_MARGIN_MODE)AccountInfoInteger(ACCOUNT_MARGIN_MODE);
    if(margin_mode != ACCOUNT_MARGIN_MODE_RETAIL_HEDGING) {
        Print("Warning: Account does not support hedging. Operating in netting mode.");
        Print("Current margin mode: ", margin_mode);
    }
    
    // Initialize broker specs
    QueryBrokerSpecs();
    
    // State recovery for existing positions
    PerformStateRecovery();
    
    // Initialize UI elements (before gRPC to ensure they work regardless)
    InitStatusIndicator();
    InitStatusOverlay();
    InitATRTrailing();
    
    // Initialize gRPC connection (NON-BLOCKING - EA should work without bridge)
    Print("Attempting gRPC connection (EA will work without bridge)...");
    if(!InitializeGrpcConnection()) {
        Print("INFO: gRPC connection not available. EA running in offline mode.");
        Print("Bridge server connection will be retried automatically.");
        grpc_connected = false;
        UpdateStatusIndicator("Bridge Offline", clrOrange);
    } else {
        Print("gRPC connection established successfully");
        grpc_connected = true;
        UpdateStatusIndicator("Bridge Connected", clrGreen);
        
        // Start trade streaming (non-critical)
        if(!StartGrpcTradeStreaming()) {
            Print("INFO: Trade streaming not started. Will retry automatically.");
        }
    }
    
    Print("=================================");
    Print("✓ ACHedgeMaster gRPC initialization complete");
    Print("Server: ", BridgeServerAddress, ":", BridgeServerPort);
    Print("EA Status: Ready (works with or without bridge)");
    Print("=================================");
    
    // Set up millisecond timer for fast trade processing (100ms intervals)
    EventSetMillisecondTimer(100);
    Print("Fast trade processing timer initialized (100ms intervals)");
    
    return(INIT_SUCCEEDED);
}

//+------------------------------------------------------------------+
//| Timer event handler - Fast trade processing (100ms intervals)   |
//+------------------------------------------------------------------+
void OnTimer()
{
    // Process gRPC trades without verbose logging
    ProcessGrpcTradesQuiet();
}

// Periodic maintenance checks handled in OnTick

//+------------------------------------------------------------------+
//| Trade Processing Functions                                       |
//+------------------------------------------------------------------+
void ProcessTradeFromJson(const string& trade_json)
{
    // Debug logging for all responses (including CLOSE_HEDGE detection)
    if(StringFind(trade_json, "CLOSE_HEDGE") >= 0) {
        Print("ACHM_CLOSURE_DEBUG: [ProcessTradeFromJson] *** DETECTED CLOSE_HEDGE IN gRPC RESPONSE ***");
        Print("ACHM_CLOSURE_DEBUG: [ProcessTradeFromJson] Full Response: ", trade_json);
    }
    
    // Check for duplicate trade based on trade ID
    string tradeId = "";
    int idPos = StringFind(trade_json, "\"id\":\"");
    if(idPos >= 0) {
        idPos += 6;  // Length of "\"id\":\""
        int idEndPos = StringFind(trade_json, "\"", idPos);
        if(idEndPos > idPos) {
            tradeId = StringSubstr(trade_json, idPos, idEndPos - idPos);
            if(tradeId == lastTradeId) {
                Print("ACHM_LOG: [ProcessTradeFromJson] Ignoring duplicate message with ID: ", tradeId);
                return;
            }
            
            // Ignore init_stream messages
            if(tradeId == "init_stream") {
                Print("ACHM_LOG: [ProcessTradeFromJson] Ignoring init_stream message");
                return;
            }
            
            lastTradeId = tradeId;
        }
    }
    
    // Parse trade information from JSON response
    string incomingNtAction = "";
    double incomingNtQuantity = 0.0;
    double price = 0.0;
    string baseIdFromJson = "";
    bool isExit = false;
    int measurementPips = 0;
    string orderType = "";
    
    // Parse NT performance data from enhanced JSON message
    double nt_balance = 0.0;
    double nt_daily_pnl = 0.0;
    string nt_trade_result = "";
    int nt_session_trades = 0;
    
    // Parse enhanced NT performance data if available
    ParseNTPerformanceData(trade_json, nt_balance, nt_daily_pnl, nt_trade_result, nt_session_trades);
    UpdateNTPerformanceTracking(nt_balance, nt_daily_pnl, nt_trade_result, nt_session_trades);
    
    // Parse basic trade data
    incomingNtAction = GetJSONStringValue(trade_json, "\"action\"");
    incomingNtQuantity = GetJSONDouble(trade_json, "quantity");
    price = GetJSONDouble(trade_json, "price");
    
    // Parse base_id
    baseIdFromJson = GetJSONStringValue(trade_json, "\"base_id\"");
    if (baseIdFromJson == "") {
        int tempBaseIdPos = StringFind(trade_json, "\"base_id\":\"");
        if(tempBaseIdPos >= 0) {
            tempBaseIdPos += 11;
            int tempBaseIdEndPos = StringFind(trade_json, "\"", tempBaseIdPos);
            if(tempBaseIdEndPos > tempBaseIdPos) {
                baseIdFromJson = StringSubstr(trade_json, tempBaseIdPos, tempBaseIdEndPos - tempBaseIdPos);
            }
        }
    }
    
    // Parse order type and measurement
    orderType = GetJSONStringValue(trade_json, "\"order_type\"");
    measurementPips = GetJSONIntValue(trade_json, "measurement_pips", 0);
    
    Print("ACHM_LOG: [ProcessTradeFromJson] Parsed NT base_id: '", baseIdFromJson, "', Action: '", incomingNtAction, "', Qty: ", incomingNtQuantity);
    
    // Validate parsed data - prevent processing empty trades
    if(StringLen(incomingNtAction) == 0 && incomingNtQuantity == 0.0 && StringLen(baseIdFromJson) == 0) {
        Print("ACHM_LOG: [ProcessTradeFromJson] Ignoring empty trade data");
        return;
    }
    
    // Filter out HedgeClose orders
    string orderName = GetJSONStringValue(trade_json, "\"order_name\"");
    if (orderName == "") {
        orderName = GetJSONStringValue(trade_json, "\"name\"");
    }
    
    if (StringFind(orderName, "HedgeClose") >= 0) {
        Print("ACHM_LOG: [ProcessTradeFromJson] Ignoring HedgeClose order: ", orderName);
        return;
    }
    
    // Process the trade based on action type
    if(incomingNtAction == "CLOSE_HEDGE") {
        // Extract MT5 ticket from JSON if available
        ulong mt5Ticket = 0;
        Print("ACHM_CLOSURE_DEBUG: [ProcessTradeFromJson] Examining JSON for mt5_ticket: ", StringSubstr(trade_json, 0, 200));
        int ticketPos = StringFind(trade_json, "\"mt5_ticket\":");
        if(ticketPos >= 0) {
            ticketPos += 13; // Length of "mt5_ticket":
            string ticketStr = StringSubstr(trade_json, ticketPos, 20);
            int commaPos = StringFind(ticketStr, ",");
            int bracePos = StringFind(ticketStr, "}");
            int endPos = -1;
            if(commaPos > 0 && (bracePos < 0 || commaPos < bracePos)) endPos = commaPos;
            else if(bracePos > 0) endPos = bracePos;
            
            if(endPos > 0) {
                ticketStr = StringSubstr(ticketStr, 0, endPos);
                mt5Ticket = StringToInteger(ticketStr);
                Print("ACHM_CLOSURE_DEBUG: [ProcessTradeFromJson] Extracted MT5 ticket: ", mt5Ticket);
            }
        }
        
        ProcessCloseHedgeAction(baseIdFromJson, trade_json, mt5Ticket);
    } else if(orderType == "TP" || orderType == "SL") {
        ProcessTPSLOrder(baseIdFromJson, orderType, measurementPips, trade_json);
    } else {
        ProcessRegularTrade(incomingNtAction, incomingNtQuantity, price, baseIdFromJson, trade_json);
    }
}

void ProcessCloseHedgeAction(const string& baseId, const string& trade_json, ulong mt5Ticket = 0)
{
    Print("ACHM_CLOSURE_DEBUG: [ProcessCloseHedgeAction] Processing CLOSE_HEDGE for base_id: ", baseId, ", mt5Ticket: ", mt5Ticket);
    
    bool hedgeFound = false;
    int totalPositions = PositionsTotal();
    Print("ACHM_CLOSURE_DEBUG: [ProcessCloseHedgeAction] Total positions to search: ", totalPositions);
    
    // If we have an MT5 ticket, try to close by ticket first
    if(mt5Ticket > 0) {
        if(PositionSelectByTicket(mt5Ticket)) {
            double volume = PositionGetDouble(POSITION_VOLUME);
            Print("ACHM_CLOSURE_DEBUG: [ProcessCloseHedgeAction] Found position by ticket #", mt5Ticket, " with volume ", volume);
            
            if(trade.PositionClose(mt5Ticket)) {
                Print("ACHM_CLOSURE: Successfully closed hedge position by ticket #", mt5Ticket, " for base_id: ", baseId);
                SubmitTradeResult("success", mt5Ticket, volume, true, baseId);
                
                // Remove from tracking
                if(g_map_position_id_to_base_id != NULL) {
                    CString* baseIdPtr = NULL;
                    g_map_position_id_to_base_id.TryGetValue(mt5Ticket, baseIdPtr);
                    if(baseIdPtr != NULL) {
                        delete baseIdPtr;
                        g_map_position_id_to_base_id.Remove(mt5Ticket);
                    }
                }
                return; // Successfully closed by ticket
            } else {
                int closeError = GetLastError();
                Print("ACHM_CLOSURE: Failed to close position by ticket #", mt5Ticket, " - Error: ", closeError);
                // Fall through to try comment matching
            }
        } else {
            Print("ACHM_CLOSURE_DEBUG: [ProcessCloseHedgeAction] Could not select position by ticket #", mt5Ticket);
            // Fall through to try comment matching
        }
    }
    
    // Fallback to comment-based matching
    for(int i = totalPositions - 1; i >= 0; i--) {
        if(PositionGetTicket(i) == 0) {
            Print("ACHM_CLOSURE_DEBUG: [ProcessCloseHedgeAction] Position ", i, ": Failed to get ticket, skipping");
            continue;
        }
        
        long positionTicket = PositionGetInteger(POSITION_TICKET);
        string comment = PositionGetString(POSITION_COMMENT);
        Print("ACHM_CLOSURE_DEBUG: [ProcessCloseHedgeAction] Position ", i, ": Ticket #", positionTicket, ", Comment: '", comment, "'");
        
        // Check if this position matches the base_id (flexible matching for truncated comments)
        bool commentMatches = false;
        
        // First try exact match
        if(StringFind(comment, baseId) >= 0) {
            commentMatches = true;
            Print("ACHM_CLOSURE_DEBUG: [ProcessCloseHedgeAction] Position #", positionTicket, " EXACT match found");
        }
        // MT5 truncates comments at ~31 chars, so we need smarter matching
        else {
            // Extract the timestamp portion from both baseId and comment
            // BaseId format: TRADE_YYYYMMDD_HHMMSS_XXX_XXXX
            // Comment format: NT_Hedge_DIR_TRADE_YYYYMMDD_HHM... (truncated)
            
            // Check if comment contains the hedge prefix and extract the trade portion
            string buyPrefix = "NT_Hedge_BUY_";
            string sellPrefix = "NT_Hedge_SELL_";
            string tradePortionComment = "";
            
            if(StringFind(comment, buyPrefix) == 0) {
                tradePortionComment = StringSubstr(comment, StringLen(buyPrefix));
            } else if(StringFind(comment, sellPrefix) == 0) {
                tradePortionComment = StringSubstr(comment, StringLen(sellPrefix));
            }
            
            if(tradePortionComment != "") {
                // Now check if the baseId starts with what we have in the truncated comment
                if(StringFind(baseId, tradePortionComment) == 0) {
                    commentMatches = true;
                    Print("ACHM_CLOSURE_DEBUG: [ProcessCloseHedgeAction] Position #", positionTicket, " TRUNCATED match found - comment trade portion: '", tradePortionComment, "' matches start of baseId: '", baseId, "'");
                } else {
                    // Try matching just the date/time portion (first 15 chars of TRADE_YYYYMMDD_HH)
                    string baseIdPrefix = StringSubstr(baseId, 0, MathMin(15, StringLen(tradePortionComment)));
                    if(tradePortionComment == baseIdPrefix) {
                        commentMatches = true;
                        Print("ACHM_CLOSURE_DEBUG: [ProcessCloseHedgeAction] Position #", positionTicket, " DATE/TIME match found - '", baseIdPrefix, "'");
                    }
                }
            }
            
            if(!commentMatches) {
                Print("ACHM_CLOSURE_DEBUG: [ProcessCloseHedgeAction] Position #", positionTicket, " NO MATCH - Comment: '", comment, "', BaseId: '", baseId, "'");
            }
        }
        
        if(commentMatches) {
            hedgeFound = true;
            
            // Close the position
            double volume = PositionGetDouble(POSITION_VOLUME);
            Print("ACHM_CLOSURE_DEBUG: [ProcessCloseHedgeAction] Attempting to close position #", positionTicket, " with volume ", volume);
            
            if(trade.PositionClose(positionTicket)) {
                Print("ACHM_CLOSURE: Successfully closed hedge position #", positionTicket, " for base_id: ", baseId);
                
                // Submit trade result via gRPC
                SubmitTradeResult("success", positionTicket, volume, true, baseId);
                
                // Remove from tracking
                if(g_map_position_id_to_base_id != NULL) {
                    CString* baseIdPtr = NULL;
                    g_map_position_id_to_base_id.TryGetValue(positionTicket, baseIdPtr);
                    if(baseIdPtr != NULL) {
                        delete baseIdPtr;
                        g_map_position_id_to_base_id.Remove(positionTicket);
                    }
                }
            } else {
                int closeError = GetLastError();
                Print("ACHM_CLOSURE: Failed to close hedge position #", positionTicket, " for base_id: ", baseId, " - Error: ", closeError);
                Print("ACHM_CLOSURE_DEBUG: [ProcessCloseHedgeAction] Close failure details - Volume: ", volume, ", Symbol: ", _Symbol, ", Error: ", closeError);
                SubmitTradeResult("failed", positionTicket, volume, true, baseId);
            }
            
            // CRITICAL: Stop after closing one position to avoid closing multiple positions with similar BaseIDs
            break;
        }
    }
    
    if(!hedgeFound) {
        Print("ACHM_CLOSURE: No hedge position found for base_id: ", baseId);
        Print("ACHM_CLOSURE_DEBUG: [ProcessCloseHedgeAction] SUMMARY - Searched ", totalPositions, " positions, none matched base_id: '", baseId, "'");
        Print("ACHM_CLOSURE_DEBUG: [ProcessCloseHedgeAction] Expected comment format: 'NT_Hedge_BUY_", baseId, "' or 'NT_Hedge_SELL_", baseId, "'");
    } else {
        Print("ACHM_CLOSURE_DEBUG: [ProcessCloseHedgeAction] Successfully found and processed hedge position for base_id: ", baseId);
    }
}

void ProcessTPSLOrder(const string& baseId, const string& orderType, int measurementPips, const string& trade_json)
{
    Print("ACHM_LOG: [ProcessTPSLOrder] Processing ", orderType, " order for base_id: ", baseId, ", pips: ", measurementPips);
    
    // Store TP/SL measurement
    lastTPSL.baseTradeId = baseId;
    lastTPSL.orderType = orderType;
    lastTPSL.pips = measurementPips;
    
    // Get raw measurement from JSON
    double rawMeasurement = 0.0;
    int rawPos = StringFind(trade_json, "\"raw_measurement\":");
    if(rawPos >= 0) {
        rawPos += 18; // Length of "\"raw_measurement\":"
        string rawStr = StringSubstr(trade_json, rawPos, 20);
        int commaPos = StringFind(rawStr, ",");
        if(commaPos > 0) {
            rawStr = StringSubstr(rawStr, 0, commaPos);
        }
        rawMeasurement = StringToDouble(rawStr);
    }
    lastTPSL.rawMeasurement = rawMeasurement;
    
    Print("ACHM_LOG: [ProcessTPSLOrder] Stored TP/SL measurement: ", orderType, " = ", measurementPips, " pips (", rawMeasurement, ")");
}

void ProcessRegularTrade(const string& action, double quantity, double price, const string& baseId, const string& trade_json)
{
    Print("ACHM_LOG: [ProcessRegularTrade] Processing regular trade - Action: ", action, ", Qty: ", quantity, ", Price: ", price, ", BaseId: ", baseId);
    
    // Determine trade direction for hedging
    ENUM_ORDER_TYPE orderType;
    string commentPrefix;
    
    Print("ACHM_HEDGE_DEBUG: [ProcessRegularTrade] NT Action: '", action, "', EnableHedging: ", EnableHedging);
    
    if(EnableHedging) {
        // Hedge opposite direction
        if(action == "buy" || action == "BUY" || action == "Buy") {
            orderType = ORDER_TYPE_SELL;
            commentPrefix = EA_COMMENT_PREFIX_SELL;
            Print("ACHM_HEDGE_DEBUG: [ProcessRegularTrade] HEDGING: NT BUY → MT5 SELL");
        } else {
            orderType = ORDER_TYPE_BUY;
            commentPrefix = EA_COMMENT_PREFIX_BUY;
            Print("ACHM_HEDGE_DEBUG: [ProcessRegularTrade] HEDGING: NT SELL → MT5 BUY");
        }
    } else {
        // Copy same direction
        if(action == "buy" || action == "BUY" || action == "Buy") {
            orderType = ORDER_TYPE_BUY;
            commentPrefix = EA_COMMENT_PREFIX_BUY;
            Print("ACHM_HEDGE_DEBUG: [ProcessRegularTrade] COPYING: NT BUY → MT5 BUY");
        } else {
            orderType = ORDER_TYPE_SELL;
            commentPrefix = EA_COMMENT_PREFIX_SELL;
            Print("ACHM_HEDGE_DEBUG: [ProcessRegularTrade] COPYING: NT SELL → MT5 SELL");
        }
    }
    
    Print("ACHM_HEDGE_DEBUG: [ProcessRegularTrade] Final orderType: ", EnumToString(orderType));
    
    // Calculate lot size based on mode
    double lotSize = CalculateLotSize(quantity, baseId, trade_json);
    
    // Validate lot size
    double minLot = SymbolInfoDouble(_Symbol, SYMBOL_VOLUME_MIN);
    double maxLot = SymbolInfoDouble(_Symbol, SYMBOL_VOLUME_MAX);
    double lotStep = SymbolInfoDouble(_Symbol, SYMBOL_VOLUME_STEP);
    
    if(lotSize < minLot) {
        Print("ACHM_LOG: Calculated lot size ", lotSize, " is below minimum ", minLot, ". Using minimum.");
        lotSize = minLot;
    }
    if(lotSize > maxLot) {
        Print("ACHM_LOG: Calculated lot size ", lotSize, " exceeds maximum ", maxLot, ". Using maximum.");
        lotSize = maxLot;
    }
    
    // Round to lot step
    lotSize = NormalizeDouble(lotSize / lotStep, 0) * lotStep;
    
    // Execute the trades - loop for multiple contracts
    string comment = commentPrefix + baseId;
    int totalContracts = (int)MathRound(quantity);  // Round to nearest integer
    int successfulTrades = 0;
    
    Print("ACHM_LOG: Need to open ", totalContracts, " hedge trades for NT quantity ", quantity);
    
    for(int i = 0; i < totalContracts; i++) {
        bool success = false;
        ulong ticket = 0;
        
        if(orderType == ORDER_TYPE_BUY) {
            success = trade.Buy(lotSize, _Symbol, 0, 0, 0, comment);
        } else {
            success = trade.Sell(lotSize, _Symbol, 0, 0, 0, comment);
        }
        
        if(success) {
            ticket = trade.ResultOrder();
            Print("ACHM_LOG: Successfully executed ", EnumToString(orderType), " order #", ticket, " for ", lotSize, " lots (trade ", (i+1), " of ", totalContracts, "), base_id: ", baseId);
            
            // Add to position tracking
            if(g_map_position_id_to_base_id != NULL && ticket > 0) {
                CString* baseIdPtr = new CString();
                baseIdPtr.Assign(baseId);
                g_map_position_id_to_base_id.Add(ticket, baseIdPtr);
            }
            
            // Submit success result for each trade
            SubmitTradeResult("success", ticket, lotSize, false, baseId);
            successfulTrades++;
            
            // Handle ATR trailing for this position if enabled
            if(UseATRTrailing) {
                double currentPrice = (orderType == ORDER_TYPE_BUY) ? SymbolInfoDouble(_Symbol, SYMBOL_ASK) : SymbolInfoDouble(_Symbol, SYMBOL_BID);
                string positionType = (orderType == ORDER_TYPE_BUY) ? "BUY" : "SELL";
                HandleATRTrailingForPosition(ticket, price, currentPrice, positionType, lotSize);
            }
            
        } else {
            int error = GetLastError();
            Print("ACHM_LOG: Failed to execute ", EnumToString(orderType), " order ", (i+1), " of ", totalContracts, " for base_id: ", baseId, ". Error: ", error);
            SubmitTradeResult("failed", 0, lotSize, false, baseId);
        }
        
        // Small delay between trades to avoid overwhelming the broker
        if(i < totalContracts - 1) {
            Sleep(50);  // 50ms delay
        }
    }
    
    // Update global futures tracking based on successful trades
    if(action == "buy" || action == "BUY") {
        globalFutures += successfulTrades;
    } else {
        globalFutures -= successfulTrades;
    }
    
    // Force overlay recalculation
    ForceOverlayRecalculation();
    
    Print("ACHM_LOG: Opened ", successfulTrades, " of ", totalContracts, " requested hedge trades for base_id: ", baseId);
}

double CalculateLotSize(double ntQuantity, const string& baseId, const string& trade_json)
{
    double lotSize = DefaultLot;
    
    switch(LotSizingMode) {
        case Asymmetric_Compounding:
            // Use AC risk management
            if(UseACRiskManagement) {
                lotSize = CalculateACLotSize(ntQuantity);
            } else {
                lotSize = DefaultLot;
            }
            break;
            
        case Fixed_Lot_Size:
            lotSize = DefaultLot;
            break;
            
        case Elastic_Hedging:
            lotSize = CalculateElasticLotSize(ntQuantity);
            break;
    }
    
    return lotSize;
}

//+------------------------------------------------------------------+
//| Trade Result Submission                                         |
//+------------------------------------------------------------------+
void SubmitTradeResult(const string& status, ulong ticket, double volume, bool isClose, const string& id)
{
    string result_json = "{";
    result_json += "\"status\":\"" + status + "\",";
    result_json += "\"ticket\":" + IntegerToString(ticket) + ",";
    result_json += "\"volume\":" + DoubleToString(volume, 2) + ",";
    result_json += "\"is_close\":" + (isClose ? "true" : "false") + ",";
    result_json += "\"id\":\"" + id + "\"";
    result_json += "}";
    
    int result = GrpcSubmitTradeResult(result_json);
    
    if(result != 0) {
        string error_msg;
        GrpcGetLastError(error_msg, 1024);
        Print("Failed to submit trade result via gRPC. Error: ", result, " - ", error_msg);
    }
}

//+------------------------------------------------------------------+
//| Expert deinitialization function                                |
//+------------------------------------------------------------------+
void OnDeinit(const int reason)
{
    Print("OnDeinit: Starting graceful cleanup... Reason: ", reason);
    Print("OnDeinit: Deinit reason codes: 0=Program, 1=Remove, 2=Recompile, 3=ChartClose, 4=Parameters, 5=Account, 6=Template, 7=Initfailed, 8=Close");
    
    // Step 1: Stop timer immediately to prevent new processing
    EventKillTimer();
    Print("OnDeinit: Timer stopped");
    
    // Step 2: Set global flag to stop all processing
    grpc_connected = false;
    grpc_streaming = false;
    
    // Step 3: Allow brief time for current operations to complete
    Sleep(50);  // Reduced from 100ms for faster shutdown
    
    // Step 4: Attempt graceful gRPC shutdown with timeout protection
    Print("OnDeinit: Attempting graceful gRPC shutdown...");
    
    // Try to stop streaming first (safer)
    int stream_stop_result = GrpcStopTradeStream();
    if(stream_stop_result != 0) {
        Print("OnDeinit: Trade stream stop returned: ", stream_stop_result, " (non-critical)");
    }
    
    // Brief pause before full shutdown
    Sleep(25);
    
    // Attempt graceful shutdown with error handling
    int shutdown_result = GrpcShutdown();
    if(shutdown_result != 0) {
        string error_msg = "Unknown error";
        GrpcGetLastError(error_msg, 1024);
        Print("OnDeinit: gRPC shutdown returned: ", shutdown_result, " - ", error_msg);
        Print("OnDeinit: This is normal if bridge was not connected");
    } else {
        Print("OnDeinit: gRPC connection shut down successfully");
    }

    // Step 5: Clean up UI elements (always safe)
    ObjectDelete(0, ButtonName);
    RemoveStatusIndicator();
    RemoveStatusOverlay();
    Comment("");
    Print("OnDeinit: UI elements cleaned up");
    
    // Step 6: Clean up memory structures with enhanced safety
    if(CheckPointer(g_map_position_id_to_base_id) == POINTER_DYNAMIC) {
        Print("OnDeinit: Cleaning up position tracking map...");
        
        // Enhanced safety checks
        int mapCount = 0;
        mapCount = g_map_position_id_to_base_id.Count();
        if(mapCount >= 0 && mapCount < 10000) { // Sanity check
            long keys[];
            CString* values_ptr[];
            if(g_map_position_id_to_base_id.CopyTo(keys, values_ptr)) {
                for(int i = 0; i < ArraySize(values_ptr); i++) {
                    if(CheckPointer(values_ptr[i]) == POINTER_DYNAMIC) {
                        delete values_ptr[i];
                    }
                }
                Print("OnDeinit: Cleaned up ", ArraySize(values_ptr), " map entries");
            }
            g_map_position_id_to_base_id.Clear();
        }
        delete g_map_position_id_to_base_id;
        g_map_position_id_to_base_id = NULL;
        Print("OnDeinit: Position tracking map cleaned up");
    }
    
    // Step 7: Clean up ATR trailing resources
    CleanupATRTrailing();
    
    // Step 8: Final brief pause to ensure cleanup completion
    Sleep(25);  // Reduced from 200ms for faster shutdown
    
    Print("OnDeinit: EA shutdown complete - all resources cleaned up");
    Print("OnDeinit: EA can be safely removed or reloaded");
}

//+------------------------------------------------------------------+
//| Expert tick function                                             |
//+------------------------------------------------------------------+
void OnTick()
{
    // CRITICAL: Process gRPC trade queue
    ProcessGrpcTrades();
    
    // Add periodic connection checks
    static int health_check_counter = 0;
    health_check_counter++;
    if(health_check_counter >= 100) { // Check every 100 ticks
        health_check_counter = 0;
        CheckGrpcConnection();
    }
    
    // Throttle UI updates to reduce CPU usage - update every 10 ticks
    static int tick_counter = 0;
    static bool last_connection_status = false;
    tick_counter++;
    
    bool current_connection_status = (GrpcIsConnected() == 1);
    
    // Update display every 10 ticks or when connection status changes
    if(tick_counter >= 10 || current_connection_status != last_connection_status) {
        tick_counter = 0;
        last_connection_status = current_connection_status;
        
        // Display EA status on chart
        string ea_name = MQLInfoString(MQL_PROGRAM_NAME);
        string ea_version = "3.00"; // gRPC version
        string connection_status = current_connection_status ? "Connected" : "Disconnected";
        
        string stats_comment = StringFormat("%s v%s | %s | Balance: %.2f | Positions: %d | gRPC: %s",
                                            ea_name,
                                            ea_version,
                                            _Symbol,
                                            AccountInfoDouble(ACCOUNT_BALANCE),
                                            PositionsTotal(),
                                            connection_status);
        Comment(stats_comment);
        
        // Update status indicator only when connection status changes
        if(current_connection_status) {
            UpdateStatusIndicator("HedgeBot: gRPC Connected & Ready", clrLime);
        } else {
            UpdateStatusIndicator("HedgeBot: gRPC Disconnected", clrRed);
        }
    }
    
    // Update trailing stops only if we have open positions - throttle to every 5 ticks
    static int trailing_tick_counter = 0;
    trailing_tick_counter++;
    
    if(PositionsTotal() > 0 && trailing_tick_counter >= 5) {
        trailing_tick_counter = 0;
        
        for(int i = 0; i < PositionsTotal(); i++)
        {
            ulong ticket = PositionGetTicket(i);
            if(ticket == 0) continue;

            if(PositionSelectByTicket(ticket))
            {
                if(PositionGetInteger(POSITION_MAGIC) == MagicNumber && PositionGetString(POSITION_SYMBOL) == _Symbol)
                {
                    string orderTypeString = "";
                    ENUM_POSITION_TYPE positionType = (ENUM_POSITION_TYPE)PositionGetInteger(POSITION_TYPE);
                    if(positionType == POSITION_TYPE_BUY) orderTypeString = "BUY";
                    else if(positionType == POSITION_TYPE_SELL) orderTypeString = "SELL";
                    else continue;

                    double entryPrice = PositionGetDouble(POSITION_PRICE_OPEN);
                    double currentPrice = (positionType == POSITION_TYPE_BUY) ? 
                                        SymbolInfoDouble(_Symbol, SYMBOL_BID) : 
                                        SymbolInfoDouble(_Symbol, SYMBOL_ASK);
                    double profit = PositionGetDouble(POSITION_PROFIT);
                    double volume = PositionGetDouble(POSITION_VOLUME);
                    
                    // Handle ATR trailing stop logic
                    HandleATRTrailingForPosition(ticket, entryPrice, currentPrice, positionType == POSITION_TYPE_BUY ? "BUY" : "SELL", volume);
                }
            }
        }
    }
    
    // Update status overlay only when necessary - throttle with UI updates
    if(tick_counter == 0) {
        UpdateStatusOverlay();
    }
    
    // Essential maintenance tasks
    static datetime g_last_maintenance = 0;
    static datetime g_last_integrity_check = 0;
    const int MAINTENANCE_INTERVAL = 60;        // 1 minute for general maintenance
    const int INTEGRITY_CHECK_INTERVAL = 300;   // 5 minutes for integrity checks
    
    datetime current_time = TimeCurrent();
    
    // General maintenance every minute
    if(current_time - g_last_maintenance >= MAINTENANCE_INTERVAL) {
        g_last_maintenance = current_time;
        
        // Check gRPC connection status periodically
        if(GrpcIsConnected() != 1) {
            Print("gRPC connection lost, attempting reconnection...");
            ReconnectGrpc();
        }
        
        // Defer processing if broker specs are not ready
        if(!g_broker_specs_ready) {
            UpdateStatusIndicator("Specs...", clrOrange);
        }
    }
    
    // Array integrity and cleanup checks every 5 minutes
    if(current_time - g_last_integrity_check >= INTEGRITY_CHECK_INTERVAL) {
        g_last_integrity_check = current_time;
        
        if(!ValidateArrayIntegrity(false)) {
            Print("CRITICAL_ARRAY_CORRUPTION: Array integrity check failed at ", TimeToString(current_time));
            ValidateArrayIntegrity(true);
        }
        
        CleanupNotificationTracking();
        CleanupClosedBaseIdTracking();
    }
}

//+------------------------------------------------------------------+
//| OnTradeTransaction - Handle trade transactions for closure detection |
//+------------------------------------------------------------------+
void OnTradeTransaction(const MqlTradeTransaction& trans,
                       const MqlTradeRequest& request,
                       const MqlTradeResult& result)
{
    // Only process deal transactions (actual position changes)
    if(trans.type != TRADE_TRANSACTION_DEAL_ADD)
        return;
    
    // Only process deals from our EA (matching magic number)
    if(trans.deal == 0)
        return;
        
    // Get deal information
    if(!HistoryDealSelect(trans.deal))
        return;
        
    long deal_magic = HistoryDealGetInteger(trans.deal, DEAL_MAGIC);
    if(deal_magic != MagicNumber)
        return;
        
    ENUM_DEAL_TYPE deal_type = (ENUM_DEAL_TYPE)HistoryDealGetInteger(trans.deal, DEAL_TYPE);
    ENUM_DEAL_ENTRY deal_entry = (ENUM_DEAL_ENTRY)HistoryDealGetInteger(trans.deal, DEAL_ENTRY);
    string deal_comment = HistoryDealGetString(trans.deal, DEAL_COMMENT);
    ulong position_ticket = HistoryDealGetInteger(trans.deal, DEAL_POSITION_ID);
    double deal_volume = HistoryDealGetDouble(trans.deal, DEAL_VOLUME);
    
    // Only process position closures (exit deals)
    if(deal_entry != DEAL_ENTRY_OUT)
        return;
        
    Print("CLOSURE_DETECTION: Position closed - Ticket: ", position_ticket, 
          ", Volume: ", deal_volume, ", Comment: ", deal_comment);
    
    // Extract BaseID from comment (format: NT_Hedge_BUY_BaseID or NT_Hedge_SELL_BaseID)
    string baseId = "";
    if(StringFind(deal_comment, CommentPrefix) == 0) {
        // Extract BaseID from comment
        string temp_comment = deal_comment;
        StringReplace(temp_comment, EA_COMMENT_PREFIX_BUY, "");
        StringReplace(temp_comment, EA_COMMENT_PREFIX_SELL, "");
        StringReplace(temp_comment, CommentPrefix, "");
        baseId = temp_comment;
    }
    
    if(baseId == "") {
        Print("CLOSURE_DETECTION: Could not extract BaseID from comment: ", deal_comment);
        return;
    }
    
    Print("CLOSURE_DETECTION: Extracted BaseID: ", baseId, " from closed position");
    
    // Determine closure reason based on context
    string closure_reason = "MT5_position_closed";
    
    // Check if it was a stop loss
    if(StringFind(deal_comment, "[sl]") >= 0 || StringFind(deal_comment, "stop loss") >= 0) {
        closure_reason = "MT5_stop_loss";
    }
    // Check if it was a take profit  
    else if(StringFind(deal_comment, "[tp]") >= 0 || StringFind(deal_comment, "take profit") >= 0) {
        closure_reason = "MT5_take_profit";
    }
    // Check if it was a manual close
    else if(result.retcode == TRADE_RETCODE_DONE && request.action == TRADE_ACTION_DEAL) {
        closure_reason = "MT5_manual_close";
    }
    
    // Send closure notification to Bridge Server
    NotifyMT5PositionClosure(baseId, position_ticket, deal_volume, closure_reason);
}

//+------------------------------------------------------------------+
//| Notify Bridge Server of MT5 position closure                   |
//+------------------------------------------------------------------+
void NotifyMT5PositionClosure(string baseId, ulong mt5Ticket, double volume, string closureReason)
{
    Print("CLOSURE_NOTIFICATION: Notifying bridge of MT5 closure - BaseID: ", baseId, 
          ", Ticket: ", mt5Ticket, ", Reason: ", closureReason);
          
    // Create hedge close notification JSON
    string notification_json = StringFormat(
        "{"
        "\"event_type\":\"HEDGE_CLOSE\","
        "\"base_id\":\"%s\","
        "\"nt_instrument_symbol\":\"%s\","
        "\"nt_account_name\":\"MT5_Account\","
        "\"closed_hedge_quantity\":%.2f,"
        "\"closed_hedge_action\":\"%s\","
        "\"timestamp\":\"%s\","
        "\"closure_reason\":\"%s\","
        "\"mt5_ticket\":%d"
        "}",
        baseId,
        _Symbol,
        volume,
        volume > 0 ? "SELL" : "BUY",  // Opposite of original direction
        TimeToString(TimeCurrent(), TIME_DATE|TIME_MINUTES|TIME_SECONDS),
        closureReason,
        mt5Ticket
    );
    
    Print("CLOSURE_NOTIFICATION: Sending notification JSON: ", notification_json);
    
    // Send via gRPC
    int result = GrpcNotifyHedgeClose(notification_json);
    if(result == 0) {
        Print("CLOSURE_NOTIFICATION: Successfully sent MT5 closure notification for BaseID: ", baseId);
    } else {
        string error_msg;
        GrpcGetLastError(error_msg, 1024);
        Print("CLOSURE_NOTIFICATION: Failed to send closure notification. Error: ", result, " - ", error_msg);
    }
}

//+------------------------------------------------------------------+
//| ChartEvent function - Handle button clicks                      |
//+------------------------------------------------------------------+
void OnChartEvent(const int id, const long &lparam, const double &dparam, const string &sparam)
{
    // Check if this is a button click event
    if(id == CHARTEVENT_OBJECT_CLICK)
    {
        // Check if our trailing button was clicked
        if(sparam == ButtonName)
        {
            // Toggle manual trailing activation
            ManualTrailingActivated = !ManualTrailingActivated;
            
            // Update button color and text based on state
            ObjectSetInteger(0, ButtonName, OBJPROP_COLOR, 
                            ManualTrailingActivated ? ButtonColorActive : ButtonColorInactive);
            ObjectSetString(0, ButtonName, OBJPROP_TEXT, 
                           ManualTrailingActivated ? "Trailing Active" : "Start Trailing?");
            
            // Print status message
            Print(ManualTrailingActivated ? "Manual trailing activation enabled" : "Manual trailing activation disabled");
            
            ChartRedraw();
        }
    }
}

//+------------------------------------------------------------------+
//| Array Integrity Validation Functions                            |
//+------------------------------------------------------------------+
bool ValidateArrayIntegrity(bool log_details = false)
{
    int pos_ids_size = ArraySize(g_open_mt5_pos_ids);
    int actions_size = ArraySize(g_open_mt5_actions);
    int base_ids_size = ArraySize(g_open_mt5_base_ids);
    int nt_symbols_size = ArraySize(g_open_mt5_nt_symbols);
    int nt_accounts_size = ArraySize(g_open_mt5_nt_accounts);
    int orig_nt_actions_size = ArraySize(g_open_mt5_original_nt_actions);
    int orig_nt_qty_size = ArraySize(g_open_mt5_original_nt_quantities);
    
    bool integrity_ok = true;
    
    if(log_details) {
        Print("ARRAY_INTEGRITY_CHECK: Array sizes - pos_ids=", pos_ids_size, 
              ", actions=", actions_size, ", base_ids=", base_ids_size, 
              ", nt_symbols=", nt_symbols_size, ", nt_accounts=", nt_accounts_size, 
              ", orig_actions=", orig_nt_actions_size, ", orig_qty=", orig_nt_qty_size);
    }
    
    // Check if all arrays have the same size
    if(actions_size != pos_ids_size || base_ids_size != pos_ids_size ||
       nt_symbols_size != pos_ids_size || nt_accounts_size != pos_ids_size ||
       orig_nt_actions_size != pos_ids_size || orig_nt_qty_size != pos_ids_size) {
        
        integrity_ok = false;
        Print("ARRAY_INTEGRITY_ERROR: Size mismatch detected! Expected all arrays to have size ", pos_ids_size);
        Print("ARRAY_INTEGRITY_ERROR: Actual sizes - actions=", actions_size, 
              ", base_ids=", base_ids_size, ", nt_symbols=", nt_symbols_size, 
              ", nt_accounts=", nt_accounts_size, ", orig_actions=", orig_nt_actions_size, 
              ", orig_qty=", orig_nt_qty_size);
    }
    
    // Enhanced content validation: Check for invalid data in all parallel arrays
    for(int i = 0; i < MathMin(pos_ids_size, actions_size); i++) {
        if(g_open_mt5_actions[i] == "") {
            integrity_ok = false;
            Print("ARRAY_INTEGRITY_ERROR: Empty action at index ", i, " (PosID: ", g_open_mt5_pos_ids[i], ")");
        }
        if(g_open_mt5_base_ids[i] == "") {
            integrity_ok = false;
            Print("ARRAY_INTEGRITY_ERROR: Empty base_id at index ", i, " (PosID: ", g_open_mt5_pos_ids[i], ")");
        }
        if(g_open_mt5_pos_ids[i] <= 0) {
            integrity_ok = false;
            Print("ARRAY_INTEGRITY_ERROR: Invalid position ID at index ", i, " (PosID: ", g_open_mt5_pos_ids[i], ")");
        }
    }
    
    return integrity_ok;
}

//+------------------------------------------------------------------+
//| Clean up old closed base_id tracking entries                    |
//+------------------------------------------------------------------+
void CleanupClosedBaseIdTracking()
{
    datetime current_time = TimeCurrent();
    int cleanup_threshold = 300; // 5 minutes

    for(int i = ArraySize(g_closed_base_ids) - 1; i >= 0; i--)
    {
        if(current_time - g_closed_base_timestamps[i] > cleanup_threshold)
        {
            // Remove old entry
            for(int j = i; j < ArraySize(g_closed_base_ids) - 1; j++)
            {
                g_closed_base_ids[j] = g_closed_base_ids[j + 1];
                g_closed_base_timestamps[j] = g_closed_base_timestamps[j + 1];
            }
            ArrayResize(g_closed_base_ids, ArraySize(g_closed_base_ids) - 1);
            ArrayResize(g_closed_base_timestamps, ArraySize(g_closed_base_timestamps) - 1);

            Print("TRAILING_STOP_IGNORE: Cleaned up old closed base_id tracking entry. Remaining: ", ArraySize(g_closed_base_ids));
        }
    }
}

//+------------------------------------------------------------------+
//| Clean up old notification tracking entries                      |
//+------------------------------------------------------------------+
void CleanupNotificationTracking()
{
    datetime current_time = TimeCurrent();
    int cleanup_threshold = 300; // 5 minutes

    for(int i = ArraySize(g_notified_base_ids) - 1; i >= 0; i--)
    {
        if(current_time - g_notified_timestamps[i] > cleanup_threshold)
        {
            // Remove old entry
            for(int j = i; j < ArraySize(g_notified_base_ids) - 1; j++)
            {
                g_notified_base_ids[j] = g_notified_base_ids[j + 1];
                g_notified_timestamps[j] = g_notified_timestamps[j + 1];
            }
            ArrayResize(g_notified_base_ids, ArraySize(g_notified_base_ids) - 1);
            ArrayResize(g_notified_timestamps, ArraySize(g_notified_timestamps) - 1);

            Print("COMPREHENSIVE_DUPLICATE_PREVENTION: Cleaned up old notification tracking entry. Remaining: ", ArraySize(g_notified_base_ids));
        }
    }
}

//+------------------------------------------------------------------+
//| Clean up completed trade groups                                 |
//+------------------------------------------------------------------+
void CleanupTradeGroups()
{
    Print("ACHM_DIAG: [CleanupTradeGroups] Starting cleanup. Current g_baseIds size: ", ArraySize(g_baseIds));
    int arraySize = ArraySize(g_baseIds);
    if(arraySize == 0) return;  // Nothing to clean up

    int keepCount = 0;
    bool groupsToKeep[]; // Temp array to mark groups to keep
    if(arraySize > 0) ArrayResize(groupsToKeep, arraySize);

    for(int i = 0; i < arraySize; i++)
    {
        bool nt_fills_complete = g_isComplete[i];
        // Ensure index is valid for new arrays before accessing
        bool mt5_hedges_opened_exist = (i < ArraySize(g_mt5HedgesOpenedCount) && g_mt5HedgesOpenedCount[i] > 0);
        bool all_mt5_hedges_closed = (i < ArraySize(g_mt5HedgesClosedCount) && i < ArraySize(g_mt5HedgesOpenedCount) &&
                                      g_mt5HedgesClosedCount[i] >= g_mt5HedgesOpenedCount[i]);

        // Keep if NT not complete, OR if NT is complete but MT5 side is not fully resolved
        if (!nt_fills_complete || (nt_fills_complete && mt5_hedges_opened_exist && !all_mt5_hedges_closed) ) {
            groupsToKeep[i] = true;
            keepCount++;
            Print("ACHM_DIAG: [CleanupTradeGroups] KEEPING group with base_id: '", g_baseIds[i], "' at index ", i,
                  ". NT_Complete: ", nt_fills_complete,
                  ", MT5_Opened_Exist: ", mt5_hedges_opened_exist,
                  ", MT5_All_Closed: ", all_mt5_hedges_closed,
                  ", Opened: ", (i < ArraySize(g_mt5HedgesOpenedCount) ? (string)g_mt5HedgesOpenedCount[i] : "N/A"),
                  ", Closed: ", (i < ArraySize(g_mt5HedgesClosedCount) ? (string)g_mt5HedgesClosedCount[i] : "N/A"));
        } else {
            groupsToKeep[i] = false; // Mark for removal
            Print("ACHM_DIAG: [CleanupTradeGroups] Eligible for REMOVAL group with base_id: '", g_baseIds[i], "' at index ", i,
                  ". NT_Complete: ", nt_fills_complete,
                  ", MT5_Opened_Exist: ", mt5_hedges_opened_exist,
                  ", MT5_All_Closed: ", all_mt5_hedges_closed,
                  ", Opened: ", (i < ArraySize(g_mt5HedgesOpenedCount) ? (string)g_mt5HedgesOpenedCount[i] : "N/A"),
                  ", Closed: ", (i < ArraySize(g_mt5HedgesClosedCount) ? (string)g_mt5HedgesClosedCount[i] : "N/A"));
        }
    }

    if(keepCount < arraySize) // If there are groups to remove
    {
        string tempBaseIds[];
        int tempTotalQty[];
        int tempProcessedQty[];
        string tempActions[];
        bool tempComplete[];
        string tempNtSymbols[];
        string tempNtAccounts[];
        int tempMt5Opened[];
        int tempMt5Closed[];
        bool tempIsMT5Opened[];
        bool tempIsMT5Closed[];

        if(keepCount > 0)
        {
            ArrayResize(tempBaseIds, keepCount);
            ArrayResize(tempTotalQty, keepCount);
            ArrayResize(tempProcessedQty, keepCount);
            ArrayResize(tempActions, keepCount);
            ArrayResize(tempComplete, keepCount);
            ArrayResize(tempNtSymbols, keepCount);
            ArrayResize(tempNtAccounts, keepCount);
            ArrayResize(tempMt5Opened, keepCount);
            ArrayResize(tempMt5Closed, keepCount);
            ArrayResize(tempIsMT5Opened, keepCount);
            ArrayResize(tempIsMT5Closed, keepCount);

            int newIndex = 0;
            for(int i = 0; i < arraySize; i++)
            {
                if(groupsToKeep[i]) // If marked to keep
                {
                    tempBaseIds[newIndex] = g_baseIds[i];
                    tempTotalQty[newIndex] = g_totalQuantities[i];
                    tempProcessedQty[newIndex] = g_processedQuantities[i];
                    tempActions[newIndex] = g_actions[i];
                    tempComplete[newIndex] = g_isComplete[i];
                    if (i < ArraySize(g_ntInstrumentSymbols)) tempNtSymbols[newIndex] = g_ntInstrumentSymbols[i]; else tempNtSymbols[newIndex] = "";
                    if (i < ArraySize(g_ntAccountNames)) tempNtAccounts[newIndex] = g_ntAccountNames[i]; else tempNtAccounts[newIndex] = "";
                    if (i < ArraySize(g_mt5HedgesOpenedCount)) tempMt5Opened[newIndex] = g_mt5HedgesOpenedCount[i]; else tempMt5Opened[newIndex] = 0;
                    if (i < ArraySize(g_mt5HedgesClosedCount)) tempMt5Closed[newIndex] = g_mt5HedgesClosedCount[i]; else tempMt5Closed[newIndex] = 0;
                    if (i < ArraySize(g_isMT5Opened)) tempIsMT5Opened[newIndex] = g_isMT5Opened[i]; else tempIsMT5Opened[newIndex] = false;
                    if (i < ArraySize(g_isMT5Closed)) tempIsMT5Closed[newIndex] = g_isMT5Closed[i]; else tempIsMT5Closed[newIndex] = false;
                    newIndex++;
                }
            }
        }

        ArrayFree(g_baseIds);
        ArrayFree(g_totalQuantities);
        ArrayFree(g_processedQuantities);
        ArrayFree(g_actions);
        ArrayFree(g_isComplete);
        ArrayFree(g_ntInstrumentSymbols);
        ArrayFree(g_ntAccountNames);
        ArrayFree(g_mt5HedgesOpenedCount);
        ArrayFree(g_mt5HedgesClosedCount);
        ArrayFree(g_isMT5Opened);
        ArrayFree(g_isMT5Closed);

        if(keepCount > 0)
        {
            ArrayCopy(g_baseIds, tempBaseIds);
            ArrayCopy(g_totalQuantities, tempTotalQty);
            ArrayCopy(g_processedQuantities, tempProcessedQty);
            ArrayCopy(g_actions, tempActions);
            ArrayCopy(g_isComplete, tempComplete);
            ArrayCopy(g_ntInstrumentSymbols, tempNtSymbols);
            ArrayCopy(g_ntAccountNames, tempNtAccounts);
            ArrayCopy(g_mt5HedgesOpenedCount, tempMt5Opened);
            ArrayCopy(g_mt5HedgesClosedCount, tempMt5Closed);
            ArrayCopy(g_isMT5Opened, tempIsMT5Opened);
            ArrayCopy(g_isMT5Closed, tempIsMT5Closed);
        }
        else // No groups to keep, so resize all to 0
        {
            ArrayResize(g_baseIds, 0);
            ArrayResize(g_totalQuantities, 0);
            ArrayResize(g_processedQuantities, 0);
            ArrayResize(g_actions, 0);
            ArrayResize(g_isComplete, 0);
            ArrayResize(g_ntInstrumentSymbols, 0);
            ArrayResize(g_ntAccountNames, 0);
            ArrayResize(g_mt5HedgesOpenedCount, 0);
            ArrayResize(g_mt5HedgesClosedCount, 0);
            ArrayResize(g_isMT5Opened, 0);
            ArrayResize(g_isMT5Closed, 0);
        }
    } else {
         Print("ACHM_DIAG: [CleanupTradeGroups] No groups eligible for removal based on new criteria. Current count: ", arraySize);
    }
    if(arraySize > 0) ArrayFree(groupsToKeep); // Free the temporary boolean array
}

//+------------------------------------------------------------------+
//| Reset all trade group arrays                                    |
//+------------------------------------------------------------------+
void ResetTradeGroups()
{
    Print("DEBUG: Resetting all trade group arrays");
    ArrayResize(g_baseIds, 0);
    ArrayResize(g_totalQuantities, 0);
    ArrayResize(g_processedQuantities, 0);
    ArrayResize(g_actions, 0);
    ArrayResize(g_isComplete, 0);
    ArrayResize(g_ntInstrumentSymbols, 0);
    ArrayResize(g_ntAccountNames, 0);
    ArrayResize(g_mt5HedgesOpenedCount, 0);
    ArrayResize(g_mt5HedgesClosedCount, 0);
    ArrayResize(g_isMT5Opened, 0);
    ArrayResize(g_isMT5Closed, 0);
    
    Print("DEBUG: All trade group arrays reset to size 0");
}

//+------------------------------------------------------------------+
//| JSON Parsing Helper Functions                                   |
//| Note: Still needed for MQL5↔C# DLL interface                    |
//+------------------------------------------------------------------+
double GetJSONDouble(string json, string key)
{
    string searchKey = "\"" + key + "\"";
    int keyPos = StringFind(json, searchKey);
    if(keyPos == -1)
        return 0.0;
        
    int colonPos = StringFind(json, ":", keyPos);
    if(colonPos == -1)
        return 0.0;
        
    int start = colonPos + 1;
    // Skip whitespace characters
    while(start < StringLen(json))
    {
        ushort ch = StringGetCharacter(json, start);
        if(ch != ' ' && ch != '\t' && ch != '\n' && ch != '\r')
            break;
        start++;
    }
    
    // Build the numeric string
    string numStr = "";
    while(start < StringLen(json))
    {
        ushort ch = StringGetCharacter(json, start);
        if((ch >= '0' && ch <= '9') || ch == '.' || ch == '-')
        {
            numStr += CharToString((uchar)ch);
            start++;
        }
        else
            break;
    }
    
    return StringToDouble(numStr);
}

//+------------------------------------------------------------------+
//| Extract integer value from JSON string                          |
//+------------------------------------------------------------------+
int GetJSONIntValue(string json, string key, int defaultValue)
{
    string searchKey = "\"" + key + "\"";
    int keyPos = StringFind(json, searchKey);
    if(keyPos == -1) {
        return defaultValue;
    }

    // Search for colon *after* the key itself to avoid matching colons in preceding values
    int colonPos = StringFind(json, ":", keyPos + StringLen(searchKey));
    if(colonPos == -1) {
        return defaultValue;
    }

    int start = colonPos + 1;
    // Skip whitespace characters
    while(start < StringLen(json))
    {
        ushort ch = StringGetCharacter(json, start);
        if(ch != ' ' && ch != '\t' && ch != '\n' && ch != '\r')
            break;
        start++;
    }

    if(start >= StringLen(json)) { // Reached end of string while skipping whitespace
        return defaultValue;
    }

    // Build the numeric string
    string numStr = "";
    while(start < StringLen(json))
    {
        ushort ch = StringGetCharacter(json, start);
        if(ch >= '0' && ch <= '9') // Only digits for an integer
        {
            numStr += CharToString((uchar)ch);
            start++;
        }
        else
            break;
    }

    if(numStr == "") {
        return defaultValue; // No digits found after key and colon
    }
    
    int result = (int)StringToInteger(numStr);
    return result;
}

//+------------------------------------------------------------------+
//| Extract string value from JSON                                  |
//+------------------------------------------------------------------+
string GetJSONStringValue(string json_string, string key_with_quotes)
{
    // The key_with_quotes parameter is expected to be like "\"nt_instrument_symbol\""
    // So, we search for key_with_quotes + ":" + "\"" 
    // e.g., "\"nt_instrument_symbol\":\""
    string search_pattern = StringSubstr(key_with_quotes, 1, StringLen(key_with_quotes) - 2); // Remove outer quotes from key_with_quotes
    search_pattern = "\"" + search_pattern + "\":\"";

    int key_pos = StringFind(json_string, search_pattern, 0);
    if(key_pos == -1)
    {
        // Fallback: Try key without quotes around it in the JSON
        string plain_key = StringSubstr(key_with_quotes, 1, StringLen(key_with_quotes) - 2);
        search_pattern = plain_key + ":\""; 
        key_pos = StringFind(json_string, search_pattern, 0);
        if(key_pos == -1) return ""; // Key not found
    }

    int value_start_pos = key_pos + StringLen(search_pattern);
    int value_end_pos = StringFind(json_string, "\"", value_start_pos);

    if(value_end_pos == -1) return ""; // Closing quote not found for the value

    return StringSubstr(json_string, value_start_pos, value_end_pos - value_start_pos);
}

// Duplicate JSON parsing functions removed - using originals at lines 315-331

//+------------------------------------------------------------------+
//| Elastic Hedging Position Management                             |
//+------------------------------------------------------------------+
// Note: ElasticHedgePosition struct and g_elasticPositions array already declared above

//+------------------------------------------------------------------+
//| Process elastic hedge update from NT                            |
//+------------------------------------------------------------------+
void ProcessElasticHedgeUpdate(string baseId, double currentProfit, int profitLevel)
{
    if (!ElasticHedging_Enabled) {
        Print("ELASTIC_HEDGE: Updates disabled in settings");
        return;
    }
    
    // Find the elastic position
    int posIndex = FindElasticPosition(baseId);
    if (posIndex < 0) {
        Print("ELASTIC_HEDGE: No elastic position found for BaseID: ", baseId);
        return;
    }
    
    // Check if enough time has passed since last reduction
    if (TimeCurrent() - g_elasticPositions[posIndex].lastReductionTime < ElasticHedging_MinUpdateInterval) {
        Print("ELASTIC_HEDGE: Update too soon. Last reduction was ", 
              (int)(TimeCurrent() - g_elasticPositions[posIndex].lastReductionTime), " seconds ago");
        return;
    }
    
    // Determine which tier to use based on NT PnL
    bool isHighRiskTier = (g_ntDailyPnL <= ElasticHedging_Tier2_Threshold);
    
    double lotsToClose, maxReductionPercent;
    if (isHighRiskTier) {
        lotsToClose = ElasticHedging_Tier2_LotReduction;
        maxReductionPercent = ElasticHedging_Tier2_MaxReduction;
        Print("ELASTIC_HEDGE: Using Tier 2 (High Risk) settings - NT PnL: $", g_ntDailyPnL);
    } else {
        lotsToClose = ElasticHedging_Tier1_LotReduction;
        maxReductionPercent = ElasticHedging_Tier1_MaxReduction;
        Print("ELASTIC_HEDGE: Using Tier 1 (Standard) settings - NT PnL: $", g_ntDailyPnL);
    }
    
    double maxReduction = g_elasticPositions[posIndex].initialLots * maxReductionPercent;
    
    // Debug logging
    Print("ELASTIC_HEDGE: Position details - Initial: ", g_elasticPositions[posIndex].initialLots,
          ", Remaining: ", g_elasticPositions[posIndex].remainingLots,
          ", Already reduced: ", g_elasticPositions[posIndex].totalLotsReduced);
    Print("ELASTIC_HEDGE: Reduction settings - Per update: ", lotsToClose,
          ", Max allowed: ", maxReduction, " (", (maxReductionPercent*100), "% of initial)");
    
    // Don't exceed max reduction
    if (g_elasticPositions[posIndex].totalLotsReduced + lotsToClose > maxReduction) {
        lotsToClose = maxReduction - g_elasticPositions[posIndex].totalLotsReduced;
        Print("ELASTIC_HEDGE: Adjusting lots to close to stay within max: ", lotsToClose);
    }
    
    double minLot = SymbolInfoDouble(_Symbol, SYMBOL_VOLUME_MIN);
    Print("ELASTIC_HEDGE: Will attempt to close ", lotsToClose, " lots. Min lot: ", minLot);
    
    if (lotsToClose > 0 && lotsToClose >= minLot) {
        // Execute partial close
        if (PartialClosePosition(g_elasticPositions[posIndex].positionTicket, lotsToClose)) {
            g_elasticPositions[posIndex].remainingLots -= lotsToClose;
            g_elasticPositions[posIndex].totalLotsReduced += lotsToClose;
            g_elasticPositions[posIndex].profitLevelsReceived = profitLevel;
            g_elasticPositions[posIndex].lastReductionTime = TimeCurrent();
            
            Print("ELASTIC_HEDGE: Reduced ", lotsToClose, " lots for BaseID: ", baseId,
                  " at NT profit: $", DoubleToString(currentProfit, 2),
                  ". Total reduced: ", g_elasticPositions[posIndex].totalLotsReduced,
                  ", Remaining: ", g_elasticPositions[posIndex].remainingLots);
                  
            // Send notification to Bridge via gRPC
            string update_json = "{";
            update_json += "\"base_id\":\"" + baseId + "\",";
            update_json += "\"current_profit\":" + DoubleToString(currentProfit, 2) + ",";
            update_json += "\"profit_level\":" + IntegerToString(profitLevel);
            update_json += "}";
            
            GrpcSubmitElasticUpdate(update_json);
        }
    } else {
        Print("ELASTIC_HEDGE: Max reduction reached or lots too small for BaseID: ", baseId);
    }
}

//+------------------------------------------------------------------+
//| Process trailing stop update from NT                            |
//+------------------------------------------------------------------+
void ProcessTrailingStopUpdate(string baseId, double newStopPrice, double currentPrice)
{
    // Find corresponding MT5 position
    int posIndex = FindPositionByBaseId(baseId);
    if (posIndex < 0) {
        Print("TRAIL_STOP: No position found for BaseID: ", baseId);
        return;
    }
    
    ulong ticket = g_open_mt5_pos_ids[posIndex];
    if (!PositionSelectByTicket(ticket)) {
        Print("TRAIL_STOP: Failed to select position ticket: ", ticket);
        return;
    }
    
    // Update stop loss to match NT trailing stop
    double currentSL = PositionGetDouble(POSITION_SL);
    ENUM_POSITION_TYPE posType = (ENUM_POSITION_TYPE)PositionGetInteger(POSITION_TYPE);
    bool isLong = (posType == POSITION_TYPE_BUY);
    
    // Only update if new stop is better
    bool shouldUpdate = false;
    if (isLong && (currentSL == 0 || newStopPrice > currentSL)) {
        shouldUpdate = true;
    } else if (!isLong && (currentSL == 0 || newStopPrice < currentSL)) {
        shouldUpdate = true;
    }
    
    if (shouldUpdate) {
        MqlTradeRequest request = {};
        MqlTradeResult result = {};
        
        request.action = TRADE_ACTION_SLTP;
        request.position = ticket;
        request.symbol = _Symbol;
        request.sl = NormalizeDouble(newStopPrice, _Digits);
        request.tp = PositionGetDouble(POSITION_TP); // Keep existing TP
        request.magic = MagicNumber;
        
        if (OrderSend(request, result)) {
            if (result.retcode == TRADE_RETCODE_DONE) {
                Print("TRAIL_STOP: Updated stop for ", baseId, " from ", currentSL, " to ", newStopPrice);
                
                // Send notification to Bridge via gRPC
                string update_json = "{";
                update_json += "\"base_id\":\"" + baseId + "\",";
                update_json += "\"new_stop_price\":" + DoubleToString(newStopPrice, _Digits) + ",";
                update_json += "\"reason\":\"TrailingStop\"";
                update_json += "}";
                
                GrpcSubmitTrailingUpdate(update_json);
            } else {
                Print("TRAIL_STOP: Failed to update stop. Error: ", result.retcode);
            }
        }
    } else {
        Print("TRAIL_STOP: Stop not updated. Current: ", currentSL, ", New: ", newStopPrice);
    }
}

//+------------------------------------------------------------------+
//| Find elastic position by base ID                                |
//+------------------------------------------------------------------+
int FindElasticPosition(string baseId)
{
    for (int i = 0; i < ArraySize(g_elasticPositions); i++) {
        if (g_elasticPositions[i].baseId == baseId) {
            return i;
        }
    }
    return -1;
}

//+------------------------------------------------------------------+
//| Find position index by base ID                                  |
//+------------------------------------------------------------------+
int FindPositionByBaseId(string baseId)
{
    // Search in the open positions arrays
    for (int i = 0; i < ArraySize(g_open_mt5_base_ids); i++) {
        if (g_open_mt5_base_ids[i] == baseId) {
            return i;
        }
    }
    return -1;
}

//+------------------------------------------------------------------+
//| Partial close position function                                 |
//+------------------------------------------------------------------+
bool PartialClosePosition(ulong ticket, double lotsToClose)
{
    if (!PositionSelectByTicket(ticket)) {
        Print("ELASTIC_HEDGE: Failed to select position ", ticket);
        return false;
    }
    
    double currentLots = PositionGetDouble(POSITION_VOLUME);
    if (lotsToClose >= currentLots) {
        Print("ELASTIC_HEDGE: Cannot partial close - would close entire position");
        return false; // Don't close entire position
    }
    
    // Normalize lots
    double minLot = SymbolInfoDouble(_Symbol, SYMBOL_VOLUME_MIN);
    double lotStep = SymbolInfoDouble(_Symbol, SYMBOL_VOLUME_STEP);
    lotsToClose = NormalizeDouble(MathFloor(lotsToClose / lotStep) * lotStep, 2);
    
    if (lotsToClose < minLot) {
        Print("ELASTIC_HEDGE: Lots to close (", lotsToClose, ") is less than minimum (", minLot, ")");
        return false;
    }
    
    // Execute partial close using CTrade
    trade.SetExpertMagicNumber(MagicNumber);
    trade.SetDeviationInPoints(Slippage);
    
    // Use PositionClosePartial which is designed for partial closes
    if (!trade.PositionClosePartial(ticket, lotsToClose)) {
        Print("ELASTIC_HEDGE: PositionClosePartial failed. Error: ", GetLastError(), ", Result comment: ", trade.ResultComment());
        return false;
    }
    
    // Check result
    if (trade.ResultRetcode() != TRADE_RETCODE_DONE) {
        Print("ELASTIC_HEDGE: Partial close failed. Retcode: ", trade.ResultRetcode(), ", Comment: ", trade.ResultComment());
        return false;
    }
    
    Print("ELASTIC_HEDGE: Successfully closed ", lotsToClose, " lots of position ", ticket);
    return true;
}

//+------------------------------------------------------------------+
//| Add position to elastic tracking                                |
//+------------------------------------------------------------------+
void AddElasticPosition(string baseId, ulong positionTicket, double lots)
{
    if (!ElasticHedging_Enabled) return;
    
    // Check if already exists
    int existingIndex = FindElasticPosition(baseId);
    if (existingIndex >= 0) {
        Print("ELASTIC_HEDGE: Position already tracked for BaseID: ", baseId);
        return;
    }
    
    // Add new elastic position
    int newSize = ArraySize(g_elasticPositions) + 1;
    ArrayResize(g_elasticPositions, newSize);
    
    g_elasticPositions[newSize - 1].baseId = baseId;
    g_elasticPositions[newSize - 1].positionTicket = positionTicket;
    g_elasticPositions[newSize - 1].initialLots = lots;
    g_elasticPositions[newSize - 1].remainingLots = lots;
    g_elasticPositions[newSize - 1].profitLevelsReceived = 0;
    g_elasticPositions[newSize - 1].totalLotsReduced = 0;
    g_elasticPositions[newSize - 1].lastReductionTime = 0;
    
    Print("ELASTIC_HEDGE: Added elastic tracking for BaseID: ", baseId, 
          ", Ticket: ", positionTicket, ", Lots: ", lots);
}

//+------------------------------------------------------------------+
//| Notification System for Bridge Communication via gRPC          |
//+------------------------------------------------------------------+

// COMPREHENSIVE DUPLICATE PREVENTION: Track all notifications sent per base_id to prevent duplicates
// Note: g_notified_base_ids and g_notified_timestamps arrays already declared above

//+------------------------------------------------------------------+
//| Add a base_id to the notification tracking list                 |
//+------------------------------------------------------------------+
void AddNotifiedBaseId(string base_id)
{
    int current_size = ArraySize(g_notified_base_ids);
    ArrayResize(g_notified_base_ids, current_size + 1);
    ArrayResize(g_notified_timestamps, current_size + 1);

    g_notified_base_ids[current_size] = base_id;
    g_notified_timestamps[current_size] = TimeCurrent();

    Print("COMPREHENSIVE_DUPLICATE_PREVENTION: Added base_id '", base_id, "' to notification tracking list. Total tracked: ", current_size + 1);
}

//+------------------------------------------------------------------+
//| Check if a base_id has already been notified                   |
//+------------------------------------------------------------------+
bool IsBaseIdAlreadyNotified(string base_id)
{
    for(int i = 0; i < ArraySize(g_notified_base_ids); i++)
    {
        if(g_notified_base_ids[i] == base_id)
        {
            Print("COMPREHENSIVE_DUPLICATE_PREVENTION: Base_id '", base_id, "' found in notification tracking list. Skipping duplicate notification.");
            return true;
        }
    }
    return false;
}

//+------------------------------------------------------------------+
//| Check if notification has been sent for specific event type     |
//+------------------------------------------------------------------+
bool HasNotificationBeenSent(string baseId, string eventType)
{
    // For now, we use the base_id tracking as a general mechanism
    // Can be extended to track specific event types if needed
    return IsBaseIdAlreadyNotified(baseId + "_" + eventType);
}

//+------------------------------------------------------------------+
//| Mark notification as sent for specific event type               |
//+------------------------------------------------------------------+
void MarkNotificationSent(string baseId, string eventType)
{
    AddNotifiedBaseId(baseId + "_" + eventType);
}

//+------------------------------------------------------------------+
//| Send hedge close notification to Bridge via gRPC               |
//+------------------------------------------------------------------+
void SendHedgeCloseNotification(string base_id,
                                string nt_instrument_symbol,
                                string nt_account_name,
                                double closed_hedge_quantity,
                                string closed_hedge_action,
                                datetime timestamp_dt,
                                string closure_reason)
{
    // Check for duplicate notification
    if(IsBaseIdAlreadyNotified(base_id)) {
        Print("SendHedgeCloseNotification: Skipping duplicate notification for base_id: ", base_id);
        return;
    }
    
    // Format timestamp
    string timestamp_str = TimeToString(timestamp_dt, TIME_DATE|TIME_SECONDS) + " GMT";
    
    // Build JSON payload for gRPC notification
    string payload = "{";
    payload += "\"event_type\":\"hedge_close_notification\",";
    payload += "\"base_id\":\"" + base_id + "\",";
    payload += "\"nt_instrument_symbol\":\"" + nt_instrument_symbol + "\",";
    payload += "\"nt_account_name\":\"" + nt_account_name + "\",";
    payload += "\"closed_hedge_quantity\":" + DoubleToString(closed_hedge_quantity, (int)SymbolInfoInteger(_Symbol, SYMBOL_DIGITS)) + ",";
    payload += "\"closed_hedge_action\":\"" + closed_hedge_action + "\",";
    payload += "\"timestamp\":\"" + timestamp_str + "\",";
    payload += "\"closure_reason\":\"" + closure_reason + "\"";
    payload += "}";
    
    // Send notification via gRPC
    int result = GrpcNotifyHedgeClose(payload);
    
    if(result == 0) {
        Print("SendHedgeCloseNotification: Successfully sent notification for base_id: ", base_id);
        AddNotifiedBaseId(base_id); // Track as sent
    } else {
        string error_msg;
        GrpcGetLastError(error_msg, 1024);
        Print("SendHedgeCloseNotification: Failed to send notification for base_id: ", base_id, ". Error: ", result, " - ", error_msg);
    }
}

//+------------------------------------------------------------------+
//| Send elastic hedge update notification to Bridge via gRPC      |
//+------------------------------------------------------------------+
void SendElasticUpdateNotification(string baseId, double currentProfit, int profitLevel)
{
    if(HasNotificationBeenSent(baseId, "elastic_update")) {
        Print("SendElasticUpdateNotification: Skipping duplicate notification for base_id: ", baseId);
        return;
    }
    
    // Find the MT5 position ticket for this BaseID
    ulong mt5Ticket = 0;
    int posIndex = FindPositionByBaseId(baseId);
    if (posIndex >= 0) {
        mt5Ticket = g_open_mt5_pos_ids[posIndex];
    }
    
    string update_json = "{";
    update_json += "\"event_type\":\"elastic_hedge_update\",";
    update_json += "\"base_id\":\"" + baseId + "\",";
    update_json += "\"current_profit\":" + DoubleToString(currentProfit, 2) + ",";
    update_json += "\"profit_level\":" + IntegerToString(profitLevel) + ",";
    update_json += "\"timestamp\":\"" + TimeToString(TimeCurrent(), TIME_DATE|TIME_SECONDS) + " GMT\",";
    update_json += "\"mt5_ticket\":" + IntegerToString(mt5Ticket);
    update_json += "}";
    
    Print("ELASTIC_UPDATE: Sending notification with MT5 ticket: ", mt5Ticket, " for BaseID: ", baseId);
    
    int result = GrpcSubmitElasticUpdate(update_json);
    
    if(result == 0) {
        Print("SendElasticUpdateNotification: Successfully sent elastic update for base_id: ", baseId);
        MarkNotificationSent(baseId, "elastic_update");
    } else {
        string error_msg;
        GrpcGetLastError(error_msg, 1024);
        Print("SendElasticUpdateNotification: Failed to send elastic update for base_id: ", baseId, ". Error: ", result, " - ", error_msg);
    }
}

//+------------------------------------------------------------------+
//| Send trailing stop update notification to Bridge via gRPC      |
//+------------------------------------------------------------------+
void SendTrailingUpdateNotification(string baseId, double newStopPrice, string reason)
{
    if(HasNotificationBeenSent(baseId, "trailing_update")) {
        Print("SendTrailingUpdateNotification: Skipping duplicate notification for base_id: ", baseId);
        return;
    }
    
    // Find the MT5 position ticket for this BaseID
    ulong mt5Ticket = 0;
    int posIndex = FindPositionByBaseId(baseId);
    if (posIndex >= 0) {
        mt5Ticket = g_open_mt5_pos_ids[posIndex];
    }
    
    string update_json = "{";
    update_json += "\"event_type\":\"trailing_stop_update\",";
    update_json += "\"base_id\":\"" + baseId + "\",";
    update_json += "\"new_stop_price\":" + DoubleToString(newStopPrice, _Digits) + ",";
    update_json += "\"reason\":\"" + reason + "\",";
    update_json += "\"timestamp\":\"" + TimeToString(TimeCurrent(), TIME_DATE|TIME_SECONDS) + " GMT\",";
    update_json += "\"mt5_ticket\":" + IntegerToString(mt5Ticket);
    update_json += "}";
    
    Print("TRAILING_UPDATE: Sending notification with MT5 ticket: ", mt5Ticket, " for BaseID: ", baseId);
    
    int result = GrpcSubmitTrailingUpdate(update_json);
    
    if(result == 0) {
        Print("SendTrailingUpdateNotification: Successfully sent trailing update for base_id: ", baseId);
        MarkNotificationSent(baseId, "trailing_update");
    } else {
        string error_msg;
        GrpcGetLastError(error_msg, 1024);
        Print("SendTrailingUpdateNotification: Failed to send trailing update for base_id: ", baseId, ". Error: ", result, " - ", error_msg);
    }
}

//+------------------------------------------------------------------+
//| Helper to extract Base ID from MT5 Position Comment              |
//| Comment format: "AC_HEDGE;BID:{base_id};NTA:..."               |
//+------------------------------------------------------------------+
string ExtractBaseIdFromComment(string comment_str)
{
    string base_id = "";
    if (comment_str == NULL || StringLen(comment_str) == 0) return "";

    string bid_marker = "BID:";
    int bid_marker_len = StringLen(bid_marker);
    int start_pos = StringFind(comment_str, bid_marker, 0);

    if(start_pos != -1)
    {
        int value_start_pos = start_pos + bid_marker_len;
        // Ensure value_start_pos is within bounds of the comment string
        if (value_start_pos < StringLen(comment_str))
        {
            int end_pos = StringFind(comment_str, ";", value_start_pos);
            if(end_pos != -1)
            {
                // Found a semicolon after BID:value
                base_id = StringSubstr(comment_str, value_start_pos, end_pos - value_start_pos);
            }
            else
            {
                // No semicolon after BID:value, take the rest of the string
                // This handles cases where the comment might be truncated after the base_id
                base_id = StringSubstr(comment_str, value_start_pos);
            }
        }
    }

    int id_len = StringLen(base_id);
    // Updated length check for shortened base_ids (16 chars) due to MT5 comment field limitations
    // Log warnings only for comments that appear to be AC_HEDGE related to reduce noise.
    if (StringFind(comment_str, "AC_HEDGE", 0) != -1) { // Check if it's likely one of our comments
        if (id_len > 0 && (id_len < 16 || id_len > 36) && base_id != "TEST_BASE_ID_RECOVERY") { // Allow 16-36 chars for compatibility
             Print("ACHM_PARSE_INFO: ExtractBaseIdFromComment - Extracted base_id '", base_id, "' from '", comment_str, "' has length: ", id_len, " (expected 16 for new format, 32 for legacy)");
        } else if (id_len == 0 && StringFind(comment_str, "BID:", 0) != -1) {
            // If it's an AC_HEDGE comment and contains BID: but we got no base_id, that's a specific failure.
            Print("ACHM_PARSE_FAIL: ExtractBaseIdFromComment - Failed to extract base_id from AC_HEDGE comment containing BID: '", comment_str, "'");
        }
    }
    
    return base_id;
}

//+------------------------------------------------------------------+
//| Perform state recovery for existing positions                   |
//+------------------------------------------------------------------+
void PerformStateRecovery()
{
    Print("ACHM_RECOVERY: Starting state recovery for existing MT5 positions...");
    int total_positions = PositionsTotal();
    int rehydrated_count = 0;
    double recovered_global_futures_adjustment = 0.0;

    for(int i = 0; i < total_positions; i++) {
        ulong mt5_ticket = PositionGetTicket(i);
        if(mt5_ticket == 0) continue;
        if(!PositionSelectByTicket(mt5_ticket)) continue;

        if(PositionGetInteger(POSITION_MAGIC) == MagicNumber && PositionGetString(POSITION_SYMBOL) == _Symbol) {
            string comment = PositionGetString(POSITION_COMMENT);
            long mt5_pos_id = (long)PositionGetInteger(POSITION_IDENTIFIER); // Same as mt5_ticket for MT5 positions
            ENUM_POSITION_TYPE mt5_pos_type = (ENUM_POSITION_TYPE)PositionGetInteger(POSITION_TYPE);
            double mt5_pos_volume = PositionGetDouble(POSITION_VOLUME);

            Print("ACHM_RECOVERY: Checking EA position. Ticket: ", mt5_ticket, ", Comment: '", comment, "', Type: ", EnumToString(mt5_pos_type), ", Vol: ", mt5_pos_volume);

            // Parse comment: "AC_HEDGE;BID:{base_id};NTA:{NT_ACTION};NTQ:{NT_QTY};MTA:{MT5_ACTION}"
            string base_id_str = ExtractBaseIdFromComment(comment);

            if (base_id_str != "") {
                Print("ACHM_RECOVERY: Extracted BaseID='", base_id_str, "' from comment '", comment, "' for PosID ", mt5_pos_id);

                // 1. Re-populate g_map_position_id_to_base_id (Primary Goal)
                if(CheckPointer(g_map_position_id_to_base_id) == POINTER_DYNAMIC) {
                    CString *s_base_id_obj = new CString();
                    s_base_id_obj.Assign(base_id_str);
                    if(!g_map_position_id_to_base_id.Add(mt5_pos_id, s_base_id_obj)) {
                        Print("ACHM_RECOVERY_ERROR: Failed to add to g_map_position_id_to_base_id for PosID ", mt5_pos_id, " with base_id '", base_id_str, "'");
                        delete s_base_id_obj;
                    } else {
                         Print("ACHM_RECOVERY: Re-mapped g_map_position_id_to_base_id: MT5 PosID ", mt5_pos_id, " -> base_id '", base_id_str, "'");
                    }
                }

                // Attempt to parse other parts for more complete rehydration
                string nt_action_str = "";
                int nt_qty_val = 0;
                string mt5_action_str = ""; // This is the MT5 hedge's action (Buy/Sell)

                string parts[];
                int num_parts = StringSplit(comment, ';', parts);

                if (num_parts > 0 && parts[0] == "AC_HEDGE") {
                    // NTA:{NT_ACTION} - Original NT Action (parts[2])
                    if (num_parts > 2 && StringFind(parts[2], "NTA:", 0) == 0) {
                        string nta_part[]; StringSplit(parts[2], ':', nta_part);
                        if(ArraySize(nta_part) == 2) nt_action_str = nta_part[1];
                    }
                    // NTQ:{NT_QTY} - Original NT Quantity (parts[3])
                    if (num_parts > 3 && StringFind(parts[3], "NTQ:", 0) == 0) {
                        string ntq_part[]; StringSplit(parts[3], ':', ntq_part);
                        if(ArraySize(ntq_part) == 2) nt_qty_val = (int)StringToInteger(ntq_part[1]);
                    }
                    // MTA:{MT5_ACTION} - MT5 Action (parts[4])
                    if (num_parts > 4 && StringFind(parts[4], "MTA:", 0) == 0) {
                        string mta_part[]; StringSplit(parts[4], ':', mta_part);
                        if(ArraySize(mta_part) == 2) mt5_action_str = mta_part[1];
                    }
                    Print("ACHM_RECOVERY: Attempted parsing NTA/NTQ/MTA from '", comment, "': NT_Action='", nt_action_str, "', NT_Qty=", nt_qty_val, ", MT5_Action='", mt5_action_str, "'");
                } else {
                    Print("ACHM_RECOVERY_INFO: Comment '", comment, "' for PosID ", mt5_pos_id, " did not start with AC_HEDGE or was too short for full NTA/NTQ/MTA parsing after splitting by ';'. BaseID '", base_id_str, "' was still extracted.");
                }

                // MODIFIED: Proceed with full rehydration if all essential parts were parsed
                if(nt_action_str != "" && nt_qty_val > 0 && mt5_action_str != "") {
                    Print("ACHM_RECOVERY: All parts parsed for full rehydration. Ticket ", mt5_ticket, ": BaseID='", base_id_str, "', NT_Action='", nt_action_str, "', NT_Qty=", nt_qty_val, ", MT5_Action='", mt5_action_str, "'");

                    // 2. Re-create Trade Group Entry
                    int group_idx = -1;
                    // Handle both full match (legacy) and partial match (new format due to MT5 comment length limit)
                    for(int k=0; k < ArraySize(g_baseIds); k++) {
                        bool isMatch = false;
                        if(g_baseIds[k] == base_id_str) {
                            // Full match (legacy format)
                            isMatch = true;
                        } else if(StringLen(g_baseIds[k]) >= 16 && StringLen(base_id_str) >= 16) {
                            // Partial match - compare first 16 characters (new format)
                            string shortStoredBaseId = StringSubstr(g_baseIds[k], 0, 16);
                            string shortBaseId = StringSubstr(base_id_str, 0, 16);
                            if(shortStoredBaseId == shortBaseId) {
                                isMatch = true;
                                Print("ACHM_RECOVERY: Matched using partial base_id. Stored: '", shortStoredBaseId, "' (from full: '", g_baseIds[k], "'), Input: '", shortBaseId, "' (from full: '", base_id_str, "')");
                            }
                        }

                        if(isMatch) {
                            group_idx = k;
                            Print("ACHM_RECOVERY: Found existing (potentially incomplete) trade group for base_id '", base_id_str, "' at index ", group_idx);
                            break;
                        }
                    }
                    if(group_idx == -1) { // Create new if not found
                        group_idx = ArraySize(g_baseIds);
                        ArrayResize(g_baseIds, group_idx + 1);
                        ArrayResize(g_totalQuantities, group_idx + 1);
                        ArrayResize(g_processedQuantities, group_idx + 1);
                        ArrayResize(g_actions, group_idx + 1);
                        ArrayResize(g_isComplete, group_idx + 1);
                        ArrayResize(g_mt5HedgesOpenedCount, group_idx + 1);
                        ArrayResize(g_mt5HedgesClosedCount, group_idx + 1);
                        ArrayResize(g_isMT5Opened, group_idx + 1);
                        ArrayResize(g_isMT5Closed, group_idx + 1);
                        ArrayResize(g_ntInstrumentSymbols, group_idx + 1);
                        ArrayResize(g_ntAccountNames, group_idx + 1);
                        Print("ACHM_RECOVERY: Creating new trade group for rehydrated base_id '", base_id_str, "' at index ", group_idx);
                    }

                    g_baseIds[group_idx] = base_id_str;
                    g_actions[group_idx] = nt_action_str;
                    g_totalQuantities[group_idx] = nt_qty_val;
                    g_processedQuantities[group_idx] = nt_qty_val;
                    g_isComplete[group_idx] = true;
                    
                    g_mt5HedgesOpenedCount[group_idx] = 1;
                    g_mt5HedgesClosedCount[group_idx] = 0;
                    g_isMT5Opened[group_idx] = true;
                    g_isMT5Closed[group_idx] = false;
                    
                    // Set placeholder NT details (will be updated with real data when available)
                    g_ntInstrumentSymbols[group_idx] = "RECOVERED_SYMBOL";
                    g_ntAccountNames[group_idx] = "RECOVERED_ACCOUNT";

                    // 3. Add to parallel tracking arrays
                    int open_mt5_idx = ArraySize(g_open_mt5_pos_ids);
                    ArrayResize(g_open_mt5_pos_ids, open_mt5_idx + 1);
                    ArrayResize(g_open_mt5_base_ids, open_mt5_idx + 1);
                    ArrayResize(g_open_mt5_original_nt_actions, open_mt5_idx + 1);
                    ArrayResize(g_open_mt5_original_nt_quantities, open_mt5_idx + 1);
                    ArrayResize(g_open_mt5_actions, open_mt5_idx + 1);

                    g_open_mt5_pos_ids[open_mt5_idx] = mt5_pos_id;
                    g_open_mt5_base_ids[open_mt5_idx] = base_id_str;
                    g_open_mt5_original_nt_actions[open_mt5_idx] = nt_action_str;
                    g_open_mt5_original_nt_quantities[open_mt5_idx] = nt_qty_val;
                    g_open_mt5_actions[open_mt5_idx] = mt5_action_str; // <<< ADDED FOR MT5 ACTION RECOVERY
                    Print("ACHM_RECOVERY: Added to g_open_mt5_ arrays. PosID:", mt5_pos_id, " BaseID:", base_id_str, " NT_Action:'", nt_action_str, "', NT_Qty:", nt_qty_val, ", MT5_Action:'", mt5_action_str, "'"); // <<< UPDATED LOG

                    // 4. Adjust globalFutures
                    if (mt5_pos_type == POSITION_TYPE_BUY) {
                        recovered_global_futures_adjustment -= nt_qty_val;
                        Print("ACHM_RECOVERY: MT5 BUY hedge (for NT SELL) rehydrated. Adjusting globalFutures by -", nt_qty_val);
                    } else if (mt5_pos_type == POSITION_TYPE_SELL) {
                        recovered_global_futures_adjustment += nt_qty_val;
                        Print("ACHM_RECOVERY: MT5 SELL hedge (for NT BUY) rehydrated. Adjusting globalFutures by +", nt_qty_val);
                    }
                    rehydrated_count++;
                    Print("ACHM_RECOVERY: Successfully rehydrated state for MT5 PositionID ", mt5_pos_id, " (Ticket: ", mt5_ticket, ")");
                } else {
                    // CORRUPTION FIX: Even if full parsing failed, ensure parallel arrays are populated with placeholders
                     Print("ACHM_RECOVERY_WARN: Base_id '", base_id_str, "' extracted, but other parts (NTA/NTQ/MTA) for full rehydration are missing/invalid from comment '", comment, "'. Adding to arrays with placeholder values.");
                     
                     // Use placeholder values for missing data
                     string placeholder_nt_action = (nt_action_str != "") ? nt_action_str : "UNKNOWN_ACTION";
                     int placeholder_nt_qty = (nt_qty_val > 0) ? nt_qty_val : 1;
                     string placeholder_mt5_action = (mt5_action_str != "") ? mt5_action_str : ((mt5_pos_type == POSITION_TYPE_BUY) ? "BUY" : "SELL");
                     
                     // Add to parallel arrays to prevent corruption
                     int open_mt5_idx = ArraySize(g_open_mt5_pos_ids);
                     ArrayResize(g_open_mt5_pos_ids, open_mt5_idx + 1);
                     ArrayResize(g_open_mt5_base_ids, open_mt5_idx + 1);
                     ArrayResize(g_open_mt5_original_nt_actions, open_mt5_idx + 1);
                     ArrayResize(g_open_mt5_original_nt_quantities, open_mt5_idx + 1);
                     ArrayResize(g_open_mt5_actions, open_mt5_idx + 1);
                     
                     g_open_mt5_pos_ids[open_mt5_idx] = mt5_pos_id;
                     g_open_mt5_base_ids[open_mt5_idx] = base_id_str;
                     g_open_mt5_original_nt_actions[open_mt5_idx] = placeholder_nt_action;
                     g_open_mt5_original_nt_quantities[open_mt5_idx] = placeholder_nt_qty;
                     g_open_mt5_actions[open_mt5_idx] = placeholder_mt5_action;
                     
                     Print("ACHM_RECOVERY_PLACEHOLDER: Added position ", mt5_pos_id, " to arrays with placeholders - NT_Action:'", placeholder_nt_action, "', NT_Qty:", placeholder_nt_qty, ", MT5_Action:'", placeholder_mt5_action, "'");
                }
            } else { // base_id_str is empty
                Print("ACHM_RECOVERY_FAIL: Failed to extract a valid base_id from comment '", comment, "' for position ticket ", mt5_ticket, ". Cannot rehydrate this position's state.");
            }
        }
    }
    globalFutures += recovered_global_futures_adjustment; // Apply the total adjustment
    Print("ACHM_RECOVERY: State recovery complete. Rehydrated ", rehydrated_count, " positions. Total adjustment to globalFutures: ", recovered_global_futures_adjustment, ". New globalFutures: ", globalFutures);
}

// Note: Duplicate OnInit function removed - using the one at line 746

//+------------------------------------------------------------------+
//| Open a new hedge order - AC-aware + dynamic elastic hedging     |
//+------------------------------------------------------------------+
bool OpenNewHedgeOrder(string hedgeOrigin, string tradeId, string nt_instrument_symbol, string nt_account_name)
{
    /*----------------------------------------------------------------
     0.  Generic request skeleton
    ----------------------------------------------------------------*/
    MqlTradeRequest request = {};
    MqlTradeResult  result  = {};
    request.action    = TRADE_ACTION_DEAL;
    request.symbol    = _Symbol;
    request.magic     = MagicNumber;
    request.deviation = Slippage;

    /*----------------------------------------------------------------
     1.  Symbol limits
    ----------------------------------------------------------------*/
    const double minLot  = SymbolInfoDouble(_Symbol, SYMBOL_VOLUME_MIN);
    const double maxLot  = SymbolInfoDouble(_Symbol, SYMBOL_VOLUME_MAX);
    const double lotStep = SymbolInfoDouble(_Symbol, SYMBOL_VOLUME_STEP);

    /*----------------------------------------------------------------
     2.  Stop-loss distance (ATR-based)
    ----------------------------------------------------------------*/
    double slDist = GetStopLossDistance();
    if(slDist <= 0)
    {
        Print("ERROR – SL distance not available, aborting order.");
        return false;
    }
    double slPoints = slDist / SymbolInfoDouble(_Symbol, SYMBOL_POINT);

    /*----------------------------------------------------------------
     3.  Volume calculation
    ----------------------------------------------------------------*/
    double volume = DefaultLot;                 // fallback default

    if(LotSizingMode == Asymmetric_Compounding && UseACRiskManagement)
    {
        double equity      = AccountInfoDouble(ACCOUNT_EQUITY);
        double riskAmount  = equity * (currentRisk / 100.0);
        double point       = SymbolInfoDouble(_Symbol, SYMBOL_POINT);
        double tickValue   = SymbolInfoDouble(_Symbol, SYMBOL_TRADE_TICK_VALUE);
        double tickSize    = SymbolInfoDouble(_Symbol, SYMBOL_TRADE_TICK_SIZE);
        double onePointVal = tickValue * (point / tickSize);

        volume = riskAmount / (slPoints * onePointVal);
    }
    else if(LotSizingMode == Elastic_Hedging)
    {
        // Use tier-based calculation for elastic hedging
        double targetProfit;
        bool isHighRiskTier = (g_ntDailyPnL <= -1000.0); // Tier 2 threshold
        
        if (isHighRiskTier) {
            targetProfit = 200.0; // Tier 2 target
        } else {
            targetProfit = 70.0;  // Tier 1 target
        }
        
        double pointsMove = 50.0 * ElasticHedging_NTPointsToMT5; // Convert NT points to MT5 points
        double tickValue = SymbolInfoDouble(_Symbol, SYMBOL_TRADE_TICK_VALUE);
        double tickSize = SymbolInfoDouble(_Symbol, SYMBOL_TRADE_TICK_SIZE);
        double pointSize = SymbolInfoDouble(_Symbol, SYMBOL_POINT);
        double pointValue = (tickValue / tickSize) * pointSize;
        
        if (pointValue > 0) {
            volume = targetProfit / (pointsMove * pointValue);
        } else {
            volume = DefaultLot; // Fallback
        }
    }

    double finalVol = volume;  // Volume already calculated based on selected mode

    // Clamp to limits
    if(finalVol < minLot)  finalVol = minLot;
    if(finalVol > maxLot)  finalVol = maxLot;

    // Round to step
    finalVol = NormalizeDouble(MathFloor(finalVol / lotStep) * lotStep, 8);

    /*----------------------------------------------------------------
     4.  Order type & comment (hedgeOrigin determines Buy vs. Sell)
    ----------------------------------------------------------------*/
    // If EnableHedging is true, OnTimer sets hedgeOrigin to the OPPOSITE of the NT action.
    // If EnableHedging is false (copying), OnTimer sets hedgeOrigin to the SAME as the NT action.
    // Therefore, OpenNewHedgeOrder simply executes the action specified by hedgeOrigin.
    if (hedgeOrigin == "Buy") {
        request.type = ORDER_TYPE_BUY;
    } else if (hedgeOrigin == "Sell") {
        request.type = ORDER_TYPE_SELL;
    } else {
        Print("ERROR: OpenNewHedgeOrder - Invalid hedgeOrigin '", hedgeOrigin, "'. Cannot determine order type.");
        return false;
    }

    // Determine original NT action and quantity for the comment
    string original_nt_action_for_comment = "N/A";
    int original_nt_qty_for_comment = 0;
    int group_idx_for_comment = -1;
    for(int k=0; k < ArraySize(g_baseIds); k++) {
        if(g_baseIds[k] == tradeId) { // tradeId is base_id
            group_idx_for_comment = k;
            break;
        }
    }
    if(group_idx_for_comment != -1) {
        if(group_idx_for_comment < ArraySize(g_actions)) original_nt_action_for_comment = g_actions[group_idx_for_comment];
        if(group_idx_for_comment < ArraySize(g_totalQuantities)) original_nt_qty_for_comment = g_totalQuantities[group_idx_for_comment];
    } else {
        Print("WARN: OpenNewHedgeOrder - Could not find trade group for base_id '", tradeId, "' to create detailed comment. Using N/A.");
    }

    // Format comment: "AC_HEDGE;BID:{short_base_id};NTA:{NT_ACTION};NTQ:{NT_QTY};MTA:{MT5_ACTION}"
    string short_base_id = StringSubstr(tradeId, 0, 16); // Use first 16 chars to fit in MT5 comment limit
    request.comment = StringFormat("AC_HEDGE;BID:%s;NTA:%s;NTQ:%d;MTA:%s",
                                   short_base_id,
                                   original_nt_action_for_comment,
                                   original_nt_qty_for_comment,
                                   hedgeOrigin); // hedgeOrigin is the MT5 action

    request.price   = SymbolInfoDouble(_Symbol,
                   (request.type == ORDER_TYPE_BUY) ? SYMBOL_ASK
                                                     : SYMBOL_BID);

    /*----------------------------------------------------------------
     5.  SL / TP
    ----------------------------------------------------------------*/
    double slPrice = (request.type == ORDER_TYPE_BUY)
                     ? request.price - slDist
                     : request.price + slDist;

    double tpPrice = 0.0;
    if(UseACRiskManagement)
    {
        double rr       = currentReward / currentRisk;     // e.g. 3 : 1
        double tpPoints = slPoints * rr;
        double tpDist   = tpPoints * SymbolInfoDouble(_Symbol, SYMBOL_POINT);
        tpPrice = (request.type == ORDER_TYPE_BUY)
                  ? request.price + tpDist
                  : request.price - tpDist;
    }

    /*----------------------------------------------------------------
     6.  Send via CTrade
    ----------------------------------------------------------------*/
    Print("INFO: OpenNewHedgeOrder: Placing MT5 Order. Determined MT5 Action (from hedgeOrigin param): '", hedgeOrigin, "', Actual MqlTradeRequest.type: ", EnumToString(request.type), ", Comment: '", request.comment, "', Volume: ", finalVol, " for base_id: '", tradeId, "'");
    bool sent = (request.type == ORDER_TYPE_BUY)
                ? trade.Buy (finalVol, _Symbol, request.price,
                             slPrice, tpPrice, request.comment)
                : trade.Sell(finalVol, _Symbol, request.price,
                             slPrice, tpPrice, request.comment);

    if(!sent)
    {
        PrintFormat("ERROR – CTrade %s failed (%d / %s)",
                    (request.type == ORDER_TYPE_BUY ? "Buy" : "Sell"),
                    trade.ResultRetcode(), trade.ResultComment());
        return false;
    }
    
    ulong deal_ticket_for_map = trade.ResultDeal();
    if(sent && deal_ticket_for_map > 0)
    {
        // Increment MT5 hedges opened count for this base_id's group
        int groupIdxOpen = -1;
        for(int i = 0; i < ArraySize(g_baseIds); i++) {
            if(g_baseIds[i] == tradeId) {
                groupIdxOpen = i;
                break;
            }
        }
        if(groupIdxOpen != -1 && groupIdxOpen < ArraySize(g_mt5HedgesOpenedCount)) {
            g_mt5HedgesOpenedCount[groupIdxOpen]++;
            Print("ACHM_DIAG: [OpenNewHedgeOrder] Incremented g_mt5HedgesOpenedCount for base_id '", tradeId, "' (index ", groupIdxOpen, ") to ", g_mt5HedgesOpenedCount[groupIdxOpen]);
        }
    }

    PrintFormat("INFO  – hedge %s %.2f lots  SL %.1f  TP %.1f  deal %I64u",
                (request.type == ORDER_TYPE_BUY ? "BUY" : "SELL"),
                finalVol, slPrice, tpPrice, deal_ticket_for_map);

    if(deal_ticket_for_map > 0)
    {
        if(HistoryDealSelect(deal_ticket_for_map))
        {
            ulong new_mt5_position_id = HistoryDealGetInteger(deal_ticket_for_map, DEAL_POSITION_ID);
            if(new_mt5_position_id > 0)
            {
                // Store details in parallel arrays
                if(!ValidateArrayIntegrity()) {
                    PrintFormat("CRITICAL_ARRAY_ERROR: Array integrity check failed BEFORE adding new position. Aborting position addition.");
                    return false;
                }
                
                int current_array_size = ArraySize(g_open_mt5_pos_ids);
                PrintFormat("ARRAY_ADD: Adding new position at index %d. Current array size: %d", current_array_size, current_array_size);
                
                // Perform atomic array resizing
                ArrayResize(g_open_mt5_pos_ids, current_array_size + 1);
                ArrayResize(g_open_mt5_base_ids, current_array_size + 1);
                ArrayResize(g_open_mt5_nt_symbols, current_array_size + 1);
                ArrayResize(g_open_mt5_nt_accounts, current_array_size + 1);
                ArrayResize(g_open_mt5_actions, current_array_size + 1);
                ArrayResize(g_open_mt5_original_nt_actions, current_array_size + 1);
                ArrayResize(g_open_mt5_original_nt_quantities, current_array_size + 1);
                
                // Add position data
                g_open_mt5_pos_ids[current_array_size] = (long)new_mt5_position_id;
                g_open_mt5_base_ids[current_array_size] = tradeId;
                g_open_mt5_nt_symbols[current_array_size] = nt_instrument_symbol;
                g_open_mt5_nt_accounts[current_array_size] = nt_account_name;
                g_open_mt5_actions[current_array_size] = hedgeOrigin;
                
                // Get original NT details for new arrays
                string original_nt_action_for_open_mt5 = "";
                int original_nt_qty_for_open_mt5 = 0;
                if(group_idx_for_comment != -1) {
                    if(group_idx_for_comment < ArraySize(g_actions)) original_nt_action_for_open_mt5 = g_actions[group_idx_for_comment];
                    if(group_idx_for_comment < ArraySize(g_totalQuantities)) original_nt_qty_for_open_mt5 = g_totalQuantities[group_idx_for_comment];
                }
                
                // Validate data and use placeholders if invalid
                if(original_nt_action_for_open_mt5 == "") {
                    Print("CRITICAL: OpenNewHedgeOrder - Trade group found but NT action is empty for base_id '", tradeId, "'. Using placeholder.");
                    original_nt_action_for_open_mt5 = "EMPTY_GROUP_ACTION";
                }
                if(original_nt_qty_for_open_mt5 <= 0) {
                    Print("CRITICAL: OpenNewHedgeOrder - Trade group found but NT quantity is invalid for base_id '", tradeId, "'. Using placeholder.");
                    original_nt_qty_for_open_mt5 = 1;
                }
                
                g_open_mt5_original_nt_actions[current_array_size] = original_nt_action_for_open_mt5;
                g_open_mt5_original_nt_quantities[current_array_size] = original_nt_qty_for_open_mt5;
                
                Print("DEBUG: Stored details in parallel arrays for PosID ", (long)new_mt5_position_id, " at index ", current_array_size,
                      ". BaseID: ", tradeId, ", NT Symbol: ", nt_instrument_symbol, ", NT Account: ", nt_account_name,
                      ", MT5 Action: ", hedgeOrigin, ", Orig NT Action: ", original_nt_action_for_open_mt5, ", Orig NT Qty: ", original_nt_qty_for_open_mt5);
                
                // Store in hashmap
                if(CheckPointer(g_map_position_id_to_base_id) == POINTER_DYNAMIC) {
                    CString *s_base_id_obj = new CString();
                    if(CheckPointer(s_base_id_obj) != POINTER_INVALID) {
                        s_base_id_obj.Assign(tradeId);
                        if(!g_map_position_id_to_base_id.Add((long)new_mt5_position_id, s_base_id_obj)) {
                            Print("ERROR: OpenNewHedgeOrder - Failed to Add base_id '", tradeId, "' to g_map_position_id_to_base_id for PositionID ", new_mt5_position_id, ". Deleting CString.");
                            delete s_base_id_obj;
                        } else {
                            Print("DEBUG_HEDGE_CLOSURE: Stored mapping for MT5 PosID ", (long)new_mt5_position_id, " to base_id '", s_base_id_obj.Str(), "' in g_map_position_id_to_base_id.");
                        }
                    }
                }
                
                // Add to elastic hedging tracking if enabled
                if (ElasticHedging_Enabled && LotSizingMode == Elastic_Hedging) {
                    AddElasticPosition(tradeId, new_mt5_position_id, finalVol);
                }
                
                // Final validation after position addition
                if(!ValidateArrayIntegrity()) {
                    PrintFormat("CRITICAL_ARRAY_ERROR: Array integrity check failed AFTER adding new position at index %d", current_array_size);
                    return false;
                } else {
                    PrintFormat("ARRAY_ADD_SUCCESS: Position added successfully at index %d. All arrays remain synchronized.", current_array_size);
                }
            }
        }
    }

    SubmitTradeResult("success", deal_ticket_for_map, finalVol, false, tradeId);
    return true;
}

//+------------------------------------------------------------------+
//| Close one hedge position                                         |
//+------------------------------------------------------------------+
bool CloseOneHedgePosition(string hedgeOrigin, string specificTradeId = "")
{
    long ticket_to_close_long = 0;

    if (specificTradeId != "") {
        // If specificTradeId is provided, prioritize finding by it + origin
        int total = PositionsTotal();
        for(int i = 0; i < total; i++) {
            ulong current_ticket_ulong = PositionGetTicket(i);
            if(current_ticket_ulong == 0) continue;
            if(!PositionSelectByTicket(current_ticket_ulong)) continue;

            if(PositionGetString(POSITION_SYMBOL)   != _Symbol)     continue;
            if(PositionGetInteger(POSITION_MAGIC)   != MagicNumber) continue;
            
            string comment = PositionGetString(POSITION_COMMENT);
            string originSearchStr = CommentPrefix + hedgeOrigin;

            if (StringFind(comment, originSearchStr) != -1 && StringFind(comment, specificTradeId) != -1) {
                ticket_to_close_long = (long)current_ticket_ulong;
                Print("DEBUG: CloseOneHedgePosition - Found specific ticket ", ticket_to_close_long, " matching ID ", specificTradeId, " and origin ", hedgeOrigin);
                break;
            }
        }
        if (ticket_to_close_long == 0) {
            Print("DEBUG: CloseOneHedgePosition - No ticket found matching specificTradeId '", specificTradeId, "' and origin '", hedgeOrigin, "'");
            return false;
        }
    } else {
        // If no specificTradeId, find by origin only
        ticket_to_close_long = FindOldestHedgeToCloseTicket(hedgeOrigin);
        if (ticket_to_close_long == 0) {
            Print("DEBUG: CloseOneHedgePosition - No ticket found by origin '", hedgeOrigin, "' (no specificTradeId provided).");
            return false;
        }
    }
    
    ulong ticket_to_close = (ulong)ticket_to_close_long;
    
    // Select again to be sure
    if(!PositionSelectByTicket(ticket_to_close)) {
        Print("ERROR: CloseOneHedgePosition - Failed to select ticket ", (long)ticket_to_close, " before closing.");
        return false;
    }
    
    double volumeToClose = PositionGetDouble(POSITION_VOLUME);
    string originalComment = PositionGetString(POSITION_COMMENT);

    Print(StringFormat(
          "DEBUG: Closing hedge position via CTrade (CloseOneHedgePosition) – Ticket:%I64u  Vol:%.2f  Comment:%s",
          ticket_to_close, volumeToClose, originalComment));

    bool closed = trade.PositionClose(ticket_to_close, Slippage);

    if(closed)
    {
        Print("DEBUG: PositionClose succeeded (via CloseOneHedgePosition). Order:", trade.ResultOrder(),
              "  Deal:", trade.ResultDeal());

        string closedTradeId = "";
        // Extract trade-id from comment
        int originMarkerEnd = StringFind(originalComment, hedgeOrigin);
        if(originMarkerEnd != -1) originMarkerEnd += StringLen(hedgeOrigin);
        
        int idStart = -1;
        if(originMarkerEnd != -1 && originMarkerEnd < StringLen(originalComment)) {
            idStart = StringFind(originalComment, "_", originMarkerEnd) + 1;
        }

        if(idStart > 0 && idStart < StringLen(originalComment)) {
            closedTradeId = StringSubstr(originalComment, idStart);
        }

        if(UseACRiskManagement)
        {
            double closeProfit = 0;
            if(trade.ResultDeal() > 0) closeProfit = HistoryDealGetDouble(trade.ResultDeal(), DEAL_PROFIT);
            ProcessTradeResult(closeProfit > 0, closedTradeId, closeProfit);
        }

        SubmitTradeResult("success", trade.ResultOrder(), volumeToClose, true, closedTradeId);
        return true;
    }
    else
    {
        Print(StringFormat("ERROR: PositionClose failed (via CloseOneHedgePosition) for ticket %I64u – %d / %s",
              ticket_to_close, trade.ResultRetcode(), trade.ResultComment()));
        return false;
    }
}

//+------------------------------------------------------------------+
//| Count hedge positions for a specific base_id and MT5 action     |
//+------------------------------------------------------------------+
int CountHedgePositionsForBaseId(string baseIdToCount, string mt5HedgeAction)
{
    int count = 0;
    string specificCommentSearch = StringFormat("%s%s_%s", CommentPrefix, mt5HedgeAction, baseIdToCount);

    int total = PositionsTotal();
    for(int i = 0; i < total; i++)
    {
        ulong ticket = PositionGetTicket(i);
        if(ticket == 0) continue;
        if(!PositionSelectByTicket(ticket)) continue;

        if(PositionGetString(POSITION_SYMBOL)   != _Symbol)     continue;
        if(PositionGetInteger(POSITION_MAGIC)   != MagicNumber) continue;

        string comment = PositionGetString(POSITION_COMMENT);
        
        // Check if comment contains our search pattern
        if(StringFind(comment, specificCommentSearch) >= 0) {
            count++;
            Print("DEBUG: CountHedgePositionsForBaseId - Found matching position: Ticket=", ticket, ", Comment='", comment, "'");
        }
    }

    Print("DEBUG: CountHedgePositionsForBaseId - Found ", count, " positions for baseId='", baseIdToCount, "', action='", mt5HedgeAction, "'");
    return count;
}

//+------------------------------------------------------------------+
//| Close hedge positions for a specific base_id                    |
//+------------------------------------------------------------------+
bool CloseHedgePositionsForBaseId(string baseId, string reason = "NT_CLOSE_REQUEST")
{
    int closedCount = 0;
    int total = PositionsTotal();
    
    Print("ACHM_NT_CLOSURE: [CloseHedgePositionsForBaseId] Starting closure for base_id: '", baseId, "', reason: '", reason, "'. Total positions to check: ", total);

    for(int i = total - 1; i >= 0; i--) // Loop backwards to avoid index issues when closing
    {
        ulong ticket = PositionGetTicket(i);
        if(ticket == 0) continue;
        if(!PositionSelectByTicket(ticket)) continue;

        if(PositionGetString(POSITION_SYMBOL) != _Symbol) continue;
        if(PositionGetInteger(POSITION_MAGIC) != MagicNumber) continue;

        string posComment = PositionGetString(POSITION_COMMENT);
        string posSymbol = PositionGetString(POSITION_SYMBOL);
        double posVolume = PositionGetDouble(POSITION_VOLUME);
        ENUM_POSITION_TYPE posType = (ENUM_POSITION_TYPE)PositionGetInteger(POSITION_TYPE);

        // Extract base_id from position comment
        string extractedBaseId = ExtractBaseIdFromComment(posComment);

        // Check if this position matches the base_id we want to close
        bool isMatch = false;
        if(extractedBaseId == baseId) {
            // Full match (legacy format)
            isMatch = true;
        } else if(StringLen(extractedBaseId) >= 16 && StringLen(baseId) >= 16) {
            // Partial match - compare first 16 characters (new format)
            string shortBaseId = StringSubstr(baseId, 0, 16);
            if(extractedBaseId == shortBaseId) {
                isMatch = true;
                Print("ACHM_NT_CLOSURE: [CloseHedgePositionsForBaseId] Matched using partial base_id. Extracted: '", extractedBaseId, "', Target: '", shortBaseId, "' (from full: '", baseId, "')");
            }
        }
        
        // If comment-based matching failed, check the hashmap for full base_id
        if(!isMatch) {
            CString* positionBaseIdPtr = NULL;
            if(g_map_position_id_to_base_id.TryGetValue((long)ticket, positionBaseIdPtr) && positionBaseIdPtr != NULL) {
                string fullBaseIdFromMap = positionBaseIdPtr.Str();
                if(fullBaseIdFromMap == baseId) {
                    isMatch = true;
                    Print("ACHM_NT_CLOSURE: [CloseHedgePositionsForBaseId] Matched using hashmap lookup. Position: ", ticket, ", Full BaseID: '", fullBaseIdFromMap, "'");
                }
            }
        }

        if(isMatch)
        {
            Print("ACHM_NT_CLOSURE: [CloseHedgePositionsForBaseId] Closing position: Ticket=", ticket, ", Volume=", posVolume, ", Type=", EnumToString(posType), ", Comment='", posComment, "'");
            
            bool closed = trade.PositionClose(ticket, Slippage);
            if(closed) {
                closedCount++;
                Print("ACHM_NT_CLOSURE: [CloseHedgePositionsForBaseId] Successfully closed position: ", ticket);
                
                // Submit result
                SubmitTradeResult("success", trade.ResultOrder(), posVolume, true, baseId);
            } else {
                Print("ACHM_NT_CLOSURE: [CloseHedgePositionsForBaseId] Failed to close position: ", ticket, ". Error: ", trade.ResultRetcode(), " - ", trade.ResultComment());
            }
        }
    }

    Print("ACHM_NT_CLOSURE: [CloseHedgePositionsForBaseId] Completed closure for base_id: '", baseId, "'. Closed ", closedCount, " positions.");
    return (closedCount > 0);
}

//+------------------------------------------------------------------+
//| Find oldest hedge position ticket to close                      |
//+------------------------------------------------------------------+
long FindOldestHedgeToCloseTicket(string hedgeOrigin)
{
    ulong oldestTicket = 0;
    datetime oldestTime = LONG_MAX;
    
    int total = PositionsTotal();
    for(int i = 0; i < total; i++)
    {
        ulong ticket = PositionGetTicket(i);
        if(ticket == 0) continue;
        if(!PositionSelectByTicket(ticket)) continue;

        if(PositionGetString(POSITION_SYMBOL) != _Symbol) continue;
        if(PositionGetInteger(POSITION_MAGIC) != MagicNumber) continue;

        string comment = PositionGetString(POSITION_COMMENT);
        string searchStr = CommentPrefix + hedgeOrigin;
        
        if(StringFind(comment, searchStr) >= 0) {
            datetime posTime = (datetime)PositionGetInteger(POSITION_TIME);
            if(posTime < oldestTime) {
                oldestTime = posTime;
                oldestTicket = ticket;
            }
        }
    }

    return (long)oldestTicket;
}

//+------------------------------------------------------------------+
//| Process trade result for AC risk management                     |
//+------------------------------------------------------------------+
void ProcessTradeResult(bool isWin, string tradeId, double profit = 0.0)
{
    if(UseACRiskManagement)
    {
        Print("DEBUG: ProcessTradeResult - IsWin: ", isWin, ", TradeId: ", tradeId, ", Profit: ", profit);
        UpdateRiskBasedOnResult(isWin, MagicNumber);
        Print("DEBUG: Updated asymmetrical compounding after trade result. New risk: ", 
              currentRisk, "%, Consecutive wins: ", consecutiveWins);
    }
}

//+------------------------------------------------------------------+
//| Stub implementations for missing functions                      |
//+------------------------------------------------------------------+
void InitATRTrailing()
{
    // Stub implementation
    Print("ATR Trailing initialized");
}

void HandleATRTrailingForPosition(ulong ticket, double entryPrice, double currentPrice, string orderType, double volume)
{
    // Stub implementation - no trailing for now
}

double CalculateACLotSize(double ntQuantity)
{
    // Stub implementation - return default lot
    return DefaultLot;
}

double CalculateElasticLotSize(double ntQuantity)
{
    // Stub implementation - return default lot
    return DefaultLot;
}

// AddElasticPosition function is declared at line 71 - implementation removed to fix duplicate definition error