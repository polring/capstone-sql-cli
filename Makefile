CC ?= gcc
CFLAGS ?= -std=c11 -O2 -Wall -Wextra -Werror
LDLIBS = -lm
BUILD_DIR = build

ifeq ($(OS),Windows_NT)
	EXE = .exe
else
	EXE =
endif

SQL_CLI = $(BUILD_DIR)/sql-cli$(EXE)
FIXTURE_TEST = $(BUILD_DIR)/fixture_test$(EXE)
ERROR_TEST = $(BUILD_DIR)/error_test$(EXE)
# Every contract version the engine supports keeps its fixture sets here
# (fixtures/v<N>/<set>/model.gguf + cases.txt).
FIXTURE_DIRS = $(patsubst %/cases.txt,%,$(wildcard fixtures/v*/*/cases.txt))
# Error cases corrupt this model at known offsets (context 32, merge_base 298).
ERROR_FIXTURE = fixtures/v2/base/model.gguf

CORE_SRCS = src/core/engine/loader.c src/core/pipeline/pipeline.c
CLI_SRCS = src/cli/main.c
POSTPROCESSOR_SRCS = src/core/postprocessor/core.c src/core/postprocessor/parser.c src/core/postprocessor/replacement.c src/core/postprocessor/schema.c
INCLUDES = -Isrc/core/include -Isrc/core/postprocessor

# Detect if sqlite3 is available
SQLITE_AVAILABLE := $(shell $(CC) -x c -E -include sqlite3.h /dev/null -o /dev/null 2>/dev/null && echo 1 || echo 0)
ifeq ($(SQLITE_AVAILABLE), 1)
	CFLAGS += -DHAVE_SQLITE3
	LDLIBS += -lsqlite3
endif

.PHONY: all engine postprocessor test test-fixtures test-errors test-postprocessor clean

all: engine postprocessor

engine: $(SQL_CLI)

$(SQL_CLI): $(CLI_SRCS) $(CORE_SRCS) $(POSTPROCESSOR_SRCS) | $(BUILD_DIR)
	$(CC) $(CFLAGS) $(INCLUDES) $^ $(LDLIBS) -o $@

# The test includes loader.c directly, so it needs the core source path.
$(FIXTURE_TEST): tests/fixture_test.c src/core/engine/loader.c | $(BUILD_DIR)
	$(CC) $(CFLAGS) $(INCLUDES) -Wno-unused-function $< $(LDLIBS) -o $@

$(ERROR_TEST): tests/error_test.c | $(BUILD_DIR)
	$(CC) $(CFLAGS) $< -o $@

$(BUILD_DIR):
	mkdir -p $(BUILD_DIR)

postprocessor:
	$(MAKE) -C src/core/postprocessor all

test: test-fixtures test-errors test-postprocessor

test-fixtures: $(FIXTURE_TEST)
	$(foreach dir,$(FIXTURE_DIRS),./$(FIXTURE_TEST) $(dir)/model.gguf $(dir)/cases.txt &&) true

test-errors: $(SQL_CLI) $(ERROR_TEST)
	./$(ERROR_TEST) ./$(SQL_CLI) $(ERROR_FIXTURE) $(BUILD_DIR)

test-postprocessor:
	$(MAKE) -C src/core/postprocessor test

clean:
	$(MAKE) -C src/core/postprocessor clean
	rm -rf $(BUILD_DIR)
