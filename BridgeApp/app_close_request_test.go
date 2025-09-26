package main

import "testing"

func drainTrade(app *App) (Trade, bool) {
	out := app.PollTradeFromQueue()
	if out == nil {
		return Trade{}, false
	}
	trade, ok := out.(Trade)
	return trade, ok
}

func TestHandleCloseHedgeRequestWithExplicitTicket(t *testing.T) {
	a := NewApp()
	baseID := "BASE_EXPLICIT"
	ticket := uint64(9001)

	a.mt5TicketMux.Lock()
	a.mt5TicketToBaseId[ticket] = baseID
	a.mt5TicketMux.Unlock()

	req := map[string]interface{}{
		"BaseID":              baseID,
		"ClosedHedgeQuantity": 1.0,
		"NTInstrumentSymbol":  "NQ",
		"NTAccountName":       "Sim101",
		"MT5Ticket":           float64(ticket),
	}

	if err := a.HandleCloseHedgeRequest(req); err != nil {
		t.Fatalf("HandleCloseHedgeRequest returned error: %v", err)
	}

	trade, ok := drainTrade(a)
	if !ok {
		t.Fatalf("expected one trade in queue for explicit ticket request")
	}
	if trade.MT5Ticket != ticket || trade.BaseID != baseID || trade.Action != "CLOSE_HEDGE" {
		t.Fatalf("unexpected trade payload: %+v", trade)
	}

	a.mt5TicketMux.RLock()
	if _, exists := a.mt5TicketToBaseId[ticket]; exists {
		a.mt5TicketMux.RUnlock()
		t.Fatalf("expected ticket %d to be removed from reverse mapping", ticket)
	}
	a.mt5TicketMux.RUnlock()

	a.clientCloseMux.Lock()
	if _, exists := a.clientInitiatedTickets[ticket]; !exists {
		a.clientCloseMux.Unlock()
		t.Fatalf("expected ticket %d to be tracked for acknowledgement", ticket)
	}
	a.clientCloseMux.Unlock()
}

func TestHandleCloseHedgeRequestAllocatesFromPool(t *testing.T) {
	a := NewApp()
	baseID := "BASE_POOL"
	tickets := []uint64{101, 202}

	a.mt5TicketMux.Lock()
	a.baseIdToTickets[baseID] = append([]uint64{}, tickets...)
	for _, tk := range tickets {
		a.mt5TicketToBaseId[tk] = baseID
	}
	a.mt5TicketMux.Unlock()

	req := map[string]interface{}{
		"BaseID":              baseID,
		"ClosedHedgeQuantity": 2.0,
		"NTInstrumentSymbol":  "ES",
		"NTAccountName":       "Sim101",
	}

	if err := a.HandleCloseHedgeRequest(req); err != nil {
		t.Fatalf("HandleCloseHedgeRequest returned error: %v", err)
	}

	first, ok := drainTrade(a)
	if !ok {
		t.Fatalf("expected first CLOSE_HEDGE trade")
	}
	second, ok := drainTrade(a)
	if !ok {
		t.Fatalf("expected second CLOSE_HEDGE trade")
	}

	seen := []uint64{first.MT5Ticket, second.MT5Ticket}
	if seen[0] != tickets[0] || seen[1] != tickets[1] {
		t.Fatalf("expected tickets %v, got %v", tickets, seen)
	}

	if _, ok = drainTrade(a); ok {
		t.Fatalf("expected exactly two trades in queue")
	}

	a.mt5TicketMux.RLock()
	if len(a.baseIdToTickets[baseID]) != 0 {
		a.mt5TicketMux.RUnlock()
		t.Fatalf("expected no tickets remaining in pool for %s", baseID)
	}
	for _, tk := range tickets {
		if _, exists := a.mt5TicketToBaseId[tk]; exists {
			a.mt5TicketMux.RUnlock()
			t.Fatalf("expected ticket %d to be removed from reverse mapping", tk)
		}
	}
	a.mt5TicketMux.RUnlock()

	a.clientCloseMux.Lock()
	if len(a.clientInitiatedTickets) != 2 {
		a.clientCloseMux.Unlock()
		t.Fatalf("expected two client-initiated tickets to be tracked, got %d", len(a.clientInitiatedTickets))
	}
	for _, tk := range tickets {
		if _, exists := a.clientInitiatedTickets[tk]; !exists {
			a.clientCloseMux.Unlock()
			t.Fatalf("expected ticket %d to be tracked for acknowledgement", tk)
		}
	}
	a.clientCloseMux.Unlock()
}

func TestHandleCloseHedgeRequestErrorsWhenPoolEmpty(t *testing.T) {
	a := NewApp()

	req := map[string]interface{}{
		"BaseID":              "BASE_EMPTY",
		"ClosedHedgeQuantity": 1.0,
		"NTInstrumentSymbol":  "NQ",
		"NTAccountName":       "Sim101",
	}

	if err := a.HandleCloseHedgeRequest(req); err == nil {
		t.Fatalf("expected error when no tickets are available")
	}

	if _, ok := drainTrade(a); ok {
		t.Fatalf("expected no trades enqueued when request fails")
	}
}
