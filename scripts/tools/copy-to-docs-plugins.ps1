$ErrorActionPreference = 'Stop'
$docs = [Environment]::GetFolderPath('MyDocuments')
$srcCandidates = @(
  'C\Quantower\Settings\Scripts\plug-ins\MultiStratQuantower',
  (Join-Path $docs 'Quantower\Settings\Scripts\plug-ins\MultiStratQuantower'),
  (Join-Path $docs 'Quantower\Settings\Scripts\plugins\MultiStratQuantower')
)
$src = $srcCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $src) { throw "Source not found in any of: $($srcCandidates -join ', ')" }
$targets = @(
  (Join-Path $docs 'Quantower\Settings\Scripts\plugins\MultiStratQuantower'),
  (Join-Path $docs 'Quantower\Settings\Scripts\plug-ins\MultiStratQuantower')
)
foreach($t in $targets){
  if (!(Test-Path $t)) { New-Item -ItemType Directory -Path $t -Force | Out-Null }
  Copy-Item -Path (Join-Path $src '*') -Destination $t -Recurse -Force
  Write-Host "COPIED to $t"
}
