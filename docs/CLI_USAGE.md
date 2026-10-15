# SQL CLI (`sql-cli`) 사용 설명서 및 옵션 가이드

본 문서는 `src/cli/main.c`로 구현된 **`sql-cli`** 콘솔 인터페이스의 사용 방법, 지원하는 실행 모드, 명시적 옵션 플래그 및 대화형(Interactive) REPL 환경을 설명합니다.

---

## 1. 개요 (Overview)

`sql-cli`는 C 언어로 구현된 Transformer 추론 엔진(`src/core/engine/loader.c`)과 SQLite 스키마 기반 SQL 오타 교정기(`src/core/postprocessor/`)를 콘솔에서 사용하는 통합 도구입니다.

* **바이너리 파일**:
  * Linux / macOS: `build/sql-cli`
  * Windows: `build\sql-cli.exe`
* **기본 실행 방식**:
  ```bash
  # 1) 일회성 배치 실행 (명시적 플래그 사용)
  sql-cli <MODEL.gguf> --query "질문" --db <DB경로> [옵션...]

  # 2) 대화형 REPL 쉘 실행 (Zero-latency 연속 질의)
  sql-cli [MODEL.gguf] -i
  ```

---

## 2. 지원 모드 및 명시적 옵션 체계

### 1) 주요 모드 (Modes)

| 모드 플래그                    | 설명                                                                                      | 필수/선택 인자       |
| :------------------------ | :-------------------------------------------------------------------------------------- | :------------- |
| **`--query`** *(또는 `-q`)* | **[기본 질의]** 자연어 $\rightarrow$ LLM 추론 $\rightarrow$ 5단계 계단식 후처리기 파이프라인 (구 `--pipeline` 호환) | `QUESTION`     |
| **`--postprocess`**       | **[단독 후처리]** LLM 모델 없이 입력된 SQL에 대해 5단계 계단식 후처리 직접 적용                                  | `SQL`          |
| **`--json`**              | **[JSON 파일 입출력]** 입력 JSON을 읽고 질의별 SQL 결과(`question`, `sql`)를 출력 JSON 파일로 작성                 | `IN.json OUT.json` |
| **`--generate`**          | *(계약 테스트 호환)* 순수 모델 생성 (CLI에서는 `--query --strategy none` 사용 권장)                      | `QUESTION`     |
| **`--encode`**            | BPE 토큰화: 입력 텍스트를 정수 토큰 ID 배열로 변환                                                        | `TEXT`         |
| **`--logits`**            | 로짓 계산: 주어진 토큰 시퀀스에 대해 Vocab 확률 벡터 계산                                                    | `TOKEN_ID...`  |
| **`-i`, `--interactive`** | **[대화형]** 모델/DB를 메모리에 상주시킨 상태로 연속 질의 가능한 REPL 쉘 시작                                      | `[MODEL.gguf]` |

### 2) 5단계 계단식 후처리 전략 (Cascading Strategies)

전략은 반드시 **왼쪽에서 오른쪽 순서대로만 누적 적용**되며, 뒤쪽 단계는 생략할 수 있습니다 (순서 역전이나 중간 단계 건너뛰기는 유효성 검사에서 차단됩니다):

```text
[Stage 0: none]     날것의 SQL 그대로 유지 (구 generate 모드와 동일)
       ↓
[Stage 1: keyword]  SQL 예약어 및 구문 교정 (SELECT, FROM 등)
       ↓
[Stage 2: typo]     예약어 교정 + SQLite DB 스키마 기반 유사도 오타 교정 (--db 필수)
       ↓
[Stage 3: ngram]    예약어 + 오타 + N-gram 식별자 매칭 (--db 필수)
       ↓
[Stage 4: semantic] 예약어 + 오타 + N-gram + Word2Vec 임베딩 코사인 유사도 교정 (--db, --dim, --threshold 필수)
```

