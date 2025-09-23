//go:build headless

package main

import (
	blog "BridgeApp/internal/logging"
	"log"
	"os"
	"os/signal"
	"syscall"
)

// Headless entrypoint: start the gRPC server without Wails GUI
func main() {
	app := NewApp()
	// startServer is normally called by Wails OnStartup; invoke directly here
	log.Printf("[headless] Starting Bridge gRPC server...")
	app.startServer()
	// Wait for termination signal to flush logs
	sigCh := make(chan os.Signal, 1)
	signal.Notify(sigCh, os.Interrupt, syscall.SIGTERM)
	<-sigCh
	log.Printf("[headless] Shutdown signal received, flushing logs...")
	blog.L().Shutdown()
}
