# Native engine and tests. The GUI is built with dotnet.
BUILD_DIR ?= build
CONFIG ?= Release

.PHONY: all configure engine test clean

all: configure
	cmake --build "$(BUILD_DIR)" --config "$(CONFIG)"

configure:
	cmake -S native -B "$(BUILD_DIR)" -DCMAKE_BUILD_TYPE="$(CONFIG)"

engine: configure
	cmake --build "$(BUILD_DIR)" --config "$(CONFIG)" --target sql_engine

test: all
	cmake -E chdir "$(BUILD_DIR)" ctest -C "$(CONFIG)" --output-on-failure

clean:
	@if [ -f "$(BUILD_DIR)/CMakeCache.txt" ]; then cmake --build "$(BUILD_DIR)" --config "$(CONFIG)" --target clean; fi
