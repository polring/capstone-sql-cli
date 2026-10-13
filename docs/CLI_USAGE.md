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
| **`--query`** *(또는 `-q`)* | **[기본 추천]** 자연어 $\rightarrow$ SQL 생성 $\rightarrow$ 스키마 오타 교정 원스톱 실행 (구 `--pipeline` 호환) | `QUESTION`     |
| **`--generate`**          | 순수 모델 생성: 오타 교정 없이 모델의 Raw SQL 출력                                                       | `QUESTION`     |
| **`--postprocess`**       | **[단독]** 모델 없이 입력된 SQL의 예약어 및 스키마 오타 직접 교정                                              | `SQL`          |
| **`--encode`**            | BPE 토큰화: 입력 텍스트를 정수 토큰 ID 배열로 변환                                                        | `TEXT`         |
| **`--logits`**            | 로짓 계산: 주어진 토큰 시퀀스에 대해 Vocab 확률 벡터 계산                                                    | `TOKEN_ID...`  |
| **`-i`, `--interactive`** | **[대화형]** 모델/DB를 메모리에 상주시킨 상태로 연속 질의 가능한 REPL 쉘 시작                                      | `[MODEL.gguf]` |

### 2) 명시적 옵션 플래그 (Options)

| 플래그                          | 설명                                      | 기본값          | 비고                                                         |
| :--------------------------- | :-------------------------------------- | :----------- | :--------------------------------------------------------- |
| **`-b`, `--db <경로>`**        | 스키마 사전(테이블/컬럼명)을 로드할 SQLite `.db` 파일 경로 | 없음           | 생략 시 SQL 예약어만 기본 교정 (배치 모드 자동 탐색 없음)             |
| **`-s`, `--strategy <목록>`**  | 적용할 후처리 교정 전략 목록 (쉼표 구분 순서 지정)          | `typo,ngram` | 사용 가능 전략: `typo`, `ngram`, `semantic`                      |
| **`-d`, `--dim <값>`**        | GloVe 단어 임베딩 벡터 차원                      | 없음           | **`semantic` 전략 포함 시 필수** (`50`, `100`, `200`, `300` 중 택1) |
| **`-t`, `--threshold <실수>`** | `semantic` 유사도 코사인 임계값                  | `0.0`        | 0.0 ~ 1.0 실수 (대화형 모드는 기본값 `0.5`)                     |
| **`--max-tokens <정수>`**      | 최대 생성 토큰 수                              | `32`         | 양의 정수                                                      |

> [!NOTE]
> **배치 모드 vs 대화형 모드 정책 차이**:
> * **기본 CLI 한 줄 실행 (배치 모드)**: 묵시적 기본값이나 임의의 자동 탐색 없이, 사용자가 명령줄에 명시한 옵션(`--db`, `--strategy`, `--dim` 등)만 엄격히 적용됩니다 (`threshold` 기본값 `0.0`).
> * **대화형 모드 (`-i`)**: 실행 시 `assets/` 디렉터리를 스캔하여 존재하는 자산을 자동으로 감지/기본 지정하며, `dim=300`, `threshold=0.5`가 적용되어 즉시 테스트할 수 있습니다.

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

# 특정 파일 유형만 필터링
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

---

## 5. 컴파일 방법

### Linux / macOS
```bash
make
```

### Windows (Visual Studio MSVC)
```powershell
cmd.exe /c 'call "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat" && cl.exe /O2 /W3 /D_CRT_SECURE_NO_WARNINGS /Isrc\core\include /Isrc\core\postprocessor src\cli\main.c src\core\engine\loader.c src\core\pipeline\pipeline.c src\core\postprocessor\core.c src\core\postprocessor\parser.c src\core\postprocessor\replacement.c src\core\postprocessor\schema.c /Fe:build\sql-cli.exe /Fo:build\'
```

---

## 6. 종료 코드 (Exit Codes)

* `0`: 명령어 또는 REPL 정상 수행 완료
* `1`: 런타임 오류 (파일 손상, 메모리 부족, 필수 옵션 누락 등)
* `2`: 잘못된 명령줄 인자 개수 또는 사용법 오류 (Usage 출력)
