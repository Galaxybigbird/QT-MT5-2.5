# Makefile for MT5 gRPC Wrapper DLL
# Requires Visual Studio Build Tools or MinGW-w64

# Compiler and flags
CXX = cl.exe
CXXFLAGS = /std:c++17 /EHsc /MD /O2 /DWIN32 /D_WINDOWS /D_USRDLL /DUNICODE /D_UNICODE
LDFLAGS = /DLL /MACHINE:X64
LIBS = mscoree.lib ole32.lib oleaut32.lib

# Source and target files
TARGET = MT5GrpcWrapper.dll
SOURCE = MT5GrpcWrapper.cpp
OBJECT = MT5GrpcWrapper.obj

# Default target
all: $(TARGET)

# Build the DLL
$(TARGET): $(OBJECT)
	link $(LDFLAGS) $(OBJECT) $(LIBS) /OUT:$(TARGET)

# Compile source to object
$(OBJECT): $(SOURCE)
	$(CXX) $(CXXFLAGS) /c $(SOURCE) /Fo$(OBJECT)

# Clean up generated files
clean:
	if exist $(OBJECT) del $(OBJECT)
	if exist $(TARGET) del $(TARGET)
	if exist *.exp del *.exp
	if exist *.lib del *.lib
	if exist *.pdb del *.pdb
	if exist *.ilk del *.ilk

# Alternative using MinGW-w64 (if Visual Studio not available)
mingw: 
	x86_64-w64-mingw32-g++ -shared -std=c++17 -O2 -DWIN32 -D_WINDOWS -DUNICODE -D_UNICODE \
		-o $(TARGET) $(SOURCE) -lmscoree -lole32 -loleaut32 -static-libgcc -static-libstdc++

# Install DLL to MT5 Libraries folder
install: $(TARGET)
	copy $(TARGET) "C:\Users\marth\AppData\Roaming\MetaQuotes\Terminal\7BC3F33EDFDBDBDBADB45838B9A2D03F\MQL5\Libraries\"

.PHONY: all clean mingw install