# SQL Post-processor C 구현 가이드

학습 저장소의 Python 구현체(`src/postprocessor/python`)와 같은 알고리즘·동작을 하도록 C 언어로 작성된 핵심 라이브러리 모듈(`src/core/postprocessor/`)에 대한 설계 및 사용 설명서입니다.

C 프로젝트에서 헤더를 인클루드하여 정적 라이브러리(`postprocessor.lib` 또는 `libpostprocessor.a`)로 링크해 사용할 수 있으며, 통합 CLI 도구(`build/sql-cli`)의 `--postprocess` 모드로 단독 실행할 수도 있습니다.

---

## 1. 아키텍처 및 모듈 구성

모듈은 `src/core/postprocessor/` 디렉터리에 위치하며 다음 파일들로 구성되어 있습니다:

| 파일명 | 역할 설명 |
| :--- | :--- |
| [`postprocessor.h`](../src/core/postprocessor/postprocessor.h) / [`../include/postprocessor.h`](../src/core/include/postprocessor.h) | 외부 공개용 C 라이브러리 인터페이스 헤더 |
| [`core.h`](../src/core/postprocessor/core.h) / [`core.c`](../src/core/postprocessor/core.c) | 전체 교정 파이프라인(`process_sql`) 제어 및 오케스트레이션 |
| [`parser.h`](../src/core/postprocessor/parser.h) / [`parser.c`](../src/core/postprocessor/parser.c) | SQL 예약어 사전 보정(`pre_correct_keywords`), 토크나이저 및 SQL 재구성기 |
| [`replacement.h`](../src/core/postprocessor/replacement.h) / [`replacement.c`](../src/core/postprocessor/replacement.c) | Typo(difflib 비율), N-gram(Jaccard 유사도), Semantic(임베딩 코사인 유사도) 교정기 및 전략 체이닝 |
| [`schema.h`](../src/core/postprocessor/schema.h) / [`schema.c`](../src/core/postprocessor/schema.c) | SQLite 데이터베이스로부터 테이블 및 컬럼 스키마 사전 로더 |
| [`tests/test_postprocessor.c`](../tests/test_postprocessor.c) | Python 테스트 케이스를 1:1 검증하는 C 단위 테스트 |

---

## 2. 알고리즘 1:1 대응 원리

1. **SQL 예약어 사전 보정 (`pre_correct_keywords`)**:
   - `SELECT`, `FROM`, `WHERE` 등 표준 SQL 예약어 리스트(`PRIMARY_KEYWORDS`)를 대상으로 `difflib.SequenceMatcher`와 동일한 Gestalt Pattern Matching 알고리즘을 수행하여 유사도 $\ge 0.7$일 때 오타를 사전 교정합니다 (예: `SELETC` $\rightarrow$ `SELECT`, `FRMO` $\rightarrow$ `FROM`).
   - 원래 토큰의 대소문자(전체 소문자 여부)를 유지합니다.

2. **식별자 토큰 추출 및 교정**:
   - 쿼리를 단어, 문자열 리터럴, 기호, 공백 등으로 분해하여 키워드가 아닌 단어를 식별자(테이블/컬럼명)로 인식합니다.
   - 원본 대소문자 속성(`is_all_upper`)을 기록하여 교정 후 원래 대소문자에 맞게 복원합니다.

3. **교정 알고리즘 (전략 및 체이닝)**:
   - **Typo**: Python `difflib.get_close_matches(word, dictionary, cutoff=0.7)`와 동일한 문자열 일치 비율($2M / (|A| + |B|)$)로 가장 유사한 사전 단어를 탐색합니다.
   - **N-gram**: 2-gram 부분 문자열 집합을 추출하고 Python의 `set.intersection` 및 `set.union`과 동일하게 Jaccard 유사도($|A \cap B| / (|A \cup B|)$)를 계산(cutoff $\ge 0.3$)합니다. 단어 길이가 $N$ 미만이면 Typo로 Fallback합니다.
   - **Semantic**: Snake_case로 분리된 단어들의 임베딩 벡터를 Mean-pooling한 뒤 코사인 유사도로 타겟 사전을 검색합니다. 캐시가 없으면 파이프라인 오류 없이 안전하게 원래 단어를 반환합니다.
   - **전략 체이닝**: `typo,ngram,semantic`처럼 쉼표로 연결된 전략 순서대로 교정을 시도하며, 먼저 교정에 성공한 전략이 나오면 즉시 중단합니다.

