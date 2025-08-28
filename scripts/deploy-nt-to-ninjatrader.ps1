<#
Deploy NT gRPC client (Grpc.Core) + deps to NinjaTrader folders.
Optionally build NTGrpcClient (targeting .NET Framework 4.8 / net48) first and
optionally sync addon source files that changed.

Notes:
- We purposely avoid Grpc.Net.* and newer .NET runtime deps; NinjaTrader runs on .NET Framework 4.x.
- Build uses net48 to stay compatible with NinjaTrader 8.
#>
param(
  [string]$Source = "C:\\Documents\\Dev\\OfficialFuturesHedgebotv2\\MultiStratManagerRepo\\External\\NTGrpcClient\\bin\\Release\\net48",
  [string]$Root,
  [switch]$DryRun,
  [switch]$Build,
  [string]$Configuration = "Release",
  [string]$Framework = "net48",
  [switch]$CopySources,
  [switch]$CopySourcesToAllRoots
)

$ErrorActionPreference = 'Stop'

# Defaults: if no flags provided, build and copy sources by default
if (-not $PSBoundParameters.ContainsKey('Build')) { $Build = $true }
if (-not $PSBoundParameters.ContainsKey('CopySources')) { $CopySources = $true }

Write-Output "================ NT Deploy ================="
Write-Output ("Start: {0:yyyy-MM-dd HH:mm:ss}" -f (Get-Date))

