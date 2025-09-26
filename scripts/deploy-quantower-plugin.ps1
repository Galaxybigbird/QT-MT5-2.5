[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$QuantowerPluginsPath,
    [string]$PluginFolderName = "MultiStratQuantower"
)

$ErrorActionPreference = 'Stop'

function Resolve-DefaultQuantowerPluginsPath {
    param(
        [string]$OverridePath
    )

    if ($OverridePath) {
        if (!(Test-Path $OverridePath)) {
            throw "Specified Quantower plugins path '$OverridePath' does not exist."
        }

        return (Resolve-Path -Path $OverridePath).Path
    }

    $portableCandidates = @()
    try {
        $portableRoot = (Get-Item 'C:\Quantower' -ErrorAction Stop).FullName
        $portableCandidates = @(
            (Join-Path $portableRoot 'Settings\Scripts\plug-ins'),
            (Join-Path $portableRoot 'Settings\Scripts\plugins')
        )
    } catch {
        $portableCandidates = @()
    }

    foreach ($candidate in $portableCandidates) {
        if (Test-Path $candidate) {
            return (Resolve-Path -Path $candidate).Path
        }
    }

    $documents = [Environment]::GetFolderPath('MyDocuments')
    $docCandidates = @(
        (Join-Path $documents 'Quantower\Settings\Scripts\plug-ins'),
        (Join-Path $documents 'Quantower\Settings\Scripts\plugins')
    )

    foreach ($docPath in $docCandidates) {
        if (Test-Path $docPath) {
            return (Resolve-Path -Path $docPath).Path
        }
    }

    $fallback = $docCandidates[0]
    New-Item -ItemType Directory -Path $fallback -Force | Out-Null
    return (Resolve-Path -Path $fallback).Path
}
function Detect-QuantowerSdk {
    try {
        $base = 'C:\Quantower\TradingPlatform'
        if (Test-Path $base) {
            $dirs = Get-ChildItem -Path $base -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -like 'v*' } |
                Sort-Object @{ Expression = {
                        $name = $_.Name
                        if ($name.Length -le 1) { return [Version]::new(0,0) }
                        $trimmed = $name.Substring(1)
                        try { return [Version]$trimmed } catch { return [Version]::new(0,0) }
                    }; Descending = $true }
            foreach ($d in $dirs) {
                $bin = Join-Path $d.FullName 'bin'
                $businessLayer = Join-Path $bin 'TradingPlatform.BusinessLayer.dll'
                if (Test-Path $businessLayer) {
                    return @{ Version = $d.Name; Bin = $bin }
                }
            }
        }
    } catch {
        # ignore
    }
    return $null
}



$projectPath = Join-Path $PSScriptRoot '..\MultiStratManagerRepo\Quantower\QuantowerMultiStratAddOn\QuantowerMultiStratAddOn.csproj'
if (!(Test-Path $projectPath)) {
    throw "Quantower plugin project not found at $projectPath"
}

$publishDir = Join-Path $PSScriptRoot "..\MultiStratManagerRepo\Quantower\QuantowerMultiStratAddOn\bin\$Configuration\net8.0-windows\publish"

# Auto-detect Quantower SDK version and bin path (portable install preferred)
$detected = Detect-QuantowerSdk
$qtVersion = $null
$qtSdkBin = $null
if ($detected) {
    $qtVersion = $detected.Version
    $qtSdkBin = $detected.Bin
    # Ensure trailing backslash so csproj HintPath concatenation works
    if ($qtSdkBin -and ($qtSdkBin[-1] -ne '\\')) { $qtSdkBin = $qtSdkBin + '\\' }
    Write-Host "Using Quantower SDK: $qtVersion ($qtSdkBin)" -ForegroundColor Yellow
} else {
    Write-Host "Could not auto-detect Quantower SDK. Proceeding with csproj defaults." -ForegroundColor DarkYellow
}

Write-Host "Publishing Quantower plugin ($Configuration)..." -ForegroundColor Cyan
$props = @('-p:DisableImplicitNuGetFallbackFolder=true','-p:RestoreFallbackFolders=','-p:EnableWindowsTargeting=true')
if ($qtVersion) { $props += "-p:QuantowerVersion=$qtVersion" }
if ($qtSdkBin) { $props += "-p:QuantowerSdkDir=$qtSdkBin" }

# Try fast path (no restore) first; on failure, restore and retry
$publishArgsFast = @($projectPath,'-c',$Configuration,'-o',$publishDir,'--nologo','--no-restore') + $props
$publishOutput = & dotnet publish @publishArgsFast 2>&1
$exit = $LASTEXITCODE
if ($exit -ne 0) {
    Write-Warning "dotnet publish (no-restore) failed with exit code $exit. Retrying with restore..."
    $restoreOut = & dotnet restore $projectPath 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Error "dotnet restore failed with exit code $LASTEXITCODE"
        if ($restoreOut) { $restoreOut | ForEach-Object { Write-Error $_ } }
        exit $LASTEXITCODE
    }
    $publishArgs = @($projectPath,'-c',$Configuration,'-o',$publishDir,'--nologo') + $props
    $publishOutput = & dotnet publish @publishArgs 2>&1
    $exit = $LASTEXITCODE
}
if ($exit -ne 0) {
    Write-Error "dotnet publish failed with exit code $exit"
    if ($publishOutput) {
        Write-Error "dotnet publish output:"
        $publishOutput | ForEach-Object { Write-Error $_ }
    }
    exit $exit
}
$publishOutput | Write-Verbose

if (!(Test-Path $publishDir)) {
    throw "Publish directory not found: $publishDir"
}

