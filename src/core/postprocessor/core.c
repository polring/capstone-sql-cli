/**
 * @file core.c
 * @brief Implementation of process_sql matching Python core.py.
 */

#include "core.h"
#include "parser.h"
#include "replacement.h"
#include <ctype.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static char *make_error_message(const char *msg) {
    size_t len = strlen(msg);
    char *res = (char *)malloc(len + 1);
    if (res) {
        memcpy(res, msg, len + 1);
    }
    return res;
}

int parse_postprocess_level(const char *strategy, PostprocessLevel *out_level) {
    if (!strategy || !out_level) {
        return -1;
    }

    char buf[256];
    strncpy(buf, strategy, sizeof(buf) - 1);
    buf[sizeof(buf) - 1] = '\0';

    /* Trim leading spaces */
    char *p = buf;
    while (*p && isspace((unsigned char)*p)) {
        p++;
    }
    /* Trim trailing spaces */
    char *end = p + strlen(p) - 1;
    while (end > p && isspace((unsigned char)*end)) {
        *end = '\0';
        end--;
    }

    if (!*p) {
        return -1;
    }

    /* Single token direct matches */
    if (strcmp(p, "none") == 0 || strcmp(p, "0") == 0) {
        *out_level = POSTPROCESS_LEVEL_NONE;
        return 0;
    }
    if (strcmp(p, "keyword") == 0 || strcmp(p, "key") == 0 || strcmp(p, "1") == 0) {
        *out_level = POSTPROCESS_LEVEL_KEYWORD;
        return 0;
    }
    if (strcmp(p, "typo") == 0 || strcmp(p, "2") == 0) {
        *out_level = POSTPROCESS_LEVEL_TYPO;
        return 0;
    }
    if (strcmp(p, "ngram") == 0 || strcmp(p, "3") == 0) {
        *out_level = POSTPROCESS_LEVEL_NGRAM;
        return 0;
    }
    if (strcmp(p, "semantic") == 0 || strcmp(p, "4") == 0) {
        *out_level = POSTPROCESS_LEVEL_SEMANTIC;
        return 0;
    }

    /* Comma-separated token verification.
     * Must strictly follow prefix sequence of: keyword -> typo -> ngram -> semantic
     * or typo -> ngram -> semantic.
     */
    char *tokens[8];
    int token_count = 0;
    char *tok = strtok(p, ",");
    while (tok && token_count < 8) {
        while (*tok && isspace((unsigned char)*tok)) {
            tok++;
        }
        char *t_end = tok + strlen(tok) - 1;
        while (t_end > tok && isspace((unsigned char)*t_end)) {
            *t_end = '\0';
            t_end--;
        }
        if (*tok) {
            tokens[token_count++] = tok;
        }
        tok = strtok(NULL, ",");
    }

    if (token_count == 0) {
        return -1;
    }

    /* Check sequence starting with keyword */
    if (strcmp(tokens[0], "keyword") == 0 || strcmp(tokens[0], "key") == 0) {
        if (token_count == 1) {
            *out_level = POSTPROCESS_LEVEL_KEYWORD;
            return 0;
        }
        if (token_count >= 2 && strcmp(tokens[1], "typo") == 0) {
            if (token_count == 2) {
                *out_level = POSTPROCESS_LEVEL_TYPO;
                return 0;
            }
            if (token_count >= 3 && strcmp(tokens[2], "ngram") == 0) {
                if (token_count == 3) {
                    *out_level = POSTPROCESS_LEVEL_NGRAM;
                    return 0;
                }
                if (token_count == 4 && strcmp(tokens[3], "semantic") == 0) {
                    *out_level = POSTPROCESS_LEVEL_SEMANTIC;
                    return 0;
                }
            }
        }
        return -1;
    }

    /* Check sequence starting with typo (legacy compatibility) */
    if (strcmp(tokens[0], "typo") == 0) {
        if (token_count == 1) {
            *out_level = POSTPROCESS_LEVEL_TYPO;
            return 0;
        }
        if (token_count >= 2 && strcmp(tokens[1], "ngram") == 0) {
            if (token_count == 2) {
                *out_level = POSTPROCESS_LEVEL_NGRAM;
                return 0;
            }
            if (token_count == 3 && strcmp(tokens[2], "semantic") == 0) {
                *out_level = POSTPROCESS_LEVEL_SEMANTIC;
                return 0;
            }
        }
        return -1;
    }

    return -1;
}

const char *postprocess_level_to_string(PostprocessLevel level) {
    switch (level) {
    case POSTPROCESS_LEVEL_NONE:
        return "none";
    case POSTPROCESS_LEVEL_KEYWORD:
        return "keyword";
    case POSTPROCESS_LEVEL_TYPO:
        return "typo";
    case POSTPROCESS_LEVEL_NGRAM:
        return "ngram";
    case POSTPROCESS_LEVEL_SEMANTIC:
        return "semantic";
    default:
        return "unknown";
    }
}

char *process_sql(const char *sql_query, const char *strategy, const char **dictionary,
                  size_t dict_size, int dim, double threshold) {
    if (!sql_query) {
        return NULL;
    }

    PostprocessLevel level;
    if (parse_postprocess_level(strategy, &level) != 0) {
        return make_error_message("Error: Invalid strategy. Choose from cascading "
                                  "levels: none, keyword, typo, ngram, semantic.");
    }

    /* Stage 0 (none): Return copy of raw SQL directly without touching anything */
    if (level == POSTPROCESS_LEVEL_NONE) {
        size_t len = strlen(sql_query);
        char *res = (char *)malloc(len + 1);
        if (res) {
            memcpy(res, sql_query, len + 1);
        }
        return res;
    }

    /* Stage 2..4 require database dictionary */
    if (level >= POSTPROCESS_LEVEL_TYPO && (!dictionary || dict_size == 0)) {
        return make_error_message("Error: No valid dictionary provided for correction. "
                                  "Please check the DB path.");
    }

    if (level == POSTPROCESS_LEVEL_SEMANTIC && dim <= 0) {
        return make_error_message(
            "Error: dim is required when 'semantic' strategy is used.");
    }

    /* 1. Pre-correct SQL keywords (Stage 1..4) */
    char *pre_corrected = pre_correct_keywords(sql_query);
    if (!pre_corrected) {
        return make_error_message(
            "Error processing SQL: Failed keyword pre-correction");
    }

    /* 2. Tokenize SQL */
    TokenStream *stream = tokenize_sql(pre_corrected);
    free(pre_corrected);

    if (!stream) {
        return make_error_message("Error processing SQL: Tokenization failed");
    }

    /* 3. Correct identifiers in-place if level >= TYPO */
    if (level >= POSTPROCESS_LEVEL_TYPO && dictionary && dict_size > 0) {
        correct_identifiers_by_level(stream, dictionary, dict_size, level, dim,
                                     threshold);
    }

    /* 4. Reconstruct SQL string */
    char *result = reconstruct_sql(stream);
    free_token_stream(stream);

    if (!result) {
        return make_error_message("Error processing SQL: Reconstruction failed");
    }

    return result;
}
