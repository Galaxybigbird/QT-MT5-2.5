package grpc

import (
	"context"
	"encoding/json"
	"fmt"
	"log"
	"net"
	"sync"
	"time"

	trading "BridgeApp/internal/grpc/proto"
	"google.golang.org/grpc"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/keepalive"
	"google.golang.org/grpc/status"
)

// Server represents the gRPC server for the trading bridge
type Server struct {
	trading.UnimplementedTradingServiceServer
	trading.UnimplementedStreamingServiceServer
	app              AppInterface
	server           *grpc.Server
	tradeStreams     map[string]chan *trading.Trade
	streamsMux       sync.RWMutex
	healthStreams    map[string]chan *trading.HealthResponse
	healthStreamsMux sync.RWMutex
}

// AppInterface defines the interface that the App struct must implement for gRPC integration
type AppInterface interface {
	GetTradeQueue() chan interface{}
	PollTradeFromQueue() interface{} // Non-blocking trade retrieval
	AddToTradeQueue(trade interface{}) error
	GetNetPosition() int
	GetHedgeSize() float64
	GetQueueSize() int
	IsAddonConnected() bool
	IsHedgebotActive() bool
	SetAddonConnected(connected bool)
	SetHedgebotActive(active bool)
	AddToTradeHistory(trade interface{})
	HandleHedgeCloseNotification(notification interface{}) error
	HandleMT5TradeResult(result interface{}) error
	HandleElasticUpdate(update interface{}) error
	HandleTrailingStopUpdate(update interface{}) error
	HandleNTCloseHedgeRequest(request interface{}) error
}

// NewGRPCServer creates a new gRPC server instance
func NewGRPCServer(app AppInterface) *Server {
	return &Server{
		app:           app,
		tradeStreams:  make(map[string]chan *trading.Trade),
		healthStreams: make(map[string]chan *trading.HealthResponse),
	}
}

// StartGRPCServer starts the gRPC server on the specified port
func (s *Server) StartGRPCServer(port string) error {
	lis, err := net.Listen("tcp", ":"+port)
	if err != nil {
		return fmt.Errorf("failed to listen on port %s: %v", port, err)
	}

	// Configure server options with keepalive and insecure settings for .NET Framework compatibility
	opts := []grpc.ServerOption{
		grpc.KeepaliveParams(keepalive.ServerParameters{
			Time:    30 * time.Second,
			Timeout: 5 * time.Second,
		}),
		grpc.KeepaliveEnforcementPolicy(keepalive.EnforcementPolicy{
			MinTime:             5 * time.Second,
			PermitWithoutStream: true,
		}),
		grpc.MaxRecvMsgSize(1024 * 1024), // 1MB
		grpc.MaxSendMsgSize(1024 * 1024), // 1MB
	}

	s.server = grpc.NewServer(opts...)

	// Register services
	trading.RegisterTradingServiceServer(s.server, s)
	trading.RegisterStreamingServiceServer(s.server, s)

	log.Printf("gRPC server starting on port %s", port)

	// Start server in goroutine but wait to ensure it starts successfully
	serverStarted := make(chan error, 1)
	go func() {
		if err := s.server.Serve(lis); err != nil {
			log.Printf("gRPC server error: %v", err)
			serverStarted <- err
		}
	}()

	// Wait a moment to see if the server starts successfully
	select {
	case err := <-serverStarted:
		// Server failed to start immediately
		return fmt.Errorf("gRPC server failed to start: %v", err)
	case <-time.After(100 * time.Millisecond):
		// Server seems to have started successfully
		// Verify the port is actually listening
		testConn, err := net.DialTimeout("tcp", fmt.Sprintf("localhost:%s", port), 1*time.Second)
		if err != nil {
			s.server.Stop() // Stop the server if it's not accessible
			return fmt.Errorf("gRPC server started but port %s is not accessible: %v", port, err)
		}
		testConn.Close()
		log.Printf("gRPC server verified listening on port %s", port)
	}

	return nil
}

// Stop gracefully stops the gRPC server
func (s *Server) Stop() {
	if s.server != nil {
		log.Println("Stopping gRPC server...")
		s.server.GracefulStop()
	}
}

