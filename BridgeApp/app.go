package main

import (
	"context"
	"encoding/json"
	"fmt"
	"log"
	"os"
	"reflect"
	"strconv"
	"strings"
	"sync"
	"time"

	grpcserver "BridgeApp/internal/grpc"
	blog "BridgeApp/internal/logging"
)

// App struct
type App struct {
	ctx                  context.Context
	tradeQueue           chan Trade
	queueMux             sync.Mutex
	netPosition          int
	hedgeLot             float64
	bridgeActive         bool
	platformConnected    bool
	tradeHistory         []Trade
	eaActive             bool
	tradeLogSenderActive bool

	// gRPC server integration
	grpcServer *grpcserver.Server
	grpcPort   string

	// Addon connection tracking
	lastAddonRequestTime time.Time
	addonStatusMux       sync.Mutex
	// HedgeBot connection tracking
	eaStatusMux sync.Mutex // Protects eaActive and eaLastPing
	eaLastPing  time.Time  // Timestamp of the last successful ping from Hedgebot

	// MT5 ticket to BaseID mapping
	// CRITICAL: BaseID is always Quantower Position.Id - the authoritative correlation key
	mt5TicketMux       sync.RWMutex
	mt5TicketToBaseId  map[uint64]string          // MT5 ticket -> BaseID (Quantower Position.Id)
	baseIdToTickets    map[string][]uint64        // BaseID (Quantower Position.Id) -> all MT5 hedge tickets
	pendingCloseByBase map[string][]pendingTicket // BaseID (Quantower Position.Id) -> tickets actively being closed

	// Metadata to aid resolution when BaseID mismatches occur
	baseIdToInstrument map[string]string // BaseID (Quantower Position.Id) -> instrument symbol
	baseIdToAccount    map[string]string // BaseID (Quantower Position.Id) -> account name
	// Track client-initiated close requests by MT5 ticket to tag subsequent MT5 close results as acks
	clientCloseMux         sync.Mutex
	clientInitiatedTickets map[uint64]time.Time // ticket -> time marked

	// Cache sizing hints per BaseID so elastic events can carry them
	baseIdToElastic map[string]elasticInfo // BaseID -> elastic sizing and context

	// Recent elastic-close context to align/suppress subsequent generic MT5TradeResult closes
	elasticMux            sync.Mutex
	recentElasticByBase   map[string]elasticMark // BaseID -> last elastic close marker
	recentElasticByTicket map[uint64]elasticMark // MT5 ticket -> last elastic close marker

	// Sequence counter for unique elastic event IDs
	elasticSeqMux   sync.Mutex
	elasticSeqCount uint64
}

type elasticInfo struct {
	PointsPer1kLoss float64
	Instrument      string
	Account         string
}

// elasticMark captures an elastic close signal context for short-lived correlation
type elasticMark struct {
	reason string
	when   time.Time
	ticket uint64
	qty    float64
}

type pendingTicket struct {
	ticket uint64
	marked time.Time
}

// markElasticClose records an elastic close marker for correlation windows
func (a *App) markElasticClose(baseID string, ticket uint64, reason string, qty float64) {
	if strings.TrimSpace(baseID) == "" || !strings.HasPrefix(strings.ToLower(reason), "elastic_") {
		return
	}
	a.elasticMux.Lock()
	if a.recentElasticByBase == nil {
		a.recentElasticByBase = make(map[string]elasticMark)
	}
	if a.recentElasticByTicket == nil {
		a.recentElasticByTicket = make(map[uint64]elasticMark)
	}
	mk := elasticMark{reason: reason, when: time.Now(), ticket: ticket, qty: qty}
	a.recentElasticByBase[baseID] = mk
	if ticket != 0 {
		a.recentElasticByTicket[ticket] = mk
	}
	a.elasticMux.Unlock()
	log.Printf("gRPC: Marked recent elastic close context: base_id=%s ticket=%d reason=%s qty=%.4f", baseID, ticket, reason, qty)
}

// recentElasticFor returns an elastic marker if found for base or ticket within a TTL
func (a *App) recentElasticFor(baseID string, ticket uint64, within time.Duration) (elasticMark, bool) {
	a.elasticMux.Lock()
	defer a.elasticMux.Unlock()
	var mk elasticMark
	var ok bool
	// Prefer ticket match when available
	if ticket != 0 {
		if v, exists := a.recentElasticByTicket[ticket]; exists && time.Since(v.when) <= within {
			return v, true
		}
	}
	if v, exists := a.recentElasticByBase[baseID]; exists && time.Since(v.when) <= within {
		mk, ok = v, true
	}
	// Garbage collect stale entries occasionally
	if len(a.recentElasticByBase) > 0 || len(a.recentElasticByTicket) > 0 {
		cutoff := time.Now().Add(-within)
		for k, v := range a.recentElasticByBase {
			if v.when.Before(cutoff) {
				delete(a.recentElasticByBase, k)
			}
		}
		for k, v := range a.recentElasticByTicket {
			if v.when.Before(cutoff) {
				delete(a.recentElasticByTicket, k)
			}
		}
	}
	return mk, ok
}

// bestInstAcctFor returns the best-known instrument and account for a given BaseID.
func (a *App) bestInstAcctFor(baseID string) (string, string) {
	a.mt5TicketMux.RLock()
	defer a.mt5TicketMux.RUnlock()

	inst := strings.TrimSpace(a.baseIdToInstrument[baseID])
	acct := strings.TrimSpace(a.baseIdToAccount[baseID])
	return inst, acct
}

type Trade struct {
	ID              string    `json:"id"`      // Unique trade identifier
	BaseID          string    `json:"base_id"` // Base ID for multi-contract trades
	Time            time.Time `json:"time"`    // Timestamp of trade
	Action          string    `json:"action"`  // "buy" or "sell"
	Quantity        float64   `json:"quantity"`
	Price           float64   `json:"price"`
	TotalQuantity   int       `json:"total_quantity"` // Total contracts in this trade group
	ContractNum     int       `json:"contract_num"`   // Which contract this is (1-based)
	OrderType       string    `json:"order_type"`     // "ENTRY", "TP", "SL", "NT_CLOSE"
	MeasurementPips int       `json:"measurement_pips"`
	RawMeasurement  float64   `json:"raw_measurement"`
	Instrument      string    `json:"instrument"`
	AccountName     string    `json:"account_name"`

	// Enhanced NT Performance Data (omit when not provided to avoid resetting EA state)
	NTBalance       float64 `json:"nt_balance,omitempty"`
	NTDailyPnL      float64 `json:"nt_daily_pnl,omitempty"`
	NTTradeResult   string  `json:"nt_trade_result,omitempty"` // "win", "loss", "pending"
	NTSessionTrades int     `json:"nt_session_trades,omitempty"`

	// MT5 position tracking
	MT5Ticket         uint64  `json:"mt5_ticket"`                      // MT5 position ticket number (always include, even if 0)
	NTPointsPer1kLoss float64 `json:"nt_points_per_1k_loss,omitempty"` // Elastic sizing hint

	// Optional event enrichment (forwarded verbatim to MT5 stream JSON)
	EventType            string  `json:"event_type,omitempty"`
	ElasticCurrentProfit float64 `json:"elastic_current_profit,omitempty"`
	ElasticProfitLevel   int32   `json:"elastic_profit_level,omitempty"`

	// Quantower identifiers (optional during transition)
	QTTradeID    string `json:"qt_trade_id,omitempty"`
	QTPositionID string `json:"qt_position_id,omitempty"`
	StrategyTag  string `json:"strategy_tag,omitempty"`
	Origin       string `json:"origin_platform,omitempty"`
}

