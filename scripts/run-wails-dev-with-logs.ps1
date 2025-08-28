$ErrorActionPreference = 'Stop'

# Stop any headless/background server so the port is free
foreach ($p in @('bridge-server','BridgeApp')) {
  try { Get-Process -Name $p -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue } catch {}
}

# Set working dir and env
Set-Location "$PSScriptRoot/../BridgeApp"
if (-not (Test-Path './logs')) { New-Item -ItemType Directory -Path './logs' | Out-Null }
$env:BRIDGE_LOG_DIR = (Resolve-Path './logs').Path
$env:BRIDGE_GRPC_PORT = '50051'

# Clear current day logs as requested
$currentDate = Get-Date -Format "yyyyMMdd"
$logPattern = "./logs/unified-$currentDate.jsonl"
if (Test-Path $logPattern) {
    Write-Host "Clearing current day logs: $logPattern"
    Remove-Item $logPattern -Force
}

Write-Host "BRIDGE_LOG_DIR=$env:BRIDGE_LOG_DIR"
Write-Host "Starting Wails dev (GUI)..."

# --- Sentry (Observability) Placeholders ---
# Uncomment and fill these to enable Sentry while running `wails dev`.
# 1. Quick test: set DSN + MIN level (WARN recommended) + ENV (dev/staging/prod)
# 2. Durable setup: after validating, keep these lines uncommented or move to a dedicated startup script.
#
$env:SENTRY_DSN = 'https://42ce93c5b392d92bd8478eeb72e85905@o4509828075552768.ingest.us.sentry.io/4509839352659968'              # Required to enable Sentry
$env:SENTRY_ENV = 'dev'                                # dev | staging | prod
$env:SENTRY_MIN_EVENT_LEVEL = 'INFO'                   # INFO | WARN | ERROR (INFO now default: capture all logs)
$env:SENTRY_RELEASE = 'bridge@2.0.0'                   # Optional: version tagging
$env:SENTRY_DEBUG = 'false'                            # Optional verbose SDK logging
Write-Host "Sentry configured: env=$env:SENTRY_ENV level>=$env:SENTRY_MIN_EVENT_LEVEL"


# Start Wails dev in current terminal so you can see output
wails dev
