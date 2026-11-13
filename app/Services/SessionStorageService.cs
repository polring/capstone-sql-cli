using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using SqlApp.Models;

namespace SqlApp.Services
{
    public class SessionStorageDto
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string? AssetBundleName { get; set; }
        public string BundleDirectory { get; set; } = string.Empty;
        public bool IsDualMode { get; set; }
        public StageRange? SelectedRange { get; set; }
        public string ModelPath { get; set; } = string.Empty;
        public string DbPath { get; set; } = string.Empty;
        public int MaxTokens { get; set; } = ConversationSession.DefaultMaxTokens;
        public List<ChatMessageDto> Messages { get; set; } = new();
    }

    public class ChatMessageDto
    {
        public Guid Id { get; set; }
        public MessageSender Sender { get; set; }
        public string Text { get; set; } = string.Empty;
        public string Sql { get; set; } = string.Empty;
        public string? StopReason { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public long InferenceTimeMs { get; set; }
        public double? ComputeTimeMs { get; set; }
        public DateTime Timestamp { get; set; }
        public string? ErrorMessage { get; set; }
        public QueryResultData? QueryResult { get; set; }
        public Guid? ResultMessageId { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Guid? SourceMessageId { get; set; }
        public string? AssetDirectory { get; set; }
        public string? DbPath { get; set; }
        public PipelineRun? PipelineRun { get; set; }
    }

    public class SessionStorageService
    {
        private readonly string _storagePath;
        private readonly System.Threading.SemaphoreSlim _writeLock = new(1);

        public SessionStorageService() : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "capstone_sql_gui"))
        {
        }

        internal SessionStorageService(string directory)
        {
            Directory.CreateDirectory(directory);
            _storagePath = Path.Combine(directory, "sessions.json");
        }

        public async Task SaveSessionsAsync(IEnumerable<ConversationSession> sessions)
        {
            try
            {
                var dtos = new List<SessionStorageDto>();
                foreach (var s in sessions)
                {
                    var dto = new SessionStorageDto
                    {
                        Id = s.Id,
                        Title = s.Title,
                        CreatedAt = s.CreatedAt,
                        UpdatedAt = s.UpdatedAt,
                        AssetBundleName = s.Bundle?.Name,
                        BundleDirectory = s.Bundle?.DirectoryPath ?? s.BundleDirectory,
                        IsDualMode = s.IsDualMode,
                        SelectedRange = s.SelectedRange,
                        ModelPath = s.Bundle?.ModelPath ?? string.Empty,
                        DbPath = s.Bundle?.DbPath ?? string.Empty,
                        MaxTokens = s.MaxTokens,
                    };

                    foreach (var m in s.Messages)
                    {
                        dto.Messages.Add(new ChatMessageDto
                        {
                            Id = m.Id,
                            Sender = m.Sender,
                            Text = m.Text,
                            Sql = m.Sql,
                            StopReason = m.StopReason,
                            InferenceTimeMs = m.InferenceTimeMs,
                            ComputeTimeMs = m.ComputeTimeMs,
                            Timestamp = m.Timestamp,
                            ErrorMessage = m.ErrorMessage,
                            QueryResult = m.QueryResult,
                            ResultMessageId = m.ResultMessageId,
                            SourceMessageId = m.SourceMessageId,
                            AssetDirectory = m.AssetDirectory,
                            DbPath = m.DbPath,
                            PipelineRun = m.PipelineRun
                        });
                    }
                    dtos.Add(dto);
                }

                string json = JsonSerializer.Serialize(dtos, new JsonSerializerOptions { WriteIndented = true });
                await _writeLock.WaitAsync();
                try
                {
                    string temp = _storagePath + ".tmp";
                    await File.WriteAllTextAsync(temp, json);
                    File.Move(temp, _storagePath, overwrite: true);
                }
                finally
                {
                    _writeLock.Release();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine($"Could not save session history: {ex.Message}");
            }
        }

        private static AssetBundle? RestoreBundle(SessionStorageDto dto, IList<AssetBundle> bundles, IReadOnlyList<AssetBundle> defaults)
        {
            var comparison = AssetDiscoveryService.PathComparison;
            var folder = dto.BundleDirectory;
            if (string.IsNullOrEmpty(folder) && !string.IsNullOrEmpty(dto.ModelPath))
                folder = Path.GetDirectoryName(dto.ModelPath) ?? "";
            if (!string.IsNullOrEmpty(folder))
            {
                var exact = bundles.FirstOrDefault(b => b.IsDual == dto.IsDualMode && b.DirectoryPath.Equals(folder, comparison));
                if (exact != null) return exact;
                if (!Path.IsPathFullyQualified(folder)) return null;
                try
                {
                    return AssetDiscoveryService.AddFolder(bundles, folder, dto.IsDualMode);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                return null;
            }
            return defaults.FirstOrDefault(b => b.IsDual == dto.IsDualMode && b.Name.Equals(dto.AssetBundleName, comparison))
                ?? AssetDiscoveryService.GetDefaultBundle(defaults, dto.IsDualMode);
        }

        public async Task<List<ConversationSession>> LoadSessionsAsync(IList<AssetBundle> bundles)
        {
            var list = new List<ConversationSession>();
            if (!File.Exists(_storagePath)) return list;

            try
            {
                string json = await File.ReadAllTextAsync(_storagePath);
                var dtos = JsonSerializer.Deserialize<List<SessionStorageDto>>(json);
                if (dtos == null) return list;

                var defaults = bundles.ToArray();
                var validator = new SqlExecutionService();
                foreach (var dto in dtos)
                {
                    var s = new ConversationSession
                    {
                        Id = dto.Id,
                        Title = dto.Title,
                        CreatedAt = dto.CreatedAt,
                        UpdatedAt = dto.UpdatedAt,
                        Bundle = RestoreBundle(dto, bundles, defaults),
                        BundleDirectory = dto.BundleDirectory.Length > 0 ? dto.BundleDirectory : Path.GetDirectoryName(dto.ModelPath) ?? "",
                        IsDualMode = dto.IsDualMode,
                        SelectedRange = dto.SelectedRange ?? new StageRange(PipelineStage.Front, PipelineStage.Postprocessor),
                        MaxTokens = dto.MaxTokens,
                    };

                    foreach (var m in dto.Messages)
                    {
                        var message = new ChatMessage
                        {
                            Id = m.Id,
                            Sender = m.Sender,
                            Text = m.Text,
                            Sql = m.Sql,
                            StopReason = m.StopReason,
                            InferenceTimeMs = m.InferenceTimeMs,
                            ComputeTimeMs = m.ComputeTimeMs,
                            Timestamp = m.Timestamp,
                            ErrorMessage = m.ErrorMessage,
                            QueryResult = m.QueryResult,
                            ResultMessageId = m.ResultMessageId,
                            SourceMessageId = m.SourceMessageId,
                            AssetDirectory = m.AssetDirectory ?? m.PipelineRun?.BundleDirectory ?? s.BundleDirectory,
                            DbPath = m.DbPath ?? m.PipelineRun?.DbPath ?? dto.DbPath,
                            PipelineRun = m.PipelineRun
                        };
                        await validator.ValidateMessageAsync(message);
                        s.Messages.Add(message);
                    }
                    list.Add(s);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                System.Diagnostics.Debug.WriteLine($"Could not load session history: {ex.Message}");
            }

            return list;
        }
    }
}
