@echo off
setlocal enabledelayedexpansion
echo Building C++ gRPC client for MT5...
echo.

REM Check if we're in the correct directory
if not exist "MT5GrpcClient.h" (
    echo ERROR: MT5GrpcClient.h not found. Please run this script from the cpp-grpc-client directory.
    exit /b 1
)

REM Ensure MSVC build environment is initialized (cl.exe available)
where cl >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    set "_VCVARS=C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat"
    if exist "%_VCVARS%" (
        echo Initializing Visual Studio x64 build environment...
        call "%_VCVARS%" >nul
    ) else (
        echo WARNING: Could not find vcvars64.bat at default path. If build fails, run from an "x64 Native Tools Command Prompt for VS".
    )
)

REM Check if vcpkg is available; fallback to common VS path
where vcpkg >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    set "_VCPKG_CANDIDATE=C:\Program Files\Microsoft Visual Studio\2022\Community\VC\vcpkg"
    if exist "%_VCPKG_CANDIDATE%\vcpkg.exe" (
        echo vcpkg not in PATH; using VS-installed vcpkg at: %_VCPKG_CANDIDATE%
        set "VCPKG_ROOT=%_VCPKG_CANDIDATE%"
        set "PATH=%_VCPKG_CANDIDATE%;%PATH%"
    ) else (
        echo ERROR: vcpkg not found in PATH and default VS path not present.
        echo Please install vcpkg and run 'vcpkg integrate install' first.
        echo Then run vcpkg_install.bat to install dependencies.
        exit /b 1
    )
)

REM Check if CMake is available
where cmake >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: CMake not found in PATH.
    echo Please install CMake and add it to your PATH.
    exit /b 1
)

REM Clean and recreate build directory to avoid stale generated protobufs
if exist "build" (
    echo Cleaning previous build directory...
    rmdir /s /q build
)
mkdir build
cd build

echo Configuring CMake with vcpkg toolchain...
if defined VCPKG_ROOT (
    echo Using VCPKG_ROOT: %VCPKG_ROOT%
    cmake .. -DCMAKE_TOOLCHAIN_FILE="%VCPKG_ROOT%/scripts/buildsystems/vcpkg.cmake" -DVCPKG_TARGET_TRIPLET=x64-windows -A x64
) else (
    echo VCPKG_ROOT not set. Trying to auto-detect vcpkg...
    for /f "tokens=*" %%i in ('where vcpkg 2^>nul') do set "VCPKG_EXE=%%i"
    if defined VCPKG_EXE (
        for %%i in ("%VCPKG_EXE%") do set "VCPKG_DIR=%%~dpi"
        set "VCPKG_DIR=!VCPKG_DIR:~0,-1!"
        echo Found vcpkg at: !VCPKG_DIR!
        cmake .. -DCMAKE_TOOLCHAIN_FILE="!VCPKG_DIR!/scripts/buildsystems/vcpkg.cmake" -DVCPKG_TARGET_TRIPLET=x64-windows -A x64
    ) else (
    echo ERROR: Could not find vcpkg. Please set VCPKG_ROOT environment variable.
    cd ..
    exit /b 1
    )
)

if %ERRORLEVEL% NEQ 0 (
    echo ERROR: CMake configuration failed.
    echo Make sure you have run 'vcpkg integrate install' and installed the required packages.
    cd ..
    exit /b 1
)

echo Building MT5GrpcClient.dll...
cmake --build . --config Release

if %ERRORLEVEL% NEQ 0 (
    echo ERROR: Build failed.
    cd ..
    exit /b 1
)

echo.
echo ✅ Build completed successfully!
set "BUILD_OK=YES"
echo.
REM ---------------------------------------------------------------------
REM Deploy to MT5 MQL5 folders (mirrors build.ps1 behavior)
REM ---------------------------------------------------------------------
REM Default MT5 data folder (match build.ps1). Override by setting MT5_ROOT before running.
if not defined MT5_ROOT set "MT5_ROOT=C:\Users\marth\AppData\Roaming\MetaQuotes\Terminal\7BC3F33EDFDBDBDBADB45838B9A2D03F\MQL5"
set "MT5_LIB=%MT5_ROOT%\Libraries"
set "MT5_INC=%MT5_ROOT%\Include\gRPC"
set "MT5_EXP=%MT5_ROOT%\Experts"

