package main

import (
	"context"
	"encoding/json"
	"fmt"
	"log"
	"os"
	"reflect"
	"sync"
	"time"

	grpcserver "BridgeApp/internal/grpc"
)

// App struct
type App struct {
	ctx                  context.Context
	tradeQueue           chan Trade
	queueMux             sync.Mutex
	netNT                int
	hedgeLot             float64
	bridgeActive         bool
	addonConnected       bool
	tradeHistory         []Trade
	hedgebotActive       bool
	tradeLogSenderActive bool

	// gRPC server integration
	grpcServer     *grpcserver.Server
	grpcPort       string

	// Addon connection tracking
	lastAddonRequestTime time.Time
	addonStatusMux       sync.Mutex
	// HedgeBot connection tracking
	hedgebotStatusMux sync.Mutex // Protects hedgebotActive and hedgebotLastPing
	hedgebotLastPing  time.Time  // Timestamp of the last successful ping from Hedgebot
	
	// MT5 ticket to BaseID mapping
	mt5TicketMux     sync.RWMutex
	mt5TicketToBaseId map[uint64]string  // MT5 ticket -> BaseID
	baseIdToMT5Ticket map[string]uint64  // BaseID -> MT5 ticket
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

	// Enhanced NT Performance Data
	NTBalance       float64 `json:"nt_balance"`
	NTDailyPnL      float64 `json:"nt_daily_pnl"`
	NTTradeResult   string  `json:"nt_trade_result"` // "win", "loss", "pending"
	NTSessionTrades int     `json:"nt_session_trades"`
	
	// MT5 position tracking
	MT5Ticket       uint64  `json:"mt5_ticket"` // MT5 position ticket number (always include, even if 0)
}

// NewApp creates a new App application struct
func NewApp() *App {
	fmt.Println("DEBUG: app.go - In NewApp") // Added for debug
	
	// Read configuration from environment variables
	grpcPort := getEnvString("BRIDGE_GRPC_PORT", "50051")
	
	log.Printf("Configuration: gRPC=true, gRPCPort=%s", grpcPort)
	
	app := &App{
		tradeQueue:           make(chan Trade, 100),
		hedgebotActive:       false, // Initialize HedgeBot as inactive
		tradeLogSenderActive: false,
		// gRPC configuration from environment
		grpcPort:     grpcPort,
		// Initialize MT5 ticket mappings
		mt5TicketToBaseId: make(map[uint64]string),
		baseIdToMT5Ticket: make(map[string]uint64),
	}
	
	// Initialize gRPC server
	app.grpcServer = grpcserver.NewGRPCServer(app)
	
	return app
}

// Helper functions for environment variable configuration
func getEnvString(key, defaultValue string) string {
	if value := os.Getenv(key); value != "" {
		return value
	}
	return defaultValue
}

func getEnvBool(key string, defaultValue bool) bool {
	if value := os.Getenv(key); value != "" {
		switch value {
		case "true", "1", "yes", "on":
			return true
		case "false", "0", "no", "off":
			return false
		}
	}
	return defaultValue
}

// startup is called when the app starts. The context is saved
// so we can call the runtime methods
func (a *App) startup(ctx context.Context) {
	fmt.Println("DEBUG: app.go - In startup") // Added for debug
	a.ctx = ctx

	// Start background goroutine to monitor addon connection status
	go func() {
		ticker := time.NewTicker(10 * time.Second)
		defer ticker.Stop()
		for {
			<-ticker.C
			
			a.addonStatusMux.Lock()
			if a.addonConnected && time.Since(a.lastAddonRequestTime) > 30*time.Second {
				log.Printf("Addon connection timeout detected (last request: %s ago)", time.Since(a.lastAddonRequestTime))
				a.addonConnected = false
				log.Printf("Addon connection set to false due to timeout")
			}
			a.addonStatusMux.Unlock()
		}
	}()
	
	// Start server initialization
	a.startServer()
}

