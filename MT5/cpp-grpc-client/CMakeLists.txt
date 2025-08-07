cmake_minimum_required(VERSION 3.15)
project(MT5GrpcClient)

# Set C++ standard
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Find required packages
find_package(Protobuf CONFIG REQUIRED)
find_package(gRPC CONFIG REQUIRED)
find_package(nlohmann_json CONFIG REQUIRED)

# Set output directory for the DLL
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/bin)
set(CMAKE_LIBRARY_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/lib)

# Generate protobuf and gRPC files
set(PROTO_FILES
    proto/trading.proto
)

# Add custom command to generate protobuf files
add_custom_command(
    OUTPUT 
        ${CMAKE_CURRENT_BINARY_DIR}/proto/trading.pb.cc
        ${CMAKE_CURRENT_BINARY_DIR}/proto/trading.pb.h
        ${CMAKE_CURRENT_BINARY_DIR}/proto/trading.grpc.pb.cc
        ${CMAKE_CURRENT_BINARY_DIR}/proto/trading.grpc.pb.h
    COMMAND ${CMAKE_COMMAND} -E make_directory ${CMAKE_CURRENT_BINARY_DIR}/proto
    COMMAND protobuf::protoc
        --proto_path=${CMAKE_CURRENT_SOURCE_DIR}/proto
        --cpp_out=${CMAKE_CURRENT_BINARY_DIR}/proto
        --grpc_out=${CMAKE_CURRENT_BINARY_DIR}/proto
        --plugin=protoc-gen-grpc=$<TARGET_FILE:gRPC::grpc_cpp_plugin>
        ${CMAKE_CURRENT_SOURCE_DIR}/proto/trading.proto
    DEPENDS ${PROTO_FILES}
    COMMENT "Generating protobuf files"
)

# Create library for generated protobuf files
add_library(trading_proto
    ${CMAKE_CURRENT_BINARY_DIR}/proto/trading.pb.cc
    ${CMAKE_CURRENT_BINARY_DIR}/proto/trading.grpc.pb.cc
)

target_link_libraries(trading_proto
    PUBLIC
        protobuf::libprotobuf
        gRPC::grpc++
        gRPC::grpc++_reflection
)

target_include_directories(trading_proto PUBLIC ${CMAKE_CURRENT_BINARY_DIR})

# Main MT5 gRPC client DLL
add_library(MT5GrpcClient SHARED
    MT5GrpcClient.cpp
    JsonConverter.cpp
    MT5GrpcClient.h
    JsonConverter.h
)

# Set DLL exports
target_compile_definitions(MT5GrpcClient PRIVATE MT5_GRPC_EXPORTS)

# Link libraries
target_link_libraries(MT5GrpcClient
    PRIVATE
        trading_proto
        protobuf::libprotobuf
        gRPC::grpc++
        gRPC::grpc++_reflection
        nlohmann_json::nlohmann_json
)

# Include directories
target_include_directories(MT5GrpcClient
    PRIVATE
        ${CMAKE_CURRENT_SOURCE_DIR}
        ${CMAKE_CURRENT_BINARY_DIR}
)

# Set output name
set_target_properties(MT5GrpcClient PROPERTIES
    OUTPUT_NAME "MT5GrpcClient"
    PREFIX ""
)

# Compiler-specific options
if(MSVC)
    target_compile_options(MT5GrpcClient PRIVATE /W3 /MD)
    target_compile_options(trading_proto PRIVATE /MD)
    target_compile_definitions(MT5GrpcClient PRIVATE _WIN32_WINNT=0x0A00)
    
    # Force release mode to use dynamic runtime
    set(CMAKE_CXX_FLAGS_RELEASE "${CMAKE_CXX_FLAGS_RELEASE} /MD")
    set(CMAKE_CXX_FLAGS_DEBUG "${CMAKE_CXX_FLAGS_DEBUG} /MDd")
endif()

# Copy DLL to MT5 Libraries folder after build
if(WIN32)
    add_custom_command(TARGET MT5GrpcClient POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E echo "Copying MT5GrpcClient.dll to MT5 Libraries folder..."
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            $<TARGET_FILE:MT5GrpcClient>
            "C:/Users/marth/AppData/Roaming/MetaQuotes/Terminal/7BC3F33EDFDBDBDBADB45838B9A2D03F/MQL5/Libraries/MT5GrpcClient.dll"
        COMMENT "Copying DLL to MT5 Libraries folder"
    )
endif()

# Install rules
install(TARGETS MT5GrpcClient
    RUNTIME DESTINATION bin
    LIBRARY DESTINATION lib
    ARCHIVE DESTINATION lib
)

install(FILES MT5GrpcClient.h
    DESTINATION include
)