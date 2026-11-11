namespace SqlApp.Models;

public sealed partial class ChatSqlCandidate : ObservableObject
{
    internal ChatSqlCandidate(ChatMessage message, string sql, string label, string restorationError = "")
    {
        Message = message;
        Sql = sql;
        Label = label;
        RestorationError = restorationError;
        if (restorationError.Length > 0) ValidationError = restorationError;
    }

    internal ChatMessage Message { get; }
    internal string RestorationError { get; }
    public string Sql { get; }
    public string Label { get; }
    public bool HasLabel => Label.Length > 0;
    public bool HasSql => Sql.Length > 0;

    // Null while preparing, empty when the SQL prepares on this message's database.
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsValidated)), NotifyPropertyChangedFor(nameof(HasValidationError)),
     NotifyPropertyChangedFor(nameof(IsChecking)), NotifyPropertyChangedFor(nameof(ValidationLabel)),
     NotifyPropertyChangedFor(nameof(CanExecuteSql))]
    public partial string? ValidationError { get; internal set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanExecuteSql))]
    public partial bool IsRunning { get; internal set; }

    public bool IsChecking => ValidationError == null;
    public bool IsValidated => ValidationError == string.Empty;
    public bool HasValidationError => !string.IsNullOrEmpty(ValidationError);
    public bool CanExecuteSql => IsValidated && HasSql && !IsRunning && Message.DbPath.Length > 0;
    public string ValidationLabel => IsChecking ? "SQL 검사 중" : IsValidated ? "실행 가능" : $"실행 불가 · {ValidationError}";
}
