CC ?= gcc
CFLAGS ?= -std=c11 -O2 -Wall -Wextra -Werror
LDLIBS = -lm
BUILD_DIR = build

ifeq ($(OS),Windows_NT)
	EXE = .exe
else
	EXE =
endif

LOADER = $(BUILD_DIR)/loader$(EXE)
FIXTURE_TEST = $(BUILD_DIR)/fixture_test$(EXE)
ERROR_TEST = $(BUILD_DIR)/error_test$(EXE)
# Every contract version the engine supports keeps its fixture sets here
# (fixtures/v<N>/<set>/model.gguf + cases.txt).
FIXTURE_DIRS = $(patsubst %/cases.txt,%,$(wildcard fixtures/v*/*/cases.txt))
# Error cases corrupt this model at known offsets (context 32, merge_base 298).
ERROR_FIXTURE = fixtures/v2/base/model.gguf

CORE_SRCS = src/core/engine/loader.c
CLI_SRCS = src/cli/main.c
INCLUDES = -Isrc/core/include

.PHONY: all engine postprocessor test test-fixtures test-errors test-postprocessor clean

all: engine postprocessor

engine: $(LOADER)

$(LOADER): $(CLI_SRCS) $(CORE_SRCS) | $(BUILD_DIR)
	$(CC) $(CFLAGS) $(INCLUDES) $(CLI_SRCS) $(CORE_SRCS) $(LDLIBS) -o $@

# The test includes loader.c directly, so it needs the core source path.
$(FIXTURE_TEST): tests/fixture_test.c $(CORE_SRCS) | $(BUILD_DIR)
	$(CC) $(CFLAGS) $(INCLUDES) -Wno-unused-function $< $(LDLIBS) -o $@

$(ERROR_TEST): tests/error_test.c | $(BUILD_DIR)
	$(CC) $(CFLAGS) $< -o $@

$(BUILD_DIR):
	mkdir $(BUILD_DIR)

postprocessor:
	$(MAKE) -C src/core/postprocessor all

test: test-fixtures test-errors test-postprocessor

test-fixtures: $(FIXTURE_TEST)
	$(foreach dir,$(FIXTURE_DIRS),./$(FIXTURE_TEST) $(dir)/model.gguf $(dir)/cases.txt &&) true

test-errors: $(LOADER) $(ERROR_TEST)
	./$(ERROR_TEST) ./$(LOADER) $(ERROR_FIXTURE) $(BUILD_DIR)

test-postprocessor:
	$(MAKE) -C src/core/postprocessor test

clean:
	$(MAKE) -C src/core/postprocessor clean
	rm -rf $(BUILD_DIR)
