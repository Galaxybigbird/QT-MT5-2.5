@echo off
echo Building MT5GrpcClient.dll...

REM Check if vcpkg is available
where vcpkg >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo Error: vcpkg not found in PATH. Please install vcpkg and add it to your PATH.
    echo Download from: https://github.com/Microsoft/vcpkg
    pause
    exit /b 1
)

REM Create build directory
if not exist build mkdir build
cd build

REM Configure CMake with vcpkg
echo Configuring CMake with vcpkg...
cmake .. -DCMAKE_TOOLCHAIN_FILE=%VCPKG_ROOT%/scripts/buildsystems/vcpkg.cmake -DVCPKG_TARGET_TRIPLET=x64-windows -A x64

if %ERRORLEVEL% NEQ 0 (
    echo Error: CMake configuration failed
    pause
    exit /b 1
)

REM Build the project
echo Building project...
cmake --build . --config Release

if %ERRORLEVEL% NEQ 0 (
    echo Error: Build failed
    pause
    exit /b 1
)

echo Build completed successfully!
echo MT5GrpcClient.dll should be in the Release directory.

REM Copy DLL to parent directory for easy access
if exist Release\MT5GrpcClient.dll (
    copy Release\MT5GrpcClient.dll ..\MT5GrpcClient.dll
    echo DLL copied to parent directory.
)

pause