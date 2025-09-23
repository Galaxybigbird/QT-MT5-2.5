package logging

import (
	"os"
	"strconv"
	"strings"
	"sync"
	"time"

	sentry "github.com/getsentry/sentry-go"
)

var (
	sentryOnce sync.Once
	// Default threshold lowered to INFO so all logs flow to Sentry unless user raises it.
	sentryMinLevel = "INFO"
	sentryEnabled  bool
)

// initSentryFromEnvOnce initializes Sentry based on environment variables (best-effort).
// Environment:
//
//	SENTRY_DSN (required to enable)
//	SENTRY_ENV (optional)
//	SENTRY_RELEASE (optional)
//	SENTRY_DEBUG (optional bool)
//	SENTRY_MIN_EVENT_LEVEL (INFO|WARN|ERROR) default ERROR
func initSentryFromEnvOnce() {
	sentryOnce.Do(func() {
		dsn := strings.TrimSpace(os.Getenv("SENTRY_DSN"))
		if dsn == "" {
			return // disabled
		}
		if lvl := strings.ToUpper(strings.TrimSpace(os.Getenv("SENTRY_MIN_EVENT_LEVEL"))); lvl != "" {
			switch lvl {
			case "INFO", "WARN", "ERROR":
				sentryMinLevel = lvl
			}
		}
		debug := false
		if v := os.Getenv("SENTRY_DEBUG"); v != "" {
			if b, err := strconv.ParseBool(v); err == nil {
				debug = b
			}
		}
		opts := sentry.ClientOptions{Dsn: dsn}
		if env := os.Getenv("SENTRY_ENV"); env != "" {
			opts.Environment = env
		}
		if rel := os.Getenv("SENTRY_RELEASE"); rel != "" {
			opts.Release = rel
		}
		opts.Debug = debug
		// Attempt init; ignore error silently to avoid impacting logging
		if err := sentry.Init(opts); err == nil {
			sentryEnabled = true
			// Emit a one-time internal info event so we immediately verify ingestion & threshold.
			sentry.WithScope(func(scope *sentry.Scope) {
				scope.SetTag("bootstrap", "true")
				scope.SetTag("configured_min_level", sentryMinLevel)
				ev := sentry.NewEvent()
				ev.Message = "Sentry initialized (bootstrap)"
				ev.Level = sentry.LevelInfo
				ev.Timestamp = time.Now()
				sentry.CaptureEvent(ev)
			})
		}
	})
}

// maybeSendToSentry promotes qualifying events to Sentry.
func maybeSendToSentry(ev Event) {
	if !sentryEnabled {
		return
	}
	if !levelQualifies(ev.Level) {
		return
	}
	sentry.WithScope(func(scope *sentry.Scope) {
		scope.SetTag("source", ev.Source)
		if ev.Component != "" {
			scope.SetTag("component", ev.Component)
		}
		if ev.BaseID != "" {
			scope.SetTag("base_id", ev.BaseID)
		}
		if ev.TradeID != "" {
			scope.SetTag("trade_id", ev.TradeID)
		}
		if ev.NTOrderID != "" {
			scope.SetTag("nt_order_id", ev.NTOrderID)
		}
		if ev.MT5Ticket != 0 {
			scope.SetTag("mt5_ticket", strconv.FormatUint(ev.MT5Ticket, 10))
		}
		if ev.ErrorCode != "" {
			scope.SetTag("error_code", ev.ErrorCode)
		}
		if ev.SchemaVersion != "" {
			scope.SetTag("schema_version", ev.SchemaVersion)
		}
		if ev.CorrelationID != "" {
			scope.SetTag("correlation_id", ev.CorrelationID)
		}
		if ev.QueueSize != 0 {
			scope.SetContext("queue", map[string]interface{}{"queue_size": ev.QueueSize})
		}
		if ev.NetPosition != 0 || ev.HedgeSize != 0 {
			scope.SetContext("state", map[string]interface{}{"net_position": ev.NetPosition, "hedge_size": ev.HedgeSize})
		}
		if len(ev.Tags) > 0 {
			for k, v := range ev.Tags {
				scope.SetTag(k, v)
			}
		}
		if ev.Extra != nil {
			scope.SetContext("extra", ev.Extra)
		}
		// Use timestamp if provided
		ts := time.Unix(0, ev.TimestampNS)
		event := sentry.NewEvent()
		event.Message = ev.Message
		event.Level = mapLevel(ev.Level)
		event.Timestamp = ts
		if ev.Stack != "" {
			event.Extra = map[string]interface{}{"stack": ev.Stack}
		}
		sentry.CaptureEvent(event)
	})
}

func mapLevel(l string) sentry.Level {
	switch strings.ToUpper(l) {
	case "INFO":
		return sentry.LevelInfo
	case "WARN":
		return sentry.LevelWarning
	case "ERROR":
		return sentry.LevelError
	default:
		return sentry.LevelInfo
	}
}

func levelQualifies(l string) bool {
	order := func(v string) int {
		switch v {
		case "INFO":
			return 1
		case "WARN":
			return 2
		case "ERROR":
			return 3
		default:
			return 0
		}
	}
	return order(strings.ToUpper(l)) >= order(sentryMinLevel)
}

// flushSentry ensures pending events are sent.
func flushSentry() {
	if !sentryEnabled {
		return
	}
	sentry.Flush(2 * time.Second)
}
