namespace SqlApp.Models;

// Numeric values are persisted in existing sessions.json files.
public enum MessageSender { User = 0, Assistant = 1, QueryResult = 2 }

public sealed partial class ChatMessage : ObservableObject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public MessageSender Sender { get; set; }
    public string Text { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public Guid? ResultMessageId { get; set; }
    public Guid? SourceMessageId { get; set; }
    public string AssetDirectory { get; set; } = string.Empty;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanExecuteSql))]
    public partial string DbPath { get; set; } = string.Empty;
    public bool IsUser => Sender == MessageSender.User;
    public bool IsAssistant => Sender == MessageSender.Assistant;
    public bool IsQueryResultSender => Sender == MessageSender.QueryResult;
    public string QueryResultTitle => Text.Length == 0 ? "DB 쿼리 실행 결과" : $"{Text} · DB 쿼리 실행 결과";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSql)), NotifyPropertyChangedFor(nameof(IsWaitingResponse)),
     NotifyPropertyChangedFor(nameof(IsSqlCandidate)), NotifyPropertyChangedFor(nameof(CanExecuteSql)),
     NotifyPropertyChangedFor(nameof(GenerationWarning))]
    public partial string Sql { get; set; } = string.Empty;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsWaitingResponse)),
     NotifyPropertyChangedFor(nameof(HasGenerationWarning)), NotifyPropertyChangedFor(nameof(GenerationWarning))]
    public partial string? StopReason { get; set; }

    private IReadOnlyList<ChatSqlCandidate>? _sqlCandidates;
    public IReadOnlyList<ChatSqlCandidate> SqlCandidates => _sqlCandidates ??= CreateCandidates();
    public bool HasSqlCandidates => SqlCandidates.Count > 0;

    private IReadOnlyList<ChatSqlCandidate> CreateCandidates()
    {
        if (!IsAssistant || HasError) return [];
        if (PipelineRun == null)
            return HasSql ? [new ChatSqlCandidate(this, Sql, "")] : [];
        if (PipelineRun.Range.End != PipelineStage.Postprocessor || PipelineRun.Error.Length > 0) return [];
        var restored = PipelineRun.FinalSqlCandidates;
        return restored.Select((candidate, index) => new ChatSqlCandidate(this, candidate.Sql,
            restored.Count == 1 && candidate.MappingIndices.Count <= 1 ? "" :
            "후보 " + FormatMappingNumbers(candidate.MappingIndices, index), candidate.Error)).ToArray();
    }

    private static string FormatMappingNumbers(IReadOnlyList<int> indices, int fallbackIndex)
    {
        if (indices.Count == 0) return (fallbackIndex + 1).ToString();
        var spans = new List<string>();
        for (int i = 0; i < indices.Count; i++)
        {
            int first = indices[i] + 1;
            while (i + 1 < indices.Count && indices[i + 1] == indices[i] + 1) i++;
            int last = indices[i] + 1;
            spans.Add(first == last ? first.ToString() : $"{first}–{last}");
        }
        return string.Join(", ", spans);
    }

    partial void OnSqlChanged(string value) => ResetCandidates();
    partial void OnDbPathChanged(string value) => ResetCandidates();
    partial void OnPipelineRunChanged(PipelineRun? value) => ResetCandidates();
    partial void OnErrorMessageChanged(string? value) => ResetCandidates();

    private void ResetCandidates()
    {
        _sqlCandidates = null;
        OnPropertyChanged(nameof(SqlCandidates));
        OnPropertyChanged(nameof(HasSqlCandidates));
        OnPropertyChanged(nameof(ShowPipelineOutput));
        NotifySqlValidation();
    }

    internal void NotifySqlValidation()
    {
        OnPropertyChanged(nameof(SqlValidationError));
        OnPropertyChanged(nameof(HasSqlValidationError));
        OnPropertyChanged(nameof(CanExecuteSql));
    }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError)), NotifyPropertyChangedFor(nameof(IsWaitingResponse)),
     NotifyPropertyChangedFor(nameof(IsSqlCandidate)), NotifyPropertyChangedFor(nameof(CanExecuteSql))]
    public partial string? ErrorMessage { get; set; }

    // Older sessions recorded the whole request in this field.
    [ObservableProperty, NotifyPropertyChangedFor(nameof(InferenceTimeLabel)), NotifyPropertyChangedFor(nameof(HasInferenceTime))]
    public partial long InferenceTimeMs { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(InferenceTimeLabel)), NotifyPropertyChangedFor(nameof(HasInferenceTime))]
    public partial double? ComputeTimeMs { get; set; }
    public bool HasInferenceTime => ComputeTimeMs.HasValue || InferenceTimeMs > 0;
    public string InferenceTimeLabel => ComputeTimeMs is double ms ? $"추론 {ms:0.###} ms" : $"응답 {InferenceTimeMs} ms";
    [ObservableProperty] public partial bool IsRunningQuery { get; set; }
    [ObservableProperty] public partial bool IsQueryResultExpanded { get; set; } = true;
    [ObservableProperty] public partial QueryResultData? QueryResult { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPipelineRun)), NotifyPropertyChangedFor(nameof(PipelineOutput)),
     NotifyPropertyChangedFor(nameof(StageDetails)),
     NotifyPropertyChangedFor(nameof(IsSqlCandidate)), NotifyPropertyChangedFor(nameof(CanExecuteSql)),
     NotifyPropertyChangedFor(nameof(IsWaitingResponse)), NotifyPropertyChangedFor(nameof(HasGenerationWarning)),
     NotifyPropertyChangedFor(nameof(GenerationWarning))]
    public partial PipelineRun? PipelineRun { get; set; }

    public bool HasSql => !string.IsNullOrEmpty(Sql);
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    public bool HasPipelineRun => PipelineRun != null;
    public bool IsWaitingResponse => IsAssistant && !HasSql && !HasPipelineRun && StopReason == null && !HasError;
    public bool IsSqlCandidate => SqlCandidates.Count == 1 && SqlCandidates[0].HasSql;
    public bool CanExecuteSql => IsSqlCandidate && SqlCandidates[0].CanExecuteSql;
    public string? SqlValidationError => SqlCandidates.Count == 1 ? SqlCandidates[0].ValidationError : null;
    public bool HasSqlValidationError => !string.IsNullOrEmpty(SqlValidationError);
    public bool HasGenerationWarning => IsAssistant && !HasPipelineRun && StopReason is "max_new_tokens" or "context";
    public string GenerationWarning
    {
        get
        {
            if (!HasGenerationWarning) return string.Empty;
            string reason = StopReason == "max_new_tokens"
                ? "Max Tokens에 도달해 생성이 중단되었습니다."
                : "모델의 문맥 길이에 도달해 생성이 중단되었습니다.";
            return HasSql ? reason : $"{reason} 생성된 SQL이 없습니다.";
        }
    }
    public bool ShowPipelineOutput => HasPipelineRun && SqlCandidates.Count == 0;
    public string PipelineOutput => PipelineRun?.Steps.LastOrDefault()?.Output ?? string.Empty;
    public string StageDetails
    {
        get
        {
            if (PipelineRun is not { } run) return "";
            var sections = new List<string>();
            foreach (var step in run.Steps)
            {
                string status = step.Error.Length > 0 ? "실패" : step.StopReason.Length > 0 ? step.StopReason : "완료";
                string input = step.Stage == PipelineStage.Mapper
                    ? $"{step.Input}\n\n원문 질문\n{run.MapperSourceQuestion ?? run.Input}"
                    : step.Input;
                string timing = step.ComputeTimeMs is double ms ? $" · 추론 {ms:0.###} ms" : "";
                string section = $"{StageLabel(step.Stage)} · {status}{timing}\n입력\n{input}\n출력\n{step.Output}";
                if (step.Error.Length > 0) section += $"\n오류\n{step.Error}";
                sections.Add(section);
            }
            if (run.MappingCandidates.Count > 0)
                sections.Add($"복원 기록\n{run.SerializeMappings(indented: true)}");
            return string.Join("\n\n", sections);
        }
    }

    internal static string StageLabel(PipelineStage stage) => stage switch
    {
        PipelineStage.Front => "앞모델",
        PipelineStage.Mapper => "매퍼",
        PipelineStage.Back => "뒷모델",
        PipelineStage.Postprocessor => "후처리기",
        _ => stage.ToString()
    };
}
