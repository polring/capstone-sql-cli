using System.Text.Json.Serialization;

namespace SqlApp.Models;

public sealed class QueryResultData
{
    public List<string> Columns { get; set; } = new();
    public List<List<string>> Rows { get; set; } = new();
    public long ExecutionTimeMs { get; set; }
    public string? ErrorMessage { get; set; }
    [JsonIgnore] public int RowCount => Rows.Count;
    [JsonIgnore] public bool HasRows => Rows.Count != 0;
    [JsonIgnore] public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
}