// startServer initializes the gRPC server
func (a *App) startServer() {
	fmt.Println("DEBUG: app.go - In startServer") // Added for debug
	
	log.Printf("=== Bridge Server Starting ===")
	log.Printf("Initial state:")
	log.Printf("Net position: %d", a.netNT)
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
	a.DisableAllProtocols()
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
				"error": err.Error(),
			}
		}
		a.bridgeActive = true
	}
	
	return map[string]interface{}{
		"success": true,
		"message": "gRPC server is active",
		"grpc": true,
		"http": false,
	}
}

// GetStatus returns the current bridge status
func (a *App) GetStatus() map[string]interface{} {
	a.queueMux.Lock()
	defer a.queueMux.Unlock()
	
	a.addonStatusMux.Lock()
	addonConnected := a.addonConnected
	a.addonStatusMux.Unlock()

	a.hedgebotStatusMux.Lock()
	hedgebotActive := a.hedgebotActive
	a.hedgebotStatusMux.Unlock()

	return map[string]interface{}{
		"bridgeActive":         a.bridgeActive,
		"addonConnected":       addonConnected,
		"hedgebotActive":       hedgebotActive,
		"tradeLogSenderActive": a.tradeLogSenderActive,
		"netPosition":          a.netNT,
		"hedgeSize":            a.hedgeLot,
		"queueSize":            len(a.tradeQueue),
	}
}

// GetNetPosition returns the current net position
func (a *App) GetNetPosition() int {
	return a.netNT
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
	return a.addonConnected
}

// SetAddonConnected sets the addon connection status
func (a *App) SetAddonConnected(connected bool) {
	a.addonStatusMux.Lock()
	defer a.addonStatusMux.Unlock()
	a.addonConnected = connected
	if connected {
		a.lastAddonRequestTime = time.Now()
	}
}

// IsHedgebotActive returns whether the hedgebot is active
func (a *App) IsHedgebotActive() bool {
	a.hedgebotStatusMux.Lock()
	defer a.hedgebotStatusMux.Unlock()
	return a.hedgebotActive
}

