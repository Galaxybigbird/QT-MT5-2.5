@echo off
echo Building MT5 gRPC Wrapper DLL...

REM Check for Visual Studio Build Tools
where cl.exe >nul 2>nul
if %ERRORLEVEL% NEQ 0 (
    echo Visual Studio Build Tools not found in PATH
    echo Please run this from a Visual Studio Developer Command Prompt
    echo or install Visual Studio Build Tools
    pause
    exit /b 1
)

REM Compile the gRPC wrapper DLL with proper streaming support
echo Compiling GrpcWrapper.cpp...
cl.exe /std:c++17 /EHsc /MD /O2 /DWIN32 /D_WINDOWS /D_USRDLL /DUNICODE /D_UNICODE ^
    /favor:INTEL64 /LD GrpcWrapper.cpp /Fe:MT5GrpcWrapper.dll /link /DEF:MT5GrpcWrapper.def /MACHINE:X64 ws2_32.lib

if %ERRORLEVEL% EQU 0 (
    echo Build successful!
    echo Copying DLL to MT5 Libraries folder...
    copy MT5GrpcWrapper.dll "C:\Users\marth\AppData\Roaming\MetaQuotes\Terminal\7BC3F33EDFDBDBDBADB45838B9A2D03F\MQL5\Libraries\"
    echo Done! You can now test the EA in MT5.
) else (
    echo Build failed!
)

pause