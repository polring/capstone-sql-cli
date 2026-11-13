using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
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
        private readonly Func<PipelineStage, string, string, int, Task<InferenceResult>> _runStage;
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

        [ObservableProperty]
        public partial bool IsGenerating { get; set; }

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
                return "이 구간에 필요한 shop.db가 에셋 폴더에 없습니다.";
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
            var bundle = _allBundles.FirstOrDefault(b => b.IsDual == dual);
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
            var bundle = AssetDiscoveryService.AddFolder(_allBundles, folderPath);
            if (bundle == null || bundle.IsDual != IsDualMode)
            {
                StatusMessage = IsDualMode ? "front.gguf, back.gguf 또는 shop.db가 있는 이중 모델 폴더를 선택하세요."
                    : "단일 .gguf 모델과 .db가 있는 단일 모델 폴더를 선택하세요.";
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
            Func<PipelineStage, string, string, int, Task<InferenceResult>>? runStage = null)
        {
            _allBundles = bundles;
            _sqlExecutionService = new SqlExecutionService();
            _inferenceService = inference;
            _runStage = runStage ?? inference.RunStageAsync;
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
            var session = new ConversationSession
            {
                Title = $"새 대화 {Sessions.Count + 1}",
                Bundle = _allBundles.FirstOrDefault(b => !b.IsDual),
                MaxTokens = 64
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
            }
            finally
            {
                if (CurrentSessionBundle == bundle) IsLoadingSchema = false;
            }
        }

        [RelayCommand]
        public void UsePromptSuggestion(string? suggestion)
        {
            if (IsSingleMode && !string.IsNullOrWhiteSpace(suggestion))
            {
                InputText = suggestion;
            }
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
            if (dual && GetRangeUnavailableReason(CurrentSessionBundle, range) is { } reason)
            {
                StatusMessage = reason;
                return;
            }
            var bundle = dual ? CurrentSessionBundle : GetAvailableSessionBundle();
            if (bundle == null) return;

            string chatInput = InputText;
            int maxTokens = session.MaxTokens;
            ClearInputs();

            // Auto-rename session title on first message
            if (session.Messages.Count == 0 || session.Title.StartsWith("새 대화", StringComparison.Ordinal))
            {
                session.Title = chatInput.Length > 20 ? chatInput[..20] + "..." : chatInput;
            }

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
                    IReadOnlyList<TableSchemaInfo> schema = Array.Empty<TableSchemaInfo>();
                    if (range.Contains(PipelineStage.Mapper) || range.Contains(PipelineStage.Postprocessor))
                        schema = await _sqlExecutionService.GetDatabaseSchemaAsync(bundle.DbPath);

                    var run = await PipelineRunner.RunAsync(range, chatInput, "", bundle.DirectoryPath, bundle.DbPath, schema,
                        (stage, input) => _runStage(stage,
                            stage == PipelineStage.Front ? bundle.FrontModelPath : bundle.BackModelPath, input, maxTokens),
                        session.Id, assistantMsg.Id);
                    assistantMsg.PipelineRun = run;
                    assistantMsg.ComputeTimeMs = run.ComputeTimeMs;
                    assistantMsg.Sql = run.FinalSql;
                    assistantMsg.StopReason = run.Steps.LastOrDefault()?.StopReason;
                    if (run.Error.Length > 0) assistantMsg.ErrorMessage = run.Error;
                }
                else
                {
                    var result = await _inferenceService.RunQueryAsync(bundle.ModelPath, chatInput, maxTokens);
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
            if (session == null || session.IsDualMode || string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return;
            }

            try
            {
                string json = await File.ReadAllTextAsync(filePath);
                using var doc = JsonDocument.Parse(json);
                if (session != SelectedSession || session.IsDualMode) return;

                var root = doc.RootElement;
                IEnumerable<JsonElement> rows = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray() : new[] { root };
                string[] keys = ["question", "sql"];
                foreach (var row in rows.Where(e => e.ValueKind == JsonValueKind.Object))
                {
                    foreach (var key in keys)
                    {
                        if (!row.TryGetProperty(key, out var field) || field.ValueKind != JsonValueKind.String ||
                            string.IsNullOrWhiteSpace(field.GetString())) continue;
                        InputText = field.GetString()!;
                        StatusMessage = "JSON 파일에서 첫 번째 입력을 불러왔습니다.";
                        return;
                    }
                }
                StatusMessage = $"JSON 파일에 {string.Join(", ", keys)} 입력이 없습니다.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"JSON 읽기 오류: {ex.Message}";
            }
        }
    }
}
