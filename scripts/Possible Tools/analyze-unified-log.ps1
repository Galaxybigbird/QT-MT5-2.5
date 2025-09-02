param(
    [Parameter(Mandatory=$true)]
    [string]$Path,
    [int]$Tail = 800,
    [string]$BaseId
)

if (-not (Test-Path -LiteralPath $Path)) {
    Write-Error "Path not found: $Path"
    exit 1
}

try {
    $lines = Get-Content -LiteralPath $Path -Tail $Tail -ErrorAction Stop
} catch {
    Write-Error "Failed to read file: $($_.Exception.Message)"
    exit 1
}

# Parse JSON lines into simplified event objects
$events = @()
foreach ($line in $lines) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    try { $e = $line | ConvertFrom-Json } catch { continue }
    $corr = $e.correlation_id
    if (-not $corr) {
        try { if ($e.tags -and $e.tags.correlation_id) { $corr = [string]$e.tags.correlation_id } } catch {}
    }
    $ev = [pscustomobject]@{
        ts   = $e.timestamp
        src  = $e.source
        lvl  = $e.level
        base = $e.base_id
        corr = $corr
        norm = $null
        msg  = $e.message
    }
    if ($e.tags -and $null -ne $e.tags.normalized) { $ev.norm = [bool]$e.tags.normalized }
    $events += $ev
}

function Normalize-Message {
    param([string]$m)
    if ([string]::IsNullOrWhiteSpace($m)) { return '' }
    return ($m -replace '\s+', ' ').Trim()
}

# Count adjacent duplicate events (same src, lvl, base, and normalized message)
$adjDupIdx = @()
$adjDupIdxWithBase = @()
for ($i = 1; $i -lt $events.Count; $i++) {
    $a = $events[$i-1]
    $b = $events[$i]
    if ($a.src -eq $b.src -and $a.lvl -eq $b.lvl -and $a.base -eq $b.base) {
        $am = Normalize-Message $a.msg
        $bm = Normalize-Message $b.msg
        if ($am -eq $bm) {
            $adjDupIdx += $i
            if ($a.base) { $adjDupIdxWithBase += $i }
        }
    }
}

# Find correlation drift (same base_id but multiple corr IDs)
$drift = @()
$byBase = $events | Where-Object { $_.base } | Group-Object base
foreach ($g in $byBase) {
    $corrs = $g.Group | Where-Object { $_.corr } | Select-Object -ExpandProperty corr | Sort-Object -Unique
    if ($corrs.Count -gt 1) {
        $drift += [pscustomobject]@{ base=$g.Name; corrCount=$corrs.Count; corrs=($corrs -join ',') }
    }
}

# Output summary
[pscustomobject]@{
    total              = $events.Count
    withBaseId         = ($events | Where-Object { $_.base }).Count
    withCorrelationId  = ($events | Where-Object { $_.corr }).Count
    adjacentDuplicates = $adjDupIdx.Count
    adjacentDupWithBase= $adjDupIdxWithBase.Count
    driftGroups        = $drift.Count
} | Format-List *

"`n--- Last 5 events ---"
$events | Select-Object -Last 5 |
    Select-Object ts, lvl, src, base, corr, norm, @{N='msg';E={ $m = Normalize-Message $_.msg; if ($m.Length -gt 140) { $m.Substring(0,140) } else { $m } }} |
    Format-Table -AutoSize

if ($adjDupIdx.Count -gt 0) {
    "`n--- First duplicate pair ---"
    $i = $adjDupIdx[0]
    $events[($i-1)..$i] | Select-Object ts,lvl,src,base,corr,norm,@{N='msg';E={ Normalize-Message $_.msg }} | Format-Table -AutoSize
}

if ($drift.Count -gt 0) {
    "`n--- Correlation drift groups (top 5) ---"
    $drift | Select-Object -First 5 | Format-Table -AutoSize
}

"`n--- Bases with correlation_id (sample up to 5) ---"
$events |
    Where-Object { $_.base -and $_.corr } |
    Group-Object base |
    Select-Object -First 5 @{N='base';E={$_.Name}}, @{N='corrCount';E={(($_.Group | Select-Object -ExpandProperty corr | Sort-Object -Unique).Count)}} |
    Format-Table -AutoSize

if ($BaseId) {
        "`n--- Details for base_id: $BaseId ---"
        $events | Where-Object { $_.base -eq $BaseId } |
            Select-Object ts,lvl,src,base,corr,norm,@{N='msg';E={ Normalize-Message $_.msg }} |
            Format-Table -AutoSize
}

exit 0
