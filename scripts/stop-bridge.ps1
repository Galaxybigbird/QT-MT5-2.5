$ErrorActionPreference = 'SilentlyContinue'

# Stop any running bridge-server.exe or BridgeApp process windows
$procs = @()
$procs += Get-Process -Name 'bridge-server' -ErrorAction SilentlyContinue
$procs += Get-Process -Name 'BridgeApp' -ErrorAction SilentlyContinue
if ($procs -and $procs.Count -gt 0) {
    $procs | Stop-Process -Force -ErrorAction SilentlyContinue
    Write-Host "Stopped bridge processes: $($procs.Name -join ', ')"
} else {
    Write-Host 'No bridge processes running.'
}