// SubmitTrade handles trade submission from NinjaTrader
func (s *Server) SubmitTrade(ctx context.Context, req *trading.Trade) (*trading.GenericResponse, error) {
	log.Printf("gRPC: Received trade submission - ID: %s, Action: %s, Quantity: %.2f", 
		req.Id, req.Action, req.Quantity)

	// Update addon connection status
	s.app.SetAddonConnected(true)

	// Convert protobuf Trade to internal trade format
	internalTrade := convertProtoToInternalTrade(req)
	
	// Convert internal trade to main.Trade format for app
	mainTrade := convertInternalToMainTrade(internalTrade)

	// Add to trade queue
	err := s.app.AddToTradeQueue(mainTrade)
	if err != nil {
		log.Printf("gRPC: Failed to add trade to queue: %v", err)
		return &trading.GenericResponse{
			Status:  "error",
			Message: "Failed to add trade to queue: " + err.Error(),
		}, status.Error(codes.Internal, "Failed to process trade")
	}

	// Add to trade history
	s.app.AddToTradeHistory(mainTrade)

	// Send trade to active streams
	s.broadcastTradeToStreams(req)

	log.Printf("gRPC: Trade processed successfully - ID: %s", req.Id)

	return &trading.GenericResponse{
		Status:  "success",
		Message: "Trade processed successfully",
		Metadata: map[string]string{
			"trade_id":   req.Id,
			"timestamp":  time.Now().Format(time.RFC3339),
			"queue_size": fmt.Sprintf("%d", s.app.GetQueueSize()),
		},
	}, nil
}

// GetTrades handles streaming trade requests from MT5
func (s *Server) GetTrades(stream trading.TradingService_GetTradesServer) error {
	log.Println("gRPC: New trade stream connected from MT5")
	
	// Mark hedgebot as active when MT5 connects via gRPC streaming
	s.app.SetHedgebotActive(true)
	log.Println("gRPC: Hedgebot marked as active via streaming connection")
	
	// Create channel for this stream
	streamChan := make(chan *trading.Trade, 100)
	streamID := fmt.Sprintf("stream_%d", time.Now().UnixNano())
	
	s.streamsMux.Lock()
	s.tradeStreams[streamID] = streamChan
	s.streamsMux.Unlock()

	// Clean up on exit
	defer func() {
		s.streamsMux.Lock()
		delete(s.tradeStreams, streamID)
		close(streamChan)
		streamCount := len(s.tradeStreams)
		s.streamsMux.Unlock()
		
		log.Printf("gRPC: Trade stream %s disconnected", streamID)
		
		// Mark hedgebot as inactive when stream disconnects
		// Only if this was the last active stream
		if streamCount == 0 {
			s.app.SetHedgebotActive(false)
			log.Println("gRPC: Hedgebot marked as inactive - no active streams")
		}
	}()

	// Start goroutine to monitor app trade queue and forward to stream
	go s.forwardTradesToStream(streamChan, streamID)

	// Handle incoming health requests and send trades
	for {
		select {
		case <-stream.Context().Done():
			log.Printf("gRPC: Trade stream %s context cancelled", streamID)
			return nil
		case trade := <-streamChan:
			if trade == nil {
				log.Printf("gRPC: Received nil trade in stream %s, continuing", streamID)
				continue
			}
			
			log.Printf("gRPC: Sending trade to MT5 stream - ID: %s, Action: %s", trade.Id, trade.Action)
			if err := stream.Send(trade); err != nil {
				log.Printf("gRPC: Error sending trade to stream: %v", err)
				return err
			}
		}
	}
}

// forwardTradesToStream monitors the app trade queue and forwards trades to the stream
func (s *Server) forwardTradesToStream(streamChan chan<- *trading.Trade, streamID string) {
	log.Printf("gRPC: Starting trade forwarding for stream %s", streamID)
	
	ticker := time.NewTicker(200 * time.Millisecond) // Poll interval similar to HTTP
	defer ticker.Stop()
	
	for {
		select {
		case <-ticker.C:
			// Check if stream is still active
			s.streamsMux.RLock()
			_, exists := s.tradeStreams[streamID]
			s.streamsMux.RUnlock()
			
			if !exists {
				log.Printf("gRPC: Stream %s no longer exists, stopping trade forwarding", streamID)
				return
			}
			
			// Try to get trade from app queue (non-blocking) - NO POLLING LOGS
			if trade := s.pollTradeFromApp(); trade != nil {
				select {
				case streamChan <- trade:
					log.Printf("gRPC: Forwarded trade %s to stream %s", trade.Id, streamID)
				default:
					log.Printf("gRPC: Stream %s buffer full, dropping trade %s", streamID, trade.Id)
				}
			}
		}
	}
}

