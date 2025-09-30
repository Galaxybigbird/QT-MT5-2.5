# Monitor Quantower Plugin Loading
# This script helps diagnose plugin loading issues

$LogFile = "C:\Documents\Dev\OfficialFuturesHedgebotv2.5QT\logs.txt"

Write-Host "=== Quantower Plugin Loading Monitor ===" -ForegroundColor Cyan
Write-Host "This script will monitor the plugin loading process." -ForegroundColor Yellow
Write-Host "Log file: $LogFile" -ForegroundColor Cyan
Write-Host ""

# Clear log file on each run
if (Test-Path $LogFile) {
    Clear-Content $LogFile
    Write-Host "✅ Cleared previous log file" -ForegroundColor Green
} else {
    New-Item -Path $LogFile -ItemType File -Force | Out-Null
    Write-Host "✅ Created new log file" -ForegroundColor Green
}

# Check if plugin files exist
$pluginPath = "C:\Quantower\Settings\Scripts\plug-ins\MultiStratQuantower"
$dllPath = Join-Path $pluginPath "QuantowerMultiStratAddOn.dll"
$htmlPath = Join-Path $pluginPath "layout.html"

Write-Host ""
Write-Host "Checking plugin deployment..." -ForegroundColor Green
if (Test-Path $dllPath) {
    $dll = Get-Item $dllPath
    $msg = "✅ Plugin DLL found: $($dll.FullName) (Size: $($dll.Length) bytes, Modified: $($dll.LastWriteTime))"
    Write-Host $msg -ForegroundColor Green
    Add-Content -Path $LogFile -Value $msg
} else {
    $msg = "❌ Plugin DLL NOT found at: $dllPath"
    Write-Host $msg -ForegroundColor Red
    Add-Content -Path $LogFile -Value $msg
}

if (Test-Path $htmlPath) {
    $html = Get-Item $htmlPath
    $msg = "✅ HTML template found: $($html.FullName) (Size: $($html.Length) bytes)"
    Write-Host $msg -ForegroundColor Green
    Add-Content -Path $LogFile -Value $msg
} else {
    $msg = "❌ HTML template NOT found at: $htmlPath"
    Write-Host $msg -ForegroundColor Red
    Add-Content -Path $LogFile -Value $msg
}

Write-Host ""
Write-Host "Clearing old temp log files..." -ForegroundColor Green
Remove-Item "$env:TEMP\msb-plugin.log" -ErrorAction SilentlyContinue

# Check for existing Quantower processes
$qtProcs = Get-Process -Name "Quantower" -ErrorAction SilentlyContinue
if ($qtProcs) {
    Write-Host "⚠️  Quantower is already running (PID: $($qtProcs.Id -join ', '))" -ForegroundColor Yellow
    Write-Host "   Close Quantower and restart it to load the updated plugin." -ForegroundColor Yellow
} else {
    Write-Host "✅ Quantower is not running - ready for fresh start" -ForegroundColor Green
}

Write-Host ""
Write-Host "=== INSTRUCTIONS ===" -ForegroundColor Cyan
Write-Host "1. Start Quantower" -ForegroundColor White
Write-Host "2. Look for the plugin in the sidebar or panels menu" -ForegroundColor White
Write-Host "3. Try to open 'Multi-Strat Bridge (WPF Fallback)'" -ForegroundColor White
Write-Host "4. If it doesn't appear, run this script again to check logs" -ForegroundColor White
Write-Host ""

# Function to monitor logs
function Watch-PluginLogs {
    Write-Host "Monitoring plugin logs (press Ctrl+C to stop)..." -ForegroundColor Yellow
    Write-Host "Writing to: $LogFile" -ForegroundColor Cyan
    Write-Host ""

    $tempLogPath = "$env:TEMP\msb-plugin.log"
    $lastSize = 0

    while ($true) {
        if (Test-Path $tempLogPath) {
            $currentSize = (Get-Item $tempLogPath).Length
            if ($currentSize -gt $lastSize) {
                $content = Get-Content $tempLogPath -Tail 10
                foreach ($line in $content) {
                    # Write to file
                    Add-Content -Path $LogFile -Value $line

                    # Write to console with colors
                    if ($line -match "GetInfo") {
                        Write-Host $line -ForegroundColor Green
                    } elseif ($line -match "ctor|Initialize") {
                        Write-Host $line -ForegroundColor Cyan
                    } elseif ($line -match "ERROR|failed") {
                        Write-Host $line -ForegroundColor Red
                    } elseif ($line -match "WARN") {
                        Write-Host $line -ForegroundColor Yellow
                    } else {
                        Write-Host $line -ForegroundColor Gray
                    }
                }
                $lastSize = $currentSize
            }
        }
        Start-Sleep -Seconds 2
    }
}

$response = Read-Host "Do you want to start monitoring logs now? (y/n)"
if ($response -eq 'y' -or $response -eq 'Y') {
    Watch-PluginLogs
} else {
    Write-Host ""
    Write-Host "To check logs later, run:" -ForegroundColor Green
    Write-Host "Get-Content `"$env:TEMP\msb-plugin.log`" -Tail 20" -ForegroundColor Gray
}
