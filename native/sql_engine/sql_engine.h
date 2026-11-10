#ifndef SQL_ENGINE_H
#define SQL_ENGINE_H
#include <stddef.h>
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

typedef struct {
    unsigned char *data;
    size_t length;
    const char *stop_reason;
} SqlGeneration;

/* UTF-8 path. The caller owns the returned handle. NULL indicates failure. */
SQL_API SqlModel *sql_model_open(const char *path);
SQL_API void sql_model_close(SqlModel *model);
/* NUL-terminated ASCII question. Returns 1 on success, 0 on failure.
 * data is length-delimited (may contain NUL); stop_reason is static.
 * Release a successful result before reusing it. */
SQL_API int sql_model_generate(const SqlModel *model, const char *question, int limit,
                               SqlGeneration *out);
SQL_API void sql_generation_free(SqlGeneration *result);
/* Borrowed, thread-local UTF-8 message, valid until the next open/generate call. */
SQL_API const char *sql_get_last_error(void);
#ifdef __cplusplus
}
#endif
#endif
