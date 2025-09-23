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

    $documents = [Environment]::GetFolderPath('MyDocuments')
    $fallback1 = Join-Path $documents 'Quantower\Settings\Scripts\plug-ins'
    $fallback2 = 'C:\Quantower\Settings\Scripts\plug-ins'

    if (Test-Path $fallback1) { return (Resolve-Path -Path $fallback1).Path }
    if (Test-Path $fallback2) { return (Resolve-Path -Path $fallback2).Path }

    New-Item -ItemType Directory -Path $fallback1 -Force | Out-Null
    return (Resolve-Path -Path $fallback1).Path
}

$projectPath = Join-Path $PSScriptRoot '..\MultiStratManagerRepo\Quantower\QuantowerMultiStratAddOn\QuantowerMultiStratAddOn.csproj'
if (!(Test-Path $projectPath)) {
    throw "Quantower plugin project not found at $projectPath"
}

$publishDir = Join-Path $PSScriptRoot "..\MultiStratManagerRepo\Quantower\QuantowerMultiStratAddOn\bin\$Configuration\net8.0-windows\publish"

Write-Host "Publishing Quantower plugin ($Configuration)..." -ForegroundColor Cyan
$publishOutput = & dotnet publish $projectPath -c $Configuration -o $publishDir --nologo --no-restore -p:DisableImplicitNuGetFallbackFolder=true -p:RestoreFallbackFolders= -p:EnableWindowsTargeting=true 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed with exit code $LASTEXITCODE"
    if ($publishOutput) {
        Write-Error "dotnet publish output:"
        $publishOutput | ForEach-Object { Write-Error $_ }
    }
    exit $LASTEXITCODE
}
$publishOutput | Write-Verbose

if (!(Test-Path $publishDir)) {
    throw "Publish directory not found: $publishDir"
}

$pluginsRoot = Resolve-DefaultQuantowerPluginsPath -OverridePath $QuantowerPluginsPath
$destination = Join-Path $pluginsRoot $PluginFolderName

Write-Host "Deploying to $destination" -ForegroundColor Cyan
if (!(Test-Path $destination)) {
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
}

Copy-Item -Path (Join-Path $publishDir '*') -Destination $destination -Recurse -Force

Write-Host "Deployment complete." -ForegroundColor Green
Write-Host "Restart Quantower and open the panel from the Sidebar to load the plug-in." -ForegroundColor Yellow
