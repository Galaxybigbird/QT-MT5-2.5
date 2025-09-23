package logging

import (
	"bufio"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"sync"
	"time"
)

// Event is the unified log event shape written by the bridge collector.
type Event struct {
	TimestampNS   int64                  `json:"timestamp_ns"`
	Source        string                 `json:"source"`
	Level         string                 `json:"level"`
	Component     string                 `json:"component,omitempty"`
	Message       string                 `json:"message"`
	BaseID        string                 `json:"base_id,omitempty"`
	TradeID       string                 `json:"trade_id,omitempty"`
	NTOrderID     string                 `json:"nt_order_id,omitempty"`
	MT5Ticket     uint64                 `json:"mt5_ticket,omitempty"`
	QueueSize     int                    `json:"queue_size,omitempty"`
	NetPosition   int                    `json:"net_position,omitempty"`
	HedgeSize     float64                `json:"hedge_size,omitempty"`
	ErrorCode     string                 `json:"error_code,omitempty"`
	Stack         string                 `json:"stack,omitempty"`
	Tags          map[string]string      `json:"tags,omitempty"`
	Extra         map[string]interface{} `json:"extra,omitempty"`
	SchemaVersion string                 `json:"schema_version,omitempty"`
	CorrelationID string                 `json:"correlation_id,omitempty"`
}

type stateSnapshot func() (queueSize int, netPosition int, hedgeSize float64)

// Logger is a simple JSONL writer with daily rotation.
type Logger struct {
	mu       sync.Mutex
	started  bool
	source   string
	dir      string
	fileDate string
	f        *os.File
	w        *bufio.Writer
	ch       chan Event
	quit     chan struct{}
	state    stateSnapshot
}

var defaultLogger = &Logger{}

// CurrentSchemaVersion marks the schema version for Event enrichment.
const CurrentSchemaVersion = "1.0"

// L returns the process-wide logger.
func L() *Logger { return defaultLogger }

// EnsureStarted initializes the logger if not yet started.
func (l *Logger) EnsureStarted(source string) {
	l.mu.Lock()
	defer l.mu.Unlock()
	if l.started {
		return
	}
	l.source = source
	// Initialize Sentry (best-effort) when logger starts
	initSentryFromEnvOnce()
	// Determine logs directory with override:
	// 1) BRIDGE_LOG_DIR env var, if set
	// 2) "logs" next to the current executable (more stable than CWD)
	// 3) fallback to CWD "logs"
	if envDir := os.Getenv("BRIDGE_LOG_DIR"); envDir != "" {
		l.dir = envDir
	} else if exePath, err := os.Executable(); err == nil {
		exeDir := filepath.Dir(exePath)
		l.dir = filepath.Join(exeDir, "logs")
	} else {
		l.dir = filepath.Join("logs")
	}
	_ = os.MkdirAll(l.dir, 0o755)
	l.ch = make(chan Event, 1000)
	l.quit = make(chan struct{})
	l.started = true
	go l.loop()
}

// SetStateProvider attaches a snapshotter to enrich WARN/ERROR.
func (l *Logger) SetStateProvider(s stateSnapshot) { l.state = s }

func (l *Logger) loop() {
	ticker := time.NewTicker(250 * time.Millisecond)
	defer ticker.Stop()
	for {
		select {
		case ev := <-l.ch:
			l.write(ev)
		case <-ticker.C:
			l.flush()
		case <-l.quit:
			l.flush()
			if l.f != nil {
				_ = l.f.Close()
			}
			return
		}
	}
}

func (l *Logger) rotateIfNeeded(now time.Time) error {
	date := now.Format("20060102")
	if l.f != nil && date == l.fileDate {
		return nil
	}
	if l.f != nil {
		_ = l.w.Flush()
		_ = l.f.Close()
		l.f, l.w = nil, nil
	}
	path := filepath.Join(l.dir, fmt.Sprintf("unified-%s.jsonl", date))
	f, err := os.OpenFile(path, os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0o644)
	if err != nil {
		return err
	}
	l.f = f
	l.w = bufio.NewWriterSize(f, 64*1024)
	l.fileDate = date
	return nil
}

func (l *Logger) write(ev Event) {
	now := time.Now()
	if ev.TimestampNS == 0 {
		ev.TimestampNS = now.UnixNano()
	}
	if ev.Source == "" {
		ev.Source = l.source
	}
	if ev.SchemaVersion == "" {
		ev.SchemaVersion = CurrentSchemaVersion
	}
	if err := l.rotateIfNeeded(now); err != nil {
		return // best effort
	}
	b, err := json.Marshal(ev)
	if err != nil {
		return
	}
	if l.w != nil {
		_, _ = l.w.Write(b)
		_, _ = l.w.WriteString("\n")
	}
	// Forward to Sentry (non-blocking best-effort)
	maybeSendToSentry(ev)
}

func (l *Logger) flush() {
	if l.w != nil {
		_ = l.w.Flush()
	}
}

func (l *Logger) emit(level, component, msg string, extra map[string]interface{}) {
	ev := Event{Level: level, Component: component, Message: msg}
	if (level == "WARN" || level == "ERROR") && l.state != nil {
		q, n, h := l.state()
		ev.QueueSize, ev.NetPosition, ev.HedgeSize = q, n, h
	}
	if extra != nil {
		ev.Extra = extra
	}
	ev.SchemaVersion = CurrentSchemaVersion
	select {
	case l.ch <- ev:
	default:
		// drop silently if backpressured
	}
}

func (l *Logger) Info(component, msg string, extra map[string]interface{}) {
	l.emit("INFO", component, msg, extra)
}
func (l *Logger) Warn(component, msg string, extra map[string]interface{}) {
	l.emit("WARN", component, msg, extra)
}
func (l *Logger) Error(component, msg string, extra map[string]interface{}) {
	l.emit("ERROR", component, msg, extra)
}

// Ingest enqueues a fully-formed Event (from external clients) preserving
// its timestamp, source, and level. Best-effort: drops if buffer is full.
func (l *Logger) Ingest(ev Event) {
	if ev.SchemaVersion == "" {
		ev.SchemaVersion = CurrentSchemaVersion
	}
	select {
	case l.ch <- ev:
	default:
		// drop silently if backpressured
	}
}

// Shutdown flushes logger buffers and stops background goroutine.
func (l *Logger) Shutdown() {
	l.mu.Lock()
	if !l.started {
		l.mu.Unlock()
		return
	}
	select {
	case <-l.quit:
		// already closed
	default:
		close(l.quit)
	}
	l.started = false
	l.mu.Unlock()
	// Give loop a moment to drain
	time.Sleep(100 * time.Millisecond)
	flushSentry()
}
