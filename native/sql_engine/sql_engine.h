#ifndef SQL_ENGINE_H
#define SQL_ENGINE_H
#include <stdint.h>
#if defined(_WIN32) || defined(__CYGWIN__)
#if defined(SQL_BUILD_SHARED)
#define SQL_API __declspec(dllexport)
#elif defined(SQL_USE_SHARED)
#define SQL_API __declspec(dllimport)
#else
#define SQL_API
#endif
#elif defined(__GNUC__) && __GNUC__ >= 4
#define SQL_API __attribute__((visibility("default")))
#else
#define SQL_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct SqlModel SqlModel;

/* UTF-8 path. The caller owns the returned handle. NULL indicates failure. */
SQL_API SqlModel *sql_model_open(const char *path);
SQL_API void sql_model_close(SqlModel *model);
/* Compute final-position F32 logits for a nonempty prefix (up to model context).
 * Both arrays belong to the caller. logits_capacity must be at least vocab_size.
 * Returns 1 on success and stores native forward time in milliseconds.
 * On failure returns 0, stores zero elapsed time, and logits must be discarded. */
SQL_API int sql_model_forward(const SqlModel *model, const int32_t *token_ids,
                              int32_t token_count, float *logits,
                              int32_t logits_capacity, double *elapsed_ms);

enum SqlStopReason {
    SQL_STOP_EOS = 0,
    SQL_STOP_MAX_NEW_TOKENS = 1,
    SQL_STOP_CONTEXT = 2
};

/* One generation request with a private KV cache. allowed has one byte per
 * vocab entry; nonzero means decodable. EOS is always eligible and wins ties.
 * generated holds emitted IDs only (no prompt or EOS); capacity must be >=
 * min(max_new_tokens, context - prompt_count). It can be NULL if none can fit.
 * compute_ms counts native cache setup/prefill/decode, excluding token selection.
 * Returns 1 on success; on error discard generated, count and time become 0. */
SQL_API int sql_model_generate_ids(const SqlModel *model, const int32_t *prompt_ids,
                                   int32_t prompt_count, const uint8_t *allowed,
                                   int32_t allowed_count, int32_t eos_id,
                                   int32_t max_new_tokens, int32_t *generated,
                                   int32_t generated_capacity, int32_t *generated_count,
                                   int32_t *stop_reason, double *compute_ms);
/* Borrowed, thread-local UTF-8 message, valid until the next engine call. */
SQL_API const char *sql_get_last_error(void);
#ifdef __cplusplus
}
#endif
#endif