func normalizeTrade(t *Trade) {
	t.ID = strings.TrimSpace(t.ID)
	t.BaseID = strings.TrimSpace(t.BaseID)
	t.QTTradeID = strings.TrimSpace(t.QTTradeID)
	t.QTPositionID = strings.TrimSpace(t.QTPositionID)
	t.StrategyTag = strings.TrimSpace(t.StrategyTag)
	t.Origin = strings.TrimSpace(t.Origin)

	if t.Origin == "" && t.QTTradeID != "" {
		t.Origin = "quantower"
	}

	if t.QTTradeID != "" {
		t.ID = t.QTTradeID
	} else if t.ID == "" && t.BaseID != "" {
		t.ID = t.BaseID
	}
}

func (a *App) popTicket(baseID string) (uint64, bool) {
	if strings.TrimSpace(baseID) == "" {
		return 0, false
	}
	a.mt5TicketMux.Lock()
	defer a.mt5TicketMux.Unlock()

	list := a.baseIdToTickets[baseID]
	if len(list) == 0 {
		return 0, false
	}
	var ticket uint64
	if len(list) == 1 {
		ticket = list[0]
		delete(a.baseIdToTickets, baseID)
	} else {
		ticket = list[0]
		a.baseIdToTickets[baseID] = list[1:]
	}
	return ticket, true
}

func (a *App) popTicketWithWait(baseID string, maxWait, poll time.Duration) (uint64, bool) {
	deadline := time.Now().Add(maxWait)
	for {
		ticket, ok := a.popTicket(baseID)
		if ok {
			return ticket, true
		}
		if time.Now().After(deadline) {
			return 0, false
		}
		time.Sleep(poll)
	}
}

func (a *App) pushTicket(baseID string, ticket uint64) {
	if strings.TrimSpace(baseID) == "" || ticket == 0 {
		return
	}
	a.mt5TicketMux.Lock()
	defer a.mt5TicketMux.Unlock()

	current := a.baseIdToTickets[baseID]
	updated := append([]uint64{ticket}, current...)
	a.baseIdToTickets[baseID] = updated
}

func (a *App) removeTicketFromPool(baseID string, ticket uint64) {
	if ticket == 0 {
		return
	}
	a.mt5TicketMux.Lock()
	defer a.mt5TicketMux.Unlock()

	trimmedBase := strings.TrimSpace(baseID)
	if trimmedBase == "" {
		if mapped, ok := a.mt5TicketToBaseId[ticket]; ok {
			trimmedBase = mapped
		}
	}
	if trimmedBase != "" {
		if list, ok := a.baseIdToTickets[trimmedBase]; ok {
			filtered := make([]uint64, 0, len(list))
			for _, v := range list {
				if v != ticket {
					filtered = append(filtered, v)
				}
			}
			if len(filtered) == 0 {
				delete(a.baseIdToTickets, trimmedBase)
			} else {
				a.baseIdToTickets[trimmedBase] = filtered
			}
		}
		if entries, ok := a.pendingCloseByBase[trimmedBase]; ok {
			filtered := make([]pendingTicket, 0, len(entries))
			for _, entry := range entries {
				if entry.ticket != ticket {
					filtered = append(filtered, entry)
				}
			}
			if len(filtered) == 0 {
				delete(a.pendingCloseByBase, trimmedBase)
			} else {
				a.pendingCloseByBase[trimmedBase] = filtered
			}
		}
	}
	delete(a.mt5TicketToBaseId, ticket)
}

const pendingCloseTTL = 15 * time.Second

func (a *App) evictTicketFromQueue(baseID string, ticket uint64) bool {
	if strings.TrimSpace(baseID) == "" || ticket == 0 {
		return false
	}
	a.mt5TicketMux.Lock()
	defer a.mt5TicketMux.Unlock()

	list := a.baseIdToTickets[baseID]
	if len(list) == 0 {
		return false
	}
	filtered := make([]uint64, 0, len(list))
	removed := false
	for _, v := range list {
		if !removed && v == ticket {
			removed = true
			continue
		}
		filtered = append(filtered, v)
	}
	if !removed {
		return false
	}
	if len(filtered) == 0 {
		delete(a.baseIdToTickets, baseID)
	} else {
		a.baseIdToTickets[baseID] = filtered
	}
	return true
}

func (a *App) trackPendingTicket(baseID string, ticket uint64) {
	if strings.TrimSpace(baseID) == "" || ticket == 0 {
		return
	}
	a.mt5TicketMux.Lock()
	defer a.mt5TicketMux.Unlock()

	if a.pendingCloseByBase == nil {
		a.pendingCloseByBase = make(map[string][]pendingTicket)
	}
	entries := a.pendingCloseByBase[baseID]
	now := time.Now()
	updated := false
	for i := range entries {
		if entries[i].ticket == ticket {
			entries[i].marked = now
			updated = true
			break
		}
	}
	if !updated {
		entries = append(entries, pendingTicket{ticket: ticket, marked: now})
	}
	a.pendingCloseByBase[baseID] = entries
	if _, exists := a.mt5TicketToBaseId[ticket]; !exists {
		a.mt5TicketToBaseId[ticket] = baseID
	}
}

func (a *App) hasRecentPendingClose(baseID string, within time.Duration) bool {
	if strings.TrimSpace(baseID) == "" {
		return false
	}
	a.mt5TicketMux.Lock()
	defer a.mt5TicketMux.Unlock()

	entries, ok := a.pendingCloseByBase[baseID]
	if !ok || len(entries) == 0 {
		return false
	}
	now := time.Now()
	filtered := make([]pendingTicket, 0, len(entries))
	found := false
	for _, entry := range entries {
		if entry.ticket == 0 {
			continue
		}
		age := now.Sub(entry.marked)
		if age <= pendingCloseTTL {
			filtered = append(filtered, entry)
			if within > 0 && age <= within {
				found = true
			}
		}
	}
	if len(filtered) == 0 {
		delete(a.pendingCloseByBase, baseID)
	} else {
		a.pendingCloseByBase[baseID] = filtered
	}
	return found
}

func (a *App) openTicketCount(baseID string) int {
	if strings.TrimSpace(baseID) == "" {
		return 0
	}
	a.mt5TicketMux.RLock()
	defer a.mt5TicketMux.RUnlock()

	count := len(a.baseIdToTickets[baseID])
	if entries, ok := a.pendingCloseByBase[baseID]; ok {
		now := time.Now()
		for _, entry := range entries {
			if entry.ticket != 0 && now.Sub(entry.marked) <= pendingCloseTTL {
				count++
			}
		}
	}
	return count
}

