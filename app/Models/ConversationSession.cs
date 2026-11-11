using System.Collections.ObjectModel;
using SqlApp.Services;

namespace SqlApp.Models;

public sealed partial class ConversationSession : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public AssetBundle? Bundle { get; set; }
    public string BundleDirectory { get; set; } = string.Empty;
    public bool IsDualMode { get; set; }
    public StageRange SelectedRange
    {
        get;
        set => field = value.SnapForSelection();
    } = new(PipelineStage.Front, PipelineStage.Postprocessor);

    [ObservableProperty] public partial string Title { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsEditingTitle { get; set; }
    [ObservableProperty] public partial int MaxTokens { get; set; } = 64;

    public ObservableCollection<ChatMessage> Messages { get; } = new();
    public bool HasMessages => Messages.Count != 0;

    public ConversationSession()
    {
        Messages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasMessages));
    }
}
