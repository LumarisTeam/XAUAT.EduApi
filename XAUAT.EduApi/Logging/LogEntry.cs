namespace XAUAT.EduApi.Logging;

public sealed record LogEntry(
    DateTimeOffset Timestamp,
    string Level,
    string Message,
    string? Source,
    string? Exception);
