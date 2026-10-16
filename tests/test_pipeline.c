/**
 * @file test_pipeline.c
 * @brief Unit tests for SqlPipeline C API and sql_engine shared library.
 *
 * Verifies:
 * 1. Graceful return on null/invalid/missing model file (NO exit(1), NO crash).
 * 2. Proper error message propagation via sql_get_last_error().
 * 3. Successful pipeline lifecycle (create -> run -> free_string -> destroy).
 */

#include "../src/core/include/pipeline.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>

static void test_invalid_model_path_graceful_return(void) {
    printf("[TEST] Testing graceful failure with non-existent model path...\n");
    SqlPipeline *pipe = sql_pipeline_create("non_existent_dummy_model.gguf", NULL);
    if (pipe != NULL) {
        fprintf(stderr,
                "FAIL: expected sql_pipeline_create to return NULL for invalid path\n");
        exit(1);
    }
    const char *err = sql_get_last_error();
    if (!err || strlen(err) == 0) {
        fprintf(stderr,
                "FAIL: expected non-empty error message from sql_get_last_error()\n");
        exit(1);
    }
    printf("  -> OK: Returned NULL gracefully. Error message: '%s'\n", err);
}

static void test_null_model_path_graceful_return(void) {
    printf("[TEST] Testing graceful failure with NULL model path...\n");
    SqlPipeline *pipe = sql_pipeline_create(NULL, NULL);
    if (pipe != NULL) {
        fprintf(stderr,
                "FAIL: expected sql_pipeline_create to return NULL for NULL path\n");
        exit(1);
    }
    const char *err = sql_get_last_error();
    if (!err || strlen(err) == 0) {
        fprintf(stderr,
                "FAIL: expected non-empty error message from sql_get_last_error()\n");
        exit(1);
    }
    printf("  -> OK: Returned NULL gracefully. Error message: '%s'\n", err);
}

static void test_valid_model_pipeline(const char *valid_model_path) {
    printf("[TEST] Testing valid pipeline with model: %s ...\n", valid_model_path);
    SqlPipeline *pipe = sql_pipeline_create(valid_model_path, NULL);
    if (!pipe) {
        fprintf(stderr,
                "FAIL: expected sql_pipeline_create to succeed for %s, error: %s\n",
                valid_model_path, sql_get_last_error());
        exit(1);
    }

    sql_pipeline_set_strategy(pipe, "typo,ngram", 0, 0.0);

    char *raw_sql = NULL;
    const char *stop_reason = NULL;
    char *sql = sql_pipeline_run(pipe, "show all items", 16, &raw_sql, &stop_reason);
    if (!sql) {
        fprintf(stderr, "FAIL: sql_pipeline_run returned NULL, error: %s\n",
                sql_get_last_error());
        sql_pipeline_destroy(pipe);
        exit(1);
    }

    printf("  -> OK: Generated SQL: '%s' (raw: '%s', stop: '%s')\n", sql,
           raw_sql ? raw_sql : "(none)", stop_reason ? stop_reason : "(none)");

    sql_free_string(sql);
    if (raw_sql) {
        sql_free_string(raw_sql);
    }

    sql_pipeline_destroy(pipe);
    printf("  -> OK: Destroyed pipeline successfully.\n");
}

static void test_sql_postprocess_standalone(void) {
    printf("[TEST] Testing standalone sql_postprocess C API...\n");
    char *res = sql_postprocess("SELETC id FRMO users", "keyword", NULL, 0, 0.0);
    if (!res) {
        fprintf(stderr, "FAIL: sql_postprocess returned NULL\n");
        exit(1);
    }
    if (strcmp(res, "SELECT id FROM users") != 0) {
        fprintf(stderr, "FAIL: expected 'SELECT id FROM users', got '%s'\n", res);
        sql_free_string(res);
        exit(1);
    }
    printf("  -> OK: sql_postprocess correctly corrected: '%s'\n", res);
    sql_free_string(res);
}

static void test_non_ascii_bailout(const char *valid_model_path) {
    printf("[TEST] Testing non-ASCII prompt graceful bailout (no exit/crash)...\n");
    SqlPipeline *pipe = sql_pipeline_create(valid_model_path, NULL);
    if (!pipe) {
        fprintf(stderr, "FAIL: failed to create pipeline for non-ASCII test\n");
        exit(1);
    }

    char *raw_sql = NULL;
    const char *stop_reason = NULL;
    /* Korean characters: should trigger fail("ASCII English input required") and
     * gracefully return NULL via longjmp */
    char *sql = sql_pipeline_run(pipe, "한국어 질문", 16, &raw_sql, &stop_reason);
    if (sql != NULL) {
        fprintf(stderr,
                "FAIL: expected sql_pipeline_run to return NULL on non-ASCII input\n");
        sql_free_string(sql);
        sql_pipeline_destroy(pipe);
        exit(1);
    }

    const char *err = sql_get_last_error();
    if (!err || strstr(err, "ASCII") == NULL) {
        fprintf(stderr, "FAIL: expected error message mentioning ASCII, got: '%s'\n",
                err ? err : "(null)");
        sql_pipeline_destroy(pipe);
        exit(1);
    }

    printf("  -> OK: Gracefully returned NULL without process exit. Error: '%s'\n",
           err);
    sql_pipeline_destroy(pipe);
}

int main(int argc, char **argv) {
    printf("=== SqlPipeline Unit & Safety Tests ===\n");

    test_null_model_path_graceful_return();
    test_invalid_model_path_graceful_return();
    test_sql_postprocess_standalone();

    if (argc >= 2) {
        test_valid_model_pipeline(argv[1]);
        test_non_ascii_bailout(argv[1]);
    } else {
        printf("[INFO] No valid model path provided as argv[1], skipping valid model "
               "run.\n");
    }

    printf("=== All SqlPipeline Tests Passed! ===\n");
    return 0;
}
