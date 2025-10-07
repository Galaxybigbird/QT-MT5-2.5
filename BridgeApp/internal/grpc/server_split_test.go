package grpc

import (
	"fmt"
	"testing"

	trading "BridgeApp/internal/grpc/proto"
)

type stubApp struct {
	trades  []interface{}
	history []interface{}
}

func (s *stubApp) GetTradeQueue() chan interface{} { return make(chan interface{}, 1) }
func (s *stubApp) PollTradeFromQueue() interface{} { return nil }
func (s *stubApp) AddToTradeQueue(trade interface{}) error {
	s.trades = append(s.trades, trade)
	return nil
}
func (s *stubApp) GetNetPosition() int                            { return 0 }
func (s *stubApp) GetHedgeSize() float64                          { return 0 }
func (s *stubApp) GetQueueSize() int                              { return len(s.trades) }
func (s *stubApp) IsAddonConnected() bool                         { return true }
func (s *stubApp) IsHedgebotActive() bool                         { return true }
func (s *stubApp) SetAddonConnected(bool)                         {}
func (s *stubApp) SetHedgebotActive(bool)                         {}
func (s *stubApp) AddToTradeHistory(trade interface{})            { s.history = append(s.history, trade) }
func (s *stubApp) HandleHedgeCloseNotification(interface{}) error { return nil }
func (s *stubApp) HandleMT5TradeResult(interface{}) error         { return nil }
func (s *stubApp) HandleElasticUpdate(interface{}) error          { return nil }
func (s *stubApp) HandleTrailingStopUpdate(interface{}) error     { return nil }
func (s *stubApp) HandleCloseHedgeRequest(interface{}) error      { return nil }

func TestEnqueueTradeWithSplitDeltaTracking(t *testing.T) {
	app := &stubApp{}
	server := NewGRPCServer(app)

	fill1 := &trading.Trade{Id: "pos1-fill1", BaseId: "pos1", Quantity: 1, Action: "sell"}
	if err := server.enqueueTradeWithSplit(fill1); err != nil {
		t.Fatalf("enqueueTradeWithSplit fill1 failed: %v", err)
	}
	if len(app.trades) != 1 {
		t.Fatalf("expected 1 trade after fill1, got %d", len(app.trades))
	}
	first := app.trades[0].(*InternalTrade)
	if first.ContractNum != 1 || first.TotalQuantity != 1 {
		t.Fatalf("unexpected first trade: %+v", first)
	}
	if got := server.mirroredHedgeQty["pos1"]; got != 1 {
		t.Fatalf("expected mirrored count 1 after fill1, got %d", got)
	}

	fill2 := &trading.Trade{Id: "pos1-fill2", BaseId: "pos1", Quantity: 1, Action: "sell"}
	if err := server.enqueueTradeWithSplit(fill2); err != nil {
		t.Fatalf("enqueueTradeWithSplit fill2 failed: %v", err)
	}
	if len(app.trades) != 2 {
		t.Fatalf("expected 2 trades after fill2, got %d", len(app.trades))
	}
	second := app.trades[1].(*InternalTrade)
	if second.ContractNum != 2 || second.TotalQuantity != 2 {
		t.Fatalf("unexpected second trade: %+v", second)
	}
	if got := server.mirroredHedgeQty["pos1"]; got != 2 {
		t.Fatalf("expected mirrored count 2 after fill2, got %d", got)
	}

	snapshot2 := &trading.Trade{Id: "pos1-snap2", BaseId: "pos1", Quantity: 2, TotalQuantity: 2, Action: "sell"}
	if err := server.enqueueTradeWithSplit(snapshot2); err != nil {
		t.Fatalf("enqueueTradeWithSplit snapshot2 failed: %v", err)
	}
	if len(app.trades) != 2 {
		t.Fatalf("expected no new trades from snapshot2, got %d", len(app.trades))
	}
	if got := server.mirroredHedgeQty["pos1"]; got != 2 {
		t.Fatalf("expected mirrored count to remain 2 after snapshot2, got %d", got)
	}

	fill3 := &trading.Trade{Id: "pos1-fill3", BaseId: "pos1", Quantity: 1, Action: "sell"}
	fill4 := &trading.Trade{Id: "pos1-fill4", BaseId: "pos1", Quantity: 1, Action: "sell"}
	if err := server.enqueueTradeWithSplit(fill3); err != nil {
		t.Fatalf("enqueueTradeWithSplit fill3 failed: %v", err)
	}
	if err := server.enqueueTradeWithSplit(fill4); err != nil {
		t.Fatalf("enqueueTradeWithSplit fill4 failed: %v", err)
	}
	if len(app.trades) != 4 {
		t.Fatalf("expected 4 trades after scaling in, got %d", len(app.trades))
	}
	third := app.trades[2].(*InternalTrade)
	fourth := app.trades[3].(*InternalTrade)
	if third.ContractNum != 3 || third.TotalQuantity != 3 {
		t.Fatalf("unexpected third trade after fill3: %+v", third)
	}
	if fourth.ContractNum != 4 || fourth.TotalQuantity != 4 {
		t.Fatalf("unexpected fourth trade after fill4: %+v", fourth)
	}
	if got := server.mirroredHedgeQty["pos1"]; got != 4 {
		t.Fatalf("expected mirrored count 4 after scaling in, got %d", got)
	}

	snapshot4 := &trading.Trade{Id: "pos1-snap4", BaseId: "pos1", Quantity: 4, TotalQuantity: 4, Action: "sell"}
	if err := server.enqueueTradeWithSplit(snapshot4); err != nil {
		t.Fatalf("enqueueTradeWithSplit snapshot4 failed: %v", err)
	}
	if len(app.trades) != 4 {
		t.Fatalf("expected no new trades from snapshot4, got %d", len(app.trades))
	}

	lowerSnapshot := &trading.Trade{Id: "pos1-snap2b", BaseId: "pos1", Quantity: 2, TotalQuantity: 2, Action: "sell"}
	if err := server.enqueueTradeWithSplit(lowerSnapshot); err != nil {
		t.Fatalf("enqueueTradeWithSplit lowerSnapshot failed: %v", err)
	}
	if got := server.mirroredHedgeQty["pos1"]; got != 4 {
		t.Fatalf("expected mirrored count to stay 4 until MT5 confirms, got %d", got)
	}
	if len(app.trades) != 4 {
		t.Fatalf("expected no new trades from lower snapshot, got %d", len(app.trades))
	}

	server.trackHedgeClosure("pos1", 2, "MT5_position_closed")
	if got := server.mirroredHedgeQty["pos1"]; got != 2 {
		t.Fatalf("expected mirrored count 2 after MT5 close, got %d", got)
	}
	if got := server.nextContractIndex["pos1"]; got != 3 {
		t.Fatalf("expected next contract index 3 after partial close, got %d", got)
	}

	fill5 := &trading.Trade{Id: "pos1-fill5", BaseId: "pos1", Quantity: 1, Action: "sell"}
	if err := server.enqueueTradeWithSplit(fill5); err != nil {
		t.Fatalf("enqueueTradeWithSplit fill5 failed: %v", err)
	}
	if len(app.trades) != 5 {
		t.Fatalf("expected 5 trades after re-adding, got %d", len(app.trades))
	}
	resumed := app.trades[4].(*InternalTrade)
	if resumed.ContractNum != 3 || resumed.TotalQuantity != 3 {
		t.Fatalf("unexpected resumed trade: %+v", resumed)
	}
	if got := server.mirroredHedgeQty["pos1"]; got != 3 {
		t.Fatalf("expected mirrored count 3 after fill5, got %d", got)
	}
}

