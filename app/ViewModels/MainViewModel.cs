using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlApp.Models;
using SqlApp.Services;

namespace SqlApp.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly SqlExecutionService _sqlExecutionService;
        private readonly InferenceService _inferenceService;
        private readonly SessionStorageService _sessionStorageService;

        public ObservableCollection<ConversationSession> Sessions { get; } = new();

        [ObservableProperty]
        public partial ConversationSession? SelectedSession { get; set; }

        partial void OnSelectedSessionChanged(ConversationSession? value)
        {
            NotifyBundleProperties();
            _ = LoadSchemaAsync();
        }

        [ObservableProperty]
        public partial string InputText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool IsGenerating { get; set; }

        [ObservableProperty]
        public partial string StatusMessage { get; set; } = string.Empty;

        public ObservableCollection<AssetBundle> AvailableBundles { get; } = new(AssetDiscoveryService.DiscoverBundles());

        public AssetBundle? CurrentSessionBundle => SelectedSession?.Bundle;

        public void SelectAssetFolder(string folderPath)
        {
            var bundle = AssetDiscoveryService.AddFolder(AvailableBundles, folderPath);
            if (bundle == null)
            {
                StatusMessage = "선택한 폴더에 .gguf 모델과 .db 파일이 모두 있어야 합니다.";
                return;
            }
            SelectBundle(bundle);
        }

        public void SelectBundle(AssetBundle? bundle)
        {
            if (bundle == null || SelectedSession == null || !AvailableBundles.Contains(bundle)) return;
            SelectedSession.Bundle = bundle;
            StatusMessage = string.Empty;
            NotifyBundleProperties();
            _ = LoadSchemaAsync();
            _ = _sessionStorageService.SaveSessionsAsync(Sessions);
        }

        private void NotifyBundleProperties()
        {
            OnPropertyChanged(nameof(CurrentSessionBundle));
            OnPropertyChanged(nameof(SelectedModelFileName));
            OnPropertyChanged(nameof(SelectedDbFileName));
            OnPropertyChanged(nameof(SelectedModelPath));
            OnPropertyChanged(nameof(SelectedDbPath));
        }

        public string SelectedModelPath => CurrentSessionBundle?.ModelPath ?? string.Empty;
        public string SelectedDbPath => CurrentSessionBundle?.DbPath ?? string.Empty;
        public string SelectedModelFileName => SelectedModelPath.Length == 0 ? "(없음)" : Path.GetFileName(SelectedModelPath);
        public string SelectedDbFileName => SelectedDbPath.Length == 0 ? "(없음)" : Path.GetFileName(SelectedDbPath);

        public ObservableCollection<TableSchemaInfo> CurrentSchema { get; } = new();

        [ObservableProperty]
        public partial bool IsLoadingSchema { get; set; }

        public MainViewModel()
        {
            _sqlExecutionService = new SqlExecutionService();
            _inferenceService = new InferenceService();
            _sessionStorageService = new SessionStorageService();

            _ = LoadInitialSessionsAsync();
        }

        private async Task LoadInitialSessionsAsync()
        {
            var saved = await _sessionStorageService.LoadSessionsAsync(AvailableBundles);
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
                Bundle = AvailableBundles.FirstOrDefault(),
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
            var bundle = GetAvailableSessionBundle();
            if (bundle == null)
            {
                CurrentSchema.Clear();
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
            if (!string.IsNullOrWhiteSpace(suggestion))
            {
                InputText = suggestion;
            }
        }

        [RelayCommand]
        public async Task SendMessageAsync()
        {
            if (SelectedSession == null || string.IsNullOrWhiteSpace(InputText) || IsGenerating)
            {
                return;
            }

            var bundle = GetAvailableSessionBundle();
            if (bundle == null) return;

            string question = InputText;
            InputText = string.Empty;

            // Auto-rename session title on first message
            if (SelectedSession.Messages.Count == 0 || SelectedSession.Title.StartsWith("새 대화", StringComparison.Ordinal))
            {
                SelectedSession.Title = question.Length > 20 ? question[..20] + "..." : question;
            }

            // User Message
            var userMsg = new ChatMessage
            {
                Sender = MessageSender.User,
                Text = question
            };
            SelectedSession.Messages.Add(userMsg);

            // Assistant placeholder message
            var assistantMsg = new ChatMessage
            {
                Sender = MessageSender.Assistant
            };
            SelectedSession.Messages.Add(assistantMsg);

            IsGenerating = true;
            StatusMessage = "SQL 생성 중...";

            try
            {
                var sw = Stopwatch.StartNew();

                var result = await _inferenceService.RunQueryAsync(
                    bundle.ModelPath, question, SelectedSession.MaxTokens);
                assistantMsg.Sql = result.Sql;
                assistantMsg.StopReason = result.StopReason;
                assistantMsg.InferenceTimeMs = sw.ElapsedMilliseconds;

                StatusMessage = $"완료 ({assistantMsg.InferenceTimeMs}ms)";
            }
            catch (Exception ex)
            {
                assistantMsg.ErrorMessage = $"오류 발생: {ex.Message}";
                StatusMessage = "추론 중 오류가 발생했습니다.";
            }
            finally
            {
                IsGenerating = false;
                _ = _sessionStorageService.SaveSessionsAsync(Sessions);
            }
        }

        [RelayCommand]
        public async Task RunSqlAsync(ChatMessage? message)
        {
            if (message == null || string.IsNullOrWhiteSpace(message.Sql) || SelectedSession == null)
            {
                return;
            }

            var session = SelectedSession;
            var bundle = GetAvailableSessionBundle();
            if (bundle == null) return;

            message.IsRunningQuery = true;
            try
            {
                var result = await _sqlExecutionService.ExecuteQueryAsync(bundle.DbPath, message.Sql);
                ApplyQueryResult(session, message, result);
            }
            catch (Exception ex)
            {
                var errorResult = new QueryResultData
                {
                    ErrorMessage = $"실행 실패: {ex.Message}"
                };
                ApplyQueryResult(session, message, errorResult);
            }
            finally
            {
                message.IsRunningQuery = false;
                _ = _sessionStorageService.SaveSessionsAsync(Sessions);
            }
        }

        private static void ApplyQueryResult(ConversationSession session, ChatMessage message, QueryResultData result)
        {
            // 1. 이미 생성된 결과 말풍선이 있는지 확인 (ID 매칭 또는 바로 다음 메시지 확인)
            ChatMessage? existingResultMsg = null;
            if (message.ResultMessageId.HasValue)
            {
                existingResultMsg = session.Messages.FirstOrDefault(m => m.Id == message.ResultMessageId.Value);
            }

            if (existingResultMsg == null)
            {
                int currentIdx = session.Messages.IndexOf(message);
                if (currentIdx >= 0 && currentIdx + 1 < session.Messages.Count &&
                    session.Messages[currentIdx + 1].Sender == MessageSender.QueryResult)
                {
                    existingResultMsg = session.Messages[currentIdx + 1];
                    message.ResultMessageId = existingResultMsg.Id;
                }
            }

            // 2. 이미 존재하면 기존 말풍선에 결과만 갱신
            if (existingResultMsg != null)
            {
                existingResultMsg.QueryResult = result;
                existingResultMsg.IsQueryResultExpanded = true;
            }
            else
            {
                // 3. 존재하지 않으면 새 말풍선 생성 후 해당 모델 메시지 바로 아래에 삽입
                var newResultMsg = new ChatMessage
                {
                    Sender = MessageSender.QueryResult,
                    Sql = message.Sql,
                    QueryResult = result,
                    IsQueryResultExpanded = true
                };
                message.ResultMessageId = newResultMsg.Id;

                int index = session.Messages.IndexOf(message);
                if (index >= 0 && index + 1 <= session.Messages.Count)
                {
                    session.Messages.Insert(index + 1, newResultMsg);
                }
                else
                {
                    session.Messages.Add(newResultMsg);
                }
            }
        }

        [RelayCommand]
        public void CopySql(string? sql)
        {
            if (string.IsNullOrWhiteSpace(sql)) return;

            try
            {
                var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dataPackage.SetText(sql);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
                StatusMessage = "SQL이 클립보드에 복사되었습니다.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"복사 실패: {ex.Message}";
            }
        }

        [RelayCommand]
        public async Task ImportJsonFileAsync(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath) || SelectedSession == null)
            {
                return;
            }

            try
            {
                string json = await File.ReadAllTextAsync(filePath);
                using var doc = JsonDocument.Parse(json);

                var questions = new List<string>();

                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var elem in doc.RootElement.EnumerateArray())
                    {
                        if (elem.TryGetProperty("question", out var q) && !string.IsNullOrWhiteSpace(q.GetString()))
                        {
                            questions.Add(q.GetString()!);
                        }
                        else if (elem.TryGetProperty("sql", out var s) && !string.IsNullOrWhiteSpace(s.GetString()))
                        {
                            questions.Add(s.GetString()!);
                        }
                    }
                }
                else if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (doc.RootElement.TryGetProperty("question", out var q) && !string.IsNullOrWhiteSpace(q.GetString()))
                    {
                        questions.Add(q.GetString()!);
                    }
                    else if (doc.RootElement.TryGetProperty("sql", out var s) && !string.IsNullOrWhiteSpace(s.GetString()))
                    {
                        questions.Add(s.GetString()!);
                    }
                }

                if (questions.Count > 0)
                {
                    InputText = questions[0];
                    StatusMessage = $"JSON 파일에서 {questions.Count}개 항목 로드됨. 첫 번째 질문이 입력창에 배치되었습니다.";
                }
                else
                {
                    StatusMessage = "JSON 파일에서 question 또는 sql 키를 찾지 못했습니다.";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"JSON 읽기 오류: {ex.Message}";
            }
        }
    }
}