---

## 3. 빌드 및 테스트 방법 (CMake)

루트 `CMakeLists.txt`를 통해 프로젝트 전체와 함께 빌드됩니다:

### 빌드
```bash
cmake -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build --config Release
```

빌드 산출물:
* `build/postprocessor.lib` (또는 `libpostprocessor.a`): 다른 C 애플리케이션 및 CLI에서 링크 가능한 정적 라이브러리
* `build/test_postprocessor`: 단위 테스트 실행 파일

### C 단위 테스트 실행
```bash
ctest --test-dir build -R postprocessor -C Release --output-on-failure
# 또는 바이너리 직접 실행:
./build/test_postprocessor
```
Python pytest와 동일한 시나리오(예약어 보정, 식별자 보정, 복합 오타, n-gram, 체이닝, semantic fallback 등 17개 검증)를 모두 확인합니다.

---

## 4. 라이브러리로 사용하기 (`#include "postprocessor.h"`)

다른 C 프로그램에서 라이브러리로 임포트하여 사용할 때는 `postprocessor.h`를 인클루드하고 정적 라이브러리를 링크합니다.

### C 코드 예제:
```c
#include "postprocessor.h"
#include <stdio.h>
#include <stdlib.h>

int main(void) {
    /* 1. 스키마 사전 정의 (또는 load_dictionary_from_sqlite 사용) */
    const char *dictionary[] = {
        "users", "user_name", "email", "id", "name", "department", "manager_id"
    };
    size_t dict_size = sizeof(dictionary) / sizeof(dictionary[0]);

    /* 2. 교정할 SQL 입력 (예약어 및 컬럼 오타 포함) */
    const char *input_sql = "SELETC usr_name, emil FRMO users WHERE manger_id = 1";

    /* 3. process_sql 호출 (전략: typo,ngram) */
    char *fixed_sql = process_sql(input_sql, "typo,ngram", dictionary, dict_size, 0, 0.0);

    printf("교정 결과: %s\n", fixed_sql);
    /* 출력: SELECT user_name, email FROM users WHERE manager_id = 1 */

    /* 4. 메모리 해제 */
    free(fixed_sql);
    free_semantic_cache();
    return 0;
}
```

---

## 5. CLI 단독 후처리 도구로 사용하기 (`sql-cli --postprocess`)

신경망 모델 없이 입력 SQL만 직접 교정할 때는 통합 CLI 도구인 `sql-cli`의 `--postprocess` 모드를 사용합니다.

```bash
# 1. 기본 실행 (스키마 DB 연동)
./build/sql-cli --postprocess "SELETC usr_name, emil FRMO users" --db assets/stage1-contract2-rc1/shop.db

# 2. 특정 전략만 지정하여 실행 (N-gram)
./build/sql-cli --postprocess "SELECT id FROM departmnt" --strategy ngram --db assets/stage1-contract2-rc1/shop.db

# 3. GloVe 차원 지정하여 Semantic 전략 포함 실행
./build/sql-cli --postprocess "SELECT usr FROM users" --strategy typo,ngram,semantic --dim 100 --threshold 0.5 --db assets/stage1-contract2-rc1/shop.db
```

---

## 6. 벡터 임베딩(Semantic) 데이터 연동 방법

임베딩 파일(`semantic_dict_<dim>d.bin`)은 `scripts/fetch-assets` 스크립트를 통해 원격 GitHub Release에서 가져올 수 있으며, `assets/<태그명>/` 폴더에 위치합니다:

```bash
# Release에서 300d 임베딩 파일 다운로드
scripts/fetch-assets stage1-contract2-rc1 embedding
```
이후 `--dim 300` 옵션을 지정하면 동일 폴더의 `semantic_dict_300d.bin`을 자동으로 탐색하여 로드합니다.