if ($Build) {
  Write-Output "[build] Building NTGrpcClient ($Configuration, $Framework) ..."
  $proj = "C:\\Documents\\Dev\\OfficialFuturesHedgebotv2\\MultiStratManagerRepo\\External\\NTGrpcClient\\NTGrpcClient.csproj"
  if (!(Test-Path $proj)) { throw "Project not found: $proj" }
  $dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue)
  if (-not $dotnet) { throw "dotnet CLI not found in PATH. Please install .NET SDK that supports building net48." }
  $buildArgs = @("build", "`"$proj`"", "-c", $Configuration, "-f", $Framework, "-v", "minimal")
  $p = Start-Process -FilePath $dotnet.Source -ArgumentList $buildArgs -NoNewWindow -Wait -PassThru
  if ($p.ExitCode -ne 0) { throw "dotnet build failed with exit code $($p.ExitCode)" }
  $Source = Join-Path (Split-Path $proj -Parent) ("bin\\{0}\\{1}" -f $Configuration, $Framework)
  Write-Output ("[BUILD] SUCCESS -> {0}" -f (Join-Path $Source 'NTGrpcClient.dll'))
} else {
  Write-Output "[build] SKIPPED"
}

Write-Output "[deploy] Source (DLLs): $Source"
if (!(Test-Path $Source)) { throw "Build output not found: $Source" }

# Files to copy (copy only those that exist)
$desired = @(
  'NTGrpcClient.dll',
  'Grpc.Core.dll','Grpc.Core.Api.dll','Google.Protobuf.dll','System.Text.Json.dll','grpc_csharp_ext.x64.dll',
  'Microsoft.Bcl.AsyncInterfaces.dll','System.Buffers.dll','System.Memory.dll','System.Numerics.Vectors.dll',
  'System.Runtime.CompilerServices.Unsafe.dll','System.Threading.Tasks.Extensions.dll','System.ValueTuple.dll','System.Text.Encodings.Web.dll'
)
$files = Get-ChildItem -Path $Source -File -ErrorAction Stop | Where-Object { $_.Name -in $desired }
if ($files.Count -eq 0) { throw "No desired DLLs found in $Source" }
Write-Output ("[deploy] Will copy {0} files:" -f $files.Count)
$files | ForEach-Object { Write-Output (" - {0}" -f $_.Name) }

# Discover NinjaTrader roots in common paths and the documented custom path (or use override)
if ($Root) {
  if (!(Test-Path $Root)) { throw "Provided Root not found: $Root" }
  $roots = @($Root)
} else {
  $roots = @()
  $roots += Join-Path $env:USERPROFILE 'Documents\NinjaTrader 8'
  $roots += Join-Path $env:USERPROFILE 'OneDrive\Documents\NinjaTrader 8'
  $roots += Join-Path $env:USERPROFILE 'OneDrive\Desktop\NinjaTrader 8'
  $roots += Join-Path $env:USERPROFILE 'Desktop\NinjaTrader 8'
  $docPath = 'C:\\Users\\marth\\OneDrive\\Desktop\\OneDrive\\Old video editing files\\NinjaTrader 8'
  if (Test-Path $docPath) { $roots += $docPath }
  $roots = $roots | Where-Object { Test-Path $_ } | Select-Object -Unique
}
if ($roots.Count -eq 0) { throw "No NinjaTrader 8 roots found. Provide exact path or open NinjaTrader once to create it." }
Write-Output "[deploy] Roots:"
$roots | ForEach-Object { Write-Output (" - {0}" -f $_) }

# Build target folders per root
$targets = @()
foreach ($r in $roots) {
  $targets += Join-Path $r 'bin\Custom\AddOns\MultiStratManager\External'
  $targets += Join-Path $r 'bin\Custom\External'
  $targets += Join-Path $r 'External'
  $targets += Join-Path $r 'bin\Custom\AddOns\MultiStratManager'
  $targets += Join-Path $r 'bin\Custom\AddOns\MultiStratManager\bin\External'
  $targets += Join-Path $r 'bin\External'
  $targets += Join-Path $r 'bin\Custom'
}
$targets = $targets | Select-Object -Unique


# Copy DLLs (or DryRun)
$summary = @()
foreach ($t in $targets) {
  try {
    if (!(Test-Path (Split-Path $t -Parent))) { continue } # skip if root is missing
    if ($DryRun) {
      $summary += [pscustomobject]@{ Target=$t; Status='DRYRUN'; Files=$files.Count }
    } else {
      if (!(Test-Path $t)) { New-Item -ItemType Directory -Path $t -Force | Out-Null }
      foreach ($f in $files) { Copy-Item -Path $f.FullName -Destination (Join-Path $t $f.Name) -Force }
      $summary += [pscustomobject]@{ Target=$t; Status='OK'; Files=$files.Count }
    }
  } catch {
    $summary += [pscustomobject]@{ Target=$t; Status=('ERR: ' + $_.Exception.Message); Files=0 }
  }
}

Write-Output "[deploy] Copy summary:"
$summary | ForEach-Object { Write-Output (" {0} -> {1} (files: {2})" -f $_.Status, $_.Target, $_.Files) }

# Aggregate copy result
$okCount = ($summary | Where-Object { $_.Status -eq 'OK' }).Count
$tgtCount = $summary.Count
if ($tgtCount -gt 0) {
  if ($okCount -gt 0) { Write-Output ("[COPY] SUCCESS to {0}/{1} targets" -f $okCount, $tgtCount) } else { Write-Output "[COPY] SKIPPED or FAILED" }
}

# Verify primary External under each root
foreach ($r in $roots) {
  $primary = Join-Path $r 'bin\Custom\AddOns\MultiStratManager\External'
  if (Test-Path $primary) {
    Write-Output ("[deploy] Listing: {0}" -f $primary)
    Get-ChildItem -Path $primary -File | Where-Object { $_.Name -in $desired } | Sort-Object Name | ForEach-Object {
      Write-Output ("  - {0}  {1}" -f $_.Name, $_.LastWriteTime)
    }
  }
}

# Ensure critical references exist in compile path for NinjaTrader (bin\Custom for all detected roots)
try {
  $mustRefs = @('Grpc.Core.dll','Grpc.Core.Api.dll','Google.Protobuf.dll','System.Text.Json.dll')
  foreach ($r in $roots) {
    $compileDir = Join-Path $r 'bin\Custom'
    if ((Test-Path $compileDir) -and (-not $DryRun)) {
      foreach ($m in $mustRefs) {
        $srcFile = Join-Path $Source $m
        $dstFile = Join-Path $compileDir $m
        if (Test-Path $srcFile) {
          try { Copy-Item -Path $srcFile -Destination $dstFile -Force } catch { Write-Output ("[deploy] WARN: could not place {0} in {1}: {2}" -f $m, $compileDir, $_.Exception.Message) }
        }
      }
    }
  }
} catch { Write-Output ("[deploy] Reference check warning: {0}" -f $_.Exception.Message) }

# Purge DLL-only source files accidentally placed under NinjaTrader bin\Custom (avoid NinjaScript compiling them)
try {
  foreach ($r in $roots) {
    $customRoot = Join-Path $r 'bin\Custom'
    if (Test-Path $customRoot) {
      $patterns = @('UnifiedLogWriter*.cs')
      $removed = 0
      foreach ($pat in $patterns) {
        $hits = Get-ChildItem -Path $customRoot -Recurse -Filter $pat -File -ErrorAction SilentlyContinue
        foreach ($f in $hits) {
          try { Remove-Item -Force $f.FullName; $removed++ } catch {}
        }
      }
      if ($removed -gt 0) { Write-Output ("[sources] Removed disallowed source files under {0}: {1}" -f $customRoot, $removed) }
    }
  }
} catch { Write-Output ("[sources] Purge (global) warning: {0}" -f $_.Exception.Message) }

# Optionally copy updated addon source files (compiled by NinjaTrader on rebuild)
if ($CopySources) {
  Write-Output "[sources] Syncing addon source files..."
  $srcRoot = "C:\\Documents\\Dev\\OfficialFuturesHedgebotv2\\MultiStratManagerRepo"
  # Only top-level .cs files (NinjaScript addon sources)
  $addonSrc = Get-ChildItem -Path $srcRoot -Filter '*.cs' -File -ErrorAction Stop
  # No subfolder .cs files should be copied as addon sources
  # Enforce exclusive source location by default (preferred root), or sync to all roots if requested
  $preferredRoot = "C:\\Users\\marth\\OneDrive\\Desktop\\OneDrive\\Old video editing files\\NinjaTrader 8"
  $sourcesSynced = 0
  $addonTarget = Join-Path $preferredRoot 'bin\Custom\AddOns\MultiStratManager'
  if ($DryRun) {
    Write-Output ("[sources] DRYRUN -> {0}" -f $addonTarget)
    $addonSrc | ForEach-Object { Write-Output ("  - {0}" -f $_.Name) }
  } else {
    if ($CopySourcesToAllRoots) {
      foreach ($r in $roots) {
        $rtAddon = Join-Path $r 'bin\Custom\AddOns\MultiStratManager'
        if (!(Test-Path $rtAddon)) { New-Item -ItemType Directory -Path $rtAddon -Force | Out-Null }
        foreach ($s in $addonSrc) { Copy-Item -Force $s.FullName -Destination $rtAddon }
        $sourcesSynced++
        Write-Output ("[sources] Synced to: {0}" -f $rtAddon)
      }
    } else {
    if (!(Test-Path $addonTarget)) { New-Item -ItemType Directory -Path $addonTarget -Force | Out-Null }
      foreach ($s in $addonSrc) { Copy-Item -Force $s.FullName -Destination $addonTarget }
    Write-Output ("[sources] Synced to: {0}" -f $addonTarget)
    $sourcesSynced++
      # Also purge duplicates within the preferred root outside the exact AddOns path
      try {
        $customRoot = Join-Path $preferredRoot 'bin\Custom'
        if (Test-Path $customRoot) {
          # In AddOns folder, keep only the top-level repo .cs set; remove any stray .cs (e.g., External\UnifiedLogWriter.cs)
          $allowed = $addonSrc | ForEach-Object { $_.Name }
          $addonCs = Get-ChildItem -Path $addonTarget -Filter '*.cs' -File -ErrorAction SilentlyContinue
          foreach ($f in $addonCs) {
            if ($allowed -notcontains $f.Name) {
              try { Remove-Item -Force $f.FullName } catch {}
            }
          }
          Write-Output "[sources] Purged non-root .cs files from AddOns path"
        }
      } catch { Write-Output ("[sources] Purge warning: {0}" -f $_.Exception.Message) }
    }

    # Verify critical files exist at exact path
    $msmSrc = Join-Path $srcRoot 'MultiStratManager.cs'
    $msmDst = Join-Path $addonTarget 'MultiStratManager.cs'
    if (!(Test-Path $msmDst)) {
      try { if (Test-Path $msmSrc) { Copy-Item -Force $msmSrc -Destination $addonTarget } } catch {}
    }
    if (Test-Path $msmDst) { Write-Output "[check] MultiStratManager.cs OK" } else { Write-Output "[check] ERROR: MultiStratManager.cs missing at addon path"; throw "MultiStratManager.cs not deployed" }

    # Verify required core sources exist in AddOns path
    foreach ($name in @('MultiStratManager.cs','IndicatorCalculator.cs')) {
      $dst = Join-Path $addonTarget $name
      if (Test-Path $dst) { Write-Output ("[check] {0} OK" -f $name) } else { Write-Output ("[check] ERROR: {0} missing at addon path" -f $name) }
    }
  }
  if ($sourcesSynced -gt 0) { Write-Output ("[SOURCES] SUCCESS to {0} root(s) (exclusive)" -f $sourcesSynced) } else { Write-Output "[SOURCES] SKIPPED" }
}

Write-Output "-------------------------------------------"
$buildStatus = if ($Build) { 'OK' } else { 'SKIPPED' }
$copyStatus = if ($okCount -gt 0) { 'OK' } else { if ($tgtCount -eq 0) { 'SKIPPED' } else { 'SKIPPED' } }
$sourcesStatus = if ($CopySources -and ($sourcesSynced -gt 0)) { 'OK' } elseif ($CopySources) { 'SKIPPED' } else { 'SKIPPED' }
Write-Output ("[RESULT] BUILD={0} COPY={1} SOURCES={2}" -f $buildStatus, $copyStatus, $sourcesStatus)
Write-Output ("End:   {0:yyyy-MM-dd HH:mm:ss}" -f (Get-Date))
Write-Output "[deploy] Done. If NinjaTrader is open, rebuild NinjaScript (F5) to load changes."
