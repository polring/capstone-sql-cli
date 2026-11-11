using System.Text.Json;
using System.Text.Json.Serialization;
using SqlApp.Models;

namespace SqlApp.Services.Pipeline;

public static class PipelineRunner
{
    private static readonly JsonSerializerOptions RecordJson = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static async Task<PipelineRun> RunAsync(StageRange range, string input, string auxiliaryInput,
        string bundleDirectory, string dbPath, IReadOnlyList<TableSchemaInfo> schema,
        Func<PipelineStage, string, Task<InferenceResult>> infer, string conversationId, Guid messageId,
        Func<Guid, PipelineRun?>? findSource = null)
    {
        var run = new PipelineRun
        {
            Range = range, Input = input,
            MapperSourceQuestion = range.Start == PipelineStage.Mapper ? auxiliaryInput : null,
            BundleDirectory = bundleDirectory, DbPath = dbPath
        };
        PipelineStepResult? step = null;
        try
        {
            if (!range.IsValid) throw new FormatException("실행 구간을 확인하세요.");
            if (string.IsNullOrWhiteSpace(conversationId) || messageId == Guid.Empty)
                throw new FormatException("대화 ID와 메시지 ID가 필요합니다.");
            if (string.IsNullOrWhiteSpace(input)) throw new FormatException("시작 단계에 넣을 입력이 필요합니다.");
            if ((range.Contains(PipelineStage.Mapper) || range.Contains(PipelineStage.Postprocessor)) && schema.Count == 0)
                throw new FormatException("매퍼와 후처리기를 실행하려면 해당 버전 DB의 스키마가 필요합니다.");
            if (range.Start == PipelineStage.Mapper && string.IsNullOrWhiteSpace(auxiliaryInput))
                throw new FormatException("매퍼의 보조 입력에 앞모델이 태깅하기 전의 원문 질문을 넣으세요.");

            if (range.Start == PipelineStage.Back) run.GeneralizedQuestion = input;
            if (range.Start == PipelineStage.Postprocessor) run.TaggedSql = input;
            if (range.Start >= PipelineStage.Back && range.End == PipelineStage.Postprocessor)
            {
                if (string.IsNullOrWhiteSpace(auxiliaryInput))
                    throw new FormatException("후처리에 사용할 같은 질문의 복원 기록 JSON이 필요합니다.");
                var imported = await Task.Run(() => ReadMappings(auxiliaryInput));
                if (imported.ConversationId != conversationId)
                    throw new FormatException("현재 대화에서 만든 복원 기록만 사용할 수 있습니다.");
                var source = findSource?.Invoke(imported.MessageId);
                if (source?.Mappings is not { Candidates.Count: > 0 } original || !source.Range.Contains(PipelineStage.Mapper) ||
                    original.ConversationId != conversationId || original.MessageId != imported.MessageId)
                    throw new FormatException("이 대화에서 복원 기록을 만든 매퍼 메시지를 찾을 수 없습니다.");
                if (!SamePath(bundleDirectory, source.BundleDirectory) || !SamePath(dbPath, source.DbPath))
                    throw new FormatException("매퍼를 실행한 에셋 폴더 또는 DB가 현재 선택과 다릅니다.");
                if (range.Start == PipelineStage.Back && source.GeneralizedQuestion != input)
                    throw new FormatException("복원 기록의 일반화 질문이 입력과 다릅니다.");
                await Task.Run(() => ValidateCandidates(imported.Candidates, original.Candidates, schema)).ConfigureAwait(false);
                run.Mappings = original;
            }

            for (PipelineStage stage = range.Start; stage <= range.End; stage++)
            {
                step = new PipelineStepResult { Stage = stage };
                run.Steps.Add(step);
                switch (stage)
                {
                    case PipelineStage.Front:
                    case PipelineStage.Back:
                        step.Input = stage == PipelineStage.Front ? input : run.GeneralizedQuestion;
                        var result = await infer(stage, step.Input).ConfigureAwait(false);
                        step.Output = result.Text;
                        step.StopReason = result.StopReason;
                        step.ComputeTimeMs = result.ComputeTimeMs;
                        if (!string.Equals(step.StopReason, "eos", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException($"모델 생성이 완료되지 않았습니다 ({step.StopReason}).");
                        if (string.IsNullOrWhiteSpace(step.Output)) throw new FormatException("모델 출력이 비어 있습니다.");
                        if (stage == PipelineStage.Front)
                        {
                            if (!Mapper.IsTaggedSentence(step.Output))
                                throw new FormatException("앞모델 출력은 DB 태그 문장이어야 합니다.");
                            run.FrontOutput = step.Output;
                        }
                        else run.TaggedSql = step.Output;
                        break;
                    case PipelineStage.Mapper:
                        step.Input = range.Start == PipelineStage.Mapper ? input : run.FrontOutput;
                        var mapped = await Task.Run(() => Mapper.MapCandidates(run.MapperSourceQuestion ?? input, step.Input, schema))
                            .ConfigureAwait(false);
                        run.Mappings = new MappingDocument
                        {
                            ConversationId = conversationId, MessageId = messageId, Candidates = mapped.Candidates
                        };
                        run.GeneralizedQuestion = mapped.GeneralizedQuestion;
                        step.Output = run.GeneralizedQuestion;
                        step.StopReason = "완료";
                        break;
                    case PipelineStage.Postprocessor:
                        step.Input = run.TaggedSql;
                        run.FinalSqlCandidates = await Task.Run(() =>
                            Postprocessor.RestoreCandidates(run.TaggedSql, run.MappingCandidates, schema))
                            .ConfigureAwait(false);
                        int failed = run.FinalSqlCandidates.Count(candidate => candidate.Error.Length > 0);
                        step.Output = run.FinalSql.Length > 0 ? run.FinalSql
                            : $"복원된 SQL {run.FinalSqlCandidates.Count - failed}개 · 복원 실패 {failed}개";
                        step.StopReason = failed switch
                        {
                            0 => "완료",
                            _ when failed == run.FinalSqlCandidates.Count => "복원 실패",
                            _ => "일부 복원 실패"
                        };
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException)
        {
            run.Error = ex.Message;
            if (step != null) step.Error = ex.Message;
        }
        return run;
    }

    private static MappingDocument ReadMappings(string json)
    {
        MappingDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<MappingDocument>(json, RecordJson);
        }
        catch (JsonException ex)
        {
            throw new FormatException("복원 기록 JSON의 대화 ID, 메시지 ID와 매핑 후보 형식을 확인하세요.", ex);
        }
        if (document?.Candidates is not { Count: > 0 } || document.MessageId == Guid.Empty ||
            string.IsNullOrWhiteSpace(document.ConversationId) || document.Candidates.Any(record => record == null))
            throw new FormatException("복원 기록에는 대화 ID, 메시지 ID, 한 개 이상의 매핑 후보가 필요합니다.");
        return document;
    }

    private static void ValidateCandidates(List<MappingRecord> imported, List<MappingRecord> original,
                                           IReadOnlyList<TableSchemaInfo> schema)
    {
        var first = original[0];
        var tagOrder = first.Values.Keys.ToArray();
        var importedValues = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in imported)
        {
            SchemaTags.ValidateRecord(record, schema);
            if (!SameBindings(record.Tables, first.Tables) || !SameBindings(record.Columns, first.Columns) ||
                record.Values.Count != first.Values.Count || !record.Values.Keys.All(first.Values.ContainsKey))
                throw new FormatException("복원 후보의 태그가 원본 매퍼 메시지와 다릅니다.");
            importedValues.Add(SchemaTags.ValueTupleKey(tagOrder, record.Values));
        }
        if (!importedValues.SetEquals(original.Select(record => SchemaTags.ValueTupleKey(tagOrder, record.Values))))
            throw new FormatException("복원 후보의 값 조합이 원본 매퍼 메시지와 다릅니다.");
    }

    private static bool SameBindings<T>(Dictionary<string, T> left, Dictionary<string, T> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) &&
            EqualityComparer<T>.Default.Equals(pair.Value, value));

    private static bool SamePath(string first, string second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)) return false;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
                             Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), comparison);
    }
}