func (a *App) rehydrateTickets(baseID string) []uint64 {
	if strings.TrimSpace(baseID) == "" {
		return nil
	}
	a.mt5TicketMux.Lock()
	defer a.mt5TicketMux.Unlock()

	var restored []uint64
	for ticket, mappedBase := range a.mt5TicketToBaseId {
		if ticket == 0 || mappedBase != baseID {
			continue
		}
		if containsUint64(a.baseIdToTickets[baseID], ticket) {
			continue
		}
		if entries, ok := a.pendingCloseByBase[baseID]; ok {
			alreadyPending := false
			for _, entry := range entries {
				if entry.ticket == ticket {
					alreadyPending = true
					break
				}
			}
			if alreadyPending {
				continue
			}
		}
		restored = append(restored, ticket)
	}
	if len(restored) > 0 {
		existing := a.baseIdToTickets[baseID]
		merged := make([]uint64, 0, len(restored)+len(existing))
		merged = append(merged, restored...)
		merged = append(merged, existing...)
		a.baseIdToTickets[baseID] = merged
	}
	return restored
}

func containsUint64(list []uint64, target uint64) bool {
	for _, v := range list {
		if v == target {
			return true
		}
	}
	return false
}

func (a *App) recordInstrumentAccount(baseID, instrument, account string) {
	if strings.TrimSpace(baseID) == "" {
		return
	}
	if instrument == "" && account == "" {
		return
	}
	a.mt5TicketMux.Lock()
	if instrument != "" {
		a.baseIdToInstrument[baseID] = instrument
	}
	if account != "" {
		a.baseIdToAccount[baseID] = account
	}
	a.mt5TicketMux.Unlock()
}

func (a *App) enqueueCloseTrade(baseID string, ticket uint64, instrument, account string, request interface{}) error {
	trade := Trade{
		ID:              fmt.Sprintf("close_%d", time.Now().UnixNano()),
		BaseID:          baseID,
		Time:            time.Now(),
		Action:          "CLOSE_HEDGE",
		Quantity:        1,
		Price:           0.0,
		TotalQuantity:   1,
		ContractNum:     1,
		OrderType:       "CLOSE",
		MeasurementPips: 0,
		RawMeasurement:  0.0,
		Instrument:      instrument,
		AccountName:     account,
		NTBalance:       0.0,
		NTDailyPnL:      0.0,
		NTTradeResult:   "closed",
		NTSessionTrades: 0,
		MT5Ticket:       ticket,
	}
	if trade.Instrument == "" {
		trade.Instrument = getInstrumentFromRequest(request)
	}
	if trade.AccountName == "" {
		trade.AccountName = getAccountFromRequest(request)
	}
	if err := a.AddToTradeQueue(trade); err != nil {
		return err
	}
	a.clientCloseMux.Lock()
	if ticket != 0 {
		a.clientInitiatedTickets[ticket] = time.Now()
	}
	a.clientCloseMux.Unlock()
	log.Printf("gRPC: Enqueued CLOSE_HEDGE ticket %d for BaseID %s", ticket, baseID)
	return nil
}

// NewApp creates a new App application struct
func NewApp() *App {

	// Read configuration from environment variables
	grpcPort := getEnvString("BRIDGE_GRPC_PORT", "50051")

	log.Printf("Configuration: gRPC=true, gRPCPort=%s", grpcPort)

	app := &App{
		tradeQueue:           make(chan Trade, 100),
		eaActive:             false, // Initialize HedgeBot as inactive
		tradeLogSenderActive: false,
		// gRPC configuration from environment
		grpcPort: grpcPort,
		// Initialize MT5 ticket mappings
		mt5TicketToBaseId:      make(map[uint64]string),
		baseIdToTickets:        make(map[string][]uint64),
		pendingCloseByBase:     make(map[string][]pendingTicket),
		baseIdToInstrument:     make(map[string]string),
		baseIdToAccount:        make(map[string]string),
		clientInitiatedTickets: make(map[uint64]time.Time),
		baseIdToElastic:        make(map[string]elasticInfo),
	}

	// Initialize gRPC server
	app.grpcServer = grpcserver.NewGRPCServer(app)

	// Initialize elastic correlation maps
	app.initElasticMaps()

	return app
}

// Helper functions for environment variable configuration
func getEnvString(key, defaultValue string) string {
	if value := os.Getenv(key); value != "" {
		return value
	}
	return defaultValue
}

// startup is called when the app starts. The context is saved
// so we can call the runtime methods
func (a *App) startup(ctx context.Context) {
	a.ctx = ctx

	// Start background goroutine to monitor addon connection status
	go func() {
		ticker := time.NewTicker(10 * time.Second)
		defer ticker.Stop()
		for {
			<-ticker.C

			a.addonStatusMux.Lock()
			// Do not forcibly mark disconnected on idle; only warn if stale.
			if a.platformConnected && time.Since(a.lastAddonRequestTime) > 2*time.Minute {
				log.Printf("Addon connection appears stale (last request: %s ago) — keeping connected=true until explicit disconnect", time.Since(a.lastAddonRequestTime))
			}
			a.addonStatusMux.Unlock()
		}
	}()

	// Start server initialization
	a.startServer()

}

// startServer initializes the gRPC server
func (a *App) startServer() {

	log.Printf("=== Bridge Server Starting (alignment+pclose enabled) ===")
	log.Printf("Initial state:")
	log.Printf("Net position: %d", a.netPosition)
	log.Printf("Hedge size: %.2f", a.hedgeLot)
	log.Printf("Queue size: %d", len(a.tradeQueue))
	log.Printf("gRPC enabled: true")

	// Start gRPC server ONLY - no HTTP fallback
	log.Printf("Starting gRPC server on port %s", a.grpcPort)
	if err := a.grpcServer.StartGRPCServer(a.grpcPort); err != nil {
		log.Printf("FATAL: Failed to start gRPC server: %v", err)
		panic("gRPC server failed to start - no fallback available")
	} else {
		log.Printf("gRPC server started successfully on port %s", a.grpcPort)
	}

	a.bridgeActive = true
}

// shutdown is called when the app terminates
func (a *App) shutdown(ctx context.Context) {
	log.Printf("Application shutting down...")
	a.DisableAllProtocols("app shutdown")
	// Flush unified logger + Sentry
	blog.L().Shutdown()
}

// === FRONTEND API FUNCTIONS ===

// AttemptReconnect restarts the gRPC server (for pure gRPC mode)
func (a *App) AttemptReconnect(enableGRPC, enableHTTP, enableDual bool) map[string]interface{} {
	log.Printf("Frontend requested reconnect - pure gRPC mode only")

	// In pure gRPC mode, we ignore the parameters and just ensure gRPC is running
	if !a.bridgeActive {
		log.Printf("Restarting gRPC server...")
		if err := a.grpcServer.StartGRPCServer(a.grpcPort); err != nil {
			log.Printf("ERROR: Failed to restart gRPC server: %v", err)
			return map[string]interface{}{
				"success": false,
				"message": "Failed to restart gRPC server",
				"error":   err.Error(),
			}
		}
		a.bridgeActive = true
	}

	return map[string]interface{}{
		"success": true,
		"message": "gRPC server is active",
		"grpc":    true,
		"http":    false,
	}
}

