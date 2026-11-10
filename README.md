# Capstone SQL GUI

영어 질문을 로컬 GGUF 모델로 SQL로 변환하는 데스크톱 앱입니다. **에셋 폴더**에서 모델과 SQLite DB를 함께 선택합니다. 생성된 SQL의 **실행**을 누르면 같은 폴더의 DB에서 조회 결과를 볼 수 있습니다.

## 🛠️ 기술 스택 및 실행 환경

| 구분 | Windows x64 | macOS |
| --- | --- | --- |
| GUI | Windows 11 · WinUI 3 | Uno Platform · Skia Desktop |
| GUI 빌드 | .NET 10 SDK, Windows SDK 10.0.26100 | Mac CPU에 맞는 .NET 10 SDK |
| C 추론기 빌드 | Visual Studio의 **C++를 사용한 데스크톱 개발** 워크로드 및 CMake 도구 | Xcode Command Line Tools의 Apple Clang, CMake 3.16 이상 |
| 실행 시 사용하는 추론 라이브러리 | `sql_engine.dll` | `libsql_engine.dylib` |

아래 명령은 모두 `SqlApp.sln`이 있는 **저장소 루트**에서 실행합니다. [Windows 빌드, 실행 및 배포](#3-windows-빌드-실행-및-배포) · [macOS 빌드 및 실행](#4-macos-빌드-및-실행)

## 1. 프로젝트 구조

`SqlApp.sln`은 GUI 솔루션입니다. `app/SqlApp.csproj`에서 C# 빌드 옵션과 SQLite 패키지 버전을 관리합니다. `Services/`는 모델 호출, 세션 저장, SQLite 조회를 맡고, `Interop/SqlEngine/`은 C 추론 API를 호출합니다.

`native/sql_engine/`에는 GGUF 추론기와 C 테스트가 있습니다. CMake는 빌드한 공유 라이브러리를 플랫폼별 `app/runtimes/<RID>/native/`에 복사합니다. `scripts/`는 릴리스 파일을 다운로드하고 검증하며, `assets/`에는 받은 모델과 DB가 태그별로 저장됩니다. `fixtures/v2/`에는 추론 계약 검증용 모델과 기대값이 있습니다.

```text
capstone-sql-cli/
├── SqlApp.sln
├── app/
│   ├── SqlApp.csproj
│   ├── Services/
│   ├── Interop/SqlEngine/
│   └── runtimes/<RID>/native/
├── native/sql_engine/
├── assets/
├── fixtures/v2/
└── scripts/
```

GUI의 호출 흐름은 [GUI 코드 안내](app/ReadMe.md)에, 모델의 입력 형식·연산·종료 조건은 [GGUF 계약](CONTRACT.md)에 정리되어 있습니다.

## 2. 모델과 DB 받기

같은 릴리스의 `model.gguf`와 `shop.db`를 `assets/<태그>/`에 함께 저장합니다. 기본값은 현재 추론기와 호환되는 가장 높은 단계의 릴리스입니다. 같은 단계에서는 정식 태그를 우선합니다. 두 파일의 SHA-256이 모두 일치하면 저장합니다. [학습 저장소 릴리스](https://github.com/polring/capstone-design-1/releases)

앱은 시작할 때 현재 작업 폴더와 앱 실행 폴더에서 상위 폴더를 따라 `assets/`를 찾습니다. **에셋 폴더** 목록에는 바로 아래의 폴더 중 `.gguf`와 `.db`가 모두 들어 있는 폴더가 표시됩니다. 새 대화는 stage와 contract 번호가 높은 폴더를 우선하며, 두 번호가 같으면 정식 태그를 우선합니다.

다른 경로에 두 파일이 있다면 **폴더 탐색**으로 해당 폴더를 선택하세요. 그 폴더가 목록에 추가되고 모델과 DB가 현재 대화에 함께 적용됩니다. 앱을 다시 열면 저장된 대화의 폴더 경로를 확인해 복원합니다.

**Windows · Developer Command Prompt for Visual Studio**

```
bash scripts/fetch-asset
```

**macOS · 터미널**

```
bash scripts/fetch-asset
```

**특정 릴리스 받기 · Windows와 macOS 공통**

```
bash scripts/fetch-asset stage2-contract2
```

---

## 3. Windows 빌드, 실행 및 배포

### 1) 빌드 — Developer Command Prompt for Visual Studio

중단점에서 멈춰 변수 값을 확인하려면 Debug를, 배포용으로 최적화하려면 Release를 선택하세요. Developer Command Prompt에서는 원하는 구성의 `#` 설명 줄 아래에 있는 명령 세 줄만 복사해 저장소 루트에서 실행하세요.

```
# 디버그 빌드
cmake -S native -B build -A x64
cmake --build build --config Debug --clean-first
dotnet build app/SqlApp.csproj -f net10.0-windows10.0.26100 -c Debug

# 또는 릴리즈 빌드
cmake -S native -B build -A x64
cmake --build build --config Release --clean-first
dotnet build app/SqlApp.csproj -f net10.0-windows10.0.26100 -c Release
```

두 구성 모두 추론기 DLL을 `app/runtimes/win-x64/native/sql_engine.dll`에 복사합니다. `--clean-first`로 다시 빌드하면 구성을 바꿔도 앱에 해당 구성의 DLL이 포함됩니다.

### 2) 실행 — 빌드한 GUI 열기

저장소 루트에서 빌드한 구성에 맞는 명령 하나를 실행하세요.

```
# 디버그 빌드 실행
.\app\bin\Debug\net10.0-windows10.0.26100\SqlApp.exe

# 또는 릴리즈 빌드 실행
.\app\bin\Release\net10.0-windows10.0.26100\SqlApp.exe
```

새 대화에서 영어 질문을 입력하고, 생성된 SQL 아래의 **실행**을 눌러 DB를 읽기 전용으로 조회합니다. 다른 모델과 DB를 사용하려면 상단의 **에셋 폴더** 목록에서 바꾸거나 **폴더 탐색**에서 두 파일이 들어 있는 폴더를 여세요.

### 3) 배포 — Windows x64 ZIP 만들기

위의 릴리즈 빌드를 마친 뒤 .NET 런타임을 포함한 게시 폴더를 만듭니다.

```
dotnet publish app/SqlApp.csproj -f net10.0-windows10.0.26100 -c Release -r win-x64 --self-contained -o build/publish/win-x64
```

실행 파일과 DLL이 함께 배포되도록 게시 폴더 전체를 압축합니다.

```
tar.exe -a -cf build\SqlApp-win-x64.zip -C build\publish\win-x64 .
```

**배포 파일**은 `build/SqlApp-win-x64.zip`입니다. 받는 사람은 ZIP 전체를 한 폴더에 풀고, `SqlApp.exe` 옆의 `assets/<태그>/`에 모델과 DB를 함께 넣은 뒤 앱을 실행합니다. C 추론기에는 Visual C++ 런타임이 필요합니다. 대상 PC에 `VCRUNTIME140.dll`이 없다면 x64 재배포 패키지를 설치하세요. [Microsoft Visual C++ 재배포 패키지](https://learn.microsoft.com/ko-kr/cpp/windows/latest-supported-vc-redist)

---

## 4. macOS 빌드 및 실행

### 1) 빌드 — macOS 터미널

터미널에서 사용하는 아키텍처를 확인합니다. 출력이 `arm64`이면 ARM64용 .NET SDK를, `x86_64`이면 x64용 SDK를 사용하세요.

```
uname -m
```

Apple Clang으로 해당 아키텍처의 C 추론 라이브러리를 빌드합니다.

```
cmake -S native -B build -DCMAKE_BUILD_TYPE=Release -DCMAKE_OSX_ARCHITECTURES="$(uname -m)"
cmake --build build --config Release
```

이어서 Uno Skia 데스크톱 앱을 빌드합니다.

```
dotnet build app/SqlApp.csproj -f net10.0-desktop -c Release
```

| Mac CPU | .NET 런타임 식별자(RID) | CMake 빌드 결과 |
| --- | --- | --- |
| Apple Silicon | `osx-arm64` | `app/runtimes/osx-arm64/native/libsql_engine.dylib` |
| Intel | `osx-x64` | `app/runtimes/osx-x64/native/libsql_engine.dylib` |

### 2) 실행 — 빌드한 GUI 열기

앞에서 빌드한 Release 앱을 실행합니다.

```
dotnet run --project app/SqlApp.csproj -f net10.0-desktop -c Release --no-build --no-launch-profile
```

새 대화에서 영어 질문을 보내면 자동 선택된 모델이 생성한 SQL이 표시됩니다. **에셋 폴더** 목록이나 **폴더 탐색**에서 폴더를 바꾸면 모델과 DB가 함께 전환됩니다.

## 5. 테스트 실행

빌드가 끝나면 CTest로 네 가지 fixture 세트의 추론 결과와 오류 후 복구를 검증합니다.

**Windows · Developer Command Prompt for Visual Studio**

Debug로 빌드했다면 아래 명령에서 `-C Release`를 `-C Debug`로 바꾸세요.

```
cmake -E chdir build ctest -C Release --output-on-failure
```

**macOS · 터미널**

```
cmake -E chdir build ctest -C Release --output-on-failure
```

**GNU Make를 사용하는 경우**

추론 라이브러리를 빌드한 뒤 C 테스트를 실행합니다.

```
make engine
make test
```

C 코드 서식은 [clang-format 설정](.clang-format)을 따릅니다.
