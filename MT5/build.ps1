# Build script for MT5GrpcClient.dll
# Targets .NET Framework 4.8 for compatibility with MT5 (x64)

param(
    [string]$Configuration = "Release",
    [string]$Platform = "x64",
    [string]$Mt5RootPath = "C:\Users\marth\AppData\Roaming\MetaQuotes\Terminal\7BC3F33EDFDBDBDBADB45838B9A2D03F\MQL5",
    [string]$TerminalRootPath = $null
)

Write-Host "Building MT5GrpcClient.dll..." -ForegroundColor Green
Write-Host "Configuration: $Configuration" -ForegroundColor Yellow
Write-Host "Platform: $Platform" -ForegroundColor Yellow

Push-Location $PSScriptRoot
try {
# Clean previous build
Write-Host "`nCleaning previous build..." -ForegroundColor Yellow
if (Test-Path "bin") {
    Remove-Item -Path "bin" -Recurse -Force
}
if (Test-Path "obj") {
    Remove-Item -Path "obj" -Recurse -Force
}

# Restore NuGet packages (for this project only)
Write-Host "`nRestoring NuGet packages..." -ForegroundColor Yellow
dotnet restore "MT5GrpcClient.csproj"

if ($LASTEXITCODE -ne 0) {
    Write-Host "Failed to restore NuGet packages!" -ForegroundColor Red
    exit 1
}

# Build the project (this csproj only to avoid pulling solution dependencies)
Write-Host "`nBuilding project..." -ForegroundColor Yellow
dotnet build "MT5GrpcClient.csproj" -c $Configuration

if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}

# Copy output to repo root for convenience
$outputPath = "bin\$Configuration\net48\MT5GrpcManaged.dll"
$destinationPath = "MT5GrpcManaged.dll"

