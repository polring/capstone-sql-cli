#include "../include/pipeline.h"
#include "../include/engine.h"
#include "../postprocessor/core.h"
#include "../postprocessor/schema.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

struct SqlPipeline {
    Model model;
    char **dictionary;
    size_t dict_size;
};

SqlPipeline *sql_pipeline_create(const char *model_path, const char *db_path) {
    if (!model_path) {
        return NULL;
    }
    SqlPipeline *pipeline = (SqlPipeline *)calloc(1, sizeof(SqlPipeline));
    if (!pipeline) {
        return NULL;
    }

    pipeline->model = load_model(model_path);

    if (db_path && strlen(db_path) > 0) {
        if (load_dictionary_from_sqlite(db_path, &pipeline->dictionary,
                                        &pipeline->dict_size) != 0) {
            /* DB 로드 실패 시 사전 없이 진행 (경고 출력) */
            pipeline->dictionary = NULL;
            pipeline->dict_size = 0;
        }
    }

    return pipeline;
}

char *sql_pipeline_run(SqlPipeline *pipeline, const char *question, int max_new_tokens,
                       char **out_raw_sql, const char **out_stop_reason) {
    if (!pipeline || !question) {
        return NULL;
    }

    int capacity = pipeline->model.config.context_length;
    int *token_ids = (int *)allocate_array(capacity, sizeof(int));
    int prompt_count = prepare_prompt_tokens(&pipeline->model, question, token_ids);
    int token_count = prompt_count;

    int tokens_to_generate =
        max_new_tokens > 0 ? max_new_tokens : DEFAULT_MAX_NEW_TOKENS;
    const char *stop_reason =
        generate_tokens(&pipeline->model, token_ids, &token_count, tokens_to_generate);

    if (out_stop_reason) {
        *out_stop_reason = stop_reason;
    }

    /* 생성된 토큰 바이트 계산 */
    size_t raw_sql_len = 0;
    for (int index = prompt_count; index < token_count; index++) {
        int token_id = token_ids[index];
        raw_sql_len += pipeline->model.tokenizer.token_byte_lengths[token_id];
    }

    char *raw_sql = (char *)malloc(raw_sql_len + 1);
    if (!raw_sql) {
        free(token_ids);
        return NULL;
    }

    size_t offset = 0;
    for (int index = prompt_count; index < token_count; index++) {
        int token_id = token_ids[index];
        size_t len = pipeline->model.tokenizer.token_byte_lengths[token_id];
        memcpy(raw_sql + offset, pipeline->model.tokenizer.token_bytes[token_id], len);
        offset += len;
    }
    raw_sql[raw_sql_len] = '\0';

    free(token_ids);

    /* 후처리 실행 */
    char *processed_sql = NULL;
    if (pipeline->dictionary && pipeline->dict_size > 0) {
        processed_sql =
            process_sql(raw_sql, "typo,ngram", (const char **)pipeline->dictionary,
                        pipeline->dict_size, 0, 0.0);
    } else {
        /* 사전이 없을 경우 예약어 기본 보정 등 후처리 시도 (빈 사전) */
        processed_sql = process_sql(raw_sql, "typo,ngram", NULL, 0, 0, 0.0);
    }

    if (!processed_sql) {
        /* 후처리 실패 시 raw_sql 복사본 반환 */
        processed_sql = (char *)malloc(raw_sql_len + 1);
        if (processed_sql) {
            memcpy(processed_sql, raw_sql, raw_sql_len + 1);
        }
    }

    if (out_raw_sql) {
        *out_raw_sql = raw_sql;
    } else {
        free(raw_sql);
    }

    return processed_sql;
}

void sql_pipeline_destroy(SqlPipeline *pipeline) {
    if (!pipeline) {
        return;
    }
    release_model(&pipeline->model);
    if (pipeline->dictionary) {
        free_dictionary(pipeline->dictionary, pipeline->dict_size);
        pipeline->dictionary = NULL;
        pipeline->dict_size = 0;
    }
    free(pipeline);
}

void sql_free_string(char *str) {
    if (str) {
        free(str);
    }
}
