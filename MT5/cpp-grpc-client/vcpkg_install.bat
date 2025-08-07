@echo off
echo Installing C++ gRPC dependencies via vcpkg...
echo.

REM Check if vcpkg is available
where vcpkg >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: vcpkg not found in PATH.
    echo Please install vcpkg and run 'vcpkg integrate install' first.
    echo See: https://github.com/Microsoft/vcpkg
    pause
    exit /b 1
)

REM Check if vcpkg.json exists
if not exist "vcpkg.json" (
    echo ERROR: vcpkg.json manifest file not found.
    echo This file should be in the same directory as this script.
    pause
    exit /b 1
)

echo Installing dependencies from vcpkg.json manifest...
vcpkg install --triplet=x64-windows

echo.
if %ERRORLEVEL% EQU 0 (
    echo ✅ Dependencies installed successfully!
    echo You can now run build_cpp_grpc.bat to build the MT5 gRPC client.
) else (
    echo ❌ Installation failed. Please check the error messages above.
    echo Make sure you have run 'vcpkg integrate install' first.
)
pause