// GetStatus returns the current bridge status
func (a *App) GetStatus() map[string]interface{} {
	a.queueMux.Lock()
	defer a.queueMux.Unlock()

	a.addonStatusMux.Lock()
	platformConnected := a.platformConnected
	a.addonStatusMux.Unlock()

	a.eaStatusMux.Lock()
	eaActive := a.eaActive
	a.eaStatusMux.Unlock()

	return map[string]interface{}{
		"bridgeActive":         a.bridgeActive,
		"platformConnected":    platformConnected,
		"addonConnected":       platformConnected,
		"eaActive":             eaActive,
		"hedgebotActive":       eaActive,
		"tradeLogSenderActive": a.tradeLogSenderActive,
		"netPosition":          a.netPosition,
		"hedgeSize":            a.hedgeLot,
		"queueSize":            len(a.tradeQueue),
	}
}

// GetNetPosition returns the current net position
func (a *App) GetNetPosition() int {
	return a.netPosition
}

// GetHedgeSize returns the current hedge size
func (a *App) GetHedgeSize() float64 {
	return a.hedgeLot
}

// GetQueueSize returns the current queue size
func (a *App) GetQueueSize() int {
	return len(a.tradeQueue)
}

// IsAddonConnected returns whether the addon is connected
func (a *App) IsAddonConnected() bool {
	a.addonStatusMux.Lock()
	defer a.addonStatusMux.Unlock()
	return a.platformConnected
}

// SetAddonConnected sets the addon connection status
func (a *App) SetAddonConnected(connected bool) {
	a.addonStatusMux.Lock()
	defer a.addonStatusMux.Unlock()
	a.platformConnected = connected
	if connected {
		a.lastAddonRequestTime = time.Now()
	}
}

// IsHedgebotActive returns whether the hedgebot is active
func (a *App) IsHedgebotActive() bool {
	a.eaStatusMux.Lock()
	defer a.eaStatusMux.Unlock()
	return a.eaActive
}

// SetHedgebotActive sets the hedgebot active status
func (a *App) SetHedgebotActive(active bool) {
	a.eaStatusMux.Lock()
	prev := a.eaActive
	a.eaActive = active
	if active {
		a.eaLastPing = time.Now()
	}
	a.eaStatusMux.Unlock()
	// Only log on state changes to avoid spam
	if prev != active {
		log.Printf("Hedgebot active status changed: %v -> %v", prev, active)
	}
}

// GetTradeQueue returns the current trade queue
func (a *App) GetTradeQueue() chan interface{} {
	// Convert to interface{} channel for gRPC compatibility
	ch := make(chan interface{}, len(a.tradeQueue))

	a.queueMux.Lock()
	defer a.queueMux.Unlock()

	// Copy current trades to the interface channel
	for {
		select {
		case trade := <-a.tradeQueue:
			ch <- trade
		default:
			close(ch)
			return ch
		}
	}
}

// PollTradeFromQueue returns a trade from the queue (non-blocking)
func (a *App) PollTradeFromQueue() interface{} {
	select {
	case trade := <-a.tradeQueue:
		return trade
	default:
		return nil
	}
}

// AddToTradeQueue adds a trade to the queue
func (a *App) AddToTradeQueue(trade interface{}) error {
	// Use reflection to convert the incoming trade to our Trade type
	var t Trade

	log.Printf("AddToTradeQueue: Received trade type: %T", trade)

	// Always use JSON marshaling for consistent conversion regardless of type
	// Support both internal.grpc.InternalTrade and main.Trade shapes
	jsonBytes, err := json.Marshal(trade)
	if err != nil {
		log.Printf("AddToTradeQueue: Failed to marshal trade: %v", err)
		return fmt.Errorf("failed to marshal trade: %v", err)
	}

	// First try to unmarshal directly into our Trade struct
	if err := json.Unmarshal(jsonBytes, &t); err != nil {
		// Fallback: adapt known field name mismatches if any
		// e.g., internal uses 'instrument' json tag, Time as unix seconds via 'timestamp' when coming from proto
		var aux map[string]interface{}
		if err2 := json.Unmarshal(jsonBytes, &aux); err2 == nil {
			// Normalize keys
			if v, ok := aux["instrument_name"]; ok {
				aux["instrument"] = v
			}
			if v, ok := aux["timestamp"]; ok {
				// timestamp (seconds) -> time
				switch tv := v.(type) {
				case float64:
					aux["time"] = time.Unix(int64(tv), 0)
				case int64:
					aux["time"] = time.Unix(tv, 0)
				}
			}
			// Try again after normalization
			if reb, err3 := json.Marshal(aux); err3 == nil {
				if err4 := json.Unmarshal(reb, &t); err4 != nil {
					log.Printf("AddToTradeQueue: Failed to unmarshal normalized trade: %v", err4)
					return fmt.Errorf("failed to unmarshal trade: %v", err4)
				}
			} else {
				log.Printf("AddToTradeQueue: Failed to re-marshal normalized trade: %v", err3)
				return fmt.Errorf("failed to marshal trade: %v", err3)
			}
		} else {
			log.Printf("AddToTradeQueue: Failed to unmarshal trade to map: %v", err2)
			return fmt.Errorf("failed to unmarshal trade: %v", err)
		}
	}

	log.Printf("AddToTradeQueue: Successfully converted trade - ID: %s, Action: %s", t.ID, t.Action)
	normalizeTrade(&t)
	log.Printf("AddToTradeQueue: Normalized trade - canonical_id: %s (qt_trade_id=%s base_id=%s)", t.ID, t.QTTradeID, t.BaseID)

	// If this is an EVENT trade, emit a concise debug snapshot of the enrichment payload
	if strings.EqualFold(strings.TrimSpace(t.Action), "EVENT") {
		log.Printf("DEBUG: EVENT enqueue base_id=%s event_type=%s elastic_current_profit=%.4f elastic_profit_level=%d nt_points_per_1k_loss=%.4f", strings.TrimSpace(t.BaseID), strings.TrimSpace(t.EventType), t.ElasticCurrentProfit, t.ElasticProfitLevel, t.NTPointsPer1kLoss)
	}

	// Track instrument/account and nt_points_per_1k_loss per BaseID for later use
	if b := strings.TrimSpace(t.BaseID); b != "" {
		actLower := strings.ToLower(strings.TrimSpace(t.Action))
		if actLower == "buy" || actLower == "sell" {
			inst := strings.TrimSpace(t.Instrument)
			acct := strings.TrimSpace(t.AccountName)
			points := t.NTPointsPer1kLoss

			a.mt5TicketMux.Lock()
			if inst != "" {
				a.baseIdToInstrument[b] = inst
			}
			if acct != "" {
				a.baseIdToAccount[b] = acct
			}

			info := a.baseIdToElastic[b]
			if points > 0 {
				info.PointsPer1kLoss = points
			}
			if inst != "" {
				info.Instrument = inst
			}
			if acct != "" {
				info.Account = acct
			}
			a.baseIdToElastic[b] = info
			a.mt5TicketMux.Unlock()
		}
	}

	select {
	case a.tradeQueue <- t:
		return nil
	default:
		return fmt.Errorf("trade queue is full")
	}
}

