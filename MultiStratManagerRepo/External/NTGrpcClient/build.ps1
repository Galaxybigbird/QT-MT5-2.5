# Build script for NTGrpcClient.dll
# Targets .NET Standard 2.0 for compatibility with NinjaTrader 8 (.NET Framework 4.8)

param(
    [string]$Configuration = "Release",
    [string]$Platform = "x64"
)

Write-Host "Building NTGrpcClient.dll..." -ForegroundColor Green
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

# Copy output to parent directory
$outputPath = "bin\$Configuration\netstandard2.0\NTGrpcClient.dll"
$destinationPath = "..\NTGrpcClient.dll"

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
        $depPath = "bin\$Configuration\netstandard2.0\$dep"
        if (Test-Path $depPath) {
            Copy-Item -Path $depPath -Destination "..\$dep" -Force
            Write-Host "Copied dependency: $dep" -ForegroundColor Gray
        }
    }
    
    Write-Host "`nBuild completed successfully!" -ForegroundColor Green
    Write-Host "Output: $destinationPath" -ForegroundColor Green
} else {
    Write-Host "Build output not found!" -ForegroundColor Red
    exit 1
}

# Generate documentation (optional)
Write-Host "`nGenerating XML documentation..." -ForegroundColor Yellow
$docPath = "bin\$Configuration\netstandard2.0\NTGrpcClient.xml"
if (Test-Path $docPath) {
    Copy-Item -Path $docPath -Destination "..\NTGrpcClient.xml" -Force
    Write-Host "Documentation generated: ..\NTGrpcClient.xml" -ForegroundColor Green
}