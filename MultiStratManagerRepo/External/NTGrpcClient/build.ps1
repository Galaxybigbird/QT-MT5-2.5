param(
    [string]$Configuration = "Release",
    [switch]$Clean,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

function Write-Info($msg, $color='Gray') { if (-not $Quiet) { Write-Host $msg -ForegroundColor $color } }
function Write-Ok($msg) { if (-not $Quiet) { Write-Host $msg -ForegroundColor Green } }
function Write-WarnMsg($msg) { if (-not $Quiet) { Write-Host $msg -ForegroundColor Yellow } }
function Write-Err($msg) { Write-Host $msg -ForegroundColor Red }

Write-Ok "Building NTGrpcClient.dll"
Write-Info "Configuration: $Configuration" 'Yellow'

# Detect target framework from csproj
$csproj = Join-Path $PSScriptRoot 'NTGrpcClient.csproj'
if (-not (Test-Path -LiteralPath $csproj)) {
    Write-Err "Could not find NTGrpcClient.csproj at $csproj"
    exit 1
}

[xml]$xml = Get-Content -LiteralPath $csproj
$tfm = ($xml.Project.PropertyGroup | ForEach-Object { $_.TargetFramework } | Where-Object { $_ -and $_.Trim() -ne '' } | Select-Object -First 1)
if (-not $tfm) { $tfm = 'net48' }
Write-Info "Detected TargetFramework: $tfm"

if ($Clean) {
    Write-WarnMsg "Cleaning previous build..."
    if (Test-Path "$PSScriptRoot/bin") { Remove-Item -Recurse -Force "$PSScriptRoot/bin" }
    if (Test-Path "$PSScriptRoot/obj") { Remove-Item -Recurse -Force "$PSScriptRoot/obj" }
}

Write-Info "Restoring packages..." 'Yellow'
dotnet restore "$csproj" | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Err "Restore failed"; exit 1 }

Write-Info "Building project..." 'Yellow'
dotnet build "$csproj" -c $Configuration | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Err "Build failed"; exit 1 }

$outDir = Join-Path $PSScriptRoot "bin/$Configuration/$tfm"
$dll = Join-Path $outDir 'NTGrpcClient.dll'
if (-not (Test-Path -LiteralPath $dll)) {
    Write-Err "Build output not found at $dll"
    Get-ChildItem -Recurse -File (Join-Path $PSScriptRoot 'bin') -Filter NTGrpcClient.dll | Select-Object -First 5 FullName | ForEach-Object { Write-Info "Saw: $_" }
    exit 1
}

# Report details
$fi = Get-Item -LiteralPath $dll
Write-Ok "Build completed successfully"
Write-Info ("Output: {0}" -f $fi.FullName)
Write-Info ("LastWriteTimeUtc: {0:u}" -f $fi.LastWriteTimeUtc)
Write-Info ("Size: {0:n0} bytes" -f $fi.Length)

# Copy to parent folder for convenience
$destinationPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'NTGrpcClient.dll'
Copy-Item -LiteralPath $dll -Destination $destinationPath -Force
Write-Info "Copied to: $destinationPath"

# Copy common runtime dependencies next to parent (if exist)
$deps = @(
    'Grpc.Core.dll','Grpc.Core.Api.dll','Google.Protobuf.dll','System.Text.Json.dll',
    'Microsoft.Bcl.AsyncInterfaces.dll','System.Buffers.dll','System.Memory.dll',
    'System.Numerics.Vectors.dll','System.Runtime.CompilerServices.Unsafe.dll',
    'System.Threading.Tasks.Extensions.dll','System.ValueTuple.dll','System.Text.Encodings.Web.dll',
    'grpc_csharp_ext.x64.dll','grpc_csharp_ext.x86.dll'
)
$parent = Split-Path $PSScriptRoot -Parent
foreach ($d in $deps) {
    $src = Join-Path $outDir $d
    if (Test-Path -LiteralPath $src) {
        Copy-Item -LiteralPath $src -Destination (Join-Path $parent $d) -Force
        Write-Info "Copied dependency: $d"
    }
}

exit 0