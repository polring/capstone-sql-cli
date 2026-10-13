# Wrapper Makefile delegating to CMake as the single source of truth
BUILD_DIR ?= build
CONFIG ?= Release

.PHONY: all engine test clean

all:
	cmake -B $(BUILD_DIR) -DCMAKE_BUILD_TYPE=$(CONFIG)
	cmake --build $(BUILD_DIR) --config $(CONFIG)

engine: all

test: all
	ctest --test-dir $(BUILD_DIR) -C $(CONFIG) --output-on-failure

clean:
	cmake --build $(BUILD_DIR) --target clean 2>/dev/null || true
	rm -rf $(BUILD_DIR)