if (Test-Path $outputPath) {
    Write-Host "`nCopying output DLL..." -ForegroundColor Yellow
    Copy-Item -Path $outputPath -Destination $destinationPath -Force
    
    # Also copy required dependencies
    $dependencies = @(
        "Google.Protobuf.dll",
        "Grpc.Core.dll",
        "Grpc.Core.Api.dll",
        "Grpc.Net.Client.dll",
        "Grpc.Net.Common.dll",
        "System.Text.Json.dll",
        # Support libraries often required on .NET Framework
        "Microsoft.Bcl.AsyncInterfaces.dll",
        "System.Buffers.dll",
        "System.Memory.dll",
        "System.Numerics.Vectors.dll",
        "System.Runtime.CompilerServices.Unsafe.dll",
        "System.Threading.Tasks.Extensions.dll",
        "System.ValueTuple.dll",
        "System.Text.Encodings.Web.dll",
        "System.Net.Http.dll",
        "netstandard.dll",
        "System.Diagnostics.DiagnosticSource.dll",
        "Microsoft.Extensions.Logging.Abstractions.dll",
        "System.Net.Http.WinHttpHandler.dll",
        "grpc_csharp_ext.x64.dll"
    )
    
    foreach ($dep in $dependencies) {
        $depPath = "bin\$Configuration\net48\$dep"
        if (Test-Path $depPath) {
            Copy-Item -Path $depPath -Destination $dep -Force
            Write-Host "Copied dependency: $dep" -ForegroundColor Gray
        }
    }
    
    Write-Host "`nBuild completed successfully!" -ForegroundColor Green
    Write-Host "Output: $destinationPath" -ForegroundColor Green

    # Also copy outputs to real MT5 directories per repo instructions
    Write-Host "`nDeploying to MT5 path: $Mt5RootPath" -ForegroundColor Yellow
    if (!(Test-Path $Mt5RootPath)) {
        Write-Host "MT5 root path not found: $Mt5RootPath" -ForegroundColor Red
    } else {
        $mt5Lib   = Join-Path $Mt5RootPath 'Libraries'
        $mt5Incl  = Join-Path $Mt5RootPath 'Include\\gRPC'
        $mt5Exp   = Join-Path $Mt5RootPath 'Experts'

        New-Item -ItemType Directory -Force -Path $mt5Lib, $mt5Incl, $mt5Exp | Out-Null

    # Managed DLLs (MT5GrpcManaged.dll) + deps
    $managed = Join-Path "bin/$Configuration/net48" 'MT5GrpcManaged.dll'
    $deps = @(
    'Google.Protobuf.dll',
    'Grpc.Core.dll',
        'Grpc.Core.Api.dll',
        'Grpc.Net.Client.dll',
        'Grpc.Net.Common.dll',
        'System.Text.Json.dll',
        # Support libraries often required on .NET Framework
        'Microsoft.Bcl.AsyncInterfaces.dll',
        'System.Buffers.dll',
        'System.Memory.dll',
        'System.Numerics.Vectors.dll',
        'System.Runtime.CompilerServices.Unsafe.dll',
        'System.Threading.Tasks.Extensions.dll',
        'System.ValueTuple.dll',
        'System.Text.Encodings.Web.dll',
    'System.Net.Http.dll',
    'netstandard.dll',
    'System.Diagnostics.DiagnosticSource.dll',
    'Microsoft.Extensions.Logging.Abstractions.dll',
    'System.Net.Http.WinHttpHandler.dll',
    'grpc_csharp_ext.x64.dll'
    ) | ForEach-Object { Join-Path "bin/$Configuration/net48" $_ }

    if (Test-Path $managed) { Copy-Item -Force $managed (Join-Path $mt5Lib 'MT5GrpcManaged.dll') ; Write-Host "Copied: MT5GrpcManaged.dll -> Libraries" -ForegroundColor Gray }
    foreach ($d in $deps) { if (Test-Path $d) { Copy-Item -Force $d $mt5Lib ; Write-Host "Copied: $(Split-Path $d -Leaf) -> Libraries" -ForegroundColor Gray } }

        # Native wrapper(s) (if present in repo root or build output), copy to Libraries and Terminal root
        $nativeDlls = @(
            'MT5GrpcWrapper.dll',
            'MT5GrpcWrapper.exp',
            'MT5GrpcWrapper.lib',
            'MT5GrpcWrapper.def',
            'MT5GrpcWrapper.pdb',
            'MT5GrpcWrapper.map',
            'MT5GrpcClientNative.dll',
            'MT5GrpcClientSimple.dll',
            'MT5GrpcWrapper.exp',
            'MT5GrpcWrapper.lib',
            'grpc_csharp_ext.x64.dll'
        )
        foreach ($ndll in $nativeDlls) {
            $srcNative = Join-Path (Get-Location) $ndll
            if (Test-Path $srcNative) {
                Copy-Item -Force $srcNative $mt5Lib ; Write-Host "Copied: $ndll -> Libraries" -ForegroundColor Gray
            }
        }
        # Optionally, copy from bin/Release/net48 if not found in repo root
        $binNative = "bin/$Configuration/net48"
        foreach ($ndll in $nativeDlls) {
            $srcNative = Join-Path $binNative $ndll
            if (Test-Path $srcNative) {
                Copy-Item -Force $srcNative $mt5Lib ; Write-Host "Copied: $ndll (bin) -> Libraries" -ForegroundColor Gray
            }
        }

        # Also copy native C++ pure client from cpp-grpc-client build if present
        $cppBuildDir = Join-Path (Get-Location) 'cpp-grpc-client\\build\\bin\\Release'
        $cppClientNative = Join-Path $cppBuildDir 'MT5GrpcClientNative.dll'
        $cppClientOriginal = Join-Path $cppBuildDir 'MT5GrpcClient.dll'
        if (Test-Path $cppClientNative) {
            Copy-Item -Force $cppClientNative $mt5Lib ; Write-Host "Copied: MT5GrpcClientNative.dll (cpp-grpc-client) -> Libraries" -ForegroundColor Gray
        } elseif (Test-Path $cppClientOriginal) {
            # Copy and rename original MT5GrpcClient.dll as MT5GrpcClientNative.dll to avoid name collision
            Copy-Item -Force $cppClientOriginal (Join-Path $mt5Lib 'MT5GrpcClientNative.dll') ; Write-Host "Copied: MT5GrpcClient.dll as MT5GrpcClientNative.dll -> Libraries" -ForegroundColor Gray
        }

        # Copy C++ client runtime dependencies if present
        $cppDeps = @('abseil_dll.dll','cares.dll','libcrypto-3-x64.dll','libprotobuf.dll','libssl-3-x64.dll','re2.dll','zlib1.dll')
        foreach ($dep in $cppDeps) {
            $srcDep = Join-Path $cppBuildDir $dep
            if (Test-Path $srcDep) { Copy-Item -Force $srcDep $mt5Lib ; Write-Host "Copied: $dep (cpp-grpc-client) -> Libraries" -ForegroundColor Gray }
        }

        # Copy updated include we edited
        $srcIncl = Join-Path (Get-Location) 'Include\\gRPC\\UnifiedLogging.mqh'
        if (Test-Path $srcIncl) { Copy-Item -Force $srcIncl (Join-Path $mt5Incl 'UnifiedLogging.mqh') ; Write-Host "Copied: UnifiedLogging.mqh -> Include\\gRPC" -ForegroundColor Gray }

        # Copy updated EA we edited
        $srcEa = Join-Path (Get-Location) 'ACHedgeMaster_gRPC.mq5'
        if (Test-Path $srcEa) { Copy-Item -Force $srcEa (Join-Path $mt5Exp 'ACHedgeMaster_gRPC.mq5') ; Write-Host "Copied: ACHedgeMaster_gRPC.mq5 -> Experts" -ForegroundColor Gray }

        # Also copy managed DLLs to Terminal root so CLR probing can find them when only filename is provided
        # Always use TerminalRootPath as the authoritative root for Terminal root copy
        if (![string]::IsNullOrWhiteSpace($TerminalRootPath) -and (Test-Path $TerminalRootPath)) {
            Write-Host "Copying managed/native DLLs to Terminal root: $TerminalRootPath" -ForegroundColor Yellow
            # Helper function for verbose copy and verification
            function Copy-And-Verify($src, $dstDir, $dstName) {
                $dst = Join-Path $dstDir $dstName
                Write-Host "Attempting copy: $src -> $dst" -ForegroundColor Cyan
                if (!(Test-Path $src)) {
                    Write-Host "WARNING: Source file does not exist: $src" -ForegroundColor Red
                    return
                }
                Copy-Item -Force $src $dst
                if (Test-Path $dst) {
                    Write-Host "SUCCESS: $dstName copied to $dstDir" -ForegroundColor Green
                } else {
                    Write-Host "FAIL: $dstName NOT found in $dstDir after copy!" -ForegroundColor Red
                }
            }

            if (Test-Path $managed) { Copy-And-Verify $managed $TerminalRootPath 'MT5GrpcManaged.dll' }
            foreach ($d in $deps) {
                if (Test-Path $d) { Copy-And-Verify $d $TerminalRootPath (Split-Path $d -Leaf) }
            }
            foreach ($ndll in $nativeDlls) {
                $srcNative = Join-Path (Get-Location) $ndll
                if (Test-Path $srcNative) { Copy-And-Verify $srcNative $TerminalRootPath $ndll }
            }
            foreach ($ndll in $nativeDlls) {
                $srcNative = Join-Path $binNative $ndll
                if (Test-Path $srcNative) { Copy-And-Verify $srcNative $TerminalRootPath $ndll }
            }
            # Terminal root copies for native client and deps
            if (Test-Path $cppClientNative) { Copy-And-Verify $cppClientNative $TerminalRootPath 'MT5GrpcClientNative.dll' }
            elseif (Test-Path $cppClientOriginal) { Copy-And-Verify $cppClientOriginal $TerminalRootPath 'MT5GrpcClientNative.dll' }
            foreach ($dep in $cppDeps) {
                $srcDep = Join-Path $cppBuildDir $dep
                if (Test-Path $srcDep) { Copy-And-Verify $srcDep $TerminalRootPath $dep }
            }
        } else {
            Write-Host "Warning: TerminalRootPath not provided or does not exist. Skipping Terminal root copy." -ForegroundColor Yellow
        }

        Write-Host "`nDeployment to MT5 completed." -ForegroundColor Green
        Write-Host "Recompile the EA in MetaEditor and ensure DLL imports are enabled." -ForegroundColor Cyan
    }
} else {
    Write-Host "Build output not found!" -ForegroundColor Red
    exit 1
}

# Generate documentation (optional)
Write-Host "`nGenerating XML documentation..." -ForegroundColor Yellow
$docPath = "bin\$Configuration\net48\MT5GrpcClient.xml"
if (Test-Path $docPath) {
    Copy-Item -Path $docPath -Destination "MT5GrpcClient.xml" -Force
    Write-Host "Documentation generated: MT5GrpcClient.xml" -ForegroundColor Green
}

Write-Host "`nReady for MT5 integration!" -ForegroundColor Green
} finally {
    Pop-Location
}