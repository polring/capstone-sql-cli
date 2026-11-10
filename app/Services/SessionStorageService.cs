using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
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
        public string ModelPath { get; set; } = string.Empty;
        public string DbPath { get; set; } = string.Empty;
        public int MaxTokens { get; set; } = 64;
        public List<ChatMessageDto> Messages { get; set; } = new();
    }

    public class ChatMessageDto
    {
        public Guid Id { get; set; }
        public MessageSender Sender { get; set; }
        public string Text { get; set; } = string.Empty;
        public string Sql { get; set; } = string.Empty;
        public string? StopReason { get; set; }
        public long InferenceTimeMs { get; set; }
        public DateTime Timestamp { get; set; }
        public string? ErrorMessage { get; set; }
        public QueryResultData? QueryResult { get; set; }
        public Guid? ResultMessageId { get; set; }
    }

    public class SessionStorageService
    {
        private readonly string _storagePath;
        private readonly System.Threading.SemaphoreSlim _writeLock = new(1);

        public SessionStorageService()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string dir = Path.Combine(appData, "capstone_sql_gui");
            Directory.CreateDirectory(dir);
            _storagePath = Path.Combine(dir, "sessions.json");
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
                            Timestamp = m.Timestamp,
                            ErrorMessage = m.ErrorMessage,
                            QueryResult = m.QueryResult,
                            ResultMessageId = m.ResultMessageId
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
            catch { }
        }

        private static AssetBundle? RestoreBundle(SessionStorageDto dto, IList<AssetBundle> bundles, IReadOnlyList<AssetBundle> defaults)
        {
            var comparison = AssetDiscoveryService.PathComparison;
            var exact = bundles.FirstOrDefault(b => b.ModelPath.Equals(dto.ModelPath, comparison)
                                                 && b.DbPath.Equals(dto.DbPath, comparison));
            if (exact != null) return exact;

            string? folder = Path.GetDirectoryName(dto.ModelPath);
            if (folder != null && Path.IsPathFullyQualified(folder)
                && folder.Equals(Path.GetDirectoryName(dto.DbPath), comparison))
            {
                // Resolve the saved directory before falling back to a built-in folder with the same name.
                try
                {
                    var restored = AssetDiscoveryService.AddFolder(bundles, folder);
                    if (restored != null) return restored;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return defaults.FirstOrDefault(b => b.Name.Equals(dto.AssetBundleName, comparison)) ?? defaults.FirstOrDefault();
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
                foreach (var dto in dtos)
                {
                    var s = new ConversationSession
                    {
                        Id = dto.Id,
                        Title = dto.Title,
                        CreatedAt = dto.CreatedAt,
                        UpdatedAt = dto.UpdatedAt,
                        Bundle = RestoreBundle(dto, bundles, defaults),
                        MaxTokens = dto.MaxTokens,
                    };

                    foreach (var m in dto.Messages)
                    {
                        s.Messages.Add(new ChatMessage
                        {
                            Id = m.Id,
                            Sender = m.Sender,
                            Text = m.Text,
                            Sql = m.Sql,
                            StopReason = m.StopReason,
                            InferenceTimeMs = m.InferenceTimeMs,
                            Timestamp = m.Timestamp,
                            ErrorMessage = m.ErrorMessage,
                            QueryResult = m.QueryResult,
                            ResultMessageId = m.ResultMessageId
                        });
                    }
                    list.Add(s);
                }
            }
            catch { }

            return list;
        }
    }
}