// pollTradeFromApp attempts to get a trade from the app's trade queue
func (s *Server) pollTradeFromApp() *trading.Trade {
	tradeInterface := s.app.PollTradeFromQueue()
	if tradeInterface == nil {
		return nil
	}
	
	// The app.go returns main.Trade, we need to convert it to protobuf format
	// First convert to InternalTrade, then to proto
	internal := &InternalTrade{}
	
	// Use JSON marshaling to convert from main.Trade to InternalTrade
	jsonBytes, err := json.Marshal(tradeInterface)
	if err != nil {
		log.Printf("gRPC: Failed to marshal trade from queue: %v", err)
		return nil
	}
	
	if err := json.Unmarshal(jsonBytes, internal); err != nil {
		log.Printf("gRPC: Failed to unmarshal trade to internal format: %v", err)
		return nil
	}
	
	// Now convert InternalTrade to protobuf Trade
	return ConvertInternalToProtoTrade(internal)
}

// SubmitTradeResult handles trade execution results from MT5
func (s *Server) SubmitTradeResult(ctx context.Context, req *trading.MT5TradeResult) (*trading.GenericResponse, error) {
	log.Printf("gRPC: Received trade result - Ticket: %d, Status: %s", req.Ticket, req.Status)

	// Update hedgebot active status
	s.app.SetHedgebotActive(true)

	// Convert protobuf to internal format and handle
	result := convertProtoToInternalMT5Result(req)
	err := s.app.HandleMT5TradeResult(result)
	if err != nil {
		log.Printf("gRPC: Failed to handle MT5 trade result: %v", err)
		return &trading.GenericResponse{
			Status:  "error",
			Message: "Failed to handle trade result: " + err.Error(),
		}, status.Error(codes.Internal, "Failed to process trade result")
	}

	return &trading.GenericResponse{
		Status:  "success",
		Message: "Trade result processed successfully",
	}, nil
}

// NotifyHedgeClose handles hedge closure notifications from MT5
func (s *Server) NotifyHedgeClose(ctx context.Context, req *trading.HedgeCloseNotification) (*trading.GenericResponse, error) {
	log.Printf("gRPC: Received hedge close notification - BaseID: %s, Reason: %s", 
		req.BaseId, req.ClosureReason)

	// Update hedgebot active status
	s.app.SetHedgebotActive(true)

	// Convert and handle notification
	notification := convertProtoToInternalHedgeClose(req)
	err := s.app.HandleHedgeCloseNotification(notification)
	if err != nil {
		log.Printf("gRPC: Failed to handle hedge close notification: %v", err)
		return &trading.GenericResponse{
			Status:  "error",
			Message: "Failed to handle hedge close notification: " + err.Error(),
		}, status.Error(codes.Internal, "Failed to process notification")
	}

	return &trading.GenericResponse{
		Status:  "success",
		Message: "Hedge close notification processed successfully",
	}, nil
}

// SubmitElasticUpdate handles elastic hedge updates
func (s *Server) SubmitElasticUpdate(ctx context.Context, req *trading.ElasticHedgeUpdate) (*trading.GenericResponse, error) {
	log.Printf("gRPC: Received elastic update - BaseID: %s, ProfitLevel: %d", 
		req.BaseId, req.ProfitLevel)

	// Convert and handle update
	update := convertProtoToInternalElasticUpdate(req)
	err := s.app.HandleElasticUpdate(update)
	if err != nil {
		log.Printf("gRPC: Failed to handle elastic update: %v", err)
		return &trading.GenericResponse{
			Status:  "error",
			Message: "Failed to handle elastic update: " + err.Error(),
		}, status.Error(codes.Internal, "Failed to process elastic update")
	}

	return &trading.GenericResponse{
		Status:  "success",
		Message: "Elastic update processed successfully",
	}, nil
}

// SubmitTrailingUpdate handles trailing stop updates
func (s *Server) SubmitTrailingUpdate(ctx context.Context, req *trading.TrailingStopUpdate) (*trading.GenericResponse, error) {
	log.Printf("gRPC: Received trailing update - BaseID: %s, NewStopPrice: %.2f", 
		req.BaseId, req.NewStopPrice)

	// Convert and handle update
	update := convertProtoToInternalTrailingUpdate(req)
	err := s.app.HandleTrailingStopUpdate(update)
	if err != nil {
		log.Printf("gRPC: Failed to handle trailing update: %v", err)
		return &trading.GenericResponse{
			Status:  "error",
			Message: "Failed to handle trailing update: " + err.Error(),
		}, status.Error(codes.Internal, "Failed to process trailing update")
	}

	return &trading.GenericResponse{
		Status:  "success",
		Message: "Trailing update processed successfully",
	}, nil
}

