namespace SqlEngine.Core
{
    public sealed record SqlResult(
        string Sql,
        string? RawSql,
        string? StopReason
    );
}