// AddToTradeHistory adds a trade to the history
func (a *App) AddToTradeHistory(trade interface{}) {
	// Use the same conversion logic as AddToTradeQueue
	var t Trade

	switch v := trade.(type) {
	case Trade:
		t = v
	default:
		// Use JSON marshaling/unmarshaling as a universal converter
		jsonBytes, err := json.Marshal(trade)
		if err != nil {
			log.Printf("Failed to marshal trade for history: %v", err)
			return
		}

		if err := json.Unmarshal(jsonBytes, &t); err != nil {
			log.Printf("Failed to unmarshal trade for history: %v", err)
			return
		}
	}

	a.tradeHistory = append(a.tradeHistory, t)
}

// GetTradeHistory returns the trade history
func (a *App) GetTradeHistory() []Trade {
	return a.tradeHistory
}

// initElasticMaps ensures new elastic correlation maps are initialized
func (a *App) initElasticMaps() {
	a.elasticMux.Lock()
	if a.recentElasticByBase == nil {
		a.recentElasticByBase = make(map[string]elasticMark)
	}
	if a.recentElasticByTicket == nil {
		a.recentElasticByTicket = make(map[uint64]elasticMark)
	}
	a.elasticMux.Unlock()
}

// Placeholder implementations for gRPC interface compliance
func (a *App) HandleHedgeCloseNotification(notification interface{}) error {
	log.Printf("gRPC: Received hedge close notification: %+v", notification)

	baseID := strings.TrimSpace(getBaseIDFromRequest(notification))
	if baseID == "" {
		log.Printf("gRPC: Warning - hedge close notification missing BaseID")
		return fmt.Errorf("hedge close notification missing BaseID")
	}

	closureReason := strings.TrimSpace(getClosureReasonFromRequest(notification))
	lowerReason := strings.ToLower(closureReason)
	quantity := getQuantityFromRequest(notification)
	inst := strings.TrimSpace(getInstrumentFromRequest(notification))
	acct := strings.TrimSpace(getAccountFromRequest(notification))
	mt5Ticket := getMT5TicketFromRequest(notification)

	if inst != "" || acct != "" {
		a.recordInstrumentAccount(baseID, inst, acct)
	}

	if strings.HasPrefix(lowerReason, "elastic_") {
		a.markElasticClose(baseID, mt5Ticket, closureReason, quantity)
	}

	closeNotification := struct {
		ID              string    `json:"id"`
		BaseID          string    `json:"base_id"`
		Time            time.Time `json:"time"`
		Action          string    `json:"action"`
		Quantity        float64   `json:"quantity"`
		Price           float64   `json:"price"`
		TotalQuantity   float64   `json:"total_quantity"`
		ContractNum     int       `json:"contract_num"`
		OrderType       string    `json:"order_type"`
		MeasurementPips float64   `json:"measurement_pips"`
		RawMeasurement  float64   `json:"raw_measurement"`
		Instrument      string    `json:"instrument"`
		AccountName     string    `json:"account_name"`
		NTBalance       float64   `json:"nt_balance"`
		NTDailyPnL      float64   `json:"nt_daily_pnl"`
		NTTradeResult   string    `json:"nt_trade_result"`
		NTSessionTrades int       `json:"nt_session_trades"`
		MT5Ticket       uint64    `json:"mt5_ticket"`
		ClosureReason   string    `json:"closure_reason"`
	}{
		ID:              fmt.Sprintf("mt5close_%d", time.Now().UnixNano()),
		BaseID:          baseID,
		Time:            time.Now(),
		Action:          "MT5_CLOSE_NOTIFICATION",
		Quantity:        quantity,
		Price:           0,
		TotalQuantity:   quantity,
		ContractNum:     1,
		OrderType:       "MT5_CLOSE",
		MeasurementPips: 0,
		RawMeasurement:  0,
		Instrument:      inst,
		AccountName:     acct,
		NTBalance:       0,
		NTDailyPnL:      0,
		NTTradeResult:   "mt5_closed",
		NTSessionTrades: 0,
		MT5Ticket:       mt5Ticket,
		ClosureReason:   closureReason,
	}

	if mt5Ticket == 0 {
		log.Printf("WARN: MT5 close notification for base_id=%s did not include a ticket; downstream consumers may fall back to base-only handling", baseID)
	}

	a.grpcServer.BroadcastMT5CloseToAddonStreams(closeNotification)

	if mt5Ticket != 0 && !strings.EqualFold(lowerReason, "elastic_partial_close") {
		a.removeTicketFromPool(baseID, mt5Ticket)
	}

	log.Printf("gRPC: Successfully processed MT5 closure notification for BaseID: %s (reason=%s, ticket=%d)", baseID, closureReason, mt5Ticket)
	return nil
}

func (a *App) HandleMT5TradeResult(result interface{}) error {
	log.Printf("gRPC: Received MT5 trade result: %+v", result)

	switch mt5Result := result.(type) {
	case *grpcserver.InternalMT5TradeResult:
		return a.handleInternalMT5TradeResult(mt5Result)
	case map[string]interface{}:
		converted := &grpcserver.InternalMT5TradeResult{}
		if status, ok := mt5Result["Status"].(string); ok {
			converted.Status = status
		}
		if ticketVal, ok := mt5Result["Ticket"].(float64); ok {
			converted.Ticket = uint64(ticketVal)
		}
		if volumeVal, ok := mt5Result["Volume"].(float64); ok {
			converted.Volume = volumeVal
		}
		if isClose, ok := mt5Result["IsClose"].(bool); ok {
			converted.IsClose = isClose
		}
		if idVal, ok := mt5Result["ID"].(string); ok {
			converted.ID = idVal
		}
		return a.handleInternalMT5TradeResult(converted)
	default:
		log.Printf("gRPC: WARNING - Unknown MT5 trade result type: %T", result)
		return nil
	}
}

