param(
    [Parameter(Mandatory=$true)]
    [string]$Path,
    [int]$Tail = 2000
)

if (-not (Test-Path -LiteralPath $Path)) {
    Write-Error "Path not found: $Path"
    exit 1
}

function Normalize-Message([string]$m){
    if ([string]::IsNullOrWhiteSpace($m)) { return '' }
    return ($m -replace '\s+', ' ').Trim()
}

try { $lines = Get-Content -LiteralPath $Path -Tail $Tail -ErrorAction Stop } catch { Write-Error $_; exit 1 }

$evts = @()
foreach($l in $lines){
    if([string]::IsNullOrWhiteSpace($l)){ continue }
    try { $e = $l | ConvertFrom-Json } catch { continue }
    if($e.message -and $e.base_id){
        $evts += [pscustomobject]@{ msg=(Normalize-Message $e.message); base=$e.base_id; lvl=$e.level; src=$e.source }
    }
}

$groups = $evts | Group-Object -Property msg
$multi = @()
foreach($g in $groups){
    $bases = $g.Group | Select-Object -ExpandProperty base | Sort-Object -Unique
    if($bases.Count -gt 1){
        $multi += [pscustomobject]@{ msg=$g.Name; baseCount=$bases.Count; bases=($bases -join ', ') }
    }
}

if($multi.Count -eq 0){
    Write-Output "No multi-base messages found in tail=$Tail."
} else {
    $multi | Sort-Object -Property baseCount -Descending | Select-Object -First 15 |
        Format-Table -AutoSize
}

exit 0
