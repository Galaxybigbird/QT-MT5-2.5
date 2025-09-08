# Fix NinjaTrader Addon Loading Issues
# This script helps diagnose and fix common addon loading problems

param(
    [switch]$CleanAll,
    [switch]$CheckDependencies,
    [switch]$ResetNT,
    [switch]$Verbose
)

$ErrorActionPreference = 'Continue'

Write-Host "================ NT Addon Loading Fix ================" -ForegroundColor Cyan
Write-Host "Diagnosing and fixing MultiStratManager addon loading issues..." -ForegroundColor Yellow

# Find NinjaTrader installations
$ntRoots = @()
$ntRoots += Join-Path $env:USERPROFILE 'Documents\NinjaTrader 8'
$ntRoots += Join-Path $env:USERPROFILE 'OneDrive\Documents\NinjaTrader 8'
$ntRoots += Join-Path $env:USERPROFILE 'OneDrive\Desktop\NinjaTrader 8'
$ntRoots += Join-Path $env:USERPROFILE 'Desktop\NinjaTrader 8'
$docPath = 'C:\Users\marth\OneDrive\Desktop\OneDrive\Old video editing files\NinjaTrader 8'
if (Test-Path $docPath) { $ntRoots += $docPath }
$ntRoots = $ntRoots | Where-Object { Test-Path $_ } | Select-Object -Unique

if ($ntRoots.Count -eq 0) {
    Write-Host "❌ No NinjaTrader 8 installations found!" -ForegroundColor Red
    exit 1
}

Write-Host "📁 Found NinjaTrader installations:" -ForegroundColor Green
$ntRoots | ForEach-Object { Write-Host "  - $_" -ForegroundColor Gray }

foreach ($ntRoot in $ntRoots) {
    Write-Host "`n🔍 Checking: $ntRoot" -ForegroundColor Cyan
    
    $addonPath = Join-Path $ntRoot 'bin\Custom\AddOns\MultiStratManager'
    $externalPath = Join-Path $addonPath 'External'
    $binCustom = Join-Path $ntRoot 'bin\Custom'
    
    # 1. Check if addon source files exist
    Write-Host "1️⃣ Checking addon source files..." -ForegroundColor Yellow
    $requiredFiles = @('MultiStratManager.cs', 'UIForManager.cs', 'TrailingAndElasticManager.cs')
    $missingFiles = @()
    
    foreach ($file in $requiredFiles) {
        $filePath = Join-Path $addonPath $file
        if (Test-Path $filePath) {
            Write-Host "  ✅ $file" -ForegroundColor Green
        } else {
            Write-Host "  ❌ $file MISSING" -ForegroundColor Red
            $missingFiles += $file
        }
    }
    
    # 2. Check gRPC dependencies
    Write-Host "2️⃣ Checking gRPC dependencies..." -ForegroundColor Yellow
    $requiredDlls = @('Grpc.Core.dll', 'Grpc.Core.Api.dll', 'Google.Protobuf.dll', 'System.Text.Json.dll', 'NTGrpcClient.dll')
    $missingDlls = @()
    
    foreach ($dll in $requiredDlls) {
        $dllPath = Join-Path $externalPath $dll
        $binPath = Join-Path $binCustom $dll
        
        if (Test-Path $dllPath) {
            Write-Host "  ✅ $dll (External)" -ForegroundColor Green
        } elseif (Test-Path $binPath) {
            Write-Host "  ✅ $dll (bin\Custom)" -ForegroundColor Green
        } else {
            Write-Host "  ❌ $dll MISSING" -ForegroundColor Red
            $missingDlls += $dll
        }
    }
    
    # 3. Check for compilation errors in NinjaScript log
    Write-Host "3️⃣ Checking NinjaScript compilation log..." -ForegroundColor Yellow
    $logPath = Join-Path $ntRoot 'trace\NinjaScript*.log'
    $logFiles = Get-ChildItem -Path (Split-Path $logPath -Parent) -Filter (Split-Path $logPath -Leaf) -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    
    if ($logFiles) {
        $recentErrors = Get-Content $logFiles.FullName -Tail 50 | Where-Object { $_ -match "error|exception|MultiStratManager" -and $_ -match (Get-Date).ToString("yyyy-MM-dd") }
        if ($recentErrors) {
            Write-Host "  ⚠️ Recent compilation errors found:" -ForegroundColor Red
            $recentErrors | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
        } else {
            Write-Host "  ✅ No recent compilation errors" -ForegroundColor Green
        }
    }
    
    # 4. Clean operations if requested
    if ($CleanAll) {
        Write-Host "4️⃣ Performing cleanup operations..." -ForegroundColor Yellow
        
        # Remove compiled assemblies to force recompilation
        $compiledPath = Join-Path $ntRoot 'bin\Custom\AddOns\MultiStratManager.dll'
        if (Test-Path $compiledPath) {
            try {
                Remove-Item $compiledPath -Force
                Write-Host "  ✅ Removed compiled assembly" -ForegroundColor Green
            } catch {
                Write-Host "  ❌ Could not remove compiled assembly: $($_.Exception.Message)" -ForegroundColor Red
            }
        }
        
        # Clear NinjaScript cache
        $cachePath = Join-Path $ntRoot 'bin\Custom\*.cache'
        Get-ChildItem -Path (Split-Path $cachePath -Parent) -Filter (Split-Path $cachePath -Leaf) -ErrorAction SilentlyContinue | ForEach-Object {
            try {
                Remove-Item $_.FullName -Force
                Write-Host "  ✅ Cleared cache: $($_.Name)" -ForegroundColor Green
            } catch {
                Write-Host "  ❌ Could not clear cache: $($_.Name)" -ForegroundColor Red
            }
        }
    }
    
    # 5. Recommendations
    Write-Host "5️⃣ Recommendations:" -ForegroundColor Yellow
    
    if ($missingFiles.Count -gt 0) {
        Write-Host "  🔧 Run deployment script to copy missing source files" -ForegroundColor Cyan
    }
    
    if ($missingDlls.Count -gt 0) {
        Write-Host "  🔧 Run deployment script to copy missing DLL dependencies" -ForegroundColor Cyan
    }
    
    Write-Host "  🔧 Close NinjaTrader completely before redeploying" -ForegroundColor Cyan
    Write-Host "  🔧 After deployment, press F5 in NinjaTrader to recompile" -ForegroundColor Cyan
    Write-Host "  🔧 Check NinjaScript Output tab for compilation errors" -ForegroundColor Cyan
}

Write-Host "`n📋 Quick Fix Steps:" -ForegroundColor Green
Write-Host "1. Close NinjaTrader completely" -ForegroundColor White
Write-Host "2. Run: .\scripts\deploy-nt-to-ninjatrader.ps1" -ForegroundColor White
Write-Host "3. Open NinjaTrader" -ForegroundColor White
Write-Host "4. Press F5 to recompile NinjaScript" -ForegroundColor White
Write-Host "5. Check Output tab for errors" -ForegroundColor White
Write-Host "6. Try opening addon from Tools menu" -ForegroundColor White

Write-Host "`n✅ Diagnosis complete!" -ForegroundColor Green