* **지정 방식**: 단일 전략 이름 (`none`, `keyword`, `typo`, `ngram`, `semantic`) 또는 누적 순서 (`keyword,typo`, `keyword,typo,ngram` 등)로 지정할 수 있습니다.

### 3) 명시적 옵션 플래그 (One-line CLI: 기본값 없음, 직접 지정 필수)

배치/한 줄 실행 모드에서는 묵시적 기본값이 없으며, 모드 및 전략에 따라 필요한 모든 옵션을 직접 지정해야 합니다:

| 플래그                          | 설명                                      | 필수 여부                                                     |
| :--------------------------- | :-------------------------------------- | :-------------------------------------------------------- |
| **`-s`, `--strategy <전략>`**  | 5단계 계단식 후처리 전략 (`none`, `keyword`, `typo`, `ngram`, `semantic`) | **모든 한 줄 실행 필수**                                         |
| **`--max-tokens <정수>`**      | 모델 추론 시 최대 생성 토큰 수                      | **`--query` 및 `--json` 실행 시 필수**                        |
| **`-b`, `--db <경로>`**        | 스키마 사전(테이블/컬럼명)을 로드할 SQLite `.db` 파일 경로 | **`typo`, `ngram`, `semantic` 전략 사용 시 필수**             |
| **`-d`, `--dim <값>`**        | 단어 임베딩 벡터 차원 (`50`, `100`, `200`, `300`)   | **`semantic` 전략 사용 시 필수**                               |
| **`-t`, `--threshold <실수>`** | `semantic` 유사도 코사인 임계값 (0.0 ~ 1.0)        | **`semantic` 전략 사용 시 필수**                               |

> [!IMPORTANT]
> **배치 모드 vs 대화형 모드 정책**:
> * **CLI 한 줄 실행**: 기본값이 없으므로 필요한 옵션을 모두 명령줄에 명시해야 합니다. 누락 시 안내 오류와 함께 종료 코드 1로 거부됩니다.
> * **대화형 모드 (`-i`)**: 쉘 편의를 위해 `assets/` 자산 자동 탐색 및 빠른 시작 기본값이 제공되며, `/set` 명령어로 자유롭게 재설정할 수 있습니다.

---

## 3. 대화형 (Interactive) REPL 모드

대화형 쉘에서는 약 120MB의 신경망 모델 가중치와 SQLite 스키마를 메모리에 한 번만 로드해 두고 유지하므로, 매 질문마다 디스크 I/O 없이 **0.05초 이내에 즉각 응답**합니다.

### 1) 진입 방법 및 자산 자동 탐색 (Auto-Discovery)
```bash
# 1. 인자 없이 실행 (터미널 환경에서 자동 쉘 진입 및 assets/ 폴더 내 자산 자동 로드)
./build/sql-cli

# 2. 명시적으로 대화형 플래그 지정
./build/sql-cli -i

# 3. 특정 모델 파일을 지정하여 진입
./build/sql-cli assets/stage1-contract2-rc1/model.gguf -i
```
> [!TIP]
> 모델/DB를 지정하지 않고 쉘을 시작하면, `assets/` 및 하위 폴더를 스캔하여 존재하는 유효 모델(`model.gguf`), DB 스키마(`shop.db`), GloVe 임베딩(`semantic_dict_300d.bin`)을 **자동으로 감지하여 즉시 사용할 수 있도록 기본 로드**합니다.

### 2) 대화형 쉘 설정 및 제어 명령어 (`/set` 등)
명령어는 슬래시(`/`) 접두사로 입력합니다.