// HealthCheck handles health check requests
func (s *Server) HealthCheck(ctx context.Context, req *trading.HealthRequest) (*trading.HealthResponse, error) {
	log.Printf("gRPC: Health check from source: %s", req.Source)

	// Update connection status based on source
	switch req.Source {
	case "hedgebot", "MT5_EA":
		s.app.SetHedgebotActive(true)
		log.Printf("gRPC: Marked hedgebot as active via health check from source: %s", req.Source)
	case "addon", "NT_ADDON", "nt_addon_init":
		s.app.SetAddonConnected(true)
		log.Printf("gRPC: Marked addon as connected via health check from source: %s", req.Source)
	}

	response := &trading.HealthResponse{
		Status:      "healthy",
		QueueSize:   int32(s.app.GetQueueSize()),
		NetPosition: int32(s.app.GetNetPosition()),
		HedgeSize:   s.app.GetHedgeSize(),
	}

	return response, nil
}

// GetSettings handles settings requests (placeholder implementation)
func (s *Server) GetSettings(ctx context.Context, req *trading.SettingsRequest) (*trading.SettingsResponse, error) {
	log.Printf("gRPC: Settings request for: %s", req.SettingName)

	// TODO: Implement actual settings retrieval
	return &trading.SettingsResponse{
		SettingName:  req.SettingName,
		SettingValue: "default_value",
		Success:      true,
	}, nil
}

// SystemHeartbeat handles system heartbeat requests
func (s *Server) SystemHeartbeat(ctx context.Context, req *trading.HeartbeatRequest) (*trading.HeartbeatResponse, error) {
	log.Printf("gRPC: Heartbeat from component: %s, Status: %s", req.Component, req.Status)

	return &trading.HeartbeatResponse{
		Status:  "acknowledged",
		Message: "Heartbeat received successfully",
	}, nil
}

// NTCloseHedge handles hedge closure requests from NinjaTrader
func (s *Server) NTCloseHedge(ctx context.Context, req *trading.HedgeCloseNotification) (*trading.GenericResponse, error) {
	log.Printf("gRPC: NT close hedge request - BaseID: %s", req.BaseId)

	// Convert and handle request
	request := convertProtoToInternalHedgeClose(req)
	err := s.app.HandleNTCloseHedgeRequest(request)
	if err != nil {
		log.Printf("gRPC: Failed to handle NT close hedge request: %v", err)
		return &trading.GenericResponse{
			Status:  "error",
			Message: "Failed to handle close hedge request: " + err.Error(),
		}, status.Error(codes.Internal, "Failed to process close hedge request")
	}

	return &trading.GenericResponse{
		Status:  "success",
		Message: "Close hedge request processed successfully",
	}, nil
}

// broadcastTradeToStreams sends a trade to all active streams
func (s *Server) broadcastTradeToStreams(trade *trading.Trade) {
	s.streamsMux.RLock()
	defer s.streamsMux.RUnlock()

	for streamID, streamChan := range s.tradeStreams {
		select {
		case streamChan <- trade:
			log.Printf("gRPC: Trade broadcasted to stream %s", streamID)
		default:
			log.Printf("gRPC: Stream %s buffer full, skipping trade", streamID)
		}
	}
}

// BroadcastMT5CloseNotification sends an MT5 closure notification to NinjaTrader via streams
func (s *Server) BroadcastMT5CloseNotification(notification interface{}) {
	log.Printf("gRPC: Broadcasting MT5 close notification: %+v", notification)
	
	// Convert the notification to a trading.Trade message
	protoTrade := convertMT5CloseNotificationToProtoTrade(notification)
	
	// Broadcast to all active streams (primarily NinjaTrader)
	s.broadcastTradeToStreams(protoTrade)
}