func (a *App) handleInternalMT5TradeResult(res *grpcserver.InternalMT5TradeResult) error {
	if res == nil {
		return nil
	}

	baseID := strings.TrimSpace(res.ID)
	ticket := res.Ticket
	if baseID == "" && ticket == 0 {
		log.Printf("gRPC: Ignoring MT5 trade result with no identifiers: %+v", res)
		return nil
	}

	if res.IsClose {
		closureReason := strings.TrimSpace(res.Status)
		if closureReason == "" {
			closureReason = "MT5_position_closed"
		}
		suppressBroadcast := false
		if mk, ok := a.recentElasticFor(baseID, ticket, 3*time.Second); ok {
			if strings.EqualFold(mk.reason, "elastic_partial_close") {
				suppressBroadcast = true
				closureReason = mk.reason
			} else if strings.TrimSpace(mk.reason) != "" {
				closureReason = mk.reason
			}
		}

		orderType := "MT5_CLOSE"
		a.clientCloseMux.Lock()
		if ticket != 0 {
			if ts, ok := a.clientInitiatedTickets[ticket]; ok {
				if time.Since(ts) <= 5*time.Second {
					orderType = "NT_CLOSE_ACK"
				}
				delete(a.clientInitiatedTickets, ticket)
			}
		}
		a.clientCloseMux.Unlock()

		shouldPrune := ticket != 0 && !strings.EqualFold(closureReason, "elastic_partial_close")
		if shouldPrune {
			a.removeTicketFromPool(baseID, ticket)
		}

		inst, acct := a.bestInstAcctFor(baseID)
		closeNotification := struct {
			ID              string    `json:"id"`
			BaseID          string    `json:"base_id"`
			Time            time.Time `json:"time"`
			Action          string    `json:"action"`
			Quantity        float64   `json:"quantity"`
			Price           float64   `json:"price"`
			TotalQuantity   float64   `json:"total_quantity"`
			ContractNum     int       `json:"contract_num"`
			OrderType       string    `json:"order_type"`
			MeasurementPips float64   `json:"measurement_pips"`
			RawMeasurement  float64   `json:"raw_measurement"`
			Instrument      string    `json:"instrument"`
			AccountName     string    `json:"account_name"`
			NTBalance       float64   `json:"nt_balance"`
			NTDailyPnL      float64   `json:"nt_daily_pnl"`
			NTTradeResult   string    `json:"nt_trade_result"`
			NTSessionTrades int       `json:"nt_session_trades"`
			MT5Ticket       uint64    `json:"mt5_ticket"`
			ClosureReason   string    `json:"closure_reason"`
		}{
			ID:              fmt.Sprintf("mt5close_result_%d", time.Now().UnixNano()),
			BaseID:          baseID,
			Time:            time.Now(),
			Action:          "MT5_CLOSE_NOTIFICATION",
			Quantity:        res.Volume,
			Price:           0,
			TotalQuantity:   res.Volume,
			ContractNum:     1,
			OrderType:       orderType,
			MeasurementPips: 0,
			RawMeasurement:  0,
			Instrument:      inst,
			AccountName:     acct,
			NTBalance:       0,
			NTDailyPnL:      0,
			NTTradeResult:   "mt5_closed",
			NTSessionTrades: 0,
			MT5Ticket:       ticket,
			ClosureReason:   closureReason,
		}

		if suppressBroadcast {
			log.Printf("gRPC: Suppressed MT5 close broadcast for ticket %d (BaseID: %s) due to elastic_partial_close context.", ticket, baseID)
		} else {
			a.grpcServer.BroadcastMT5CloseToAddonStreams(closeNotification)
			log.Printf("gRPC: Processed MT5 close result for ticket %d (BaseID: %s) reason=%s", ticket, baseID, closureReason)
		}
		return nil
	}

	if baseID == "" || ticket == 0 {
		log.Printf("gRPC: MT5 open/fill result missing base or ticket: %+v", res)
		return nil
	}

	a.mt5TicketMux.Lock()
	prevBase, exists := a.mt5TicketToBaseId[ticket]
	a.mt5TicketToBaseId[ticket] = baseID
	list := a.baseIdToTickets[baseID]
	for _, existing := range list {
		if existing == ticket {
			a.mt5TicketMux.Unlock()
			if exists && prevBase != baseID {
				log.Printf("gRPC: MT5 ticket %d reassigned from BaseID %s to %s", ticket, prevBase, baseID)
			}
			return nil
		}
	}
	a.baseIdToTickets[baseID] = append(list, ticket)
	a.mt5TicketMux.Unlock()

	log.Printf("gRPC: Stored MT5 ticket mapping - Ticket: %d -> BaseID: %s (open result)", ticket, baseID)
	return nil
}

func (a *App) HandleElasticUpdate(update interface{}) error {
	log.Printf("gRPC: Received elastic update: %+v", update)
	// Extract minimal fields
	baseID := getBaseIDFromRequest(update)
	profitLvl := getElasticProfitLevel(update)
	curProfit := getElasticCurrentProfit(update)
	mt5tk := getMT5TicketFromRequest(update)
	log.Printf("DEBUG: Elastic extract base_id=%s profit_level=%d current_profit=%.4f mt5_ticket=%d", baseID, profitLvl, curProfit, mt5tk)

	// Inject cached Quantower sizing hint and enrichment where possible
	a.mt5TicketMux.RLock()
	info := a.baseIdToElastic[baseID]
	ntPts := info.PointsPer1kLoss
	inst := strings.TrimSpace(a.baseIdToInstrument[baseID])
	acct := strings.TrimSpace(a.baseIdToAccount[baseID])
	if inst == "" {
		inst = strings.TrimSpace(info.Instrument)
	}
	if acct == "" {
		acct = strings.TrimSpace(info.Account)
	}
	a.mt5TicketMux.RUnlock()

	// If essentials missing, wait briefly for originating trade to populate caches
	if ntPts <= 0 || inst == "" || acct == "" {
		for i := 0; i < 10; i++ {
			time.Sleep(15 * time.Millisecond)
			a.mt5TicketMux.RLock()
			info = a.baseIdToElastic[baseID]
			if ntPts <= 0 {
				ntPts = info.PointsPer1kLoss
			}
			if inst == "" {
				inst = strings.TrimSpace(a.baseIdToInstrument[baseID])
				if inst == "" {
					inst = strings.TrimSpace(info.Instrument)
				}
			}
			if acct == "" {
				acct = strings.TrimSpace(a.baseIdToAccount[baseID])
				if acct == "" {
					acct = strings.TrimSpace(info.Account)
				}
			}
			a.mt5TicketMux.RUnlock()
			if ntPts > 0 && inst != "" && acct != "" {
				break
			}
		}
	}

	// Generate unique ID using sequence counter to prevent duplicate rejection
	a.elasticSeqMux.Lock()
	a.elasticSeqCount++
	uniqueID := fmt.Sprintf("elastic_evt_%d_%d", time.Now().UnixNano(), a.elasticSeqCount)
	a.elasticSeqMux.Unlock()

	// Enqueue a lightweight event trade carrying enrichment so EA can branch on event_type
	ct := Trade{
		ID:                   uniqueID,
		BaseID:               baseID,
		Time:                 time.Now(),
		Action:               "EVENT",
		Quantity:             0,
		Price:                0,
		TotalQuantity:        0,
		ContractNum:          0,
		OrderType:            "EVENT",
		MeasurementPips:      0,
		RawMeasurement:       0,
		Instrument:           inst,
		AccountName:          acct,
		MT5Ticket:            mt5tk,
		NTPointsPer1kLoss:    ntPts,
		EventType:            "elastic_hedge_update",
		ElasticCurrentProfit: curProfit,
		ElasticProfitLevel:   int32(profitLvl),
	}
	if err := a.AddToTradeQueue(ct); err != nil {
		return fmt.Errorf("failed to enqueue elastic event: %v", err)
	}
	if ntPts <= 0 {
		log.Printf("WARN: Enqueued elastic event without nt_points_per_1k_loss (base_id=%s, inst=%s)", baseID, inst)
	}
	return nil
}

func (a *App) HandleTrailingStopUpdate(update interface{}) error {
	log.Printf("gRPC: Received trailing stop update: %+v", update)
	// No synthetic mapping; EA may act on elastic updates sent explicitly via SubmitElasticUpdate.
	return nil
}

