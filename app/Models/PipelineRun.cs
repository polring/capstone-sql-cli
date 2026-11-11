using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlApp.Models;

public sealed class PipelineStepResult
{
    public PipelineStage Stage { get; set; }
    public string Input { get; set; } = "";
    public string Output { get; set; } = "";
    public string StopReason { get; set; } = "";
    public double? ComputeTimeMs { get; set; }
    public string Error { get; set; } = "";
}

public sealed class RestoredSqlCandidate
{
    public string Sql { get; init; } = "";
    public string Error { get; init; } = "";
    // Indices into this run's MappingCandidates. Several mappings can yield the same SQL.
    public List<int> MappingIndices { get; init; } = [];
}

public sealed class PipelineRun
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    public string BundleDirectory { get; set; } = "";
    public string DbPath { get; set; } = "";
    public StageRange Range { get; set; }
    public string Input { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MapperSourceQuestion { get; set; }
    public string FrontOutput { get; set; } = "";
    public string GeneralizedQuestion { get; set; } = "";
    public MappingDocument? Mappings { get; set; }
    public string TaggedSql { get; set; } = "";
    public List<RestoredSqlCandidate> FinalSqlCandidates { get; set; } = [];

    [JsonIgnore]
    public IReadOnlyList<MappingRecord> MappingCandidates => Mappings is { } document ? document.Candidates : [];

    [JsonIgnore]
    public MappingRecord? Mapping => MappingCandidates.Count == 1 ? MappingCandidates[0] : null;

    [JsonInclude, JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    public string FinalSql
    {
        get => FinalSqlCandidates.Count == 1 ? FinalSqlCandidates[0].Sql : "";
        private set
        {
            if (!string.IsNullOrEmpty(value) && FinalSqlCandidates.Count == 0)
                FinalSqlCandidates.Add(new RestoredSqlCandidate { Sql = value, MappingIndices = [0] });
        }
    }

    public string SerializeMappings(bool indented = false)
    {
        return Mappings == null ? "" : JsonSerializer.Serialize(Mappings, indented ? IndentedJson : null);
    }

    public List<PipelineStepResult> Steps { get; set; } = new();
    [JsonIgnore]
    public double? ComputeTimeMs => Steps.Any(step => step.ComputeTimeMs.HasValue)
        ? Steps.Sum(step => step.ComputeTimeMs) : null;
    public string Error { get; set; } = "";
}
