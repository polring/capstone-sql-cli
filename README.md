# capstone_sql C 추론 엔진 · SQL 후처리기

영어 자연어 질문으로부터 SQLite SQL을 생성하는 스크래치 Transformer의 C 실행 엔진 및 SQL 후처리기입니다.  
외부 런타임 종속성 없이 학습 저장소([polring/capstone-design-1](https://github.com/polring/capstone-design-1))가 배포한 단일 GGUF 파일과 SQLite 데이터베이스(`shop.db`)로 동작합니다.

CLI(콘솔) 환경과 크로스플랫폼 GUI(Windows 11 WinUI 3 & Uno Platform) 환경에서 동일한 C 코어를 공유할 수 있도록 모듈화된 계층형 아키텍처로 구성되어 있습니다.

---

## 🛠️ 기술 스택 및 버전 명세 (Tech Stack & Versions)

| 구분 | 기술 / 도구 | 버전 | 사용 용도 및 비고 |
| :--- | :--- | :--- | :--- |
| **언어 (Languages)** | **C** | **C11** (`CMAKE_C_STANDARD 11`) | 코어 GGUF 파서, Transformer 순전파 연산, 5단계 후처리 오타 교정기 |
| | **C#** | **C# 14** | 네이티브 P/Invoke 바인딩 및 데스크톱 GUI 애플리케이션 |
| | **Bash** | **Bash 4+** | 릴리스 에셋 자동 다운로드 및 체크섬 검증 스크립트 |
| **프레임워크 / SDK** | **.NET** | **.NET 10.0 (LTS)** | GUI 및 P/Invoke 네이티브 래퍼 런타임 |
| | **Windows App SDK** | **10.0.26100 (Windows 11 24H2)** | Windows 11 순정 WinUI 3 네이티브 Fluent UI 런타임 |
| | **Uno Platform** | **6.7.30 (`Uno.Sdk`)** | 크로스플랫폼 XAML 데스크톱 프레임워크 (Windows / Linux / macOS) |
| | **SkiaSharp** | Uno Platform 내장 | Linux, macOS 크로스플랫폼 데스크톱 그래픽 렌더러 |
| **빌드 시스템** | **CMake** | **3.16 이상** | 크로스플랫폼 C 코어 컴파일 및 CTest 테스트 시스템 |
| | **MSVC** | **Visual Studio 2022 / 2026 (v143 / v144)** | Windows 64비트 C/C++ 네이티브 컴파일러 툴체인 |
| **라이브러리** | **CommunityToolkit.Mvvm** | **8.x (Uno 번들)** | 반응형 MVVM 아키텍처 및 상태 관리 코드 생성기 |
| | **Microsoft.Data.Sqlite** | **9.0.2** | SQLite 연결 및 쿼리 실행기 (ADO.NET) |
| | **SQLitePCLRaw** | **3.0.5** (`bundle_e_sqlite3`) | SQLite3 네이티브 임베디드 런타임 엔진 |
| | **SQLite3 (C API)** | **3.x** | C 후처리기 내 스키마 어휘 사전 구축용 라이브러리 |
| | **GGUF 포맷** | **v2 / v3** | 외부 종속성 없이 자체 C로 구현한 텐서/메타데이터 바이너리 규격 |

---

## 1. 프로젝트 구조

```text
capstone-sql-cli/
├── src/
│   ├── core/                          # C 추론 코어 및 후처리기
│   │   ├── include/                   # 공개 C API 헤더
│   │   ├── engine/                    # GGUF 파서, Transformer 추론, BPE 토크나이저
│   │   ├── postprocessor/             # SQL 키워드 및 스키마 식별자 오타 교정 모듈
│   │   └── pipeline/                  # 추론-후처리 통합 파이프라인
│   ├── cli/                           # 콘솔 CLI 프론트엔드
│   │   └── main.c
│   ├── bindings/                      # C# P/Invoke 바인딩
│   │   └── SqlEngine.Core/
│   └── gui/                           # 데스크톱 GUI (Uno Platform / WinUI 3)
│       ├── SqlApp.sln
│       ├── runtimes/                  # OS별 C 네이티브 라이브러리 (win-x64, linux-x64, osx-arm64)
│       └── SqlApp/
│           ├── Models/
│           ├── Services/              # 에셋 탐색, DB 쿼리 실행, C 바인딩 연동
│           ├── ViewModels/            # MVVM 뷰모델
│           ├── Converters/
│           ├── MainPage.xaml          # 메인 UI
│           └── Platforms/             # 플랫폼별 진입점 (Windows App SDK / Skia Desktop)
├── assets/                            # 다운로드된 모델(.gguf), DB(.db), 임베딩(.bin)
├── fixtures/v<N>/                     # 계약 버전별 소형 검증 모델 및 정답 데이터
├── tests/                             # 단위, 오류 거부, 계약 픽스처 테스트
├── docs/                              # 상세 문서 (POSTPROCESSOR_GUIDE)
├── scripts/
│   └── fetch-assets                   # 릴리스 에셋 다운로드 스크립트
├── CMakeLists.txt                     # CMake 빌드 설정
├── Makefile                           # 빌드 편의 스크립트
├── CONTRACT.md                        # 모델 계산 및 데이터 규격 계약서
└── .clang-format                      # C 코드 서식 규칙
```

---

## 2. Contract에 대한 내용 (규격 명세 및 마이그레이션)

`CONTRACT.md`는 학습 저장소([polring/capstone-design-1](https://github.com/polring/capstone-design-1))와 본 C 추론 코어 간의 **수학적 계산 및 데이터 입출력 규격 계약**입니다.

### 1) 핵심 계약 규격
* **텐서 명명 규칙**: 모델 가중치 텐서 이름(`tok_embeddings.weight`, `layers.<N>.attention.wq.weight`, `output.weight` 등).
* **토크나이저 메타데이터**: BPE 토큰 어휘 사전(`tokenizer.ggml.tokens`), 병합 규칙(`tokenizer.ggml.merges`), 특수 토큰(`[bos]`, `[eos]`, `[pad]`, `[unk]`).
* **수치 정밀도 허용오차 (Golden Logits & Loss)**: 학습 프레임워크(PyTorch)와 C 네이티브 추론 엔진 간의 부동소수점 오차 허용 한계(Cosine Similarity > 0.9999, Logits Absolute Diff < 1e-4).
* **무결성 검증**: `fixtures/v<N>/` 디렉터리에 계약 버전별 소형 모델 및 입출력 정답 쌍을 보관하여 회귀 테스트 수행.

### 2) 계약 버전이 바뀔 때 (Contract Migration 절차)

1. **학습 저장소 작업**: `CONTRACT_VERSION`을 올리고 `python -m src.export_fixtures --new-model`을 실행하여 새 버전 픽스처를 생성합니다.
2. **픽스처 이관**: 생성된 픽스처를 본 저장소의 `fixtures/v<새 버전>/`으로 복사하고 `manifest.json` 해시를 대조합니다.
3. **C 코어 업데이트 및 테스트 통과**: C 엔진 코드를 수정하여 `ctest --test-dir build -C Release --output-on-failure`가 100% 통과하도록 만듭니다. [CONTRACT.md](CONTRACT.md)의 변경 내역을 함께 개정합니다.
4. **저장소 릴리스 동기화**: 본 저장소의 PR이 먼저 병합된 후, 학습 저장소가 새 버전 태그로 GitHub Release를 발행합니다.

---

## 3. Asset 동기화 방법 (`model.gguf`, `shop.db`, `semantic_dict.bin`)

배포 모델은 학습 저장소의 GitHub Release(태그 형식: `stage<N>-contract<V>`, 시험용은 `-rc` 접미사)에서 버전별 번들 세트로 제공됩니다:
* `model.gguf`: Transformer 가중치 및 BPE 토크나이저 메타데이터
* `shop.db`: 모델 학습 기준이 된 SQLite 데이터베이스 (테이블/컬럼 스키마 및 레코드)
* `semantic_dict_300d.bin`: 시맨틱 벡터 유사도 교정을 위한 임베딩 사전
* `model_info.json`: 모델 구조 및 하이퍼파라미터 정보
* `SHA256SUMS`: 무결성 검증용 SHA-256 체크섬

### 모델과 DB가 태그별로 짝을 이루어야 하는 이유
1. **스키마 종속성**: Transformer 모델은 특정 데이터베이스 구조(예: `stage1`의 `customers`, `items`, `orders`)를 기반으로 질의 변환을 학습합니다.
2. **후처리기 오타 교정 정확도**: 후처리기는 `shop.db`로부터 테이블/컬럼명을 읽어와 단어 사전을 구축합니다. 모델 버전과 다른 구버전 DB를 연결하면 정상적으로 생성된 새 컬럼명을 오타로 오인하여 잘못 교정하는 문제가 발생합니다.
3. 따라서 `scripts/fetch-assets`는 `assets/<태그명>/` 폴더를 생성하여 해당 버전의 에셋들을 항상 한 쌍으로 관리합니다.

### Asset 다운로드 및 동기화 실행

#### [Windows (PowerShell / CMD / Git Bash)]
```powershell
# 1. [최신 버전 일괄 자동 다운로드] 최신 Release 태그를 자동 감지하여 모든 자산 동기화
bash scripts/fetch-assets

# 2. 특정 구성 요소만 최신 버전으로 가져오기
bash scripts/fetch-assets model          # 최신 버전 model.gguf 다운로드
bash scripts/fetch-assets db             # 최신 버전 shop.db 다운로드
bash scripts/fetch-assets embedding      # 최신 버전 semantic_dict_300d.bin 다운로드

# 3. 특정 릴리스 버전을 명시하여 다운로드
bash scripts/fetch-assets stage1-contract2 all
bash scripts/fetch-assets stage1-contract2 model
```
> **팁**: `bash`가 인식되지 않는 경우 Git for Windows 기본 경로 직접 호출:
> ```powershell
> "C:\Program Files\Git\bin\bash.exe" scripts/fetch-assets
> ```

#### [Linux / macOS]
```bash
# 실행 권한 부여 (최초 1회)
chmod +x scripts/fetch-assets

# 1. [최신 버전 일괄 자동 다운로드] 최신 Release 태그를 자동 감지하여 모든 자산 동기화
scripts/fetch-assets

# 2. 특정 구성 요소만 최신 버전으로 가져오기
scripts/fetch-assets model          # 최신 버전 model.gguf 다운로드
scripts/fetch-assets db             # 최신 버전 shop.db 다운로드
scripts/fetch-assets embedding      # 최신 버전 semantic_dict_300d.bin 다운로드

# 3. 특정 릴리스 버전을 명시하여 다운로드
scripts/fetch-assets stage1-contract2 all
scripts/fetch-assets stage1-contract2 model
```

> **참고**: 대용량 바이너리(`*.gguf`, `*.db`, `*.bin`)는 `.gitignore`에 등록되어 로컬에만 보관됩니다.

### 다운로드 파일 검증

모든 다운로드 대상(`all`, `model`, `db`, `embedding`/`emb`)은 같은 릴리스의
`SHA256SUMS`를 받아 **선택한 파일만** SHA-256으로 검사합니다. `sha256sum` 또는
`shasum`이 필요합니다. 검증에 통과한 파일만 출력 폴더에 저장하며, 다운로드 실패·체크섬
누락·불일치 시 파일명을 안내하고 오류로 종료합니다. 해당 파일의 기존 로컬 사본은 보존됩니다.
`all` 실행 중 앞서 검증하여 저장한 다른 파일까지 되돌리지는 않습니다.

이전 릴리스의 `SHA256SUMS`에 임베딩 사전 항목이 없으면 `all`은 경고와 함께 사전을
건너뜁니다. 명시적으로 `embedding`/`emb`를 요청하면 해당 항목이 반드시 필요합니다.
사전 항목이 있는데 다운로드나 검증이 실패하는 경우에는 `all`도 오류로 종료합니다.

큰 모델이나 네트워크 연결 없이 다운로드 검증 테스트를 실행할 수 있습니다.
작은 테스트 파일과 모의 다운로드 명령을 사용하며 Linux·macOS CI에서도 실행합니다.

```bash
bash tests/test_fetch_assets.sh
```

---

## 4. CLI 빌드 및 실행 방법

### 1) CLI 빌드 방법 (CMake)

#### [Developer PowerShell for VS]
```powershell
# 1. CMake 빌드 환경 생성
cmake -B build

# 2. Release 모드로 sql-cli 빌드
cmake --build build --target sql-cli --config Release
```
* **생성 바이너리**: `build/sql-cli.exe`

#### [Windows (일반 PowerShell / CMD)]
```powershell
cmd.exe /c 'call "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat" && cmake -B build && cmake --build build --target sql-cli --config Release'
```
* **생성 바이너리**: `build/sql-cli.exe`
* *(참고: Visual Studio 2022 환경인 경우 경로의 `18` 대신 `2022`로 지정)*

#### [Linux / macOS]
```bash
# 1. 빌드 환경 설정 (최초 1회)
cmake -B build -DCMAKE_BUILD_TYPE=Release

# 2. sql-cli 실행 파일 빌드
cmake --build build --target sql-cli --config Release
```
* **생성 바이너리**: `build/sql-cli`

---

### 2) CLI 실행 방법

```bash
# [추천 1] 대화형 REPL 쉘 실행
./build/sql-cli -i

# 특정 모델 파일을 지정하여 실행
./build/sql-cli assets/stage1-contract2/model.gguf -i

# [추천 2] 일회성 질문 질의
./build/sql-cli assets/stage1-contract2/model.gguf \
  --query "what city does ashley live in?" \
  --db assets/stage1-contract2/shop.db \
  --strategy typo,ngram \
  --max-tokens 64
```

---

## 5. Uno Platform(Windows11 (WinUI3) & Cross-Platform(Skia))

.NET 10 (LTS) 기반의 데스크톱 대화형 GUI 애플리케이션입니다. C 추론 엔진(`sql_engine.dll` / `libsql_engine.so`) 및 SQLite와 직접 연동되어 Claude Desktop 스타일의 멀티 세션 대화 환경을 제공합니다.

![GUI 실행 화면 - 질의 모드 및 세션 관리](docs/gui_1.png)

![GUI 실행 화면 - 후처리 교정 및 DB 쿼리 결과](docs/gui_2.png)

### 1) 사전 준비: C 공유 라이브러리 및 바인딩 빌드

GUI가 P/Invoke로 호출할 C 공유 라이브러리를 먼저 컴파일합니다 (반드시 1번 → 2번 순서대로 실행):

#### [Developer PowerShell for VS]
```powershell
# 1. C 공유 라이브러리(sql_engine.dll) 빌드
cmake -B build -DSQL_BUILD_SHARED=ON
cmake --build build --target sql_engine --config Release

# 2. C# P/Invoke 바인딩 프로젝트(SqlEngine.Core) 컴파일
dotnet build src/bindings/SqlEngine.Core
```
* **생성 파일**: `build/sql_engine.dll`

#### [Windows (일반 PowerShell / CMD)]
```powershell
# 1. C 공유 라이브러리(sql_engine.dll) 빌드 (MSVC 원라이너)
cmd.exe /c 'call "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat" && cmake -B build -DSQL_BUILD_SHARED=ON && cmake --build build --target sql_engine --config Release'

# 2. C# P/Invoke 바인딩 프로젝트(SqlEngine.Core) 컴파일
dotnet build src/bindings/SqlEngine.Core
```
* **생성 파일**: `build/sql_engine.dll`
* *(참고: Visual Studio 2022 환경인 경우 경로의 `18` 대신 `2022`로 지정)*

#### [Linux / macOS]
```bash
# 1. C 공유 라이브러리(libsql_engine.so / libsql_engine.dylib) 빌드
cmake -B build -DSQL_BUILD_SHARED=ON -DCMAKE_BUILD_TYPE=Release
cmake --build build --target sql_engine --config Release

# 2. C# P/Invoke 바인딩 프로젝트(SqlEngine.Core) 컴파일
dotnet build src/bindings/SqlEngine.Core
```

---

### 2) GUI 빌드 방법

#### [Windows 전용 (WinUI 3)]
```powershell
# 개발(Debug) 빌드
dotnet build src/gui/SqlApp -f net10.0-windows10.0.26100

# 릴리즈(Release) 빌드 (최적화 및 빠른 반응 속도)
dotnet build src/gui/SqlApp -c Release -f net10.0-windows10.0.26100
```
* **Debug 빌드 출력**: `src/gui/SqlApp/bin/Debug/net10.0-windows10.0.26100/SqlApp.exe`
* **Release 빌드 출력**: `src/gui/SqlApp/bin/Release/net10.0-windows10.0.26100/SqlApp.exe`

#### [모든 플랫폼 (Skia)]
```bash
# 개발(Debug) 빌드
dotnet build src/gui/SqlApp -f net10.0-desktop

# 릴리즈(Release) 빌드
dotnet build src/gui/SqlApp -c Release -f net10.0-desktop
```

---

### 3) GUI 실행 방법

#### [Windows 전용 (WinUI 3)]
```powershell
# [추천] 빌드된 네이티브 .exe 직접 실행 (Debug)
.\src\gui\SqlApp\bin\Debug\net10.0-windows10.0.26100\SqlApp.exe

# [추천] 빌드된 네이티브 .exe 직접 실행 (Release - 권장)
.\src\gui\SqlApp\bin\Release\net10.0-windows10.0.26100\SqlApp.exe

# 또는 dotnet run 실행 (Debug)
dotnet run --project src/gui/SqlApp -f net10.0-windows10.0.26100 --no-launch-profile

# 또는 dotnet run 실행 (Release)
dotnet run --project src/gui/SqlApp -c Release -f net10.0-windows10.0.26100 --no-launch-profile
```

#### [모든 플랫폼 (Skia)]
```bash
# 개발(Debug) 모드 실행
dotnet run --project src/gui/SqlApp -f net10.0-desktop

# 릴리즈(Release) 모드 실행
dotnet run --project src/gui/SqlApp -c Release -f net10.0-desktop
```

---

### 4) 패키징 방법

MSIX 패키징이나 개발자 인증서 서명 없이, 더블 클릭만으로 바로 실행되는 단독 실행 바이너리로 배포합니다:

#### [Windows 전용 (WinUI 3)]
```powershell
dotnet publish src/gui/SqlApp -f net10.0-windows10.0.26100 -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```
* **배포 바이너리 출력**: `src/gui/SqlApp/bin/Release/net10.0-windows10.0.26100/win-x64/publish/SqlApp.exe`

#### [모든 플랫폼 (Skia)]
```bash
dotnet publish src/gui/SqlApp -f net10.0-desktop -c Release -r win-x64 --self-contained -p:PublishSingleFile=true     # Windows
dotnet publish src/gui/SqlApp -f net10.0-desktop -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true   # Linux
dotnet publish src/gui/SqlApp -f net10.0-desktop -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true   # macOS
```
* **배포 바이너리 출력**: `src/gui/SqlApp/bin/Release/net10.0-desktop/<RID>/publish/`

---

## 6. TEST 빌드 및 실행 방법

CTest 및 CMake 기반으로 4개 테스트 스위트(단위 테스트, 오류 거부, 계약 픽스처 검증, CLI 회귀)를 수행합니다.

#### [Developer PowerShell for VS]
```powershell
# 1. 전체 타깃 일괄 빌드 (테스트 바이너리 포함)
cmake -B build
cmake --build build --config Release

# 2. CTest 일괄 실행
ctest --test-dir build -C Release --output-on-failure
```

#### [Windows (일반 PowerShell / CMD)]
```powershell
# 빌드 및 CTest 일괄 실행 (MSVC 원라이너)
cmd.exe /c 'call "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat" && cmake -B build && cmake --build build --config Release && ctest --test-dir build -C Release --output-on-failure'
```
* *(참고: Visual Studio 2022 환경인 경우 경로의 `18` 대신 `2022`로 지정)*

#### [Linux / macOS]
```bash
# 1. 테스트 바이너리 빌드
cmake -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build --config Release

# 2. CTest 일괄 실행 (또는 make test)
ctest --test-dir build -C Release --output-on-failure

# 개별 테스트 바이너리 직접 실행:
./build/postprocessor_unit_test  # 5단계 계단식 후처리기 단위 테스트 (17개 케이스)
./build/error_test               # 비정상 GGUF/JSON/CLI 입력 거부 안전성 테스트 (39개 케이스)
./build/fixture_test             # 계약 버전별 소형 픽스처 입출력 정답 검증 (53개 케이스)
```

---

## 7. 코드 스타일 및 포맷 검사

본 프로젝트는 C 언어와 C# 코드 모두 엄격한 자동 포맷팅 규칙을 준수합니다.

### 1) C 언어 코드 스타일 ([.clang-format](.clang-format))
* **규격**: LLVM 스타일 기반, 들여쓰기 4칸, 연산자 공백 표준화.

#### [Developer PowerShell for VS]
```powershell
# 1. C 및 헤더 파일 포맷 자동 적용 (src, tests 전체)
Get-ChildItem -Recurse -Include *.c,*.h src,tests | ForEach-Object { clang-format -i $_.FullName }

# 2. CI 서식 일치 여부 검사 (dry-run 검사)
Get-ChildItem -Recurse -Include *.c,*.h src,tests | ForEach-Object { clang-format --dry-run --Werror $_.FullName }
```

#### [Windows (일반 PowerShell / CMD)]
```powershell
# 1. Visual Studio 내장 clang-format 경로 호출로 포맷 자동 적용
$clang = "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\Llvm\x64\bin\clang-format.exe"
Get-ChildItem -Recurse -Include *.c,*.h src,tests | ForEach-Object { & $clang -i $_.FullName }

# 2. CI 서식 일치 여부 검사 (dry-run 검사)
Get-ChildItem -Recurse -Include *.c,*.h src,tests | ForEach-Object { & $clang --dry-run --Werror $_.FullName }
```
* *(참고: Visual Studio 2022 환경인 경우 경로의 `18` 대신 `2022`로 지정)*

#### [Linux / macOS]
```bash
# 1. C 및 헤더 파일 포맷 자동 적용 (src, tests 전체)
find src tests -type f \( -name "*.c" -o -name "*.h" \) -exec clang-format -i {} +

# 2. CI 서식 일치 여부 검사 (dry-run 검사)
find src tests -type f \( -name "*.c" -o -name "*.h" \) -exec clang-format --dry-run --Werror {} +
```

### 2) C# / .NET 코드 스타일 ([.editorconfig](.editorconfig))
* **규격**: .NET 10 / C# 14 공식 코딩 컨벤션, 파일 범위 네임스페이스(`namespace SqlApp;`), Nullable 활성화.

#### [전 플랫폼 공통]
```powershell
# 1. C# 코드 서식 자동 교정
dotnet format src/gui/SqlApp.sln

# 2. CI 서식 일치 여부 검사 (변경 사항 발생 시 빌드 실패 처리)
dotnet format src/gui/SqlApp.sln --verify-no-changes
```