func (a *App) HandleCloseHedgeRequest(request interface{}) error {
	log.Printf("gRPC: Received close hedge request: %+v", request)

	baseID := strings.TrimSpace(getBaseIDFromRequest(request))
	if baseID == "" {
		return fmt.Errorf("close hedge request missing base_id")
	}

	inst := strings.TrimSpace(getInstrumentFromRequest(request))
	acct := strings.TrimSpace(getAccountFromRequest(request))
	if inst != "" || acct != "" {
		a.recordInstrumentAccount(baseID, inst, acct)
	}

	// CRITICAL FIX: Since we removed trade splitting, each QT position = 1 MT5 hedge
	// Always close exactly 1 ticket per close request, regardless of quantity
	qty := 1

	providedTicket := getMT5TicketFromRequest(request)
	if providedTicket != 0 {
		a.evictTicketFromQueue(baseID, providedTicket)
		if err := a.enqueueCloseTrade(baseID, providedTicket, inst, acct, request); err != nil {
			return fmt.Errorf("failed to enqueue targeted CLOSE_HEDGE for ticket %d: %w", providedTicket, err)
		}
		a.trackPendingTicket(baseID, providedTicket)
		log.Printf("gRPC: Enqueued targeted CLOSE_HEDGE for BaseID %s (ticket=%d)", baseID, providedTicket)
		return nil
	}

	const (
		maxTicketWait = 2 * time.Second
		pollInterval  = 50 * time.Millisecond
	)

	log.Printf("gRPC: Closing 1 MT5 ticket for BaseID %s (1 QT position = 1 MT5 hedge)", baseID)

	allocated := make([]uint64, 0, qty)
	for i := 0; i < qty; i++ {
		ticket, ok := a.popTicketWithWait(baseID, maxTicketWait, pollInterval)
		if !ok {
			if restored := a.rehydrateTickets(baseID); len(restored) > 0 {
				ticket, ok = a.popTicket(baseID)
			}
		}
		if !ok {
			for _, tk := range allocated {
				a.pushTicket(baseID, tk)
				a.mt5TicketMux.Lock()
				a.mt5TicketToBaseId[tk] = baseID
				a.mt5TicketMux.Unlock()
			}
			if a.hasRecentPendingClose(baseID, maxTicketWait) {
				log.Printf("gRPC: Duplicate CLOSE_HEDGE detected for BaseID %s; pending MT5 closure still in flight", baseID)
				return nil
			}
			if a.openTicketCount(baseID) == 0 {
				log.Printf("gRPC: No tracked MT5 tickets remain for BaseID %s; treating close request as idempotent", baseID)
				return nil
			}
			return fmt.Errorf("no MT5 tickets available for base_id %s (requested %d, got %d)", baseID, qty, len(allocated))
		}
		allocated = append(allocated, ticket)
	}

	for idx, tk := range allocated {
		if err := a.enqueueCloseTrade(baseID, tk, inst, acct, request); err != nil {
			// restore current ticket and any remaining ones
			a.pushTicket(baseID, tk)
			a.mt5TicketMux.Lock()
			a.mt5TicketToBaseId[tk] = baseID
			a.mt5TicketMux.Unlock()
			for j := idx + 1; j < len(allocated); j++ {
				pending := allocated[j]
				a.pushTicket(baseID, pending)
				a.mt5TicketMux.Lock()
				a.mt5TicketToBaseId[pending] = baseID
				a.mt5TicketMux.Unlock()
			}
			return fmt.Errorf("failed to enqueue CLOSE_HEDGE for ticket %d: %w", tk, err)
		}

		a.trackPendingTicket(baseID, tk)
	}

	log.Printf("gRPC: Enqueued CLOSE_HEDGE for BaseID %s using %d ticket(s)", baseID, len(allocated))
	return nil
}

func getBaseIDFromRequest(request interface{}) string {
	if req, ok := request.(map[string]interface{}); ok {
		// Support common key variants
		if baseID, ok := req["BaseID"].(string); ok {
			return baseID
		}
		if baseID, ok := req["base_id"].(string); ok {
			return baseID
		}
		if baseID, ok := req["BaseId"].(string); ok { // camel variant
			return baseID
		}
	}
	// Try reflection for struct fields
	val := reflect.ValueOf(request)
	if val.Kind() == reflect.Ptr {
		val = val.Elem()
	}
	if val.Kind() == reflect.Struct {
		baseIDField := val.FieldByName("BaseID")
		if baseIDField.IsValid() && baseIDField.Kind() == reflect.String {
			return baseIDField.String()
		}
	}
	return ""
}

func getQuantityFromRequest(request interface{}) float64 {
	if req, ok := request.(map[string]interface{}); ok {
		if quantity, ok := req["ClosedHedgeQuantity"].(float64); ok {
			return quantity
		}
	}
	// Try reflection for struct fields
	val := reflect.ValueOf(request)
	if val.Kind() == reflect.Ptr {
		val = val.Elem()
	}
	if val.Kind() == reflect.Struct {
		quantityField := val.FieldByName("ClosedHedgeQuantity")
		if quantityField.IsValid() && quantityField.Kind() == reflect.Float64 {
			return quantityField.Float()
		}
	}
	return 1.0 // Default
}

func getInstrumentFromRequest(request interface{}) string {
	if req, ok := request.(map[string]interface{}); ok {
		if instrument, ok := req["NTInstrumentSymbol"].(string); ok {
			return instrument
		}
	}
	// Try reflection for struct fields
	val := reflect.ValueOf(request)
	if val.Kind() == reflect.Ptr {
		val = val.Elem()
	}
	if val.Kind() == reflect.Struct {
		instrumentField := val.FieldByName("NTInstrumentSymbol")
		if instrumentField.IsValid() && instrumentField.Kind() == reflect.String {
			return instrumentField.String()
		}
	}
	return ""
}

func getAccountFromRequest(request interface{}) string {
	if req, ok := request.(map[string]interface{}); ok {
		if account, ok := req["NTAccountName"].(string); ok {
			return account
		}
	}
	// Try reflection for struct fields
	val := reflect.ValueOf(request)
	if val.Kind() == reflect.Ptr {
		val = val.Elem()
	}
	if val.Kind() == reflect.Struct {
		accountField := val.FieldByName("NTAccountName")
		if accountField.IsValid() && accountField.Kind() == reflect.String {
			return accountField.String()
		}
	}
	return ""
}

func getClosureReasonFromRequest(request interface{}) string {
	if req, ok := request.(map[string]interface{}); ok {
		if reason, ok := req["ClosureReason"].(string); ok {
			return reason
		}
	}
	// Try reflection for struct fields
	val := reflect.ValueOf(request)
	if val.Kind() == reflect.Ptr {
		val = val.Elem()
	}
	if val.Kind() == reflect.Struct {
		reasonField := val.FieldByName("ClosureReason")
		if reasonField.IsValid() && reasonField.Kind() == reflect.String {
			return reasonField.String()
		}
	}
	return ""
}