```text
  /set asset <폴더|태그>   지정 폴더 내의 모델(*.gguf), DB(*.db), 임베딩(*.bin)을 일괄 로드
                         (별칭: /asset. 예: /set asset stage1-contract2-rc1 또는 /asset <폴더>)
  /set db <경로>         SQLite 데이터베이스 스키마 사전 로드 (예: /set db shop.db)
  /set model <경로>      GGUF 신경망 모델 파일 로드 (예: /set model model.gguf)
  /set embedding <차원>  GloVe 벡터 차원 설정 (50, 100, 200, 300. 기본값: 300)
  /set strategy <전략>   교정 전략 목록 설정 (예: /set strategy typo,ngram,semantic)
  /set threshold <실수>  유사도 코사인 임계값 설정 (기본값: 0.5)
  /set tokens <숫자>     최대 생성 토큰 수 설정 (기본값: 32)
  /set mode <모드>       실행 모드 전환 (query, gen, post, enc)
  /json <입력> <출력>    현재 쉘의 모드(query/gen), DB, 전략 설정을 반영하여 JSON 파일 일괄 처리
  /ls [경로|유형]        자산 폴더를 탐색하여 선택 가능한 모델(*.gguf), DB(*.db), 임베딩(*.bin) 목록 확인
                         (별칭: /list, /dir, /files. 예: /ls, /ls model, /ls db, /ls embedding, /ls assets/tag)
  /status                현재 로드된 모델, DB 경로, 교정 전략, 차원, 임계값 등 상태 확인
  /info (또는 /config, /state, /show) /status 명령어의 별칭 (동일 동작)
  /help                  사용 가능한 명령어 도움말 출력
  /exit / /quit / /q     쉘 종료
```

### 3) 자산 일괄 로드 (`/set asset`, `/asset`)
하위 폴더 이름이나 태그명 하나만 입력하면 모델, DB, 임베딩 벡터 사전을 한 번에 교체할 수 있습니다.

```text
sql-query> /set asset stage1-contract2-rc1
Loading asset bundle from 'assets/stage1-contract2-rc1' ...
[Asset] Loading model: assets/stage1-contract2-rc1/model.gguf ...
[Asset] Loaded database: assets/stage1-contract2-rc1/shop.db (15 schema items)
[Asset] Configured embedding: assets/stage1-contract2-rc1/semantic_dict_300d.bin (Dim: 300)

--- Current Configuration ---
  Model:       assets/stage1-contract2-rc1/model.gguf [Loaded]
  Database:    assets/stage1-contract2-rc1/shop.db (Schema items: 15)
  Strategy:    typo,ngram
  Dimension:   300
  Threshold:   0.50
  Max Tokens:  32
  Mode:        query
-----------------------------
```

### 4) 파일 탐색 및 빠른 설정 (`/ls`, `/list`)
자산 폴더에 어떤 모델, DB, 임베딩이 있는지 확장자 기준으로 빠르게 탐색하고, 표시된 안내에 따라 `/set` 또는 `/set asset` 명령어로 즉시 적용할 수 있습니다.

```text
sql-query> /ls
=== Available Assets & Selectable Files ===

  [Asset Bundles (Batch Load: Model + DB + Embedding)]
    assets/stage1-contract2-rc1
      -> /set asset stage1-contract2-rc1  (or /asset stage1-contract2-rc1)

  [Models (*.gguf)]
    assets/stage1-contract2-rc1/model.gguf
      -> /set model assets/stage1-contract2-rc1/model.gguf

  [Embedding Dictionaries (*.bin)]
    assets/stage1-contract2-rc1/semantic_dict_300d.bin (Dim: 300)
      -> /set embedding 300

  [Databases (*.db, *.sqlite)]
    assets/stage1-contract2-rc1/shop.db
      -> /set db assets/stage1-contract2-rc1/shop.db
===========================================

# 특정 파일/번들 유형만 필터링
sql-query> /ls asset
sql-query> /ls model
sql-query> /ls db
sql-query> /ls embedding

# 특정 하위 폴더 직접 탐색
sql-query> /ls assets/stage1-contract2-rc1
```

