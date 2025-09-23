package logging

import (
	"log"
	"strings"
)

// stdWriter implements io.Writer to redirect Go's standard logger into the unified logger.
type stdWriter struct{}

func (w stdWriter) Write(p []byte) (n int, err error) {
	msg := strings.TrimSpace(string(p))
	lvl := detectLevelFromPrefix(msg)
	// Use component "bridge" for internal logs
	L().emit(lvl, "bridge", msg, nil)
	return len(p), nil
}

// detectLevelFromPrefix maps common textual prefixes to levels.
func detectLevelFromPrefix(msg string) string {
	up := strings.ToUpper(strings.TrimSpace(msg))
	switch {
	case strings.HasPrefix(up, "ERROR"):
		return "ERROR"
	case strings.HasPrefix(up, "WARN"):
		return "WARN"
	default:
		return "INFO"
	}
}

// HookStdLogger redirects the default log output to the unified JSONL logger.
func HookStdLogger() {
	log.SetFlags(0)
	log.SetOutput(stdWriter{})
}
