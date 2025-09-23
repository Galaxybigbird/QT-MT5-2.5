@echo off
setlocal EnableExtensions EnableDelayedExpansion
rem Ensure we run from the script directory
cd /d "%~dp0"
echo Building MT5 Full gRPC Wrapper DLL...

REM Check for Visual Studio Build Tools, try to auto-load VsDevCmd via vswhere
where cl.exe >nul 2>nul
if %ERRORLEVEL% NEQ 0 (
    set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
    if exist "%VSWHERE%" (
        for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSINSTALL=%%i"
        if defined VSINSTALL (
            call "%VSINSTALL%\Common7\Tools\VsDevCmd.bat" -arch=x64 -host_arch=x64
        )
    )
)
where cl.exe >nul 2>nul
if %ERRORLEVEL% NEQ 0 (
    echo Visual Studio Build Tools not found in PATH
    if exist "MT5GrpcWrapper.dll" (
        echo Build tools missing, but existing DLL found. Proceeding with copy-only...
        set BUILD_OK=1
        goto :_CopyPhase
    )
    echo Please run this from a Visual Studio Developer Command Prompt
    echo or install Visual Studio Build Tools
    pause
    exit /b 1
)

REM Compile the full gRPC wrapper DLL
echo Compiling MT5GrpcWrapper.cpp...
cl.exe /std:c++17 /EHsc /MD /O2 /DWIN32 /D_WINDOWS /D_USRDLL /DUNICODE /D_UNICODE ^
    /favor:INTEL64 /LD MT5GrpcWrapper.cpp /Fe:MT5GrpcWrapper.dll /link /DEF:MT5GrpcWrapper.def /MACHINE:X64 ^
    ws2_32.lib

set BUILD_OK=0
if exist "MT5GrpcWrapper.dll" set BUILD_OK=1
rem Handle compiler return status without parentheses to avoid delayed expansion pitfalls
if errorlevel 1 if "%BUILD_OK%"=="1" goto :_WarnProceed
if errorlevel 1 goto :_BuildFailed

if "%BUILD_OK%"=="1" goto :_CopyPhase
echo Build failed (DLL not found).
goto :_End

goto :_End

:_BuildFailed
echo Build failed!

goto :_End

:_WarnProceed
echo Compiler returned non-zero (errorlevel=%ERRORLEVEL%) but DLL was produced. Proceeding with copy...
goto :_CopyPhase

:_End
pause
endlocal

goto :eof

:_CopyPhase
rem Resolve MT5 Libraries target folder
if not defined MT5LIB (
    set "DEFROOT=%APPDATA%\MetaQuotes\Terminal"
    if exist "%DEFROOT%\7BC3F33EDFDBDBDBADB45838B9A2D03F\MQL5\Libraries" set "MT5LIB=%DEFROOT%\7BC3F33EDFDBDBDBADB45838B9A2D03F\MQL5\Libraries"
    if not defined MT5LIB (
        for /f "delims=" %%D in ('dir /ad /b "%DEFROOT%\*" 2^>nul') do (
            if exist "%DEFROOT%\%%D\MQL5\Libraries" (
                set "MT5LIB=%DEFROOT%\%%D\MQL5\Libraries"
                goto :_LibFound
            )
        )
    )
)
:_LibFound
if not defined MT5LIB (
    echo Could not auto-detect your MT5 Libraries folder.
    echo Please paste the full path to your MQL5\Libraries folder (e.g., C:\Users\<you>\AppData\Roaming\MetaQuotes\Terminal\<hash>\MQL5\Libraries)
    set /p MT5LIB=MT5 Libraries path: 
)
if not defined MT5LIB (
    echo No path entered. Aborting.
    goto :_End
)
if not exist "%MT5LIB%" (
    echo Entered path does not exist: "%MT5LIB%"
    echo Please verify the path and rerun.
    goto :_End
)

echo Copying wrapper DLL to MT5 Libraries folder: "%MT5LIB%"
copy /Y "MT5GrpcWrapper.dll" "%MT5LIB%" >nul && echo Copied MT5GrpcWrapper.dll || echo Failed to copy MT5GrpcWrapper.dll

echo Copying managed DLLs to MT5 Libraries folder...
call :_CopyOne "MT5GrpcManaged.dll"
call :_CopyOne "Google.Protobuf.dll"
call :_CopyOne "Grpc.Core.Api.dll"
call :_CopyOne "Grpc.Net.Client.dll"
call :_CopyOne "Grpc.Net.Common.dll"
call :_CopyOne "System.Text.Json.dll"

echo Done. You can now test the full gRPC EA in MT5.
echo.
echo Notes:
echo - Exported GrpcLog is available from MT5GrpcWrapper.dll
echo - Managed assembly loaded: MT5GrpcManaged.dll
echo.
goto :_End

:_CopyOne
rem Copies a DLL from bin\Release\net48 or repo root to MT5LIB
setlocal
set "FN=%~1"
set "SRC1=bin\Release\net48\%FN%"
set "SRC2=%FN%"
if exist "%SRC1%" (
    copy /Y "%SRC1%" "%MT5LIB%" >nul && echo Copied %FN% ^(from bin\Release\net48^) & endlocal & goto :eof
)
if exist "%SRC2%" (
    copy /Y "%SRC2%" "%MT5LIB%" >nul && echo Copied %FN% ^(from repo root^) & endlocal & goto :eof
)
echo Skipped %FN% ^(not found^)
endlocal
goto :eof