### 5) 대화형 세션 전체 예시
```text
$ ./build/sql-cli -i

======================================================================
            SQL Engine & Post-processor Interactive Shell             
======================================================================
 Type your question/query or a meta-command (e.g. '/set db shop.db', '/help').
[Auto-discovered] Loading model: assets/stage1-contract2-rc1/model.gguf ...
[Auto-discovered] Loaded database: assets/stage1-contract2-rc1/shop.db (15 schema items)
[Auto-discovered] Configured embedding: assets/stage1-contract2-rc1/semantic_dict_300d.bin (Dim: 300)

--- Current Configuration ---
  Model:       assets/stage1-contract2-rc1/model.gguf [Loaded]
  Database:    assets/stage1-contract2-rc1/shop.db (Schema items: 15)
  Strategy:    typo,ngram
  Dimension:   300
  Threshold:   0.50
  Max Tokens:  32
  Mode:        query
-----------------------------

sql-query> what city does ashley live in?
[model] SELECT city FROM customers WHERE name = 'ashley' (stop=eos)
===> SELECT city FROM customers WHERE name = 'ashley'

sql-query> /set mode post
Mode switched to: postprocess (SQL correction only)

sql-post> SELETC ctiy FRMO costumers
===> SELECT city FROM customers

sql-post> /exit
Bye!
```

---

## 4. 일회성 명령줄 실행 예시

### 1) 자연어 질의 및 스키마 기반 완성형 SQL 도출 (`--query`)
```bash
./build/sql-cli assets/stage1-contract2-rc1/model.gguf \
  --query "what city does ashley live in?" \
  --db assets/stage1-contract2-rc1/shop.db \
  --strategy typo,ngram \
  --max-tokens 64
```
**출력**:
```text
[model] SELECT city FROM customers WHERE name = 'ashley'
stop=eos
SELECT city FROM customers WHERE name = 'ashley'
```

### 2) Semantic 벡터 유사도 교정 적용 시 (`--strategy ...semantic --dim <값>`)
```bash
./build/sql-cli assets/stage1-contract2-rc1/model.gguf \
  --query "show user email" \
  --db assets/stage1-contract2-rc1/shop.db \
  --strategy typo,ngram,semantic \
  --dim 100 \
  --threshold 0.5
```
> [!IMPORTANT]
> `--strategy`에 `semantic`이 포함된 경우 `--dim`은 필수값이며, `50`, `100`, `200`, `300` 중 하나여야 합니다. 누락 시 오류 메시지를 출력하고 종료합니다.

### 3) 독립형 SQL 오타 교정 (`--postprocess`)
신경망 모델 없이 SQL 문장의 오타만 빠르게 교정하고 싶을 때 사용합니다.
```bash
./build/sql-cli --postprocess "SELETC ctiy FRMO costumers" \
  --db assets/stage1-contract2-rc1/shop.db \
  --strategy typo,ngram
```
**출력**:
```text
SELECT city FROM customers
```

### 4) CLI JSON 파일 입출력 모드 (`--json`)

입력 JSON 파일(`input.json`)에서 `question` 필드를 추출하여 SQL을 생성/후처리한 뒤, 각 질문과 생성된 SQL(`sql`)만 담은 결과 JSON 파일(`output.json`)로 저장합니다. 하나의 파일에 여러 개의 질의가 포함되어 있어도 일괄 처리합니다.

- **명령줄 실행 (Query 모드: LLM 추론 + 후처리)**:
  ```bash
  ./build/sql-cli assets/stage1-contract2-rc1/model.gguf \
    --json input.json output.json \
    --strategy typo,ngram \
    --db assets/stage1-contract2-rc1/shop.db \
    --max-tokens 64
  ```
- **순수 모델 생성(Stage 0: none)으로 일괄 변환 (후처리 없음)**:
  ```bash
  ./build/sql-cli assets/stage1-contract2-rc1/model.gguf \
    --json input.json output.json \
    --strategy none \
    --max-tokens 64
  ```
- **단독 후처리 JSON 일괄 변환 (`--postprocess --json`)**:
  모델 없이 SQL 목록이 담긴 JSON을 읽어 오타를 일괄 교정합니다:
  ```bash
  ./build/sql-cli --postprocess \
    --json input.json output.json \
    --strategy typo \
    --db assets/stage1-contract2-rc1/shop.db
  ```
