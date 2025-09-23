@echo off
setlocal EnableDelayedExpansion

rem Modern MT5 wrapper builds piggyback on the dedicated C++ gRPC client project.
rem Delegate to cpp-grpc-client\build_cpp_grpc.bat so dependencies (gRPC, protobuf,
rem nlohmann_json) are resolved via vcpkg/CMake.

pushd "%~dp0" >nul
if not exist "cpp-grpc-client\build_cpp_grpc.bat" (
    echo ERROR: cpp-grpc-client\build_cpp_grpc.bat not found. Please pull submodules or check the repo layout.
    popd >nul
    exit /b 1
)

pushd cpp-grpc-client >nul
call build_cpp_grpc.bat %*
popd >nul
popd >nul

endlocal