$pluginsRoot = Resolve-DefaultQuantowerPluginsPath -OverridePath $QuantowerPluginsPath
$destination = Join-Path $pluginsRoot $PluginFolderName

Write-Host "Publish output: $publishDir" -ForegroundColor DarkCyan
(Get-ChildItem -Path $publishDir -File | Select-Object Name,Length,LastWriteTime | Format-Table | Out-String).Trim() | Write-Host

Write-Host "Deploying to $destination" -ForegroundColor Cyan

# Detect running Quantower processes to warn about file locks
$qtProcs = Get-Process -Name Quantower -ErrorAction SilentlyContinue
if ($qtProcs) {
    Write-Warning ("Quantower appears to be running (PID(s): {0}). Plugin files may be locked; close Quantower for a clean deploy." -f ($qtProcs.Id -join ','))
}

if (!(Test-Path $destination)) {
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
} else {
    Write-Host "Cleaning destination folder before copy..." -ForegroundColor DarkYellow
    $before = Get-ChildItem -Path $destination -Recurse -Force -ErrorAction SilentlyContinue
    $removed = 0
    try {
        $before | ForEach-Object {
            try { Remove-Item -LiteralPath $_.FullName -Force -Recurse -ErrorAction Stop; $removed++ } catch {}
        }
        Write-Host "Removed $removed item(s) from destination." -ForegroundColor DarkYellow
    } catch {
        Write-Warning "Some files could not be removed (likely locked by Quantower). Close Quantower and rerun. Error: $($_.Exception.Message)"
    }
}

# Copy files from publish dir
$toCopy = Get-ChildItem -Path $publishDir -Recurse -File
Copy-Item -Path (Join-Path $publishDir '*') -Destination $destination -Recurse -Force
$copied = (Get-ChildItem -Path $destination -Recurse -File | Measure-Object).Count
Write-Host "Copied $($toCopy.Count) file(s). Destination now contains $copied file(s)." -ForegroundColor DarkCyan

# Ensure layout.html is available at plugin root (Quantower expects TemplateName at root)
try {
    $layoutCandidates = @(
        (Join-Path $publishDir 'layout.html'),
        (Join-Path $publishDir 'HTML\layout.html'),
        (Join-Path $publishDir 'html\layout.html')
    )
    foreach ($src in $layoutCandidates) {
        if (Test-Path $src) {
            $dst = Join-Path $destination 'layout.html'
            Copy-Item -Path $src -Destination $dst -Force
            Write-Host "Placed layout.html at plugin root: $dst" -ForegroundColor DarkCyan
            break
        }
    }
} catch {
    Write-Warning "Could not place layout.html at plugin root: $($_.Exception.Message)"
}

# Report main assembly version + hash after deploy
$mainDll = Join-Path $destination 'QuantowerMultiStratAddOn.dll'
if (Test-Path $mainDll) {
    $ver = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($mainDll).FileVersion
    $hash = (Get-FileHash -Path $mainDll -Algorithm SHA1).Hash
    $ts = (Get-Item $mainDll).LastWriteTime
    Write-Host "Deployed DLL: $mainDll" -ForegroundColor Green
    Write-Host "  Version: $ver" -ForegroundColor Green
    Write-Host "  SHA1:    $hash" -ForegroundColor Green
    Write-Host "  Stamp:   $ts" -ForegroundColor Green
} else {
    Write-Warning "Main DLL not found after copy: $mainDll"
}

Write-Host "Final files in destination:" -ForegroundColor DarkCyan
(Get-ChildItem -Path $destination -File | Select-Object Name,Length,LastWriteTime | Format-Table | Out-String).Trim() | Write-Host

# Mirror HTML and root layout.html to Quantower SDK bin plug-ins folder (for browser template resolution)
if ($qtSdkBin) {
    try {
        $binPlugins = Join-Path $qtSdkBin 'plug-ins'
        $binDest = Join-Path $binPlugins $PluginFolderName
        if (!(Test-Path $binDest)) { New-Item -ItemType Directory -Path $binDest -Force | Out-Null }

        # Copy HTML folder if present (handle both 'HTML' and 'html')
        $htmlDirCandidates = @(
            (Join-Path $publishDir 'HTML'),
            (Join-Path $publishDir 'html')
        )
        $htmlCopied = $false
        foreach ($dir in $htmlDirCandidates) {
            if (Test-Path $dir) {
                Copy-Item -Path $dir -Destination $binDest -Recurse -Force
                Write-Host ("Mirrored HTML dir to {0}" -f (Join-Path $binDest (Split-Path $dir -Leaf))) -ForegroundColor DarkCyan
                $htmlCopied = $true
                break
            }
        }
        if (-not $htmlCopied) { Write-Warning "No HTML directory found in publish output; Browser templates may not load." }

        # Ensure layout.html at binDest root as well
        $layoutCandidates = @(
            (Join-Path $publishDir 'layout.html'),
            (Join-Path $publishDir 'HTML\layout.html'),
            (Join-Path $publishDir 'html\layout.html')
        )
        foreach ($src in $layoutCandidates) {
            if (Test-Path $src) {
                Copy-Item -Path $src -Destination (Join-Path $binDest 'layout.html') -Force
                Write-Host ("Placed layout.html at {0}" -f (Join-Path $binDest 'layout.html')) -ForegroundColor DarkCyan
                break
            }
        }
    } catch {
        Write-Warning "Failed to mirror HTML to SDK bin plug-ins: $($_.Exception.Message)"
    }
}

Write-Host "Deployment complete." -ForegroundColor Green
Write-Host "Restart Quantower and open the panel from the Sidebar to load the plug-in." -ForegroundColor Yellow
