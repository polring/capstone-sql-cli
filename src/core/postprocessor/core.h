/**
 * @file core.h
 * @brief Core postprocessor coordination.
 */

#ifndef POSTPROCESSOR_CORE_H
#define POSTPROCESSOR_CORE_H

#include "common.h"
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef enum {
    POSTPROCESS_LEVEL_NONE = 0, /* Stage 0: No postprocessing, raw SQL */
    POSTPROCESS_LEVEL_KEYWORD =
        1, /* Stage 1: Keyword pre-correction + SQL formatting only */
    POSTPROCESS_LEVEL_TYPO =
        2, /* Stage 2: Stage 1 + Typo (difflib) identifier correction */
    POSTPROCESS_LEVEL_NGRAM = 3, /* Stage 3: Stage 2 + N-gram identifier correction */
    POSTPROCESS_LEVEL_SEMANTIC =
        4 /* Stage 4: Stage 3 + Semantic embedding correction */
} PostprocessLevel;

SQL_API int parse_postprocess_level(const char *strategy, PostprocessLevel *out_level);
SQL_API const char *postprocess_level_to_string(PostprocessLevel level);

SQL_API char *process_sql(const char *sql_query, const char *strategy,
                          const char **dictionary, size_t dict_size, int dim,
                          double threshold);

#ifdef __cplusplus
}
#endif

#endif /* POSTPROCESSOR_CORE_H */
