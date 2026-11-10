namespace SqlApp.Models;

// Numeric values are persisted in existing sessions.json files.
public enum MessageSender { User = 0, Assistant = 1, QueryResult = 2 }

public sealed partial class ChatMessage : ObservableObject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public MessageSender Sender { get; set; }
    public string Text { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public Guid? ResultMessageId { get; set; }
    public bool IsUser => Sender == MessageSender.User;
    public bool IsAssistant => Sender == MessageSender.Assistant;
    public bool IsQueryResultSender => Sender == MessageSender.QueryResult;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSql)), NotifyPropertyChangedFor(nameof(IsWaitingResponse))]
    public partial string Sql { get; set; } = string.Empty;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsWaitingResponse))]
    public partial string? StopReason { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError)), NotifyPropertyChangedFor(nameof(IsWaitingResponse))]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty] public partial long InferenceTimeMs { get; set; }
    [ObservableProperty] public partial bool IsRunningQuery { get; set; }
    [ObservableProperty] public partial bool IsQueryResultExpanded { get; set; } = true;
    [ObservableProperty] public partial QueryResultData? QueryResult { get; set; }

    public bool HasSql => !string.IsNullOrEmpty(Sql);
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    public bool IsWaitingResponse => IsAssistant && !HasSql && StopReason == null && !HasError;
}
