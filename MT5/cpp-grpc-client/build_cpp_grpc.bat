@echo off
setlocal enabledelayedexpansion
echo Building C++ gRPC client for MT5...
echo.

REM Check if we're in the correct directory
if not exist "MT5GrpcClient.h" (
    echo ERROR: MT5GrpcClient.h not found. Please run this script from the cpp-grpc-client directory.
    pause
    exit /b 1
)

REM Check if vcpkg is available
where vcpkg >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: vcpkg not found in PATH.
    echo Please install vcpkg and run 'vcpkg integrate install' first.
    echo Then run vcpkg_install.bat to install dependencies.
    pause
    exit /b 1
)

REM Check if CMake is available
where cmake >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: CMake not found in PATH.
    echo Please install CMake and add it to your PATH.
    pause
    exit /b 1
)

REM Create build directory
if not exist "build" mkdir build
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
        pause
        exit /b 1
    )
)

if %ERRORLEVEL% NEQ 0 (
    echo ERROR: CMake configuration failed.
    echo Make sure you have run 'vcpkg integrate install' and installed the required packages.
    cd ..
    pause
    exit /b 1
)

echo Building MT5GrpcClient.dll...
cmake --build . --config Release

if %ERRORLEVEL% NEQ 0 (
    echo ERROR: Build failed.
    cd ..
    pause
    exit /b 1
)

echo.
echo ✅ Build completed successfully!
echo.
echo The MT5GrpcClient.dll has been built and should be automatically copied to:
echo C:\Users\marth\AppData\Roaming\MetaQuotes\Terminal\7BC3F33EDFDBDBDBADB45838B9A2D03F\MQL5\Libraries\
echo.

REM List the built files
echo Built files:
dir /b Release\*.dll 2>nul
if %ERRORLEVEL% NEQ 0 (
    dir /b lib\Release\*.dll 2>nul
)

echo.
echo You can now:
echo 1. Test the DLL with your MT5 EA
echo 2. Run the Bridge Server (wails dev in BridgeApp directory)
echo 3. Start NinjaTrader with the MultiStratManager addon
echo 4. Test the complete trade flow
echo.

cd ..
pause