// TradingStream handles bidirectional streaming for real-time updates
func (s *Server) TradingStream(stream trading.StreamingService_TradingStreamServer) error {
	log.Println("gRPC: New bidirectional trading stream connected")

	// Create channel for this stream
	streamChan := make(chan *trading.Trade, 100)
	streamID := fmt.Sprintf("bidir_stream_%d", time.Now().UnixNano())

	s.streamsMux.Lock()
	s.tradeStreams[streamID] = streamChan
	s.streamsMux.Unlock()

	// Clean up on exit
	defer func() {
		s.streamsMux.Lock()
		delete(s.tradeStreams, streamID)
		close(streamChan)
		s.streamsMux.Unlock()
		log.Printf("gRPC: Bidirectional trading stream %s disconnected", streamID)
	}()

	// Handle the stream
	errChan := make(chan error, 2)

	// Goroutine for receiving trades from client
	go func() {
		for {
			trade, err := stream.Recv()
			if err != nil {
				errChan <- err
				return
			}

			// Process incoming trade (similar to SubmitTrade)
			log.Printf("gRPC: Received trade via bidirectional stream - ID: %s", trade.Id)
			
			// Convert and handle trade
			internalTrade := convertProtoToInternalTrade(trade)
			if err := s.app.AddToTradeQueue(internalTrade); err != nil {
				log.Printf("gRPC: Failed to add streamed trade to queue: %v", err)
			} else {
				s.app.AddToTradeHistory(internalTrade)
			}
		}
	}()

	// Goroutine for sending trades to client
	go func() {
		for {
			select {
			case <-stream.Context().Done():
				errChan <- stream.Context().Err()
				return
			case trade := <-streamChan:
				if err := stream.Send(trade); err != nil {
					errChan <- err
					return
				}
			}
		}
	}()

	// Wait for error or context cancellation
	return <-errChan
}

// StatusStream handles streaming status updates
func (s *Server) StatusStream(stream trading.StreamingService_StatusStreamServer) error {
	log.Println("gRPC: New status stream connected")

	// Create channel for this stream
	streamChan := make(chan *trading.HealthResponse, 100)
	streamID := fmt.Sprintf("status_stream_%d", time.Now().UnixNano())

	s.healthStreamsMux.Lock()
	s.healthStreams[streamID] = streamChan
	s.healthStreamsMux.Unlock()

	// Clean up on exit
	defer func() {
		s.healthStreamsMux.Lock()
		delete(s.healthStreams, streamID)
		close(streamChan)
		s.healthStreamsMux.Unlock()
		log.Printf("gRPC: Status stream %s disconnected", streamID)
	}()

	// Send periodic status updates
	ticker := time.NewTicker(5 * time.Second)
	defer ticker.Stop()

	for {
		select {
		case <-stream.Context().Done():
			return nil
		case <-ticker.C:
			// Send status update
			status := &trading.HealthResponse{
				Status:      "healthy",
				QueueSize:   int32(s.app.GetQueueSize()),
				NetPosition: int32(s.app.GetNetPosition()),
				HedgeSize:   s.app.GetHedgeSize(),
			}

			if err := stream.Send(status); err != nil {
				log.Printf("gRPC: Error sending status update: %v", err)
				return err
			}
		case status := <-streamChan:
			if err := stream.Send(status); err != nil {
				log.Printf("gRPC: Error sending status update: %v", err)
				return err
			}
		}
	}
}

// ElasticUpdatesStream handles streaming elastic hedge updates
func (s *Server) ElasticUpdatesStream(stream trading.StreamingService_ElasticUpdatesStreamServer) error {
	log.Println("gRPC: New elastic updates stream connected")

	for {
		update, err := stream.Recv()
		if err != nil {
			log.Printf("gRPC: Elastic updates stream error: %v", err)
			return err
		}

		// Process elastic update
		internalUpdate := convertProtoToInternalElasticUpdate(update)
		if err := s.app.HandleElasticUpdate(internalUpdate); err != nil {
			response := &trading.GenericResponse{
				Status:  "error",
				Message: "Failed to process elastic update: " + err.Error(),
			}
			if err := stream.Send(response); err != nil {
				return err
			}
		} else {
			response := &trading.GenericResponse{
				Status:  "success",
				Message: "Elastic update processed successfully",
			}
			if err := stream.Send(response); err != nil {
				return err
			}
		}
	}
}

// TrailingUpdatesStream handles streaming trailing stop updates
func (s *Server) TrailingUpdatesStream(stream trading.StreamingService_TrailingUpdatesStreamServer) error {
	log.Println("gRPC: New trailing updates stream connected")

	for {
		update, err := stream.Recv()
		if err != nil {
			log.Printf("gRPC: Trailing updates stream error: %v", err)
			return err
		}

		// Process trailing update
		internalUpdate := convertProtoToInternalTrailingUpdate(update)
		if err := s.app.HandleTrailingStopUpdate(internalUpdate); err != nil {
			response := &trading.GenericResponse{
				Status:  "error",
				Message: "Failed to process trailing update: " + err.Error(),
			}
			if err := stream.Send(response); err != nil {
				return err
			}
		} else {
			response := &trading.GenericResponse{
				Status:  "success",
				Message: "Trailing update processed successfully",
			}
			if err := stream.Send(response); err != nil {
				return err
			}
		}
	}
}