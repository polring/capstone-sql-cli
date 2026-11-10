using SqlApp.Interop.SqlEngine;

namespace SqlApp.Services;

public sealed class InferenceService : IDisposable
{
    private readonly object _lock = new();
    private string? _modelPath;
    private SqlModel? _model;

    public Task<(string Sql, string StopReason)> RunQueryAsync(string path, string question, int maxTokens) =>
        Task.Run(() =>
        {
            lock (_lock)
            {
                if (string.IsNullOrWhiteSpace(path))
                    throw new InvalidOperationException("GGUF 모델을 선택하세요.");
                if (_model == null || _modelPath != path)
                {
                    var next = SqlModel.Open(path);
                    _model?.Dispose();
                    _model = next;
                    _modelPath = path;
                }
                return _model.Generate(question, maxTokens);
            }
        });

    public void Dispose()
    {
        lock (_lock)
        {
            _model?.Dispose();
            _model = null;
        }
    }
}