echo Deploying to MT5 path: %MT5_ROOT%
if not exist "%MT5_ROOT%" (
    echo WARNING: MT5 root not found: %MT5_ROOT%
    echo Tip: Open MetaTrader 5, use File -> Open Data Folder, and update MT5_ROOT accordingly.
) else (
    if not exist "%MT5_LIB%" (
        echo Creating folder: %MT5_LIB%
        mkdir "%MT5_LIB%"
    )
    if not exist "%MT5_INC%" (
        echo Creating folder: %MT5_INC%
        mkdir "%MT5_INC%"
    )
    if not exist "%MT5_EXP%" (
        echo Creating folder: %MT5_EXP%
        mkdir "%MT5_EXP%"
    )

    REM Prefer build\bin\Release output location
    set "BUILD_BIN=!CD!\bin\Release"

    if not exist "!BUILD_BIN!\MT5GrpcClientNative.dll" (
        if not exist "!BUILD_BIN!\MT5GrpcClient.dll" (
            REM Some CMake setups use build\Release
            set "BUILD_BIN=!CD!\Release"
        )
    )

    if exist "!BUILD_BIN!\MT5GrpcClientNative.dll" (
        call :CopyAndVerify "!BUILD_BIN!\MT5GrpcClientNative.dll" "%MT5_LIB%" "MT5GrpcClientNative.dll" "COPIED_NATIVE"
    ) else (
        if exist "!BUILD_BIN!\MT5GrpcClient.dll" (
            call :CopyAndVerify "!BUILD_BIN!\MT5GrpcClient.dll" "%MT5_LIB%" "MT5GrpcClientNative.dll" "COPIED_NATIVE"
            if defined COPIED_NATIVE echo Note: Renamed MT5GrpcClient.dll -> MT5GrpcClientNative.dll
        ) else (
            echo WARNING: Build output not found at "!BUILD_BIN!". Skipping DLL copy.
        )
    )

    REM Copy C++ client runtime dependencies (if present), like build.ps1
    for %%D in (abseil_dll.dll cares.dll libcrypto-3-x64.dll libprotobuf.dll libssl-3-x64.dll re2.dll zlib1.dll) do (
        if exist "!BUILD_BIN!\%%D" (
            call :CopyAndVerify "!BUILD_BIN!\%%D" "%MT5_LIB%" "%%D" "COPIED_DEP_%%D"
        )
    )

    REM Copy updated include and EA files from repo
    pushd ..
    if exist "..\Include\gRPC\UnifiedLogging.mqh" (
        call :CopyAndVerify "..\Include\gRPC\UnifiedLogging.mqh" "%MT5_INC%" "UnifiedLogging.mqh" "COPIED_ULOG"
    )
    if exist "..\ACHedgeMaster_gRPC.mq5" (
        call :CopyAndVerify "..\ACHedgeMaster_gRPC.mq5" "%MT5_EXP%" "ACHedgeMaster_gRPC.mq5" "COPIED_EA"
    )
    popd
)

REM Optional: copy to a Terminal root if TERMINAL_ROOT env var is provided
if defined TERMINAL_ROOT (
    if exist "%TERMINAL_ROOT%" (
        echo Copying native client and deps to Terminal root: %TERMINAL_ROOT%
        if exist "!BUILD_BIN!\MT5GrpcClientNative.dll" (
            call :CopyAndVerify "!BUILD_BIN!\MT5GrpcClientNative.dll" "%TERMINAL_ROOT%" "MT5GrpcClientNative.dll" "COPIED_TERM_NATIVE"
        ) else (
            if exist "!BUILD_BIN!\MT5GrpcClient.dll" (
                call :CopyAndVerify "!BUILD_BIN!\MT5GrpcClient.dll" "%TERMINAL_ROOT%" "MT5GrpcClientNative.dll" "COPIED_TERM_NATIVE"
            )
        )
        for %%D in (abseil_dll.dll cares.dll libcrypto-3-x64.dll libprotobuf.dll libssl-3-x64.dll re2.dll zlib1.dll) do (
            if exist "!BUILD_BIN!\%%D" (
                call :CopyAndVerify "!BUILD_BIN!\%%D" "%TERMINAL_ROOT%" "%%D" "COPIED_TERM_DEP_%%D"
            )
        )
    ) else (
        echo WARNING: TERMINAL_ROOT not found: %TERMINAL_ROOT%
    )
)

echo.
echo ✅ Build and initial deploy complete.
echo Close MetaTrader before copying to avoid file locks, then reopen and recompile the EA.
echo.
REM ---------------------------------------------------------------------
REM Post-deploy: run smart copier to detect and copy edited .mq5/.mqh files
REM ---------------------------------------------------------------------
cd ..
if exist "..\deploy-mt5-grpc.ps1" (
    echo Running smart deploy to copy changed .mq5/.mqh files to their live locations...
    powershell -NoProfile -ExecutionPolicy Bypass -File "..\deploy-mt5-grpc.ps1" -VerboseCopy
    set "SMART_DEPLOY_RC=%ERRORLEVEL%"
    if %SMART_DEPLOY_RC% EQU 0 (
        echo Smart deploy: SUCCESS
    ) else (
        echo Smart deploy: FAILED with exit code %SMART_DEPLOY_RC%
    )
)
echo ================= SUMMARY =================
echo Build:           %BUILD_OK%
echo Native DLL copy: %COPIED_NATIVE%
echo ULog .mqh copy:  %COPIED_ULOG%
echo EA .mq5 copy:    %COPIED_EA%
if defined COPIED_TERM_NATIVE echo Terminal native: %COPIED_TERM_NATIVE%
if defined SMART_DEPLOY_RC echo Smart deploy RC: %SMART_DEPLOY_RC%
echo ===========================================
exit /b 0

:CopyAndVerify
REM %1=src %2=dstDir %3=dstName %4=flagVar
set "_SRC=%~1"
set "_DSTDIR=%~2"
set "_DSTNAME=%~3"
set "_FLAGVAR=%~4"
set "_DST=%_DSTDIR%\%_DSTNAME%"
echo Attempting copy: "%_SRC%" -> "%_DST%"
if not exist "%_SRC%" (
    echo WARNING: Source not found: %_SRC%
    goto :eof
)
if not exist "%_DSTDIR%" (
    echo Creating folder: %_DSTDIR%
    mkdir "%_DSTDIR%"
)
copy /Y "%_SRC%" "%_DST%" >nul
if exist "%_DST%" (
    echo SUCCESS: %_DSTNAME% copied to %_DSTDIR%
    set "%_FLAGVAR%=YES"
) else (
    echo FAIL: %_DSTNAME% NOT found in %_DSTDIR% after copy
)
goto :eof