param(
    [string]$TerminalRoot = "C:\Users\marth\AppData\Roaming\MetaQuotes\Terminal\7BC3F33EDFDBDBDBADB45838B9A2D03F"
)

$ErrorActionPreference = 'SilentlyContinue'

if (!(Test-Path $TerminalRoot)) {
    Write-Host "Terminal root not found: $TerminalRoot" -ForegroundColor Red
    exit 1
}

$lib = Join-Path $TerminalRoot 'MQL5\Libraries'
if (!(Test-Path $lib)) {
    Write-Host "Libraries folder not found: $lib" -ForegroundColor Red
    exit 1
}

$files = @(
    'MT5GrpcManaged.dll',
    'MT5GrpcWrapper.dll',
    'Google.Protobuf.dll',
    'Grpc.Core.dll',
    'Grpc.Core.Api.dll',
    'Grpc.Net.Client.dll',
    'Grpc.Net.Common.dll',
    'System.Text.Json.dll',
    'Microsoft.Bcl.AsyncInterfaces.dll',
    'System.Buffers.dll',
    'System.Memory.dll',
    'System.Numerics.Vectors.dll',
    'System.Runtime.CompilerServices.Unsafe.dll',
    'System.Threading.Tasks.Extensions.dll',
    'System.ValueTuple.dll',
    'System.Text.Encodings.Web.dll',
    'System.Diagnostics.DiagnosticSource.dll',
    'Microsoft.Extensions.Logging.Abstractions.dll',
    'System.Net.Http.WinHttpHandler.dll',
    'grpc_csharp_ext.x64.dll'
)

function Show-Set($title, $basePath) {
    Write-Host "=== $title ===" -ForegroundColor Cyan
    foreach ($f in $files) {
        $p = Join-Path $basePath $f
        if (Test-Path $p) {
            $i = Get-Item $p
            Write-Host ("OK   {0,-40} {1,10:N0} {2:yyyy-MM-dd HH:mm:ss}" -f $f, $i.Length, $i.LastWriteTime)
        }
        else {
            Write-Host ("MISS {0}" -f $f) -ForegroundColor Yellow
        }
    }
    Write-Host
}

Show-Set "Terminal root ($TerminalRoot)" $TerminalRoot
Show-Set "MQL5\\Libraries ($lib)" $lib

# Also scan for duplicates of the wrapper and managed DLL under the Terminal tree
Write-Host "=== Duplicate scan under Terminal root ===" -ForegroundColor Cyan
Get-ChildItem -Path $TerminalRoot -Recurse -Filter 'MT5GrpcWrapper.dll' | Select-Object FullName, LastWriteTime | Format-Table -AutoSize
Get-ChildItem -Path $TerminalRoot -Recurse -Filter 'MT5GrpcManaged.dll' | Select-Object FullName, LastWriteTime | Format-Table -AutoSize

Write-Host "Done." -ForegroundColor Green