// SetHedgebotActive sets the hedgebot active status
func (a *App) SetHedgebotActive(active bool) {
	a.hedgebotStatusMux.Lock()
	defer a.hedgebotStatusMux.Unlock()
	a.hedgebotActive = active
	if active {
		a.hedgebotLastPing = time.Now()
	}
	log.Printf("Hedgebot active status set to: %v", active)
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
	jsonBytes, err := json.Marshal(trade)
	if err != nil {
		log.Printf("AddToTradeQueue: Failed to marshal trade: %v", err)
		return fmt.Errorf("failed to marshal trade: %v", err)
	}
	
	if err := json.Unmarshal(jsonBytes, &t); err != nil {
		log.Printf("AddToTradeQueue: Failed to unmarshal trade: %v", err)
		return fmt.Errorf("failed to unmarshal trade: %v", err)
	}
	
	log.Printf("AddToTradeQueue: Successfully converted trade - ID: %s, Action: %s", t.ID, t.Action)
	
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

// Placeholder implementations for gRPC interface compliance
func (a *App) HandleHedgeCloseNotification(notification interface{}) error {
	log.Printf("gRPC: Received hedge close notification: %+v", notification)
	
	// Parse the CLOSE_HEDGE notification 
	baseID := getBaseIDFromRequest(notification)
	if baseID == "" {
		log.Printf("gRPC: Warning - hedge close notification missing BaseID")
		return fmt.Errorf("hedge close notification missing BaseID")
	}
	
	// Determine source of the closure by checking closure_reason
	closureReason := getClosureReasonFromRequest(notification)
	log.Printf("gRPC: Closure reason: %s for BaseID: %s", closureReason, baseID)
	
	// Check if this is an MT5-initiated closure
	isMT5Closure := closureReason == "MT5_position_closed" || 
					closureReason == "MT5_stop_loss" || 
					closureReason == "MT5_manual_close" ||
					closureReason == "MT5_take_profit"
	
	if isMT5Closure {
		// MT5 initiated the closure - need to notify NinjaTrader
		log.Printf("gRPC: MT5-initiated closure detected - notifying NinjaTrader for BaseID: %s", baseID)
		
		// Create a close notification trade for NinjaTrader
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
			Quantity:        getQuantityFromRequest(notification),
			Price:           0.0,
			TotalQuantity:   getQuantityFromRequest(notification),
			ContractNum:     1,
			OrderType:       "MT5_CLOSE",
			MeasurementPips: 0.0,
			RawMeasurement:  0.0,
			Instrument:      getInstrumentFromRequest(notification),
			AccountName:     getAccountFromRequest(notification),
			NTBalance:       0.0,
			NTDailyPnL:      0.0,
			NTTradeResult:   "mt5_closed",
			NTSessionTrades: 0,
			MT5Ticket:       getMT5TicketFromRequest(notification),
			ClosureReason:   closureReason,
		}
		
		// Send notification to NinjaTrader via gRPC server
		if a.grpcServer != nil {
			a.grpcServer.BroadcastMT5CloseNotification(closeNotification)
		}
		
		log.Printf("gRPC: Successfully sent MT5 closure notification to NinjaTrader for BaseID: %s", baseID)
		return nil
	} else {
		// NinjaTrader initiated the closure - queue for MT5 to process
		log.Printf("gRPC: NT-initiated closure detected - queuing for MT5 for BaseID: %s", baseID)
		
		// Convert to a CLOSE_HEDGE trade structure that MT5 can process  
		closeTrade := struct {
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
		}{
			ID:              fmt.Sprintf("ntclose_%d", time.Now().UnixNano()),
			BaseID:          baseID,
			Time:            time.Now(),
			Action:          "CLOSE_HEDGE",
			Quantity:        getQuantityFromRequest(notification),
			Price:           0.0,
			TotalQuantity:   getQuantityFromRequest(notification),
			ContractNum:     1,
			OrderType:       "CLOSE",
			MeasurementPips: 0.0,
			RawMeasurement:  0.0,
			Instrument:      getInstrumentFromRequest(notification),
			AccountName:     getAccountFromRequest(notification),
			NTBalance:       0.0,
			NTDailyPnL:      0.0,
			NTTradeResult:   "closed",
			NTSessionTrades: 0,
		}
		
		log.Printf("gRPC: Processing NT hedge close notification - Adding CLOSE_HEDGE to trade queue - BaseID: %s", closeTrade.BaseID)
		
		// Add to trade queue for MT5 to process
		err := a.AddToTradeQueue(closeTrade)
		if err != nil {
			log.Printf("gRPC: Failed to add NT CLOSE_HEDGE to trade queue: %v", err)
			return fmt.Errorf("failed to add NT CLOSE_HEDGE to trade queue: %v", err)
		}
		
		log.Printf("gRPC: Successfully queued NT CLOSE_HEDGE for BaseID: %s", closeTrade.BaseID)
		return nil
	}
}

func (a *App) HandleMT5TradeResult(result interface{}) error {
	log.Printf("gRPC: Received MT5 trade result: %+v", result)
	
	// Type assertion to get the MT5TradeResult struct
	if mt5Result, ok := result.(map[string]interface{}); ok {
		// Extract ticket and baseId
		if ticketFloat, ok := mt5Result["Ticket"].(float64); ok {
			ticket := uint64(ticketFloat)
			
			if baseId, ok := mt5Result["ID"].(string); ok && baseId != "" {
				// Store the mapping
				a.mt5TicketMux.Lock()
				a.mt5TicketToBaseId[ticket] = baseId
				a.baseIdToMT5Ticket[baseId] = ticket
				a.mt5TicketMux.Unlock()
				
				log.Printf("gRPC: Stored MT5 ticket mapping - Ticket: %d -> BaseID: %s", ticket, baseId)
			}
		}
	}
	
	return nil
}

func (a *App) HandleElasticUpdate(update interface{}) error {
	log.Printf("gRPC: Received elastic update: %+v", update)
	return nil
}

