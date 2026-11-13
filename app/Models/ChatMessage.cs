using SqlApp.Services.Pipeline;

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
    public bool HasAssistantText => IsAssistant && !string.IsNullOrEmpty(Text);
    public bool IsQueryResultSender => Sender == MessageSender.QueryResult;
    public string QueryResultTitle => Text.Length == 0 ? "DB 쿼리 실행 결과" : $"{Text} · DB 쿼리 실행 결과";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSql)), NotifyPropertyChangedFor(nameof(IsWaitingResponse)),
     NotifyPropertyChangedFor(nameof(IsSqlCandidate)), NotifyPropertyChangedFor(nameof(CanExecuteSql)),
     NotifyPropertyChangedFor(nameof(GenerationWarning)), NotifyPropertyChangedFor(nameof(PipelineOutput))]
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
        if (!IsFullPipelineRun || PipelineRun.Error.Length > 0) return [];
        var restored = PipelineRun.FinalSqlCandidates;
        return restored.Select((candidate, index) => new ChatSqlCandidate(this, candidate.Sql,
            CandidateLabel(candidate, index, restored.Count), candidate.Error)).ToArray();
    }

    private static string CandidateLabel(RestoredSqlCandidate candidate, int index, int count) =>
        count == 1 && candidate.MappingIndices.Count <= 1 ? "" : "후보 " + FormatMappingNumbers(candidate.MappingIndices, index);

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
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPipelineRun)), NotifyPropertyChangedFor(nameof(IsFullPipelineRun)),
     NotifyPropertyChangedFor(nameof(PipelineOutput)),
     NotifyPropertyChangedFor(nameof(StageDetails)), NotifyPropertyChangedFor(nameof(ShowStageDetails)),
     NotifyPropertyChangedFor(nameof(HasMapperError)), NotifyPropertyChangedFor(nameof(MapperErrorDetails)),
     NotifyPropertyChangedFor(nameof(IsSqlCandidate)), NotifyPropertyChangedFor(nameof(CanExecuteSql)),
     NotifyPropertyChangedFor(nameof(IsWaitingResponse)), NotifyPropertyChangedFor(nameof(HasGenerationWarning)),
     NotifyPropertyChangedFor(nameof(GenerationWarning))]
    public partial PipelineRun? PipelineRun { get; set; }

    public bool HasSql => !string.IsNullOrEmpty(Sql);
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    public bool HasPipelineRun => PipelineRun != null;
    public bool IsFullPipelineRun => PipelineRun?.Range is { Start: PipelineStage.Front, End: PipelineStage.Postprocessor };
    private PipelineStepResult? FailedMapperStep => PipelineRun?.Steps.LastOrDefault() is
        { Stage: PipelineStage.Mapper, Error.Length: > 0 } step ? step : null;
    public bool HasMapperError => FailedMapperStep != null;
    public bool ShowStageDetails => PipelineRun is { Steps.Count: > 0 } or { MappingCandidates.Count: > 0 };
    public string MapperErrorDetails
    {
        get
        {
            if (FailedMapperStep is not { } step || PipelineRun is not { } run) return string.Empty;
            return $"원문 질문\n{run.MapperSourceQuestion ?? run.Input}\n\n매퍼 입력\n{step.Input}\n\n오류 원인\n{step.Error}";
        }
    }
    public bool IsWaitingResponse => IsAssistant && !HasAssistantText && !HasSql && !HasPipelineRun && StopReason == null && !HasError;
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
    public bool ShowPipelineOutput => HasPipelineRun && !HasMapperError && SqlCandidates.Count == 0;
    public string PipelineOutput
    {
        get
        {
            if (PipelineRun is not { } run) return string.Empty;
            // Partial postprocessor runs in saved chats can contain several restored queries.
            if (!IsFullPipelineRun && run.Range.End == PipelineStage.Postprocessor && run.FinalSqlCandidates.Count > 0)
                return string.Join("\n\n", run.FinalSqlCandidates.Select(c => c.Error.Length > 0 ? c.Error : c.Sql));
            return run.Steps.LastOrDefault()?.Output ?? (run.Range.End switch
            {
                PipelineStage.Front => run.FrontOutput,
                PipelineStage.Mapper => run.GeneralizedQuestion,
                PipelineStage.Back => run.TaggedSql,
                PipelineStage.Postprocessor => Sql,
                _ => string.Empty
            });
        }
    }
    public string StageDetails
    {
        get
        {
            if (PipelineRun is not { } run) return "";
            var sections = new List<string>();
            foreach (var step in run.Steps)
            {
                string status = step.Error.Length > 0 ? "실패" : step.StopReason.Length > 0 ? step.StopReason : "완료";
                string timing = step.ComputeTimeMs is double ms ? $" · 추론 {ms:0.###} ms" : "";
                string section = $"[{StageLabel(step.Stage)} · {status}{timing}]\n입력\n";
                if (step.Stage == PipelineStage.Mapper)
                {
                    section += $"{run.MapperSourceQuestion ?? run.Input}\n{step.Input}";
                    string mappings = Mapper.FormatMappings(run, step);
                    if (mappings.Length > 0) section += $"\n매핑\n{mappings}";
                }
                else section += step.Input;

                string output = step.Stage == PipelineStage.Postprocessor && run.FinalSqlCandidates.Count > 0
                    ? FormatRestoredOutputs(run.FinalSqlCandidates)
                    : step.Output;
                section += $"\n출력\n{output}";
                if (step.Error.Length > 0) section += $"\n오류\n{step.Error}";
                sections.Add(section);
            }
            if (run.MappingCandidates.Count > 0 && !run.Steps.Any(s => s.Stage == PipelineStage.Mapper))
                sections.Add($"매핑\n{Mapper.FormatMappings(run, null)}");
            return string.Join("\n\n", sections);
        }
    }

    private static string FormatRestoredOutputs(IReadOnlyList<RestoredSqlCandidate> candidates)
    {
        return string.Join("\n\n", candidates.Select((candidate, index) =>
        {
            string output = candidate.Error.Length > 0 ? $"오류\n{candidate.Error}" : candidate.Sql;
            string label = CandidateLabel(candidate, index, candidates.Count);
            return label.Length == 0 ? output : $"{label}\n{output}";
        }));
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
