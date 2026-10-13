# capstone_sql C 추론 엔진 · SQL 후처리기

영어 자연어 질문으로부터 SQLite SQL을 생성하는 스크래치 Transformer의 C 실행 엔진 및 SQL 후처리기입니다. 
외부 런타임 종속성 없이 학습 저장소([polring/capstone-design-1](https://github.com/polring/capstone-design-1))가 배포한 단일 GGUF 파일과 SQLite 데이터베이스(`shop.db`)로 동작합니다.

CLI(콘솔) 환경과 크로스플랫폼 GUI(Uno Platform) 환경에서 동일한 C 코어를 공유할 수 있도록 모듈화된 아키텍처(`src/core/`, `src/cli/`)로 구성되어 있습니다.

---

## 1. 프로젝트 구조

```text
├── src/
│   ├── core/                          # [C] 핵심 라이브러리 (추론 + 후처리 파이프라인)
│   │   ├── include/                   # 공개 C API 헤더 (common.h, engine.h, pipeline.h, postprocessor.h)
│   │   ├── engine/                    # GGUF 로더, Transformer 순전파, BPE 토크나이저
│   │   ├── postprocessor/             # SQL 예약어 및 식별자 오타 교정 모듈
│   │   └── pipeline/                  # 추론 -> SQL 디코딩 -> 오타 교정 원스톱 파이프라인
│   ├── cli/                           # [C] 콘솔 CLI 프론트엔드 (main.c)
│   ├── bindings/                      # [C#] Core C 라이브러리 P/Invoke 바인딩 (.NET 8)
│   └── gui/                           # [Uno Platform] 크로스플랫폼 데스크톱 GUI (Windows, Linux, macOS)
├── fixtures/v<N>/<세트>/              # 계약 버전별 소형 테스트 모델 및 정답 기대값 (CONTRACT.md 7절)
├── tests/                             # 계약 기대값 검증(fixture_test.c) 및 오류 처리 테스트(error_test.c)
├── scripts/fetch-assets               # 학습 저장소 Release에서 모델, DB, 임베딩 다운로드 스크립트
├── Makefile                           # GCC/Make 기반 빌드 및 테스트 자동화 스크립트
└── CONTRACT.md                        # 모델 계산 및 데이터 규격 계약 문서
```

---

## 2. 모델 및 데이터베이스 동기화 (`model.gguf` & `shop.db`)

배포 모델은 학습 저장소의 GitHub Release(태그 `stage<N>-contract<V>`, `-rc` 접미사는 시험용)에서 4종 파일 세트로 제공됩니다:
* `model.gguf`: Transformer 가중치 및 BPE 토크나이저 메타데이터
* `shop.db`: 모델 학습 기준이 된 SQLite 데이터베이스 (테이블/컬럼 스키마 및 레코드)
* `model_info.json`: 모델 구조 및 하이퍼파라미터 정보
* `SHA256SUMS`: 다운로드 무결성 검증용 체크섬

### 모델과 DB가 태그별로 짝을 이루어야 하는 이유
1. **스키마 종속성**: Transformer 모델은 특정 데이터베이스 구조(예: `stage1`의 `customers`, `items` 등)를 기준으로 SQL 변환을 학습합니다.
2. **후처리기 오타 교정 정확도**: 후처리기는 `shop.db`로부터 테이블/컬럼명을 읽어와 단어 사전을 만듭니다. 모델 버전과 다른 구버전 DB를 연결하면 정상적으로 생성된 새 컬럼명을 오타로 오인하여 엉뚱한 단어로 잘못 교정하는 문제가 발생합니다.
3. 따라서 `scripts/fetch-assets`는 다운로드 시 `assets/<태그명>/` 폴더를 생성하여 해당 버전의 `model.gguf`와 `shop.db`를 항상 한 쌍으로 함께 관리합니다.

> **참고**: 대용량 모델(`model.gguf`)과 데이터베이스(`shop.db`)는 `.gitignore`에 등록되어 있어 Git 원격 저장소에는 올라가지 않으며, 로컬에만 보관됩니다.

---

## 3. 빌드 및 테스트

### Linux (GCC & Make)
```bash
make            # build/sql-cli 및 src/core/postprocessor 빌드
make test       # 계약 fixture 테스트 + 오류 거부 테스트 + 후처리기 단위 테스트
make clean
```

Make 없이 GCC로 직접 빌드할 때:
```bash
gcc -std=c11 -O2 -Wall -Wextra -Werror -Isrc/core/include -Isrc/core/postprocessor src/cli/main.c src/core/engine/loader.c src/core/pipeline/pipeline.c src/core/postprocessor/core.c src/core/postprocessor/parser.c src/core/postprocessor/replacement.c src/core/postprocessor/schema.c -lm -o build/sql-cli
```

### macOS (Apple Clang & Make)
Xcode Command Line Tools(`xcode-select --install`)가 설치된 환경에서 Apple Silicon(`arm64`) 및 Intel 네이티브 바이너리로 빌드됩니다:
```bash
make            # 빌드
make test       # 테스트 실행
```

Make 없이 Clang으로 직접 빌드할 때:
```bash
clang -std=c11 -O2 -Wall -Wextra -Werror -Isrc/core/include -Isrc/core/postprocessor src/cli/main.c src/core/engine/loader.c src/core/pipeline/pipeline.c src/core/postprocessor/core.c src/core/postprocessor/parser.c src/core/postprocessor/replacement.c src/core/postprocessor/schema.c -lm -o build/sql-cli
```

### Windows (Visual Studio / MSVC)
일반 터미널(PowerShell/CMD)에는 `cl.exe` 컴파일러 경로가 등록되어 있지 않으므로, Visual Studio의 64비트 빌드 환경(`vcvars64.bat`)을 불러와서 컴파일합니다.

**방법 1: PowerShell에서 한 줄로 환경 로드 및 빌드 (추천)**
```powershell
cmd.exe /c 'call "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat" && cl.exe /O2 /W3 /D_CRT_SECURE_NO_WARNINGS /Isrc\core\include /Isrc\core\postprocessor src\cli\main.c src\core\engine\loader.c src\core\postprocessor\core.c src\core\postprocessor\parser.c src\core\postprocessor\replacement.c src\core\postprocessor\schema.c /Fe:build\sql-cli.exe /Fo:build\'
```
*(설치된 VS 버전에 따라 경로의 `18`을 `2022` 등으로 변경합니다.)*

**방법 2: "Developer Command Prompt for VS" 터미널 사용**
1. Windows 시작 메뉴에서 **"Developer Command Prompt for VS"**를 검색하여 엽니다.
2. 프로젝트 디렉터리로 이동 후 바로 컴파일합니다:
```cmd
cl.exe /O2 /W3 /D_CRT_SECURE_NO_WARNINGS /Isrc\core\include /Isrc\core\postprocessor src\cli\main.c src\core\engine\loader.c src\core\postprocessor\core.c src\core\postprocessor\parser.c src\core\postprocessor\replacement.c src\core\postprocessor\schema.c /Fe:build\sql-cli.exe /Fo:build\
```

**방법 3: 현재 열려 있는 PowerShell 세션에 환경 변수 불러오기**
현재 PowerShell 창에서 아래 명령어를 한 번 실행해 두면, 이후 창을 닫기 전까지 `cl.exe ...` 명령을 바로 사용할 수 있습니다:
```powershell
& "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\Tools\Launch-VsDevShell.ps1"
```

---

## 4. 실행 방법

### 1) 모델, DB 및 임베딩 사전 가져오기 (Fetch)
`scripts/fetch-assets` 스크립트를 통해 필요한 구성 요소만 선택적으로 다운로드하거나 전체를 일괄 업데이트할 수 있습니다:

```bash
# 전체 일괄 업데이트 (모델 + DB + 임베딩 .bin 일괄 다운로드, 기본값)
scripts/fetch-assets stage1-contract2-rc1 all

# 모델 가중치만 가져오기 (model.gguf, model_info.json, SHA256SUMS)
scripts/fetch-assets stage1-contract2-rc1 model

# SQLite 스키마 DB만 가져오기 (shop.db)
scripts/fetch-assets stage1-contract2-rc1 db

# 후처리기 유사도 임베딩 사전만 가져오기 (semantic_dict_300d.bin)
scripts/fetch-assets stage1-contract2-rc1 embedding
```

### 2) CLI 실행 방법

자세한 모든 옵션 및 파라미터는 [docs/CLI_USAGE.md](docs/CLI_USAGE.md)를 참고하세요.

```bash
# [추천 1] 대화형 REPL 쉘 실행 (모델 1회 메모리 로드 후 Zero-latency 연속 질의)
./build/sql-cli assets/stage1-contract2-rc1/model.gguf -i

# [추천 2] 일회성 질문 질의: 자연어 -> SQL 생성 -> SQLite 스키마 오타 교정
./build/sql-cli assets/stage1-contract2-rc1/model.gguf \
  --query "what city does ashley live in?" \
  --db assets/stage1-contract2-rc1/shop.db \
  --strategy typo,ngram \
  --max-tokens 64

# [단독 후처리] 모델 없이 SQL 오타 직접 교정
./build/sql-cli --postprocess "SELETC ctiy FRMO costumers" --db assets/stage1-contract2-rc1/shop.db

# 순수 모델 추론: 질문 -> 날것의 SQL 생성 (stderr에 종료 이유 출력)
./build/sql-cli assets/stage1-contract2-rc1/model.gguf --generate "what city does ashley live in?" 64

# 텍스트 BPE 토큰화 (토큰 ID 출력)
./build/sql-cli assets/stage1-contract2-rc1/model.gguf --encode "SELECT stock FROM items;"

# 주어진 토큰 시퀀스의 마지막 위치 Logits 확률 벡터 출력
./build/sql-cli assets/stage1-contract2-rc1/model.gguf --logits 257 104 105 259
```

---

## 5. 계약 버전이 바뀔 때 (Contract Migration)

1. 학습 저장소에서 `CONTRACT_VERSION`을 올리고 `python -m src.export_fixtures --new-model`로 새 fixture를 생성합니다.
2. 이 저장소의 `fixtures/v<새 버전>/`으로 복사하고(`manifest.json` 해시 확인), `Makefile`의 `ERROR_FIXTURE` 및 테스트 기대치를 확인한 뒤 C 엔진 코드를 수정해 `make test`를 통과시킵니다. [CONTRACT.md](CONTRACT.md)도 함께 개정합니다.
3. 이 저장소가 먼저 병합된 뒤, 학습 저장소가 새 버전 태그로 GitHub Release를 발행합니다.

---

## 6. 코드 스타일 및 포맷 검사

[.clang-format](.clang-format)(들여쓰기 4칸, LLVM 기반) 스타일을 준수합니다.

```bash
# 포맷 적용
clang-format -i src/core/engine/loader.c src/cli/main.c

# CI 검사 (GitHub Actions)
clang-format --dry-run --Werror src/core/engine/loader.c src/cli/main.c tests/fixture_test.c tests/error_test.c
```
