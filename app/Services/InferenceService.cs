using System.Text;
using SqlApp.Interop.SqlEngine;
using SqlApp.Models;

namespace SqlApp.Services;

public sealed class InferenceService : IDisposable
{
    private readonly object _lock = new();
    private sealed record LoadedModel(string Path, DateTime Modified, long Length, BpeCodec Tokenizer, SqlModel Handle)
    {
        public bool Matches(FileInfo file) =>
            Path == file.FullName && Modified == file.LastWriteTimeUtc && Length == file.Length;
    }
    private readonly Dictionary<ModelRole, LoadedModel> _models = new();
    private bool _disposed;

    public Task<InferenceResult> RunQueryAsync(string path, string question, int maxTokens)
        => GenerateAsync(ModelRole.Single, path, question, maxTokens);

    private static ModelRole GetModelRole(PipelineStage stage) => stage switch
    {
        PipelineStage.Front => ModelRole.Front,
        PipelineStage.Back => ModelRole.Back,
        _ => throw new ArgumentOutOfRangeException(nameof(stage))
    };

    public string? GetStageUnavailableReason(PipelineStage stage, string modelPath)
    {
        var role = GetModelRole(stage);
        string name = role == ModelRole.Front ? "앞모델" : "뒷모델";
        if (!File.Exists(modelPath)) return $"{name} GGUF 파일이 필요합니다.";
        try
        {
            var file = new FileInfo(modelPath);
            lock (_lock)
            {
                if (_models.TryGetValue(role, out var loaded) && loaded.Matches(file)) return null;
            }
            GgufContract.ReadTokenizer(modelPath, role);
            return null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OverflowException or UnauthorizedAccessException)
        {
            return $"{name} GGUF 확인 실패: {ex.Message}";
        }
    }

    public Task<InferenceResult> RunStageAsync(PipelineStage stage, string modelPath, string input, int maxTokens)
    {
        var reason = GetStageUnavailableReason(stage, modelPath);
        if (reason != null) throw new InvalidOperationException(reason);
        return GenerateAsync(GetModelRole(stage), modelPath, input, maxTokens);
    }

    private Task<InferenceResult> GenerateAsync(ModelRole role, string path, string input, int maxTokens) =>
        Task.Run(() =>
        {
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (string.IsNullOrWhiteSpace(path))
                    throw new InvalidOperationException("GGUF 모델을 선택하세요.");
                var file = new FileInfo(path);
                if (!file.Exists) throw new FileNotFoundException("GGUF 모델을 찾을 수 없습니다.", path);
                if (!_models.TryGetValue(role, out var current) || !current.Matches(file))
                {
                    var tokenizer = GgufContract.ReadTokenizer(file.FullName, role);
                    var next = new LoadedModel(file.FullName, file.LastWriteTimeUtc, file.Length,
                        tokenizer, SqlModel.Open(file.FullName));
                    // The tokenizer and weights are read separately. Do not cache a pair
                    // from different revisions if the bundle is replaced during loading.
                    file.Refresh();
                    if (!file.Exists || file.LastWriteTimeUtc != next.Modified || file.Length != next.Length)
                    {
                        next.Handle.Dispose();
                        throw new IOException("읽는 동안 GGUF 파일이 변경되었습니다. 다시 실행하세요.");
                    }
                    current?.Handle.Dispose();
                    _models[role] = current = next;
                }
                var result = TokenGeneration.Generate(current.Handle, current.Tokenizer, input, maxTokens);
                return new InferenceResult(Encoding.UTF8.GetString(result.Bytes), result.StopReason, result.ComputeTimeMs);
            }
        });

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            foreach (var model in _models.Values) model.Handle.Dispose();
            _models.Clear();
        }
    }
}
