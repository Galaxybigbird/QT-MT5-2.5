# Makefile for BridgeApp gRPC/Protobuf generation

.PHONY: proto proto-clean proto-go proto-csharp proto-cpp help

# Default target
help:
	@echo "Available targets:"
	@echo "  proto       - Generate all protobuf code (Go, C#, C++)"
	@echo "  proto-go    - Generate Go protobuf code"
	@echo "  proto-csharp- Generate C# protobuf code"
	@echo "  proto-cpp   - Generate C++ protobuf code"
	@echo "  proto-clean - Clean generated protobuf files"

# Generate all protobuf code
proto: proto-go proto-csharp proto-cpp

# Clean generated protobuf files
proto-clean:
	@echo "Cleaning generated protobuf files..."
	rm -rf internal/grpc/proto/
	rm -rf ../MultiStratManagerRepo/External/Proto/
	rm -rf ../MT5/Proto/

# Generate Go protobuf code
proto-go:
	@echo "Generating Go protobuf code..."
	mkdir -p internal/grpc/proto
	export PATH=$${PATH}:$$(go env GOPATH)/bin && \
	protoc --go_out=internal/grpc --go_opt=paths=source_relative \
		--go-grpc_out=internal/grpc --go-grpc_opt=paths=source_relative \
		proto/trading.proto

# Generate C# protobuf code (for NinjaTrader DLL)
proto-csharp:
	@echo "Generating C# protobuf code..."
	mkdir -p ../MultiStratManagerRepo/External/Proto
	protoc --csharp_out=../MultiStratManagerRepo/External/Proto \
		--grpc_out=../MultiStratManagerRepo/External/Proto \
		--plugin=protoc-gen-grpc=$$(which grpc_csharp_plugin 2>/dev/null || echo "/usr/bin/grpc_csharp_plugin") \
		proto/trading.proto

# Generate C++ protobuf code (for MT5 DLL)
proto-cpp:
	@echo "Generating C++ protobuf code..."
	mkdir -p ../MT5/Proto
	protoc --cpp_out=../MT5/Proto \
		--grpc_out=../MT5/Proto \
		--plugin=protoc-gen-grpc=$$(which grpc_cpp_plugin 2>/dev/null || echo "/usr/bin/grpc_cpp_plugin") \
		proto/trading.proto

# Build the Go application
build:
	@echo "Building BridgeApp..."
	wails build

# Development mode
dev:
	@echo "Starting development mode..."
	wails dev

# Install Go protobuf tools
install-tools:
	@echo "Installing Go protobuf tools..."
	go install google.golang.org/protobuf/cmd/protoc-gen-go@latest
	go install google.golang.org/grpc/cmd/protoc-gen-go-grpc@latest