- **대화형 쉘 내부에서 실행 (`/json`)**:
  대화형 쉘에서는 현재 활성화된 모드(`query` 또는 `post`), DB, 전략 설정을 그대로 반영하여 일괄 처리합니다:
  ```text
  sql-query> /set strategy typo
  sql-query> /json input.json output.json
  Processing JSON: 'input.json' -> 'output.json'
    Strategy: typo (Level 2)
    Database: assets/stage1-contract2-rc1/shop.db
  Successfully wrote results to 'output.json'
  ```
- **또는 출력 파일 경로 옵션(`-o` / `--output`) 사용**:
  ```bash
  ./build/sql-cli assets/stage1-contract2-rc1/model.gguf \
    --json input.json -o output.json \
    --db assets/stage1-contract2-rc1/shop.db
  ```
- **입력/출력 JSON 파일 형식 비교**:
  * **Query 모드 (`--query` / 기본)**:
    - 입력(`input.json`): `"question"` 키 탐색
    - 출력(`output.json`): `"question"`, `"sql"` 키로 구성
    ```json
    // input.json
    [
      { "question": "what city does ashley live in?" },
      { "question": "how many customers are there?" }
    ]
    // output.json
    [
      {
        "question": "what city does ashley live in?",
        "sql": "SELECT city FROM customers WHERE name = 'ashley'"
      },
      {
        "question": "how many customers are there?",
        "sql": "SELECT count(*) FROM customers"
      }
    ]
    ```
  * **Postprocess 모드 (`--postprocess`)**:
    - 입력(`input_sql.json`): `"sql"` 키 탐색
    - 출력(`output_sql.json`): `"input"`, `"output"` 키로 구성
    ```json
    // input_sql.json
    [
      { "sql": "SELETC * FRMO users" },
      { "sql": "SELETC custmer_city FRMO customers" }
    ]
    // output_sql.json
    [
      {
        "input": "SELETC * FRMO users",
        "output": "SELECT * FROM customers"
      },
      {
        "input": "SELETC custmer_city FRMO customers",
        "output": "SELECT customer_id FROM customers"
      }
    ]
    ```

---

## 5. 컴파일 방법 (CMake 통합 빌드)

### 1) CLI 실행 파일 단독 빌드 (`sql-cli`)
```bash
# 1. 빌드 디렉터리 구성 (최초 1회)
cmake -B build -DCMAKE_BUILD_TYPE=Release

# 2. sql-cli 단독 컴파일 (build/sql-cli 또는 build/sql-cli.exe 생성)
cmake --build build --target sql-cli --config Release
```

> **Windows (PowerShell 한 줄 빌드)**:
> ```powershell
> cmd.exe /c 'call "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat" && cmake -B build && cmake --build build --target sql-cli --config Release'
> ```

### 2) 전체 빌드 및 검증 테스트 실행 (CTest)
```bash
# 1. 전체 타깃 일괄 컴파일
cmake --build build --config Release

# 2. CTest 테스트 실행 (전체 통과 시 약 1초 소요)
ctest --test-dir build -C Release --output-on-failure
```

### 3) C 공유 라이브러리(`sql_engine`) 및 C# 바인딩 빌드
- **`sql_engine.dll` (또는 `libsql_engine.so`) 빌드**:
  ```bash
  cmake -B build -DSQL_BUILD_SHARED=ON -DCMAKE_BUILD_TYPE=Release
  cmake --build build --target sql_engine --config Release
  ```
- **C# .NET 8 바인딩 (`src/bindings/SqlEngine.Core`) 빌드**:
  ```bash
  dotnet build src/bindings/SqlEngine.Core
  ```

---

## 6. 종료 코드 (Exit Codes)

* `0`: 명령어 또는 REPL 정상 수행 완료
* `1`: 런타임 오류 (파일 손상, 메모리 부족, 필수 옵션 누락 등)
* `2`: 잘못된 명령줄 인자 개수 또는 사용법 오류 (Usage 출력)
