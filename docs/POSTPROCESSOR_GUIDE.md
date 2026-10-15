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

3. **5단계 계단식 교정 전략 (Cascading Strategies)**:
   - 교정 전략은 반드시 **왼쪽에서 오른쪽으로 순차적으로 누적 적용**됩니다:
     * **Stage 0: `none`**: 원본 SQL 유지 (구 `generate` 모드와 동일, 후처리 미적용)
     * **Stage 1: `keyword`**: 표준 SQL 예약어 사전 보정만 수행 (`SELETC` $\rightarrow$ `SELECT` 등)
     * **Stage 2: `typo`**: 예약어 보정 + SQLite DB 스키마 기반 Levenshtein/Gestalt 비율 유사도 교정 (cutoff $\ge 0.7$)
     * **Stage 3: `ngram`**: 예약어 + Typo + Jaccard 2-gram 유사도 매칭 (cutoff $\ge 0.3$)
     * **Stage 4: `semantic`**: 예약어 + Typo + N-gram + GloVe 단어 임베딩 코사인 유사도 교정
   - **체이닝 규칙**: 단일 이름(`keyword`, `typo`, `ngram`, `semantic`, `none`) 또는 누적 순서(`keyword,typo`, `keyword,typo,ngram` 등)로 지정할 수 있으며, 뒤쪽 옵션은 생략 가능하나 중간 단계를 건너뛰거나 순서를 역전할 수는 없습니다.

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
* `build/sql_engine.dll` (또는 `libsql_engine.so`): GUI / C# P/Invoke 연동용 동적 공유 라이브러리
* `build/test_postprocessor`: 단위 테스트 실행 파일

### C 단위 테스트 실행
```bash
ctest --test-dir build -R postprocessor -C Release --output-on-failure
# 또는 바이너리 직접 실행:
./build/test_postprocessor
```
Python pytest와 동일한 시나리오(예약어 보정, 식별자 보정, 복합 오타, n-gram, 체이닝, semantic fallback 등 17개 검증)를 모두 확인합니다.

---

## 4. 라이브러리로 사용하기

### 1) 통합 C API (`sql_postprocess`) - 권장
동적 공유 라이브러리(`sql_engine.dll`) 또는 `pipeline.h`를 통해 DB 스키마 로드와 교정을 원스톱으로 처리합니다:

```c
#include "pipeline.h"
#include <stdio.h>
#include <stdlib.h>

int main(void) {
    const char *input_sql = "SELETC usr_name, emil FRMO users WHERE manger_id = 1";
    
    // 5단계 계단식 전략: "none", "keyword", "typo", "ngram", "semantic"
    char *fixed_sql = sql_postprocess(input_sql, "ngram", "shop.db", 0, 0.0);
    
    if (fixed_sql != NULL) {
        printf("교정 결과: %s\n", fixed_sql);
        // 출력: SELECT user_name, email FROM users WHERE manager_id = 1
        sql_free_string(fixed_sql);
    }
    return 0;
}
```

### 2) C# P/Invoke 바인딩 (`SqlEngine.Core` - Uno Platform GUI 연동)
C# .NET 8 및 Uno Platform GUI 프로젝트에서는 `SqlPipeline.Postprocess()`를 직접 호출할 수 있습니다.
`sql_engine.dll`은 `SqlEngine.Core.csproj` 빌드 시 자동으로 출력 디렉터리에 복사됩니다.

```csharp
using SqlEngine.Core;

// 1. 단독 SQL 오타 교정
string correctedSql = SqlPipeline.Postprocess(
    sql: "SELETC usr_name, emil FRMO users WHERE manger_id = 1",
    strategy: "ngram", // 5단계 계단식 전략
    dbPath: "shop.db"
);

Console.WriteLine($"교정 결과: {correctedSql}");
// 출력: SELECT user_name, email FROM users WHERE manager_id = 1

// 2. GUI 프로세스 보호 안전 장치 (Crash Prevention)
// 비 ASCII 입력(한글 등)이나 잘못된 쿼리가 전달되더라도 C 엔진 내부의
// thread-local setjmp/longjmp bailout 메커니즘을 통해 GUI 호스트 프로세스가
// 비정상 종료(exit(1))되지 않고 안전하게 null 반환 및 에러 문자열을 기록합니다.
```

### 3) 저수준 C API (`postprocessor.h`)
정적 라이브러리 직접 링크 시 토크나이저와 사전 배열을 직접 전달하여 사용할 수 있습니다:

```c
#include "postprocessor.h"
#include <stdio.h>
#include <stdlib.h>

int main(void) {
    const char *dictionary[] = {
        "users", "user_name", "email", "id", "name", "department", "manager_id"
    };
    size_t dict_size = sizeof(dictionary) / sizeof(dictionary[0]);

    const char *input_sql = "SELETC usr_name, emil FRMO users WHERE manger_id = 1";
    char *fixed_sql = process_sql(input_sql, "typo,ngram", dictionary, dict_size, 0, 0.0);

    printf("교정 결과: %s\n", fixed_sql);
    free(fixed_sql);
    free_semantic_cache();
    return 0;
}
```

---

## 5. CLI 단독 후처리 도구로 사용하기 (`sql-cli --postprocess`)

신경망 모델 없이 입력 SQL만 직접 교정할 때는 통합 CLI 도구인 `sql-cli`의 `--postprocess` 모드를 사용합니다.
한 줄 실행 시 모든 옵션을 명시적으로 지정해야 합니다 (기본값 없음):

```bash
# 1. 예약어 보정만 적용 (Stage 1: keyword, --db 불필요)
./build/sql-cli --postprocess "SELETC * FRMO users" --strategy keyword

# 2. Typo 오타 교정 적용 (Stage 2: typo, --db 필수)
./build/sql-cli --postprocess "SELETC usr_name, emil FRMO users" --strategy typo --db assets/stage1-contract2-rc1/shop.db

# 3. N-gram 매칭 교정 적용 (Stage 3: ngram, --db 필수)
./build/sql-cli --postprocess "SELECT id FROM departmnt" --strategy ngram --db assets/stage1-contract2-rc1/shop.db

# 4. GloVe 차원 및 코사인 임계값 지정하여 Semantic 교정 (Stage 4: semantic, --db, --dim, --threshold 필수)
./build/sql-cli --postprocess "SELECT usr FROM users" --strategy semantic --dim 300 --threshold 0.5 --db assets/stage1-contract2-rc1/shop.db

# 5. JSON 파일 일괄 후처리 (input.json 내 "sql" 키 탐색 -> output.json 내 "input", "output"으로 저장)
./build/sql-cli --postprocess --json input_sql.json output_sql.json --strategy typo --db assets/stage1-contract2-rc1/shop.db
```

---

## 6. 벡터 임베딩(Semantic) 데이터 연동 방법

임베딩 파일(`semantic_dict_<dim>d.bin`)은 `scripts/fetch-assets` 스크립트를 통해 원격 GitHub Release에서 가져올 수 있으며, `assets/<태그명>/` 폴더에 위치합니다:

```bash
# Release에서 300d 임베딩 파일 다운로드
scripts/fetch-assets stage1-contract2-rc1 embedding
```
이후 `--strategy semantic --dim 300` 옵션을 지정하면 동일 폴더의 `semantic_dict_300d.bin`을 로드하여 사용합니다.
