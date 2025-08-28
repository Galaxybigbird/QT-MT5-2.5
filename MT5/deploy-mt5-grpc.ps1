param(
	[string]$Mt5RootPath = $null,
	[switch]$VerboseCopy
)

$ErrorActionPreference = 'Stop'

function Write-Info($msg)  { Write-Host $msg -ForegroundColor Cyan }
function Write-Warn($msg)  { Write-Host $msg -ForegroundColor Yellow }
function Write-Err($msg)   { Write-Host $msg -ForegroundColor Red }
function Write-Ok($msg)    { Write-Host $msg -ForegroundColor Green }

function Resolve-Mt5RootPath {
	param([string]$Hint)
	if ($Hint -and (Test-Path $Hint)) { return (Resolve-Path $Hint).Path }
	$defaultRoot = Join-Path $env:APPDATA 'MetaQuotes/Terminal'
	if (-not (Test-Path $defaultRoot)) { throw "MT5 Terminal folder not found at $defaultRoot. Provide -Mt5RootPath." }
	$candidates = Get-ChildItem -Path $defaultRoot -Directory -ErrorAction SilentlyContinue | ForEach-Object {
		$mql = Join-Path $_.FullName 'MQL5'
		if (Test-Path $mql) { [PSCustomObject]@{ Path=$mql; LastWrite=(Get-Item $mql).LastWriteTimeUtc } }
	} | Sort-Object LastWrite -Descending
	if (-not $candidates) { throw "No MQL5 folders found under $defaultRoot" }
	return $candidates[0].Path
}

function Get-RelativePath {
	param([string]$Base, [string]$Path)
	$baseUri = (Resolve-Path $Base).Path
	$full    = (Resolve-Path $Path).Path
	$baseUri = if ($baseUri[-1] -eq '\\') { $baseUri } else { $baseUri + '\\' }
	return ($full -replace [regex]::Escape($baseUri), '')
}

function Select-TargetPathForSource {
	param(
		[string]$RepoMt5Root,
		[string]$Mt5Root,
		[System.IO.FileInfo]$Src
	)
	$rel = Get-RelativePath -Base $RepoMt5Root -Path $Src.FullName
	$ext = $Src.Extension.ToLowerInvariant()
	# Preferred mapping by relative structure
	if ($rel -like 'Include*') {
		$candidate = Join-Path $Mt5Root $rel
		if (Test-Path (Split-Path $candidate -Parent)) { return $candidate }
	} elseif ($rel -like 'Experts*') {
		$candidate = Join-Path $Mt5Root $rel
		if (Test-Path (Split-Path $candidate -Parent)) { return $candidate }
	} else {
		# Fallback by extension-based well-known folders
		if ($ext -eq '.mqh') {
			$candidate = Join-Path (Join-Path $Mt5Root 'Include') $Src.Name
			if (Test-Path (Split-Path $candidate -Parent)) { return $candidate }
		} elseif ($ext -eq '.mq5') {
			$candidate = Join-Path (Join-Path $Mt5Root 'Experts') $Src.Name
			if (Test-Path (Split-Path $candidate -Parent)) { return $candidate }
		}
	}

	# Search entire MQL5 tree for an existing file with the same name if structured path missing
	$matches = Get-ChildItem -Path $Mt5Root -Recurse -File -Filter $Src.Name -ErrorAction SilentlyContinue
	if ($matches.Count -eq 1) { return $matches[0].FullName }
	if ($matches.Count -gt 1) {
		# Prefer folder type by extension
		if ($ext -eq '.mqh') {
			$inc = $matches | Where-Object { $_.DirectoryName -match '\\Include(\\|$)' }
			if ($inc.Count -eq 1) { return $inc[0].FullName }
			if ($inc.Count -gt 1) { return ($inc | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName }
		} elseif ($ext -eq '.mq5') {
			$exp = $matches | Where-Object { $_.DirectoryName -match '\\Experts(\\|$)' }
			if ($exp.Count -eq 1) { return $exp[0].FullName }
			if ($exp.Count -gt 1) { return ($exp | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName }
		}
		# Otherwise pick the most recently modified target
		return ($matches | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
	}

	# No target found: create sensible default by extension
	if ($ext -eq '.mqh') { return Join-Path (Join-Path $Mt5Root 'Include') $Src.Name }
	if ($ext -eq '.mq5') { return Join-Path (Join-Path $Mt5Root 'Experts') $Src.Name }
	return Join-Path $Mt5Root $Src.Name
}

function Copy-IfChanged {
	param([string]$SrcPath, [string]$DstPath)
	$srcHash = (Get-FileHash -Algorithm SHA256 -Path $SrcPath).Hash
	$dstExists = Test-Path $DstPath
	$same = $false
	if ($dstExists) {
		try { $dstHash = (Get-FileHash -Algorithm SHA256 -Path $DstPath).Hash; $same = ($dstHash -eq $srcHash) } catch { $same = $false }
	}
	if ($same) {
		if ($VerboseCopy) { Write-Host "= Unchanged: $DstPath" -ForegroundColor DarkGray }
		return $false
	}
	$dstDir = Split-Path $DstPath -Parent
	if (-not (Test-Path $dstDir)) { New-Item -ItemType Directory -Force -Path $dstDir | Out-Null }
	Copy-Item -Force -Path $SrcPath -Destination $DstPath
	Write-Host "> Copied: $SrcPath -> $DstPath" -ForegroundColor Gray
	return $true
}

try {
	$repoMt5Root = $PSScriptRoot  # This script sits under repo MT5/
	$mt5Root = Resolve-Mt5RootPath -Hint $Mt5RootPath
	Write-Info "Deploying edited MQL sources to MT5: $mt5Root"

	# Collect candidate source files (.mq5/.mqh) under repo MT5, excluding build outputs/third-party
	$excludes = @('bin', 'obj', 'Generated', 'proto', 'build', 'Release', 'Debug')
	$sources = Get-ChildItem -Path $repoMt5Root -Recurse -File -Include *.mq5, *.mqh |
		Where-Object {
			$rel = Get-RelativePath -Base $repoMt5Root -Path $_.FullName
			-not ($excludes | ForEach-Object { $rel -match ("(^|\\)" + [regex]::Escape($_) + "(\\|$)") } | Where-Object { $_ } )
		}

	if (-not $sources) { Write-Warn "No .mq5/.mqh sources found under $repoMt5Root"; exit 0 }

	$copied = 0; $unchanged = 0
	foreach ($src in $sources) {
		$target = Select-TargetPathForSource -RepoMt5Root $repoMt5Root -Mt5Root $mt5Root -Src $src
		$didCopy = Copy-IfChanged -SrcPath $src.FullName -DstPath $target
		if ($didCopy) { $copied++ } else { $unchanged++ }
	}
	Write-Ok "Deployment complete. Copied: $copied, Unchanged: $unchanged"
	Write-Host "Recompile the EA in MetaEditor (F7) and reload on chart." -ForegroundColor DarkCyan
	exit 0
} catch {
	Write-Err $_
	exit 1
}

