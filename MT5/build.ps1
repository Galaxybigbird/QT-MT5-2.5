# Build script for MT5GrpcClient.dll
# Targets .NET Framework 4.8 for compatibility with MT5 (x64)

param(
    [string]$Configuration = "Release",
    [string]$Platform = "x64"
)

Write-Host "Building MT5GrpcClient.dll..." -ForegroundColor Green
Write-Host "Configuration: $Configuration" -ForegroundColor Yellow
Write-Host "Platform: $Platform" -ForegroundColor Yellow

# Clean previous build
Write-Host "`nCleaning previous build..." -ForegroundColor Yellow
if (Test-Path "bin") {
    Remove-Item -Path "bin" -Recurse -Force
}
if (Test-Path "obj") {
    Remove-Item -Path "obj" -Recurse -Force
}

# Restore NuGet packages
Write-Host "`nRestoring NuGet packages..." -ForegroundColor Yellow
dotnet restore

if ($LASTEXITCODE -ne 0) {
    Write-Host "Failed to restore NuGet packages!" -ForegroundColor Red
    exit 1
}

# Build the project
Write-Host "`nBuilding project..." -ForegroundColor Yellow
dotnet build -c $Configuration

if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}

# Copy output to root directory for MT5
$outputPath = "bin\$Configuration\net48\MT5GrpcClient.dll"
$destinationPath = "MT5GrpcClient.dll"

if (Test-Path $outputPath) {
    Write-Host "`nCopying output DLL..." -ForegroundColor Yellow
    Copy-Item -Path $outputPath -Destination $destinationPath -Force
    
    # Also copy required dependencies
    $dependencies = @(
        "Google.Protobuf.dll",
        "Grpc.Core.Api.dll",
        "Grpc.Net.Client.dll",
        "Grpc.Net.Common.dll",
        "System.Text.Json.dll"
    )
    
    foreach ($dep in $dependencies) {
        $depPath = "bin\$Configuration\net48\$dep"
        if (Test-Path $depPath) {
            Copy-Item -Path $depPath -Destination $dep -Force
            Write-Host "Copied dependency: $dep" -ForegroundColor Gray
        }
    }
    
    Write-Host "`nBuild completed successfully!" -ForegroundColor Green
    Write-Host "Output: $destinationPath" -ForegroundColor Green
    Write-Host "`nTo use with MT5:" -ForegroundColor Cyan
    Write-Host "1. Copy MT5GrpcClient.dll to your MT5 MQL5\Libraries folder" -ForegroundColor Cyan
    Write-Host "2. Copy all dependency DLLs to the same folder" -ForegroundColor Cyan
    Write-Host "3. Ensure MT5 allows DLL imports in settings" -ForegroundColor Cyan
} else {
    Write-Host "Build output not found!" -ForegroundColor Red
    exit 1
}

# Generate documentation (optional)
Write-Host "`nGenerating XML documentation..." -ForegroundColor Yellow
$docPath = "bin\$Configuration\net48\MT5GrpcClient.xml"
if (Test-Path $docPath) {
    Copy-Item -Path $docPath -Destination "MT5GrpcClient.xml" -Force
    Write-Host "Documentation generated: MT5GrpcClient.xml" -ForegroundColor Green
}

Write-Host "`nReady for MT5 integration!" -ForegroundColor Green