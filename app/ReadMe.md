# GUI 코드 안내

`SqlApp.csproj`는 Windows WinUI와 Uno 데스크톱 앱의 공통 프로젝트입니다. 저장소 루트에서 네이티브 엔진을 먼저 빌드한 뒤 운영체제에 맞는 GUI를 실행합니다. [Windows 빌드·실행·배포](../README.md#3-windows-빌드-실행-및-배포) · [macOS 빌드·실행](../README.md#4-macos-빌드-및-실행)

## 앱 소스 구조

| 영역 | 소스 위치와 책임 |
| --- | --- |
| 앱 시작과 공통 리소스 | Uno 데스크톱 호스트의 시작점은 `Platforms/Desktop/Program.cs`입니다. `App.xaml.cs`가 창을 초기화하고 `MainPage`를 엽니다. 페이지와 컨트롤이 공유하는 테마별 브러시는 `App.xaml`에 정의합니다. [데스크톱 시작점](Platforms/Desktop/Program.cs) · [앱 초기화](App.xaml.cs) · [공통 리소스](App.xaml) |
| 화면 구성 | `MainPage.xaml`은 메인 페이지의 레이아웃과 바인딩을, `MainPage.xaml.cs`는 페이지 이벤트를 담당합니다. 구간 선택 컨트롤은 `Controls/StageRangeSlider.xaml`과 같은 이름의 코드 비하인드에 모여 있습니다. [메인 페이지](MainPage.xaml) · [구간 선택 컨트롤](Controls/StageRangeSlider.xaml) · [바인딩 변환기](Converters/ValueConverters.cs) |
| 화면 상태와 데이터 | `MainViewModel.cs`가 상태와 명령을 관리하고 `MainViewModel.Platform.cs`의 기본 생성자가 실제 서비스를 연결합니다. 대화·메시지, 매핑 후보, 파이프라인 실행 결과의 자료형은 `Models/`에 있습니다. [뷰모델](ViewModels/MainViewModel.cs) · [서비스 연결](ViewModels/MainViewModel.Platform.cs) · [데이터 모델](Models/) |
| 서비스와 파이프라인 | 에셋 탐색, 대화 저장, SQLite 접근, 모델 추론은 `Services/`에 각각 분리되어 있습니다. `Services/Pipeline/`에는 이중 모델의 단계 순서를 제어하는 `PipelineRunner.cs`, 매핑과 복원을 담당하는 `Mapper.cs`와 `Postprocessor.cs`, 공통 태그 규칙을 정의하는 `SchemaTags.cs`가 있습니다. [서비스](Services/) · [파이프라인](Services/Pipeline/) |
| C 추론기 연동 | `Interop/SqlEngine/`에서 `GgufContract.cs`는 GGUF 메타데이터를 읽고 `SqlTokenizer.cs`는 문자열과 토큰 ID를 변환합니다. `SqlModel.cs`는 C API를 호출하고 네이티브 모델 핸들의 수명을 관리합니다. [C# 연동 코드](Interop/SqlEngine/) · [C API 선언](../native/sql_engine/sql_engine.h) |

## 질문을 보낼 때

전송 버튼과 Enter는 모두 `MainViewModel.SendMessageAsync`를 호출합니다. 단일 모드에서는 직접 입력하거나 **JSON 파일 가져오기**로 채운 질문을 `InferenceService.RunQueryAsync`로 보냅니다. 이중 모드에서는 전송 버튼 왼쪽의 아래쪽 꺾쇠를 눌러 슬라이더를 엽니다. 메뉴 안의 **›**를 누르면 다섯 구간이 표시되고, 하나를 고르면 슬라이더로 돌아옵니다. `PipelineRunner.RunAsync`는 현재 채팅창에 입력한 원문 질문(앞모델 시작) 또는 일반화 질문(뒷모델 단독)을 받아 실행합니다. [화면 이벤트](MainPage.xaml.cs) · [구간 선택 컨트롤](Controls/StageRangeSlider.xaml.cs) · [구간 실행](Services/Pipeline/PipelineRunner.cs)

| 처리 | 구현과 입출력 |
| --- | --- |
| 에셋 선택 | `AssetDiscoveryService`가 자동 탐색 또는 폴더 탐색으로 단일·이중 모델 폴더를 식별합니다. 응답 메시지에는 실행 당시 폴더와 DB 경로를 남깁니다. [에셋 탐색](Services/AssetDiscoveryService.cs) |
| 모델 추론 | 단일·앞·뒤 역할별로 토크나이저와 네이티브 모델 핸들을 보관합니다. C#이 GGUF 메타데이터로 입력을 토큰화하고 프롬프트 ID와 생성 조건을 한 번 전달합니다. C는 요청별 KV 캐시로 생성한 ID, 개수, 종료 사유, 계산 시간을 반환합니다. C#이 실제 생성된 개수만 디코딩합니다. [모델 관리](Services/InferenceService.cs) · [토큰 생성 호출](Services/TokenGeneration.cs) · [네이티브 바인딩](Interop/SqlEngine/SqlModel.cs) |
| 매핑 | `Mapper.MapCandidates`는 원문과 앞모델의 태그 문장을 대조합니다. 후보마다 `Tables`에 실제 테이블명, `Columns`에 컬럼명과 유형, `Values`에 각 값 태그에 대응하는 원문 문자열을 저장합니다. 모든 값 태그가 같은 문자열 조합을 가리키면 후보 하나로 합칩니다. 뒷모델 입력은 모든 후보가 공유합니다. [매퍼](Services/Pipeline/Mapper.cs) · [복원 기록](Models/MappingRecord.cs) |
| SQL 복원 | 뒷모델이 한 번 생성한 태그 SQL을 `Postprocessor.RestoreCandidates`가 각 복원 기록에 적용합니다. 최종 SQL 문자열이 같으면 한 항목의 `MappingIndices`에 해당 복원 기록의 인덱스를 모읍니다. 인덱스는 0부터, 화면의 후보 번호는 1부터 셉니다. 복원 실패 사유도 해당 후보에 남깁니다. [후처리기](Services/Pipeline/Postprocessor.cs) · [실행 결과 자료구조](Models/PipelineRun.cs) |

모델이 생성 한도나 문맥 길이에 도달하면 단일 모드는 생성된 텍스트와 종료 사유를 채팅에 표시합니다. 이중 모드는 해당 단계에 부분 출력을 남기고 다음 단계로의 전달을 멈춥니다. 모델 계산 시간은 단계별로 기록하고 응답에는 그 합계를 표시합니다. [GGUF 계약과 시간 측정](../CONTRACT.md)

## SQL 카드와 조회 결과

`ChatMessage.SqlCandidates`가 단일 모델 출력과 이중 모델의 최종 후보를 같은 형식의 카드로 제공합니다. 각 SQL 오른쪽 끝에는 검사 상태 아이콘과 **▶** 실행 버튼이 있습니다. 아이콘은 시계(검사 중), ✓(실행 가능), ✕(실행 불가)로 바뀝니다. 복원 또는 검사에 실패하면 해당 카드에 사유가 표시됩니다. [SQL 카드 바인딩](MainPage.xaml) · [후보 상태](Models/ChatSqlCandidate.cs)

`SqlExecutionService`는 응답에 기록된 DB를 읽기 전용으로 열고 `sqlite3_prepare_v2`로 각 SQL을 검사합니다. 결과 열이 있는 조회문 한 개여야 하고 미지정 매개변수가 없어야 합니다. **▶**를 누르면 다시 검사하고 최대 100행을 읽습니다. 같은 후보를 다시 실행하면 그 후보의 결과만 갱신합니다. [SQL 검사와 실행](Services/SqlExecutionService.cs) · [후보별 실행 명령](ViewModels/MainViewModel.cs)

## 대화 저장

대화는 운영체제의 `LocalApplicationData` 아래 `capstone_sql_gui/sessions.json`에 저장합니다. Windows 경로는 `%LOCALAPPDATA%\capstone_sql_gui\sessions.json`입니다. 세션에는 모드·폴더·실행 구간·Max Tokens를, 응답에는 단계별 입출력·복원 후보·최종 SQL·조회 결과와의 연결을 저장합니다. 앱을 다시 열면 원래 DB 경로로 실행 가능 여부를 새로 검사합니다. [대화 저장·복원](Services/SessionStorageService.cs)

매퍼의 복원 기록 JSON에는 대화의 `ConversationId`, 매퍼를 실행한 응답의 `MessageId`, 후보 배열 `Candidates`를 저장합니다. 응답의 **단계별 입력·출력**에서 이 기록을 확인할 수 있습니다. 전체 구간을 실행하면 후처리기가 같은 실행에서 만들어진 매핑 후보와 태그 SQL을 받아 후보별로 실제 SQL을 복원합니다.
