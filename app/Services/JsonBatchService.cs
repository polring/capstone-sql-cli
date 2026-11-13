using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SqlApp.Services;

internal sealed record JsonBatchResult(string Path, int Total, int Processed, int Matched, int Succeeded, bool IsEvaluation)
{
    public bool IsCancelled => Processed < Total;
}

internal static class JsonBatchService
{
    internal static Task<JsonBatchResult> ProcessAsync(string path, Func<string, Task<IReadOnlyList<string>>> infer,
        Action<int, int>? onProgress = null) =>
        ProcessAsync(path, infer, CancellationToken.None, onProgress);

    internal static Task<JsonBatchResult> ProcessAsync(string path,
        Func<string, Task<IReadOnlyList<string>>> infer, CancellationToken cancellationToken,
        Action<int, int>? onProgress = null) =>
        ProcessAsync(path, [infer], cancellationToken, onProgress);

    internal static async Task<JsonBatchResult> ProcessAsync(string path,
        IReadOnlyList<Func<string, Task<IReadOnlyList<string>>>> inferWorkers, CancellationToken cancellationToken,
        Action<int, int>? onProgress = null)
    {
        if (inferWorkers.Count == 0)
            throw new ArgumentException("추론 작업자가 한 개 이상 필요합니다.", nameof(inferWorkers));
        string source = Path.GetFullPath(path);
        var (isArray, rows, isEvaluation) = await Task.Run(() => ReadDataset(source));
        int workerCount = Math.Min(inferWorkers.Count, rows.Count);
        var active = new List<Task<RowPrediction>>(workerCount);
        int nextRow = 0;
        int processed = 0;
        int matched = 0;
        int succeeded = 0;

        void StartNext(int worker)
        {
            if (nextRow >= rows.Count || cancellationToken.IsCancellationRequested) return;
            int index = nextRow++;
            string input = rows[index]["input"]!.GetValue<string>();
            active.Add(PredictRowAsync(index, worker, input, inferWorkers[worker]));
        }

        onProgress?.Invoke(0, rows.Count);
        try
        {
            for (int worker = 0; worker < workerCount; worker++)
                StartNext(worker);

            while (active.Count > 0)
            {
                var completed = await Task.WhenAny(active);
                active.Remove(completed);
                var prediction = await completed;
                var row = rows[prediction.RowIndex];
                var outputs = prediction.Outputs;
                if (outputs.Length > 0) succeeded++;
                if (isEvaluation)
                {
                    bool correct = outputs.Contains(row["output"]!.GetValue<string>(), StringComparer.Ordinal);
                    row["matched"] = correct;
                    if (correct) matched++;
                }
                else
                {
                    row["output"] = outputs.Length switch
                    {
                        0 => null,
                        1 => JsonValue.Create(outputs[0]),
                        _ => new JsonArray(outputs.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray())
                    };
                }
                processed++;
                onProgress?.Invoke(processed, rows.Count);
                StartNext(prediction.Worker);
            }
        }
        catch
        {
            // Finish other active calls before their owner disposes the model handles.
            Task draining = Task.WhenAll(active);
            await draining.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            throw;
        }

        string suffix = isEvaluation ? "_eval.json" : "_infer.json";
        string destination = Path.Combine(Path.GetDirectoryName(source)!, Path.GetFileNameWithoutExtension(source) + suffix);
        // Rows were assigned in input order and all started rows have finished.
        if (processed > 0) await Task.Run(() => SaveResult(destination, isArray, rows, processed));
        return new JsonBatchResult(processed > 0 ? destination : "", rows.Count, processed, matched, succeeded, isEvaluation);
    }

    private readonly record struct RowPrediction(int RowIndex, int Worker, string[] Outputs);

    private static async Task<RowPrediction> PredictRowAsync(int index, int worker, string input,
        Func<string, Task<IReadOnlyList<string>>> infer)
    {
        IReadOnlyList<string> predictions;
        try
        {
            predictions = await infer(input).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FormatException || ex.GetType() == typeof(InvalidOperationException) ||
                                   ex.GetType() == typeof(ArgumentException))
        {
            // The tokenizer and inference engine report unsupported inputs with these exceptions.
            predictions = [];
        }
        var outputs = predictions.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal).ToArray();
        return new RowPrediction(index, worker, outputs);
    }

    private static (bool IsArray, List<JsonObject> Rows, bool IsEvaluation) ReadDataset(string path)
    {
        using var stream = File.OpenRead(path);
        var root = JsonNode.Parse(stream, documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false });
        IEnumerable<JsonNode?> entries = root switch
        {
            JsonArray array => array,
            JsonObject row => [row],
            _ => throw new FormatException("JSON에는 입력 객체 한 개 또는 입력 객체 배열이 필요합니다.")
        };

        var rows = new List<JsonObject>();
        bool? isEvaluation = null;
        foreach (var entry in entries)
        {
            int number = rows.Count + 1;
            if (entry is not JsonObject row || row["input"] is not JsonValue input ||
                !input.TryGetValue<string>(out var question) || string.IsNullOrWhiteSpace(question))
                throw new FormatException($"{number}번째 항목에 비어 있지 않은 문자열 input이 필요합니다.");

            bool hasOutput = row.TryGetPropertyValue("output", out var output);
            if (hasOutput && (output is not JsonValue value || !value.TryGetValue<string>(out _)))
                throw new FormatException($"{number}번째 항목의 output은 정답 문자열이어야 합니다.");
            if (isEvaluation.HasValue && isEvaluation.Value != hasOutput)
                throw new FormatException($"{number}번째 항목의 형식이 다릅니다. 한 파일의 모든 항목에 output이 있거나 모두 없어야 합니다.");
            isEvaluation = hasOutput;
            rows.Add(row);
        }
        if (rows.Count == 0) throw new FormatException("JSON 배열에 처리할 입력이 없습니다.");
        return (root is JsonArray, rows, isEvaluation == true);
    }

    private static void SaveResult(string destination, bool isArray, IReadOnlyList<JsonObject> rows, int processed)
    {
        string temporary = Path.Combine(Path.GetDirectoryName(destination)!, Path.GetRandomFileName());
        var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try
        {
            using (stream)
            {
                using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
                {
                    Indented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
                if (isArray) writer.WriteStartArray();
                for (int i = 0; i < processed; i++) rows[i].WriteTo(writer);
                if (isArray) writer.WriteEndArray();
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
