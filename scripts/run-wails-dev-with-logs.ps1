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

# --- Sentry (Observability) Opt-in ---
# Default to disabled unless explicitly opted in by caller.
if ([string]::IsNullOrWhiteSpace($env:SENTRY_ENABLED)) {
    $env:SENTRY_ENABLED = 'false'
}
# To send local logs to Sentry set $env:SENTRY_ENABLED='true' (and optionally preset SENTRY_DSN etc.)
# By default no telemetry is forwarded.
if ($env:SENTRY_ENABLED -and $env:SENTRY_ENABLED.Equals('true', 'InvariantCultureIgnoreCase')) {
    if (-not $env:SENTRY_DSN) {
        Write-Host 'SENTRY_ENABLED=true but SENTRY_DSN is not set. Skipping Sentry setup.' -ForegroundColor Yellow
    }
    else {
        # Respect existing overrides but provide sensible defaults.
        if (-not $env:SENTRY_ENV) { $env:SENTRY_ENV = 'dev' }
        if (-not $env:SENTRY_MIN_EVENT_LEVEL) { $env:SENTRY_MIN_EVENT_LEVEL = 'WARN' }
        if (-not $env:SENTRY_RELEASE) { $env:SENTRY_RELEASE = 'bridge@dev-local' }
        if (-not $env:SENTRY_DEBUG) { $env:SENTRY_DEBUG = 'false' }
        Write-Host "Sentry enabled: env=$env:SENTRY_ENV level>=$env:SENTRY_MIN_EVENT_LEVEL"
    }
} else {
    foreach ($key in 'SENTRY_DSN','SENTRY_ENV','SENTRY_MIN_EVENT_LEVEL','SENTRY_RELEASE','SENTRY_DEBUG') {
        Remove-Item "env:$key" -ErrorAction SilentlyContinue
    }
    Write-Host 'Sentry disabled (set SENTRY_ENABLED=true and provide SENTRY_DSN to opt in).'
}


# Start Wails dev in current terminal so you can see output
wails dev