func getMT5TicketFromRequest(request interface{}) uint64 {
	log.Printf("DEBUG: getMT5TicketFromRequest called with type %T, value: %+v", request, request)
	if req, ok := request.(map[string]interface{}); ok {
		log.Printf("DEBUG: Processing as map[string]interface{}")
		// Try both field name variants (MT5Ticket and mt5_ticket)
		if ticket, ok := req["MT5Ticket"].(uint64); ok {
			log.Printf("DEBUG: Found MT5Ticket as uint64: %d", ticket)
			return ticket
		}
		if ticket, ok := req["mt5_ticket"].(uint64); ok {
			log.Printf("DEBUG: Found mt5_ticket as uint64: %d", ticket)
			return ticket
		}
		// Handle float64 conversion (common in JSON)
		if ticketFloat, ok := req["MT5Ticket"].(float64); ok {
			log.Printf("DEBUG: Found MT5Ticket as float64: %f", ticketFloat)
			return uint64(ticketFloat)
		}
		if ticketFloat, ok := req["mt5_ticket"].(float64); ok {
			log.Printf("DEBUG: Found mt5_ticket as float64: %f", ticketFloat)
			return uint64(ticketFloat)
		}
		// Handle string representation
		if ticketStr, ok := req["MT5Ticket"].(string); ok {
			if v, err := strconv.ParseUint(ticketStr, 10, 64); err == nil {
				log.Printf("DEBUG: Parsed MT5Ticket from string: %s -> %d", ticketStr, v)
				return v
			}
		}
		if ticketStr, ok := req["mt5_ticket"].(string); ok {
			if v, err := strconv.ParseUint(ticketStr, 10, 64); err == nil {
				log.Printf("DEBUG: Parsed mt5_ticket from string: %s -> %d", ticketStr, v)
				return v
			}
		}
		log.Printf("DEBUG: No ticket field found in map keys: %v", reflect.ValueOf(req).MapKeys())
	}
	// Try reflection for struct fields
	val := reflect.ValueOf(request)
	if val.Kind() == reflect.Ptr {
		val = val.Elem()
	}
	if val.Kind() == reflect.Struct {
		log.Printf("DEBUG: Processing as struct")
		// List all available fields for debugging
		structType := val.Type()
		log.Printf("DEBUG: Struct has %d fields:", structType.NumField())
		for i := 0; i < structType.NumField(); i++ {
			field := structType.Field(i)
			log.Printf("DEBUG: Field %d: %s (type: %s)", i, field.Name, field.Type)
		}
		// Try both field name variants
		ticketField := val.FieldByName("MT5Ticket")
		if ticketField.IsValid() && ticketField.Kind() == reflect.Uint64 {
			log.Printf("DEBUG: Found MT5Ticket field as uint64: %d", ticketField.Uint())
			return ticketField.Uint()
		}
		ticketField = val.FieldByName("Mt5Ticket")
		if ticketField.IsValid() && ticketField.Kind() == reflect.Uint64 {
			log.Printf("DEBUG: Found Mt5Ticket field as uint64: %d", ticketField.Uint())
			return ticketField.Uint()
		}
	}
	log.Printf("DEBUG: No ticket found, returning 0")
	return 0
}

// Elastic update helpers (reflective extract to avoid package coupling)
func getElasticProfitLevel(update interface{}) int {
	// Helper to parse any to int
	parseToInt := func(x interface{}) (int, bool) {
		switch t := x.(type) {
		case int:
			return t, true
		case int32:
			return int(t), true
		case int64:
			return int(t), true
		case float64:
			return int(t), true
		case float32:
			return int(t), true
		case string:
			if i, err := strconv.Atoi(strings.TrimSpace(t)); err == nil {
				return i, true
			}
		}
		return 0, false
	}
	// Map case: try multiple aliases and casings
	if m, ok := update.(map[string]interface{}); ok {
		keys := []string{"ProfitLevel", "profit_level", "profitLevel", "ElasticProfitLevel", "elastic_profit_level", "elasticProfitLevel", "level"}
		for _, k := range keys {
			if v, ok := m[k]; ok {
				if iv, ok2 := parseToInt(v); ok2 {
					return iv
				}
			}
		}
	}
	// Struct case: scan fields case-insensitively
	val := reflect.ValueOf(update)
	if val.Kind() == reflect.Ptr {
		val = val.Elem()
	}
	if val.Kind() == reflect.Struct {
		cand := map[string]struct{}{"profitlevel": {}, "elasticprofitlevel": {}, "level": {}}
		t := val.Type()
		for i := 0; i < val.NumField(); i++ {
			name := strings.ToLower(t.Field(i).Name)
			if _, ok := cand[name]; ok {
				f := val.Field(i)
				switch f.Kind() {
				case reflect.Int, reflect.Int32, reflect.Int64:
					return int(f.Int())
				case reflect.Float32, reflect.Float64:
					return int(f.Float())
				case reflect.String:
					if i, err := strconv.Atoi(strings.TrimSpace(f.String())); err == nil {
						return i
					}
				}
			}
		}
		// Fallback to exact name
		f := val.FieldByName("ProfitLevel")
		if f.IsValid() && (f.Kind() == reflect.Int || f.Kind() == reflect.Int32 || f.Kind() == reflect.Int64) {
			return int(f.Int())
		}
	}
	return 0
}

func getElasticCurrentProfit(update interface{}) float64 {
	// Helper to parse any to float64
	parseToFloat := func(x interface{}) (float64, bool) {
		switch t := x.(type) {
		case float64:
			return t, true
		case float32:
			return float64(t), true
		case int:
			return float64(t), true
		case int32:
			return float64(t), true
		case int64:
			return float64(t), true
		case string:
			if f, err := strconv.ParseFloat(strings.TrimSpace(t), 64); err == nil {
				return f, true
			}
		}
		return 0, false
	}
	if m, ok := update.(map[string]interface{}); ok {
		keys := []string{"CurrentProfit", "current_profit", "currentProfit", "ElasticCurrentProfit", "elastic_current_profit", "elasticCurrentProfit", "profit"}
		for _, k := range keys {
			if v, ok := m[k]; ok {
				if fv, ok2 := parseToFloat(v); ok2 {
					return fv
				}
			}
		}
	}
	val := reflect.ValueOf(update)
	if val.Kind() == reflect.Ptr {
		val = val.Elem()
	}
	if val.Kind() == reflect.Struct {
		cand := map[string]struct{}{"currentprofit": {}, "elasticcurrentprofit": {}, "profit": {}}
		t := val.Type()
		for i := 0; i < val.NumField(); i++ {
			name := strings.ToLower(t.Field(i).Name)
			if _, ok := cand[name]; ok {
				f := val.Field(i)
				switch f.Kind() {
				case reflect.Float32, reflect.Float64:
					return f.Float()
				case reflect.Int, reflect.Int32, reflect.Int64:
					return float64(f.Int())
				case reflect.String:
					if fl, err := strconv.ParseFloat(strings.TrimSpace(f.String()), 64); err == nil {
						return fl
					}
				}
			}
		}
		// Fallback to exact name
		f := val.FieldByName("CurrentProfit")
		if f.IsValid() && (f.Kind() == reflect.Float64 || f.Kind() == reflect.Float32) {
			return f.Float()
		}
	}
	return 0.0
}

// DisableAllProtocols stops the gRPC server (for shutdown or UI action)
func (a *App) DisableAllProtocols(reason ...string) error {
	r := "unspecified"
	if len(reason) > 0 && reason[0] != "" {
		r = reason[0]
	}
	log.Printf("Disabling gRPC server... (reason=%s)", r)

	// Stop gRPC server
	if a.grpcServer != nil {
		log.Printf("Stopping gRPC server... (reason=%s)", r)
		a.grpcServer.Stop()
	}

	a.bridgeActive = false
	log.Printf("gRPC server disabled (reason=%s)", r)
	return nil
}
