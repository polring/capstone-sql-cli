using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlApp.Models;
using SqlApp.Services;
using SqlApp.Services.Pipeline;

namespace SqlApp.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly SqlExecutionService _sqlExecutionService;
        private readonly InferenceService _inferenceService;
        private readonly SessionStorageService _sessionStorageService;
        private readonly List<AssetBundle> _allBundles;
        private readonly Func<PipelineStage, string, string, int, Task<InferenceResult>>? _runStageOverride;
        private readonly Func<string, string, int, Task<InferenceResult>>? _runSingleOverride;
        private readonly int _batchWorkerCount;
        private CancellationTokenSource? _batchCancellation;
        private ConversationSession? _batchSession;
        internal Task Initialization { get; }
        private bool _refreshingBundleChoices;

        public ObservableCollection<ConversationSession> Sessions { get; } = new();

        [ObservableProperty]
        public partial ConversationSession? SelectedSession { get; set; }

        partial void OnSelectedSessionChanged(ConversationSession? value)
        {
            ClearInputs();
            if (value?.Bundle is { } previous)
                value.Bundle = _allBundles.FirstOrDefault(b => b.IsDual == value.IsDualMode &&
                    b.DirectoryPath.Equals(previous.DirectoryPath, AssetDiscoveryService.PathComparison));
            RefreshBundleChoices();
            _ = LoadSchemaAsync();
        }

        [ObservableProperty]
        public partial string InputText { get; set; } = string.Empty;

        [ObservableProperty, NotifyPropertyChangedFor(nameof(CanStartInference))]
        public partial bool IsGenerating { get; set; }
        public bool CanStartInference => !IsGenerating;

        private enum JsonBatchState { Idle, Preparing, Running, Stopping, Saving }

        [ObservableProperty, NotifyPropertyChangedFor(nameof(IsJsonBatchRunning)), NotifyPropertyChangedFor(nameof(IsStoppingJsonBatch)),
         NotifyPropertyChangedFor(nameof(CanCancelJsonBatch)), NotifyCanExecuteChangedFor(nameof(CancelJsonBatchCommand))]
        private partial JsonBatchState BatchState { get; set; }
        public bool IsJsonBatchRunning => BatchState != JsonBatchState.Idle;
        public bool IsStoppingJsonBatch => BatchState == JsonBatchState.Stopping;
        public bool CanCancelJsonBatch => BatchState is JsonBatchState.Preparing or JsonBatchState.Running;

        [RelayCommand(CanExecute = nameof(CanCancelJsonBatch))]
        public void CancelJsonBatch()
        {
            if (!CanCancelJsonBatch || _batchCancellation == null) return;
            if (SelectedSession == _batchSession)
                StatusMessage = BatchState == JsonBatchState.Preparing
                    ? "첫 질문을 시작하기 전에 중단합니다."
                    : "진행 중인 질문을 모두 마친 뒤 중단합니다.";
            BatchState = JsonBatchState.Stopping;
            _batchCancellation.Cancel();
        }

        [ObservableProperty]
        public partial string StatusMessage { get; set; } = string.Empty;

        public ObservableCollection<AssetBundle> AvailableBundles { get; } = new();

        public AssetBundle? CurrentSessionBundle => SelectedSession?.Bundle;
        public bool IsDualMode => SelectedSession?.IsDualMode == true;
        public bool IsSingleMode => !IsDualMode;
        public bool HasPreprocessor => CurrentSessionBundle?.HasPreprocessor == true;
        public StageRange SelectedRange => SelectedSession?.SelectedRange ?? new(PipelineStage.Front, PipelineStage.Postprocessor);
        public string PrimaryInputHint => !IsDualMode ? "질문을 입력하세요."
            : SelectedRange.Start == PipelineStage.Back ? "매퍼가 생성한 일반화 질문을 입력하세요."
            : "앞모델에 전달할 질문을 입력하세요.";

        public string WelcomeDescription => !IsDualMode
            ? "영어로 질문하면 모델이 SQL을 생성합니다. 실행을 누르면 같은 에셋 폴더의 DB에서 결과를 조회합니다."
            : SelectedRange switch
            {
                { Start: PipelineStage.Back } =>
                    "매퍼가 만든 일반화 질문을 입력하면 뒷모델이 태그 SQL을 생성합니다.",
                { End: PipelineStage.Front } =>
                    "영어로 질문하면 앞모델이 테이블명, 컬럼명, 조건값에 태그를 붙인 질문을 보여줍니다.",
                { End: PipelineStage.Mapper } =>
                    "영어로 질문하면 앞모델이 태그를 붙이고 매퍼가 테이블명과 컬럼명을 TABLE1, COL1 같은 기호로 바꾼 일반화 질문을 보여줍니다.",
                { End: PipelineStage.Back } =>
                    "영어로 질문하면 매퍼가 앞모델의 출력을 일반화하고, 뒷모델이 태그 SQL을 생성합니다.",
                _ => "영어로 질문하면 두 모델과 매퍼를 거쳐 후처리기가 최종 SQL을 복원합니다. 실행을 누르면 선택한 DB에서 결과를 조회합니다."
            };

        public IReadOnlyList<string> PromptSuggestions
        {
            get
            {
                if (IsSingleMode) return
                [
                    "please show me information of peter.",
                    "show me where peter lives in.",
                    "List all products with stock quantity 10 or less."
                ];
                if (SelectedRange.Start == PipelineStage.Back) return
                [
                    "How many <TABLE1> are there?",
                    "Show the {TABLE1.COL1, string} of each <TABLE1>.",
                    "Show the {TABLE1.COL1, string} of <TABLE1> where the <TABLE1.COL2, numeric> is greater than [TABLE1.COL2, numeric, #1]."
                ];

                var first = CurrentSchema.FirstOrDefault();
                string table = first?.TableName ?? "items";
                string column = first?.Columns.FirstOrDefault()?.Split(" (", 2)[0] ?? "item_name";
                string count = $"How many {table} are there?";
                string total = $"What is the total number of {CurrentSchema.LastOrDefault()?.TableName ?? table}?";

                // Use a numeric attribute from each table for MAX/MIN examples when available.
                var measures = CurrentSchema.Select(t => (t.TableName, Column: FindMeasureColumn(t)))
                    .Where(t => t.Column != null).ToArray();
                string highest = measures.Length > 0
                    ? $"Show the highest {measures[0].Column} of {measures[0].TableName}."
                    : $"List the {column} of all {table}.";
                string lowest = measures.Length > 0
                    ? $"Show the lowest {measures[^1].Column} of {measures[^1].TableName}."
                    : $"Show the {column} of each record in {table}.";
                return SelectedRange.End switch
                {
                    PipelineStage.Front => [count, total, highest],
                    PipelineStage.Mapper => [total, highest, lowest],
                    PipelineStage.Back => [count, lowest, total],
                    _ => [count, highest, lowest]
                };
            }
        }

        private static string? FindMeasureColumn(TableSchemaInfo table)
        {
            foreach (string descriptor in table.Columns)
            {
                string[] parts = descriptor.Split(" (", 2);
                string name = parts[0];
                if (parts.Length == 2 && !name.Equals("id", StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith("_id", StringComparison.OrdinalIgnoreCase) &&
                    Regex.IsMatch(parts[1], SchemaTags.NumericSqlType, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    return name;
            }
            return null;
        }

        private string? GetRangeUnavailableReason(AssetBundle? bundle, StageRange range)
        {
            if (bundle == null || !bundle.IsDual) return "이중 모델 에셋 폴더를 선택하세요.";
            foreach (var stage in new[] { PipelineStage.Front, PipelineStage.Back })
            {
                if (!range.Contains(stage)) continue;
                var path = stage == PipelineStage.Front ? bundle.FrontModelPath : bundle.BackModelPath;
                var reason = _inferenceService.GetStageUnavailableReason(stage, path);
                if (reason != null) return reason;
            }
            if ((range.Contains(PipelineStage.Mapper) || range.Contains(PipelineStage.Postprocessor)) && !File.Exists(bundle.DbPath))
                return "이 구간에 필요한 .db 파일이 에셋 폴더에 없습니다.";
            return null;
        }

        private void ClearInputs()
        {
            InputText = string.Empty;
            StatusMessage = string.Empty;
        }

        private void RefreshBundleChoices()
        {
            _refreshingBundleChoices = true;
            try
            {
                AvailableBundles.Clear();
                foreach (var bundle in _allBundles.Where(b => b.IsDual == IsDualMode))
                    AvailableBundles.Add(bundle);
            }
            finally { _refreshingBundleChoices = false; }
            NotifyBundleProperties();
        }

        public void SetDualMode(bool dual)
        {
            var session = SelectedSession;
            if (session == null || dual == session.IsDualMode) return;
            session.IsDualMode = dual;
            var bundle = AssetDiscoveryService.GetDefaultBundle(_allBundles, dual);
            session.Bundle = bundle;
            session.BundleDirectory = bundle?.DirectoryPath ?? "";
            session.SelectedRange = DefaultRange(bundle);
            ClearInputs();
            RefreshBundleChoices();
            _ = LoadSchemaAsync();
            _ = _sessionStorageService.SaveSessionsAsync(Sessions);
        }

        private static StageRange DefaultRange(AssetBundle? bundle) =>
            bundle is { IsDual: true, FrontModelPath.Length: 0, BackModelPath.Length: > 0 } ? new(PipelineStage.Back, PipelineStage.Back) :
            bundle is { IsDual: true, BackModelPath.Length: 0 } or { IsDual: true, DbPath.Length: 0 } ? new(PipelineStage.Front, PipelineStage.Front) :
            new(PipelineStage.Front, PipelineStage.Postprocessor);

        public void SelectRange(StageRange range)
        {
            if (SelectedSession is not { IsDualMode: true } session) return;
            var previous = session.SelectedRange;
            session.SelectedRange = range;
            if (session.SelectedRange == previous) return;
            NotifyRangeProperties();
            _ = _sessionStorageService.SaveSessionsAsync(Sessions);
        }

        public void SelectAssetFolder(string folderPath)
        {
            var bundle = AssetDiscoveryService.AddFolder(_allBundles, folderPath, IsDualMode);
            if (bundle == null)
            {
                StatusMessage = IsDualMode ? "앞·뒷모델 .gguf 파일이 있는 폴더나, 사용할 단계의 모델 또는 DB가 있는 폴더를 선택하세요."
                    : ".gguf 파일 하나와 .db 파일 하나가 있는 단일 모델 폴더를 선택하세요.";
                return;
            }
            RefreshBundleChoices();
            SelectBundle(bundle);
        }

        public void SelectBundle(AssetBundle? bundle)
        {
            if (_refreshingBundleChoices || bundle == null || SelectedSession == null ||
                bundle.IsDual != IsDualMode || !AvailableBundles.Contains(bundle)) return;
            SelectedSession.Bundle = bundle;
            SelectedSession.BundleDirectory = bundle.DirectoryPath;
            SelectedSession.SelectedRange = DefaultRange(bundle);
            ClearInputs();
            NotifyBundleProperties();
            _ = LoadSchemaAsync();
            _ = _sessionStorageService.SaveSessionsAsync(Sessions);
        }

        private void NotifyBundleProperties()
        {
            OnPropertyChanged(nameof(IsSingleMode));
            OnPropertyChanged(nameof(IsDualMode));
            OnPropertyChanged(nameof(HasPreprocessor));
            NotifyRangeProperties();
            OnPropertyChanged(nameof(CurrentSessionBundle));
            OnPropertyChanged(nameof(SelectedModelFileName));
            OnPropertyChanged(nameof(SelectedDbFileName));
            OnPropertyChanged(nameof(SelectedModelPath));
            OnPropertyChanged(nameof(SelectedDbPath));
        }

        private void NotifyRangeProperties()
        {
            OnPropertyChanged(nameof(SelectedRange));
            OnPropertyChanged(nameof(PrimaryInputHint));
            OnPropertyChanged(nameof(WelcomeDescription));
            OnPropertyChanged(nameof(PromptSuggestions));
        }

        private string ModelPaths(bool fileNames)
        {
            if (CurrentSessionBundle is not { } bundle) return "";
            string Format(string path) => fileNames ? Path.GetFileName(path) : path;
            if (!bundle.IsDual) return Format(bundle.ModelPath);
            var roles = new List<string>();
            if (bundle.FrontModelPath.Length > 0) roles.Add($"앞모델 · {Format(bundle.FrontModelPath)}");
            if (bundle.BackModelPath.Length > 0) roles.Add($"뒷모델 · {Format(bundle.BackModelPath)}");
            return string.Join(" / ", roles);
        }

        public string SelectedModelPath => ModelPaths(false);
        public string SelectedDbPath => CurrentSessionBundle?.DbPath ?? string.Empty;
        public string SelectedModelFileName => ModelPaths(true) is { Length: > 0 } name ? name : "(없음)";
        public string SelectedDbFileName => SelectedDbPath.Length == 0 ? "(없음)" : Path.GetFileName(SelectedDbPath);

        public ObservableCollection<TableSchemaInfo> CurrentSchema { get; } = new();

        [ObservableProperty]
        public partial bool IsLoadingSchema { get; set; }

        internal MainViewModel(List<AssetBundle> bundles, SessionStorageService sessionStorage, InferenceService inference,
            Func<PipelineStage, string, string, int, Task<InferenceResult>>? runStage = null,
            Func<string, string, int, Task<InferenceResult>>? runSingle = null, int? batchWorkerCount = null)
        {
            _allBundles = bundles;
            _sqlExecutionService = new SqlExecutionService();
            _inferenceService = inference;
            _runStageOverride = runStage;
            _runSingleOverride = runSingle;
            _batchWorkerCount = batchWorkerCount ?? Math.Min(Environment.ProcessorCount, 4);
            ArgumentOutOfRangeException.ThrowIfLessThan(_batchWorkerCount, 1, nameof(batchWorkerCount));
            _sessionStorageService = sessionStorage;
            Initialization = LoadInitialSessionsAsync();
        }

        private async Task LoadInitialSessionsAsync()
        {
            var saved = await _sessionStorageService.LoadSessionsAsync(_allBundles);
            if (saved.Count == 0)
            {
                CreateNewSession();
                return;
            }

            foreach (var session in saved)
            {
                Sessions.Add(session);
            }
            SelectedSession = Sessions[0];
        }

        [RelayCommand]
        public void CreateNewSession()
        {
            var bundle = AssetDiscoveryService.GetDefaultBundle(_allBundles, false);
            var session = new ConversationSession
            {
                Title = $"새 대화 {Sessions.Count + 1}",
                Bundle = bundle,
                BundleDirectory = bundle?.DirectoryPath ?? ""
            };

            Sessions.Insert(0, session);
            SelectedSession = session;
            _ = _sessionStorageService.SaveSessionsAsync(Sessions);
        }

        [RelayCommand]
        public void DeleteSession(ConversationSession? session)
        {
            if (session == null) return;

            int index = Sessions.IndexOf(session);
            Sessions.Remove(session);

            if (SelectedSession == session)
            {
                if (Sessions.Count > 0)
                {
                    SelectedSession = Sessions[Math.Max(0, index - 1)];
                }
                else
                {
                    CreateNewSession();
                }
            }
            _ = _sessionStorageService.SaveSessionsAsync(Sessions);
        }

        [RelayCommand]
        public void SelectSession(ConversationSession? session)
        {
            if (session == null) return;
            SelectedSession = session;
        }

        [RelayCommand]
        public void StartEditTitle(ConversationSession? session)
        {
            if (session != null)
            {
                session.IsEditingTitle = true;
            }
        }

        [RelayCommand]
        public void FinishEditTitle(ConversationSession? session)
        {
            if (session != null)
            {
                session.IsEditingTitle = false;
                _ = _sessionStorageService.SaveSessionsAsync(Sessions);
            }
        }

        private AssetBundle? GetAvailableSessionBundle()
        {
            var bundle = CurrentSessionBundle;
            if (IsDualMode)
            {
                if (GetRangeUnavailableReason(bundle, SelectedRange) is not { } reason) return bundle;
                StatusMessage = reason;
                return null;
            }
            if (bundle != null && File.Exists(bundle.ModelPath) && File.Exists(bundle.DbPath))
                return bundle;

            StatusMessage = "폴더 탐색에서 .gguf 모델과 .db 파일이 함께 있는 폴더를 선택해 주세요.";
            return null;
        }

        [RelayCommand]
        public async Task LoadSchemaAsync()
        {
            var bundle = CurrentSessionBundle;
            CurrentSchema.Clear();
            OnPropertyChanged(nameof(PromptSuggestions));
            if (bundle == null || !File.Exists(bundle.DbPath))
            {
                IsLoadingSchema = false;
                return;
            }

            IsLoadingSchema = true;
            try
            {
                var tables = await _sqlExecutionService.GetDatabaseSchemaAsync(bundle.DbPath);
                if (CurrentSessionBundle != bundle) return;
                CurrentSchema.Clear();
                foreach (var t in tables)
                {
                    CurrentSchema.Add(t);
                }
                OnPropertyChanged(nameof(PromptSuggestions));
            }
            finally
            {
                if (CurrentSessionBundle == bundle) IsLoadingSchema = false;
            }
        }

        [RelayCommand]
        public void UsePromptSuggestion(string? suggestion)
        {
            if (!string.IsNullOrWhiteSpace(suggestion))
            {
                InputText = suggestion;
            }
        }

        private async Task<IReadOnlyList<TableSchemaInfo>> GetPipelineSchemaAsync(AssetBundle bundle, StageRange range)
        {
            if (bundle.IsDual && (range.Contains(PipelineStage.Mapper) || range.Contains(PipelineStage.Postprocessor)))
                return await _sqlExecutionService.GetDatabaseSchemaAsync(bundle.DbPath);
            return [];
        }

        private Task<PipelineRun> RunPipelineAsync(InferenceService inference, AssetBundle bundle, StageRange range,
            string input, IReadOnlyList<TableSchemaInfo> schema, int maxTokens, string sessionId, Guid messageId)
        {
            var runStage = _runStageOverride ?? inference.RunStageAsync;
            return PipelineRunner.RunAsync(range, input, "", bundle.DirectoryPath, bundle.DbPath, schema,
                (stage, prompt) => runStage(stage,
                    stage == PipelineStage.Front ? bundle.FrontModelPath : bundle.BackModelPath, prompt, maxTokens),
                sessionId, messageId);
        }

        private Task<InferenceResult> RunSingleAsync(InferenceService inference, AssetBundle bundle, string input, int maxTokens)
        {
            var runSingle = _runSingleOverride ?? inference.RunQueryAsync;
            return runSingle(bundle.ModelPath, input, maxTokens);
        }

        private static void SetDefaultTitle(ConversationSession session, string title)
        {
            if (session.Messages.Count == 0 || session.Title.StartsWith("새 대화", StringComparison.Ordinal))
                session.Title = title.Length > 20 ? title[..20] + "..." : title;
        }

        [RelayCommand]
        public async Task SendMessageAsync()
        {
            var session = SelectedSession;
            if (session == null || string.IsNullOrWhiteSpace(InputText) || IsGenerating)
            {
                return;
            }

            bool dual = IsDualMode;
            var range = SelectedRange;
            var bundle = GetAvailableSessionBundle();
            if (bundle == null) return;

            string chatInput = InputText;
            int maxTokens = session.MaxTokens;
            ClearInputs();

            SetDefaultTitle(session, chatInput);

            // User Message
            var userMsg = new ChatMessage
            {
                Sender = MessageSender.User,
                Text = chatInput
            };
            session.Messages.Add(userMsg);

            // Assistant placeholder message
            var assistantMsg = new ChatMessage
            {
                Sender = MessageSender.Assistant,
                DbPath = bundle.DbPath,
                AssetDirectory = bundle.DirectoryPath
            };
            session.Messages.Add(assistantMsg);

            IsGenerating = true;
            StatusMessage = dual ? "선택한 구간 실행 중..." : "SQL 생성 중...";

            try
            {
                if (dual)
                {
                    var schema = await GetPipelineSchemaAsync(bundle, range);
                    var run = await RunPipelineAsync(_inferenceService, bundle, range, chatInput, schema, maxTokens, session.Id, assistantMsg.Id);
                    assistantMsg.PipelineRun = run;
                    assistantMsg.ComputeTimeMs = run.ComputeTimeMs;
                    assistantMsg.Sql = run.FinalSql;
                    assistantMsg.StopReason = run.Steps.LastOrDefault()?.StopReason;
                    if (run.Error.Length > 0) assistantMsg.ErrorMessage = run.Error;
                }
                else
                {
                    var result = await RunSingleAsync(_inferenceService, bundle, chatInput, maxTokens);
                    assistantMsg.ComputeTimeMs = result.ComputeTimeMs;
                    assistantMsg.Sql = result.Text;
                    assistantMsg.StopReason = result.StopReason;
                }
                await _sqlExecutionService.ValidateMessageAsync(assistantMsg);
            }
            catch (Exception ex)
            {
                assistantMsg.ErrorMessage = $"오류 발생: {ex.Message}";
                if (dual)
                    assistantMsg.PipelineRun = new PipelineRun
                    {
                        Input = chatInput, Range = range,
                        BundleDirectory = bundle.DirectoryPath, DbPath = bundle.DbPath, Error = ex.Message
                    };
            }
            finally
            {
                IsGenerating = false;
                if (SelectedSession == session && CurrentSessionBundle == bundle)
                {
                    if (assistantMsg.HasError) StatusMessage = assistantMsg.ErrorMessage!;
                    else if (assistantMsg.HasGenerationWarning) StatusMessage = assistantMsg.GenerationWarning;
                    else if (assistantMsg.HasSqlValidationError) StatusMessage = assistantMsg.SqlValidationError!;
                    else StatusMessage = string.Empty;
                }
                _ = _sessionStorageService.SaveSessionsAsync(Sessions);
            }
        }

        [RelayCommand]
        public Task RunSqlAsync(ChatMessage? message) =>
            message?.SqlCandidates is { Count: 1 } candidates ? RunCandidateAsync(candidates[0]) : Task.CompletedTask;

        [RelayCommand(AllowConcurrentExecutions = true)]
        public async Task RunCandidateAsync(ChatSqlCandidate? candidate)
        {
            var session = SelectedSession;
            if (candidate == null || !candidate.CanExecuteSql || session == null) return;
            var message = candidate.Message;
            if (!session.Messages.Contains(message) || !message.SqlCandidates.Contains(candidate)) return;

            candidate.IsRunning = true;
            message.IsRunningQuery = true;
            try
            {
                var result = await _sqlExecutionService.ExecuteQueryAsync(message.DbPath, candidate.Sql);
                if (result.HasError)
                    await _sqlExecutionService.ValidateMessageAsync(message);
                ApplyQueryResult(session, candidate, result);
            }
            catch (Exception ex)
            {
                var errorResult = new QueryResultData
                {
                    ErrorMessage = $"실행 실패: {ex.Message}"
                };
                ApplyQueryResult(session, candidate, errorResult);
            }
            finally
            {
                candidate.IsRunning = false;
                message.IsRunningQuery = message.SqlCandidates.Any(c => c.IsRunning);
                message.NotifySqlValidation();
                _ = _sessionStorageService.SaveSessionsAsync(Sessions);
            }
        }

        private static void ApplyQueryResult(ConversationSession session, ChatSqlCandidate candidate, QueryResultData result)
        {
            var message = candidate.Message;
            var existing = session.Messages.FirstOrDefault(m => m.IsQueryResultSender &&
                m.SourceMessageId == message.Id && m.Sql == candidate.Sql);
            int index = session.Messages.IndexOf(message);
            if (index < 0) return;
            bool single = message.SqlCandidates.Count == 1;
            if (existing == null && single)
            {
                // Older sessions linked one result by ID or placed it immediately after the SQL.
                existing = session.Messages.FirstOrDefault(m => m.IsQueryResultSender && m.Id == message.ResultMessageId);
                if (existing == null && index + 1 < session.Messages.Count &&
                    session.Messages[index + 1] is { IsQueryResultSender: true, SourceMessageId: null } adjacent)
                    existing = adjacent;
            }
            if (existing != null)
            {
                existing.SourceMessageId = message.Id;
                existing.QueryResult = result;
                existing.IsQueryResultExpanded = true;
                if (single) message.ResultMessageId = existing.Id;
                return;
            }

            var resultMessage = new ChatMessage
            {
                Sender = MessageSender.QueryResult,
                SourceMessageId = message.Id,
                Text = candidate.Label,
                Sql = candidate.Sql,
                AssetDirectory = message.AssetDirectory,
                DbPath = message.DbPath,
                QueryResult = result
            };
            if (single) message.ResultMessageId = resultMessage.Id;
            while (++index < session.Messages.Count && session.Messages[index].SourceMessageId == message.Id) { }
            session.Messages.Insert(index, resultMessage);
        }

        [RelayCommand]
        public async Task ImportJsonFileAsync(string filePath)
        {
            var session = SelectedSession;
            if (session == null || IsGenerating || string.IsNullOrWhiteSpace(filePath)) return;
            var bundle = GetAvailableSessionBundle();
            if (bundle == null) return;

            var range = session.SelectedRange;
            int maxTokens = session.MaxTokens;
            string fileName = Path.GetFileName(filePath);
            SetDefaultTitle(session, fileName);
            session.Messages.Add(new ChatMessage { Sender = MessageSender.User, Text = $"JSON 첨부 · {fileName}" });
            var summary = new ChatMessage { Sender = MessageSender.Assistant };
            using var cancellation = new CancellationTokenSource();
            var additionalWorkers = new List<InferenceService>();
            _batchCancellation = cancellation;
            _batchSession = session;
            IsGenerating = true;
            BatchState = JsonBatchState.Preparing;
            StatusMessage = $"{fileName} 처리 중…";
            try
            {
                var schema = await GetPipelineSchemaAsync(bundle, range);
                // Each service serializes its own model handle. Keep the first worker's warmed models for reuse.
                for (int i = 1; i < _batchWorkerCount; i++)
                    additionalWorkers.Add(new InferenceService());
                var workers = new List<Func<string, Task<IReadOnlyList<string>>>>(_batchWorkerCount);
                string sessionId = session.Id;
                foreach (var worker in additionalWorkers.Prepend(_inferenceService))
                    workers.Add(input => InferBatchRowAsync(worker, bundle, range, input, schema, maxTokens, sessionId));
                var result = await JsonBatchService.ProcessAsync(filePath, workers, cancellation.Token,
                    (done, total) =>
                    {
                        if (done == total) BatchState = JsonBatchState.Saving;
                        else if (BatchState == JsonBatchState.Preparing) BatchState = JsonBatchState.Running;
                        if (SelectedSession == session && !IsStoppingJsonBatch)
                            StatusMessage = $"{fileName} · {done}/{total}개 처리";
                    });
                string prefix = result.IsCancelled
                    ? $"전체 {result.Total}개 중 {result.Processed}개 처리 후 중단했습니다. "
                    : $"전체 {result.Total}개 중 ";
                string counts = result.IsEvaluation
                    ? $"{result.Matched}개가 일치했습니다."
                    : $"{result.Succeeded}개의 output을 저장했습니다.";
                if (result.Succeeded < result.Processed)
                    counts += $" 추론 실패 {result.Processed - result.Succeeded}개.";
                summary.Text = result.Processed == 0
                    ? prefix.TrimEnd()
                    : $"{prefix}{counts}\n{result.Path}";
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                summary.ErrorMessage = $"JSON 처리 실패 · {ex.Message}";
            }
            finally
            {
                foreach (var worker in additionalWorkers) worker.Dispose();
                _batchCancellation = null;
                _batchSession = null;
                BatchState = JsonBatchState.Idle;
                session.Messages.Add(summary);
                if (SelectedSession == session) StatusMessage = summary.ErrorMessage ?? string.Empty;
                IsGenerating = false;
                await _sessionStorageService.SaveSessionsAsync(Sessions);
            }
        }

        private async Task<IReadOnlyList<string>> InferBatchRowAsync(InferenceService inference, AssetBundle bundle,
            StageRange range, string input, IReadOnlyList<TableSchemaInfo> schema, int maxTokens, string sessionId)
        {
            if (!bundle.IsDual)
            {
                var single = await RunSingleAsync(inference, bundle, input, maxTokens).ConfigureAwait(false);
                return [single.Text];
            }

            var run = await RunPipelineAsync(inference, bundle, range, input, schema, maxTokens, sessionId, Guid.NewGuid())
                .ConfigureAwait(false);
            if (run.Error.Length > 0) throw new InvalidOperationException(run.Error);
            if (range.End != PipelineStage.Postprocessor) return [run.Steps[^1].Output];
            return run.FinalSqlCandidates.Where(c => c.Error.Length == 0).Select(c => c.Sql).ToArray();
        }
    }
}
