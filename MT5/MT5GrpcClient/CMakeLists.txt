cmake_minimum_required(VERSION 3.20)
project(MT5GrpcClient)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Set Windows specific options
if(WIN32)
    set(CMAKE_WINDOWS_EXPORT_ALL_SYMBOLS ON)
    add_definitions(-DWIN32_LEAN_AND_MEAN)
    add_definitions(-DNOMINMAX)
endif()

# Find required packages
find_package(Protobuf CONFIG REQUIRED)
find_package(gRPC CONFIG REQUIRED)
find_package(jsoncpp CONFIG REQUIRED)

# Proto files
set(PROTO_FILES
    proto/trading.proto
)

# Generate protobuf and gRPC C++ files
foreach(PROTO_FILE ${PROTO_FILES})
    get_filename_component(PROTO_NAME ${PROTO_FILE} NAME_WE)
    get_filename_component(PROTO_PATH ${PROTO_FILE} PATH)
    
    set(PROTO_SRCS "${CMAKE_CURRENT_BINARY_DIR}/${PROTO_NAME}.pb.cc")
    set(PROTO_HDRS "${CMAKE_CURRENT_BINARY_DIR}/${PROTO_NAME}.pb.h")
    set(GRPC_SRCS "${CMAKE_CURRENT_BINARY_DIR}/${PROTO_NAME}.grpc.pb.cc")
    set(GRPC_HDRS "${CMAKE_CURRENT_BINARY_DIR}/${PROTO_NAME}.grpc.pb.h")
    
    add_custom_command(
        OUTPUT ${PROTO_SRCS} ${PROTO_HDRS} ${GRPC_SRCS} ${GRPC_HDRS}
        COMMAND protobuf::protoc
        ARGS --grpc_out=${CMAKE_CURRENT_BINARY_DIR}
             --cpp_out=${CMAKE_CURRENT_BINARY_DIR}
             --plugin=protoc-gen-grpc=$<TARGET_FILE:gRPC::grpc_cpp_plugin>
             --proto_path=${CMAKE_CURRENT_SOURCE_DIR}/${PROTO_PATH}
             ${CMAKE_CURRENT_SOURCE_DIR}/${PROTO_FILE}
        DEPENDS ${CMAKE_CURRENT_SOURCE_DIR}/${PROTO_FILE}
        COMMENT "Generating protobuf and gRPC C++ files for ${PROTO_FILE}"
    )
    
    list(APPEND ALL_PROTO_SRCS ${PROTO_SRCS} ${GRPC_SRCS})
    list(APPEND ALL_PROTO_HDRS ${PROTO_HDRS} ${GRPC_HDRS})
endforeach()

# Include directories
include_directories(
    ${CMAKE_CURRENT_SOURCE_DIR}/include
    ${CMAKE_CURRENT_BINARY_DIR}
)

# Source files
set(SOURCES
    src/mt5_grpc_client.cpp
    src/grpc_client_impl.cpp
    src/json_converter.cpp
    ${ALL_PROTO_SRCS}
)

# Headers
set(HEADERS
    include/mt5_grpc_client.h
    include/grpc_client_impl.h
    include/json_converter.h
    ${ALL_PROTO_HDRS}
)

# Create shared library (DLL on Windows)
add_library(MT5GrpcClient SHARED ${SOURCES} ${HEADERS})

# Link libraries
target_link_libraries(MT5GrpcClient
    protobuf::libprotobuf
    gRPC::grpc++
    gRPC::grpc++_reflection
    JsonCpp::JsonCpp
)

# Windows specific linking
if(WIN32)
    target_link_libraries(MT5GrpcClient ws2_32 wsock32)
endif()

# Set output directory
set_target_properties(MT5GrpcClient PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR}/../
    LIBRARY_OUTPUT_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR}/../
    ARCHIVE_OUTPUT_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR}/../
)

# Copy DLL to MT5 Experts folder after build (optional)
if(WIN32)
    add_custom_command(TARGET MT5GrpcClient POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E echo "Built MT5GrpcClient.dll successfully"
    )
endif()