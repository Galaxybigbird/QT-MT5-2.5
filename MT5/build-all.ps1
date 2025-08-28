# Orchestrates MT5 managed build+deploy and native wrapper build in one go (Option A)
param(
    [string]$Mt5RootPath = "$env:APPDATA\MetaQuotes\Terminal\7BC3F33EDFDBDBDBADB45838B9A2D03F\MQL5",
    [switch]$SkipWrapper
)

$ErrorActionPreference = 'Stop'

function Invoke-ManagedBuildAndDeploy {
    param([string]$RootPath)
    Write-Host "[1/2] Building managed MT5 gRPC client and deploying artifacts..." -ForegroundColor Cyan
    $buildScript = Join-Path $PSScriptRoot 'build.ps1'
    if (-not (Test-Path $buildScript)) { throw "Missing build.ps1 at $buildScript" }
    & powershell -NoProfile -ExecutionPolicy Bypass -File $buildScript -Mt5RootPath $RootPath
    if ($LASTEXITCODE -ne 0) { throw "Managed build/deploy failed with exit code $LASTEXITCODE" }
}

function Invoke-NativeWrapperBuild {
    Write-Host "[2/2] Building native MT5 wrapper (requires Visual Studio C++ toolchain)..." -ForegroundColor Cyan

    $batPath = Join-Path $PSScriptRoot 'build_grpc_wrapper.bat'
    if (-not (Test-Path $batPath)) { throw "Missing build_grpc_wrapper.bat at $batPath" }

    # Try to locate VsDevCmd.bat via vswhere for a self-contained build
    $vswhere = Join-Path "$Env:ProgramFiles(x86)" 'Microsoft Visual Studio\Installer\vswhere.exe'
    $vsDevCmd = $null
    if (Test-Path $vswhere) {
        try {
            $installPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
            if ($LASTEXITCODE -eq 0 -and $installPath) {
                $vsDevCmdCandidate = Join-Path $installPath 'Common7\Tools\VsDevCmd.bat'
                if (Test-Path $vsDevCmdCandidate) { $vsDevCmd = $vsDevCmdCandidate }
            }
        } catch { }
    }

    if ($vsDevCmd) {
        Write-Host "Using VS Dev environment: $vsDevCmd" -ForegroundColor DarkGray
        Push-Location $PSScriptRoot
        try {
            $cmd = '"' + $vsDevCmd + '" -arch=x64 -host_arch=x64 && call "' + $batPath + '"'
            & cmd.exe /c $cmd
            if ($LASTEXITCODE -ne 0) { throw "Wrapper build failed with exit code $LASTEXITCODE" }
        } finally { Pop-Location }
    } else {
        Write-Warning "vswhere/VsDevCmd.bat not found. Attempting to run wrapper build directly. If it fails, open a 'x64 Native Tools Command Prompt for VS' and run the .bat manually."
        Push-Location $PSScriptRoot
        try {
            & $batPath
            if ($LASTEXITCODE -ne 0) { throw "Wrapper build failed with exit code $LASTEXITCODE" }
        } finally { Pop-Location }
    }
}

try {
    Invoke-ManagedBuildAndDeploy -RootPath $Mt5RootPath
    if (-not $SkipWrapper) { Invoke-NativeWrapperBuild }
    Write-Host "Build-all complete." -ForegroundColor Green
} catch {
    Write-Error $_
    exit 1
}
