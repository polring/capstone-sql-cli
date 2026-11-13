using System.Text.Json.Serialization;

namespace SqlApp.Models;

public sealed record MappedColumn(string Name, string Type);

public sealed class MappingDocument
{
    public required string ConversationId { get; init; }
    public required Guid MessageId { get; init; }
    public required List<MappingRecord> Candidates { get; init; }
}

public sealed class MappingRecord
{
    [JsonRequired]
    public Dictionary<string, string> Tables { get; set; } = new();
    [JsonRequired]
    public Dictionary<string, MappedColumn> Columns { get; set; } = new();
    [JsonRequired]
    public Dictionary<string, string> Values { get; set; } = new();
}