func TestEnqueueTradeWithSplitHandlesAggregateSnapshots(t *testing.T) {
	app := &stubApp{}
	server := NewGRPCServer(app)

	initial := &trading.Trade{Id: "pos3", BaseId: "pos3", Quantity: 2, TotalQuantity: 2, Action: "sell"}
	if err := server.enqueueTradeWithSplit(initial); err != nil {
		t.Fatalf("initial enqueue failed: %v", err)
	}
	if len(app.trades) != 2 {
		t.Fatalf("expected 2 hedges for initial snapshot, got %d", len(app.trades))
	}
	for i := 0; i < 2; i++ {
		edge := app.trades[i].(*InternalTrade)
		expectedContract := i + 1
		if edge.ContractNum != expectedContract {
			t.Fatalf("expected contract %d, got %d", expectedContract, edge.ContractNum)
		}
		if edge.TotalQuantity != 2 {
			t.Fatalf("expected total quantity 2 for hedge %d, got %d", expectedContract, edge.TotalQuantity)
		}
	}
	if got := server.mirroredHedgeQty["pos3"]; got != 2 {
		t.Fatalf("expected tracked hedges 2 after initial snapshot, got %d", got)
	}

	aggregated := &trading.Trade{Id: "pos3", BaseId: "pos3", Quantity: 3, TotalQuantity: 5, Action: "sell"}
	if err := server.enqueueTradeWithSplit(aggregated); err != nil {
		t.Fatalf("aggregated snapshot enqueue failed: %v", err)
	}
	if len(app.trades) != 5 {
		t.Fatalf("expected 5 hedges after aggregated snapshot, got %d", len(app.trades))
	}
	for i := 2; i < 5; i++ {
		edge := app.trades[i].(*InternalTrade)
		expectedContract := i + 1
		if edge.ContractNum != expectedContract {
			t.Fatalf("expected contract %d for aggregated hedge, got %d", expectedContract, edge.ContractNum)
		}
		if edge.TotalQuantity != 5 {
			t.Fatalf("expected total quantity 5 for aggregated hedge, got %d", edge.TotalQuantity)
		}
	}
	if got := server.mirroredHedgeQty["pos3"]; got != 5 {
		t.Fatalf("expected tracked hedges 5 after aggregated snapshot, got %d", got)
	}

	duplicateSnapshot := &trading.Trade{Id: "pos3", BaseId: "pos3", Quantity: 5, TotalQuantity: 5, Action: "sell"}
	if err := server.enqueueTradeWithSplit(duplicateSnapshot); err != nil {
		t.Fatalf("duplicate snapshot enqueue failed: %v", err)
	}
	if len(app.trades) != 5 {
		t.Fatalf("expected duplicate snapshot to be ignored, hedges=%d", len(app.trades))
	}
	if got := server.mirroredHedgeQty["pos3"]; got != 5 {
		t.Fatalf("expected tracked hedges 5 after duplicate snapshot, got %d", got)
	}

	shrinkingSnapshot := &trading.Trade{Id: "pos3", BaseId: "pos3", Quantity: 2, TotalQuantity: 2, Action: "sell"}
	if err := server.enqueueTradeWithSplit(shrinkingSnapshot); err != nil {
		t.Fatalf("shrinking snapshot enqueue failed: %v", err)
	}
	if got := server.mirroredHedgeQty["pos3"]; got != 5 {
		t.Fatalf("expected tracked hedges to remain 5 pending MT5 close, got %d", got)
	}

	server.trackHedgeClosure("pos3", 3, "MT5_position_closed")
	if got := server.mirroredHedgeQty["pos3"]; got != 2 {
		t.Fatalf("expected tracked hedges 2 after MT5 closure, got %d", got)
	}
	if got := server.nextContractIndex["pos3"]; got != 3 {
		t.Fatalf("expected next contract index 3 after MT5 closure, got %d", got)
	}

	reopen := &trading.Trade{Id: "pos3", BaseId: "pos3", Quantity: 2, TotalQuantity: 4, Action: "sell"}
	if err := server.enqueueTradeWithSplit(reopen); err != nil {
		t.Fatalf("reopen enqueue failed: %v", err)
	}
	if len(app.trades) != 7 {
		t.Fatalf("expected 7 hedges after reopening, got %d", len(app.trades))
	}
	reopened3 := app.trades[5].(*InternalTrade)
	reopened4 := app.trades[6].(*InternalTrade)
	if reopened3.ContractNum != 3 || reopened4.ContractNum != 4 {
		t.Fatalf("expected reopened contracts 3 & 4, got %d and %d", reopened3.ContractNum, reopened4.ContractNum)
	}
	if reopened3.TotalQuantity != 4 || reopened4.TotalQuantity != 4 {
		t.Fatalf("expected reopened hedges to reflect total 4, got %d and %d", reopened3.TotalQuantity, reopened4.TotalQuantity)
	}
	if got := server.mirroredHedgeQty["pos3"]; got != 4 {
		t.Fatalf("expected tracked hedges 4 after reopening, got %d", got)
	}
}

