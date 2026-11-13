using System.Collections.ObjectModel;
using SqlApp.Services;

namespace SqlApp.Models;

public sealed partial class ConversationSession : ObservableObject
{
    public const int DefaultMaxTokens = 200;
    public static int MaxJsonBatchWorkerCount => Environment.ProcessorCount;
    public static int DefaultJsonBatchWorkerCount => Math.Min(4, MaxJsonBatchWorkerCount);

    public string Id { get; set; } = Guid.NewGuid().ToString();
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public AssetBundle? Bundle { get; set; }
    public string BundleDirectory { get; set; } = string.Empty;
    public bool IsDualMode { get; set; }
    internal string? PendingJsonFilePath { get; set; }
    public StageRange SelectedRange
    {
        get;
        set => field = value.SnapForSelection();
    } = new(PipelineStage.Front, PipelineStage.Postprocessor);

    [ObservableProperty] public partial string Title { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsEditingTitle { get; set; }
    [ObservableProperty] public partial int MaxTokens { get; set; } = DefaultMaxTokens;
    public int JsonBatchWorkerCount
    {
        get;
        set => SetProperty(ref field, Math.Clamp(value, 1, MaxJsonBatchWorkerCount));
    } = DefaultJsonBatchWorkerCount;

    public ObservableCollection<ChatMessage> Messages { get; } = new();
    public bool HasMessages => Messages.Count != 0;

    public ConversationSession()
    {
        Messages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasMessages));
    }
}
