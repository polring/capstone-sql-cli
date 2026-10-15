/**
 * @file test_postprocessor.c
 * @brief Unit tests in C matching Python tests/test_postprocessor_python.py.
 */

#include "postprocessor.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static int g_tests_passed = 0;
static int g_tests_total = 0;

#define ASSERT_TRUE(expr, msg)                                                         \
    do {                                                                               \
        g_tests_total++;                                                               \
        if (expr) {                                                                    \
            g_tests_passed++;                                                          \
            printf("  [PASS] %s\n", msg);                                              \
        } else {                                                                       \
            printf("  [FAIL] %s (Line %d: %s)\n", msg, __LINE__, #expr);               \
        }                                                                              \
    } while (0)

static const char *TEST_DICTIONARY[] = {"users", "user_name",  "email",     "id",
                                        "name",  "department", "manager_id"};
static const size_t TEST_DICT_SIZE =
    sizeof(TEST_DICTIONARY) / sizeof(TEST_DICTIONARY[0]);

void test_pre_correct_keywords(void) {
    printf("Running test_pre_correct_keywords...\n");
    const char *sql = "SELETC id, name FRMO users";
    char *fixed = pre_correct_keywords(sql);
    ASSERT_TRUE(fixed != NULL, "pre_correct_keywords returns non-null");
    if (fixed) {
        ASSERT_TRUE(strcmp(fixed, "SELECT id, name FROM users") == 0,
                    "SELETC -> SELECT and FRMO -> FROM corrected");
        free(fixed);
    }
}

void test_typo_correction_keywords(void) {
    printf("Running test_typo_correction_keywords...\n");
    const char *sql = "SELETC id, name FRMO users";
    char *result = process_sql(sql, "typo", TEST_DICTIONARY, TEST_DICT_SIZE, 0, 0.0);
    ASSERT_TRUE(result != NULL, "process_sql returns non-null");
    if (result) {
        ASSERT_TRUE(strcmp(result, "SELECT id, name FROM users") == 0,
                    "Full query with keyword typos resolved accurately");
        free(result);
    }
}

void test_typo_correction_identifiers(void) {
    printf("Running test_typo_correction_identifiers...\n");
    const char *sql = "SELECT usr_name, emil FROM users";
    char *result = process_sql(sql, "typo", TEST_DICTIONARY, TEST_DICT_SIZE, 0, 0.0);
    ASSERT_TRUE(result != NULL, "process_sql returns non-null");
    if (result) {
        ASSERT_TRUE(strstr(result, "user_name") != NULL,
                    "usr_name corrected to user_name");
        ASSERT_TRUE(strstr(result, "email") != NULL, "emil corrected to email");
        free(result);
    }
}

void test_typo_correction_complex(void) {
    printf("Running test_typo_correction_complex...\n");
    const char *sql = "SELECT manger_id FROM departmnt WHERE id = 1";
    char *result = process_sql(sql, "typo", TEST_DICTIONARY, TEST_DICT_SIZE, 0, 0.0);
    ASSERT_TRUE(result != NULL, "process_sql returns non-null");
    if (result) {
        ASSERT_TRUE(strstr(result, "manager_id") != NULL,
                    "manger_id corrected to manager_id");
        ASSERT_TRUE(strstr(result, "department") != NULL,
                    "departmnt corrected to department");
        free(result);
    }
}

void test_ngram_correction(void) {
    printf("Running test_ngram_correction...\n");
    const char *sql = "SELECT id FROM departmnt";
    char *result = process_sql(sql, "ngram", TEST_DICTIONARY, TEST_DICT_SIZE, 0, 0.0);
    ASSERT_TRUE(result != NULL, "process_sql returns non-null");
    if (result) {
        ASSERT_TRUE(strstr(result, "department") != NULL,
                    "departmnt corrected to department via ngram");
        free(result);
    }
}

void test_strategy_chaining(void) {
    printf("Running test_strategy_chaining...\n");
    const char *sql = "SELECT usr_name FROM departmnt";
    char *result =
        process_sql(sql, "typo,ngram", TEST_DICTIONARY, TEST_DICT_SIZE, 0, 0.0);
    ASSERT_TRUE(result != NULL, "process_sql returns non-null");
    if (result) {
        ASSERT_TRUE(strstr(result, "user_name") != NULL,
                    "usr_name corrected in chained strategy");
        ASSERT_TRUE(strstr(result, "department") != NULL,
                    "departmnt corrected in chained strategy");
        free(result);
    }
}

void test_semantic_correction_pipeline(void) {
    printf("Running test_semantic_correction_pipeline...\n");
    const char *sql = "SELECT usr FROM users";
    char *result =
        process_sql(sql, "semantic", TEST_DICTIONARY, TEST_DICT_SIZE, 300, 0.0);
    ASSERT_TRUE(result != NULL, "process_sql returns non-null");
    if (result) {
        ASSERT_TRUE(strstr(result, "usr") != NULL ||
                        strstr(result, "user_name") != NULL ||
                        strstr(result, "users") != NULL,
                    "semantic fallback or match safely handles query");
        free(result);
    }
}

void test_cascading_strategy_none(void) {
    printf("Running test_cascading_strategy_none...\n");
    const char *sql = "SELETC usr_name FRMO users";
    char *result = process_sql(sql, "none", NULL, 0, 0, 0.0);
    ASSERT_TRUE(result != NULL, "process_sql with none returns non-null");
    if (result) {
        ASSERT_TRUE(strcmp(result, sql) == 0,
                    "none returns raw input exactly untouched");
        free(result);
    }
}

void test_cascading_strategy_keyword(void) {
    printf("Running test_cascading_strategy_keyword...\n");
    const char *sql = "SELETC usr_name FRMO users";
    char *result = process_sql(sql, "keyword", NULL, 0, 0, 0.0);
    ASSERT_TRUE(result != NULL, "process_sql with keyword returns non-null");
    if (result) {
        ASSERT_TRUE(strstr(result, "SELECT") != NULL,
                    "keyword corrects SELETC to SELECT");
        ASSERT_TRUE(strstr(result, "FROM") != NULL, "keyword corrects FRMO to FROM");
        ASSERT_TRUE(strstr(result, "usr_name") != NULL,
                    "keyword leaves usr_name unchanged");
        free(result);
    }
}

void test_cascading_sequence_enforcement(void) {
    printf("Running test_cascading_sequence_enforcement...\n");
    const char *sql = "SELECT usr_name FROM users";
    char *bad1 =
        process_sql(sql, "ngram,typo", TEST_DICTIONARY, TEST_DICT_SIZE, 0, 0.0);
    ASSERT_TRUE(bad1 && strncmp(bad1, "Error:", 6) == 0,
                "out-of-order strategy rejected");
    if (bad1) {
        free(bad1);
    }

    char *bad2 =
        process_sql(sql, "keyword,semantic", TEST_DICTIONARY, TEST_DICT_SIZE, 300, 0.0);
    ASSERT_TRUE(bad2 && strncmp(bad2, "Error:", 6) == 0, "skipping stages rejected");
    if (bad2) {
        free(bad2);
    }

    char *good =
        process_sql(sql, "keyword,typo,ngram", TEST_DICTIONARY, TEST_DICT_SIZE, 0, 0.0);
    ASSERT_TRUE(good && strncmp(good, "Error:", 6) != 0,
                "valid prefix cascade accepted");
    if (good) {
        free(good);
    }
}

int main(void) {
    printf("=========================================\n");
    printf("Running C SQL Post-processor Unit Tests\n");
    printf("=========================================\n\n");

    test_pre_correct_keywords();
    test_typo_correction_keywords();
    test_typo_correction_identifiers();
    test_typo_correction_complex();
    test_ngram_correction();
    test_strategy_chaining();
    test_semantic_correction_pipeline();
    test_cascading_strategy_none();
    test_cascading_strategy_keyword();
    test_cascading_sequence_enforcement();

    printf("\n=========================================\n");
    printf("Summary: %d / %d assertions passed.\n", g_tests_passed, g_tests_total);
    printf("=========================================\n");

    return (g_tests_passed == g_tests_total) ? 0 : 1;
}
