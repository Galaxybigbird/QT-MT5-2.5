$ErrorActionPreference = 'Stop'
Set-Location "$PSScriptRoot/../BridgeApp"
if (-not (Test-Path './logs')) { New-Item -ItemType Directory -Path './logs' | Out-Null }
$env:BRIDGE_LOG_DIR = (Resolve-Path './logs').Path
$env:BRIDGE_GRPC_PORT = '50051'
Write-Host "BRIDGE_LOG_DIR=$env:BRIDGE_LOG_DIR"
Write-Host "Building headless..."
go build -tags headless -o ./bridge-server.exe .
Write-Host "Starting headless..."
Start-Process -FilePath './bridge-server.exe' -WorkingDirectory (Get-Location).Path -WindowStyle Hidden
Start-Sleep -Seconds 2
$today = Get-Date -Format 'yyyyMMdd'
$file = Join-Path $env:BRIDGE_LOG_DIR "unified-$today.jsonl"
if (Test-Path $file) {
  Write-Host "--- Tail of $file ---"
  Get-Content -Path $file -Tail 40
} else {
  Write-Host "Not found: $file"
}
