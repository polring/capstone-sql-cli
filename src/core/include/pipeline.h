#ifndef SQL_PIPELINE_H
#define SQL_PIPELINE_H

#include "common.h"
#include "engine.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct SqlPipeline SqlPipeline;

/* 파이프라인 생성 (모델 경로 및 SQLite DB 경로) */
SQL_API SqlPipeline *sql_pipeline_create(const char *model_path, const char *db_path);

/* 후처리 교정 전략 및 파라미터 설정 (예: "typo,ngram,semantic", dim=100, threshold=0.0) */
SQL_API void sql_pipeline_set_strategy(SqlPipeline *pipeline, const char *strategy, int dim, double threshold);

/* 임베딩 사전 파일 경로 수동 지정 */
SQL_API void sql_pipeline_set_embedding_path(SqlPipeline *pipeline, const char *path);

/* 자연어 질문으로부터 SQL 생성 및 오타 교정 수행 */
SQL_API char *sql_pipeline_run(SqlPipeline *pipeline, const char *question, int max_new_tokens,
                              char **out_raw_sql, const char **out_stop_reason);

/* 파이프라인 리소스 해제 */
SQL_API void sql_pipeline_destroy(SqlPipeline *pipeline);

/* 라이브러리에서 동적 할당된 문자열 메모리 해제 */
SQL_API void sql_free_string(char *str);

#ifdef __cplusplus
}
#endif

#endif /* SQL_PIPELINE_H */
