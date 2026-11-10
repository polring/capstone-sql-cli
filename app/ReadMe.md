# GUI 코드 안내

`SqlApp.csproj`는 Windows WinUI와 Uno 데스크톱 앱의 공통 프로젝트입니다. 저장소 루트에서 네이티브 엔진을 먼저 빌드하고, 해당 운영체제의 절차에 따라 GUI를 실행하거나 게시합니다. [Windows 빌드·실행·배포](../README.md#3-windows-빌드-실행-및-배포) · [macOS 빌드·실행·배포](../README.md#4-macos-빌드-실행-및-배포)

## 질문에서 조회 결과까지

1. `MainPage.xaml`과 `ViewModels/MainViewModel.cs`가 사용자의 질문, 선택한 모델 경로, 생성 토큰 한도를 `Services/InferenceService.cs`에 전달합니다.
2. `Interop/SqlEngine/SqlModel.cs`가 네이티브 엔진을 호출하고 반환된 길이만큼 생성 바이트를 읽습니다. 생성 SQL은 대화에 표시·저장됩니다.
3. 사용자가 실행을 누르면 `Services/SqlExecutionService.cs`가 화면의 SQL을 선택한 SQLite DB에서 실행합니다. 조회 결과나 SQLite 오류는 해당 대화에 표시됩니다.

## 파일 위치

`Models/`는 대화·메시지·조회 결과를 나타내는 C# 소스입니다. 앱은 `assets/` 바로 아래의 폴더에서 GGUF 모델과 DB를 찾습니다. **폴더 탐색**으로 다른 경로의 폴더도 **에셋 폴더** 목록에 추가할 수 있습니다. 폴더를 선택하면 그 안의 두 파일이 해당 대화에 함께 적용됩니다. `runtimes/<RID>/native/`에는 CMake가 빌드한 추론 라이브러리가 들어갑니다.
