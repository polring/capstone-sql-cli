#ifndef SQL_ENGINE_H
#define SQL_ENGINE_H

#include <stddef.h>
#include <stdint.h>
#include "common.h"

#define GGUF_VERSION 3
#define CONTRACT_VERSION 2
#define CONTRACT_VERSION_TEXT "2"
#define DEFAULT_MAX_NEW_TOKENS 32

typedef struct {
    char *name;
    uint32_t rank;
    size_t dimensions[2];
    size_t element_count;
    uint64_t data_offset;
    float *values;
} Tensor;

typedef struct {
    uint32_t left_token_id;
    uint32_t right_token_id;
    uint32_t merged_token_id;
} BpeMerge;

typedef struct {
    int vocab_size;
    int embedding_dim;
    int layer_count;
    int head_count;
    int ffn_dim;
    int context_length;
    float norm_epsilon;
    float rope_theta;
} ModelConfig;

typedef struct {
    char **seed_tokens;
    size_t seed_count;
    uint32_t pad_id;
    uint32_t bos_id;
    uint32_t eos_id;
    uint32_t sep_id;
    uint32_t seed_base;
    uint32_t merge_base;
    BpeMerge *merges;
    size_t merge_count;
    unsigned char **token_bytes;
    size_t *token_byte_lengths;
} Tokenizer;

typedef struct {
    ModelConfig config;
    Tensor *tensors;
    size_t tensor_count;
    Tokenizer tokenizer;
} Model;

#ifdef __cplusplus
extern "C" {
#endif

SQL_API void fail(const char *message);
SQL_API const char *sql_get_last_error(void);
SQL_API void sql_set_last_error(const char *message);
SQL_API void *allocate_array(size_t count, size_t element_size);
SQL_API void append_token_id(int *token_ids, int *token_count, int capacity,
                             int token_id);

SQL_API Model load_model(const char *file_path);
SQL_API void release_model(Model *model);

SQL_API float *forward(const Model *model, const int *token_ids, int token_count);
SQL_API int encode_text(const Model *model, const char *text, int *token_ids,
                        int capacity);
SQL_API char *normalize_question(const char *text);
SQL_API int prepare_prompt_tokens(const Model *model, const char *text, int *token_ids);
SQL_API const char *generate_tokens(const Model *model, int *token_ids,
                                    int *token_count, int max_new_tokens);
SQL_API int select_next_token(const Model *model, const float *logits);

#ifdef __cplusplus
}
#endif

#endif /* SQL_ENGINE_H */