func TestTrackHedgeClosureResetsContractIndex(t *testing.T) {
	app := &stubApp{}
	server := NewGRPCServer(app)

	trade := &trading.Trade{Id: "pos2", BaseId: "pos2", Quantity: 4, TotalQuantity: 4, Action: "sell"}
	if err := server.enqueueTradeWithSplit(trade); err != nil {
		t.Fatalf("enqueueTradeWithSplit (qty=4) failed: %v", err)
	}
	if len(app.trades) != 4 {
		t.Fatalf("expected 4 trades for qty=4, got %d", len(app.trades))
	}
	for i := 0; i < 4; i++ {
		internal := app.trades[i].(*InternalTrade)
		expectedID := "pos2"
		if i > 0 {
			expectedID = "pos2-" + fmt.Sprint(i+1)
		}
		if internal.ID != expectedID {
			t.Fatalf("unexpected trade id at index %d: %+v", i, internal)
		}
		if internal.ContractNum != i+1 {
			t.Fatalf("expected contract %d got %d", i+1, internal.ContractNum)
		}
		if internal.TotalQuantity != 4 {
			t.Fatalf("expected total quantity 4, got %d", internal.TotalQuantity)
		}
	}

	server.trackHedgeClosure("pos2", 2, "MT5_position_closed")
	if got := server.mirroredHedgeQty["pos2"]; got != 2 {
		t.Fatalf("expected mirrored count 2 after partial close, got %d", got)
	}
	if got := server.nextContractIndex["pos2"]; got != 3 {
		t.Fatalf("expected next contract index 3 after partial close, got %d", got)
	}

	if err := server.enqueueTradeWithSplit(trade); err != nil {
		t.Fatalf("enqueueTradeWithSplit after partial close failed: %v", err)
	}
	if len(app.trades) != 6 {
		t.Fatalf("expected 6 trades after reopening, got %d", len(app.trades))
	}
	resumed3 := app.trades[4].(*InternalTrade)
	resumed4 := app.trades[5].(*InternalTrade)
	if resumed3.ContractNum != 3 || resumed4.ContractNum != 4 {
		t.Fatalf("expected reopened contracts 3 & 4, got %d and %d", resumed3.ContractNum, resumed4.ContractNum)
	}
	if resumed3.TotalQuantity != 4 || resumed4.TotalQuantity != 4 {
		t.Fatalf("expected reopened trades to reflect total 4, got %d and %d", resumed3.TotalQuantity, resumed4.TotalQuantity)
	}
}