func (a *App) HandleTrailingStopUpdate(update interface{}) error {
	log.Printf("gRPC: Received trailing stop update: %+v", update)
	return nil
}

func (a *App) HandleNTCloseHedgeRequest(request interface{}) error {
	log.Printf("gRPC: Received NT close hedge request: %+v", request)
	
	// Extract BaseID from request
	baseID := getBaseIDFromRequest(request)
	
	// Look up MT5 ticket from our mapping
	var mt5Ticket uint64
	a.mt5TicketMux.RLock()
	if ticket, exists := a.baseIdToMT5Ticket[baseID]; exists {
		mt5Ticket = ticket
		log.Printf("gRPC: Found MT5 ticket %d for BaseID %s", mt5Ticket, baseID)
	} else {
		log.Printf("gRPC: WARNING - No MT5 ticket found for BaseID %s, will rely on comment matching", baseID)
	}
	a.mt5TicketMux.RUnlock()
	
	// Convert to a CLOSE_HEDGE trade structure that MT5 can process
	closeTrade := Trade{
		ID:              fmt.Sprintf("close_%d", time.Now().UnixNano()),
		BaseID:          baseID,
		Time:            time.Now(),
		Action:          "CLOSE_HEDGE",
		Quantity:        getQuantityFromRequest(request),
		Price:           0.0,
		TotalQuantity:   int(getQuantityFromRequest(request)),
		ContractNum:     1,
		OrderType:       "CLOSE",
		MeasurementPips: 0,
		RawMeasurement:  0.0,
		Instrument:      getInstrumentFromRequest(request),
		AccountName:     getAccountFromRequest(request),
		NTBalance:       0.0,
		NTDailyPnL:      0.0,
		NTTradeResult:   "closed",
		NTSessionTrades: 0,
		MT5Ticket:       mt5Ticket, // Include MT5 ticket if we have it
	}
	
	log.Printf("gRPC: Adding CLOSE_HEDGE to trade queue - BaseID: %s, Action: %s, MT5Ticket: %d", closeTrade.BaseID, closeTrade.Action, closeTrade.MT5Ticket)
	
	// Add to trade queue for MT5 to process
	err := a.AddToTradeQueue(closeTrade)
	if err != nil {
		log.Printf("gRPC: Failed to add CLOSE_HEDGE to trade queue: %v", err)
		return fmt.Errorf("failed to add CLOSE_HEDGE to trade queue: %v", err)
	}
	
	log.Printf("gRPC: Successfully queued CLOSE_HEDGE for BaseID: %s", closeTrade.BaseID)
	return nil
}

// Helper functions to extract data from the hedge close request
func getBaseIDFromRequest(request interface{}) string {
	if req, ok := request.(map[string]interface{}); ok {
		if baseID, ok := req["BaseID"].(string); ok {
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
	if req, ok := request.(map[string]interface{}); ok {
		if ticket, ok := req["MT5Ticket"].(uint64); ok {
			return ticket
		}
		// Handle float64 conversion (common in JSON)
		if ticketFloat, ok := req["MT5Ticket"].(float64); ok {
			return uint64(ticketFloat)
		}
	}
	// Try reflection for struct fields
	val := reflect.ValueOf(request)
	if val.Kind() == reflect.Ptr {
		val = val.Elem()
	}
	if val.Kind() == reflect.Struct {
		ticketField := val.FieldByName("MT5Ticket")
		if ticketField.IsValid() && ticketField.Kind() == reflect.Uint64 {
			return ticketField.Uint()
		}
	}
	return 0
}

// DisableAllProtocols stops the gRPC server (for shutdown)
func (a *App) DisableAllProtocols() error {
	log.Printf("Disabling gRPC server...")
	
	// Stop gRPC server
	if a.grpcServer != nil {
		log.Printf("Stopping gRPC server...")
		a.grpcServer.Stop()
	}
	
	a.bridgeActive = false
	log.Printf("gRPC server disabled")
	return nil
}